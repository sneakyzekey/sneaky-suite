#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// 2026-09-21 -- INDICATOR port of TurtleTrendBreakout_v1_0_0 (the unmanaged-order Strategy
// version). That file straddled both sides of a Donchian channel with resting stop orders
// and managed its own ATR initial/trailing stop once filled. This file drops all of the
// order-management machinery and keeps only the ENTRY signal: a Donchian-channel breakout,
// in the same spirit as the Turtle Trading rules.
//
// Purpose: expose that breakout as a single numeric plot ("Signal") that an external
// automation layer (an AddOn, ATI/NTDirect bridge, or another strategy reading this
// indicator's Values[0] via IndicatorBase/AddChartIndicator) can poll and act on, instead
// of relying on this file to place orders itself.
//
//   Signal[0] ==  1  -> long bias (price broke out above the prior N-bar high)
//   Signal[0] == -1  -> short bias (price broke out below the prior N-bar low)
//   Signal[0] ==  0  -> no breakout yet (only possible before the first breakout of the
//                       session, or right after a session reset -- see
//                       ResetSignalAtSessionStart below)
//
// The Signal plot is STATEFUL, not a one-bar pulse: once a breakout fires, Signal holds
// that value on every subsequent bar until the OPPOSITE breakout fires (or the session
// resets). Good for "which side does the channel currently favor," bad for driving an
// auto-entry bridge -- a bridge that arms mid-trend sees an already-true condition and
// fires immediately on whatever bar it was turned on, not on an actual new breakout.
//
// 2026-09-21 -- Added a second plot, Trigger, to fix exactly that. Trigger is a ONE-BAR
// PULSE: it is 0 on every bar except the bar a NEW breakout happens, where it takes the
// same +1/-1 value as Signal for that bar only, then reverts to 0 the bar after. Point an
// auto-entry bridge's condition at Trigger (== 1 for long, == -1 for short) instead of
// Signal, and enabling the bridge mid-trend will no longer fire a trade off of stale
// state -- it will wait for the next genuine breakout, same as it would if it had been
// running the whole time.
//
//   Signal[0]  ==  1 / -1 / 0  -> persistent directional bias (see above)
//   Trigger[0] ==  1           -> a NEW long breakout happened on THIS bar only
//   Trigger[0] == -1           -> a NEW short breakout happened on THIS bar only
//   Trigger[0] ==  0           -> no new breakout on this bar
//
// 2026-09-21 (later same day) -- AllowContinuationTriggers added. Originally Trigger only
// fired on a direction CHANGE (flat/short -> long, or flat/long -> short) -- so once the
// channel had been long for a while, a fresh higher high that pushed the Donchian box up
// again did NOT re-pulse Trigger, since Signal was already 1. That matches the original
// Strategy's one-entry-per-leg behavior (no new entry order once a position is already
// open in that direction), but it means someone watching the chart sees price visibly
// clear the range box with no Trigger firing, which reads as broken even though it isn't.
// With AllowContinuationTriggers on (the default now), Trigger re-pulses on EVERY bar
// price makes a fresh N-bar extreme, even while Signal is already sitting at that same
// value -- i.e. it fires once per genuine breakout event, continuation legs included, not
// just once per new leg. Signal's own persistent value is unaffected either way. Turn this
// off to go back to the original one-shot-per-leg behavior (e.g. if your bridge doesn't
// gate on "already in a position" and you don't want repeat same-direction entries).
//
// Donchian channel is computed over the PRIOR N bars ([1] deliberately excludes the bar
// still forming), exactly as in the Strategy version, so the breakout level for the bar
// being tested never includes that same bar's own high/low.
//
// This is a SIGNAL indicator only -- it does not size positions, place orders, or manage
// stops. Bring your own risk management (ATR stop, trailing stop, position sizing) in
// whatever consumes this signal, the same way the original Strategy version did for ATR
// stops.
namespace NinjaTrader.NinjaScript.Indicators
{
    public class TurtleTrendBreakoutSignal_v1_0_0 : Indicator
    {
        private int currentSignal;
        private int lastFlipBar = -1;
        private int lastFlipDirection;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "TurtleTrendBreakoutSignal_v1_0_0";
                Description = "Donchian-channel breakout: Signal (+1 long / -1 short, persistent) and Trigger (+1/-1, one-bar pulse on a NEW breakout only -- use this for auto-entry bridges). No order management -- signal only.";

                EntryChannelLength = 20;
                AllowLong = true;
                AllowShort = true;
                ShowRangeBox = true;
                DrawArrows = true;
                ResetSignalAtSessionStart = true;
                AllowContinuationTriggers = true;

                Calculate = Calculate.OnEachTick;
                IsOverlay = false;
                DisplayInDataBox = true;
                PaintPriceMarkers = false;
                ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive = true;

