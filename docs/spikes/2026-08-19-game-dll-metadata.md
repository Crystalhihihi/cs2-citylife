# Spike：Game.dll 元数据精确复核（GameDllDump）

> 日期：2026-08-19
> 工具：`tools/GameDllDump/`（MetadataLoadContext 只读反射，不加载/执行游戏代码，不反编译方法体）
> 对象：`D:\SteamLibrary\steamapps\common\Cities Skylines II\Cities2_Data\Managed\Game.dll`
> 原始输出：`tools/GameDllDump/dump.txt`（本报告所有字段/签名均来自该次真实运行，无凭记忆补全）
> 设计文档：`docs/specs/2026-08-16-cs2-living-city-design.md`（下称"设计文档"，M1=读侧 §4，M6=写回原语 §4/§M6）

## 0. 程序集总体信息

| 项 | 值 |
|---|---|
| AssemblyName / Version | Game, 0.0.0.0 |
| TargetFrameworkAttribute | **未标注**（Unity 编译的程序集通常不带此特性） |
| 目标运行时（由引用推断） | 引用 `netstandard, Version=2.1.0.0`（+ `mscorlib 4.0.0.0` facade）→ **mod csproj 应 target `netstandard2.1`** |
| 类型总数 | **8903** |
| 值得注意的引用 | cohtml.Net 1.64.0.7（UI 层）、Unity.InputSystem 1.14.2.0、Unity.Entities/Burst/Collections/Mathematics 全套 |

## 1. 市民（Game.Citizens）

### Game.Citizens.Citizen —— struct，IComponentData

| 字段 | 类型 |
|---|---|
| m_PseudoRandom | UInt16 |
| m_State | Game.Citizens.CitizenFlags |
| m_WellBeing | Byte |
| m_Health | Byte |
| m_LeisureCounter | Byte |
| m_PenaltyCounter | Byte |
| m_UnemploymentCounter | Int32 |
| m_BirthDay | Int16 |
| m_UnemploymentTimeCounter | Single |
| m_SicknessPenalty | Int32 |

**⚠️ 没有 `m_Age` 字段**（与设计文档预期不符）。年龄段编码在 `m_State` 里：`CitizenFlags` 的 `AgeBit1=1` / `AgeBit2=2` 两位拼出 `CitizenAge` 枚举（Child=0, Teen=1, Adult=2, Elderly=3）；精确生日用 `m_BirthDay`（Int16）。`CitizenFlags` 其余位：Male=8、EducationBit1-3、Tourist=512、Commuter=1024、Homeless=16384 等——**读侧可直接分辨游客/通勤者/无家可归者/教育等级**。

### Game.Citizens.Household —— struct，IComponentData

m_Flags(HouseholdFlags)、m_Resources(Int32，家庭资金)、m_ConsumptionPerDay(Int16)、m_ShoppedValuePerDay/m_ShoppedValueLastDay(UInt32)、m_LastDayFrameIndex(UInt32)、m_SalaryLastDay(Int32)、m_MoneySpendOnBuildingLevelingLastDay(Int32)。

### Game.Citizens.HouseholdMember —— struct，IComponentData

仅 m_Household(Entity)。

### Game.Citizens.Worker —— struct，IComponentData

m_Workplace(Entity)、m_LastCommuteTime(Single)、m_Level(Byte)、m_Shift(Game.Companies.Workshift)。

### Game.Citizens.Student —— struct，IComponentData

m_School(Entity)、m_LastCommuteTime(Single)、m_Level(Byte)。

### Game.Citizens.TravelPurpose —— struct，IComponentData

m_Purpose(Game.Citizens.Purpose)、m_Data(Int32)、m_Resource(Game.Economy.Resource)。

### Game.Citizens.TripNeeded —— struct，**IBufferElementData**（注入点是 DynamicBuffer）

| 字段 | 类型 |
|---|---|
| m_TargetAgent | Entity |
| m_Purpose | Game.Citizens.Purpose |
| m_Data | Int32 |
| m_Resource | Game.Economy.Resource |
| m_Priority | **Byte**（0-255） |

`Purpose` 枚举共 41 个值，与 CimLife 相关的：Shopping=1、Leisure=2、GoingHome=3、GoingToWork=4、Sightseeing=32、VisitAttractions=33、Hospital=12、Crime=17。设计文档烟花节例子 `TripNeeded{Leisure, priority=128}` 类型上成立（128 在 Byte 范围内）；**能否抢占上班调度仍需 M4 实测**（元数据无法回答）。

