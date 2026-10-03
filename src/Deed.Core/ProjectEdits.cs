namespace Deed.Core;

public sealed record ReassignedEndpoint(NodeId NodeId, CourseId FormerProducer, CourseId NewProducer);

public sealed class CascadeDeletionReport
{
    internal CascadeDeletionReport(IEnumerable<CourseId> courses, IEnumerable<NodeId> nodes,
        IEnumerable<NodeId> preservedStarts, IEnumerable<ReassignedEndpoint> reassigned)
    {
        DeletedCourseIds = Array.AsReadOnly(courses.Distinct()
            .OrderBy(x => x.ToString(), StringComparer.Ordinal).ToArray());
        DeletedNodeIds = Array.AsReadOnly(nodes.Distinct()
            .OrderBy(x => x.ToString(), StringComparer.Ordinal).ToArray());
        PreservedParentOwnedStartingNodeIds = Array.AsReadOnly(preservedStarts.Distinct()
            .OrderBy(x => x.ToString(), StringComparer.Ordinal).ToArray());
        ReassignedEndpoints = Array.AsReadOnly(reassigned.DistinctBy(x => x.NodeId)
            .OrderBy(x => x.NodeId.ToString(), StringComparer.Ordinal).ToArray());
    }

    public IReadOnlyList<CourseId> DeletedCourseIds { get; }
    public IReadOnlyList<NodeId> DeletedNodeIds { get; }
    public IReadOnlyList<NodeId> PreservedParentOwnedStartingNodeIds { get; }
    public IReadOnlyList<ReassignedEndpoint> ReassignedEndpoints { get; }
}

public sealed class ProjectEditResult
{
    internal ProjectEditResult(DeedProject project, bool changed, IEnumerable<Diagnostic> diagnostics,
        CascadeDeletionReport? deletionReport = null)
    {
        Project = project;
        Changed = changed;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        DeletionReport = deletionReport;
    }

    public DeedProject Project { get; }
    public bool Changed { get; }
    public bool IsSuccess => Diagnostics.All(x => x.Severity != DiagnosticSeverity.Error);
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public CascadeDeletionReport? DeletionReport { get; }
}

/// <summary>Explicit Stage A transcription edits. Every result holds a complete immutable project.</summary>
public static class ProjectEdits
{
    private static ProjectEditResult Failure(DeedProject project, string code, string message,
        string? type = null, string? id = null) =>
        new(project, false, new[] { new Diagnostic(code, DiagnosticSeverity.Error, message, type, id) });

    private static bool Valid(Distance value) => double.IsFinite(value.Value) && value.Value >= 0;

    private static ProjectEditResult EditCourse(DeedProject project, RecordId recordId, CourseId courseId,
        Func<StraightCourse, (StraightCourse? Course, Diagnostic? Error)> change)
    {
        if (!project.Records.TryGetValue(recordId, out var record))
            return Failure(project, DiagnosticCodes.EditMissingRecord, "Record is missing.", "record", recordId.ToString());
        if (!record.Courses.TryGetValue(courseId, out var course))
            return Failure(project, DiagnosticCodes.EditMissingCourse, "Course is missing.", "course", courseId.ToString());
        var invalid = ProjectValidator.Validate(project).FirstOrDefault(x => x.Severity == DiagnosticSeverity.Error);
        if (invalid is not null)
            return Failure(project, DiagnosticCodes.EditInvalidProject,
                $"Project has a structural error: {invalid.Code}.", invalid.EntityType, invalid.EntityId);
        var (replacement, error) = change(course);
        if (error is not null) return new(project, false, new[] { error });
        if (replacement is null || SameInputs(course, replacement))
            return new(project, false, Array.Empty<Diagnostic>());
        var courses = new Dictionary<CourseId, StraightCourse>(record.Courses) { [courseId] = replacement };
        return Finish(project, recordId, new DeedRecord(recordId,
            new Dictionary<NodeId, DeedNode>(record.Nodes), courses,
            new Dictionary<BlockId, DraftingBlock>(record.DraftingBlocks)));
    }

