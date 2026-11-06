"""
Observer models and simulation gates for SONA Proposal 1.3's frozen oddity bank (Protocol tab: "Observer models and
analysis" and "Simulation gates").

The bank is read from the parity harness's export, Tools/SonaParity/cs_out/study_bank.csv (run the parity harness
first), so the simulation uses the bank the headset plays.

Each interval gives a reading of the two cues in ln-distance units (x_S, x_R) with Gaussian noise. Every model picks the
interval most likely to hold the change, knowing the bank but not the trial's direction, size or sign, so the likelihood
is summed over the bank's moves (Versfeld et al. 1996). The reference is integrated out with a flat prior.

    joint      both readings kept: noise sigma_S, sigma_R and correlation rho (rho = 0, or free)
    scalar     one weighted value z = w x_S + (1 - w) x_R plus noise tau; opposing moves cancel where w dS + (1 - w) dR = 0
    selection  the sweep cue with probability q, otherwise DRR (q = 0, 1, 0.5 or free)
    clearer    both readings, but each trial is answered by whichever cue singles out one interval more decisively

Every model has a fitted lapse rate: P = (1 - lambda) P_model + lambda / 3. Fits are maximum likelihood on a grid;
P_model for each bank cell and grid point comes from simulated readings with common random numbers.

Model comparison: each session is fitted on its axis, matching and catch trials (within a family, the variant with the
best AIC), then scored by the log predictive density of its opposing trials. The best family wins only by more than a
margin; otherwise the verdict is undecided.

Delta: per participant (A_match - A_oppose)_after - (A_match - A_oppose)_before, the opposing ratios weighted equally and
catch trials left out, "after" averaging visits 4 and 5 where present. The group value is the mean, with a participant
bootstrap 95% interval. shortcut: the proposal's primary analysis is a hierarchical logistic model; the bootstrap is its
stated check, used here because it is quick. Fit the hierarchical model in the analysis code before the design freeze.

Run with numpy:  python observers.py [--datasets 100] [--seed 2026]
The proposal's final error rates use 1,000 data sets per scenario: python observers.py --datasets 1000
"""
import argparse
import csv
import hashlib
import math
import os
import sys
import tempfile

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
BANK_CSV = os.path.join(HERE, "..", "SonaParity", "cs_out", "study_bank.csv")

SIGMAS = np.geomspace(0.03, 0.9, 16)       # ln units; indices 3 to 11 span the proposal's 0.05 to 0.40
RHOS = np.array([-0.6, -0.3, 0.0, 0.3, 0.6])
WS = np.linspace(0.05, 0.95, 19)
QS = np.linspace(0.0, 1.0, 11)
LAPSES = np.linspace(0.0, 0.15, 16)
M = 1500                                     # simulated trials per cell and grid point
FAMILIES = ["joint", "scalar", "selection", "clearer"]
MARGINS = [0.0, 1.0, 2.0, 4.0]               # nats of held-out log predictive density
SIGMA_LO, SIGMA_HI = 3, 12                   # baseline sigma indices drawn from [3, 12)
SHIFTS = np.arange(-3, 3)                    # sensitivity changes of 1.25^s: 0.51 to 1.57


def logsumexp(a, axis):
    m = a.max(axis=axis, keepdims=True)
    return (m + np.log(np.exp(a - m).sum(axis=axis, keepdims=True))).squeeze(axis)


def oddity_rule(r, moves, logprior, prec):
    """Bank-aware oddity decision. r: (..., 3, D) readings; moves: (K, D); prec: (D, D) inverse noise covariance.
    With a flat prior on the reference, log L(interval i moved by m) = a_i' P m - m' P m / 3 + const, where a_i is the
    reading's deviation from the mean of the three. Returns the chosen interval and its posterior of being odd."""
    a = r - r.mean(axis=-2, keepdims=True)
    s = (a @ prec) @ moves.T + (logprior - np.einsum("kd,de,ke->k", moves, prec, moves) / 3.0)
    lik = logsumexp(s, -1)
    post = np.exp(lik - logsumexp(lik, -1)[..., None])
    return post.argmax(-1), post.max(-1)


