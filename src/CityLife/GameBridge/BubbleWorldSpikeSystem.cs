using System.Text;
using Game;
using Game.Buildings;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Transform = Game.Objects.Transform;

namespace CityLife.GameBridge
{
    /// <summary>
    /// M3-W S1：世界空间渲染气泡探索（2026-08-20 玩家定案：放弃 cohtml 屏幕贴纸路线，
    /// 直接在 HDRP 3D 管线做；cohtml spike 保留作对照不再演进）。
    ///
    /// S1 判定三件事：
    /// 1. 着色器从哪来——运行时无法编译着色器（无 Unity 编辑器/AssetBundle），只能复用游戏
    ///    已加载的：`Resources.FindObjectsOfTypeAll<Shader>()` 全量枚举找世界文本/SDF 候选
    ///    （游戏自己的道路名/建筑名就是 3D 文本，着色器必已加载），备选 HDRP/Unlit；
    /// 2. 文本网格从哪来——UnityEngine.TextMesh 只借网格不渲染本体（绕开手写字形布局），
    ///    字体走系统字库（new Font("msyh")，微软雅黑全 CJK，mod 不打包字体文件）；
    /// 3. 出不出字——Ctrl+9 在某建筑顶上渲染一个静态气泡（出字清晰/不紫不黑=管线通）。
    /// 已知未知项：Font 图集（Alpha8）在 HDRP/Unlit 下的采样行为——实测见分晓
    /// （RGB=0 则黑字白底气泡正好；RGB=1 则 BaseColor 给黑）。S2/S3 视 S1 结果推进。
    /// </summary>
    public partial class BubbleWorldSpikeSystem : GameSystemBase
    {
        private bool m_ShadersDumped;
        private Font? m_Font;
        private GameObject? m_Bubble;
        private bool m_Active;
        private uint m_Frame;
        private uint m_LastKeyFrame;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 1;

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

            // 面向镜头（顺手验证：S2 的朝向方案就是它；相机查找与 cohtml spike 同纪律——
            // Camera.main 在某些阶段为 null，退 allCameras[0]）
            if (m_Active && m_Bubble != null)
            {
                var cam = Camera.main != null ? Camera.main
                    : Camera.allCameras.Length > 0 ? Camera.allCameras[0] : null;
                if (cam != null)
                    m_Bubble.transform.rotation = cam.transform.rotation;
            }
            m_Frame++;
        }

        /// <summary>全量枚举已加载着色器：找游戏自己的世界文本着色器（道路名/建筑名同款），零成本复用。</summary>
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
            var all = new StringBuilder(shaders.Length * 24);
            foreach (var s in shaders)
                all.Append(s.name).Append(" | ");
            Mod.Log.Info($"[BubbleW] 全量：{all}");
        }

        private void Toggle()
        {
            if (m_Active)
            {
                if (m_Bubble != null)
                    Object.Destroy(m_Bubble);
                m_Bubble = null;
                m_Active = false;
                Mod.Log.Info("[BubbleW] 单气泡已销毁");
                return;
            }

            // 锚点：任意一栋建筑（S1 静态验证）
            var q = GetEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            var arr = q.ToEntityArray(Unity.Collections.Allocator.Temp);
            if (arr.Length == 0)
            {
                Mod.Log.Warn("[BubbleW] 城里没有建筑");
                arr.Dispose();
                return;
            }
            var pos = EntityManager.GetComponentData<Transform>(arr[0]).m_Position;
            arr.Dispose();
            CreateBubble(pos + new float3(0, 15f, 0));
        }

        private void CreateBubble(float3 pos)
        {
            m_Font ??= CreateCjkFont();
            if (m_Font == null)
            {
                Mod.Log.Warn("[BubbleW] CJK 字体创建失败（msyh 与 OS 字体都拿不到）");
                return;
            }

            // TextMesh 只借网格（文本网格+Font 图集），不渲染本体
            var tmGo = new GameObject("CityLifeBubbleTextGen");
            tmGo.hideFlags = HideFlags.HideAndDontSave;
            var tm = tmGo.AddComponent<TextMesh>();
            tm.text = "吃了吗";
            tm.font = m_Font;
            tm.fontSize = 64;
            tm.anchor = TextAnchor.LowerCenter; // 以锚点（头顶）为底边中点
            tm.alignment = TextAlignment.Center;
            tm.characterSize = 0.25f; // 世界尺寸（一格 0.25m）
            var mesh = tmGo.GetComponent<MeshFilter>().sharedMesh;

            var shader = PickShader();
            if (shader == null)
            {
                Mod.Log.Warn("[BubbleW] 无可用着色器（文本候选与 HDRP/Unlit 都没找到）");
                Object.Destroy(tmGo);
                return;
            }
            var mat = new Material(shader)
            {
                mainTexture = m_Font.material.mainTexture
            };
            TryMakeTransparent(mat);

            m_Bubble = new GameObject("CityLifeBubbleW");
            m_Bubble.hideFlags = HideFlags.HideAndDontSave;
            m_Bubble.AddComponent<MeshFilter>().sharedMesh = mesh;
            m_Bubble.AddComponent<MeshRenderer>().sharedMaterial = mat;
            m_Bubble.transform.position = new Vector3(pos.x, pos.y, pos.z);
            m_Active = true;
            Mod.Log.Info($"[BubbleW] 单气泡已创建 @({pos.x:F0},{pos.y:F0},{pos.z:F0}) shader={shader.name} fontTex={m_Font.material.mainTexture?.GetType().Name}");
        }

        /// <summary>着色器选择：游戏文本/SDF 候选优先（道路名同款），备选 HDRP/Unlit。</summary>
        private static Shader? PickShader()
        {
            foreach (var s in Resources.FindObjectsOfTypeAll<Shader>())
                if (s.name.Contains("Text") || s.name.Contains("SDF"))
                    return s;
            return Shader.Find("HDRP/Unlit");
        }

        /// <summary>HDRP 透明设置（尽力而为版；透明正确性本就是 S1 判定项之一）。</summary>
        private static void TryMakeTransparent(Material mat)
        {
            // HDRP 材质属性面（运行时已知）：_SurfaceType 1=Transparent；透明队列前置
            if (mat.HasProperty("_SurfaceType"))
                mat.SetFloat("_SurfaceType", 1f);
            if (mat.HasProperty("_ZWriteEnable"))
                mat.SetFloat("_ZWriteEnable", 0f);
            mat.renderQueue = 3000;
        }

        /// <summary>CJK 字体：微软雅黑（Windows 必带），回退 OS 字体名。mod 不打包字体文件。</summary>
        private static Font? CreateCjkFont()
        {
            try { return new Font("msyh"); }
            catch
            {
                try { return Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 32); }
                catch { return null; }
            }
        }
    }
}