    private static bool SameInputs(StraightCourse a, StraightCourse b) =>
        a.OriginalRecordedText == b.OriginalRecordedText &&
        a.ParsedBearing?.OriginalText == b.ParsedBearing?.OriginalText &&
        a.RecordedDistance == b.RecordedDistance &&
        a.DraftedCompletion == b.DraftedCompletion && a.FinalPart == b.FinalPart &&
        a.AlongPoints.SequenceEqual(b.AlongPoints);

    private static ProjectEditResult Finish(DeedProject project, RecordId recordId,
        DeedRecord replacement, CascadeDeletionReport? report = null)
    {
        var records = new Dictionary<RecordId, DeedRecord>(project.Records) { [recordId] = replacement };
        var edited = new DeedProject(project.Settings,
            new Dictionary<DraftingTypeId, DraftingType>(project.DraftingTypes), records, project.IdCounters);
        var errors = ProjectValidator.Validate(edited)
            .Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
        return errors.Length == 0 ? new(edited, true, Array.Empty<Diagnostic>(), report) :
            new(project, false, errors);
    }

    public static ProjectEditResult EditRecordedText(DeedProject project, RecordId recordId,
        CourseId courseId, string? text) => EditCourse(project, recordId, courseId, course =>
        text is null ? (null, new Diagnostic(DiagnosticCodes.EditInvalidValue,
            DiagnosticSeverity.Error, "Recorded text cannot be null.", "course", courseId.ToString())) :
            (course with { OriginalRecordedText = text }, null));

    public static ProjectEditResult EditRecordedCall(DeedProject project, RecordId recordId,
        CourseId courseId, string? text, string? bearingText, Distance? distance) =>
        EditCourse(project, recordId, courseId, course =>
        {
            if (text is null || distance is { } d && !Valid(d))
                return (null, new Diagnostic(DiagnosticCodes.EditInvalidValue,
                    DiagnosticSeverity.Error, "Recorded text and distance are invalid.",
                    "course", courseId.ToString()));
            var parsed = BearingParser.Parse(bearingText);
            if (!parsed.IsSuccess)
                return (null, new Diagnostic(DiagnosticCodes.EditInvalidBearing,
                    DiagnosticSeverity.Error, parsed.Diagnostic!.Value.Message,
                    "course", courseId.ToString()));
            return (course with { OriginalRecordedText = text, ParsedBearing = parsed.Value,
                RecordedDistance = distance,
                DraftedCompletion = distance is null ? course.DraftedCompletion : null }, null);
        });

    public static ProjectEditResult EditLineInputs(DeedProject project, RecordId recordId,
        CourseId courseId, string? recordedText, string? bearingText, Distance? recordedDistance,
        DraftedDistanceCompletion? draftedCompletion, Distance? finalPart) =>
        EditCourse(project, recordId, courseId, course =>
        {
            if (recordedText is null || recordedDistance is { } recorded && !Valid(recorded) ||
                draftedCompletion is { } drafted && (!Valid(drafted.Distance) ||
                    string.IsNullOrWhiteSpace(drafted.Reason)) ||
                recordedDistance is not null && draftedCompletion is not null ||
                finalPart is { } final && !Valid(final))
                return (null, new Diagnostic(DiagnosticCodes.EditInvalidValue,
                    DiagnosticSeverity.Error, "Line inputs are invalid.", "course", courseId.ToString()));
            var parsed = BearingParser.Parse(bearingText);
            if (!parsed.IsSuccess)
                return (null, new Diagnostic(DiagnosticCodes.EditInvalidBearing,
                    DiagnosticSeverity.Error, parsed.Diagnostic!.Value.Message,
                    "course", courseId.ToString()));
            return (course with { OriginalRecordedText = recordedText, ParsedBearing = parsed.Value,
                RecordedDistance = recordedDistance, DraftedCompletion = draftedCompletion,
                FinalPart = finalPart }, null);
        });

