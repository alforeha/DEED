namespace Deed.Core;

public static class StraightLine
{
    public static Vector2D Displacement(Distance distance, Azimuth azimuth)
    {
        double radians = azimuth.Degrees * Math.PI / 180.0;
        return new Vector2D(
            distance.Value * Math.Sin(radians),
            distance.Value * Math.Cos(radians));
    }

    public static Coordinate2D Endpoint(Coordinate2D start, Distance distance, Azimuth azimuth) =>
        start.Translate(Displacement(distance, azimuth));
}
