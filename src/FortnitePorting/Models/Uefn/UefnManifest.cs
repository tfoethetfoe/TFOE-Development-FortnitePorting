using System.Collections.Generic;

namespace FortnitePorting.Models.Uefn;

public sealed class UefnManifestAsset
{
    public string Kind { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string AssetName { get; set; } = string.Empty;
    public string DestinationRelative { get; set; } = string.Empty;
}

public sealed class UefnImportManifest
{
    public int Version { get; set; } = 1;
    public string JobId { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public string AssetName { get; set; } = string.Empty;
    public string AssetType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string ImportRelative { get; set; } = string.Empty;
    public string LedgerKey { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string StagingDirectory { get; set; } = string.Empty;
    public string PrimarySource { get; set; } = string.Empty;
    public string ResultPath { get; set; } = string.Empty;
    public bool ImportTextures { get; set; }
    public bool ImportMaterials { get; set; }
    public bool ReplaceExisting { get; set; }
    public string MountHint { get; set; } = string.Empty;
    public List<UefnManifestAsset> Assets { get; set; } = [];
}
