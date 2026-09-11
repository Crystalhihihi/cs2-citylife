using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Events;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 环境圈摘要（S6，§12 #48"人为主、场景为调料"）：给闲聊炉的处境卡附"旁边有什么"的调味素材。
    /// 以市民现位为圆心做四叉树半径查询，聚类蒸馏出 ≤3 条有戏点（每条 ≤15 字、"、"分隔），
    /// 由 BubbleChatterSystem 组炉时对每张被选中的处境卡调一次 <see cref="BuildDigest"/>——
    /// 每炉查询次数=卡片数（≤14），3-5 游戏分钟一炉，不进任何每帧/渲染热路径。
    ///
    /// 四叉树契约一句话：游戏自带 SearchSystem 树直接可迭代——`GetXxxSearchTree(readOnly: true, out deps)`
    /// → `deps.Complete()` → 自定义 `INativeQuadTreeIterator&lt;TItem, QuadTreeBoundsXZ&gt;`（Intersect 剪枝 /
    /// Iterate 收果）→ `tree.Iterate(ref it)`；树元素是粗粒度包围盒，收到后按包围盒中心精判 XZ 圆距。
    /// **勿复探**：六棵树归属/泛型参数/依赖纪律全实锤见 docs/spikes/2026-09-09-situation-matrix-fields.md §6
    /// （S0 勘误：QuadTreeBoundsXZ 在 Game.Common 不在 Colossal.Collections；Zones 树是 Bounds2 本系统不用）。
    ///
    /// 本系统用三棵（字段级复核记录）：
    /// - Objects static 树（Object+Static 实体）：建筑；**车站也在这棵**——车站是 ObjectPrefab 物件，
    ///   WaitingPassengers 直接挂车站实体（Game.Prefabs.TransportStop archetype 实锤，spike §4）；
    /// - Objects moving 树：人数密度。**坑**：沿车道走的行人登记在车道 LaneObject 缓冲而不进树
    ///   （Game.Creatures.ReferencesSystem 实锤），moving 树里只有"离道行人 agent + 载具"——
    ///   所以密度计数必须只数带 Game.Creatures.Resident 的实体（排除载具/动物），且读数偏保守；
    /// - Game.Routes 树（Waypoint+Position 实体，RouteSearchItem）兜底扫候车——车站若哪天不进
    ///   static 树（版本变化），候车数从这里补；取 max 幂等，重复命中无副作用。
    ///
    /// 蒸馏规则（执行层确定性、可审计，戏值排序=事件&gt;景点&gt;人气(候车/人挤人)&gt;店铺聚类）：
    /// a. 半径内建筑带 AccidentSite（位标志读法照 EventNewsSystem）→ 事件点（遭窃优先于车祸）；
    /// b. 签名建筑（SignatureBuildingData，CityChangeSystem 先例）→ 景点点（带真实地名）；
    /// c. 候车 max(WaitingPassengers.m_Count) ≥5 → 候车点；离道市民数 &gt;15 → "人挤人"点；
    /// d. 其余建筑按 CitizenPoolSystem.ClassifyBuilding 聚类计数，数量最多且 ≥2 的一类报代表
    ///    （真实店名=租户 Renter 的 NameSystem.GetRenderedLabelName，EntityAnchorSystem 先例）。
    /// 如何扩展：新戏点=在 Distill 里按戏值位次插入一条候选文案即可，别动查询层。
    ///
    /// 纪律：只读不写；自身 OnUpdate 为空（ RequireForUpdate 市民闸保持主菜单/空城静默），
    /// 所有工作由 BuildDigest 拉式驱动；SearchSystem/NameSystem 句柄懒解析+判空（游戏未就绪=空串）。
    /// </summary>
    public partial class EnvironmentDigestSystem : GameSystemBase
    {
        // 半径 40m（约半个街区）：设置页暴露留待正式版（S6 不做设置页，先定死）
        private const float k_Radius = 40f;
        private const int k_MaxPointLength = 15;  // 每条 ≤15 字（#48 防 token 爆）
        private const int k_MinWaiting = 5;       // 候车人气门槛：≥5 人才算"有人在等车"
        private const int k_CrowdThreshold = 15;  // 人挤人门槛：半径内离道市民数 >15（#48 定值）

        private EntityQuery m_CitizenQuery = default!;
        private Game.Objects.SearchSystem? m_ObjectSearch;  // 惰性：游戏自建系统，主菜单期拿不到
        private Game.Routes.SearchSystem? m_RouteSearch;    // 惰性同上（候车兜底扫描）
        private Game.UI.NameSystem? m_NameSystem;           // 惰性：真实店名/地名（CitizenPoolSystem 同款先例）
        private bool m_SampleLogged;                        // 首炉样例只打一次（实机核对用）

        protected override void OnCreate()
        {
            base.OnCreate();
            // 空城/主菜单闸（与各读侧系统同口径）：无市民则整系统静默
            m_CitizenQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Citizens.Citizen>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            RequireForUpdate(m_CitizenQuery);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 4096; // 2 的幂；OnUpdate 本为空转，仅压调度频率

        protected override void OnUpdate()
        {
            // 拉式驱动：工作全在 BuildDigest（闲聊炉组炉时逐卡调用），这里无事可做
        }

        /// <summary>
        /// 以 center 为圆心查半径 40m 环境，蒸馏 ≤maxPoints 条有戏点拼接（"、"分隔，每条 ≤15 字）。
        /// 无戏点/游戏未就绪 → 空串。只在组炉低频点调用（主线程），禁入热路径。
        /// </summary>
        public string BuildDigest(float3 center, int maxPoints = 3)
        {
            if (maxPoints <= 0)
                return "";
            // 懒解析 + 判空：主菜单/游戏系统未就绪时静默空串（绝不阻塞/抛错）
            m_ObjectSearch ??= World.GetExistingSystemManaged<Game.Objects.SearchSystem>();
            m_RouteSearch ??= World.GetExistingSystemManaged<Game.Routes.SearchSystem>();
            m_NameSystem ??= World.GetExistingSystemManaged<Game.UI.NameSystem>();
            if (m_ObjectSearch == null)
                return "";

            // 查询盒：Y 轴放宽到近乎全程——四叉树按 XZ 组织，放宽 Y 即纯水平半径语义
            var queryBounds = new Bounds3(
                new float3(center.x - k_Radius, -10000f, center.z - k_Radius),
                new float3(center.x + k_Radius, 10000f, center.z + k_Radius));

            // 读树纪律（spike §6 注意③）：readOnly 取树 + 依赖 Complete，主线程直查最省心
            var statics = new NativeList<Entity>(64, Allocator.Temp);
            var staticTree = m_ObjectSearch.GetStaticSearchTree(readOnly: true, out var depsStatic);
            depsStatic.Complete();
            var itStatic = new NearbyEntityIterator(queryBounds, center.xz, k_Radius, statics);
            staticTree.Iterate(ref itStatic);

            var moving = new NativeList<Entity>(32, Allocator.Temp);
            var movingTree = m_ObjectSearch.GetMovingSearchTree(readOnly: true, out var depsMoving);
            depsMoving.Complete();
            var itMoving = new NearbyEntityIterator(queryBounds, center.xz, k_Radius, moving);
            movingTree.Iterate(ref itMoving);

            var routeStops = new NativeList<Entity>(8, Allocator.Temp);
            if (m_RouteSearch != null)
            {
                var routeTree = m_RouteSearch.GetSearchTree(readOnly: true, out var depsRoute);
                depsRoute.Complete();
                var itRoute = new NearbyRouteIterator(queryBounds, center.xz, routeStops);
                routeTree.Iterate(ref itRoute);
            }

            var result = Distill(statics, moving, routeStops, maxPoints);
            statics.Dispose();
            moving.Dispose();
            routeStops.Dispose();

            // 首炉打一行样例（center→digest）供实机核对；之后每炉计数行在 BubbleChatterSystem
            if (result.Length > 0 && !m_SampleLogged)
            {
                m_SampleLogged = true;
                Mod.Log.Info($"[环境圈] 摘要样例：({center.x:F0},{center.z:F0}) → {result}");
            }
            return result;
        }

        /// <summary>
        /// 半径收集（S7 小剧场选锚点/绑名单用）：center 半径 radius 内的 Objects static 树实体
        /// （建筑/车站——车站是 ObjectPrefab 物件，spike §4）与 moving 树实体（离道行人 agent + 载具，
        /// 带 Game.Creatures.Resident 的才是行人，调用方再滤）。结果**追加**进调用方给的表（自行清空）。
        /// 主线程低频点专用（组炉/组剧场级），禁入热路径；游戏未就绪=两表原样（不加任何东西）。
        /// </summary>
        public void CollectAround(float3 center, float radius, NativeList<Entity> statics, NativeList<Entity> movers)
        {
            m_ObjectSearch ??= World.GetExistingSystemManaged<Game.Objects.SearchSystem>();
            if (m_ObjectSearch == null)
                return;
            // 查询盒 Y 轴放宽到近乎全程——四叉树按 XZ 组织，放宽 Y 即纯水平半径语义
            var queryBounds = new Bounds3(
                new float3(center.x - radius, -10000f, center.z - radius),
                new float3(center.x + radius, 10000f, center.z + radius));
            // 读树纪律（spike §6 注意③）：readOnly 取树 + 依赖 Complete，主线程直查
            var staticTree = m_ObjectSearch.GetStaticSearchTree(readOnly: true, out var depsStatic);
            depsStatic.Complete();
            var itStatic = new NearbyEntityIterator(queryBounds, center.xz, radius, statics);
            staticTree.Iterate(ref itStatic);
            var movingTree = m_ObjectSearch.GetMovingSearchTree(readOnly: true, out var depsMoving);
            depsMoving.Complete();
            var itMoving = new NearbyEntityIterator(queryBounds, center.xz, radius, movers);
            movingTree.Iterate(ref itMoving);
        }

        /// <summary>聚类蒸馏（执行层确定性）：戏值排序 事件&gt;景点&gt;人气(候车/人挤人)&gt;店铺聚类，取前 maxPoints 条逐条截 15 字。</summary>
        private string Distill(NativeList<Entity> statics, NativeList<Entity> moving, NativeList<Entity> routeStops, int maxPoints)
        {
            string? crimePoint = null, trafficPoint = null, signaturePoint = null;
            var clusterCounts = new Dictionary<string, int>();      // 类词 → 栋数
            var clusterRep = new Dictionary<string, Entity>();      // 类词 → 代表建筑（取名用，首个）
            var maxWaiting = 0;

            for (int i = 0; i < statics.Length; i++)
            {
                var e = statics[i];
                if (EntityManager.HasComponent<WaitingPassengers>(e))
                {
                    var w = EntityManager.GetComponentData<WaitingPassengers>(e).m_Count; // 当前候车人数（spike §4：每帧重建的计数）
                    if (w > maxWaiting)
                        maxWaiting = w;
                }
                if (!EntityManager.HasComponent<Building>(e))
                    continue; // 车站牌/树等非建筑物件不进建筑规则
                if (EntityManager.HasComponent<AccidentSite>(e))
                {
                    // 位标志读法照 EventNewsSystem：CrimeScene 且非 CrimeFinished=遭窃；TrafficAccident=车祸
                    var flags = EntityManager.GetComponentData<AccidentSite>(e).m_Flags;
                    if ((flags & AccidentSiteFlags.CrimeScene) != 0 && (flags & AccidentSiteFlags.CrimeFinished) == 0)
                        crimePoint = "旁边有店铺遭了窃";
                    else if ((flags & AccidentSiteFlags.TrafficAccident) != 0)
                        trafficPoint = "旁边路口出了车祸";
                }
                if (EntityManager.HasComponent<Game.Prefabs.SignatureBuildingData>(e) && signaturePoint == null)
                {
                    var name = RenderedName(m_NameSystem, e);
                    signaturePoint = name != null ? $"就在「{name}」旁边" : "旁边有个景点";
                    continue; // 景点单独优先报，不进聚类计数
                }
                var kind = CitizenPoolSystem.ClassifyBuilding(EntityManager, e);
                if (kind == null || kind == "景点")
                    continue; // 景点（第二处起的签名建筑）单独优先报过了，不进聚类计数
                clusterCounts[kind] = clusterCounts.TryGetValue(kind, out var c) ? c + 1 : 1;
                if (!clusterRep.ContainsKey(kind))
                    clusterRep[kind] = e;
            }

            // Routes 树兜底扫候车（车站若没进 static 树从这里补；取 max 幂等）
            for (int i = 0; i < routeStops.Length; i++)
            {
                var e = routeStops[i];
                if (!EntityManager.HasComponent<WaitingPassengers>(e))
                    continue;
                var w = EntityManager.GetComponentData<WaitingPassengers>(e).m_Count;
                if (w > maxWaiting)
                    maxWaiting = w;
            }

            // 人挤人：moving 树只数带 Resident 的（排除载具/动物；沿车道行人本就不在树里，读数偏保守）
            var crowd = 0;
            for (int i = 0; i < moving.Length; i++)
                if (EntityManager.HasComponent<Game.Creatures.Resident>(moving[i]))
                    crowd++;

            // 店铺聚类：数量最多且 ≥2 的一类报代表（单栋不成"聚类"）
            string? clusterPoint = null;
            var bestCount = 1;
            string? bestKind = null;
            foreach (var kv in clusterCounts)
            {
                if (kv.Value > bestCount)
                {
                    bestCount = kv.Value;
                    bestKind = kv.Key;
                }
            }
            if (bestKind != null)
            {
                var name = ShopNameOf(EntityManager, m_NameSystem, clusterRep[bestKind]);
                var suffix = ClusterSuffix(bestKind);
                clusterPoint = name != null ? $"「{name}」等{bestCount}{suffix}" : $"旁边有{bestCount}{suffix}";
            }

            // 按戏值排序收前 maxPoints 条
            var points = new List<string>(5);
            if (crimePoint != null) points.Add(crimePoint);
            else if (trafficPoint != null) points.Add(trafficPoint);
            if (signaturePoint != null) points.Add(signaturePoint);
            if (maxWaiting >= k_MinWaiting) points.Add($"旁边车站有{maxWaiting}人候车");
            if (crowd > k_CrowdThreshold) points.Add("这块儿人挤人");
            if (clusterPoint != null) points.Add(clusterPoint);
            if (points.Count == 0)
                return "";

            var sb = new System.Text.StringBuilder();
            var n = math.min(maxPoints, points.Count);
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                    sb.Append('、');
                var p = points[i];
                sb.Append(p.Length > k_MaxPointLength ? p.Substring(0, k_MaxPointLength) : p);
            }
            return sb.ToString();
        }

        /// <summary>真实店名：租户公司名优先（EntityAnchorSystem 先例：NameSystem.GetRenderedLabelName(company)），
        /// 无名回退建筑自身渲染名，再无名 → null（聚类点退纯计数文案）。
        /// internal static：S7 小剧场的场景卡取店名共用这一处定义（别复制粘贴）。</summary>
        internal static string? ShopNameOf(EntityManager em, Game.UI.NameSystem? nameSystem, Entity building)
        {
            if (nameSystem == null)
                return null;
            if (em.HasBuffer<Renter>(building))
            {
                var renters = em.GetBuffer<Renter>(building);
                for (int i = 0; i < renters.Length; i++)
                {
                    var name = nameSystem.GetRenderedLabelName(renters[i].m_Renter);
                    if (!IsUglyName(name)) // 脏键跳过：继续找下一个租户，不行才落建筑名（IsUglyName 实锤见该方法注释）
                        return name;
                }
            }
            return RenderedName(nameSystem, building);
        }

        /// <summary>渲染名兜底（NameSystem 缺席/空名/未本地化脏键 → null，不硬造）。internal static：S7 场景卡共用。</summary>
        internal static string? RenderedName(Game.UI.NameSystem? nameSystem, Entity entity)
        {
            var name = nameSystem?.GetRenderedLabelName(entity);
            return IsUglyName(name) ? null : name;
        }

        /// <summary>脏键判定（一处定义全桥共用）：null/空/未本地化原始资产键（"Assets.NAME[...]"/"Assets.xxx"——
        /// 部分资产无本地化名，实机实锤 Commercial_ChemicalStore）都算脏，调用方一律走降级，脏键绝不进 prompt
        /// （2026-09-11 玩家实机"Assets 频率这么高"——经环境圈摘要灌进闲聊卡被模型复读，高频污染源）。</summary>
        internal static bool IsUglyName(string? name)
            => string.IsNullOrEmpty(name) || name.StartsWith("Assets.", System.StringComparison.Ordinal);

        /// <summary>聚类文案量词后缀（类词出自 CitizenPoolSystem.ClassifyBuilding，一处定义）。</summary>
        private static string ClusterSuffix(string kind) => kind switch
        {
            "商店" => "家店",
            "住宅区" => "栋住宅楼",
            "工厂" => "家工厂",
            "办公楼" => "栋办公楼",
            "公园" => "个公园",
            "学校" => "所学校",
            "医院" => "家医院",
            _ => "处建筑",
        };

        /// <summary>
        /// Entity 树通用半径收集器（Objects static/moving 两棵同型复用）：
        /// Intersect 子树剪枝（包围盒相交即进），Iterate 按包围盒中心精判 XZ 圆距后收实体
        /// （树元素是粗粒度包围盒，spike §6 注意②）。
        /// </summary>
        private struct NearbyEntityIterator : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
        {
            private Bounds3 m_QueryBounds;
            private float2 m_Center;
            private float m_Radius;   // 精判圆半径（S7 起按调用方传入，BuildDigest 用 k_Radius）
            private NativeList<Entity> m_Results;

            public NearbyEntityIterator(Bounds3 queryBounds, float2 center, float radius, NativeList<Entity> results)
            {
                m_QueryBounds = queryBounds;
                m_Center = center;
                m_Radius = radius;
                m_Results = results;
            }

            public bool Intersect(QuadTreeBoundsXZ bounds) => MathUtils.Intersect(bounds.m_Bounds, m_QueryBounds);

            public void Iterate(QuadTreeBoundsXZ bounds, Entity item)
            {
                float2 d = MathUtils.Center(bounds.m_Bounds).xz - m_Center;
                if (math.dot(d, d) <= m_Radius * m_Radius)
                    m_Results.Add(item);
            }
        }

        /// <summary>Routes 树收集器（RouteSearchItem.m_Entity 即站点/路径点实体；m_Element 下标不需要）。</summary>
        private struct NearbyRouteIterator : INativeQuadTreeIterator<RouteSearchItem, QuadTreeBoundsXZ>
        {
            private Bounds3 m_QueryBounds;
            private float2 m_Center;
            private NativeList<Entity> m_Results;

            public NearbyRouteIterator(Bounds3 queryBounds, float2 center, NativeList<Entity> results)
            {
                m_QueryBounds = queryBounds;
                m_Center = center;
                m_Results = results;
            }

            public bool Intersect(QuadTreeBoundsXZ bounds) => MathUtils.Intersect(bounds.m_Bounds, m_QueryBounds);

            public void Iterate(QuadTreeBoundsXZ bounds, RouteSearchItem item)
            {
                float2 d = MathUtils.Center(bounds.m_Bounds).xz - m_Center;
                if (math.dot(d, d) <= k_Radius * k_Radius)
                    m_Results.Add(item.m_Entity);
            }
        }
    }
}
