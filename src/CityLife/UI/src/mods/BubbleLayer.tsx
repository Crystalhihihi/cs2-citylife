import { useMemo } from "react";
import { useValue } from "cs2/api";
import { bubblesBinding } from "mods/bindings";
import styles from "./BubbleLayer.module.css";

// 气泡结构（线格式契约见 bindings.ts 的 bubblesBinding 注释）
interface Bubble {
    i: number; // 锚点实体 index（React key + 文案稳定性都靠它）
    k: number; // 锚点类型：0 人 1 车 2 楼（配色用）
    x: number; // 屏宽百分比 0-100
    y: number; // 屏高百分比 0-100（已翻转为 CSS 自上而下）
    t: string;
}

// 解析 + 形状校验：坏数据一律空数组——宁可不显示，绝不渲染半个气泡
const parse = (json: string): Bubble[] => {
    if (!json || json.length < 3) return [];
    try {
        const o = JSON.parse(json);
        if (!Array.isArray(o)) return [];
        return o.filter(
            (b: unknown): b is Bubble =>
                typeof (b as Bubble)?.i === "number" &&
                typeof (b as Bubble)?.k === "number" &&
                typeof (b as Bubble)?.x === "number" &&
                typeof (b as Bubble)?.y === "number" &&
                typeof (b as Bubble)?.t === "string"
        );
    } catch {
        return [];
    }
};

/** M3-spike 气泡层（"Game" overlay）：整层 pointer-events:none，绝不拦截游戏操作。 */
export const BubbleLayer = () => {
    const json = useValue(bubblesBinding);
    // binding 值（整串 JSON）没变就不重解析（10Hz 重推节流后的主防线）
    const bubbles = useMemo(() => parse(json), [json]);
    if (bubbles.length === 0) return null;
    return (
        <div className={styles.layer}>
            {bubbles.map((b) => (
                <div
                    key={b.i}
                    className={
                        styles.bubble +
                        " " +
                        (b.k === 1
                            ? styles.bubbleCar
                            : b.k === 2
                              ? styles.bubbleBuilding
                              : styles.bubblePerson)
                    }
                    style={{ left: b.x + "%", top: b.y + "%" }}
                >
                    {b.t}
                </div>
            ))}
        </div>
    );
};
