"""Compare C# SONA core outputs against the Python reference outputs."""
import json, struct, sys
import numpy as np

REF, CS = sys.argv[1], sys.argv[2]


def read_doubles(path):
    with open(path, "rb") as f:
        n, m = struct.unpack("<ii", f.read(8))
        return np.frombuffer(f.read(), dtype="<f8").reshape(n, m) if n else np.zeros((0, m))


def read_floats(path):
    with open(path, "rb") as f:
        (n,) = struct.unpack("<i", f.read(4))
        return np.frombuffer(f.read(), dtype="<f4").reshape(-1, 2)


def angle_diff(a, b):
    return np.abs((a - b + np.pi) % (2 * np.pi) - np.pi)


meta = json.load(open(f"{REF}/meta.json"))
ok = True
for m in meta:
    name = m["name"]
    pr = np.load(f"{REF}/{name}_returns.npy")
    cr = read_doubles(f"{CS}/{name}_returns.bin")
    pp = np.load(f"{REF}/{name}_plan.npy")
    cp = read_doubles(f"{CS}/{name}_plan.bin")
    pa = np.load(f"{REF}/{name}_audio.npy")
    ca = read_floats(f"{CS}/{name}_audio.bin")
    mf = read_floats(f"{CS}/{name}_mixer_frozen.bin")
    ml = read_floats(f"{CS}/{name}_mixer_live.bin")

    res = {}
    res["returns_n"] = (len(pr), len(cr))
    if len(pr) == len(cr):
        res["ret_pos"] = np.max(np.abs(pr[:, :3] - cr[:, :3]))
        res["ret_az"] = np.max(angle_diff(pr[:, 3], cr[:, 3]))
        res["ret_el"] = np.max(np.abs(pr[:, 4] - cr[:, 4]))
        res["ret_rng"] = np.max(np.abs(pr[:, 5:7] - cr[:, 5:7]))
    res["plan_n"] = (len(pp), len(cp))
    if len(pp) == len(cp):
        # Equal-onset ties may be ordered differently (1-ulp onsets), so compare as multisets.
        def canon(a):
            key = np.round(a[:, [0, 1, 2, 3, 7]], 9)
            return a[np.lexsort((key[:, 4], key[:, 3], key[:, 2], key[:, 1], key[:, 0]))]
        a, b = canon(pp), canon(cp)
        res["plan_onset"] = np.max(np.abs(a[:, 0] - b[:, 0]))
        res["plan_pos"] = np.max(np.abs(a[:, 1:4] - b[:, 1:4]))
        res["plan_amp"] = np.max(np.abs(a[:, 6] - b[:, 6]))
        res["plan_freq"] = np.max(np.abs(a[:, 7] - b[:, 7]))
    res["audio_shape"] = (pa.shape, ca.shape)
    if pa.shape == ca.shape:
        res["audio_offline_maxdiff"] = float(np.max(np.abs(pa - ca)))
        res["mixer_frozen_vs_python"] = float(np.max(np.abs(pa - mf)))
        res["mixer_live_vs_python"] = float(np.max(np.abs(pa - ml)))
        res["audio_peak"] = float(np.max(np.abs(pa)))
        rms = np.sqrt(np.mean(pa.astype(np.float64) ** 2))
        res["live_rel_rms_err"] = float(np.sqrt(np.mean((pa - ml).astype(np.float64) ** 2)) / max(rms, 1e-12))
    good = (res["returns_n"][0] == res["returns_n"][1] and res["plan_n"][0] == res["plan_n"][1]
            and res.get("ret_pos", 1) < 1e-9 and res.get("ret_az", 1) < 1e-9 and res.get("ret_el", 1) < 1e-9
            and res.get("ret_rng", 1) < 1e-9 and res.get("plan_onset", 1) < 1e-9
            and res.get("plan_amp", 1) < 1e-9 and res.get("plan_freq", 1) < 1e-6 and res.get("plan_pos", 1) < 1e-9
            and res.get("audio_offline_maxdiff", 1) < 1e-6 and res.get("mixer_frozen_vs_python", 1) < 1e-5)
    ok &= good
    print(("PASS " if good else "FAIL ") + name)
    for k, v in res.items():
        print(f"    {k:24s} {v}")
print("ALL PASS" if ok else "SOME FAILED")
sys.exit(0 if ok else 1)
