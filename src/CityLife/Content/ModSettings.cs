using System;
using System.IO;
using CityLife.Util;

namespace CityLife.Content
{
    /// <summary>
    /// mod 设置（v2：游戏内设置页为玩家配置唯一主源，§12 #45）。
    /// 主源 = GameBridge.CityLifeSetting（选项→Mods→CityLife，Mod.Options 持有实例）；
    /// 本类只剩开发级旋钮（不进 UI 的冷却/调试字段），文件：游戏用户目录 ModsSettings/CityLife/settings.json，缺失=全部默认。
    ///
    /// 已迁移进设置页的键（feedMode/t0Fallback/writeBackTier/feedMaxItems）：
    /// 同名属性改从 Mod.Options 透传（null 回默认值），并在透传属性内做 enum→字符串映射，
    /// 消费处（"always"/"openOnly"/"throttled"、"mild"/"normal"/"crazy"）零改动。
    /// Load() 不再读取这四个键——settings.json 里残留的旧键被静默忽略。
    ///
    /// 剩余 JSON 字段（开发级旋钮）：
    /// - breakingNews（bool，默认 true）：突发事件报道总开关（快讯+热议；关=信息流不谈火灾车祸盗窃）。
    /// - breakingHotHours（int，默认 12，钳 1-72）：热议档闸门（游戏小时）——
    ///   每这么久最多一次"全城集中讨论某事件"；快讯帖不受此限（它有自己的分类限流）。
    ///   2026-08-20 玩家定案：盗窃/车祸是高频事件，没这道闸信息流会全变成讨论事件。
    /// - petitionEnabled（bool，默认 true）：民意层 v2 总开关（市民请愿→聚集市政厅）。
    /// - petitionCooldownH（int，默认 48，钳 6-240）：两次请愿的最小间隔（游戏小时）。
    /// - petitionWindowH（int，默认 6，钳 1-48）：诉求窗口期——市长在此期限内发帖算回应，超时聚集。
    /// - petitionScale（int，默认 150，钳 50-800）：聚集注入总量基数（再乘写回档位系数）。
    /// - petitionDebug（bool，默认 false）：测试开关——绕过情绪阈值（幸福城也能触发请愿）。
    /// 如何扩展：加玩家旋钮去 CityLifeSetting（见类头注释）；加开发级旋钮 = 这里加属性 + Load 加一行 + 消费处读属性。
    /// </summary>
    public static class ModSettings
    {
        /// <summary>T0 模板兜底（默认 false）——透传设置页；LLM 断粮时是否用模板帖填充。</summary>
        public static bool T0Fallback => Mod.Options?.T0Fallback ?? false;

        /// <summary>信息流生成节拍（默认 "always"）——透传设置页，enum→字符串映射保持消费处零改动。</summary>
        public static string FeedMode => Mod.Options == null ? "always" : Mod.Options.FeedMode switch
        {
            GameBridge.CityLifeSetting.FeedModeOption.OpenOnly => "openOnly",
            GameBridge.CityLifeSetting.FeedModeOption.Throttled => "throttled",
            _ => "always",
        };

        /// <summary>写回强度档（默认 "normal"）——透传设置页，enum→字符串映射。</summary>
        public static string WriteBackTier => Mod.Options == null ? "normal" : Mod.Options.WriteBackTier switch
        {
            GameBridge.CityLifeSetting.WriteBackTierOption.Mild => "mild",
            GameBridge.CityLifeSetting.WriteBackTierOption.Crazy => "crazy",
            _ => "normal",
        };

        /// <summary>信息流仓库上限（默认 100；超 500 的全量推送开始亏性能，钳 20-500）——透传设置页。</summary>
        public static int FeedMaxItems => Mod.Options?.FeedMaxItems ?? 100;

        /// <summary>气泡 AI 闲聊生成（默认开）——透传设置页。§12 #49：闲聊炉不随 feedMode 停（气泡不看面板也在显示）。</summary>
        public static bool BubbleChatterEnabled => Mod.Options?.BubbleChatterEnabled ?? true;

        /// <summary>气泡小剧场（默认开）——透传设置页。§12 #49：同闲聊炉口径，独立开关。</summary>
        public static bool BubbleTheaterEnabled => Mod.Options?.BubbleTheaterEnabled ?? true;

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
                // feedMode/t0Fallback/writeBackTier/feedMaxItems 已迁移游戏内设置页（§12 #45），这里不再读取
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
                // feedMode/t0Fallback/writeBackTier/feedMaxItems 四键已迁移游戏内设置页，不再入 log
                log($"[Settings] breakingNews={BreakingNews} breakingHotHours={BreakingHotHours} petition={PetitionEnabled}/{PetitionCooldownH}h/{PetitionWindowH}h/{PetitionScale}人/debug={PetitionDebug} shopAds={ShopAds}/{ShopAdCooldownH}h");
            }
            catch (Exception e)
            {
                log("[Settings] 设置读取失败（全部默认）：" + e.Message);
            }
        }
    }
}
