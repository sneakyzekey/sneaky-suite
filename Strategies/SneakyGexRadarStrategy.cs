#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using D2D = SharpDX.Direct2D1;
using DW = SharpDX.DirectWrite;
#endregion

// SneakyGexRadarStrategy  (STANDALONE - needs no indicator)
// One file that does everything the Sneaky GEX Radar indicator does AND trades it:
//   * reads GexLevels_<NQ|ES|YM>.csv / GexProfile_*.csv from your NinjaTrader user-data folder (same files as the indicator)
//   * draws level lines, order-block / FVG zones, entry/stop/target plan, B/W/X/P/C/F marks, FROZEN WALLs, GEX 1-5 strikes,
//     the Radar card / pill and the toggle chips (all draggable, same as the indicator)
//   * detects every setup type: Stop-hunt sweep, FVG retest, Gamma breakout, Wall-touch fade, Flip breakdown, Pin-trap reversal, GEX chop range
//   * trades them: market entry on the bar after the setup, stop + target (price OCO), optional breakeven, time stop,
//     exit on opposite setup, flatten time, daily loss limit, max trades per day, session window
//   The checkboxes under "7 Setup types" (+ "Enable FVG retest entries") decide which setups are drawn AND traded.
//   Also built in (ported from the Kinetic / Pin Payoff / GEX Profile indicators): the Kinetic Cockpit card and the Pin Payoff + GEX Profile strips.
//   Needs NO custom indicator (only NinjaTrader's own EMA/ATR/ADX/RSI/SMA). Do NOT keep the Sneaky GEX Radar / Kinetic / Pin Payoff / GEX Profile indicators on the same chart: both would draw the same things and write the same status file.
//   Real-time only by default (the levels file is today's snapshot, so replaying it over old bars is not a real backtest).
//   Closed trades are appended to radar_strategy_<root>.csv; every setup's outcome goes to SneakyExport/radar_<instrument>_<bars>.csv.
namespace NinjaTrader.NinjaScript.Strategies
{
    public enum RadarStratCorner { TopLeft, TopRight, BottomLeft, BottomRight }
    public enum GexProfileScopeS { Near, All }
    public enum PinSourceS { Auto, ProfileMagnet, CsvPinStrike, Manual }
    public enum WingPresetS { Min15, Std40, Wide70, Max95 }
    public enum TradeSideS { Both, LongOnly, ShortOnly }

    public class SneakyGexRadarStrategy : Strategy
    {
        private class Lvl { public double Price; public string Label; public string Type; }
        private class Zone { public int Bar, Born, Tested; public double Lo, Hi, Vol, Pct; public bool Bull, Dead, Fvg, InZone; public int Uses; }
        private class Setup
        {
            public int Dir, Bar, Score, State, Held, Kind; public DateTime Time;
            public double Level, Entry, Stop, Target, R;
            public string LevelLabel; public bool Ob, Fvg;
        }
        private class Mark { public int Bar, Dir, Kind; public double Price; }

        private readonly object sync = new object();
        private List<Lvl> levels = new List<Lvl>();
        private List<Zone> zones = new List<Zone>();
        private List<Setup> setups = new List<Setup>();
        private List<Mark> marks = new List<Mark>();
        private DateTime levelsStamp = DateTime.MinValue, lastCheck = DateTime.MinValue;
        private string levelsInfo = "levels: not loaded";
        private string gexLevelsPathNote = "", gexProfilePathNote = "";
        private EMA emaTrend; private ATR atrI;
        private StreamWriter rec;
        private double netGex = 0; private bool netGexKnown = false; private Dictionary<int, int> lastGexBar = new Dictionary<int, int>();
        private int lastSignalBarLong = -1000, lastSignalBarShort = -1000, lastFvgBarLong = -1000, lastFvgBarShort = -1000;
        // "Require break of signal bar" bookkeeping: a setup that's waiting for a later bar to break its own signal bar's high/low
        private Setup pendingSetup; private int pendingDir, pendingSignalBar; private double pendingSignalHigh, pendingSignalLow;

        // ------------------------------------------------------------------ properties
        // ------------------------------------------------------------------ properties
        [Display(Name = "Levels CSV (GexLevels_NQ.csv)", GroupName = "1 Levels", Order = 0, Description = "Leave empty: picks GexLevels_NQ / _ES / _YM.csv from your NinjaTrader user-data folder by chart instrument.")]
        public string LevelsPath { get; set; }
        [Display(Name = "Extra level prices (comma separated)", GroupName = "1 Levels", Order = 1)]
        public string ExtraLevels { get; set; }
        [Range(1, 600)]
        [Display(Name = "Reload check (sec)", GroupName = "1 Levels", Order = 2)]
        public int ReloadSec { get; set; }

        [Range(0, 100)]
        [Display(Name = "Min score to signal", GroupName = "2 Setup rules", Order = 0)]
        public int MinScore { get; set; }
        [Range(0, 200)]
        [Display(Name = "Sweep penetration (ticks)", GroupName = "2 Setup rules", Order = 1)]
        public int PenTicks { get; set; }
        [Range(0.1, 10.0)]
        [Display(Name = "Max sweep depth (ATR)", GroupName = "2 Setup rules", Order = 2)]
        public double MaxDepthAtr { get; set; }
        [Range(0.5, 10.0)]
        [Display(Name = "Order block displacement (ATR)", GroupName = "2 Setup rules", Order = 3)]
        public double DispAtr { get; set; }
        [Range(0.0, 5.0)]
        [Display(Name = "Min FVG size (ATR)", GroupName = "2 Setup rules", Order = 4)]
        public double FvgMinAtr { get; set; }
        [Range(2, 500)]
        [Display(Name = "Trend EMA", GroupName = "2 Setup rules", Order = 5)]
        public int TrendEma { get; set; }
        [Range(2, 200)]
        [Display(Name = "ATR length", GroupName = "2 Setup rules", Order = 6)]
        public int AtrLen { get; set; }
        [Range(0, 200)]
        [Display(Name = "Cooldown (bars per level/side)", GroupName = "2 Setup rules", Order = 7)]
        public int CooldownBars { get; set; }

        [Range(0, 100)]
        [Display(Name = "Stop buffer (ticks)", GroupName = "3 Plan", Order = 0)]
        public int StopBufTicks { get; set; }
        [Range(1, 1000)]
        [Display(Name = "Min risk (ticks)", GroupName = "3 Plan", Order = 1)]
        public int MinRiskTicks { get; set; }
        [Range(1, 2000)]
        [Display(Name = "Max risk (ticks)", GroupName = "3 Plan", Order = 2)]
        public int MaxRiskTicks { get; set; }
        [Range(0.5, 10.0)]
        [Display(Name = "Fallback target (R)", GroupName = "3 Plan", Order = 3)]
        public double TargetR { get; set; }
        [Range(1.0, 50.0)]
        [Display(Name = "Max target distance (R) - farther levels use the fallback", GroupName = "3 Plan", Order = 11)]
        public double MaxTargetR { get; set; }
        [Range(1, 500)]
        [Display(Name = "Max bars to resolve", GroupName = "3 Plan", Order = 4)]
        public int MaxAgeBars { get; set; }

        [Display(Name = "Corner", GroupName = "4 Display", Order = 0)]
        public RadarStratCorner Corner { get; set; }
        [Range(0, 2000)]
        [Display(Name = "Offset X (px)", GroupName = "4 Display", Order = 1)]
        public int OffsetX { get; set; }
        [Range(0, 2000)]
        [Display(Name = "Offset Y (px)", GroupName = "4 Display", Order = 2)]
        public int OffsetY { get; set; }
        [Range(0.5, 2.0)]
        [Display(Name = "Panel scale", GroupName = "4 Display", Order = 3)]
        public double PanelScale { get; set; }
        [Range(1, 6)]
        [Display(Name = "Max rows in panel", GroupName = "4 Display", Order = 4)]
        public int MaxRows { get; set; }
        [Display(Name = "Draw level lines", GroupName = "4 Display", Order = 5, Description = "Off by default if your GexLevels indicator already draws them.")]
        public bool ShowLevels { get; set; }
        [Range(0, 100)]
        [Display(Name = "Touch distance (ticks)", GroupName = "4 Display", Order = 6)]
        public int TouchTicks { get; set; }
        [Display(Name = "Draw entry/stop/target", GroupName = "4 Display", Order = 7)]
        public bool ShowPlan { get; set; }
        [Display(Name = "Draw order-block / FVG zones", GroupName = "4 Display", Order = 8, Description = "Shaded zones with strength %, times tested and volume.")]
        public bool ShowZones { get; set; }
        [Range(1, 20)]
        [Display(Name = "Max zones drawn (nearest to price)", GroupName = "4 Display", Order = 9)]
        public int MaxZones { get; set; }
        [Display(Name = "Mark FROZEN WALLs", GroupName = "4 Display", Order = 10, Description = "Rule: a call/put wall tested at least N times this session without a close through it.")]
        public bool ShowFrozen { get; set; }
        [Range(2, 20)]
        [Display(Name = "Frozen wall: min tests", GroupName = "4 Display", Order = 11)]
        public int FrozenTouches { get; set; }
        [Display(Name = "Compact pill (hide card body)", GroupName = "4 Display", Order = 13, Description = "One-line pill like 'RADAR [SCALPER]: 1 CHANCES (NQ 99%) - LIVE' instead of the full card.")]
        public bool CompactCard { get; set; }
        [Display(Name = "Show GEX 1-5 strikes", GroupName = "4 Display", Order = 12, Description = "The five strikes with the largest |net GEX| near price, read from GexProfile_<ticker>.csv.")]
        public bool ShowGexRank { get; set; }


        [Display(Name = "Draw above price bars", GroupName = "4 Display", Order = 14, Description = "ON: zone labels, marks and panels are drawn on top of the candles. OFF: everything sits behind the candles (old behaviour).")]
        public bool DrawOnTop { get; set; }

        [Display(Name = "Enable FVG retest entries", GroupName = "6 FVG entries", Order = 0, Description = "Extra entry type: price retraces into a fresh fair-value gap and a candle closes back out in the trend direction. Shown with an F mark and logged as 'FVG retest'. Does not need a GEX level.")]
        public bool FvgEntry { get; set; }
        [Display(Name = "Require trend EMA agreement", GroupName = "6 FVG entries", Order = 2)]
        public bool FvgTrend { get; set; }
        [Range(5, 400)]
        [Display(Name = "Max FVG age (bars)", GroupName = "6 FVG entries", Order = 2)]
        public int FvgMaxAge { get; set; }
        [Range(1, 20)]
        [Display(Name = "Max entries per FVG", GroupName = "6 FVG entries", Order = 3, Description = "How many times the same gap can signal on later retests while it is still drawn (unbroken).")]
        public int FvgMaxUses { get; set; }
        [Range(0, 200)]
        [Display(Name = "FVG cooldown (bars per side)", GroupName = "6 FVG entries", Order = 4)]
        public int FvgCooldown { get; set; }
        [Range(0, 100)]
        [Display(Name = "Min FVG score", GroupName = "6 FVG entries", Order = 5)]
        public int FvgMinScore { get; set; }

        [Display(Name = "Stop-hunt sweep", GroupName = "7 Setup types", Order = 0, Description = "Original radar setup: wick through a level and close back.")]
        public bool EnableSweep { get; set; }
        [Display(Name = "Gamma breakout", GroupName = "7 Setup types", Order = 1, Description = "EXPERIMENTAL: close through a call/put wall by >=0.2 ATR -> continuation.")]
        public bool EnableBreakout { get; set; }
        [Display(Name = "Wall-touch fade", GroupName = "7 Setup types", Order = 2, Description = "EXPERIMENTAL: touch a call/put wall without closing through, close back by >=0.2 ATR -> fade.")]
        public bool EnableWallFade { get; set; }
        [Display(Name = "Flip breakdown", GroupName = "7 Setup types", Order = 3, Description = "EXPERIMENTAL: close through a gamma flip / vol trigger / vanna flip level -> continuation.")]
        public bool EnableFlip { get; set; }
        [Display(Name = "Pin-trap reversal", GroupName = "7 Setup types", Order = 4, Description = "EXPERIMENTAL: price stretched >=2.5 ATR from a pin / charm magnet with a reversal candle back toward it.")]
        public bool EnablePinTrap { get; set; }
        [Display(Name = "GEX chop range", GroupName = "7 Setup types", Order = 5, Description = "EXPERIMENTAL: positive net GEX + choppy price: fade the edges of the nearest levels toward the middle.")]
        public bool EnableChop { get; set; }

        [Display(Name = "Alert (sound + log) on new setup", GroupName = "5 Output", Order = 0)]
        public bool AlertOn { get; set; }
        [Display(Name = "Record results CSV", GroupName = "5 Output", Order = 1)]
        public bool LogCsv { get; set; }

        // ------------------------------------------------------------------ 0 Trading
        [Display(Name = "Contracts", GroupName = "0 Trading", Order = 0)]
        [Range(1, 1000)]
        public int Quantity { get; set; }
        [Display(Name = "Trade historical bars (fake backtest - levels are today's)", GroupName = "0 Trading", Order = 1, Description = "OFF (default): only live bars are scanned and traded. ON: old bars are checked against TODAY's levels (look-ahead; for eyeballing only).")]
        public bool TradeHistorical { get; set; }
        [Display(Name = "Max trades per day (0 = unlimited)", GroupName = "0 Trading", Order = 2)]
        [Range(0, 100)]
        public int MaxTradesPerDay { get; set; }
        [Display(Name = "Daily loss limit $ (0 = off)", GroupName = "0 Trading", Order = 3)]
        [Range(0, 1000000)]
        public double DailyLossLimit { get; set; }
        [Display(Name = "Use session window", GroupName = "0 Trading", Order = 4)]
        public bool UseSession { get; set; }
        [Display(Name = "Session start (HHmm, chart time)", GroupName = "0 Trading", Order = 5)]
        [Range(0, 2359)]
        public int SessionStart { get; set; }
        [Display(Name = "Session end (HHmm, chart time)", GroupName = "0 Trading", Order = 6)]
        [Range(0, 2359)]
        public int SessionEnd { get; set; }
        [Display(Name = "Min score to trade", GroupName = "0 Trading", Order = 7, Description = "Applied on top of the setup's own score rules. 0 = trade every setup shown.")]
        [Range(0, 100)]
        public int MinTradeScore { get; set; }
        [Display(Name = "Trade side", GroupName = "0 Trading", Order = 8, Description = "Both (default): take every qualifying setup, long or short. Long only / Short only: ignore setups in the other direction entirely (they're still drawn/scored, just never traded).")]
        public TradeSideS TradeSide { get; set; }

        [Display(Name = "Require break of signal bar", GroupName = "2b Entry filters", Order = 0, Description = "OFF (default): enter right on the setup's own signal bar, as before. ON: instead of entering immediately, hold the setup and wait - only enter once a LATER bar's high (for a long) / low (for a short) trades beyond the signal bar's own high/low, i.e. price actually follows through past where the setup printed. The setup is dropped (no trade) if the break hasn't happened within 'Max bars to resolve' bars (Plan group).")]
        public bool RequireBreakOfSignalBar { get; set; }
        [Display(Name = "Require N consecutive same-color bars", GroupName = "2b Entry filters", Order = 1, Description = "OFF (default): no bar-color requirement on the entry bar. ON: the bar an entry is about to trigger on, plus the N-1 bars immediately before it, must all be the same color as the trade direction - up-closed bars (Close > Open) for a long, down-closed bars (Close < Open) for a short (e.g. N=2 needs 'up bar after up bar' for a long). A doji (Close == Open) breaks the run and blocks that entry. Works standalone or combined with the break-of-signal-bar filter above (checked on whichever bar the entry would otherwise fire on).")]
        public bool RequireConsecutiveBars { get; set; }
        [Range(1, 20)]
        [Display(Name = "Consecutive bars needed", GroupName = "2b Entry filters", Order = 2)]
        public int ConsecutiveBarsCount { get; set; }

        // ------------------------------------------------------------------ 9 Panels & history
        [Display(Name = "Draw setups on history (no trading)", GroupName = "9 Panels & history", Order = 0, Description = "Scans old bars against TODAY's levels so zones, B/W/X/P/C/F marks and plan lines show on the existing chart. Draw-only: history bars are never traded. Look-ahead, for eyeballing only; results are not logged.")]
        public bool ScanHistory { get; set; }
        [Display(Name = "Show Kinetic Cockpit panel", GroupName = "9 Panels & history", Order = 1, Description = "Multi-timeframe strength tiles, order flow, RSI and the bias score (built in; adds M1-H1 data series).")]
        public bool ShowKinetic { get; set; }
        [Display(Name = "Show Pin Payoff panel (bottom strip)", GroupName = "9 Panels & history", Order = 2, Description = "Pin-target butterfly payoff + Eastern-time session timeline, drawn as a strip at the bottom of the price panel.")]
        public bool ShowPinPayoff { get; set; }
        [Display(Name = "Show GEX Profile panel (bottom strip)", GroupName = "9 Panels & history", Order = 3, Description = "Per-strike GEX histogram with calls / puts / net, magnet and barycenter, drawn as a strip at the bottom of the price panel.")]
        public bool ShowGexProfile { get; set; }
        [Range(120, 600)]
        [Display(Name = "Bottom card height at start (px)", GroupName = "9 Panels & history", Order = 4, Description = "Starting height of the Pin Payoff and GEX Profile cards. They start side by side at the bottom of the price chart; drag a card's top bar to move it, its bottom-right corner to resize it, double-click the bar to reset it.")]
        public int StripHeight { get; set; }
        [Display(Name = "Lite drawing while dragging a card", GroupName = "9 Panels & history", Order = 4, Description = "Keeps card dragging smooth: while you drag a card the zones, level lines, marks, plan lines and both bottom strips are skipped; they come back when you let go.")]
        public bool HideStripsOnDrag { get; set; }
        [Display(Name = "Show draw-time readout (ms)", GroupName = "9 Panels & history", Order = 9, Description = "Small grey line at the bottom-left of the chart with how many milliseconds the strategy spends drawing each frame.")]
        public bool ShowPerf { get; set; }
        [Display(Name = "Kinetic corner", GroupName = "9 Panels & history", Order = 5)]
        public RadarStratCorner KinCorner { get; set; }
        [Range(0, 2000)]
        [Display(Name = "Kinetic offset X (px)", GroupName = "9 Panels & history", Order = 6)]
        public int KinOffsetX { get; set; }
        [Range(0, 2000)]
        [Display(Name = "Kinetic offset Y (px)", GroupName = "9 Panels & history", Order = 7)]
        public int KinOffsetY { get; set; }
        [Range(0.5, 2.0)]
        [Display(Name = "Kinetic scale", GroupName = "9 Panels & history", Order = 8)]
        public double KinScale { get; set; }

        // ------------------------------------------------------------------ 8 Exits
        [Display(Name = "Time stop: exit after N bars (0 = off)", GroupName = "8 Exits", Order = 0)]
        [Range(0, 1000)]
        public int MaxHoldBars { get; set; }
        [Display(Name = "Move stop to breakeven at +R (0 = off)", GroupName = "8 Exits", Order = 1)]
        [Range(0, 20)]
        public double BreakevenR { get; set; }
        [Display(Name = "Breakeven offset (ticks)", GroupName = "8 Exits", Order = 2)]
        [Range(0, 50)]
        public int BreakevenTicks { get; set; }
        [Display(Name = "Flatten orphan account position", GroupName = "8 Exits", Order = 4, Description = "If the account holds a position in this instrument that this strategy does not own (for example left behind when the strategy was recompiled or reloaded mid-trade, which cancels its stop/target), flatten it and skip the entry instead of trading on top of it. Use a dedicated account for this strategy.")]
        public bool FlattenOrphan { get; set; }
        [Display(Name = "Keep running through connection loss", GroupName = "8 Exits", Order = 5, Description = "ON (recommended): a short loss of the price feed / connection (for example a broker's nightly reset) does not restart the strategy, so its stop and target stay working. OFF: NinjaTrader's default 'Recalculate' - the strategy is restarted after the drop, which cancels its stop and target and leaves an open position unprotected.")]
        public bool KeepRunningOnDisconnect { get; set; }
        [Display(Name = "Keep manual stop / target moves", GroupName = "8 Exits", Order = 6, Description = "ON: if you move this strategy's stop or target yourself (drag on the chart, Chart Trader, a trade HUD) the strategy keeps your price. OFF: NinjaTrader puts the stop / target back at the strategy's original price at the next bar close.")]
        public bool KeepManualMoves { get; set; }
        [Display(Name = "Exit on opposite setup", GroupName = "8 Exits", Order = 3, Description = "Close the position when a setup prints in the other direction.")]
        public bool ExitOnOpposite { get; set; }
        [Display(Name = "Flatten at time (HHmm, 0 = off)", GroupName = "8 Exits", Order = 4, Description = "Closes any open position at/after this chart time and blocks new entries until the next day.")]
        [Range(0, 2359)]
        public int FlattenTime { get; set; }


        // ------------------------------------------------------------------ lifecycle
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Sneaky GEX Radar Strategy";
                Description = "Standalone strategy: GEX stop-hunt radar (all setup types, zones, plan lines, panel) with built-in entries and exits. Needs no indicator.";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = false;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelCloseIgnoreRejects;   // a rejected order no longer disables the strategy (handled in OnOrderUpdate)
                BarsRequiredToTrade = 60;
                IsInstantiatedOnEachOptimizationIteration = true;

                Quantity = 1; TradeHistorical = false; MaxTradesPerDay = 0; DailyLossLimit = 0; MinTradeScore = 0; TradeSide = TradeSideS.Both;
                RequireBreakOfSignalBar = false; RequireConsecutiveBars = false; ConsecutiveBarsCount = 2;
                UseSession = false; SessionStart = 930; SessionEnd = 1600;
                ScanHistory = true; ShowKinetic = true; ShowPinPayoff = true; ShowGexProfile = true; StripHeight = 230; HideStripsOnDrag = true; ShowPerf = true;
                KinCorner = RadarStratCorner.TopRight; KinOffsetX = 70; KinOffsetY = 40; KinScale = 1.0;
                MaxHoldBars = 30; BreakevenR = 0; BreakevenTicks = 1; ExitOnOpposite = false; FlattenTime = 0; FlattenOrphan = true; KeepRunningOnDisconnect = true; KeepManualMoves = true;

