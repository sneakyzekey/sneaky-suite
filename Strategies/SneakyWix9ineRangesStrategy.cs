#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies;
#endregion

// SneakyWix9ineRangesStrategy
// Trades the Signal output of SneakyWix9ineRanges (failed breakout of a story-timeframe
// range; Rejection or Retest entry). Entry at market on the bar after the signal bar.
// Stop  = extreme of the last N bars (incl. the signal bar) +/- buffer, clamped to
//         [MinStopTicks, MaxStopTicks]. Placed in TICKS from the fill so it can never
//         land on the wrong side of the market and get rejected.
// Target = RewardRisk x the stop distance.
// Chart timeframe must be lower than the indicator's story timeframe.

namespace NinjaTrader.NinjaScript.Strategies
{
    public class SneakyWix9ineRangesStrategy : Strategy
    {
        private SneakyWix9ineRanges wix;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                     = "Trades SneakyWix9ineRanges failed-breakout signals with extreme-based stop and R-multiple target.";
                Name                            = "SneakyWix9ineRangesStrategy";
                Calculate                       = Calculate.OnBarClose;
                EntriesPerDirection             = 1;
                EntryHandling                   = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy    = true;
                ExitOnSessionCloseSeconds       = 30;
                IsFillLimitOnTouch              = false;
                TraceOrders                     = false;
                StartBehavior                   = StartBehavior.WaitUntilFlat;
                BarsRequiredToTrade             = 20;

                Quantity        = 1;
                RewardRisk      = 2.0;
                LookbackBars    = 3;
                StopBufferTicks = 2;
                MinStopTicks    = 8;
                MaxStopTicks    = 200;
                AllowLong       = true;
                AllowShort      = true;
                Reverse         = true;
                UseTimeWindow   = false;
                StartTime       = 93000;
                EndTime         = 155500;

                StoryMinutes    = 60;
                NumRanges       = 3;
                EntryMode       = SneakyWix9ineEntryMode.Retest;
                BiasFilter      = SneakyWix9ineBiasFilter.NotAgainst;
                WickFilter      = true;
                FailBars        = 3;
                RetestBars      = 12;
                TolATR          = 0.25;
                Cooldown        = 3;
            }
            else if (State == State.DataLoaded)
            {
                wix = SneakyWix9ineRanges(StoryMinutes, NumRanges, EntryMode, BiasFilter, WickFilter, FailBars, RetestBars, TolATR, Cooldown);
                AddChartIndicator(wix);
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0 || CurrentBar < BarsRequiredToTrade) return;

            if (UseTimeWindow)
            {
                int t = ToTime(Time[0]);
                if (t < StartTime || t >= EndTime)
                {
                    if (Position.MarketPosition != MarketPosition.Flat) { ExitLong("TimeOut", ""); ExitShort("TimeOut", ""); }
                    return;
                }
            }

            int sig = wix.Signal[0];
            if (sig == 0) return;
            if (sig > 0 && !AllowLong)  return;
            if (sig < 0 && !AllowShort) return;

            if (Position.MarketPosition == MarketPosition.Long  && (sig > 0 || !Reverse)) return;
            if (Position.MarketPosition == MarketPosition.Short && (sig < 0 || !Reverse)) return;

            int n = Math.Min(LookbackBars, CurrentBar);
            double ext;
            double riskTicks;
            if (sig > 0)
            {
                ext = Low[0];
                for (int i = 1; i < n; i++) ext = Math.Min(ext, Low[i]);
                riskTicks = (Close[0] - (ext - StopBufferTicks * TickSize)) / TickSize;
            }
            else
            {
                ext = High[0];
                for (int i = 1; i < n; i++) ext = Math.Max(ext, High[i]);
                riskTicks = ((ext + StopBufferTicks * TickSize) - Close[0]) / TickSize;
            }

            int stopTicks   = (int)Math.Round(Math.Max(MinStopTicks, Math.Min(MaxStopTicks, riskTicks)));
            int targetTicks = (int)Math.Max(1, Math.Round(stopTicks * RewardRisk));

