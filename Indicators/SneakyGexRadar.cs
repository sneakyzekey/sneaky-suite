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
using D2D = SharpDX.Direct2D1;
using DW = SharpDX.DirectWrite;
#endregion

// SneakyGexRadar
// Watches your GEX / key levels (the GexLevels_NQ.csv that your fetch_gex_nq.py already writes, plus optional manual
// prices) for STOP-HUNT setups and tracks how every setup actually turned out.
//
//   Setup (open rules, no black box):
//     STOP-HUNT LONG : a bar wicks BELOW a level by >= PenTicks, but no deeper than MaxDepthAtr x ATR, and CLOSES back at/above it.
//     STOP-HUNT SHORT: mirror image above a level.
//   Score 0-100 (NOT a probability):  40 sweep  + 10 clean depth + 10 strong close + 10 with-trend (EMA)
//                                      + 25 order block under/over the sweep + 15 fair value gap.
//   Plan: entry = close of the sweep bar; stop = sweep extreme -/+ StopBufTicks; target = next level that is >= 1R away,
//         else TargetR x risk.
//   Optional extra setup types (each has its own checkbox under '7 Setup types'; all but Sweep default OFF, UNPROVEN):
//     Gamma Breakout : close through a Call/Put wall by >= 0.2 ATR after the previous close was on the other side -> continuation.
//     Wall-Touch Fade: bar touches a wall (within 2 ticks) without closing through, closes >= 0.2 ATR back on the near side -> fade.
//     Flip Breakdown : same close-through rule on a flip level (VolTrigger / VannaFlip / ZeroGamma) -> continuation.
//     Pin-Trap Rev.  : price stretched >= 2.5 ATR from a PinStrike / CharmMagnet, reversal candle toward it; target = the pin.
//     GEX Chop Range : net GEX positive + low efficiency ratio (choppy): fade the edges of the nearest levels above/below, target = middle.
//   Outcome tracking: pessimistic (stop checked before target on the same bar), written to radar_<instrument>.csv.
namespace NinjaTrader.NinjaScript.Indicators
{
    public enum RadarCorner { TopLeft, TopRight, BottomLeft, BottomRight }

    public class SneakyGexRadar : Indicator
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
        public RadarCorner Corner { get; set; }
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

        [Display(Name = "Scan history (uses TODAY's levels)", GroupName = "2 Setup rules", Order = 8, Description = "Off: only live bars are scanned, so setups and the results log are real. On: old bars are checked against today's levels (look-ahead, for eyeballing only; results are NOT logged).")]
        public bool BackfillHistory { get; set; }

        [Display(Name = "Draw above price bars", GroupName = "4 Display", Order = 14, Description = "ON: zone labels, marks and panels are drawn on top of the candles. OFF: everything sits behind the candles (old behaviour).")]
        public bool DrawOnTop { get; set; }

        [Display(Name = "Enable FVG retest entries", GroupName = "6 FVG entries", Order = 0, Description = "Extra entry type: price retraces into a fresh fair-value gap and a candle closes back out in the trend direction. Shown with an F mark and logged as 'FVG retest'. Does not need a GEX level.")]
        public bool FvgEntry { get; set; }
        [Display(Name = "FVG entries also drive 'Radar Signal' plot", GroupName = "6 FVG entries", Order = 1, Description = "OFF (default): FVG entries only use the separate 'FVG Signal' plot, so Predator's inputs are unchanged. ON: they also set Radar Signal / Score / Plan plots when no level setup fires.")]
        public bool FvgFeedSignal { get; set; }
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

        [Display(Name = "Alert on new setup", GroupName = "5 Output", Order = 0)]
        public bool AlertOn { get; set; }
        [Display(Name = "Record results CSV", GroupName = "5 Output", Order = 1)]
        public bool LogCsv { get; set; }

