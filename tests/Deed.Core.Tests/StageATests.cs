namespace Deed.Core.Tests;

public class StageATests
{
    private static RecordId R(int n) => RecordId.TryParse($"rec-{n:00000}").Value;
    private static BlockId B(int n) => BlockId.TryParse($"blk-{n:00000}").Value;
    private static CourseId C(int n) => CourseId.TryParse($"c-{n:00000}").Value;
    private static NodeId N(int n) => NodeId.TryParse($"n-{n:00000}").Value;
    private static DraftingTypeId T(int n) => DraftingTypeId.TryParse($"dt-{n:00000}").Value;
    private static Distance D(double value) => Distance.TryCreate(value).Value;
    private static Bearing Bearing(string text) => BearingParser.Parse(text).Value.Bearing;
    private static DeedNode Fixed(int n, double x = 0, double y = 0) =>
        new(N(n), "point", null, new FixedNodeDefinition(new(x, y)));
    private static DeedNode End(int n, int course) =>
        new(N(n), "point", null, new CourseEndNodeDefinition(C(course)));
    private static StraightCourse Line(int n, int from, int to, string bearing = "N",
        Distance? recorded = null, DraftedDistanceCompletion? drafted = null,
        DraftingTypeId? type = null) =>
        new(C(n), type ?? T(1), N(from), N(to), $"  original {n}  ",
            Bearing(bearing), recorded, drafted);
    private static DraftingBlock Block(IEnumerable<CourseId> ids, NodeId? origin = null,
        DraftingTypeId? type = null) =>
        new(B(1), "Boundary", type ?? T(1), null, 0, origin, ids, false);
    private static DeedProject Project(IEnumerable<DeedNode> nodes,
        IEnumerable<StraightCourse> courses, DraftingBlock? block = null,
        IEnumerable<DraftingType>? types = null) =>
        new(ProjectSettings.Default,
            (types ?? new[] { new DraftingType(T(1), "Boundary", DraftingCategory.Boundary) })
                .ToDictionary(t => t.Id),
            new Dictionary<RecordId, DeedRecord> { [R(1)] = new(R(1),
                nodes.ToDictionary(n => n.Id), courses.ToDictionary(c => c.Id),
                block is null ? new Dictionary<BlockId, DraftingBlock>() :
                    new Dictionary<BlockId, DraftingBlock> { [block.Id] = block }) });
    private static RecordSolveResult Solve(DeedProject project) => RecordSolver.Solve(project, R(1));
    private static void Has(RecordSolveResult result, string code) =>
        Assert.Contains(result.Diagnostics, d => d.Code == code);

    [Fact]
    public void TypedIdsHaveCanonicalCaseSensitiveValueAndStructuredFailures()
    {
        Assert.Equal("rec-00001", R(1).ToString());
        Assert.Equal("blk-00001", B(1).ToString());
        Assert.Equal("c-00001", C(1).ToString());
        Assert.Equal("n-00001", N(1).ToString());
        Assert.Equal("dt-00001", T(1).ToString());
        Assert.Equal(DiagnosticCodes.InvalidId, RecordId.TryParse("REC-00001").Diagnostic?.Code);
        Assert.Equal(DiagnosticCodes.InvalidId, BlockId.TryParse("blk-001").Diagnostic?.Code);
        Assert.Equal(DiagnosticCodes.InvalidId, CourseId.TryParse("c-000001").Diagnostic?.Code);
        Assert.Equal(DiagnosticCodes.InvalidId, NodeId.TryParse("n-0000x").Diagnostic?.Code);
        Assert.Equal(DiagnosticCodes.InvalidId, DraftingTypeId.TryParse("dt-００００１").Diagnostic?.Code);
        Assert.Equal("safe", new Dictionary<NodeId, string> { [N(1)] = "safe" }[N(1)]);
    }

