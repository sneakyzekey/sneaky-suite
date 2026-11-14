#!/usr/bin/env python3
"""
grade_sneaky.py — read SneakyRecorder JSONL and answer whether any of it works.

    python grade_sneaky.py <file-or-directory> [--horizon 20] [--min-n 30] [--tick-only] [--bartype X]
                            [--excursion-folder <path>]

    --bartype X   keep only rows recorded on bar type X (e.g. 4v1x1). REQUIRED
                  in practice if your Recorder folder mixes bar types: a 20-BAR
                  horizon means a completely different amount of time and price
                  movement on a 1-tick series vs a Drift chart vs a 5-tick series.
                  Mixing them inflates the spread of every table and makes edges
                  look noisier (or occasionally larger) than they really are.

    --tick-only   keep only rows where pressure was classified from REAL TICKS.
                  Proxy rows are price-derived, so leaving them in means the
                  "tape" tables are partly measuring price. Costs sample size.

    --excursion-folder <path>   2026-08-09 -- use ExcursionRecorder's real
                  maxMFE/maxMAE for section 20 (TP/STOP OPTIMIZATION) instead of
                  approximating from Recorder's own forward-window fields. Real
                  tape-fillable prices, full signal life rather than a fixed
                  h-bar window. Point it at your Sneaky\\Excursions folder (or a
                  narrower subfolder/file). Omit it and section 20 falls back to
                  the original approximation, unchanged.

WHAT THIS IS FOR
Every weight and threshold in the Sneaky suite is currently a REASONED GUESS,
including all the ones I argued hardest for. This reads the recorder's output and
produces the stratification tables that turn those guesses into measurements.

WHAT IT DELIBERATELY DOES NOT DO
  · No p-values or significance stars. With a few hundred rows from one
    instrument, correlated bar-to-bar, the independence assumption behind a
    t-test is simply false. A star would look like proof and would not be one.
  · No recommendations. It reports; you decide.
  · No dropping of "bad" rows beyond the documented filters. Selecting rows on
    the outcome is how you manufacture an edge that is not there.

HOW TO READ IT
The only number that matters is EDGE = mean signed forward move, in ticks, for
rows in a bucket. Signed means: a row where the Council said SHORT and price fell
scores POSITIVE. A bucket whose edge is flat across every level of a sensor is
telling you that sensor does not stratify outcomes — which is a real finding.

BASE RATE FIRST. If the all-rows edge is +2 ticks and every bucket is +2, the
sensor adds nothing; the instrument was drifting. Always compare a bucket against
the base rate, never against zero.
"""

import sys, os, json, glob, math
from collections import defaultdict
from datetime import datetime

# ── loading ──────────────────────────────────────────────────────────────────

def load(path, verbose_bad=True, max_samples=8):
    """Returns (rows, files, bad). As a side effect (2026-08-29), also stashes
    load.bad_by_file (dict: filename -> count) and load.bad_samples (list of
    (filename, 1-based line#, exception text, raw line trimmed to 200 chars))
    on the function object, so a caller that wants the detail behind a nonzero
    `bad` count doesn't have to re-read every file itself.

    2026-08-29 — was: silently `bad += 1` on any exception with ZERO record of
    WHICH file, WHERE in it, or WHY — a real run against the live Excursions
    folder came back with 36.9M "unparseable" rows and there was no way to even
    start diagnosing that from the tool's own output. Root cause hypothesis
    (not yet confirmed against real data, see the printed per-file breakdown
    below to check it): SneakyExcursionRecorder's log and tick-path files are
    opened via File.AppendAllText per write with no cross-instance file lock
    (see WriteTickPath/FlushAll in SneakyExcursionRecorder_v2_0_0.cs) — if two
    Recorder instances (e.g. Council Prior + Council Learned, both recording
    the same instrument) ever append to the same path at the same instant, one
    write can land malformed. Because these are single ever-growing append-only
    logs, ONE such collision near the front of a file can misalign every line
    after it, so a handful of real collision EVENTS could plausibly cascade
    into tens of millions of "bad" LINES. The per-file breakdown below is what
    would confirm or kill that theory: a few files each showing bad counts
    that are (a) large and (b) contiguous from some point to EOF looks exactly
    like this cascade; bad counts scattered as occasional singletons across
    many files would point somewhere else entirely.
    """
    files = []
    if os.path.isdir(path):
        files = sorted(glob.glob(os.path.join(path, "**", "*.jsonl"), recursive=True))
        # 2026-08-15 — skip anything under an "_archive" folder (a convention this whole
        # toolkit now uses for old-schema/retired-instance files moved out of the live
        # Recorder path — see archive_old_recorder_files.py). Matched on exact path
        # component, not substring, so a differently-named folder that merely contains
        # "_archive" as part of a longer name is never accidentally excluded.
        files = [f for f in files if "_archive" not in os.path.normpath(f).split(os.sep)]
        # 2026-08-29 — also skip ExcursionRecorder's "ticks" sidecar folder (WriteTickPath in
        # SneakyExcursionRecorder_v2_0_0.cs writes one file per fire there, kind:"council_tickpath",
        # never "excursion"). These lines parse as valid JSON so they never showed up as "bad" —
        # this is a pure waste-avoidance skip, not part of the unparseable-rows fix above — but a
        # single trade's tick path can run into the hundreds of lines, and reading + discarding
        # (by the kind=="excursion" filter, downstream) every one of them for every trade on an
        # actively-recording chart is real, avoidable I/O and memory for rows never used here.
        files = [f for f in files if "ticks" not in os.path.normpath(f).split(os.sep)]
    else:
        files = [path]
    rows, bad = [], 0
    bad_by_file = {}
    bad_samples = []
    for f in files:
        file_bad = 0
        with open(f, "r", encoding="utf-8-sig") as fh:
            for lineno, line in enumerate(fh, start=1):
                line = line.strip()
                if not line:
                    continue
                try:
                    rows.append(json.loads(line))
                    continue
                except Exception as e:
                    err = e
                # 2026-08-29 — best-effort recovery, not a guess: if trimming to the outermost
                # {...} on the line yields VALID json, accept it (handles a stray byte or two
                # stitched on by a torn concurrent write); if it still doesn't parse, count it
                # bad for real. Never invents or repairs content — only ever narrows the slice.
                l0, l1 = line.find("{"), line.rfind("}")
                if l0 != -1 and l1 > l0:
                    try:
                        rows.append(json.loads(line[l0:l1 + 1]))
                        continue
                    except Exception:
                        pass
                bad += 1
                file_bad += 1
                if len(bad_samples) < max_samples:
                    bad_samples.append((f, lineno, str(err), line[:200]))
        if file_bad:
            bad_by_file[f] = file_bad
    load.bad_by_file = bad_by_file
    load.bad_samples = bad_samples
    if verbose_bad and bad:
        print_bad_detail(bad_by_file, bad_samples, len(files))
    return rows, files, bad


