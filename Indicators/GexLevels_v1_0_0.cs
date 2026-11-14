#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// GexLevels_v1_0_0 -- ORIGINAL indicator, built at the user's request for a "gamma/GEX
// levels on the chart" overlay. NinjaTrader has no options-chain access on a futures-only
// data feed (confirmed with the user -- their prop-firm futures feeds, e.g. Rithmic/
// Tradovate, carry no options data at all), so this indicator does NOT compute gamma
// exposure itself. It only READS a small CSV file and draws whatever levels are in it as
// horizontal lines + labels. The actual GEX/DEX math lives in a companion Python script,
// fetch_gex_nq.py, meant to run on the user's own trading PC (scheduled once a day
// pre-market via Windows Task Scheduler -- see setup_instructions.txt).
//
// 2026-09-25 -- v2 REWRITE. The user found a third-party NT8 GEX/DEX tool on YouTube
// ("OFK GEX NQ") with a much richer level taxonomy and asked to port as much of it as
// possible. Ported: DTE-bucketed level groups (Structural 49d+ / Intraday 0-7DTE / 0DTE),
// DEX D+/D- magnets, 0DTE Pin Strike / Max Pain / Charm Magnet, an Expected Move band, a
// Vanna Flip approximation, per-group visibility toggles, and right-anchored labels with a
// background box (their fix for label-on-line readability, cleaner than this file's earlier
// pixel-offset-only approach -- kept both). NOT ported: VIX regime, the "macro: Nm to next
// event" countdown, and scalping alerts -- all three need entirely separate data feeds this
// project doesn't have, left out rather than faked. See fetch_gex_nq.py's own header for
// the full data-source rewrite (CME-direct scrape replacing the old QQQ-scaled Yahoo feed)
// and for which level definitions are standard formulas vs. this project's own approximation
// of an unpublished proprietary term.
//
// FILE FORMAT (produced by fetch_gex_nq.py v2): a plain CSV with '#' comment lines
// (skipped), then a "Price,Label,Type,Group" header (skipped), then one row per level.
// Type is one of: CallWall/PutWall/ZeroGamma/DexPlus/DexMinus/PinStrike/MaxPain/
// CharmMagnet/ExpectedMove/VannaFlip/Secondary+/Secondary- -- anything else falls back to
// a neutral gray color and stays visible rather than being dropped, so a future script
// change that adds a new Type still renders instead of silently vanishing. Group is one of
// Structural/Intraday/ZeroDTE/Other and drives both the per-group visibility toggle and the
// line's dash style (a quick visual "which DTE bucket is this" cue without needing a
// separate color per bucket on top of per-TYPE colors).
//
// REFRESH DESIGN: unchanged from v1 -- a System.Timers.Timer polls the file's last-write
// time every RefreshCheckSeconds on its own thread pool thread and hands parsed results to
// OnBarUpdate (the engine thread) via a lock-protected field + a dirty flag. Draw.* calls
// happen only from OnBarUpdate, never from the timer callback directly.
//
// STALE-DATA WARNING: unchanged from v1 -- the on-chart status line shows the CSV's own
// generation timestamp and flags red/"STALE" once older than StaleHoursThreshold.
//
// 2026-09-26 -- v8 addition: intraday snapshot + replay scrubber, ported from the reference
// tool's own OFK_ReplayWindow.cs. A new "Intraday Replay" panel button opens a slider window
// (GexReplayWindow.cs, a sibling file in this same Indicators folder) that scrubs through
// today's 15-min snapshots -- written by a NEW, SEPARATE companion script,
// fetch_gex_nq_intraday.py, on its own Task Scheduler job (every 15 min, RTH only). Unlike the
// OFK reference (which recomputes GEX from a fresh options-chain fetch every 5 min), and per
// explicit user direction, this DOES re-run the same full CME-direct fetch each time (no
// yfinance involved -- that was retired in v4/v5 of fetch_gex_nq.py) rather than just
// recomputing off stale OI with a live spot -- see fetch_gex_nq_intraday.py's own header for
// why. replayMode freezes the normal live-file poll (CheckAndLoadFile) while scrubbing; the
// replay window's own "Resume LIVE" button (or closing it) restores it. See
// setup_instructions.txt for the new Task Scheduler job and folder setup.

namespace NinjaTrader.NinjaScript.Indicators
{
    public class GexLevels_v1_0_0 : Indicator
    {
        private struct GexLevel
        {
            public double Price;
            public string Label;
            public string Type;
            public string Group;
        }

        private System.Timers.Timer refreshTimer;
        private readonly object levelsLock = new object();
        private List<GexLevel> pendingLevels;
        private volatile bool levelsDirty;
        private DateTime lastFileWriteUtc = DateTime.MinValue;
        private bool lastFileWriteKnown;

        private List<GexLevel> activeLevels = new List<GexLevel>();
        private List<GexLevel> visibleLevels = new List<GexLevel>();
        private int lastDrawnCount;

        // Status-line fields, parsed from the CSV's own '#' comment lines -- purely
        // informational, shown so a stale/failed overnight fetch is visible on the chart
        // instead of silently going unnoticed.
        private string statusAsOf = "never";
        private string statusRegime = "";
        private bool statusStale = true;

        // v4 additions -- more '#' comment-line metrics from fetch_gex_nq.py, same
        // best-effort parse-or-blank pattern as statusRegime above.
        private string statusSkew = "";
        private string statusTerm = "";
        private string statusPcr = "";
        private string statusGammaZone = "";
        private string statusIvr = "";
        private string statusVix = "";

        // ── Floating panel (v6 addition) -- a separate draggable WPF window, modeled on
        // the reference tool's own floating panel (github.com/Kza56/OFK_Atas_GEX,
        // OFK_NQ_GEX_Levels.cs's OpenPanel/UpdatePanelText), reusing the SAME statusX
        // fields and activeLevels list this file already maintains for the on-chart
        // status line -- no new data, just a second, richer presentation of it.
        // NinjaScript indicators run OnBarUpdate/the timer callback off the UI thread, so
        // every touch of _panelWindow or its children goes through
        // Application.Current.Dispatcher, exactly like the reference tool does for ATAS
        // (both are plain WPF hosts under the hood, so the same pattern applies).
        private Window panelWindow;
        private bool panelOpen;
        private TextBlock panelInfoText;
        private TextBlock panelStatusText;
        private Button panelRefreshButton;
        private Process refreshProcess;
        private bool refreshRunning;

        // ── Briefing button (v7 addition) -- runs generate_briefing_nq.py (a separate
        // companion script, NOT fetch_gex_nq.py) which reads the CSV fetch_gex_nq.py just
        // wrote, sends it to the Anthropic API for an AI-generated trading thesis, and
        // renders GexBriefing_NQ.pdf. Same non-blocking subprocess pattern as
        // RunRefreshScript below (redirected streams, log file, Exited event on the process,
        // dispatcher-marshaled UI updates) -- see RunRefreshScript's own comment for why.
        private Button panelBriefingButton;
        private Button panelViewPdfButton;
        private Process briefingProcess;
        private bool briefingRunning;

        // ── Intraday replay (v8 addition) -- ported from the reference tool's own
        // OFK_ReplayWindow.cs (github.com/Kza56/OFK_Atas_GEX). A separate WPF window
        // (GexReplayWindow.cs, a sibling file in this same Indicators folder -- NinjaTrader
        // compiles every .cs file in the folder together, so no extra wiring is needed) with
        // a slider lets the user scrub through today's 15-min intraday snapshots, written by
        // a SEPARATE companion script (fetch_gex_nq_intraday.py, its own Task Scheduler job
        // -- NOT fetch_gex_nq.py, which stays the once-daily pre-market fetch). replayMode
        // freezes CheckAndLoadFile's normal live-file poll while scrubbing -- otherwise the
        // timer would silently overwrite whatever historical snapshot is on screen, exactly
        // the failure mode the reference tool's own _replayMode flag guards against.
        private GexReplayWindow replayWindow;
        private bool replayMode;
        private DateTime replayTimestamp = DateTime.MinValue;
        private Button panelReplayButton;

        // Draw tags are capped well above what any realistic level count produces (v2's
        // richer taxonomy can plausibly reach ~25-30 levels across all groups/buckets on a
        // busy day) -- purely a sanity ceiling against a malformed file producing an
        // unbounded draw list, same reasoning as v1's cap just raised to match the bigger
        // level set.
        private const int MaxDrawnLevels = 48;

        private static readonly SimpleFont levelLabelFont = new SimpleFont("Arial", 10) { Bold = true };
        private static readonly SimpleFont statusFont = new SimpleFont("Arial", 11) { Bold = true };

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "GexLevels_v1_0_0";
                Description = "Plots gamma/DEX levels (structural + intraday + 0DTE walls, DEX magnets, pin/max-pain/charm, expected move) read from a CSV produced by the companion fetch_gex_nq.py script. Does not compute GEX/DEX itself -- see file header comment.";
                Calculate = Calculate.OnBarClose;
                ZOrder = 0;       // keep below overlay panels (Kinetic panel etc.)
                IsOverlay = true;
                DisplayInDataBox = false;

