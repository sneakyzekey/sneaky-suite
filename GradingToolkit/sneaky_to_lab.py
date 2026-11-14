#!/usr/bin/env python3
"""sneaky_to_lab.py — translate real SneakyRecorder rows into the row-corpus shape
lab/ingest/ingest.py's _row_rec() expects, WITHOUT touching ingest.py itself.

WHY THIS EXISTS
---------------
lab/README.md's occupancy-model warning is real, and the tools that could actually settle
it (exit_policy_sweep.py and friends) need data in lab's own sentinel.db — built by
ingest.py from JSONL under Excursions\\council\\<schema>\\*.jsonl. ingest.py's own field
mapper (_row_rec) expects keys like inst/bartype/fireTime/votes/dir/firePx — but the REAL,
currently-live SneakyRecorder.cs writes a different, cnl-prefixed schema (cnlBias, cnlVotes,
cnlConv, ...). This bridges the two WITHOUT editing ingest.py's own parsing logic, which has
a lot of careful, audited history behind it (schema versioning, the 1.3/1.4 label-
contamination finding, the drop-tracking discipline) that a same-file edit risks disturbing.

VERIFIED FIELD MAPPING (checked directly against SneakyRecorder_v1_0_0.cs's actual field-
writing code, not assumed):
    lab expects        sneaky writes           note
    ------------        ------------           ----
    inst                inst                   same name
    bartype             bartype                same name
    fireTime            time                   renamed
    votes               cnlVotes               same shape already: {tag: {"dir":.., "w":..}}
                                                (confirmed against train_council_weights.py's
                                                own read of this exact field)
    dir                 cnlBias                POST-veto, not cnlPreVetoBias — deliberately.
                                                Using the pre-veto lean would count a vetoed
                                                bar as a real signal, exactly the kind of
                                                error this whole bridge exists to avoid.
    firePx              price                  the bar's price at record time
    conviction          cnlConv                same concept
    sizeMult            cnlSize                same concept
    netScore            cnlNet                 same concept
    activeW             cnlActive              same concept
    reasons             cnlReasons             same concept
    scope               scope                  same name
    regime              regime                 sneaky writes an INT (0/1/2); mapped here to
                                                the low/normal/high labels grade_sneaky.py's
                                                own tables already use, for consistency
    agree               agree                  same name
    disagree            against                same concept, renamed
    voters              voters                 same name
    schema              (new tag: "sneaky.1")  deliberately NOT "1.3"/"1.4"/"1.5" — those
                                                already carry their own documented provenance
                                                and the known HA-close contamination finding.
                                                Reusing one of those tags would silently pool
                                                genuinely different data into an already-
                                                audited corpus. See NEW SCHEMA TAG below.

NOT TRANSLATED (left null; genuinely absent or not a clean match in the current Sneaky
schema — listed explicitly rather than silently omitted, so nobody assumes this is a
complete migration):
    maxMFE, maxMAE, msToMFE, msToMAE, firstTouch, milestone curves, ms_to_target_r/stop_r
        — these are TICK-PATH data in Sneaky's own architecture too (a separate recorder,
          SneakyExcursionRecorder, not the main SneakyRecorder this script reads). Row-side
          rows are fine to leave these zero/null — that matches _row_rec()'s own defaulting
          (`d.get("maxMFE", 0) or 0`), and matches how the Lab's OWN row corpus already
          works: the row and the tick sidecar are two separate ingestion paths, joined later
          on (inst, bartype, entry_utc).
    episodeId, barrierTicks (as a tick distance), recVer/coreVer/cnclVer/barLabel,
    convBucket, adx (numeric), endReason
        — no clean equivalent in Sneaky's current row schema. adx specifically: Sneaky has
          `adxStrong` (a BOOLEAN), not a numeric ADX reading — mapping a bool to a fake
          numeric would be worse than leaving it null, so it's left null.

NEW SCHEMA TAG: "sneaky.1"
    Output goes to a NEW folder, Excursions\\council\\sneaky.1\\, not into the existing
    1.3/1.4/1.5 directories. To make ingest.py actually scan it, add one line to its own
    CROW_SCHEMAS tuple (see the printed instructions at the end of a run) — a small,
    additive, one-line change to ingest.py, not a rewrite of its parsing logic.

USAGE
    python sneaky_to_lab.py --src "C:\\Users\\Administrator\\Documents\\NinjaTrader 8\\Sneaky\\Recorder\\rec-1" \\
                             --dst "C:\\Users\\Administrator\\Documents\\NinjaTrader 8\\Sentinel\\Excursions\\council\\sneaky.1"
    (adjust --dst to wherever your actual Lab tree's Excursions\\council\\ root lives)
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import sys

SCHEMA_TAG = "sneaky.1"

REGIME_LABELS = {0: "low", 1: "normal", 2: "high"}


class Drops:
    """Mirrors lab/ingest/ingest.py's own Drops class — same discipline: nothing is
    silently discarded without being counted and reported."""
    def __init__(self):
        self.n = {}

    def add(self, reason, k=1):
        self.n[reason] = self.n.get(reason, 0) + k

    def total(self):
        return sum(self.n.values())

    def report(self):
        if not self.n:
            print("  drops: none")
            return
        parts = ", ".join(f"{r}={self.n[r]}" for r in sorted(self.n))
        print(f"  drops ({self.total()} total): {parts}")


def translate_row(d: dict, drops: Drops) -> dict | None:
    """One real SneakyRecorder row -> one lab-shaped row, or None if it can't be translated
    (missing a genuinely required field — counted in drops, never silently skipped)."""
    inst = d.get("inst")
    bartype = d.get("bartype")
    fire_time = d.get("time")
    votes = d.get("cnlVotes")

    if not (inst and bartype and fire_time):
        drops.add("missing_inst_bartype_time")
        return None
    if not isinstance(votes, dict) or not votes:
        # matches ingest.py's own rule: a row with no vote vector is a defect for a
        # council-sourced row (there's no non-Council producer in Sneaky's schema the way
        # KEEL was in the Lab's own history, so this is unconditional here).
        drops.add("no_votes")
        return None

    regime_raw = d.get("regime")
    regime = REGIME_LABELS.get(regime_raw) if regime_raw is not None else None

    return {
        "inst": inst,
        "bartype": bartype,
        "fireTime": fire_time,
        "votes": votes,
        "dir": d.get("cnlBias", 0),
        "firePx": d.get("price"),
        "schema": SCHEMA_TAG,
        "conviction": d.get("cnlConv"),
        "sizeMult": d.get("cnlSize"),
        "netScore": d.get("cnlNet"),
        "activeW": d.get("cnlActive"),
        "reasons": d.get("cnlReasons"),
        "scope": d.get("scope"),
        "regime": regime,
        "agree": d.get("agree"),
        "disagree": d.get("against"),
        "voters": d.get("voters"),
        # deliberately NOT included: maxMFE/maxMAE/msToMFE/msToMAE/firstTouch/milestones,
        # episodeId, barrierTicks, recVer/coreVer/cnclVer/barLabel, convBucket, adx,
        # endReason — see the NOT TRANSLATED section in the module docstring.
    }


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--src", required=True, help="Real SneakyRecorder folder, e.g. .../Sneaky/Recorder/rec-1")
    ap.add_argument("--dst", required=True, help="Output folder for the translated rows, e.g. .../Excursions/council/sneaky.1")
    ap.add_argument("--bartype", default=None, help="Optional: only translate this bartype (e.g. 4v1x1)")
    args = ap.parse_args()

    os.makedirs(args.dst, exist_ok=True)
    files = sorted(glob.glob(os.path.join(args.src, "*.jsonl")))
    if not files:
        print(f"No .jsonl files found under {args.src}")
        sys.exit(1)

    drops = Drops()
    total_in = total_out = 0
    written_files = unchanged_files = 0
    for path in files:
        out_rows = []
        with open(path, encoding="utf-8-sig") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                total_in += 1
                try:
                    d = json.loads(line)
                except json.JSONDecodeError:
                    drops.add("bad_json")
                    continue
                if args.bartype and d.get("bartype") != args.bartype:
                    drops.add("bartype_filtered")
                    continue
                r = translate_row(d, drops)
                if r is not None:
                    out_rows.append(r)
                    total_out += 1

        if out_rows:
            out_name = os.path.splitext(os.path.basename(path))[0] + ".sneaky1.jsonl"
            out_path = os.path.join(args.dst, out_name)
            new_content = "".join(json.dumps(r) + "\n" for r in out_rows)

            # 2026-08-15 — only write if the content actually changed. Dict key order is
            # stable across runs (translate_row always builds the same shape), so this
            # string comparison is a reliable "did anything really change" check, not an
            # approximation. Rewriting unconditionally every cycle gave every output file
            # a fresh mtime even when nothing inside it changed, which defeated ingest.py's
            # own mtime-based skip check — every sync cycle was re-processing the entire
            # historical corpus instead of just what's genuinely new. Skipping the write
            # when content is identical keeps the old mtime, so ingest.py's skip logic
            # actually works as intended.
            existing = None
            if os.path.exists(out_path):
                try:
                    with open(out_path, encoding="utf-8") as fh:
                        existing = fh.read()
                except OSError:
                    existing = None  # can't read it -> just rewrite, safest fallback

            if existing == new_content:
                unchanged_files += 1
            else:
                with open(out_path, "w", encoding="utf-8") as fh:
                    fh.write(new_content)
                written_files += 1

    print(f"Read {total_in} raw rows from {len(files)} files.")
    print(f"Translated {total_out} rows -> {args.dst}")
    print(f"  {written_files} output file(s) written (new or changed), "
          f"{unchanged_files} unchanged (left alone, mtime preserved)")
    drops.report()
    print()
    print("NEXT STEP — ingest.py needs ONE additive line so it scans this new folder.")
    print("Find this line in lab/ingest/ingest.py:")
    print('    CROW_SCHEMAS = ("1.3", "1.4", "1.5")')
    print("and change it to:")
    print(f'    CROW_SCHEMAS = ("1.3", "1.4", "1.5", "{SCHEMA_TAG}")')
    print("That's the only change needed to ingest.py itself — its own parsing logic,")
    print("schema-versioning discipline, and drop-tracking are untouched.")


if __name__ == "__main__":
    main()
