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

namespace CityLife.GameBridge
{
    /// <summary>
    /// 实质性 spike（2026-08-20）：购物注入是否真成交？（验证完成后拆除）
    ///
    /// 问题：Leisure 注入的人群是"打卡"——到店不发生购买，商店打折聚众没有实质性改善（玩家推断）。
    /// 假设：TripNeeded{ Purpose.Shopping, m_Resource=店的主营资源 } 能触发游戏原生购买
    /// （ResourceBuyerSystem 存在 m_SalesQueue/FailedShopping 追踪——购买结算链路 dump 实锤）。
    ///
    /// 判定（按 Ctrl+B 向第一家商业公司注入 30 个购物行程）：
    /// - 成交实锤：到店人数上升时该资源库存同步明显下降 → 商家广告层按"真实购买"建；
    /// - 库存不动：打卡实锤 → 广告层降级纯舆情，照实记录不包装。
    ///
    /// 观测：注入时打库存基线，之后每 128 帧打库存+到店/在途数，4096 帧收尾出结论行。
    /// spike 纪律：测试存档专用（注入会拽走市民）；Ctrl+字母组合（F9 撞车教训）。
    /// </summary>
    public partial class ShoppingSpikeSystem : GameSystemBase
    {
        private const int k_InjectCount = 30;
        private const uint k_ObserveFrames = 4096;

        private EntityQuery m_CompanyQuery = default!;
        private EntityQuery m_CitizenQuery = default!;
        private EntityQuery m_ArrivalQuery = default!;
        private PrefabSystem m_PrefabSystem = default!;
        private SimulationSystem m_Simulation = default!;

        private Entity m_Shop;          // 目标店面（建筑实体）
        private Entity m_Company;       // 目标公司
        private Resource m_Resource;    // 公司主营资源（存货最多的非货币资源）
        private int m_BaselineStock = -1; // <0 = 未在观测
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
            if (ctrl && Input.GetKeyDown(KeyCode.B) && m_Frame - m_LastKeyFrame > 30)
            {
                m_LastKeyFrame = m_Frame;
                StartSpike();
            }

            if (m_BaselineStock >= 0)
            {
                if (m_Frame % 128 == 0)
                    Observe(final: false);
                if (Now - m_StartFrame > k_ObserveFrames)
                {
                    Observe(final: true);
                    m_BaselineStock = -1;
                }
            }
            m_Frame++;
        }

        private void StartSpike()
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
            if (m_Shop == Entity.Null || !EntityManager.HasComponent<Game.Objects.Transform>(m_Shop))
            {
                Mod.Log.Warn("[ShopSpike] 公司没有店面，换一座城试");
                return;
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
                return;
            }

            m_BaselineStock = GetStock();
            m_StartFrame = Now;

            // 注入 30 个购物行程：Purpose.Shopping + m_Resource=主营资源 + 配套三件套（同 Leisure 注入纪律）
            var citizens = m_CitizenQuery.ToEntityArray(Allocator.Temp);
            var injected = 0;
            foreach (var citizen in citizens)
            {
                if (injected >= k_InjectCount)
                    break;
                EntityManager.GetBuffer<TripNeeded>(citizen).Add(new TripNeeded
                {
                    m_TargetAgent = m_Shop,
                    m_Purpose = Purpose.Shopping,
                    m_Resource = m_Resource,
                    m_Priority = 128,
                });
                var target = new Target { m_Target = m_Shop };
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
            citizens.Dispose();

            var shopName = "?";
            if (EntityManager.HasComponent<PrefabRef>(m_Shop)
                && m_PrefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(m_Shop).m_Prefab, out PrefabBase prefab))
                shopName = prefab.name;
            Mod.Log.Info($"[ShopSpike] 开测：{shopName} 主营={m_Resource} 基线库存={m_BaselineStock}，注入 {injected} 人");
        }

        private int GetStock()
        {
            foreach (var r in EntityManager.GetBuffer<Resources>(m_Company))
                if (r.m_Resource == m_Resource)
                    return r.m_Amount;
            return 0;
        }

        private void Observe(bool final)
        {
            var stock = GetStock();
            int arrivals = 0, onTheWay = 0;
            var arr = m_ArrivalQuery.ToEntityArray(Allocator.Temp);
            foreach (var citizen in arr)
            {
                if (!EntityManager.HasComponent<TravelPurpose>(citizen))
                    continue;
                var tp = EntityManager.GetComponentData<TravelPurpose>(citizen);
                if (tp.m_Purpose != Purpose.Shopping)
                    continue;
                if (EntityManager.HasComponent<CurrentBuilding>(citizen)
                    && EntityManager.GetComponentData<CurrentBuilding>(citizen).m_CurrentBuilding == m_Shop)
                    arrivals++;
                else if (EntityManager.HasComponent<Target>(citizen)
                         && EntityManager.GetComponentData<Target>(citizen).m_Target == m_Shop)
                    onTheWay++;
            }
            arr.Dispose();
            Mod.Log.Info($"[ShopSpike] t+{Now - m_StartFrame} 库存 {m_BaselineStock}→{stock}（Δ{stock - m_BaselineStock}）到店 {arrivals} 在途 {onTheWay}");

            if (final)
            {
                var delta = stock - m_BaselineStock;
                Mod.Log.Info(delta <= -k_InjectCount / 2
                    ? $"[ShopSpike][结论] 库存净减 {-delta}：成交实锤——购物注入走真实购买，商家广告层按实质性建"
                    : $"[ShopSpike][结论] 库存变化 {delta}：未达成交判据（需净减≥{k_InjectCount / 2}）——打卡实锤，广告层降级纯舆情");
            }
        }
    }
}
