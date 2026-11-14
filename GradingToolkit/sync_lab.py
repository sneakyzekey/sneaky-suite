#!/usr/bin/env python3
"""sync_lab.py -- keeps sentinel.db current automatically.

Runs TWO steps in sequence, every cycle:
  1. sneaky_to_lab.py  -- translates fresh rows from the real Recorder folder into the
     Lab's row-corpus shape (Excursions/council/sneaky.1/).
  2. ingest.py          -- pulls those translated rows, plus any new tick-path sidecars,
     into sentinel.db. Already incremental on its own (skips a file whose mtime hasn't
     changed since last time), so re-running it every cycle is cheap once the corpus is
     large -- only genuinely new/changed files actually get re-read into the database.

Step 1 re-scans the whole Recorder folder each cycle (translation itself is fast --
it's just JSONL in, JSONL out, no database writes) so nothing from step 1 needs its own
incremental logic; step 2's own mtime-skip already makes the expensive part efficient.

TWO WAYS TO ACTUALLY GET "AUTOMATIC":

  A) Leave a window open, running continuously:
       python sync_lab.py --loop 900
     (every 900s = 15 min; Ctrl+C to stop)

  B) Windows Task Scheduler (recommended -- keeps running even with no window open):
       python sync_lab.py --once
     as the scheduled action, triggered on whatever interval you want (e.g. every 15
     min). See the printed setup notes at the bottom of a --once run for the exact
     Task Scheduler fields to fill in.

Defaults below match the real folder layout established this session -- override with
the flags if your paths differ.
"""
import argparse
import datetime
import os
import subprocess
import sys
import time

DEFAULT_RECORDER = r"C:\Users\Administrator\Documents\NinjaTrader 8\Sneaky\Recorder\rec-1"
DEFAULT_ROWDST = r"C:\Users\Administrator\Documents\NinjaTrader 8\Sneaky\Excursions\council\sneaky.1"
DEFAULT_LABDIR = r"C:\Users\Administrator\Documents\NinjaTrader 8\Sneaky\lab"
DEFAULT_TOOLDIR = r"C:\ntbv"


def log(msg):
    ts = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    print(f"[{ts}] {msg}")


def run_step(name, cmd, cwd=None):
    log(f"-- {name} " + "-" * (60 - len(name)))
    try:
        result = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, timeout=600)
    except subprocess.TimeoutExpired:
        log(f"{name}: TIMED OUT after 600s -- skipping this cycle's remaining steps")
        return False
    except OSError as ex:
        log(f"{name}: FAILED TO START -- {ex}")
        return False

    if result.stdout.strip():
        for line in result.stdout.strip().splitlines():
            print(f"    {line}")
    if result.returncode != 0:
        log(f"{name}: exited with code {result.returncode}")
        if result.stderr.strip():
            for line in result.stderr.strip().splitlines()[-15:]:
                print(f"    STDERR: {line}")
        return False
    return True


def run_cycle(args):
    log("=" * 70)
    log("sync_lab.py cycle starting")

    ok1 = run_step(
        "translate (sneaky_to_lab.py)",
        [sys.executable, os.path.join(args.tooldir, "sneaky_to_lab.py"),
         "--src", args.recorder, "--dst", args.rowdst],
    )
    if not ok1:
        log("Translation step failed -- skipping ingest this cycle (will retry next cycle).")
        return

    ingest_py = os.path.join(args.labdir, "ingest", "ingest.py")
    ok2 = run_step(
        "ingest (ingest.py)",
        [sys.executable, ingest_py],
        cwd=os.path.join(args.labdir, "ingest"),
    )
    if not ok2:
        log("Ingest step failed this cycle -- will retry next cycle.")
        return

    log("Cycle complete.")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--recorder", default=DEFAULT_RECORDER, help="real SneakyRecorder folder")
    ap.add_argument("--rowdst", default=DEFAULT_ROWDST, help="translated-rows output folder (sneaky.1)")
    ap.add_argument("--labdir", default=DEFAULT_LABDIR, help="the lab/ folder itself")
    ap.add_argument("--tooldir", default=DEFAULT_TOOLDIR, help="folder holding sneaky_to_lab.py")
    ap.add_argument("--once", action="store_true", help="run a single cycle and exit (default)")
    ap.add_argument("--loop", type=int, default=None, metavar="SECONDS",
                     help="run continuously, sleeping SECONDS between cycles, until Ctrl+C")
    args = ap.parse_args()

    if args.loop:
        log(f"Looping every {args.loop}s. Ctrl+C to stop.")
        try:
            while True:
                run_cycle(args)
                log(f"Sleeping {args.loop}s until next cycle...")
                time.sleep(args.loop)
        except KeyboardInterrupt:
            log("Stopped.")
        return

    run_cycle(args)

    if not args.loop:
        print()
        print("=" * 70)
        print("For TRUE background automation (recommended over --loop), set this up")
        print("in Windows Task Scheduler instead of leaving a window open:")
        print("  Program/script:  python")
        print(f"  Add arguments:   \"{os.path.abspath(__file__)}\" --once")
        print("  Start in:        (leave blank, or the folder holding this script)")
        print("  Trigger:         Daily, repeat task every 15 minutes (or whatever interval you want)")
        print("  'Run whether user is logged on or not' if you want it to keep going unattended.")


if __name__ == "__main__":
    main()
