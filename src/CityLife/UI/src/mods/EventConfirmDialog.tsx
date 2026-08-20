import { useEffect, useMemo, useRef, useState } from "react";
import { useValue } from "cs2/api";
import { selectedInfo } from "cs2/bindings";
import { eventConfirmBinding, eventConfirmResult, pickVenue } from "mods/bindings";
import styles from "./EventConfirmDialog.module.css";

// 弹窗请求结构（线格式契约见 bindings.ts 的 eventConfirm 注释）
interface EventConfirmReq {
    id: number;
    title: string;
    lines: string[];
    danger?: boolean;
    venues?: string[];  // 候选场馆标签；缺省或长度 <=1 时不显示选择器
    venueIdx?: number;  // 当前默认推荐的下标
    picked?: string;    // T2：玩家地图选定的场馆名（C# 校验选点后重推）；存在时地点行显示它
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
        if (o.picked !== undefined && typeof o.picked !== "string") return null;
        return {
            id: o.id,
            title: o.title,
            lines: o.lines,
            danger: o.danger === true,
            venues: o.venues,
            venueIdx: o.venueIdx,
            picked: o.picked,
        };
    } catch {
        return null;
    }
};

// 场馆选择器的左右箭头：内联 SVG 三角（硬编码 fill；
// 不用 ◀ ▶ 字符——符号字形在游戏字体里会变豆腐块，与面板图标同一纪律；emoji 同理禁用）
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
 *  面板没开也能弹。空串/已应答时返回 null 完全不渲染，不留隐形拦截层挡玩家操作。
 *  T2 地图选点：选点模式下整卡收起只剩提示条（pointer-events:none），地图点击直达游戏。 */
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

    // T2 地图选点：pickMode=选点模式（整卡收起等玩家点图）；
    // clearedPick=玩家点了"改回列表"（req.picked 还在但本地不再采用）
    const [pickMode, setPickMode] = useState(false);
    const [clearedPick, setClearedPick] = useState(false);

    useEffect(() => {
        if (!req) return;
        const max = Math.max(0, (req.venues?.length ?? 1) - 1);
        setVenueIdx(Math.min(Math.max(req.venueIdx ?? 0, 0), max));
        setClearedPick(false); // 新卡/新推送：picked 以 C# 为准，清掉本地"改回"状态
    }, [req]);

    const visible = req !== null && req.id !== dismissedId;

    // 地图选定展示值：本地点过"改回列表"就不再采用 C# 的 picked（提前声明，供 Esc 闭包引用）
    const pickedShown = clearedPick ? undefined : req?.picked;

    // 玩家在游戏里的当前选中实体（游戏自带 binding，selectedInfo 命名空间下——
    // 与 camera.focusEntity 同纪律：cs2/bindings 无顶层导出，必须走命名空间）。
    // 进入选点模式时记录当时的选中，之后"变化"才算一次点选——防止把进入前随手选着的东西当场馆
    const selected = useValue(selectedInfo.selectedEntity$);
    const pickEntryRef = useRef<{ index: number; version: number } | null>(null);

    // 选点模式下监听选中变化：非空且与进入时不同 → 发给 C#（校验/应用由 C# 做）并退回卡片
    useEffect(() => {
        if (!pickMode || !req) return;
        const e = selected;
        if (!e || e.index === 0) return;
        const entry = pickEntryRef.current;
        if (entry && entry.index === e.index && entry.version === e.version)
            return;
        pickVenue(req.id, e.index, e.version);
        setPickMode(false);
    }, [pickMode, selected, req]);

    // Esc：选点模式只退出选点（不取消整张卡）；否则=取消。只在可见时挂监听，隐藏即摘除
    useEffect(() => {
        if (!visible || !req) return;
        const onKey = (e: KeyboardEvent) => {
            if (e.key === "Escape") {
                e.preventDefault();
                if (pickMode) {
                    setPickMode(false);
                    return;
                }
                eventConfirmResult(req.id, false, pickedShown ? -1 : venueIdx);
                setDismissedId(req.id);
            }
        };
        window.addEventListener("keydown", onKey);
        return () => window.removeEventListener("keydown", onKey);
    }, [visible, req, venueIdx, pickMode, clearedPick]);

    if (!visible || !req) return null;

    // 选点模式：整卡收起，只剩不拦截点击的提示条（地图点击直达游戏选建筑）
    if (pickMode) {
        return (
            <div className={styles.pickHint}>
                点击地图上的建筑作为活动场地 · Esc 返回
            </div>
        );
    }

    // 应答：发结果（地图选定时 venueIdx 发 -1）+ 本地立即隐藏；C# 收到后清空 binding（幂等）
    const settle = (accept: boolean) => {
        eventConfirmResult(req.id, accept, pickedShown ? -1 : venueIdx);
        setDismissedId(req.id);
    };

    // 进入选点模式：记录当前选中（防止误把它当场馆），整卡收起等点图
    const enterPickMode = () => {
        pickEntryRef.current = selected ?? null;
        setPickMode(true);
    };

    // 场馆循环切换（◀ ▶ 箭头，比下拉列表在 cohtml 里稳）
    const venues = req.venues;
    const hasVenuePicker = !!venues && venues.length > 1 && !pickedShown;
    const cycleVenue = (delta: number) => {
        if (!venues || venues.length === 0) return;
        setVenueIdx((prev) => (prev + delta + venues.length) % venues.length);
    };

    // 渲染一行：地点行按状态替换——地图选定（带"改回列表"）> 箭头-场馆-箭头 > 原文
    const renderLine = (line: string, i: number) => {
        if (line.startsWith("地点：")) {
            if (pickedShown) {
                return (
                    <div className={styles.venueRow} key={i}>
                        <span>地点：</span>
                        <span className={styles.venueName}>{pickedShown}</span>
                        <span
                            className={styles.clearPick}
                            onClick={() => setClearedPick(true)}
                        >
                            改回列表
                        </span>
                    </div>
                );
            }
            if (hasVenuePicker) {
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
                    <div className={styles.btnPick} onClick={enterPickMode}>
                        在地图上选点
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
// picked 时地点行显示选定名+"改回列表"、"在地图上选点"进入选点模式（整卡收起只剩提示条）、
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
//     picked: "城西·口袋公园",
// };
// const req: EventConfirmReq | null = kMockReq;
