using Colossal.UI.Binding;
using Game;
using Game.UI;

namespace CityLife.GameBridge
{
    /// <summary>
    /// M2 信息流面板数据桥 + M4 活动确认弹窗数据桥。
    ///
    /// 数据契约（与 UI 侧 src/CityLife/UI/src/mods/bindings.ts 一一对应）：
    /// - posts（ValueBinding&lt;string&gt;）：FeedStore.ToJson() 的产物（最新在后）；
    /// - uiLog（TriggerBinding&lt;string&gt;）：UI → C# 单向日志通道；
    /// - mayorPost（TriggerBinding&lt;string&gt;）：市长发帖——入信息流 + LiveContext + 市民回应炉 + M4 意图解析炉；
    /// - replyPost（TriggerBinding&lt;string&gt;）：市长回复市民帖（"seq|text"）；
    /// - panelState（TriggerBinding&lt;int&gt;）：面板开合（feedMode 联动）；
    /// - eventConfirm（ValueBinding&lt;string&gt;）：活动确认卡 JSON，空串=不显示（M4）；
    /// - eventConfirmResult（TriggerBinding&lt;string&gt;）：确认回执 "{id}:{1|0}"（M4）。
    ///
    /// 为什么脏检查：ToJson 每次全量序列化环形缓冲（最多 100 条），
    /// 逐帧无脑重推既浪费主线程又刷 binding 流量；Feed.Version 每次新增自增，
    /// 比对 Version 没变就直接跳过（FeedStore 设计即为此服务）。
    /// </summary>
    public partial class CityLifeUISystem : UISystemBase
    {
        /// <summary>只在游戏内挂 UI（主菜单/编辑器不推数据）。</summary>
        public override GameMode gameMode => GameMode.Game;

        private ValueBinding<string> m_PostsBinding = default!;
        private ValueBinding<string> m_EventConfirmBinding = default!;
        private int m_LastVersion = -1;
        private int m_LastConfirmVersion = -1;
        private Game.UI.InGame.ChirperUISystem? m_VanillaChirper;   // 显示闸（可关：无副作用实锤）
        private uint m_EnforceCounter;

        protected override void OnCreate()
        {
            base.OnCreate();
            // 先 ValueBinding 后 TriggerBinding（先例顺序；顺序本身无强制，保持范本形态便于对照）
            AddBinding(m_PostsBinding = new ValueBinding<string>("CityLife", "posts", "[]"));
            AddBinding(m_EventConfirmBinding = new ValueBinding<string>("CityLife", "eventConfirm", ""));
            AddBinding(new TriggerBinding<string>("CityLife", "uiLog", msg => Mod.Log.Info("[UI] " + msg)));
            AddBinding(new TriggerBinding<string>("CityLife", "mayorPost", OnMayorPost));
            AddBinding(new TriggerBinding<string>("CityLife", "replyPost", OnReplyPost));
            AddBinding(new TriggerBinding<string>("CityLife", "eventConfirmResult", OnEventConfirmResult));
            AddBinding(new TriggerBinding<int>("CityLife", "panelState", v => Content.LiveContext.PanelOpen = v == 1));

            // 关停原版 Chirper 显示闸。两条实机教训：
            // 1) 一次性关停无效——载入存档时游戏会按 gameMode 复活系统，必须持续执法（见 OnUpdate）；
            // 2) **生成闸 CreateChirpSystem 绝不可关**（2026-08-19 CRITICAL 实锤）：GetQueue() 有运行断言，
            //    LifePathEventSystem 等生产侧系统每帧调它，关了等于让游戏自己每帧抛 AssertionException。
            //    实体级过滤走发布侧补丁（VanillaChirpFilterPatch）。
            try { m_VanillaChirper = World.GetOrCreateSystemManaged<Game.UI.InGame.ChirperUISystem>(); }
            catch (System.Exception e) { Mod.Log.Warn($"[UI] 找不到 ChirperUISystem：{e.Message}"); }
        }

