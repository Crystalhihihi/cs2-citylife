using Game;
using Game.Citizens;
using Game.Simulation;
using Unity.Entities;

namespace CityLife.GameBridge
{
    /// <summary>
    /// M0 读侧探针：周期性统计市民/家庭实体数量与城市聚合指标并打 log，
    /// 验证"加 System 不改游戏"的读取链路。聚合指标来源：
    /// Game.Simulation.CountHouseholdDataSystem（30+ 只读属性，元数据 spike 报告 §6 已核实）。
    ///
    /// 读侧纪律（全项目适用，新增读取系统前必读）：
    /// 1. EntityQuery 一律在 OnCreate 缓存，禁止在 OnUpdate 里临时构造；
    /// 2. 降频错峰：正式版按 frameIndex % 128 + 每系统 offset 摊薄，本探针先用简单计数器；
    /// 3. 只读访问用 ComponentType.ReadOnly；RequireForUpdate 让主菜单等无数据场景自动停更；
    /// 4. 人口级遍历必须 Burst + IJobChunk（范本 InfoLoom）；本探针只 Count 不遍历，零负担；
    /// 5. 读游戏系统聚合数据：直接 GetOrCreateSystemManaged 取系统引用读属性（范本 CS2MCP），
    ///    job 线程读者才需要 AddHouseholdDataReader 登记依赖，主线程直读即可。
    /// </summary>
    public partial class ReadProbeSystem : GameSystemBase
    {
        private EntityQuery m_CitizenQuery = default!;
        private EntityQuery m_HouseholdQuery = default!;
        private CountHouseholdDataSystem m_HouseholdData = default!;
        private uint m_Frame;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CitizenQuery = GetEntityQuery(ComponentType.ReadOnly<Citizen>());
            m_HouseholdQuery = GetEntityQuery(ComponentType.ReadOnly<Household>());
            m_HouseholdData = World.GetOrCreateSystemManaged<CountHouseholdDataSystem>();
            // 主菜单/编辑器场景没有市民实体，系统随之停更，不产生噪音日志
            RequireForUpdate(m_CitizenQuery);
        }

        protected override void OnUpdate()
        {
            // 每 512 个模拟帧打一次，避免刷爆日志
            if (m_Frame++ % 512 != 0)
                return;

            Mod.Log.Info(
                $"[ReadProbe] 市民={m_CitizenQuery.CalculateEntityCount()}, 家庭={m_HouseholdQuery.CalculateEntityCount()}, " +
                $"游客={m_HouseholdData.TouristCitizenCount}, 幸福度={m_HouseholdData.AverageCitizenHappiness}, " +
                $"失业率={m_HouseholdData.UnemploymentRate:F2}, 无家可归率={m_HouseholdData.HomelessnessRate:F2}");
        }
    }
}
