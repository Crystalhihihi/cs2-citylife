using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CityLife.Util;

namespace CityLife.Llm
{
    /// <summary>
    /// OpenAI 兼容端点供给（API 计费轨首发：硅基流动 SiliconFlow）。
    /// POST {baseUrl}/chat/completions，one-shot（messages 仅一条 user，无会话状态）。
    /// 与订阅 CLI 轨的差异与纪律：
    /// - 响应带 usage → 精确 token 记账（填 CliResult.PromptTokens/ResponseTokens 预留字段，
    ///   网关统计自动从字符估算升级为精确值）；
    /// - 缓存纪律不变：prompt 固定前缀+动态尾部（§4 M3 / §12 #18），厂商服务端缓存才有得吃；
    /// - key 只来自游戏内设置页（kGroupLlm 组，.coc 明文仅本地保存；llm.json 为一次性迁移种子，
    ///   见 Mod.SeedLlmFromJson），绝不进仓库、绝不进日志。
    /// </summary>
    public sealed class OpenAiCompatibleProvider : ICliProvider
    {
        // 静态复用 HttpClient（握手/连接池摊薄到每次 one-shot；请求级依然独立，无会话语义）
        private static readonly HttpClient s_Http = new HttpClient();

        private readonly string m_Url;
        private readonly string m_Key;
        private readonly string m_Model;
        private readonly string m_Thinking; // "disabled" → 请求体带 "thinking":{"type":"disabled"}

        /// <summary>诊断日志，宿主注入（Mod.OnLoad）。</summary>
        public static Action<string>? Log { get; set; }

        public OpenAiCompatibleProvider(string baseUrl, string apiKey, string model, string thinking = "")
        {
            m_Url = (baseUrl ?? "").TrimEnd('/') + "/chat/completions";
            m_Key = apiKey ?? "";
            m_Model = model ?? "";
            m_Thinking = thinking ?? "";
        }

        public string Name => $"OpenAiCompat({m_Model})";

        /// <inheritdoc/>
        public int MaxConcurrency => 3; // 纯 HTTP 无共享可变状态（HttpClient 静态复用线程安全），3 路削排队

        public bool IsAvailable()
            => !string.IsNullOrEmpty(m_Key) && !string.IsNullOrEmpty(m_Model) && m_Url.Length > 1;

        public async Task<CliResult> OneShotAsync(string prompt, CancellationToken ct)
        {
            var promptChars = prompt?.Length ?? 0;
            if (!IsAvailable())
                return CliResult.Fail("配置缺失（baseUrl/apiKey/model 任一为空）", promptChars);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // temperature 0.8：社交语料要一点发散；JSON 体手工拼（禁 NuGet 的现实）
                // thinking=disabled 时关深度思考（DeepSeek V4 系实测：隐式推理 token 占 output 大头，关掉立省）
                var thinkingPart = m_Thinking == "disabled" ? ",\"thinking\":{\"type\":\"disabled\"}" : "";
                var body = "{\"model\":\"" + JsonMini.Escape(m_Model) +
                           "\",\"messages\":[{\"role\":\"user\",\"content\":\"" + JsonMini.Escape(prompt!) +
                           "\"}],\"temperature\":0.8" + thinkingPart + "}";

                using var req = new HttpRequestMessage(HttpMethod.Post, m_Url);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", m_Key);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(420)); // v4-flash 隐式推理 token 占大头（实测一炉 8715 output tokens/71s），高峰方差大；240s 实机超时三连（2026-08-19）

                using var resp = await s_Http.SendAsync(req, linked.Token).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                sw.Stop();

                if (!resp.IsSuccessStatusCode)
                    return CliResult.Fail($"HTTP {(int)resp.StatusCode}：{OneLine(text, 500)}", promptChars, sw.ElapsedMilliseconds);

                // 响应：choices[0].message.content + usage.prompt_tokens/completion_tokens
                var msgIdx = text.IndexOf("\"message\"", StringComparison.Ordinal);
                var content = msgIdx >= 0 ? JsonMini.GetStr(text.Substring(msgIdx), "content") : null;
                if (content == null)
                    return CliResult.Fail("响应解析失败：" + OneLine(text, 300), promptChars, sw.ElapsedMilliseconds);

                var r = CliResult.Ok(content, promptChars, sw.ElapsedMilliseconds);
                r.PromptTokens = JsonMini.GetInt(text, "prompt_tokens");
                r.ResponseTokens = JsonMini.GetInt(text, "completion_tokens");
                return r;
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return CliResult.Fail(ct.IsCancellationRequested ? "已取消" : "超时（420s）", promptChars, sw.ElapsedMilliseconds);
            }
            catch (Exception e)
            {
                sw.Stop();
                return CliResult.Fail($"{e.GetType().Name}：{OneLine(e.Message, 300)}", promptChars, sw.ElapsedMilliseconds);
            }
        }

        private static string OneLine(string s, int max)
            => (s.Length <= max ? s : s.Substring(0, max)).Replace('\n', ' ').Replace('\r', ' ');
    }
}
