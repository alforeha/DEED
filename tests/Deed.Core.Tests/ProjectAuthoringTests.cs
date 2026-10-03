using Deed.Core;

namespace Deed.Core.Tests;

public sealed class ProjectAuthoringTests
{
    private static RecordId R => RecordId.TryParse("rec-00001").Value;
    private static BlockId B => BlockId.TryParse("blk-00001").Value;
    private static DraftingTypeId T => DraftingTypeId.TryParse("dt-00001").Value;
    private static Distance D(double value) => Distance.TryCreate(value).Value;
    private static CourseInput Input(string text = "exact", string bearing = "N", double distance = 100) =>
        new(text, bearing, D(distance), null, null, "corner", null);
    private static DeedProject Root() => ProjectAuthoring.AddRoot(ProjectAuthoring.CreateStageAProject(),
        R, B, new(1000, 2000), "POB", null, Input(), T).Project;

    [Fact]
    public void NewProjectHasOneEmptyBlockAndAdvancedSeedIds()
    {
        var project = ProjectAuthoring.CreateStageAProject();
        Assert.Empty(ProjectValidator.Validate(project));
        Assert.Equal(new ProjectIdCounters(1, 1, 0, 0, 1), project.IdCounters);
        Assert.Empty(project.Records[R].Nodes);
        Assert.Empty(project.Records[R].Courses);
        Assert.Null(project.Records[R].DraftingBlocks[B].Origin);
    }

    [Fact]
    public void RootAndBranchesAllocateAtomicallyAndPreserveHierarchy()
    {
        var project = Root();
        var record = project.Records[R];
        var root = record.Courses.Values.Single();
        Assert.Equal(new ProjectIdCounters(1, 1, 1, 2, 1), project.IdCounters);
        Assert.Equal(new Coordinate2D(1000, 2000),
            ((FixedNodeDefinition)record.Nodes[root.FromNodeId].Definition).Coordinate);
        Assert.Equal(root.FromNodeId, record.DraftingBlocks[B].Origin);
        for (int i = 0; i < 3; i++)
        {
            var result = ProjectAuthoring.AddChild(project, R, B, root.Id, root.ToNodeId,
                null, Input($"child {i}", "E", 10 + i), T);
            Assert.True(result.IsSuccess, string.Join(";", result.Diagnostics.Select(x => x.Code)));
            project = result.Project;
        }
        record = project.Records[R];
        Assert.Equal(4, record.Courses.Count);
        Assert.All(record.Courses.Values.Where(c => c.Id != root.Id), c =>
        {
            Assert.Equal(root.Id, c.ParentCourseId);
            Assert.Equal(root.ToNodeId, c.FromNodeId);
        });
        Assert.Equal(4, record.DraftingBlocks[B].Courses.Count);
        Assert.Empty(ProjectValidator.Validate(project));
    }

    [Fact]
    public void AlongPointsKeepEnteredPartsAndCanBranchFromMiddle()
    {
        var project = Root();
        var root = project.Records[R].Courses.Values.Single();
        project = ProjectEdits.EditFinalPart(project, R, root.Id, D(15)).Project;
        var ids = new List<NodeId>();
        for (int i = 0; i < 3; i++)
        {
            var result = ProjectAuthoring.AddAlongPoint(project, R, B, root.Id, i, D(i + 2), "along", null);
            Assert.True(result.IsSuccess);
            project = result.Project;
            ids.Add(project.Records[R].Courses[root.Id].AlongPoints[i].NodeId);
        }
        var course = project.Records[R].Courses[root.Id];
        Assert.Equal(new[] { 2d, 3d, 4d }, course.AlongPoints.Select(p => p.FromPrevious.Value));
        Assert.Equal(15, course.FinalPart!.Value.Value);
        var branch = ProjectAuthoring.AddChild(project, R, B, root.Id, ids[1], null, Input("branch", "E", 5), T);
        Assert.True(branch.IsSuccess);
        Assert.Equal(ids[1], branch.Project.Records[R].Courses.Values.Last().FromNodeId);
    }