**对 CimLife 的用途判断**：Citizen/HouseholdMember/Worker/Student 覆盖 M1 读侧市民画像（幸福感 m_WellBeing、健康 m_Health、通勤时间、失业计数全是现成的话题素材）；TripNeeded 是 M6 "聚集人群"原语的核心注入点，字段结构与 Time2Work 用法一致；Household 的 m_Resources/m_SalaryLastDay 可支撑"工资/房租"类话题。

## 2. 公司（Game.Companies）

### Game.Companies.Profitability —— struct，IComponentData ✅ 公司利润组件确认存在

| 字段 | 类型 |
|---|---|
| m_Profitability | Byte（盈利评分，UI 直接展示的值） |
| m_LastTotalWorth | Int32 |

其余 Profitability* 类型：`Game.Serialization.DataMigration.CompanyAndCargoFixSystem+ProfitabilityFixJob`（存档迁移 job）、`Game.UI.InGame.ProfitabilitySection`（**选中建筑信息面板的盈利板块，内含 ProfitabilityJob，逐个 lookup 了 Employee/Renter/Efficiency/TaxRates 等——它本身就是一份"盈利因子怎么算"的官方清单**，其 m_Results NativeArray&lt;int&gt; 与 m_Factors NativeArray&lt;int2&gt; 表明游戏把盈利拆成有序因子对）。

### Game.Companies.CommercialCompany —— struct，IComponentData

**无任何字段**（纯标记组件）。

### Game.Companies.WorkProvider —— struct，IComponentData

m_MaxWorkers(Int32)、m_UneducatedCooldown/m_EducatedCooldown(Int16)、m_UneducatedNotificationEntity/m_EducatedNotificationEntity(Entity)、m_EfficiencyCooldown(Int16)。注意：只有编制上限，**当前在岗人数要数 Employee buffer**。

### Game.Companies.Employee —— struct，IBufferElementData

m_Worker(Entity)、m_Level(Byte)。

**对 CimLife 的用途判断**：M0 spike 项"公司利润组件"**已关闭**——M1 可直接读 `Profitability.m_Profitability`（0-255）做"商家经营"话题，无需退到"岗位空缺率+客流"代理（代理仍可作为补充：`WorkProvider.m_MaxWorkers` vs Employee buffer 长度 = 空缺率）。CommercialCompany 空标记 + Employee buffer 可用于识别商家与统计员工。M6 "商家客流微升"原语没有直接字段可写，仍需走吸引力/TripNeeded 注入路线。

## 3. 事件/事故（Game.Events）

### Game.Events.AccidentSite —— struct，IComponentData

m_Event(Entity)、m_PoliceRequest(Entity)、m_Flags(AccidentSiteFlags)、m_CreationFrame(UInt32)、m_SecuredFrame(UInt32)。
`AccidentSiteFlags`（UInt32）：StageAccident=1、Secured=2、CrimeScene=4、TrafficAccident=8、CrimeFinished=16、CrimeDetected=32、CrimeMonitored=64、RequirePolice=128、MovingVehicles=256。

### Game.Simulation.AccidentSiteSystem —— class : GameSystemBase

公开方法仅 `int GetUpdateInterval(SystemUpdatePhase phase)`（逻辑全在私有 job，写侧别碰）。

### Game.Citizens.CrimeVictim —— struct，IComponentData

仅 m_Effect(Byte)。另发现 `Game.Events.Crime`（空标记组件）、`Game.Citizens.Criminal`（m_Event/m_JailTime/m_Flags）、`Game.Prefabs.Crime`（prefab 参数：发生率/持续时间/赃款范围等 Bounds1）。

### Game.Events 命名空间概览（85 个类型名，节略见 dump.txt）

事件实体组件：Event、Fire、Flood、TrafficAccident、LightningStrike、WaterLevelChange、WeatherPhenomenon、HealthEvent、Crime、SpectatorEvent、CalendarEvent、JournalEvent；状态/标记：InDanger、OnFire、Flooded、InvolvedInAccident、AttendingEvent、FindingEventParticipants；系统：AddAccidentSiteSystem、AddCriminalSystem、AddHealthProblemSystem、EndangerSystem、IgniteSystem、SubmergeSystem、SpectateSystem、EventJournalSystem、EventJournalInitializeSystem、ImpactSystem；日记缓冲：EventJournalEntry/EventJournalData/EventJournalCityEffect/EventJournalPending/EventJournalCompleted；接口 **IEventJournalSystem**。

