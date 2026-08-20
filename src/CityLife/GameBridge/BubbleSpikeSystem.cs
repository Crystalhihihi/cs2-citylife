using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Colossal.UI.Binding;
using Game;
using Game.Citizens;
using Game.UI;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Transform = Game.Objects.Transform;

namespace CityLife.GameBridge
{
    /// <summary>
    /// M3-spike：气泡渲染管线验证（纯读+overlay，验证后拆除或转正式版）。
    ///
    /// 通道结论（2026-08-20 探明）：游戏无原生气泡通道（Game.dll 无 Nameplate/FloatingText/
    /// SpeechBubble/OverlaySystem；cs2/bindings 无投影助手）——唯一可行路 = cohtml overlay（"Game" 层）
    /// + C# 侧投影（Camera.main.WorldToScreenPoint，UnityEngine.CoreModule 与 Input 同源可用）。
    ///
    /// 做法（spike 就测最坏情况）：
    /// - 每 512 帧重采样"离镜头最近的市民"（≥250m 丢弃）；
    /// - 每帧对每个目标 WorldToScreenPoint（头顶+2m）→ 屏宽/屏高**百分比**（免疫 UI 缩放/DPI），
    ///   剔除镜头背后/屏外；JSON 每帧重推（binding 流量+cohtml 重绘的极限压测）；
    /// - 占位文案三档长度（测换行宽度）；Ctrl+1/2/3=100/300/600 个，Ctrl+0=关；
    /// - Time.deltaTime 每 4 秒打平均 FPS，三档对比定去留（回退：降推送频率/降上限/innerHTML）。
    /// spike 纪律：不写任何模拟数值；Ctrl+字母组合（F9 撞车教训）。
    /// </summary>
    public partial class BubbleSpikeSystem : UISystemBase
    {
        /// <summary>只在游戏内挂 UI（主菜单/编辑器不推数据）。</summary>
        public override GameMode gameMode => GameMode.Game;

        private const float k_MaxDist = 250f;

        private ValueBinding<string> m_BubblesBinding = default!;
        private EntityQuery m_CitizenQuery = default!;
        private readonly List<Entity> m_Sampled = new();
        private int m_Level;          // 0=关 1=100 2=300 3=600
        private uint m_Frame;
        private uint m_LastKeyFrame;
        private float m_FpsAccum;
        private int m_FpsFrames;
        private float m_FpsTimer;

        // 占位文案（三种长度，测气泡宽度与换行）
        private static readonly string[] k_Texts = { "……", "吃了吗", "今天这公交又晚点了，离谱" };

        protected override void OnCreate()
        {
            base.OnCreate();
            AddBinding(m_BubblesBinding = new ValueBinding<string>("CityLife", "bubbles", "[]"));
            m_CitizenQuery = GetEntityQuery(
                ComponentType.ReadOnly<Citizen>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
        }

        protected override void OnUpdate()
        {
            var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            var debounced = m_Frame - m_LastKeyFrame > 30;
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.Alpha0)) { m_LastKeyFrame = m_Frame; SetLevel(0); }
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.Alpha1)) { m_LastKeyFrame = m_Frame; SetLevel(1); }
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.Alpha2)) { m_LastKeyFrame = m_Frame; SetLevel(2); }
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.Alpha3)) { m_LastKeyFrame = m_Frame; SetLevel(3); }

            // FPS 计：每 4 秒一行（开着的档位才有意义）
            m_FpsAccum += UnityEngine.Time.deltaTime;
            m_FpsFrames++;
            m_FpsTimer += UnityEngine.Time.deltaTime;
            if (m_FpsTimer >= 4f)
            {
                if (m_Level > 0)
                    Mod.Log.Info($"[Bubble] FPS avg={(m_FpsFrames / m_FpsAccum):F1}（N={LevelCount()}，采样 {m_Sampled.Count}）");
                m_FpsAccum = 0;
                m_FpsFrames = 0;
                m_FpsTimer = 0f;
            }

            if (m_Level == 0)
            {
                m_Frame++;
                return;
            }
            var cam = Camera.main;
            if (cam == null)
            {
                m_Frame++;
                return;
            }

            if (m_Frame % 512 == 0 || m_Sampled.Count == 0)
                Resample(cam.transform.position);
            PushBubbles(cam);
            m_Frame++;
        }

        private int LevelCount() => m_Level == 1 ? 100 : m_Level == 2 ? 300 : 600;

        private void SetLevel(int level)
        {
            m_Level = level;
            m_Sampled.Clear(); // 强制重采样
            if (level == 0)
                m_BubblesBinding.Update("[]");
            Mod.Log.Info($"[Bubble] 档位 → {(level == 0 ? "关" : LevelCount().ToString())}");
        }

        /// <summary>重采样：离镜头最近的市民取前 N（250m 外丢弃）。</summary>
        private void Resample(float3 camPos)
        {
            var want = LevelCount();
            m_Sampled.Clear();
            var arr = m_CitizenQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            var scored = new List<(float d, Entity e)>(arr.Length);
            foreach (var e in arr)
            {
                var d = math.distancesq(EntityManager.GetComponentData<Transform>(e).m_Position, camPos);
                if (d < k_MaxDist * k_MaxDist)
                    scored.Add((d, e));
            }
            arr.Dispose();
            scored.Sort((a, b) => a.d.CompareTo(b.d));
            for (int i = 0; i < scored.Count && i < want; i++)
                m_Sampled.Add(scored[i].e);
        }

        /// <summary>每帧投影+推送（最坏情况压测：JSON 全量重推）。</summary>
        private void PushBubbles(Camera cam)
        {
            var sb = new StringBuilder(m_Sampled.Count * 40 + 2);
            sb.Append('[');
            var first = true;
            var shown = 0;
            foreach (var e in m_Sampled)
            {
                if (!EntityManager.Exists(e) || !EntityManager.HasComponent<Transform>(e))
                    continue;
                var p = EntityManager.GetComponentData<Transform>(e).m_Position;
                p.y += 2f; // 头顶
                var s = cam.WorldToScreenPoint(p);
                if (s.z < 0.1f)
                    continue; // 镜头背后
                var x = s.x / Screen.width * 100f;
                var y = (1f - s.y / Screen.height) * 100f; // Unity 自下而上 → CSS 自上而下
                if (x < -5f || x > 105f || y < -5f || y > 105f)
                    continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"x\":").Append(x.ToString("F1", CultureInfo.InvariantCulture))
                  .Append(",\"y\":").Append(y.ToString("F1", CultureInfo.InvariantCulture))
                  .Append(",\"t\":\"").Append(k_Texts[shown % k_Texts.Length]).Append("\"}");
                shown++;
            }
            sb.Append(']');
            m_BubblesBinding.Update(sb.ToString());
        }
    }
}
