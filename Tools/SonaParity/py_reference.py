"""Generate reference outputs from the ORIGINAL Python SONA code for C# parity tests.

For each case: returns (unity frame), plan, and the rendered stereo buffer at 44.1 kHz.
"""
import json, math, os, sys
import numpy as np

# The original Python prototype (Echolocation Assistant (DareDevil)/Code). Override with SONA_PY_CODE.
HERE = os.path.dirname(os.path.abspath(__file__))
PY_CODE = os.environ.get("SONA_PY_CODE", os.path.normpath(os.path.join(HERE, "..", "..", "..", "..", "Code")))
sys.path.insert(0, PY_CODE)
from scene import make_preset, make_custom_room
from frontend import surface_emission
from encoder import encode, EncodingParams
from spatializer import GranularBinauralSpatializer, surface_to_freq
from returns import ListenerPose

CASES = [
    dict(name="open_demo", preset="open_demo"),
    dict(name="enclosed_demo", preset="enclosed_demo"),
    dict(name="no_walls_demo", preset="no_walls_demo"),
    dict(name="open_large", preset="open_large"),
    dict(name="custom_8x12x4_closed", custom=(8.0, 12.0, 4.0, "closed")),
    dict(name="open_demo_moved", preset="open_demo", listener=(1.2, 1.0, 1.5, 37.0),
         objects={"box-1": dict(x=-1.0, y=3.2, yaw=30.0), "panel-1": dict(x=1.4, y=4.1, yaw=75.0)},
         speed=1.5),
    dict(name="enclosed_compact_back", preset="enclosed_compact", listener=(0.3, 2.0, 1.6, -120.0), speed=0.5),
]


def build(case):
    if "custom" in case:
        w, d, h, mode = case["custom"]
        sc = make_custom_room(w, d, h, mode)
    else:
        sc = make_preset(case["preset"])
    if "listener" in case:
        x, y, z, yaw = case["listener"]
        sc.listener.x, sc.listener.y, sc.listener.z, sc.listener.yaw = x, y, z, yaw % 360.0
    for oid, mods in case.get("objects", {}).items():
        ent = sc.get_object(oid)
        ent.x, ent.y, ent.yaw = mods["x"], mods["y"], mods["yaw"] % 360.0
        sc.clamp_entity(ent)
    return sc


def to_unity(p):
    return [float(p[0]), float(p[2]), float(p[1])]


def main(out_dir):
    meta = []
    for case in CASES:
        sc = build(case)
        speed = case.get("speed", 1.0)
        sweep = 1.2 / speed
        far = max(float(sc.farthest_corner_distance()), 1e-6)
        L = sc.listener
        pose = ListenerPose(position=np.array([L.x, L.y, L.z], dtype=float),
                            heading=math.radians(L.yaw), forward=L.forward)
        rets = surface_emission(sc.to_surface_quads(), listener_pose=pose,
                                samples_per_meter=5.0, max_range=far + 1e-9)
        params = EncodingParams(near=0.4, far=far, sweep_duration=sweep, foveation_exponent=0.5)
        plan = encode(rets, params)
        spat = GranularBinauralSpatializer(sample_rate=44100)
        stereo = spat.render(plan, params.sweep_duration)

        r_arr = np.array([[*to_unity(r.world_position), r.azimuth, r.elevation, r.range, r.propagation_range]
                          for r in rets], dtype=np.float64)
        p_arr = np.array([[e.onset, *to_unity(e.world_position), e.azimuth, e.elevation, e.amplitude,
                           surface_to_freq(e.elevation, e.material)] for e in plan], dtype=np.float64)
        np.save(f"{out_dir}/{case['name']}_returns.npy", r_arr)
        np.save(f"{out_dir}/{case['name']}_plan.npy", p_arr)
        np.save(f"{out_dir}/{case['name']}_audio.npy", stereo.astype(np.float32))
        meta.append(dict(name=case["name"], returns=len(rets), plan=len(plan), far=far,
                         frames=int(stereo.shape[0]), listener=[L.x, L.y, L.z, L.yaw], sweep=sweep))
        print(f"{case['name']:24s} returns={len(rets):5d} plan={len(plan):5d} far={far:.4f} frames={stereo.shape[0]}")
    with open(f"{out_dir}/meta.json", "w") as f:
        json.dump(meta, f, indent=1)


if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else "ref"
    os.makedirs(out, exist_ok=True)
    main(out)
