using Colossal.UI.Binding;
using Game;
using Game.UI;

namespace CityLife.GameBridge
{
    /// <summary>
    /// M2 信息流面板数据桥：把 <see cref="Content.FeedStore"/> 的帖子 JSON 经 binding 推给 cohtml/React 面板。
    ///
    /// 数据契约（与 UI 侧 src/CityLife/UI/src/mods/bindings.ts 一一对应）：
    /// - group 固定 "CityLife"；
    /// - posts（ValueBinding&lt;string&gt;）：FeedStore.ToJson() 的产物（最新在后）；
    /// - uiLog（TriggerBinding&lt;string&gt;）：UI → C# 单向日志通道；
    /// - mayorPost（TriggerBinding&lt;string&gt;）：市长发帖（M2-C）——写入信息流 + LiveContext，
    ///   随后 3 炉的 prompt 带【市长说】上下文让市民回应；意图→游戏效果是 M4 写回层的事。
    ///
    /// 原版 Chirper 双闸关停（M2-C 收尾，2026-08-19）：
    /// 显示闸 ChirperUISystem + 生成闸 Game.Triggers.CreateChirpSystem（dump 实测）。
    /// 生成闸一关 chirp 实体根本不产生——比 CustomChirps 的发布侧过滤更上游，CustomChirps 从此完全可选。
    /// 注意必须**持续执法**：载入存档时游戏会按 gameMode 重排系统复活它们（实机踩坑）。
    /// </summary>
    public partial class CityLifeUISystem : UISystemBase
    {
        /// <summary>只在游戏内挂 UI（主菜单/编辑器不推数据）。</summary>
        public override GameMode gameMode => GameMode.Game;

        private ValueBinding<string> m_PostsBinding = default!;
        private int m_LastVersion = -1;
        private Game.UI.InGame.ChirperUISystem? m_VanillaChirper;   // 显示闸
        private Game.Triggers.CreateChirpSystem? m_ChirpFactory;    // 生成闸
        private uint m_EnforceCounter;

        protected override void OnCreate()
        {
            base.OnCreate();
            // 先 ValueBinding 后 TriggerBinding（先例顺序；顺序本身无强制，保持范本形态便于对照）
            AddBinding(m_PostsBinding = new ValueBinding<string>("CityLife", "posts", "[]"));
            AddBinding(new TriggerBinding<string>("CityLife", "uiLog", msg => Mod.Log.Info("[UI] " + msg)));
            AddBinding(new TriggerBinding<string>("CityLife", "mayorPost", OnMayorPost));
            AddBinding(new TriggerBinding<int>("CityLife", "panelState", v => Content.LiveContext.PanelOpen = v == 1));

            // 双闸引用（找不到=版本变动，仅警告不影响其他功能）
            try { m_VanillaChirper = World.GetOrCreateSystemManaged<Game.UI.InGame.ChirperUISystem>(); }
            catch (System.Exception e) { Mod.Log.Warn($"[UI] 找不到 ChirperUISystem：{e.Message}"); }
            try { m_ChirpFactory = World.GetOrCreateSystemManaged<Game.Triggers.CreateChirpSystem>(); }
            catch (System.Exception e) { Mod.Log.Warn($"[UI] 找不到 CreateChirpSystem：{e.Message}"); }
        }

        /// <summary>市长发帖：清洗 → 入信息流 → 存 LiveContext（下几炉市民会回应）。</summary>
        private void OnMayorPost(string text)
        {
            var t = (text ?? "").Trim().Replace("\n", " ").Replace("\r", " ");
            if (t.Length == 0)
                return;
            if (t.Length > 200)
                t = t.Substring(0, 200);

            Mod.Feed.Record(new Content.Post("市长", t, Content.Topic.Daily, "mayor"));
            Content.LiveContext.PublishMayor(t);
            Mod.Log.Info($"[UI] 市长发帖：{t.Substring(0, System.Math.Min(24, t.Length))}…");
        }

        protected override void OnUpdate()
        {
            // 双闸持续执法：原版 Chirper 复活就按死（每 128 帧查一次，2 的幂）
            if (m_EnforceCounter++ % 128 == 0)
            {
                if (m_VanillaChirper != null && m_VanillaChirper.Enabled)
                {
                    m_VanillaChirper.Enabled = false;
                    Mod.Log.Info("[UI] 原版 Chirper 显示闸已关停");
                }
                if (m_ChirpFactory != null && m_ChirpFactory.Enabled)
                {
                    m_ChirpFactory.Enabled = false;
                    Mod.Log.Info("[UI] 原版 Chirper 生成闸已关停（chirp 实体不再产生）");
                }
            }

            // 脏检查：Feed 没新增就不序列化、不推送
            if (Mod.Feed.Version == m_LastVersion)
                return;
            m_LastVersion = Mod.Feed.Version;
            m_PostsBinding.Update(Mod.Feed.ToJson());
        }
    }
}
