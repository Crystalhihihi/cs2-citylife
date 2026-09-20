using System.Collections.Generic;
using System.Text;

namespace CityLife.Content
{
    /// <summary>
    /// prompt 组装器（缓存纪律 §4 M3 / §12 #18）：
    /// BuildHead 产出**逐字节稳定**的前缀（规则+卡库+形态库+输出 schema，禁时间戳/随机 ID），
    /// 启动时拼一次缓存复用；BuildBatch 把全部动态内容压在尾部。同前缀请求才吃得到服务端缓存。
    /// 内容纪律（2026-08-20 v2 治僵硬）：**正例 > 禁令**——模型镜像语域不遵守清单；
    /// 六条【样子】给语气，三条原则（别端别装/别空/别齐）守底线，分配行带【处境】给现场感。
    /// （调研依据 docs/spikes/2026-08-20-platform-styles.md）
    /// </summary>
    public static class PromptBuilder
    {
        /// <summary>固定前缀（规则 + 正例样子 + 三原则 + 风格卡库 + 形态库 + 评论分工 + 输出 schema）。卡册不变则逐字节稳定。</summary>
        public static string BuildHead(IReadOnlyList<Persona> cards)
        {
            var sb = new StringBuilder(2560);
            sb.Append("你是虚构城市社交平台\"市民圈\"的内容生成器。城市是模拟游戏里的虚构城市，一切内容虚构。\n");
            sb.Append("【铁律】对事不对人：可以吐槽政策、物价、交通、天气，绝不攻击市长本人或任何真实人物；不碰现实政治、种族、性别议题；不生成自伤内容；不使用真实名人、品牌、事件名。\n");
            sb.Append("【语言】简体中文口语，像真人刷手机随手发的：大部分一两句话（≤50字），偶尔可以稍长（硬上限80字）。会断句会重复，允许毛边，不用句句通顺完整。\n");
            // 正例 few-shot（2026-08-20 治僵硬主药：禁令十条不如样子六条——模型镜像语域不遵守清单）
            sb.Append("【样子】只学语气和松散度，内容和物件一律不许照抄：\n");
            sb.Append("- tieba：东站那家面馆又涨两块？16 一碗素面，老板是觉得咱工资跟房价一个涨法吗\n");
            sb.Append("- xhs：挖到宝了！城西菜市场最里头那家卤味，鸭翅十块四个，香到排队排上马路牙子\n");
            sb.Append("- worker：加班到九点，楼下便利店关东煮只剩萝卜。也行，萝卜也是肉\n");
            sb.Append("- dama：三号楼那个小年轻，垃圾又堆门口两天了！大妈先帮你倒了，下回注意啊\n");
            sb.Append("- weibo：解放路那个红绿灯，红灯九十秒绿灯十五秒，谁设计的，出来聊聊？\n");
            sb.Append("- zhihu：冷知识：高峰期公交车均速 14 公里，不如骑车。所以别骂司机了，骂路\n");
            // 禁令三合一（原十条禁令堆砌致模型"安全化"——2026-08-20 实机僵硬；事实锚定纪律保留）
            sb.Append("【别端别装】不端公文腔，不装（\"作为一名市民\"\"谢邀\"\"小编认为\"），不升华结尾，不喊\"你们怎么看\"。\n");
            sb.Append("【别空】每条必须有一个具体的东西：数字/店名/街道/物件（可虚构但要有生活质感），且别总拿同一类东西说事。\"早市韭菜两块五一斤\"算合格，\"菜价好便宜\"判零分。你只知道【城市此刻】里写了的事，别编精确统计和\"调查显示\"。\n");
            sb.Append("【别齐】一炉十条不能像一个妈生的：长短不一、句式不一，有的就扔下一句话。\n");
            sb.Append("【作者风格卡】按分配的 persona 严格模仿其语域，话题不受限制：\n");
            foreach (var c in cards)
                sb.Append("- ").Append(c.Id).Append('：').Append(c.Style).Append('\n');
            sb.Append("【形态】按分配的形态写，骨架：\n");
            foreach (var f in PostForms.All)
                sb.Append("- ").Append(f.Id).Append('：').Append(f.Skeleton).Append('\n');
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
        /// 商家广告炉的固定前缀（广告层 v1：纯舆情+氛围，不承诺经济效果——四轮购物 spike 实锤后定案）。
        /// 缓存纪律同主头——启动后首发时拼一次（ShopAdSystem 静态缓存）。
        /// </summary>
        public static string BuildShopAdHead()
        {
            var sb = new StringBuilder(512);
            sb.Append("你是虚构城市社交平台\"市民圈\"里一家本地小店的账号（老板自己发的）。城市是模拟游戏里的虚构城市，一切内容虚构。\n");
            sb.Append("【铁律】不碰现实政治、真实名人、品牌、事件名。\n");
            sb.Append("【体裁】小店广告：像朋友圈/同城号里小店老板亲手发的，≤60字，诚恳不端着，可以有具体价格/折扣/时段（虚构但合理）；禁公文腔，不用 emoji，不吹\"全市最好\"式空话。\n");
            sb.Append("【输出】只输出广告正文本身，禁止任何其他字符（不要引号、不要 JSON、不要前缀）。\n");
            return sb.ToString();
        }

        /// <summary>商家广告完整 prompt = 广告头 + 店铺（名+业态）+ 事由。</summary>
        public static string BuildShopAdPrompt(string adHead, string name, string word, string reason)
            => adHead + "【店铺】" + name + "（" + word + "）【事由】" + reason + "\n写一条广告。\n";

        /// <summary>
        /// 话题创建炉的固定前缀（S3，§12 #48 水位触发段）：启动时拼一次缓存复用（缓存纪律同主头，逐字节稳定）。
        /// 角色=虚构城市居民闲聊话题策划；内容纪律守 #23 红线（无专名/无政治/无现实热点）。
        /// 输出 schema 与社区 topics.jsonl 完全同构——解析直接复用 TopicReservoir 的装载代码路径。
        /// </summary>
        public static string BuildTopicForgeHead()
        {
            var sb = new StringBuilder(768);
            sb.Append("你是虚构城市\"市民圈\"的话题策划，给居民闲聊出话题。城市是模拟游戏里的虚构城市，一切内容虚构。\n");
            sb.Append("【铁律】话题要生活化、口语化，是居民随口能接的日常题；不使用真实名人、品牌、地名、事件名；不碰现实政治、种族、性别议题；不追现实热点——写虚构城市里永远不过时的日常。\n");
            sb.Append("【覆盖】分区尽量铺开：美食/通勤/职场/家里/萌宠/消费/娱乐/沙雕轮着来，也可开新分区（衣着、住房、出行、八卦、工作、宠物……），别扎堆。\n");
            sb.Append("【规格】每条 ≤20 字，是一句居民能接话的题目（\"夜宵哪家强\"\"停车又绕了三圈\"这种），不是新闻标题，不要访谈腔。\n");
            sb.Append("【输出】只输出 JSONL：一行一条 {\"zone\":\"分区\",\"topic\":\"话题\",\"tags\":[\"场合标签，可空\"]}，禁止输出任何其他字符（不要 markdown 围栏、不要解释、不要序号）。\n");
            return sb.ToString();
        }

        /// <summary>
        /// 话题炉完整 prompt = 话题头 + 动态尾：城市此刻 + 已有话题抽样（禁重复反馈）+ 条数任务。
        /// existing 是执行层确定性抽样（TopicReservoir.SampleForPrompt，~20 条）；count 由水位调用方定（30-50）。
        /// </summary>
        public static string BuildTopicForgePrompt(string head, in CitySnapshot s, IReadOnlyList<string> existing, int count)
        {
            var sb = new StringBuilder(head.Length + 512);
            sb.Append(head);
            sb.Append("【城市此刻】").Append(DescribeCity(s)).Append('\n');
            if (existing.Count > 0)
            {
                sb.Append("【已有话题，禁止重复或换汤不换药】\n");
                foreach (var t in existing)
                    sb.Append("- ").Append(t).Append('\n');
            }
            sb.Append("【任务】写 ").Append(count).Append(" 条新话题。\n");
            return sb.ToString();
        }

        /// <summary>
        /// 闲聊炉的固定前缀（S4，§12 #48 双炉定案）：城市街头路人的嘴替——**张嘴说话不是发帖**。
        /// 缓存纪律同主头（BubbleChatterSystem.OnCreate 拼一次复用，逐字节稳定：禁时间戳/随机内容，动态全压尾部）。
        /// 语域定案（§12 #60 刀④长短句规格）：每卡至少一短（≤15字纯反应）一长（20-40字带信息骨架——
        /// 数字/物件/店名/价签，主头【别空】纪律下沉）；禁 hashtag/禁@/禁"家人们"直播腔。
        /// §12 #69（2026-09-16 玩家拍板，三变体实验定案）：<b>【样子】内容例句整段移除</b>——实验实锤
        /// 例句镜像率本就极低（0.01-0.02），但它是隐性句长锚（纯删塌 71% 短句）；长短句引导改由
        /// 系统分配规格签承担（卡尾"｜写：｜情："，ChatterSpec 执行层抽签，LLM 只服从）。
        /// §12 #63 对话场景卡：卡文带"｜对："的是双人卡（两人正走在一起），输出 {"card":N,"a":..,"b":..}
        /// 各 ≤12 字的来回对话，名字前缀执行层收炉拼装（LLM 不碰名字，单向阀门）。
        /// 【输出样子】行（2026-09-15 快轨强化）：V3 无 thinking 对"只有对卡才 a/b、每对卡恰好 1 条"
        /// 跟随不稳（实机漏产/超产实锤）——few-shot 样子行+红线明示，例句中性防镜像照抄（#61 教训）。
        /// 此行是 JSONL 格式锚，§12 #69 明确保留不动（与内容例句不同类——格式锚是 schema 跟随救命索）。
        /// 场合不再由模型判（§12 #60 刀① Plan B）：乘车/在建筑/走路是采样时已确定事实，
        /// CitizenPoolSystem 组卡时执行层盖章；模型只报 card 归属，收炉按 card 回填场合。
        /// 【名字】规则（§12 #62）：「」内才是真实场所名可念；无括号的场所词只是业态类别，禁止当店名念
        /// ——治实机"功能区标签（北美低密度商业）被当店名念"（执行层已斩断来源，此行是防残余双保险）。
        /// </summary>
        public static string BuildChatterHead()
        {
            var sb = new StringBuilder(896);
            sb.Append("你是虚构城市街头路人的嘴替——把路人此刻嘴里嘟囔的话写出来。城市是模拟游戏里的虚构城市，一切内容虚构。\n");
            sb.Append("【铁律】对事不对人：可以吐槽天气、通勤、物价，绝不攻击市长本人或任何真实人物；不碰现实政治、种族、性别议题；不生成自伤内容；不使用真实名人、品牌、事件名。\n");
            sb.Append("【语域】张嘴说话，不是发帖：第一人称随口一句，像走在路上/坐在车里/待在屋里说给身边人听的；短句 ≤15 字（纯反应，脱口而出），长句 20-40 字（必须带一件具体的事：数字/物件/店名/价签，可虚构但要有生活质感、别和处境卡矛盾）；禁止 hashtag、禁止@、禁止\"家人们\"等直播腔、禁止 emoji、禁止书面腔。\n");
            // §12 #69：【样子】内容例句段已移除（实验定案——镜像不实锤，句长锚功能移交系统规格签）；
            // 规则段（铁律/语域/写法/配额）全部保留（防 AI 腔的兜底，实验零命中验证）
            sb.Append("【写法】每张处境卡写 2-3 条（至少一短一长）：就照这个人的处境和配给他的话头写，必须是从这个人嘴里能说出来的话；每条必须带 card 标明出自哪张处境卡（1 起）。可以顺势吐槽【城市此刻】里的天气/通勤/物价或【城里最近在传】里的事。卡里若带\"｜旁边：\"（S6 环境圈摘要），是这人边上此刻真实有的东西，可以顺手当话料，没有就是没有。\n");
            sb.Append("【名字】卡里和\"｜旁边：\"摘要里「」内才是真实场所名（店名/地名），可以点名吐槽；没加「」的场所词（商店/工厂/住宅区/办公楼…）只是业态类别不是名字，禁止当店名念——别编全名、别加引号，要提就说\"那家店\"\"这附近\"。\n");
            sb.Append("【配额】萌宠题材（猫/狗/宠物）全炉至多 1 条——它最安全最容易写滥，写超判废。\n");
            // §12 #63 对话场景卡小节（语域措辞借 BuildTheaterStockHead 当面聊天段：有来有回、别喊名字别自称）。
            // 2026-09-15 快轨实锤强化（V3 无 thinking 漏产/超产）：加 JSONL 输出样子（few-shot 对无 thinking
            // 最有效；例句用中性通用句——#61 教训样子会被镜像照抄，内容必须即使照抄也不出戏）+ 两条红线明示。
            sb.Append("【对话】卡文带\"｜对：\"的是双人卡（\"｜对：\"后是另一人的处境）：两人对话一句起一句应，像街坊擦肩接话，a/b 各 ≤12 字，可聊两人的处境/旁边的东西/题里的话头；台词禁喊名字禁自称（显示名系统自动加）、禁动作神态描写，只写说出口的话。\n");
            sb.Append("【输出样子】独白卡→{\"text\":\"这儿风真大\",\"card\":2}；双人卡→{\"card\":5,\"a\":\"你也走这条路啊\",\"b\":\"对，天天走\"}。红线：只有带\"｜对：\"的卡才用 a/b，其余卡只许 {\"text\":..} 独白；每张双人卡必须恰好写 1 条 a/b 行且 card 必须等于它的卡号——双人卡不写 a/b、写多条、a/b 行缺 card 或 card 指错卡，全部判废丢弃。\n");
            sb.Append("【输出】只输出 JSONL，一行一条，格式照【输出样子】；禁止 markdown 围栏、禁止解释、禁止序号。\n");
            return sb.ToString();
        }

        /// <summary>
        /// 闲聊炉完整 prompt = 闲聊头 + 动态尾：【城市此刻】（DescribeCity 同款，别重复造）
        /// + 处境卡（每张配一题；§12 #69 起卡尾带"｜写：｜情："系统规格签——BubbleChatterSystem 组卡时
        /// ChatterSpec 抽签缀上）+【规格】段（卡带签才出现——无签卡具/旧回归路径不出这段，向后兼容）
        /// + 条数任务。cards/topics 等长对齐（BubbleChatterSystem 从
        /// CitizenPoolSystem.Entries 抽样 + TopicReservoir.TopicFor 配题）。
        /// pairCardNos=双人卡卡号列表（§12 #63：卡文已带"｜对："段；2026-09-15 快轨强化——V3 无
        /// thinking 认不出"哪张是双人卡"致漏产，段头+任务行显式点名卡号双重锚定，消除识别负担）。
        /// </summary>
        public static string BuildChatterPrompt(string head, in CitySnapshot s,
                                                IReadOnlyList<string> cards, IReadOnlyList<string> topics,
                                                IReadOnlyList<string>? rumors = null,
                                                IReadOnlyList<int>? pairCardNos = null)
        {
            var pairCards = pairCardNos?.Count ?? 0;
            // §12 #69：卡带系统规格签（"｜写："）才补规格说明——探测现成的卡文本，不动签名（无签卡具=旧回归路径）
            var hasSpec = false;
            var hasScene = false; // §12 #71：卡带场景签（"｜景："）才补场景语义说明
            var hasVoice = false; // §12 #72：卡带口吻签（"｜口："）才补口气说明
            for (var i = 0; i < cards.Count; i++)
            {
                if (cards[i].Contains("｜写：")) hasSpec = true;
                if (cards[i].Contains("｜景：")) hasScene = true;
                if (cards[i].Contains("｜口：")) hasVoice = true;
            }
            var sb = new StringBuilder(head.Length + 512);
            sb.Append(head);
            sb.Append("【城市此刻】").Append(DescribeCity(s)).Append('\n');
            AppendRumors(sb, rumors, "可以当话料（别逐字复读，别每条都蹭）");
            sb.Append("【处境卡】一行一张（真实市民此刻的状态），\"｜题：\"后是配给这人的话头");
            sb.Append("；卡里场所词已细分到真实业态（餐饮店/软件公司/服装厂这类——§12 #62），话题贴着这个人的场所业态写，别把餐饮店和学校写成一个味儿");
            if (hasSpec)
                sb.Append("；\"｜写：\"是系统分配的句型规格、\"｜情：\"是情绪底色（必须服从）");
            if (hasVoice)
                sb.Append("，\"｜口：\"是这人的说话口气（必须服从，但别每条都硬凹同一个把戏，自然为主）"); // §12 #72（沿 voicevar 实验措辞转正）
            if (hasScene)
                sb.Append("；\"｜景：\"/\"｜话核：\"是场景签（口径见【场景】段，带签必须服从）"); // §12 #71 措辞优化（2026-09-20 实验定案：头部只留短指针，释义归独立【场景】段）
            if (pairCards > 0)
                sb.Append("，\"｜对：\"后是正和这人走在一起的另一人（双人卡=卡 ").Append(string.Join("、", pairCardNos!)).Append("）");
            sb.Append("：\n");
            for (int i = 0; i < cards.Count; i++)
            {
                sb.Append(i + 1).Append(". ").Append(cards[i]);
                if (i < topics.Count && topics[i].Length > 0)
                    sb.Append("｜题：").Append(topics[i]);
                sb.Append('\n');
            }
            if (hasScene)
                sb.Append("【场景】带\"｜景：\"的卡：这人此刻就在那个场所里，话要从那个场所里长出来——在那办事、等人、干活、陪人，说的就是那地方的事。\"｜话核：\"是这地方人们嘴边的事，顺着它自然聊，别逐字复读、别当成必答清单。没\"｜景：\"的卡自由发挥。\n"); // §12 #71 措辞优化（2026-09-20 三变体实验 V1 胜者：无场景例名防镜像带偏、话核用法说清、反通用线已实锤删）
            if (hasSpec)
                sb.Append("【规格】句型三档：短=≤12字脱口而出；中=13-25字一句话说清；长=26-40字带一件具体的事。规格必须服从，规格之外自由发挥。\n"); // §12 #72（句型签 6→3——带数字/物件/因果强制签退役，具体感归闲聊头【语域】）
            var monos = cards.Count - pairCards;
            // 按卡号顺序逐卡写（2026-09-15 快轨强化：顺序锚定治 V3 无 thinking 漏卡——跳卡是漏产首因）
            sb.Append("【任务】按卡号顺序逐卡写：独白卡每张写 2-3 条（至少一短一长，长句 20-40 字带一件具体的事）");
            if (pairCards > 0)
                sb.Append("；卡 ").Append(string.Join("、", pairCardNos!))
                  .Append(" 是双人卡，每张必须写恰好 1 条 {\"card\":卡号,\"a\":..,\"b\":..} 对话");
            sb.Append("，共 ").Append(monos * 2 + pairCards).Append('-').Append(monos * 3 + pairCards)
              .Append(" 条；哪张卡的话用完就换下一张，别复读。\n");
            return sb.ToString();
        }

        /// <summary>
        /// 事件现场围观炉的固定前缀（§12 #70 事件现场反应层）：现场路人的嘴替——**惊呼不是发帖也不是闲聊**。
        /// 缓存纪律同闲聊头（EventSceneSystem.OnCreate 拼一次复用，逐字节稳定：动态全压尾部）。
        /// 无内容例句（#69 同哲学：规则段兜底防 AI 腔， diversity 靠处境事实+情绪子集）；【输出】JSONL
        /// 格式行保留（快轨 schema 跟随救命索）。输出 schema：一行一条 {"text":"话"}——与
        /// BubbleSnippetPool.ParseBatch 解析口径同炉改（card 缺失落 Any 不连坐，反应卡无卡号概念）。
        /// </summary>
        public static string BuildEventReactionHead()
        {
            var sb = new StringBuilder(512);
            sb.Append("你是虚构城市事件现场的路人嘴替——把围观者此刻脱口而出的话写出来。城市是模拟游戏里的虚构城市，一切内容虚构。\n");
            sb.Append("【铁律】对事不对人：可以惊呼、吐槽、同情、议论，绝不攻击任何具体个人；不碰现实政治、种族、性别议题；不生成自伤内容；不使用真实名人、品牌、事件名；别写见死不救式的冷血（可以抱怨添堵/心疼当事人，别诅咒）。\n");
            sb.Append("【语域】现场惊呼，不是发帖也不是闲聊：脱口而出的短句 ≤15 字为主（喊出来的），长句 20-30 字必须带一件现场细节（烟/警笛/残骸/排队/味道）；第一人称；禁止 hashtag、禁止@、禁止\"家人们\"等直播腔、禁止 emoji、禁止书面腔、禁止动作神态描写（\"捂着嘴\"\"指着\"这类一律不要）。\n");
            sb.Append("【输出】只输出 JSONL：一行一条 {\"text\":\"话\"}；禁止 markdown 围栏、禁止解释、禁止序号。\n");
            return sb.ToString();
        }

        /// <summary>
        /// 事件围观炉完整 prompt = 围观头 + 动态尾：【事件】（类型+地点+烈度档词，执行层组装的事实，LLM 只写反应）
        /// +【情绪】（系统分配的围观情绪子集签 2-3 个——至少一半条目贴底色，§12 #70 情绪处境子集化）
        /// + 条数任务。sceneDesc=执行层组装的一行事件事实（如"「和平路」路口两车相撞，挺严重，警车已到"）。
        /// </summary>
        public static string BuildEventReactionPrompt(string head, string sceneDesc,
                                                      IReadOnlyList<string> moods, int count)
        {
            var sb = new StringBuilder(head.Length + 256);
            sb.Append(head);
            sb.Append("【事件】").Append(sceneDesc).Append("——就发生在这群路人眼前。\n");
            if (moods.Count > 0)
            {
                sb.Append("【情绪】现场情绪的底色是：");
                for (var i = 0; i < moods.Count; i++)
                {
                    if (i > 0)
                        sb.Append('、');
                    sb.Append(moods[i]);
                }
                sb.Append("——至少一半条目贴这几种底色，其余自由。\n");
            }
            sb.Append("【任务】写 ").Append(count).Append(" 条围观者的话：一条一个声音，别复读；可以心疼当事人、抱怨添堵、看热闹议论、提醒别人避让；别写成新闻播报、别复述事件本身。\n");
            return sb.ToString();
        }

        /// <summary>【城里最近在传】段（§12 #60 刀②城市记忆）：活力的本质=这座城市有昨天。两炉同款，一处组装。</summary>
        private static void AppendRumors(StringBuilder sb, IReadOnlyList<string>? rumors, string usage)
        {
            if (rumors == null || rumors.Count == 0)
                return;
            sb.Append("【城里最近在传】最近城里真发生的事，").Append(usage).Append("：\n");
            foreach (var r in rumors)
                sb.Append("- ").Append(r).Append('\n');
        }

        /// <summary>
        /// 小剧场剧本炉的固定前缀（§12 #52 炉→池→放送，推翻 #48 播前绑定段）：写的是**库存剧本**——
        /// 不针对具体市民，产出入 TheaterScriptStock 池，放送时才绑当时在场的真人演。
        /// 缓存纪律同主头（BubbleTheaterSystem.OnCreate 拼一次复用，逐字节稳定：动态全压尾部）。
        /// 语域=几人当面聊天（接话、有来有回、口语、每条 ≤20 字、禁 hashtag、禁旁白描写）。
        /// 输出 schema：一行一整部 {"scene":"station|park|shop|home|window|street","cast":2,"lines":[{"speaker":1,"text":"…"}]}
        /// ——与 TheaterScriptStock.ParseBatch 的解析口径同炉改（扩展纪律见 TheaterScriptStock 头注释）。
        /// </summary>
        public static string BuildTheaterStockHead()
        {
            var sb = new StringBuilder(1024);
            sb.Append("你是虚构城市的短剧编剧——给城市各处随时会发生的路人聊天写迷你剧本库存，之后由当时在场的市民临场演出。城市是模拟游戏里的虚构城市，一切内容虚构。\n");
            sb.Append("【铁律】对事不对人：可以吐槽天气、通勤、物价、排队，绝不攻击市长本人或任何真实人物；不碰现实政治、种族、性别议题；不生成自伤内容；不使用真实名人、品牌、事件名。\n");
            sb.Append("【语域】当面聊天，不是发帖也不是独白：有来有回（一句问一句答、一句吐槽一句跟），口语短句，每条≤20字，越短越像越好；禁止 hashtag、禁止@、禁止\"家人们\"等直播腔、禁止 emoji、禁止书面腔；只写说出口的话，禁止动作/神态/旁白描写（\"笑了笑\"\"指着远处\"这类一律不要）。\n");
            sb.Append("【场景】每部发生在一类场所，scene 只能填这六值：station=车站候车（聊等车/车次/晚点）、park=公园或景点（聊风景/拍照/溜达）、shop=商店里（聊商品/价格/排队结账）、home=住宅里（聊邻里/家务/房租）、street=街头闲谈（路上行人偶遇搭话：熟人打招呼/问路/唠家常均可，cast 2-5 人可变——别每部都顶格，lines 2-12 句可变）、window=窗口混编（路人经过店门口/人家窗前，指点或搭话一句，里面人——店员或住户——接一句；cast 恒 2、lines 恒 2，speaker 1=路人、speaker 2=屋里人）。\n");
            sb.Append("【样子】只学语气和松散度，内容和物件一律不许照抄：\n");
            sb.Append("- station：\"这趟车又晚了吧\"\"可不，都过去两趟了\"\n");
            sb.Append("- park：\"这花开得还行\"\"拍一张拍一张\"\n");
            sb.Append("- shop：\"鸡蛋又涨五毛\"\"那也得买，娃要吃\"\n");
            sb.Append("- street：\"哟，买菜去啊\"\"嗯，今儿豆角便宜\"\n");
            sb.Append("- window：\"哟，这窗台的花养得真好\"\"是吧，天天浇水呢\"\n");
            sb.Append("【写法】cast=这部几个人说（2 或 3，street 2-5 可变，window 恒 2 人 2 句）；speaker=角色序号（1 起，不超过 cast）；别一个人连说三句；允许话没接完、允许岔开题，别像开会轮流发言那么齐。角色只有序号没有身份——剧本会绑给当时在场的任意市民，禁止写真名、真店名、具体住址。【灵感】卡只是氛围参考，禁止照抄卡里的人名/店名/原话。\n");
            sb.Append("【占位】在地具体感靠占位符（放送时填真人真名），别编造：{place}=这家店/这个家（填场景真名）、{name1}~{name5}=第几个角色（填真人名，不超过 cast）。shop/home/window 每部至少 1 处 {place}（店名/家名是这场戏的地利）；station/park/street 是路人局——别叫名字、可不用占位；{nameN} 只给熟人局（家人/同事/邻居）。自称红线：speaker N 的台词里禁用 {nameN}——名字叫的是别人，speaker 1 的台词用 {name1}=自己喊自己，出戏判废。\n");
            sb.Append("【占位样子】shop：\"你们{place}还招人吗\"\"招是招，就是累\"｜home：\"{name2}，垃圾又堆门口两天了\"\"这就倒这就倒\"｜window：\"{place}窗台的花养得真好\"\"是吧，天天浇水呢\"\n");
            sb.Append("【输出】只输出 JSONL：一行一整部 {\"scene\":\"station\",\"cast\":2,\"lines\":[{\"speaker\":1,\"text\":\"话\"},{\"speaker\":2,\"text\":\"话\"}]}；禁止 markdown 围栏、禁止解释、禁止序号、禁止任何其他字符。\n");
            return sb.ToString();
        }

        /// <summary>
        /// 剧本炉完整 prompt = 剧本头 + 动态尾：【城市此刻】（DescribeCity 同款，别重复造）
        /// +【库存】（各场景标签现存部数——迭代 TheaterScriptStock.Scenes 白名单，执行层确定性计数）
        /// +【灵感】处境卡（真实市民此刻状态抽样，只借氛围，禁真名真店名）
        /// + 部数任务（缺口分区定向补给，2026-09-16 修正注记挂 §12 #67：炉只由分区水位触发，deficits 恒非空——
        /// 配额定向给缺口分区；window/street 在缺口中时至少各 1 部，它俩最高频）。
        /// streetMaxCast=街头闲谈人数上限（§12 #67 设置页值进动态尾——头部逐字节稳定纪律不动，设置值天然是动态量）。
        /// </summary>
        public static string BuildTheaterStockPrompt(string head, in CitySnapshot s, TheaterScriptStock stock,
                                                     IReadOnlyList<string> inspiration, int count, int streetMaxCast,
                                                     IReadOnlyList<string> deficits)
        {
            var sb = new StringBuilder(head.Length + 512);
            sb.Append(head);
            sb.Append("【城市此刻】").Append(DescribeCity(s)).Append('\n');
            sb.Append("【库存】现存：");
            for (var i = 0; i < TheaterScriptStock.Scenes.Length; i++)
            {
                if (i > 0)
                    sb.Append('，');
                var sc = TheaterScriptStock.Scenes[i];
                sb.Append(sc).Append(' ').Append(stock.CountOf(sc)).Append(" 部");
            }
            sb.Append("。\n");
            if (inspiration.Count > 0)
            {
                sb.Append("【灵感】此刻真实市民的状态（只借氛围，禁止照抄人名/店名/原话，禁止把卡里的人写进剧本）：\n");
                foreach (var c in inspiration)
                    sb.Append("- ").Append(c).Append('\n');
            }
            sb.Append("【任务】写 ").Append(count).Append(" 部新剧本，一行一部；每部 6-8 句（street 2-12 句可变、别每部都顶格；window 例外：固定 2 句——speaker 1 路人一句、speaker 2 屋里人接一句）。");
            if (deficits.Count > 0)
            {
                sb.Append("库存见底的分区：");
                for (var i = 0; i < deficits.Count; i++)
                {
                    if (i > 0)
                        sb.Append('、');
                    sb.Append(deficits[i]);
                }
                sb.Append("——只给这些分区写，每区至少 1 部；window/street 若在列必须各至少 1 部（它俩最高频），写不下时 window/street 优先。");
            }
            sb.Append("street 人数最多 ").Append(streetMaxCast).Append(" 人（当前玩家设置上限，超出的人数现场演不了）。\n");
            return sb.ToString();
        }

        /// <summary>
        /// 市长回应炉的固定前缀（M2-C 追加）：与主头同一套缓存纪律——启动时拼一次复用。
        /// 只写评论，卡库共享主卡册（评论的 persona 任选）。
        /// </summary>
        public static string BuildReplyHead(IReadOnlyList<Persona> cards)
        {
            var sb = new StringBuilder(2048);
            sb.Append("你是虚构城市社交平台\"市民圈\"的市民。城市是模拟游戏里的虚构城市，一切内容虚构。\n");
            sb.Append("【铁律】对事不对人：可以夸可以怼市长说的话，但绝不攻击其本人或任何真实人物；不碰现实政治、种族、性别议题；不生成自伤内容；不使用真实名人、品牌、事件名。\n");
            sb.Append("【语言】简体中文口语，每条不超过40字，像真人刷评论——短、碎、允许毛边。\n");
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
        /// 完整批量 prompt = 稳定头 + 动态尾。动态部分：城市此刻/传闻/本轮话题/分配/锚点/前情/突发/市长说/已发禁重复。
        /// recent = 最近已发正文（去重反馈）；seed = 处境池轮换（Daily 话题抽题已迁 TopicReservoir）；anchors/prevPosts 与 assigned 等长对齐（可空）；
        /// breaking = 突发事件文本（非空则本炉是热议串）；mayorContext = 市长发言（市民回应用，写回是 M4）；
        /// rumors = 城市传闻榜最新条目（§12 #60 刀②城市记忆，可空）。
        /// </summary>
        public static string BuildBatch(string head, in CitySnapshot s, Topic topic,
                                        IReadOnlyList<Assignment> assigned, IReadOnlyList<string> recent, uint seed,
                                        IReadOnlyList<string?>? anchors = null,
                                        IReadOnlyList<string?>? prevPosts = null,
                                        string? breaking = null, string? mayorContext = null,
                                        string? eventOutcome = null, string? ongoingEvent = null,
                                        string? petition = null, string? petitionResolved = null,
                                        IReadOnlyList<string?>? contexts = null,
                                        IReadOnlyList<string?>? topics = null,
                                        IReadOnlyList<string>? rumors = null)
        {
            var sb = new StringBuilder(head.Length + 896);
            sb.Append(head);
            sb.Append("【城市此刻】").Append(DescribeCity(s)).Append('\n');
            AppendRumors(sb, rumors, "帖子和评论可以当话料（别逐字复读，别每帖都蹭）");
            sb.Append("【本轮话题】").Append(DescribeTopic(topic, s, seed)).Append('\n');
            sb.Append("【分配】").Append(assigned.Count).Append(" 条：");
            var anyTopic = false;
            for (int i = 0; i < assigned.Count; i++)
            {
                sb.Append(i + 1).Append('=').Append(assigned[i].Persona.Id)
                  .Append('/').Append(assigned[i].Form.Id)
                  .Append('×').Append(assigned[i].CommentTarget).Append("评");
                // 处境：市民语境池（真实市民的当下）优先，池空回退罐头处境池
                var ctx = contexts != null && i < contexts.Count ? contexts[i] : null;
                sb.Append('（').Append(ctx ?? k_Contexts[(int)((seed + (uint)i) % (uint)k_Contexts.Length)]).Append('）');
                // 话题：每席位独立抽题（TopicReservoir 分区制——真实社区一帖一题，2026-08-20 玩家定案）
                var slotTopic = topics != null && i < topics.Count ? topics[i] : null;
                if (!string.IsNullOrEmpty(slotTopic))
                {
                    sb.Append('（').Append("题：").Append(slotTopic).Append('）');
                    anyTopic = true;
                }
                var anchor = anchors != null && i < anchors.Count ? anchors[i] : null;
                if (!string.IsNullOrEmpty(anchor))
                    sb.Append('（').Append("锚：").Append(anchor).Append('）');
                sb.Append(' ');
            }
            sb.Append('\n');
            sb.Append("【处境】分配里（…）括注是发帖人此刻的真实状态（谁、在干嘛——真实市民采样）。就照这个人的处境写，别解释别介绍。\n");
            if (anyTopic)
                sb.Append("【话题】带（题：…）的帖子写自己那题，各写各的，别串题。\n");
            if (anchors != null)
                sb.Append("【锚点】带（锚：…）的帖子围绕那个具体对象写（可一笔带过，别编与它矛盾的细节）；锚点名是真实店名/地名，可以点名，但别逐字复读整串（自然提到就行）。没带的自由发挥。\n");
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
            if (!string.IsNullOrEmpty(petition))
                sb.Append("【民意沸腾】市民对").Append(petition)
                  .Append("强烈不满，正在联署要求市长回应。本炉是热议帖：帖子带怨气（对事不对人，别攻击市长本人），评论多站队吵起来。\n");
            if (!string.IsNullOrEmpty(petitionResolved))
                sb.Append("【请愿后续】").Append(petitionResolved)
                  .Append("。市民还在议论这事（有人满意有人继续怼，别一边倒）。\n");
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

        // Daily 话题分区池已迁入 TopicReservoir（S2，§12 #48 社区拓展段）：内置货架原样搬迁一座不动，
        // 社区 topics.jsonl 追加/replace、生成源新鲜度衰减都在那里；每席位抽题走 TopicReservoir.TopicFor(batch, slot)

        // 处境池（2026-08-20 治僵硬第二刀：模型"凭空发帖"必僵——给个此刻状态就有现场感）；
        // 执行层按 seed+席位轮换，与话题/锚点解耦——锚点管"说什么"，处境管"在干嘛说"
        private static readonly string[] k_Contexts =
        {
            "刚下班", "排队中", "躺床上刷手机", "上班摸鱼", "等公交",
            "遛弯", "蹲坑刷手机", "哄娃睡觉中", "吃夜宵", "摸鱼上厕所",
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
                case Topic.Petition: return "民意沸腾（见【民意沸腾】）";
                default: return "生活闲聊，各帖话题见分配行（题：…）";
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
