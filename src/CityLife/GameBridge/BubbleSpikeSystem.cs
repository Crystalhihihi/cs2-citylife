using System;
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

        private const float k_MaxDist = 400f; // 可读距离上限（spike 取值；正式版对齐游戏人形渲染 LOD，§12 #41）

        private ValueBinding<string> m_BubblesBinding = default!;
        private EntityQuery m_HumanQuery = default!;    // Human 实体 + Transform（行人）
        private EntityQuery m_ResidentQuery = default!; // Resident 实体 + Transform（备选标记）
        private EntityQuery m_CarQuery = default!;      // Car 实体 + Transform（车顶锚点）
        private EntityQuery m_BuildingQuery = default!; // Building 实体 + Transform（楼顶锚点）
        private EntityQuery m_ActiveQuery;              // 普查后选定的采样查询（m_QueryReady=false 时不可用）
        private bool m_QueryReady;
        private bool m_Censused;
        private readonly List<(Entity e, byte kind)> m_Sampled = new(); // kind: 0 人 1 车 2 楼
        private int m_Level;          // 0=关 1=100 2=300 3=600
        private uint m_Frame;
        private uint m_LastKeyFrame;
        private float m_FpsAccum;
        private int m_FpsFrames;
        private float m_FpsTimer;

        // 占位文案（三种长度，测气泡宽度与换行）
        private static readonly string[] k_Texts = { "……", "吃了吗", "今天这公交又晚点了，离谱" };
        private static readonly string[] k_CarTexts = { "滴——", "又堵了" };
        private static readonly string[] k_BuildingTexts = { "……", "晚饭吃啥" };

        protected override void OnCreate()
        {
            base.OnCreate();
            AddBinding(m_BubblesBinding = new ValueBinding<string>("CityLife", "bubbles", "[]"));
            // 市民本体（Citizen）不带 Transform（实机采样恒 0 实锤）——世界上可见的人是
            // Game.Creatures 的 creature 实体（Human/Resident 标记），位置挂在它们身上
            m_HumanQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Creatures.Human>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_ResidentQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Creatures.Resident>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            // 车顶/楼顶锚点（M3 设计：市民头/车顶/楼顶三类锚定）
            m_CarQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Vehicles.Car>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_BuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.Building>(),
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
            var cam = FindCam();
            if (cam == null)
            {
                m_Frame++;
                return;
            }

            if (!m_Censused)
                Census();
            if (!m_QueryReady)
            {
                m_Frame++;
                return;
            }
            if (m_Frame % 512 == 0 || m_Sampled.Count == 0)
                Resample(cam);
            PushBubbles(cam);
            m_Frame++;
        }

        /// <summary>
        /// 找相机：先 Camera.main， null 再 allCameras[0]。
        /// 实机踩坑（2026-08-20）：UIUpdate 阶段 Camera.main 恒为 null（游戏主相机没挂 MainCamera
        /// 标签或该阶段解析不到）——症状是普查/采样永远不执行、气泡一个不出（日志只有 FPS 行）。
        /// </summary>
        private static Camera? FindCam()
        {
            if (Camera.main != null)
                return Camera.main;
            var all = Camera.allCameras;
            return all != null && all.Length > 0 ? all[0] : null;
        }
        /// <summary>一次性普查：Human/Resident 两种 creature 标记谁带 Transform 用谁（分类学摸底）。</summary>
        private void Census()
        {
            m_Censused = true;
            var humans = GetEntityQuery(ComponentType.ReadOnly<Game.Creatures.Human>()).CalculateEntityCount();
            var humansT = m_HumanQuery.CalculateEntityCount();
            var residents = GetEntityQuery(ComponentType.ReadOnly<Game.Creatures.Resident>()).CalculateEntityCount();
            var residentsT = m_ResidentQuery.CalculateEntityCount();
            Mod.Log.Info($"[Bubble] 普查：Human={humans}（带Transform {humansT}）Resident={residents}（带Transform {residentsT}）"
                         + $" 车={m_CarQuery.CalculateEntityCount()} 楼={m_BuildingQuery.CalculateEntityCount()}");
            m_QueryReady = humansT > 0 || residentsT > 0;
            m_ActiveQuery = humansT > 0 ? m_HumanQuery : m_ResidentQuery;
            if (!m_QueryReady)
                Mod.Log.Warn("[Bubble] Human/Resident 实体都不带 Transform——位置链路仍未知，需下一轮 dump");
        }

        // （FocusPoint 落点法已退役：镜头 620m 高时落点 535m 内无人——高空本来就不该有气泡；
        //   教训保留：采样必须跟着"屏上可见"走，不跟几何落点走）

        private int LevelCount() => m_Level == 1 ? 100 : m_Level == 2 ? 300 : 600;

        private void SetLevel(int level)
        {
            m_Level = level;
            m_Sampled.Clear(); // 强制重采样
            m_Diagnose = true; // 下次重采样打一行诊断（相机名/位置/朝向/落点/最近距离）
            if (level == 0)
                m_BubblesBinding.Update("[]");
            Mod.Log.Info($"[Bubble] 档位 → {(level == 0 ? "关" : LevelCount().ToString())}");
        }

        private bool m_Diagnose;

        /// <summary>
        /// 重采样（v2：屏幕投影法，替代落点圈法——诊断实锤：镜头 620m 高时落点 535m 内无人，
        /// 高空本来就不该有气泡）：候选 = 投影在屏内（±5% 边距）且距离 ≤ k_MaxDist 的实体，
        /// 按距离取前 N。与"人清晰可见才挂气泡"（§12 #41）同构。
        /// 三类锚点（M3 设计：市民头/车顶/楼顶）：人:车:楼 配比取样，楼/车是背景氛围不盖过人。
        /// </summary>
        private void Resample(Camera cam)
        {
            var want = LevelCount();
            m_Sampled.Clear();
            var cars = Collect(cam, m_CarQuery, Math.Max(6, want / 6), 1);
            var buildings = Collect(cam, m_BuildingQuery, Math.Max(4, want / 10), 2);
            var humans = Collect(cam, m_ActiveQuery, want, 0);
            if (m_Diagnose)
            {
                m_Diagnose = false;
                Mod.Log.Info($"[Bubble·诊断] cam={cam.name} 高={cam.transform.position.y:F0}m 屏内候选 人={humans} 车={cars} 楼={buildings}");
            }
        }

        /// <summary>屏幕投影采样：屏内（±5% 边距）且 z∈(5,k_MaxDist] 的实体按距离取前 cap 个入 m_Sampled。返回入圈数。</summary>
        private int Collect(Camera cam, EntityQuery query, int cap, byte kind)
        {
            var arr = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            var scored = new List<(float d, Entity e)>(arr.Length);
            foreach (var e in arr)
            {
                var p = EntityManager.GetComponentData<Transform>(e).m_Position;
                var s = cam.WorldToScreenPoint(p);
                if (s.z < 5f || s.z > k_MaxDist)
                    continue; // 背后/贴脸/超可读距离
                if (s.x < -0.05f * Screen.width || s.x > 1.05f * Screen.width
                    || s.y < -0.05f * Screen.height || s.y > 1.05f * Screen.height)
                    continue; // 屏外
                scored.Add((s.z, e));
            }
            arr.Dispose();
            scored.Sort((a, b) => a.d.CompareTo(b.d));
            var n = 0;
            for (int i = 0; i < scored.Count && i < cap; i++)
            {
                m_Sampled.Add((scored[i].e, kind));
                n++;
            }
            return n;
        }

        /// <summary>每帧投影+推送（最坏情况压测：JSON 全量重推）。</summary>
        private void PushBubbles(Camera cam)
        {
            var sb = new StringBuilder(m_Sampled.Count * 40 + 2);
            sb.Append('[');
            var first = true;
            var shown = 0;
            foreach (var (e, kind) in m_Sampled)
            {
                if (!EntityManager.Exists(e) || !EntityManager.HasComponent<Transform>(e))
                    continue;
                var p = EntityManager.GetComponentData<Transform>(e).m_Position;
                p.y += kind == 0 ? 2f : kind == 1 ? 2.5f : 12f; // 人头/车顶/楼顶（spike 估值，正式版按包围盒）
                var s = cam.WorldToScreenPoint(p);
                if (s.z < 0.1f)
                    continue; // 镜头背后
                var x = s.x / Screen.width * 100f;
                var y = (1f - s.y / Screen.height) * 100f; // Unity 自下而上 → CSS 自上而下
                if (x < -5f || x > 105f || y < -5f || y > 105f)
                    continue;
                var text = kind == 0 ? k_Texts[shown % k_Texts.Length]
                    : kind == 1 ? k_CarTexts[shown % k_CarTexts.Length]
                    : k_BuildingTexts[shown % k_BuildingTexts.Length];
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"x\":").Append(x.ToString("F1", CultureInfo.InvariantCulture))
                  .Append(",\"y\":").Append(y.ToString("F1", CultureInfo.InvariantCulture))
                  .Append(",\"t\":\"").Append(text).Append("\"}");
                shown++;
            }
            sb.Append(']');
            m_BubblesBinding.Update(sb.ToString());
        }
    }
}
