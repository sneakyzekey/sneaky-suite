"""
sneaky_ml_common.py — shared loading/splitting utilities for the two training
scripts (train_council_weights.py, train_predictor.py). Not meant to be run
directly.

Mirrors grade_sneaky.py's own load() function so both tools read the exact
same Recorder JSONL format the same way.
"""
import json, glob, os
from datetime import datetime

def lane_ok(r):
    """True if this row's "scope" field shows its seams were actually laned
    to ITS OWN instance when they were consulted — False if it was recorded
    during a lane-registration gap (see AssertLane, SneakyCouncil/Confluence,
    2026-08-04) and its sensor fields (trendDir, lvlSide, mtfBias, ...) may
    reflect a DIFFERENT chart's published state rather than this one's, even
    though cnlInstance itself is always correct either way. Returns True
    (benefit of the doubt) for a row with no "scope" field at all — those
    predate this field being added to Recorder and can't be checked; treat
    them as you did before this existed, not as flagged-bad."""
    scope = r.get("scope")
    if not scope:
        return True
    inst = r.get("cnlInstance")
    if not inst:
        return True   # nothing to check against — not this function's call to make
    return scope.endswith("@" + inst)


def load_rows(folder, bartype=None, after=None, instance=None, require_lane_ok=False,
              require_complete=True):
    """after, if given, is an ISO 8601 timestamp string (or a date-only
    prefix like '2026-08-04') — rows are kept only if their own "time" field
    is >= after. ISO 8601 sorts correctly as a plain string, so this is a
    string comparison, not date parsing. A row with no "time" field is
    dropped when --after is set (can't tell which side of the cutoff it's
    on). instance, if given, keeps only rows whose cnlInstance matches —
    for separating multiple Council instances that collide on one seam.
    require_lane_ok, if True, drops rows recorded during a lane-registration
    gap (see lane_ok() above) — needs Recorder's "scope" field, so it's a
    no-op (keeps everything) on older recordings that predate it.
    require_complete, if False (2026-09-05), keeps rows whose outcome never
    resolved ("complete": false) instead of dropping them — for a caller
    whose diagnostic doesn't depend on the outcome at all (e.g. a signal-time
    conviction/veto check), the default drop would silently hide every recent,
    still-open bar, which is exactly the data a "why isn't it trading lately"
    tool needs to see. Every trainer keeps the default (True): labels DO
    depend on resolution there, so dropping unresolved rows is correct."""
    files = glob.glob(os.path.join(folder, "**", "*.jsonl"), recursive=True)
    # 2026-08-15 — skip "_archive" (see the matching fix in grade_sneaky.py's own load() —
    # kept consistent between the two, same as this module's docstring already promises).
    files = [f for f in files if "_archive" not in os.path.normpath(f).split(os.sep)]
    # 2026-09-05 — also skip the ExcursionRecorder "ticks" sidecar folder, matching the
    # equivalent fix already in grade_sneaky.py's own load(): those are per-fire tick paths
    # (kind:"council_tickpath"), not the row corpus this function is meant to read, and
    # reading + silently discarding them here wastes real I/O/memory on an actively-recording
    # chart for rows never used by any of this module's three callers.
    files = [f for f in files if "ticks" not in os.path.normpath(f).split(os.sep)]
    rows = []
    dropped_lane = 0
    bad = 0
    bad_by_file = {}
    for f in files:
        file_bad = 0
        with open(f, "r", encoding="utf-8-sig") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    r = json.loads(line)
                except Exception:
                    # 2026-09-05 — this used to be a silent `continue` with zero count and zero
                    # report. grade_sneaky.py's load() was hardened after a real incident (a live
                    # run came back with 36.9M "unparseable" rows with no way to even start
                    # diagnosing it from the tool's own output) — that fix never propagated here,
                    # even though this is the loader all three trainers (train_council_weights.py,
                    # train_predictor.py, train_confluence.py) depend on. Count and report instead,
                    # so a similar incident is visible immediately instead of silently shrinking
                    # the training corpus with no trace.
                    bad += 1
                    file_bad += 1
                    continue
                if bartype and r.get("bartype") != bartype:
                    continue
                if require_complete and not r.get("complete", True):
                    continue
                if after and (r.get("time") or "") < after:
                    continue
                if instance and r.get("cnlInstance") != instance and r.get("cnfInstance") != instance:
                    continue
                if require_lane_ok and not lane_ok(r):
                    dropped_lane += 1
                    continue
                rows.append(r)
        if file_bad:
            bad_by_file[f] = file_bad
    # stashed on the function object for a caller that wants the per-file detail, same
    # pattern grade_sneaky.py's load() already uses for load.bad_by_file/load.bad_samples.
    load_rows.bad_by_file = bad_by_file
    if bad:
        worst = sorted(bad_by_file.items(), key=lambda kv: -kv[1])[:5]
        print("WARNING: %d unparseable row(s) across %d/%d file(s) -- worst offenders: %s" %
              (bad, len(bad_by_file), len(files),
               ", ".join("%s (%d)" % (os.path.basename(p), n) for p, n in worst)))
    if require_lane_ok and dropped_lane:
        print("--require-lane-ok dropped %d row(s) recorded during a lane-registration "
              "gap (sensor fields may have reflected another chart's state)" % dropped_lane)
    return rows, len(files)


