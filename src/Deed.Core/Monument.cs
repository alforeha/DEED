namespace Deed.Core;

public sealed record Monument
{
    private Monument(string description, string? evidence)
    {
        Description = description;
        Evidence = evidence;
    }

    public string Description { get; }
    public string? Evidence { get; }

    public static DomainResult<Monument> TryCreate(string? description, string? evidence = null)
    {
        if (string.IsNullOrWhiteSpace(description))
            return DomainResult<Monument>.Failure(DiagnosticCodes.InvalidMonument,
                "Monument description is required and cannot be blank.");
        if (evidence is not null && string.IsNullOrWhiteSpace(evidence))
            return DomainResult<Monument>.Failure(DiagnosticCodes.InvalidMonument,
                "Monument evidence cannot be blank when present.");
        return DomainResult<Monument>.Success(new(description, evidence));
    }
}