                AddPlot(new Stroke(Brushes.DodgerBlue, 2), PlotStyle.Line, "Signal");
                AddPlot(new Stroke(Brushes.Gold, 2), PlotStyle.Bar, "Trigger");
                AddLine(Brushes.DimGray, 1, "ZeroLine");
            }
            else if (State == State.Configure)
            {
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < EntryChannelLength + 1)
            {
                Values[0][0] = 0;
                Values[1][0] = 0;
                return;
            }

            if (ResetSignalAtSessionStart && Bars.IsFirstBarOfSession)
                currentSignal = 0;

            // Donchian channel over the PRIOR N bars -- see header comment for why [1].
            double donchianHigh = Instrument.MasterInstrument.RoundToTickSize(MAX(High, EntryChannelLength)[1]);
            double donchianLow = Instrument.MasterInstrument.RoundToTickSize(MIN(Low, EntryChannelLength)[1]);

            if (ShowRangeBox && IsFirstTickOfBar)
            {
                Draw.Rectangle(this, "TurtleRangeBox", false,
                    EntryChannelLength, donchianHigh, 1, donchianLow,
                    Brushes.DodgerBlue, Brushes.DodgerBlue, 15);
            }

            bool longBreak = AllowLong && High[0] >= donchianHigh;
            bool shortBreak = AllowShort && Low[0] <= donchianLow;

            // With AllowContinuationTriggers off, only fire on an actual direction CHANGE
            // (currentSignal != 1/-1) -- the original one-shot-per-leg behavior. With it
            // on (the default), fire on every fresh N-bar extreme regardless of whether
            // Signal was already sitting at that value, so continuation breakouts inside
            // an existing trend still pulse Trigger. Either way, if both sides break on the
            // same bar (a wide/gap bar), don't guess -- do nothing rather than picking a
            // side arbitrarily.
            bool longFire = longBreak && !shortBreak && (AllowContinuationTriggers || currentSignal != 1);
            bool shortFire = shortBreak && !longBreak && (AllowContinuationTriggers || currentSignal != -1);

            if (longFire)
            {
                currentSignal = 1;
                lastFlipBar = CurrentBar;
                lastFlipDirection = 1;
                if (DrawArrows)
                    Draw.ArrowUp(this, "TurtleLongSig" + CurrentBar, true, 0, Low[0] - 2 * TickSize, Brushes.Lime);
            }
            else if (shortFire)
            {
                currentSignal = -1;
                lastFlipBar = CurrentBar;
                lastFlipDirection = -1;
                if (DrawArrows)
                    Draw.ArrowDown(this, "TurtleShortSig" + CurrentBar, true, 0, High[0] + 2 * TickSize, Brushes.Red);
            }

            Values[0][0] = currentSignal;

