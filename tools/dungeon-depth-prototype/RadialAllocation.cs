namespace DungeonDepthPrototype;

// Штатная зона 64 м содержит не больше одной локации. Чётность работает и для отрицательных координат.
public static class RadialAllocation
{
    public const int ZoneSize = 64, Width = 120, Height = 96, BaseAltitude = 3500, LayerStep = 128;
    public static int Altitude(int zoneX, int zoneZ) =>
        BaseAltitude + LayerStep * ((zoneX & 1) * 2 + (zoneZ & 1));
}
