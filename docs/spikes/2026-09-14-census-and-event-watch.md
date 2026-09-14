# Spike 报告：≥50 栋建筑普查 dump + 事件监听口探针（#62 场所名分级 / #64 城市记忆层 / #65 玩家动作反应层 的收口工具）

> 日期：2026-09-14 ｜ 对象：Game.dll（只读反编译，ilspycmd 8.2.0.7535，`DOTNET_ROLL_FORWARD=LatestMajor` 绕过本机无 .NET 6 运行时问题）+ 游戏内实机 dump（本报告 §4 待回填）
> Game.dll 版本：`Cities2_Data\Managed\Game.dll`，12,055,552 字节，文件时间 2026-06-29；Steam buildid **23700737**（appmanifest_949230.acf）；Unity 引擎 2022.3.71f1（与 2026-09-11 address-spike 同版，本报告头部照抄并当日复核一致）
> 反编译产物存档：`logs/census-spike-20260914/`（普查字段）+ `logs/event-watch-spike-20260914/`（事件监听口）
> 收口工具：`src/CityLife/GameBridge/CensusSpikeSystem.cs`（Ctrl+6 普查 dump / Ctrl+7 事件探针开关）
> 结论先行：**组件层路径全部钉死**（§1 实锤表）；**事件监听口语义实锤**——Created/Deleted 只活一帧、EventJournal 只跟 EventPrefab（建造拆除走它无用）、灾难规模=受害实体 m_Event 回指针计数（§3）。剩 7 项资产层/运行时可见的未实锤（§4），靠玩家实机按两个热键把数据打进 `Logs/CityLife.log` 收口。

---

## 1. 普查字段路径实锤表（组件层，逐条定向反编译）

