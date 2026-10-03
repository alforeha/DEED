using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Deed.Core;
using Microsoft.Win32;

namespace Deed.Desktop;

public partial class MainWindow : Window
{
    private readonly WorkspaceSession _session = new();
    private IReadOnlyList<Diagnostic> _editDiagnostics = Array.Empty<Diagnostic>();
    private RecordSolveResult? _solve;
    private bool _refreshing;

    private sealed record Choice<T>(T Id, string Label);
    private sealed record CourseRow(int Position, CourseId Id, string Parent, string Start,
        string Bearing, string Length, string Parts, string Final);
    private sealed record DiagnosticRow(string Severity, string Code, string Entity, string Message);

    public MainWindow()
    {
        InitializeComponent();
        Refresh();
        Loaded += (_, _) => Drawing.FitDrawing();
    }

    private (RecordId RecordId, BlockId BlockId, DeedRecord Record, DraftingBlock Block,
        DraftingTypeId TypeId) Current()
    {
        var project = _session.History.Project;
        var record = project.Records.Values.First();
        var block = record.DraftingBlocks.Values.First();
        return (record.Id, block.Id, record, block, block.DraftingTypeId);
    }

    private void Refresh(CourseId? selected = null)
    {
        _refreshing = true;
        try
        {
            var (recordId, _, record, block, _) = Current();
            _solve = RecordSolver.Solve(_session.History.Project, recordId);
            var previous = selected ?? (CourseTable.SelectedItem as CourseRow)?.Id;
            var rows = block.Courses.Select((id, index) =>
            {
                var c = record.Courses[id];
                return new CourseRow(index + 1, id, c.ParentCourseId?.ToString() ?? "root",
                    c.FromNodeId.ToString(), c.ParsedBearing?.OriginalText ?? "",
                    (c.RecordedDistance ?? c.DraftedCompletion?.Distance)?.Value.ToString("G", CultureInfo.InvariantCulture) ?? "",
                    string.Join(", ", c.AlongPoints.Select(p => $"{p.NodeId}:{p.FromPrevious.Value:G}")),
                    c.FinalPart?.Value.ToString("G", CultureInfo.InvariantCulture) ?? "");
            }).ToArray();
            CourseTable.ItemsSource = rows;
            var selectedRow = rows.FirstOrDefault(r => r.Id == previous) ?? rows.LastOrDefault();
            CourseTable.SelectedItem = selectedRow;
            var selectedId = selectedRow?.Id;
            var choices = block.Courses.Select(id => new Choice<CourseId>(id, id.ToString())).ToArray();
            ParentCourse.ItemsSource = choices;
            AlongHost.ItemsSource = choices;
            ParentCourse.SelectedItem = choices.FirstOrDefault(x => x.Id == selectedId);
            AlongHost.SelectedItem = choices.FirstOrDefault(x => x.Id == selectedId);
            var endChoices = new List<Choice<NodeId>> { new(default, "New endpoint") };
            endChoices.AddRange(record.Nodes.Values.Where(n => n.Definition is FixedNodeDefinition or CourseEndNodeDefinition)
                .Select(n => new Choice<NodeId>(n.Id, $"{n.Id} — {n.Role}")));
            ExistingEnd.ItemsSource = endChoices;
            ExistingEnd.SelectedIndex = 0;
            AddRootButton.IsEnabled = block.Courses.Count == 0;
            AddChildButton.IsEnabled = block.Courses.Count > 0;
            Drawing.SetDrawing(record, block, _solve, selectedId);
            var diagnostics = ProjectValidator.Validate(_session.History.Project)
                .Concat(_solve.Diagnostics).Concat(_session.FileDiagnostics).Concat(_editDiagnostics)
                .Distinct().Select(d => new DiagnosticRow(d.Severity.ToString(), d.Code,
                    string.Join(" ", new[] { d.EntityType, d.EntityId }.Where(s => !string.IsNullOrEmpty(s))), d.Message));
            DiagnosticsTable.ItemsSource = diagnostics.ToArray();
            Title = $"DEED — {(_session.Path ?? "Untitled")}{(_session.IsDirty ? " *" : "")}";
        }
        finally { _refreshing = false; }
        Parent_SelectionChanged(ParentCourse, null!);
        AlongHost_SelectionChanged(AlongHost, null!);
        LoadSelectedCourse();
    }

