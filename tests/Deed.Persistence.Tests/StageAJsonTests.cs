using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deed.Core;
using Deed.Persistence;

namespace Deed.Persistence.Tests;

public class StageAJsonTests
{
    private static readonly string Fixture = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "StageA.json"));
    private static RecordId R(int n) => RecordId.TryParse($"rec-{n:00000}").Value;
    private static CourseId C(int n) => CourseId.TryParse($"c-{n:00000}").Value;
    private static NodeId N(int n) => NodeId.TryParse($"n-{n:00000}").Value;

    private static ProjectDocument Loaded()
    {
        var result = StageAJson.Load(Fixture);
        Assert.NotNull(result.Document);
        Assert.Empty(result.Diagnostics);
        return result.Document;
    }

    [Fact]
    public void HandwrittenFixtureLoadsAndSolvesThreeCourseChain()
    {
        var document = Loaded();
        var solved = RecordSolver.Solve(document.Project, R(1));
        Assert.Equal(3, solved.SolvedLines.Count);
        double diagonal = 100 / Math.Sqrt(2);
        Assert.Equal(1000 + diagonal, solved.NodeCoordinates[N(2)].Easting, 9);
        Assert.Equal(2000 + diagonal, solved.NodeCoordinates[N(2)].Northing, 9);
        Assert.Equal(1000 + 2 * diagonal, solved.NodeCoordinates[N(3)].Easting, 9);
        Assert.Equal(2000, solved.NodeCoordinates[N(3)].Northing, 9);
        Assert.Equal(1000 + diagonal, solved.NodeCoordinates[N(4)].Easting, 9);
        Assert.Equal(2000 - diagonal, solved.NodeCoordinates[N(4)].Northing, 9);
        Assert.True(solved.SolvedLines[C(3)].UsedDraftedDistance);
    }

    [Fact]
    public void ExactTranscriptionAndUnknownFieldsSurviveSemanticRoundTrip()
    {
        var original = Loaded();
        var saved = StageAJson.Save(original);
        Assert.True(saved.IsSuccess);
        var reloaded = StageAJson.Load(saved.Utf8Json!);
        Assert.NotNull(reloaded.Document);
        Assert.Empty(reloaded.Diagnostics);
        var before = original.Project.Records[R(1)].Courses[C(1)];
        var after = reloaded.Document.Project.Records[R(1)].Courses[C(1)];
        Assert.Equal("  Thence North 45 degrees East, 100.00 feet  ", before.OriginalRecordedText);
        Assert.Equal(before.OriginalRecordedText, after.OriginalRecordedText);
        Assert.Equal("  n 45° 00' 00\" e  ", after.ParsedBearing?.OriginalText);
        Assert.Equal(before.ParsedBearing?.Bearing.Azimuth.Degrees,
            after.ParsedBearing?.Bearing.Azimuth.Degrees);
        using var source = JsonDocument.Parse(Fixture);
        using var output = JsonDocument.Parse(saved.Utf8Json!);
        foreach (string path in new[]
        {
            "futureEnvelope", "ext", "settings.futureSettings", "settings.ext",
            "draftingTypes.dt-00001.futureType", "draftingTypes.dt-00001.ext",
            "records.rec-00001.futureRecord", "records.rec-00001.ext",
            "records.rec-00001.nodes.n-00001.futureNode",
            "records.rec-00001.nodes.n-00001.ext",
            "records.rec-00001.nodes.n-00001.monument.futureMonument",
            "records.rec-00001.nodes.n-00001.monument.ext",
            "records.rec-00001.courses.c-00001.futureCourse",
            "records.rec-00001.courses.c-00001.ext",
            "records.rec-00001.blocks.blk-00001.futureBlock",
            "records.rec-00001.blocks.blk-00001.ext"
        })
        {
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(At(source.RootElement, path).GetRawText()),
                JsonNode.Parse(At(output.RootElement, path).GetRawText())), path);
        }
        Assert.Equal(original.Project.Settings, reloaded.Document.Project.Settings);
        Assert.Equal(original.Project.IdCounters, reloaded.Document.Project.IdCounters);
    }

    [Fact]
    public void SerializationIsDeterministicOrderedAndUsesStageAShapes()
    {
        var document = Loaded();
        var first = StageAJson.Save(document);
        var second = StageAJson.Save(document);
        Assert.Equal(first.Utf8Json, second.Utf8Json);
        string json = first.Json!;
        Assert.True(json.IndexOf("\"n-00001\":", StringComparison.Ordinal) <
            json.IndexOf("\"n-00002\":", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"c-00001\":", StringComparison.Ordinal) <
            json.IndexOf("\"c-00002\":", StringComparison.Ordinal));
        using var parsed = JsonDocument.Parse(first.Utf8Json!);
        var root = parsed.RootElement;
        Assert.Equal("DEED", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.False(root.TryGetProperty("sourceRoot", out _));
        var record = root.GetProperty("records").GetProperty("rec-00001");
        var nodes = record.GetProperty("nodes");
        Assert.Equal("fixed", nodes.GetProperty("n-00001").GetProperty("definition").GetProperty("kind").GetString());
        Assert.Equal("courseEnd", nodes.GetProperty("n-00002").GetProperty("definition").GetProperty("kind").GetString());
        var course = record.GetProperty("courses").GetProperty("c-00001");
        Assert.False(course.TryGetProperty("block", out _));
        Assert.Equal("recorded", course.GetProperty("driving").GetString());
        var drafted = record.GetProperty("courses").GetProperty("c-00003");
        Assert.Equal(JsonValueKind.Null, drafted.GetProperty("recorded").GetProperty("length").ValueKind);
        Assert.Equal(100, drafted.GetProperty("drafted").GetProperty("length").GetProperty("value").GetDouble());
        Assert.Equal("Scaled from survey sketch",
            drafted.GetProperty("drafted").GetProperty("length").GetProperty("reason").GetString());
    }

    [Fact]
    public void DefaultTolerancesAndUtcTimestampsRoundTrip()
    {
        var document = Loaded();
        var saved = StageAJson.Save(document);
        var loaded = StageAJson.Load(saved.Json!);
        Assert.Equal(ProjectSettings.Default, loaded.Document!.Project.Settings);
        Assert.Equal(TimeSpan.Zero, loaded.Document.Envelope.Created.Offset);
        Assert.Equal(TimeSpan.Zero, loaded.Document.Envelope.Modified.Offset);
        Assert.Equal("2026-01-02T03:04:05.0000000Z",
            JsonDocument.Parse(saved.Json!).RootElement.GetProperty("created").GetString());
    }

    [Fact]
    public void BytesAndStreamsLoadTheSameDocument()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Fixture);
        Assert.NotNull(StageAJson.Load(bytes.AsMemory()).Document);
        using var stream = new MemoryStream(bytes);
        Assert.NotNull(StageAJson.Load(stream).Document);
    }

    [Theory]
    [InlineData("{", PersistenceDiagnosticCodes.InvalidJson)]
    [InlineData("[]", PersistenceDiagnosticCodes.InvalidValue)]
    public void InvalidJsonAndRootReturnDiagnostics(string json, string code) =>
        Assert.Contains(StageAJson.Load(json).Diagnostics, x => x.Code == code);

    [Fact]
    public void InvalidFormatAndSchemaReturnStableDiagnostics()
    {
        Assert.Contains(StageAJson.Load(Fixture.Replace("\"DEED\"", "\"OTHER\"" )).Diagnostics,
            x => x.Code == PersistenceDiagnosticCodes.InvalidFormat);
        Assert.Contains(StageAJson.Load(Fixture.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2")).Diagnostics,
            x => x.Code == PersistenceDiagnosticCodes.UnsupportedSchemaVersion);
    }

    [Fact]
    public void InvalidIdAndBearingAreDiagnosedWithoutInventingEntities()
    {
        var badId = JsonNode.Parse(Fixture)!.AsObject();
        var nodes = badId["records"]!["rec-00001"]!["nodes"]!.AsObject();
        nodes["n-invalid"] = nodes["n-00001"]!.DeepClone();
        nodes.Remove("n-00001");
        var idResult = StageAJson.Load(badId.ToJsonString());
        Assert.Contains(idResult.Diagnostics, x => x.Code == DiagnosticCodes.InvalidId);
        Assert.NotNull(idResult.Document);
        Assert.False(idResult.Document.Project.Records[R(1)].Nodes.ContainsKey(N(1)));
        Assert.False(StageAJson.Save(idResult.Document).IsSuccess);

        var badBearing = JsonNode.Parse(Fixture)!.AsObject();
        badBearing["records"]!["rec-00001"]!["courses"]!["c-00001"]!["recorded"]!["bearing"] = "nonsense";
        var bearingResult = StageAJson.Load(badBearing.ToJsonString());
        Assert.Contains(bearingResult.Diagnostics, x => x.Code == PersistenceDiagnosticCodes.InvalidBearing);
        Assert.NotNull(bearingResult.Document);
        Assert.False(bearingResult.Document.Project.Records[R(1)].Courses.ContainsKey(C(1)));
        Assert.False(StageAJson.Save(bearingResult.Document).IsSuccess);
    }

    [Fact]
    public void MalformedEntityValueProducesDiagnosticWithoutException()
    {
        var edited = JsonNode.Parse(Fixture)!.AsObject();
        edited["records"]!["rec-00001"]!["nodes"]!["n-00001"]!["definition"]!["at"] =
            JsonNode.Parse("[\"bad\", 2000]");
        var result = StageAJson.Load(edited.ToJsonString());
        Assert.NotNull(result.Document);
        Assert.Contains(result.Diagnostics, x => x.Code == PersistenceDiagnosticCodes.InvalidValue);
        Assert.False(StageAJson.Save(result.Document).IsSuccess);
    }

    [Fact]
    public void MissingDraftedLengthValueCannotBeSilentlySaved()
    {
        var edited = JsonNode.Parse(Fixture)!.AsObject();
        edited["records"]!["rec-00001"]!["courses"]!["c-00003"]!["drafted"]!["length"]!
            .AsObject().Remove("value");
        var result = StageAJson.Load(edited.ToJsonString());
        Assert.NotNull(result.Document);
        Assert.Contains(result.Diagnostics, x => x.Code == PersistenceDiagnosticCodes.InvalidValue);
        Assert.False(StageAJson.Save(result.Document).IsSuccess);
    }

    [Fact]
    public void OrphanLoadsButPreventsAuthoritativeSerialization()
    {
        var edited = JsonNode.Parse(Fixture)!.AsObject();
        edited["idCounters"]!["n"] = 5;
        edited["records"]!["rec-00001"]!["nodes"]!["n-00005"] = JsonNode.Parse(
            "{\"definition\":{\"kind\":\"fixed\",\"at\":[3,4]},\"role\":\"unused\",\"monument\":null}");
        var result = StageAJson.Load(edited.ToJsonString());
        Assert.NotNull(result.Document);
        Assert.True(result.Document.Project.Records[R(1)].Nodes.ContainsKey(N(5)));
        Assert.Contains(result.Diagnostics, x => x.Code == DiagnosticCodes.OrphanNode);
        var save = StageAJson.Save(result.Document);
        Assert.False(save.IsSuccess);
        Assert.Null(save.Utf8Json);
        Assert.Contains(save.Diagnostics, x => x.Code == PersistenceDiagnosticCodes.ValidationFailed);
    }

    [Fact]
    public void CounterBehindAnExistingIdRefusesSave()
    {
        var edited = JsonNode.Parse(Fixture)!.AsObject();
        edited["idCounters"]!["c"] = 1;
        var result = StageAJson.Load(edited.ToJsonString());
        Assert.Contains(result.Diagnostics, x => x.Code == DiagnosticCodes.IdCounterBehind);
        Assert.Equal(1, result.Document!.Project.IdCounters.C);
        Assert.False(StageAJson.Save(result.Document).IsSuccess);
    }

    [Fact]
    public void PreservationCollectionsAreReadOnly()
    {
        var fields = Loaded().Preservation.UnknownFields;
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, IReadOnlyDictionary<string, string>>)fields).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)fields["$"]).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)fields["$/records/rec-00001/nodes/n-00001/monument"]).Clear());
    }

    [Fact]
    public void MonumentObjectAndNullMonumentRoundTrip()
    {
        var document = Loaded();
        var nodes = document.Project.Records[R(1)].Nodes;
        Assert.Equal("pob", nodes[N(1)].Role);
        Assert.Equal("1/2 inch iron rod with cap LS 4471", nodes[N(1)].Monument?.Description);
        Assert.Equal("found", nodes[N(1)].Monument?.Evidence);
        Assert.Null(nodes[N(2)].Monument);

        var saved = StageAJson.Save(document);
        Assert.True(saved.IsSuccess);
        var reloaded = StageAJson.Load(saved.Utf8Json!);
        Assert.NotNull(reloaded.Document);
        Assert.Equal(nodes[N(1)].Monument,
            reloaded.Document.Project.Records[R(1)].Nodes[N(1)].Monument);
        Assert.Null(reloaded.Document.Project.Records[R(1)].Nodes[N(2)].Monument);
        using var json = JsonDocument.Parse(saved.Utf8Json!);
        var writtenNodes = json.RootElement.GetProperty("records").GetProperty("rec-00001")
            .GetProperty("nodes");
        var monument = writtenNodes.GetProperty("n-00001").GetProperty("monument");
        Assert.Equal(JsonValueKind.Object, monument.ValueKind);
        Assert.Equal("1/2 inch iron rod with cap LS 4471",
            monument.GetProperty("description").GetString());
        Assert.Equal("found", monument.GetProperty("evidence").GetString());
        Assert.True(monument.TryGetProperty("futureMonument", out _));
        Assert.True(monument.TryGetProperty("ext", out _));
        Assert.Equal(JsonValueKind.Null,
            writtenNodes.GetProperty("n-00002").GetProperty("monument").ValueKind);
    }

    [Theory]
    [InlineData("\"old monument label\"")]
    [InlineData("[\"old monument label\"]")]
    [InlineData("42")]
    public void InvalidMonumentKindsAreDiagnosedAndBlockSave(string raw)
    {
        var edited = SetMonument(raw);
        var result = StageAJson.Load(edited.ToJsonString());
        Assert.NotNull(result.Document);
        Assert.Contains(result.Diagnostics, x => x.Code == PersistenceDiagnosticCodes.InvalidValue);
        Assert.False(result.Document.Project.Records[R(1)].Nodes.ContainsKey(N(1)));
        Assert.False(StageAJson.Save(result.Document).IsSuccess);
    }

    [Theory]
    [InlineData("{\"evidence\":\"found\"}")]
    [InlineData("{\"description\":\"  \",\"evidence\":\"found\"}")]
    public void MissingOrBlankDescriptionIsDiagnosed(string raw)
    {
        var result = StageAJson.Load(SetMonument(raw).ToJsonString());
        Assert.Contains(result.Diagnostics, x => x.Code == PersistenceDiagnosticCodes.InvalidValue);
        Assert.False(StageAJson.Save(result.Document!).IsSuccess);
    }

    [Fact]
    public void BlankEvidenceIsDiagnosedAndNullEvidenceIsAllowed()
    {
        var invalid = StageAJson.Load(SetMonument(
            "{\"description\":\"rod\",\"evidence\":\"  \"}").ToJsonString());
        Assert.Contains(invalid.Diagnostics, x => x.Code == PersistenceDiagnosticCodes.InvalidValue);
        Assert.False(StageAJson.Save(invalid.Document!).IsSuccess);

        var valid = StageAJson.Load(SetMonument(
            "{\"description\":\"  rod  \",\"evidence\":null}").ToJsonString());
        Assert.NotNull(valid.Document);
        Assert.Empty(valid.Diagnostics);
        Assert.Equal("  rod  ", valid.Document.Project.Records[R(1)].Nodes[N(1)].Monument?.Description);
        Assert.Null(valid.Document.Project.Records[R(1)].Nodes[N(1)].Monument?.Evidence);
        Assert.True(StageAJson.Save(valid.Document).IsSuccess);
    }

    [Fact]
    public void MonumentFieldIsRequiredEvenWhenNull()
    {
        var edited = JsonNode.Parse(Fixture)!.AsObject();
        edited["records"]!["rec-00001"]!["nodes"]!["n-00002"]!.AsObject()
            .Remove("monument");
        var result = StageAJson.Load(edited.ToJsonString());
        Assert.Contains(result.Diagnostics, x => x.Code == PersistenceDiagnosticCodes.InvalidValue);
        Assert.False(StageAJson.Save(result.Document!).IsSuccess);
    }

    [Fact]
    public void RuntimeMonumentFieldsOverridePreservedFields()
    {
        var document = Loaded();
        var record = document.Project.Records[R(1)];
        var nodes = record.Nodes.ToDictionary(x => x.Key, x => x.Value);
        nodes[N(1)] = nodes[N(1)] with
        {
            Monument = Monument.TryCreate("  replacement cap  ", "recovered").Value
        };
        var changedRecord = new DeedRecord(record.Id, nodes,
            record.Courses.ToDictionary(x => x.Key, x => x.Value),
            record.DraftingBlocks.ToDictionary(x => x.Key, x => x.Value));
        var records = document.Project.Records.ToDictionary(x => x.Key, x => x.Value);
        records[R(1)] = changedRecord;
        var project = new DeedProject(document.Project.Settings,
            document.Project.DraftingTypes.ToDictionary(x => x.Key, x => x.Value),
            records, document.Project.IdCounters);
        var saved = StageAJson.Save(document with { Project = project });
        Assert.True(saved.IsSuccess);
        using var json = JsonDocument.Parse(saved.Utf8Json!);
        var monument = json.RootElement.GetProperty("records").GetProperty("rec-00001")
            .GetProperty("nodes").GetProperty("n-00001").GetProperty("monument");
        Assert.Equal("  replacement cap  ", monument.GetProperty("description").GetString());
        Assert.Equal("recovered", monument.GetProperty("evidence").GetString());
        Assert.True(monument.TryGetProperty("futureMonument", out _));
        Assert.True(monument.TryGetProperty("ext", out _));
    }

    [Fact]
    public void NewProjectSerializesWithoutPreservationMetadata()
    {
        var project = new DeedProject(ProjectSettings.Default,
            new Dictionary<DraftingTypeId, DraftingType>(),
            new Dictionary<RecordId, DeedRecord>(), ProjectIdCounters.Empty);
        var envelope = new ProjectEnvelope("1.0", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var save = StageAJson.Save(project, envelope);
        Assert.True(save.IsSuccess);
        var reloaded = StageAJson.Load(save.Utf8Json!);
        Assert.NotNull(reloaded.Document);
        Assert.Empty(reloaded.Diagnostics);
        Assert.Empty(reloaded.Document.Project.Records);
    }

    [Fact]
    public void CurrentRuntimeFieldsWinWhilePreservedUnknownFieldsRemain()
    {
        var document = Loaded();
        var typeId = DraftingTypeId.TryParse("dt-00001").Value;
        var types = document.Project.DraftingTypes.ToDictionary(x => x.Key, x => x.Value);
        types[typeId] = types[typeId] with { Name = "Updated Boundary" };
        var project = new DeedProject(document.Project.Settings, types,
            document.Project.Records.ToDictionary(x => x.Key, x => x.Value), document.Project.IdCounters);
        var changed = document with { Project = project };
        var save = StageAJson.Save(changed);
        Assert.True(save.IsSuccess);
        using var json = JsonDocument.Parse(save.Utf8Json!);
        var type = json.RootElement.GetProperty("draftingTypes").GetProperty("dt-00001");
        Assert.Equal("Updated Boundary", type.GetProperty("name").GetString());
        Assert.True(type.TryGetProperty("futureType", out _));
        Assert.True(type.TryGetProperty("ext", out _));
    }

    private static JsonElement At(JsonElement root, string path)
    {
        foreach (string part in path.Split('.')) root = root.GetProperty(part);
        return root;
    }

    private static JsonObject SetMonument(string raw)
    {
        var edited = JsonNode.Parse(Fixture)!.AsObject();
        edited["records"]!["rec-00001"]!["nodes"]!["n-00001"]!["monument"] = JsonNode.Parse(raw);
        return edited;
    }
}
