# Spike 报告：≥50 栋建筑普查 dump + 事件监听口探针（#62 场所名分级 / #64 城市记忆层 / #65 玩家动作反应层 的收口工具）

> 日期：2026-09-14 ｜ 对象：Game.dll（只读反编译，ilspycmd 8.2.0.7535，`DOTNET_ROLL_FORWARD=LatestMajor` 绕过本机无 .NET 6 运行时问题）+ 游戏内实机 dump（§4 已全收口：两轮 Ctrl+6 存档 `logs/census-spike-20260914/player-dump-20260914.log`，集合差集探针复跑存档 `logs/census-spike-20260914/player-probe-20260914.log`）
> Game.dll 版本：`Cities2_Data\Managed\Game.dll`，12,055,552 字节，文件时间 2026-06-29；Steam buildid **23700737**（appmanifest_949230.acf）；Unity 引擎 2022.3.71f1（与 2026-09-11 address-spike 同版，本报告头部照抄并当日复核一致）
> 反编译产物存档：`logs/census-spike-20260914/`（普查字段）+ `logs/event-watch-spike-20260914/`（事件监听口）
> 收口工具：`src/CityLife/GameBridge/CensusSpikeSystem.cs`（Ctrl+6 普查 dump / Ctrl+7 事件探针开关）
> 结论先行：**组件层路径全部钉死**（§1 实锤表）；**事件监听口语义实锤**——Created/Deleted 只活一帧、EventJournal 只跟 EventPrefab（建造拆除走它无用）、灾难规模=受害实体 m_Event 回指针计数（§3）。§4 七项**全部收口**（当日三轮实机：两轮 Ctrl+6 普查 64+64 栋 + Ctrl+7 探针三窗，末窗为集合差集法复跑 22:29-22:33）：**①②⑦ 实锤、③ 产出读法实锤（标记分布无样本）、④⑥ 证伪、⑤ 实锤**。监听口路线曲折全记录在 §3.3/§4.5：标记直读相位错位实锤 → 探针改**集合差集法** → 复跑正样本全检出，**#65 监听口选型定案=集合差集法**（附三条实操约束：修路预览防抖/自长过滤/树 diff 成本，§3.3）；**Owner 判别证伪**（玩家手种树同样带 Owner，§4.5）；**#62 小修复查通过、零误伤**（自长建筑渲染名 100% 为 zone 通用名样式，非自长 100% 为正常 prefab 本地化名，无 Assets. 脏键漏网进建筑名）。

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
- **EventJournal 对建造拆除无用**：其 prefab 源查询只有 `EventPrefab + PrefabData`（EventJournalSystem.cs:503）——入刊的只有灾难类事件预制体，建筑/道路不是 EventPrefab，建造拆除永远不进 journal。
- **实机实锤（2026-09-14 两轮零命中，相位错位）**：初版探针=Created/Deleted 标记直读、跑 GameSimulation 相位 interval=1。玩家 21:40:09 开探针，21:40:15-55 建造工具活跃建 17 段路（[Change] 快讯 21:41:00 实锤"新修了 17 段路"），探针**零命中**，两轮皆然。根因：`SystemUpdatePhase` 枚举（定向反编译）含 ApplyTool 且 **Cleanup 排最末**——玩家放置经 Apply 相位落地挂 Created/Deleted，Cleanup 相位帧末清标记，而 GameSimulation 在 Apply **之前**更新、下一帧标记已被清 → GameSimulation 相位的 watcher **永远看不到玩家放置的标记**。
- **#65 监听口选型结论（定案）**：**集合差集法**——相位无关，快照三类实体集合、每帧差集报新增/消失，GameSimulation 相位 interval=1 即可。已实装进 Ctrl+7 探针并**实机复跑收口**（2026-09-14 22:29-22:33，正样本全检出，证据见 §4.5）；原"Created 标记直读"路线因相位错位实锤弃用。
- **#65 实操约束（复跑实锤，写进选型）**：
  - **修路预览必须防抖**：修路工具拖动预览/曲线调整产生真实实体churn——本轮窗口新路 86 段/拆路 86 段，实体号 **86/86 全配对**（同段先增后消，存活 0.4s~45s，如 232134:17 于 22:29:44 新增、22:29:46 消失；118444:27 仅活 0.4s），两段基准快照路数 3417→3417 净零。反应层必须"落地稳定 N 秒才算数"，且注意长拖动会有 45 秒级存活的假落地段。
  - **自长过滤**：成熟城市自长速率慢、且以突发形式到达——本轮 6 栋分区自长住宅在修路完工后 3 秒内连发（22:31:59-22:32:02，新临街面触发）。#65 只收玩家动作（`自长=否`）即可天然过滤掉这批噪音。
  - **树 diff 成本**：树基数 23.5 万/城（基准快照 235070），本轮 4.5 分钟窗口 churn = 新增 278 棵/消失 268 棵。全量树差集只配探针用，正式 watcher 要么不做树 diff，要么限半径/降频。

