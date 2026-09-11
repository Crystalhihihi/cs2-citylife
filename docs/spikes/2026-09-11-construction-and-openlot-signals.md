# Spike 报告：施工中建筑与开放空间（操场/堆场/停车场）的只读判别信号

> 日期：2026-09-11 ｜ 对象：Game.dll（只读反编译，ilspycmd 8.2.0.7535，`DOTNET_ROLL_FORWARD=LatestMajor`）｜ 未启动游戏
> Game.dll 版本：`Cities2_Data\Managed\Game.dll`，12,055,552 字节，文件时间 2026-06-29；Steam buildid **23700737**（同 address spike）
> 反编译产物存档：`logs/construction-openlot-spike-20260911/`（28 个定向类型 dump，下文逐处引用）
> 结论先行：
> ① **施工中**：`Game.Objects.UnderConstruction` 组件有无即答案——游戏 UI 自己就用它（`InfoSectionBase.UnderConstruction`），完工由 `BuildingConstructionSystem` 移除。施工楼能过白名单是因为**租户签约不避施工楼**（三个 FindPropertySystem 的查询都不排 UnderConstruction），"有租户"≠"已建成"。
> ② **开放空间**：学校操场/体育场走**服务升级机制**——实体侧 `Game.Buildings.ServiceUpgrade`（空标记）与 `Game.Buildings.Extension` 两个组件把两条升级路径全堵死；停车场有专属组件 `ParkingFacility`/`CarParkingFacility`；露台仓库（开放仓储堆场）首选**租户侧** `Game.Companies.StorageCompany` 判别，通用解候选 prefab 侧 `BuildingData.m_Flags & HasInsideRoom`（语义正中靶心但位值分布离线拿不到，需实机标定后启用）。

---

## 1. 施工中判别信号

### 1.1 首选：`Game.Objects.UnderConstruction` 组件（IComponentData，挂建筑实体本体）

组件定义（UnderConstruction.cs:7-12，全字段 public）：

```csharp
[FormerlySerializedAs("Game.Buildings.SetLevel, Game")]   // 旧名 SetLevel，序列化兼容
public struct UnderConstruction : IComponentData, ISerializable
{
    public Entity m_NewPrefab;  // Null=空地新建；非Null=改建/升级的目标 prefab
    public byte m_Progress;     // 施工进度，0 起步，完工阈值 100（见下）
    public byte m_Speed;        // 施工速度（随机 39-89，BuildingConstructionSystem.cs:144）
}
```

生命周期行级实锤（`Game.Simulation.BuildingConstructionSystem`，64 帧档）：
- **挂着即施工**：系统查询 = `UnderConstruction + Building`（排除 Destroyed/Deleted/Temp）（BuildingConstructionSystem.cs:637）——组件就挂在施工中的建筑实体上。
- **完工即移除**：`m_Progress < 100` 走累积分支（:140，每 64 帧按 m_Speed 累加，`math.min(255, ...)` 封顶 :160）；`>= 100` 走完工分支：换 prefab（`UpdatePrefab` :172）+ **`RemoveComponent<UnderConstruction>`（:173）**。
- 玩家看到的"施工中 15%"与 `m_Progress` 同刻度（阈值 100 ≈ 百分比；UI 显示侧绑定键 `isUnderConstruction`/`progress` 在 dll 字符串堆实锤，写入端属 cohtml UI 层，未逐行追——不影响判别）。

**游戏 UI 自用同款信号**（`Game.UI.InGame.InfoSectionBase.cs:52-62`）：

```csharp
protected bool UnderConstruction => TryGetComponent<UnderConstruction>(selectedEntity, out var c)
                                     && c.m_NewPrefab == Entity.Null;   // :58
```

信息面板各 section 默认 `displayForUnderConstruction => false`（:34），即 UI 用"组件存在 **且 m_NewPrefab==Null**"判定"新建施工中"并隐藏详情；`m_NewPrefab` 非 Null 的改建/升级中建筑（旧 prefab→新 prefab，完工时若 Null 才补填当前 prefab，BuildingConstructionSystem.cs:168-170）UI 照常显示信息——旧楼视为仍在运营。

**mod 口径建议**：
- **简单口径（推荐）**：`HasComponent<Game.Objects.UnderConstruction>(e)` 即排除。新建+改建全闭嘴，零误伤（改建就几十秒游戏时间），一次 HasComponent 成本。
- UI 对齐口径（备选）：只排 `TryGetComponent<UnderConstruction>(e, out var c) && c.m_NewPrefab == Entity.Null` 的新建施工，改建中楼照常说话。若后续实机发现"改建中说话"违和再切简单口径。

