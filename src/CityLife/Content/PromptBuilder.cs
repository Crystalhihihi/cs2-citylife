using System.Collections.Generic;
using System.Text;

namespace CityLife.Content
{
    /// <summary>
    /// prompt 组装器（缓存纪律 §4 M3 / §12 #18）：
    /// BuildHead 产出**逐字节稳定**的前缀（规则+卡库+形态库+输出 schema，禁时间戳/随机 ID），
    /// 启动时拼一次缓存复用；BuildBatch 把全部动态内容压在尾部。同前缀请求才吃得到服务端缓存。
    /// 内容纪律（2026-08-20 真实平台调研，docs/spikes/2026-08-20-platform-styles.md）：
    /// 具体名词密度 > 情绪词——每条必含 1-2 个具体细节，情绪必须有具体对象。
    /// </summary>
    public static class PromptBuilder
    {
        /// <summary>固定前缀（规则 + 风格卡库 + 形态库 + 体裁约束 + 评论分工 + 输出 schema）。卡册不变则逐字节稳定。</summary>
        public static string BuildHead(IReadOnlyList<Persona> cards)
        {
            var sb = new StringBuilder(2560);
            sb.Append("你是虚构城市社交平台\"市民圈\"的内容生成器。城市是模拟游戏里的虚构城市，一切内容虚构。\n");
            sb.Append("【铁律】对事不对人：可以吐槽政策、物价、交通、天气，绝不攻击市长本人或任何真实人物；不碰现实政治、种族、性别议题；不生成自伤内容；不使用真实名人、品牌、事件名。\n");
            sb.Append("【语言】简体中文口语，每条不超过50字，像真人刷手机时随手发的。\n");
            sb.Append("【密度】每条必须含 1-2 个具体细节（数字/地点/物件，可虚构但要有生活质感）；情绪必须有具体对象。\"早市韭菜两块五一斤\"好过\"菜价好便宜\"。同一作者每次举例必须换具体事物，不许总拿同一类东西说事。\n");
            sb.Append("【作者风格卡】按分配的 persona 严格模仿其语域，话题不受限制：\n");
            foreach (var c in cards)
                sb.Append("- ").Append(c.Id).Append('：').Append(c.Style).Append('\n');
            sb.Append("【形态】按分配的形态写，骨架：\n");
            foreach (var f in PostForms.All)
                sb.Append("- ").Append(f.Id).Append('：').Append(f.Skeleton).Append('\n');
            // 体裁硬约束（公告腔/评论区腔/白开水三大已实锤翻车模式 + 调研 §4d 反例清单精选）
            sb.Append("【体裁】禁止新闻发布会/官方通报腔；禁止\"作为一名市民\"\"谢邀\"式开头；禁止升华式结尾；禁止像回复别人；禁止编造精确数字与\"调查显示\"式伪引用——你只知道【城市此刻】里写了的事；禁止\"今天也是普普通通的一天\"式零细节句；禁止\"一方面另一方面\"式端水；禁止每句结尾都问\"你们怎么看\"；书面连接词（然而/因此/与此同时）少用；真人会断句会重复，不用句句标点正确。\n");
            // 评论区角色分工（调研 §4c）+ 数量分布（2026-08-19 玩家定案：常态 1-3，热帖 8-30）
            sb.Append("【评论】评论条数按分配行 ×N 评为准。角色按序分配：神回复（必须咬住主帖最具体的名词做反转或延伸，禁泛夸）、细节补充（同类经历+一个新细节）、抬杠（挑刺但对事不对人）、共情复读（≤10字）。立场不许一边倒；评论必须短于主帖；评论的 persona 从风格卡里任选，不必与主帖同卡。×8 以上是热帖：评论要吵起来/接龙/多方混战，楼主可以下场。\n");
            sb.Append("【争论串】评论之间可以@前面的人（\"@卡id \"开头，系统会自动换成显示名）：×4 评起至少有两条@前面的评论者；热帖必须形成争论串——多人互相@、有来有回、别各说各话；@主帖卡 id 就是怼楼主。\n");
            sb.Append("【输出】只输出一个 JSON 对象 {\"posts\":[{\"persona\":\"卡id\",\"text\":\"主帖\",\"comments\":[{\"persona\":\"卡id\",\"text\":\"评论\"}]}]}，条数与【分配】完全一致，顺序一致。禁止输出任何其他字符。\n");
            return sb.ToString();
        }

