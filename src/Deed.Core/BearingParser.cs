using System.Globalization;
using System.Text.RegularExpressions;

namespace Deed.Core;

public static partial class BearingParser
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"^\s*(?<prefix>North|South|N|S)\s*(?<angle>.*?)\s*(?<suffix>East|West|E|W)\s*$", Options)]
    private static partial Regex QuadrantPattern();

    [GeneratedRegex(@"^\d+(?:\.\d+)?$")]
    private static partial Regex DecimalPattern();

    [GeneratedRegex(@"^(?<d>\d+)-(?<m>\d+)-(?<s>\d+)$")]
    private static partial Regex HyphenPattern();

    [GeneratedRegex(@"^(?<d>\d+)\s+(?<m>\d+)\s+(?<s>\d+)$")]
    private static partial Regex SpacePattern();

    [GeneratedRegex("^(?<d>\\d+)°\\s*(?<m>\\d+)'\\s*(?<s>\\d+)\"$")]
    private static partial Regex SymbolPattern();

    [GeneratedRegex(@"^(?<d>\d+)\s+degrees?\s+(?<m>\d+)\s+minutes?\s+(?<s>\d+)\s+seconds?$", Options)]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"[A-Za-z]+\s*$", Options)]
    private static partial Regex TrailingWordPattern();

    public static GeometryResult<ParsedBearing> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return GeometryResult<ParsedBearing>.Failure(
                GeometryError.EmptyInput, "Bearing text is empty.");
        }

        string trimmed = text.Trim();
        CardinalDirection? cardinal = trimmed.ToUpperInvariant() switch
        {
            "N" => CardinalDirection.North,
            "E" => CardinalDirection.East,
            "S" => CardinalDirection.South,
            "W" => CardinalDirection.West,
            _ => null
        };

        if (cardinal is not null)
        {
            return GeometryResult<ParsedBearing>.Success(
                new ParsedBearing(text, Bearing.TryCreateCardinal(cardinal.Value).Value));
        }

        Match match = QuadrantPattern().Match(trimmed);
        if (!match.Success)
        {
            return GeometryResult<ParsedBearing>.Failure(
                TrailingWordPattern().IsMatch(trimmed)
                    ? GeometryError.InvalidSuffix
                    : GeometryError.InvalidBearingFormat,
                "Expected a quadrant bearing ending in east or west.");
        }

        CardinalDirection reference = match.Groups["prefix"].Value.StartsWith("N", StringComparison.OrdinalIgnoreCase)
            ? CardinalDirection.North
            : CardinalDirection.South;
        CardinalDirection toward = match.Groups["suffix"].Value.StartsWith("E", StringComparison.OrdinalIgnoreCase)
            ? CardinalDirection.East
            : CardinalDirection.West;
        string angleText = match.Groups["angle"].Value.Trim();

        double degrees;
        int minutes = 0;
        int seconds = 0;

        if (DecimalPattern().IsMatch(angleText))
        {
            if (!double.TryParse(angleText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out degrees))
            {
                return GeometryResult<ParsedBearing>.Failure(
                    GeometryError.InvalidDegrees, "Degrees are not a finite decimal number.");
            }
        }
        else
        {
            Match angleMatch = HyphenPattern().Match(angleText);
            if (!angleMatch.Success) angleMatch = SpacePattern().Match(angleText);
            if (!angleMatch.Success) angleMatch = SymbolPattern().Match(angleText);
            if (!angleMatch.Success) angleMatch = WordPattern().Match(angleText);
            if (!angleMatch.Success)
            {
                return GeometryResult<ParsedBearing>.Failure(
                    GeometryError.InvalidBearingFormat, "Expected decimal degrees or degrees, minutes and seconds.");
            }

            if (!double.TryParse(angleMatch.Groups["d"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out degrees))
            {
                return GeometryResult<ParsedBearing>.Failure(
                    GeometryError.InvalidDegrees, "Degrees are not a finite number.");
            }

            if (!int.TryParse(angleMatch.Groups["m"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out minutes))
            {
                return GeometryResult<ParsedBearing>.Failure(
                    GeometryError.InvalidMinutes, "Minutes are not a valid integer.");
            }

            if (!int.TryParse(angleMatch.Groups["s"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out seconds))
            {
                return GeometryResult<ParsedBearing>.Failure(
                    GeometryError.InvalidSeconds, "Seconds are not a valid integer.");
            }
        }

        GeometryResult<Bearing> bearing = Bearing.TryCreateQuadrant(reference, degrees, minutes, seconds, toward);
        return bearing.IsSuccess
            ? GeometryResult<ParsedBearing>.Success(new ParsedBearing(text, bearing.Value))
            : GeometryResult<ParsedBearing>.Failure(
                bearing.Diagnostic!.Value.Code,
                bearing.Diagnostic.Value.Message);
    }
}
