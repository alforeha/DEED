using Deed.Core;

namespace Deed.Desktop;

public sealed class CanvasTransform
{
    public double Scale { get; private set; } = 1;
    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }

    public (double X, double Y) ToScreen(Coordinate2D point) =>
        (point.Easting * Scale + OffsetX, -point.Northing * Scale + OffsetY);

    public Coordinate2D ToWorld(double x, double y) =>
        new((x - OffsetX) / Scale, -(y - OffsetY) / Scale);

    public void Pan(double dx, double dy) { OffsetX += dx; OffsetY += dy; }

    public void ZoomAt(double factor, double x, double y)
    {
        if (!double.IsFinite(factor) || factor <= 0) return;
        double next = Math.Clamp(Scale * factor, 1e-8, 1e8);
        double ratio = next / Scale;
        OffsetX = x + (OffsetX - x) * ratio;
        OffsetY = y + (OffsetY - y) * ratio;
        Scale = next;
    }

    public void Fit(IEnumerable<Coordinate2D> points, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return;
        var valid = points.Where(p => double.IsFinite(p.Easting) && double.IsFinite(p.Northing)).ToArray();
        if (valid.Length == 0) { Scale = 1; OffsetX = width / 2; OffsetY = height / 2; return; }
        double minE = valid.Min(p => p.Easting), maxE = valid.Max(p => p.Easting);
        double minN = valid.Min(p => p.Northing), maxN = valid.Max(p => p.Northing);
        double spanE = Math.Max(maxE - minE, 1), spanN = Math.Max(maxN - minN, 1);
        Scale = Math.Clamp(Math.Min(Math.Max(width - 80, 1) / spanE,
            Math.Max(height - 80, 1) / spanN), 1e-8, 1e8);
        OffsetX = width / 2 - (minE + maxE) / 2 * Scale;
        OffsetY = height / 2 + (minN + maxN) / 2 * Scale;
    }
}
