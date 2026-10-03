using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deed.Core;
using Deed.Persistence;

namespace Deed.Persistence.Tests;

public sealed class ProjectFileStoreTests
{
    private static readonly byte[] Fixture = File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "StageA.json"));
    private static readonly DateTimeOffset Moment = new(2026, 4, 5, 6, 7, 8, TimeSpan.Zero);
    private static ProjectDocument Document() => StageAJson.Load(Fixture).Document!;
    private static DateTimeOffset At(int n) => Moment.AddMinutes(n);
    private static RecordId RecordId1 => RecordId.TryParse("rec-00001").Value;
    private static NodeId NodeId1 => NodeId.TryParse("n-00001").Value;
    private static ProjectDocument DocumentWithSecondRecord()
    {
        var original = Document();
        var secondId = RecordId.TryParse("rec-00002").Value;
        var records = original.Project.Records.ToDictionary(x => x.Key, x => x.Value);
        records[secondId] = new DeedRecord(secondId, new Dictionary<NodeId, DeedNode>(),
            new Dictionary<CourseId, StraightCourse>(), new Dictionary<BlockId, DraftingBlock>());
        var project = new DeedProject(original.Project.Settings,
            original.Project.DraftingTypes.ToDictionary(x => x.Key, x => x.Value), records,
            original.Project.IdCounters with { Rec = 2 });
        return original with { Project = project };
    }

    [Fact]
    public void FirstSavePreservesCreatedAndRoundTripsUnknownFields()
    {
        using var dir = new TempDirectory();
        var store = new ProjectFileStore();
        var source = Document();
        var saved = store.Save(dir.ProjectPath, source, Moment);
        Assert.True(saved.IsSuccess);
        Assert.Equal(source.Envelope.Created, saved.Document!.Envelope.Created);
        Assert.Equal(Moment, saved.Document.Envelope.Modified);
        Assert.Empty(saved.BackupPaths);
        var reopened = store.Load(dir.ProjectPath);
        Assert.True(reopened.IsSuccess);
        Assert.Equal(Moment, reopened.Document!.Envelope.Modified);
        Assert.Equal(source.Project.Records[RecordId1].Nodes[NodeId1].Monument,
            reopened.Document.Project.Records[RecordId1].Nodes[NodeId1].Monument);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(source.Preservation.UnknownFields["$"]["futureEnvelope"]),
            JsonNode.Parse(reopened.Document.Preservation.UnknownFields["$"]["futureEnvelope"])));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(source.Preservation.UnknownFields["$/records/rec-00001/nodes/n-00001/monument"]["ext"]),
            JsonNode.Parse(reopened.Document.Preservation.UnknownFields["$/records/rec-00001/nodes/n-00001/monument"]["ext"])));
        Assert.Empty(dir.Temps());
    }

    [Fact]
    public void BackupsAreOrderedRetainedAndExplicitlyReadOnly()
    {
        using var dir = new TempDirectory();
        var store = new ProjectFileStore();
        for (int i = 0; i < 8; i++) Assert.True(store.Save(dir.ProjectPath, Document(), At(i)).IsSuccess);
        var paths = store.ListBackups(dir.ProjectPath).Paths;
        Assert.Equal(5, paths.Count);
        Assert.Equal(Enumerable.Range(1, 5).Select(i => dir.ProjectPath + ".bak" + i), paths);
        byte[] authoritative = File.ReadAllBytes(dir.ProjectPath);
        for (int i = 1; i <= 5; i++)
        {
            var backup = store.LoadBackup(dir.ProjectPath, i);
            Assert.True(backup.IsSuccess);
            Assert.Equal(At(7 - i), backup.Document!.Envelope.Modified);
            Assert.Equal(authoritative, File.ReadAllBytes(dir.ProjectPath));
        }
        Assert.Empty(dir.Temps());
    }

    [Fact]
    public void SerializationFailureTouchesNoFiles()
    {
        using var dir = new TempDirectory();
        var store = new ProjectFileStore();
        Assert.True(store.Save(dir.ProjectPath, Document(), At(0)).IsSuccess);
        Assert.True(store.Save(dir.ProjectPath, Document(), At(1)).IsSuccess);
        var before = dir.Snapshot();
        var source = Document();
        var bad = source with { Envelope = source.Envelope with { AppVersion = "" } };
        var failed = store.Save(dir.ProjectPath, bad, At(2));
        Assert.False(failed.IsSuccess);
        Assert.Contains(failed.Diagnostics, d => d.Code == FileDiagnosticCodes.SerializationFailed);
        Assert.Equal(before, dir.Snapshot());
    }

    [Theory]
    [InlineData(Failure.Write, FileDiagnosticCodes.IoError)]
    [InlineData(Failure.Flush, FileDiagnosticCodes.IoError)]
    [InlineData(Failure.Backup, FileDiagnosticCodes.BackupFailed)]
    [InlineData(Failure.Replace, FileDiagnosticCodes.AtomicReplaceFailed)]
    public void FailedSaveKeepsValidAuthoritativeFile(Failure failure, string code)
    {
        using var dir = new TempDirectory();
        var files = new FaultFiles();
        var store = new ProjectFileStore(5, TimeProvider.System, files);
        Assert.True(store.Save(dir.ProjectPath, Document(), At(0)).IsSuccess);
        Assert.True(store.Save(dir.ProjectPath, Document(), At(1)).IsSuccess);
        byte[] before = File.ReadAllBytes(dir.ProjectPath);
        files.Fail = failure;
        var result = store.Save(dir.ProjectPath, Document(), At(2));
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, d => d.Code == code && d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(before, File.ReadAllBytes(dir.ProjectPath));
        Assert.Equal(At(1), new ProjectFileStore().Load(dir.ProjectPath).Document!.Envelope.Modified);
        Assert.Empty(dir.Temps());
    }

    [Fact]
    public void CleanupFailureDoesNotHideOriginalFailure()
    {
        using var dir = new TempDirectory();
        var files = new FaultFiles { Fail = Failure.Write | Failure.Cleanup };
        var result = new ProjectFileStore(5, TimeProvider.System, files).Save(dir.ProjectPath, Document(), Moment);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, d => d.Code == FileDiagnosticCodes.IoError);
        Assert.Contains(result.Diagnostics, d => d.Code == FileDiagnosticCodes.TempCleanupFailed);
        files.Fail = Failure.None;
    }

    [Fact]
    public void MissingInvalidAndBackupCandidatesNeverChangeMainFile()
    {
        using var dir = new TempDirectory();
        var store = new ProjectFileStore();
        Assert.Contains(store.Load(dir.ProjectPath).Diagnostics, d => d.Code == FileDiagnosticCodes.NotFound);
        Assert.True(store.Save(dir.ProjectPath, Document(), At(0)).IsSuccess);
        Assert.True(store.Save(dir.ProjectPath, Document(), At(1)).IsSuccess);
        File.WriteAllText(dir.ProjectPath, "{broken", Encoding.UTF8);
        byte[] before = File.ReadAllBytes(dir.ProjectPath);
        var invalid = store.Load(dir.ProjectPath);
        Assert.False(invalid.IsSuccess);
        Assert.Contains(invalid.Diagnostics, d => d.Code == PersistenceDiagnosticCodes.InvalidJson);
        Assert.Single(invalid.BackupPaths);
        Assert.True(store.LoadBackup(dir.ProjectPath, 1).IsSuccess);
        Assert.Equal(before, File.ReadAllBytes(dir.ProjectPath));
    }

    [Fact]
    public void StructurallyInvalidProjectIsNotRewritten()
    {
        using var dir = new TempDirectory();
        var root = JsonNode.Parse(Fixture)!.AsObject();
        root["settings"]!["tolerances"]!["coincidenceDistance"] = 0;
        File.WriteAllText(dir.ProjectPath, root.ToJsonString());
        byte[] before = File.ReadAllBytes(dir.ProjectPath);
        var result = new ProjectFileStore().Load(dir.ProjectPath);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, d => d.Code == PersistenceDiagnosticCodes.InvalidValue);
        Assert.Equal(before, File.ReadAllBytes(dir.ProjectPath));
    }

    [Theory]
    [InlineData(Failure.ReadDenied, FileDiagnosticCodes.AccessDenied)]
    [InlineData(Failure.ReadIo, FileDiagnosticCodes.IoError)]
    public void LoadClassifiesFilesystemFailures(Failure failure, string code)
    {
        using var dir = new TempDirectory();
        File.WriteAllBytes(dir.ProjectPath, Fixture);
        var files = new FaultFiles { Fail = failure };
        var result = new ProjectFileStore(5, TimeProvider.System, files).Load(dir.ProjectPath);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, d => d.Code == code && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void PathsResolveAndMissingParentIsNotCreated()
    {
        using var dir = new TempDirectory();
        var store = new ProjectFileStore();
        string missing = Path.Combine(dir.Path, "missing", "project.deed.json");
        var failed = store.Save(missing, Document(), Moment);
        Assert.Contains(failed.Diagnostics, d => d.Code == FileDiagnosticCodes.InvalidPath);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, dir.ProjectPath);
        Assert.True(store.Save(relative, Document(), Moment).IsSuccess);
        Assert.True(store.Load(relative).IsSuccess);
    }

    [Fact]
    public void CachePathAndFreshSolveAreStable()
    {
        using var dir = new TempDirectory();
        Assert.Equal(Path.Combine(dir.Path, "project.deed.cache.json"), ProjectCachePath.ForProject(dir.ProjectPath));
        Assert.Equal(Path.Combine(dir.Path, "other.txt.cache.json"),
            ProjectCachePath.ForProject(Path.Combine(dir.Path, "other.txt")));
        var store = new ProjectFileStore();
        Assert.True(store.Save(dir.ProjectPath, Document(), Moment).IsSuccess);
        string cache = ProjectCachePath.ForProject(dir.ProjectPath);
        using (var parsed = JsonDocument.Parse(File.ReadAllBytes(cache)))
        {
            Assert.Equal("DEED-CACHE", parsed.RootElement.GetProperty("format").GetString());
            Assert.Equal(1, parsed.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("0.1.0", parsed.RootElement.GetProperty("solverVersion").GetString());
            Assert.False(parsed.RootElement.TryGetProperty("diagnostics", out _));
            Assert.Equal(new[] { "rec-00001" }, parsed.RootElement.GetProperty("records")
                .EnumerateObject().Select(p => p.Name));
            Assert.Equal(Enumerable.Range(1, 8).Select(n => $"n-{n:00000}"),
                parsed.RootElement.GetProperty("records").GetProperty("rec-00001")
                    .GetProperty("nodes").EnumerateObject().Select(p => p.Name));
            var cachedNodes = parsed.RootElement.GetProperty("records").GetProperty("rec-00001")
                .GetProperty("nodes");
            double diagonal = 1 / Math.Sqrt(2);
            foreach (var (id, distance) in new[]
            {
                ("n-00005", 86.25), ("n-00006", 136.25), ("n-00007", 186.25)
            })
            {
                Assert.Equal(1000 + distance * diagonal, cachedNodes.GetProperty(id)[0].GetDouble(), 9);
                Assert.Equal(2000 + distance * diagonal, cachedNodes.GetProperty(id)[1].GetDouble(), 9);
            }
            Assert.Equal(1040 + 136.25 * diagonal,
                cachedNodes.GetProperty("n-00008")[0].GetDouble(), 9);
            Assert.Equal(2000 + 136.25 * diagonal,
                cachedNodes.GetProperty("n-00008")[1].GetDouble(), 9);
        }
        var matching = store.Load(dir.ProjectPath);
        Assert.True(matching.IsSuccess);
        Assert.Equal(1000, matching.FreshCoordinates[RecordId1][NodeId1].Easting);
        var baseline = matching.FreshCoordinates[RecordId1];
        foreach (int number in new[] { 5, 6, 7, 8 })
            Assert.True(baseline.ContainsKey(NodeId.TryParse($"n-{number:00000}").Value));
        File.WriteAllText(cache, "{broken");
        var corruptCache = store.Load(dir.ProjectPath);
        Assert.True(corruptCache.IsSuccess);
        Assert.Contains(corruptCache.Diagnostics, d => d.Code == CacheDiagnosticCodes.Invalid);
        foreach (int number in new[] { 5, 6, 7, 8 })
        {
            var id = NodeId.TryParse($"n-{number:00000}").Value;
            Assert.Equal(baseline[id], corruptCache.FreshCoordinates[RecordId1][id]);
        }
        File.Delete(cache);
        var missingCache = store.Load(dir.ProjectPath);
        Assert.True(missingCache.IsSuccess);
        Assert.DoesNotContain(missingCache.Diagnostics, d => d.Code.StartsWith("cache.", StringComparison.Ordinal));
        foreach (int number in new[] { 5, 6, 7, 8 })
        {
            var id = NodeId.TryParse($"n-{number:00000}").Value;
            Assert.Equal(baseline[id], missingCache.FreshCoordinates[RecordId1][id]);
        }
        Assert.Empty(dir.Temps());
    }

    [Theory]
    [InlineData("empty_records", CacheDiagnosticCodes.Invalid)]
    [InlineData("missing_record", CacheDiagnosticCodes.Invalid)]
    [InlineData("extra_record", CacheDiagnosticCodes.Invalid)]
    [InlineData("duplicate_record", CacheDiagnosticCodes.Invalid)]
    [InlineData("empty_nodes", CacheDiagnosticCodes.Invalid)]
    [InlineData("missing_node", CacheDiagnosticCodes.Invalid)]
    [InlineData("extra_node", CacheDiagnosticCodes.Invalid)]
    [InlineData("duplicate_node", CacheDiagnosticCodes.Invalid)]
    [InlineData("small_coordinate_change", CacheDiagnosticCodes.CoordinateMismatch)]
    public void CacheIntegrityRequiresCompleteIdsAndExactCoordinates(string mode, string expectedCode)
    {
        using var dir = new TempDirectory();
        var store = new ProjectFileStore();
        var source = mode == "missing_record" ? DocumentWithSecondRecord() : Document();
        Assert.True(store.Save(dir.ProjectPath, source, Moment).IsSuccess);
        byte[] authoritative = File.ReadAllBytes(dir.ProjectPath);
        string cachePath = ProjectCachePath.ForProject(dir.ProjectPath);
        var root = JsonNode.Parse(File.ReadAllText(cachePath))!.AsObject();
        var records = root["records"]!.AsObject();
        var nodes = records["rec-00001"]!["nodes"]!.AsObject();

        switch (mode)
        {
            case "empty_records": records.Clear(); break;
            case "missing_record": records.Remove("rec-00002"); break;
            case "extra_record": records["rec-00002"] = JsonNode.Parse("{\"nodes\":{}}"); break;
            case "empty_nodes": nodes.Clear(); break;
            case "missing_node": nodes.Remove("n-00001"); break;
            case "extra_node": nodes["n-99999"] = JsonNode.Parse("[0,0]"); break;
            case "small_coordinate_change":
                double original = nodes["n-00001"]![0]!.GetValue<double>();
                double changed = original + source.Project.Settings.CoincidenceDistance / 10;
                Assert.True(changed != original);
                Assert.True(changed - original < source.Project.Settings.CoincidenceDistance);
                nodes["n-00001"]![0] = changed;
                break;
        }

        string edited = root.ToJsonString();
        if (mode == "duplicate_record")
            edited = edited.Replace("\"records\":{",
                "\"records\":{\"rec-00001\":" + records["rec-00001"]!.ToJsonString() + ",",
                StringComparison.Ordinal);
        if (mode == "duplicate_node")
            edited = edited.Replace("\"nodes\":{",
                "\"nodes\":{\"n-00001\":" + nodes["n-00001"]!.ToJsonString() + ",",
                StringComparison.Ordinal);
        File.WriteAllText(cachePath, edited);

        var loaded = store.Load(dir.ProjectPath);
        Assert.True(loaded.IsSuccess);
        Assert.Contains(loaded.Diagnostics, d => d.Code == expectedCode && d.Severity == DiagnosticSeverity.Warning);
        Assert.Equal(authoritative, File.ReadAllBytes(dir.ProjectPath));
        var expected = ProjectCache.SolveFresh(loaded.Document!);
        Assert.Equal(expected.Keys.OrderBy(x => x.ToString()), loaded.FreshCoordinates.Keys.OrderBy(x => x.ToString()));
        foreach (var (recordId, freshNodes) in expected)
        {
            Assert.Equal(freshNodes.Keys.OrderBy(x => x.ToString()),
                loaded.FreshCoordinates[recordId].Keys.OrderBy(x => x.ToString()));
            foreach (var (nodeId, coordinate) in freshNodes)
                Assert.Equal(coordinate, loaded.FreshCoordinates[recordId][nodeId]);
        }
    }

    [Theory]
    [InlineData("invalid", CacheDiagnosticCodes.Invalid)]
    [InlineData("schema", CacheDiagnosticCodes.UnsupportedSchema)]
    [InlineData("solver", CacheDiagnosticCodes.SolverVersionMismatch)]
    [InlineData("stale", CacheDiagnosticCodes.Stale)]
    [InlineData("coordinate", CacheDiagnosticCodes.CoordinateMismatch)]
    public void BadCacheOnlyWarns(string mode, string code)
    {
        using var dir = new TempDirectory();
        var store = new ProjectFileStore();
        Assert.True(store.Save(dir.ProjectPath, Document(), Moment).IsSuccess);
        string path = ProjectCachePath.ForProject(dir.ProjectPath);
        if (mode == "invalid") File.WriteAllText(path, "{broken");
        else
        {
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (mode == "schema") root["schemaVersion"] = 2;
            if (mode == "solver") root["solverVersion"] = "9.9.9";
            if (mode == "stale") root["projectModified"] = "2000-01-01T00:00:00Z";
            if (mode == "coordinate") root["records"]!["rec-00001"]!["nodes"]!["n-00001"]![0] = 999999;
            File.WriteAllText(path, root.ToJsonString());
        }
        var loaded = store.Load(dir.ProjectPath);
        Assert.True(loaded.IsSuccess);
        Assert.Contains(loaded.Diagnostics, d => d.Code == code && d.Severity == DiagnosticSeverity.Warning);
        Assert.Equal(1000, loaded.FreshCoordinates[RecordId1][NodeId1].Easting);
    }

    [Fact]
    public void CacheWriteFailureDoesNotInvalidateAuthoritativeSave()
    {
        using var dir = new TempDirectory();
        var files = new FaultFiles { Fail = Failure.CacheWrite };
        var result = new ProjectFileStore(5, TimeProvider.System, files).Save(dir.ProjectPath, Document(), Moment);
        Assert.True(result.IsSuccess);
        Assert.Contains(result.Diagnostics, d => d.Code == CacheDiagnosticCodes.WriteFailed && d.Severity == DiagnosticSeverity.Warning);
        Assert.True(new ProjectFileStore().Load(dir.ProjectPath).IsSuccess);
        Assert.Empty(dir.Temps());
    }

    [Flags]
    public enum Failure { None = 0, Write = 1, Flush = 2, Backup = 4, Replace = 8, Cleanup = 16,
        CacheWrite = 32, ReadDenied = 64, ReadIo = 128 }

    private sealed class FaultFiles : IProjectFileSystem
    {
        private readonly RealProjectFileSystem _real = new();
        public Failure Fail { get; set; }
        public bool DirectoryExists(string path) => _real.DirectoryExists(path);
        public bool FileExists(string path) => _real.FileExists(path);
        public byte[] ReadAllBytes(string path)
        {
            if (path.EndsWith(".deed.json", StringComparison.Ordinal))
            {
                if (Fail.HasFlag(Failure.ReadDenied)) throw new UnauthorizedAccessException();
                if (Fail.HasFlag(Failure.ReadIo)) throw new IOException();
            }
            return _real.ReadAllBytes(path);
        }
        public void WriteNewAndFlush(string path, byte[] bytes)
        {
            bool cache = path.Contains(".cache.json.", StringComparison.Ordinal);
            if ((cache && Fail.HasFlag(Failure.CacheWrite)) || (!cache && (Fail.HasFlag(Failure.Write) || Fail.HasFlag(Failure.Flush))))
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(bytes.AsSpan(0, Fail.HasFlag(Failure.Write) ? Math.Min(bytes.Length, 5) : bytes.Length));
                if (Fail.HasFlag(Failure.Flush) && !cache)
                    throw new IOException("Injected flush failure");
                throw new IOException(cache ? "Injected cache write failure" : "Injected write or flush failure");
            }
            _real.WriteNewAndFlush(path, bytes);
        }
        public void Move(string source, string destination, bool overwrite)
        {
            if (Fail.HasFlag(Failure.Backup) && destination.EndsWith(".bak2", StringComparison.Ordinal))
                throw new IOException("Injected backup failure");
            _real.Move(source, destination, overwrite);
        }
        public void Replace(string source, string destination, string? backup)
        {
            if (Fail.HasFlag(Failure.Replace) && backup is not null)
                throw new IOException("Injected replacement failure");
            _real.Replace(source, destination, backup);
        }
        public void Delete(string path)
        {
            if (Fail.HasFlag(Failure.Cleanup) && path.EndsWith(".tmp", StringComparison.Ordinal))
                throw new IOException("Injected cleanup failure");
            _real.Delete(path);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "deed-file-tests-" + Guid.NewGuid().ToString("N"));
        public string ProjectPath => System.IO.Path.Combine(Path, "project.deed.json");
        public TempDirectory() => Directory.CreateDirectory(Path);
        public string[] Temps() => Directory.GetFiles(Path, "*.tmp", SearchOption.TopDirectoryOnly);
        public string Snapshot() => string.Join("|", Directory.GetFiles(Path).Order(StringComparer.Ordinal)
            .Select(p => System.IO.Path.GetFileName(p) + ":" + Convert.ToBase64String(File.ReadAllBytes(p))));
        public void Dispose()
        {
            string root = System.IO.Path.GetFullPath(Path);
            string temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (!root.StartsWith(temp.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(root).StartsWith("deed-file-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe test cleanup target.");
            Directory.Delete(root, recursive: true);
        }
    }
}
