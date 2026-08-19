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
    /// 如何扩展：加字段 = 这里加属性 + Load 加一行 + 消费处读属性。
    /// </summary>
    public static class ModSettings
    {
        public static bool T0Fallback { get; private set; } = false;
        public static string FeedMode { get; private set; } = "always";

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
                log($"[Settings] t0Fallback={T0Fallback} feedMode={FeedMode}");
            }
            catch (Exception e)
            {
                log("[Settings] 设置读取失败（全部默认）：" + e.Message);
            }
        }
    }
}
