#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
using DX  = SharpDX;
using D2D = SharpDX.Direct2D1;
using DW  = SharpDX.DirectWrite;
#endregion

// ===========================================================================
// SneakyInsaneToOsc_v1_0_0  --  PORTING NOTES
// ===========================================================================
// V3 = your V2 NinjaTrader port brought up to "Insane Oscillator v4.0"
// (Pine Script v5). Everything in V2 that is not contradicted by v4 is kept
// (order-flow delta, Delta Divergence, vibrancy on the vector-candle arrows
// and the Waddah line, RSI Ribbon, bold line widths, sound alerts).
//
// NEW from Pine v4:
//   * 21 color themes + Custom (V2's 5 themes kept; OrderFlowLabs = old #4)
//   * Timeframe presets: Manual / Auto / Scalp / Intraday / Swing
//   * Compact ribbon meter layout (+ Classic V2 layout), ADX fade, fresh-flip
//     dots, Heikin-Ashi source for the meters, confluence meter + threshold
//   * HTF filter + HTF meter, reference-symbol filter  (extra data series)
//   * Session filter, regime filter, volume confirmation, signal cooldown
//   * Adaptive OB/OS (RSI Bollinger / percentile), OB/OS squares
//   * Gradient RSI style + RSI glow; MFI is now an accent line
//   * Exhaustion squares, pin-bar / engulfing flags, Stoch RSI crosses
//   * Hidden RSI + composite divergences, MFI divergence channel
//   * WaveTrend line, squeeze-tinted Waddah line
//   * Vodka Shot rewritten to the v4 formula (selectable DER MA / volume mode)
//   * Per-signal alerts with optional JSON messages, signal-bridge plot
//
// BEHAVIOUR CHANGES you may notice vs V2 (all because v4 changed the logic):
//   * Meter vibrancy: V2's per-meter vibrancy is replaced by v4's ADX fade
//     (weak ADX = dimmer) plus a full-bright color on the bar a meter flips.
//   * Vodka volume spike: v4 uses stdev of the AVERAGED volume (looser).
//     Toggle "Vodka volume spike: V2 formula" to get the V2 behaviour back.
//   * Vodka no longer requires the net-DER sign; LazyLine length is now the
//     fixed v4 triple WMA(7).
//   * RSI divergence runs on the main RSI (V2 used a separate "Shark RSI").
//   * ADX/DI is hand-computed with separate DI and ADX lengths (as Pine's
//     ta.dmi) instead of NinjaTrader's ADX().
//   * Divergence labels and backgrounds now respect the session / HTF /
//     reference / regime filters and the cooldown.
//   * RSI divergence Bull/Bear labels are now drawn in OnRender as pointer
//     boxes (SharpDX, modelled on NewNebula's Box marker), centered on the
//     pivot bar, instead of Draw.Text. Reversal arrows sit next to the 50 line.
//   * Alerts (sound + Alerts window) fire in realtime only, never while
//     historical bars are loading.
//
// NOT PORTED (no NinjaScript equivalent / deliberately skipped):
//   * The Pine OB/OS band shading and the gradient fills above OB / below OS
//     (deliberately left out; the RSI Ribbon, a fill from the RSI line to the
//     50 midline, is the only shading and is on by default).
//   * Custom price-source inputs (RSI/BB/WT/StochRSI use Close / HLC3).
//   * V2's unused WAE BB-channel inputs and Halftrend channel deviation.
//
// Plot indices 0-14 are unchanged from V2 (RSI, MFI, Mid, Vodka, Trip, HT,
// CCI, IMD, Rush, WAE, 5 flags) so existing references keep working; new
// plots are appended from index 15. Plot 23 is the signal-bridge code.
// ===========================================================================

public enum SneakyInsaneToOsc_v1_0_0Theme
{
	Standard, PinkyAndTheBrain, ColorBlind, EyePopper, Ocean, Sunset, Forest, Cyberpunk, Synthwave,
	Nord, Dracula, Solarized, Gruvbox, Monokai, TokyoNight, Catppuccin, FireAndIce, RoyalGold,
	Monochrome, PaperLightCharts, OrderFlowLabs, Custom
}
public enum SneakyInsaneToOsc_v1_0_0Preset { Manual, Auto, Scalp, Intraday, Swing }
public enum SneakyInsaneToOsc_v1_0_0MeterPos { CompactRibbon, Classic }
public enum SneakyInsaneToOsc_v1_0_0ObosMode { Fixed, AdaptiveRsiBollinger, AdaptivePercentile }
public enum SneakyInsaneToOsc_v1_0_0RsiStyle { Gradient, ClassicMfiTint }
public enum SneakyInsaneToOsc_v1_0_0VolCalc { Relative, Full, None }
public enum SneakyInsaneToOsc_v1_0_0MaType { WMA, EMA, SMA }

namespace NinjaTrader.NinjaScript.Indicators
{
	public class SneakyInsaneToOsc_v1_0_0 : Indicator
	{
		#region Inputs

		[NinjaScriptProperty]
		[Display(Name = "Color theme", Description = "Bull / bear / accent palette used by every line, dot, label and background. Custom uses the three colors below. OrderFlowLabs is the V2 theme 4.", GroupName = "1. Theme & Preset", Order = 0)]
		public SneakyInsaneToOsc_v1_0_0Theme ColorTheme { get; set; }

		[Range(10, 100)]
		[NinjaScriptProperty]
		[Display(Name = "Mild color opacity % (100 = V2 look, 60 = Pine v4 look)", Description = "Opacity of the 'mild' bull/bear colors. V2 forced these to 100; Pine v4 uses 60.", GroupName = "1. Theme & Preset", Order = 1)]
		public int MildOpacity { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Timeframe preset", Description = "Overrides Trampoline thresholds, Squeeze tolerance/ADX and the Waddah trigger. Manual uses the values in their own groups. Auto picks Scalp below 15m, Intraday below 4H, Swing above (tick/range/volume charts fall back to Intraday).", GroupName = "1. Theme & Preset", Order = 2)]
		public SneakyInsaneToOsc_v1_0_0Preset Preset { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "RSI line", GroupName = "2. Display", Order = 0)]
		public bool ShowRSI { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "RSI style", Description = "Gradient: bear-neutral-bull blend. Classic: V2 look, MFI-tinted with vibrancy.", GroupName = "2. Display", Order = 1)]
		public SneakyInsaneToOsc_v1_0_0RsiStyle RsiStyle { get; set; }

