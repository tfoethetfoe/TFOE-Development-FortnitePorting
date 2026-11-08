using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Platform;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FortnitePorting.Models.Uefn;

public static class UefnCompanionScript
{
    public const string CurrentVersion = "1.0.0";
    public const string ScriptFileName = "fortnite_porting_uefn.py";

    private const string ScriptResource =
        "avares://FortnitePorting/UEFN/Content/Python/fortnite_porting_uefn.py";

    public static string Load() => LoadFromResource();

    public static UefnCompanionInstallResult Install(UefnProject project)
    {
        var directory = project.PythonFolder;
        Directory.CreateDirectory(directory);

        var destination = project.CompanionScriptPath;
        var script = LoadFromResource();

        if (File.Exists(destination))
        {
            var existing = File.ReadAllText(destination);
            if (existing.Equals(script, StringComparison.Ordinal))
                return new UefnCompanionInstallResult { Changed = false, Path = destination };
        }

        var temporary = destination + ".tmp";
        File.WriteAllText(temporary, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, destination, overwrite: true);

        return new UefnCompanionInstallResult { Changed = true, Path = destination };
    }

    public static UefnSetupResult Configure(UefnProject project)
    {
        var details = new List<string>();
        var warnings = new List<string>();
        var changed = false;

        var install = Install(project);
        if (install.Changed)
        {
            changed = true;
            details.Add($"Installed {ScriptFileName}");
        }

        // Enable Python Editor Scripting for this UEFN project. UEFN stores this in the
        // .uefnproject descriptor; it is read when the project next opens.
        try
        {
            var descriptorText = File.ReadAllText(project.ProjectFile);
            var descriptor = JObject.Parse(descriptorText);
            var pythonWasEnabled = descriptor
                .SelectToken("dataSets.experimental.pythonExperimental.bEnablePythonForProject")?.Value<bool>() ?? false;

            if (!pythonWasEnabled)
            {
                Backup(project, project.ProjectFile);
                SetNestedValue(descriptor, "dataSets", "experimental", "pythonExperimental", "bEnablePythonForProject", true);
                File.WriteAllText(project.ProjectFile, descriptor.ToString(Formatting.Indented), new UTF8Encoding(false));
                changed = true;
                details.Add("Enabled Python Editor Scripting for the project");
            }
        }
        catch (Exception e)
        {
            warnings.Add($"Could not update the .uefnproject file: {e.Message}");
        }

        // Remote execution is an editor-startup setting, stored in the project config.
        try
        {
            var configDirectory = Path.GetDirectoryName(project.ConfigFile)!;
            Directory.CreateDirectory(configDirectory);

            var config = File.Exists(project.ConfigFile) ? File.ReadAllText(project.ConfigFile) : string.Empty;
            var updated = SetIniValues(config,
                "[/Script/PythonScriptPlugin.PythonScriptPluginSettings]",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["bRemoteExecution"] = "True",
                    ["RemoteExecutionMulticastGroupEndpoint"] = "239.0.0.1:6766",
                    ["RemoteExecutionMulticastBindAddress"] = "127.0.0.1",
                    ["RemoteExecutionMulticastTtl"] = "0"
                });

            if (!updated.Equals(config, StringComparison.Ordinal))
            {
                Backup(project, project.ConfigFile);
                File.WriteAllText(project.ConfigFile, updated, new UTF8Encoding(false));
                changed = true;
                details.Add("Enabled Python remote execution in Config/DefaultEngine.ini");
            }
        }
        catch (Exception e)
        {
            warnings.Add($"Could not update Config/DefaultEngine.ini: {e.Message}");
        }

        if (!File.Exists(project.ConfigFile))
            warnings.Add("The project config file was created. Restart UEFN so remote execution starts.");

        var editorRunning = IsEditorRunning();
        if (editorRunning)
            warnings.Add("UEFN is running. Close and reopen the project so the new Python and remote execution settings take effect.");

        if (!details.Any() && !warnings.Any())
            warnings.Add("Python and remote execution are already configured. If the editor is not connected, reopen the project.");

        return new UefnSetupResult
        {
            Changed = changed,
            EditorRunning = editorRunning,
            Details = details,
            Warnings = warnings
        };
    }

    public static bool IsEditorRunning()
    {
        try
        {
            return Process.GetProcessesByName("UnrealEditorFortnite-Win64-Shipping").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string LoadFromResource()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(ScriptResource));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                "The UEFN companion importer was not bundled with this build of Fortnite Porting. " +
                $"Rebuild the application (missing resource '{ScriptResource}').", e);
        }
    }

    private static void Backup(UefnProject project, string file)
    {
        if (!File.Exists(file)) return;

        Directory.CreateDirectory(project.BackupsFolder);
        var name = Path.GetFileName(file);
        var destination = Path.Combine(project.BackupsFolder, $"{name}.{DateTime.Now:yyyyMMdd-HHmmss-fff}.bak");
        File.Copy(file, destination, overwrite: false);
    }

    private static void SetNestedValue(JObject root, params object[] path)
    {
        var current = root;
        for (var index = 0; index < path.Length - 2; index++)
        {
            var key = (string) path[index];
            if (current[key] is not JObject child)
            {
                child = new JObject();
                current[key] = child;
            }

            current = child;
        }

        current[(string) path[^2]] = JToken.FromObject(path[^1]);
    }

    private static string SetIniValues(string text, string section, IReadOnlyDictionary<string, string> values)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : Environment.NewLine;
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        var sectionIndex = lines.FindIndex(line => line.Trim().Equals(section, StringComparison.OrdinalIgnoreCase));
        if (sectionIndex < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add(string.Empty);
            lines.Add(section);
            lines.AddRange(values.Select(pair => $"{pair.Key}={pair.Value}"));
            return string.Join(newline, lines) + newline;
        }

        var endIndex = lines.Count;
        for (var index = sectionIndex + 1; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                endIndex = index;
                break;
            }
        }

        var remaining = new HashSet<string>(values.Keys, StringComparer.OrdinalIgnoreCase);
        for (var index = sectionIndex + 1; index < endIndex; index++)
        {
            var line = lines[index];
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var key = line[..separator].Trim();
            if (!values.TryGetValue(key, out var value)) continue;

            lines[index] = $"{key}={value}";
            remaining.Remove(key);
        }

        var additions = remaining.Select(key => $"{key}={values[key]}").ToList();
        if (additions.Count > 0)
            lines.InsertRange(endIndex, additions);

        return string.Join(newline, lines) + newline;
    }
}
