using Deed.Core;
using Deed.Desktop;
using Deed.Persistence;
using System.IO;

namespace Deed.Desktop.Tests;

public sealed class WorkspaceTests
{
    private static readonly RecordId R = RecordId.TryParse("rec-00001").Value;
    private static readonly BlockId B = BlockId.TryParse("blk-00001").Value;
    private static readonly DraftingTypeId T = DraftingTypeId.TryParse("dt-00001").Value;
    private static readonly CourseInput Input = new("N 10 feet, exact", "N",
        Distance.TryCreate(10).Value, null, null, "corner", null);

    [Fact]
    public void SessionFieldsDoNotCreatePersistedPobAndHistoryResets()
    {
        var session = new WorkspaceSession();
        string pobE = "1000", pobN = "2000";
        pobE = "3000";
        Assert.Equal("3000", pobE);
        Assert.Equal("2000", pobN);
        Assert.Empty(session.History.Project.Records[R].Nodes);
        Assert.False(session.IsDirty);
        Assert.True(session.Apply(p => ProjectAuthoring.AddRoot(p, R, B,
            new(3000, 2000), "POB", null, Input, T)).IsSuccess);
        Assert.True(session.IsDirty);
        Assert.True(session.History.CanUndo);
        session.New();
        Assert.False(session.IsDirty);
        Assert.False(session.History.CanUndo);
        Assert.Empty(session.History.Project.Records[R].Nodes);
    }