class Bank:
    """The frozen bank: one cell per distinct signed move, with its trial count."""

    def __init__(self, path):
        with open(path, newline="") as f:
            rows = list(csv.DictReader(f))
        keys = [(r["direction"], r["size"], r["sign"]) for r in rows]
        uniq = list(dict.fromkeys(keys))
        move = np.array([[int(r["sign"]) * float(r["size"]) * float(r["u_sweep"]), int(r["sign"]) * float(r["size"]) * float(r["u_drr"])]
                         for r in rows])
        self.cell = np.array([uniq.index(k) for k in keys])
        self.odd = np.array([int(r["odd"]) for r in rows])
        self.trial_move = move
        self.moves = np.array([move[keys.index(u)] for u in uniq])
        self.direction = [u[0] for u in uniq]
        self.size = np.array([float(u[1]) for u in uniq])
        self.K = len(uniq)
        self.n = np.bincount(self.cell, minlength=self.K).astype(float)
        self.logprior = np.log(self.n / self.n.sum())
        self.matching = np.array([d == "matching" for d in self.direction])
        self.opposing = np.array([d.startswith("opposing") for d in self.direction])
        self.train = ~self.opposing
        self.ratios = sorted({d for d in self.direction if d.startswith("opposing")}, key=lambda d: float(d.split("-")[1]))
        self.digest = hashlib.sha1(self.moves.tobytes() + self.n.tobytes()).hexdigest()[:12]


def single_cue(bank, X, noise, cue, sigma):
    """One cue's reading and decision, for the selection and clearer observers."""
    return oddity_rule(X[..., cue:cue + 1] + sigma * noise[..., cue:cue + 1], bank.moves[:, cue:cue + 1], bank.logprior,
                       np.array([[1.0 / sigma ** 2]]))


def scalar_choice(bank, X, noise, w, tau):
    mu = (w * bank.moves[:, 0] + (1 - w) * bank.moves[:, 1])[:, None]
    z = (w * X[..., 0] + (1 - w) * X[..., 1])[..., None] + tau * noise[..., :1]
    return oddity_rule(z, mu, bank.logprior, np.array([[1.0 / tau ** 2]]))[0]


def joint_choice(bank, X, noise, ss, sr, rho):
    cov = np.array([[ss * ss, rho * ss * sr], [rho * ss * sr, sr * sr]])
    return oddity_rule(X + noise @ np.linalg.cholesky(cov).T, bank.moves, bank.logprior, np.linalg.inv(cov))[0]


def build_tables(bank, seed=7):
    """P(correct) without lapses for every bank cell on every grid point, cached by bank and grid."""
    key = hashlib.sha1(repr((bank.digest, SIGMAS.tolist(), RHOS.tolist(), WS.tolist(), M, seed)).encode()).hexdigest()[:12]
    cache = os.path.join(tempfile.gettempdir(), "sona_observer_tables_" + key + ".npz")
    if os.path.exists(cache):
        return dict(np.load(cache))
    rng = np.random.default_rng(seed)
    noise = rng.standard_normal((M, 1, 3, 2))
    X = np.zeros((1, bank.K, 3, 2))
    X[0, :, 0] = bank.moves                     # odd interval first: the rule treats positions alike
    ns = len(SIGMAS)
    T = {"joint": np.zeros((ns, ns, len(RHOS), bank.K)), "scalar": np.zeros((len(WS), ns, bank.K))}
    for i, ss in enumerate(SIGMAS):
        for j, sr in enumerate(SIGMAS):
            for k, rho in enumerate(RHOS):
                T["joint"][i, j, k] = (joint_choice(bank, X, noise, ss, sr, rho) == 0).mean(0)
        print(f"  tables: joint {i + 1}/{ns}", end="\r", flush=True)
    for i, w in enumerate(WS):
        for j, tau in enumerate(SIGMAS):
            T["scalar"][i, j] = (scalar_choice(bank, X, noise, w, tau) == 0).mean(0)
    picks = {cue: [single_cue(bank, X, noise, c, s) for s in SIGMAS] for c, cue in enumerate(("sweep", "drr"))}
    for cue in ("sweep", "drr"):
        T[cue] = np.array([(ch == 0).mean(0) for ch, _ in picks[cue]])
    T["clearer"] = np.zeros((ns, ns, bank.K))
    for i, (cs, ps) in enumerate(picks["sweep"]):
        for j, (cr, pr) in enumerate(picks["drr"]):
            T["clearer"][i, j] = np.where(ps >= pr, cs == 0, cr == 0).mean(0)
    print(" " * 40, end="\r")
    np.savez(cache, **T)
    return T


