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
        private CitizenPoolSystem m_CitizenPool = default!;
        private Content.PostPool m_Pool = default!;
        private List<Content.Persona> m_Personas = default!;
        private List<string> m_Usernames = default!;
        private string m_Head = "";
        private List<Content.Assignment> m_CurrentAssigned = new();
        private readonly List<Entity> m_CurrentAnchorEntities = new(); // 与 m_CurrentAssigned 对齐（Null=无锚点）
        private readonly List<string?> m_CurrentCitizenNames = new();  // 与 m_CurrentAssigned 对齐（null=卡名/回退路径）
        private readonly Queue<string> m_Recent = new();  // 已发帖子正文（去重反馈，RimTalk TalkHistory 移植）
        private readonly Dictionary<string, string> m_LastByPersona = new(); // 人格卡→上集正文（连载机制，超 32 清空重来）

        private Content.Topic m_LastTopic;
        private Content.Topic m_BatchTopic;      // 在飞那一炉的话题（收炉时给池帖用）
        private uint m_Seed;
        private uint m_BatchCount;               // 炉计数（Daily 话题/形态轮换用——别用 m_Seed：它按 k_BatchSize 步进会与池长撞车）
        private uint m_LastThreadBatch;          // 上一次发续热炉的炉计数（每 5 炉一次的节流）
        private uint m_Tick;                     // 导演自身节拍计数（feedMode=throttled 降频用）
        private bool m_BatchPending;

        /// <summary>市长回应炉的固定 prompt 头（OnCreate 时拼好；CityLifeUISystem 发炉时取用）。</summary>
        public static string? ReplyHead;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CitizenQuery = GetEntityQuery(ComponentType.ReadOnly<Citizen>());
            m_Radar = World.GetOrCreateSystemManaged<TopicRadarSystem>();
            m_AnchorSystem = World.GetOrCreateSystemManaged<EntityAnchorSystem>();
            m_CitizenPool = World.GetOrCreateSystemManaged<CitizenPoolSystem>();
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
            ReplyHead = Content.PromptBuilder.BuildReplyHead(m_Personas); // 市长回应炉固定头（同纪律）
            Content.ModSettings.Load(cfgDir, msg => Mod.Log.Info(msg)); // 玩家开关（t0Fallback 等）
            Mod.Feed.MaxItems = Content.ModSettings.FeedMaxItems;   // 信息流上限（玩家可调）

            RequireForUpdate(m_CitizenQuery);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 4096;

        protected override void OnUpdate()
        {
            // ① 收炉：LLM 结果 → 解析打捞 → 查重兜底 → 入池（锚点实体+评论串随帖入池，绝不阻塞模拟线程）
            while (Mod.Gateway != null && Mod.Gateway.TryDequeueResult(out var r))
            {
                // 突发快讯炉走专线路由："城市快讯"账号单帖，requestId 带锚点实体（不占常规批次位）
                if (r.RequestId != null && r.RequestId.StartsWith("breaking:"))
                {
                    HandleBreakingFlash(r);
                    continue;
                }
                // 评论续热炉走专线路由：新评论追加到热帖评论串（不占常规批次位）
                if (r.RequestId != null && r.RequestId.StartsWith("thread:"))
                {
                    HandleThreadContinue(r);
                    continue;
                }
                // 市长回应炉走专线路由：评论挂到市长帖下，不占常规批次位
                if (r.RequestId != null && r.RequestId.StartsWith("mayor-reply:"))
                {
                    HandleMayorReply(r);
                    continue;
                }
                // M4 意图解析炉路由：命中事件包则进活动链（确认弹窗）
                if (r.RequestId != null && r.RequestId.StartsWith("intent:"))
                {
                    if (r.Result.Success)
                        World.GetOrCreateSystemManaged<EventChainSystem>().OnIntentJson(r.Result.Text);
                    else
                        Mod.Log.Info($"[LLM] 意图解析炉失败：{r.Result.Error}");
                    continue;
                }

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
                        // 署名：市民语境席位 → 那个市民的真名；否则回退卡名路径（Always 卡在 AuthorFor 内恒显本人名）
                        var mainAuthor = idx < m_CurrentCitizenNames.Count && m_CurrentCitizenNames[idx] != null
                            ? m_CurrentCitizenNames[idx]!
                            : AuthorFor(item.PersonaId);
                        // 评论者名字解析（人格 id → 显示名，规则与主帖一致）
                        var comments = new string[item.Comments.Count][];
                        for (int ci = 0; ci < comments.Length; ci++)
                            comments[ci] = new[] { AuthorFor(item.Comments[ci].PersonaId), item.Comments[ci].Text };

                        // 争论串 @替换：模型只认识卡 id（prompt 里让它 @卡id 互怼），显示名在这里换——
                        // 含主帖作者（@主帖卡 id = 怼楼主）。string.Replace 精确串，误伤可忽略
                        for (int ci = 0; ci < comments.Length; ci++)
                        {
                            if (item.PersonaId.Length > 0)
                                comments[ci][1] = comments[ci][1].Replace("@" + item.PersonaId, "@" + mainAuthor);
                            for (int cj = 0; cj < comments.Length; cj++)
                            {
                                var cid = item.Comments[cj].PersonaId;
                                if (cid.Length > 0)
                                    comments[ci][1] = comments[ci][1].Replace("@" + cid, "@" + comments[cj][0]);
                            }
                        }

                        m_Pool.Add(new Content.PoolEntry(
                            new Content.Post(mainAuthor, item.Text, m_BatchTopic, item.PersonaId),
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
            // 昼夜节律：深夜（23-6 点）释放减半——夜里刷帖本来就稀
            var night = snapshot.HourOfDay >= 23 || snapshot.HourOfDay < 6;
            var allowRelease = !paused && (!slowMo || m_Tick % 4 == 0) && (!night || m_Tick % 2 == 0);
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
                // 民意/突发联动：请愿优先于突发（请愿是玩家可交互的，突发是纯新闻）；
                // 请愿在场时突发留到下一炉（LastBreaking 不清，排队不丢）
                var petition = Content.LiveContext.PendingPetition;
                Content.LiveContext.PendingPetition = null;
                var breaking = petition == null ? Content.LiveContext.LastBreaking : null;
                if (petition == null)
                    Content.LiveContext.LastBreaking = null;
                m_BatchTopic = petition != null
                    ? Content.Topic.Petition
                    : breaking != null
                        ? Content.Topic.Breaking
                        : Content.ContentDirector.DetectTopic(snapshot, m_LastTopic);
                // 热帖抽签：请愿/突发必热；平时每 7 炉带一炉热帖（大事件/随机对喷，2026-08-19 玩家定案分布）
                var hotOne = petition != null || breaking != null || m_BatchCount % 7 == 3;
                m_CurrentAssigned = PickAssigned(k_BatchSize, hotOne);
                var anchorTexts = AssignAnchors(); // 实体锚点：吐槽/求助/盘点席位优先
                var prevPosts = CollectPrevPosts(); // 连载机制：有前情的席位喂回上集
                var citizenCtx = AssignCitizenContexts(); // 市民语境：每席位一个真实市民的当下（处境进 prompt、真名随炉署名）
                // 市长发言上下文：一条发言影响随后 3 炉（写回层是 M4，这里只到舆情）
                string? mayorCtx = null;
                if (Content.LiveContext.MayorBatchesLeft > 0)
                {
                    mayorCtx = Content.LiveContext.MayorPost;
                    Content.LiveContext.MayorBatchesLeft--;
                    if (Content.LiveContext.MayorBatchesLeft <= 0)
                        Content.LiveContext.MayorPost = null;
                }
                // 活动结果上下文：结算一次喂一炉，市民议论现场/财政账
                var outcomeCtx = Content.LiveContext.LastEventOutcome;
                Content.LiveContext.LastEventOutcome = null;
                // 请愿后续上下文：回应/散去一次喂一炉（有人满意有人继续怼）
                var petitionResolvedCtx = Content.LiveContext.PetitionResolved;
                Content.LiveContext.PetitionResolved = null;
                var prompt = Content.PromptBuilder.BuildBatch(
                    m_Head, snapshot, m_BatchTopic, m_CurrentAssigned,
                    new List<string>(m_Recent), m_BatchCount, anchorTexts, prevPosts,
                    breaking, mayorCtx, outcomeCtx, Content.LiveContext.OngoingEvent,
                    petition, petitionResolvedCtx, citizenCtx);
                Mod.Gateway.Enqueue(new Llm.CliRequest(prompt, Llm.CliPriority.Normal, 600));
                m_BatchPending = true;
                m_BatchCount++;
            }

            // ④ 评论续热炉（评论区生态：热帖过一会儿继续长评论，争论有来回——2026-08-20 玩家反馈）
            // 每 5 炉一次、Low 优先级、TTL 180s（宁缺毋滥）；只挑评论 4-29 的帖（太冷没得聊，太热已完结）
            if (!paused && Mod.Gateway != null && !Llm.CliGateway.Mute
                && m_BatchCount % 5 == 2 && m_BatchCount != m_LastThreadBatch
                && ReplyHead != null
                && Mod.Feed.TryGetHotThread(4, 29, out var tSeq, out var tAuthor, out var tText, out var tComments))
            {
                m_LastThreadBatch = m_BatchCount;
                var count = 2 + (int)(tSeq % 3); // 2-4 条，别千篇整数
                Mod.Gateway.Enqueue(new Llm.CliRequest(
                    Content.PromptBuilder.BuildThreadPrompt(ReplyHead, tAuthor, tText, tComments, count),
                    Llm.CliPriority.Low, 180, "thread:" + tSeq));
            }

            m_Tick++;
        }

        /// <summary>评论续热炉结果处理：解析评论 → 逐条追加到热帖评论串（模型看着真名@人，无需替换）。</summary>
        private void HandleThreadContinue(Llm.CliCompletedResult r)
        {
            if (!r.Result.Success)
            {
                Mod.Log.Info($"[LLM] 续热炉失败：{r.Result.Error}");
                return;
            }
            if (!uint.TryParse(r.RequestId.Substring("thread:".Length), out var seq))
                return;
            var comments = Content.BatchParser.ParseCommentArray(r.Result.Text, msg => Mod.Log.Info(msg));
            var added = 0;
            foreach (var (pid, text) in comments)
                if (Mod.Feed.AppendComment(seq, AuthorFor(pid), text))
                    added++;
            if (added > 0)
                Mod.Log.Info($"[LLM] 帖 #{seq} 续热 +{added} 条评论");
        }

        /// <summary>突发快讯炉结果处理：清洗正文 → "城市快讯"账号单帖入信息流（锚点从 requestId 解析）。</summary>
        private void HandleBreakingFlash(Llm.CliCompletedResult r)
        {
            if (!r.Result.Success)
            {
                Mod.Log.Info($"[LLM] 快讯炉失败：{r.Result.Error}");
                return;
            }
            // requestId 契约："breaking:{entityIndex}:{entityVersion}"（EventNewsSystem.Report 发炉时埋的锚点）
            var parts = r.RequestId.Substring("breaking:".Length).Split(':');
            var entityIndex = 0;
            var entityVersion = 0;
            if (parts.Length == 2)
            {
                int.TryParse(parts[0], out entityIndex);
                int.TryParse(parts[1], out entityVersion);
            }

            // 正文清洗（模型不守规矩也兜得住）：去引号/换行/JSON 残壳，只留第一行，超长硬截
            var text = r.Result.Text.Trim();
            var nl = text.IndexOf('\n');
            if (nl > 0)
                text = text.Substring(0, nl);
            text = text.Trim().Trim('"', '"', '"', '{', '}').Trim();
            if (text.Length == 0)
                return;
            if (text.Length > 80)
                text = text.Substring(0, 80);

            Mod.Feed.Record(new Content.Post("城市快讯", text, Content.Topic.Breaking, "newsflash"),
                            entityIndex, entityVersion);
            Mod.Log.Info($"[LLM] 快讯帖：{text.Substring(0, System.Math.Min(24, text.Length))}…");
        }

        /// <summary>市长回应炉结果处理：解析评论 → 逐条挂到市长帖（按 seq）。</summary>
        private void HandleMayorReply(Llm.CliCompletedResult r)
        {
            if (!r.Result.Success)
            {
                Mod.Log.Info($"[LLM] 市长回应炉失败：{r.Result.Error}");
                return;
            }
            if (!uint.TryParse(r.RequestId.Substring("mayor-reply:".Length), out var seq))
                return;

            var comments = Content.BatchParser.ParseCommentArray(r.Result.Text, msg => Mod.Log.Info(msg));
            var added = 0;
            foreach (var (pid, text) in comments)
                if (Mod.Feed.AppendComment(seq, AuthorFor(pid), text))
                    added++;
            Mod.Log.Info($"[LLM] 市长帖 #{seq} 回应 +{added} 条评论");
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
        /// 市民语境分配（"创造条件，不做限制"——2026-08-20 玩家狼人杀经验）：
        /// 每席位抽一个真实市民，处境进 prompt、真名随炉署名（收炉时按席位对齐取用）。
        /// Always 卡（明星/彩蛋位）跳过——卡本人就是人设；池空时回退罐头处境池/卡名路径。
        /// </summary>
        private List<string?> AssignCitizenContexts()
        {
            m_CurrentCitizenNames.Clear();
            var contexts = new List<string?>(m_CurrentAssigned.Count);
            var pool = m_CitizenPool.Entries;
            for (int i = 0; i < m_CurrentAssigned.Count; i++)
            {
                if (m_CurrentAssigned[i].Persona.Always || pool.Count == 0)
                {
                    contexts.Add(null);
                    m_CurrentCitizenNames.Add(null);
                    continue;
                }
                var entry = pool[(int)((m_BatchCount + (uint)i) % (uint)pool.Count)];
                contexts.Add(entry.Context);
                m_CurrentCitizenNames.Add(entry.Name);
            }
            return contexts;
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

        /// <summary>
        /// 收炉取名（2026-08-20 玩家定案：发帖用原版名，除非特殊注入）：
        /// 特殊注入卡（Always，明星/彩蛋位）恒显卡本人名；其余一律用真实市民名池（CitizenNamePoolSystem）。
        /// 池空（开局未采到）回退旧逻辑：卡候选名 ∪ 网名字池混抽。
        /// </summary>
        private string AuthorFor(string personaId)
        {
            var p = m_Personas.Find(x => x.Id == personaId)
                    ?? (m_CurrentAssigned.Count > 0
                        ? m_CurrentAssigned[(int)(m_Seed % m_CurrentAssigned.Count)].Persona
                        : null);
            m_Seed++;

            // 特殊注入：Crystal 这类彩蛋/明星卡恒显本人名
            if (p != null && p.Always && p.Names.Length > 0)
                return p.Names[0];

            // 常规作者：真实市民名（原版名）——满屏"热心大妈"的根治
            var pool = m_CitizenPool.Entries;
            if (pool.Count > 0)
                return pool[(int)(m_Seed % pool.Count)].Name;

            // 回退：卡候选名 ∪ 全网网名（usernames.jsonl 目前只在这条回退路径用）
            if (p == null)
                return "热心市民";
            var poolCount = p.Names.Length + m_Usernames.Count;
            var idx = (int)(m_Seed % poolCount);
            return idx < p.Names.Length ? p.Names[idx] : m_Usernames[idx - p.Names.Length];
        }
    }
}
