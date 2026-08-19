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
    venues?: string[];  // 候选场馆标签；缺省或长度 <=1 时不显示选择器
    venueIdx?: number;  // 当前默认推荐的下标
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
        // venues 可选，但若存在必须是全 string 数组；venueIdx 可选，但若存在必须是 number
        if (
            o.venues !== undefined &&
            (!Array.isArray(o.venues) ||
                o.venues.some((s: unknown) => typeof s !== "string"))
        )
            return null;
        if (o.venueIdx !== undefined && typeof o.venueIdx !== "number")
            return null;
        return {
            id: o.id,
            title: o.title,
            lines: o.lines,
            danger: o.danger === true,
            venues: o.venues,
            venueIdx: o.venueIdx,
        };
    } catch {
        return null;
    }
};

// 场馆选择器的左右箭头：内联 SVG 三角（硬编码 fill；
// 不用 ◀ ▶ 字符——符号字形在游戏字体里会变豆腐块，与面板图标同一纪律）
const PrevIcon = (
    <svg viewBox="0 0 8 12" width="100%" height="100%">
        <path fill="rgba(255,255,255,0.7)" d="M8 0 L8 12 L0 6 Z" />
    </svg>
);
const NextIcon = (
    <svg viewBox="0 0 8 12" width="100%" height="100%">
        <path fill="rgba(255,255,255,0.7)" d="M0 0 L8 6 L0 12 Z" />
    </svg>
);

/** 活动确认弹窗（M4，cohtml overlay）：挂 "Game" 层，独立于市民信息流面板——
 *  面板没开也能弹。空串/已应答时返回 null 完全不渲染，不留隐形拦截层挡玩家操作。 */
export const EventConfirmDialog = () => {
    const json = useValue(eventConfirmBinding);

    // binding 值（整串 JSON）没变就不重解析
    const req = useMemo(() => parseReq(json), [json]);

    // 本地已应答的 id：点确认/取消后立即隐藏（不等 C# 清空 binding），同 id 不再弹；
    // 新 id 到来时 dismissedId 是旧值，自然重新显示
    const [dismissedId, setDismissedId] = useState<number | null>(null);

    // 场馆选择：当前选中的候选下标。新弹窗（req 变化）重置为 JSON 推荐值并钳进合法区间；
    // 与 dismissedId 互不干扰——那边管"弹不弹"，这边管"选中第几个"
    const [venueIdx, setVenueIdx] = useState(0);
    useEffect(() => {
        if (!req) return;
        const max = Math.max(0, (req.venues?.length ?? 1) - 1);
        setVenueIdx(Math.min(Math.max(req.venueIdx ?? 0, 0), max));
    }, [req]);

    const visible = req !== null && req.id !== dismissedId;

    // Esc = 取消；只在可见时挂监听，隐藏即摘除（不对玩家的 Esc 造成任何常驻拦截）
    useEffect(() => {
        if (!visible || !req) return;
        const onKey = (e: KeyboardEvent) => {
            if (e.key === "Escape") {
                e.preventDefault();
                eventConfirmResult(req.id, false, venueIdx);
                setDismissedId(req.id);
            }
        };
        window.addEventListener("keydown", onKey);
        return () => window.removeEventListener("keydown", onKey);
    }, [visible, req, venueIdx]);

    if (!visible || !req) return null;

    // 应答：发结果（带当前选中场馆下标）+ 本地立即隐藏；C# 收到后清空 binding（清不清都已隐藏，幂等）
    const settle = (accept: boolean) => {
        eventConfirmResult(req.id, accept, venueIdx);
        setDismissedId(req.id);
    };

    // 场馆循环切换（◀ ▶ 箭头，比下拉列表在 cohtml 里稳）
    const venues = req.venues;
    const hasVenuePicker = !!venues && venues.length > 1;
    const cycleVenue = (delta: number) => {
        if (!venues || venues.length === 0) return;
        setVenueIdx((prev) => (prev + delta + venues.length) % venues.length);
    };

    // 渲染一行：有选择器时，"地点："那行替换成 箭头-场馆-箭头（保留原行位置与前缀）
    const renderLine = (line: string, i: number) => {
        if (hasVenuePicker && line.startsWith("地点：")) {
            return (
                <div className={styles.venueRow} key={i}>
                    <span>地点：</span>
                    <span
                        className={styles.venueArrow}
                        onClick={() => cycleVenue(-1)}
                    >
                        {PrevIcon}
                    </span>
                    <span className={styles.venueName}>
                        {venues[venueIdx] ?? venues[0]}
                    </span>
                    <span
                        className={styles.venueArrow}
                        onClick={() => cycleVenue(1)}
                    >
                        {NextIcon}
                    </span>
                </div>
            );
        }
        return (
            <div
                key={i}
                className={
                    req.danger && i === 0 ? styles.lineWarn : styles.line
                }
            >
                {line}
            </div>
        );
    };

    return (
        <div className={styles.backdrop} onClick={() => settle(false)}>
            <div className={styles.card} onClick={(e) => e.stopPropagation()}>
                <div
                    className={req.danger ? styles.titleDanger : styles.title}
                >
                    {req.title}
                </div>
                {req.lines.map(renderLine)}
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
// 场馆选择器循环切换（"地点："行被替换为箭头-场馆-箭头，默认选中 venueIdx 项）、
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
//     venues: ["城东南公园", "市中心公园", "城西北景点"],
//     venueIdx: 1,
// };
// const req: EventConfirmReq | null = kMockReq;