def row_time(r):
    """Best-effort timestamp for chronological ordering/splitting. Falls back
    to insertion order (0) if no time field is present — callers should check
    for that and warn, since a bad split defeats the whole point of walk-
    forward validation."""
    t = r.get("time")
    if not t:
        return None
    try:
        return datetime.fromisoformat(t.replace("Z", "+00:00"))
    except Exception:
        return None


def chronological_split(rows, train_frac=0.7):
    """Sorts by time and splits FIRST train_frac as train, REST as test.
    Never shuffles — financial rows are autocorrelated, and a random k-fold
    split leaks future information into training (a bar's neighbors in time
    are highly similar), which would make any reported performance a lie.
    """
    timed = [(row_time(r), r) for r in rows]
    if any(t is None for t, _ in timed):
        n_missing = sum(1 for t, _ in timed if t is None)
        print("WARNING: %d/%d rows have no parseable 'time' field — falling "
              "back to file order for those, which may not be chronological. "
              "Results from this run are less trustworthy than a clean split." %
              (n_missing, len(timed)))
        timed.sort(key=lambda p: (p[0] is None, p[0] or datetime.min))
    else:
        timed.sort(key=lambda p: p[0])
    ordered = [r for _, r in timed]
    cut = int(len(ordered) * train_frac)
    return ordered[:cut], ordered[cut:]


