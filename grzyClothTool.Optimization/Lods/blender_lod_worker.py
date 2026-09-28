"""grzyOptimizer LOD worker: runs inside Blender (``blender --background --python blender_lod_worker.py -- ...``).

Loads Sollumz, then reads one JSON job per line from stdin and answers one line per job on stdout:

    job:    {"id": 1, "input": "C:/tmp/x/in/foo.ydd.xml", "output_dir": "C:/tmp/x/out",
             "ratios": {"medium": 0.5, "low": 0.25}}
    answer: @@GRZY@@ {"id": 1, "ok": true, "output": "C:/tmp/x/out/foo.ydd.xml", "log": [...]}

Only lines starting with the marker are protocol; Blender and Sollumz print plenty of other things to stdout.
For every Drawable Model that has a High mesh, the missing Medium/Low levels are generated with Sollumz's
"Generate LODs" operator (edge-collapse decimation of the High mesh), then the file is exported as CodeWalker XML.
"""

import addon_utils
import argparse
import importlib
import importlib.util
import json
import os
import sys
import traceback
from pathlib import Path

import bpy

MARKER = "@@GRZY@@ "
LEVELS = ("medium", "low")


def emit(payload: dict):
    sys.stdout.write(MARKER + json.dumps(payload) + "\n")
    sys.stdout.flush()


def parse_args() -> argparse.Namespace:
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(prog="blender_lod_worker")
    parser.add_argument("--sollumz", required=True, help="'installed' or the folder of the Sollumz add-on")
    parser.add_argument("--data-dir", help="Where a bundled Sollumz keeps its Python dependencies")
    return parser.parse_args(argv)


# --------------------------------------------------------------------------------------------------------------------
# Sollumz setup
# --------------------------------------------------------------------------------------------------------------------

def load_bundled_sollumz(folder: Path, data_dir: Path | None) -> str:
    """Registers the Sollumz add-on found in ``folder`` without installing it in the user's Blender."""
    package = "grzy_sollumz"
    spec = importlib.util.spec_from_file_location(
        package, folder / "__init__.py", submodule_search_locations=[str(folder)])
    if spec is None or spec.loader is None:
        raise RuntimeError(f"'{folder}' is not a Sollumz add-on folder (no __init__.py).")
    module = importlib.util.module_from_spec(spec)
    sys.modules[package] = module
    spec.loader.exec_module(module)

    if data_dir is not None:
        # Keep the dependencies and preferences of this copy away from a Sollumz the user may have installed:
        # installing dependencies renames the existing site-packages folder, which would break that other copy.
        data_dir.mkdir(parents=True, exist_ok=True)
        known_paths = importlib.import_module(f"{package}.known_paths")
        known_paths.data_directory_path = lambda: str(data_dir)
        known_paths.config_directory_path = lambda: str(data_dir)
        known_paths.prefs_file_path = lambda: str(data_dir / "sollumz_prefs.ini")

    dependencies = importlib.import_module(f"{package}.dependencies")
    dependencies.mount_dependencies()
    if not dependencies.has_required_dependencies():
        print("grzyOptimizer: installing Sollumz dependencies (first run only)...", flush=True)
        dependencies.unmount_dependencies()
        if not dependencies.install_dependencies(online_access_override=True):
            raise RuntimeError("Could not install the Sollumz dependencies (szio). Check the internet connection.")
        importlib.invalidate_caches()
        dependencies.mount_dependencies()
        if not dependencies.has_required_dependencies():
            raise RuntimeError("Sollumz dependencies were installed but could not be loaded.")

    # Enable through addon_utils (not module.register()) so Sollumz finds its preferences under this name.
    # __time__ tells addon_utils the module is current; otherwise it tries to reload it from the add-on paths.
    module.__time__ = os.path.getmtime(module.__file__)
    errors = []
    addon_utils.enable(package, default_set=True, handle_error=errors.append)
    if errors or not hasattr(bpy.types.Object, "sollum_type"):
        raise RuntimeError(f"Sollumz could not be enabled: {errors[0] if errors else 'unknown error'}")
    return package


