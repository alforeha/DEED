using System.Collections.ObjectModel;
using System.Text;
using Deed.Core;

namespace Deed.Persistence;

public static class PersistenceDiagnosticCodes
{
    public const string InvalidJson = "persistence.invalid_json";
    public const string InvalidFormat = "persistence.invalid_format";
    public const string UnsupportedSchemaVersion = "persistence.unsupported_schema_version";
    public const string InvalidValue = "persistence.invalid_value";
    public const string InvalidBearing = "persistence.invalid_bearing";
    public const string ValidationFailed = "persistence.validation_failed";
}

public sealed record ProjectEnvelope(string AppVersion, DateTimeOffset Created, DateTimeOffset Modified);

public sealed class PreservationMetadata
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _fields;

    internal PreservationMetadata(Dictionary<string, Dictionary<string, string>> fields)
    {
        _fields = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>(
            fields.ToDictionary(x => x.Key, x => (IReadOnlyDictionary<string, string>)
                new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(x.Value))));
    }

    public static PreservationMetadata Empty { get; } = new(new());
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> UnknownFields => _fields;
    internal IReadOnlyDictionary<string, string>? At(string path) => _fields.GetValueOrDefault(path);
}

public sealed record ProjectDocument(DeedProject Project, ProjectEnvelope Envelope,
    PreservationMetadata Preservation)
{
    internal IReadOnlyList<Diagnostic> SourceDiagnostics { get; init; } = Array.Empty<Diagnostic>();

    internal ProjectDocument(DeedProject project, ProjectEnvelope envelope,
        PreservationMetadata preservation, IEnumerable<Diagnostic> sourceDiagnostics)
        : this(project, envelope, preservation)
    {
        SourceDiagnostics = Array.AsReadOnly(sourceDiagnostics.ToArray());
    }

    public ProjectDocument(DeedProject project, ProjectEnvelope envelope)
        : this(project, envelope, PreservationMetadata.Empty) { }
}

public sealed class ProjectLoadResult
{
    public ProjectLoadResult(ProjectDocument? document, IEnumerable<Diagnostic> diagnostics)
    {
        Document = document;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }
    public ProjectDocument? Document { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
}

public sealed class ProjectSaveResult
{
    private readonly byte[]? _utf8Json;

    public ProjectSaveResult(byte[]? utf8Json, IEnumerable<Diagnostic> diagnostics)
    {
        _utf8Json = utf8Json is null ? null : (byte[])utf8Json.Clone();
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }
    public byte[]? Utf8Json => _utf8Json is null ? null : (byte[])_utf8Json.Clone();
    public string? Json => _utf8Json is null ? null : Encoding.UTF8.GetString(_utf8Json);
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool IsSuccess => _utf8Json is not null;
}