        /// <summary>
        /// 意图解析的固定前缀（M4）：市长发言 → 事件包 id + 参数。缓存纪律同主头——卡包不变则逐字节稳定。
        /// </summary>
        public static string BuildIntentHead(IReadOnlyList<EventPack> packs)
        {
            var sb = new StringBuilder(1024);
            sb.Append("你是意图解析器。玩家扮演市长，他的发言若在说\"要办某活动/安排某事\"，映射到下列事件包之一；只是闲聊/吐槽/提问则不算。\n");
            sb.Append("【事件包】\n");
            foreach (var p in packs)
                sb.Append("- ").Append(p.Id).Append('：').Append(p.Match).Append('\n');
            sb.Append("【输出】匹配：{\"pack\":\"包id\",\"venue\":\"地点原文或空\",\"budget\":\"低|中|高\",\"startHour\":19,\"durationH\":4,\"scale\":300}；不匹配：{\"pack\":null}。规则：startHour 是 0-23 整数，从时间原文换算（\"明晚8点\"→20，\"下午\"→15，没提→19）；durationH 默认 4；scale 从人数原文换算（\"千人大派对\"→1000），没提→300。只输出 JSON，禁止任何其他字符。\n");
            return sb.ToString();
        }

        /// <summary>意图解析完整 prompt = 意图头 + 市长发言。</summary>
        public static string BuildIntentPrompt(string intentHead, string mayorText)
            => intentHead + "【市长发言】\"" + mayorText + "\"\n";

        /// <summary>
        /// 突发快讯炉的固定前缀（T3 追加）：本地资讯账号"城市快讯"的快讯帖。
        /// 缓存纪律同主头——启动时拼一次复用（EventNewsSystem 静态缓存）。
        /// 体裁红线：本地媒体快讯腔（同城微博号），不是公文通报腔——玩家要的是"官方通报感"，
        /// 但真公文腔在信息流里极违和（已实锤翻车），取中间的本地快讯体。
        /// </summary>
        public static string BuildBreakingHead()
        {
            var sb = new StringBuilder(512);
            sb.Append("你是虚构城市社交平台\"市民圈\"的本地资讯账号\"城市快讯\"。城市是模拟游戏里的虚构城市，一切内容虚构。\n");
            sb.Append("【铁律】对事不对人；不碰现实政治、真实名人、品牌、事件名。\n");
            sb.Append("【体裁】本地快讯：一两句话，≤50字，口语化的同城媒体腔（\"城西有楼起火，消防已到场\"）；不编精确伤亡数字；不写\"据悉\\有关部门\\高度重视\"公文腔；不用 emoji。\n");
            sb.Append("【输出】只输出快讯正文本身，禁止任何其他字符（不要引号、不要 JSON、不要前缀）。\n");
            return sb.ToString();
        }

        /// <summary>突发快讯完整 prompt = 快讯头 + 事件事实（执行层给的确定性文本）。</summary>
        public static string BuildBreakingPrompt(string breakingHead, string facts)
            => breakingHead + "【事件】刚刚：" + facts + "。写一条快讯。\n";

        /// <summary>
        /// 市长回应炉的固定前缀（M2-C 追加）：与主头同一套缓存纪律——启动时拼一次复用。
        /// 只写评论，卡库共享主卡册（评论的 persona 任选）。
        /// </summary>
        public static string BuildReplyHead(IReadOnlyList<Persona> cards)
        {
            var sb = new StringBuilder(2048);
            sb.Append("你是虚构城市社交平台\"市民圈\"的市民。城市是模拟游戏里的虚构城市，一切内容虚构。\n");
            sb.Append("【铁律】对事不对人：可以夸可以怼市长说的话，但绝不攻击其本人或任何真实人物；不碰现实政治、种族、性别议题；不生成自伤内容；不使用真实名人、品牌、事件名。\n");
            sb.Append("【语言】简体中文口语，每条不超过40字，像真人刷评论。\n");
            sb.Append("【密度】尽量咬住帖子里最具体的名词（数字/地点/物件）做反转或延伸，禁泛夸。\n");
            sb.Append("【市民风格卡】（每条评论的 persona 从卡里任选）：\n");
            foreach (var c in cards)
                sb.Append("- ").Append(c.Id).Append('：').Append(c.Style).Append('\n');
            sb.Append("【角色分工】神回复/细节补充（同类经历+新细节）/抬杠（对事不对人）/共情复读（≤10字）；立场不许一边倒，有夸有怼才是真评论区。\n");
            sb.Append("【输出】只输出一个 JSON 对象 {\"comments\":[{\"persona\":\"卡id\",\"text\":\"评论\"}]}，禁止输出任何其他字符。\n");
            return sb.ToString();
        }

