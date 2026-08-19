using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Pathfind;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CityLife.GameBridge
{
    /// <summary>
    /// M0 spike ④：TripNeeded 注入压测（验证完成后拆除，正式版重写为带上限的原语执行器）。
    ///
    /// 验证目标（设计文档 §9 M0）：
    /// 1. 翘班抢占——按 Ctrl+K：对正在上班/通勤的市民注入 priority=128 的 Leisure 行程，
    ///    看普查里 Leisure 是否站住、GoingToWork/Working 是否下跌（Time2Work 刻意回避的区域，无人验证过）；
    /// 2. 大规模寻路——按 Ctrl+L：尽量多市民（不限是否上班）注入 priority=255，看帧率与日志节奏是否崩。
    ///
    /// 注入写法依据 Time2Work SocialTripSystem.cs:309-379（社区验证过的模式）：
    /// buffer 挂市民本人；只填 m_TargetAgent/m_Purpose/m_Priority 三个字段（m_Data/m_Resource 留默认）；
    /// 配套三件套缺一不可：设 Target、改写 TravelPurpose、移除 Leisure/PathInformation/PathElement。
    ///
    /// spike 纪律：
    /// - 每帧最多注入 50 个，分批错峰（Time2Work "凌晨 2 点行程洪峰"教训）；
    /// - 务必用测试存档：抢占实验故意拽走上班市民，最坏情况数十人行为异常；
    /// - GetUpdateInterval=1 仅为捕获键盘输入；正式系统一律降频错峰（设计文档 §4 M1 纪律）；
    /// - 按键教训：F9 是原版"读取存档"快捷键（实测撞车），spike 键一律用 Ctrl+字母组合；
    ///   正式版触发走事件包/设置页按钮，不用裸键盘。
    /// </summary>
    public partial class InjectionSpikeSystem : GameSystemBase
    {
        private EntityQuery m_WorkerQuery = default!;
        private EntityQuery m_AllCitizenQuery = default!;
        private EntityQuery m_PurposeQuery = default!;
        private EntityQuery m_TargetBuildingQuery = default!;

        private int m_PendingWave;      // 本波剩余待注入数
        private byte m_WavePriority;    // 本波优先级（Ctrl+K=128，Ctrl+L=255）
        private bool m_WorkersOnly;     // true=只抓正在上班的（抢占实验）；false=全体市民（压测）
        private int m_InjectedTotal;    // 本波累计已注入
        private uint m_Frame;
        private uint m_LastKeyFrame;    // 上次触发帧，防抖用

        private const int kBatchPerFrame = 50;

        protected override void OnCreate()
        {
            base.OnCreate();
            // 抢占实验对象：有工作、当前带出行目的的市民
            m_WorkerQuery = GetEntityQuery(
                ComponentType.ReadOnly<Citizen>(),
                ComponentType.ReadOnly<Worker>(),
                ComponentType.ReadOnly<TravelPurpose>(),
                ComponentType.ReadWrite<TripNeeded>(),
                ComponentType.Exclude<HealthProblem>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
            // 压测对象：全体市民（不限是否在上班/有无出行目的）
            m_AllCitizenQuery = GetEntityQuery(
                ComponentType.ReadOnly<Citizen>(),
                ComponentType.ReadWrite<TripNeeded>(),
                ComponentType.Exclude<HealthProblem>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
            // 普查对象：全体带出行目的的市民
            m_PurposeQuery = GetEntityQuery(
                ComponentType.ReadOnly<TravelPurpose>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
            // 聚集目标：带吸引力组件的建筑（公园等）
            m_TargetBuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<AttractivenessProvider>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
            RequireForUpdate(m_WorkerQuery);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 1;

        protected override void OnUpdate()
        {
            // Ctrl+K 小波抢占实验 / Ctrl+L 大波压测；30 帧防抖（按住或单键多帧登记只触发一次）
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool debounced = m_Frame - m_LastKeyFrame > 30;
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.K)) { m_LastKeyFrame = m_Frame; StartWave(100, 128, true); }
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.L)) { m_LastKeyFrame = m_Frame; StartWave(2000, 255, false); }

            if (m_PendingWave > 0)
                InjectBatch();

            if (m_Frame++ % 512 == 0)
                LogPurposeCensus("定时");
        }

        private void StartWave(int count, byte priority, bool workersOnly)
        {
            if (m_TargetBuildingQuery.IsEmptyIgnoreFilter)
            {
                Mod.Log.Warn("[InjectSpike] 城里没有带吸引力的建筑（公园等），先放一个再按");
                return;
            }
            m_PendingWave = count;
            m_WavePriority = priority;
            m_WorkersOnly = workersOnly;
            m_InjectedTotal = 0;
            Mod.Log.Info($"[InjectSpike] 开波：目标 {count} 人，priority={priority}，定向={(workersOnly ? "正在上班的市民" : "全体市民")}");
            LogPurposeCensus("开波前");
        }

        private void InjectBatch()
        {
            var targets = m_TargetBuildingQuery.ToEntityArray(Allocator.Temp);
            var target = targets[0];
            var candidates = (m_WorkersOnly ? m_WorkerQuery : m_AllCitizenQuery).ToEntityArray(Allocator.Temp);
            int budget = math.min(m_PendingWave, kBatchPerFrame);
            int injected = 0;

            foreach (var citizen in candidates)
            {
                if (injected >= budget) break;

                if (m_WorkersOnly)
                {
                    var tp = EntityManager.GetComponentData<TravelPurpose>(citizen);
                    // 只对正在上班/去上班的市民下手——抢占实验的核心样本
                    if (tp.m_Purpose != Purpose.GoingToWork && tp.m_Purpose != Purpose.Working)
                        continue;
                }

                var trips = EntityManager.GetBuffer<TripNeeded>(citizen);
                // 去重：同目标+同目的已存在则跳过（Time2Work 同款）
                bool dup = false;
                for (int i = 0; i < trips.Length; i++)
                {
                    if (trips[i].m_TargetAgent == target && trips[i].m_Purpose == Purpose.Leisure)
                    {
                        dup = true;
                        break;
                    }
                }
                if (dup) continue;

                trips.Add(new TripNeeded
                {
                    m_TargetAgent = target,
                    m_Purpose = Purpose.Leisure,
                    m_Priority = m_WavePriority,
                });

                // 配套三件套：设 Target / 改写 TravelPurpose（有则改、无则加）/ 撕掉当前路径
                if (EntityManager.HasComponent<Target>(citizen))
                    EntityManager.SetComponentData(citizen, new Target { m_Target = target });
                else
                    EntityManager.AddComponentData(citizen, new Target { m_Target = target });

                var leisurePurpose = new TravelPurpose { m_Purpose = Purpose.Leisure };
                if (EntityManager.HasComponent<TravelPurpose>(citizen))
                    EntityManager.SetComponentData(citizen, leisurePurpose);
                else
                    EntityManager.AddComponentData(citizen, leisurePurpose);

                EntityManager.RemoveComponent<Leisure>(citizen);
                EntityManager.RemoveComponent<PathInformation>(citizen);
                EntityManager.RemoveComponent<PathElement>(citizen);

                injected++;
            }

            targets.Dispose();
            candidates.Dispose();

            m_PendingWave -= injected;
            m_InjectedTotal += injected;
            Mod.Log.Info($"[InjectSpike] 本批 +{injected}，累计 {m_InjectedTotal}，剩余 {m_PendingWave}（候选池 {candidates.Length}）");

            if (m_PendingWave <= 0 || injected == 0)
            {
                Mod.Log.Info($"[InjectSpike] 波次结束：共注入 {m_InjectedTotal} 人");
                m_PendingWave = 0;
                LogPurposeCensus("波次后");
            }
        }

        /// <summary>TravelPurpose 普查：抢占是否成立的直接证据（Leisure 是否站住、工作项是否下跌）。</summary>
        private void LogPurposeCensus(string tag)
        {
            var purposes = m_PurposeQuery.ToComponentDataArray<TravelPurpose>(Allocator.Temp);
            int goingWork = 0, working = 0, leisure = 0, other = 0;
            foreach (var p in purposes)
            {
                if (p.m_Purpose == Purpose.GoingToWork) goingWork++;
                else if (p.m_Purpose == Purpose.Working) working++;
                else if (p.m_Purpose == Purpose.Leisure) leisure++;
                else other++;
            }
            Mod.Log.Info($"[InjectSpike][普查:{tag}] GoingToWork={goingWork} Working={working} Leisure={leisure} 其他={other} 总数={purposes.Length}");
            purposes.Dispose();
        }
    }
}
