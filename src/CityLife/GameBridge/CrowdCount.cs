using Game.Citizens;
using Game.Common;
using Unity.Entities;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 到场人数实数（共享读侧助手）：人在场馆的（CurrentBuilding）+ 以场馆为休闲目标在路上的
    /// （TravelPurpose=Leisure 且 Target=场馆）。逻辑与 EventChainSystem.TickRunning 原循环一致，
    /// 抽出来供活动链与请愿聚集（PetitionSystem）共用——只读不写。
    /// </summary>
    public static class CrowdCount
    {
        /// <summary>实数到场。citizens 查询由调用方提供（Citizen + 排除 Temp/Deleted）。</summary>
        public static (int onSite, int onTheWay) At(EntityManager em, EntityQuery citizens, Entity venue)
        {
            int onSite = 0, onTheWay = 0;
            var arr = citizens.ToEntityArray(Unity.Collections.Allocator.Temp);
            foreach (var citizen in arr)
            {
                if (em.HasComponent<CurrentBuilding>(citizen)
                    && em.GetComponentData<CurrentBuilding>(citizen).m_CurrentBuilding == venue)
                {
                    onSite++;
                    continue;
                }
                if (em.HasComponent<TravelPurpose>(citizen)
                    && em.GetComponentData<TravelPurpose>(citizen).m_Purpose == Purpose.Leisure
                    && em.HasComponent<Target>(citizen)
                    && em.GetComponentData<Target>(citizen).m_Target == venue)
                {
                    onTheWay++;
                }
            }
            arr.Dispose();
            return (onSite, onTheWay);
        }
    }
}
