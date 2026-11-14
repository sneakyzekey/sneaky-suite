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

// 2026-09-22 -- PHASE 2 of the from-scratch "Praedox Sentinel" rebuild (see
// PraedoxSignalEngine_v0_1_0 for phase 1 and the full project context/disclaimer -- same
// situation here: original implementation from marketing-copy behavior, no surviving
// source). This phase is the "Rolling Zone Fibonacci Engine" -- an auto-updating Fibonacci
// retracement grid plus the confluence/suppression signals it's meant to feed into the
// signal engine later (that wiring is a follow-up phase, not done in this file yet).
//
// SWING DETECTION ("rolling zone"): every bar, scan the last FibLookback bars for the
// highest high and lowest low AND which one is more recent (smaller bars-ago = more
// recent). Whichever extreme is more recent anchors the 0% end of the grid -- that's the
// "auto-detected direction":
//   Direction =  1 (Up)   -> the low is the more recent extreme. Grid runs 0% at the low
//                             up to 100% at the high. Levels between are read as
//                             RESISTANCE the market is retracing UP into.
//   Direction = -1 (Down) -> the high is the more recent extreme. Grid runs 0% at the high
//                             down to 100% at the low. Levels are read as SUPPORT the
//                             market is retracing DOWN into.
// Because the window is rolling, a new extreme forming shifts the anchor and can FLIP the
// direction -- that's "sidebar alerts when fibs flip" in the spec; this file surfaces the
// flip as an on-chart text marker rather than a literal sidebar (no cockpit UI yet).
//
// THE 8 LEVELS: 0, 23.6, 38.2, 50, 61.8, 65, 78.6, 100 -- matches "8 levels" and the
// specific percentages visible in the reference screenshots (23.6% weak support, 38.2%
// moderate, 50% psychological, 61.8% Golden Ratio, 65% Golden Pocket, 78.6% deep
// retracement). Golden Zone = the 50-61.8% band, Golden Pocket = 61.8-65% -- both shaded.
//
// KILL ZONE / CONFLUENCE (same physical zone, two different consumers): a price band
// (KillZoneTicks wide) around each of the 61.8%, 65%, and 78.6% levels -- the spec's "key
// fib levels." InKillZone[0] is meant to SUPPRESS breakout/continuation-style signals
// (price fighting a major level is a bad place to trust a breakout), while
// NearKeyLevel[0] is meant to give CONFLUENCE credit to reversal-style signals firing in
// that same zone (a reversal AT a major level is exactly the "instant confirmation" the
// spec describes). Which consumer uses which output is up to whatever wires this into the
// signal engine next -- this file just computes and draws both.
//
// EXHAUSTION SUPPRESSION: if the anchor extreme (whichever one is currently at the 0% end)
// hasn't been replaced by a NEWER extreme in StaleBarsThreshold bars, the move this fib
// grid is measuring is old -- ExhaustionActive[0] goes true (drawn as a purple zone at the
// anchor) until a fresh extreme resets it. Per spec this is meant to block continuation
// signals (the move already played out) while still allowing reversal signals through
// (which is exactly what a stale extreme suggests might be coming).
//
// OUTPUTS (sub-panel plots, not overlaid on price so they don't distort the price y-axis --
// the fib lines/zones themselves still draw on the price chart via Draw.*, same pattern as
// TurtleTrendBreakoutSignal_v1_0_0's range box):
//   Direction[0]       ==  1 / -1        -> current grid orientation (see above)
//   NearKeyLevel[0]    ==  1 / -1 / 0    -> price is within the kill-zone tolerance of a
//                                            key level, signed by Direction (confluence)
//   InKillZone[0]      ==  1 / 0         -> price is inside a kill zone (suppression)
//   ExhaustionActive[0]==  1 / 0         -> anchor extreme is stale (see above)
// The 8 level PRICES themselves are exposed as plain (non-plotted) Series<double>
// properties -- Level0/Level236/Level382/Level500/Level618/Level650/Level786/Level1000 --
// for another script to read the actual price of a level without them cluttering either
// panel's y-axis scale.
namespace NinjaTrader.NinjaScript.Indicators
{
    public class PraedoxFibEngine_v0_1_0 : Indicator
    {
        private static readonly double[] LevelPercents = { 0, 23.6, 38.2, 50, 61.8, 65, 78.6, 100 };
        private static readonly string[] LevelLabels = { "0%", "23.6%", "38.2%", "50%", "61.8%", "65%", "78.6%", "100%" };