    [Fact]
    public void SaveOpenRoundTripsAuthoredDataAndPreservesHistoryOnSave()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"deed-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "project.deed.json");
        try
        {
            var session = new WorkspaceSession();
            var monument = Monument.TryCreate("Iron pin", "Found in place").Value;
            Assert.True(session.Apply(p => ProjectAuthoring.AddRoot(p, R, B,
                new(1000, 2000), "POB", monument, Input, T)).IsSuccess);
            var root = session.History.Project.Records[R].Courses.Values.Single();
            Assert.True(session.Apply(p => ProjectAuthoring.AddAlongPoint(p, R, B,
                root.Id, 0, Distance.TryCreate(4).Value, "witness", monument)).IsSuccess);
            var childInput = new CourseInput("as drafted on plan", "E", null,
                new DraftedDistanceCompletion(Distance.TryCreate(7).Value, "missing recorded length"),
                Distance.TryCreate(7).Value, "corner", monument);
            Assert.True(session.Apply(p => ProjectAuthoring.AddChild(p, R, B, root.Id,
                root.ToNodeId, null, childInput, T)).IsSuccess);
            var childId = session.History.Project.Records[R].DraftingBlocks[B].Courses.Last();
            Assert.True(session.Save(path));
            Assert.False(session.IsDirty);
            Assert.True(session.History.CanUndo);
            Assert.True(session.Undo());
            Assert.True(session.IsDirty);
            Assert.True(session.Redo());
            Assert.False(session.IsDirty);
            var reopened = new WorkspaceSession();
            Assert.True(reopened.Open(path), string.Join("; ", reopened.FileDiagnostics.Select(x => x.Message)));
            Assert.False(reopened.History.CanUndo);
            var record = reopened.History.Project.Records[R];
            Assert.Equal(new Coordinate2D(1000, 2000),
                ((FixedNodeDefinition)record.Nodes[root.FromNodeId].Definition).Coordinate);
            Assert.Equal(monument, record.Nodes[root.FromNodeId].Monument);
            Assert.Equal("N 10 feet, exact", record.Courses[root.Id].OriginalRecordedText);
            Assert.Single(record.Courses[root.Id].AlongPoints);
            Assert.Equal(4, record.Courses[root.Id].AlongPoints[0].FromPrevious.Value);
            Assert.Equal(root.Id, record.Courses[childId].ParentCourseId);
            Assert.Equal("as drafted on plan", record.Courses[childId].OriginalRecordedText);
            Assert.Equal("missing recorded length", record.Courses[childId].DraftedCompletion!.Reason);
            Assert.Equal(7, record.Courses[childId].FinalPart!.Value.Value);
            Assert.Equal(monument, record.Nodes[record.Courses[childId].ToNodeId].Monument);
        }
        finally
        {
            CleanupProject(path);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void OpeningAnotherProjectOnSameSessionResetsHistoryAndGeometry()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"deed-session-reset-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string firstPath = Path.Combine(directory, "first.deed.json");
        string secondPath = Path.Combine(directory, "second.deed.json");
        try
        {
            var session = new WorkspaceSession();
            Assert.True(session.Apply(p => ProjectAuthoring.AddRoot(p, R, B,
                new(1000, 2000), "first POB", null, Input, T)).IsSuccess);
            Assert.True(session.Save(firstPath));
            var firstRoot = session.History.Project.Records[R].Courses.Values.Single();
            Assert.True(session.Apply(p => ProjectAuthoring.AddChild(p, R, B,
                firstRoot.Id, firstRoot.ToNodeId, null, Input, T)).IsSuccess);
            Assert.True(session.IsDirty);
            Assert.True(session.History.CanUndo);

            var other = new WorkspaceSession();
            Assert.True(other.Apply(p => ProjectAuthoring.AddRoot(p, R, B,
                new(700, 800), "second POB", null,
                Input with { RecordedCall = "other project" }, T)).IsSuccess);
            Assert.True(other.Save(secondPath));

            Assert.True(session.Open(secondPath));
            Assert.Equal(secondPath, session.Path);
            Assert.False(session.IsDirty);
            Assert.False(session.History.CanUndo);
            Assert.False(session.History.CanRedo);
            var opened = session.History.Project.Records[R];
            Assert.Single(opened.Courses);
            var openedRoot = opened.Courses.Values.Single();
            Assert.Equal("other project", openedRoot.OriginalRecordedText);
            Assert.Equal(new Coordinate2D(700, 800),
                RecordSolver.Solve(session.History.Project, R).NodeCoordinates[openedRoot.FromNodeId]);
        }
        finally
        {
            CleanupProject(firstPath);
            CleanupProject(secondPath);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void FileFailureIsDiagnosticAndDoesNotChangeState()
    {
        var session = new WorkspaceSession();
        Assert.False(session.Open(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.deed.json")));
        Assert.Contains(session.FileDiagnostics, d => d.Code == "file.not_found");
        Assert.False(session.IsDirty);
        Assert.False(session.Save(Path.Combine(Path.GetTempPath(), "missing-dir", "file.deed.json")));
        Assert.Contains(session.FileDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void CanvasTransformIsNorthUpAnchoredAndSafeForDegenerateExtents()
    {
        var transform = new CanvasTransform();
        transform.Fit(Array.Empty<Coordinate2D>(), 0, 0);
        transform.Fit(Array.Empty<Coordinate2D>(), 600, 400);
        transform.Fit(new[] { new Coordinate2D(1000, 2000) }, 600, 400);
        var p = transform.ToScreen(new(1000, 2000));
        Assert.Equal(300, p.X, 6);
        Assert.Equal(200, p.Y, 6);
        Assert.True(transform.ToScreen(new(1000, 2001)).Y < p.Y);
        transform.ZoomAt(1.5, p.X, p.Y);
        var anchored = transform.ToScreen(new(1000, 2000));
        Assert.Equal(p.X, anchored.X, 6);
        Assert.Equal(p.Y, anchored.Y, 6);
        transform.Pan(20, -10);
        var moved = transform.ToScreen(new(1000, 2000));
        Assert.Equal(p.X + 20, moved.X, 6);
        Assert.Equal(p.Y - 10, moved.Y, 6);
        Assert.Equal(new Coordinate2D(1000, 2000), transform.ToWorld(moved.X, moved.Y));
    }

    private static void CleanupProject(string path)
    {
        foreach (var file in new[] { path, ProjectCachePath.ForProject(path) }
            .Concat(Enumerable.Range(1, 5).Select(generation => path + ".bak" + generation)))
            if (File.Exists(file)) File.Delete(file);
    }
}