def print_bad_detail(bad_by_file, bad_samples, n_files):
    """Shared by load()'s own verbose_bad path and by any caller that ran with
    verbose_bad=False (to control WHERE in its own output the detail lands)
    and wants to print it explicitly afterward via load.bad_by_file/bad_samples."""
    total = sum(bad_by_file.values())
    print("\n  UNPARSEABLE ROWS — %d total across %d/%d files:" % (total, len(bad_by_file), n_files))
    for fn, n in sorted(bad_by_file.items(), key=lambda kv: -kv[1])[:15]:
        print("    %6d bad   %s" % (n, fn))
    if len(bad_by_file) > 15:
        print("    ... and %d more file(s) with at least one bad row" % (len(bad_by_file) - 15))
    print("  sample bad line(s):")
    for fn, lineno, err, snippet in bad_samples:
        print("    %s:%d  %s" % (fn, lineno, err))
        print("      %r" % snippet)


def fwd(row, h, field):
    """Forward outcome at horizon h. None when absent — never 0."""
    f = row.get("fwd") or {}
    b = f.get("h%d" % h)
    if not b:
        return None
    return b.get(field)


def signed_edge(row, h, direction_key="cnlBias"):
    """
    Signed forward close, in ticks, relative to the row's own directional claim.
    Short + price down = positive. Rows with no direction are excluded from edge
    stats entirely rather than counted as zero — 'no opinion' is not 'wrong'.
    direction_key lets the same function score any fuser's own directional field
    (cnlBias for Council, cnfBias for Confluence) rather than assuming Council.
    """
    d = row.get(direction_key)
    c = fwd(row, h, "cls")
    if d is None or c is None or d == 0:
        return None
    return c * (1 if d > 0 else -1)


def mean(xs):
    xs = [x for x in xs if x is not None]
    return sum(xs) / len(xs) if xs else None


def stdev(xs):
    xs = [x for x in xs if x is not None]
    if len(xs) < 2:
        return None
    m = sum(xs) / len(xs)
    return math.sqrt(sum((x - m) ** 2 for x in xs) / (len(xs) - 1))


def fmt(v, nd=2):
    return "—" if v is None else ("%+.*f" % (nd, v))


# ── tables ───────────────────────────────────────────────────────────────────

def table(title, buckets, base, min_n, note=None):
    """buckets: ordered list of (label, [edge values])"""
    print("\n" + title)
    if note:
        print("  " + note)
    print("  %-26s %6s %9s %9s %9s %9s" % ("bucket", "n", "edge", "vs base", "win%", "sd"))
    print("  " + "-" * 72)
    for label, vals in buckets:
        vals = [v for v in vals if v is not None]
        n = len(vals)
        if n == 0:
            print("  %-26s %6d %9s %9s %9s %9s" % (label, 0, "—", "—", "—", "—"))
            continue
        e = mean(vals)
        w = 100.0 * sum(1 for v in vals if v > 0) / n
        sd = stdev(vals)
        rel = (e - base) if (base is not None and e is not None) else None
        thin = "  (thin)" if n < min_n else ""
        print("  %-26s %6d %9s %9s %8.1f%% %9s%s"
              % (label, n, fmt(e), fmt(rel), w, fmt(sd) if sd else "—", thin))


def bucket_by(rows, h, keyfn, order=None, direction_key="cnlBias"):
    out = defaultdict(list)
    for r in rows:
        k = keyfn(r)
        if k is None:
            continue
        e = signed_edge(r, h, direction_key)
        if e is None:
            continue
        out[k].append(e)
    keys = order if order else sorted(out.keys(), key=lambda x: str(x))
    return [(str(k), out.get(k, [])) for k in keys]


def band(v, edges, labels):
    if v is None:
        return None
    for i, e in enumerate(edges):
        if v < e:
            return labels[i]
    return labels[-1]


# 2026-09-05 -- session phase (table 8) only splits open-drive/midday/close, anchored to
# whatever the chart's own TradingHours template calls "session start/end" -- on this
# instrument's data that's an almost-24h Globex-style session opening ~18:00 ET, so a
# clock question like "is 9:30-11am good" can't be answered from that table at all (9:30am
# falls deep inside the single giant "midday" bucket). This buckets directly off the row's
# own wall-clock "time" field instead, same convention check_market_hours_v2.py already
# established: ET_OFFSET_HOURS=0 there because the recording machine's clock is already US
# Eastern, even though "time" carries a literal Z/UTC-style suffix -- confirmed by that
# script's own Sunday-hours sanity check (rows start at 18:00, which is Globex's actual
# Sunday-evening open in Eastern time, not UTC). No timezone conversion is applied here,
# matching that same established convention exactly.
def eastern_half_hour(r):
    """30-minute Eastern wall-clock bucket, labelled by the START of the window
    (e.g. '09:30' covers 09:30:00-09:59:59). None if 'time' is missing/unparseable."""
    t = r.get("time")
    if not t:
        return None
    try:
        dt = datetime.fromisoformat(t.replace("Z", "+00:00"))
    except Exception:
        return None
    half = 0 if dt.minute < 30 else 30
    return "%02d:%02d" % (dt.hour, half)


HALF_HOUR_ORDER = ["%02d:%02d" % (hh, mm) for hh in range(24) for mm in (0, 30)]


# ── TP/stop optimization (2026-08-08) — adapted from the real Sentinel Suite's own
# SentinelExcursions.cs, not a straight port. That tool reads a separate "excursion" JSONL
# schema (written by SignalExcursionRecorder, which isn't part of this build and doesn't
# exist in the source available here) where MFE/MAE are already stored relative to the
# SIGNAL's own direction. Recorder's actual mfe/mae fields are relative to PRICE direction
# (mfe = how far price ran UP from entry, mae = how far it ran DOWN) regardless of which way
# the row's own bias pointed -- so a short's true favorable move is the raw MAE, negated, and
# its true adverse move is the raw MFE, negated. Get this backwards and the optimizer would
# be sizing stops against the wrong side of every short. The core percentile/grid math below
# (pctl, best_tp_stop, tp_stop_grid) mirrors the original's Pctl/BestTpStop/TpStopGrid exactly.
#
# One real adaptation, not a silent change: the original breaks a same-window "both TP and
# stop level were touched" tie using millisecond first-touch timestamps it has and this data
# doesn't. It already defaults to "stop first" (pessimistic) when that timing data is
# unavailable -- this always takes that same pessimistic branch, consistent with the
# original's own fallback rather than inventing new tie-breaking behavior.

def pctl(values, p):
    """Linear-interpolated percentile, matching SentinelExcursions.Pctl exactly."""
    if not values:
        return None
    s = sorted(values)
    if len(s) == 1:
        return s[0]
    idx = (p / 100.0) * (len(s) - 1)
    lo, hi = int(idx), int(idx) + (0 if idx == int(idx) else 1)
    hi = min(hi, len(s) - 1)
    if lo == hi:
        return s[lo]
    return s[lo] + (s[hi] - s[lo]) * (idx - lo)


