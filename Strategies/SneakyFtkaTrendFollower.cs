#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// SneakyFtkaTrendFollower
// NinjaTrader 8 STRATEGY port of the TradingView Pine v6 strategy "FTKA Trend Follower".
//
//   Entry types (each switchable): 1) reversal bar after N opposite-coloured candles, 2) first green/red candle after an ATF
//   (Adaptive Trend Flow) colour flip, 3) SMA pullback (touch, then a candle closing back in the trend direction, max 3 per phase).
//   Filters: SMA (close on the right side, and the SMA must not sit inside the ATF band), optional ATF mid-line filter,
//   optional "WAE momentum active" requirement.
//   Exits: hard stop (65 ticks), TP1 (75% of the contracts at 44 ticks), runner TP2 (106 ticks) with a breakeven floor
//   after TP1, runner exit when WAE momentum is lost for 2 bars, flatten at session end / no-trade window, optional daily caps.
//
// Uses real unmanaged orders; signals are evaluated on bar close (Calculate.OnBarClose). Times are Eastern.
//
// VISUALS (ported from the original Pine source):
//   - ATF trend line (+ optional faint upper/lower zone lines), colour-flips bull/bear; optional bar colouring.
//   - SMA line.
//   - Live Entry / Stop / TP1 / TP2 horizontal lines with labels while a position is open; stop line relabels "Breakeven" after TP1.
//   - Per-leg exit labels (TP1 / TP2 / Stop / flatten) drawn as each leg fills, coloured by that leg's $ result.
//   - WIN / LOSS / BE summary label at the close of every full trade cycle.
//   - Vertical de-overlap: clustered labels are nudged apart so back-to-back trades stay readable (LabelDodgeBars window).
//   - Session P&L panel drawn in OnRender (Direct2D): header bar, Asia/London/NY/TOTAL rows with green/red today + rolling $,
//     live-session highlight, max drawdown, win rate, position and last event. Replaces the old flat Draw.TextFixed panel.

namespace NinjaTrader.NinjaScript.Strategies
{
    public class SneakyFtkaTrendFollower : Strategy
    {
        // ------------------------------------------------------------------ series state
        double tick, pv;
        int tfMin = 15;
        double fastE = double.NaN, slowE = double.NaN, smvE = double.NaN, wfE = double.NaN, wsE = double.NaN, prevMacd = double.NaN;
        List<double> typBuf = new List<double>(), clBuf = new List<double>();
        double basis, atfUp, atfLo, atfLevel = double.NaN, smaV = double.NaN;
        int atfTrend; bool prevBull, prevBear, waitG, waitR, pbG, pbR; int pbCount, waeStreak;
        bool waeLost;

        // orders / position
        Order entryOrd, stopOrd, tp1Ord, tp2Ord;
        int pendDir, pendBar = -1; double pendPx;
        int ordSeq; string pendName = "";
        bool tp1Done;
        double entryAvg, floorPx;
        int tp1Q, runQ, tradeDir;
        // daily cap
        double sessStartMtm = double.NaN; int capDay = -1; bool capHit, lossHit;
        // time
        int tOpen, tNext;
        bool prevInSess;

        // ------------------------------------------------------------------ visuals / session P&L state
        double cycleStartNetProfit = double.NaN;
        string lastEvent = "-";
        double lastCountedMtm = double.NaN;
        bool prevInAsia, inAsiaNow, inLondonNow, inNYNow;
        double asiaToday, londonToday, nyToday;
        int asiaWinsToday, asiaLossesToday, asiaBEToday;
        int londonWinsToday, londonLossesToday, londonBEToday;
        int nyWinsToday, nyLossesToday, nyBEToday;
        List<double> asiaHist = new List<double>(), londonHist = new List<double>(), nyHist = new List<double>();
        List<int> asiaHistW = new List<int>(), asiaHistL = new List<int>(), asiaHistBE = new List<int>();
        List<int> londonHistW = new List<int>(), londonHistL = new List<int>(), londonHistBE = new List<int>();
        List<int> nyHistW = new List<int>(), nyHistL = new List<int>(), nyHistBE = new List<int>();
        double asiaRollTotal, londonRollTotal, nyRollTotal;
        int asiaRollW, asiaRollL, asiaRollBE, londonRollW, londonRollL, londonRollBE, nyRollW, nyRollL, nyRollBE;
        double dayPeakPnL, dayMaxDrawdown;

        // label de-overlap: each anchor = { barIndex, yPrice }
        List<double[]> labelAnchors = new List<double[]>();
        NinjaTrader.Gui.Tools.SimpleFont _labelFont;

        // OnRender panel resources (device brushes created in OnRenderTargetChanged; text formats are device-independent)
        SharpDX.Direct2D1.SolidColorBrush bgBr, hdrBr, liveBr, txtBr, dimBr, posBr, negBr;
        SharpDX.DirectWrite.TextFormat fNorm, fBold, fSmall;
        bool _panelErrLogged;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "FTKA Trend Follower: reversal / ATF colour-flip / SMA-pullback stop entries with TP1 + runner (NT8 port of the Pine strategy).";
                Name = "SneakyFtkaTrendFollower";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true; IsUnmanaged = true; IsExitOnSessionCloseStrategy = false; StartBehavior = StartBehavior.WaitUntilFlat;
                BarsRequiredToTrade = 150; TimeInForce = TimeInForce.Gtc; IsInstantiatedOnEachOptimizationIteration = true;
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite;

                Contracts = 4; EntryBufferTicks = 2; ExpireBars = 2;
                UseReversal = true; ReversalBars = 2; UseAtfEntry = true; UsePullback = true; MaxPullbacks = 3; PullbackProximity = false; ProximityTicks = 20;
                StopTicks = 65; UseTp1 = true; Tp1Ticks = 44; Tp1Pct = 75; ExitFullAtTp1 = false; UseTp2 = true; Tp2Ticks = 106;
                BreakevenOffsetTicks = 2; FloorBufferTicks = 4; WaeExitRunner = true; WaeRequireEntries = false;
                SmaLen = 14; UseSmaFilter = true; UseAtfFilter = false; AtfLen = 5; AtfSmooth = 20; AtfSens = 0.3;
                WaeSens = 150; WaeFast = 20; WaeSlow = 40; WaeBb = 20; WaeBbMult = 2.0;
                UseSession = true; SessionStartHHMM = 1845; SessionEndHHMM = 1600; NoTrade = true; NoTradeStartHHMM = 800; NoTradeEndHHMM = 1000; FlattenInNoTrade = true;
                UseProfitCap = false; ProfitCap = 900; UseLossCap = false; LossCap = 600; FlattenOnCap = true;
                AllowLongs = true; AllowShorts = true; EtOffsetHours = 0;
                ShowSignals = true;

