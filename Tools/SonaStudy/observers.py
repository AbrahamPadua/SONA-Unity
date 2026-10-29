"""
Observer simulations and parameter recovery for the SONA Proposal 1.2 oddity study (step 5 of
AudioSweep/SONA Proposal 1.2 implementation plan.md).

Model: each of the three intervals gives the listener a reading of each cue in log-distance units,
delay with Gaussian noise sigma_d and DRR with sigma_r. The odd interval is moved by delta * (k_d, k_r),
as in the app's conditions. The listener picks the interval that stands out most from the other two,
the triangle-test rule (Frijters 1979; Ennis 1993). A lapse rate mixes in random answers. Three observers:

    selection  uses only the more reliable cue
    joint      uses both cues as one 2-D reading
    fusion     uses one reliability-weighted estimate (Ernst & Banks 2002), so opposite changes cancel

The recovery runs simulated participants through the app's Single cues and Fusion blocks (Negen's
staircases with the app's caps), fits all three observers to every trial, and reports how well the cue
noise comes back and how often the true observer wins.

Run with the prototype's environment (numpy only):  python observers.py
"""
import math
import sys

import numpy as np

LAPSE = 0.02
START, DOWN, UP_STEPS = 0.2, 0.9, 3   # Negen et al. (2023): x0.9 after a right answer, /0.9^3 after a wrong one
# Condition: (k_d, k_r, delay cue present, DRR cue present, cap for +, cap for -). The caps are
# StudyTrials.MaxDelta for the app's default StudyParams, as printed by the parity audit.
CONDITIONS = {
    "delay-only": (1, 0, True, False, 0.741, 1.211),
    "drr-only": (0, 1, False, True, 1.552, 1.552),
    "congruent": (1, 1, True, True, 0.741, 1.211),
    "opposite": (1, -1, True, True, 0.741, 1.211),
}
BLOCKS = ["delay-only", "drr-only", "congruent", "opposite"]   # Single cues + Fusion, each with both signs
MODELS = ["selection", "joint", "fusion"]

_phi = np.vectorize(lambda x: 0.5 * (1.0 + math.erf(x / math.sqrt(2.0))))


def triangle_pc(delta):
    """P(correct) with one cue, odd mean shifted by delta noise sd (Frijters 1979; Ennis 1993):
    Pc = 2 * integral_0^inf [Phi(-x sqrt3 + delta sqrt(2/3)) + Phi(-x sqrt3 - delta sqrt(2/3))] phi(x) dx."""
    x = np.linspace(0.0, 10.0, 4001)[:, None]
    d = np.abs(np.atleast_1d(delta))[None, :]
    pdf = np.exp(-0.5 * x * x) / math.sqrt(2.0 * math.pi)
    f = (_phi(-x * math.sqrt(3) + d * math.sqrt(2 / 3)) + _phi(-x * math.sqrt(3) - d * math.sqrt(2 / 3))) * pdf
    return 2.0 * np.trapezoid(f, x[:, 0], axis=0)


def simulate_pc(shift, n, rng):
    """Monte Carlo of the most-deviant rule with unit, independent noise per dimension; interval 0 is odd."""
    obs = rng.standard_normal((n, 3, len(shift)))
    obs[:, 0, :] += shift
    others = (obs.sum(axis=1, keepdims=True) - obs) / 2.0
    return float(np.mean(((obs - others) ** 2).sum(axis=-1).argmax(axis=1) == 0))


# Psychometric tables in noise-sd units: 1-D from the closed form, 2-D (joint access) by Monte Carlo. In
# whitened 2-D space the rule is rotation invariant, so P depends only on the length of the shift.
GRID = np.linspace(0.0, 8.0, 81)
P1 = triangle_pc(GRID)
P2 = np.array([simulate_pc(np.array([g, 0.0]), 200_000, np.random.default_rng(100 + i)) for i, g in enumerate(GRID)])
P2 = np.maximum.accumulate(P2)   # remove Monte Carlo wiggle so the curve is monotone


def sensitivity(model, cond, sd, sr):
    """Shift per unit delta, in noise-sd units, and which table it uses. sd and sr broadcast."""
    kd, kr, has_d, has_r = CONDITIONS[cond][:4]
    sd, sr = np.asarray(sd, float), np.asarray(sr, float)
    if not has_r:
        return abs(kd) / sd, P1
    if not has_d:
        return abs(kr) / sr, P1
    if model == "selection":
        return np.where(sd <= sr, abs(kd) / sd, abs(kr) / sr), P1
    if model == "joint":
        return np.sqrt((kd / sd) ** 2 + (kr / sr) ** 2), P2
    wd = sr ** 2 / (sd ** 2 + sr ** 2)
    sz = np.sqrt(wd ** 2 * sd ** 2 + (1 - wd) ** 2 * sr ** 2)
    return np.abs(wd * kd + (1 - wd) * kr) / sz, P1


def pc(model, cond, delta, sd, sr):
    g, table = sensitivity(model, cond, sd, sr)
    return (1 - LAPSE) * np.interp(np.asarray(delta) * g, GRID, table) + LAPSE / 3


def threshold(model, cond, sd, sr, target=0.75):
    """Difference (ln units) where P(correct) reaches the staircase's 75% point; inf if never."""
    g, table = sensitivity(model, cond, sd, sr)
    p = (1 - LAPSE) * table + LAPSE / 3
    if g == 0 or p[-1] < target:
        return math.inf
    return float(np.interp(target, p, GRID) / g)


