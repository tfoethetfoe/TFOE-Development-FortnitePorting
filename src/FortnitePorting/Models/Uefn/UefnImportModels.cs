using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json;

namespace FortnitePorting.Models.Uefn;

public enum EUefnImportKind
{
    Mesh,
    Texture,
    Unsupported
}

public enum EUefnImportStatus
{
    Imported,
    Skipped,
    Failed,
    Queued
}

public enum EUefnConflictResolution
{
    Overwrite,
    Rename,
    Skip
}

public enum EUefnNoticeSeverity
{
    Informational,
    Success,
    Warning,
    Error
}

public sealed class UefnImportOptions
{
    public required UefnProject Project { get; init; }
    public required string ExportFolder { get; init; }
    public required string ImportRelativeFolder { get; init; }
    public bool ImportTextures { get; init; }
    public bool ImportMaterials { get; init; }
    public bool OrganizeAssets { get; init; }
    public bool DuplicateDetection { get; init; }
    public bool ImportNotifications { get; init; }
}

public sealed class UefnConflictRequest
{
    public required string AssetName { get; init; }
    public required string AssetType { get; init; }
    public required string DestinationPath { get; init; }
    public required string ExistingSource { get; init; }
}

public sealed class UefnImportProgress
{
    public bool IsScanning { get; init; }
    public bool IsImporting { get; init; }
    public int Completed { get; init; }
    public int Total { get; init; }
    public string Status { get; init; } = string.Empty;
}

public sealed class UefnImportHistoryEntry : ObservableObject
{
    public string PackageName { get; set; } = string.Empty;
    public string AssetType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public EUefnImportStatus Status { get; set; } = EUefnImportStatus.Imported;
    public DateTime Timestamp { get; set; } = DateTime.Now;

    [JsonIgnore] public string TimeDisplay => Timestamp.ToString("g");

    [JsonIgnore]
    public string StatusText => Status switch
    {
        EUefnImportStatus.Imported => "Imported",
        EUefnImportStatus.Skipped => "Skipped",
        EUefnImportStatus.Failed => "Failed",
        EUefnImportStatus.Queued => "Queued",
        _ => Status.ToString()
    };

    [JsonIgnore]
    public SolidColorBrush StatusBrush => Status switch
    {
        EUefnImportStatus.Imported => SolidColorBrush.Parse("#17854F"),
        EUefnImportStatus.Skipped => SolidColorBrush.Parse("#6F6F75"),
        EUefnImportStatus.Failed => SolidColorBrush.Parse("#A61717"),
        EUefnImportStatus.Queued => SolidColorBrush.Parse("#E0A100"),
        _ => SolidColorBrush.Parse("#6F6F75")
    };
}

public sealed class UefnCompanionAssetResult
{
    public string Path { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
}

public sealed class UefnCompanionResult
{
    public bool Success { get; set; }
    public string EngineVersion { get; set; } = string.Empty;
    public string Mount { get; set; } = string.Empty;
    public List<UefnCompanionAssetResult> Imported { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public string Output { get; set; } = string.Empty;
}

public sealed class UefnImportJob
{
    public required string Id { get; init; }
    public required EUefnImportKind Kind { get; init; }
    public required string PackageName { get; init; }
    public required string AssetName { get; init; }
    public required string PrimaryFile { get; init; }
    public List<string> Files { get; init; } = [];
    public string UnsupportedReason { get; init; } = string.Empty;
}

public sealed class UefnImportRecord
{
    public string Hash { get; set; } = string.Empty;
    public string HashAlgorithm { get; set; } = "SHA256";
    public string AssetType { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string ImportedUtc { get; set; } = string.Empty;
    public List<string> AssetPaths { get; set; } = [];
    public List<string> Files { get; set; } = [];
}

public sealed class UefnImportLedger
{
    public int Version { get; set; } = 1;
    public Dictionary<string, UefnImportRecord> Assets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class UefnProjectValidationResult
{
    public bool Success { get; init; }
    public string Error { get; init; } = string.Empty;
    public List<string> Warnings { get; init; } = [];
    public UefnProject? Project { get; init; }
}

public sealed class UefnCompanionInstallResult
{
    public bool Changed { get; init; }
    public string Path { get; init; } = string.Empty;
}

public sealed class UefnSetupResult
{
    public bool Changed { get; init; }
    public bool EditorRunning { get; init; }
    public bool RequiresRestart => Changed;
    public List<string> Details { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed class UefnImportSummary
{
    public int Imported { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public int Queued { get; init; }
    public ObservableCollection<UefnImportHistoryEntry> Entries { get; init; } = [];
}