### 1.2 备选：无（其他路径全部排查过）

- `Game.Buildings.Building.m_Flags`（`Game.Buildings.BuildingFlags`，**byte** 枚举）：None/HighRentWarning/StreetLightsOff/LowEfficiency/Illuminated/Historical（GameBuildings.BuildingFlags.cs）——**无施工位**。
- `Game.Prefabs.BuildingFlags`（uint，19 位，BuildingFlags.cs）是 prefab 侧静态属性（RequireRoad/HasInsideRoom/...），同样无施工位。
- 全类型清单（8,884 类型，logs/address-spike-20260911/all.txt）搜 construction 相关仅 `BuildingConstructionSystem`/`FixUnderConstructionJob`（存档修复）/`UnderConstruction` 本体——组件是唯一载体。

### 1.3 谜解：施工中的商业楼为什么能过白名单②（PropertyRenter 非空）

**租户签约不避施工楼**，租约在"决定搬入"时即挂上，不等完工：

- `CommercialFindPropertySystem` 找房查询 = `PropertyOnMarket + CommercialProperty + PrefabRef`，排除 Abandoned/Condemned/Destroyed/Deleted/Temp——**没有 Exclude\<UnderConstruction\>**（CommercialFindPropertySystem.cs:141）。
- `IndustrialFindPropertySystem` 同口径（IndustrialFindPropertySystem.cs:197，另排 ExtractorProperty）；`HouseholdFindPropertySystem` 全文 0 次出现 UnderConstruction（HouseholdFindPropertySystem.cs:974-975 为找房主体查询）。
- 实机旁证闭环：玩家抓到"施工中 15%"商业楼说话 = 过了白名单② = Renter buffer 非空，与"签约即挂 Renter"一致。

所以"Renter 非空"不是"楼已建成"的代理信号，UnderConstruction 排除必须独立加在租户检查**之前**。

## 2. 开放空间判别信号

### 2.1 学校操场/体育场：服务升级机制，两个实体侧组件全堵

机制链（全部行级实锤）：
1. 服务升级的 prefab 组件 `Game.Prefabs.ServiceUpgrade` 给实体挂 **`Game.Buildings.ServiceUpgrade`（空标记，Size=1）**（Y.ServiceUpgrade.cs:77-80 `GetArchetypeComponents`；组件定义 ServiceUpgrade.cs）。这类升级是**独立放置的升级建筑**（BuildingPrefab+ServiceUpgrade，有 `Building` 组件——BuildingPrefab.cs:43）。
2. 贴附式扩展走 `Game.Prefabs.BuildingExtensionPrefab`，给实体挂 **`Game.Buildings.Extension { ExtensionFlags m_Flags }`**（X.Prefabs.BuildingExtensionPrefab.cs:45-49；组件定义 Extension.cs；ExtensionFlags 仅 None/Disabled）。
3. 操场为什么带 `School` 组件：`Game.Prefabs.School` 的 `GetArchetypeComponents` **无条件**给实体加 `Game.Buildings.School`（Y.School.cs:31-40，ServiceUpgrade==null 检查只门 Student/Efficiency/ServiceDistrict）——所以操场实体能过白名单①直收。
4. 升级与主楼的绑定：`IServiceUpgrade.GetUpgradeComponents` 的组件（School+Student，Y.School.cs:42-46）加给的是**主楼** `owner.m_Owner`（ServiceUpgradeSystem.UpgradeInstalled，ServiceUpgradeSystem.cs:138-156）；主楼侧 `InstalledUpgrade` buffer（m_Upgrade→升级实体，InstalledUpgrade.cs）与升级实体侧 `Owner` 互指。

**判别信号（首选）**：实体侧 `Game.Buildings.ServiceUpgrade` **或** `Game.Buildings.Extension` 任一 → 排除。两条升级实体路径各中其一，双组件判定即全包；零解引用（不碰 Owner/buffer），一次 HasComponent 成本。**排除必须放在白名单① School/Hospital/Signature 直收之前**——操场正是从①漏入的（医院侧楼等同理一并堵住）。

备选（第三保险）：`Game.Common.Owner` 存在且 owner 有 `Building` → 附属。覆盖面与前两组件重叠，仅当前两组件实机漏网时启用。

### 2.2 停车场：专属组件直球排除

