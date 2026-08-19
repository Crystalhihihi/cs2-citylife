import {
    useEffect,
    useMemo,
    useRef,
    useState,
    PointerEvent as ReactPointerEvent,
    KeyboardEvent as ReactKeyboardEvent,
} from "react";
import { Button } from "cs2/ui";
import { useValue } from "cs2/api";
import { postsBinding, uiLog, mayorPost, FeedPost } from "mods/bindings";
import styles from "./CityLifePanel.module.css";

// 内联 SVG 聊天气泡图标（硬编码 fill；禁用 emoji/符号字形——游戏字体缺字形会变豆腐块）
const ChatIcon = (
    <svg viewBox="0 0 24 24" width="100%" height="100%">
        <path
            fill="#ffffff"
            d="M4 2h16a2 2 0 0 1 2 2v11a2 2 0 0 1-2 2H9l-5 5v-5H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2z"
        />
    </svg>
);

// 缩放手柄的内联 SVG 三角（右下角，硬编码 fill 同上）
const ResizeIcon = (
    <svg viewBox="0 0 12 12" width="100%" height="100%">
        <path fill="rgba(255,255,255,0.35)" d="M12 0 L12 12 L0 12 Z" />
    </svg>
);

// 面板一次最多渲染的帖子数（FeedStore 上限 100，截尾防爆高）
const kMaxShown = 50;

// 面板尺寸边界（需求：min 280x320 / max 720x80vh）
const kMinW = 280;
const kMinH = 320;
const kMaxW = 720;

// 列表高度 = 面板高 - chrome。
// chrome = 上下 padding(20) + 头部(24+8+1+6=39) + tab 行(30) + 发帖框(40)。
// 实机教训（2026-08-19）：cohtml 的 flex 子项收缩不可靠，列表高度由 TSX 算死 px，不走 flex；
// 几何改动必须 TSX 常量与 CSS 注释全链同步核对（面板底部加输入框的溢出教训）。
const kTabsH = 30; // tab 行：按钮高 24 + margin-bottom 6（见 css .tabs 注释）
const kComposerH = 40; // 发帖框：margin-top 6 + 高 34（见 css .composer 注释）
const kChromeH = 60 + kTabsH + kComposerH; // 60 = M2-B 原 chrome（头部 39 + 上下 padding 20，取整）

// 贴底判定：距底 <30px 视为贴底——贴底时新帖自动滚底，否则亮"有新帖"气泡
const kStickPx = 30;

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));

// ── 板块 tab（M2-C）：纯客户端过滤，posts 数据本身不变 ──
type TabKey = "all" | "breaking" | "hot" | "serial";
const kTabs: { key: TabKey; label: string }[] = [
    { key: "all", label: "综合" },
    { key: "breaking", label: "突发" },
    { key: "hot", label: "热帖" },
    { key: "serial", label: "连载" },
];
const kTabFilter: Record<TabKey, (p: FeedPost) => boolean> = {
    all: () => true,
    breaking: (p) => p.k === "Breaking",
    hot: (p) => (p.c?.length ?? 0) >= 5,
    serial: (p) => p.t.startsWith("更新：") || p.t.startsWith("后续："),
};

// 市长帖判定（契约：a === "市长"，p === "mayor" 为人格 id 佐证；UI 只认 a）
const isMayorPost = (p: FeedPost) => p.a === "市长";

/** 市民信息流入口：GameTopLeft 浮动按钮 + 自绘面板（M2-B：评论区/自绘滚动条/拖拽/缩放）。 */
export const CityLifeButton = () => {
    const [open, setOpen] = useState(false);

    const toggle = () => {
        // 验证 UI → C# 通道：开合时经 uiLog TriggerBinding 转发一行进 Mod.Log
        uiLog(open ? "面板关闭" : "面板打开");
        setOpen(!open);
    };

    return (
        <>
            <Button variant="floating" onSelect={toggle}>
                {ChatIcon}
            </Button>
            {open && <CityLifePanel />}
        </>
    );
};

