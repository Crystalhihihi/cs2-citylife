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
    /// §12 #63 对话场景卡：组炉时对 Walk 卡约 60% 槽位（炉计数+卡序锚定）复用环境圈现位 CollectAround(12m)
    /// 找步行市民 B 组双人卡（卡文缀"｜对：B处境"），LLM 对该卡输出 a/b 两句各 ≤12 字的对话，
    /// 收炉执行层拼"名字A：a\n名字B：b"单条 Text（渲染零改动），凑不到 B 天然降级独白；车辆卡保持独白。
    /// §12 #69 写法规格签（2026-09-16 玩家拍板，三变体实验定案）：每张处境卡（人卡+车卡）尾缀系统分配
    /// 规格"｜写：句型｜情：情绪"（ChatterSpec 确定性抽签：炉计数+卡序锚定，同炉同签顺延去重）——
    /// 多样性不靠模型自觉；固定头已删【样子】内容例句（镜像不实锤、句长锚功能移交规格签）。
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
        private TopicRadarSystem m_Radar = default!;
        private CitizenPoolSystem m_CitizenPool = default!;
        private ContentDirectorSystem m_Director = default!; // 话题库持有方（别重复造，配题抽同一货架）
        private SimulationSystem m_SimulationSystem = default!;
        private EnvironmentDigestSystem m_Environment = default!; // S6 环境圈摘要：组炉时逐卡查"旁边有什么"
        private Game.UI.NameSystem? m_NameSystem;           // 惰性：车卡目的地真名层（§12 #62；拿不到=类别词兜底）

        private readonly Content.BubbleSnippetPool m_Pool = new();
        private string m_Head = "";
        private uint m_ForgeCount;            // 炉计数：配题 seed / 卡数 jitter / 3-4-5 分钟轮换的锚
        private uint m_NextForgeAt;           // 下一炉游戏时刻（tick）
        private bool m_ClockInitialized;
        private bool m_ForgePending;          // 在飞标志（同时在飞最多一炉）
        private DateTime m_ForgeSince;        // 发炉墙钟（UTC）：网关过期丢弃不回包，TTL+60s 兜底解锁
        private readonly List<string> m_CurrentZones = new(); // 在飞炉的话题分区（与处境卡序对齐，收炉按 card 回填 Zone 用）
        private readonly List<Content.BubbleOccasion> m_CurrentOccasions = new(); // 在飞炉的场合（与处境卡序对齐，§12 #60 刀①执行层盖章，收炉按 card 回填）
        private readonly List<(string NameA, string NameB)?> m_CurrentPairs = new(); // 在飞炉的配对（与处境卡序对齐，§12 #63：null=该卡独白；收炉拼"名字A：a\n名字B：b"用）

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
            var usedSpecs = new HashSet<(int Shape, int Mood)>(); // §12 #69：本炉已占规格签组合（同炉同签顺延去重——实验"困得眼皮打架"×2 撞车实锤）
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
                (string NameA, string NameB)? pair = null;
                if (entry.Occasion == Content.BubbleOccasion.Walk && pos.HasValue
                    && (m_ForgeCount + (uint)k) % 5u < 3u)
                {
                    var b = TryPairWalker(pos.Value, entry.Entity, pickedSet, m_ForgeCount + (uint)k);
                    if (b.HasValue)
                    {
                        card += "｜对：" + b.Value.Card;
                        pair = (entry.Name, b.Value.Name);
                        pairCardNos.Add(k + 1); // 卡号 1 起，与 prompt 卡序对齐
                    }
                }
                // §12 #69 写法规格签：卡尾缀"｜写：句型｜情：情绪"（ChatterSpec 执行层确定性抽签，
                // 炉计数+卡序锚定+同炉同签顺延去重；LLM 只服从不参与分配——多样性不靠模型自觉）
                card += Content.ChatterSpec.TagFor(m_ForgeCount, k, usedSpecs);
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
            AppendVehicleCards(cards, topics, ref digested, usedSpecs);

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
        /// §12 #63 对话条：对卡行（a/b 双全）且该卡确为配对卡 → 执行层拼"名字A：a\n名字B：b"
        /// （名字前缀执行层加，LLM 不碰名字，单向阀门守住）；拼装超长/配对缺失有 text 回退独白。
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
            var composeOver = 0;
            foreach (var p in parsed)
            {
                var occasion = Content.BubbleOccasion.Any;
                string? zone = null;
                (string NameA, string NameB)? pair = null;
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
                // 对卡行 + 该卡确为配对卡 → 拼对话条；配对缺失（模型给独白卡写了 a/b）/拼装超长 → 有 text 落独白
                string? text = null;
                if (p.IsDialogue && pair.HasValue)
                {
                    pairRows++;
                    text = ComposeDialogue(pair.Value.NameA, p.A!, pair.Value.NameB, p.B!);
                    if (text != null)
                        dialogues++;
                    else
                        composeOver++;
                }
                else if (p.IsDialogue)
                {
                    strayDialogueRows++; // a/b 写给非配对卡：越界超产，无名字可拼必落丢弃/独白回退
                }
                else if (pair.HasValue)
                {
                    pairMonoRows++; // 配对卡收到独白行：schema 未跟随（模型把它当独白卡写了）
                }
                text ??= p.Text;
                if (text == null)
                {
                    skipped++; // 对卡行无 text 可回退（拼装超长/a b 写给非配对卡）——与解析丢弃同口径计数
                    continue;
                }
                if (m_Pool.Add(text, occasion, zone))
                    added++;
            }
            Mod.Log.Info($"[闲聊炉] 入库 {added} 条（对话 {dialogues} 条，解析丢 {skipped} 条，去重丢 {parsed.Count - added} 条，无归属 {orphan} 条，池现 {m_Pool.Count} 条）");
            // §12 #63 对话缺口诊断（轻量一行，不逐行 dump）：配对>0 但入库对话<配对数时粗分原因，
            // 下次实机直接定位是模型没写 a/b（schema 未跟随/漏产）还是执行层闸拦的（拼装超 40）
            if (pairCards > 0 && dialogues < pairCards)
                Mod.Log.Info($"[闲聊炉] 对话缺口：配对 {pairCards} 对入库 {dialogues} 条——对卡合规 a/b 行 {pairRows}、对卡独白行 {pairMonoRows}（schema 未跟随倾向）、越界 a/b 行 {strayDialogueRows}（非对卡超产）、拼装超40丢 {composeOver}、全炉解析丢 {skipped}");
        }

        /// <summary>
        /// 对话条拼装（§12 #63）："名字A：台词A\n名字B：台词B"——单条 Text 内嵌 \n 两行，
        /// 渲染零改动（WrapForBake 原生支持 \n）；名字前缀格式同剧场（BubbleTheaterSystem 全角冒号先例）。
        /// 防线：台词内嵌换行压成空格（JSON \n 解码脏数据防版式炸）；总长超 40（气泡排版硬顶，
        /// 与 ParseBatch 同尺）→ null，调用方有 text 回退独白、无则丢弃计数。
        /// </summary>
        private static string? ComposeDialogue(string nameA, string a, string nameB, string b)
        {
            var text = string.Concat(nameA, "：", a.Replace('\n', ' '), "\n", nameB, "：", b.Replace('\n', ' '));
            return text.Length <= 40 ? text : null;
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
        /// 车卡同缀 §12 #69 规格签（usedSpecs 与人卡同一本炉去重集）。</summary>
        private void AppendVehicleCards(List<string> cards, List<string> topics, ref int digested,
                                        HashSet<(int Shape, int Mood)> usedSpecs)
        {
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
                if (EntityManager.HasComponent<Transform>(arr[i]))
                {
                    var digest = m_Environment.BuildDigest(EntityManager.GetComponentData<Transform>(arr[i]).m_Position);
                    if (digest.Length > 0)
                    {
                        card += "｜旁边：" + digest;
                        digested++;
                    }
                }
                card += Content.ChatterSpec.TagFor(m_ForgeCount, cards.Count, usedSpecs); // §12 #69 规格签（卡序=当前尾位）
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
        /// 配对（§12 #63 对话场景卡）：在 A 现位 k_PairRadius 内找另一位步行市民 B，组双人卡。
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
