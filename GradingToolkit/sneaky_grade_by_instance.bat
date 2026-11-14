@echo off
setlocal EnableDelayedExpansion

:: ============================================================================
:: EDIT ME FIRST (new setup) -- around line 22-23 below:
::   set PYDIR=C:\ntbv
::   set RECDIR="C:\Users\Administrator\Documents\NinjaTrader 8\Sneaky\Recorder\rec-1"
:: PYDIR should point at wherever you put this GradingToolkit folder. RECDIR should
:: point at <your Documents>\NinjaTrader 8\Sneaky\Recorder\rec-1 (where Recorder
:: writes automatically -- see SETUP_GRADING.md).
:: Also around line 40: "for %%I in (D E F learned1 learned2) do (" -- that list is
:: sneaky_zekey's own Council InstanceTag names. Replace it with YOUR OWN InstanceTag
:: values (whatever you set Council's "Instance Tag" property to on each chart).
:: Everything else below this banner is original logic -- no need to touch it.
:: ============================================================================

:: ============================================================================
:: SNEAKY GRADE -- BY INSTANCE (D, E, F, learned1, learned2)
:: ============================================================================
:: sneaky_full_check.bat only ever grades D/E/F (sections 4a-4c) -- it was
:: never updated to cover the learned1/learned2 chart instances that also
:: exist in the Recorder data now. This runs grade_sneaky.py separately for
:: all FIVE current instances, over the FULL history (no --after), so table 1
:: (COUNCIL CONVICTION) can be compared side by side per-instance instead of
:: only ever seeing it pooled across everything.
::
:: WHY THIS MATTERS: if one or more instances are deliberately set up to run
:: an inverted/contrarian read of the panel, pooling them together with a
:: "normal" instance can make conviction look backwards in the pooled report
:: even if it's actually fine on the normal instance(s) -- or can hide a real
:: problem on the normal instance behind a contrarian one that looks "fine"
:: by design. This is the only way to tell those two situations apart.
:: ============================================================================

set PYDIR=C:\ntbv
set RECDIR="C:\Users\Administrator\Documents\NinjaTrader 8\Sneaky\Recorder\rec-1"

set LOGFILE_STAMP=
for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set LOGFILE_STAMP=%%i
set LOGFILE=sneaky_grade_by_instance_%LOGFILE_STAMP%.txt

cd /d "%PYDIR%"

echo ============================================================================ > "%LOGFILE%"
echo  SNEAKY GRADE -- BY INSTANCE (full history, 4v1x1)  --  %DATE% %TIME% >> "%LOGFILE%"
echo  Recorder folder: %RECDIR% >> "%LOGFILE%"
echo ============================================================================ >> "%LOGFILE%"
echo. >> "%LOGFILE%"

echo Grading each instance separately, writing to %LOGFILE% ...
echo.

for %%I in (D E F learned1 learned2) do (
    echo. >> "%LOGFILE%"
    echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
    echo  INSTANCE %%I >> "%LOGFILE%"
    echo ---------------------------------------------------------------------------- >> "%LOGFILE%"
    echo.
    echo === INSTANCE %%I ===
    python "%PYDIR%\grade_sneaky.py" %RECDIR% --bartype 4v1x1 --min-n 30 --instance %%I >> "%LOGFILE%" 2>&1
    powershell -NoProfile -Command "Get-Content -Path '%LOGFILE%' -Tail 30"
)

echo.
echo ============================================================================
echo  Done. Full output saved to %PYDIR%\%LOGFILE%
echo ============================================================================
pause