### 3.4 美化（玩家种树/庭院树）

- 玩家种树 = `Game.Objects.Tree + Created`（⚠️ 同样受 §3.3 相位错位约束：GameSimulation 相位看不到，差集法探针打 `有Owner=` 收口）。`Game.Common.Owner { Entity m_Owner }` 组件存在性已补钉实锤（2026-09-14 ilspycmd 定向，ApplyNetSystem/DestroySystem 等多处挂它）。
- "玩家手种的树无 Owner、庭院树有 Owner（挂在建筑/地块下）"的推断**已被实机证伪**（§4.5）：玩家手种 AppleTree01 有Owner=是，手种 SpruceTree01 有Owner=否——Owner 无法区分玩家种 vs 附带/自长树。美化（种树）监听若要区分玩家手种需另找信号（候选方向：新树与路/建筑的空间关系判别，或 Tool 侧信号——本轮未验证，列入开放问题）。

## 4. 未实锤清单 = 本次实机普查收口项（工具用法 + **结果已回填**）

| # | 未实锤项 | 收口手段 | 回填结论（2026-09-14 晚实机） |
|---|---|---|---|
| ① | 各 prefab Localization 挂接面（多少 prefab 挂了、ID 长什么样） | Ctrl+6 每栋行 `loc=` 字段 | **实锤：挂接面 0%**——128/128 栋 `loc=无`，详见 §4.1 |
| ② | household 住户姓字面（GetRenderedLabelName(household) 是姓还是全名/脏键） | Ctrl+6 租户行 `住户姓=` | **实锤：真住户姓**——170/170 全为"X 一家"式本地化姓，详见 §4.2 |
| ③ | extractor/service 公司产出读法（无 IndustrialProcessData 时的标记组件分布） | Ctrl+6 公司租户行 `extractor=/service=/commercial=` | **产出读法实锤（IndustrialProcessData 通吃 34/34）；标记组件分布无样本**（零公司落入该分支），详见 §4.3 |
| ④ | GetRenderedLabelName(company) 是否=品牌名（CompanyData.m_Brand 对照） | Ctrl+6 公司租户行 `名=` vs `品牌=` | **证伪：≠ 品牌名，34/34 全是 Assets.NAME 脏键**；品牌名须走 m_Brand，详见 §4.4 |
| ⑤ | 分区自长建筑是否带 Created（自长 vs 玩家手放的判别是否成立） | Ctrl+7 开探针后等分区自长/手动放建筑，看 `自长=` | **实锤（集合差集法）**：22:29-22:33 复跑，手放 Playground02=自长否、自长住宅 6 栋=自长是，当帧检出且 IsZoneGrown 判别全对，详见 §4.5 |
| ⑥ | 庭院树 Owner 判别（玩家手种树是否无 Owner） | Ctrl+7 开探针后手动种树 vs 分区自长带出的树，`有Owner=` | **证伪**：玩家手种 AppleTree01 有Owner=是、手种 SpruceTree01 有Owner=否——Owner 无法区分玩家种 vs 附带/自长树，详见 §4.5 |
| ⑦ | 可入刊灾难 prefab 清单（eventPrefabs 实际内容） | Ctrl+6 末尾 `可入刊事件 prefab 共 N：` 行 | **实锤：为空**——两轮均 `共 0`，清单体零行、无报错，详见 §4.6 |

### 实机概况（回填数据的口径）

- 存档城市：全城建筑 **2598 栋**，市民 26730；类别分布两轮逐值一致（住宅区=1656 工厂=437 商店=231 未分类=218 公园=43 学校=8 医院=5）。
- 第一轮 Ctrl+6 19:52:00（抽样 64/2598，175 行）→ Ctrl+7 探针开 19:52:10.764 → 第二轮 Ctrl+6 19:55:52（抽样 64/2598，186 行）→ Ctrl+7 探针关 19:55:53.388。
- 探针窗口约 **3 分 43 秒**，窗口内游戏确认在跑（闲聊炉第 1→10 炉连续开炉、BubbleW FPS≈45-50、无 ERR/WARN）；两轮普查之间全城建筑数 2598→2598 零增减。
- 两轮抽样 128 栋仅 2 栋重叠，覆盖 126 个不同建筑实体。
- 原始抽取：`logs/census-spike-20260914/player-dump-20260914.log`（365 行 = 两轮普查 363 行 [普查] + 2 行 [事件探针] 开关行）。