- `Game.Buildings.CarParkingFacility`（空标记）、`Game.Buildings.ParkingFacility { float m_ComfortFactor; ParkingFacilityFlags m_Flags }`、`Game.Buildings.BicycleParkingFacility`（T.Buildings.*.cs，all.txt:5946-5948）——停车场/停车楼/自行车棚实体专属组件。排除后停车设施声音归车档/行人锚点，与"车站不当楼说话"（§12 #57 既有先例）同一治理逻辑。

### 2.3 露台仓库（开放仓储堆场）：租户侧首选，prefab 侧待标定

**首选 A：租户公司类型 `Game.Companies.StorageCompany`**（T.Companies.StorageCompany.cs，IComponentData）。
- 实锤链：仓储公司 = 公司侧组件（公司查询 `StorageCompany + PropertyRenter`，W.StorageCompanySystem.cs:933）；仓储系统处理函数直接消费楼的 `SpawnableBuildingData + BuildingData`（ProcessStorage 签名，:1060）——仓储公司租的就是分区自长建筑（工业/商业 property），与玩家观察"属于工业/商业 property"吻合。
- 读法：白名单②拿到 Renter buffer 后，`TryGetComponent<Game.Companies.StorageCompany>(renter)` 一层解引用（buffer 租户数小、主线程低频，成本可忽略；保守起见查全部租户任一命中即排）。
- 覆盖面：仓储公司承租的堆场/仓库全中；非仓储公司租的开放场地（若有此类造型）漏网。

**首选 B（通用解，待实机标定后启用）：prefab 侧 `BuildingData.m_Flags & BuildingFlags.HasInsideRoom`**。
- 实锤：`Game.Prefabs.BuildingData { int2 m_LotSize; Game.Prefabs.BuildingFlags m_Flags }`（PF.BuildingData.cs:13-15）；`Game.Prefabs.BuildingFlags.HasInsideRoom = 0x800`（BuildingFlags.cs）——命名语义"有室内空间"，正中"密闭楼 vs 开放空间"判别。
- 局限（如实标注）：位值分布在 **prefab 资产数据**里（.cok 内容包），Game.dll 元数据无法 xref 消费方（枚举名字符串全 dll 仅 1 处），离线拿不到"哪些 prefab 置/未置该位"。**必须先实机标定**（§4.2）：采样各类型建筑打印 prefab 名 + m_Flags，确认开放类该位为 0、密闭楼为 1，再启用。
- 读法（标定通过后）：`PrefabRef.m_Prefab` → prefab 实体 `GetComponentData<Game.Prefabs.BuildingData>` → 位与。两层只读，主线程低频。

**备选**：几何占地比——prefab 侧 `ObjectGeometryData.m_Size`（建筑网格尺寸，X.Prefabs.ObjectGeometryData.cs:13-17）vs `BuildingData.m_LotSize × 8m`：开放堆场建筑本体矮/占地比低。确定性计算、执行层友好，但阈值需实机标定，且"小房子大地块"正常楼有误伤风险。prefab 名规律匹配为最末兜底（脆弱，不推荐）。

**别用**：`Game.Buildings.Warehouse` 是 `[Obsolete]` 空标记（Z.Buildings.Warehouse.cs），历史遗留；`Game.Buildings.StorageProperty` 是第 5 种 Property 空标记（StorageProperty.cs），不在白名单四类内天然不收，与本次场景无关。

### 2.4 覆盖面评估

| 场景 | 信号 | 覆盖 |
|---|---|---|
| 学校操场/体育场（附属升级） | `ServiceUpgrade` 或 `Extension` | ✅ 全包（两条实体路径各中其一） |
| 医院/警局等附属升级楼 | 同上 | ✅ 同机制顺带覆盖 |
| 停车场/停车楼 | `ParkingFacility`/`CarParkingFacility`（+`BicycleParkingFacility`） | ✅ 全包 |
| 露台仓库/开放仓储堆场 | 首选 A 租户 `StorageCompany`；首选 B `HasInsideRoom`（待标定） | 仓储公司承租全中；B 标定通过则通用 |
| 独立地标体育场 | **刻意不排**（SignatureBuildingData 直收维持，地标说话是特性） | 保留现状 |
| 公园/景点 | 已有 `AttractivenessProvider` 查询层排除（§12 #57） | 现状不动 |
| 开采设施（农田/矿场） | `ExtractorProperty` 不在白名单四类 | 天然不收，现状不动 |

## 3. mod 侧接入建议

接入点：`BubbleWorldSpikeSystem.IsTalkativeBuilding`（src/CityLife/GameBridge/BubbleWorldSpikeSystem.cs:628），在**最前面**（①直收之前）加排除闸——全部 HasComponent 级，符合该方法"判定全部 HasComponent/buffer Length 级别，禁止遍历嵌套"的既有纪律（:617-627 注释）：

