using Unity.Mathematics;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 方位命名工具（GameBridge 内部共享）：相对地图原点的 8 方位+距离带（"城东""市中心"）。
    /// 不读游戏本地化，纯几何计算——锚点标签、场馆候选标签、突发事件定位共用一套口径，
    /// 玩家看到的是一致的方位语言（"城西北那家便利店"和"城西北有建筑起火"说的是同一片）。
    /// </summary>
    public static class Geo
    {
        /// <summary>方位命名：x 东 z 北；dist&lt;500 算市中心，否则按角度分 8 方位。</summary>
        public static string DirectionOf(float3 pos)
        {
            var dist = math.length(pos.xz);
            if (dist < 500f)
                return "市中心";

            var angle = math.degrees(math.atan2(pos.z, pos.x)); // -180..180
            if (angle >= -22.5f && angle < 22.5f) return "城东";
            if (angle >= 22.5f && angle < 67.5f) return "城东北";
            if (angle >= 67.5f && angle < 112.5f) return "城北";
            if (angle >= 112.5f && angle < 157.5f) return "城西北";
            if (angle >= 157.5f || angle < -157.5f) return "城西";
            if (angle >= -157.5f && angle < -112.5f) return "城西南";
            if (angle >= -112.5f && angle < -67.5f) return "城南";
            return "城东南";
        }
    }
}