### 4.1 ① prefab Localization 挂接面 —— 实锤：0%

128/128 栋全部 `loc=无`，横跨所有类别（住宅区 85、工厂 22、商店 12、未分类 7、医院 1、公园 1；自长 119 / 非自长 9）无一例外。**本体 prefab 没有一家挂 `Game.Prefabs.Localization` 组件**；而渲染名全是正常中文（北美低密度住宅、小型医疗诊所、回收中心……），说明显示名全部走 §1 #2 的 fallback——PrefabUISystem 拼 `Assets.NAME[prefab名]` 键再查游戏本地化库。**推论**：#62 场所名分级的"prefab 名"档不需要走 Localization 组件，`NameSystem.GetRenderedLabelName` 的解析结果或 prefab 内部名+本地化表即可；`pb.TryGet<Localization>` 这条路在本体资产上可弃用（DLC/mod 资产另说，本次无样本）。

### 4.2 ② household 住户姓 —— 实锤：真住户姓

170/170 住户行全是"X 一家"式本地化真姓，零脏键零乱码零全名。字面实例（照抄）：**汉密尔顿一家、克罗斯比一家、莱德利一家、哈德利一家、兰德里一家、基顿一家、奥尔特加一家、钱伯斯一家、法利一家、霍洛韦一家、莫斯利一家、马利一家、迪亚兹一家、蒙托亚一家、皮尔森一家、约翰逊一家、普莱斯一家、桑莫斯一家、霍普金斯一家**。`GetRenderedLabelName(household)` 可直接当"住户姓"用，无需任何后处理。

### 4.3 ③ extractor/service 公司产出 —— 产出读法实锤，标记分布无样本

34/34 家抽样公司**全部有 IndustrialProcessData**（工具只在无它时才打 `extractor=/service=/commercial=` 标记行——该格式本轮一行未出，标记组件分布无直接观测）。分业态读法：

- **加工类**：正常投入产出，如 `Ore×10→Metals×8`（MetalSmelter）、`Petrochemicals×10+Chemicals×10→Plastics×16`、`Vegetables×10+Livestock×10→Food×16`。
- **开采类**：`Industrial_LivestockExtractor 投入=NoResource×0+NoResource×0 产出=Livestock×20`——**修正 §1 #10 的反编译推断**：`ExtractorCompany.GetPrefabComponents` 虽只挂 ExtractorCompanyData 空标记，但实机上开采公司 prefab 另有 IndustrialProcessData（应来自公司 prefab 基座其他组件），**产出直接可读**，投入恒空即是"从地里/水里长出来"的语义。
- **商业类**：投入=产出=所售商品×1（如 `Textiles×1→Textiles×1`、`Beverages×1→Entertainment×1` 是酒吧把酒水变娱乐），进货转售语义可读。
- **办公类**：虚货产出可读：`Electronics×1→Software×3`、`Electronics×2+Software×1→Telecom×4`、`Software×1→Media×3 / Financial×3`。
- `import=0` 全场。

### 4.4 ④ GetRenderedLabelName(company) —— 证伪：不是品牌名，是 100% 脏键

34/34 公司 `名=` 全是 **`Assets.NAME[prefab内部名]` 未解析键**（中文客户端下 NameSystem 对公司实体不解析该键），如 `名=Assets.NAME[Commercial_FashionStore] prefab=Commercial_FashionStore 品牌=Cheap & Random`。而 `品牌=`（CompanyData.m_Brand → 品牌 prefab 名）全是真品牌：Cheap & Random、Smelt 'n Blast、Lehto Electronics、Maito、Limzabar、Pteropus、Neckbeard、InstaLOD……（英文虚构品牌，中文客户端保持原文）。**#62 公司档定稿：店名取 m_Brand 品牌名，GetRenderedLabelName(company) 不可用作显示名**。附带观测：品牌是"虚构集团"可跨业态复用——Crapfish Granules 同挂 PlasticsStore 与 PlasticsFactory。

### 4.5 ⑤⑥ 定案 —— 集合差集法复跑收口（⑤ 实锤 / ⑥ 证伪）

