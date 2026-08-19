using Game.Buildings;
using Unity.Entities;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 吸引力原语（写回层）：临时提升场馆吸引力 + 到期恢复。
    /// 军规③（§12 #29）：参数写回必须自限时长——TryBoost 记录原值，Restore 必须被调用；
    /// 调用方（活动链系统）负责在结束/读档异常时兜底恢复。组件 AttractivenessProvider.m_Attractiveness
    /// 是公开 Int32 字段（2026-08-19 元数据实锤），写它=游戏原生寻路偏置自动生效（Time2Work 已验证）。
    /// </summary>
    public static class AttractionRamp
    {
        /// <summary>提升吸引力：original 出参保存原值（恢复用）。boost 是直接改写值（非增量），由调用方按档位算好。</summary>
        public static bool TryBoost(EntityManager em, Entity venue, int boostValue, out int original)
        {
            original = 0;
            if (venue == Entity.Null || !em.HasComponent<AttractivenessProvider>(venue))
                return false;
            var a = em.GetComponentData<AttractivenessProvider>(venue);
            original = a.m_Attractiveness;
            a.m_Attractiveness = boostValue;
            em.SetComponentData(venue, a);
            return true;
        }

        /// <summary>恢复原值。幂等：重复恢复无副作用。</summary>
        public static void Restore(EntityManager em, Entity venue, int original)
        {
            if (venue == Entity.Null || !em.HasComponent<AttractivenessProvider>(venue))
                return;
            var a = em.GetComponentData<AttractivenessProvider>(venue);
            a.m_Attractiveness = original;
            em.SetComponentData(venue, a);
        }
    }
}
