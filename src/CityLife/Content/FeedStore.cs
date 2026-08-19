using System.Collections.Generic;
using System.Text;
using CityLife.Util;

namespace CityLife.Content
{
    /// <summary>
    /// 信息流仓库：记录每一条已发帖子（含突发帖），供 M2 自建面板消费。
    /// 纯内存环形缓冲（默认 100 条）；序列化成 JSON 经 binding 推给 React。
    /// 纯 C# 零游戏引用（版本免疫）；Version 自增供 UI 侧比对，免全量重推。
    /// 如何扩展：加字段（点赞等）→ 改 FeedItem + Record + ToJson 三处。
    /// </summary>
    public sealed class FeedStore
    {
        public readonly struct FeedItem
        {
            public readonly string Author;
            public readonly string Text;
            public readonly string Topic;
            public readonly string PersonaId;
            public readonly uint Seq;
            public readonly int EntityIndex;    // 锚点实体（0=无），面板点击聚焦预留
            public readonly int EntityVersion;
            public readonly string[][] Comments; // [评论者, 正文] pairs（M2-B 评论区），空数组=无

            public FeedItem(string author, string text, string topic, string personaId, uint seq,
                            int entityIndex, int entityVersion, string[][]? comments)
            {
                Author = author;
                Text = text;
                Topic = topic;
                PersonaId = personaId;
                Seq = seq;
                EntityIndex = entityIndex;
                EntityVersion = entityVersion;
                Comments = comments ?? System.Array.Empty<string[]>();
            }
        }

        private const int k_Max = 100;

        private readonly Queue<FeedItem> m_Items = new();
        private readonly object m_Lock = new();
        private uint m_Seq;

        /// <summary>每次新增自增。UI 侧比对 Version 没变就跳过渲染（性能纪律）。</summary>
        public int Version { get; private set; }

        public int Count
        {
            get { lock (m_Lock) return m_Items.Count; }
        }

        /// <summary>登记一帖。entityIndex/Version = 锚点实体坐标（无则 0），comments = 评论串（无则 null）。</summary>
        public void Record(in Post post, int entityIndex = 0, int entityVersion = 0, string[][]? comments = null)
        {
            lock (m_Lock)
            {
                m_Items.Enqueue(new FeedItem(post.Author, post.Text, post.Topic.ToString(),
                                             post.PersonaId, m_Seq++, entityIndex, entityVersion, comments));
                while (m_Items.Count > k_Max)
                    m_Items.Dequeue();
                Version++;
            }
        }

        /// <summary>整流序列化为 JSON（最新在后）。短键名省流量：a=作者 t=正文 k=话题 p=人格 n=序号 e=锚点实体[index,version] c=评论[[名,文]]。</summary>
        public string ToJson()
        {
            lock (m_Lock)
            {
                var sb = new StringBuilder(m_Items.Count * 160 + 2);
                sb.Append('[');
                var first = true;
                foreach (var it in m_Items)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"a\":\"").Append(JsonMini.Escape(it.Author))
                      .Append("\",\"t\":\"").Append(JsonMini.Escape(it.Text))
                      .Append("\",\"k\":\"").Append(JsonMini.Escape(it.Topic))
                      .Append("\",\"p\":\"").Append(JsonMini.Escape(it.PersonaId))
                      .Append("\",\"n\":").Append(it.Seq);
                    if (it.EntityIndex != 0)
                        sb.Append(",\"e\":[").Append(it.EntityIndex).Append(',').Append(it.EntityVersion).Append(']');
                    if (it.Comments.Length > 0)
                    {
                        sb.Append(",\"c\":[");
                        for (int i = 0; i < it.Comments.Length; i++)
                        {
                            if (i > 0) sb.Append(',');
                            sb.Append("[\"").Append(JsonMini.Escape(it.Comments[i][0]))
                              .Append("\",\"").Append(JsonMini.Escape(it.Comments[i][1])).Append("\"]");
                        }
                        sb.Append(']');
                    }
                    sb.Append('}');
                }
                sb.Append(']');
                return sb.ToString();
            }
        }
    }
}