                ShowAtfOnChart = true; ShowAtfZone = true; ColorBarsByAtf = false;
                AtfBullColor = Brushes.LimeGreen; AtfBearColor = Brushes.Red;
                ShowSmaOnChart = true; SmaLineColor = Brushes.Black;
                PlotLevels = true;
                EntryLineColor = Brushes.Black; StopLineColor = Brushes.Red; Tp1LineColor = Brushes.DodgerBlue; Tp2LineColor = Brushes.Green; BeLineColor = Brushes.Orange;
                ShowTradeLabels = true; ShowLegLabels = true; TradeLabelOffsetTicks = 20; LabelDodgeBars = 8; BreakevenThresholdUSD = 5;
                ShowPnlTable = true; RollingHistoryDays = 30;

                AddPlot(Brushes.Gray, "ATF Line");
                AddPlot(Brushes.Gray, "ATF Zone Upper");
                AddPlot(Brushes.Gray, "ATF Zone Lower");
                AddPlot(Brushes.Black, "SMA");
            }
            else if (State == State.DataLoaded)
            {
                tick = Instrument.MasterInstrument.TickSize; pv = Instrument.MasterInstrument.PointValue;
                tfMin = BarsPeriod.BarsPeriodType == BarsPeriodType.Minute && BarsPeriod.Value > 0 ? BarsPeriod.Value : 15;
                tp1Q = !UseTp1 ? 0 : (ExitFullAtTp1 ? Contracts : Math.Max(1, Math.Min(Contracts, (int)Math.Round(Contracts * Tp1Pct / 100.0, MidpointRounding.AwayFromZero))));
                runQ = Contracts - tp1Q;
                _labelFont = new NinjaTrader.Gui.Tools.SimpleFont("Arial", 12) { Bold = true };
                fNorm = new NinjaTrader.Gui.Tools.SimpleFont("Arial", 12).ToDirectWriteTextFormat();
                fBold = new NinjaTrader.Gui.Tools.SimpleFont("Arial", 13) { Bold = true }.ToDirectWriteTextFormat();
                fSmall = new NinjaTrader.Gui.Tools.SimpleFont("Arial", 10).ToDirectWriteTextFormat();
            }
            else if (State == State.Terminated)
            {
                DisposeBrushes();
                if (fNorm != null) { fNorm.Dispose(); fNorm = null; }
                if (fBold != null) { fBold.Dispose(); fBold = null; }
                if (fSmall != null) { fSmall.Dispose(); fSmall = null; }
            }
        }

        static int Mins(int hhmm) { return (hhmm / 100) * 60 + hhmm % 100; }
        bool InSess(int m) { if (!UseSession) return true; int a = Mins(SessionStartHHMM), b = Mins(SessionEndHHMM); return a > b ? (m >= a || m < b) : (m >= a && m < b); }
        bool InNtw(int m) { return NoTrade && m >= Mins(NoTradeStartHHMM) && m < Mins(NoTradeEndHHMM); }
        static bool InWin(int m, int startHHMM, int endHHMM) { int a = Mins(startHHMM), b = Mins(endHHMM); return a > b ? (m >= a || m < b) : (m >= a && m < b); }
        double RT(double px) { return Instrument.MasterInstrument.RoundToTickSize(px); }
        static bool Live(Order o) { return o != null && (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted || o.OrderState == OrderState.Submitted || o.OrderState == OrderState.ChangePending || o.OrderState == OrderState.ChangeSubmitted); }
        int RealDir { get { return Position.MarketPosition == MarketPosition.Long ? 1 : Position.MarketPosition == MarketPosition.Short ? -1 : 0; } }

        static double Ema(double prev, double x, int n) { double a = 2.0 / (n + 1); return double.IsNaN(prev) ? x : a * x + (1 - a) * prev; }
        static double PStd(List<double> v, int n)
        {
            int cnt = Math.Min(n, v.Count); if (cnt == 0) return 0; double s = 0, s2 = 0;
            for (int k = v.Count - cnt; k < v.Count; k++) { s += v[k]; s2 += v[k] * v[k]; }
            double m = s / cnt, var = s2 / cnt - m * m; return var > 0 ? Math.Sqrt(var) : 0;
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1) { UpdateSeries(); return; }
            int i = CurrentBar;
            DateTime tOpenDt = Time[0].AddMinutes(-tfMin).AddHours(EtOffsetHours);
            tOpen = tOpenDt.Hour * 60 + tOpenDt.Minute;
            DateTime tNextDt = tOpenDt.AddMinutes(tfMin);
            tNext = tNextDt.Hour * 60 + tNextDt.Minute;
            double o = Open[0], h = High[0], l = Low[0], c = Close[0];

            // ---------- indicators on this bar's close
            UpdateSeries();
            double bs = (fastE + slowE) / 2.0; basis = bs;
            atfUp = bs + smvE * AtfSens; atfLo = bs - smvE * AtfSens;
            if (atfTrend == 0) { atfTrend = c > bs ? 1 : -1; atfLevel = atfTrend == 1 ? atfLo : atfUp; }
            else if (atfTrend == 1) { if (c < atfLo) { atfTrend = -1; atfLevel = atfUp; } else atfLevel = atfLo; }
            else { if (c > atfUp) { atfTrend = 1; atfLevel = atfLo; } else atfLevel = atfUp; }
            bool bull = atfTrend == 1, bear = atfTrend == -1;
            bool flipBull = bull && !prevBull, flipBear = bear && !prevBear; prevBull = bull; prevBear = bear;
            waeStreak = waeLost ? waeStreak + 1 : 0;
            bool green = c > o, red = c < o;

            // ---------- visuals: ATF line / zone / bar colour, SMA line
            Values[0][0] = ShowAtfOnChart ? atfLevel : double.NaN;
            Values[1][0] = (ShowAtfOnChart && ShowAtfZone) ? atfUp : double.NaN;
            Values[2][0] = (ShowAtfOnChart && ShowAtfZone) ? atfLo : double.NaN;
            Values[3][0] = (ShowSmaOnChart && !double.IsNaN(smaV)) ? smaV : double.NaN;
            if (ShowAtfOnChart)
            {
                Brush atfBrush = bull ? AtfBullColor : bear ? AtfBearColor : Brushes.Gray;
                PlotBrushes[0][0] = atfBrush; PlotBrushes[1][0] = atfBrush; PlotBrushes[2][0] = atfBrush;
                if (ColorBarsByAtf) BarBrushes[CurrentBar] = atfBrush;
            }
            if (ShowSmaOnChart && !double.IsNaN(smaV)) PlotBrushes[3][0] = SmaLineColor;

            // ---------- entry-type state machines (run every bar, like the Pine script)
            if (flipBull) { waitG = true; waitR = false; }
            if (flipBear) { waitR = true; waitG = false; }
            bool sigAtfB = UseAtfEntry && waitG && green, sigAtfS = UseAtfEntry && waitR && red;
            if (sigAtfB) waitG = false; if (sigAtfS) waitR = false;
            if (flipBull || flipBear) pbCount = 0;
            bool touchL = !double.IsNaN(smaV) && (PullbackProximity ? (l - smaV) <= ProximityTicks * tick : l <= smaV);
            bool touchS = !double.IsNaN(smaV) && (PullbackProximity ? (smaV - h) <= ProximityTicks * tick : h >= smaV);
            if (UsePullback && bull && touchL && pbCount < MaxPullbacks) pbG = true;
            if (UsePullback && bear && touchS && pbCount < MaxPullbacks) pbR = true;
            bool sigPbB = UsePullback && pbG && green && c > bs && c > smaV, sigPbS = UsePullback && pbR && red && c < bs && c < smaV;
            if (sigPbB) { pbG = false; pbCount++; }
            if (sigPbS) { pbR = false; pbCount++; }
            bool sigRevB = UseReversal && green && Consec(true), sigRevS = UseReversal && red && Consec(false);
            bool isBull = sigRevB || sigAtfB || sigPbB, isBear = sigRevS || sigAtfS || sigPbS;
            if (i < BarsRequiredToTrade) { prevInSess = InSess(tOpen); return; }

            // ---------- session / windows
            bool sess = InSess(tOpen), sessNext = InSess(tNext);
            bool sessJustEnded = UseSession && prevInSess && !sess; prevInSess = sess;
            bool willSessEnd = UseSession && sess && !sessNext;
            bool inW = InNtw(tOpen), willW = InNtw(tNext) && !inW;

            // ---------- daily cap (18:00 ET reset)
            double unreal = Position.MarketPosition != MarketPosition.Flat ? Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, c) : 0.0;
            double mtm = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit + unreal;
            int dayKey = tOpenDt.Year * 10000 + tOpenDt.Month * 100 + tOpenDt.Day;
            if (double.IsNaN(sessStartMtm) || (tOpen >= 1080 && dayKey != capDay)) { sessStartMtm = mtm; capDay = tOpen >= 1080 ? dayKey : -1; capHit = lossHit = false; }
            double sp = mtm - sessStartMtm;
            if (UseProfitCap && sp >= ProfitCap) capHit = true;
            if (UseLossCap && sp <= -LossCap) lossHit = true;
            bool capBreached = capHit || lossHit;

            bool baseOk = sess && !inW && !willW && !willSessEnd && !capBreached;
            bool zoneBlock = UseSmaFilter && !double.IsNaN(smaV) && atfLo <= smaV && smaV <= atfUp;
            bool gateL = AllowLongs && baseOk && (!UseAtfFilter || c > bs) && (!UseSmaFilter || (c > smaV && !zoneBlock)) && (!WaeRequireEntries || !waeLost);
            bool gateS = AllowShorts && baseOk && (!UseAtfFilter || c < bs) && (!UseSmaFilter || (c < smaV && !zoneBlock)) && (!WaeRequireEntries || !waeLost);

            // ---------- session P&L tracking (Asia 1800 ET -> London -> NY close 1700 ET, no gap)
            inAsiaNow = InWin(tOpen, 1800, 200); inLondonNow = InWin(tOpen, 200, 800); inNYNow = InWin(tOpen, 800, 1700);
            bool newCapDay = inAsiaNow && !prevInAsia; prevInAsia = inAsiaNow;
            if (newCapDay)
            {
                PushCappedF(asiaHist, asiaToday, ref asiaRollTotal); PushCappedI(asiaHistW, asiaWinsToday, ref asiaRollW); PushCappedI(asiaHistL, asiaLossesToday, ref asiaRollL); PushCappedI(asiaHistBE, asiaBEToday, ref asiaRollBE);
                PushCappedF(londonHist, londonToday, ref londonRollTotal); PushCappedI(londonHistW, londonWinsToday, ref londonRollW); PushCappedI(londonHistL, londonLossesToday, ref londonRollL); PushCappedI(londonHistBE, londonBEToday, ref londonRollBE);
                PushCappedF(nyHist, nyToday, ref nyRollTotal); PushCappedI(nyHistW, nyWinsToday, ref nyRollW); PushCappedI(nyHistL, nyLossesToday, ref nyRollL); PushCappedI(nyHistBE, nyBEToday, ref nyRollBE);
                asiaToday = londonToday = nyToday = 0.0;
                asiaWinsToday = asiaLossesToday = asiaBEToday = 0;
                londonWinsToday = londonLossesToday = londonBEToday = 0;
                nyWinsToday = nyLossesToday = nyBEToday = 0;
                dayPeakPnL = 0.0; dayMaxDrawdown = 0.0;
            }
            double pnlDelta = double.IsNaN(lastCountedMtm) ? 0.0 : mtm - lastCountedMtm;
            lastCountedMtm = mtm;
            if (pnlDelta != 0.0) { if (inNYNow) nyToday += pnlDelta; else if (inLondonNow) londonToday += pnlDelta; else if (inAsiaNow) asiaToday += pnlDelta; }
            double dayPnL = asiaToday + londonToday + nyToday;
            dayPeakPnL = Math.Max(dayPeakPnL, dayPnL);
            dayMaxDrawdown = Math.Max(dayMaxDrawdown, dayPeakPnL - dayPnL);

            // ---------- pending entry order management
            bool flat = Position.MarketPosition == MarketPosition.Flat;
            if (!Live(entryOrd)) { entryOrd = null; pendBar = -1; }
            if (flat)
            {
                if (isBull && gateL) ArmEntry(1, h + EntryBufferTicks * tick, i);
                if (isBear && gateS) ArmEntry(-1, l - EntryBufferTicks * tick, i);
                if (pendBar >= 0 && ExpireBars > 0 && i - pendBar >= ExpireBars) CancelEntry();
                if (pendBar >= 0 && (sessJustEnded || willSessEnd || inW || willW)) CancelEntry();
            }
            else
            {
                CancelEntry();
                string why = null;
                if (UseSession && sessJustEnded) why = "SessionEnd";
                else if (FlattenInNoTrade && inW) why = "NtwFlatten";
                else if (capBreached && FlattenOnCap) why = "CapFlatten";
                else if (WaeExitRunner && tp1Done && waeStreak >= 2 && ((RealDir == 1 && c >= floorPx) || (RealDir == -1 && c <= floorPx))) why = "WAEExit";
                if (why != null) { CancelExits(); SubmitOrderUnmanaged(0, RealDir == 1 ? OrderAction.Sell : OrderAction.BuyToCover, OrderType.Market, Position.Quantity, 0, 0, "", "ftkaX_" + why); }
            }
            if (ShowSignals)
            {
                if (isBull && gateL) Draw.ArrowUp(this, "ftkaB" + i, false, 0, l - 4 * tick, Brushes.LimeGreen);
                if (isBear && gateS) Draw.ArrowDown(this, "ftkaS" + i, false, 0, h + 4 * tick, Brushes.Red);
            }
        }

        bool Consec(bool wantRed)
        {
            int cnt = 0;
            for (int k = 1; k <= 20; k++)
            {
                if (CurrentBar - k < 0) return false;
                double cc = Close[k], oo = Open[k];
                if (cc == oo) continue;
                if ((wantRed && cc < oo) || (!wantRed && cc > oo)) { cnt++; if (cnt >= ReversalBars) return true; }
                else return false;
            }
            return cnt >= ReversalBars;
        }

        void UpdateSeries()
        {
            double t = (High[0] + Low[0] + Close[0]) / 3.0, c = Close[0];
            typBuf.Add(t);
            fastE = Ema(fastE, t, AtfLen); slowE = Ema(slowE, t, AtfLen * 2);
            smvE = Ema(smvE, PStd(typBuf, AtfLen), AtfSmooth);
            clBuf.Add(c);
            if (clBuf.Count >= SmaLen) { double s = 0; for (int k = clBuf.Count - SmaLen; k < clBuf.Count; k++) s += clBuf[k]; smaV = s / SmaLen; } else smaV = double.NaN;
            wfE = Ema(wfE, c, WaeFast); wsE = Ema(wsE, c, WaeSlow);
            double macd = wfE - wsE; double t1 = double.IsNaN(prevMacd) ? 0.0 : (macd - prevMacd) * WaeSens; prevMacd = macd;
            double up = t1 >= 0 ? t1 : 0.0, dn = t1 < 0 ? -t1 : 0.0;
            double expl = 2.0 * WaeBbMult * PStd(clBuf, WaeBb);
            waeLost = !(up > expl || dn > expl);
        }

        // ------------------------------------------------------------------ rolling-history helpers (session P&L panel)
        void PushCappedF(List<double> hist, double val, ref double rollTotal)
        {
            hist.Add(val);
            double shiftedOut = 0.0; bool shifted = false;
            if (hist.Count > RollingHistoryDays) { shiftedOut = hist[0]; hist.RemoveAt(0); shifted = true; }
            rollTotal += val - (shifted ? shiftedOut : 0.0);
        }
        void PushCappedI(List<int> hist, int val, ref int rollTotal)
        {
            hist.Add(val);
            int shiftedOut = 0; bool shifted = false;
            if (hist.Count > RollingHistoryDays) { shiftedOut = hist[0]; hist.RemoveAt(0); shifted = true; }
            rollTotal += val - (shifted ? shiftedOut : 0);
        }

        // ------------------------------------------------------------------ entry/stop/tp1/tp2/breakeven chart lines
        void DrawEntryLines(double entryPx, double stopPx, double tp1Px, double tp2Px)
        {
            Draw.HorizontalLine(this, "ftkaLnEntry", entryPx, EntryLineColor);
            Draw.Text(this, "ftkaLbEntry", "Entry " + entryPx.ToString("F2"), -3, entryPx, EntryLineColor);
            Draw.HorizontalLine(this, "ftkaLnStop", stopPx, StopLineColor);
            Draw.Text(this, "ftkaLbStop", "Stop " + stopPx.ToString("F2"), -3, stopPx, StopLineColor);
            if (!double.IsNaN(tp1Px))
            {
                Draw.HorizontalLine(this, "ftkaLnTp1", tp1Px, Tp1LineColor);
                Draw.Text(this, "ftkaLbTp1", "TP1 " + tp1Px.ToString("F2"), -3, tp1Px, Tp1LineColor);
            }
            if (!double.IsNaN(tp2Px))
            {
                Draw.HorizontalLine(this, "ftkaLnTp2", tp2Px, Tp2LineColor);
                Draw.Text(this, "ftkaLbTp2", "TP2 " + tp2Px.ToString("F2"), -3, tp2Px, Tp2LineColor);
            }
        }

        void DrawBreakeven(double bePx)
        {
            RemoveDrawObject("ftkaLnStop"); RemoveDrawObject("ftkaLbStop");
            RemoveDrawObject("ftkaLnTp1"); RemoveDrawObject("ftkaLbTp1");
            Draw.HorizontalLine(this, "ftkaLnBe", bePx, BeLineColor);
            Draw.Text(this, "ftkaLbBe", "Breakeven " + bePx.ToString("F2"), -3, bePx, BeLineColor);
        }

        void ClearEntryLines()
        {
            RemoveDrawObject("ftkaLnEntry"); RemoveDrawObject("ftkaLbEntry");
            RemoveDrawObject("ftkaLnStop"); RemoveDrawObject("ftkaLbStop");
            RemoveDrawObject("ftkaLnTp1"); RemoveDrawObject("ftkaLbTp1");
            RemoveDrawObject("ftkaLnTp2"); RemoveDrawObject("ftkaLbTp2");
            RemoveDrawObject("ftkaLnBe"); RemoveDrawObject("ftkaLbBe");
        }

        // ------------------------------------------------------------------ outcome / leg labels with vertical de-overlap
        double PlaceLabelY(bool above, double baseY, double step)
        {
            double chosen = baseY;
            for (int lvl = 0; lvl < 15; lvl++)
            {
                double testY = above ? baseY + lvl * step : baseY - lvl * step;
                bool clash = false;
                for (int k = 0; k < labelAnchors.Count; k++)
                    if (CurrentBar - (int)labelAnchors[k][0] <= LabelDodgeBars && Math.Abs(testY - labelAnchors[k][1]) < step)
                    { clash = true; break; }
                chosen = testY;
                if (!clash) break;
            }
            labelAnchors.Add(new double[] { CurrentBar, chosen });
            for (int k = labelAnchors.Count - 1; k >= 0; k--)
                if (CurrentBar - (int)labelAnchors[k][0] > LabelDodgeBars * 3) labelAnchors.RemoveAt(k);
            return chosen;
        }

        void DrawLabel(string tag, string text, bool above, Brush area)
        {
            double baseOff = TradeLabelOffsetTicks * tick;
            double step = Math.Max(TradeLabelOffsetTicks, 8) * tick;
            double baseY = above ? High[0] + baseOff : Low[0] - baseOff;
            double yy = PlaceLabelY(above, baseY, step);
            Draw.Text(this, tag, false, text, 0, yy, 0, Brushes.White, _labelFont, System.Windows.TextAlignment.Center, Brushes.Transparent, area, 80);
        }

        static bool IsExitName(string n) { return n == "ftkaTP1" || n == "ftkaTP2" || n == "ftkaSL" || n.StartsWith("ftkaX_"); }
        static string LegWord(string n)
        {
            if (n == "ftkaTP1") return "TP1";
            if (n == "ftkaTP2") return "TP2";
            if (n == "ftkaSL") return "Stop";
            if (n == "ftkaX_SessionEnd") return "Session";
            if (n == "ftkaX_NtwFlatten") return "No-Trade";
            if (n == "ftkaX_CapFlatten") return "Cap";
            if (n == "ftkaX_WAEExit") return "WAE";
            return "Exit";
        }

        // ------------------------------------------------------------------ orders
        const int EntrySideSafetyTicks = 3;

        bool IsEntrySideValid(int dir, double stopPrice)
        {
            double reference = dir == 1
                ? (GetCurrentAsk(0) > 0 ? GetCurrentAsk(0) : Close[0])
                : (GetCurrentBid(0) > 0 ? GetCurrentBid(0) : Close[0]);
            double buffer = tick * EntrySideSafetyTicks;
            return dir == 1 ? stopPrice > reference + buffer : stopPrice < reference - buffer;
        }

        bool IsStopSideValid(int dir, double stopPrice)
        {
            double reference = dir == 1
                ? (GetCurrentBid(0) > 0 ? GetCurrentBid(0) : Close[0])
                : (GetCurrentAsk(0) > 0 ? GetCurrentAsk(0) : Close[0]);
            double buffer = tick * EntrySideSafetyTicks;
            return dir == 1 ? stopPrice < reference - buffer : stopPrice > reference + buffer;
        }

        void ArmEntry(int dir, double px, int bar)
        {
            CancelEntry();
            pendDir = dir; pendPx = RT(px); pendBar = bar;
            if (!IsEntrySideValid(dir, pendPx)) { pendBar = -1; return; }
            pendName = "ftkaE" + (++ordSeq);
            try { entryOrd = SubmitOrderUnmanaged(0, dir == 1 ? OrderAction.Buy : OrderAction.SellShort, OrderType.StopMarket, Contracts, 0, pendPx, "", pendName); }
            catch (Exception) { entryOrd = null; pendBar = -1; }
        }

        void CancelEntry()
        {
            if (Live(entryOrd)) CancelOrder(entryOrd);
            entryOrd = null; pendBar = -1;
        }

        void CancelExits()
        {
            if (Live(stopOrd)) CancelOrder(stopOrd);
            if (Live(tp1Ord)) CancelOrder(tp1Ord);
            if (Live(tp2Ord)) CancelOrder(tp2Ord);
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order.Name == "ftkaSL") stopOrd = order; else if (order.Name == "ftkaTP1") tp1Ord = order; else if (order.Name == "ftkaTP2") tp2Ord = order;
            else if (order.Name.StartsWith("ftkaE")) { if (orderState == OrderState.Working || orderState == OrderState.Accepted || orderState == OrderState.Submitted) entryOrd = order; }
            if (orderState == OrderState.Rejected && order.Name.StartsWith("ftka") && !order.Name.StartsWith("ftkaE") && RealDir != 0)
            {
                Print("SneakyFtkaTrendFollower: exit order " + order.Name + " rejected (" + nativeError + ") - flattening");
                CancelExits();
                SubmitOrderUnmanaged(0, RealDir == 1 ? OrderAction.Sell : OrderAction.BuyToCover, OrderType.Market, Position.Quantity, 0, 0, "", "ftkaX_rej");
            }
            else if (orderState == OrderState.Rejected && order.Name.StartsWith("ftkaE"))
            {
                Print("SneakyFtkaTrendFollower: entry order " + order.Name + " rejected (" + nativeError + ") - will re-arm next bar");
                entryOrd = null; pendBar = -1;
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            Order o = execution.Order; if (o == null || o.OrderState != OrderState.Filled) return;

            // per-leg exit label, drawn as each leg fills, coloured by that leg's own $ result
            if (ShowLegLabels && IsExitName(o.Name) && !double.IsNaN(entryAvg) && tradeDir != 0)
            {
                double legPnl = (price - entryAvg) * tradeDir * quantity * pv;
                bool legWin = legPnl >= 0;
                string txt = LegWord(o.Name) + " " + (legWin ? "+" : "") + legPnl.ToString("F2");
                DrawLabel("ftkaLeg" + CurrentBar + "_" + o.Name, txt, legWin, legWin ? Brushes.SeaGreen : Brushes.Firebrick);
            }

            if (o.Name.StartsWith("ftkaE") && Position.MarketPosition != MarketPosition.Flat)
            {
                // entry filled: bracket it
                entryOrd = null; pendBar = -1; tp1Done = false;
                entryAvg = Position.AveragePrice; tradeDir = RealDir; int dir = tradeDir; int q = Position.Quantity;
                OrderAction act = dir == 1 ? OrderAction.Sell : OrderAction.BuyToCover;
                stopOrd = SubmitOrderUnmanaged(0, act, OrderType.StopMarket, q, 0, RT(entryAvg - dir * StopTicks * tick), "", "ftkaSL");
                if (UseTp1 && tp1Q > 0) tp1Ord = SubmitOrderUnmanaged(0, act, OrderType.Limit, ExitFullAtTp1 ? q : Math.Min(tp1Q, q), RT(entryAvg + dir * Tp1Ticks * tick), 0, "", "ftkaTP1");
                else if (UseTp2) tp2Ord = SubmitOrderUnmanaged(0, act, OrderType.Limit, q, RT(entryAvg + dir * Tp2Ticks * tick), 0, "", "ftkaTP2");

                cycleStartNetProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
                lastEvent = dir == 1 ? "Long Filled" : "Short Filled";
                if (PlotLevels)
                {
                    double tp1Px = UseTp1 ? RT(entryAvg + dir * Tp1Ticks * tick) : double.NaN;
                    double tp2Px = (UseTp2 || (UseTp1 && !ExitFullAtTp1)) ? RT(entryAvg + dir * Tp2Ticks * tick) : double.NaN;
                    DrawEntryLines(entryAvg, RT(entryAvg - dir * StopTicks * tick), tp1Px, tp2Px);
                }
            }
            else if (o.Name == "ftkaTP1" && Position.MarketPosition != MarketPosition.Flat)
            {
                // TP1 done: runner keeps going with the breakeven floor and TP2
                tp1Done = true; int dir = RealDir; int q = Position.Quantity;
                floorPx = RT(entryAvg + dir * (BreakevenOffsetTicks + FloorBufferTicks) * tick);
                double hard = entryAvg - dir * StopTicks * tick;
                double newStop = dir == 1 ? Math.Max(hard, floorPx) : Math.Min(hard, floorPx);
                if (Live(stopOrd) && IsStopSideValid(dir, newStop)) ChangeOrder(stopOrd, q, 0, RT(newStop));
                if (UseTp2) tp2Ord = SubmitOrderUnmanaged(0, dir == 1 ? OrderAction.Sell : OrderAction.BuyToCover, OrderType.Limit, q, RT(entryAvg + dir * Tp2Ticks * tick), 0, "", "ftkaTP2");

                lastEvent = "TP1 Hit";
                if (PlotLevels) DrawBreakeven(RT(newStop));
            }
            else if (Position.MarketPosition == MarketPosition.Flat)
            {
                CancelExits(); tp1Done = false; floorPx = double.NaN;
                if (PlotLevels) ClearEntryLines();

                if (!double.IsNaN(cycleStartNetProfit))
                {
                    double tradeProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - cycleStartNetProfit;
                    bool isBE = Math.Abs(tradeProfit) <= BreakevenThresholdUSD;
                    bool isWin = tradeProfit > BreakevenThresholdUSD;
                    bool isLoss = tradeProfit < -BreakevenThresholdUSD;
                    if (inNYNow) { if (isWin) nyWinsToday++; else if (isLoss) nyLossesToday++; else nyBEToday++; }
                    else if (inLondonNow) { if (isWin) londonWinsToday++; else if (isLoss) londonLossesToday++; else londonBEToday++; }
                    else if (inAsiaNow) { if (isWin) asiaWinsToday++; else if (isLoss) asiaLossesToday++; else asiaBEToday++; }

                    string word = isWin ? "WIN" : isBE ? "BE" : "LOSS";
                    lastEvent = word + " (" + (tradeProfit >= 0 ? "+" : "") + tradeProfit.ToString("F2") + ")";
                    if (ShowTradeLabels)
                    {
                        Brush area = isWin ? Brushes.SeaGreen : isBE ? Brushes.Gray : Brushes.Firebrick;
                        DrawLabel("ftkaOutcome" + CurrentBar, lastEvent, !isLoss, area);
                    }
                    cycleStartNetProfit = double.NaN;
                }
                tradeDir = 0;
            }
        }

        // ------------------------------------------------------------------ OnRender session P&L panel (Direct2D)
        SharpDX.Direct2D1.SolidColorBrush Sb(float r, float g, float b, float a)
        {
            return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(r, g, b, a));
        }

        void DisposeBrushes()
        {
            if (bgBr != null) { bgBr.Dispose(); bgBr = null; }
            if (hdrBr != null) { hdrBr.Dispose(); hdrBr = null; }
            if (liveBr != null) { liveBr.Dispose(); liveBr = null; }
            if (txtBr != null) { txtBr.Dispose(); txtBr = null; }
            if (dimBr != null) { dimBr.Dispose(); dimBr = null; }
            if (posBr != null) { posBr.Dispose(); posBr = null; }
            if (negBr != null) { negBr.Dispose(); negBr = null; }
        }

        public override void OnRenderTargetChanged()
        {
            DisposeBrushes();
            if (RenderTarget == null) return;
            bgBr = Sb(0.07f, 0.08f, 0.11f, 0.92f);
            hdrBr = Sb(0.15f, 0.20f, 0.29f, 0.96f);
            liveBr = Sb(0.20f, 0.30f, 0.44f, 0.80f);
            txtBr = Sb(0.88f, 0.90f, 0.94f, 1f);
            dimBr = Sb(0.58f, 0.62f, 0.68f, 1f);
            posBr = Sb(0.30f, 0.82f, 0.47f, 1f);
            negBr = Sb(0.93f, 0.34f, 0.34f, 1f);
        }

        void DrawT(string s, float x, float y, float w, SharpDX.Direct2D1.Brush b, SharpDX.DirectWrite.TextFormat f)
        {
            if (string.IsNullOrEmpty(s) || b == null || f == null) return;
            RenderTarget.DrawText(s, f, new SharpDX.RectangleF(x, y, w, 20f), b);
        }

        static string Money(double v) { return (v >= 0 ? "+" : "") + v.ToString("F0"); }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (!ShowPnlTable || RenderTarget == null || ChartPanel == null || bgBr == null || fNorm == null) return;
            try { RenderPanel(); }
            catch (Exception ex) { if (!_panelErrLogged) { _panelErrLogged = true; Print("SneakyFtkaTrendFollower panel render error: " + ex.Message); } }
        }

        void RenderPanel()
        {
            float rowH = 18f, pad = 10f, w = 340f;
            float x = ChartPanel.X + ChartPanel.W - w - 14f;
            float y = ChartPanel.Y + 14f;
            int rowCount = 11;
            float h = pad * 2 + rowH * rowCount;

            RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, w, h), bgBr);
            RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, w, rowH + 2f), hdrBr);

            float cL = x + pad;
            float cT = x + 150f;
            float c30 = x + 225f;
            float cWL = x + 288f;
            float ty = y + 3f;

            DrawT("FTKA SESSION P&L", cL, ty, w - 2 * pad, txtBr, fBold); ty += rowH + 2f;
            DrawT("Today", cT, ty, 70f, dimBr, fSmall);
            DrawT(RollingHistoryDays + "d", c30, ty, 60f, dimBr, fSmall);
            DrawT("W/L", cWL, ty, 50f, dimBr, fSmall); ty += rowH;

            ty = SessRow("Asia", asiaToday, asiaRollTotal, asiaRollW, asiaRollL, inAsiaNow, cL, cT, c30, cWL, ty, rowH, x, w);
            ty = SessRow("London", londonToday, londonRollTotal, londonRollW, londonRollL, inLondonNow, cL, cT, c30, cWL, ty, rowH, x, w);
            ty = SessRow("NY", nyToday, nyRollTotal, nyRollW, nyRollL, inNYNow, cL, cT, c30, cWL, ty, rowH, x, w);

            double todayTot = asiaToday + londonToday + nyToday;
            double rollTot = asiaRollTotal + londonRollTotal + nyRollTotal;
            DrawT("TOTAL", cL, ty, 80f, txtBr, fBold);
            DrawT(Money(todayTot), cT, ty, 70f, todayTot >= 0 ? posBr : negBr, fBold);
            DrawT(Money(rollTot), c30, ty, 60f, rollTot >= 0 ? posBr : negBr, fBold); ty += rowH + 3f;

            DrawT("Max DD Today: " + dayMaxDrawdown.ToString("F0"), cL, ty, w - 2 * pad, dimBr, fNorm); ty += rowH;
            int wT = asiaWinsToday + londonWinsToday + nyWinsToday, lT = asiaLossesToday + londonLossesToday + nyLossesToday;
            int wR = asiaRollW + londonRollW + nyRollW, lR = asiaRollL + londonRollL + nyRollL;
            string wrT = (wT + lT) > 0 ? (100.0 * wT / (wT + lT)).ToString("F0") + "%" : "-";
            string wrR = (wR + lR) > 0 ? (100.0 * wR / (wR + lR)).ToString("F0") + "%" : "-";
            DrawT("Win Rate:  today " + wrT + "    " + RollingHistoryDays + "d " + wrR, cL, ty, w - 2 * pad, txtBr, fNorm); ty += rowH;

            string posTxt = Position.MarketPosition == MarketPosition.Long ? "LONG" : Position.MarketPosition == MarketPosition.Short ? "SHORT" : "FLAT";
            var pBr = Position.MarketPosition == MarketPosition.Long ? posBr : Position.MarketPosition == MarketPosition.Short ? negBr : dimBr;
            DrawT("Position:", cL, ty, 70f, dimBr, fNorm);
            DrawT(posTxt, cL + 62f, ty, 90f, pBr, fBold); ty += rowH;

            DrawT("Last: " + lastEvent, cL, ty, w - 2 * pad, dimBr, fSmall);
        }

        float SessRow(string name, double today, double roll, int w_, int l_, bool liveNow, float cL, float cT, float c30, float cWL, float ty, float rowH, float x, float panelW)
        {
            if (liveNow) RenderTarget.FillRectangle(new SharpDX.RectangleF(x + 2f, ty - 1f, panelW - 4f, rowH), liveBr);
            DrawT(name + (liveNow ? " •" : ""), cL, ty, 80f, liveNow ? txtBr : dimBr, fNorm);
            DrawT(Money(today), cT, ty, 70f, today >= 0 ? posBr : negBr, fNorm);
            DrawT(Money(roll), c30, ty, 60f, roll >= 0 ? posBr : negBr, fNorm);
            DrawT(w_ + "/" + l_, cWL, ty, 50f, dimBr, fSmall);
            return ty + rowH;
        }

        #region Properties
        [NinjaScriptProperty, Range(1, 1000), Display(Name = "Contracts", GroupName = "1. Entry", Order = 0)] public int Contracts { get; set; }
        [NinjaScriptProperty, Range(0, 50), Display(Name = "Entry buffer (ticks)", GroupName = "1. Entry", Order = 1)] public int EntryBufferTicks { get; set; }
        [NinjaScriptProperty, Range(0, 20), Display(Name = "Trigger valid for N bars after the signal bar", GroupName = "1. Entry", Order = 2)] public int ExpireBars { get; set; }
        [NinjaScriptProperty, Display(Name = "Reversal bar entry", GroupName = "2. Entry types", Order = 0)] public bool UseReversal { get; set; }
        [NinjaScriptProperty, Range(1, 10), Display(Name = "Opposite candles required", GroupName = "2. Entry types", Order = 1)] public int ReversalBars { get; set; }
        [NinjaScriptProperty, Display(Name = "ATF colour-flip entry", GroupName = "2. Entry types", Order = 2)] public bool UseAtfEntry { get; set; }
        [NinjaScriptProperty, Display(Name = "SMA pullback entry", GroupName = "2. Entry types", Order = 3)] public bool UsePullback { get; set; }
        [NinjaScriptProperty, Range(1, 20), Display(Name = "Max pullback entries per ATF phase", GroupName = "2. Entry types", Order = 4)] public int MaxPullbacks { get; set; }
        [NinjaScriptProperty, Display(Name = "Pullback: proximity instead of touch", GroupName = "2. Entry types", Order = 5)] public bool PullbackProximity { get; set; }
        [NinjaScriptProperty, Range(1, 500), Display(Name = "Pullback proximity (ticks)", GroupName = "2. Entry types", Order = 6)] public int ProximityTicks { get; set; }
        [NinjaScriptProperty, Range(1, 2000), Display(Name = "Hard stop (ticks)", GroupName = "3. Exits", Order = 0)] public int StopTicks { get; set; }
        [NinjaScriptProperty, Display(Name = "Use TP1", GroupName = "3. Exits", Order = 1)] public bool UseTp1 { get; set; }
        [NinjaScriptProperty, Range(1, 2000), Display(Name = "TP1 (ticks)", GroupName = "3. Exits", Order = 2)] public int Tp1Ticks { get; set; }
        [NinjaScriptProperty, Range(1, 100), Display(Name = "TP1 quantity %", GroupName = "3. Exits", Order = 3)] public int Tp1Pct { get; set; }
        [NinjaScriptProperty, Display(Name = "Exit FULL position at TP1", GroupName = "3. Exits", Order = 4)] public bool ExitFullAtTp1 { get; set; }
        [NinjaScriptProperty, Display(Name = "Use TP2 (runner)", GroupName = "3. Exits", Order = 5)] public bool UseTp2 { get; set; }
        [NinjaScriptProperty, Range(1, 5000), Display(Name = "TP2 (ticks)", GroupName = "3. Exits", Order = 6)] public int Tp2Ticks { get; set; }
        [NinjaScriptProperty, Range(0, 200), Display(Name = "Breakeven offset (ticks)", GroupName = "3. Exits", Order = 7)] public int BreakevenOffsetTicks { get; set; }
        [NinjaScriptProperty, Range(0, 200), Display(Name = "Runner floor slippage buffer (ticks)", GroupName = "3. Exits", Order = 8)] public int FloorBufferTicks { get; set; }
        [NinjaScriptProperty, Display(Name = "Exit runner when WAE momentum is lost (2 bars)", GroupName = "3. Exits", Order = 9)] public bool WaeExitRunner { get; set; }
        [NinjaScriptProperty, Display(Name = "Require active WAE momentum for entries", GroupName = "3. Exits", Order = 10)] public bool WaeRequireEntries { get; set; }
        [NinjaScriptProperty, Range(1, 500), Display(Name = "SMA length", GroupName = "4. Filters", Order = 0)] public int SmaLen { get; set; }
        [NinjaScriptProperty, Display(Name = "SMA filter", GroupName = "4. Filters", Order = 1)] public bool UseSmaFilter { get; set; }
        [NinjaScriptProperty, Display(Name = "ATF mid-line filter", GroupName = "4. Filters", Order = 2)] public bool UseAtfFilter { get; set; }
        [NinjaScriptProperty, Range(1, 200), Display(Name = "ATF main length", GroupName = "5. ATF / WAE", Order = 0)] public int AtfLen { get; set; }
        [NinjaScriptProperty, Range(1, 200), Display(Name = "ATF smoothing length", GroupName = "5. ATF / WAE", Order = 1)] public int AtfSmooth { get; set; }
        [NinjaScriptProperty, Range(0.01, 10.0), Display(Name = "ATF sensitivity", GroupName = "5. ATF / WAE", Order = 2)] public double AtfSens { get; set; }
        [NinjaScriptProperty, Range(1, 1000), Display(Name = "WAE sensitivity", GroupName = "5. ATF / WAE", Order = 3)] public int WaeSens { get; set; }
        [NinjaScriptProperty, Range(1, 200), Display(Name = "WAE fast EMA", GroupName = "5. ATF / WAE", Order = 4)] public int WaeFast { get; set; }
        [NinjaScriptProperty, Range(1, 400), Display(Name = "WAE slow EMA", GroupName = "5. ATF / WAE", Order = 5)] public int WaeSlow { get; set; }
        [NinjaScriptProperty, Range(1, 200), Display(Name = "WAE Bollinger length", GroupName = "5. ATF / WAE", Order = 6)] public int WaeBb { get; set; }
        [NinjaScriptProperty, Range(0.1, 10.0), Display(Name = "WAE Bollinger multiplier", GroupName = "5. ATF / WAE", Order = 7)] public double WaeBbMult { get; set; }
        [NinjaScriptProperty, Display(Name = "Use session filter (ET)", GroupName = "6. Session", Order = 0)] public bool UseSession { get; set; }
        [NinjaScriptProperty, Range(0, 2359), Display(Name = "Session start (HHMM ET)", GroupName = "6. Session", Order = 1)] public int SessionStartHHMM { get; set; }
        [NinjaScriptProperty, Range(0, 2359), Display(Name = "Session end / flatten (HHMM ET)", GroupName = "6. Session", Order = 2)] public int SessionEndHHMM { get; set; }
        [NinjaScriptProperty, Display(Name = "No-trade window", GroupName = "6. Session", Order = 3)] public bool NoTrade { get; set; }
        [NinjaScriptProperty, Range(0, 2359), Display(Name = "No-trade start (HHMM ET)", GroupName = "6. Session", Order = 4)] public int NoTradeStartHHMM { get; set; }
        [NinjaScriptProperty, Range(0, 2359), Display(Name = "No-trade end (HHMM ET)", GroupName = "6. Session", Order = 5)] public int NoTradeEndHHMM { get; set; }
        [NinjaScriptProperty, Display(Name = "Flatten open position in the no-trade window", GroupName = "6. Session", Order = 6)] public bool FlattenInNoTrade { get; set; }
        [NinjaScriptProperty, Range(-12, 12), Display(Name = "Hours to add to chart time to get ET", GroupName = "6. Session", Order = 7)] public int EtOffsetHours { get; set; }
        [NinjaScriptProperty, Display(Name = "Daily profit cap", GroupName = "7. Daily caps (reset 18:00 ET)", Order = 0)] public bool UseProfitCap { get; set; }
        [NinjaScriptProperty, Range(1, 1000000), Display(Name = "Profit cap ($)", GroupName = "7. Daily caps (reset 18:00 ET)", Order = 1)] public double ProfitCap { get; set; }
        [NinjaScriptProperty, Display(Name = "Daily loss cap", GroupName = "7. Daily caps (reset 18:00 ET)", Order = 2)] public bool UseLossCap { get; set; }
        [NinjaScriptProperty, Range(1, 1000000), Display(Name = "Loss cap ($)", GroupName = "7. Daily caps (reset 18:00 ET)", Order = 3)] public double LossCap { get; set; }
        [NinjaScriptProperty, Display(Name = "Flatten open position when a cap is hit", GroupName = "7. Daily caps (reset 18:00 ET)", Order = 4)] public bool FlattenOnCap { get; set; }
        [NinjaScriptProperty, Display(Name = "Allow longs", GroupName = "8. Direction", Order = 0)] public bool AllowLongs { get; set; }
        [NinjaScriptProperty, Display(Name = "Allow shorts", GroupName = "8. Direction", Order = 1)] public bool AllowShorts { get; set; }
        [NinjaScriptProperty, Display(Name = "Draw signal arrows", GroupName = "9. Display", Order = 0)] public bool ShowSignals { get; set; }

        [NinjaScriptProperty, Display(Name = "Show ATF line on chart", GroupName = "9. Display", Order = 1)] public bool ShowAtfOnChart { get; set; }
        [NinjaScriptProperty, Display(Name = "Show ATF zone (upper/lower)", GroupName = "9. Display", Order = 2)] public bool ShowAtfZone { get; set; }
        [NinjaScriptProperty, Display(Name = "Colour bars by ATF trend", GroupName = "9. Display", Order = 3)] public bool ColorBarsByAtf { get; set; }
        [XmlIgnore, Display(Name = "ATF bullish colour", GroupName = "9. Display", Order = 4)] public Brush AtfBullColor { get; set; }
        [Browsable(false)] public string AtfBullColorSerializable { get { return Serialize.BrushToString(AtfBullColor); } set { AtfBullColor = Serialize.StringToBrush(value); } }
        [XmlIgnore, Display(Name = "ATF bearish colour", GroupName = "9. Display", Order = 5)] public Brush AtfBearColor { get; set; }
        [Browsable(false)] public string AtfBearColorSerializable { get { return Serialize.BrushToString(AtfBearColor); } set { AtfBearColor = Serialize.StringToBrush(value); } }
        [NinjaScriptProperty, Display(Name = "Show SMA on chart", GroupName = "9. Display", Order = 6)] public bool ShowSmaOnChart { get; set; }
        [XmlIgnore, Display(Name = "SMA line colour", GroupName = "9. Display", Order = 7)] public Brush SmaLineColor { get; set; }
        [Browsable(false)] public string SmaLineColorSerializable { get { return Serialize.BrushToString(SmaLineColor); } set { SmaLineColor = Serialize.StringToBrush(value); } }
        [NinjaScriptProperty, Display(Name = "Plot entry/stop/TP1/TP2/breakeven lines", GroupName = "9. Display", Order = 8)] public bool PlotLevels { get; set; }
        [XmlIgnore, Display(Name = "Entry line colour", GroupName = "9. Display", Order = 9)] public Brush EntryLineColor { get; set; }
        [Browsable(false)] public string EntryLineColorSerializable { get { return Serialize.BrushToString(EntryLineColor); } set { EntryLineColor = Serialize.StringToBrush(value); } }
        [XmlIgnore, Display(Name = "Stop line colour", GroupName = "9. Display", Order = 10)] public Brush StopLineColor { get; set; }
        [Browsable(false)] public string StopLineColorSerializable { get { return Serialize.BrushToString(StopLineColor); } set { StopLineColor = Serialize.StringToBrush(value); } }
        [XmlIgnore, Display(Name = "TP1 line colour", GroupName = "9. Display", Order = 11)] public Brush Tp1LineColor { get; set; }
        [Browsable(false)] public string Tp1LineColorSerializable { get { return Serialize.BrushToString(Tp1LineColor); } set { Tp1LineColor = Serialize.StringToBrush(value); } }
        [XmlIgnore, Display(Name = "TP2 line colour", GroupName = "9. Display", Order = 12)] public Brush Tp2LineColor { get; set; }
        [Browsable(false)] public string Tp2LineColorSerializable { get { return Serialize.BrushToString(Tp2LineColor); } set { Tp2LineColor = Serialize.StringToBrush(value); } }
        [XmlIgnore, Display(Name = "Breakeven line colour", GroupName = "9. Display", Order = 13)] public Brush BeLineColor { get; set; }
        [Browsable(false)] public string BeLineColorSerializable { get { return Serialize.BrushToString(BeLineColor); } set { BeLineColor = Serialize.StringToBrush(value); } }
        [NinjaScriptProperty, Display(Name = "Show WIN/LOSS/BE trade labels", GroupName = "9. Display", Order = 14)] public bool ShowTradeLabels { get; set; }
        [NinjaScriptProperty, Display(Name = "Show per-leg exit labels (TP1/TP2/Stop)", GroupName = "9. Display", Order = 15)] public bool ShowLegLabels { get; set; }
        [NinjaScriptProperty, Range(0, 500), Display(Name = "Trade label offset (ticks)", GroupName = "9. Display", Order = 16)] public int TradeLabelOffsetTicks { get; set; }
        [NinjaScriptProperty, Range(0, 100), Display(Name = "Label de-overlap window (bars)", GroupName = "9. Display", Order = 17)] public int LabelDodgeBars { get; set; }
        [NinjaScriptProperty, Range(0, 100000), Display(Name = "Breakeven threshold ($)", GroupName = "9. Display", Order = 18)] public double BreakevenThresholdUSD { get; set; }
        [NinjaScriptProperty, Display(Name = "Show Session P&L panel", GroupName = "9. Display", Order = 19)] public bool ShowPnlTable { get; set; }
        [NinjaScriptProperty, Range(1, 365), Display(Name = "Rolling history (days)", GroupName = "9. Display", Order = 20)] public int RollingHistoryDays { get; set; }
        #endregion
    }
}
