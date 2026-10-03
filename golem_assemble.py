"""Tier-1 adapter: turn a multi-part model (e.g. Hyper3D BANG output) into a GOLEM split.

The model chooses, the geometry computes: the caller names which parts stay fixed and
which group moves, on which side its hinge is and which way it opens (discrete choices,
made by a person or a vision model); this script computes the pivot and axis and writes
the same files as golem_cutter.py, so the Unity side treats both tiers alike.

  python golem_assemble.py assets/raw/vault_door_bang.glb --root root.5 \
      --move door=root.12,root.1,root.2:left:front:100

--move NAME=NODES:HINGE:OPENS[:LIMIT] (repeatable): a hinged part
  NODES   comma-separated node names that move together
  HINGE   side of the group the hinge is on: left, right, back, front, top, bottom
  OPENS   direction the free edge swings toward: front, back, up, down, left, right
  LIMIT   opening limit in degrees (default 100)
The pivot sits on the group's box at its extreme toward HINGE and toward OPENS (so the part
swings clear of what it closes against), centered along the hinge line.

--slide NAME=NODES:DIRECTION[:TRAVEL] (repeatable): a sliding part, e.g. a drawer
  DIRECTION  which way it pulls out: front, back, left, right, up, down
  TRAVEL     how far, in scene units (default: half the fixed body's depth along DIRECTION,
             since a drawer front is thin but the drawer runs back into the body)

--carve NEW=SOURCE:X0,X1,Y0,Y1,Z0,Z1 (repeatable, applied first): a new part made of SOURCE's
  loose pieces that lie wholly inside this box (glTF frame: +Y up, front +Z, scene units), cut
  out of SOURCE. For a part the multi-part model left fused into another: BANG drew four drawer
  fronts on the filing cabinet but split out only three; the fourth is loose pieces of the carcass.
  NEW can then be used in --root, --move or --slide like any other part.

Writes assets/split/<name>/<name>_split.glb and <name>_joints.json. Runs in headless Blender.
"""

import json
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
BLENDER = os.environ.get("GOLEM_BLENDER", r"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe")
# Directions in Blender's frame (Z up; glTF's front +Z imports as Blender -Y).
SIDES = {
    "left": (-1, 0, 0), "right": (1, 0, 0), "back": (0, 1, 0), "front": (0, -1, 0),
    "top": (0, 0, 1), "up": (0, 0, 1), "bottom": (0, 0, -1), "down": (0, 0, -1),
}


def parse_args(argv):
    import argparse

    import numpy as np

    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("input", type=Path)
    parser.add_argument("--root", required=True, help="comma-separated node names that stay fixed")
    parser.add_argument("--move", action="append", default=[], help="NAME=NODES:HINGE:OPENS[:LIMIT]")
    parser.add_argument("--slide", action="append", default=[], help="NAME=NODES:DIRECTION[:TRAVEL]")
    parser.add_argument("--carve", action="append", default=[], help="NEW=SOURCE:X0,X1,Y0,Y1,Z0,Z1")
    parser.add_argument("--name", help="output name (default: input file name)")
    parser.add_argument("--out", type=Path, default=ROOT / "assets" / "split")
    args = parser.parse_args(argv)
    groups = []
    for spec in args.move:
        name, rest = spec.split("=", 1)
        fields = rest.split(":")
        if len(fields) not in (3, 4) or fields[1] not in SIDES or fields[2] not in SIDES:
            parser.error(f"bad --move {spec!r}; expected NAME=NODES:HINGE:OPENS[:LIMIT]")
        groups.append({"kind": "hinge", "name": name, "nodes": fields[0].split(","), "hinge": fields[1],
                       "opens": fields[2], "limit": float(fields[3]) if len(fields) == 4 else 100.0})
    for spec in args.slide:
        name, rest = spec.split("=", 1)
        fields = rest.split(":")
        if len(fields) not in (2, 3) or fields[1] not in SIDES:
            parser.error(f"bad --slide {spec!r}; expected NAME=NODES:DIRECTION[:TRAVEL]")
        groups.append({"kind": "slide", "name": name, "nodes": fields[0].split(","), "direction": fields[1],
                       "travel": float(fields[2]) if len(fields) == 3 else None})
    if not groups:
        parser.error("give at least one --move or --slide")
    args.groups = groups
    carves = []
    for spec in args.carve:
        try:
            name, rest = spec.split("=", 1)
            source, box = rest.split(":", 1)
            bounds = [float(v) for v in box.split(",")]
        except ValueError:
            bounds = []
        if len(bounds) != 6 or bounds[0] >= bounds[1] or bounds[2] >= bounds[3] or bounds[4] >= bounds[5]:
            parser.error(f"bad --carve {spec!r}; expected NEW=SOURCE:X0,X1,Y0,Y1,Z0,Z1 with each min below its max")
        carves.append({"name": name, "source": source, "box": np.array(bounds).reshape(3, 2)})
    args.carves = carves
    return args


