using System.Collections.Generic;
using Game;
using Game.Citizens;
using Unity.Entities;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 内容导演系统（编排层，T1 版）：每 4096 帧一轮——
    /// 收炉（网关结果 → 解析打捞 → 查重 → 入池）→ 放一条进信息流（池帖优先，锚点实体坐标随帖入库；
    /// 空则 T0 模板兜底，开关可控）→ 补炉（池低位且空闲则发一炉，分配=人格卡×形态，
    /// 吐槽/求助/盘点席位优先配实体锚点，有前情的席位喂回上集）。
    ///
    /// 2026-08-20 起**发帖直写 FeedStore**（自家面板是唯一展示层）：CustomChirps 软依赖退役留档，
    /// 玩家不再需要安装它。阈值与模板知识全在 Content 命名空间（版本免疫）；这里只做编排，不写业务规则。
    /// 注意：GameSystemBase 更新间隔**必须是 2 的幂**（非 2 幂 mod 初始化直接抛异常，2026-08-19 踩坑）。
    /// </summary>
    public partial class ContentDirectorSystem : GameSystemBase
    {
        private const int k_BatchSize = 10;     // 每炉条数（高速游戏下池子要更深：释放随模拟帧加速，补炉是墙钟延迟）
        private const int k_RefillWatermark = 5; // 池低于此数补炉
        private const int k_RecentKeep = 12;     // 已发正文保留数（去重反馈+查重共用）

        private EntityQuery m_CitizenQuery = default!;
        private TopicRadarSystem m_Radar = default!;
        private EntityAnchorSystem m_AnchorSystem = default!;
        private Content.PostPool m_Pool = default!;
        private List<Content.Persona> m_Personas = default!;
        private List<string> m_Usernames = default!;
        private string m_Head = "";
        private List<Content.Assignment> m_CurrentAssigned = new();
        private readonly List<Entity> m_CurrentAnchorEntities = new(); // 与 m_CurrentAssigned 对齐（Null=无锚点）
        private readonly Queue<string> m_Recent = new();  // 已发帖子正文（去重反馈，RimTalk TalkHistory 移植）
        private readonly Dictionary<string, string> m_LastByPersona = new(); // 人格卡→上集正文（连载机制，超 32 清空重来）

        private Content.Topic m_LastTopic;
        private Content.Topic m_BatchTopic;      // 在飞那一炉的话题（收炉时给池帖用）
        private uint m_Seed;
        private uint m_BatchCount;               // 炉计数（Daily 话题/形态轮换用——别用 m_Seed：它按 k_BatchSize 步进会与池长撞车）
        private uint m_Tick;                     // 导演自身节拍计数（feedMode=throttled 降频用）
        private bool m_BatchPending;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CitizenQuery = GetEntityQuery(ComponentType.ReadOnly<Citizen>());
            m_Radar = World.GetOrCreateSystemManaged<TopicRadarSystem>();
            m_AnchorSystem = World.GetOrCreateSystemManaged<EntityAnchorSystem>();
            m_Pool = new Content.PostPool();

            // 风格卡册 + 全网名字池（ModsSettings/CityLife/ 下，schema 见 Persona.cs 头注释）
            var cfgDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                @"..\LocalLow\Colossal Order\Cities Skylines II\ModsSettings\CityLife"));
            m_Personas = Content.PersonaBook.Load(
                System.IO.Path.Combine(cfgDir, "personas.jsonl"), msg => Mod.Log.Info(msg));
            m_Usernames = Content.PersonaBook.LoadUsernames(
                System.IO.Path.Combine(cfgDir, "usernames.jsonl"), msg => Mod.Log.Info(msg));
            m_Head = Content.PromptBuilder.BuildHead(m_Personas); // 拼一次缓存复用（缓存纪律）
            Content.ModSettings.Load(cfgDir, msg => Mod.Log.Info(msg)); // 玩家开关（t0Fallback 等）

            RequireForUpdate(m_CitizenQuery);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 4096;

        protected override void OnUpdate()
        {
            // ① 收炉：LLM 结果 → 解析打捞 → 查重兜底 → 入池（锚点实体+评论串随帖入池，绝不阻塞模拟线程）
            while (Mod.Gateway != null && Mod.Gateway.TryDequeueResult(out var r))
            {
                m_BatchPending = false;
                if (r.Result.Success)
                {
                    var items = Content.BatchParser.ParsePosts(r.Result.Text, msg => Mod.Log.Info(msg));
                    var kept = 0;
                    var idx = 0;
                    foreach (var item in items)
                    {
                        // 查重：与已发/池内正文重复即丢弃
                        if (m_Recent.Contains(item.Text) || m_Pool.ContainsText(item.Text))
                        {
                            Mod.Log.Info($"[Batch] 查重丢弃一条（{item.Text.Substring(0, System.Math.Min(16, item.Text.Length))}…）");
                            idx++;
                            continue;
                        }
                        var anchor = idx < m_CurrentAnchorEntities.Count ? m_CurrentAnchorEntities[idx] : Entity.Null;
                        // 评论者名字解析（人格 id → 显示名，规则与主帖一致）
                        var comments = new string[item.Comments.Count][];
                        for (int ci = 0; ci < comments.Length; ci++)
                            comments[ci] = new[] { AuthorFor(item.Comments[ci].PersonaId), item.Comments[ci].Text };

                        m_Pool.Add(new Content.PoolEntry(
                            new Content.Post(AuthorFor(item.PersonaId), item.Text, m_BatchTopic, item.PersonaId),
                            anchor == Entity.Null ? null : (object)anchor,
                            comments));
                        kept++;
                        idx++;
                    }
                    Mod.Log.Info($"[LLM] 一炉到位 {kept}/{items.Count} 条（{r.Result.LatencyMs}ms，池存 {m_Pool.Count}）");
                }
                else
                {
                    Mod.Log.Info($"[LLM] 一炉失败：{r.Result.Error}（T0 模板顶着）");
                }
            }

            var snapshot = m_Radar.Latest;
            if (snapshot.Citizens == 0)
                return; // 雷达还没采到样

            // 进城即时反馈：首发一条系统帖，告诉玩家"活着、首批在生成"（空面板=以为坏了的教训，2026-08-19）
            if (m_Seed == 0 && m_BatchCount == 0 && !m_BatchPending)
                Mod.Feed.Record(new Content.Post("市民圈", "市民圈已接入本市网络，首批帖子生成中……", Content.Topic.Daily, "system"));

            // feedMode 门控（2026-08-19 玩家定案）：always 常跑 / openOnly 仅面板展开 / throttled 收起降频保温
            var panelOpen = Content.LiveContext.PanelOpen;
            var mode = Content.ModSettings.FeedMode;
            var paused = mode == "openOnly" && !panelOpen;
            var slowMo = mode == "throttled" && !panelOpen;

            // ② 放一条进信息流：池帖优先（锚点实体坐标随帖入库，面板点击聚焦预留）；空则 T0 兜底（开关可控）
            var allowRelease = !paused && (!slowMo || m_Tick % 4 == 0); // throttled 收起时 1/4 速滴灌
            if (allowRelease && m_Pool.TryTake(out var entry))
            {
                var entityIndex = 0;
                var entityVersion = 0;
                if (entry.Tag is Entity anchorEntity && anchorEntity != Entity.Null)
                {
                    entityIndex = anchorEntity.Index;
                    entityVersion = anchorEntity.Version;
                }
                Mod.Feed.Record(entry.Post, entityIndex, entityVersion, entry.Comments);
                RememberPosted(entry.Post.Text, entry.Post.PersonaId);
            }
            else if (allowRelease && Content.ModSettings.T0Fallback)
            {
                // T0 兜底帖（"又下雨了"式模板）。settings.json 里 t0Fallback=true 才启用——本质是 AI mod，默认关
                var topic = Content.ContentDirector.DetectTopic(snapshot, m_LastTopic);
                m_LastTopic = topic;
                var t0 = Content.TemplateEngine.Compose(snapshot, topic, m_Seed++);
                Mod.Feed.Record(t0);
                RememberPosted(t0.Text, t0.PersonaId);
            }

            // ③ 补炉：池低位 + 无在飞 + 网关可用 + 非 MUTE + feedMode 门控（throttled 收起时仅池空才补）
            var allowRefill = !paused && (!slowMo || m_Pool.Count == 0);
            if (allowRefill && !m_BatchPending && m_Pool.Count < k_RefillWatermark
                && Mod.Gateway != null && !Llm.CliGateway.Mute && m_Personas.Count > 0)
            {
                // 突发联动：新闻入刊 → 本炉变热议串（事件文本进尾 + 热帖评论配额）
                var breaking = Content.LiveContext.LastBreaking;
                Content.LiveContext.LastBreaking = null;
                m_BatchTopic = breaking != null
                    ? Content.Topic.Breaking
                    : Content.ContentDirector.DetectTopic(snapshot, m_LastTopic);
                // 热帖抽签：突发事件必热；平时每 7 炉带一炉热帖（大事件/随机对喷，2026-08-19 玩家定案分布）
                var hotOne = breaking != null || m_BatchCount % 7 == 3;
                m_CurrentAssigned = PickAssigned(k_BatchSize, hotOne);
                var anchorTexts = AssignAnchors(); // 实体锚点：吐槽/求助/盘点席位优先
                var prevPosts = CollectPrevPosts(); // 连载机制：有前情的席位喂回上集
                // 市长发言上下文：一条发言影响随后 3 炉（写回层是 M4，这里只到舆情）
                string? mayorCtx = null;
                if (Content.LiveContext.MayorBatchesLeft > 0)
                {
                    mayorCtx = Content.LiveContext.MayorPost;
                    Content.LiveContext.MayorBatchesLeft--;
                    if (Content.LiveContext.MayorBatchesLeft <= 0)
                        Content.LiveContext.MayorPost = null;
                }
                var prompt = Content.PromptBuilder.BuildBatch(
                    m_Head, snapshot, m_BatchTopic, m_CurrentAssigned,
                    new List<string>(m_Recent), m_BatchCount, anchorTexts, prevPosts,
                    breaking, mayorCtx);
                Mod.Gateway.Enqueue(new Llm.CliRequest(prompt, Llm.CliPriority.Normal, 600));
                m_BatchPending = true;
                m_BatchCount++;
            }

            m_Tick++;
        }

        /// <summary>已发正文登记：去重反馈池 + 人格卡前情（连载机制数据源）。</summary>
        private void RememberPosted(string text, string personaId)
        {
            m_Recent.Enqueue(text);
            while (m_Recent.Count > k_RecentKeep)
                m_Recent.Dequeue();
            if (!string.IsNullOrEmpty(personaId))
            {
                if (m_LastByPersona.Count > 32)
                    m_LastByPersona.Clear(); // 粗放生灭，防无限涨
                m_LastByPersona[personaId] = text;
            }
        }

        /// <summary>连载机制：收集当前分配里有人格卡前情的席位的上集正文（无则 null）。</summary>
        private List<string?> CollectPrevPosts()
        {
            var list = new List<string?>(m_CurrentAssigned.Count);
            foreach (var a in m_CurrentAssigned)
                list.Add(m_LastByPersona.TryGetValue(a.Persona.Id, out var prev) ? prev : null);
            return list;
        }

        /// <summary>
        /// 实体锚点分配：吐槽/求助/盘点形态优先挂锚（这些形状吃具体对象），轮换取锚防总写同一家店。
        /// 返回与 m_CurrentAssigned 对齐的锚点文本表；同步填 m_CurrentAnchorEntities（收炉时随帖入池）。
        /// </summary>
        private List<string?> AssignAnchors()
        {
            m_CurrentAnchorEntities.Clear();
            var texts = new List<string?>(m_CurrentAssigned.Count);
            var anchors = m_AnchorSystem.Anchors;
            var cursor = anchors.Count > 0 ? (int)(m_BatchCount % (uint)anchors.Count) : 0;

            foreach (var a in m_CurrentAssigned)
            {
                var wantsAnchor = a.Form.Id == "吐槽" || a.Form.Id == "求助" || a.Form.Id == "盘点";
                if (wantsAnchor && anchors.Count > 0)
                {
                    var anchor = anchors[cursor % anchors.Count];
                    cursor++;
                    texts.Add(anchor.PromptText);
                    m_CurrentAnchorEntities.Add(anchor.Entity);
                }
                else
                {
                    texts.Add(null);
                    m_CurrentAnchorEntities.Add(Entity.Null);
                }
            }
            return texts;
        }

        /// <summary>
        /// 作者分配（角度轴）：always 卡（明星/彩蛋位）先固定入座，其余席位按人格卡 × 形态抽签轮换。
        /// 评论数配额：常态 1-3；hotOne 时随机一席变热帖 8-30（热议/对喷生态位）。
        /// </summary>
        private List<Content.Assignment> PickAssigned(int count, bool hotOne)
        {
            var list = new List<Content.Assignment>(count);
            var hotSlot = hotOne ? (int)(m_Seed % count) : -1;
            int Target(int i) => i == hotSlot ? 8 + (int)((m_Seed / 3) % 23) : 1 + (int)((m_Seed + i) % 3);

            foreach (var p in m_Personas)
                if (p.Always && list.Count < count)
                    list.Add(new Content.Assignment(p, PickForm(list.Count), Target(list.Count)));

            var rot = m_Personas.FindAll(x => !x.Always);
            for (int i = list.Count, k = 0; i < count && rot.Count > 0; i++, k++)
                list.Add(new Content.Assignment(rot[(int)((m_Seed + k) % rot.Count)], PickForm(i), Target(i)));

            m_Seed += (uint)count;
            return list;
        }

        /// <summary>形态抽签：炉计数+席位错开（别用 m_Seed：它按 k_BatchSize 步进，与池长撞车会把槽位钉死）。</summary>
        private Content.PostForm PickForm(int slot)
            => Content.PostForms.All[(int)((m_BatchCount + slot) % Content.PostForms.All.Length)];

        /// <summary>收炉取名：优先按模型返回的 persona id 找卡；显示名 = 卡候选名 ∪ 全网名字池混抽。</summary>
        private string AuthorFor(string personaId)
        {
            var p = m_Personas.Find(x => x.Id == personaId)
                    ?? (m_CurrentAssigned.Count > 0
                        ? m_CurrentAssigned[(int)(m_Seed % m_CurrentAssigned.Count)].Persona
                        : null);
            m_Seed++;
            if (p == null)
                return "热心市民";

            // 名字池合并：卡自带候选 + 全网网名（社区贡献）；Crystal 这类单名卡自然恒显本人名
            var poolCount = p.Names.Length + m_Usernames.Count;
            var idx = (int)(m_Seed % poolCount);
            return idx < p.Names.Length ? p.Names[idx] : m_Usernames[idx - p.Names.Length];
        }
    }
}
