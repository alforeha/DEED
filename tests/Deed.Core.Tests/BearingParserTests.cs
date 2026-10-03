using System.Globalization;

namespace Deed.Core.Tests;

public class BearingParserTests
{
    [Theory]
    [InlineData("N45-30-00E")]
    [InlineData("N 45 30 00 E")]
    [InlineData("N45°30'00\"E")]
    [InlineData("North 45 degrees 30 minutes 00 seconds East")]
    [InlineData("N45.5E")]
    [InlineData("n45-30-00e")]
    public void AcceptedFormsPreserveOriginalText(string input)
    {
        GeometryResult<ParsedBearing> result = BearingParser.Parse(input);

        Assert.True(result.IsSuccess, result.Diagnostic?.Message);
        Assert.Equal(input, result.Value.OriginalText);
        Assert.Equal(45.5, result.Value.Bearing.Azimuth.Degrees, 10);
    }

    [Theory]
    [InlineData("N45E", 45)]
    [InlineData("S45E", 135)]
    [InlineData("S45W", 225)]
    [InlineData("N45W", 315)]
    public void QuadrantsMapToClockwiseAzimuth(string input, double expected)
    {
        GeometryResult<ParsedBearing> result = BearingParser.Parse(input);

        Assert.True(result.IsSuccess, result.Diagnostic?.Message);
        Assert.Null(result.Value.Bearing.Cardinal);
        Assert.Equal(45, result.Value.Bearing.QuadrantAngleDegrees);
        Assert.Equal(expected, result.Value.Bearing.Azimuth.Degrees, 10);
    }

    [Theory]
    [InlineData("N", CardinalDirection.North, 0)]
    [InlineData("E", CardinalDirection.East, 90)]
    [InlineData("S", CardinalDirection.South, 180)]
    [InlineData("W", CardinalDirection.West, 270)]
    public void CardinalsMapToAzimuth(string input, CardinalDirection direction, double expected)
    {
        GeometryResult<ParsedBearing> result = BearingParser.Parse(input.ToLowerInvariant());

        Assert.True(result.IsSuccess, result.Diagnostic?.Message);
        Assert.Equal(direction, result.Value.Bearing.Cardinal);
        Assert.Equal(expected, result.Value.Bearing.Azimuth.Degrees);
    }

    [Theory]
    [InlineData("N91E", GeometryError.InvalidDegrees)]
    [InlineData("N90-00-01E", GeometryError.InvalidDegrees)]
    [InlineData("N45-60-00E", GeometryError.InvalidMinutes)]
    [InlineData("N45-00-60E", GeometryError.InvalidSeconds)]
    [InlineData("N45Q", GeometryError.InvalidSuffix)]
    [InlineData("N45.5-30-00E", GeometryError.InvalidBearingFormat)]
    [InlineData("N45,5E", GeometryError.InvalidBearingFormat)]
    [InlineData("", GeometryError.EmptyInput)]
    public void InvalidInputsReturnDiagnostics(string input, GeometryError expected)
    {
        GeometryResult<ParsedBearing> result = BearingParser.Parse(input);

        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Diagnostic?.Code);
        Assert.False(string.IsNullOrWhiteSpace(result.Diagnostic?.Message));
    }

    [Fact]
    public void NullInputReturnsDiagnostic()
    {
        GeometryResult<ParsedBearing> result = BearingParser.Parse(null);

        Assert.False(result.IsSuccess);
        Assert.Equal(GeometryError.EmptyInput, result.Diagnostic?.Code);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    public void ParsingUsesInvariantDecimalSyntax(string cultureName)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);

            GeometryResult<ParsedBearing> accepted = BearingParser.Parse("N45.5E");
            GeometryResult<ParsedBearing> rejected = BearingParser.Parse("N45,5E");

            Assert.True(accepted.IsSuccess, accepted.Diagnostic?.Message);
            Assert.Equal(45.5, accepted.Value.Bearing.Azimuth.Degrees);
            Assert.False(rejected.IsSuccess);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void TranscriptionRetainsWhitespaceAndCase()
    {
        const string input = "  n 45 30 00 e  ";
        GeometryResult<ParsedBearing> result = BearingParser.Parse(input);

        Assert.True(result.IsSuccess, result.Diagnostic?.Message);
        Assert.Equal(input, result.Value.OriginalText);
    }
}
