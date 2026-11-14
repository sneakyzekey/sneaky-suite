#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using SDX = SharpDX;
using D2D = SharpDX.Direct2D1;
using DW  = SharpDX.DirectWrite;
#endregion

// SneakyLivewirePropBuster
// NinjaTrader 8 INDICATOR port of the TradingView strategy "Livewire Prop Buster" (Pine v6, 5-minute NQ / MNQ).
//
// It runs the strategy's rules on every bar close with a built-in virtual broker and shows the trades on the chart
// (entry arrows, stop / target lines, entry-to-exit lines, B / 1 / 2 setup marks, ORB box, key levels, status table).
// It never places an order. Sparse signal plots are provided for scanners / automation (see README).
//
//   1. ORB FADE   - opening range 9:30-9:45 ET. First 5m close outside it = breakout. First opposite-colour candle = leg 1.
//                   Price breaking the leg-1 extreme = leg 2. Then a stop order 2 ticks inside the ORB edge, fading the breakout.
//                   Stop 2 ticks beyond the push, target 1R. One fade per day. (Two other entry methods are available.)
//   2. BROOKS H2 / L2 - pullback leg count (H1 optional) filtered by Trend Magic, PSAR on LSMA and ADX/DMI.
//                   Buy / sell stop 1 tick through the signal candle, stop 1 tick beyond the PSAR dot, target 2R, max 2 a day.
//   3. EVAL BOT   - optional prop-eval layer: account size, consistency rule, daily profit cap, end-of-day trailing drawdown,
//                   target, SLOW (fixed size) / FAST (pace sizing) modes. Eval and daily limits use the SIMULATED P&L.
//
// Differences from the TradingView script
//   * Indicator, not strategy: fills are simulated from the 5-minute bars (stop orders fill at the stop or the open if it gaps;
//     targets fill at the target; when a stop and a target are both inside one bar the stop is assumed first unless you pick
//     "Pine emulator path"). Slippage / commission are inputs. TradingView's broker emulator uses its own intrabar path, so
//     trade-by-trade P&L will differ slightly from TradingView.
//   * No TradersPost JSON / webhook text, no 7/30/90-day stats table.
//   * Trend Magic always uses the chart timeframe.
//   * Key levels: NY open, daily open (18:00 ET), prior day open, prior day NY open. Lines only (no price labels).
//   * Eval defaults to OFF here so the signals match the raw strategy rules (the Pine default is the 50k account).
//   * Needs a 5-minute chart. Chart time zone: set "Hours to add to chart time to get ET" if your PC is not on Eastern time.
//
// Not a recommendation. In my own test on Jan-Oct 2026 NQ data the rules did not show a statistically reliable edge.

namespace NinjaTrader.NinjaScript.Indicators
{
    public enum SneakyLwAccount { Off, A25k, A50k, A100k, A150k, Custom }
    public enum SneakyLwCons { P20, P30, P40, P50, None }
    public enum SneakyLwStrategy { OrbPlusLegs, OrbOnly }
    public enum SneakyLwSize { Recommended, UseContracts }
    public enum SneakyLwSpeed { Slow, Fast }
    public enum SneakyLwTarget { RMultiple, Midline }
    public enum SneakyLwEntry { LegStopOrder, LegCloseInside, CloseInsideNoLeg }
    public enum SneakyLwPullback { OppositeColour, LowerLow, Either }
    public enum SneakyLwLegFilter { TrendMagic, None }
    public enum SneakyLwLegStop { PsarLsma, PullbackExtreme, SignalCandle }
    public enum SneakyLwLegWhen { AnyTime, AfterFadeDone, OnlyNoFadeDays }
    public enum SneakyLwPath { StopFirst, PineEmulator }

    public class SneakyLivewirePropBuster : Indicator
    {
        // ------------------------------------------------------------------ plot indexes (0-based)
        const int P_STOP = 0, P_TGT = 1, P_ORDER = 2, P_TM = 3, P_PSAR = 4, P_ORBH = 5, P_ORBL = 6, P_ORBM = 7,
                  P_BUY = 8, P_SELL = 9, P_FADE = 10, P_LEG = 11, P_EXIT = 12, P_BRK = 13, P_PB = 14, P_LB = 15,
                  P_PENDL = 16, P_PENDS = 17, P_PHASE = 18, P_DONE = 19, P_EVBAL = 20, P_EVST = 21, P_QTY = 22, P_DAYPNL = 23;

        private sealed class Ord { public int Dir, Qty; public double Px, Stop, Tgt; public string Kind; }
        private sealed class Pos { public int Dir, Qty, Bar0; public double Entry, Stop, Tgt, EStop, ETgt, Risk; public string Kind; }

        // ------------------------------------------------------------------ state
        double tick, pv;
        int barMin = 5;
        // indicators
        List<double> trL = new List<double>();
        double mtPrev = double.NaN; bool tmBull, tmBullPrev; double tmLine;
        double lr1 = double.NaN, lr2 = double.NaN, lr0 = double.NaN;
        bool psUp; double psEP = double.NaN, psSAR = double.NaN, psAF, psNext = double.NaN;
        double prevH = double.NaN, prevL = double.NaN, prevC = double.NaN;
        sealed class Rma { public int N, Cnt; public double Sum, Prev = double.NaN, Val = double.NaN;
            public void Add(double v, int n) { N = n; if (double.IsNaN(v)) { Val = double.NaN; return; }
                Cnt++; if (Cnt < n) { Sum += v; Val = double.NaN; } else if (Cnt == n) { Sum += v; Prev = Sum / n; Val = Prev; } else { Prev = (Prev * (n - 1) + v) / n; Val = Prev; } } }
        Rma rTr = new Rma(), rP = new Rma(), rM = new Rma(), rA = new Rma();
        double diP = double.NaN, diM = double.NaN, adxV = double.NaN, fixP = double.NaN, fixM = double.NaN;
        // time
        DateTime tOpenDt; int tOpen, tClose, prevTOpen = -1, prevDay = -1;
        bool newDay;
        int ibStartM, ibEndM, tradeEndM, flatM, legStartM;
        // opening range / prior day
        double ibH = double.NaN, ibL = double.NaN, ibMid = double.NaN; bool ibReady; int orbBar0 = -1;
        double rH = double.NaN, rL = double.NaN, rC = double.NaN, pH = double.NaN, pL = double.NaN, pC = double.NaN;
        bool newsDay, listDay, newsSkip;
        // fade setup
        int brk, phase; double ext = double.NaN, leg1 = double.NaN; bool dayDone; string why = "";
        double stopPx = double.NaN, tgtPx = double.NaN, riskPts = double.NaN; bool beOn; int p3Start;
        // legs
        double lgLeg = double.NaN, sgLeg = double.NaN, lgPbx = double.NaN, sgPbx = double.NaN; bool lgPb, lgArm, sgPb, sgArm; int lgCnt, sgCnt;
        int emaTr, tmGen, tmGenSeen = -1, tmGenCnt; bool fadeTraded;
        // broker
        List<Ord> pend = new List<Ord>(); Pos pos; double netPnl, dayNet0; bool dayLocked;
        double capUpL = double.NaN, capDnS = double.NaN, losDnL = double.NaN, losUpS = double.NaN;
        // eval
        double evNet0 = double.NaN, evPeak, evDayBal, evBest, evBal, evFloor, evEq; bool evPassed, evFailed, evLive, evDone; int evDayN, qtyPace = 1;
        double evTgt, evDD, evPct; int evIdx, ci, qtyEff, qtyUse; double pCap; bool usePC;
        // levels
        double nyOpen = double.NaN, pNyOpen = double.NaN, dOpen = double.NaN, pdOpen = double.NaN; int cmeKey = -1;
        sealed class Lvl { public string Tag; public int Bar0; public double Y; public Brush B; public DashStyleHelper D; public bool Open; }
        List<Lvl> lvlAll = new List<Lvl>(); Lvl curNYO, curDO, curPDO, curPNYO;
        List<string> orbTags = new List<string>(); string curOrbTag; bool orbOpen; double orbDrawH, orbDrawL; int orbDrawStart;
        HashSet<string> skipSet = new HashSet<string>();
        bool badChart;
        // stats
        int nTr, nWin; double sumWin, sumLoss; List<double> dayPnls = new List<double>();

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Livewire Prop Buster: ORB midline fade + Brooks H2/L2 with eval bot (indicator port of the TradingView strategy).";
                Name = "SneakyLivewirePropBuster";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true; IsAutoScale = false; DisplayInDataBox = true; DrawOnPricePanel = true; PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true; MaximumBarsLookBack = MaximumBarsLookBack.Infinite;

                UseOrb = true; UseH1 = false; UseH2 = true;
                UseMaxLoss = false; MaxLossDay = 1500; UseMaxProfit = false; MaxProfitDay = 1200; OwnLimits = false;
                EvalAccount = SneakyLwAccount.Off; EvalCons = SneakyLwCons.P40; EvalStrategyMode = SneakyLwStrategy.OrbPlusLegs; EvalSize = SneakyLwSize.Recommended;
                EvalStart = new DateTime(2026, 10, 1); EvalStopWhenDone = true; EvalSpeed = SneakyLwSpeed.Slow; EvalLimitDays = 21; EvalPace = 0;
                EvalMinQ = 1; EvalMaxQ = 30; EvalCustomTarget = 3000; EvalCustomDD = 2000;
                Contracts = 1; TargetType = SneakyLwTarget.RMultiple; TargetR = 1.0; TargetPct = 100; EntryMethod = SneakyLwEntry.LegStopOrder;
                PullbackDef = SneakyLwPullback.OppositeColour; EntryTicksInside = 2; StopTicksBeyond = 2;
                UseBE = false; BeTriggerR = 1.0; BeTicks = 2; FadeUpShorts = true; FadeDownLongs = true;
                UseTrendFilter = true; TrendZone = 20; UseNewsFilter = true; SkipDates = ""; SkipToday = false;
                EtOffsetHours = 0; IbStartHHMM = 930; IbEndHHMM = 945; LastEntryHHMM = 1500; FlattenHHMM = 1600;
                LegFilter = SneakyLwLegFilter.TrendMagic; LegPerTrend = 3; LegStopMode = SneakyLwLegStop.PsarLsma; LegR = 2.0; LegMaxStopPts = 80;
                LegMaxPerDay = 2; LegStartHHMM = 945; LegWhen = SneakyLwLegWhen.AnyTime;
                PsarFilter = true; UseAdx = true; AdxLevel = 25; AdxLen = 14; PsarLen = 10; PsarStart = 0.02; PsarInc = 0.02; PsarMax = 0.12;
                CciPeriod = 20; TmAtrMult = 1.0; TmAtrPeriod = 3;
                SlippageTicks = 1; CommissionPerContract = 2.40; IntrabarPath = SneakyLwPath.StopFirst;
                ShowTable = true; ShowMarks = true; ShowTrades = true; ShowOrb = true; OrbDays = 80; ShowTrendMagic = true; ShowPsar = true;
                ShowNYO = true; ShowDO = true; ShowPDO = true; ShowPNYO = false; LevelDays = 40; AlertOnTrades = false;
                OrbBrush = Brushes.Teal; TmBullBrush = Brushes.Cyan; TmBearBrush = Brushes.Gold; NyoBrush = Brushes.DodgerBlue; DoBrush = Brushes.MediumPurple; PdoBrush = Brushes.Gray;

