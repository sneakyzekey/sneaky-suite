#!/usr/bin/env python3
"""
train_council_weights.py — fits Council voter weights from its own recorded
decision vector (cnlVotes: {tag: {dir, w}}), instead of eyeballing them by
hand. Compares the LEARNED weights against Council's CURRENT weights on a
held-out, chronologically-later slice of your own data — never trains and
tests on the same bars.

WHY THIS EXISTS: Council's weights (Weight — Trend 1.0, Weight — ADX 0.6,
etc.) are set manually right now — reasoned, not fit. This closes that loop:
regress each voter's own direction against the actual forward outcome, and
see whether the manual weights are even in the right neighborhood.

VALIDATION METHOD: purged walk-forward with an embargo, by default — several
sequential folds, each with a gap of `horizon` bars purged from training
immediately before its test block (a training row whose label looks
`horizon` bars ahead would otherwise leak into a test block that starts
inside that window). This is the same method the original Sentinel Lab's own
`cv` module uses ("purged walk-forward with an embargo — never random
k-fold"). Pass --legacy-split to fall back to one plain chronological 70/30
split instead, e.g. for a quick comparison against an older run.

WHAT IT DOES NOT DO: this does not change your live Council weights for you.
It prints what it found; you decide whether to update the NinjaScript
properties by hand. There is no auto-deploy step, on purpose — moving from
"a fit exists" to "I trust this fit enough to trade it" is a judgment call
this script cannot make for you.

Usage:
    python train_council_weights.py <recorder-folder> --bartype 4v1x1 --horizon 20
    python train_council_weights.py <recorder-folder> --bartype 4v1x1 --horizon 20 --tick-only
    python train_council_weights.py <recorder-folder> --bartype 4v1x1 --horizon 20 --cv-folds 8
    python train_council_weights.py <recorder-folder> --bartype 4v1x1 --horizon 20 --cost-ticks 2
    python train_council_weights.py <recorder-folder> --bartype 4v1x1 --horizon 20 --legacy-split --train-frac 0.6

Requires: numpy (pip install numpy --break-system-packages, if not already present)
"""
import sys, os, argparse
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sneaky_ml_common import (load_rows, chronological_split, purged_walk_forward_splits,
                               cost_adjusted, uniqueness_weights, barrier_summary, fwd_cls, row_time)

try:
    import numpy as np
except ImportError:
    print("This script needs numpy. Install it with:")
    print("    pip install numpy --break-system-packages")
    sys.exit(1)


def build_matrix(rows, horizon, tags):
    """One row per bar, one column per voter tag. X[i,j] = that voter's
    direction on bar i (0 if it abstained/didn't vote that bar — NOT the
    same as voting neutral, matching the ML spec's own distinction). y[i] =
    the signed forward move at the given horizon."""
    X, y, kept = [], [], []
    for r in rows:
        cls = fwd_cls(r, horizon)
        votes = r.get("cnlVotes")
        if cls is None or votes is None:
            continue
        row = [votes.get(t, {}).get("dir", 0) for t in tags]
        if all(v == 0 for v in row):
            continue  # nobody voted — nothing to fit or test against here
        X.append(row)
        y.append(cls)
        kept.append(r)
    return np.array(X, dtype=float), np.array(y, dtype=float), kept


def fmt_weight(w):
    """Normal 3-decimal format for anything that would show as non-zero at
    that precision; scientific notation for anything smaller, so a real but
    heavily-shrunk coefficient (e.g. from a small/uniqueness-down-weighted
    fold) is visibly distinct from an exact zero instead of both printing as
    '+0.000' — the ambiguity that made fold 1's shrinkage look like a bug
    until the full vector got printed and it turned out to be all near-zero,
    not all exactly zero."""
    return "%+.2e" % w if 0 < abs(w) < 0.0005 else "%+.3f" % w