                // NinjaTrader.Core.Globals.UserDataDir (NOT a hardcoded Documents\NinjaTrader 8
                // path) -- resolves correctly even when a user's NinjaTrader user-data folder
                // has been relocated off the Documents default. fetch_gex_nq.py (plain Python,
                // no NinjaTrader API access) can't resolve this itself, so its OUTPUT_PATH
                // constant must be kept pointed at the SAME resolved folder by hand -- see
                // setup_instructions.txt.
                GexFilePath = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexLevels_NQ.csv");
                RefreshCheckSeconds = 30;
                StaleHoursThreshold = 20;

                ShowStructural = true;
                ShowIntraday = true;
                ShowZeroDTE = true;
                ShowDex = true;
                ShowExpectedMove = true;
                ShowExtendedWalls = true;

                ShowLevelLabels = true;
                ShowStatusLine = true;
                LineWidth = 2;
                LabelPixelOffset = 6;
                LabelBackgroundOpacity = 65;

                CallWallBrush = Brushes.Lime;
                PutWallBrush = Brushes.Red;
                ZeroGammaBrush = Brushes.Yellow;
                DexPlusBrush = Brushes.MediumPurple;
                DexMinusBrush = Brushes.DarkOrchid;
                ZeroDTEBrush = Brushes.Orange;
                ExpectedMoveBrush = Brushes.Gray;
                VannaFlipBrush = Brushes.Cyan;
                SecondaryBrush = Brushes.DodgerBlue;
                StructuralExtraBrush = Brushes.Goldenrod;
                TransitionBrush = Brushes.HotPink;
                LabelBackgroundBrush = Brushes.Black;

