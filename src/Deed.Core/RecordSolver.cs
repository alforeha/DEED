using System.Collections.ObjectModel;

namespace Deed.Core;

public sealed record SolvedLine(CourseId CourseId, NodeId FromNodeId, NodeId ToNodeId,
    Coordinate2D Start, Coordinate2D ComputedRecordedEnd, bool UsedDraftedDistance);

public sealed record CoursePartSummary(double EnteredAlongParts, double? FinalPart,
    double? EnteredTotal, double? UnenteredRemainder, double? SignedDiscrepancy);

public sealed class RecordSolveResult
{
    internal RecordSolveResult(Dictionary<NodeId, Coordinate2D> nodes,
        Dictionary<CourseId, SolvedLine> lines, Dictionary<CourseId, CoursePartSummary> parts,
        IEnumerable<Diagnostic> diagnostics)
    {
        NodeCoordinates = new ReadOnlyDictionary<NodeId, Coordinate2D>(new Dictionary<NodeId, Coordinate2D>(nodes));
        SolvedLines = new ReadOnlyDictionary<CourseId, SolvedLine>(new Dictionary<CourseId, SolvedLine>(lines));
        PartSummaries = new ReadOnlyDictionary<CourseId, CoursePartSummary>(
            new Dictionary<CourseId, CoursePartSummary>(parts));
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }
    public IReadOnlyDictionary<NodeId, Coordinate2D> NodeCoordinates { get; }
    public IReadOnlyDictionary<CourseId, SolvedLine> SolvedLines { get; }
    public IReadOnlyDictionary<CourseId, CoursePartSummary> PartSummaries { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
}

public static class RecordSolver
{
    public static RecordSolveResult Solve(DeedProject project, RecordId recordId)
    {
        if (!project.Records.TryGetValue(recordId, out var record))
            return new(new(), new(), new(), new[] { new Diagnostic(DiagnosticCodes.DanglingReference,
                DiagnosticSeverity.Error, "Record is missing.", "record", recordId.ToString()) });
        return Solve(project, record);
    }

    public static RecordSolveResult Solve(DeedProject project, DeedRecord record)
    {
        var diagnostics = new List<Diagnostic>(ProjectValidator.Validate(project, record));
        var nodes = new Dictionary<NodeId, Coordinate2D>();
        var lines = new Dictionary<CourseId, SolvedLine>();
        var parts = new Dictionary<CourseId, CoursePartSummary>();
        var failed = new HashSet<CourseId>();
        var placedAlong = new HashSet<CourseId>();
        void Add(string code, string message, CourseId id) =>
            diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error, message, "course", id.ToString()));

        foreach (var (id, node) in record.Nodes)
            if (node.Definition is FixedNodeDefinition fixedNode &&
                double.IsFinite(fixedNode.Coordinate.Easting) &&
                double.IsFinite(fixedNode.Coordinate.Northing))
                nodes[id] = fixedNode.Coordinate;

