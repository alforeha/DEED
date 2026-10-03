namespace Deed.Core;

public sealed class ParsedBearing
{
    internal ParsedBearing(string originalText, Bearing bearing)
    {
        OriginalText = originalText;
        Bearing = bearing;
    }

    public string OriginalText { get; }

    public Bearing Bearing { get; }
}