def find_installed_sollumz() -> str:
    """Package name of the Sollumz add-on/extension enabled in the user's Blender preferences."""
    for name, module in list(sys.modules.items()):
        if name.endswith(".ydr.ydrexport") or name.endswith(".sollumz_operators"):
            return name.rsplit(".", 2 if name.endswith(".ydr.ydrexport") else 1)[0]
    raise RuntimeError("Sollumz is not enabled in Blender. Enable it in Preferences > Add-ons, or use --sollumz bundled.")


def patch_skinning_without_skeleton(package: str):
    """Keeps the skinning of ped components, which have no skeleton of their own.

    Sollumz only reads and writes blend weights when the drawable has an armature (normally the freemode ped
    skeleton, imported from a .yft). Without it the weights would be dropped, so:
      - on import, the vertex groups are created anyway, named ``UNKNOWN_BONE.<bone index>`` (Sollumz's own naming
        for bones it cannot resolve);
      - on export, those groups are mapped back to the same bone index.
    The generated LODs then keep exactly the bone bindings of the High model.
    """
    ydrimport = importlib.import_module(f"{package}.ydr.ydrimport")
    ydrexport = importlib.import_module(f"{package}.ydr.ydrexport")
    vbb = importlib.import_module(f"{package}.ydr.vertex_buffer_builder")
    for module, name in ((ydrimport, "create_lod_meshes"), (ydrexport, "try_get_bone_by_vgroup")):
        if not callable(getattr(module, name, None)):
            raise RuntimeError(f"Unsupported Sollumz version (no {module.__name__}.{name}); use --sollumz bundled.")

    original_create_lod_meshes = ydrimport.create_lod_meshes

    def create_lod_meshes(model_data, model_obj, materials, hi_materials, bones=None):
        # An empty bone list (instead of None) makes Sollumz create the UNKNOWN_BONE.<n> vertex groups.
        return original_create_lod_meshes(model_data, model_obj, materials, hi_materials, [] if bones is None else bones)

    ydrimport.create_lod_meshes = create_lod_meshes
    original = vbb.try_get_bone_by_vgroup

    def try_get_bone_by_vgroup(obj, armature_obj):
        result = original(obj, armature_obj)
        if result is not None or armature_obj is not None or not obj.vertex_groups:
            return result

        mapping = {}
        for i, group in enumerate(obj.vertex_groups):
            if group.name.startswith("UNKNOWN_BONE."):
                mapping[i] = int(group.name.rsplit(".", 1)[1])
            elif group.name == getattr(vbb, "CLOTH_CHAR_VERTEX_GROUP_NAME", None):
                mapping[i] = vbb.VGROUP_CLOTH_ID
            else:
                mapping[i] = 0
        return mapping

    ydrexport.try_get_bone_by_vgroup = try_get_bone_by_vgroup


# --------------------------------------------------------------------------------------------------------------------
# Jobs
# --------------------------------------------------------------------------------------------------------------------

def clear_scene():
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    for collection in list(bpy.data.collections):
        bpy.data.collections.remove(collection)
    for datablocks in (bpy.data.meshes, bpy.data.materials, bpy.data.images, bpy.data.armatures, bpy.data.node_groups):
        for block in list(datablocks):
            datablocks.remove(block)
    bpy.data.orphans_purge(do_recursive=True)


def tri_count(mesh) -> int:
    mesh.calc_loop_triangles()
    return len(mesh.loop_triangles)


