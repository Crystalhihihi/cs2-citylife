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
        // 资源→中文业态映射（Game.Economy.Resource 枚举 2026-08-20 dump 实测；未覆盖的走兜底"店"）
        private static readonly (Resource Res, string Word)[] k_ResourceWords =
        {
            (Resource.Meals, "餐馆"), (Resource.ConvenienceFood, "便利店"), (Resource.Food, "食品店"),
            (Resource.Vegetables, "菜店"), (Resource.Beverages, "饮品店"), (Resource.Fish, "水产店"),
            (Resource.Textiles, "服装店"), (Resource.Furniture, "家具店"), (Resource.Vehicles, "车行"),
            (Resource.Electronics, "电子产品店"), (Resource.Pharmaceuticals, "药店"),
            (Resource.Lodging, "酒店"), (Resource.Paper, "文具店"), (Resource.Telecom, "手机店"),
            (Resource.Entertainment, "娱乐场所"), (Resource.Recreation, "休闲场所"),
            (Resource.Financial, "银行"), (Resource.Media, "传媒公司"), (Resource.Software, "软件公司"),
        };

        /// <summary>资源 → 中文业态词（锚点标签/广告 prompt 共用；未覆盖回退"店"）。</summary>
        public static string WordOf(Resource res)
        {
            foreach (var (r, word) in k_ResourceWords)
                if (res == r)
                    return word;
            return "店";
        }

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
