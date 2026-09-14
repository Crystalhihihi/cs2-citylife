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
    /// 实体锚点：一条"具体到对象"的话题线索。Label=真实店名/地名（NameSystem+路名），
    /// Detail=一句可入 prompt 的线索（"空 3 个岗"）。Entity 供信息流挂"点击聚焦"用。
    /// Company=公司锚点时的公司实体（广告层取产出业态用；非公司锚点 Null）。
    /// </summary>
    public readonly struct Anchor
    {
        public readonly Entity Entity;
        public readonly AnchorKind Kind;
        public readonly string Label;
        public readonly string Detail;
        public readonly Entity Company;

        public Anchor(Entity entity, AnchorKind kind, string label, string detail, Entity company = default)
        {
            Entity = entity;
            Kind = kind;
            Label = label;
            Detail = detail;
            Company = company;
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
                var word = BusinessWord(EntityManager, company, EntityManager.GetBuffer<Resources>(company));
                string detail = kind == AnchorKind.Hiring ? $"空 {vacancy} 个岗"
                    : kind == AnchorKind.BusinessGood ? "听说赚了"
                    : "听说快撑不住了";
                m_Anchors.Add(new Anchor(building, kind.Value, CompanyLabel(company, building, word, pos), detail, company));
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

        /// <summary>公司主业判定（2026-08-20 R3.1 修正）：产出声明优先（ShopOutput——存货最多≠卖什么，
        /// 投入品混入实锤：餐厅 Food 是原料 Meals 才是商品）；拿不到回退 Resources 存货猜测。</summary>
        private static string BusinessWord(EntityManager em, Entity company, DynamicBuffer<Resources> resources)
        {
            var output = ShopOutput.OutputOf(em, company);
            if (output != Resource.NoResource)
                return ShopOutput.WordOf(output);
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
            return ShopOutput.WordOf(top);
        }

        // （k_ResourceWords 已移交 ShopOutput——业态词一处定义，锚点/广告共用）

        /// <summary>
        /// 公司锚点标签（2026-08-20 玩家定案：人说"解放路那家面馆"，不说"城西北"）：
        /// 真实公司名（NameSystem）+ 所在路名后缀；无名公司走真路名地址制（§12 #60 刀⑤，
        /// "115冬青街那家便利店"——门牌号由游戏公共方法 BuildingUtils.GetAddress 现算，spike 实锤
        /// docs/spikes/2026-09-11-building-address-system.md）；地址拿不到才退"方位+那家+业态"最末兜底。
        /// 2026-09-09 实锤补充：GetRenderedLabelName 对无自定义名公司返回原始资产 ID
        /// （"Assets.NAME[Commercial_ConvenienceFoodStore]"——署名乱码源头），检出即视同无名回退；
        /// 2026-09-14 普查再实锤：脏键率 34/34=公司 prefab 全没本地化名，故脏键后**先落品牌真名层**
        /// （CompanyNameOf：m_Brand 品牌实体名，与游戏 UI 公司名同源），品牌也拿不到才退地址制；
        /// word 本身已是产出业态词（BusinessWord → ShopOutput），无需再借 ClassifyBuilding。
        /// </summary>
        private string CompanyLabel(Entity company, Entity building, string word, float3 pos)
        {
            m_NameSystem ??= World.GetExistingSystemManaged<Game.UI.NameSystem>();
            if (m_NameSystem != null)
            {
                var name = m_NameSystem.GetRenderedLabelName(company);
                if (!EnvironmentDigestSystem.IsUglyName(name)) // 非脏键=玩家自定义公司名，最优先
                    return name + RoadSuffix(building);
                var brand = EnvironmentDigestSystem.CompanyNameOf(EntityManager, m_NameSystem, company);
                if (brand != null) // 品牌真名层（普查实锤：公司渲染名全脏键，真店名在 m_Brand）
                    return brand + RoadSuffix(building);
            }
            return TryGetAddressLabel(building, out var addr)
                ? addr + "那家" + word
                : Geo.DirectionOf(pos) + "那家" + word + RoadSuffix(building);
        }

        /// <summary>地点锚点标签（公园/景点）：真实地名 + 路名后缀；无名走真路名地址（刀⑤），再退方位兜底。</summary>
        private string PlaceLabel(Entity building, float3 pos, string fallback)
        {
            m_NameSystem ??= World.GetExistingSystemManaged<Game.UI.NameSystem>();
            if (m_NameSystem != null)
            {
                var name = m_NameSystem.GetRenderedLabelName(building);
                if (!EnvironmentDigestSystem.IsUglyName(name))
                    return name + RoadSuffix(building);
            }
            return TryGetAddressLabel(building, out var addr)
                ? addr + fallback
                : Geo.DirectionOf(pos) + fallback;
        }

        /// <summary>所在路名后缀"（神太街）"：建筑 m_RoadEdge 的渲染名；拿不到/脏键就空串（不硬造）。</summary>
        private string RoadSuffix(Entity building)
        {
            if (m_NameSystem == null || !EntityManager.HasComponent<Game.Buildings.Building>(building))
                return "";
            var road = EntityManager.GetComponentData<Game.Buildings.Building>(building).m_RoadEdge;
            if (road == Entity.Null)
                return "";
            var name = m_NameSystem.GetRenderedLabelName(road);
            return EnvironmentDigestSystem.IsUglyName(name) ? "" : $"（{name}）";
        }

        /// <summary>真路名地址（"115冬青街"，§12 #60 刀⑤）：实现已收编 EnvironmentDigestSystem.TryGetAddressLabel
        /// （一处定义别复制粘贴，语义/实锤见彼处注释）；此处只做惰性 NameSystem 解析的实例包装。</summary>
        private bool TryGetAddressLabel(Entity building, out string label)
        {
            m_NameSystem ??= World.GetExistingSystemManaged<Game.UI.NameSystem>();
            return EnvironmentDigestSystem.TryGetAddressLabel(EntityManager, m_NameSystem, building, out label);
        }

        // 方位命名已抽到 Geo.DirectionOf（GameBridge 共享：锚点/场馆/突发定位同一口径）
    }
}