def fit_ridge_weights(train_rows, horizon, tags, l2, sample_weights=None):
    """Ridge regression, closed form: w = (X'X + l2*I)^-1 X'y — a deliberately
    simple, transparent linear model. No black box: every learned weight is
    directly comparable to the manual weight it might replace. Returns None
    if there isn't enough data in this training slice to fit at all.

    sample_weights, if given, is a dict of id(row) -> weight (e.g. from
    uniqueness_weights()) — applied as ordinary weighted least squares by
    scaling each row of X and y by sqrt(weight) before the normal equations,
    which is numerically equivalent to weighting the loss directly."""
    Xtr, ytr, kept = build_matrix(train_rows, horizon, tags)
    if len(Xtr) < len(tags) + 5:   # not even enough rows to fit len(tags) coefficients sanely
        return None, len(Xtr)
    Xn = Xtr - Xtr.mean(axis=0)
    yn = ytr - ytr.mean()
    if sample_weights is not None:
        sw = np.array([sample_weights.get(id(r), 1.0) for r in kept])
        sw = np.sqrt(np.maximum(sw, 1e-9))
        Xn = Xn * sw[:, None]
        yn = yn * sw
    A = Xn.T @ Xn + l2 * np.eye(len(tags))
    w = np.linalg.solve(A, Xn.T @ yn)
    return w, len(Xtr)


def limit_vs_market(rows, tags, horizon, fill_horizon, limit_ticks):
    """Would a resting LIMIT order, placed limit_ticks BETTER than market at
    the moment of the signal, have actually filled — using the recorded
    MAE/MFE at fill_horizon (a SHORT window right after the signal, not the
    full horizon) to check whether price retraced far enough. This needs no
    new capture: MAE/MFE are already tracked continuously (RunHi/RunLo),
    this just reads them at a short horizon instead of the long one. The
    earlier claim that this needed pre-entry data was wrong — a limit placed
    AT the signal is a forward-looking question, answerable with data
    Recorder already had.

    A LONG signal buys limit_ticks BELOW market: filled if MAE (how far
    price dipped, always <=0) reaches -limit_ticks or further within
    fill_horizon bars. A SHORT signal sells limit_ticks ABOVE market: filled
    if MFE reaches +limit_ticks or further. If never filled within that
    short window, the trade is MISSED entirely — a real cost of chasing a
    better price, reported explicitly, not folded away.

    IDEALIZATION, stated plainly: this assumes the limit fills the instant
    price touches it. Real fills depend on liquidity and queue position at
    that exact price, which isn't modeled — treat "filled" here as
    optimistic, not guaranteed.
    """
    n_dir, n_filled, n_missed = 0, 0, 0
    market_edge_sum, limit_edge_sum = 0.0, 0.0
    market_wins, limit_wins = 0, 0
    for r in rows:
        cls_full = fwd_cls(r, horizon)
        if cls_full is None:
            continue
        b = current_weighted_bias(r, tags)
        if b == 0:
            continue
        n_dir += 1

        market_signed = cls_full if b > 0 else -cls_full
        market_edge_sum += market_signed
        if market_signed > 0:
            market_wins += 1

        fh = (r.get("fwd") or {}).get("h%d" % fill_horizon) or {}
        mae, mfe = fh.get("mae"), fh.get("mfe")
        filled = (mae is not None and mae <= -limit_ticks) if b > 0 else \
                 (mfe is not None and mfe >= limit_ticks)
        if not filled:
            n_missed += 1
            continue
        n_filled += 1
        limit_signed = market_signed + limit_ticks   # entered limit_ticks better, parallel shift
        limit_edge_sum += limit_signed
        if limit_signed > 0:
            limit_wins += 1

    return {
        "n_directional": n_dir, "n_filled": n_filled, "n_missed": n_missed,
        "fill_rate": (n_filled / n_dir) if n_dir else None,
        "market_edge": (market_edge_sum / n_dir) if n_dir else None,
        "market_win": (market_wins / n_dir) if n_dir else None,
        "limit_edge_if_filled": (limit_edge_sum / n_filled) if n_filled else None,
        "limit_win_if_filled": (limit_wins / n_filled) if n_filled else None,
    }


