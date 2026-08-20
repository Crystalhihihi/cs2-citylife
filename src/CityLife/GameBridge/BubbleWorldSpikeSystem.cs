using Game;
using Game.Rendering;
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
        private Entity m_LabelEntity;
        private bool m_Active;
        private bool m_LoggedDraw;
        private bool m_LoggedNoCam;
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
                Draw();
            m_Frame++;
        }

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
            var camPos = cam.transform.position;
            var fwd = cam.transform.forward;
            var t = fwd.y < -0.001f ? camPos.y / -fwd.y : 100f;
            var pos = (float3)(camPos + fwd * t + new Vector3(0, 10f, 0));

            var buffer = m_Overlay.GetBuffer(out var deps);
            // 底板：面朝镜头的平面（白），文字压上
            buffer.DrawCustomMesh(Color.white, pos, 1.2f, 3.6f, OverlayRenderSystem.CustomMeshType.Plane, cam.transform.rotation);
            buffer.DrawText(m_LabelEntity, pos, true); // cameraFace=true 自动面向镜头
            m_Overlay.AddBufferWriter(deps);

            if (!m_LoggedDraw)
            {
                m_LoggedDraw = true;
                Mod.Log.Info($"[BubbleW] 首帧已画 @({pos.x:F0},{pos.y:F0},{pos.z:F0}) cam={cam.name}");
            }
        }
    }
}
