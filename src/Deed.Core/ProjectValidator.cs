namespace Deed.Core;

public static class ProjectValidator
{
    public static IReadOnlyList<Diagnostic> Validate(DeedProject project)
    {
        var diagnostics = new List<Diagnostic>();
        void Add(string code, string message, string type, string id) =>
            diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error, message, type, id));
        var maxima = new Dictionary<ProjectIdKind, int>();
        var seenNodes = new HashSet<NodeId>();
        var seenCourses = new HashSet<CourseId>();
        var seenBlocks = new HashSet<BlockId>();
        var seenRecordEntities = new HashSet<RecordId>();
        var seenTypeEntities = new HashSet<DraftingTypeId>();
        var seenNodeEntities = new HashSet<NodeId>();
        var seenCourseEntities = new HashSet<CourseId>();
        var seenBlockEntities = new HashSet<BlockId>();
        void Count(ProjectIdKind kind, string value)
        {
            if (value.Length >= 5 && int.TryParse(value.AsSpan(value.Length - 5), out int number))
                maxima[kind] = Math.Max(maxima.GetValueOrDefault(kind), number);
        }
        foreach (var (id, type) in project.DraftingTypes)
        {
            Count(ProjectIdKind.DraftingType, id.ToString());
            Count(ProjectIdKind.DraftingType, type.Id.ToString());
            if (id != type.Id)
                Add(DiagnosticCodes.IdMismatch, "Drafting type key differs from its ID.", "draftingType", id.ToString());
            if (!seenTypeEntities.Add(type.Id))
                Add(DiagnosticCodes.DuplicateProjectId, "Drafting type entity ID occurs more than once.",
                    "draftingType", type.Id.ToString());
            if (!DraftingTypeId.TryParse(id.ToString()).IsSuccess ||
                !DraftingTypeId.TryParse(type.Id.ToString()).IsSuccess)
                Add(DiagnosticCodes.InvalidId, "Drafting type ID is not canonical.", "draftingType", id.ToString());
        }
        foreach (var (id, record) in project.Records)
        {
            Count(ProjectIdKind.Record, id.ToString());
            Count(ProjectIdKind.Record, record.Id.ToString());
            if (id != record.Id)
                Add(DiagnosticCodes.IdMismatch, "Record key differs from its ID.", "record", id.ToString());
            if (!seenRecordEntities.Add(record.Id))
                Add(DiagnosticCodes.DuplicateProjectId, "Record entity ID occurs more than once.",
                    "record", record.Id.ToString());
            diagnostics.AddRange(Validate(project, record));
            foreach (var (node, entity) in record.Nodes)
            {
                Count(ProjectIdKind.Node, node.ToString());
                Count(ProjectIdKind.Node, entity.Id.ToString());
                if (!seenNodes.Add(node))
                    Add(DiagnosticCodes.DuplicateProjectId, "Node ID occurs in multiple records.", "node", node.ToString());
                if (!seenNodeEntities.Add(entity.Id))
                    Add(DiagnosticCodes.DuplicateProjectId, "Node entity ID occurs in multiple records.", "node", entity.Id.ToString());
            }
            foreach (var (course, entity) in record.Courses)
            {
                Count(ProjectIdKind.Course, course.ToString());
                Count(ProjectIdKind.Course, entity.Id.ToString());
                if (!seenCourses.Add(course))
                    Add(DiagnosticCodes.DuplicateProjectId, "Course ID occurs in multiple records.", "course", course.ToString());
                if (!seenCourseEntities.Add(entity.Id))
                    Add(DiagnosticCodes.DuplicateProjectId, "Course entity ID occurs in multiple records.", "course", entity.Id.ToString());
            }
            foreach (var (block, entity) in record.DraftingBlocks)
            {
                Count(ProjectIdKind.Block, block.ToString());
                Count(ProjectIdKind.Block, entity.Id.ToString());
                if (!seenBlocks.Add(block))
                    Add(DiagnosticCodes.DuplicateProjectId, "Block ID occurs in multiple records.", "block", block.ToString());
                if (!seenBlockEntities.Add(entity.Id))
                    Add(DiagnosticCodes.DuplicateProjectId, "Block entity ID occurs in multiple records.", "block", entity.Id.ToString());
            }
        }
        foreach (var kind in Enum.GetValues<ProjectIdKind>())
        {
            int stored = project.IdCounters.Get(kind);
            if (stored < 0 || stored > ProjectIdCounters.Maximum)
                Add(DiagnosticCodes.InvalidSettings, "ID counter is outside 0 through 99999.", "idCounter", kind.ToString());
            else if (stored < maxima.GetValueOrDefault(kind))
                Add(DiagnosticCodes.IdCounterBehind, "ID counter is behind an existing ID.", "idCounter", kind.ToString());
        }
        return Array.AsReadOnly(diagnostics.ToArray());
    }

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
        var blocksByCourse = new Dictionary<CourseId, HashSet<BlockId>>();
        var placementCount = new Dictionary<NodeId, int>();
        foreach (var (id, course) in record.Courses)
        {
            CheckId(id.ToString(), "c-", "course", id.ToString());
            CheckId(course.Id.ToString(), "c-", "course", id.ToString());
            CheckId(course.DraftingTypeId.ToString(), "dt-", "course", id.ToString());
            CheckId(course.FromNodeId.ToString(), "n-", "course", id.ToString());
            CheckId(course.ToNodeId.ToString(), "n-", "course", id.ToString());
            if (course.ParentCourseId is { } parentId)
                CheckId(parentId.ToString(), "c-", "course", id.ToString());
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
                end.Definition is not FixedNodeDefinition &&
                (end.Definition is not CourseEndNodeDefinition courseEnd ||
                 !record.Courses.TryGetValue(courseEnd.ProducingCourseId, out var producer) ||
                 producer.ToNodeId != course.ToNodeId))
                Add(DiagnosticCodes.CourseEndMismatch,
                    "Course end must be fixed or owned by its producing course.", "course", id.ToString());
            if (course.DraftedCompletion is { } completion &&
                (course.RecordedDistance is not null || string.IsNullOrWhiteSpace(completion.Reason)))
                Add(DiagnosticCodes.InvalidDraftedCompletion,
                    "Drafted completion requires a missing recorded distance and a reason.", "course", id.ToString());
            foreach (var placement in course.AlongPoints)
            {
                CheckId(placement.NodeId.ToString(), "n-", "course", id.ToString());
                int count = placementCount.GetValueOrDefault(placement.NodeId) + 1;
                placementCount[placement.NodeId] = count;
                if (count > 1)
                    Add(DiagnosticCodes.DuplicateAlongPlacement,
                        "Along node appears in more than one placement.", "node", placement.NodeId.ToString());
                if (!record.Nodes.TryGetValue(placement.NodeId, out var alongNode))
                    Add(DiagnosticCodes.MissingAlongPlacement,
                        "Placed along node is missing.", "course", id.ToString());
                else if (alongNode.Definition is not AlongCourseNodeDefinition along || along.HostCourseId != id)
                    Add(DiagnosticCodes.AlongHostMismatch,
                        "Along node definition does not name its host course.", "node", placement.NodeId.ToString());
                else usedNodes.Add(placement.NodeId);
            }
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
            if (node.Definition is AlongCourseNodeDefinition along)
            {
                CheckId(along.HostCourseId.ToString(), "c-", "node", id.ToString());
                if (!record.Courses.ContainsKey(along.HostCourseId) ||
                    placementCount.GetValueOrDefault(id) == 0)
                    Add(DiagnosticCodes.MissingAlongPlacement,
                        "Along node has no placement on its host course.", "node", id.ToString());
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
                if (!blocksByCourse.TryGetValue(courseId, out var owners))
                    blocksByCourse[courseId] = owners = new HashSet<BlockId>();
                owners.Add(id);
            }
            if (block.Courses.Count > 0)
            {
                var roots = block.Courses.Where(courseId => record.Courses.TryGetValue(courseId, out var c) &&
                    c.ParentCourseId is null).Distinct().ToArray();
                if (roots.Length != 1)
                    Add(DiagnosticCodes.InvalidRootCount,
                        "Nonempty block must have exactly one root course.", "block", id.ToString());
                else if (record.Courses[roots[0]].FromNodeId != block.Origin)
                    Add(DiagnosticCodes.InvalidBlockOrigin,
                        "Root course must start at block origin.", "block", id.ToString());
                else if (block.Origin is { } rootOrigin &&
                    record.Nodes.TryGetValue(rootOrigin, out var originNode) &&
                    originNode.Definition is not FixedNodeDefinition)
                    Add(DiagnosticCodes.InvalidBlockOrigin,
                        "Stage A root origin must be a fixed node.", "block", id.ToString());
            }
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
        foreach (var (id, course) in record.Courses)
        {
            if (course.ParentCourseId is not { } parentId) continue;
            if (!record.Courses.TryGetValue(parentId, out var parent))
            {
                Add(DiagnosticCodes.InvalidParent, "Parent course is missing.", "course", id.ToString());
                continue;
            }
            if (!blocksByCourse.TryGetValue(id, out var childBlocks) ||
                !blocksByCourse.TryGetValue(parentId, out var parentBlocks) ||
                !childBlocks.Overlaps(parentBlocks))
                Add(DiagnosticCodes.ParentOutsideBlock,
                    "Parent course is outside the child's block.", "course", id.ToString());
            if (course.FromNodeId != parent.FromNodeId && course.FromNodeId != parent.ToNodeId &&
                !parent.AlongPoints.Any(p => p.NodeId == course.FromNodeId))
                Add(DiagnosticCodes.ParentAttachmentMismatch,
                    "Child start is not a point on its parent course.", "course", id.ToString());
        }
        foreach (var id in FindParentCycles(record))
            Add(DiagnosticCodes.ParentCycle, "Course is in a parent cycle.", "course", id.ToString());
        return Array.AsReadOnly(diagnostics.ToArray());
    }

    internal static HashSet<CourseId> FindParentCycles(DeedRecord record)
    {
        var cyclic = new HashSet<CourseId>();
        var state = new Dictionary<CourseId, int>();
        var path = new List<CourseId>();
        void Visit(CourseId id)
        {
            if (state.GetValueOrDefault(id) == 2) return;
            if (state.GetValueOrDefault(id) == 1)
            {
                for (int i = path.IndexOf(id); i < path.Count; i++) cyclic.Add(path[i]);
                return;
            }
            state[id] = 1;
            path.Add(id);
            if (record.Courses[id].ParentCourseId is { } parent && record.Courses.ContainsKey(parent))
                Visit(parent);
            path.RemoveAt(path.Count - 1);
            state[id] = 2;
        }
        foreach (var id in record.Courses.Keys) Visit(id);
        return cyclic;
    }
}
