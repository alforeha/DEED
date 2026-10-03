namespace Deed.Core;

public readonly record struct Azimuth
{
    private Azimuth(double degrees) => Degrees = degrees;

    public double Degrees { get; }

    public static GeometryResult<Azimuth> TryCreate(double degrees)
    {
        GeometryResult<double> normalized = Angle.NormalizeDegrees(degrees);
        return normalized.IsSuccess
            ? GeometryResult<Azimuth>.Success(new Azimuth(normalized.Value))
            : GeometryResult<Azimuth>.Failure(
                normalized.Diagnostic!.Value.Code,
                normalized.Diagnostic.Value.Message);
    }
}
