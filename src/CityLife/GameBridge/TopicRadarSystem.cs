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

        protected override void OnUpdate()
        {
            var settings = m_TimeSettingsQuery.GetSingleton<Game.Prefabs.TimeSettingsData>();
            var data = m_TimeDataQuery.GetSingleton<Game.Common.TimeData>();
            var tod = m_TimeSystem.GetTimeOfDay(settings, data, m_SimulationSystem.frameIndex); // 0..1

            Latest = new Content.CitySnapshot
            {
                Citizens = m_CitizenQuery.CalculateEntityCount(),
                Households = m_HouseholdQuery.CalculateEntityCount(),
                Tourists = m_HouseholdData.TouristCitizenCount,
                Happiness = m_HouseholdData.AverageCitizenHappiness,
                UnemploymentRate = m_HouseholdData.UnemploymentRate,
                HomelessnessRate = m_HouseholdData.HomelessnessRate,
                IsRaining = m_Climate.isRaining,
                IsSnowing = m_Climate.isSnowing,
                Temperature = m_Climate.temperature.value,
                SeasonName = m_Climate.currentSeasonName,
                HourOfDay = System.Math.Clamp((int)(tod * 24f), 0, 23),
            };
        }
    }
}
