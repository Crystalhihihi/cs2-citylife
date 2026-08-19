using System;
using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Simulation;
using Unity.Entities;

namespace CityLife.GameBridge
{
    /// <summary>活动链状态。</summary>
    public enum ChainState { Idle, AwaitingConfirm, Scheduled, Running, Cooldown }

    /// <summary>
    /// 活动链系统（写回层 M4 首发闭环）：市长发言 → 意图解析 → 原生确认 →
    /// 真扣预算 → 公告 → 吸引力 ramp + 分批注入人群 → 实数到场人数 → 结算回补/挨骂 → 恢复。
    ///
    /// 拷问定案（2026-08-20）纪律：
    /// - 全城同时仅一个活动；每场馆 24 游戏小时冷却；回补永不超过花费（防套利）；
    /// - 参数写回自限：吸引力 boost 在结算/异常退出时强制恢复（§12 #29）；
    /// - 数到场人数是实数（TravelPurpose=Leisure 且 Target=场馆 + CurrentBuilding=场馆）；
    /// - 取消确认 → 轻舆情反噬（官方帖说明，无数值惩罚）。
    /// </summary>
    public partial class EventChainSystem : GameSystemBase
    {
        // —— 与 UI/导演的静态接口（主线程） ——
        /// <summary>待确认活动的 JSON（空串=无）。版本号变了 UI 才推。</summary>
        public static string PendingConfirmJson = "";
        public static int PendingConfirmVersion;
        /// <summary>意图解析的固定 prompt 头（OnCreate 拼好，CityLifeUISystem 发炉取用）。</summary>
        public static string? IntentHead;

        private static int s_ConfirmId = -1;      // 待确认的卡片 id
        private static int s_ConfirmResult = -1;  // UI 回执：1 确认 0 取消

        /// <summary>UI 回执入口（CityLifeUISystem 的 trigger 调它）。</summary>
        public static void SetConfirmResult(int id, bool ok)
        {
            s_ConfirmId = id;
            s_ConfirmResult = ok ? 1 : 0;
        }

        private EntityQuery m_CitizenQuery = default!;
        private EntityQuery m_InjectQuery = default!;
        private EntityQuery m_AttendanceQuery = default!;
        private EntityAnchorSystem m_AnchorSystem = default!;
        private CitySystem m_CitySystem = default!;
        private SimulationSystem m_SimulationSystem = default!;
        private TimeSystem m_TimeSystem = default!;
        private List<Content.EventPack> m_Packs = default!;

        private ChainState m_State = ChainState.Idle;
        private Content.EventPack? m_Pack;
        private Entity m_Venue;
        private string m_VenueLabel = "";
        private int m_BudgetTier = 1;      // 0 低 1 中 2 高
        private int m_Spent;
        private int m_Scale;               // 目标到场人数（已乘写回档位系数）
        private uint m_StartFrame;
        private uint m_EndFrame;
        private int m_InjectedTotal;
        private int m_AttendancePeak;
        private int m_OriginalAttr;
        private bool m_AttractionBoosted;
        private int m_CardId;
        private readonly Dictionary<Entity, uint> m_VenueCooldownUntil = new();

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
            m_AnchorSystem = World.GetOrCreateSystemManaged<EntityAnchorSystem>();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();

