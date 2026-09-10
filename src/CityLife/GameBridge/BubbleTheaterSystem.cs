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
    /// 多人小剧场（S7，§12 #48 多人小剧场段 + §4 M5"多人对话"）：对话锚点成组 + 真名单绑定 + 轮流冒泡。
    /// 执行层确定性管生命周期（选锚点/绑人/播放/终了），LLM 只写剧本（架构铁律 1：算得准的全给执行层）。
    ///
    /// 状态机（一拍一拍走，全部锚游戏时间——暂停零成本、倍速同速放大，#48 节拍定案）：
    /// ① 选锚点：每 3-4-5 游戏分钟评估一次（节拍轮换照抄闲聊炉）。候选只从 BubbleWorldSpikeSystem
    ///    的可见锚点附近找（采样池天然全是屏内点，离镜头近优先=按锚点离相机距离升序扫）：
    ///    a) 车站候车——EnvironmentDigestSystem.CollectAround 40m 圈扫 static 树，
    ///       WaitingPassengers.m_Count ≥2（候车是计数不是名单，spike §4）；
    ///    b) 公园/景点——圈内带 AttractivenessProvider / SignatureBuildingData 的建筑；
    ///    c) 店内——市民查询按 CurrentBuilding 分组计数 ≥2（剧场级低频全量扫，帧预算内），
    ///       且建筑须落在某个可见锚点 40m 内（离镜头近优先的落点保证）。
    /// ② 绑真名单（不绑随机路人）：开放场所（车站/公园）= moving 树圈内带 Human+Resident 的行人 agent
    ///    回指市民（S6 实锤候车行人在树里），按离地点距离升序取 2-4 人，锚点=agent 本体；
    ///    店内 = CurrentBuilding==该建筑的市民 2-4 人，锚点=建筑本体（室内市民无 agent，
    ///    多人共锚——气泡轮播剧本台词，见 ④）。参与者已在他组/冷却中的跳过。
    /// ③ 开炉：场景卡（地点名+环境摘要 BuildDigest）+ 每人一张处境卡（CitizenPoolSystem.DescribeCitizen
    ///    按实体出卡）→ 一次 LLM 调用产 2-4 轮剧本 JSONL {"speaker":1,"text":...}（speaker=参与者序号，
    ///    1 起）。固定头 PromptBuilder.BuildTheaterHead 启动拼一次缓存（逐字节稳定纪律）。
    ///    网关/MUTE/TTL 兜底闸门照抄闲聊炉；feedMode 已解耦（§12 #49），改吃设置页"气泡小剧场"
    ///    独立开关（默认开，关=零 token）。结果走 ContentDirectorSystem 按 "theater:" 前缀
    ///    转交 OnTheaterResult（不自己 TryDequeueResult——两个消费者轮询同一队列会互相偷包）。
    /// ④ 播放：激活前全量复核（任一参与者失效/离开/锚点不可锚=开播中止，全有或全无——speaker 序号
    ///    与名单绑定，减员不重排）；激活时第一句直接落到其说话人锚点，其余参与者锚点显"……"（在听）。
    ///    轮次推进挂气泡换文案节拍：BubbleWorldSpikeSystem.TickLifecycle 里任一剧场锚点到时
    ///    → OnAnchorRotated → 下一句写到其说话人锚点 + RefreshAnchorText 立即换文案（上条读完
    ///    下条接话）；SetBubbleText 的插队分支 TryGetLine 是**纯查询**，绝不推轮次。
    /// ⑤ 终了：剧本播完 / 任一参与者实体失效 / 任一锚点离屏（气泡系统 Resample 裁掉即检测不到）
    ///    / 地点实体失效（车站被拆）——锚点失效即锁（#28）：终了即给地点上冷却 ≈3 炉节拍防连开。
    ///
    /// 上限与降级：同屏活剧场 ≤2（k_MaxActive）；同时在飞最多一炉；剧本池空/供给不可用=不开
    /// （不致命，单人吐槽管道照常）；有效剧本 &lt;2 句作废。只在评估/收炉低频点查树与扫市民，
    /// 不进任何每帧/渲染热路径；只读模拟数据不写。
    /// 日志纪律 `[剧场]`：开组（地点+人数+名单）、开炉（人数×轮数）、剧本入库（N 句开播）、
    /// 终了（原因+播到第几句）各一行，失败/中止同前缀一行。
    /// </summary>
    public partial class BubbleTheaterSystem : GameSystemBase
    {
        private const double k_ForgeTtl = 300;   // 在飞请求 TTL（秒）：低频补给宁缺毋滥（闲聊炉同值）
        private const int k_MaxActive = 2;       // 同屏活剧场 ≤2（#48 上限定案）
        private const int k_MinParticipants = 2; // 一组 2-4 人（#48/M5）
        private const int k_MaxParticipants = 4;
        private const float k_ScanRadius = 40f;  // 选锚点扫描半径（环境圈同尺，S6 定值）
        private const float k_BindRadius = 30f;  // 绑名单半径：参与者须离地点 30m 内（"共位"）
        private const int k_MaxAnchorScan = 8;   // 每拍最多扫几个可见锚点（帧预算闸）
        private const int k_MinWaiting = 2;      // 车站候车人气门槛（#48"≥2 人"）
        private const uint k_CooldownMinutes = 12; // 锚点冷却 ≈3 炉节拍（3-4-5 分钟轮换 ×3），防连开

        private EntityQuery m_CitizenQuery = default!;
        private EntityQuery m_IndoorQuery = default!;
        private TopicRadarSystem m_Radar = default!;
        private SimulationSystem m_SimulationSystem = default!;
        private EnvironmentDigestSystem m_Environment = default!; // S6 半径收集/环境摘要共用口
        private CameraUpdateSystem m_CameraUpdate = default!;
        private BubbleWorldSpikeSystem? m_BubbleWorld;  // 惰性：主菜单世界可能不存在
        private Game.UI.NameSystem? m_NameSystem;       // 惰性：真实地名/人名（CitizenPoolSystem 同款先例）

        private string m_Head = "";
        private uint m_EvalCount;              // 评估拍计数：3-4-5 分钟轮换的锚
        private uint m_NextEvalAt;             // 下一评估拍游戏时刻（tick）
        private bool m_ClockInitialized;
        private uint m_ForgeCount;             // 炉计数：轮数轮换 / requestId 的锚
        private bool m_ForgePending;           // 在飞标志（同时在飞最多一炉）
        private DateTime m_ForgeSince;         // 发炉墙钟（UTC）：网关过期丢弃不回包，TTL+60s 兜底解锁

        private Casting? m_Casting;                       // 在飞剧组（剧本回来前名单先锁）
        private readonly List<Theater> m_Active = new();  // 活剧场（≤k_MaxActive）
        private readonly Dictionary<Entity, Theater> m_ByAnchor = new(); // 锚点→剧场（插队查询主键）
        private readonly Dictionary<Entity, uint> m_CooldownUntil = new(); // 地点→解禁时刻（tick）
        private readonly List<(Entity Anchor, byte Kind, float3 Pos)> m_AnchorSnap = new(); // 可见锚点快照（复用）

        /// <summary>一名参与者：真名单市民 + 气泡锚点（室外=行人 agent，室内=建筑本体）+ 显示名（日志用）。</summary>
        private sealed class Participant
        {
            public Entity Citizen;
            public Entity Anchor;
            public string Name = "";
            public bool Indoor; // true=锚点是建筑（多人共锚）；false=锚点是行人 agent
        }

        /// <summary>在飞剧组：名单+地点+任务规格，剧本回来时全量复核后才激活。</summary>
        private sealed class Casting
        {
            public Entity Location;
            public float3 Pos;
            public string SceneName = "";
            public string KindLabel = "";
            public int Rounds;
            public int Lines; // 请求句数（收炉解析上限对齐用）
            public readonly List<Participant> Participants = new();
        }

        /// <summary>一个活剧场：名单 + 剧本队列 + 当前各锚点在显的台词。</summary>
        private sealed class Theater
        {
            public Entity Location;
            public string SceneName = "";
            public readonly List<Participant> Participants = new();
            public readonly List<(int Speaker, string Text)> Script = new(); // Speaker=0 基参与者序号
            public int Cursor;              // 下一句待播下标
            public bool Finished;           // 播完标记（OnAnchorRotated 置位，OnUpdate 收尾）
            public readonly List<Entity> Anchors = new();            // 去重后的锚点实体
            public readonly Dictionary<Entity, string> Lines = new(); // 锚点→当前台词（共锚=最新一句）
        }

        /// <summary>选锚点候选：地点实体 + 已绑名单（≥2 人才算候选成立）。</summary>
        private sealed class Candidate
        {
            public Entity Location;
            public float3 Pos;
            public string KindLabel = "";
            public float DistToCam;
            public readonly List<(Entity Citizen, Entity Anchor, bool Indoor)> Roster = new();
        }

        /// <summary>店内分组用：建筑 → 楼内市民（名单封顶 k_MaxParticipants，计数全记）。</summary>
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
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Environment = World.GetOrCreateSystemManaged<EnvironmentDigestSystem>();
            m_CameraUpdate = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            m_Head = Content.PromptBuilder.BuildTheaterHead(); // 固定头拼一次缓存复用（逐字节稳定纪律）
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 128; // 2 的幂；只是节拍检查粒度

        private uint Now => (uint)m_SimulationSystem.frameIndex;
        private static uint TicksPerHour => (uint)Math.Max(1, TimeSystem.kTicksPerDay / 24);
        private static uint TicksPerMinute => Math.Max(1u, TicksPerHour / 60u);

        protected override void OnUpdate()
        {
            if (!m_ClockInitialized)
            {
                m_NextEvalAt = Now + TicksPerMinute; // 进城 1 游戏分钟后首评（让气泡层先采样一轮）
                m_ClockInitialized = true;
            }

            // 在飞兜底解锁：网关过期丢弃不回包（闲聊炉/S3 同款墙钟兜底）——剧组解散不设冷却（不是地点的锅）
            if (m_ForgePending && (DateTime.UtcNow - m_ForgeSince).TotalSeconds > k_ForgeTtl + 60)
            {
                m_ForgePending = false;
                m_Casting = null;
            }

            SweepTheaters(); // 终了判定每拍都查（播完/失效/离屏不等评估节拍）

            if ((int)(Now - m_NextEvalAt) < 0)
                return; // 还没到点（uint 差值比较，tick 回绕安全）
            // 无论本拍成败都先排下一拍——扫描频率被节拍硬限，候选落空不空转
            m_NextEvalAt = Now + (3u + m_EvalCount % 3u) * TicksPerMinute;
            m_EvalCount++;

            if (m_Active.Count >= k_MaxActive || m_ForgePending)
                return;
            if (Mod.Gateway == null || Llm.CliGateway.Mute)
                return; // MUTE 静默零成本：连扫描都不做（供给不可用=不开，不致命）

            // 开关闸（§12 #49）：同闲聊炉口径——独立于 feedMode 的设置页开关，默认开；关=零 token
            if (!Content.ModSettings.BubbleTheaterEnabled)
                return;

            var snapshot = m_Radar.Latest;
            if (snapshot.Citizens == 0)
                return; // 雷达还没采到样

            TryCast(snapshot);
        }

        // —— ①② 选锚点 + 绑名单 ——

        /// <summary>评估拍主流程：可见锚点快照 → 室外候选（车站/公园景点，按离镜头近扫）+ 店内候选
        /// （CurrentBuilding 分组），取离镜头最近且绑得够 2 人的开炉；一个都绑不成=本拍放弃（下拍再试）。</summary>
        private void TryCast(in Content.CitySnapshot snapshot)
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
            var scanCount = math.min(k_MaxAnchorScan, m_AnchorSnap.Count);
            var statics = new NativeList<Entity>(32, Allocator.Temp);
            var movers = new NativeList<Entity>(32, Allocator.Temp);
            for (int i = 0; i < scanCount; i++)
            {
                var cand = ScanOutdoor(m_AnchorSnap[i].Pos, camPos, statics, movers);
                if (cand != null && (best == null || cand.DistToCam < best.DistToCam))
                    best = cand;
            }
            statics.Dispose();
            movers.Dispose();

            var indoor = ScanIndoor(camPos);
            if (indoor != null && (best == null || indoor.DistToCam < best.DistToCam))
                best = indoor;

            if (best == null)
                return; // 本拍无候选（城市安静/都在冷却），下拍自然再试
            FireForge(best, snapshot);
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
            if (station != Entity.Null) { loc = station; locPos = stationPos; label = "车站候车"; }
            else if (park != Entity.Null) { loc = park; locPos = parkPos; label = parkIsSignature ? "景点" : "公园"; }
            else return null;
            if (OnCooldown(loc) || LocationInUse(loc))
                return null;

            var cand = new Candidate
            {
                Location = loc,
                Pos = locPos,
                KindLabel = label,
                DistToCam = math.distance(camPos, locPos),
            };
            BindOutdoorRoster(locPos, movers, cand.Roster);
            return cand.Roster.Count >= k_MinParticipants ? cand : null;
        }

        /// <summary>开放场所绑人：圈内 agent → 带 Human（排动物）+ Resident（排载具）+ Transform（可锚定）
        /// 的回指市民，按离地点距离升序取前 k_MaxParticipants；市民实体去重+排除在组/在冷却名单。</summary>
        private void BindOutdoorRoster(float3 locPos, NativeList<Entity> movers, List<(Entity Citizen, Entity Anchor, bool Indoor)> dst)
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
            for (int i = 0; i < scored.Count && dst.Count < k_MaxParticipants; i++)
                if (scored[i].D2 <= r2)
                    dst.Add((scored[i].Citizen, scored[i].Agent, false));
        }

        /// <summary>店内候选：市民查询按 CurrentBuilding 分组计数（剧场级低频全量扫，3-5 游戏分钟才一次），
        /// ≥2 人且落在某个可见锚点 40m 内的建筑才算；绑人=楼内市民（锚点=建筑本体，多人共锚），
        /// 取离镜头最近的一栋。全不中 → null。</summary>
        private Candidate? ScanIndoor(float3 camPos)
        {
            var arr = m_IndoorQuery.ToEntityArray(Allocator.Temp);
            var groups = new Dictionary<Entity, IndoorGroup>();
            for (int i = 0; i < arr.Length; i++)
            {
                var e = arr[i];
                var b = EntityManager.GetComponentData<CurrentBuilding>(e).m_CurrentBuilding;
                // 外部连接等非建筑实体剔除（spike §1 兜底纪律）；读不到位置的没法演
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
                    DistToCam = dist,
                };
                foreach (var m in kv.Value.Members)
                    cand.Roster.Add((m, b, true));
                best = cand;
            }
            return best;
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

        // —— ③ 开炉 ——

        /// <summary>组合卡开炉：场景卡（地点名+环境摘要）+ 每人一张处境卡 → 一炉产 人数×2-4轮 句剧本。
        /// 出卡此刻才做（DescribeCitizen 按实体）——绑人时只验实体，卡无效者此刻丢；丢完 &lt;2 人=组炉放弃。</summary>
        private void FireForge(Candidate cand, in Content.CitySnapshot snapshot)
        {
            var participants = new List<Participant>(cand.Roster.Count);
            var cards = new List<string>(cand.Roster.Count);
            var names = new List<string>(cand.Roster.Count);
            foreach (var (citizen, anchor, indoor) in cand.Roster)
            {
                var card = CitizenPoolSystem.DescribeCitizen(EntityManager, citizen);
                if (card == null)
                    continue; // 儿童/MovingAway/实体刚死——跳过该参与者
                var name = m_NameSystem != null ? m_NameSystem.GetRenderedLabelName(citizen) : null;
                participants.Add(new Participant { Citizen = citizen, Anchor = anchor, Name = string.IsNullOrEmpty(name) ? "市民" : name, Indoor = indoor });
                cards.Add(card);
                names.Add(participants[participants.Count - 1].Name);
            }
            if (participants.Count < k_MinParticipants)
            {
                Mod.Log.Info("[剧场] 组炉放弃：有效参与者不足 2 人（出卡后减员）");
                return;
            }

            var rounds = 2 + (int)(m_ForgeCount % 3u); // 2-4 轮（炉计数轮换，确定性）
            var lines = participants.Count * rounds;   // 每人每轮一句
            var sceneName = SceneNameOf(cand);
            var scene = sceneName + "（" + cand.KindLabel + "）";
            var digest = m_Environment.BuildDigest(cand.Pos); // 环境摘要（S6 同款蒸馏，≤3 条）
            if (digest.Length > 0)
                scene += "｜旁边：" + digest;
            var prompt = Content.PromptBuilder.BuildTheaterPrompt(m_Head, snapshot, scene, cards, lines);
            Mod.Gateway!.Enqueue(new Llm.CliRequest(prompt, Llm.CliPriority.Low, k_ForgeTtl, "theater:" + m_ForgeCount));

            var casting = new Casting
            {
                Location = cand.Location,
                Pos = cand.Pos,
                SceneName = sceneName,
                KindLabel = cand.KindLabel,
                Rounds = rounds,
                Lines = lines,
            };
            casting.Participants.AddRange(participants);
            m_Casting = casting;
            m_ForgePending = true;
            m_ForgeSince = DateTime.UtcNow;
            Mod.Log.Info($"[剧场] 开组：{sceneName}（{cand.KindLabel}），{participants.Count} 人：{string.Join("、", names)}");
            Mod.Log.Info($"[剧场] 开炉：{participants.Count} 人 × {rounds} 轮 = {lines} 句（第 {m_ForgeCount + 1} 炉）");
            m_ForgeCount++;
        }

        /// <summary>场景地点名：店内=店名（租户公司名优先），车站/公园=渲染名；读不到 → 类型词兜底（不硬造）。</summary>
        private string SceneNameOf(Candidate cand)
        {
            string? name = cand.KindLabel.EndsWith("内")
                ? EnvironmentDigestSystem.ShopNameOf(EntityManager, m_NameSystem, cand.Location)
                : EnvironmentDigestSystem.RenderedName(m_NameSystem, cand.Location);
            return name ?? cand.KindLabel;
        }

        /// <summary>
        /// 小剧场炉结果处理（ContentDirectorSystem 按 "theater:" 前缀转交，闲聊炉同款路由纪律）：
        /// JSONL salvage 解析（坏行跳过计数，speaker 超界/文本空/超 40 字都丢）→ 有效 &lt;2 句作废 →
        /// 激活全量复核（锚点失效即锁，全有或全无）→ 建组开播。失败只记日志不致命，下拍自然会再评。
        /// </summary>
        public void OnTheaterResult(Llm.CliCompletedResult r)
        {
            m_ForgePending = false;
            var cast = m_Casting;
            m_Casting = null;
            if (cast == null)
                return; // 兜底解锁时已解散
            if (!r.Result.Success)
            {
                Mod.Log.Info($"[剧场] 一炉失败：{r.Result.Error}（下拍再试）");
                return;
            }
            var script = ParseScript(r.Result.Text, cast.Participants.Count, cast.Lines, out var skipped);
            if (script.Count < 2)
            {
                Mod.Log.Info($"[剧场] 剧本作废：有效 {script.Count} 句（解析丢 {skipped} 条），不开播");
                SetCooldown(cast.Location);
                return;
            }

            // 激活复核：名单/地点在飞行窗口里可能已变（锚点失效即锁 #28）
            var fail = ValidateCasting(cast);
            m_BubbleWorld ??= World.GetExistingSystemManaged<BubbleWorldSpikeSystem>();
            var bubbles = m_BubbleWorld; // 局部变量落地——编译器 nullable 流分析认局部不认字段
            if (fail == null && bubbles == null)
                fail = "气泡系统未就绪";
            if (fail != null || bubbles == null)
            {
                Mod.Log.Info($"[剧场] 开播中止：{cast.SceneName}（{fail}）");
                SetCooldown(cast.Location);
                return;
            }

            // 建剧场：锚点→台词表先就位（插队分支即刻生效），第一句直接落到说话人锚点，其余"……"（在听）
            var t = new Theater { Location = cast.Location, SceneName = cast.SceneName };
            t.Participants.AddRange(cast.Participants);
            t.Script.AddRange(script);
            foreach (var p in t.Participants)
                if (!t.Lines.ContainsKey(p.Anchor))
                {
                    t.Lines[p.Anchor] = "……";
                    t.Anchors.Add(p.Anchor);
                }
            var (s0, t0) = t.Script[0];
            t.Lines[t.Participants[s0].Anchor] = t0;
            t.Cursor = 1;
            if (t.Cursor >= t.Script.Count)
                t.Finished = true; // 纯防御（有效 ≥2 句到不了这）
            m_Active.Add(t);
            foreach (var a in t.Anchors)
                m_ByAnchor[a] = t;

            // 锚点建组（五道闸在 EnsureAnchor 里）；任一不可锚=开播中止（回滚登记，冷却地点）
            foreach (var a in t.Anchors)
            {
                if (!bubbles.EnsureAnchor(a, AnchorKindOf(t, a)))
                {
                    foreach (var x in t.Anchors)
                        m_ByAnchor.Remove(x);
                    m_Active.Remove(t);
                    Mod.Log.Info($"[剧场] 开播中止：{cast.SceneName}（锚点不可锚：离屏/层关/容量满）");
                    SetCooldown(cast.Location);
                    return;
                }
            }
            // 立即换文案开播（不等各锚点自己的时钟——第一拍就全是剧场台词）
            foreach (var a in t.Anchors)
                bubbles.RefreshAnchorText(a);
            Mod.Log.Info($"[剧场] 剧本入库：{cast.SceneName}，{t.Script.Count} 句（{cast.Rounds} 轮）开播");
        }

        /// <summary>激活复核（飞行窗口全量重验）：地点在、参与者在、室内参与者还在该建筑内、
        /// 室外参与者的 agent 还在且可定位。全有或全无——任一不过返回原因串，全过返回 null。</summary>
        private string? ValidateCasting(Casting cast)
        {
            if (!EntityManager.Exists(cast.Location))
                return "地点失效";
            foreach (var p in cast.Participants)
            {
                if (!EntityManager.Exists(p.Citizen))
                    return "参与者失效（" + p.Name + "）";
                if (p.Indoor)
                {
                    if (!EntityManager.HasComponent<CurrentBuilding>(p.Citizen)
                        || EntityManager.GetComponentData<CurrentBuilding>(p.Citizen).m_CurrentBuilding != cast.Location)
                        return "参与者已离开（" + p.Name + "）";
                }
                else if (!EntityManager.Exists(p.Anchor) || !EntityManager.HasComponent<Transform>(p.Anchor))
                    return "参与者离开画面（" + p.Name + "）";
            }
            return null;
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

        /// <summary>气泡生命周期回调（BubbleWorldSpikeSystem.TickLifecycle 在任一剧场锚点到时换文案前调）：
        /// 剧本推进一句——下一句写到其说话人的锚点上，并让该锚点立即换文案（RefreshAnchorText：
        /// 上条读完下条接话，不等说话人自己的时钟）。播完置 Finished（OnUpdate 统一收尾）。</summary>
        internal void OnAnchorRotated(Entity anchor)
        {
            if (!m_ByAnchor.TryGetValue(anchor, out var t) || t.Finished)
                return;
            if (t.Cursor >= t.Script.Count)
                return;
            var (speaker, text) = t.Script[t.Cursor++];
            var speakerAnchor = t.Participants[speaker].Anchor;
            t.Lines[speakerAnchor] = text;
            if (t.Cursor >= t.Script.Count)
                t.Finished = true;
            if (speakerAnchor != anchor)
            {
                m_BubbleWorld ??= World.GetExistingSystemManaged<BubbleWorldSpikeSystem>();
                m_BubbleWorld?.RefreshAnchorText(speakerAnchor); // 推轮即时换文案（纯查询路径，无递归）
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

        /// <summary>剧本 JSONL salvage 解析（BubbleSnippetPool.ParseBatch 同纪律——LLM 输出非法 JSON 是最高频故障）：
        /// 逐行 {"speaker":1,"text":..}；speaker∈1..人数（转 0 基）、text 非空 ≤40 字（气泡排版硬顶
        /// 同片段池口径），坏行跳过计数；超 maxLines 截断。返回序保持模型输出序（即剧本时间序）。</summary>
        private static List<(int Speaker, string Text)> ParseScript(string jsonl, int participants, int maxLines, out int skipped)
        {
            var list = new List<(int, string)>();
            skipped = 0;
            if (string.IsNullOrWhiteSpace(jsonl))
                return list;
            foreach (var raw in jsonl.Split('\n'))
            {
                if (list.Count >= maxLines)
                    break;
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//"))
                    continue;
                var speaker = Util.JsonMini.GetInt(line, "speaker");
                var text = Util.JsonMini.GetStr(line, "text");
                if (speaker == null || speaker.Value < 1 || speaker.Value > participants || string.IsNullOrWhiteSpace(text))
                {
                    skipped++;
                    continue;
                }
                text = text!.Trim();
                if (text.Length > 40)
                {
                    skipped++;
                    continue;
                }
                list.Add((speaker.Value - 1, text));
            }
            return list;
        }

        /// <summary>锚点类型：室内参与者的锚点是建筑（kind 2），室外是行人 agent（kind 0）。</summary>
        private static byte AnchorKindOf(Theater t, Entity anchor)
        {
            foreach (var p in t.Participants)
                if (p.Anchor == anchor)
                    return p.Indoor ? (byte)2 : (byte)0;
            return 0; // 理论到不了，纯防御
        }

        /// <summary>地点冷却（锚点失效即锁 #28：终了/作废/中止都给地点上 ≈3 炉节拍的冷却，防连开）。</summary>
        private void SetCooldown(Entity loc) => m_CooldownUntil[loc] = Now + k_CooldownMinutes * TicksPerMinute;

        /// <summary>地点是否在冷却中（过期条目顺带清，表恒小）。</summary>
        private bool OnCooldown(Entity loc)
        {
            if (!m_CooldownUntil.TryGetValue(loc, out var until))
                return false;
            if ((int)(Now - until) < 0)
                return true;
            m_CooldownUntil.Remove(loc);
            return false;
        }

        /// <summary>地点是否已被占用（在飞剧组或活剧场）。</summary>
        private bool LocationInUse(Entity loc)
        {
            if (m_Casting != null && m_Casting.Location == loc)
                return true;
            foreach (var t in m_Active)
                if (t.Location == loc)
                    return true;
            return false;
        }

        /// <summary>市民是否已在剧组里（在飞/在演都算——真名单不串场）。</summary>
        private bool InUse(Entity citizen)
        {
            if (m_Casting != null)
                foreach (var p in m_Casting.Participants)
                    if (p.Citizen == citizen)
                        return true;
            foreach (var t in m_Active)
                foreach (var p in t.Participants)
                    if (p.Citizen == citizen)
                        return true;
            return false;
        }
    }
}
