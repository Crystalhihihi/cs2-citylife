using System.Collections.Generic;

namespace CityLife.Content
{
    /// <summary>
    /// 城市传闻榜（§12 #60 刀②"城市记忆喂入"）：执行层各系统把城里刚发生的真事
    /// （突发/落成拆除/活动开结/请愿）写成短句进环形榜，组炉时取最新 ≤3 条缀进 prompt 尾
    /// 【城里最近在传】——活力的本质是"这座城市有昨天"。
    /// 纯静态、主线程读写（LiveContext 同款纪律）；容量 12，同文本去重刷新置顶；
    /// 不耗 LLM、不进存档（会话级，与 LiveContext 同生命周期）。
    /// 如何扩展：新的事件源想被市民议论，在产生处 CityRumors.Add(短句) 一行即可——
    /// 短句必须是"事实蒸馏"（≤25 字，不带实体 id/坐标），别写观点。
    /// </summary>
    public static class CityRumors
    {
        private const int k_Capacity = 12;
        private static readonly List<string> s_Items = new();

        /// <summary>写入一条（同文本已存在则刷新置顶——同类事反复发生只记最新一次）。</summary>
        public static void Add(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            text = text.Trim();
            s_Items.Remove(text);
            s_Items.Add(text);
            while (s_Items.Count > k_Capacity)
                s_Items.RemoveAt(0);
        }

        /// <summary>最新 max 条（新→旧）。</summary>
        public static List<string> Recent(int max)
        {
            var list = new List<string>(max);
            for (var i = s_Items.Count - 1; i >= 0 && list.Count < max; i--)
                list.Add(s_Items[i]);
            return list;
        }
    }
}