路线曲折全程：两轮零命中 → 定位相位错位 → 探针重写 → 复跑正样本全检出。

- **第一窗（19:52:10 开 → 19:55:53 关，约 3m43s）**：零命中，窗口内游戏在跑（闲聊炉连开 10 炉、FPS≈45-50）、两轮普查全城 2598→2598 零增减——该窗确无建造活动，属"无正样本"。
- **第二窗（21:40:09 开）**：玩家 21:40:15-55 建造工具活跃建 17 段路（[Change] 快讯 21:41:00 实锤"新修了 17 段路"），探针**仍零命中**——有正样本却看不到，证伪"无活动"解释，定位真根因=**相位错位**（§3.3：GameSimulation 在 ApplyTool 之前、Cleanup 帧末清标记，标记直读在 GameSimulation 相位永远看不到玩家放置）。
- **第三窗（集合差集探针复跑，22:29:02 开 → 22:31:39 关、22:31:54 开 → 22:33:36 关，两段；基准快照 2650 建筑/3417 路/235070 树 → 2651/3417/235085）**：正样本全检出，抽取存档 `logs/census-spike-20260914/player-probe-20260914.log`（458 行）。

第三窗证据明细：

- **⑤ 实锤**：新建建筑 ×7 全部当帧检出且 `自长=` 判别全对——22:29:16 玩家手放 `Playground02`（自长=否 ✓）；22:31:59-22:32:02 分区自长住宅 6 栋（`NA_ResidentialLow01_L1_2x2`/`Low03_L1_2x2`/`Low01_L1_3x2` 等，自长=是 ✓）。拆建筑 0（玩家本轮未拆）。集合差集法对建造/拆除的当帧检出成立，**#65 监听口选型定案**（§3.3）。
- **⑥ 证伪**：新树 278 棵（逐条播报 226 + 超阈值合并 2 批 52）；逐条带 Owner 字段的 226 棵中 **218 有Owner=是 / 8 有Owner=否**。关键反例：玩家手种 `AppleTree01`×2（22:33:31）**有Owner=是**——"手种无 Owner"推断直接证伪；而 8 棵 `有Owner=否` 的 SpruceTree01（22:29:28/22:29:31 两批，紧随 Playground02 放置）是独立树，唯一来源是美化工具手种——即**玩家手种内部"是/否"两态并存**。Owner 判别彻底不可用；区分玩家手种需另找信号（开放问题，§3.4）。附带树侧证据自洽：修路成批附带的 NA_LindenTree01/NA_LondonPlaneTree01 全部 有Owner=是。
- **修路预览噪音实锤**：新路 86 段 / 拆路 86 段（全部 `prefab=Small Road`，拆路打快照存的 prefab 名播报），实体号 **86/86 全配对**——窗口内新增的路段 100% 随后消失（存活 0.4s~45s），两段基准快照路数 3417→3417 净零。整窗路 churn 全是拖动预览/曲线调整的临时段，正式反应层必须防抖（§3.3 实操约束）。
- 树消失 45 批共 268 棵，只打合并计数行（符合设计，防刷屏）；逐条新树中 2 批同帧 >20 条合并（×22/×30）不带 Owner 字段——合并行丢 Owner 细节，工具口径说明。

### 4.6 ⑦ 可入刊事件 prefab 清单 —— 实锤：为空

两轮普查末尾均 `可入刊事件 prefab 共 0：`——清单体零行、无报错。即本存档该时刻 `EventJournalSystem.eventPrefabs` 是**空枚举**：与 §1 #12"源查询只有灾难类 EventPrefab"不矛盾（城市无灾难史/灾难内容未加载时即为空），但给 #64 灾难 watcher 提了个醒：**不能假定清单非空**，要么容忍空清单运行时懒等，要么另找 prefab 源。

### 4.7 #62 小修复查（本轮回填重点）—— 通过，零误伤

- **自长=是 119 栋**：渲染名 **100% 是 zone 通用名样式**——北美低密度住宅 45、北美低密度滨水住宅 12、北美中密度联排住宅 12、工业设施 12、北美低密度商业 9、低密度办公楼 9、北美中密度住宅 8、欧洲中密度联排住宅 4、欧洲中密度住宅 3、北美低密度滨水商业 3、廉租住宅 1、农业区枢纽 1。**无一反例**（无真名/自定义名/脏键）。
- **自长=否 9 栋**：渲染名全是正常 prefab 本地化名——小型医疗诊所、回收中心、摩托停车场×2、小微公园、采集器 水产养殖养鱼场 04/08、石冢 03、农业加工温室 05。**零 Assets. 脏键漏网**（脏键全部出现在公司租户行而非建筑行，属 ④ 的范畴，不在 #62 斩断逻辑覆盖面内）。
- 附带观测：`IsZoneGrown` 会把**农业区枢纽**（IndustrialAgricultureHub02_L3，有 SpawnableBuildingData）与**廉租住宅**也判为自长——其渲染名仍是通用类别名，斩断无害；但今后若要给自长建筑补真名，注意 hub 类也会落入"功能区标签"档。
- **结论：#62 斩断逻辑（自长建筑的 zone 通用名不当店名）在 128 栋样本上零误伤、零漏网，判定成立。**

