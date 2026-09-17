# Spike 报告：事件场景普查（现场反应层技术可行性——"车祸现场围观市民不再唠家常"）

> 日期：2026-09-17 ｜ 性质：纯调查（零生产代码改动）
> 工具：`tools/GameDllDump`（MetadataLoadContext 只读元数据，本 spike 新增「事件场景 spike」探测节，输出 `tools/GameDllDump/dump.txt`，行号引用即该文件本轮产物）+ 既有实锤（docs/spikes/2026-08-20-event-taxonomy.md、2026-09-14-census-and-event-watch.md、logs/decomp-notification-icon.cs、logs/icon-dump-20260820.txt）
> Game.dll 版本：`Cities2_Data\Managed\Game.dll`（Steam buildid 23700737，与 2026-09-14 普查同版）
> 结论先行：**五问全部有解，现场反应层技术可行性成立**——事件源/位置/生命周期/严重度全字段级实锤（§1）；受害者除"车祸涉事者名单与市民的最后一跳需实机复核"外全部可锁定（§2）；圈人两件现成兵器（§3）；图标系统的分类/分级口径可直接借（§4）；建议顺序 车祸/火灾 > 犯罪 > 死亡 > 疾病伤 >> 灾害（§5）。

---

## 1. 事件类型全枚举（ECS 组件/挂载/位置/生命周期/严重度）

### 1.1 日常事件（反应层主战场）