```csharp
private bool IsTalkativeBuilding(Entity e)
{
    var em = EntityManager;
    // ⓪ 施工中/升级附属/开放设施排除（2026-09-11 spike：docs/spikes/2026-09-11-construction-and-openlot-signals.md）
    if (em.HasComponent<Game.Objects.UnderConstruction>(e)) return false;  // 施工中（租户预挂不算数，§1.3）
    if (em.HasComponent<Game.Buildings.ServiceUpgrade>(e)) return false;   // 服务升级建筑：学校操场/体育场（§2.1）
    if (em.HasComponent<Game.Buildings.Extension>(e)) return false;        // 贴附式扩展（双保险，§2.1）
    if (em.HasComponent<Game.Buildings.ParkingFacility>(e)) return false;  // 停车场：声音归车/行人（§2.2）
    // ① 市政/地标直收（原样）
    if (em.HasComponent<Game.Buildings.School>(e) || ... ) return true;
    // ② 租户非空（原样）+ 仓储堆场闸：
    //   遍历 Renter buffer，任一租户 TryGetComponent<Game.Companies.StorageCompany> 命中 → false（§2.3 首选A）
    // ③ 低密住宅降频（原样）
}
```

- 顺序要点：⓪ 必须在①**之前**（操场经 School 直收漏入是本 spike 的原始 bug）；`StorageCompany` 闸在②拿到 Renter buffer 后顺带做，不新增组件查询。
- `HasInsideRoom`（首选 B）标定通过后再加，需要 `PrefabRef`→prefab 实体→`BuildingData` 两级只读，与②同级成本。
- **查询层不动**：`m_BuildingQuery`（:347-359）保持组件有无粗筛，细口径留 Collect 层逐实体判定——与 §12 #57 ② 的既有分层一致。
- **EntityAnchorSystem 不动**：公司锚点（"空 3 个岗"）挂施工楼语义上成立（"新店筹备中"），玩家未投诉；若后续要一致性，在 EntityAnchorSystem.cs:106 处加一行 `HasComponent<UnderConstruction>(building)` 跳过即可（一行改动，本次不做）。
- **EnsureAnchor（剧场口）不动**：白名单只管"什么楼能自己冒泡"（§12 #57 ③ 既定）。

## 4. 实机验证建议

1. **三场景复现日志**：用 ReadProbeSystem/校准日志对"施工中商业楼、露台仓库、学校操场"各打印一次组件全套：`UnderConstruction`（含 m_Progress/m_NewPrefab）、`ServiceUpgrade`、`Extension`、`ParkingFacility`、Renter[0] 是否 `StorageCompany`、prefab 名。预期：三者分别被 ⓪ 各行闸掉。
2. **HasInsideRoom 标定**（首选 B 启用前提）：采样 ≥50 栋各类建筑（住宅/商业/工业/办公/学校本体/操场/堆场/停车楼），打印 prefab 名 + `BuildingData.m_Flags` 位分布，确认开放类该位 0、密闭楼该位 1；若分布混乱则弃用 B 只用 A。
3. **完工回归**：跟踪同一栋施工楼到完工——UnderConstruction 移除后应恢复说话（验证不误伤正常楼）。
4. **改建抽查**：找一栋升级改建中的商业楼，确认 m_NewPrefab 非 Null（若采用简单口径它会短暂闭嘴，属预期）。
5. **版本回归**：补丁日后重跑 `ilspycmd -t Game.Objects.UnderConstruction -t Game.Simulation.BuildingConstructionSystem -t Game.Prefabs.BuildingFlags` 对比签名（版本敏感面收敛在 GameBridge）。

## 5. 风险与备注

- **性能**：全部新增判定为 HasComponent/单层 buffer TryGetComponent，发生在 Collect 层（逐实体、帧级采样池规模），零 job 化改动；无热路径风险。
- **改建闭嘴窗口**：简单口径下改建中的楼短暂不说话（游戏时间几十秒），可接受；若实机观感违和，切 UI 对齐口径（§1.1）。
- **首选 B 的不确定性**：HasInsideRoom 消费方未定位（元数据无法 xref），位值分布未验证——报告按"待标定"如实交付，标定前不启用。
- **存档兼容**：UnderConstruction/ServiceUpgrade/Extension 均 ISerializable 游戏自管组件，mod 只读不引入持久化负担。
- **模组建筑**：第三方资产若用自定义升级机制（不走 ServiceUpgrade/Extension），⓪ 可能漏网——Owner 备选闸（§2.1）留作后手。