    private void LoadSelectedCourse()
    {
        if (CourseTable.SelectedItem is not CourseRow row) { EditAlongPoint.ItemsSource = null; return; }
        var c = Current().Record.Courses[row.Id];
        RecordedCall.Text = c.OriginalRecordedText;
        Bearing.Text = c.ParsedBearing?.OriginalText ?? "";
        RecordedDistance.Text = c.RecordedDistance?.Value.ToString("G", CultureInfo.InvariantCulture) ?? "";
        DraftedDistance.Text = c.DraftedCompletion?.Distance.Value.ToString("G", CultureInfo.InvariantCulture) ?? "";
        DraftedReason.Text = c.DraftedCompletion?.Reason ?? "";
        FinalPart.Text = c.FinalPart?.Value.ToString("G", CultureInfo.InvariantCulture) ?? "";
        var record = Current().Record;
        EditAlongPoint.ItemsSource = c.AlongPoints.Select(p =>
            new Choice<NodeId>(p.NodeId, $"{p.NodeId} — {record.Nodes[p.NodeId].Role}")) .ToArray();
        EditAlongPoint.SelectedIndex = c.AlongPoints.Count > 0 ? 0 : -1;
        EditAlongDistance.Text = c.AlongPoints.Count > 0
            ? c.AlongPoints[0].FromPrevious.Value.ToString("G", CultureInfo.InvariantCulture) : "";
    }

