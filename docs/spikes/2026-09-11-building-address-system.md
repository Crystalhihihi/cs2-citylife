# Spike 报告：住宅建筑"115 冬青街"地址串来源与 mod 只读获取路径

> 日期：2026-09-11 ｜ 对象：Game.dll（只读反编译，ilspycmd 8.2.0.7535，`DOTNET_ROLL_FORWARD=LatestMajor` 绕过本机无 .NET 6 运行时问题）+ 游戏内嵌 Locale.cok ｜ 未启动游戏
> Game.dll 版本：`Cities2_Data\Managed\Game.dll`，12,055,552 字节，文件时间 2026-06-29；Steam buildid **23700737**（appmanifest_949230.acf）；Unity 引擎 2022.3.71f1
> 反编译产物存档：`logs/address-spike-20260911/`（NameSystem.cs / BuildingUtils.cs / TitleSection.cs / SelectedInfoUISystem.cs / 全类型清单 all.txt）
> 结论先行：**门牌号不存在任何组件里，是 `Game.Buildings.BuildingUtils.GetAddress`（public static）现算的**——road=路聚合实体、number=沿路里程÷8m取整×2+奇偶侧。mod 只读路径就是**直接调这个游戏公共静态方法**（纯 TryGetComponent/只读 buffer，不复制公式、不写数据），路名用现有 `NameSystem.GetRenderedLabelName(road)`。中文格式串实锤 `"{NUMBER}{ROAD}"` → 玩家看到的"115冬青街"。

---

## 1. UI 链路：选中住宅的地址从哪来（行级实锤）

链路四跳，全部定向反编译实锤：

1. **`Game.UI.InGame.TitleSection.OnWriteProperties`**（选中信息面板标题栏）：`writer.PropertyName("name"); m_NameSystem.BindName(writer, selectedEntity);`（logs/address-spike-20260911/TitleSection.cs:71-72）。`SelectedInfoUISystem` 本身不碰名字，名字绑定在 TitleSection。
2. **`Game.UI.NameSystem.BindName → GetName(entity)`**（NameSystem.cs:282-339）：建筑 prefab 上**有 `SpawnableBuildingData` 且无 `SignatureBuildingData`**（即分区自长建筑，签名/服务建筑除外）时走 `GetSpawnableBuildingName(entity, zonePrefab, omitBrand)`（NameSystem.cs:310-313）。
3. **`GetSpawnableBuildingName`**（NameSystem.cs:465-485）：

   ```csharp
   BuildingUtils.GetAddress(base.EntityManager, building, out var road, out var number);  // 行467
   if (!TryGetCustomName(road, out var customName)) customName = GetId(road);            // 行468-470：玩家自定义路名优先，否则随机路名ID
   ...
   // 住宅（ZoneData.m_AreaType == AreaType.Residential）或无品牌时：
   return Name.FormattedName("Assets.ADDRESS_NAME_FORMAT", "ROAD", customName, "NUMBER", number.ToString());  // 行484
   // 非住宅且 Renter buffer 里有公司 → 带品牌："Assets.NAMED_ADDRESS_NAME_FORMAT"（NAME/ROAD/NUMBER 三参，行481）
   ```

4. **本地化格式串**（`Cities2_Data\Content\Game\Locale.cok` 二进制实锤）：
   - 简体中文：`Assets.ADDRESS_NAME_FORMAT = "{NUMBER}{ROAD}"` → **"115冬青街"**（玩家实锤的串）；`NAMED_ADDRESS_NAME_FORMAT = "{NAME}，{NUMBER}{ROAD}"`
   - English：`"{NUMBER} {ROAD}"` → "115 Holly Street"；`"{NAME}, {NUMBER} {ROAD}"`
   - 旁证：同文件 zh 路名池含"冬青巷"（ALLEY_NAME:56）等 Holly 系译名，与"冬青街"同源。

## 2. 门牌号本体：`BuildingUtils.GetAddress`（不存储，现算）

**全类型枚举（8,884 个类型，logs/address-spike-20260911/all.txt）搜 Address/HouseNumber/BuildingNumber/RoadName/StreetName 全部零命中**——游戏没有地址组件，门牌号不落盘、不进组件，是每次现算的纯函数。