class Variant:
    """One fitted model: its P table over a parameter grid, with the lapse grid folded in."""

    def __init__(self, family, name, P, params, k, bank):
        self.family, self.name, self.params, self.k = family, name, params, k
        p = (1 - LAPSES[None, :, None]) * P[:, None, :] + LAPSES[None, :, None] / 3.0
        p = np.clip(p, 1e-9, 1 - 1e-9)
        self.logp, self.log_wrong = np.log(p), np.log((1 - p) / 2.0)   # a wrong answer is one of two intervals
        self.train, self.test = bank.train, bank.opposing
        self.train_p, self.train_w = self.logp[..., self.train], self.log_wrong[..., self.train]

    def fit(self, k, n, mask=None):
        """Best grid point and lapse: on the training cells, or on mask."""
        lp, lw = (self.train_p, self.train_w) if mask is None else (self.logp[..., mask], self.log_wrong[..., mask])
        m = self.train if mask is None else mask
        ll = lp @ k[m] + lw @ (n - k)[m]
        g, l = np.unravel_index(ll.argmax(), ll.shape)
        return ll[g, l], g, l

    def lpd(self, g, l, k, n):
        """Log predictive density of the held-out opposing cells."""
        t = self.test
        return self.logp[g, l, t] @ k[t] + self.log_wrong[g, l, t] @ (n - k)[t]


def variants(T, bank):
    K, ns = bank.K, len(SIGMAS)
    grid2 = np.array([(a, b) for a in SIGMAS for b in SIGMAS])
    return [
        Variant("joint", "rho = 0", T["joint"][:, :, 2].reshape(-1, K), np.c_[grid2, np.zeros(len(grid2))], 2, bank),
        Variant("joint", "rho free", T["joint"].reshape(-1, K),
                np.array([(a, b, r) for a in SIGMAS for b in SIGMAS for r in RHOS]), 3, bank),
        Variant("scalar", "w free", T["scalar"].reshape(-1, K), np.array([(w, t, np.nan) for w in WS for t in SIGMAS]), 2, bank),
        Variant("selection", "q = 1", T["sweep"], np.c_[SIGMAS, np.full(ns, np.nan), np.ones(ns)], 1, bank),
        Variant("selection", "q = 0", T["drr"], np.c_[np.full(ns, np.nan), SIGMAS, np.zeros(ns)], 1, bank),
        Variant("selection", "q = 0.5", (0.5 * (T["sweep"][:, None] + T["drr"][None])).reshape(-1, K),
                np.c_[grid2, np.full(len(grid2), 0.5)], 2, bank),
        Variant("selection", "q free",
                (QS[:, None, None, None] * T["sweep"][None, :, None] + (1 - QS)[:, None, None, None] * T["drr"][None, None]).reshape(-1, K),
                np.array([(a, b, q) for q in QS for a in SIGMAS for b in SIGMAS]), 3, bank),
        Variant("clearer", "both read", T["clearer"].reshape(-1, K), np.c_[grid2, np.zeros(len(grid2))], 2, bank),
    ]


def compare(vs, bank, k):
    """Held-out comparison for one session: per family the best-AIC variant on the training cells, scored on opposing."""
    score = {}
    for fam in FAMILIES:
        best = None
        for v in (x for x in vs if x.family == fam):
            ll, g, l = v.fit(k, bank.n)
            aic = -2 * ll + 2 * (v.k + 1)
            if best is None or aic < best[0]:
                best = (aic, v, g, l)
        _, v, g, l = best
        score[fam] = v.lpd(g, l, k, bank.n)
    order = sorted(score, key=score.get, reverse=True)
    return order[0], score[order[0]] - score[order[1]]


def verdict(best, gap, margin):
    return best if gap > margin else "undecided"


# ---------------------------------------------------------------- simulated participants

