using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using FortnitePorting.Framework;
using FortnitePorting.Models.Information;
using FortnitePorting.Models.Uefn;
using FortnitePorting.Services;
using Newtonsoft.Json;

namespace FortnitePorting.ViewModels.Plugin;

public partial class UefnPluginViewModel : ViewModelBase
{
    private readonly UefnImportService _service;
    private string? _watchedFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyPropertyChangedFor(nameof(HasProject))]
    [NotifyPropertyChangedFor(nameof(CanSetUpProject))]
    [NotifyPropertyChangedFor(nameof(ProjectStatusBrush))]
    [NotifyPropertyChangedFor(nameof(ImportBrowserPath))]
    [property: JsonIgnore]
    private UefnProject? _project;

    [ObservableProperty] private string _projectPath = string.Empty;
    [ObservableProperty] [property: JsonIgnore] private string _projectName = "No UEFN project selected";
    [ObservableProperty] [property: JsonIgnore] private string _projectContentPath = "Choose the folder that contains your .uefnproject file.";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectStatusBrush))]
    [property: JsonIgnore]
    private bool _projectValid;
    [ObservableProperty] [property: JsonIgnore] private string _projectStatus = "Select your UEFN project to begin.";
    [ObservableProperty] [property: JsonIgnore] private string _setupStatus = string.Empty;
    [ObservableProperty] [property: JsonIgnore] private bool _setupRequired;
    [ObservableProperty] [property: JsonIgnore] private bool _requiresRestart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    private string _exportFolder = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportBrowserPath))]
    private string _importRelativeFolder = "FortnitePorting";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoImportStatus))]
    [NotifyPropertyChangedFor(nameof(AutoImportStatusBrush))]
    private bool _autoImport = true;

    [ObservableProperty] private bool _importTextures = true;
    [ObservableProperty] private bool _importMaterials = true;
    [ObservableProperty] private bool _organizeAssets = true;
    [ObservableProperty] private bool _duplicateDetection = true;
    [ObservableProperty] private bool _importNotifications = true;

    [ObservableProperty] [property: JsonIgnore] private string _status = "Ready.";
    [ObservableProperty] [property: JsonIgnore] private string _progressText = string.Empty;
    [ObservableProperty] [property: JsonIgnore] private double _progress;
    [ObservableProperty] [property: JsonIgnore] private bool _isScanning;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [property: JsonIgnore]
    private bool _isImporting;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSetUpProject))]
    [property: JsonIgnore]
    private bool _isBusy;

    [ObservableProperty] [property: JsonIgnore] private string _bridgeStatus = "Not checked.";
    [ObservableProperty] [property: JsonIgnore] private bool _bridgeConnected;

    [ObservableProperty] private ObservableCollection<UefnImportHistoryEntry> _history = [];

    [JsonIgnore] public bool HasProject => Project is not null;
    [JsonIgnore] public bool HasHistory => History.Count > 0;
    [JsonIgnore] public bool HasNoHistory => History.Count == 0;
    [JsonIgnore] public bool CanImport => Project is not null && !IsImporting &&
                                          !string.IsNullOrWhiteSpace(ExportFolder) &&
                                          Directory.Exists(ExportFolder);
    [JsonIgnore] public bool CanSetUpProject => Project is not null && !IsBusy;
    [JsonIgnore] public string AutoImportStatus => AutoImport ? "Auto Import On" : "Auto Import Off";
    [JsonIgnore] public SolidColorBrush AutoImportStatusBrush => AutoImport ? SolidColorBrush.Parse("#17854F") : SolidColorBrush.Parse("#6F6F75");
    [JsonIgnore] public SolidColorBrush ProjectStatusBrush => ProjectValid
        ? SolidColorBrush.Parse("#17854F")
        : Project is null ? SolidColorBrush.Parse("#6F6F75") : SolidColorBrush.Parse("#A61717");
    [JsonIgnore] public string ImportBrowserPath => Project is null ? "/FortnitePorting" : $"{Project.ContentBrowserRoot}/{ImportRelativeFolder.Trim('/')}";

    public UefnPluginViewModel()
    {
        _service = new UefnImportService(GetOptions, ResolveConflictAsync);
        _service.ProgressChanged += OnServiceProgress;
        _service.HistoryAdded += OnHistoryAdded;
        _service.Notice += OnServiceNotice;
    }

    public override Task Initialize()
    {
        EnsureDefaults();
        ReloadProject();
        StartWatchingIfEnabled();
        return Task.CompletedTask;
    }

    public override async Task OnViewOpened()
    {
        EnsureDefaults();
        ReloadProject();
        StartWatchingIfEnabled();

        if (Project is not null)
            await RefreshBridge();
    }

    public override void OnApplicationExit()
    {
        _service.Stop();
    }

    public void StartWatchingIfEnabled()
    {
        // The selected project is validated from disk on startup; the VM is not opened yet.
        if (Project is null && !string.IsNullOrWhiteSpace(ProjectPath))
            ReloadProject();

        if (!AutoImport)
        {
            _service.Stop();
            _watchedFolder = null;
            Status = "Auto import is off.";
            return;
        }

        EnsureDefaults();
        if (Project is null)
        {
            Status = "Select a valid UEFN project to enable auto import.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ExportFolder) || !Directory.Exists(ExportFolder))
        {
            Status = "Select a Fortnite Porting export folder to enable auto import.";
            return;
        }

        if (_service.IsWatching && string.Equals(_watchedFolder, ExportFolder, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            _watchedFolder = ExportFolder;
            _service.Start(ExportFolder);
            Status = $"Watching {ExportFolder}";
        }
        catch (Exception e)
        {
            _watchedFolder = null;
            Status = "Auto import could not start.";
            Info.Message("UEFN Import", $"Could not watch the export folder: {e.Message}", InfoBarSeverity.Error, autoClose: false);
        }
    }

    private void EnsureDefaults()
    {
        if (string.IsNullOrWhiteSpace(ExportFolder) && !string.IsNullOrWhiteSpace(App.AssetsFolder.FullName))
            ExportFolder = App.AssetsFolder.FullName;

        if (string.IsNullOrWhiteSpace(ImportRelativeFolder))
            ImportRelativeFolder = "FortnitePorting";
    }

    private void ReloadProject()
    {
        if (string.IsNullOrWhiteSpace(ProjectPath))
            return;

        var result = UefnProject.TryOpen(ProjectPath);
        if (!result.Success || result.Project is null)
        {
            Project = null;
            ProjectValid = false;
            ProjectStatus = result.Error;
            ProjectName = "Invalid UEFN project";
            ProjectContentPath = ProjectPath;
            return;
        }

        ApplyProject(result.Project);
    }

    private void ApplyProject(UefnProject project)
    {
        Project = project;
        ProjectPath = project.RootFolder;
        ProjectName = project.DisplayName;
        ProjectContentPath = project.ContentRoot;
        ProjectValid = true;
        ProjectStatus = $"Valid UEFN project  •  /{project.Mount}/";
        SetupRequired = !project.PythonEnabled || !project.RemoteExecutionEnabled || !project.CompanionInstalled || project.CompanionOutdated;

        SetupStatus = !project.CompanionInstalled
            ? "The UEFN companion importer is not installed yet. Select Set Up UEFN Project."
            : !project.PythonEnabled
                ? "Python Editor Scripting is disabled for this project. Select Set Up UEFN Project, then restart UEFN."
                : !project.RemoteExecutionEnabled
                    ? "Python remote execution is disabled. Select Set Up UEFN Project, then restart UEFN."
                    : "The companion importer needs an update. Select Set Up UEFN Project.";

        PersistSettings();
    }

    [RelayCommand]
    private async Task BrowseProject()
    {
        var start = Directory.Exists(UefnProject.DefaultProjectsFolder)
            ? UefnProject.DefaultProjectsFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var folder = await App.BrowseFolderDialog(start);
        if (string.IsNullOrWhiteSpace(folder)) return;

        var result = UefnProject.TryOpen(folder);
        if (!result.Success || result.Project is null)
        {
            Info.Message("UEFN Project", result.Error, InfoBarSeverity.Error, autoClose: false);
            return;
        }

        ApplyProject(result.Project);
        if (AutoImport) StartWatchingIfEnabled();
        await RefreshBridge();
    }

    [RelayCommand]
    private async Task BrowseExportFolder()
    {
        var start = Directory.Exists(ExportFolder) ? ExportFolder : App.AssetsFolder.FullName;
        var folder = await App.BrowseFolderDialog(start);
        if (string.IsNullOrWhiteSpace(folder)) return;

        ExportFolder = folder;
        PersistSettings();

        if (AutoImport) StartWatchingIfEnabled();
    }

    [RelayCommand]
    private async Task BrowseImportFolder()
    {
        if (Project is null)
        {
            Info.Message("UEFN Project", "Select a valid UEFN project first.", InfoBarSeverity.Warning);
            return;
        }

        var start = Path.Combine(Project.ContentRoot, ImportRelativeFolder.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(start))
            start = Project.ContentRoot;

        var folder = await App.BrowseFolderDialog(start);
        if (string.IsNullOrWhiteSpace(folder)) return;

        var relative = Path.GetRelativePath(Project.ContentRoot, folder).Replace('\\', '/');
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            Info.Message("UEFN Import Folder", "Choose a folder inside the UEFN project's Content folder.", InfoBarSeverity.Warning);
            return;
        }

        if (relative.Equals(".", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(relative))
            relative = "FortnitePorting";

        if (UefnProject.IsForbiddenImportFolder(folder))
        {
            Info.Message("UEFN Import Folder", "Content/Python and external-actor folders cannot be used for imported assets. Choose another folder.", InfoBarSeverity.Warning);
            return;
        }

        ImportRelativeFolder = relative;
        PersistSettings();
    }

    [RelayCommand]
    private void OpenProjectFolder()
    {
        if (Project is null) return;
        App.Launch(Project.RootFolder);
    }

    [RelayCommand]
    private void OpenImportFolder()
    {
        if (Project is null) return;

        var path = Path.Combine(Project.ContentRoot, ImportRelativeFolder.Replace('/', Path.DirectorySeparatorChar));
        if (!Project.IsWithinContent(path)) return;

        Directory.CreateDirectory(path);
        App.Launch(path);
    }

    [RelayCommand]
    private async Task SetUpProject()
    {
        if (Project is null) return;

        IsBusy = true;
        try
        {
            var project = Project;
            var result = await Task.Run(() => UefnCompanionScript.Configure(project));
            ReloadProject();

            RequiresRestart = result.Changed || result.EditorRunning;

            var details = result.Details.Concat(result.Warnings).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
            var message = details.Length > 0
                ? string.Join("\n", details)
                : "Python Editor Scripting, remote execution and the companion importer are already set up.";

            Info.Dialog("UEFN Setup", message, buttons:
            [
                new DialogButton { Text = "OK", IsPrimary = true }
            ]);

            if (result.Changed)
            {
                Info.Message("UEFN Setup",
                    "Setup changed. Restart UEFN before importing so Python Editor Scripting and remote execution are loaded.",
                    InfoBarSeverity.Warning, autoClose: false, useButton: true, buttonTitle: "Open Project Folder",
                    buttonCommand: OpenProjectFolder);
            }

            await RefreshBridge();
        }
        catch (Exception e)
        {
            Info.Message("UEFN Setup", $"Could not configure the project: {e.Message}", InfoBarSeverity.Error, autoClose: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshBridge()
    {
        if (Project is null)
        {
            BridgeStatus = "Select a UEFN project first.";
            BridgeConnected = false;
            return;
        }

        BridgeStatus = "Checking for UEFN...";
        try
        {
            var node = await new UefnRemoteExecutionClient()
                .ProbeAsync(Project.Name, TimeSpan.FromSeconds(3), CancellationToken.None);

            if (node is null)
            {
                BridgeConnected = false;
                BridgeStatus = "UEFN is not running or Python remote execution is off. Setup and restart the editor, or use the queued import workflow.";
                return;
            }

            BridgeConnected = true;
            var projectName = string.IsNullOrWhiteSpace(node.ProjectName) ? Project.Name : node.ProjectName;
            BridgeStatus = $"Connected to UEFN ({projectName}).";
        }
        catch (UefnBridgeUnavailableException e)
        {
            BridgeConnected = false;
            BridgeStatus = e.Message;
        }
        catch (Exception e)
        {
            BridgeConnected = false;
            BridgeStatus = $"Could not check UEFN: {e.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportNow()
    {
        if (Project is null)
        {
            Info.Message("UEFN Import", "Select a valid UEFN project first.", InfoBarSeverity.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(ExportFolder) || !Directory.Exists(ExportFolder))
        {
            Info.Message("UEFN Import", "Choose a Fortnite Porting export folder first.", InfoBarSeverity.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            var summary = await _service.ImportNowAsync(CancellationToken.None);
            var parts = new[]
            {
                $"{summary.Imported} imported",
                $"{summary.Skipped} skipped",
                $"{summary.Failed} failed",
                $"{summary.Queued} queued"
            };
            Status = string.Join(", ", parts);

            var severity = summary.Failed > 0 ? InfoBarSeverity.Error
                : summary.Queued > 0 ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;

            Info.Message("UEFN Import", Status, severity, autoClose: summary.Failed == 0, closeTime: 5);
        }
        catch (OperationCanceledException)
        {
            Status = "Import cancelled.";
        }
        catch (Exception e)
        {
            Status = "Import failed.";
            Info.Message("UEFN Import", e.Message, InfoBarSeverity.Error, autoClose: false);
        }
        finally
        {
            IsBusy = false;
            IsImporting = false;
            Progress = 0;
            ProgressText = string.Empty;
        }
    }

    private UefnImportOptions? GetOptions()
    {
        var project = Project ?? (string.IsNullOrWhiteSpace(ProjectPath) ? null : UefnProject.TryOpen(ProjectPath).Project);
        if (project is null || string.IsNullOrWhiteSpace(ExportFolder) || !Directory.Exists(ExportFolder))
            return null;

        return new UefnImportOptions
        {
            Project = project,
            ExportFolder = ExportFolder,
            ImportRelativeFolder = string.IsNullOrWhiteSpace(ImportRelativeFolder) ? "FortnitePorting" : ImportRelativeFolder,
            ImportTextures = ImportTextures,
            ImportMaterials = ImportMaterials,
            OrganizeAssets = OrganizeAssets,
            DuplicateDetection = DuplicateDetection,
            ImportNotifications = ImportNotifications
        };
    }

    private Task<EUefnConflictResolution> ResolveConflictAsync(UefnConflictRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<EUefnConflictResolution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        TaskService.RunDispatcher(() =>
        {
            Info.Dialog("Asset Already Exists",
                $"/{request.DestinationPath.TrimStart('/')} already exists and has different content.\n\nExisting: {request.ExistingSource}\n\nOverwrite replaces the existing asset permanently. Rename keeps both copies.",
                buttons:
                [
                    new DialogButton
                    {
                        Text = "Overwrite",
                        Action = () => completion.TrySetResult(EUefnConflictResolution.Overwrite)
                    },
                    new DialogButton
                    {
                        Text = "Rename",
                        IsPrimary = true,
                        Action = () => completion.TrySetResult(EUefnConflictResolution.Rename)
                    },
                    new DialogButton
                    {
                        Text = "Skip",
                        Action = () => completion.TrySetResult(EUefnConflictResolution.Skip)
                    }
                ],
                canClose: false);
        });

        completion.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        return completion.Task;
    }

    private void OnServiceProgress(UefnImportProgress progress)
    {
        TaskService.PostDispatcher(() =>
        {
            IsScanning = progress.IsScanning;
            IsImporting = progress.IsImporting;
            Status = progress.Status;

            if (progress.Total > 0)
            {
                Progress = Math.Clamp(progress.Completed * 100.0 / progress.Total, 0, 100);
                ProgressText = $"{progress.Completed} / {progress.Total}";
            }
            else if (progress.IsScanning)
            {
                Progress = 0;
                ProgressText = "Scanning...";
            }
        });
    }

    private void OnHistoryAdded(UefnImportHistoryEntry entry)
    {
        TaskService.PostDispatcher(() =>
        {
            History.Insert(0, entry);
            while (History.Count > 50)
                History.RemoveAt(History.Count - 1);

            OnPropertyChanged(nameof(HasHistory));
            OnPropertyChanged(nameof(HasNoHistory));
        });
    }

    private void OnServiceNotice(string title, string message, EUefnNoticeSeverity severity)
    {
        TaskService.PostDispatcher(() =>
        {
            var infoSeverity = severity switch
            {
                EUefnNoticeSeverity.Success => InfoBarSeverity.Success,
                EUefnNoticeSeverity.Warning => InfoBarSeverity.Warning,
                EUefnNoticeSeverity.Error => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational
            };

            Info.Message(title, message, infoSeverity, autoClose: severity is not EUefnNoticeSeverity.Error, closeTime: 5);
        });
    }

    private void PersistSettings()
    {
        if (!IsInitialized) return;

        try
        {
            AppSettings.Save();
        }
        catch
        {
            // Settings are also saved when Fortnite Porting exits.
        }
    }

    partial void OnAutoImportChanged(bool value)
    {
        OnPropertyChanged(nameof(AutoImportStatus));
        OnPropertyChanged(nameof(AutoImportStatusBrush));

        if (!IsInitialized) return;

        PersistSettings();
        if (value)
            StartWatchingIfEnabled();
        else
        {
            _service.Stop();
            _watchedFolder = null;
            Status = "Auto import is off.";
        }
    }

    partial void OnExportFolderChanged(string value)
    {
        if (!IsInitialized) return;

        PersistSettings();
        if (AutoImport) StartWatchingIfEnabled();
    }

    partial void OnImportRelativeFolderChanged(string value)
    {
        OnPropertyChanged(nameof(ImportBrowserPath));
        if (!IsInitialized) return;
        PersistSettings();
    }
}