            if (sig > 0)
            {
                SetStopLoss("WixL", CalculationMode.Ticks, stopTicks, false);
                SetProfitTarget("WixL", CalculationMode.Ticks, targetTicks);
                EnterLong(Quantity, "WixL");
            }
            else
            {
                SetStopLoss("WixS", CalculationMode.Ticks, stopTicks, false);
                SetProfitTarget("WixS", CalculationMode.Ticks, targetTicks);
                EnterShort(Quantity, "WixS");
            }
        }

        #region Properties
        [NinjaScriptProperty][Range(1, int.MaxValue)]
        [Display(Name = "Quantity", GroupName = "1. Trade", Order = 0)]
        public int Quantity { get; set; }

        [NinjaScriptProperty][Range(0.1, 20)]
        [Display(Name = "Reward : risk", GroupName = "1. Trade", Order = 1)]
        public double RewardRisk { get; set; }

        [NinjaScriptProperty][Range(1, 20)]
        [Display(Name = "Stop lookback (bars)", Description = "Stop sits beyond the extreme of this many bars including the signal bar.", GroupName = "1. Trade", Order = 2)]
        public int LookbackBars { get; set; }

        [NinjaScriptProperty][Range(0, 100)]
        [Display(Name = "Stop buffer (ticks)", GroupName = "1. Trade", Order = 3)]
        public int StopBufferTicks { get; set; }

        [NinjaScriptProperty][Range(1, 1000)]
        [Display(Name = "Min stop (ticks)", GroupName = "1. Trade", Order = 4)]
        public int MinStopTicks { get; set; }

        [NinjaScriptProperty][Range(1, 5000)]
        [Display(Name = "Max stop (ticks)", GroupName = "1. Trade", Order = 5)]
        public int MaxStopTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow longs", GroupName = "1. Trade", Order = 6)]
        public bool AllowLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow shorts", GroupName = "1. Trade", Order = 7)]
        public bool AllowShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Reverse on opposite signal", GroupName = "1. Trade", Order = 8)]
        public bool Reverse { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use time window", GroupName = "2. Session", Order = 0)]
        public bool UseTimeWindow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Start time (HHmmss)", GroupName = "2. Session", Order = 1)]
        public int StartTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "End time (HHmmss)", GroupName = "2. Session", Order = 2)]
        public int EndTime { get; set; }

        [NinjaScriptProperty][Range(1, int.MaxValue)]
        [Display(Name = "Story timeframe (minutes)", GroupName = "3. Indicator", Order = 0)]
        public int StoryMinutes { get; set; }

        [NinjaScriptProperty][Range(2, 10)]
        [Display(Name = "Ranges to track", GroupName = "3. Indicator", Order = 1)]
        public int NumRanges { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Entry mode", GroupName = "3. Indicator", Order = 2)]
        public SneakyWix9ineEntryMode EntryMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Story-TF bias filter", GroupName = "3. Indicator", Order = 3)]
        public SneakyWix9ineBiasFilter BiasFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Wick filter", GroupName = "3. Indicator", Order = 4)]
        public bool WickFilter { get; set; }

        [NinjaScriptProperty][Range(0, 20)]
        [Display(Name = "Max closes outside (trap)", GroupName = "3. Indicator", Order = 5)]
        public int FailBars { get; set; }

        [NinjaScriptProperty][Range(1, 200)]
        [Display(Name = "Retest window (bars)", GroupName = "3. Indicator", Order = 6)]
        public int RetestBars { get; set; }

        [NinjaScriptProperty][Range(0, double.MaxValue)]
        [Display(Name = "Level tolerance (x ATR 14)", GroupName = "3. Indicator", Order = 7)]
        public double TolATR { get; set; }

        [NinjaScriptProperty][Range(0, int.MaxValue)]
        [Display(Name = "Cooldown (bars)", GroupName = "3. Indicator", Order = 8)]
        public int Cooldown { get; set; }
        #endregion
    }
}
