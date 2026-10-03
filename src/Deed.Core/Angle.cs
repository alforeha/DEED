namespace Deed.Core;

public static class Angle
{
    public static GeometryResult<double> NormalizeDegrees(double degrees)
    {
        if (!double.IsFinite(degrees))
        {
            return GeometryResult<double>.Failure(
                GeometryError.NonFiniteValue, "Angle must be finite.");
        }

        double normalized = degrees % 360.0;
        if (normalized < 0)
        {
            normalized += 360.0;
        }

        return GeometryResult<double>.Success(normalized is 0.0 or 360.0 ? 0.0 : normalized);
    }
}