    [Fact]
    public void DefaultValueIdCannotPassProjectValidation()
    {
        var record = new DeedRecord(R(1), new Dictionary<NodeId, DeedNode>
        {
            [default] = new(default, "point", null, new FixedNodeDefinition(new(0, 0)))
        }, new Dictionary<CourseId, StraightCourse>(), new Dictionary<BlockId, DraftingBlock>());
        var project = new DeedProject(ProjectSettings.Default,
            new Dictionary<DraftingTypeId, DraftingType>(),
            new Dictionary<RecordId, DeedRecord> { [R(1)] = record });
        Has(Solve(project), DiagnosticCodes.InvalidId);
    }

    [Fact]
    public void CollectionKeysMustAgreeWithEntityIds()
    {
        var record = new DeedRecord(R(1), new Dictionary<NodeId, DeedNode>
        {
            [N(1)] = Fixed(2)
        }, new Dictionary<CourseId, StraightCourse>(), new Dictionary<BlockId, DraftingBlock>());
        var project = new DeedProject(ProjectSettings.Default,
            new Dictionary<DraftingTypeId, DraftingType>(),
            new Dictionary<RecordId, DeedRecord> { [R(1)] = record });
        Has(Solve(project), DiagnosticCodes.IdMismatch);
    }

    [Theory]
    [InlineData(double.NaN, 0.01, 1, 5000)]
    [InlineData(0.01, 0, 1, 5000)]
    [InlineData(0.01, 0.01, double.PositiveInfinity, 5000)]
    [InlineData(0.01, 0.01, 1, -1)]
    public void InvalidSettingsReturnDiagnostic(double a, double b, double c, double d) =>
        Assert.Equal(DiagnosticCodes.InvalidSettings, ProjectSettings.TryCreate(a, b, c, d).Diagnostic?.Code);

    [Fact]
    public void SettingsUseApprovedDefaults()
    {
        Assert.Equal(0.01, ProjectSettings.Default.CoincidenceDistance);
        Assert.Equal(0.01, ProjectSettings.Default.LinearElementDifference);
        Assert.Equal(1, ProjectSettings.Default.AngularElementDifferenceSeconds);
        Assert.Equal(5000, ProjectSettings.Default.MinimumClosurePrecision);
    }

    [Fact]
    public void ThreeCourseChainSolvesFromIdsRegardlessOfDisplayOrder()
    {
        var courses = new[] { Line(1, 1, 2, "N45E", D(100)),
            Line(2, 2, 3, "S45E", D(100)), Line(3, 3, 4, "S45W", D(100)) };
        var nodes = new[] { Fixed(1, 1000, 2000), End(2, 1), End(3, 2), End(4, 3) };
        var first = Solve(Project(nodes, courses, Block(new[] { C(1), C(2), C(3) }, N(1))));
        var reordered = Solve(Project(nodes, courses, Block(new[] { C(3), C(1), C(2) }, N(1))));
        double component = 100 / Math.Sqrt(2);
        Assert.Equal(1000 + component, first.NodeCoordinates[N(2)].Easting, 9);
        Assert.Equal(2000 + component, first.NodeCoordinates[N(2)].Northing, 9);
        Assert.Equal(1000 + 2 * component, first.NodeCoordinates[N(3)].Easting, 9);
        Assert.Equal(2000, first.NodeCoordinates[N(3)].Northing, 9);
        Assert.Equal(1000 + component, first.NodeCoordinates[N(4)].Easting, 9);
        Assert.Equal(2000 - component, first.NodeCoordinates[N(4)].Northing, 9);
        Assert.Equal(first.NodeCoordinates, reordered.NodeCoordinates);
        Assert.Equal(3, first.SolvedLines.Count);
        Assert.DoesNotContain(first.Diagnostics, d => d.Code == DiagnosticCodes.DisconnectedBlock);
        Assert.Equal("  original 1  ", courses[0].OriginalRecordedText);
    }

    [Fact]
    public void DraftedDistanceCompletesOnlyMissingRecordedDistance()
    {
        var course = Line(1, 1, 2, "W", drafted: new(D(20), "Measured on plat"));
        var result = Solve(Project(new[] { Fixed(1), End(2, 1) }, new[] { course }, Block(new[] { C(1) }, N(1))));
        Assert.True(result.SolvedLines[C(1)].UsedDraftedDistance);
        Assert.Equal(-20, result.NodeCoordinates[N(2)].Easting, 9);
    }

