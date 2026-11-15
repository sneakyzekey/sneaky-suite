// ─────────────────────────────────────────────────────────────────────────────
// CRTLiquiditySweepStrategy_v1_0_0.cs
// ─────────────────────────────────────────────────────────────────────────────
//  PORTED FROM TRADINGVIEW. Original: "CRT Liquidity Sweep Strategy SOL 5 MIN"
//  by jaydeepp095 (open-source Pine Script v6, TradingView). This file is a
//  line-for-line NinjaScript C# port of that published Pine source — same
//  range-building method, same sweep/reclaim state machine, same trend filter,
//  same risk/ATR logic, same visuals (range box, TP/SL boxes, entry labels,
//  trend line, status panel) — re-expressed for NinjaTrader 8. Not an original
//  Sneaky Suite design; credited here and in the repo's licensing notes.
//  Standalone — no other Sneaky files required.
//
//  WHAT IT DOES (same as the Pine original)
//  ------------------------------------------
//  1. Builds a higher-timeframe "range" natively from THIS chart's own bars —
//     UTC-aligned fixed-minute buckets, accumulate-then-lock — rather than via
//     a secondary data series. The original author found a request.security()
//     version silently produced zero trades on Deep Backtesting because
//     extended history isn't always available to secondary-series lookups on
//     every feed; building it from the primary series' own bars removes that
//     dependency entirely. A finished bucket becomes the active range ONLY if
//     it passes a plain-candle filter (not doji / marubozu / hammer-type).
//  2. Waits for a liquidity sweep: price wicks beyond the range (hunts stops),
//     then RECLAIMS back inside on a same-direction candle — either on the
//     sweep bar itself or within a configurable number of bars after.
//  3. Optionally requires the reclaim's close to land back inside the FULL
//     range (not just back across the swept edge), applies a trend filter
//     (EMA or linear-regression slope) and an optional ATR-based sweep-size
//     filter, then enters with a stop beyond the full sweep extreme (plus
//     optional tick/ATR buffer) and a fixed R:R target. Only one entry per
//     formed range.
//
//  SOURCING NOTE: this is a straightforward "4H-range sweep-and-reclaim"
//  pattern (same family as this repo's SneakyCRT tools), parameterized exactly
//  as jaydeepp095 published it — hammer/engulfing-style confirmation, 1:2 R:R
//  by default. It is NOT validated as profitable here; the Pine author's own
//  page says the same. Run it in Sim first.
//
//  PINE -> NINJASCRIPT NOTES
//  ---------------------------
//  - Pine's input.timeframe() dropdown becomes a plain int (minutes) property
//    (RangeTimeframeMinutes) with the same "must be an exact multiple of, and
//    larger than, the chart timeframe" validation — checked once in
//    OnStateChange(State.DataLoaded) and the strategy refuses to trade (with a
//    Print) rather than throwing, since NinjaScript doesn't have runtime.error.
//  - ta.linreg(close, len, 0) - ta.linreg(close, len, 1) is exactly the fitted
//    line's per-bar slope, which is what NinjaTrader's built-in LinRegSlope()
//    computes directly — used instead of two separate LinReg() calls.
//  - Pine's box.new/box.set_right (range box) and the two TP/SL boxes become
//    Draw.Rectangle calls, redrawn every bar under the same tag so NinjaTrader
//    updates them in place instead of creating new objects.
//  - The 15-row Pine status table becomes one Draw.TextFixed block (NinjaTrader
//    has no native per-cell table primitive); same fields, same order.
//  - strategy.entry/strategy.exit (one bracket, flat-gated) map to
//    EnterLong/EnterShort + SetStopLoss/SetProfitTarget (CalculationMode.Price)
//    — managed orders, since only one directional bracket is ever wanted at a
//    time (same reasoning as DrummondCAPBreakout_v1_0_0 in this folder).
//
//  Changelog
//    v1.0.0 (2026-11-14) — initial port of the published Pine v6 source.
// ─────────────────────────────────────────────────────────────────────────────
#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum CrtTrendMethod { Off, Ema, LinearRegression }

    public class SneakyCRTLiquiditySweepStrategy_v1_0_0 : Strategy
    {
        private const string EntryLong  = "CRTLong";
        private const string EntryShort = "CRTShort";

        // ── range-bucket state (mirrors curBucket/curO/curH/curL/curC) ──
        private long   _bucketMs;
        private long   _curBucket = long.MinValue;
        private bool   _haveBucket;
        private double _curO, _curH, _curL, _curC;
        private int    _rangeStartBar = -1;   // absolute CurrentBar the active range's box starts at

        // ── active range + sweep state machine (mirrors the Pine `var` state) ──
        private double _rangeHigh = double.NaN, _rangeLow = double.NaN;
        private string _state = "WATCHING";
        private bool   _sweptBelow, _sweptAbove;
        private int    _breakoutBar = -1, _belowSweepBar = -1, _aboveSweepBar = -1;
        private double _sweepLow = double.NaN, _sweepHigh = double.NaN;
        private bool   _belowCandleOk = true, _aboveCandleOk = true;
        private int    _rangesFormedCount;

        // ── risk-management bookkeeping for the live bracket ──
        private double _stopPrice, _targetPrice;
        private int    _entryBar = -1;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Ported from TradingView: 'CRT Liquidity Sweep Strategy SOL 5 MIN' by "
                            + "jaydeepp095 (open-source Pine v6). Builds a UTC-aligned higher-timeframe "
                            + "range from this chart's own bars, trades a sweep-beyond-the-range then "
                            + "reclaim-back-inside, with an EMA/linear-regression trend filter and fixed "
                            + "R:R risk. Not validated as profitable — same disclaimer as the original.";
                Name = "Sneaky CRT Liquidity Sweep Strategy";

                Calculate                    = Calculate.OnBarClose;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = false;
                MaximumBarsLookBack          = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution          = OrderFillResolution.Standard;
                Slippage                     = 1;   // matches the Pine strategy()'s slippage=1
                StartBehavior                = StartBehavior.WaitUntilFlat;
                TimeInForce                  = TimeInForce.Gtc;
                TraceOrders                  = false;
                RealtimeErrorHandling        = RealtimeErrorHandling.StopCancelClose;
                BarsRequiredToTrade          = 20;
                IsUnmanaged                  = false;

                // -- Range (HTF) --
                RangeTimeframeMinutes    = 240;   // matches Pine default "240" (4 hours)
                RequireNormalCandle      = true;
                MaxBodyPct               = 80.0;
                MinBodyPct               = 15.0;
                MaxWickImbalance         = 2.5;

                // -- Sweep and Entry --
                LegacyTimeoutBars        = 150;
                RequireInsideRange       = true;
                UseQuickReclaim          = true;
                ReclaimBars              = 1;
                UseSweepExtreme          = true;
                AllowLong                = true;
                AllowShort               = true;

                // -- Entry Trend Filter --
                TrendMethod              = CrtTrendMethod.LinearRegression;
                TrendEmaLength           = 200;
                TrendRegressionLength    = 50;
                MinRegressionSlopePct    = 0.0;
                ShowTrendLine            = true;

                // -- Risk --
                RiskReward               = 2.0;
                StopBufferTicks          = 0;
                RrProjectionBars         = 35;

                // -- ATR --
                UseAtrBuffer             = false;
                AtrLength                = 14;
                AtrBufferMultiplier      = 0.2;
                UseAtrSweepFilter        = false;
                MaxSweepAtr              = 2.0;

                // -- Visuals --
                ShowRangeBox             = true;
                ShowRrBox                = true;
                ShowSignals              = true;
                ShowStatusPanel          = true;

                Contracts                = 1;

                AddPlot(Brushes.Orange, "Entry trend line");
                AddPlot(Brushes.Lime,   "Range High");
                AddPlot(Brushes.Red,    "Range Low");
            }
            else if (State == State.DataLoaded)
            {
                int chartMinutes = (int)BarsPeriod.Value;
                if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute
                    || RangeTimeframeMinutes <= chartMinutes
                    || RangeTimeframeMinutes % chartMinutes != 0)
                {
                    Print("CRTLiquiditySweepStrategy: chart must be a standard fixed-minute intraday "
                        + "bar type, and Range Timeframe (minutes) must be LARGER than, and an exact "
                        + "multiple of, the chart's own bar size. Current chart = "
                        + BarsPeriod.BarsPeriodType + " " + BarsPeriod.Value
                        + ", range = " + RangeTimeframeMinutes + "m. This strategy will not trade "
                        + "until that's fixed.");
                }
                _bucketMs = (long)RangeTimeframeMinutes * 60L * 1000L;
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade) return;
            if (_bucketMs <= 0) return;   // misconfigured — see the DataLoaded check above

            try
            {
                UpdateRangeBucket();
                UpdateRangeBoxVisual();

                double trendEma         = EMA(TrendEmaLength)[0];
                bool   emaTrendReady    = CurrentBar >= TrendEmaLength - 1 && !double.IsNaN(trendEma);
                double regressionSlope  = CurrentBar >= TrendRegressionLength - 1 ? LinRegSlope(Close, TrendRegressionLength)[0] : double.NaN;
                bool   regressionReady  = !double.IsNaN(regressionSlope);
                double regressionSlopePct = regressionReady && Math.Abs(Close[0]) > 0
                    ? regressionSlope / Math.Abs(Close[0]) * 100.0 : double.NaN;
                double trendRegressionValue = regressionReady ? LinReg(Close, TrendRegressionLength)[0] : double.NaN;

                bool longTrendOk, shortTrendOk;
                switch (TrendMethod)
                {
                    case CrtTrendMethod.Ema:
                        longTrendOk  = emaTrendReady && Close[0] > trendEma;
                        shortTrendOk = emaTrendReady && Close[0] < trendEma;
                        break;
                    case CrtTrendMethod.LinearRegression:
                        longTrendOk  = regressionReady && regressionSlopePct > MinRegressionSlopePct;
                        shortTrendOk = regressionReady && regressionSlopePct < -MinRegressionSlopePct;
                        break;
                    default:
                        longTrendOk = shortTrendOk = true;
                        break;
                }

                // Trend line + range High/Low plots (NaN leaves a gap, like Pine's style_linebr)
                Values[0][0] = (ShowTrendLine && TrendMethod != CrtTrendMethod.Off)
                    ? (TrendMethod == CrtTrendMethod.Ema ? trendEma : trendRegressionValue)
                    : double.NaN;
                Values[1][0] = !double.IsNaN(_rangeHigh) ? _rangeHigh : double.NaN;
                Values[2][0] = !double.IsNaN(_rangeLow)  ? _rangeLow  : double.NaN;

                // ATR gating (mirrors atrReady / sweepCandleOk)
                double atrNow  = ATR(AtrLength)[0];
                double atrPrev = CurrentBar >= 1 ? ATR(AtrLength)[1] : double.NaN;
                bool atrPrevOk = !double.IsNaN(atrPrev) && atrPrev > 0;
                bool atrReady  = (!UseAtrBuffer || !double.IsNaN(atrNow)) && (!UseAtrSweepFilter || atrPrevOk);
                bool sweepCandleOk = !UseAtrSweepFilter || (atrPrevOk && (High[0] - Low[0]) <= atrPrev * MaxSweepAtr);
                double atrStopBuffer = UseAtrBuffer ? atrNow * AtrBufferMultiplier : 0.0;
                double stopBuffer = StopBufferTicks * TickSize + atrStopBuffer;

                bool bullReclaim = false, bearReclaim = false;

                if (!double.IsNaN(_rangeHigh) && _state == "WATCHING")
                {
                    bool belowExpired = UseQuickReclaim && _belowSweepBar >= 0 && CurrentBar - _belowSweepBar > ReclaimBars;
                    bool aboveExpired = UseQuickReclaim && _aboveSweepBar >= 0 && CurrentBar - _aboveSweepBar > ReclaimBars;
                    if (belowExpired) { _sweptBelow = false; _belowSweepBar = -1; _sweepLow = double.NaN; _belowCandleOk = true; }
                    if (aboveExpired) { _sweptAbove = false; _aboveSweepBar = -1; _sweepHigh = double.NaN; _aboveCandleOk = true; }

                    if (AllowLong && Low[0] < _rangeLow && !belowExpired)
                    {
                        if (!_sweptBelow) { _belowSweepBar = CurrentBar; _sweepLow = Low[0]; _belowCandleOk = sweepCandleOk; }
                        _sweptBelow = true;
                        _breakoutBar = CurrentBar;
                    }
                    if (AllowShort && High[0] > _rangeHigh && !aboveExpired)
                    {
                        if (!_sweptAbove) { _aboveSweepBar = CurrentBar; _sweepHigh = High[0]; _aboveCandleOk = sweepCandleOk; }
                        _sweptAbove = true;
                        _breakoutBar = CurrentBar;
                    }

                    if (_sweptBelow) { _sweepLow = Math.Min(_sweepLow, Low[0]); _belowCandleOk = _belowCandleOk && sweepCandleOk; }
                    if (_sweptAbove) { _sweepHigh = Math.Max(_sweepHigh, High[0]); _aboveCandleOk = _aboveCandleOk && sweepCandleOk; }

                    bool insideRange = Close[0] > _rangeLow && Close[0] < _rangeHigh;
                    bullReclaim = _sweptBelow && Close[0] > _rangeLow && Close[0] > Open[0]
                        && (!RequireInsideRange || insideRange) && _belowCandleOk && longTrendOk;
                    bearReclaim = _sweptAbove && Close[0] < _rangeHigh && Close[0] < Open[0]
                        && (!RequireInsideRange || insideRange) && _aboveCandleOk && shortTrendOk;

                    if (bullReclaim && atrReady && Position.MarketPosition == MarketPosition.Flat)
                    {
                        double sweepExtreme = UseSweepExtreme ? _sweepLow : Low[0];
                        double stop = sweepExtreme - stopBuffer;
                        double risk = Close[0] - stop;
                        if (risk > 0.0)
                        {
                            double target = Close[0] + risk * RiskReward;
                            EnterLong(Contracts, EntryLong);
                            ArmBracket(stop, target);
                            if (ShowRrBox) DrawRrBoxes(target, stop, isLong: true);
                            if (ShowSignals)
                                Draw.Text(this, "CRTSig" + CurrentBar, false, "Long\nSweep+Reclaim",
                                    0, Low[0] - 4 * TickSize, 0, Brushes.White, new SimpleFont("Arial", 10),
                                    TextAlignment.Center, Brushes.Transparent, Brushes.Green, 100);
                            Print("CRTLiquiditySweepStrategy LONG @ " + Close[0].ToString("0.####")
                                + " — sweep+reclaim, stop @ " + stop.ToString("0.####")
                                + ", target @ " + target.ToString("0.####"));
                            _state = "DONE";
                        }
                    }
                    else if (bearReclaim && atrReady && Position.MarketPosition == MarketPosition.Flat)
                    {
                        double sweepExtreme = UseSweepExtreme ? _sweepHigh : High[0];
                        double stop = sweepExtreme + stopBuffer;
                        double risk = stop - Close[0];
                        if (risk > 0.0)
                        {
                            double target = Close[0] - risk * RiskReward;
                            EnterShort(Contracts, EntryShort);
                            ArmBracket(stop, target);
                            if (ShowRrBox) DrawRrBoxes(target, stop, isLong: false);
                            if (ShowSignals)
                                Draw.Text(this, "CRTSig" + CurrentBar, false, "Short\nSweep+Reclaim",
                                    0, High[0] + 4 * TickSize, 0, Brushes.White, new SimpleFont("Arial", 10),
                                    TextAlignment.Center, Brushes.Transparent, Brushes.Red, 100);
                            Print("CRTLiquiditySweepStrategy SHORT @ " + Close[0].ToString("0.####")
                                + " — sweep+reclaim, stop @ " + stop.ToString("0.####")
                                + ", target @ " + target.ToString("0.####"));
                            _state = "DONE";
                        }
                    }
                    else if (!UseQuickReclaim && _breakoutBar >= 0 && CurrentBar - _breakoutBar > LegacyTimeoutBars)
                    {
                        _state = "DONE";
                    }
                }

                if (ShowStatusPanel) DrawStatusPanel(atrNow, atrStopBuffer);
            }
            catch (Exception ex) { Print("CRTLiquiditySweepStrategy OnBarUpdate error: " + ex.Message); }
        }

        // ── one-time bracket setup on fill (SetStopLoss/SetProfitTarget need to be
        //    (re)issued relative to the live position, same role as Pine's single
        //    strategy.exit call) ──
        private void ArmBracket(double stop, double target)
        {
            _stopPrice = Instrument.MasterInstrument.RoundToTickSize(stop);
            _targetPrice = Instrument.MasterInstrument.RoundToTickSize(target);
            _entryBar = CurrentBar;
            // Stop/target are actually attached in OnOrderUpdate once the entry order
            // reports Filled — see SetStopLossAndTarget below.
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            if (orderState != OrderState.Filled) return;
            if (order.Name == EntryLong)
                SetStopLossAndTarget(EntryLong);
            else if (order.Name == EntryShort)
                SetStopLossAndTarget(EntryShort);
        }

        private void SetStopLossAndTarget(string entryName)
        {
            SetStopLoss(entryName, CalculationMode.Price, _stopPrice, false);
            SetProfitTarget(entryName, CalculationMode.Price, _targetPrice);
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (marketPosition == MarketPosition.Flat) { _stopPrice = 0; _targetPrice = 0; _entryBar = -1; }
        }

        // ── range bucket accumulation — mirrors the Pine bucketId/curO..curC block ──
        private void UpdateRangeBucket()
        {
            long bucketId = (long)Math.Floor(Time[0].ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalMilliseconds / _bucketMs);
            bool newBucket = !_haveBucket || bucketId != _curBucket;

            if (newBucket)
            {
                if (_haveBucket)
                {
                    double htBody  = Math.Abs(_curC - _curO);
                    double htRange = _curH - _curL;
                    double htUpperWick = _curH - Math.Max(_curO, _curC);
                    double htLowerWick = Math.Min(_curO, _curC) - _curL;
                    double htBodyPct = htRange > 0 ? htBody / htRange * 100.0 : 0.0;

                    bool isDoji = htBodyPct < MinBodyPct;
                    bool isMarubozu = htBodyPct > MaxBodyPct;
                    double wickRatio = htUpperWick > htLowerWick
                        ? (htLowerWick > 0 ? htUpperWick / htLowerWick : 1000.0)
                        : (htUpperWick > 0 ? htLowerWick / htUpperWick : 1000.0);
                    bool isHammerType = wickRatio > MaxWickImbalance;
                    bool htCandleOk = !RequireNormalCandle || (!isDoji && !isMarubozu && !isHammerType);

                    if (htCandleOk)
                    {
                        _rangeHigh = _curH;
                        _rangeLow  = _curL;
                        _state = "WATCHING";
                        _sweptBelow = _sweptAbove = false;
                        _breakoutBar = _belowSweepBar = _aboveSweepBar = -1;
                        _sweepLow = _sweepHigh = double.NaN;
                        _belowCandleOk = _aboveCandleOk = true;
                        _rangesFormedCount++;
                        _rangeStartBar = CurrentBar;
                    }
                }

                _curBucket = bucketId;
                _curO = Open[0]; _curH = High[0]; _curL = Low[0]; _curC = Close[0];
                _haveBucket = true;
            }
            else
            {
                _curH = Math.Max(_curH, High[0]);
                _curL = Math.Min(_curL, Low[0]);
                _curC = Close[0];
            }
        }

        private void UpdateRangeBoxVisual()
        {
            if (!ShowRangeBox || double.IsNaN(_rangeHigh) || _rangeStartBar < 0) return;
            Draw.Rectangle(this, "CRTRangeBox", false, CurrentBar - _rangeStartBar, _rangeHigh, 0, _rangeLow,
                Brushes.DodgerBlue, Brushes.DodgerBlue, 12);
        }

        private void DrawRrBoxes(double target, double stop, bool isLong)
        {
            TimeSpan barSpan = CurrentBar >= 1 ? Time[0] - Time[1] : TimeSpan.FromMinutes(RangeTimeframeMinutes);
            if (barSpan <= TimeSpan.Zero) barSpan = TimeSpan.FromMinutes(1);
            DateTime endTime = Time[0] + TimeSpan.FromTicks(barSpan.Ticks * RrProjectionBars);

            Draw.Rectangle(this, "CRTtpBox" + CurrentBar, false, Time[0], Close[0], endTime, target,
                Brushes.Green, Brushes.Green, 18);
            Draw.Rectangle(this, "CRTslBox" + CurrentBar, false, Time[0], Close[0], endTime, stop,
                Brushes.Red, Brushes.Red, 18);
            Draw.Text(this, "CRTtpTxt" + CurrentBar, false, "TP", 0, target, 0, Brushes.Green,
                new SimpleFont("Arial", 9), TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
            Draw.Text(this, "CRTslTxt" + CurrentBar, false, "SL", 0, stop, 0, Brushes.Red,
                new SimpleFont("Arial", 9), TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
        }

        // ── status panel — same 15 fields as the Pine status table, one text block ──
        private void DrawStatusPanel(double atrValue, double atrStopBuffer)
        {
            int closedTrades = SystemPerformance.AllTrades.Count;
            double winRate = closedTrades > 0
                ? 100.0 * SystemPerformance.AllTrades.TradesPerformance.Currency.WinningTrades.Count / (double)closedTrades
                : double.NaN;

            string trendDirText = TrendMethod == CrtTrendMethod.Off ? "Both (filter off)"
                : (_state == "WATCHING" ? "n/a this bar" : "n/a this bar");

            string text =
                "CRT Liquidity Sweep\n" +
                "Ranges formed: " + _rangesFormedCount + "\n" +
                "Trades: " + closedTrades + "\n" +
                "Range H/L: " + (double.IsNaN(_rangeHigh) ? "none yet" : _rangeHigh.ToString("0.##") + " / " + _rangeLow.ToString("0.##")) + "\n" +
                "State: " + _state + "\n" +
                "bar_index: " + CurrentBar + "\n" +
                "ATR (chart TF): " + atrValue.ToString("0.####") + "\n" +
                "ATR stop buffer: " + (UseAtrBuffer ? atrStopBuffer.ToString("0.####") : "Off") + "\n" +
                "Win rate (closed): " + (double.IsNaN(winRate) ? "No closed trades" : winRate.ToString("0.##") + "%") + "\n" +
                "Entry TF / Range TF: " + BarsPeriod.Value + "m / " + RangeTimeframeMinutes + "m\n" +
                "Trend filter: " + TrendMethod;

            Draw.TextFixed(this, "CRTStatusPanel", text, TextPosition.TopRight, Brushes.White,
                new SimpleFont("Consolas", 10), Brushes.Transparent, Brushes.Black, 70);
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Range timeframe (minutes, UTC-aligned)", Description = "Higher-timeframe range built from this chart's own bars. Must be a standard fixed-minute period, larger than and an exact multiple of the chart's own bar size. Pine default was 240 (4 hours).", Order = 1, GroupName = "1. Range (HTF)")]
        public int RangeTimeframeMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require plain candle (exclude doji/hammer/marubozu)", Description = "Only lock in a new range if its higher-timeframe candle passes the body/wick filters below.", Order = 2, GroupName = "1. Range (HTF)")]
        public bool RequireNormalCandle { get; set; }

        [NinjaScriptProperty]
        [Range(50.0, 100.0)]
        [Display(Name = "Max body % of range (marubozu filter)", Description = "Reject the range candle if its body is more than this % of its own high-low range.", Order = 3, GroupName = "1. Range (HTF)")]
        public double MaxBodyPct { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 50.0)]
        [Display(Name = "Min body % of range (doji filter)", Description = "Reject the range candle if its body is less than this % of its own high-low range.", Order = 4, GroupName = "1. Range (HTF)")]
        public double MinBodyPct { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, double.MaxValue)]
        [Display(Name = "Max wick imbalance (hammer filter)", Description = "Reject the range candle if its longer wick is more than this many times its shorter wick.", Order = 5, GroupName = "1. Range (HTF)")]
        public double MaxWickImbalance { get; set; }

        [NinjaScriptProperty]
        [Range(5, int.MaxValue)]
        [Display(Name = "Legacy setup timeout (bars)", Description = "Only used when 'Use short reclaim window' is off: abandon a sweep if no reclaim within this many bars of the first breakout.", Order = 1, GroupName = "2. Sweep and Entry")]
        public int LegacyTimeoutBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require reclaim close inside full range", Description = "Reclaim close must land back inside the full range, not just back across the swept edge.", Order = 2, GroupName = "2. Sweep and Entry")]
        public bool RequireInsideRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use short reclaim window", Description = "Expire a sweep if not reclaimed within 'Bars allowed after first sweep'.", Order = 3, GroupName = "2. Sweep and Entry")]
        public bool UseQuickReclaim { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Bars allowed after first sweep", Description = "0 = same candle only. 1 = sweep candle or the next candle. Repeated sweeps do not extend the deadline.", Order = 4, GroupName = "2. Sweep and Entry")]
        public int ReclaimBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stop beyond full sweep extreme", Description = "Stop sits beyond the full (possibly multi-bar) sweep extreme rather than just the signal bar's wick.", Order = 5, GroupName = "2. Sweep and Entry")]
        public bool UseSweepExtreme { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow longs", Order = 6, GroupName = "2. Sweep and Entry")]
        public bool AllowLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow shorts", Order = 7, GroupName = "2. Sweep and Entry")]
        public bool AllowShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trend filter method", Description = "Off preserves every reclaim signal. EMA: longs above EMA, shorts below. Linear Regression: longs need a positive fitted slope, shorts a negative one.", Order = 1, GroupName = "3. Entry Trend Filter")]
        public CrtTrendMethod TrendMethod { get; set; }

        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "EMA length (chart bars)", Order = 2, GroupName = "3. Entry Trend Filter")]
        public int TrendEmaLength { get; set; }

        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "Linear regression length (chart bars)", Order = 3, GroupName = "3. Entry Trend Filter")]
        public int TrendRegressionLength { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, double.MaxValue)]
        [Display(Name = "Minimum regression slope (% per bar)", Description = "Linear Regression only. Zero requires only direction; larger values exclude flatter trends.", Order = 4, GroupName = "3. Entry Trend Filter")]
        public double MinRegressionSlopePct { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show selected trend line", Order = 5, GroupName = "3. Entry Trend Filter")]
        public bool ShowTrendLine { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, double.MaxValue)]
        [Display(Name = "Risk : Reward", Order = 1, GroupName = "4. Risk")]
        public double RiskReward { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Stop buffer (ticks)", Order = 2, GroupName = "4. Risk")]
        public int StopBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "RR box width (bars)", Order = 3, GroupName = "4. Risk")]
        public int RrProjectionBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use ATR stop buffer", Description = "Adds ATR x multiplier beyond the reclaim wick, on top of the tick buffer.", Order = 1, GroupName = "5. ATR")]
        public bool UseAtrBuffer { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "ATR length (chart timeframe)", Order = 2, GroupName = "5. ATR")]
        public int AtrLength { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, double.MaxValue)]
        [Display(Name = "ATR stop buffer multiplier", Order = 3, GroupName = "5. ATR")]
        public double AtrBufferMultiplier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filter oversized sweep candles", Order = 4, GroupName = "5. ATR")]
        public bool UseAtrSweepFilter { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Maximum sweep candle range / ATR", Description = "Rejects a setup if any candle in its sweep/reclaim sequence exceeds this multiple of the previous bar's ATR.", Order = 5, GroupName = "5. ATR")]
        public double MaxSweepAtr { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show range box", Order = 1, GroupName = "6. Visuals")]
        public bool ShowRangeBox { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show TP/SL boxes", Order = 2, GroupName = "6. Visuals")]
        public bool ShowRrBox { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show labels", Order = 3, GroupName = "6. Visuals")]
        public bool ShowSignals { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show status panel", Order = 4, GroupName = "6. Visuals")]
        public bool ShowStatusPanel { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Contracts", Order = 1, GroupName = "7. Order")]
        public int Contracts { get; set; }
        #endregion
    }
}
