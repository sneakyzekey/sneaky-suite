#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
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

// 2026-09-23 -- PHASE 3 of the from-scratch "Praedox Sentinel" rebuild (see
// PraedoxSignalEngine_v0_1_0 for phase 1, PraedoxFibEngine_v0_1_0 for phase 2, and the
// full project context/disclaimer -- same situation here: original implementation from
// marketing-copy behavior, no surviving source). This phase is "Volatility Shield" +
// "Smart Trader" -- a 4-state volatility regime read plus a gate that says whether
// automated trading should be allowed right now.
//
// VOLATILITY RATIO (VR): the marketing copy describes VR as a live number ("VR: 1.02",
// "VR: 0.70" in surviving screenshots) with no surviving formula. Implemented here as
// current ATR divided by a longer-baseline SMA of that same ATR -- VR near 1.0 means
// "trading like its own recent normal," VR well below 1.0 means volatility has gone
// unusually quiet, VR well above 1.0 means it's unusually elevated. This is a standard,
// defensible way to build a self-relative volatility ratio and matches both surviving
// screenshots (VR 1.02 shown as Normal, VR 0.70 shown as "Lower Volatility").
//
// FOUR STATES -- the copy is directional, not symmetric: Yellow is explicitly the LOW
// side ("Lower Volatility, Exercise Caution... momentum is thin"), while Red/Dark Red are
// explicitly the HIGH side ("volatility is elevated... extreme conditions"). Nothing in
// the surviving copy describes a low-side Red/Dark Red, so this implementation doesn't
// invent one -- the band layout below is a direct, documented choice, not a guess:
//   Green      : VR between YellowLowThreshold and RedHighThreshold      -- normal
//   Yellow     : VR <  YellowLowThreshold                                -- quiet/thin
//   Red        : VR >  RedHighThreshold and <= DarkRedThreshold          -- elevated
//   Dark Red   : VR >  DarkRedThreshold                                  -- extreme
// All three thresholds are tunable properties, not hardcoded.
//
// MIN STOP/TARGET RECOMMENDATIONS: "each state also displays minimum stop and target
// recommendations." Implemented as ATR multiples, one pair per state, all tunable, so the
// recommendation scales with the instrument automatically instead of being a fixed tick
// count. Defaults escalate with the state's risk (matches "risk per trade is meaningfully
// higher" in Red/Dark Red): Green 1.0x/2.0x ATR, Yellow 1.3x/2.2x, Red 1.8x/2.6x, Dark Red
// 2.5x/3.2x.
//
// SMART TRADER: "automatically enabling/disabling your trading button based on live
// volatility." This indicator doesn't have a trading button (no cockpit UI yet, no order
// management -- same signal-only pattern as PraedoxSignalEngine_v0_1_0), so what it
// outputs is the boolean an automation bridge or the future cockpit would read:
// TradingAllowed[0]. Three modes, matching the surviving copy exactly:
//   GreenOnly           -- trades only in Green, pauses on Yellow/Red/Dark Red
//   GreenAndYellow       -- trades in Green+Yellow, blocks Red/Dark Red
//   GreenYellowAndRed    -- only blocks Dark Red
// Dark Red is ALWAYS blocked regardless of mode -- "no exceptions, ever," per the copy --
// and EnableSmartTrader is the on/off checkbox from "toggleable live from the sidebar";
// when off, TradingAllowed is always 1 (no volatility-based restriction applied).
//
// OUTPUTS (sub-panel plots, not overlaid on price):
//   VolatilityRatio[0] -> the raw VR value described above
//   VolatilityState[0] -> 0=Green, 1=Yellow, 2=Red, 3=Dark Red
//   TradingAllowed[0]  -> 1/0, Smart Trader's gate (see above)
//   MinStopTicks[0]    -> recommended stop, in ticks, for the CURRENT state
//   MinTargetTicks[0]  -> recommended target, in ticks, for the CURRENT state
// On-chart: a small state label (e.g. "VR 1.02 - NORMAL") drawn at the current bar, same
// axis-hugging/high-contrast style as PraedoxFibEngine_v0_1_0's level labels.
namespace NinjaTrader.NinjaScript.Indicators
{
    public enum PraedoxSmartTraderMode
    {
        GreenOnly,
        GreenAndYellow,
        GreenYellowAndRed
    }

    public class PraedoxVolatilityShield_v0_1_0 : Indicator
    {
        private ATR atr;
        private SMA atrBaseline;