| # | 字段 | 可得性 | 读法 | 证据（logs/census-spike-20260914/ 除另注） |
|---|---|---|---|---|
| 1 | prefab 内部名 | ✅ 实锤 | 建筑实体 `PrefabRef.m_Prefab` → `PrefabSystem.GetPrefabName(prefabEntity)`（public；内部走 PrefabData.m_Index → PrefabBase.name） | PrefabSystem.cs:975-986 |
| 2 | prefab 本地化名键 | ✅ 读法实锤，挂接面待实机（§4①） | prefab **资产**挂 `Game.Prefabs.Localization`（class : ComponentBase，有 `m_LocalizationID`）；其 `GetPrefabComponents` **空挂**——组件不上 prefab 实体，`EntityManager.HasComponent<Localization>` 无从谈起，必须 `PrefabSystem.TryGetPrefab(prefabEntity, out PrefabBase)` → `pb.TryGet<Localization>(out var loc)`。未挂则 PrefabUISystem 拼 `Assets.NAME[prefab.name]`（服务建筑是 `Services.NAME[...]`） | Localization.cs:7-18；PrefabUISystem.cs:1541-1563 |
| 3 | 分区自长判定 | ✅ 生产已有 | `EnvironmentDigestSystem.IsZoneGrown(em, building)`：prefab 有 `SpawnableBuildingData`（m_ZonePrefab/m_Level）且无 `SignatureBuildingData`（实体/prefab 两查保险）——与游戏 NameSystem.GetName 选择逻辑同源 | SpawnableBuildingData.cs:8-12；address-spike §3/§4 |
| 4 | 建筑分类 | ✅ 生产已有 | `CitizenPoolSystem.ClassifyBuilding(em, building)`：景点/学校/医院/住宅区/商店/工厂/办公楼/公园（SignatureBuildingData/School/Hospital/四 Property/AttractivenessProvider） | CitizenPoolSystem.cs:284-297 |
| 5 | prefab 侧 BuildingData | ✅ 实锤（只在 prefab 实体） | `Game.Prefabs.BuildingData { int2 m_LotSize; BuildingFlags m_Flags }`——HasComponent(prefab 实体) 先行再 GetComponentData；运行时建筑实体**没有** | Prefabs.BuildingData.cs:7-12 |
| 6 | 实体侧 Building.m_Flags | ✅ 实锤 | `Game.Buildings.Building { m_RoadEdge; m_CurvePosition; m_OptionMask; BuildingFlags m_Flags }`——`Game.Buildings.BuildingFlags` 是 **byte**，状态类。⚠️ 与 #5 的 `Game.Prefabs.BuildingFlags`（**uint**，地块/通行类）同名不同义，两套都打印并标注 | Building.cs:6-15；Buildings.BuildingFlags.cs；Prefabs.BuildingFlags.cs |
| 7 | 租户 | ✅ 实锤 | 建筑 `Game.Buildings.Renter` buffer，元素只有 `Entity m_Renter` | Renter.cs:6-14 |
| 8 | 租户类型判别 | ✅ 实锤 | 公司=HasComponent `Game.Companies.CompanyData`（有 `m_Brand` 品牌 prefab 指针）；住户=HasComponent `Game.Citizens.Household`；其余=其他 | CompanyData.cs:7-13；Household.cs:6-22 |
| 9 | 公司产出（加工/工业） | ✅ 实锤 | `Game.Prefabs.IndustrialProcessData { ResourceStack m_Input1/m_Input2; m_Output; int m_WorkPerUnit; byte m_IsImport }`，`ResourceStack { Resource m_Resource（ulong 位标志，§2）; int m_Amount }`。**只在公司 prefab 实体**（ProcessingCompany.GetPrefabComponents 挂它），运行时公司实体没有 | IndustrialProcessData.cs:8-19；ResourceStack.cs:8-11；Prefabs.ProcessingCompany.cs:21 |
| 10 | 开采/服务公司产出 | ⚠️ 半实锤（§4③） | 无 IndustrialProcessData。开采公司 prefab 挂 `Game.Prefabs.ExtractorCompanyData`=**空标记**（本次写码时补钉：命名空间是 Game.Prefabs 不是 Game.Companies，且无字段）；服务公司 prefab 挂 `Game.Companies.ServiceCompanyData`（m_MaxService/m_WorkPerUnit/m_MaxWorkersPerCell/m_ServiceConsuming——**无产出资源字段**）。产出读法只能实机 dump 再看 | Prefabs.ExtractorCompany.cs:13；ServiceCompanyData.cs:6-13；Prefabs.ServiceCompany.cs:21 |
| 11 | 显示名/地址 | ✅ 生产已有 | `NameSystem.GetRenderedLabelName(entity)`（mod 各处同款）；地址 `EnvironmentDigestSystem.TryGetAddressLabel`（游戏公共静态 BuildingUtils.GetAddress 现算，不复制公式） | address-spike 全文 |
| 12 | 可入刊事件 prefab 清单 | ✅ API 实锤，内容待实机（§4⑦） | `Game.Events.EventJournalSystem.eventPrefabs`（public `IEnumerable<JournalEventComponent>`，逐项 `jc.name`/`jc.GetDataFlags()`/`jc.GetEffectFlags()`）；来源查询 = EventPrefab+PrefabData（**只有灾难类 EventPrefab，建造/拆除不在其列**） | event-watch/EventJournalSystem.cs:425-453、503 |

## 2. 枚举表（普查映射要用，照抄反编译）

### 2.1 `Game.Economy.Resource : ulong`（位标志，Resource.cs:3-49 全量）

| 值 | 名 | 值 | 名 |
|---|---|---|---|
| 0 | NoResource | 0x1 | Money |
| 0x2 | Grain | 0x4 | ConvenienceFood |
| 0x8 | Food | 0x10 | Vegetables |
| 0x20 | Meals | 0x40 | Wood |
| 0x80 | Timber | 0x100 | Paper |
| 0x200 | Furniture | 0x400 | Vehicles |
| 0x800 | Lodging | 0x1000 | UnsortedMail |
| 0x2000 | LocalMail | 0x4000 | OutgoingMail |
| 0x8000 | Oil | 0x10000 | Petrochemicals |
| 0x20000 | Ore | 0x40000 | Plastics |
| 0x80000 | Metals | 0x100000 | Electronics |
| 0x200000 | Software | 0x400000 | Coal |
| 0x800000 | Stone | 0x1000000 | Livestock |
| 0x2000000 | Cotton | 0x4000000 | Steel |
| 0x8000000 | Minerals | 0x10000000 | Concrete |
| 0x20000000 | Machinery | 0x40000000 | Chemicals |
| 0x80000000 | Pharmaceuticals | 0x100000000 | Beverages |
| 0x200000000 | Textiles | 0x400000000 | Telecom |
| 0x800000000 | Financial | 0x1000000000 | Media |
| 0x2000000000 | Entertainment | 0x4000000000 | Recreation |
| 0x8000000000 | Garbage | 0x10000000000 | Fish |
| 0x20000000000 | Last | ulong.MaxValue | All |

