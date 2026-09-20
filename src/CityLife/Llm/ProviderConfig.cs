using System;
using System.IO;
using CityLife.Util;

namespace CityLife.Llm
{
    /// <summary>
    /// LLM 供给配置（§12 #59 起降级为**一次性迁移种子**：游戏内设置页 kGroupLlm 组是配置主源，
    /// 本文件仅在设置页尚无配置时被 Mod.SeedLlmFromJson 读一次，此后不再读取）。
    /// 文件位置：游戏用户目录 ModsSettings/CityLife/llm.json——**在仓库外，API key 绝不进 git**。
    /// 字段：provider（"kimi-cli" 默认 / "openai-compatible"）、baseUrl、apiKey、model、thinking；
    /// **分轨扩展**（2026-09-20 双轨定轨，§12 #59 修正注记）：slowModel/slowThinking（高质量轨）与
    /// fastModel/fastThinking（高吞吐轨）——缺省回落全局 model/thinking（老文件行为不变）。
    /// thinking 值域：""=厂商默认、"disabled"=关、"high"/"medium"/"low"=显式档位（effort 透传）。
    /// 示例：
    /// { "provider": "openai-compatible", "baseUrl": "https://api.deepseek.com/v1",
    ///   "apiKey": "sk-...", "slowModel": "deepseek-v4-pro", "slowThinking": "high",
    ///   "fastModel": "deepseek-v4-flash", "fastThinking": "disabled" }
    /// </summary>
    public sealed class ProviderConfig
    {
        public string Provider = "kimi-cli";
        public string BaseUrl = "";
        public string ApiKey = "";
        public string Model = "";
        public string Thinking = ""; // "disabled" = 关闭深度思考（DeepSeek V4 系支持；省 token 大头）

        /// <summary>慢轨模型（缺省回落 <see cref="Model"/>）。</summary>
        public string SlowModel = "";
        /// <summary>慢轨 thinking（缺省回落 <see cref="Thinking"/>）。</summary>
        public string SlowThinking = "";
        /// <summary>快轨模型（缺省回落 <see cref="Model"/>）。</summary>
        public string FastModel = "";
        /// <summary>快轨 thinking（缺省回落 <see cref="Thinking"/>）。</summary>
        public string FastThinking = "";

        /// <summary>慢轨 baseUrl（缺省回落 <see cref="BaseUrl"/>；2026-09-20 整 provider 分轨）。</summary>
        public string SlowBaseUrl = "";
        /// <summary>慢轨 apiKey（缺省回落 <see cref="ApiKey"/>；分轨存放但任何日志只记域名，key 永不打印）。</summary>
        public string SlowApiKey = "";
        /// <summary>快轨 baseUrl（缺省回落 <see cref="BaseUrl"/>）。</summary>
        public string FastBaseUrl = "";
        /// <summary>快轨 apiKey（缺省回落 <see cref="ApiKey"/>；同上纪律）。</summary>
        public string FastApiKey = "";

        /// <summary>慢轨有效模型（分轨字段优先，全局兜底）。</summary>
        public string EffectiveSlowModel => SlowModel.Length > 0 ? SlowModel : Model;
        /// <summary>慢轨有效 thinking。</summary>
        public string EffectiveSlowThinking => SlowThinking.Length > 0 ? SlowThinking : Thinking;
        /// <summary>快轨有效模型。</summary>
        public string EffectiveFastModel => FastModel.Length > 0 ? FastModel : Model;
        /// <summary>快轨有效 thinking。</summary>
        public string EffectiveFastThinking => FastThinking.Length > 0 ? FastThinking : Thinking;
        /// <summary>慢轨有效 baseUrl。</summary>
        public string EffectiveSlowBaseUrl => SlowBaseUrl.Length > 0 ? SlowBaseUrl : BaseUrl;
        /// <summary>慢轨有效 apiKey（永不进日志）。</summary>
        public string EffectiveSlowApiKey => SlowApiKey.Length > 0 ? SlowApiKey : ApiKey;
        /// <summary>快轨有效 baseUrl。</summary>
        public string EffectiveFastBaseUrl => FastBaseUrl.Length > 0 ? FastBaseUrl : BaseUrl;
        /// <summary>快轨有效 apiKey（永不进日志）。</summary>
        public string EffectiveFastApiKey => FastApiKey.Length > 0 ? FastApiKey : ApiKey;

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
                cfg.SlowModel = JsonMini.GetStr(json, "slowModel") ?? "";
                cfg.SlowThinking = JsonMini.GetStr(json, "slowThinking") ?? "";
                cfg.FastModel = JsonMini.GetStr(json, "fastModel") ?? "";
                cfg.FastThinking = JsonMini.GetStr(json, "fastThinking") ?? "";
                cfg.SlowBaseUrl = JsonMini.GetStr(json, "slowBaseUrl") ?? "";
                cfg.SlowApiKey = JsonMini.GetStr(json, "slowApiKey") ?? "";
                cfg.FastBaseUrl = JsonMini.GetStr(json, "fastBaseUrl") ?? "";
                cfg.FastApiKey = JsonMini.GetStr(json, "fastApiKey") ?? "";
                // 注意：key 永远不进日志（分轨 key 同纪律——只记域名）
                log("[LLM] 供给配置：" + cfg.Provider +
                    (cfg.Provider == "openai-compatible"
                        ? $" 慢轨={DomainOf(cfg.EffectiveSlowBaseUrl)}/{cfg.EffectiveSlowModel}/{Norm(cfg.EffectiveSlowThinking)} 快轨={DomainOf(cfg.EffectiveFastBaseUrl)}/{cfg.EffectiveFastModel}/{Norm(cfg.EffectiveFastThinking)}"
                        : ""));
                return cfg;
            }
            catch (Exception e)
            {
                log("[LLM] 供给配置读取失败（回退 KimiCli）：" + e.Message);
                return new ProviderConfig();
            }

            static string Norm(string t) => t.Length > 0 ? t : "默认";
            static string DomainOf(string url)
            {
                var i = url.IndexOf("://", StringComparison.Ordinal);
                var rest = i >= 0 ? url[(i + 3)..] : url;
                var slash = rest.IndexOf('/');
                return slash > 0 ? rest[..slash] : rest;
            }
        }
    }
}