| 事件 | 事件实体 | 现场实体（位置源） | 生命周期（怎么知道结束） | 严重度 | 依据（dump.txt 行） |
|---|---|---|---|---|---|
| **交通事故**（截图橙色六边形） | `Game.Events.TrafficAccident`（空标记，L3739） | `Game.Events.AccidentSite`：`m_Event` 回指、`m_PoliceRequest`、`m_Flags` 含 `TrafficAccident=8`、`m_CreationFrame`/`m_SecuredFrame`（L3608；flags 全量 L3613-3625） | `Secured=2` 置位（`m_SecuredFrame` 记帧）=警方处置完；或现场实体消失。`m_CreationFrame` 可与 frameIndex 求龄 | 每个涉事者一份 `InvolvedInAccident.m_Severity`（L3550-3556） | 分类学 2026-08-20 spike 已实锤上车（EventNewsSystem/BubbleW 事件快反层在用） |
| **建筑火灾** | `Game.Events.Fire`（空标记，L3723） | `Game.Events.OnFire` 挂**燃烧建筑本体**（自带 Transform）：`m_Event` 回指、`m_RescueRequest`、`m_Intensity` 强度、`m_RequestFrame` 起火帧（L3642-3649） | `OnFire` 组件移除=扑灭/烧毁；`m_RequestFrame` 求龄；`m_Intensity` 可作严重度直读；prefab 侧蔓延语义 `Game.Prefabs.Fire`（m_EscalationRate/m_SpreadProbability/m_SpreadRange，L3729-3737） | `m_Intensity`（建筑级，逐栋一份） | 同上，已在用 |
| **犯罪现场** | `Game.Events.Crime`（空标记，L3702） | 同为 `AccidentSite`：`m_Flags` 含 `CrimeScene=4`；`CrimeDetected=32`=已发现、`CrimeMonitored=64`=监控中、`CrimeFinished=16`=**结案（不再活动）**（L3613-3625） | `CrimeFinished=16` 置位/现场实体消失；prefab 侧 `Game.Prefabs.Crime.m_CrimeDuration` 区间（L3708-3721） | 无分级字段；类型只有 `CrimeType.Robbery=0` 一种（L4144-4152） | 分类学已实锤，已在用 |
| **死亡/灵车** | 无独立"死亡事件实体"组件实证；死亡=市民状态位（见下） | 死者=**市民实体本身**：`Game.Citizens.HealthProblem`（`m_Event`/`m_HealthcareRequest`/`m_Flags`/`m_Timer`，L3593-3600）其 `m_Flags` 含 **`Dead=2`**（`HealthProblemFlags` 全量：Sick=1/**Dead=2**/Injured=4/RequireTransport=8/InDanger=16/Trapped=32/NoHealthcare=64，L4002-4016） | `HealthProblem` 组件移除/市民实体被灵车运走回收 | 无；死因类型在 prefab 侧 `Game.Prefabs.HealthEventType`：Disease=0/Injury=1/**Death=2**（L4132-4140） | `Game.Simulation.DeathCheckSystem` 存在（死因判定系统，L3958-3970 名扫描）；灵车佐证见 §2 |
| **疾病伤/救护车** | `Game.Events.HealthEvent`（空标记，L3753） | 病人=`HealthProblem` 含 `Injured=4`/`Sick=1`/`RequireTransport=8` 的市民（同上枚举） | 组件移除 | 无分级；类型 `HealthEventType`（Disease/Injury/Death，L4132-4140） | 救护车字段见 §2 |

### 1.2 灾害（玩家一般关，优先级最低——枚举全量在此）

CS2 灾害事件= `Game.Events.Event` 空标记实体 + 种类组件（2026-09-14 spike §3.2 定案框架，本轮字段复核）：

| 灾害 | 组件 | 位置/范围 | 严重度 | 依据 |
|---|---|---|---|---|
| 龙卷风/风暴 | `Game.Events.WeatherPhenomenon` | `m_PhenomenonPosition`+`m_PhenomenonRadius`（影响圈）、`m_HotspotPosition/Velocity/Radius`（破坏核）（L3664-3674） | `m_Intensity`；prefab 侧 `m_DamageSeverity/m_DangerLevel/m_Evacuate/m_StayIndoors`（L3676-3691） | dump.txt L3664 |
| 洪水/海啸 | `Game.Events.WaterLevelChange` | `m_Direction` 方向 | `m_Intensity`/`m_MaxIntensity`/`m_DangerHeight`（L3693-3700） | 同上 |
| 森林大火 | `Game.Events.Fire` + 多点 `OnFire`（spread，prefab `m_SpreadRange`） | 每栋燃烧建筑 | 同火灾 | §1.1 |
| 雷击 | `Game.Events.LightningStrike` | `m_Position`，`m_HitEntity` 被击实体（L3651-3656） | 无 | 瞬时事件 |

受害回指针（灾害规模=#64 定案"m_Event 圈人法"）：受害实体挂 `OnFire{m_Event}`（L353）/`Flooded{m_Event,m_Depth}`/`Game.Common.Destroyed{m_Event}`/`InDanger{m_Event,m_Flags,m_EndFrame}`（L3558-3566）/`HealthProblem{m_Event}`（L3593）——按 `m_Event==灾害实体` 过滤计数即得规模（2026-09-14 spike §3.2）。

## 2. 受害者可锁定性（每类事件"当事人圈"怎么拿）

| 事件 | 可锁定性 | 路径 | 依据 |
|---|---|---|---|
| 交通事故涉事者 | ✅ **有专属组件** | `Game.Events.InvolvedInAccident`：`m_Event`（回指事故）、**`m_Severity`（该涉事者受伤度）**、`m_InvolvedFrame`（L3550-3556）——按 m_Event==事故实体 反查涉事实体集。**待实机复核一跳**：涉事实体若是车辆/行人 creature，到市民走 `Game.Creatures.Resident.m_Citizen` 回指（生产先例 BindOutdoorRoster）；车内乘员另有 `Game.Vehicles.Passenger` buffer（`m_Passenger`，L3804-3808）兜底 | dump.txt L3550 |
| 火灾住户/被困者 | ✅ | 住户=燃烧建筑 `Game.Buildings.Renter` buffer→Household（Renter.cs 已实锤）；楼内市民=`CurrentBuilding==该楼`（剧场 BuildIndoorGroups 生产先例）；被困者=`HealthProblem` 含 `Trapped=32` 且 m_Event 回指（L4012） | census spike §1 #7 |
| 犯罪受害者 | ✅ **直达** | `Game.Citizens.CrimeVictim{m_Effect}` 挂受害市民（L3602-3606）——按组件查询即得 | dump.txt L3602 |
| 死亡/死者 | ✅ **死者即实体本身** | `HealthProblem.m_Flags & Dead=2`（L4002-4016）；灵车 `Game.Vehicles.Hearse.m_TargetCorpse` 同指一实体（L3825-3833；状态机 `HearseFlags` Dispatched=2/Transporting=4/AtTarget=8，L4048-4060） | 名扫描无"Dead"独立组件，DeathCheckSystem 为判定系统 |
| 疾病伤患者 | ✅ | 救护车 `Game.Vehicles.Ambulance.m_TargetPatient`（L3810-3823；`AmbulanceFlags` 含 **Critical=256 危重**（L4024-4046）可当严重度）；或 HealthProblem Injured/Sick 自查 | dump.txt L3810 |
| 灾害波及者 | ✅ | `InDanger{m_Event,m_EvacuationRequest,m_Flags(StayIndoors=1/Evacuate=2/UseTransport=4/WaitingCitizens=8),m_EndFrame}`（L3558-3566 + DangerFlags L3568-3576）+ HealthProblem InDanger=16/Trapped=32 回指 | dump.txt L3558 |
| 应急服务车辆（反应当事方） | ✅ | 消防车 `FireEngine{m_TargetRequest,m_State(Extinguishing=2/DisasterResponse=8),m_ExtinguishingAmount}`（L3839-3856/L4066-4084）；警车 `PoliceCar{m_TargetRequest,m_State(AccidentTarget=4/AtTarget=8),m_PurposeMask}`（L3858-3882/L4086-4114）；`m_TargetRequest`↔现场 `m_PoliceRequest`/`m_RescueRequest` 对上即"这辆车在处置这件事" | dump.txt L3839 |

写侧纪律备注：`AddAccidentSiteSystem`/`EndangerSystem`/`AddHealthProblemSystem`/`AddCriminalSystem`（icon-dump-20260820.txt L204-212 类型名）是游戏的写入系统，**只读不写，我们不碰**。

## 3. 圈人可行性（给定坐标+半径圈附近市民）

两件现成兵器，都是生产在用的：

1. **空间圈（通用）**：`EnvironmentDigestSystem.CollectAround(pos, radius, statics, movers)`——游戏六棵四叉树（NativeQuadTree）半径查询，statics/movers 分桶；movers 滤 `Game.Creatures.Human+Resident` 回指市民即"周边步行市民圈"（生产先例：S6 环境圈 40m、剧场 BindOutdoorRoster 30m、§12 #63 TryPairWalker 12m）。围观圈半径建议 30-60m，低频调用（事件刷新级，不进热路径）。
2. **回指圈（免空间猜，更准）**：`m_Event==事件实体` 反查——InDanger/HealthProblem/InvolvedInAccident/OnFire/Flooded/Destroyed 一族全部带 m_Event 回指针（§1.2/§2），2026-09-14 spike §3.2"m_Event 圈人法"定案。受害者/被困者优先走这条；围观者（无回指）走空间圈。

补充：`Game.Events.SpectatorEvent`（空标记，L3777-3782）/`SpectatorSite{m_Event}`（L3791-3795）/`Spectate{m_Event,m_Target}`（L3797-3802）是游戏自带的"围观"语义组件族（prefab 侧有 m_PreparationDuration/m_ActiveDuration/m_TerminationDuration 三段时长）——字段结构证明游戏有"谁在看（Spectate.m_Target=被看对象）"的表达。**但它是大型活动（音乐会类）的围观机制，不是事故围观**（开放问题，§6 Q4）：事故围观圈建议自组（空间圈），Spectate 语义只当措辞参考。

## 4. 图标系统对应（橙色六边形归属与可借口径）

**渲染链**（logs/decomp-notification-icon.cs 全文反编译实锤）：`Game.Notifications.Icon` 组件（`m_Location` 世界坐标/**`m_Priority`**/`m_ClusterLayer`/`m_Flags`，dump.txt L3884-3892）挂图标实体 → `Game.Rendering.NotificationIconBufferSystem` 收聚簇/优先级排序 → `NotificationIconRenderSystem`（±1 quad + Texture2DArray + instanced draw）画出。截图橙色六边形=某 `NotificationIconPrefab` 资产（`m_Icon` 贴图，L3943-3955）——交通事故警告图标。

