using Deed.Core;

namespace Deed.Persistence;

public static class FileDiagnosticCodes
{
    public const string InvalidPath = "file.invalid_path";
    public const string NotFound = "file.not_found";
    public const string AccessDenied = "file.access_denied";
    public const string IoError = "file.io_error";
    public const string SerializationFailed = "file.serialization_failed";
    public const string TempValidationFailed = "file.temp_validation_failed";
    public const string BackupFailed = "file.backup_failed";
    public const string AtomicReplaceFailed = "file.atomic_replace_failed";
    public const string TempCleanupFailed = "file.temp_cleanup_failed";
}

public sealed record ProjectFileResult(ProjectDocument? Document, IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<string> BackupPaths)
{
    public bool IsSuccess => Document is not null && Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);
    public IReadOnlyDictionary<RecordId, IReadOnlyDictionary<NodeId, Coordinate2D>> FreshCoordinates { get; init; } =
        new Dictionary<RecordId, IReadOnlyDictionary<NodeId, Coordinate2D>>();
}

public sealed record BackupListResult(IReadOnlyList<string> Paths, IReadOnlyList<Diagnostic> Diagnostics);

internal interface IProjectFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    byte[] ReadAllBytes(string path);
    void WriteNewAndFlush(string path, byte[] bytes);
    void Move(string source, string destination, bool overwrite);
    void Replace(string source, string destination, string? backup);
    void Delete(string path);
}

internal sealed class RealProjectFileSystem : IProjectFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
    public void WriteNewAndFlush(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
    public void Move(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);
    public void Replace(string source, string destination, string? backup) => File.Replace(source, destination, backup);
    public void Delete(string path) => File.Delete(path);
}

public sealed class ProjectFileStore
{
    private readonly IProjectFileSystem _files;
    private readonly TimeProvider _clock;
    private readonly int _generations;

    public ProjectFileStore(int backupGenerations = 5, TimeProvider? timeProvider = null)
        : this(backupGenerations, timeProvider ?? TimeProvider.System, new RealProjectFileSystem()) { }

    internal ProjectFileStore(int backupGenerations, TimeProvider timeProvider, IProjectFileSystem files)
    {
        if (backupGenerations is < 3 or > 5) throw new ArgumentOutOfRangeException(nameof(backupGenerations));
        _generations = backupGenerations;
        _clock = timeProvider;
        _files = files;
    }

    public BackupListResult ListBackups(string? projectPath)
    {
        if (!TryPath(projectPath, out var path))
            return new(Array.Empty<string>(), new[] { Error(FileDiagnosticCodes.InvalidPath, "Project path is invalid.") });
        try { return new(ExistingBackups(path), Array.Empty<Diagnostic>()); }
        catch (UnauthorizedAccessException) { return new(Array.Empty<string>(), new[] { Error(FileDiagnosticCodes.AccessDenied, "Access to backups was denied.") }); }
        catch (IOException) { return new(Array.Empty<string>(), new[] { Error(FileDiagnosticCodes.IoError, "Backups could not be listed.") }); }
    }

    public ProjectFileResult Load(string? projectPath)
    {
        if (!TryPath(projectPath, out var path))
            return new(null, new[] { Error(FileDiagnosticCodes.InvalidPath, "Project path is invalid.") }, Array.Empty<string>());
        var backups = ListBackups(path);
        return Read(path, backups.Paths, backups.Diagnostics, compareCache: true);
    }

    public ProjectFileResult LoadBackup(string? projectPath, int generation)
    {
        if (!TryPath(projectPath, out var path) || generation < 1 || generation > _generations)
            return new(null, new[] { Error(FileDiagnosticCodes.InvalidPath, "Backup selection is invalid.") }, Array.Empty<string>());
        var backups = ListBackups(path);
        return Read(BackupPath(path, generation), backups.Paths, backups.Diagnostics, compareCache: false);
    }

