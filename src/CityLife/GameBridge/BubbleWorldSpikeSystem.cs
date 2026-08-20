using System.Collections.Generic;
using System.Text;
using Game;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CityLife.GameBridge
{
    /// <summary>
    /// M3-W S1：世界空间渲染气泡探索（2026-08-20 玩家定案：放弃 cohtml 屏幕贴纸路线，
    /// 直接在 HDRP 3D 管线做；cohtml spike 保留作对照不再演进）。
    ///
    /// 渲染接入 v3（前两条已实锤排除）：
    /// - TextMesh 借网格：三连 NRE，TextMesh 在本环境就是坏的；
    /// - 场景 GameObject+MeshRenderer：创建成功但永不渲染（CS2 的 HDRP 相机不吃场景 GameObject，
    ///   对照组纯红方块同样不可见）——本版换 **SRP 原生注入点**：
    ///   `RenderPipelineManager.beginCameraRendering` + `Graphics.DrawMesh`，
    ///   渲染请求直接进当帧相机队列，不依赖场景 GameObject 是否被渲染。
    ///
    /// 本版判定：Ctrl+9 在视线落点+10m 画红色"吃了吗"+对照红方块。
    /// 出字/出方块=注入点通；仍不出=看 `cam.cullingMask` 日志（外部内容被相机掩码排除的铁证）。
    /// 网格手写（Font.GetCharacterInfo 逐字四边形，纯托管数学）；CJK 字体走系统字库（msyh）。
    /// </summary>
    public partial class BubbleWorldSpikeSystem : GameSystemBase
    {
        private bool m_ShadersDumped;
        private Font? m_Font;
        private bool m_DrawActive;
        private Mesh? m_TextMesh;
        private Mesh? m_QuadMesh;
        private Material? m_TextMat;
        private Material? m_QuadMat;
        private GameObject? m_VolumeGo;
        private CityLifeBubblePass? m_Pass;
        private uint m_Frame;
        private uint m_LastKeyFrame;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 1;

        protected override void OnDestroy()
        {
            TearDownVolume();
            DestroyDrawAssets();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            if (!m_ShadersDumped)
            {
                m_ShadersDumped = true;
                DumpShaders();
            }

            var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl && Input.GetKeyDown(KeyCode.Alpha9) && m_Frame - m_LastKeyFrame > 30)
            {
                m_LastKeyFrame = m_Frame;
                Toggle();
            }
            m_Frame++;
        }

        private void DumpShaders()
        {
            var shaders = Resources.FindObjectsOfTypeAll<Shader>();
            var interesting = new StringBuilder();
            foreach (var s in shaders)
            {
                var n = s.name;
                if (n.Contains("Text") || n.Contains("SDF") || n.Contains("Font") || n.Contains("Name") || n.Contains("Unlit"))
                    interesting.Append(n).Append(" | ");
            }
            Mod.Log.Info($"[BubbleW] 已加载着色器 {shaders.Length} 个；文本/SDF/Unlit 候选：{interesting}");
        }

        /// <summary>相机普查（统一假说验证用）：全部相机的名字/深度/掩码/带不带 HDAdditionalCameraData——
        /// 找出真正的世界渲染相机（"Main Camera" 可能只是投影代理）。</summary>
        private void DumpCameras()
        {
            var sb = new StringBuilder();
            foreach (var c in Camera.allCameras)
            {
                var hd = c.GetComponent<HDAdditionalCameraData>() != null ? "+HD" : "-HD";
                sb.Append($"[{c.name} d={c.depth} mask=0x{c.cullingMask:X} {hd}] ");
            }
            Mod.Log.Info($"[BubbleW] 相机普查（{Camera.allCameras.Length}）：{sb}");
        }

        private void Toggle()
        {
            if (m_DrawActive)
            {
                m_DrawActive = false;
                TearDownVolume();
                DestroyDrawAssets();
                Mod.Log.Info("[BubbleW] 绘制关闭");
                return;
            }

            // 锚点 = 镜头视线落点 + 10m（S1：你看哪它画哪）
            var cam = Camera.main != null ? Camera.main
                : Camera.allCameras.Length > 0 ? Camera.allCameras[0] : null;
            if (cam == null)
            {
                Mod.Log.Warn("[BubbleW] 找不到相机");
                return;
            }
            var camPos = cam.transform.position;
            var fwd = cam.transform.forward;
            var t = fwd.y < -0.001f ? camPos.y / -fwd.y : 100f;
            var focus = camPos + fwd * t;
            var pos = new Vector3((float)focus.x, (float)(focus.y + 10.0), (float)focus.z);
            var quadPos = camPos + fwd.normalized * 30f; // 贴脸对照组：相机正前 30m（排除一切位置/视锥怀疑）

            m_Font ??= CreateCjkFont();
            if (m_Font == null || m_Font.material == null)
            {
                Mod.Log.Warn("[BubbleW] CJK 字体/材质不可用");
                return;
            }
            m_TextMesh = BuildTextMesh("吃了吗", m_Font, 64, 0.02f);
            if (m_TextMesh == null)
                return;
            m_QuadMesh = BuildQuadMesh(3f);

            var shader = PickShader();
            if (shader == null)
            {
                Mod.Log.Warn("[BubbleW] 无可用着色器");
                return;
            }
            m_TextMat = new Material(shader) { mainTexture = m_Font.material.mainTexture };
            m_QuadMat = new Material(shader);
            if (m_TextMat.HasProperty("_BaseColor"))
                m_TextMat.SetColor("_BaseColor", Color.red);
            if (m_QuadMat.HasProperty("_BaseColor"))
                m_QuadMat.SetColor("_BaseColor", Color.red);

            // 渲染接入 = HDRP CustomPass（Unity HDRP 文档明写的自定义渲染注入点，公开 API）：
            // 全局 volume（**isGlobal=true 全相机生效**——2026-08-21 统一假说："Main Camera" 可能只是
            // 投影代理而非真正的世界渲染相机，挂单相机可能挂错对象；全局则谁是真相机都拦得住）+ cmd.DrawMesh
            DumpCameras();
            DumpVolumes();
            m_VolumeGo = new GameObject("CityLifeBubbleVolume");
            m_VolumeGo.hideFlags = HideFlags.HideAndDontSave;
            var volume = m_VolumeGo.AddComponent<CustomPassVolume>();
            volume.isGlobal = true;
            volume.injectionPoint = CustomPassInjectionPoint.BeforeTransparent;
            m_Pass = volume.AddPassOfType(typeof(CityLifeBubblePass)) as CityLifeBubblePass;
            if (m_Pass == null)
            {
                Mod.Log.Warn("[BubbleW] AddPassOfType 失败（CustomPass API 面不符？）");
                TearDownVolume();
                return;
            }
            m_Pass.TextMesh = m_TextMesh;
            m_Pass.QuadMesh = m_QuadMesh;
            m_Pass.TextMat = m_TextMat;
            m_Pass.QuadMat = m_QuadMat;
            m_Pass.Pos = pos;
            m_Pass.QuadPos = quadPos;

            m_DrawActive = true;
            Mod.Log.Info($"[BubbleW] 绘制开启(CustomPass) @({pos.x:F0},{pos.y:F0},{pos.z:F0}) shader={shader.name} verts={m_TextMesh.vertexCount}");
        }

        /// <summary>CustomPassVolume 枚举（CS2 支持 CustomPass 的铁证/反证）：游戏自己注册了几个、注入点都是啥。</summary>
        private void DumpVolumes()
        {
            var volumes = Resources.FindObjectsOfTypeAll<CustomPassVolume>();
            var sb = new StringBuilder();
            foreach (var v in volumes)
                sb.Append($"[{v.injectionPoint} global={v.isGlobal} enabled={v.enabled}] ");
            Mod.Log.Info($"[BubbleW] CustomPassVolume 共 {volumes.Length} 个：{sb}");
        }

        private void TearDownVolume()
        {
            if (m_VolumeGo != null)
                Object.Destroy(m_VolumeGo);
            m_VolumeGo = null;
            m_Pass = null;
        }

        private void DestroyDrawAssets()
        {
            if (m_TextMesh != null) Object.Destroy(m_TextMesh);
            if (m_QuadMesh != null) Object.Destroy(m_QuadMesh);
            if (m_TextMat != null) Object.Destroy(m_TextMat);
            if (m_QuadMat != null) Object.Destroy(m_QuadMat);
            m_TextMesh = null;
            m_QuadMesh = null;
            m_TextMat = null;
            m_QuadMat = null;
        }

        /// <summary>手写文本网格（TextMesh 已弃：三连 NRE 实锤在本环境坏死）。逐字四边形+法线+双面索引。</summary>
        private Mesh? BuildTextMesh(string text, Font font, int fontSize, float charScale)
        {
            font.RequestCharactersInTexture(text, fontSize);
            var verts = new List<Vector3>(text.Length * 4);
            var uvs = new List<Vector2>(text.Length * 4);
            var tris = new List<int>(text.Length * 12);
            float penX = 0;
            var glyphs = 0;
            foreach (var ch in text)
            {
                if (!font.GetCharacterInfo(ch, out CharacterInfo ci, fontSize))
                {
                    Mod.Log.Warn($"[BubbleW] 字体缺字形：U+{(int)ch:X4}（{ch}）");
                    continue;
                }
                int b = verts.Count;
                float x0 = penX + ci.minX * charScale, x1 = penX + ci.maxX * charScale;
                float y0 = ci.minY * charScale, y1 = ci.maxY * charScale;
                verts.Add(new Vector3(x0, y0, 0));
                verts.Add(new Vector3(x1, y0, 0));
                verts.Add(new Vector3(x1, y1, 0));
                verts.Add(new Vector3(x0, y1, 0));
                uvs.Add(ci.uvBottomLeft);
                uvs.Add(ci.uvBottomRight);
                uvs.Add(ci.uvTopRight);
                uvs.Add(ci.uvTopLeft);
                tris.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                tris.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
                penX += ci.advance * charScale;
                glyphs++;
            }
            if (glyphs == 0)
            {
                Mod.Log.Warn("[BubbleW] 一个字形都没排到");
                return null;
            }
            for (int i = 0; i < verts.Count; i++)
                verts[i] = new Vector3(verts[i].x - penX / 2f, verts[i].y, 0f);
            var mesh = new Mesh { name = "CityLifeBubbleText" };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        /// <summary>对照组网格：边长 size 的正方形四边形（含法线，双面索引）。</summary>
        private static Mesh BuildQuadMesh(float size)
        {
            var h = size / 2f;
            var mesh = new Mesh { name = "CityLifeQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-h, 0, 0), new Vector3(h, 0, 0),
                new Vector3(h, size, 0), new Vector3(-h, size, 0),
            };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        /// <summary>着色器：HDRP/Unlit 首选（语义已知的 HDRP 原生，必渲染）。</summary>
        private static Shader? PickShader()
        {
            var hdrp = Shader.Find("HDRP/Unlit");
            if (hdrp != null)
                return hdrp;
            foreach (var s in Resources.FindObjectsOfTypeAll<Shader>())
                if (s.name.Contains("NetName") || s.name.Contains("AreaName"))
                    return s;
            return null;
        }

        /// <summary>CJK 字体：候选名轮试，全灭则枚举 OS 字体打日志留证。</summary>
        private static Font? CreateCjkFont()
        {
            foreach (var name in new[] { "msyh", "Microsoft YaHei", "SimHei", "SimSun", "Noto Sans SC" })
            {
                try
                {
                    var f = Font.CreateDynamicFontFromOSFont(name, 64);
                    if (f != null && f.material != null)
                    {
                        Mod.Log.Info($"[BubbleW] CJK 字体就绪：{name}");
                        return f;
                    }
                }
                catch { }
            }
            try
            {
                var names = Font.GetOSInstalledFontNames();
                var sb = new StringBuilder();
                for (int i = 0; i < names.Length && i < 24; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(names[i]);
                }
                Mod.Log.Warn($"[BubbleW] OS 字体候选全灭，已安装字体前 24：{sb}");
            }
            catch { }
            return null;
        }
    }

    /// <summary>
    /// 气泡渲染 pass（HDRP CustomPass 公开扩展点，Unity HDRP 文档明写的自定义渲染注入方式）：
    /// BeforeTransparent 注入点，cmd.DrawMesh 直进 HDRP 当帧渲染——不依赖场景 GameObject 是否被相机渲染
    /// （GameObject 路、beginCameraRendering+Graphics.DrawMesh 路均已实锤不可见，此路是官方答案）。
    /// </summary>
    public sealed class CityLifeBubblePass : CustomPass
    {
        public Mesh? TextMesh;
        public Mesh? QuadMesh;
        public Material? TextMat;
        public Material? QuadMat;
        public Vector3 Pos;
        public Vector3 QuadPos; // 贴脸对照组位置（相机正前 30m）

        private int m_LogCount;

        protected override void Execute(CustomPassContext ctx)
        {
            var cam = ctx.hdCamera.camera;
            // 判定项：pass 到底跑没跑、跑在谁身上（前 3 次打日志；玩家实机验证后删）
            if (m_LogCount < 3)
            {
                m_LogCount++;
                CityLife.Mod.Log.Info($"[BubbleW] pass Execute #{m_LogCount} cam={cam.name} pos=({Pos.x:F0},{Pos.y:F0},{Pos.z:F0})");
            }
            // CustomPass 里 DrawMesh 前必须显式 SetRenderTarget（Unity 官方示例标配）——
            // 不设目标绘制可能画空（2026-08-21：pass 在跑但无显示的最后嫌疑）
            CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ctx.cameraDepthBuffer);
            var rot = cam.transform.rotation; // 面向镜头（billboard）
            var cmd = ctx.cmd;
            if (TextMesh != null && TextMat != null)
                cmd.DrawMesh(TextMesh, Matrix4x4.TRS(Pos, rot, Vector3.one), TextMat);
            if (QuadMesh != null && QuadMat != null)
                cmd.DrawMesh(QuadMesh, Matrix4x4.TRS(QuadPos, rot, Vector3.one), QuadMat);
        }
    }
}
