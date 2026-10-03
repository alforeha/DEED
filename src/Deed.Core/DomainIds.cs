namespace Deed.Core;

internal static class DomainId
{
    internal static DomainResult<string> Parse(string? value, string prefix)
    {
        if (value is not null && value.Length == prefix.Length + 5 &&
            value.StartsWith(prefix, StringComparison.Ordinal) &&
            value.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0 &&
            !value.AsSpan(prefix.Length).SequenceEqual("00000".AsSpan()))
            return DomainResult<string>.Success(value);
        return DomainResult<string>.Failure(DiagnosticCodes.InvalidId,
            $"Expected {prefix} followed by an ASCII decimal suffix from 00001 through 99999.");
    }
}

public readonly record struct RecordId
{
    private RecordId(string value) => Value = value;
    public string Value { get; }
    public static DomainResult<RecordId> TryParse(string? value) => Convert(DomainId.Parse(value, "rec-"));
    private static DomainResult<RecordId> Convert(DomainResult<string> result) => result.IsSuccess
        ? DomainResult<RecordId>.Success(new(result.Value))
        : DomainResult<RecordId>.Failure(result.Diagnostic!.Code, result.Diagnostic.Message);
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct BlockId
{
    private BlockId(string value) => Value = value;
    public string Value { get; }
    public static DomainResult<BlockId> TryParse(string? value) => Convert(DomainId.Parse(value, "blk-"));
    private static DomainResult<BlockId> Convert(DomainResult<string> result) => result.IsSuccess
        ? DomainResult<BlockId>.Success(new(result.Value))
        : DomainResult<BlockId>.Failure(result.Diagnostic!.Code, result.Diagnostic.Message);
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct CourseId
{
    private CourseId(string value) => Value = value;
    public string Value { get; }
    public static DomainResult<CourseId> TryParse(string? value) => Convert(DomainId.Parse(value, "c-"));
    private static DomainResult<CourseId> Convert(DomainResult<string> result) => result.IsSuccess
        ? DomainResult<CourseId>.Success(new(result.Value))
        : DomainResult<CourseId>.Failure(result.Diagnostic!.Code, result.Diagnostic.Message);
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct NodeId
{
    private NodeId(string value) => Value = value;
    public string Value { get; }
    public static DomainResult<NodeId> TryParse(string? value) => Convert(DomainId.Parse(value, "n-"));
    private static DomainResult<NodeId> Convert(DomainResult<string> result) => result.IsSuccess
        ? DomainResult<NodeId>.Success(new(result.Value))
        : DomainResult<NodeId>.Failure(result.Diagnostic!.Code, result.Diagnostic.Message);
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct DraftingTypeId
{
    private DraftingTypeId(string value) => Value = value;
    public string Value { get; }
    public static DomainResult<DraftingTypeId> TryParse(string? value) => Convert(DomainId.Parse(value, "dt-"));
    private static DomainResult<DraftingTypeId> Convert(DomainResult<string> result) => result.IsSuccess
        ? DomainResult<DraftingTypeId>.Success(new(result.Value))
        : DomainResult<DraftingTypeId>.Failure(result.Diagnostic!.Code, result.Diagnostic.Message);
    public override string ToString() => Value ?? string.Empty;
}
