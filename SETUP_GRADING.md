# Setting up the trade-grading system (Council / Confluence / Recorder)

This covers the full "grading" stack that sits behind Council and Confluence:
recording every bar's sensor votes to disk, then running the Python toolkit
in [`GradingToolkit/`](GradingToolkit/) against those recordings to grade
performance, check for bugs, and (optionally) train new weight profiles.

This is a genuinely complex system with several moving parts that all have
to line up. Read this whole document before wiring anything up — most of the
problems people hit come from skipping one step, not from the code itself.

## 1. What's already in this repo

Everything Council and Confluence need to **compile and run** is already in
[`Indicators/`](Indicators/) and [`AddOns/`](AddOns/) — you don't need
anything else from outside this repo for that part:

- `SneakyCore_v1_0_0.cs`, `SneakyCore.Foundation.cs`, `SneakyCore.Safety.cs`,
  `SneakyCore.SystemBuilder.cs` (AddOns) — the shared framework every Sneaky
  tool is built on. Compile these first.
- `SneakyCouncil_v1_0_0.cs`, `SneakyConfluence_v1_0_0.cs` (Indicators) — the
  two fuser indicators themselves.
- `SneakyRecorder_v1_0_0.cs`, `SneakyExcursionRecorder_v2_0_0.cs`
  (Indicators) — the two recorders that write the data the grading scripts
  read. **These are separate indicators you add to a chart alongside
  Council/Confluence** — they are not switches on Council/Confluence
  themselves.
- Every voter indicator Council can fuse (ADX, GREV, WAE, VDYA, PRS, IMKT,
  TRND, EXH, FLOW, PSAR, ARCH, SPRT, STF, AVMA, COMP, CCI, TFUS, STRC, ZSC,
  VENV, and the rest) — they're all in `Indicators/`.

NinjaTrader will tell you if anything's missing when you compile (NinjaScript
Editor → Compile, or F5), via an unresolved-reference error naming the
missing type.

## 2. What's new: the Python grading toolkit

[`GradingToolkit/`](GradingToolkit/) is new in this repo — it's
sneaky_zekey's own Python scripts for grading, bug-checking, and (re)training
weight profiles against whatever the Recorder/ExcursionRecorder indicators
write to disk. Put the whole folder somewhere on your machine (e.g.
`C:\GradingToolkit`) and install Python 3 + the usual data-science stack
(`pandas`, `numpy` at minimum — `pip install pandas numpy`, add more if a
script complains about an import).

**The three `.bat` files in that folder have hardcoded paths from
sneaky_zekey's own machine at the top — each now has an "EDIT ME FIRST"
banner comment telling you exactly which lines to change.** Nothing else in
them needs editing; the logic below the banner is tested as-is.

| File | What it does |
|---|---|
| `sneaky_full_check.bat` | Runs the full health/bug check sweep across recorder output. |
| `sneaky_grade_by_instance.bat` | Grades each Council `InstanceTag` separately (edit the instance-name list to match yours). |
| `sneaky_grade_lastweek.bat` | Grades just the last 7 days of recordings. |

Core scripts you'll actually call (directly, or via the `.bat` files above):

- `grade_sneaky.py` — the main grading script: reads recorder JSONL, scores
  how the votes/fusion would have performed.
- `check_recorder_version.py`, `check_market_hours_v2.py`,
  `check_conviction.py`, `check_no_votes.py` — sanity/bug checks on recorder
  output (stale schema, bars recorded outside market hours, Council firing
  with weak conviction, rows with no vote vector at all).
- `train_council_weights.py`, `train_confluence.py`, `train_predictor.py` —
  (re)train weight profiles from recorded data. **Optional, advanced** — see
  the caveat in section 5.
- `sneaky_ml_common.py` — shared helpers the above scripts import; not run
  directly.
- `sneaky_to_lab.py`, `sync_lab.py` — move/organize recorder output into a
  longer-term "lab" archive folder.
