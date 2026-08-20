using Game;
using Game.Rendering;
using System.Reflection;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace CityLife.GameBridge
{
    /// <summary>
    /// M3-W v4：世界渲染改走游戏原生 <see cref="OverlayRenderSystem"/>（2026-08-21 调查定案，
    /// 社区 20+ mod 验证的正路：Move It/Traffic/Platter 同款）。
    ///
    /// 此前三条路的共同死因（调查实锤）：**HDRP/Unlit 不在发布 build 的着色器清单里**——
    /// Shader.Find 拿到无效 fallback，GameObject/DrawMesh/CustomPass 渲染的全是死材质，与接入无关。
    ///
    /// 本路做法：
    /// - 世界文字 = 空实体 + NameSystem.SetCustomName（DrawText 画的是实体渲染名——写什么画什么，
    ///   CJK 零风险：游戏自己的字体图集）；底板 = Buffer.DrawCustomMesh(Plane)。
    /// - 写入纪律（Move It 源码同款）：Rendering 相位注册；每帧 GetBuffer → Draw → AddBufferWriter。
    /// - Ctrl+9 在视线落点+10m 画"吃了吗"（红圈底板+文字）。判定：出字清晰/被楼挡就消失/跟随镜头甩不飞。
    /// </summary>
    public partial class BubbleWorldSpikeSystem : GameSystemBase
    {
        private OverlayRenderSystem m_Overlay = default!;
        private Game.UI.NameSystem m_NameSystem = default!;
        private CameraUpdateSystem m_CameraUpdate = default!;
        private EntityQuery m_HumanQuery = default!;
        private Entity m_LabelEntity;
        private bool m_Active;
        private bool m_LoggedDraw;
        private bool m_LoggedNoCam;
        private uint m_DrawnSinceToggle;
        private uint m_Frame;
        private uint m_LastKeyFrame;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_NameSystem = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            // 相机走游戏自己的 CameraUpdateSystem.activeCamera（BetterTransitView 源码同款）——
            // Camera.main 在部分相位为 null 且可能是代理；游戏系统的 activeCamera 才是权威
            m_CameraUpdate = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            m_HumanQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Creatures.Human>(),
                ComponentType.ReadOnly<Game.Objects.Transform>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 1;

        protected override void OnDestroy()
        {
            if (m_LabelEntity != Entity.Null && EntityManager.Exists(m_LabelEntity))
                EntityManager.DestroyEntity(m_LabelEntity);
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl && Input.GetKeyDown(KeyCode.Alpha9) && m_Frame - m_LastKeyFrame > 30)
            {
                m_LastKeyFrame = m_Frame;
                Toggle();
            }
            if (m_Active)
            {
                // hideOverlay 闸（2026-08-21 实机：hideOverlay=True 时 overlay 系统连收都不收，
                // 计数全 0）。RenderingSystem.hideOverlay 是公开可写属性——气泡开着就压 false。
                // 渲染层开关，不涉及模拟数值（单向阀门纪律不涉）
                var rendering = World.GetExistingSystemManaged<RenderingSystem>();
                if (rendering != null && rendering.hideOverlay)
                {
                    rendering.hideOverlay = false;
                    if (!m_LoggedGateFlip)
                    {
                        m_LoggedGateFlip = true;
                        Mod.Log.Info("[BubbleW] hideOverlay 已压 false（气泡开着期间强制）");
                    }
                }
                Draw();
            }
            m_Frame++;
        }

        private bool m_LoggedGateFlip;

        private void Toggle()
        {
            if (m_Active)
            {
                m_Active = false;
                if (m_LabelEntity != Entity.Null && EntityManager.Exists(m_LabelEntity))
                    EntityManager.DestroyEntity(m_LabelEntity);
                m_LabelEntity = Entity.Null;
                Mod.Log.Info("[BubbleW] overlay 关闭");
                return;
            }

            // 文本载体：空实体 + 自定义名（DrawText 画实体渲染名——SetCustomName 写什么画什么）
            m_LabelEntity = EntityManager.CreateEntity();
            m_NameSystem.SetCustomName(m_LabelEntity, "吃了吗");
            Mod.Log.Info($"[BubbleW] overlay 开启，载体渲染名={m_NameSystem.GetRenderedLabelName(m_LabelEntity)}");
            m_Active = true;
        }

        private void Draw()
        {
            var cam = m_CameraUpdate.activeCamera != null ? m_CameraUpdate.activeCamera
                : Camera.main != null ? Camera.main
                : Camera.allCameras.Length > 0 ? Camera.allCameras[0] : null;
            if (cam == null)
            {
                // 永不静默失败（2026-08-21 教训：拿不到相机时静默 return，表现="什么都没画"，排查半天）
                if (!m_LoggedNoCam || m_Frame % 256 == 0)
                {
                    m_LoggedNoCam = true;
                    Mod.Log.Warn("[BubbleW] Draw 拿不到相机（activeCamera/Camera.main/allCameras 全空）");
                }
                return;
            }

            // 锚点 = 视线落点附近**真实市民**的头顶（2026-08-21 实锤：落点打在 y=0 平面+10m，
            // 地形在 y≈80 的城市里=埋在地下——借用 creature 的真实 y，它们站地面上）
            var focus = FocusGround(cam);
            var pos = NearestHumanPos(focus);

            var buffer = m_Overlay.GetBuffer(out var deps);
            // 探针三件套：红圈（Move It 验证过的原语）+ 白平面底板 + 文字——分别落不同渲染列表，
            // 哪个计数起来就知道哪条道通（2026-08-21 计数全 0 调查）
            buffer.DrawCircle(Color.red, pos, 4f);
            buffer.DrawCustomMesh(Color.white, pos, 1.2f, 3.6f, OverlayRenderSystem.CustomMeshType.Plane, cam.transform.rotation);
            buffer.DrawText(m_LabelEntity, pos, true); // cameraFace=true 自动面向镜头
            m_Overlay.AddBufferWriter(deps);

            if (!m_LoggedDraw)
            {
                m_LoggedDraw = true;
                Mod.Log.Info($"[BubbleW] 首帧已画 @({pos.x:F0},{pos.y:F0},{pos.z:F0}) cam={cam.name}");
            }

            // 反射诊断：我们的内容到底进没进渲染列表（实例计数）+ 渲染闸状态，128 帧一行
            if (m_Frame % 128 == 0)
                DiagnoseRenderLists();
        }

        /// <summary>视线落点（仅取 x/z 作搜索圆心；y 不可信——y=0 平面与真实地形高差可能上百米）。</summary>
        private static float3 FocusGround(Camera cam)
        {
            var camPos = cam.transform.position;
            var fwd = cam.transform.forward;
            var t = fwd.y < -0.001f ? camPos.y / -fwd.y : 100f;
            return (float3)(camPos + fwd * t);
        }

        /// <summary>离落点最近的 Human/Resident creature 的位置 +2.2m（借真实地面高度）。找不到回落点+10m。</summary>
        private float3 NearestHumanPos(float3 focus)
        {
            var arr = m_HumanQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            var best = float.MaxValue;
            var bestPos = focus + new float3(0, 10f, 0);
            foreach (var e in arr)
            {
                var p = EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position;
                var d = math.distancesq(new float2(p.x, p.z), new float2(focus.x, focus.z));
                if (d < best)
                {
                    best = d;
                    bestPos = p;
                }
            }
            arr.Dispose();
            return bestPos + new float3(0, 2.2f, 0);
        }

        /// <summary>反射诊断（写没写进渲染列表一判定）：OverlayRenderSystem 私有实例计数 + RenderingSystem.hideOverlay。</summary>
        private void DiagnoseRenderLists()
        {
            try
            {
                var t = typeof(OverlayRenderSystem);
                const BindingFlags priv = BindingFlags.NonPublic | BindingFlags.Instance;
                var textCount = (int)(t.GetField("m_TextInstanceCount", priv)?.GetValue(m_Overlay) ?? -1);
                var absCount = (int)(t.GetField("m_AbsoluteInstanceCount", priv)?.GetValue(m_Overlay) ?? -1);
                var projCount = (int)(t.GetField("m_ProjectedInstanceCount", priv)?.GetValue(m_Overlay) ?? -1);
                var customCounts = t.GetField("m_CustomMeshInstanceCount", priv)?.GetValue(m_Overlay) as int[];
                var planeCount = customCounts != null && customCounts.Length > 2 ? customCounts[2] : -1;

                var hideOverlay = "?";
                var rendering = World.GetExistingSystemManaged<RenderingSystem>();
                if (rendering != null)
                {
                    var rt = rendering.GetType();
                    var pi = rt.GetProperty("hideOverlay") ?? rt.GetProperty("HideOverlay");
                    var fi = pi == null ? rt.GetField("hideOverlay", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public) : null;
                    var v = pi != null ? pi.GetValue(rendering) : fi?.GetValue(rendering);
                    hideOverlay = v?.ToString() ?? "?";
                }
                Mod.Log.Info($"[BubbleW·诊断] 渲染列表：text={textCount} absolute={absCount} projected={projCount} plane={planeCount}；hideOverlay={hideOverlay}");
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"[BubbleW·诊断] 反射失败：{e.Message}");
            }
        }
    }
}