def current_weighted_bias(row, tags):
    votes = row.get("cnlVotes") or {}
    net = sum(votes.get(t, {}).get("dir", 0) * votes.get(t, {}).get("w", 1.0) for t in tags)
    return 1 if net > 0.0001 else (-1 if net < -0.0001 else 0)


def learned_weighted_bias(row, tags, weights):
    votes = row.get("cnlVotes") or {}
    net = sum(votes.get(t, {}).get("dir", 0) * w for t, w in zip(tags, weights))
    return 1 if net > 0.0001 else (-1 if net < -0.0001 else 0)


def score(rows, horizon, bias_fn, cost_ticks=0.0):
    """mean signed edge when bias_fn's sign matches actual direction, and a
    plain win rate — same shape as grade_sneaky.py's own tables, so you can
    read this next to that report. cost_ticks=0 (default) reproduces the
    exact old numbers; pass --cost-ticks to stress-test the edge against a
    flat assumed round-trip cost."""
    n, wins, edge_sum = 0, 0, 0.0
    for r in rows:
        cls = fwd_cls(r, horizon)
        if cls is None:
            continue
        b = bias_fn(r)
        if b == 0:
            continue
        n += 1
        signed = cls if b > 0 else -cls
        signed = cost_adjusted(signed, cost_ticks)
        edge_sum += signed
        if signed > 0:
            wins += 1
    if n == 0:
        return None
    return {"n": n, "mean_edge": edge_sum / n, "win_rate": wins / n}


def standalone_voter_edge(rows, horizon, tags):
    """Each voter's edge ENTIRELY ON ITS OWN — direction vs outcome, nothing
    else in the picture. Answers a different question than the regression
    coefficient does: the coefficient says 'what does this voter add ON TOP
    OF everything else already in the model' (can legitimately go negative
    for a genuinely good voter if its information overlaps heavily with
    other voters — a real statistical effect called suppression, not a sign
    the voter itself is backwards). This says 'is this voter, by itself,
    actually right more than it's wrong.' Comparing the two tells you which
    story a negative/surprising coefficient is: real reversal, or
    redundancy with other voters."""
    out = {}
    for tag in tags:
        n, wins, edge_sum, votes_up, votes_down = 0, 0, 0.0, 0, 0
        for r in rows:
            cls = fwd_cls(r, horizon)
            votes = r.get("cnlVotes")
            if cls is None or votes is None:
                continue
            v = votes.get(tag)
            if v is None or v.get("dir", 0) == 0:
                continue
            d = v["dir"]
            if d > 0:
                votes_up += 1
            else:
                votes_down += 1
            n += 1
            signed = cls if d > 0 else -cls
            edge_sum += signed
            if signed > 0:
                wins += 1
        out[tag] = None if n == 0 else {
            "n": n, "mean_edge": edge_sum / n, "win_rate": wins / n,
            "votes_up": votes_up, "votes_down": votes_down,
        }
    return out