### 2.2 两套 BuildingFlags（同名不同义，§1 #5/#6）

- **`Game.Prefabs.BuildingFlags : uint`**（prefab 侧，地块/通行属性，Prefabs.BuildingFlags.cs:5-27）：RequireRoad=0x1、NoRoadConnection=0x2、LeftAccess=0x4、RightAccess=0x8、BackAccess=0x10、RestrictedPedestrian=0x20、RestrictedCar=0x40、ColorizeLot=0x80、HasLowVoltageNode=0x100、HasWaterNode=0x200、HasSewageNode=0x400、HasInsideRoom=0x800、RestrictedParking=0x1000、RestrictedTrack=0x2000、CanBeOnRoad=0x4000、CanBeOnRoadArea=0x8000、RequireAccess=0x10000、CanBeRoadSide=0x20000、HasResourceNode=0x40000。
- **`Game.Buildings.BuildingFlags : byte`**（实体侧，运行状态，Buildings.BuildingFlags.cs:5-14）：HighRentWarning=0x1、StreetLightsOff=0x2、LowEfficiency=0x4、Illuminated=0x8、Historical=0x10。

## 3. 事件监听口侦察结论（反编译实锤，logs/event-watch-spike-20260914/）

### 3.1 一帧生命周期（监听口的物理基础）

`Created`/`Updated`/`Applied`/`Deleted`/`Game.Common.Event` 标记**只活一帧**：PrepareCleanUpSystem 每帧收集带这些标记的实体（PrepareCleanUpSystem.cs:21-41），CleanUpSystem 当帧 `RemoveComponent` 掉更新类标记、`DestroyEntity` 掉 Deleted/Event 实体（Common.CleanUpSystem.cs:48-55）。**推论**：当帧观察必须 GetUpdateInterval=1 的探针；拆 Deleted 支路查询**不能**加 `Exclude<Deleted>`（实体当帧还在、prefab 名/组件照常可读，帧末才销毁）。

### 3.2 灾难（#64 城市记忆层）

- 灾难事件实体 = `Game.Events.Event` 空标记（Event.cs）+ 种类组件：`WeatherPhenomenon`（风暴：m_Intensity/m_PhenomenonRadius/m_HotspotPosition，WeatherPhenomenon.cs:7-21）、`WaterLevelChange`（洪水/海啸：m_Intensity/m_MaxIntensity/m_DangerHeight，WaterLevelChange.cs:7-15）、`Fire` 空标记（Fire.cs）。
- **规模回指针计数法**：受害实体挂回指灾难事件的 m_Event——`OnFire{m_Event,m_Intensity}`（OnFire.cs:8-14）、`Flooded{m_Event,m_Depth}`（Flooded.cs:8-12）、`Game.Common.Destroyed{m_Event}`（Common.Destroyed.cs:8-13）、`InDanger{m_Event,m_Flags}`（InDanger.cs:8-14）、HealthProblem 同族。灾难规模=按 `m_Event == 灾难实体` 过滤计数受害实体（烧了几栋/淹了几栋/塌了几栋），事件本体上**没有**现成的"死亡数/损失额"字段。
- **m_Event 圈人法**：灾难波及的市民=InDanger/HealthProblem 的 m_Event 回指，反查即得"当事人圈"，不用做空间半径猜测（空间圈人另有生产件 EnvironmentDigestSystem.CollectAround 兜底）。
- 可入刊灾难 prefab 清单走 EventJournalSystem.eventPrefabs（§1 #12）——**本次 spike 不做灾难 watcher**，仅记录结论。

### 3.3 建造/拆除（#65 玩家动作反应层）

- 建造 = 实体带 `Game.Common.Created`：Building/Road/Tree 各自 +Created 查询（exclude Temp）。
- 拆除 = 带 `Game.Common.Deleted`（查询口径见 §3.1）。
- **EventJournal 对建造拆除无用**：其 prefab 源查询只有 `EventPrefab + PrefabData`（EventJournalSystem.cs:503）——入刊的只有灾难类事件预制体，建筑/道路不是 EventPrefab，建造拆除永远不进 journal。监听口只能自建一帧探针（本 spike 交付物）。

### 3.4 美化（玩家种树/庭院树）

