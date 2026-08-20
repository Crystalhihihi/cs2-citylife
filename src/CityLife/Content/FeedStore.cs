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

        private readonly Queue<FeedItem> m_Items = new();
        private readonly object m_Lock = new();
        private uint m_Seq;

        /// <summary>环形缓冲上限（默认 100，settings.json 的 feedMaxItems 可调；超限时淘汰最旧）。</summary>
        public int MaxItems { get; set; } = 100;

        /// <summary>每次新增自增。UI 侧比对 Version 没变就跳过渲染（性能纪律）。</summary>
        public int Version { get; private set; }

        public int Count
        {
            get { lock (m_Lock) return m_Items.Count; }
        }

        /// <summary>登记一帖，返回分配的序号（UI key/评论挂载都靠它）。entityIndex/Version = 锚点实体坐标（无则 0），comments = 评论串（无则 null）。</summary>
        public uint Record(in Post post, int entityIndex = 0, int entityVersion = 0, string[][]? comments = null)
        {
            lock (m_Lock)
            {
                var seq = m_Seq++;
                m_Items.Enqueue(new FeedItem(post.Author, post.Text, post.Topic.ToString(),
                                             post.PersonaId, seq, entityIndex, entityVersion, comments));
                while (m_Items.Count > MaxItems)
                    m_Items.Dequeue();
                Version++;
                return seq;
            }
        }

        /// <summary>
        /// 给已发布的帖追加一条评论（市民回应市长帖 / 市长下场回复，M2-C 追加）。
        /// Queue 无随机访问：转数组找到 seq 重建（上限 100 条，成本忽略）。找到并追加返回 true。
        /// </summary>
        public bool AppendComment(uint seq, string author, string text)
        {
            lock (m_Lock)
            {
                var arr = m_Items.ToArray();
                for (int i = arr.Length - 1; i >= 0; i--)
                {
                    if (arr[i].Seq != seq)
                        continue;

                    var old = arr[i];
                    var next = new string[old.Comments.Length + 1][];
                    old.Comments.CopyTo(next, 0);
                    next[old.Comments.Length] = new[] { author, text };
                    arr[i] = new FeedItem(old.Author, old.Text, old.Topic, old.PersonaId, old.Seq,
                                          old.EntityIndex, old.EntityVersion, next);
                    m_Items.Clear();
                    foreach (var it in arr)
                        m_Items.Enqueue(it);
                    Version++;
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 找一条适合续热的热帖（评论续热炉数据源）：评论数落在 [min,max] 的帖里评论最多的一条。
        /// min 防冷帖没得聊，max 防热帖已完结还硬续。找到返回 true（comments 是内部数组引用，只读用）。
        /// </summary>
        public bool TryGetHotThread(int minComments, int maxComments,
                                    out uint seq, out string author, out string text, out string[][] comments)
        {
            lock (m_Lock)
            {
                var arr = m_Items.ToArray();
                var best = -1;
                for (int i = arr.Length - 1; i >= 0; i--)
                {
                    var c = arr[i].Comments.Length;
                    if (c < minComments || c > maxComments)
                        continue;
                    if (best < 0 || c > arr[best].Comments.Length)
                        best = i;
                }
                if (best < 0)
                {
                    seq = 0;
                    author = "";
                    text = "";
                    comments = System.Array.Empty<string[]>();
                    return false;
                }
                var it = arr[best];
                seq = it.Seq;
                author = it.Author;
                text = it.Text;
                comments = it.Comments;
                return true;
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
