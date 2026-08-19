namespace CityLife.Content
{
    /// <summary>
    /// 帖子形态（角度轴落地，docs/spikes/2026-08-20-platform-styles.md §4b）：
    /// 每条帖除人格卡外再抽一种形态，骨架写进 prompt 头部。
    /// 形态只规定"帖子长什么形状"，话题仍由执行层统一供给。
    /// </summary>
    public sealed class PostForm
    {
        public readonly string Id;
        public readonly string Skeleton;

        public PostForm(string id, string skeleton)
        {
            Id = id;
            Skeleton = skeleton;
        }
    }

    public static class PostForms
    {
        // 六种形态骨架（调研 §4b）；骨架里不写示范句，写进去就会被模型逐字复读
        public static readonly PostForm[] All =
        {
            new PostForm("吐槽", "时间地点场景+落差/冲突+反问或冷嘲"),
            new PostForm("求助", "具体困境+背景数字+直接提问（听劝体）"),
            new PostForm("凡尔赛", "抱怨的壳+藏不住的得意"),
            new PostForm("盘点", "数字清单体，月度/年度记账"),
            new PostForm("喊话", "对无名对象喊话，或回应本城话题但不指名（独立成帖，不是回复）"),
            new PostForm("连载", "留钩（先码，下班细说）或\"更新：\"开头接前情"),
        };
    }

    /// <summary>一条分配：人格卡 × 形态 × 评论数配额（常态 1-3，热帖 8-30——2026-08-19 玩家定案的评论生态分布）。</summary>
    public readonly struct Assignment
    {
        public readonly Persona Persona;
        public readonly PostForm Form;
        public readonly int CommentTarget;

        public Assignment(Persona persona, PostForm form, int commentTarget = 2)
        {
            Persona = persona;
            Form = form;
            CommentTarget = commentTarget;
        }
    }
}
