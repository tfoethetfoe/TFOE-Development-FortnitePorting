using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace FortnitePorting.Models.Uefn;

/// <summary>
/// A validated Unreal Editor for Fortnite project.
///
/// UEFN projects are not plain Unreal projects: the project browser file is a
/// *.uefnproject descriptor, the assets live in Plugins/&lt;Project&gt;/Content, and in the
/// editor those assets are mounted at /&lt;Project&gt;/ rather than /Game/.
/// </summary>
public sealed class UefnProject
{
    public required string RootFolder { get; init; }
    public required string ProjectFile { get; init; }
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string ProjectId { get; init; }
    public required string PluginFolder { get; init; }
    public required string PluginFile { get; init; }
    public required string ContentRoot { get; init; }
    public required string Mount { get; init; }
    public required string CompanionScriptPath { get; init; }
    public required string SavedFolder { get; init; }
    public required string ConfigFile { get; init; }

    public bool PythonEnabled { get; init; }
    public bool RemoteExecutionEnabled { get; init; }
    public bool CompanionInstalled { get; init; }
    public bool CompanionOutdated { get; init; }

    public string PythonFolder => Path.Combine(ContentRoot, "Python");
    public string ImportRoot => Path.Combine(ContentRoot, "FortnitePorting");
    public string ContentBrowserRoot => $"/{Mount.Trim('/')}";
    public string QueueFolder => Path.Combine(SavedFolder, "Queue");
    public string StagingRoot => Path.Combine(SavedFolder, "Staging");
    public string LedgerFile => Path.Combine(SavedFolder, "import_records.json");
    public string BackupsFolder => Path.Combine(SavedFolder, "Backups");

    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? Name : Title;

    public static string DefaultProjectsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Fortnite Projects");

    public static UefnProjectValidationResult TryOpen(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Failure("Choose the folder of a UEFN project (the folder that contains the .uefnproject file).");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception e)
        {
            return Failure($"That path is not valid: {e.Message}");
        }

        var root = FindProjectRoot(fullPath, out var projectFile);
        if (root is null || projectFile is null)
            return Failure("This is not a UEFN project. Select the project folder that contains a .uefnproject file (usually Documents/Fortnite Projects/<Project>).");

        JObject descriptor;
        try
        {
            descriptor = JObject.Parse(File.ReadAllText(projectFile));
        }
        catch (Exception e)
        {
            return Failure($"The .uefnproject file could not be read: {e.Message}");
        }

        var name = Path.GetFileNameWithoutExtension(projectFile);
        if (string.IsNullOrWhiteSpace(name))
            return Failure("The .uefnproject file has no project name.");