### Game.Events.EventJournalSystem —— class : GameSystemBase

公开方法：
- `EventJournalEntry GetInfo(Entity journalEntity)`
- `Entity GetPrefab(Entity journalEntity)`
- `bool TryGetData(Entity journalEntity, ref DynamicBuffer<EventJournalData> data)`
- `bool TryGetCityEffects(Entity journalEntity, ref DynamicBuffer<EventJournalCityEffect> data)`

公开属性/事件：`NativeList<Entity> eventJournal`、`IEnumerable<JournalEventComponent> eventPrefabs`、`Action eventEntryAdded`、`Action<Entity> eventEventDataChanged`——**有新增日记条目的事件回调，读侧零轮询**。

### Game.Events.EventJournalEntry —— struct，IComponentData

m_Event(Entity)、m_StartFrame(UInt32)。

### Game.Events.EventJournalData —— struct，IBufferElementData

m_Type(EventDataTrackingType)、m_Value(Int32)（如死亡人数/损失金额等按 Type 解释）。

**对 CimLife 的用途判断**：设计文档"城市事件日志直接当新闻源"**完全成立且超出预期**——`eventJournal` 列表 + `eventEntryAdded` 回调 + `GetInfo/TryGetData` 三件套就是 M1 新闻话题源的官方 API；AccidentSite 的 flags 还能区分交通事故/犯罪现场供"突发"板块使用；`Game.Events.Crime` 实体可检测犯罪事件。M6 "环境走间接、Game.Events 原生事件"路线也因此有了明确的类型抓手。

## 4. 旅游

### Game.Simulation.TouristSpawnSystem —— class : GameSystemBase

私有字段：m_HouseholdPrefabQuery / m_OutsideConnectionQuery / m_AttractivenessParameterQuery / m_DemandParameterQuery（EntityQuery）、m_EndBarrier、SimulationSystem/ClimateSystem/CitySystem/CityStatisticsSystem 引用。公开方法仅 `int GetUpdateInterval(SystemUpdatePhase)`。

### Game.Citizens.TouristHousehold —— struct，IComponentData

m_Hotel(Entity)、m_LeavingTime(UInt32)——**游客-酒店关联与离店时间可直接读**。

### Game.Simulation.TourismSystem —— class : GameSystemBase

公开方法（这批是写回原语的金矿）：
- `int GetTouristRandomStay()`
- `float GetRawTouristProbability(int attractiveness)`
- `int GetTargetTourists(int attractiveness)`
- `float GetSpawnProbability(int attractiveness, int currentTourists)`
- `float GetTouristProbability(AttractivenessParameterData, int attractiveness, int numberOfCurrentTourists, WeatherClassification, float temperature, float precipitation, bool isRaining, bool isSnowing)`
- `float GetWeatherEffect(AttractivenessParameterData, WeatherClassification, float temperature, float precipitation, bool isRaining, bool isSnowing)`

私有字段含 m_CachedLodging(int2)、CountHouseholdDataSystem 引用、m_AttractivenessProviderGroup / m_HotelGroup 查询。

### Lodging 相关

- `Game.Citizens.LodgingSeeker`：空标记组件
- `Game.Companies.LodgingProvider`：m_FreeRooms(Int32)、m_Price(Int32)——**酒店空房率/房价可读**
- `Game.Simulation.LodgingProviderSystem`：GameSystemBase，`static int kUpdatesPerDay`

### Attractiveness —— ⚠️ 不存在叫 "Attractiveness" 的独立类型

实际等价物：
- `Game.Buildings.AttractivenessProvider`：struct IComponentData，**m_Attractiveness(Int32)** ——建筑吸引力数值就在这里
- `Game.Simulation.AttractionSystem`：GameSystemBase，公开 `SetFactor(NativeArray<int> factors, AttractivenessFactor factor, float attractiveness)`；其嵌套枚举 AttractivenessFactor：Efficiency=0、Maintenance=1、Forest=2、Beach=3、Height=4
- `Game.Simulation.TerrainAttractiveness`：struct，m_ShoreBonus/m_ForestBonus(Single)；`TerrainAttractivenessSystem : CellMapSystem<TerrainAttractiveness>`（地形吸引力 CellMap）
- `Game.Prefabs.AttractivenessParameterData`：prefab 参数（森林/海岸/高度/温度/雨雪效应的全部系数）——**写回"景区吸引力微调"的 prefab 参数抓手**