                ShowPanel = true;
                PythonPath = "python";
                // Sibling "GexScripts" folder next to the resolved NinjaTrader user-data
                // folder -- matches this user's actual layout (D:\NinjaTrader 8\GexScripts\
                // next to D:\NinjaTrader 8\, which is what UserDataDir resolves to here; see
                // the GexFilePath comment above for why UserDataDir and not a hardcoded
                // Documents path). Change it in the Properties panel if it ever moves.
                ScriptPath = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexScripts", "fetch_gex_nq.py");
                BriefingScriptPath = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexScripts", "generate_briefing_nq.py");
                // generate_briefing_nq.py's own DEFAULT_DIR/PDF_OUT_PATH constants write here
                // -- same UserDataDir root as GexFilePath above, kept in sync by hand (see
                // setup_instructions.txt) since the Python side can't resolve UserDataDir itself.
                BriefingPdfPath = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexBriefing_NQ.pdf");
                // fetch_gex_nq_intraday.py's own INTRADAY_DIR constant writes snapshot CSVs
                // here every 15 min during RTH -- same UserDataDir root as the paths above,
                // kept in sync by hand (see setup_instructions.txt) for the same reason.
                IntradaySnapshotsFolder = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexIntraday");
            }
            else if (State == State.DataLoaded)
            {
                // Multi-ticker: chart instrument picks the CSV (GexLevels_ES.csv for ES/MES, _YM for YM/MYM, else NQ).
                // Only swaps the DEFAULT file name, so a custom path you typed is left alone.
                activeTicker = TickerForInstrument(Instrument != null ? Instrument.MasterInstrument.Name : "NQ");
                if (activeTicker != "NQ" && !string.IsNullOrEmpty(GexFilePath) && GexFilePath.EndsWith("GexLevels_NQ.csv", StringComparison.OrdinalIgnoreCase))
                    GexFilePath = GexFilePath.Substring(0, GexFilePath.Length - "GexLevels_NQ.csv".Length) + "GexLevels_" + activeTicker + ".csv";
                lastFileWriteUtc = DateTime.MinValue;
                lastFileWriteKnown = false;
                statusAsOf = "never";
                statusRegime = "";
                statusStale = true;
                statusSkew = "";
                statusTerm = "";
                statusPcr = "";
                statusGammaZone = "";
                statusIvr = "";
                statusVix = "";
                replayMode = false;
                replayTimestamp = DateTime.MinValue;
            }
            else if (State == State.Historical)
            {
                // Timer is wall-clock-paced, independent of bar timing entirely -- the CSV
                // can land (or a new session can start) while the chart is idle, and this
                // still needs to pick it up without waiting for the next bar close.
                refreshTimer = new System.Timers.Timer(Math.Max(5, RefreshCheckSeconds) * 1000.0);
                refreshTimer.Elapsed += OnRefreshTimerElapsed;
                refreshTimer.AutoReset = true;
                refreshTimer.Start();

                // Check once immediately on attach rather than waiting a full
                // RefreshCheckSeconds for the first read.
                CheckAndLoadFile();

                if (ShowPanel)
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(OpenPanel));
            }
            else if (State == State.Terminated)
            {
                if (refreshTimer != null)
                {
                    refreshTimer.Stop();
                    refreshTimer.Elapsed -= OnRefreshTimerElapsed;
                    refreshTimer.Dispose();
                    refreshTimer = null;
                }

                if (refreshProcess != null)
                {
                    try { if (!refreshProcess.HasExited) refreshProcess.Kill(); } catch { }
                    try { refreshProcess.Dispose(); } catch { }
                    refreshProcess = null;
                }

                if (briefingProcess != null)
                {
                    try { if (!briefingProcess.HasExited) briefingProcess.Kill(); } catch { }
                    try { briefingProcess.Dispose(); } catch { }
                    briefingProcess = null;
                }

                Application.Current?.Dispatcher?.BeginInvoke(new Action(ClosePanel));
                Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    try { replayWindow?.Close(); } catch { }
                    replayWindow = null;
                }));
            }
        }

        private void OnRefreshTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            CheckAndLoadFile();
        }

        /// <summary>
        /// Runs on the timer's own thread pool thread -- file I/O and parsing only, no
        /// Draw.* calls here (those must only ever happen from OnBarUpdate/the engine
        /// thread). Hands the parsed result to OnBarUpdate via pendingLevels + levelsDirty.
        /// </summary>
        private void CheckAndLoadFile()
        {
            // v8: frozen while the intraday-replay window is scrubbing a historical
            // snapshot -- otherwise this timer (still running underneath) would silently
            // overwrite the replay view with the live file every RefreshCheckSeconds.
            if (replayMode)
                return;

            try
            {
                if (string.IsNullOrWhiteSpace(GexFilePath) || !File.Exists(GexFilePath))
                    return;

                DateTime writeUtc = File.GetLastWriteTimeUtc(GexFilePath);
                if (lastFileWriteKnown && writeUtc == lastFileWriteUtc)
                    return; // unchanged since last read -- nothing to do

                List<GexLevel> parsedLevels;
                string parsedAsOf, parsedRegime, parsedSkew, parsedTerm, parsedPcr, parsedGammaZone, parsedIvr, parsedVix;
                if (!ParseGexCsv(GexFilePath, out parsedLevels, out parsedAsOf, out parsedRegime,
                                 out parsedSkew, out parsedTerm, out parsedPcr, out parsedGammaZone,
                                 out parsedIvr, out parsedVix))
                    return;

                lock (levelsLock)
                {
                    pendingLevels = parsedLevels;
                }
                levelsDirty = true;
                lastFileWriteUtc = writeUtc;
                lastFileWriteKnown = true;

                statusAsOf = parsedAsOf ?? writeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                statusRegime = parsedRegime;
                statusSkew = parsedSkew;
                statusTerm = parsedTerm;
                statusPcr = parsedPcr;
                statusGammaZone = parsedGammaZone;
                statusIvr = parsedIvr;
                statusVix = parsedVix;
                statusStale = (DateTime.UtcNow - writeUtc).TotalHours > StaleHoursThreshold;
            }
            catch (Exception ex)
            {
                Print("GexLevels file read/parse failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Shared CSV parse routine (v8 extraction) -- CheckAndLoadFile above (the live
        /// poll) and LoadReplaySnapshot below (a one-off historical load, same file format,
        /// written by the companion fetch_gex_nq_intraday.py) both call this instead of each
        /// having their own copy of the '#'-comment-line and Price/Label/Type/Group parsing.
        /// Returns false (nothing to apply) on any read/parse failure, exactly matching this
        /// method's previous inline best-effort behavior -- the caller decides what "no
        /// update" means for it (CheckAndLoadFile: silently keep the current view;
        /// LoadReplaySnapshot: stay on whatever snapshot was already showing).
        /// </summary>
        private bool ParseGexCsv(string path, out List<GexLevel> levels, out string asOf, out string regime,
                                  out string skew, out string term, out string pcr, out string gammaZone,
                                  out string ivr, out string vix)
        {
            levels = new List<GexLevel>();
            asOf = null;
            regime = "";
            skew = "";
            term = "";
            pcr = "";
            gammaZone = "";
            ivr = "";
            vix = "";

            try
            {
                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0)
                        continue;

                    if (line.StartsWith("#"))
                    {
                        // Pull the informational fields out of the script's own comment
                        // lines -- best-effort only: if the format ever drifts, the status
                        // line just stays blank/stale rather than this throwing.
                        if (line.Contains("generated "))
                        {
                            int idx = line.IndexOf("generated ", StringComparison.Ordinal);
                            asOf = line.Substring(idx + "generated ".Length).Trim();
                        }
                        if (line.Contains("regime="))
                        {
                            int idx = line.IndexOf("regime=", StringComparison.Ordinal);
                            regime = line.Substring(idx + "regime=".Length).Trim();
                        }
                        // v4 additions -- one-token-per-field (unlike regime= above, which
                        // takes the rest of the line, these can share a line with each
                        // other, so each is extracted up to the next space only). Key names
                        // deliberately avoid containing "regime=" as a substring (e.g.
                        // "term_bias=", not "term_regime=") so they can't be mistaken for
                        // the net-gex regime= line above by the Contains() check on it.
                        string skewTok = ExtractToken(line, "skew_25d=");
                        if (skewTok != null) skew = skewTok;
                        string termSlopeTok = ExtractToken(line, "term_slope_vp=");
                        string termBiasTok = ExtractToken(line, "term_bias=");
                        if (termSlopeTok != null || termBiasTok != null)
                            term = (termSlopeTok ?? "?") + "vp (" + (termBiasTok ?? "?") + ")";
                        string pcrTok = ExtractToken(line, "pcr=");
                        if (pcrTok != null) pcr = pcrTok;
                        string gammaZoneTok = ExtractToken(line, "gamma_zone=");
                        // fetch_gex_nq.py replaces spaces with '_' in this token (values
                        // like "BEARISH SQUEEZE" would otherwise get cut at the space by
                        // ExtractToken above) -- undo that here for display.
                        if (gammaZoneTok != null) gammaZone = gammaZoneTok.Replace('_', ' ');
                        string ivrTok = ExtractToken(line, "ivr=");
                        if (ivrTok != null) ivr = ivrTok;
                        // v5 addition -- VIX regime. vix_regime= is a plain word (LOW/NORMAL/
                        // ELEVATED/EXTREME/n/a), no underscore-for-space undo needed like
                        // gamma_zone= above. vix= is fetched from a separate source (CBOE, not
                        // CME) and may be "n/a" on a run where that fetch failed (non-fatal on
                        // the Python side) -- shown as "n/a" here too rather than hidden, so a
                        // persistently missing VIX is visible on the chart, not silently absent.
                        string vixTok = ExtractToken(line, "vix=");
                        string vixRegimeTok = ExtractToken(line, "vix_regime=");
                        if (vixTok != null || vixRegimeTok != null)
                            vix = (vixTok ?? "?") + " (" + (vixRegimeTok ?? "?") + ")";
                        continue;
                    }

                    if (line.StartsWith("Price,", StringComparison.OrdinalIgnoreCase))
                        continue; // header row

                    string[] parts = line.Split(',');
                    if (parts.Length < 3)
                        continue;

                    double price;
                    if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out price))
                        continue;

                    // Group is a v2 addition (4th column) -- default to "Other" for a v1-style
                    // 3-column CSV so an old fetch script (or a partial rewrite) doesn't just
                    // stop rendering everything.
                    string group = parts.Length >= 4 ? parts[3].Trim() : "Other";

                    levels.Add(new GexLevel
                    {
                        Price = price,
                        Label = parts[1].Trim(),
                        Type = parts[2].Trim(),
                        Group = group
                    });
                }
                return true;
            }
            catch (Exception ex)
            {
                Print("GexLevels ParseGexCsv failed for " + path + ": " + ex.Message);
                return false;
            }
        }

        // ── Intraday replay: opening the scrubber window ──────────────────────────────
        private void OpenReplayWindow()
        {
            if (replayWindow != null)
            {
                try { replayWindow.Activate(); } catch { }
                return;
            }

            var snaps = GexSnapshotUtil.ListSnapshots(IntradaySnapshotsFolder, DateTime.Today);
            if (snaps.Count == 0)
            {
                SetPanelStatus(
                    "No intraday snapshots found for today yet. fetch_gex_nq_intraday.py runs "
                    + "every 15 min during RTH via its own Task Scheduler job -- see "
                    + "setup_instructions.txt. Folder checked: " + IntradaySnapshotsFolder, true);
                return;
            }

            replayWindow = new GexReplayWindow(snaps,
                onSnapshotSelected: (path, ts) => LoadReplaySnapshot(path, ts),
                onExitReplay: () => ExitReplayMode());
            replayWindow.Closed += (s, e) =>
            {
                replayWindow = null;
                if (replayMode)
                    ExitReplayMode();
            };
            replayWindow.Show();
        }

        /// <summary>Called by GexReplayWindow (on the UI thread, from its slider/Prev/Next
        /// handlers) each time the user scrubs to a different intraday snapshot. Stages the
        /// parsed levels through the same pendingLevels/levelsDirty hand-off CheckAndLoadFile
        /// uses -- OnBarUpdate (engine thread) picks them up and redraws on its own next
        /// tick, same as a normal live-file update.</summary>
        public void LoadReplaySnapshot(string snapshotPath, DateTime timestamp)
        {
            List<GexLevel> parsedLevels;
            string parsedAsOf, parsedRegime, parsedSkew, parsedTerm, parsedPcr, parsedGammaZone, parsedIvr, parsedVix;
            if (!ParseGexCsv(snapshotPath, out parsedLevels, out parsedAsOf, out parsedRegime,
                             out parsedSkew, out parsedTerm, out parsedPcr, out parsedGammaZone,
                             out parsedIvr, out parsedVix))
                return;

            replayMode = true;
            replayTimestamp = timestamp;

            lock (levelsLock)
            {
                pendingLevels = parsedLevels;
            }
            levelsDirty = true;

            statusAsOf = "REPLAY " + timestamp.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                         + " (source: " + parsedAsOf + ")";
            statusRegime = parsedRegime;
            statusSkew = parsedSkew;
            statusTerm = parsedTerm;
            statusPcr = parsedPcr;
            statusGammaZone = parsedGammaZone;
            statusIvr = parsedIvr;
            statusVix = parsedVix;
            statusStale = false; // replay is intentionally historical -- STALE would mislead here

            Application.Current?.Dispatcher?.BeginInvoke(new Action(UpdatePanelText));
        }

        /// <summary>"Resume LIVE" -- exits replay mode and re-reads the live CSV immediately
        /// (rather than waiting for the next RefreshCheckSeconds tick), matching the
        /// reference tool's own ExitReplayMode.</summary>
        public void ExitReplayMode()
        {
            replayMode = false;
            replayTimestamp = DateTime.MinValue;
            lastFileWriteKnown = false; // force CheckAndLoadFile to treat the live file as changed
            CheckAndLoadFile();
            Application.Current?.Dispatcher?.BeginInvoke(new Action(UpdatePanelText));
        }

        /// <summary>Finds "key" (including its trailing '=') in line and returns everything
        /// up to the next space (or end of line), or null if key isn't present. Used for
        /// the v4 metric fields, which can share one comment line with each other.</summary>
        private static string ExtractToken(string line, string key)
        {
            int idx = line.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0)
                return null;
            int start = idx + key.Length;
            int end = line.IndexOf(' ', start);
            string token = end < 0 ? line.Substring(start) : line.Substring(start, end - start);
            return token.Trim();
        }

        /// <summary>v9: tick events arrive on the engine thread regardless of the bar type or
        /// Calculate setting, so a freshly parsed GEX file is applied (lines, labels, status)
        /// within a tick instead of waiting for the next bar close on slow charts.</summary>
        protected override void OnMarketData(NinjaTrader.Data.MarketDataEventArgs e)
        {
            if (!levelsDirty || State != State.Realtime || CurrentBar < 0)
                return;
            try
            {
                lock (levelsLock)
                {
                    activeLevels = pendingLevels ?? new List<GexLevel>();
                }
                levelsDirty = false;
                RebuildVisibleLevels();
                RedrawLevels();
                if (ShowLevelLabels)
                    RedrawLabelsOnly();
                if (ShowStatusLine)
                    DrawStatusLine();
                if (panelOpen)
                    UpdatePanelText();
            }
            catch (Exception ex)
            {
                Print("GexLevels live apply failed: " + ex.Message);
            }
        }

        protected override void OnBarUpdate()
        {
            if (levelsDirty)
            {
                lock (levelsLock)
                {
                    activeLevels = pendingLevels ?? new List<GexLevel>();
                }
                levelsDirty = false;
                RebuildVisibleLevels();
                RedrawLevels();
            }

            // HorizontalLine draw objects auto-extend across new bars on their own -- they
            // don't need re-issuing every bar. The text labels do, though (a Draw.Text tag
            // stays pinned to the barsAgo it was drawn at, which would otherwise scroll off
            // the right edge as new bars form), so those are re-issued here every bar close.
            if (ShowLevelLabels)
                RedrawLabelsOnly();

            if (ShowStatusLine)
                DrawStatusLine();

            if (panelOpen)
                UpdatePanelText();
        }

        /// <summary>Applies the per-group/per-type visibility toggles to activeLevels ->
        /// visibleLevels. Re-run whenever the parsed level set changes; the toggles
        /// themselves are NinjaScriptProperty values that only change via a Properties-panel
        /// edit, which itself re-triggers OnStateChange -> re-parse, so this never goes
        /// stale against a toggle flip.</summary>
        private void RebuildVisibleLevels()
        {
            var result = new List<GexLevel>(activeLevels.Count);
            foreach (var level in activeLevels)
                if (IsLevelVisible(level))
                    result.Add(level);
            visibleLevels = result;
        }

        private bool IsLevelVisible(GexLevel level)
        {
            switch (level.Type)
            {
                case "DexPlus":
                case "DexMinus":
                    return ShowDex;
                case "ExpectedMove":
                    return ShowExpectedMove;
                case "Secondary+":
                case "Secondary-":
                    return ShowExtendedWalls;
                case "PinStrike":
                case "MaxPain":
                    return ShowZeroDTE;
                case "VannaFlip":
                case "VolTrigger":
                case "RiskPivot":
                    return ShowStructural;
                case "CTrans":
                case "PTrans":
                    return ShowIntraday;
                case "MaxPainAll":
                    // All-expirations Max Pain -- more of a swing-timeframe level per the
                    // reference tool's own guide ("more useful in swing than scalping"),
                    // so it rides the Structural toggle rather than ShowZeroDTE.
                    return ShowStructural;
                case "CallWall":
                case "PutWall":
                case "ZeroGamma":
                case "CharmMagnet":
                case "TopOI":
                    // CharmMagnet is now emitted for BOTH Structural (real per-strike charm,
                    // matching the reference tool's CME-49d Charm Magnet) and ZeroDTE (same
                    // as before) -- gate it by Group like the wall/flip levels instead of the
                    // old hardcoded ShowZeroDTE-only check. TopOI (new) is only ever emitted
                    // for Structural/Intraday, so it rides the same per-Group switch.
                    switch (level.Group)
                    {
                        case "Structural": return ShowStructural;
                        case "Intraday": return ShowIntraday;
                        case "ZeroDTE": return ShowZeroDTE;
                        default: return true; // "Other"/unrecognized bucket -- show rather than hide
                    }
                default:
                    return true; // unrecognized Type -- still show it, same "never silently drop" philosophy as v1
            }
        }

        private void RedrawLevels()
        {
            try
            {
                for (int i = 0; i < MaxDrawnLevels; i++)
                {
                    RemoveDrawObject("GexLine_" + i);
                    RemoveDrawObject("GexLabel_" + i);
                }

                int count = Math.Min(visibleLevels.Count, MaxDrawnLevels);
                for (int i = 0; i < count; i++)
                {
                    GexLevel level = visibleLevels[i];
                    Brush brush = BrushForType(level.Type);
                    DashStyleHelper dash = DashForGroup(level.Group);
                    Draw.HorizontalLine(this, "GexLine_" + i, level.Price, brush, dash, LineWidth);
                }
                lastDrawnCount = count;
            }
            catch (Exception ex)
            {
                Print("GexLevels RedrawLevels failed: " + ex.Message);
            }
        }

        private void RedrawLabelsOnly()
        {
            try
            {
                int count = Math.Min(visibleLevels.Count, lastDrawnCount);

                // De-collide labels: price distance that equals ~13px on the current scale.
                double pxPrice = 0;
                try
                {
                    if (ChartPanel != null && ChartPanel.H > 0)
                        pxPrice = (ChartPanel.MaxValue - ChartPanel.MinValue) / ChartPanel.H * 13.0;
                }
                catch { pxPrice = 0; }
                var order = new List<int>();
                for (int i = 0; i < count; i++) order.Add(i);
                order.Sort((a, b) => visibleLevels[a].Price.CompareTo(visibleLevels[b].Price));
                var stack = new int[count];
                int run = 0; double prev = double.MinValue;
                foreach (int idx in order)
                {
                    double pr = visibleLevels[idx].Price;
                    run = (pxPrice > 0 && pr - prev < pxPrice) ? run + 1 : 0;
                    stack[idx] = run;
                    prev = pr;
                }

                for (int i = 0; i < count; i++)
                {
                    GexLevel level = visibleLevels[i];
                    Brush brush = BrushForType(level.Type);
                    // Right-anchored at the current bar (barsAgo=0) with a background box
                    // behind the text -- ported from the third-party tool's own
                    // label-readability fix (LabelBackgroundBrush/Opacity), on top of this
                    // file's existing small pixel offset. Draw.Text's outlineBrush param
                    // stays Transparent (that's a text-outline stroke, not what we want
                    // here); areaBrush/areaOpacity are the actual background box.
                    //
                    // NOTE: this used to hardcode barsAgo=3 ("right-anchored, same
                    // convention as before"), but that throws ArgumentOutOfRangeException
                    // on this chart ("'barsAgo' needed to be between 0 and 3449 but was 3"),
                    // which the surrounding try/catch swallows -- silently killing every
                    // label in the loop (the horizontal lines are drawn in a separate method
                    // and were unaffected, which is why only the lines showed up). barsAgo=0
                    // (the most-recently-closed bar) is always a valid index and keeps the
                    // same right-edge-pinned visual result since this whole method already
                    // re-runs every bar close.
                    Draw.Text(this, "GexLabel_" + i, false, level.Label, 0, level.Price, LabelPixelOffset - stack[i] * 13,
                        brush, levelLabelFont, TextAlignment.Left, Brushes.Transparent, LabelBackgroundBrush, LabelBackgroundOpacity);
                }
            }
            catch (Exception ex)
            {
                Print("GexLevels RedrawLabelsOnly failed: " + ex.Message);
            }
        }

        private void DrawStatusLine()
        {
            try
            {
                var sb = new StringBuilder();
                if (replayMode)
                    sb.Append("REPLAY MODE -- ").Append(replayTimestamp.ToString("HH:mm")).Append("\n");
                sb.Append("GEX levels as of: ").Append(statusAsOf);
                if (statusRegime.Length > 0)
                    sb.Append("\n").Append(statusRegime);
                if (statusGammaZone.Length > 0)
                    sb.Append("\nGamma Zone: ").Append(statusGammaZone);

                // Key-levels recap, ported from the third-party tool's bottom-left status
                // block -- a compact "what are the headline numbers right now" summary so
                // you don't have to hunt for the right line among a busy chart. Only the
                // first match per type is shown (each of these types should only appear
                // once per bucket in practice); silently skips any that aren't in today's
                // file rather than showing a placeholder.
                AppendFirstMatch(sb, "Structural Gamma Flip", "ZeroGamma", "Structural");
                AppendFirstMatch(sb, "Structural Call Wall", "CallWall", "Structural");
                AppendFirstMatch(sb, "Structural Put Wall", "PutWall", "Structural");
                AppendFirstMatch(sb, "0DTE Pin", "PinStrike", "ZeroDTE");
                AppendFirstMatch(sb, "0DTE Max Pain", "MaxPain", "ZeroDTE");

                if (statusSkew.Length > 0)
                    sb.Append("\nSkew 25d: ").Append(statusSkew);
                if (statusTerm.Length > 0)
                    sb.Append("\nTerm: ").Append(statusTerm);
                if (statusPcr.Length > 0)
                    sb.Append("\nPCR: ").Append(statusPcr);
                if (statusIvr.Length > 0)
                    sb.Append("\nIVR: ").Append(statusIvr);
                if (statusVix.Length > 0)
                    sb.Append("\nVIX: ").Append(statusVix);

                if (statusStale)
                    sb.Append("\n*** STALE -- check fetch_gex_nq.py / Task Scheduler ***");

                Brush color = replayMode ? Brushes.MediumPurple : (statusStale ? Brushes.Red : Brushes.Silver);
                Draw.TextFixed(this, "GexStatusLine", sb.ToString(), TextPosition.BottomLeft, color, statusFont, Brushes.Transparent, Brushes.Black, 92);
            }
            catch (Exception ex)
            {
                Print("GexLevels DrawStatusLine failed: " + ex.Message);
            }
        }

        private void AppendFirstMatch(StringBuilder sb, string label, string type, string group)
        {
            foreach (var level in activeLevels)
            {
                if (level.Type == type && level.Group == group)
                {
                    sb.Append("\n").Append(label).Append(": ").Append(level.Price.ToString("0.00", CultureInfo.InvariantCulture));
                    return;
                }
            }
        }

        // ── Floating panel ──────────────────────────────────────────────────────────
        // Modeled on the reference tool's own floating panel (OFK_NQ_GEX_Levels.cs's
        // OpenPanel/UpdatePanelText -- see github.com/Kza56/OFK_Atas_GEX), but built from
        // this file's OWN data (activeLevels + the statusX fields already parsed from the
        // CSV) rather than any new computation. MUST run on the UI thread -- every call
        // site below goes through Application.Current.Dispatcher for that reason.

        private void OpenPanel()
        {
            if (panelOpen)
                return;

            var bgColor = Color.FromRgb(22, 27, 39);
            var bgDark = Color.FromRgb(13, 17, 23);
            var borderColor = Color.FromRgb(33, 41, 61);
            var textColor = Color.FromRgb(201, 209, 217);
            var textDimColor = Color.FromRgb(139, 148, 158);
            var accentBlue = Color.FromRgb(79, 139, 209);

            panelWindow = new Window
            {
                Title = "GexLevels NQ",
                Width = 380,
                Height = 620,
                MinWidth = 320,
                MinHeight = 300,
                Topmost = true,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 80,
                Top = 80,
                Background = new SolidColorBrush(bgColor),
                ShowInTaskbar = false,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
            };

            var root = new Border { Background = new SolidColorBrush(bgColor), BorderBrush = new SolidColorBrush(borderColor), BorderThickness = new Thickness(1) };
            var dock = new DockPanel { LastChildFill = true };

            var hdr = new Border { Background = new SolidColorBrush(bgDark), BorderBrush = new SolidColorBrush(borderColor), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 8, 12, 8) };
            hdr.Child = new TextBlock { Text = "GexLevels NQ", Foreground = new SolidColorBrush(accentBlue), FontSize = 13, FontWeight = FontWeights.SemiBold };
            DockPanel.SetDock(hdr, Dock.Top);
            dock.Children.Add(hdr);

            var infoSec = new Border { Background = new SolidColorBrush(Color.FromRgb(18, 22, 30)), Padding = new Thickness(12, 10, 12, 10) };
            panelInfoText = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 11, Foreground = new SolidColorBrush(textColor), TextWrapping = TextWrapping.NoWrap, LineHeight = 17 };
            infoSec.Child = panelInfoText;

            // Refresh button (full width -- runs fetch_gex_nq.py).
            var btnSec = new Border { Padding = new Thickness(12, 8, 12, 4), BorderBrush = new SolidColorBrush(borderColor), BorderThickness = new Thickness(0, 1, 0, 0) };
            panelRefreshButton = new Button
            {
                Content = "Refresh (run fetch_gex_nq.py)",
                Height = 32,
                Background = new SolidColorBrush(Color.FromRgb(20, 40, 90)),
                Foreground = new SolidColorBrush(accentBlue),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                BorderBrush = new SolidColorBrush(accentBlue),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            panelRefreshButton.Click += (s, e) => RunRefreshScript();
            btnSec.Child = panelRefreshButton;

            // Briefing row (v7 addition) -- Briefing button runs generate_briefing_nq.py
            // (AI-generated trading thesis PDF); View PDF opens the last one generated, in
            // whatever the OS's default PDF viewer is. Two buttons side by side since
            // "generate" and "view the result" are logically separate actions -- a run can
            // fail (see RunBriefingScript), and re-viewing an old PDF shouldn't require
            // re-running the (paid, slower) LLM call.
            var briefingSec = new Border { Padding = new Thickness(12, 4, 12, 8), BorderBrush = new SolidColorBrush(borderColor), BorderThickness = new Thickness(0, 0, 0, 0) };
            var briefingRow = new Grid();
            briefingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            briefingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            briefingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            panelBriefingButton = new Button
            {
                Content = "Briefing (AI thesis)",
                Height = 32,
                Background = new SolidColorBrush(Color.FromRgb(20, 40, 90)),
                Foreground = new SolidColorBrush(accentBlue),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                BorderBrush = new SolidColorBrush(accentBlue),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            panelBriefingButton.Click += (s, e) => RunBriefingScript();
            Grid.SetColumn(panelBriefingButton, 0);
            briefingRow.Children.Add(panelBriefingButton);

            panelViewPdfButton = new Button
            {
                Content = "View PDF",
                Height = 32,
                Background = new SolidColorBrush(Color.FromRgb(30, 34, 46)),
                Foreground = new SolidColorBrush(textColor),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                BorderBrush = new SolidColorBrush(borderColor),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            panelViewPdfButton.Click += (s, e) => OpenBriefingPdf();
            Grid.SetColumn(panelViewPdfButton, 2);
            briefingRow.Children.Add(panelViewPdfButton);

            briefingSec.Child = briefingRow;

            // Replay row (v8 addition) -- opens the intraday scrubber window (a separate
            // WPF window, GexReplayWindow.cs) listing today's 15-min snapshots written by
            // fetch_gex_nq_intraday.py. Full width, own row -- distinct action from
            // Refresh/Briefing above (navigates history rather than fetching new data).
            var replaySec = new Border { Padding = new Thickness(12, 4, 12, 8), BorderBrush = new SolidColorBrush(borderColor), BorderThickness = new Thickness(0, 0, 0, 0) };
            panelReplayButton = new Button
            {
                Content = "Intraday Replay",
                Height = 32,
                Background = new SolidColorBrush(Color.FromRgb(40, 25, 60)),
                Foreground = new SolidColorBrush(Color.FromRgb(189, 147, 249)),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                BorderBrush = new SolidColorBrush(Color.FromRgb(189, 147, 249)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            panelReplayButton.Click += (s, e) => OpenReplayWindow();
            replaySec.Child = panelReplayButton;

            var statusSec = new Border { Background = new SolidColorBrush(bgDark), Padding = new Thickness(12, 5, 12, 5), BorderBrush = new SolidColorBrush(borderColor), BorderThickness = new Thickness(0, 1, 0, 0) };
            panelStatusText = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(textDimColor), Text = "Idle" };
            statusSec.Child = panelStatusText;

            DockPanel.SetDock(statusSec, Dock.Bottom);
            dock.Children.Add(statusSec);
            DockPanel.SetDock(replaySec, Dock.Bottom);
            dock.Children.Add(replaySec);
            DockPanel.SetDock(briefingSec, Dock.Bottom);
            dock.Children.Add(briefingSec);
            DockPanel.SetDock(btnSec, Dock.Bottom);
            dock.Children.Add(btnSec);

            var scroll = new ScrollViewer
            {
                Content = infoSec,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = new SolidColorBrush(Color.FromRgb(18, 22, 30)),
            };
            dock.Children.Add(scroll);

            root.Child = dock;
            panelWindow.Content = root;
            panelWindow.Closed += (s, e) =>
            {
                panelOpen = false;
                panelWindow = null;
                panelInfoText = null;
                panelStatusText = null;
                panelRefreshButton = null;
                panelBriefingButton = null;
                panelViewPdfButton = null;
                panelReplayButton = null;
            };

            panelOpen = true;
            panelWindow.Show();
            UpdatePanelText();
        }

        private void ClosePanel()
        {
            try { panelWindow?.Close(); } catch { }
            panelOpen = false;
        }

        private void UpdatePanelText()
        {
            if (panelInfoText == null)
                return;

            var sb = new StringBuilder();
            sb.Append("As of: ").Append(statusAsOf);
            if (statusStale)
                sb.Append("  *** STALE ***");
            sb.Append("\n");
            if (statusRegime.Length > 0)
                sb.Append("\n").Append(statusRegime);
            if (statusGammaZone.Length > 0)
                sb.Append("\nGamma Zone: ").Append(statusGammaZone);
            if (statusSkew.Length > 0)
                sb.Append("\nSkew 25d: ").Append(statusSkew);
            if (statusTerm.Length > 0)
                sb.Append("\nTerm: ").Append(statusTerm);
            if (statusPcr.Length > 0)
                sb.Append("\nPCR: ").Append(statusPcr);
            if (statusIvr.Length > 0)
                sb.Append("\nIVR: ").Append(statusIvr);
            if (statusVix.Length > 0)
                sb.Append("\nVIX: ").Append(statusVix);

            sb.Append("\n\n-- STRUCTURAL (49d+) --");
            AppendFirstMatch(sb, "Gamma Flip", "ZeroGamma", "Structural");
            AppendFirstMatch(sb, "Vanna Flip (approx.)", "VannaFlip", "Structural");
            AppendFirstMatch(sb, "Vol Trigger", "VolTrigger", "Structural");
            AppendFirstMatch(sb, "Risk Pivot", "RiskPivot", "Structural");
            AppendFirstMatch(sb, "Call Wall", "CallWall", "Structural");
            AppendFirstMatch(sb, "Put Wall", "PutWall", "Structural");
            AppendFirstMatch(sb, "Charm Magnet", "CharmMagnet", "Structural");
            AppendFirstMatch(sb, "Top OI #1", "TopOI", "Structural");

            sb.Append("\n\n-- INTRADAY (0-7DTE) --");
            AppendFirstMatch(sb, "Gamma Flip", "ZeroGamma", "Intraday");
            AppendFirstMatch(sb, "Call Wall", "CallWall", "Intraday");
            AppendFirstMatch(sb, "Put Wall", "PutWall", "Intraday");
            AppendFirstMatch(sb, "cTrans", "CTrans", "Intraday");
            AppendFirstMatch(sb, "pTrans", "PTrans", "Intraday");
            AppendFirstMatch(sb, "D+", "DexPlus", "Intraday");
            AppendFirstMatch(sb, "D-", "DexMinus", "Intraday");
            AppendFirstMatch(sb, "Top OI #1", "TopOI", "Intraday");

            sb.Append("\n\n-- 0DTE (session end) --");
            AppendFirstMatch(sb, "Pin Strike", "PinStrike", "ZeroDTE");
            AppendFirstMatch(sb, "Max Pain", "MaxPain", "ZeroDTE");
            AppendFirstMatch(sb, "Charm Magnet", "CharmMagnet", "ZeroDTE");

            sb.Append("\n\n-- OTHER --");
            AppendFirstMatch(sb, "Max Pain (All Exp)", "MaxPainAll", "Other");
            AppendFirstMatch(sb, "Expected Move Up", "ExpectedMove", "Other");
            AppendFirstMatch(sb, "Expected Move Dn", "ExpectedMove", "Other");

            string text = sb.ToString();
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                if (panelInfoText != null)
                    panelInfoText.Text = text;
            }));
        }

        // ── Refresh button: launches fetch_gex_nq.py as a subprocess, same script the
        // Task Scheduler job runs, just triggerable on demand from the panel. Non-blocking
        // (Process.Start + an event handler for exit, not a synchronous Wait) so it never
        // freezes the chart while the fetch runs (10-30+ seconds, see setup_instructions.txt).
        private void RunRefreshScript()
        {
            if (refreshRunning)
                return;

            if (string.IsNullOrWhiteSpace(ScriptPath) || !File.Exists(ScriptPath))
            {
                SetPanelStatus("Script not found: " + ScriptPath, true);
                return;
            }

            try
            {
                refreshRunning = true;
                SetPanelStatus("Running fetch_gex_nq.py ...", false);
                if (panelRefreshButton != null)
                    panelRefreshButton.IsEnabled = false;

                // FIXED (2026-09-26): the first version launched with CreateNoWindow=true
                // and BOTH streams left un-redirected. On Windows that combination often
                // gives the child process an invalid console handle -- fetch_gex_nq.py's
                // logging is plain print(), so its very first log() call throws and the
                // whole script dies immediately with exit code 1, before it even reaches
                // Playwright. Confirmed from a real run: the button showed exactly that
                // "exited with code 1" with nothing else to go on, matching this failure
                // mode exactly. Fix: redirect both streams to a real log file (also gives
                // a persistent record to inspect after the fact, not just a status line).
                string logDir = Path.GetDirectoryName(ScriptPath);
                string logPath = Path.Combine(string.IsNullOrEmpty(logDir) ? "." : logDir, "panel_refresh_last_run.log");
                var output = new StringBuilder();

                var psi = new ProcessStartInfo
                {
                    FileName = PythonPath,
                    Arguments = "\"" + ScriptPath + "\"" + (activeTicker != "NQ" ? " --ticker " + activeTicker : ""),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                refreshProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
                refreshProcess.OutputDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                refreshProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                refreshProcess.Exited += (s, e) =>
                {
                    int exitCode = -1;
                    try { exitCode = refreshProcess.ExitCode; } catch { }

                    string captured;
                    lock (output) captured = output.ToString();
                    try { File.WriteAllText(logPath, captured, Encoding.UTF8); } catch { }

                    // Last non-blank line is usually the most useful single-line summary
                    // (fetch_gex_nq.py's own final "Wrote N levels..." or "FETCH FAILED: ...").
                    string lastLine = "";
                    var lines = captured.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0)
                        lastLine = lines[lines.Length - 1];

                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        refreshRunning = false;
                        if (panelRefreshButton != null)
                            panelRefreshButton.IsEnabled = true;
                        SetPanelStatus(exitCode == 0
                            ? "OK: " + lastLine
                            : "Exit " + exitCode + ": " + lastLine + "  (full log: " + logPath + ")", exitCode != 0);
                    }));
                    // Pick up the new CSV immediately rather than waiting for the timer.
                    CheckAndLoadFile();
                };
                refreshProcess.Start();
                refreshProcess.BeginOutputReadLine();
                refreshProcess.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                refreshRunning = false;
                if (panelRefreshButton != null)
                    panelRefreshButton.IsEnabled = true;
                SetPanelStatus("Failed to launch: " + ex.Message, true);
            }
        }

        // ── Briefing button: launches generate_briefing_nq.py as a subprocess -- a
        // SEPARATE script from fetch_gex_nq.py (reads the CSV the refresh already wrote,
        // calls the Anthropic API for an AI-generated trading thesis, renders a PDF).
        // Same redirected-stream/log-file/Exited-event pattern as RunRefreshScript above,
        // for the same reason (CreateNoWindow=true + un-redirected streams crashes a
        // print()-based Python script immediately on Windows -- see that method's comment).
        // On success, enables the "View PDF" button rather than auto-opening the PDF, since
        // an LLM call is slow/costs money per click and the user may not be at the chart to
        // see a window pop up uninvited.
        private void RunBriefingScript()
        {
            if (briefingRunning)
                return;

            if (string.IsNullOrWhiteSpace(BriefingScriptPath) || !File.Exists(BriefingScriptPath))
            {
                SetPanelStatus("Briefing script not found: " + BriefingScriptPath, true);
                return;
            }

            if (string.IsNullOrWhiteSpace(GexFilePath) || !File.Exists(GexFilePath))
            {
                SetPanelStatus("No GexLevels_NQ.csv yet -- run Refresh first.", true);
                return;
            }

            try
            {
                briefingRunning = true;
                SetPanelStatus("Running generate_briefing_nq.py (calls Anthropic API, may take 10-30s) ...", false);
                if (panelBriefingButton != null)
                    panelBriefingButton.IsEnabled = false;

                string logDir = Path.GetDirectoryName(BriefingScriptPath);
                string logPath = Path.Combine(string.IsNullOrEmpty(logDir) ? "." : logDir, "panel_briefing_last_run.log");
                var output = new StringBuilder();

                var psi = new ProcessStartInfo
                {
                    FileName = PythonPath,
                    Arguments = "\"" + BriefingScriptPath + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                briefingProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
                briefingProcess.OutputDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                briefingProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                briefingProcess.Exited += (s, e) =>
                {
                    int exitCode = -1;
                    try { exitCode = briefingProcess.ExitCode; } catch { }

                    string captured;
                    lock (output) captured = output.ToString();
                    try { File.WriteAllText(logPath, captured, Encoding.UTF8); } catch { }

                    string lastLine = "";
                    var lines = captured.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0)
                        lastLine = lines[lines.Length - 1];

                    bool pdfExists = false;
                    try { pdfExists = !string.IsNullOrWhiteSpace(BriefingPdfPath) && File.Exists(BriefingPdfPath); } catch { }

                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        briefingRunning = false;
                        if (panelBriefingButton != null)
                            panelBriefingButton.IsEnabled = true;
                        SetPanelStatus(exitCode == 0 && pdfExists
                            ? "Briefing ready -- click View PDF"
                            : "Exit " + exitCode + ": " + lastLine + "  (full log: " + logPath + ")", exitCode != 0 || !pdfExists);
                    }));
                };
                briefingProcess.Start();
                briefingProcess.BeginOutputReadLine();
                briefingProcess.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                briefingRunning = false;
                if (panelBriefingButton != null)
                    panelBriefingButton.IsEnabled = true;
                SetPanelStatus("Failed to launch: " + ex.Message, true);
            }
        }

        // Opens the last-generated briefing PDF in the OS's default PDF viewer.
        // UseShellExecute=true is required for this (the "open a file with its
        // associated app" behavior lives in shell integration, not Process.Start's own
        // direct-launch path -- UseShellExecute=false, used above for the Python
        // subprocesses, would instead try to execute the PDF itself as a program and fail).
        private void OpenBriefingPdf()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(BriefingPdfPath) || !File.Exists(BriefingPdfPath))
                {
                    SetPanelStatus("No briefing PDF yet -- click Briefing first.", true);
                    return;
                }
                Process.Start(new ProcessStartInfo(BriefingPdfPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetPanelStatus("Failed to open PDF: " + ex.Message, true);
            }
        }

        private void SetPanelStatus(string text, bool isError)
        {
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                if (panelStatusText == null)
                    return;
                panelStatusText.Text = text;
                panelStatusText.Foreground = new SolidColorBrush(isError ? Color.FromRgb(224, 108, 117) : Color.FromRgb(139, 148, 158));
            }));
        }

        private Brush BrushForType(string type)
        {
            switch (type)
            {
                case "ZeroGamma": return ZeroGammaBrush;
                case "CallWall": return CallWallBrush;
                case "PutWall": return PutWallBrush;
                case "DexPlus": return DexPlusBrush;
                case "DexMinus": return DexMinusBrush;
                case "PinStrike":
                case "MaxPain":
                case "MaxPainAll":
                case "CharmMagnet": return ZeroDTEBrush;
                case "TopOI": return SecondaryBrush;
                case "ExpectedMove": return ExpectedMoveBrush;
                case "VannaFlip": return VannaFlipBrush;
                case "VolTrigger":
                case "RiskPivot": return StructuralExtraBrush;
                case "CTrans":
                case "PTrans": return TransitionBrush;
                case "Secondary+":
                case "Secondary-": return SecondaryBrush;
                default: return Brushes.Gray; // unrecognized Type -- still draw it, just neutral colored
            }
        }

        /// <summary>Dash style keyed off the DTE bucket (Group), a quick "which timeframe is
        /// this level from" visual cue without needing a separate color per bucket stacked
        /// on top of the per-TYPE colors above -- ported in spirit from the third-party
        /// tool's much larger per-group/per-type style matrix, simplified to keep this
        /// file's property count sane.</summary>
        private DashStyleHelper DashForGroup(string group)
        {
            switch (group)
            {
                case "Structural": return DashStyleHelper.Solid;
                case "Intraday": return DashStyleHelper.Dash;
                case "ZeroDTE": return DashStyleHelper.Dot;
                default: return DashStyleHelper.DashDot;
            }
        }

        public override string DisplayName
        {
            get { return "GexLevels_v1_0_0"; }
        }


        private string activeTicker = "NQ";
        private static string TickerForInstrument(string root)
        {
            if (string.IsNullOrEmpty(root)) return "NQ";
            string r = root.ToUpperInvariant();
            if (r == "ES" || r == "MES") return "ES";
            if (r == "YM" || r == "MYM") return "YM";
            return "NQ";
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "GEX CSV File Path", GroupName = "1 Source", Order = 1)]
        public string GexFilePath { get; set; }

        [NinjaScriptProperty]
        [Range(5, 3600)]
        [Display(Name = "Refresh Check (seconds)", GroupName = "1 Source", Order = 2)]
        public int RefreshCheckSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 168)]
        [Display(Name = "Stale After (hours)", GroupName = "1 Source", Order = 3)]
        public int StaleHoursThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Structural (49d+)", Description = "Gamma Flip / Call Wall / Put Wall / Vanna Flip from expirations 49+ days out.", GroupName = "2 Level Groups", Order = 1)]
        public bool ShowStructural { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Intraday walls (0-7DTE)", Description = "Call Wall / Put Wall / Gamma Flip from expirations within 7 days.", GroupName = "2 Level Groups", Order = 2)]
        public bool ShowIntraday { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "0DTE levels", Description = "Pin Strike / Max Pain / Charm Magnet from today's expiration.", GroupName = "2 Level Groups", Order = 3)]
        public bool ShowZeroDTE { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "DEX magnets (D+/D-)", Description = "Largest positive/negative delta-exposure strikes.", GroupName = "2 Level Groups", Order = 4)]
        public bool ShowDex { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Expected move band", GroupName = "2 Level Groups", Order = 5)]
        public bool ShowExpectedMove { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Extended GEX walls", Description = "Secondary strikes beyond the primary call/put wall in each bucket.", GroupName = "2 Level Groups", Order = 6)]
        public bool ShowExtendedWalls { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Level Labels", GroupName = "3 Display", Order = 1)]
        public bool ShowLevelLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Status Line", GroupName = "3 Display", Order = 2)]
        public bool ShowStatusLine { get; set; }

        [NinjaScriptProperty]
        [Range(1, 6)]
        [Display(Name = "Line Width", GroupName = "3 Display", Order = 3)]
        public int LineWidth { get; set; }

        [NinjaScriptProperty]
        [Range(0, 60)]
        [Display(Name = "Label Pixel Offset", Description = "Vertical pixel offset for level labels, above the line (0 = sits on the line).", GroupName = "3 Display", Order = 4)]
        public int LabelPixelOffset { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Label Background Opacity %", Description = "Background box behind each label so it reads over candles (0 = no box).", GroupName = "3 Display", Order = 5)]
        public int LabelBackgroundOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Label Background Color", GroupName = "3 Display", Order = 6)]
        public Brush LabelBackgroundBrush { get; set; }
        [Browsable(false)]
        public string LabelBackgroundBrushSerializable
        {
            get { return Serialize.BrushToString(LabelBackgroundBrush); }
            set { LabelBackgroundBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Call Wall Color", GroupName = "4 Colors", Order = 1)]
        public Brush CallWallBrush { get; set; }
        [Browsable(false)]
        public string CallWallBrushSerializable
        {
            get { return Serialize.BrushToString(CallWallBrush); }
            set { CallWallBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Put Wall Color", GroupName = "4 Colors", Order = 2)]
        public Brush PutWallBrush { get; set; }
        [Browsable(false)]
        public string PutWallBrushSerializable
        {
            get { return Serialize.BrushToString(PutWallBrush); }
            set { PutWallBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Zero Gamma Color", GroupName = "4 Colors", Order = 3)]
        public Brush ZeroGammaBrush { get; set; }
        [Browsable(false)]
        public string ZeroGammaBrushSerializable
        {
            get { return Serialize.BrushToString(ZeroGammaBrush); }
            set { ZeroGammaBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "DEX D+ Color", GroupName = "4 Colors", Order = 4)]
        public Brush DexPlusBrush { get; set; }
        [Browsable(false)]
        public string DexPlusBrushSerializable
        {
            get { return Serialize.BrushToString(DexPlusBrush); }
            set { DexPlusBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "DEX D- Color", GroupName = "4 Colors", Order = 5)]
        public Brush DexMinusBrush { get; set; }
        [Browsable(false)]
        public string DexMinusBrushSerializable
        {
            get { return Serialize.BrushToString(DexMinusBrush); }
            set { DexMinusBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "0DTE Level Color", Description = "Shared by Pin Strike / Max Pain / Charm Magnet.", GroupName = "4 Colors", Order = 6)]
        public Brush ZeroDTEBrush { get; set; }
        [Browsable(false)]
        public string ZeroDTEBrushSerializable
        {
            get { return Serialize.BrushToString(ZeroDTEBrush); }
            set { ZeroDTEBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Expected Move Color", GroupName = "4 Colors", Order = 7)]
        public Brush ExpectedMoveBrush { get; set; }
        [Browsable(false)]
        public string ExpectedMoveBrushSerializable
        {
            get { return Serialize.BrushToString(ExpectedMoveBrush); }
            set { ExpectedMoveBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Vanna Flip Color", GroupName = "4 Colors", Order = 8)]
        public Brush VannaFlipBrush { get; set; }
        [Browsable(false)]
        public string VannaFlipBrushSerializable
        {
            get { return Serialize.BrushToString(VannaFlipBrush); }
            set { VannaFlipBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Secondary/Extended Color", GroupName = "4 Colors", Order = 9)]
        public Brush SecondaryBrush { get; set; }
        [Browsable(false)]
        public string SecondaryBrushSerializable
        {
            get { return Serialize.BrushToString(SecondaryBrush); }
            set { SecondaryBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Vol Trigger / Risk Pivot Color", Description = "Shared by Vol Trigger and Risk Pivot (Structural).", GroupName = "4 Colors", Order = 10)]
        public Brush StructuralExtraBrush { get; set; }
        [Browsable(false)]
        public string StructuralExtraBrushSerializable
        {
            get { return Serialize.BrushToString(StructuralExtraBrush); }
            set { StructuralExtraBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "cTrans / pTrans Color", Description = "Shared by cTrans and pTrans (Intraday).", GroupName = "4 Colors", Order = 11)]
        public Brush TransitionBrush { get; set; }
        [Browsable(false)]
        public string TransitionBrushSerializable
        {
            get { return Serialize.BrushToString(TransitionBrush); }
            set { TransitionBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Show floating panel", Description = "A separate draggable window with the same metrics as the status line, organized by Structural/Intraday/0DTE section.", GroupName = "5 Floating Panel", Order = 1)]
        public bool ShowPanel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Python exe path", Description = "Passed to Process.Start for the panel's refresh button. \"python\" works if it's on PATH; otherwise give the full path to python.exe.", GroupName = "5 Floating Panel", Order = 2)]
        public string PythonPath { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "fetch_gex_nq.py path", Description = "Full path to the companion fetch script, launched by the panel's refresh button.", GroupName = "5 Floating Panel", Order = 3)]
        public string ScriptPath { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "generate_briefing_nq.py path", Description = "Full path to the companion AI-briefing script, launched by the panel's Briefing button. Requires 'pip install anthropic reportlab' and an ANTHROPIC_API_KEY environment variable -- see setup_instructions.txt.", GroupName = "5 Floating Panel", Order = 4)]
        public string BriefingScriptPath { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Briefing PDF path", Description = "Where generate_briefing_nq.py writes GexBriefing_NQ.pdf -- must match that script's own PDF_OUT_PATH constant. The panel's View PDF button opens this file.", GroupName = "5 Floating Panel", Order = 5)]
        public string BriefingPdfPath { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Intraday snapshots folder", Description = "Folder of 15-min timestamped snapshot CSVs (GexLevels_NQ_snapshot_YYYYMMDD_HHMM.csv) written by fetch_gex_nq_intraday.py -- its own separate Task Scheduler job, NOT fetch_gex_nq.py. The panel's Intraday Replay button lists today's files from here.", GroupName = "6 Intraday Replay", Order = 1)]
        public string IntradaySnapshotsFolder { get; set; }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private GexLevels_v1_0_0[] cacheGexLevels_v1_0_0;
		public GexLevels_v1_0_0 GexLevels_v1_0_0(string gexFilePath, int refreshCheckSeconds, int staleHoursThreshold, bool showStructural, bool showIntraday, bool showZeroDTE, bool showDex, bool showExpectedMove, bool showExtendedWalls, bool showLevelLabels, bool showStatusLine, int lineWidth, int labelPixelOffset, int labelBackgroundOpacity, bool showPanel, string pythonPath, string scriptPath, string briefingScriptPath, string briefingPdfPath, string intradaySnapshotsFolder)
		{
			return GexLevels_v1_0_0(Input, gexFilePath, refreshCheckSeconds, staleHoursThreshold, showStructural, showIntraday, showZeroDTE, showDex, showExpectedMove, showExtendedWalls, showLevelLabels, showStatusLine, lineWidth, labelPixelOffset, labelBackgroundOpacity, showPanel, pythonPath, scriptPath, briefingScriptPath, briefingPdfPath, intradaySnapshotsFolder);
		}

		public GexLevels_v1_0_0 GexLevels_v1_0_0(ISeries<double> input, string gexFilePath, int refreshCheckSeconds, int staleHoursThreshold, bool showStructural, bool showIntraday, bool showZeroDTE, bool showDex, bool showExpectedMove, bool showExtendedWalls, bool showLevelLabels, bool showStatusLine, int lineWidth, int labelPixelOffset, int labelBackgroundOpacity, bool showPanel, string pythonPath, string scriptPath, string briefingScriptPath, string briefingPdfPath, string intradaySnapshotsFolder)
		{
			if (cacheGexLevels_v1_0_0 != null)
				for (int idx = 0; idx < cacheGexLevels_v1_0_0.Length; idx++)
					if (cacheGexLevels_v1_0_0[idx] != null && cacheGexLevels_v1_0_0[idx].GexFilePath == gexFilePath && cacheGexLevels_v1_0_0[idx].RefreshCheckSeconds == refreshCheckSeconds && cacheGexLevels_v1_0_0[idx].StaleHoursThreshold == staleHoursThreshold && cacheGexLevels_v1_0_0[idx].ShowStructural == showStructural && cacheGexLevels_v1_0_0[idx].ShowIntraday == showIntraday && cacheGexLevels_v1_0_0[idx].ShowZeroDTE == showZeroDTE && cacheGexLevels_v1_0_0[idx].ShowDex == showDex && cacheGexLevels_v1_0_0[idx].ShowExpectedMove == showExpectedMove && cacheGexLevels_v1_0_0[idx].ShowExtendedWalls == showExtendedWalls && cacheGexLevels_v1_0_0[idx].ShowLevelLabels == showLevelLabels && cacheGexLevels_v1_0_0[idx].ShowStatusLine == showStatusLine && cacheGexLevels_v1_0_0[idx].LineWidth == lineWidth && cacheGexLevels_v1_0_0[idx].LabelPixelOffset == labelPixelOffset && cacheGexLevels_v1_0_0[idx].LabelBackgroundOpacity == labelBackgroundOpacity && cacheGexLevels_v1_0_0[idx].ShowPanel == showPanel && cacheGexLevels_v1_0_0[idx].PythonPath == pythonPath && cacheGexLevels_v1_0_0[idx].ScriptPath == scriptPath && cacheGexLevels_v1_0_0[idx].BriefingScriptPath == briefingScriptPath && cacheGexLevels_v1_0_0[idx].BriefingPdfPath == briefingPdfPath && cacheGexLevels_v1_0_0[idx].IntradaySnapshotsFolder == intradaySnapshotsFolder && cacheGexLevels_v1_0_0[idx].EqualsInput(input))
						return cacheGexLevels_v1_0_0[idx];
			return CacheIndicator<GexLevels_v1_0_0>(new GexLevels_v1_0_0(){ GexFilePath = gexFilePath, RefreshCheckSeconds = refreshCheckSeconds, StaleHoursThreshold = staleHoursThreshold, ShowStructural = showStructural, ShowIntraday = showIntraday, ShowZeroDTE = showZeroDTE, ShowDex = showDex, ShowExpectedMove = showExpectedMove, ShowExtendedWalls = showExtendedWalls, ShowLevelLabels = showLevelLabels, ShowStatusLine = showStatusLine, LineWidth = lineWidth, LabelPixelOffset = labelPixelOffset, LabelBackgroundOpacity = labelBackgroundOpacity, ShowPanel = showPanel, PythonPath = pythonPath, ScriptPath = scriptPath, BriefingScriptPath = briefingScriptPath, BriefingPdfPath = briefingPdfPath, IntradaySnapshotsFolder = intradaySnapshotsFolder }, input, ref cacheGexLevels_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.GexLevels_v1_0_0 GexLevels_v1_0_0(string gexFilePath, int refreshCheckSeconds, int staleHoursThreshold, bool showStructural, bool showIntraday, bool showZeroDTE, bool showDex, bool showExpectedMove, bool showExtendedWalls, bool showLevelLabels, bool showStatusLine, int lineWidth, int labelPixelOffset, int labelBackgroundOpacity, bool showPanel, string pythonPath, string scriptPath, string briefingScriptPath, string briefingPdfPath, string intradaySnapshotsFolder)
		{
			return indicator.GexLevels_v1_0_0(Input, gexFilePath, refreshCheckSeconds, staleHoursThreshold, showStructural, showIntraday, showZeroDTE, showDex, showExpectedMove, showExtendedWalls, showLevelLabels, showStatusLine, lineWidth, labelPixelOffset, labelBackgroundOpacity, showPanel, pythonPath, scriptPath, briefingScriptPath, briefingPdfPath, intradaySnapshotsFolder);
		}

		public Indicators.GexLevels_v1_0_0 GexLevels_v1_0_0(ISeries<double> input , string gexFilePath, int refreshCheckSeconds, int staleHoursThreshold, bool showStructural, bool showIntraday, bool showZeroDTE, bool showDex, bool showExpectedMove, bool showExtendedWalls, bool showLevelLabels, bool showStatusLine, int lineWidth, int labelPixelOffset, int labelBackgroundOpacity, bool showPanel, string pythonPath, string scriptPath, string briefingScriptPath, string briefingPdfPath, string intradaySnapshotsFolder)
		{
			return indicator.GexLevels_v1_0_0(input, gexFilePath, refreshCheckSeconds, staleHoursThreshold, showStructural, showIntraday, showZeroDTE, showDex, showExpectedMove, showExtendedWalls, showLevelLabels, showStatusLine, lineWidth, labelPixelOffset, labelBackgroundOpacity, showPanel, pythonPath, scriptPath, briefingScriptPath, briefingPdfPath, intradaySnapshotsFolder);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.GexLevels_v1_0_0 GexLevels_v1_0_0(string gexFilePath, int refreshCheckSeconds, int staleHoursThreshold, bool showStructural, bool showIntraday, bool showZeroDTE, bool showDex, bool showExpectedMove, bool showExtendedWalls, bool showLevelLabels, bool showStatusLine, int lineWidth, int labelPixelOffset, int labelBackgroundOpacity, bool showPanel, string pythonPath, string scriptPath, string briefingScriptPath, string briefingPdfPath, string intradaySnapshotsFolder)
		{
			return indicator.GexLevels_v1_0_0(Input, gexFilePath, refreshCheckSeconds, staleHoursThreshold, showStructural, showIntraday, showZeroDTE, showDex, showExpectedMove, showExtendedWalls, showLevelLabels, showStatusLine, lineWidth, labelPixelOffset, labelBackgroundOpacity, showPanel, pythonPath, scriptPath, briefingScriptPath, briefingPdfPath, intradaySnapshotsFolder);
		}

		public Indicators.GexLevels_v1_0_0 GexLevels_v1_0_0(ISeries<double> input , string gexFilePath, int refreshCheckSeconds, int staleHoursThreshold, bool showStructural, bool showIntraday, bool showZeroDTE, bool showDex, bool showExpectedMove, bool showExtendedWalls, bool showLevelLabels, bool showStatusLine, int lineWidth, int labelPixelOffset, int labelBackgroundOpacity, bool showPanel, string pythonPath, string scriptPath, string briefingScriptPath, string briefingPdfPath, string intradaySnapshotsFolder)
		{
			return indicator.GexLevels_v1_0_0(input, gexFilePath, refreshCheckSeconds, staleHoursThreshold, showStructural, showIntraday, showZeroDTE, showDex, showExpectedMove, showExtendedWalls, showLevelLabels, showStatusLine, lineWidth, labelPixelOffset, labelBackgroundOpacity, showPanel, pythonPath, scriptPath, briefingScriptPath, briefingPdfPath, intradaySnapshotsFolder);
		}
	}
}

#endregion
