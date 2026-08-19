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
    /// 活动链系统（写回层 M4 首发闭环）：市长发言 → 意图解析（结构化参数：时间/时长/人数/预算档）
    /// → 原生确认 → 真扣预算 → 公告 → 吸引力 ramp + 分批注入人群 → 实数到场人数 → 结算回补/挨骂 → 恢复。
    ///
    /// 拷问定案（2026-08-20）+ 玩家增补（时段/时长/人数自定义）：
    /// - 时段影响人气：傍晚黄金档全额，凌晨"阴间时段"到场惨淡且必然挨骂（时段系数表，执行层确定计算）；
    /// - 时长负担非线性：费用随（时长-4h）² 超线性加价，拖太长后半场人困马乏进舆情；
    /// - 全城同时仅一个活动；每场馆 24 游戏小时冷却；回补永不超过花费（防套利）；
    /// - 参数写回自限：吸引力 boost 在结算/异常退出时强制恢复（§12 #29）。
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
        private static int s_ConfirmVenueIdx;     // UI 回执：玩家选中的场馆候选下标

        /// <summary>UI 回执入口（CityLifeUISystem 的 trigger 调它）。venueIdx=玩家最终选中的场馆候选。</summary>
        public static void SetConfirmResult(int id, bool ok, int venueIdx = 0)
        {
            s_ConfirmId = id;
            s_ConfirmResult = ok ? 1 : 0;
            s_ConfirmVenueIdx = venueIdx;
        }

        private EntityQuery m_CitizenQuery = default!;
        private EntityQuery m_InjectQuery = default!;
        private EntityQuery m_AttendanceQuery = default!;
        private EntityQuery m_TimeDataQuery = default!;
        private EntityQuery m_TimeSettingsQuery = default!;
        private EntityAnchorSystem m_AnchorSystem = default!;
        private CitySystem m_CitySystem = default!;
        private SimulationSystem m_SimulationSystem = default!;
        private TimeSystem m_TimeSystem = default!;
        private List<Content.EventPack> m_Packs = default!;

        private ChainState m_State = ChainState.Idle;
        private Content.EventPack? m_Pack;
        private Entity m_Venue;
        private string m_VenueLabel = "";
        private readonly List<Anchor> m_Candidates = new(); // 场馆候选（确认卡选择器的数据源）
        private int m_VenueIdx;                              // 当前选中的候选下标
        private int m_BudgetTier = 1;      // 0 低 1 中 2 高
        private int m_Spent;
        private int m_Scale;               // 目标到场人数（LLM 自定义人数 × 写回档位系数）
        private int m_Expected;            // 预期到场 = 规模 × 时段系数（评价的基准线）
        private int m_StartHour = 19;      // 开场整点（默认 19，LLM 可改）
        private int m_DurationH = 4;       // 时长（默认 4，LLM 可改，钳 1-12）
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
            // 时间查询走 EntityQuery.GetSingleton——SystemAPI 依赖 Unity 源码生成器，我们的构建不跑生成器，
            // 用了会在运行时抛 "No suitable code replacement generated"（2026-08-20 CRITICAL 实锤）
            m_TimeDataQuery = GetEntityQuery(ComponentType.ReadOnly<Game.Common.TimeData>());
            m_TimeSettingsQuery = GetEntityQuery(ComponentType.ReadOnly<Game.Prefabs.TimeSettingsData>());
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

        /// <summary>当前时刻（0-23 游戏小时，TimeSystem 官方接口换算；单例走 EntityQuery 不走 SystemAPI）。</summary>
        private int CurrentHour()
        {
            var settings = m_TimeSettingsQuery.GetSingleton<Game.Prefabs.TimeSettingsData>();
            var data = m_TimeDataQuery.GetSingleton<Game.Common.TimeData>();
            var tod = m_TimeSystem.GetTimeOfDay(settings, data, m_SimulationSystem.frameIndex); // 0..1
            return Math.Clamp((int)(tod * 24f), 0, 23);
        }

        /// <summary>距下一个整点目标时刻的 tick 数；已过点则排到明天。</summary>
        private uint TicksUntilHour(int targetHour)
        {
            var hour = CurrentHour();
            var deltaHours = targetHour > hour ? targetHour - hour : targetHour + 24 - hour;
            return (uint)deltaHours * TicksPerHour;
        }

        /// <summary>时段人气系数：傍晚黄金档全额，凌晨"阴间时段"惨淡（玩家增补：阴间排期没人来还挨骂）。</summary>
        private static float TimeFactor(int hour) => hour switch
        {
            >= 18 and <= 21 => 1.0f,   // 黄金档
            >= 14 and <= 17 => 0.7f,   // 下午
            >= 22 and <= 23 => 0.7f,   // 前半夜
            >= 7 and <= 13 => 0.5f,    // 白天（上班时段）
            _ => 0.15f,                // 0-6 点阴间时段
        };

        /// <summary>时长费用系数：4h 内不收附加，超出部分平方级加价（负担非线性，玩家增补）。</summary>
        private static float DurationCostFactor(int durationH)
        {
            var over = Math.Max(0, durationH - 4);
            return 1f + over * over * 0.05f; // 8h→1.8x，12h→4.2x
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

            // 场馆候选：全部公园锚点（冷却中的剔除）；LLM 地点原文能对上标签的当默认推荐，对不上用第一个。
            // 确认卡带选择器，玩家可改（2026-08-20 实机：对不上就随机扔，玩家看不到人）
            var venueText = Util.JsonMini.GetStr(json, "venue") ?? "";
            m_Candidates.Clear();
            var defaultIdx = 0;
            foreach (var a in m_AnchorSystem.Anchors)
            {
                if (a.Kind != AnchorKind.Park)
                    continue;
                if (m_VenueCooldownUntil.TryGetValue(a.Entity, out var cd) && Now < cd)
                    continue; // 冷却中的场馆不进候选
                if (venueText.Length > 0 && (a.Label.Contains(venueText) || venueText.Contains(a.Label)))
                    defaultIdx = m_Candidates.Count;
                m_Candidates.Add(a);
            }
            if (m_Candidates.Count == 0)
            {
                Mod.Log.Info("[Event] 没有可用场馆（没公园或全在冷却），降级纯舆情");
                return;
            }

            // —— 结构化参数（LLM 归一化，缺省给默认）——
            var budgetText = Util.JsonMini.GetStr(json, "budget") ?? "";
            m_BudgetTier = budgetText.Contains("低") ? 0 : budgetText.Contains("高") ? 2 : 1;
            m_StartHour = Math.Clamp(Util.JsonMini.GetInt(json, "startHour") ?? 19, 0, 23);
            m_DurationH = Math.Clamp(Util.JsonMini.GetInt(json, "durationH") ?? 4, 1, 12);
            var tierScale = Content.ModSettings.WriteBackTier == "mild" ? 0.5f
                : Content.ModSettings.WriteBackTier == "crazy" ? 2f : 1f;
            var wantScale = Util.JsonMini.GetInt(json, "scale") ?? pack.Scale;
            m_Scale = (int)(Math.Clamp(wantScale, 50, 2000) * tierScale);
            m_Expected = Math.Max(1, (int)(m_Scale * TimeFactor(m_StartHour)));

            m_Pack = pack;
            m_VenueIdx = defaultIdx;
            m_Venue = m_Candidates[defaultIdx].Entity;
            m_VenueLabel = m_Candidates[defaultIdx].Label;
            m_CardId++;

            // 费用 = 预算档 × 时长费用系数（非线性）
            var cost = (int)(pack.Budgets[m_BudgetTier] * DurationCostFactor(m_DurationH));
            var hour = CurrentHour();
            var whenText = m_StartHour > hour ? $"今晚 {m_StartHour}:00" : $"明晚 {m_StartHour}:00";
            var timeWarn = TimeFactor(m_StartHour) <= 0.3f;

            // 候选场馆标签数组（确认卡选择器）
            var venueArr = new System.Text.StringBuilder();
            for (int i = 0; i < m_Candidates.Count; i++)
            {
                if (i > 0) venueArr.Append(',');
                venueArr.Append('\"').Append(m_Candidates[i].Label).Append('\"');
            }

            PendingConfirmJson = "{\"id\":" + m_CardId + ",\"title\":\"活动确认\",\"lines\":["
                + $"\"活动：{pack.Name}\",\"地点：{m_VenueLabel}\","
                + $"\"预算：{BudgetTierName(m_BudgetTier)}（约 {cost / 10000} 万，含时长系数，从财政真扣）\","
                + $"\"开始：{whenText}（时长 {m_DurationH} 小时）\",\"预计到场：约 {m_Expected} 人（规模 {m_Scale} × 时段系数）\""
                + (timeWarn ? ",\"⚠ 时段阴间，预计人气惨淡，市民可能开骂\"" : "")
                + "],\"danger\":" + (Content.ModSettings.WriteBackTier == "crazy" || timeWarn ? "true" : "false")
                + ",\"venues\":[" + venueArr + "],\"venueIdx\":" + defaultIdx + "}";
            PendingConfirmVersion++;
            m_State = ChainState.AwaitingConfirm;
            Mod.Log.Info($"[Event] 待确认：{pack.Name} @ {m_VenueLabel} {whenText}，等玩家确认");
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
                        if (ok)
                        {
                            // 玩家可能在确认卡里换了场馆——以回执下标为准
                            var pick = Math.Clamp(s_ConfirmVenueIdx, 0, m_Candidates.Count - 1);
                            m_VenueIdx = pick;
                            m_Venue = m_Candidates[pick].Entity;
                            m_VenueLabel = m_Candidates[pick].Label;
                            ConfirmAndSchedule();
                        }
                        else
                        {
                            Cancel("玩家取消");
                        }
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
            // 真扣预算（含时长系数的总价；拷问定案：回补永不超过花费）
            var cost = (int)(m_Pack!.Budgets[m_BudgetTier] * DurationCostFactor(m_DurationH));
            if (MoneyOps.TryAdjust(EntityManager, m_CitySystem.City, -cost, out _, msg => Mod.Log.Info(msg)))
                m_Spent = cost;
            else
                m_Spent = 0;

            m_StartFrame = Now + TicksUntilHour(m_StartHour); // 下一个目标整点开场（游戏时钟，不通宵）
            m_VenueCooldownUntil[m_Venue] = Now + TicksPerHour * (uint)m_Pack!.CooldownH;
            m_State = ChainState.Scheduled;
            var whenText = m_StartHour > CurrentHour() ? $"今晚 {m_StartHour}:00" : $"明晚 {m_StartHour}:00";
            OfficialPost($"公告：{m_Pack.Name}将于{whenText}在{m_VenueLabel}举办，时长 {m_DurationH} 小时，欢迎市民前往。", m_Venue);
            Mod.Log.Info($"[Event] 已排期：{m_Pack.Name} @ {m_VenueLabel} {whenText} 开场");
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
            m_EndFrame = Now + TicksPerHour * (uint)m_DurationH;
            m_State = ChainState.Running;
            Content.LiveContext.OngoingEvent = $"{m_Pack!.Name}（{m_VenueLabel}）"; // 信息流实时跟着活动走
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

            // 分批错峰注入：每次补 scale 的 1/6，直到足额（时段差即少注——预期低就不硬灌）
            var targetTotal = m_Expected;
            if (m_InjectedTotal < targetTotal)
            {
                var batch = Math.Max(1, targetTotal / 6);
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

            var ratio = m_AttendancePeak / (float)Math.Max(1, m_Expected);
            string outcome;
            if (ratio >= 0.8f)
            {
                var rebate = m_Spent / 2; // 爆棚返 50%（净收益恒负，防套利）
                if (rebate > 0)
                    MoneyOps.TryAdjust(EntityManager, m_CitySystem.City, rebate, out _, msg => Mod.Log.Info(msg));
                outcome = $"爆棚——到场峰值 {m_AttendancePeak} 人（预期 {m_Expected}），口碑爆了，财政返还 {rebate / 10000} 万";
            }
            else if (ratio >= 0.4f)
            {
                var rebate = m_Spent / 5; // 及格返 20%
                if (rebate > 0)
                    MoneyOps.TryAdjust(EntityManager, m_CitySystem.City, rebate, out _, msg => Mod.Log.Info(msg));
                outcome = $"及格——到场峰值 {m_AttendancePeak} 人（预期 {m_Expected}），返还 {rebate / 10000} 万";
            }
            else
            {
                outcome = $"冷场——到场峰值 {m_AttendancePeak} 人（预期 {m_Expected}），预算打了水漂";
            }

            // 舆情调味（玩家增补）：阴间排期必挨骂；拖太长人困马乏
            var tf = TimeFactor(m_StartHour);
            if (tf <= 0.3f)
                outcome += "；排期实在阴间（凌晨开活动），市民怨声载道";
            else if (m_DurationH >= 8)
                outcome += "；拖得太长，后半场人困马乏，吐槽不少";

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