    private void CourseTable_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        LoadSelectedCourse();
        var (_, _, record, block, _) = Current();
        if (_solve is not null) Drawing.SetDrawing(record, block, _solve, (CourseTable.SelectedItem as CourseRow)?.Id);
    }

    private void Parent_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ParentCourse.SelectedItem is not Choice<CourseId> choice) { StartPoint.ItemsSource = null; return; }
        var c = Current().Record.Courses[choice.Id];
        var record = Current().Record;
        StartPoint.ItemsSource = new[] { c.FromNodeId, c.ToNodeId }
            .Concat(c.AlongPoints.Select(x => x.NodeId)).Distinct()
            .Select(id => new Choice<NodeId>(id, $"{id} — {record.Nodes[id].Role}")) .ToArray();
        StartPoint.SelectedIndex = 1;
    }

    private void AlongHost_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AlongHost.SelectedItem is not Choice<CourseId> choice) { AlongPosition.ItemsSource = null; return; }
        var count = Current().Record.Courses[choice.Id].AlongPoints.Count;
        AlongPosition.ItemsSource = Enumerable.Range(1, count + 1).ToArray();
        AlongPosition.SelectedIndex = count;
    }

    private void EditAlongPoint_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || CourseTable.SelectedItem is not CourseRow row ||
            EditAlongPoint.SelectedItem is not Choice<NodeId> point) return;
        var course = Current().Record.Courses[row.Id];
        EditAlongDistance.Text = course.AlongPoints.First(p => p.NodeId == point.Id)
            .FromPrevious.Value.ToString("G", CultureInfo.InvariantCulture);
    }

    private static Distance? OptionalDistance(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            throw new FormatException("Enter a valid decimal distance using a period.");
        var distance = Distance.TryCreate(number);
        if (!distance.IsSuccess) throw new FormatException(distance.Diagnostic!.Value.Message);
        return distance.Value;
    }

    private static Monument? OptionalMonument(string description, string evidence)
    {
        if (string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(evidence)) return null;
        var result = Monument.TryCreate(description, string.IsNullOrWhiteSpace(evidence) ? null : evidence);
        if (!result.IsSuccess) throw new FormatException(result.Diagnostic!.Message);
        return result.Value;
    }

    private CourseInput ReadInput()
    {
        var recorded = OptionalDistance(RecordedDistance.Text);
        var drafted = OptionalDistance(DraftedDistance.Text);
        return new(RecordedCall.Text, Bearing.Text, recorded,
            drafted is { } value ? new(value, DraftedReason.Text) : null,
            OptionalDistance(FinalPart.Text), EndpointRole.Text,
            OptionalMonument(EndpointMonument.Text, EndpointEvidence.Text));
    }

    private void Apply(Func<DeedProject, ProjectEditResult> command)
    {
        try
        {
            var result = _session.Apply(command);
            _editDiagnostics = result.Diagnostics;
            Refresh();
        }
        catch (FormatException ex) { InputError(ex.Message); }
    }

    private void InputError(string message)
    {
        _editDiagnostics = new[] { new Diagnostic(AuthoringDiagnosticCodes.InvalidInput,
            DiagnosticSeverity.Error, message) };
        Refresh();
    }

    private void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!double.TryParse(PobE.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var easting) ||
                !double.TryParse(PobN.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var northing))
                throw new FormatException("POB coordinates must be decimal numbers.");
            var input = ReadInput();
            var monument = OptionalMonument(PobMonument.Text, PobEvidence.Text);
            var (recordId, blockId, _, _, typeId) = Current();
            Apply(project => ProjectAuthoring.AddRoot(project, recordId, blockId,
                new(easting, northing), PobRole.Text, monument, input, typeId));
            if (_session.History.Project.Records[recordId].Courses.Count == 1) Drawing.FitDrawing();
        }
        catch (FormatException ex) { InputError(ex.Message); }
    }

    private void AddChild_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ParentCourse.SelectedItem is not Choice<CourseId> parent ||
                StartPoint.SelectedItem is not Choice<NodeId> start)
                throw new FormatException("Choose a parent course and start point.");
            var input = ReadInput();
            var existing = ExistingEnd.SelectedItem is Choice<NodeId> end && end.Id != default ? end.Id : (NodeId?)null;
            var (recordId, blockId, _, _, typeId) = Current();
            Apply(project => ProjectAuthoring.AddChild(project, recordId, blockId,
                parent.Id, start.Id, existing, input, typeId));
        }
        catch (FormatException ex) { InputError(ex.Message); }
    }

    private void AddAlong_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (AlongHost.SelectedItem is not Choice<CourseId> host ||
                AlongPosition.SelectedItem is not int position ||
                OptionalDistance(AlongDistance.Text) is not { } distance)
                throw new FormatException("Choose a host, insertion position, and distance.");
            var monument = OptionalMonument(AlongMonument.Text, AlongEvidence.Text);
            var (recordId, blockId, _, _, _) = Current();
            Apply(project => ProjectAuthoring.AddAlongPoint(project, recordId, blockId,
                host.Id, position - 1, distance, AlongRole.Text, monument));
        }
        catch (FormatException ex) { InputError(ex.Message); }
    }

    private void EditCourse_Click(object sender, RoutedEventArgs e)
    {
        if (CourseTable.SelectedItem is not CourseRow row) return;
        try
        {
            var input = ReadInput();
            var recordId = Current().RecordId;
            Apply(project => ProjectEdits.EditLineInputs(project, recordId, row.Id,
                input.RecordedCall, input.BearingText, input.RecordedDistance,
                input.DraftedCompletion, input.FinalPart));
        }
        catch (FormatException ex) { InputError(ex.Message); }
    }

    private void EditPart_Click(object sender, RoutedEventArgs e)
    {
        if (CourseTable.SelectedItem is not CourseRow row ||
            EditAlongPoint.SelectedItem is not Choice<NodeId> point) return;
        try
        {
            var distance = OptionalDistance(EditAlongDistance.Text) ??
                throw new FormatException("Enter a fromPrevious distance.");
            var recordId = Current().RecordId;
            Apply(project => ProjectEdits.EditAlongDistance(project, recordId, row.Id, point.Id, distance));
        }
        catch (FormatException ex) { InputError(ex.Message); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (CourseTable.SelectedItem is not CourseRow row) return;
        var recordId = Current().RecordId;
        var result = _session.Apply(p => ProjectEdits.DeleteCourse(p, recordId, row.Id));
        _editDiagnostics = result.Diagnostics;
        Refresh();
        if (result.DeletionReport is not { } report) return;
        var answer = MessageBox.Show(this,
            $"Deleted courses: {string.Join(", ", report.DeletedCourseIds)}\n" +
            $"Deleted nodes: {string.Join(", ", report.DeletedNodeIds)}\n" +
            $"Preserved starts: {string.Join(", ", report.PreservedParentOwnedStartingNodeIds)}\n" +
            $"Reassigned endpoints: {string.Join(", ", report.ReassignedEndpoints.Select(x => x.NodeId))}\n\nUndo this deletion?",
            "Cascade deletion report", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes) { _session.Undo(); Refresh(); }
    }

    private bool ConfirmDiscard() => !_session.IsDirty || MessageBox.Show(this,
        "Discard unsaved changes?", "Unsaved changes", MessageBoxButton.YesNo,
        MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        _session.New(); _editDiagnostics = Array.Empty<Diagnostic>(); Refresh(); Drawing.FitDrawing();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dialog = new OpenFileDialog { Filter = "DEED project (*.deed.json)|*.deed.json|JSON files (*.json)|*.json",
            DefaultExt = ".deed.json" };
        if (dialog.ShowDialog(this) != true) return;
        _session.Open(dialog.FileName);
        _editDiagnostics = Array.Empty<Diagnostic>(); Refresh(); Drawing.FitDrawing();
    }

    private bool SaveTo(string? path)
    {
        if (path is null)
        {
            var dialog = new SaveFileDialog { Filter = "DEED project (*.deed.json)|*.deed.json",
                DefaultExt = ".deed.json", AddExtension = true, OverwritePrompt = true };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }
        var result = _session.Save(path);
        Refresh();
        return result;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveTo(_session.Path);
    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveTo(null);
    private void Undo_Click(object sender, RoutedEventArgs e) { if (_session.Undo()) { _editDiagnostics = Array.Empty<Diagnostic>(); Refresh(); } }
    private void Redo_Click(object sender, RoutedEventArgs e) { if (_session.Redo()) { _editDiagnostics = Array.Empty<Diagnostic>(); Refresh(); } }
    private void Fit_Click(object sender, RoutedEventArgs e) => Drawing.FitDrawing();
    private void Window_Closing(object sender, CancelEventArgs e) => e.Cancel = !ConfirmDiscard();
}