        foreach (var (id, course) in record.Courses)
        {
            parts[id] = Summarize(course);
            if (parts[id].SignedDiscrepancy is { } discrepancy && discrepancy != 0)
                diagnostics.Add(new Diagnostic(DiagnosticCodes.PartLengthDiscrepancy,
                    DiagnosticSeverity.Warning, "Entered parts differ from overall recorded length.",
                    "course", id.ToString()));
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

        var cyclic = ProjectValidator.FindParentCycles(record);
        foreach (var id in cyclic)
        {
            Add(DiagnosticCodes.CircularDependency, "Course is in a dependency cycle.", id);
            failed.Add(id);
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var (id, course) in record.Courses.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal))
            {
                if (!cyclic.Contains(id) && !placedAlong.Contains(id) && course.Bearing is not null &&
                    nodes.TryGetValue(course.FromNodeId, out var alongStart))
                {
                    double cumulative = 0;
                    double? driving = course.RecordedDistance?.Value ?? course.DraftedCompletion?.Distance.Value;
                    foreach (var placement in course.AlongPoints)
                    {
                        cumulative += placement.FromPrevious.Value;
                        if (!double.IsFinite(cumulative))
                        {
                            Add(DiagnosticCodes.NonFiniteCoordinate,
                                "Cumulative along distance is not finite.", id);
                            break;
                        }
                        if (driving is { } length && cumulative > length)
                            diagnostics.Add(new Diagnostic(DiagnosticCodes.AlongPointBeyondCourse,
                                DiagnosticSeverity.Warning, "Along point lies beyond the driving length.",
                                "node", placement.NodeId.ToString()));
                        if (!record.Nodes.TryGetValue(placement.NodeId, out var alongNode) ||
                            alongNode.Definition is not AlongCourseNodeDefinition along || along.HostCourseId != id)
                            continue;
                        var position = StraightLine.Endpoint(alongStart, Distance.TryCreate(cumulative).Value,
                            course.Bearing.Azimuth);
                        if (double.IsFinite(position.Easting) && double.IsFinite(position.Northing))
                        {
                            nodes[placement.NodeId] = position;
                            changed = true;
                        }
                        else Add(DiagnosticCodes.NonFiniteCoordinate,
                            "Computed along point is not finite.", id);
                    }
                    placedAlong.Add(id);
                }
                if (failed.Contains(id) || lines.ContainsKey(id) || !nodes.TryGetValue(course.FromNodeId, out var start) ||
                    !record.Nodes.TryGetValue(course.ToNodeId, out var endNode)) continue;
                if (endNode.Definition is not (CourseEndNodeDefinition or FixedNodeDefinition))
                    continue;
                bool existingEnd = endNode.Definition is FixedNodeDefinition ||
                    endNode.Definition is CourseEndNodeDefinition end && end.ProducingCourseId != id;
                if (existingEnd && !nodes.ContainsKey(course.ToNodeId)) continue;
                Distance distance = course.RecordedDistance ?? course.DraftedCompletion!.Distance;
                var endpoint = StraightLine.Endpoint(start, distance, course.Bearing!.Azimuth);
                if (!double.IsFinite(endpoint.Easting) || !double.IsFinite(endpoint.Northing))
                {
                    Add(DiagnosticCodes.NonFiniteCoordinate, "Computed endpoint is not finite.", id);
                    failed.Add(id);
                    continue;
                }
                if (existingEnd)
                {
                    var actualEnd = nodes[course.ToNodeId];
                    double dx = endpoint.Easting - actualEnd.Easting;
                    double dy = endpoint.Northing - actualEnd.Northing;
                    if (Math.Sqrt(dx * dx + dy * dy) > project.Settings.CoincidenceDistance)
                        Add(endNode.Definition is FixedNodeDefinition
                            ? DiagnosticCodes.FixedEndMismatch : DiagnosticCodes.ExistingEndMismatch,
                            "Recorded course endpoint differs from its existing node.", id);
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

        return new(nodes, lines, parts, diagnostics);
    }

    private static CoursePartSummary Summarize(StraightCourse course)
    {
        double along = course.AlongPoints.Sum(p => p.FromPrevious.Value);
        double? final = course.FinalPart?.Value;
        double? total = final is null ? null : along + final.Value;
        double? remainder = final is null && course.RecordedDistance is { } recorded
            ? recorded.Value - along : null;
        double? discrepancy = null;
        if (final is not null && course.RecordedDistance is { } overall)
        {
            try
            {
                decimal entered = course.AlongPoints.Sum(p => (decimal)p.FromPrevious.Value) +
                    (decimal)final.Value;
                discrepancy = (double)((decimal)overall.Value - entered);
            }
            catch (OverflowException)
            {
                discrepancy = overall.Value - total;
            }
        }
        return new(along, final, total, remainder, discrepancy);
    }
}
