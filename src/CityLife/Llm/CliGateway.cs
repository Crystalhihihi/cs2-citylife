using System;
using System.Collections.Generic;
using System.Threading;

namespace CityLife.Llm
{
    /// <summary>请求优先级。网关后台泵严格按 High → Normal → Low 取件。</summary>
    public enum CliPriority
    {
        /// <summary>低（闲聊池补炉等可延后内容）。</summary>
        Low = 0,
        /// <summary>普通（默认）。</summary>
        Normal = 1,
        /// <summary>高（市长发言意图解析等玩家等结果的场景）。</summary>
        High = 2,
    }

    /// <summary>
    /// 一条待发的 LLM one-shot 请求。prompt 由调用方按"固定前缀 + 动态尾部"组织（缓存纪律）。
    /// </summary>
    public sealed class CliRequest
    {
        /// <summary>请求标识（结果回填用）。默认随机生成。</summary>
        public string Id { get; }

        /// <summary>完整 prompt。</summary>
        public string Prompt { get; }

        /// <summary>优先级。</summary>
        public CliPriority Priority { get; }

        /// <summary>入队时刻（UTC）。</summary>
        public DateTime EnqueueTime { get; }

        /// <summary>存活秒数。网关取件时已过期的请求直接丢弃（AI 内容宁缺毋滥）。</summary>
        public double TtlSeconds { get; }

        public CliRequest(string prompt, CliPriority priority = CliPriority.Normal, double ttlSeconds = 300, string? id = null)
        {
            Id = id ?? Guid.NewGuid().ToString("N");
            Prompt = prompt;
            Priority = priority;
            EnqueueTime = DateTime.UtcNow;
            TtlSeconds = ttlSeconds;
        }

        /// <summary>当前是否已过期。</summary>
        public bool IsExpired => (DateTime.UtcNow - EnqueueTime).TotalSeconds > TtlSeconds;
    }

    /// <summary>
    /// 一条已完成的请求结果，由游戏主线程经 <see cref="CliGateway.TryDequeueResult"/> 轮询取走。
    /// </summary>
    public sealed class CliCompletedResult
    {
        /// <summary>对应 <see cref="CliRequest.Id"/>。</summary>
        public string RequestId { get; }

        /// <summary>one-shot 结果（含失败信息）。</summary>
        public CliResult Result { get; }

        /// <summary>实际尝试次数（1 = 一次成功/放弃；2 = 重试过一次）。</summary>
        public int Attempts { get; }

        public CliCompletedResult(string requestId, CliResult result, int attempts)
        {
            RequestId = requestId;
            Result = result;
            Attempts = attempts;
        }
    }

    /// <summary>
    /// CLI 网关：mod 内唯一 LLM 入口。
    /// 任何线程都可 <see cref="Enqueue"/>；游戏主线程只做 <see cref="TryDequeueResult"/> 轮询，
    /// 真正的 CLI 调用全部在网关自带的后台线程上串行发出，绝不阻塞模拟线程（架构铁律 6）。
    /// </summary>
    public sealed class CliGateway : IDisposable
    {
        /// <summary>
        /// MUTE 总开关：true 时 <see cref="Enqueue"/> 直接丢弃（纯模板零 token 模式，见设计文档 §8）。
        /// 静态属性，设置页一键切换，无需拿到网关实例。
        /// </summary>
        public static bool Mute { get; set; }

        /// <summary>诊断日志出口，宿主注入；未注入时静默。</summary>
        public static Action<string>? Log { get; set; }

        /// <summary>
        /// token 估算折算系数：约 2 字符 ≈ 1 token。
        /// 纯经验估算（中英混合语料的粗口径）——CLI 输出不暴露 usage
        /// （docs/spikes/2026-08-19-cli-link.md §2.3），仅用于统计日志里的量级参考，
        /// 不得用于任何计费/限流判断。API 计费接入填了精确 usage 后应改用精确值。
        /// </summary>
        public const double CharsPerTokenEstimate = 2.0;

        /// <summary>请求间隔冷却秒数（保护订阅额度/速率限制），默认 2s。</summary>
        public double CooldownSeconds { get; set; } = 2.0;

        /// <summary>失败后重试间隔秒数，默认 5s。</summary>
        public double RetryDelaySeconds { get; set; } = 5.0;