def purged_walk_forward_splits(rows, horizon, n_folds=5, min_train=50, min_test=10):
    """Expanding-window walk-forward with a PURGE/EMBARGO gap between train and
    test, instead of one fixed 70/30 split. Mirrors the real Sentinel Lab's own
    cv module ("purged walk-forward with an embargo — never random k-fold").

    Why this matters beyond a plain chronological split: a training row's
    label looks HORIZON bars into the future. If a training row sits within
    `horizon` bars of the test block's start, its label window overlaps the
    test period — the model gets a peek at outcomes that are, causally,
    "test data." A plain 70/30 split only avoids shuffling; it doesn't purge
    this boundary overlap. This does, by dropping the `horizon` rows
    immediately before each test block from that fold's training set.

    Yields (train_rows, test_rows) once per fold. Fold 1 trains on the
    earliest chunk and tests on the next; each later fold's train set grows
    to include everything up through the embargo before its own test block
    (expanding window, not a fixed-size rolling window).
    """
    ordered = sorted(rows, key=lambda r: row_time(r) or datetime.min)
    n = len(ordered)
    if n == 0:
        return
    fold_size = max(1, n // n_folds)
    for k in range(1, n_folds):
        test_start = k * fold_size
        test_end = (k + 1) * fold_size if k < n_folds - 1 else n
        if test_start >= n:
            break
        test_rows = ordered[test_start:test_end]
        train_end = max(0, test_start - horizon)   # the embargo: drop the
        train_rows = ordered[:train_end]            # last `horizon` rows
        if len(train_rows) < min_train or len(test_rows) < min_test:
            continue
        yield train_rows, test_rows


def uniqueness_weights(rows, horizon):
    """Approximate 'uniqueness' weights for overlapping-in-time labels — the
    financial-ML idea that adjacent rows' forward-looking labels overlap in
    time (this row's horizon window covers much of the NEXT row's window
    too), so they aren't independent observations and shouldn't each count
    as a full, separate data point the way random-sample rows would.

    Recorder doesn't store a bar-duration field, so this ESTIMATES one bar's
    duration as the median gap between consecutive rows' own timestamps —
    an approximation, not exact concurrency counting, but an honest one that
    doesn't require any new Recorder field. Weight = 1 / (count of OTHER
    rows whose estimated label window overlaps this one's). Rows with no
    parseable time get weight 1.0 (can't tell, so don't penalize or boost).
    """
    from bisect import bisect_left, bisect_right
    from datetime import timedelta

    timed = [(row_time(r), r) for r in rows]
    valid = [(t, r) for t, r in timed if t is not None]
    weights = {id(r): 1.0 for t, r in timed if t is None}
    if len(valid) < 3:
        weights.update({id(r): 1.0 for _, r in valid})
        return weights

    valid.sort(key=lambda p: p[0])
    times = [t for t, _ in valid]
    gaps = sorted((times[i + 1] - times[i]).total_seconds() for i in range(len(times) - 1)
                  if (times[i + 1] - times[i]).total_seconds() > 0)
    if not gaps:
        weights.update({id(r): 1.0 for _, r in valid})
        return weights
    median_gap = gaps[len(gaps) // 2]
    window = timedelta(seconds=median_gap * horizon)

    for i, (t, r) in enumerate(valid):
        window_end = t + window
        lo = bisect_left(times, t - window)
        hi = bisect_right(times, window_end)
        count = 0
        for j in range(lo, hi):
            tj = times[j]
            if tj + window > t and tj < window_end:
                count += 1
        weights[id(r)] = 1.0 / max(1, count)
    return weights


def barrier_summary(rows):
    """Summarizes the triple-barrier fields (barrierHit/barrierBars/censored)
    IF the recording has them — older rows, or rows from before the Recorder
    barrier-labeling extension, simply won't have this key at all, and are
    reported as 'not available' rather than silently treated as censored."""
    have, upper, lower, censored, bars = 0, 0, 0, 0, []
    total = len(rows)
    for r in rows:
        if "barrierHit" not in r or r.get("barrierHit") is None:
            continue
        have += 1
        bh = r["barrierHit"]
        if bh > 0:
            upper += 1
            if r.get("barrierBars") is not None:
                bars.append(r["barrierBars"])
        elif bh < 0:
            lower += 1
            if r.get("barrierBars") is not None:
                bars.append(r["barrierBars"])
        if r.get("censored"):
            censored += 1
    return {"total": total, "have": have, "upper": upper, "lower": lower,
            "censored": censored, "bars": bars}


def cost_adjusted(signed_move, cost_ticks):
    """Subtracts a flat round-trip cost (in ticks) from a signed outcome.
    cost_ticks=0 (the default everywhere) reproduces the exact old behavior —
    this never silently changes a number unless the person opts in with
    --cost-ticks. Not a real fee schedule: it's a single flat number you
    supply, meant for a stress test ("does this edge survive a few ticks of
    friction"), not a precise cost model — no fee schedule or slippage curve
    for your specific broker/instrument is built in here."""
    return signed_move - cost_ticks


def fwd_cls(r, h):
    fwd = r.get("fwd") or {}
    hh = fwd.get("h%d" % h) or {}
    return hh.get("cls")