        private static readonly string[] StateNames = { "NORMAL", "LOWER VOL", "ELEVATED", "EXTREME" };
        private static readonly SimpleFont labelFont = new SimpleFont("Arial", 12) { Bold = true };

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "PraedoxVolatilityShield_v0_1_0";
                Description = "Phase 3: 4-state Volatility Shield (VR-based) + Smart Trader auto-trading gate + per-state min stop/target recommendations. Original implementation from behavior spec.";

                Calculate = Calculate.OnBarClose;
                IsOverlay = false;
                DisplayInDataBox = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;

                VrAtrLength = 14;
                VrBaselineLength = 50;

                YellowLowThreshold = 0.85;
                RedHighThreshold = 1.5;
                DarkRedThreshold = 2.5;

                GreenMinStopAtrMult = 1.0;
                GreenMinTargetAtrMult = 2.0;
                YellowMinStopAtrMult = 1.3;
                YellowMinTargetAtrMult = 2.2;
                RedMinStopAtrMult = 1.8;
                RedMinTargetAtrMult = 2.6;
                DarkRedMinStopAtrMult = 2.5;
                DarkRedMinTargetAtrMult = 3.2;

                EnableSmartTrader = true;
                Mode = PraedoxSmartTraderMode.GreenAndYellow;

                DrawLabel = true;