class Observer:
    """A true observer: family, grid indices of its parameters, and a lapse rate."""

    def __init__(self, family, idx, lapse):
        self.family, self.idx, self.lapse = family, idx, lapse

    def table_p(self, T):
        name = {"joint": "joint", "scalar": "scalar", "clearer": "clearer"}.get(self.family)
        if self.family == "selection":
            q, i, j = self.idx
            p = QS[q] * T["sweep"][i] + (1 - QS[q]) * T["drr"][j]
        else:
            p = T[name][self.idx]
        return (1 - self.lapse) * p + self.lapse / 3.0

    def respond(self, bank, rng):
        """One session: every bank trial answered once, trial by trial. Returns correct counts per cell."""
        n = len(bank.odd)
        X = np.zeros((n, 3, 2))
        X[np.arange(n), bank.odd] = bank.trial_move
        noise = rng.standard_normal((n, 3, 2))
        if self.family == "joint":
            i, j, r = self.idx
            choice = joint_choice(bank, X, noise, SIGMAS[i], SIGMAS[j], RHOS[r])
        elif self.family == "scalar":
            i, j = self.idx
            choice = scalar_choice(bank, X, noise, WS[i], SIGMAS[j])
        else:
            if self.family == "selection":
                q, i, j = self.idx
            else:
                i, j = self.idx
            cs, ps = single_cue(bank, X, noise, 0, SIGMAS[i])
            cr, pr = single_cue(bank, X, noise, 1, SIGMAS[j])
            use_sweep = rng.random(n) < QS[q] if self.family == "selection" else ps >= pr
            choice = np.where(use_sweep, cs, cr)
        lapsed = rng.random(n) < self.lapse
        choice = np.where(lapsed, rng.integers(0, 3, n), choice)
        return np.bincount(bank.cell, weights=(choice == bank.odd).astype(float), minlength=bank.K)


def draw(family, rng, base=None, shift=(0, 0)):
    """A random observer of a family; with base, the same observer after a sensitivity change (sigma index shifts)."""
    clip = lambda i: int(np.clip(i, 0, len(SIGMAS) - 1))
    if base is not None and base.family == family:
        idx = list(base.idx)
        if family == "joint":
            idx[0], idx[1] = clip(idx[0] - shift[0]), clip(idx[1] - shift[1])
        elif family == "scalar":
            idx[1] = clip(idx[1] - shift[0])
        elif family == "selection":
            idx[1], idx[2] = clip(idx[1] - shift[0]), clip(idx[2] - shift[1])
        else:
            idx[0], idx[1] = clip(idx[0] - shift[0]), clip(idx[1] - shift[1])
        return Observer(family, tuple(idx), base.lapse)
    s, r = rng.integers(SIGMA_LO, SIGMA_HI, 2)
    lapse = rng.uniform(0.0, 0.10)
    if family == "joint":
        return Observer(family, (s, r, rng.integers(len(RHOS))), lapse)
    if family == "scalar":
        return Observer(family, (rng.integers(1, len(WS) - 1), s), lapse)
    if family == "selection":
        return Observer(family, (rng.integers(len(QS)), s, r), lapse)
    return Observer(family, (s, r), lapse)


def accuracy_gap(bank, k):
    """A_match - A_oppose from correct counts (or expected counts), the opposing ratios weighted equally."""
    a_match = k[bank.matching].sum() / bank.n[bank.matching].sum()
    a_opp = np.mean([k[[d == ratio for d in bank.direction]].sum() / bank.n[[d == ratio for d in bank.direction]].sum()
                     for ratio in bank.ratios])
    return a_match - a_opp


def simulate(bank, T, vs, pre_family, post_family, datasets, rng, n_people=16):
    """Data sets of N people at visits 1, 4 and 5: Delta's bias, coverage and detection, and the model verdicts."""
    deltas_hat, deltas_true, lows, highs = [], [], [], []
    verdicts = {m: {} for m in MARGINS}
    for _ in range(datasets):
        missing = rng.uniform(0.10, 0.20)
        per_hat, per_true = [], []
        for _p in range(n_people):
            pre = draw(pre_family, rng)
            shift = tuple(rng.choice(SHIFTS, 2))
            post = draw(post_family, rng, base=pre if post_family == pre_family else None, shift=shift)
            if post_family != pre_family:
                post.lapse = pre.lapse
            sessions = [(pre, pre.respond(bank, rng))]
            present = [v for v in (4, 5) if rng.random() >= missing]
            sessions += [(post, post.respond(bank, rng)) for _v in present]
            for obs, k in sessions:
                best, gap = compare(vs, bank, k)
                for m in MARGINS:
                    key = (obs.family, verdict(best, gap, m))
                    verdicts[m][key] = verdicts[m].get(key, 0) + 1
            if present:
                before = accuracy_gap(bank, sessions[0][1])
                after = np.mean([accuracy_gap(bank, k) for _o, k in sessions[1:]])
                per_hat.append(after - before)
                per_true.append(accuracy_gap(bank, bank.n * post.table_p(T)) - accuracy_gap(bank, bank.n * pre.table_p(T)))
        per_hat = np.array(per_hat)
        boot = per_hat[rng.integers(0, len(per_hat), (2000, len(per_hat)))].mean(1)
        lo, hi = np.percentile(boot, [2.5, 97.5])
        deltas_hat.append(per_hat.mean())
        deltas_true.append(np.mean(per_true))
        lows.append(lo)
        highs.append(hi)
    d_hat, lows, highs = map(np.array, (deltas_hat, lows, highs))
    # The interval is for the population's Delta, estimated by the mean true Delta over all data sets.
    pop = np.mean(deltas_true)
    return {
        "true": pop, "bias": (d_hat - pop).mean(), "coverage": np.mean((lows <= pop) & (pop <= highs)),
        "width": np.mean(highs - lows), "detected": np.mean(lows > 0), "meaningful": np.mean(lows > 0.10), "verdicts": verdicts,
    }


