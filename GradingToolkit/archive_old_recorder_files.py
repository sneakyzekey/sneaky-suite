#!/usr/bin/env python3
"""archive_old_recorder_files.py — tidy the Recorder folder WITHOUT deleting anything.

Moves (never deletes) any .jsonl file where EVERY row is either:
    - old-schema (no cnlVotes key at all — predates the ML Recorder update), or
    - tagged cnlInstance A or B (retired instances)
into an "_archive" subfolder, out of the live recording path but still fully on disk.

SAFE BY DESIGN: a file is only moved if it is ENTIRELY unwanted rows — if a file has even
ONE row from D/E/F, or one row with a real vote vector under no instance tag, the WHOLE file
is left exactly where it is, untouched. This avoids ever rewriting a file NinjaTrader might
still have open, and avoids any row-level editing that could go wrong. Nothing is deleted;
everything moved can be moved right back if you ever want it.

Usage (dry run first, recommended):
    python archive_old_recorder_files.py --src "C:\\Users\\Administrator\\Documents\\NinjaTrader 8\\Sneaky\\Recorder\\rec-1" --dry-run
    python archive_old_recorder_files.py --src "C:\\Users\\Administrator\\Documents\\NinjaTrader 8\\Sneaky\\Recorder\\rec-1"
"""
import argparse
import glob
import json
import os
import shutil


def file_is_entirely_unwanted(path):
    """True only if EVERY row in this file is old-schema or instance A/B. False (keep it in
    place) if the file has any row with real, current data from EITHER Council or Confluence,
    or is empty/unreadable — when in doubt, never move it.

    2026-08-15 — CORRECTED. The original version only checked cnlVotes/cnlInstance (Council's
    own fields). Instance D is Confluence-only and NEVER runs Council, so every genuine,
    current D row has empty cnlVotes by design, not because it's old — the original check
    couldn't tell those two situations apart and archived real D data as if it were old-schema
    junk. Now checks cnfBias/cnfInstance (Confluence's own, separate fields) independently, so
    a row is only ever treated as safely-skippable if BOTH systems it could belong to are
    either absent entirely or explicitly tagged A/B."""
    saw_any_row = False
    with open(path, encoding="utf-8-sig") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            saw_any_row = True
            try:
                d = json.loads(line)
            except json.JSONDecodeError:
                return False  # can't parse it -> don't touch it

            votes = d.get("cnlVotes")
            cnl_inst = d.get("cnlInstance")
            has_real_votes = isinstance(votes, dict) and bool(votes)

            # cnfBias is written as an int (…, -1/0/+1) ONLY when Confluence is actually
            # attached to this chart — absent (key missing) or explicit JSON null both mean
            # "no Confluence here", matching how Recorder itself null-guards this field.
            cnf_bias = d.get("cnfBias")
            has_confluence = cnf_bias is not None
            cnf_inst = d.get("cnfInstance")

            if has_real_votes and cnl_inst not in ("A", "B"):
                return False  # real, current Council data -> keep the whole file
            if has_confluence and cnf_inst not in ("A", "B"):
                return False  # real, current Confluence data -> keep the whole file
            # otherwise: this row is either truly old-schema (neither system present), or
            # every system it DOES have data for is tagged A/B -> safe, keep checking the rest

    return saw_any_row  # empty files are left alone too (nothing to archive)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--src", required=True)
    ap.add_argument("--dry-run", action="store_true", help="report what WOULD move, without moving anything")
    args = ap.parse_args()

    print("archive_old_recorder_files.py -- version 2026-08-15b (checks BOTH cnlVotes/cnlInstance")
    print("and cnfBias/cnfInstance -- if you don't see this line, you're running an old copy)")
    print()

    archive_dir = os.path.join(args.src, "_archive")
    files = sorted(glob.glob(os.path.join(args.src, "*.jsonl")))
    if not files:
        print(f"No .jsonl files found under {args.src}")
        return

    to_move = []
    for path in files:
        try:
            if file_is_entirely_unwanted(path):
                to_move.append(path)
        except OSError as ex:
            print(f"  skip (unreadable): {os.path.basename(path)} — {ex}")

    print(f"Scanned {len(files)} files.")
    print(f"{len(to_move)} file(s) are ENTIRELY old-schema/A/B and would be archived:")
    for p in to_move:
        print(f"  {os.path.basename(p)}")
    kept = len(files) - len(to_move)
    print(f"{kept} file(s) left in place (contain D/E/F or untagged real-vote rows, or are mixed).")

    if args.dry_run:
        print("\n--dry-run: nothing moved. Re-run without --dry-run to actually archive these files.")
        return

    if not to_move:
        print("\nNothing to move.")
        return

    os.makedirs(archive_dir, exist_ok=True)
    moved = 0
    for p in to_move:
        dest = os.path.join(archive_dir, os.path.basename(p))
        try:
            shutil.move(p, dest)
            moved += 1
        except OSError as ex:
            print(f"  FAILED to move {os.path.basename(p)}: {ex}")
    print(f"\nMoved {moved} file(s) to {archive_dir}")
    print("Nothing was deleted — move them back out of _archive at any time if needed.")


if __name__ == "__main__":
    main()
