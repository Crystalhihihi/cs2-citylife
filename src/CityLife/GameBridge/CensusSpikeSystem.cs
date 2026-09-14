using System.Collections.Generic;
using System.Text;
using Game;
using Game.Common;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 普查 + 事件监听口 spike（2026-09-14，§12 #62 第二步 / #64 / #65 的收口工具）。
    /// **spike 工具，验证完即退役**——退役方法：Mod.cs 里删掉本系统的 UpdateAt 一行即可（登记处搜 CensusSpikeSystem）。
    ///
    /// 目的：反编译侦察（logs/census-spike-20260914/ + logs/event-watch-spike-20260914/，
    /// 报告 docs/spikes/2026-09-14-census-and-event-watch.md）已把组件层路径钉死，
    /// 但有一批"资产层/运行时才可见"的未实锤项只能靠游戏内 dump 收口（报告 §4 清单）。
    ///
    /// 两个热键（Ctrl+数字组合，F9 撞车教训后 spike 键惯例；Ctrl+6/7 于 2026-09-09 §12 #45 砍除后空闲）：
    /// - **Ctrl+6 普查 dump**（按一次打一轮，一次性）：跨步抽样 ≤64 栋建筑逐栋打 `[普查]` 行
    ///   （实体号/类别/自长/prefab 名/两套 BuildingFlags/Localization 挂接/渲染名/门牌地址），
    ///   每栋 ≤4 个租户各打一行（公司：prefab 名 + IndustrialProcessData 有无及投入产出 + m_IsImport + 品牌名，
    ///   无则打 Extractor/Service/Commercial 标记组件有无；住户：渲染名=住户姓验证），
    ///   末尾一行全量类别分布 + 一行可入刊事件 prefab 清单（EventJournalSystem.eventPrefabs）+ 合计行数。
    /// - **Ctrl+7 事件探针开关**（拨一下开、再拨关，状态进日志）：开启期间每帧查
    ///   Building+Created / Road+Created / Tree+Created / Building+Deleted（Created/Deleted 只活一帧，
    ///   PrepareCleanUpSystem→CleanUpSystem 每帧清，必须 GetUpdateInterval=1 当帧观察；
    ///   Deleted 支路查询不加 Exclude&lt;Deleted&gt;——实体当帧还在、组件可读）。
    ///   命中逐条打 `[事件探针]`；单帧同类 &gt;20 条合并成一行计数防爆日志。
    ///
    /// 如何扩展：加普查字段=在 DumpBuilding/DumpRenter 里加一行读取（HasComponent 先行再 GetComponentData）；
    /// 加探针类别=OnCreate 缓存新查询 + ProbeFrame 里加一支（ Deleted 类支路记得别加 Exclude&lt;Deleted&gt;）。
    ///
    /// 纪律：只读不写；dump 是主线程一次性 O(64) 循环不阻塞模拟；探针关闭时零成本（不跑任何查询），
    /// 开启时命中为空 ToEntityArray 后 Length==0 直接返回不分配；禁用 SystemAPI（源码生成器不跑）；
    /// 查询全部 OnCreate 缓存；游戏自建系统（PrefabSystem/NameSystem/EventJournalSystem）惰性解析+判空。
    /// </summary>
    public partial class CensusSpikeSystem : GameSystemBase
    {
        private const int k_MaxCensus = 64;      // 普查抽样上限（≥50 栋目标的两倍余量）
        private const int k_MaxRenters = 4;      // 每栋最多打的租户行数
        private const int k_MergeThreshold = 20; // 单帧同类命中超此数合并成一行计数

        private EntityQuery m_BuildingQuery = default!;        // 普查主查询 + RequireForUpdate 闸（主菜单/空城静默）
        private EntityQuery m_BuildingCreatedQuery = default!;
        private EntityQuery m_RoadCreatedQuery = default!;
        private EntityQuery m_TreeCreatedQuery = default!;
        private EntityQuery m_BuildingDeletedQuery = default!; // Deleted 支路：不能加 Exclude<Deleted>

        private PrefabSystem? m_PrefabSystem;                  // 惰性：prefab 内部名（GetPrefabName public）
        private Game.UI.NameSystem? m_NameSystem;              // 惰性：渲染名/路名
        private Game.Events.EventJournalSystem? m_JournalSystem; // 惰性：可入刊事件 prefab 清单

        private bool m_ProbeOn;      // 事件探针开关（Ctrl+7 拨动）
        private uint m_Frame;
        private uint m_LastKeyFrame; // 上次触发帧，30 帧防抖（按住/单键多帧登记只触发一次）

        protected override void OnCreate()
        {
            base.OnCreate();
            m_BuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.Building>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            // —— 事件探针四类（Created/Deleted 只活一帧，查询不含 Temp 即可，命中当帧组件全可读）——
            m_BuildingCreatedQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.Building>(),
                ComponentType.ReadOnly<Created>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_RoadCreatedQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Net.Road>(),
                ComponentType.ReadOnly<Created>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_TreeCreatedQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Objects.Tree>(),
                ComponentType.ReadOnly<Created>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            // 拆除支路：Deleted 当帧实体还在，绝不能 Exclude<Deleted>
            m_BuildingDeletedQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.Building>(),
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            RequireForUpdate(m_BuildingQuery); // 主菜单/空城整系统静默（热键也不响应）
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 1; // 2 的幂；热键捕获+探针当帧都要逐帧

        protected override void OnUpdate()
        {
            // Ctrl+6 普查 dump / Ctrl+7 探针开关；30 帧防抖（InjectionSpikeSystem 同款）
            var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            var debounced = m_Frame - m_LastKeyFrame > 30;
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.Alpha6)) { m_LastKeyFrame = m_Frame; RunCensus(); }
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.Alpha7)) { m_LastKeyFrame = m_Frame; ToggleProbe(); }
            m_Frame++;

            if (m_ProbeOn)
                ProbeFrame();
        }

        // ==================================================================
        // 热键A：普查 dump
        // ==================================================================

        private void RunCensus()
        {
            m_PrefabSystem ??= World.GetExistingSystemManaged<PrefabSystem>();
            m_NameSystem ??= World.GetExistingSystemManaged<Game.UI.NameSystem>();
            if (m_PrefabSystem == null || m_NameSystem == null)
            {
                Mod.Log.Info("[普查] 游戏系统未就绪（PrefabSystem/NameSystem 拿不到），稍后重按");
                return;
            }

            var all = m_BuildingQuery.ToEntityArray(Allocator.Temp);
            var total = all.Length;
            var lines = 0;
            Mod.Log.Info($"[普查] ===== 开始：全城建筑 {total} 栋 ====="); lines++;

            // 全量类别分布（ClassifyBuilding 一处定义，CitizenPoolSystem）
            var dist = new Dictionary<string, int>();
            for (var i = 0; i < total; i++)
            {
                var kind = CitizenPoolSystem.ClassifyBuilding(EntityManager, all[i]) ?? "未分类";
                dist[kind] = dist.TryGetValue(kind, out var c) ? c + 1 : 1;
            }
            var distSb = new StringBuilder("[普查] 类别分布：");
            foreach (var kv in dist)
                distSb.Append($" {kv.Key}={kv.Value}");
            Mod.Log.Info(distSb.ToString()); lines++;

            // 跨步抽样 ≤64 栋（CitizenPoolSystem 同款）
            var stride = math.max(1, total / k_MaxCensus);
            var sampled = 0;
            for (var i = 0; i < total && sampled < k_MaxCensus; i += stride, sampled++)
                lines += DumpBuilding(all[i], sampled + 1);

            lines += DumpJournalPrefabs();
            Mod.Log.Info($"[普查] ===== 结束：抽样 {sampled}/{total} 栋，本轮合计 {lines} 行 =====");
            all.Dispose();
        }

        /// <summary>单栋建筑一行 + 租户行，返回打了几行。</summary>
        private int DumpBuilding(Entity building, int seq)
        {
            var lines = 0;
            var kind = CitizenPoolSystem.ClassifyBuilding(EntityManager, building) ?? "未分类";
            var grown = EnvironmentDigestSystem.IsZoneGrown(EntityManager, building);

            // prefab 侧：内部名 + BuildingData（m_LotSize/m_Flags，只在 prefab 实体上）+ Localization 挂接（资产层组件）
            var prefab = EntityManager.HasComponent<PrefabRef>(building)
                ? EntityManager.GetComponentData<PrefabRef>(building).m_Prefab
                : Entity.Null;
            var prefabName = prefab != Entity.Null ? m_PrefabSystem!.GetPrefabName(prefab) : "n/a";
            string lot = "n/a", prefabFlags = "n/a", loc = "n/a";
            if (prefab != Entity.Null)
            {
                if (EntityManager.HasComponent<BuildingData>(prefab))
                {
                    var bd = EntityManager.GetComponentData<BuildingData>(prefab);
                    lot = $"{bd.m_LotSize.x}×{bd.m_LotSize.y}";
                    prefabFlags = bd.m_Flags.ToString(); // Game.Prefabs.BuildingFlags（uint，地块/通行类）
                }
                // Localization 是 ComponentBase 资产组件（GetPrefabComponents 空挂，prefab 实体上查不到），
                // 必须经 PrefabSystem 拿 PrefabBase 再 TryGet——资产层挂接面正是本次要收口的 ①
                if (m_PrefabSystem!.TryGetPrefab(prefab, out PrefabBase pb)
                    && pb.TryGet<Localization>(out var l))
                    loc = l.m_LocalizationID ?? "（挂但ID空）";
                else
                    loc = "无";
            }

            // 实体侧 Building.m_Flags（Game.Buildings.BuildingFlags，byte，状态类）——与 prefab 侧同名不同义，都打印
            var entityFlags = EntityManager.GetComponentData<Game.Buildings.Building>(building).m_Flags.ToString();

            var rendered = m_NameSystem!.GetRenderedLabelName(building);
            if (EnvironmentDigestSystem.IsUglyName(rendered))
                rendered += "（脏键）";
            var address = EnvironmentDigestSystem.TryGetAddressLabel(EntityManager, m_NameSystem, building, out var label)
                ? label : "n/a";

            Mod.Log.Info($"[普查] #{seq} {building.Index}:{building.Version} 类别={kind} 自长={(grown ? "是" : "否")} " +
                         $"prefab={prefabName} 地块={lot} prefab侧Flags={prefabFlags} 实体侧Flags={entityFlags} " +
                         $"loc={loc} 渲染名={rendered} 地址={address}");
            lines++;

            // 租户（≤4）：公司/住户判别 = CompanyData/Household HasComponent
            if (!EntityManager.HasBuffer<Game.Buildings.Renter>(building))
                return lines;
            var renters = EntityManager.GetBuffer<Game.Buildings.Renter>(building, isReadOnly: true);
            var n = math.min(renters.Length, k_MaxRenters);
            for (var j = 0; j < n; j++)
            {
                DumpRenter(renters[j].m_Renter, j + 1);
                lines++;
            }
            if (renters.Length > k_MaxRenters)
            {
                Mod.Log.Info($"[普查]   …另有 {renters.Length - k_MaxRenters} 个租户未打");
                lines++;
            }
            return lines;
        }

        private void DumpRenter(Entity renter, int seq)
        {
            if (EntityManager.HasComponent<Game.Companies.CompanyData>(renter))
            {
                var name = m_NameSystem!.GetRenderedLabelName(renter);
                var companyPrefab = EntityManager.HasComponent<PrefabRef>(renter)
                    ? EntityManager.GetComponentData<PrefabRef>(renter).m_Prefab
                    : Entity.Null;
                var cpName = companyPrefab != Entity.Null ? m_PrefabSystem!.GetPrefabName(companyPrefab) : "n/a";
                // 品牌对照（收口④：GetRenderedLabelName(company) 是否=品牌名）
                var brand = EntityManager.GetComponentData<Game.Companies.CompanyData>(renter).m_Brand;
                var brandName = brand != Entity.Null ? m_NameSystem.GetRenderedLabelName(brand) : "n/a";

                if (companyPrefab != Entity.Null && EntityManager.HasComponent<IndustrialProcessData>(companyPrefab))
                {
                    // 加工/工业公司：产出在 prefab 的 IndustrialProcessData（运行时公司实体没有）
                    var p = EntityManager.GetComponentData<IndustrialProcessData>(companyPrefab);
                    Mod.Log.Info($"[普查]   租户{seq} 公司 {renter.Index}:{renter.Version} 名={name} prefab={cpName} 品牌={brandName} " +
                                 $"投入={Stack(p.m_Input1)}+{Stack(p.m_Input2)} 产出={Stack(p.m_Output)} import={p.m_IsImport}");
                }
                else if (companyPrefab != Entity.Null)
                {
                    // 无 IndustrialProcessData：打标记组件有无（收口③：开采/服务公司产出读法未实锤）
                    var extractor = EntityManager.HasComponent<ExtractorCompanyData>(companyPrefab); // Game.Prefabs 空标记
                    var service = EntityManager.HasComponent<Game.Companies.ServiceCompanyData>(companyPrefab);
                    var commercial = EntityManager.HasComponent<CommercialCompanyData>(companyPrefab); // Game.Prefabs 空标记
                    Mod.Log.Info($"[普查]   租户{seq} 公司 {renter.Index}:{renter.Version} 名={name} prefab={cpName} 品牌={brandName} " +
                                 $"无IndustrialProcessData extractor={(extractor ? "有" : "无")} service={(service ? "有" : "无")} commercial={(commercial ? "有" : "无")}");
                }
                else
                {
                    Mod.Log.Info($"[普查]   租户{seq} 公司 {renter.Index}:{renter.Version} 名={name} prefab=n/a 品牌={brandName}");
                }
            }
            else if (EntityManager.HasComponent<Game.Citizens.Household>(renter))
            {
                // 住户：渲染名=住户姓验证（收口②）
                Mod.Log.Info($"[普查]   租户{seq} 住户 {renter.Index}:{renter.Version} 住户姓={m_NameSystem!.GetRenderedLabelName(renter)}");
            }
            else
            {
                Mod.Log.Info($"[普查]   租户{seq} 其他 {renter.Index}:{renter.Version} 名={m_NameSystem!.GetRenderedLabelName(renter)}");
            }
        }

        private static string Stack(ResourceStack s) => $"{s.m_Resource}×{s.m_Amount}";

        /// <summary>可入刊事件 prefab 清单（EventJournalSystem.eventPrefabs 公开枚举，收口⑦）。拿不到记一行原因。</summary>
        private int DumpJournalPrefabs()
        {
            m_JournalSystem ??= World.GetExistingSystemManaged<Game.Events.EventJournalSystem>();
            if (m_JournalSystem == null)
            {
                Mod.Log.Info("[普查] 可入刊事件 prefab：EventJournalSystem 拿不到（游戏未就绪）");
                return 1;
            }
            try
            {
                var sb = new StringBuilder();
                var n = 0;
                foreach (var jc in m_JournalSystem.eventPrefabs)
                {
                    if (n > 0)
                        sb.Append('、');
                    sb.Append($"{jc.name}(data=0x{jc.GetDataFlags():X},effect=0x{jc.GetEffectFlags():X})");
                    n++;
                }
                Mod.Log.Info($"[普查] 可入刊事件 prefab 共 {n}：{sb}");
                return 1;
            }
            catch (System.Exception e)
            {
                Mod.Log.Info($"[普查] 可入刊事件 prefab 枚举失败：{e.GetType().Name} {e.Message}");
                return 1;
            }
        }

        // ==================================================================
        // 热键B：事件探针
        // ==================================================================

        private void ToggleProbe()
        {
            m_ProbeOn = !m_ProbeOn;
            Mod.Log.Info(m_ProbeOn
                ? "[事件探针] 开（每帧查 Building/Road/Tree+Created 与 Building+Deleted，Ctrl+7 再拨关闭）"
                : "[事件探针] 关");
        }

        private void ProbeFrame()
        {
            ProbeCreatedBuildings();
            ProbeSimple(m_RoadCreatedQuery, "新路", showOwner: false);
            ProbeSimple(m_TreeCreatedQuery, "新树", showOwner: true); // Owner 有无=玩家种的判别候选（收口⑥，推断待实机）
            ProbeDeletedBuildings();
        }

        private void ProbeCreatedBuildings()
        {
            var arr = m_BuildingCreatedQuery.ToEntityArray(Allocator.Temp);
            if (arr.Length == 0) { arr.Dispose(); return; }
            if (arr.Length > k_MergeThreshold)
            {
                Mod.Log.Info($"[事件探针] 新建建筑 本帧 ×{arr.Length}（>{k_MergeThreshold} 合并）");
                arr.Dispose();
                return;
            }
            foreach (var e in arr)
            {
                // 自长=是 → 分区自长（游戏系统生成）；否 → 玩家手放/签名建筑（收口⑤：自长是否带 Created）
                var grown = EnvironmentDigestSystem.IsZoneGrown(EntityManager, e);
                Mod.Log.Info($"[事件探针] 新建建筑 {e.Index}:{e.Version} prefab={PrefabNameOf(e)} 自长={(grown ? "是" : "否")}");
            }
            arr.Dispose();
        }

        private void ProbeSimple(EntityQuery query, string label, bool showOwner)
        {
            var arr = query.ToEntityArray(Allocator.Temp);
            if (arr.Length == 0) { arr.Dispose(); return; }
            if (arr.Length > k_MergeThreshold)
            {
                Mod.Log.Info($"[事件探针] {label} 本帧 ×{arr.Length}（>{k_MergeThreshold} 合并）");
                arr.Dispose();
                return;
            }
            foreach (var e in arr)
            {
                var owner = showOwner ? $" 有Owner={(EntityManager.HasComponent<Owner>(e) ? "是" : "否")}" : "";
                Mod.Log.Info($"[事件探针] {label} {e.Index}:{e.Version} prefab={PrefabNameOf(e)}{owner}");
            }
            arr.Dispose();
        }

        private void ProbeDeletedBuildings()
        {
            var arr = m_BuildingDeletedQuery.ToEntityArray(Allocator.Temp);
            if (arr.Length == 0) { arr.Dispose(); return; }
            if (arr.Length > k_MergeThreshold)
            {
                Mod.Log.Info($"[事件探针] 拆建筑 本帧 ×{arr.Length}（>{k_MergeThreshold} 合并）");
                arr.Dispose();
                return;
            }
            foreach (var e in arr)
            {
                // Deleted 当帧组件全在（CleanUpSystem 帧末才 DestroyEntity），prefab 名/自长照常可读
                var grown = EnvironmentDigestSystem.IsZoneGrown(EntityManager, e);
                Mod.Log.Info($"[事件探针] 拆建筑 {e.Index}:{e.Version} prefab={PrefabNameOf(e)} 自长={(grown ? "是" : "否")}");
            }
            arr.Dispose();
        }

        /// <summary>prefab 内部名兜底链：无 PrefabRef/PrefabSystem 未就绪 → n/a。</summary>
        private string PrefabNameOf(Entity e)
        {
            m_PrefabSystem ??= World.GetExistingSystemManaged<PrefabSystem>();
            if (m_PrefabSystem == null || !EntityManager.HasComponent<PrefabRef>(e))
                return "n/a";
            return m_PrefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab);
        }
    }
}