def true_mfe_mae(row, h, direction_key="cnlBias"):
    """
    Direction-adjusted favorable/adverse excursion in ticks, both returned as
    non-negative magnitudes (favorable, adverse). None if direction or either raw
    value is missing. Recorder's own mfe/mae are price-direction (up/down); this
    flips them onto signal-direction (favorable/adverse) using the row's own bias.
    """
    d = row.get(direction_key)
    raw_mfe = fwd(row, h, "mfe")   # >= 0, how far price ran UP from entry
    raw_mae = fwd(row, h, "mae")   # <= 0, how far price ran DOWN from entry
    if d is None or d == 0 or raw_mfe is None or raw_mae is None:
        return None
    if d > 0:
        favorable, adverse = raw_mfe, -raw_mae
    else:
        favorable, adverse = -raw_mae, raw_mfe
    return (favorable, adverse)


def excursion_favorable_adverse(row):
    """
    2026-08-09 — the REAL counterpart to true_mfe_mae() above, reading
    ExcursionRecorder's own maxMFE/maxMAE directly instead of approximating from
    Recorder's forward-window fields. No direction flip needed here: unlike
    Recorder's mfe/mae (price-direction, up/down), ExcursionRecorder's maxMFE/
    maxMAE are ALREADY favorable/adverse by definition (that's what MFE/MAE mean
    in standard trading usage) -- confirmed against the real source
    (SneakyExcursionRecorder_v2_0_0.cs's ToJson). Also measured from a real,
    fillable tape price (schema 1.5+; see that file's own v2.2.0 comment), not a
    bar close -- strictly more accurate than the approximation, not just a
    different source.

    IMPORTANT DIFFERENCE FROM true_mfe_mae(): this is the excursion over the
    signal's FULL LIFE (until it naturally concluded -- target/stop/timeout/
    session end), not a fixed "h bars forward" window. Excursion rows don't
    carry a bar-indexed forward window at all; their own milestone fields
    (mfe1/mfe5/mfe15/mfe60) are MINUTE-indexed, not bar-indexed, so they don't
    line up with --horizon either. Presented as its own thing, not forced into
    a false correspondence with the rest of this report's h-bar framing.
    """
    fav = row.get("maxMFE")
    adv = row.get("maxMAE")
    if fav is None or adv is None:
        return None
    return (float(fav), float(adv))


def best_tp_stop(pts, tp_pcts=(33, 50, 67), sl_fracs=(0.5, 0.75, 1.0, 1.5)):
    """
    pts: list of (favorable, adverse) tick tuples, both non-negative, from true_mfe_mae().
    Grid-searches TP (a percentile of the favorable distribution) x stop (a FRACTION of
    that TP, so R:R is structurally capped) and returns the max-expectancy config.
    Matches SentinelExcursions.BestTpStop's win/loss/scratch/tiebreak logic exactly.
    """
    if not pts or len(pts) < 15:
        return None
    favs = [p[0] for p in pts]
    best = None
    for tp_p in tp_pcts:
        tp = pctl(favs, tp_p)
        if tp is None or tp <= 0:
            continue
        for f in sl_fracs:
            sl = tp * f
            wins = n = 0
            pnl = 0.0
            for fav, adv in pts:
                n += 1
                hit_tp, hit_sl = fav >= tp, adv >= sl
                if hit_tp and not hit_sl:
                    wins += 1; pnl += tp
                elif hit_sl and not hit_tp:
                    pnl -= sl
                elif hit_tp and hit_sl:
                    pnl -= sl   # pessimistic default (stop-first) -- see module note above
                # neither hit -> scratch (0)
            if n == 0:
                continue
            exp = pnl / n
            if best is None or exp > best["exp"]:
                best = {"tp": tp, "stop": sl, "hit_rate": wins / n, "exp": exp, "n": n}
    return best


def tp_stop_grid(pts, tp_pcts=(33, 50, 67), sl_fracs=(0.5, 0.75, 1.0, 1.5)):
    """All 12 TP/stop configs' estimates, not just the best one. Same rules as best_tp_stop."""
    if not pts or len(pts) < 15:
        return []
    favs = [p[0] for p in pts]
    out = []
    for tp_p in tp_pcts:
        tp = pctl(favs, tp_p)
        if tp is None or tp <= 0:
            continue
        for f in sl_fracs:
            sl = tp * f
            wins = n = 0
            pnl = 0.0
            for fav, adv in pts:
                n += 1
                hit_tp, hit_sl = fav >= tp, adv >= sl
                if hit_tp and not hit_sl:
                    wins += 1; pnl += tp
                elif hit_sl and not hit_tp:
                    pnl -= sl
                elif hit_tp and hit_sl:
                    pnl -= sl
            if n == 0:
                continue
            out.append({"tp_pct": tp_p, "sl_frac": f, "tp": tp, "stop": sl,
                        "hit_rate": wins / n, "exp": pnl / n, "n": n})
    return out


# ── main ─────────────────────────────────────────────────────────────────────

