"""Fortnite Porting -> UEFN importer.

Installed by Fortnite Porting into <Project>/Plugins/<Project>/Content/Python/.
UEFN's Python Editor Scripting runs this file through Epic's remote execution protocol,
or manually via Tools -> Execute Python Script.

It uses UEFN's own AssetImportTask/Interchange pipeline. It never writes .uasset bytes.
"""

import json
import os
import traceback

import unreal

FORTNITE_PORTING_UEFN_VERSION = "1.0.0"


def _log(message):
    unreal.log("[FortnitePorting] " + str(message))


def _log_warning(message):
    unreal.log_warning("[FortnitePorting] " + str(message))


def _log_error(message):
    unreal.log_error("[FortnitePorting] " + str(message))


def _project_root():
    try:
        root = unreal.EditorAssetLibrary.get_project_root_asset_directory()
        if root:
            return str(root).replace("\\", "/").rstrip("/")
    except Exception as error:
        _log_warning("Could not read the project asset root: " + str(error))

    raise RuntimeError(
        "UEFN did not return a project asset root. Open a UEFN project, not a plain Unreal project."
    )


def _saved_dir():
    try:
        saved = unreal.Paths.project_saved_dir()
        if saved:
            return str(saved)
    except Exception:
        pass

    raise RuntimeError("UEFN did not return the project Saved directory.")


def _queue_dir():
    return os.path.join(_saved_dir(), "FortnitePorting", "Queue")


def _safe_relative(relative):
    parts = []
    for part in str(relative or "").replace("\\", "/").split("/"):
        part = part.strip()
        if part and part not in (".", ".."):
            parts.append(part)
    return "/".join(parts)


def _safe_asset_name(name):
    allowed = []
    for character in str(name or ""):
        allowed.append(character if character.isalnum() or character == "_" else "_")
    value = "".join(allowed).strip("_")
    if not value:
        value = "Asset"
    if value[0].isdigit():
        value = "_" + value
    return value[:100]


def _asset_class(path):
    try:
        data = unreal.EditorAssetLibrary.find_asset_data(path)
        if not data:
            return ""
        class_path = data.asset_class_path
        return str(class_path.asset_name)
    except Exception:
        return ""


def _list_assets(folder):
    try:
        return set(str(path) for path in unreal.EditorAssetLibrary.list_assets(
            folder, recursive=True, include_folder=False))
    except Exception:
        return set()


def _write_result(result, result_path):
    if not result_path:
        return

    try:
        os.makedirs(os.path.dirname(result_path), exist_ok=True)
        with open(result_path, "w", encoding="utf-8") as handle:
            json.dump(result, handle, ensure_ascii=False, indent=2)
    except Exception as error:
        _log_error("Could not write the Fortnite Porting result file: " + str(error))


def _print_result(result):
    try:
        _log("FORTNITE_PORTING_RESULT:" + json.dumps(result, ensure_ascii=False))
    except Exception:
        pass


def _import_asset(asset, destination_root, replace_existing):
    source = str(asset.get("Source") or "")
    if not source or not os.path.isfile(source):
        raise RuntimeError("The staged file is missing: " + source)

    relative = _safe_relative(asset.get("DestinationRelative"))
    asset_name = _safe_asset_name(asset.get("AssetName"))

    destination = destination_root
    if relative:
        destination += "/" + relative

    before = _list_assets(destination)

    task = unreal.AssetImportTask()
    task.set_editor_property("filename", source)
    task.set_editor_property("destination_path", destination)
    task.set_editor_property("destination_name", asset_name)
    task.set_editor_property("automated", True)
    task.set_editor_property("save", True)
    task.set_editor_property("replace_existing", bool(replace_existing))

    _log("Importing " + source + " -> " + destination + "/" + asset_name)
    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])

    imported = []
    try:
        imported = [str(path) for path in (task.get_editor_property("imported_object_paths") or [])]
    except Exception as error:
        _log_warning("Could not read imported_object_paths: " + str(error))

    if not imported:
        after = _list_assets(destination)
        imported = sorted(after - before)

    if not imported:
        expected = destination + "/" + asset_name
        if unreal.EditorAssetLibrary.does_asset_exist(expected):
            imported = [expected]

    if not imported:
        raise RuntimeError(
            "UEFN reported no imported assets for " + asset_name +
            ". Check the Output Log for glTF/Interchange errors.")

    results = []
    for path in imported:
        results.append({
            "Path": path,
            "ClassName": _asset_class(path),
            "Kind": str(asset.get("Kind") or ""),
        })

    return results


def run_manifest(manifest_path):
    """Import one manifest. Used by Fortnite Porting's remote-execution command."""
    result = {
        "Success": False,
        "EngineVersion": "",
        "Mount": "",
        "Imported": [],
        "Errors": [],
        "Warnings": [],
        "Output": "",
    }

    manifest = None
    result_path = ""
    try:
        with open(manifest_path, "r", encoding="utf-8") as handle:
            manifest = json.load(handle)

        result_path = str(manifest.get("ResultPath") or "")
        try:
            result["EngineVersion"] = str(unreal.SystemLibrary.get_engine_version())
        except Exception as error:
            result["Warnings"].append("Could not read the UEFN engine version: " + str(error))

        root = _project_root()
        result["Mount"] = root
        if not manifest.get("ImportTextures"):
            result["Warnings"].append("Textures were stripped from the staged mesh (Import Textures is off).")
        if not manifest.get("ImportMaterials"):
            result["Warnings"].append("Materials were stripped from the staged mesh (Import Materials is off).")

        assets = manifest.get("Assets") or []
        if not assets:
            raise RuntimeError("The manifest contains no assets.")

        for asset in assets:
            result["Imported"].extend(
                _import_asset(asset, root, bool(manifest.get("ReplaceExisting"))))

        if not result["Imported"]:
            raise RuntimeError("UEFN did not create any assets.")

        result["Success"] = True
        _log("Imported " + str(len(result["Imported"])) + " asset(s) for " +
             str(manifest.get("AssetName") or manifest_path))
    except Exception as error:
        message = str(error)
        result["Errors"].append(message)
        _log_error(message)
        _log_error(traceback.format_exc())
    finally:
        _write_result(result, result_path)
        _print_result(result)

    return result


def run_pending_queue():
    """Process every queued manifest. Used by Tools -> Execute Python Script."""
    queue = _queue_dir()
    if not os.path.isdir(queue):
        _log("No queue folder exists at " + queue)
        return []

    results = []
    for name in sorted(os.listdir(queue)):
        if not name.endswith(".json") or name.endswith(".result.json"):
            continue

        manifest_path = os.path.join(queue, name)
        _log("Processing queued manifest " + manifest_path)
        results.append(run_manifest(manifest_path))

    imported = sum(len(result.get("Imported", [])) for result in results)
    failed = sum(1 for result in results if not result.get("Success"))
    _log("Queue complete: " + str(imported) + " asset(s) imported, " +
         str(failed) + " manifest(s) failed.")
    return results


# Tools -> Execute Python Script runs this file as __main__. Fortnite Porting imports
# this module and calls run_manifest explicitly, so normal auto-imports do not process
# unrelated queued jobs.
if __name__ == "__main__":
    run_pending_queue()