        /// <summary>市长发帖：清洗 → 入信息流 → LiveContext → 市民回应炉 + M4 意图解析炉。</summary>
        private void OnMayorPost(string text)
        {
            var t = (text ?? "").Trim().Replace("\n", " ").Replace("\r", " ");
            if (t.Length == 0)
                return;
            if (t.Length > 200)
                t = t.Substring(0, 200);

            var seq = Mod.Feed.Record(new Content.Post("市长", t, Content.Topic.Daily, "mayor"));
            Content.LiveContext.PublishMayor(t);
            Mod.Log.Info($"[UI] 市长发帖：{t.Substring(0, System.Math.Min(24, t.Length))}…");

            if (Mod.Gateway == null || Llm.CliGateway.Mute)
                return;

            // 市民回应炉（高优先级）：结果由导演按 requestId "mayor-reply:" 路由挂载
            if (ContentDirectorSystem.ReplyHead != null)
            {
                var count = 4 + (int)(seq % 4); // 4-7 条，别千篇整数
                Mod.Gateway.Enqueue(new Llm.CliRequest(
                    Content.PromptBuilder.BuildReplyPrompt(ContentDirectorSystem.ReplyHead, t, count),
                    Llm.CliPriority.High, 300, "mayor-reply:" + seq));
            }

            // M4 意图解析炉：命中事件包则进活动链（确认弹窗）；不命中静默走舆情层
            if (EventChainSystem.IntentHead != null)
            {
                Mod.Gateway.Enqueue(new Llm.CliRequest(
                    Content.PromptBuilder.BuildIntentPrompt(EventChainSystem.IntentHead, t),
                    Llm.CliPriority.Normal, 300, "intent:" + seq));
            }
        }

        /// <summary>市长下场回复市民的帖：直接挂到该帖评论串末尾（"市长"身份）。</summary>
        private void OnReplyPost(string payload)
        {
            // 契约："seq|text"（UI 已 trim/非空守卫；这里防御性再查）
            var sep = (payload ?? "").IndexOf('|');
            if (sep <= 0 || !uint.TryParse(payload.Substring(0, sep), out var seq))
                return;
            var t = payload.Substring(sep + 1).Trim();
            if (t.Length == 0)
                return;
            if (t.Length > 200)
                t = t.Substring(0, 200);

            if (Mod.Feed.AppendComment(seq, "市长", t))
                Mod.Log.Info($"[UI] 市长回复帖 #{seq}：{t.Substring(0, System.Math.Min(24, t.Length))}…");
        }

        /// <summary>活动确认回执："{id}:{1|0}" → 路由给活动链（id 不匹配的迟到回执丢弃）。</summary>
        private void OnEventConfirmResult(string payload)
        {
            var sep = (payload ?? "").IndexOf(':');
            if (sep <= 0 || !int.TryParse(payload.Substring(0, sep), out var id))
                return;
            var ok = payload.Substring(sep + 1) == "1";
            EventChainSystem.SetConfirmResult(id, ok);
        }

        protected override void OnUpdate()
        {
            // 显示闸持续执法：原版 Chirper 复活就按死（每 128 帧查一次，2 的幂）
            if (m_EnforceCounter++ % 128 == 0)
            {
                if (m_VanillaChirper != null && m_VanillaChirper.Enabled)
                {
                    m_VanillaChirper.Enabled = false;
                    Mod.Log.Info("[UI] 原版 Chirper 显示闸已关停");
                }
            }

            // 活动确认卡：版本变了才推（含清空）
            if (EventChainSystem.PendingConfirmVersion != m_LastConfirmVersion)
            {
                m_LastConfirmVersion = EventChainSystem.PendingConfirmVersion;
                m_EventConfirmBinding.Update(EventChainSystem.PendingConfirmJson);
            }

            // 脏检查：Feed 没新增就不序列化、不推送
            if (Mod.Feed.Version == m_LastVersion)
                return;
            m_LastVersion = Mod.Feed.Version;
            m_PostsBinding.Update(Mod.Feed.ToJson());
        }
    }
}
