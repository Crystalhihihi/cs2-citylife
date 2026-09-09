# Spike 报告：处境矩阵数据源字段级验证（§12 #48 落地前奏）

> 日期：2026-09-09 ｜ 对象：Game.dll（只读反编译，ilspycmd 8.2）+ 仓内 CitizenPoolSystem / SerialCast 源码复核 ｜ 未启动游戏
> 结论先行：**7 项全部"可用"或"部分可用"，无不可用项**。两处认知修正：① `TravelPurpose.m_Data` 是 **int 不是实体**（线索里的猜测证伪），行程终点在 `Game.Common.Target.m_Target`；② `CurrentTransport` 步行时指向行人 agent 而非载具，分类前必须先判 `Vehicle` 组件。

---

## 1. Citizen.CurrentBuilding —— ✅ 可用

- **字段签名**：`Game.Citizens.CurrentBuilding : IComponentData { Entity m_CurrentBuilding }`（独立组件，挂在市民实体上，不在 Citizen struct 内）。
- **语义**：市民**在建筑内时**挂上，值为建筑实体。出生即挂：`CitizenPresenceSystem.CitizenPresenceJob` 给新市民 `AddComponent(new CurrentBuilding{ m_CurrentBuilding = 自家房产/服务建筑/外部连接 })`，同时 `RemoveComponent<TravelPurpose>`。传送到站分支：`TripNeededSystem` `SetComponent(new CurrentBuilding{ m_CurrentBuilding = target.m_Target })`。
- **离开清空**：**实锤**——`TripNeededSystem.TripNeededJob.Execute` 行程分发分支（约 decomp 行 1622）：`AddComponent(TravelPurpose{...})` 紧接 `RemoveComponent<CurrentBuilding>(entity)`。佐证：同系统 `m_StuckGroup` 查询 `Exclude<CurrentTransport>` + `Exclude<CurrentBuilding>`（"在路上"与"在建筑内"互斥）。
- **注意**：市民在载具/路上时**没有**该组件（HasComponent 判空即可区分"室内/在外"）；"建筑实体"可能是外部连接（OutsideConnection）等非建筑实体，消费时要 `HasComponent<Game.Buildings.Building>` 兜底。

## 2. 行程两端建筑 —— ✅ 可用（读法与直觉不同）

- **TravelPurpose 纠错**：`{ Purpose m_Purpose; int m_Data; Resource m_Resource }`——`m_Data` 是 **int**（用途相关数据，如 Leisure 资源索引），**不是目标实体**。
- **行程终点**：分发瞬间 `TripNeededSystem` 给市民 `AddComponent(new Target{ m_Target = trips[0].m_TargetAgent })`（decomp 行 1353-1359）。即旅行中读 **`Game.Common.Target.m_Target`**（`Game.Common.Target { Entity m_Target }`）；`Target` 在行程结束/清理时 `RemoveComponent<Target>`（同文件 IL_1458 分支）。
- **起点**：行程 buffer 元素 `TripNeeded : IBufferElementData { Entity m_TargetAgent; Purpose m_Purpose; ... }`（挂在市民上的动态缓冲区，分发时被消费移除）。**旅行中起点不在市民实体上保留**；但行人 agent（`Game.Creatures.Resident` 实体）携带 `Game.Pathfind.PathInformation { Entity m_Origin; Entity m_Destination; ... }`，两端实体齐全（TripNeededSystem decomp 行 988 用 `m_CurrentBuilding == m_Destination` 判到达，佐证两端是建筑级实体）。
- **目标→建筑映射**（`TripNeededSystem.SpawnDeliveryTruck`，decomp 行 128-150，语义铁证）：
  - `m_ServiceRequestData.HasComponent(targetAgent)` → 服务请求：目的地取请求上 `PathInformation.m_Destination`；
  - 否则 targetAgent 本体即目标；
  - `PropertyRenter.m_Property`：目标是租户实体（公司/住户）时，映射到其房产建筑（decomp 行 145-150 `TryGetComponent(entity3, out componentData2)` → `entity3 = componentData2.m_Property`）；
  - 余下情况可能是外部连接（OutsideConnection）或载具，需按组件逐级降级。
- **无 CurrentTrip/Trip 组件**：Game.Citizens 全类型枚举无 Trip 系组件（仅 `TripNeeded` buffer 与 `PetTrip`）。

## 3. 载具类型 —— ✅ 可用