        var title = descriptor.Value<string>("title")?.Trim();
        var projectId = descriptor.SelectToken("bindings.projectId")?.Value<string>()?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(projectId))
            return Failure("This .uefnproject file is missing its UEFN project metadata (title/project id). It may be damaged or belong to another tool.");

        var pluginsFolder = Path.Combine(root, "Plugins");
        if (!Directory.Exists(pluginsFolder))
            return Failure("This UEFN project has no Plugins folder. Open it once in UEFN and try again.");

        string[] pluginFiles;
        try
        {
            pluginFiles = Directory.EnumerateFiles(pluginsFolder, "*.uplugin", SearchOption.AllDirectories).ToArray();
        }
        catch (Exception e)
        {
            return Failure($"The project's Plugins folder could not be read: {e.Message}");
        }
        var pluginFile = pluginFiles.FirstOrDefault(file => Path.GetFileNameWithoutExtension(file)
                               .Equals(name, StringComparison.OrdinalIgnoreCase))
                         ?? (pluginFiles.Length == 1 ? pluginFiles[0] : null);

        if (pluginFile is null)
            return Failure("This UEFN project's plugin descriptor (<Project>.uplugin) could not be found. Open the project in UEFN once and try again.");

        var pluginFolder = Path.GetDirectoryName(pluginFile)!;
        var contentRoot = Path.Combine(pluginFolder, "Content");
        if (!Directory.Exists(contentRoot))
            return Failure($"This UEFN project has no project content folder at {contentRoot}. Open the project in UEFN once and try again.");

        var project = new UefnProject
        {
            RootFolder = root,
            ProjectFile = projectFile,
            Name = name,
            Title = string.IsNullOrWhiteSpace(title) ? name : title,
            ProjectId = projectId,
            PluginFolder = pluginFolder,
            PluginFile = pluginFile,
            ContentRoot = contentRoot,
            Mount = Path.GetFileName(pluginFolder),
            CompanionScriptPath = Path.Combine(contentRoot, "Python", UefnCompanionScript.ScriptFileName),
            SavedFolder = Path.Combine(root, "Saved", "FortnitePorting"),
            ConfigFile = Path.Combine(root, "Config", "DefaultEngine.ini"),
            PythonEnabled = descriptor.SelectToken("dataSets.experimental.pythonExperimental.bEnablePythonForProject")?.Value<bool>() ?? false,
            RemoteExecutionEnabled = IsRemoteExecutionEnabled(Path.Combine(root, "Config", "DefaultEngine.ini"))
        };

        var companionInstalled = File.Exists(project.CompanionScriptPath);
        var companionOutdated = false;
        if (companionInstalled)
        {
            try
            {
                companionOutdated = !File.ReadAllText(project.CompanionScriptPath)
                    .Contains($"FORTNITE_PORTING_UEFN_VERSION = \"{UefnCompanionScript.CurrentVersion}\"", StringComparison.Ordinal);
            }
            catch
            {
                companionOutdated = true;
            }
        }

        var warnings = new List<string>();
        if (!project.PythonEnabled)
            warnings.Add("Python Editor Scripting is not enabled for this project. Use Set Up UEFN Project, then restart UEFN.");
        if (!project.RemoteExecutionEnabled)
            warnings.Add("Python remote execution is not enabled. Use Set Up UEFN Project, then restart UEFN.");
        if (!companionInstalled)
            warnings.Add("The UEFN companion importer is not installed in this project yet.");

        return new UefnProjectValidationResult
        {
            Success = true,
            Project = new UefnProject
            {
                RootFolder = project.RootFolder,
                ProjectFile = project.ProjectFile,
                Name = project.Name,
                Title = project.Title,
                ProjectId = project.ProjectId,
                PluginFolder = project.PluginFolder,
                PluginFile = project.PluginFile,
                ContentRoot = project.ContentRoot,
                Mount = project.Mount,
                CompanionScriptPath = project.CompanionScriptPath,
                SavedFolder = project.SavedFolder,
                ConfigFile = project.ConfigFile,
                PythonEnabled = project.PythonEnabled,
                RemoteExecutionEnabled = project.RemoteExecutionEnabled,
                CompanionInstalled = companionInstalled,
                CompanionOutdated = companionOutdated
            },
            Warnings = warnings
        };
    }

    public static UefnProject Reopen(UefnProject project) => TryOpen(project.RootFolder).Project ?? project;

    public bool IsWithinContent(string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var content = Path.GetFullPath(ContentRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalized.Equals(content, StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith(content + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsForbiddenImportFolder(string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);

        var separator = Path.DirectorySeparatorChar;
        return normalized.EndsWith($"{separator}Python", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains($"{separator}Python{separator}", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains($"{separator}__ExternalActors__", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains($"{separator}__ExternalObjects__", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindProjectRoot(string fullPath, out string? projectFile)
    {
        projectFile = null;

        string startDirectory;
        if (File.Exists(fullPath))
        {
            if (!fullPath.EndsWith(".uefnproject", StringComparison.OrdinalIgnoreCase))
                return null;

            projectFile = fullPath;
            startDirectory = Path.GetDirectoryName(fullPath)!;
        }
        else if (Directory.Exists(fullPath))
        {
            startDirectory = fullPath;
        }
        else
        {
            return null;
        }

        if (projectFile is null)
        {
            var direct = SafeProjectFiles(startDirectory);
            if (direct.Length == 1)
            {
                projectFile = direct[0];
            }
        }

        if (projectFile is not null)
            return Path.GetDirectoryName(projectFile);

        // Allow the user to pick Plugins/<Project>/Content (or any folder inside the project).
        var current = new DirectoryInfo(startDirectory);
        for (var depth = 0; depth < 5 && current.Parent is not null; depth++)
        {
            current = current.Parent;
            var candidates = SafeProjectFiles(current.FullName);
            if (candidates.Length == 1)
            {
                projectFile = candidates[0];
                return current.FullName;
            }

            if (candidates.Length > 1)
                return null;
        }

        return null;
    }

    private static string[] SafeProjectFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.uefnproject", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static bool IsRemoteExecutionEnabled(string configFile)
    {
        try
        {
            if (!File.Exists(configFile))
                return false;

            var lines = File.ReadAllLines(configFile);
            var inSection = false;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    inSection = trimmed.Equals("[/Script/PythonScriptPlugin.PythonScriptPluginSettings]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection) continue;
                var separator = trimmed.IndexOf('=');
                if (separator <= 0) continue;

                var key = trimmed[..separator].Trim();
                var value = trimmed[(separator + 1)..].Trim();
                if (key.Equals("bRemoteExecution", StringComparison.OrdinalIgnoreCase))
                    return value.Equals("True", StringComparison.OrdinalIgnoreCase) || value == "1";
            }
        }
        catch
        {
            // A malformed config simply means "not configured"; the setup action can repair it.
        }

        return false;
    }

    private static UefnProjectValidationResult Failure(string message) => new() { Success = false, Error = message };
}