    public static ProjectEditResult EditBearing(DeedProject project, RecordId recordId,
        CourseId courseId, string? bearingText) => EditCourse(project, recordId, courseId, course =>
    {
        var parsed = BearingParser.Parse(bearingText);
        return parsed.IsSuccess ? (course with { ParsedBearing = parsed.Value }, null) :
            (null, new Diagnostic(DiagnosticCodes.EditInvalidBearing, DiagnosticSeverity.Error,
                parsed.Diagnostic!.Value.Message, "course", courseId.ToString()));
    });

    public static ProjectEditResult EditRecordedDistance(DeedProject project, RecordId recordId,
        CourseId courseId, Distance? distance) => EditCourse(project, recordId, courseId, course =>
        distance is { } d && !Valid(d)
            ? (null, new Diagnostic(DiagnosticCodes.EditInvalidValue, DiagnosticSeverity.Error,
                "Recorded distance must be finite and nonnegative.", "course", courseId.ToString()))
            : (course with { RecordedDistance = distance,
                DraftedCompletion = distance is null ? course.DraftedCompletion : null }, null));

    public static ProjectEditResult EditDraftedCompletion(DeedProject project, RecordId recordId,
        CourseId courseId, DraftedDistanceCompletion? completion) => EditCourse(project, recordId, courseId, course =>
        completion is not null && (course.RecordedDistance is not null ||
            !Valid(completion.Distance) || string.IsNullOrWhiteSpace(completion.Reason))
            ? (null, new Diagnostic(DiagnosticCodes.EditInvalidValue, DiagnosticSeverity.Error,
                "Drafted completion requires absent recorded distance, valid distance, and a reason.",
                "course", courseId.ToString()))
            : (course with { DraftedCompletion = completion }, null));

    public static ProjectEditResult EditAlongDistance(DeedProject project, RecordId recordId,
        CourseId courseId, NodeId alongNodeId, Distance fromPrevious) =>
        EditCourse(project, recordId, courseId, course =>
        {
            int index = -1;
            for (int i = 0; i < course.AlongPoints.Count; i++)
                if (course.AlongPoints[i].NodeId == alongNodeId) { index = i; break; }
            if (index < 0) return (null, new Diagnostic(DiagnosticCodes.EditMissingAlongPoint,
                DiagnosticSeverity.Error, "Along point is missing from course.", "node", alongNodeId.ToString()));
            if (!Valid(fromPrevious)) return (null, new Diagnostic(DiagnosticCodes.EditInvalidValue,
                DiagnosticSeverity.Error, "Along distance must be finite and nonnegative.",
                "node", alongNodeId.ToString()));
            var placements = course.AlongPoints.ToArray();
            placements[index] = new AlongPointPlacement(alongNodeId, fromPrevious);
            return (new StraightCourse(course.Id, course.DraftingTypeId, course.FromNodeId,
                course.ToNodeId, course.ParentCourseId, placements, course.FinalPart,
                course.OriginalRecordedText, course.ParsedBearing, course.RecordedDistance,
                course.DraftedCompletion), null);
        });

    public static ProjectEditResult EditFinalPart(DeedProject project, RecordId recordId,
        CourseId courseId, Distance? finalPart) => EditCourse(project, recordId, courseId, course =>
        finalPart is { } d && !Valid(d)
            ? (null, new Diagnostic(DiagnosticCodes.EditInvalidValue, DiagnosticSeverity.Error,
                "Final part must be finite and nonnegative.", "course", courseId.ToString()))
            : (course with { FinalPart = finalPart }, null));

