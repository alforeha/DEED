namespace Deed.Core;

public sealed record ProjectSettings
{
    private ProjectSettings(double coincidenceDistance, double linearElementDifference,
        double angularElementDifferenceSeconds, double minimumClosurePrecision)
    {
        CoincidenceDistance = coincidenceDistance;
        LinearElementDifference = linearElementDifference;
        AngularElementDifferenceSeconds = angularElementDifferenceSeconds;
        MinimumClosurePrecision = minimumClosurePrecision;
    }

    public double CoincidenceDistance { get; }
    public double LinearElementDifference { get; }
    public double AngularElementDifferenceSeconds { get; }
    public double MinimumClosurePrecision { get; }

    public static ProjectSettings Default { get; } = new(0.01, 0.01, 1, 5000);

    public static DomainResult<ProjectSettings> TryCreate(double coincidenceDistance,
        double linearElementDifference, double angularElementDifferenceSeconds,
        double minimumClosurePrecision)
    {
        if (!Valid(coincidenceDistance) || !Valid(linearElementDifference) ||
            !Valid(angularElementDifferenceSeconds) || !Valid(minimumClosurePrecision))
            return DomainResult<ProjectSettings>.Failure(DiagnosticCodes.InvalidSettings,
                "All project tolerances and closure precision must be finite and greater than zero.");
        return DomainResult<ProjectSettings>.Success(new(coincidenceDistance, linearElementDifference,
            angularElementDifferenceSeconds, minimumClosurePrecision));
    }

    private static bool Valid(double value) => double.IsFinite(value) && value > 0;
}
