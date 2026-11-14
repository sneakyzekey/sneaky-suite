#!/usr/bin/env python3
"""check_no_votes.py — direct sanity check on sneaky_to_lab.py's "no_votes" drops.

Re-reads the same source files sneaky_to_lab.py just processed, applies the exact same
"does this row have a real cnlVotes dict" check, and for every row that WOULD be dropped,
tallies which cnlInstance it belongs to. This turns "probably instance D" from an inference
into a directly-verified count.

Usage:
    python check_no_votes.py --src "C:\\Users\\Administrator\\Documents\\NinjaTrader 8\\Sneaky\\Recorder\\rec-1"
"""
import argparse
import glob
import json
import os
from collections import Counter


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True)
    args = ap.parse_args()

    files = sorted(glob.glob(os.path.join(args.src, "*.jsonl")))
    if not files:
        print(f"No .jsonl files found under {args.src}")
        return

    total = 0
    bad_json = 0
    no_votes = 0
    no_votes_by_instance = Counter()
    has_votes_by_instance = Counter()

    for path in files:
        with open(path, encoding="utf-8-sig") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    d = json.loads(line)
                except json.JSONDecodeError:
                    # 2026-09-05 -- `total` used to be incremented BEFORE this try/except,
                    # so a malformed line inflated the no_votes% denominator while vanishing
                    # from both the no_votes and has_votes numerators -- silently skewing
                    # every reported percentage low. This tool exists to turn an inference
                    # into a directly-verified count, so its own parse failures need to be
                    # counted and shown, not hidden inside someone else's denominator.
                    bad_json += 1
                    continue
                total += 1
                inst = d.get("cnlInstance") or "(no cnlInstance tag)"
                votes = d.get("cnlVotes")
                if not isinstance(votes, dict) or not votes:
                    no_votes += 1
                    no_votes_by_instance[inst] += 1
                else:
                    has_votes_by_instance[inst] += 1

    print(f"Total rows scanned: {total}")
    if bad_json:
        print(f"  ({bad_json} additional line(s) were unparseable JSON -- excluded from "
              f"the count above and from every percentage below)")
    if total:
        print(f"no_votes rows:      {no_votes}  ({100.0*no_votes/total:.1f}%)")
    else:
        print("no_votes rows:      0")
    print()
    print("no_votes rows, broken down by cnlInstance:")
    for inst, n in no_votes_by_instance.most_common():
        print(f"  {inst:20s} {n:8d}  ({100.0*n/no_votes:.1f}% of all no_votes drops)")
    print()
    print("rows WITH real votes, broken down by cnlInstance (for comparison):")
    for inst, n in has_votes_by_instance.most_common():
        print(f"  {inst:20s} {n:8d}")


if __name__ == "__main__":
    main()