                AddPlot(new Stroke(Brushes.DodgerBlue, 2), PlotStyle.Line, "VolatilityRatio");
                AddPlot(new Stroke(Brushes.Gold, 1), PlotStyle.Bar, "VolatilityState");
                AddPlot(new Stroke(Brushes.Lime, 1), PlotStyle.Bar, "TradingAllowed");
                AddPlot(new Stroke(Brushes.Gray, 1), PlotStyle.Line, "MinStopTicks");
                AddPlot(new Stroke(Brushes.DarkGray, 1), PlotStyle.Line, "MinTargetTicks");
            }
            else if (State == State.DataLoaded)
            {
                atr = ATR(VrAtrLength);
                atrBaseline = SMA(atr, VrBaselineLength);
            }
        }

        protected override void OnBarUpdate()
        {
            int warmup = VrAtrLength + VrBaselineLength + 2;
            if (CurrentBar < warmup)
            {
                for (int i = 0; i < 5; i++)
                    Values[i][0] = 0;
                return;
            }

            double vr = atrBaseline[0] > 0 ? atr[0] / atrBaseline[0] : 1.0;

            // Directional bands -- see header comment for why this isn't a symmetric
            // "distance from 1.0" scheme.
            int state;
            if (vr < YellowLowThreshold)
                state = 1; // Yellow -- quiet/thin
            else if (vr <= RedHighThreshold)
                state = 0; // Green -- normal
            else if (vr <= DarkRedThreshold)
                state = 2; // Red -- elevated
            else
                state = 3; // Dark Red -- extreme

            bool allowed;
            if (!EnableSmartTrader)
            {
                allowed = true;
            }
            else
            {
                switch (state)
                {
                    case 3: allowed = false; break; // Dark Red always blocked, no exceptions
                    case 2: allowed = Mode == PraedoxSmartTraderMode.GreenYellowAndRed; break;
                    case 1: allowed = Mode == PraedoxSmartTraderMode.GreenAndYellow || Mode == PraedoxSmartTraderMode.GreenYellowAndRed; break;
                    default: allowed = true; break; // Green always allowed
                }
            }

            double stopMult, targetMult;
            switch (state)
            {
                case 1: stopMult = YellowMinStopAtrMult; targetMult = YellowMinTargetAtrMult; break;
                case 2: stopMult = RedMinStopAtrMult; targetMult = RedMinTargetAtrMult; break;
                case 3: stopMult = DarkRedMinStopAtrMult; targetMult = DarkRedMinTargetAtrMult; break;
                default: stopMult = GreenMinStopAtrMult; targetMult = GreenMinTargetAtrMult; break;
            }

            double minStopTicks = TickSize > 0 ? Math.Round(atr[0] * stopMult / TickSize) : 0;
            double minTargetTicks = TickSize > 0 ? Math.Round(atr[0] * targetMult / TickSize) : 0;

            Values[0][0] = vr;
            Values[1][0] = state;
            Values[2][0] = allowed ? 1 : 0;
            Values[3][0] = minStopTicks;
            Values[4][0] = minTargetTicks;

            // Color the VolatilityState bar plot per-state so it reads at a glance without
            // needing the text label -- same green/yellow/red/dark-red the marketing copy
            // uses for the sidebar dots.
            Brush stateColor = StateColor(state);
            PlotBrushes[1][0] = stateColor;

            if (DrawLabel)
            {
                string text = "VR " + vr.ToString("0.00") + " - " + StateNames[state];
                // Same axis-hugging, high-contrast label style as PraedoxFibEngine_v0_1_0:
                // solid dark backing plate behind bold text so it reads against candles or
                // anything else drawn on the chart, regardless of theme.
                Draw.Text(this, "PraedoxVolShieldLabel", false, text, 0, High[0], 20, Brushes.White, labelFont, TextAlignment.Left, Brushes.Black, Brushes.Black, 80);
            }
        }

        private static Brush StateColor(int state)
        {
            switch (state)
            {
                case 1: return Brushes.Gold;
                case 2: return Brushes.OrangeRed;
                case 3: return Brushes.DarkRed;
                default: return Brushes.Lime;
            }
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "VR ATR Length", GroupName = "Core", Order = 1)]
        public int VrAtrLength { get; set; }

        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "VR Baseline Length (bars)", GroupName = "Core", Order = 2)]
        public int VrBaselineLength { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, double.MaxValue)]
        [Display(Name = "Yellow Threshold (VR below = quiet)", GroupName = "Thresholds", Order = 1)]
        public double YellowLowThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, double.MaxValue)]
        [Display(Name = "Red Threshold (VR above = elevated)", GroupName = "Thresholds", Order = 2)]
        public double RedHighThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, double.MaxValue)]
        [Display(Name = "Dark Red Threshold (VR above = extreme)", GroupName = "Thresholds", Order = 3)]
        public double DarkRedThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Green: Min Stop (x ATR)", GroupName = "Stop/Target Recs", Order = 1)]
        public double GreenMinStopAtrMult { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Green: Min Target (x ATR)", GroupName = "Stop/Target Recs", Order = 2)]
        public double GreenMinTargetAtrMult { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Yellow: Min Stop (x ATR)", GroupName = "Stop/Target Recs", Order = 3)]
        public double YellowMinStopAtrMult { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Yellow: Min Target (x ATR)", GroupName = "Stop/Target Recs", Order = 4)]
        public double YellowMinTargetAtrMult { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Red: Min Stop (x ATR)", GroupName = "Stop/Target Recs", Order = 5)]
        public double RedMinStopAtrMult { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Red: Min Target (x ATR)", GroupName = "Stop/Target Recs", Order = 6)]
        public double RedMinTargetAtrMult { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Dark Red: Min Stop (x ATR)", GroupName = "Stop/Target Recs", Order = 7)]
        public double DarkRedMinStopAtrMult { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Dark Red: Min Target (x ATR)", GroupName = "Stop/Target Recs", Order = 8)]
        public double DarkRedMinTargetAtrMult { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Smart Trader", GroupName = "Smart Trader", Order = 1)]
        public bool EnableSmartTrader { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Smart Trader Mode", GroupName = "Smart Trader", Order = 2)]
        public PraedoxSmartTraderMode Mode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw State Label", GroupName = "Display", Order = 1)]
        public bool DrawLabel { get; set; }

        [Browsable(false)]
        [XmlIgnore()]
        public Series<double> VolatilityRatio
        {
            get { return Values[0]; }
        }

        [Browsable(false)]
        [XmlIgnore()]
        public Series<double> VolatilityState
        {
            get { return Values[1]; }
        }

        [Browsable(false)]
        [XmlIgnore()]
        public Series<double> TradingAllowed
        {
            get { return Values[2]; }
        }

        [Browsable(false)]
        [XmlIgnore()]
        public Series<double> MinStopTicks
        {
            get { return Values[3]; }
        }

        [Browsable(false)]
        [XmlIgnore()]
        public Series<double> MinTargetTicks
        {
            get { return Values[4]; }
        }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private PraedoxVolatilityShield_v0_1_0[] cachePraedoxVolatilityShield_v0_1_0;

        public PraedoxVolatilityShield_v0_1_0 PraedoxVolatilityShield_v0_1_0(int vrAtrLength, int vrBaselineLength, double yellowLowThreshold, double redHighThreshold, double darkRedThreshold, double greenMinStopAtrMult, double greenMinTargetAtrMult, double yellowMinStopAtrMult, double yellowMinTargetAtrMult, double redMinStopAtrMult, double redMinTargetAtrMult, double darkRedMinStopAtrMult, double darkRedMinTargetAtrMult, bool enableSmartTrader, PraedoxSmartTraderMode mode, bool drawLabel)
        {
            return PraedoxVolatilityShield_v0_1_0(Input, vrAtrLength, vrBaselineLength, yellowLowThreshold, redHighThreshold, darkRedThreshold, greenMinStopAtrMult, greenMinTargetAtrMult, yellowMinStopAtrMult, yellowMinTargetAtrMult, redMinStopAtrMult, redMinTargetAtrMult, darkRedMinStopAtrMult, darkRedMinTargetAtrMult, enableSmartTrader, mode, drawLabel);
        }

        public PraedoxVolatilityShield_v0_1_0 PraedoxVolatilityShield_v0_1_0(ISeries<double> input, int vrAtrLength, int vrBaselineLength, double yellowLowThreshold, double redHighThreshold, double darkRedThreshold, double greenMinStopAtrMult, double greenMinTargetAtrMult, double yellowMinStopAtrMult, double yellowMinTargetAtrMult, double redMinStopAtrMult, double redMinTargetAtrMult, double darkRedMinStopAtrMult, double darkRedMinTargetAtrMult, bool enableSmartTrader, PraedoxSmartTraderMode mode, bool drawLabel)
        {
            if (cachePraedoxVolatilityShield_v0_1_0 != null)
                for (int idx = 0; idx < cachePraedoxVolatilityShield_v0_1_0.Length; idx++)
                    if (cachePraedoxVolatilityShield_v0_1_0[idx] != null && cachePraedoxVolatilityShield_v0_1_0[idx].VrAtrLength == vrAtrLength && cachePraedoxVolatilityShield_v0_1_0[idx].VrBaselineLength == vrBaselineLength && cachePraedoxVolatilityShield_v0_1_0[idx].YellowLowThreshold == yellowLowThreshold && cachePraedoxVolatilityShield_v0_1_0[idx].RedHighThreshold == redHighThreshold && cachePraedoxVolatilityShield_v0_1_0[idx].DarkRedThreshold == darkRedThreshold && cachePraedoxVolatilityShield_v0_1_0[idx].GreenMinStopAtrMult == greenMinStopAtrMult && cachePraedoxVolatilityShield_v0_1_0[idx].GreenMinTargetAtrMult == greenMinTargetAtrMult && cachePraedoxVolatilityShield_v0_1_0[idx].YellowMinStopAtrMult == yellowMinStopAtrMult && cachePraedoxVolatilityShield_v0_1_0[idx].YellowMinTargetAtrMult == yellowMinTargetAtrMult && cachePraedoxVolatilityShield_v0_1_0[idx].RedMinStopAtrMult == redMinStopAtrMult && cachePraedoxVolatilityShield_v0_1_0[idx].RedMinTargetAtrMult == redMinTargetAtrMult && cachePraedoxVolatilityShield_v0_1_0[idx].DarkRedMinStopAtrMult == darkRedMinStopAtrMult && cachePraedoxVolatilityShield_v0_1_0[idx].DarkRedMinTargetAtrMult == darkRedMinTargetAtrMult && cachePraedoxVolatilityShield_v0_1_0[idx].EnableSmartTrader == enableSmartTrader && cachePraedoxVolatilityShield_v0_1_0[idx].Mode == mode && cachePraedoxVolatilityShield_v0_1_0[idx].DrawLabel == drawLabel && cachePraedoxVolatilityShield_v0_1_0[idx].EqualsInput(input))
                        return cachePraedoxVolatilityShield_v0_1_0[idx];

            return CacheIndicator<PraedoxVolatilityShield_v0_1_0>(new PraedoxVolatilityShield_v0_1_0(){ VrAtrLength = vrAtrLength, VrBaselineLength = vrBaselineLength, YellowLowThreshold = yellowLowThreshold, RedHighThreshold = redHighThreshold, DarkRedThreshold = darkRedThreshold, GreenMinStopAtrMult = greenMinStopAtrMult, GreenMinTargetAtrMult = greenMinTargetAtrMult, YellowMinStopAtrMult = yellowMinStopAtrMult, YellowMinTargetAtrMult = yellowMinTargetAtrMult, RedMinStopAtrMult = redMinStopAtrMult, RedMinTargetAtrMult = redMinTargetAtrMult, DarkRedMinStopAtrMult = darkRedMinStopAtrMult, DarkRedMinTargetAtrMult = darkRedMinTargetAtrMult, EnableSmartTrader = enableSmartTrader, Mode = mode, DrawLabel = drawLabel }, input, ref cachePraedoxVolatilityShield_v0_1_0);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.PraedoxVolatilityShield_v0_1_0 PraedoxVolatilityShield_v0_1_0(int vrAtrLength, int vrBaselineLength, double yellowLowThreshold, double redHighThreshold, double darkRedThreshold, double greenMinStopAtrMult, double greenMinTargetAtrMult, double yellowMinStopAtrMult, double yellowMinTargetAtrMult, double redMinStopAtrMult, double redMinTargetAtrMult, double darkRedMinStopAtrMult, double darkRedMinTargetAtrMult, bool enableSmartTrader, Indicators.PraedoxSmartTraderMode mode, bool drawLabel)
        {
            return indicator.PraedoxVolatilityShield_v0_1_0(Input, vrAtrLength, vrBaselineLength, yellowLowThreshold, redHighThreshold, darkRedThreshold, greenMinStopAtrMult, greenMinTargetAtrMult, yellowMinStopAtrMult, yellowMinTargetAtrMult, redMinStopAtrMult, redMinTargetAtrMult, darkRedMinStopAtrMult, darkRedMinTargetAtrMult, enableSmartTrader, mode, drawLabel);
        }

        public Indicators.PraedoxVolatilityShield_v0_1_0 PraedoxVolatilityShield_v0_1_0(ISeries<double> input, int vrAtrLength, int vrBaselineLength, double yellowLowThreshold, double redHighThreshold, double darkRedThreshold, double greenMinStopAtrMult, double greenMinTargetAtrMult, double yellowMinStopAtrMult, double yellowMinTargetAtrMult, double redMinStopAtrMult, double redMinTargetAtrMult, double darkRedMinStopAtrMult, double darkRedMinTargetAtrMult, bool enableSmartTrader, Indicators.PraedoxSmartTraderMode mode, bool drawLabel)
        {
            return indicator.PraedoxVolatilityShield_v0_1_0(input, vrAtrLength, vrBaselineLength, yellowLowThreshold, redHighThreshold, darkRedThreshold, greenMinStopAtrMult, greenMinTargetAtrMult, yellowMinStopAtrMult, yellowMinTargetAtrMult, redMinStopAtrMult, redMinTargetAtrMult, darkRedMinStopAtrMult, darkRedMinTargetAtrMult, enableSmartTrader, mode, drawLabel);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.PraedoxVolatilityShield_v0_1_0 PraedoxVolatilityShield_v0_1_0(int vrAtrLength, int vrBaselineLength, double yellowLowThreshold, double redHighThreshold, double darkRedThreshold, double greenMinStopAtrMult, double greenMinTargetAtrMult, double yellowMinStopAtrMult, double yellowMinTargetAtrMult, double redMinStopAtrMult, double redMinTargetAtrMult, double darkRedMinStopAtrMult, double darkRedMinTargetAtrMult, bool enableSmartTrader, Indicators.PraedoxSmartTraderMode mode, bool drawLabel)
        {
            return indicator.PraedoxVolatilityShield_v0_1_0(Input, vrAtrLength, vrBaselineLength, yellowLowThreshold, redHighThreshold, darkRedThreshold, greenMinStopAtrMult, greenMinTargetAtrMult, yellowMinStopAtrMult, yellowMinTargetAtrMult, redMinStopAtrMult, redMinTargetAtrMult, darkRedMinStopAtrMult, darkRedMinTargetAtrMult, enableSmartTrader, mode, drawLabel);
        }

        public Indicators.PraedoxVolatilityShield_v0_1_0 PraedoxVolatilityShield_v0_1_0(ISeries<double> input, int vrAtrLength, int vrBaselineLength, double yellowLowThreshold, double redHighThreshold, double darkRedThreshold, double greenMinStopAtrMult, double greenMinTargetAtrMult, double yellowMinStopAtrMult, double yellowMinTargetAtrMult, double redMinStopAtrMult, double redMinTargetAtrMult, double darkRedMinStopAtrMult, double darkRedMinTargetAtrMult, bool enableSmartTrader, Indicators.PraedoxSmartTraderMode mode, bool drawLabel)
        {
            return indicator.PraedoxVolatilityShield_v0_1_0(input, vrAtrLength, vrBaselineLength, yellowLowThreshold, redHighThreshold, darkRedThreshold, greenMinStopAtrMult, greenMinTargetAtrMult, yellowMinStopAtrMult, yellowMinTargetAtrMult, redMinStopAtrMult, redMinTargetAtrMult, darkRedMinStopAtrMult, darkRedMinTargetAtrMult, enableSmartTrader, mode, drawLabel);
        }
    }
}

#endregion