                LevelsPath = ""; ExtraLevels = ""; ReloadSec = 30;
                MinScore = 50; PenTicks = 2; MaxDepthAtr = 1.5; DispAtr = 1.5; FvgMinAtr = 0.25; TrendEma = 50; AtrLen = 14; CooldownBars = 12;
                StopBufTicks = 2; MinRiskTicks = 4; MaxRiskTicks = 120; TargetR = 2.0; MaxTargetR = 5.0; MaxAgeBars = 30;
                Corner = RadarStratCorner.TopLeft; OffsetX = 12; OffsetY = 30; PanelScale = 1.0; MaxRows = 3;
                ShowLevels = false; TouchTicks = 4; ShowPlan = true; ShowZones = true; MaxZones = 6; ShowFrozen = true; FrozenTouches = 3; ShowGexRank = true; CompactCard = false;
                AlertOn = true; LogCsv = true;
                EnableSweep = true; EnableBreakout = false; EnableWallFade = false; EnableFlip = false; EnablePinTrap = false; EnableChop = false;
                DrawOnTop = true; FvgEntry = false; FvgTrend = true; FvgMaxAge = 400; FvgMaxUses = 3; FvgCooldown = 6; FvgMinScore = 0;
            }
            else if (State == State.Configure)
            {
                // A short feed drop (e.g. a broker's nightly reset) with the NinjaTrader default (Recalculate) RESTARTS the strategy:
                // the restart cancels its working stop + target and the open position is left unprotected. KeepRunning leaves the
                // strategy and its orders alone through the drop.
                ConnectionLossHandling = KeepRunningOnDisconnect ? ConnectionLossHandling.KeepRunning : ConnectionLossHandling.Recalculate;
                if (ShowKinetic) { int[] tfm = { 1, 3, 5, 15, 30, 60 }; for (int i = 0; i < tfm.Length; i++) AddDataSeries(BarsPeriodType.Minute, tfm[i]); }
                // ZOrder is looked up by reflection so the file compiles on every NT8 build
                try { System.Reflection.PropertyInfo zp = GetType().GetProperty("ZOrder"); if (zp != null && zp.CanWrite) zp.SetValue(this, DrawOnTop ? 10020 : 0, null); } catch { }
            }
            else if (State == State.Historical)
            {
                if (ChartControl != null) ChartControl.Dispatcher.InvokeAsync(() => { AttachDrag(); if (kin != null) kin.AttachDrag(); if (pin != null) pin.AttachClick(); if (prof != null) prof.AttachClick(); });
            }
            else if (State == State.DataLoaded)
            {
                LoadUi();
                emaTrend = EMA(Close, TrendEma);
                atrI = ATR(Close, AtrLen);
                if (ShowKinetic) { kin = new KineticModule(this); kin.DataLoaded(); }
                if (ShowPinPayoff) pin = new PinPayoffModule(this);
                if (ShowGexProfile) prof = new GexProfileModule(this);
            }
            else if (State == State.Terminated)
            {
                try { if (ChartControl != null) ChartControl.Dispatcher.InvokeAsync(() => DetachDrag()); else DetachDrag(); } catch { }
                try { if (kin != null) kin.Terminate(); if (pin != null) pin.Terminate(); if (prof != null) prof.Terminate(); } catch { }
                try { if (trec != null) { trec.Flush(); trec.Dispose(); } } catch { } trec = null;
                try { if (rec != null) { rec.Flush(); rec.Dispose(); } } catch { }
                rec = null;
                lock (sync) DisposeGfx();
            }
        }

        // ------------------------------------------------------------------ levels
        // Falls back to the literal %USERPROFILE%\Documents\NinjaTrader 8 folder when a GEX
        // data file isn't where NinjaTrader's own Globals.UserDataDir points. fetch_gex_nq.py
        // writes via Python's os.path.expanduser("~"), which does NOT follow a redirected
        // Windows Documents folder (e.g. moved onto a D: drive) the way NinjaTrader's own
        // Known-Folder-aware UserDataDir does -- so on a machine with Documents redirected,
        // the two can point at different folders unless GEX_DATA_DIR is set to match. This
        // keeps the chart working either way and reports which folder it actually used.
        private string ResolveGexFile(string fileName, out string foundNote)
        {
            string primaryDir = NinjaTrader.Core.Globals.UserDataDir;
            string primary = Path.Combine(primaryDir, fileName);
            if (File.Exists(primary)) { foundNote = ""; return primary; }

            string fallbackDir = null;
            try
            {
                string up = Environment.GetEnvironmentVariable("USERPROFILE");
                if (!string.IsNullOrEmpty(up)) fallbackDir = Path.Combine(up, "Documents", "NinjaTrader 8");
            }
            catch { }

            if (!string.IsNullOrEmpty(fallbackDir) && !string.Equals(fallbackDir, primaryDir, StringComparison.OrdinalIgnoreCase))
            {
                string fb = Path.Combine(fallbackDir, fileName);
                if (File.Exists(fb))
                {
                    foundNote = "using " + fileName + " from " + fallbackDir + " (not " + primaryDir + " -- set GEX_DATA_DIR to fix)";
                    return fb;
                }
            }

            foundNote = "";
            return primary; // neither exists -- let the caller's own "not found" message report this path
        }

        private string ResolvePath()
        {
            if (!string.IsNullOrEmpty(LevelsPath)) return LevelsPath;
            string root = Instrument.MasterInstrument.Name.ToUpperInvariant();
            string tk = (root == "ES" || root == "MES") ? "ES" : ((root == "YM" || root == "MYM") ? "YM" : "NQ");
            string note;
            string p = ResolveGexFile("GexLevels_" + tk + ".csv", out note);
            gexLevelsPathNote = note;
            return p;
        }

        private void ReloadLevelsIfNeeded()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - lastCheck).TotalSeconds < ReloadSec && levels.Count > 0) return;
            lastCheck = now;
            try
            {
                List<Lvl> list = new List<Lvl>(); double newNet = 0; bool newNetKnown = false;
                string p = ResolvePath();
                string info = "levels: ";
                if (File.Exists(p))
                {
                    DateTime stamp = File.GetLastWriteTimeUtc(p);
                    if (stamp == levelsStamp && levels.Count > 0) return;
                    levelsStamp = stamp;
                    string[] lines;
                    using (FileStream fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader sr = new StreamReader(fs))
                        lines = sr.ReadToEnd().Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string raw in lines)
                    {
                        string line = raw.Trim();
                        if (line.StartsWith("#") && line.IndexOf("net_gex_signed=", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            int gi = line.IndexOf("net_gex_signed=", StringComparison.OrdinalIgnoreCase) + 15; int ge = gi;
                            while (ge < line.Length && (char.IsDigit(line[ge]) || line[ge] == '-' || line[ge] == '+' || line[ge] == '.' || line[ge] == 'e' || line[ge] == 'E')) ge++;
                            double ng; if (double.TryParse(line.Substring(gi, ge - gi), NumberStyles.Float, CultureInfo.InvariantCulture, out ng)) { newNet = ng; newNetKnown = true; }
                        }
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("Price,", StringComparison.OrdinalIgnoreCase)) continue;
                        string[] a = line.Split(',');
                        double px;
                        if (a.Length < 2 || !double.TryParse(a[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out px) || px <= 0) continue;
                        Lvl l = new Lvl(); l.Price = px; l.Label = a[1].Trim(); l.Type = a.Length > 2 ? a[2].Trim() : "";
                        list.Add(l);
                    }
                    info += list.Count + " from file (" + stamp.ToLocalTime().ToString("MM-dd HH:mm") + ")";
                }
                else info += "file not found";
                if (!string.IsNullOrEmpty(ExtraLevels))
                {
                    foreach (string t in ExtraLevels.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        double px;
                        if (double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out px) && px > 0)
                        { Lvl l = new Lvl(); l.Price = px; l.Label = "Manual " + px.ToString("0.##", CultureInfo.InvariantCulture); l.Type = "Manual"; list.Add(l); }
                    }
                }
                lock (sync) { levels = list; netGex = newNet; netGexKnown = newNetKnown; levelsInfo = info + (list.Count == 0 ? " - radar idle" : ""); }
            }
            catch (Exception ex) { lock (sync) levelsInfo = "levels: read error " + ex.Message; }
        }


        // ------------------------------------------------------------------ main logic
        private void Notify(string kind, Setup q, string where)
        {
            if (!AlertOn || State != State.Realtime) return;
            string msg = "Radar " + kind + (q.Dir > 0 ? " LONG " : " SHORT ") + Instrument.FullName + " score " + q.Score + " @ " + q.Level.ToString("F2", CultureInfo.InvariantCulture);
            Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " " + msg);
            try { PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav"); } catch { }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            try
            {
                // NinjaTrader re-sends a Set-method stop / target at its remembered price on every bar, which snaps a stop or target
                // you moved by hand (chart drag, Chart Trader, a trade HUD...) back to the original. So when the working stop / target
                // shows a price this strategy did not set, adopt it as the new remembered price.
                if (KeepManualMoves && order != null && Position.MarketPosition != MarketPosition.Flat
                    && (orderState == OrderState.Working || orderState == OrderState.Accepted))
                {
                    string sig = Position.MarketPosition == MarketPosition.Long ? "RadarLong" : "RadarShort";
                    if (order.Name == "Stop loss" && stopPrice > 0 && Math.Abs(stopPrice - keptStop) > TickSize / 2)
                    {
                        keptStop = stopPrice; movedByHand = true; SetStopLoss(sig, CalculationMode.Price, stopPrice, false);
                        Print(DateTime.Now.ToString("HH:mm:ss") + " SGR stop moved outside the strategy -> keeping " + stopPrice.ToString("F2", CultureInfo.InvariantCulture));
                    }
                    else if (order.Name == "Profit target" && limitPrice > 0 && Math.Abs(limitPrice - keptTarget) > TickSize / 2)
                    {
                        keptTarget = limitPrice; movedByHand = true; SetProfitTarget(sig, CalculationMode.Price, limitPrice);
                        Print(DateTime.Now.ToString("HH:mm:ss") + " SGR target moved outside the strategy -> keeping " + limitPrice.ToString("F2", CultureInfo.InvariantCulture));
                    }
                }
                if (orderState != OrderState.Rejected) return;
                Print(DateTime.Now.ToString("HH:mm:ss") + " SGR order REJECTED: " + order.Name + " " + order.OrderAction + " " + order.OrderType + " lim " + limitPrice + " stop " + stopPrice + "  " + error + " " + comment);
                // a rejected protective order must never leave a position unprotected: flatten, but keep the strategy running
                if (Position.MarketPosition != MarketPosition.Flat && order.Name != "RadarLong" && order.Name != "RadarShort")
                    CloseAll("RejectGuard");
            }
            catch { }
        }

        protected override void OnBarUpdate()
        {
            try { OnBarUpdateCore(); }
            catch (Exception ex) { Print(DateTime.Now.ToString("HH:mm:ss") + " SGR OnBarUpdate error (strategy kept running): " + ex.Message + "  @ " + ex.StackTrace); }
        }

        private void OnBarUpdateCore()
        {
            if (kin != null) { try { kin.OnBar(); } catch { } }
            if (BarsInProgress != 0) return;
            if (pin != null) { try { pin.OnBar(); } catch { } }
            if (prof != null) { try { prof.OnBar(); } catch { } }
            if (CurrentBar < Math.Max(TrendEma, AtrLen) + 5) return;
            ReloadLevelsIfNeeded();
            WriteStatus();
            double atr = atrI[0];
            if (atr <= 0) return;
            ReloadRankIfNeeded();
            lock (sync) TrackWalls(atr);

            int hm = Time[0].Hour * 100 + Time[0].Minute;
            if (Time[0].Date != tradeDay) { tradeDay = Time[0].Date; tradesToday = 0; }

            // exits that run on every bar while a position is open
            bool inPos = Position.MarketPosition != MarketPosition.Flat;
            if (inPos)
            {
                if (MaxHoldBars > 0 && BarsSinceEntryExecution(0, "", 0) >= MaxHoldBars) { CloseAll("TimeStop"); inPos = false; }
                else if (FlattenTime > 0 && hm >= FlattenTime) { CloseAll("Flatten"); inPos = false; }
                else ManageBreakeven();
            }

            // one line per start so the Output window shows what the strategy found when it went live (helps diagnose restarts)
            if (State == State.Realtime && !rtNoted)
            {
                rtNoted = true;
                try
                {
                    string acc = "flat";
                    if (Account != null)
                        foreach (NinjaTrader.Cbi.Position ap in Account.Positions)
                            if (ap != null && ap.Instrument != null && ap.Instrument.FullName == Instrument.FullName && ap.MarketPosition != MarketPosition.Flat) { acc = ap.MarketPosition + " " + ap.Quantity; break; }
                    Print(DateTime.Now.ToString("HH:mm:ss") + " SGR live: strategy position " + Position.MarketPosition + ", account position " + acc + ", flatten-orphan " + (FlattenOrphan ? "ON" : "OFF") + ", keep-running-on-disconnect " + (KeepRunningOnDisconnect ? "ON" : "OFF"));
                }
                catch { }
            }

            // a position on the account that this strategy does not own has no stop/target: never trade on top of it
            if (!inPos && Position.MarketPosition == MarketPosition.Flat && DateTime.Now >= noOrphanUntil && FlattenOrphan && State == State.Realtime && HandleOrphan()) { lock (sync) UpdateZones(atr); return; }

            bool canTrade = State == State.Realtime || TradeHistorical;
            if (!canTrade && !ScanHistory) { lock (sync) UpdateZones(atr); return; }

            Setup best = null;
            lock (sync)
            {
                UpdateZones(atr);
                ResolveSetups();
                List<Setup> fresh = new List<Setup>();
                Setup s = EnableSweep ? DetectNew(atr) : null;
                if (s != null)
                {
                    setups.Add(s); fresh.Add(s);
                    Mark m = new Mark(); m.Bar = CurrentBar; m.Dir = s.Dir; m.Price = s.Dir > 0 ? Low[0] : High[0]; marks.Add(m);
                    if (s.Dir > 0) lastSignalBarLong = CurrentBar; else lastSignalBarShort = CurrentBar;
                    Notify("STOP-HUNT", s, "");
                }
                Setup g = DetectGex(atr);
                if (g != null)
                {
                    setups.Add(g); fresh.Add(g);
                    Mark gm = new Mark(); gm.Bar = CurrentBar; gm.Dir = g.Dir; gm.Kind = g.Kind; gm.Price = g.Dir > 0 ? Low[0] : High[0]; marks.Add(gm);
                    Notify(KindName(g.Kind), g, "");
                }
                if (FvgEntry)
                {
                    Setup f = DetectFvg(atr);
                    if (f != null)
                    {
                        setups.Add(f); fresh.Add(f);
                        Mark fm = new Mark(); fm.Bar = CurrentBar; fm.Dir = f.Dir; fm.Kind = 1; fm.Price = f.Dir > 0 ? Low[0] : High[0]; marks.Add(fm);
                        Notify("FVG RETEST", f, "");
                    }
                }
                for (int i = fresh.Count - 1; i >= 0; i--) if (best == null || fresh[i].Score > best.Score) best = fresh[i];
                LogTouches();
                if (setups.Count > 200) setups.RemoveRange(0, setups.Count - 200);
                if (marks.Count > 400) marks.RemoveRange(0, marks.Count - 400);
            }
            if (!canTrade) return;

            // ---------------- entry
            if (inPos)
            {
                if (best != null)
                {
                    bool opposite = (Position.MarketPosition == MarketPosition.Long && best.Dir < 0) || (Position.MarketPosition == MarketPosition.Short && best.Dir > 0);
                    if (ExitOnOpposite && opposite) CloseAll("OppositeSetup");
                }
                pendingSetup = null;   // a position is open: any earlier pending breakout wait is moot
                return;   // one position at a time
            }

            // "Require break of signal bar": a setup that's waiting for a LATER bar to break its own signal bar's high/low.
            // Checked every bar it stays alive, even bars with no fresh setup of their own.
            if (pendingSetup != null)
            {
                if (CurrentBar - pendingSignalBar > MaxAgeBars) pendingSetup = null;
                else
                {
                    bool brokeOut = pendingDir > 0 ? High[0] > pendingSignalHigh : Low[0] < pendingSignalLow;
                    if (brokeOut)
                    {
                        Setup toEnter = pendingSetup; int pDir = pendingDir;
                        pendingSetup = null;   // one shot: this pending setup is resolved either way
                        if (!RequireConsecutiveBars || ConsecutiveBarsOk(pDir)) PlaceEntry(toEnter, pDir, atr);
                    }
                }
            }
            if (Position.MarketPosition != MarketPosition.Flat || lastEntryBar == CurrentBar) return;   // the pending check above may have just filled this bar

            if (best == null) return;
            int dir = best.Dir;
            if (TradeSide == TradeSideS.LongOnly && dir < 0) return;
            if (TradeSide == TradeSideS.ShortOnly && dir > 0) return;
            if (best.Score < MinTradeScore) return;
            if (MaxTradesPerDay > 0 && tradesToday >= MaxTradesPerDay) return;
            if (DailyLossLimit > 0 && TodayPnl() <= -DailyLossLimit) return;
            if (FlattenTime > 0 && hm >= FlattenTime) return;
            if (UseSession)
            {
                bool inside = SessionStart <= SessionEnd ? (hm >= SessionStart && hm < SessionEnd) : (hm >= SessionStart || hm < SessionEnd);
                if (!inside) return;
            }
            if (lastEntryBar == CurrentBar) return;

            if (RequireBreakOfSignalBar)
            {
                // hold the setup; only place the order once a later bar actually breaks this signal bar's high/low (see above)
                pendingSetup = best; pendingDir = dir; pendingSignalHigh = High[0]; pendingSignalLow = Low[0]; pendingSignalBar = CurrentBar;
                return;
            }
            if (RequireConsecutiveBars && !ConsecutiveBarsOk(dir)) return;
            PlaceEntry(best, dir, atr);
        }

        // true when the current bar, plus the (ConsecutiveBarsCount - 1) bars immediately before it, are all the same
        // color as the trade direction: Close > Open ("up bar") for a long, Close < Open ("down bar") for a short.
        private bool ConsecutiveBarsOk(int dir)
        {
            int n = ConsecutiveBarsCount;
            if (CurrentBar < n - 1) return false;
            for (int i = 0; i < n; i++)
            {
                if (dir > 0) { if (!(Close[i] > Open[i])) return false; }
                else { if (!(Close[i] < Open[i])) return false; }
            }
            return true;
        }

        private void PlaceEntry(Setup best, int dir, double atr)
        {
            // a protective stop / target must sit on the correct side of the market or NinjaTrader rejects the order
            double edge = 2 * TickSize;
            if (dir > 0 && !(best.Stop < Close[0] - edge && best.Target > Close[0] + edge)) return;
            if (dir < 0 && !(best.Stop > Close[0] + edge && best.Target < Close[0] - edge)) return;

            string nm = dir > 0 ? "RadarLong" : "RadarShort";
            SetStopLoss(nm, CalculationMode.Price, best.Stop, false);
            SetProfitTarget(nm, CalculationMode.Price, best.Target);
            keptStop = best.Stop; keptTarget = best.Target;
            try
            {
                CultureInfo ci = CultureInfo.InvariantCulture;
                double ng; bool ngk; lock (sync) { ng = netGex; ngk = netGexKnown; }
                entryCtx = (kin != null ? kin.CtxCsv() : ",,,,,") + "," + (ngk ? ng.ToString("0.###", ci) : "") + "," + (ngk ? (ng >= 0 ? "pos" : "neg") : "") + "," + atr.ToString("F2", ci) + ","
                         + (lastExitWall == DateTime.MinValue ? "" : (DateTime.Now - lastExitWall).TotalSeconds.ToString("F0", ci)) + "," + (double.IsNaN(lastTradePnl) ? "" : lastTradePnl.ToString("F2", ci));
            }
            catch { entryCtx = ",,,,,,,,,,"; }
            movedByHand = false;
            activePlan = best; beDone = false;
            lastEntryBar = CurrentBar; tradesToday++;
            if (dir > 0) EnterLong(Quantity, nm); else EnterShort(Quantity, nm);
            Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " RADAR ENTRY " + KindName(best.Kind) + " " + (dir > 0 ? "LONG" : "SHORT") + " score " + best.Score + " @ " + (best.LevelLabel ?? "") + " " + best.Level.ToString("F2", CultureInfo.InvariantCulture)
                + "  entry~" + best.Entry.ToString("F2", CultureInfo.InvariantCulture) + " SL " + best.Stop.ToString("F2", CultureInfo.InvariantCulture) + " TP " + best.Target.ToString("F2", CultureInfo.InvariantCulture));
        }

        // ------------------------------------------------------------------ built-in panels: state + glue
        private KineticModule kin; private PinPayoffModule pin; private GexProfileModule prof;
        protected override void OnMarketData(MarketDataEventArgs e) { try { if (kin != null) kin.OnTick(e); } catch { } }
        public override void OnRenderTargetChanged()
        {
            lock (sync) DisposeGfx();
            try { if (kin != null) kin.ResetGfx(); if (pin != null) pin.ResetGfx(); if (prof != null) prof.ResetGfx(); } catch { }
        }
        private double msRadar, msStrips, msKin, msGap, msLag; private long lastFrameTs, inputTs;
        public void Refresh()
        {
            try { ForceRefresh(); } catch { }
            try { if (ChartControl != null) ChartControl.InvalidateVisual(); } catch { }
        }
        public void RefreshLater()
        {
            Refresh();
            try { if (ChartControl != null) ChartControl.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { Refresh(); })); } catch { }
        }
        public void MarkInput() { if (inputTs == 0) inputTs = System.Diagnostics.Stopwatch.GetTimestamp(); }
        private readonly System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            sw.Restart();
            {
                long nowTs = System.Diagnostics.Stopwatch.GetTimestamp(); double tf = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (lastFrameTs != 0) { double gap = (nowTs - lastFrameTs) * tf; if (gap < 500) msGap = msGap * 0.7 + gap * 0.3; }
                lastFrameTs = nowTs;
                if (inputTs != 0) { msLag = (nowTs - inputTs) * tf; inputTs = 0; }
            }
            RadarRender(chartControl, chartScale);
            double rawRadar = sw.Elapsed.TotalMilliseconds;
            msRadar = msRadar * 0.8 + rawRadar * 0.2;
            if (RenderTarget == null || ChartPanel == null) return;
            try
            {
                float L = ChartPanel.X, T = ChartPanel.Y, W = ChartPanel.W, H = ChartPanel.H, B = T + H;
                bool busy = HideStripsOnDrag && (dragging || (kin != null && kin.IsDragging));
                cardPL = L; cardPT = T; cardPW = W; cardPH = H;
                if (!busy && (pin != null || prof != null))
                {
                    // floating cards: 0 = Pin Payoff, 1 = GEX Profile. Profile is drawn first so Pin Payoff is on top if they overlap.
                    for (int ci = 1; ci >= 0; ci--)
                    {
                        if (ci == 0 ? pin == null : prof == null) { cardPx[ci][2] = 0f; continue; }
                        if (ci == 0 ? !uiPin : !uiProf)          // hidden with its chip: no card, no grip, no clickable buttons
                        {
                            cardPx[ci][2] = 0f;
                            if (ci == 0) pin.XfSW = -1f; else prof.XfSW = -1f;
                            continue;
                        }
                        float[] f = cardF[ci];
                        if (f[3] <= 0f) CardDefault(ci, H);
                        float cw = Math.Max(Math.Min(CardMinW, W), Math.Min(W, f[2] * W)), ch = Math.Max(Math.Min(CardMinH, H), Math.Min(H, f[3] * H));
                        float cx = Math.Max(L, Math.Min(L + W - cw, L + f[0] * W)), cy = Math.Max(T, Math.Min(B - ch, T + f[1] * H));
                        cardPx[ci][0] = cx; cardPx[ci][1] = cy; cardPx[ci][2] = cw; cardPx[ci][3] = ch;
                        StripBg(cx, cy, cw, ch);
                        CardGrip(ci, cx, cy, cw, ch);
                        // the card content has a fixed pixel layout made for a wide strip: draw it scaled down so it fits a narrower card
                        float ck = Math.Max(0.35f, Math.Min(1f, cw / CardDesignW));
                        float oy = cy + CardGripH, sw2 = cw, sh2 = ch - CardGripH;
                        if (ci == 0) { pin.XfX = cx; pin.XfY = oy; pin.XfSW = sw2; pin.XfSH = sh2; pin.XfK = ck; }
                        else { prof.XfX = cx; prof.XfY = oy; prof.XfSW = sw2; prof.XfSH = sh2; prof.XfK = ck; }
                        bool clipped = false, xf = false;
                        SharpDX.Matrix3x2 oldXf = RenderTarget.Transform;
                        try
                        {
                            RenderTarget.PushAxisAlignedClip(new SharpDX.RectangleF(cx, oy, sw2, sh2), D2D.AntialiasMode.Aliased); clipped = true;
                            if (ck < 0.999f) { RenderTarget.Transform = SharpDX.Matrix3x2.Scaling(ck, ck, new SharpDX.Vector2(cx, oy)); xf = true; }
                            if (ci == 0) pin.Render(chartControl, chartScale, cx, oy, sw2 / ck, sh2 / ck);
                            else prof.Render(chartControl, chartScale, cx, oy, sw2 / ck, sh2 / ck);
                        }
                        catch { }
                        finally
                        {
                            if (xf) { try { RenderTarget.Transform = oldXf; } catch { } }
                            if (clipped) { try { RenderTarget.PopAxisAlignedClip(); } catch { } }
                        }
                    }
                }
                if (uiStats) StatsRender(L, T, W, H); else { statPx[2] = 0f; statRangeRect[2] = 0f; statGroupRect[2] = 0f; statAcctRect[2] = 0f; }
                msStrips = msStrips * 0.8 + (sw.Elapsed.TotalMilliseconds - rawRadar) * 0.2;
                if (kin != null)
                {
                    double t0 = sw.Elapsed.TotalMilliseconds;
                    if (uiKin) kin.Render(chartControl, chartScale, L, T, W, H);
                    else
                    {
                        // hidden with its chip: still run it (it computes the SYNC % the Radar line shows) but draw into an empty clip
                        bool kc = false;
                        try { RenderTarget.PushAxisAlignedClip(new SharpDX.RectangleF(0f, 0f, 0f, 0f), D2D.AntialiasMode.Aliased); kc = true; kin.Render(chartControl, chartScale, L, T, W, H); }
                        catch { }
                        finally { if (kc) { try { RenderTarget.PopAxisAlignedClip(); } catch { } } }
                        kin.Hide();
                    }
                    msKin = msKin * 0.8 + (sw.Elapsed.TotalMilliseconds - t0) * 0.2;
                }
                if (ShowPerf)
                {
                    lock (sync) Txt("SGR draw ms  radar " + msRadar.ToString("0.0", CultureInfo.InvariantCulture) + "  strips " + msStrips.ToString("0.0", CultureInfo.InvariantCulture) + "  kinetic " + msKin.ToString("0.0", CultureInfo.InvariantCulture)
                        + "  total " + sw.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)
                        + "   | chart frame gap " + msGap.ToString("0", CultureInfo.InvariantCulture) + " ms   click/drag -> draw " + msLag.ToString("0", CultureInfo.InvariantCulture) + " ms", L + 8f, T + H - 62f, 760f, 9.5f, CGrey, false);
                }
            }
            catch { }
        }
        private void StripBg(float x, float y, float w, float h)
        {
            try
            {
                lock (sync)
                {
                    RFill(x, y, w, h, 0f, 0x0A0F1A, 0.93f);
                    HLine(x, x + w, y, 0x00E5D0, 0.7f, 1f, false);
                }
            }
            catch { }
        }

        // ---- floating bottom cards (Pin Payoff / GEX Profile): drag the grip bar to move, drag the bottom-right corner to resize,
        //      double-click the grip bar to put the card back in its default place. Positions are kept as fractions of the price panel.
        private const float CardGripH = 13f, CardMinW = 400f, CardMinH = 110f, CardCorner = 16f, CardDesignW = 1150f;
        private readonly float[][] cardF = { new float[] { 0f, 0f, 0.5f, 0f }, new float[] { 0.5f, 0f, 0.5f, 0f } };   // x, y, w, h  (h <= 0 = not placed yet)
        private readonly float[][] cardPx = { new float[4], new float[4] };                                              // last drawn rectangle in pixels
        private float cardPL, cardPT, cardPW, cardPH;
        private int cardDrag = -1; private bool cardResize; private System.Windows.Point cardStart; private readonly float[] cardStartF = new float[4];
        private void CardDefault(int ci, float panelH)
        {
            bool both = pin != null && prof != null;
            float hf = panelH > 0f ? Math.Min(StripHeight, panelH * 0.45f) / panelH : 0.3f;
            cardF[ci][0] = both && ci == 1 ? 0.5f : 0f; cardF[ci][2] = both ? 0.5f : 1f;
            cardF[ci][3] = hf; cardF[ci][1] = 1f - hf;
        }
        private void CardGrip(int ci, float x, float y, float w, float h)
        {
            try
            {
                lock (sync)
                {
                    bool act = cardDrag == ci;
                    RFill(x, y, w, CardGripH, 0f, act ? 0x1F3347 : 0x141C2A, 1f);
                    Txt("\u2261  " + (ci == 0 ? "PIN PAYOFF" : "GEX PROFILE") + "   drag to move  \u00B7  corner to resize  \u00B7  double-click to reset", x + 6f, y + 1f, w - 12f, 8.5f, act ? CWhite : CGrey, false);
                    RFill(x + w - 11f, y + h - 11f, 9f, 9f, 0f, act && cardResize ? CWhite : CBorder, 0.85f);
                }
            }
            catch { }
        }
        // which card (if any) is under the mouse: grip bar = move, bottom-right corner = resize
        private int CardHit(double mx, double my, out bool resize)
        {
            resize = false;
            for (int ci = 0; ci < 2; ci++)
            {
                float[] r = cardPx[ci];
                if (r[2] <= 0f) continue;
                if (mx >= r[0] + r[2] - CardCorner && mx <= r[0] + r[2] && my >= r[1] + r[3] - CardCorner && my <= r[1] + r[3]) { resize = true; return ci; }
                if (mx >= r[0] && mx <= r[0] + r[2] && my >= r[1] && my <= r[1] + CardGripH) return ci;
            }
            return -1;
        }

        // ---- SETUP PERFORMANCE card: closed trades of THIS strategy grouped by setup type, read from radar_strategy_<root>.csv
        //      (the file this strategy appends to on every closed trade). Drag its top bar to move it; click the range chip to
        //      switch All / Today / 7 days; the STATS chip (top right) shows / hides it.
        private bool uiStats = true, statDrag;
        private float[] chipS = new float[4];
        private readonly float[] statF = { 0.012f, 0.34f };            // top-left corner as fractions of the price panel
        private readonly float[] statPx = new float[4], statRangeRect = new float[4];
        private System.Windows.Point statStart; private float statStartX, statStartY;
        private int statRange;                                         // 0 = all, 1 = today, 2 = last 7 days
        private DateTime statRead = DateTime.MinValue;
        private sealed class StatRow { public string Name; public int N, W, Ord; public double Net, GrossW, GrossL; }
        private int statAcct, statNoAcct;                              // 0 = all accounts, 1 = only the account this strategy runs on
        private readonly float[] statAcctRect = new float[4];
        private int statGroup;                                         // 0 = by setup, 1 = by time of day (entry time)
        private readonly float[] statGroupRect = new float[4];
        private static readonly string[] StatSess = { "Evening 18-21", "Overnight 21-03", "Europe 03-08", "Open 08-11", "Midday 11-14", "Afternoon 14-18" };
        private static int StatSessIdx(int h) { return h >= 18 && h < 21 ? 0 : (h >= 21 || h < 3 ? 1 : (h < 8 ? 2 : (h < 11 ? 3 : (h < 14 ? 4 : 5)))); }
        private List<StatRow> statRows = new List<StatRow>();
        private string statNote = "";
        private void StatsRead()
        {
            if ((DateTime.Now - statRead).TotalSeconds < 5) return;
            statRead = DateTime.Now;
            List<StatRow> rows = new List<StatRow>(); string note = "";
            try
            {
                string path = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "radar_strategy_" + MyRoot() + ".csv");
                if (!File.Exists(path)) note = "no closed trades logged yet";
                else
                {
                    string[] lines;
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader sr = new StreamReader(fs)) lines = sr.ReadToEnd().Split('\n');
                    DateTime today = DateTime.Now.Date, from = statRange == 1 ? today : (statRange == 2 ? today.AddDays(-6) : DateTime.MinValue);
                    Dictionary<string, StatRow> map = new Dictionary<string, StatRow>();
                    string myAcct = ""; try { if (Account != null) myAcct = Account.Name.Replace(",", ";"); } catch { }
                    int noAcct = 0;
                    for (int i = 1; i < lines.Length; i++)
                    {
                        string[] f = lines[i].Trim().Split(',');
                        // three row shapes exist: 12 columns (oldest, no setup type), 13 (with type), 14 (with type + account)
                        if (f.Length < 12) continue;
                        bool old12 = f.Length == 12;
                        DateTime et; double pnl;
                        if (!DateTime.TryParseExact(f[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out et)) continue;
                        if (!double.TryParse(f[old12 ? 11 : 12], NumberStyles.Float, CultureInfo.InvariantCulture, out pnl)) continue;
                        if (et < from) continue;
                        string racct = f.Length >= 14 ? f[13].Trim() : "";
                        if (racct.Length == 0) noAcct++;
                        if (statAcct == 1 && !string.Equals(racct, myAcct, StringComparison.OrdinalIgnoreCase)) continue;
                        int so = statGroup == 1 ? StatSessIdx(et.Hour) : 0;
                        string nm = statGroup == 1 ? StatSess[so] : (old12 || f[2].Length == 0 ? "(setup not logged)" : f[2]);
                        StatRow r;
                        if (!map.TryGetValue(nm, out r)) { r = new StatRow(); r.Name = nm; r.Ord = so; map[nm] = r; rows.Add(r); }
                        r.N++; r.Net += pnl;
                        if (pnl > 0) { r.W++; r.GrossW += pnl; } else if (pnl < 0) r.GrossL -= pnl;
                    }
                    if (statGroup == 1) rows.Sort((x, y) => x.Ord.CompareTo(y.Ord)); else rows.Sort((x, y) => y.Net.CompareTo(x.Net));
                    if (rows.Count == 0) note = statAcct == 1 ? "no trades logged for " + myAcct + " yet (older rows have no account)" : "no closed trades in this range";
                    statNoAcct = noAcct;
                }
            }
            catch (Exception ex) { note = "stats read error: " + ex.Message; }
            statRows = rows; statNote = note;
        }
        private static string StatMoney(double v) { return (v >= 0 ? "+$" : "-$") + Math.Abs(v).ToString("N0", CultureInfo.InvariantCulture); }
        private static string StatPf(StatRow r) { return r.GrossL > 0 ? (r.GrossW / r.GrossL).ToString("0.00", CultureInfo.InvariantCulture) : (r.GrossW > 0 ? "max" : "-"); }
        private void StatsRender(float pL, float pT, float pW, float pH)
        {
            try
            {
                StatsRead();
                List<StatRow> rows = statRows; string note = statNote;
                const float W = 392f, RowH = 15f;
                bool foot = statNoAcct > 0;
                float h = CardGripH + 22f + 16f + Math.Max(1, rows.Count) * RowH + (rows.Count > 1 ? RowH + 4f : 0f) + 8f + (foot ? 13f : 0f);
                float x = Math.Max(pL, Math.Min(pL + pW - W, pL + statF[0] * pW)), y = Math.Max(pT, Math.Min(pT + pH - h, pT + statF[1] * pH));
                statPx[0] = x; statPx[1] = y; statPx[2] = W; statPx[3] = h;
                lock (sync)
                {
                    RFill(x, y, W, h, 0f, 0x0A0F1A, 0.93f);
                    RFrame(x, y, W, h, 0f, CBorder, 0.7f, 1f);
                    RFill(x, y, W, CardGripH, 0f, statDrag ? 0x1F3347 : 0x141C2A, 1f);
                    Txt("\u2261  SETUP PERFORMANCE   drag to move", x + 6f, y + 1f, W - 12f, 8.5f, statDrag ? CWhite : CGrey, false);
                    float cy = y + CardGripH + 5f;
                    string gl = statGroup == 1 ? "BY TIME" : "BY SETUP";
                    float gw = 70f;
                    RFill(x + 8f, cy - 1f, gw, 15f, 3f, CAmber, 0.16f); RFrame(x + 8f, cy - 1f, gw, 15f, 3f, CAmber, 0.9f, 1f);
                    Txt(gl, x + 15f, cy + 1f, gw, 9f, CAmber, true);
                    statGroupRect[0] = x + 8f; statGroupRect[1] = cy - 1f; statGroupRect[2] = gw; statGroupRect[3] = 15f;
                    string an = ""; try { if (Account != null) an = Account.Name; } catch { }
                    Txt(statAcct == 1 ? an : (statGroup == 1 ? "entry hour, chart time" : "closed trades"), x + 86f, cy + 1f, 140f, 9f, statAcct == 1 ? CWhite : CGrey, false);
                    float aw = 84f, ax = x + W - 62f - 8f - aw - 6f;
                    RFill(ax, cy - 1f, aw, 15f, 3f, CGreen, 0.14f); RFrame(ax, cy - 1f, aw, 15f, 3f, CGreen, 0.9f, 1f);
                    Txt(statAcct == 1 ? "THIS ACCT" : "ALL ACCTS", ax + 8f, cy + 1f, aw, 9f, CGreen, true);
                    statAcctRect[0] = ax; statAcctRect[1] = cy - 1f; statAcctRect[2] = aw; statAcctRect[3] = 15f;
                    string rl = statRange == 1 ? "TODAY" : (statRange == 2 ? "7 DAYS" : "ALL");
                    float rw = 62f, rx = x + W - rw - 8f;
                    RFill(rx, cy - 1f, rw, 15f, 3f, CCyan, 0.16f); RFrame(rx, cy - 1f, rw, 15f, 3f, CCyan, 0.9f, 1f);
                    Txt(rl, rx + 8f, cy + 1f, rw, 9f, CCyan, true);
                    statRangeRect[0] = rx; statRangeRect[1] = cy - 1f; statRangeRect[2] = rw; statRangeRect[3] = 15f;
                    cy += 22f;
                    float c1 = x + 8f, c2 = x + 158f, c3 = x + 204f, c4 = x + 250f, c5 = x + 308f, c6 = x + 356f;
                    Txt(statGroup == 1 ? "ENTRY TIME" : "SETUP", c1, cy, 140f, 8.5f, CGrey, true); Txt("TRADES", c2, cy, 50f, 8.5f, CGrey, true); Txt("WIN %", c3, cy, 50f, 8.5f, CGrey, true);
                    Txt("NET", c4, cy, 60f, 8.5f, CGrey, true); Txt("AVG", c5, cy, 50f, 8.5f, CGrey, true); Txt("PF", c6, cy, 40f, 8.5f, CGrey, true);
                    cy += 16f;
                    if (rows.Count == 0) Txt(note, c1, cy, W - 16f, 10f, CGrey, false);
                    int tn = 0, tw = 0; double tnet = 0, tgw = 0, tgl = 0;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        StatRow r = rows[i];
                        int col = r.Net > 0 ? CGreen : (r.Net < 0 ? CRed : CWhite);
                        bool top = statGroup == 0 && i == 0 && r.Net > 0;
                        Txt(r.Name, c1, cy, 148f, 10f, top ? CYellow : CWhite, top);
                        Txt(r.N.ToString(CultureInfo.InvariantCulture), c2, cy, 44f, 10f, CWhite, false);
                        Txt((100.0 * r.W / r.N).ToString("0", CultureInfo.InvariantCulture) + "%", c3, cy, 44f, 10f, CWhite, false);
                        Txt(StatMoney(r.Net), c4, cy, 58f, 10f, col, true);
                        Txt(StatMoney(r.Net / r.N), c5, cy, 48f, 10f, col, false);
                        Txt(StatPf(r), c6, cy, 36f, 10f, CWhite, false);
                        tn += r.N; tw += r.W; tnet += r.Net; tgw += r.GrossW; tgl += r.GrossL;
                        cy += RowH;
                    }
                    if (rows.Count > 1)
                    {
                        HLine(x + 6f, x + W - 6f, cy + 1f, CGrey, 0.5f, 1f, false);
                        cy += 4f;
                        StatRow t = new StatRow(); t.N = tn; t.W = tw; t.Net = tnet; t.GrossW = tgw; t.GrossL = tgl;
                        int tc = tnet > 0 ? CGreen : (tnet < 0 ? CRed : CWhite);
                        Txt("TOTAL", c1, cy, 148f, 10f, CGrey, true);
                        Txt(tn.ToString(CultureInfo.InvariantCulture), c2, cy, 44f, 10f, CWhite, false);
                        Txt((100.0 * tw / tn).ToString("0", CultureInfo.InvariantCulture) + "%", c3, cy, 44f, 10f, CWhite, false);
                        Txt(StatMoney(tnet), c4, cy, 58f, 10f, tc, true);
                        Txt(StatMoney(tnet / tn), c5, cy, 48f, 10f, tc, false);
                        Txt(StatPf(t), c6, cy, 36f, 10f, CWhite, false);
                    }
                    if (foot) Txt(statNoAcct + " older trade(s) in this range have no account recorded" + (statAcct == 1 ? " (left out)" : " (included)"), x + 8f, y + h - 15f, W - 16f, 8.5f, CGrey, false);
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------ trade management
        private Setup activePlan;
        private bool beDone;
        private int lastEntryBar = -1000, loggedTrades = 0, tradesToday = 0;
        private DateTime tradeDay = DateTime.MinValue;

        // returns true when an unowned account position was found (and a flatten was sent)
        private DateTime noOrphanUntil = DateTime.MinValue;
        private bool rtNoted;
        private string entryCtx = ",,,,,,,,,,"; private DateTime lastExitWall = DateTime.MinValue; private double lastTradePnl = double.NaN; private bool movedByHand;
        private double keptStop, keptTarget;     // the stop / target prices NinjaTrader currently remembers for the open trade
        private bool HandleOrphan()
        {
            try
            {
                if (Account == null) return false;
                int qty = 0; MarketPosition mp = MarketPosition.Flat;
                foreach (NinjaTrader.Cbi.Position ap in Account.Positions)
                {
                    if (ap == null || ap.Instrument == null || ap.Instrument.FullName != Instrument.FullName) continue;
                    if (ap.MarketPosition == MarketPosition.Flat) continue;
                    qty = ap.Quantity; mp = ap.MarketPosition; break;
                }
                if (qty == 0) return false;
                Print(DateTime.Now.ToString("HH:mm:ss") + " SGR ORPHAN account position " + mp + " " + qty + " " + Instrument.FullName + " on " + Account.Name + " is not owned by this strategy (no stop/target) - closing it at market (strategy stays enabled) and skipping this entry");
                // Close it with a plain account market order. Account.Flatten() would also make NinjaTrader DISABLE this strategy.
                // This strategy is flat, so closing the stray position puts account and strategy back in sync and trading carries on.
                noOrphanUntil = DateTime.Now.AddSeconds(15);     // don't send a second close while this one is filling
                OrderAction oa = mp == MarketPosition.Long ? OrderAction.Sell : OrderAction.BuyToCover;
                NinjaTrader.Cbi.Order oc = Account.CreateOrder(Instrument, oa, OrderType.Market, OrderEntry.Automated, TimeInForce.Day,
                                                               qty, 0, 0, string.Empty, "SgrOrphanClose", NinjaTrader.Core.Globals.MaxDate, null);
                Account.Submit(new NinjaTrader.Cbi.Order[] { oc });
                return true;
            }
            catch (Exception ex) { Print("SGR orphan check error: " + ex.Message); return false; }
        }

        private void CloseAll(string name)
        {
            noOrphanUntil = DateTime.Now.AddSeconds(15);   // our own exit is in flight: the account is still briefly in position, that is not an orphan
            if (Position.MarketPosition == MarketPosition.Long) ExitLong(name, "RadarLong");
            else if (Position.MarketPosition == MarketPosition.Short) ExitShort(name, "RadarShort");
        }

        private void ManageBreakeven()
        {
            if (BreakevenR <= 0 || beDone || activePlan == null) return;
            double risk = Math.Abs(activePlan.Entry - activePlan.Stop);
            if (risk <= 0) return;
            double avg = Position.AveragePrice;
            // the break-even stop must sit on the correct side of the CURRENT price or NinjaTrader rejects it (and used to disable the strategy)
            if (Position.MarketPosition == MarketPosition.Long && High[0] >= avg + BreakevenR * risk)
            {
                double ns = avg + BreakevenTicks * TickSize;
                if (keptStop >= ns) beDone = true;       // the stop was already moved to break-even or better by hand: never loosen it
                else if (ns < Close[0] - TickSize) { keptStop = ns; SetStopLoss("RadarLong", CalculationMode.Price, ns, false); beDone = true; }
            }
            else if (Position.MarketPosition == MarketPosition.Short && Low[0] <= avg - BreakevenR * risk)
            {
                double ns = avg - BreakevenTicks * TickSize;
                if (keptStop > 0 && keptStop <= ns) beDone = true;
                else if (ns > Close[0] + TickSize) { keptStop = ns; SetStopLoss("RadarShort", CalculationMode.Price, ns, false); beDone = true; }
            }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            try
            {
                if (marketPosition != MarketPosition.Flat) return;
                int n = SystemPerformance.AllTrades.Count;
                if (n <= loggedTrades || n == 0) return;
                loggedTrades = n;
                Trade t = SystemPerformance.AllTrades[n - 1];
                string path = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "radar_strategy_" + MyRoot() + ".csv");
                bool exists = File.Exists(path);
                using (StreamWriter w = new StreamWriter(path, true))
                {
                    if (!exists) w.WriteLine("entry_time,dir,type,level,label,score,plan_entry,plan_stop,plan_target,fill_entry,fill_exit,exit_name,pnl_currency,account");
                    Setup a = activePlan;
                    w.WriteLine(string.Join(",", new string[] {
                        t.Entry.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        t.Entry.MarketPosition == MarketPosition.Long ? "1" : "-1",
                        a == null ? "" : KindName(a.Kind),
                        a == null ? "" : a.Level.ToString("F2", CultureInfo.InvariantCulture),
                        a == null ? "" : (a.LevelLabel ?? "").Replace(",", ";"),
                        a == null ? "" : a.Score.ToString(CultureInfo.InvariantCulture),
                        a == null ? "" : a.Entry.ToString("F2", CultureInfo.InvariantCulture),
                        a == null ? "" : a.Stop.ToString("F2", CultureInfo.InvariantCulture),
                        a == null ? "" : a.Target.ToString("F2", CultureInfo.InvariantCulture),
                        t.Entry.Price.ToString("F2", CultureInfo.InvariantCulture),
                        t.Exit.Price.ToString("F2", CultureInfo.InvariantCulture),
                        t.Exit.Name,
                        t.ProfitCurrency.ToString("F2", CultureInfo.InvariantCulture),
                        Account == null ? "" : Account.Name.Replace(",", ";") }));      // account: added as the LAST column so older rows still line up
                }
                // second, wider log (own file, own header) with the context at entry, for testing filters later
                try
                {
                    CultureInfo ci = CultureInfo.InvariantCulture;
                    string p2 = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "radar_trades_" + MyRoot() + ".csv");
                    bool ex2 = File.Exists(p2);
                    Setup b = activePlan;
                    double mae = double.NaN, mfe = double.NaN;
                    try { mae = t.MaeCurrency; mfe = t.MfeCurrency; } catch { }
                    using (StreamWriter w2 = new StreamWriter(p2, true))
                    {
                        if (!ex2) w2.WriteLine("entry_time,exit_time,account,dir,type,level,label,score,plan_entry,plan_stop,plan_target,fill_entry,fill_exit,exit_name,pnl_currency,qty,mae_currency,mfe_currency,moved_by_hand,"
                                             + "sync_pct,bias,tf_score,flow_score,rsi,adx,net_gex,gamma_regime,atr,secs_since_last_exit,prev_trade_pnl");
                        w2.WriteLine(string.Join(",", new string[] {
                            t.Entry.Time.ToString("yyyy-MM-dd HH:mm:ss", ci), t.Exit.Time.ToString("yyyy-MM-dd HH:mm:ss", ci),
                            Account == null ? "" : Account.Name.Replace(",", ";"),
                            t.Entry.MarketPosition == MarketPosition.Long ? "1" : "-1",
                            b == null ? "" : KindName(b.Kind), b == null ? "" : b.Level.ToString("F2", ci), b == null ? "" : (b.LevelLabel ?? "").Replace(",", ";"),
                            b == null ? "" : b.Score.ToString(ci), b == null ? "" : b.Entry.ToString("F2", ci), b == null ? "" : b.Stop.ToString("F2", ci), b == null ? "" : b.Target.ToString("F2", ci),
                            t.Entry.Price.ToString("F2", ci), t.Exit.Price.ToString("F2", ci), (t.Exit.Name ?? "").Replace(",", ";"), t.ProfitCurrency.ToString("F2", ci),
                            t.Quantity.ToString(ci), double.IsNaN(mae) ? "" : mae.ToString("F2", ci), double.IsNaN(mfe) ? "" : mfe.ToString("F2", ci), movedByHand ? "1" : "0",
                            entryCtx }));
                    }
                }
                catch { }
                lastExitWall = DateTime.Now; lastTradePnl = t.ProfitCurrency;
            }
            catch { }
        }

        private double TodayPnl()
        {
            double sum = 0;
            try
            {
                for (int i = 0; i < SystemPerformance.AllTrades.Count; i++)
                {
                    Trade t = SystemPerformance.AllTrades[i];
                    if (t.Exit.Time.Date == Time[0].Date) sum += t.ProfitCurrency;
                }
            }
            catch { }
            return sum;
        }


        private void UpdateZones(double atr)
        {
            double avgVol = 0; int nv = Math.Min(20, CurrentBar);
            for (int j = 0; j < nv; j++) avgVol += Volume[j];
            avgVol = nv > 0 ? avgVol / nv : 1; if (avgVol <= 0) avgVol = 1;

            // tested counter: a bar that trades into a zone after it formed counts once per visit
            for (int pass = 0; pass < 2; pass++)
            {
                List<Zone> lst = pass == 0 ? zones : fvgs;
                for (int i = 0; i < lst.Count; i++)
                {
                    Zone q = lst[i];
                    if (CurrentBar <= q.Born) continue;
                    bool ov = Low[0] <= q.Hi && High[0] >= q.Lo;
                    if (ov && !q.InZone) { q.Tested++; q.InZone = true; }
                    else if (!ov) q.InZone = false;
                }
            }

            double range0 = High[0] - Low[0], body0 = Math.Abs(Close[0] - Open[0]);
            if (range0 >= DispAtr * atr && body0 >= 0.6 * range0)
            {
                if (Close[0] > Open[0])
                {
                    for (int k = 1; k <= 3; k++)
                        if (Close[k] < Open[k]) { zones.Add(NewZone(CurrentBar - k, Low[k], High[k], true, false, Volume[k], avgVol)); break; }
                }
                else if (Close[0] < Open[0])
                {
                    for (int k = 1; k <= 3; k++)
                        if (Close[k] > Open[k]) { zones.Add(NewZone(CurrentBar - k, Low[k], High[k], false, false, Volume[k], avgVol)); break; }
                }
            }
            if (Low[0] > High[2] && (Low[0] - High[2]) >= FvgMinAtr * atr) AddFvg(NewZone(CurrentBar - 1, High[2], Low[0], true, true, Volume[1], avgVol));
            if (High[0] < Low[2] && (Low[2] - High[0]) >= FvgMinAtr * atr) AddFvg(NewZone(CurrentBar - 1, High[0], Low[2], false, true, Volume[1], avgVol));

            CullZones(zones);
            CullZones(fvgs);
            if (zones.Count > 80) zones.RemoveRange(0, zones.Count - 80);
            if (fvgs.Count > 80) fvgs.RemoveRange(0, fvgs.Count - 80);
        }
        // Strength % is a heuristic: zone-candle volume vs the 20-bar average (50% = average), reduced 8 points per retest.
        private Zone NewZone(int bar, double lo, double hi, bool bull, bool fvg, double vol, double avgVol)
        {
            Zone z = new Zone(); z.Bar = bar; z.Born = CurrentBar; z.Lo = lo; z.Hi = hi; z.Bull = bull; z.Fvg = fvg; z.Vol = vol; z.InZone = true;
            z.Pct = Math.Max(5, Math.Min(95, 50.0 * vol / avgVol));
            return z;
        }
        private List<Zone> fvgs = new List<Zone>();
        private void AddFvg(Zone z) { fvgs.Add(z); }
        private void CullZones(List<Zone> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Zone z = list[i];
                if (z.Bull && Close[0] < z.Lo) z.Dead = true;
                if (!z.Bull && Close[0] > z.Hi) z.Dead = true;
                if (CurrentBar - z.Bar > 400) list.RemoveAt(i);
            }
        }

        private bool ZoneNear(List<Zone> list, bool bull, double extreme, double level, double atr)
        {
            for (int i = 0; i < list.Count; i++)
            {
                Zone z = list[i];
                if (z.Bull != bull || z.Dead) continue;
                if (bull) { if (z.Hi >= extreme - 0.25 * atr && z.Lo <= level + 0.5 * atr) return true; }
                else { if (z.Lo <= extreme + 0.25 * atr && z.Hi >= level - 0.5 * atr) return true; }
            }
            return false;
        }

        private Setup DetectNew(double atr)
        {
            Setup best = null;
            double tick = TickSize, range = High[0] - Low[0];
            for (int i = 0; i < levels.Count; i++)
            {
                Lvl l = levels[i];
                bool longSweep = Low[0] <= l.Price - PenTicks * tick && Close[0] >= l.Price && (l.Price - Low[0]) <= MaxDepthAtr * atr;
                bool shortSweep = High[0] >= l.Price + PenTicks * tick && Close[0] <= l.Price && (High[0] - l.Price) <= MaxDepthAtr * atr;
                if (longSweep == shortSweep) continue;
                int dir = longSweep ? 1 : -1;
                if (dir > 0 && CurrentBar - lastSignalBarLong < CooldownBars) continue;
                if (dir < 0 && CurrentBar - lastSignalBarShort < CooldownBars) continue;

                double extreme = dir > 0 ? Low[0] : High[0];
                double depth = Math.Abs(l.Price - extreme);
                int score = 40;
                if (depth / atr >= 0.05 && depth / atr <= 1.0) score += 10;
                if (range > 0 && ((dir > 0 && (Close[0] - Low[0]) / range >= 0.6) || (dir < 0 && (High[0] - Close[0]) / range >= 0.6))) score += 10;
                if ((dir > 0 && Close[0] > emaTrend[0]) || (dir < 0 && Close[0] < emaTrend[0])) score += 10;
                bool ob = ZoneNear(zones, dir > 0, extreme, l.Price, atr);
                bool fvg = ZoneNear(fvgs, dir > 0, extreme, l.Price, atr);
                if (ob) score += 25;
                if (fvg) score += 15;
                if (score < MinScore) continue;

                double entry = Close[0];
                double stop = dir > 0 ? extreme - StopBufTicks * tick : extreme + StopBufTicks * tick;
                double riskTicks = Math.Abs(entry - stop) / tick;
                if (riskTicks < MinRiskTicks || riskTicks > MaxRiskTicks) continue;
                double risk = Math.Abs(entry - stop);
                double target = dir > 0 ? entry + TargetR * risk : entry - TargetR * risk;
                double nearest = double.MaxValue;
                for (int j = 0; j < levels.Count; j++)
                {
                    double d = (levels[j].Price - entry) * dir;
                    if (d >= risk && d <= risk * MaxTargetR && d < nearest) { nearest = d; target = levels[j].Price; }   // levels beyond MaxTargetR stay on the fallback (TargetR)
                }
                // GEX 1-5 strikes (largest |net GEX| near price, same set the chart draws) are valid targets too
                foreach (double gp in GexRankPrices(entry))
                {
                    double d = (gp - entry) * dir;
                    if (d >= risk && d <= risk * MaxTargetR && d < nearest) { nearest = d; target = gp; }
                }
                if (best == null || score > best.Score)
                {
                    Setup s = new Setup();
                    s.Dir = dir; s.Bar = CurrentBar; s.Time = Time[0]; s.Level = l.Price; s.LevelLabel = l.Label; s.Score = score;
                    s.Ob = ob; s.Fvg = fvg; s.Entry = entry; s.Stop = stop; s.Target = target; s.State = 0; s.Held = 0;
                    best = s;
                }
            }
            return best;
        }

        // nearest level / GEX 1-5 strike between 1R and MaxTargetR, else TargetR (same rule as the sweep setups)
        private static string KindName(int k)
        {
            switch (k) { case 1: return "FVG RETEST"; case 2: return "GAMMA BREAKOUT"; case 3: return "WALL FADE"; case 4: return "FLIP BREAKDOWN"; case 5: return "PIN-TRAP REVERSAL"; case 6: return "GEX CHOP RANGE"; default: return "STOP-HUNT"; }
        }
        private static string KindTag(int k)
        {
            switch (k) { case 1: return "F"; case 2: return "B"; case 3: return "W"; case 4: return "X"; case 5: return "P"; case 6: return "C"; default: return ""; }
        }
        private static bool IsWall(Lvl l) { return l.Type.IndexOf("Wall", StringComparison.OrdinalIgnoreCase) >= 0; }
        private static bool IsFlip(Lvl l)
        {
            string t = l.Type;
            return t.IndexOf("Flip", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("VolTrigger", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("ZeroGamma", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        private static bool IsPin(Lvl l)
        {
            return l.Type.IndexOf("PinStrike", StringComparison.OrdinalIgnoreCase) >= 0 || l.Type.IndexOf("CharmMagnet", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool GexCool(int kind, int dir)
        {
            int key = kind * 2 + (dir > 0 ? 1 : 0); int last;
            return lastGexBar.TryGetValue(key, out last) && CurrentBar - last < CooldownBars;
        }

        // builds a plan for the extra setup types; stop = bar extreme -/+ buffer (or the level, whichever is further), target given or PickTarget
        private Setup MakeGex(int kind, int dir, Lvl l, double stopRef, double target, double atr, string label)
        {
            double tick = TickSize, range = High[0] - Low[0];
            double entry = Close[0];
            double stop = dir > 0 ? stopRef - StopBufTicks * tick : stopRef + StopBufTicks * tick;
            double risk = Math.Abs(entry - stop);
            double riskTicks = risk / tick;
            if (riskTicks < MinRiskTicks || riskTicks > MaxRiskTicks) return null;
            if (target <= 0) target = PickTarget(entry, risk, dir);
            if ((target - entry) * dir < risk) return null;
            int score = 40;
            if (range > 0 && ((dir > 0 && (Close[0] - Low[0]) / range >= 0.6) || (dir < 0 && (High[0] - Close[0]) / range >= 0.6))) score += 10;
            if ((dir > 0 && Close[0] > emaTrend[0]) || (dir < 0 && Close[0] < emaTrend[0])) score += 10;
            double extreme = dir > 0 ? Low[0] : High[0];
            bool ob = ZoneNear(zones, dir > 0, extreme, l.Price, atr);
            bool fvg = ZoneNear(fvgs, dir > 0, extreme, l.Price, atr);
            if (ob) score += 25;
            if (fvg) score += 15;
            Setup s = new Setup();
            s.Dir = dir; s.Bar = CurrentBar; s.Time = Time[0]; s.Kind = kind; s.Level = l.Price; s.LevelLabel = label; s.Score = score;
            s.Ob = ob; s.Fvg = fvg; s.Entry = entry; s.Stop = stop; s.Target = target; s.State = 0; s.Held = 0;
            return s;
        }

        // extra GEX setup types (each behind its own checkbox). Returns at most one setup per bar (highest score).
        private Setup DetectGex(double atr)
        {
            if (!(EnableBreakout || EnableWallFade || EnableFlip || EnablePinTrap || EnableChop)) return null;
            if (CurrentBar < 22) return null;
            double tick = TickSize, brk = 0.2 * atr, touch = 2 * tick;
            Setup best = null; int bestKind = 0;
            for (int i = 0; i < levels.Count; i++)
            {
                Lvl l = levels[i]; double p = l.Price; Setup c = null;
                bool wall = IsWall(l), flip = IsFlip(l), pin = IsPin(l);
                if ((EnableBreakout && wall) || (EnableFlip && flip))
                {
                    int kind = wall ? 2 : 4;
                    if (Close[1] <= p && Close[0] >= p + brk && !GexCool(kind, 1)) c = MakeGex(kind, 1, l, Math.Min(Low[0], p), 0, atr, l.Label);
                    else if (Close[1] >= p && Close[0] <= p - brk && !GexCool(kind, -1)) c = MakeGex(kind, -1, l, Math.Max(High[0], p), 0, atr, l.Label);
                    if (c != null && (best == null || c.Score > best.Score)) { best = c; bestKind = c.Kind; }
                    c = null;
                }
                if (EnableWallFade && wall)
                {
                    // fade: touch from the near side, no close through, close back >= 0.2 ATR away
                    if (Low[0] <= p + touch && Low[0] >= p - touch * 2 && Close[0] >= p + brk && Close[1] > p && !GexCool(3, 1)) c = MakeGex(3, 1, l, Low[0], 0, atr, l.Label);
                    else if (High[0] >= p - touch && High[0] <= p + touch * 2 && Close[0] <= p - brk && Close[1] < p && !GexCool(3, -1)) c = MakeGex(3, -1, l, High[0], 0, atr, l.Label);
                    if (c != null && (best == null || c.Score > best.Score)) { best = c; bestKind = c.Kind; }
                    c = null;
                }
                if (EnablePinTrap && pin && Math.Abs(p - Close[0]) <= 0.03 * Close[0])   // ignore bogus/stale pins >3% from price
                {
                    if (p - Low[0] >= 2.5 * atr && Close[0] > Open[0] && Close[0] > Low[0] + 0.5 * (High[0] - Low[0]) && !GexCool(5, 1)) c = MakeGex(5, 1, l, Low[0], NextLevelTarget(Close[0], 1, p), atr, l.Label);
                    else if (High[0] - p >= 2.5 * atr && Close[0] < Open[0] && Close[0] < High[0] - 0.5 * (High[0] - Low[0]) && !GexCool(5, -1)) c = MakeGex(5, -1, l, High[0], NextLevelTarget(Close[0], -1, p), atr, l.Label);
                    if (c != null && (best == null || c.Score > best.Score)) { best = c; bestKind = c.Kind; }
                }
            }
            if (EnableChop && netGexKnown && netGex > 0)
            {
                double net = Math.Abs(Close[0] - Close[20]), path = 0;
                for (int k = 0; k < 20; k++) path += Math.Abs(Close[k] - Close[k + 1]);
                if (path > 0 && net / path < 0.3)
                {
                    double lo = double.MinValue, hi = double.MaxValue; Lvl loL = null, hiL = null;
                    for (int i = 0; i < levels.Count; i++)
                    {
                        double p = levels[i].Price;
                        if (p <= Close[0] && p > lo) { lo = p; loL = levels[i]; }
                        if (p >= Close[0] && p < hi) { hi = p; hiL = levels[i]; }
                    }
                    if (loL != null && hiL != null && hi - lo >= 1.5 * atr)
                    {
                        double mid = (lo + hi) / 2.0; Setup c = null;
                        if (Low[0] <= lo + touch && Close[0] > lo && Close[0] > Open[0] && !GexCool(6, 1)) c = MakeGex(6, 1, loL, Low[0], mid, atr, loL.Label + "~" + hiL.Label);
                        else if (High[0] >= hi - touch && Close[0] < hi && Close[0] < Open[0] && !GexCool(6, -1)) c = MakeGex(6, -1, hiL, High[0], mid, atr, loL.Label + "~" + hiL.Label);
                        if (c != null && (best == null || c.Score > best.Score)) { best = c; bestKind = c.Kind; }
                    }
                }
            }
            if (best != null) lastGexBar[bestKind * 2 + (best.Dir > 0 ? 1 : 0)] = CurrentBar;
            return best;
        }

        // first GEX level / GEX 1-5 strike between entry and the pin in the trade direction (at least 4 ticks away), else the pin itself
        private double NextLevelTarget(double entry, int dir, double pin)
        {
            double best = pin, bestD = (pin - entry) * dir;
            for (int j = 0; j < levels.Count; j++)
            {
                double d = (levels[j].Price - entry) * dir;
                if (d >= 4 * TickSize && d < bestD) { bestD = d; best = levels[j].Price; }
            }
            foreach (double gp in GexRankPrices(entry))
            {
                double d = (gp - entry) * dir;
                if (d >= 4 * TickSize && d < bestD) { bestD = d; best = gp; }
            }
            return best;
        }

        private double PickTarget(double entry, double risk, int dir)
        {
            double target = dir > 0 ? entry + TargetR * risk : entry - TargetR * risk;
            double nearest = double.MaxValue;
            for (int j = 0; j < levels.Count; j++)
            {
                double d = (levels[j].Price - entry) * dir;
                if (d >= risk && d <= risk * MaxTargetR && d < nearest) { nearest = d; target = levels[j].Price; }
            }
            foreach (double gp in GexRankPrices(entry))
            {
                double d = (gp - entry) * dir;
                if (d >= risk && d <= risk * MaxTargetR && d < nearest) { nearest = d; target = gp; }
            }
            return target;
        }

        // FVG retest: a live (unbroken, unused) FVG up to FvgMaxAge bars old is touched, and the bar closes back out of it
        // in the zone's direction on a candle of that colour (optionally with the trend EMA). Stop beyond the zone / bar extreme.
        private Setup DetectFvg(double atr)
        {
            double tick = TickSize, range = High[0] - Low[0];
            Setup best = null; Zone bestZ = null;
            for (int i = 0; i < fvgs.Count; i++)
            {
                Zone z = fvgs[i];
                if (z.Dead || z.Uses >= FvgMaxUses || CurrentBar <= z.Born) continue;
                if (CurrentBar - z.Born > FvgMaxAge) continue;
                int dir = z.Bull ? 1 : -1;
                if (dir > 0 && CurrentBar - lastFvgBarLong < FvgCooldown) continue;
                if (dir < 0 && CurrentBar - lastFvgBarShort < FvgCooldown) continue;
                bool touch = dir > 0 ? Low[0] <= z.Hi : High[0] >= z.Lo;
                bool rej = dir > 0 ? (Close[0] > z.Hi && Close[0] > Open[0]) : (Close[0] < z.Lo && Close[0] < Open[0]);
                if (!touch || !rej) continue;
                bool trendOk = (dir > 0 && Close[0] > emaTrend[0]) || (dir < 0 && Close[0] < emaTrend[0]);
                if (FvgTrend && !trendOk) continue;

                int score = 40;
                if (trendOk) score += 10;
                if (range > 0 && ((dir > 0 && (Close[0] - Low[0]) / range >= 0.6) || (dir < 0 && (High[0] - Close[0]) / range >= 0.6))) score += 10;
                bool near = false; string nearLbl = "";
                for (int j = 0; j < levels.Count; j++)
                    if (levels[j].Price >= z.Lo - 0.5 * atr && levels[j].Price <= z.Hi + 0.5 * atr) { near = true; nearLbl = levels[j].Label; break; }
                if (near) score += 15;
                bool ob = ZoneNear(zones, dir > 0, dir > 0 ? Low[0] : High[0], dir > 0 ? z.Hi : z.Lo, atr);
                if (ob) score += 15;
                if (z.Tested <= 1) score += 5;
                if (score < FvgMinScore) continue;

                double entry = Close[0];
                double stop = dir > 0 ? Math.Min(Low[0], z.Lo) - StopBufTicks * tick : Math.Max(High[0], z.Hi) + StopBufTicks * tick;
                double riskTicks = Math.Abs(entry - stop) / tick;
                if (riskTicks < MinRiskTicks || riskTicks > MaxRiskTicks) continue;
                double risk = Math.Abs(entry - stop);
                if (best == null || score > best.Score)
                {
                    Setup s = new Setup();
                    s.Dir = dir; s.Bar = CurrentBar; s.Time = Time[0]; s.Kind = 1; s.Level = (z.Lo + z.Hi) / 2.0;
                    s.LevelLabel = "FVG retest" + (near ? " + " + nearLbl : ""); s.Score = score; s.Ob = ob; s.Fvg = true;
                    s.Entry = entry; s.Stop = stop; s.Target = PickTarget(entry, risk, dir); s.State = 0; s.Held = 0;
                    best = s; bestZ = z;
                }
            }
            if (best != null)
            {
                bestZ.Uses++;
                if (best.Dir > 0) lastFvgBarLong = CurrentBar; else lastFvgBarShort = CurrentBar;
            }
            return best;
        }

        private void ResolveSetups()
        {
            for (int i = 0; i < setups.Count; i++)
            {
                Setup s = setups[i];
                if (s.State != 0 || s.Bar == CurrentBar) continue;
                s.Held++;
                double risk = Math.Abs(s.Entry - s.Stop);
                bool stopHit = s.Dir > 0 ? Low[0] <= s.Stop : High[0] >= s.Stop;
                bool tgtHit = s.Dir > 0 ? High[0] >= s.Target : Low[0] <= s.Target;
                if (stopHit) { s.State = 2; s.R = -1.0; }
                else if (tgtHit) { s.State = 1; s.R = Math.Abs(s.Target - s.Entry) / risk; }
                else if (s.Held >= MaxAgeBars) { s.State = 3; s.R = (Close[0] - s.Entry) * s.Dir / risk; }
                if (s.State != 0) LogResult(s);
            }
        }

        private void LogResult(Setup s)
        {
            if (!LogCsv || State != State.Realtime) return;
            try
            {
                if (rec == null)
                {
                    string dir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "SneakyExport");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, "radar_" + Instrument.MasterInstrument.Name + "_" + BarsPeriod.BarsPeriodType.ToString() + BarsPeriod.Value.ToString(CultureInfo.InvariantCulture) + ".csv");
                    bool exists = File.Exists(path);
                    rec = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                    if (!exists) rec.WriteLine("time,dir,level,label,score,ob,fvg,entry,stop,target,outcome,bars_held,r");
                }
                string[] outc = { "open", "target", "stop", "expired" };
                rec.WriteLine(string.Join(",", new string[] {
                    s.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), s.Dir > 0 ? "LONG" : "SHORT",
                    s.Level.ToString("F2", CultureInfo.InvariantCulture), (s.Kind >= 2 ? KindName(s.Kind) + " " : "") + s.LevelLabel.Replace(",", " "), s.Score.ToString(CultureInfo.InvariantCulture),
                    s.Ob ? "1" : "0", s.Fvg ? "1" : "0", s.Entry.ToString("F2", CultureInfo.InvariantCulture), s.Stop.ToString("F2", CultureInfo.InvariantCulture),
                    s.Target.ToString("F2", CultureInfo.InvariantCulture), outc[s.State], s.Held.ToString(CultureInfo.InvariantCulture), s.R.ToString("F2", CultureInfo.InvariantCulture) }));
                rec.Flush();
            }
            catch { }
        }

        // ------------------------------------------------------------------ rendering
        private Dictionary<long, D2D.SolidColorBrush> brushCache = new Dictionary<long, D2D.SolidColorBrush>();
        private Dictionary<int, DW.TextFormat> fmtCache = new Dictionary<int, DW.TextFormat>();
        private TextCache txc = new TextCache();
        private D2D.StrokeStyle stDash;

        private void DisposeGfx()
        {
            txc.Clear();
            foreach (D2D.SolidColorBrush b in brushCache.Values) { try { b.Dispose(); } catch { } }
            brushCache.Clear();
            foreach (DW.TextFormat f in fmtCache.Values) { try { f.Dispose(); } catch { } }
            fmtCache.Clear();
            try { if (stDash != null) stDash.Dispose(); } catch { }
            stDash = null;
        }
        

        private D2D.SolidColorBrush Br(int rgb, float a)
        {
            long key = ((long)rgb << 8) | (long)Math.Max(0, Math.Min(255, (int)(a * 255)));
            D2D.SolidColorBrush b;
            if (!brushCache.TryGetValue(key, out b))
            {
                b = new D2D.SolidColorBrush(RenderTarget, new SharpDX.Color4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a));
                brushCache[key] = b;
            }
            return b;
        }
        private DW.TextFormat Fmt(float px, bool bold)
        {
            int key = ((int)(px * 10)) * 2 + (bold ? 1 : 0);
            DW.TextFormat f;
            if (!fmtCache.TryGetValue(key, out f))
            {
                DW.Factory dw = new DW.Factory();
                f = new DW.TextFormat(dw, "Arial", bold ? DW.FontWeight.Bold : DW.FontWeight.Normal, DW.FontStyle.Normal, px);
                f.WordWrapping = DW.WordWrapping.NoWrap;
                dw.Dispose();
                fmtCache[key] = f;
            }
            return f;
        }
        private void Txt(string s, float x, float y, float w, float px, int rgb, bool bold)
        {
            DW.TextFormat tf = Fmt(px, bold); txc.Draw(RenderTarget, s, tf, x, y, w, px + 6f, Br(rgb, 1f));
        }
        private void RFill(float x, float y, float w, float h, float r, int rgb, float a)
        {
            D2D.RoundedRectangle rr = new D2D.RoundedRectangle();
            rr.Rect = new SharpDX.RectangleF(x, y, Math.Max(1f, w), Math.Max(1f, h)); rr.RadiusX = r; rr.RadiusY = r;
            RenderTarget.FillRoundedRectangle(rr, Br(rgb, a));
        }
        private void RFrame(float x, float y, float w, float h, float r, int rgb, float a, float t)
        {
            D2D.RoundedRectangle rr = new D2D.RoundedRectangle();
            rr.Rect = new SharpDX.RectangleF(x, y, Math.Max(1f, w), Math.Max(1f, h)); rr.RadiusX = r; rr.RadiusY = r;
            RenderTarget.DrawRoundedRectangle(rr, Br(rgb, a), t);
        }
        private void HLine(float x1, float x2, float y, int rgb, float a, float w, bool dashed)
        {
            if (dashed)
            {
                if (stDash == null)
                {
                    D2D.StrokeStyleProperties sp = new D2D.StrokeStyleProperties(); sp.DashStyle = D2D.DashStyle.Dash;
                    stDash = new D2D.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory, sp);
                }
                RenderTarget.DrawLine(new SharpDX.Vector2(x1, y), new SharpDX.Vector2(x2, y), Br(rgb, a), w, stDash);
            }
            else RenderTarget.DrawLine(new SharpDX.Vector2(x1, y), new SharpDX.Vector2(x2, y), Br(rgb, a), w);
        }

        private const int CBg = 0x0A0F1A, CBorder = 0x00E5D0, CGreen = 0x00E676, CRed = 0xFF3355, CAmber = 0xFFB300, CGrey = 0x8A93A6, CWhite = 0xF2F5FA, CCyan = 0x00B8FF, CYellow = 0xFFD400, CFvgUp = 0x00FFC8, CFvgDn = 0xFF4FD8;


        // ---- drag-to-move (grab the header strip) ----
        private System.Windows.FrameworkElement dragCtl;
        private bool dragging, invPending;
        private float pRx, pRy, pRw, pRh;
        private System.Windows.Point dStart; private int dOffX, dOffY;
        private double _dsv; private int _dsT; private double DevScale()
        {
            if (_dsv > 0 && Environment.TickCount - _dsT < 2000) return _dsv;
            _dsT = Environment.TickCount;
            try { var src = System.Windows.PresentationSource.FromVisual(dragCtl); if (src != null && src.CompositionTarget != null) return (_dsv = src.CompositionTarget.TransformToDevice.M11); } catch { }
            return 1.0;
        }
        private void AttachDrag()
        {
            try
            {
                if (ChartControl == null || dragCtl != null) return;
                dragCtl = ChartControl;
                dragCtl.AddHandler(System.Windows.UIElement.PreviewMouseLeftButtonDownEvent, new System.Windows.Input.MouseButtonEventHandler(DragDown), true);
                dragCtl.AddHandler(System.Windows.UIElement.PreviewMouseMoveEvent, new System.Windows.Input.MouseEventHandler(DragMove), true);
                dragCtl.AddHandler(System.Windows.UIElement.PreviewMouseLeftButtonUpEvent, new System.Windows.Input.MouseButtonEventHandler(DragUp), true);
            }
            catch { }
        }
        private void DetachDrag()
        {
            try
            {
                if (dragCtl == null) return;
                dragCtl.RemoveHandler(System.Windows.UIElement.PreviewMouseLeftButtonDownEvent, new System.Windows.Input.MouseButtonEventHandler(DragDown));
                dragCtl.RemoveHandler(System.Windows.UIElement.PreviewMouseMoveEvent, new System.Windows.Input.MouseEventHandler(DragMove));
                dragCtl.RemoveHandler(System.Windows.UIElement.PreviewMouseLeftButtonUpEvent, new System.Windows.Input.MouseButtonEventHandler(DragUp));
                dragCtl = null;
            }
            catch { }
        }
        private void DragDown(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                MarkInput();
                if (dragCtl == null) return;
                double sc = DevScale();
                System.Windows.Point p = e.GetPosition(dragCtl);
                double mx = p.X * sc, my = p.Y * sc;
                if (InRect(chipS, mx, my)) { uiStats = !uiStats; SaveUi(); e.Handled = true; try { Refresh(); } catch { } return; }
                if (uiStats && InRect(statAcctRect, mx, my)) { statAcct = 1 - statAcct; statRead = DateTime.MinValue; SaveUi(); e.Handled = true; try { Refresh(); } catch { } return; }
                if (uiStats && InRect(statGroupRect, mx, my)) { statGroup = 1 - statGroup; statRead = DateTime.MinValue; SaveUi(); e.Handled = true; try { Refresh(); } catch { } return; }
                if (uiStats && InRect(statRangeRect, mx, my)) { statRange = (statRange + 1) % 3; statRead = DateTime.MinValue; SaveUi(); e.Handled = true; try { Refresh(); } catch { } return; }
                if (uiStats && statPx[2] > 0f && mx >= statPx[0] && mx <= statPx[0] + statPx[2] && my >= statPx[1] && my <= statPx[1] + CardGripH)
                {
                    statDrag = true; statStart = new System.Windows.Point(mx, my); statStartX = statF[0]; statStartY = statF[1];
                    dragCtl.CaptureMouse(); e.Handled = true; return;
                }
                if (InRect(chipZ, mx, my) || InRect(chipH, mx, my) || InRect(chipR, mx, my) || InRect(chipK, mx, my) || InRect(chipP, mx, my) || InRect(chipG, mx, my))
                {
                    if (InRect(chipZ, mx, my)) uiZones = !uiZones; else if (InRect(chipH, mx, my)) uiHide = !uiHide; else if (InRect(chipR, mx, my)) uiRadar = !uiRadar;
                    else if (InRect(chipK, mx, my)) uiKin = !uiKin; else if (InRect(chipP, mx, my)) uiPin = !uiPin; else uiProf = !uiProf;
                    SaveUi(); e.Handled = true;
                    try { Refresh(); } catch { }
                    return;
                }
                if (modeRect[2] > 0 && mx >= modeRect[0] && mx <= modeRect[0] + modeRect[2] && my >= modeRect[1] && my <= modeRect[1] + modeRect[3])
                {
                    CycleMode(); e.Handled = true;
                    try { Refresh(); } catch { }
                    return;
                }
                bool cRes; int cHit = CardHit(mx, my, out cRes);
                if (cHit >= 0)
                {
                    if (e.ClickCount >= 2 && !cRes) { cardF[cHit][3] = 0f; SaveUi(); e.Handled = true; try { Refresh(); } catch { } return; }   // back to the default place
                    if (cardF[cHit][3] <= 0f) CardDefault(cHit, cardPH);
                    cardDrag = cHit; cardResize = cRes; cardStart = new System.Windows.Point(mx, my);
                    for (int q = 0; q < 4; q++) cardStartF[q] = cardF[cHit][q];
                    dragCtl.CaptureMouse();
                    e.Handled = true;
                    return;
                }
                if (pRw <= 0f || mx < pRx || mx > pRx + pRw || my < pRy || my > pRy + pRh) return;
                dragging = true; dStart = new System.Windows.Point(mx, my); dOffX = OffsetX; dOffY = OffsetY;
                dragCtl.CaptureMouse();
                e.Handled = true;
            }
            catch { }
        }
        private void DragMove(object s, System.Windows.Input.MouseEventArgs e)
        {
            try
            {
                MarkInput();
                if (statDrag && dragCtl != null && cardPW > 0f && cardPH > 0f)
                {
                    double ssc = DevScale();
                    System.Windows.Point sp = e.GetPosition(dragCtl);
                    statF[0] = Math.Max(0f, Math.Min(0.97f, statStartX + (float)((sp.X * ssc - statStart.X) / cardPW)));
                    statF[1] = Math.Max(0f, Math.Min(0.95f, statStartY + (float)((sp.Y * ssc - statStart.Y) / cardPH)));
                    if (!invPending)
                    {
                        invPending = true;
                        dragCtl.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
                        {
                            invPending = false;
                            try { Refresh(); } catch { }
                        }));
                    }
                    e.Handled = true;
                    return;
                }
                if (cardDrag >= 0 && dragCtl != null && cardPW > 0f && cardPH > 0f)
                {
                    double csc = DevScale();
                    System.Windows.Point cp = e.GetPosition(dragCtl);
                    float fx = (float)((cp.X * csc - cardStart.X) / cardPW), fy = (float)((cp.Y * csc - cardStart.Y) / cardPH);
                    float[] f = cardF[cardDrag];
                    if (cardResize)
                    {
                        f[2] = Math.Max(Math.Min(1f, CardMinW / cardPW), Math.Min(1f - cardStartF[0], cardStartF[2] + fx));
                        f[3] = Math.Max(Math.Min(1f, CardMinH / cardPH), Math.Min(1f - cardStartF[1], cardStartF[3] + fy));
                    }
                    else
                    {
                        f[0] = Math.Max(0f, Math.Min(1f - f[2], cardStartF[0] + fx));
                        f[1] = Math.Max(0f, Math.Min(1f - f[3], cardStartF[1] + fy));
                    }
                    if (!invPending)
                    {
                        invPending = true;
                        dragCtl.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
                        {
                            invPending = false;
                            try { Refresh(); } catch { }
                        }));
                    }
                    e.Handled = true;
                    return;
                }
                if (!dragging || dragCtl == null) return;
                double sc = DevScale();
                System.Windows.Point p = e.GetPosition(dragCtl);
                double dx = p.X * sc - dStart.X, dy = p.Y * sc - dStart.Y;
                bool left = Corner == RadarStratCorner.TopLeft || Corner == RadarStratCorner.BottomLeft, top = Corner == RadarStratCorner.TopLeft || Corner == RadarStratCorner.TopRight;
                OffsetX = (int)Math.Max(0, Math.Min(2000, dOffX + (left ? dx : -dx)));
                OffsetY = (int)Math.Max(0, Math.Min(2000, dOffY + (top ? dy : -dy)));
                if (!invPending)
                {
                    invPending = true;   // coalesce: at most one chart redraw per render frame
                    dragCtl.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
                    {
                        invPending = false;
                        try { Refresh(); } catch { }
                    }));
                }
                e.Handled = true;
            }
            catch { }
        }
        private void DragUp(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (statDrag)
                {
                    statDrag = false; SaveUi(); RefreshLater();
                    dragCtl.ReleaseMouseCapture();
                    e.Handled = true;
                    return;
                }
                if (cardDrag >= 0)
                {
                    cardDrag = -1; SaveUi(); RefreshLater();
                    dragCtl.ReleaseMouseCapture();
                    e.Handled = true;
                    return;
                }
                if (!dragging) return;
                dragging = false; RefreshLater();
                dragCtl.ReleaseMouseCapture();
                e.Handled = true;
            }
            catch { }
        }



        // ---- touch log: one CSV row each time a bar's range reaches a GEX level (realtime only, 20-bar cooldown per level) ----
        private Dictionary<long, int> touchBar = new Dictionary<long, int>();
        private StreamWriter trec;
        private void LogTouches()
        {
            try
            {
                if (State != State.Realtime || levels.Count == 0) return;
                double tol = TouchTicks * TickSize;
                for (int i = 0; i < levels.Count; i++)
                {
                    Lvl lv = levels[i];
                    if (!(Low[0] - tol <= lv.Price && High[0] + tol >= lv.Price)) continue;
                    long key = (long)Math.Round(lv.Price / TickSize);
                    int lb;
                    if (touchBar.TryGetValue(key, out lb) && CurrentBar - lb < 20) continue;
                    touchBar[key] = CurrentBar;
                    if (trec == null)
                    {
                        string tp = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "radar_touches_" + MyRoot() + ".csv");
                        bool fresh = !File.Exists(tp);
                        trec = new StreamWriter(new FileStream(tp, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
                        if (fresh) trec.WriteLine("Time,Instrument,Level,Label,Type,BarOpen,BarHigh,BarLow,BarClose,Side");
                    }
                    string side = Close[0] > lv.Price ? "closed_above" : (Close[0] < lv.Price ? "closed_below" : "closed_on");
                    trec.WriteLine(string.Join(",", new string[] {
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), Instrument.MasterInstrument.Name,
                        lv.Price.ToString("F2", CultureInfo.InvariantCulture), (lv.Label ?? "").Replace(",", " "), (lv.Type ?? "").Replace(",", " "),
                        Open[0].ToString("F2", CultureInfo.InvariantCulture), High[0].ToString("F2", CultureInfo.InvariantCulture),
                        Low[0].ToString("F2", CultureInfo.InvariantCulture), Close[0].ToString("F2", CultureInfo.InvariantCulture), side }));
                    trec.Flush();
                }
            }
            catch { }
        }

        // ---- FROZEN WALL (my rule): a call/put wall that price has tested >= FrozenTouches times this
        //      session (separate visits into +/- tolerance of the wall) and never closed through. ----
        private class WallState { public int Touches; public bool Broke, Inside; }
        private Dictionary<long, WallState> walls = new Dictionary<long, WallState>();
        private long WKey(double price) { return (long)Math.Round(price / Math.Max(TickSize, 1e-9)); }
        private void TrackWalls(double atr)
        {
            if (Bars.IsFirstBarOfSession || double.IsNaN(dayHi)) { walls.Clear(); dayHi = High[0]; dayLo = Low[0]; }
            dayHi = Math.Max(dayHi, High[0]); dayLo = Math.Min(dayLo, Low[0]);
            double tol = Math.Max(TouchTicks * TickSize, 0.1 * atr);
            for (int i = 0; i < levels.Count; i++)
            {
                Lvl l = levels[i];
                bool call = l.Type == "CallWall", put = l.Type == "PutWall";
                if (!call && !put) continue;
                long key = WKey(l.Price);
                WallState w; if (!walls.TryGetValue(key, out w)) { w = new WallState(); walls[key] = w; }
                if (call && Close[0] > l.Price + tol) w.Broke = true;
                if (put && Close[0] < l.Price - tol) w.Broke = true;
                bool inside = Low[0] <= l.Price + tol && High[0] >= l.Price - tol;
                if (inside && !w.Inside) w.Touches++;
                w.Inside = inside;
            }
        }
        private bool IsFrozen(Lvl l)
        {
            if (!ShowFrozen || (l.Type != "CallWall" && l.Type != "PutWall")) return false;
            WallState w; return walls.TryGetValue(WKey(l.Price), out w) && !w.Broke && w.Touches >= FrozenTouches;
        }
        // icon + name rule. HVL (high-volume level) = Top OI #1, the strike with the largest call+put open interest.
        private static string Decorate(Lvl l)
        {
            string lab = l.Label == null ? "" : l.Label;
            switch (l.Type)
            {
                case "CallWall": return "\u25B2 " + lab;
                case "PutWall": return "\u25BC " + lab;
                case "ZeroGamma": return "\u26A1 " + lab;
                case "TopOI": return "\u2605 " + lab + (lab.EndsWith("#1") ? " (HVL)" : "");
                case "PinStrike": return "\u25CE " + lab;
                case "MaxPain": return "\u2715 " + lab;
                case "CharmMagnet": return "\u25D0 " + lab;
                case "ExpectedMove": return "\u2194 " + lab;
                default: return lab;
            }
        }

        // ---- GEX 1-5: largest |net GEX| strikes near price from GexProfile_<root>.csv ----
        private class RankK { public double Strike, Net; }
        private List<RankK> ranks = new List<RankK>();
        private DateTime rankStamp = DateTime.MinValue, rankCheck = DateTime.MinValue;
        private int rankMode = -1; private double regimeNet = 0;
        private void ReloadRankIfNeeded()
        {
            try
            {
                if (!ShowGexRank || (DateTime.UtcNow - rankCheck).TotalSeconds < 5) return;
                rankCheck = DateTime.UtcNow;
                string f = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexProfile_" + MyRoot() + ".csv");
                if (!File.Exists(f)) return;
                DateTime st = File.GetLastWriteTimeUtc(f); if (st == rankStamp && rankMode == modeIdx) return; rankStamp = st; rankMode = modeIdx;
                string[] lines;
                using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs)) lines = sr.ReadToEnd().Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                List<RankK> near = new List<RankK>(), all = new List<RankK>();
                foreach (string raw in lines)
                {
                    if (raw.StartsWith("#") || raw.StartsWith("Group", StringComparison.OrdinalIgnoreCase)) continue;
                    string[] a = raw.Split(',');
                    double k, n;
                    if (a.Length < 5 || !double.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out k) || !double.TryParse(a[4], NumberStyles.Float, CultureInfo.InvariantCulture, out n)) continue;
                    RankK r = new RankK(); r.Strike = k; r.Net = n;
                    if (a[0].Trim() == "Near") near.Add(r); else all.Add(r);
                }
                List<RankK> pick = (modeIdx == 2 || near.Count == 0) ? all : near;
                double net = 0; foreach (RankK r in pick) net += r.Net;
                lock (sync) { ranks = pick; regimeNet = net; }
            }
            catch { }
        }
        // prices of the GEX 1-5 strikes (same window/ranking the chart uses), for target selection
        private List<double> GexRankPrices(double px)
        {
            List<double> o = new List<double>();
            if (ranks.Count == 0 || px <= 0) return o;
            List<RankK> cand = new List<RankK>();
            foreach (RankK r in ranks) if (Math.Abs(r.Strike - px) <= px * (modeIdx == 0 ? 0.02 : (modeIdx == 1 ? 0.05 : 0.10))) cand.Add(r);
            cand.Sort((a, b) => Math.Abs(b.Net).CompareTo(Math.Abs(a.Net)));
            for (int i = 0; i < cand.Count && i < 5; i++) o.Add(cand[i].Strike);
            return o;
        }
        private string ValTxt(double price)
        {
            RankK best = null; double bd = double.MaxValue;
            foreach (RankK r in ranks) { double d = Math.Abs(r.Strike - price); if (d < bd) { bd = d; best = r; } }
            if (best == null || bd > Math.Max(2 * TickSize, 1.0)) return "";
            double a = Math.Abs(best.Net); string sg = best.Net < 0 ? "-" : "+";
            string n = a >= 1e9 ? (a / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + "B" : (a >= 1e6 ? (a / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "M" : (a / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + "K");
            return " (" + sg + "$" + n + ")";
        }

        // ---- shared mode (SCALPER / INTRADAY / SWING), one tiny file read by every Sneaky GEX panel ----
        private static readonly string[] ModeNames = { "SCALPER", "INTRADAY", "SWING" };
        private int modeIdx = 0; private DateTime modeRead = DateTime.MinValue;
        private float[] modeRect = new float[4];
        // ---- on-chart toggle chips: ACTIVE-ZONES / HIDE-BROKEN / RADAR (saved in GexUi.txt, one line "z|h|r") ----
        private bool uiZones = true, uiHide = true, uiRadar = true; private bool uiLoaded = false;
        private bool uiKin = true, uiPin = true, uiProf = true;      // show / hide chips for the Kinetic Cockpit, Pin Payoff and GEX Profile cards
        private float[] chipZ = new float[4], chipH = new float[4], chipR = new float[4];
        private float[] chipK = new float[4], chipP = new float[4], chipG = new float[4];
        private string UiPath() { return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexUi_" + MyRoot() + ".txt"); }
        private void LoadUi()
        {
            uiLoaded = true; uiZones = ShowZones;
            try
            {
                string f = UiPath(); if (!File.Exists(f)) return;
                string[] a = File.ReadAllText(f).Trim().Split('|');
                if (a.Length >= 3) { uiZones = a[0] == "1"; uiHide = a[1] == "1"; uiRadar = a[2] == "1"; }
                if (a.Length >= 14) { uiKin = a[11] == "1"; uiPin = a[12] == "1"; uiProf = a[13] == "1"; }
                if (a.Length >= 18)
                {
                    uiStats = a[14] == "1";
                    float sx, sy; int sr;
                    if (float.TryParse(a[15], System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out sx) && sx >= 0f && sx <= 1f) statF[0] = sx;
                    if (float.TryParse(a[16], System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out sy) && sy >= 0f && sy <= 1f) statF[1] = sy;
                    if (int.TryParse(a[17], out sr) && sr >= 0 && sr <= 2) statRange = sr;
                    if (a.Length >= 19) statGroup = a[18] == "1" ? 1 : 0;
                    if (a.Length >= 20) statAcct = a[19] == "1" ? 1 : 0;
                }
                if (a.Length >= 11)
                    for (int ci = 0; ci < 2; ci++)
                    {
                        float[] v = new float[4]; bool ok = true;
                        for (int q = 0; q < 4; q++) ok &= float.TryParse(a[3 + ci * 4 + q], System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out v[q]) && v[q] >= 0f && v[q] <= 1f;
                        if (ok) for (int q = 0; q < 4; q++) cardF[ci][q] = v[q];
                    }
            }
            catch { }
        }
        private void SaveUi()
        {
            try
            {
                StringBuilder sb = new StringBuilder((uiZones ? "1" : "0") + "|" + (uiHide ? "1" : "0") + "|" + (uiRadar ? "1" : "0"));
                for (int ci = 0; ci < 2; ci++) for (int q = 0; q < 4; q++) sb.Append('|').Append(cardF[ci][q].ToString("0.####", CultureInfo.InvariantCulture));
                sb.Append('|').Append(uiKin ? "1" : "0").Append('|').Append(uiPin ? "1" : "0").Append('|').Append(uiProf ? "1" : "0");
                sb.Append('|').Append(uiStats ? "1" : "0").Append('|').Append(statF[0].ToString("0.####", CultureInfo.InvariantCulture)).Append('|').Append(statF[1].ToString("0.####", CultureInfo.InvariantCulture)).Append('|').Append(statRange).Append('|').Append(statGroup).Append('|').Append(statAcct);
                File.WriteAllText(UiPath(), sb.ToString());
            }
            catch { }
        }
        private static bool InRect(float[] r, double x, double y) { return r[2] > 0 && x >= r[0] && x <= r[0] + r[2] && y >= r[1] && y <= r[1] + r[3]; }
        private DateTime modeStamp = DateTime.MinValue;
        private DateTime modeChk = DateTime.MinValue;
        private void ReadMode()
        {
            try
            {
                if ((DateTime.Now - modeChk).TotalMilliseconds < 1000) return; modeChk = DateTime.Now;
                
                string f = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexMode.txt");
                if (!File.Exists(f)) return;
                DateTime st = File.GetLastWriteTimeUtc(f); if (st == modeStamp) return; modeStamp = st;
                int i = Array.IndexOf(ModeNames, File.ReadAllText(f).Trim().ToUpperInvariant()); if (i >= 0) modeIdx = i;
            }
            catch { }
        }
        private void CycleMode()
        {
            try { modeIdx = (modeIdx + 1) % 3; File.WriteAllText(Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexMode.txt"), ModeNames[modeIdx]); modeRead = DateTime.Now; rankCheck = DateTime.MinValue; }
            catch { }
        }
        // ---- sync % written by the Kinetic Cockpit ----
        private int syncPct = -1; private DateTime syncRead = DateTime.MinValue;
        private void ReadSync()
        {
            try
            {
                if ((DateTime.Now - syncRead).TotalSeconds < 2) return; syncRead = DateTime.Now;
                string f = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexSync_" + MyRoot() + ".txt");
                if (!File.Exists(f)) { syncPct = -1; return; }
                string[] a = File.ReadAllText(f).Split('|');
                syncPct = (DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalMinutes < 5 ? int.Parse(a[0]) : -1;
            }
            catch { syncPct = -1; }
        }
        private double dayHi = double.NaN, dayLo = double.NaN;

        private static string BigTxt(double v)
        {
            double a = Math.Abs(v); string sg = v < 0 ? "-" : "+";
            if (a >= 1e9) return sg + (a / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + "B";
            if (a >= 1e6) return sg + (a / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            if (a >= 1e3) return sg + (a / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + "K";
            return sg + a.ToString("0", CultureInfo.InvariantCulture);
        }

        private static double Ready(TkStat t)
        {
            double d = Math.Abs(t.NearPrice - t.Last) / Math.Max(1.0, Math.Abs(t.Last));
            return Math.Max(0.0, Math.Min(100.0, 100.0 * (1.0 - d / 0.004)));   // readiness = closeness to the nearest level (0.4% of price = 0)
        }
        private static double TickOf(string root) { return root == "YM" ? 1.0 : 0.25; }

        private static string VolTxt(double v)
        {
            if (v >= 1e6) return (v / 1e6).ToString("F1", CultureInfo.InvariantCulture) + "M";
            if (v >= 1e3) return (v / 1e3).ToString("F1", CultureInfo.InvariantCulture) + "k";
            return v.ToString("F0", CultureInfo.InvariantCulture);
        }

        // ---- cross-ticker status (each Radar instance writes one file; every card reads all three) ----
        private DateTime lastStatusWrite = DateTime.MinValue, lastStatusRead = DateTime.MinValue;
        private class TkStat { public string Root; public DateTime Ts; public double Last; public int Open, Dir, Score; public double Entry, Stop, Target, NearPrice; public string Near = "", Why = ""; public int Share; public bool HasReg; }
        private Dictionary<string, TkStat> tkStats = new Dictionary<string, TkStat>();
        private static readonly string[] TkRoots = new string[] { "NQ", "ES", "YM" };
        private string MyRoot()
        {
            string r = Instrument.MasterInstrument.Name.ToUpperInvariant();
            if (r == "MNQ") return "NQ"; if (r == "MES") return "ES"; if (r == "MYM") return "YM";
            return r;
        }
        private string StatusFile(string root) { return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "RadarStatus_" + root + ".txt"); }
        private void WriteStatus()
        {
            try
            {
                if (DateTime.Now - lastStatusWrite < TimeSpan.FromSeconds(2) || Bars == null || Bars.Count < 2) return;
                if (State != State.Realtime && State != State.Historical) return;
                lastStatusWrite = DateTime.Now;
                double last = Bars.GetClose(Bars.Count - 1);
                int open = 0; Setup top = null;
                lock (sync)
                {
                    for (int i = 0; i < setups.Count; i++) if (setups[i].State == 0) { open++; if (top == null || setups[i].Score >= top.Score) top = setups[i]; }
                    string near = ""; double nearP = 0, best = double.MaxValue;
                    for (int i = 0; i < levels.Count; i++)
                    {
                        double d = Math.Abs(levels[i].Price - last);
                        if (d < best) { best = d; nearP = levels[i].Price; near = levels[i].Label == null ? "" : levels[i].Label.Replace("|", "/"); }
                    }
                    double posN = 0, negN = 0; foreach (RankK rk0 in ranks) { if (rk0.Net >= 0) posN += rk0.Net; else negN -= rk0.Net; }
                    string shareTxt = ranks.Count > 0 && (posN + negN) > 0 ? ((int)Math.Round(100.0 * (posN - negN) / (posN + negN))).ToString() : "NA";
                    string whyTxt = top == null ? "" : (top.Kind != 0 ? (KindName(top.Kind) + " [" + top.LevelLabel + "] (" + top.Level.ToString("0.##", CultureInfo.InvariantCulture) + ") \u2192 " + (top.Dir > 0 ? "LONG!" : "SHORT!")) : ("SWEEP [" + top.LevelLabel + "] (" + top.Level.ToString("0.##", CultureInfo.InvariantCulture) + ")" + (top.Ob ? " + OB-" + (top.Dir > 0 ? "SUPPORT" : "RESISTANCE") : "") + (top.Fvg ? " + FVG" : "") + " \u2192 " + (top.Dir > 0 ? "LONG!" : "SHORT!"))).Replace("|", "/");
                    string line = string.Join("|", new string[] {
                        MyRoot(), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), last.ToString("R", CultureInfo.InvariantCulture), open.ToString(),
                        top == null ? "0" : top.Dir.ToString(), top == null ? "0" : top.Score.ToString(),
                        (top == null ? 0 : top.Entry).ToString("R", CultureInfo.InvariantCulture), (top == null ? 0 : top.Stop).ToString("R", CultureInfo.InvariantCulture), (top == null ? 0 : top.Target).ToString("R", CultureInfo.InvariantCulture),
                        nearP.ToString("R", CultureInfo.InvariantCulture), near, shareTxt, whyTxt });
                    File.WriteAllText(StatusFile(MyRoot()), line);
                }
            }
            catch { }
        }
        private void ReadStatuses()
        {
            try
            {
                if (DateTime.Now - lastStatusRead < TimeSpan.FromSeconds(2)) return;
                lastStatusRead = DateTime.Now;
                Dictionary<string, TkStat> d = new Dictionary<string, TkStat>();
                foreach (string r in TkRoots)
                {
                    try
                    {
                        string f = StatusFile(r);
                        if (!File.Exists(f)) continue;
                        string[] a = File.ReadAllText(f).Split('|');
                        if (a.Length < 11) continue;
                        TkStat t = new TkStat(); t.Root = a[0];
                        t.Ts = DateTime.Parse(a[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
                        t.Last = double.Parse(a[2], CultureInfo.InvariantCulture); t.Open = int.Parse(a[3]); t.Dir = int.Parse(a[4]); t.Score = int.Parse(a[5]);
                        t.Entry = double.Parse(a[6], CultureInfo.InvariantCulture); t.Stop = double.Parse(a[7], CultureInfo.InvariantCulture); t.Target = double.Parse(a[8], CultureInfo.InvariantCulture);
                        t.NearPrice = double.Parse(a[9], CultureInfo.InvariantCulture); t.Near = a[10];
                        if (a.Length > 12) { t.HasReg = a[11] != "NA"; if (t.HasReg) t.Share = int.Parse(a[11]); t.Why = a[12]; }
                        d[r] = t;
                    }
                    catch { }
                }
                tkStats = d;
            }
            catch { }
        }

        private void RadarRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (RenderTarget == null || ChartPanel == null || chartControl == null || chartScale == null || Bars == null || ChartBars == null) return;
            base.OnRender(chartControl, chartScale);
            WriteStatus(); ReadStatuses(); ReadMode(); ReadSync();
            bool lite = HideStripsOnDrag && (dragging || cardDrag >= 0 || (kin != null && kin.IsDragging));
            float k = (float)PanelScale;
            float L = ChartPanel.X, T = ChartPanel.Y, R = ChartPanel.X + ChartPanel.W, B = ChartPanel.Y + ChartPanel.H;
            double lastPx = Bars.Count > 0 ? Bars.GetClose(Bars.Count - 1) : 0;

            lock (sync)
            {
                // label de-collision: bottom-to-top, labels closer than ~12px stack upward
                int[] stk = new int[levels.Count];
                {
                    List<int> ord = new List<int>();
                    for (int i = 0; i < levels.Count; i++) ord.Add(i);
                    ord.Sort((a, b) => levels[a].Price.CompareTo(levels[b].Price));   // low price (bottom) first
                    int run = 0; float prevY = float.MaxValue;
                    foreach (int idx in ord)
                    {
                        float yy = chartScale.GetYByValue(levels[idx].Price);
                        run = (prevY - yy < 12f * k) ? run + 1 : 0;
                        stk[idx] = run; prevY = yy;
                    }
                }
                // order-block / FVG zones
                if (uiZones && !lite)
                {
                    List<Zone> vis = new List<Zone>();
                    for (int pass = 0; pass < 2; pass++)
                    {
                        List<Zone> lst = pass == 0 ? zones : fvgs;
                        for (int i = 0; i < lst.Count; i++)
                        {
                            Zone z = lst[i];
                            if ((z.Dead && uiHide) || z.Bar > ChartBars.ToIndex) continue;
                            if (z.Hi < chartScale.MinValue || z.Lo > chartScale.MaxValue) continue;
                            vis.Add(z);
                        }
                    }
                    double refPx = lastPx;
                    vis.Sort((a, b) => Math.Abs((a.Lo + a.Hi) * 0.5 - refPx).CompareTo(Math.Abs((b.Lo + b.Hi) * 0.5 - refPx)));
                    List<float[]> zlab = new List<float[]>(); bool tagDone = false;
                    for (int i = 0; i < vis.Count && i < MaxZones; i++)
                    {
                        Zone z = vis[i];
                        float yT = chartScale.GetYByValue(z.Hi), yB = chartScale.GetYByValue(z.Lo);
                        float zx = z.Bar < ChartBars.FromIndex ? L : chartControl.GetXByBarIndex(ChartBars, z.Bar);
                        float zr = Math.Min(R - 70f * k, chartControl.GetXByBarIndex(ChartBars, ChartBars.ToIndex) + 36f * k);
                        float zw = zr - zx; if (zw < 4f) continue;
                        float zh = Math.Max(2f, yB - yT);
                        int zc = z.Dead ? CGrey : (z.Bull ? CGreen : CRed);
                        RFill(zx, yT, zw, zh, 0f, zc, z.Dead ? 0.05f : (z.Fvg ? 0.07f : 0.13f));
                        HLine(zx, zr, yT, zc, z.Dead ? 0.35f : 0.55f, 1f, z.Fvg || z.Dead);
                        HLine(zx, zr, yB, zc, z.Dead ? 0.35f : 0.55f, 1f, z.Fvg || z.Dead);
                        double pct = Math.Max(1, z.Pct - 8.0 * z.Tested);
                        string lab;
                        if (z.Fvg)
                        {
                            string gr = pct >= 80 ? "A+" : (pct >= 65 ? "A" : (pct >= 50 ? "B" : (pct >= 35 ? "C" : "D")));
                            string stars = pct >= 65 ? "\u2605\u2605\u2605" : (pct >= 45 ? "\u2605\u2605" : "\u2605");
                            lab = gr + " FVG (Score: " + pct.ToString("F0", CultureInfo.InvariantCulture) + " | " + ((z.Hi - z.Lo) / TickSize).ToString("F0", CultureInfo.InvariantCulture) + " Ticks) " + stars;
                        }
                        else lab = (z.Bull ? "Bull OB " : "Bear OB ") + pct.ToString("F0", CultureInfo.InvariantCulture) + "% [Tested " + z.Tested + "x] Vol " + VolTxt(z.Vol);
                        if (z.Dead) lab = "BROKEN " + lab;
                        if (!tagDone && !z.Dead)
                        {
                            tagDone = true;
                            // probability tag on the nearest zone: strength % of holding (heuristic, see README)
                            string tg = (z.Bull ? "\u25B2 " : "\u25BC ") + pct.ToString("F0", CultureInfo.InvariantCulture) + "%";
                            float tx = Math.Max(L + 4f * k, Math.Min(zx + 4f * k, R - 60f * k)), ty = yT - 16f * k, tw = 50f * k;
                            RFill(tx, ty, tw, 14f * k, 3f * k, CBg, 0.85f); RFrame(tx, ty, tw, 14f * k, 3f * k, zc, 0.9f, 1f);
                            Txt(tg, tx + 4f * k, ty + 2f * k, tw, 9f * k, zc, true);
                            zlab.Add(new float[] { tx, ty, tw, 14f * k });
                        }
                        float lw = lab.Length * 5.4f * k + 8f * k, lh = 13f * k;
                        float lx = Math.Max(L + 4f * k, Math.Min(zx + 4f * k, R - lw - 8f * k));   // sits on the zone's own start bar; only pulled left if it would run off the panel
                        float ly = zh >= 15f * k ? yT + 2f * k : yT - 14f * k;
                        for (int tries = 0; tries < 8; tries++)
                        {
                            bool hit = false;
                            foreach (float[] q in zlab)
                                if (lx < q[0] + q[2] && lx + lw > q[0] && ly < q[1] + q[3] && ly + lh > q[1]) { hit = true; ly = q[1] + q[3] + 1f * k; break; }
                            if (!hit) break;
                        }
                        zlab.Add(new float[] { lx, ly, lw, lh });
                        RFill(lx - 2f * k, ly - 1f * k, lw, lh, 3f * k, CBg, 0.82f);
                        Txt(lab, lx + 2f * k, ly, 300f * k, 9f * k, zc, false);
                    }
                }

                // GEX 1-5 (largest |net GEX| strikes within 5% of price)
                if (!lite && ShowGexRank && ranks.Count > 0 && lastPx > 0)
                {
                    List<RankK> cand = new List<RankK>();
                    foreach (RankK r in ranks) if (Math.Abs(r.Strike - lastPx) <= lastPx * (modeIdx == 0 ? 0.02 : (modeIdx == 1 ? 0.05 : 0.10))) cand.Add(r);
                    cand.Sort((a, b) => Math.Abs(b.Net).CompareTo(Math.Abs(a.Net)));
                    for (int i = 0; i < cand.Count && i < 5; i++)
                    {
                        float gy = chartScale.GetYByValue(cand[i].Strike);
                        if (gy < T || gy > B) continue;
                        int gc = cand[i].Net >= 0 ? CCyan : CRed;
                        HLine(L, L + 240f * k, gy, gc, 0.45f, 1f, true);
                        Txt("GEX " + (i + 1) + "  " + cand[i].Strike.ToString("0.00", CultureInfo.InvariantCulture) + "  (" + BigTxt(cand[i].Net) + ")", L + 6f * k, gy - 13f * k, 260f * k, 10.5f * k, gc, true);
                    }
                }

                // level lines + touch tags
                for (int i = 0; !lite && i < levels.Count; i++)
                {
                    Lvl lv = levels[i];
                    float y = chartScale.GetYByValue(lv.Price);
                    if (y < T || y > B) continue;
                    bool touch = lastPx > 0 && Math.Abs(lastPx - lv.Price) <= TouchTicks * TickSize;
                    string tl = lv.Label == null ? "" : lv.Label.ToLowerInvariant();
                    int c = (tl.Contains("call") || tl.Contains("resist") || (tl.Contains("wall") && !tl.Contains("put"))) ? CRed : ((tl.Contains("put") || tl.Contains("support")) ? CGreen : CAmber);
                    bool wallT = lv.Type == "CallWall" || lv.Type == "PutWall";
                    if (ShowLevels && wallT) RFill(L, y - 2.5f * k, (R - 70f * k) - L, 5f * k, 0f, c, touch ? 0.35f : 0.20f);
                    if (ShowLevels) HLine(L, R - 70f * k, y, c, touch ? 0.95f : 0.55f, touch ? 1.6f : 1f, true);
                    if (lv.Type == "TopOI" && lv.Label != null && lv.Label.EndsWith("#1"))
                    {
                        bool dte = lv.Label.StartsWith("Intraday", StringComparison.OrdinalIgnoreCase) || lv.Label.StartsWith("0DTE", StringComparison.OrdinalIgnoreCase);
                        HLine(L, R - 70f * k, y, CWhite, 0.7f, 1.2f, false);
                        Txt("HVL " + (dte ? "0DTE " : "") + lv.Price.ToString("0.00", CultureInfo.InvariantCulture), R - 150f * k, y + 2f * k, 90f * k, 10.5f * k, CWhite, true);
                    }
                    bool frozen = IsFrozen(lv);
                    string baseLab = Decorate(lv);
                    if (baseLab.IndexOf(((long)Math.Round(lv.Price)).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) < 0) baseLab += " (" + lv.Price.ToString("0.##", CultureInfo.InvariantCulture) + ")";
                    baseLab += ValTxt(lv.Price);
                    float ly0 = y - 14f * k - stk[i] * 12f * k;
                    if (ShowLevels || touch)
                    {
                        RenderTarget.FillEllipse(new D2D.Ellipse(new SharpDX.Vector2(R - 394f * k, ly0 + 7f * k), 4.5f * k, 4.5f * k), Br(c, touch ? 1f : 0.85f));
                        Txt((touch ? "[TOUCH] " : "") + baseLab, R - 384f * k, ly0, 320f * k, 11.5f * k, touch ? CYellow : c, touch);
                        if (frozen) Txt("\u2744 FROZEN WALL", R - 384f * k, ly0 + 12f * k, 160f * k, 9f * k, CCyan, true);
                    }
                }

                // day high / low
                if (!lite && !double.IsNaN(dayHi))
                {
                    float yh = chartScale.GetYByValue(dayHi), yl = chartScale.GetYByValue(dayLo);
                    if (yh >= T && yh <= B) { HLine(L, R - 70f * k, yh, CAmber, 0.5f, 1f, true); Txt("1D Max " + dayHi.ToString("0.00", CultureInfo.InvariantCulture), L + 250f * k, yh - 12f * k, 160f * k, 9.5f * k, CAmber, true); }
                    if (yl >= T && yl <= B) { HLine(L, R - 70f * k, yl, CAmber, 0.5f, 1f, true); Txt("1D Min " + dayLo.ToString("0.00", CultureInfo.InvariantCulture), L + 250f * k, yl + 2f * k, 160f * k, 9.5f * k, CAmber, true); }
                }

                // top status line
                if (!lite)
                {
                    string reg = regimeNet >= 0 ? "+GAMMA (Stable / Mean-Reversion)" : "-GAMMA (Volatile / Forced-Hedging)";
                    string st = "GEX LEVELS " + levels.Count + "  |  " + reg + "  |  SYNC " + (syncPct >= 0 ? syncPct + "%" : "--") + "  [MODE: " + ModeNames[modeIdx] + "]";
                    float sw = st.Length * 6.0f * k;
                    Txt(st, L + (R - L) * 0.5f - sw / 2f, T + 6f * k, sw + 20f * k, 10.5f * k, regimeNet >= 0 ? CGreen : CRed, true);
                }

                // plan lines for open setups + markers
                int shown = 0;
                for (int i = setups.Count - 1; i >= 0 && ShowPlan && !lite; i--)
                {
                    Setup s = setups[i];
                    if (s.State != 0) continue;
                    if (++shown > 3) break;
                    float x0 = chartControl.GetXByBarIndex(ChartBars, Math.Max(ChartBars.FromIndex, s.Bar));
                    HLine(x0, R - 70f * k, chartScale.GetYByValue(s.Entry), CWhite, 0.8f, 1f, true);
                    HLine(x0, R - 70f * k, chartScale.GetYByValue(s.Stop), CRed, 0.9f, 1.2f, true);
                    HLine(x0, R - 70f * k, chartScale.GetYByValue(s.Target), CGreen, 0.9f, 1.2f, true);
                    Txt("E", R - 66f * k, chartScale.GetYByValue(s.Entry) - 7f * k, 60f * k, 9f * k, CWhite, true);
                    Txt("SL " + s.Stop.ToString("F2", CultureInfo.InvariantCulture), R - 66f * k, chartScale.GetYByValue(s.Stop) - 7f * k, 90f * k, 9f * k, CRed, true);
                    Txt("TP " + s.Target.ToString("F2", CultureInfo.InvariantCulture), R - 66f * k, chartScale.GetYByValue(s.Target) - 7f * k, 90f * k, 9f * k, CGreen, true);
                }
                for (int i = 0; !lite && i < marks.Count; i++)
                {
                    Mark m = marks[i];
                    if (m.Bar < ChartBars.FromIndex || m.Bar > ChartBars.ToIndex) continue;
                    float x = chartControl.GetXByBarIndex(ChartBars, m.Bar);
                    float y = chartScale.GetYByValue(m.Price);
                    if (m.Dir > 0) Txt("▲" + KindTag(m.Kind), x - 6f * k, y + 3f * k, 24f * k, 13f * k, m.Kind != 0 ? CFvgUp : CGreen, true);
                    else Txt("▼" + KindTag(m.Kind), x - 6f * k, y - 18f * k, 24f * k, 13f * k, m.Kind != 0 ? CFvgDn : CRed, true);
                }

                // on-chart toggle chips (top right): ACTIVE-ZONES / HIDE-BROKEN / RADAR / KINETIC / PIN / PROFILE (click = show / hide)
                {
                    float ch = 17f * k, cy = T + 4f * k, gap = 5f * k;
                    string[] nm = { "Active-Zones", "Hide-Broken", "RADAR", "KINETIC", "PIN", "PROFILE", "STATS" };
                    bool[] on = { uiZones, uiHide, uiRadar, uiKin, uiPin, uiProf, uiStats };
                    bool[] have = { true, true, true, kin != null, pin != null, prof != null, true };     // a chip only for panels that are switched on in the settings
                    float[][] rc2 = { chipZ, chipH, chipR, chipK, chipP, chipG, chipS };
                    float cw0 = 0f; for (int q = 0; q < 7; q++) if (have[q]) cw0 += nm[q].Length * 6.4f * k + 18f * k + gap;
                    float cx = R - 76f * k - cw0;
                    for (int q = 0; q < 7; q++)
                    {
                        if (!have[q]) { rc2[q][2] = 0f; continue; }
                        float cw = nm[q].Length * 6.4f * k + 18f * k;
                        int cc = on[q] ? CGreen : CRed;
                        RFill(cx, cy, cw, ch, 4f * k, cc, on[q] ? 0.18f : 0.10f); RFrame(cx, cy, cw, ch, 4f * k, cc, 0.9f, 1.1f);
                        Txt(nm[q], cx + 9f * k, cy + 3f * k, cw, 10f * k, cc, true);
                        rc2[q][0] = cx; rc2[q][1] = cy; rc2[q][2] = cw; rc2[q][3] = ch;
                        cx += cw + gap;
                    }
                }
                if (!uiRadar) { pRw = 0f; modeRect[2] = 0f; }
                else
                {
                // radar panel
                List<Setup> act = new List<Setup>();
                for (int i = setups.Count - 1; i >= 0 && act.Count < MaxRows; i--) if (setups[i].State == 0) act.Add(setups[i]);
                float tkH = 0f;
                foreach (string tr0 in TkRoots) { TkStat tt0; bool ok0 = tkStats.TryGetValue(tr0, out tt0) && (DateTime.UtcNow - tt0.Ts) < TimeSpan.FromMinutes(15); tkH += !ok0 ? 18f : (tt0.Open > 0 ? 46f : 34f); }
                bool compact = CompactCard;
                float W = (compact ? 470f : 560f) * k, pad = 12f * k, rowH = 54f * k;
                float H = compact ? 34f * k : ((act.Count == 0 ? 62f : 34f + act.Count * rowH + 10f) + 34f + tkH) * k;
                float px, py;
                px = (Corner == RadarStratCorner.TopLeft || Corner == RadarStratCorner.BottomLeft) ? L + OffsetX : R - W - OffsetX;
                py = (Corner == RadarStratCorner.TopLeft || Corner == RadarStratCorner.TopRight) ? T + OffsetY : B - H - OffsetY;
                pRx = px; pRy = py; pRw = W; pRh = 30f * k;
                RFill(px, py, W, H, 9f * k, CBg, 0.93f);
                RFrame(px, py, W, H, 9f * k, CCyan, 1f, 1.6f);
                float x2 = px + pad, y2 = py + 8f * k;
                Txt("\u26A1 GEX RADAR", x2, y2, 110f * k, 12f * k, CCyan, true);
                {
                    float mx = x2 + 96f * k, mw = 104f * k, mh = 18f * k;
                    RFill(mx, y2 - 2f * k, mw, mh, 4f * k, CCyan, 0.14f); RFrame(mx, y2 - 2f * k, mw, mh, 4f * k, CCyan, 0.9f, 1.1f);
                    Txt("[" + ModeNames[modeIdx] + "] \u25BE", mx + 8f * k, y2 + 1f * k, mw, 10.5f * k, CCyan, true);
                    modeRect[0] = mx; modeRect[1] = y2 - 2f * k; modeRect[2] = mw; modeRect[3] = mh;
                    string lead = ""; { int bs = -1; foreach (KeyValuePair<string, TkStat> kv in tkStats) if (kv.Value.Open > 0 && kv.Value.Score > bs && (DateTime.UtcNow - kv.Value.Ts) < TimeSpan.FromMinutes(15)) { bs = kv.Value.Score; lead = " (" + kv.Key + " " + bs + "%)"; } }
                    Txt(compact ? (act.Count + " CHANCES" + lead) : (act.Count + " OPEN SETUP" + (act.Count == 1 ? "" : "S") + "  |  " + Instrument.MasterInstrument.Name), mx + mw + 10f * k, y2, 260f * k, 12f * k, CCyan, true);
                    bool live = State == State.Realtime;
                    Txt("\u25CF " + (live ? "LIVE" : "HIST"), px + W - pad - 56f * k, y2, 60f * k, 11f * k, live ? CGreen : CGrey, true);
                }
                y2 += 24f * k;
                if (!compact)
                {
                if (act.Count == 0)
                {
                    string pathNote = gexLevelsPathNote.Length > 0 ? gexLevelsPathNote : gexProfilePathNote;
                    Txt("Waiting for a sweep of a level.  " + levelsInfo + (pathNote.Length > 0 ? "  |  " + pathNote : ""), x2, y2, W - pad * 2, 10f * k, CGrey, false);
                    y2 += 20f * k;
                }
                else
                {
                    for (int i = 0; i < act.Count; i++)
                    {
                        Setup s = act[i];
                        int c = s.Dir > 0 ? CGreen : CRed;
                        double riskT = Math.Abs(s.Entry - s.Stop) / TickSize, tgtT = Math.Abs(s.Target - s.Entry) / TickSize;
                        RFill(x2 - 4f * k, y2 - 2f * k, W - pad * 2 + 8f * k, rowH - 4f * k, 5f * k, c, 0.08f);
                        RFrame(x2 - 4f * k, y2 - 2f * k, W - pad * 2 + 8f * k, rowH - 4f * k, 5f * k, c, 0.8f, 1.1f);
                        Txt(Instrument.MasterInstrument.Name + "  [score " + s.Score + "]  " + (s.Kind != 0 ? KindName(s.Kind) + (s.Dir > 0 ? " LONG" : " SHORT") : (s.Dir > 0 ? "STOP-HUNT LONG" : "STOP-HUNT SHORT")), x2 + 2f * k, y2, W - pad * 2, 11f * k, c, true);
                        string why = s.Kind != 0 ? (KindName(s.Kind) + " [" + s.LevelLabel + "]" + (s.Ob ? " + OB" : "") + " \u2192 " + (s.Dir > 0 ? "LONG" : "SHORT")) : "SWEEP [" + s.LevelLabel + "]" + (s.Ob ? " + OB-" + (s.Dir > 0 ? "SUPPORT" : "RESISTANCE") : "") + (s.Fvg ? " + FVG" : "") + " → " + (s.Dir > 0 ? "LONG" : "SHORT");
                        Txt(why, x2 + 2f * k, y2 + 15f * k, W - pad * 2, 10f * k, CWhite, false);
                        Txt("E: " + s.Entry.ToString("F2", CultureInfo.InvariantCulture) + "  |  SL: " + s.Stop.ToString("F2", CultureInfo.InvariantCulture) + " (-" + riskT.ToString("F0", CultureInfo.InvariantCulture) + "T)  |  TP: " + s.Target.ToString("F2", CultureInfo.InvariantCulture) + " (+" + tgtT.ToString("F0", CultureInfo.InvariantCulture) + "T)  |  R:R 1:" + (tgtT / Math.Max(1.0, riskT)).ToString("F1", CultureInfo.InvariantCulture),
                            x2 + 2f * k, y2 + 30f * k, W - pad * 2, 10f * k, CYellow, true);
                        y2 += rowH;
                    }
                }
                // ---- all three tickers ----
                {
                    RFill(x2 - 4f * k, y2 - 2f * k, W - pad * 2 + 8f * k, 1f, 0f, CGrey, 0.35f);
                    y2 += 4f * k;
                    string me = MyRoot();
                    float iw2 = W - pad * 2;
                    {
                        TkStat lead = null, near = null; double nearD = double.MaxValue, other = 0;
                        foreach (string r0 in TkRoots)
                        {
                            TkStat t0; if (!tkStats.TryGetValue(r0, out t0) || (DateTime.UtcNow - t0.Ts) >= TimeSpan.FromMinutes(15)) continue;
                            if (t0.Open > 0 && (lead == null || t0.Score > lead.Score)) lead = t0;
                            double dd = Math.Abs(t0.NearPrice - t0.Last) / Math.Max(1.0, Math.Abs(t0.Last)); if (dd < nearD) { nearD = dd; near = t0; }
                        }
                        if (lead != null)
                        {
                            foreach (string r1 in TkRoots)
                            {
                                TkStat t1; if (r1 == lead.Root || !tkStats.TryGetValue(r1, out t1) || (DateTime.UtcNow - t1.Ts) >= TimeSpan.FromMinutes(15)) continue;
                                other = Math.Max(other, t1.Open > 0 ? t1.Score : Ready(t1));
                            }
                            double rk = Math.Abs(lead.Entry - lead.Stop), rw = Math.Abs(lead.Target - lead.Entry);
                            int lc = lead.Dir > 0 ? CGreen : CRed;
                            Txt("LEADER: " + lead.Root + " (" + (lead.Score / Math.Max(1.0, other)).ToString("0.0", CultureInfo.InvariantCulture) + "x) " + (lead.Dir > 0 ? "\u25B2" : "\u25BC") + "   " + lead.Score + "% confidence  |  CRV 1:" + (rw / Math.Max(1e-9, rk)).ToString("F1", CultureInfo.InvariantCulture), x2 + 2f * k, y2, iw2, 11f * k, lc, true);
                        }
                        else if (near != null)
                            Txt("LEADER: none yet  |  closest to a level: " + near.Root + " (" + near.Near + ")", x2 + 2f * k, y2, iw2, 10f * k, CGrey, false);
                        else
                            Txt("LEADER: none  |  open Radar on NQ / ES / YM charts", x2 + 2f * k, y2, iw2, 10f * k, CGrey, false);
                        y2 += 22f * k;
                    }
                    foreach (string r in TkRoots)
                    {
                        TkStat t; bool ok = tkStats.TryGetValue(r, out t) && (DateTime.UtcNow - t.Ts) < TimeSpan.FromMinutes(15);
                        Txt(r, x2 + 2f * k, y2, 40f * k, 10.5f * k, r == me ? CCyan : CWhite, true);
                        if (!ok) { Txt("chart not open / no data yet", x2 + 40f * k, y2, iw2 - 40f * k, 10f * k, CGrey, false); y2 += 18f * k; continue; }
                        bool has = t.Open > 0;
                        double conf = has ? t.Score : Ready(t);
                        int rc = has ? (t.Dir > 0 ? CGreen : CRed) : CGrey;
                        {
                            int segs = 5, on = (int)Math.Round(Math.Max(0, Math.Min(100, conf)) / 20.0);
                            float bx = x2 + 34f * k, sg = 2f * k, sw = (46f * k - sg * (segs - 1)) / segs;
                            for (int q = 0; q < segs; q++) RFill(bx + q * (sw + sg), y2 + 3f * k, sw, 7f * k, 1f * k, q < on ? rc : CGrey, q < on ? 0.95f : 0.2f);
                        }
                        Txt("[" + conf.ToString("F0", CultureInfo.InvariantCulture) + "%]", x2 + 86f * k, y2, 50f * k, 10f * k, rc, true);
                        if (has)
                        {
                            double rk = Math.Abs(t.Entry - t.Stop), rw = Math.Abs(t.Target - t.Entry), crv = rw / Math.Max(1e-9, rk), tkz = TickOf(r);
                            Txt(t.Dir > 0 ? "STOP-HUNT LONG" : "STOP-HUNT SHORT", x2 + 130f * k, y2, 170f * k, 10.5f * k, rc, true);
                            Txt("CRV 1:" + crv.ToString("F1", CultureInfo.InvariantCulture) + (crv >= 2.0 ? " \u2605" : ""), x2 + iw2 - 110f * k, y2, 110f * k, 10f * k, CYellow, true);
                            Txt(t.Why.Length > 0 ? t.Why : ("SWEEP \u2192 " + (t.Dir > 0 ? "LONG!" : "SHORT!")), x2 + 2f * k, y2 + 15f * k, iw2, 10f * k, CWhite, false);
                            Txt("E: " + t.Entry.ToString("F2", CultureInfo.InvariantCulture) + " | SL: " + t.Stop.ToString("F2", CultureInfo.InvariantCulture) + " (-" + (rk / tkz).ToString("F0", CultureInfo.InvariantCulture) + "T) | TP: " + t.Target.ToString("F2", CultureInfo.InvariantCulture) + " (+" + (rw / tkz).ToString("F0", CultureInfo.InvariantCulture) + "T)" + (t.Open > 1 ? "   (+" + (t.Open - 1) + " more)" : ""),
                                x2 + 2f * k, y2 + 29f * k, iw2, 9.5f * k, CYellow, false);
                            y2 += 46f * k;
                        }
                        else
                        {
                            Txt("NO SETUP", x2 + 130f * k, y2, 100f * k, 10.5f * k, CGrey, true);
                            Txt(t.Last.ToString("F2", CultureInfo.InvariantCulture), x2 + iw2 - 90f * k, y2, 90f * k, 10f * k, CWhite, false);
                            double dist = t.NearPrice - t.Last;
                            string reg = t.HasReg
                                ? (t.Share < 0 ? "-GAMMA Turbo [GEX] | E:" + Math.Abs(t.Share) + "% | Waiting for Breakout" : "+GAMMA Range [GEX] | E:" + t.Share + "% | Waiting for Wall Test")
                                : "no GEX profile loaded";
                            Txt(reg + "   (" + t.Near + " " + (dist >= 0 ? "+" : "") + dist.ToString("F1", CultureInfo.InvariantCulture) + ")", x2 + 2f * k, y2 + 15f * k, iw2, 9.5f * k, CGrey, false);
                            y2 += 34f * k;
                        }
                    }
                }
                } // !compact
                } // uiRadar
            }
        }

        // ------------------------------------------------------------------ built-in panels (ported from SneakyKineticPanel / SneakyPinPayoff / SneakyGexProfile)
        // They live inside this strategy: no indicator files are needed. Each one forwards the few chart members it uses to the strategy.
        private sealed class TextCache
        {
            private readonly Dictionary<string, DW.TextLayout> d = new Dictionary<string, DW.TextLayout>();
            public void Draw(SharpDX.Direct2D1.RenderTarget rt, string s, DW.TextFormat f, float x, float y, float w, float h, D2D.SolidColorBrush b)
            {
                if (string.IsNullOrEmpty(s) || rt == null) return;
                string key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(f) + "|" + ((int)w) + "|" + s;
                DW.TextLayout l;
                if (!d.TryGetValue(key, out l))
                {
                    if (d.Count > 1500) Clear();
                    l = new DW.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, s, f, Math.Max(1f, w), h);
                    d[key] = l;
                }
                rt.DrawTextLayout(new SharpDX.Vector2(x, y), l, b);
            }
            public void Clear() { foreach (DW.TextLayout l in d.Values) { try { l.Dispose(); } catch { } } d.Clear(); }
        }
        private abstract class PanelBase
        {
            protected readonly SneakyGexRadarStrategy H;
            protected PanelBase(SneakyGexRadarStrategy h) { H = h; }
            protected Instrument Instrument { get { return H.Instrument; } }
            protected NinjaTrader.Data.Bars Bars { get { return H.Bars; } }
            protected NinjaTrader.Data.BarsPeriod BarsPeriod { get { return H.BarsPeriod; } }
            protected ISeries<double> Close { get { return H.Close; } }
            protected ISeries<double> High { get { return H.High; } }
            protected ISeries<double> Low { get { return H.Low; } }
            protected ISeries<double> Volume { get { return H.Volume; } }
            protected TimeSeries Time { get { return H.Time; } }
            protected ISeries<double>[] Closes { get { return H.Closes; } }
            protected int CurrentBar { get { return H.CurrentBar; } }
            protected int[] CurrentBars { get { return H.CurrentBars; } }
            protected int BarsInProgress { get { return H.BarsInProgress; } }
            protected bool IsFirstTickOfBar { get { return H.IsFirstTickOfBar; } }
            protected ChartControl ChartControl { get { return H.ChartControl; } }
            protected SharpDX.Direct2D1.RenderTarget RenderTarget { get { return H.RenderTarget; } }
            protected EMA EMA(ISeries<double> s, int n) { return H.EMA(s, n); }
            protected ATR ATR(ISeries<double> s, int n) { return H.ATR(s, n); }
            protected ADX ADX(ISeries<double> s, int n) { return H.ADX(s, n); }
            protected RSI RSI(ISeries<double> s, int n, int smooth) { return H.RSI(s, n, smooth); }
            protected SMA SMA(ISeries<double> s, int n) { return H.SMA(s, n); }
        }

private sealed class KineticModule : PanelBase
    {
        private float rL, rT, rW, rH;
        public KineticModule(SneakyGexRadarStrategy h) : base(h)
        {
            EmaLen = 20; AtrLen = 14; StrengthScale = 2.0; AdxLen = 14; RsiLen = 14; FlowWindowSec = 300;
            WMtf = 60; WFlow = 30; WRsi = 10; SignalThreshold = 40; SignalPulse = true;
            ShowVixDxy = false; VixName = "^VIX"; DxyName = "DX 12-26"; LogCsv = true;
        }
        public void DataLoaded()
        {
            for (int i = 0; i < NTF; i++)
            {
                emaTF[i] = EMA(Closes[i + 1], EmaLen);
                atrTF[i] = ATR(Closes[i + 1], AtrLen);
            }
            adxP = ADX(Close, AdxLen);
            rsiP = RSI(Close, RsiLen, 1);
            atrP = ATR(Close, AtrLen);
            volFast = SMA(Volume, 3);
            volSlow = SMA(Volume, 30);
            lastRow = null;
        }
        public bool IsDragging { get { return dragging; } }
        public void Hide() { pRw = 0f; }      // not drawn this frame: nothing to grab
        // sync %, bias, timeframe score, flow score (blank without live ticks), RSI, ADX  -- for the per-trade context log
        public string CtxCsv()
        {
            try
            {
                lock (sync)
                {
                    CultureInfo ci = CultureInfo.InvariantCulture;
                    return syncPct.ToString(ci) + "," + bias.ToString("F1", ci) + "," + mtfScore.ToString("F1", ci) + "," + (flowOk ? flowScore.ToString("F1", ci) : "") + ","
                         + (rsiOk ? rsiVal.ToString("F1", ci) : "") + "," + (powerOk ? power.ToString("F1", ci) : "");
                }
            }
            catch { return ",,,,,"; }
        }
        public void Terminate()
        {
            DetachDrag();
            try { if (rec != null) { rec.Flush(); rec.Dispose(); } } catch { }
            rec = null;
            lock (sync) DisposeGfx();
        }

        private const int NTF = 6;
        private static readonly string[] TfName = { "M1", "M3", "M5", "M15", "M30", "H1" };
        private static readonly int[] TfMin = { 1, 3, 5, 15, 30, 60 };
        private static readonly double[] TfW = { 1.0, 1.0, 1.5, 2.0, 2.0, 2.5 };

        private readonly object sync = new object();

        // indicators
        private EMA[] emaTF = new EMA[NTF];
        private ATR[] atrTF = new ATR[NTF];
        private ADX adxP;
        private RSI rsiP;
        private ATR atrP;
        private SMA volFast, volSlow;

        // live state (guarded by sync)
        private double[] strength = new double[NTF];
        private bool[] strengthOk = new bool[NTF];
        private double power, rsiVal, fastMom, volRatio, vixVal, dxyVal, dxyChg;
        private bool powerOk, rsiOk, fastOk, volOk, vixOk, dxyOk;
        private double bias, mtfScore, flowScore, rsiScore;
        private int signal, rawSignal, rawLast, pulseBar = -1, pulseDir; private bool armed = true;
        private double flowBuyPct; private bool flowOk;

        // Radar link: SneakyGexRadar on the same instrument writes RadarStatus_<root>.txt
        private DateTime lastRadarRead = DateTime.MinValue;
        private int radarOpen, radarDir, radarScore; private bool radarOk;
        private string RadarRoot()
        {
            string r = Instrument.MasterInstrument.Name.ToUpperInvariant();
            if (r == "MNQ") return "NQ"; if (r == "MES") return "ES"; if (r == "MYM") return "YM";
            return r;
        }
        // SYNC = share of available timeframe tiles pointing the same way as the bias (written for the Radar status line)
        private int syncPct = 0; private DateTime lastSyncWrite = DateTime.MinValue;
        private void CalcSync(double[] st, bool[] ok, double bi)
        {
            int n = 0, agree = 0, up = 0;
            for (int i = 0; i < NTF; i++) { if (!ok[i]) continue; n++; if (st[i] >= 0) up++; }
            if (n == 0) { syncPct = 0; return; }
            agree = bi >= 0 ? up : n - up;
            if (Math.Abs(bi) < 0.5) agree = Math.Max(up, n - up);
            syncPct = (int)Math.Round(100.0 * agree / n);
            if (DateTime.Now - lastSyncWrite < TimeSpan.FromSeconds(2)) return;
            lastSyncWrite = DateTime.Now;
            try { File.WriteAllText(Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexSync_" + RadarRoot() + ".txt"), syncPct + "|" + bi.ToString("F0", CultureInfo.InvariantCulture)); } catch { }
        }
        private void ReadRadar()
        {
            try
            {
                if (DateTime.Now - lastRadarRead < TimeSpan.FromSeconds(2)) return;
                lastRadarRead = DateTime.Now;
                string f = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "RadarStatus_" + RadarRoot() + ".txt");
                if (!File.Exists(f)) { radarOk = false; return; }
                string[] a = File.ReadAllText(f).Split('|');
                if (a.Length < 6) { radarOk = false; return; }
                DateTime ts = DateTime.Parse(a[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
                radarOk = (DateTime.UtcNow - ts) < TimeSpan.FromMinutes(3);
                radarOpen = int.Parse(a[3]); radarDir = int.Parse(a[4]); radarScore = int.Parse(a[5]);
            }
            catch { radarOk = false; }
        }

        // order flow window
        private struct FlowEv { public DateTime T; public double Buy; public double Sell; }
        private Queue<FlowEv> flowQ = new Queue<FlowEv>();
        private double flowBuy, flowSell;
        private double lastAsk, lastBid, lastPx;

        private int vixIdx = -1, dxyIdx = -1;
        private StreamWriter rec;
        private string recPath = "";
        private string[] lastRow;


        // ------------------------------------------------------------------ properties
        public RadarStratCorner Corner { get { return H.KinCorner; } set { H.KinCorner = value; } }
        public int OffsetX { get { return H.KinOffsetX; } set { H.KinOffsetX = value; } }
        public int OffsetY { get { return H.KinOffsetY; } set { H.KinOffsetY = value; } }
        public double PanelScale { get { return H.KinScale; } set { H.KinScale = value; } }

        public int EmaLen { get; set; }
        public int AtrLen { get; set; }
        public double StrengthScale { get; set; }
        public int AdxLen { get; set; }
        public int RsiLen { get; set; }
        public int FlowWindowSec { get; set; }

        public int WMtf { get; set; }
        public int WFlow { get; set; }
        public int WRsi { get; set; }
        public int SignalThreshold { get; set; }
        public bool SignalPulse { get; set; }

        public bool ShowVixDxy { get; set; }
        public string VixName { get; set; }
        public string DxyName { get; set; }

        public bool LogCsv { get; set; }

        // ------------------------------------------------------------------ lifecycle


        // ------------------------------------------------------------------ order flow (live ticks)
        public void OnTick(MarketDataEventArgs e)
        {
            if (BarsInProgress != 0) return;
            if (e.MarketDataType == MarketDataType.Ask) { lastAsk = e.Price; return; }
            if (e.MarketDataType == MarketDataType.Bid) { lastBid = e.Price; return; }
            if (e.MarketDataType != MarketDataType.Last) return;

            double buy = 0, sell = 0, v = e.Volume;
            if (lastAsk > 0 && e.Price >= lastAsk) buy = v;
            else if (lastBid > 0 && e.Price <= lastBid) sell = v;
            else if (lastPx > 0 && e.Price > lastPx) buy = v;
            else if (lastPx > 0 && e.Price < lastPx) sell = v;
            else { buy = v * 0.5; sell = v * 0.5; }
            lastPx = e.Price;

            lock (sync)
            {
                FlowEv ev = new FlowEv(); ev.T = e.Time; ev.Buy = buy; ev.Sell = sell;
                flowQ.Enqueue(ev); flowBuy += buy; flowSell += sell;
                PruneFlow(e.Time);
            }
        }

        private void PruneFlow(DateTime now)
        {
            DateTime cut = now.AddSeconds(-FlowWindowSec);
            while (flowQ.Count > 0 && flowQ.Peek().T < cut)
            {
                FlowEv o = flowQ.Dequeue();
                flowBuy -= o.Buy; flowSell -= o.Sell;
            }
            if (flowQ.Count == 0) { flowBuy = 0; flowSell = 0; }
        }

        // ------------------------------------------------------------------ calculations
        private static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }

        public void OnBar()
        {
            int bip = BarsInProgress;
            if (bip >= 1 && bip <= NTF)
            {
                int i = bip - 1;
                if (CurrentBars[bip] < Math.Max(EmaLen, AtrLen) + 2) return;
                double atr = atrTF[i][0];
                if (atr <= 0) return;
                double s = Clamp((Closes[bip][0] - emaTF[i][0]) / (StrengthScale * atr) * 100.0, -100, 100);
                lock (sync) { strength[i] = s; strengthOk[i] = true; }
                return;
            }
            if (bip == vixIdx && vixIdx > 0) { lock (sync) { vixVal = Closes[bip][0]; vixOk = true; } return; }
            if (bip == dxyIdx && dxyIdx > 0)
            {
                double chg = 0;
                if (CurrentBars[bip] > 61 && Closes[bip][60] > 0) chg = (Closes[bip][0] / Closes[bip][60] - 1.0) * 100.0;
                lock (sync) { dxyVal = Closes[bip][0]; dxyChg = chg; dxyOk = true; }
                return;
            }
            if (bip != 0) return;
            if (CurrentBar < Math.Max(30, Math.Max(AdxLen, RsiLen) + 2)) return;

            if (IsFirstTickOfBar && CurrentBar > 1 && LogCsv) WriteRow();

            lock (sync)
            {
                PruneFlow(Time[0]);
                power = adxP[0]; powerOk = true;
                rsiVal = rsiP[0]; rsiOk = true;
                double a = atrP[0];
                if (a > 0) { fastMom = (Close[0] - Close[3]) / a; fastOk = true; }
                double vs = volSlow[1];
                if (vs > 0) { volRatio = volFast[1] / vs; volOk = true; }

                // timeframe score (weighted mean of available tiles)
                double sw = 0, sv = 0;
                for (int i = 0; i < NTF; i++) if (strengthOk[i]) { sw += TfW[i]; sv += TfW[i] * strength[i]; }
                mtfScore = sw > 0 ? sv / sw : 0;

                double tot = flowBuy + flowSell;
                flowOk = tot > 0;
                flowBuyPct = flowOk ? 100.0 * flowBuy / tot : 50.0;
                flowScore = flowOk ? (flowBuyPct - 50.0) * 2.0 : 0;

                rsiScore = Clamp((rsiVal - 50.0) * 2.0, -100, 100);

                double w1 = WMtf, w2 = flowOk ? WFlow : 0, w3 = WRsi;
                double wt = w1 + w2 + w3;
                bias = wt > 0 ? (w1 * mtfScore + w2 * flowScore + w3 * rsiScore) / wt : 0;
                rawSignal = bias >= SignalThreshold ? 1 : (bias <= -SignalThreshold ? -1 : 0);
                if (SignalPulse)
                {
                    if (IsFirstTickOfBar && rawLast == 0) armed = true;      // previous bar closed inside the threshold: ready for a new cross
                    if (rawSignal != 0 && armed) { armed = false; pulseBar = CurrentBar; pulseDir = rawSignal; }
                    signal = (pulseBar == CurrentBar && rawSignal == pulseDir) ? pulseDir : 0;
                }
                else signal = rawSignal;
                rawLast = rawSignal;


                lastRow = BuildRow(Time[0], Close[0]);
            }
        }

        private string[] BuildRow(DateTime t, double c)
        {
            List<string> r = new List<string>();
            r.Add(t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            r.Add(c.ToString("F2", CultureInfo.InvariantCulture));
            for (int i = 0; i < NTF; i++) r.Add(strengthOk[i] ? strength[i].ToString("F1", CultureInfo.InvariantCulture) : "");
            r.Add(powerOk ? power.ToString("F1", CultureInfo.InvariantCulture) : "");
            r.Add(flowOk ? flowBuyPct.ToString("F1", CultureInfo.InvariantCulture) : "");
            r.Add(rsiOk ? rsiVal.ToString("F1", CultureInfo.InvariantCulture) : "");
            r.Add(fastOk ? fastMom.ToString("F2", CultureInfo.InvariantCulture) : "");
            r.Add(bias.ToString("F1", CultureInfo.InvariantCulture));
            r.Add(rawSignal.ToString(CultureInfo.InvariantCulture));
            r.Add(vixOk ? vixVal.ToString("F2", CultureInfo.InvariantCulture) : "");
            r.Add(dxyOk ? dxyVal.ToString("F2", CultureInfo.InvariantCulture) : "");
            return r.ToArray();
        }

        // snapshot from the last tick of the bar that just closed -> one row per bar
        private void WriteRow()
        {
            try
            {
                string[] row;
                lock (sync) row = lastRow;
                if (row == null) return;
                if (rec == null)
                {
                    string dir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "SneakyExport");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string nm = Instrument.MasterInstrument.Name + "_" + BarsPeriod.BarsPeriodType.ToString() + BarsPeriod.Value.ToString(CultureInfo.InvariantCulture);
                    recPath = Path.Combine(dir, "kinetic_" + nm + ".csv");
                    bool exists = File.Exists(recPath);
                    rec = new StreamWriter(new FileStream(recPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                    if (!exists) rec.WriteLine("time,close,m1,m3,m5,m15,m30,h1,power,flow_buy_pct,rsi,fast_mom,bias,signal,vix,dxy");
                }
                rec.WriteLine(string.Join(",", row));
                rec.Flush();
            }
            catch { }
        }

        // ------------------------------------------------------------------ rendering
        private Dictionary<long, D2D.SolidColorBrush> brushCache = new Dictionary<long, D2D.SolidColorBrush>();
        private Dictionary<int, DW.TextFormat> fmtCache = new Dictionary<int, DW.TextFormat>();
        private TextCache txc = new TextCache();

        private void DisposeGfx()
        {
            txc.Clear();
            foreach (D2D.SolidColorBrush b in brushCache.Values) { try { b.Dispose(); } catch { } }
            brushCache.Clear();
            foreach (DW.TextFormat f in fmtCache.Values) { try { f.Dispose(); } catch { } }
            fmtCache.Clear();
        }
        public void ResetGfx() { lock (sync) DisposeGfx(); }

        private D2D.SolidColorBrush Br(int rgb, float a)
        {
            long key = ((long)rgb << 8) | (long)Math.Max(0, Math.Min(255, (int)(a * 255)));
            D2D.SolidColorBrush b;
            if (!brushCache.TryGetValue(key, out b))
            {
                b = new D2D.SolidColorBrush(RenderTarget, new SharpDX.Color4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a));
                brushCache[key] = b;
            }
            return b;
        }
        private DW.TextFormat Fmt(float px, bool bold, bool center)
        {
            int key = ((int)(px * 10)) * 4 + (bold ? 1 : 0) + (center ? 2 : 0);
            DW.TextFormat f;
            if (!fmtCache.TryGetValue(key, out f))
            {
                DW.Factory dw = new DW.Factory();
                f = new DW.TextFormat(dw, "Arial", bold ? DW.FontWeight.Bold : DW.FontWeight.Normal, DW.FontStyle.Normal, px);
                f.WordWrapping = DW.WordWrapping.NoWrap;
                if (center) f.TextAlignment = DW.TextAlignment.Center;
                dw.Dispose();
                fmtCache[key] = f;
            }
            return f;
        }

        private void Fill(float x, float y, float w, float h, int rgb, float a)
        {
            RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, Math.Max(1f, w), Math.Max(1f, h)), Br(rgb, a));
        }
        private void Frame(float x, float y, float w, float h, int rgb, float a, float t)
        {
            RenderTarget.DrawRectangle(new SharpDX.RectangleF(x, y, Math.Max(1f, w), Math.Max(1f, h)), Br(rgb, a), t);
        }
        private void Txt(string s, float x, float y, float w, float px, int rgb, bool bold, bool center)
        {
            DW.TextFormat tf = Fmt(px, bold, center); txc.Draw(RenderTarget, s, tf, x, y, w, px + 6f, Br(rgb, 1f));
        }

        private const int CBg = 0x0A0F1A, CBorder = 0x00E5D0, CGreen = 0x00E676, CRed = 0xFF3355, CAmber = 0xFFB300, CYellow = 0xFFD400, CCyan = 0x00E5FF, CGrey = 0x8A93A6, CWhite = 0xF2F5FA, COrange = 0xFF9100;

        private void RFill(float x, float y, float w, float h, float r, int rgb, float a)
        {
            D2D.RoundedRectangle rr = new D2D.RoundedRectangle();
            rr.Rect = new SharpDX.RectangleF(x, y, Math.Max(1f, w), Math.Max(1f, h)); rr.RadiusX = r; rr.RadiusY = r;
            RenderTarget.FillRoundedRectangle(rr, Br(rgb, a));
        }
        private void RFrame(float x, float y, float w, float h, float r, int rgb, float a, float t)
        {
            D2D.RoundedRectangle rr = new D2D.RoundedRectangle();
            rr.Rect = new SharpDX.RectangleF(x, y, Math.Max(1f, w), Math.Max(1f, h)); rr.RadiusX = r; rr.RadiusY = r;
            RenderTarget.DrawRoundedRectangle(rr, Br(rgb, a), t);
        }


        // ---- drag-to-move (grab the header strip) ----
        private System.Windows.FrameworkElement dragCtl;
        private bool dragging, invPending;
        private float pRx, pRy, pRw, pRh;
        private System.Windows.Point dStart; private int dOffX, dOffY;
        private double _dsv; private int _dsT; private double DevScale()
        {
            if (_dsv > 0 && Environment.TickCount - _dsT < 2000) return _dsv;
            _dsT = Environment.TickCount;
            try { var src = System.Windows.PresentationSource.FromVisual(dragCtl); if (src != null && src.CompositionTarget != null) return (_dsv = src.CompositionTarget.TransformToDevice.M11); } catch { }
            return 1.0;
        }
        public void AttachDrag()
        {
            try
            {
                if (ChartControl == null || dragCtl != null) return;
                dragCtl = ChartControl;
                dragCtl.AddHandler(System.Windows.UIElement.PreviewMouseLeftButtonDownEvent, new System.Windows.Input.MouseButtonEventHandler(DragDown), true);
                dragCtl.AddHandler(System.Windows.UIElement.PreviewMouseMoveEvent, new System.Windows.Input.MouseEventHandler(DragMove), true);
                dragCtl.AddHandler(System.Windows.UIElement.PreviewMouseLeftButtonUpEvent, new System.Windows.Input.MouseButtonEventHandler(DragUp), true);
            }
            catch { }
        }
        public void DetachDrag()
        {
            try
            {
                if (dragCtl == null) return;
                dragCtl.RemoveHandler(System.Windows.UIElement.PreviewMouseLeftButtonDownEvent, new System.Windows.Input.MouseButtonEventHandler(DragDown));
                dragCtl.RemoveHandler(System.Windows.UIElement.PreviewMouseMoveEvent, new System.Windows.Input.MouseEventHandler(DragMove));
                dragCtl.RemoveHandler(System.Windows.UIElement.PreviewMouseLeftButtonUpEvent, new System.Windows.Input.MouseButtonEventHandler(DragUp));
                dragCtl = null;
            }
            catch { }
        }
        private void DragDown(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                H.MarkInput();
                if (dragCtl == null || pRw <= 0f) return;
                double sc = DevScale();
                System.Windows.Point p = e.GetPosition(dragCtl);
                double mx = p.X * sc, my = p.Y * sc;
                if (mx < pRx || mx > pRx + pRw || my < pRy || my > pRy + pRh) return;
                dragging = true; dStart = new System.Windows.Point(mx, my); dOffX = OffsetX; dOffY = OffsetY;
                dragCtl.CaptureMouse();
                e.Handled = true;
            }
            catch { }
        }
        private void DragMove(object s, System.Windows.Input.MouseEventArgs e)
        {
            try
            {
                H.MarkInput();
                if (!dragging || dragCtl == null) return;
                double sc = DevScale();
                System.Windows.Point p = e.GetPosition(dragCtl);
                double dx = p.X * sc - dStart.X, dy = p.Y * sc - dStart.Y;
                bool left = Corner == RadarStratCorner.TopLeft || Corner == RadarStratCorner.BottomLeft, top = Corner == RadarStratCorner.TopLeft || Corner == RadarStratCorner.TopRight;
                OffsetX = (int)Math.Max(0, Math.Min(2000, dOffX + (left ? dx : -dx)));
                OffsetY = (int)Math.Max(0, Math.Min(2000, dOffY + (top ? dy : -dy)));
                if (!invPending)
                {
                    invPending = true;   // coalesce: at most one chart redraw per render frame
                    dragCtl.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
                    {
                        invPending = false;
                        try { H.Refresh(); } catch { }
                    }));
                }
                e.Handled = true;
            }
            catch { }
        }
        private void DragUp(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (!dragging) return;
                dragging = false; H.RefreshLater();
                dragCtl.ReleaseMouseCapture();
                e.Handled = true;
            }
            catch { }
        }

        public void Render(ChartControl chartControl, ChartScale chartScale, float pL, float pT, float pW, float pH)
        {
            rL = pL; rT = pT; rW = pW; rH = pH;
            if (RenderTarget == null || chartControl == null) return;
            ReadRadar();

            double[] s = new double[NTF]; bool[] sOk = new bool[NTF];
            double pw, rs, fm, vr, bi, ms, fs, rsc, vx, dx, dxc, fb;
            bool pwOk, rsOk, fmOk, vrOk, flOk, vxOk, dxOk; int sg;
            lock (sync)
            {
                Array.Copy(strength, s, NTF); Array.Copy(strengthOk, sOk, NTF);
                pw = power; pwOk = powerOk; rs = rsiVal; rsOk = rsiOk; fm = fastMom; fmOk = fastOk; vr = volRatio; vrOk = volOk;
                bi = bias; ms = mtfScore; fs = flowScore; rsc = rsiScore; vx = vixVal; vxOk = vixOk; dx = dxyVal; dxc = dxyChg; dxOk = dxyOk;
                fb = flowBuyPct; flOk = flowOk; sg = rawSignal;
            }

            CalcSync(s, sOk, bi);
            float k = (float)PanelScale;
            float W = 410f * k, pad = 12f * k, f1 = 12f * k, f2 = 10.5f * k;
            float H = 300f * k;
            float px, py;
            float L = rL, T = rT, R = rL + rW, B = rT + rH;
            if (Corner == RadarStratCorner.TopRight || Corner == RadarStratCorner.BottomRight) px = R - W - OffsetX; else px = L + OffsetX;
            if (Corner == RadarStratCorner.TopRight || Corner == RadarStratCorner.TopLeft) py = T + OffsetY; else py = B - H - OffsetY;
            if (px < L) px = L; if (py < T) py = T;

            pRx = px; pRy = py; pRw = W; pRh = 34f * k;
            int dirC = bi >= SignalThreshold ? CGreen : (bi <= -SignalThreshold ? CRed : CAmber);
            lock (sync)
            {
                RFill(px, py, W, H, 10f * k, CBg, 0.94f);
                RFrame(px, py, W, H, 10f * k, CBorder, 1f, 1.6f);

                float x = px + pad, y = py + 9f * k, iw = W - pad * 2;

                // title + bias tag
                Txt("\u26A1 " + Instrument.MasterInstrument.Name + " KINETIC COCKPIT [SYNC " + syncPct + "%]", x, y + 4f * k, iw - 156f * k, 11f * k, CGreen, true, false);
                string tag = "[BIAS] " + (bi >= SignalThreshold ? "LONG " : (bi <= -SignalThreshold ? "SHORT " : "NEUTRAL ")) + (bi >= 0 ? "+" : "") + bi.ToString("F0", CultureInfo.InvariantCulture);
                float tagW = 150f * k;
                RFill(px + W - pad - tagW, y, tagW, 24f * k, 5f * k, dirC, 0.12f);
                RFrame(px + W - pad - tagW, y, tagW, 24f * k, 5f * k, dirC, 1f, 1.3f);
                Txt(tag, px + W - pad - tagW, y + 5f * k, tagW, f2, dirC, true, true);
                y += 30f * k;

                // radar badge (reads the Sneaky GEX Radar on this instrument)
                {
                    string rb; int rbC;
                    if (!radarOk) { rb = "[RADAR] off - no fresh status (levels file missing?)"; rbC = CGrey; }
                    else if (radarOpen > 0) { rb = "[RADAR] " + radarScore + "% STOP-HUNT " + (radarDir > 0 ? "LONG" : "SHORT") + (radarOpen > 1 ? "  (+" + (radarOpen - 1) + ")" : ""); rbC = radarDir > 0 ? CGreen : CRed; }
                    else { rb = "[RADAR] scanning - no sweep yet"; rbC = CAmber; }
                    RFill(x, y, iw, 18f * k, 4f * k, rbC, radarOk && radarOpen > 0 ? 0.14f : 0.07f);
                    RFrame(x, y, iw, 18f * k, 4f * k, rbC, 0.9f, 1.1f);
                    Txt(rb, x, y + 3f * k, iw, f2, rbC, true, true);
                }
                y += 26f * k;

                // timeframe tiles
                float gap = 6f * k, tw = (iw - gap * (NTF - 1)) / NTF, th = 40f * k;
                for (int i = 0; i < NTF; i++)
                {
                    float tx = x + i * (tw + gap);
                    bool flat0 = sOk[i] && Math.Round(s[i]) == 0;   // rounds to 0% -> neutral, not a red/green arrow
                    int c = !sOk[i] ? CGrey : (flat0 ? CYellow : (s[i] >= 0 ? CGreen : CRed));
                    RFill(tx, y, tw, th, 5f * k, c, 0.10f);
                    RFrame(tx, y, tw, th, 5f * k, c, 1f, 1.4f);
                    Txt(TfName[i], tx, y + 4f * k, tw, f2, CWhite, false, true);
                    string v = !sOk[i] ? "--" : (flat0 ? "\u2014 0%" : ((s[i] >= 0 ? "\u25B2 " : "\u25BC ") + Math.Abs(s[i]).ToString("F0", CultureInfo.InvariantCulture) + "%"));
                    Txt(v, tx, y + 21f * k, tw, f2, c, true, true);
                }
                y += th + 10f * k;

                // power (10 bright segments)
                Txt("POWER: " + (pwOk ? pw.ToString("F0", CultureInfo.InvariantCulture) + "%" : "--"), x, y, iw, f2, CYellow, true, false);
                y += 16f * k;
                Segs(x, y, iw * 0.34f, 7f * k, 10, pwOk ? (int)Math.Round(Clamp(pw, 0, 100) / 10.0) : 0, CYellow, 3f * k);
                y += 14f * k;

                // order flow line + segmented bar
                string press = !flOk ? "--" : (fb >= 55 ? "BUY \u25B2" : (fb <= 45 ? "SELL \u25BC" : "BALANCED"));
                string vol = !vrOk ? "--" : (vr >= 1.3 ? "HIGH" : (vr <= 0.7 ? "LOW" : "NORMAL"));
                int pressC = !flOk ? CGrey : (fb >= 55 ? CGreen : (fb <= 45 ? CRed : CAmber));
                string flowTxt = flOk
                    ? ("ORDERFLOW:  " + (100 - fb).ToString("F0", CultureInfo.InvariantCulture) + "% SELL vs " + fb.ToString("F0", CultureInfo.InvariantCulture) + "% BUY  |  PRESSURE: " + press + "  |  VOL: " + vol)
                    : ("ORDERFLOW:  no live ticks  |  VOL: " + vol);
                Txt(flowTxt, x, y, iw, f2, flOk ? CWhite : CGrey, true, false);
                y += 16f * k;
                {
                    int tot = 44, sellN = flOk ? (int)Math.Round((100 - fb) / 100.0 * tot) : 0;
                    float sgap = 2f * k, sw = (iw - sgap * (tot - 1)) / tot;
                    for (int i = 0; i < tot; i++)
                        Fill(x + i * (sw + sgap), y, sw, 7f * k, !flOk ? CGrey : (i < sellN ? CRed : CGreen), !flOk ? 0.25f : 1f);
                }
                y += 14f * k;

                // fast momentum + bar
                string fmT = !fmOk ? "--" : (Math.Abs(fm).ToString("F1", CultureInfo.InvariantCulture) + "x " + (fm >= 0 ? "\u25B2" : "\u25BC") + (Math.Abs(fm) >= 1.0 ? "  (MOMENTUM ACTIVE)" : "  (WEAK)"));
                int fmC = !fmOk ? CGrey : (Math.Abs(fm) >= 1.0 ? (fm > 0 ? CGreen : CRed) : CGrey);
                Txt("FAST MOMENTUM:  " + fmT, x, y, iw, f2, fmC, true, false);
                y += 16f * k;
                {
                    int tot = 44, on = fmOk ? (int)Math.Round(Clamp(Math.Abs(fm) / 3.0, 0, 1) * tot) : 0;
                    float sgap = 2f * k, sw = (iw - sgap * (tot - 1)) / tot;
                    for (int i = 0; i < tot; i++)
                        Fill(x + i * (sw + sgap), y, sw, 7f * k, i < on ? fmC : CGrey, i < on ? 1f : 0.2f);
                }
                y += 15f * k;

                // RSI (left) + VIX / DXY (right)
                string rsT = !rsOk ? "--" : (rs.ToString("F1", CultureInfo.InvariantCulture) + (rs >= 70 ? " (OVERBOUGHT)" : (rs <= 30 ? " (OVERSOLD)" : " (NEUTRAL)")));
                Txt("RSI: " + rsT, x, y, iw * 0.45f, f2, CGrey, false, false);
                if (ShowVixDxy)
                {
                    float rx = x + iw * 0.47f;
                    string vlab = !vxOk ? "n/a" : (vx < 15 ? "LOW" : (vx < 20 ? "NORMAL" : (vx < 30 ? "ELEVATED" : "PANIC")));
                    int vlev = !vxOk ? 0 : (vx < 15 ? 1 : (vx < 20 ? 2 : 3));
                    int vc = !vxOk ? CGrey : (vx < 15 ? CGreen : (vx < 20 ? COrange : CRed));
                    Txt("VIX (FEAR):  \u25CF " + (vxOk ? vx.ToString("F1", CultureInfo.InvariantCulture) : "--") + " (" + vlab + ")", rx, y, iw * 0.53f, f2, vc, true, false);
                    Segs(x + iw - 30f * k, y + 3f * k, 30f * k, 8f * k, 3, vlev, vc, 2f * k);
                    string dlab = !dxOk ? "n/a" : (dxc > 0.15 ? "STRONG \u25B2" : (dxc < -0.15 ? "WEAK \u25BC" : "NEUTRAL \u2014"));
                    Txt("DXY (DOLLAR):  \u25CF " + dlab, rx, y + 15f * k, iw * 0.53f, f2, CGrey, true, false);
                }
                y += 34f * k;

                // bias score (the honest name for the "forecast" line)
                Txt("BIAS SCORE: " + (bi >= 0 ? "+" : "") + bi.ToString("F0", CultureInfo.InvariantCulture), x, y, iw * 0.55f, 16f * k, dirC, true, false);
                Txt("tf " + ms.ToString("+0;-0;0", CultureInfo.InvariantCulture) + " | flow " + (flOk ? fs.ToString("+0;-0;0", CultureInfo.InvariantCulture) : "--") + " | rsi " + rsc.ToString("+0;-0;0", CultureInfo.InvariantCulture),
                    x + iw * 0.52f, y + 5f * k, iw * 0.48f, 9.5f * k, CGrey, false, false);
                y += 26f * k;

                // forecast: bias mapped through a logistic curve, nudged by an open Radar setup.
                // This is a readable re-scaling of the bias score, NOT a backtested probability.
                {
                    double z = bi / 30.0 + (radarOk && radarOpen > 0 ? radarDir * 0.4 : 0.0);
                    double pUp = 100.0 / (1.0 + Math.Exp(-z));
                    bool up = pUp >= 50.0;
                    double pc = up ? pUp : 100.0 - pUp;
                    int fc = pc < 55 ? CAmber : (up ? CGreen : CRed);
                    Txt("FORECAST: " + pc.ToString("F0", CultureInfo.InvariantCulture) + "% " + (pc < 55 ? "FLAT" : (up ? "UP \u25B2" : "DOWN \u25BC")), x, y, iw * 0.6f, 16f * k, fc, true, false);
                    Txt("bias-based, not backtested", x + iw * 0.6f, y + 5f * k, iw * 0.4f, 9.5f * k, CGrey, false, false);
                }
            }
        }

        private void Segs(float x, float y, float w, float h, int total, int on, int rgb, float gap)
        {
            float sw = (w - gap * (total - 1)) / total;
            for (int i = 0; i < total; i++) Fill(x + i * (sw + gap), y, sw, h, rgb, i < on ? 1f : 0.16f);
        }
    }

private sealed class PinPayoffModule : PanelBase
    {
        public float XfX, XfY, XfSW, XfSH, XfK = 1f;      // where the host drew this card on screen and at what scale (for click mapping)
        private float rL, rT, rW, rH;
        public PinPayoffModule(SneakyGexRadarStrategy h) : base(h)
        {
            Source = PinSourceS.Auto; StartPreset = WingPresetS.Min15; DebitVertPct = 45; ManualStrike = 0; WingPoints = 0; DebitPct = 20;
            Contracts = 1; IvPct = 18; ExpiryHHmm = 1600; PowerHourHHmm = 1500; ReloadSec = 30;
            preset = Math.Max(0, Math.Min(3, (int)StartPreset)); structure = 0;
        }
        public void Terminate() { DetachClick(); lock (sync) DisposeGfx(); }

        private readonly object sync = new object();
        private double profMagnet = double.NaN, csvPin = double.NaN, profStep = 0;
        private DateTime profStamp = DateTime.MinValue, lvlStamp = DateTime.MinValue, lastCheck = DateTime.MinValue;
        private string info = "";

        // clickable chips: wing preset (confidence coverage of the expected move) and structure toggle
        private static readonly string[] ModeNames = { "SCALPER", "INTRADAY", "SWING" };
        private int modeIdx = 0; private DateTime modeRead = DateTime.MinValue;
        private bool autoPreset = true; private int wingSteps = 0;
        private DateTime modeStamp = DateTime.MinValue;
        private DateTime modeChk = DateTime.MinValue;
        private void ReadMode()
        {
            try
            {
                if ((DateTime.Now - modeChk).TotalMilliseconds < 1000) return; modeChk = DateTime.Now;
                
                string f = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexMode.txt");
                if (!File.Exists(f)) return;
                DateTime st = File.GetLastWriteTimeUtc(f); if (st == modeStamp) return; modeStamp = st;
                int i = Array.IndexOf(ModeNames, File.ReadAllText(f).Trim().ToUpperInvariant()); if (i >= 0) modeIdx = i;
            }
            catch { }
        }
        private void CycleMode()
        {
            try { modeIdx = (modeIdx + 1) % 3; File.WriteAllText(Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexMode.txt"), ModeNames[modeIdx]); modeRead = DateTime.Now; }
            catch { }
        }
        private double axTop = 1, axBot = -1;

        private int rOpen, rDir, rScore; private string rWhy = ""; private double rBias; private DateTime rRead = DateTime.MinValue;
        private void ReadRadarInfo()
        {
            try
            {
                if ((DateTime.Now - rRead).TotalSeconds < 2) return; rRead = DateTime.Now;
                string tk = Tk();
                string f = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "RadarStatus_" + tk + ".txt");
                rOpen = 0; rWhy = "";
                if (File.Exists(f))
                {
                    string[] a = File.ReadAllText(f).Split('|');
                    DateTime ts = DateTime.Parse(a[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
                    if ((DateTime.UtcNow - ts) < TimeSpan.FromMinutes(3)) { rOpen = int.Parse(a[3]); rDir = int.Parse(a[4]); rScore = int.Parse(a[5]); if (a.Length > 12) rWhy = a[12]; }
                }
                string g = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexSync_" + tk + ".txt");
                if (File.Exists(g)) { string[] b = File.ReadAllText(g).Split('|'); if (b.Length > 1) rBias = double.Parse(b[1], CultureInfo.InvariantCulture); }
            }
            catch { rOpen = 0; }
        }
        private int preset = 0;              // 0 MIN 15%, 1 STD 40%, 2 WEIT 70%, 3 MAX 95%
        private int structure = 0;           // 0 PIN (long butterfly), 1 CALL (long call spread), 2 PUT (long put spread)
        private static readonly double[] PresetZ = { 0.19, 0.52, 1.04, 1.96 };   // z for 15/40/70/95% two-sided coverage
        private static readonly string[] PresetName = { "MIN 15%", "STD 40%", "WEIT 70%", "MAX 95%" };
        private static readonly string[] StructName = { "PIN", "CALL", "PUT", "BWB" };
        private class Hit { public float X, Y, W, H; public int Id; }
        private List<Hit> hits = new List<Hit>();
        private readonly object hitSync = new object();
        private System.Windows.FrameworkElement clickCtl;
        private double _dsv; private int _dsT; private double DevScale()
        {
            if (_dsv > 0 && Environment.TickCount - _dsT < 2000) return _dsv;
            _dsT = Environment.TickCount;
            try { var src = System.Windows.PresentationSource.FromVisual(clickCtl); if (src != null && src.CompositionTarget != null) return (_dsv = src.CompositionTarget.TransformToDevice.M11); } catch { }
            return 1.0;
        }
        public void AttachClick()
        {
            try { if (ChartControl == null || clickCtl != null) return; clickCtl = ChartControl; clickCtl.AddHandler(System.Windows.UIElement.PreviewMouseLeftButtonDownEvent, new System.Windows.Input.MouseButtonEventHandler(ClickDown), true); } catch { }
        }
        public void DetachClick()
        {
            try { if (clickCtl == null) return; clickCtl.RemoveHandler(System.Windows.UIElement.PreviewMouseLeftButtonDownEvent, new System.Windows.Input.MouseButtonEventHandler(ClickDown)); clickCtl = null; } catch { }
        }
        private void ClickDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                H.MarkInput();
                if (clickCtl == null) return;
                double sc = DevScale();
                System.Windows.Point p = e.GetPosition(clickCtl);
                double mx = p.X * sc, my = p.Y * sc;
                if (XfSW < 0f) return;                                                                  // this card is hidden
                if (XfSW > 0f)
                {
                    if (mx < XfX || mx > XfX + XfSW || my < XfY || my > XfY + XfSH) return;        // outside this card
                    mx = XfX + (mx - XfX) / XfK; my = XfY + (my - XfY) / XfK;                       // the card is drawn scaled: map the click back
                }
                lock (hitSync)
                    foreach (Hit h in hits)
                        if (mx >= h.X && mx <= h.X + h.W && my >= h.Y && my <= h.Y + h.H)
                        {
                            if (h.Id < 4) { preset = h.Id; autoPreset = false; wingSteps = 0; }
                            else if (h.Id >= 10 && h.Id <= 13) structure = h.Id - 10;
                            else if (h.Id == 20) { wingSteps--; autoPreset = false; }
                            else if (h.Id == 21) { wingSteps++; autoPreset = false; }
                            else if (h.Id == 30) CycleMode();
                            else if (h.Id == 31) { autoPreset = !autoPreset; wingSteps = 0; }
                            e.Handled = true;
                            clickCtl.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { try { H.Refresh(); } catch { } }));
                            return;
                        }
            }
            catch { }
        }



        // ------------------------------------------------------------------ data
        private string Tk()
        {
            string root = Instrument.MasterInstrument.Name.ToUpperInvariant();
            return (root == "ES" || root == "MES") ? "ES" : ((root == "YM" || root == "MYM") ? "YM" : "NQ");
        }

        private static string[] ReadLines(string p)
        {
            using (FileStream fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader sr = new StreamReader(fs))
                return sr.ReadToEnd().Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private void ReloadIfNeeded()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - lastCheck).TotalSeconds < ReloadSec) return;
            lastCheck = now;
            string msg = "";
            try
            {
                string pp = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexProfile_" + Tk() + ".csv");
                if (File.Exists(pp))
                {
                    DateTime st = File.GetLastWriteTimeUtc(pp);
                    if (st != profStamp)
                    {
                        profStamp = st;
                        double magK = double.NaN, magV = 0, prev = double.NaN, step = double.MaxValue;
                        foreach (string raw in ReadLines(pp))
                        {
                            string line = raw.Trim();
                            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("Group,", StringComparison.OrdinalIgnoreCase)) continue;
                            string[] a = line.Split(',');
                            if (a.Length < 5 || !a[0].Trim().Equals("Near", StringComparison.OrdinalIgnoreCase)) continue;
                            double k, n;
                            if (!double.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out k)) continue;
                            if (!double.TryParse(a[4], NumberStyles.Float, CultureInfo.InvariantCulture, out n)) continue;
                            if (Math.Abs(n) > Math.Abs(magV)) { magV = n; magK = k; }
                            if (!double.IsNaN(prev) && k - prev > 0 && k - prev < step) step = k - prev;
                            prev = k;
                        }
                        lock (sync) { profMagnet = magK; profStep = step == double.MaxValue ? 0 : step; }
                    }
                }
                else msg = "no GexProfile file (update fetch_gex_nq.py); ";

                string lp = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexLevels_" + Tk() + ".csv");
                if (File.Exists(lp))
                {
                    DateTime st = File.GetLastWriteTimeUtc(lp);
                    if (st != lvlStamp)
                    {
                        lvlStamp = st;
                        double pin = double.NaN;
                        foreach (string raw in ReadLines(lp))
                        {
                            string line = raw.Trim();
                            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("Price,", StringComparison.OrdinalIgnoreCase)) continue;
                            string[] a = line.Split(',');
                            double px;
                            if (a.Length < 3 || !double.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out px)) continue;
                            if (a[2].Trim().Equals("PinStrike", StringComparison.OrdinalIgnoreCase)) pin = px;
                        }
                        lock (sync) csvPin = pin;
                    }
                }
                else msg += "no GexLevels file; ";
            }
            catch (Exception ex) { msg += "read error " + ex.Message; }
            lock (sync) info = msg;
        }

        public void OnBar() { ReloadIfNeeded(); }

        // ------------------------------------------------------------------ gfx helpers
        private Dictionary<long, D2D.SolidColorBrush> brushCache = new Dictionary<long, D2D.SolidColorBrush>();
        private Dictionary<int, DW.TextFormat> fmtCache = new Dictionary<int, DW.TextFormat>();
        private TextCache txc = new TextCache();
        private D2D.StrokeStyle stDash;

        private void DisposeGfx()
        {
            txc.Clear();
            foreach (D2D.SolidColorBrush b in brushCache.Values) { try { b.Dispose(); } catch { } }
            brushCache.Clear();
            foreach (DW.TextFormat f in fmtCache.Values) { try { f.Dispose(); } catch { } }
            fmtCache.Clear();
            try { if (stDash != null) stDash.Dispose(); } catch { }
            stDash = null;
        }
        public void ResetGfx() { lock (sync) DisposeGfx(); }

        private D2D.SolidColorBrush Br(int rgb, float a)
        {
            long key = ((long)rgb << 8) | (long)Math.Max(0, Math.Min(255, (int)(a * 255)));
            D2D.SolidColorBrush b;
            if (!brushCache.TryGetValue(key, out b))
            {
                b = new D2D.SolidColorBrush(RenderTarget, new SharpDX.Color4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a));
                brushCache[key] = b;
            }
            return b;
        }
        private DW.TextFormat Fmt(float px, bool bold)
        {
            int key = ((int)(px * 10)) * 2 + (bold ? 1 : 0);
            DW.TextFormat f;
            if (!fmtCache.TryGetValue(key, out f))
            {
                DW.Factory dw = new DW.Factory();
                f = new DW.TextFormat(dw, "Arial", bold ? DW.FontWeight.Bold : DW.FontWeight.Normal, DW.FontStyle.Normal, px);
                f.WordWrapping = DW.WordWrapping.NoWrap;
                dw.Dispose();
                fmtCache[key] = f;
            }
            return f;
        }
        private void Txt(string s, float x, float y, float w, float px, int rgb, bool bold)
        {
            DW.TextFormat tf = Fmt(px, bold); txc.Draw(RenderTarget, s, tf, x, y, w, px + 6f, Br(rgb, 1f));
        }
        private void Fill(float x, float y, float w, float h, int rgb, float a)
        {
            RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, Math.Max(1f, w), Math.Max(1f, h)), Br(rgb, a));
        }
        private void RFrame(float x, float y, float w, float h, float r, int rgb, float a, float t)
        {
            D2D.RoundedRectangle rr = new D2D.RoundedRectangle();
            rr.Rect = new SharpDX.RectangleF(x, y, Math.Max(1f, w), Math.Max(1f, h)); rr.RadiusX = r; rr.RadiusY = r;
            RenderTarget.DrawRoundedRectangle(rr, Br(rgb, a), t);
        }
        private void Line(float x1, float y1, float x2, float y2, int rgb, float a, float w, bool dashed)
        {
            if (dashed)
            {
                if (stDash == null)
                {
                    D2D.StrokeStyleProperties sp = new D2D.StrokeStyleProperties(); sp.DashStyle = D2D.DashStyle.Dash;
                    stDash = new D2D.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory, sp);
                }
                RenderTarget.DrawLine(new SharpDX.Vector2(x1, y1), new SharpDX.Vector2(x2, y2), Br(rgb, a), w, stDash);
            }
            else RenderTarget.DrawLine(new SharpDX.Vector2(x1, y1), new SharpDX.Vector2(x2, y2), Br(rgb, a), w);
        }
        private float Chip(string s, float x, float y, float px, int rgb, bool bold)
        {
            float w = s.Length * px * 0.62f + 16f;
            RFrame(x, y, w, px + 10f, 5f, rgb, 0.9f, 1.2f);
            Txt(s, x + 8f, y + 4f, w, px, rgb, bold);
            return x + w + 8f;
        }

        private const int CBg = 0x0A0F1A, CCyan = 0x00B8FF, CRed = 0xFF3355, CGold = 0xFFC400, CGrey = 0x8A93A6, CWhite = 0xF2F5FA, CGreen = 0x00E676, CPurple = 0xC77DFF, CYellow = 0xFFE066;

        // ------------------------------------------------------------------ model
        private static int Struct = 0;   // set per render (single UI thread)
        private static int BwbDir = 1;    // +1 = trend up (wide wing above), -1 = trend down (wide wing below)
        private static double Payoff(double s, double k, double w)
        {
            if (Struct == 1) return Math.Max(0, s - k) - Math.Max(0, s - (k + w));       // long call spread K / K+w
            if (Struct == 2) return Math.Max(0, k - s) - Math.Max(0, (k - w) - s);       // long put spread K / K-w
            if (Struct == 3) { double q = BwbDir >= 0 ? s : 2.0 * k - s; return Math.Max(0, q - (k - w)) - 2.0 * Math.Max(0, q - k) + Math.Max(0, q - (k + 2.0 * w)); }   // broken-wing fly, wide wing on the trend side
            return Math.Max(0, s - (k - w)) - 2.0 * Math.Max(0, s - k) + Math.Max(0, s - (k + w));
        }
        // expected fly payoff if the underlying ends ~ Normal(s0, sd)
        private static double ModelValue(double s0, double sd, double k, double w)
        {
            if (sd <= 1e-9) return Payoff(s0, k, w);
            double sum = 0, wsum = 0;
            for (double z = -4.0; z <= 4.0001; z += 0.25)
            {
                double wt = Math.Exp(-0.5 * z * z);
                sum += wt * Payoff(s0 + z * sd, k, w);
                wsum += wt;
            }
            return sum / wsum;
        }
        private static DateTime EasternNow()
        {
            try { return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Eastern Standard Time"); }
            catch { return DateTime.UtcNow.AddHours(-4); }
        }
        private static double MinOfDay(int hhmm) { return (hhmm / 100) * 60.0 + (hhmm % 100); }

        // ------------------------------------------------------------------ render
        public void Render(ChartControl chartControl, ChartScale chartScale, float pL, float pT, float pW, float pH)
        {
            rL = pL; rT = pT; rW = pW; rH = pH;
            if (RenderTarget == null || Bars == null || Bars.Count == 0) return;
            float L = rL, T = rT, W = rW, H = rH, R = L + W, B = T + H;
            double spot = Bars.GetClose(Bars.Count - 1);

            double pm, cp, ps; string inf;
            lock (sync) { pm = profMagnet; cp = csvPin; ps = profStep; inf = info; }

            // pin strike
            double K = double.NaN; string kSrc = "";
            if (Source == PinSourceS.Manual && ManualStrike > 0) { K = ManualStrike; kSrc = "manual"; }
            else if (Source == PinSourceS.CsvPinStrike) { K = cp; kSrc = "0DTE pin"; }
            else if (Source == PinSourceS.ProfileMagnet) { K = pm; kSrc = "GEX magnet"; }
            else
            {
                if (!double.IsNaN(cp)) { K = cp; kSrc = "0DTE pin"; }
                else if (!double.IsNaN(pm)) { K = pm; kSrc = "GEX magnet"; }
            }

            lock (sync)
            {
                Fill(L, T, W, H, CBg, 0.92f);
                if (double.IsNaN(K))
                {
                    Txt("No pin/magnet strike available. " + inf, L + 12f, T + 12f, W - 24f, 11f, CGrey, false);
                    return;
                }

                double pv = Instrument.MasterInstrument.PointValue * Contracts;
                ReadMode(); ReadRadarInfo();
                BwbDir = rOpen > 0 ? (rDir >= 0 ? 1 : -1) : (rBias >= 0 ? 1 : -1);
                if (autoPreset) preset = modeIdx == 0 ? 0 : (modeIdx == 1 ? 1 : 2);
                Struct = structure;
                double stp = ps > 0 ? ps : Math.Max(1.0, Instrument.MasterInstrument.TickSize);
                // session clock first: preset wing = z(coverage) x expected 1-sigma move over the time left (floor: 10% of session)
                DateTime et0 = EasternNow();
                double nm0 = et0.Hour * 60.0 + et0.Minute;
                double sl0 = Math.Max(1, MinOfDay(ExpiryHHmm) - (9 * 60 + 30));
                bool live0 = et0.DayOfWeek != DayOfWeek.Saturday && et0.DayOfWeek != DayOfWeek.Sunday && nm0 >= 9 * 60 + 30 && nm0 < MinOfDay(ExpiryHHmm);
                double rf0 = !live0 ? 1.0 : Math.Max(0.10, Math.Min(1.0, (MinOfDay(ExpiryHHmm) - nm0) / sl0));
                double sigma0 = spot * (IvPct / 100.0) * Math.Sqrt(rf0 / 252.0);
                double wing;
                if (WingPoints > 0) wing = WingPoints;
                else wing = Math.Max(stp, Math.Round(PresetZ[preset] * sigma0 / stp) * stp + wingSteps * stp);
                double debit = wing * ((structure == 0 || structure == 3) ? DebitPct : DebitVertPct) / 100.0;
                double maxP = (wing - debit) * pv, maxL = debit * pv;
                if (structure == 3) maxL = (wing + debit) * pv;     // narrow side loses the debit, wide side loses wing + debit
                double rr = structure == 3 ? (maxL > 0 ? maxP / maxL : 0) : (debit > 0 ? (wing - debit) / debit : 0);

                // session clock (Eastern)
                DateTime et = EasternNow();
                double nowMin = et.Hour * 60.0 + et.Minute + et.Second / 60.0;
                double openMin = 9 * 60 + 30, expMin = MinOfDay(ExpiryHHmm), phMin = MinOfDay(PowerHourHHmm);
                double sessLen = Math.Max(1, expMin - openMin);
                double rem = Math.Max(0, Math.Min(sessLen, expMin - nowMin));
                bool sessionLive = et.DayOfWeek != DayOfWeek.Saturday && et.DayOfWeek != DayOfWeek.Sunday && nowMin >= openMin && nowMin < expMin;
                double remFrac = nowMin < openMin ? 1.0 : rem / sessLen;
                double thetaPct = 100.0 * (1.0 - Math.Sqrt(remFrac));
                bool powerHour = sessionLive && nowMin >= phMin;
                double dist = spot - K;

                // header
                float hx = L + 8f, hy = T + 20f;
                hx = Chip(structure == 0 ? "LONG FLY (model)" : (structure == 1 ? "CALL SPREAD (model)" : (structure == 2 ? "PUT SPREAD (model)" : "BWB TREND " + (BwbDir > 0 ? "\u25B2" : "\u25BC") + " (model)")), hx, hy, 10f, structure == 2 ? CRed : CGreen, true);
                hx = Chip("PIN " + K.ToString("0.##", CultureInfo.InvariantCulture) + "  [" + kSrc + "]", hx, hy, 10f, CGold, true);
                Txt("R:R 1:" + rr.ToString("0.0", CultureInfo.InvariantCulture) + "  |  MAX +$" + maxP.ToString("0", CultureInfo.InvariantCulture) + "  /  RISK -$" + maxL.ToString("0", CultureInfo.InvariantCulture)
                    + "  (wing " + wing.ToString("0.#", CultureInfo.InvariantCulture) + " pts, assumed debit " + debit.ToString("0.#", CultureInfo.InvariantCulture) + ")", hx, hy + 4f, 520f, 10f, CGrey, true);

                // banner
                string banner;
                int bannerC;
                if (powerHour && Math.Abs(dist) <= wing)
                { banner = "POWER-HOUR PINNING (THETA " + thetaPct.ToString("0", CultureInfo.InvariantCulture) + "%): Forced Lock on Magnet " + K.ToString("0.##", CultureInfo.InvariantCulture) + "!  (price " + Math.Abs(dist).ToString("0.#", CultureInfo.InvariantCulture) + " pts " + (dist >= 0 ? "above" : "below") + ")"; bannerC = CPurple; }
                else if (sessionLive)
                { banner = "PIN TARGET " + K.ToString("0.##", CultureInfo.InvariantCulture) + " is " + Math.Abs(dist).ToString("0.#", CultureInfo.InvariantCulture) + " pts " + (dist >= 0 ? "below" : "above") + " price  |  model theta decayed " + thetaPct.ToString("0", CultureInfo.InvariantCulture) + "%"; bannerC = CCyan; }
                else
                { banner = "Outside the " + (ExpiryHHmm / 100).ToString("00") + ":" + (ExpiryHHmm % 100).ToString("00") + " ET 0DTE session - showing expiry payoff only"; bannerC = CGrey; }
                if (rOpen > 0 && rWhy.Length > 0) { banner = rWhy; bannerC = rDir > 0 ? CGreen : CRed; }
                float bw = Math.Min(W - 16f, banner.Length * 6.6f + 24f);
                if (rOpen > 0)
                {
                    string rt = (rScore >= 80 ? "\u2605\u2605\u2605" : (rScore >= 60 ? "\u2605\u2605" : "\u2605")) + " RADAR [" + rScore + "%]";
                    Txt(rt, R - rt.Length * 7f - 16f, T + 22f, 200f, 10.5f, CGold, true);
                }
                RFrame(L + 8f, T + 50f, bw, 22f, 6f, bannerC, 0.9f, 1.3f);
                Txt(banner, L + 18f, T + 54f, bw, 10f, bannerC, true);
                if (powerHour && Math.Abs(dist) <= wing) Chip("POWER-HOUR PIN", L + 8f + bw + 10f, T + 49f, 10f, CPurple, true);

                // clickable preset + structure chips
                {
                    List<Hit> nh = new List<Hit>();
                    float cy = T + 76f, cx = L + 8f;
                    {
                        string mt = ModeNames[modeIdx] + " (GEX) \u25BE"; float mw = mt.Length * 6.4f + 16f;
                        RFrame(cx, cy, mw, 20f, 5f, CGold, 1f, 1.2f); Txt(mt, cx + 8f, cy + 4f, mw, 9.5f, CGold, true);
                        Hit hm = new Hit(); hm.X = cx; hm.Y = cy; hm.W = mw; hm.H = 20f; hm.Id = 30; nh.Add(hm); cx += mw + 6f;
                        float aw = 50f;
                        if (autoPreset) Fill(cx, cy, aw, 20f, CGreen, 0.22f);
                        RFrame(cx, cy, aw, 20f, 5f, autoPreset ? CGreen : CGrey, autoPreset ? 1f : 0.6f, 1.2f); Txt("AUTO", cx + 10f, cy + 4f, aw, 9.5f, autoPreset ? CGreen : CGrey, autoPreset);
                        Hit ha = new Hit(); ha.X = cx; ha.Y = cy; ha.W = aw; ha.H = 20f; ha.Id = 31; nh.Add(ha); cx += aw + 14f;
                    }
                    Txt("WING", cx, cy + 4f, 40f, 9f, CGrey, true); cx += 40f;
                    for (int i = 0; i < 4; i++)
                    {
                        float cw = PresetName[i].Length * 6.4f + 16f;
                        bool on = (WingPoints <= 0) && i == preset && wingSteps == 0;
                        if (on) Fill(cx, cy, cw, 20f, CCyan, 0.22f);
                        RFrame(cx, cy, cw, 20f, 5f, on ? CCyan : CGrey, on ? 1f : 0.6f, 1.2f);
                        Txt(PresetName[i], cx + 8f, cy + 4f, cw, 9.5f, on ? CCyan : CGrey, on);
                        Hit h = new Hit(); h.X = cx; h.Y = cy; h.W = cw; h.H = 20f; h.Id = i; nh.Add(h);
                        cx += cw + 6f;
                    }
                    for (int sgn = 0; sgn < 2; sgn++)
                    {
                        float cw = 24f; string t = sgn == 0 ? "\u2212" : "+";
                        RFrame(cx, cy, cw, 20f, 5f, CGrey, 0.8f, 1.2f); Txt(t, cx + 8f, cy + 3f, cw, 11f, CWhite, true);
                        Hit h = new Hit(); h.X = cx; h.Y = cy; h.W = cw; h.H = 20f; h.Id = 20 + sgn; nh.Add(h); cx += cw + 6f;
                    }
                    cx += 14f;
                    for (int i = 0; i < 4; i++)
                    {
                        string sn = i == 3 ? "BWB " + (BwbDir > 0 ? "\u25B2" : "\u25BC") : StructName[i];
                        float cw = sn.Length * 7f + 18f;
                        bool on = i == structure; int sc = i == 2 ? CRed : (i == 1 ? CGreen : (i == 3 ? CPurple : CGold));
                        if (on) Fill(cx, cy, cw, 20f, sc, 0.22f);
                        RFrame(cx, cy, cw, 20f, 5f, on ? sc : CGrey, on ? 1f : 0.6f, 1.2f);
                        Txt(sn, cx + 9f, cy + 4f, cw, 9.5f, on ? sc : CGrey, on);
                        Hit h = new Hit(); h.X = cx; h.Y = cy; h.W = cw; h.H = 20f; h.Id = 10 + i; nh.Add(h);
                        cx += cw + 6f;
                    }
                    if (WingPoints > 0) Txt("(wing fixed by setting: " + WingPoints.ToString("0.#", CultureInfo.InvariantCulture) + " pts)", cx + 8f, cy + 4f, 260f, 9f, CGrey, false);
                    lock (hitSync) hits = nh;
                }

                // plot area
                float x0 = L + 40f, x1 = R - 70f, y0 = T + 112f, y1 = B - 44f;
                if (y1 - y0 < 40f) return;
                double lo = Math.Min(K - wing * 2.4, spot - wing * 0.3), hi = Math.Max(K + wing * 2.4, spot + wing * 0.3);
                double vTop = Math.Max(maxP, 1) * 1.25, vBot = -Math.Max(maxL, maxP * 0.12) * 1.5;
                Func<double, float> xOf = s => x0 + (float)((s - lo) / (hi - lo)) * (x1 - x0);
                Func<double, float> yOf = v => y1 - (float)((v - vBot) / (vTop - vBot)) * (y1 - y0);
                float zeroY = yOf(0);
                {
                    // NinjaTrader's right-hand axis in real dollars: vTop at y0, vBot at y1
                    double span = vTop - vBot, nt = vTop + (y0 - T) / (y1 - y0) * span, nb = vBot - (B - y1) / (y1 - y0) * span;
                    if (Math.Abs(nt - axTop) > Math.Abs(nt) * 0.005 + 1e-9 || Math.Abs(nb - axBot) > Math.Abs(nb) * 0.005 + 1e-9)
                    { axTop = nt; axBot = nb;  }
                }
                List<double> beList = new List<double>();

                Line(x0, zeroY, x1, zeroY, CGrey, 0.6f, 1f, false);

                // expiry payoff (filled by sign)
                int N = 120;
                float prevX = 0, prevY = 0;
                for (int i = 0; i <= N; i++)
                {
                    double s = lo + (hi - lo) * i / N;
                    double v = (Payoff(s, K, wing) - debit) * pv;
                    float px = xOf(s), py = yOf(v);
                    if (i > 0)
                    {
                        double vPrev = (Payoff(lo + (hi - lo) * (i - 1) / N, K, wing) - debit) * pv;
                        if ((vPrev < 0 && v >= 0) || (vPrev >= 0 && v < 0)) beList.Add(lo + (hi - lo) * (i - 1 + vPrev / (vPrev - v)) / N);
                        Line(prevX, prevY, px, py, CCyan, 1f, 2.2f, false);
                        float top = Math.Min(py, zeroY), hgt = Math.Abs(py - zeroY);
                        Fill(px - (px - prevX), top, Math.Max(1f, px - prevX), hgt, v >= 0 ? CGreen : CRed, 0.18f);
                    }
                    prevX = px; prevY = py;
                }

                // model "value now" (theta) curve
                double sd = spot * (IvPct / 100.0) * Math.Sqrt((remFrac * 1.0 / 252.0));
                prevX = 0; prevY = 0;
                for (int i = 0; i <= N; i++)
                {
                    double s = lo + (hi - lo) * i / N;
                    double v = (ModelValue(s, sd, K, wing) - debit) * pv;
                    float px = xOf(s), py = yOf(v);
                    if (i > 0) Line(prevX, prevY, px, py, CYellow, 0.9f, 1.4f, true);
                    prevX = px; prevY = py;
                }

                // breakeven prices on the payoff line
                foreach (double be in beList)
                {
                    float bx = xOf(be); string bt = be.ToString("0.#", CultureInfo.InvariantCulture);
                    Fill(bx - 3f, zeroY - 3f, 6f, 6f, CGold, 1f);
                    Txt(bt, bx - bt.Length * 3.2f, zeroY + 5f, 80f, 9f, CGold, true);
                }
                // markers
                float kx = xOf(K);
                Line(kx, y0, kx, y1, CGold, 0.9f, 1.4f, false);
                Txt("PIN " + K.ToString("0.##", CultureInfo.InvariantCulture), kx + 4f, y0 - 14f, 120f, 9.5f, CGold, true);
                if (spot >= lo && spot <= hi)
                {
                    float sx = xOf(spot);
                    Line(sx, y0, sx, y1, CWhite, 0.9f, 1.2f, true);
                    Txt(spot.ToString("0.00", CultureInfo.InvariantCulture), sx + 4f, y1 - 14f, 100f, 9.5f, CWhite, true);
                    double vNow = (Payoff(spot, K, wing) - debit) * pv;
                    Txt((vNow >= 0 ? "+$" : "-$") + Math.Abs(vNow).ToString("0", CultureInfo.InvariantCulture) + " if expired here", sx + 4f, y0 + 2f, 170f, 9.5f, vNow >= 0 ? CGreen : CRed, true);
                }
                // current price on the model "value now" curve, with its P&L tag
                if (spot >= lo && spot <= hi)
                {
                    double vm = (ModelValue(spot, sd, K, wing) - debit) * pv;
                    float mx = xOf(spot), my = yOf(vm);
                    RenderTarget.FillEllipse(new D2D.Ellipse(new SharpDX.Vector2(mx, my), 5f, 5f), Br(CBg, 1f));
                    RenderTarget.DrawEllipse(new D2D.Ellipse(new SharpDX.Vector2(mx, my), 5f, 5f), Br(CYellow, 1f), 1.6f);
                    string vt = (vm >= 0 ? "+$" : "-$") + Math.Abs(vm).ToString("0", CultureInfo.InvariantCulture) + " now";
                    float vw = vt.Length * 6.4f + 12f;
                    Fill(mx + 10f, my - 18f, vw, 16f, CBg, 0.95f); RFrame(mx + 10f, my - 18f, vw, 16f, 4f, vm >= 0 ? CGreen : CRed, 1f, 1.2f);
                    Txt(vt, mx + 16f, my - 16f, vw, 9.5f, vm >= 0 ? CGreen : CRed, true);
                }
                // value labels on the right
                {
                    string[] tg = { "+$" + maxP.ToString("0", CultureInfo.InvariantCulture), "$0.00", "-$" + maxL.ToString("0", CultureInfo.InvariantCulture) };
                    float[] tyy = { yOf(maxP), zeroY, yOf(-maxL) }; int[] tc = { CGreen, CGrey, CRed };
                    for (int q = 0; q < 3; q++)
                    {
                        Fill(x1 + 4f, tyy[q] - 9f, 60f, 18f, CBg, 0.95f); RFrame(x1 + 4f, tyy[q] - 9f, 60f, 18f, 4f, tc[q], 1f, 1.2f);
                        Txt(tg[q], x1 + 10f, tyy[q] - 6f, 56f, 10f, tc[q], true);
                    }
                    string pk = K.ToString("0.##", CultureInfo.InvariantCulture) + " \u2605"; float pw = pk.Length * 6.6f + 14f;
                    Fill(kx - pw / 2f, y1 + 2f, pw, 16f, CBg, 1f); RFrame(kx - pw / 2f, y1 + 2f, pw, 16f, 5f, CPurple, 1f, 1.3f); Txt(pk, kx - pw / 2f + 7f, y1 + 4f, pw, 9.5f, CWhite, true);
                }
                Txt("solid = value at expiry   dotted = model value now (IV " + IvPct.ToString("0.#", CultureInfo.InvariantCulture) + "%)", x0, y1 + 4f, 420f, 8.5f, CGrey, false);

                // time-of-day timeline (Eastern)
                float tx0 = L + 40f, tx1 = R - 40f, ty = B - 20f;
                Line(tx0, ty, tx1, ty, CGrey, 0.7f, 1.2f, false);
                Func<double, float> tOf = m => tx0 + (float)((m - openMin) / sessLen) * (tx1 - tx0);
                double[] tm = new double[] { openMin, MinOfDay(1130), MinOfDay(1330), phMin, expMin - 15 };
                string[] tl = new string[] { "09:30 START", "11:30 MIDDAY", "13:30 BREAKOUT", PowerHourHHmm.ToString("0000").Insert(2, ":") + " FINAL STRETCH", ((ExpiryHHmm - 15) / 100).ToString("00") + ":" + ((ExpiryHHmm - 15) % 100).ToString("00") + " EXIT!" };
                for (int i = 0; i < tm.Length; i++)
                {
                    if (tm[i] < openMin || tm[i] > expMin) continue;
                    float mx = tOf(tm[i]);
                    Line(mx, ty - 4f, mx, ty + 4f, CGrey, 0.9f, 1.2f, false);
                    Txt(tl[i], Math.Min(mx - 6f, tx1 - 84f), ty + 5f, 100f, 8.5f, i == 3 ? CPurple : CGrey, i == 3 || i == 4);
                }
                if (nowMin >= openMin && nowMin <= expMin)
                {
                    float nx = tOf(nowMin);
                    Fill(nx - 4f, ty - 4f, 8f, 8f, CGold, 1f);
                }
                if (!string.IsNullOrEmpty(inf)) Txt(inf, L + 8f, B - 12f, W - 16f, 8f, CGrey, false);
            }
        }

        #region Properties
        public PinSourceS Source { get; set; }

        public double ManualStrike { get; set; }

        public double WingPoints { get; set; }

        public double DebitPct { get; set; }

        public WingPresetS StartPreset { get; set; }

        public double DebitVertPct { get; set; }

        public int Contracts { get; set; }

        public double IvPct { get; set; }

        public int ExpiryHHmm { get; set; }

        public int PowerHourHHmm { get; set; }

        public int ReloadSec { get; set; }
        #endregion
    }

