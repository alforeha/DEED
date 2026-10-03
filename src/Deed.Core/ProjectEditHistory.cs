namespace Deed.Core;

/// <summary>Session-only history of complete immutable project snapshots.</summary>
public sealed class ProjectEditHistory
{
    private readonly Stack<DeedProject> _undo = new();
    private readonly Stack<DeedProject> _redo = new();

    public ProjectEditHistory(DeedProject project) => Project = project ?? throw new ArgumentNullException(nameof(project));

    public DeedProject Project { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public ProjectEditResult Apply(Func<DeedProject, ProjectEditResult> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var result = operation(Project);
        if (result.IsSuccess && result.Changed)
        {
            _undo.Push(Project);
            Project = result.Project;
            _redo.Clear();
        }
        return result;
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        _redo.Push(Project);
        Project = _undo.Pop();
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        _undo.Push(Project);
        Project = _redo.Pop();
        return true;
    }
}
