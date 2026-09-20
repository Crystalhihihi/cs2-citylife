using System;
using System.Collections.Generic;
using Game;
using Game.Citizens;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 闲聊炉（S4，§12 #48 双炉定案）：3-5 游戏分钟一炉，把真实市民处境卡（CitizenPoolSystem）
    /// × 话题库配题（TopicReservoir）×【城市此刻】（TopicRadarSystem 快照）喂给 LLM，
    /// 产出"张嘴说话"语域的气泡片段入 BubbleSnippetPool——展示零 token：池是缓冲，
    /// S5 接气泡世界层后刷屏不再花 token（本任务不接世界层，只备好池子）。
    /// S6 环境圈摘要：组炉时对每张被选中的处境卡按市民实体现位查一次 EnvironmentDigestSystem.BuildDigest
    /// （40m 半径四叉树聚类蒸馏），非空缀"｜旁边：X、Y"进卡的 prompt 文本；每炉查询次数=卡片数（≤14），不进热路径。
    /// §12 #63 对话场景卡（2026-09-17 形态修正：显示层改串行双泡，玩家三次实机反馈）：组炉时对 Walk 卡约 60%
    /// 槽位（炉计数+卡序锚定）复用环境圈现位 CollectAround(12m) 找步行市民 B 组双人卡（卡文缀"｜对：B处境"），
    /// LLM 对该卡输出 a/b 两句各 ≤12 字的对话；收炉注入 BubbleTheaterSystem.InjectPairPlay 当即席剧开播
    /// （预绑 A/B 真人锚点，CurrentAnchor 严格串行——A 句 HoldFor 播完泡消失、B 句再接，剧场同款"你一句我一句"）；
    /// 拒收（演员离场/离屏/满员/剧场开关关）→ 只留 A 句降级独白入池；凑不到 B 天然降级独白；车辆卡保持独白。
    /// §12 #69 写法规格签（2026-09-16 玩家拍板，三变体实验定案）：每张处境卡（人卡+车卡）尾缀系统分配
    /// 规格"｜写：句型｜情：情绪"（ChatterSpec 确定性抽签：炉计数+卡序锚定，同炉同签顺延去重）——
    /// 多样性不靠模型自觉；固定头已删【样子】内容例句（镜像不实锤、句长锚功能移交规格签）。
    /// §12 #71 场景签（2026-09-17 玩家拍板）：Indoor 卡 60% 掷签贴景——卡文缀"｜景：场景词｜话核：词"
    /// （SceneWords 执行层写死词表：服务/公共建筑细分，商业走 #62 业态、住宅无词）；贴景卡情绪签从
    /// 场景推荐子集抽（医院→烦/疼/急/麻木、小学→乐/烦/馋/困…），不贴的走日常套；街上卡不缀场景签
    /// =自由发挥型（玩家原话）。60/40 闸在执行层，prompt 只负责服从。
    ///
    /// 节拍锚游戏时间（#48：与信息流同尺——暂停=零成本、倍速=生成消费同速放大）：
    /// Now = SimulationSystem.frameIndex（模拟 tick，暂停即停走），游戏分钟 = TicksPerHour/60
    /// （TicksPerHour = TimeSystem.kTicksPerDay/24，EventNewsSystem/EventChainSystem 同款先例）。
    /// 更新间隔 128 帧只是时刻表检查粒度（2 的幂铁律，AGENTS.md 实锤坑），真实节拍由
    /// m_NextForgeAt 游戏时刻控制：开炉后定下一炉 = Now + (3|4|5) 游戏分钟（炉计数轮换，确定性）。
    ///
    /// 闸门（§12 #49 起独立于 feedMode——气泡不看面板也在显示，收面板不该退回占位话）：
    /// 设置页"气泡 AI 闲聊"开关（默认开，关=零 token）+ MUTE 静默（连 prompt 都不拼，零成本）
    /// + 网关可用 + 无在飞。在飞标志 + 墙钟 TTL+60s 兜底解锁（S3 同款：网关过期丢弃不回包，绝不能死等）。
    ///
    /// 结果路由：本炉发快轨网关（§12 #59 快慢双轨——闲聊炉量大句短，thinking 关省 token，
    /// 是快轨 V1 唯一搬家户），结果由 LlmResultPumpSystem 统一出队（快慢两网关都泵），
    /// 按 requestId "chatter:" 前缀经 ContentDirectorSystem.RouteResult 转交 OnChatterResult
    /// （"ad:"→ShopAdSystem 同款先例——本系统不能自己 TryDequeueResult，多消费者会互相偷包）。
    ///
    /// 读侧纪律：只读 CitizenPoolSystem.Entries / TopicRadarSystem.Latest / ContentDirectorSystem.Topics
    /// （均主线程只读视图）；自身市民查询仅做空城/主菜单闸（RequireForUpdate），只读不写。
    /// </summary>
    public partial class BubbleChatterSystem : GameSystemBase
    {
        private const double k_ForgeTtl = 300;  // 在飞请求 TTL（秒）：低频补给宁缺毋滥（S3 同值）
        private const int k_MinCards = 12;      // 每炉处境卡 12-16 张（炉计数取模确定性变化）×每卡 2-3 句（§12 #53 一次性供给侧加产）
        private const int k_MinVehicleCards = 3; // 每炉 Vehicle 场合保底卡数（池里有才保——场合供给侧保底，治车载泡被吃回归）
        private const int k_MinWalkCards = 3;    // 每炉 Walk 场合保底卡数（同上）
        private const float k_PairRadius = 12f;  // 配对半径（§12 #63：Walk 卡身边 12m 内找步行 B——擦肩/并肩能搭上话的距离）

        private EntityQuery m_CitizenQuery = default!;
        private EntityQuery m_VehicleQuery = default!; // 车卡源（载具本体采样——司机多是过境/服务人口，市民池天然车 0，[Pool] 日志实锤）
        private EntityQuery m_SelQuery = default!;     // §12 #63 2B①：玩家选中的车（SelectionInfo+Car，锁定通道一）
        private EntityQuery m_FollowedQuery = default!; // §12 #63 2B①：玩家跟随的市民（Followed 标记，锁定通道二）
        private TopicRadarSystem m_Radar = default!;
        private CitizenPoolSystem m_CitizenPool = default!;
        private ContentDirectorSystem m_Director = default!; // 话题库持有方（别重复造，配题抽同一货架）
        private SimulationSystem m_SimulationSystem = default!;
        private EnvironmentDigestSystem m_Environment = default!; // S6 环境圈摘要：组炉时逐卡查"旁边有什么"
        private Game.UI.NameSystem? m_NameSystem;           // 惰性：车卡目的地真名层（§12 #62；拿不到=类别词兜底）
        private BubbleTheaterSystem? m_Theater;             // 惰性：收炉注入即席剧（§12 #63 形态修正 2026-09-17；主菜单世界可能不存在）

        private readonly Content.BubbleSnippetPool m_Pool = new();
        // §12 #72 二批③ 宠物闯祸发牌词表（执行层——防"全是叼肉"实锤预防；组卡按炉计数+卡序轮转）
        private static readonly string[] k_PetMischiefs =
            { "叼肉", "拆家", "打翻花盆", "偷鱼", "追尾巴", "越狱", "跟邻居猫打架" };
        // §12 #72 二批③ 萌宠负向过滤词表（收炉侧，与 PromptEval 猫密度口径同源）
        private static readonly string[] k_PetWords = { "猫", "狗", "宠物", "喵", "汪" };
        private string m_Head = "";
        private uint m_ForgeCount;            // 炉计数：配题 seed / 卡数 jitter / 3-4-5 分钟轮换的锚
        private uint m_NextForgeAt;           // 下一炉游戏时刻（tick）
        private bool m_ClockInitialized;
        private bool m_ForgePending;          // 在飞标志（同时在飞最多一炉）
        private DateTime m_ForgeSince;        // 发炉墙钟（UTC）：网关过期丢弃不回包，TTL+60s 兜底解锁
        private readonly List<string> m_CurrentZones = new(); // 在飞炉的话题分区（与处境卡序对齐，收炉按 card 回填 Zone 用）
        private readonly List<Content.BubbleOccasion> m_CurrentOccasions = new(); // 在飞炉的场合（与处境卡序对齐，§12 #60 刀①执行层盖章，收炉按 card 回填）
        private readonly List<(string NameA, string NameB, Entity A, Entity B)?> m_CurrentPairs = new(); // 在飞炉的配对（与处境卡序对齐，§12 #63：null=该卡独白；收炉注入即席剧用——串行双泡要真人实体，2026-09-17 形态修正后带 A/B 实体）

        /// <summary>气泡片段池（S5 展示层取泡口；主线程只读）。</summary>
        public Content.BubbleSnippetPool Snippets => m_Pool;

        protected override void OnCreate()
        {
            base.OnCreate();
            // 空城/主菜单闸：有市民才更新（RequireForUpdate 空表即整系统静默，零成本）
            m_CitizenQuery = GetEntityQuery(
                ComponentType.ReadOnly<Citizen>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            RequireForUpdate(m_CitizenQuery);
            // 车卡源查询（不加 RequireForUpdate：没车的城也得产人卡，只闸市民查询）
            m_VehicleQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Vehicles.Vehicle>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            // §12 #63 2B① 车辆长对话闸·锁定双通道（spike docs/spikes/2026-09-20-vehicle-dialog-spike.md）：
            // 选中=SelectionInfo+Car（实体侧选中标记）；跟随=Followed 市民（经 CurrentVehicle 落车）
            m_SelQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Tools.SelectionInfo>(),
                ComponentType.ReadOnly<Game.Vehicles.Car>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_FollowedQuery = GetEntityQuery(
                ComponentType.ReadOnly<Citizen>(),
                ComponentType.ReadOnly<Game.Citizens.Followed>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_Radar = World.GetOrCreateSystemManaged<TopicRadarSystem>();
            m_CitizenPool = World.GetOrCreateSystemManaged<CitizenPoolSystem>();
            m_Director = World.GetOrCreateSystemManaged<ContentDirectorSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Environment = World.GetOrCreateSystemManaged<EnvironmentDigestSystem>();
            m_Head = Content.PromptBuilder.BuildChatterHead(); // 固定头拼一次缓存复用（逐字节稳定纪律）
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 128; // 2 的幂；只是时刻表检查粒度

        private uint Now => (uint)m_SimulationSystem.frameIndex;
        private static uint TicksPerHour => (uint)Math.Max(1, TimeSystem.kTicksPerDay / 24);
        private static uint TicksPerMinute => Math.Max(1u, TicksPerHour / 60u);

        protected override void OnUpdate()
        {
            if (!m_ClockInitialized)
            {
                m_NextForgeAt = Now; // 进城第一拍即可开炉（池是空的）；初始化前 Now 未必有意义
                m_ClockInitialized = true;
            }

            // 在飞兜底解锁：网关过期丢弃不回包（S3 话题炉同款墙钟兜底）
            if (m_ForgePending && (DateTime.UtcNow - m_ForgeSince).TotalSeconds > k_ForgeTtl + 60)
                m_ForgePending = false;

            if ((int)(Now - m_NextForgeAt) < 0)
                return; // 还没到点（uint 差值比较，tick 回绕安全）
            if (m_ForgePending || Mod.FastGateway == null || Llm.CliGateway.Mute)
                return; // MUTE 静默零成本：连 prompt 都不拼（MUTE 是两轨共用的静态总闸）

            // 开关闸（§12 #49）：气泡不看面板也在显示，故不随 feedMode 停——独立开关，默认开；关=零 token
            if (!Content.ModSettings.BubbleChatterEnabled)
                return;

            var snapshot = m_Radar.Latest;
            if (snapshot.Citizens == 0)
                return; // 雷达还没采到样

            // 处境卡抽样：10-14 张，跨步铺满池子（炉计数锚定，确定性——同炉次+同池状态必同批卡）
            var entries = m_CitizenPool.Entries;
            if (entries.Count == 0)
                return; // 市民池还没采到（开局）；m_NextForgeAt 不动，下拍再试
            var count = Math.Min(k_MinCards + (int)(m_ForgeCount % 5), entries.Count);
            // 先定人后组卡：场合供给侧保底（车载/步行锚点需求在街面，市民池分布夜晚室内占大头——
            // 刀①拆除 Any 桥梁后 Vehicle/Walk 片段被一次性消耗饿死=车载泡全沉默，保底详见 EnsureOccasionSupply）
            var picked = new List<CitizenContext>(count);
            var stride = Math.Max(1, entries.Count / count);
            var start = (int)(m_ForgeCount % (uint)entries.Count);
            for (int k = 0; k < count; k++)
                picked.Add(entries[(start + k * stride) % entries.Count]);
            EnsureOccasionSupply(entries, picked, m_ForgeCount);
            var cards = new List<string>(count);
            var topics = new List<string>(count);
            m_CurrentZones.Clear();
            m_CurrentOccasions.Clear();
            m_CurrentPairs.Clear();
            // §12 #63 配对：本炉已选市民集合——B 不得与炉内任何卡撞人（同一市民不能既独白又对话）
            var pickedSet = new HashSet<Entity>();
            for (int k = 0; k < picked.Count; k++)
                pickedSet.Add(picked[k].Entity);
            var digested = 0; // 本炉带环境摘要的卡数（[环境圈] 每炉一行计数用）
            var pairCardNos = new List<int>(); // 本炉配对成功的双人卡号（1 起，与卡序对齐；prompt 点名锚定+开炉日志用）
            var usedSpecs = new HashSet<(int Shape, int Mood, int Voice)>(); // §12 #69/#72：本炉已占规格签组合（同炉同签顺延去重；#72 起三元=写×情×口）
            var petAssigned = false; // §12 #72 二批③：本炉宠物闯祸签已发牌（稀有档全炉 ≤1）
            for (int k = 0; k < picked.Count; k++)
            {
                var entry = picked[k];
                // S6 环境圈摘要（§12 #48 场景为调料）：对卡的市民实体现位查一次 40m 半径，
                // 非空则缀"｜旁边：X、Y"。每炉查询次数=卡片数（≤14），3-5 游戏分钟一炉，不进热路径
                var card = entry.Context;
                var pos = CitizenPosition(entry.Entity);
                if (pos.HasValue)
                {
                    var digest = m_Environment.BuildDigest(pos.Value);
                    if (digest.Length > 0)
                    {
                        card += "｜旁边：" + digest;
                        digested++;
                    }
                }
                // §12 #63 对话场景卡：Walk 卡约 60% 槽位尝试配对（炉计数+卡序锚定，确定性）——
                // 复用上面环境圈已解出的现位做圆心（成本零新增数量级），凑不到 B 天然降级独白（不硬凑）
                (string NameA, string NameB, Entity A, Entity B)? pair = null;
                if (entry.Occasion == Content.BubbleOccasion.Walk && pos.HasValue
                    && (m_ForgeCount + (uint)k) % 5u < 3u)
                {
                    var b = TryPairWalker(pos.Value, entry.Entity, pickedSet, m_ForgeCount + (uint)k);
                    if (b.HasValue)
                    {
                        card += "｜对：" + b.Value.Card;
                        pair = (entry.Name, b.Value.Name, entry.Entity, b.Value.B); // 2026-09-17 形态修正：收炉注入即席剧要真人实体
                        pairCardNos.Add(k + 1); // 卡号 1 起，与 prompt 卡序对齐
                    }
                }
                // §12 #71 场景签 + #72 二批：景签闸 60%→30%（3:7——三成贴话题且必须长在关系里，
                // 七成关系日常）+ 微处境签（住宅/医院/商店/中学"手头正在干的事"）+ 宠物闯祸签（全炉 ≤1）
                string[]? sceneMoods = null;
                var indoorBld = entry.Occasion == Content.BubbleOccasion.Indoor
                                && EntityManager.HasComponent<CurrentBuilding>(entry.Entity)
                    ? EntityManager.GetComponentData<CurrentBuilding>(entry.Entity).m_CurrentBuilding
                    : Entity.Null;
                if (indoorBld != Entity.Null)
                {
                    if ((m_ForgeCount + (uint)k) % 10u < 3u)
                    {
                        var row = SceneWords.Of(EntityManager, indoorBld);
                        if (row != null)
                        {
                            card += "｜景：" + row.Word + "｜话核：" + SceneWords.PickCores(row, m_ForgeCount, k);
                            sceneMoods = row.Moods;
                        }
                    }
                    var micro = MicroSituations.Pick(EntityManager, indoorBld, m_ForgeCount + (uint)k, k);
                    if (micro != null)
                        card += "｜微：" + micro;
                    if (!petAssigned
                        && CitizenPoolSystem.ClassifyBuilding(EntityManager, indoorBld) == "住宅区"
                        && (m_ForgeCount + (uint)k) % 4u == 0u)
                    {
                        card += "｜事：宠物闯祸（" + k_PetMischiefs[(int)((m_ForgeCount + (uint)k) % (uint)k_PetMischiefs.Length)] + "）";
                        petAssigned = true;
                    }
                }
                // §12 #69/#72 写法规格签：卡尾缀"｜写：句型｜情：情绪｜口：口吻"（ChatterSpec 执行层确定性抽签——
                // 炉计数+卡序锚定+同炉同签顺延去重，LLM 只服从不参与分配；口吻由性格签加权
                // （按人锚：实体 Index 哈希，碎嘴→复读反问连发高权/闷葫芦→省略号高权…，不上卡面）；
                // §12 #71 联动：贴景卡情绪从场景推荐子集抽，其余走日常套）
                var personality = Content.ChatterSpec.PersonalityOf(entry.Entity.Index);
                card += sceneMoods != null
                    ? Content.ChatterSpec.TagFor(m_ForgeCount, k, usedSpecs, personality, sceneMoods)
                    : Content.ChatterSpec.TagFor(m_ForgeCount, k, usedSpecs, personality);
                cards.Add(card);
                m_CurrentPairs.Add(pair);
                m_CurrentOccasions.Add(entry.Occasion); // 场合随卡盖章（§12 #60 刀①：采样时已确定，收炉按 card 回填）
                // 弱卡配强题（刀③）：低熵卡避开萌宠/沙雕万能安全区，否则模型必逃中文语料最安全题材
                var topic = m_Director.Topics.TopicFor(m_ForgeCount, k, avoidSafeZones: IsWeakCard(card));
                topics.Add(topic);
                m_CurrentZones.Add(ZoneOf(topic)); // 分区回填备收炉按 card 对齐（执行层查表，不赌模型复述）
            }
            // 车卡（车载泡根治 v2：车里多是过境/服务司机，市民池天然车 0——[Pool] 日志实锤连续车 0，
            // 人卡保底找不到候选；车锚的声源必须是车自己：从载具本体采样组卡，场合恒 Vehicle）
            AppendVehicleCards(cards, topics, ref digested, usedSpecs, pairCardNos);

            m_Pool.CurrentCycle = m_ForgeCount; // BornCycle 基准锚本炉
            var rumorsNow = Content.CityRumors.Recent(3); // 刀②城市记忆：最新 3 条传闻当话料
            var prompt = Content.PromptBuilder.BuildChatterPrompt(m_Head, snapshot, cards, topics, rumorsNow, pairCardNos);
            Mod.FastGateway!.Enqueue(new Llm.CliRequest(prompt, Llm.CliPriority.Low, k_ForgeTtl, "chatter:" + m_ForgeCount)); // 快轨（§12 #59）
            m_ForgePending = true;
            m_ForgeSince = DateTime.UtcNow;
            var occTally = TallyOccasions();
            Mod.Log.Info($"[闲聊炉] 开炉：处境卡 {count} 张（走{occTally[1]}/车{occTally[2]}/室{occTally[3]}/通{occTally[0]}，配对 {pairCardNos.Count} 对，第 {m_ForgeCount + 1} 炉，池存 {m_Pool.Count}）");
            if (rumorsNow.Count > 0)
                Mod.Log.Info($"[闲聊炉] 本炉传闻：{string.Join(" / ", rumorsNow)}"); // 城市记忆可观测性：直接看到它在干活
            Mod.Log.Info($"[环境圈] 本炉摘要：{digested} 条非空（共 {count} 卡）"); // 每炉最多一行计数（首炉样例行在 EnvironmentDigestSystem）

            m_ForgeCount++;
            m_NextForgeAt = Now + (3u + m_ForgeCount % 3u) * TicksPerMinute; // 3-4-5 游戏分钟轮换
        }

        /// <summary>
        /// 闲聊炉结果处理（ContentDirectorSystem 按 "chatter:" 前缀转交）：
        /// JSONL salvage 解析（坏行跳过计数）→ 场合/话题分区按 card 号逐条回填（§12 #60 刀①：
        /// 场合采样时已盖章、分区执行层查表，模型只报归属；card 缺失/越界的孤儿行落 Any/留空，
        /// 不再整批连坐）→ 入 BubbleSnippetPool（池内同文本去重）。
        /// §12 #63 对话条（2026-09-17 形态修正，玩家三次实机反馈）：对卡行（a/b 双全）且该卡确为配对卡 →
        /// **注入 BubbleTheaterSystem 当即席剧开播**（串行双泡：A 说完泡消失 B 再接，预绑真人锚点）——
        /// 单泡双行拼装形态废除（ComposeDialogue 退役）；剧场拒收（演员离场/离屏/满员/开关关）→
        /// 只留 A 句降级独白入池（不硬演、不浪费 token）；配对缺失（模型给独白卡写了 a/b）有 text 落独白。
        /// 失败只记日志不致命，下一炉自然会再产。
        /// </summary>
        public void OnChatterResult(Llm.CliCompletedResult r)
        {
            m_ForgePending = false;
            if (!r.Result.Success)
            {
                Mod.Log.Info($"[闲聊炉] 一炉失败：{r.Result.Error}（下节拍再试）");
                return;
            }
            var parsed = Content.BubbleSnippetPool.ParseBatch(r.Result.Text, out var skipped);
            var added = 0;
            var orphan = 0;
            var dialogues = 0;
            // §12 #63 快轨对账计数（2026-09-15 实锤 V3 无 thinking 对卡 schema 跟随不稳：漏产/超产）：
            // 配对卡收到的合规 a/b 行 / 配对卡收到的独白行（schema 未跟随倾向）/ 非配对卡收到 a/b 行（越界超产）/ 拼装超 40 丢
            var pairCards = 0;
            for (int i = 0; i < m_CurrentPairs.Count; i++)
                if (m_CurrentPairs[i].HasValue)
                    pairCards++;
            var pairRows = 0;
            var pairMonoRows = 0;
            var strayDialogueRows = 0;
            var playRejected = 0; // 即席剧拒收数（降级 A 句独白；2026-09-17 形态修正后替代原"拼装超40"计数位）
            foreach (var p in parsed)
            {
                var occasion = Content.BubbleOccasion.Any;
                string? zone = null;
                (string NameA, string NameB, Entity A, Entity B)? pair = null;
                if (p.Card >= 1 && p.Card <= m_CurrentOccasions.Count)
                {
                    occasion = m_CurrentOccasions[p.Card - 1];
                    zone = m_CurrentZones[p.Card - 1];
                    pair = m_CurrentPairs[p.Card - 1];
                }
                else
                {
                    orphan++;
                }
                // 对卡行 + 该卡确为配对卡 → 注入剧场当即席剧（串行双泡）；拒收 → 只留 A 句降级独白；
                // 配对缺失（模型给独白卡写了 a/b）→ 有 text 落独白
                string? text = null;
                if (p.IsDialogue && pair.HasValue)
                {
                    pairRows++;
                    var pv = pair.Value;
                    if (m_Theater == null)
                        m_Theater = World.GetExistingSystemManaged<BubbleTheaterSystem>();
                    if (m_Theater != null && m_Theater.InjectPairPlay(pv.A, pv.NameA, p.A!, pv.B, pv.NameB, p.B!))
                    {
                        dialogues++; // 即席剧开播成功——对话不走片段池，下一条
                        continue;
                    }
                    playRejected++;
                    text = p.A; // 降级：只留 A 句当独白入池（名字不进池——池片段不带署名传统）
                }
                else if (p.IsDialogue)
                {
                    strayDialogueRows++; // a/b 写给非配对卡：越界超产，无名字可用必落丢弃/独白回退
                }
                else if (pair.HasValue)
                {
                    pairMonoRows++; // 配对卡收到独白行：schema 未跟随（模型把它当独白卡写了）
                }
                text ??= p.Text;
                if (text == null)
                {
                    skipped++; // 对卡行无 text 可回退（a b 写给非配对卡且缺 text）——与解析丢弃同口径计数
                    continue;
                }
                // §12 #72 二批③ 萌宠负向过滤（负向进过滤不进 prompt）：含萌宠词+无（）拍+非对话条=丢
                // （"光蹲那被观察"——玩家原话判废；（）拍是情节不是观察，放行）
                if (IsPassivePetLine(text))
                {
                    skipped++;
                    continue;
                }
                if (m_Pool.Add(text, occasion, zone))
                    added++;
            }
            Mod.Log.Info($"[闲聊炉] 入库 {added} 条（对话 {dialogues} 条，解析丢 {skipped} 条，去重丢 {parsed.Count - added} 条，无归属 {orphan} 条，池现 {m_Pool.Count} 条）");
            // §12 #63 对话缺口诊断（轻量一行，不逐行 dump）：配对>0 但开播对话<配对数时粗分原因——
            // 下次实机直接定位是模型没写 a/b（schema 未跟随/漏产）还是剧场侧拒收（演员离场/离屏/满员）
            if (pairCards > 0 && dialogues < pairCards)
                Mod.Log.Info($"[闲聊炉] 对话缺口：配对 {pairCards} 对开播 {dialogues} 条——对卡合规 a/b 行 {pairRows}、对卡独白行 {pairMonoRows}（schema 未跟随倾向）、越界 a/b 行 {strayDialogueRows}（非对卡超产）、剧场拒收 {playRejected}（降级独白）、全炉解析丢 {skipped}");
        }

        /// <summary>题面 → 话题分区（TopicReservoir.Entries 线性查表，取首个同题面条目；查不到返回 ""）。</summary>
        private string ZoneOf(string topic)
        {
            var entries = m_Director.Topics.Entries;
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Topic == topic)
                    return entries[i].Zone;
            return "";
        }

        /// <summary>萌宠负向过滤（§12 #72 二批③，负向进过滤不进 prompt）：含萌宠词+无（）拍+非对话条 → true 丢。
        /// 对话条（即席剧播放路径）与（）拍（"（那猫又在拆家）"是情节不是观察）放行。</summary>
        private static bool IsPassivePetLine(string text)
        {
            if (text.Contains("（"))
                return false;
            for (var i = 0; i < k_PetWords.Length; i++)
                if (text.Contains(k_PetWords[i]))
                    return true;
            return false;
        }

        /// <summary>场合供给侧保底：每炉 Walk 保 k_MinWalkCards 张（池里有才保，没有不硬造）。
        /// 根因链：生产端按市民池分布（夜晚室内占大头），消费端按锚点分布（街面人锚吃 Walk），
        /// 刀①拆除 Any 桥梁+§12 #53 一次性消耗下，Walk 片段入不敷出=人锚全落沉默泡。
        /// （Vehicle 不在这里保——市民池天然车 0，车卡改从载具本体采样，见 AppendVehicleCards。）
        /// 保底=确定性换坑：从炉计数锚定偏移顺找未选中的走路市民，替换室内槽位（室内是夜晚绝对多数派，换得起）。</summary>
        private static void EnsureOccasionSupply(IReadOnlyList<CitizenContext> entries, List<CitizenContext> picked, uint salt)
        {
            EnsureOccasion(entries, picked, salt + 7919u, Content.BubbleOccasion.Walk, k_MinWalkCards);
        }

        /// <summary>单场合保底：picked 里 occ 不足 min 时，用池里未选中的 occ 市民替换室内槽（锚定 salt 顺找，确定性）。</summary>
        private static void EnsureOccasion(IReadOnlyList<CitizenContext> entries, List<CitizenContext> picked,
                                           uint salt, Content.BubbleOccasion occ, int min)
        {
            var have = 0;
            var indoorSlots = 0;
            for (int i = 0; i < picked.Count; i++)
            {
                if (picked[i].Occasion == occ) have++;
                if (picked[i].Occasion == Content.BubbleOccasion.Indoor) indoorSlots++;
            }
            for (int i = 0; i < entries.Count && have < min && indoorSlots > 0; i++)
            {
                var cand = entries[(int)((salt + (uint)i) % (uint)entries.Count)];
                if (cand.Occasion != occ)
                    continue;
                var already = false;
                for (int j = 0; j < picked.Count; j++)
                    if (picked[j].Entity == cand.Entity) { already = true; break; }
                if (already)
                    continue;
                // 从 salt 锚定顺找一个室内槽换掉（确定性）
                for (var s = 0; s < picked.Count; s++)
                {
                    var slot = (int)(((uint)s + salt) % (uint)picked.Count);
                    if (picked[slot].Occasion == Content.BubbleOccasion.Indoor)
                    {
                        picked[slot] = cand;
                        have++;
                        indoorSlots--;
                        break;
                    }
                }
            }
        }

        /// <summary>本炉场合计数（开炉日志用，与 m_CurrentOccasions 对齐）。</summary>
        private int[] TallyOccasions()
        {
            var t = new int[4];
            for (int i = 0; i < m_CurrentOccasions.Count; i++)
                t[(int)m_CurrentOccasions[i]]++;
            return t;
        }

        /// <summary>车卡组卡追加（车载泡根治 v2）：从载具本体采样 ≤k_MinVehicleCards 张，场合恒 Vehicle。
        /// 只收四类民用载具（私家车/出租车/公交/货车——警车/垃圾车等服务车说话="空车说话"同款诡异，不收）；
        /// 跨步抽样+炉计数锚定（与人卡同款确定性）；车也吃 S6 环境圈摘要（Transform 现位直读，不进热路径）；
        /// 车卡同缀 §12 #69 规格签（usedSpecs 与人卡同一本炉去重集）。
        /// §12 #63 2B① 车辆长对话闸（修正注记，spike docs/spikes/2026-09-20-vehicle-dialog-spike.md）：
        /// 玩家"正在看这辆车"（锁定双通道=选中 SelectionInfo ∨ 跟随 Followed 市民落车）且乘员 ≥2（含司机）
        /// → 强制第一卡=车内双人卡（出租车 A=后座乘客 B=司机），对卡管线与人卡同款（pairCardNos 点名+
        /// 收炉注入即席剧，剧场侧车内共锚见 BubbleTheaterSystem.InjectPairPlay）；
        /// 锁定车仅司机 1 人 / 普通采样车 OccupantCount==1 → 卡文加"，在打电话"（独驾独白给个由头）。</summary>
        private void AppendVehicleCards(List<string> cards, List<string> topics, ref int digested,
                                        HashSet<(int Shape, int Mood, int Voice)> usedSpecs, List<int> pairCardNos)
        {
            m_NameSystem ??= World.GetExistingSystemManaged<Game.UI.NameSystem>(); // 锁定双人卡署名/目的地真名都要，提前解析
            var locked = FindLockedVehiclePair();
            if (locked.HasValue && m_NameSystem != null)
            {
                var (lv, lp, ld) = locked.Value;
                var nameA = lp != Entity.Null ? m_NameSystem.GetRenderedLabelName(lp) : null;
                var nameB = ld != Entity.Null ? m_NameSystem.GetRenderedLabelName(ld) : null;
                if (lp != Entity.Null && ld != Entity.Null
                    && !string.IsNullOrEmpty(nameA) && !string.IsNullOrEmpty(nameB))
                {
                    var taxi = EntityManager.HasComponent<Game.Vehicles.Taxi>(lv);
                    var card = (taxi ? "出租车乘客，坐在后座" : "私家车乘客，坐在车里")
                             + (taxi ? "｜对：出租车司机，在开车" : "｜对：私家车司机，在开车");
                    card += Content.ChatterSpec.TagFor(m_ForgeCount, 0, usedSpecs,
                        Content.ChatterSpec.PersonalityOf(lp.Index)); // 性格按乘客锚（与人卡同口径：实体 Index 哈希）
                    for (int i = 0; i < pairCardNos.Count; i++)
                        pairCardNos[i]++; // 人卡配对号顺延——锁定卡插队成第 1 卡（1 起卡号全 +1）
                    cards.Insert(0, card);
                    topics.Insert(0, m_Director.Topics.TopicFor(m_ForgeCount, 0, avoidSafeZones: false));
                    m_CurrentZones.Insert(0, ZoneOf(topics[0]));
                    m_CurrentOccasions.Insert(0, Content.BubbleOccasion.Vehicle);
                    m_CurrentPairs.Insert(0, (nameA, nameB, lp, ld));
                    pairCardNos.Add(1);
                }
            }
            var arr = m_VehicleQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            var n = System.Math.Min(k_MinVehicleCards, arr.Length);
            var stride = System.Math.Max(1, arr.Length / System.Math.Max(1, n));
            var start = arr.Length > 0 ? (int)(m_ForgeCount % (uint)arr.Length) : 0;
            var added = 0;
            for (int i = start; i < arr.Length && added < n; i += stride)
            {
                var card = DescribeVehicle(arr[i], out var parked);
                if (card == null)
                    continue; // 服务车/特种车：v1 不收
                if (OccupantCount(arr[i]) == 1)
                    card += "，在打电话"; // §12 #63 2B①：独驾（司机+0 乘客）=打电话卡——独白给个由头
                if (EntityManager.HasComponent<Transform>(arr[i]))
                {
                    var digest = m_Environment.BuildDigest(EntityManager.GetComponentData<Transform>(arr[i]).m_Position);
                    if (digest.Length > 0)
                    {
                        card += "｜旁边：" + digest;
                        digested++;
                    }
                }
                card += Content.ChatterSpec.TagFor(m_ForgeCount, cards.Count, usedSpecs,
                    Content.ChatterSpec.PersonalityOf(arr[i].Index)); // §12 #72 规格签（车卡性格按车实体哈希，与人卡同口径）
                cards.Add(card);
                m_CurrentOccasions.Add(Content.BubbleOccasion.Vehicle); // 车卡场合恒 Vehicle（采样时已确定）
                m_CurrentPairs.Add(null); // 车卡保持独白（§12 #63：配对只限 Walk 人卡；平行表与 cards 等长对齐）
                var topic = m_Director.Topics.TopicFor(m_ForgeCount, cards.Count - 1, avoidSafeZones: parked); // 停车=弱卡（无处可去），避开萌宠/沙雕
                topics.Add(topic);
                m_CurrentZones.Add(ZoneOf(topic));
                added++;
            }
            arr.Dispose();
        }

        /// <summary>车卡文案："货车司机，送货路上（去工业区）" / "公交司机，在线路上跑" / "私家车司机，停在路边"。
        /// 非四类民用载具 → null（不收）。parked=是否停着（停车卡判弱，配题避开安全区）。</summary>
        private string? DescribeVehicle(Entity v, out bool parked)
        {
            parked = EntityManager.HasComponent<Game.Vehicles.ParkedCar>(v);
            string who;
            string moving;
            if (EntityManager.HasComponent<Game.Vehicles.Taxi>(v)) { who = "出租车司机"; moving = "街上兜客"; }
            else if (EntityManager.HasComponent<Game.Vehicles.PublicTransport>(v)) { who = "公交司机"; moving = "在线路上跑"; }
            else if (EntityManager.HasComponent<Game.Vehicles.DeliveryTruck>(v)) { who = "货车司机"; moving = "送货路上"; }
            else if (EntityManager.HasComponent<Game.Vehicles.PersonalCar>(v)) { who = "私家车司机"; moving = "开车赶路"; }
            else return null;
            if (parked)
                return who + "，停在路边";
            m_NameSystem ??= World.GetExistingSystemManaged<Game.UI.NameSystem>();
            var dest = CitizenPoolSystem.DestinationPlace(EntityManager, v, m_NameSystem); // §12 #62 真名层："（去「胖东来」）"
            return dest != null ? $"{who}，{moving}（去{dest}）" : $"{who}，{moving}";
        }

        /// <summary>§12 #63 2B① 车辆长对话闸·锁定双通道（修正注记；spike 实锤：Followed/SelectionInfo 均实体侧标记，
        /// UI bindings 有 followedCitizens$ 同款语义）：玩家"正在看这辆车"= 玩家选中它（SelectionInfo+Car 查询首辆）
        /// ∨ 玩家跟随的市民坐在里面（Followed 查询 → CurrentVehicle 落车）。
        /// 命中后解析乘员：司机=载具 Game.Vehicles.Controller.m_Controller（creature agent）经 Resident 回指市民，
        /// 乘客=Game.Vehicles.Passenger buffer 逐个经 Resident 回指（取第一位——双人卡只组一对，出租车=后座那位）。
        /// 返回 （车， 乘客市民， 司机市民)；乘客/司机任一回指不到 → 对应字段 Entity.Null（调用方判：双全才出双人卡）。
        /// 双通道都空 / 车上 0 人 → null（走普通采样，天然降级不硬凑）。
        /// 主线程组炉级低频调用（每炉一次，查询恒小表），禁入热路径。</summary>
        private (Entity Vehicle, Entity Passenger, Entity Driver)? FindLockedVehiclePair()
        {
            var v = LockedVehicle();
            if (v == Entity.Null)
                return null;
            var driver = Entity.Null;
            if (EntityManager.HasComponent<Game.Vehicles.Controller>(v))
            {
                var c = EntityManager.GetComponentData<Game.Vehicles.Controller>(v).m_Controller;
                if (c != Entity.Null && EntityManager.HasComponent<Game.Creatures.Resident>(c))
                    driver = EntityManager.GetComponentData<Game.Creatures.Resident>(c).m_Citizen;
            }
            var passenger = Entity.Null;
            if (EntityManager.HasBuffer<Game.Vehicles.Passenger>(v))
            {
                var buf = EntityManager.GetBuffer<Game.Vehicles.Passenger>(v);
                for (int i = 0; i < buf.Length; i++)
                {
                    var agent = buf[i].m_Passenger;
                    if (agent != Entity.Null && EntityManager.HasComponent<Game.Creatures.Resident>(agent))
                    {
                        var cit = EntityManager.GetComponentData<Game.Creatures.Resident>(agent).m_Citizen;
                        if (cit != Entity.Null)
                        {
                            passenger = cit;
                            break;
                        }
                    }
                }
            }
            if (driver == Entity.Null && passenger == Entity.Null)
                return null; // 0 人（空车/乘员回指全灭）→ 普通采样（现司机卡口粮）
            return (v, passenger, driver);
        }

        /// <summary>锁定双通道解析：选中通道优先（m_SelQuery 首辆），其次跟随通道（Followed 市民的现车）。
        /// 都落空 → Entity.Null。</summary>
        private Entity LockedVehicle()
        {
            if (!m_SelQuery.IsEmptyIgnoreFilter)
            {
                var arr = m_SelQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
                var v = arr.Length > 0 ? arr[0] : Entity.Null;
                arr.Dispose();
                if (v != Entity.Null)
                    return v;
            }
            if (!m_FollowedQuery.IsEmptyIgnoreFilter)
            {
                var arr = m_FollowedQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
                for (int i = 0; i < arr.Length; i++)
                {
                    var v = VehicleOf(arr[i]);
                    if (v != Entity.Null)
                    {
                        arr.Dispose();
                        return v;
                    }
                }
                arr.Dispose();
            }
            return Entity.Null;
        }

        /// <summary>市民当前所乘载具（Game.Creatures.CurrentVehicle 挂市民实体上——不在车上即无此组件）；
        /// 读不到/实体已死 → Entity.Null。</summary>
        private Entity VehicleOf(Entity citizen)
        {
            if (!EntityManager.HasComponent<Game.Creatures.CurrentVehicle>(citizen))
                return Entity.Null;
            var v = EntityManager.GetComponentData<Game.Creatures.CurrentVehicle>(citizen).m_Vehicle;
            return v != Entity.Null && EntityManager.Exists(v) ? v : Entity.Null;
        }

        /// <summary>车上乘员数（含司机）：司机=Controller.m_Controller 非空按 1 算，乘客=Passenger buffer 长度。
        /// 只用于"独驾=打电话卡"判定（==1），不回指市民实体（组炉低频，HasBuffer 直读）。</summary>
        private int OccupantCount(Entity v)
        {
            var n = 0;
            if (EntityManager.HasComponent<Game.Vehicles.Controller>(v)
                && EntityManager.GetComponentData<Game.Vehicles.Controller>(v).m_Controller != Entity.Null)
                n = 1;
            if (EntityManager.HasBuffer<Game.Vehicles.Passenger>(v))
                n += EntityManager.GetBuffer<Game.Vehicles.Passenger>(v).Length;
            return n;
        }

        /// <summary>弱卡判定（刀③，启发式阈值待实机校准）：纯身份无处境/只"呆着"=低信息熵卡——
        /// 配题避开万能安全区（萌宠/沙雕），否则模型必逃中文语料最安全题材（猫灾的卡片侧成因）。</summary>
        private static bool IsWeakCard(string card)
            => card.IndexOf('，') < 0 || card.Contains("呆着");

        /// <summary>
        /// 市民现位（S6 环境圈圆心）：在建筑内 → 建筑位置（CurrentBuilding 仅室内挂，spike §1）；
        /// 在路上 → CurrentTransport 所指行人 agent/载具的位置（spike §3）；兜底市民自身 Transform
        /// （市民本体是逻辑实体一般没有，防版本差异留着）；实体已死/读不到 → null（该卡不缀摘要）。
        /// 全程 HasComponent 先行：池子采样到组炉之间市民可能已搬走（CitizenContext.Entity 是采样时快照）。
        /// </summary>
        private float3? CitizenPosition(Entity citizen)
        {
            if (citizen == Entity.Null || !EntityManager.Exists(citizen))
                return null;
            if (EntityManager.HasComponent<CurrentBuilding>(citizen))
            {
                var b = EntityManager.GetComponentData<CurrentBuilding>(citizen).m_CurrentBuilding;
                if (EntityManager.HasComponent<Transform>(b))
                    return EntityManager.GetComponentData<Transform>(b).m_Position;
                return null; // 建筑读不到位置（外部连接等）：不退市民自身坐标（可能不是现位）
            }
            if (EntityManager.HasComponent<CurrentTransport>(citizen))
            {
                var t = EntityManager.GetComponentData<CurrentTransport>(citizen).m_CurrentTransport;
                if (t != Entity.Null && EntityManager.HasComponent<Transform>(t))
                    return EntityManager.GetComponentData<Transform>(t).m_Position;
            }
            if (EntityManager.HasComponent<Transform>(citizen))
                return EntityManager.GetComponentData<Transform>(citizen).m_Position;
            return null;
        }

        /// <summary>
        /// 配对（§12 #63 对话场景卡；2026-09-17 形态修正：收炉注入即席剧串行双泡——B 的实体随卡留存，
        /// 名字只用于 prompt 卡文与剧场台词前缀）：在 A 现位 k_PairRadius 内找另一位步行市民 B，组双人卡。
        /// movers 滤 Human+Resident 回指市民（BubbleTheaterSystem.BindOutdoorRoster 先例）；
        /// B 排除口径=CitizenPoolSystem 采样同款（MovingAway/无名，DescribeCitizen 一支全含；
        /// 儿童自 §12 #66 起放行——气泡=说话不是发帖，孩子可以被配对搭讪）
        /// + 对话特有排除三条：A 自己、乘车市民（Game.Creatures.CurrentVehicle——乘车不算步行，
        /// BubbleWorldSpikeSystem 人查询同款）、本炉已选卡市民（一人只出一声）。
        /// 多候选取 salt（=炉计数+卡序）取模确定性选一（同炉次+同街况必同选）。
        /// 找不到/名字系统不可用 → null（天然降级独白，不硬凑）。
        /// 主线程组炉级低频调用（每炉 ≤ 人卡数 次四叉树半径查询），禁入热路径。
        /// </summary>
        private (Entity B, string Name, string Card)? TryPairWalker(float3 pos, Entity self, HashSet<Entity> pickedSet, uint salt)
        {
            m_NameSystem ??= World.GetExistingSystemManaged<Game.UI.NameSystem>();
            if (m_NameSystem == null)
                return null; // 名字是拼装硬需求（收炉名字前缀），拿不到则不配对
            var statics = new Unity.Collections.NativeList<Entity>(Unity.Collections.Allocator.Temp);
            var movers = new Unity.Collections.NativeList<Entity>(Unity.Collections.Allocator.Temp);
            m_Environment.CollectAround(pos, k_PairRadius, statics, movers);
            statics.Dispose(); // 配对只找人（movers）；statics 是 CollectAround 追加语义的副产物，收掉
            var cands = new List<(Entity B, string Name, string Card)>();
            for (int i = 0; i < movers.Length; i++)
            {
                var agent = movers[i];
                if (!EntityManager.HasComponent<Game.Creatures.Human>(agent)
                    || !EntityManager.HasComponent<Game.Creatures.Resident>(agent))
                    continue;
                var b = EntityManager.GetComponentData<Game.Creatures.Resident>(agent).m_Citizen;
                if (b == Entity.Null || b == self || pickedSet.Contains(b))
                    continue;
                if (EntityManager.HasComponent<Game.Creatures.CurrentVehicle>(b))
                    continue; // 乘车市民不算步行
                // MovingAway/无 Citizen 一支全含（池采样同一口径，一处定义别复制粘贴；儿童 §12 #66 起放行）；
                // 场合不回章——B 的卡文只当"对："段处境描述（moving 树行人 agent 即走路状态）
                var cardB = CitizenPoolSystem.DescribeCitizen(EntityManager, b, out _, m_NameSystem);
                if (cardB == null)
                    continue;
                var name = m_NameSystem.GetRenderedLabelName(b);
                if (string.IsNullOrEmpty(name))
                    continue; // 无名（池采样同款口径）
                cands.Add((b, name, cardB));
            }
            movers.Dispose();
            if (cands.Count == 0)
                return null;
            return cands[(int)(salt % (uint)cands.Count)];
        }
    }
}