def run_participant(model, sd, sr, trials, rng):
    """One simulated listener through Single cues + Fusion: 8 staircases interleaved, as in the app."""
    tracks = [(c, s) for c in BLOCKS for s in (1, -1)]
    value = {t: START for t in tracks}
    left = {t: trials for t in tracks}
    rows = []
    while any(left.values()):
        t = [k for k in tracks if left[k]][rng.integers(sum(1 for k in tracks if left[k]))]
        cond, sign = t
        cap = CONDITIONS[cond][4 if sign > 0 else 5]
        d = value[t]
        ok = rng.random() < pc(model, cond, d, sd, sr)
        rows.append((cond, d, ok))
        value[t] = d * DOWN if ok else min(d / DOWN ** UP_STEPS, cap)
        left[t] -= 1
    return rows


FIT_GRID = np.exp(np.linspace(math.log(0.03), math.log(3.0), 48))
SD_G, SR_G = np.meshgrid(FIT_GRID, FIT_GRID, indexing="ij")


def fit(rows):
    """Maximum likelihood over (sigma_d, sigma_r) on a grid, for each observer. Same two parameters each, so the
    largest likelihood picks the observer."""
    conds = np.array([r[0] for r in rows])
    delta = np.array([r[1] for r in rows])
    ok = np.array([r[2] for r in rows])
    out = {}
    for model in MODELS:
        ll = np.zeros(SD_G.shape)
        for cond in BLOCKS:
            m = conds == cond
            g, table = sensitivity(model, cond, SD_G, SR_G)
            p = (1 - LAPSE) * np.interp(g[..., None] * delta[m], GRID, table) + LAPSE / 3
            ll += np.where(ok[m], np.log(p), np.log1p(-p)).sum(axis=-1)
        i = np.unravel_index(np.argmax(ll), ll.shape)
        out[model] = (ll[i], SD_G[i], SR_G[i])
    return out


def checks():
    rng = np.random.default_rng(7)
    ok = True

    def check(cond, what):
        nonlocal ok
        ok &= bool(cond)
        print(("  ok   " if cond else "  FAIL ") + what)

    check(abs(triangle_pc(0.0)[0] - 1 / 3) < 1e-6, "closed-form triangle test gives 1/3 with no difference")
    worst = 0.0
    for d in (0.5, 1.0, 2.0, 3.0):
        mc = simulate_pc(np.array([d]), 400_000, rng)
        se = math.sqrt(mc * (1 - mc) / 400_000)
        worst = max(worst, abs(mc - triangle_pc(d)[0]) / se)
    check(worst < 4, f"Monte Carlo of the most-deviant rule matches the closed form at d' = 0.5, 1, 2, 3 (worst {worst:.1f} SE)")
    check(abs(P2[0] - 1 / 3) < 0.005 and P2[-1] > 0.99, "2-D table starts at chance and reaches 1")
    check(abs(threshold("joint", "opposite", 0.2, 0.3) - threshold("joint", "congruent", 0.2, 0.3)) < 1e-12,
          "joint access: opposite and congruent changes are equally easy")
    check(threshold("fusion", "opposite", 0.25, 0.25) == math.inf, "fusion with equal weights: opposite changes cancel")
    sd, sr = 0.2, 0.3
    check(threshold("fusion", "congruent", sd, sr) < min(threshold("selection", "congruent", sd, sr), threshold("joint", "congruent", sd, sr)),
          "fusion: congruent changes are easiest (combined noise is lowest)")
    return ok


def report():
    print("\nPredicted 75% thresholds (ln distance; the staircase's target), lapse 2%:")
    scenarios = {"matched": (0.25, 0.25), "unmatched": (0.15, 0.35)}
    print(f"  {'scenario':10} {'observer':10} " + " ".join(f"{c:>11}" for c in BLOCKS) + "   opp/cong")
    for name, (sd, sr) in scenarios.items():
        for model in MODELS:
            t = [threshold(model, c, sd, sr) for c in BLOCKS]
            ratio = t[3] / t[2]
            cells = " ".join(f"{x:11.3f}" if x < math.inf else f"{'never':>11}" for x in t)
            print(f"  {name:10} {model:10} {cells}   {ratio:8.2f}" if ratio < math.inf else f"  {name:10} {model:10} {cells}   {'inf':>8}")
        print(f"  {'':10} (sigma_d = {sd}, sigma_r = {sr}; caps: delay +0.741/-1.211, DRR +-1.552, both +0.741/-1.211)")

    print("\nRecovery: 100 simulated listeners per cell, Single cues + Fusion, all three observers fitted to every trial.")
    print("  'picked' = how often the true observer has the highest likelihood; sigma error = median |ln(fit/true)| as %.")
    print(f"  {'scenario':10} {'true':10} {'trials/stair':>12} {'picked':>7}   {'chosen sel/joint/fus':>20} {'sigma_d err':>11} {'sigma_r err':>11}")
    rng = np.random.default_rng(2026)
    for name, (sd, sr) in scenarios.items():
        for true in MODELS:
            for trials in (30, 65, 120):
                chosen = {m: 0 for m in MODELS}
                ed, er = [], []
                for _ in range(100):
                    res = fit(run_participant(true, sd, sr, trials, rng))
                    best = max(res, key=lambda m: res[m][0])
                    chosen[best] += 1
                    ed.append(abs(math.log(res[true][1] / sd)))
                    er.append(abs(math.log(res[true][2] / sr)))
                print(f"  {name:10} {true:10} {trials:12d} {chosen[true]:6d}%   {chosen['selection']:6d} /{chosen['joint']:4d} /{chosen['fusion']:4d}"
                      f" {100 * np.median(ed):10.0f}% {100 * np.median(er):10.0f}%")
            sys.stdout.flush()


if __name__ == "__main__":
    print("Checks:")
    passed = checks()
    print("CHECKS: " + ("ALL PASS" if passed else "FAILED"))
    if not passed:
        sys.exit(1)
    report()
