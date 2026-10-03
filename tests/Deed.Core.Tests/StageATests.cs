namespace Deed.Core.Tests;

public class StageATests
{
    private static RecordId R(int n) => RecordId.TryParse($"rec-{n:00000}").Value;
    private static BlockId B(int n) => BlockId.TryParse($"blk-{n:00000}").Value;
    private static CourseId C(int n) => CourseId.TryParse($"c-{n:00000}").Value;
    private static NodeId N(int n) => NodeId.TryParse($"n-{n:00000}").Value;
    private static DraftingTypeId T(int n) => DraftingTypeId.TryParse($"dt-{n:00000}").Value;
    private static Distance D(double value) => Distance.TryCreate(value).Value;
    private static ParsedBearing Bearing(string text) => BearingParser.Parse(text).Value;
    private static DeedNode Fixed(int n, double x = 0, double y = 0) =>
        new(N(n), "point", null, new FixedNodeDefinition(new(x, y)));
    private static DeedNode End(int n, int course) =>
        new(N(n), "point", null, new CourseEndNodeDefinition(C(course)));
    private static StraightCourse Line(int n, int from, int to, string bearing = "N",
        Distance? recorded = null, DraftedDistanceCompletion? drafted = null,
        DraftingTypeId? type = null, CourseId? parent = null,
        IEnumerable<AlongPointPlacement>? along = null, Distance? finalPart = null) =>
        new(C(n), type ?? T(1), N(from), N(to), parent ?? (n > 1 ? C(n - 1) : null),
            along ?? Array.Empty<AlongPointPlacement>(), finalPart, $"  original {n}  ",
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
        Has(disconnected, DiagnosticCodes.ParentAttachmentMismatch);
        var cycle = Solve(Project(new[] { End(1, 2), End(2, 1) },
            new[] { Line(1, 1, 2, recorded: D(1), parent: C(2)),
                Line(2, 2, 1, recorded: D(1)) },
            Block(new[] { C(1), C(2) }, N(1))));
        Has(cycle, DiagnosticCodes.ParentCycle);
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
        Assert.Equal(10, result.SolvedLines[C(1)].ComputedRecordedEnd.Northing);
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

    [Fact]
    public void BranchesSolveFromAlongEndAndStartRegardlessOfStoredOrder()
    {
        var placements = new List<AlongPointPlacement>
        {
            new(N(3), D(20)), new(N(4), D(30))
        };
        var parent = Line(1, 1, 2, recorded: D(40), along: placements);
        placements.Clear();
        Assert.Equal(2, parent.AlongPoints.Count);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<AlongPointPlacement>)parent.AlongPoints).Clear());
        var courses = new[]
        {
            Line(4, 1, 7, "E", D(5), parent: C(1)),
            Line(3, 2, 6, "E", D(5), parent: C(1)),
            Line(2, 4, 5, "E", D(5), parent: C(1)), parent
        };
        var nodes = new[] { Fixed(1), End(2, 1),
            new DeedNode(N(3), "witness", null, new AlongCourseNodeDefinition(C(1))),
            new DeedNode(N(4), "witness", null, new AlongCourseNodeDefinition(C(1))),
            End(5, 2), End(6, 3), End(7, 4) };
        var project = Project(nodes, courses, Block(new[] { C(4), C(2), C(1), C(3) }, N(1)));
        var result = Solve(project);
        Assert.All(result.Diagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.Equal(20, result.NodeCoordinates[N(3)].Northing, 9);
        Assert.Equal(50, result.NodeCoordinates[N(4)].Northing, 9);
        Assert.Equal(5, result.NodeCoordinates[N(5)].Easting, 9);
        Assert.Equal(50, result.NodeCoordinates[N(5)].Northing, 9);
        Assert.Equal(40, result.NodeCoordinates[N(6)].Northing, 9);
        Assert.Equal(0, result.NodeCoordinates[N(7)].Northing, 9);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.AlongPointBeyondCourse);
    }

    [Fact]
    public void PartSummaryReportsSignedHundredthAndDoesNotMoveEndpoint()
    {
        var placements = new[] { new AlongPointPlacement(N(3), D(86.25)),
            new AlongPointPlacement(N(4), D(50)), new AlongPointPlacement(N(5), D(50)) };
        var course = Line(1, 1, 2, recorded: D(250), along: placements, finalPart: D(63.74));
        var nodes = new[] { Fixed(1), End(2, 1),
            new DeedNode(N(3), "a", null, new AlongCourseNodeDefinition(C(1))),
            new DeedNode(N(4), "b", null, new AlongCourseNodeDefinition(C(1))),
            new DeedNode(N(5), "c", null, new AlongCourseNodeDefinition(C(1))) };
        var result = Solve(Project(nodes, new[] { course }, Block(new[] { C(1) }, N(1))));
        var summary = result.PartSummaries[C(1)];
        Assert.Equal(186.25, summary.EnteredAlongParts);
        Assert.Equal(63.74, summary.FinalPart);
        Assert.Equal(249.99, summary.EnteredTotal);
        Assert.Equal(0.01, summary.SignedDiscrepancy);
        Assert.Null(summary.UnenteredRemainder);
        Assert.Equal(250, result.NodeCoordinates[N(2)].Northing);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.PartLengthDiscrepancy &&
            d.Severity == DiagnosticSeverity.Warning);

        var noFinal = Line(1, 1, 2, recorded: D(250), along: placements);
        var remainder = Solve(Project(nodes, new[] { noFinal }, Block(new[] { C(1) }, N(1))))
            .PartSummaries[C(1)];
        Assert.Equal(63.75, remainder.UnenteredRemainder);
        Assert.Null(remainder.SignedDiscrepancy);

        var exact = Line(1, 1, 2, recorded: D(250), along: placements, finalPart: D(63.75));
        var exactResult = Solve(Project(nodes, new[] { exact }, Block(new[] { C(1) }, N(1))));
        Assert.Equal(0, exactResult.PartSummaries[C(1)].SignedDiscrepancy);
        Assert.DoesNotContain(exactResult.Diagnostics,
            d => d.Code == DiagnosticCodes.PartLengthDiscrepancy);
    }

    [Fact]
    public void DraftedLengthNeverBecomesRecordedOverallForPartSummary()
    {
        var nodes = new[] { Fixed(1), End(2, 1),
            new DeedNode(N(3), "along", null, new AlongCourseNodeDefinition(C(1))) };
        var course = Line(1, 1, 2, drafted: new DraftedDistanceCompletion(D(100), "sketch"),
            along: new[] { new AlongPointPlacement(N(3), D(30)) }, finalPart: D(70));
        var result = Solve(Project(nodes, new[] { course }, Block(new[] { C(1) }, N(1))));
        var summary = result.PartSummaries[C(1)];
        Assert.Equal(30, summary.EnteredAlongParts);
        Assert.Equal(70, summary.FinalPart);
        Assert.Equal(100, summary.EnteredTotal);
        Assert.Null(summary.UnenteredRemainder);
        Assert.Null(summary.SignedDiscrepancy);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.PartLengthDiscrepancy);
        Assert.Equal(100, result.NodeCoordinates[N(2)].Northing, 9);
    }

    [Fact]
    public void IncompleteParentStillAllowsChildFromAvailableStart()
    {
        var courses = new[] { Line(1, 1, 2), Line(2, 1, 3, "E", D(7), parent: C(1)) };
        var result = Solve(Project(new[] { Fixed(1), End(2, 1), End(3, 2) }, courses,
            Block(new[] { C(2), C(1) }, N(1))));
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.IncompleteCourse);
        Assert.False(result.SolvedLines.ContainsKey(C(1)));
        Assert.Equal(7, result.NodeCoordinates[N(3)].Easting, 9);
    }

    [Fact]
    public void AlongPointCanResolveWithoutParentEndpointDistance()
    {
        var courses = new[]
        {
            Line(1, 1, 2, along: new[] { new AlongPointPlacement(N(3), D(8)) }),
            Line(2, 3, 4, "E", D(5), parent: C(1))
        };
        var nodes = new[] { Fixed(1), End(2, 1),
            new DeedNode(N(3), "along", null, new AlongCourseNodeDefinition(C(1))), End(4, 2) };
        var result = Solve(Project(nodes, courses, Block(new[] { C(2), C(1) }, N(1))));
        Has(result, DiagnosticCodes.IncompleteCourse);
        Assert.Equal(8, result.NodeCoordinates[N(3)].Northing, 9);
        Assert.Equal(5, result.NodeCoordinates[N(4)].Easting, 9);
        Assert.Equal(8, result.NodeCoordinates[N(4)].Northing, 9);
    }

    [Fact]
    public void AlongPlacementAndParentErrorsHaveDistinctStructuralCodes()
    {
        var nodes = new[] { Fixed(1), End(2, 1), End(3, 2),
            new DeedNode(N(4), "along", null, new AlongCourseNodeDefinition(C(1))),
            new DeedNode(N(5), "along", null, new AlongCourseNodeDefinition(C(2))),
            new DeedNode(N(6), "along", null, new AlongCourseNodeDefinition(C(1))) };
        var courses = new[]
        {
            Line(1, 1, 2, recorded: D(10), along: new[]
            {
                new AlongPointPlacement(N(4), D(2)),
                new AlongPointPlacement(N(4), D(3)),
                new AlongPointPlacement(N(5), D(2))
            }),
            Line(2, 1, 3, recorded: D(2), parent: C(9))
        };
        var result = Solve(Project(nodes, courses, Block(new[] { C(1), C(2) }, N(1))));
        Has(result, DiagnosticCodes.DuplicateAlongPlacement);
        Has(result, DiagnosticCodes.AlongHostMismatch);
        Has(result, DiagnosticCodes.InvalidParent);
        Has(result, DiagnosticCodes.MissingAlongPlacement);
    }

    [Fact]
    public void ParentInOtherBlockAndMultipleRootsAreRejected()
    {
        var courses = new[] { Line(1, 1, 2, recorded: D(2)),
            Line(2, 2, 3, recorded: D(2), parent: C(1)) };
        var blocks = new Dictionary<BlockId, DraftingBlock>
        {
            [B(1)] = Block(new[] { C(1) }, N(1)),
            [B(2)] = new(B(2), "Second", T(1), null, 1, N(2), new[] { C(2) }, false)
        };
        var record = new DeedRecord(R(1), new[] { Fixed(1), End(2, 1), End(3, 2) }
            .ToDictionary(n => n.Id), courses.ToDictionary(c => c.Id), blocks);
        var project = new DeedProject(ProjectSettings.Default,
            new Dictionary<DraftingTypeId, DraftingType>
            { [T(1)] = new(T(1), "Boundary", DraftingCategory.Boundary) },
            new Dictionary<RecordId, DeedRecord> { [R(1)] = record });
        Has(Solve(project), DiagnosticCodes.ParentOutsideBlock);
        Has(Solve(project), DiagnosticCodes.InvalidRootCount);
    }

    [Fact]
    public void SiblingsMayShareTheSameParentEndpointInEitherCourseOrder()
    {
        var root = Line(1, 1, 2, recorded: D(10));
        var east = Line(2, 2, 3, "E", D(5), parent: C(1));
        var west = Line(3, 2, 4, "W", D(5), parent: C(1));
        var nodes = new[] { Fixed(1), End(2, 1), End(3, 2), End(4, 3) };
        var first = Solve(Project(nodes, new[] { west, east, root },
            Block(new[] { C(3), C(1), C(2) }, N(1))));
        var second = Solve(Project(nodes, new[] { root, east, west },
            Block(new[] { C(2), C(3), C(1) }, N(1))));
        Assert.Empty(first.Diagnostics);
        Assert.Empty(second.Diagnostics);
        foreach (var id in nodes.Select(n => n.Id))
            Assert.Equal(first.NodeCoordinates[id], second.NodeCoordinates[id]);
        Assert.Equal(5, first.NodeCoordinates[N(3)].Easting, 9);
        Assert.Equal(10, first.NodeCoordinates[N(3)].Northing, 9);
        Assert.Equal(-5, first.NodeCoordinates[N(4)].Easting, 9);
        Assert.Equal(10, first.NodeCoordinates[N(4)].Northing, 9);
    }

    [Fact]
    public void MissingParentIsReportedDirectly()
    {
        var project = Project(new[] { Fixed(1), End(2, 1), End(3, 2) },
            new[] { Line(1, 1, 2, recorded: D(5)),
                Line(2, 2, 3, recorded: D(5), parent: C(9)) },
            Block(new[] { C(1), C(2) }, N(1)));
        Has(Solve(project), DiagnosticCodes.InvalidParent);
    }

    [Fact]
    public void CrossBlockParentIsReportedDirectly()
    {
        var courses = new[] { Line(1, 1, 2, recorded: D(5)),
            Line(2, 3, 4, recorded: D(5)) with { ParentCourseId = null },
            Line(3, 2, 5, "E", D(5), parent: C(1)) };
        var blocks = new Dictionary<BlockId, DraftingBlock>
        {
            [B(1)] = Block(new[] { C(1) }, N(1)),
            [B(2)] = new(B(2), "Second", T(1), null, 1, N(3),
                new[] { C(3), C(2) }, false)
        };
        var record = new DeedRecord(R(1),
            new[] { Fixed(1), End(2, 1), Fixed(3, 20, 0), End(4, 2), End(5, 3) }
                .ToDictionary(n => n.Id), courses.ToDictionary(c => c.Id), blocks);
        var project = new DeedProject(ProjectSettings.Default,
            new Dictionary<DraftingTypeId, DraftingType>
            { [T(1)] = new(T(1), "Boundary", DraftingCategory.Boundary) },
            new Dictionary<RecordId, DeedRecord> { [R(1)] = record });
        var result = Solve(project);
        Has(result, DiagnosticCodes.ParentOutsideBlock);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.InvalidRootCount);
    }

    [Fact]
    public void ParentAttachmentMismatchIsReportedDirectly()
    {
        var result = Solve(Project(new[] { Fixed(1), End(2, 1), Fixed(3, 20, 0), End(4, 2) },
            new[] { Line(1, 1, 2, recorded: D(5)),
                Line(2, 3, 4, recorded: D(5), parent: C(1)) },
            Block(new[] { C(1), C(2) }, N(1))));
        Has(result, DiagnosticCodes.ParentAttachmentMismatch);
    }

    [Fact]
    public void ZeroAndMultipleRootsAreReportedSeparately()
    {
        var zero = Solve(Project(new[] { Fixed(1), End(2, 1) },
            new[] { Line(1, 1, 2, recorded: D(5), parent: C(9)) },
            Block(new[] { C(1) }, N(1))));
        Has(zero, DiagnosticCodes.InvalidRootCount);
        var multiple = Solve(Project(new[] { Fixed(1), End(2, 1), Fixed(3, 20, 0), End(4, 2) },
            new[] { Line(1, 1, 2, recorded: D(5)),
                Line(2, 3, 4, recorded: D(5)) with { ParentCourseId = null } },
            Block(new[] { C(1), C(2) }, N(1))));
        Has(multiple, DiagnosticCodes.InvalidRootCount);
    }

    [Fact]
    public void ParentCycleHasStructuralDiagnostic()
    {
        var result = Solve(Project(new[] { End(1, 2), End(2, 1) },
            new[] { Line(1, 1, 2, recorded: D(5), parent: C(2)),
                Line(2, 2, 1, recorded: D(5), parent: C(1)) },
            Block(new[] { C(1), C(2) }, N(1))));
        Has(result, DiagnosticCodes.ParentCycle);
    }

    [Fact]
    public void StageARootMustStartAtFixedNode()
    {
        var result = Solve(Project(new[] { End(1, 2), End(2, 1) },
            new[] { Line(1, 1, 2, recorded: D(5)) },
            Block(new[] { C(1) }, N(1))));
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.InvalidBlockOrigin &&
            d.Message.Contains("fixed node", StringComparison.Ordinal));
        var alongRoot = Solve(Project(new[]
        {
            new DeedNode(N(1), "pob", null, new AlongCourseNodeDefinition(C(1))), End(2, 1)
        }, new[] { Line(1, 1, 2, recorded: D(5),
            along: new[] { new AlongPointPlacement(N(1), D(0)) }) },
            Block(new[] { C(1) }, N(1))));
        Assert.Contains(alongRoot.Diagnostics, d => d.Code == DiagnosticCodes.InvalidBlockOrigin &&
            d.Message.Contains("fixed node", StringComparison.Ordinal));
    }

    [Fact]
    public void EndingAtExistingPointsDoesNotChangeExplicitParentage()
    {
        var rootToFixed = Line(1, 1, 2, recorded: D(10));
        var childToFixed = Line(2, 1, 2, recorded: D(10), parent: C(1));
        var fixedResult = Solve(Project(new[] { Fixed(1), Fixed(2, 0, 10) },
            new[] { childToFixed, rootToFixed }, Block(new[] { C(2), C(1) }, N(1))));
        Assert.Empty(fixedResult.Diagnostics);
        Assert.Equal(C(1), childToFixed.ParentCourseId);

        var rootToOwned = Line(1, 1, 2, recorded: D(10));
        var childToOwned = Line(2, 1, 2, recorded: D(10), parent: C(1));
        var ownedResult = Solve(Project(new[] { Fixed(1), End(2, 1) },
            new[] { childToOwned, rootToOwned }, Block(new[] { C(2), C(1) }, N(1))));
        Assert.Empty(ownedResult.Diagnostics);
        Assert.Equal(C(1), childToOwned.ParentCourseId);
        Assert.Equal(2, ownedResult.SolvedLines.Count);
    }
}