                AddPlot(new Stroke(Brushes.Red, 2),         PlotStyle.Line, "Stop");          // 1
                AddPlot(new Stroke(Brushes.LimeGreen, 2),   PlotStyle.Line, "Target");        // 2
                AddPlot(new Stroke(Brushes.DodgerBlue, 2),  PlotStyle.Line, "EntryOrder");    // 3 pending stop-order level for the next bar
                AddPlot(new Stroke(Brushes.Cyan, 2),        PlotStyle.Line, "TrendMagic");    // 4
                AddPlot(new Stroke(Brushes.Gray, 1),        PlotStyle.Dot,  "PsarLsma");      // 5
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "OrbHigh");       // 6
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "OrbLow");        // 7
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "OrbMid");        // 8
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "BuySignal");     // 9  +1 on a bar where a long (any kind) was filled
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SellSignal");    // 10 -1 on a bar where a short was filled
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "FadeFill");      // 11 +1 long / -1 short, ORB fade
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "LegFill");       // 12 +1 long / -1 short, H / L entry
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "ExitSignal");    // 13 +1 a long exited / -1 a short exited
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Breakout");      // 14 +1 breakout up, -1 breakout down (B)
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Pullback");      // 15 first pullback found (1), sign = breakout direction
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "LegBreak");      // 16 leg 1 broken (2), sign = breakout direction
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "PendingBuyStop");  // 17 price of a buy stop to place for the next bar
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "PendingSellStop"); // 18
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "FadePhase");     // 19 0 wait breakout,1 wait pullback,2 wait leg break,3 armed
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "DayDone");       // 20 1 when no more entries today
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "EvalBalance");   // 21 simulated eval P&L ($)
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "EvalState");     // 22 0 off, 1 live, 2 passed, 3 failed
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Contracts");     // 23 size in use
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "DayPnl");        // 24 simulated day P&L ($)
            }
            else if (State == State.Configure)
            {
            }
            else if (State == State.Terminated) { Kill(ref dxBack); Kill(ref dxText); }
            else if (State == State.DataLoaded)
            {
                tick = Instrument.MasterInstrument.TickSize; pv = Instrument.MasterInstrument.PointValue;
                badChart = !(BarsPeriod.BarsPeriodType == BarsPeriodType.Minute && BarsPeriod.Value == 5);
                barMin = BarsPeriod.Value > 0 ? BarsPeriod.Value : 5;
                ibStartM = Mins(IbStartHHMM); ibEndM = Mins(IbEndHHMM); tradeEndM = Mins(LastEntryHHMM); flatM = Mins(FlattenHHMM); legStartM = Mins(LegStartHHMM);
                psAF = PsarStart;
                skipSet.Clear();
                if (!string.IsNullOrEmpty(SkipDates)) foreach (string s in SkipDates.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)) skipSet.Add(s.Trim());
                evIdx = EvalAccount == SneakyLwAccount.A25k ? 0 : EvalAccount == SneakyLwAccount.A50k ? 1 : EvalAccount == SneakyLwAccount.A100k ? 2 : EvalAccount == SneakyLwAccount.A150k ? 3 : -1;
                evTgt = evIdx == 0 ? 1500 : evIdx == 1 ? 3000 : evIdx == 2 ? 6000 : evIdx == 3 ? 9000 : EvalCustomTarget;
                evDD = evIdx == 0 ? 1500 : evIdx == 1 ? 2000 : evIdx == 2 ? 3000 : evIdx == 3 ? 4500 : EvalCustomDD;
                evPct = EvalCons == SneakyLwCons.P20 ? 0.20 : EvalCons == SneakyLwCons.P30 ? 0.30 : EvalCons == SneakyLwCons.P40 ? 0.40 : EvalCons == SneakyLwCons.P50 ? 0.50 : double.NaN;
                ci = EvalCons == SneakyLwCons.P20 ? 0 : EvalCons == SneakyLwCons.P30 ? 1 : EvalCons == SneakyLwCons.P40 ? 2 : 3;
                qtyEff = (EvalAccount != SneakyLwAccount.Off && EvalSize == SneakyLwSize.Recommended && evIdx >= 0) ? RecQty(EvalStrategyMode == SneakyLwStrategy.OrbOnly, evIdx, ci) : Contracts;
            }
        }

        static int Mins(int hhmm) { return (hhmm / 100) * 60 + hhmm % 100; }
        double Rnd(double x) { return Math.Round(x / tick) * tick; }
        bool EvOn { get { return EvalAccount != SneakyLwAccount.Off; } }
        bool UseOrbE { get { return EvOn ? true : UseOrb; } }
        bool UseLegs { get { return EvOn ? (EvalStrategyMode != SneakyLwStrategy.OrbOnly && (UseH1 || UseH2)) : (UseH1 || UseH2); } }

        static int RecQty(bool orbOnly, int acct, int c)
        {
            if (orbOnly) return acct == 0 ? 2 : acct == 1 ? (c == 0 ? 3 : 6) : acct == 2 ? (c == 0 ? 8 : c == 1 ? 10 : 15) : acct == 3 ? (c == 0 ? 12 : 15) : 2;
            return acct == 0 ? (c <= 1 ? 1 : c == 2 ? 2 : 3) : acct == 1 ? (c == 0 ? 3 : 4) : acct == 2 ? (c == 0 ? 6 : 7) : acct == 3 ? 8 : 2;
        }

        static double Sma(List<double> v, int end, int n) { double s = 0; for (int k = 0; k < n; k++) s += v[end - k]; return s / n; }

        // ------------------------------------------------------------------ indicators (same maths as the Pine script / my Python port)
        void CalcIndicators()
        {
            int i = CurrentBar; double h = High[0], l = Low[0], c = Close[0];
            double tr = i == 0 ? h - l : Math.Max(h - l, Math.Max(Math.Abs(h - prevC), Math.Abs(l - prevC)));
            trL.Add(tr);
            // Trend Magic
            double atr = trL.Count >= TmAtrPeriod ? Sma(trL, trL.Count - 1, TmAtrPeriod) : double.NaN;
            double upT = l - atr * TmAtrMult, dnT = h + atr * TmAtrMult, cci = double.NaN;
            if (i >= CciPeriod - 1)
            {
                double ma = 0; for (int k = 0; k < CciPeriod; k++) ma += Close[k]; ma /= CciPeriod;
                double dev = 0; for (int k = 0; k < CciPeriod; k++) dev += Math.Abs(Close[k] - ma); dev /= CciPeriod;
                cci = dev > 0 ? (c - ma) / (0.015 * dev) : double.NaN;
            }
            double nzPrev = double.IsNaN(mtPrev) ? 0.0 : mtPrev;
            double mt;
            if (!double.IsNaN(cci) && cci >= 0) mt = (upT < nzPrev) ? nzPrev : upT; else mt = (dnT > nzPrev) ? nzPrev : dnT;
            mtPrev = mt; tmLine = mt; tmBullPrev = tmBull; tmBull = !double.IsNaN(cci) && cci >= 0;
            // linreg(close, PsarLen, 0)
            lr2 = lr1; lr1 = lr0; lr0 = double.NaN;
            int L = PsarLen;
            if (i >= L - 1)
            {
                double sx = L * (L - 1) / 2.0, sxx = (L - 1.0) * L * (2.0 * L - 1.0) / 6.0, sy = 0, sxy = 0;
                for (int k = 0; k < L; k++) { double y = Close[L - 1 - k]; sy += y; sxy += k * y; }
                double slope = (L * sxy - sx * sy) / (L * sxx - sx * sx), icpt = (sy - slope * sx) / L;
                lr0 = icpt + slope * (L - 1);
            }
            // PSAR on the regression line
            if (i > L)
            {
                bool first = false; psSAR = psNext;
                if (i == L + 1) { double pS = lr1, pE = lr0; psUp = lr0 > lr1; psEP = lr0; first = true; psSAR = pS + PsarStart * (pE - pS); }
                if (psUp) { if (psSAR > lr0) { first = true; psUp = false; psSAR = Math.Max(psEP, lr0); psEP = lr0; psAF = PsarStart; } }
                else { if (psSAR < lr0) { first = true; psUp = true; psSAR = Math.Min(psEP, lr0); psEP = lr0; psAF = PsarStart; } }
                if (!first)
                {
                    if (psUp && lr0 > psEP) { psEP = lr0; psAF = Math.Min(psAF + PsarInc, PsarMax); }
                    else if (!psUp && lr0 < psEP) { psEP = lr0; psAF = Math.Min(psAF + PsarInc, PsarMax); }
                }
                if (psUp) { psSAR = Math.Min(psSAR, lr1); psSAR = Math.Min(psSAR, lr2); }
                else { psSAR = Math.Max(psSAR, lr1); psSAR = Math.Max(psSAR, lr2); }
                psNext = psSAR + psAF * (psEP - psSAR);
            }
            // DMI / ADX
            double pdm = double.NaN, mdm = double.NaN;
            if (i > 0) { double up = h - prevH, dn = -(l - prevL); pdm = (up > dn && up > 0) ? up : 0.0; mdm = (dn > up && dn > 0) ? dn : 0.0; }
            rTr.Add(tr, AdxLen); rP.Add(pdm, AdxLen); rM.Add(mdm, AdxLen);
            double p = 100.0 * rP.Val / rTr.Val, m = 100.0 * rM.Val / rTr.Val;
            if (!double.IsNaN(p)) fixP = p; if (!double.IsNaN(m)) fixM = m;
            diP = fixP; diM = fixM;
            double ad = double.NaN;
            if (!double.IsNaN(diP) && !double.IsNaN(diM)) { double s = diP + diM; ad = Math.Abs(diP - diM) / (s == 0 ? 1 : s); }
            rA.Add(ad, AdxLen); adxV = 100.0 * rA.Val;
            prevH = h; prevL = l; prevC = c;
        }

        // ------------------------------------------------------------------ helpers
        double OpenProfit() { return pos == null ? 0.0 : pos.Dir * (Close[0] - pos.Entry) * pv * pos.Qty; }

        void ClosePos(double exitPx, string why2, int tag)
        {
            double gross = pos.Dir * (exitPx - pos.Entry) * pv * pos.Qty;
            double pnl = gross - 2.0 * CommissionPerContract * pos.Qty;
            netPnl += pnl; nTr++; if (pnl > 0) { nWin++; sumWin += pnl; } else sumLoss += -pnl;
            Values[P_EXIT][0] = pos.Dir;
            if (TradeLog != null) TradeLog("X," + Time[0].ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "," + pos.Dir + "," + pos.Kind + "," + exitPx.ToString("R", CultureInfo.InvariantCulture) + "," + why2 + "," + pnl.ToString("R", CultureInfo.InvariantCulture) + "," + pos.Entry.ToString("R", CultureInfo.InvariantCulture) + "," + pos.Qty);
            if (ShowTrades)
            {
                Brush b = pnl >= 0 ? Brushes.LimeGreen : Brushes.Red;
                Draw.Line(this, "lw_tr" + CurrentBar, false, CurrentBar - pos.Bar0, pos.Entry, 0, exitPx, b, DashStyleHelper.Dash, 2);
                Draw.Text(this, "lw_x" + pos.Kind + CurrentBar, false, why2, 0, pos.Dir > 0 ? exitPx - 18 * tick : exitPx + 18 * tick, 0, b, new SimpleFont("Arial", 9), System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            }
            if (AlertOnTrades && State == State.Realtime) Alert("lw_exit" + CurrentBar, Priority.High, "Livewire " + pos.Kind + (pos.Dir > 0 ? " long" : " short") + " exit " + why2 + " " + exitPx.ToString("0.00", CultureInfo.InvariantCulture), "Alert2.wav", 10, Brushes.Black, Brushes.White);
            pos = null;
        }

        double Slip { get { return SlippageTicks * tick; } }

        // returns exit price or NaN. fillBar: the bar on which the entry just filled.
        double CheckExit(bool fillBar, out string why2)
        {
            double o = Open[0], h = High[0], l = Low[0]; why2 = "";
            int dr = pos.Dir; double stop = pos.EStop, tgt = pos.ETgt;
            bool stopHit, tgtHit;
            double ex = double.NaN;
            if (fillBar)
            {
                bool hiFirst = (h - o) < (o - l);
                if (dr == 1)
                {
                    if (hiFirst) { if (h >= tgt) { ex = tgt; why2 = "TP"; } else if (l <= stop) { ex = stop - Slip; why2 = "SL"; } }
                    else { if (h >= tgt) { ex = tgt; why2 = "TP"; } }
                }
                else
                {
                    if (!hiFirst) { if (l <= tgt) { ex = tgt; why2 = "TP"; } else if (h >= stop) { ex = stop + Slip; why2 = "SL"; } }
                    else { if (l <= tgt) { ex = tgt; why2 = "TP"; } }
                }
                return ex;
            }
            if (dr == 1) { stopHit = l <= stop; tgtHit = h >= tgt; } else { stopHit = h >= stop; tgtHit = l <= tgt; }
            if (stopHit && tgtHit && IntrabarPath == SneakyLwPath.PineEmulator)
            {
                bool hiFirst = (h - o) < (o - l);
                bool tgtFirst = dr == 1 ? hiFirst : !hiFirst;      // long: O,H,L reaches the target first; short: O,L,H
                if (tgtFirst) stopHit = false; else tgtHit = false;
            }
            if (stopHit) { ex = dr == 1 ? Math.Min(stop, o) - Slip : Math.Max(stop, o) + Slip; why2 = pos.EStop == pos.Stop ? (beOn && pos.Kind == "Fade" ? "BE" : "SL") : "Limit"; }
            else if (tgtHit) { ex = tgt; why2 = pos.ETgt == pos.Tgt ? "TP" : "Cap"; }
            return ex;
        }

        void OpenPos(Ord od, double fillPx, bool atClose)
        {
            double fp = od.Dir == 1 ? fillPx + Slip : fillPx - Slip;
            pos = new Pos { Dir = od.Dir, Qty = od.Qty, Entry = fp, Stop = od.Stop, Tgt = od.Tgt, EStop = od.Stop, ETgt = od.Tgt, Kind = od.Kind, Bar0 = CurrentBar, Risk = Math.Abs(fp - od.Stop) };
            if (TradeLog != null) TradeLog("E," + Time[0].ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "," + od.Dir + "," + od.Kind + "," + fp.ToString("R", CultureInfo.InvariantCulture) + "," + od.Qty);
            Values[od.Dir == 1 ? P_BUY : P_SELL][0] = od.Dir;
            Values[od.Kind == "Fade" ? P_FADE : P_LEG][0] = od.Dir;
            if (ShowTrades)
            {
                string tg = "lw_" + od.Kind + (od.Dir == 1 ? "Buy" : "Sell") + CurrentBar;
                if (od.Dir == 1) Draw.ArrowUp(this, tg, false, 0, Low[0] - 4 * tick, od.Kind == "Fade" ? Brushes.DodgerBlue : Brushes.LimeGreen);
                else Draw.ArrowDown(this, tg, false, 0, High[0] + 4 * tick, od.Kind == "Fade" ? Brushes.DodgerBlue : Brushes.Red);
                Draw.Text(this, "lw_t" + od.Kind + CurrentBar, false, (od.Kind == "Fade" ? "F" : (od.Dir == 1 ? "H" : "L")) + " x" + od.Qty, 0, od.Dir == 1 ? Low[0] - 12 * tick : High[0] + 12 * tick, 0, Brushes.White, new SimpleFont("Arial", 9), System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            }
            if (AlertOnTrades && State == State.Realtime) Alert("lw_entry" + CurrentBar, Priority.High, "Livewire " + od.Kind + (od.Dir > 0 ? " BUY " : " SELL ") + fp.ToString("0.00", CultureInfo.InvariantCulture) + " x" + od.Qty, "Alert2.wav", 10, Brushes.Black, Brushes.White);
        }

        void Mark(string tag, string txt, int dir, bool above, Brush b)
        {
            if (!ShowMarks) return;
            Draw.Text(this, tag + CurrentBar, false, txt, 0, above ? High[0] + 8 * tick : Low[0] - 8 * tick, 0, b, new SimpleFont("Arial", 9), System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
        }

        bool CalcNews(DateTime t)
        {
            int y = t.Year, m = t.Month, d = t.Day;
            int bd = 0; for (int k = 1; k <= d; k++) { DayOfWeek w = new DateTime(y, m, k).DayOfWeek; if (w != DayOfWeek.Saturday && w != DayOfWeek.Sunday) bd++; }
            int dim = DateTime.DaysInMonth(y, m);
            bool wk = t.DayOfWeek != DayOfWeek.Saturday && t.DayOfWeek != DayOfWeek.Sunday; bool tue = t.DayOfWeek == DayOfWeek.Tuesday;
            return wk && (bd == 1 || bd == 3 || (tue && d <= 7) || (tue && d + 7 > dim));
        }

        // ------------------------------------------------------------------ main
        protected override void OnBarUpdate()
        {
            if (badChart) { if (CurrentBar == Bars.Count - 1) Draw.TextFixed(this, "lw_bad", "Livewire Prop Buster needs a 5-minute chart", TextPosition.TopRight, Brushes.Orange, new SimpleFont("Arial", 12), Brushes.Transparent, Brushes.Transparent, 0); return; }
            int i = CurrentBar;
            for (int k = P_BUY; k <= P_LB + 0; k++) Values[k].Reset();
            Values[P_STOP].Reset(); Values[P_TGT].Reset(); Values[P_ORDER].Reset(); Values[P_PENDL].Reset(); Values[P_PENDS].Reset();
            Values[P_ORBH].Reset(); Values[P_ORBL].Reset(); Values[P_ORBM].Reset();

            // ---- time (chart time + offset = ET). NT8 time stamps are bar END times.
            tOpenDt = Time[0].AddMinutes(-barMin).AddHours(EtOffsetHours);
            tOpen = tOpenDt.Hour * 60 + tOpenDt.Minute; tClose = tOpen + barMin;
            int dayKey = tOpenDt.Year * 10000 + tOpenDt.Month * 100 + tOpenDt.Day;
            newDay = i == 0 || dayKey != prevDay;
            CalcIndicators();
            double o = Open[0], h = High[0], l = Low[0], c = Close[0];
            double hPrev = i > 0 ? High[1] : double.NaN, lPrev = i > 0 ? Low[1] : double.NaN;

            // ================= A. broker: orders placed at the previous close meet this bar =================
            Pos filled = null; string newFillKind = null;
            if (pos != null && newDay)
            {   // safety: never carry a position across a day change (holiday early close etc.)
                ClosePos(prevC0 - pos.Dir * Slip, "Session end", i);
            }
            if (pos == null && pend.Count > 0)
            {
                double bestKey = double.MaxValue; Ord best = null; double bestFp = 0;
                foreach (Ord od in pend)
                {
                    if (od.Dir == 1 && h >= od.Px) { double key = o < od.Px ? Math.Abs(od.Px - o) : 0; if (key < bestKey) { bestKey = key; best = od; bestFp = Math.Max(o, od.Px); } }
                    if (od.Dir == -1 && l <= od.Px) { double key = o > od.Px ? Math.Abs(od.Px - o) : 0; if (key < bestKey) { bestKey = key; best = od; bestFp = Math.Min(o, od.Px); } }
                }
                if (best != null) { OpenPos(best, bestFp, false); filled = pos; newFillKind = best.Kind; }
            }
            pend.Clear();
            double exitedPx = double.NaN; bool exitedNow = false;
            if (pos != null)
            {
                string w2; double ex = CheckExit(filled != null, out w2);
                if (double.IsNaN(ex) && tClose == flatM) { ex = c - pos.Dir * Slip; w2 = "EOD"; }
                if (!double.IsNaN(ex)) { ClosePos(ex, w2, i); exitedNow = true; exitedPx = ex; }
            }
            if (pos != null) { pos.EStop = pos.Stop; pos.ETgt = pos.Tgt; }

            // ================= B. script logic at this bar's close =================
            // ---- new day resets
            if (newDay)
            {
                if (!double.IsNaN(rH)) { pH = rH; pL = rL; pC = rC; }
                rH = rL = rC = double.NaN; ibH = ibL = double.NaN; ibReady = false; orbBar0 = -1;
                brk = 0; phase = 0; ext = double.NaN; leg1 = double.NaN; dayDone = !UseOrbE; why = UseOrbE ? "waiting for ORB" : "ORB fade OFF"; beOn = false;
                emaTr = 0; fadeTraded = false;
                newsDay = CalcNews(tOpenDt);
                listDay = skipSet.Contains(tOpenDt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                dayNet0 = netPnl; dayLocked = false;
                if (orbOpen) FinishOrb();
            }
            newsSkip = (UseNewsFilter && newsDay) || listDay;
            if (SkipToday && i == Bars.Count - 1 && tOpenDt.Date == DateTime.Now.AddHours(EtOffsetHours).Date) newsSkip = true;
            if (tOpen >= ibStartM && tOpen < ibEndM)
            {
                ibH = double.IsNaN(ibH) ? h : Math.Max(ibH, h); ibL = double.IsNaN(ibL) ? l : Math.Min(ibL, l);
                if (orbBar0 < 0) orbBar0 = i;
            }
            if (tClose >= ibEndM && tOpen < ibEndM && !double.IsNaN(ibH)) { if (!ibReady) StartOrb(); ibReady = true; }
            ibMid = ibReady ? (ibH + ibL) / 2.0 : double.NaN;
            if (tOpen >= ibStartM && tOpen < flatM) { rH = double.IsNaN(rH) ? h : Math.Max(rH, h); rL = double.IsNaN(rL) ? l : Math.Min(rL, l); rC = c; }
            double pPct = (!double.IsNaN(pH) && pH - pL > 0) ? (pC - pL) / (pH - pL) * 100.0 : double.NaN;
            bool pTrUp = !double.IsNaN(pPct) && pPct >= 100 - TrendZone, pTrDn = !double.IsNaN(pPct) && pPct <= TrendZone;
            if (tOpen == ibStartM && prevTOpen != ibStartM) { pNyOpen = nyOpen; nyOpen = o; NyoStart(); }
            NewCmeCheck(dayKey);

            // ---- trend magic generation + fill bookkeeping (Pine order: tm gen, fill counters, gen reset)
            if (i > 0 && tmBull != tmBullPrev) tmGen++;
            if (newFillKind != null)
            {
                if (newFillKind == "Fade") dayDone = true;   // a FADE fill uses up the fade for the day
                if (newFillKind == "Leg") { emaTr++; tmGenCnt++; } else fadeTraded = true;
            }
            if (tmGenSeen < 0 || tmGen != tmGenSeen) { tmGenSeen = tmGen; tmGenCnt = 0; }

            // ---- eval tracking
            evLive = EvOn && tOpenDt >= EvalStart;
            if (evLive && double.IsNaN(evNet0)) evNet0 = netPnl;
            evBal = evLive ? netPnl - evNet0 : 0.0;
            if (evLive && newDay && !double.IsNaN(evNet0)) { evBest = Math.Max(evBest, evBal - evDayBal); evPeak = Math.Max(evPeak, evBal); evDayBal = evBal; }
            evFloor = Math.Min(evPeak - evDD, 0.0);
            evEq = evBal + OpenProfit();
            if (evLive && !evPassed && !evFailed) { if (evBal >= evTgt) evPassed = true; else if (evEq <= evFloor) evFailed = true; }
            evDone = EvalStopWhenDone && (evPassed || evFailed);
            double paceUse = EvalPace > 0 ? EvalPace : (double.IsNaN(evPct) ? 5.0 : 10.0);
            if (evLive && tOpen == ibStartM && prevTOpen != ibStartM)
            {
                evDayN++; int left = Math.Max(1, EvalLimitDays - evDayN + 1); double need = Math.Max(0.0, evTgt - evBal);
                qtyPace = (int)Math.Min(EvalMaxQ, Math.Max(EvalMinQ, Math.Ceiling(need / (left * paceUse))));
            }
            bool usePace = evLive && EvalSpeed == SneakyLwSpeed.Fast;
            qtyUse = usePace ? qtyPace : qtyEff;
            double consCap = double.IsNaN(evPct) ? double.NaN : 0.9 * evPct * evTgt;
            double tgtLeft = evTgt - evDayBal;
            double pCapEv = double.IsNaN(consCap) ? tgtLeft : Math.Min(consCap, tgtLeft);
            usePC = evLive ? true : UseMaxProfit;
            pCap = evLive ? (OwnLimits ? (UseMaxProfit ? Math.Min(MaxProfitDay, tgtLeft) : tgtLeft) : pCapEv) : MaxProfitDay;
            if (evDone && pos != null) ClosePos(c - pos.Dir * Slip, evPassed ? "Eval target" : "Eval drawdown", i);

            // ---- daily max loss / profit (whole strategy)
            double dayPnl = netPnl + OpenProfit() - dayNet0;
            if (!dayLocked && ((UseMaxLoss && dayPnl <= -MaxLossDay) || (usePC && dayPnl >= pCap)))
            {
                dayLocked = true; dayDone = true; why = "STOPPED for the day - daily " + (dayPnl < 0 ? "max loss" : "max profit") + " reached";
                pend.Clear();
                if (pos != null) ClosePos(c - pos.Dir * Slip, "Daily limit", i);
            }
            if (dayLocked || evDone) dayDone = true;

            bool flat = pos == null;
            bool inWindow = ibReady && tOpen >= ibEndM && tClose <= tradeEndM;
            bool legMode = EntryMethod != SneakyLwEntry.CloseInsideNoLeg, stopMode = EntryMethod == SneakyLwEntry.LegStopOrder;
            bool brkBar = false, pbBar = false, legBrkBar = false, enterS = false, enterL = false;

            // ---- fade setup tracking (keeps tracking while an H/L trade is open)
            if (inWindow && !dayDone)
            {
                if (phase == 0)
                {
                    if (c > ibH) { brk = 1; ext = h; } else if (c < ibL) { brk = -1; ext = l; }
                    if (brk != 0)
                    {
                        brkBar = true; phase = legMode ? 1 : 3; p3Start = i;
                        bool trendSk = UseTrendFilter && ((brk == 1 && pTrUp) || (brk == -1 && pTrDn));
                        if (trendSk) { dayDone = true; why = "SKIPPED - prior day closed trending in the breakout direction"; }
                        else if (newsSkip) { dayDone = true; why = "SKIPPED - news / skip day"; }
                        else if ((brk == 1 && !FadeUpShorts) || (brk == -1 && !FadeDownLongs)) { dayDone = true; why = "direction switched off"; }
                        else why = "breakout " + (brk == 1 ? "UP" : "DOWN") + (legMode ? " - waiting for the first pullback candle" : " - waiting for a 5m close back inside");
                    }
                }
                else
                {
                    bool midHit = brk == 1 ? l <= ibMid : h >= ibMid;
                    if (phase == 1)
                    {
                        ext = brk == 1 ? Math.Max(ext, h) : Math.Min(ext, l);
                        bool pbCol = brk == 1 ? c < o : c > o;
                        bool pbLL = i > 0 && (brk == 1 ? l < lPrev : h > hPrev);
                        bool isPb = PullbackDef == SneakyLwPullback.OppositeColour ? pbCol : PullbackDef == SneakyLwPullback.Either ? (pbCol || pbLL) : pbLL;
                        if (isPb) { leg1 = ext; phase = 2; pbBar = true; why = "pullback found - waiting for price to break the leg-1 " + (brk == 1 ? "high " : "low ") + leg1.ToString("0.00", CultureInfo.InvariantCulture); }
                    }
                    else if (phase == 2)
                    {
                        if (midHit) { dayDone = true; why = "reached the midline before breaking leg 1 - no trade"; }
                        else if ((brk == 1 && h > leg1) || (brk == -1 && l < leg1))
                        {
                            ext = brk == 1 ? Math.Max(ext, h) : Math.Min(ext, l); phase = 3; p3Start = i; legBrkBar = true;
                            why = stopMode ? "leg 1 broken - entry order " + EntryTicksInside + " ticks inside the ORB" : "leg 1 broken - waiting for a 5m close back inside";
                        }
                    }
                    else if (phase == 3) ext = brk == 1 ? Math.Max(ext, h) : Math.Min(ext, l);
                    if (phase == 3 && !stopMode && !dayDone)
                    {
                        bool firstBar = i == p3Start + (legMode ? 0 : 1);
                        if (midHit && !firstBar) { dayDone = true; why = "reached the midline before a close back inside - no trade"; }
                        else
                        {
                            bool backIn = brk == 1 ? c < ibH : c > ibL;
                            if (backIn)
                            {
                                bool pastMid = brk == 1 ? c <= ibMid : c >= ibMid;
                                dayDone = true;
                                if (pastMid) why = "closed back inside but already past the midline - no trade";
                                else if (!flat) why = "fade signal skipped - another trade was open";
                                else if (brk == 1) enterS = true; else enterL = true;
                            }
                        }
                    }
                }
            }
            if (ibReady && tClose > tradeEndM && !dayDone && flat) { dayDone = true; why = brk == 0 ? "no breakout today" : "no entry before the cut-off"; }
            if (brkBar) { Values[P_BRK][0] = brk; Mark("lw_B", "B", brk, brk == 1, Brushes.Orange); }
            if (pbBar) { Values[P_PB][0] = brk; Mark("lw_1", "1", brk, brk != 1, Brushes.MediumOrchid); }
            if (legBrkBar) { Values[P_LB][0] = brk; Mark("lw_2", "2", brk, brk == 1, brk == 1 ? Brushes.Red : Brushes.LimeGreen); }

            // ---- (a) market entry at the close of the entry candle
            if (enterS || enterL)
            {
                int dr = enterS ? -1 : 1;
                stopPx = enterS ? ext + StopTicksBeyond * tick : ext - StopTicksBeyond * tick;
                riskPts = enterS ? stopPx - c : c - stopPx;
                if (TargetType == SneakyLwTarget.RMultiple) tgtPx = Rnd(c + dr * riskPts * TargetR);
                else tgtPx = enterS ? Rnd(c - (c - ibMid) * TargetPct / 100.0) : Rnd(c + (ibMid - c) * TargetPct / 100.0);
                beOn = false;
                Ord mo = new Ord { Dir = dr, Qty = qtyUse, Px = c, Stop = stopPx, Tgt = tgtPx, Kind = "Fade" };
                OpenPos(mo, c, true); filled = pos; why = (enterS ? "SHORT at " : "LONG at ") + c.ToString("0.00", CultureInfo.InvariantCulture);
                fadeTraded = true; flat = false;
            }
            // ---- (b) leg-count stop order at the ORB edge, live from the candle after the leg-1 break
            bool pendStop = stopMode && phase == 3 && !dayDone && flat && inWindow;
            if (pendStop)
            {
                if (brk == 1)
                {
                    double lvlS = ibH - EntryTicksInside * tick; stopPx = ext + StopTicksBeyond * tick; riskPts = stopPx - lvlS;
                    tgtPx = TargetType == SneakyLwTarget.RMultiple ? Rnd(lvlS - riskPts * TargetR) : Rnd(lvlS - (lvlS - ibMid) * TargetPct / 100.0); beOn = false;
                    pend.Add(new Ord { Dir = -1, Px = lvlS, Stop = stopPx, Tgt = tgtPx, Kind = "Fade", Qty = qtyUse });
                    Values[P_ORDER][0] = lvlS; Values[P_PENDS][0] = lvlS;
                }
                else if (brk == -1)
                {
                    double lvlL = ibL + EntryTicksInside * tick; stopPx = ext - StopTicksBeyond * tick; riskPts = lvlL - stopPx;
                    tgtPx = TargetType == SneakyLwTarget.RMultiple ? Rnd(lvlL + riskPts * TargetR) : Rnd(lvlL + (ibMid - lvlL) * TargetPct / 100.0); beOn = false;
                    pend.Add(new Ord { Dir = 1, Px = lvlL, Stop = stopPx, Tgt = tgtPx, Kind = "Fade", Qty = qtyUse });
                    Values[P_ORDER][0] = lvlL; Values[P_PENDL][0] = lvlL;
                }
            }
            // ---- on the candle a stop-order fade fills: include that candle's extreme in the stop and re-base the target
            if (stopMode && filled != null && pos != null && pos.Kind == "Fade" && newFillKind == "Fade")
            {
                double fp = pos.Entry;
                if (pos.Dir < 0) { ext = Math.Max(ext, h); stopPx = ext + StopTicksBeyond * tick; riskPts = stopPx - fp; tgtPx = TargetType == SneakyLwTarget.RMultiple ? Rnd(fp - riskPts * TargetR) : Rnd(fp - (fp - ibMid) * TargetPct / 100.0); why = "SHORT at " + fp.ToString("0.00", CultureInfo.InvariantCulture); }
                else { ext = Math.Min(ext, l); stopPx = ext - StopTicksBeyond * tick; riskPts = fp - stopPx; tgtPx = TargetType == SneakyLwTarget.RMultiple ? Rnd(fp + riskPts * TargetR) : Rnd(fp + (ibMid - fp) * TargetPct / 100.0); why = "LONG at " + fp.ToString("0.00", CultureInfo.InvariantCulture); }
                pos.Stop = stopPx; pos.Tgt = tgtPx; pos.Risk = riskPts;
            }

            // ---- Brooks leg count
            bool rthBar = tOpen >= ibStartM && tOpen < flatM;
            if (tOpen == ibStartM)
            {
                lgLeg = h; sgLeg = l; lgPb = false; lgArm = false; lgCnt = 0; sgPb = false; sgArm = false; sgCnt = 0;
            }
            else if (rthBar && !double.IsNaN(lgLeg) && i > 0)
            {
                if (lgPb && lgArm && h >= hPrev + tick) { lgCnt++; lgArm = false; }
                if (h > lgLeg) { lgLeg = h; lgPb = false; lgCnt = 0; }
                if (h < hPrev) { if (!lgPb) { lgPb = true; lgCnt = 0; lgPbx = l; } lgArm = true; }
                if (lgPb) lgPbx = Math.Min(lgPbx, l);
                if (sgPb && sgArm && l <= lPrev - tick) { sgCnt++; sgArm = false; }
                if (l < sgLeg) { sgLeg = l; sgPb = false; sgCnt = 0; }
                if (l > lPrev) { if (!sgPb) { sgPb = true; sgCnt = 0; sgPbx = h; } sgArm = true; }
                if (sgPb) sgPbx = Math.Max(sgPbx, h);
            }
            bool fadeOver = LegWhen == SneakyLwLegWhen.AnyTime ? true : LegWhen == SneakyLwLegWhen.OnlyNoFadeDays ? ((dayDone || newsSkip) && !fadeTraded) : (dayDone || newsSkip);
            bool legWin = tClose >= Math.Max(ibEndM, legStartM) && tClose <= tradeEndM - 5;
            bool legOK = UseLegs && !dayLocked && !evDone && fadeOver && legWin && pos == null && emaTr < LegMaxPerDay && rthBar;
            bool cntOKL = lgPb && lgArm && ((UseH1 && lgCnt + 1 == 1) || (UseH2 && lgCnt + 1 == 2));
            bool cntOKS = sgPb && sgArm && ((UseH1 && sgCnt + 1 == 1) || (UseH2 && sgCnt + 1 == 2));
            bool adxL = !UseAdx || (adxV > AdxLevel && diP > diM), adxS = !UseAdx || (adxV > AdxLevel && diM > diP);
            bool trendL = (LegFilter == SneakyLwLegFilter.None ? true : tmBull) && (!PsarFilter || psUp) && adxL;
            bool trendS = (LegFilter == SneakyLwLegFilter.None ? true : !tmBull) && (!PsarFilter || !psUp) && adxS;
            bool perTrOK = LegPerTrend == 0 || tmGenCnt < LegPerTrend;
            bool psMode = LegStopMode == SneakyLwLegStop.PsarLsma, havePs = !double.IsNaN(psSAR);
            double slCandL = psMode ? psSAR - tick : LegStopMode == SneakyLwLegStop.PullbackExtreme ? lgPbx - tick : l - tick;
            double slCandS = psMode ? psSAR + tick : LegStopMode == SneakyLwLegStop.PullbackExtreme ? sgPbx + tick : h + tick;
            bool psOKL = !psMode || (havePs && psSAR - tick < h + tick), psOKS = !psMode || (havePs && psSAR + tick > l - tick);
            bool slOKL = LegMaxStopPts == 0 || (h + tick - slCandL) <= LegMaxStopPts, slOKS = LegMaxStopPts == 0 || (slCandS - (l - tick)) <= LegMaxStopPts;
            bool goL = legOK && cntOKL && trendL && perTrOK && slOKL && psOKL && FadeDownLongs;
            bool goS = legOK && cntOKS && trendS && perTrOK && slOKS && psOKS && FadeUpShorts;
            if (goL)
            {
                double epL = h + tick; pend.Add(new Ord { Dir = 1, Px = epL, Stop = slCandL, Tgt = Rnd(epL + LegR * (epL - slCandL)), Kind = "Leg", Qty = qtyUse });
                Values[P_PENDL][0] = epL;
                if (ShowMarks) Draw.Text(this, "lw_H" + i, false, "H", 0, l - 8 * tick, 0, Brushes.DodgerBlue, new SimpleFont("Arial", 9), System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            }
            if (goS)
            {
                double epS = l - tick; pend.Add(new Ord { Dir = -1, Px = epS, Stop = slCandS, Tgt = Rnd(epS - LegR * (slCandS - epS)), Kind = "Leg", Qty = qtyUse });
                Values[P_PENDS][0] = epS;
                if (ShowMarks) Draw.Text(this, "lw_L" + i, false, "L", 0, h + 8 * tick, 0, Brushes.DodgerBlue, new SimpleFont("Arial", 9), System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            }

            // ---- breakeven (fade only), on the candle close
            if (UseBE && !beOn && pos != null && pos.Kind == "Fade")
            {
                double ep = pos.Entry;
                if (pos.Dir < 0 && (ep - l) >= riskPts * BeTriggerR) { beOn = true; stopPx = ep - BeTicks * tick; pos.Stop = stopPx; }
                if (pos.Dir > 0 && (h - ep) >= riskPts * BeTriggerR) { beOn = true; stopPx = ep + BeTicks * tick; pos.Stop = stopPx; }
            }
            // ---- daily limits as resting exit prices for the next bar
            if (pos != null)
            {
                double dClosed = netPnl - dayNet0, perPt = pos.Qty * pv;
                double capUp = pos.Dir > 0 && usePC ? pos.Entry + (pCap - dClosed) / perPt : double.NaN;
                double capDn = pos.Dir < 0 && usePC ? pos.Entry - (pCap - dClosed) / perPt : double.NaN;
                double losDn = pos.Dir > 0 && UseMaxLoss ? pos.Entry - (MaxLossDay + dClosed) / perPt : double.NaN;
                double losUp = pos.Dir < 0 && UseMaxLoss ? pos.Entry + (MaxLossDay + dClosed) / perPt : double.NaN;
                pos.EStop = pos.Stop; pos.ETgt = pos.Tgt;
                if (pos.Dir > 0) { if (!double.IsNaN(capUp)) pos.ETgt = Math.Min(pos.Tgt, Rnd(capUp)); if (!double.IsNaN(losDn)) pos.EStop = Math.Max(pos.Stop, Rnd(losDn)); }
                else { if (!double.IsNaN(capDn)) pos.ETgt = Math.Max(pos.Tgt, Rnd(capDn)); if (!double.IsNaN(losUp)) pos.EStop = Math.Min(pos.Stop, Rnd(losUp)); }
                Values[P_STOP][0] = pos.Stop; Values[P_TGT][0] = pos.Tgt;
            }
            // flatten on the candle that closes at the flatten time (position that opened at this bar's close, etc.)
            if (tClose == flatM && pos != null) ClosePos(c - pos.Dir * Slip, "EOD", i);
            if (tClose >= flatM) pend.Clear();

            // ================= plots / visuals =================
            if (ibReady) { Values[P_ORBH][0] = ibH; Values[P_ORBL][0] = ibL; Values[P_ORBM][0] = ibMid; }
            Values[P_TM][0] = (ShowTrendMagic && i >= CciPeriod && tmLine > 0) ? tmLine : double.NaN;
            PlotBrushes[P_TM][0] = tmBull ? TmBullBrush : TmBearBrush;
            Values[P_PSAR][0] = (ShowPsar && !double.IsNaN(psSAR)) ? psSAR : double.NaN;
            Values[P_PHASE][0] = phase; Values[P_DONE][0] = dayDone ? 1 : 0; Values[P_EVBAL][0] = evBal;
            Values[P_EVST][0] = !EvOn ? 0 : evPassed ? 2 : evFailed ? 3 : evLive ? 1 : 0; Values[P_QTY][0] = qtyUse; Values[P_DAYPNL][0] = netPnl + OpenProfit() - dayNet0;
            UpdateOrb(); UpdateLevels();
            if (ShowTable)
            {
                try { DrawStatus(dayPnl, pPct, pTrUp, pTrDn, usePace, paceUse); }
                catch (Exception ex) { Print("SneakyLivewirePropBuster status box error: " + ex.Message); }
            }
            prevTOpen = tOpen; prevDay = dayKey; prevC0 = c;
        }
        double prevC0 = double.NaN;
        internal Action<string> TradeLog;   // test hook (null in normal use)

        // ------------------------------------------------------------------ ORB box, key levels
        void StartOrb()
        {
            if (!ShowOrb) return;
            curOrbTag = "lw_orb" + CurrentBar; orbOpen = true; orbDrawStart = orbBar0; orbDrawH = ibH; orbDrawL = ibL; orbTags.Add(curOrbTag);
            DrawOrb();
            while (orbTags.Count > OrbDays) { string t = orbTags[0]; orbTags.RemoveAt(0); RemoveDrawObject(t); RemoveDrawObject(t + "m"); }
        }
        void DrawOrb()
        {
            if (curOrbTag == null) return;
            int sb = CurrentBar - orbDrawStart;
            Draw.Rectangle(this, curOrbTag, false, sb, ibH, 0, ibL, OrbBrush, OrbBrush, 8);
            double m = (ibH + ibL) / 2.0;
            Draw.Line(this, curOrbTag + "m", false, sb, m, 0, m, OrbBrush, DashStyleHelper.Dash, 1);
        }
        void UpdateOrb() { if (orbOpen && (CurrentBar >= Bars.Count - 1 || tClose >= flatM)) { DrawOrb(); if (tClose >= flatM) orbOpen = false; } }
        void FinishOrb()
        {
            if (curOrbTag != null)
            {
                int sb = CurrentBar - orbDrawStart; double m = (orbDrawH + orbDrawL) / 2.0;
                Draw.Rectangle(this, curOrbTag, false, sb, orbDrawH, 1, orbDrawL, OrbBrush, OrbBrush, 8);
                Draw.Line(this, curOrbTag + "m", false, sb, m, 1, m, OrbBrush, DashStyleHelper.Dash, 1);
            }
            orbOpen = false;
        }

        void NewCmeCheck(int dayKey)
        {
            int key = tOpenDt.AddHours(6).Year * 10000 + tOpenDt.AddHours(6).Month * 100 + tOpenDt.AddHours(6).Day;
            if (key != cmeKey) { cmeKey = key; NewCme(); }
        }
        void NewCme()
        {
            pdOpen = dOpen; dOpen = Open[0];
            CloseLevel(curDO); CloseLevel(curPDO);
            if (ShowDO) curDO = AddLevel("lw_do", dOpen, DoBrush, DashStyleHelper.Solid);
            if (ShowPDO && !double.IsNaN(pdOpen)) curPDO = AddLevel("lw_pdo", pdOpen, PdoBrush, DashStyleHelper.Dash);
        }
        void NyoStart()
        {
            CloseLevel(curNYO); CloseLevel(curPNYO);
            if (ShowNYO) curNYO = AddLevel("lw_nyo", nyOpen, NyoBrush, DashStyleHelper.Solid);
            if (ShowPNYO && !double.IsNaN(pNyOpen)) curPNYO = AddLevel("lw_pnyo", pNyOpen, Brushes.Navy, DashStyleHelper.Dash);
        }
        Lvl AddLevel(string tg, double y, Brush b, DashStyleHelper d)
        {
            Lvl v = new Lvl { Tag = tg + CurrentBar, Bar0 = CurrentBar, Y = y, B = b, D = d, Open = true };
            lvlAll.Add(v); Draw.Line(this, v.Tag, false, 0, y, 0, y, b, d, 2);
            while (lvlAll.Count > LevelDays * 4) { RemoveDrawObject(lvlAll[0].Tag); lvlAll.RemoveAt(0); }
            return v;
        }
        void CloseLevel(Lvl v)
        {
            if (v == null || !v.Open) return; v.Open = false;
            int e = Math.Min(1, CurrentBar - v.Bar0);
            Draw.Line(this, v.Tag, false, CurrentBar - v.Bar0, v.Y, e, v.Y, v.B, v.D, 2);
        }
        void ExtendLevel(Lvl v) { if (v != null && v.Open) Draw.Line(this, v.Tag, false, CurrentBar - v.Bar0, v.Y, 0, v.Y, v.B, v.D, 2); }
        void UpdateLevels()
        {
            if (CurrentBar < Bars.Count - 1) return;
            ExtendLevel(curNYO); ExtendLevel(curDO); ExtendLevel(curPDO); ExtendLevel(curPNYO);
        }

        // ------------------------------------------------------------------ status table (text box)
        static string M(double v) { return (v < 0 ? "-$" : "$") + Math.Abs(v).ToString("#,##0", CultureInfo.InvariantCulture); }
        void DrawStatus(double dayPnl, double pPct, bool pTrUp, bool pTrDn, bool usePace, double paceUse)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("LIVEWIRE PROP BUSTER (simulated)");
            sb.AppendLine("Today    : " + why);
            sb.AppendLine("Prior day: " + (double.IsNaN(pPct) ? "n/a" : pPct.ToString("0", CultureInfo.InvariantCulture) + "% of range" + (pTrUp ? " (trend UP)" : pTrDn ? " (trend DOWN)" : "")));
            sb.AppendLine("News/skip: " + (newsSkip ? "YES - no fade" : "no"));
            sb.AppendLine("Target   : " + (TargetType == SneakyLwTarget.RMultiple ? TargetR.ToString("0.##", CultureInfo.InvariantCulture) + "R" : TargetPct + "% to midline") + "   BE: " + (UseBE ? "+" + BeTicks + "t at " + BeTriggerR + "R" : "off"));
            string legName = UseH1 && UseH2 ? "H1+H2 / L1+L2" : UseH1 ? "H1 / L1" : "H2 / L2";
            sb.AppendLine("Legs     : " + (UseLegs ? legName + " - " + (dayLocked ? "stopped (daily limit)" : emaTr + " of " + LegMaxPerDay + " today") : "off"));
            string lim = (UseMaxLoss ? "-$" + MaxLossDay.ToString("#,##0", CultureInfo.InvariantCulture) : "no max loss") + " / " + (usePC ? "+$" + pCap.ToString("#,##0", CultureInfo.InvariantCulture) : "no max profit");
            sb.AppendLine("Day P&L  : " + M(dayPnl) + "  (limits " + lim + ")" + (dayLocked ? "  STOPPED" : ""));
            sb.AppendLine("Trades   : " + nTr + "  win " + (nTr > 0 ? (nWin * 100.0 / nTr).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "-") + "  net " + M(netPnl) + "  (sim, " + (qtyUse) + " ct)");
            if (EvOn)
            {
                string st = evPassed ? "PASSED" : evFailed ? "FAILED (drawdown hit)" : !evLive ? "starts " + EvalStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "in progress";
                sb.AppendLine("Eval     : " + EvalAccount + " | " + EvalCons + " rule | " + qtyUse + " ct" + (usePace ? " (pace) | day " + evDayN + " of " + EvalLimitDays : "") + " | " + st);
                sb.AppendLine("Progress : " + M(evBal) + " of " + M(evTgt) + "  (" + (evTgt > 0 ? (evBal / evTgt * 100).ToString("0", CultureInfo.InvariantCulture) : "0") + "%)");
                sb.AppendLine("Room     : " + M(evEq - evFloor) + " to drawdown | best day " + M(Math.Max(evBest, evBal - evDayBal)) + " (cap " + M(pCap) + ")");
            }
            statusText = sb.ToString();
        }

        // ------------------------------------------------------------------ status box (drawn by the chart renderer)
        string statusText = "";
        D2D.Brush dxBack, dxText;
        public override void OnRenderTargetChanged()
        {
            Kill(ref dxBack); Kill(ref dxText);
            if (RenderTarget == null) return;
            dxBack = Brushes.Black.ToDxBrush(RenderTarget); dxBack.Opacity = 0.78f;
            dxText = Brushes.White.ToDxBrush(RenderTarget);
        }
        static void Kill(ref D2D.Brush b) { if (b != null) { b.Dispose(); b = null; } }
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (!ShowTable || badChart || string.IsNullOrEmpty(statusText) || RenderTarget == null || dxBack == null || ChartPanel == null) return;
            try
            {
                string[] lines = statusText.Replace("\r", "").Split('\n');
                using (DW.TextFormat tf = new DW.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", 12f))
                {
                    float w = 0f, h = 0f, lineH = 0f; List<DW.TextLayout> tls = new List<DW.TextLayout>();
                    foreach (string ln in lines)
                    {
                        if (ln.Length == 0) continue;
                        DW.TextLayout tl = new DW.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, ln, tf, 3000f, 100f);
                        tls.Add(tl); w = Math.Max(w, tl.Metrics.Width); lineH = Math.Max(lineH, tl.Metrics.Height);
                    }
                    h = lineH * tls.Count;
                    float x0 = ChartPanel.X + ChartPanel.W - w - 24f, y0 = ChartPanel.Y + 42f;
                    RenderTarget.FillRectangle(new SDX.RectangleF(x0 - 8f, y0 - 4f, w + 16f, h + 8f), dxBack);
                    for (int k = 0; k < tls.Count; k++) { RenderTarget.DrawTextLayout(new SDX.Vector2(x0, y0 + k * lineH), tls[k], dxText); tls[k].Dispose(); }
                }
            }
            catch { }
        }

        #region Properties
        [NinjaScriptProperty, Display(Name = "ORB fade entries", GroupName = "1. Entries", Order = 0)] public bool UseOrb { get; set; }
        [NinjaScriptProperty, Display(Name = "H1 / L1 entries", GroupName = "1. Entries", Order = 1)] public bool UseH1 { get; set; }
        [NinjaScriptProperty, Display(Name = "H2 / L2 entries", GroupName = "1. Entries", Order = 2)] public bool UseH2 { get; set; }

        [NinjaScriptProperty, Display(Name = "Stop for the day at max loss", GroupName = "2. Daily limits", Order = 0)] public bool UseMaxLoss { get; set; }
        [NinjaScriptProperty, Range(1, 1000000), Display(Name = "Max loss ($)", GroupName = "2. Daily limits", Order = 1)] public double MaxLossDay { get; set; }
        [NinjaScriptProperty, Display(Name = "Stop for the day at max profit", GroupName = "2. Daily limits", Order = 2)] public bool UseMaxProfit { get; set; }
        [NinjaScriptProperty, Range(1, 1000000), Display(Name = "Max profit ($)", GroupName = "2. Daily limits", Order = 3)] public double MaxProfitDay { get; set; }
        [NinjaScriptProperty, Display(Name = "Use MY daily limits during the eval", GroupName = "2. Daily limits", Order = 4, Description = "Off: the eval bot sets the daily profit cap from the consistency rule (90% of the limit).")] public bool OwnLimits { get; set; }

        [NinjaScriptProperty, Display(Name = "Eval account", GroupName = "3. Eval bot", Order = 0, Description = "Off = raw strategy signals. 25k 1.5k/1.5k, 50k 3k/2k, 100k 6k/3k, 150k 9k/4.5k (target/drawdown).")] public SneakyLwAccount EvalAccount { get; set; }
        [NinjaScriptProperty, Display(Name = "Consistency rule", GroupName = "3. Eval bot", Order = 1)] public SneakyLwCons EvalCons { get; set; }
        [NinjaScriptProperty, Display(Name = "Strategy", GroupName = "3. Eval bot", Order = 2)] public SneakyLwStrategy EvalStrategyMode { get; set; }
        [NinjaScriptProperty, Display(Name = "Contracts", GroupName = "3. Eval bot", Order = 3, Description = "Recommended = best tested size for the account / rule / strategy (MNQ contracts in the original).")] public SneakyLwSize EvalSize { get; set; }
        [NinjaScriptProperty, Display(Name = "Eval start date", GroupName = "3. Eval bot", Order = 4)] public DateTime EvalStart { get; set; }
        [NinjaScriptProperty, Display(Name = "Stop when target reached / drawdown hit", GroupName = "3. Eval bot", Order = 5)] public bool EvalStopWhenDone { get; set; }
        [NinjaScriptProperty, Display(Name = "Speed mode", GroupName = "3. Eval bot", Order = 6)] public SneakyLwSpeed EvalSpeed { get; set; }
        [NinjaScriptProperty, Range(1, 400), Display(Name = "FAST: time limit (trading days)", GroupName = "3. Eval bot", Order = 7)] public int EvalLimitDays { get; set; }
        [NinjaScriptProperty, Range(0, 1000), Display(Name = "FAST pace $ per contract per day (0 = auto)", GroupName = "3. Eval bot", Order = 8)] public double EvalPace { get; set; }
        [NinjaScriptProperty, Range(1, 1000), Display(Name = "Pace sizing: min contracts", GroupName = "3. Eval bot", Order = 9)] public int EvalMinQ { get; set; }
        [NinjaScriptProperty, Range(1, 1000), Display(Name = "Pace sizing: max contracts", GroupName = "3. Eval bot", Order = 10)] public int EvalMaxQ { get; set; }
        [NinjaScriptProperty, Range(1, 10000000), Display(Name = "CUSTOM account: target ($)", GroupName = "3. Eval bot", Order = 11)] public double EvalCustomTarget { get; set; }
        [NinjaScriptProperty, Range(1, 10000000), Display(Name = "CUSTOM account: drawdown ($)", GroupName = "3. Eval bot", Order = 12)] public double EvalCustomDD { get; set; }

        [NinjaScriptProperty, Range(1, 1000), Display(Name = "Contracts (when eval is off / not Recommended)", GroupName = "4. Trade", Order = 0)] public int Contracts { get; set; }
        [NinjaScriptProperty, Display(Name = "Target type", GroupName = "4. Trade", Order = 1)] public SneakyLwTarget TargetType { get; set; }
        [NinjaScriptProperty, Range(0.25, 20.0), Display(Name = "R multiple target", GroupName = "4. Trade", Order = 2)] public double TargetR { get; set; }
        [NinjaScriptProperty, Range(10, 100), Display(Name = "Midline target: % of the way", GroupName = "4. Trade", Order = 3)] public int TargetPct { get; set; }
        [NinjaScriptProperty, Display(Name = "Entry method", GroupName = "4. Trade", Order = 4)] public SneakyLwEntry EntryMethod { get; set; }
        [NinjaScriptProperty, Display(Name = "First pullback =", GroupName = "4. Trade", Order = 5)] public SneakyLwPullback PullbackDef { get; set; }
        [NinjaScriptProperty, Range(0, 100), Display(Name = "Stop-order entry: ticks inside the ORB edge", GroupName = "4. Trade", Order = 6)] public int EntryTicksInside { get; set; }
        [NinjaScriptProperty, Range(0, 100), Display(Name = "Stop: ticks beyond the push", GroupName = "4. Trade", Order = 7)] public int StopTicksBeyond { get; set; }
        [NinjaScriptProperty, Display(Name = "Move stop to breakeven + ticks", GroupName = "4. Trade", Order = 8)] public bool UseBE { get; set; }
        [NinjaScriptProperty, Range(0.25, 20.0), Display(Name = "Breakeven trigger (R)", GroupName = "4. Trade", Order = 9)] public double BeTriggerR { get; set; }
        [NinjaScriptProperty, Range(0, 100), Display(Name = "Breakeven lock (ticks)", GroupName = "4. Trade", Order = 10)] public int BeTicks { get; set; }
        [NinjaScriptProperty, Display(Name = "Fade breakouts UP (shorts)", GroupName = "4. Trade", Order = 11)] public bool FadeUpShorts { get; set; }
        [NinjaScriptProperty, Display(Name = "Fade breakouts DOWN (longs)", GroupName = "4. Trade", Order = 12)] public bool FadeDownLongs { get; set; }

        [NinjaScriptProperty, Display(Name = "Skip if prior day closed trending in the breakout direction", GroupName = "5. Skip filters", Order = 0)] public bool UseTrendFilter { get; set; }
        [NinjaScriptProperty, Range(1, 50), Display(Name = "Trending close = within this % of the prior high / low", GroupName = "5. Skip filters", Order = 1)] public double TrendZone { get; set; }
        [NinjaScriptProperty, Display(Name = "Skip 10:00 ET news days (calendar rules)", GroupName = "5. Skip filters", Order = 2)] public bool UseNewsFilter { get; set; }
        [NinjaScriptProperty, Display(Name = "Extra dates to skip (yyyy-MM-dd, comma separated)", GroupName = "5. Skip filters", Order = 3)] public string SkipDates { get; set; }
        [NinjaScriptProperty, Display(Name = "Skip TODAY (manual)", GroupName = "5. Skip filters", Order = 4)] public bool SkipToday { get; set; }

        [NinjaScriptProperty, Range(-24, 24), Display(Name = "Hours to add to chart time to get ET", GroupName = "6. Session", Order = 0, Description = "0 if your PC / chart is on Eastern time. Chart on Central: 1.")] public int EtOffsetHours { get; set; }
        [NinjaScriptProperty, Range(0, 2359), Display(Name = "ORB start (HHMM, ET)", GroupName = "6. Session", Order = 1)] public int IbStartHHMM { get; set; }
        [NinjaScriptProperty, Range(0, 2359), Display(Name = "ORB end (HHMM)", GroupName = "6. Session", Order = 2)] public int IbEndHHMM { get; set; }
        [NinjaScriptProperty, Range(0, 2359), Display(Name = "Last entry - candle close (HHMM)", GroupName = "6. Session", Order = 3)] public int LastEntryHHMM { get; set; }
        [NinjaScriptProperty, Range(0, 2359), Display(Name = "Flatten at (HHMM)", GroupName = "6. Session", Order = 4)] public int FlattenHHMM { get; set; }

        [NinjaScriptProperty, Display(Name = "Trend filter", GroupName = "7. H / L entries", Order = 0)] public SneakyLwLegFilter LegFilter { get; set; }
        [NinjaScriptProperty, Range(0, 100), Display(Name = "Max entries per Trend Magic trend (0 = no limit)", GroupName = "7. H / L entries", Order = 1)] public int LegPerTrend { get; set; }
        [NinjaScriptProperty, Display(Name = "Stop", GroupName = "7. H / L entries", Order = 2)] public SneakyLwLegStop LegStopMode { get; set; }
        [NinjaScriptProperty, Range(0.5, 20.0), Display(Name = "Target (R)", GroupName = "7. H / L entries", Order = 3)] public double LegR { get; set; }
        [NinjaScriptProperty, Range(0, 100000), Display(Name = "Skip if the stop is wider than (points, 0 = off)", GroupName = "7. H / L entries", Order = 4)] public double LegMaxStopPts { get; set; }
        [NinjaScriptProperty, Range(1, 100), Display(Name = "Max H / L trades per day", GroupName = "7. H / L entries", Order = 5)] public int LegMaxPerDay { get; set; }
        [NinjaScriptProperty, Range(0, 2359), Display(Name = "Earliest H / L entry (HHMM)", GroupName = "7. H / L entries", Order = 6)] public int LegStartHHMM { get; set; }
        [NinjaScriptProperty, Display(Name = "When", GroupName = "7. H / L entries", Order = 7)] public SneakyLwLegWhen LegWhen { get; set; }
        [NinjaScriptProperty, Display(Name = "PSAR direction filter", GroupName = "7. H / L entries", Order = 8)] public bool PsarFilter { get; set; }
        [NinjaScriptProperty, Display(Name = "ADX / DMI filter", GroupName = "7. H / L entries", Order = 9)] public bool UseAdx { get; set; }
        [NinjaScriptProperty, Range(5, 100), Display(Name = "ADX level", GroupName = "7. H / L entries", Order = 10)] public double AdxLevel { get; set; }
        [NinjaScriptProperty, Range(2, 100), Display(Name = "DI length / ADX smoothing", GroupName = "7. H / L entries", Order = 11)] public int AdxLen { get; set; }
        [NinjaScriptProperty, Range(1, 1000), Display(Name = "PSAR: regression length", GroupName = "7. H / L entries", Order = 12)] public int PsarLen { get; set; }
        [NinjaScriptProperty, Range(0.001, 1.0), Display(Name = "PSAR: start", GroupName = "7. H / L entries", Order = 13)] public double PsarStart { get; set; }
        [NinjaScriptProperty, Range(0.001, 1.0), Display(Name = "PSAR: increment", GroupName = "7. H / L entries", Order = 14)] public double PsarInc { get; set; }
        [NinjaScriptProperty, Range(0.001, 1.0), Display(Name = "PSAR: maximum", GroupName = "7. H / L entries", Order = 15)] public double PsarMax { get; set; }
        [NinjaScriptProperty, Range(1, 200), Display(Name = "Trend Magic: CCI period", GroupName = "7. H / L entries", Order = 16)] public int CciPeriod { get; set; }
        [NinjaScriptProperty, Range(0.0, 20.0), Display(Name = "Trend Magic: ATR multiplier", GroupName = "7. H / L entries", Order = 17)] public double TmAtrMult { get; set; }
        [NinjaScriptProperty, Range(1, 200), Display(Name = "Trend Magic: ATR period", GroupName = "7. H / L entries", Order = 18)] public int TmAtrPeriod { get; set; }

        [NinjaScriptProperty, Range(0, 20), Display(Name = "Slippage (ticks, stop / market fills)", GroupName = "8. Simulation", Order = 0)] public int SlippageTicks { get; set; }
        [NinjaScriptProperty, Range(0.0, 100.0), Display(Name = "Commission ($ per contract per side)", GroupName = "8. Simulation", Order = 1)] public double CommissionPerContract { get; set; }
        [NinjaScriptProperty, Display(Name = "Stop and target in the same bar", GroupName = "8. Simulation", Order = 2, Description = "StopFirst: assume the stop (conservative, what my backtests used). PineEmulator: O-H-L-C or O-L-H-C path like TradingView.")] public SneakyLwPath IntrabarPath { get; set; }

        [NinjaScriptProperty, Display(Name = "Show status box", GroupName = "9. Visuals", Order = 0)] public bool ShowTable { get; set; }
        [NinjaScriptProperty, Display(Name = "Show B / 1 / 2 / H / L marks", GroupName = "9. Visuals", Order = 1)] public bool ShowMarks { get; set; }
        [NinjaScriptProperty, Display(Name = "Show trades (arrows, exit lines)", GroupName = "9. Visuals", Order = 2)] public bool ShowTrades { get; set; }
        [NinjaScriptProperty, Display(Name = "Show 15m ORB box", GroupName = "9. Visuals", Order = 3)] public bool ShowOrb { get; set; }
        [NinjaScriptProperty, Range(1, 400), Display(Name = "Days of ORB boxes to keep", GroupName = "9. Visuals", Order = 4)] public int OrbDays { get; set; }
        [NinjaScriptProperty, Display(Name = "Show Trend Magic", GroupName = "9. Visuals", Order = 5)] public bool ShowTrendMagic { get; set; }
        [NinjaScriptProperty, Display(Name = "Show PSAR (LSMA)", GroupName = "9. Visuals", Order = 6)] public bool ShowPsar { get; set; }
        [NinjaScriptProperty, Display(Name = "NY open line", GroupName = "9. Visuals", Order = 7)] public bool ShowNYO { get; set; }
        [NinjaScriptProperty, Display(Name = "Daily open line (18:00 ET)", GroupName = "9. Visuals", Order = 8)] public bool ShowDO { get; set; }
        [NinjaScriptProperty, Display(Name = "Prior day open line", GroupName = "9. Visuals", Order = 9)] public bool ShowPDO { get; set; }
        [NinjaScriptProperty, Display(Name = "Prior day NY open line", GroupName = "9. Visuals", Order = 10)] public bool ShowPNYO { get; set; }
        [NinjaScriptProperty, Range(1, 120), Display(Name = "Days of key levels to keep", GroupName = "9. Visuals", Order = 11)] public int LevelDays { get; set; }
        [NinjaScriptProperty, Display(Name = "Alert on simulated entries / exits", GroupName = "9. Visuals", Order = 12)] public bool AlertOnTrades { get; set; }

        [XmlIgnore, Display(Name = "ORB colour", GroupName = "10. Colours", Order = 0)] public Brush OrbBrush { get; set; }
        [Browsable(false)] public string OrbBrushSerialize { get { return Serialize.BrushToString(OrbBrush); } set { OrbBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore, Display(Name = "Trend Magic bullish", GroupName = "10. Colours", Order = 1)] public Brush TmBullBrush { get; set; }
        [Browsable(false)] public string TmBullBrushSerialize { get { return Serialize.BrushToString(TmBullBrush); } set { TmBullBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore, Display(Name = "Trend Magic bearish", GroupName = "10. Colours", Order = 2)] public Brush TmBearBrush { get; set; }
        [Browsable(false)] public string TmBearBrushSerialize { get { return Serialize.BrushToString(TmBearBrush); } set { TmBearBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore, Display(Name = "NY open line", GroupName = "10. Colours", Order = 3)] public Brush NyoBrush { get; set; }
        [Browsable(false)] public string NyoBrushSerialize { get { return Serialize.BrushToString(NyoBrush); } set { NyoBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore, Display(Name = "Daily open line", GroupName = "10. Colours", Order = 4)] public Brush DoBrush { get; set; }
        [Browsable(false)] public string DoBrushSerialize { get { return Serialize.BrushToString(DoBrush); } set { DoBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore, Display(Name = "Prior day open line", GroupName = "10. Colours", Order = 5)] public Brush PdoBrush { get; set; }
        [Browsable(false)] public string PdoBrushSerialize { get { return Serialize.BrushToString(PdoBrush); } set { PdoBrush = Serialize.StringToBrush(value); } }

        // ---- plot accessors (sparse signals are N/A on non-signal bars)
        [Browsable(false), XmlIgnore] public Series<double> BuySignal { get { Update(); return Values[P_BUY]; } }
        [Browsable(false), XmlIgnore] public Series<double> SellSignal { get { Update(); return Values[P_SELL]; } }
        [Browsable(false), XmlIgnore] public Series<double> FadeFill { get { Update(); return Values[P_FADE]; } }
        [Browsable(false), XmlIgnore] public Series<double> LegFill { get { Update(); return Values[P_LEG]; } }
        [Browsable(false), XmlIgnore] public Series<double> ExitSignal { get { Update(); return Values[P_EXIT]; } }
        [Browsable(false), XmlIgnore] public Series<double> PendingBuyStop { get { Update(); return Values[P_PENDL]; } }
        [Browsable(false), XmlIgnore] public Series<double> PendingSellStop { get { Update(); return Values[P_PENDS]; } }
        [Browsable(false), XmlIgnore] public Series<double> EvalBalance { get { Update(); return Values[P_EVBAL]; } }
        [Browsable(false), XmlIgnore] public Series<double> EvalState { get { Update(); return Values[P_EVST]; } }
        #endregion
    }
}
