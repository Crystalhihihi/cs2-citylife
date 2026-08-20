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
    /// 实质性 spike R3（2026-08-20）：购物注入/改道是否真成交？（验证完成后拆除）
    ///
    /// 背景（玩家追问）：原版购物是**需求驱动**——家庭缺货 → 行为系统派人买
    /// （TripNeeded{Purpose.Shopping, m_Resource=缺的货}）→ ResourceBuyerSystem 结算
    /// （m_SalesQueue 销售事件队列实锤）。模拟里没有"广告"概念。
    ///
    /// 前两轮教训：
    /// - R1（17:31）：窗口太短没人走到，实验无效；
    /// - R2（18:25）：**自动判定误报"成交实锤"**——库存 -15 其实是 vanilla 背景消耗
    ///   （营业中的店正常有顾客），无对照组分不清；且 在途 30 恒定=注入的购物行程被
    ///   市民 AI 无视（无真需求不执行，手写 TravelPurpose 成僵尸标记），到店AnyΔ≈0。
    ///
    /// R3 决定性设计（全部指标抗噪声）：
    /// - **逐人追踪**：被注入/改道的市民进 HashSet，逐帧看 CurrentBuilding——
    ///   "N 人动了，M 人真的进店"是最硬的行程执行证据（瞬时到店也不漏）；
    /// - **对照店**：第二家商业公司的主营资源做背景参照，(本店Δ−对照Δ) 才是真实效应，
    ///   vanilla 背景两家均担自动抵消；
    /// - 判定三分支：到店率≥1/4 且差分≤-10=成交实锤；到店率≥1/4 但差分≈0=人到店
    ///   但购买结算没跟来（购买目标记在别处）；到店率<1/4=行程没执行（此路不通）。
    ///
    /// 三个键：Ctrl+B 就近注入（店面）/ Ctrl+N 就近注入（公司）/ Ctrl+M 改道真需求（重点）。
    /// spike 纪律：测试存档专用；Ctrl+字母组合（F9 撞车教训）。
    /// </summary>
    public partial class ShoppingSpikeSystem : GameSystemBase
    {
        private const int k_InjectCount = 30;
        private const uint k_ObserveFrames = 32768; // R3 教训：跨城改道 16384 帧走不完（窗口末还有 37 在途）

        private EntityQuery m_CompanyQuery = default!;
        private EntityQuery m_CitizenQuery = default!;
        private EntityQuery m_ArrivalQuery = default!;
        private PrefabSystem m_PrefabSystem = default!;
        private SimulationSystem m_Simulation = default!;

        private Entity m_Shop;            // 目标店面（建筑实体）
        private Entity m_Target;          // 本轮注入/改道的寻路目标（Ctrl+B/M=店面，Ctrl+N=公司）
        private Entity m_Company;         // 目标公司
        private Entity m_ControlCompany;  // 对照公司（vanilla 背景参照，Null=没有第二家）
        private Resource m_Resource;      // 目标公司主营资源
        private Resource m_ControlResource; // 对照公司主营资源
        private int m_ControlBaseline;
        private int m_BaselineStock = -1; // <0 = 未在观测
        private readonly HashSet<Entity> m_Injected = new();  // 本轮动了的人
        private readonly HashSet<Entity> m_Arrived = new();   // 其中真进了店的（逐帧追踪）
        private readonly List<(Resource res, int baseline)> m_Watch = new();
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

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 1; // 仅为捕获键盘+逐帧追踪；正式系统一律降频

        private uint Now => (uint)m_Simulation.frameIndex;

        protected override void OnUpdate()
        {
            var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            var debounced = m_Frame - m_LastKeyFrame > 30;
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.B)) { m_LastKeyFrame = m_Frame; StartInject(targetCompany: false, needBased: false); }
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.N)) { m_LastKeyFrame = m_Frame; StartInject(targetCompany: true, needBased: false); }
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.J)) { m_LastKeyFrame = m_Frame; StartInject(targetCompany: false, needBased: true); }
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.M)) { m_LastKeyFrame = m_Frame; StartRetarget(); }

            if (m_BaselineStock >= 0)
            {
                TrackArrivals();
                if (m_Frame % 512 == 0)
                    Observe();
                var elapsed = Now - m_StartFrame;
                if (elapsed > k_ObserveFrames)
                    Finish("窗口到点");
            }
            m_Frame++;
        }

        /// <summary>逐帧追踪：被动过的市民，谁 CurrentBuilding==本店就记名（瞬时到店也不漏）。</summary>
        private void TrackArrivals()
        {
            foreach (var citizen in m_Injected)
            {
                if (m_Arrived.Contains(citizen))
                    continue;
                if (EntityManager.HasComponent<CurrentBuilding>(citizen)
                    && EntityManager.GetComponentData<CurrentBuilding>(citizen).m_CurrentBuilding == m_Shop)
                    m_Arrived.Add(citizen);
            }
        }

        /// <summary>选店：目标=第一家有店面有存货的商业公司；对照=第二家（没有则 Null）。</summary>
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
            m_ControlCompany = companies.Length > 1 ? companies[1] : Entity.Null;
            companies.Dispose();

            m_Shop = EntityManager.GetComponentData<PropertyRenter>(m_Company).m_Property;
            if (m_Shop == Entity.Null || !EntityManager.HasComponent<Transform>(m_Shop))
            {
                Mod.Log.Warn("[ShopSpike] 公司没有店面，换一座城试");
                return false;
            }
            if (!TryGetTopResource(m_Company, out m_Resource))
            {
                Mod.Log.Warn("[ShopSpike] 这家店没有存货，换一家（换座城/等公司补货）");
                return false;
            }
            m_ControlResource = Resource.NoResource;
            if (m_ControlCompany != Entity.Null)
                TryGetTopResource(m_ControlCompany, out m_ControlResource);
            return true;
        }

        /// <summary>主营资源 = 店的商品。产出声明优先（ShopOutput，R3.1 实锤路径）；拿不到回退存货猜测（杂货店类无加工数据的场景）。</summary>
        private bool TryGetTopResource(Entity company, out Resource top)
        {
            top = ShopOutput.OutputOf(EntityManager, company);
            if (top != Resource.NoResource)
                return true;
            var best = -1;
            foreach (var r in EntityManager.GetBuffer<Resources>(company))
            {
                if (r.m_Resource == Resource.Money || r.m_Resource == Resource.NoResource)
                    continue;
                if (r.m_Amount > best)
                {
                    best = r.m_Amount;
                    top = r.m_Resource;
                }
            }
            return top != Resource.NoResource;
        }

        // —— Ctrl+B/N/J：就近注入 30 个购物行程（B=店面/N=公司；J=缺口注入——只挑家里缺这个货的市民）——
        private void StartInject(bool targetCompany, bool needBased)
        {
            if (!TryPickShop())
                return;
            m_Target = targetCompany ? m_Company : m_Shop;
            var shopPos = EntityManager.GetComponentData<Transform>(m_Shop).m_Position;

            // 就近取 30 人（位置回退链：本人 Transform → 所在建筑——市民在建筑里/车上时位置不挂本人）
            var citizens = m_CitizenQuery.ToEntityArray(Allocator.Temp);
            var scored = new List<(float d, Entity e)>(citizens.Length);
            foreach (var c in citizens)
            {
                if (!TryGetPos(c, out var p))
                    continue;
                scored.Add((math.distancesq(p, shopPos), c));
            }
            citizens.Dispose();
            scored.Sort((a, b) => a.d.CompareTo(b.d));

            BeginWave();
            var injected = 0;
            foreach (var (_, citizen) in scored)
            {
                if (injected >= k_InjectCount)
                    break;
                // 缺口注入（Ctrl+J）：只挑家里这个货见底的市民——vanilla 需求驱动同构形态，
                // AI 不会当僵尸行程冲掉（R3：无需求注入被无视、改道被周期重规划冲掉 86/141）
                if (needBased && HouseholdStock(citizen, m_Resource) > 20)
                    continue;
                EntityManager.GetBuffer<TripNeeded>(citizen).Add(new TripNeeded
                {
                    m_TargetAgent = m_Target,
                    m_Purpose = Purpose.Shopping,
                    m_Resource = m_Resource,
                    m_Priority = 128,
                });
                SetTargetAndRepath(citizen, m_Target);
                var purpose = new TravelPurpose { m_Purpose = Purpose.Shopping, m_Resource = m_Resource };
                if (EntityManager.HasComponent<TravelPurpose>(citizen))
                    EntityManager.SetComponentData(citizen, purpose);
                else
                    EntityManager.AddComponentData(citizen, purpose);
                m_Injected.Add(citizen);
                injected++;
            }

            m_Watch.Add((m_Resource, GetStockOf(m_Company, m_Resource)));
            var nearest = scored.Count > 0 ? math.sqrt(scored[0].d) : 0f;
            var farthest = injected > 0 ? math.sqrt(scored[injected - 1].d) : 0f;
            Mod.Log.Info($"[ShopSpike] {(needBased ? "缺口注入" : "注入")}：{ShopName()} 主营={m_Resource}，就近 {injected} 人（{nearest:F0}-{farthest:F0}m），"
                         + $"目标={(targetCompany ? "公司" : "店面")}，{ControlText()}");
        }

        /// <summary>市民家庭某资源库存（缺口注入的筛选依据）：无家庭/无条目=0（缺）。</summary>
        private int HouseholdStock(Entity citizen, Resource res)
        {
            if (!EntityManager.HasComponent<HouseholdMember>(citizen))
                return int.MaxValue; // 无家庭（游客等）不当缺口户
            var hh = EntityManager.GetComponentData<HouseholdMember>(citizen).m_Household;
            foreach (var r in EntityManager.GetBuffer<Resources>(hh))
                if (r.m_Resource == res)
                    return r.m_Amount;
            return 0; // 没有该资源条目 = 库存 0（缺）
        }

        // —— Ctrl+M 改道（重点）：把正在去别家买同种货的市民（真需求）改道到咱家店 ——
        private void StartRetarget()
        {
            if (!TryPickShop())
                return;
            m_Target = m_Shop;
            BeginWave(); // 先清观测（R3 首跑踩坑：先填快照后 BeginWave，库存观测被清空，成交无法判定）

            // 快照全店库存（多资源——改道市民买什么跌什么，观测面铺全）
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
                if (GetStockOf(m_Company, tp.m_Resource) <= 0)
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
                SetTargetAndRepath(citizen, m_Shop);
                m_Injected.Add(citizen);
                retargeted++;
            }
            arr.Dispose();

            Mod.Log.Info($"[ShopSpike] 改道：{retargeted} 个真购物市民引向 {ShopName()}（跳过 {skipped} 人无货），{ControlText()}");
        }

        /// <summary>改 Target + 撕路径重寻（注入/改道共用）。</summary>
        private void SetTargetAndRepath(Entity citizen, Entity targetEntity)
        {
            var target = new Target { m_Target = targetEntity };
            if (EntityManager.HasComponent<Target>(citizen))
                EntityManager.SetComponentData(citizen, target);
            else
                EntityManager.AddComponentData(citizen, target);
            EntityManager.RemoveComponent<Leisure>(citizen);
            EntityManager.RemoveComponent<PathInformation>(citizen);
            EntityManager.RemoveComponent<PathElement>(citizen);
        }

        private void BeginWave()
        {
            m_Injected.Clear();
            m_Arrived.Clear();
            m_Watch.Clear();
            m_BaselineStock = GetStockOf(m_Company, m_Resource);
            m_ControlBaseline = m_ControlCompany != Entity.Null && m_ControlResource != Resource.NoResource
                ? GetStockOf(m_ControlCompany, m_ControlResource)
                : -1;
            m_StartFrame = Now;
        }

        private string ShopName()
        {
            if (EntityManager.HasComponent<PrefabRef>(m_Shop)
                && m_PrefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(m_Shop).m_Prefab, out PrefabBase prefab))
                return prefab.name;
            return "?";
        }

        private string ControlText()
            => m_ControlBaseline >= 0
                ? $"对照店 {m_ControlResource}={m_ControlBaseline}"
                : "无对照店（只有一家商业公司）";

        private bool TryGetPos(Entity citizen, out float3 pos)
        {
            if (EntityManager.HasComponent<Transform>(citizen))
            {
                pos = EntityManager.GetComponentData<Transform>(citizen).m_Position;
                return true;
            }
            if (EntityManager.HasComponent<CurrentBuilding>(citizen))
            {
                var b = EntityManager.GetComponentData<CurrentBuilding>(citizen).m_CurrentBuilding;
                if (b != Entity.Null && EntityManager.HasComponent<Transform>(b))
                {
                    pos = EntityManager.GetComponentData<Transform>(b).m_Position;
                    return true;
                }
            }
            pos = default;
            return false;
        }

        private int GetStockOf(Entity company, Resource res)
        {
            foreach (var r in EntityManager.GetBuffer<Resources>(company))
                if (r.m_Resource == res)
                    return r.m_Amount;
            return 0;
        }

        /// <summary>在途数（Shopping 目的且 Target=本轮目标；注意：被 AI 无视的行程会留下僵尸标记，仅供参照）。</summary>
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
            var sb = new System.Text.StringBuilder();
            foreach (var (res, baseline) in m_Watch)
            {
                if (sb.Length > 0) sb.Append(' ');
                var d = GetStockOf(m_Company, res) - baseline;
                sb.Append(res).Append('=').Append(GetStockOf(m_Company, res))
                  .Append('(').Append(d >= 0 ? "+" : "").Append(d).Append(')');
            }
            var controlDeltaText = m_ControlBaseline >= 0
                ? $" 对照Δ{GetStockOf(m_ControlCompany, m_ControlResource) - m_ControlBaseline}"
                : "";
            Mod.Log.Info($"[ShopSpike] t+{Now - m_StartFrame} {sb}{controlDeltaText} 到店 {m_Arrived.Count}/{m_Injected.Count} 在途 {CountTransit()}");
        }

        private void Finish(string why)
        {
            Observe();
            // 本店最差资源跌幅
            var worstDelta = 0;
            Resource worstRes = Resource.NoResource;
            foreach (var (res, baseline) in m_Watch)
            {
                var d = GetStockOf(m_Company, res) - baseline;
                if (d < worstDelta)
                {
                    worstDelta = d;
                    worstRes = res;
                }
            }
            // 差分 = 本店跌幅 − 对照跌幅（vanilla 背景抵消后的真实效应；无对照时退化为本店跌幅）
            var controlDelta = m_ControlBaseline >= 0
                ? GetStockOf(m_ControlCompany, m_ControlResource) - m_ControlBaseline
                : 0;
            var effect = worstDelta - controlDelta;
            var arrivalRate = m_Injected.Count > 0 ? m_Arrived.Count / (float)m_Injected.Count : 0f;

            string verdict;
            if (arrivalRate >= 0.25f && effect <= -10)
                verdict = $"成交实锤——到店 {m_Arrived.Count}/{m_Injected.Count}，{worstRes} 差分 {effect}（本店 {worstDelta} 对照 {controlDelta}），广告层按实质性建";
            else if (arrivalRate >= 0.25f)
                verdict = $"人到店但购买结算没跟来——到店 {m_Arrived.Count}/{m_Injected.Count} 但差分 {effect}≈0：购买目标记在别处（m_Data/需求链路），光改 Target 不够";
            else
                verdict = $"行程没执行——到店仅 {m_Arrived.Count}/{m_Injected.Count}：此路不通（注入被 AI 无视/改道无效）";
            Mod.Log.Info($"[ShopSpike][结论·{why}] {verdict}");
            m_BaselineStock = -1;
        }
    }
}
