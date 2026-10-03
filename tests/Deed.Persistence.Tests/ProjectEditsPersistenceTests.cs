using System.Text.Json.Nodes;
using Deed.Core;
using Deed.Persistence;

namespace Deed.Persistence.Tests;

public class ProjectEditsPersistenceTests
{
    private static readonly string Fixture = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "StageA.json"));
    private static RecordId R(int n) => RecordId.TryParse($"rec-{n:00000}").Value;
    private static CourseId C(int n) => CourseId.TryParse($"c-{n:00000}").Value;
    private static NodeId N(int n) => NodeId.TryParse($"n-{n:00000}").Value;
    private static Distance D(double n) => Distance.TryCreate(n).Value;

    [Fact]
    public void HandwrittenFixtureEditsAndDeletionPreserveSurvivingUnknownFields()
    {
        var document = StageAJson.Load(Fixture).Document!;
        var history = new ProjectEditHistory(document.Project);
        Assert.True(history.Apply(p => ProjectEdits.EditRecordedCall(p, R(1), C(1),
            "corrected transcription", "N45E", D(260))).IsSuccess);
        Assert.True(history.Apply(p => ProjectEdits.EditAlongDistance(p, R(1), C(1), N(6), D(51))).IsSuccess);
        var deletion = history.Apply(p => ProjectEdits.DeleteCourse(p, R(1), C(2)));
        Assert.True(deletion.IsSuccess);
        Assert.Equal(new[] { C(2), C(3) }, deletion.DeletionReport!.DeletedCourseIds);
        Assert.True(history.Undo());
        Assert.Contains(C(3), history.Project.Records[R(1)].Courses.Keys);
        Assert.True(history.Redo());
        var saved = StageAJson.Save(document with { Project = history.Project });
        Assert.True(saved.IsSuccess);
        var reloaded = StageAJson.Load(saved.Utf8Json!).Document!;
        Assert.DoesNotContain(C(2), reloaded.Project.Records[R(1)].Courses.Keys);
        Assert.DoesNotContain(C(3), reloaded.Project.Records[R(1)].Courses.Keys);
        Assert.Equal("corrected transcription", reloaded.Project.Records[R(1)].Courses[C(1)].OriginalRecordedText);
        Assert.Equal(51, reloaded.Project.Records[R(1)].Courses[C(1)].AlongPoints[1].FromPrevious.Value);
        var output = JsonNode.Parse(saved.Json!)!;
        var original = JsonNode.Parse(Fixture)!;
        foreach (var path in new[] { "ext", "futureEnvelope", "records.rec-00001.ext",
            "records.rec-00001.nodes.n-00001.futureNode",
            "records.rec-00001.courses.c-00001.ext",
            "records.rec-00001.courses.c-00001.along.1.futureAlong" })
            Assert.True(JsonNode.DeepEquals(At(original, path), At(output, path)), path);
        Assert.Null(At(output, "records.rec-00001.courses.c-00002"));
        Assert.Null(At(output, "records.rec-00001.nodes.n-00003"));
        Assert.Empty(ProjectValidator.Validate(reloaded.Project));
    }

    [Fact]
    public void ReassignedEndpointKeepsRoleMonumentAndUnknownFieldsAcrossSaveReload()
    {
        var fixture = JsonNode.Parse(Fixture)!;
        var record = fixture["records"]!["rec-00001"]!;
        var nodes = record["nodes"]!.AsObject();
        var courses = record["courses"]!.AsObject();
        var blockCourses = record["blocks"]!["blk-00001"]!["courses"]!.AsArray();
        var endpoint = nodes["n-00003"]!;
        endpoint["role"] = "sharedCorner";
        endpoint["monument"] = JsonNode.Parse("""{"description":"Iron pin","evidence":"found","ext":{"source":"field"}}""");
        endpoint["ext"] = JsonNode.Parse("""{"tag":{"survey":12}}""");
        endpoint["definition"]!["ext"] = JsonNode.Parse("""{"legacy":true}""");
        courses["c-00005"] = JsonNode.Parse("""{"type":"dt-00001","from":"n-00002","to":"n-00003","parent":"c-00001","along":[],"finalPart":null,"recorded":{"kind":"line","text":"alternate","bearing":"S45E","length":100},"drafted":null,"driving":"recorded"}""");
        courses["c-00006"] = JsonNode.Parse("""{"type":"dt-00001","from":"n-00003","to":"n-00009","parent":"c-00005","along":[],"finalPart":null,"recorded":{"kind":"line","text":"continuation","bearing":"E","length":5},"drafted":null,"driving":"recorded"}""");
        nodes["n-00009"] = JsonNode.Parse("""{"definition":{"kind":"courseEnd","course":"c-00006"},"role":"end","monument":null}""");
        blockCourses.Add("c-00005");
        blockCourses.Add("c-00006");
        fixture["idCounters"]!["c"] = 6;
        fixture["idCounters"]!["n"] = 9;
        var loaded = StageAJson.Load(fixture.ToJsonString());
        Assert.NotNull(loaded.Document);
        Assert.DoesNotContain(loaded.Diagnostics, x => x.Severity == DiagnosticSeverity.Error);
        var deletion = ProjectEdits.DeleteCourse(loaded.Document.Project, R(1), C(2));
        Assert.True(deletion.IsSuccess);
        Assert.Equal(new[] { new ReassignedEndpoint(N(3), C(2), C(5)) },
            deletion.DeletionReport!.ReassignedEndpoints);
        var saved = StageAJson.Save(loaded.Document with { Project = deletion.Project });
        Assert.True(saved.IsSuccess);
        var reloaded = StageAJson.Load(saved.Utf8Json!).Document!;
        var retained = reloaded.Project.Records[R(1)].Nodes[N(3)];
        Assert.Equal("sharedCorner", retained.Role);
        Assert.Equal("Iron pin", retained.Monument!.Description);
        Assert.Equal(C(5), ((CourseEndNodeDefinition)retained.Definition).ProducingCourseId);
        Assert.Contains(C(6), reloaded.Project.Records[R(1)].Courses.Keys);
        var output = JsonNode.Parse(saved.Json!)!;
        foreach (var path in new[] { "records.rec-00001.nodes.n-00003.ext",
            "records.rec-00001.nodes.n-00003.monument.ext",
            "records.rec-00001.nodes.n-00003.definition.ext" })
            Assert.True(JsonNode.DeepEquals(At(fixture, path), At(output, path)), path);
        Assert.Null(At(output, "records.rec-00001.courses.c-00002"));
        Assert.Null(At(output, "records.rec-00001.courses.c-00003"));
    }

    private static JsonNode? At(JsonNode node, string path)
    {
        JsonNode? current = node;
        foreach (string part in path.Split('.'))
            current = current is JsonArray array ? array[int.Parse(part)] : current?[part];
        return current;
    }
}
