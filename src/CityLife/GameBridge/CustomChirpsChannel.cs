using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Citizens;
using Game.Common;
using Game.Tools;
using Game.UI;
using Unity.Entities;

namespace CityLife.GameBridge
{
    /// <summary>
    /// CustomChirps 发帖通道（软依赖反射桥，惰性解析——mod 加载顺序不定，2026-08-19 实机教训）。
    ///
    /// 头像与名字纪律（2026-08-20 玩家定案）：
    /// - 首选 PostChirpFromEntity：发送者=城里真实市民实体，头像=该市民本人的脸，零版权风险；
    /// - 名字默认=该市民的**真实姓名**（NameSystem.GetRenderedLabelName 显式读出再传入——
    ///   既保"脸名同一人"，又避开 null 名在 1.6 下的兼容疑团）；
    /// - 仅明星彩蛋/特殊注入作者保留自定义名；明星绑定固定市民实体（人设连续）。
    /// - 不内置任何图片头像（PostLargeChirpWithPortraitImage 是玩家自定义留口，与发布物无关）。
    /// </summary>
    public sealed class CustomChirpsChannel : Content.IChirpChannel
    {
        private const int k_FacePoolSize = 64;

        private readonly World m_World;
        private readonly Action<string> m_Log;
        private readonly EntityQuery m_CitizenQuery;
        private readonly NameSystem m_NameSystem;
        private readonly List<Entity> m_Faces = new();
        private int m_FaceIndex;

        private MethodInfo? m_PostChirp;
        private MethodInfo? m_PostChirpFromEntity;
        private object? m_Department;
        private Entity m_StarFace = Entity.Null;   // 明星作者绑定的固定市民（彩蛋位）

        public CustomChirpsChannel(World world, Action<string> log)
        {
            m_World = world;
            m_Log = log;
            m_CitizenQuery = world.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<Citizen>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
            m_NameSystem = world.GetOrCreateSystemManaged<NameSystem>();
        }

        /// <summary>普通发帖（无实体链接）。</summary>
        public bool TryPost(in Content.Post post) => PostInternal(post, Entity.Null);

        /// <summary>
        /// 带目标实体发帖：正文自动追加 {LINK_1}（CustomChirps 行为），玩家点击镜头聚焦到事件/建筑。
        /// 突发新闻与实体话题的"注意力引导"通道（§4 M6 感知率原则的落点）。
        /// </summary>
        public bool TryPost(in Content.Post post, Entity target) => PostInternal(post, target);

        private bool PostInternal(in Content.Post post, Entity target)
        {
            if (m_PostChirp == null && !TryResolve())
            {
                m_Log($"[Chirper·T0·日志通道] {post.Author}：{post.Text}（话题:{post.Topic}）");
                return true;
            }

            try
            {
                // 首选市民头像路：真实市民当发送者（CustomChirps 会校验 Citizen 组件）
                if (m_PostChirpFromEntity != null)
                {
                    // 明星作者（彩蛋位 Crystalhihihi）绑定固定市民实体——头像与人设恒同一人
                    var face = post.Author == "Crystalhihihi" ? StarFace() : NextFace();
                    if (face != Entity.Null)
                    {
                        var sender = IsSpecialAuthor(post.Author) ? Truncate(post.Author) : RealName(face);
                        // PostChirpFromEntity(string text, Entity citizenSender, Entity target, string customSenderName = null)
                        m_PostChirpFromEntity.Invoke(null, new object[] { post.Text, face, target, sender });
                        // 临时诊断日志（排查"帖子不可见"用，稳定后删除/降频）
                        m_Log($"[Chirp·发] FromEntity 名={sender} target={(target == Entity.Null ? "无" : "有")}：{post.Text.Substring(0, System.Math.Min(18, post.Text.Length))}…");
                        return true;
                    }
                }
                // 退路：部门图标（此时自定义名可空，显示部门名）
                var deptSender = IsSpecialAuthor(post.Author) ? Truncate(post.Author) : null;
                m_PostChirp!.Invoke(null, new object[] { post.Text, m_Department!, target, deptSender });
                m_Log($"[Chirp·发] 部门路 名={deptSender ?? "<部门名>"}：{post.Text.Substring(0, System.Math.Min(18, post.Text.Length))}…");
                return true;
            }
            catch (Exception e)
            {
                m_Log($"[Chirp] 发帖失败（降级跳过）：{e.GetType().Name} {e.Message}");
                return false;
            }
        }