        // ------------------------------------------------------------------ lifecycle
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Sneaky GEX Radar";
                Description = "Stop-hunt radar on GEX/key levels: open-rule setups, order block / FVG confluence score, trade plan and tracked results.";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true; ZOrder = 0; IsAutoScale = false; DisplayInDataBox = false; PaintPriceMarkers = false; IsSuspendedWhileInactive = true;
                LevelsPath = ""; ExtraLevels = ""; ReloadSec = 30;
                BackfillHistory = false; MinScore = 50; PenTicks = 2; MaxDepthAtr = 1.5; DispAtr = 1.5; FvgMinAtr = 0.25; TrendEma = 50; AtrLen = 14; CooldownBars = 12;
                StopBufTicks = 2; MinRiskTicks = 4; MaxRiskTicks = 120; TargetR = 2.0; MaxTargetR = 5.0; MaxAgeBars = 30;
                Corner = RadarCorner.TopLeft; OffsetX = 12; OffsetY = 30; PanelScale = 1.0; MaxRows = 3;
                ShowLevels = false; TouchTicks = 4; ShowPlan = true; ShowZones = true; MaxZones = 6; ShowFrozen = true; FrozenTouches = 3; ShowGexRank = true; CompactCard = false;
                AlertOn = true; LogCsv = true;
                EnableSweep = true; EnableBreakout = false; EnableWallFade = false; EnableFlip = false; EnablePinTrap = false; EnableChop = false;
                DrawOnTop = true; FvgEntry = true; FvgFeedSignal = false; FvgTrend = true; FvgMaxAge = 400; FvgMaxUses = 3; FvgCooldown = 6; FvgMinScore = 0;
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Radar Signal");
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Setup Score");
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Plan Entry");
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Plan Stop");
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Plan Target");
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "FVG Signal");
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Breakout Signal");
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "WallFade Signal");
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Flip Signal");
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "PinTrap Signal");
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Chop Signal");
            }
            else if (State == State.Configure)
            {
                ZOrder = DrawOnTop ? 10020 : 0;
            }
            else if (State == State.Historical)
            {
                if (ChartControl != null) ChartControl.Dispatcher.InvokeAsync(() => AttachDrag());
            }
            else if (State == State.DataLoaded)
            {
                LoadUi();
                emaTrend = EMA(Close, TrendEma);
                atrI = ATR(Close, AtrLen);
            }
            else if (State == State.Terminated)
            {
                try { if (ChartControl != null) ChartControl.Dispatcher.InvokeAsync(() => DetachDrag()); else DetachDrag(); } catch { }
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
        protected override void OnBarUpdate()
        {
            if (CurrentBar < Math.Max(TrendEma, AtrLen) + 5) return;
            ReloadLevelsIfNeeded();
            WriteStatus();
            double atr = atrI[0];
            if (atr <= 0) return;

            Values[0][0] = 0; Values[1][0] = 0; Values[2][0] = 0; Values[3][0] = 0; Values[4][0] = 0; Values[5][0] = 0;
            for (int pv = 6; pv <= 10; pv++) Values[pv][0] = 0;
            ReloadRankIfNeeded();
            lock (sync) TrackWalls(atr);
            if (State != State.Realtime && !BackfillHistory) { lock (sync) UpdateZones(atr); return; }

            lock (sync)
            {
                UpdateZones(atr);
                ResolveSetups();
                Setup s = EnableSweep ? DetectNew(atr) : null;
                if (s != null)
                {
                    setups.Add(s);
                    Mark m = new Mark(); m.Bar = CurrentBar; m.Dir = s.Dir; m.Price = s.Dir > 0 ? Low[0] : High[0]; marks.Add(m);
                    Values[0][0] = s.Dir; Values[1][0] = s.Score; Values[2][0] = s.Entry; Values[3][0] = s.Stop; Values[4][0] = s.Target;
                    if (s.Dir > 0) lastSignalBarLong = CurrentBar; else lastSignalBarShort = CurrentBar;
                    if (AlertOn && State == State.Realtime)
                        Alert("radar" + CurrentBar, Priority.Medium, "Radar " + (s.Dir > 0 ? "STOP-HUNT LONG " : "STOP-HUNT SHORT ") + Instrument.FullName + " score " + s.Score + " @ " + s.Level.ToString("F2", CultureInfo.InvariantCulture),
                            NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, Brushes.Black, Brushes.Yellow);
                }
                Setup g = DetectGex(atr);
                if (g != null)
                {
                    setups.Add(g);
                    if (g.Kind >= 2 && g.Kind <= 6) Values[g.Kind + 4][0] = g.Dir;
                    Mark gm = new Mark(); gm.Bar = CurrentBar; gm.Dir = g.Dir; gm.Kind = g.Kind; gm.Price = g.Dir > 0 ? Low[0] : High[0]; marks.Add(gm);
                    if (s == null) { Values[0][0] = g.Dir; Values[1][0] = g.Score; Values[2][0] = g.Entry; Values[3][0] = g.Stop; Values[4][0] = g.Target; }
                    if (AlertOn && State == State.Realtime)
                        Alert("radargex" + CurrentBar, Priority.Medium, "Radar " + KindName(g.Kind) + (g.Dir > 0 ? " LONG " : " SHORT ") + Instrument.FullName + " score " + g.Score + " @ " + g.Level.ToString("F2", CultureInfo.InvariantCulture),
                            NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, Brushes.Black, Brushes.Orange);
                }
                if (FvgEntry)
                {
                    Setup f = DetectFvg(atr);
                    if (f != null)
                    {
                        setups.Add(f);
                        Mark fm = new Mark(); fm.Bar = CurrentBar; fm.Dir = f.Dir; fm.Kind = 1; fm.Price = f.Dir > 0 ? Low[0] : High[0]; marks.Add(fm);
                        Values[5][0] = f.Dir;
                        if (FvgFeedSignal && s == null) { Values[0][0] = f.Dir; Values[1][0] = f.Score; Values[2][0] = f.Entry; Values[3][0] = f.Stop; Values[4][0] = f.Target; }
                        if (AlertOn && State == State.Realtime)
                            Alert("radarfvg" + CurrentBar, Priority.Medium, "Radar FVG RETEST " + (f.Dir > 0 ? "LONG " : "SHORT ") + Instrument.FullName + " score " + f.Score + " @ " + f.Level.ToString("F2", CultureInfo.InvariantCulture),
                                NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, Brushes.Black, Brushes.Cyan);
                    }
                }
                LogTouches();
                if (setups.Count > 200) setups.RemoveRange(0, setups.Count - 200);
                if (marks.Count > 400) marks.RemoveRange(0, marks.Count - 400);
            }
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
        private D2D.StrokeStyle stDash;

        private void DisposeGfx()
        {
            foreach (D2D.SolidColorBrush b in brushCache.Values) { try { b.Dispose(); } catch { } }
            brushCache.Clear();
            foreach (DW.TextFormat f in fmtCache.Values) { try { f.Dispose(); } catch { } }
            fmtCache.Clear();
            try { if (stDash != null) stDash.Dispose(); } catch { }
            stDash = null;
        }
        public override void OnRenderTargetChanged() { lock (sync) DisposeGfx(); }

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
            RenderTarget.DrawText(s, Fmt(px, bold), new SharpDX.RectangleF(x, y, w, px + 6f), Br(rgb, 1f));
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
        private double DevScale()
        {
            try { var src = System.Windows.PresentationSource.FromVisual(dragCtl); if (src != null && src.CompositionTarget != null) return src.CompositionTarget.TransformToDevice.M11; } catch { }
            return 1.0;
        }
        private void AttachDrag()
        {
            try
            {
                if (ChartControl == null || dragCtl != null) return;
                dragCtl = ChartControl;
                dragCtl.PreviewMouseLeftButtonDown += DragDown;
                dragCtl.PreviewMouseMove += DragMove;
                dragCtl.PreviewMouseLeftButtonUp += DragUp;
            }
            catch { }
        }
        private void DetachDrag()
        {
            try
            {
                if (dragCtl == null) return;
                dragCtl.PreviewMouseLeftButtonDown -= DragDown;
                dragCtl.PreviewMouseMove -= DragMove;
                dragCtl.PreviewMouseLeftButtonUp -= DragUp;
                dragCtl = null;
            }
            catch { }
        }
        private void DragDown(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (dragCtl == null) return;
                double sc = DevScale();
                System.Windows.Point p = e.GetPosition(dragCtl);
                double mx = p.X * sc, my = p.Y * sc;
                if (InRect(chipZ, mx, my) || InRect(chipH, mx, my) || InRect(chipR, mx, my))
                {
                    if (InRect(chipZ, mx, my)) uiZones = !uiZones; else if (InRect(chipH, mx, my)) uiHide = !uiHide; else uiRadar = !uiRadar;
                    SaveUi(); e.Handled = true;
                    try { dragCtl.InvalidateVisual(); } catch { }
                    return;
                }
                if (modeRect[2] > 0 && mx >= modeRect[0] && mx <= modeRect[0] + modeRect[2] && my >= modeRect[1] && my <= modeRect[1] + modeRect[3])
                {
                    CycleMode(); e.Handled = true;
                    try { dragCtl.InvalidateVisual(); } catch { }
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
                if (!dragging || dragCtl == null) return;
                double sc = DevScale();
                System.Windows.Point p = e.GetPosition(dragCtl);
                double dx = p.X * sc - dStart.X, dy = p.Y * sc - dStart.Y;
                bool left = Corner == RadarCorner.TopLeft || Corner == RadarCorner.BottomLeft, top = Corner == RadarCorner.TopLeft || Corner == RadarCorner.TopRight;
                OffsetX = (int)Math.Max(0, Math.Min(2000, dOffX + (left ? dx : -dx)));
                OffsetY = (int)Math.Max(0, Math.Min(2000, dOffY + (top ? dy : -dy)));
                if (!invPending)
                {
                    invPending = true;   // coalesce: at most one chart redraw per render frame
                    dragCtl.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
                    {
                        invPending = false;
                        try { if (dragCtl != null) dragCtl.InvalidateVisual(); } catch { }
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
                dragging = false;
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
                string pnote;
                string f = ResolveGexFile("GexProfile_" + MyRoot() + ".csv", out pnote);
                gexProfilePathNote = pnote;
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
        private float[] chipZ = new float[4], chipH = new float[4], chipR = new float[4];
        private string UiPath() { return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexUi_" + MyRoot() + ".txt"); }
        private void LoadUi()
        {
            uiLoaded = true; uiZones = ShowZones;
            try
            {
                string f = UiPath(); if (!File.Exists(f)) return;
                string[] a = File.ReadAllText(f).Trim().Split('|');
                if (a.Length >= 3) { uiZones = a[0] == "1"; uiHide = a[1] == "1"; uiRadar = a[2] == "1"; }
            }
            catch { }
        }
        private void SaveUi()
        {
            try { File.WriteAllText(UiPath(), (uiZones ? "1" : "0") + "|" + (uiHide ? "1" : "0") + "|" + (uiRadar ? "1" : "0")); } catch { }
        }
        private static bool InRect(float[] r, double x, double y) { return r[2] > 0 && x >= r[0] && x <= r[0] + r[2] && y >= r[1] && y <= r[1] + r[3]; }
        private DateTime modeStamp = DateTime.MinValue;
        private void ReadMode()
        {
            try
            {
                
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

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (RenderTarget == null || ChartPanel == null || chartControl == null || chartScale == null || Bars == null || ChartBars == null) return;
            base.OnRender(chartControl, chartScale);
            WriteStatus(); ReadStatuses(); ReadMode(); ReadSync();
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
                if (uiZones)
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
                if (ShowGexRank && ranks.Count > 0 && lastPx > 0)
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
                for (int i = 0; i < levels.Count; i++)
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
                if (!double.IsNaN(dayHi))
                {
                    float yh = chartScale.GetYByValue(dayHi), yl = chartScale.GetYByValue(dayLo);
                    if (yh >= T && yh <= B) { HLine(L, R - 70f * k, yh, CAmber, 0.5f, 1f, true); Txt("1D Max " + dayHi.ToString("0.00", CultureInfo.InvariantCulture), L + 250f * k, yh - 12f * k, 160f * k, 9.5f * k, CAmber, true); }
                    if (yl >= T && yl <= B) { HLine(L, R - 70f * k, yl, CAmber, 0.5f, 1f, true); Txt("1D Min " + dayLo.ToString("0.00", CultureInfo.InvariantCulture), L + 250f * k, yl + 2f * k, 160f * k, 9.5f * k, CAmber, true); }
                }

                // top status line
                {
                    string reg = regimeNet >= 0 ? "+GAMMA (Stable / Mean-Reversion)" : "-GAMMA (Volatile / Forced-Hedging)";
                    string st = "GEX LEVELS " + levels.Count + "  |  " + reg + "  |  SYNC " + (syncPct >= 0 ? syncPct + "%" : "--") + "  [MODE: " + ModeNames[modeIdx] + "]";
                    float sw = st.Length * 6.0f * k;
                    Txt(st, L + (R - L) * 0.5f - sw / 2f, T + 6f * k, sw + 20f * k, 10.5f * k, regimeNet >= 0 ? CGreen : CRed, true);
                }

                // plan lines for open setups + markers
                int shown = 0;
                for (int i = setups.Count - 1; i >= 0 && ShowPlan; i--)
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
                for (int i = 0; i < marks.Count; i++)
                {
                    Mark m = marks[i];
                    if (m.Bar < ChartBars.FromIndex || m.Bar > ChartBars.ToIndex) continue;
                    float x = chartControl.GetXByBarIndex(ChartBars, m.Bar);
                    float y = chartScale.GetYByValue(m.Price);
                    if (m.Dir > 0) Txt("▲" + KindTag(m.Kind), x - 6f * k, y + 3f * k, 24f * k, 13f * k, m.Kind != 0 ? CFvgUp : CGreen, true);
                    else Txt("▼" + KindTag(m.Kind), x - 6f * k, y - 18f * k, 24f * k, 13f * k, m.Kind != 0 ? CFvgDn : CRed, true);
                }

                // on-chart toggle chips (top right): ACTIVE-ZONES / HIDE-BROKEN / RADAR
                {
                    float ch = 17f * k, cy = T + 4f * k, gap = 5f * k;
                    string[] nm = { "Active-Zones", "Hide-Broken", "RADAR" };
                    bool[] on = { uiZones, uiHide, uiRadar };
                    float[][] rc2 = { chipZ, chipH, chipR };
                    float cw0 = 0f; for (int q = 0; q < 3; q++) cw0 += nm[q].Length * 6.4f * k + 18f * k + gap;
                    float cx = R - 76f * k - cw0;
                    for (int q = 0; q < 3; q++)
                    {
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
                px = (Corner == RadarCorner.TopLeft || Corner == RadarCorner.BottomLeft) ? L + OffsetX : R - W - OffsetX;
                py = (Corner == RadarCorner.TopLeft || Corner == RadarCorner.TopRight) ? T + OffsetY : B - H - OffsetY;
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
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private SneakyGexRadar[] cacheSneakyGexRadar;
		public SneakyGexRadar SneakyGexRadar()
		{
			return SneakyGexRadar(Input);
		}

		public SneakyGexRadar SneakyGexRadar(ISeries<double> input)
		{
			if (cacheSneakyGexRadar != null)
				for (int idx = 0; idx < cacheSneakyGexRadar.Length; idx++)
					if (cacheSneakyGexRadar[idx] != null &&  cacheSneakyGexRadar[idx].EqualsInput(input))
						return cacheSneakyGexRadar[idx];
			return CacheIndicator<SneakyGexRadar>(new SneakyGexRadar(), input, ref cacheSneakyGexRadar);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.SneakyGexRadar SneakyGexRadar()
		{
			return indicator.SneakyGexRadar(Input);
		}

		public Indicators.SneakyGexRadar SneakyGexRadar(ISeries<double> input )
		{
			return indicator.SneakyGexRadar(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.SneakyGexRadar SneakyGexRadar()
		{
			return indicator.SneakyGexRadar(Input);
		}

		public Indicators.SneakyGexRadar SneakyGexRadar(ISeries<double> input )
		{
			return indicator.SneakyGexRadar(input);
		}
	}
}

#endregion