    [Fact]
    public void DraftedCompletionCannotOverrideRecordedDistanceOrOmitReason()
    {
        var course = Line(1, 1, 2, recorded: D(10), drafted: new(D(20), "override"));
        var result = Solve(Project(new[] { Fixed(1), End(2, 1) }, new[] { course }, Block(new[] { C(1) }, N(1))));
        Has(result, DiagnosticCodes.InvalidDraftedCompletion);
        Assert.Equal(10, result.NodeCoordinates[N(2)].Northing, 9);
        Assert.False(result.SolvedLines[C(1)].UsedDraftedDistance);
        var blank = Solve(Project(new[] { Fixed(1), End(2, 1) },
            new[] { Line(1, 1, 2, drafted: new(D(20), " ")) }, Block(new[] { C(1) }, N(1))));
        Has(blank, DiagnosticCodes.InvalidDraftedCompletion);
    }

    [Fact]
    public void IncompleteCourseRetainsIndependentResultsAndBlocksDownstream()
    {
        var courses = new[] { Line(1, 1, 2, recorded: D(10)), Line(2, 2, 3),
            Line(3, 3, 4, recorded: D(10)), Line(4, 5, 6, recorded: D(7)) };
        var nodes = new[] { Fixed(1), End(2, 1), End(3, 2), End(4, 3), Fixed(5), End(6, 4) };
        var result = Solve(Project(nodes, courses, Block(courses.Select(c => c.Id), N(1))));
        Has(result, DiagnosticCodes.IncompleteCourse);
        Has(result, DiagnosticCodes.UnresolvedDependency);
        Assert.Equal(2, result.SolvedLines.Count);
        Assert.True(result.NodeCoordinates.ContainsKey(N(2)));
        Assert.True(result.NodeCoordinates.ContainsKey(N(6)));
        Assert.False(result.NodeCoordinates.ContainsKey(N(3)));
    }

    [Fact]
    public void StructureReportsDanglingMembershipAndOrphanIssues()
    {
        var course = Line(1, 9, 2, recorded: D(10), type: T(9));
        var result = Solve(Project(new[] { End(2, 1), Fixed(3) }, new[] { course },
            Block(new[] { C(9) }, N(3), T(9))));
        Has(result, DiagnosticCodes.DanglingReference);
        Has(result, DiagnosticCodes.CourseMembership);
        Has(result, DiagnosticCodes.OrphanNode);
        var duplicate = Solve(Project(new[] { Fixed(1), End(2, 1) },
            new[] { Line(1, 1, 2, recorded: D(1)) }, Block(new[] { C(1), C(1) }, N(1))));
        Has(duplicate, DiagnosticCodes.DuplicateCourseMembership);
    }

    [Fact]
    public void CourseEndMismatchAndNonFiniteFixedCoordinatesAreDiagnosed()
    {
        var result = Solve(Project(new[] { Fixed(1, double.NaN), End(2, 9) },
            new[] { Line(1, 1, 2, recorded: D(1)) }, Block(new[] { C(1) }, N(1))));
        Has(result, DiagnosticCodes.CourseEndMismatch);
        Has(result, DiagnosticCodes.NonFiniteCoordinate);
        Assert.Empty(result.SolvedLines);
    }

    [Fact]
    public void EmptyBlockAllowsNullOriginAndNonemptyBlockRequiresValidOrigin()
    {
        var empty = Solve(Project(Array.Empty<DeedNode>(), Array.Empty<StraightCourse>(), Block(Array.Empty<CourseId>())));
        Assert.DoesNotContain(empty.Diagnostics, d => d.Code == DiagnosticCodes.InvalidBlockOrigin);
        var invalid = Solve(Project(new[] { Fixed(1), End(2, 1) },
            new[] { Line(1, 1, 2, recorded: D(1)) }, Block(new[] { C(1) })));
        Has(invalid, DiagnosticCodes.InvalidBlockOrigin);
    }