        /// <summary>市民真实姓名（显式读取）；读不到给兜底名，绝不传 null。</summary>
        private string RealName(Entity face)
        {
            try
            {
                var n = m_NameSystem.GetRenderedLabelName(face);
                if (!string.IsNullOrWhiteSpace(n))
                    return Truncate(n);
            }
            catch
            {
                // 姓名系统读失败不致命
            }
            return "热心市民";
        }

        /// <summary>发送者名上限 FixedString128 → 中文约 42 字，留余量截 40。</summary>
        private static string Truncate(string s) => s.Length > 40 ? s.Substring(0, 40) : s;

        /// <summary>特殊注入作者名单（保留自定义名）：明星彩蛋 + 突发新闻。其余一律用市民真实姓名。</summary>
        private static bool IsSpecialAuthor(string author)
            => author == "Crystalhihihi" || author == "现场直击";

        /// <summary>取下一个活着的市民实体；池空重抓（跨步抽样，避免总抓前 64 个）。</summary>
        private Entity NextFace()
        {
            for (int guard = 0; guard < m_Faces.Count + 2; guard++)
            {
                if (m_FaceIndex >= m_Faces.Count)
                    RefillFaces();
                if (m_Faces.Count == 0)
                    return Entity.Null;
                var e = m_Faces[m_FaceIndex++];
                if (m_World.EntityManager.Exists(e))
                    return e;
            }
            return Entity.Null;
        }

        /// <summary>明星脸：固定绑定一个市民实体，被删（搬走/去世）才换下一个——人设连续性靠它。</summary>
        private Entity StarFace()
        {
            if (m_StarFace == Entity.Null || !m_World.EntityManager.Exists(m_StarFace))
                m_StarFace = NextFace();
            return m_StarFace;
        }

        private void RefillFaces()
        {
            m_Faces.Clear();
            var arr = m_CitizenQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            var stride = System.Math.Max(1, arr.Length / k_FacePoolSize);
            for (int i = 0; i < arr.Length && m_Faces.Count < k_FacePoolSize; i += stride)
                m_Faces.Add(arr[i]);
            arr.Dispose();
            m_FaceIndex = 0;
        }

        /// <summary>反射解析：API 类型 + PostChirp（必需）+ PostChirpFromEntity（头像路，可选）+ 部门枚举。</summary>
        private bool TryResolve()
        {
            var apiType = Type.GetType("CustomChirps.Systems.CustomChirpApiSystem, CustomChirps")
                          ?? FindTypeAcrossAssemblies("CustomChirps.Systems.CustomChirpApiSystem");
            if (apiType == null)
                return false;

            var postChirp = apiType.GetMethod("PostChirp", BindingFlags.Public | BindingFlags.Static);
            var fromEntity = apiType.GetMethod("PostChirpFromEntity", BindingFlags.Public | BindingFlags.Static);
            var deptType = apiType.Assembly.GetType("CustomChirps.Systems.DepartmentAccount");
            if (postChirp == null || deptType == null)
                return false;

            object dept;
            try
            {
                dept = Enum.Parse(deptType, "ParkAndRec");
            }
            catch
            {
                dept = Enum.GetValues(deptType).GetValue(0)!;
            }

            m_PostChirp = postChirp;
            m_PostChirpFromEntity = fromEntity; // 可能为 null（老版本没有），走部门图标退路
            m_Department = dept;
            m_Log(fromEntity != null
                ? "[Chirp] CustomChirps 通道接通（含市民头像路）"
                : "[Chirp] CustomChirps 通道接通（仅部门图标）");
            return true;
        }

        private static Type? FindTypeAcrossAssemblies(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName);
                if (t != null)
                    return t;
            }
            return null;
        }
    }
}
