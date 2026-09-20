using System;
using System.Collections.Generic;
using Game;
using Game.Citizens;
using Game.Rendering;
using Game.Routes;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 多人小剧场（§12 #52"剧本库存+放送时绑定"，推翻 #48 播前绑定段）：炉→池→放送——剧本炉水位触发
    /// 产库存入 TheaterScriptStock 池，放送时场景+人选成立即从池取匹配剧本绑真名单即时开播。
    /// 执行层确定性管生命周期（选锚点/绑人/播放/终了），LLM 只写剧本库存（架构铁律 1：算得准的全给执行层）。
    /// 为什么推翻播前绑定：绑真名单→等 LLM→到货复核开播，thinking 时代 LLM 延迟分钟级，剧本到货时人散了
    /// →开播中止连发、每中止白烧一炉（2026-09-10 实机实锤）。改抄话题炉/闲聊炉同款"炉→池"缓冲：
    /// 分钟级延迟被池子吸收，放送零 LLM 等待、绑定即复核。
    ///
    /// 状态机（一拍一拍走；供给节拍（评估拍/冷却）恒锚游戏时间——暂停零成本、倍速同速放大（#48；
    /// 2026-09-15 实机修正：不走 #68 墙钟档——墙钟把 3-4-5 游戏分钟放大成现实分钟，剧场 9 分钟零开播实锤）；
    /// 逐句显示时钟才走 §12 #68 选项，挂世界泡 PaceNow）：
    /// ① 剧本炉（水位触发）：每 3-4-5 游戏分钟评估拍（节拍轮换照抄闲聊炉，恒锚游戏时间 #48——供给节奏不走 #68 选项，选项只管逐句显示时钟）查池水位——任一场景标签库存 &lt;2
    ///    （分区水位，2026-09-16 修正注记挂 §12 #67：原"总库存 &lt;6"只认总数，分区归零会饿死该场景）且
    ///    无在飞+网关可用+非 MUTE+设置页开关开 → 发一炉产 4 部（Normal 优先级：thinking 时代低优先级
    ///    队尾等死，§12 #51 实锤；TTL 300s + 墙钟 TTL+60s 兜底解锁，闲聊炉/S3 同款）。prompt=固定头
    ///    PromptBuilder.BuildTheaterStockHead（启动拼一次缓存，逐字节稳定纪律）+ 动态尾：各场景标签
    ///    现存部数 + 缺口分区定向补给 + 城市快照 + 3-5 张处境卡当灵感池（禁写真名/真店名，只写氛围）。
    ///    结果走 ContentDirectorSystem 按 "theater:" 前缀转交 OnTheaterResult（不自己 TryDequeueResult
    ///    ——两个消费者轮询同一队列会互相偷包）；到货只做 JSONL salvage 解析入池。
    /// ② 放送选锚点（零 token 纯执行层，与播前绑定时代同一套扫描）：候选只从 BubbleWorldSpikeSystem
    ///    的可见锚点附近找（采样池天然全是屏内点，按锚点离相机距离升序扫）：
    ///    a) 车站候车——EnvironmentDigestSystem.CollectAround 40m 圈扫 static 树，
    ///       WaitingPassengers.m_Count ≥2（候车是计数不是名单，spike §4）；
    ///    b) 公园/景点——圈内带 AttractivenessProvider / SignatureBuildingData 的建筑；
    ///    c) 店内——市民查询按 CurrentBuilding 分组计数 ≥2（剧场级低频全量扫，帧预算内），
    ///       且建筑须落在某个可见锚点 40m 内（离镜头近优先的落点保证）；
    ///    d) 街头闲谈（§12 #67，见⑦）——可见行人锚点圈内抓步行路人组 roster。
    /// ③ 绑真名单+取剧本（不绑随机路人）：开放场所（车站/公园/街头）= moving 树圈内带 Human+Resident 的
    ///    行人 agent 回指市民（S6 实锤候车行人在树里），按离地点距离升序取 2-4 人（街头场=设置页上限 2-5），
    ///    锚点=agent 本体；店内 = CurrentBuilding==该建筑的市民 2-4 人，锚点=建筑本体（室内市民无 agent，多人共锚）。
    ///    参与者已在他组的跳过。场景标签映射：车站候车→station、公园/景点→park、商店→shop、
    ///    住宅区→home、窗口混编→window（见⑥）、街头闲谈→street（见⑦）；其余室内类型（学校/医院/办公楼等）无标签=让贤。候选成立 →
    ///    best 竞争前先过"池有匹配剧本"闸（让贤机制，2026-09-16 修正注记挂⑦：没剧本的分区不吞拍）→
    ///    stock.TryTake(标签, 名单人数)：场景严格相符+cast≤人数确定性取一条（街头场多一段：
    ///    没有正好演得起的就取大剧本裁着演，见⑦）；炉只由水位触发，放送侧绝不开炉。
    /// ④ 开播：取到剧本即绑名单建剧场——Participants/Lines 初始化其余锚点显"……"（在听）+
    ///    第一句带名字前缀落到说话人锚点（§12 #50："安珀尔：……"——多人/共锚分辨说话人，顺带成
    ///    剧场视觉标识）+ Anchors/m_ByAnchor 登记 + EnsureAnchor 五道闸。<b>剧本开播才消耗</b>：
    ///    任一锚点不可锚=开播中止+剧本 Return 退回池+不上冷却（#52：冷却只在终了后上——中止
    ///    不是地点的锅）；全过=RefreshAnchorText 全锚点立即换文案开播。轮次推进挂气泡换文案节拍：
    ///    BubbleWorldSpikeSystem.TickLifecycle 里<b>当前说话人锚点</b>到时 → OnAnchorRotated → 下一句写到
    ///    其说话人锚点 + RefreshAnchorText 立即换文案（上条读完下条接话）；旁听锚点到期不推轮次——
    ///    严格串行，同剧组同屏只 1 泡（2026-09-16 玩家定案，注记挂⑦）；SetBubbleText 的插队
    ///    分支 TryGetLine 是**纯查询**，绝不推轮次。
    /// ⑤ 终了：剧本播完 / 任一参与者实体失效 / 任一锚点离屏（气泡系统 Resample 裁掉即检测不到）
    ///    / 地点实体失效（车站被拆）——锚点失效即锁（#28）：终了即给地点上冷却 ≈3 炉节拍防连开；
    ///    播过的剧本即弃不退池。
    /// ⑥ 窗口混编（§12 #56，2026-09-11 定案，新形态排序第一）：从可见行人锚点（kind=0）出发，
    ///    20m 静树查最近一栋"有人气"的商店/住宅（其余业态/车站/公园跳过——复用 IndoorSceneTag
    ///    室内分类映射当白名单）+ 屋内 ≥1 个空闲市民（CurrentBuilding==该楼、不在组）→ 1 外 1 内
    ///    混编名单（路人锚行人 agent kind=0、屋里人锚建筑本体 kind=2——Theater 混合锚点架构现成），
    ///    地点=建筑（冷却/占用判它），标签 window，cast=2/lines=2 短剧（1 轮，路人不逗留；schema
    ///    恒值校验在 TheaterScriptStock）。剧本炉每炉至少配 1 部 window（最高频场景，库存没了满街
    ///    没得演）。窗口候选与室外/店内候选同台竞争（DistToCam 近者优先，沿用 best 比较），评估拍
    ///    节奏/同屏 ≤2/冷却不变，开播/播放/终了全部复用现有机制零改动。
    /// ⑦ 街头闲谈（§12 #67，2026-09-15 定案，#63 形态升级——快轨单泡对话卡不动，两形态并存）：
    ///    路上闲谈可变人变长。候选=从可见行人锚点出发圈内抓步行路人（BindOutdoorRoster 同款口径，
    ///    上限=设置页"街头短剧最大人数"2-5 默认 3，现场不足 2 人不开演）；地点=出发锚点本体
    ///    （街头无固定场所——冷却/占用判它，锚点走远失效自然终了="人散"）；标签 street
    ///    （剧本 cast 2-5/lines 2-12 可变，槽位化照旧；剧本炉每炉至少配 1 部 street——
    ///    六场景一炉 4 部装不下，不配额就永远排不上，PromptEval 首轮 street=0 实锤）。向下取不硬凑：剧本 cast>有效人数时取前 N 名
    ///    小演+丢 speaker 越界台词（丢完不足 2 句弃播退回池）、播放句数截到设置页"最长句数"短演
    ///    （2-12 默认 6）。权重略低（DistToCam ×1.5）防遍地行人锚点挤掉车站/窗口场；放送闸/冷却/
    ///    终了全部复用现有机制（名额账按 2026-09-16 修正注记：剧场泡计入同屏上限但必留）。儿童不特别排除（§12 #66 末段）。
    ///
    /// 上限与降级：同屏活剧场 ≤2（k_MaxActive）；同时在飞最多一炉剧本炉；池空/供给不可用=不开
    /// （不致命，单人吐槽管道照常）。只在评估/收炉低频点查树与扫市民，不进任何每帧/渲染热路径；
    /// 只读模拟数据不写。开关闸（§12 #49）：设置页"气泡小剧场"独立开关同时闸住剧本炉与放送——关=零 token 也零播出。
    /// 日志纪律 `[剧场]`：剧本炉开炉（池存数）、剧本入库（N 部池存 M）、开播（地点+人数+名单）、
    /// 终了（原因+播到第几句）、开播中止（原因+退回池）各一行。
    /// </summary>
    public partial class BubbleTheaterSystem : GameSystemBase
    {
        private const double k_ForgeTtl = 480;   // 在飞请求 TTL（秒）：低频补给宁缺毋滥（2026-09-20 双轨定轨慢轨=v4-pro+high 130-221s/炉+排队，300 会误杀，抬 480）
        private const int k_MaxActive = 2;       // 同屏活剧场 ≤2（#48 上限定案）
        private const int k_MinParticipants = 2; // 一组 2-4 人（#48/M5）；街头闲谈上限=设置页（§12 #67，2-5 默认 3）
        private const int k_MaxParticipants = 4;
        private const int k_MinLinesTheater = 2; // 有效剧本最少句数（<2 句不成对话；与 TheaterScriptStock 解析口径一致）
        private const float k_ScanRadius = 40f;  // 选锚点扫描半径（环境圈同尺，S6 定值）
        private const float k_BindRadius = 30f;  // 绑名单半径：参与者须离地点 30m 内（"共位"）
        private const float k_WindowScanRadius = 20f; // 窗口混编找楼半径（§12 #56 定 15-20m，取上沿：树里是楼心不是门脸，临街楼心离人行道常超 15m）
        private const float k_StreetScanRadius = 30f; // 街头闲谈抓人半径（§12 #67：与 k_BindRadius 同尺——能搭上话的距离）
        private const float k_StreetScorePenalty = 1.5f; // 街头场权重折扣（§12 #67"与 window 相当或略低"：行人锚点遍地都是，不压会挤掉车站/窗口场）
        private const int k_MaxAnchorScan = 8;   // 每拍最多扫几个可见锚点（帧预算闸）
        private const int k_MinWaiting = 2;      // 车站候车人气门槛（#48"≥2 人"）
        private const uint k_CooldownMinutes = 12; // 锚点冷却 ≈3 炉节拍（3-4-5 分钟轮换 ×3），防连开
        private const int k_SceneLowWater = 2;   // 分区水位下限：任一场景标签库存 <2 即触发剧本炉（2026-09-16 修正，
                                                 // 注记挂 §12 #67——原"总库存 <6"只认总数，分区归零饿死 street 实锤）
        private const int k_ForgeBatch = 4;      // 一炉产几部剧本（§12 #52）

        private EntityQuery m_CitizenQuery = default!;
        private EntityQuery m_IndoorQuery = default!;
        private TopicRadarSystem m_Radar = default!;
        private CitizenPoolSystem m_CitizenPool = default!; // 剧本炉灵感池：处境卡抽样（只借氛围）
        private SimulationSystem m_SimulationSystem = default!;
        private EnvironmentDigestSystem m_Environment = default!; // S6 半径收集共用口
        private CameraUpdateSystem m_CameraUpdate = default!;
        private BubbleWorldSpikeSystem? m_BubbleWorld;  // 惰性：主菜单世界可能不存在
        private Game.UI.NameSystem? m_NameSystem;       // 惰性：真名（CitizenPoolSystem 同款先例）

        private readonly Content.TheaterScriptStock m_Stock = new(); // 剧本池（§12 #52：炉→池→放送的池）
        private string m_Head = "";
        private uint m_EvalCount;              // 评估拍计数：3-4-5 分钟轮换的锚
        private uint m_NextEvalAt;             // 下一评估拍时刻（游戏 tick；供给节奏恒锚游戏时间 #48，不走 #68 选项）
        private bool m_ClockInitialized;
        private uint m_ForgeCount;             // 剧本炉计数：灵感卡抽样的锚 / requestId 后缀
        private bool m_ForgePending;           // 在飞标志（同时在飞最多一炉）
        private DateTime m_ForgeSince;         // 发炉墙钟（UTC）：网关过期丢弃不回包，TTL+60s 兜底解锁

        private readonly List<Theater> m_Active = new();  // 活剧场（≤k_MaxActive）
        private readonly Dictionary<Entity, Theater> m_ByAnchor = new(); // 锚点→剧场（插队查询主键）
        private readonly Dictionary<Entity, uint> m_CooldownUntil = new(); // 地点→解禁时刻（游戏 tick，#48 供给节奏）
        private readonly List<(Entity Anchor, byte Kind, float3 Pos)> m_AnchorSnap = new(); // 可见锚点快照（复用）

        /// <summary>一名参与者：真名单市民 + 气泡锚点（室外=行人 agent，室内=建筑本体）+ 显示名（日志/台词前缀用）。</summary>
        private sealed class Participant
        {
            public Entity Citizen;
            public Entity Anchor;
            public string Name = "";
            public bool Indoor; // true=锚点是建筑（多人共锚）；false=锚点是行人 agent
        }

        /// <summary>一个活剧场：名单 + 剧本队列 + 当前各锚点在显的台词。
        /// 严格串行（2026-09-16 玩家定案，注记挂 §12 #67）：同一时刻一个剧组只有一泡在屏上——
        /// 只有 CurrentAnchor（当前说话人锚点）的 Lines 是真实台词，其余参与者恒"……"（隐身）；
        /// 上一句 HoldFor 播完才接下一句，接话瞬间上一句立即回"……"让位。</summary>
        private sealed class Theater
        {
            public Entity Location;
            public string SceneName = "";
            public readonly List<Participant> Participants = new();
            public readonly List<(int Speaker, string Text)> Script = new(); // Speaker=0 基参与者序号
            public int Cursor;              // 下一句待播下标
            public Entity CurrentAnchor;    // 当前说话人锚点（严格串行：只有它出泡；共锚=恒为那栋建筑）
            public bool Finished;           // 播完标记（OnAnchorRotated 置位，OnUpdate 收尾）
            public readonly List<Entity> Anchors = new();            // 去重后的锚点实体
            public readonly Dictionary<Entity, string> Lines = new(); // 锚点→当前台词（共锚=最新一句；非当前说话人恒"……"）
        }

        /// <summary>选锚点候选：地点实体 + 已绑名单（≥2 人才算候选成立）+ 剧本场景标签（取件主键）。</summary>
        private sealed class Candidate
        {
            public Entity Location;
            public float3 Pos;
            public string KindLabel = "";
            public string SceneTag = "";     // 剧本池场景标签（station/park/shop/home/window/street）；""=无标签（本拍不可用）
            public float DistToCam;
            public readonly List<(Entity Citizen, Entity Anchor, bool Indoor)> Roster = new();
        }

        /// <summary>楼内市民分组（店内候选与窗口混编⑥共用，BuildIndoorGroups 每评估拍最多建一次）：
        /// 建筑 → 楼内市民（名单封顶 k_MaxParticipants，计数全记）。</summary>
        private sealed class IndoorGroup
        {
            public int Count;
            public readonly List<Entity> Members = new(k_MaxParticipants);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            // 空城/主菜单闸（与各读侧系统同口径）：无市民则整系统静默
            m_CitizenQuery = GetEntityQuery(
                ComponentType.ReadOnly<Citizen>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            // 店内候选专用：只扫"在建筑内"的市民（CurrentBuilding 仅室内挂，spike §1——查询天然滤掉在路上的）
            m_IndoorQuery = GetEntityQuery(
                ComponentType.ReadOnly<Citizen>(),
                ComponentType.ReadOnly<CurrentBuilding>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            RequireForUpdate(m_CitizenQuery);
            m_Radar = World.GetOrCreateSystemManaged<TopicRadarSystem>();
            m_CitizenPool = World.GetOrCreateSystemManaged<CitizenPoolSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Environment = World.GetOrCreateSystemManaged<EnvironmentDigestSystem>();
            m_CameraUpdate = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            m_Head = Content.PromptBuilder.BuildTheaterStockHead(); // 固定头拼一次缓存复用（逐字节稳定纪律）
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 128; // 2 的幂；只是节拍检查粒度

        private uint Now => (uint)m_SimulationSystem.frameIndex;

        // 游戏分钟 tick 折算（闲聊炉/EventNewsSystem 同款惯例）
        private static uint TicksPerHour => (uint)Math.Max(1, TimeSystem.kTicksPerDay / 24);
        private static uint TicksPerMinute => Math.Max(1u, TicksPerHour / 60u);

        protected override void OnUpdate()
        {
            // 评估拍/地点冷却恒锚游戏时间（#48 供给节奏：暂停零成本、倍速同速放大）——不随 #68 选项：
            // 选项只管"阅读舒适度"时钟（世界泡 HoldFor/剧场换句显示，挂世界泡 PaceNow）；实机实锤
            // 默认墙钟档把 3-4-5 游戏分钟放大成 3-5 现实分钟（3x 玩家≈7 倍稀薄），剧场 9 分钟零开播（2026-09-15）
            if (!m_ClockInitialized)
            {
                m_NextEvalAt = Now + TicksPerMinute; // 进城 1 游戏分钟后首评（让气泡层先采样一轮）
                m_ClockInitialized = true;
            }

            // 在飞兜底解锁：网关过期丢弃不回包（闲聊炉/S3 同款墙钟兜底——真实网络超时，与节奏时钟无关）
            if (m_ForgePending && (DateTime.UtcNow - m_ForgeSince).TotalSeconds > k_ForgeTtl + 60)
                m_ForgePending = false;

            SweepTheaters(); // 终了判定每拍都查（播完/失效/离屏不等评估节拍）

            var now = Now;
            if (now < m_NextEvalAt)
                return; // 还没到点
            // 无论本拍成败都先排下一拍——扫描频率被节拍硬限，候选落空不空转
            m_NextEvalAt = now + (3u + m_EvalCount % 3u) * TicksPerMinute; // 3-4-5 游戏分钟轮换
            m_EvalCount++;

            // 开关闸（§12 #49 + #52）：独立于 feedMode 的设置页开关，默认开；一闸同时闸住剧本炉与放送——关=零 token 也零播出
            if (!Content.ModSettings.BubbleTheaterEnabled)
                return;

            // ① 剧本炉水位补给：分区水位（任一场景标签库存 <k_SceneLowWater，2026-09-16 修正注记挂 §12 #67）
            // 且无在飞+网关可用+非 MUTE 才烧 token（池是缓冲，放送侧绝不开炉）
            var deficits = m_Stock.DeficitScenes(k_SceneLowWater); // 缺口分区（低频分配可接受）
            if (deficits.Count > 0 && !m_ForgePending && Mod.Gateway != null && !Llm.CliGateway.Mute)
            {
                var snapshot = m_Radar.Latest;
                if (snapshot.Citizens != 0) // 雷达还没采到样则本拍不开炉（快照要进 prompt）
                    FireStockForge(snapshot, deficits);
            }

            // ② 放送评估：纯执行层零 token——扫描绑人成立即从池取匹配剧本即时开播
            if (m_Active.Count >= k_MaxActive)
                return;
            TryCast();
        }

        // —— ① 剧本炉（水位触发，产库存入池） ——

        /// <summary>剧本炉开炉：固定头 + 动态尾（各场景库存数 + 缺口分区定向补给 + 城市快照 + 3-5 张处境卡灵感池，
        /// 禁写真名/真店名只写氛围——剧本是库存货不针对具体市民，#52 代价明账：味道靠场景标签+城市热点+灵感卡保）。
        /// 灵感卡从市民池跨步抽样（炉计数锚定，确定性——同炉次+同池状态必同批卡）。
        /// deficits=缺口分区（分区水位低于下限的标签，2026-09-16 修正注记挂 §12 #67）：补炉配额定向给缺口。</summary>
        private void FireStockForge(in Content.CitySnapshot snapshot, IReadOnlyList<string> deficits)
        {
            var entries = m_CitizenPool.Entries;
            var cards = new List<string>(5);
            if (entries.Count > 0)
            {
                var count = Math.Min(3 + (int)(m_ForgeCount % 3u), entries.Count); // 3-5 张
                var stride = Math.Max(1, entries.Count / count);
                var start = (int)(m_ForgeCount % (uint)entries.Count);
                for (var k = 0; k < count; k++)
                {
                    var entry = entries[(start + k * stride) % entries.Count];
                    if (entry.Age == CitizenAge.Child)
                        continue; // §12 #66：灵感卡只借氛围，与信息流主炉同口径滤儿童（街头场绑真人不走这里）
                    cards.Add(entry.Context);
                }
            }
            var prompt = Content.PromptBuilder.BuildTheaterStockPrompt(m_Head, snapshot, m_Stock, cards, k_ForgeBatch,
                Content.ModSettings.StreetTheaterMaxCast, deficits); // 设置上限+缺口分区都进动态尾（固定头逐字节稳定不动）
            Mod.Gateway!.Enqueue(new Llm.CliRequest(prompt, Llm.CliPriority.Normal, k_ForgeTtl, "theater:" + m_ForgeCount)); // Normal 不 Low：thinking 时代低优先级在队尾等死（§12 #51 实机）
            m_ForgePending = true;
            m_ForgeSince = DateTime.UtcNow;
            Mod.Log.Info($"[剧场] 剧本炉开炉：池存 {m_Stock.Count}（缺口：{string.Join("、", deficits)}，第 {m_ForgeCount + 1} 炉，产 {k_ForgeBatch} 部）");
            m_ForgeCount++;
        }

        /// <summary>
        /// 剧本炉结果处理（ContentDirectorSystem 按 "theater:" 前缀转交，闲聊炉同款路由纪律）：
        /// JSONL salvage 解析入池（坏行跳过计数，解析口径全在 TheaterScriptStock.ParseBatch）。
        /// 失败只记日志不致命——水位低了下一评估拍自然会再开炉。
        /// </summary>
        public void OnTheaterResult(Llm.CliCompletedResult r)
        {
            m_ForgePending = false;
            if (!r.Result.Success)
            {
                Mod.Log.Info($"[剧场] 剧本炉一炉失败：{r.Result.Error}（下拍再试）");
                return;
            }
            var added = m_Stock.AddBatch(r.Result.Text, out var skipped);
            Mod.Log.Info($"[剧场] 剧本入库 {added} 部（解析丢 {skipped} 行，池存 {m_Stock.Count}）");
        }

        // —— ② 放送选锚点 ——

        /// <summary>放送评估拍主流程：可见锚点快照 → 室外候选（车站/公园景点，按离镜头近扫）+ 窗口混编候选
        /// （行人锚点旁"有人气"的商店/住宅，§12 #56）+ 街头闲谈候选（行人锚点圈内抓路人，§12 #67）
        /// + 店内候选（CurrentBuilding 分组）——窗口/街头与店内共用
        /// 每拍最多一次的楼内市民分组（BuildIndoorGroups 懒建，街头不需要——纯室外抓人）。四类同台竞争，取离镜头最近且绑得够人的 →
        /// 按场景标签向剧本池取件：取到即绑名单开播（零 LLM 等待，绑定即复核）。
        /// 让贤机制（2026-09-16 修正注记挂 §12 #67）：<b>池里没匹配剧本的分区不参与 best 竞争</b>——
        /// 原写法是 best 选定后才 TryTake，空配分区（典型：home/shop 剧本被消耗完）每拍赢下 best 再空转跳过，
        /// 有剧本的分区（street）永远排不上（实机日志 20 连拍空转实锤）。距离折扣逻辑不变
        /// （street DistToCam ×1.5 照算），只是没剧本时让贤而不是吞拍。</summary>
        private void TryCast()
        {
            m_BubbleWorld ??= World.GetExistingSystemManaged<BubbleWorldSpikeSystem>();
            if (m_BubbleWorld == null)
                return;
            var cam = m_CameraUpdate.activeCamera;
            if (cam == null)
                return;
            var camPos = (float3)cam.transform.position;

            m_BubbleWorld.SnapshotAnchors(m_AnchorSnap);
            if (m_AnchorSnap.Count == 0)
                return; // 气泡层关着/还没采样——剧场无人可演
            // 离镜头近优先：按锚点离相机距离升序扫（参与者在锚点附近出没，近锚点≈近镜头）
            m_AnchorSnap.Sort((a, b) => math.distancesq(a.Pos, camPos).CompareTo(math.distancesq(b.Pos, camPos)));

            m_NameSystem ??= World.GetExistingSystemManaged<Game.UI.NameSystem>();

            Candidate? best = null;
            var skippedNoScript = new List<string>(); // 让贤分区计数（无剧本候选的标签，重复各记一次；日志用）
            // 有剧本才参与竞争（让贤）：无标签场景（学校/医院内等）天然无剧本，一并让贤
            bool HasScript(Candidate c)
                => c.SceneTag.Length > 0
                   && (m_Stock.HasMatch(c.SceneTag, c.Roster.Count)
                       || (c.SceneTag == Content.TheaterScriptStock.Street
                           && m_Stock.HasMatch(c.SceneTag, Content.ModSettings.StreetTheaterMaxCast))); // 街头场二段取件同口径（大剧本裁着演）
            void Consider(Candidate? c)
            {
                if (c == null)
                    return;
                if (!HasScript(c))
                {
                    skippedNoScript.Add(c.SceneTag.Length > 0 ? c.SceneTag : c.KindLabel);
                    return; // 让贤：池里没货/无标签的分区不占拍
                }
                if (best == null || c.DistToCam < best.DistToCam)
                    best = c;
            }

            var scanCount = math.min(k_MaxAnchorScan, m_AnchorSnap.Count);
            var statics = new NativeList<Entity>(32, Allocator.Temp);
            var movers = new NativeList<Entity>(32, Allocator.Temp);
            Dictionary<Entity, IndoorGroup>? indoorGroups = null; // 楼内市民分组：窗口/店内候选共用，每拍最多全量扫一次（懒建）
            for (int i = 0; i < scanCount; i++)
            {
                Consider(ScanOutdoor(m_AnchorSnap[i].Pos, camPos, statics, movers));
                if (m_AnchorSnap[i].Kind == 0) // 窗口混编/街头闲谈只从可见行人锚点出发（§12 #56/#67）
                {
                    indoorGroups ??= BuildIndoorGroups();
                    Consider(ScanWindow(m_AnchorSnap[i], camPos, statics, movers, indoorGroups));
                    Consider(ScanStreet(m_AnchorSnap[i], camPos, statics, movers));
                }
            }
            statics.Dispose();
            movers.Dispose();

            Consider(ScanIndoor(camPos, indoorGroups ?? BuildIndoorGroups()));

            if (best == null)
            {
                // 本拍无可演：全落空（城市安静/都在冷却）不打日志；有候选但都没剧本才打一行（让贤可观测）
                if (skippedNoScript.Count > 0)
                    Mod.Log.Info($"[剧场] 本拍 {skippedNoScript.Count} 个候选池无匹配剧本，全部让贤（{string.Join("、", skippedNoScript)}，池存 {m_Stock.Count}），本拍不开播");
                return;
            }
            var script = m_Stock.TryTake(best.SceneTag, best.Roster.Count);
            if (script == null && best.SceneTag == Content.TheaterScriptStock.Street)
            {
                // §12 #67 小演第二段：街头场没有正好演得起的剧本时，允许取大剧本裁着演
                // （StartTheater 里 cast>有效人数取前 N 名+丢 speaker 越界台词，丢完不足 2 句弃播退回池）
                script = m_Stock.TryTake(best.SceneTag, Content.ModSettings.StreetTheaterMaxCast);
            }
            if (script == null)
            {
                // 理论到不了（HasScript 已保证有匹配），纯防御——HasMatch 与 TryTake 同口径扫描
                Mod.Log.Info($"[剧场] 候选成立但取件失败：{best.KindLabel}（标签 {best.SceneTag}，{best.Roster.Count} 人，池存 {m_Stock.Count}），本拍跳过");
                return;
            }
            StartTheater(best, script);
        }

        /// <summary>室外候选：以一个可见锚点为圆心扫 40m 圈——车站（候车 ≥2）优先，其次公园/景点；
        /// 绑人=圈内 30m 带 Human+Resident 的行人 agent 回指市民（真名单，不绑随机路过的车/动物）。
        /// 同一圈只出一个候选；绑不够 2 人/地点在冷却/已被占用 → null。</summary>
        private Candidate? ScanOutdoor(float3 anchorPos, float3 camPos, NativeList<Entity> statics, NativeList<Entity> movers)
        {
            statics.Clear();
            movers.Clear();
            m_Environment.CollectAround(anchorPos, k_ScanRadius, statics, movers);

            var station = Entity.Null;
            var stationPos = float3.zero;
            var stationWait = 0;
            var park = Entity.Null;
            var parkPos = float3.zero;
            var parkIsSignature = false;
            for (int i = 0; i < statics.Length; i++)
            {
                var e = statics[i];
                if (!EntityManager.HasComponent<Transform>(e))
                    continue;
                if (EntityManager.HasComponent<WaitingPassengers>(e))
                {
                    var w = EntityManager.GetComponentData<WaitingPassengers>(e).m_Count; // 候车人数（计数不是名单，spike §4）
                    if (w >= k_MinWaiting && w > stationWait)
                    {
                        station = e;
                        stationPos = EntityManager.GetComponentData<Transform>(e).m_Position;
                        stationWait = w;
                    }
                }
                else if (park == Entity.Null && EntityManager.HasComponent<Game.Buildings.Building>(e)
                         && (EntityManager.HasComponent<Game.Prefabs.SignatureBuildingData>(e)
                             || EntityManager.HasComponent<Game.Buildings.AttractivenessProvider>(e)))
                {
                    park = e;
                    parkPos = EntityManager.GetComponentData<Transform>(e).m_Position;
                    parkIsSignature = EntityManager.HasComponent<Game.Prefabs.SignatureBuildingData>(e);
                }
            }

            Entity loc;
            float3 locPos;
            string label;
            string tag; // 剧本池场景标签（TheaterScriptStock 白名单值）
            if (station != Entity.Null) { loc = station; locPos = stationPos; label = "车站候车"; tag = Content.TheaterScriptStock.Station; }
            else if (park != Entity.Null) { loc = park; locPos = parkPos; label = parkIsSignature ? "景点" : "公园"; tag = Content.TheaterScriptStock.Park; }
            else return null;
            if (OnCooldown(loc) || LocationInUse(loc))
                return null;

            var cand = new Candidate
            {
                Location = loc,
                Pos = locPos,
                KindLabel = label,
                SceneTag = tag,
                DistToCam = math.distance(camPos, locPos),
            };
            BindOutdoorRoster(locPos, movers, cand.Roster);
            return cand.Roster.Count >= k_MinParticipants ? cand : null;
        }

        /// <summary>开放场所绑人：圈内 agent → 带 Human（排动物）+ Resident（排载具）+ Transform（可锚定）
        /// 的回指市民，按离地点距离升序取前 maxCount；市民实体去重+排除在组/在冷却名单。
        /// maxCount 默认 k_MaxParticipants（车站/公园场）；街头闲谈（§12 #67）传设置页"最大人数"（2-5）。
        /// #66 起儿童不特别排除（街头场 cast 不排儿童，§12 #66 末段）。</summary>
        private void BindOutdoorRoster(float3 locPos, NativeList<Entity> movers, List<(Entity Citizen, Entity Anchor, bool Indoor)> dst, int maxCount = k_MaxParticipants)
        {
            var scored = new List<(Entity Citizen, Entity Agent, float D2)>(movers.Length);
            for (int i = 0; i < movers.Length; i++)
            {
                var agent = movers[i];
                if (!EntityManager.HasComponent<Game.Creatures.Human>(agent)
                    || !EntityManager.HasComponent<Game.Creatures.Resident>(agent)
                    || !EntityManager.HasComponent<Transform>(agent))
                    continue;
                var citizen = EntityManager.GetComponentData<Game.Creatures.Resident>(agent).m_Citizen;
                if (citizen == Entity.Null || !EntityManager.HasComponent<Citizen>(citizen) || InUse(citizen))
                    continue;
                if (m_ByAnchor.ContainsKey(agent))
                    continue; // 这个锚点已经在别的剧场里演着
                var dup = false;
                foreach (var s in scored)
                    if (s.Citizen == citizen) { dup = true; break; }
                if (dup)
                    continue;
                var p = EntityManager.GetComponentData<Transform>(agent).m_Position;
                var d = p - locPos;
                scored.Add((citizen, agent, math.dot(d, d)));
            }
            scored.Sort((a, b) => a.D2.CompareTo(b.D2));
            var r2 = k_BindRadius * k_BindRadius;
            for (int i = 0; i < scored.Count && dst.Count < maxCount; i++)
                if (scored[i].D2 <= r2)
                    dst.Add((scored[i].Citizen, scored[i].Agent, false));
        }

        /// <summary>楼内市民分组（剧场级低频全量扫，3-5 游戏分钟才一次；窗口⑥/店内候选共用，每评估拍
        /// 最多建一次）：建筑 → 楼内市民（Count 全记，Members 只收空闲的——不在组，封顶 k_MaxParticipants）。
        /// 外部连接等非建筑实体剔除（spike §1 兜底纪律）；读不到位置的没法演。</summary>
        private Dictionary<Entity, IndoorGroup> BuildIndoorGroups()
        {
            var arr = m_IndoorQuery.ToEntityArray(Allocator.Temp);
            var groups = new Dictionary<Entity, IndoorGroup>();
            for (int i = 0; i < arr.Length; i++)
            {
                var e = arr[i];
                var b = EntityManager.GetComponentData<CurrentBuilding>(e).m_CurrentBuilding;
                if (!EntityManager.HasComponent<Game.Buildings.Building>(b)
                    || !EntityManager.HasComponent<Transform>(b))
                    continue;
                if (!groups.TryGetValue(b, out var g))
                {
                    g = new IndoorGroup();
                    groups[b] = g;
                }
                g.Count++;
                if (g.Members.Count < k_MaxParticipants && !InUse(e))
                    g.Members.Add(e);
            }
            arr.Dispose();
            return groups;
        }

        /// <summary>店内候选：从楼内市民分组里挑 ≥2 人且落在某个可见锚点 40m 内的建筑才算；
        /// 绑人=楼内市民（锚点=建筑本体，多人共锚），取离镜头最近的一栋。全不中 → null。</summary>
        private Candidate? ScanIndoor(float3 camPos, Dictionary<Entity, IndoorGroup> groups)
        {
            Candidate? best = null;
            foreach (var kv in groups)
            {
                if (kv.Value.Count < k_MinParticipants || kv.Value.Members.Count < k_MinParticipants)
                    continue;
                var b = kv.Key;
                if (OnCooldown(b) || LocationInUse(b) || m_ByAnchor.ContainsKey(b))
                    continue;
                var pos = EntityManager.GetComponentData<Transform>(b).m_Position;
                if (!NearAnyVisibleAnchor(pos))
                    continue; // 离镜头近优先的落点保证：参与者最终要能落在屏内
                var dist = math.distance(camPos, pos);
                if (best != null && dist >= best.DistToCam)
                    continue;
                var kind = CitizenPoolSystem.ClassifyBuilding(EntityManager, b);
                var cand = new Candidate
                {
                    Location = b,
                    Pos = pos,
                    KindLabel = (kind ?? "建筑") + "内",
                    SceneTag = IndoorSceneTag(kind), // 商店→shop、住宅区→home；其余室内类型无标签=本拍跳过
                    DistToCam = dist,
                };
                foreach (var m in kv.Value.Members)
                    cand.Roster.Add((m, b, true));
                best = cand;
            }
            return best;
        }

        /// <summary>窗口混编候选（§12 #56）：以一个可见行人锚点（kind=0）为圆心查 20m 静树，取最近一栋
        /// "有人气"的商店/住宅（车站/公园/其他业态跳过——复用 IndoorSceneTag 室内分类映射当白名单），
        /// 且屋内 ≥1 个空闲市民（groups 的 Members 已滤在组）。名单=[（路人市民, 行人 agent, 室外）,
        /// （屋内市民, 建筑本体, 室内）]——1 外 1 内混编（混合锚点现成：agent kind=0、建筑 kind=2；
        /// 顺序即角色序号，prompt 头写明 speaker 1=路人、speaker 2=屋里人）。地点=建筑（冷却/占用判它），
        /// 标签 window。路人不成立/圈内没有够格的楼 → null。</summary>
        private Candidate? ScanWindow((Entity Anchor, byte Kind, float3 Pos) a, float3 camPos,
                                      NativeList<Entity> statics, NativeList<Entity> movers,
                                      Dictionary<Entity, IndoorGroup> groups)
        {
            // 路人侧：行人 agent 回指市民（BindOutdoorRoster 同款防御：Human 排动物、Resident 排载具、Transform 可锚定）
            var agent = a.Anchor;
            if (!EntityManager.HasComponent<Game.Creatures.Human>(agent)
                || !EntityManager.HasComponent<Game.Creatures.Resident>(agent)
                || !EntityManager.HasComponent<Transform>(agent))
                return null;
            var pedestrian = EntityManager.GetComponentData<Game.Creatures.Resident>(agent).m_Citizen;
            if (pedestrian == Entity.Null || !EntityManager.HasComponent<Citizen>(pedestrian) || InUse(pedestrian))
                return null;
            if (m_ByAnchor.ContainsKey(agent))
                return null; // 这个行人已经在别的剧场里演着

            statics.Clear();
            movers.Clear();
            m_Environment.CollectAround(a.Pos, k_WindowScanRadius, statics, movers);

            // 圈内最近一栋"有人气"的商店/住宅（有人气=屋内 ≥1 个空闲市民）
            var loc = Entity.Null;
            var locPos = float3.zero;
            string? locKind = null;
            var indoorCitizen = Entity.Null;
            var bestD2 = float.MaxValue;
            for (int i = 0; i < statics.Length; i++)
            {
                var e = statics[i];
                if (!EntityManager.HasComponent<Game.Buildings.Building>(e)
                    || !EntityManager.HasComponent<Transform>(e))
                    continue;
                var kind = CitizenPoolSystem.ClassifyBuilding(EntityManager, e);
                if (IndoorSceneTag(kind).Length == 0)
                    continue; // 只要商店/住宅，车站/公园/学校/医院/办公楼等跳过
                if (OnCooldown(e) || LocationInUse(e) || m_ByAnchor.ContainsKey(e))
                    continue;
                if (!groups.TryGetValue(e, out var g))
                    continue;
                var member = Entity.Null;
                foreach (var m in g.Members)
                    if (m != pedestrian) { member = m; break; } // 顺带排掉路人自己（理论到不了，纯防御）
                if (member == Entity.Null)
                    continue; // 屋里没有空闲市民
                var p = EntityManager.GetComponentData<Transform>(e).m_Position;
                var d = p - a.Pos;
                var d2 = math.dot(d, d);
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    loc = e;
                    locPos = p;
                    locKind = kind;
                    indoorCitizen = member;
                }
            }
            if (loc == Entity.Null)
                return null;

            var cand = new Candidate
            {
                Location = loc,
                Pos = locPos,
                KindLabel = locKind + "窗口", // 商店窗口/住宅区窗口（日志+场景名兜底用）
                SceneTag = Content.TheaterScriptStock.Window,
                DistToCam = math.distance(camPos, locPos),
            };
            cand.Roster.Add((pedestrian, agent, false));   // speaker 1=路人
            cand.Roster.Add((indoorCitizen, loc, true));   // speaker 2=屋里人
            return cand;
        }

        /// <summary>街头闲谈候选（§12 #67，#63 形态升级：路上闲谈可变人变长、复用剧场机制）：
        /// 从可见行人锚点出发，圈内 30m 抓步行路人组 roster（BindOutdoorRoster 同款口径，
        /// 上限=设置页"街头短剧最大人数"）——熟人偶遇/陌生人搭话均可，现场不足 2 人不开演（不硬凑）。
        /// 地点=出发锚点本体（街头无固定场所：冷却/占用判它，锚点走远失效自然终了="人散"）；
        /// 标签 street；DistToCam ×k_StreetScorePenalty 权重折扣参与同台竞争（别让它刷屏；
        /// 池里有 street 剧本时豁免折扣——有货让路没货才压，2026-09-17 实机 street 0 开播修复）。
        /// 锚点在冷却/在演 → null。</summary>
        private Candidate? ScanStreet((Entity Anchor, byte Kind, float3 Pos) a, float3 camPos,
                                      NativeList<Entity> statics, NativeList<Entity> movers)
        {
            if (OnCooldown(a.Anchor) || LocationInUse(a.Anchor) || m_ByAnchor.ContainsKey(a.Anchor))
                return null;
            statics.Clear();
            movers.Clear();
            m_Environment.CollectAround(a.Pos, k_StreetScanRadius, statics, movers); // statics 用不上，收在调用方
            var cand = new Candidate
            {
                Location = a.Anchor,
                Pos = a.Pos,
                KindLabel = "街头",
                SceneTag = Content.TheaterScriptStock.Street,
                // 权重略低（§12 #67：与 window 相当或略低）——池里有 street 剧本时豁免折扣
                // （2026-09-17 实机 street 0 开播修复②：没剧本时本来就赢不了、保持折扣；
                // 有剧本时压折扣=永远演不了——有货让路，没货才压）
                DistToCam = math.distance(camPos, a.Pos) * (m_Stock.HasMatch(Content.TheaterScriptStock.Street, Content.ModSettings.StreetTheaterMaxCast) ? 1f : k_StreetScorePenalty),
            };
            BindOutdoorRoster(a.Pos, movers, cand.Roster, Content.ModSettings.StreetTheaterMaxCast);
            return cand.Roster.Count >= k_MinParticipants ? cand : null;
        }

        /// <summary>地点是否落在任一可见锚点 k_ScanRadius 内（店内候选的"离镜头近"判据）。</summary>
        private bool NearAnyVisibleAnchor(float3 pos)
        {
            var r2 = k_ScanRadius * k_ScanRadius;
            foreach (var a in m_AnchorSnap)
            {
                var d = a.Pos - pos;
                if (math.dot(d, d) <= r2)
                    return true;
            }
            return false;
        }

        // —— ③ 取件开播（放送时绑定，绑定即复核） ——

        /// <summary>室内建筑类型词 → 剧本场景标签（§12 #52 定案只四类分区）：商店→shop、住宅区→home；
        /// 学校/医院/工厂/办公楼/未分类 → ""（池无此分区，候选本拍跳过——扩新分区见 TheaterScriptStock 头注释，三处同改）。
        /// 窗口混编（§12 #56）不复用返回值，只把本映射当"有人气建筑"白名单——非 ""（商店/住宅）才够格（见 ScanWindow）。</summary>
        private static string IndoorSceneTag(string? kind) => kind switch
        {
            "商店" => Content.TheaterScriptStock.Shop,
            "住宅区" => Content.TheaterScriptStock.Home,
            _ => "",
        };

        /// <summary>取到剧本即开播：绑名单（roster 前 castN 人——castN=min（剧本人数， 现场有效人数），
        /// §12 #67 小演：剧本人多就裁着演，丢 speaker 越界台词，丢完不足 2 句弃播退回池；街头场句数
        /// 再截到设置页"最长句数"短演——全部向下取不硬凑；名单是这一拍刚扫出来的活人，绑定即复核，
        /// 无飞行窗口）→ Participants/Lines 初始化（其余锚点"……"在听，第一句带名字前缀落说话人锚点，
        /// §12 #50）→ Anchors/m_ByAnchor 登记 → EnsureAnchor 五道闸。
        /// 剧本开播才消耗：任一锚点不可锚=回滚登记+Return 退回池+日志开播中止，不上冷却（#52：冷却只在终了后上）。</summary>
        private void StartTheater(Candidate cand, Content.TheaterScript script)
        {
            m_BubbleWorld ??= World.GetExistingSystemManaged<BubbleWorldSpikeSystem>();
            var bubbles = m_BubbleWorld; // 局部变量落地——编译器 nullable 流分析认局部不认字段
            if (bubbles == null)
            {
                m_Stock.Return(script); // 气泡层未就绪——退回池不消耗
                return;
            }

            var sceneName = SceneNameOf(cand);
            var t = new Theater { Location = cand.Location, SceneName = sceneName };
            // §12 #67 小演：剧本 cast > 现场有效人数时取前 N 名（街头场二段取件会拿到大剧本；
            // 其余场景 TryTake 已保证 cast ≤ roster，这里等值不裁）。室外名单已按离地点升序，前 N 名=最近的真人
            var castN = Math.Min(script.Cast, cand.Roster.Count);
            if (castN < k_MinParticipants)
            {
                m_Stock.Return(script); // 理论到不了（候选门槛 ≥2 人），纯防御——名单不足退回不消耗
                return;
            }
            var names = new List<string>(castN);
            for (var i = 0; i < castN; i++)
            {
                var (citizen, anchor, indoor) = cand.Roster[i];
                var name = m_NameSystem != null ? m_NameSystem.GetRenderedLabelName(citizen) : null;
                t.Participants.Add(new Participant { Citizen = citizen, Anchor = anchor, Name = string.IsNullOrEmpty(name) ? "市民" : name, Indoor = indoor });
                names.Add(t.Participants[t.Participants.Count - 1].Name);
            }
            // §12 #67 短演：街头场句数截到设置页"最长句数"（其余场景不截——池解析已有 12 句硬顶）；
            // 小演丢 speaker 越界台词（指着被裁角色的句子没人说）
            var maxLines = cand.SceneTag == Content.TheaterScriptStock.Street
                ? Content.ModSettings.StreetTheaterMaxLines : int.MaxValue;
            for (var li = 0; li < script.Lines.Count && t.Script.Count < maxLines; li++)
            {
                var (sp, text) = script.Lines[li];
                if (sp >= castN)
                    continue; // 越界台词丢弃（§12 #67 向下取）
                t.Script.Add((sp, FillSlots(text, t.Participants, sceneName, sp))); // 刀⑥槽位化：绑定即填真人真名场景名（自称守卫随槽走）
            }
            if (t.Script.Count < k_MinLinesTheater)
            {
                m_Stock.Return(script); // 小演丢完不足 2 句=弃播：退回池不消耗（与绑锚失败同口径，不上冷却）
                Mod.Log.Info($"[剧场] 弃播：{sceneName}（{cand.KindLabel}，cast {script.Cast} 小演到 {castN} 人后不足 2 句，剧本退回池存 {m_Stock.Count}）");
                return;
            }
            foreach (var p in t.Participants)
                if (!t.Lines.ContainsKey(p.Anchor))
                {
                    t.Lines[p.Anchor] = "……"; // 在听
                    t.Anchors.Add(p.Anchor);
                }
            var (s0, t0) = t.Script[0];
            t.Lines[t.Participants[s0].Anchor] = t.Participants[s0].Name + "：" + t0; // 名字前缀（§12 #50：多人/共锚分辨说话人，顺带成剧场视觉标识）
            t.CurrentAnchor = t.Participants[s0].Anchor; // 严格串行（2026-09-16 注记挂 §12 #67）：只有当前说话人出泡
            t.Cursor = 1;
            // 这里不置 Finished（哪怕单句剧本）：Finished 语义=末句被读完，在 OnAnchorRotated 里置——
            // 推句时置会让 TryGetLine 的 !Finished 闸把末句挡在渲染外（2026-09-14"屋里人回话被吃"实锤）
            m_Active.Add(t);
            foreach (var a in t.Anchors)
                m_ByAnchor[a] = t;

            // 锚点建组（五道闸在 EnsureAnchor 里）；任一不可锚=开播中止（回滚登记，剧本退回池，不上冷却）
            foreach (var a in t.Anchors)
            {
                if (!bubbles.EnsureAnchor(a, AnchorKindOf(t, a)))
                {
                    foreach (var x in t.Anchors)
                        m_ByAnchor.Remove(x);
                    m_Active.Remove(t);
                    m_Stock.Return(script);
                    Mod.Log.Info($"[剧场] 开播中止：{sceneName}（锚点不可锚：离屏/层关/容量满），剧本退回池（池存 {m_Stock.Count}）");
                    return;
                }
            }
            // 立即换文案开播（不等各锚点自己的时钟——第一拍说话人就位；旁听者刷成"……"隐身，
            // 严格串行从开播即成立：同剧组同屏只 1 泡，2026-09-16 注记挂 §12 #67）
            foreach (var a in t.Anchors)
                bubbles.RefreshAnchorText(a);
            Mod.Log.Info($"[剧场] 开播：{sceneName}（{cand.KindLabel}），{t.Participants.Count} 人 {t.Script.Count} 句：{string.Join("、", names)}");
        }

        /// <summary>即席剧注入（§12 #63 修正，2026-09-17 玩家三次实机反馈定案：配对对话卡废除"单泡双行"，
        /// 改串行双泡——你一句我一句）：收炉到货时把配对双人卡当 cast=2/lines=2 的微型剧场开播——
        /// 预绑 A/B 真人（配对时扫到的市民，不走 roster 抓人/剧本池/放送评估），显示语义与 #67 严格串行
        /// 完全一致（CurrentAnchor 闸：A 句 HoldFor 播完消失、B 句再接；同组同屏只 1 泡；剧场泡必留+
        /// 计入同屏上限账，世界泡侧零改动复用）。
        /// 拒收即 false（调用方降级只留 A 句独白入池，别硬演）：剧场开关关/同屏满员/气泡层未就绪/
        /// 演员实体失效/在他组串场/任一方已不在步行（进楼/上车→无 agent）/锚点五道闸不过。
        /// 即席剧无剧本可退——不入池不消耗（剧本池语义不涉及）；地点=agentA（冷却语义同街头场，ephemeral 无妨）。</summary>
        internal bool InjectPairPlay(Entity citizenA, string nameA, string lineA, Entity citizenB, string nameB, string lineB)
        {
            if (!Content.ModSettings.BubbleTheaterEnabled)
                return false; // 剧场开关关="不成组"——即席剧同闸
            if (m_Active.Count >= k_MaxActive)
                return false; // 同屏满员
            m_BubbleWorld ??= World.GetExistingSystemManaged<BubbleWorldSpikeSystem>();
            var bubbles = m_BubbleWorld; // 局部变量落地（编译器 nullable 流分析认局部不认字段）
            if (bubbles == null)
                return false;
            if (!EntityManager.Exists(citizenA) || !EntityManager.Exists(citizenB)
                || InUse(citizenA) || InUse(citizenB))
                return false; // 演员到货窗口期内离场/串场
            if (!TryGetWalkerAgent(citizenA, out var agentA) || !TryGetWalkerAgent(citizenB, out var agentB))
                return false; // 已不在步行（进楼/上车）——即席剧只锚行人 agent（台词是擦肩对话）
            if (agentA == agentB || m_ByAnchor.ContainsKey(agentA) || m_ByAnchor.ContainsKey(agentB))
                return false;

            var t = new Theater { Location = agentA, SceneName = "街头即席" };
            t.Participants.Add(new Participant { Citizen = citizenA, Anchor = agentA, Name = nameA, Indoor = false });
            t.Participants.Add(new Participant { Citizen = citizenB, Anchor = agentB, Name = nameB, Indoor = false });
            t.Script.Add((0, lineA));
            t.Script.Add((1, lineB));
            t.Lines[agentA] = nameA + "：" + lineA; // 名字前缀（§12 #50：分辨说话人+剧场视觉标识）
            t.Lines[agentB] = "……"; // 在听（严格串行：B 等 A 说完）
            t.Anchors.Add(agentA);
            t.Anchors.Add(agentB);
            t.CurrentAnchor = agentA;
            t.Cursor = 1;
            // 登记+锚点建组（五道闸在 EnsureAnchor；与 StartTheater 开播段同构——即席剧无剧本可退，回滚即弃）
            m_Active.Add(t);
            m_ByAnchor[agentA] = t;
            m_ByAnchor[agentB] = t;
            foreach (var a in t.Anchors)
            {
                if (!bubbles.EnsureAnchor(a, 0))
                {
                    m_ByAnchor.Remove(agentA);
                    m_ByAnchor.Remove(agentB);
                    m_Active.Remove(t);
                    Mod.Log.Info($"[剧场] 即席剧拒收：{nameA}×{nameB}（锚点不可锚：离屏/层关/容量满），降级独白");
                    return false;
                }
            }
            // 立即换文案开播（A 出句 B 隐身；不等各自时钟，同 StartTheater）
            bubbles.RefreshAnchorText(agentA);
            bubbles.RefreshAnchorText(agentB);
            Mod.Log.Info($"[剧场] 即席剧开播：{nameA}×{nameB}（串行双泡 2 句）");
            return true;
        }

        /// <summary>市民 → 其步行行人 agent（CurrentTransport 回指；Human+Transform 双闸——进楼/上车即无，拒收降级）。</summary>
        private bool TryGetWalkerAgent(Entity citizen, out Entity agent)
        {
            agent = Entity.Null;
            if (!EntityManager.HasComponent<CurrentTransport>(citizen))
                return false;
            var t = EntityManager.GetComponentData<CurrentTransport>(citizen).m_CurrentTransport;
            if (t == Entity.Null || !EntityManager.HasComponent<Game.Creatures.Human>(t)
                || !EntityManager.HasComponent<Transform>(t))
                return false;
            agent = t;
            return true;
        }

        /// <summary>占位填充（§12 #60 刀⑥剧场槽位化）：{place}→场景真名、{nameN}→第 N 个参与者真人名
        /// （生成时写槽、放送时填——库存剧本的通用性与在地具体感的换层解；§12 #67 起槽位放开到 name5，
        /// street cast 可到 5）。
        /// N 越界（模型写嗨了）落"朋友"salvage 不丢句；填充后超长不截（名字短，气泡排版自适应）。
        /// 自称守卫（2026-09-14 实机"大卫：大卫，垃圾又堆门口了"实锤——prompt 样子曾把 speaker 1 的台词
        /// 写成 {name1}，模型照镜像）：speaker（0 基）自己的 {nameN} 槽改填下一位参与者（2 人局=对方）。</summary>
        private static string FillSlots(string text, System.Collections.Generic.List<Participant> participants, string sceneName, int speaker)
        {
            var selfSlot = "{name" + (speaker + 1) + "}";
            if (text.Contains(selfSlot) && participants.Count > 1)
                text = text.Replace(selfSlot, participants[(speaker + 1) % participants.Count].Name);
            if (text.Contains("{place}"))
                text = text.Replace("{place}", sceneName);
            for (var n = 1; n <= 5; n++)
            {
                var slot = "{name" + n + "}";
                if (text.Contains(slot))
                    text = text.Replace(slot, n <= participants.Count ? participants[n - 1].Name : "朋友");
            }
            return text;
        }

        /// <summary>场景地点名：店内与窗口（§12 #56 地点同为建筑）走 ShopNameOf（店内店名=租户公司名优先、
        /// 住宅落住户姓；分区自长建筑的 zone 通用名已在那层斩断=功能区标签绝不进台词，§12 #62），
        /// 车站/公园=渲染名；读不到或拿到未本地化的
        /// 资源键（"Assets.NAME[...]"——部分资产无本地化名的实机实锤，2026-09-11 Commercial_ChemicalStore）
        /// → 类型词兜底（不硬造，脏键绝不进 prompt）。</summary>
        private string SceneNameOf(Candidate cand)
        {
            string? name = cand.KindLabel.EndsWith("内") || cand.SceneTag == Content.TheaterScriptStock.Window
                ? EnvironmentDigestSystem.ShopNameOf(EntityManager, m_NameSystem, cand.Location)
                : EnvironmentDigestSystem.RenderedName(m_NameSystem, cand.Location);
            return name == null || name.Contains("Assets.") ? cand.KindLabel : name;
        }

        // —— ④ 播放（气泡系统的两个回调口）——

        /// <summary>气泡系统插队查询（<b>纯查询，绝不推轮次</b>——推进只走 <see cref="OnAnchorRotated"/>）：
        /// 锚点挂在活剧场 → 当前应显台词；不在任何剧场/剧场已播完 → false（调用方回退片段池）。</summary>
        internal bool TryGetLine(Entity anchor, out string line)
        {
            line = "";
            if (m_ByAnchor.TryGetValue(anchor, out var t) && !t.Finished && t.Lines.TryGetValue(anchor, out var l))
            {
                line = l;
                return true;
            }
            return false;
        }

        /// <summary>气泡防叠优先级查询（纯查询，§12 #49）：锚点挂在未播完的活剧场 → true——
        /// 剧场泡不被路人泡顶掉（人堆里"近者优先"帧帧翻盘、谁也没读完的实机教训）。</summary>
        internal bool HasActiveOn(Entity anchor)
            => m_ByAnchor.TryGetValue(anchor, out var t) && !t.Finished;

        /// <summary>气泡生命周期回调（BubbleWorldSpikeSystem.TickLifecycle 在剧场锚点到时换文案前调）：
        /// 剧本推进一句——下一句写到其说话人的锚点上，并让该锚点立即换文案（RefreshAnchorText：
        /// 上条读完下条接话，不等说话人自己的时钟）。
        /// 严格串行（2026-09-16 玩家定案，注记挂 §12 #67）：<b>只有当前说话人锚点（CurrentAnchor）的轮换
        /// 才推轮次</b>——隐身旁听锚点到期不推（不然旁听者"……"短驻留会抢拍推句）；接话瞬间其余锚点
        /// 全部回"……"隐身，同一时刻一个剧组只有一泡在屏上（原写法说完的句子挂在各锚点重播，多人句子
        /// 同屏重叠、占满上限互顶，实机实锤）。共锚（室内多人一栋楼）天然单泡串行，同一套代码覆盖。
        /// <b>Finished 只在"末句被读完"（游标尽头的锚点再次轮换）时置</b>——推末句时就置会让
        /// TryGetLine 的 !Finished 闸把末句挡在渲染外（2026-09-14 实机实锤：窗口剧场 2 句制，
        /// 屋里人回话 100% 被吃）。播完后 OnUpdate 统一收尾。</summary>
        internal void OnAnchorRotated(Entity anchor)
        {
            if (!m_ByAnchor.TryGetValue(anchor, out var t) || t.Finished)
                return;
            if (anchor != t.CurrentAnchor)
                return; // 严格串行：只有当前说话人播完（HoldFor 到期）才接下一句；旁听锚点到期不推轮次
            if (t.Cursor >= t.Script.Count)
            {
                t.Finished = true; // 末句读完，剧终
                return;
            }
            var (speaker, text) = t.Script[t.Cursor++];
            var speakerAnchor = t.Participants[speaker].Anchor;
            // 接话瞬间上一句让位：除新说话人外的锚点全部回"……"隐身（同剧组同屏只 1 泡）
            foreach (var a in t.Anchors)
            {
                if (a == speakerAnchor || t.Lines[a] == "……")
                    continue;
                t.Lines[a] = "……";
                if (a != anchor) // 触发本轮换的锚点由 TickLifecycle 随后的 SetBubbleText 自然落"……"，别重复刷新
                {
                    m_BubbleWorld ??= World.GetExistingSystemManaged<BubbleWorldSpikeSystem>();
                    m_BubbleWorld?.RefreshAnchorText(a); // 立即隐身上一句（RefreshAnchorText 不推轮次，纯换文案）
                }
            }
            t.CurrentAnchor = speakerAnchor;
            t.Lines[speakerAnchor] = t.Participants[speaker].Name + "：" + text; // 名字前缀（§12 #50）
            if (speakerAnchor != anchor)
            {
                m_BubbleWorld ??= World.GetExistingSystemManaged<BubbleWorldSpikeSystem>();
                m_BubbleWorld?.RefreshAnchorText(speakerAnchor); // 接话人立即出泡（不等它自己的时钟；纯查询路径，无递归）
            }
        }

        // —— ⑤ 终了 ——

        /// <summary>终了判定（每拍都查）：播完 / 地点失效 / 任一参与者失效 / 任一锚点离屏——锚点失效即锁。</summary>
        private void SweepTheaters()
        {
            if (m_Active.Count == 0)
                return;
            m_BubbleWorld ??= World.GetExistingSystemManaged<BubbleWorldSpikeSystem>();
            for (int i = m_Active.Count - 1; i >= 0; i--)
            {
                var t = m_Active[i];
                string? reason = null;
                if (t.Finished)
                    reason = "播完";
                else if (!EntityManager.Exists(t.Location))
                    reason = "地点失效";
                else
                {
                    foreach (var p in t.Participants)
                        if (!EntityManager.Exists(p.Citizen)) { reason = "参与者失效（" + p.Name + "）"; break; }
                    if (reason == null && m_BubbleWorld != null)
                        foreach (var a in t.Anchors)
                            if (!m_BubbleWorld.HasAnchorOf(a)) { reason = "锚点离屏"; break; }
                }
                if (reason != null)
                    EndTheater(t, reason);
            }
        }

        /// <summary>终了收尾：日志一行（原因+播到第几句）+ 地点上冷却 + 登记清除。
        /// 锚点气泡不主动清——插队失效后下一轮换文案自然回退片段池。</summary>
        private void EndTheater(Theater t, string reason)
        {
            Mod.Log.Info($"[剧场] 终了：{t.SceneName}（{reason}，播到第 {t.Cursor}/{t.Script.Count} 句）");
            SetCooldown(t.Location);
            foreach (var a in t.Anchors)
                m_ByAnchor.Remove(a);
            m_Active.Remove(t);
        }

        // —— 工具 ——

        /// <summary>锚点类型：室内参与者的锚点是建筑（kind 2），室外是行人 agent（kind 0）。</summary>
        private static byte AnchorKindOf(Theater t, Entity anchor)
        {
            foreach (var p in t.Participants)
                if (p.Anchor == anchor)
                    return p.Indoor ? (byte)2 : (byte)0;
            return 0; // 理论到不了，纯防御
        }

        /// <summary>地点冷却（锚点失效即锁 #28 + §12 #52：只在终了后上 ≈3 炉节拍冷却防连开；
        /// 开播中止不上冷却——中止是锚点侧问题不是地点的锅，剧本已退回池）。
        /// 时刻锚游戏时间 tick（#48 供给节奏，不走 #68 选项——选项只管逐句显示时钟）。</summary>
        private void SetCooldown(Entity loc) => m_CooldownUntil[loc] = Now + k_CooldownMinutes * TicksPerMinute;

        /// <summary>地点是否在冷却中（过期条目顺带清，表恒小）。</summary>
        private bool OnCooldown(Entity loc)
        {
            if (!m_CooldownUntil.TryGetValue(loc, out var until))
                return false;
            if (Now < until)
                return true;
            m_CooldownUntil.Remove(loc);
            return false;
        }

        /// <summary>地点是否已被占用（活剧场在演）。</summary>
        private bool LocationInUse(Entity loc)
        {
            foreach (var t in m_Active)
                if (t.Location == loc)
                    return true;
            return false;
        }

        /// <summary>市民是否已在剧组里（在演——真名单不串场）。</summary>
        private bool InUse(Entity citizen)
        {
            foreach (var t in m_Active)
                foreach (var p in t.Participants)
                    if (p.Citizen == citizen)
                        return true;
            return false;
        }
    }
}