- 玩家种树 = `Game.Objects.Tree + Created`。`Game.Common.Owner { Entity m_Owner }` 组件存在性已补钉实锤（2026-09-14 ilspycmd 定向，ApplyNetSystem/DestroySystem 等多处挂它）。
- "玩家手种的树无 Owner、庭院树有 Owner（挂在建筑/地块下）"是**推断**未实锤 → §4⑥ 探针逐条打 HasComponent<Owner> 供实机对照。

## 4. 未实锤清单 = 本次实机普查收口项（工具用法 + 结果回填位）

| # | 未实锤项 | 收口手段 | 回填 |
|---|---|---|---|
| ① | 各 prefab Localization 挂接面（多少 prefab 挂了、ID 长什么样） | Ctrl+6 每栋行 `loc=` 字段 | 待玩家实机，结果回填本节 |
| ② | household 住户姓字面（GetRenderedLabelName(household) 是姓还是全名/脏键） | Ctrl+6 租户行 `住户姓=` | 待玩家实机，结果回填本节 |
| ③ | extractor/service 公司产出读法（无 IndustrialProcessData 时的标记组件分布） | Ctrl+6 公司租户行 `extractor=/service=/commercial=` | 待玩家实机，结果回填本节 |
| ④ | GetRenderedLabelName(company) 是否=品牌名（CompanyData.m_Brand 对照） | Ctrl+6 公司租户行 `名=` vs `品牌=` | 待玩家实机，结果回填本节 |
| ⑤ | 分区自长建筑是否带 Created（自长 vs 玩家手放的判别是否成立） | Ctrl+7 开探针后等分区自长/手动放建筑，看 `自长=` | 待玩家实机，结果回填本节 |
| ⑥ | 庭院树 Owner 判别（玩家手种树是否无 Owner） | Ctrl+7 开探针后手动种树 vs 分区自长带出的树，`有Owner=` | 待玩家实机，结果回填本节 |
| ⑦ | 可入刊灾难 prefab 清单（eventPrefabs 实际内容） | Ctrl+6 末尾 `可入刊事件 prefab 共 N：` 行 | 待玩家实机，结果回填本节 |

### 工具用法（玩家照跑）

1. 部署后进任意**有 ≥50 栋建筑的城市存档**，`tail -f Logs/CityLife.log`（--developerMode 下日志在 `AppData/LocalLow/Colossal Order/Cities Skylines II/Logs/CityLife.log`）。
2. **Ctrl+6 = 普查 dump**（按一次打一轮）：`[普查]` 前缀——开头类别分布行 → ≤64 栋逐栋行（实体号/类别/自长/prefab 名/地块/两套 Flags/loc/渲染名/地址）+ 每栋 ≤4 个租户行 → 末尾可入刊事件 prefab 清单 + 合计行数。
3. **Ctrl+7 = 事件探针开关**（拨开再拨关）：开启期间 `[事件探针]` 前缀逐条打新建建筑（含 `自长=`）/新路/新树（含 `有Owner=`）/拆建筑。建议开着探针做一轮操作：手动放一栋建筑、等分区自长、手动种棵树、拆一栋建筑，然后 Ctrl+7 关掉。
4. 单帧同类命中 >20 条自动合并成一行计数（防批量生成时刷屏）。

## 5. 风险与纪律

- **只读不写**：全系统无一处 Add/Set/RemoveComponent，不写回任何模拟数据。
- **主线程低频**：普查 dump 是热键触发的一次性 O(64) 循环（租户 ≤4/栋，一轮几百行内）；GetAddress 只在抽样栋上跑（O(路段数) 警告适用但量级极小）。
- **探针默认关**：m_ProbeOn 默认 false，关闭时不跑任何探针查询（零成本）；开启时空命中 ToEntityArray 后 Length==0 直接返回不分配。
- **ECS 纪律**：禁用 SystemAPI；查询全部 OnCreate 缓存；GetUpdateInterval=1（2 的幂，热键捕获与一帧标记语义的双重需要）；exclude Temp/Deleted（Deleted 探针支路除外，§3.1）；HasComponent 先行再 GetComponentData；游戏自建系统（PrefabSystem/NameSystem/EventJournalSystem）惰性解析+判空。
- **退役方法**：验证完即退役——Mod.cs 删 `updateSystem.UpdateAt<GameBridge.CensusSpikeSystem>(...)` 一行即可（登记处注释已注明）。
