import os
import sys
from pathlib import Path

import bpy

sys.path.extend((os.environ["PEDTOPROP_SOLLUMZ_PARENT"], os.environ["PEDTOPROP_SOLLUMZ_LIB"]))
entry = bpy.context.preferences.addons.new()
entry.module = "sollumz"
import sollumz
sollumz.register()

directory = Path(sys.argv[sys.argv.index("--") + 1])
name = sys.argv[sys.argv.index("--") + 2]
bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)
result = bpy.ops.sollumz.import_assets(
    directory=str(directory),
    files=[{"name": name + ".ydr"}],
    use_custom_settings=True, split_by_group=False, textures_mode="PACK")
meshes = [o for o in bpy.data.objects if o.type == "MESH" and o.name != name + ".col"]
images = [i for i in bpy.data.images if i.packed_file and i.name not in {"Render Result", "Viewer Node"}]
print("VERIFY_RESULT", result, "meshes", len(meshes), "images", len(images),
      "vertices", sum(len(o.data.vertices) for o in meshes))
if "FINISHED" not in result or not meshes or not images or not any(o.data.vertices for o in meshes):
    sys.exit(1)
