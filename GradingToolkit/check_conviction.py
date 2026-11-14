#!/usr/bin/env python3
"""
check_conviction.py — diagnoses WHY a Council instance isn't taking trades,
using only what SneakyRecorder already captures per bar: cnlConv, cnlSize,
cnlVetoed, cnlVeto, cnlNet. No live chart monitoring needed — this reads
whatever's already been recorded, so it works fine even if you were away
(at work, asleep, whatever) while the chart was running.

Answers three things directly:
  1. Of all bars, how many were vetoed vs not, and by which veto reason?
  2. On the NON-vetoed bars specifically, what did conviction actually look
     like? This is the direct test of the "Learned's conviction is
     structurally landing below the floor" hypothesis — if most non-vetoed
     bars show a real, nonzero conviction % that's just under the floor,
     that confirms it. If conviction is basically always ~0 even when not
     vetoed, something else is going on (net score never becoming
     meaningfully nonzero at all, not just "below floor").
  3. How many bars actually had size > 0 (i.e., were actionable)?

Usage:
    python check_conviction.py <recorder-folder> --instance E
    python check_conviction.py <recorder-folder> --instance E --after 2026-08-11
    python check_conviction.py <recorder-folder> --instance E --floor 0.35
"""
import sys, os, argparse
from collections import Counter

from sneaky_ml_common import load_rows as _load_rows_common


def load_rows(folder, instance=None, after=None, bartype=None):
    """Thin wrapper around sneaky_ml_common.load_rows (the shared, hardened loader --
    counts/reports bad JSON, skips _archive/ticks folders) that then applies this tool's
    own extra filter: only rows that actually carry Council data at all (cnlConv/cnlVetoed),
    since a Confluence-only chart's rows have neither and would otherwise pad every count
    in this diagnostic with rows it can say nothing about.

    2026-09-05 -- this used to be a THIRD, independently-drifted reimplementation of the
    same JSONL-scanning loop (see grade_sneaky.py's load() and sneaky_ml_common.load_rows())
    -- the weakest of the three: no "_archive" exclusion at all (so a scan against a live
    Recorder tree with retired/old-schema instance files sitting in _archive would silently
    mix stale data into this "live" diagnostic with zero warning), and it silently dropped
    bad JSON with no count, same gap the other two had before their own fixes. Delegating to
    the shared loader fixes both at once and keeps all the ntbv tools reading the same
    corpus the same way.

    require_complete=False is passed deliberately: this tool diagnoses conviction/veto at
    SIGNAL time, which doesn't depend on whether the trade's outcome ever resolved. The
    shared loader's default drops "complete": false rows for the trainers, where that's
    correct (a label needs a resolved outcome) -- but here it would silently hide every
    recent, still-open bar, which is exactly the data a "why isn't it trading lately" tool
    needs to see.
    """
    rows, nfiles = _load_rows_common(folder, bartype=bartype, after=after, instance=instance,
                                      require_complete=False)
    rows = [r for r in rows if "cnlConv" in r or "cnlVetoed" in r]
    return rows, nfiles


def pct(n, d):
    return 100.0 * n / d if d else 0.0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("folder")
    ap.add_argument("--instance", default=None, help="cnlInstance to filter to, e.g. E")
    ap.add_argument("--after", default=None, help="ISO date/time, e.g. 2026-08-11 -- only rows recorded on/after this")
    ap.add_argument("--bartype", default=None)
    ap.add_argument("--floor", type=float, default=0.35, help="ConvictionFloor to test against (default 0.35, Council's own default)")
    args = ap.parse_args()

    rows, nfiles = load_rows(args.folder, args.instance, args.after, args.bartype)
    print("=" * 72)
    print("CONVICTION / VETO DIAGNOSTIC")
    print("=" * 72)
    print("files scanned   %d" % nfiles)
    print("rows with Council data%s   %d" % (
        (" (instance=%s)" % args.instance) if args.instance else "", len(rows)))
    if args.after:
        print("filtered to time >= %s" % args.after)
    if not rows:
        print("\nNo matching rows found. Check --instance / --after / the folder path.")
        sys.exit(1)

    vetoed = [r for r in rows if r.get("cnlVetoed")]
    not_vetoed = [r for r in rows if not r.get("cnlVetoed")]

    print("\n" + "-" * 72)
    print("1. VETO BREAKDOWN")
    print("-" * 72)
    print("  vetoed        %6d  (%.1f%%)" % (len(vetoed), pct(len(vetoed), len(rows))))
    print("  not vetoed    %6d  (%.1f%%)" % (len(not_vetoed), pct(len(not_vetoed), len(rows))))

    reasons = Counter(r.get("cnlVeto") or "(no reason recorded)" for r in vetoed)
    if reasons:
        print("\n  veto reasons, most common first:")
        for reason, n in reasons.most_common(15):
            print("    %-40s %6d  (%.1f%% of vetoed)" % (reason, n, pct(n, len(vetoed))))

    print("\n" + "-" * 72)
    print("2. CONVICTION ON NON-VETOED BARS  (the direct test)")
    print("-" * 72)
    convs = [r.get("cnlConv") for r in not_vetoed if r.get("cnlConv") is not None]
    if not convs:
        print("  No conviction values recorded on non-vetoed bars -- unexpected, check the field name.")
    else:
        convs_sorted = sorted(convs)
        n = len(convs_sorted)
        mean_c = sum(convs_sorted) / n
        below_floor = sum(1 for c in convs_sorted if c < args.floor)
        print("  n = %d non-vetoed bars with a recorded conviction value" % n)
        print("  mean conviction        %.1f%%" % (mean_c * 100))
        print("  min / max              %.1f%% / %.1f%%" % (convs_sorted[0] * 100, convs_sorted[-1] * 100))
        print("  median                 %.1f%%" % (convs_sorted[n // 2] * 100))
        print("  below floor (%.0f%%)      %d / %d  (%.1f%%)" % (
            args.floor * 100, below_floor, n, pct(below_floor, n)))
        print("\n  distribution (10-point buckets):")
        buckets = [0] * 11
        for c in convs_sorted:
            idx = min(10, int(c * 10))
            buckets[idx] += 1
        for i, count in enumerate(buckets):
            lo = i * 10
            hi = lo + 10
            bar = "#" * int(60 * count / n) if n else ""
            print("    %3d-%3d%%  %6d  %s" % (lo, hi, count, bar))

    print("\n" + "-" * 72)
    print("3. ACTUAL ACTIONABLE RATE  (cnlSize > 0 -- what would actually trade)")
    print("-" * 72)
    sizes = [r.get("cnlSize") for r in rows if r.get("cnlSize") is not None]
    actionable = sum(1 for s in sizes if s and s > 0)
    print("  bars with size > 0     %d / %d  (%.1f%%)" % (actionable, len(sizes), pct(actionable, len(sizes))))

    print("\n" + "=" * 72)
    print("READING THIS:")
    print("If most non-vetoed bars show a real, nonzero conviction that's")
    print("clustered just under the floor line above, that confirms the")
    print("floor is the blocker -- the fix is a Learned-specific (lower)")
    print("ConvictionFloor, not a weights change.")
    print("If conviction is landing near 0% even on non-vetoed bars, the")
    print("net score itself isn't building up meaningfully -- a different,")
    print("deeper issue than just the floor threshold.")
    print("=" * 72)


if __name__ == "__main__":
    main()
