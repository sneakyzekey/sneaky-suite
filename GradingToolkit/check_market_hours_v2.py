#!/usr/bin/env python3
"""
check_market_hours_v2.py — same as check_market_hours.py, but breaks down
each weekday bucket by hour so you can see EXACTLY when rows land instead
of just a closed/open count. Also lists the earliest and latest timestamp
seen overall, and per-Sunday breakdown by calendar date (not just weekday
name), since "Sunday" alone can span several different actual Sundays in a
multi-day recording folder.

Usage:
    python check_market_hours_v2.py <recorder-folder> [--bartype NAME]
"""
import sys, os, glob, json
from collections import Counter, defaultdict
from datetime import datetime

ET_OFFSET_HOURS = 0  # set to 0 if machine clock is already US Eastern

def eastern_hour(dt):
    return (dt.hour - ET_OFFSET_HOURS) % 24

def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__); sys.exit(1)
    folder = args[0]
    bartype = None
    if "--bartype" in args:
        bartype = args[args.index("--bartype") + 1]

    files = glob.glob(os.path.join(folder, "**", "*.jsonl"), recursive=True)
    if not files:
        print("no .jsonl files found under", folder); sys.exit(1)

    all_dt = []
    sunday_hours = Counter()
    sunday_dates = defaultdict(int)

    for f in files:
        with open(f, "r", encoding="utf-8-sig") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    r = json.loads(line)
                except Exception:
                    continue
                if bartype and r.get("bartype") != bartype:
                    continue
                t = r.get("time")
                if not t:
                    continue
                try:
                    dt = datetime.fromisoformat(t.replace("Z", "+00:00"))
                except Exception:
                    continue
                all_dt.append(dt)
                if dt.weekday() == 6:  # Sunday
                    sunday_hours[eastern_hour(dt)] += 1
                    sunday_dates[dt.date().isoformat()] += 1

    if not all_dt:
        print("no rows with parseable timestamps found"); sys.exit(1)

    all_dt.sort()
    print("=" * 70)
    print("earliest row     ", all_dt[0])
    print("latest row       ", all_dt[-1])
    print("=" * 70)
    print()
    print("SUNDAY rows by Eastern hour (0-23):")
    for h in range(24):
        n = sunday_hours.get(h, 0)
        flag = "  <-- market should be CLOSED before 18" if h < 18 and n > 0 else ""
        print("  %02d:00   %6d%s" % (h, n, flag))
    print()
    print("SUNDAY rows by calendar date:")
    for d in sorted(sunday_dates):
        print("  %s   %d rows" % (d, sunday_dates[d]))

if __name__ == "__main__":
    main()
