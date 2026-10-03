using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deed.Core;

namespace Deed.Persistence;

public static class CacheDiagnosticCodes
{
    public const string Invalid = "cache.invalid";
    public const string UnsupportedSchema = "cache.unsupported_schema";
    public const string SolverVersionMismatch = "cache.solver_version_mismatch";
    public const string Stale = "cache.stale";
    public const string CoordinateMismatch = "cache.coordinate_mismatch";
    public const string WriteFailed = "cache.write_failed";
}

public static class ProjectCachePath
{
    public static string ForProject(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var full = Path.GetFullPath(projectPath);
        return full.EndsWith(".deed.json", StringComparison.OrdinalIgnoreCase)
            ? full[..^".deed.json".Length] + ".deed.cache.json"
            : full + ".cache.json";
    }
}

public static class ProjectCache
{
    public const string SolverVersion = "0.1.0";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static IReadOnlyDictionary<RecordId, IReadOnlyDictionary<NodeId, Coordinate2D>> SolveFresh(ProjectDocument document)
    {
        var records = new Dictionary<RecordId, IReadOnlyDictionary<NodeId, Coordinate2D>>();
        foreach (var record in document.Project.Records.Keys)
            records[record] = RecordSolver.Solve(document.Project, record).NodeCoordinates;
        return records;
    }

    internal static IReadOnlyList<Diagnostic> Write(string projectPath, ProjectDocument document,
        IProjectFileSystem files, DateTimeOffset computedAt)
    {
        string path = ProjectCachePath.ForProject(projectPath);
        string temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var diagnostics = new List<Diagnostic>();
        try
        {
            var records = new JsonObject();
            foreach (var (recordId, nodes) in SolveFresh(document).OrderBy(x => x.Key.ToString(), StringComparer.Ordinal))
            {
                var nodeObject = new JsonObject();
                foreach (var (nodeId, coordinate) in nodes.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal))
                    nodeObject[nodeId.ToString()] = new JsonArray(coordinate.Easting, coordinate.Northing);
                records[recordId.ToString()] = new JsonObject { ["nodes"] = nodeObject };
            }
            var root = new JsonObject
            {
                ["format"] = "DEED-CACHE", ["schemaVersion"] = 1,
                ["solverVersion"] = SolverVersion,
                ["projectModified"] = Timestamp(document.Envelope.Modified),
                ["computedAt"] = Timestamp(computedAt), ["records"] = records
            };
            files.WriteNewAndFlush(temp, JsonSerializer.SerializeToUtf8Bytes(root, Options));
            if (files.FileExists(path)) files.Replace(temp, path, null);
            else files.Move(temp, path, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            diagnostics.Add(Warning(CacheDiagnosticCodes.WriteFailed, "Cache could not be written."));
        }
        finally
        {
            if (files.FileExists(temp))
                try { files.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { diagnostics.Add(Warning(CacheDiagnosticCodes.WriteFailed, "Temporary cache file could not be removed.")); }
        }
        return diagnostics;
    }

    internal static IReadOnlyList<Diagnostic> ReadAndCompare(string projectPath, ProjectDocument document,
        IProjectFileSystem files)
    {
        string path = ProjectCachePath.ForProject(projectPath);
        byte[] bytes;
        try { bytes = files.ReadAllBytes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return Array.Empty<Diagnostic>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new[] { Warning(CacheDiagnosticCodes.Invalid, "Cache could not be read.") }; }
        try
        {
            using var parsed = JsonDocument.Parse(bytes);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("format").GetString() != "DEED-CACHE")
                return new[] { Warning(CacheDiagnosticCodes.Invalid, "Cache format is invalid.") };
            if (root.GetProperty("schemaVersion").GetInt32() != 1)
                return new[] { Warning(CacheDiagnosticCodes.UnsupportedSchema, "Cache schema version is unsupported.") };
            if (root.GetProperty("solverVersion").GetString() != SolverVersion)
                return new[] { Warning(CacheDiagnosticCodes.SolverVersionMismatch, "Cache solver version differs.") };
            var modified = root.GetProperty("projectModified").GetDateTimeOffset();
            if (modified != document.Envelope.Modified)
                return new[] { Warning(CacheDiagnosticCodes.Stale, "Cache is stale.") };
            _ = root.GetProperty("computedAt").GetDateTimeOffset();
            var cachedRecords = root.GetProperty("records");
            if (cachedRecords.ValueKind != JsonValueKind.Object) throw new JsonException();
            var fresh = SolveFresh(document);
            var diagnostics = new List<Diagnostic>();
            var seenRecords = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in cachedRecords.EnumerateObject())
            {
                if (!seenRecords.Add(record.Name)) throw new JsonException();
                var recordId = RecordId.TryParse(record.Name);
                if (!recordId.IsSuccess || !fresh.TryGetValue(recordId.Value, out var nodes)) throw new JsonException();
                var cachedNodes = record.Value.GetProperty("nodes");
                if (cachedNodes.ValueKind != JsonValueKind.Object) throw new JsonException();
                var seenNodes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var node in cachedNodes.EnumerateObject())
                {
                    if (!seenNodes.Add(node.Name)) throw new JsonException();
                    var nodeId = NodeId.TryParse(node.Name);
                    if (!nodeId.IsSuccess || !nodes.TryGetValue(nodeId.Value, out var coordinate) ||
                        node.Value.ValueKind != JsonValueKind.Array || node.Value.GetArrayLength() != 2)
                        throw new JsonException();
                    double east = node.Value[0].GetDouble(), north = node.Value[1].GetDouble();
                    if (!double.IsFinite(east) || !double.IsFinite(north)) throw new JsonException();
                    if (east != coordinate.Easting || north != coordinate.Northing)
                        diagnostics.Add(Warning(CacheDiagnosticCodes.CoordinateMismatch,
                            "Cached coordinate differs from fresh solve.", "node", node.Name));
                }
                if (seenNodes.Count != nodes.Count ||
                    nodes.Keys.Any(id => !seenNodes.Contains(id.ToString()))) throw new JsonException();
            }
            if (seenRecords.Count != fresh.Count ||
                fresh.Keys.Any(id => !seenRecords.Contains(id.ToString()))) throw new JsonException();
            return diagnostics;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentOutOfRangeException)
        { return new[] { Warning(CacheDiagnosticCodes.Invalid, "Cache is invalid.") }; }
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static Diagnostic Warning(string code, string message, string? type = null, string? id = null) =>
        new(code, DiagnosticSeverity.Warning, message, type, id);
}