### 4.8 意外发现

1. **实体侧 Flags 全场同值**：128/128 栋清一色 `实体侧Flags=StreetLightsOff, Illuminated`——连石冢废墟、水产养鱼场都是同值。"路灯关+已亮化"并存且全场一致，疑似工具读法或该时刻全局状态问题，存疑待查（不影响其他字段结论）。
2. **采集器建筑无租户**：ExtractorAquacultureFishFarm04/08、ExtractorAgricultureGreenhouse05 三栋零租户行；开采公司（LivestockExtractor）实际挂在自长的"农业区枢纽"（IndustrialAgricultureHub02_L3）里当租户——开采业的"建筑→公司"挂靠与直觉不同，做业态叙事时别去养鱼场找公司。
3. **`地址=n/a` 恰为 4 栋 = 全部 NoRoadConnection prefab**（石冢 03、养鱼场×2、温室 05）——GetAddress 对无路连接建筑返回空是游戏行为，字段自洽，非工具 bug。
4. **大户型住宅租户规模**：L5 5x5 中密度住宅 75 户、L4 6x6 达 94 户（`…另有 71/90 个租户未打`）——按"≤4 租户/栋"的普查口径会漏掉绝大多数住户，今后做住户层叙事需注意此采样偏差（生产代码无此限制，仅 dump 工具截断）。

### 工具用法（玩家照跑）

1. 部署后进任意**有 ≥50 栋建筑的城市存档**，`tail -f Logs/CityLife.log`（--developerMode 下日志在 `AppData/LocalLow/Colossal Order/Cities Skylines II/Logs/CityLife.log`）。
2. **Ctrl+6 = 普查 dump**（按一次打一轮）：`[普查]` 前缀——开头类别分布行 → ≤64 栋逐栋行（实体号/类别/自长/prefab 名/地块/两套 Flags/loc/渲染名/地址）+ 每栋 ≤4 个租户行 → 末尾可入刊事件 prefab 清单 + 合计行数。
3. **Ctrl+7 = 事件探针开关**（拨开再拨关）：**集合差集法**——开探针帧先快照全城建筑/路/树为基准（打一行基准计数，不报"新增"），开启期间每帧差集，`[事件探针]` 前缀逐条打新建建筑（含 `自长=`）/新路/新树（含 `有Owner=`）/拆建筑/拆路（消失的打快照存的 prefab 名）；树消失只打合并计数行。建议开着探针做一轮正样本操作：手动放一栋建筑、等分区自长、手动种棵树、拆一栋建筑，然后 Ctrl+7 关掉。
4. 单帧同类新增/消失 >20 条自动合并成一行计数（防批量生成时刷屏）。

## 5. 风险与纪律

- **只读不写**：全系统无一处 Add/Set/RemoveComponent，不写回任何模拟数据。
- **主线程低频**：普查 dump 是热键触发的一次性 O(64) 循环（租户 ≤4/栋，一轮几百行内）；GetAddress 只在抽样栋上跑（O(路段数) 警告适用但量级极小）。
- **探针默认关**：m_ProbeOn 默认 false，关闭时不跑任何探针查询、快照清空（零成本）；开启时每帧三查询差集，ToEntityArray/NativeHashSet 用 Allocator.Temp 无残留分配，prefab 名只给新增实体解析。
- **ECS 纪律**：禁用 SystemAPI；查询全部 OnCreate 缓存；GetUpdateInterval=1（2 的幂，热键捕获与每帧差集的双重需要）；查询 exclude Temp/Deleted；HasComponent 先行再 GetComponentData；游戏自建系统（PrefabSystem/NameSystem/EventJournalSystem）惰性解析+判空。
- **退役方法**：验证完即退役——Mod.cs 删 `updateSystem.UpdateAt<GameBridge.CensusSpikeSystem>(...)` 一行即可（登记处注释已注明）。
