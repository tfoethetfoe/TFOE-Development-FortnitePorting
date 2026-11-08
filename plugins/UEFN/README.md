# UEFN Auto Import

This plugin connects Fortnite Porting's exports to an Unreal Editor for Fortnite (UEFN) project.
Select your project once, export as usual, and the plugin stages and imports the new assets through
UEFN's own Python Editor Scripting / Interchange importer.

## Requirements

- Windows 10 or 11
- Fortnite Porting 4.x
- UEFN with the project you want to import into
- Fortnite Porting's mesh format set to **glTF 2.0 (binary)** (`.glb`) or **glTF 2.0** (`.gltf`)
  in **Settings → Export → Folder → Mesh and Animation Format**
- Textures exported as PNG/JPG/JPEG/TGA/BMP/DDS/EXR/HDR/PCX/PSD/TIF/TIFF (WebP is converted to PNG automatically)

UEFN's Python importer does not read Fortnite Porting's UEFormat `.uemodel`/`.ueanim` files.
The plugin reports those files clearly instead of pretending they were imported.

## Install

1. Build Fortnite Porting from this repository (or use a release that includes this page).
2. Open Fortnite Porting and go to **Plugins → UEFN**.
3. Select **Browse** under **UEFN Project** and choose the folder containing your
   `<Project>.uefnproject` file (usually `Documents/Fortnite Projects/<Project>`).
4. Select **Browse** under **Export Folder** and choose the folder Fortnite Porting exports to.
5. Select **Set Up UEFN Project**. This installs the companion Python importer into
   `<Project>/Plugins/<Project>/Content/Python/`, enables Python Editor Scripting and Python
   remote execution for the project, and backs up the files it changes.
6. Close and reopen the project in UEFN. Python and remote-execution settings are read at startup.
7. Use **Check UEFN** to confirm the connection. Then export from Fortnite Porting, or press
   **Import Now**.

The companion script is also available at
`plugins/UEFN/Content/Python/fortnite_porting_uefn.py` in the repository.

## What Set Up UEFN Project changes

The setup command edits only the selected project and creates timestamped backups under
`<Project>/Saved/FortnitePorting/Backups/`:

- `<Project>.uefnproject`:
  `dataSets.experimental.pythonExperimental.bEnablePythonForProject = true`
- `Config/DefaultEngine.ini`:
  `[/Script/PythonScriptPlugin.PythonScriptPluginSettings]` with `bRemoteExecution=True` and
  the multicast settings documented by Epic for local remote execution.

It also installs `fortnite_porting_uefn.py` in the project's plugin content folder.
UEFN must be restarted after these changes.

## How importing works

1. A file watcher monitors the selected export folder. Changes are debounced until the file is
   closed and its size is stable, so partially written exports are not imported.
2. `.gltf` files are grouped with their relative `.bin` and image dependencies. The group is copied
   to a staging folder inside `<Project>/Saved/FortnitePorting/Staging`.
3. If **Import Textures** or **Import Materials** is off, only the staged copy of the mesh is
   rewritten to remove that data. The original Fortnite Porting export is never modified.
4. SHA-256 hashes are recorded in `<Project>/Saved/FortnitePorting/import_records.json`.
   Identical content is skipped. A same-name asset with different content asks whether to
   overwrite, keep both (rename), or skip.
5. The staged manifest is sent to UEFN through Epic's Python remote execution protocol.
   The companion script calls `unreal.AssetImportTask` and
   `AssetToolsHelpers.get_import_asset_tools().import_asset_tasks(...)`, which uses UEFN's
   Interchange/glTF pipeline to create real StaticMesh and Texture2D assets.
6. If UEFN is not running or remote execution is unavailable, the job stays queued. Start UEFN,
   then press **Import Now**. You can also run the companion script manually from
   **Tools → Execute Python Script** in UEFN; it processes the queued manifests.

Assets are imported under `/<Project>/FortnitePorting/` by default. UEFN projects mount their
content at `/<Project>/`, not `/Game/`. Enable **Organize Assets** to sort into `Meshes`,
`Characters`, `Environment`, `Props`, `Weapons`, `Textures`, and `Materials`.

## Troubleshooting

**No UEFN editor answers / queued imports**

- Run **Set Up UEFN Project** again, then fully close and reopen the project.
- Make sure only one program is bound to UDP port 6766. Other Python remote-execution tools can
  hold it open.
- In UEFN, check **Window → Output Log** for Python errors.
- UEFN 41.x had a regression where Python could be disabled at startup; update UEFN if possible.

**The import command runs but no assets appear**

- Confirm the mesh is `.glb`/`.gltf`, not `.uemodel`.
- UEFN 43.00+ supports glTF static-mesh import. Update UEFN if glTF import is unavailable.
- Check that the destination folder is inside the project's
  `Plugins/<Project>/Content` folder and is not `Content/Python`.

**Permission errors**

- Close UEFN while running setup, or make sure the project is not read-only.
- Fortnite Porting needs write access to the project's `Content`, `Config`, and `Saved` folders.

**Import Textures/Materials toggles do not affect the original export**

That is intentional. The toggles rewrite only the staged copy sent to UEFN.

## Limitations

- Automatic import requires UEFN's Python remote execution. On UEFN builds where it is unavailable,
  the plugin queues the job and gives you the Tools → Execute Python Script fallback; it does not
  silently copy files and call that an import.
- `.ueanim`/`.uemodel`/`.uepose` cannot be imported through this path. Use glTF for meshes and
  UEFN's supported animation import workflow for animations.
- glTF import support and material/texture behavior depend on the installed UEFN version. Check the
  UEFN Output Log if a particular asset does not import.
- The plugin was compiled and its staging/hash/queue logic can be tested without UEFN, but the
  editor-side import must be verified against the UEFN version you have installed.