**对 CimLife 的用途判断**：M6 "旅游口碑循环"**可行性大幅证实**——`TourismSystem.GetSpawnProbability(attractiveness, currentTourists)` 直接给出"吸引力→游客 spawn 概率"的函数签名，写回原语调吸引力（AttractivenessProvider.m_Attractiveness 或 prefab 参数）即可闭环，无需逆推公式；`TouristHousehold.m_Hotel` + `LodgingProvider.m_FreeRooms/m_Price` 支撑"游客住店/酒店满房"话题；天气对游客概率的影响原版就有（GetWeatherEffect），"下雨冷场"剧本有模拟依据。Time2Work 式 TouristSpawnSystem 介入路线依旧可用。

## 5. 外部连接/交通

### OutsideConnection —— 三个同名类型

- `Game.Net.OutsideConnection`：struct，m_Delay(Single)
- `Game.Objects.OutsideConnection`：struct，**空标记**
- `Game.Prefabs.OutsideConnection`：class ComponentBase，m_TradedResources(ResourceInEditor[])、m_Commuting(Boolean)、m_TransferType(OutsideConnectionTransferType)、m_Remoteness(Single)

### TrafficSpawner —— 两个同名类型

- `Game.Buildings.TrafficSpawner`：struct IComponentData，m_TrafficRequest(Entity)；实现 Serialize/Deserialize
- `Game.Prefabs.TrafficSpawner`：class ComponentBase，m_RoadType(RoadTypes)、m_TrackType(TrackTypes)、**m_SpawnRate(Single)**、m_NoSlowVehicles(Boolean)

### Game.Simulation.RandomTrafficRequest —— struct，IComponentData

| 字段 | 类型 |
|---|---|
| m_Target | Entity |
| m_RoadType | Game.Net.RoadTypes |
| m_TrackType | Game.Net.TrackTypes |
| m_EnergyTypes | Game.Vehicles.EnergyTypes |
| m_SizeClass | Game.Vehicles.SizeClass |
| m_Flags | Game.Simulation.RandomTrafficRequestFlags |

`RandomTrafficRequestFlags`（Byte）：NoSlowVehicles=1、DeliveryTruck=2、TransportVehicle=4。注意**没有数量/概率字段**——一辆车一个请求实体，"交通注入量"靠请求个数控制。

**对 CimLife 的用途判断**：M6 "交通注入（RandomTrafficRequest）"原语字段齐全、目标实体明确，可行；注入频率控制也可走 `Game.Prefabs.TrafficSpawner.m_SpawnRate`（prefab 参数路线，设计文档偏好这种稳的写法）。OutsideConnection 三件套用于定位城口实体（注入的 m_Target）。

## 6. 聚合（Game.Simulation.CountHouseholdDataSystem）

class : GameSystemBase。公开方法：

- `HouseholdData GetHouseholdCountData()`（嵌套 struct）
- `NativeArray<int> GetResourceNeeds(ref JobHandle deps)`
- `NativeArray<int> GetEmployables(ref JobHandle deps)`
- `bool IsCountDataNotReady()`
- `void AddHouseholdDataReader(JobHandle reader)`
- `int GetUpdateInterval(SystemUpdatePhase)` + Serialize/Deserialize/SetDefaults

公开属性（全部 get-only）：MovingInHouseholdCount、MovingInCitizenCount、MovingAwayHouseholdCount、CommuterHouseholdCount、**TouristCitizenCount**、HomelessHouseholdCount、HomelessCitizenCount、MovedInHouseholdCount、MovedInCitizenCount、ChildrenCount、AdultCount、TeenCount、SeniorCount、StudentCount、UneducatedCount、PoorlyEducatedCount、EducatedCount、WellEducatedCount、HighlyEducatedCount、WorkableCitizenCount、CityWorkerCount、DeadCitizenCount、**AverageCitizenHappiness**、**AverageCitizenHealth**、**UnemploymentRate(Single)**、**HomelessnessRate(Single)**。

**对 CimLife 的用途判断**：M1 城市级聚合读侧一站式解决——市民平均幸福度/健康/失业率/无家可归率/年龄结构/游客数全有现成属性，是"情绪配比跟随真实城市状态"（§6 内容安全）和每日城市播报话题的直接数据源。注意 `AddHouseholdDataReader` 模式：读后要用它登记 JobHandle。

## 7. 气候（Game.Simulation.ClimateSystem）

