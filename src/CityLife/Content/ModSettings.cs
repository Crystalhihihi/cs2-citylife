using System;
using System.IO;
using CityLife.Util;

namespace CityLife.Content
{
    /// <summary>
    /// mod 设置（v1 读 JSON 文件；游戏内设置页 M6 替换读取源，字段结构不变）。
    /// 文件：游戏用户目录 ModsSettings/CityLife/settings.json。文件缺失=全部默认。
    /// 字段：
    /// - t0Fallback（bool，默认 false）：LLM 帖池断粮时是否用 T0 模板帖兜底。
    ///   2026-08-20 定案：**本 mod 本质是 AI mod，不为无 AI 玩家设计**——默认断粮即静默；
    ///   想开模板兜底的玩家在 settings.json 里显式写 true。
    /// - feedMode（string，默认 "always"）：生成节拍与面板开合的联动——
    ///   "always" 常开（城市自己在活着，打开面板时平台已运转）；
    ///   "openOnly" 仅限展开（收起即停生成，省 token，但打开会有一段冷场期）；
    ///   "throttled" 折中（收起时降频保温：释放 1/4 速、仅池空才补炉）。
    /// - writeBackTier（string，默认 "normal"）：写回强度档（§6 红线）——
    ///   "mild" 体验档（注入规模 ×0.5）/"normal" 正常档（×1.0）/"crazy" 疯狂档（×2.0，确认卡带警示）。
    /// - breakingNews（bool，默认 true）：突发事件报道总开关（快讯+热议；关=信息流不谈火灾车祸盗窃）。
    /// - breakingHotHours（int，默认 12，钳 1-72）：热议档闸门（游戏小时）——
    ///   每这么久最多一次"全城集中讨论某事件"；快讯帖不受此限（它有自己的分类限流）。
    ///   2026-08-20 玩家定案：盗窃/车祸是高频事件，没这道闸信息流会全变成讨论事件。
    /// - petitionEnabled（bool，默认 true）：民意层 v2 总开关（市民请愿→聚集市政厅）。
    /// - petitionCooldownH（int，默认 48，钳 6-240）：两次请愿的最小间隔（游戏小时）。
    /// - petitionWindowH（int，默认 6，钳 1-48）：诉求窗口期——市长在此期限内发帖算回应，超时聚集。
    /// - petitionScale（int，默认 150，钳 50-800）：聚集注入总量基数（再乘写回档位系数）。
    /// - petitionDebug（bool，默认 false）：测试开关——绕过情绪阈值（幸福城也能触发请愿）。
    /// 如何扩展：加字段 = 这里加属性 + Load 加一行 + 消费处读属性。
    /// </summary>
    public static class ModSettings
    {
        public static bool T0Fallback { get; private set; } = false;
        public static string FeedMode { get; private set; } = "always";
        public static string WriteBackTier { get; private set; } = "normal";
        /// <summary>信息流仓库上限（默认 100；超 500 的全量推送开始亏性能，钳 20-500）。</summary>
        public static int FeedMaxItems { get; private set; } = 100;
        /// <summary>突发事件报道总开关（默认开）。</summary>
        public static bool BreakingNews { get; private set; } = true;
        /// <summary>热议档闸门：两次"全城集中讨论"的最小间隔（游戏小时，默认 12，钳 1-72）。</summary>
        public static int BreakingHotHours { get; private set; } = 12;
        /// <summary>民意层总开关（默认开）：市民请愿 → 超时未回应聚集市政厅/地标。</summary>
        public static bool PetitionEnabled { get; private set; } = true;
        /// <summary>两次请愿的最小间隔（游戏小时，默认 48，钳 6-240）。</summary>
        public static int PetitionCooldownH { get; private set; } = 48;
        /// <summary>诉求窗口期（游戏小时，默认 6，钳 1-48）：市长期限内发帖算回应，超时聚集。</summary>
        public static int PetitionWindowH { get; private set; } = 6;
        /// <summary>聚集注入总量基数（默认 150，钳 50-800；再乘写回档位系数）。</summary>
        public static int PetitionScale { get; private set; } = 150;
        /// <summary>测试开关（默认关）：绕过情绪阈值触发请愿。</summary>
        public static bool PetitionDebug { get; private set; } = false;
        /// <summary>商家广告层总开关（默认开）：店铺广告帖+评论互动+打折氛围排队（纯舆情无经济效果）。</summary>
        public static bool ShopAds { get; private set; } = true;
        /// <summary>两次商家广告的最小间隔（游戏小时，默认 12，钳 2-72）。</summary>
        public static int ShopAdCooldownH { get; private set; } = 12;

        public static void Load(string cfgDir, Action<string> log)
        {
            try
            {
                var path = Path.Combine(cfgDir, "settings.json");
                if (!File.Exists(path))
                {
                    log("[Settings] 无设置文件，全部默认（" + path + "）");
                    return;
                }
                var json = File.ReadAllText(path);
                var t0 = JsonMini.GetRaw(json, "t0Fallback");
                if (t0 != null)
                    T0Fallback = t0 != "false";
                var fm = JsonMini.GetStr(json, "feedMode");
                if (fm == "always" || fm == "openOnly" || fm == "throttled")
                    FeedMode = fm;
                var wt = JsonMini.GetStr(json, "writeBackTier");
                if (wt == "mild" || wt == "normal" || wt == "crazy")
                    WriteBackTier = wt;
                var fm2 = JsonMini.GetInt(json, "feedMaxItems");
                if (fm2 != null)
                    FeedMaxItems = System.Math.Clamp(fm2.Value, 20, 500);
                var bn = JsonMini.GetRaw(json, "breakingNews");
                if (bn != null)
                    BreakingNews = bn != "false";
                var bh = JsonMini.GetInt(json, "breakingHotHours");
                if (bh != null)
                    BreakingHotHours = System.Math.Clamp(bh.Value, 1, 72);
                var pe = JsonMini.GetRaw(json, "petitionEnabled");
                if (pe != null)
                    PetitionEnabled = pe != "false";
                var pc = JsonMini.GetInt(json, "petitionCooldownH");
                if (pc != null)
                    PetitionCooldownH = System.Math.Clamp(pc.Value, 6, 240);
                var pw = JsonMini.GetInt(json, "petitionWindowH");
                if (pw != null)
                    PetitionWindowH = System.Math.Clamp(pw.Value, 1, 48);
                var ps = JsonMini.GetInt(json, "petitionScale");
                if (ps != null)
                    PetitionScale = System.Math.Clamp(ps.Value, 50, 800);
                var pd = JsonMini.GetRaw(json, "petitionDebug");
                if (pd != null)
                    PetitionDebug = pd == "true";
                var sa = JsonMini.GetRaw(json, "shopAds");
                if (sa != null)
                    ShopAds = sa != "false";
                var sc = JsonMini.GetInt(json, "shopAdCooldownH");
                if (sc != null)
                    ShopAdCooldownH = System.Math.Clamp(sc.Value, 2, 72);
                log($"[Settings] t0Fallback={T0Fallback} feedMode={FeedMode} writeBackTier={WriteBackTier} feedMaxItems={FeedMaxItems} breakingNews={BreakingNews} breakingHotHours={BreakingHotHours} petition={PetitionEnabled}/{PetitionCooldownH}h/{PetitionWindowH}h/{PetitionScale}人/debug={PetitionDebug} shopAds={ShopAds}/{ShopAdCooldownH}h");
            }
            catch (Exception e)
            {
                log("[Settings] 设置读取失败（全部默认）：" + e.Message);
            }
        }
    }
}