		[Range(1, 12)]
		[NinjaScriptProperty]
		[Display(Name = "RSI line width", GroupName = "2. Display", Order = 2)]
		public int RsiWidth { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "RSI glow", GroupName = "2. Display", Order = 3)]
		public bool ShowGlow { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "OB/OS band + midline", GroupName = "2. Display", Order = 4)]
		public bool ShowBands { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Trampoline 'T'", GroupName = "2. Display", Order = 5)]
		public bool ShowTramp { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Squeeze dots", GroupName = "2. Display", Order = 6)]
		public bool ShowSqueeze { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "BB wicking", GroupName = "2. Display", Order = 7)]
		public bool ShowBB { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Vector candles (PVSRA)", GroupName = "2. Display", Order = 8)]
		public bool ShowVector { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Exhaustion squares", GroupName = "2. Display", Order = 9)]
		public bool ShowExh { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Pin bar / engulfing", GroupName = "2. Display", Order = 10)]
		public bool ShowPatterns { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Stoch RSI crosses", GroupName = "2. Display", Order = 11)]
		public bool ShowStochX { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "WaveTrend line", GroupName = "2. Display", Order = 12)]
		public bool ShowWT { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Waddah explosion", GroupName = "2. Display", Order = 13)]
		public bool ShowWAE { get; set; }

		[Range(1, 10)]
		[NinjaScriptProperty]
		[Display(Name = "Waddah explosion thickness (min 3 applied, as in V2)", GroupName = "2. Display", Order = 14)]
		public int ThickWaddah { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Tint Waddah line while a squeeze is on", GroupName = "2. Display", Order = 15)]
		public bool SqzOnWAE { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "MFI line", GroupName = "2. Display", Order = 16)]
		public bool ShowMFI { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "RSI divergence labels", GroupName = "2. Display", Order = 17)]
		public bool ShowDivLabels { get; set; }

		[Range(6, 24)]
		[NinjaScriptProperty]
		[Display(Name = "Divergence label font size", GroupName = "2. Display", Order = 18)]
		public int DivLabelSize { get; set; }

		[Range(0, 40)]
		[NinjaScriptProperty]
		[Display(Name = "Divergence label gap from the RSI pivot (pixels)", Description = "Distance between the label's pointer tip and the pivot point. Bull labels hang below the pivot, Bear labels sit above it.", GroupName = "2. Display", Order = 19)]
		public int DivLabelGap { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Include hidden divergences", Description = "Hidden divergences (trend continuation) get lighter labels and lighter background shading.", GroupName = "2. Display", Order = 20)]
		public bool ShowHidden { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Divergence background bars", GroupName = "2. Display", Order = 21)]
		public bool ShowDivies { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Buy/sell background bars", GroupName = "2. Display", Order = 22)]
		public bool ShowBuySellies { get; set; }

		[Range(1, 100)]
		[NinjaScriptProperty]
		[Display(Name = "Background transparency (100 = invisible, 0 = opaque)", GroupName = "2. Display", Order = 23)]
		public int BgTrans { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Color price candles by confluence", GroupName = "2. Display", Order = 24)]
		public bool BarColorConf { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Delta Divergence (V2 extra)", Description = "Not in the Pine script: price makes a fractal high/low while session cumulative order-flow delta disagrees. Needs bid/ask tick data.", GroupName = "2. Display", Order = 25)]
		public bool ShowDeltaDivergence { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Shade RSI to midline (RSI Ribbon)", Description = "Translucent fill between the RSI line and the 50 midline: above 50 when RSI is above it, below 50 when RSI is below it. Stops at 50. One drawing object per bar.", GroupName = "2. Display", Order = 26)]
		public bool ShowRSIRibbon { get; set; }

		[Range(10, 99)]
		[NinjaScriptProperty]
		[Display(Name = "Ribbon transparency (100 = invisible)", GroupName = "2. Display", Order = 27)]
		public int RibbonTransp { get; set; }

		[Range(1, 40)]
		[NinjaScriptProperty]
		[Display(Name = "Reversal arrows: distance from the 50 midline", Description = "Pin bar / engulfing / Stoch RSI arrows sit this far below (bull) or above (bear) the midline.", GroupName = "2. Display", Order = 28)]
		public int ArrowGap { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show trend meters", GroupName = "3. Trend Meters", Order = 0)]
		public bool ShowTrend { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Position", Description = "CompactRibbon stacks the meters just beyond the OB/OS zone. Classic is the V2 layout (68-73 / 29-31).", GroupName = "3. Trend Meters", Order = 1)]
		public SneakyInsaneToOsc_v1_0_0MeterPos MeterPos { get; set; }

		[Range(72.0, 98.0)]
		[NinjaScriptProperty]
		[Display(Name = "Ribbon starts at", Description = "Top ribbon starts at this RSI level and stacks upward; the bottom ribbon mirrors it (100 minus this) and stacks downward.", GroupName = "3. Trend Meters", Order = 2)]
		public double StripIn { get; set; }

		[Range(0.5, 5.0)]
		[NinjaScriptProperty]
		[Display(Name = "Ribbon spacing", GroupName = "3. Trend Meters", Order = 3)]
		public double MeterGap { get; set; }

		[Range(1, 8)]
		[NinjaScriptProperty]
		[Display(Name = "Meter line width", GroupName = "3. Trend Meters", Order = 4)]
		public int MeterWidth { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Confluence meter", GroupName = "3. Trend Meters", Order = 5)]
		public bool ShowConf { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Mark fresh flips", GroupName = "3. Trend Meters", Order = 6)]
		public bool FlipDots { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Fade meters when ADX is weak", GroupName = "3. Trend Meters", Order = 7)]
		public bool AdxWeight { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Fade: ADX from", GroupName = "3. Trend Meters", Order = 8)]
		public int AdxFadeLo { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Fade: ADX to", GroupName = "3. Trend Meters", Order = 9)]
		public int AdxFadeHi { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Heikin-Ashi candles for meters", Description = "Feeds smoothed candles into Supertrend, Half Trend, CCI, KST, Impulse MACD and Bull Rush for fewer false flips.", GroupName = "3. Trend Meters", Order = 10)]
		public bool MeterHA { get; set; }

		[Range(1, 9)]
		[NinjaScriptProperty]
		[Display(Name = "Confluence threshold (meters agreeing)", GroupName = "4. Confluence & Filters", Order = 0)]
		public int ConfN { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Strict mode: WaveTrend buy/sell needs confluence", GroupName = "4. Confluence & Filters", Order = 1)]
		public bool Strict { get; set; }

		[Range(0, 10000)]
		[NinjaScriptProperty]
		[Display(Name = "Signal cooldown (bars, 0 = off)", Description = "Suppresses a repeat signal of the same type and direction within this many bars.", GroupName = "4. Confluence & Filters", Order = 2)]
		public int Cooldown { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Volume confirmation for Trampoline / Squeeze", GroupName = "4. Confluence & Filters", Order = 3)]
		public bool VolConfirm { get; set; }

		[Range(0.1, 100.0)]
		[NinjaScriptProperty]
		[Display(Name = "Volume multiple of 20-bar average", GroupName = "4. Confluence & Filters", Order = 4)]
		public double VolConfMult { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Regime filter", Description = "Trending = ADX above the level and Bollinger width expanding. Trending hides mean-reversion signals; ranging hides confluence signals.", GroupName = "4. Confluence & Filters", Order = 5)]
		public bool Regime { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Regime: ADX level", GroupName = "4. Confluence & Filters", Order = 6)]
		public int AdxTrend { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Higher-timeframe filter", Description = "Hides bull signals when the HTF triple supertrend is bearish and vice versa; adds an HTF meter and counts it in confluence. Uses the last closed HTF bar. Adds a second data series.", GroupName = "5. Higher Timeframe & Reference", Order = 0)]
		public bool UseHTF { get; set; }

		[Range(1, 100000)]
		[NinjaScriptProperty]
		[Display(Name = "HTF (minutes)", GroupName = "5. Higher Timeframe & Reference", Order = 1)]
		public int HTFMinutes { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Reference-symbol filter", Description = "Only allow signals that agree with the reference symbol's triple supertrend on the chart timeframe. Adds a data series.", GroupName = "5. Higher Timeframe & Reference", Order = 2)]
		public bool UseRef { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Reference instrument", Description = "NinjaTrader instrument name, e.g. SPY or ES 12-26.", GroupName = "5. Higher Timeframe & Reference", Order = 3)]
		public string RefSymbol { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Only signal inside session", Description = "Intraday time-based charts only. Compares bar close time, window is (start, end].", GroupName = "6. Session Filter", Order = 0)]
		public bool UseSession { get; set; }

		[Range(0, 2359)]
		[NinjaScriptProperty]
		[Display(Name = "Session start (HHmm)", GroupName = "6. Session Filter", Order = 1)]
		public int SessStart { get; set; }

		[Range(0, 2359)]
		[NinjaScriptProperty]
		[Display(Name = "Session end (HHmm)", GroupName = "6. Session Filter", Order = 2)]
		public int SessEnd { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Session time zone (Windows ID)", Description = "Windows time zone ID, e.g. Eastern Standard Time, GMT Standard Time, Tokyo Standard Time, UTC. Chart times are assumed to be in this PC's local zone.", GroupName = "6. Session Filter", Order = 3)]
		public string SessTZ { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Mode", Description = "Adaptive modes derive the levels from RSI's own recent behaviour. Squares, exhaustion and pattern checks follow them.", GroupName = "7. Overbought / Oversold Levels", Order = 0)]
		public SneakyInsaneToOsc_v1_0_0ObosMode ObosMode { get; set; }

		[Range(10, 5000)]
		[NinjaScriptProperty]
		[Display(Name = "Adaptive lookback", GroupName = "7. Overbought / Oversold Levels", Order = 1)]
		public int AdaptLen { get; set; }

		[Range(0.5, 10.0)]
		[NinjaScriptProperty]
		[Display(Name = "Bollinger multiplier", GroupName = "7. Overbought / Oversold Levels", Order = 2)]
		public double AdaptMult { get; set; }

		[Range(55, 99)]
		[NinjaScriptProperty]
		[Display(Name = "Percentile", GroupName = "7. Overbought / Oversold Levels", Order = 3)]
		public int AdaptPct { get; set; }

		[Range(1, int.MaxValue)]
		[NinjaScriptProperty]
		[Display(Name = "RSI length", GroupName = "8. RSI / MFI / Wicking", Order = 0)]
		public int RsiLength { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "RSI oversold", GroupName = "8. RSI / MFI / Wicking", Order = 1)]
		public int RsiOS { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "RSI overbought", GroupName = "8. RSI / MFI / Wicking", Order = 2)]
		public int RsiOB { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "RSI lower wick limit", GroupName = "8. RSI / MFI / Wicking", Order = 3)]
		public int RsiLowerW { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "RSI upper wick limit", GroupName = "8. RSI / MFI / Wicking", Order = 4)]
		public int RsiUpperW { get; set; }

		[Range(1, 2000)]
		[NinjaScriptProperty]
		[Display(Name = "MFI length", GroupName = "8. RSI / MFI / Wicking", Order = 5)]
		public int MfiLength { get; set; }

		[Range(0.0, 1.0)]
		[NinjaScriptProperty]
		[Display(Name = "Bollinger width threshold (Manual preset)", Description = "0.003 for daily, 0.0015 for 30 min candles", GroupName = "9. Trampoline", Order = 0)]
		public double IBBThreshold { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "RSI lower threshold (Manual preset)", GroupName = "9. Trampoline", Order = 1)]
		public int RSIThreshold { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "RSI upper threshold (Manual preset)", GroupName = "9. Trampoline", Order = 2)]
		public int RSIDown { get; set; }

		[Range(1, 1000)]
		[NinjaScriptProperty]
		[Display(Name = "Trampoline BB length", GroupName = "9. Trampoline", Order = 3)]
		public int LengthBB { get; set; }

		[Range(0.001, 50.0)]
		[NinjaScriptProperty]
		[Display(Name = "Trampoline BB StdDev", GroupName = "9. Trampoline", Order = 4)]
		public double MultBB { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Squeeze tolerance (Manual preset; lower = more sensitive)", GroupName = "10. Squeeze Relaxer / ADX", Order = 0)]
		public int SqTolerance { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "ADX threshold for squeeze (Manual preset)", GroupName = "10. Squeeze Relaxer / ADX", Order = 1)]
		public int AdxSqueeze { get; set; }

		[Range(1, 500)]
		[NinjaScriptProperty]
		[Display(Name = "ADX smoothing", GroupName = "10. Squeeze Relaxer / ADX", Order = 2)]
		public int AdxLen { get; set; }

		[Range(1, 500)]
		[NinjaScriptProperty]
		[Display(Name = "DI length", GroupName = "10. Squeeze Relaxer / ADX", Order = 3)]
		public int DiLen { get; set; }

		[Range(1, 1000)]
		[NinjaScriptProperty]
		[Display(Name = "Wicking BB length", GroupName = "11. Wicking BB / Half Trend", Order = 0)]
		public int WLengthBB { get; set; }

		[Range(0.001, 50.0)]
		[NinjaScriptProperty]
		[Display(Name = "Wicking BB StdDev", GroupName = "11. Wicking BB / Half Trend", Order = 1)]
		public double WMultBB { get; set; }

		[Range(1, 100)]
		[NinjaScriptProperty]
		[Display(Name = "Half Trend amplitude", GroupName = "11. Wicking BB / Half Trend", Order = 2)]
		public int Amplitude { get; set; }

		[Range(1, 500)]
		[NinjaScriptProperty]
		[Display(Name = "VADER length", Description = "rlength", GroupName = "12. Vodka Shot (VADER)", Order = 0)]
		public int VaderRLength { get; set; }

		[Range(1, 500)]
		[NinjaScriptProperty]
		[Display(Name = "VADER average", Description = "DER_avg", GroupName = "12. Vodka Shot (VADER)", Order = 1)]
		public int VaderDerAvg { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "DER MA type", GroupName = "12. Vodka Shot (VADER)", Order = 2)]
		public SneakyInsaneToOsc_v1_0_0MaType DerMaType { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Volume calculation", GroupName = "12. Vodka Shot (VADER)", Order = 3)]
		public SneakyInsaneToOsc_v1_0_0VolCalc VCalc { get; set; }

		[Range(1, 1000)]
		[NinjaScriptProperty]
		[Display(Name = "Lookback (for Relative)", GroupName = "12. Vodka Shot (VADER)", Order = 4)]
		public int VaderVLookback { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Vodka volume spike: V2 formula", Description = "Pine v4 takes the stdev of the 70-bar AVERAGE volume in the spike threshold; V2 used the stdev of raw volume (a much stricter spike). Off = v4 literal, On = V2 behaviour.", GroupName = "12. Vodka Shot (VADER)", Order = 5)]
		public bool VodkaLegacySpike { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Trigger threshold (Manual preset; lower = more sensitive)", GroupName = "13. Waddah Explosion", Order = 0)]
		public int Threshold { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Sensitivity", GroupName = "13. Waddah Explosion", Order = 1)]
		public int Sensitivity { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Fast EMA length", GroupName = "13. Waddah Explosion", Order = 2)]
		public int FastLength { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Slow EMA length", GroupName = "13. Waddah Explosion", Order = 3)]
		public int SlowLength { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "WT channel length", GroupName = "14. Shark WaveTrend / Divergences", Order = 0)]
		public int WtChannelLen { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "WT average length", GroupName = "14. Shark WaveTrend / Divergences", Order = 1)]
		public int WtAverageLen { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "WT MA length", GroupName = "14. Shark WaveTrend / Divergences", Order = 2)]
		public int WtMALen { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "WT overbought level", GroupName = "14. Shark WaveTrend / Divergences", Order = 3)]
		public int ObLevel { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "WT oversold level", GroupName = "14. Shark WaveTrend / Divergences", Order = 4)]
		public int OsLevel { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "WT bearish divergence min", GroupName = "14. Shark WaveTrend / Divergences", Order = 5)]
		public int WtDivOBLevel { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "WT bullish divergence min", GroupName = "14. Shark WaveTrend / Divergences", Order = 6)]
		public int WtDivOSLevel { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "WT 2nd bearish divergence", GroupName = "14. Shark WaveTrend / Divergences", Order = 7)]
		public int WtDivOBLevelAdd { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "WT 2nd bullish divergence", GroupName = "14. Shark WaveTrend / Divergences", Order = 8)]
		public int WtDivOSLevelAdd { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "RSI bearish divergence min", GroupName = "14. Shark WaveTrend / Divergences", Order = 9)]
		public int RsiDivOBLevel { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "RSI bullish divergence min", GroupName = "14. Shark WaveTrend / Divergences", Order = 10)]
		public int RsiDivOSLevel { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "MFI bearish divergence min", GroupName = "14. Shark WaveTrend / Divergences", Order = 11)]
		public int MfiDivOBLevel { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "MFI bullish divergence min", GroupName = "14. Shark WaveTrend / Divergences", Order = 12)]
		public int MfiDivOSLevel { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Stochastic RSI length", GroupName = "14. Shark WaveTrend / Divergences", Order = 13)]
		public int StochLen { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Stochastic RSI: RSI length", GroupName = "14. Shark WaveTrend / Divergences", Order = 14)]
		public int StochRsiLen { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Stochastic RSI K smooth", GroupName = "14. Shark WaveTrend / Divergences", Order = 15)]
		public int StochKSmooth { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Stochastic RSI D smooth", GroupName = "14. Shark WaveTrend / Divergences", Order = 16)]
		public int StochDSmooth { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable alerts (sound + Alerts window)", GroupName = "15. Alerts", Order = 0)]
		public bool EnableSoundAlerts { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "JSON alert messages (for webhook relays)", GroupName = "15. Alerts", Order = 1)]
		public bool AlertJSON { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Alert: Trampoline", GroupName = "15. Alerts", Order = 2)]
		public bool AlTramp { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Alert: Squeeze", GroupName = "15. Alerts", Order = 3)]
		public bool AlSqz { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Alert: WaveTrend buy/sell", GroupName = "15. Alerts", Order = 4)]
		public bool AlBG { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Alert: Divergences", GroupName = "15. Alerts", Order = 5)]
		public bool AlDiv { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Alert: Confluence", GroupName = "15. Alerts", Order = 6)]
		public bool AlConf { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Alert: Supertrend flip", GroupName = "15. Alerts", Order = 7)]
		public bool AlFlip { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Alert: Exhaustion", GroupName = "15. Alerts", Order = 8)]
		public bool AlExh { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Alert: Patterns", GroupName = "15. Alerts", Order = 9)]
		public bool AlPat { get; set; }

		[TypeConverter(typeof(SneakyInsaneToOsc_v1_0_0SoundConverter))]
		[Display(Name = "Squeeze Sound", Description = "Squeeze relaxer diamond", GroupName = "15. Alerts", Order = 10)]
		public string SqueezeSound { get; set; }

		[TypeConverter(typeof(SneakyInsaneToOsc_v1_0_0SoundConverter))]
		[Display(Name = "Trampoline Sound", Description = "Trampoline T marker", GroupName = "15. Alerts", Order = 11)]
		public string TrampolineSound { get; set; }

		[TypeConverter(typeof(SneakyInsaneToOsc_v1_0_0SoundConverter))]
		[Display(Name = "RSI Divergence Sound", Description = "RSI Bull/Bear divergence", GroupName = "15. Alerts", Order = 12)]
		public string DivergenceSound { get; set; }

		[TypeConverter(typeof(SneakyInsaneToOsc_v1_0_0SoundConverter))]
		[Display(Name = "Vector Candle Sound", Description = "PVSRA climax / above-average vector candle", GroupName = "15. Alerts", Order = 13)]
		public string VectorCandleSound { get; set; }

		[TypeConverter(typeof(SneakyInsaneToOsc_v1_0_0SoundConverter))]
		[Display(Name = "WaveTrend Sound", Description = "Shark WaveTrend buy/sell cross", GroupName = "15. Alerts", Order = 14)]
		public string WaveTrendSound { get; set; }

		[TypeConverter(typeof(SneakyInsaneToOsc_v1_0_0SoundConverter))]
		[Display(Name = "All Trend Meters Sound", Description = "Vodka + KST + Bull Rush + Triple Supertrend + CCI all agreeing", GroupName = "15. Alerts", Order = 15)]
		public string AllTrendMetersSound { get; set; }

		[TypeConverter(typeof(SneakyInsaneToOsc_v1_0_0SoundConverter))]
		[Display(Name = "Background Divergence Sound", Description = "WaveTrend / RSI / MFI / Stoch fractal divergence", GroupName = "15. Alerts", Order = 16)]
		public string BackgroundDivergenceSound { get; set; }

		[TypeConverter(typeof(SneakyInsaneToOsc_v1_0_0SoundConverter))]
		[Display(Name = "Delta Divergence Sound", Description = "Price vs session cumulative delta divergence (V2 extra)", GroupName = "15. Alerts", Order = 17)]
		public string DeltaDivergenceSound { get; set; }

		[TypeConverter(typeof(SneakyInsaneToOsc_v1_0_0SoundConverter))]
		[Display(Name = "Confluence Sound", Description = "Confluence threshold reached (new in v4)", GroupName = "15. Alerts", Order = 18)]
		public string ConfluenceSound { get; set; }

		[TypeConverter(typeof(SneakyInsaneToOsc_v1_0_0SoundConverter))]
		[Display(Name = "Supertrend Flip Sound", Description = "Triple Supertrend flip (new in v4)", GroupName = "15. Alerts", Order = 19)]
		public string FlipSound { get; set; }

		[TypeConverter(typeof(SneakyInsaneToOsc_v1_0_0SoundConverter))]
		[Display(Name = "Exhaustion Sound", Description = "Exhaustion square (new in v4)", GroupName = "15. Alerts", Order = 20)]
		public string ExhaustionSound { get; set; }

		[TypeConverter(typeof(SneakyInsaneToOsc_v1_0_0SoundConverter))]
		[Display(Name = "Pattern Sound", Description = "Pin bar / engulfing (new in v4)", GroupName = "15. Alerts", Order = 21)]
		public string PatternSound { get; set; }


		// Custom theme colors (Brush + serialization pair, standard NinjaTrader pattern)
		[XmlIgnore]
		[Display(Name = "Custom: bull", GroupName = "1. Theme & Preset", Order = 10)]
		public Brush CustomBull { get; set; }
		[Browsable(false)]
		public string CustomBullSerialize { get { return Serialize.BrushToString(CustomBull); } set { CustomBull = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Custom: bear", GroupName = "1. Theme & Preset", Order = 11)]
		public Brush CustomBear { get; set; }
		[Browsable(false)]
		public string CustomBearSerialize { get { return Serialize.BrushToString(CustomBear); } set { CustomBear = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Custom: accent", GroupName = "1. Theme & Preset", Order = 12)]
		public Brush CustomAccent { get; set; }
		[Browsable(false)]
		public string CustomAccentSerialize { get { return Serialize.BrushToString(CustomAccent); } set { CustomAccent = Serialize.StringToBrush(value); } }

		#endregion

		#region Plot indices

		private const int P_RSI = 0, P_MFI = 1, P_MID = 2, P_VODKA = 3, P_TRIP = 4, P_HT = 5, P_CCI = 6, P_IMD = 7, P_RUSH = 8, P_WAE = 9;
		private const int P_FLAG_RSIDIV = 10, P_FLAG_ALLMETERS = 11, P_FLAG_BGDIV = 12, P_FLAG_BUYSELL = 13, P_FLAG_DELTADIV = 14;
		private const int P_KST = 15, P_HTF = 16, P_CONF = 17, P_FLIP = 18, P_OB = 19, P_OS = 20, P_GLOW = 21, P_WT = 22, P_CODE = 23, P_ANYLONG = 24, P_ANYSHORT = 25;

		#endregion

		#region Series / state

		private Series<double> rsi, mfiSeries, cci1, cci2;
		private Series<double> valSq;
		private Series<double> adxS, dmTr, dmP, dmM;
		private Series<double> basisBB, upperBBt, lowerBBt, bbw;
		private Series<double> wbasisBB, wupperBB, wlowerBB;
		private Series<bool>   isRedSeries, isGreenSeries;
		private Series<double> haO, haC, mO, mH, mL, mC, mHlc3;
		private Series<double> hiMCD, loMCD, miMCD;
		private Series<double> kst, kstSig;
		private Series<double> waeT1;
		private Series<double> wt1, wt2;
		private Series<double> stochK, stochD, stochKRaw, logCloseSeries;
		private Series<double> vola, demSeries, supSeries, adp, asp, volAvgL;
		private Series<double> rsiUp, rsiDown;
		private Series<double> obxS, osxS;
		private Func<int, double> mHlc3Fn;
		private double[] pctScratch;

		// Divergence label markers, drawn in OnRender as Pine-style pointer boxes (same approach as NewNebula's Box marker)
		private sealed class DivMarker { public int Bar; public double Rsi; public bool Below; public string Text; public Color Fill, TextCol; }
		private readonly List<DivMarker> divMarkers = new List<DivMarker>();
		private readonly object sync = new object();
		private readonly Dictionary<uint, D2D.SolidColorBrush> dxBrushes = new Dictionary<uint, D2D.SolidColorBrush>();
		private readonly Dictionary<string, DW.TextFormat> fmts = new Dictionary<string, DW.TextFormat>();

		private SimpleFont markerFontBig;   // single-char markers: T, ss
		private SimpleFont markerFontArrow; // PVSRA / Delta Divergence triangle glyphs

		// --- Order flow delta: live tick classification via OnMarketData, Lee & Ready (1991) quote-rule
		// + tick-rule fallback for mid-quote prints -- same mechanism as this account's TONebulaRealFlow /
		// SneakyAggressionSplit / InsaneOscillator indicators. Swapped in here instead of NinjaTrader's
		// native OrderFlowCumulativeDelta, which needs the paid NinjaTrader order-flow/volumetric add-on
		// and a secondary tick series -- this needs neither. OnMarketData fires on its own thread, so
		// this gets its own lock, not 'sync'.
		private readonly object flowLock = new object();
		private double liveBuyVol, liveSellVol;
		private double prevBid = double.NaN, prevAsk = double.NaN, prevTradePrice = double.NaN;
		private bool haveQuote, lastSideWasBuy, realTimeFlowActive;
		private bool barDeltaCached = false;
		private double cachedBarDelta = 0;
		private Series<double> sessionCumDelta;
		private double sessionDeltaSum = 0;
		private DateTime lastDeltaSessionDate = DateTime.MinValue;

		// secondary series bookkeeping: 1 = tick (delta); HTF / reference are added only when enabled
		private int htfIdx = -1, refIdx = -1;
		private int htfDir = 0, refDir = 0;
		private TripleSt tripMain, tripHtf, tripRef;

		// RSI divergence (lookback 5/5)
		private const int lookbackLeft = 5, lookbackRight = 5, rangeUpper = 60, rangeLower = 5;
		private Dictionary<string, double> divLastHighSrc = new Dictionary<string, double>();
		private Dictionary<string, double> divLastHighPrice = new Dictionary<string, double>();
		private Dictionary<string, double> divLastLowSrc = new Dictionary<string, double>();
		private Dictionary<string, double> divLastLowPrice = new Dictionary<string, double>();
		private int barsSincePL = int.MaxValue, barsSincePH = int.MaxValue;
		private double prevPivotLowRsi = double.NaN, prevPivotLowLow = double.NaN;
		private double prevPivotHighRsi = double.NaN, prevPivotHighHigh = double.NaN;

		// trampoline thrust state
		private bool[] weGoUpHist = new bool[6];
		private bool[] weGoDownHist = new bool[6];

		// squeeze relaxer state
		private int cRed = 0, cGreen = 0;
		private bool posPrevBar = false, negPrevBar = false;

		// half trend state
		private int htTrend = 0, htNext = 0;
		private double htMaxLow = 0, htMinHigh = 0;
		private bool htInit = false;

		// meter state memory (fresh-flip detection): 0 vodka 1 trip 2 HT 3 CCI 4 KST 5 IMD 6 rush 7 WAE 8 HTF
		private int[] prevMeter = new int[9];
		private int[] stNow = new int[9];
		private bool havePrevMeter = false;
		private bool prevConfLong = false, prevConfShort = false;

		// cooldown memory per signal key
		private Dictionary<string, int> lastSigBar = new Dictionary<string, int>();

		// effective (preset-resolved) thresholds
		private double effBBThr, effRSILo, effRSIHi, effADXSq, effWAE;
		private int effSqTol;
		private TimeZoneInfo sessTz;

		#endregion

		#region Colors (theme)

		private static readonly int[] PalBull =
		{
			0x66ff00, 0x00f5f1, 0x03fcf4, 0xf8fc03, 0x00c2ff, 0xffd166, 0x52b788, 0x00fff0, 0x2de2e6,
			0xa3be8c, 0x50fa7b, 0x859900, 0xb8bb26, 0xa6e22e, 0x9ece6a, 0xa6e3a1, 0x48cae4, 0xffd700,
			0xffffff, 0x2e7d32, 0x339cff
		};
		private static readonly int[] PalBear =
		{
			0xff0000, 0xfc03f8, 0xfca903, 0xdf03fc, 0xff6b6b, 0xef476f, 0xbc4749, 0xff007c, 0xf6019d,
			0xbf616a, 0xff5555, 0xdc322f, 0xfb4934, 0xf92672, 0xf7768e, 0xf38ba8, 0xff5400, 0x9b59b6,
			0x7f7f7f, 0xc62828, 0xff2d55
		};
		private static readonly int[] PalAccent =
		{
			0xffeb3b, 0xffffff, 0xffffff, 0x00e5ff, 0xffd166, 0x06d6a0, 0xf2e8cf, 0xfcee0a, 0xff6c11,
			0xebcb8b, 0xbd93f9, 0xb58900, 0xfabd2f, 0xe6db74, 0x7aa2f7, 0xf9e2af, 0xffffff, 0xecf0f1,
			0xc8c8c8, 0x1565c0, 0xffeb3b
		};

		private Color cBull, cBear, cAccent, cNeutral;
		private Brush brBull, brBear, brAccent, brBullMild, brBearMild;
		private readonly Brush colorInvisible = Brushes.Transparent;
		private Dictionary<long, Brush> brushCache = new Dictionary<long, Brush>();

		private static Color Rgb(int rgb) { return Color.FromRgb((byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff)); }

		private static Brush FrozenBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

		private static Color BrushColor(Brush b, Color fallback)
		{
			var scb = b as SolidColorBrush;
			return scb != null ? scb.Color : fallback;
		}

		private void BuildPalette()
		{
			if (ColorTheme == SneakyInsaneToOsc_v1_0_0Theme.Custom)
			{
				cBull = BrushColor(CustomBull, Rgb(PalBull[0]));
				cBear = BrushColor(CustomBear, Rgb(PalBear[0]));
				cAccent = BrushColor(CustomAccent, Rgb(PalAccent[0]));
			}
			else
			{
				int t = (int)ColorTheme;
				cBull = Rgb(PalBull[t]); cBear = Rgb(PalBear[t]); cAccent = Rgb(PalAccent[t]);
			}
			cNeutral = Rgb(0x787B86);
			brushCache.Clear();
			int mildTransp = 100 - MildOpacity;
			brBull = GetBrush(cBull, 0); brBear = GetBrush(cBear, 0); brAccent = GetBrush(cAccent, 0);
			brBullMild = GetBrush(cBull, mildTransp); brBearMild = GetBrush(cBear, mildTransp);
		}

		// Cached, frozen brush for a color at a Pine-style transparency (0 = opaque, 100 = invisible).
		private Brush GetBrush(Color c, int transp)
		{
			transp = Math.Max(0, Math.Min(100, transp));
			long key = ((long)c.R << 32) | ((long)c.G << 24) | ((long)c.B << 16) | (long)(uint)transp;
			Brush b;
			if (brushCache.TryGetValue(key, out b)) return b;
			byte a = (byte)Math.Round(255.0 * (100 - transp) / 100.0);
			var sb = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
			sb.Freeze();
			brushCache[key] = sb;
			return sb;
		}

		// opacity01 version: 1 = opaque
		private Brush Tinted(Color c, double opacity01)
		{
			opacity01 = Math.Max(0, Math.Min(1, opacity01));
			return GetBrush(c, (int)Math.Round((1.0 - opacity01) * 100.0));
		}

		// Pine color.from_gradient with clamping; t quantized to 1/64 so the brush cache stays bounded.
		private Color Lerp(double v, double lo, double hi, Color a, Color b)
		{
			double t = hi == lo ? 1.0 : (v - lo) / (hi - lo);
			if (double.IsNaN(t)) t = 0;
			t = Math.Max(0, Math.Min(1, t));
			t = Math.Round(t * 64.0) / 64.0;
			return Color.FromRgb(
				(byte)Math.Round(a.R + (b.R - a.R) * t),
				(byte)Math.Round(a.G + (b.G - a.G) * t),
				(byte)Math.Round(a.B + (b.B - a.B) * t));
		}

		// V2 vibrancy scaling, kept for the vector-candle arrows
		private Brush ScaleOpacity(Brush baseBrush, double strength01)
		{
			if (!(baseBrush is SolidColorBrush)) return baseBrush;
			var scb = (SolidColorBrush)baseBrush;
			strength01 = Math.Max(0, Math.Min(1, strength01));
			const double minOpacity = 0.55, maxOpacity = 1.0;
			double opacity = minOpacity + (maxOpacity - minOpacity) * strength01;
			var c = scb.Color;
			byte a = (byte)Math.Round(Math.Min(255.0, c.A * opacity));
			var br = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
			br.Freeze();
			return br;
		}

		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Insane Oscillator v4 port: RSI core + stacked trend meters + confluence engine + reversal signals (RSI/MFI/CCI-cross/Trampoline/Squeeze/WickBB/PVSRA/ImpulseMACD/TripleSuperTrend/Halftrend/BullRush/KST/WAE/VADER/SharkWaveTrend)";
				Name = "SneakyInsaneToOsc_v1_0_0";
				Calculate = Calculate.OnBarClose;
				IsOverlay = false;
				DisplayInDataBox = true;
				DrawOnPricePanel = false;
				DrawHorizontalGridLines = true;
				DrawVerticalGridLines = true;
				PaintPriceMarkers = true;
				ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
				IsSuspendedWhileInactive = true;

				ColorTheme = SneakyInsaneToOsc_v1_0_0Theme.Standard;
				MildOpacity = 100;
				Preset = SneakyInsaneToOsc_v1_0_0Preset.Manual;
				ShowRSI = true;
				RsiStyle = SneakyInsaneToOsc_v1_0_0RsiStyle.Gradient;
				RsiWidth = 5;
				ShowGlow = true;
				ShowBands = true;
				ShowTramp = true;
				ShowSqueeze = true;
				ShowBB = true;
				ShowVector = false;
				ShowExh = true;
				ShowPatterns = true;
				ShowStochX = false;
				ShowWT = false;
				ShowWAE = true;
				ThickWaddah = 2;
				SqzOnWAE = true;
				ShowMFI = false;
				ShowDivLabels = true;
				DivLabelSize = 10;
				DivLabelGap = 4;
				ShowHidden = false;
				ShowDivies = false;
				ShowBuySellies = false;
				BgTrans = 85;
				BarColorConf = false;
				ShowDeltaDivergence = false;
				ShowRSIRibbon = true;
				RibbonTransp = 72;
				ArrowGap = 2;
				ShowTrend = true;
				MeterPos = SneakyInsaneToOsc_v1_0_0MeterPos.CompactRibbon;
				StripIn = 86;
				MeterGap = 1.5;
				MeterWidth = 3;
				ShowConf = true;
				FlipDots = true;
				AdxWeight = true;
				AdxFadeLo = 15;
				AdxFadeHi = 35;
				MeterHA = false;
				ConfN = 6;
				Strict = false;
				Cooldown = 0;
				VolConfirm = false;
				VolConfMult = 1.2;
				Regime = false;
				AdxTrend = 25;
				UseHTF = false;
				HTFMinutes = 240;
				UseRef = false;
				RefSymbol = "SPY";
				UseSession = false;
				SessStart = 930;
				SessEnd = 1600;
				SessTZ = "Eastern Standard Time";
				ObosMode = SneakyInsaneToOsc_v1_0_0ObosMode.Fixed;
				AdaptLen = 100;
				AdaptMult = 2.0;
				AdaptPct = 90;
				RsiLength = 14;
				RsiOS = 30;
				RsiOB = 70;
				RsiLowerW = 30;
				RsiUpperW = 70;
				MfiLength = 14;
				IBBThreshold = 0.0015;
				RSIThreshold = 25;
				RSIDown = 72;
				LengthBB = 20;
				MultBB = 2.0;
				SqTolerance = 2;
				AdxSqueeze = 21;
				AdxLen = 14;
				DiLen = 14;
				WLengthBB = 20;
				WMultBB = 2.5;
				Amplitude = 2;
				VaderRLength = 12;
				VaderDerAvg = 5;
				DerMaType = SneakyInsaneToOsc_v1_0_0MaType.WMA;
				VCalc = SneakyInsaneToOsc_v1_0_0VolCalc.Relative;
				VaderVLookback = 20;
				VodkaLegacySpike = false;
				Threshold = 120;
				Sensitivity = 150;
				FastLength = 20;
				SlowLength = 40;
				WtChannelLen = 9;
				WtAverageLen = 12;
				WtMALen = 3;
				ObLevel = 53;
				OsLevel = -53;
				WtDivOBLevel = 45;
				WtDivOSLevel = -65;
				WtDivOBLevelAdd = 15;
				WtDivOSLevelAdd = -40;
				RsiDivOBLevel = 60;
				RsiDivOSLevel = 30;
				MfiDivOBLevel = 65;
				MfiDivOSLevel = 35;
				StochLen = 14;
				StochRsiLen = 14;
				StochKSmooth = 3;
				StochDSmooth = 3;
				EnableSoundAlerts = false;
				AlertJSON = true;
				AlTramp = true;
				AlSqz = true;
				AlBG = true;
				AlDiv = true;
				AlConf = true;
				AlFlip = false;
				AlExh = false;
				AlPat = false;
				SqueezeSound = "Alert1.wav";
				TrampolineSound = "Alert2.wav";
				DivergenceSound = "Alert3.wav";
				VectorCandleSound = "Alert4.wav";
				WaveTrendSound = "Alert3.wav";
				AllTrendMetersSound = "Alert4.wav";
				BackgroundDivergenceSound = "Alert2.wav";
				DeltaDivergenceSound = "Alert1.wav";
				ConfluenceSound = "Alert3.wav";
				FlipSound = "Alert1.wav";
				ExhaustionSound = "Alert2.wav";
				PatternSound = "Alert4.wav";


				CustomBull = FrozenBrush(Color.FromRgb(0x66, 0xff, 0x00));
				CustomBear = FrozenBrush(Color.FromRgb(0xff, 0x00, 0x00));
				CustomAccent = FrozenBrush(Color.FromRgb(0xff, 0xeb, 0x3b));

				// Plots (indices 0-14 identical to V2; 15+ new). Widths are finalized in State.Configure.
				AddPlot(new Stroke(Brushes.DimGray, DashStyleHelper.Solid, 5), PlotStyle.Line, "RSI");   // 0
				AddPlot(new Stroke(Brushes.Gray, DashStyleHelper.Solid, 3), PlotStyle.Line, "MFI");      // 1
				AddPlot(Brushes.Gray, "MidLine50");              // 2
				AddPlot(Brushes.Gray, "VodkaShot");              // 3
				AddPlot(Brushes.Gray, "TripleSuperTrend");       // 4
				AddPlot(Brushes.Gray, "HalfTrend");              // 5
				AddPlot(Brushes.Gray, "CCIDoubleCross");         // 6
				AddPlot(Brushes.Gray, "ImpulseMACD");            // 7
				AddPlot(Brushes.Gray, "BullRush");               // 8
				AddPlot(Brushes.Gray, "WaddahExplosion");        // 9

				// Hidden 0/1 flag plots: select via Tools > Alerts > Indicator Value > pick the plot > "Crosses Above" 0.5
				AddPlot(Brushes.Transparent, "RsiDivergenceFlag");        // 10
				AddPlot(Brushes.Transparent, "AllTrendMetersActiveFlag"); // 11
				AddPlot(Brushes.Transparent, "BackgroundDivergenceFlag"); // 12
				AddPlot(Brushes.Transparent, "BuySellSignalFlag");        // 13
				AddPlot(Brushes.Transparent, "DeltaDivergenceFlag");      // 14

				AddPlot(Brushes.Gray, "KST");                    // 15
				AddPlot(Brushes.Gray, "HTFTrend");               // 16
				AddPlot(Brushes.Gray, "Confluence");             // 17
				AddPlot(new Stroke(Brushes.Gray, DashStyleHelper.Solid, 5), PlotStyle.Dot, "MeterFlip"); // 18
				AddPlot(new Stroke(Brushes.DarkGray, DashStyleHelper.Dash, 1), PlotStyle.Line, "OBLevel"); // 19
				AddPlot(new Stroke(Brushes.DarkGray, DashStyleHelper.Dash, 1), PlotStyle.Line, "OSLevel"); // 20
				AddPlot(new Stroke(Brushes.Gray, DashStyleHelper.Solid, 9), PlotStyle.Line, "RSIGlow");    // 21
				AddPlot(new Stroke(Brushes.Gray, DashStyleHelper.Solid, 2), PlotStyle.Line, "WaveTrend");  // 22
				AddPlot(Brushes.Transparent, "SignalCode");      // 23 (bridge: long bits 0-9, short bits 10-19 x1024)
				AddPlot(Brushes.Transparent, "AnyLongFlag");     // 24
				AddPlot(Brushes.Transparent, "AnyShortFlag");    // 25
			}
			else if (State == State.Configure)
			{
				Plots[P_RSI].Width = RsiWidth;
				Plots[P_GLOW].Width = RsiWidth + 4;
				Plots[P_MFI].Width = 3;
				Plots[P_MID].Width = 1;
				Plots[P_VODKA].Width = MeterWidth;
				Plots[P_TRIP].Width = MeterWidth;
				Plots[P_HT].Width = MeterWidth;
				Plots[P_CCI].Width = MeterWidth;
				Plots[P_IMD].Width = MeterWidth;
				Plots[P_RUSH].Width = MeterWidth;
				Plots[P_KST].Width = MeterWidth;
				Plots[P_HTF].Width = MeterWidth;
				Plots[P_CONF].Width = MeterWidth + 2;
				Plots[P_FLIP].Width = MeterWidth + 2;
				Plots[P_WAE].Width = Math.Max(3, ThickWaddah);

				// Secondary series. Order-flow delta no longer needs its own tick series -- OnMarketData
				// subscribes to the primary instrument's live quotes/trades directly -- so HTF and
				// reference series are appended starting at index 1 instead of 2.
				int nextIdx = 1;
				if (UseHTF)
				{
					AddDataSeries(Data.BarsPeriodType.Minute, Math.Max(1, HTFMinutes));
					htfIdx = nextIdx++;
				}
				else htfIdx = -1;
				if (UseRef && !string.IsNullOrWhiteSpace(RefSymbol))
				{
					AddDataSeries(RefSymbol.Trim(), BarsPeriod.BarsPeriodType, BarsPeriod.Value);
					refIdx = nextIdx++;
				}
				else refIdx = -1;
			}
			else if (State == State.DataLoaded)
			{
				BuildPalette();
				ResolvePreset();

				try { sessTz = TimeZoneInfo.FindSystemTimeZoneById(SessTZ); }
				catch { sessTz = null; }

				rsi = new Series<double>(this, MaximumBarsLookBack.Infinite); mfiSeries = new Series<double>(this, MaximumBarsLookBack.Infinite);
				cci1 = new Series<double>(this, MaximumBarsLookBack.Infinite); cci2 = new Series<double>(this, MaximumBarsLookBack.Infinite);
				valSq = new Series<double>(this, MaximumBarsLookBack.Infinite);
				adxS = new Series<double>(this, MaximumBarsLookBack.Infinite); dmTr = new Series<double>(this, MaximumBarsLookBack.Infinite); dmP = new Series<double>(this, MaximumBarsLookBack.Infinite); dmM = new Series<double>(this, MaximumBarsLookBack.Infinite);
				basisBB = new Series<double>(this, MaximumBarsLookBack.Infinite); upperBBt = new Series<double>(this, MaximumBarsLookBack.Infinite); lowerBBt = new Series<double>(this, MaximumBarsLookBack.Infinite); bbw = new Series<double>(this, MaximumBarsLookBack.Infinite);
				wbasisBB = new Series<double>(this, MaximumBarsLookBack.Infinite); wupperBB = new Series<double>(this, MaximumBarsLookBack.Infinite); wlowerBB = new Series<double>(this, MaximumBarsLookBack.Infinite);
				isRedSeries = new Series<bool>(this, MaximumBarsLookBack.Infinite); isGreenSeries = new Series<bool>(this, MaximumBarsLookBack.Infinite);
				haO = new Series<double>(this, MaximumBarsLookBack.Infinite); haC = new Series<double>(this, MaximumBarsLookBack.Infinite);
				mO = new Series<double>(this, MaximumBarsLookBack.Infinite); mH = new Series<double>(this, MaximumBarsLookBack.Infinite); mL = new Series<double>(this, MaximumBarsLookBack.Infinite); mC = new Series<double>(this, MaximumBarsLookBack.Infinite); mHlc3 = new Series<double>(this, MaximumBarsLookBack.Infinite);
				hiMCD = new Series<double>(this, MaximumBarsLookBack.Infinite); loMCD = new Series<double>(this, MaximumBarsLookBack.Infinite); miMCD = new Series<double>(this, MaximumBarsLookBack.Infinite);
				kst = new Series<double>(this, MaximumBarsLookBack.Infinite); kstSig = new Series<double>(this, MaximumBarsLookBack.Infinite);
				waeT1 = new Series<double>(this, MaximumBarsLookBack.Infinite);
				wt1 = new Series<double>(this, MaximumBarsLookBack.Infinite); wt2 = new Series<double>(this, MaximumBarsLookBack.Infinite);
				stochK = new Series<double>(this, MaximumBarsLookBack.Infinite); stochD = new Series<double>(this, MaximumBarsLookBack.Infinite);
				stochKRaw = new Series<double>(this, MaximumBarsLookBack.Infinite); logCloseSeries = new Series<double>(this, MaximumBarsLookBack.Infinite);
				vola = new Series<double>(this, MaximumBarsLookBack.Infinite); demSeries = new Series<double>(this, MaximumBarsLookBack.Infinite); supSeries = new Series<double>(this, MaximumBarsLookBack.Infinite);
				adp = new Series<double>(this, MaximumBarsLookBack.Infinite); asp = new Series<double>(this, MaximumBarsLookBack.Infinite); volAvgL = new Series<double>(this, MaximumBarsLookBack.Infinite);
				rsiUp = new Series<double>(this, MaximumBarsLookBack.Infinite); rsiDown = new Series<double>(this, MaximumBarsLookBack.Infinite);
				obxS = new Series<double>(this, MaximumBarsLookBack.Infinite); osxS = new Series<double>(this, MaximumBarsLookBack.Infinite);
				sessionCumDelta = new Series<double>(this, MaximumBarsLookBack.Infinite);
				mHlc3Fn = i => mHlc3[i];
				pctScratch = new double[Math.Max(10, AdaptLen) + 2];

				markerFontBig = new SimpleFont("Arial", 20) { Bold = true };
				markerFontArrow = new SimpleFont("Arial", 28) { Bold = true };

				sessionDeltaSum = 0;
				lastDeltaSessionDate = DateTime.MinValue;
				barDeltaCached = false; cachedBarDelta = 0;
				lock (flowLock) { liveBuyVol = 0; liveSellVol = 0; prevBid = double.NaN; prevAsk = double.NaN; prevTradePrice = double.NaN; haveQuote = false; lastSideWasBuy = false; realTimeFlowActive = false; }

				// Reset every non-Series stateful field so an indicator recalculation starts clean.
				divLastHighSrc.Clear(); divLastHighPrice.Clear();
				divLastLowSrc.Clear(); divLastLowPrice.Clear();
				barsSincePL = int.MaxValue; barsSincePH = int.MaxValue;
				prevPivotLowRsi = double.NaN; prevPivotLowLow = double.NaN;
				prevPivotHighRsi = double.NaN; prevPivotHighHigh = double.NaN;
				weGoUpHist = new bool[6]; weGoDownHist = new bool[6];
				cRed = 0; cGreen = 0;
				posPrevBar = false; negPrevBar = false;
				htTrend = 0; htNext = 0; htMaxLow = 0; htMinHigh = 0; htInit = false;
				prevMeter = new int[9]; stNow = new int[9]; havePrevMeter = false;
				prevConfLong = false; prevConfShort = false;
				lastSigBar.Clear();
				lock (sync) { divMarkers.Clear(); }
				emaCache.Clear(); emaPrevCache.Clear();
				rollingBuffers.Clear();
				tripMain = new TripleSt(); tripHtf = new TripleSt(); tripRef = new TripleSt();
				htfDir = 0; refDir = 0;
				lastVC = 0; llPrev = double.NaN; lastConfScore = 0;
			}
		}

		// Preset resolution (Pine: presetEff / eBBThr / eRSILo / eRSIHi / eSqTol / eADXSq / eWAE)
		private void ResolvePreset()
		{
			SneakyInsaneToOsc_v1_0_0Preset p = Preset;
			if (p == SneakyInsaneToOsc_v1_0_0Preset.Auto)
			{
				double tfSec = -1;
				switch (BarsPeriod.BarsPeriodType)
				{
					case Data.BarsPeriodType.Second: tfSec = BarsPeriod.Value; break;
					case Data.BarsPeriodType.Minute: tfSec = BarsPeriod.Value * 60.0; break;
					case Data.BarsPeriodType.Day: tfSec = BarsPeriod.Value * 86400.0; break;
					case Data.BarsPeriodType.Week: tfSec = BarsPeriod.Value * 604800.0; break;
					case Data.BarsPeriodType.Month: tfSec = BarsPeriod.Value * 2592000.0; break;
					case Data.BarsPeriodType.Year: tfSec = BarsPeriod.Value * 31536000.0; break;
				}
				if (tfSec < 0) p = SneakyInsaneToOsc_v1_0_0Preset.Intraday;
				else p = tfSec < 900 ? SneakyInsaneToOsc_v1_0_0Preset.Scalp : tfSec < 14400 ? SneakyInsaneToOsc_v1_0_0Preset.Intraday : SneakyInsaneToOsc_v1_0_0Preset.Swing;
			}
			bool man = p == SneakyInsaneToOsc_v1_0_0Preset.Manual, scalp = p == SneakyInsaneToOsc_v1_0_0Preset.Scalp, intra = p == SneakyInsaneToOsc_v1_0_0Preset.Intraday;
			effBBThr = man ? IBBThreshold : scalp ? 0.0008 : intra ? 0.0015 : 0.003;
			effRSILo = man ? RSIThreshold : scalp ? 22 : intra ? 25 : 28;
			effRSIHi = man ? RSIDown : scalp ? 78 : intra ? 72 : 70;
			effSqTol = man ? SqTolerance : scalp ? 3 : intra ? 2 : 1;
			effADXSq = man ? AdxSqueeze : scalp ? 23 : intra ? 21 : 18;
			effWAE = man ? Threshold : scalp ? 150 : intra ? 120 : 100;
		}

		private double lastVC = 0, llPrev = double.NaN;
		private int lastConfScore = 0;

		protected override void OnBarUpdate()
		{
			// ---- secondary series: HTF, reference (order-flow delta no longer uses one -- see OnMarketData) ----
			if (htfIdx >= 0 && BarsInProgress == htfIdx) { htfDir = tripHtf.Update(High[0], Low[0], Close[0]); return; }
			if (refIdx >= 0 && BarsInProgress == refIdx) { refDir = tripRef.Update(High[0], Low[0], Close[0]); return; }
			if (BarsInProgress != 0) return;

			// ---- meter source candles (optionally Heikin-Ashi), valid from bar 0 ----
			double haCloseNow = (Open[0] + High[0] + Low[0] + Close[0]) / 4.0;
			double haOpenNow = CurrentBar == 0 ? (Open[0] + Close[0]) / 2.0 : (haO[1] + haC[1]) / 2.0;
			haO[0] = haOpenNow; haC[0] = haCloseNow;
			double haHighNow = Math.Max(High[0], Math.Max(haOpenNow, haCloseNow));
			double haLowNow = Math.Min(Low[0], Math.Min(haOpenNow, haCloseNow));
			mO[0] = MeterHA ? haOpenNow : Open[0];
			mH[0] = MeterHA ? haHighNow : High[0];
			mL[0] = MeterHA ? haLowNow : Low[0];
			mC[0] = MeterHA ? haCloseNow : Close[0];
			mHlc3[0] = (mH[0] + mL[0] + mC[0]) / 3.0;

			if (CurrentBar < 2) return;
			barDeltaCached = false;
			UpdateSessionCumDelta();

			// ===============================================================
			// CORE: RSI (Wilder RMA), MFI, adaptive OB/OS
			// ===============================================================
			double change = Close[0] - Close[1];
			double upVal = Math.Max(change, 0);
			double dnVal = Math.Max(-change, 0);
			rsiUp[0]   = CurrentBar == 2 ? upVal : (rsiUp[1] * (RsiLength - 1) + upVal) / RsiLength;
			rsiDown[0] = CurrentBar == 2 ? dnVal : (rsiDown[1] * (RsiLength - 1) + dnVal) / RsiLength;
			rsi[0] = rsiDown[0] == 0 ? 100 : (rsiUp[0] == 0 ? 0 : 100 - 100 / (1 + rsiUp[0] / rsiDown[0]));
			mfiSeries[0] = MFI(MfiLength)[0];

			double obx, osx;
			ComputeObos(out obx, out osx);
			obxS[0] = obx; osxS[0] = osx;
			double obxPrev = CurrentBar >= 3 ? obxS[1] : obx;
			double osxPrev = CurrentBar >= 3 ? osxS[1] : osx;

			// ===============================================================
			// VOLUME, SESSION, DMI / ADX
			// ===============================================================
			double volAvg20 = SMA_Volume(20);
			bool volOk = !VolConfirm || Volume[0] >= volAvg20 * VolConfMult;
			bool inSession = InSessionNow();

			double upMove = High[0] - High[1], dnMove = Low[1] - Low[0];
			double pDM = (upMove > dnMove && upMove > 0) ? upMove : 0;
			double mDM = (dnMove > upMove && dnMove > 0) ? dnMove : 0;
			dmTr[0] = RmaStep(dmTr, TrueRange(0), DiLen);
			dmP[0] = RmaStep(dmP, pDM, DiLen);
			dmM[0] = RmaStep(dmM, mDM, DiLen);
			double diPlus = dmTr[0] == 0 ? 0 : 100.0 * dmP[0] / dmTr[0];
			double diMinus = dmTr[0] == 0 ? 0 : 100.0 * dmM[0] / dmTr[0];
			double diSum = diPlus + diMinus;
			double dx = diSum == 0 ? 0 : 100.0 * Math.Abs(diPlus - diMinus) / diSum;
			adxS[0] = RmaStep(adxS, dx, AdxLen);
			double adxValue = adxS[0];

			// ===============================================================
			// CCI double cross (hlc3 of the meter source)
			// ===============================================================
			double ma14 = SMA_Series(mHlc3, 14);
			double dev14 = MeanDev(mHlc3Fn, 14);
			cci1[0] = dev14 == 0 ? 0 : (mHlc3[0] - ma14) / (0.015 * dev14);
			double dev100 = MeanDev(mHlc3Fn, 100);   // 14-bar mean kept from v2 so signals match
			cci2[0] = dev100 == 0 ? 0 : (mHlc3[0] - ma14) / (0.015 * dev100);
			bool cciWasAbove = false;
			for (int k = 1; k <= 7 && CurrentBar - k >= 0; k++)
				if (cci1[k] > cci2[k]) { cciWasAbove = true; break; }
			bool cciBull = cci1[0] > cci2[0] && cci1[0] >= 100;
			bool cciBear = cci1[0] < cci2[0] && cci1[0] < -100 && cciWasAbove;

			// ===============================================================
			// RSI pivot divergence (regular + hidden), lookback 5/5
			// ===============================================================
			bool plFound = false, phFound = false;
			if (CurrentBar >= lookbackLeft + lookbackRight)
			{
				double centerRsi = rsi[lookbackRight];
				bool isPL = true, isPH = true;
				for (int i = 0; i <= lookbackLeft + lookbackRight; i++)
				{
					int barsAgo = lookbackRight + lookbackLeft - i;
					if (barsAgo == lookbackRight) continue;
					if (rsi[barsAgo] <= centerRsi) isPL = false;
					if (rsi[barsAgo] >= centerRsi) isPH = false;
				}
				plFound = isPL; phFound = isPH;
			}

			// Pine: _inRange(plFound[1]) = bars since the previous pivot AS OF the previous bar,
			// so the range check happens before the counters are advanced.
			bool bullCond = false, bearCond = false, hBullCond = false, hBearCond = false;
			if (plFound)
			{
				bool inRange = barsSincePL >= rangeLower && barsSincePL <= rangeUpper;
				double r5 = rsi[lookbackRight], l5 = Low[lookbackRight];
				if (!double.IsNaN(prevPivotLowRsi) && inRange)
				{
					bullCond = r5 > prevPivotLowRsi && l5 < prevPivotLowLow;
					hBullCond = r5 < prevPivotLowRsi && l5 > prevPivotLowLow;
				}
				prevPivotLowRsi = r5; prevPivotLowLow = l5;
			}
			if (phFound)
			{
				bool inRange = barsSincePH >= rangeLower && barsSincePH <= rangeUpper;
				double r5 = rsi[lookbackRight], h5 = High[lookbackRight];
				if (!double.IsNaN(prevPivotHighRsi) && inRange)
				{
					bearCond = r5 < prevPivotHighRsi && h5 > prevPivotHighHigh;
					hBearCond = r5 > prevPivotHighRsi && h5 < prevPivotHighHigh;
				}
				prevPivotHighRsi = r5; prevPivotHighHigh = h5;
			}
			barsSincePL = plFound ? 0 : (barsSincePL == int.MaxValue ? int.MaxValue : barsSincePL + 1);
			barsSincePH = phFound ? 0 : (barsSincePH == int.MaxValue ? int.MaxValue : barsSincePH + 1);

			// ===============================================================
			// TRAMPOLINE + REGIME
			// ===============================================================
			basisBB[0] = SMA_Series(Close, LengthBB);
			double devBBv = MultBB * StdDev_Series(Close, LengthBB);
			upperBBt[0] = basisBB[0] + devBBv;
			lowerBBt[0] = basisBB[0] - devBBv;
			bbw[0] = basisBB[0] == 0 ? 0 : (upperBBt[0] - lowerBBt[0]) / basisBB[0];
			isRedSeries[0] = Close[0] < Open[0];
			isGreenSeries[0] = Close[0] > Open[0];

			bool weGoUp = false, weGoDown = false;
			if (CurrentBar >= 5)
			{
				bool back1to5 = false, for1to5 = false;
				for (int k = 1; k <= 5; k++)
				{
					if (isRedSeries[k] && rsi[k] <= effRSILo && Close[k] < lowerBBt[k] && bbw[k] > effBBThr) back1to5 = true;
					if (isGreenSeries[k] && rsi[k] >= effRSIHi && Close[k] > upperBBt[k] && bbw[k] > effBBThr) for1to5 = true;
				}
				weGoUp = isGreenSeries[0] && back1to5 && (High[0] > High[1]);
				weGoDown = isRedSeries[0] && for1to5 && (Low[0] < Low[1]);
			}
			for (int i = 5; i >= 1; i--) { weGoUpHist[i] = weGoUpHist[i - 1]; weGoDownHist[i] = weGoDownHist[i - 1]; }
			weGoUpHist[0] = weGoUp; weGoDownHist[0] = weGoDown;
			bool upThrust = weGoUpHist[0] && !weGoUpHist[1] && !weGoUpHist[2] && !weGoUpHist[3] && !weGoUpHist[4];
			bool downThrust = weGoDownHist[0] && !weGoDownHist[1] && !weGoDownHist[2] && !weGoDownHist[3] && !weGoDownHist[4];

			bool trending = adxValue >= AdxTrend && bbw[0] > SMA_Series(bbw, 50);

			// ===============================================================
			// SQUEEZE RELAXER 2.1
			// ===============================================================
			bool adxOk = adxValue > effADXSq;
			double sqBasis = SMA_Series(Close, 20);
			bool sqzOn = StdDev_Series(Close, 20) < SMA_Range(20);   // BB(20,1.5) inside KC(20,1.5)
			double avg1 = (HighestVal(High, 20) + LowestVal(Low, 20)) / 2.0;
			double avg2 = (avg1 + sqBasis) / 2.0;
			valSq[0] = LinRegClose(avg2, 20);

			bool pos = false, neg = false;
			double prevVal = CurrentBar >= 1 ? valSq[1] : valSq[0];
			if (valSq[0] < prevVal && valSq[0] < 5 && !sqzOn) cRed++;
			if (valSq[0] > prevVal && valSq[0] > 5 && !sqzOn) cGreen++;
			if (valSq[0] > prevVal && cRed > effSqTol && valSq[0] < 5 && !posPrevBar && adxOk) { cRed = 0; pos = true; }
			if (valSq[0] < prevVal && cGreen > effSqTol && valSq[0] > 5 && !negPrevBar && adxOk) { cGreen = 0; neg = true; }
			bool sqzBuy = pos, sqzSell = neg;
			posPrevBar = pos; negPrevBar = neg;

			// ===============================================================
			// WICKING BB & CANDLE ANATOMY
			// ===============================================================
			wbasisBB[0] = SMA_Series(Close, WLengthBB);
			double wdev = WMultBB * StdDev_Series(Close, WLengthBB);
			wupperBB[0] = wbasisBB[0] + wdev; wlowerBB[0] = wbasisBB[0] - wdev;

			double body = Math.Abs(Close[0] - Open[0]);
			double rng = High[0] - Low[0];
			double upperWick = High[0] - Math.Max(Open[0], Close[0]);
			double lowerWick = Math.Min(Open[0], Close[0]) - Low[0];
			bool upWickBig = Close[0] > Open[0] && Math.Abs(High[0] - Close[0]) > body;
			bool downWickBig = Close[0] < Open[0] && Math.Abs(Low[0] - Close[0]) > body;

			// ===============================================================
			// PVSRA / VECTOR CANDLES (exact port of calcPvsra, no symbol override)
			// ===============================================================
			bool pvsraAlert = false, pvsraBig = false;
			if (CurrentBar >= 10)
			{
				double avgVol10 = 0;
				for (int k = 1; k <= 10; k++) avgVol10 += Volume[k];
				avgVol10 /= 10.0;
				double volumeSpread = Volume[0] * (High[0] - Low[0]);
				double highestVolumeSpread = double.MinValue;
				for (int k = 1; k <= 10; k++)
				{
					double vs = Volume[k] * (High[k] - Low[k]);
					if (vs > highestVolumeSpread) highestVolumeSpread = vs;
				}
				if (Volume[0] >= 2 * avgVol10 || volumeSpread >= highestVolumeSpread)
				{
					pvsraBig = true; pvsraAlert = true;
				}
				else if (Volume[0] >= 1.5 * avgVol10)
				{
					pvsraAlert = true;
				}
			}
			// Delta-refined vibrancy for the vector arrow (V2 extra)
			double barDeltaNowAll = GetBarDelta();
			double pvsraDeltaRatio = Volume[0] > 0 ? Math.Min(1, Math.Abs(barDeltaNowAll) / Volume[0]) : 0;
			bool pvsraDeltaAgrees = (Close[0] > Open[0] && barDeltaNowAll > 0) || (Close[0] < Open[0] && barDeltaNowAll < 0);
			double pvsraVibrancyStrength = pvsraDeltaAgrees ? Math.Min(1, pvsraDeltaRatio * 1.5) : 0.15;

			// ===============================================================
			// IMPULSE MACD (meter source)
			// ===============================================================
			hiMCD[0] = Smma(mH, hiMCD, 34);
			loMCD[0] = Smma(mL, loMCD, 34);
			double mcdEma1 = EMA_Series(mHlc3[0], 34, "mcdEma1");
			double mcdEma2 = EMA_Series(mcdEma1, 34, "mcdEma2");
			miMCD[0] = 2.0 * mcdEma1 - mcdEma2;
			bool imdStrong = mHlc3[0] > hiMCD[0] || mHlc3[0] < loMCD[0];

			// ===============================================================
			// TRIPLE SUPERTREND, HALF TREND, BULL RUSH, KST (meter source)
			// ===============================================================
			int tripDir = tripMain.Update(mH[0], mL[0], mC[0]);
			bool tripBuy = tripDir == 1, tripSell = !tripBuy;

			UpdateHalfTrend();

			double ema9 = EMA_Series(mC[0], 9, "br9");
			double ema21 = EMA_Series(mC[0], 21, "br21");
			double ema50 = EMA_Series(mC[0], 50, "br50");
			bool bullUp = ema9 > ema21 && mC[0] > ema50 && mO[0] > ema50;
			bool bullDown = ema9 < ema21 && mC[0] < ema50 && mO[0] < ema50;

			kst[0] = SmaRoc(10, 10) + 2 * SmaRoc(15, 10) + 3 * SmaRoc(20, 10) + 4 * SmaRoc(30, 15);
			kstSig[0] = SMA_Series(kst, 9);
			bool kstBull = kst[0] >= kstSig[0];

			// ===============================================================
			// VODKA SHOT (RedK Dual VADER + slow-smooth WMA), Pine v4 formula
			// ===============================================================
			double nzVolume = Volume[0];
			volAvgL[0] = SMA_Volume(70);
			double volDevSd = VodkaLegacySpike ? StdDev_Volume(70) : StdDev_Series(volAvgL, 70);
			double volDev = volAvgL[0] == 0 ? 0 : (volAvgL[0] + 1.618034 * volDevSd) / volAvgL[0] * 11.0 / 100.0;
			bool volSpike = volAvgL[0] > 0 && nzVolume / volAvgL[0] * 0.145898 > volDev;

			double volaNow = VCalc == SneakyInsaneToOsc_v1_0_0VolCalc.None ? 1.0
				: VCalc == SneakyInsaneToOsc_v1_0_0VolCalc.Relative ? StochOf(nzVolume, VaderVLookback) / 100.0
				: nzVolume;
			vola[0] = volaNow;
			double vRange = (Highest2(2) - Lowest2(2)) / 2.0;
			double vSr = vRange == 0 ? 0 : (Close[0] - Close[1]) / vRange;
			double vCraw = Math.Max(Math.Min(vSr, 1), -1) * volaNow;
			double vC = double.IsNaN(vCraw) ? lastVC : vCraw;   // fixnan
			lastVC = vC;

			double avgVolaV = DerMa(volaNow, "dmVola", VaderRLength);
			double demRaw = DerMa(Math.Max(vC, 0), "dmDem", VaderRLength);
			double supRaw = DerMa(-Math.Min(vC, 0), "dmSup", VaderRLength);
			demSeries[0] = avgVolaV == 0 ? 0 : demRaw / avgVolaV;
			supSeries[0] = avgVolaV == 0 ? 0 : supRaw / avgVolaV;
			adp[0] = 100.0 * Wma_Series(demSeries, VaderDerAvg);
			asp[0] = 100.0 * Wma_Series(supSeries, VaderDerAvg);
			bool vRising = adp[0] - adp[1] > 0;
			bool vaderUp = adp[0] > asp[0] && vRising;
			bool vaderDn = adp[0] < asp[0] && !vRising;

			double ll1 = Wma_OnRaw(Close[0], "ll1", 7);
			double ll2 = Wma_OnRaw(ll1, "ll2", 7);
			double ll3 = Wma_OnRaw(ll2, "ll3", 7);   // RedK Slow_Smooth WMA, length 21
			bool llUp = ll3 > llPrev, llDn = ll3 < llPrev;
			llPrev = ll3;

			bool vodkaUp = llUp && vaderUp && rsiUp[0] > 0 && Close[0] > Open[0] && volSpike;
			bool vodkaDn = llDn && vaderDn && Close[0] < Open[0] && volSpike;

			// ===============================================================
			// WADDAH ATTAR EXPLOSION
			// ===============================================================
			double macdNow = EMA_Series(Close[0], FastLength, "waeF") - EMA_Series(Close[0], SlowLength, "waeS");
			double macdPrev = EmaPrevCache("waeF") - EmaPrevCache("waeS");
			double t1 = (macdNow - macdPrev) * Sensitivity;
			waeT1[0] = t1;

			// ===============================================================
			// SHARK WAVETREND & STOCH RSI
			// ===============================================================
			double hlc3_0 = (High[0] + Low[0] + Close[0]) / 3.0;
			double esa = EMA_Series(hlc3_0, WtChannelLen, "wtEsa");
			double absDiff = Math.Abs(hlc3_0 - esa);
			double de = EMA_Series(absDiff, WtChannelLen, "wtDe");
			double ci = de == 0 ? 0 : (hlc3_0 - esa) / (0.015 * de);
			wt1[0] = EMA_Series(ci, WtAverageLen, "wtWt1");
			wt2[0] = SMA_Series(wt1, WtMALen);
			bool wtOversold = wt2[0] <= OsLevel;
			bool wtOverbought = wt2[0] >= ObLevel;
			bool wtCross = CurrentBar >= 1 && ((wt1[1] - wt2[1]) * (wt1[0] - wt2[0]) < 0);
			bool buySignal = wtCross && wt2[0] - wt1[0] <= 0 && wtOversold;
			bool sellSignal = wtCross && wt2[0] - wt1[0] >= 0 && wtOverbought;

			// Stochastic RSI on log(close), K averaged with D (as v4: stochK = avg(stKraw, stD))
			logCloseSeries[0] = Math.Log(Close[0] <= 0 ? 1e-8 : Close[0]);
			double rsiForStoch = RSI(logCloseSeries, StochRsiLen, 1)[0];
			double kk = SMA_OnStoch(rsiForStoch, StochLen, StochKSmooth);
			stochKRaw[0] = kk;
			double d1 = SmaOnSeries(stochKRaw, StochDSmooth);
			stochD[0] = d1;
			stochK[0] = (kk + d1) / 2.0;
			bool stXBull = CurrentBar >= 1 && stochKRaw[1] <= stochD[1] && stochKRaw[0] > stochD[0] && stochKRaw[0] < 20;
			bool stXBear = CurrentBar >= 1 && stochKRaw[1] >= stochD[1] && stochKRaw[0] < stochD[0] && stochKRaw[0] > 80;

			// Fractal divergences (5-bar fractal, offset 2): regular + hidden
			bool wtBearDiv = false, wtBullDiv = false, wtBearDivH = false, wtBullDivH = false;
			bool wtBearDiv2 = false, wtBullDiv2 = false;
			bool rsiBearDiv = false, rsiBullDiv = false, rsiBearDivH = false, rsiBullDivH = false;
			bool mfiBearDiv = false, mfiBullDiv = false, mfiBearDivH = false, mfiBullDivH = false;
			bool stBearDiv = false, stBullDiv = false;
			bool deltaBearDiv = false, deltaBullDiv = false;
			bool dmyA, dmyB;
			if (CurrentBar >= 6)
			{
				CheckFractalDiv("wt", wt2, WtDivOBLevel, WtDivOSLevel, true, out wtBearDiv, out wtBullDiv, out wtBearDivH, out wtBullDivH);
				CheckFractalDiv("wtAdd", wt2, WtDivOBLevelAdd, WtDivOSLevelAdd, true, out wtBearDiv2, out wtBullDiv2, out dmyA, out dmyB);
				CheckFractalDiv("rsi", rsi, RsiDivOBLevel, RsiDivOSLevel, true, out rsiBearDiv, out rsiBullDiv, out rsiBearDivH, out rsiBullDivH);
				CheckFractalDiv("mfi", mfiSeries, MfiDivOBLevel, MfiDivOSLevel, true, out mfiBearDiv, out mfiBullDiv, out mfiBearDivH, out mfiBullDivH);
				CheckFractalDiv("stoch", stochK, 0, 0, false, out stBearDiv, out stBullDiv, out dmyA, out dmyB);
				// V2 extra: price fractal vs session cumulative delta (no natural OB/OS levels -> no limits)
				CheckFractalDiv("delta", sessionCumDelta, 0, 0, false, out deltaBearDiv, out deltaBullDiv, out dmyA, out dmyB);
			}
			bool buySignalDiv = wtBullDiv || wtBullDiv2 || stBullDiv || rsiBullDiv || mfiBullDiv;
			bool sellSignalDiv = wtBearDiv || wtBearDiv2 || stBearDiv || rsiBearDiv || mfiBearDiv;
			bool hidBuyDiv = wtBullDivH || rsiBullDivH || mfiBullDivH;
			bool hidSellDiv = wtBearDivH || rsiBearDivH || mfiBearDivH;

			// ===============================================================
			// EXHAUSTION & PATTERNS
			// ===============================================================
			double waeHi5 = double.MinValue, waeLo5 = double.MaxValue;
			for (int k = 1; k <= 5 && k <= CurrentBar; k++)
			{
				if (waeT1[k] > waeHi5) waeHi5 = waeT1[k];
				if (waeT1[k] < waeLo5) waeLo5 = waeT1[k];
			}
			double t1Prev = waeT1[1];
			bool exhTop = rsi[0] >= obx && t1 < t1Prev && waeHi5 >= effWAE && ((High[0] >= wupperBB[0] && Close[0] < wupperBB[0]) || upWickBig);
			bool exhBot = rsi[0] <= osx && t1 > t1Prev && waeLo5 <= -effWAE && ((Low[0] <= wlowerBB[0] && Close[0] > wlowerBB[0]) || downWickBig);

			bool nearOS = rsi[0] <= osx + 5 || rsi[1] <= osxPrev + 5;
			bool nearOB = rsi[0] >= obx - 5 || rsi[1] >= obxPrev - 5;
			bool pinBull = rng > 0 && lowerWick >= 2 * body && lowerWick >= 0.6 * rng && nearOS;
			bool pinBear = rng > 0 && upperWick >= 2 * body && upperWick >= 0.6 * rng && nearOB;
			bool engBull = Close[0] > Open[0] && Close[1] < Open[1] && Close[0] >= Open[1] && Open[0] <= Close[1] && nearOS;
			bool engBear = Close[0] < Open[0] && Close[1] > Open[1] && Close[0] <= Open[1] && Open[0] >= Close[1] && nearOB;
			bool patBull = pinBull || engBull;
			bool patBear = pinBear || engBear;

			// ===============================================================
			// METER STATES & CONFLUENCE
			// ===============================================================
			int sVodka = vodkaUp ? 1 : vodkaDn ? -1 : 0;
			int sTrip = tripDir;
			int sHT = htTrend == 0 ? 1 : -1;
			int sCCI = cciBull ? 1 : cciBear ? -1 : 0;
			int sKST = kstBull ? 1 : -1;
			int sIMD = mHlc3[0] > miMCD[0] ? 1 : -1;
			int sRush = bullUp ? 1 : bullDown ? -1 : 0;
			int sWAE = t1 >= effWAE ? 1 : -t1 >= effWAE ? -1 : 0;
			int sHTF = UseHTF ? htfDir : 0;
			stNow[0] = sVodka; stNow[1] = sTrip; stNow[2] = sHT; stNow[3] = sCCI; stNow[4] = sKST;
			stNow[5] = sIMD; stNow[6] = sRush; stNow[7] = sWAE; stNow[8] = sHTF;

			int bullCount = 0, bearCount = 0;
			for (int i = 0; i < 9; i++) { if (stNow[i] > 0) bullCount++; else if (stNow[i] < 0) bearCount++; }
			int confTotal = UseHTF ? 9 : 8;
			int confScore = bullCount - bearCount;
			lastConfScore = confScore;
			bool confLong = bullCount >= ConfN, confShort = bearCount >= ConfN;

			bool frVod = havePrevMeter && stNow[0] != 0 && stNow[0] != prevMeter[0];
			bool frTrip = havePrevMeter && stNow[1] != 0 && stNow[1] != prevMeter[1];
			bool frHT = havePrevMeter && stNow[2] != 0 && stNow[2] != prevMeter[2];
			bool frCCI = havePrevMeter && stNow[3] != 0 && stNow[3] != prevMeter[3];
			bool frKST = havePrevMeter && stNow[4] != 0 && stNow[4] != prevMeter[4];
			bool frIMD = havePrevMeter && stNow[5] != 0 && stNow[5] != prevMeter[5];
			bool frRush = havePrevMeter && stNow[6] != 0 && stNow[6] != prevMeter[6];
			bool frHTF = havePrevMeter && stNow[8] != 0 && stNow[8] != prevMeter[8];
			bool tripFlip = havePrevMeter && sTrip != prevMeter[1];

			// ===============================================================
			// FILTERS -> FINAL SIGNALS
			// ===============================================================
			bool htfOkBull = !UseHTF || htfDir != -1, htfOkBear = !UseHTF || htfDir != 1;
			bool refOkBull = !UseRef || refDir != -1, refOkBear = !UseRef || refDir != 1;
			bool bullGate = inSession && htfOkBull && refOkBull;
			bool bearGate = inSession && htfOkBear && refOkBear;
			bool revOk = !Regime || !trending;
			bool trdOk = !Regime || trending;

			bool sigTrampUp = Cool("tu", upThrust && bullGate && revOk && volOk);
			bool sigTrampDn = Cool("td", downThrust && bearGate && revOk && volOk);
			bool sigSqzBuy = Cool("sb", sqzBuy && bullGate && revOk && volOk);
			bool sigSqzSell = Cool("ss", sqzSell && bearGate && revOk && volOk);
			bool sigBuyBG = Cool("bb", buySignal && bullGate && revOk && (!Strict || confLong));
			bool sigSellBG = Cool("sg", sellSignal && bearGate && revOk && (!Strict || confShort));
			bool sigDivBull = Cool("db", buySignalDiv && bullGate && revOk);
			bool sigDivBear = Cool("ds", sellSignalDiv && bearGate && revOk);
			bool sigHDivBull = Cool("hdb", ShowHidden && hidBuyDiv && bullGate);
			bool sigHDivBear = Cool("hds", ShowHidden && hidSellDiv && bearGate);
			bool sigRsiDivBull = Cool("rb", bullCond && bullGate && revOk);
			bool sigRsiDivBear = Cool("rs", bearCond && bearGate && revOk);
			bool sigRsiHBull = Cool("rhb", ShowHidden && hBullCond && bullGate);
			bool sigRsiHBear = Cool("rhs", ShowHidden && hBearCond && bearGate);
			bool sigExhBot = Cool("eb", exhBot && bullGate && revOk);
			bool sigExhTop = Cool("et", exhTop && bearGate && revOk);
			bool sigPatBull = Cool("pb", patBull && bullGate && revOk);
			bool sigPatBear = Cool("ps", patBear && bearGate && revOk);
			bool sigStBull = Cool("tb", stXBull && bullGate);
			bool sigStBear = Cool("ts", stXBear && bearGate);
			bool sigConfLong = confLong && !prevConfLong && trdOk && bullGate;
			bool sigConfShort = confShort && !prevConfShort && trdOk && bearGate;
			bool sigFlipUp = tripFlip && tripBuy;
			bool sigFlipDn = tripFlip && tripSell;
			bool sigDeltaBull = Cool("xb", deltaBullDiv && bullGate);   // V2 extra, session/HTF/ref gated
			bool sigDeltaBear = Cool("xs", deltaBearDiv && bearGate);

			prevConfLong = confLong; prevConfShort = confShort;

			bool anyLong = sigTrampUp || sigSqzBuy || sigBuyBG || sigDivBull || sigRsiDivBull || sigConfLong || sigFlipUp || sigPatBull || sigExhBot || sigStBull;
			bool anyShort = sigTrampDn || sigSqzSell || sigSellBG || sigDivBear || sigRsiDivBear || sigConfShort || sigFlipDn || sigPatBear || sigExhTop || sigStBear;
			bool bAllActive = (vodkaUp || vodkaDn) && kstBull && (bullDown || bullUp) && (tripBuy || tripSell) && (cciBull || cciBear);

			// ===============================================================
			// ALERTS
			// ===============================================================
			if (EnableSoundAlerts)
			{
				if (AlTramp) { if (sigTrampUp) Fire(TrampolineSound, "trampoline", "long"); if (sigTrampDn) Fire(TrampolineSound, "trampoline", "short"); }
				if (AlSqz) { if (sigSqzBuy) Fire(SqueezeSound, "squeeze", "long"); if (sigSqzSell) Fire(SqueezeSound, "squeeze", "short"); }
				if (AlBG) { if (sigBuyBG) Fire(WaveTrendSound, "wavetrend", "long"); if (sigSellBG) Fire(WaveTrendSound, "wavetrend", "short"); }
				if (AlDiv)
				{
					if (sigDivBull) Fire(BackgroundDivergenceSound, "divergence", "long");
					if (sigDivBear) Fire(BackgroundDivergenceSound, "divergence", "short");
					if (sigRsiDivBull) Fire(DivergenceSound, "rsi_divergence", "long");
					if (sigRsiDivBear) Fire(DivergenceSound, "rsi_divergence", "short");
				}
				if (AlConf) { if (sigConfLong) Fire(ConfluenceSound, "confluence", "long"); if (sigConfShort) Fire(ConfluenceSound, "confluence", "short"); }
				if (AlFlip) { if (sigFlipUp) Fire(FlipSound, "supertrend_flip", "long"); if (sigFlipDn) Fire(FlipSound, "supertrend_flip", "short"); }
				if (AlExh) { if (sigExhBot) Fire(ExhaustionSound, "exhaustion", "long"); if (sigExhTop) Fire(ExhaustionSound, "exhaustion", "short"); }
				if (AlPat) { if (sigPatBull) Fire(PatternSound, "pattern", "long"); if (sigPatBear) Fire(PatternSound, "pattern", "short"); }
				if (pvsraAlert) Fire(VectorCandleSound, "vector_candle", Close[0] >= Open[0] ? "long" : "short");
				if (bAllActive) Fire(AllTrendMetersSound, "all_meters_active", "any");
				if (sigDeltaBull) Fire(DeltaDivergenceSound, "delta_divergence", "long");
				if (sigDeltaBear) Fire(DeltaDivergenceSound, "delta_divergence", "short");
			}

			// ===============================================================
			// PLOTS
			// ===============================================================
			double adxN = Math.Max(Math.Min((adxValue - AdxFadeLo) / Math.Max(AdxFadeHi - AdxFadeLo, 1), 1), 0);
			int meterTransp = AdxWeight ? (int)Math.Round(70 * (1 - adxN)) : 0;

			// RSI line color
			Color rsiCol;
			Brush rsiBrush;
			if (RsiStyle == SneakyInsaneToOsc_v1_0_0RsiStyle.Gradient)
			{
				rsiCol = rsi[0] < 50 ? Lerp(rsi[0], osx, 50, cBear, cNeutral) : Lerp(rsi[0], 50, obx, cNeutral, cBull);
				rsiBrush = GetBrush(rsiCol, 0);
			}
			else
			{
				bool mild = false; double strength;
				if (rsi[0] > obx) { rsiCol = cBull; strength = (100 - obx) > 0 ? (rsi[0] - obx) / (100 - obx) : 1; }
				else if (rsi[0] < osx) { rsiCol = cBear; strength = osx > 0 ? (osx - rsi[0]) / osx : 1; }
				else if (mfiSeries[0] > 50) { rsiCol = cBull; mild = true; strength = Math.Abs(mfiSeries[0] - 50) / 50.0; }
				else if (mfiSeries[0] < 50) { rsiCol = cBear; mild = true; strength = Math.Abs(mfiSeries[0] - 50) / 50.0; }
				else { rsiCol = Color.FromRgb(128, 128, 128); strength = 0.5; }
				strength = Math.Max(0, Math.Min(1, strength));
				rsiBrush = Tinted(rsiCol, (mild ? MildOpacity / 100.0 : 1.0) * (0.55 + 0.45 * strength));
			}

			Values[P_RSI][0] = ShowRSI ? rsi[0] : double.NaN;
			PlotBrushes[P_RSI][0] = rsiBrush;
			Values[P_GLOW][0] = ShowRSI && ShowGlow ? rsi[0] : double.NaN;
			PlotBrushes[P_GLOW][0] = GetBrush(rsiCol, 82);

			Values[P_MFI][0] = ShowMFI ? mfiSeries[0] : double.NaN;
			PlotBrushes[P_MFI][0] = GetBrush(cAccent, 40);

			Values[P_MID][0] = (ShowRSI || ShowBands) ? 50 : double.NaN;
			PlotBrushes[P_MID][0] = ShowBands ? GetBrush(cNeutral, 70) : colorInvisible;
			Values[P_OB][0] = ShowBands ? obx : double.NaN;
			Values[P_OS][0] = ShowBands ? osx : double.NaN;
			PlotBrushes[P_OB][0] = GetBrush(cNeutral, 45);
			PlotBrushes[P_OS][0] = GetBrush(cNeutral, 45);

			// Trend meters: top ribbon inner->outer: CCI, Half Trend, Triple Supertrend, Vodka, KST, HTF; bottom: Impulse MACD, Bull Rush, Confluence
			bool tm = ShowTrend;
			Values[P_CCI][0] = tm ? TopY(0) : double.NaN;   PlotBrushes[P_CCI][0] = MeterBrush(sCCI, frCCI, meterTransp);
			Values[P_HT][0] = tm ? TopY(1) : double.NaN;    PlotBrushes[P_HT][0] = MeterBrush(sHT, frHT, meterTransp);
			Values[P_TRIP][0] = tm ? TopY(2) : double.NaN;  PlotBrushes[P_TRIP][0] = MeterBrush(sTrip, frTrip, meterTransp);
			Values[P_VODKA][0] = tm ? TopY(3) : double.NaN; PlotBrushes[P_VODKA][0] = MeterBrush(sVodka, frVod, meterTransp);
			Values[P_KST][0] = tm ? TopY(4) : double.NaN;   PlotBrushes[P_KST][0] = MeterBrush(sKST, frKST, meterTransp);
			Values[P_HTF][0] = tm && UseHTF ? TopY(5) : double.NaN; PlotBrushes[P_HTF][0] = MeterBrush(sHTF, frHTF, meterTransp);
			int imdTransp = (imdStrong || frIMD) ? 0 : Math.Max(meterTransp, 40);
			Values[P_IMD][0] = tm ? BotY(0) : double.NaN;   PlotBrushes[P_IMD][0] = GetBrush(sIMD > 0 ? cBull : cBear, imdTransp);
			Values[P_RUSH][0] = tm ? BotY(1) : double.NaN;  PlotBrushes[P_RUSH][0] = MeterBrush(sRush, frRush, meterTransp);

			Color confCol = confScore > 0 ? Lerp(confScore, 0, confTotal, cNeutral, cBull) : Lerp(confScore, -confTotal, 0, cBear, cNeutral);
			Values[P_CONF][0] = tm && ShowConf ? BotY(2.5) : double.NaN;
			PlotBrushes[P_CONF][0] = GetBrush(confCol, 0);

			// One flip-dot plot for all binary meters (priority: Supertrend, Half Trend, KST, HTF, Impulse MACD)
			double flipY = double.NaN; int flipS = sIMD;
			if (frTrip) { flipY = TopY(2); flipS = sTrip; }
			else if (frHT) { flipY = TopY(1); flipS = sHT; }
			else if (frKST) { flipY = TopY(4); flipS = sKST; }
			else if (UseHTF && frHTF) { flipY = TopY(5); flipS = sHTF; }
			else if (frIMD) { flipY = BotY(0); flipS = sIMD; }
			Values[P_FLIP][0] = tm && FlipDots ? flipY : double.NaN;
			PlotBrushes[P_FLIP][0] = flipS > 0 ? brBull : brBear;

			Values[P_WT][0] = ShowWT ? 50 + wt1[0] / 2.0 : double.NaN;
			PlotBrushes[P_WT][0] = GetBrush(wt1[0] > wt2[0] ? cBull : cBear, 25);

			// Waddah line: bull/bear on trigger (with V2 magnitude vibrancy), accent tint during a squeeze
			Brush waeBrush;
			if (t1 >= effWAE || -t1 >= effWAE)
			{
				double waeMag = Math.Abs(t1);
				double waeStrength = effWAE > 0 ? Math.Min(1, (waeMag - effWAE) / (2.0 * effWAE)) : 1;
				waeBrush = Tinted(t1 >= effWAE ? cBull : cBear, 0.55 + 0.45 * waeStrength);
			}
			else if (SqzOnWAE && sqzOn) waeBrush = GetBrush(cAccent, 35);
			else waeBrush = GetBrush(cNeutral, 75);
			Values[P_WAE][0] = ShowWAE ? 50 : double.NaN;
			PlotBrushes[P_WAE][0] = waeBrush;

			// hidden alert-condition flags (select in Tools > Alerts via "Crosses Above" 0.5)
			Values[P_FLAG_RSIDIV][0] = (sigRsiDivBull || sigRsiDivBear) ? 1 : 0;
			Values[P_FLAG_ALLMETERS][0] = bAllActive ? 1 : 0;
			Values[P_FLAG_BGDIV][0] = (sigDivBull || sigDivBear) ? 1 : 0;
			Values[P_FLAG_BUYSELL][0] = (sigBuyBG || sigSellBG) ? 1 : 0;
			Values[P_FLAG_DELTADIV][0] = (sigDeltaBull || sigDeltaBear) ? 1 : 0;
			Values[P_ANYLONG][0] = anyLong ? 1 : 0;
			Values[P_ANYSHORT][0] = anyShort ? 1 : 0;

			// Signal bridge: long bits 0-9, short bits 10-19 (short code x1024)
			int longCode = Bit(sigTrampUp, 1) + Bit(sigSqzBuy, 2) + Bit(sigBuyBG, 4) + Bit(sigDivBull, 8) + Bit(sigRsiDivBull, 16) + Bit(sigConfLong, 32) + Bit(sigFlipUp, 64) + Bit(sigPatBull, 128) + Bit(sigExhBot, 256) + Bit(sigStBull, 512);
			int shortCode = Bit(sigTrampDn, 1) + Bit(sigSqzSell, 2) + Bit(sigSellBG, 4) + Bit(sigDivBear, 8) + Bit(sigRsiDivBear, 16) + Bit(sigConfShort, 32) + Bit(sigFlipDn, 64) + Bit(sigPatBear, 128) + Bit(sigExhTop, 256) + Bit(sigStBear, 512);
			Values[P_CODE][0] = longCode + shortCode * 1024;

			// remember meter states for next bar's fresh-flip detection
			for (int i = 0; i < 9; i++) prevMeter[i] = stNow[i];
			havePrevMeter = true;

			// ===============================================================
			// DRAW OBJECTS: shapes / chars / labels
			// ===============================================================
			string tag = "IO_" + CurrentBar;

			// OB/OS squares (accent = exhaustion)
			if (rsi[0] > obx || rsi[0] < osx)
			{
				Brush sq;
				if (rsi[0] > obx) sq = (ShowExh && sigExhTop) ? brAccent : (upWickBig && rsi[0] > RsiUpperW) ? brBull : brBullMild;
				else sq = (ShowExh && sigExhBot) ? brAccent : (downWickBig && rsi[0] < RsiLowerW) ? brBear : brBearMild;
				Draw.Square(this, tag + "_sq", false, 0, rsi[0] > obx ? obx : osx, sq);
			}

			if (ShowTramp && sigTrampUp)
				Draw.Text(this, tag + "_Tu", false, "T", 0, 25, 0, brAccent, markerFontBig, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			if (ShowTramp && sigTrampDn)
				Draw.Text(this, tag + "_Td", false, "T", 0, 75, 0, brAccent, markerFontBig, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);

			if (ShowSqueeze && sigSqzBuy) Draw.Diamond(this, tag + "_sqB", false, 0, 25, brAccent);
			if (ShowSqueeze && sigSqzSell) Draw.Diamond(this, tag + "_sqS", false, 0, 75, brAccent);

			if (ShowBB)
			{
				if (Low[0] <= wlowerBB[0] && Close[0] >= wlowerBB[0] && Close[0] < Open[0])
					Draw.Text(this, tag + "_wbL", false, "\u00DF", 0, 35, 0, brBear, markerFontBig, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
				if (High[0] >= wupperBB[0] && Close[0] < wupperBB[0] && Close[0] > Open[0])
					Draw.Text(this, tag + "_wbH", false, "\u00DF", 0, 65, 0, brBull, markerFontBig, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			}

			double arrowTriY = 50;
			// Vector candle arrow (climax candles only) + Delta Divergence arrow (V2 extras; delta wins if both)
			bool pvsraShowsUp = ShowVector && pvsraBig && Close[0] > Open[0];
			bool pvsraShowsDown = ShowVector && pvsraBig && Close[0] < Open[0];
			bool deltaShowsUp = ShowDeltaDivergence && sigDeltaBull;
			bool deltaShowsDown = ShowDeltaDivergence && sigDeltaBear;
			if (deltaShowsUp)
				Draw.Text(this, tag + "_deltaBull", false, "\u25B2", 0, arrowTriY, 0, Brushes.Orange, markerFontArrow, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			else if (pvsraShowsUp)
				Draw.Text(this, tag + "_vecU", false, "\u25B2", 0, arrowTriY, 0, ScaleOpacity(brBullMild, pvsraVibrancyStrength), markerFontArrow, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			if (deltaShowsDown)
				Draw.Text(this, tag + "_deltaBear", false, "\u25BC", 0, arrowTriY, 0, Brushes.Orange, markerFontArrow, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			else if (pvsraShowsDown)
				Draw.Text(this, tag + "_vecD", false, "\u25BC", 0, arrowTriY, 0, ScaleOpacity(brBearMild, pvsraVibrancyStrength), markerFontArrow, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);

			// Reversal flags share one lane: inner (40/60) = pin bar / engulfing, outer (near the ribbon) = Stoch RSI cross
			bool patOn = ShowPatterns && (sigPatBull || sigPatBear);
			bool stOn = ShowStochX && (sigStBull || sigStBear);
			if (patOn || stOn)
			{
				bool revBull = patOn ? sigPatBull : sigStBull;
				double revY = revBull ? 50 - ArrowGap : 50 + ArrowGap;
				if (revBull) Draw.ArrowUp(this, tag + "_rev", false, 0, revY, brBull);
				else Draw.ArrowDown(this, tag + "_rev", false, 0, revY, brBear);
			}

			// RSI divergence labels: collected here, drawn in OnRender as pointer boxes (Pine label_up / label_down look).
			// Bull hangs below the pivot, Bear sits above it. Fill = mild bull/bear color; text = black/white for contrast.
			if (ShowDivLabels)
			{
				int pivBar = Math.Max(0, CurrentBar - lookbackRight);
				double pivY = CurrentBar >= lookbackRight ? rsi[lookbackRight] : rsi[0];
				int mildT = 100 - MildOpacity;
				if (sigRsiDivBull) AddDivMarker(pivBar, pivY, true, "Bull", ColorT(cBull, mildT), TextOnC(cBull));
				if (sigRsiDivBear) AddDivMarker(pivBar, pivY, false, "Bear", ColorT(cBear, mildT), TextOnC(cBear));
				if (sigRsiHBull) AddDivMarker(pivBar, pivY, true, "H Bull", ColorT(cBull, 70), Color.FromRgb(245, 245, 245));
				if (sigRsiHBear) AddDivMarker(pivBar, pivY, false, "H Bear", ColorT(cBear, 70), Color.FromRgb(245, 245, 245));
			}

			// RSI Ribbon (V2 extra, opt-in): soft fill between the RSI line and the 50 midline
			if (ShowRSIRibbon && ShowRSI && CurrentBar >= 1)
				Draw.Region(this, "IO_ribbon_" + CurrentBar, 1, 0, Values[P_RSI], Values[P_MID], Brushes.Transparent, GetBrush(rsiCol, RibbonTransp), 100);

			// Backgrounds (priority: divergence > hidden divergence > buy/sell) and candle coloring
			Brush bg = null;
			if (ShowDivies && sigDivBull) bg = GetBrush(cBull, Math.Max(BgTrans - 10, 0));
			else if (ShowDivies && sigDivBear) bg = GetBrush(cBear, Math.Max(BgTrans - 10, 0));
			else if (ShowDivies && sigHDivBull) bg = GetBrush(cBull, Math.Min(BgTrans + 7, 98));
			else if (ShowDivies && sigHDivBear) bg = GetBrush(cBear, Math.Min(BgTrans + 7, 98));
			else if (ShowBuySellies && sigBuyBG) bg = GetBrush(cBull, BgTrans);
			else if (ShowBuySellies && sigSellBG) bg = GetBrush(cBear, BgTrans);
			if (bg != null) BackBrushes[0] = bg;

			if (BarColorConf && Math.Abs(confScore) >= 2)
			{
				// Pine: gradient from 60% transparent (score 2) to opaque (score = total)
				double tb = Math.Max(0, Math.Min(1, (Math.Abs(confScore) - 2.0) / Math.Max(confTotal - 2.0, 1.0)));
				BarBrushes[0] = GetBrush(confScore > 0 ? cBull : cBear, (int)Math.Round(60.0 * (1.0 - tb)));
			}
		}

		#region Helper methods (ta.* equivalents with no direct NT built-in)

		// ---------------------------------------------------------------
		// Divergence label rendering (SharpDX), modelled on NewNebula's Box marker
		// ---------------------------------------------------------------
		private static Color ColorT(Color c, int transp)
		{
			transp = Math.Max(0, Math.Min(100, transp));
			return Color.FromArgb((byte)Math.Round(255.0 * (100 - transp) / 100.0), c.R, c.G, c.B);
		}
		private static Color TextOnC(Color c) { return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 150 ? Color.FromRgb(0, 0, 0) : Color.FromRgb(255, 255, 255); }
		private static uint Argb(Color c) { return ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B; }

		private void AddDivMarker(int bar, double rsiVal, bool below, string text, Color fill, Color textCol)
		{
			lock (sync) divMarkers.Add(new DivMarker { Bar = bar, Rsi = rsiVal, Below = below, Text = text, Fill = fill, TextCol = textCol });
		}

		public override void OnRenderTargetChanged()
		{
			foreach (var b in dxBrushes.Values) if (b != null && !b.IsDisposed) b.Dispose();
			dxBrushes.Clear();
		}

		private D2D.SolidColorBrush Dx(Color c)
		{
			uint k = Argb(c);
			D2D.SolidColorBrush b;
			if (!dxBrushes.TryGetValue(k, out b) || b == null || b.IsDisposed)
			{
				b = new D2D.SolidColorBrush(RenderTarget, new DX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f));
				dxBrushes[k] = b;
			}
			return b;
		}

		private DW.TextFormat Fmt(string family, float size, bool bold)
		{
			string key = family + "|" + size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + bold;
			DW.TextFormat f;
			if (!fmts.TryGetValue(key, out f))
			{
				f = new DW.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, family, bold ? DW.FontWeight.Bold : DW.FontWeight.Normal, DW.FontStyle.Normal, size);
				f.WordWrapping = DW.WordWrapping.NoWrap;
				fmts[key] = f;
			}
			return f;
		}

		private DX.Vector2 Measure(string s, DW.TextFormat f)
		{
			using (var tl = new DW.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, s ?? "", f, 4000, 4000))
				return new DX.Vector2(tl.Metrics.WidthIncludingTrailingWhitespace, tl.Metrics.Height);
		}

		private void DrawTextAt(string s, DW.TextFormat f, float x, float y, Color c)
		{
			using (var tl = new DW.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, s ?? "", f, 4000, 4000))
				RenderTarget.DrawTextLayout(new DX.Vector2(x, y), tl, Dx(c), D2D.DrawTextOptions.NoSnap);
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (ChartBars == null || RenderTarget == null || IsInHitTest) return;
			base.OnRender(chartControl, chartScale);
			if (!ShowDivLabels) return;

			lock (sync)
			{
				if (divMarkers.Count == 0) return;
				var oldAA = RenderTarget.AntialiasMode;
				RenderTarget.AntialiasMode = D2D.AntialiasMode.PerPrimitive;
				try
				{
					int from = ChartBars.FromIndex, to = ChartBars.ToIndex;
					foreach (var m in divMarkers)
					{
						if (m.Bar < from || m.Bar > to) continue;
						float x = chartControl.GetXByBarIndex(ChartBars, m.Bar);
						float y = chartScale.GetYByValue(m.Rsi);
						DrawDivBox(m, x, y);
					}
				}
				finally { RenderTarget.AntialiasMode = oldAA; }
			}
		}

		// Pointer box: tip on the pivot, box centered on the bar's x using the measured text width (so it is exactly centered)
		private void DrawDivBox(DivMarker m, float x, float y)
		{
			var f = Fmt("Segoe UI", DivLabelSize, true);
			var sz = Measure(m.Text, f);
			float pad = 4, pt = 4, w = sz.X + pad * 2, hb = sz.Y + 2;
			float tipY = m.Below ? y + DivLabelGap : y - DivLabelGap;
			float top = m.Below ? tipY + pt : tipY - pt - hb;
			float edgeY = m.Below ? top : top + hb;
			float farY = m.Below ? top + hb : top;
			using (var g = new D2D.PathGeometry(NinjaTrader.Core.Globals.D2DFactory))
			{
				using (var sink = g.Open())
				{
					sink.BeginFigure(new DX.Vector2(x, tipY), D2D.FigureBegin.Filled);
					sink.AddLine(new DX.Vector2(x + pt, edgeY));
					sink.AddLine(new DX.Vector2(x + w / 2, edgeY));
					sink.AddLine(new DX.Vector2(x + w / 2, farY));
					sink.AddLine(new DX.Vector2(x - w / 2, farY));
					sink.AddLine(new DX.Vector2(x - w / 2, edgeY));
					sink.AddLine(new DX.Vector2(x - pt, edgeY));
					sink.EndFigure(D2D.FigureEnd.Closed);
					sink.Close();
				}
				RenderTarget.FillGeometry(g, Dx(m.Fill));
			}
			DrawTextAt(m.Text, f, x - sz.X / 2, top + (hb - sz.Y) / 2, m.TextCol);
		}

		// Pine f_textOn: black on light colors, white on dark ones
		private static Brush TextOn(Color c)
		{
			return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 150 ? Brushes.Black : Brushes.White;
		}

		private static int Bit(bool c, int b) { return c ? b : 0; }

		private double TopY(double i) { return MeterPos == SneakyInsaneToOsc_v1_0_0MeterPos.Classic ? 68.0 + i : StripIn + MeterGap * i; }
		private double BotY(double i) { return MeterPos == SneakyInsaneToOsc_v1_0_0MeterPos.Classic ? 31.0 - i : (100.0 - StripIn) - MeterGap * i; }

		// Pine f_mc: meter color, full strength on the bar it flips, ADX-faded otherwise
		private Brush MeterBrush(int s, bool fresh, int transp)
		{
			if (s > 0) return GetBrush(cBull, fresh ? 0 : transp);
			if (s < 0) return GetBrush(cBear, fresh ? 0 : transp);
			return colorInvisible;
		}

		// Pine f_cool: passes a signal only if the same key hasn't passed one within Cooldown bars
		private bool Cool(string key, bool sig)
		{
			int lastBar;
			bool ok = sig && (Cooldown <= 0 || !lastSigBar.TryGetValue(key, out lastBar) || CurrentBar - lastBar > Cooldown);
			if (ok) lastSigBar[key] = CurrentBar;
			return ok;
		}

		// Session window on bar-close time, (start, end]; wraps past midnight if start > end
		private bool InSessionNow()
		{
			if (!UseSession) return true;
			DateTime t = Time[0];
			if (sessTz != null)
			{
				try { t = TimeZoneInfo.ConvertTime(t, TimeZoneInfo.Local, sessTz); }
				catch { }
			}
			int hhmm = t.Hour * 100 + t.Minute;
			if (SessStart > SessEnd) return hhmm > SessStart || hhmm <= SessEnd;
			return hhmm > SessStart && hhmm <= SessEnd;
		}

		// Wilder smoothing for the hand-rolled DMI; processing starts at bar 2, so the first len bars use a running mean as the seed
		private double RmaStep(Series<double> self, double src, int len)
		{
			int n = CurrentBar - 1;
			if (n <= 1) return src;
			if (n <= len) return (self[1] * (n - 1) + src) / n;
			return (self[1] * (len - 1) + src) / len;
		}

		private void ComputeObos(out double obx, out double osx)
		{
			double o, s;
			int n = Math.Max(1, Math.Min(AdaptLen, CurrentBar - 1));
			// Pine: ta.sma / ta.stdev / percentile are na until a full window exists, and nz(OBx, rsiOB) then falls back to the fixed level
			bool windowFull = CurrentBar - 1 >= AdaptLen;
			if (ObosMode != SneakyInsaneToOsc_v1_0_0ObosMode.Fixed && !windowFull) { o = RsiOB; s = RsiOS; }
			else if (ObosMode == SneakyInsaneToOsc_v1_0_0ObosMode.AdaptiveRsiBollinger)
			{
				double sum = 0; for (int i = 0; i < n; i++) sum += rsi[i];
				double mean = sum / n;
				double sq = 0; for (int i = 0; i < n; i++) { double d = rsi[i] - mean; sq += d * d; }
				double sd = Math.Sqrt(sq / n);
				o = mean + AdaptMult * sd; s = mean - AdaptMult * sd;
			}
			else if (ObosMode == SneakyInsaneToOsc_v1_0_0ObosMode.AdaptivePercentile)
			{
				for (int i = 0; i < n; i++) pctScratch[i] = rsi[i];
				Array.Sort(pctScratch, 0, n);
				int hiIdx = Math.Max(0, Math.Min(n - 1, (int)Math.Ceiling(AdaptPct / 100.0 * n) - 1));
				int loIdx = Math.Max(0, Math.Min(n - 1, (int)Math.Ceiling((100 - AdaptPct) / 100.0 * n) - 1));
				o = pctScratch[hiIdx]; s = pctScratch[loIdx];
			}
			else { o = RsiOB; s = RsiOS; }
			obx = Math.Max(Math.Min(double.IsNaN(o) ? RsiOB : o, 95), 55);
			osx = Math.Min(Math.Max(double.IsNaN(s) ? RsiOS : s, 5), 45);
		}

		// Pine f_derma: WMA / EMA / SMA of a raw per-bar value (call exactly once per bar per key)
		private double DerMa(double value, string key, int len)
		{
			switch (DerMaType)
			{
				case SneakyInsaneToOsc_v1_0_0MaType.SMA:
					{
						var b = GetBuf(key, len);
						b.Push(value);
						return b.Sma();
					}
				case SneakyInsaneToOsc_v1_0_0MaType.EMA:
					return EMA_Series(value, len, key);
				default:
					return Wma_OnRaw(value, key, len);
			}
		}

		// Half Trend on the meter source (Pine v4 block, state kept in fields)
		private void UpdateHalfTrend()
		{
			int amp = Amplitude;
			int hb = 0; double hv = double.MinValue;
			for (int i = 0; i < amp && i <= CurrentBar; i++) if (mH[i] > hv) { hv = mH[i]; hb = i; }
			int lb = 0; double lv = double.MaxValue;
			for (int i = 0; i < amp && i <= CurrentBar; i++) if (mL[i] < lv) { lv = mL[i]; lb = i; }
			double highPrice = mH[hb], lowPrice = mL[lb];
			double highMa = SMA_Series(mH, amp), lowMa = SMA_Series(mL, amp);
			double mLprev = CurrentBar >= 1 ? mL[1] : mL[0];
			double mHprev = CurrentBar >= 1 ? mH[1] : mH[0];
			if (!htInit) { htMaxLow = mLprev; htMinHigh = mHprev; htInit = true; }

			if (htNext == 1)
			{
				htMaxLow = Math.Max(lowPrice, htMaxLow);
				if (highMa < htMaxLow && mC[0] < mLprev) { htTrend = 1; htNext = 0; htMinHigh = highPrice; }
			}
			else
			{
				htMinHigh = Math.Min(highPrice, htMinHigh);
				if (lowMa > htMinHigh && mC[0] > mHprev) { htTrend = 0; htNext = 1; htMaxLow = lowPrice; }
			}
		}

		private double HLC3Val(int barsAgo) { return (High[barsAgo] + Low[barsAgo] + Close[barsAgo]) / 3.0; }

		// This bar's raw order-flow delta (buy volume - sell volume), cached once per bar. Uses live
		// tick classification (Lee & Ready quote-rule + tick-rule fallback) when real-time/replay data
		// is flowing, or a bar-geometry estimate otherwise -- same fallback as TONebulaRealFlow.
		private double GetBarDelta()
		{
			if (barDeltaCached) return cachedBarDelta;
			double barBuyVol, barSellVol; bool flowIsLive;
			lock (flowLock)
			{
				flowIsLive = realTimeFlowActive;
				barBuyVol = liveBuyVol; liveBuyVol = 0;
				barSellVol = liveSellVol; liveSellVol = 0;
			}
			double buyingVol, sellingVol;
			if (flowIsLive)
			{
				buyingVol = barBuyVol; sellingVol = barSellVol;
			}
			else
			{
				double o = Open[0], h = High[0], l = Low[0], c = Close[0], v = Volume[0];
				double ofUpW = c > o ? h - c : h - o, ofLoW = c > o ? o - l : c - l, ofSpread = h - l;
				double ofPUW = ofSpread > 0 ? ofUpW / ofSpread : 0, ofPLW = ofSpread > 0 ? ofLoW / ofSpread : 0;
				double ofPBody = ofSpread > 0 ? (ofSpread - (ofUpW + ofLoW)) / ofSpread : 0, ofWickShare = (ofPUW + ofPLW) / 2;
				buyingVol = c > o ? (ofPBody + ofWickShare) * v : ofWickShare * v;
				sellingVol = c < o ? (ofPBody + ofWickShare) * v : ofWickShare * v;
			}
			cachedBarDelta = buyingVol - sellingVol;
			barDeltaCached = true;
			return cachedBarDelta;
		}

		// Real-time (and Tick-Replay-historical) tick classification: Lee & Ready (1991) quote-rule with
		// tick-rule fallback for mid-quote prints. Runs on its own thread, so all shared state here stays
		// behind flowLock, not 'sync'.
		protected override void OnMarketData(NinjaTrader.Data.MarketDataEventArgs e)
		{
			lock (flowLock)
			{
				if (e.MarketDataType == MarketDataType.Bid) { prevBid = e.Price; haveQuote = true; return; }
				if (e.MarketDataType == MarketDataType.Ask) { prevAsk = e.Price; haveQuote = true; return; }
				if (e.MarketDataType != MarketDataType.Last || !haveQuote) return;

				double px = e.Price;
				bool isBuy = px >= prevAsk ? true
					: px <= prevBid ? false
					: !double.IsNaN(prevTradePrice) && px > prevTradePrice ? true
					: !double.IsNaN(prevTradePrice) && px < prevTradePrice ? false
					: lastSideWasBuy;

				if (isBuy) liveBuyVol += e.Volume; else liveSellVol += e.Volume;
				lastSideWasBuy = isBuy;
				prevTradePrice = px;
				realTimeFlowActive = true;
			}
		}

		// Running session cumulative delta, reset on a calendar-day change (feeds Delta Divergence)
		private void UpdateSessionCumDelta()
		{
			if (Time[0].Date != lastDeltaSessionDate)
			{
				sessionDeltaSum = 0;
				lastDeltaSessionDate = Time[0].Date;
			}
			sessionDeltaSum += GetBarDelta();
			sessionCumDelta[0] = sessionDeltaSum;
		}

		private double SMA_Series(ISeries<double> src, int len)
		{
			if (CurrentBar < len - 1) len = CurrentBar + 1;
			double sum = 0; for (int i = 0; i < len; i++) sum += src[i]; return sum / len;
		}
		private double SMA_Series(Func<int, double> src, int len)
		{
			if (CurrentBar < len - 1) len = CurrentBar + 1;
			double sum = 0; for (int i = 0; i < len; i++) sum += src(i); return sum / len;
		}
		private double StdDev_Series(ISeries<double> src, int len)
		{
			if (CurrentBar < len - 1) len = CurrentBar + 1;
			double mean = SMA_Series(src, len);
			double sq = 0; for (int i = 0; i < len; i++) { double d = src[i] - mean; sq += d * d; }
			return Math.Sqrt(sq / len);
		}
		private double MeanDev(Func<int, double> src, int len)
		{
			if (CurrentBar < len - 1) len = CurrentBar + 1;
			double mean = SMA_Series(src, len);
			double sum = 0; for (int i = 0; i < len; i++) sum += Math.Abs(src(i) - mean);
			return sum / len;
		}
		private double SMA_Range(int len)
		{
			if (CurrentBar < len - 1) len = CurrentBar + 1;
			double sum = 0; for (int i = 0; i < len; i++) sum += (High[i] - Low[i]);
			return sum / len;
		}
		private double TrueRange(int barsAgo)
		{
			if (CurrentBar - barsAgo < 1) return High[barsAgo] - Low[barsAgo];
			return Math.Max(High[barsAgo] - Low[barsAgo], Math.Max(Math.Abs(High[barsAgo] - Close[barsAgo + 1]), Math.Abs(Low[barsAgo] - Close[barsAgo + 1])));
		}
		private double SMA_Volume(int len)
		{
			if (CurrentBar < len - 1) len = CurrentBar + 1;
			double sum = 0; for (int i = 0; i < len; i++) sum += Volume[i]; return sum / len;
		}
		private double StdDev_Volume(int len)
		{
			if (CurrentBar < len - 1) len = CurrentBar + 1;
			double mean = SMA_Volume(len);
			double sq = 0; for (int i = 0; i < len; i++) { double d = Volume[i] - mean; sq += d * d; }
			return Math.Sqrt(sq / len);
		}
		private double HighestVal(ISeries<double> src, int len)
		{
			int n = Math.Min(len, CurrentBar + 1);
			double h = double.MinValue; for (int i = 0; i < n; i++) h = Math.Max(h, src[i]); return h;
		}
		private double LowestVal(ISeries<double> src, int len)
		{
			int n = Math.Min(len, CurrentBar + 1);
			double l = double.MaxValue; for (int i = 0; i < n; i++) l = Math.Min(l, src[i]); return l;
		}
		private double Highest2(int len)
		{
			double h = double.MinValue; int n = Math.Min(len, CurrentBar + 1);
			for (int i = 0; i < n; i++) h = Math.Max(h, High[i]); return h;
		}
		private double Lowest2(int len)
		{
			double l = double.MaxValue; int n = Math.Min(len, CurrentBar + 1);
			for (int i = 0; i < n; i++) l = Math.Min(l, Low[i]); return l;
		}
		private double StochOf(double v, int len)
		{
			int n = Math.Min(len, CurrentBar + 1);
			double hh = double.MinValue, ll = double.MaxValue;
			for (int i = 0; i < n; i++) { hh = Math.Max(hh, Volume[i]); ll = Math.Min(ll, Volume[i]); }
			return hh == ll ? 0 : (v - ll) / (hh - ll) * 100.0;
		}

		private Dictionary<string, double> emaCache = new Dictionary<string, double>();
		private Dictionary<string, double> emaPrevCache = new Dictionary<string, double>();
		private double EMA_Series(double value, int len, string key)
		{
			double alpha = 2.0 / (len + 1);
			double prev;
			if (!emaCache.TryGetValue(key, out prev)) prev = value;
			double result = (value - prev) * alpha + prev;
			emaPrevCache[key] = prev;
			emaCache[key] = result;
			return result;
		}
		private double EmaPrevCache(string key) { double v; return emaPrevCache.TryGetValue(key, out v) ? v : 0; }

		// Pine ta.rma seeded with the true SMA on the first full window, then recursive
		private double Smma(ISeries<double> priceSrc, Series<double> self, int len)
		{
			if (CurrentBar < len - 1) return double.NaN;
			if (CurrentBar == len - 1) return SMA_Series(priceSrc, len);
			return (self[1] * (len - 1) + priceSrc[0]) / len;
		}

		private double Wma_Series(Series<double> src, int len)
		{
			if (CurrentBar < len - 1) len = CurrentBar + 1;
			double norm = len * (len + 1) / 2.0, sum = 0;
			for (int i = 0; i < len; i++) sum += src[i] * (len - i);
			return sum / norm;
		}

		// Fixed-capacity circular buffer (zero allocation after warm-up) for rolling WMA/SMA/Highest/Lowest
		private class RollingBuffer
		{
			private readonly double[] buf;
			private int head = 0, count = 0;
			public RollingBuffer(int capacity) { buf = new double[Math.Max(1, capacity)]; }
			public int Count { get { return count; } }
			public void Push(double v)
			{
				buf[head] = v;
				head = (head + 1) % buf.Length;
				if (count < buf.Length) count++;
			}
			public double At(int i) { return buf[(head - count + i + buf.Length * 2) % buf.Length]; }
			public double Wma()
			{
				if (count == 0) return 0;
				double norm = count * (count + 1) / 2.0, sum = 0;
				for (int i = 0; i < count; i++) sum += At(i) * (i + 1);
				return sum / norm;
			}
			public double Sma()
			{
				if (count == 0) return 0;
				double sum = 0; for (int i = 0; i < count; i++) sum += At(i);
				return sum / count;
			}
			public double Highest()
			{
				double h = double.MinValue; for (int i = 0; i < count; i++) h = Math.Max(h, At(i));
				return h;
			}
			public double Lowest()
			{
				double l = double.MaxValue; for (int i = 0; i < count; i++) l = Math.Min(l, At(i));
				return l;
			}
		}

		private Dictionary<string, RollingBuffer> rollingBuffers = new Dictionary<string, RollingBuffer>();
		private RollingBuffer GetBuf(string key, int capacity)
		{
			RollingBuffer rb;
			if (!rollingBuffers.TryGetValue(key, out rb)) { rb = new RollingBuffer(capacity); rollingBuffers[key] = rb; }
			return rb;
		}

		private double Wma_OnRaw(double value, string key, int len)
		{
			var rb = GetBuf(key, len);
			rb.Push(value);
			return rb.Wma();
		}
		private double SmaOnSeries(Series<double> src, int len)
		{
			if (CurrentBar < len - 1) len = CurrentBar + 1;
			double sum = 0; for (int i = 0; i < len; i++) sum += src[i]; return sum / len;
		}
		private double SMA_OnStoch(double value, int stochLen, int kSmooth)
		{
			var q = GetBuf("stoch", stochLen);
			q.Push(value);
			double hh = q.Highest(), ll = q.Lowest();
			double stochVal = hh == ll ? 0 : (value - ll) / (hh - ll) * 100.0;
			var qk = GetBuf("stochK", kSmooth);
			qk.Push(stochVal);
			return qk.Sma();
		}

		// ta.linreg(close - avg2, len, 0) via OLS over the last `len` values
		private double LinRegClose(double y, int len)
		{
			var buf = GetBuf("linregbuf", len);
			double src = Close[0] - y;
			buf.Push(src);
			int n = buf.Count;
			if (n < 2) return src;
			double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
			for (int i = 0; i < n; i++)
			{
				double x = i; double yv = buf.At(i);
				sumX += x; sumY += yv; sumXY += x * yv; sumX2 += x * x;
			}
			double denom = n * sumX2 - sumX * sumX;
			if (denom == 0) return buf.At(n - 1);
			double slope = (n * sumXY - sumX * sumY) / denom;
			double intercept = (sumY - slope * sumX) / n;
			return intercept + slope * (n - 1);
		}

		// KST building block on the meter source: sma(roc(mC, rocLen), smaLen)
		private double SmaRoc(int rocLen, int smaLen)
		{
			if (CurrentBar < rocLen) return 0;
			double roc = mC[rocLen] == 0 ? 0 : (mC[0] - mC[rocLen]) / mC[rocLen] * 100.0;
			var buf = GetBuf("roc_" + rocLen + "_" + smaLen, smaLen);
			buf.Push(roc);
			return buf.Sma();
		}

		// Pine f_findDivs: regular + hidden fractal divergence, independent pivot memory per channel
		private void CheckFractalDiv(string channel, Series<double> src, double topLimit, double botLimit, bool useLimits,
			out bool bearSignal, out bool bullSignal, out bool bearHidden, out bool bullHidden)
		{
			bearSignal = false; bullSignal = false; bearHidden = false; bullHidden = false;
			if (CurrentBar < 6) return;
			bool topFractal = src[4] < src[2] && src[3] < src[2] && src[2] > src[1] && src[2] > src[0];
			bool botFractal = src[4] > src[2] && src[3] > src[2] && src[2] < src[1] && src[2] < src[0];
			bool fractalTop = topFractal && (!useLimits || src[2] >= topLimit);
			bool fractalBot = botFractal && (!useLimits || src[2] <= botLimit);

			if (fractalTop)
			{
				double prevHighPrice;
				if (divLastHighPrice.TryGetValue(channel, out prevHighPrice))
				{
					double prevHighSrc = divLastHighSrc[channel];
					bearSignal = High[2] > prevHighPrice && src[2] < prevHighSrc;
					bearHidden = High[2] < prevHighPrice && src[2] > prevHighSrc;
				}
				divLastHighSrc[channel] = src[2];
				divLastHighPrice[channel] = High[2];
			}
			if (fractalBot)
			{
				double prevLowPrice;
				if (divLastLowPrice.TryGetValue(channel, out prevLowPrice))
				{
					double prevLowSrc = divLastLowSrc[channel];
					bullSignal = Low[2] < prevLowPrice && src[2] > prevLowSrc;
					bullHidden = Low[2] > prevLowPrice && src[2] < prevLowSrc;
				}
				divLastLowSrc[channel] = src[2];
				divLastLowPrice[channel] = Low[2];
			}
		}

		// Per-signal alert: NinjaTrader Alerts-window message (optionally JSON) + optional .wav
		private void Fire(string sound, string sig, string side)
		{
			// Alert() is only meaningful live; without this a fresh load would replay every historical signal as an alert
			if (State != State.Realtime) return;
			string msg;
			if (AlertJSON)
				msg = "{\"ticker\":\"" + Instrument.FullName + "\",\"tf\":\"" + BarsPeriod.Value + " " + BarsPeriod.BarsPeriodType
					+ "\",\"signal\":\"" + sig + "\",\"side\":\"" + side
					+ "\",\"price\":" + Close[0].ToString(System.Globalization.CultureInfo.InvariantCulture)
					+ ",\"rsi\":" + rsi[0].ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
					+ ",\"confluence\":" + lastConfScore + "}";
			else
				msg = "Insane Oscillator | " + Instrument.FullName + " " + BarsPeriod.Value + " " + BarsPeriod.BarsPeriodType + " | " + sig + " " + side + " @ " + Close[0].ToString(System.Globalization.CultureInfo.InvariantCulture);

			string soundPath = "";
			bool wantSound = !string.IsNullOrEmpty(sound) && sound != "None";
			try
			{
				if (wantSound)
				{
					soundPath = Path.Combine(NinjaTrader.Core.Globals.InstallDir, "sounds", sound);
					if (!File.Exists(soundPath))
						soundPath = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "sounds", sound);
					if (!File.Exists(soundPath))
					{
						soundPath = "";
						PlaySound(sound);
					}
				}
				Alert("SneakyInsaneToOsc_v1_0_0" + sig + "_" + side + "_" + CurrentBar, Priority.Medium, msg, soundPath, 0, Brushes.Transparent, Brushes.Transparent);
			}
			catch { }
		}

		// Triple supertrend majority vote (1x, 2x, 3x of a shared 10-bar SMA of true range), one instance per series
		private class TripleSt
		{
			private readonly double[] lo = new double[3];
			private readonly double[] hi = new double[3];
			private readonly int[] dir = { 1, 1, 1 };
			private readonly double[] trBuf = new double[10];
			private int trCount = 0, trHead = 0;
			private bool init = false;
			private double prevClose = double.NaN;
			public int Dir = 1;
			public int BullVotes = 3;

			public int Update(double h, double l, double c)
			{
				double tr = double.IsNaN(prevClose) ? h - l : Math.Max(h - l, Math.Max(Math.Abs(h - prevClose), Math.Abs(l - prevClose)));
				trBuf[trHead] = tr; trHead = (trHead + 1) % 10; if (trCount < 10) trCount++;
				double sum = 0; for (int i = 0; i < trCount; i++) sum += trBuf[i];
				double atr = sum / trCount;
				double mid = (h + l) / 2.0;
				int bull = 0;
				for (int k = 0; k < 3; k++)
				{
					double m = k + 1;
					double nlo = mid - m * atr, nhi = mid + m * atr;
					double loPrev = init ? lo[k] : nlo;
					double hiPrev = init ? hi[k] : nhi;
					double curLo = (!double.IsNaN(prevClose) && prevClose > loPrev) ? Math.Max(nlo, loPrev) : nlo;
					double curHi = (!double.IsNaN(prevClose) && prevClose < hiPrev) ? Math.Min(nhi, hiPrev) : nhi;
					if (dir[k] == -1 && c > hiPrev) dir[k] = 1;
					else if (dir[k] == 1 && c < loPrev) dir[k] = -1;
					lo[k] = curLo; hi[k] = curHi;
					if (dir[k] == 1) bull++;
				}
				init = true; prevClose = c;
				BullVotes = bull;
				Dir = bull >= 2 ? 1 : -1;
				return Dir;
			}
		}

		#endregion

		#region Properties (plot series accessors)

		[Browsable(false)]
		[XmlIgnore()]
		public Series<double> RsiSeries { get { return rsi; } }

		[Browsable(false)]
		[XmlIgnore()]
		public Series<double> MfiValueSeries { get { return mfiSeries; } }

		// Signal bridge for strategies: Values[23] = longBits + shortBits * 1024
		[Browsable(false)]
		[XmlIgnore()]
		public Series<double> SignalCode { get { Update(); return Values[P_CODE]; } }

		#endregion
	}

	#region Sound Converter

	public class SneakyInsaneToOsc_v1_0_0SoundConverter : TypeConverter
	{
		public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
		public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return true; }

		public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
		{
			var soundsPath = Path.Combine(NinjaTrader.Core.Globals.InstallDir, "sounds");
			var soundFiles = new List<string> { "None" };

			if (Directory.Exists(soundsPath))
			{
				soundFiles.AddRange(Directory.GetFiles(soundsPath, "*.wav")
					.Select(f => Path.GetFileName(f))
					.OrderBy(f => f));
			}
			else
			{
				soundsPath = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "sounds");
				if (Directory.Exists(soundsPath))
				{
					soundFiles.AddRange(Directory.GetFiles(soundsPath, "*.wav")
						.Select(f => Path.GetFileName(f))
						.OrderBy(f => f));
				}
			}

			return new StandardValuesCollection(soundFiles);
		}
	}

	#endregion
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private SneakyInsaneToOsc_v1_0_0[] cacheSneakyInsaneToOsc_v1_0_0;
		public SneakyInsaneToOsc_v1_0_0 SneakyInsaneToOsc_v1_0_0(SneakyInsaneToOsc_v1_0_0Theme colorTheme, int mildOpacity, SneakyInsaneToOsc_v1_0_0Preset preset, bool showRSI, SneakyInsaneToOsc_v1_0_0RsiStyle rsiStyle, int rsiWidth, bool showGlow, bool showBands, bool showTramp, bool showSqueeze, bool showBB, bool showVector, bool showExh, bool showPatterns, bool showStochX, bool showWT, bool showWAE, int thickWaddah, bool sqzOnWAE, bool showMFI, bool showDivLabels, int divLabelSize, int divLabelGap, bool showHidden, bool showDivies, bool showBuySellies, int bgTrans, bool barColorConf, bool showDeltaDivergence, bool showRSIRibbon, int ribbonTransp, int arrowGap, bool showTrend, SneakyInsaneToOsc_v1_0_0MeterPos meterPos, double stripIn, double meterGap, int meterWidth, bool showConf, bool flipDots, bool adxWeight, int adxFadeLo, int adxFadeHi, bool meterHA, int confN, bool strict, int cooldown, bool volConfirm, double volConfMult, bool regime, int adxTrend, bool useHTF, int hTFMinutes, bool useRef, string refSymbol, bool useSession, int sessStart, int sessEnd, string sessTZ, SneakyInsaneToOsc_v1_0_0ObosMode obosMode, int adaptLen, double adaptMult, int adaptPct, int rsiLength, int rsiOS, int rsiOB, int rsiLowerW, int rsiUpperW, int mfiLength, double iBBThreshold, int rSIThreshold, int rSIDown, int lengthBB, double multBB, int sqTolerance, int adxSqueeze, int adxLen, int diLen, int wLengthBB, double wMultBB, int amplitude, int vaderRLength, int vaderDerAvg, SneakyInsaneToOsc_v1_0_0MaType derMaType, SneakyInsaneToOsc_v1_0_0VolCalc vCalc, int vaderVLookback, bool vodkaLegacySpike, int threshold, int sensitivity, int fastLength, int slowLength, int wtChannelLen, int wtAverageLen, int wtMALen, int obLevel, int osLevel, int wtDivOBLevel, int wtDivOSLevel, int wtDivOBLevelAdd, int wtDivOSLevelAdd, int rsiDivOBLevel, int rsiDivOSLevel, int mfiDivOBLevel, int mfiDivOSLevel, int stochLen, int stochRsiLen, int stochKSmooth, int stochDSmooth, bool enableSoundAlerts, bool alertJSON, bool alTramp, bool alSqz, bool alBG, bool alDiv, bool alConf, bool alFlip, bool alExh, bool alPat)
		{
			return SneakyInsaneToOsc_v1_0_0(Input, colorTheme, mildOpacity, preset, showRSI, rsiStyle, rsiWidth, showGlow, showBands, showTramp, showSqueeze, showBB, showVector, showExh, showPatterns, showStochX, showWT, showWAE, thickWaddah, sqzOnWAE, showMFI, showDivLabels, divLabelSize, divLabelGap, showHidden, showDivies, showBuySellies, bgTrans, barColorConf, showDeltaDivergence, showRSIRibbon, ribbonTransp, arrowGap, showTrend, meterPos, stripIn, meterGap, meterWidth, showConf, flipDots, adxWeight, adxFadeLo, adxFadeHi, meterHA, confN, strict, cooldown, volConfirm, volConfMult, regime, adxTrend, useHTF, hTFMinutes, useRef, refSymbol, useSession, sessStart, sessEnd, sessTZ, obosMode, adaptLen, adaptMult, adaptPct, rsiLength, rsiOS, rsiOB, rsiLowerW, rsiUpperW, mfiLength, iBBThreshold, rSIThreshold, rSIDown, lengthBB, multBB, sqTolerance, adxSqueeze, adxLen, diLen, wLengthBB, wMultBB, amplitude, vaderRLength, vaderDerAvg, derMaType, vCalc, vaderVLookback, vodkaLegacySpike, threshold, sensitivity, fastLength, slowLength, wtChannelLen, wtAverageLen, wtMALen, obLevel, osLevel, wtDivOBLevel, wtDivOSLevel, wtDivOBLevelAdd, wtDivOSLevelAdd, rsiDivOBLevel, rsiDivOSLevel, mfiDivOBLevel, mfiDivOSLevel, stochLen, stochRsiLen, stochKSmooth, stochDSmooth, enableSoundAlerts, alertJSON, alTramp, alSqz, alBG, alDiv, alConf, alFlip, alExh, alPat);
		}

		public SneakyInsaneToOsc_v1_0_0 SneakyInsaneToOsc_v1_0_0(ISeries<double> input, SneakyInsaneToOsc_v1_0_0Theme colorTheme, int mildOpacity, SneakyInsaneToOsc_v1_0_0Preset preset, bool showRSI, SneakyInsaneToOsc_v1_0_0RsiStyle rsiStyle, int rsiWidth, bool showGlow, bool showBands, bool showTramp, bool showSqueeze, bool showBB, bool showVector, bool showExh, bool showPatterns, bool showStochX, bool showWT, bool showWAE, int thickWaddah, bool sqzOnWAE, bool showMFI, bool showDivLabels, int divLabelSize, int divLabelGap, bool showHidden, bool showDivies, bool showBuySellies, int bgTrans, bool barColorConf, bool showDeltaDivergence, bool showRSIRibbon, int ribbonTransp, int arrowGap, bool showTrend, SneakyInsaneToOsc_v1_0_0MeterPos meterPos, double stripIn, double meterGap, int meterWidth, bool showConf, bool flipDots, bool adxWeight, int adxFadeLo, int adxFadeHi, bool meterHA, int confN, bool strict, int cooldown, bool volConfirm, double volConfMult, bool regime, int adxTrend, bool useHTF, int hTFMinutes, bool useRef, string refSymbol, bool useSession, int sessStart, int sessEnd, string sessTZ, SneakyInsaneToOsc_v1_0_0ObosMode obosMode, int adaptLen, double adaptMult, int adaptPct, int rsiLength, int rsiOS, int rsiOB, int rsiLowerW, int rsiUpperW, int mfiLength, double iBBThreshold, int rSIThreshold, int rSIDown, int lengthBB, double multBB, int sqTolerance, int adxSqueeze, int adxLen, int diLen, int wLengthBB, double wMultBB, int amplitude, int vaderRLength, int vaderDerAvg, SneakyInsaneToOsc_v1_0_0MaType derMaType, SneakyInsaneToOsc_v1_0_0VolCalc vCalc, int vaderVLookback, bool vodkaLegacySpike, int threshold, int sensitivity, int fastLength, int slowLength, int wtChannelLen, int wtAverageLen, int wtMALen, int obLevel, int osLevel, int wtDivOBLevel, int wtDivOSLevel, int wtDivOBLevelAdd, int wtDivOSLevelAdd, int rsiDivOBLevel, int rsiDivOSLevel, int mfiDivOBLevel, int mfiDivOSLevel, int stochLen, int stochRsiLen, int stochKSmooth, int stochDSmooth, bool enableSoundAlerts, bool alertJSON, bool alTramp, bool alSqz, bool alBG, bool alDiv, bool alConf, bool alFlip, bool alExh, bool alPat)
		{
			if (cacheSneakyInsaneToOsc_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyInsaneToOsc_v1_0_0.Length; idx++)
					if (cacheSneakyInsaneToOsc_v1_0_0[idx] != null && cacheSneakyInsaneToOsc_v1_0_0[idx].ColorTheme == colorTheme && cacheSneakyInsaneToOsc_v1_0_0[idx].MildOpacity == mildOpacity && cacheSneakyInsaneToOsc_v1_0_0[idx].Preset == preset && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowRSI == showRSI && cacheSneakyInsaneToOsc_v1_0_0[idx].RsiStyle == rsiStyle && cacheSneakyInsaneToOsc_v1_0_0[idx].RsiWidth == rsiWidth && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowGlow == showGlow && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowBands == showBands && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowTramp == showTramp && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowSqueeze == showSqueeze && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowBB == showBB && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowVector == showVector && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowExh == showExh && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowPatterns == showPatterns && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowStochX == showStochX && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowWT == showWT && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowWAE == showWAE && cacheSneakyInsaneToOsc_v1_0_0[idx].ThickWaddah == thickWaddah && cacheSneakyInsaneToOsc_v1_0_0[idx].SqzOnWAE == sqzOnWAE && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowMFI == showMFI && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowDivLabels == showDivLabels && cacheSneakyInsaneToOsc_v1_0_0[idx].DivLabelSize == divLabelSize && cacheSneakyInsaneToOsc_v1_0_0[idx].DivLabelGap == divLabelGap && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowHidden == showHidden && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowDivies == showDivies && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowBuySellies == showBuySellies && cacheSneakyInsaneToOsc_v1_0_0[idx].BgTrans == bgTrans && cacheSneakyInsaneToOsc_v1_0_0[idx].BarColorConf == barColorConf && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowDeltaDivergence == showDeltaDivergence && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowRSIRibbon == showRSIRibbon && cacheSneakyInsaneToOsc_v1_0_0[idx].RibbonTransp == ribbonTransp && cacheSneakyInsaneToOsc_v1_0_0[idx].ArrowGap == arrowGap && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowTrend == showTrend && cacheSneakyInsaneToOsc_v1_0_0[idx].MeterPos == meterPos && cacheSneakyInsaneToOsc_v1_0_0[idx].StripIn == stripIn && cacheSneakyInsaneToOsc_v1_0_0[idx].MeterGap == meterGap && cacheSneakyInsaneToOsc_v1_0_0[idx].MeterWidth == meterWidth && cacheSneakyInsaneToOsc_v1_0_0[idx].ShowConf == showConf && cacheSneakyInsaneToOsc_v1_0_0[idx].FlipDots == flipDots && cacheSneakyInsaneToOsc_v1_0_0[idx].AdxWeight == adxWeight && cacheSneakyInsaneToOsc_v1_0_0[idx].AdxFadeLo == adxFadeLo && cacheSneakyInsaneToOsc_v1_0_0[idx].AdxFadeHi == adxFadeHi && cacheSneakyInsaneToOsc_v1_0_0[idx].MeterHA == meterHA && cacheSneakyInsaneToOsc_v1_0_0[idx].ConfN == confN && cacheSneakyInsaneToOsc_v1_0_0[idx].Strict == strict && cacheSneakyInsaneToOsc_v1_0_0[idx].Cooldown == cooldown && cacheSneakyInsaneToOsc_v1_0_0[idx].VolConfirm == volConfirm && cacheSneakyInsaneToOsc_v1_0_0[idx].VolConfMult == volConfMult && cacheSneakyInsaneToOsc_v1_0_0[idx].Regime == regime && cacheSneakyInsaneToOsc_v1_0_0[idx].AdxTrend == adxTrend && cacheSneakyInsaneToOsc_v1_0_0[idx].UseHTF == useHTF && cacheSneakyInsaneToOsc_v1_0_0[idx].HTFMinutes == hTFMinutes && cacheSneakyInsaneToOsc_v1_0_0[idx].UseRef == useRef && cacheSneakyInsaneToOsc_v1_0_0[idx].RefSymbol == refSymbol && cacheSneakyInsaneToOsc_v1_0_0[idx].UseSession == useSession && cacheSneakyInsaneToOsc_v1_0_0[idx].SessStart == sessStart && cacheSneakyInsaneToOsc_v1_0_0[idx].SessEnd == sessEnd && cacheSneakyInsaneToOsc_v1_0_0[idx].SessTZ == sessTZ && cacheSneakyInsaneToOsc_v1_0_0[idx].ObosMode == obosMode && cacheSneakyInsaneToOsc_v1_0_0[idx].AdaptLen == adaptLen && cacheSneakyInsaneToOsc_v1_0_0[idx].AdaptMult == adaptMult && cacheSneakyInsaneToOsc_v1_0_0[idx].AdaptPct == adaptPct && cacheSneakyInsaneToOsc_v1_0_0[idx].RsiLength == rsiLength && cacheSneakyInsaneToOsc_v1_0_0[idx].RsiOS == rsiOS && cacheSneakyInsaneToOsc_v1_0_0[idx].RsiOB == rsiOB && cacheSneakyInsaneToOsc_v1_0_0[idx].RsiLowerW == rsiLowerW && cacheSneakyInsaneToOsc_v1_0_0[idx].RsiUpperW == rsiUpperW && cacheSneakyInsaneToOsc_v1_0_0[idx].MfiLength == mfiLength && cacheSneakyInsaneToOsc_v1_0_0[idx].IBBThreshold == iBBThreshold && cacheSneakyInsaneToOsc_v1_0_0[idx].RSIThreshold == rSIThreshold && cacheSneakyInsaneToOsc_v1_0_0[idx].RSIDown == rSIDown && cacheSneakyInsaneToOsc_v1_0_0[idx].LengthBB == lengthBB && cacheSneakyInsaneToOsc_v1_0_0[idx].MultBB == multBB && cacheSneakyInsaneToOsc_v1_0_0[idx].SqTolerance == sqTolerance && cacheSneakyInsaneToOsc_v1_0_0[idx].AdxSqueeze == adxSqueeze && cacheSneakyInsaneToOsc_v1_0_0[idx].AdxLen == adxLen && cacheSneakyInsaneToOsc_v1_0_0[idx].DiLen == diLen && cacheSneakyInsaneToOsc_v1_0_0[idx].WLengthBB == wLengthBB && cacheSneakyInsaneToOsc_v1_0_0[idx].WMultBB == wMultBB && cacheSneakyInsaneToOsc_v1_0_0[idx].Amplitude == amplitude && cacheSneakyInsaneToOsc_v1_0_0[idx].VaderRLength == vaderRLength && cacheSneakyInsaneToOsc_v1_0_0[idx].VaderDerAvg == vaderDerAvg && cacheSneakyInsaneToOsc_v1_0_0[idx].DerMaType == derMaType && cacheSneakyInsaneToOsc_v1_0_0[idx].VCalc == vCalc && cacheSneakyInsaneToOsc_v1_0_0[idx].VaderVLookback == vaderVLookback && cacheSneakyInsaneToOsc_v1_0_0[idx].VodkaLegacySpike == vodkaLegacySpike && cacheSneakyInsaneToOsc_v1_0_0[idx].Threshold == threshold && cacheSneakyInsaneToOsc_v1_0_0[idx].Sensitivity == sensitivity && cacheSneakyInsaneToOsc_v1_0_0[idx].FastLength == fastLength && cacheSneakyInsaneToOsc_v1_0_0[idx].SlowLength == slowLength && cacheSneakyInsaneToOsc_v1_0_0[idx].WtChannelLen == wtChannelLen && cacheSneakyInsaneToOsc_v1_0_0[idx].WtAverageLen == wtAverageLen && cacheSneakyInsaneToOsc_v1_0_0[idx].WtMALen == wtMALen && cacheSneakyInsaneToOsc_v1_0_0[idx].ObLevel == obLevel && cacheSneakyInsaneToOsc_v1_0_0[idx].OsLevel == osLevel && cacheSneakyInsaneToOsc_v1_0_0[idx].WtDivOBLevel == wtDivOBLevel && cacheSneakyInsaneToOsc_v1_0_0[idx].WtDivOSLevel == wtDivOSLevel && cacheSneakyInsaneToOsc_v1_0_0[idx].WtDivOBLevelAdd == wtDivOBLevelAdd && cacheSneakyInsaneToOsc_v1_0_0[idx].WtDivOSLevelAdd == wtDivOSLevelAdd && cacheSneakyInsaneToOsc_v1_0_0[idx].RsiDivOBLevel == rsiDivOBLevel && cacheSneakyInsaneToOsc_v1_0_0[idx].RsiDivOSLevel == rsiDivOSLevel && cacheSneakyInsaneToOsc_v1_0_0[idx].MfiDivOBLevel == mfiDivOBLevel && cacheSneakyInsaneToOsc_v1_0_0[idx].MfiDivOSLevel == mfiDivOSLevel && cacheSneakyInsaneToOsc_v1_0_0[idx].StochLen == stochLen && cacheSneakyInsaneToOsc_v1_0_0[idx].StochRsiLen == stochRsiLen && cacheSneakyInsaneToOsc_v1_0_0[idx].StochKSmooth == stochKSmooth && cacheSneakyInsaneToOsc_v1_0_0[idx].StochDSmooth == stochDSmooth && cacheSneakyInsaneToOsc_v1_0_0[idx].EnableSoundAlerts == enableSoundAlerts && cacheSneakyInsaneToOsc_v1_0_0[idx].AlertJSON == alertJSON && cacheSneakyInsaneToOsc_v1_0_0[idx].AlTramp == alTramp && cacheSneakyInsaneToOsc_v1_0_0[idx].AlSqz == alSqz && cacheSneakyInsaneToOsc_v1_0_0[idx].AlBG == alBG && cacheSneakyInsaneToOsc_v1_0_0[idx].AlDiv == alDiv && cacheSneakyInsaneToOsc_v1_0_0[idx].AlConf == alConf && cacheSneakyInsaneToOsc_v1_0_0[idx].AlFlip == alFlip && cacheSneakyInsaneToOsc_v1_0_0[idx].AlExh == alExh && cacheSneakyInsaneToOsc_v1_0_0[idx].AlPat == alPat && cacheSneakyInsaneToOsc_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyInsaneToOsc_v1_0_0[idx];
			return CacheIndicator<SneakyInsaneToOsc_v1_0_0>(new SneakyInsaneToOsc_v1_0_0(){ ColorTheme = colorTheme, MildOpacity = mildOpacity, Preset = preset, ShowRSI = showRSI, RsiStyle = rsiStyle, RsiWidth = rsiWidth, ShowGlow = showGlow, ShowBands = showBands, ShowTramp = showTramp, ShowSqueeze = showSqueeze, ShowBB = showBB, ShowVector = showVector, ShowExh = showExh, ShowPatterns = showPatterns, ShowStochX = showStochX, ShowWT = showWT, ShowWAE = showWAE, ThickWaddah = thickWaddah, SqzOnWAE = sqzOnWAE, ShowMFI = showMFI, ShowDivLabels = showDivLabels, DivLabelSize = divLabelSize, DivLabelGap = divLabelGap, ShowHidden = showHidden, ShowDivies = showDivies, ShowBuySellies = showBuySellies, BgTrans = bgTrans, BarColorConf = barColorConf, ShowDeltaDivergence = showDeltaDivergence, ShowRSIRibbon = showRSIRibbon, RibbonTransp = ribbonTransp, ArrowGap = arrowGap, ShowTrend = showTrend, MeterPos = meterPos, StripIn = stripIn, MeterGap = meterGap, MeterWidth = meterWidth, ShowConf = showConf, FlipDots = flipDots, AdxWeight = adxWeight, AdxFadeLo = adxFadeLo, AdxFadeHi = adxFadeHi, MeterHA = meterHA, ConfN = confN, Strict = strict, Cooldown = cooldown, VolConfirm = volConfirm, VolConfMult = volConfMult, Regime = regime, AdxTrend = adxTrend, UseHTF = useHTF, HTFMinutes = hTFMinutes, UseRef = useRef, RefSymbol = refSymbol, UseSession = useSession, SessStart = sessStart, SessEnd = sessEnd, SessTZ = sessTZ, ObosMode = obosMode, AdaptLen = adaptLen, AdaptMult = adaptMult, AdaptPct = adaptPct, RsiLength = rsiLength, RsiOS = rsiOS, RsiOB = rsiOB, RsiLowerW = rsiLowerW, RsiUpperW = rsiUpperW, MfiLength = mfiLength, IBBThreshold = iBBThreshold, RSIThreshold = rSIThreshold, RSIDown = rSIDown, LengthBB = lengthBB, MultBB = multBB, SqTolerance = sqTolerance, AdxSqueeze = adxSqueeze, AdxLen = adxLen, DiLen = diLen, WLengthBB = wLengthBB, WMultBB = wMultBB, Amplitude = amplitude, VaderRLength = vaderRLength, VaderDerAvg = vaderDerAvg, DerMaType = derMaType, VCalc = vCalc, VaderVLookback = vaderVLookback, VodkaLegacySpike = vodkaLegacySpike, Threshold = threshold, Sensitivity = sensitivity, FastLength = fastLength, SlowLength = slowLength, WtChannelLen = wtChannelLen, WtAverageLen = wtAverageLen, WtMALen = wtMALen, ObLevel = obLevel, OsLevel = osLevel, WtDivOBLevel = wtDivOBLevel, WtDivOSLevel = wtDivOSLevel, WtDivOBLevelAdd = wtDivOBLevelAdd, WtDivOSLevelAdd = wtDivOSLevelAdd, RsiDivOBLevel = rsiDivOBLevel, RsiDivOSLevel = rsiDivOSLevel, MfiDivOBLevel = mfiDivOBLevel, MfiDivOSLevel = mfiDivOSLevel, StochLen = stochLen, StochRsiLen = stochRsiLen, StochKSmooth = stochKSmooth, StochDSmooth = stochDSmooth, EnableSoundAlerts = enableSoundAlerts, AlertJSON = alertJSON, AlTramp = alTramp, AlSqz = alSqz, AlBG = alBG, AlDiv = alDiv, AlConf = alConf, AlFlip = alFlip, AlExh = alExh, AlPat = alPat }, input, ref cacheSneakyInsaneToOsc_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.SneakyInsaneToOsc_v1_0_0 SneakyInsaneToOsc_v1_0_0(SneakyInsaneToOsc_v1_0_0Theme colorTheme, int mildOpacity, SneakyInsaneToOsc_v1_0_0Preset preset, bool showRSI, SneakyInsaneToOsc_v1_0_0RsiStyle rsiStyle, int rsiWidth, bool showGlow, bool showBands, bool showTramp, bool showSqueeze, bool showBB, bool showVector, bool showExh, bool showPatterns, bool showStochX, bool showWT, bool showWAE, int thickWaddah, bool sqzOnWAE, bool showMFI, bool showDivLabels, int divLabelSize, int divLabelGap, bool showHidden, bool showDivies, bool showBuySellies, int bgTrans, bool barColorConf, bool showDeltaDivergence, bool showRSIRibbon, int ribbonTransp, int arrowGap, bool showTrend, SneakyInsaneToOsc_v1_0_0MeterPos meterPos, double stripIn, double meterGap, int meterWidth, bool showConf, bool flipDots, bool adxWeight, int adxFadeLo, int adxFadeHi, bool meterHA, int confN, bool strict, int cooldown, bool volConfirm, double volConfMult, bool regime, int adxTrend, bool useHTF, int hTFMinutes, bool useRef, string refSymbol, bool useSession, int sessStart, int sessEnd, string sessTZ, SneakyInsaneToOsc_v1_0_0ObosMode obosMode, int adaptLen, double adaptMult, int adaptPct, int rsiLength, int rsiOS, int rsiOB, int rsiLowerW, int rsiUpperW, int mfiLength, double iBBThreshold, int rSIThreshold, int rSIDown, int lengthBB, double multBB, int sqTolerance, int adxSqueeze, int adxLen, int diLen, int wLengthBB, double wMultBB, int amplitude, int vaderRLength, int vaderDerAvg, SneakyInsaneToOsc_v1_0_0MaType derMaType, SneakyInsaneToOsc_v1_0_0VolCalc vCalc, int vaderVLookback, bool vodkaLegacySpike, int threshold, int sensitivity, int fastLength, int slowLength, int wtChannelLen, int wtAverageLen, int wtMALen, int obLevel, int osLevel, int wtDivOBLevel, int wtDivOSLevel, int wtDivOBLevelAdd, int wtDivOSLevelAdd, int rsiDivOBLevel, int rsiDivOSLevel, int mfiDivOBLevel, int mfiDivOSLevel, int stochLen, int stochRsiLen, int stochKSmooth, int stochDSmooth, bool enableSoundAlerts, bool alertJSON, bool alTramp, bool alSqz, bool alBG, bool alDiv, bool alConf, bool alFlip, bool alExh, bool alPat)
		{
			return indicator.SneakyInsaneToOsc_v1_0_0(Input, colorTheme, mildOpacity, preset, showRSI, rsiStyle, rsiWidth, showGlow, showBands, showTramp, showSqueeze, showBB, showVector, showExh, showPatterns, showStochX, showWT, showWAE, thickWaddah, sqzOnWAE, showMFI, showDivLabels, divLabelSize, divLabelGap, showHidden, showDivies, showBuySellies, bgTrans, barColorConf, showDeltaDivergence, showRSIRibbon, ribbonTransp, arrowGap, showTrend, meterPos, stripIn, meterGap, meterWidth, showConf, flipDots, adxWeight, adxFadeLo, adxFadeHi, meterHA, confN, strict, cooldown, volConfirm, volConfMult, regime, adxTrend, useHTF, hTFMinutes, useRef, refSymbol, useSession, sessStart, sessEnd, sessTZ, obosMode, adaptLen, adaptMult, adaptPct, rsiLength, rsiOS, rsiOB, rsiLowerW, rsiUpperW, mfiLength, iBBThreshold, rSIThreshold, rSIDown, lengthBB, multBB, sqTolerance, adxSqueeze, adxLen, diLen, wLengthBB, wMultBB, amplitude, vaderRLength, vaderDerAvg, derMaType, vCalc, vaderVLookback, vodkaLegacySpike, threshold, sensitivity, fastLength, slowLength, wtChannelLen, wtAverageLen, wtMALen, obLevel, osLevel, wtDivOBLevel, wtDivOSLevel, wtDivOBLevelAdd, wtDivOSLevelAdd, rsiDivOBLevel, rsiDivOSLevel, mfiDivOBLevel, mfiDivOSLevel, stochLen, stochRsiLen, stochKSmooth, stochDSmooth, enableSoundAlerts, alertJSON, alTramp, alSqz, alBG, alDiv, alConf, alFlip, alExh, alPat);
		}

		public Indicators.SneakyInsaneToOsc_v1_0_0 SneakyInsaneToOsc_v1_0_0(ISeries<double> input , SneakyInsaneToOsc_v1_0_0Theme colorTheme, int mildOpacity, SneakyInsaneToOsc_v1_0_0Preset preset, bool showRSI, SneakyInsaneToOsc_v1_0_0RsiStyle rsiStyle, int rsiWidth, bool showGlow, bool showBands, bool showTramp, bool showSqueeze, bool showBB, bool showVector, bool showExh, bool showPatterns, bool showStochX, bool showWT, bool showWAE, int thickWaddah, bool sqzOnWAE, bool showMFI, bool showDivLabels, int divLabelSize, int divLabelGap, bool showHidden, bool showDivies, bool showBuySellies, int bgTrans, bool barColorConf, bool showDeltaDivergence, bool showRSIRibbon, int ribbonTransp, int arrowGap, bool showTrend, SneakyInsaneToOsc_v1_0_0MeterPos meterPos, double stripIn, double meterGap, int meterWidth, bool showConf, bool flipDots, bool adxWeight, int adxFadeLo, int adxFadeHi, bool meterHA, int confN, bool strict, int cooldown, bool volConfirm, double volConfMult, bool regime, int adxTrend, bool useHTF, int hTFMinutes, bool useRef, string refSymbol, bool useSession, int sessStart, int sessEnd, string sessTZ, SneakyInsaneToOsc_v1_0_0ObosMode obosMode, int adaptLen, double adaptMult, int adaptPct, int rsiLength, int rsiOS, int rsiOB, int rsiLowerW, int rsiUpperW, int mfiLength, double iBBThreshold, int rSIThreshold, int rSIDown, int lengthBB, double multBB, int sqTolerance, int adxSqueeze, int adxLen, int diLen, int wLengthBB, double wMultBB, int amplitude, int vaderRLength, int vaderDerAvg, SneakyInsaneToOsc_v1_0_0MaType derMaType, SneakyInsaneToOsc_v1_0_0VolCalc vCalc, int vaderVLookback, bool vodkaLegacySpike, int threshold, int sensitivity, int fastLength, int slowLength, int wtChannelLen, int wtAverageLen, int wtMALen, int obLevel, int osLevel, int wtDivOBLevel, int wtDivOSLevel, int wtDivOBLevelAdd, int wtDivOSLevelAdd, int rsiDivOBLevel, int rsiDivOSLevel, int mfiDivOBLevel, int mfiDivOSLevel, int stochLen, int stochRsiLen, int stochKSmooth, int stochDSmooth, bool enableSoundAlerts, bool alertJSON, bool alTramp, bool alSqz, bool alBG, bool alDiv, bool alConf, bool alFlip, bool alExh, bool alPat)
		{
			return indicator.SneakyInsaneToOsc_v1_0_0(input, colorTheme, mildOpacity, preset, showRSI, rsiStyle, rsiWidth, showGlow, showBands, showTramp, showSqueeze, showBB, showVector, showExh, showPatterns, showStochX, showWT, showWAE, thickWaddah, sqzOnWAE, showMFI, showDivLabels, divLabelSize, divLabelGap, showHidden, showDivies, showBuySellies, bgTrans, barColorConf, showDeltaDivergence, showRSIRibbon, ribbonTransp, arrowGap, showTrend, meterPos, stripIn, meterGap, meterWidth, showConf, flipDots, adxWeight, adxFadeLo, adxFadeHi, meterHA, confN, strict, cooldown, volConfirm, volConfMult, regime, adxTrend, useHTF, hTFMinutes, useRef, refSymbol, useSession, sessStart, sessEnd, sessTZ, obosMode, adaptLen, adaptMult, adaptPct, rsiLength, rsiOS, rsiOB, rsiLowerW, rsiUpperW, mfiLength, iBBThreshold, rSIThreshold, rSIDown, lengthBB, multBB, sqTolerance, adxSqueeze, adxLen, diLen, wLengthBB, wMultBB, amplitude, vaderRLength, vaderDerAvg, derMaType, vCalc, vaderVLookback, vodkaLegacySpike, threshold, sensitivity, fastLength, slowLength, wtChannelLen, wtAverageLen, wtMALen, obLevel, osLevel, wtDivOBLevel, wtDivOSLevel, wtDivOBLevelAdd, wtDivOSLevelAdd, rsiDivOBLevel, rsiDivOSLevel, mfiDivOBLevel, mfiDivOSLevel, stochLen, stochRsiLen, stochKSmooth, stochDSmooth, enableSoundAlerts, alertJSON, alTramp, alSqz, alBG, alDiv, alConf, alFlip, alExh, alPat);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.SneakyInsaneToOsc_v1_0_0 SneakyInsaneToOsc_v1_0_0(SneakyInsaneToOsc_v1_0_0Theme colorTheme, int mildOpacity, SneakyInsaneToOsc_v1_0_0Preset preset, bool showRSI, SneakyInsaneToOsc_v1_0_0RsiStyle rsiStyle, int rsiWidth, bool showGlow, bool showBands, bool showTramp, bool showSqueeze, bool showBB, bool showVector, bool showExh, bool showPatterns, bool showStochX, bool showWT, bool showWAE, int thickWaddah, bool sqzOnWAE, bool showMFI, bool showDivLabels, int divLabelSize, int divLabelGap, bool showHidden, bool showDivies, bool showBuySellies, int bgTrans, bool barColorConf, bool showDeltaDivergence, bool showRSIRibbon, int ribbonTransp, int arrowGap, bool showTrend, SneakyInsaneToOsc_v1_0_0MeterPos meterPos, double stripIn, double meterGap, int meterWidth, bool showConf, bool flipDots, bool adxWeight, int adxFadeLo, int adxFadeHi, bool meterHA, int confN, bool strict, int cooldown, bool volConfirm, double volConfMult, bool regime, int adxTrend, bool useHTF, int hTFMinutes, bool useRef, string refSymbol, bool useSession, int sessStart, int sessEnd, string sessTZ, SneakyInsaneToOsc_v1_0_0ObosMode obosMode, int adaptLen, double adaptMult, int adaptPct, int rsiLength, int rsiOS, int rsiOB, int rsiLowerW, int rsiUpperW, int mfiLength, double iBBThreshold, int rSIThreshold, int rSIDown, int lengthBB, double multBB, int sqTolerance, int adxSqueeze, int adxLen, int diLen, int wLengthBB, double wMultBB, int amplitude, int vaderRLength, int vaderDerAvg, SneakyInsaneToOsc_v1_0_0MaType derMaType, SneakyInsaneToOsc_v1_0_0VolCalc vCalc, int vaderVLookback, bool vodkaLegacySpike, int threshold, int sensitivity, int fastLength, int slowLength, int wtChannelLen, int wtAverageLen, int wtMALen, int obLevel, int osLevel, int wtDivOBLevel, int wtDivOSLevel, int wtDivOBLevelAdd, int wtDivOSLevelAdd, int rsiDivOBLevel, int rsiDivOSLevel, int mfiDivOBLevel, int mfiDivOSLevel, int stochLen, int stochRsiLen, int stochKSmooth, int stochDSmooth, bool enableSoundAlerts, bool alertJSON, bool alTramp, bool alSqz, bool alBG, bool alDiv, bool alConf, bool alFlip, bool alExh, bool alPat)
		{
			return indicator.SneakyInsaneToOsc_v1_0_0(Input, colorTheme, mildOpacity, preset, showRSI, rsiStyle, rsiWidth, showGlow, showBands, showTramp, showSqueeze, showBB, showVector, showExh, showPatterns, showStochX, showWT, showWAE, thickWaddah, sqzOnWAE, showMFI, showDivLabels, divLabelSize, divLabelGap, showHidden, showDivies, showBuySellies, bgTrans, barColorConf, showDeltaDivergence, showRSIRibbon, ribbonTransp, arrowGap, showTrend, meterPos, stripIn, meterGap, meterWidth, showConf, flipDots, adxWeight, adxFadeLo, adxFadeHi, meterHA, confN, strict, cooldown, volConfirm, volConfMult, regime, adxTrend, useHTF, hTFMinutes, useRef, refSymbol, useSession, sessStart, sessEnd, sessTZ, obosMode, adaptLen, adaptMult, adaptPct, rsiLength, rsiOS, rsiOB, rsiLowerW, rsiUpperW, mfiLength, iBBThreshold, rSIThreshold, rSIDown, lengthBB, multBB, sqTolerance, adxSqueeze, adxLen, diLen, wLengthBB, wMultBB, amplitude, vaderRLength, vaderDerAvg, derMaType, vCalc, vaderVLookback, vodkaLegacySpike, threshold, sensitivity, fastLength, slowLength, wtChannelLen, wtAverageLen, wtMALen, obLevel, osLevel, wtDivOBLevel, wtDivOSLevel, wtDivOBLevelAdd, wtDivOSLevelAdd, rsiDivOBLevel, rsiDivOSLevel, mfiDivOBLevel, mfiDivOSLevel, stochLen, stochRsiLen, stochKSmooth, stochDSmooth, enableSoundAlerts, alertJSON, alTramp, alSqz, alBG, alDiv, alConf, alFlip, alExh, alPat);
		}

		public Indicators.SneakyInsaneToOsc_v1_0_0 SneakyInsaneToOsc_v1_0_0(ISeries<double> input , SneakyInsaneToOsc_v1_0_0Theme colorTheme, int mildOpacity, SneakyInsaneToOsc_v1_0_0Preset preset, bool showRSI, SneakyInsaneToOsc_v1_0_0RsiStyle rsiStyle, int rsiWidth, bool showGlow, bool showBands, bool showTramp, bool showSqueeze, bool showBB, bool showVector, bool showExh, bool showPatterns, bool showStochX, bool showWT, bool showWAE, int thickWaddah, bool sqzOnWAE, bool showMFI, bool showDivLabels, int divLabelSize, int divLabelGap, bool showHidden, bool showDivies, bool showBuySellies, int bgTrans, bool barColorConf, bool showDeltaDivergence, bool showRSIRibbon, int ribbonTransp, int arrowGap, bool showTrend, SneakyInsaneToOsc_v1_0_0MeterPos meterPos, double stripIn, double meterGap, int meterWidth, bool showConf, bool flipDots, bool adxWeight, int adxFadeLo, int adxFadeHi, bool meterHA, int confN, bool strict, int cooldown, bool volConfirm, double volConfMult, bool regime, int adxTrend, bool useHTF, int hTFMinutes, bool useRef, string refSymbol, bool useSession, int sessStart, int sessEnd, string sessTZ, SneakyInsaneToOsc_v1_0_0ObosMode obosMode, int adaptLen, double adaptMult, int adaptPct, int rsiLength, int rsiOS, int rsiOB, int rsiLowerW, int rsiUpperW, int mfiLength, double iBBThreshold, int rSIThreshold, int rSIDown, int lengthBB, double multBB, int sqTolerance, int adxSqueeze, int adxLen, int diLen, int wLengthBB, double wMultBB, int amplitude, int vaderRLength, int vaderDerAvg, SneakyInsaneToOsc_v1_0_0MaType derMaType, SneakyInsaneToOsc_v1_0_0VolCalc vCalc, int vaderVLookback, bool vodkaLegacySpike, int threshold, int sensitivity, int fastLength, int slowLength, int wtChannelLen, int wtAverageLen, int wtMALen, int obLevel, int osLevel, int wtDivOBLevel, int wtDivOSLevel, int wtDivOBLevelAdd, int wtDivOSLevelAdd, int rsiDivOBLevel, int rsiDivOSLevel, int mfiDivOBLevel, int mfiDivOSLevel, int stochLen, int stochRsiLen, int stochKSmooth, int stochDSmooth, bool enableSoundAlerts, bool alertJSON, bool alTramp, bool alSqz, bool alBG, bool alDiv, bool alConf, bool alFlip, bool alExh, bool alPat)
		{
			return indicator.SneakyInsaneToOsc_v1_0_0(input, colorTheme, mildOpacity, preset, showRSI, rsiStyle, rsiWidth, showGlow, showBands, showTramp, showSqueeze, showBB, showVector, showExh, showPatterns, showStochX, showWT, showWAE, thickWaddah, sqzOnWAE, showMFI, showDivLabels, divLabelSize, divLabelGap, showHidden, showDivies, showBuySellies, bgTrans, barColorConf, showDeltaDivergence, showRSIRibbon, ribbonTransp, arrowGap, showTrend, meterPos, stripIn, meterGap, meterWidth, showConf, flipDots, adxWeight, adxFadeLo, adxFadeHi, meterHA, confN, strict, cooldown, volConfirm, volConfMult, regime, adxTrend, useHTF, hTFMinutes, useRef, refSymbol, useSession, sessStart, sessEnd, sessTZ, obosMode, adaptLen, adaptMult, adaptPct, rsiLength, rsiOS, rsiOB, rsiLowerW, rsiUpperW, mfiLength, iBBThreshold, rSIThreshold, rSIDown, lengthBB, multBB, sqTolerance, adxSqueeze, adxLen, diLen, wLengthBB, wMultBB, amplitude, vaderRLength, vaderDerAvg, derMaType, vCalc, vaderVLookback, vodkaLegacySpike, threshold, sensitivity, fastLength, slowLength, wtChannelLen, wtAverageLen, wtMALen, obLevel, osLevel, wtDivOBLevel, wtDivOSLevel, wtDivOBLevelAdd, wtDivOSLevelAdd, rsiDivOBLevel, rsiDivOSLevel, mfiDivOBLevel, mfiDivOSLevel, stochLen, stochRsiLen, stochKSmooth, stochDSmooth, enableSoundAlerts, alertJSON, alTramp, alSqz, alBG, alDiv, alConf, alFlip, alExh, alPat);
		}
	}
}

#endregion