const CityLifePanel = () => {
    const json = useValue(postsBinding);

    // JSON 解析缓存：binding 值（整串 JSON）没变就不重解析。
    // 解析失败兜底空数组，绝不让面板白屏。
    const posts = useMemo<FeedPost[]>(() => {
        try {
            const arr = JSON.parse(json);
            return Array.isArray(arr) ? arr : [];
        } catch {
            return [];
        }
    }, [json]);

    // 面板位置与尺寸（拖拽移动 / 缩放手柄改写；几何一律 px 内联，见 kChromeH 注释）
    const [pos, setPos] = useState({ x: 10, y: 80 });
    const [size, setSize] = useState({ w: 360, h: 520 });

    // 评论展开状态：key = 帖序号 n，缺省即收起
    const [expanded, setExpanded] = useState<Record<number, boolean>>({});
    const toggleComments = (n: number) =>
        setExpanded((prev) => ({ ...prev, [n]: !prev[n] }));

    // 当前板块 tab：切换时重置贴底（回到过滤后列表底部），不影响既有滚动条逻辑
    const [tab, setTab] = useState<TabKey>("all");

    // 市长发帖框草稿（受控 textarea）
    const [draft, setDraft] = useState("");

    // 提交：UI 侧先 trim，空文本/纯空白拒发；成功后清空草稿，经 trigger 进 C#
    const submitMayor = () => {
        const text = draft.trim();
        if (!text) return;
        mayorPost(text);
        setDraft("");
    };

    // Ctrl+Enter 快捷提交；IME 组词中的按键不触发（中文输入法回车选词防误发）
    const onComposerKeyDown = (e: ReactKeyboardEvent<HTMLTextAreaElement>) => {
        if (e.nativeEvent.isComposing) return;
        if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
            e.preventDefault();
            submitMayor();
        }
    };

    // 滚动：贴底状态用 ref（effect 回调里要读最新值，不进渲染）；
    // 滚动度量用 state（驱动自绘滚动条 thumb 的尺寸与位置）
    const listRef = useRef<HTMLDivElement>(null);
    const stickRef = useRef(true);
    const [hasNew, setHasNew] = useState(false);
    const [metrics, setMetrics] = useState({ top: 0, height: 0, client: 1 });

    const readMetrics = () => {
        const el = listRef.current;
        if (el)
            setMetrics({
                top: el.scrollTop,
                height: el.scrollHeight,
                client: el.clientHeight,
            });
    };

    // 新帖到达（json 变化）：贴底则自动滚底，否则亮"有新帖"气泡；
    // tab 切换也走这里——切 tab 前置贴底，效果即滚到过滤后列表底部
    useEffect(() => {
        const el = listRef.current;
        if (!el) return;
        if (stickRef.current) {
            el.scrollTop = el.scrollHeight;
            setHasNew(false);
        } else {
            setHasNew(true);
        }
        readMetrics();
    }, [json, tab]);

    const switchTab = (key: TabKey) => {
        stickRef.current = true;
        setHasNew(false);
        setTab(key);
    };

    // 缩放后面板几何变化，重算 thumb
    useEffect(readMetrics, [size]);

    const onScroll = () => {
        const el = listRef.current;
        if (!el) return;
        const atBottom =
            el.scrollHeight - el.scrollTop - el.clientHeight < kStickPx;
        stickRef.current = atBottom;
        if (atBottom) setHasNew(false); // 回到底部即消气泡
        readMetrics();
    };

    // 点击"有新帖"气泡跳底
    const jumpBottom = () => {
        const el = listRef.current;
        if (!el) return;
        el.scrollTop = el.scrollHeight;
        stickRef.current = true;
        setHasNew(false);
        readMetrics();
    };

    // 拖拽/缩放/拖 thumb 共用的指针跟踪：pointerdown 起手后在 window 上跟 move/up。
    // （city-storytelling-mod 的 useDrag 先例证实 pointer 事件在 cohtml 可用）
    const trackPointer = (onMove: (ev: PointerEvent) => void) => {
        const onUp = () => {
            window.removeEventListener("pointermove", onMove);
            window.removeEventListener("pointerup", onUp);
        };
        window.addEventListener("pointermove", onMove);
        window.addEventListener("pointerup", onUp);
    };

    // 头部拖拽移动（钳在屏幕内，防止拖丢）
    const onHeaderPointerDown = (e: ReactPointerEvent<HTMLDivElement>) => {
        if (e.button !== 0) return;
        e.preventDefault();
        const sx = e.clientX;
        const sy = e.clientY;
        const orig = pos;
        trackPointer((ev) => {
            setPos({
                x: clamp(orig.x + ev.clientX - sx, 0, window.innerWidth - 60),
                y: clamp(orig.y + ev.clientY - sy, 0, window.innerHeight - 40),
            });
        });
    };

    // 右下角缩放手柄：min 280x320 / max 720x80vh
    const onResizePointerDown = (e: ReactPointerEvent<HTMLDivElement>) => {
        if (e.button !== 0) return;
        e.preventDefault();
        e.stopPropagation();
        const sx = e.clientX;
        const sy = e.clientY;
        const orig = size;
        const maxH = Math.round(window.innerHeight * 0.8);
        trackPointer((ev) => {
            setSize({
                w: clamp(orig.w + ev.clientX - sx, kMinW, kMaxW),
                h: clamp(orig.h + ev.clientY - sy, kMinH, maxH),
            });
        });
    };

    // 自绘滚动条几何：thumb 高按 可视/全文 比例缩放，位置随 scrollTop 同步
    const listH = size.h - kChromeH;
    const scrollable = metrics.height > metrics.client + 1;
    const thumbH = scrollable
        ? Math.max(24, Math.round((listH * metrics.client) / metrics.height))
        : 0;
    const thumbY = scrollable
        ? Math.round(
              ((listH - thumbH) * metrics.top) /
                  (metrics.height - metrics.client)
          )
        : 0;

    // 拖动 thumb：位移按 内容可滚距离/thumb 可走距离 换算成 scrollTop
    const onThumbPointerDown = (e: ReactPointerEvent<HTMLDivElement>) => {
        if (e.button !== 0) return;
        e.preventDefault();
        e.stopPropagation();
        const el = listRef.current;
        if (!el || !scrollable) return;
        const sy = e.clientY;
        const startTop = el.scrollTop;
        const ratio =
            (el.scrollHeight - el.clientHeight) / Math.max(1, listH - thumbH);
        trackPointer((ev) => {
            el.scrollTop = startTop + (ev.clientY - sy) * ratio;
        });
    };

    // 先按当前 tab 过滤再截尾：key={p.n} 用帖序号，过滤只增减条目不改 key，复用稳定
    const shown = posts.filter(kTabFilter[tab]).slice(-kMaxShown);

    // 评论区：有 c 才渲染。收起态显示首条作"神评"；展开态列全部评论。
    const renderComments = (p: FeedPost) => {
        const cs = p.c;
        if (!cs || cs.length === 0) return null;
        const open = !!expanded[p.n];
        return (
            <div className={styles.comments}>
                {open ? (
                    cs.map((c, i) => (
                        <div className={styles.comment} key={i}>
                            <span className={styles.commentAuthor}>{c[0]}</span>
                            <span>{c[1]}</span>
                        </div>
                    ))
                ) : (
                    <div className={styles.comment}>
                        <span className={styles.bestTag}>神评：</span>
                        <span className={styles.commentAuthor}>{cs[0][0]}</span>
                        <span>{cs[0][1]}</span>
                    </div>
                )}
                {cs.length > 1 && (
                    <div
                        className={styles.commentToggle}
                        onClick={() => toggleComments(p.n)}
                    >
                        {open ? "收起评论" : `共 ${cs.length} 条评论`}
                    </div>
                )}
            </div>
        );
    };

    return (
        <div
            className={styles.panel}
            style={{
                left: pos.x,
                top: pos.y,
                width: size.w,
                height: size.h,
            }}
        >
            <div className={styles.header} onPointerDown={onHeaderPointerDown}>
                市民信息流
            </div>
            <div className={styles.tabs}>
                {kTabs.map((t) => (
                    <div
                        key={t.key}
                        className={
                            tab === t.key ? styles.tabActive : styles.tab
                        }
                        onClick={() => switchTab(t.key)}
                    >
                        {t.label}
                    </div>
                ))}
            </div>
            <div className={styles.listWrap} style={{ height: listH }}>
                <div className={styles.list} ref={listRef} onScroll={onScroll}>
                    {shown.length === 0 && (
                        <div className={styles.empty}>
                            {posts.length === 0
                                ? "首批 AI 帖子生成中，约需一分钟…"
                                : "该板块暂无帖子"}
                        </div>
                    )}
                    {shown.map((p) => (
                        <div
                            className={
                                isMayorPost(p) ? styles.postMayor : styles.post
                            }
                            key={p.n}
                        >
                            <span className={styles.author}>{p.a}</span>
                            {isMayorPost(p) && (
                                <span className={styles.mayorBadge}>市长</span>
                            )}
                            <span className={styles.text}>{p.t}</span>
                            <div className={styles.topic}>#{p.k}</div>
                            {renderComments(p)}
                        </div>
                    ))}
                </div>
                {scrollable && (
                    <div className={styles.track}>
                        <div
                            className={styles.thumb}
                            style={{
                                height: thumbH,
                                transform: `translateY(${thumbY}px)`,
                            }}
                            onPointerDown={onThumbPointerDown}
                        />
                    </div>
                )}
                {hasNew && (
                    <div className={styles.newBubble} onClick={jumpBottom}>
                        有新帖
                    </div>
                )}
            </div>
            <div className={styles.composer}>
                <textarea
                    className={styles.mayorInput}
                    value={draft}
                    placeholder="以市长身份发言…"
                    rows={2}
                    onChange={(e) => setDraft(e.target.value)}
                    onKeyDown={onComposerKeyDown}
                />
                <div className={styles.sendBtn} onClick={submitMayor}>
                    发送
                </div>
            </div>
            <div
                className={styles.resizeHandle}
                onPointerDown={onResizePointerDown}
            >
                {ResizeIcon}
            </div>
        </div>
    );
};

