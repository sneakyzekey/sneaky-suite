#!/usr/bin/env python3
"""
train_predictor.py — trains a standalone model on EVERY field Recorder
captures (not just Council's votes) to predict the forward move directly.
Different question from train_council_weights.py: that one asks "are
Council's weights right", this one asks "is there predictive signal in the
raw sensor readings at all, independent of how Council currently combines
them."

DESIGN CHOICE, STATED PLAINLY: this fits a ridge-regularized LINEAR model,
not a black-box gradient-boosted tree or neural net. On a dataset this size
(likely thousands, not millions of rows) and this noisy (financial time
series), a complex model will find "patterns" that are pure noise and will
look great in-sample while being worthless out-of-sample. A simple, fully
interpretable model that's honest about a weak result is more useful than a
complex one that lies to you with a good-looking backtest. If you later have
a much larger dataset and want to try something more expressive, the same
walk-forward discipline this script uses still applies.

VALIDATION METHOD: purged walk-forward with an embargo, by default — same
method train_council_weights.py uses, and the same one the original Sentinel
Lab's own `cv` module uses ("purged walk-forward with an embargo — never
random k-fold"). Pass --legacy-split for one plain chronological 70/30 split
instead.

WHAT IT DOES NOT DO: does not place trades, does not modify any indicator,
does not tell you to trade this. It's a diagnostic for whether your sensors
carry information the current fusion logic (Council/Confluence) might not
be fully using yet.

Usage:
    python train_predictor.py <recorder-folder> --bartype 4v1x1 --horizon 20
    python train_predictor.py <recorder-folder> --bartype 4v1x1 --horizon 20 --tick-only
    python train_predictor.py <recorder-folder> --bartype 4v1x1 --horizon 20 --cv-folds 8
    python train_predictor.py <recorder-folder> --bartype 4v1x1 --horizon 20 --cost-ticks 2
    python train_predictor.py <recorder-folder> --bartype 4v1x1 --horizon 20 --legacy-split --train-frac 0.6

Requires: numpy (pip install numpy --break-system-packages, if not already present)
"""
import sys, os, argparse
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sneaky_ml_common import load_rows, chronological_split, purged_walk_forward_splits, cost_adjusted, fwd_cls

try:
    import numpy as np
except ImportError:
    print("This script needs numpy. Install it with:")
    print("    pip install numpy --break-system-packages")
    sys.exit(1)

# (json field, kind) — kind controls how it's encoded. Add a new sensor's
# fields here when you extend Recorder; nothing else in this script needs to
# change, since the feature matrix is built generically from this list.
FIELDS = [
    ("cnlConv", "num"), ("cnlSize", "num"), ("cnlNet", "num"), ("cnlActive", "num"),
    ("voters", "num"), ("rvol", "num"),
    ("eyeDir", "dir"), ("prsDir", "dir"), ("imktLean", "dir"), ("cvbBias", "dir"),
    ("trendDir", "dir"), ("lvlSide", "dir"), ("mtfBias", "dir"), ("profLoc", "dir"), ("profSignal", "dir"),
    ("lvlDistTicks", "num"), ("lvlDistAtr", "num"), ("mtfAlign", "num"),
    ("mtfAlignedCount", "num"), ("mtfTfCount", "num"), ("profDistPocTicks", "num"),
    ("phase", "num"), ("regime", "num"),
    ("dryUp", "bool"), ("adxStrong", "bool"), ("mtfAllAgree", "bool"),
    ("profNearHvn", "bool"), ("profNearLvn", "bool"),
    ("cnlBreadthDamped", "bool"), ("cnlSqueezeDamped", "bool"),
    ("cnlDryUpDamped", "bool"), ("cnlInValueDamped", "bool"),
    ("cnfRegimeOk", "bool"), ("cnfAdxOk", "bool"), ("cnfLiqOk", "bool"), ("cnfLevelOk", "bool"),
    ("cnfVolOk", "bool"), ("cnfZscoreOk", "bool"), ("cnfCvbOk", "bool"),
    ("cnfImktOk", "bool"), ("cnfEyeOk", "bool"), ("cnfFlowOk", "bool"), ("cnfMtfOk", "bool"),
    ("cnfProfOk", "bool"), ("cnfPartOk", "bool"), ("cnfStructOk", "bool"), ("cnfClockOk", "bool"),
    # 2026-08-09 — added: these five didn't exist in Recorder's schema when this list was first
    # written and the docstring's "every field Recorder captures" promise silently went stale as
    # Recorder grew. cnfProfOverextOk was already in grade_sneaky.py's gate list; the other four
    # (Flux/TrendFusion/Arch/News) are newer still.
    ("cnfFluxOk", "bool"), ("cnfTfusOk", "bool"), ("cnfArchOk", "bool"), ("cnfNewsOk", "bool"),
    ("cnfProfOverextOk", "bool"),
    # 2026-08-09 — cnfOptOk and cnfChopOk were BOTH also missed in the earlier pass that added the
    # five fields just above -- caught while doing unrelated work checking Recorder's full field
    # list, not because I'd already gotten chop right. Correcting the record rather than leaving a
    # wrong claim in a comment: neither was present before this edit.
    # NOTE ON cnfOptOk SPECIFICALLY: unlike every other gate here, it's a COMPOSITE -- it folds in
    # the 9 gates already separately tracked (imkt/eye/flow/participation/structure/clock/mtf/
    # profile/profile-overext/flux/trendfusion/arch/news) PLUS 7 UNTRACKED price-derived ones with
    # no field of their own (cci/exhaustion/harmonic/brick-dir/brick-run/absorption/inefficient) --
    # see grade_sneaky.py's own comment above cnf_gate_blockers() for the same finding. Expect real
    # collinearity with those other 9 columns; its unique information is only the part explained by
    # the 7 untracked sub-gates, not the whole signal.
    ("cnfChopOk", "bool"), ("cnfOptOk", "bool"),
]


