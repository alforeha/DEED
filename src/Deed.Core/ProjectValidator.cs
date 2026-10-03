namespace Deed.Core;

public static class ProjectValidator
{
    public static IReadOnlyList<Diagnostic> Validate(DeedProject project, DeedRecord record)
    {
        var diagnostics = new List<Diagnostic>();
        void Add(string code, string message, string type, string id) =>
            diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error, message, type, id));
        void CheckId(string value, string prefix, string type, string id)
        {
            if (!DomainId.Parse(value, prefix).IsSuccess)
                Add(DiagnosticCodes.InvalidId, "Entity ID is not canonical.", type, id);
        }

        CheckId(record.Id.ToString(), "rec-", "record", record.Id.ToString());
        foreach (var (id, type) in project.DraftingTypes)
        {
            CheckId(id.ToString(), "dt-", "draftingType", id.ToString());
            CheckId(type.Id.ToString(), "dt-", "draftingType", id.ToString());
            if (id != type.Id)
                Add(DiagnosticCodes.IdMismatch, "Drafting type key differs from its ID.", "draftingType", id.ToString());
        }

        var usedNodes = new HashSet<NodeId>();
        var membership = new Dictionary<CourseId, int>();
        foreach (var (id, course) in record.Courses)
        {
            CheckId(id.ToString(), "c-", "course", id.ToString());
            CheckId(course.Id.ToString(), "c-", "course", id.ToString());
            CheckId(course.DraftingTypeId.ToString(), "dt-", "course", id.ToString());
            CheckId(course.FromNodeId.ToString(), "n-", "course", id.ToString());
            CheckId(course.ToNodeId.ToString(), "n-", "course", id.ToString());
            if (id != course.Id)
                Add(DiagnosticCodes.IdMismatch, "Course key differs from its ID.", "course", id.ToString());
            if (!project.DraftingTypes.ContainsKey(course.DraftingTypeId))
                Add(DiagnosticCodes.DanglingReference, "Course drafting type is missing.", "course", id.ToString());
            foreach (var nodeId in new[] { course.FromNodeId, course.ToNodeId })
            {
                if (!record.Nodes.ContainsKey(nodeId))
                    Add(DiagnosticCodes.DanglingReference, "Course node is missing.", "course", id.ToString());
                else usedNodes.Add(nodeId);
            }
            if (record.Nodes.TryGetValue(course.ToNodeId, out var end) &&
                end.Definition is CourseEndNodeDefinition courseEnd && courseEnd.ProducingCourseId != id)
                Add(DiagnosticCodes.CourseEndMismatch, "Course end names a different producing course.", "course", id.ToString());
            if (course.DraftedCompletion is { } completion &&
                (course.RecordedDistance is not null || string.IsNullOrWhiteSpace(completion.Reason)))
                Add(DiagnosticCodes.InvalidDraftedCompletion,
                    "Drafted completion requires a missing recorded distance and a reason.", "course", id.ToString());
        }

        foreach (var (id, node) in record.Nodes)
        {
            CheckId(id.ToString(), "n-", "node", id.ToString());
            CheckId(node.Id.ToString(), "n-", "node", id.ToString());
            if (id != node.Id)
                Add(DiagnosticCodes.IdMismatch, "Node key differs from its ID.", "node", id.ToString());
            if (node.Definition is FixedNodeDefinition fixedNode &&
                (!double.IsFinite(fixedNode.Coordinate.Easting) ||
                 !double.IsFinite(fixedNode.Coordinate.Northing)))
                Add(DiagnosticCodes.NonFiniteCoordinate, "Fixed coordinate must be finite.", "node", id.ToString());
            if (node.Definition is CourseEndNodeDefinition end)
            {
                CheckId(end.ProducingCourseId.ToString(), "c-", "node", id.ToString());
                if (!record.Courses.TryGetValue(end.ProducingCourseId, out var producer))
                    Add(DiagnosticCodes.DanglingReference, "Producing course is missing.", "node", id.ToString());
                else
                {
                    if (producer.ToNodeId != id)
                        Add(DiagnosticCodes.CourseEndMismatch,
                            "Producing course does not end at this node.", "node", id.ToString());
                    else usedNodes.Add(id);
                }
            }
            if (!usedNodes.Contains(id) && !record.Courses.Values.Any(c =>
                    c.FromNodeId == id || c.ToNodeId == id))
                Add(DiagnosticCodes.OrphanNode, "Node is not used by any course.", "node", id.ToString());
        }

        foreach (var (id, block) in record.DraftingBlocks)
        {
            CheckId(id.ToString(), "blk-", "block", id.ToString());
            CheckId(block.Id.ToString(), "blk-", "block", id.ToString());
            CheckId(block.DraftingTypeId.ToString(), "dt-", "block", id.ToString());
            if (id != block.Id)
                Add(DiagnosticCodes.IdMismatch, "Block key differs from its ID.", "block", id.ToString());
            if (block.ParentBlockId is { } parentId)
                CheckId(parentId.ToString(), "blk-", "block", id.ToString());
            if (block.Origin is { } originId)
                CheckId(originId.ToString(), "n-", "block", id.ToString());
            if (!project.DraftingTypes.ContainsKey(block.DraftingTypeId))
                Add(DiagnosticCodes.DanglingReference, "Block drafting type is missing.", "block", id.ToString());
            if (block.ParentBlockId is { } parent && !record.DraftingBlocks.ContainsKey(parent))
                Add(DiagnosticCodes.DanglingReference, "Parent block is missing.", "block", id.ToString());
            if (block.Courses.Count == 0 ? block.Origin is not null :
                block.Origin is null || !record.Nodes.ContainsKey(block.Origin.Value))
                Add(DiagnosticCodes.InvalidBlockOrigin, "Block origin does not match its contents.", "block", id.ToString());

            var seen = new HashSet<CourseId>();
            foreach (var courseId in block.Courses)
            {
                CheckId(courseId.ToString(), "c-", "block", id.ToString());
                if (!record.Courses.ContainsKey(courseId))
                    Add(DiagnosticCodes.DanglingReference, "Block course is missing.", "block", id.ToString());
                if (!seen.Add(courseId))
                    Add(DiagnosticCodes.DuplicateCourseMembership, "Course appears twice in this block.", "block", id.ToString());
                membership[courseId] = membership.GetValueOrDefault(courseId) + 1;
            }
            if (project.DraftingTypes.TryGetValue(block.DraftingTypeId, out var type) &&
                type.Category == DraftingCategory.Boundary && block.Courses.Count > 0 &&
                block.Origin is { } origin && record.Nodes.ContainsKey(origin) &&
                !IsConnectedChain(record, block, origin))
                Add(DiagnosticCodes.DisconnectedBlock,
                    "Boundary courses do not form one chain from the origin.", "block", id.ToString());
        }

        foreach (var id in record.Courses.Keys)
        {
            int count = membership.GetValueOrDefault(id);
            if (count == 0)
                Add(DiagnosticCodes.CourseMembership, "Course is absent from all blocks.", "course", id.ToString());
            else if (count > 1)
                Add(DiagnosticCodes.DuplicateCourseMembership,
                    "Course belongs to more than one block position.", "course", id.ToString());
        }
        return Array.AsReadOnly(diagnostics.ToArray());
    }

    private static bool IsConnectedChain(DeedRecord record, DraftingBlock block, NodeId origin)
    {
        var ids = block.Courses.Where(record.Courses.ContainsKey).Distinct().ToArray();
        if (ids.Length != block.Courses.Count) return false;
        var byFrom = new Dictionary<NodeId, CourseId>();
        var toNodes = new HashSet<NodeId>();
        foreach (var id in ids)
        {
            var course = record.Courses[id];
            if (!byFrom.TryAdd(course.FromNodeId, id) || !toNodes.Add(course.ToNodeId)) return false;
        }
        var visited = new HashSet<CourseId>();
        var current = origin;
        while (byFrom.TryGetValue(current, out var id) && visited.Add(id))
            current = record.Courses[id].ToNodeId;
        return visited.Count == ids.Length && !byFrom.ContainsKey(current);
    }
}
