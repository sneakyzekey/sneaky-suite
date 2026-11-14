#!/usr/bin/env python3
"""
check_recorder_version.py — reports, per file, whether it was written by the
ML-updated SneakyRecorder (the one that captures cnlVotes) or an older one.

A file is "updated" if AT LEAST ONE row has "cnlVotes" as a real object (not
just present-but-null, and not missing the key entirely). Council doesn't
run on every single bar, so even an updated file can have some rows where
cnlVotes is null — that's normal, not a sign of an old file.

Usage:
    python check_recorder_version.py <recorder-folder>
"""
import sys, os, glob, json

def main():
    if len(sys.argv) < 2:
        print(__doc__); sys.exit(1)
    folder = sys.argv[1]
    files = sorted(glob.glob(os.path.join(folder, "**", "*.jsonl"), recursive=True))
    if not files:
        print("no .jsonl files found under", folder); sys.exit(1)

    updated, old, empty = [], [], []
    complete_with_votes, incomplete_with_votes = 0, 0
    for f in files:
        has_key = False
        has_data = False
        rows = 0
        try:
            with open(f, "r", encoding="utf-8-sig") as fh:
                for line in fh:
                    line = line.strip()
                    if not line:
                        continue
                    rows += 1
                    try:
                        r = json.loads(line)
                    except Exception:
                        continue
                    if "cnlVotes" in r:
                        has_key = True
                        if isinstance(r["cnlVotes"], dict) and r["cnlVotes"]:
                            has_data = True
                            if r.get("complete", True):
                                complete_with_votes += 1
                            else:
                                incomplete_with_votes += 1
        except Exception as e:
            print("  (couldn't read %s: %s)" % (os.path.basename(f), e))
            continue

        name = os.path.basename(f)
        if not has_key:
            old.append((name, rows))
        elif has_data:
            updated.append((name, rows))
        else:
            empty.append((name, rows))  # updated Recorder, but Council never fired on any row

    print("=" * 70)
    print("RECORDER VERSION CHECK")
    print("=" * 70)
    print("total files:", len(files))
    print()
    print("UPDATED (has real cnlVotes data — usable for train_council_weights.py):")
    for name, rows in updated:
        print("  %-40s %5d rows" % (name, rows))
    if not updated:
        print("  (none)")

    print()
    print("UPDATED RECORDER, BUT NO cnlVotes DATA YET (Council never fired on any")
    print("row in this file — usually just means the file is short/quiet, not old):")
    for name, rows in empty:
        print("  %-40s %5d rows" % (name, rows))
    if not empty:
        print("  (none)")

    print()
    print("OLD (no cnlVotes key at all — predates the ML Recorder update, not usable")
    print("for train_council_weights.py; still fine for grade_sneaky.py / train_predictor.py):")
    for name, rows in old:
        print("  %-40s %5d rows" % (name, rows))
    if not old:
        print("  (none)")

    print()
    print("=" * 70)
    print("COMPLETE vs INCOMPLETE  (of rows that DO have real cnlVotes data)")
    print("=" * 70)
    print("  complete=true  (usable — outcome resolved)    ", complete_with_votes)
    print("  complete=false (NOT usable — force-flushed before its horizon finished,")
    print("                  usually from a NinjaTrader/indicator restart)  ", incomplete_with_votes)
    if incomplete_with_votes > 0 and complete_with_votes == 0:
        print()
        print("  Every row with real vote data is incomplete. This matches heavy recompiling/")
        print("  restarting today interrupting Recorder before rows could finish their horizon.")
        print("  Let NinjaTrader run uninterrupted for a while (long enough for your longest")
        print("  configured horizon to elapse a few times over) and complete rows should start")
        print("  accumulating.")

if __name__ == "__main__":
    main()
