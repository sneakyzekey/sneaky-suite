#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// SneakyFtkaTrendFollower
// NinjaTrader 8 STRATEGY port of the TradingView Pine v6 strategy "FTKA Trend Follower" (revision with the stopLoss alert field;
// the trading rules are identical to the earlier revision).
//
//   Entry types (each switchable): 1) reversal bar after N opposite-coloured candles, 2) first green/red candle after an ATF
//   (Adaptive Trend Flow) colour flip, 3) SMA pullback (touch, then a candle closing back in the trend direction, max 3 per phase).
//   A signal on a bar close arms a STOP entry at that bar's high + buffer (long) / low - buffer (short), valid for the next
//   N bars, replaced by a newer same-side signal, cancelled at session end / no-trade windows.
//   Filters: SMA (close on the right side, and the SMA must not sit inside the ATF band), optional ATF mid-line filter,
//   optional "WAE momentum active" requirement.
//   Exits: hard stop (65 ticks), TP1 (75% of the contracts at 44 ticks), runner TP2 (106 ticks) with a breakeven floor
//   (entry +/- (offset + buffer) ticks) after TP1, runner exit when WAE momentum is lost for 2 bars (and price is on the profit side
//   of the floor), flatten at session end and in the no-trade window, optional daily profit / loss cap.
//
// Uses real unmanaged orders; signals are evaluated on bar close (Calculate.OnBarClose). Times are Eastern: set
// "Hours to add to chart time to get ET" if your PC is not on Eastern time.
//
// In my own Python re-implementation on Jan-Oct 2026 NQ data it was profitable on 15m and 30m charts and lost money on 1-5m charts
// (and the edge shrinks a lot above ~4 ticks of slippage). That backtest is a re-implementation, not a TradingView cross-check.
// Forward-test on SIM first. Not a recommendation.

namespace NinjaTrader.NinjaScript.Strategies
{
    public class SneakyFtkaTrendFollower : Strategy
    {
        // ------------------------------------------------------------------ series state
        double tick, pv;
        int tfMin = 15;
        List<double> typ = new List<double>(), cl = new List<double>();
        double fastE = double.NaN, slowE = double.NaN, smvE = double.NaN, wfE = double.NaN, wsE = double.NaN, prevMacd = double.NaN;
        List<double> typBuf = new List<double>(), clBuf = new List<double>();
        double basis, atfUp, atfLo, atfLevel = double.NaN, smaV = double.NaN;
        int atfTrend; bool prevBull, prevBear, waitG, waitR, pbG, pbR; int pbCount, waeStreak;
        bool waeLost;

        // orders / position
        Order entryOrd, stopOrd, tp1Ord, tp2Ord;
        int pendDir, pendBar = -1; double pendPx;
        int ordSeq; string pendName = "";
        bool tp1Done, wantExit; string exitWhy = "";
        double entryAvg, floorPx;
        int tp1Q, runQ;
        // daily cap
        double sessStartMtm = double.NaN; int capDay = -1; bool capHit, lossHit;
        // time
        int tOpen, tNext;
        bool prevInSess;
        List<string> tags = new List<string>();

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
            }
            else if (State == State.DataLoaded)
            {
                tick = Instrument.MasterInstrument.TickSize; pv = Instrument.MasterInstrument.PointValue;
                tfMin = BarsPeriod.BarsPeriodType == BarsPeriodType.Minute && BarsPeriod.Value > 0 ? BarsPeriod.Value : 15;
                tp1Q = !UseTp1 ? 0 : (ExitFullAtTp1 ? Contracts : Math.Max(1, Math.Min(Contracts, (int)Math.Round(Contracts * Tp1Pct / 100.0, MidpointRounding.AwayFromZero))));
                runQ = Contracts - tp1Q;
            }
        }

        static int Mins(int hhmm) { return (hhmm / 100) * 60 + hhmm % 100; }
        bool InSess(int m) { if (!UseSession) return true; int a = Mins(SessionStartHHMM), b = Mins(SessionEndHHMM); return a > b ? (m >= a || m < b) : (m >= a && m < b); }
        bool InNtw(int m) { return NoTrade && m >= Mins(NoTradeStartHHMM) && m < Mins(NoTradeEndHHMM); }
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

            // ---------- pending entry order management
            bool flat = Position.MarketPosition == MarketPosition.Flat;
            // a pending order that already filled or was cancelled leaves nothing to manage
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
                // bar-close exits (market, fill on the next bar open like the backtest)
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

        // ------------------------------------------------------------------ orders
        void ArmEntry(int dir, double px, int bar)
        {
            CancelEntry();
            pendDir = dir; pendPx = RT(px); pendBar = bar; pendName = "ftkaE" + (++ordSeq);
            entryOrd = SubmitOrderUnmanaged(0, dir == 1 ? OrderAction.Buy : OrderAction.SellShort, OrderType.StopMarket, Contracts, 0, pendPx, "", pendName);
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
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            Order o = execution.Order; if (o == null || o.OrderState != OrderState.Filled) return;
            if (o.Name.StartsWith("ftkaE") && Position.MarketPosition != MarketPosition.Flat)
            {
                // entry filled: bracket it
                entryOrd = null; pendBar = -1; tp1Done = false;
                entryAvg = Position.AveragePrice; int dir = RealDir; int q = Position.Quantity;
                OrderAction act = dir == 1 ? OrderAction.Sell : OrderAction.BuyToCover;
                stopOrd = SubmitOrderUnmanaged(0, act, OrderType.StopMarket, q, 0, RT(entryAvg - dir * StopTicks * tick), "", "ftkaSL");
                if (UseTp1 && tp1Q > 0) tp1Ord = SubmitOrderUnmanaged(0, act, OrderType.Limit, ExitFullAtTp1 ? q : Math.Min(tp1Q, q), RT(entryAvg + dir * Tp1Ticks * tick), 0, "", "ftkaTP1");
                else if (UseTp2) tp2Ord = SubmitOrderUnmanaged(0, act, OrderType.Limit, q, RT(entryAvg + dir * Tp2Ticks * tick), 0, "", "ftkaTP2");
            }
            else if (o.Name == "ftkaTP1" && Position.MarketPosition != MarketPosition.Flat)
            {
                // TP1 done: runner keeps going with the breakeven floor and TP2
                tp1Done = true; int dir = RealDir; int q = Position.Quantity;
                floorPx = RT(entryAvg + dir * (BreakevenOffsetTicks + FloorBufferTicks) * tick);
                double hard = entryAvg - dir * StopTicks * tick;
                double newStop = dir == 1 ? Math.Max(hard, floorPx) : Math.Min(hard, floorPx);
                if (Live(stopOrd)) ChangeOrder(stopOrd, q, 0, RT(newStop));
                if (UseTp2) tp2Ord = SubmitOrderUnmanaged(0, dir == 1 ? OrderAction.Sell : OrderAction.BuyToCover, OrderType.Limit, q, RT(entryAvg + dir * Tp2Ticks * tick), 0, "", "ftkaTP2");
            }
            else if (Position.MarketPosition == MarketPosition.Flat)
            {
                CancelExits(); tp1Done = false; floorPx = double.NaN;
            }
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
        #endregion
    }
}
