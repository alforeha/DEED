namespace Deed.Core;

public readonly record struct Distance
{
    private Distance(double value) => Value = value;

    public double Value { get; }

    public static GeometryResult<Distance> TryCreate(double value)
    {
        if (!double.IsFinite(value))
        {
            return GeometryResult<Distance>.Failure(
                GeometryError.NonFiniteValue, "Distance must be finite.");
        }

        if (value < 0)
        {
            return GeometryResult<Distance>.Failure(
                GeometryError.NegativeDistance, "Distance cannot be negative.");
        }

        return GeometryResult<Distance>.Success(new Distance(value));
    }
}
