using System;
using System.Collections.Generic;
using Game;
using Game.Rendering;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Transform = Game.Objects.Transform;

namespace CityLife.GameBridge
{
    /// <summary>
    /// M3 气泡层 v1（世界空间渲染，游戏原生 OverlayRenderSystem 通道）：
    /// 市民/车辆/建筑三类锚点的头顶气泡——正式版前身（2026-08-21 路线贯通后直接从 spike 升级）。
    ///
    /// 路线（全部实锤，勿再探）：
    /// - 写入：GetBuffer(out deps) → DrawCircle/DrawCustomMesh(Plane)/DrawText → AddBufferWriter；
    ///   **注册相位必须 GameSimulation**（Rendering 相位写入被 OverlayRenderSystem 的清/拷时序永久跳过）；
    /// - 世界文字 = 空实体 + NameSystem.SetCustomName（DrawText 画实体渲染名，写什么画什么，
    ///   CJK 零风险——游戏自己的字体图集）；
    /// - hideOverlay 闸：开着期间每帧压 false（渲染层开关，不涉模拟数值）；
    /// - 锚点 y 用实体真实 Transform（地形高度不可信——落点 y=0 平面曾把气泡埋在地下）。
    ///
    /// 尺寸机制：DrawText 无尺寸参——文本网格由系统的 TMP 生成器懒烘焙（新字符串首画时生成）。
    /// 在 Draw 内"设小字号→画→恢复"的窗口期，让我们的字符串以小字号烘焙（游戏既有字符串
    /// 已缓存于默认字号，不受影响）。
    /// 键位：Ctrl+9 开关；Ctrl+8 循环数量档（30/60/120）。
    /// </summary>
    public partial class BubbleWorldSpikeSystem : GameSystemBase
    {
        private const int k_MaxBubbles = 120;
        private const float k_MaxDist = 400f;      // 可读距离上限（spike 取值；LOD 闸 §12 #41 对齐人形渲染）
        private const float k_LodMaxHeight = 350f; // 镜头高于此不再画（人形 LOD 直觉：看不清人就不该有气泡）

        private OverlayRenderSystem m_Overlay = default!;
        private Game.UI.NameSystem m_NameSystem = default!;
        private CameraUpdateSystem m_CameraUpdate = default!;
        private EntityQuery m_HumanQuery = default!;
        private EntityQuery m_CarQuery = default!;
        private EntityQuery m_BuildingQuery = default!;

        private bool m_Active;
        private int m_Level = 2;                    // 0=30 1=60 2=120
        private readonly List<TrackedBubble> m_Bubbles = new();
        private uint m_Frame;
        private uint m_LastKeyFrame;
        private bool m_TmpLogged;
        private float m_FpsAccum;
        private int m_FpsFrames;
        private float m_FpsTimer;

        /// <summary>一个被追踪的气泡：锚点实体 + 文本载体实体 + 生命周期。</summary>
        private struct TrackedBubble
        {
            public Entity Anchor;
            public byte Kind;       // 0 人 1 车 2 楼
            public Entity Label;    // 文本载体（SetCustomName 写什么画什么）
            public int TextIdx;
            public float NextAt;    // 下次换文案时刻（Time.time）
        }

        // 占位文案池（正式版换内容管道；按类型分池：公园/住宅已分，载具类型全分待正式版）
        private static readonly string[] k_Texts =
            { "……", "吃了吗", "今天这公交又晚点了，离谱", "风好大", "快走要迟到了", "这店排队也太长了", "听说东区新开了家店" };
        private static readonly string[] k_CarTexts =
            { "嘀嘀——", "又堵了", "轰——", "前面路口慢点" };
        private static readonly string[] k_BuildingTexts =
            { "……", "晚饭吃啥", "电视小点声！", "装修第三天了", "快递放门口", "楼上又拖椅子" };
        private static readonly string[] k_ParkTexts =
            { "风一吹真舒服", "鸽子真多", "遛弯第三圈了", "这花开得不错" };

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_NameSystem = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            // 相机走游戏自己的 CameraUpdateSystem.activeCamera（BetterTransitView 源码同款——权威源）
            m_CameraUpdate = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            m_HumanQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Creatures.Human>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_CarQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Vehicles.Car>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Game.Vehicles.ParkedCar>(), // 空车不说话（说话的是车里的人）
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_BuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.Building>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 1;