# ---------------------------------------------------------------- checks and report

def checks(bank, T):
    rng = np.random.default_rng(11)
    ok = True

    def check(cond, what):
        nonlocal ok
        ok &= bool(cond)
        print(("  ok   " if cond else "  FAIL ") + what)

    n = 20000
    noise = rng.standard_normal((n, 3, 2))
    zero = np.zeros((n, 3, 2))
    pc0 = (joint_choice(bank, zero, noise, 0.2, 0.3, 0.0) == 0).mean()
    check(abs(pc0 - 1 / 3) < 4 * math.sqrt(2 / 9 / n), f"no change: the bank-aware rule is at chance ({pc0:.3f})")

    kstar = float(bank.ratios[1].split("-")[1])
    w = kstar / (1 + kstar)
    cancel = [c for c, d in enumerate(bank.direction) if d == f"opposing-{kstar:.2f}"]
    X = np.zeros((n, 3, 2))
    X[:, 0] = bank.moves[cancel[-1]]
    pc_cancel = (scalar_choice(bank, X, noise, w, 0.1) == 0).mean()
    check(abs(pc_cancel - 1 / 3) < 4 * math.sqrt(2 / 9 / n),
          f"scalar observer with w = k*/(1 + k*) = {w:.3f} is at chance on opposing-{kstar:.2f} at the largest size ({pc_cancel:.3f})")

    match = [c for c, d in enumerate(bank.direction) if d == "matching"]
    opp1 = [c for c, d in enumerate(bank.direction) if d == "opposing-1.00"]
    i = 8
    diff = abs(T["joint"][i, i, 2, match].mean() - T["joint"][i, i, 2, opp1].mean()) if opp1 else 0.0
    check(not opp1 or diff < 0.03, f"joint observer, equal noise: matching and opposing-1.00 are equally easy (difference {diff:.3f})")

    axis = [c for c, d in enumerate(bank.direction) if d == "sweep-axis"]
    curve = T["sweep"][:, axis].mean(1)
    check(np.all(np.diff(curve) < 0.02) and curve[0] > 0.9 and curve[-1] < 0.5,
          f"sweep-axis accuracy falls as sweep noise grows ({curve[0]:.2f} at {SIGMAS[0]:.2f} to {curve[-1]:.2f} at {SIGMAS[-1]:.2f})")

    vs = variants(T, bank)
    true = Observer("joint", (6, 10, 2), 0.03)
    k = sum(true.respond(bank, rng) for _ in range(20))
    v = vs[0]
    ll, g, l = v.fit(k, 20 * bank.n, np.ones(bank.K, bool))
    ss, sr = v.params[g][:2]
    check(abs(math.log(ss / SIGMAS[6])) < 0.3 and abs(math.log(sr / SIGMAS[10])) < 0.3,
          f"recovery over 20 banks: sigma_S {ss:.3f} (true {SIGMAS[6]:.3f}), sigma_R {sr:.3f} (true {SIGMAS[10]:.3f}), lapse {LAPSES[l]:.2f} (true 0.03)")
    return ok, vs


