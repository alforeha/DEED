namespace Deed.Core;

public readonly record struct Coordinate2D(double Easting, double Northing)
{
    public Coordinate2D Translate(Vector2D displacement) =>
        new(Easting + displacement.Easting, Northing + displacement.Northing);
}
