using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Companies;
using Game.Net;
using Unity.Entities;
using Transform = Game.Objects.Transform;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 城市变化感知器（读侧，2026-08-20"有看头"第一刀：与我有关+信息增量）：
    /// 玩家/城市造成的**变化**变成话题素材——新开的公司、落成的公服/公园/地标、被拆的建筑、
    /// 新建道路、人口门槛。信息流从"平行宇宙自言自语"变成"市长决策的回声壁"。
    ///
    /// 产出两条通道：
    /// - 变化锚点队列（DrainChanges 供内容导演每炉最多取 2 条，走常规锚点管道进 prompt，
    ///   市民自然谈论"新开了家 XX"——零额外 LLM 调用）；
    /// - 大事件直发快讯（拆除/公服落成/人口门槛/新路，模板帖不耗 LLM）。
    ///
    /// 判别（组件全实锤，GameDllDump）：学校/医院/派出所/消防站=Game.Buildings 同名标记组件；
    /// 公园=AttractivenessProvider；地标=Game.Prefabs.SignatureBuildingData；道路=Game.Net.Road 计数差。
    /// 纪律：首轮只建基线不翻旧账（读档不刷历史）；锚点队列上限 12（积压丢最旧）；只读不写。
    /// </summary>
    public partial class CityChangeSystem : GameSystemBase
    {
        private const int k_MaxPending = 12;

        private EntityQuery m_CitizenQuery = default!;
        private EntityQuery m_CompanyQuery = default!;
        private EntityQuery m_BuildingQuery = default!;
        private EntityQuery m_RoadQuery = default!;
        private Game.UI.NameSystem? m_NameSystem;   // 惰性解析（真实公司名/建筑名）

        private readonly HashSet<Entity> m_KnownCompanies = new();
        private readonly Dictionary<Entity, string> m_KnownTrackBuildings = new(); // 受追踪建筑 → 标签（拆除播报用）
        private readonly Queue<Anchor> m_Pending = new();   // 待分配的变化锚点
        private bool m_Initialized;
        private int m_LastRoadCount;
        private int m_NextMilestoneIdx;

        // 人口门槛（破了发快讯——城市长大的实感时刻）
        private static readonly int[] k_Milestones = { 1000, 5000, 10000, 25000, 50000, 100000, 200000, 500000 };

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CitizenQuery = GetEntityQuery(
                ComponentType.ReadOnly<Citizen>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_CompanyQuery = GetEntityQuery(
                ComponentType.ReadOnly<CommercialCompany>(),
                ComponentType.ReadOnly<PropertyRenter>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_BuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_RoadQuery = GetEntityQuery(
                ComponentType.ReadOnly<Road>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            RequireForUpdate(m_CitizenQuery);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 4096;

        /// <summary>导演取变化锚点（每炉最多 max 条）：从队列吸出，吸完即止（不复用——变化不重复播）。</summary>
        public int DrainChanges(List<Anchor> dst, int max)
        {
            var n = 0;
            while (n < max && m_Pending.Count > 0)
            {
                dst.Add(m_Pending.Dequeue());
                n++;
            }
            return n;
        }

        protected override void OnUpdate()
        {
            m_NameSystem ??= World.GetExistingSystemManaged<Game.UI.NameSystem>();
            if (!m_Initialized)
            {
                // 基线：已存在的公司/建筑/道路/人口都不算新闻（防读档刷历史）
                RegisterBaseline();
                m_Initialized = true;
                return;
            }

            PollCompanies();
            PollBuildings();
            PollRoads();
            PollMilestones();
        }

        private void RegisterBaseline()
        {
            var companies = m_CompanyQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            foreach (var c in companies)
                m_KnownCompanies.Add(c);
            companies.Dispose();

            var buildings = m_BuildingQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            foreach (var b in buildings)
            {
                var kind = Classify(b);
                if (IsTracked(kind)) // 只登记受追踪类型（标签存好，拆除才有得播）
                    m_KnownTrackBuildings[b] = LabelOf(b, kind);
            }
            buildings.Dispose();

            m_LastRoadCount = m_RoadQuery.CalculateEntityCount();
            var citizens = m_CitizenQuery.CalculateEntityCount();
            m_NextMilestoneIdx = 0;
            while (m_NextMilestoneIdx < k_Milestones.Length && citizens >= k_Milestones[m_NextMilestoneIdx])
                m_NextMilestoneIdx++;
            Mod.Log.Info($"[Change] 基线：公司 {m_KnownCompanies.Count}，受追踪建筑 {m_KnownTrackBuildings.Count}，道路 {m_LastRoadCount}，人口 {citizens}");
        }

        // —— 新公司："新开了家 XX"（取真实公司名；锚定其店面建筑供"前往现场"）——
        private void PollCompanies()
        {
            var companies = m_CompanyQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            foreach (var c in companies)
            {
                if (m_KnownCompanies.Contains(c))
                    continue;
                m_KnownCompanies.Add(c);
                var building = EntityManager.GetComponentData<PropertyRenter>(c).m_Property;
                var name = RenderedName(c);
                if (string.IsNullOrEmpty(name))
                    name = "一家新店";
                Enqueue(new Anchor(building, AnchorKind.NewShop, name, "新开张，去瞅瞅"));
            }
            // 公司注销（倒闭/搬走）本轮不播——只报生不报死（死的量太吵，留给市民自己发现）
            companies.Dispose();
        }

        // —— 建筑 diff：新落成（公服/公园/地标）+ 被拆（受追踪建筑消失）——
        private void PollBuildings()
        {
            var current = m_BuildingQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            var currentSet = new HashSet<Entity>(current.Length);
            foreach (var b in current)
                currentSet.Add(b);

            // 新落成
            foreach (var b in current)
            {
                if (m_KnownTrackBuildings.ContainsKey(b))
                    continue;
                var kind = Classify(b);
                if (!IsTracked(kind))
                    continue; // 住宅/工业/办公等不追踪（量太大）
                var label = LabelOf(b, kind);
                m_KnownTrackBuildings[b] = label;
                var detail = kind == AnchorKind.NewSignature ? "新地标落成了" : "刚落成";
                Enqueue(new Anchor(b, kind, label, detail));
                // 公服/地标落成直发快讯（玩家动作的即时回声）
                if (kind == AnchorKind.NewService || kind == AnchorKind.NewSignature)
                    Flash($"{label} 落成。", b);
            }

            // 被拆（受追踪建筑从世界消失）
            m_Scratch.Clear();
            foreach (var kv in m_KnownTrackBuildings)
                if (!currentSet.Contains(kv.Key))
                    m_Scratch.Add(kv.Key);
            foreach (var dead in m_Scratch)
            {
                var label = m_KnownTrackBuildings[dead];
                m_KnownTrackBuildings.Remove(dead);
                // 拆除锚 Entity=Null：楼没了，"前往现场"无的放矢
                Enqueue(new Anchor(Entity.Null, AnchorKind.Demolished, label, "被拆了"));
                Flash($"{label} 被拆了，老地方没了。", Entity.Null);
            }

            current.Dispose();
        }

        // —— 道路：计数差（"路网还在长"）——
        private void PollRoads()
        {
            var count = m_RoadQuery.CalculateEntityCount();
            var delta = count - m_LastRoadCount;
            m_LastRoadCount = count;
            if (delta > 0)
                Flash($"新修了 {delta} 段路，路网还在长。", Entity.Null);
        }

        // —— 人口门槛：破了发快讯（城市长大的实感时刻）——
        private void PollMilestones()
        {
            if (m_NextMilestoneIdx >= k_Milestones.Length)
                return;
            var citizens = m_CitizenQuery.CalculateEntityCount();
            if (citizens < k_Milestones[m_NextMilestoneIdx])
                return;
            Flash($"本市人口突破 {k_Milestones[m_NextMilestoneIdx]}！", Entity.Null);
            m_NextMilestoneIdx++;
        }

        /// <summary>建筑分类（组件全实锤）：学校/医院/派出所/消防站/公园/地标，其余不追踪。</summary>
        private AnchorKind Classify(Entity building)
        {
            if (EntityManager.HasComponent<Game.Prefabs.SignatureBuildingData>(building)) return AnchorKind.NewSignature;
            if (EntityManager.HasComponent<School>(building)
                || EntityManager.HasComponent<Hospital>(building)
                || EntityManager.HasComponent<PoliceStation>(building)
                || EntityManager.HasComponent<FireStation>(building)) return AnchorKind.NewService;
            if (EntityManager.HasComponent<AttractivenessProvider>(building)) return AnchorKind.NewPark;
            return AnchorKind.Park; // 未用占位：不追踪（IsTracked 会过滤掉）
        }

        private static bool IsTracked(AnchorKind kind)
            => kind == AnchorKind.NewSignature || kind == AnchorKind.NewService || kind == AnchorKind.NewPark;

        /// <summary>建筑标签：真实建筑名（NameSystem），拿不到按类型给兜底词。</summary>
        private string LabelOf(Entity building, AnchorKind kind)
        {
            var name = RenderedName(building);
            if (!string.IsNullOrEmpty(name))
                return name;
            if (kind == AnchorKind.NewSignature) return "新地标";
            if (kind == AnchorKind.NewPark) return "新公园";
            if (EntityManager.HasComponent<School>(building)) return "新学校";
            if (EntityManager.HasComponent<Hospital>(building)) return "新医院";
            if (EntityManager.HasComponent<PoliceStation>(building)) return "新派出所";
            if (EntityManager.HasComponent<FireStation>(building)) return "新消防站";
            return "新建筑";
        }

        private string? RenderedName(Entity entity)
            => m_NameSystem?.GetRenderedLabelName(entity);

        private void Enqueue(Anchor anchor)
        {
            while (m_Pending.Count >= k_MaxPending)
                m_Pending.Dequeue(); // 积压丢最旧（变化太多时保新鲜）
            m_Pending.Enqueue(anchor);
            Mod.Log.Info($"[Change] 锚点入队：{anchor.Label}（{anchor.Kind}）");
        }

        /// <summary>快讯直发（模板帖，不耗 LLM）：拆除/落成/门槛/新路。</summary>
        private static void Flash(string text, Entity entity)
        {
            if (entity != Entity.Null)
                Mod.Feed.Record(new Content.Post("城市快讯", text, Content.Topic.Breaking, "newsflash"),
                                entity.Index, entity.Version);
            else
                Mod.Feed.Record(new Content.Post("城市快讯", text, Content.Topic.Breaking, "newsflash"));
            Mod.Log.Info($"[Change] 快讯：{text}");
        }

        private readonly List<Entity> m_Scratch = new();
    }
}
