namespace Deed.Core;

public static class AuthoringDiagnosticCodes
{
    public const string InvalidProject = "authoring.invalid_project";
    public const string InvalidInput = "authoring.invalid_input";
    public const string InvalidBearing = "authoring.invalid_bearing";
    public const string InvalidAttachment = "authoring.invalid_attachment";
    public const string MissingEntity = "authoring.missing_entity";
    public const string IdAllocation = "authoring.id_allocation";
}

public sealed record CourseInput(string RecordedCall, string BearingText, Distance? RecordedDistance,
    DraftedDistanceCompletion? DraftedCompletion, Distance? FinalPart, string EndpointRole,
    Monument? EndpointMonument);

public static class ProjectAuthoring
{
    public static DeedProject CreateStageAProject()
    {
        var typeId = DraftingTypeId.TryParse("dt-00001").Value;
        var recordId = RecordId.TryParse("rec-00001").Value;
        var blockId = BlockId.TryParse("blk-00001").Value;
        return new(ProjectSettings.Default,
            new Dictionary<DraftingTypeId, DraftingType> { [typeId] = new(typeId, "Boundary", DraftingCategory.Boundary) },
            new Dictionary<RecordId, DeedRecord> { [recordId] = new(recordId,
                new Dictionary<NodeId, DeedNode>(), new Dictionary<CourseId, StraightCourse>(),
                new Dictionary<BlockId, DraftingBlock> { [blockId] = new(blockId, "Boundary", typeId,
                    null, 0, null, Array.Empty<CourseId>(), false) }) },
            new ProjectIdCounters(1, 1, 0, 0, 1));
    }

    public static ProjectEditResult AddRoot(DeedProject project, RecordId recordId, BlockId blockId,
        Coordinate2D pob, string pobRole, Monument? pobMonument, CourseInput input,
        DraftingTypeId draftingTypeId)
    {
        if (!TryBlock(project, recordId, blockId, out var record, out var block, out var failure)) return failure!;
        if (block!.Courses.Count != 0 || block.Origin is not null)
            return Fail(project, AuthoringDiagnosticCodes.InvalidAttachment, "Block already has a root course.");
        if (!double.IsFinite(pob.Easting) || !double.IsFinite(pob.Northing) || string.IsNullOrWhiteSpace(pobRole))
            return Fail(project, AuthoringDiagnosticCodes.InvalidInput, "POB coordinate and role are required.");
        if (!TryInput(project, input, draftingTypeId, out var bearing, out failure)) return failure!;
        var counters = project.IdCounters;
        if (!Allocate(ref counters, ProjectIdKind.Node, out var origin, out failure, project) ||
            !Allocate(ref counters, ProjectIdKind.Node, out var endpoint, out failure, project) ||
            !Allocate(ref counters, ProjectIdKind.Course, out var course, out failure, project)) return failure!;
        var originId = origin.NodeId;
        var endpointId = endpoint.NodeId;
        var courseId = course.CourseId;
        var nodes = new Dictionary<NodeId, DeedNode>(record!.Nodes)
        {
            [originId] = new(originId, pobRole, pobMonument, new FixedNodeDefinition(pob)),
            [endpointId] = new(endpointId, input.EndpointRole, input.EndpointMonument,
                new CourseEndNodeDefinition(courseId))
        };
        var courses = new Dictionary<CourseId, StraightCourse>(record.Courses)
        {
            [courseId] = MakeCourse(courseId, draftingTypeId, originId, endpointId, null, input, bearing)
        };
        var blocks = new Dictionary<BlockId, DraftingBlock>(record.DraftingBlocks)
        {
            [blockId] = new(blockId, block.Name, block.DraftingTypeId, block.ParentBlockId,
                block.ViewOrder, originId, new[] { courseId }, block.ReportClose)
        };
        return Finish(project, recordId, nodes, courses, blocks, counters);
    }