            // Trigger is a one-bar pulse: it only reads non-zero on the bar the flip above
            // happened on THIS pass through history/real-time, then reverts to 0 the very
            // next bar -- see header comment. Re-deriving it from lastFlipBar (rather than
            // just writing a literal 1/-1 the instant a flip occurs) means it stays correct
            // across multiple ticks of the same bar under Calculate.OnEachTick: every tick
            // of the flip bar re-derives the same pulse value instead of a later tick
            // wiping out what an earlier tick on that bar just set.
            Values[1][0] = (CurrentBar == lastFlipBar) ? lastFlipDirection : 0;
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Entry Channel Length", GroupName = "Parameters", Order = 1)]
        public int EntryChannelLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow Long Signal", GroupName = "Parameters", Order = 2)]
        public bool AllowLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow Short Signal", GroupName = "Parameters", Order = 3)]
        public bool AllowShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Reset Signal At Session Start", GroupName = "Parameters", Order = 4)]
        public bool ResetSignalAtSessionStart { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow Continuation Triggers", GroupName = "Parameters", Order = 5)]
        public bool AllowContinuationTriggers { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Range Box", GroupName = "Display", Order = 1)]
        public bool ShowRangeBox { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw Arrows", GroupName = "Display", Order = 2)]
        public bool DrawArrows { get; set; }

        [Browsable(false)]
        [XmlIgnore()]
        public Series<double> Signal
        {
            get { return Values[0]; }
        }

        [Browsable(false)]
        [XmlIgnore()]
        public Series<double> Trigger
        {
            get { return Values[1]; }
        }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private TurtleTrendBreakoutSignal_v1_0_0[] cacheTurtleTrendBreakoutSignal_v1_0_0;

        public TurtleTrendBreakoutSignal_v1_0_0 TurtleTrendBreakoutSignal_v1_0_0(int entryChannelLength, bool allowLong, bool allowShort, bool resetSignalAtSessionStart, bool allowContinuationTriggers, bool showRangeBox, bool drawArrows)
        {
            return TurtleTrendBreakoutSignal_v1_0_0(Input, entryChannelLength, allowLong, allowShort, resetSignalAtSessionStart, allowContinuationTriggers, showRangeBox, drawArrows);
        }

        public TurtleTrendBreakoutSignal_v1_0_0 TurtleTrendBreakoutSignal_v1_0_0(ISeries<double> input, int entryChannelLength, bool allowLong, bool allowShort, bool resetSignalAtSessionStart, bool allowContinuationTriggers, bool showRangeBox, bool drawArrows)
        {
            if (cacheTurtleTrendBreakoutSignal_v1_0_0 != null)
                for (int idx = 0; idx < cacheTurtleTrendBreakoutSignal_v1_0_0.Length; idx++)
                    if (cacheTurtleTrendBreakoutSignal_v1_0_0[idx] != null && cacheTurtleTrendBreakoutSignal_v1_0_0[idx].EntryChannelLength == entryChannelLength && cacheTurtleTrendBreakoutSignal_v1_0_0[idx].AllowLong == allowLong && cacheTurtleTrendBreakoutSignal_v1_0_0[idx].AllowShort == allowShort && cacheTurtleTrendBreakoutSignal_v1_0_0[idx].ResetSignalAtSessionStart == resetSignalAtSessionStart && cacheTurtleTrendBreakoutSignal_v1_0_0[idx].AllowContinuationTriggers == allowContinuationTriggers && cacheTurtleTrendBreakoutSignal_v1_0_0[idx].ShowRangeBox == showRangeBox && cacheTurtleTrendBreakoutSignal_v1_0_0[idx].DrawArrows == drawArrows && cacheTurtleTrendBreakoutSignal_v1_0_0[idx].EqualsInput(input))
                        return cacheTurtleTrendBreakoutSignal_v1_0_0[idx];

            return CacheIndicator<TurtleTrendBreakoutSignal_v1_0_0>(new TurtleTrendBreakoutSignal_v1_0_0
            {
                EntryChannelLength = entryChannelLength,
                AllowLong = allowLong,
                AllowShort = allowShort,
                ResetSignalAtSessionStart = resetSignalAtSessionStart,
                AllowContinuationTriggers = allowContinuationTriggers,
                ShowRangeBox = showRangeBox,
                DrawArrows = drawArrows
            }, input, ref cacheTurtleTrendBreakoutSignal_v1_0_0);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.TurtleTrendBreakoutSignal_v1_0_0 TurtleTrendBreakoutSignal_v1_0_0(int entryChannelLength, bool allowLong, bool allowShort, bool resetSignalAtSessionStart, bool allowContinuationTriggers, bool showRangeBox, bool drawArrows)
        {
            return indicator.TurtleTrendBreakoutSignal_v1_0_0(Input, entryChannelLength, allowLong, allowShort, resetSignalAtSessionStart, allowContinuationTriggers, showRangeBox, drawArrows);
        }

        public Indicators.TurtleTrendBreakoutSignal_v1_0_0 TurtleTrendBreakoutSignal_v1_0_0(ISeries<double> input, int entryChannelLength, bool allowLong, bool allowShort, bool resetSignalAtSessionStart, bool allowContinuationTriggers, bool showRangeBox, bool drawArrows)
        {
            return indicator.TurtleTrendBreakoutSignal_v1_0_0(input, entryChannelLength, allowLong, allowShort, resetSignalAtSessionStart, allowContinuationTriggers, showRangeBox, drawArrows);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.TurtleTrendBreakoutSignal_v1_0_0 TurtleTrendBreakoutSignal_v1_0_0(int entryChannelLength, bool allowLong, bool allowShort, bool resetSignalAtSessionStart, bool allowContinuationTriggers, bool showRangeBox, bool drawArrows)
        {
            return indicator.TurtleTrendBreakoutSignal_v1_0_0(Input, entryChannelLength, allowLong, allowShort, resetSignalAtSessionStart, allowContinuationTriggers, showRangeBox, drawArrows);
        }

        public Indicators.TurtleTrendBreakoutSignal_v1_0_0 TurtleTrendBreakoutSignal_v1_0_0(ISeries<double> input, int entryChannelLength, bool allowLong, bool allowShort, bool resetSignalAtSessionStart, bool allowContinuationTriggers, bool showRangeBox, bool drawArrows)
        {
            return indicator.TurtleTrendBreakoutSignal_v1_0_0(input, entryChannelLength, allowLong, allowShort, resetSignalAtSessionStart, allowContinuationTriggers, showRangeBox, drawArrows);
        }
    }
}

#endregion
