using System;
using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Resources = Game.Economy.Resources;
using Transform = Game.Objects.Transform;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 实质性 spike（2026-08-20）：购物注入/改道是否真成交？（验证完成后拆除）
    ///
    /// 背景（玩家追问）：原版购物是**需求驱动**——家庭缺货 → 行为系统派人买
    /// （TripNeeded{Purpose.Shopping, m_Resource=缺的货}）→ ResourceBuyerSystem 结算
    /// （m_SalesQueue 销售事件队列实锤）。模拟里没有"广告"概念。
    /// 所以广告层若建，正确形态只能是**重定向既有需求**（打折=抢别家生意），不是创造需求。
    ///
    /// 第一轮教训（17:31 日志）：全城随机注入+4096 帧窗口——没人走到就出了"结论"，实验无效。
    /// 第二轮修正：就近注入、窗口 16384 帧、双口径到店（到店瞬间目的可能翻转）。
    ///
    /// 三个键：
    /// - Ctrl+B 就近注入 30 个购物行程（目标=店面）——底线：强行注入能不能成交；
    /// - Ctrl+N 同 B 但目标=公司实体（购物寻路目标备选手）；
    /// - **Ctrl+M 改道（重点）**：把正在去别家买同种货的市民（真需求）改道到咱家店——
    ///   没货的不改（别无故毁人行程），成交看全店多资源库存跌幅。
    ///
    /// 判定：任一观测资源净减≥15=成交实锤；净减 5-14=有成交迹象（弱）；
    /// 峰值到店>0 但库存不动=打卡实锤；峰值到店=0=寻路/目标问题（换键）。
    /// spike 纪律：测试存档专用（注入/改道会拽走市民）；Ctrl+字母组合（F9 撞车教训）。
    /// </summary>
    public partial class ShoppingSpikeSystem : GameSystemBase
    {
        private const int k_InjectCount = 30;
        private const uint k_ObserveFrames = 16384;

        private EntityQuery m_CompanyQuery = default!;
        private EntityQuery m_CitizenQuery = default!;
        private EntityQuery m_ArrivalQuery = default!;
        private PrefabSystem m_PrefabSystem = default!;
        private SimulationSystem m_Simulation = default!;

        private Entity m_Shop;          // 目标店面（建筑实体）
        private Entity m_Target;        // 本轮注入/改道的寻路目标（Ctrl+B/M=店面，Ctrl+N=公司）
        private Entity m_Company;       // 目标公司
        private Resource m_Resource;    // 公司主营资源（存货最多的非货币资源）
        private int m_BaselineStock = -1; // <0 = 未在观测
        private int m_BaselineAny;      // 基线任意目的到店数（店面里常住/上班的人）
        private int m_PeakArrivals;     // 峰值到店数
        private readonly List<(Resource res, int baseline)> m_Watch = new(); // 观测资源清单（B/N=1 项，M=多资源）
        private uint m_StartFrame;
        private uint m_LastKeyFrame;
        private uint m_Frame;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CompanyQuery = GetEntityQuery(
                ComponentType.ReadOnly<CommercialCompany>(),
                ComponentType.ReadOnly<PropertyRenter>(),
                ComponentType.ReadOnly<Resources>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
            m_CitizenQuery = GetEntityQuery(
                ComponentType.ReadOnly<Citizen>(),
                ComponentType.ReadWrite<TripNeeded>(),
                ComponentType.Exclude<HealthProblem>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
            m_ArrivalQuery = GetEntityQuery(
                ComponentType.ReadOnly<Citizen>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            RequireForUpdate(m_CitizenQuery);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 1; // 仅为捕获键盘；正式系统一律降频

        private uint Now => (uint)m_Simulation.frameIndex;

        protected override void OnUpdate()
        {
            var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            var debounced = m_Frame - m_LastKeyFrame > 30;
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.B)) { m_LastKeyFrame = m_Frame; StartInject(targetCompany: false); }
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.N)) { m_LastKeyFrame = m_Frame; StartInject(targetCompany: true); }
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.M)) { m_LastKeyFrame = m_Frame; StartRetarget(); }

            if (m_BaselineStock >= 0)
            {
                if (m_Frame % 512 == 0)
                    Observe();
                var elapsed = Now - m_StartFrame;
                if (elapsed > k_ObserveFrames)
                    Finish("窗口到点");
                else if (m_PeakArrivals > 0 && elapsed > 1024 && CountTransit() == 0)
                    Finish("波次走完");
            }
            m_Frame++;
        }

        /// <summary>选店：第一家有店面、有存货的商业公司。成功设 m_Company/m_Shop/m_Resource，返回 true。</summary>
        private bool TryPickShop()
        {
            var companies = m_CompanyQuery.ToEntityArray(Allocator.Temp);
            if (companies.Length == 0)
            {
                Mod.Log.Warn("[ShopSpike] 城里没有商业公司，先放几家再按");
                companies.Dispose();
                return false;
            }
            m_Company = companies[0];
            companies.Dispose();

            m_Shop = EntityManager.GetComponentData<PropertyRenter>(m_Company).m_Property;
            if (m_Shop == Entity.Null || !EntityManager.HasComponent<Transform>(m_Shop))
            {
                Mod.Log.Warn("[ShopSpike] 公司没有店面，换一座城试");
                return false;
            }

            // 主营资源 = 存货最多的非货币资源（与锚点系统同一判定）
            m_Resource = Resource.NoResource;
            var best = -1;
            foreach (var r in EntityManager.GetBuffer<Resources>(m_Company))
            {
                if (r.m_Resource == Resource.Money || r.m_Resource == Resource.NoResource)
                    continue;
                if (r.m_Amount > best)
                {
                    best = r.m_Amount;
                    m_Resource = r.m_Resource;
                }
            }
            if (m_Resource == Resource.NoResource)
            {
                Mod.Log.Warn("[ShopSpike] 这家店没有存货，换一家（换座城/等公司补货）");
                return false;
            }
            return true;
        }

        // —— Ctrl+B/N：就近注入 30 个购物行程（底线：强行注入能不能成交）——
        private void StartInject(bool targetCompany)
        {
            if (!TryPickShop())
                return;
            m_Target = targetCompany ? m_Company : m_Shop;
            var shopPos = EntityManager.GetComponentData<Transform>(m_Shop).m_Position;

            // 就近取 30 人（按当前位置到店的距离²排序——治第一轮"全城徒步走不到"的无效实验）
            var citizens = m_CitizenQuery.ToEntityArray(Allocator.Temp);
            var scored = new List<(float d, Entity e)>(citizens.Length);
            foreach (var c in citizens)
            {
                if (!EntityManager.HasComponent<Transform>(c))
                    continue;
                scored.Add((math.distancesq(EntityManager.GetComponentData<Transform>(c).m_Position, shopPos), c));
            }
            citizens.Dispose();
            scored.Sort((a, b) => a.d.CompareTo(b.d));

            var injected = 0;
            foreach (var (_, citizen) in scored)
            {
                if (injected >= k_InjectCount)
                    break;
                EntityManager.GetBuffer<TripNeeded>(citizen).Add(new TripNeeded
                {
                    m_TargetAgent = m_Target,
                    m_Purpose = Purpose.Shopping,
                    m_Resource = m_Resource,
                    m_Priority = 128,
                });
                var target = new Target { m_Target = m_Target };
                if (EntityManager.HasComponent<Target>(citizen))
                    EntityManager.SetComponentData(citizen, target);
                else
                    EntityManager.AddComponentData(citizen, target);
                var purpose = new TravelPurpose { m_Purpose = Purpose.Shopping, m_Resource = m_Resource };
                if (EntityManager.HasComponent<TravelPurpose>(citizen))
                    EntityManager.SetComponentData(citizen, purpose);
                else
                    EntityManager.AddComponentData(citizen, purpose);
                EntityManager.RemoveComponent<Leisure>(citizen);
                EntityManager.RemoveComponent<PathInformation>(citizen);
                EntityManager.RemoveComponent<PathElement>(citizen);
                injected++;
            }

            m_Watch.Clear();
            m_Watch.Add((m_Resource, GetStockOf(m_Resource)));
            BeginObserve();
            var nearest = scored.Count > 0 ? math.sqrt(scored[0].d) : 0f;
            var farthest = injected > 0 ? math.sqrt(scored[injected - 1].d) : 0f;
            Mod.Log.Info($"[ShopSpike] 注入：{ShopName()} 主营={m_Resource}，就近 {injected} 人（{nearest:F0}-{farthest:F0}m），"
                         + $"目标={(targetCompany ? "公司" : "店面")}，基线 {WatchText()}");
        }

        // —— Ctrl+M 改道（重点）：把正在去别家买同种货的市民（真需求）改道到咱家店 ——
        private void StartRetarget()
        {
            if (!TryPickShop())
                return;
            m_Target = m_Shop;

            // 快照全店库存（多资源——改道市民买什么跌什么，观测面铺全）
            m_Watch.Clear();
            foreach (var r in EntityManager.GetBuffer<Resources>(m_Company))
            {
                if (r.m_Resource == Resource.Money || r.m_Resource == Resource.NoResource || r.m_Amount <= 0)
                    continue;
                m_Watch.Add((r.m_Resource, r.m_Amount));
                if (m_Watch.Count >= 6)
                    break;
            }

            int retargeted = 0, skipped = 0;
            var arr = m_ArrivalQuery.ToEntityArray(Allocator.Temp);
            foreach (var citizen in arr)
            {
                if (!EntityManager.HasComponent<TravelPurpose>(citizen))
                    continue;
                var tp = EntityManager.GetComponentData<TravelPurpose>(citizen);
                if (tp.m_Purpose != Purpose.Shopping)
                    continue;
                if (EntityManager.HasComponent<Target>(citizen)
                    && EntityManager.GetComponentData<Target>(citizen).m_Target == m_Shop)
                    continue; // 本来就是来咱家的
                // 只改道"咱家店有他要的货"的——没货改过去也是白跑（侵入度纪律：别无故毁人行程）
                if (GetStockOf(tp.m_Resource) <= 0)
                {
                    skipped++;
                    continue;
                }

                // 改道三件套：TripNeeded 里的购物条目换目标 / Target 换目标 / 撕路径重寻
                var trips = EntityManager.GetBuffer<TripNeeded>(citizen);
                for (int i = 0; i < trips.Length; i++)
                {
                    if (trips[i].m_Purpose != Purpose.Shopping)
                        continue;
                    var t = trips[i];
                    t.m_TargetAgent = m_Shop;
                    trips[i] = t;
                }
                var target = new Target { m_Target = m_Shop };
                if (EntityManager.HasComponent<Target>(citizen))
                    EntityManager.SetComponentData(citizen, target);
                else
                    EntityManager.AddComponentData(citizen, target);
                EntityManager.RemoveComponent<PathInformation>(citizen);
                EntityManager.RemoveComponent<PathElement>(citizen);
                retargeted++;
            }
            arr.Dispose();

            BeginObserve();
            Mod.Log.Info($"[ShopSpike] 改道：{retargeted} 个真购物市民引向 {ShopName()}（跳过 {skipped} 人无货），基线 {WatchText()}");
        }

        private void BeginObserve()
        {
            m_BaselineStock = GetStockOf(m_Resource);
            m_BaselineAny = CountArrivalsAny();
            m_PeakArrivals = 0;
            m_StartFrame = Now;
        }

        private string ShopName()
        {
            if (EntityManager.HasComponent<PrefabRef>(m_Shop)
                && m_PrefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(m_Shop).m_Prefab, out PrefabBase prefab))
                return prefab.name;
            return "?";
        }

        private int GetStockOf(Resource res)
        {
            foreach (var r in EntityManager.GetBuffer<Resources>(m_Company))
                if (r.m_Resource == res)
                    return r.m_Amount;
            return 0;
        }

        /// <summary>观测资源快照："Food=4494(-12) Beverages=2001(+0)"（基线差分）。</summary>
        private string WatchText()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (res, baseline) in m_Watch)
            {
                if (sb.Length > 0) sb.Append(' ');
                var d = GetStockOf(res) - baseline;
                sb.Append(res).Append('=').Append(GetStockOf(res))
                  .Append('(').Append(d >= 0 ? "+" : "").Append(d).Append(')');
            }
            return sb.ToString();
        }

        /// <summary>任意目的到店数（含住/工作在该店的人——用基线差分去噪）。</summary>
        private int CountArrivalsAny()
        {
            var n = 0;
            var arr = m_ArrivalQuery.ToEntityArray(Allocator.Temp);
            foreach (var citizen in arr)
                if (EntityManager.HasComponent<CurrentBuilding>(citizen)
                    && EntityManager.GetComponentData<CurrentBuilding>(citizen).m_CurrentBuilding == m_Shop)
                    n++;
            arr.Dispose();
            return n;
        }

        /// <summary>在途数（Shopping 目的且 Target=本轮目标）。</summary>
        private int CountTransit()
        {
            var n = 0;
            var arr = m_ArrivalQuery.ToEntityArray(Allocator.Temp);
            foreach (var citizen in arr)
            {
                if (!EntityManager.HasComponent<TravelPurpose>(citizen))
                    continue;
                var tp = EntityManager.GetComponentData<TravelPurpose>(citizen);
                if (tp.m_Purpose == Purpose.Shopping
                    && EntityManager.HasComponent<Target>(citizen)
                    && EntityManager.GetComponentData<Target>(citizen).m_Target == m_Target)
                    n++;
            }
            arr.Dispose();
            return n;
        }

        private void Observe()
        {
            var anyNow = CountArrivalsAny();
            var shopping = 0;
            var arr = m_ArrivalQuery.ToEntityArray(Allocator.Temp);
            foreach (var citizen in arr)
            {
                if (EntityManager.HasComponent<TravelPurpose>(citizen)
                    && EntityManager.GetComponentData<TravelPurpose>(citizen).m_Purpose == Purpose.Shopping
                    && EntityManager.HasComponent<CurrentBuilding>(citizen)
                    && EntityManager.GetComponentData<CurrentBuilding>(citizen).m_CurrentBuilding == m_Shop)
                    shopping++;
            }
            arr.Dispose();
            m_PeakArrivals = Math.Max(m_PeakArrivals, shopping + Math.Max(0, anyNow - m_BaselineAny));
            Mod.Log.Info($"[ShopSpike] t+{Now - m_StartFrame} {WatchText()} 到店AnyΔ{anyNow - m_BaselineAny} 在途 {CountTransit()}");
        }

        private void Finish(string why)
        {
            Observe();
            // 最差资源跌幅（多资源取最狠的一项当判据）
            var worstDelta = 0;
            Resource worstRes = Resource.NoResource;
            foreach (var (res, baseline) in m_Watch)
            {
                var d = GetStockOf(res) - baseline;
                if (d < worstDelta)
                {
                    worstDelta = d;
                    worstRes = res;
                }
            }
            string verdict;
            if (worstDelta <= -15)
                verdict = $"成交实锤——{worstRes} 净减 {-worstDelta}，购买真实发生，广告层按实质性建";
            else if (worstDelta <= -5)
                verdict = $"有成交迹象（弱）——{worstRes} 净减 {-worstDelta}，加按几次/放大规模再确认";
            else if (m_PeakArrivals > 0)
                verdict = $"打卡实锤——峰值到店 {m_PeakArrivals} 但最差跌幅 {worstDelta}，广告层降级纯舆情";
            else
                verdict = "没人到店——寻路/目标问题，换另一个键的变体（店面↔公司）再试";
            Mod.Log.Info($"[ShopSpike][结论·{why}] {verdict}");
            m_BaselineStock = -1;
        }
    }
}