// ── dev 自测区块（勿带进发布）────────────────────────────────────────────
// 用法：注释掉上面真实的 `const shown = posts.filter(...).slice(-kMaxShown);`，
// 并取消下面这段的注释，npm run build 后进游戏验证：
// 评论展开/收起、自绘滚动条拖动、贴底自动滚动与"有新帖"气泡、
// 市长帖徽标与左边框高亮（首条 mock 市长帖；tab 过滤逻辑走真实数据，mock 模式下 shown 为静态不过滤）、
// 发帖框发送/Ctrl+Enter/空文本拒发。验毕恢复。
// const kMockMayor: FeedPost = {
//     a: "市长",
//     t: "更新：地铁三号线延误已定位为信号故障，抢修预计两小时内完成，早高峰请优先换乘二号线。",
//     k: "Breaking",
//     p: "mayor",
//     n: 0,
//     c: [
//         ["李翠花", "终于有个准信了。"],
//         ["赵四", "二号线也挤爆了啊市长。"],
//         ["王五", "抢修师傅辛苦。"],
//         ["刘能", "建议加开临时摆渡车。"],
//         ["赵六", "已改骑共享单车，真香。"],
//     ] as [string, string][],
// };
// const kMockPosts: FeedPost[] = [
//     kMockMayor,
//     ...Array.from({ length: 30 }, (_, i) => ({
//     a: `市民${i + 1}号`,
//     t:
//         i % 3 === 0
//             ? "地铁三号线又延误了，早高峰在站台堵了四十分钟，这个月全勤奖泡汤，城市的公共交通到底什么时候能靠谱一回？"
//             : "今天下班路过中央公园，新修的滨河步道亮灯之后还挺好看。",
//     k: i % 2 === 0 ? "交通出行" : "城市生活",
//     p: "grumpy",
//     n: i + 1,
//     e: [100 + i, 1] as [number, number],
//     c:
//         i % 4 === 0
//             ? ([
//                   ["李翠花", "同一个世界同一个站台，我也迟到了。"],
//                   ["赵四", "打市长热线有用吗？"],
//                   ["王五", "习惯就好，我已经改骑共享单车了。"],
//               ] as [string, string][])
//             : i % 4 === 1
//               ? ([["李翠花", "改天一起去散步。"]] as [string, string][])
//               : undefined,
// }))];
// const shown = kMockPosts;
