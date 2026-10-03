using Deed.Core;
using Deed.Persistence;

namespace Deed.Desktop;

/// <summary>File and edit state; input fields remain outside the persisted project.</summary>
public sealed class WorkspaceSession
{
    private readonly ProjectFileStore _store;
    private DeedProject _saved;
    public WorkspaceSession(ProjectFileStore? store = null)
    {
        _store = store ?? new ProjectFileStore();
        History = new(ProjectAuthoring.CreateStageAProject());
        _saved = History.Project;
        Document = NewDocument(History.Project);
    }
    public ProjectEditHistory History { get; private set; }
    public ProjectDocument Document { get; private set; }
    public string? Path { get; private set; }
    public bool IsDirty => !ReferenceEquals(History.Project, _saved);
    public IReadOnlyList<Diagnostic> FileDiagnostics { get; private set; } = Array.Empty<Diagnostic>();

    public void New()
    {
        var project = ProjectAuthoring.CreateStageAProject();
        History = new(project);
        _saved = project;
        Document = NewDocument(project);
        Path = null;
        FileDiagnostics = Array.Empty<Diagnostic>();
    }

    public bool Open(string path)
    {
        var loaded = _store.Load(path);
        FileDiagnostics = loaded.Diagnostics;
        if (!loaded.IsSuccess) return false;
        Document = loaded.Document!;
        History = new(Document.Project);
        _saved = History.Project;
        Path = path;
        return true;
    }

    public bool Save(string? path = null)
    {
        var destination = path ?? Path;
        if (destination is null)
        {
            FileDiagnostics = new[] { new Diagnostic(FileDiagnosticCodes.InvalidPath,
                DiagnosticSeverity.Error, "Choose a project file path.") };
            return false;
        }
        var result = _store.Save(destination, Document with { Project = History.Project });
        FileDiagnostics = result.Diagnostics;
        if (!result.IsSuccess) return false;
        Document = result.Document!;
        _saved = History.Project;
        Path = destination;
        return true;
    }

    public ProjectEditResult Apply(Func<DeedProject, ProjectEditResult> edit) => History.Apply(edit);
    public bool Undo() => History.Undo();
    public bool Redo() => History.Redo();

    private static ProjectDocument NewDocument(DeedProject project)
    {
        var now = DateTimeOffset.UtcNow;
        return new(project, new ProjectEnvelope("0.1.0", now, now));
    }
}