        /// <summary>市长回应炉完整 prompt = 回应头 + 市长帖 + 条数任务。</summary>
        public static string BuildReplyPrompt(string replyHead, string mayorText, int count)
        {
            return replyHead + "【市长发帖】\"" + mayorText + "\"\n【任务】写 " + count + " 条市民评论。\n";
        }

        /// <summary>
        /// 评论续热完整 prompt（评论区生态：老帖新一波评论，争论有来回）=
        /// 回应头（复用市长回应炉同一张固定头，缓存纪律）+ 帖子 + 已有评论 + 任务。
        /// 与 BuildReplyPrompt 的差别：语境是老帖续热，必须@已有评论者接着聊。
        /// </summary>
        public static string BuildThreadPrompt(string replyHead, string author, string text,
                                               IReadOnlyList<string[]> comments, int count)
        {
            var sb = new StringBuilder(replyHead.Length + 512);
            sb.Append(replyHead);
            sb.Append("【帖子】").Append(author).Append('：').Append(text).Append('\n');
            sb.Append("【已有评论】\n");
            var start = System.Math.Max(0, comments.Count - 12); // 只带最近 12 条，控长度
            for (int i = start; i < comments.Count; i++)
                sb.Append("- ").Append(comments[i][0]).Append('：').Append(comments[i][1]).Append('\n');
            sb.Append("【任务】这是老帖的新一波评论。写 ").Append(count)
              .Append(" 条新评论：必须@已有评论者或楼主（@名字 开头）接着聊——抬杠/补刀/站队/反转/爆新细节；别复述已有观点，别各说各话。\n");
            return sb.ToString();
        }

        /// <summary>
        /// 完整批量 prompt = 稳定头 + 动态尾。动态部分：城市此刻/本轮话题/分配/锚点/前情/突发/市长说/已发禁重复。
        /// recent = 最近已发正文（去重反馈）；seed = Daily 话题轮换；anchors/prevPosts 与 assigned 等长对齐（可空）；
        /// breaking = 突发事件文本（非空则本炉是热议串）；mayorContext = 市长发言（市民回应用，写回是 M4）。
        /// </summary>
        public static string BuildBatch(string head, in CitySnapshot s, Topic topic,
                                        IReadOnlyList<Assignment> assigned, IReadOnlyList<string> recent, uint seed,
                                        IReadOnlyList<string?>? anchors = null,
                                        IReadOnlyList<string?>? prevPosts = null,
                                        string? breaking = null, string? mayorContext = null,
                                        string? eventOutcome = null, string? ongoingEvent = null)
        {
            var sb = new StringBuilder(head.Length + 896);
            sb.Append(head);
            sb.Append("【城市此刻】").Append(DescribeCity(s)).Append('\n');
            sb.Append("【本轮话题】").Append(DescribeTopic(topic, s, seed)).Append('\n');
            sb.Append("【分配】").Append(assigned.Count).Append(" 条：");
            for (int i = 0; i < assigned.Count; i++)
            {
                sb.Append(i + 1).Append('=').Append(assigned[i].Persona.Id)
                  .Append('/').Append(assigned[i].Form.Id)
                  .Append('×').Append(assigned[i].CommentTarget).Append("评");
                var anchor = anchors != null && i < anchors.Count ? anchors[i] : null;
                if (!string.IsNullOrEmpty(anchor))
                    sb.Append('（').Append(anchor).Append('）');
                sb.Append(' ');
            }
            sb.Append('\n');
            if (anchors != null)
                sb.Append("【锚点】分配里带（锚：…）的帖子围绕那个具体对象写（可一笔带过，别编与它矛盾的细节）；没带的自由发挥。\n");
            if (prevPosts != null)
            {
                // 连载机制：有前情的席位喂回上集正文，允许（不强制）用"更新：/后续："续写
                var any = false;
                for (int i = 0; i < assigned.Count; i++)
                {
                    var prev = i < prevPosts.Count ? prevPosts[i] : null;
                    if (string.IsNullOrEmpty(prev))
                        continue;
                    if (!any)
                    {
                        sb.Append("【前情】以下席位有上集；若该席位形态是连载，可用\"更新：/后续：\"续写，否则别硬接：\n");
                        any = true;
                    }
                    sb.Append("- 第").Append(i + 1).Append("条的上集：").Append(prev).Append('\n');
                }
            }
            if (!string.IsNullOrEmpty(breaking))
                sb.Append("【突发事件】刚才城里出事：").Append(breaking)
                  .Append("。本炉是热议帖：锚定这件事写，评论串要多要热。\n");
            if (!string.IsNullOrEmpty(mayorContext))
                sb.Append("【市长说】市长刚刚发言：\"").Append(mayorContext)
                  .Append("\"。市民会读到；帖子和评论可以回应他（夸怼随意，对事不对人）。\n");
            if (!string.IsNullOrEmpty(eventOutcome))
                sb.Append("【活动结果】上次活动结算：").Append(eventOutcome)
                  .Append("。市民还在议论这事（有人晒现场、有人算财政账、有人吐槽）。\n");
            if (!string.IsNullOrEmpty(ongoingEvent))
                sb.Append("【活动进行中】").Append(ongoingEvent)
                  .Append(" 正在举办，市民正在陆续前往——本炉帖子/评论多聊现场（晒人潮、吐槽排队、安利摊位）。\n");
            if (recent.Count > 0)
            {
                // 去重反馈：one-shot 无记忆，已发内容必须喂回来模型才知道避开
                sb.Append("【以下已发过，禁止重复或换汤不换药】\n");
                var shown = 0;
                for (int i = recent.Count - 1; i >= 0 && shown < 8; i--, shown++)
                    sb.Append("- ").Append(recent[i]).Append('\n');
            }
            return sb.ToString();
        }

