#!/usr/bin/env python3
"""inspect_one_file.py — detailed row-level breakdown of a single Recorder .jsonl file.

Usage:
    python inspect_one_file.py "C:\\...\\Recorder\\rec-1\\20260811T062900__NQ__4v1x1.jsonl"
"""
import json
import sys
from collections import Counter

if len(sys.argv) != 2:
    print("Usage: python inspect_one_file.py <path to .jsonl file>")
    sys.exit(1)

path = sys.argv[1]
total = 0
has_votes = 0
no_votes = 0
inst_counts = Counter()
time_min = time_max = None

with open(path, encoding="utf-8-sig") as fh:
    for line in fh:
        line = line.strip()
        if not line:
            continue
        total += 1
        try:
            d = json.loads(line)
        except json.JSONDecodeError:
            continue
        t = d.get("time")
        if t:
            if time_min is None or t < time_min:
                time_min = t
            if time_max is None or t > time_max:
                time_max = t
        votes = d.get("cnlVotes")
        inst = d.get("cnlInstance") or "(untagged)"
        inst_counts[inst] += 1
        if isinstance(votes, dict) and votes:
            has_votes += 1
        else:
            no_votes += 1

print(f"File: {path}")
print(f"Total rows: {total}")
print(f"Rows with real cnlVotes: {has_votes}")
print(f"Rows with NO cnlVotes:   {no_votes}")
print(f"Time range in this file: {time_min}  to  {time_max}")
print()
print("cnlInstance breakdown (all rows, regardless of vote status):")
for inst, n in inst_counts.most_common():
    print(f"  {inst:20s} {n}")