def generate_lods(package: str, ratios: dict[str, float], log: list[str]) -> int:
    props = importlib.import_module(f"{package}.sollumz_properties")
    SollumType, LODLevel = props.SollumType, props.LODLevel
    by_name = {"medium": LODLevel.MEDIUM, "low": LODLevel.LOW}

    generated = 0
    view_layer = bpy.context.view_layer
    for obj in [o for o in bpy.data.objects if o.sollum_type == SollumType.DRAWABLE_MODEL]:
        lods = obj.sz_lods
        high = lods.get_lod(LODLevel.HIGH).mesh
        if high is None:
            continue

        missing = [name for name in LEVELS if name in ratios and lods.get_lod(by_name[name]).mesh is None]
        if not missing:
            continue

        lods.active_lod_level = LODLevel.HIGH
        for other in view_layer.objects:
            other.select_set(False)
        obj.hide_set(False)
        obj.select_set(True)
        view_layer.objects.active = obj

        # per_lod_ratio is indexed by LODLevel order: very high, high, medium, low, very low.
        per_lod_ratio = (1.0, 1.0, ratios.get("medium", 1.0), ratios.get("low", 1.0), 0.15)
        result = bpy.ops.sollumz.auto_lod(
            ref_mesh_name=high.name,
            levels={by_name[name].value for name in missing},
            decimate_method="COLLAPSE",
            decimate_from_original=True,
            use_per_lod_ratios=True,
            per_lod_ratio=per_lod_ratio,
            auto_set_distances=False,
            auto_merge_materials=False,
        )
        if result != {"FINISHED"}:
            raise RuntimeError(f"Sollumz 'Generate LODs' failed on '{obj.name}' ({result}).")

        stats = ", ".join(f"{name} {tri_count(lods.get_lod(by_name[name]).mesh)}" for name in missing)
        log.append(f"{obj.name}: high {tri_count(high)} tris -> {stats}")
        generated += len(missing)

    return generated


def run_job(package: str, job: dict) -> dict:
    log: list[str] = []
    source = Path(job["input"])
    output_dir = Path(job["output_dir"])
    output_dir.mkdir(parents=True, exist_ok=True)

    clear_scene()
    result = bpy.ops.sollumz.import_assets(
        directory=str(source.parent),
        files=[{"name": source.name}],
        use_custom_settings=True,
        split_by_group=False,
        dwd_import_external_skeleton="NO",
        textures_mode="PACK",
    )
    if result != {"FINISHED"}:
        raise RuntimeError(f"Sollumz could not import '{source.name}' ({result}).")

    generated = generate_lods(package, job.get("ratios", {}), log)
    if generated == 0:
        return {"ok": True, "output": None, "log": log + ["nothing to generate"]}

    result = bpy.ops.sollumz.export_assets(
        directory=str(output_dir),
        direct_export=True,
        use_custom_settings=True,
        target_formats={"CWXML"},
        target_versions={"GEN8"},
        limit_to_selected=False,
        exclude_skeleton=False,
        apply_transforms=False,
        export_ytyps=False,
        export_ymaps=False,
        export_ytds=False,
    )
    exported = sorted(output_dir.rglob("*.ydd.xml"))
    if result != {"FINISHED"} or not exported:
        raise RuntimeError(f"Sollumz could not export '{source.name}' ({result}).")

    return {"ok": True, "output": str(exported[0]), "log": log}


def main():
    args = parse_args()
    try:
        if args.sollumz == "installed":
            package = find_installed_sollumz()
        else:
            package = load_bundled_sollumz(Path(args.sollumz), Path(args.data_dir) if args.data_dir else None)
        if not hasattr(bpy.ops.sollumz, "auto_lod"):
            raise RuntimeError("This Sollumz version has no 'Generate LODs' tool; Sollumz 2.9 or newer is required.")
        patch_skinning_without_skeleton(package)
    except Exception as ex:
        emit({"fatal": f"{ex}", "trace": traceback.format_exc()})
        return

    sz_version = getattr(sys.modules.get(package), "bl_info", {}).get("version", ())
    emit({"ready": True, "blender": bpy.app.version_string, "sollumz": ".".join(map(str, sz_version))})

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        job = json.loads(line)
        try:
            answer = run_job(package, job)
        except Exception as ex:
            answer = {"ok": False, "error": f"{ex}", "trace": traceback.format_exc()}
        answer["id"] = job.get("id")
        emit(answer)


main()
