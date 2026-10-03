using System.Collections.ObjectModel;

namespace Deed.Core;

public sealed record SolvedLine(CourseId CourseId, NodeId FromNodeId, NodeId ToNodeId,
    Coordinate2D Start, Coordinate2D ComputedRecordedEnd, bool UsedDraftedDistance);

public sealed class RecordSolveResult
{
    internal RecordSolveResult(Dictionary<NodeId, Coordinate2D> nodes,
        Dictionary<CourseId, SolvedLine> lines, IEnumerable<Diagnostic> diagnostics)
    {
        NodeCoordinates = new ReadOnlyDictionary<NodeId, Coordinate2D>(new Dictionary<NodeId, Coordinate2D>(nodes));
        SolvedLines = new ReadOnlyDictionary<CourseId, SolvedLine>(new Dictionary<CourseId, SolvedLine>(lines));
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }
    public IReadOnlyDictionary<NodeId, Coordinate2D> NodeCoordinates { get; }
    public IReadOnlyDictionary<CourseId, SolvedLine> SolvedLines { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
}

public static class RecordSolver
{
    public static RecordSolveResult Solve(DeedProject project, RecordId recordId)
    {
        if (!project.Records.TryGetValue(recordId, out var record))
            return new(new(), new(), new[] { new Diagnostic(DiagnosticCodes.DanglingReference,
                DiagnosticSeverity.Error, "Record is missing.", "record", recordId.ToString()) });
        return Solve(project, record);
    }

    public static RecordSolveResult Solve(DeedProject project, DeedRecord record)
    {
        var diagnostics = new List<Diagnostic>(ProjectValidator.Validate(project, record));
        var nodes = new Dictionary<NodeId, Coordinate2D>();
        var lines = new Dictionary<CourseId, SolvedLine>();
        var failed = new HashSet<CourseId>();
        void Add(string code, string message, CourseId id) =>
            diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error, message, "course", id.ToString()));

        foreach (var (id, node) in record.Nodes)
            if (node.Definition is FixedNodeDefinition fixedNode &&
                double.IsFinite(fixedNode.Coordinate.Easting) &&
                double.IsFinite(fixedNode.Coordinate.Northing))
                nodes[id] = fixedNode.Coordinate;

        foreach (var (id, course) in record.Courses)
        {
            if (course.Bearing is null ||
                (course.RecordedDistance is null && course.DraftedCompletion is null))
            {
                Add(DiagnosticCodes.IncompleteCourse, "Course needs a bearing and distance.", id);
                failed.Add(id);
            }
            if (course.DraftedCompletion is { } completion &&
                course.RecordedDistance is null && string.IsNullOrWhiteSpace(completion.Reason))
                failed.Add(id);
        }

        var cyclic = FindCyclicCourses(record);
        foreach (var id in cyclic)
        {
            Add(DiagnosticCodes.CircularDependency, "Course is in a dependency cycle.", id);
            failed.Add(id);
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var (id, course) in record.Courses)
            {
                if (failed.Contains(id) || lines.ContainsKey(id) || !nodes.TryGetValue(course.FromNodeId, out var start) ||
                    !record.Nodes.TryGetValue(course.ToNodeId, out var endNode)) continue;
                if (endNode.Definition is CourseEndNodeDefinition end && end.ProducingCourseId != id)
                    continue;
                if (endNode.Definition is not (CourseEndNodeDefinition or FixedNodeDefinition))
                    continue;
                Distance distance = course.RecordedDistance ?? course.DraftedCompletion!.Distance;
                var endpoint = StraightLine.Endpoint(start, distance, course.Bearing!.Azimuth);
                if (!double.IsFinite(endpoint.Easting) || !double.IsFinite(endpoint.Northing))
                {
                    Add(DiagnosticCodes.NonFiniteCoordinate, "Computed endpoint is not finite.", id);
                    failed.Add(id);
                    continue;
                }
                if (endNode.Definition is FixedNodeDefinition)
                {
                    if (!nodes.TryGetValue(course.ToNodeId, out var fixedEnd)) continue;
                    double dx = endpoint.Easting - fixedEnd.Easting;
                    double dy = endpoint.Northing - fixedEnd.Northing;
                    if (Math.Sqrt(dx * dx + dy * dy) > project.Settings.CoincidenceDistance)
                        Add(DiagnosticCodes.FixedEndMismatch,
                            "Recorded course endpoint differs from its fixed node.", id);
                }
                else nodes[course.ToNodeId] = endpoint;
                lines[id] = new SolvedLine(id, course.FromNodeId, course.ToNodeId,
                    start, endpoint, course.RecordedDistance is null);
                changed = true;
            }
        } while (changed);

        foreach (var (id, course) in record.Courses)
            if (!lines.ContainsKey(id) && !failed.Contains(id))
                Add(DiagnosticCodes.UnresolvedDependency,
                    $"Course cannot resolve from node {course.FromNodeId}.", id);

        return new(nodes, lines, diagnostics);
    }

    private static HashSet<CourseId> FindCyclicCourses(DeedRecord record)
    {
        var cyclic = new HashSet<CourseId>();
        var state = new Dictionary<CourseId, int>();
        var path = new List<CourseId>();
        void Visit(CourseId id)
        {
            if (state.GetValueOrDefault(id) == 2) return;
            if (state.GetValueOrDefault(id) == 1)
            {
                int start = path.IndexOf(id);
                for (int i = start; i < path.Count; i++) cyclic.Add(path[i]);
                return;
            }
            state[id] = 1;
            path.Add(id);
            var course = record.Courses[id];
            if (record.Nodes.TryGetValue(course.FromNodeId, out var from) &&
                from.Definition is CourseEndNodeDefinition end &&
                record.Courses.TryGetValue(end.ProducingCourseId, out var producer) &&
                producer.ToNodeId == course.FromNodeId)
                Visit(end.ProducingCourseId);
            path.RemoveAt(path.Count - 1);
            state[id] = 2;
        }
        foreach (var id in record.Courses.Keys) Visit(id);
        return cyclic;
    }
}
