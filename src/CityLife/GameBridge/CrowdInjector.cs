using Game.Citizens;
using Game.Common;
using Game.Pathfind;
using Unity.Collections;
using Unity.Entities;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 人群注入原语（写回层，侵入度 2 级）：定向 TripNeeded 注入。
    /// 写法来自 M0 实机压测（docs/spikes/2026-08-19-trip-injection.md）：
    /// buffer 挂市民本人、三件套（Target/TravelPurpose/撕路径）、分批错峰。
    /// 军规②纪律：调用方必须遵守并发上限+冷却+通勤高峰禁演——本类只管注入，不管时机。
    /// </summary>
    public static class CrowdInjector
    {
        /// <summary>
        /// 向场馆注入一批休闲行程。返回实际注入数。
        /// candidates 查询由调用方提供（必须含 TripNeeded RW + 排除 Temp/Deleted）。
        /// </summary>
        public static int InjectBatch(EntityManager em, EntityQuery candidates, Entity venue, int maxCount, byte priority)
        {
            if (venue == Entity.Null)
                return 0;

            var arr = candidates.ToEntityArray(Allocator.Temp);
            var injected = 0;
            try
            {
                foreach (var citizen in arr)
                {
                    if (injected >= maxCount)
                        break;

                    var trips = em.GetBuffer<TripNeeded>(citizen);
                    // 去重：同目标+同目的已存在则跳过（Time2Work 同款）
                    var dup = false;
                    for (int i = 0; i < trips.Length; i++)
                    {
                        if (trips[i].m_TargetAgent == venue && trips[i].m_Purpose == Purpose.Leisure)
                        {
                            dup = true;
                            break;
                        }
                    }
                    if (dup)
                        continue;

                    trips.Add(new TripNeeded
                    {
                        m_TargetAgent = venue,
                        m_Purpose = Purpose.Leisure,
                        m_Priority = priority,
                    });

                    // 配套三件套：设 Target / 改写 TravelPurpose（有则改无则加）/ 撕掉当前路径
                    var target = new Target { m_Target = venue };
                    if (em.HasComponent<Target>(citizen))
                        em.SetComponentData(citizen, target);
                    else
                        em.AddComponentData(citizen, target);

                    var purpose = new TravelPurpose { m_Purpose = Purpose.Leisure };
                    if (em.HasComponent<TravelPurpose>(citizen))
                        em.SetComponentData(citizen, purpose);
                    else
                        em.AddComponentData(citizen, purpose);

                    em.RemoveComponent<Leisure>(citizen);
                    em.RemoveComponent<PathInformation>(citizen);
                    em.RemoveComponent<PathElement>(citizen);

                    injected++;
                }
            }
            finally
            {
                arr.Dispose();
            }
            return injected;
        }
    }
}
