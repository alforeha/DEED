namespace Deed.Core.Tests;

public class ProjectContractTests
{
    private static RecordId R(int n) => RecordId.TryParse($"rec-{n:00000}").Value;
    private static BlockId B(int n) => BlockId.TryParse($"blk-{n:00000}").Value;
    private static CourseId C(int n) => CourseId.TryParse($"c-{n:00000}").Value;
    private static NodeId N(int n) => NodeId.TryParse($"n-{n:00000}").Value;
    private static DraftingTypeId T(int n) => DraftingTypeId.TryParse($"dt-{n:00000}").Value;

    [Theory]
    [InlineData(ProjectIdKind.Record, "rec-00001")]
    [InlineData(ProjectIdKind.Block, "blk-00001")]
    [InlineData(ProjectIdKind.Course, "c-00001")]
    [InlineData(ProjectIdKind.Node, "n-00001")]
    [InlineData(ProjectIdKind.DraftingType, "dt-00001")]
    public void AllocatesEveryTypedId(ProjectIdKind kind, string expected)
    {
        var result = ProjectIdCounters.Empty.Allocate(kind);
        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value.Value);
        Assert.Equal(1, result.Value.Counters.Get(kind));
        Assert.Equal(0, ProjectIdCounters.Empty.Get(kind));
        Assert.Equal(expected, kind switch
        {
            ProjectIdKind.Record => result.Value.RecordId.ToString(),
            ProjectIdKind.Block => result.Value.BlockId.ToString(),
            ProjectIdKind.Course => result.Value.CourseId.ToString(),
            ProjectIdKind.Node => result.Value.NodeId.ToString(),
            _ => result.Value.DraftingTypeId.ToString()
        });
        var exhausted = kind switch
        {
            ProjectIdKind.Record => ProjectIdCounters.Empty with { Rec = 99999 },
            ProjectIdKind.Block => ProjectIdCounters.Empty with { Blk = 99999 },
            ProjectIdKind.Course => ProjectIdCounters.Empty with { C = 99999 },
            ProjectIdKind.Node => ProjectIdCounters.Empty with { N = 99999 },
            _ => ProjectIdCounters.Empty with { Dt = 99999 }
        };
        Assert.Equal(DiagnosticCodes.IdSpaceExhausted,
            exhausted.Allocate(kind).Diagnostic?.Code);
    }

    [Theory]
    [InlineData(ProjectIdKind.Record, "rec-")]
    [InlineData(ProjectIdKind.Block, "blk-")]
    [InlineData(ProjectIdKind.Course, "c-")]
    [InlineData(ProjectIdKind.Node, "n-")]
    [InlineData(ProjectIdKind.DraftingType, "dt-")]
    public void TypedIdsRejectZeroAndAcceptBothIssuedBounds(ProjectIdKind kind, string prefix)
    {
        switch (kind)
        {
            case ProjectIdKind.Record: AssertIssuedBounds(RecordId.TryParse, prefix); break;
            case ProjectIdKind.Block: AssertIssuedBounds(BlockId.TryParse, prefix); break;
            case ProjectIdKind.Course: AssertIssuedBounds(CourseId.TryParse, prefix); break;
            case ProjectIdKind.Node: AssertIssuedBounds(NodeId.TryParse, prefix); break;
            case ProjectIdKind.DraftingType: AssertIssuedBounds(DraftingTypeId.TryParse, prefix); break;
        }
    }

    private static void AssertIssuedBounds<T>(Func<string?, DomainResult<T>> parse, string prefix)
    {
        Assert.Equal(DiagnosticCodes.InvalidId, parse(prefix + "00000").Diagnostic?.Code);
        Assert.True(parse(prefix + "00001").IsSuccess);
        Assert.True(parse(prefix + "99999").IsSuccess);
    }

    [Fact]
    public void MonumentRequiresDescriptionAndNonblankOptionalEvidence()
    {
        Assert.Equal(DiagnosticCodes.InvalidMonument,
            Monument.TryCreate(" ").Diagnostic?.Code);
        Assert.Equal(DiagnosticCodes.InvalidMonument,
            Monument.TryCreate("rod", "  ").Diagnostic?.Code);
        Assert.True(Monument.TryCreate("rod", null).IsSuccess);
        var monument = Monument.TryCreate("  capped rod  ", "  found  ").Value;
        Assert.Equal("  capped rod  ", monument.Description);
        Assert.Equal("  found  ", monument.Evidence);
    }

    [Fact]
    public void DeletedIdsAreNotReusedAndExhaustionIsDiagnostic()
    {
        var counters = ProjectIdCounters.Empty.Allocate(ProjectIdKind.Course).Value.Counters;
        Assert.Equal(C(2), counters.Allocate(ProjectIdKind.Course).Value.CourseId);
        var exhausted = counters with { C = 99999 };
        Assert.Equal(DiagnosticCodes.IdSpaceExhausted,
            exhausted.Allocate(ProjectIdKind.Course).Diagnostic?.Code);
    }

    [Fact]
    public void CounterBehindAnExistingIdIsNotRepaired()
    {
        var project = new DeedProject(ProjectSettings.Default, new Dictionary<DraftingTypeId, DraftingType>(),
            new Dictionary<RecordId, DeedRecord> { [R(2)] = EmptyRecord(R(2)) },
            ProjectIdCounters.Empty);
        Assert.Contains(ProjectValidator.Validate(project), d => d.Code == DiagnosticCodes.IdCounterBehind);
        Assert.Equal(0, project.IdCounters.Rec);
        Assert.Equal(DiagnosticCodes.IdCounterBehind,
            ProjectIdAllocator.Allocate(project, ProjectIdKind.Record).Diagnostic?.Code);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("course")]
    [InlineData("block")]
    public void EntityIdsCannotRepeatAcrossRecords(string type)
    {
        var records = new Dictionary<RecordId, DeedRecord>
        {
            [R(1)] = Record(R(1), type),
            [R(2)] = Record(R(2), type)
        };
        var project = new DeedProject(ProjectSettings.Default,
            new Dictionary<DraftingTypeId, DraftingType> { [T(1)] = new(T(1), "Boundary", DraftingCategory.Boundary) },
            records, new(2, 1, 1, 2, 1));
        Assert.Contains(ProjectValidator.Validate(project), d =>
            d.Code == DiagnosticCodes.DuplicateProjectId && d.EntityType == type);
    }

    [Fact]
    public void RecordDictionaryKeyMustMatchRecordId()
    {
        var project = new DeedProject(ProjectSettings.Default,
            new Dictionary<DraftingTypeId, DraftingType>(),
            new Dictionary<RecordId, DeedRecord> { [R(1)] = EmptyRecord(R(2)) },
            new(2, 0, 0, 0, 0));
        Assert.Contains(ProjectValidator.Validate(project), d =>
            d.Code == DiagnosticCodes.IdMismatch && d.EntityType == "record");
    }

    [Fact]
    public void ProjectAllocatorUsesStoredCounterAndReturnsUpdatedCounters()
    {
        var project = new DeedProject(ProjectSettings.Default,
            new Dictionary<DraftingTypeId, DraftingType>(),
            new Dictionary<RecordId, DeedRecord>(), new(7, 4, 9, 12, 2));
        var allocation = ProjectIdAllocator.Allocate(project, ProjectIdKind.Node);
        Assert.True(allocation.IsSuccess);
        Assert.Equal(N(13), allocation.Value.NodeId);
        Assert.Equal(13, allocation.Value.Counters.N);
        Assert.Equal(12, project.IdCounters.N);
    }

    [Fact]
    public void DuplicateDraftingTypeEntityIdsAreDiagnosed()
    {
        var project = new DeedProject(ProjectSettings.Default,
            new Dictionary<DraftingTypeId, DraftingType>
            {
                [T(1)] = new(T(1), "A", DraftingCategory.Other),
                [T(2)] = new(T(1), "B", DraftingCategory.Other)
            }, new Dictionary<RecordId, DeedRecord>(), new(0, 0, 0, 0, 2));
        Assert.Contains(ProjectValidator.Validate(project), d =>
            d.Code == DiagnosticCodes.DuplicateProjectId && d.EntityType == "draftingType");
    }

    private static DeedRecord EmptyRecord(RecordId id) => new(id,
        new Dictionary<NodeId, DeedNode>(), new Dictionary<CourseId, StraightCourse>(),
        new Dictionary<BlockId, DraftingBlock>());

    private static DeedRecord Record(RecordId id, string repeated)
    {
        var nodes = new Dictionary<NodeId, DeedNode>();
        var courses = new Dictionary<CourseId, StraightCourse>();
        var blocks = new Dictionary<BlockId, DraftingBlock>();
        if (repeated is "node" or "course")
        {
            nodes[N(1)] = new(N(1), "pob", null, new FixedNodeDefinition(new(0, 0)));
            nodes[N(2)] = new(N(2), "corner", null, new CourseEndNodeDefinition(C(1)));
        }
        if (repeated == "course")
        {
            courses[C(1)] = new(C(1), T(1), N(1), N(2), null,
                Array.Empty<AlongPointPlacement>(), null, "N 1", BearingParser.Parse("N").Value,
                Distance.TryCreate(1).Value, null);
            blocks[B(id == R(1) ? 1 : 2)] = new(B(id == R(1) ? 1 : 2), "Boundary", T(1),
                null, 0, N(1), new[] { C(1) }, false);
        }
        if (repeated == "block")
            blocks[B(1)] = new(B(1), "Empty", T(1), null, 0, null, Array.Empty<CourseId>(), false);
        return new(id, nodes, courses, blocks);
    }
}