        // Bold + a bit larger than NT8's default chart font so the level labels stay
        // legible against whatever's drawn behind them (candles, golden zone, kill zones).
        private static readonly SimpleFont labelFont = new SimpleFont("Arial", 12) { Bold = true };

        // Non-plotted price-scale series, one per level above, indexed the same way.
        private Series<double>[] levelSeries;

        private int lastDirection;
        private int anchorBarsAgo; // how many bars ago the currently-anchoring extreme sat

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "PraedoxFibEngine_v0_1_0";
                Description = "Phase 2: rolling auto-Fibonacci grid (8 levels), Golden Zone/Pocket, kill-zone suppression + confluence, exhaustion suppression. Original implementation from behavior spec.";

                Calculate = Calculate.OnBarClose;
                IsOverlay = false;
                DisplayInDataBox = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;

                FibLookback = 50;
                StaleBarsThreshold = 35;
                KillZoneTicks = 8;
                ZoneDisplayBars = 15;

                DrawFibLines = true;
                DrawGoldenZone = true;
                DrawGoldenPocket = true;
                DrawKillZones = true;
                DrawExhaustionZone = true;
                DrawFlipAlerts = true;

                AddPlot(new Stroke(Brushes.DodgerBlue, 2), PlotStyle.Line, "Direction");
                AddPlot(new Stroke(Brushes.Cyan, 2), PlotStyle.Bar, "NearKeyLevel");
                AddPlot(new Stroke(Brushes.OrangeRed, 1), PlotStyle.Bar, "InKillZone");
                AddPlot(new Stroke(Brushes.MediumPurple, 1), PlotStyle.Bar, "ExhaustionActive");
            }
            else if (State == State.DataLoaded)
            {
                levelSeries = new Series<double>[LevelPercents.Length];
                for (int i = 0; i < levelSeries.Length; i++)
                    levelSeries[i] = new Series<double>(this);
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < FibLookback + 1)
            {
                Values[0][0] = 0;
                Values[1][0] = 0;
                Values[2][0] = 0;
                Values[3][0] = 0;
                return;
            }

            double swingHigh, swingLow;
            int swingHighBarsAgo, swingLowBarsAgo;
            FindSwingExtremes(FibLookback, out swingHigh, out swingHighBarsAgo, out swingLow, out swingLowBarsAgo);

            // More recent (smaller bars-ago) extreme anchors the 0% end. A tie (shouldn't
            // happen with real price data) keeps the previous direction rather than
            // guessing.
            int direction;
            if (swingHighBarsAgo < swingLowBarsAgo)
            {
                direction = -1; // high is more recent -> retracing down from it
                anchorBarsAgo = swingHighBarsAgo;
            }
            else if (swingLowBarsAgo < swingHighBarsAgo)
            {
                direction = 1; // low is more recent -> retracing up from it
                anchorBarsAgo = swingLowBarsAgo;
            }
            else
            {
                direction = lastDirection != 0 ? lastDirection : 1;
                anchorBarsAgo = Math.Min(swingHighBarsAgo, swingLowBarsAgo);
            }

            bool flipped = lastDirection != 0 && direction != lastDirection;
            lastDirection = direction;

            // Compute and store each level's price. Direction 1 (Up): 0% at swingLow,
            // 100% at swingHigh. Direction -1 (Down): 0% at swingHigh, 100% at swingLow.
            double range = swingHigh - swingLow;
            for (int i = 0; i < LevelPercents.Length; i++)
            {
                double pct = LevelPercents[i] / 100.0;
                double price = direction > 0
                    ? swingLow + range * pct
                    : swingHigh - range * pct;
                levelSeries[i][0] = Instrument.MasterInstrument.RoundToTickSize(price);
            }

