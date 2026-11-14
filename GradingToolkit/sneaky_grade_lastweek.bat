@echo off
setlocal EnableDelayedExpansion

:: ============================================================================
:: EDIT ME FIRST (new setup) -- around line 17-19 below:
::   set PYDIR=C:\ntbv
::   set RECDIR="C:\Users\Administrator\Documents\NinjaTrader 8\Sneaky\Recorder\rec-1"
::   set EXCURSIONS_DIR="C:\Users\Administrator\Documents\NinjaTrader 8\Sneaky\Excursions"
:: PYDIR should point at wherever you put this GradingToolkit folder. RECDIR/
:: EXCURSIONS_DIR should point at <your Documents>\NinjaTrader 8\Sneaky\Recorder\rec-1
:: and \Sneaky\Excursions -- see SETUP_GRADING.md.
:: Everything else below this banner (including the date-math workaround further down)
:: is original, tested logic -- do not need to touch it.
:: ============================================================================

:: ============================================================================
:: SNEAKY GRADE -- LAST 7 DAYS
:: Runs grade_sneaky.py (the real C:\ntbv one, same as sneaky_full_check.bat's
:: sections 3a/3c/4a-4c/6a/7b) filtered to just the trailing 7 days instead of
:: a fixed historical corpus slice. Uses --after with a date computed at
:: run-time, so re-running this later always means "the last 7 days as of
:: whenever you run it" -- not a fixed date baked in today.
::
:: Uses --excursion-folder like section 3c does: real ExcursionRecorder
:: maxMFE/maxMAE for the TP/STOP OPTIMIZATION section, not the plain-Recorder
:: approximation. More accurate, same reason 3c exists in the full check.
:: ============================================================================

set PYDIR=C:\ntbv
set RECDIR="C:\Users\Administrator\Documents\NinjaTrader 8\Sneaky\Recorder\rec-1"
set EXCURSIONS_DIR="C:\Users\Administrator\Documents\NinjaTrader 8\Sneaky\Excursions"

:: 2026-09-12 FIX: the first version of this line used single quotes for BOTH
:: the outer for/f command delimiter AND the inner PowerShell -Format string
:: ('yyyy-MM-dd') -- the inner quote closed the for/f command early and SINCE
:: came out as the single stray character "+", which grade_sneaky.py's --after
:: then compared against every row's timestamp and (silently) matched nothing,
:: so it dropped 0 rows and graded the ENTIRE corpus instead of the last week.
:: Fixed by using "usebackq" so backticks delimit the command instead, leaving
:: single AND double quotes free to use normally inside it.
set SINCE=
for /f usebackq %%i in (`powershell -NoProfile -Command "(Get-Date).AddDays(-7).ToString('yyyy-MM-dd')"`) do set "SINCE=%%i"

:: Sanity check -- refuse to silently grade the wrong thing a second time.
:: A real date looks like YYYY-MM-DD (10 chars, dashes at 5/8). Anything else
:: means the date computation broke again; stop instead of guessing.
set SINCE_OK=1
if not "!SINCE:~4,1!"=="-" set SINCE_OK=0
if not "!SINCE:~7,1!"=="-" set SINCE_OK=0
if "!SINCE!"=="" set SINCE_OK=0
if "!SINCE_OK!"=="0" (
    echo.
    echo ============================================================================
    echo  ERROR: could not compute "7 days ago" as a real date -- got: "%SINCE%"
    echo  Refusing to run grade_sneaky.py, since an unrecognized --after value gets
    echo  silently ignored ^(matches nothing, filters out 0 rows^) instead of erroring,
    echo  which is exactly the bug this script hit the first time.
    echo ============================================================================
    pause
    goto :eof
)

set LOGFILE_STAMP=
for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set LOGFILE_STAMP=%%i
set LOGFILE=sneaky_grade_lastweek_%LOGFILE_STAMP%.txt

cd /d "%PYDIR%"

echo ============================================================================ > "%LOGFILE%"
echo  SNEAKY GRADE -- LAST 7 DAYS  ^(rows on/after %SINCE%^)  --  %DATE% %TIME% >> "%LOGFILE%"
echo  Recorder folder: %RECDIR% >> "%LOGFILE%"
echo  Excursions folder: %EXCURSIONS_DIR% >> "%LOGFILE%"
echo ============================================================================ >> "%LOGFILE%"
echo. >> "%LOGFILE%"

echo Grading the last 7 days (on/after %SINCE%), writing to %LOGFILE% ...
echo.

echo === GRADE -- last 7 days, 4v1x1, real Excursion data for TP/stop === >> "%LOGFILE%"
python "%PYDIR%\grade_sneaky.py" %RECDIR% --bartype 4v1x1 --after %SINCE% --excursion-folder %EXCURSIONS_DIR% >> "%LOGFILE%" 2>&1
powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%'"

echo.
echo ============================================================================
echo  Done. Full output saved to %PYDIR%\%LOGFILE%
echo ============================================================================
pause