    [Fact]
    public void ExistingEndpointKeepsItsProducerWithoutChangingSelectedParent()
    {
        var project = Root();
        var root = project.Records[R].Courses.Values.Single();
        project = ProjectAuthoring.AddChild(project, R, B, root.Id, root.ToNodeId,
            null, Input("first", "E", 10), T).Project;
        var first = project.Records[R].Courses.Values.Last();
        project = ProjectAuthoring.AddChild(project, R, B, root.Id, root.ToNodeId,
            null, Input("second", "E", 20), T).Project;
        var second = project.Records[R].Courses.Values.Last();
        var existingNode = project.Records[R].Nodes[second.ToNodeId];
        var counters = project.IdCounters;

        var result = ProjectAuthoring.AddChild(project, R, B, first.Id, first.ToNodeId,
            second.ToNodeId, Input("closing", "N", 5), T);

        Assert.True(result.IsSuccess, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var added = result.Project.Records[R].Courses.Values.Last();
        Assert.Equal(first.Id, added.ParentCourseId);
        Assert.Equal(first.ToNodeId, added.FromNodeId);
        Assert.Equal(second.ToNodeId, added.ToNodeId);
        Assert.Equal(counters.N, result.Project.IdCounters.N);
        Assert.Equal(counters.C + 1, result.Project.IdCounters.C);
        Assert.Same(existingNode, result.Project.Records[R].Nodes[second.ToNodeId]);
        Assert.Equal(second.Id,
            ((CourseEndNodeDefinition)result.Project.Records[R].Nodes[second.ToNodeId].Definition).ProducingCourseId);
        Assert.Empty(ProjectValidator.Validate(result.Project));
        Assert.Contains(RecordSolver.Solve(result.Project, R).Diagnostics,
            d => d.Code == DiagnosticCodes.ExistingEndMismatch && d.EntityId == added.Id.ToString());
    }

    [Fact]
    public void MidListInsertionPreservesPartsAndMovesDownstreamSolvedPoints()
    {
        var project = Root();
        var root = project.Records[R].Courses.Values.Single();
        project = ProjectEdits.EditFinalPart(project, R, root.Id, D(40)).Project;
        foreach (var length in new[] { 10d, 20d, 30d })
        {
            var index = project.Records[R].Courses[root.Id].AlongPoints.Count;
            project = ProjectAuthoring.AddAlongPoint(project, R, B, root.Id,
                index, D(length), "along", null).Project;
        }
        var before = project.Records[R].Courses[root.Id];
        var original = before.AlongPoints.ToArray();
        var counters = project.IdCounters;
        var beforeSolve = RecordSolver.Solve(project, R);
        Assert.Equal(2030, beforeSolve.NodeCoordinates[original[1].NodeId].Northing, 6);

        var result = ProjectAuthoring.AddAlongPoint(project, R, B, root.Id, 1, D(5), "inserted", null);

        Assert.True(result.IsSuccess);
        var after = result.Project.Records[R].Courses[root.Id];
        Assert.Equal(new[] { original[0].NodeId, after.AlongPoints[1].NodeId,
            original[1].NodeId, original[2].NodeId }, after.AlongPoints.Select(p => p.NodeId));
        Assert.Equal(new[] { 10d, 5d, 20d, 30d }, after.AlongPoints.Select(p => p.FromPrevious.Value));
        Assert.Equal(before.FinalPart, after.FinalPart);
        Assert.Equal(counters.N + 1, result.Project.IdCounters.N);
        Assert.Equal(counters.C, result.Project.IdCounters.C);
        var solved = RecordSolver.Solve(result.Project, R);
        Assert.Equal(2010, solved.NodeCoordinates[original[0].NodeId].Northing, 6);
        Assert.Equal(2015, solved.NodeCoordinates[after.AlongPoints[1].NodeId].Northing, 6);
        Assert.Equal(2035, solved.NodeCoordinates[original[1].NodeId].Northing, 6);
        Assert.Equal(2065, solved.NodeCoordinates[original[2].NodeId].Northing, 6);
        Assert.Empty(ProjectValidator.Validate(result.Project));
    }

    [Fact]
    public void FailureLeavesProjectAndHistoryUntouched()
    {
        var history = new ProjectEditHistory(Root());
        var source = history.Project;
        var root = source.Records[R].Courses.Values.Single();
        var bad = history.Apply(p => ProjectAuthoring.AddChild(p, R, B, root.Id,
            NodeId.TryParse("n-99999").Value, null, Input(), T));
        Assert.False(bad.IsSuccess);
        Assert.Equal(AuthoringDiagnosticCodes.InvalidAttachment, bad.Diagnostics[0].Code);
        Assert.Same(source, history.Project);
        Assert.False(history.CanUndo);
        bad = history.Apply(p => ProjectAuthoring.AddChild(p, R, B, root.Id,
            root.ToNodeId, null, Input(bearing: "bad"), T));
        Assert.Equal(AuthoringDiagnosticCodes.InvalidBearing, bad.Diagnostics[0].Code);
        Assert.Same(source, history.Project);
        Assert.False(history.CanUndo);
        var exhausted = new DeedProject(source.Settings, source.DraftingTypes.ToDictionary(),
            source.Records.ToDictionary(), source.IdCounters with { C = ProjectIdCounters.Maximum });
        var allocation = ProjectAuthoring.AddChild(exhausted, R, B, root.Id,
            root.ToNodeId, null, Input(), T);
        Assert.Equal(AuthoringDiagnosticCodes.IdAllocation, allocation.Diagnostics[0].Code);
        Assert.Same(exhausted, allocation.Project);
    }

    [Fact]
    public void HistoryUndoesCreationEditingAlongAndCascade()
    {
        var history = new ProjectEditHistory(ProjectAuthoring.CreateStageAProject());
        Assert.True(history.Apply(p => ProjectAuthoring.AddRoot(p, R, B, new(0, 0), "POB", null, Input(), T)).IsSuccess);
        var root = history.Project.Records[R].Courses.Values.Single();
        Assert.True(history.Apply(p => ProjectAuthoring.AddAlongPoint(p, R, B, root.Id, 0, D(5), "along", null)).IsSuccess);
        Assert.True(history.Apply(p => ProjectEdits.EditFinalPart(p, R, root.Id, D(95))).IsSuccess);
        Assert.True(history.Apply(p => ProjectEdits.DeleteCourse(p, R, root.Id)).IsSuccess);
        Assert.Empty(history.Project.Records[R].Courses);
        for (int i = 0; i < 4; i++) Assert.True(history.Undo());
        Assert.Empty(history.Project.Records[R].Courses);
        Assert.Equal(0, history.Project.IdCounters.C);
        for (int i = 0; i < 4; i++) Assert.True(history.Redo());
        Assert.Empty(history.Project.Records[R].Courses);
        Assert.Equal(1, history.Project.IdCounters.C);
    }
}