    public ProjectFileResult Save(string? projectPath, ProjectDocument document, DateTimeOffset? utcNow = null)
    {
        var diagnostics = new List<Diagnostic>();
        ProjectDocument updated;
        ProjectSaveResult serialized;
        DateTimeOffset now;
        try
        {
            now = (utcNow ?? _clock.GetUtcNow()).ToUniversalTime();
            updated = document with { Envelope = document.Envelope with { Modified = now } };
            serialized = StageAJson.Save(updated);
            diagnostics.AddRange(serialized.Diagnostics);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException)
        {
            diagnostics.Add(Error(FileDiagnosticCodes.SerializationFailed, "Project serialization failed."));
            return new(null, diagnostics, Array.Empty<string>());
        }
        if (!serialized.IsSuccess)
        {
            diagnostics.Add(Error(FileDiagnosticCodes.SerializationFailed, "Project serialization failed."));
            return new(null, diagnostics, Array.Empty<string>());
        }
        if (!TryPath(projectPath, out var path))
            return new(null, diagnostics.Append(Error(FileDiagnosticCodes.InvalidPath, "Project path is invalid.")).ToArray(), Array.Empty<string>());
        string parent = Path.GetDirectoryName(path)!;
        if (!_files.DirectoryExists(parent))
            return new(null, diagnostics.Append(Error(FileDiagnosticCodes.InvalidPath, "Project directory does not exist.")).ToArray(), Array.Empty<string>());

        string temp = Path.Combine(parent, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            try { _files.WriteNewAndFlush(temp, serialized.Utf8Json!); }
            catch (UnauthorizedAccessException) { diagnostics.Add(Error(FileDiagnosticCodes.AccessDenied, "Temporary project file could not be written.")); return Failed(); }
            catch (IOException) { diagnostics.Add(Error(FileDiagnosticCodes.IoError, "Temporary project file could not be written or flushed.")); return Failed(); }

            try
            {
                var checkedFile = StageAJson.Load(_files.ReadAllBytes(temp));
                if (checkedFile.Document is null || checkedFile.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                {
                    diagnostics.Add(Error(FileDiagnosticCodes.TempValidationFailed, "Temporary project file failed validation."));
                    return Failed();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Error(FileDiagnosticCodes.TempValidationFailed, "Temporary project file could not be validated."));
                return Failed();
            }

            if (_files.FileExists(path))
            {
                try { RotateBackups(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(Error(FileDiagnosticCodes.BackupFailed, "Backup rotation failed."));
                    return Failed();
                }
                try { _files.Replace(temp, path, BackupPath(path, 1)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(Error(FileDiagnosticCodes.AtomicReplaceFailed, "Atomic project replacement failed."));
                    return Failed();
                }
            }
            else
            {
                try { _files.Move(temp, path, false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(Error(FileDiagnosticCodes.AtomicReplaceFailed, "Atomic project creation failed."));
                    return Failed();
                }
            }
            // Cache is derived from a fresh solve and may fail independently.
            diagnostics.AddRange(ProjectCache.Write(path, updated, _files, now));
            return new(updated, diagnostics, ExistingBackups(path));
        }
        finally
        {
            if (_files.FileExists(temp))
            {
                try { _files.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(new(FileDiagnosticCodes.TempCleanupFailed,
                        diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                        "Temporary project file could not be removed."));
                }
            }
        }

        ProjectFileResult Failed() => new(null, diagnostics, ExistingBackups(path));
    }

    private ProjectFileResult Read(string path, IReadOnlyList<string> backups, IReadOnlyList<Diagnostic> initial,
        bool compareCache)
    {
        var diagnostics = new List<Diagnostic>(initial);
        byte[] bytes;
        try { bytes = _files.ReadAllBytes(path); }
        catch (FileNotFoundException) { diagnostics.Add(Error(FileDiagnosticCodes.NotFound, "Project file was not found.")); return new(null, diagnostics, backups); }
        catch (DirectoryNotFoundException) { diagnostics.Add(Error(FileDiagnosticCodes.NotFound, "Project file was not found.")); return new(null, diagnostics, backups); }
        catch (UnauthorizedAccessException) { diagnostics.Add(Error(FileDiagnosticCodes.AccessDenied, "Project file access was denied.")); return new(null, diagnostics, backups); }
        catch (IOException) { diagnostics.Add(Error(FileDiagnosticCodes.IoError, "Project file could not be read.")); return new(null, diagnostics, backups); }
        var loaded = StageAJson.Load(bytes);
        diagnostics.AddRange(loaded.Diagnostics);
        if (loaded.Document is null || loaded.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            return new(null, diagnostics, backups);
        for (int i = 0; i < initial.Count; i++)
            diagnostics[i] = diagnostics[i] with { Severity = DiagnosticSeverity.Warning };
        if (compareCache)
            diagnostics.AddRange(ProjectCache.ReadAndCompare(path, loaded.Document, _files));
        return new(loaded.Document, diagnostics, backups)
        { FreshCoordinates = ProjectCache.SolveFresh(loaded.Document) };
    }

    private void RotateBackups(string path)
    {
        string oldest = BackupPath(path, _generations);
        if (_files.FileExists(oldest)) _files.Delete(oldest);
        for (int i = _generations - 1; i >= 1; i--)
            if (_files.FileExists(BackupPath(path, i)))
                _files.Move(BackupPath(path, i), BackupPath(path, i + 1), false);
    }

    private string[] ExistingBackups(string path) => Enumerable.Range(1, _generations)
        .Select(i => BackupPath(path, i)).Where(_files.FileExists).ToArray();
    private static string BackupPath(string path, int generation) => path + ".bak" + generation;
    private static Diagnostic Error(string code, string message) => new(code, DiagnosticSeverity.Error, message);
    private static bool TryPath(string? supplied, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(supplied)) return false;
        try { full = Path.GetFullPath(supplied); return Path.GetFileName(full).Length > 0; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