- **链**：市民 `Game.Citizens.CurrentTransport { Entity m_CurrentTransport }` → 该实体若是载具则有 `Game.Vehicles.Vehicle`（空标记组件）；步行时指向行人 agent（**无 Vehicle 组件 → 判"步行"**，这是必须的先行判断）。
- **分类组件（均存在于 Game.Vehicles，字段级实锤）**：
  - 私家车 `PersonalCar { Entity m_Keeper; PersonalCarFlags m_State }`
  - 出租车 `Taxi { Entity m_TargetRequest; TaxiFlags m_State; ... }`
  - 公交/公共运输 `PublicTransport { Entity m_TargetRequest; PublicTransportFlags m_State; uint m_DepartureFrame; int m_RequestCount; ... }`
  - 货车 `DeliveryTruck { DeliveryTruckFlags m_State; Resource m_Resource; int m_Amount }`（货运细分还有 `CargoTransport`、`GoodsDeliveryVehicle`、`PostVan`、`GarbageTruck`）
  - 服务车：`Ambulance/PoliceCar/FireEngine/Hearse/MaintenanceVehicle` 等同模式。
- **市民↔载具的间接层**：agent 上 `Game.Creatures.CurrentVehicle { Entity m_Vehicle; CreatureVehicleFlags m_Flags }` 指所乘载具；载具反向有 `Passenger` buffer。
- **注意**：CurrentTransport 的指向在"步行 agent / 载具"间随上/下车切换（TripNeededSystem 分发时指向 SpawnResident 产生的 agent，行 1610）；prefab 侧无需再查类型标记，运行时组件分类已够用。

## 4. 车站候车人数 —— ✅ 可用

- **字段签名**：`Game.Routes.WaitingPassengers : IComponentData { int m_Count; int m_OngoingAccumulation; int m_ConcludedAccumulation; ushort m_SuccessAccumulation; ushort m_AverageWaitingTime }`，挂在**车站实体**上（`Game.Routes.TransportStop { Entity m_AccessRestriction; float m_ComfortFactor; ... }` 同实体共存）。
- **语义**：`m_Count` = **当前候车人数**。证据：`Game.Simulation.WaitingPassengersSystem` 三段式——`ClearWaitingPassengersJob`（清零）→ `CountWaitingPassengersJob`（扫车道上行人：`CreatureUtils.TransportStopReached(currentLane)` 或 Queue 元素 `queue.m_TargetEntity` 指向车站时 `Interlocked.Add(ref valueRW.m_Count, accumulation.x)` 累加，含 GroupCreature 同伴数）→ `TickWaitingPassengersJob`（衰减/结算）。
- **注意**：是**每帧统计重建的计数**，不是站内实体列表；"≥2 人真名单绑定"（#48 多人小剧场）需另走四叉树或 Queue buffer 拿实体名单，m_Count 只做"人气够不够"的门槛判据。`m_AverageWaitingTime` 可当"等多久了"的戏点调料。

## 5. 建筑分类 —— ✅ 可用

- **类别组件（Game.Buildings，实锤存在）**：`ResidentialProperty`（空标记 IEmptySerializable）、`CommercialProperty { Resource m_Resources }`、`IndustrialProperty { Resource m_Resources }`、`OfficeProperty`（空标记）、另有 `ExtractorProperty`（原材料）。建筑实体上互斥挂其一。
- **zone 类型读法**（链式，两级 prefab）：建筑实体 `PrefabRef.m_Prefab` → 建筑 prefab 上 `Game.Prefabs.SpawnableBuildingData { Entity m_ZonePrefab; byte m_Level }` → zone prefab 上 `Game.Prefabs.ZoneData { Game.Zones.ZoneType m_ZoneType; AreaType m_AreaType; ... }`（`ZoneType { ushort m_Index }`）；zone 显示名经 ZoneData 所在 prefab 走 PrefabSystem/`m_Index` 解析。
- **签名建筑**：`Game.Prefabs.SignatureBuildingData`（空标记组件，挂在**建筑实体**上）——仓内现有用法复核无误：`CityChangeSystem.cs:210` `HasComponent<SignatureBuildingData>(building)` 判 NewSignature 锚点；`PetitionSystem.cs:73-75` 注释"实体侧空标记组件，dump 实锤"+查询引用。两处引用方式与本 spike 复核一致。
- **注意**：服务建筑（学校/医院等）不走 zone 链，分类要靠各自 prefab/组件（如 `Game.Buildings.School`、`Hospital`），处境矩阵 v1 用"住宅/商/工/办/签名/其他"六分即可。

