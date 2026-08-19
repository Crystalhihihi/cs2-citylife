namespace CityLife.Content
{
    /// <summary>
    /// 发帖通道抽象：内容引擎只认这个口，不关心对面是原版 Chirper 还是自建面板。
    /// 实现必须非阻塞（模拟线程纪律）。
    /// 如何扩展：新通道实现本接口并在 GameBridge.ContentDirectorSystem.OnCreate 替换注入。
    /// </summary>
    public interface IChirpChannel
    {
        /// <summary>尝试发布一条帖子；返回是否被接受。</summary>
        bool TryPost(in Post post);
    }

    /// <summary>
    /// 空通道（日志嘴替）：MVP 验证期把帖子打进日志，验证"话题→成文"链路。
    /// CustomChirps/自建通道就绪后替换——替换点是 ContentDirectorSystem.OnCreate 一行。
    /// </summary>
    public sealed class NullChirpChannel : IChirpChannel
    {
        private readonly System.Action<string> m_Log;

        public NullChirpChannel(System.Action<string> log) => m_Log = log;

        public bool TryPost(in Post post)
        {
            m_Log($"[Chirper·T0] {post.Author}：{post.Text}（话题:{post.Topic}）");
            return true;
        }
    }
}
