using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CityLife.GameBridge
{
    /// <summary>锚点类型（实体话题第二刀的第一批信号 + 城市变化感知器的变化信号，全部来自已验证组件）。</summary>
    public enum AnchorKind { Hiring, BusinessGood, BusinessBad, Park, NewShop, NewService, NewPark, NewSignature, Demolished }

    /// <summary>
    /// 实体锚点：一条"具体到对象"的话题线索。Label=中文方位+真实业态名（"城东那家便利店"），
    /// Detail=一句可入 prompt 的线索（"空 3 个岗"）。Entity 供信息流挂"点击聚焦"用。
    /// </summary>
    public readonly struct Anchor
    {
        public readonly Entity Entity;
        public readonly AnchorKind Kind;
        public readonly string Label;
        public readonly string Detail;

        public Anchor(Entity entity, AnchorKind kind, string label, string detail)
        {
            Entity = entity;
            Kind = kind;
            Label = label;
            Detail = detail;
        }

        public string PromptText => $"{Label}（{Detail}）";
    }

    /// <summary>
    /// 实体锚点系统（读侧）：每 1024 帧从公司/公园实体采样一批话题锚点，供内容导演分配。
    /// 信号全部来自元数据已验证组件：空缺=WorkProvider.m_MaxWorkers−Employee buffer 数；
    /// 盈亏=Profitability.m_Profitability；业态=Resources buffer 里存货最多的非货币资源。
    /// 标签=**真实店名/地名**（NameSystem.GetRenderedLabelName + 所在路名，2026-08-20 玩家定案：
    /// 人发帖会说"解放路那家面馆"，不会说"城西北"——方位只作无名时的兜底）。
    /// 纪律：跨步抽样防总抓同一批；采样整体轮换（m_Offset）；只读不写。
    /// </summary>
    public partial class EntityAnchorSystem : GameSystemBase
    {
        // 上限随"全面"反馈上调（2026-08-20）：锚点是 LLM 唯一的城市具体名词来源，池子太浅话题就干瘪
        private const int k_MaxCompanyAnchors = 12;
        private const int k_MaxParkAnchors = 6;

        private EntityQuery m_CitizenQuery = default!;
        private EntityQuery m_CompanyQuery = default!;
        private EntityQuery m_ParkQuery = default!;
        private Game.UI.NameSystem? m_NameSystem;  // 惰性解析（真实店名/路名；拿不到回退方位兜底）
        private readonly List<Anchor> m_Anchors = new();
        private int m_Offset;
        private uint m_Cycle;

        /// <summary>当前可用锚点（主线程只读）。</summary>
        public IReadOnlyList<Anchor> Anchors => m_Anchors;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CitizenQuery = GetEntityQuery(ComponentType.ReadOnly<Citizen>());
            m_CompanyQuery = GetEntityQuery(
                ComponentType.ReadOnly<CommercialCompany>(),
                ComponentType.ReadOnly<WorkProvider>(),
                ComponentType.ReadOnly<Profitability>(),
                ComponentType.ReadOnly<PropertyRenter>(),
                ComponentType.ReadOnly<Employee>(),
                ComponentType.ReadOnly<Resources>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_ParkQuery = GetEntityQuery(
                ComponentType.ReadOnly<AttractivenessProvider>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            RequireForUpdate(m_CitizenQuery);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 1024;

        protected override void OnUpdate()
        {
            m_Anchors.Clear();
            m_Cycle++;
            bool calibrate = m_Cycle % 16 == 1; // 校准日志：盈利分布/业态分布，低频

            // —— 公司锚点（招聘/盈亏），标签带真实业态 ——
            var companies = m_CompanyQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            int stride = math.max(1, companies.Length / 24);
            int added = 0;
            for (int i = m_Offset % stride; i < companies.Length && added < k_MaxCompanyAnchors; i += stride)
            {
                var company = companies[i];
                var renter = EntityManager.GetComponentData<PropertyRenter>(company);
                var building = renter.m_Property;
                if (building == Entity.Null || !EntityManager.HasComponent<Transform>(building))
                    continue;

                var wp = EntityManager.GetComponentData<WorkProvider>(company);
                var prof = EntityManager.GetComponentData<Profitability>(company);
                int vacancy = wp.m_MaxWorkers - EntityManager.GetBuffer<Employee>(company).Length;

                AnchorKind? kind =
                    vacancy > 0 ? AnchorKind.Hiring :
                    prof.m_Profitability >= 200 ? AnchorKind.BusinessGood :
                    prof.m_Profitability <= 50 ? AnchorKind.BusinessBad :
                    (AnchorKind?)null;
                if (kind == null)
                    continue;

                var pos = EntityManager.GetComponentData<Transform>(building).m_Position;
                var word = BusinessWord(EntityManager.GetBuffer<Resources>(company));
                string detail = kind == AnchorKind.Hiring ? $"空 {vacancy} 个岗"
                    : kind == AnchorKind.BusinessGood ? "听说赚了"
                    : "听说快撑不住了";
                m_Anchors.Add(new Anchor(building, kind.Value, CompanyLabel(company, building, word, pos), detail));
                added++;

                if (calibrate)
                    Mod.Log.Info($"[Anchor·校准] 业态={word} 盈利={prof.m_Profitability} 空缺={vacancy}");
            }
            m_Offset++;
            companies.Dispose();

            // —— 公园/景点锚点 ——
            var parks = m_ParkQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            stride = math.max(1, parks.Length / (k_MaxParkAnchors * 2));
            added = 0;
            for (int i = 0; i < parks.Length && added < k_MaxParkAnchors; i += stride)
            {
                var building = parks[i];
                var pos = EntityManager.GetComponentData<Transform>(building).m_Position;
                m_Anchors.Add(new Anchor(building, AnchorKind.Park, PlaceLabel(building, pos, "公园/景点"), "散心的好去处"));
                added++;
            }
            parks.Dispose();

            if (calibrate)
                Mod.Log.Info($"[Anchor] 本轮锚点 {m_Anchors.Count} 个（公司池 {companies.Length}，公园池 {parks.Length}）");
        }

        // 资源→中文业态映射（Game.Economy.Resource 枚举 2026-08-20 dump 实测；未覆盖的走兜底"店"）
        private static readonly (Resource Res, string Word)[] k_ResourceWords =
        {
            (Resource.Meals, "餐馆"), (Resource.ConvenienceFood, "便利店"), (Resource.Food, "食品店"),
            (Resource.Vegetables, "菜店"), (Resource.Beverages, "饮品店"), (Resource.Fish, "水产店"),
            (Resource.Textiles, "服装店"), (Resource.Furniture, "家具店"), (Resource.Vehicles, "车行"),
            (Resource.Electronics, "电子产品店"), (Resource.Pharmaceuticals, "药店"),
            (Resource.Lodging, "酒店"), (Resource.Paper, "文具店"), (Resource.Telecom, "手机店"),
            (Resource.Entertainment, "娱乐场所"), (Resource.Recreation, "休闲场所"),
            (Resource.Financial, "银行"), (Resource.Media, "传媒公司"), (Resource.Software, "软件公司"),
        };

        /// <summary>公司主业判定：Resources buffer 里存货最多的非货币资源 → 中文业态词。</summary>
        private static string BusinessWord(DynamicBuffer<Resources> resources)
        {
            Resource top = Resource.NoResource;
            int best = -1;
            foreach (var r in resources)
            {
                if (r.m_Resource == Resource.Money || r.m_Resource == Resource.NoResource)
                    continue;
                if (r.m_Amount > best)
                {
                    best = r.m_Amount;
                    top = r.m_Resource;
                }
            }
            foreach (var (res, word) in k_ResourceWords)
                if (top == res)
                    return word;
            return "店"; // 兜底：未知/无存货业态
        }

        /// <summary>
        /// 公司锚点标签（2026-08-20 玩家定案：人说"解放路那家面馆"，不说"城西北"）：
        /// 真实公司名（NameSystem）+ 所在路名后缀；无名系统时回退"方位+那家+业态"的旧兜底。
        /// </summary>
        private string CompanyLabel(Entity company, Entity building, string word, float3 pos)
        {
            m_NameSystem ??= World.GetExistingSystemManaged<Game.UI.NameSystem>();
            if (m_NameSystem != null)
            {
                var name = m_NameSystem.GetRenderedLabelName(company);
                if (!string.IsNullOrEmpty(name))
                    return name + RoadSuffix(building);
            }
            return Geo.DirectionOf(pos) + "那家" + word + RoadSuffix(building);
        }

        /// <summary>地点锚点标签（公园/景点）：真实地名 + 路名后缀；无名系统回退方位+兜底词。</summary>
        private string PlaceLabel(Entity building, float3 pos, string fallback)
        {
            m_NameSystem ??= World.GetExistingSystemManaged<Game.UI.NameSystem>();
            if (m_NameSystem != null)
            {
                var name = m_NameSystem.GetRenderedLabelName(building);
                if (!string.IsNullOrEmpty(name))
                    return name + RoadSuffix(building);
            }
            return Geo.DirectionOf(pos) + fallback;
        }

        /// <summary>所在路名后缀"（神太街）"：建筑 m_RoadEdge 的渲染名；拿不到就空串（不硬造）。</summary>
        private string RoadSuffix(Entity building)
        {
            if (m_NameSystem == null || !EntityManager.HasComponent<Game.Buildings.Building>(building))
                return "";
            var road = EntityManager.GetComponentData<Game.Buildings.Building>(building).m_RoadEdge;
            if (road == Entity.Null)
                return "";
            var name = m_NameSystem.GetRenderedLabelName(road);
            return string.IsNullOrEmpty(name) ? "" : $"（{name}）";
        }

        // 方位命名已抽到 Geo.DirectionOf（GameBridge 共享：锚点/场馆/突发定位同一口径）
    }
}