    public static ProjectEditResult AddChild(DeedProject project, RecordId recordId, BlockId blockId,
        CourseId parentCourseId, NodeId fromNodeId, NodeId? existingEndNodeId,
        CourseInput input, DraftingTypeId draftingTypeId)
    {
        if (!TryBlock(project, recordId, blockId, out var record, out var block, out var failure)) return failure!;
        if (!record!.Courses.TryGetValue(parentCourseId, out var parent) || !block!.Courses.Contains(parentCourseId))
            return Fail(project, AuthoringDiagnosticCodes.MissingEntity, "Parent course is not in this block.", "course", parentCourseId.ToString());
        if (fromNodeId != parent.FromNodeId && fromNodeId != parent.ToNodeId &&
            !parent.AlongPoints.Any(x => x.NodeId == fromNodeId))
            return Fail(project, AuthoringDiagnosticCodes.InvalidAttachment, "Start must be a point on the selected parent.", "node", fromNodeId.ToString());
        if (existingEndNodeId is { } existing && (!record.Nodes.TryGetValue(existing, out var end) ||
            end.Definition is not (FixedNodeDefinition or CourseEndNodeDefinition)))
            return Fail(project, AuthoringDiagnosticCodes.InvalidAttachment, "Existing endpoint must be fixed or a course endpoint.", "node", existing.ToString());
        if (!TryInput(project, input, draftingTypeId, out var bearing, out failure)) return failure!;
        var counters = project.IdCounters;
        if (!Allocate(ref counters, ProjectIdKind.Course, out var course, out failure, project)) return failure!;
        NodeId toNodeId;
        var nodes = new Dictionary<NodeId, DeedNode>(record.Nodes);
        if (existingEndNodeId is { } selected) toNodeId = selected;
        else
        {
            if (!Allocate(ref counters, ProjectIdKind.Node, out var endpoint, out failure, project)) return failure!;
            toNodeId = endpoint.NodeId;
            nodes[toNodeId] = new(toNodeId, input.EndpointRole, input.EndpointMonument,
                new CourseEndNodeDefinition(course.CourseId));
        }
        var courses = new Dictionary<CourseId, StraightCourse>(record.Courses)
        {
            [course.CourseId] = MakeCourse(course.CourseId, draftingTypeId, fromNodeId, toNodeId,
                parentCourseId, input, bearing)
        };
        var blocks = new Dictionary<BlockId, DraftingBlock>(record.DraftingBlocks)
        {
            [blockId] = new(block.Id, block.Name, block.DraftingTypeId, block.ParentBlockId,
                block.ViewOrder, block.Origin, block.Courses.Append(course.CourseId), block.ReportClose)
        };
        return Finish(project, recordId, nodes, courses, blocks, counters);
    }

    public static ProjectEditResult AddAlongPoint(DeedProject project, RecordId recordId, BlockId blockId,
        CourseId hostCourseId, int insertionIndex, Distance fromPrevious, string role, Monument? monument)
    {
        if (!TryBlock(project, recordId, blockId, out var record, out var block, out var failure)) return failure!;
        if (!record!.Courses.TryGetValue(hostCourseId, out var host) || !block!.Courses.Contains(hostCourseId))
            return Fail(project, AuthoringDiagnosticCodes.MissingEntity, "Host course is not in this block.", "course", hostCourseId.ToString());
        if (insertionIndex < 0 || insertionIndex > host.AlongPoints.Count ||
            !double.IsFinite(fromPrevious.Value) || fromPrevious.Value < 0 || string.IsNullOrWhiteSpace(role))
            return Fail(project, AuthoringDiagnosticCodes.InvalidInput, "Along-point position, distance, or role is invalid.");
        var counters = project.IdCounters;
        if (!Allocate(ref counters, ProjectIdKind.Node, out var node, out failure, project)) return failure!;
        var nodes = new Dictionary<NodeId, DeedNode>(record.Nodes)
        {
            [node.NodeId] = new(node.NodeId, role, monument, new AlongCourseNodeDefinition(hostCourseId))
        };
        var placements = host.AlongPoints.ToList();
        placements.Insert(insertionIndex, new(node.NodeId, fromPrevious));
        var courses = new Dictionary<CourseId, StraightCourse>(record.Courses)
        {
            [hostCourseId] = new(host.Id, host.DraftingTypeId, host.FromNodeId, host.ToNodeId,
                host.ParentCourseId, placements, host.FinalPart, host.OriginalRecordedText,
                host.ParsedBearing, host.RecordedDistance, host.DraftedCompletion)
        };
        return Finish(project, recordId, nodes, courses,
            new Dictionary<BlockId, DraftingBlock>(record.DraftingBlocks), counters);
    }