    public static ProjectEditResult DeleteCourse(DeedProject project, RecordId recordId, CourseId courseId)
    {
        if (!project.Records.TryGetValue(recordId, out var record))
            return Failure(project, DiagnosticCodes.EditMissingRecord, "Record is missing.", "record", recordId.ToString());
        if (!record.Courses.ContainsKey(courseId))
            return Failure(project, DiagnosticCodes.EditMissingCourse, "Course is missing.", "course", courseId.ToString());
        var invalid = ProjectValidator.Validate(project).FirstOrDefault(x => x.Severity == DiagnosticSeverity.Error);
        if (invalid is not null)
            return Failure(project, DiagnosticCodes.EditInvalidProject,
                $"Project has a structural error: {invalid.Code}.", invalid.EntityType, invalid.EntityId);

        var removed = new HashSet<CourseId> { courseId };
        bool added;
        do
        {
            added = false;
            foreach (var course in record.Courses.Values)
                if (course.ParentCourseId is { } parent && removed.Contains(parent))
                    added |= removed.Add(course.Id);
        } while (added);

        var survivors = record.Courses.Where(x => !removed.Contains(x.Key))
            .ToDictionary(x => x.Key, x => x.Value);
        var referenced = new HashSet<NodeId>();
        foreach (var course in survivors.Values)
        {
            referenced.Add(course.FromNodeId);
            referenced.Add(course.ToNodeId);
            foreach (var along in course.AlongPoints) referenced.Add(along.NodeId);
        }
        var candidateNodes = new HashSet<NodeId>();
        foreach (var id in removed)
        {
            var course = record.Courses[id];
            candidateNodes.Add(course.FromNodeId);
            candidateNodes.Add(course.ToNodeId);
            foreach (var along in course.AlongPoints) candidateNodes.Add(along.NodeId);
        }
        var deletedNodes = candidateNodes.Where(id => !referenced.Contains(id)).ToHashSet();
        var nodes = record.Nodes.Where(x => !deletedNodes.Contains(x.Key))
            .ToDictionary(x => x.Key, x => x.Value);
        var reassigned = new List<ReassignedEndpoint>();
        foreach (var (id, node) in nodes.ToArray())
        {
            if (node.Definition is not CourseEndNodeDefinition end || !removed.Contains(end.ProducingCourseId))
                continue;
            var newProducer = survivors.Values.Where(x => x.ToNodeId == id)
                .Select(x => x.Id).OrderBy(x => x.ToString(), StringComparer.Ordinal).FirstOrDefault();
            if (newProducer == default)
                return Failure(project, DiagnosticCodes.EditEndpointWithoutProducer,
                    "Retained endpoint has no surviving producing course.", "node", id.ToString());
            nodes[id] = node with { Definition = new CourseEndNodeDefinition(newProducer) };
            reassigned.Add(new ReassignedEndpoint(id, end.ProducingCourseId, newProducer));
        }
        var reassignedIds = reassigned.Select(x => x.NodeId).ToHashSet();
        var preservedStarts = removed.Select(id => record.Courses[id])
            .Where(course => course.ParentCourseId is { } parentId &&
                survivors.TryGetValue(parentId, out var parent) &&
                (course.FromNodeId == parent.FromNodeId ||
                    course.FromNodeId == parent.ToNodeId ||
                    parent.AlongPoints.Any(point => point.NodeId == course.FromNodeId)) &&
                nodes.ContainsKey(course.FromNodeId) && !reassignedIds.Contains(course.FromNodeId))
            .Select(course => course.FromNodeId).Distinct().ToArray();
        var blocks = record.DraftingBlocks.ToDictionary(x => x.Key, x =>
        {
            var b = x.Value;
            var courses = b.Courses.Where(id => !removed.Contains(id)).ToArray();
            return new DraftingBlock(b.Id, b.Name, b.DraftingTypeId, b.ParentBlockId,
                b.ViewOrder, courses.Length == 0 ? null : b.Origin, courses, b.ReportClose);
        });
        var report = new CascadeDeletionReport(removed, deletedNodes, preservedStarts, reassigned);
        return Finish(project, recordId, new DeedRecord(recordId, nodes, survivors, blocks), report);
    }
}
