using System;
using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Pathfind;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 商家广告层 v1（2026-08-20 玩家定案：降级纯舆情+氛围——购物注入/改道四轮 spike 实锤
    /// "制造真实购买"此路不通；不伪造行为系统内部购物动作——铁律"加 System 不改游戏"）。
    ///
    /// 闭环：锚点信号（盈利差/空缺/盈利好）→ LLM 广告帖（店铺账号口吻，真实店名+产出业态）
    /// → 评论互动炉（市民问价/质疑/安利）→ **打折广告带氛围排队**（Leisure 通道，活动链已验证；
    /// 老年人隔老远都来、距离太远不去——玩家最初设想的响应规则）。
    ///
    /// 红线：只动人流与舆情，不碰任何经济数值（不承诺实质性，发布文案照实写）。
    /// 频率闸：全局 shopAdCooldownH（默认 12h）+ 同店 72h 冷却；注入规模走写回档位。
    /// </summary>
    public partial class ShopAdSystem : GameSystemBase
    {
        private const uint k_ShopCooldownH = 72;
        private const int k_RushBase = 30;   // 氛围排队基数（normal 档）

        private EntityQuery m_InjectQuery = default!;
        private EntityAnchorSystem m_AnchorSystem = default!;
        private SimulationSystem m_Simulation = default!;

        private uint m_NextAdAt;
        private readonly Dictionary<Entity, uint> m_ShopCooldownUntil = new();

        // 在飞广告（等 LLM 结果）
        private Entity m_PendingShop;
        private string m_PendingLabel = "";
        private AnchorKind m_PendingKind;

        // 氛围排队（等开场/分批注入）
        private Entity m_RushShop;
        private uint m_RushAt;
        private int m_RushTotal;
        private int m_RushInjected;
        private int m_RushBatches;

        private static string? s_Head; // 广告炉固定头（首发时拼一次，缓存纪律）

        protected override void OnCreate()
        {
            base.OnCreate();
            m_InjectQuery = GetEntityQuery(
                ComponentType.ReadOnly<Citizen>(),
                ComponentType.ReadWrite<TripNeeded>(),
                ComponentType.Exclude<HealthProblem>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_AnchorSystem = World.GetOrCreateSystemManaged<EntityAnchorSystem>();
            m_Simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            RequireForUpdate(m_InjectQuery);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 2048;

        private uint Now => (uint)m_Simulation.frameIndex;
        private static uint TicksPerHour => (uint)Math.Max(1, TimeSystem.kTicksPerDay / 24);

        protected override void OnUpdate()
        {
            TickRush();
            if (!Content.ModSettings.ShopAds || Now < m_NextAdAt)
                return;
            if (Mod.Gateway == null || Llm.CliGateway.Mute || m_PendingShop != Entity.Null)
                return; // 供给不可用 / 一炉在飞
            TryIssueAd();
        }

        /// <summary>发广告炉：打折（盈利差）> 招工（空缺）> 新品（盈利好），过冷却 + 有公司实体。</summary>
        private void TryIssueAd()
        {
            var anchors = m_AnchorSystem.Anchors;
            for (int pass = 0; pass < 3; pass++)
            {
                var want = pass == 0 ? AnchorKind.BusinessBad : pass == 1 ? AnchorKind.Hiring : AnchorKind.BusinessGood;
                foreach (var a in anchors)
                {
                    if (a.Kind != want || a.Company == Entity.Null)
                        continue;
                    if (m_ShopCooldownUntil.TryGetValue(a.Entity, out var cd) && Now < cd)
                        continue;

                    m_PendingShop = a.Entity;
                    m_PendingLabel = a.Label;
                    m_PendingKind = want;
                    var word = ShopOutput.WordOf(ShopOutput.OutputOf(EntityManager, a.Company));
                    var reason = want == AnchorKind.BusinessBad
                        ? "最近生意清淡，想搞波清仓打折（折扣/价格你自己编个实在的）"
                        : want == AnchorKind.Hiring
                            ? $"店里缺人想招工（{a.Detail}）"
                            : "上了新品/新花样，想吆喝一声";
                    s_Head ??= Content.PromptBuilder.BuildShopAdHead();
                    Mod.Gateway!.Enqueue(new Llm.CliRequest(
                        Content.PromptBuilder.BuildShopAdPrompt(s_Head, StripParen(a.Label), word, reason),
                        Llm.CliPriority.Low, 180, "ad:1"));
                    m_ShopCooldownUntil[a.Entity] = Now + k_ShopCooldownH * TicksPerHour;
                    m_NextAdAt = Now + (uint)Content.ModSettings.ShopAdCooldownH * TicksPerHour;
                    Mod.Log.Info($"[Ad] 广告炉已发：{a.Label}（{word}，{want}）");
                    return;
                }
            }
        }

        /// <summary>广告炉结果（导演路由 "ad:"）：清洗发帖 → 评论互动炉 → 打折排氛围队。</summary>
        public void OnAdResult(Llm.CliCompletedResult r)
        {
            var shop = m_PendingShop;
            var kind = m_PendingKind;
            var label = m_PendingLabel;
            m_PendingShop = Entity.Null;
            if (!r.Result.Success)
            {
                Mod.Log.Info($"[Ad] 广告炉失败：{r.Result.Error}");
                return;
            }
            var text = Clean(r.Result.Text);
            if (text.Length == 0)
                return;

            var seq = Mod.Feed.Record(new Content.Post(StripParen(label), text, Content.Topic.Daily, "shopad"),
                                      shop.Index, shop.Version);
            Mod.Log.Info($"[Ad] 广告帖 #{seq}：{text.Substring(0, Math.Min(24, text.Length))}…（{label}）");

            // 评论互动炉（复用回应炉固定头，缓存照吃）：市民问价/质疑/安利/踩坑
            if (Mod.Gateway != null && !Llm.CliGateway.Mute && ContentDirectorSystem.ReplyHead != null)
            {
                var count = 2 + (int)(seq % 3); // 2-4 条，别千篇整数
                Mod.Gateway.Enqueue(new Llm.CliRequest(
                    Content.PromptBuilder.BuildReplyPrompt(ContentDirectorSystem.ReplyHead, text, count),
                    Llm.CliPriority.Low, 180, "ad-reply:" + seq));
            }

            // 打折广告 → 1 游戏小时后氛围排队（Leisure 通道，观感不承诺经济效果）
            if (kind == AnchorKind.BusinessBad)
            {
                var tierScale = Content.ModSettings.WriteBackTier == "mild" ? 0.5f
                    : Content.ModSettings.WriteBackTier == "crazy" ? 2f : 1f;
                m_RushShop = shop;
                m_RushAt = Now + TicksPerHour;
                m_RushTotal = (int)(k_RushBase * tierScale);
                m_RushInjected = 0;
                m_RushBatches = 0;
                Mod.Log.Info($"[Ad] 打折排队已排期：{label}（{m_RushTotal} 人，1h 后，氛围无经济效果）");
            }
        }

        /// <summary>正文清洗（同快讯炉纪律）：去引号/换行/JSON 残壳，只留第一行，超长硬截。</summary>
        private static string Clean(string raw)
        {
            var text = raw.Trim();
            var nl = text.IndexOf('\n');
            if (nl > 0)
                text = text.Substring(0, nl);
            text = text.Trim().Trim('"', '"', '"', '{', '}').Trim();
            return text.Length > 80 ? text.Substring(0, 80) : text;
        }

        /// <summary>店名剥路名后缀（"嘎吱脆食品店（神太街）"→"嘎吱脆食品店"）：作者署名用。</summary>
        private static string StripParen(string s)
        {
            var i = s.IndexOf('（');
            return i > 0 ? s.Substring(0, i) : s;
        }

        /// <summary>
        /// 氛围排队（Leisure 通道）：老年 +300（隔老远都来）/ 近距 +200 / 非老年远距 -150（太远不去）。
        /// 分两批错峰；分数为负不硬拽（拒绝也是城市的一部分）。
        /// </summary>
        private void TickRush()
        {
            if (m_RushAt == 0 || Now < m_RushAt)
                return;
            if (!EntityManager.Exists(m_RushShop))
            {
                m_RushAt = 0;
                return;
            }

            var shopPos = EntityManager.HasComponent<Transform>(m_RushShop)
                ? EntityManager.GetComponentData<Transform>(m_RushShop).m_Position
                : float3.zero;
            var arr = m_InjectQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            var scored = new List<(float s, Entity e)>(arr.Length);
            foreach (var citizen in arr)
            {
                var c = EntityManager.GetComponentData<Citizen>(citizen);
                var elderly = (CitizenAge)(int)(c.m_State & (CitizenFlags.AgeBit1 | CitizenFlags.AgeBit2)) == CitizenAge.Elderly;
                float score = citizen.Index * 7919 % 100; // 确定性噪声打散（防总抓同一批）
                if (elderly)
                    score += 300;
                if (Geo.TryGetPos(EntityManager, citizen, out var p))
                {
                    var dist = math.distance(p, shopPos);
                    if (dist < 1500f)
                        score += 200;
                    else if (!elderly && dist > 4000f)
                        score -= 150; // 太远的可能不去（老年人除外）
                }
                scored.Add((score, citizen));
            }
            arr.Dispose();
            scored.Sort((a, b) => b.s.CompareTo(a.s));

            var budget = Math.Min(m_RushTotal - m_RushInjected, Math.Max(1, m_RushTotal / 2));
            var injected = 0;
            foreach (var (s, citizen) in scored)
            {
                if (injected >= budget || s < 0)
                    break;
                var trips = EntityManager.GetBuffer<TripNeeded>(citizen);
                var dup = false;
                for (int i = 0; i < trips.Length; i++)
                {
                    if (trips[i].m_TargetAgent == m_RushShop && trips[i].m_Purpose == Purpose.Leisure)
                    {
                        dup = true;
                        break;
                    }
                }
                if (dup)
                    continue;
                trips.Add(new TripNeeded
                {
                    m_TargetAgent = m_RushShop,
                    m_Purpose = Purpose.Leisure,
                    m_Priority = 128,
                });
                var target = new Target { m_Target = m_RushShop };
                if (EntityManager.HasComponent<Target>(citizen))
                    EntityManager.SetComponentData(citizen, target);
                else
                    EntityManager.AddComponentData(citizen, target);
                var purpose = new TravelPurpose { m_Purpose = Purpose.Leisure };
                if (EntityManager.HasComponent<TravelPurpose>(citizen))
                    EntityManager.SetComponentData(citizen, purpose);
                else
                    EntityManager.AddComponentData(citizen, purpose);
                EntityManager.RemoveComponent<Leisure>(citizen);
                EntityManager.RemoveComponent<PathInformation>(citizen);
                EntityManager.RemoveComponent<PathElement>(citizen);
                injected++;
            }
            m_RushInjected += injected;
            m_RushBatches++;

            if (m_RushInjected >= m_RushTotal || m_RushBatches >= 2)
            {
                Mod.Log.Info($"[Ad] 排队注入完成：{m_RushInjected}/{m_RushTotal} 人（氛围排队，无经济效果）");
                m_RushAt = 0;
            }
            else
            {
                m_RushAt = Now + 256; // 第二批错峰
            }
        }
    }
}
