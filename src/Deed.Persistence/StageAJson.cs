using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deed.Core;

namespace Deed.Persistence;

public static class StageAJson
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static ProjectLoadResult Load(string? json) => Loader.Load(json);
    public static ProjectLoadResult Load(ReadOnlyMemory<byte> utf8Json) => Loader.Load(utf8Json);
    public static ProjectLoadResult Load(Stream? stream)
    {
        if (stream is null)
            return new(null, new[] { Error(PersistenceDiagnosticCodes.InvalidValue, "Stream is null.") });
        try
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return Load(buffer.ToArray());
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
            return new(null, new[] { Error(PersistenceDiagnosticCodes.InvalidValue, ex.Message) });
        }
    }

    public static ProjectSaveResult Save(DeedProject project, ProjectEnvelope envelope) =>
        Save(new ProjectDocument(project, envelope));

    public static ProjectSaveResult Save(ProjectDocument document)
    {
        List<Diagnostic> diagnostics;
        try
        {
            diagnostics = document.SourceDiagnostics.Concat(ProjectValidator.Validate(document.Project)).ToList();
            diagnostics.AddRange(ValidateStorageValues(document.Project));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException)
        {
            return new(null, new[] { Error(PersistenceDiagnosticCodes.InvalidValue, ex.Message) });
        }
        if (diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error))
        {
            diagnostics.Add(Error(PersistenceDiagnosticCodes.ValidationFailed,
                "Project has structural errors and cannot be serialized authoritatively."));
            return new(null, diagnostics);
        }
        if (string.IsNullOrWhiteSpace(document.Envelope.AppVersion))
            return new(null, new[] { Error(PersistenceDiagnosticCodes.InvalidValue, "App version is required.") });
        try
        {
            var p = document.Preservation;
            var project = document.Project;
            var root = new JsonObject
            {
                ["format"] = "DEED",
                ["schemaVersion"] = 1,
                ["appVersion"] = document.Envelope.AppVersion,
                ["created"] = Timestamp(document.Envelope.Created),
                ["modified"] = Timestamp(document.Envelope.Modified),
                ["settings"] = Settings(project.Settings, p),
                ["idCounters"] = Counters(project.IdCounters, p)
            };
            var types = new JsonObject();
            foreach (var (id, type) in project.DraftingTypes.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal))
            {
                var value = new JsonObject
                {
                    ["name"] = type.Name,
                    ["category"] = type.Category == DraftingCategory.Boundary ? "boundary" : "other"
                };
                Merge(value, p, $"$/draftingTypes/{id}");
                types[id.ToString()] = value;
            }
            root["draftingTypes"] = types;
            var records = new JsonObject();
            foreach (var (id, record) in project.Records.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal))
                records[id.ToString()] = Record(record, p, $"$/records/{id}");
            root["records"] = records;
            Merge(root, p, "$");
            return new(JsonSerializer.SerializeToUtf8Bytes(root, WriteOptions), diagnostics);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
        {
            return new(null, new[] { Error(PersistenceDiagnosticCodes.InvalidValue, ex.Message) });
        }
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static IEnumerable<Diagnostic> ValidateStorageValues(DeedProject project)
    {
        foreach (var (id, type) in project.DraftingTypes)
        {
            if (type.Name is null || !Enum.IsDefined(type.Category))
                yield return Error(PersistenceDiagnosticCodes.InvalidValue,
                    "Drafting type name or category is invalid.", "draftingType", id.ToString());
        }
        foreach (var record in project.Records.Values)
        {
            foreach (var (id, node) in record.Nodes)
            {
                if (node.Role is null || node.Definition is null)
                    yield return Error(PersistenceDiagnosticCodes.InvalidValue,
                        "Node role or definition is missing.", "node", id.ToString());
                if (node.Monument is { } monument &&
                    !Monument.TryCreate(monument.Description, monument.Evidence).IsSuccess)
                    yield return Error(PersistenceDiagnosticCodes.InvalidValue,
                        "Monument description or evidence is invalid.", "node", id.ToString());
            }
            foreach (var (id, course) in record.Courses)
                if (course.OriginalRecordedText is null || course.ParsedBearing is null)
                    yield return Error(PersistenceDiagnosticCodes.InvalidValue,
                        "Recorded call text and bearing are required.", "course", id.ToString());
            foreach (var (id, block) in record.DraftingBlocks)
                if (block.Name is null)
                    yield return Error(PersistenceDiagnosticCodes.InvalidValue,
                        "Block name is required.", "block", id.ToString());
        }
    }

    private static JsonObject Settings(ProjectSettings settings, PreservationMetadata p)
    {
        var tolerances = new JsonObject
        {
            ["coincidenceDistance"] = settings.CoincidenceDistance,
            ["linearElementDifference"] = settings.LinearElementDifference,
            ["angularElementDifferenceSeconds"] = settings.AngularElementDifferenceSeconds,
            ["minimumClosurePrecision"] = settings.MinimumClosurePrecision
        };
        Merge(tolerances, p, "$/settings/tolerances");
        var result = new JsonObject { ["tolerances"] = tolerances };
        Merge(result, p, "$/settings");
        return result;
    }

    private static JsonObject Counters(ProjectIdCounters counters, PreservationMetadata p)
    {
        var result = new JsonObject
        {
            ["rec"] = counters.Rec, ["blk"] = counters.Blk, ["c"] = counters.C,
            ["n"] = counters.N, ["dt"] = counters.Dt
        };
        Merge(result, p, "$/idCounters");
        return result;
    }

    private static JsonObject Record(DeedRecord record, PreservationMetadata p, string path)
    {
        var nodes = new JsonObject();
        foreach (var (id, node) in record.Nodes.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal))
        {
            if (node.Definition is AlongCourseNodeDefinition) continue;
            nodes[id.ToString()] = Node(node, p, $"{path}/nodes/{id}");
        }
        var courses = new JsonObject();
        foreach (var (id, course) in record.Courses.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal))
            courses[id.ToString()] = Course(course, record, p, $"{path}/courses/{id}");
        var blocks = new JsonObject();
        foreach (var (id, block) in record.DraftingBlocks.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal))
            blocks[id.ToString()] = Block(block, p, $"{path}/blocks/{id}");
        var result = new JsonObject { ["nodes"] = nodes, ["courses"] = courses, ["blocks"] = blocks };
        Merge(result, p, path);
        return result;
    }

    private static JsonObject Node(DeedNode node, PreservationMetadata p, string path)
    {
        JsonObject definition = node.Definition switch
        {
            FixedNodeDefinition fixedNode => new()
            {
                ["kind"] = "fixed",
                ["at"] = new JsonArray(fixedNode.Coordinate.Easting, fixedNode.Coordinate.Northing)
            },
            CourseEndNodeDefinition end => new()
            {
                ["kind"] = "courseEnd", ["course"] = end.ProducingCourseId.ToString()
            },
            _ => throw new InvalidOperationException("Unknown node definition.")
        };
        Merge(definition, p, $"{path}/definition");
        JsonObject? monument = null;
        if (node.Monument is { } value)
        {
            monument = new JsonObject
            {
                ["description"] = value.Description,
                ["evidence"] = value.Evidence
            };
            Merge(monument, p, $"{path}/monument");
        }
        var result = new JsonObject
        {
            ["definition"] = definition, ["role"] = node.Role,
            ["monument"] = monument
        };
        Merge(result, p, path);
        return result;
    }

    private static JsonObject Course(StraightCourse course, DeedRecord record,
        PreservationMetadata p, string path)
    {
        var recorded = new JsonObject
        {
            ["kind"] = "line", ["text"] = course.OriginalRecordedText,
            ["bearing"] = course.ParsedBearing?.OriginalText,
            ["length"] = course.RecordedDistance?.Value
        };
        Merge(recorded, p, $"{path}/recorded");
        JsonObject? drafted = null;
        if (course.DraftedCompletion is { } completion)
        {
            var length = new JsonObject
            {
                ["value"] = completion.Distance.Value, ["reason"] = completion.Reason
            };
            Merge(length, p, $"{path}/drafted/length");
            drafted = new JsonObject { ["length"] = length };
            Merge(drafted, p, $"{path}/drafted");
        }
        var result = new JsonObject
        {
            ["type"] = course.DraftingTypeId.ToString(),
            ["from"] = course.FromNodeId.ToString(),
            ["to"] = course.ToNodeId.ToString(),
            ["parent"] = course.ParentCourseId?.ToString(),
            ["along"] = new JsonArray(course.AlongPoints.Select(placement =>
            {
                var node = record.Nodes[placement.NodeId];
                JsonObject? monument = null;
                if (node.Monument is { } value)
                {
                    monument = new JsonObject
                    {
                        ["description"] = value.Description,
                        ["evidence"] = value.Evidence
                    };
                    Merge(monument, p, $"{path}/along/{placement.NodeId}/monument");
                }
                var item = new JsonObject
                {
                    ["id"] = placement.NodeId.ToString(),
                    ["fromPrevious"] = placement.FromPrevious.Value,
                    ["role"] = node.Role,
                    ["monument"] = monument
                };
                Merge(item, p, $"{path}/along/{placement.NodeId}");
                return (JsonNode?)item;
            }).ToArray()),
            ["finalPart"] = course.FinalPart?.Value,
            ["recorded"] = recorded, ["drafted"] = drafted, ["driving"] = "recorded"
        };
        Merge(result, p, path);
        return result;
    }

    private static JsonObject Block(DraftingBlock block, PreservationMetadata p, string path)
    {
        var result = new JsonObject
        {
            ["name"] = block.Name,
            ["type"] = block.DraftingTypeId.ToString(),
            ["parentBlock"] = block.ParentBlockId?.ToString(),
            ["viewOrder"] = block.ViewOrder,
            ["origin"] = block.Origin?.ToString(),
            ["reportClose"] = block.ReportClose,
            ["courses"] = new JsonArray(block.Courses.Select(x => (JsonNode?)JsonValue.Create(x.ToString())).ToArray())
        };
        Merge(result, p, path);
        return result;
    }

    private static void Merge(JsonObject target, PreservationMetadata preservation, string path)
    {
        if (preservation.At(path) is not { } fields) return;
        foreach (var (name, raw) in fields)
            if (!target.ContainsKey(name)) target[name] = JsonNode.Parse(raw);
    }

    internal static Diagnostic Error(string code, string message, string? type = null, string? id = null) =>
        new(code, DiagnosticSeverity.Error, message, type, id);
}
