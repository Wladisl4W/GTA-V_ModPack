import os
import sys
import traceback
from pathlib import Path

import bpy
from mathutils import Matrix, Vector


def main():
    args = sys.argv[sys.argv.index("--") + 1:]
    source = Path(args[0])
    name = args[1]
    output = Path(args[2])
    scale = float(args[3]) if len(args) > 3 else 1.0
    if not 0.1 <= scale <= 40.0:
        raise ValueError("Scale must be between 0.1 and 40.0")
    prop_name = "prop_p2p_" + name.lower()
    if not source.is_dir() or len(list(source.iterdir())) != 4:
        raise ValueError("Input directory must contain exactly four ped files")
    if not name or not all(c.isascii() and (c.isalnum() or c == "_") for c in name):
        raise ValueError("Ped name must contain only ASCII letters, digits and underscore")
    for ext in ("ydd", "ytd", "yft", "ymt"):
        path = source / (name + "." + ext)
        if not path.is_file() or path.stat().st_size < 16:
            raise ValueError("Missing or empty input: " + str(path))
        with path.open("rb") as stream:
            if stream.read(4) != b"RSC7":
                raise ValueError("Expected GTA V Legacy RSC7 file: " + str(path))

    sys.path.extend((os.environ["PEDTOPROP_SOLLUMZ_PARENT"], os.environ["PEDTOPROP_SOLLUMZ_LIB"]))
    addon_entry = bpy.context.preferences.addons.new()
    addon_entry.module = "sollumz"
    import sollumz
    if tuple(sollumz.bl_info["version"]) < (2, 9, 0):
        raise RuntimeError("Sollumz 2.9.0 or newer is required")
    sollumz.register()

    from sollumz.sollumz_properties import SollumType
    from sollumz.tools.drawablehelper import convert_objs_to_single_drawable
    from sollumz.tools.boundhelper import create_bound_box
    from sollumz.tools.blenderhelper import create_empty_object
    from sollumz.ybn.collision_materials import create_collision_material_from_index
    from sollumz.ydr.ydrexport import export_ydr
    from sollumz.ydr.operators.materials import MaterialConverter
    from sollumz.iecontext import ExportContext, ExportSettings, export_context_scope
    from szio.gta5 import AssetTarget, AssetFormat, AssetVersion, try_load_asset

    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    result = bpy.ops.sollumz.import_assets(
        directory=str(source), files=[{"name": name + ".ytd"}, {"name": name + ".ydd"}],
        use_custom_settings=True, dwd_import_external_skeleton="FROM_DIR",
        split_by_group=False, textures_mode="PACK")
    if "FINISHED" not in result:
        raise RuntimeError("Sollumz import failed")

    imported = [o for o in bpy.data.objects if o.type == "MESH" and o.sollum_type == SollumType.DRAWABLE_MODEL]
    packed_images = [image for image in bpy.data.images if image.packed_file]
    print("PEDTOPROP_IMPORT", "meshes", len(imported), "packed_images", len(packed_images), flush=True)
    if not imported or not packed_images:
        raise RuntimeError("Ped geometry or textures missing")

    graph = bpy.context.evaluated_depsgraph_get()
    baked = []
    for original in imported:
        evaluated = original.evaluated_get(graph)
        mesh = bpy.data.meshes.new_from_object(evaluated, preserve_all_data_layers=True, depsgraph=graph)
        mesh.transform(Matrix.Scale(scale, 4))
        obj = bpy.data.objects.new(original.name + "_baked", mesh)
        bpy.context.collection.objects.link(obj)
        obj.matrix_world = original.matrix_world.copy()
        for slot in list(obj.material_slots):
            if slot.material is not None:
                MaterialConverter(obj, slot.material).convert("normal_spec.sps")
                for node in slot.material.node_tree.nodes:
                    if node.type == "TEX_IMAGE" and node.image is not None:
                        node.texture_properties.embedded = True
                        node.image.sz_is_hd = False
        baked.append(obj)

    drawable = convert_objs_to_single_drawable(baked)
    drawable.name = prop_name
    bounds = [v.co for obj in baked for v in obj.data.vertices]
    if not bounds:
        raise RuntimeError("Empty output mesh")
    low = Vector((min(v.x for v in bounds), min(v.y for v in bounds), min(v.z for v in bounds)))
    high = Vector((max(v.x for v in bounds), max(v.y for v in bounds), max(v.z for v in bounds)))
    size = high - low
    if any(value < 0.001 or value > 800 for value in size):
        raise RuntimeError("Implausible character size: " + str(tuple(size)))
    composite = create_empty_object(SollumType.BOUND_COMPOSITE)
    composite.name = prop_name + ".bounds"
    composite.parent = drawable
    collision = create_bound_box()
    collision.name = prop_name + ".col"
    collision.parent = composite
    collision.location = (low + high) / 2
    collision.scale = (min(size.x, 1.2 * scale), min(size.y, 1.2 * scale), size.z * 0.9)
    collision.data.materials.append(create_collision_material_from_index(0))

    output.mkdir(parents=True, exist_ok=True)
    settings = ExportSettings(targets=(AssetTarget(AssetFormat.NATIVE, AssetVersion.GEN8),))
    with export_context_scope(ExportContext(prop_name, settings)):
        drawable_bundle = export_ydr(drawable)
        if not drawable_bundle:
            raise RuntimeError("Sollumz could not export drawable")
        drawable_bundle.save(output, settings.targets)
    file = output / (prop_name + ".ydr")
    if not file.is_file() or file.stat().st_size < 100:
        raise RuntimeError("Drawable was not exported")
    ydr = try_load_asset(file)
    if not ydr or not ydr.models or ydr.bounds is None or (ydr.skeleton is not None and ydr.skeleton.bones) or not ydr.shader_group or not ydr.shader_group.embedded_textures:
        raise RuntimeError("Output geometry, collision or embedded textures are incomplete")
    for obj in bpy.data.objects:
        if obj not in baked and obj.type in {"MESH", "ARMATURE"}:
            obj.hide_render = True
    center = (low + high) / 2
    bpy.ops.object.camera_add(location=center + Vector((6, -8, 4)) * max(scale, 0.5))
    camera = bpy.context.object
    camera.rotation_euler = (center - camera.location).to_track_quat("-Z", "Y").to_euler()
    bpy.context.scene.camera = camera
    bpy.ops.object.light_add(type="AREA", location=center + Vector((2, -4, 7)) * max(scale, 0.5))
    bpy.context.object.data.energy = 1200 * max(scale, 0.5) ** 2
    bpy.context.object.data.shape = "DISK"
    bpy.context.object.data.size = 5 * max(scale, 0.5)
    bpy.context.scene.render.engine = "CYCLES"
    bpy.context.scene.cycles.samples = 16
    bpy.context.scene.render.resolution_x = 700
    bpy.context.scene.render.resolution_y = 700
    bpy.context.scene.render.resolution_percentage = 100
    bpy.context.scene.render.image_settings.file_format = "PNG"
    bpy.context.scene.render.filepath = str(output / (prop_name + "_preview.png"))
    bpy.ops.render.render(write_still=True)
    print("PEDTOPROP_OK", prop_name, len(baked), len(ydr.shader_group.embedded_textures), tuple(round(v, 3) for v in size), "scale", scale)


try:
    main()
except Exception:
    traceback.print_exc()
    sys.exit(1)
