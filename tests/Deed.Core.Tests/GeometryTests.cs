namespace Deed.Core.Tests;

public class GeometryTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(360, 0)]
    [InlineData(-360, 0)]
    [InlineData(720, 0)]
    [InlineData(-0.5, 359.5)]
    [InlineData(360.5, 0.5)]
    public void AzimuthNormalizesIntoFullCircle(double input, double expected)
    {
        GeometryResult<double> angle = Angle.NormalizeDegrees(input);
        GeometryResult<Azimuth> azimuth = Azimuth.TryCreate(input);

        Assert.True(angle.IsSuccess);
        Assert.True(azimuth.IsSuccess);
        Assert.Equal(expected, angle.Value);
        Assert.Equal(expected, azimuth.Value.Degrees);
    }

    [Theory]
    [InlineData("N45E", 1, 1)]
    [InlineData("S45E", 1, -1)]
    [InlineData("S45W", -1, -1)]
    [InlineData("N45W", -1, 1)]
    public void EndpointUsesEastingSineAndNorthingCosine(
        string bearingText, int eastingSign, int northingSign)
    {
        Bearing bearing = BearingParser.Parse(bearingText).Value.Bearing;
        Distance distance = Distance.TryCreate(100).Value;
        Coordinate2D start = new(1000, 2000);

        Coordinate2D endpoint = StraightLine.Endpoint(start, distance, bearing.Azimuth);
        Vector2D displacement = StraightLine.Displacement(distance, bearing.Azimuth);
        double component = 100 / Math.Sqrt(2);

        Assert.Equal(1000 + eastingSign * component, endpoint.Easting, 10);
        Assert.Equal(2000 + northingSign * component, endpoint.Northing, 10);
        Assert.Equal(eastingSign * component, displacement.Easting, 10);
        Assert.Equal(northingSign * component, displacement.Northing, 10);
    }

    [Fact]
    public void ZeroDistanceLeavesStartUnchanged()
    {
        Coordinate2D start = new(123, -456);
        Distance zero = Distance.TryCreate(0).Value;
        Azimuth azimuth = Azimuth.TryCreate(237).Value;

        Assert.Equal(start, StraightLine.Endpoint(start, zero, azimuth));
        Assert.Equal(new Vector2D(0, 0), StraightLine.Displacement(zero, azimuth));
    }

    [Fact]
    public void NegativeDistanceIsRejectedBeforeCourseGeometry()
    {
        GeometryResult<Distance> result = Distance.TryCreate(-0.01);

        Assert.False(result.IsSuccess);
        Assert.Equal(GeometryError.NegativeDistance, result.Diagnostic?.Code);
    }

    [Fact]
    public void NonFiniteInputsReturnDiagnostics()
    {
        Assert.Equal(GeometryError.NonFiniteValue, Distance.TryCreate(double.NaN).Diagnostic?.Code);
        Assert.Equal(GeometryError.NonFiniteValue, Azimuth.TryCreate(double.PositiveInfinity).Diagnostic?.Code);
    }

    [Fact]
    public void QuadrantFactoryValidatesDirectionsAndNegativeDegrees()
    {
        Assert.Equal(
            GeometryError.InvalidPrefix,
            Bearing.TryCreateQuadrant(CardinalDirection.East, 45, 0, 0, CardinalDirection.West).Diagnostic?.Code);
        Assert.Equal(
            GeometryError.InvalidSuffix,
            Bearing.TryCreateQuadrant(CardinalDirection.North, 45, 0, 0, CardinalDirection.South).Diagnostic?.Code);
        Assert.Equal(
            GeometryError.InvalidDegrees,
            Bearing.TryCreateQuadrant(CardinalDirection.North, -1, 0, 0, CardinalDirection.East).Diagnostic?.Code);
    }
}
