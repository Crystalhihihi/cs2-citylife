using System;
using System.Collections.Generic;
using Game;
using Game.Citizens;
using Game.Events;
using Game.Notifications;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 事件现场反应层（§12 #70，2026-09-17 玩家拍板；组件路径依据 docs/spikes/2026-09-17-event-scenes-spike.md）：
    /// 治"车祸现场市民还在唠家常"——事件发生 → 角色圈三层 → 按角色出反应。
    ///
    /// 事件源（读侧 128 帧错峰只读，查询 OnCreate 缓存排 Temp/Deleted；写侧系统一律不碰）：
    /// P0 交通事故=AccidentSite（m_Flags 含 TrafficAccident=8；涉事圈 InvolvedInAccident.m_Event 反查，
    /// m_Severity 字段已钉、值域待实机复核）；P0 建筑火灾=OnFire 挂燃烧建筑（m_Intensity 烈度）；
    /// P1 犯罪现场=AccidentSite（CrimeScene=4 且 CrimeFinished 未置位，CrimeVictim 直达）。
    /// P1 死亡=HealthProblem.Dead=2 ——玩家明确大幅限流：不做现场形态、绝不逐人播报，降频扫描
    /// （16 拍≈2048 帧）聚合进传闻榜（桶词"最近城里走了不少人"式）+游戏日冷却至多 1 条。
    /// P2 救护车/P3 灾害本批留接口不实现（EventType 占位）。
    ///
    /// 反应形态（角色圈三层）：
    /// ① 受害者=强制独白模板（0 token、锚点钉死——creature/载具锚点匹配受害市民；有 Injured/Trapped/Dead
    ///    健康信号=重伤呼救档，否则轻伤骂街档；犯罪受害=CrimeVictim 空间圈）；
    /// ② 楼内圈（火灾）="我家着火了"型模板挂燃烧建筑本体锚（剧场室内共锚先例，楼内市民无 agent 硬约束不变）；
    /// ③ 围观圈=快轨围观炉（每现场一炉，固定头 BuildEventReactionHead+动态尾：类型+地点+烈度档词+围观情绪
    ///    子集签），产出一次性消耗——就近锚点放送（BubbleWorldSpikeSystem.SetBubbleText 插队，优先级
    ///    剧场 > 本层 > #58 事件快反模板 > 片段）。
    /// 事件在=反应在；事件终（Secured/CrimeFinished/组件消失）进余韵窗（90 游戏分钟）——剩余围观卡继续
    /// 就近放完（"刚才路口撞了"语域自然衰减）。烈度=Icon 就近匹配（≤40m 取最高 IconPriority）借游戏分级，
    /// 拿不到按类型兜底。跟踪上限 4 个现场（防大城市事件风暴刷屏），围观炉同时在飞最多一炉。
    /// 开关闸：复用设置页"气泡 AI 闲聊"总开关（本层是其快轨下游消费者，关=围观炉零 token；模板层 0 token
    /// 不受影响）+ MUTE 静态总闸。日志纪律 `[现场]`：发现/终了+余韵/围观炉开炉与入库/死亡传闻 各一行。
    /// </summary>
    public partial class EventSceneSystem : GameSystemBase
    {
        private const int k_MaxScenes = 4;          // 同时跟踪现场上限（防事件风暴）
        private const float k_ReactRadius = 60f;    // 反应半径（#58 事件快反同尺）
        private const float k_IconMatchRadius = 40f; // Icon 烈度匹配半径
        private const uint k_AftermathMinutes = 90; // 余韵窗（游戏分钟）
        private const double k_ForgeTtl = 60;       // 围观炉在飞 TTL（快轨短句，闲聊炉同档）
        private const uint k_DeathScanDiv = 16;     // 死亡扫描降频（每 16 拍≈2048 帧一次）
        private const uint k_DeathRumorCooldownMin = 24 * 60; // 死亡传闻冷却（游戏日，至多 1 条）
        private const int k_OnlookerBatch = 10;     // 每现场围观炉产条数（一次性消耗）

        /// <summary>现场类型（P2 救护车/P3 灾害留接口占位，本批不接）。</summary>
        private enum SceneType : byte { Traffic, Fire, Crime }

        /// <summary>一个被跟踪的现场：源实体 + 事件回指 + 角色圈 + 围观卡库存 + 余韵状态。</summary>
        private sealed class SceneEvent
        {
            public Entity Source;                       // 现场实体（AccidentSite 现场/燃烧建筑）
            public Entity EventEntity;                  // 事件实体（m_Event 回指目标，Null=读不到）
            public SceneType Type;
            public float3 Pos;
            public int Severity = 100;                  // IconPriority 整数（借游戏分级；默认 Warning 档）
            public uint FirstSeenFrame;
            public bool Ended;                          // 事件已终（进余韵）
            public uint AftermathUntil;                 // 余韵截止帧（Ended 时上）
            public readonly HashSet<Entity> Victims = new(); // 受害市民（车祸涉事/犯罪受害/火灾被困）
            public bool HasHurtVictim;                  // 受害圈有 Injured/Trapped/Dead → 重伤呼救档
            public bool ForgeDone;                      // 围观炉已发过（每现场一炉，不重复触炉=去重）
            public readonly List<string> Lines = new(); // 围观卡库存（一次性消耗）
            public int LineCursor;
        }

        private EntityQuery m_CitizenQuery = default!;    // 空城/主菜单闸
        private EntityQuery m_FireQuery = default!;       // OnFire 挂燃烧建筑（自带 Transform）
        private EntityQuery m_AccidentQuery = default!;   // AccidentSite 现场
        private EntityQuery m_InvolvedQuery = default!;   // 车祸涉事（m_Event 反查）
        private EntityQuery m_HealthQuery = default!;     // HealthProblem（被困/伤者/死亡）
        private EntityQuery m_CrimeVictimQuery = default!;// 犯罪受害者
        private EntityQuery m_IconQuery = default!;       // Icon（烈度借游戏分级）

        private SimulationSystem m_SimulationSystem = default!;
        private EnvironmentDigestSystem m_Environment = default!;
        private BubbleWorldSpikeSystem? m_BubbleWorld;   // 惰性：主菜单世界可能不存在
        private Game.UI.NameSystem? m_NameSystem;        // 惰性：地点真名层

        private readonly List<SceneEvent> m_Scenes = new();
        private string m_Head = "";
        private bool m_ForgePending;
        private DateTime m_ForgeSince;
        private Entity m_ForgeSource = Entity.Null; // 在飞炉属哪个现场（收炉回填用）

        // 死亡限流状态
        private readonly HashSet<Entity> m_PrevDead = new();
        private int m_DeathsSince;
        private uint m_LastDeathRumorFrame;
        private bool m_DeathBaselineSet;
        private uint m_RefreshCount;

        protected override void OnCreate()
        {
            base.OnCreate();
            // 空城/主菜单闸（与各读侧系统同口径）
            m_CitizenQuery = GetEntityQuery(
                ComponentType.ReadOnly<Citizen>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            RequireForUpdate(m_CitizenQuery);
            m_FireQuery = GetEntityQuery(
                ComponentType.ReadOnly<OnFire>(),
                ComponentType.ReadOnly<Game.Buildings.Building>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_AccidentQuery = GetEntityQuery(
                ComponentType.ReadOnly<AccidentSite>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_InvolvedQuery = GetEntityQuery(
                ComponentType.ReadOnly<InvolvedInAccident>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_HealthQuery = GetEntityQuery(
                ComponentType.ReadOnly<HealthProblem>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_CrimeVictimQuery = GetEntityQuery(
                ComponentType.ReadOnly<CrimeVictim>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_IconQuery = GetEntityQuery(
                ComponentType.ReadOnly<Icon>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Environment = World.GetOrCreateSystemManaged<EnvironmentDigestSystem>();
            m_Head = Content.PromptBuilder.BuildEventReactionHead(); // 固定头拼一次缓存（逐字节稳定纪律）
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 128; // 2 的幂；读侧错峰同口径

        private uint Now => (uint)m_SimulationSystem.frameIndex;
        private static uint TicksPerHour => (uint)Math.Max(1, TimeSystem.kTicksPerDay / 24);
        private static uint TicksPerMinute => Math.Max(1u, TicksPerHour / 60u);

        protected override void OnUpdate()
        {
            m_RefreshCount++;

            // 在飞兜底解锁：网关过期丢弃不回包（各炉同款墙钟兜底——真实网络超时）
            if (m_ForgePending && (DateTime.UtcNow - m_ForgeSince).TotalSeconds > k_ForgeTtl + 60)
                m_ForgePending = false;

            SweepScenes();   // 终了/余韵判定
            DiscoverScenes(); // 新现场发现（含角色圈+围观炉触发）
            ScanDeaths();     // 死亡限流通路（降频）
        }

        // —— ① 事件监听 ——

        /// <summary>终了/余韵判定：源实体消失 / AccidentSite Secured / CrimeFinished → Ended 进余韵窗；
        /// 余韵到期（或源实体彻底没了）移除出表。</summary>
        private void SweepScenes()
        {
            for (int i = m_Scenes.Count - 1; i >= 0; i--)
            {
                var s = m_Scenes[i];
                if (!s.Ended)
                {
                    var gone = !EntityManager.Exists(s.Source);
                    if (!gone && s.Type != SceneType.Fire && EntityManager.HasComponent<AccidentSite>(s.Source))
                    {
                        var flags = EntityManager.GetComponentData<AccidentSite>(s.Source).m_Flags;
                        if ((flags & AccidentSiteFlags.Secured) != 0
                            || (s.Type == SceneType.Crime && (flags & AccidentSiteFlags.CrimeFinished) != 0))
                            gone = true; // 处置完=事件终（Secured 置位；犯罪结案）
                    }
                    else if (!gone && s.Type == SceneType.Fire && !EntityManager.HasComponent<OnFire>(s.Source))
                    {
                        gone = true; // 火灭了
                    }
                    if (gone)
                    {
                        s.Ended = true;
                        s.AftermathUntil = Now + k_AftermathMinutes * TicksPerMinute;
                        Mod.Log.Info($"[现场] 终了：{TypeName(s.Type)} @({s.Pos.x:F0},{s.Pos.z:F0})（余韵 {k_AftermathMinutes} 游戏分钟，剩 {s.Lines.Count - s.LineCursor} 条围观卡续播）");
                    }
                }
                if (s.Ended && (int)(Now - s.AftermathUntil) > 0)
                {
                    Mod.Log.Info($"[现场] 余韵尽：{TypeName(s.Type)} @({s.Pos.x:F0},{s.Pos.z:F0})（围观卡播 {s.LineCursor}/{s.Lines.Count} 条）");
                    m_Scenes.RemoveAt(i);
                }
            }
        }

        /// <summary>新现场发现：火灾=OnFire 建筑直取；车祸/犯罪=AccidentSite 位标志分流
        /// （位置=现场本体 Transform → m_Event 的 Transform，EventNewsSystem.TryGetSitePos 同路径）。
        /// 新现场=建角色圈（受害圈 m_Event 反查/犯罪受害空间圈）+ Icon 烈度匹配 + 触发围观炉（每现场一炉）。</summary>
        private void DiscoverScenes()
        {
            // 火灾（建筑本体即现场，位置白送）
            var fires = m_FireQuery.ToEntityArray(Allocator.Temp);
            foreach (var e in fires)
            {
                if (FindScene(e) != null || m_Scenes.Count >= k_MaxScenes)
                    continue;
                var onFire = EntityManager.GetComponentData<OnFire>(e);
                var s = new SceneEvent
                {
                    Source = e,
                    EventEntity = onFire.m_Event,
                    Type = SceneType.Fire,
                    Pos = EntityManager.GetComponentData<Transform>(e).m_Position,
                    FirstSeenFrame = Now,
                };
                // 火灾受害圈：HealthProblem 含 Trapped/Injured 且 m_Event 回指同一事件（被困/烧伤者）
                BindHealthVictims(s);
                s.Severity = MatchIconSeverity(s.Pos, 100); // 兜底 Warning
                m_Scenes.Add(s);
                Mod.Log.Info($"[现场] 发现：火灾 @{NameOf(e)}（{s.Pos.x:F0},{s.Pos.z:F0}，烈度 {SeverityName(s.Severity)}，被困 {s.Victims.Count}）");
                TryFireOnlookerForge(s);
            }
            fires.Dispose();

            // 车祸/犯罪（AccidentSite 位标志分流；CrimeFinished 结案不收）
            var sites = m_AccidentQuery.ToEntityArray(Allocator.Temp);
            foreach (var e in sites)
            {
                if (FindScene(e) != null || m_Scenes.Count >= k_MaxScenes)
                    continue;
                var site = EntityManager.GetComponentData<AccidentSite>(e);
                var isTraffic = (site.m_Flags & AccidentSiteFlags.TrafficAccident) != 0;
                var isCrime = (site.m_Flags & AccidentSiteFlags.CrimeScene) != 0
                              && (site.m_Flags & AccidentSiteFlags.CrimeFinished) == 0;
                if (!isTraffic && !isCrime)
                    continue;
                if (!TryGetSitePos(e, site, out var pos))
                    continue; // 拿不到位置的现场无法做圈定，不收
                var s = new SceneEvent
                {
                    Source = e,
                    EventEntity = site.m_Event,
                    Type = isTraffic ? SceneType.Traffic : SceneType.Crime,
                    Pos = pos,
                    FirstSeenFrame = Now,
                };
                if (isTraffic)
                    BindTrafficVictims(s);
                else
                    BindCrimeVictims(s);
                s.Severity = MatchIconSeverity(s.Pos, isTraffic ? 100 : 50); // 兜底：车祸 Warning、犯罪 Problem
                m_Scenes.Add(s);
                Mod.Log.Info($"[现场] 发现：{TypeName(s.Type)} @({s.Pos.x:F0},{s.Pos.z:F0})（烈度 {SeverityName(s.Severity)}，受害 {s.Victims.Count}{(s.HasHurtVictim ? "含伤" : "")}）");
                TryFireOnlookerForge(s);
            }
            sites.Dispose();
        }

        /// <summary>车祸涉事圈：InvolvedInAccident.m_Event==事故事件 反查涉事实体 → 解析到市民
        /// （涉事实体=市民直收；=行人 agent 经 Resident 回指；=载具经 Passenger buffer→agent→Resident 回指）。
        /// 任一涉事市民带 Injured/Trapped/Dead 健康信号 → 重伤档。</summary>
        private void BindTrafficVictims(SceneEvent s)
        {
            var arr = m_InvolvedQuery.ToEntityArray(Allocator.Temp);
            foreach (var e in arr)
            {
                var inv = EntityManager.GetComponentData<InvolvedInAccident>(e);
                if (inv.m_Event != s.EventEntity || s.Victims.Count >= 6)
                    continue;
                ResolveVictim(s, e);
            }
            arr.Dispose();
        }

        /// <summary>涉事实体 → 市民（三条解析路径，解析不到=跳过该实体）。</summary>
        private void ResolveVictim(SceneEvent s, Entity involved)
        {
            if (EntityManager.HasComponent<Citizen>(involved))
            {
                AddVictim(s, involved);
                return;
            }
            if (EntityManager.HasComponent<Game.Creatures.Resident>(involved))
            {
                var c = EntityManager.GetComponentData<Game.Creatures.Resident>(involved).m_Citizen;
                if (c != Entity.Null && EntityManager.HasComponent<Citizen>(c))
                    AddVictim(s, c);
                return;
            }
            if (EntityManager.HasComponent<Game.Vehicles.Vehicle>(involved)
                && EntityManager.HasBuffer<Game.Vehicles.Passenger>(involved))
            {
                var passengers = EntityManager.GetBuffer<Game.Vehicles.Passenger>(involved);
                for (int i = 0; i < passengers.Length; i++)
                {
                    var agent = passengers[i].m_Passenger;
                    if (EntityManager.HasComponent<Game.Creatures.Resident>(agent))
                    {
                        var c = EntityManager.GetComponentData<Game.Creatures.Resident>(agent).m_Citizen;
                        if (c != Entity.Null && EntityManager.HasComponent<Citizen>(c))
                            AddVictim(s, c);
                    }
                }
            }
        }

        /// <summary>火灾被困圈：HealthProblem 含 Trapped/Injured/InDanger 且 m_Event 回指同一火灾事件。</summary>
        private void BindHealthVictims(SceneEvent s)
        {
            var arr = m_HealthQuery.ToEntityArray(Allocator.Temp);
            foreach (var e in arr)
            {
                var hp = EntityManager.GetComponentData<HealthProblem>(e);
                if (hp.m_Event != s.EventEntity || s.Victims.Count >= 6)
                    continue;
                if ((hp.m_Flags & (HealthProblemFlags.Trapped | HealthProblemFlags.Injured | HealthProblemFlags.InDanger)) == 0)
                    continue;
                AddVictim(s, e);
            }
            arr.Dispose();
        }

        /// <summary>犯罪受害圈：CrimeVictim 市民按空间圈（该组件无事件回指，spike §2 实锤只能就近取）。</summary>
        private void BindCrimeVictims(SceneEvent s)
        {
            var arr = m_CrimeVictimQuery.ToEntityArray(Allocator.Temp);
            foreach (var e in arr)
            {
                if (s.Victims.Count >= 4)
                    break;
                var pos = CitizenPos(e);
                if (pos.HasValue && math.distance(pos.Value, s.Pos) <= k_ReactRadius)
                    AddVictim(s, e);
            }
            arr.Dispose();
        }

        private void AddVictim(SceneEvent s, Entity citizen)
        {
            if (!s.Victims.Add(citizen))
                return;
            if (EntityManager.HasComponent<HealthProblem>(citizen))
            {
                var f = EntityManager.GetComponentData<HealthProblem>(citizen).m_Flags;
                if ((f & (HealthProblemFlags.Injured | HealthProblemFlags.Trapped | HealthProblemFlags.Dead)) != 0)
                    s.HasHurtVictim = true;
            }
        }

        private SceneEvent? FindScene(Entity source)
        {
            foreach (var s in m_Scenes)
                if (s.Source == source)
                    return s;
            return null;
        }

        /// <summary>Icon 烈度匹配：现场 k_IconMatchRadius 内取最高 IconPriority（借游戏分级，不自己造）；
        /// 拿不到落调用方给的类型兜底档。</summary>
        private int MatchIconSeverity(float3 pos, int fallback)
        {
            var best = -1;
            var arr = m_IconQuery.ToEntityArray(Allocator.Temp);
            foreach (var e in arr)
            {
                var icon = EntityManager.GetComponentData<Icon>(e);
                if (math.distance(icon.m_Location, pos) <= k_IconMatchRadius && (int)icon.m_Priority > best)
                    best = (int)icon.m_Priority;
            }
            arr.Dispose();
            return best >= 0 ? best : fallback;
        }

        // —— ② 围观炉（快轨，每现场一炉） ——

        /// <summary>围观炉触发：每现场一炉（ForgeDone 去重不重复触炉）+ 同时在飞最多一炉 +
        /// 闲聊总开关（本层是其快轨下游）+ 网关可用 + 非 MUTE。闸不过不致命——模板层照常。</summary>
        private void TryFireOnlookerForge(SceneEvent s)
        {
            if (s.ForgeDone || m_ForgePending || Mod.FastGateway == null || Llm.CliGateway.Mute)
                return;
            if (!Content.ModSettings.BubbleChatterEnabled)
                return; // 复用"气泡 AI 闲聊"总开关（§12 #70 纪律：关=零 token）
            // 情绪子集签：围观套确定性取 3 个（源实体 Index 锚定，同现场必同签）
            var moods = new List<string>(3);
            for (var i = 0; i < 3; i++)
            {
                var w = Content.ChatterSpec.OnlookerMoods[(s.Source.Index + i * 2) % Content.ChatterSpec.OnlookerMoods.Length];
                if (!moods.Contains(w))
                    moods.Add(w);
            }
            var prompt = Content.PromptBuilder.BuildEventReactionPrompt(m_Head, SceneDesc(s), moods, k_OnlookerBatch);
            Mod.FastGateway.Enqueue(new Llm.CliRequest(prompt, Llm.CliPriority.Low, k_ForgeTtl, "scene:" + s.Source.Index));
            m_ForgePending = true;
            m_ForgeSince = DateTime.UtcNow;
            m_ForgeSource = s.Source;
            s.ForgeDone = true;
            Mod.Log.Info($"[现场] 围观炉开炉：{TypeName(s.Type)} @({s.Pos.x:F0},{s.Pos.z:F0})（情绪 {string.Join("、", moods)}）");
        }

        /// <summary>事件描述行（执行层组装的事实，LLM 只写反应）：类型词+地点真名层+烈度档词+应急到场。</summary>
        private string SceneDesc(SceneEvent s)
        {
            var kind = s.Type == SceneType.Traffic ? "两车相撞" : s.Type == SceneType.Fire ? "建筑起火" : "有人当街抢劫";
            string where;
            if (s.Type == SceneType.Fire)
            {
                where = NameOf(s.Source);
            }
            else
            {
                m_NameSystem ??= World.GetExistingSystemManaged<Game.UI.NameSystem>();
                where = "这个路口"; // 车祸/犯罪现场无建筑名层（路口泛称；路名层候选挂后续）
            }
            var response = s.Type == SceneType.Fire ? "，消防车已到场" : s.Type == SceneType.Traffic ? "，警车已到" : "，有人报了警";
            return $"{where}{kind}，{SeverityName(s.Severity)}{response}";
        }

        /// <summary>围观炉结果处理（ContentDirectorSystem 按 "scene:" 前缀转交）：
        /// JSONL salvage 解析（BubbleSnippetPool.ParseBatch 同口径，card 缺失落 Any 不连坐）→ 取非空 Text
        /// 入现场库存（一次性消耗）。失败只记日志不致命（模板层照常兜底）。</summary>
        public void OnSceneResult(Llm.CliCompletedResult r)
        {
            m_ForgePending = false;
            if (!r.Result.Success)
            {
                Mod.Log.Info($"[现场] 围观炉一炉失败：{r.Result.Error}（下现场再产）");
                return;
            }
            var scene = FindScene(m_ForgeSource);
            if (scene == null)
                return; // 现场已移除（余韵尽），丢弃不致命
            var parsed = Content.BubbleSnippetPool.ParseBatch(r.Result.Text, out var skipped);
            var added = 0;
            foreach (var p in parsed)
                if (!string.IsNullOrWhiteSpace(p.Text) && added < 16 && !scene.Lines.Contains(p.Text!))
                {
                    scene.Lines.Add(p.Text!); // 同现场去重（同一现场同一句话只听一遍，一次性语义配套）
                    added++;
                }
            Mod.Log.Info($"[现场] 围观卡入库 {added} 条（{TypeName(scene.Type)} @({scene.Pos.x:F0},{scene.Pos.z:F0})，解析丢 {skipped} 条）");
        }

        // —— ③ 反应放送（BubbleWorldSpikeSystem 插队口） ——

        /// <summary>事件现场反应行（BubbleWorldSpikeSystem.SetBubbleText 插队查询，主线程低频；
        /// 优先级：剧场 &gt; 本层 &gt; #58 事件快反模板 &gt; 片段）：
        /// 受害者模板（锚点=受害市民的行人 agent/载具，轻伤骂街/重伤呼救两档，salt 确定性轮换不消耗）→
        /// 火灾楼本体模板（"我家着火了"型）→ 围观 LLM 卡（半径内一次性消耗）。无命中=false（调用方回退 #58 模板）。</summary>
        internal bool TryGetSceneLine(Entity anchor, float3 anchorPos, int salt, out string line)
        {
            line = "";
            for (int i = 0; i < m_Scenes.Count; i++)
            {
                var s = m_Scenes[i];
                if (math.distance(anchorPos, s.Pos) > k_ReactRadius)
                    continue;
                // 受害者模板：锚=受害市民的 creature/载具（受害圈是市民实体，两级匹配）
                if (s.Victims.Count > 0 && MatchVictimAnchor(s, anchor, out var victim))
                {
                    line = VictimLine(s, victim, salt);
                    return line.Length > 0;
                }
                // 火灾楼本体模板（燃烧建筑=现场本体即锚）
                if (s.Type == SceneType.Fire && anchor == s.Source)
                {
                    line = k_FireBuildingLines[(anchor.Index + salt) % k_FireBuildingLines.Length];
                    return true;
                }
                // 围观 LLM 卡（一次性消耗，同泡连换 salt 顺探——顺探也消耗，一次性语义）
                if (s.LineCursor < s.Lines.Count)
                {
                    line = s.Lines[s.LineCursor++];
                    return true;
                }
            }
            return false;
        }

        /// <summary>锚点是否属于受害圈市民的显形实体：行人 agent（CurrentTransport 回指）或所乘载具
        /// （CurrentVehicle，§12 #57 ① 组件在 Game.Creatures 命名空间实锤）。受害圈 ≤6 人，逐人判成本低。</summary>
        private bool MatchVictimAnchor(SceneEvent s, Entity anchor, out Entity victim)
        {
            foreach (var v in s.Victims)
            {
                if (EntityManager.HasComponent<CurrentTransport>(v)
                    && EntityManager.GetComponentData<CurrentTransport>(v).m_CurrentTransport == anchor)
                {
                    victim = v;
                    return true;
                }
                if (EntityManager.HasComponent<Game.Creatures.CurrentVehicle>(v)
                    && EntityManager.GetComponentData<Game.Creatures.CurrentVehicle>(v).m_Vehicle == anchor)
                {
                    victim = v;
                    return true;
                }
            }
            victim = Entity.Null;
            return false;
        }

        /// <summary>受害者模板行：有伤信号=重伤呼救档，否则按类型轻伤档（salt 确定性轮换，不消耗——
        /// 强制独白在事件存续期允许重复喊，#58 模板同口径）。</summary>
        private string VictimLine(SceneEvent s, Entity victim, int salt)
        {
            if (s.HasHurtVictim && s.Type == SceneType.Traffic)
                return k_TrafficHurtLines[(victim.Index + salt) % k_TrafficHurtLines.Length];
            return s.Type switch
            {
                SceneType.Traffic => k_TrafficMildLines[(victim.Index + salt) % k_TrafficMildLines.Length],
                SceneType.Fire => k_FireVictimLines[(victim.Index + salt) % k_FireVictimLines.Length],
                _ => k_CrimeVictimLines[(victim.Index + salt) % k_CrimeVictimLines.Length],
            };
        }

        // —— ④ 死亡限流通路（传闻聚合+游戏日冷却，不做现场形态） ——

        /// <summary>死亡扫描（16 拍一次）：统计 HealthProblem.Dead 新增，进传闻榜桶词聚合 +
        /// 游戏日冷却至多 1 条（玩家明确：这游戏死亡率太高，不限就被讣告刷屏）。首拍只建基线不报历史。</summary>
        private void ScanDeaths()
        {
            if (m_RefreshCount % k_DeathScanDiv != 0)
                return;
            var current = new HashSet<Entity>();
            var arr = m_HealthQuery.ToEntityArray(Allocator.Temp);
            foreach (var e in arr)
            {
                if ((EntityManager.GetComponentData<HealthProblem>(e).m_Flags & HealthProblemFlags.Dead) != 0)
                    current.Add(e);
            }
            arr.Dispose();
            if (!m_DeathBaselineSet)
            {
                foreach (var e in current)
                    m_PrevDead.Add(e);
                m_DeathBaselineSet = true;
                return; // 基线不翻旧账（城市变化感知器同款纪律）
            }
            foreach (var e in current)
                if (m_PrevDead.Add(e))
                    m_DeathsSince++; // 新死者（m_PrevDead 顺带累计，实体版本代际防复用）
            if (m_DeathsSince == 0)
                return;
            if ((int)(Now - m_LastDeathRumorFrame) < (int)(k_DeathRumorCooldownMin * TicksPerMinute))
                return; // 冷却中（m_LastDeathRumorFrame 初值 0：Now 很小的新档会等满一天才首发，合理）
            var text = m_DeathsSince switch
            {
                <= 3 => "最近城里走了几位市民",
                <= 15 => "最近城里走了不少人",
                <= 49 => "最近城里走了几十号人",
                _ => "最近城里走了上百号人",
            };
            Content.CityRumors.Add(text);
            Mod.Log.Info($"[现场] 死亡传闻：{text}（本窗 {m_DeathsSince} 人，游戏日冷却）");
            m_DeathsSince = 0;
            m_LastDeathRumorFrame = Now;
        }

        // —— 工具 ——

        /// <summary>事故现场取位置：本体 Transform → m_Event 的 Transform → 放弃
        /// （EventNewsSystem.TryGetSitePos 同路径——AccidentSite 现场实体自身未必有 Transform）。</summary>
        private bool TryGetSitePos(Entity siteEntity, AccidentSite site, out float3 pos)
        {
            if (EntityManager.HasComponent<Transform>(siteEntity))
            {
                pos = EntityManager.GetComponentData<Transform>(siteEntity).m_Position;
                return true;
            }
            if (site.m_Event != Entity.Null && EntityManager.Exists(site.m_Event)
                && EntityManager.HasComponent<Transform>(site.m_Event))
            {
                pos = EntityManager.GetComponentData<Transform>(site.m_Event).m_Position;
                return true;
            }
            pos = default;
            return false;
        }

        /// <summary>市民现位（犯罪受害空间圈用）：CurrentBuilding→建筑位 / CurrentTransport→agent 位
        /// （BubbleChatterSystem.CitizenPosition 同口径，一处定义别复制粘贴的简化版）。</summary>
        private float3? CitizenPos(Entity citizen)
        {
            if (EntityManager.HasComponent<CurrentBuilding>(citizen))
            {
                var b = EntityManager.GetComponentData<CurrentBuilding>(citizen).m_CurrentBuilding;
                if (EntityManager.HasComponent<Transform>(b))
                    return EntityManager.GetComponentData<Transform>(b).m_Position;
                return null;
            }
            if (EntityManager.HasComponent<CurrentTransport>(citizen))
            {
                var t = EntityManager.GetComponentData<CurrentTransport>(citizen).m_CurrentTransport;
                if (t != Entity.Null && EntityManager.HasComponent<Transform>(t))
                    return EntityManager.GetComponentData<Transform>(t).m_Position;
            }
            return null;
        }

        /// <summary>建筑显示名（火灾现场描述用）：ShopNameOf 真名层（zone 标签已斩断）→ 渲染名 → 类型词兜底。</summary>
        private string NameOf(Entity building)
        {
            m_NameSystem ??= World.GetExistingSystemManaged<Game.UI.NameSystem>();
            var name = EnvironmentDigestSystem.ShopNameOf(EntityManager, m_NameSystem, building)
                       ?? EnvironmentDigestSystem.RenderedName(m_NameSystem, building);
            return name == null || name.Contains("Assets.") ? "一栋建筑" : $"「{name}」";
        }

        private static string TypeName(SceneType t) => t switch
        {
            SceneType.Traffic => "车祸",
            SceneType.Fire => "火灾",
            _ => "抢劫",
        };

        /// <summary>烈度档词（IconPriority 整数 → 中文档）：借游戏分级映射（#70 定案）。</summary>
        private static string SeverityName(int priority) => priority switch
        {
            >= 250 => "特大事故",
            >= 200 => "重大事故",
            >= 150 => "很严重",
            >= 100 => "挺严重",
            >= 50 => "有点严重",
            _ => "小事故",
        };

        // —— 受害者/楼内模板池（0 token，强制独白与"我家着火了"型；salt 确定性轮换，事件存续期允许重复喊）——
        // 如何扩展：直接往对应数组加句即可，轮换/显隐全自动（#58 反应模板同纪律）。
        private static readonly string[] k_TrafficMildLines =
        {
            "会不会开车啊！",
            "我这车刚提的！",
            "急什么急啊！",
            "吓死人了，看着点！",
            "吓死我了，腿都软了",
        };
        private static readonly string[] k_TrafficHurtLines =
        {
            "救命……有人受伤了！",
            "快叫救护车！",
            "别动我……疼……",
            "有没有人啊，帮帮忙！",
            "我喘不上气了……",
        };
        private static readonly string[] k_FireVictimLines =
        {
            "救命啊，下不去了！",
            "烟好大，咳……咳咳……",
            "有没有人听见！",
            "快叫消防车！",
        };
        private static readonly string[] k_CrimeVictimLines =
        {
            "抓小偷啊！他抢我东西！",
            "光天化日抢劫啊！",
            "拦住他！别让他跑了！",
            "我的包！还我的包！",
        };
        private static readonly string[] k_FireBuildingLines =
        {
            "着火了！快跑啊！",
            "我家着火了！救命！",
            "楼上还有人没有？！",
            "别靠近，火太大了！",
            "消防怎么还没到！",
            "我的家……全完了……",
        };
    }
}
