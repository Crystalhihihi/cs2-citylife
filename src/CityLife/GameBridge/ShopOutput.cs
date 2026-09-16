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
    ///
    /// §12 #62 业态细分（2026-09-16 落地）：<see cref="BusinessWordOf"/> 建筑级业态词——商业按租户销售资源、
    /// 工厂按产出资源、办公按 immaterial 四业态；处境卡/目的地/环境圈聚类的场所类别词都从这层升级，
    /// 住宅无业态回落粗类名（一处定义，别复制粘贴）。
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

        /// <summary>商业资源 → 中文业态词（未映射 → null，调用方回落粗类"商店"）。</summary>
        private static string? CommercialWordOf(Resource res)
        {
            foreach (var (r, word) in k_ResourceWords)
                if (res == r)
                    return word;
            return null;
        }

        /// <summary>资源 → 中文业态词（锚点标签/广告 prompt 共用；未覆盖回退"店"）。</summary>
        public static string WordOf(Resource res) => CommercialWordOf(res) ?? "店";

        // —— §12 #62 业态细分（2026-09-16 落地，字段口径全普查实锤 logs/census-spike-20260914：Resource 位标志表
        //    / Renters buffer / IndustrialProcessData.m_Output；映射表写死在执行层，LLM 只消费不分类——单向阀门）——

        // 工厂（工业楼按产出资源分业态）
        private static string? IndustrialWordOf(Resource res) => res switch
        {
            Resource.Meals or Resource.Food or Resource.ConvenienceFood or Resource.Vegetables
                or Resource.Grain or Resource.Livestock or Resource.Fish => "食品厂",
            Resource.Beverages => "饮料厂",
            Resource.Textiles or Resource.Cotton => "纺织厂",
            Resource.Furniture => "家具厂",
            Resource.Timber or Resource.Wood => "木材厂",
            Resource.Paper => "造纸厂",
            Resource.Vehicles => "汽车厂",
            Resource.Electronics => "电子厂",
            Resource.Pharmaceuticals => "药厂",
            Resource.Oil => "炼油厂",
            Resource.Petrochemicals or Resource.Chemicals or Resource.Plastics => "化工厂",
            Resource.Steel or Resource.Metals => "钢铁厂",
            Resource.Ore or Resource.Coal or Resource.Minerals or Resource.Stone => "矿场",
            Resource.Concrete => "建材厂",
            Resource.Machinery => "机械厂",
            Resource.Garbage => "垃圾处理厂",
            Resource.UnsortedMail or Resource.LocalMail or Resource.OutgoingMail => "邮件分拣中心",
            _ => null,
        };

        // 办公楼（immaterial 产出四业态）
        private static string? OfficeWordOf(Resource res) => res switch
        {
            Resource.Software => "软件公司",
            Resource.Financial => "金融公司",
            Resource.Telecom => "电信公司",
            Resource.Media => "传媒公司",
            _ => null,
        };

        /// <summary>建筑 → 业态词（§12 #62 业态细分）：商业楼按租户公司销售资源分（餐馆/便利店/服装店…，
        /// 复用 k_ResourceWords）、工厂按产出资源分（食品厂/电子厂/纺织厂…）、办公楼按 immaterial 产出分四业态
        /// （软件/金融/电信/传媒——判 office 用 EconomyUtils.IsOfficeResource 掩码直调游戏 API，与游戏同源不复制公式，
        /// 掩码=Software|Telecom|Financial|Media，dump 行级实锤勿复探）。
        /// 读法：建筑 Renters buffer（Game.Buildings.Renter）→ 租户公司 → OutputOf 产出声明两级查；
        /// 多租户取首个可解析资源（确定性）。非商/工/办楼、无租户、资源读不出/未映射 → null
        /// （调用方回落粗类名；住宅本来就无业态，§12 #62 定案）。</summary>
        public static string? BusinessWordOf(EntityManager em, Entity building)
        {
            if (!em.HasBuffer<Game.Buildings.Renter>(building))
                return null;
            var renters = em.GetBuffer<Game.Buildings.Renter>(building);
            for (int i = 0; i < renters.Length; i++)
            {
                var res = OutputOf(em, renters[i].m_Renter);
                if (res == Resource.NoResource)
                    continue;
                if (em.HasComponent<Game.Buildings.CommercialProperty>(building))
                    return CommercialWordOf(res);
                if (em.HasComponent<Game.Buildings.OfficeProperty>(building))
                    return EconomyUtils.IsOfficeResource(res) ? OfficeWordOf(res) : null;
                if (em.HasComponent<Game.Buildings.IndustrialProperty>(building))
                    return IndustrialWordOf(res);
                return null; // 住宅/公共建筑无业态
            }
            return null;
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
