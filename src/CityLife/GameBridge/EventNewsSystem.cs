using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Events;
using Game.Prefabs;
using Game.Simulation;
using Unity.Entities;
using Transform = Game.Objects.Transform;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 突发新闻系统（实体级话题源）：直采事件组件（2026-08-20 分类学实锤，docs/spikes/2026-08-20-event-taxonomy.md）——
    /// 火灾 = <see cref="OnFire"/>（挂在燃烧中的建筑本体）；车祸/犯罪 = <see cref="AccidentSite"/> 的 m_Flags 位标志
    /// （TrafficAccident=8 / CrimeScene=4 / CrimeFinished=16）。
    ///
    /// 频率两档分离（2026-08-20 玩家定案：盗窃/车祸在 CS2 是高频事件，全变讨论就淹了信息流）：
    /// - 快讯档（每条都做）：目击模板帖（即时）+ LLM"城市快讯"媒体帖——按类限流
    ///   （火灾 4h / 车祸 6h / 犯罪 8h，全局 1h，单位游戏小时）；
    /// - 热议档（全城讨论）：写 LiveContext.LastBreaking 让下一炉变热议串——单独更严的闸门
    ///   （settings.json breakingHotHours，默认 12 游戏小时一次）。大多数事件只发快讯不进全民讨论。
    ///
    /// 纪律：
    /// - EventJournal 监听保留但只打日志（分类学摸底：日常火灾/犯罪不入刊，实机长期空白，不作为话题源）；
    /// - 去重按实体；只读不写；查询排除 Temp/Deleted；总开关 settings.json breakingNews（默认开）。
    /// </summary>
    public partial class EventNewsSystem : GameSystemBase
    {
        // 快讯档限流（游戏小时）：高频事件也只闻其声不见其淹
        private const int k_FireCooldownH = 4;
        private const int k_AccidentCooldownH = 6;
        private const int k_CrimeCooldownH = 8;
        private const int k_AnyCooldownH = 1;

        private EventJournalSystem m_Journal = default!;
        private PrefabSystem m_PrefabSystem = default!;
        private SimulationSystem m_SimulationSystem = default!;
        private EntityQuery m_CitizenQuery = default!;
        private EntityQuery m_FireQuery = default!;
        private EntityQuery m_AccidentQuery = default!;
        private int m_LastCount;
        private bool m_Initialized;
        private bool m_ClockInitialized;
        private readonly HashSet<Entity> m_Reported = new();
        private readonly List<Entity> m_PruneScratch = new();
        private uint m_LastFireAt;
        private uint m_LastAccidentAt;
        private uint m_LastCrimeAt;
        private uint m_LastAnyAt;
        private uint m_LastHotAt;   // 上一次放行进热议档的时刻

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Journal = World.GetOrCreateSystemManaged<EventJournalSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_CitizenQuery = GetEntityQuery(ComponentType.ReadOnly<Citizen>());
            // 火灾：燃烧中的建筑本体（OnFire.m_Intensity 强度，m_RequestFrame 起火帧；字段见 dump 补探）
            m_FireQuery = GetEntityQuery(
                ComponentType.ReadOnly<OnFire>(),
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            // 车祸/犯罪：事故现场实体（m_Flags 位标志分类）
            m_AccidentQuery = GetEntityQuery(
                ComponentType.ReadOnly<AccidentSite>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            RequireForUpdate(m_CitizenQuery);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 128;

        private uint Now => (uint)m_SimulationSystem.frameIndex;
        private static uint TicksPerHour => (uint)System.Math.Max(1, TimeSystem.kTicksPerDay / 24);

        protected override void OnUpdate()
        {
            if (!m_ClockInitialized)
            {
                // 各冷却起点拨到"很久以前"——新档/读档后第一条突发不用干等一个冷却周期
                // （uint 回绕无所谓：模 2^32 差值比较仍成立）
                var longAgo = Now - 72u * TicksPerHour;
                m_LastFireAt = m_LastAccidentAt = m_LastCrimeAt = m_LastAnyAt = m_LastHotAt = longAgo;
                m_ClockInitialized = true;
            }

            PollJournal();   // 分类学日志（只记不发）
            if (Content.ModSettings.BreakingNews)
            {
                PollFires();
                PollAccidents();
            }
            PruneReported();
        }

        // —— EventJournal 监听：只打日志喂分类学。日常火灾/犯罪不入刊（实机 [News] 长期空白），不作为话题源 ——
        private void PollJournal()
        {
            var journal = m_Journal.eventJournal;
            if (!m_Initialized)
            {
                m_LastCount = journal.Length; // 基线：已有事件不算新闻（防读档刷历史）
                m_Initialized = true;
                return;
            }
            if (journal.Length <= m_LastCount)
            {
                m_LastCount = journal.Length; // 列表收缩（新档/重置）时跟齐
                return;
            }
            for (int i = m_LastCount; i < journal.Length; i++)
            {
                var journalEntity = journal[i];
                var prefabEntity = m_Journal.GetPrefab(journalEntity);
                string prefabName = "?";
                if (m_PrefabSystem.TryGetPrefab(prefabEntity, out PrefabBase prefab))
                    prefabName = prefab.name;
                // 追踪数据全记（类型+数值对），攒日志摸清 journal 到底收什么
                string tracking = "";
                if (m_Journal.TryGetData(journalEntity, out DynamicBuffer<EventJournalData> data))
                {
                    for (int d = 0; d < data.Length && d < 6; d++)
                        tracking += $" {data[d].m_Type}={data[d].m_Value}";
                }
                Mod.Log.Info($"[News] 事件入刊（仅日志）：prefab={prefabName}{tracking}");
            }
            m_LastCount = journal.Length;
        }

        // —— 火灾：OnFire 新实体（挂建筑本体，自带 Transform 定位）——
        private void PollFires()
        {
            if (!KindReady(m_LastFireAt, k_FireCooldownH) || !AnyReady)
                return;
            var arr = m_FireQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            foreach (var e in arr)
            {
                if (m_Reported.Contains(e))
                    continue;
                var dir = Geo.DirectionOf(EntityManager.GetComponentData<Transform>(e).m_Position);
                Report(e, "fire", $"{dir}有建筑起火", $"{dir}有建筑起火了，消防车正赶过去，希望人没事");
                m_LastFireAt = Now;
                break; // 每轮最多一条（限流纪律；其余下轮再说，还烧着自然轮到）
            }
            arr.Dispose();
        }

        // —— 车祸/犯罪：AccidentSite 新实体，m_Flags 位标志分类 ——
        private void PollAccidents()
        {
            if (!AnyReady)
                return;
            var arr = m_AccidentQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            foreach (var e in arr)
            {
                if (m_Reported.Contains(e))
                    continue;
                var site = EntityManager.GetComponentData<AccidentSite>(e);
                var isCrime = (site.m_Flags & AccidentSiteFlags.CrimeScene) != 0
                              && (site.m_Flags & AccidentSiteFlags.CrimeFinished) == 0; // 已结案的旧现场不报
                var isTraffic = (site.m_Flags & AccidentSiteFlags.TrafficAccident) != 0;
                if (isCrime && KindReady(m_LastCrimeAt, k_CrimeCooldownH))
                {
                    var dir = DirectionOfSite(e, site);
                    Report(e, "crime", $"{dir}有店铺遭窃", $"{dir}有店铺遭窃，警察已经到场，附近注意下可疑人员");
                    m_LastCrimeAt = Now;
                    break;
                }
                if (isTraffic && KindReady(m_LastAccidentAt, k_AccidentCooldownH))
                {
                    var dir = DirectionOfSite(e, site);
                    Report(e, "accident", $"{dir}路口发生车祸", $"{dir}路口出车祸了，围了一堆人，路过绕一下");
                    m_LastAccidentAt = Now;
                    break;
                }
            }
            arr.Dispose();
        }

        private bool KindReady(uint lastAt, int cooldownH) => Now - lastAt >= (uint)cooldownH * TicksPerHour;
        private bool AnyReady => Now - m_LastAnyAt >= TicksPerHour * (uint)k_AnyCooldownH;

        /// <summary>事故实体方位：自身没 Transform 则退到 m_Event 的，再没有就用"市区"兜底。</summary>
        private string DirectionOfSite(Entity siteEntity, AccidentSite site)
        {
            if (EntityManager.HasComponent<Transform>(siteEntity))
                return Geo.DirectionOf(EntityManager.GetComponentData<Transform>(siteEntity).m_Position);
            if (site.m_Event != Entity.Null && EntityManager.Exists(site.m_Event)
                && EntityManager.HasComponent<Transform>(site.m_Event))
                return Geo.DirectionOf(EntityManager.GetComponentData<Transform>(site.m_Event).m_Position);
            return "市区";
        }

        /// <summary>
        /// 命中上报，两档分离：
        /// 快讯档（每条都做）——目击模板帖（即时）+ LLM"城市快讯"媒体帖（requestId 带锚点，导演路由发帖）；
        /// 热议档（闸门控制）——写 LiveContext.LastBreaking 让下一炉变热议串，
        /// 间隔由 settings.json breakingHotHours 控制（默认 12h），防高频事件把信息流全变成讨论事件。
        /// </summary>
        private void Report(Entity e, string kind, string breaking, string postText)
        {
            m_Reported.Add(e);
            m_LastAnyAt = Now;
            Content.CityRumors.Add(breaking); // 城市记忆（§12 #60 刀②）：进传闻榜，闲聊炉/信息流当话料
            Mod.Feed.Record(new Content.Post("现场直击", postText, Content.Topic.Breaking, "live"), e.Index, e.Version);
            if (Mod.Gateway != null && !Llm.CliGateway.Mute)
            {
                s_BreakingHead ??= Content.PromptBuilder.BuildBreakingHead(); // 拼一次缓存复用（缓存纪律）
                Mod.Gateway.Enqueue(new Llm.CliRequest(
                    Content.PromptBuilder.BuildBreakingPrompt(s_BreakingHead, breaking),
                    Llm.CliPriority.Normal, 480, $"breaking:{e.Index}:{e.Version}")); // 快讯宁缺毋滥；慢轨 v4-pro+high 抬档 120→480（2026-09-20，130-221s/炉必超 120）
            }

            // 热议档闸门：半个游戏日才准全城讨论一次（2026-08-20 玩家定案）
            if (Now - m_LastHotAt >= (uint)Content.ModSettings.BreakingHotHours * TicksPerHour)
            {
                Content.LiveContext.LastBreaking = breaking;
                m_LastHotAt = Now;
                Mod.Log.Info($"[News] 突发（{kind}，进热议）：{breaking}（{e.Index}:{e.Version}）");
            }
            else
            {
                Mod.Log.Info($"[News] 突发（{kind}，仅快讯）：{breaking}（{e.Index}:{e.Version}）");
            }
        }

        private static string? s_BreakingHead; // 快讯炉固定头（启动后首报时拼一次）

        /// <summary>已报道集合清理：实体不存在（火灭/现场撤除）即移除，防无限涨。</summary>
        private void PruneReported()
        {
            m_PruneScratch.Clear();
            foreach (var e in m_Reported)
                if (!EntityManager.Exists(e))
                    m_PruneScratch.Add(e);
            foreach (var e in m_PruneScratch)
                m_Reported.Remove(e);
        }
    }
}
