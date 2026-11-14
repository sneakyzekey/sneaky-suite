#!/usr/bin/env python3
"""
train_confluence.py — audits whether each of Confluence's gates is actually
earning its place in the cascade, using real recorded outcomes.

WHY THIS ISN'T "train_council_weights.py FOR CONFLUENCE": Council COMBINES
its voters via a weighted sum, so "what weight should each voter have" is a
real, fittable question — that's what train_council_weights.py answers.
Confluence doesn't combine gates that way. It's a strict AND-cascade: every
REQUIRED gate must independently read True, or nothing fires. There's no
weight to fit, because there's no weighted sum to fit it into.

The honest, analogous question for a gate cascade isn't "what weight" — it's
"does this gate's own True/False split actually correlate with a better
forward outcome, or is it just adding friction (fewer fires) without adding
value?" That's what this script measures, per gate: the mean signed edge on
rows where the gate read True, against rows where it read False, among rows
Confluence had a directional read on (cnfBias != 0) either way — this asks
about the gate's OWN discriminating power, not just about bars where every
other gate also happened to agree.

STABILITY, NOT JUST ONE NUMBER: a single aggregate True-vs-False difference
can be a lucky (or unlucky) slice of history. Every gate is also checked
fold-by-fold under the same purged walk-forward split train_council_weights.py
and train_predictor.py use, and reported by how many folds agree on the
SIGN of the difference — a gate showing a positive edge-gap in 4 of 5 folds
is a genuinely different finding than one showing +5 ticks in the aggregate
but flipping sign every other fold.

WHAT IT DOES NOT DO: does not turn any gate on or off for you, does not
touch SneakyConfluence's properties. It prints what it found; you decide
which gates are worth requiring, same philosophy as the other two scripts.

Usage:
    python train_confluence.py <recorder-folder> --bartype 4v1x1 --horizon 20
    python train_confluence.py <recorder-folder> --bartype 4v1x1 --horizon 20 --tick-only
    python train_confluence.py <recorder-folder> --bartype 4v1x1 --horizon 20 --cv-folds 8
    python train_confluence.py <recorder-folder> --bartype 4v1x1 --horizon 20 --cost-ticks 2
    python train_confluence.py <recorder-folder> --bartype 4v1x1 --horizon 20 --min-n 20

No numpy required — this is bucket means and counts, not matrix algebra.
"""
import sys, os, argparse
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sneaky_ml_common import load_rows, purged_walk_forward_splits, cost_adjusted, fwd_cls


# 2026-08-09 — every Confluence gate Recorder currently captures. Kept as one
# explicit list (rather than discovered from whatever keys happen to be in
# the first row) so a row simply missing a gate — an older recording, a gate
# added after that session — is visibly "N/A", not silently absent from the
# report. Mirrors the same list added to train_predictor.py's FIELDS the
# same night, for the same reason: this should track every real gate,
# not whichever ones happened to exist when it was first written.
GATE_FIELDS = [
    "cnfRegimeOk", "cnfAdxOk", "cnfLiqOk", "cnfLevelOk", "cnfVolOk", "cnfZscoreOk",
    "cnfCvbOk", "cnfImktOk", "cnfEyeOk", "cnfFlowOk", "cnfMtfOk", "cnfProfOk",
    "cnfPartOk", "cnfStructOk", "cnfClockOk", "cnfFluxOk", "cnfTfusOk", "cnfArchOk",
    "cnfNewsOk", "cnfProfOverextOk",
    # 2026-08-09 — added along with train_predictor.py's own fix, same correction, same reason.
    # cnfOptOk is a COMPOSITE (folds in the 9 gates above it here PLUS 7 untracked price-derived
    # sub-gates with no field of their own -- see grade_sneaky.py's own comment above
    # cnf_gate_blockers() for the source of this). Expect its True/False split to correlate
    # heavily with several gates already in this list; its unique information is only the part
    # explained by those untracked sub-gates.
    "cnfChopOk", "cnfOptOk",
]