        protected override void OnDestroy()
        {
            DestroyAllBubbles();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            var debounced = m_Frame - m_LastKeyFrame > 30;
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.Alpha9)) { m_LastKeyFrame = m_Frame; Toggle(); }
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.Alpha8)) { m_LastKeyFrame = m_Frame; CycleLevel(); }

            // FPS 计：每 4 秒一行（开着才有意义）
            m_FpsAccum += UnityEngine.Time.deltaTime;
            m_FpsFrames++;
            m_FpsTimer += UnityEngine.Time.deltaTime;
            if (m_FpsTimer >= 4f)
            {
                if (m_Active)
                    Mod.Log.Info($"[BubbleW] FPS avg={(m_FpsFrames / m_FpsAccum):F1}（N={LevelCount()}，在场 {m_Bubbles.Count}）");
                m_FpsAccum = 0;
                m_FpsFrames = 0;
                m_FpsTimer = 0f;
            }

            if (!m_Active)
            {
                m_Frame++;
                return;
            }

            // hideOverlay 闸：开着期间压 false（渲染层开关，不涉模拟数值）
            var rendering = World.GetExistingSystemManaged<RenderingSystem>();
            if (rendering != null && rendering.hideOverlay)
                rendering.hideOverlay = false;

            var cam = m_CameraUpdate.activeCamera != null ? m_CameraUpdate.activeCamera
                : Camera.main != null ? Camera.main
                : Camera.allCameras.Length > 0 ? Camera.allCameras[0] : null;
            if (cam == null)
            {
                m_Frame++;
                return;
            }

            if (m_Frame % 512 == 0)
                Resample(cam);
            TickLifecycle();
            Draw(cam);
            m_Frame++;
        }

        private int LevelCount() => m_Level == 0 ? 30 : m_Level == 1 ? 60 : 120;

        private void Toggle()
        {
            m_Active = !m_Active;
            if (!m_Active)
                DestroyAllBubbles();
            else
                m_Bubbles.Clear(); // 强制重采样建组
            Mod.Log.Info($"[BubbleW] 气泡层 {(m_Active ? "开启" : "关闭")}（N={LevelCount()}）");
        }

        private void CycleLevel()
        {
            m_Level = (m_Level + 1) % 3;
            m_Bubbles.Clear();
            Mod.Log.Info($"[BubbleW] 数量档 → {LevelCount()}");
        }

        private void DestroyAllBubbles()
        {
            foreach (var b in m_Bubbles)
                if (m_NameSystem != null && EntityManager.Exists(b.Label))
                    EntityManager.DestroyEntity(b.Label);
            m_Bubbles.Clear();
        }

        // —— 采样：三类锚点，屏内+距离上限，人:车:楼 配比 ——
        private void Resample(Camera cam)
        {
            // 留旧：锚点还在屏内的气泡保留（生命周期/文案不中断）；出屏/死亡的清掉
            for (int i = m_Bubbles.Count - 1; i >= 0; i--)
            {
                var b = m_Bubbles[i];
                if (!EntityManager.Exists(b.Anchor) || !EntityManager.HasComponent<Transform>(b.Anchor)
                    || !OnScreen(cam, b.Anchor))
                {
                    if (EntityManager.Exists(b.Label))
                        EntityManager.DestroyEntity(b.Label);
                    m_Bubbles.RemoveAt(i);
                }
            }

            var want = LevelCount();
            Collect(cam, m_CarQuery, Math.Max(4, want / 6), 1);
            Collect(cam, m_BuildingQuery, Math.Max(3, want / 10), 2);
            Collect(cam, m_HumanQuery, want, 0);
        }

        private bool OnScreen(Camera cam, Entity e)
        {
            var p = EntityManager.GetComponentData<Transform>(e).m_Position;
            var s = cam.WorldToScreenPoint(p);
            return s.z > 5f && s.z <= k_MaxDist
                && s.x >= 0 && s.x <= Screen.width && s.y >= 0 && s.y <= Screen.height;
        }

        private void Collect(Camera cam, EntityQuery query, int cap, byte kind)
        {
            var current = m_Bubbles.Count;
            if (current >= LevelCount())
                return;
            var arr = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            var scored = new List<(float d, Entity e)>(arr.Length);
            foreach (var e in arr)
            {
                if (HasAnchor(e))
                    continue;
                var p = EntityManager.GetComponentData<Transform>(e).m_Position;
                var s = cam.WorldToScreenPoint(p);
                if (s.z < 5f || s.z > k_MaxDist)
                    continue;
                if (s.x < 0 || s.x > Screen.width || s.y < 0 || s.y > Screen.height)
                    continue;
                scored.Add((s.z, e));
            }
            arr.Dispose();
            scored.Sort((a, b) => a.d.CompareTo(b.d));
            var now = UnityEngine.Time.time;
            for (int i = 0; i < scored.Count && i < cap && m_Bubbles.Count < k_MaxBubbles; i++)
            {
                var e = scored[i].e;
                var label = EntityManager.CreateEntity();
                var b = new TrackedBubble
                {
                    Anchor = e,
                    Kind = kind,
                    Label = label,
                    TextIdx = 0,
                    NextAt = now + HoldFor(e.Index, 0),
                };
                SetBubbleText(ref b, 0);
                m_Bubbles.Add(b);
            }
        }

        private bool HasAnchor(Entity e)
        {
            foreach (var b in m_Bubbles)
                if (b.Anchor == e)
                    return true;
            return false;
        }

        // —— 生命周期：各气泡独立时钟（6-15s 错相，绝不同时切换）——
        private void TickLifecycle()
        {
            var now = UnityEngine.Time.time;
            for (int i = 0; i < m_Bubbles.Count; i++)
            {
                var b = m_Bubbles[i];
                if (!EntityManager.Exists(b.Anchor))
                    continue; // 出组清理由 Resample 负责
                if (now >= b.NextAt)
                {
                    b.TextIdx++;
                    b.NextAt = now + HoldFor(b.Anchor.Index, b.TextIdx);
                    SetBubbleText(ref b, b.TextIdx);
                    m_Bubbles[i] = b;
                }
            }
        }

        private void SetBubbleText(ref TrackedBubble b, int textIdx)
        {
            var pool = b.Kind == 0 ? k_Texts
                : b.Kind == 1 ? k_CarTexts
                : EntityManager.HasComponent<Game.Buildings.AttractivenessProvider>(b.Anchor) ? k_ParkTexts
                : k_BuildingTexts;
            var text = pool[(b.Anchor.Index + textIdx) % pool.Length];
            if (EntityManager.Exists(b.Label))
                m_NameSystem.SetCustomName(b.Label, text);
        }

        /// <summary>气泡驻留时长（6-15s，确定性错相：实体×集数散列——全屏绝不同时切换）。</summary>
        private static float HoldFor(int entityIndex, int textIdx)
            => 6f + ((entityIndex * 7919 + textIdx * 104729) % 900) / 100f;

        // —— 绘制：每帧（世界空间 overlay，零屏幕贴纸）——
        private void Draw(Camera cam)
        {
            // LOD 闸（§12 #41：镜头高于 k_LodMaxHeight 不画——看不清人的高度就不该有气泡）
            if (cam.transform.position.y > k_LodMaxHeight)
                return;

            var tmp = m_Overlay.GetTextMesh();
            float? origSize = null;
            if (tmp != null)
            {
                origSize = tmp.fontSize;
                if (!m_TmpLogged)
                {
                    m_TmpLogged = true;
                    Mod.Log.Info($"[BubbleW] TMP 默认字号={origSize}（我们的文本以小字号烘焙）");
                }
                tmp.fontSize = origSize.Value * 0.22f; // 小字号窗口期开始（游戏既有字符串已缓存，不受影响）
            }

            var deps = default(JobHandle);
            var buffer = m_Overlay.GetBuffer(out deps);
            foreach (var b in m_Bubbles)
            {
                if (!EntityManager.Exists(b.Anchor) || !EntityManager.HasComponent<Transform>(b.Anchor))
                    continue;
                var p = EntityManager.GetComponentData<Transform>(b.Anchor).m_Position;
                p.y += b.Kind == 0 ? 2.2f : b.Kind == 1 ? 2.5f : 12f; // 人头/车顶/楼顶（估值，正式版按包围盒）

                // 底板（平面，按类型配色；文字向镜头前移一点防共面闪）
                var plateColor = b.Kind == 0 ? Color.white
                    : b.Kind == 1 ? new Color(0.8f, 0.9f, 1f)
                    : new Color(1f, 0.95f, 0.75f);
                var text = m_NameSystem.GetRenderedLabelName(b.Label);
                var width = math.clamp(0.55f * text.Length + 1.2f, 2f, 8f);
                buffer.DrawCustomMesh(plateColor, p, 1.2f, width, OverlayRenderSystem.CustomMeshType.Plane, cam.transform.rotation);
                buffer.DrawText(b.Label, p - (float3)cam.transform.forward * 0.15f, true);
            }
            m_Overlay.AddBufferWriter(deps);

            if (tmp != null && origSize.HasValue)
                tmp.fontSize = origSize.Value; // 窗口期结束：恢复游戏默认字号
        }
    }
}
