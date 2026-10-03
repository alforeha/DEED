using System.Collections.ObjectModel;

namespace Deed.Core;

public enum DraftingCategory { Boundary, Other }

public sealed record DraftingType(DraftingTypeId Id, string Name, DraftingCategory Category);

public abstract record NodeDefinition;
public sealed record FixedNodeDefinition(Coordinate2D Coordinate) : NodeDefinition;
public sealed record CourseEndNodeDefinition(CourseId ProducingCourseId) : NodeDefinition;

public sealed record DeedNode(NodeId Id, string Role, Monument? Monument,
    NodeDefinition Definition);

public sealed record DraftedDistanceCompletion(Distance Distance, string Reason);

public enum DrivingRealization { Recorded }

public sealed record StraightCourse(CourseId Id, DraftingTypeId DraftingTypeId,
    NodeId FromNodeId, NodeId ToNodeId, string OriginalRecordedText,
    ParsedBearing? ParsedBearing, Distance? RecordedDistance, DraftedDistanceCompletion? DraftedCompletion)
{
    public Bearing? Bearing => ParsedBearing?.Bearing;
    public DrivingRealization DrivingRealization => DrivingRealization.Recorded;
}

public sealed class DraftingBlock
{
    public DraftingBlock(BlockId id, string name, DraftingTypeId draftingTypeId,
        BlockId? parentBlockId, int viewOrder, NodeId? origin,
        IEnumerable<CourseId> courses, bool reportClose)
    {
        Id = id;
        Name = name;
        DraftingTypeId = draftingTypeId;
        ParentBlockId = parentBlockId;
        ViewOrder = viewOrder;
        Origin = origin;
        Courses = Array.AsReadOnly(courses.ToArray());
        ReportClose = reportClose;
    }

    public BlockId Id { get; }
    public string Name { get; }
    public DraftingTypeId DraftingTypeId { get; }
    public BlockId? ParentBlockId { get; }
    public int ViewOrder { get; }
    public NodeId? Origin { get; }
    public IReadOnlyList<CourseId> Courses { get; }
    public bool ReportClose { get; }
}

public sealed class DeedRecord
{
    public DeedRecord(RecordId id, IDictionary<NodeId, DeedNode> nodes,
        IDictionary<CourseId, StraightCourse> courses,
        IDictionary<BlockId, DraftingBlock> draftingBlocks)
    {
        Id = id;
        Nodes = new ReadOnlyDictionary<NodeId, DeedNode>(new Dictionary<NodeId, DeedNode>(nodes));
        Courses = new ReadOnlyDictionary<CourseId, StraightCourse>(new Dictionary<CourseId, StraightCourse>(courses));
        DraftingBlocks = new ReadOnlyDictionary<BlockId, DraftingBlock>(new Dictionary<BlockId, DraftingBlock>(draftingBlocks));
    }

    public RecordId Id { get; }
    public IReadOnlyDictionary<NodeId, DeedNode> Nodes { get; }
    public IReadOnlyDictionary<CourseId, StraightCourse> Courses { get; }
    public IReadOnlyDictionary<BlockId, DraftingBlock> DraftingBlocks { get; }
}

public sealed class DeedProject
{
    public DeedProject(ProjectSettings settings, IDictionary<DraftingTypeId, DraftingType> draftingTypes,
        IDictionary<RecordId, DeedRecord> records, ProjectIdCounters? idCounters = null)
    {
        Settings = settings;
        DraftingTypes = new ReadOnlyDictionary<DraftingTypeId, DraftingType>(
            new Dictionary<DraftingTypeId, DraftingType>(draftingTypes));
        Records = new ReadOnlyDictionary<RecordId, DeedRecord>(new Dictionary<RecordId, DeedRecord>(records));
        IdCounters = idCounters ?? ProjectIdCounters.Empty;
    }

    public ProjectSettings Settings { get; }
    public IReadOnlyDictionary<DraftingTypeId, DraftingType> DraftingTypes { get; }
    public IReadOnlyDictionary<RecordId, DeedRecord> Records { get; }
    public ProjectIdCounters IdCounters { get; }
}
