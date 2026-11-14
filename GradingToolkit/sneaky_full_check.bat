@echo off
setlocal EnableDelayedExpansion

:: ============================================================================
:: EDIT ME FIRST (new setup) -- three lines around line 61-63 below are
:: hardcoded to sneaky_zekey's own machine and MUST be changed before this
:: will do anything useful on yours:
::   set RECDIR="C:\Users\Administrator\Documents\NinjaTrader 8\Sneaky\Recorder\rec-1"
::   set EXCURSIONS_DIR="C:\Users\Administrator\Documents\NinjaTrader 8\Sneaky\Excursions"
::   set PYDIR=C:\ntbv
:: RECDIR/EXCURSIONS_DIR should point at <your Documents>\NinjaTrader 8\Sneaky\Recorder\rec-1
:: and \Sneaky\Excursions (Recorder/ExcursionRecorder write there automatically — see
:: SETUP_GRADING.md). PYDIR should point at wherever you put this GradingToolkit folder.
:: Everything else below this banner is original logic -- do not need to touch it.
:: ============================================================================

:: ============================================================================
:: SINGLE-INSTANCE GUARD (2026-08-16) -- a real, reproduced-live bug: TWO
:: copies of this script running at once end up sharing the SAME output
:: filenames. LOGFILE_STAMP is only second-resolution and %RANDOM% is a
:: known cmd.exe gotcha -- two cmd.exe processes started within the same
:: clock tick can derive the identical seed and therefore the identical
:: %RANDOM% value, giving both instances byte-identical LOGFILE/STEPFILE/
:: TRACEFILE names. Confirmed via a live instrumented trace (2026-08-16):
:: sections 4b/4c/5a-5e vanished from the log (their content got clobbered
:: by the other instance's concurrent writes to the same shared files) and
:: "DONE"/"Finished" printed TWICE in one physical LOGFILE. mkdir is atomic
:: on Windows -- a second instance's mkdir fails immediately if the lock dir
:: already exists, so it exits cleanly instead of racing the first instance.
:: Mirrors the guard ingest.py already uses (GUARD_PORT / socket bind) for
:: the identical class of problem, just via mkdir since batch has no easy
:: socket bind. Lock is released down at :done, whichever path gets there.
:: ============================================================================
set LOCKDIR=%TEMP%\sneaky_full_check.lock
mkdir "%LOCKDIR%" 2>nul
if errorlevel 1 (
    echo.
    echo ============================================================================
    echo  Another instance of this check appears to already be running.
    echo  Lock: %LOCKDIR%
    echo  If you're SURE nothing else is running ^(e.g. a previous run crashed
    echo  without cleaning up^), delete that folder and re-run.
    echo ============================================================================
    pause
    goto :eof
)

:: ============================================================================
:: SNEAKY FULL CHECK
:: Runs every script in the Python toolkit against the full Recorder corpus,
:: then per-instance (D/E/F), then before/after slices around known changes.
:: ============================================================================
::
:: EDIT THE VALUES BELOW BEFORE RUNNING:
::
::   RECDIR               your Recorder folder
::   EXCURSIONS_DIR        your Excursions folder (2026-08-09 -- new. Points
::                         grade_sneaky.py's --excursion-folder at real
::                         ExcursionRecorder data for section 3c's TP/stop
::                         optimization, instead of the older approximation.
::                         Safe to point at the whole Excursions folder --
::                         grade_sneaky.py already filters to kind=="excursion"
::                         rows itself, so the tick-sidecar files under
::                         council\ticks\ get correctly ignored here even
::                         though they live in the same tree.)
::   PYDIR                 where the .py scripts live
::   NEWEST_CHANGES_DATE    the date you actually recompile+restart with
::                         tonight's newest changes -- see the updated note
::                         at section 7 below for what "tonight" now covers.
::                         Leave it blank until you know the real date --
::                         section 7 auto-skips until you fill this in.
:: ============================================================================

set RECDIR="C:\Users\Administrator\Documents\NinjaTrader 8\Sneaky\Recorder\rec-1"
set EXCURSIONS_DIR="C:\Users\Administrator\Documents\NinjaTrader 8\Sneaky\Excursions"
set PYDIR=C:\ntbv