**可借口径（站在游戏肩膀上，不自己造分类分级）**：

- **分类**：图标实体带 `PrefabRef` → 每类通知一个 `NotificationIconPrefab` prefab——**prefab 名即游戏自己的事件分类**（事故/火灾/犯罪/垃圾/水务…）。反应层若要"只反应玩家看得见的事件"，用图标 prefab 名当白名单最对齐玩家感知（名录需实机 dump 补齐，§6 开放问题 Q3；IconConfigurationPrefab.m_MissingIcon 兜底存在）。
- **分级（严重度）**：`Icon.m_Priority` 直读——`IconPriority` 全量：**Min=0 / Info=10 / Problem=50 / Warning=100 / MajorProblem=150 / Error=200 / FatalProblem=250 / Max=255**（L3894-3918）。游戏已替我们分好严重度，反应烈度直接映射（Info=议论、Warning=惊呼、Major+=恐慌），零自造。
- **位置**：`Icon.m_Location` 世界坐标白送；`IconFlags`（Unique/IgnoreTarget/TargetLocation/OnTop/SecondaryLocation/CustomLocation，L3920-3932）可判锚定语义；`IconClusterLayer` Default=0/Marker=1/Transaction=2（L4116-4124）。
- **生命周期兜底**：图标实体随事件消失而销毁——"图标还在=事件还在"（与 §1 组件级生命周期互为冗余校验）。
- 注意分层：`NotificationIconDisplayData.m_CategoryMask`（L3934-3941）+ InfoviewData.m_NotificationMask 是 infoview 的 UI 过滤层，与事件分类无关，别误用。

