using Game;
using Game.Citizens;
using Game.Simulation;
using Unity.Entities;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 话题雷达（读侧）：每 512 帧把城市聚合状态蒸馏成 CitySnapshot，供内容引擎消费，只读不写。
    /// 版本敏感点（元数据 spike 2026-08-19 已核实）：
    /// CountHouseholdDataSystem 的聚合属性名、ClimateSystem 的 isRaining/isSnowing/temperature.value。
    /// </summary>
    public partial class TopicRadarSystem : GameSystemBase
    {
        private EntityQuery m_CitizenQuery = default!;
        private EntityQuery m_HouseholdQuery = default!;
        private EntityQuery m_TimeDataQuery = default!;
        private EntityQuery m_TimeSettingsQuery = default!;
        private CountHouseholdDataSystem m_HouseholdData = default!;
        private ClimateSystem m_Climate = default!;
        private TimeSystem m_TimeSystem = default!;
        private SimulationSystem m_SimulationSystem = default!;

        /// <summary>最近一次采样快照。主线程专用；内容引擎只读，不写。</summary>
        public Content.CitySnapshot Latest { get; private set; }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CitizenQuery = GetEntityQuery(ComponentType.ReadOnly<Citizen>());
            m_HouseholdQuery = GetEntityQuery(ComponentType.ReadOnly<Household>());
            m_HouseholdData = World.GetOrCreateSystemManaged<CountHouseholdDataSystem>();
            m_Climate = World.GetOrCreateSystemManaged<ClimateSystem>();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            // 单例走 EntityQuery.GetSingleton——SystemAPI 依赖源码生成器，我们的构建不跑（2026-08-20 实锤）
            m_TimeDataQuery = GetEntityQuery(ComponentType.ReadOnly<Game.Common.TimeData>());
            m_TimeSettingsQuery = GetEntityQuery(ComponentType.ReadOnly<Game.Prefabs.TimeSettingsData>());
            RequireForUpdate(m_CitizenQuery);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 512;

        private uint m_Cycle;   // 采样周期计数（校准日志低频用）

        protected override void OnUpdate()
        {
            var settings = m_TimeSettingsQuery.GetSingleton<Game.Prefabs.TimeSettingsData>();
            var data = m_TimeDataQuery.GetSingleton<Game.Common.TimeData>();
            var tod = m_TimeSystem.GetTimeOfDay(settings, data, m_SimulationSystem.frameIndex); // 0..1

            var citizens = m_CitizenQuery.CalculateEntityCount();
            var workable = m_HouseholdData.WorkableCitizenCount;
            var workers = m_HouseholdData.CityWorkerCount;

            Latest = new Content.CitySnapshot
            {
                Citizens = citizens,
                Households = m_HouseholdQuery.CalculateEntityCount(),
                Tourists = m_HouseholdData.TouristCitizenCount,
                Happiness = m_HouseholdData.AverageCitizenHappiness,
                UnemploymentRate = m_HouseholdData.UnemploymentRate,
                HomelessnessRate = m_HouseholdData.HomelessnessRate,
                // 失业率/无家可归率：计数直算（2026-08-20 实机冤案：raw=1.2（已是百分数）被启发式
                // 当成分数×100→120%，全城"失业率高"——启发式已废，改计数直算量纲实锤）
                UnemploymentPercent = workable > 0 ? System.Math.Max(0f, (workable - workers) * 100f / workable) : 0f,
                HomelessPercent = citizens > 0 ? m_HouseholdData.HomelessCitizenCount * 100f / citizens : 0f,
                IsRaining = m_Climate.isRaining,
                IsSnowing = m_Climate.isSnowing,
                Temperature = m_Climate.temperature.value,
                SeasonName = m_Climate.currentSeasonName,
                HourOfDay = System.Math.Clamp((int)(tod * 24f), 0, 23),
            };

            // 校准日志：raw vs 计数直算（低频；与游戏 UI 对账一致后可视情况删减）
            if (m_Cycle++ % 32 == 0)
                Mod.Log.Info($"[Radar·校准] 失业率 raw={m_HouseholdData.UnemploymentRate:F2} → 计数直算 {Latest.UnemploymentPercent:F1}%（劳动人口 {workable} 就业 {workers}）；无家可归 raw={m_HouseholdData.HomelessnessRate:F2} → {Latest.HomelessPercent:F1}%");
        }
    }
}
