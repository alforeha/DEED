using System.Collections.ObjectModel;

namespace Deed.Core;

public enum DraftingCategory { Boundary, Other }

public sealed record DraftingType(DraftingTypeId Id, string Name, DraftingCategory Category);

public abstract record NodeDefinition;
public sealed record FixedNodeDefinition(Coordinate2D Coordinate) : NodeDefinition;
public sealed record CourseEndNodeDefinition(CourseId ProducingCourseId) : NodeDefinition;
public sealed record AlongCourseNodeDefinition(CourseId HostCourseId) : NodeDefinition;

public sealed record DeedNode(NodeId Id, string Role, Monument? Monument,
    NodeDefinition Definition);

public sealed record DraftedDistanceCompletion(Distance Distance, string Reason);

public enum DrivingRealization { Recorded }

public readonly record struct AlongPointPlacement(NodeId NodeId, Distance FromPrevious);

public sealed record StraightCourse
{
    public StraightCourse(CourseId id, DraftingTypeId draftingTypeId,
        NodeId fromNodeId, NodeId toNodeId, CourseId? parentCourseId,
        IEnumerable<AlongPointPlacement> alongPoints, Distance? finalPart,
        string originalRecordedText, ParsedBearing? parsedBearing,
        Distance? recordedDistance, DraftedDistanceCompletion? draftedCompletion)
    {
        Id = id;
        DraftingTypeId = draftingTypeId;
        FromNodeId = fromNodeId;
        ToNodeId = toNodeId;
        ParentCourseId = parentCourseId;
        AlongPoints = Array.AsReadOnly(alongPoints.ToArray());
        FinalPart = finalPart;
        OriginalRecordedText = originalRecordedText;
        ParsedBearing = parsedBearing;
        RecordedDistance = recordedDistance;
        DraftedCompletion = draftedCompletion;
    }

    public CourseId Id { get; init; }
    public DraftingTypeId DraftingTypeId { get; init; }
    public NodeId FromNodeId { get; init; }
    public NodeId ToNodeId { get; init; }
    public CourseId? ParentCourseId { get; init; }
    public IReadOnlyList<AlongPointPlacement> AlongPoints { get; }
    public Distance? FinalPart { get; init; }
    public string OriginalRecordedText { get; init; }
    public ParsedBearing? ParsedBearing { get; init; }
    public Distance? RecordedDistance { get; init; }
    public DraftedDistanceCompletion? DraftedCompletion { get; init; }
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
