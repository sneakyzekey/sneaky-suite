#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// SneakyEmaPullback
// Signal indicator for a plain trend-pullback rule, built to match the Python backtest it came from
// (5-minute NQ, Jan-Oct 2026). Intended chart: 5-minute NQ / MNQ.
//
// Rules (long; shorts are the mirror image)
//   1. Trend:    EMA(fast) > EMA(slow)                        (defaults 50 / 200)
//   2. Pullback: previous bar's Low <= previous bar's EMA(fast)
//   3. Trigger:  this bar closes above the previous bar's High AND above EMA(fast)
//   4. Risk:     Close - lowest Low of the last N bars (N = SwingBars, default 10, this bar included);
//                skipped if risk is below MinRisk or above MaxRisk points
//   5. Entry:    next bar's open.   Stop = entry - risk.   Target = entry + RR * risk.
//   6. One trade at a time; signals that appear while a trade is open are ignored (as in the backtest).
//      If the stop and target are both inside the same bar the stop is assumed to hit first.
//      A trade still open when the data gaps by more than 20 minutes (the daily break) is closed
//      at the previous bar's close.
//
// Sessions
//   "Trade all hours" ON  -> every bar can signal.
//   OFF (default)         -> only signals whose entry bar starts inside the window below.
//   Default window 18:00 -> 09:30 US Eastern. The test showed the edge came from the overnight and
//   early-morning hours; regular hours (09:30-16:00) were flat. Window times are US Eastern and are
//   converted from the chart's time zone automatically.
//
// Series for strategies / other indicators (value on the SIGNAL bar, i.e. the bar that just closed):
//   SignalDirection:  1 = long, -1 = short, 0 = none
//   Plots: EmaFast, EmaSlow
//
// This is a research/visual tool, not a promise of profit. Past results do not predict future results.

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SneakyEmaPullback : Indicator
    {
        // ------------------------------------------------------------------ state
        private EMA emaF, emaS;
        private TimeZoneInfo easternTz;

        // pending signal (fired on the previous bar's close, enters at this bar's open)
        private bool   pending;
        private int    pendDir;
        private double pendRisk;
        private int    pendSignalBar;

        // open trade
        private bool   inTrade;
        private int    tDir, tEntryBar;
        private double tEntry, tStop, tTarget, tRisk;
        private string tTag;

        // stats
        private int    nTrades, nWins;
        private double netPts, grossWin, grossLoss;
        private int    tradeCounter;

        private Series<double> sigSeries;

        // ------------------------------------------------------------------ lifecycle
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                 = "Trend pullback to the EMA with swing stop and R-multiple target. Optional overnight-only filter.";
                Name                        = "SneakyEmaPullback";
                Calculate                   = Calculate.OnBarClose;
                IsOverlay                   = true;
                IsAutoScale                 = false;   // invisible automation plots must not stretch the price scale
                DisplayInDataBox            = true;
                DrawOnPricePanel            = true;
                PaintPriceMarkers           = false;
                IsSuspendedWhileInactive    = true;
                MaximumBarsLookBack         = MaximumBarsLookBack.Infinite;

                FastPeriod      = 50;
                SlowPeriod      = 200;
                SwingBars       = 10;
                RewardRisk      = 3.0;
                MinRisk         = 6.0;
                MaxRisk         = 80.0;
                CostPoints      = 0.75;

                TradeAllHours   = false;
                SessionStart    = 1800;
                SessionEnd      = 930;

                ShowTradeLines  = true;
                ShowMarkers     = true;
                MarkerSize      = 20;
                MarkerDistance  = 26;
                LineBars        = 6;
                ShowStats       = true;
                PopupAlerts     = true;
                SoundFile       = "Alert2.wav";

                LongBrush       = Brushes.LimeGreen;
                ShortBrush      = Brushes.OrangeRed;

                AddPlot(new Stroke(Brushes.DodgerBlue, 2), PlotStyle.Line, "EmaFast");
                AddPlot(new Stroke(Brushes.Gray, 2),       PlotStyle.Line, "EmaSlow");

                // ---- automation plots (invisible, readable by strategies / third-party tools) ----
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "BuySignal");       // 2: 1 on a long signal bar, else 0
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SellSignal");      // 3: -1 on a short signal bar, else 0
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "StopPrice");       // 4: stop price (from signal-bar close) on a signal bar, else 0
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "TargetPrice");     // 5: target price (from signal-bar close) on a signal bar, else 0
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "StopDistance");    // 6: stop distance in points on a signal bar, else 0
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "TargetDistance");  // 7: target distance in points on a signal bar, else 0
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SimPosition");     // 8: +1 / -1 while the indicator's own simulated trade is open, else 0
            }
            else if (State == State.Configure)
            {
                try { easternTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
                catch { easternTz = null; }
            }
            else if (State == State.DataLoaded)
            {
                emaF      = EMA(FastPeriod);
                emaS      = EMA(SlowPeriod);
                sigSeries = new Series<double>(this, MaximumBarsLookBack.Infinite);
                pending = false; inTrade = false;
                nTrades = 0; nWins = 0; netPts = 0; grossWin = 0; grossLoss = 0; tradeCounter = 0;
            }
        }

        // ------------------------------------------------------------------ helpers
        private bool InWindow(DateTime chartTime)
        {
            if (TradeAllHours) return true;

            DateTime et = chartTime;
            try
            {
                if (easternTz != null && Bars != null && Bars.TradingHours != null && Bars.TradingHours.TimeZoneInfo != null)
                    et = TimeZoneInfo.ConvertTime(chartTime, Bars.TradingHours.TimeZoneInfo, easternTz);
            }
            catch { et = chartTime; }

            int hhmm = et.Hour * 100 + et.Minute;
            if (SessionStart > SessionEnd)                 // wraps midnight (e.g. 1800 -> 0930)
                return hhmm >= SessionStart || hhmm < SessionEnd;
            return hhmm >= SessionStart && hhmm < SessionEnd;
        }

        private void CloseTrade(int bar, double exitPrice, string reason)
        {
            double pts = tDir * (exitPrice - tEntry) - CostPoints;
            nTrades++;
            if (pts > 0) { nWins++; grossWin += pts; } else grossLoss += -pts;
            netPts += pts;

            if (ShowTradeLines)
            {
                int entryAgo = CurrentBar - tEntryBar;
                int exitAgo  = CurrentBar - bar;
                int endAgo   = Math.Max(exitAgo, entryAgo - LineBars);   // short stubs: at most LineBars bars long
                Draw.Line(this, tTag + "s", false, entryAgo, tStop,   endAgo, tStop,   Brushes.Red,       DashStyleHelper.Dash, 1);
                Draw.Line(this, tTag + "t", false, entryAgo, tTarget, endAgo, tTarget, Brushes.LimeGreen, DashStyleHelper.Dash, 1);
                Draw.Line(this, tTag + "e", false, entryAgo, tEntry,  endAgo, tEntry,  Brushes.Gray,      DashStyleHelper.Dot,  1);
            }
            if (ShowMarkers)
            {
                int exitAgo = CurrentBar - bar;
                bool win = pts > 0;
                // longs: mark sits above the exit bar; shorts: below it (opposite side from that direction's entry arrow)
                double anchor = tDir == 1 ? High[exitAgo] : Low[exitAgo];
                int    yOff   = tDir == 1 ? -MarkerDistance : MarkerDistance;
                Draw.Text(this, tTag + "m", false, win ? "\u2713" : "\u2715", exitAgo, anchor, yOff,
                    win ? Brushes.LimeGreen : Brushes.Red, new SimpleFont("Segoe UI Symbol", MarkerSize),
                    System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            }
            inTrade = false;
        }

        private void UpdateStatsText()
        {
            if (!ShowStats) return;
            double pf = grossLoss > 0 ? grossWin / grossLoss : 0;
            string txt = string.Format(
                "EMA Pullback {0}\nTrades {1}   Win {2:0.#}%\nNet {3:0.00} pts (${4:#,0})\nPF {5:0.00}   cost {6:0.00}/trade incl.",
                TradeAllHours ? "(all hours)" : "(" + SessionStart.ToString("0000") + "-" + SessionEnd.ToString("0000") + " ET)",
                nTrades, nTrades > 0 ? 100.0 * nWins / nTrades : 0.0,
                netPts, netPts * Instrument.MasterInstrument.PointValue, pf, CostPoints);
            Draw.TextFixed(this, "ep_stats", txt, TextPosition.TopLeft, Brushes.White, new SimpleFont("Consolas", 11),
                Brushes.Transparent, Brushes.Black, 60);
        }

        // ------------------------------------------------------------------ main
        protected override void OnBarUpdate()
        {
            for (int k = 2; k <= 8; k++) Values[k][0] = 0;
            Core();
            Values[8][0] = inTrade ? tDir : 0;
        }

        private void Core()
        {
            int need = Math.Max(SlowPeriod, SwingBars) + 10;
            if (CurrentBar < need) { sigSeries[0] = 0; return; }

            Values[0][0] = emaF[0];
            Values[1][0] = emaS[0];
            sigSeries[0] = 0;

            bool gap = (Time[0] - Time[1]).TotalMinutes > 20;
            bool exitedThisBar = false;

            // 1) a trade left open across a data gap is closed at the last close before the gap
            if (inTrade && gap)
            {
                CloseTrade(CurrentBar - 1, Close[1], "gap");
                UpdateStatsText();
            }

            // 2) a signal from the last bar before a gap is dropped
            if (pending && gap) pending = false;

            // 3) a pending signal enters at this bar's open
            if (pending)
            {
                pending = false;
                inTrade   = true;
                tDir      = pendDir;
                tRisk     = pendRisk;
                tEntry    = Open[0];
                tEntryBar = CurrentBar;
                tStop     = tEntry - tDir * tRisk;
                tTarget   = tEntry + tDir * RewardRisk * tRisk;
                tradeCounter++;
                tTag      = "ep_" + tradeCounter;

                if (ShowTradeLines)
                {
                    // provisional stub lines; redrawn from entry bar to exit bar when the trade closes
                    Draw.Line(this, tTag + "s", false, 0, tStop,   -3, tStop,   Brushes.Red,       DashStyleHelper.Dash, 1);
                    Draw.Line(this, tTag + "t", false, 0, tTarget, -3, tTarget, Brushes.LimeGreen, DashStyleHelper.Dash, 1);
                    Draw.Line(this, tTag + "e", false, 0, tEntry,  -3, tEntry,  Brushes.Gray,      DashStyleHelper.Dot,  1);
                }
            }

            // 4) manage the open trade on this bar (entry bar included, stop first)
            if (inTrade)
            {
                bool stopHit, tgtHit;
                if (tDir == 1) { stopHit = Low[0]  <= tStop; tgtHit = High[0] >= tTarget; }
                else           { stopHit = High[0] >= tStop; tgtHit = Low[0]  <= tTarget; }

                if (stopHit)      { CloseTrade(CurrentBar, tStop,   "stop");   exitedThisBar = true; }
                else if (tgtHit)  { CloseTrade(CurrentBar, tTarget, "target"); exitedThisBar = true; }
                if (exitedThisBar) UpdateStatsText();
            }

            // 5) look for a new signal (not while in a trade, not on the bar a trade just closed)
            if (inTrade || exitedThisBar) return;
            if (!InWindow(Time[0])) return;

            double ef  = emaF[0], es = emaS[0];
            double efp = emaF[1];

            // lowest low / highest high of the last SwingBars bars including this one
            double swLow = double.MaxValue, swHigh = double.MinValue;
            for (int k = 0; k < SwingBars; k++)
            {
                if (Low[k]  < swLow)  swLow  = Low[k];
                if (High[k] > swHigh) swHigh = High[k];
            }

            int    dir  = 0;
            double risk = 0;

            if (ef > es && Low[1] <= efp && Close[0] > High[1] && Close[0] > ef)
            {
                risk = Close[0] - swLow;
                if (risk >= MinRisk && risk <= MaxRisk) dir = 1;
            }
            else if (ef < es && High[1] >= efp && Close[0] < Low[1] && Close[0] < ef)
            {
                risk = swHigh - Close[0];
                if (risk >= MinRisk && risk <= MaxRisk) dir = -1;
            }

            if (dir == 0) return;

            pending = true; pendDir = dir; pendRisk = risk; pendSignalBar = CurrentBar;
            sigSeries[0] = dir;
            Values[dir == 1 ? 2 : 3][0] = dir;
            Values[4][0] = Close[0] - dir * risk;
            Values[5][0] = Close[0] + dir * RewardRisk * risk;
            Values[6][0] = risk;
            Values[7][0] = RewardRisk * risk;

            if (dir == 1) Draw.ArrowUp(this,   "ep_sig" + CurrentBar, false, 0, Low[0]  - 8 * TickSize, LongBrush);
            else          Draw.ArrowDown(this, "ep_sig" + CurrentBar, false, 0, High[0] + 8 * TickSize, ShortBrush);

            if (State == State.Realtime && PopupAlerts)
            {
                string msg = string.Format("{0} EMA pullback {1}: enter next bar open. Risk {2:0.00} pts, stop ~{3:0.00}, target ~{4:0.00} (from close {5:0.00})",
                    Instrument.FullName, dir == 1 ? "LONG" : "SHORT", risk,
                    Close[0] - dir * risk, Close[0] + dir * RewardRisk * risk, Close[0]);
                string snd = NinjaTrader.Core.Globals.InstallDir + @"\sounds\" + SoundFile;
                Alert("ep_" + CurrentBar, Priority.High, msg, snd, 10, Brushes.Black, dir == 1 ? Brushes.LimeGreen : Brushes.OrangeRed);
            }
        }

        // ------------------------------------------------------------------ outputs
        [Browsable(false), XmlIgnore]
        public Series<double> SignalDirection { get { Update(); return sigSeries; } }

        [Browsable(false), XmlIgnore]
        public Series<double> EmaFast { get { Update(); return Values[0]; } }

        [Browsable(false), XmlIgnore] public Series<double> BuySignal     { get { Update(); return Values[2]; } }
        [Browsable(false), XmlIgnore] public Series<double> SellSignal    { get { Update(); return Values[3]; } }
        [Browsable(false), XmlIgnore] public Series<double> StopPrice     { get { Update(); return Values[4]; } }
        [Browsable(false), XmlIgnore] public Series<double> TargetPrice   { get { Update(); return Values[5]; } }
        [Browsable(false), XmlIgnore] public Series<double> StopDistance  { get { Update(); return Values[6]; } }
        [Browsable(false), XmlIgnore] public Series<double> TargetDistance{ get { Update(); return Values[7]; } }
        [Browsable(false), XmlIgnore] public Series<double> SimPosition   { get { Update(); return Values[8]; } }

        [Browsable(false), XmlIgnore]
        public Series<double> EmaSlow { get { Update(); return Values[1]; } }

        // ------------------------------------------------------------------ properties
        [NinjaScriptProperty, Range(2, 500)]
        [Display(Name = "Fast EMA", GroupName = "1. Signal", Order = 0)]
        public int FastPeriod { get; set; }

        [NinjaScriptProperty, Range(5, 1000)]
        [Display(Name = "Slow EMA", GroupName = "1. Signal", Order = 1)]
        public int SlowPeriod { get; set; }

        [NinjaScriptProperty, Range(2, 100)]
        [Display(Name = "Swing bars (stop)", GroupName = "1. Signal", Order = 2)]
        public int SwingBars { get; set; }

        [NinjaScriptProperty, Range(0.5, 20.0)]
        [Display(Name = "Reward : risk", GroupName = "1. Signal", Order = 3)]
        public double RewardRisk { get; set; }

        [NinjaScriptProperty, Range(0.0, 1000.0)]
        [Display(Name = "Min risk (points)", GroupName = "1. Signal", Order = 4)]
        public double MinRisk { get; set; }

        [NinjaScriptProperty, Range(1.0, 2000.0)]
        [Display(Name = "Max risk (points)", GroupName = "1. Signal", Order = 5)]
        public double MaxRisk { get; set; }

        [NinjaScriptProperty, Range(0.0, 20.0)]
        [Display(Name = "Cost per trade (points)", Description = "Used only in the on-chart stats box", GroupName = "1. Signal", Order = 6)]
        public double CostPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trade all hours", Description = "OFF = only inside the session window below", GroupName = "2. Session", Order = 0)]
        public bool TradeAllHours { get; set; }

        [NinjaScriptProperty, Range(0, 2359)]
        [Display(Name = "Window start (HHMM, US Eastern)", GroupName = "2. Session", Order = 1)]
        public int SessionStart { get; set; }

        [NinjaScriptProperty, Range(0, 2359)]
        [Display(Name = "Window end (HHMM, US Eastern)", GroupName = "2. Session", Order = 2)]
        public int SessionEnd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show trade lines", GroupName = "3. Display", Order = 0)]
        public bool ShowTradeLines { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show exit marks (check / x)", GroupName = "3. Display", Order = 0)]
        public bool ShowMarkers { get; set; }

        [NinjaScriptProperty, Range(8, 60)]
        [Display(Name = "Exit mark size (pt)", GroupName = "3. Display", Order = 0)]
        public int MarkerSize { get; set; }

        [NinjaScriptProperty, Range(0, 150)]
        [Display(Name = "Exit mark distance from bar (px)", GroupName = "3. Display", Order = 0)]
        public int MarkerDistance { get; set; }

        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "Stop/target line length (bars)", GroupName = "3. Display", Order = 0)]
        public int LineBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show stats box", GroupName = "3. Display", Order = 1)]
        public bool ShowStats { get; set; }

        [XmlIgnore]
        [Display(Name = "Long color", GroupName = "3. Display", Order = 2)]
        public Brush LongBrush { get; set; }
        [Browsable(false)]
        public string LongBrushSerialize { get { return Serialize.BrushToString(LongBrush); } set { LongBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Short color", GroupName = "3. Display", Order = 3)]
        public Brush ShortBrush { get; set; }
        [Browsable(false)]
        public string ShortBrushSerialize { get { return Serialize.BrushToString(ShortBrush); } set { ShortBrush = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Display(Name = "Alert on live signals", GroupName = "4. Alerts", Order = 0)]
        public bool PopupAlerts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert sound file", GroupName = "4. Alerts", Order = 1)]
        public string SoundFile { get; set; }
    }
}