## 6. 四叉树空间查询 —— ✅ 可用（六棵树全部字段级实锤）

**访问器与泛型参数**（`GetXxxSearchTree(bool readOnly, out JobHandle dependencies)`，readOnly=false 时 Combine 读写依赖）：

| 系统 | 树 | 元素类型 TItem | 边界 TBounds | 喂的实体 |
|---|---|---|---|---|
| Game.Objects.SearchSystem | Get**Static**SearchTree / Get**Moving**SearchTree（两棵） | `Entity` | `QuadTreeBoundsXZ` | static：Object+Static（建筑/树等，OnCreate 查询实锤）；moving：行人/动物 agent（`Game.Creatures.ReferencesSystem` 对不在车道缓冲的 Human/Animal 实体 `m_SearchTree.Add(entity, new QuadTreeBoundsXZ(bounds))`）+ 载具（`Game.Vehicles.ReferencesSystem` UpdateVehicleReferencesJob 同法 Add，decomp 行 1053 `GetMovingSearchTree(false)`） |
| Game.Net.SearchSystem | Get**Net**SearchTree / Get**Lane**SearchTree（两棵） | `Entity` | `QuadTreeBoundsXZ` | 路段/车道 |
| Game.Zones.SearchSystem | GetSearchTree | `Entity` | **`Bounds2`**（唯一非 QuadTreeBoundsXZ） | 分区 Block（ZoneUtils.CalculateBounds） |
| Game.Areas.SearchSystem | GetSearchTree（另有带 `out NativeParallelHashMap<Entity,int> triangleCount` 重载） | `AreaSearchItem { Entity m_Area; int m_Triangle }` | `QuadTreeBoundsXZ` | 区域三角面片 |
| Game.Routes.SearchSystem | GetSearchTree | `RouteSearchItem { Entity m_Entity; int m_Element }` | `QuadTreeBoundsXZ` | Waypoint+Position 实体（线路站点/路径点） |
| Game.Effects.SearchSystem | GetSearchTree | `SourceInfo` | `QuadTreeBoundsXZ` | 效果源 |

**类型契约**（Colossal.Collections.dll 实锤）：
- `NativeQuadTree<TItem, TBounds>`：`Add/Update/TryRemove/Get/TryGet/Clear` + `Iterate<TIterator>(ref TIterator iterator, int startNode = 0)`（另有 Select/带子数据重载）。
- `INativeQuadTreeIterator<TItem, TBounds> : IUnsafeQuadTreeIterator<TItem, TBounds>`，两方法：`bool Intersect(TBounds bounds)`（子树剪枝，false=不进）+ `void Iterate(TBounds bounds, TItem item)`（叶子收集）。
- `Game.Common.QuadTreeBoundsXZ { Bounds3 m_Bounds; BoundsMask m_Mask; byte m_MinLod; byte m_MaxLod }`；默认构造 mask=`Debug|NormalLayers|NotOverridden|NotWalkThrough`。参考实现：`QuadTreeBoundsXZ.DebugIterator<TItem>`（Intersect 里 `MathUtils.Intersect(bounds.m_Bounds, m_Bounds)`）。

**归属判定**：市民（行人形态）= Objects **moving** 树（元素是 agent 实体，`Game.Creatures.Resident.m_Citizen` 回指市民）；车辆 = Objects **moving** 树；建筑 = Objects **static** 树。

**最小可用写法（契约级示意，非实现）**：

```csharp
// 圆心 center(float3, XZ 平面)、半径 R 查附近实体
var tree = m_ObjectsSearchSystem.GetStaticSearchTree(readOnly: true, out var deps);
deps.Complete();                       // 组炉在主线程低频点查，直接 Complete 最省心
var query = new QuadTreeBoundsXZ(new Bounds3(center - R, center + R), BoundsMask.NormalLayers, minLod: 0, maxLod: 4);
var it = new NearbyIterator { queryBounds = query, results = list };
tree.Iterate(ref it);                  // 结构性遍历：Intersect 剪枝 → Iterate 收 Entity
struct NearbyIterator : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ> {
    public QuadTreeBoundsXZ queryBounds; public NativeList<Entity> results;
    public bool Intersect(QuadTreeBoundsXZ b) => MathUtils.Intersect(b.m_Bounds, queryBounds.m_Bounds);
    public void Iterate(QuadTreeBoundsXZ b, Entity item) { /* 精判距离后 Add(item) */ }
}
```