def signed_edge(row, h, direction_key="cnfBias"):
    """Mirrors grade_sneaky.py's own signed_edge() exactly (same formula, same
    exclusion of rows with no directional read) so numbers from this script
    and that one are directly comparable, not just similarly-shaped."""
    d = row.get(direction_key)
    fwd = row.get("fwd") or {}
    hh = fwd.get("h%d" % h) or {}
    c = hh.get("cls")
    if d is None or c is None or d == 0:
        return None
    return c * (1 if d > 0 else -1)


def bucket_stats(rows, gate, h, cost_ticks, direction_key="cnfBias"):
    """Splits rows on gate's True/False value (rows where the gate is absent/
    None are excluded from BOTH buckets, not folded into False — a gate that
    was never evaluated said nothing, which isn't the same as it failing)."""
    true_edges, false_edges = [], []
    for r in rows:
        g = r.get(gate)
        if g is None:
            continue
        e = signed_edge(r, h, direction_key)
        if e is None:
            continue
        e = cost_adjusted(e, cost_ticks)
        (true_edges if g else false_edges).append(e)

    def summarize(xs):
        if not xs:
            return None
        return {"n": len(xs), "edge": sum(xs) / len(xs),
                "hit_rate": sum(1 for x in xs if x > 0) / len(xs)}

    t, f = summarize(true_edges), summarize(false_edges)
    return t, f