## 5. 建议优先级（日常频率 × 感知价值）

| 优先级 | 事件类型 | 频率×感知 | 理由（含锁定成熟度） |
|---|---|---|---|
| **P0** | 交通事故 | 高频×高感知 | 实机车祸日常发生且玩家截图原点；图标常驻醒目；涉事圈（InvolvedInAccident）+围观圈（空间圈）+警车处置过程（PoliceCar.AccidentTarget）叙事三段俱全 |
| **P0** | 建筑火灾 | 中频×高感知 | 消防车/强度/蔓延视觉强；受害圈（Renter 住户+CurrentBuilding+Trapped）最完整；OnFire.m_Intensity 直读烈度 |
| **P1** | 犯罪现场 | 中频×中感知 | CrimeVictim 直达；警匪叙事强；但无严重度字段、类型只有 Robbery（题材单一，文案要靠场景补） |
| **P1** | 死亡/灵车 | 高频×低-中感知 | 自然死亡日常但非"现场围观"形态——适合做小范围哀悼/议论（死者家属/邻居），HealthProblem.Dead+ Hearse 链路完整 |
| **P2** | 疾病伤/救护车 | 中频×低感知 | 画面感弱于前三；Ambulance.Critical=256 可当烈度档 |
| **P3** | 灾害（龙卷风/洪水/森林大火/雷击） | 低频×（玩家一般关） | 组件全实锤但玩家关灾害=内容空窗，挂后续；InDanger 回指圈成熟，真要做成本不高 |

## 6. 落地建议与开放问题

**建议架构**（供 #65 反应层扩展参考，本次不定案）：事件表=OnFire/AccidentSite/HealthProblem(Dead|Injured|Trapped)/InvolvedInAccident 低频只读查询（128 帧错峰先例=BubbleW.RefreshActiveEvents/EventNewsSystem 现成）→ 角色圈=**m_Event 回指优先**（受害者）> Renter/CurrentBuilding（住户）> CollectAround 空间圈（围观者 30-60m）→ 反应卡按角色分档（受害者/家属/围观者/应急车辆）→ 现有突发/闲聊管线发炉。只读不写（AddXxxSystem 全不碰）。

**开放问题**：
1. InvolvedInAccident 挂载实体类型（车辆 creature 还是市民）与 m_Severity 值域——需实机车祸现场 dump 复核一跳（字段已钉，值未目击）。
2. `HealthEvent` 空标记组件挂哪类实体、`HealthEventType.Death` 实机是否真产生独立事件实体——死亡监听当前以 HealthProblem.Dead 为准即可，HealthEvent 事件实体是锦上添花。
3. NotificationIconPrefab 名录（事故/火灾/犯罪各叫什么 prefab 名）——图标白名单需要时实机 dump 一次即可补齐（NameSystem/PrefabSystem 读名生产已有先例）。
4. SpectatorEvent 是否为大型活动专用（若实机发现它也挂事故现场，围观圈可白嫖游戏语义，升级 P0 体验）。

**纪律**：本 spike 全部只读（MetadataLoadContext/既有反编译存档），零生产代码改动；GameDllDump 新增探测节与 dump.txt 为工具产物。