def pairwise_agreement(rows, tags, min_n=30):
    """For every pair of voters, how often do they BOTH cast a real vote on the
    same bar, and among those bars, what fraction agree (same direction) vs
    disagree? This is a DIRECT measurement of redundancy — unlike the
    regression's suppression pattern (a voter's coefficient going negative
    despite positive standalone edge), which only tells you redundancy exists
    SOMEWHERE without saying between which specific voters. A very high
    agreement rate between a specific pair means they're functionally voting
    as one, not two independent opinions."""
    from itertools import combinations
    counts = {}
    for r in rows:
        votes = r.get("cnlVotes")
        if not votes:
            continue
        voted = {t: votes[t].get("dir", 0) for t in tags
                  if t in votes and votes[t].get("dir", 0) != 0}
        for a, b in combinations(sorted(voted.keys()), 2):
            key = (a, b)
            c = counts.setdefault(key, {"both": 0, "agree": 0})
            c["both"] += 1
            if voted[a] == voted[b]:
                c["agree"] += 1

    out = []
    for (a, b), c in counts.items():
        if c["both"] < min_n:
            continue
        out.append((a, b, c["both"], c["agree"] / c["both"]))
    out.sort(key=lambda x: -x[3])
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("folder")
    ap.add_argument("--bartype", required=True,
                     help="Required — mixing bar types corrupts everything below, same reason grade_sneaky.py requires it.")
    ap.add_argument("--after", default=None,
                     help="Only include rows recorded at or after this ISO timestamp (or date prefix, e.g. 2026-08-04). Useful for isolating data recorded after a live config/weight change.")
    ap.add_argument("--instance", default=None,
                     help="Only include rows from Council instances tagged with this InstanceTag. Use this if you're running more than one Council on the same instrument+bar-type and set InstanceTag to tell them apart.")
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
                     help="Fall back to one plain chronological train/test split instead of purged walk-forward. Mainly for comparing against an older run.")
    ap.add_argument("--cost-ticks", type=float, default=0.0,
                     help="Flat round-trip cost in ticks, subtracted from every scored trade. Default 0 (no assumption forced on you) — this is a stress test using a number YOU supply, not a real fee/slippage model for your broker.")
    ap.add_argument("--uniqueness-weights", action="store_true",
                     help="Down-weight rows whose forward-looking label windows overlap in time (adjacent bars aren't independent observations). Off by default — never changes an existing result unless you opt in.")
    ap.add_argument("--limit-ticks", type=float, default=0.0,
                     help="If set (>0), reports whether a resting limit order this many ticks better than market would have filled, using MAE/MFE at --fill-horizon. Off by default.")
    ap.add_argument("--fill-horizon", type=int, default=3,
                     help="The SHORT horizon (bars) to check for a limit fill within — must be one of the horizons you actually recorded via Recorder's HorizonBars property. Default 3.")
    ap.add_argument("--l2", type=float, default=1.0,
                     help="Ridge penalty. Higher = weights pulled harder toward 0 (safer with few voters/rows). Default 1.0.")
    ap.add_argument("--min-n", type=int, default=30,
                     help="Minimum bars two voters must have BOTH voted on before their pairwise agreement rate is trusted. Default 30.")
    ap.add_argument("--stale-days", type=float, default=3.0,
                     help="A discovered voter tag whose MOST RECENT vote in this corpus is older than this "
                          "many days before the corpus's own newest row is flagged STALE and excluded from "
                          "fitting by default (see --include-stale-tags). Catches a renamed/retired voter "
                          "(e.g. an old tag from before a refactor) that a plain 'every key ever seen' scan "
                          "would otherwise treat as still active — a large fitted weight on a handful of old, "
                          "frozen rows is misleading, not a real signal. Default 3.0.")
    ap.add_argument("--include-stale-tags", action="store_true",
                     help="Fit stale tags anyway instead of excluding them (still prints the staleness report).")
    args = ap.parse_args()

    rows, nfiles = load_rows(args.folder, args.bartype, args.after, args.instance,
                              require_lane_ok=args.require_lane_ok)
    if args.tick_only:
        rows = [r for r in rows if r.get("prsTick") is True]
    print("=" * 70)
    print("COUNCIL WEIGHT FITTING")
    print("=" * 70)
    print("files", nfiles, " rows", len(rows), " bartype", args.bartype)

    # discover every voter tag actually seen, so a newly-added voter (like
    # Mtf or Profile) is picked up automatically without editing this script.
    tags = sorted({t for r in rows for t in (r.get("cnlVotes") or {}).keys()})
    if not tags:
        print("\nNo rows have 'cnlVotes' at all. This needs the updated SneakyRecorder")
        print("that captures the per-voter decision vector — re-record after updating it.")
        sys.exit(1)

    # STALE TAG CHECK (2026-08-11) — a tag surviving in cnlVotes purely because a handful of OLD
    # rows still have it (e.g. BSP: found frozen at n=33 across three checks spanning two full days
    # of new recording — a renamed/retired voter tag, not a quiet-but-live one) gets fit a real
    # ridge coefficient from a tiny, non-representative, non-current sample. That coefficient can be
    # large (BSP's was -45.885, the biggest in the whole table) precisely BECAUSE it's underdetermined
    # by so little data, not because the signal is strong — and it can silently poison a downstream
    # weight-application (this is exactly what caused a live [Range] rejection on WeightTrend when
    # a much smaller BSP-adjacent mistake got hand-applied). Flag anything whose last vote predates
    # the corpus's own newest row by more than --stale-days, and exclude it from fitting by default.
    last_seen = {}
    for r in rows:
        rv = r.get("cnlVotes") or {}
        if not rv:
            continue
        t = row_time(r)
        if t is None:
            continue
        for tag in rv.keys():
            if tag not in last_seen or t > last_seen[tag]:
                last_seen[tag] = t
    newest_row = max((t for t in last_seen.values()), default=None)
    stale_tags = []
    if newest_row is not None:
        from datetime import timedelta
        cutoff = newest_row - timedelta(days=args.stale_days)
        stale_tags = sorted(t for t in tags if t in last_seen and last_seen[t] < cutoff)
    if stale_tags:
        verb = "flagged but still fit (--include-stale-tags)" if args.include_stale_tags else "EXCLUDED from fitting"
        print("\nSTALE VOTER TAGS (last vote >%.1f day(s) before this corpus's newest row, %s):"
              % (args.stale_days, verb))
        for t in stale_tags:
            print("  %-6s last voted %s" % (t, last_seen[t].date().isoformat()))
        if not args.include_stale_tags:
            tags = [t for t in tags if t not in stale_tags]

    print("voters found:", ", ".join(tags))

    if args.legacy_split:
        train_rows, test_rows = chronological_split(rows, args.train_frac)
        print("mode: legacy single split   train rows", len(train_rows),
              " test rows (held out, LATER in time)", len(test_rows))
        if len(test_rows) < 30:
            print("\nWARNING: fewer than 30 held-out rows. Any comparison below will be noisy —")
            print("treat it as a rough signal, not a verdict, until you have more recorded data.")
        folds = [(train_rows, test_rows)]
    else:
        folds = list(purged_walk_forward_splits(rows, args.horizon, args.cv_folds))
        print("mode: purged walk-forward, %d fold(s), embargo=%d bars (the horizon)" %
              (len(folds), args.horizon))
        if not folds:
            print("\nNot enough data for even one fold at this --cv-folds count. Try a smaller")
            print("--cv-folds, or --legacy-split for a single plain split instead.")
            sys.exit(1)
        if len(folds) < args.cv_folds - 1:
            print("WARNING: some folds were dropped for having too few rows — treat results")
            print("as more exploratory than a full %d-fold run would give you." % args.cv_folds)

    print("\n" + "-" * 70)
    print("STANDALONE VOTER EDGE  (each voter completely on its own — no regression,")
    print("no other voters in the picture. Computed on ALL rows, train+test combined,")
    print("since a simple average doesn't carry the same overfit risk a multivariate")
    print("fit does — more data just makes this number more reliable.)")
    print("-" * 70)
    standalone = standalone_voter_edge(rows, args.horizon, tags)
    for tag in sorted(tags, key=lambda t: -abs((standalone[t] or {}).get("mean_edge", 0))):
        s = standalone[tag]
        if s is None:
            print("  %-14s never voted in this data" % tag)
        else:
            print("  %-14s n=%-6d mean edge %+7.2f  win %5.1f%%   (voted up %d / down %d)" %
                  (tag, s["n"], s["mean_edge"], s["win_rate"] * 100, s["votes_up"], s["votes_down"]))
    print("\n  Read this next to LEARNED WEIGHTS below. If a voter's standalone edge is")
    print("  POSITIVE here but its regression weight is NEGATIVE, that's redundancy with")
    print("  other correlated voters (suppression) — not evidence the voter itself is")
    print("  backwards. If standalone edge is ALSO negative, that's a more real finding.")

    print("\n" + "-" * 70)
    print("PAIRWISE VOTER AGREEMENT  (direct measurement, not inferred from the regression)")
    print("-" * 70)
    pairs = pairwise_agreement(rows, tags, args.min_n)
    if not pairs:
        print("  no voter pairs with >= %d bars where both cast a real vote" % args.min_n)
    else:
        print("  %-9s %-9s %8s %8s" % ("voter A", "voter B", "n", "agree%"))
        for a, b, n, rate in pairs[:20]:
            flag = "  <- functionally redundant" if rate >= 0.85 else ""
            print("  %-9s %-9s %8d %7.1f%%%s" % (a, b, n, rate * 100, flag))
        if len(pairs) > 20:
            print("  ... %d more pairs below --min-n cutoff or omitted for length" % (len(pairs) - 20))
    print("\n  This is a DIRECT measurement: of the bars where BOTH voters actually cast a")
    print("  vote (abstains excluded), how often did they vote the SAME direction? A pair")
    print("  at or above ~85% is functionally one vote wearing two names — Council is")
    print("  giving it roughly double the weight of a genuinely independent opinion.")

    print("\n" + "-" * 70)
    print("BARRIER LABELS  (triple-barrier: did price ever move BarrierTicks from entry,")
    print("                 and which direction, before the horizon ran out?)")
    print("-" * 70)
    bs = barrier_summary(rows)
    if bs["have"] == 0:
        print("  Not available — these rows predate the barrier-labeling Recorder update.")
        print("  Re-record after updating SneakyRecorder to get barrierHit/censored data.")
    else:
        pct_of_total = 100.0 * bs["have"] / bs["total"] if bs["total"] else 0.0
        print("  %d / %d rows have barrier data (%.1f%%)" % (bs["have"], bs["total"], pct_of_total))
        print("  upper barrier hit     %6d  (%.1f%% of rows with barrier data)" %
              (bs["upper"], 100.0 * bs["upper"] / bs["have"]))
        print("  lower barrier hit     %6d  (%.1f%% of rows with barrier data)" %
              (bs["lower"], 100.0 * bs["lower"] / bs["have"]))
        print("  censored (neither hit) %5d  (%.1f%% of rows with barrier data)" %
              (bs["censored"], 100.0 * bs["censored"] / bs["have"]))
        if bs["bars"]:
            bars_sorted = sorted(bs["bars"])
            median_bars = bars_sorted[len(bars_sorted) // 2]
            print("  median bars-to-barrier (when one was hit): %d" % median_bars)
        print("\n  A high censored rate means BarrierTicks is set wide relative to how far")
        print("  price actually moves before your horizon runs out — consider narrowing it")
        print("  in Recorder's properties if you want more rows to resolve one way or the other.")

    if args.limit_ticks > 0:
        print("\n" + "-" * 70)
        print("LIMIT VS MARKET  (%.1f ticks better, checked within %d bars)" %
              (args.limit_ticks, args.fill_horizon))
        print("-" * 70)
        sample = next((r for r in rows if (r.get("fwd") or {}).get("h%d" % args.fill_horizon) is not None), None)
        if sample is None:
            print("  No row has fwd.h%d — --fill-horizon must be a horizon you actually" % args.fill_horizon)
            print("  recorded (check Recorder's HorizonBars property). Nothing to report.")
        else:
            lvm = limit_vs_market(rows, tags, args.horizon, args.fill_horizon, args.limit_ticks)
            print("  directional signals            n=%d" % lvm["n_directional"])
            print("  would have FILLED               %d  (%.1f%%)" %
                  (lvm["n_filled"], 100.0 * (lvm["fill_rate"] or 0)))
            print("  would have been MISSED entirely %d  (%.1f%%)  <- a real cost, not a footnote" %
                  (lvm["n_missed"], 100.0 * (1 - (lvm["fill_rate"] or 0))))
            print("\n  %-32s edge %+7.2f   win %5.1f%%   n=%d" %
                  ("market order, every signal", lvm["market_edge"] or 0, 100 * (lvm["market_win"] or 0), lvm["n_directional"]))
            if lvm["n_filled"]:
                print("  %-32s edge %+7.2f   win %5.1f%%   n=%d" %
                      ("limit order, WHEN FILLED", lvm["limit_edge_if_filled"], 100 * lvm["limit_win_if_filled"], lvm["n_filled"]))
            else:
                print("  limit order, WHEN FILLED        never filled at this offset/window — 0 rows")
            print("\n  IDEALIZED FILL: this assumes the limit fills the instant price touches it —")
            print("  real fills depend on liquidity/queue position at that price, not modeled here.")
            print("  Read the two edges TOGETHER with the miss rate above, not the 'when filled'")
            print("  edge alone — a better price on the trades you catch is not free if you're")
            print("  missing a large share of the trades entirely.")

    # ── fit + score EACH fold independently, then pool every fold's held-out
    # rows for the final comparison. Pooling (rather than averaging small
    # per-fold numbers) is more statistically stable; printing each fold's
    # own numbers too makes fold-to-fold instability visible instead of
    # hidden inside an average. ──
    if args.cost_ticks:
        print("\nCOST MODEL: subtracting a flat %.2f ticks round-trip from every scored trade" % args.cost_ticks)
        print("below. This is a number YOU supplied, not a real fee/slippage schedule for your")
        print("broker or instrument — treat it as a stress test, not a precise cost estimate.")

    if args.uniqueness_weights:
        print("\nUNIQUENESS WEIGHTING ON — rows with heavily overlapping label windows count")
        print("for less than a fully independent observation each in the fit below.")

    pooled_current_rows, pooled_learned_bias = [], {}
    last_w_learned, last_train_n = None, 0
    fold_results = []

    for fi, (train_rows, test_rows) in enumerate(folds, start=1):
        sw = uniqueness_weights(train_rows, args.horizon) if args.uniqueness_weights else None
        w_learned, train_n = fit_ridge_weights(train_rows, args.horizon, tags, args.l2, sw)
        if w_learned is None:
            print("\nFold %d: only %d usable training rows, not enough to fit %d voters — skipped." %
                  (fi, train_n, len(tags)))
            continue
        last_w_learned, last_train_n = w_learned, train_n
        weights_map = dict(zip(tags, w_learned))
        weights_ordered = [weights_map[t] for t in tags]

        cur = score(test_rows, args.horizon, lambda r: current_weighted_bias(r, tags), args.cost_ticks)
        lrn = score(test_rows, args.horizon, lambda r: learned_weighted_bias(r, tags, weights_ordered), args.cost_ticks)
        fold_results.append((fi, train_n, len(test_rows), cur, lrn, w_learned))
        pooled_current_rows.extend(test_rows)
        for r in test_rows:
            pooled_learned_bias[id(r)] = learned_weighted_bias(r, tags, weights_ordered)

    if not fold_results:
        print("\nNo fold produced a usable fit. Nothing further to report — you likely need")
        print("more recorded data, or a smaller --cv-folds count.")
        return

    print("\n" + "-" * 70)
    print("LEARNED WEIGHTS  (from the LAST fold — trained on the most data, %d rows,"
          " ridge L2=%.2f)" % (last_train_n, args.l2))
    print("-" * 70)
    order = np.argsort(-np.abs(last_w_learned))
    for i in order:
        print("  %-14s %s" % (tags[i], fmt_weight(last_w_learned[i])))

    if len(fold_results) > 1:
        print("\n" + "-" * 70)
        print("PER-FOLD LEARNED WEIGHTS  (2026-08-05 — added to diagnose folds that show")
        print("'no directional rows' below: is the fit shrinking to near-zero on that")
        print("fold's smaller/earlier training slice, or something else? max|weight| tells")
        print("you which — a tiny max means real shrinkage, a normal-sized max with a zero")
        print("score means the vote sum is genuinely cancelling out across that fold's rows.)")
        print("-" * 70)
        for fi, train_n, test_n, cur, lrn, w in fold_results:
            i = int(np.argmax(np.abs(w)))
            dark = "  <- scored 'no directional rows' below" if lrn is None else ""
            print("  fold %d (train=%-5d)  max|weight| %10s (%s)%s" % (fi, train_n, fmt_weight(w[i]), tags[i], dark))
        dark_folds = [(fi, train_n, w) for fi, train_n, _, _, lrn, w in fold_results if lrn is None]
        if dark_folds:
            print("\n  Full weight vector for each dark fold above:")
            for fi, train_n, w in dark_folds:
                print("\n  fold %d (train=%d rows):" % (fi, train_n))
                order_d = np.argsort(-np.abs(w))
                for i in order_d:
                    print("    %-14s %s" % (tags[i], fmt_weight(w[i])))

        print("\n" + "-" * 70)
        print("PER-FOLD OUT-OF-SAMPLE EDGE  (watch for consistency, not just the average)")
        print("-" * 70)
        for fi, train_n, test_n, cur, lrn, w in fold_results:
            cur_s = "n=%-4d edge %+6.2f win %4.1f%%" % (cur["n"], cur["mean_edge"], cur["win_rate"]*100) if cur else "no directional rows"
            lrn_s = "n=%-4d edge %+6.2f win %4.1f%%" % (lrn["n"], lrn["mean_edge"], lrn["win_rate"]*100) if lrn else "no directional rows"
            print("  fold %d (train=%-5d test=%-4d)  current: %-32s  learned: %s" %
                  (fi, train_n, test_n, cur_s, lrn_s))
        print("\n  If learned beats current in every fold, that's real, stable signal. If it")
        print("  only wins in one or two folds, that fold is probably an outlier window, not")
        print("  a general result — don't trust the pooled number below as much in that case.")

    # pooled comparison across every fold's held-out rows
    pooled_current = score(pooled_current_rows, args.horizon, lambda r: current_weighted_bias(r, tags), args.cost_ticks)
    n_p, wins_p, edge_p = 0, 0, 0.0
    for r in pooled_current_rows:
        cls = fwd_cls(r, args.horizon)
        b = pooled_learned_bias.get(id(r), 0)
        if cls is None or b == 0:
            continue
        signed = cost_adjusted(cls if b > 0 else -cls, args.cost_ticks)
        n_p += 1; edge_p += signed
        if signed > 0: wins_p += 1
    pooled_learned = None if n_p == 0 else {"n": n_p, "mean_edge": edge_p / n_p, "win_rate": wins_p / n_p}

    print("\n" + "=" * 70)
    print("POOLED OUT-OF-SAMPLE COMPARISON  (every fold's held-out rows combined)")
    print("=" * 70)
    for label, res in (("Council's CURRENT weights", pooled_current), ("LEARNED weights", pooled_learned)):
        if res is None:
            print("  %-28s no directional rows to score" % label)
        else:
            print("  %-28s n=%-5d  mean edge %+.2f  win %.1f%%" %
                  (label, res["n"], res["mean_edge"], res["win_rate"] * 100))

    if pooled_current and pooled_learned:
        if pooled_learned["mean_edge"] > pooled_current["mean_edge"]:
            print("\nLearned weights beat current weights, pooled across every fold's held-out data.")
            print("That's a real signal worth investigating further — not a reason to")
            print("switch immediately. Re-run on a longer/newer recording period before")
            print("touching your live weights, and expect this gap to narrow with more data.")
        else:
            print("\nCurrent (manual) weights held up better, or about the same, pooled across")
            print("every fold's held-out data. That's a genuinely useful result too — it means")
            print("your reasoned weights aren't leaving obvious edge on the table.")

    print("\nNOTE: this compares SIGN-based edge only (does the weighted vote's direction")
    print("agree with the outcome), the same way grade_sneaky.py's own tables do.")
    if not args.cost_ticks:
        print("No transaction cost is assumed (--cost-ticks 0, the default). Pass e.g.")
        print("--cost-ticks 2 to see whether the edge survives a flat assumed round-trip cost.")


if __name__ == "__main__":
    main()