            double level618 = levelSeries[4][0];
            double level650 = levelSeries[5][0];
            double level786 = levelSeries[6][0];

            double tolerance = TickSize * KillZoneTicks;
            bool nearAny =
                Math.Abs(Close[0] - level618) <= tolerance ||
                Math.Abs(Close[0] - level650) <= tolerance ||
                Math.Abs(Close[0] - level786) <= tolerance;

            bool exhaustion = anchorBarsAgo >= StaleBarsThreshold;

            Values[0][0] = direction;
            Values[1][0] = nearAny ? direction : 0;
            Values[2][0] = nearAny ? 1 : 0;
            Values[3][0] = exhaustion ? 1 : 0;

            if (DrawFibLines || DrawGoldenZone || DrawGoldenPocket || DrawKillZones || DrawExhaustionZone)
                DrawGrid(swingHigh, swingLow, direction, level618, level650, level786, exhaustion);

            if (DrawFlipAlerts && flipped)
            {
                string text = direction > 0 ? "Fib flip -> UP" : "Fib flip -> DOWN";
                Brush color = direction > 0 ? Brushes.Lime : Brushes.OrangeRed;
                Draw.Text(this, "PraedoxFibFlip" + CurrentBar, text, 0, direction > 0 ? Low[0] - 4 * TickSize : High[0] + 4 * TickSize, color);
            }
        }

        /// <summary>
        /// Scans the last `lookback` bars (plus the current bar) for the highest high and
        /// lowest low and how many bars ago each one sits. A plain loop rather than
        /// MAX()/MIN() because, unlike those built-ins, this also needs to know WHICH bar
        /// produced the extreme, not just its value.
        /// </summary>
        private void FindSwingExtremes(int lookback, out double swingHigh, out int swingHighBarsAgo, out double swingLow, out int swingLowBarsAgo)
        {
            swingHigh = High[0];
            swingHighBarsAgo = 0;
            swingLow = Low[0];
            swingLowBarsAgo = 0;

            int n = Math.Min(lookback, CurrentBar);
            for (int i = 1; i <= n; i++)
            {
                if (High[i] > swingHigh)
                {
                    swingHigh = High[i];
                    swingHighBarsAgo = i;
                }
                if (Low[i] < swingLow)
                {
                    swingLow = Low[i];
                    swingLowBarsAgo = i;
                }
            }
        }

        private void DrawGrid(double swingHigh, double swingLow, int direction, double level618, double level650, double level786, bool exhaustion)
        {
            int anchorSpan = Math.Max(anchorBarsAgo, 1);

            // The rectangles (fib lines, golden zone/pocket, kill zones, exhaustion zone)
            // used to stretch all the way back to the anchor bar -- fine most of the time,
            // but once a swing extreme goes stale (anchorBarsAgo climbing toward
            // StaleBarsThreshold/FibLookback) that turned into a zone spanning dozens of
            // bars and swallowing most of the visible chart, most noticeably the purple
            // exhaustion zone. Capping the DRAWN width at ZoneDisplayBars keeps every zone
            // a fixed, readable size near the right edge regardless of how stale the
            // anchor is -- the actual exhaustion/direction logic above still uses the real
            // anchorSpan, only the drawing width is capped.
            int displaySpan = Math.Min(anchorSpan, ZoneDisplayBars);

            if (DrawFibLines)
            {
                for (int i = 0; i < LevelPercents.Length; i++)
                {
                    double price = levelSeries[i][0];
                    string tag = "PraedoxFibLevel" + i;
                    Brush lineBrush = (i == 0 || i == LevelPercents.Length - 1) ? Brushes.DimGray : Brushes.Goldenrod;
                    // A near-zero-height rectangle reads as a horizontal line without
                    // relying on Draw.Line's exact signature -- same low-risk approach
                    // used for the label offsets elsewhere in this project.
                    Draw.Rectangle(this, tag, false, displaySpan, price + TickSize * 0.5, 0, price - TickSize * 0.5, lineBrush, lineBrush, 40);

                    // Labels sit at x=0 (the current bar), tucked next to the price axis --
                    // matches how the original Praedox Sentinel product renders them (short
                    // "61.8%"-style tags hugging the right edge, not spread across the
                    // chart). Percentage-only text is the declutter vs. the old
                    // "61.8% - Golden Ratio - Key Support - High Probability" long-form
                    // labels from the reference screenshots.
                    //
                    // Plain colored text was getting lost against the golden zone/pocket
                    // and kill zone fills sitting right behind it (goldenrod-on-navy,
                    // white-on-white, etc.), and the 61.8%/65% pair sits close enough in
                    // price to nearly overlap. Fixed with: a solid dark backing plate
                    // behind every label so it reads against any zone color, a bold font
                    // for legibility at a glance, and a small alternating pixel nudge so
                    // neighboring levels don't render on the same row.
                    int pixelNudge = (i % 2 == 0) ? -7 : 7;
                    Draw.Text(this, tag + "Label", false, LevelLabels[i], 0, price, pixelNudge, Brushes.White, labelFont, TextAlignment.Left, Brushes.Black, Brushes.Black, 80);
                }
            }

            if (DrawGoldenZone)
            {
                double a = levelSeries[3][0]; // 50%
                double b = levelSeries[4][0]; // 61.8%
                Draw.Rectangle(this, "PraedoxGoldenZone", false, displaySpan, Math.Max(a, b), 0, Math.Min(a, b), Brushes.SteelBlue, Brushes.SteelBlue, 15);
            }

            if (DrawGoldenPocket)
            {
                double a = levelSeries[4][0]; // 61.8%
                double b = levelSeries[5][0]; // 65%
                Draw.Rectangle(this, "PraedoxGoldenPocket", false, displaySpan, Math.Max(a, b), 0, Math.Min(a, b), Brushes.Orange, Brushes.Orange, 20);
            }

            if (DrawKillZones)
            {
                double tolerance = TickSize * KillZoneTicks;
                Brush zoneBrush = direction > 0 ? Brushes.Green : Brushes.Red;
                Draw.Rectangle(this, "PraedoxKillZone618", false, displaySpan, level618 + tolerance, 0, level618 - tolerance, zoneBrush, zoneBrush, 12);
                Draw.Rectangle(this, "PraedoxKillZone650", false, displaySpan, level650 + tolerance, 0, level650 - tolerance, zoneBrush, zoneBrush, 12);
                Draw.Rectangle(this, "PraedoxKillZone786", false, displaySpan, level786 + tolerance, 0, level786 - tolerance, zoneBrush, zoneBrush, 12);
            }

            if (DrawExhaustionZone && exhaustion)
            {
                double anchorPrice = direction > 0 ? swingLow : swingHigh;
                double span = (swingHigh - swingLow) * 0.05;
                Draw.Rectangle(this, "PraedoxExhaustionZone", false, displaySpan, anchorPrice + span, 0, anchorPrice - span, Brushes.MediumPurple, Brushes.MediumPurple, 35);
            }
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(5, int.MaxValue)]
        [Display(Name = "Fib Lookback (bars)", GroupName = "Core", Order = 1)]
        public int FibLookback { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Stale/Exhaustion Threshold (bars)", GroupName = "Core", Order = 2)]
        public int StaleBarsThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Kill Zone Width (ticks)", GroupName = "Core", Order = 3)]
        public int KillZoneTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Zone Display Width (bars)", GroupName = "Core", Order = 4, Description = "How many bars back the fib lines/zones are drawn, regardless of how stale the anchor swing is. Keeps the exhaustion zone (and everything else) from stretching across the whole chart once a swing goes stale.")]
        public int ZoneDisplayBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw Fib Lines", GroupName = "Display", Order = 1)]
        public bool DrawFibLines { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw Golden Zone (50-61.8%)", GroupName = "Display", Order = 2)]
        public bool DrawGoldenZone { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw Golden Pocket (61.8-65%)", GroupName = "Display", Order = 3)]
        public bool DrawGoldenPocket { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw Kill Zones", GroupName = "Display", Order = 4)]
        public bool DrawKillZones { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw Exhaustion Zone", GroupName = "Display", Order = 5)]
        public bool DrawExhaustionZone { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw Flip Alerts", GroupName = "Display", Order = 6)]
        public bool DrawFlipAlerts { get; set; }

        [Browsable(false)]
        [XmlIgnore()]
        public Series<double> Direction
        {
            get { return Values[0]; }
        }

        [Browsable(false)]
        [XmlIgnore()]
        public Series<double> NearKeyLevel
        {
            get { return Values[1]; }
        }

        [Browsable(false)]
        [XmlIgnore()]
        public Series<double> InKillZone
        {
            get { return Values[2]; }
        }

        [Browsable(false)]
        [XmlIgnore()]
        public Series<double> ExhaustionActive
        {
            get { return Values[3]; }
        }

        [Browsable(false)] [XmlIgnore()] public Series<double> Level0 { get { return levelSeries[0]; } }
        [Browsable(false)] [XmlIgnore()] public Series<double> Level236 { get { return levelSeries[1]; } }
        [Browsable(false)] [XmlIgnore()] public Series<double> Level382 { get { return levelSeries[2]; } }
        [Browsable(false)] [XmlIgnore()] public Series<double> Level500 { get { return levelSeries[3]; } }
        [Browsable(false)] [XmlIgnore()] public Series<double> Level618 { get { return levelSeries[4]; } }
        [Browsable(false)] [XmlIgnore()] public Series<double> Level650 { get { return levelSeries[5]; } }
        [Browsable(false)] [XmlIgnore()] public Series<double> Level786 { get { return levelSeries[6]; } }
        [Browsable(false)] [XmlIgnore()] public Series<double> Level1000 { get { return levelSeries[7]; } }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private PraedoxFibEngine_v0_1_0[] cachePraedoxFibEngine_v0_1_0;
		public PraedoxFibEngine_v0_1_0 PraedoxFibEngine_v0_1_0(int fibLookback, int staleBarsThreshold, int killZoneTicks, int zoneDisplayBars, bool drawFibLines, bool drawGoldenZone, bool drawGoldenPocket, bool drawKillZones, bool drawExhaustionZone, bool drawFlipAlerts)
		{
			return PraedoxFibEngine_v0_1_0(Input, fibLookback, staleBarsThreshold, killZoneTicks, zoneDisplayBars, drawFibLines, drawGoldenZone, drawGoldenPocket, drawKillZones, drawExhaustionZone, drawFlipAlerts);
		}

		public PraedoxFibEngine_v0_1_0 PraedoxFibEngine_v0_1_0(ISeries<double> input, int fibLookback, int staleBarsThreshold, int killZoneTicks, int zoneDisplayBars, bool drawFibLines, bool drawGoldenZone, bool drawGoldenPocket, bool drawKillZones, bool drawExhaustionZone, bool drawFlipAlerts)
		{
			if (cachePraedoxFibEngine_v0_1_0 != null)
				for (int idx = 0; idx < cachePraedoxFibEngine_v0_1_0.Length; idx++)
					if (cachePraedoxFibEngine_v0_1_0[idx] != null && cachePraedoxFibEngine_v0_1_0[idx].FibLookback == fibLookback && cachePraedoxFibEngine_v0_1_0[idx].StaleBarsThreshold == staleBarsThreshold && cachePraedoxFibEngine_v0_1_0[idx].KillZoneTicks == killZoneTicks && cachePraedoxFibEngine_v0_1_0[idx].ZoneDisplayBars == zoneDisplayBars && cachePraedoxFibEngine_v0_1_0[idx].DrawFibLines == drawFibLines && cachePraedoxFibEngine_v0_1_0[idx].DrawGoldenZone == drawGoldenZone && cachePraedoxFibEngine_v0_1_0[idx].DrawGoldenPocket == drawGoldenPocket && cachePraedoxFibEngine_v0_1_0[idx].DrawKillZones == drawKillZones && cachePraedoxFibEngine_v0_1_0[idx].DrawExhaustionZone == drawExhaustionZone && cachePraedoxFibEngine_v0_1_0[idx].DrawFlipAlerts == drawFlipAlerts && cachePraedoxFibEngine_v0_1_0[idx].EqualsInput(input))
						return cachePraedoxFibEngine_v0_1_0[idx];
			return CacheIndicator<PraedoxFibEngine_v0_1_0>(new PraedoxFibEngine_v0_1_0(){ FibLookback = fibLookback, StaleBarsThreshold = staleBarsThreshold, KillZoneTicks = killZoneTicks, ZoneDisplayBars = zoneDisplayBars, DrawFibLines = drawFibLines, DrawGoldenZone = drawGoldenZone, DrawGoldenPocket = drawGoldenPocket, DrawKillZones = drawKillZones, DrawExhaustionZone = drawExhaustionZone, DrawFlipAlerts = drawFlipAlerts }, input, ref cachePraedoxFibEngine_v0_1_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.PraedoxFibEngine_v0_1_0 PraedoxFibEngine_v0_1_0(int fibLookback, int staleBarsThreshold, int killZoneTicks, int zoneDisplayBars, bool drawFibLines, bool drawGoldenZone, bool drawGoldenPocket, bool drawKillZones, bool drawExhaustionZone, bool drawFlipAlerts)
		{
			return indicator.PraedoxFibEngine_v0_1_0(Input, fibLookback, staleBarsThreshold, killZoneTicks, zoneDisplayBars, drawFibLines, drawGoldenZone, drawGoldenPocket, drawKillZones, drawExhaustionZone, drawFlipAlerts);
		}

		public Indicators.PraedoxFibEngine_v0_1_0 PraedoxFibEngine_v0_1_0(ISeries<double> input , int fibLookback, int staleBarsThreshold, int killZoneTicks, int zoneDisplayBars, bool drawFibLines, bool drawGoldenZone, bool drawGoldenPocket, bool drawKillZones, bool drawExhaustionZone, bool drawFlipAlerts)
		{
			return indicator.PraedoxFibEngine_v0_1_0(input, fibLookback, staleBarsThreshold, killZoneTicks, zoneDisplayBars, drawFibLines, drawGoldenZone, drawGoldenPocket, drawKillZones, drawExhaustionZone, drawFlipAlerts);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.PraedoxFibEngine_v0_1_0 PraedoxFibEngine_v0_1_0(int fibLookback, int staleBarsThreshold, int killZoneTicks, int zoneDisplayBars, bool drawFibLines, bool drawGoldenZone, bool drawGoldenPocket, bool drawKillZones, bool drawExhaustionZone, bool drawFlipAlerts)
		{
			return indicator.PraedoxFibEngine_v0_1_0(Input, fibLookback, staleBarsThreshold, killZoneTicks, zoneDisplayBars, drawFibLines, drawGoldenZone, drawGoldenPocket, drawKillZones, drawExhaustionZone, drawFlipAlerts);
		}

		public Indicators.PraedoxFibEngine_v0_1_0 PraedoxFibEngine_v0_1_0(ISeries<double> input , int fibLookback, int staleBarsThreshold, int killZoneTicks, int zoneDisplayBars, bool drawFibLines, bool drawGoldenZone, bool drawGoldenPocket, bool drawKillZones, bool drawExhaustionZone, bool drawFlipAlerts)
		{
			return indicator.PraedoxFibEngine_v0_1_0(input, fibLookback, staleBarsThreshold, killZoneTicks, zoneDisplayBars, drawFibLines, drawGoldenZone, drawGoldenPocket, drawKillZones, drawExhaustionZone, drawFlipAlerts);
		}
	}
}

#endregion
