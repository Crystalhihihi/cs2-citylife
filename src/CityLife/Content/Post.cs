namespace CityLife.Content
{
    /// <summary>
    /// 一条待发布的帖子。T0 期 Author 是固定的原型名（"市民小广播"等）；
    /// T1/T2 期改为执行层抽签器分配的画像名（防风格坍缩，见设计文档 §3）。
    /// PersonaId 用于跨炉认人（连载机制，platform-styles §4b-4）：同一人格卡下次中签时可带上集前情。
    /// </summary>
    public readonly struct Post
    {
        public readonly string Author;
        public readonly string Text;
        public readonly Topic Topic;
        public readonly string PersonaId;

        public Post(string author, string text, Topic topic, string personaId = "")
        {
            Author = author;
            Text = text;
            Topic = topic;
            PersonaId = personaId;
        }
    }
}