        /// <summary>失败重试次数上限，默认 1（重试 1 次后放弃）。</summary>
        public int MaxRetries { get; set; } = 1;

        /// <summary>
        /// 并发字段位（v1 固定串行 = 1，仅预留）。改成 >1 前需先把泵改为多工位，
        /// 并复核订阅速率限制（设计文档 §4 M3 全局小并发 2-3）。
        /// </summary>
        public int Concurrency { get; set; } = 1;

        private readonly ICliProvider _provider;
        private readonly object _gate = new object();
        private readonly Queue<CliRequest> _high = new Queue<CliRequest>();
        private readonly Queue<CliRequest> _normal = new Queue<CliRequest>();
        private readonly Queue<CliRequest> _low = new Queue<CliRequest>();
        private readonly Queue<CliCompletedResult> _completed = new Queue<CliCompletedResult>();

        private Thread?[]? _pumps;
        private volatile bool _running;
        private CancellationTokenSource? _cts;

        // 统计计数（后台泵与任意线程的 Enqueue 都会写，一律走 Interlocked）
        private long _enqueued;
        private long _succeeded;
        private long _failed;
        private long _dropped;
        private long _retried;
        private long _promptChars;
        private long _responseChars;

        public CliGateway(ICliProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        /// <summary>当前排队中的请求数（三档合计）。</summary>
        public int PendingCount
        {
            get { lock (_gate) { return _high.Count + _normal.Count + _low.Count; } }
        }

        /// <summary>启动后台泵线程组（并发数由供给自报：API 轨多路削排队，CLI 轨单路串行）。重复调用无效。</summary>
        public void Start()
        {
            if (_running)
            {
                return;
            }
            _cts = new CancellationTokenSource();
            _running = true;
            var workers = Math.Max(1, _provider.MaxConcurrency);
            _pumps = new Thread[workers];
            for (int i = 0; i < workers; i++)
            {
                _pumps[i] = new Thread(PumpLoop)
                {
                    Name = $"CityLife.CliGateway.{i}",
                    IsBackground = true, // 游戏退出时随进程回收，绝不拖住退出
                };
                _pumps[i].Start();
            }
            Log?.Invoke($"[CliGateway] 已启动，供给={_provider.Name}（可用={_provider.IsAvailable()}，并发={workers}）");
        }

        /// <summary>停止后台泵组。进行中的一发最多再等待 2s，随后随进程回收（one-shot 无副作用）。</summary>
        public void Stop()
        {
            _running = false;
            _cts?.Cancel();
            if (_pumps != null)
                foreach (var p in _pumps)
                    if (p != null && p.IsAlive)
                        p.Join(2000);
            _pumps = null;
        }

        public void Dispose() => Stop();

        /// <summary>
        /// 入队一条请求。MUTE 开启时直接丢弃并计数。任意线程可调用。
        /// </summary>
        public void Enqueue(CliRequest request)
        {
            if (Mute)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            lock (_gate)
            {
                switch (request.Priority)
                {
                    case CliPriority.High: _high.Enqueue(request); break;
                    case CliPriority.Low: _low.Enqueue(request); break;
                    default: _normal.Enqueue(request); break;
                }
            }
            Interlocked.Increment(ref _enqueued);
        }

        /// <summary>
        /// 游戏主线程轮询取结果（每帧/每次 UI 刷新调一次，无结果立即返回 false，绝不阻塞）。
        /// </summary>
        public bool TryDequeueResult(out CliCompletedResult result)
        {
            lock (_gate)
            {
                if (_completed.Count > 0)
                {
                    result = _completed.Dequeue();
                    return true;
                }
            }
            result = null!;
            return false;
        }

        /// <summary>累计统计摘要（日志/设置页展示用）。token 为字符折算估算值，见 <see cref="CharsPerTokenEstimate"/>。</summary>
        public string GetStatsSummary()
        {
            long enq = Interlocked.Read(ref _enqueued);
            long ok = Interlocked.Read(ref _succeeded);
            long fail = Interlocked.Read(ref _failed);
            long drop = Interlocked.Read(ref _dropped);
            long retry = Interlocked.Read(ref _retried);
            long pc = Interlocked.Read(ref _promptChars);
            long rc = Interlocked.Read(ref _responseChars);
            long estTokens = (long)((pc + rc) / CharsPerTokenEstimate);
            return $"[CliGateway] 供给={_provider.Name}｜请求 {enq}（成功 {ok}/失败 {fail}/丢弃 {drop}/重试 {retry}）"
                + $"｜字符 prompt {pc}/response {rc}｜token 估算 ≈{estTokens}（{CharsPerTokenEstimate} 字符≈1 token，估算值）"
                + $"｜待处理 {PendingCount}｜MUTE={(Mute ? "开" : "关")}";
        }

        // ------------------------------------------------------------------
        // 后台泵：串行取件 → 过期检查 → 调供给 → 失败重试 → 回填结果 → 冷却。
        // v1 并发 = 1（Concurrency 仅预留字段位）。
        // ------------------------------------------------------------------

        private void PumpLoop()
        {
            CancellationToken ct = _cts!.Token;
            while (_running)
            {
                CliRequest? req = TakeNext();
                if (req == null)
                {
                    if (SleepInterruptible(0.2)) break;
                    continue;
                }

                if (req.IsExpired)
                {
                    Interlocked.Increment(ref _dropped);
                    Log?.Invoke($"[CliGateway] 请求 {req.Id} 已过期（TTL {req.TtlSeconds}s），丢弃");
                    continue;
                }

                CliResult result;
                int attempts;
                if (!_provider.IsAvailable())
                {
                    // 供给不可用（未安装 CLI）不是瞬时故障，不重试，直接判失败
                    attempts = 1;
                    result = CliResult.Fail($"供给 {_provider.Name} 不可用", req.Prompt.Length);
                }
                else
                {
                    CliResult? retried = CallWithRetry(req, ct, out attempts);
                    if (retried == null)
                    {
                        break; // 停止信号打断重试等待
                    }
                    result = retried;
                }

                if (result.Success)
                {
                    Interlocked.Increment(ref _succeeded);
                    Interlocked.Add(ref _responseChars, result.ResponseChars);
                    // 耗时可见化（2026-09-11 "thinking 慢"排查：实测延迟=LLM+排队+收炉节拍，逐项记账才分得清）
                    Log?.Invoke($"[CliGateway] {req.Id} 完成：调用 {result.LatencyMs / 1000d:F1}s" +
                        $"（排队 {((DateTime.UtcNow - req.EnqueueTime).TotalSeconds - result.LatencyMs / 1000d):F1}s）");
                }
                else
                {
                    Interlocked.Increment(ref _failed);
                    Log?.Invoke($"[CliGateway] 请求 {req.Id} 失败（{attempts} 次尝试）：{result.Error}");
                }
                Interlocked.Add(ref _promptChars, (long)result.PromptChars * attempts);

                lock (_gate)
                {
                    _completed.Enqueue(new CliCompletedResult(req.Id, result, attempts));
                }

                if (SleepInterruptible(CooldownSeconds)) break;
            }
        }

        /// <summary>调供给，失败按 <see cref="MaxRetries"/> 重试。返回 null 表示收到停止信号。</summary>
        private CliResult? CallWithRetry(CliRequest req, CancellationToken ct, out int attempts)
        {
            attempts = 0;
            while (true)
            {
                attempts++;
                CliResult result;
                try
                {
                    result = _provider.OneShotAsync(req.Prompt, ct).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    // 供给实现漏出的异常统一按失败记账，绝不炸掉泵线程
                    result = CliResult.Fail($"供给异常：{ex.GetType().Name}: {ex.Message}", req.Prompt.Length);
                }

                if (result.Success || attempts > MaxRetries)
                {
                    return result;
                }

                Interlocked.Increment(ref _retried);
                Log?.Invoke($"[CliGateway] 请求 {req.Id} 第 {attempts} 次失败（{result.Error}），{RetryDelaySeconds}s 后重试");
                if (SleepInterruptible(RetryDelaySeconds))
                {
                    return null;
                }
            }
        }

        private CliRequest? TakeNext()
        {
            lock (_gate)
            {
                if (_high.Count > 0) return _high.Dequeue();
                if (_normal.Count > 0) return _normal.Dequeue();
                if (_low.Count > 0) return _low.Dequeue();
                return null;
            }
        }

        /// <summary>可被打断的睡眠；返回 true 表示收到停止信号。</summary>
        private bool SleepInterruptible(double seconds)
        {
            return _cts!.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds));
        }
    }
}
