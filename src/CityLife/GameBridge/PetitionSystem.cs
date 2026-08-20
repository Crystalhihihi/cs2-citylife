using System;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CityLife.GameBridge
{
    /// <summary>请愿状态。</summary>
    public enum PetitionState { Idle, Voiced, Gathering, Cooldown }

    /// <summary>
    /// 民意层 v2（市民发起的逆向闭环，活动链的镜像）：
    /// 负面情绪超阈值 + 抽签 → 诉求热帖（写 LiveContext.PendingPetition，导演下一炉变怨气热议串）→
    /// 窗口期内市长发帖 = 回应（议论合流，平息）；超时未回应 → 市民真实聚集市政厅/地标
    /// （TripNeeded 注入，复用 CrowdInjector/CrowdCount）→ 散去结算 → 冷却。
    ///
    /// 三军规落实（§12 #27/28/29）：
    /// - LLM 不握触发权：触发/时机/规模全执行层（情绪阈值 + 1/4 抽签 + 冷却 + 与活动链互斥）；
    /// - 锚点可审计：主题取真实最差维度（失业/幸福/无家可归，TopicRadar 读数）；
    /// - 舆情为主不动数值：唯一写操作是 TripNeeded 注入（2 级原语，总量×写回档位，有冷却）。
    /// 配置（settings.json）：petitionEnabled / petitionCooldownH / petitionWindowH / petitionScale / petitionDebug。
    /// </summary>
    public partial class PetitionSystem : GameSystemBase
    {
        private const int k_MinPopulation = 500;   // 太小的村没有请愿
        private const uint k_GatherHours = 3;      // 聚集时长（游戏小时，v1 固定）

        private EntityQuery m_CitizenQuery = default!;
        private EntityQuery m_InjectQuery = default!;
        private EntityQuery m_AttendanceQuery = default!;
        private EntityQuery m_SignatureQuery = default!;
        private EntityQuery m_TimeDataQuery = default!;
        private EntityQuery m_TimeSettingsQuery = default!;
        private TopicRadarSystem m_Radar = default!;
        private PrefabSystem m_PrefabSystem = default!;
        private SimulationSystem m_SimulationSystem = default!;
        private TimeSystem m_TimeSystem = default!;
        private EventChainSystem m_EventChain = default!;
        private Game.UI.NameSystem? m_NameSystem;   // 惰性解析（同 EventChainSystem 纪律）

        private PetitionState m_State = PetitionState.Idle;
        private string m_Theme = "";        // 请愿主题（"失业率高、工作难找的问题"，作"对"的宾语）
        private string m_MayorSeenAt = "";  // 触发时市长帖快照：窗口内市长帖变化 = 回应
        private Entity m_Venue;
        private string m_VenueLabel = "";
        private uint m_StateUntil;          // 当前状态截止帧
        private uint m_CooldownUntil;       // 冷却截止帧（Idle 检查）
        private int m_Scale;                // 注入总量（petitionScale × 写回档位）
        private int m_InjectedTotal;
        private int m_AttendancePeak;
        private bool m_CatalogLogged;       // 标志性建筑名录只摸一次底

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CitizenQuery = GetEntityQuery(ComponentType.ReadOnly<Citizen>());
            m_InjectQuery = GetEntityQuery(
                ComponentType.ReadOnly<Citizen>(),
                ComponentType.ReadWrite<TripNeeded>(),
                ComponentType.Exclude<HealthProblem>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_AttendanceQuery = GetEntityQuery(
                ComponentType.ReadOnly<Citizen>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            // 聚集点：标志性建筑（Game.Prefabs.SignatureBuildingData 是实体侧空标记组件，dump 实锤）
            m_SignatureQuery = GetEntityQuery(
                ComponentType.ReadOnly<SignatureBuildingData>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_TimeDataQuery = GetEntityQuery(ComponentType.ReadOnly<Game.Common.TimeData>());
            m_TimeSettingsQuery = GetEntityQuery(ComponentType.ReadOnly<Game.Prefabs.TimeSettingsData>());
            m_Radar = World.GetOrCreateSystemManaged<TopicRadarSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            m_EventChain = World.GetOrCreateSystemManaged<EventChainSystem>();
            RequireForUpdate(m_CitizenQuery);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 1024;

        private uint Now => (uint)m_SimulationSystem.frameIndex;
        private static uint TicksPerHour => (uint)Math.Max(1, TimeSystem.kTicksPerDay / 24);

        private int CurrentHour()
        {
            var settings = m_TimeSettingsQuery.GetSingleton<Game.Prefabs.TimeSettingsData>();
            var data = m_TimeDataQuery.GetSingleton<Game.Common.TimeData>();
            var tod = m_TimeSystem.GetTimeOfDay(settings, data, m_SimulationSystem.frameIndex);
            return Math.Clamp((int)(tod * 24f), 0, 23);
        }

        protected override void OnUpdate()
        {
            switch (m_State)
            {
                case PetitionState.Idle:
                    TryTrigger();
                    break;

                case PetitionState.Voiced:
                    if (MayorResponded())
                    {
                        Responded();
                        break;
                    }
                    if (Now < m_StateUntil)
                        break;
                    var hour = CurrentHour();
                    if (hour < 7)
                    {
                        // 凌晨阴间不聚集：窗口顺延到 7:00（军规：阴间时段没人来还挨骂）
                        m_StateUntil = Now + (uint)(7 - hour) * TicksPerHour;
                        Mod.Log.Info("[Petition] 超时未回应，凌晨阴间——聚集判断推迟到 7:00");
                        break;
                    }
                    BeginGathering();
                    break;

                case PetitionState.Gathering:
                    TickGathering();
                    break;

                case PetitionState.Cooldown:
                    if (Now >= m_StateUntil)
                        m_State = PetitionState.Idle;
                    break;
            }
        }

        // —— Idle：触发判定（全执行层，LLM 不握触发权）——
        private void TryTrigger()
        {
            if (!Content.ModSettings.PetitionEnabled || Now < m_CooldownUntil || m_EventChain.IsActive)
                return;
            var s = m_Radar.Latest;
            if (s.Citizens < k_MinPopulation)
                return;

            // 主题 = 最差维度（锚点可审计：全是 TopicRadar 真实读数）；petitionDebug 绕过情绪阈值（测试用）
            var theme = PickTheme(s);
            if (theme == null)
            {
                if (!Content.ModSettings.PetitionDebug)
                    return;
                theme = "生活里的各种不舒心";
            }

            // 抽签 1/4（防机械触发；帧计数+人口当噪声源，要的是"不每次都来"，不要真随机）
            if (((Now / 1024) + (uint)s.Citizens) % 4 != 0)
                return;

            m_Theme = theme;
            m_MayorSeenAt = Content.LiveContext.MayorPost ?? "";
            m_State = PetitionState.Voiced;
            m_StateUntil = Now + (uint)Content.ModSettings.PetitionWindowH * TicksPerHour;
            Content.LiveContext.PendingPetition = m_Theme; // 导演下一炉变怨气热议串
            Mod.Feed.Record(new Content.Post("城市快讯", $"市民正在联署：要求市长回应{m_Theme}。",
                                             Content.Topic.Petition, "newsflash"));
            Mod.Log.Info($"[Petition] 触发：{m_Theme}（窗口 {Content.ModSettings.PetitionWindowH}h 等市长回应）");
        }

        /// <summary>主题选择：失业/幸福/无家可归三维度超标程度取最严重者；全未超标返回 null。</summary>
        private static string? PickTheme(in Content.CitySnapshot s)
        {
            var u = s.UnemploymentPercent - 12f;  // 失业率超标量
            var h = 45f - s.Happiness;            // 幸福度缺口
            var w = s.HomelessPercent - 5f;       // 无家可归率超标量（计数直算）
            if (u <= 0 && h <= 0 && w <= 0)
                return null;
            if (u >= h && u >= w)
                return "失业率高、工作难找的问题";
            if (h >= w)
                return "生活质量差、日子越过越不舒心的问题";
            return "无家可归者越来越多的问题";
        }

        /// <summary>窗口内市长发帖 = 回应（v1 不做语义匹配：任何发言都算；MayorPost 自身有三炉讨论惯性，天然合流）。</summary>
        private bool MayorResponded()
            => Content.LiveContext.MayorPost != null && Content.LiveContext.MayorPost != m_MayorSeenAt;

        // —— 市长回应（Voiced 期内）：议论合流后平息 ——
        private void Responded()
        {
            Mod.Feed.Record(new Content.Post("城市快讯", $"市长回应了关于{m_Theme}的联署，评论区还在吵。",
                                             Content.Topic.Petition, "newsflash"));
            Content.LiveContext.PetitionResolved = $"市长回应了{m_Theme}";
            GoCooldown("市长已回应");
        }

        // —— 超时未回应：聚集市政厅/地标（fallback：无聚集点降级纯舆情）——
        private void BeginGathering()
        {
            m_Venue = ResolveVenue(out m_VenueLabel);
            if (m_Venue == Entity.Null)
            {
                Mod.Feed.Record(new Content.Post("城市快讯", $"关于{m_Theme}的联署还在继续，市长一直没有回应。",
                                                 Content.Topic.Petition, "newsflash"));
                Content.LiveContext.PetitionResolved = $"{m_Theme}仍未解决，市长没有回应";
                Mod.Log.Info("[Petition] 无可用聚集点（没市政厅/地标），聚集降级为纯舆情");
                GoCooldown("超时无回应（降级）");
                return;
            }

            var tierScale = Content.ModSettings.WriteBackTier == "mild" ? 0.5f
                : Content.ModSettings.WriteBackTier == "crazy" ? 2f : 1f;
            m_Scale = (int)(Content.ModSettings.PetitionScale * tierScale);
            m_InjectedTotal = 0;
            m_AttendancePeak = 0;
            m_State = PetitionState.Gathering;
            m_StateUntil = Now + k_GatherHours * TicksPerHour;
            Mod.Feed.Record(new Content.Post("现场直击", $"市民开始聚集在{m_VenueLabel}，要求市长回应{m_Theme}。",
                                             Content.Topic.Petition, "live"), m_Venue.Index, m_Venue.Version);
            Mod.Log.Info($"[Petition] 超时未回应，聚集开始：{m_VenueLabel}（规模 {m_Scale}，{k_GatherHours}h）");
        }

        private void TickGathering()
        {
            if (!EntityManager.Exists(m_Venue))
            {
                Disperse("聚集点消失", responded: false);
                return;
            }
            if (MayorResponded())
            {
                Disperse("市长回应", responded: true); // 聚集期间回应也管用：人群散去
                return;
            }

            // 分批错峰注入（同活动链纪律：每轮补总量的 1/6）
            if (m_InjectedTotal < m_Scale)
            {
                var batch = Math.Max(1, m_Scale / 6);
                m_InjectedTotal += CrowdInjector.InjectBatch(EntityManager, m_InjectQuery, m_Venue, batch, 128);
            }
            var (onSite, onTheWay) = CrowdCount.At(EntityManager, m_AttendanceQuery, m_Venue);
            m_AttendancePeak = Math.Max(m_AttendancePeak, onSite + onTheWay);

            if (Now >= m_StateUntil)
                Disperse("到时散去", responded: false);
        }

        private void Disperse(string reason, bool responded)
        {
            var text = responded
                ? $"市长回应后，聚集在{m_VenueLabel}的人群陆续散去。"
                : $"聚集在{m_VenueLabel}的人群陆续散去，但{m_Theme}的问题还在（到场峰值约 {m_AttendancePeak} 人）。";
            Mod.Feed.Record(new Content.Post("城市快讯", text, Content.Topic.Petition, "newsflash"),
                            m_Venue.Index, m_Venue.Version);
            Content.LiveContext.PetitionResolved = responded
                ? $"市长在聚集期间回应了{m_Theme}"
                : $"聚集散去，{m_Theme}仍未解决";
            GoCooldown(reason);
        }

        private void GoCooldown(string reason)
        {
            m_State = PetitionState.Cooldown;
            m_StateUntil = Now + (uint)Content.ModSettings.PetitionCooldownH * TicksPerHour;
            m_CooldownUntil = m_StateUntil;
            m_InjectedTotal = 0;
            m_AttendancePeak = 0;
            Mod.Log.Info($"[Petition] 平息（{reason}），冷却 {Content.ModSettings.PetitionCooldownH}h");
        }

        /// <summary>
        /// 聚集点 fallback 链：prefab 名含 "CityHall" 的标志性建筑（=市政厅）→ 任意标志性建筑（地标）→ Null（降级）。
        /// 首次解析打名录日志（分类学"先摸后扩"纪律）。
        /// </summary>
        private Entity ResolveVenue(out string label)
        {
            var arr = m_SignatureQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                if (!m_CatalogLogged)
                {
                    m_CatalogLogged = true;
                    var names = new System.Text.StringBuilder();
                    for (int i = 0; i < arr.Length && i < 12; i++)
                    {
                        if (i > 0) names.Append(", ");
                        names.Append(PrefabNameOf(arr[i]));
                    }
                    Mod.Log.Info($"[Petition] 标志性建筑名录（{arr.Length}）：{names}");
                }

                Entity first = Entity.Null;
                string firstLabel = "";
                foreach (var e in arr)
                {
                    var prefabName = PrefabNameOf(e);
                    var pos = EntityManager.GetComponentData<Transform>(e).m_Position;
                    if (prefabName.Contains("CityHall", StringComparison.OrdinalIgnoreCase))
                    {
                        label = Label(e, pos, "市政厅");
                        return e;
                    }
                    if (first == Entity.Null)
                    {
                        first = e;
                        firstLabel = Label(e, pos, prefabName.Replace('_', ' '));
                    }
                }
                label = firstLabel;
                return first;
            }
            finally
            {
                arr.Dispose();
            }
        }

        /// <summary>聚集点标签："城西·市政厅"——方位 + 真名（NameSystem 拿不到用兜底名）。</summary>
        private string Label(Entity building, float3 pos, string fallback)
        {
            string? real = null;
            m_NameSystem ??= World.GetExistingSystemManaged<Game.UI.NameSystem>();
            if (m_NameSystem != null)
                real = m_NameSystem.GetRenderedLabelName(building);
            if (string.IsNullOrEmpty(real))
                real = fallback;
            return Geo.DirectionOf(pos) + "·" + real;
        }

        private string PrefabNameOf(Entity entity)
        {
            var prefabRef = EntityManager.GetComponentData<PrefabRef>(entity);
            return m_PrefabSystem.TryGetPrefab(prefabRef.m_Prefab, out PrefabBase prefab) ? prefab.name : "?";
        }
    }
}
