namespace Deed.Core.Tests;

public class ProjectEditsTests
{
    private static RecordId R(int n) => RecordId.TryParse($"rec-{n:00000}").Value;
    private static BlockId B(int n) => BlockId.TryParse($"blk-{n:00000}").Value;
    private static CourseId C(int n) => CourseId.TryParse($"c-{n:00000}").Value;
    private static NodeId N(int n) => NodeId.TryParse($"n-{n:00000}").Value;
    private static DraftingTypeId T(int n) => DraftingTypeId.TryParse($"dt-{n:00000}").Value;
    private static Distance D(double n) => Distance.TryCreate(n).Value;
    private static DeedNode Fixed(int n) => new(N(n), "origin", null, new FixedNodeDefinition(new(0, 0)));
    private static DeedNode End(int n, int course) =>
        new(N(n), "corner", null, new CourseEndNodeDefinition(C(course)));
    private static DeedNode Along(int n, int host) =>
        new(N(n), "along", null, new AlongCourseNodeDefinition(C(host)));
    private static StraightCourse Line(int n, int from, int to, int? parent, string bearing = "N",
        Distance? distance = null, IEnumerable<AlongPointPlacement>? along = null,
        Distance? finalPart = null) =>
        new(C(n), T(1), N(from), N(to), parent is null ? null : C(parent.Value),
            along ?? Array.Empty<AlongPointPlacement>(), finalPart, $"call {n}",
            BearingParser.Parse(bearing).Value, distance, null);
    private static DeedProject Project(DeedNode[] nodes, StraightCourse[] courses,
        int[] order, ProjectIdCounters? counters = null) =>
        new(ProjectSettings.Default,
            new Dictionary<DraftingTypeId, DraftingType>
            { [T(1)] = new(T(1), "Boundary", DraftingCategory.Boundary) },
            new Dictionary<RecordId, DeedRecord>
            {
                [R(1)] = new(R(1), nodes.ToDictionary(x => x.Id), courses.ToDictionary(x => x.Id),
                    new Dictionary<BlockId, DraftingBlock>
                    { [B(1)] = new(B(1), "Block", T(1), null, 0, N(1), order.Select(C), false) })
            }, counters ?? new ProjectIdCounters(1, 1, 20, 20, 1));