class : GameSystemBase。公开属性（取当前天气全靠它们）：
- `OverridableProperty<float> temperature / precipitation / cloudiness / aurora / fog`（get）
- `float2 wind`、`float hail / rainbow / aerosolDensity`
- `bool isRaining / isSnowing / isPrecipitating`（get）
- `WeatherClassification classification`（get/set，嵌套枚举）
- `Entity currentClimate`、`Entity currentSeason`、`string currentSeasonName`
- `float averageTemperature / freezingTemperature / seasonTemperature / seasonPrecipitation / seasonCloudiness`
- `OverridableProperty<float> currentDate`

公开方法：`ClimateSample SampleClimate(Climate.ClimatePrefab, float t)` / `SampleClimate(float t)` + 序列化/PatchReferences 一组。

**对 CimLife 的用途判断**：M1 天气读取零障碍——`temperature/precipitation/isRaining/isSnowing/classification/currentSeasonName` 全覆盖；"暴雨吐槽""初雪""极光"等天气话题有确定数据源；`OverridableProperty` 还暗示天气本身可被覆盖（mod 友好），但 CimLife 暂无改天气的需求。

## 8. 结论

### 8.1 由此证实的设计假设

1. **公司利润组件存在**（设计文档 M0 spike 项② → 关闭）：`Game.Companies.Profitability{m_Profitability:Byte, m_LastTotalWorth:Int32}`，M1 直读，无需代理方案。
2. **TripNeeded 注入点成立**：`Game.Citizens.TripNeeded` 是 DynamicBuffer 元素，字段含 `m_Purpose(Purpose)` + `m_Priority(Byte)`，与设计文档烟花节示例 `priority=128` 类型兼容（抢占上班调度的行为仍需 M4 实测）。
3. **事件日志新闻源成立且更优**：`EventJournalSystem.eventJournal + eventEntryAdded 回调 + GetInfo/TryGetData` 是官方读取 API，读侧可零轮询。
4. **旅游口碑循环可行性高**：`TourismSystem.GetSpawnProbability/GetTargetTourists/GetWeatherEffect` 公开了吸引力→游客 spawn 的完整计算入口；`AttractivenessProvider.m_Attractiveness` 与 `AttractivenessParameterData` prefab 参数是写回抓手。
5. **RandomTrafficRequest 交通注入字段齐全**（m_Target/RoadType/TrackType/SizeClass/Flags），另有 `TrafficSpawner.m_SpawnRate` prefab 参数稳态路线。
6. **城市聚合读侧超预期**：CountHouseholdDataSystem 直接给平均幸福度/失业率/无家可归率/游客数等 30+ 属性。
7. **天气读取零障碍**：ClimateSystem 全套只读属性 + isRaining/isSnowing/classification。

### 8.2 未找到 / 与预期不符（需备选方案或修正文档）

1. **`Citizen.m_Age` 不存在**——年龄段在 `Citizen.m_State` 的 `CitizenFlags.AgeBit1/AgeBit2`（2bit → CitizenAge 枚举），精确生日用 `m_BirthDay:Int16`。设计文档 §4 的 m_Age 写法需修正。
2. **没有名为 `Attractiveness` 的组件**——实际是 `Game.Buildings.AttractivenessProvider{m_Attractiveness:Int32}` + `Game.Simulation.AttractionSystem`（及其 SetFactor）+ TerrainAttractiveness CellMap 三件套。
3. **`AccidentSiteSystem` 在 Game.Simulation 命名空间**（不在 Game.Events），公开面只有 GetUpdateInterval——事故细节只能从组件读，系统不可介入。
4. **`CommercialCompany` 是空标记组件**——商家"营收"没有专门字段，只能靠 Profitability + Household 式 buffer（Renter/Resources）间接看。
5. **`RandomTrafficRequest` 无数量/概率字段**——注入规模=请求实体个数，M4 压测时按个数分批。
6. **`EventJournalCityEffect` 等部分类型只确认了存在性**，字段未展开（本 spike 未涉及，不影响结论）。

### 8.3 Game.dll TargetFramework → mod csproj 决策

- Game.dll 无 TargetFrameworkAttribute，但**引用 netstandard 2.1.0.0**。
- **结论：CimLife mod csproj `<TargetFramework>netstandard2.1</TargetFramework>`**（与官方 mod 模板及 Traffic/Time2Work 等社区 mod 一致）；LangVersion 可开较新（编译器层面），运行时 API 面以 netstandard2.1 为准。
- 工具链：本机 SDK 8.0.419 可正常构建 netstandard2.1 目标。

### 8.4 复现方式

```
cd tools/GameDllDump
dotnet run -c Release            # 默认路径已内置；其他机器用 CITIES2_PATH 环境变量覆盖
```