def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    path = sys.argv[1]
    h = 20
    min_n = 30
    if "--horizon" in sys.argv:
        h = int(sys.argv[sys.argv.index("--horizon") + 1])
    if "--min-n" in sys.argv:
        min_n = int(sys.argv[sys.argv.index("--min-n") + 1])
    tick_only = "--tick-only" in sys.argv
    bartype = None
    if "--bartype" in sys.argv:
        bartype = sys.argv[sys.argv.index("--bartype") + 1]
    after = None
    if "--after" in sys.argv:
        after = sys.argv[sys.argv.index("--after") + 1]
    instance = None
    if "--instance" in sys.argv:
        instance = sys.argv[sys.argv.index("--instance") + 1]
    excursion_folder = None
    if "--excursion-folder" in sys.argv:
        excursion_folder = sys.argv[sys.argv.index("--excursion-folder") + 1]

    rows, files, bad = load(path, verbose_bad=False)
    if not rows:
        print("no rows found in %s" % path)
        sys.exit(1)

    print("=" * 76)
    print("SNEAKY RECORDER — GRADING REPORT")
    print("=" * 76)
    print("files          %d" % len(files))
    print("rows read      %d%s" % (len(rows), ("  (%d unparseable)" % bad) if bad else ""))
    if bad:
        print_bad_detail(load.bad_by_file, load.bad_samples, len(files))

    from collections import Counter
    bt_counts = Counter(r.get("bartype") for r in rows)
    print("bar types present  " + ", ".join("%s (%d)" % (k, v) for k, v in bt_counts.most_common()))

    if bartype:
        before = len(rows)
        rows = [r for r in rows if r.get("bartype") == bartype]
        print("filtered to '%s'    %d rows (dropped %d from other bar types)"
              % (bartype, len(rows), before - len(rows)))
        if not rows:
            print("\nno rows for bar type '%s'. Available: %s"
                  % (bartype, ", ".join(bt_counts.keys())))
            sys.exit(1)
    elif len(bt_counts) > 1:
        print("\n" + "!" * 76)
        print("MULTIPLE BAR TYPES IN ONE RUN. A 20-BAR horizon means a different amount")
        print("of time and price movement on each of these — mixing them inflates the")
        print("spread of every table below and can hide or fake an edge.")
        print("Re-run with --bartype <name> for each one separately, e.g.:")
        for k in bt_counts:
            print("    --bartype %s" % k)
        print("Continuing with ALL bar types mixed, since none was specified.")
        print("!" * 76)

    if after:
        before = len(rows)
        # ISO 8601 timestamps sort correctly as plain strings, so this needs
        # no date parsing — a row with no "time" field is dropped rather than
        # silently included (can't tell if it's before or after the cutoff).
        rows = [r for r in rows if (r.get("time") or "") >= after]
        print("filtered to time >= '%s'    %d rows (dropped %d from before that point)"
              % (after, len(rows), before - len(rows)))
        if not rows:
            print("\nno rows at or after '%s'." % after)
            sys.exit(1)

    if instance:
        before = len(rows)
        # 2026-08-06 — matches EITHER fuser's tag now, not just Council's. Confluence rows
        # never had their own instance tag until tonight (cnfInstance, mirrors cnlInstance),
        # so a Confluence-only chart (e.g. one with no Council attached) could never be
        # isolated before this — "--instance D" silently returned 0 rows even when D was a
        # real, correctly-tagged chart, because the check only ever looked at cnlInstance.
        # A row matches if EITHER tag equals the requested instance; in practice a given row
        # has at most one of the two set, depending on which fuser(s) are on that chart.
        rows = [r for r in rows if r.get("cnlInstance") == instance or r.get("cnfInstance") == instance]
        print("filtered to instance '%s'    %d rows (dropped %d from other/untagged instances)"
              % (instance, len(rows), before - len(rows)))
        if not rows:
            print("\nno rows tagged instance '%s' on EITHER Council (cnlInstance) or Confluence" % instance)
            print("(cnfInstance). Set 'Instance Tag' on whichever indicator you're isolating and")
            print("record fresh data — rows from before that field existed won't have it either.")
            sys.exit(1)

    complete = [r for r in rows if r.get("complete")]
    incomplete = len(rows) - len(complete)
    print("complete       %d   (%d incomplete, EXCLUDED — their outcome never resolved)"
          % (len(complete), incomplete))

    have_h = [r for r in complete if fwd(r, h, "cls") is not None]
    print("with h%-3d      %d" % (h, len(have_h)))
    if not have_h:
        hs = set()
        for r in complete:
            for k in (r.get("fwd") or {}):
                hs.add(k)
        print("\nno rows carry horizon h%d. available: %s" % (h, ", ".join(sorted(hs)) or "none"))
        sys.exit(1)

    directional = [r for r in have_h if (r.get("cnlBias") or 0) != 0]
    if tick_only:
        before = len(directional)
        directional = [r for r in directional if r.get("prsTick") is True]
        print("tick-only      %d   (dropped %d proxy/unknown rows)"
              % (len(directional), before - len(directional)))
        if not directional:
            print("\nno tick-backed rows. Either pressure never ran, or every bar fell back")
            print("to the OHLC proxy. Re-run without --tick-only.")
            sys.exit(1)
    print("directional    %d   (rows where the Council took a side; the rest cannot be scored)"
          % len(directional))

    if not directional:
        print("\n" + "=" * 76)
        print("NO COUNCIL DATA on this instance — tables 1-12 below are Council-specific and")
        print("will all read 'n=0 —' by design, not by error. If this instance runs Confluence")
        print("only (no Council attached), that's expected — scroll down to the CONFLUENCE")
        print("section for this chart's real data.")
        print("=" * 76)

    if len(directional) < min_n:
        print("\n" + "!" * 76)
        print("ONLY %d DIRECTIONAL ROWS. That is not enough to conclude anything." % len(directional))
        print("Everything below is printed so you can see the shape of the output,")
        print("NOT so you can act on it. Come back with a few hundred.")
        print("!" * 76)

    edges = [signed_edge(r, h) for r in directional]
    base = mean(edges)
    print("\nBASE RATE (all directional rows, h%d)" % h)
    print("  n %d   mean signed move %s ticks   win %.1f%%   sd %s"
          % (len(edges), fmt(base),
             100.0 * sum(1 for e in edges if e and e > 0) / max(1, len(edges)),
             fmt(stdev(edges))))
    print("  Every bucket below should be read AGAINST this number, not against zero.")

    # ── 1. the headline question ──
    table("1. COUNCIL CONVICTION — does more conviction mean more edge?",
          bucket_by(directional, h,
                    lambda r: band(r.get("cnlConv"), [0.2, 0.35, 0.5, 0.7],
                                   ["<0.20", "0.20-0.35", "0.35-0.50", "0.50-0.70", ">=0.70"]),
                    ["<0.20", "0.20-0.35", "0.35-0.50", "0.50-0.70", ">=0.70"]),
          base, min_n,
          "If this is flat, conviction is not measuring what it claims to.")

    # ── 2. does size gating help ──
    table("2. SIZE MULT — is the conviction floor cutting good rows or bad ones?",
          bucket_by(directional, h,
                    lambda r: "actionable (size>0)" if (r.get("cnlSize") or 0) > 0 else "gated out (size=0)",
                    ["gated out (size=0)", "actionable (size>0)"]),
          base, min_n)

    # ── 3. breadth ──
    table("3. VOTER COUNT — does a fuller panel vote better?",
          bucket_by(directional, h,
                    lambda r: band(r.get("voters"), [4, 8, 12],
                                   ["<4", "4-7", "8-11", ">=12"]),
                    ["<4", "4-7", "8-11", ">=12"]),
          base, min_n)

    # ── 4. the orthogonality claim, tested ──
    def eye_agree(r):
        e = r.get("eyeDir")
        b = r.get("cnlBias")
        if e is None or not b:
            return None
        if e == 0:
            return "Eye neutral"
        return "Eye AGREES" if e == b else "Eye OPPOSES"
    table("4. EYE vs COUNCIL — is the orthogonal qualifier worth anything?",
          bucket_by(directional, h, eye_agree,
                    ["Eye AGREES", "Eye neutral", "Eye OPPOSES"]),
          base, min_n,
          "This is the whole orthogonality thesis. If agreement does not beat base, it is wrong.")

    for key, lbl in (("prsDir", "PRESSURE"), ("imktLean", "INTERMARKET"), ("cvbBias", "DRIFT CVB"),
                     ("mtfBias", "MTF"), ("lvlSide", "LOCATION VWAP SIDE"), ("profSignal", "PROFILE REVERSION SIGNAL")):
        def agree(r, k=key):
            v = r.get(k)
            b = r.get("cnlBias")
            if v is None or not b:
                return None
            if v == 0:
                return "neutral"
            return "agrees" if v == b else "opposes"
        table("5. %s — does this tape source stratify?" % lbl,
              bucket_by(directional, h, agree, ["agrees", "neutral", "opposes"]),
              base, min_n)

    # ── 6. context axes ──
    table("6. PARTICIPATION — does dry-up deserve its conviction damp?",
          bucket_by(directional, h,
                    lambda r: None if r.get("dryUp") is None else ("dry-up" if r["dryUp"] else "normal"),
                    ["normal", "dry-up"]),
          base, min_n,
          "The Council currently damps conviction x0.75 on dry-up. Does the data support it?")

    # ── 6b. Council's OWN applied modulators (added 2026-08-02) ──
    # dryUp above is the RAW Participation field. These are whether COUNCIL ITSELF actually
    # multiplied conviction on this bar — different when e.g. a damp property is set to 1.00
    # (disabled) even though the underlying condition was true. Tests the panel's actual logic,
    # not just the source it reads.
    for key, lbl, note in (
        ("cnlBreadthDamped", "BREADTH", "Panel below MinVoters — thin panel, less trustworthy agreement."),
        ("cnlSqueezeDamped", "SQUEEZE", "VolEnvelope reported a squeeze in progress when this fired."),
        ("cnlDryUpDamped", "DRY-UP (Council's own flag)", "Should track table 6 above closely; a gap between "
         "them means DryUpDamp is set to 1.00 (disabled) or Participation isn't reaching Council."),
        ("cnlInValueDamped", "IN-VALUE (Profile)", "Price accepted inside today's value area when this fired."),
        ("cnlProfOverextDamped", "OVEREXTENSION (Profile)", "Profile's reversion Signal agreed with the panel's "
         "own bias when this fired — price already stretched away from value in the trade's own direction."),
    ):
        rows_k = [r for r in directional if r.get(key) is not None]
        if tick_only:
            rows_k = [r for r in rows_k if r.get("prsTick") is True]
        table("6b. %s — did Council's own damp actually fire, and did it matter?" % lbl,
              bucket_by(rows_k, h,
                        lambda r, k=key: "damped" if r.get(k) else "not damped",
                        ["not damped", "damped"]),
              base, min_n, note)

    table("7. RVOL BAND",
          bucket_by(directional, h,
                    lambda r: band(r.get("rvol"), [0.6, 1.0, 1.5],
                                   ["<0.6", "0.6-1.0", "1.0-1.5", ">=1.5"]),
                    ["<0.6", "0.6-1.0", "1.0-1.5", ">=1.5"]),
          base, min_n)

    table("8. SESSION PHASE — is midday actually worse, or is that folklore?",
          bucket_by(directional, h,
                    lambda r: {0: "0 closed", 1: "1 open-drive", 2: "2 midday", 3: "3 close"}.get(r.get("phase")),
                    ["1 open-drive", "2 midday", "3 close", "0 closed"]),
          base, min_n)

    table("8b. HOUR OF DAY (Eastern, 30-min buckets) — is a specific clock window better/worse?",
          bucket_by(directional, h, eastern_half_hour, HALF_HOUR_ORDER),
          base, min_n,
          "Label = START of its 30-min window, Eastern wall-clock (e.g. '09:30' = "
          "09:30:00-09:59:59). Unlike table 8 above (which is anchored to the chart's own "
          "session open/close and lumps almost everything into one giant 'midday' bucket "
          "on a near-24h session), this reads the row's own 'time' field directly -- so a "
          "specific clock question like 'is 9:30-11am good' has an actual answer here. "
          "Expect many buckets to read (thin) or empty; this is a map, not a verdict.")

    table("9. REGIME",
          bucket_by(directional, h,
                    lambda r: {0: "0 low", 1: "1 normal", 2: "2 high"}.get(r.get("regime")),
                    ["0 low", "1 normal", "2 high"]),
          base, min_n)

    # ── 9b. 2026-08-06 — is conviction actually broken, or just a proxy for regime? ──
    # Added after two instances (recorded independently) both showed table 1 (conviction)
    # reading non-monotonic/inverted AND table 9 (regime) showing "normal" as a severe
    # outlier bucket in both. Those two findings could be the same thing wearing two
    # names: if Council tends to produce higher conviction specifically during normal-
    # regime conditions, and normal-regime conditions have the worst edge, conviction
    # would LOOK backwards in the pooled table 1 even if it behaves sanely once you
    # hold regime fixed. This re-runs table 1's exact conviction bands, once per regime
    # value, so that question has a direct answer instead of a guess.
    for reg_val, reg_label in [(0, "0 low"), (1, "1 normal"), (2, "2 high")]:
        rows_this_regime = [r for r in directional if r.get("regime") == reg_val]
        table("9b. CONVICTION WITHIN REGIME=%s — is conviction just proxying for regime?" % reg_label,
              bucket_by(rows_this_regime, h,
                        lambda r: band(r.get("cnlConv"), [0.2, 0.35, 0.5, 0.7],
                                       ["<0.20", "0.20-0.35", "0.35-0.50", "0.50-0.70", ">=0.70"]),
                        ["<0.20", "0.20-0.35", "0.35-0.50", "0.50-0.70", ">=0.70"]),
              base, min_n,
              "Same bands as table 1, same overall base rate, just pre-filtered to this "
              "regime. If this looks monotonic even where table 1 didn't, regime was the "
              "confound. If it's STILL scrambled within a single regime, conviction has "
              "its own problem independent of regime.")

    table("10. ADX STRONG",
          bucket_by(directional, h,
                    lambda r: None if r.get("adxStrong") is None else ("strong" if r["adxStrong"] else "weak"),
                    ["weak", "strong"]),
          base, min_n)

    # ── 9c. 2026-08-06 — companion to 9b: is REGIME=normal just a THIN-PANEL proxy? ──
    # Table 3 (voter count) already showed thin panels underperform on their own. If
    # normal-regime bars simply tend to have fewer voters attached/agreeing than low/high
    # regime bars do, that alone could explain 9's whole pattern without anything being
    # wrong with "normal regime" as a concept — same logic as 9b, different candidate
    # confound.
    for reg_val, reg_label in [(0, "0 low"), (1, "1 normal"), (2, "2 high")]:
        rows_this_regime = [r for r in directional if r.get("regime") == reg_val]
        table("9c. VOTER COUNT WITHIN REGIME=%s" % reg_label,
              bucket_by(rows_this_regime, h,
                        lambda r: band(r.get("voters"), [4, 8, 12],
                                       ["<4", "4-7", "8-11", ">=12"]),
                        ["<4", "4-7", "8-11", ">=12"]),
              base, min_n,
              "Same bands as table 3. Also watch the n's themselves, not just the edges — "
              "if normal-regime rows cluster in the thin buckets while low/high don't, "
              "that's the mechanism, independent of what any edge number says.")

    # ── 11. the honesty check ──
    tick = [r for r in directional if r.get("prsTick") is True]
    proxy = [r for r in directional if r.get("prsTick") is False]
    print("\n11. TICK-BACKED vs OHLC PROXY  (data quality, not edge)")
    print("  tick-backed rows %d   proxy rows %d" % (len(tick), len(proxy)))
    if proxy:
        print("  Proxy rows are NOT order flow — pressure fell back to a price-derived")
        print("  estimate. If that share is large, every tape-based conclusion above is")
        print("  partly measuring price. Re-run with --tick-only once you have enough.")
    elif tick_only:
        print("  (already filtered to tick-backed rows only)")

    # ── 12a-b. SneakyLocation / SneakyProfile — proximity effects (added 2026-08-02) ──
    lvl_rows = [r for r in directional if r.get("lvlDistAtr") is not None]
    if tick_only:
        lvl_rows = [r for r in lvl_rows if r.get("prsTick") is True]
    table("12a. LOCATION — is proximity to a structural level associated with different edge?",
          bucket_by(lvl_rows, h,
                    lambda r: band(r.get("lvlDistAtr"), [0.25, 0.5, 1.0],
                                   ["<0.25 ATR (very near)", "0.25-0.50 ATR", "0.50-1.00 ATR", ">=1.00 ATR (clear)"]),
                    ["<0.25 ATR (very near)", "0.25-0.50 ATR", "0.50-1.00 ATR", ">=1.00 ATR (clear)"]),
          base, min_n,
          "Tests the premise behind Confluence/Council's Level veto: if 'very near' isn't "
          "worse than 'clear', the veto is filtering out edge, not risk.")

    prof_rows = [r for r in directional if r.get("profNearHvn") is not None]
    if tick_only:
        prof_rows = [r for r in prof_rows if r.get("prsTick") is True]

    def prof_node(r):
        if r.get("profNearHvn"):
            return "near HVN"
        if r.get("profNearLvn"):
            return "near LVN"
        return "neither"
    table("12b. PROFILE — does sitting on a high/low-volume node matter?",
          bucket_by(prof_rows, h, prof_node, ["near HVN", "near LVN", "neither"]),
          base, min_n,
          "HVN = price near the session's highest-volume row (POC). LVN = near the session's "
          "thinnest-traded row (a volume 'air gap'). Neither is directional by itself — this "
          "only tests whether sitting on one of these two structures changes outcomes at all.")

    # ── 13-16. CONFLUENCE — its own gate-by-gate cascade, scored on its own terms ──
    # As of SneakyCore v1.37.0 Confluence publishes its own state (see SNEAKY_HANDOFF.md).
    # This does NOT reuse the Council-anchored `directional` pool above: Confluence and
    # Council are commonly on DIFFERENT charts now (one 1-min chart per fuser), so a file
    # from the Confluence chart may carry near-zero cnlBias coverage and vice versa. Skip
    # this whole section silently if the file has no Confluence data at all.
    cnf_present_rows = [r for r in have_h if r.get("cnfBias") is not None]
    if not cnf_present_rows:
        print("\n(no cnfBias data in this file — Confluence sections skipped. Point this")
        print(" script at a Recorder file from the chart Confluence is actually attached to.)")
    else:
        cnf_rows = [r for r in cnf_present_rows if (r.get("cnfBias") or 0) != 0]
        if tick_only:
            cnf_rows = [r for r in cnf_rows if r.get("prsTick") is True]
        cnf_edges = [signed_edge(r, h, "cnfBias") for r in cnf_rows]
        cnf_base = mean(cnf_edges)

        print("\n" + "=" * 76)
        print("CONFLUENCE — scored on its own bias/gates, independent of Council above")
        print("=" * 76)
        print("CONFLUENCE BASE RATE (all bars where cnfBias != 0, h%d)" % h)
        if cnf_base is None:
            print("  no directional Confluence rows at this horizon.")
        else:
            print("  n %d   mean signed move %s ticks   win %.1f%%   sd %s"
                  % (len(cnf_edges), fmt(cnf_base),
                     100.0 * sum(1 for e in cnf_edges if e and e > 0) / max(1, len(cnf_edges)),
                     fmt(stdev(cnf_edges))))

        table("13. CONFLUENCE FIRED vs BLOCKED — is gating the cascade actually worth it?",
              bucket_by(cnf_rows, h, lambda r: "fired" if r.get("cnfFired") else "blocked (bias present, no signal)",
                        ["fired", "blocked (bias present, no signal)"], direction_key="cnfBias"),
              cnf_base, min_n,
              "If 'blocked' beats or matches 'fired', the cascade is filtering out edge, not risk.")

        gate_names = ["regime", "adx", "liq", "level", "vol", "zscore", "cvb",
                      "imkt", "eye", "flow", "mtf", "profile", "profile-overext", "flux", "trendfusion", "arch", "news",
                      "chop", "participation", "structure", "clock", "optional(other)"]

        def cnf_gate_blockers(r):
            required = [("regime", r.get("cnfRegimeOk")), ("adx", r.get("cnfAdxOk")), ("liq", r.get("cnfLiqOk")),
                        ("level", r.get("cnfLevelOk")),
                        ("vol", r.get("cnfVolOk")), ("zscore", r.get("cnfZscoreOk")), ("cvb", r.get("cnfCvbOk"))]
            tracked_opt = [("imkt", r.get("cnfImktOk")), ("eye", r.get("cnfEyeOk")), ("flow", r.get("cnfFlowOk")),
                           ("participation", r.get("cnfPartOk")), ("structure", r.get("cnfStructOk")),
                           ("clock", r.get("cnfClockOk")), ("mtf", r.get("cnfMtfOk")), ("profile", r.get("cnfProfOk")),
                           ("profile-overext", r.get("cnfProfOverextOk")), ("flux", r.get("cnfFluxOk")),
                           ("trendfusion", r.get("cnfTfusOk")),
                           ("arch", r.get("cnfArchOk")),
                           ("news", r.get("cnfNewsOk")),
                           ("chop", r.get("cnfChopOk"))]
            blockers = [name for name, ok in required if ok is False]
            blockers += [name for name, ok in tracked_opt if ok is False]
            # cnfOptOk folds in the 9 tracked gates above PLUS 7 untracked price-derived ones
            # (cci/exhaustion/harmonic/brick-dir/brick-run/absorption/inefficient — no individual
            # cnf field exists for those yet; chop was promoted out of this bucket 2026-08-03).
            # Only count "optional(other)" when OptOk is false for a reason NONE of the 9 tracked
            # gates already explain — otherwise every block by imkt/eye/flow/etc. would drag in a
            # redundant second "optional" tag (since OptOk is false whenever any of them is) and wrongly get
            # excluded from clean single-gate attribution below.
            if r.get("cnfOptOk") is False and not any(ok is False for _, ok in tracked_opt):
                blockers.append("optional(other)")
            return blockers

        blocked = [r for r in cnf_rows if not r.get("cnfFired")]

        def sole_blocker(r):
            g = cnf_gate_blockers(r)
            return g[0] if g and len(g) == 1 else None

        multi = sum(1 for r in blocked if len(cnf_gate_blockers(r)) > 1)
        clean = sum(1 for r in blocked if len(cnf_gate_blockers(r)) == 1)
        table("14. WHICH GATE IS ACTUALLY COSTING TRADES — sole-blocker attribution",
              bucket_by(blocked, h, sole_blocker, gate_names, direction_key="cnfBias"),
              cnf_base, min_n,
              "Each bucket = blocked bars where THIS gate was the ONLY thing false (clean "
              "attribution). If a bucket's edge beats 'fired' from table 13, that gate is "
              "likely blocking winners more than losers. %d blocked bars had 2+ gates false "
              "simultaneously and are excluded here (can't attribute cleanly)." % multi)
        if clean == 0 and multi > 0:
            print("  (no cleanly-attributable blocked bars yet — every block so far had "
                  "multiple simultaneous gate failures)")

        def cnl_agree(r):
            c = r.get("cnlBias"); f = r.get("cnfBias")
            if not f or c is None or c == 0:
                return None
            return "Council AGREES" if c == f else "Council OPPOSES"
        table("15. COUNCIL vs CONFLUENCE AGREEMENT — corroboration or one witness twice?",
              bucket_by(cnf_rows, h, cnl_agree, ["Council AGREES", "Council OPPOSES"], direction_key="cnfBias"),
              cnf_base, min_n,
              "Only populated when both fusers are on the SAME chart/file. On separate "
              "1-min charts (the current setup) this will read thin or empty by design.")

        print("\n16. CONFLUENCE GATE FALSE-RATE  (how often each required gate is the blocker)")
        for g in gate_names:
            n_false = sum(1 for r in cnf_rows if cnf_gate_blockers(r) and g in cnf_gate_blockers(r))
            pct = 100.0 * n_false / max(1, len(cnf_rows))
            print("  %-10s false on %6d / %d bars  (%5.1f%%)" % (g, n_false, len(cnf_rows), pct))

    # ── 17-18. COUNCIL — the two new v1.39.0 fields (kill-window veto, experimental Eye voter) ──
    # cnlPreVetoBias/cnlKillWindowActive are on every Council row regardless of whether the
    # Kill-Window veto is even switched ON — captured independently so you can measure whether
    # it WOULD help before deciding to trust it, same reasoning as Confluence's fired-vs-blocked
    # table. cnlEyeVote only appears at all once 'Include Eye As Voter' has been turned on.
    kw_rows = [r for r in have_h if (r.get("cnlPreVetoBias") or 0) != 0]
    if tick_only:
        kw_rows = [r for r in kw_rows if r.get("prsTick") is True]
    if kw_rows:
        table("17. COUNCIL KILL-WINDOW — would this veto actually help?",
              bucket_by(kw_rows, h,
                        lambda r: "in kill window" if r.get("cnlKillWindowActive") else "normal hours",
                        ["in kill window", "normal hours"], direction_key="cnlPreVetoBias"),
              base, min_n,
              "Scored on the panel's PRE-veto read, so this works even with the veto OFF. If "
              "'in kill window' isn't meaningfully worse than 'normal hours', the veto has "
              "nothing to protect against on this data yet.")
    else:
        print("\n(no cnlPreVetoBias data — this file predates SneakyCore v1.39.0, or Council "
              "never had a directional read. Kill-window section skipped.)")

    # ── 17b. 2026-08-06 — WHICH COUNCIL VETO IS COSTING TRADES ──
    # Same question as Confluence's table 14, but simpler to answer: Council vetoes are
    # "first match wins" (mutually exclusive), so every vetoed row already has exactly ONE
    # reason string (cnlVeto) -- no multi-gate attribution problem to work around. Built to
    # grade the new VetoNormalRegime alongside the vetoes it sits next to, but covers every
    # Council veto for free -- none of them had per-reason attribution before this.
    # Some reason strings carry dynamic detail (which level, which wall distance) that would
    # otherwise fragment one veto into many near-duplicate buckets, so this collapses each
    # back to its category by matching a fixed prefix from Council's own veto-reason strings.
    def cnl_veto_category(r):
        if not r.get("cnlVetoed"):
            return "not vetoed"
        v = r.get("cnlVeto") or ""
        for prefix, label in (("chop", "chop (STF)"), ("high-vol regime", "high-vol regime"),
                               ("normal-vol regime", "normal-vol regime"),
                               ("liquidity wall", "liquidity wall"), ("MTF opposed", "mtf opposed"),
                               ("kill window", "kill window")):
            if v.startswith(prefix):
                return label
        if "ATR" in v:   # near-level: "<level name> within X.XX ATR" -- name varies, suffix doesn't
            return "near level"
        return v if v else "vetoed (no reason recorded)"

    if kw_rows:
        veto_cats = sorted(set(cnl_veto_category(r) for r in kw_rows) - {"not vetoed"})
        table("17b. WHICH COUNCIL VETO IS COSTING TRADES — per-reason attribution",
              bucket_by(kw_rows, h, cnl_veto_category, ["not vetoed"] + veto_cats, direction_key="cnlPreVetoBias"),
              base, min_n,
              "Scored on the panel's PRE-veto read, so every veto here can be measured even "
              "while switched off. If a veto's bucket scores BELOW 'not vetoed', it's blocking "
              "losers -- doing its job. If it scores ABOVE, it's blocking winners.")

    # ── 17c. 2026-08-06 — the two new Council-rebase modulators (NormalVolRegimeDamp,
    # ProfileOverextendedDamp). Unlike the vetoes above, these are DAMPS -- the row still
    # fires normally when they trigger, just at reduced conviction -- so directional/cnlBias
    # already reflects them with no pre-veto-bias trick needed. Council reports them as
    # substring markers in cnlReasons (" · normal-vol", " · profile-overext") rather than
    # dedicated boolean fields, matching the real project's own audit-string convention
    # instead of reintroducing Sneaky's old per-field boolean pattern.
    def _reason_has(tag):
        return lambda r: tag in (r.get("cnlReasons") or "")

    for tag, label, note in [
        (" · normal-vol", "17c. NORMAL-VOL REGIME DAMP", "Fires when SneakyRegime reports Regime==1 and "
         "NormalVolRegimeDamp < 1.0. Off by default -- this bucket will read empty until turned on."),
        (" · profile-overext", "17d. PROFILE OVEREXTENSION DAMP", "Fires when Profile's Signal agrees with the "
         "fused bias and ProfileOverextendedDamp < 1.0. Off by default -- empty until turned on."),
        (" · active-weight-cap", "17e. ACTIVE-WEIGHT CAP", "New 2026-09-16, testing the crowding hypothesis "
         "directly (does total active voter weight, not just the net/active RATIO conviction already is, "
         "predict reversal?). Off by default -- this bucket will read empty until ActiveWeightCapDamp is set "
         "below 1.0 on a chart. If 'fired' beats 'not fired', the cap is doing its job."),
    ]:
        table(label + " — did it fire, and did it matter?",
              bucket_by(directional, h, lambda r, t=tag: "fired" if t in (r.get("cnlReasons") or "") else "not fired",
                        ["not fired", "fired"]),
              base, min_n, note)

    eye_vote_rows = [r for r in have_h if r.get("cnlEyeVote") is not None]
    if tick_only:
        eye_vote_rows = [r for r in eye_vote_rows if r.get("prsTick") is True]
    if eye_vote_rows:
        def cnl_eye_agree(r):
            v = r.get("cnlEyeVote"); b = r.get("cnlBias")
            if v is None or not b:
                return None
            if v == 0:
                return "neutral"
            return "agrees" if v == b else "opposes"
        table("18. COUNCIL'S EXPERIMENTAL EYE VOTE — earning its (half) weight?",
              bucket_by(eye_vote_rows, h, cnl_eye_agree, ["agrees", "neutral", "opposes"]),
              base, min_n,
              "Only present when 'Include Eye As Voter' was ON when these rows were recorded. "
              "Remember Eye is built from PRS/IMKT/CVB already voting separately — table 4/5 "
              "above test those directly. This table is specifically whether Eye's BUNDLED "
              "verdict adds anything beyond what its own ingredients already say.")
    else:
        print("\n(no cnlEyeVote data — 'Include Eye As Voter' was off for every row in this "
              "file, which is the default. Section 18 skipped.)")

    # ── coverage ──
    print("\n19. SENSOR COVERAGE  (how often each was actually present)")
    fields = [("eyeDir", "Eye"), ("prsDir", "Pressure"), ("imktLean", "Intermarket"),
              ("cvbBias", "Drift CVB"), ("rvol", "Participation"), ("phase", "Clock"),
              ("trendDir", "Trend"), ("regime", "Regime"), ("cnfBias", "Confluence"),
              ("lvlSide", "Location"), ("mtfBias", "Mtf"), ("profLoc", "Profile")]
    for k, lbl in fields:
        present = sum(1 for r in directional if r.get(k) is not None)
        pct = 100.0 * present / max(1, len(directional))
        flag = "   <- sparse, treat its table with suspicion" if pct < 50 else ""
        print("  %-14s %6d / %d  (%5.1f%%)%s" % (lbl, present, len(directional), pct, flag))

    # ── TP/stop optimization (2026-08-08, real-data path added 2026-08-09) — see the
    # module-level note above pctl() for the original approximation and its one real
    # adaptation (pessimistic tiebreak, no timing data). The --excursion-folder path
    # below reads ExcursionRecorder's actual maxMFE/maxMAE instead of approximating.
    print("\n20. TP/STOP OPTIMIZATION")
    pts = None
    if excursion_folder:
        if not os.path.exists(excursion_folder):
            print("  --excursion-folder given but '%s' doesn't exist -- falling back to the" % excursion_folder)
            print("  approximation below. Check the path and try again if that wasn't intended.")
            excursion_folder = None
    if excursion_folder:
        exc_rows, exc_files, exc_bad = load(excursion_folder, verbose_bad=False)
        exc_rows = [r for r in exc_rows if r.get("kind") == "excursion"]
        if bartype:
            exc_rows = [r for r in exc_rows if r.get("bartype") == bartype]
        print("  --excursion-folder given: %d files, %d excursion rows%s"
              % (len(exc_files), len(exc_rows), ("  (%d unparseable)" % exc_bad) if exc_bad else ""))
        if exc_bad:
            print_bad_detail(load.bad_by_file, load.bad_samples, len(exc_files))
        if bartype:
            print("  (filtered to bartype '%s', matching the main report's filter)" % bartype)
        print("  NOTE: --instance / --after from the main report are NOT applied to excursion")
        print("  rows here -- this reads the whole folder given. Point --excursion-folder at a")
        print("  narrower path yourself if you need the same scoping.")
        pts = []
        for r in exc_rows:
            fm = excursion_favorable_adverse(r)
            if fm is not None:
                pts.append(fm)
        print("  rows with usable maxMFE/maxMAE   %d" % len(pts))
        print("\n  Using REAL ExcursionRecorder data (maxMFE/maxMAE, real tape-fillable prices,")
        print("  full signal life -- NOT the same h%d-bar window as the rest of this report;" % h)
        print("  ExcursionRecorder tracks until natural conclusion, not a fixed bar count)." )
    else:
        print("  (at h%d, adapted from the real Sentinel Suite's own SentinelExcursions.cs --" % h)
        print("  see this script's own notes above pctl() for what changed and why. Pass")
        print("  --excursion-folder <path> to use ExcursionRecorder's real data instead of")
        print("  this approximation.)")
        pts = []
        for r in directional:
            fm = true_mfe_mae(r, h)
            if fm is not None:
                pts.append(fm)
        print("  rows with usable mfe/mae at h%d   %d" % (h, len(pts)))

    if len(pts) < 15:
        print("  fewer than 15 rows -- not enough to grid-search a TP/stop. Skipped.")
    else:
        best = best_tp_stop(pts)
        grid = tp_stop_grid(pts)
        print("\n  BEST CONFIG (by estimated expectancy):")
        print("    TP %.2f ticks   Stop %.2f ticks   hit rate %.1f%%   est. exp %.2f ticks/trade   n=%d"
              % (best["tp"], best["stop"], 100.0 * best["hit_rate"], best["exp"], best["n"]))
        print("\n  FULL GRID (12 configs -- TP = 33/50/67th pct of favorable move, stop = a")
        print("  FRACTION of TP so R:R stays capped, not farmed by a pathologically wide stop):")
        print("  %6s %8s %8s %9s %10s %6s" % ("TPpct", "TP", "Stop", "HitRate", "Exp/trade", "n"))
        for g in sorted(grid, key=lambda x: -x["exp"]):
            print("  %6.0f %8.2f %8.2f %8.1f%% %10.2f %6d"
                  % (g["tp_pct"], g["tp"], g["stop"], 100.0 * g["hit_rate"], g["exp"], g["n"]))
        print("\n  This is an ESTIMATE off aggregate max-favorable/max-adverse per row, not a")
        print("  path-level backtest -- same caveat the original tool states about itself.")
        print("  The real number only comes from actually running these TP/stop values.")
        if excursion_folder:
            print("\n  Even with real MFE/MAE, ties (both TP and stop level crossed somewhere in the")
            print("  signal's life) still default to pessimistic (stop-first): the grid tests")
            print("  ARBITRARY TP/stop values via percentiles, not the one specific barrierTicks")
            print("  distance ExcursionRecorder's own firstTouch/ftAmbig fields were resolved for,")
            print("  so that real tie-break data doesn't directly carry over to every grid point.")

    print("\n" + "=" * 76)
    print("READ THE BASE RATE FIRST. A bucket only means something if it differs from it.")
    print("Buckets marked (thin) have fewer than %d rows and will move a lot with more data." % min_n)
    print("No significance testing is done here on purpose: bar-to-bar rows are correlated,")
    print("so a p-value would be confidently wrong rather than helpfully uncertain.")
    print("=" * 76)


if __name__ == "__main__":
    main()
