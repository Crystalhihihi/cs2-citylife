using System.Threading;
using System.Threading.Tasks;

namespace CityLife.Llm
{
    // ============================================================================
    // 如何接入新供给（社区扩展指南）
    //
    // 1. 新建一个类实现 ICliProvider（三个成员：Name / IsAvailable / OneShotAsync）。
    // 2. 在宿主装配处（Mod.OnLoad 或设置页）把它注册进 CliGateway：
    //        var gateway = new CliGateway(new YourProvider());
    //        gateway.Start();
    // 3. 纪律（双轨通用，违反即破坏成本模型，见设计文档 §4 M3 / §12 #18）：
    //    - 全 one-shot：每次调用独立发一炉，绝不长连接、不多轮会话；
    //    - 缓存纪律不变：prompt 组织为"固定前缀 + 动态尾部"，前缀逐字节稳定
    //      （禁时间戳/随机 ID），全部动态内容压尾部——订阅 CLI 与 API 计费
    //      两轨的成本关键都是缓存命中；
    //    - 绝不阻塞模拟线程：调用只会发生在 CliGateway 的后台线程上。
    //
    // 按 API 计费的接入同样实现本接口（HttpClient one-shot 即可），
    // 若能拿到 usage 字段，请填进 CliResult.PromptTokens/ResponseTokens，
    // 网关统计会自动从字符估算升级为精确记账。
    // ============================================================================

    /// <summary>
    /// LLM 供给抽象（双轨：订阅制 code CLI / 按 API 计费接入）。
    /// 实现方见文件顶部"如何接入新供给"。首发实现：<see cref="KimiCliProvider"/>。
    /// </summary>
    public interface ICliProvider
    {
        /// <summary>供给名（日志与统计用），如 "KimiCli"。</summary>
        string Name { get; }

        /// <summary>
        /// 供给当前是否可用（CLI 是否安装/处于可登录态）。
        /// 应做轻量本地检查（找 exe、查配置），不得发网络请求阻塞调用方。
        /// </summary>
        bool IsAvailable();

        /// <summary>
        /// 单次一炉：送入完整 prompt，返回完整文本。绝不长连接、不多轮。
        /// 实现方负责自身超时与进程/连接回收；<paramref name="ct"/> 触发时应尽快中断。
        /// </summary>
        /// <param name="prompt">完整 prompt（固定前缀 + 动态尾部，由调用方组织）。</param>
        /// <param name="ct">取消令牌（网关停止时触发）。</param>
        Task<CliResult> OneShotAsync(string prompt, CancellationToken ct);
    }

    /// <summary>
    /// 一次 one-shot 的结果。成功时 <see cref="Text"/> 为模型输出全文；
    /// 失败时 <see cref="Error"/> 带人类可读摘要（供日志/诊断）。
    /// </summary>
    public sealed class CliResult
    {
        /// <summary>是否成功拿到模型输出。</summary>
        public bool Success { get; }

        /// <summary>模型输出全文（失败时为空串）。</summary>
        public string Text { get; }

        /// <summary>失败原因摘要（成功时为 null）。</summary>
        public string? Error { get; }

        /// <summary>端到端延迟（含进程起停），毫秒。</summary>
        public long LatencyMs { get; }

        /// <summary>prompt 字符数。usage 实测不暴露（见 docs/spikes/2026-08-19-cli-link.md），先按字符记账。</summary>
        public int PromptChars { get; }

        /// <summary>响应字符数，同上。</summary>
        public int ResponseChars { get; }

        /// <summary>精确 prompt token 数。CLI 输出不暴露 usage，留空；API 计费接入若拿得到请填上（预留字段）。</summary>
        public int? PromptTokens { get; set; }

        /// <summary>精确 response token 数，同 <see cref="PromptTokens"/>（预留字段）。</summary>
        public int? ResponseTokens { get; set; }

        private CliResult(bool success, string text, string? error, long latencyMs, int promptChars)
        {
            Success = success;
            Text = text;
            Error = error;
            LatencyMs = latencyMs;
            PromptChars = promptChars;
            ResponseChars = text.Length;
        }

        /// <summary>构造成功结果。</summary>
        public static CliResult Ok(string text, int promptChars, long latencyMs)
            => new CliResult(true, text, null, latencyMs, promptChars);

        /// <summary>构造失败结果。</summary>
        public static CliResult Fail(string error, int promptChars, long latencyMs = 0)
            => new CliResult(false, string.Empty, error, latencyMs, promptChars);
    }
}
