using System;
using System.IO;
using CityLife.Util;

namespace CityLife.Llm
{
    /// <summary>
    /// LLM 供给配置（v1 读本地 JSON 文件；游戏内设置页随 M6 替换读取源，本结构与选择分支不变）。
    /// 文件位置：游戏用户目录 ModsSettings/CityLife/llm.json——**在仓库外，API key 绝不进 git**。
    /// 字段：provider（"kimi-cli" 默认 / "openai-compatible"）、baseUrl、apiKey、model。
    /// 文件缺失或读取失败 → 回退 KimiCli 订阅轨，游戏照常（T0 模板兜底）。
    /// 示例：
    /// { "provider": "openai-compatible", "baseUrl": "https://api.siliconflow.cn/v1",
    ///   "apiKey": "sk-...", "model": "deepseek-ai/DeepSeek-R1" }
    /// 如何扩展：新供给类型在 Mod.OnLoad 的选择分支加一行，并实现 ICliProvider（见该文件顶部指南）。
    /// </summary>
    public sealed class ProviderConfig
    {
        public string Provider = "kimi-cli";
        public string BaseUrl = "";
        public string ApiKey = "";
        public string Model = "";
        public string Thinking = ""; // "disabled" = 关闭深度思考（DeepSeek V4 系支持；省 token 大头）

        public static ProviderConfig Load(string path, Action<string> log)
        {
            var cfg = new ProviderConfig();
            try
            {
                if (!File.Exists(path))
                {
                    log("[LLM] 无供给配置文件，走默认 KimiCli（配置路径：" + path + "）");
                    return cfg;
                }

                var json = File.ReadAllText(path);
                cfg.Provider = JsonMini.GetStr(json, "provider") ?? cfg.Provider;
                cfg.BaseUrl = JsonMini.GetStr(json, "baseUrl") ?? "";
                cfg.ApiKey = JsonMini.GetStr(json, "apiKey") ?? "";
                cfg.Model = JsonMini.GetStr(json, "model") ?? "";
                cfg.Thinking = JsonMini.GetStr(json, "thinking") ?? "";
                // 注意：key 永远不进日志
                log("[LLM] 供给配置：" + cfg.Provider +
                    (cfg.Provider == "openai-compatible" ? $" {cfg.BaseUrl} 模型={cfg.Model}" : ""));
                return cfg;
            }
            catch (Exception e)
            {
                log("[LLM] 供给配置读取失败（回退 KimiCli）：" + e.Message);
                return new ProviderConfig();
            }
        }
    }
}
