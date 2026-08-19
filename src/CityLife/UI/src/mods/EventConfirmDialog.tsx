import { useEffect, useMemo, useState } from "react";
import { useValue } from "cs2/api";
import { eventConfirmBinding, eventConfirmResult } from "mods/bindings";
import styles from "./EventConfirmDialog.module.css";

// 弹窗请求结构（线格式契约见 bindings.ts 的 eventConfirm 注释）
interface EventConfirmReq {
    id: number;
    title: string;
    lines: string[];
    danger?: boolean;
}

// 解析 + 形状校验：任何字段不对都返回 null——宁可不弹，绝不渲染半个弹窗
const parseReq = (json: string): EventConfirmReq | null => {
    if (!json) return null;
    try {
        const o = JSON.parse(json);
        if (typeof o?.id !== "number") return null;
        if (typeof o?.title !== "string") return null;
        if (
            !Array.isArray(o?.lines) ||
            o.lines.some((s: unknown) => typeof s !== "string")
        )
            return null;
        return {
            id: o.id,
            title: o.title,
            lines: o.lines,
            danger: o.danger === true,
        };
    } catch {
        return null;
    }
};

/** 活动确认弹窗（M4，cohtml overlay）：挂 "Game" 层，独立于市民信息流面板——
 *  面板没开也能弹。空串/已应答时返回 null 完全不渲染，不留隐形拦截层挡玩家操作。 */
export const EventConfirmDialog = () => {
    const json = useValue(eventConfirmBinding);

    // binding 值（整串 JSON）没变就不重解析
    const req = useMemo(() => parseReq(json), [json]);

    // 本地已应答的 id：点确认/取消后立即隐藏（不等 C# 清空 binding），同 id 不再弹；
    // 新 id 到来时 dismissedId 是旧值，自然重新显示
    const [dismissedId, setDismissedId] = useState<number | null>(null);

    const visible = req !== null && req.id !== dismissedId;

    // Esc = 取消；只在可见时挂监听，隐藏即摘除（不对玩家的 Esc 造成任何常驻拦截）
    useEffect(() => {
        if (!visible || !req) return;
        const onKey = (e: KeyboardEvent) => {
            if (e.key === "Escape") {
                e.preventDefault();
                eventConfirmResult(req.id, false);
                setDismissedId(req.id);
            }
        };
        window.addEventListener("keydown", onKey);
        return () => window.removeEventListener("keydown", onKey);
    }, [visible, req]);

    if (!visible || !req) return null;

    // 应答：发结果 + 本地立即隐藏；C# 收到后清空 binding（清不清都已隐藏，幂等）
    const settle = (accept: boolean) => {
        eventConfirmResult(req.id, accept);
        setDismissedId(req.id);
    };

    return (
        <div className={styles.backdrop} onClick={() => settle(false)}>
            <div className={styles.card} onClick={(e) => e.stopPropagation()}>
                <div
                    className={req.danger ? styles.titleDanger : styles.title}
                >
                    {req.title}
                </div>
                {req.lines.map((line, i) => (
                    <div
                        key={i}
                        className={
                            req.danger && i === 0 ? styles.lineWarn : styles.line
                        }
                    >
                        {line}
                    </div>
                ))}
                <div className={styles.buttons}>
                    <div
                        className={styles.btnConfirm}
                        onClick={() => settle(true)}
                    >
                        确认执行
                    </div>
                    <div
                        className={styles.btnCancel}
                        onClick={() => settle(false)}
                    >
                        取消
                    </div>
                </div>
            </div>
        </div>
    );
};

// ── dev 自测区块（勿带进发布）────────────────────────────────────────────
// 用法：注释掉组件里 `const req = useMemo(() => parseReq(json), [json]);` 一行，
// 并取消下面这段的注释，npm run build 后进游戏验证：
// 居中卡片布局、压暗背景点击取消、Esc 取消、确认/取消后立即隐藏、
// danger 改 true 验证警示标题 + 首行警告样式。验毕恢复。
// const kMockReq: EventConfirmReq = {
//     id: 7,
//     title: "活动确认",
//     lines: [
//         "活动：周末市集",
//         "地点：城西北公园",
//         "预算：中档（20万）",
//         "时间：明晚 19:00",
//         "预计规模：约 300 人",
//     ],
//     danger: false,
// };
// const req: EventConfirmReq | null = kMockReq;