`Game.Buildings.BuildingUtils`（public static class）两个 public 重载（BuildingUtils.cs:381-394、452-537）：

```csharp
public static bool GetAddress(EntityManager entityManager, Entity entity, out Entity road, out int number)
// 读 Building.m_RoadEdge + Building.m_CurvePosition（建筑实体），
// 或 Game.Objects.Attached.m_Parent + m_CurvePosition（附着物，如静态车站站牌——GetStaticTransportStopName 同走此路，NameSystem.cs:500-512）

public static bool GetAddress(EntityManager entityManager, Entity entity, Entity edge, float curvePos, out Entity road, out int number)
```

计算要点（只述语义，不抄公式——mod 直接调用，无需关心）：
- `road` = 临路边的**路聚合实体**（`Game.Net.Aggregated { Entity m_Aggregate }`，聚合实体上挂 `AggregateElement { Entity m_Edge }` buffer 列全部路段）——就是 NameSystem 能解析出路名的那个实体，玩家改路名也挂在它上面。
- `number` = 建筑沿聚合路的累计里程（逐段 Curve.m_Length 累加 + 路口弧长/环岛半径补偿）`RoundToInt(dist / 8f) * 2 + (侧 ? 2 : 1)`——**8 米一个号，奇偶分侧**（BuildingUtils.cs:527）。
- 返回值 `false`：实体无 `Building`/`Attached` 组件，或临路边没有 `Aggregated`（未聚合的路）→ road=Null、number=0。**这是门牌号唯一"不可得"的情形**。

数据来源组件字段实锤（定向反编译）：
- `Game.Buildings.Building { Entity m_RoadEdge; float m_CurvePosition; uint m_OptionMask; BuildingFlags m_Flags }`
- `Game.Objects.Attached { Entity m_Parent; Entity m_OldParent; float m_CurvePosition }`

## 3. mod 侧只读获取路径（推荐写法）

`BuildingUtils.GetAddress` 是**游戏自己的公共静态方法**：调用它=用游戏 API，不是复制游戏逻辑（公式留在游戏侧，版本变了游戏自己负责）。方法体全部 `TryGetComponent` + `GetBuffer(isReadOnly: true)`，纯读。注意只有 `EntityManager` 重载（无 ComponentLookup/job 版），**只能主线程低频用**——锚点采样（EntityAnchorSystem 1024 帧档）正合适。

```csharp
// GameBridge 命名空间内（铁律4：触碰游戏 API 只许在这层）
// 读侧纪律：HasComponent 先行、只读不写、主线程低频
private bool TryGetAddressLabel(Entity building, out string label)
{
    label = "";
    m_NameSystem ??= World.GetExistingSystemManaged<Game.UI.NameSystem>();
    if (m_NameSystem == null) return false;
    if (!EntityManager.HasComponent<Game.Buildings.Building>(building)) return false;   // 先行判空

    if (Game.Buildings.BuildingUtils.GetAddress(EntityManager, building, out var road, out int number)
        && road != Entity.Null)
    {
        string roadName = m_NameSystem.GetRenderedLabelName(road);   // 玩家自定义路名/随机路名，与 UI 同源（RoadSuffix 已在用同款）
        if (!string.IsNullOrEmpty(roadName))
        {
            label = $"{number}{roadName}";   // 与 zh 客户端 "{NUMBER}{ROAD}" 同款："115冬青街"；想要更口语可 $"{roadName}{number}号"
            return true;
        }
    }
    return false;
}
```

备选（不推荐）：`NameSystem.GetName(entity)` 也是 public，能拿到同款 `Name.FormattedName`，但 `Name` 的 `m_NameType/m_NameID/m_NameArgs` **全 private**（NameSystem.cs:40-44），解析要反射，多此一举——`GetAddress` 直调已够。

