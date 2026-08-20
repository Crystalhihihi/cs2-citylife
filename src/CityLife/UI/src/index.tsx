import { ModRegistrar } from "cs2/modding";
import { CityLifeButton } from "mods/CityLifePanel";
import { EventConfirmDialog } from "mods/EventConfirmDialog";
import { BubbleLayer } from "mods/BubbleLayer";

// 入口范式：只注册组件，不做任何初始化副作用。
// CityLifeButton 挂 GameTopLeft（左上角原生按钮区），与 InfoLoom 等先例一致；
// EventConfirmDialog 挂 Game（全屏 overlay 层）：活动确认弹窗独立于我局面板，面板没开也能弹；
// BubbleLayer 挂 Game（同 overlay 层，z-index 低于弹窗）：M3-spike 气泡渲染验证。
const register: ModRegistrar = (moduleRegistry) => {
    moduleRegistry.append("GameTopLeft", CityLifeButton);
    moduleRegistry.append("Game", EventConfirmDialog);
    moduleRegistry.append("Game", BubbleLayer);
}

export default register;
