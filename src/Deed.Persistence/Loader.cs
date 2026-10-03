using System.Globalization;
using System.Text.Json;
using Deed.Core;

namespace Deed.Persistence;

internal sealed class Loader
{
    private readonly List<Diagnostic> _diagnostics = new();
    private readonly Dictionary<string, Dictionary<string, string>> _preserved = new(StringComparer.Ordinal);

    public static ProjectLoadResult Load(string? json)
    {
        if (json is null)
            return new(null, new[] { StageAJson.Error(PersistenceDiagnosticCodes.InvalidJson, "JSON is null.") });
        try
        {
            using var parsed = JsonDocument.Parse(json);
            return new Loader().Read(parsed.RootElement);
        }
        catch (JsonException ex)
        {
            return new(null, new[] { StageAJson.Error(PersistenceDiagnosticCodes.InvalidJson, ex.Message) });
        }
    }

    public static ProjectLoadResult Load(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var parsed = JsonDocument.Parse(json);
            return new Loader().Read(parsed.RootElement);
        }
        catch (JsonException ex)
        {
            return new(null, new[] { StageAJson.Error(PersistenceDiagnosticCodes.InvalidJson, ex.Message) });
        }
    }

    private ProjectLoadResult Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return new(null, new[] { StageAJson.Error(PersistenceDiagnosticCodes.InvalidValue, "Project must be an object.") });
        string? format = String(root, "format", "$", true);
        if (format != "DEED")
            return new(null, new[] { StageAJson.Error(PersistenceDiagnosticCodes.InvalidFormat, "Format must be DEED.") });
        int? version = Int(root, "schemaVersion", "$", true);
        if (version != 1)
            return new(null, new[] { StageAJson.Error(PersistenceDiagnosticCodes.UnsupportedSchemaVersion,
                "Only schema version 1 is supported.") });
        string? appVersion = String(root, "appVersion", "$", true);
        if (appVersion is not null && string.IsNullOrWhiteSpace(appVersion))
        {
            Invalid("$/appVersion", "App version cannot be blank.");
            appVersion = null;
        }
        DateTimeOffset? created = Time(root, "created");
        DateTimeOffset? modified = Time(root, "modified");
        var settingsObject = Object(root, "settings", "$", true);
        var counterObject = Object(root, "idCounters", "$", true);
        var typesObject = Object(root, "draftingTypes", "$", true);
        var recordsObject = Object(root, "records", "$", true);
        Capture(root, "$", "format", "schemaVersion", "appVersion", "created", "modified",
            "settings", "idCounters", "draftingTypes", "records");
        if (appVersion is null || created is null || modified is null ||
            settingsObject is null || counterObject is null || typesObject is null || recordsObject is null)
            return Result(null);
        var settings = Settings(settingsObject.Value);
        var counters = Counters(counterObject.Value);
        if (settings is null || counters is null) return Result(null);
        var types = Types(typesObject.Value);
        var records = Records(recordsObject.Value);
        var project = new DeedProject(settings, types, records, counters);
        var validation = ProjectValidator.Validate(project);
        _diagnostics.AddRange(validation);
        if (validation.Any(x => x.Severity == DiagnosticSeverity.Error))
            Add(PersistenceDiagnosticCodes.ValidationFailed, "Loaded project has structural errors.");
        return Result(new ProjectDocument(project,
            new ProjectEnvelope(appVersion, created.Value, modified.Value),
            new PreservationMetadata(_preserved), _diagnostics));
    }

    private ProjectLoadResult Result(ProjectDocument? document) => new(document, _diagnostics);

    private ProjectSettings? Settings(JsonElement value)
    {
        Capture(value, "$/settings", "tolerances");
        var tolerances = Object(value, "tolerances", "$/settings", true);
        if (tolerances is null) return null;
        Capture(tolerances.Value, "$/settings/tolerances", "coincidenceDistance",
            "linearElementDifference", "angularElementDifferenceSeconds", "minimumClosurePrecision");
        double? a = Number(tolerances.Value, "coincidenceDistance", "$/settings/tolerances", true);
        double? b = Number(tolerances.Value, "linearElementDifference", "$/settings/tolerances", true);
        double? c = Number(tolerances.Value, "angularElementDifferenceSeconds", "$/settings/tolerances", true);
        double? d = Number(tolerances.Value, "minimumClosurePrecision", "$/settings/tolerances", true);
        if (a is null || b is null || c is null || d is null) return null;
        var result = ProjectSettings.TryCreate(a.Value, b.Value, c.Value, d.Value);
        if (!result.IsSuccess)
        {
            Add(PersistenceDiagnosticCodes.InvalidValue, result.Diagnostic!.Message, "settings", "tolerances");
            return null;
        }
        return result.Value;
    }

    private ProjectIdCounters? Counters(JsonElement value)
    {
        Capture(value, "$/idCounters", "rec", "blk", "c", "n", "dt");
        int? rec = Int(value, "rec", "$/idCounters", true);
        int? blk = Int(value, "blk", "$/idCounters", true);
        int? c = Int(value, "c", "$/idCounters", true);
        int? n = Int(value, "n", "$/idCounters", true);
        int? dt = Int(value, "dt", "$/idCounters", true);
        return rec is null || blk is null || c is null || n is null || dt is null
            ? null : new(rec.Value, blk.Value, c.Value, n.Value, dt.Value);
    }

    private Dictionary<DraftingTypeId, DraftingType> Types(JsonElement value)
    {
        var types = new Dictionary<DraftingTypeId, DraftingType>();
        foreach (var property in value.EnumerateObject())
        {
            var id = ParseId(property.Name, DraftingTypeId.TryParse, "draftingType");
            if (id is null) continue;
            string path = $"$/draftingTypes/{property.Name}";
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                Invalid(path, "Drafting type must be an object.");
                continue;
            }
            Capture(property.Value, path, "name", "category");
            string? name = String(property.Value, "name", path, true);
            string? category = String(property.Value, "category", path, true);
            if (name is null || category is null) continue;
            DraftingCategory kind;
            if (category == "boundary") kind = DraftingCategory.Boundary;
            else if (category == "other") kind = DraftingCategory.Other;
            else { Invalid(path, "Drafting category must be boundary or other."); continue; }
            if (!types.TryAdd(id.Value, new(id.Value, name, kind)))
                Add(DiagnosticCodes.DuplicateProjectId, "Duplicate drafting type ID.", "draftingType", property.Name);
        }
        return types;
    }

    private Dictionary<RecordId, DeedRecord> Records(JsonElement value)
    {
        var records = new Dictionary<RecordId, DeedRecord>();
        foreach (var property in value.EnumerateObject())
        {
            var id = ParseId(property.Name, RecordId.TryParse, "record");
            if (id is null) continue;
            string path = $"$/records/{property.Name}";
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                Invalid(path, "Record must be an object.");
                continue;
            }
            Capture(property.Value, path, "nodes", "courses", "blocks");
            var nodesValue = Object(property.Value, "nodes", path, true);
            var coursesValue = Object(property.Value, "courses", path, true);
            var blocksValue = Object(property.Value, "blocks", path, true);
            if (nodesValue is null || coursesValue is null || blocksValue is null) continue;
            var nodes = Nodes(nodesValue.Value, path);
            var courses = Courses(coursesValue.Value, path, nodes);
            var record = new DeedRecord(id.Value, nodes, courses, Blocks(blocksValue.Value, path));
            if (!records.TryAdd(id.Value, record))
                Add(DiagnosticCodes.DuplicateProjectId, "Duplicate record ID.", "record", property.Name);
        }
        return records;
    }

    private Dictionary<NodeId, DeedNode> Nodes(JsonElement value, string recordPath)
    {
        var nodes = new Dictionary<NodeId, DeedNode>();
        foreach (var property in value.EnumerateObject())
        {
            var id = ParseId(property.Name, NodeId.TryParse, "node");
            if (id is null) continue;
            string path = $"{recordPath}/nodes/{property.Name}";
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                Invalid(path, "Node must be an object.");
                continue;
            }
            Capture(property.Value, path, "definition", "role", "monument");
            string? role = String(property.Value, "role", path, true);
            bool validMonument = TryReadMonument(property.Value, path, out Monument? monument);
            var definition = Object(property.Value, "definition", path, true);
            if (role is null || definition is null) continue;
            string defPath = $"{path}/definition";
            string? kind = String(definition.Value, "kind", defPath, true);
            NodeDefinition? parsed = null;
            if (kind == "fixed")
            {
                Capture(definition.Value, defPath, "kind", "at");
                if (definition.Value.TryGetProperty("at", out var at) &&
                    at.ValueKind == JsonValueKind.Array && at.GetArrayLength() == 2 &&
                    at[0].ValueKind == JsonValueKind.Number &&
                    at[1].ValueKind == JsonValueKind.Number &&
                    at[0].TryGetDouble(out double x) && at[1].TryGetDouble(out double y) &&
                    double.IsFinite(x) && double.IsFinite(y))
                    parsed = new FixedNodeDefinition(new(x, y));
                else Invalid(defPath, "Fixed node at must contain two finite numbers.");
            }
            else if (kind == "courseEnd")
            {
                Capture(definition.Value, defPath, "kind", "course");
                var course = Id(definition.Value, "course", CourseId.TryParse, defPath);
                if (course is not null) parsed = new CourseEndNodeDefinition(course.Value);
            }
            else Invalid(defPath, "Unknown node definition kind.");
            if (parsed is not null && role is not null && validMonument &&
                !nodes.TryAdd(id.Value, new(id.Value, role, monument, parsed)))
                Add(DiagnosticCodes.DuplicateProjectId, "Duplicate node ID.", "node", property.Name);
        }
        return nodes;
    }

    private bool TryReadMonument(JsonElement node, string nodePath, out Monument? monument)
    {
        monument = null;
        string path = $"{nodePath}/monument";
        if (!node.TryGetProperty("monument", out var value))
        {
            Invalid(path, "Required nullable monument is missing.");
            return false;
        }
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Object)
        {
            Invalid(path, "Monument must be an object or null.");
            return false;
        }
        Capture(value, path, "description", "evidence");
        string? description = String(value, "description", path, true);
        string? evidence = String(value, "evidence", path, false);
        if (description is null) return false;
        if (value.TryGetProperty("evidence", out var evidenceValue) &&
            evidenceValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            return false;
        var result = Monument.TryCreate(description, evidence);
        if (!result.IsSuccess)
        {
            Invalid(path, result.Diagnostic!.Message);
            return false;
        }
        monument = result.Value;
        return true;
    }

    private Dictionary<CourseId, StraightCourse> Courses(JsonElement value, string recordPath,
        Dictionary<NodeId, DeedNode> nodes)
    {
        var courses = new Dictionary<CourseId, StraightCourse>();
        foreach (var property in value.EnumerateObject())
        {
            var id = ParseId(property.Name, CourseId.TryParse, "course");
            if (id is null) continue;
            string path = $"{recordPath}/courses/{property.Name}";
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                Invalid(path, "Course must be an object.");
                continue;
            }
            Capture(property.Value, path, "type", "from", "to", "parent", "along", "finalPart",
                "recorded", "drafted", "driving");
            var type = Id(property.Value, "type", DraftingTypeId.TryParse, path);
            var from = Id(property.Value, "from", NodeId.TryParse, path);
            var to = Id(property.Value, "to", NodeId.TryParse, path);
            bool validParent = property.Value.TryGetProperty("parent", out var parentValue);
            if (!validParent)
                Invalid($"{path}/parent", "Required nullable parent is missing.");
            var parent = Id(property.Value, "parent", CourseId.TryParse, path, false);
            if (validParent && parentValue.ValueKind != JsonValueKind.Null && parent is null)
                validParent = false;
            var alongPoints = new List<AlongPointPlacement>();
            var alongNodes = new List<DeedNode>();
            bool validAlong = true;
            if (!property.Value.TryGetProperty("along", out var alongValue) ||
                alongValue.ValueKind != JsonValueKind.Array)
            {
                Invalid($"{path}/along", "Along points must be an array.");
                validAlong = false;
            }
            else
            {
                int index = 0;
                foreach (var item in alongValue.EnumerateArray())
                {
                    string itemPath = $"{path}/along/{index++}";
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        Invalid(itemPath, "Along placement must be an object.");
                        validAlong = false;
                        continue;
                    }
                    var node = Id(item, "id", NodeId.TryParse, itemPath);
                    string metadataPath = node is { } nodeId
                        ? $"{path}/along/{nodeId}" : itemPath;
                    Capture(item, metadataPath, "id", "fromPrevious", "role", "monument");
                    var distance = Distance(item, "fromPrevious", itemPath, false);
                    string? role = String(item, "role", itemPath, true);
                    bool validMonument = TryReadMonument(item, metadataPath, out Monument? monument);
                    if (node is null || distance is null || role is null || !validMonument)
                        validAlong = false;
                    else
                    {
                        alongPoints.Add(new AlongPointPlacement(node.Value, distance.Value));
                        alongNodes.Add(new DeedNode(node.Value, role, monument,
                            new AlongCourseNodeDefinition(id.Value)));
                    }
                }
            }
            bool validFinalPart = property.Value.TryGetProperty("finalPart", out var finalValue);
            if (!validFinalPart)
                Invalid($"{path}/finalPart", "Required nullable final part is missing.");
            Distance? finalPart = null;
            if (validFinalPart && finalValue.ValueKind != JsonValueKind.Null)
            {
                finalPart = Distance(property.Value, "finalPart", path, false);
                if (finalPart is null) validFinalPart = false;
            }
            string? driving = String(property.Value, "driving", path, true);
            if (driving != "recorded") Invalid(path, "Driving realization must be recorded.");
            var recorded = Object(property.Value, "recorded", path, true);
            if (recorded is null) continue;
            string recPath = $"{path}/recorded";
            Capture(recorded.Value, recPath, "kind", "text", "bearing", "length");
            string? kind = String(recorded.Value, "kind", recPath, true);
            if (kind != "line") Invalid(recPath, "Recorded kind must be line.");
            string? text = String(recorded.Value, "text", recPath, true);
            string? bearingText = String(recorded.Value, "bearing", recPath, true);
            ParsedBearing? bearing = null;
            if (bearingText is not null)
            {
                var parsed = BearingParser.Parse(bearingText);
                if (parsed.IsSuccess) bearing = parsed.Value;
                else Add(PersistenceDiagnosticCodes.InvalidBearing,
                    parsed.Diagnostic!.Value.Message, "course", property.Name);
            }
            Distance? length = Distance(recorded.Value, "length", recPath, true);
            DraftedDistanceCompletion? drafted = null;
            if (!property.Value.TryGetProperty("drafted", out _))
                Invalid($"{path}/drafted", "Required nullable drafted value is missing.");
            if (property.Value.TryGetProperty("drafted", out var draftedValue) &&
                draftedValue.ValueKind != JsonValueKind.Null)
            {
                if (draftedValue.ValueKind != JsonValueKind.Object) Invalid(path, "Drafted must be an object or null.");
                else
                {
                    Capture(draftedValue, $"{path}/drafted", "length");
                    var lengthObject = Object(draftedValue, "length", $"{path}/drafted", true);
                    if (lengthObject is not null)
                    {
                        string lengthPath = $"{path}/drafted/length";
                        Capture(lengthObject.Value, lengthPath, "value", "reason");
                        Distance? draftedDistance = Distance(lengthObject.Value, "value", lengthPath, false);
                        string? reason = String(lengthObject.Value, "reason", lengthPath, true);
                        if (draftedDistance is not null && reason is not null) drafted = new(draftedDistance.Value, reason);
                    }
                }
            }
            if (type is null || from is null || to is null || !validParent || !validAlong ||
                !validFinalPart || driving != "recorded" ||
                kind != "line" || text is null || bearing is null) continue;
            var course = new StraightCourse(id.Value, type.Value, from.Value, to.Value,
                parent, alongPoints, finalPart, text, bearing, length, drafted);
            if (!courses.TryAdd(id.Value, course))
                Add(DiagnosticCodes.DuplicateProjectId, "Duplicate course ID.", "course", property.Name);
            else foreach (var alongNode in alongNodes)
                if (!nodes.TryAdd(alongNode.Id, alongNode))
                    Add(DiagnosticCodes.DuplicateProjectId,
                        "Along node ID occurs more than once.", "node", alongNode.Id.ToString());
        }
        return courses;
    }

    private Dictionary<BlockId, DraftingBlock> Blocks(JsonElement value, string recordPath)
    {
        var blocks = new Dictionary<BlockId, DraftingBlock>();
        foreach (var property in value.EnumerateObject())
        {
            var id = ParseId(property.Name, BlockId.TryParse, "block");
            if (id is null) continue;
            string path = $"{recordPath}/blocks/{property.Name}";
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                Invalid(path, "Block must be an object.");
                continue;
            }
            Capture(property.Value, path, "name", "type", "parentBlock", "viewOrder",
                "origin", "reportClose", "courses");
            string? name = String(property.Value, "name", path, true);
            var type = Id(property.Value, "type", DraftingTypeId.TryParse, path);
            if (!property.Value.TryGetProperty("parentBlock", out _))
                Invalid($"{path}/parentBlock", "Required nullable ID is missing.");
            var parent = Id(property.Value, "parentBlock", BlockId.TryParse, path, false);
            int? order = Int(property.Value, "viewOrder", path, true);
            if (!property.Value.TryGetProperty("origin", out _))
                Invalid($"{path}/origin", "Required nullable ID is missing.");
            var origin = Id(property.Value, "origin", NodeId.TryParse, path, false);
            bool? report = Bool(property.Value, "reportClose", path);
            var ids = new List<CourseId>();
            if (property.Value.TryGetProperty("courses", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        Invalid(path, "Block course ID must be a string.");
                        continue;
                    }
                    var course = ParseId(item.GetString()!, CourseId.TryParse, "course");
                    if (course is not null) ids.Add(course.Value);
                }
            }
            else Invalid(path, "Block courses must be an array.");
            if (name is null || type is null || order is null || report is null) continue;
            var block = new DraftingBlock(id.Value, name, type.Value, parent, order.Value,
                origin, ids, report.Value);
            if (!blocks.TryAdd(id.Value, block))
                Add(DiagnosticCodes.DuplicateProjectId, "Duplicate block ID.", "block", property.Name);
        }
        return blocks;
    }

    private DateTimeOffset? Time(JsonElement obj, string name)
    {
        string? value = String(obj, name, "$", true);
        if (value is null) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed)) return parsed;
        Invalid($"$/{name}", "Timestamp must be ISO-8601.");
        return null;
    }

    private static bool Property(JsonElement obj, string name, out JsonElement value) =>
        obj.TryGetProperty(name, out value);

    private JsonElement? Object(JsonElement obj, string name, string path, bool required)
    {
        if (!Property(obj, name, out var value))
        {
            if (required) Invalid($"{path}/{name}", "Required object is missing.");
            return null;
        }
        if (value.ValueKind == JsonValueKind.Object) return value;
        Invalid($"{path}/{name}", "Expected object.");
        return null;
    }

    private string? String(JsonElement obj, string name, string path, bool required)
    {
        if (!Property(obj, name, out var value))
        {
            if (required) Invalid($"{path}/{name}", "Required string is missing.");
            return null;
        }
        if (!required && value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        Invalid($"{path}/{name}", "Expected string.");
        return null;
    }

    private int? Int(JsonElement obj, string name, string path, bool required)
    {
        if (!Property(obj, name, out var value))
        {
            if (required) Invalid($"{path}/{name}", "Required integer is missing.");
            return null;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
        Invalid($"{path}/{name}", "Expected integer.");
        return null;
    }

    private double? Number(JsonElement obj, string name, string path, bool required)
    {
        if (!Property(obj, name, out var value))
        {
            if (required) Invalid($"{path}/{name}", "Required number is missing.");
            return null;
        }
        if (value.ValueKind == JsonValueKind.Null && !required) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number))
            return number;
        Invalid($"{path}/{name}", "Expected finite number.");
        return null;
    }

    private bool? Bool(JsonElement obj, string name, string path)
    {
        if (!Property(obj, name, out var value))
        {
            Invalid($"{path}/{name}", "Required boolean is missing.");
            return null;
        }
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        Invalid($"{path}/{name}", "Expected boolean.");
        return null;
    }

    private Distance? Distance(JsonElement obj, string name, string path, bool allowNull)
    {
        if (!Property(obj, name, out var raw))
        {
            Invalid($"{path}/{name}", "Required distance is missing.");
            return null;
        }
        if (allowNull && raw.ValueKind == JsonValueKind.Null) return null;
        double? number = Number(obj, name, path, true);
        if (number is null) return null;
        var distance = Deed.Core.Distance.TryCreate(number.Value);
        if (distance.IsSuccess) return distance.Value;
        Invalid($"{path}/{name}", distance.Diagnostic!.Value.Message);
        return null;
    }

    private T? Id<T>(JsonElement obj, string name, Func<string?, DomainResult<T>> parse,
        string path, bool required = true) where T : struct
    {
        string? value = String(obj, name, path, required);
        return value is null ? null : ParseId(value, parse, typeof(T).Name);
    }

    private T? ParseId<T>(string value, Func<string?, DomainResult<T>> parse, string type) where T : struct
    {
        var result = parse(value);
        if (result.IsSuccess) return result.Value;
        Add(DiagnosticCodes.InvalidId, result.Diagnostic!.Message, type, value);
        return null;
    }

    private void Capture(JsonElement obj, string path, params string[] known)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject())
        {
            if (property.Name == "ext" && property.Value.ValueKind != JsonValueKind.Object)
                Invalid($"{path}/ext", "Ext must be an object.");
            if (!known.Contains(property.Name, StringComparer.Ordinal))
                fields[property.Name] = property.Value.GetRawText();
        }
        if (fields.Count > 0) _preserved[path] = fields;
    }

    private void Invalid(string path, string message) =>
        Add(PersistenceDiagnosticCodes.InvalidValue, $"{path}: {message}");

    private void Add(string code, string message, string? type = null, string? id = null) =>
        _diagnostics.Add(StageAJson.Error(code, message, type, id));
}