def build_matrix(rows, horizon):
    """Each field contributes TWO columns: its own value (0 if missing) and
    a missing-indicator (1 if that field was null/absent on this row). This
    is deliberate — a sensor that isn't attached on some rows must not be
    silently treated as reading zero; the model gets to see the difference."""
    names = []
    for f, kind in FIELDS:
        names.append(f)
        names.append(f + "__missing")

    X, y = [], []
    for r in rows:
        cls = fwd_cls(r, horizon)
        if cls is None:
            continue
        vec = []
        for f, kind in FIELDS:
            v = r.get(f)
            missing = 1.0 if v is None else 0.0
            if v is None:
                vec.append(0.0)
            elif kind == "bool":
                vec.append(1.0 if v else 0.0)
            else:
                vec.append(float(v))
            vec.append(missing)
        X.append(vec)
        y.append(cls)
    return np.array(X, dtype=float), np.array(y, dtype=float), names


def fit_and_predict_fold(train_rows, test_rows, horizon, l2):
    """Standardizes using TRAIN stats only (never test-set stats — that would
    leak held-out information into the 'trained' model), fits ridge, and
    returns predictions on both sets plus the fitted coefficients. Shared by
    every fold and by --legacy-split, so both paths use identical fitting
    code."""
    Xtr, ytr, names = build_matrix(train_rows, horizon)
    Xte, yte, _ = build_matrix(test_rows, horizon)
    if len(Xtr) < len(names) + 5:
        return None
    mu, sd = Xtr.mean(axis=0), Xtr.std(axis=0)
    sd[sd < 1e-9] = 1.0
    Xtr_s = (Xtr - mu) / sd
    Xte_s = (Xte - mu) / sd if len(Xte) else Xte
    y_mu = ytr.mean()

    A = Xtr_s.T @ Xtr_s + l2 * np.eye(Xtr_s.shape[1])
    coefs = np.linalg.solve(A, Xtr_s.T @ (ytr - y_mu))

    pred_tr = Xtr_s @ coefs + y_mu
    pred_te = Xte_s @ coefs + y_mu if len(Xte) else np.array([])
    return {"pred_tr": pred_tr, "ytr": ytr, "pred_te": pred_te, "yte": yte,
            "coefs": coefs, "names": names, "train_n": len(Xtr), "test_n": len(Xte)}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("folder")
    ap.add_argument("--bartype", required=True)
    ap.add_argument("--after", default=None,
                     help="Only include rows recorded at or after this ISO timestamp (or date prefix, e.g. 2026-08-04). Useful for isolating data recorded after a live config/weight change.")
    ap.add_argument("--instance", default=None,
                     help="Only include rows from Council instances tagged with this InstanceTag.")
    ap.add_argument("--require-lane-ok", action="store_true",
                     help="Drop rows recorded during a lane-registration gap (see sneaky_ml_common.lane_ok) "
                          "— their sensor fields may reflect a different chart's published state even though "
                          "cnlInstance itself is correct. No-op (keeps everything) on recordings from before "
                          "Recorder started writing the 'scope' field.")
    ap.add_argument("--horizon", type=int, default=20)
    ap.add_argument("--tick-only", action="store_true")
    ap.add_argument("--train-frac", type=float, default=0.7,
                     help="Only used with --legacy-split. Fraction of rows for the single train block.")
    ap.add_argument("--cv-folds", type=int, default=5,
                     help="Number of purged walk-forward folds. Default 5. Ignored if --legacy-split is set.")
    ap.add_argument("--legacy-split", action="store_true",
                     help="Fall back to one plain chronological train/test split instead of purged walk-forward.")
    ap.add_argument("--cost-ticks", type=float, default=0.0,
                     help="Flat round-trip cost in ticks, subtracted from every scored trade. Default 0 — a stress test using a number YOU supply, not a real fee/slippage model.")
    ap.add_argument("--l2", type=float, default=5.0,
                     help="Ridge penalty. Higher = safer with many features/few rows. Default 5.0 (wide feature set here vs the weight-fitter's narrower one).")
    ap.add_argument("--trade-threshold-pct", type=float, default=70.0,
                     help="Only 'trade' on predictions in the top N%% by |predicted edge|, mirroring the idea that a near-zero prediction means no opinion. Default 70 (top 30%% of |prediction| magnitudes).")
    args = ap.parse_args()

    rows, nfiles = load_rows(args.folder, args.bartype, args.after, args.instance,
                              require_lane_ok=args.require_lane_ok)
    if args.tick_only:
        rows = [r for r in rows if r.get("prsTick") is True]
    print("=" * 70)
    print("STANDALONE PREDICTOR — all recorded sensor fields")
    print("=" * 70)
    print("files", nfiles, " rows", len(rows), " bartype", args.bartype)
    print("features:", len(FIELDS), "sensor fields x2 (value + missing-flag) =", len(FIELDS) * 2, "columns")

    if args.legacy_split:
        train_rows, test_rows = chronological_split(rows, args.train_frac)
        print("mode: legacy single split   train rows", len(train_rows),
              " test rows (held out, LATER in time)", len(test_rows))
        folds = [(train_rows, test_rows)]
    else:
        folds = list(purged_walk_forward_splits(rows, args.horizon, args.cv_folds))
        print("mode: purged walk-forward, %d fold(s), embargo=%d bars (the horizon)" %
              (len(folds), args.horizon))
        if not folds:
            print("\nNot enough data for even one fold at this --cv-folds count. Try a smaller")
            print("--cv-folds, or --legacy-split for a single plain split instead.")
            sys.exit(1)

    def corr(a, b):
        if len(a) < 2 or np.std(a) < 1e-9 or np.std(b) < 1e-9:
            return float("nan")
        return float(np.corrcoef(a, b)[0, 1])

    if args.cost_ticks:
        print("\nCOST MODEL: subtracting a flat %.2f ticks round-trip from every scored trade" % args.cost_ticks)
        print("below. This is a number YOU supplied, not a real fee/slippage schedule for your")
        print("broker or instrument — treat it as a stress test, not a precise cost estimate.")

    pooled_pred_te, pooled_yte = [], []
    last_fit, fold_stats = None, []
    for fi, (train_rows, test_rows) in enumerate(folds, start=1):
        fit = fit_and_predict_fold(train_rows, test_rows, args.horizon, args.l2)
        if fit is None or fit["test_n"] == 0:
            print("Fold %d: not enough rows to fit — skipped." % fi)
            continue
        last_fit = fit
        fold_stats.append((fi, fit["train_n"], fit["test_n"],
                            corr(fit["pred_tr"], fit["ytr"]), corr(fit["pred_te"], fit["yte"])))
        pooled_pred_te.extend(fit["pred_te"].tolist())
        pooled_yte.extend(fit["yte"].tolist())

    if last_fit is None:
        print("\nNo fold produced a usable fit. You likely need more recorded data, or a")
        print("smaller --cv-folds count.")
        return

    pooled_pred_te = np.array(pooled_pred_te)
    pooled_yte = np.array(pooled_yte)

    if len(pooled_pred_te) < 200:
        print("\nWARNING: only %d pooled held-out rows across all folds. Treat everything" % len(pooled_pred_te))
        print("below as exploratory, not conclusive.")

    if len(fold_stats) > 1:
        print("\n" + "-" * 70)
        print("PER-FOLD FIT QUALITY  (watch for consistency, not just the pooled number)")
        print("-" * 70)
        for fi, tr_n, te_n, c_tr, c_te in fold_stats:
            print("  fold %d (train=%-5d test=%-4d)  in-sample %+.3f   out-of-sample %+.3f" %
                  (fi, tr_n, te_n, c_tr, c_te))
        print("\n  If out-of-sample correlation swings between clearly positive and clearly")
        print("  negative across folds, that's instability — don't trust the pooled number")
        print("  below as much as you would if every fold agreed in sign.")

    print("\n" + "-" * 70)
    print("POOLED FIT QUALITY  (every fold's held-out predictions combined)")
    print("-" * 70)
    print("  out-of-sample corr(predicted, actual) %+.3f   <- the number that matters" %
          corr(pooled_pred_te, pooled_yte))
    print("  (compare against the per-fold table above, not just this one number —")
    print("   a pooled correlation can look fine even if individual folds disagreed)")

    print("\n" + "-" * 70)
    print("TOP FEATURES  (from the LAST fold — trained on the most data — by")
    print("               |standardized coefficient|, ranking influence fairly")
    print("               across features with very different natural scales)")
    print("-" * 70)
    coefs, names = last_fit["coefs"], last_fit["names"]
    order = np.argsort(-np.abs(coefs))
    for i in order[:20]:
        flag = "  <- missing-indicator, not the sensor itself" if names[i].endswith("__missing") else ""
        print("  %-28s %+.4f%s" % (names[i], coefs[i], flag))

    # ── does trading only the model's highest-conviction predictions beat
    # the unconditional base rate, pooled across every fold's held-out data? ──
    base_edge = float(np.mean(cost_adjusted(pooled_yte, args.cost_ticks))) if len(pooled_yte) else float("nan")
    thresh = np.percentile(np.abs(pooled_pred_te), args.trade_threshold_pct) if len(pooled_pred_te) else 0
    mask = np.abs(pooled_pred_te) >= thresh
    if mask.sum() > 0:
        sel = cost_adjusted(np.sign(pooled_pred_te[mask]) * pooled_yte[mask], args.cost_ticks)
        selective_edge = float(np.mean(sel))
        selective_win = float(np.mean(sel > 0))
    else:
        selective_edge = selective_win = float("nan")

    print("\n" + "=" * 70)
    print("SELECTIVITY TEST  (pooled held-out data, every fold combined)")
    print("=" * 70)
    print("  base rate, ALL held-out rows        mean signed move %+.2f   n=%d" % (base_edge, len(pooled_yte)))
    print("  top %.0f%% |prediction| rows only      mean signed edge %+.2f   win %.1f%%   n=%d" %
          (100 - args.trade_threshold_pct, selective_edge, selective_win * 100, int(mask.sum())))
    if not np.isnan(selective_edge) and not np.isnan(base_edge):
        if selective_edge > base_edge:
            print("\nThe model's highest-conviction predictions beat the base rate on data")
            print("it never trained on. Worth extending this test on more data before")
            print("trusting it further — pooled folds are a start, not proof.")
        else:
            print("\nThe model's highest-conviction predictions did NOT beat the base rate")
            print("out-of-sample. Read that as 'no demonstrated edge yet' — not necessarily")
            print("'no edge exists' — the honest reading of a small/early dataset.")

    print("\nCAVEATS, worth reading every time you run this:")
    print("  - Sign-based edge only, same as everywhere else in this suite's tooling.")
    if not args.cost_ticks:
        print("    No transaction cost is assumed (--cost-ticks 0, the default). Pass e.g.")
        print("    --cost-ticks 2 to see whether the edge survives a flat assumed cost.")
    print("  - Purged walk-forward is several experiments, not one — but still re-run")
    print("    periodically as you record more data, and watch whether conclusions")
    print("    HOLD UP, not just whether this one run looked good.")
    print("  - This is a diagnostic tool, not a signal generator. Nothing here should")
    print("    move into live trading without a lot more validation than one script run.")


if __name__ == "__main__":
    main()
