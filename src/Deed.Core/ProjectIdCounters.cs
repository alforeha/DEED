namespace Deed.Core;

public enum ProjectIdKind { Record, Block, Course, Node, DraftingType }

public sealed record ProjectIdCounters(int Rec, int Blk, int C, int N, int Dt)
{
    public const int Maximum = 99999;
    public static ProjectIdCounters Empty { get; } = new(0, 0, 0, 0, 0);

    public int Get(ProjectIdKind kind) => kind switch
    {
        ProjectIdKind.Record => Rec,
        ProjectIdKind.Block => Blk,
        ProjectIdKind.Course => C,
        ProjectIdKind.Node => N,
        ProjectIdKind.DraftingType => Dt,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public DomainResult<ProjectIdAllocation> Allocate(ProjectIdKind kind)
    {
        int current = Get(kind);
        if (current < 0 || current >= Maximum)
            return DomainResult<ProjectIdAllocation>.Failure(DiagnosticCodes.IdSpaceExhausted,
                $"No IDs remain for {kind}.");
        int next = current + 1;
        string prefix = kind switch
        {
            ProjectIdKind.Record => "rec",
            ProjectIdKind.Block => "blk",
            ProjectIdKind.Course => "c",
            ProjectIdKind.Node => "n",
            ProjectIdKind.DraftingType => "dt",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        ProjectIdCounters updated = kind switch
        {
            ProjectIdKind.Record => this with { Rec = next },
            ProjectIdKind.Block => this with { Blk = next },
            ProjectIdKind.Course => this with { C = next },
            ProjectIdKind.Node => this with { N = next },
            ProjectIdKind.DraftingType => this with { Dt = next },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return DomainResult<ProjectIdAllocation>.Success(new ProjectIdAllocation(
            kind, $"{prefix}-{next:00000}", updated));
    }
}

public sealed record ProjectIdAllocation(ProjectIdKind Kind, string Value, ProjectIdCounters Counters)
{
    public RecordId RecordId => Kind == ProjectIdKind.Record
        ? RecordId.TryParse(Value).Value : throw new InvalidOperationException();
    public BlockId BlockId => Kind == ProjectIdKind.Block
        ? BlockId.TryParse(Value).Value : throw new InvalidOperationException();
    public CourseId CourseId => Kind == ProjectIdKind.Course
        ? CourseId.TryParse(Value).Value : throw new InvalidOperationException();
    public NodeId NodeId => Kind == ProjectIdKind.Node
        ? NodeId.TryParse(Value).Value : throw new InvalidOperationException();
    public DraftingTypeId DraftingTypeId => Kind == ProjectIdKind.DraftingType
        ? DraftingTypeId.TryParse(Value).Value : throw new InvalidOperationException();
}
