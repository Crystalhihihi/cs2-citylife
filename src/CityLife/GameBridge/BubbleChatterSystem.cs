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
    /// 结果路由：网关结果由 ContentDirectorSystem 统一出队，按 requestId "chatter:" 前缀转交
    /// OnChatterResult（"ad:"→ShopAdSystem 同款先例——本系统不能自己 TryDequeueResult，
    /// 两个消费者轮询同一队列会互相偷包）。
    ///
    /// 读侧纪律：只读 CitizenPoolSystem.Entries / TopicRadarSystem.Latest / ContentDirectorSystem.Topics
    /// （均主线程只读视图）；自身市民查询仅做空城/主菜单闸（RequireForUpdate），只读不写。
    /// </summary>
    public partial class BubbleChatterSystem : GameSystemBase
    {
        private const double k_ForgeTtl = 300;  // 在飞请求 TTL（秒）：低频补给宁缺毋滥（S3 同值）
        private const int k_MinCards = 12;      // 每炉处境卡 12-16 张（炉计数取模确定性变化）×每卡 2-3 句（§12 #53 一次性供给侧加产）

        private EntityQuery m_CitizenQuery = default!;
        private TopicRadarSystem m_Radar = default!;
        private CitizenPoolSystem m_CitizenPool = default!;
        private ContentDirectorSystem m_Director = default!; // 话题库持有方（别重复造，配题抽同一货架）
        private SimulationSystem m_SimulationSystem = default!;
        private EnvironmentDigestSystem m_Environment = default!; // S6 环境圈摘要：组炉时逐卡查"旁边有什么"

        private readonly Content.BubbleSnippetPool m_Pool = new();
        private string m_Head = "";
        private uint m_ForgeCount;            // 炉计数：配题 seed / 卡数 jitter / 3-4-5 分钟轮换的锚
        private uint m_NextForgeAt;           // 下一炉游戏时刻（tick）
        private bool m_ClockInitialized;
        private bool m_ForgePending;          // 在飞标志（同时在飞最多一炉）
        private DateTime m_ForgeSince;        // 发炉墙钟（UTC）：网关过期丢弃不回包，TTL+60s 兜底解锁
        private readonly List<string> m_CurrentZones = new(); // 在飞炉的话题分区（与处境卡序对齐，收炉回填 Zone 用）

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
            if (m_ForgePending || Mod.Gateway == null || Llm.CliGateway.Mute)
                return; // MUTE 静默零成本：连 prompt 都不拼

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
            var cards = new List<string>(count);
            var topics = new List<string>(count);
            m_CurrentZones.Clear();
            var stride = Math.Max(1, entries.Count / count);
            var start = (int)(m_ForgeCount % (uint)entries.Count);
            var digested = 0; // 本炉带环境摘要的卡数（[环境圈] 每炉一行计数用）
            for (int k = 0; k < count; k++)
            {
                var entry = entries[(start + k * stride) % entries.Count];
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
                cards.Add(card);
                var topic = m_Director.Topics.TopicFor(m_ForgeCount, k); // 每张配一题（话题库分区轮转+新鲜度加权）
                topics.Add(topic);
                m_CurrentZones.Add(ZoneOf(topic)); // 分区回填备收炉对齐（执行层查表，不赌模型复述）
            }

            m_Pool.CurrentCycle = m_ForgeCount; // BornCycle 基准锚本炉
            var prompt = Content.PromptBuilder.BuildChatterPrompt(m_Head, snapshot, cards, topics);
            Mod.Gateway.Enqueue(new Llm.CliRequest(prompt, Llm.CliPriority.Low, k_ForgeTtl, "chatter:" + m_ForgeCount));
            m_ForgePending = true;
            m_ForgeSince = DateTime.UtcNow;
            Mod.Log.Info($"[闲聊炉] 开炉：处境卡 {count} 张（第 {m_ForgeCount + 1} 炉，池存 {m_Pool.Count}）");
            Mod.Log.Info($"[环境圈] 本炉摘要：{digested} 条非空（共 {count} 卡）"); // 每炉最多一行计数（首炉样例行在 EnvironmentDigestSystem）

            m_ForgeCount++;
            m_NextForgeAt = Now + (3u + m_ForgeCount % 3u) * TicksPerMinute; // 3-4-5 游戏分钟轮换
        }

        /// <summary>
        /// 闲聊炉结果处理（ContentDirectorSystem 按 "chatter:" 前缀转交）：
        /// JSONL salvage 解析（坏行跳过计数）→ 话题分区按序对齐回填（好行数≠派卡数说明模型
        /// 掉行/加行，整批 Zone 留空——宁缺勿错配）→ 入 BubbleSnippetPool（池内同文本去重）。
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
            var alignZone = parsed.Count == m_CurrentZones.Count;
            var added = 0;
            for (int i = 0; i < parsed.Count; i++)
                if (m_Pool.Add(parsed[i].Text, parsed[i].Occasion, alignZone ? m_CurrentZones[i] : null))
                    added++;
            Mod.Log.Info($"[闲聊炉] 入库 {added} 条（解析丢 {skipped} 条，去重丢 {parsed.Count - added} 条，池现 {m_Pool.Count} 条{(alignZone ? "" : "，行数不齐 Zone 整批留空")}）");
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
    }
}