def relaunch_in_blender() -> int:
    if not Path(BLENDER).exists():
        sys.exit(f"Blender not found at {BLENDER}. Set GOLEM_BLENDER to blender.exe.")
    args = parse_args(sys.argv[1:])
    if not args.input.exists():
        sys.exit(f"No such file: {args.input}")
    command = [BLENDER, "-b", "--factory-startup", "--python-exit-code", "1", "--python", __file__, "--", *sys.argv[1:]]
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace")
    lines = (result.stdout + result.stderr).splitlines()
    print("\n".join(lines if result.returncode else [line for line in lines if line.startswith("[golem]")]))
    return result.returncode


def run_in_blender(args) -> None:
    import bmesh
    import bpy
    import numpy as np
    from mathutils import Matrix, Vector

    log = lambda message: print(f"[golem] {message}", flush=True)
    name = args.name or args.input.stem
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(args.input.resolve()))
    meshes = {o.name: o for o in bpy.context.scene.objects if o.type == "MESH"}
    log(f"parts in {args.input.name}: {sorted(meshes)}")

    # Bake node transforms into the meshes so every part shares the scene frame.
    for obj in meshes.values():
        world = obj.matrix_world.copy()
        obj.parent = None
        obj.data = obj.data.copy()
        obj.data.transform(world)
        obj.matrix_world = Matrix.Identity(4)
    for obj in [o for o in bpy.context.scene.objects if o.type != "MESH"]:
        bpy.data.objects.remove(obj)

    def delete_vertices(obj, indices):
        bm = bmesh.new()
        bm.from_mesh(obj.data)
        bm.verts.ensure_lookup_table()
        bmesh.ops.delete(bm, geom=[bm.verts[i] for i in indices], context="VERTS")
        bm.to_mesh(obj.data)
        bm.free()

    for carve in args.carves:
        source = meshes.get(carve["source"])
        if source is None:
            sys.exit(f"[golem] --carve: unknown part {carve['source']!r}; available: {sorted(meshes)}")
        mesh = source.data
        co = np.empty(len(mesh.vertices) * 3)
        mesh.vertices.foreach_get("co", co)
        co = co.reshape(-1, 3)
        gltf = np.stack([co[:, 0], co[:, 2], -co[:, 1]], axis=1)  # Blender Z up -> glTF Y up
        box = carve["box"]
        inside = np.all((gltf >= box[:, 0]) & (gltf <= box[:, 1]), axis=1)
        # Loose pieces: vertices joined by edges (union-find). Take whole pieces only, so nothing is torn.
        edges = np.empty(len(mesh.edges) * 2, dtype=np.int64)
        mesh.edges.foreach_get("vertices", edges)
        parent = list(range(len(co)))

        def find(i):
            while parent[i] != i:
                parent[i] = parent[parent[i]]
                i = parent[i]
            return i

        for a, b in edges.reshape(-1, 2):
            ra, rb = find(a), find(b)
            if ra != rb:
                parent[ra] = rb
        piece = np.array([find(i) for i in range(len(co))])
        outside_pieces = set(piece[~inside])
        take = np.array([inside[i] and piece[i] not in outside_pieces for i in range(len(co))])
        if not take.any():
            sys.exit(f"[golem] --carve {carve['name']}: no loose piece of {carve['source']} lies wholly inside the box")
        carved = source.copy()
        carved.data = source.data.copy()
        carved.name = carved.data.name = carve["name"]
        for collection in source.users_collection:
            collection.objects.link(carved)
        delete_vertices(carved, np.flatnonzero(~take))
        delete_vertices(source, np.flatnonzero(take))
        meshes[carve["name"]] = carved
        log(f"{carve['name']}: carved {int(take.sum())} of {len(co)} vertices "
            f"({len(set(piece[take]))} loose pieces) out of {carve['source']}")

    def join(node_names, new_name):
        missing = [n for n in node_names if n not in meshes]
        if missing:
            sys.exit(f"[golem] unknown parts {missing}; available: {sorted(meshes)}")
        objects = [meshes[n] for n in node_names]
        if len(objects) > 1:
            with bpy.context.temp_override(active_object=objects[0], selected_editable_objects=objects, selected_objects=objects):
                bpy.ops.object.join()
        objects[0].name = objects[0].data.name = new_name
        return objects[0]

    def vertices(obj):
        co = np.empty(len(obj.data.vertices) * 3)
        obj.data.vertices.foreach_get("co", co)
        return co.reshape(-1, 3)

    def set_origin(obj, point):
        obj.data.transform(Matrix.Translation(Vector(-np.asarray(point))))
        obj.location = Vector(point)

    def to_gltf(v):
        """Blender (x, y, z), Z up -> glTF (x, z, -y), Y up (a proper rotation)."""
        return [round(float(v[0]), 6), round(float(v[2]), 6), round(float(-v[1]) + 0.0, 6)]

    body = join(args.root.split(","), "body")
    co = vertices(body)
    body_size = co.max(axis=0) - co.min(axis=0)
    base = np.array([(co[:, 0].min() + co[:, 0].max()) / 2, (co[:, 1].min() + co[:, 1].max()) / 2, co[:, 2].min()])
    set_origin(body, base)
    bpy.context.view_layer.update()

    joints = []
    for group in args.groups:
        part = join(group["nodes"], group["name"])
        co = vertices(part)
        if group["kind"] == "slide":
            direction = np.array(SIDES[group["direction"]], float)
            lo, hi = co.min(axis=0), co.max(axis=0)
            travel = group["travel"] if group["travel"] is not None else 0.5 * float(body_size @ np.abs(direction))
            set_origin(part, (lo + hi) / 2)  # a slide has no pivot; anchor it at its middle
            bpy.context.view_layer.update()
            part.parent = body
            part.matrix_parent_inverse = body.matrix_world.inverted()
            joints.append({
                "type": "prismatic",
                "parent": "body",
                "child": group["name"],
                "pivot": to_gltf((lo + hi) / 2),
                "axis": to_gltf(direction),
                "limits_deg": [0.0, round(travel, 6)],  # a slide's limits are in scene units, not degrees
                "rest_deg": 0.0,
                "slides_toward": group["direction"],
                "outward": to_gltf(direction),  # positive travel moves the part this way
            })
            log(f"{group['name']}: nodes {group['nodes']}, slides {group['direction']} up to {travel:.3f}")
            continue
        hinge, opens = np.array(SIDES[group["hinge"]], float), np.array(SIDES[group["opens"]], float)
        if abs(hinge @ opens) > 0.5:
            sys.exit(f"[golem] {group['name']}: hinge side and opening direction must be perpendicular")
        line = np.cross(hinge, opens)  # direction of the hinge line
        lo, hi = co.min(axis=0), co.max(axis=0)
        # Extreme toward the hinge side and toward the opening side; centered along the hinge line.
        pivot = np.where(hinge > 0, hi, lo) * np.abs(hinge) + np.where(opens > 0, hi, lo) * np.abs(opens) \
            + (lo + hi) / 2 * np.abs(line)
        axis = np.cross(-hinge, opens)  # positive rotation swings the free edge toward OPENS
        set_origin(part, pivot)
        bpy.context.view_layer.update()
        part.parent = body
        part.matrix_parent_inverse = body.matrix_world.inverted()
        joints.append({
            "type": "revolute",
            "parent": "body",
            "child": group["name"],
            "pivot": to_gltf(pivot),
            "axis": to_gltf(axis),
            "limits_deg": [0.0, group["limit"]],
            "rest_deg": 0.0,
            "hinge_side": group["hinge"],
            "opens_toward": group["opens"],
            "outward": to_gltf(hinge),
            "hinge_length": round(float((hi - lo) @ np.abs(line)), 6),
        })
        log(f"{group['name']}: nodes {group['nodes']}, hinge {group['hinge']}, opens {group['opens']}, "
            f"pivot {joints[-1]['pivot']} axis {joints[-1]['axis']} (glTF)")

    used = set(args.root.split(",")) | {n for g in args.groups for n in g["nodes"]}
    unused = sorted(set(meshes) - used)
    if unused:
        log(f"warning: parts not assigned (left out of the export): {unused}")

    out_dir = (args.out / name).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)
    glb_path, json_path = out_dir / f"{name}_split.glb", out_dir / f"{name}_joints.json"
    keep = {body} | {bpy.data.objects[g["name"]] for g in args.groups}
    for obj in bpy.context.scene.objects:
        obj.select_set(obj in keep)
    bpy.ops.export_scene.gltf(filepath=str(glb_path), export_format="GLB", use_selection=True, export_yup=True)
    json_path.write_text(json.dumps({
        "asset": name,
        "input": str(args.input),
        "splitter": "golem_assemble (multi-part model, e.g. Hyper3D BANG)",
        "frame": "glTF (right-handed, +Y up, front +Z)",
        "parts": [{"node": "body", "role": "root", "from": args.root.split(",")}]
        + [{"node": g["name"], "role": "moving", "from": g["nodes"]} for g in args.groups],
        "joints": joints,
        "unassigned_parts": unused,
    }, indent=2), encoding="utf-8")
    log(f"wrote {glb_path}")
    log(f"wrote {json_path}")


if __name__ == "__main__":
    try:
        import bpy  # noqa: F401
    except ImportError:
        sys.exit(relaunch_in_blender())
    run_in_blender(parse_args(sys.argv[sys.argv.index("--") + 1 :]))