def fold_stability(rows, gate, h, cost_ticks, n_folds, direction_key="cnfBias"):
    """Per-fold True-edge minus False-edge, under the same purged walk-forward
    split as the other two scripts. horizon is passed through as the embargo
    width even though nothing here is "trained" in the fitting sense — the
    embargo still matters: a fold boundary that splits a row's own forward-
    looking label window would let that row's outcome leak across the
    boundary between two folds' bucket assignments."""
    diffs = []
    for train_rows, test_rows in purged_walk_forward_splits(rows, h, n_folds=n_folds):
        t, f = bucket_stats(test_rows, gate, h, cost_ticks, direction_key)
        if t is not None and f is not None:
            diffs.append(t["edge"] - f["edge"])
    return diffs


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("folder")
    ap.add_argument("--bartype", default=None)
    ap.add_argument("--horizon", type=int, default=20)
    ap.add_argument("--min-n", type=int, default=30,
                     help="a gate's True or False bucket below this count is flagged thin, not hidden")
    ap.add_argument("--tick-only", action="store_true")
    ap.add_argument("--after", default=None)
    ap.add_argument("--instance", default=None)
    ap.add_argument("--require-lane-ok", action="store_true")
    ap.add_argument("--cv-folds", type=int, default=5)
    ap.add_argument("--cost-ticks", type=float, default=0.0)
    ap.add_argument("--direction-key", default="cnfBias",
                     help="which field carries Confluence's own directional read (default cnfBias)")
    args = ap.parse_args()

    rows, n_files = load_rows(args.folder, bartype=args.bartype, after=args.after,
                               instance=args.instance, require_lane_ok=args.require_lane_ok)
    if not rows:
        print("no rows found in %s" % args.folder)
        sys.exit(1)

    print("=" * 76)
    print("CONFLUENCE GATE AUDIT")
    print("=" * 76)
    print("files          %d" % n_files)
    print("rows loaded    %d" % len(rows))

    if args.tick_only:
        before = len(rows)
        rows = [r for r in rows if r.get("prsTick") is True]
        print("--tick-only    %d rows (dropped %d)" % (len(rows), before - len(rows)))

    directional = [r for r in rows if (r.get(args.direction_key) or 0) != 0]
    print("with %s != 0   %d" % (args.direction_key, len(directional)))
    if len(directional) < args.min_n:
        print("\nfewer than --min-n (%d) directional rows -- not enough to audit gates. "
              "Skipped." % args.min_n)
        sys.exit(1)

    base_t, base_f = None, None
    base_edges = [cost_adjusted(e, args.cost_ticks) for r in directional
                  if (e := signed_edge(r, args.horizon, args.direction_key)) is not None]
    if base_edges:
        print("\nBASE RATE (all directional rows, no gate filter): edge %+.2f ticks   n=%d"
              % (sum(base_edges) / len(base_edges), len(base_edges)))
        print("A gate only means something if its True-bucket edge differs from THIS,")
        print("not from zero.")

    results = []
    for gate in GATE_FIELDS:
        t, f = bucket_stats(directional, gate, args.horizon, args.cost_ticks, args.direction_key)
        if t is None and f is None:
            results.append({"gate": gate, "present": False})
            continue
        diffs = fold_stability(directional, gate, args.horizon, args.cost_ticks,
                                args.cv_folds, args.direction_key)
        agree = sum(1 for d in diffs if d > 0)
        results.append({
            "gate": gate, "present": True, "true": t, "false": f,
            "diff": (t["edge"] - f["edge"]) if (t and f) else None,
            "fold_diffs": diffs, "fold_agree": agree, "fold_n": len(diffs),
        })

    present = [r for r in results if r["present"] and r["diff"] is not None]
    absent = [r for r in results if not r["present"]]
    thin = [r for r in results if r["present"] and r["diff"] is None]

    print("\n" + "-" * 76)
    print("GATE VALUE  (True-bucket edge minus False-bucket edge, ticks -- positive means")
    print("the gate's own True/False split points the right way; negative means the gate")
    print("is, on this data, filtering AGAINST better outcomes)")
    print("-" * 76)
    print("%-18s %9s %9s %9s %8s %10s" % ("gate", "true_edge", "false_edge", "diff", "fold", "true_n/false_n"))
    for r in sorted(present, key=lambda x: -(x["diff"] or -999)):
        t, f = r["true"], r["false"]
        thin_flag = ""
        if (t and t["n"] < args.min_n) or (f and f["n"] < args.min_n):
            thin_flag = "  (thin)"
        fold_txt = "%d/%d" % (r["fold_agree"], r["fold_n"]) if r["fold_n"] else "n/a"
        print("%-18s %9.2f %9.2f %+9.2f %8s %6d/%-6d%s"
              % (r["gate"], t["edge"] if t else float("nan"), f["edge"] if f else float("nan"),
                 r["diff"], fold_txt, t["n"] if t else 0, f["n"] if f else 0, thin_flag))

    if thin:
        print("\nGates with data on only one side (True rows OR False rows, never both --")
        print("can't compute a difference; often means the gate is effectively always-on")
        print("or always-off on this data, not that anything is broken):")
        for r in thin:
            side = "True" if r["true"] else "False"
            n = (r["true"] or r["false"])["n"]
            print("  %-18s all %d rows read %s" % (r["gate"], n, side))

    if absent:
        print("\nGates with NO data at all in this recording (missing from every row --")
        print("either never evaluated, or older recordings that predate this gate):")
        for r in absent:
            print("  %s" % r["gate"])

    strong = [r for r in present if r["diff"] is not None and r["diff"] > 0
              and r["fold_n"] > 0 and r["fold_agree"] / r["fold_n"] >= 0.6]
    weak = [r for r in present if r["diff"] is not None and r["diff"] <= 0]
    print("\n" + "=" * 76)
    print("SUMMARY")
    print("=" * 76)
    if strong:
        print("Gates showing a positive edge-gap AND agreeing in sign across >=60%% of")
        print("folds (genuinely earning their place, not just a lucky aggregate):")
        for r in sorted(strong, key=lambda x: -x["diff"]):
            print("  %-18s +%.2f ticks, %d/%d folds agree" % (r["gate"], r["diff"], r["fold_agree"], r["fold_n"]))
    else:
        print("No gate cleared both bars (positive diff AND >=60%% fold agreement) on this data.")
    if weak:
        print("\nGates with a zero-or-negative edge-gap -- on THIS data, requiring these adds")
        print("friction (fewer fires) without a shown benefit. Not a recommendation to drop")
        print("them -- a gate can exist for reasons this script can't see (a rare-but-severe")
        print("failure mode it guards against, for instance) -- just what the numbers show:")
        for r in sorted(weak, key=lambda x: x["diff"]):
            print("  %-18s %+.2f ticks" % (r["gate"], r["diff"]))
    print("\nSame caveats as the other two scripts: correlated bar-to-bar rows, no")
    print("significance testing (a p-value here would be confidently wrong, not")
    print("helpfully uncertain), and more data will move every one of these numbers.")


if __name__ == "__main__":
    main()