            var cfgDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"..\LocalLow\Colossal Order\Cities Skylines II\ModsSettings\CityLife"));
            m_Packs = Content.EventPack.Load(
                System.IO.Path.Combine(cfgDir, "eventpacks.jsonl"), msg => Mod.Log.Info(msg));
            IntentHead = Content.PromptBuilder.BuildIntentHead(m_Packs);

            RequireForUpdate(m_CitizenQuery);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 256;

        private uint Now => (uint)m_SimulationSystem.frameIndex;
        private static uint TicksPerHour => (uint)Math.Max(1, TimeSystem.kTicksPerDay / 24);

        /// <summary>当前时刻（0-23 游戏小时，从 TimeSystem 官方接口换算）。</summary>
        private int CurrentHour()
        {
            var settings = SystemAPI.GetSingleton<Game.Prefabs.TimeSettingsData>();
            var data = SystemAPI.GetSingleton<Game.Common.TimeData>();
            var tod = m_TimeSystem.GetTimeOfDay(settings, data, m_SimulationSystem.frameIndex); // 0..1
            return Math.Clamp((int)(tod * 24f), 0, 23);
        }

        /// <summary>距下一个整点目标时刻（如 19:00）的 tick 数；已过点则排到明天。</summary>
        private uint TicksUntilHour(int targetHour)
        {
            var hour = CurrentHour();
            var deltaHours = targetHour > hour ? targetHour - hour : targetHour + 24 - hour;
            return (uint)deltaHours * TicksPerHour;
        }

        /// <summary>导演路由入口：意图解析结果（requestId 前缀 "intent:"）。</summary>
        public void OnIntentJson(string json)
        {
            if (m_State != ChainState.Idle)
            {
                Mod.Log.Info("[Event] 活动进行中，本次发言不按事件处理");
                return;
            }

            var packId = Util.JsonMini.GetStr(json, "pack");
            if (string.IsNullOrEmpty(packId) || packId == "null")
                return; // 非活动意图——正常走舆情层

            var pack = m_Packs.Find(p => p.Id == packId);
            if (pack == null)
            {
                Mod.Log.Info($"[Event] LLM 映射到不存在的事件包 {packId}，降级纯舆情");
                return;
            }

            // 场馆解析：LLM 给的地点原文先和公园锚点标签对，对不上用第一个公园锚点
            var venueText = Util.JsonMini.GetStr(json, "venue") ?? "";
            var venue = Entity.Null;
            var venueLabel = "";
            foreach (var a in m_AnchorSystem.Anchors)
            {
                if (a.Kind != AnchorKind.Park)
                    continue;
                if (venueText.Length > 0 && (a.Label.Contains(venueText) || venueText.Contains(a.Label)))
                {
                    venue = a.Entity;
                    venueLabel = a.Label;
                    break;
                }
                if (venue == Entity.Null)
                {
                    venue = a.Entity;
                    venueLabel = a.Label;
                }
            }
            if (venue == Entity.Null)
            {
                Mod.Log.Info("[Event] 没有可用场馆（城里没公园/景点），降级纯舆情");
                return;
            }
            if (m_VenueCooldownUntil.TryGetValue(venue, out var until) && Now < until)
            {
                Mod.Log.Info($"[Event] {venueLabel} 冷却中，降级纯舆情");
                return;
            }

            // 预算档（LLM 原文→档位，默认中档）+ 规模（写回档位系数）
            var budgetText = Util.JsonMini.GetStr(json, "budget") ?? "";
            m_BudgetTier = budgetText.Contains("低") ? 0 : budgetText.Contains("高") ? 2 : 1;
            var tierScale = Content.ModSettings.WriteBackTier == "mild" ? 0.5f
                : Content.ModSettings.WriteBackTier == "crazy" ? 2f : 1f;
            m_Scale = (int)(pack.Scale * tierScale);

            m_Pack = pack;
            m_Venue = venue;
            m_VenueLabel = venueLabel;
            m_CardId++;

            // 开始时刻按游戏时钟排：下一个 19:00（傍晚场，19:00-23:00 收摊，不通宵）
            var whenText = 19 > CurrentHour() ? "今晚 19:00" : "明晚 19:00";

            PendingConfirmJson = "{\"id\":" + m_CardId + ",\"title\":\"活动确认\",\"lines\":["
                + $"\"活动：{pack.Name}\",\"地点：{venueLabel}\","
                + $"\"预算：{BudgetTierName(m_BudgetTier)}（{pack.Budgets[m_BudgetTier] / 10000}万，从财政真扣）\","
                + $"\"开始：{whenText}（时长 4 小时）\",\"预计规模：约 {m_Scale} 人\""
                + "],\"danger\":" + (Content.ModSettings.WriteBackTier == "crazy" ? "true" : "false") + "}";
            PendingConfirmVersion++;
            m_State = ChainState.AwaitingConfirm;
            Mod.Log.Info($"[Event] 待确认：{pack.Name} @ {venueLabel}，等玩家确认");
        }

        private static string BudgetTierName(int tier) => tier == 0 ? "低档" : tier == 2 ? "高档" : "中档";

        protected override void OnUpdate()
        {
            switch (m_State)
            {
                case ChainState.AwaitingConfirm:
                    if (s_ConfirmId == m_CardId && s_ConfirmResult >= 0)
                    {
                        var ok = s_ConfirmResult == 1;
                        s_ConfirmId = -1;
                        s_ConfirmResult = -1;
                        PendingConfirmJson = "";
                        PendingConfirmVersion++;
                        if (ok) ConfirmAndSchedule();
                        else Cancel("玩家取消");
                    }
                    break;

                case ChainState.Scheduled:
                    if (Now >= m_StartFrame)
                        BeginRunning();
                    break;

                case ChainState.Running:
                    TickRunning();
                    break;

                case ChainState.Cooldown:
                    if (Now >= m_EndFrame)
                        m_State = ChainState.Idle;
                    break;
            }
        }

        private void ConfirmAndSchedule()
        {
            // 真扣预算（拷问定案）；不可写（无限钱模式）则零成本继续
            if (MoneyOps.TryAdjust(EntityManager, m_CitySystem.City, -m_Pack!.Budgets[m_BudgetTier], out _, msg => Mod.Log.Info(msg)))
                m_Spent = m_Pack.Budgets[m_BudgetTier];
            else
                m_Spent = 0;

            m_StartFrame = Now + TicksUntilHour(19); // 下一个 19:00 开场（按游戏时钟，不通宵）
            m_VenueCooldownUntil[m_Venue] = Now + TicksPerHour * (uint)m_Pack!.CooldownH;
            m_State = ChainState.Scheduled;
            var whenText = 19 > CurrentHour() ? "今晚 19:00" : "明晚 19:00";
            OfficialPost($"公告：{m_Pack.Name}将于{whenText}在{m_VenueLabel}举办，欢迎市民前往。", m_Venue);
            Mod.Log.Info($"[Event] 已排期：{m_Pack.Name} @ {m_VenueLabel}，{m_StartFrame} 开场");
        }

        private void Cancel(string reason)
        {
            OfficialPost($"公告取消：{m_Pack?.Name ?? "活动"}（{m_VenueLabel}）取消举办。（{reason}）");
            m_State = ChainState.Idle;
            Mod.Log.Info($"[Event] 取消：{reason}");
        }

        private void BeginRunning()
        {
            // 吸引力 ramp（初估值：原值 + 100；校准日志留证，分布摸清后调）
            m_AttractionBoosted = AttractionRamp.TryBoost(EntityManager, m_Venue, m_OriginalAttr + 100, out m_OriginalAttr);
            m_EndFrame = Now + TicksPerHour * (uint)m_Pack!.DurationH;
            m_State = ChainState.Running;
            Content.LiveContext.OngoingEvent = $"{m_Pack.Name}（{m_VenueLabel}）"; // 信息流实时跟着活动走
            OfficialPost($"现场：{m_Pack.Name}在{m_VenueLabel}开场了，市民正在前往。", m_Venue);
            Mod.Log.Info($"[Event] 开场：{m_Pack.Name} @ {m_VenueLabel}（吸引力 boost={(m_AttractionBoosted ? "OK" : "跳过")}）");
        }

        private void TickRunning()
        {
            // 场馆被拆/失效 → 提前结算（恢复优先，军规③）
            if (!EntityManager.Exists(m_Venue))
            {
                Mod.Log.Info("[Event] 场馆消失，提前结算");
                Evaluate();
                return;
            }

            // 分批错峰注入：每次补 scale 的 1/6，直到足额
            if (m_InjectedTotal < m_Scale)
            {
                var batch = Math.Max(1, m_Scale / 6);
                m_InjectedTotal += CrowdInjector.InjectBatch(EntityManager, m_InjectQuery, m_Venue, batch, 128);
            }

            // 实数到场： Leisure 目标=场馆的（在路上）+ 人已经在场馆的
            int onTheWay = 0, onSite = 0;
            var arr = m_AttendanceQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            foreach (var citizen in arr)
            {
                if (EntityManager.HasComponent<CurrentBuilding>(citizen)
                    && EntityManager.GetComponentData<CurrentBuilding>(citizen).m_CurrentBuilding == m_Venue)
                {
                    onSite++;
                    continue;
                }
                if (EntityManager.HasComponent<TravelPurpose>(citizen)
                    && EntityManager.GetComponentData<TravelPurpose>(citizen).m_Purpose == Purpose.Leisure
                    && EntityManager.HasComponent<Target>(citizen)
                    && EntityManager.GetComponentData<Target>(citizen).m_Target == m_Venue)
                {
                    onTheWay++;
                }
            }
            arr.Dispose();
            m_AttendancePeak = Math.Max(m_AttendancePeak, onSite + onTheWay);

            if (Now >= m_EndFrame)
                Evaluate();
        }

        private void Evaluate()
        {
            // 恢复优先（军规③：参数写回自限）
            if (m_AttractionBoosted)
            {
                AttractionRamp.Restore(EntityManager, m_Venue, m_OriginalAttr);
                m_AttractionBoosted = false;
            }

            var ratio = m_Scale > 0 ? (float)m_AttendancePeak / m_Scale : 0f;
            string outcome;
            if (ratio >= 0.8f)
            {
                var rebate = m_Spent / 2; // 爆棚返 50%（净收益恒负，防套利）
                if (rebate > 0)
                    MoneyOps.TryAdjust(EntityManager, m_CitySystem.City, rebate, out _, msg => Mod.Log.Info(msg));
                outcome = $"爆棚——到场峰值 {m_AttendancePeak} 人（目标 {m_Scale}），口碑爆了，财政返还 {rebate / 10000} 万";
            }
            else if (ratio >= 0.4f)
            {
                var rebate = m_Spent / 5; // 及格返 20%
                if (rebate > 0)
                    MoneyOps.TryAdjust(EntityManager, m_CitySystem.City, rebate, out _, msg => Mod.Log.Info(msg));
                outcome = $"及格——到场峰值 {m_AttendancePeak} 人（目标 {m_Scale}），返还 {rebate / 10000} 万";
            }
            else
            {
                outcome = $"冷场——到场峰值 {m_AttendancePeak} 人（目标 {m_Scale}），预算打了水漂";
            }

            OfficialPost($"活动落幕：{m_Pack?.Name}（{m_VenueLabel}）{outcome}。", m_Venue);
            Content.LiveContext.LastEventOutcome = $"{m_Pack?.Name}（{m_VenueLabel}）{outcome}";
            Content.LiveContext.OngoingEvent = null;
            Mod.Log.Info($"[Event] 结算：{outcome}");

            m_State = ChainState.Cooldown;
            m_EndFrame = Now + TicksPerHour; // 全局冷静 1 游戏小时
            m_Pack = null;
            m_InjectedTotal = 0;
            m_AttendancePeak = 0;
            m_Spent = 0;
        }

        /// <summary>官方号发帖（市政厅口吻，活动链全程的公共播报通道）。带场馆实体坐标——面板可点击"前往现场"。</summary>
        private static void OfficialPost(string text, Entity venue = default)
        {
            if (venue != Entity.Null)
                Mod.Feed.Record(new Content.Post("市政厅", text, Content.Topic.Breaking, "official"),
                                venue.Index, venue.Version);
            else
                Mod.Feed.Record(new Content.Post("市政厅", text, Content.Topic.Breaking, "official"));
        }
    }
}
