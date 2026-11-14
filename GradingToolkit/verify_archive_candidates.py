#!/usr/bin/env python3
"""verify_archive_candidates.py -- version 2026-08-15c

The real cross-check: does the archiver's OWN candidate list (the files it actually
proposes to move) overlap with any file that has real Council or Confluence data?
Reuses archive_old_recorder_files.py's exact function directly, so the two scripts can
never drift apart from each other the way two separate reimplementations could.

Previous version listed EVERY file with real data, regardless of whether the archiver
was ever going to touch it -- correct, but confusing, since a long list looked alarming
even when the archiver's actual candidates were completely clean.

Usage:
    python verify_archive_candidates.py --src "C:\\...\\Recorder\\rec-1"
"""
import argparse
import glob
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from archive_old_recorder_files import file_is_entirely_unwanted


def real_data_counts(path):
    """Independent count (does NOT reuse the archiver's logic) -- if this and the
    archiver's own check ever disagree on a specific file, that mismatch is exactly
    what this function exists to catch."""
    council_rows = confluence_rows = 0
    with open(path, encoding="utf-8-sig") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                d = json.loads(line)
            except json.JSONDecodeError:
                continue
            votes = d.get("cnlVotes")
            if isinstance(votes, dict) and votes and d.get("cnlInstance") not in ("A", "B"):
                council_rows += 1
            if d.get("cnfBias") is not None and d.get("cnfInstance") not in ("A", "B"):
                confluence_rows += 1
    return council_rows, confluence_rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True)
    args = ap.parse_args()

    files = sorted(glob.glob(os.path.join(args.src, "*.jsonl")))
    candidates = [f for f in files if file_is_entirely_unwanted(f)]

    print(f"Scanned {len(files)} files. Archiver would move {len(candidates)} of them.")
    print()

    bad = []
    for path in candidates:
        c, f = real_data_counts(path)
        if c > 0 or f > 0:
            bad.append((os.path.basename(path), c, f))

    if bad:
        print(f"*** MISMATCH: {len(bad)} of the archiver's OWN candidates actually have real data: ***")
        for name, c, f in bad:
            print(f"  {name}  (council={c}, confluence={f})")
        print()
        print("DO NOT run the real archive -- the archiver and this check disagree on these files.")
    else:
        print(f"CONFIRMED CLEAN: all {len(candidates)} of the archiver's candidates have zero real")
        print("Council or Confluence data. Safe to run archive_old_recorder_files.py for real.")


if __name__ == "__main__":
    main()
