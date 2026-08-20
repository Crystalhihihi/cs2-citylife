using Game.Economy;
using Game.Prefabs;
using Unity.Entities;

namespace CityLife.GameBridge
{
    /// <summary>
    /// "店卖什么"的正确读法（2026-08-20 R3.1 实锤后立）：<b>产出声明</b>
    /// （IndustrialProcessData.m_Output，公司实体 → prefab 实体两级查），不从存货猜——
    /// 存货最多 ≠ 卖什么：投入品混入（餐厅 Food 是原料、Meals 才是商品，
    /// 实机观测 Food -29 与 Meals +29 锁步 1:1 生产转化，锚点"全是吃的"同源）。
    /// ResourceStack 字段 { m_Resource, m_Amount } dump 实锤。
    /// </summary>
    public static class ShopOutput
    {
        /// <summary>店的商品（产出资源）。拿不到返回 NoResource——调用方自行决定兜底。</summary>
        public static Resource OutputOf(EntityManager em, Entity company)
        {
            // 一级：公司实体自身带加工数据
            if (em.HasComponent<IndustrialProcessData>(company))
            {
                var o = em.GetComponentData<IndustrialProcessData>(company).m_Output.m_Resource;
                if (o != Resource.NoResource)
                    return o;
            }
            // 二级：公司 prefab 实体上的加工数据声明
            if (em.HasComponent<PrefabRef>(company))
            {
                var prefabEntity = em.GetComponentData<PrefabRef>(company).m_Prefab;
                if (prefabEntity != Entity.Null && em.HasComponent<IndustrialProcessData>(prefabEntity))
                {
                    var o = em.GetComponentData<IndustrialProcessData>(prefabEntity).m_Output.m_Resource;
                    if (o != Resource.NoResource)
                        return o;
                }
            }
            return Resource.NoResource;
        }
    }
}