        private static string DescribeCity(in CitySnapshot s)
        {
            var weather = s.IsSnowing ? "雪" : s.IsRaining ? "雨" : "晴/阴";
            var season = string.IsNullOrEmpty(s.SeasonName) ? "未知" : s.SeasonName;
            return $"人口{FuzzPeople(s.Citizens)}，失业率{Qual(s.UnemploymentPercent, 5f, 12f)}，幸福度{Qual(s.Happiness, 40f, 70f)}，天气{weather}，季节{season}，时刻{s.HourOfDay}点";
        }

        // Daily 生活话题池（"日常，没有大事"零素材导致模型只能围着天气写的教训）：
        // 常青生活流话题，执行层按 seed 轮换，与天气解耦
        private static readonly string[] k_DailyTopics =
        {
            "一日三餐吃什么", "通勤路上那些事", "周末打算怎么过",
            "家里长短", "最近在追的剧或玩的游戏", "今天的心情",
            "小区快递柜又满了", "楼下新开店的尝鲜报告", "阳台种点什么好",
            "夜宵哪家强", "停车又绕了三圈", "楼上装修的电钻声",
            "换季添件衣服", "宽带又卡了", "家里的猫/狗今天又干了什么",
            "外卖红包又没了", "健身房办卡纠结", "隔壁邻居的八卦",
        };

        private static string DescribeTopic(Topic t, in CitySnapshot s, uint seed)
        {
            switch (t)
            {
                case Topic.Rain: return "正在下雨";
                case Topic.Snow: return "正在下雪";
                case Topic.HeatWave: return "高温天气";
                case Topic.HighUnemployment: return "失业率高，市民日子不好过";
                case Topic.HighHappiness: return "城市运转不错，市民心情好";
                case Topic.LowHappiness: return "市民心情低落";
                case Topic.TouristBoom: return "游客变多了";
                case Topic.Breaking: return "突发事件（见【突发事件】）";
                default: return "生活闲聊：" + k_DailyTopics[seed % k_DailyTopics.Length];
            }
        }

        private static string FuzzPeople(int n)
        {
            if (n < 1000) return n.ToString();
            if (n < 9500) return $"{n / 1000}千多";
            if (n < 10000) return "快一万";
            if (n < 95000) return $"{n / 10000}万多";
            if (n < 100000) return "快十万";
            return $"{n / 10000}来万";
        }

        /// <summary>三档定性：低于 lo 说"低"，高于 hi 说"高"，中间"一般"。</summary>
        private static string Qual(float v, float lo, float hi)
            => v < lo ? "低" : v > hi ? "高" : "一般";
    }
}