    private static StraightCourse MakeCourse(CourseId id, DraftingTypeId type, NodeId from, NodeId to,
        CourseId? parent, CourseInput input, ParsedBearing bearing) =>
        new(id, type, from, to, parent, Array.Empty<AlongPointPlacement>(), input.FinalPart,
            input.RecordedCall, bearing, input.RecordedDistance, input.DraftedCompletion);

    private static bool TryInput(DeedProject project, CourseInput input, DraftingTypeId type,
        out ParsedBearing bearing, out ProjectEditResult? failure)
    {
        bearing = default!;
        failure = null;
        if (!project.DraftingTypes.TryGetValue(type, out var draftingType) ||
            draftingType.Category != DraftingCategory.Boundary)
            failure = Fail(project, AuthoringDiagnosticCodes.InvalidInput, "Boundary drafting type is required.");
        else if (input is null || input.RecordedCall is null || string.IsNullOrWhiteSpace(input.EndpointRole) ||
            input.RecordedDistance is null && input.DraftedCompletion is null ||
            input.RecordedDistance is not null && input.DraftedCompletion is not null ||
            input.DraftedCompletion is { } drafted && string.IsNullOrWhiteSpace(drafted.Reason))
            failure = Fail(project, AuthoringDiagnosticCodes.InvalidInput, "Course call, endpoint role, and one distance source are required.");
        else
        {
            var parsed = BearingParser.Parse(input.BearingText);
            if (!parsed.IsSuccess) failure = Fail(project, AuthoringDiagnosticCodes.InvalidBearing, parsed.Diagnostic!.Value.Message);
            else bearing = parsed.Value;
        }
        return failure is null;
    }

    private static bool TryBlock(DeedProject project, RecordId recordId, BlockId blockId,
        out DeedRecord? record, out DraftingBlock? block, out ProjectEditResult? failure)
    {
        record = null; block = null; failure = null;
        var invalid = ProjectValidator.Validate(project).FirstOrDefault(x => x.Severity == DiagnosticSeverity.Error);
        if (invalid is not null) failure = Fail(project, AuthoringDiagnosticCodes.InvalidProject,
            $"Project has a structural error: {invalid.Code}.", invalid.EntityType, invalid.EntityId);
        else if (!project.Records.TryGetValue(recordId, out record) ||
            !record.DraftingBlocks.TryGetValue(blockId, out block))
            failure = Fail(project, AuthoringDiagnosticCodes.MissingEntity, "Record or block is missing.");
        return failure is null;
    }

    private static bool Allocate(ref ProjectIdCounters counters, ProjectIdKind kind,
        out ProjectIdAllocation allocation, out ProjectEditResult? failure, DeedProject project)
    {
        var result = counters.Allocate(kind);
        allocation = result.IsSuccess ? result.Value : default!;
        failure = result.IsSuccess ? null : Fail(project, AuthoringDiagnosticCodes.IdAllocation, result.Diagnostic!.Message);
        if (result.IsSuccess) counters = allocation.Counters;
        return result.IsSuccess;
    }

    private static ProjectEditResult Finish(DeedProject source, RecordId recordId,
        Dictionary<NodeId, DeedNode> nodes, Dictionary<CourseId, StraightCourse> courses,
        Dictionary<BlockId, DraftingBlock> blocks, ProjectIdCounters counters)
    {
        var records = new Dictionary<RecordId, DeedRecord>(source.Records)
        { [recordId] = new(recordId, nodes, courses, blocks) };
        var project = new DeedProject(source.Settings,
            new Dictionary<DraftingTypeId, DraftingType>(source.DraftingTypes), records, counters);
        var errors = ProjectValidator.Validate(project).Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
        return errors.Length == 0 ? new(project, true, Array.Empty<Diagnostic>()) :
            new(source, false, errors);
    }

    private static ProjectEditResult Fail(DeedProject source, string code, string message,
        string? entityType = null, string? entityId = null) =>
        new(source, false, new[] { new Diagnostic(code, DiagnosticSeverity.Error, message, entityType, entityId) });
}
