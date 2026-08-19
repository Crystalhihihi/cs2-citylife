using System;
using System.Reflection;
using Game.City;
using Unity.Entities;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 财政原语（写回层）：真扣/回补城市财政。拷问定案纪律：
    /// - 回补永不超过花费（净收益恒负，杜绝套利）——上限钳制在调用方；
    /// - 无限钱模式（m_Unlimited）下扣款/回补都跳过，如实记账即可；
    /// - 版本敏感点全部收敛于此：PlayerMoney.m_Money 是 protected 字段（2026-08-20 dump 实锤），
    ///   只能反射读写；kMaxMoney 是公开静态上限。
    /// </summary>
    public static class MoneyOps
    {
        private static readonly FieldInfo s_MoneyField =
            typeof(PlayerMoney).GetField("m_Money", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>财政是否可写（字段解析成功 + 非无限钱模式）。</summary>
        public static bool Available(EntityManager em, Entity city)
        {
            if (s_MoneyField == null || city == Entity.Null || !em.HasComponent<PlayerMoney>(city))
                return false;
            return !em.GetComponentData<PlayerMoney>(city).m_Unlimited;
        }

        /// <summary>读当前财政（无限钱模式返回 -1）。</summary>
        public static int Current(EntityManager em, Entity city)
        {
            if (s_MoneyField == null || city == Entity.Null || !em.HasComponent<PlayerMoney>(city))
                return -1;
            var pm = em.GetComponentData<PlayerMoney>(city);
            if (pm.m_Unlimited)
                return -1;
            object boxed = pm;
            return (int)s_MoneyField.GetValue(boxed)!;
        }

        /// <summary>
        /// 调整财政（delta 可正可负），钳制 [0, kMaxMoney]。返回实际生效的新值；不可写返回 false。
        /// </summary>
        public static bool TryAdjust(EntityManager em, Entity city, int delta, out int newAmount, Action<string> log)
        {
            newAmount = 0;
            if (!Available(em, city))
            {
                log("[Money] 财政不可写（字段变动或无限钱模式），跳过调整");
                return false;
            }

            var pm = em.GetComponentData<PlayerMoney>(city);
            object boxed = pm;
            var cur = (int)s_MoneyField!.GetValue(boxed)!;
            var next = (int)Math.Clamp((long)cur + delta, 0, PlayerMoney.kMaxMoney);
            s_MoneyField!.SetValue(boxed, next);
            em.SetComponentData(city, (PlayerMoney)boxed);

            newAmount = next;
            log($"[Money] 财政 {(delta >= 0 ? "+" : "")}{delta} → {next}");
            return true;
        }
    }
}
