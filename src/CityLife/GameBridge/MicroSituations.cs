using Unity.Entities;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 微处境签词表（§12 #72 二批⑥，执行层牌堆）：住宅与"游戏读不到状态的楼"的细化处境——
    /// 与 SceneWords 场景词<b>并列</b>（SceneWords 表本体一行不动，玩家预留粒度亲自审；
    /// 微处境是"手头正在干的事"，不是场所分类）。组卡时按（炉计数+卡序）确定性发牌缀卡"｜微：X"。
    /// 口径：住宅（无业态但有处境——#62 定案住宅无场景词，微处境是它的活）=做饭/哄娃/看电视/辅导作业/
    /// 吵架/躺平；医院=候诊/缴费/拿药/等报告；商店=排队结账/比价/找东西；中学（玩家示范加牌）=
    /// 晚自习/放学点/快递站几点关门/查岗（学段判定复用 SceneWords 的 EducationLevel 读法，
    /// 非"中学"不发——小学/大学不硬套）。
    /// 如何扩展：加牌=往对应数组加词；加楼型=Pick 里加一行 ClassifyBuilding 类词→牌堆映射。
    /// </summary>
    public static class MicroSituations
    {
        /// <summary>住宅微处境（无业态但有处境——玩家示范口径）。</summary>
        private static readonly string[] k_Home = { "做饭", "哄娃", "看电视", "辅导作业", "吵架", "躺平" };

        /// <summary>医院微处境（读不到状态的楼代表）。</summary>
        private static readonly string[] k_Hospital = { "候诊", "缴费", "拿药", "等报告" };

        /// <summary>商店微处境。</summary>
        private static readonly string[] k_Shop = { "排队结账", "比价", "找东西" };

        /// <summary>中学加牌（玩家示范：晚自习/放学点/快递站几点关门/查岗）。</summary>
        private static readonly string[] k_MidSchool = { "晚自习", "放学点", "快递站几点关门", "查岗" };

        /// <summary>建筑 → 微处境词（住宅/医院/商店/中学；其余楼型与读不到位置 → null 不缀签）。
        /// salt=炉计数+卡序锚定（同炉次同卡序必同词，可复现）。组炉级低频调用，不进热路径。</summary>
        public static string? Pick(EntityManager em, Entity building, uint salt, int cardIdx)
        {
            if (building == Entity.Null || !em.HasComponent<Game.Buildings.Building>(building))
                return null;
            string[]? pool = null;
            var kind = CitizenPoolSystem.ClassifyBuilding(em, building);
            if (kind == "住宅区") pool = k_Home;
            else if (kind == "医院") pool = k_Hospital;
            else if (kind == "商店") pool = k_Shop;
            else if (kind == "学校")
            {
                // 学段细分复用 SceneWords（其 EducationLevel 读法一处定义）；玩家示范只给"中学"加牌
                var row = SceneWords.Of(em, building);
                if (row != null && row.Word == "中学")
                    pool = k_MidSchool;
            }
            if (pool == null || pool.Length == 0)
                return null;
            return pool[(int)(salt % (uint)pool.Length)];
        }
    }
}