配套判定（想与游戏 UI 完全一致时照抄这个选择逻辑即可）：
- 分区自长建筑（prefab 有 `SpawnableBuildingData`、无 `SignatureBuildingData`）→ 地址制；住宅永远纯地址，商/工/办有品牌公司时是"品牌，门牌路名"。
- 签名/服务建筑 → 游戏显示 prefab 本地化名（`GetId` → prefab Localization / PrefabUISystem 标题），mod 维持现有 PlaceLabel 路径不动。

## 4. ⑤号追问：`GetRenderedLabelName(住宅建筑)` 返回什么

**永远不是地址**。`GetRenderedLabelName`（NameSystem.cs:236-248）= `TryGetCustomName` → 否则 `GetId(entity)` 查本地化字典，**不过 `GetName` 的地址分支**。住宅的 `GetId`：prefab 有 `SpawnableBuildingData` → 跳到 **zone prefab**（NameSystem.cs:394-397）→ 返回 zone 的 `Localization.m_LocalizationID` → 字典命中是"低密度住宅"式 zone 通用名，未命中是 `Assets.NAME[...]` 原始 ID（与公司名乱码同机制，2026-09-09 已实锤检出规则：`StartsWith("Assets.NAME[")` 即视同无名）。所以住宅锚点名走地址链（§3）是正解，方位兜底可以退役到最末位。

## 5. 降级方案（拿不到门牌号时）

按优先级逐级退（每级都有实锤依据）：

1. **地址全名**：`TryGetAddressLabel` 成功 → "115冬青街"。
2. **路名-only**：`GetAddress` false 或路名空，但 `Building.m_RoadEdge` 非空且 `GetRenderedLabelName(roadEdge)` 有值 → "冬青街沿线"式（现有 `RoadSuffix` 逻辑的平级变体；`m_RoadEdge` 是路段实体，路名解析同样有效，EntityAnchorSystem.cs:210-219 已在用）。
3. **prefab/zone 通用名**：`GetRenderedLabelName(building)` 非空且非 `Assets.NAME[` 前缀 → zone 通用名 + 方位（"城西北的低密度住宅"）。
4. **方位兜底**：现状 `Geo.DirectionOf(pos) + "那家…"`，最末位保留。

注意两种 `GetAddress` 返回 false 的常见情形：非建筑实体（公司锚点的 Label 本来就是公司名，不走此链）；临路边无 `Aggregated`（极少见，如未聚合的特殊道路）。

## 6. 实机验证建议（mod 接入后 smoke test）

1. **对照 UI**：日志打印锚点住宅的 `GetAddress(building) → road/number` + `GetRenderedLabelName(road)`，然后游戏内选中**同一栋**住宅，对比 TitleSection 标题"115冬青街"——数字与路名应逐字一致（zh 客户端）。
2. **校准日志点**：复用 EntityAnchorSystem 现有 `[Anchor·校准]` 低频档（每 16 轮），打印 `地址命中/路名only/方位兜底` 三档计数，确认命中率（预期绝大多数分区建筑地址命中）。
3. **边界抽查**：环岛端点建筑（公式有环岛补偿分支）、玩家自定义路名的街（应显示自定义名）、签名/服务建筑（应仍显示 prefab 名，不给地址属预期）、外部连接旁建筑（GetAddress false → 降级生效）。
4. **版本回归**：补丁日后重跑一次 `ilspycmd -t Game.Buildings.BuildingUtils` 对比签名（版本敏感面收敛在 GameBridge 一处，复核成本一次定向反编译）。

## 7. 风险与备注

- **性能**：`GetAddress` 内部遍历聚合路全部路段 buffer（O(路段数)），另有环岛分支的若干 TryGetComponent。主线程低频（≤每 1024 帧 × 锚点上限个）可忽略；**禁止进渲染热路径/逐帧查询**。
- **线程**：仅 `EntityManager` 签名，只能主线程用；job 化需求出现前不要自作聪明搬公式（搬=复制游戏逻辑，铁律3）。
- **存档兼容**：门牌号不进存档，纯展示层派生值，建筑拆建/路改后自然变化，锚点名带门牌不会引入持久化负担。
- **localization 无关性**：mod 自己拼 `{number}{roadName}`，不依赖游戏语言设置；路名本身已被 `GetRenderedLabelName` 本地化。