    [Fact]
    public void CascadeDeletesDescendantsAndKeepsParentOwnedStart()
    {
        var project = Project(new[] { Fixed(1), End(2, 1), End(3, 2), End(4, 3) },
            new[] { Line(1, 1, 2, null, distance: D(10)),
                Line(2, 2, 3, 1, distance: D(10)), Line(3, 3, 4, 2, distance: D(10)) },
            new[] { 3, 1, 2 });
        var result = ProjectEdits.DeleteCourse(project, R(1), C(2));
        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { C(2), C(3) }, result.DeletionReport!.DeletedCourseIds);
        Assert.Equal(new[] { N(3), N(4) }, result.DeletionReport.DeletedNodeIds);
        Assert.Equal(new[] { N(2) }, result.DeletionReport.PreservedParentOwnedStartingNodeIds);
        Assert.Empty(result.DeletionReport.ReassignedEndpoints);
        Assert.Equal(new[] { C(1) }, result.Project.Records[R(1)].DraftingBlocks[B(1)].Courses);
        Assert.Equal(project.IdCounters, result.Project.IdCounters);
        Assert.Empty(ProjectValidator.Validate(result.Project));
    }

    [Fact]
    public void DeletingChildAtFixedParentStartReportsPreservedPob()
    {
        var project = Project(new[] { Fixed(1), End(2, 1), End(3, 2) },
            new[] { Line(1, 1, 2, null, distance: D(10)),
                Line(2, 1, 3, 1, "E", D(5)) }, new[] { 1, 2 });
        var result = ProjectEdits.DeleteCourse(project, R(1), C(2));
        Assert.True(result.IsSuccess);
        var record = result.Project.Records[R(1)];
        Assert.Contains(C(1), record.Courses.Keys);
        Assert.Contains(N(1), record.Nodes.Keys);
        Assert.IsType<FixedNodeDefinition>(record.Nodes[N(1)].Definition);
        Assert.Equal(new[] { C(2) }, result.DeletionReport!.DeletedCourseIds);
        Assert.Equal(new[] { N(3) }, result.DeletionReport.DeletedNodeIds);
        Assert.Equal(new[] { N(1) }, result.DeletionReport.PreservedParentOwnedStartingNodeIds);
        Assert.Empty(result.DeletionReport.ReassignedEndpoints);
        Assert.DoesNotContain(N(1), result.DeletionReport.DeletedNodeIds);
        Assert.DoesNotContain(result.DeletionReport.ReassignedEndpoints, x => x.NodeId == N(1));
        Assert.Empty(ProjectValidator.Validate(result.Project));
    }

    [Fact]
    public void BranchDeletionKeepsHostAlongWhileHostDeletionRemovesEverything()
    {
        var project = Project(new[] { Fixed(1), End(2, 1), Along(3, 1), End(4, 2), End(5, 3) },
            new[] { Line(1, 1, 2, null, distance: D(10),
                    along: new[] { new AlongPointPlacement(N(3), D(4)) }),
                Line(2, 3, 4, 1, "E", D(5)), Line(3, 4, 5, 2, "E", D(2)) },
            new[] { 1, 2, 3 });
        var branch = ProjectEdits.DeleteCourse(project, R(1), C(2));
        Assert.True(branch.IsSuccess);
        Assert.Contains(N(3), branch.Project.Records[R(1)].Nodes.Keys);
        Assert.Equal(new[] { N(3) }, branch.DeletionReport!.PreservedParentOwnedStartingNodeIds);
        Assert.Equal(new[] { N(4), N(5) }, branch.DeletionReport.DeletedNodeIds);
        var root = ProjectEdits.DeleteCourse(project, R(1), C(1));
        Assert.True(root.IsSuccess);
        Assert.Empty(root.Project.Records[R(1)].Courses);
        Assert.Empty(root.Project.Records[R(1)].Nodes);
        Assert.Empty(root.Project.Records[R(1)].DraftingBlocks[B(1)].Courses);
        Assert.Null(root.Project.Records[R(1)].DraftingBlocks[B(1)].Origin);
        Assert.Equal(project.IdCounters, root.Project.IdCounters);
        Assert.Empty(ProjectValidator.Validate(root.Project));
    }

    [Fact]
    public void SharedEndpointIsReassignedToLowestSurvivingCourseWithoutReparenting()
    {
        var project = Project(new[] { Fixed(1), End(2, 1), End(3, 2), End(4, 4) },
            new[] { Line(1, 1, 2, null, distance: D(10)),
                Line(2, 2, 3, 1, "E", D(5)), Line(3, 2, 3, 1, "W", D(5)),
                Line(4, 3, 4, 3, "N", D(3)), Line(5, 2, 3, 1, "E", D(5)) },
            new[] { 1, 5, 4, 3, 2 });
        var result = ProjectEdits.DeleteCourse(project, R(1), C(2));
        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { new ReassignedEndpoint(N(3), C(2), C(3)) },
            result.DeletionReport!.ReassignedEndpoints);
        Assert.Empty(result.DeletionReport.DeletedNodeIds);
        var record = result.Project.Records[R(1)];
        Assert.Equal(C(3), ((CourseEndNodeDefinition)record.Nodes[N(3)].Definition).ProducingCourseId);
        Assert.Equal(C(1), record.Courses[C(3)].ParentCourseId);
        Assert.Equal(C(3), record.Courses[C(4)].ParentCourseId);
        Assert.Contains(RecordSolver.Solve(result.Project, R(1)).Diagnostics,
            x => x.Code == DiagnosticCodes.ExistingEndMismatch && x.EntityId == C(5).ToString());
        Assert.Empty(ProjectValidator.Validate(result.Project));
    }

    [Fact]
    public void EditsPropagateAndHistoryRestoresWholeSnapshots()
    {
        var project = Project(new[] { Fixed(1), End(2, 1), Along(3, 1), Along(4, 1), End(5, 2) },
            new[] { Line(1, 1, 2, null, distance: D(20),
                    along: new[] { new AlongPointPlacement(N(3), D(4)),
                        new AlongPointPlacement(N(4), D(5)) }),
                Line(2, 4, 5, 1, "E", D(3)) }, new[] { 1, 2 });
        var history = new ProjectEditHistory(project);
        var original = RecordSolver.Solve(project, R(1));
        Assert.True(history.Apply(p => ProjectEdits.EditAlongDistance(p, R(1), C(1), N(4), D(7))).Changed);
        var moved = RecordSolver.Solve(history.Project, R(1));
        Assert.Equal(original.NodeCoordinates[N(3)], moved.NodeCoordinates[N(3)]);
        Assert.Equal(original.NodeCoordinates[N(4)].Northing + 2, moved.NodeCoordinates[N(4)].Northing);
        Assert.Equal(original.NodeCoordinates[N(5)].Northing + 2, moved.NodeCoordinates[N(5)].Northing);
        Assert.Equal(original.NodeCoordinates[N(2)], moved.NodeCoordinates[N(2)]);
        Assert.Equal(project.Records[R(1)].Courses[C(2)].RecordedDistance,
            history.Project.Records[R(1)].Courses[C(2)].RecordedDistance);
        Assert.True(history.Apply(p => ProjectEdits.EditFinalPart(p, R(1), C(1), D(9))).Changed);
        var withFinalPart = RecordSolver.Solve(history.Project, R(1));
        Assert.Null(moved.PartSummaries[C(1)].FinalPart);
        Assert.Equal(9, withFinalPart.PartSummaries[C(1)].FinalPart);
        Assert.Equal(20, withFinalPart.PartSummaries[C(1)].EnteredTotal);
        Assert.Equal(moved.NodeCoordinates[N(2)], withFinalPart.NodeCoordinates[N(2)]);
        Assert.True(history.Undo());
        Assert.Same(project, history.Undo() ? history.Project : null);
        Assert.True(history.CanRedo);
        Assert.True(history.Redo());
        Assert.Equal(moved.NodeCoordinates[N(4)], RecordSolver.Solve(history.Project, R(1)).NodeCoordinates[N(4)]);
        Assert.True(history.Apply(p => ProjectEdits.EditBearing(p, R(1), C(1), "E")).Changed);
        Assert.False(history.CanRedo);
        Assert.Equal(20, RecordSolver.Solve(history.Project, R(1)).NodeCoordinates[N(2)].Easting);
    }

    [Fact]
    public void FailedAndNoOpEditsLeaveProjectAndHistoryUnchanged()
    {
        var project = Project(new[] { Fixed(1), End(2, 1) },
            new[] { Line(1, 1, 2, null, distance: D(10)) }, new[] { 1 });
        var history = new ProjectEditHistory(project);
        Assert.Equal(DiagnosticCodes.EditMissingRecord,
            history.Apply(p => ProjectEdits.DeleteCourse(p, R(9), C(1))).Diagnostics.Single().Code);
        Assert.Equal(DiagnosticCodes.EditMissingCourse,
            history.Apply(p => ProjectEdits.EditFinalPart(p, R(1), C(9), D(1))).Diagnostics.Single().Code);
        Assert.Equal(DiagnosticCodes.EditMissingAlongPoint,
            history.Apply(p => ProjectEdits.EditAlongDistance(p, R(1), C(1), N(9), D(1))).Diagnostics.Single().Code);
        Assert.Equal(DiagnosticCodes.EditInvalidBearing,
            history.Apply(p => ProjectEdits.EditBearing(p, R(1), C(1), "not a bearing")).Diagnostics.Single().Code);
        Assert.False(history.Apply(p => ProjectEdits.EditRecordedDistance(p, R(1), C(1), D(10))).Changed);
        Assert.Same(project, history.Project);
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.True(history.Apply(p => ProjectEdits.EditRecordedText(p, R(1), C(1), "corrected")).Changed);
        Assert.True(history.Undo());
        Assert.True(history.CanRedo);
        Assert.False(history.Apply(p => ProjectEdits.EditRecordedText(p, R(1), C(1), "call 1")).Changed);
        Assert.True(history.CanRedo);
        Assert.False(history.Apply(p => ProjectEdits.EditBearing(p, R(1), C(1), "invalid")).IsSuccess);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void RootCascadeNeedsNoSolvedCoordinatesAndUndoIsOneStep()
    {
        var project = Project(new[] { Fixed(1), End(2, 1), End(3, 2) },
            new[] { Line(1, 1, 2, null), Line(2, 2, 3, 1) }, new[] { 1, 2 });
        var history = new ProjectEditHistory(project);
        var deletion = history.Apply(p => ProjectEdits.DeleteCourse(p, R(1), C(1)));
        Assert.True(deletion.IsSuccess);
        Assert.Equal(new[] { C(1), C(2) }, deletion.DeletionReport!.DeletedCourseIds);
        Assert.True(history.Undo());
        Assert.Same(project, history.Project);
        Assert.True(history.Redo());
        Assert.Same(deletion.Project, history.Project);
        Assert.Equal(project.IdCounters, history.Project.IdCounters);
        Assert.Equal("c-00021", ProjectIdAllocator.Allocate(history.Project, ProjectIdKind.Course).Value.Value);
    }

    [Fact]
    public void ParentBearingAndLengthMoveOwnedGeometryWithoutEditingChildInputs()
    {
        var project = Project(new[] { Fixed(1), End(2, 1), Along(3, 1), End(4, 2) },
            new[] { Line(1, 1, 2, null, distance: D(10),
                    along: new[] { new AlongPointPlacement(N(3), D(4)) }),
                Line(2, 3, 4, 1, "E", D(2)) }, new[] { 1, 2 });
        var child = project.Records[R(1)].Courses[C(2)];
        var bearing = ProjectEdits.EditBearing(project, R(1), C(1), "E");
        Assert.True(bearing.IsSuccess);
        var solved = RecordSolver.Solve(bearing.Project, R(1));
        Assert.Equal(4, solved.NodeCoordinates[N(3)].Easting);
        Assert.Equal(6, solved.NodeCoordinates[N(4)].Easting);
        Assert.Equal(10, solved.NodeCoordinates[N(2)].Easting);
        var length = ProjectEdits.EditRecordedDistance(bearing.Project, R(1), C(1), D(12));
        Assert.True(length.IsSuccess);
        Assert.Equal(12, RecordSolver.Solve(length.Project, R(1)).NodeCoordinates[N(2)].Easting);
        Assert.Same(child, length.Project.Records[R(1)].Courses[C(2)]);
        Assert.Empty(ProjectValidator.Validate(length.Project));
    }

    [Fact]
    public void ParentLengthMovesChildAttachedAtEndpoint()
    {
        var project = Project(new[] { Fixed(1), End(2, 1), End(3, 2) },
            new[] { Line(1, 1, 2, null, distance: D(10)),
                Line(2, 2, 3, 1, "E", D(2)) }, new[] { 1, 2 });
        var before = RecordSolver.Solve(project, R(1));
        var edited = ProjectEdits.EditRecordedDistance(project, R(1), C(1), D(15));
        Assert.True(edited.IsSuccess);
        var after = RecordSolver.Solve(edited.Project, R(1));
        Assert.Equal(10, before.NodeCoordinates[N(3)].Northing);
        Assert.Equal(15, after.NodeCoordinates[N(2)].Northing);
        Assert.Equal(15, after.NodeCoordinates[N(3)].Northing);
        Assert.Equal(project.Records[R(1)].Courses[C(2)].RecordedDistance,
            edited.Project.Records[R(1)].Courses[C(2)].RecordedDistance);
    }

    [Fact]
    public void DraftedCompletionCanDriveAbsentRecordedDistance()
    {
        var project = Project(new[] { Fixed(1), End(2, 1) },
            new[] { Line(1, 1, 2, null) }, new[] { 1 });
        var completion = ProjectEdits.EditDraftedCompletion(project, R(1), C(1),
            new DraftedDistanceCompletion(D(7), "sketch"));
        Assert.True(completion.IsSuccess);
        Assert.Equal(7, RecordSolver.Solve(completion.Project, R(1)).NodeCoordinates[N(2)].Northing);
        var recorded = ProjectEdits.EditRecordedDistance(completion.Project, R(1), C(1), D(8));
        Assert.True(recorded.IsSuccess);
        Assert.Null(recorded.Project.Records[R(1)].Courses[C(1)].DraftedCompletion);
        Assert.Equal(D(8), recorded.Project.Records[R(1)].Courses[C(1)].RecordedDistance);
        Assert.Equal(8, RecordSolver.Solve(recorded.Project, R(1)).NodeCoordinates[N(2)].Northing);
        var invalid = ProjectEdits.EditDraftedCompletion(completion.Project, R(1), C(1),
            new DraftedDistanceCompletion(D(8), " "));
        Assert.False(invalid.IsSuccess);
        Assert.Same(completion.Project, invalid.Project);
        Assert.Equal(DiagnosticCodes.EditInvalidValue, invalid.Diagnostics.Single().Code);
    }

    [Fact]
    public void InvalidStructureIsRejectedAndReportsAreReadOnly()
    {
        var valid = Project(new[] { Fixed(1), End(2, 1) },
            new[] { Line(1, 1, 2, null, distance: D(10)) }, new[] { 1 });
        var brokenRecord = new DeedRecord(R(1),
            new Dictionary<NodeId, DeedNode>(valid.Records[R(1)].Nodes),
            new Dictionary<CourseId, StraightCourse>(valid.Records[R(1)].Courses),
            new Dictionary<BlockId, DraftingBlock>
            { [B(1)] = new(B(1), "Block", T(1), null, 0, N(1), new[] { C(9) }, false) });
        var broken = new DeedProject(valid.Settings,
            new Dictionary<DraftingTypeId, DraftingType>(valid.DraftingTypes),
            new Dictionary<RecordId, DeedRecord> { [R(1)] = brokenRecord }, valid.IdCounters);
        var failed = ProjectEdits.DeleteCourse(broken, R(1), C(1));
        Assert.False(failed.IsSuccess);
        Assert.Same(broken, failed.Project);
        Assert.Equal(DiagnosticCodes.EditInvalidProject, failed.Diagnostics.Single().Code);
        var result = ProjectEdits.DeleteCourse(valid, R(1), C(1));
        Assert.True(result.IsSuccess);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<CourseId>)result.DeletionReport!.DeletedCourseIds).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<NodeId>)result.DeletionReport!.DeletedNodeIds).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<NodeId>)result.DeletionReport!.PreservedParentOwnedStartingNodeIds).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ReassignedEndpoint>)result.DeletionReport!.ReassignedEndpoints).Clear());
    }
}