private sealed class GexProfileModule : PanelBase
    {
        public float XfX, XfY, XfSW, XfSH, XfK = 1f;      // where the host drew this card on screen and at what scale (for click mapping)
        private float rL, rT, rW, rH;
        public GexProfileModule(SneakyGexRadarStrategy h) : base(h)
        {
            Scope = GexProfileScopeS.All; RangePct = 3.0; ProfilePath = ""; ReloadSec = 30;
            ShowBary = true; ClipOutliers = true; ShowCurve = true; FollowMode = true; IvPct = 18;
        }
        public void Terminate() { DetachClick(); lock (sync) DisposeGfx(); }

        private class PRow { public double Strike, Call, Put, Net; }

        private readonly object sync = new object();
        private List<PRow> rowsAll = new List<PRow>(), rowsNear = new List<PRow>();
        private DateTime stamp = DateTime.MinValue, lastCheck = DateTime.MinValue;
        private string info = "profile: not loaded";

        // shared mode + clickable chips
        private static readonly string[] ModeNames = { "SCALPER", "INTRADAY", "SWING" };
        private int modeIdx = 0; private DateTime modeRead = DateTime.MinValue;
        private DateTime modeStamp = DateTime.MinValue;
        private DateTime modeChk = DateTime.MinValue;
        private void ReadMode()
        {
            try
            {
                if ((DateTime.Now - modeChk).TotalMilliseconds < 1000) return; modeChk = DateTime.Now;
                
                string f = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexMode.txt");
                if (!File.Exists(f)) return;
                DateTime st = File.GetLastWriteTimeUtc(f); if (st == modeStamp) return; modeStamp = st;
                int i = Array.IndexOf(ModeNames, File.ReadAllText(f).Trim().ToUpperInvariant()); if (i >= 0) modeIdx = i;
            }
            catch { }
        }
        private void CycleMode()
        {
            try { modeIdx = (modeIdx + 1) % 3; File.WriteAllText(Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexMode.txt"), ModeNames[modeIdx]); modeRead = DateTime.Now; }
            catch { }
        }
        private float[] modeRect = new float[4];
        private string echo = ""; private int echoDir; private DateTime echoRead = DateTime.MinValue;
        private void ReadEcho()
        {
            try
            {
                if ((DateTime.Now - echoRead).TotalSeconds < 2) return; echoRead = DateTime.Now;
                string r = Instrument.MasterInstrument.Name.ToUpperInvariant();
                string tk = (r == "ES" || r == "MES") ? "ES" : ((r == "YM" || r == "MYM") ? "YM" : "NQ");
                string f = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "RadarStatus_" + tk + ".txt");
                if (!File.Exists(f)) { echo = ""; return; }
                string[] a = File.ReadAllText(f).Split('|');
                DateTime ts = DateTime.Parse(a[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
                if (a.Length > 12 && int.Parse(a[3]) > 0 && (DateTime.UtcNow - ts) < TimeSpan.FromMinutes(3)) { echo = a[12]; echoDir = int.Parse(a[4]); } else echo = "";
            }
            catch { echo = ""; }
        }
        private double axTop = 1, axBot = -1;

        private System.Windows.FrameworkElement clickCtl;
        private double _dsv; private int _dsT; private double DevScale()
        {
            if (_dsv > 0 && Environment.TickCount - _dsT < 2000) return _dsv;
            _dsT = Environment.TickCount;
            try { var src = System.Windows.PresentationSource.FromVisual(clickCtl); if (src != null && src.CompositionTarget != null) return (_dsv = src.CompositionTarget.TransformToDevice.M11); } catch { }
            return 1.0;
        }
        public void AttachClick() { try { if (ChartControl == null || clickCtl != null) return; clickCtl = ChartControl; clickCtl.AddHandler(System.Windows.UIElement.PreviewMouseLeftButtonDownEvent, new System.Windows.Input.MouseButtonEventHandler(ClickDown), true); } catch { } }
        public void DetachClick() { try { if (clickCtl == null) return; clickCtl.RemoveHandler(System.Windows.UIElement.PreviewMouseLeftButtonDownEvent, new System.Windows.Input.MouseButtonEventHandler(ClickDown)); clickCtl = null; } catch { } }
        private void ClickDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                H.MarkInput();
                if (clickCtl == null || modeRect[2] <= 0) return;
                double sc = DevScale(); System.Windows.Point p = e.GetPosition(clickCtl);
                double mx = p.X * sc, my = p.Y * sc;
                if (XfSW < 0f) return;                                                                  // this card is hidden
                if (XfSW > 0f)
                {
                    if (mx < XfX || mx > XfX + XfSW || my < XfY || my > XfY + XfSH) return;        // outside this card
                    mx = XfX + (mx - XfX) / XfK; my = XfY + (my - XfY) / XfK;                       // the card is drawn scaled: map the click back
                }
                if (mx >= modeRect[0] && mx <= modeRect[0] + modeRect[2] && my >= modeRect[1] && my <= modeRect[1] + modeRect[3])
                {
                    CycleMode(); e.Handled = true;
                    clickCtl.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { try { H.Refresh(); } catch { } }));
                }
            }
            catch { }
        }



        // ------------------------------------------------------------------ data
        private string ResolvePath()
        {
            if (!string.IsNullOrEmpty(ProfilePath)) return ProfilePath;
            string root = Instrument.MasterInstrument.Name.ToUpperInvariant();
            string tk = (root == "ES" || root == "MES") ? "ES" : ((root == "YM" || root == "MYM") ? "YM" : "NQ");
            string note;
            string p = ResolveGexFile("GexProfile_" + tk + ".csv", out note);
            gexProfilePathNote = note;
            return p;
        }

        private void ReloadIfNeeded()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - lastCheck).TotalSeconds < ReloadSec && rowsAll.Count > 0) return;
            lastCheck = now;
            try
            {
                string p = ResolvePath();
                if (!File.Exists(p)) { lock (sync) info = "profile file not found - run the updated fetch_gex_nq.py" + (gexProfilePathNote.Length > 0 ? " (" + gexProfilePathNote + ")" : ""); return; }
                DateTime st = File.GetLastWriteTimeUtc(p);
                if (st == stamp && rowsAll.Count > 0) return;
                stamp = st;
                string[] lines;
                using (FileStream fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs))
                    lines = sr.ReadToEnd().Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                List<PRow> all = new List<PRow>(), near = new List<PRow>();
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("Group,", StringComparison.OrdinalIgnoreCase)) continue;
                    string[] a = line.Split(',');
                    if (a.Length < 5) continue;
                    double k, c, pu, n;
                    if (!double.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out k)) continue;
                    if (!double.TryParse(a[2], NumberStyles.Float, CultureInfo.InvariantCulture, out c)) continue;
                    if (!double.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out pu)) continue;
                    if (!double.TryParse(a[4], NumberStyles.Float, CultureInfo.InvariantCulture, out n)) continue;
                    PRow r = new PRow(); r.Strike = k; r.Call = c; r.Put = pu; r.Net = n;
                    if (a[0].Trim().Equals("Near", StringComparison.OrdinalIgnoreCase)) near.Add(r); else all.Add(r);
                }
                lock (sync)
                {
                    rowsAll = all; rowsNear = near;
                    info = "profile: " + all.Count + " strikes (" + st.ToLocalTime().ToString("MM-dd HH:mm") + ")";
                }
            }
            catch (Exception ex) { lock (sync) info = "profile read error: " + ex.Message; }
        }

        public void OnBar()
        {
            ReloadIfNeeded();
        }

        // ------------------------------------------------------------------ gfx helpers
        private Dictionary<long, D2D.SolidColorBrush> brushCache = new Dictionary<long, D2D.SolidColorBrush>();
        private Dictionary<int, DW.TextFormat> fmtCache = new Dictionary<int, DW.TextFormat>();
        private TextCache txc = new TextCache();
        private D2D.StrokeStyle stDash;

        private void DisposeGfx()
        {
            txc.Clear();
            foreach (D2D.SolidColorBrush b in brushCache.Values) { try { b.Dispose(); } catch { } }
            brushCache.Clear();
            foreach (DW.TextFormat f in fmtCache.Values) { try { f.Dispose(); } catch { } }
            fmtCache.Clear();
            try { if (stDash != null) stDash.Dispose(); } catch { }
            stDash = null;
        }
        public void ResetGfx() { lock (sync) DisposeGfx(); }

        private D2D.SolidColorBrush Br(int rgb, float a)
        {
            long key = ((long)rgb << 8) | (long)Math.Max(0, Math.Min(255, (int)(a * 255)));
            D2D.SolidColorBrush b;
            if (!brushCache.TryGetValue(key, out b))
            {
                b = new D2D.SolidColorBrush(RenderTarget, new SharpDX.Color4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a));
                brushCache[key] = b;
            }
            return b;
        }
        private DW.TextFormat Fmt(float px, bool bold)
        {
            int key = ((int)(px * 10)) * 2 + (bold ? 1 : 0);
            DW.TextFormat f;
            if (!fmtCache.TryGetValue(key, out f))
            {
                DW.Factory dw = new DW.Factory();
                f = new DW.TextFormat(dw, "Arial", bold ? DW.FontWeight.Bold : DW.FontWeight.Normal, DW.FontStyle.Normal, px);
                f.WordWrapping = DW.WordWrapping.NoWrap;
                dw.Dispose();
                fmtCache[key] = f;
            }
            return f;
        }
        private void Txt(string s, float x, float y, float w, float px, int rgb, bool bold)
        {
            DW.TextFormat tf = Fmt(px, bold); txc.Draw(RenderTarget, s, tf, x, y, w, px + 6f, Br(rgb, 1f));
        }
        private void Fill(float x, float y, float w, float h, int rgb, float a)
        {
            RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, Math.Max(1f, w), Math.Max(1f, h)), Br(rgb, a));
        }
        private void RFrame(float x, float y, float w, float h, float r, int rgb, float a, float t)
        {
            D2D.RoundedRectangle rr = new D2D.RoundedRectangle();
            rr.Rect = new SharpDX.RectangleF(x, y, Math.Max(1f, w), Math.Max(1f, h)); rr.RadiusX = r; rr.RadiusY = r;
            RenderTarget.DrawRoundedRectangle(rr, Br(rgb, a), t);
        }
        private void Line(float x1, float y1, float x2, float y2, int rgb, float a, float w, bool dashed)
        {
            if (dashed)
            {
                if (stDash == null)
                {
                    D2D.StrokeStyleProperties sp = new D2D.StrokeStyleProperties(); sp.DashStyle = D2D.DashStyle.Dash;
                    stDash = new D2D.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory, sp);
                }
                RenderTarget.DrawLine(new SharpDX.Vector2(x1, y1), new SharpDX.Vector2(x2, y2), Br(rgb, a), w, stDash);
            }
            else RenderTarget.DrawLine(new SharpDX.Vector2(x1, y1), new SharpDX.Vector2(x2, y2), Br(rgb, a), w);
        }

        private const int CBg = 0x0A0F1A, CCyan = 0x00B8FF, CRed = 0xFF3355, CGold = 0xFFC400, CGrey = 0x8A93A6, CWhite = 0xF2F5FA, CGreen = 0x00E676, CPurple = 0xC77DFF, CBlue = 0x1E88E5;

        private static string Big(double v)
        {
            double a = Math.Abs(v);
            string s = a >= 1e9 ? (a / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + "B"
                     : a >= 1e6 ? (a / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "M"
                     : a >= 1e3 ? (a / 1e3).ToString("0", CultureInfo.InvariantCulture) + "K"
                     : a.ToString("0", CultureInfo.InvariantCulture);
            return (v >= 0 ? "+" : "-") + s;
        }

        private float Chip(string s, float x, float y, float px, int rgb, bool bold)
        {
            float w = s.Length * px * 0.62f + 16f;
            RFrame(x, y, w, px + 10f, 5f, rgb, 0.9f, 1.2f);
            Txt(s, x + 8f, y + 4f, w, px, rgb, bold);
            return x + w + 8f;
        }

        // ------------------------------------------------------------------ render
        public void Render(ChartControl chartControl, ChartScale chartScale, float pL, float pT, float pW, float pH)
        {
            rL = pL; rT = pT; rW = pW; rH = pH;
            if (RenderTarget == null || Bars == null || Bars.Count == 0) return;
            ReadMode(); ReadEcho();
            bool scopeNear = FollowMode ? modeIdx != 2 : Scope == GexProfileScopeS.Near;
            double rangeE = FollowMode ? (modeIdx == 0 ? 1.5 : (modeIdx == 1 ? 3.0 : 6.0)) : RangePct;
            float L = rL, T = rT, W = rW, H = rH, R = L + W, B = T + H;
            double spot = Bars.GetClose(Bars.Count - 1);

            lock (sync)
            {
                Fill(L, T, W, H, CBg, 0.92f);
                List<PRow> rows = scopeNear && rowsNear.Count > 0 ? rowsNear : rowsAll;
                string scopeTxt = (scopeNear && rowsNear.Count > 0) ? "NEAR EXP (<=7d)" : "ALL EXPIRATIONS";
                if (rows.Count == 0)
                {
                    Txt(info, L + 12f, T + 12f, W - 24f, 11f, CGrey, false);
                    return;
                }

                // totals over the whole scope
                double calls = 0, puts = 0;
                foreach (PRow r in rows) { calls += r.Call; puts += r.Put; }
                double net = calls + puts;

                // visible window
                double lo = spot * (1.0 - rangeE / 100.0), hi = spot * (1.0 + rangeE / 100.0);
                List<PRow> vis = new List<PRow>();
                foreach (PRow r in rows) if (r.Strike >= lo && r.Strike <= hi) vis.Add(r);
                vis.Sort((a, b) => a.Strike.CompareTo(b.Strike));

                // magnet = strike with the largest |net| (whole scope); bary = |net|-weighted centre of mass (visible)
                double magK = double.NaN, magV = 0;
                foreach (PRow r in rows) if (Math.Abs(r.Net) > Math.Abs(magV)) { magV = r.Net; magK = r.Strike; }
                double sw = 0, sk = 0;
                foreach (PRow r in vis) { sw += Math.Abs(r.Net); sk += r.Strike * Math.Abs(r.Net); }
                double bary = sw > 0 ? sk / sw : double.NaN;

                // bucket strikes (~60 bars across the window) so the bars are readable
                {
                    double rawStep = double.MaxValue;
                    for (int i = 1; i < vis.Count; i++) { double d = vis[i].Strike - vis[i - 1].Strike; if (d > 1e-9 && d < rawStep) rawStep = d; }
                    double[] nice = { 1, 2, 5, 10, 25, 50, 100, 250 };
                    double bkw = nice[nice.Length - 1];
                    foreach (double nv in nice) if (nv >= (hi - lo) / 60.0) { bkw = nv; break; }
                    if (rawStep < double.MaxValue && bkw > rawStep * 1.01)
                    {
                        SortedDictionary<long, PRow> bm = new SortedDictionary<long, PRow>();
                        foreach (PRow r in vis)
                        {
                            long key = (long)Math.Floor(r.Strike / bkw); PRow b;
                            if (!bm.TryGetValue(key, out b)) { b = new PRow(); b.Strike = key * bkw + bkw / 2.0; bm[key] = b; }
                            b.Call += r.Call; b.Put += r.Put; b.Net += r.Net;
                        }
                        vis = new List<PRow>(bm.Values);
                    }
                }

                // header chips
                float hx = L + 8f, hy = T + 20f;
                {
                    string mt = ModeNames[modeIdx] + " (GEX) \u25BE";
                    modeRect[0] = hx; modeRect[1] = hy; modeRect[2] = mt.Length * 10f * 0.62f + 16f; modeRect[3] = 20f;
                    hx = Chip(mt, hx, hy, 10f, CGold, true);
                }
                hx = Chip(scopeTxt, hx, hy, 10f, CCyan, true);
                hx = Chip(net >= 0 ? "+GAMMA (stabilising)" : "-GAMMA (amplifying)", hx, hy, 10f, net >= 0 ? CGreen : CRed, true);
                Txt("CALLS " + Big(calls), hx, hy + 4f, 150f, 10f, CCyan, true); hx += 108f;
                Txt("PUTS " + Big(puts), hx, hy + 4f, 150f, 10f, CRed, true); hx += 108f;
                Txt("NET " + Big(net), hx, hy + 4f, 150f, 10f, net >= 0 ? CCyan : CRed, true); hx += 108f;
                if (!double.IsNaN(magK)) { Txt("MAGNET " + magK.ToString("0.##", CultureInfo.InvariantCulture) + " (" + Big(magV) + ")", hx, hy + 4f, 220f, 10f, CGold, true); hx += 220f; }
                if (ShowBary && !double.IsNaN(bary))
                {
                    // stretch = distance price<->barycenter as % of half the one-day expected move (IV property)
                    double sigD = spot * (IvPct / 100.0) / Math.Sqrt(252.0);
                    double stretch = sigD > 0 ? 100.0 * Math.Abs(spot - bary) / (0.5 * sigD) : 0;
                    string stT = stretch < 35 ? "AT BARY" : (stretch < 75 ? "STRETCHED" : "MAX STRETCH");
                    int stC = stretch < 35 ? CPurple : (stretch < 75 ? CGold : CRed);
                    Txt("BARY " + bary.ToString("0.0", CultureInfo.InvariantCulture) + " [" + Math.Min(999, stretch).ToString("0", CultureInfo.InvariantCulture) + "% " + stT + "]  (" + ((bary - spot) >= 0 ? "+" : "") + (bary - spot).ToString("0.0", CultureInfo.InvariantCulture) + " pts vs price)", hx, hy + 4f, 420f, 10f, stC, true);
                }

                // second chip row: call/put share and session theta
                {
                    float h2 = T + 42f, hx2 = L + 8f;
                    double tot = calls + Math.Abs(puts);
                    double bullP = tot > 0 ? 100.0 * calls / tot : 50.0;
                    hx2 = Chip("BULL " + bullP.ToString("0", CultureInfo.InvariantCulture) + "% vs BEAR " + (100.0 - bullP).ToString("0", CultureInfo.InvariantCulture) + "% (" + (bullP >= 55 ? "Bulls" : (bullP <= 45 ? "Bears" : "Balanced")) + ")", hx2, h2, 10f, bullP >= 55 ? CGreen : (bullP <= 45 ? CRed : CGold), true);
                    double remFrac = 1.0; bool liveS = false; string phase = "";
                    try
                    {
                        DateTime et = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
                        double mins = (et.Hour * 60 + et.Minute) - (9 * 60 + 30);
                        phase = mins < 120 ? "Morning" : (mins < 240 ? "Midday" : (mins < 330 ? "Afternoon" : "Power Hour"));
                        liveS = et.DayOfWeek != DayOfWeek.Saturday && et.DayOfWeek != DayOfWeek.Sunday && mins >= 0 && mins < 390;
                        remFrac = Math.Max(0.0, Math.Min(1.0, 1.0 - mins / 390.0));
                    }
                    catch { }
                    double thetaP = 100.0 * (1.0 - Math.Sqrt(remFrac));
                    // charm multiplier (my rule): how much faster delta decays than at the open = 1 / sqrt(remaining session fraction), floor 10%
                    if (liveS) hx2 = Chip("THETA " + thetaP.ToString("0", CultureInfo.InvariantCulture) + "% | CHARM " + (1.0 / Math.Sqrt(Math.Max(0.1, remFrac))).ToString("0.0", CultureInfo.InvariantCulture) + "x (" + phase + ")", hx2, h2, 10f, CGold, true);
                    else hx2 = Chip("THETA / CHARM n/a (outside 09:30-16:00 ET)", hx2, h2, 10f, CGrey, false);
                    if (echo.Length > 0) Chip(echo, hx2, h2, 10f, echoDir > 0 ? CGreen : CRed, true);
                }

                if (vis.Count == 0)
                {
                    Txt("no strikes within +/-" + rangeE.ToString("0.#", CultureInfo.InvariantCulture) + "% of price", L + 12f, T + 58f, W - 24f, 11f, CGrey, false);
                    return;
                }

                float x0 = L + 34f, x1 = R - 34f, y0 = T + 90f, y1 = B - 22f;
                float midY = (y0 + y1) / 2f, half = (y1 - y0) / 2f - 4f;
                double maxAbs = 1;
                if (ClipOutliers)
                {
                    // scale to the 90th percentile so one huge strike cannot flatten every other bar
                    List<double> av = new List<double>();
                    foreach (PRow r in vis) av.Add(Math.Abs(r.Net));
                    av.Sort();
                    maxAbs = Math.Max(1, av[Math.Min(av.Count - 1, (int)(av.Count * 0.9))] * 1.25);
                }
                else foreach (PRow r in vis) maxAbs = Math.Max(maxAbs, Math.Abs(r.Net));
                Func<double, float> xOf = k => x0 + (float)((k - lo) / (hi - lo)) * (x1 - x0);

                double step = double.MaxValue;
                for (int i = 1; i < vis.Count; i++) { double d = vis[i].Strike - vis[i - 1].Strike; if (d > 0 && d < step) step = d; }
                if (step == double.MaxValue) step = (hi - lo) / 20.0;
                float bw = Math.Max(2f, (float)(step / (hi - lo)) * (x1 - x0) * 0.72f);

                {
                    // make NinjaTrader's own right-hand axis read in real net-GEX units ($K/$M/$B): zero at midY, +/-maxAbs at +/-half px
                    double unit = maxAbs >= 5e8 ? 1e9 : (maxAbs >= 5e5 ? 1e6 : 1e3);
                    double nt = maxAbs * (midY - T) / half / unit, nb = -maxAbs * (B - midY) / half / unit;
                    if (Math.Abs(nt - axTop) > Math.Abs(nt) * 0.005 + 1e-9 || Math.Abs(nb - axBot) > Math.Abs(nb) * 0.005 + 1e-9)
                    {
                        axTop = nt; axBot = nb;
                        
                    }
                    Txt("axis: net GEX, $" + (unit == 1e9 ? "B" : (unit == 1e6 ? "M" : "K")), R - 170f, T + 66f, 165f, 8.5f, CGrey, false);
                }
                Line(x0, midY, x1, midY, CGrey, 0.5f, 1f, false);
                foreach (PRow r in vis)
                {
                    float bx = xOf(r.Strike) - bw / 2f;
                    double rel = Math.Abs(r.Net) / maxAbs;
                    bool clipped = rel > 1.0;
                    float bh = (float)Math.Min(1.0, rel) * half;
                    bool pos = r.Net >= 0;
                    bool nearPx = Math.Abs(r.Strike - spot) <= step * 2.5;
                    int col = nearPx ? (pos ? CCyan : 0xFF4D6A) : (pos ? CBlue : CRed);
                    float al = nearPx ? 1f : 0.45f;
                    if (pos) Fill(bx, midY - bh, bw, bh, col, al); else Fill(bx, midY, bw, bh, col, al);
                    if (clipped)
                    {
                        float ty = pos ? midY - bh : midY + bh;
                        Fill(bx - 1f, ty - (pos ? 2f : 0f), bw + 2f, 2f, CWhite, 1f);
                        Txt(Big(r.Net), bx - 14f, pos ? ty - 12f : ty + 2f, 70f, 8.5f, CWhite, true);
                    }
                }
                if (ShowCurve && vis.Count >= 3)
                {
                    // smoothed net-gamma curve through the bars (weights 1-2-3-2-1)
                    int rad = 8;
                    float pxPrev = 0, pyPrev = 0; bool have = false;
                    for (int i = 0; i < vis.Count; i++)
                    {
                        double a = 0, ws = 0;
                        for (int j = -rad; j <= rad; j++)
                        {
                            int q = i + j; if (q < 0 || q >= vis.Count) continue;
                            double wq = Math.Exp(-(j * j) / 18.0); a += vis[q].Net * wq; ws += wq;
                        }
                        double v = ws > 0 ? a / ws : 0;
                        float cx = xOf(vis[i].Strike), cy = midY - (float)Math.Max(-1.0, Math.Min(1.0, v / maxAbs)) * half;
                        if (have) Line(pxPrev, pyPrev, cx, cy, CGold, 0.9f, 1.4f, true);
                        pxPrev = cx; pyPrev = cy; have = true;
                    }
                }

                {
                    PRow neg = null; foreach (PRow q in vis) if (q.Net < 0 && (neg == null || q.Net < neg.Net)) neg = q;
                    if (neg != null && Math.Abs(neg.Net) / maxAbs <= 1.0)
                        Txt(Big(neg.Net), xOf(neg.Strike) - 14f, midY + (float)(Math.Abs(neg.Net) / maxAbs) * half + 2f, 80f, 8.5f, CRed, true);
                }
                // strike labels
                float lastLx = -1000f;
                foreach (PRow r in vis)
                {
                    float lx = xOf(r.Strike);
                    if (lx - lastLx < 62f) continue;
                    lastLx = lx;
                    Txt(r.Strike.ToString("0", CultureInfo.InvariantCulture), lx - 16f, y1 + 4f, 60f, 9f, CGrey, false);
                }

                // price, magnet, bary markers
                if (spot >= lo && spot <= hi)
                {
                    float px = xOf(spot);
                    Line(px, y0 - 6f, px, y1, CWhite, 0.9f, 1.2f, true);
                    PRow nr = null; foreach (PRow q in vis) if (nr == null || Math.Abs(q.Strike - spot) < Math.Abs(nr.Strike - spot)) nr = q;
                    Txt("PRICE " + spot.ToString("0.00", CultureInfo.InvariantCulture) + (nr != null ? "  " + Big(nr.Net) : ""), px + 4f, y0 - 8f, 200f, 9.5f, CWhite, true);
                }
                if (!double.IsNaN(magK) && magK >= lo && magK <= hi)
                {
                    float mx = xOf(magK);
                    Line(mx, y0, mx, y1, CGold, 0.9f, 1.6f, false);
                    Txt("MAGNET", mx + 4f, y1 - 14f, 90f, 9f, CGold, true);
                }
                if (ShowBary && !double.IsNaN(bary) && bary >= lo && bary <= hi)
                {
                    float bx2 = xOf(bary);
                    Line(bx2, y0, bx2, y1, CPurple, 0.8f, 1.2f, true);
                    Txt("BARY", bx2 + 4f, y0 + 2f, 60f, 9f, CPurple, true);
                }
                if (!double.IsNaN(magK) && magK >= lo && magK <= hi)
                {
                    float cx = xOf(magK); string t = magK.ToString("0", CultureInfo.InvariantCulture);
                    Fill(cx - 24f, y1 + 2f, 48f, 15f, CBg, 1f); RFrame(cx - 24f, y1 + 2f, 48f, 15f, 4f, CGold, 1f, 1.2f); Txt(t, cx - 16f, y1 + 4f, 60f, 9f, CGold, true);
                }
                if (spot >= lo && spot <= hi)
                {
                    float cx = xOf(spot); string t = spot.ToString("0", CultureInfo.InvariantCulture);
                    Fill(cx - 24f, y1 + 18f, 48f, 15f, CBg, 1f); RFrame(cx - 24f, y1 + 18f, 48f, 15f, 4f, CWhite, 1f, 1.2f); Txt(t, cx - 16f, y1 + 20f, 60f, 9f, CWhite, true);
                }
                Txt(info, L + 8f, B - 14f, W - 16f, 8f, CGrey, false);
            }
        }

        #region Properties
        public GexProfileScopeS Scope { get; set; }

        public double RangePct { get; set; }

        public bool ShowBary { get; set; }

        public bool ClipOutliers { get; set; }

        public bool ShowCurve { get; set; }

        public bool FollowMode { get; set; }

        public double IvPct { get; set; }

        public string ProfilePath { get; set; }

        public int ReloadSec { get; set; }
        #endregion
    }

    }
}