def report(bank, T, vs, datasets, seed):
    print("\nPredicted accuracy per direction (mean over sizes, small to large), lapse 0:")
    examples = [
        ("joint, equal noise 0.15", "joint", (np.abs(SIGMAS - 0.15).argmin(),) * 2 + (2,)),
        ("joint, sweep 0.09 / DRR 0.29", "joint", (5, 10, 2)),
        ("scalar w 0.65, tau 0.09", "scalar", (12, 5)),
        ("sweep only 0.09", "sweep", (5,)),
        ("clearer, 0.09 / 0.29", "clearer", (5, 10)),
    ]
    dirs = list(dict.fromkeys(d for d in bank.direction if not d.startswith("catch")))
    print(f"  {'observer':30}" + "".join(f"{d:>22}" for d in dirs))
    for label, table, idx in examples:
        p = T[table][idx]
        cells = []
        for d in dirs:
            sizes = sorted({s for s, dd in zip(bank.size, bank.direction) if dd == d})
            accs = [p[[(dd == d and s == sz) for dd, s in zip(bank.direction, bank.size)]].mean() for sz in sizes]
            cells.append(" / ".join(f"{a:.2f}" for a in accs))
        print(f"  {label:30}" + "".join(f"{c:>22}" for c in cells))

    print(f"\nSimulation: {datasets} data sets of 16 people per scenario; lapses 0-10%, 10-20% missing post visits,")
    print("  noise 0.06-0.36 per cue, sensitivity changes 0.5-1.6x between baseline and post, rho -0.6 to 0.6, w 0.1-0.9.")
    rng = np.random.default_rng(seed)
    scenarios = [(f, f) for f in FAMILIES] + [("joint", "scalar")]
    results = []
    for pre, post in scenarios:
        r = simulate(bank, T, vs, pre, post, datasets, rng)
        results.append((pre, post, r))
        print(f"  done: {pre} -> {post}", end="\r", flush=True)
    print(" " * 40)
    print("Delta (A_match - A_oppose, after minus before), participant bootstrap 95% interval:")
    print(f"  {'baseline -> post':22} {'true':>7} {'bias':>7} {'coverage':>9} {'width':>7} {'CI > 0':>7} {'CI > 0.10':>10}")
    for pre, post, r in results:
        print(f"  {pre + ' -> ' + post:22} {r['true']:7.3f} {r['bias']:+7.3f} {r['coverage']:9.2f} {r['width']:7.3f} {r['detected']:7.2f} {r['meaningful']:10.2f}")
    bias_ok = all(abs(r["bias"]) <= 0.02 for *_x, r in results)
    cover_ok = all(0.90 <= r["coverage"] <= 0.98 for *_x, r in results)
    print(f"  gate: bias within 0.02 {'met' if bias_ok else 'NOT met'}; coverage 90-98% {'met' if cover_ok else 'NOT met'}")

    print("\nModel verdicts per session (fit on axis, matching and catch trials; scored on held-out opposing trials):")
    for m in MARGINS:
        tally = {}
        for *_x, r in results:
            for key, c in r["verdicts"][m].items():
                tally[key] = tally.get(key, 0) + c
        print(f"  margin {m:.0f} nats: " + "   ".join(
            f"{true}: right {_rate(tally, true, true):.0%}, undecided {_rate(tally, true, 'undecided'):.0%}"
            + ("" if true == "scalar" else f", false scalar {_rate(tally, true, 'scalar'):.0%}") for true in FAMILIES))
    print("  gate: right family >= 80% and false scalar verdicts <= 5% (undecided counted), in the scenarios judged meaningful")


def _rate(tally, true, answer):
    total = sum(c for (t, _a), c in tally.items() if t == true)
    return tally.get((true, answer), 0) / total if total else float("nan")


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[1])
    ap.add_argument("--datasets", type=int, default=100)
    ap.add_argument("--seed", type=int, default=2026)
    ap.add_argument("--bank", default=BANK_CSV)
    args = ap.parse_args()
    if not os.path.exists(args.bank):
        sys.exit(f"No bank at {args.bank}: run Tools/SonaParity/run_parity.ps1 first, which writes it.")
    bank = Bank(args.bank)
    print(f"Bank: {len(bank.odd)} trials in {bank.K} cells, opposing ratios {', '.join(r.split('-')[1] for r in bank.ratios)}")
    T = build_tables(bank)
    print("Checks:")
    passed, vs = checks(bank, T)
    print("CHECKS: " + ("ALL PASS" if passed else "FAILED"))
    if not passed:
        sys.exit(1)
    report(bank, T, vs, args.datasets, args.seed)


if __name__ == "__main__":
    main()