:: Confirmed from tonight's own log analysis: the lane-registration fix
:: (AssertLane in Council/Confluence) was confirmed working in a session that
:: started 2026-08-04 20:02:50, with all lanes locked by ~20:05:22 that same
:: session. This is the one date in this whole file backed by an actual log
:: timestamp, not a guess -- use it as the "known-good" cutoff for anything
:: lane-related. Rows before this date predate BOTH the lane fix and the
:: "scope" field Recorder now writes, so --after here is doing real work
:: even though train_council_weights.py's own --require-lane-ok can't (it
:: has nothing to check on rows that old).
set LANE_FIX_DATE=2026-08-04

:: EDIT THIS the day you actually recompile with the newest NinjaScript
:: changes -- there have been several rounds since LANE_FIX_DATE, most
:: recently (2026-08-09): the news veto (VetoNews/BlockInNewsLockout), ARCH/
:: FLUX/TrendFusion wired into Confluence's gates, NoteSignalFire feeding
:: Confluence's own signals into Excursions tracking for the first time, and
:: ExcursionRecorder now writing a full Confluence decision snapshot
:: (cnfBias + all 22 gates) on a Confluence-sourced fire, not just Council's.
:: Format: YYYY-MM-DD (matches --after's expected format everywhere).
:: 2026-08-10 -- set to 2026-08-09, matching the date named in the comment
:: above (news veto, ARCH/FLUX/TrendFusion gates, NoteSignalFire, full
:: Confluence Excursion snapshots). UNLIKE LANE_FIX_DATE above, this date is
:: NOT backed by an actual log timestamp of when you recompiled+restarted --
:: it's just the date the comment says the changes were made. Confirm this
:: is really the date you recompiled and restarted NinjaTrader with them
:: live, not just the date they were written -- if the recompile happened
:: later, section 7's "before" bucket will still contain some post-change
:: rows and understate the real difference.
set NEWEST_CHANGES_DATE=2026-08-09

set LOGFILE_STAMP=
for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set LOGFILE_STAMP=%%i
set LOGFILE=sneaky_full_check_%LOGFILE_STAMP%.txt

echo ============================================================================ > "%LOGFILE%"
echo  SNEAKY FULL CHECK -- %DATE% %TIME% >> "%LOGFILE%"
echo  Recorder folder: %RECDIR% >> "%LOGFILE%"
echo  Excursions folder: %EXCURSIONS_DIR% >> "%LOGFILE%"
echo ============================================================================ >> "%LOGFILE%"
echo. >> "%LOGFILE%"

echo Running full check, writing to %LOGFILE% ...
echo (this also prints to screen as it goes)
echo.

:: ---------------------------------------------------------------------------
:: 1. RECORDER FILE / VERSION SANITY
:: ---------------------------------------------------------------------------
echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  1. RECORDER VERSION CHECK >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 1. RECORDER VERSION CHECK ===
python "%PYDIR%\check_recorder_version.py" %RECDIR% >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

:: ---------------------------------------------------------------------------
:: 2. MARKET HOURS SANITY (v2 -- hourly + per-Sunday-date breakdown)
:: ---------------------------------------------------------------------------
echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  2. MARKET HOURS CHECK >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 2. MARKET HOURS CHECK ===
python "%PYDIR%\check_market_hours_v2.py" %RECDIR% >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

:: ---------------------------------------------------------------------------
:: 3. FULL-CORPUS GRADING -- both recorded bar types
:: ---------------------------------------------------------------------------
echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  3a. GRADE -- full corpus, 4v1x1 (1-Minute) >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 3a. GRADE -- full corpus, 4v1x1 (1-Minute) ===
python "%PYDIR%\grade_sneaky.py" %RECDIR% --bartype 4v1x1 --min-n 30 >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  3b. GRADE -- full corpus, 4v5x1 (5-Minute) >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 3b. GRADE -- full corpus, 4v5x1 (5-Minute) ===
python "%PYDIR%\grade_sneaky.py" %RECDIR% --bartype 4v5x1 --min-n 30 >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

:: 2026-08-09 -- NEW. Same as 3a, but section 20 (TP/STOP OPTIMIZATION) now
:: reads ExcursionRecorder's REAL maxMFE/maxMAE via --excursion-folder instead
:: of approximating from Recorder's own forward-window fields. Real
:: tape-fillable prices, real tick-true first-touch outcomes -- a genuinely
:: different, more accurate number than 3a's TP/stop section for the same
:: corpus. Compare the two if you want to see how much the approximation was
:: off by.
echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  3c. GRADE -- full corpus, 4v1x1, REAL Excursion data for TP/stop >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 3c. GRADE -- full corpus, 4v1x1, REAL Excursion data for TP/stop ===
python "%PYDIR%\grade_sneaky.py" %RECDIR% --bartype 4v1x1 --min-n 30 --excursion-folder %EXCURSIONS_DIR% >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

:: ---------------------------------------------------------------------------
:: 4. PER-INSTANCE GRADING -- D, E, F
:: ---------------------------------------------------------------------------
:: 2026-08-09 -- UPDATED NOTE: the old note here (from before that date) said
:: Confluence had no instance tag and --instance would silently read 0 rows
:: for a Confluence-only chart like D. That's no longer true -- Recorder now
:: writes cnfInstance (mirroring cnlInstance), and --instance below already
:: checks BOTH fields (confirmed directly in grade_sneaky.py's own source).
:: D should now show real, isolated Confluence rows here, same as E/F show
:: real Council rows.
:: ---------------------------------------------------------------------------
echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  4a. GRADE -- instance D (4v1x1) >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 4a. GRADE -- instance D (4v1x1) ===
python "%PYDIR%\grade_sneaky.py" %RECDIR% --bartype 4v1x1 --instance D >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  4b. GRADE -- instance E (4v1x1) >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 4b. GRADE -- instance E (4v1x1) ===
python "%PYDIR%\grade_sneaky.py" %RECDIR% --bartype 4v1x1 --instance E >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  4c. GRADE -- instance F (4v1x1) >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 4c. GRADE -- instance F (4v1x1) ===
python "%PYDIR%\grade_sneaky.py" %RECDIR% --bartype 4v1x1 --instance F >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

:: ---------------------------------------------------------------------------
:: 5. WEIGHT FITTING / GATE AUDIT / STANDALONE PREDICTOR
:: ---------------------------------------------------------------------------
echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  5a. TRAIN COUNCIL WEIGHTS -- full corpus, lane-filtered >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 5a. TRAIN COUNCIL WEIGHTS -- full corpus, lane-filtered ===
python "%PYDIR%\train_council_weights.py" %RECDIR% --bartype 4v1x1 --horizon 20 --require-lane-ok --uniqueness-weights >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

:: 2026-08-09 -- train_predictor.py's own FIELDS list gained 7 real gate
:: columns tonight (cnfFluxOk/cnfTfusOk/cnfArchOk/cnfNewsOk/cnfProfOverextOk,
:: then cnfOptOk/cnfChopOk) -- it was silently missing them before, despite
:: its own docstring promising every field Recorder captures. Same command as
:: always; the script itself is just genuinely more complete now.
echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  5b. TRAIN PREDICTOR -- full corpus, lane-filtered >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 5b. TRAIN PREDICTOR -- full corpus, lane-filtered ===
python "%PYDIR%\train_predictor.py" %RECDIR% --bartype 4v1x1 --horizon 20 --require-lane-ok >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  5c. TRAIN COUNCIL WEIGHTS -- instance E only >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 5c. TRAIN COUNCIL WEIGHTS -- instance E only ===
python "%PYDIR%\train_council_weights.py" %RECDIR% --bartype 4v1x1 --horizon 20 --instance E --require-lane-ok >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  5d. TRAIN COUNCIL WEIGHTS -- instance F only >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 5d. TRAIN COUNCIL WEIGHTS -- instance F only ===
python "%PYDIR%\train_council_weights.py" %RECDIR% --bartype 4v1x1 --horizon 20 --instance F --require-lane-ok >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

:: 2026-08-09 -- NEW SCRIPT. Confluence is a gate-cascade, not a weighted
:: vote, so this doesn't fit weights like 5a -- it audits whether each gate's
:: own True/False split actually correlates with a better forward outcome,
:: checked for stability across the same purged walk-forward folds 5a/5b use.
echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  5e. TRAIN CONFLUENCE -- gate audit, full corpus >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 5e. TRAIN CONFLUENCE -- gate audit, full corpus ===
python "%PYDIR%\train_confluence.py" %RECDIR% --bartype 4v1x1 --horizon 20 --require-lane-ok >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  5f. TRAIN CONFLUENCE -- gate audit, instance D (now isolable -- see section 4 note) >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 5f. TRAIN CONFLUENCE -- gate audit, instance D (now isolable -- see section 4 note) ===
python "%PYDIR%\train_confluence.py" %RECDIR% --bartype 4v1x1 --horizon 20 --instance D --require-lane-ok >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

:: ---------------------------------------------------------------------------
:: 6. BEFORE / AFTER -- the lane-registration fix (LANE_FIX_DATE, confirmed)
:: ---------------------------------------------------------------------------
:: grade_sneaky.py has no --require-lane-ok flag (only the train_*.py scripts
:: do), so the "before" comparison here is just the full corpus from section
:: 3a, which already includes everything before LANE_FIX_DATE since almost
:: all of it predates the fix. Compare that against 6a below.
:: ---------------------------------------------------------------------------
echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  6a. GRADE -- AFTER lane fix only (rows recorded on/after %LANE_FIX_DATE%) >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 6a. GRADE -- AFTER lane fix only (rows recorded on/after %LANE_FIX_DATE%) ===
python "%PYDIR%\grade_sneaky.py" %RECDIR% --bartype 4v1x1 --after %LANE_FIX_DATE% >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  6b. TRAIN COUNCIL WEIGHTS -- AFTER lane fix, lane-filtered >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 6b. TRAIN COUNCIL WEIGHTS -- AFTER lane fix, lane-filtered ===
python "%PYDIR%\train_council_weights.py" %RECDIR% --bartype 4v1x1 --horizon 20 --after %LANE_FIX_DATE% --require-lane-ok --uniqueness-weights >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

:: ---------------------------------------------------------------------------
:: 7. BEFORE / AFTER -- tonight's newest changes (edit NEWEST_CHANGES_DATE above)
:: ---------------------------------------------------------------------------
if "%NEWEST_CHANGES_DATE%"=="" (
    echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  7. SKIPPED >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 7. SKIPPED ===
    echo   NEWEST_CHANGES_DATE is not set at the top of this file. >> "%LOGFILE%"
    echo   Edit it once you've recompiled with the newest NinjaScript changes >> "%LOGFILE%"
    echo   (see the comment above NEWEST_CHANGES_DATE for what that covers), >> "%LOGFILE%"
    echo   then re-run this script to get the before/after comparison. >> "%LOGFILE%"
    powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"
    goto :done
)

echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  7a. BEFORE newest changes = SAME AS 6a -- not re-run, see note >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 7a. BEFORE newest changes = SAME AS 6a -- not re-run, see note ===
echo   Section 6a above already IS this "before" baseline: it's the identical >> "%LOGFILE%"
echo   grade_sneaky.py --after %LANE_FIX_DATE% call, same corpus, same output. >> "%LOGFILE%"
echo   Re-running it here would just burn a second full pass to print the same >> "%LOGFILE%"
echo   numbers again -- skipped on purpose. Scroll up to 6a and read it as >> "%LOGFILE%"
echo   "before" only relative to 7b below. >> "%LOGFILE%"
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  7b. GRADE -- AFTER newest changes (rows on/after %NEWEST_CHANGES_DATE%) >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 7b. GRADE -- AFTER newest changes (rows on/after %NEWEST_CHANGES_DATE%) ===
python "%PYDIR%\grade_sneaky.py" %RECDIR% --bartype 4v1x1 --after %NEWEST_CHANGES_DATE% >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  7c. TRAIN COUNCIL WEIGHTS -- AFTER newest changes only, lane-filtered >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 7c. TRAIN COUNCIL WEIGHTS -- AFTER newest changes only, lane-filtered ===
python "%PYDIR%\train_council_weights.py" %RECDIR% --bartype 4v1x1 --horizon 20 --after %NEWEST_CHANGES_DATE% --require-lane-ok --uniqueness-weights >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

:: 2026-08-09 -- NEW. train_confluence.py's own before/after -- given the news
:: veto + ARCH/FLUX/TrendFusion gates are part of what "newest changes" means
:: now, this is worth its own comparison, not just Council's.
echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  7d. TRAIN CONFLUENCE -- AFTER newest changes only, lane-filtered >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === 7d. TRAIN CONFLUENCE -- AFTER newest changes only, lane-filtered ===
python "%PYDIR%\train_confluence.py" %RECDIR% --bartype 4v1x1 --horizon 20 --after %NEWEST_CHANGES_DATE% --require-lane-ok >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"

:done
echo. >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo  DONE >> "%LOGFILE%"
echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
echo.
echo === DONE ===
echo   Finished %DATE% %TIME% >> "%LOGFILE%"
echo   Full output saved to: %LOGFILE% >> "%LOGFILE%"
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 40"
rmdir "%LOCKDIR%" 2>nul

echo.
echo ============================================================================
echo  Done. Full output saved to %LOGFILE%
echo ============================================================================
pause
goto :eof
