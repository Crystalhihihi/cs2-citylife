import { ModRegistrar } from "cs2/modding";
import { CityLifeButton } from "mods/CityLifePanel";

// 入口范式：只注册组件，不做任何初始化副作用。
// 挂 GameTopLeft（左上角原生按钮区），与 InfoLoom 等先例一致。
const register: ModRegistrar = (moduleRegistry) => {
    moduleRegistry.append("GameTopLeft", CityLifeButton);
}

export default register;
