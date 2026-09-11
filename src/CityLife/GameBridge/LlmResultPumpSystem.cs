using Game;
using Unity.Entities;

namespace CityLife.GameBridge
{
    /// <summary>
    /// LLM 结果快速收炉泵（2026-09-11 "全是省略号"实机实锤后的结构性修复）：
    /// 网关结果原本只在 ContentDirectorSystem 的 4096 帧节拍（≈100 秒）统一出队——
    /// 一次性消耗语义（§12 #53）下池子等不起（chatter 炉 13.7s 就烧好了，结果在队列里躺 100 秒）。
    /// 本系统 64 帧一拍（≈1.6 秒）把结果从网关搬进导演的路由器。**唯一消费者纪律不变**：
    /// 出队只发生在这里（ContentDirectorSystem.OnUpdate 不再碰队列），路由逻辑仍归导演
    /// （<see cref="ContentDirectorSystem.RouteResult"/>）——泵只搬运，不懂业务。
    /// §12 #59 快慢双轨：慢轨（Mod.Gateway）与快轨（Mod.FastGateway）两个网关都泵，
    /// 各自 TryDequeueResult 轮一遍；路由按 requestId 前缀天然区分（"chatter:" 发自快轨），
    /// 收炉侧零改动。
    /// 如何扩展：新炉的结果路由前缀加在 RouteResult 的 if 链里，本系统零改动。
    /// </summary>
    public partial class LlmResultPumpSystem : GameSystemBase
    {
        private ContentDirectorSystem m_Director = default!;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Director = World.GetOrCreateSystemManaged<ContentDirectorSystem>();
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 64; // 2 的幂铁律；≈1.6 秒一收

        protected override void OnUpdate()
        {
            // 慢轨
            var slow = Mod.Gateway;
            if (slow != null)
                while (slow.TryDequeueResult(out var r))
                    m_Director.RouteResult(r);
            // 快轨（§12 #59：闲聊炉结果从这里回家）
            var fast = Mod.FastGateway;
            if (fast != null)
                while (fast.TryDequeueResult(out var r))
                    m_Director.RouteResult(r);
        }
    }
}
