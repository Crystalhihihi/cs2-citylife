# 事件分类学 dump 补探（2026-08-20，突发直采用）

工具：`tools/GameDllDump`（MetadataLoadContext 只读元数据，不反编译方法体）。游戏版本：当前 Steam 版。

## 结论：突发事件直采用这三个查询，全部字段级实锤

| 事件 | 组件 | 挂载实体 | 关键字段 |
|---|---|---|---|
| 火灾 | `Game.Events.OnFire` | **燃烧中的建筑本体**（配合 `Building`+`Transform` 查询） | `m_Event`（事件实体）、`m_Intensity`（强度）、`m_RequestFrame`（起火帧） |
| 车祸 | `Game.Events.AccidentSite`（m_Flags 含 `TrafficAccident=8`） | 事故现场实体 | `m_Event`、`m_PoliceRequest`、`m_CreationFrame` |
| 犯罪 | `Game.Events.AccidentSite`（m_Flags 含 `CrimeScene=4` 且 `CrimeFinished=16` 未置位） | 犯罪现场实体 | 同上 |

`AccidentSiteFlags` 全量：StageAccident=1, Secured=2, CrimeScene=4, TrafficAccident=8, CrimeFinished=16, CrimeDetected=32, CrimeMonitored=64, RequirePolice=128, MovingVehicles=256。

## 废弃路线（留证）

- ~~EventJournal + prefab 名映射~~：`EventJournalSystem.eventJournal`（NativeList\<Entity\>）+ `GetInfo/GetPrefab/TryGetData` 真实存在，但入刊事件靠 `JournalEventComponent` prefab，日常火灾/车祸/犯罪**不入刊**（实机 `[News]` 日志长期空白）。保留监听仅做分类学日志，不作为话题源。
- ~~猜测 prefab 名~~（"FireEvent"/"TrafficAccidentEvent"/"DeathEvent"）：全错，真实组件即上表。

## 其他本轮实锤

- `Game.Events.Fire` / `TrafficAccident` / `Event` / `Destruction` / `HealthEvent` / `Crime`：**空 struct 标记组件**（无字段），只适合存在性查询。
- `Game.Buildings.Building`：`m_RoadEdge / m_CurvePosition / m_OptionMask / m_Flags`——建筑校验用存在性即可（地图选点校验）。
- `Game.UI.NameSystem`：`GetRenderedLabelName(Entity)` / `TryGetCustomName(Entity, ref string)` / `GetDebugName(Entity)`——任意建筑本地化真名（地图选点标签、场馆候选真名用）。
- UI 侧：`cs2/bindings` 顶层 `selectedEntity$: ValueBinding<Entity>`——玩家当前选中实体，地图选点的 UI 数据源。
