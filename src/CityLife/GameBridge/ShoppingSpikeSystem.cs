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
    /// 实质性 spike（2026-08-20）：购物注入是否真成交？（验证完成后拆除）
    ///
    /// 问题：Leisure 注入的人群是"打卡"——到店不发生购买，商店打折聚众没有实质性改善（玩家推断）。
    /// 假设：TripNeeded{ Purpose.Shopping, m_Resource=店的主营资源 } 能触发游戏原生购买
    /// （ResourceBuyerSystem 存在 m_SalesQueue/FailedShopping 追踪——购买结算链路 dump 实锤）。
    ///
    /// 第一轮教训（实机日志 2026-08-20 17:31）：全城随机注入 30 人 + 4096 帧窗口——
    /// 在途恒 89、到店恒 0，没人走到就出了"结论"，**窗口太短+目标太远，实验无效**。
    /// 第二轮修正：
    /// - **就近注入**：按市民当前位置取最近 30 人（顺带验证广告层要的"距离衰减"机制）；
    /// - 窗口放宽到 16384 帧、每 512 帧打一行；峰值到店数与任意目的到店数都记
    ///   （到店瞬间目的可能翻转——按 Shopping 数到店会漏，Any 口径兜底）；
    /// - Ctrl+B 目标=店面建筑；Ctrl+N 目标=公司实体（备选手：若购物寻路要挂公司）。
    ///
    /// 判定：
    /// - 库存净减≥15 → 成交实锤，广告层按"真实购买"建；
    /// - 峰值到店>0 但库存不动 → 打卡实锤，广告层降级纯舆情（照实记录）；
    /// - 峰值到店=0 → 寻路/目标问题（换 Ctrl+N 变体再试）。
    /// spike 纪律：测试存档专用（注入会拽走市民）；Ctrl+字母组合（F9 撞车教训）。
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
        private Entity m_Target;        // 本轮注入的寻路目标（Ctrl+B=店面，Ctrl+N=公司）
        private Entity m_Company;       // 目标公司
        private Resource m_Resource;    // 公司主营资源（存货最多的非货币资源）
        private int m_BaselineStock = -1; // <0 = 未在观测
        private int m_BaselineAny;      // 基线任意目的到店数（店面里常住/上班的人）
        private int m_PeakArrivals;     // 峰值到店（Shopping 目的口径）
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
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.B)) { m_LastKeyFrame = m_Frame; StartSpike(targetCompany: false); }
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.N)) { m_LastKeyFrame = m_Frame; StartSpike(targetCompany: true); }

            if (m_BaselineStock >= 0)
            {
                if (m_Frame % 512 == 0)
                    Observe();
                // 收尾：窗口到点，或波次走完（到店峰值出现过且在途/到店都归零）
                var elapsed = Now - m_StartFrame;
                if (elapsed > k_ObserveFrames)
                    Finish("窗口到点");
                else if (m_PeakArrivals > 0 && elapsed > 1024 && CountTransit() == 0)
                    Finish("波次走完");
            }
            m_Frame++;
        }

        private void StartSpike(bool targetCompany)
        {
            var companies = m_CompanyQuery.ToEntityArray(Allocator.Temp);
            if (companies.Length == 0)
            {
                Mod.Log.Warn("[ShopSpike] 城里没有商业公司，先放几家再按");
                companies.Dispose();
                return;
            }
            m_Company = companies[0];
            companies.Dispose();

            m_Shop = EntityManager.GetComponentData<PropertyRenter>(m_Company).m_Property;
            if (m_Shop == Entity.Null || !EntityManager.HasComponent<Transform>(m_Shop))
            {
                Mod.Log.Warn("[ShopSpike] 公司没有店面，换一座城试");
                return;
            }
            m_Target = targetCompany ? m_Company : m_Shop;
            var shopPos = EntityManager.GetComponentData<Transform>(m_Shop).m_Position;

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
                return;
            }

            // 就近取 30 人（按当前位置到店的距离²排序——治第一轮"全城徒步走不到"的无效实验）
            var citizens = m_CitizenQuery.ToEntityArray(Allocator.Temp);
            var scored = new List<(float d, Entity e)>(citizens.Length);
            foreach (var c in citizens)
            {
                if (!EntityManager.HasComponent<Transform>(c))
                    continue;
                var p = EntityManager.GetComponentData<Transform>(c).m_Position;
                scored.Add((math.distancesq(p, shopPos), c));
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

            m_BaselineStock = GetStock();
            m_BaselineAny = CountArrivalsAny();
            m_PeakArrivals = 0;
            m_StartFrame = Now;

            var shopName = "?";
            if (EntityManager.HasComponent<PrefabRef>(m_Shop)
                && m_PrefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(m_Shop).m_Prefab, out PrefabBase prefab))
                shopName = prefab.name;
            var nearest = scored.Count > 0 ? math.sqrt(scored[0].d) : 0f;
            var farthest = injected > 0 ? math.sqrt(scored[injected - 1].d) : 0f;
            Mod.Log.Info($"[ShopSpike] 开测：{shopName} 主营={m_Resource} 基线库存={m_BaselineStock} 基线到店Any={m_BaselineAny}，"
                         + $"就近注入 {injected} 人（距离 {nearest:F0}-{farthest:F0}m），目标={(targetCompany ? "公司" : "店面")}");
        }

        private int GetStock()
        {
            foreach (var r in EntityManager.GetBuffer<Resources>(m_Company))
                if (r.m_Resource == m_Resource)
                    return r.m_Amount;
            return 0;
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
            var stock = GetStock();
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
            Mod.Log.Info($"[ShopSpike] t+{Now - m_StartFrame} 库存 {m_BaselineStock}→{stock}（Δ{stock - m_BaselineStock}）"
                         + $" 到店AnyΔ{anyNow - m_BaselineAny} 在途 {CountTransit()}");
        }

        private void Finish(string why)
        {
            Observe();
            var delta = GetStock() - m_BaselineStock;
            string verdict;
            if (delta <= -k_InjectCount / 2)
                verdict = $"成交实锤——库存净减 {-delta}，购物注入走真实购买，商家广告层按实质性建";
            else if (m_PeakArrivals > 0)
                verdict = $"打卡实锤——峰值到店 {m_PeakArrivals} 但库存变化 {delta}（未达净减 {k_InjectCount / 2}），广告层降级纯舆情";
            else
                verdict = "没人到店——寻路/目标问题，换另一个键的变体（店面↔公司）再试";
            Mod.Log.Info($"[ShopSpike][结论·{why}] {verdict}");
            m_BaselineStock = -1;
        }
    }
}
