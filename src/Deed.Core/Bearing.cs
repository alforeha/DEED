namespace Deed.Core;

public enum CardinalDirection
{
    North,
    East,
    South,
    West
}

public sealed class Bearing
{
    private Bearing(
        Azimuth azimuth,
        CardinalDirection? cardinal,
        CardinalDirection? reference,
        CardinalDirection? toward,
        double quadrantAngleDegrees)
    {
        Azimuth = azimuth;
        Cardinal = cardinal;
        Reference = reference;
        Toward = toward;
        QuadrantAngleDegrees = quadrantAngleDegrees;
    }

    public Azimuth Azimuth { get; }

    public CardinalDirection? Cardinal { get; }

    public CardinalDirection? Reference { get; }

    public CardinalDirection? Toward { get; }

    public double QuadrantAngleDegrees { get; }

    public static GeometryResult<Bearing> TryCreateCardinal(CardinalDirection direction)
    {
        double degrees = direction switch
        {
            CardinalDirection.North => 0,
            CardinalDirection.East => 90,
            CardinalDirection.South => 180,
            CardinalDirection.West => 270,
            _ => double.NaN
        };

        if (!double.IsFinite(degrees))
        {
            return GeometryResult<Bearing>.Failure(
                GeometryError.InvalidBearingFormat, "Unknown cardinal direction.");
        }

        return GeometryResult<Bearing>.Success(
            new Bearing(Azimuth.TryCreate(degrees).Value, direction, null, null, 0));
    }

    public static GeometryResult<Bearing> TryCreateQuadrant(
        CardinalDirection reference,
        double degrees,
        int minutes,
        int seconds,
        CardinalDirection toward)
    {
        if (reference is not (CardinalDirection.North or CardinalDirection.South))
        {
            return GeometryResult<Bearing>.Failure(
                GeometryError.InvalidPrefix, "Quadrant bearings start at north or south.");
        }

        if (toward is not (CardinalDirection.East or CardinalDirection.West))
        {
            return GeometryResult<Bearing>.Failure(
                GeometryError.InvalidSuffix, "Quadrant bearings turn east or west.");
        }

        if (!double.IsFinite(degrees))
        {
            return GeometryResult<Bearing>.Failure(
                GeometryError.NonFiniteValue, "Bearing degrees must be finite.");
        }

        if (minutes is < 0 or >= 60)
        {
            return GeometryResult<Bearing>.Failure(
                GeometryError.InvalidMinutes, "Minutes must be between 0 and less than 60.");
        }

        if (seconds is < 0 or >= 60)
        {
            return GeometryResult<Bearing>.Failure(
                GeometryError.InvalidSeconds, "Seconds must be between 0 and less than 60.");
        }

        if (degrees < 0 || degrees > 90 ||
            (degrees == 90 && (minutes != 0 || seconds != 0)) ||
            (degrees % 1 != 0 && (minutes != 0 || seconds != 0)))
        {
            return GeometryResult<Bearing>.Failure(
                GeometryError.InvalidDegrees, "Quadrant degrees must be between 0 and 90; 90 cannot include minutes or seconds.");
        }

        double angle = degrees + minutes / 60.0 + seconds / 3600.0;
        double azimuthDegrees = (reference, toward) switch
        {
            (CardinalDirection.North, CardinalDirection.East) => angle,
            (CardinalDirection.North, CardinalDirection.West) => 360 - angle,
            (CardinalDirection.South, CardinalDirection.East) => 180 - angle,
            (CardinalDirection.South, CardinalDirection.West) => 180 + angle,
            _ => throw new InvalidOperationException("Validated quadrant directions were lost.")
        };

        return GeometryResult<Bearing>.Success(new Bearing(
            Azimuth.TryCreate(azimuthDegrees).Value,
            null,
            reference,
            toward,
            angle));
    }
}