- **注意**：① moving/static 两棵都要查（市民+车 vs 建筑）；② 树元素是粗粒度包围盒，Iterate 收到后要按真实坐标精判距离；③ 依赖手柄纪律：读树必须 `GetSearchTree(true, out deps)` + `deps.Complete()`（或 Schedule 进 job + `AddXxxSearchTreeReader`），否则会与写线程竞争；④ Zones 树是 Bounds2 不是 Bounds3，写迭代器时泛型别抄错；⑤ 只在组炉时查（#48 已定：不进渲染热路径）。

## 7. SerialCast 实体绑定复核 —— ⚠️ 部分可用（当前未绑定，补绑成本低）

- **现状**：`SerialCast.cs:12` `SerialCharacter.Name = ""` 注释即"真实市民名（CitizenPoolSystem 取样）"——**只存名字字符串，无实体字段**。上游 `CitizenPoolSystem.CitizenContext` 同样只有 `Name + Context` 两个 string（`CitizenPoolSystem.cs:10-20`）；采样循环里实体 `e` 就在手上（行 70-86）但**丢弃了**，`Entries` 暴露的是 `IReadOnlyList<CitizenContext>`。
- **结论**：剧组角色当前**没有**绑定到模拟实体，气泡系统按实体锚定剧组角色**现在不行**。
- **补绑路径**：给 `CitizenContext` 加 `Entity` 字段（池里现成），SerialCast 侧存实体即可锚定。风险两点：① 市民会死/搬走/MovingAway——绑定必须带失效校验（`EntityManager.Exists` + `Entity` 版本代际自动防复用），失效即锁剧情换绑；② `SerialCast` 是"纯 Content 数据结构（版本免疫）"（文件头注释自述），直接塞 `Unity.Entities.Entity` 会破层——建议实体留在 GameBridge 层做"名字→实体"映射表，SerialCast 继续只存名字，或接受 SerialCast 引用 Unity.Entities 的小妥协（Entity 是 struct 无引擎依赖，实际无害，但纪律上要在文件头注释里记一笔）。

---

## 处境矩阵字段定案建议（v1 进/缓）

**进 v1**（字段直读、零推导、已有先例）：
1. **是谁**：Citizen.m_State 位（年龄/性别/游客/无家）+ Worker/Student/Household.m_Resources——CitizenPoolSystem 已量产，直接扩字段。
2. **在干嘛**：TravelPurpose.m_Purpose（已量产）+ **CurrentBuilding 有无**做"室内/在外"粗分。
3. **在哪/从哪来到哪去**：CurrentBuilding（室内时=所在建筑）；旅行中 `Target.m_Target`（终点）+ 建筑分类链（§5）；起点用 PathInformation.m_Origin（经 CurrentTransport→agent）或按 purpose 推（GoingHome⇒从单位/学校回）。
4. **乘什么**：CurrentTransport → Vehicle 判定 → PersonalCar/Taxi/PublicTransport/DeliveryTruck 四分类（无 Vehicle=步行，无 CurrentTransport=在室内）。
5. **店内/车站人气**：CurrentBuilding 计数（同建筑市民数）+ WaitingPassengers.m_Count。
6. **环境圈四叉树**：Objects static+moving 半径查询（契约已明，实现成本低），蒸馏纪律照 #48（≤3 条、每条 ≤15 字）。
7. **剧组实体绑定**：CitizenContext 加 Entity（小改），SerialCast 侧走 GameBridge 映射表。

**缓一缓**（有坑或边际收益低）：
- zone 细分（SpawnableBuildingData→ZoneData 链要两次 prefab 跳转，v1 用住宅/商/工/办/签名六分足够；细分留给"名店/街区风格"后续）。
- WaitingPassengers 的"真名单"（要扫 Queue buffer/四叉树，v1 只用 m_Count 门槛）。
- Areas/Routes/Zones 三棵树（处境矩阵主用 Objects 两棵；车站名单需求若起来再动 Routes 树）。
- 载具 prefab 级细分（公交车型/线路号：PublicTransport.m_TargetRequest 可追线路，但台词收益不抵复杂度）。

**待查记号**（本 spike 未覆盖，照 #48 遗留）：ShopAdSystem 发帖路径排查另开 spike。