- `archive_old_recorder_files.py`, `verify_archive_candidates.py`,
  `inspect_one_file.py` — housekeeping: archive old recordings, sanity-check
  what's safe to archive, inspect a single JSONL file's contents.

Run any script with `-h` / `--help` for its actual argument list — several
scripts vary in the flags they accept.

## 3. The one rule that matters most: one Recorder per chart, unique tags

This is the part people get wrong most often, so it gets its own section.

**Every chart instance that runs Council or Confluence needs its own
`SneakyRecorder_v1_0_0` indicator added to that same chart.** Recorder
doesn't share across charts — if you have Council running on three different
charts (say, three instruments, or three timeframes), you need three
separate Recorder indicators, one per chart.

The same goes for `SneakyExcursionRecorder_v2_0_0` if you want MAE/MFE
(excursion) data as well — it's a separate indicator, added the same way,
one per chart.

**Give each chart's Council (and/or Confluence) a unique "Instance Tag"**
(a property on the indicator itself, in the Indicators dialog — it's an
empty string by default). Recorder reads whatever `InstanceTag` is set on
the Council/Confluence sharing its chart and stamps every row with it. If
two charts share the same tag (or both leave it blank), the grading scripts
can't tell their recordings apart — you'll see votes from two unrelated
setups blended into one "instance" when you grade. The instance names in
`sneaky_grade_by_instance.bat` (`D E F learned1 learned2`) are
sneaky_zekey's own tags, not universal defaults — pick your own and use
those, on both the chart properties and in the `.bat` file.

In short: **one Recorder instance per chart, one unique Instance Tag per
Council/Confluence instance you want to track separately.**

## 4. Where the data actually lands

You don't configure an output path — it's automatic, and it's the same on
every machine relative to your own NinjaTrader user folder:

- Recorder writes to:
  `<your Documents>\NinjaTrader 8\Sneaky\Recorder\rec-1\{timestamp}__{instrument}__{bar-type}__{random}.jsonl`
- ExcursionRecorder writes to:
  `<your Documents>\NinjaTrader 8\Sneaky\Excursions\council\<schema>\...`

So on a typical Windows setup that's
`C:\Users\<YourUsername>\Documents\NinjaTrader 8\Sneaky\Recorder\rec-1\` —
point `RECDIR` (and `EXCURSIONS_DIR`) in the `.bat` files, or the equivalent
argument on `grade_sneaky.py` directly, at that folder under *your* username,
not `Administrator`.

Recorder creates the folder itself on first write — if you don't see it yet,
run your chart for at least one live or historical bar first.

## 5. Honest caveat on the "Learned" weight profiles

If you go on to use `train_council_weights.py` / `train_confluence.py` /
`train_predictor.py` to produce a new weight profile: treat anything labeled
"Learned" as **experimental and thin-data**, not a validated edge. That's
sneaky_zekey's own standing finding from this project's internal validation
work (`voter_edge.py` / `sensor_truth.py`), not a guess — the data volume
behind any one learned profile is typically small relative to what you'd
want before trusting it live. Use it as a research input, backtest it
properly, and don't swap your live weights to a "Learned" profile just
because training produced one.

## 6. Quick-start checklist

1. Compile `SneakyCore*.cs`, then every `Indicators/*.cs` file you're using
   (Council, Confluence, every voter, Recorder, ExcursionRecorder).
2. On each chart: add Council and/or Confluence, set a unique Instance Tag,
   then add `SneakyRecorder_v1_0_0` (and `SneakyExcursionRecorder_v2_0_0` if
   you want excursion data) to the **same chart**.
3. Let it run for a while (live or on historical bars) so Recorder has
   something to write.
4. Install Python 3 + `pandas`/`numpy`, put `GradingToolkit/` somewhere on
   disk.
5. Edit the three lines each `.bat` file's "EDIT ME FIRST" banner calls out
   (your own `Documents\NinjaTrader 8\Sneaky\...` path, your own PYDIR, your
   own instance tag names).
6. Run `sneaky_grade_lastweek.bat` (or `grade_sneaky.py` directly) and read
   the output.