    [Fact]
    public void DisconnectedBoundaryAndGenuineCycleAreReported()
    {
        var disconnected = Solve(Project(new[] { Fixed(1), End(2, 1), Fixed(3), End(4, 2) },
            new[] { Line(1, 1, 2, recorded: D(1)), Line(2, 3, 4, recorded: D(1)) },
            Block(new[] { C(2), C(1) }, N(1))));
        Has(disconnected, DiagnosticCodes.DisconnectedBlock);
        var cycle = Solve(Project(new[] { End(1, 2), End(2, 1) },
            new[] { Line(1, 1, 2, recorded: D(1)), Line(2, 2, 1, recorded: D(1)) },
            Block(new[] { C(1), C(2) }, N(1))));
        Has(cycle, DiagnosticCodes.CircularDependency);
        Assert.Empty(cycle.SolvedLines);
    }

    [Fact]
    public void DuplicateMembershipAcrossBlocksIsReported()
    {
        var course = Line(1, 1, 2, recorded: D(1));
        var blocks = new Dictionary<BlockId, DraftingBlock>
        {
            [B(1)] = Block(new[] { C(1) }, N(1)),
            [B(2)] = new(B(2), "Second", T(1), null, 1, N(1), new[] { C(1) }, false)
        };
        var record = new DeedRecord(R(1), new[] { Fixed(1), End(2, 1) }.ToDictionary(n => n.Id),
            new Dictionary<CourseId, StraightCourse> { [C(1)] = course }, blocks);
        var project = new DeedProject(ProjectSettings.Default,
            new Dictionary<DraftingTypeId, DraftingType> { [T(1)] = new(T(1), "Boundary", DraftingCategory.Boundary) },
            new Dictionary<RecordId, DeedRecord> { [R(1)] = record });
        Has(Solve(project), DiagnosticCodes.DuplicateCourseMembership);
    }

    [Fact]
    public void FixedEndRemainsAuthoritativeWhenRecordedLineDiffers()
    {
        var result = Solve(Project(new[] { Fixed(1), Fixed(2, 0, 11) },
            new[] { Line(1, 1, 2, recorded: D(10)) }, Block(new[] { C(1) }, N(1))));
        Assert.Equal(11, result.NodeCoordinates[N(2)].Northing);
        Assert.Equal(10, result.SolvedLines[C(1)].End.Northing);
        Has(result, DiagnosticCodes.FixedEndMismatch);
    }

    [Fact]
    public void ModelsAndSolveResultsCannotBeMutatedThroughExposedCollections()
    {
        var sourceNodes = new Dictionary<NodeId, DeedNode> { [N(1)] = Fixed(1) };
        var ids = new List<CourseId> { C(1) };
        var block = Block(ids, N(1));
        var record = new DeedRecord(R(1), sourceNodes,
            new Dictionary<CourseId, StraightCourse> { [C(1)] = Line(1, 1, 2, recorded: D(1)) },
            new Dictionary<BlockId, DraftingBlock> { [B(1)] = block });
        sourceNodes.Clear(); ids.Clear();
        Assert.Single(record.Nodes);
        Assert.Single(block.Courses);
        var project = new DeedProject(ProjectSettings.Default,
            new Dictionary<DraftingTypeId, DraftingType> { [T(1)] = new(T(1), "Boundary", DraftingCategory.Boundary) },
            new Dictionary<RecordId, DeedRecord> { [R(1)] = record });
        var result = Solve(project);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<NodeId, Coordinate2D>)result.NodeCoordinates).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<CourseId, SolvedLine>)result.SolvedLines).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<Diagnostic>)result.Diagnostics).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CourseId>)block.Courses).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<RecordId, DeedRecord>)project.Records).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<DraftingTypeId, DraftingType>)project.DraftingTypes).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<NodeId, DeedNode>)record.Nodes).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<CourseId, StraightCourse>)record.Courses).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<BlockId, DraftingBlock>)record.DraftingBlocks).Clear());
    }
}
