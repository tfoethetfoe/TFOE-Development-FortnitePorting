using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FortnitePorting.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace FortnitePorting.Models.Uefn;

/// <summary>
/// Watches Fortnite Porting's export folder, waits for writes to settle, stages the files inside
/// the UEFN project, runs duplicate detection, and asks the UEFN companion script to import them.
/// Nothing here writes into a .uasset directly: UEFN's own Interchange/AssetImportTask pipeline
/// creates the assets, which is what keeps them valid island content.
/// </summary>
public sealed class UefnImportService : IDisposable
{
    private const int SettleSeconds = 2;
    private const string LedgerBackupExtension = ".corrupt";

    private static readonly HashSet<string> MeshExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".glb", ".gltf"
    };

    private static readonly HashSet<string> TextureExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".dds", ".webp",
        ".exr", ".hdr", ".pcx", ".psd", ".tif", ".tiff"
    };

    private static readonly HashSet<string> UnsupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".uemodel", ".ueanim", ".uepose", ".uasset", ".umap", ".uexp", ".ubulk"
    };

    private readonly Func<UefnImportOptions?> _optionsProvider;
    private readonly Func<UefnConflictRequest, CancellationToken, Task<EUefnConflictResolution>> _conflictResolver;

    private readonly ConcurrentDictionary<string, DateTime> _activity = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime Write, long Length)> _processed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _notifiedUnsupported = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<UefnImportJob> _queue = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly SemaphoreSlim _importLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly object _lifecycleLock = new();

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private DateTime _bridgeUnavailableUntil = DateTime.MinValue;

    public UefnImportService(
        Func<UefnImportOptions?> optionsProvider,
        Func<UefnConflictRequest, CancellationToken, Task<EUefnConflictResolution>> conflictResolver)
    {
        _optionsProvider = optionsProvider;
        _conflictResolver = conflictResolver;
    }

    public bool IsWatching { get; private set; }

    public event Action<UefnImportProgress>? ProgressChanged;
    public event Action<UefnImportHistoryEntry>? HistoryAdded;
    public event Action<string, string, EUefnNoticeSeverity>? Notice;

    public void Start(string exportFolder)
    {
        lock (_lifecycleLock)
        {
            Stop();

            if (string.IsNullOrWhiteSpace(exportFolder) || !Directory.Exists(exportFolder))
                return;

            lock (_stateLock)
            {
                _processed.Clear();
                foreach (var file in SafeEnumerate(exportFolder))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        _processed[file] = (info.LastWriteTimeUtc, info.Length);
                    }
                    catch
                    {
                        // Ignore files that disappear during the baseline scan.
                    }
                }
            }

            _activity.Clear();
            _bridgeUnavailableUntil = DateTime.MinValue;
            while (_queue.TryDequeue(out _)) { }

            _cancellation = new CancellationTokenSource();
            _watcher = new FileSystemWatcher(exportFolder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = false
            };

            _watcher.Created += (_, args) => Track(args.FullPath);
            _watcher.Changed += (_, args) => Track(args.FullPath);
            _watcher.Deleted += (sender, args) => _activity.TryRemove(args.FullPath, out _);
            _watcher.Renamed += (sender, args) =>
            {
                _activity.TryRemove(args.OldFullPath, out _);
                Track(args.FullPath);
            };
            _watcher.Error += (_, args) =>
            {
                var message = args.GetException()?.Message ?? "unknown error";
                ReportNotice("UEFN Import", $"The export watcher stopped reading changes: {message}. Restart auto import.", EUefnNoticeSeverity.Error);
            };

            // Attach all handlers before enabling events so a fast export cannot race startup.
            _watcher.EnableRaisingEvents = true;
            IsWatching = true;
            _loop = Task.Run(() => RunAsync(_cancellation.Token));
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            IsWatching = false;

            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }

            if (_cancellation is not null)
            {
                _cancellation.Cancel();
                _cancellation.Dispose();
                _cancellation = null;
            }

            _activity.Clear();
            SignalWork();
        }
    }

    public async Task<UefnImportSummary> ImportNowAsync(CancellationToken cancellationToken)
    {
        await _importLock.WaitAsync(cancellationToken);
        var entries = new List<UefnImportHistoryEntry>();
        try
        {
            var options = _optionsProvider()
                          ?? throw new InvalidOperationException("Select a valid UEFN project and export folder first.");

            Report(new UefnImportProgress
            {
                IsScanning = true,
                Status = $"Scanning {options.ExportFolder}..."
            });

            await RetryPendingAsync(options, entries, cancellationToken);

            var project = UefnProject.Reopen(options.Project);
            var pendingSources = GetPendingPrimarySources(project);
            var jobs = ScanExportFolder(options, out var ignoredUnsupported)
                .Where(job => !pendingSources.Contains(Path.GetFullPath(job.PrimaryFile)))
                .ToList();
            if (ignoredUnsupported > 0)
            {
                ReportNotice("UEFN Import",
                    $"{ignoredUnsupported} Unreal-native export file(s) were ignored. UEFN's Python importer cannot read .uemodel/.ueanim/.uasset files; set Fortnite Porting's Mesh Format to glTF 2.0 (binary) and export again.",
                    EUefnNoticeSeverity.Warning);
            }

            if (jobs.Count == 0)
            {
                Report(new UefnImportProgress { Status = "No supported assets found in the export folder." });
                ReportNotice("UEFN Import", "No supported .glb/.gltf or texture files were found in the export folder.", EUefnNoticeSeverity.Informational);
                return BuildSummary(entries);
            }

            var completed = 0;
            foreach (var job in jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Report(new UefnImportProgress
                {
                    IsImporting = true,
                    Completed = completed,
                    Total = jobs.Count,
                    Status = $"Importing {job.AssetName}..."
                });

                var entry = await ImportJobAsync(job, options, cancellationToken);
                if (entry is not null)
                    entries.Add(entry);

                completed++;
                Report(new UefnImportProgress
                {
                    IsImporting = completed < jobs.Count,
                    Completed = completed,
                    Total = jobs.Count,
                    Status = completed < jobs.Count ? $"Imported {completed} of {jobs.Count}" : "Import finished"
                });
            }

            return BuildSummary(entries);
        }
        finally
        {
            Report(new UefnImportProgress { Status = "Idle" });
            _importLock.Release();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                await CollectReadyJobsAsync(cancellationToken);
                await DrainQueueAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                Log.Error(e, "UEFN auto import loop failed");
            }
        }
    }

    private async Task CollectReadyJobsAsync(CancellationToken cancellationToken)
    {
        var options = _optionsProvider();
        if (options is null) return;

        var now = DateTime.UtcNow;
        var ready = new List<(string Path, DateTime Seen)>();
        foreach (var pair in _activity.ToArray())
        {
            if (!File.Exists(pair.Key))
            {
                _activity.TryRemove(pair.Key, out _);
                continue;
            }

            if (now - pair.Value < TimeSpan.FromSeconds(SettleSeconds))
                continue;

            if (!IsFileReady(pair.Key))
                continue;

            ready.Add((pair.Key, pair.Value));
        }

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = ready
            .OrderByDescending(item => MeshExtensions.Contains(Path.GetExtension(item.Path)))
            .ThenBy(item => item.Path)
            .ToArray();

        foreach (var (path, _) in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extension = Path.GetExtension(path);
            if (!MeshExtensions.Contains(extension))
                continue;

            var dependencies = GetGltfDependencies(path, options.ExportFolder);
            var files = new[] { path }.Concat(dependencies)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var dependencyStillWriting = files
                .Where(file => !file.Equals(path, StringComparison.OrdinalIgnoreCase))
                .Any(file => _activity.ContainsKey(file) && !ready.Any(item => item.Path.Equals(file, StringComparison.OrdinalIgnoreCase)));

            if (dependencyStillWriting || files.Any(file => !File.Exists(file) || !IsFileReady(file)))
                continue;

            var stem = Path.GetFileNameWithoutExtension(path);
            var job = new UefnImportJob
            {
                Id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}",
                Kind = EUefnImportKind.Mesh,
                PackageName = BasePackageName(stem),
                AssetName = SanitizeAssetName(stem),
                PrimaryFile = path,
                Files = files.ToList()
            };

            foreach (var file in files)
            {
                _activity.TryRemove(file, out _);
                claimed.Add(file);
                MarkProcessed(file);
            }

            _queue.Enqueue(job);
        }

        foreach (var (path, _) in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extension = Path.GetExtension(path);
            if (claimed.Contains(path)) continue;

            if (TextureExtensions.Contains(extension))
            {
                if (!options.ImportTextures || IsReferencedByPendingGltf(path, options.ExportFolder))
                {
                    _activity.TryRemove(path, out _);
                    MarkProcessed(path);
                    continue;
                }

                var job = new UefnImportJob
                {
                    Id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}",
                    Kind = EUefnImportKind.Texture,
                    PackageName = BasePackageName(Path.GetFileNameWithoutExtension(path)),
                    AssetName = SanitizeAssetName(Path.GetFileNameWithoutExtension(path)),
                    PrimaryFile = path,
                    Files = [path]
                };

                _activity.TryRemove(path, out _);
                MarkProcessed(path);
                _queue.Enqueue(job);
                continue;
            }

            if (UnsupportedExtensions.Contains(extension))
            {
                _activity.TryRemove(path, out _);
                MarkProcessed(path);

                if (!_notifiedUnsupported.Add(path)) continue;

                var job = new UefnImportJob
                {
                    Id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}",
                    Kind = EUefnImportKind.Unsupported,
                    PackageName = BasePackageName(Path.GetFileNameWithoutExtension(path)),
                    AssetName = SanitizeAssetName(Path.GetFileNameWithoutExtension(path)),
                    PrimaryFile = path,
                    UnsupportedReason = UnsupportedReason(extension)
                };
                _queue.Enqueue(job);
                continue;
            }

            if (extension.Equals(".bin", StringComparison.OrdinalIgnoreCase))
            {
                // Buffers are imported as part of their .gltf package, never on their own.
                _activity.TryRemove(path, out _);
                MarkProcessed(path);
            }
        }
    }

    private async Task DrainQueueAsync(CancellationToken cancellationToken)
    {
        if (!_queue.TryPeek(out _)) return;
        if (!await _importLock.WaitAsync(0, cancellationToken)) return;

        try
        {
            while (_queue.TryDequeue(out var job))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var options = _optionsProvider();
                if (options is null) break;

                Report(new UefnImportProgress
                {
                    IsImporting = true,
                    Completed = 0,
                    Total = 1,
                    Status = $"Importing {job.AssetName}..."
                });

                await ImportJobAsync(job, options, cancellationToken);
                Report(new UefnImportProgress { IsImporting = false, Completed = 1, Total = 1, Status = "Watching for exports" });
            }
        }
        finally
        {
            _importLock.Release();
        }
    }

    private async Task<UefnImportHistoryEntry?> ImportJobAsync(
        UefnImportJob job,
        UefnImportOptions options,
        CancellationToken cancellationToken)
    {
        if (job.Kind is EUefnImportKind.Unsupported)
        {
            var unsupported = History(job, EUefnImportStatus.Failed, job.UnsupportedReason);
            ReportNotice("UEFN Import", $"{job.AssetName}: {job.UnsupportedReason}", EUefnNoticeSeverity.Warning);
            return unsupported;
        }

        var project = UefnProject.Reopen(options.Project);
        if (!project.IsWithinContent(project.ContentRoot))
        {
            var invalid = History(job, EUefnImportStatus.Failed, "The UEFN project is no longer valid.");
            ReportNotice("UEFN Import", "The selected UEFN project is no longer valid. Select it again.", EUefnNoticeSeverity.Error);
            return invalid;
        }

        var stagingDirectory = Path.Combine(project.StagingRoot, job.Id);
        string? manifestPath = null;

        try
        {
            var importRelative = NormalizeRelative(options.ImportRelativeFolder);
            if (importRelative.Length == 0)
                throw new InvalidOperationException("The import folder is not valid.");

            if (!project.IsWithinContent(Path.Combine(project.ContentRoot, importRelative)))
                throw new InvalidOperationException("The import folder is outside the UEFN project content folder.");

            if (UefnProject.IsForbiddenImportFolder(Path.Combine(project.ContentRoot, importRelative)))
                throw new InvalidOperationException("The import folder cannot be Content/Python or an external-actors folder.");

            var staged = await StageJobAsync(job, options, stagingDirectory, cancellationToken);
            var category = options.OrganizeAssets ? DetermineCategory(job.AssetName, job.Kind) : string.Empty;
            var destinationRelative = CombineRelative(importRelative, category);
            var assetName = job.AssetName;
            var ledgerKey = LedgerKey(destinationRelative, assetName);
            var ledger = LoadLedger(project);

            if (options.DuplicateDetection)
            {
                var duplicate = ledger.Assets.FirstOrDefault(pair =>
                    pair.Value.Hash.Equals(staged.Hash, StringComparison.OrdinalIgnoreCase) &&
                    pair.Value.AssetType.Equals(job.Kind.ToString(), StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(duplicate.Key))
                {
                    CleanupStaging(stagingDirectory);
                    var skipped = History(job, EUefnImportStatus.Skipped,
                        $"Identical {job.Kind.ToString().ToLowerInvariant()} content was already imported ({duplicate.Key}).");
                    ReportNotice("UEFN Import", $"{job.AssetName} was already imported with identical content. Skipped.", EUefnNoticeSeverity.Informational);
                    return skipped;
                }
            }

            var replaceExisting = false;
            var targetPath = AssetDiskPath(project, destinationRelative, assetName);
            if (File.Exists(targetPath))
            {
                var existing = ledger.Assets.GetValueOrDefault(ledgerKey);
                if (options.DuplicateDetection && existing is not null &&
                    existing.Hash.Equals(staged.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    CleanupStaging(stagingDirectory);
                    var skipped = History(job, EUefnImportStatus.Skipped,
                        $"Identical content already exists at /{project.Mount}/{destinationRelative}/{assetName}. Skipped.");
                    ReportNotice("UEFN Import", $"{job.AssetName} already exists with identical content. Skipped.", EUefnNoticeSeverity.Informational);
                    return skipped;
                }

                var resolution = await _conflictResolver(new UefnConflictRequest
                {
                    AssetName = assetName,
                    AssetType = job.Kind == EUefnImportKind.Mesh ? "Mesh" : "Texture",
                    DestinationPath = $"/{project.Mount}/{destinationRelative}/{assetName}",
                    ExistingSource = existing?.Source ?? targetPath
                }, cancellationToken);

                switch (resolution)
                {
                    case EUefnConflictResolution.Skip:
                        CleanupStaging(stagingDirectory);
                        var conflictSkipped = History(job, EUefnImportStatus.Skipped,
                            $"Skipped because /{project.Mount}/{destinationRelative}/{assetName} already exists.");
                        ReportNotice("UEFN Import", $"{job.AssetName} was skipped because the asset already exists.", EUefnNoticeSeverity.Warning);
                        return conflictSkipped;

                    case EUefnConflictResolution.Rename:
                        assetName = MakeUniqueAssetName(project, ledger, destinationRelative, assetName);
                        ledgerKey = LedgerKey(destinationRelative, assetName);
                        break;

                    case EUefnConflictResolution.Overwrite:
                        replaceExisting = true;
                        break;
                }
            }

            var destinationFolder = Path.Combine(project.ContentRoot,
                destinationRelative.Replace('/', Path.DirectorySeparatorChar));
            ValidateContentPath(project, destinationFolder);
            Directory.CreateDirectory(destinationFolder);

            Directory.CreateDirectory(project.QueueFolder);
            manifestPath = Path.Combine(project.QueueFolder, $"{job.Id}.json");
            var resultPath = Path.ChangeExtension(manifestPath, ".result.json");

            var manifest = new UefnImportManifest
            {
                Version = 1,
                JobId = job.Id,
                PackageName = job.PackageName,
                AssetName = assetName,
                AssetType = job.Kind.ToString(),
                Category = category,
                ImportRelative = importRelative,
                LedgerKey = ledgerKey,
                Hash = staged.Hash,
                StagingDirectory = stagingDirectory,
                PrimarySource = job.PrimaryFile,
                ResultPath = resultPath,
                ImportTextures = options.ImportTextures,
                ImportMaterials = options.ImportMaterials,
                ReplaceExisting = replaceExisting,
                MountHint = project.Mount,
                Assets =
                [
                    new UefnManifestAsset
                    {
                        Kind = job.Kind == EUefnImportKind.Mesh ? "mesh" : "texture",
                        Source = staged.PrimarySource,
                        AssetName = assetName,
                        DestinationRelative = destinationRelative
                    }
                ]
            };

            File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented), new UTF8Encoding(false));

            var execution = await ExecuteManifestAsync(project, manifest, cancellationToken);
            switch (execution.Kind)
            {
                case ManifestExecutionKind.Unavailable:
                {
                    var queued = History(job, EUefnImportStatus.Queued,
                        "Waiting for UEFN. Open the project, then run Import Now or Tools > Execute Python Script > fortnite_porting_uefn.py.");
                    ReportNotice("UEFN Import Queued",
                        $"{job.AssetName} was staged and queued. Open the UEFN project with Python enabled, then use Import Now or run the companion script from Tools > Execute Python Script.",
                        EUefnNoticeSeverity.Warning);
                    return queued;
                }

                case ManifestExecutionKind.Failed:
                {
                    var failed = History(job, EUefnImportStatus.Failed,
                        string.IsNullOrWhiteSpace(execution.Error) ? "UEFN failed to import the asset." : execution.Error);
                    ReportNotice("UEFN Import Failed", $"{job.AssetName}: {failed.Message}", EUefnNoticeSeverity.Error);
                    return failed;
                }

                case ManifestExecutionKind.Success:
                {
                    var result = execution.Result!;
                    ledger.Assets[ledgerKey] = new UefnImportRecord
                    {
                        Hash = staged.Hash,
                        AssetType = job.Kind.ToString(),
                        Source = job.PrimaryFile,
                        ImportedUtc = DateTime.UtcNow.ToString("O"),
                        AssetPaths = result.Imported.Select(imported => imported.Path)
                            .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct().ToList(),
                        Files = staged.Files.Select(Path.GetFileName).Where(name => name is not null).Select(name => name!).ToList()
                    };
                    SaveLedger(project, ledger);
                    CleanupStaging(stagingDirectory);
                    SafeDelete(manifestPath);
                    SafeDelete(resultPath);

                    var importedPath = result.Imported.FirstOrDefault()?.Path;
                    var message = result.Warnings.Count > 0
                        ? $"Imported {result.Imported.Count} asset(s). {string.Join(" ", result.Warnings)}"
                        : $"Imported {result.Imported.Count} asset(s)" +
                          (string.IsNullOrWhiteSpace(importedPath) ? "." : $" to {importedPath}.");

                    var imported = History(job, EUefnImportStatus.Imported, message);
                    ReportNotice("UEFN Import", $"{job.AssetName}: {message}", EUefnNoticeSeverity.Success);
                    return imported;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to import {Asset} into UEFN", job.AssetName);
            var failed = History(job, EUefnImportStatus.Failed, e.Message);
            ReportNotice("UEFN Import Failed", $"{job.AssetName}: {e.Message}", EUefnNoticeSeverity.Error);
            return failed;
        }

        return null;
    }

    private async Task<StagedJob> StageJobAsync(
        UefnImportJob job,
        UefnImportOptions options,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingDirectory);

        var baseDirectory = Path.GetDirectoryName(job.PrimaryFile) ?? options.ExportFolder;
        var stagedFiles = new List<string>();
        string? primarySource = null;

        foreach (var file in job.Files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(file))
                throw new FileNotFoundException("The export file disappeared before it could be imported.", file);

            if (!IsUnder(options.ExportFolder, file))
                throw new InvalidOperationException($"Refusing to import a file outside the selected export folder: {file}");

            var relative = Path.GetRelativePath(baseDirectory, file);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                relative = Path.GetFileName(file);

            var destination = Path.Combine(stagingDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            var isPrimary = file.Equals(job.PrimaryFile, StringComparison.OrdinalIgnoreCase);
            if (isPrimary && job.Kind == EUefnImportKind.Mesh &&
                (file.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) ||
                 file.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)))
            {
                UefnGltfProcessor.WriteProcessed(file, destination,
                    removeTextures: !options.ImportTextures,
                    removeMaterials: !options.ImportMaterials);
                primarySource = destination;
                stagedFiles.Add(destination);
                continue;
            }

            if (isPrimary && job.Kind == EUefnImportKind.Texture &&
                file.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            {
                var pngDestination = Path.ChangeExtension(destination, ".png");
                ConvertWebPToPng(file, pngDestination);
                primarySource = pngDestination;
                stagedFiles.Add(pngDestination);
                continue;
            }

            await CopyFileWithRetryAsync(file, destination, cancellationToken);
            stagedFiles.Add(destination);

            if (isPrimary)
                primarySource = destination;
        }

        if (primarySource is null || !File.Exists(primarySource))
            throw new InvalidOperationException("No importable source file was staged.");

        var hash = ComputeHash(stagedFiles);
        return new StagedJob(stagingDirectory, primarySource, hash, stagedFiles);
    }

    private async Task<ManifestExecution> ExecuteManifestAsync(
        UefnProject project,
        UefnImportManifest manifest,
        CancellationToken cancellationToken)
    {
        if (!project.PythonEnabled)
            return ManifestExecution.Unavailable("Python Editor Scripting is not enabled for this UEFN project. Use Set Up UEFN Project, then reopen the project.");

        if (DateTime.UtcNow < _bridgeUnavailableUntil)
            return ManifestExecution.Unavailable("UEFN is not currently connected.");

        try
        {
            UefnCompanionScript.Install(project);
        }
        catch (Exception e)
        {
            return ManifestExecution.Failed($"Could not install the UEFN companion script: {e.Message}");
        }

        UefnRemoteCommandResult remote;
        try
        {
            var client = new UefnRemoteExecutionClient();
            remote = await client.RunCommandAsync(
                BuildPythonCommand(project, Path.Combine(project.QueueFolder, $"{manifest.JobId}.json")),
                project.Name,
                TimeSpan.FromMinutes(10),
                cancellationToken);
        }
        catch (UefnBridgeUnavailableException e)
        {
            _bridgeUnavailableUntil = DateTime.UtcNow.AddSeconds(30);
            return ManifestExecution.Unavailable(e.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return ManifestExecution.Failed(e.Message);
        }

        _bridgeUnavailableUntil = DateTime.MinValue;

        var result = ReadCompanionResult(manifest);
        if (result is null)
            result = ParseResultFromOutput(remote.Output);

        if (result is null)
        {
            return ManifestExecution.Failed(string.IsNullOrWhiteSpace(remote.Result)
                ? "UEFN did not return an import result. Check the UEFN Output Log for Python errors."
                : remote.Result);
        }

        result.Output = remote.Output;

        if (!result.Success)
        {
            var errors = result.Errors.Count > 0 ? string.Join(" ", result.Errors) : "UEFN did not create any assets.";
            return ManifestExecution.Failed(errors);
        }

        return ManifestExecution.Success(result);
    }

    private async Task RetryPendingAsync(
        UefnImportOptions options,
        List<UefnImportHistoryEntry> entries,
        CancellationToken cancellationToken)
    {
        var project = UefnProject.Reopen(options.Project);
        if (!Directory.Exists(project.QueueFolder)) return;

        string[] manifestFiles;
        try
        {
            manifestFiles = Directory.EnumerateFiles(project.QueueFolder, "*.json")
                .Where(path => !path.EndsWith(".result.json", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not read the UEFN import queue");
            return;
        }

        foreach (var manifestPath in manifestFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            UefnImportManifest? manifest;
            try
            {
                manifest = JsonConvert.DeserializeObject<UefnImportManifest>(File.ReadAllText(manifestPath));
            }
            catch
            {
                continue;
            }

            if (manifest is null || manifest.Assets.Count == 0) continue;

            var priorResult = ReadCompanionResult(manifest);
            if (priorResult?.Success == true)
            {
                var completedLedger = LoadLedger(project);
                completedLedger.Assets[manifest.LedgerKey] = new UefnImportRecord
                {
                    Hash = manifest.Hash,
                    AssetType = manifest.AssetType,
                    Source = manifest.PrimarySource,
                    ImportedUtc = DateTime.UtcNow.ToString("O"),
                    AssetPaths = priorResult.Imported.Select(imported => imported.Path)
                        .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct().ToList()
                };
                SaveLedger(project, completedLedger);
                CleanupStaging(manifest.StagingDirectory);
                SafeDelete(manifestPath);
                SafeDelete(manifest.ResultPath);

                var reconciled = new UefnImportHistoryEntry
                {
                    PackageName = manifest.PackageName,
                    AssetType = manifest.AssetType,
                    Status = EUefnImportStatus.Imported,
                    Message = $"Imported {priorResult.Imported.Count} queued asset(s) from the companion script."
                };
                entries.Add(reconciled);
                HistoryAdded?.Invoke(reconciled);
                continue;
            }

            if (manifest.Assets.Any(asset => !File.Exists(asset.Source)))
            {
                SafeDelete(manifestPath);
                SafeDelete(manifest.ResultPath);
                continue;
            }

            var ledger = LoadLedger(project);
            var duplicate = ledger.Assets.FirstOrDefault(pair =>
                pair.Value.Hash.Equals(manifest.Hash, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(duplicate.Key))
            {
                CleanupStaging(manifest.StagingDirectory);
                SafeDelete(manifestPath);
                SafeDelete(manifest.ResultPath);
                continue;
            }

            var execution = await ExecuteManifestAsync(project, manifest, cancellationToken);
            if (execution.Kind is ManifestExecutionKind.Unavailable)
            {
                var queued = new UefnImportHistoryEntry
                {
                    PackageName = manifest.PackageName,
                    AssetType = manifest.AssetType,
                    Status = EUefnImportStatus.Queued,
                    Message = "Still queued; UEFN is not connected."
                };
                entries.Add(queued);
                HistoryAdded?.Invoke(queued);
                break;
            }

            if (execution.Kind is ManifestExecutionKind.Failed)
            {
                var failed = new UefnImportHistoryEntry
                {
                    PackageName = manifest.PackageName,
                    AssetType = manifest.AssetType,
                    Status = EUefnImportStatus.Failed,
                    Message = execution.Error
                };
                entries.Add(failed);
                HistoryAdded?.Invoke(failed);
                continue;
            }

            var result = execution.Result!;
            ledger.Assets[manifest.LedgerKey] = new UefnImportRecord
            {
                Hash = manifest.Hash,
                AssetType = manifest.AssetType,
                Source = manifest.PrimarySource,
                ImportedUtc = DateTime.UtcNow.ToString("O"),
                AssetPaths = result.Imported.Select(imported => imported.Path).Where(path => !string.IsNullOrWhiteSpace(path)).ToList()
            };
            SaveLedger(project, ledger);
            CleanupStaging(manifest.StagingDirectory);
            SafeDelete(manifestPath);
            SafeDelete(manifest.ResultPath);

            var imported = new UefnImportHistoryEntry
            {
                PackageName = manifest.PackageName,
                AssetType = manifest.AssetType,
                Status = EUefnImportStatus.Imported,
                Message = $"Imported {result.Imported.Count} queued asset(s)."
            };
            entries.Add(imported);
            HistoryAdded?.Invoke(imported);
        }
    }

    private static HashSet<string> GetPendingPrimarySources(UefnProject project)
    {
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(project.QueueFolder))
            return sources;

        try
        {
            foreach (var manifestPath in Directory.EnumerateFiles(project.QueueFolder, "*.json")
                         .Where(path => !path.EndsWith(".result.json", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var manifest = JsonConvert.DeserializeObject<UefnImportManifest>(File.ReadAllText(manifestPath));
                    if (!string.IsNullOrWhiteSpace(manifest?.PrimarySource))
                        sources.Add(Path.GetFullPath(manifest.PrimarySource));
                }
                catch
                {
                    // Ignore a manifest that is still being written or is unreadable.
                }
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not inspect the UEFN import queue");
        }

        return sources;
    }

    private List<UefnImportJob> ScanExportFolder(UefnImportOptions options, out int ignoredUnsupported)
    {
        ignoredUnsupported = 0;
        var jobs = new List<UefnImportJob>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = SafeEnumerate(options.ExportFolder)
            .OrderByDescending(file => MeshExtensions.Contains(Path.GetExtension(file)))
            .ThenBy(file => file)
            .ToArray();

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file);

            if (MeshExtensions.Contains(extension))
            {
                if (!IsFileReady(file)) continue;

                var dependencies = GetGltfDependencies(file, options.ExportFolder);
                if (dependencies.Any(dependency => !File.Exists(dependency) || !IsFileReady(dependency)))
                    continue;

                var allFiles = new[] { file }.Concat(dependencies).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var included in allFiles)
                    claimed.Add(included);

                var stem = Path.GetFileNameWithoutExtension(file);
                jobs.Add(new UefnImportJob
                {
                    Id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}",
                    Kind = EUefnImportKind.Mesh,
                    PackageName = BasePackageName(stem),
                    AssetName = SanitizeAssetName(stem),
                    PrimaryFile = file,
                    Files = allFiles.ToList()
                });
                continue;
            }

            if (TextureExtensions.Contains(extension))
            {
                if (!options.ImportTextures || claimed.Contains(file) || !IsFileReady(file))
                    continue;

                jobs.Add(new UefnImportJob
                {
                    Id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}",
                    Kind = EUefnImportKind.Texture,
                    PackageName = BasePackageName(Path.GetFileNameWithoutExtension(file)),
                    AssetName = SanitizeAssetName(Path.GetFileNameWithoutExtension(file)),
                    PrimaryFile = file,
                    Files = [file]
                });
                continue;
            }

            if (UnsupportedExtensions.Contains(extension))
                ignoredUnsupported++;
        }

        return jobs;
    }

    private List<string> GetGltfDependencies(string gltfPath, string exportFolder)
    {
        var dependencies = new List<string>();
        if (!gltfPath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
            return dependencies;

        try
        {
            var json = JObject.Parse(File.ReadAllText(gltfPath));
            var root = Path.GetDirectoryName(gltfPath)!;

            foreach (var collectionName in new[] { "buffers", "images" })
            {
                if (json[collectionName] is not JArray collection) continue;

                foreach (var item in collection.OfType<JObject>())
                {
                    var uri = item.Value<string>("uri");
                    if (string.IsNullOrWhiteSpace(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        continue;

                    uri = Uri.UnescapeDataString(uri);
                    var candidate = Path.GetFullPath(Path.Combine(root, uri.Replace('/', Path.DirectorySeparatorChar)));
                    if (File.Exists(candidate) && IsUnder(exportFolder, candidate))
                        dependencies.Add(candidate);
                }
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not read glTF dependencies from {File}", gltfPath);
        }

        return dependencies.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private bool IsReferencedByPendingGltf(string texturePath, string exportFolder)
    {
        foreach (var activityPath in _activity.Keys)
        {
            if (!activityPath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) continue;
            if (GetGltfDependencies(activityPath, exportFolder)
                .Any(dependency => dependency.Equals(texturePath, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    private void Track(string path)
    {
        try
        {
            if (!IsTrackedExtension(path)) return;

            if (!File.Exists(path))
            {
                _activity.TryRemove(path, out _);
                return;
            }

            var info = new FileInfo(path);
            lock (_stateLock)
            {
                if (_processed.TryGetValue(path, out var previous) &&
                    previous.Write == info.LastWriteTimeUtc &&
                    previous.Length == info.Length)
                    return;
            }

            _activity[path] = DateTime.UtcNow;
            SignalWork();
        }
        catch
        {
            // A file can disappear while the event is handled; the next event will pick it up.
        }
    }

    private void SignalWork()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Work is already pending.
        }
    }

    private void MarkProcessed(string path)
    {
        try
        {
            var info = new FileInfo(path);
            lock (_stateLock)
                _processed[path] = (info.LastWriteTimeUtc, info.Length);
        }
        catch
        {
            // Ignore.
        }
    }

    private static bool IsTrackedExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return MeshExtensions.Contains(extension) ||
               TextureExtensions.Contains(extension) ||
               UnsupportedExtensions.Contains(extension) ||
               extension.Equals(".bin", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFileReady(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == 0) return false;

            var info = new FileInfo(path);
            return info.Length == stream.Length;
        }
        catch
        {
            return false;
        }
    }

    private static async Task CopyFileWithRetryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(200, cancellationToken);
            }
        }

        File.Copy(source, destination, overwrite: true);
    }

    private static void ConvertWebPToPng(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var image = Image.Load(source);
        image.Save(destination, new PngEncoder());
    }

    private static string ComputeHash(IEnumerable<string> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file) ?? string.Empty));
            hash.AppendData(File.ReadAllBytes(file));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static UefnImportLedger LoadLedger(UefnProject project)
    {
        try
        {
            if (!File.Exists(project.LedgerFile))
                return new UefnImportLedger();

            var ledger = JsonConvert.DeserializeObject<UefnImportLedger>(File.ReadAllText(project.LedgerFile));
            if (ledger is null)
                return new UefnImportLedger();

            ledger.Assets = new Dictionary<string, UefnImportRecord>(ledger.Assets, StringComparer.OrdinalIgnoreCase);
            return ledger;
        }
        catch (Exception e)
        {
            Log.Warning(e, "The UEFN import ledger was unreadable; starting a new one");
            try
            {
                File.Copy(project.LedgerFile, project.LedgerFile + LedgerBackupExtension, overwrite: true);
            }
            catch
            {
                // Ignore backup failures.
            }

            return new UefnImportLedger();
        }
    }

    private static void SaveLedger(UefnProject project, UefnImportLedger ledger)
    {
        Directory.CreateDirectory(project.SavedFolder);
        var temporary = project.LedgerFile + ".tmp";
        File.WriteAllText(temporary, JsonConvert.SerializeObject(ledger, Formatting.Indented), new UTF8Encoding(false));
        File.Move(temporary, project.LedgerFile, overwrite: true);
    }

    private static string AssetDiskPath(UefnProject project, string destinationRelative, string assetName)
    {
        var folder = Path.Combine(project.ContentRoot,
            destinationRelative.Replace('/', Path.DirectorySeparatorChar));
        ValidateContentPath(project, folder);
        return Path.Combine(folder, $"{assetName}.uasset");
    }

    private static void ValidateContentPath(UefnProject project, string path)
    {
        if (!project.IsWithinContent(path))
            throw new InvalidOperationException("Refusing to write outside the UEFN project's Content folder.");
    }

    private static string MakeUniqueAssetName(
        UefnProject project,
        UefnImportLedger ledger,
        string destinationRelative,
        string assetName)
    {
        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{assetName}_{suffix}";
            var key = LedgerKey(destinationRelative, candidate);
            if (!ledger.Assets.ContainsKey(key) && !File.Exists(AssetDiskPath(project, destinationRelative, candidate)))
                return candidate;
        }

        return $"{assetName}_{DateTime.Now:yyyyMMddHHmmss}";
    }

    private static string LedgerKey(string destinationRelative, string assetName)
        => $"{NormalizeRelative(destinationRelative)}/{assetName}".Trim('/');

    private static string CombineRelative(params string[] parts)
        => string.Join('/', parts.Select(NormalizeRelative).Where(part => part.Length > 0));

    private static string NormalizeRelative(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var parts = value.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part is not "." and not "..")
            .Select(part => Regex.Replace(part, "[^A-Za-z0-9_ .-]", "_").Trim())
            .Where(part => part.Length > 0)
            .ToArray();

        return string.Join('/', parts);
    }

    private static string SanitizeAssetName(string? name)
    {
        var sanitized = Regex.Replace(name ?? string.Empty, "[^A-Za-z0-9_]", "_").Trim('_');
        if (sanitized.Length == 0)
            sanitized = "Asset";
        if (char.IsDigit(sanitized[0]))
            sanitized = $"_{sanitized}";
        if (sanitized.Length > 100)
            sanitized = sanitized[..100];
        return sanitized;
    }

    private static string BasePackageName(string stem) =>
        Regex.Replace(stem, "(_LOD\\d+|_Nanite)$", string.Empty, RegexOptions.IgnoreCase);

    private static string DetermineCategory(string assetName, EUefnImportKind kind)
    {
        if (kind is EUefnImportKind.Texture)
            return "Textures";

        var name = assetName.ToLowerInvariant();
        string[] characterTokens = ["character", "char_", "outfit", "body", "head", "hair", "face", "skin", "backpack", "pet", "npc", "sidekick"];
        string[] weaponTokens = ["weapon", "wep", "wid_", "gun", "rifle", "shotgun", "pistol", "melee", "sword", "pickaxe", "launcher"];
        string[] environmentTokens = ["env", "landscape", "terrain", "tree", "rock", "cliff", "grass", "foliage", "water", "building", "house", "wall", "floor", "roof", "prop_env"];
        string[] propTokens = ["prop", "crate", "barrel", "furniture", "sign", "container"];

        if (characterTokens.Any(name.Contains)) return "Characters";
        if (weaponTokens.Any(name.Contains)) return "Weapons";
        if (environmentTokens.Any(name.Contains)) return "Environment";
        if (propTokens.Any(name.Contains)) return "Props";
        return "Meshes";
    }

    private static string UnsupportedReason(string extension) => extension.ToLowerInvariant() switch
    {
        ".ueanim" =>
            "Fortnite Porting exported this animation as .ueanim. UEFN's Python importer cannot read UEFormat animations, and Fortnite Porting's glTF export does not include animations. Export an FBX-compatible animation or import it manually into UEFN.",
        ".uemodel" or ".uepose" =>
            "Fortnite Porting exported this asset in UEFormat. UEFN's Python importer cannot read UEFormat files. In Fortnite Porting's export settings, change Mesh Format to 'glTF 2.0 (binary)' and export again.",
        _ =>
            "This file type cannot be imported into UEFN from disk. Export the asset as glTF 2.0 or a supported texture format instead."
    };

    private static bool IsUnder(string root, string file)
    {
        try
        {
            var normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var normalizedFile = Path.GetFullPath(file);
            return normalizedFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildPythonCommand(UefnProject project, string manifestPath)
    {
        static string Literal(string value) => $"r\"{value.Replace("\"", "\\\"")}\"";

        return $"""
                import sys, importlib
                _p = {Literal(project.PythonFolder)}
                if _p not in sys.path:
                    sys.path.insert(0, _p)
                import fortnite_porting_uefn
                importlib.reload(fortnite_porting_uefn)
                fortnite_porting_uefn.run_manifest({Literal(manifestPath)})
                """;
    }

    private static UefnCompanionResult? ReadCompanionResult(UefnImportManifest manifest)
    {
        try
        {
            if (!File.Exists(manifest.ResultPath))
                return null;

            return JsonConvert.DeserializeObject<UefnCompanionResult>(File.ReadAllText(manifest.ResultPath));
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not read UEFN companion result for {Job}", manifest.JobId);
            return null;
        }
    }

    private static UefnCompanionResult? ParseResultFromOutput(string output)
    {
        const string marker = "FORTNITE_PORTING_RESULT:";
        var index = output.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;

        var json = output[(index + marker.Length)..].Trim();
        try
        {
            return JsonConvert.DeserializeObject<UefnCompanionResult>(json);
        }
        catch
        {
            return null;
        }
    }

    private UefnImportHistoryEntry History(UefnImportJob job, EUefnImportStatus status, string message)
    {
        var entry = new UefnImportHistoryEntry
        {
            PackageName = job.AssetName,
            AssetType = job.Kind == EUefnImportKind.Mesh ? "Mesh" : job.Kind == EUefnImportKind.Texture ? "Texture" : "Unsupported",
            Status = status,
            Message = message
        };
        HistoryAdded?.Invoke(entry);
        return entry;
    }

    private void Report(UefnImportProgress progress) => ProgressChanged?.Invoke(progress);

    private void ReportNotice(string title, string message, EUefnNoticeSeverity severity)
    {
        var options = _optionsProvider();
        if (options is not null && !options.ImportNotifications)
            return;

        Notice?.Invoke(title, message, severity);
    }

    private static UefnImportSummary BuildSummary(IEnumerable<UefnImportHistoryEntry> entries)
    {
        var list = entries.ToList();
        return new UefnImportSummary
        {
            Imported = list.Count(entry => entry.Status is EUefnImportStatus.Imported),
            Skipped = list.Count(entry => entry.Status is EUefnImportStatus.Skipped),
            Failed = list.Count(entry => entry.Status is EUefnImportStatus.Failed),
            Queued = list.Count(entry => entry.Status is EUefnImportStatus.Queued),
            Entries = new System.Collections.ObjectModel.ObservableCollection<UefnImportHistoryEntry>(list)
        };
    }

    private static IEnumerable<string> SafeEnumerate(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToArray();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not enumerate the export folder {Folder}", folder);
            return [];
        }
    }

    private static void CleanupStaging(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not delete UEFN import staging folder {Directory}", directory);
        }
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not delete {Path}", path);
        }
    }

    public void Dispose()
    {
        Stop();
        _signal.Dispose();
        _importLock.Dispose();
    }

    private sealed record StagedJob(string Directory, string PrimarySource, string Hash, List<string> Files);

    private enum ManifestExecutionKind
    {
        Success,
        Failed,
        Unavailable
    }

    private sealed record ManifestExecution(ManifestExecutionKind Kind, UefnCompanionResult? Result, string Error)
    {
        public static ManifestExecution Success(UefnCompanionResult result) => new(ManifestExecutionKind.Success, result, string.Empty);
        public static ManifestExecution Failed(string error) => new(ManifestExecutionKind.Failed, null, error);
        public static ManifestExecution Unavailable(string error) => new(ManifestExecutionKind.Unavailable, null, error);
    }
}
