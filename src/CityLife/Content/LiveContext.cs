namespace CityLife.Content
{
    /// <summary>
    /// 生成侧共享上下文（主线程读写，纯静态）：
    /// - 市长发言：面板 trigger 写入，一条发言影响随后 3 炉（市民的讨论有惯性；写回层 EventSpec 是 M4 的事）
    /// - 突发事件：EventNewsSystem 写入，导演读一次即清（新闻热度不过夜）
    /// </summary>
    public static class LiveContext
    {
        /// <summary>市长最近一条发言（进 prompt 尾部【市长说】）。</summary>
        public static string? MayorPost;

        /// <summary>市长发言的影响力剩余炉数。</summary>
        public static int MayorBatchesLeft;

        /// <summary>最近一条突发事件文本（下一炉变热议串的引信）。</summary>
        public static string? LastBreaking;

        public static void PublishMayor(string text)
        {
            MayorPost = text;
            MayorBatchesLeft = 3;
        }
    }
}
