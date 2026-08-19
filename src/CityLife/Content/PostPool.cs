using System.Collections.Generic;

namespace CityLife.Content
{
    /// <summary>
    /// 池条目：帖子 + 可选标签 + 评论串。Tag 目前是锚点实体（装箱的 Unity Entity，GameBridge 侧解箱），
    /// Content 命名空间只经手 object，不引用任何游戏类型（版本免疫铁律不破）。
    /// Comments = [评论者显示名, 评论正文]  pairs（M2-B 评论区），空数组=无评论。
    /// </summary>
    public readonly struct PoolEntry
    {
        public readonly Post Post;
        public readonly object? Tag;
        public readonly string[][] Comments;

        public PoolEntry(in Post post, object? tag, string[][] comments)
        {
            Post = post;
            Tag = tag;
            Comments = comments;
        }
    }

    /// <summary>
    /// 帖子池：批量一炉入库、按节拍单条出库（滴灌释放，设计文档 §4 M2"一炉出串+滴灌"）。
    /// 线程安全：生产侧是网关回调（主线程轮询）与未来的后台预生成，消费侧是导演系统。
    /// </summary>
    public sealed class PostPool
    {
        private readonly Queue<PoolEntry> m_Queue = new();
        private readonly object m_Lock = new();

        public int Count
        {
            get { lock (m_Lock) return m_Queue.Count; }
        }

        public void Add(in PoolEntry entry)
        {
            lock (m_Lock) m_Queue.Enqueue(entry);
        }

        public bool TryTake(out PoolEntry entry)
        {
            lock (m_Lock)
            {
                if (m_Queue.Count > 0)
                {
                    entry = m_Queue.Dequeue();
                    return true;
                }
            }
            entry = default;
            return false;
        }

        /// <summary>查重兜底：收炉时跳过与池内/已发重复的正文。</summary>
        public bool ContainsText(string text)
        {
            lock (m_Lock)
            {
                foreach (var p in m_Queue)
                    if (p.Post.Text == text)
                        return true;
            }
            return false;
        }
    }
}
