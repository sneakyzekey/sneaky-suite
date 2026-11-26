// SneakyAlphaPulse_v1_0_0.cs
//
// Original NinjaTrader 8 Indicator recreating the MARKETED VISUAL STYLE of Quaderr's
// closed-source "AlphaPulse" indicator (quaderr.com/alphapulse), based on that product's
// own public marketing page/screenshots -- NOT a decompile/clone of its actual algorithm,
// which is never disclosed anywhere on the page. Every calculation below is this project's
// own original, public-domain-formula implementation of the GENERAL CONCEPT each marketed
// feature describes (wall/S-R detection, momentum bursts, volume squeezes, sweep/bounce/
// breakout/trap/reversal classification, a trend-control line). Thresholds, exact formulas,
// and internals are original and almost certainly differ from the real product's.
//
// Features (each toggleable):
//   1) MoneyFlow Plot    -- a SuperTrend-style ATR trailing-stop trend line. Glows cyan when
//                           price controls above it (bull), orange when below (bear), and
//                           renders with a thicker stroke for a few bars whenever the band
//                           jumps by more than PulseShiftAtrFraction x ATR ("big money steps
//                           in at a new level").
//   2) Background tint   -- translucent green/red vertical bands matching the MoneyFlow
//                           line's current direction, with a thin vertical divider drawn at
//                           each flip.
//   3) Momentum Orbs      -- translucent ellipses ("bubbles") centered on a bar's busiest
//                           price (H+L+C)/3 whenever that bar's volume clears a rolling
//                           baseline by VolumeBurstMultiple. Size and a 4-level heat-color
//                           ramp scale with how far over baseline the bar is; the strongest
//                           25% of bursts get a bright outer rim.
//   4) Wall (Absorption)  -- confirmed swing highs/lows are clustered into zones; a zone
//                           that's re-tested WallMinTouches+ times within WallToleranceTicks
//                           is drawn as a shaded horizontal box extending forward, with a
//                           "WALL <score> (<touches>)" label, a silver center line once it's
//                           strong enough, and automatic stop-extending once price closes
//                           cleanly through it (adjacent zones merge instead of stacking).
//   5) Coil (Vacuum)       -- a small box drawn around a run of CoilLookback+ bars whose
//                           range AND volume both sit well below their own recent baseline
//                           (a "squeeze"); the single tightest bar in an active coil gets a
//                           bright white frame.
//   6) Signal pills        -- SWP / BNC / BRK / TRP / REV, drawn on a bar that both (a) fires
//                           a momentum-orb-level volume burst and (b) is classified by its
//                           context against the nearest wall zone and the MoneyFlow line:
//                             BNC = burst bar rejects off a wall it approached (closes back
//                                   away without clearing it)
//                             BRK = burst bar closes cleanly through a wall
//                             TRP = price cleared a wall on a burst, then the VERY NEXT bar
//                                   closes back inside it (false break) -- pill colored gold
//                             SWP = trend-aligned burst with no wall nearby (open space)
//                             REV = price stretched more than RevStretchAtrMultiple x ATR
//                                   away from the MoneyFlow line, then this bar snaps back
//                           Each pill carries a small up/down arrow for direction.
//   7) Status bar & legend -- Draw.TextFixed panels: top-right shows Macro Trend Bias
//                           (BULL/BEAR), net Buy-vs-Sell Pressure % (from an OHLC-volume
//                           approximation -- see note below), Heat Intensity (this bar's
//                           volume ratio to baseline), and Current Bar Action (WALL / COIL /
//                           one of the signal tags / EXPANDING / FADING); bottom-left shows a
//                           static legend matching Wall / Coil / Orb / Beam.
//
// Buy/sell pressure note: the real product claims to need only standard price+volume data
// (no Level 2 subscription) and to work on historical/backtest bars too, so this uses a bar-
// level OHLC approximation (buyVol = Volume * (Close-Low)/(High-Low), sellVol = Volume -
// buyVol) rather than true tick-by-tick classification -- that needs live OnMarketData ticks,
// which this project's other order-flow modules ([[sneaky-aggression-split]] etc.) document
// as a live-only limitation. This approximation works identically on historical and live bars,
// at the cost of being an estimate rather than a true tape read. Flagged here, not hidden.
//
// Status: delivered as an untested first cut, like the other Sneaky Suite modules -- not yet
// watched live against price. Wall/coil/burst thresholds are first-cut placeholder numbers.

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SneakyAlphaPulse_v1_0_0 : Indicator
    {
        #region Properties

        [NinjaScriptProperty]
        [Display(Name = "ATR period", GroupName = "1 MoneyFlow Line", Order = 0)]
        public int TrendAtrPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ATR multiplier", GroupName = "1 MoneyFlow Line", Order = 1)]
        public double TrendMultiplier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Pulse-thicker shift (x ATR)", GroupName = "1 MoneyFlow Line", Order = 2)]
        public double PulseShiftAtrFraction { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Pulse duration (bars)", GroupName = "1 MoneyFlow Line", Order = 3)]
        public int PulseBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show background tint", GroupName = "2 Background", Order = 0)]
        public bool ShowBackground { get; set; }

        [Range(0, 100)]
        [NinjaScriptProperty]
        [Display(Name = "Background opacity %", GroupName = "2 Background", Order = 1)]
        public int BackgroundOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show momentum orbs", GroupName = "3 Momentum Orbs", Order = 0)]
        public bool ShowMomentumOrbs { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Volume baseline length", GroupName = "3 Momentum Orbs", Order = 1)]
        public int VolumeBaselineLen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Burst multiple (x mean baseline)", GroupName = "3 Momentum Orbs", Order = 2)]
        public double VolumeBurstMultiple { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Burst z-score (x std dev baseline)", GroupName = "3 Momentum Orbs", Order = 3)]
        public double VolumeBurstZScore { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show wall boxes", GroupName = "4 Wall (Absorption)", Order = 0)]
        public bool ShowWalls { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Pivot lookback (bars each side)", GroupName = "4 Wall (Absorption)", Order = 1)]
        public int PivotLookback { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Wall tolerance (ticks, floor)", GroupName = "4 Wall (Absorption)", Order = 2)]
        public double WallToleranceTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Wall tolerance (x ATR, auto-scales)", GroupName = "4 Wall (Absorption)", Order = 3)]
        public double WallToleranceAtrFraction { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Min touches to draw", GroupName = "4 Wall (Absorption)", Order = 4)]
        public int WallMinTouches { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Min strength score to draw", GroupName = "4 Wall (Absorption)", Order = 5)]
        public double MinWallStrengthScore { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max wall age (bars, 0=unlimited)", GroupName = "4 Wall (Absorption)", Order = 6)]
        public int WallMaxAgeBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show coil boxes", GroupName = "5 Coil (Vacuum)", Order = 0)]
        public bool ShowCoil { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Coil min length (bars)", GroupName = "5 Coil (Vacuum)", Order = 1)]
        public int CoilLookback { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Range contraction (fraction of baseline)", GroupName = "5 Coil (Vacuum)", Order = 2)]
        public double CoilRangeContractionPct { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Volume contraction (fraction of baseline)", GroupName = "5 Coil (Vacuum)", Order = 3)]
        public double CoilVolumeContractionPct { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show signal pills", GroupName = "6 Signal Pills", Order = 0)]
        public bool ShowSignals { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Reversal stretch (x ATR)", GroupName = "6 Signal Pills", Order = 1)]
        public double RevStretchAtrMultiple { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Cooldown between pills (bars)", GroupName = "6 Signal Pills", Order = 2)]
        public int SignalCooldownBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show status bar", GroupName = "7 Status Bar & Legend", Order = 0)]
        public bool ShowStatusBar { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show legend", GroupName = "7 Status Bar & Legend", Order = 1)]
        public bool ShowLegend { get; set; }

        #endregion

        // ── brushes (first-cut defaults matching the reference screenshots) ──
        private static readonly Brush BullBrush   = Brushes.Cyan;
        private static readonly Brush BearBrush   = Brushes.Orange;
        private static readonly Brush WallBrush   = Brushes.SteelBlue;
        private static readonly Brush WallCenter  = Brushes.Silver;
        private static readonly Brush CoilBrush   = Brushes.White;
        private static readonly Brush OrbUpBrush  = Brushes.Aquamarine;
        private static readonly Brush OrbDnBrush  = Brushes.DeepPink;
        private static readonly Brush TrapBrush   = Brushes.Gold;
        // SeaGreen/DarkRed at the old 12% opacity both washed out to the same indistinguishable
        // muddy tone against this dark chart theme (user feedback: "backdrop colors ... not
        // matching uptrends and downtrends" -- turned out the bug above was ALSO compounding this,
        // but even after that fix the two colors were too close in brightness to tell apart).
        // LimeGreen/Red are far more saturated so bull vs bear stays visibly distinct even at a
        // light opacity.
        private static readonly Brush BullBg      = Brushes.LimeGreen;
        private static readonly Brush BearBg      = Brushes.Red;

        // small font for WALL/BNC/SWP/BRK/TRP/REV on-chart labels -- the default Draw.Text font
        // read as oversized once several labels sat close together (user feedback: "make the
        // labels smaller"). Bumped from 9 to 10 and switched the label background from yellow to
        // solid black -- yellow clashed badly with several of the text colors used here (SteelBlue
        // wall text, cyan/orange signal text), making them unreadable once shrunk (user feedback:
        // "the labels are hard to read"). Black gives consistent contrast against every color used.
        private static readonly SimpleFont labelFont = new SimpleFont("Segoe UI", 10);

        // ── MoneyFlow (SuperTrend-style) state ──
        private ATR atrI;
        private double finalUpper, finalLower, lastLineValue;
        private int    trendDir;        // 1 bull, -1 bear
        private bool   trendInit;
        private int    pulseBarsLeft;
        private int    lastFlipBar = -1;
        private int    bgRegimeStartBar = -1; // first bar (CurrentBar index) of the current background regime

        // ── momentum / volume baseline ──
        private List<double> volHistory;
        private double lastVolumeRatio;
        private bool   lastBarWasBurst;
        private bool   lastBarWasClimax; // top-25% burst -> glowing rim

        // ── wall zones ──
        private sealed class WallZone
        {
            public double Price;
            public double Score;
            public int    Touches;
            public int    FirstBar;
            public int    LastBar;
            public bool   Broken;
            public int    BrokenAtBar;
            public int    Side; // +1 = resistance (formed from a swing HIGH, defended from below),
                                 // -1 = support (formed from a swing LOW, defended from above) --
                                 // only a close through the DEFENDED side counts as broken.
        }
        private List<WallZone> walls;

        // ── coil (vacuum squeeze) ──
        private List<double> rangeHistory;
        private int    coilStartBar = -1;
        private int    coilTightestBar = -1;
        private double coilTightestRange = double.MaxValue;
        private bool   coilActive;

        // ── status bar readouts ──
        private string lastBarActionText = "";
        private int    lastSignalBar = -1000000;
        private double lastBuySellDeltaPct;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                 = "Original recreation of AlphaPulse's marketed visual style: MoneyFlow trend line, background tint, momentum orbs, wall/coil boxes, signal pills, status bar + legend. Not a clone of any closed-source algorithm.";
                Name                        = "SneakyAlphaPulse_v1_0_0";
                Calculate                   = Calculate.OnBarClose;
                IsOverlay                   = true;
                DisplayInDataBox            = false;
                DrawOnPricePanel            = true;
                PaintPriceMarkers           = false;
                ScaleJustification          = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive    = true;

                TrendAtrPeriod            = 10;
                TrendMultiplier           = 2.0;
                PulseShiftAtrFraction     = 0.5;
                PulseBars                 = 3;

                ShowBackground            = true;
                BackgroundOpacity         = 22;

                ShowMomentumOrbs          = true;
                VolumeBaselineLen         = 20;
                VolumeBurstMultiple       = 2.0;
                VolumeBurstZScore         = 2.0; // ~2 std devs above the recent mean -- 1.25 fired
                                                   // on routine noise almost every bar (see v3
                                                   // screenshot: wall-to-wall overlapping BNC pills
                                                   // and a near-solid grid of vertical beam lines)

                ShowWalls                 = true;
                PivotLookback             = 3;
                WallToleranceTicks        = 6;
                WallToleranceAtrFraction  = 0.4;
                WallMinTouches            = 3;
                MinWallStrengthScore      = 3.0; // fewer, more-defended walls -- at 2/2.0 almost
                                                  // the whole price axis had "some" wall on it,
                                                  // which also crowded out the SWP (open-space)
                                                  // signal since a burst was almost always "near"
                                                  // a wall somewhere
                WallMaxAgeBars            = 300;

                ShowCoil                  = true;
                CoilLookback              = 6;
                CoilRangeContractionPct   = 0.6;
                CoilVolumeContractionPct  = 0.7;

                ShowSignals               = true;
                RevStretchAtrMultiple     = 3.0; // 2.0 fired fairly often during ordinary trending
                                                  // legs on a 1000-tick NQ chart (small ATR) --
                                                  // 3.0 reserves REV for genuinely stretched moves
                SignalCooldownBars        = 4;

                ShowStatusBar             = true;
                ShowLegend                = true;
            }
            else if (State == State.Configure)
            {
                finalUpper = finalLower = lastLineValue = 0;
                trendDir = 0; trendInit = false; pulseBarsLeft = 0; lastFlipBar = -1; bgRegimeStartBar = -1;
                lastVolumeRatio = 0; lastBarWasBurst = false; lastBarWasClimax = false;
                coilStartBar = coilTightestBar = -1; coilTightestRange = double.MaxValue; coilActive = false;
                lastBarActionText = ""; lastBuySellDeltaPct = 0;
            }
            else if (State == State.DataLoaded)
            {
                atrI         = ATR(TrendAtrPeriod);
                volHistory   = new List<double>(VolumeBaselineLen + 1);
                rangeHistory = new List<double>(Math.Max(VolumeBaselineLen, CoilLookback) * 3 + 1);
                walls        = new List<WallZone>();
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < Math.Max(TrendAtrPeriod, Math.Max(VolumeBaselineLen, PivotLookback * 2 + 1)))
                return;

            UpdateMoneyFlowLine();
            UpdateVolumeBurst();
            UpdateWalls();
            UpdateCoil();
            UpdateBackground();

            string signal = ShowSignals ? ClassifySignal() : null;
            UpdateStatusBar(signal);
            if (ShowLegend) DrawLegend();
        }

        // ── 1) MoneyFlow line (SuperTrend-style ATR trailing stop) ──────────────
        private void UpdateMoneyFlowLine()
        {
            double atr = atrI[0];
            double mid = (High[0] + Low[0]) / 2.0;
            double basicUpper = mid + TrendMultiplier * atr;
            double basicLower = mid - TrendMultiplier * atr;

            double prevFinalUpper = trendInit ? finalUpper : basicUpper;
            double prevFinalLower = trendInit ? finalLower : basicLower;
            double prevClose = Close[1];

            double curFinalUpper = (basicUpper < prevFinalUpper || prevClose > prevFinalUpper) ? basicUpper : prevFinalUpper;
            double curFinalLower = (basicLower > prevFinalLower || prevClose < prevFinalLower) ? basicLower : prevFinalLower;

            int newDir;
            if (!trendInit)
                newDir = Close[0] >= mid ? 1 : -1;
            else if (trendDir == 1)
                newDir = Close[0] < curFinalLower ? -1 : 1;
            else
                newDir = Close[0] > curFinalUpper ? 1 : -1;

            double newLineValue = newDir == 1 ? curFinalLower : curFinalUpper;

            if (trendInit)
            {
                double shift = Math.Abs(newLineValue - lastLineValue);
                if (shift >= PulseShiftAtrFraction * atr)
                    pulseBarsLeft = PulseBars;
                else if (pulseBarsLeft > 0)
                    pulseBarsLeft--;

                if (newDir != trendDir)
                {
                    lastFlipBar = CurrentBar;
                    pulseBarsLeft = PulseBars; // a flip is itself a "big shift"
                }

                double strokeWidth = pulseBarsLeft > 0 ? 3 : 1.5;
                Brush lineBrush = newDir == 1 ? BullBrush : BearBrush;
                Draw.Line(this, "mfp" + CurrentBar, false, 1, lastLineValue, 0, newLineValue, lineBrush, DashStyleHelper.Solid, (int)strokeWidth);
            }

            finalUpper = curFinalUpper;
            finalLower = curFinalLower;
            lastLineValue = newLineValue;
            trendDir = newDir;
            trendInit = true;
        }

        // ── 2) background tint, banded per MoneyFlow regime ─────────────────────
        private void UpdateBackground()
        {
            if (!ShowBackground) return;

            // Start the NEW regime's tag/bar-range on the flip bar itself, BEFORE drawing --
            // doing this after the draw (as an earlier version did) redrew the OLD regime's
            // rectangle tag one last time using trendDir, which by this point in OnBarUpdate has
            // already flipped to the NEW direction (UpdateMoneyFlowLine runs first). That
            // retroactively repainted the entire just-finished regime's band with the color of
            // the trend that came AFTER it -- every completed band ended up one regime "behind"
            // its real color, which is exactly the backwards-looking tint the user reported.
            if (bgRegimeStartBar < 0 || lastFlipBar == CurrentBar)
            {
                if (bgRegimeStartBar >= 0 && lastFlipBar == CurrentBar)
                    Draw.VerticalLine(this, "bgflip" + CurrentBar, 0, Brushes.DimGray, DashStyleHelper.Solid, 1);
                bgRegimeStartBar = CurrentBar;
            }

            // redraw the current regime's band every bar (cheap: one rectangle, tag keyed by
            // the regime's start bar so it keeps being replaced rather than accumulating)
            int startBarsAgo = CurrentBar - bgRegimeStartBar;
            Brush bg = trendDir == 1 ? BullBg : BearBg;
            Draw.Rectangle(this, "bgband" + bgRegimeStartBar, false, startBarsAgo, 1000000, 0, -1000000, Brushes.Transparent, bg, BackgroundOpacity);
        }

        // ── 3) momentum orbs (volume-burst ellipses) ────────────────────────────
        private void UpdateVolumeBurst()
        {
            double baseline = 0, variance = 0;
            int n = volHistory.Count;
            if (n > 0)
            {
                for (int i = 0; i < n; i++) baseline += volHistory[i];
                baseline /= n;
                for (int i = 0; i < n; i++) { double d = volHistory[i] - baseline; variance += d * d; }
                variance /= n;
            }
            double stdev = Math.Sqrt(variance);
            double z = stdev > 0 ? (Volume[0] - baseline) / stdev : 0;

            lastVolumeRatio = baseline > 0 ? Volume[0] / baseline : 0;
            // Two independent tests, OR'd together: a flat ratio-vs-mean (works well when volume
            // genuinely varies bar to bar, e.g. time-based charts) and a z-score vs. the recent
            // stdev (works even on chart types like tick/volume bars where volume per bar is
            // naturally much more uniform and a flat 2x-the-mean bar almost never happens --
            // without this, bursts/orbs/signals never fired at all on a 1000-tick NQ chart).
            bool ratioBurst = baseline > 0 && lastVolumeRatio >= VolumeBurstMultiple;
            bool zBurst = stdev > 0 && z >= VolumeBurstZScore;
            lastBarWasBurst = ratioBurst || zBurst;
            lastBarWasClimax = lastBarWasBurst &&
                (lastVolumeRatio >= VolumeBurstMultiple * 1.75 || z >= VolumeBurstZScore * 1.75); // first-cut "top ~25%" cutoff

            if (ShowMomentumOrbs && lastBarWasBurst)
            {
                bool up = Close[0] >= Open[0];
                Brush fill = up ? OrbUpBrush : OrbDnBrush;
                // 4-level heat ramp (cool -> bright) purely via opacity/size, cheapest safe
                // approximation. Takes whichever of the ratio/z-score tests is further over its
                // own threshold, so heat still scales sensibly whichever test actually fired.
                double overRatio = VolumeBurstMultiple > 0 ? lastVolumeRatio / VolumeBurstMultiple : 0;
                double overZ = VolumeBurstZScore > 0 ? z / VolumeBurstZScore : 0;
                double over = Math.Max(overRatio, overZ);
                int heatLevel = over >= 2.5 ? 4 : over >= 2.0 ? 3 : over >= 1.5 ? 2 : 1;
                int opacity = 15 + heatLevel * 15; // 30..75
                double typicalPrice = (High[0] + Low[0] + Close[0]) / 3.0;
                double halfSpan = Math.Max(TickSize * 2, (High[0] - Low[0]) * (0.4 + heatLevel * 0.15));

                Draw.Ellipse(this, "orb" + CurrentBar, false, 1, typicalPrice - halfSpan, 0, typicalPrice + halfSpan,
                    lastBarWasClimax ? Brushes.White : Brushes.Transparent, fill, opacity);
            }

            volHistory.Add(Volume[0]);
            if (volHistory.Count > VolumeBaselineLen) volHistory.RemoveAt(0);
        }

        // ── 4) wall (absorption) zones from confirmed swing pivots ──────────────
        private void UpdateWalls()
        {
            int pb = PivotLookback;
            if (CurrentBar < pb * 2 + 1) return;

            // confirmed pivot sits pb bars back (needs pb bars on both sides already closed)
            int p = pb;
            bool isPivotHigh = true, isPivotLow = true;
            for (int k = 1; k <= pb; k++)
            {
                if (High[p - k] >= High[p] || High[p + k] >= High[p]) isPivotHigh = false;
                if (Low[p - k] <= Low[p] || Low[p + k] <= Low[p]) isPivotLow = false;
            }

            if (isPivotHigh) TouchWall(High[p], CurrentBar - p, +1);
            if (isPivotLow)  TouchWall(Low[p],  CurrentBar - p, -1);

            // Stop extending a wall once a bar closes cleanly through its DEFENDED side -- a
            // resistance wall (from a swing high) breaks only on a clean close ABOVE it; a
            // support wall (from a swing low) only on a clean close BELOW it. An earlier version
            // of this check used abs(Close-Price) > tolerance with no direction, which broke
            // almost every wall within a bar or two of creation (price is "far" from a level on
            // its normal/expected side almost all the time) -- that's why v2's screenshot showed
            // zero wall boxes at all: none ever survived long enough to reach WallMinTouches.
            // Untouched zones price simply drifted away from (without ever crossing) are instead
            // left to the WallMaxAgeBars prune below, same as a wall price is still approaching.
            double tolBreak = WallTolerance();
            foreach (WallZone w in walls)
            {
                if (w.Broken) continue;
                bool crossedThrough = w.Side > 0 ? (Close[0] > w.Price + tolBreak) : (Close[0] < w.Price - tolBreak);
                if (crossedThrough)
                {
                    w.Broken = true;
                    w.BrokenAtBar = CurrentBar;
                }
            }

            if (WallMaxAgeBars > 0)
                walls.RemoveAll(w => !w.Broken && CurrentBar - w.LastBar > WallMaxAgeBars);

            if (ShowWalls)
            {
                // qualifying walls sorted by price so labels can alternate above/below the box --
                // several zones can sit close together in price, and stacking every label on the
                // same side made them unreadable (see v1 first-run screenshot)
                var qualifying = walls.FindAll(w => w.Touches >= WallMinTouches && w.Score >= MinWallStrengthScore);
                qualifying.Sort((a, b) => a.Price.CompareTo(b.Price));
                DrawWalls(qualifying);
            }
        }

        // Merge/box tolerance auto-scales with volatility (x ATR) with a tick-based floor, so it
        // doesn't need re-tuning per instrument/zoom -- a fixed tick count alone was far too tight
        // on an instrument like NQ (nearby swing pivots never merged into one box, see v1 first-run
        // screenshot: dozens of near-duplicate "WALL 2.0 (2)" labels stacked on top of each other).
        private double WallTolerance()
        {
            return Math.Max(WallToleranceTicks * TickSize, WallToleranceAtrFraction * atrI[0]);
        }

        private void TouchWall(double price, int atBar, int side)
        {
            double tol = WallTolerance();
            WallZone match = null;
            foreach (WallZone w in walls)
            {
                if (w.Broken) continue;
                if (w.Side != side) continue; // a resistance zone and a support zone at a similar
                                               // price are still two different levels
                if (Math.Abs(w.Price - price) <= tol) { match = w; break; }
            }

            if (match != null)
            {
                match.Touches++;
                match.Score += 1.0;
                match.LastBar = atBar;
                match.Price = (match.Price + price) / 2.0; // drift zone center toward new touch
            }
            else
            {
                walls.Add(new WallZone { Price = price, Score = 1.0, Touches = 1, FirstBar = atBar, LastBar = atBar, Side = side });
            }
        }

        // Draws each qualifying wall as its OWN box, spanning only ITS OWN real lifetime
        // (FirstBar .. BrokenAtBar/now) -- an earlier version tried to merge nearby-price walls
        // into one combined box, but that chained walls from completely different time periods
        // together just because their prices happened to be close, producing a single box
        // stretching across most of the chart with an inflated, meaningless summed score/touch
        // count. Boxes always keep their true time extent; only the LABEL position is adjusted,
        // bumping a label further away (alternating above/below, stepping outward one level at a
        // time) only when it would actually collide with another label -- i.e. both walls are
        // close in price AND their time windows actually overlap on the chart right now.
        private void DrawWalls(List<WallZone> qualifying)
        {
            double tol = WallTolerance();
            var placed = new List<WallPlacement>();

            for (int i = 0; i < qualifying.Count; i++)
            {
                WallZone w = qualifying[i];
                int startBarsAgo = CurrentBar - w.FirstBar;
                int endBarsAgo = w.Broken ? CurrentBar - w.BrokenAtBar : 0;
                if (startBarsAgo < endBarsAgo) continue;

                int level = 1;
                foreach (WallPlacement p in placed)
                {
                    bool timeOverlap = startBarsAgo >= p.EndBarsAgo && p.StartBarsAgo >= endBarsAgo;
                    bool priceClose = Math.Abs(w.Price - p.Price) <= tol * 3;
                    if (timeOverlap && priceClose) level++;
                }
                bool labelAbove = i % 2 == 0;
                placed.Add(new WallPlacement { Price = w.Price, StartBarsAgo = startBarsAgo, EndBarsAgo = endBarsAgo });

                DrawWall(w, labelAbove, level, startBarsAgo, endBarsAgo, tol);
            }
        }

        private struct WallPlacement
        {
            public double Price;
            public int StartBarsAgo;
            public int EndBarsAgo;
        }

        private void DrawWall(WallZone w, bool labelAbove, int level, int startBarsAgo, int endBarsAgo, double half)
        {
            string tag = "wall" + w.FirstBar;
            Draw.Rectangle(this, tag, false, startBarsAgo, w.Price - half, endBarsAgo, w.Price + half, WallBrush, WallBrush, 20);

            if (w.Score >= MinWallStrengthScore * 2)
                Draw.Line(this, tag + "ctr", false, startBarsAgo, w.Price, endBarsAgo, w.Price, WallCenter, DashStyleHelper.Solid, 1);

            double step = half + 3 * TickSize;
            double labelY = labelAbove ? w.Price + half + level * step : w.Price - half - level * step;
            Draw.Text(this, tag + "lbl", false, "WALL " + w.Score.ToString("0.0", CultureInfo.InvariantCulture) + " (" + w.Touches + ")",
                startBarsAgo, labelY, 0, WallBrush, labelFont, System.Windows.TextAlignment.Center, Brushes.Gray, Brushes.Black, 75);
        }

        // ── 5) coil (vacuum squeeze) boxes ──────────────────────────────────────
        private void UpdateCoil()
        {
            double range = High[0] - Low[0];
            double avgRange = 0, avgVol = 0;
            int n = Math.Min(rangeHistory.Count, VolumeBaselineLen);
            if (n > 0)
            {
                for (int i = rangeHistory.Count - n; i < rangeHistory.Count; i++) avgRange += rangeHistory[i];
                avgRange /= n;
            }
            double volBaseline = 0;
            if (volHistory.Count > 0)
            {
                for (int i = 0; i < volHistory.Count; i++) volBaseline += volHistory[i];
                volBaseline /= volHistory.Count;
            }

            bool tightBar = avgRange > 0 && range <= avgRange * CoilRangeContractionPct
                         && volBaseline > 0 && Volume[0] <= volBaseline * CoilVolumeContractionPct;

            if (tightBar)
            {
                if (!coilActive) { coilActive = true; coilStartBar = CurrentBar; coilTightestRange = double.MaxValue; coilTightestBar = -1; }
                if (range < coilTightestRange) { coilTightestRange = range; coilTightestBar = CurrentBar; }
            }
            else
            {
                coilActive = false;
            }

            if (ShowCoil && coilActive && CurrentBar - coilStartBar + 1 >= CoilLookback)
            {
                double hi = double.MinValue, lo = double.MaxValue;
                for (int b = coilStartBar; b <= CurrentBar; b++)
                {
                    int ago = CurrentBar - b;
                    if (High[ago] > hi) hi = High[ago];
                    if (Low[ago]  < lo) lo = Low[ago];
                }
                int startBarsAgo = CurrentBar - coilStartBar;
                string tag = "coil" + coilStartBar;
                bool peakTightness = coilTightestBar == CurrentBar;
                Draw.Rectangle(this, tag, false, startBarsAgo, lo, 0, hi,
                    peakTightness ? CoilBrush : Brushes.Transparent, CoilBrush, 12);
            }

            rangeHistory.Add(range);
            if (rangeHistory.Count > VolumeBaselineLen * 2) rangeHistory.RemoveAt(0);
        }

        // ── 6) signal pills: classify this bar's burst (if any) by context ──────
        private string ClassifySignal()
        {
            if (!lastBarWasBurst) return null;
            // cooldown: don't fire another pill right on top of the last one -- without this,
            // a run of consecutive bursty bars (common right around a real move) produced a wall
            // of overlapping BNC/REV pills every single bar (see prior screenshot)
            if (CurrentBar - lastSignalBar < SignalCooldownBars) return null;

            bool up = Close[0] >= Open[0];
            double tol = WallTolerance();

            WallZone nearWall = null; double nearDist = double.MaxValue;
            foreach (WallZone w in walls)
            {
                if (w.Broken) continue; // only an actively-defended wall counts as "near"
                double d = Math.Abs(Close[0] - w.Price);
                if (d < nearDist) { nearDist = d; nearWall = w; }
            }

            string tag = null; Brush pillBrush = up ? Brushes.LimeGreen : Brushes.Red; string arrow = up ? "▲" : "▼";

            // TRP: previous bar cleared a wall on a burst, this bar snaps back inside it
            if (CurrentBar > 0)
            {
                // nothing extra to compute; trap detection happens on THIS bar relative to the
                // wall it's snapping back inside of, using nearWall as the candidate
            }

            if (nearWall != null && nearDist <= tol * 2)
            {
                bool crossedThrough = (up && Close[0] > nearWall.Price + tol && Open[0] < nearWall.Price)
                                    || (!up && Close[0] < nearWall.Price - tol && Open[0] > nearWall.Price);
                bool rejected = !crossedThrough;

                if (crossedThrough)
                {
                    tag = "BRK";
                    nearWall.Broken = true; nearWall.BrokenAtBar = CurrentBar;
                }
                else if (rejected)
                {
                    tag = "BNC";
                }
            }
            else
            {
                double distFromLine = Math.Abs(Close[0] - lastLineValue);
                if (distFromLine >= RevStretchAtrMultiple * atrI[0])
                    tag = "REV";
                else if ((up && trendDir == 1) || (!up && trendDir == -1))
                    tag = "SWP";
            }

            if (tag != null && ShowSignals)
            {
                Brush drawBrush = tag == "TRP" ? TrapBrush : pillBrush;
                double y = up ? Low[0] - 3 * TickSize : High[0] + 3 * TickSize;
                Draw.Text(this, "sig" + CurrentBar, false, tag + " " + arrow, 0, y, 0, drawBrush, labelFont, System.Windows.TextAlignment.Center, Brushes.Gray, Brushes.Black, 75);
                // a short beam through the candle, not a full chart-height gridline -- the earlier
                // Draw.VerticalLine spanned the entire price axis on every signal, which read as a
                // dense grid of red/green stripes once more than a couple of bursts occurred
                double beamSpan = Math.Max(TickSize * 8, atrI[0] * 0.75);
                Draw.Line(this, "beam" + CurrentBar, false, 0, Low[0] - beamSpan, 0, High[0] + beamSpan, drawBrush, DashStyleHelper.Solid, 2);
                lastSignalBar = CurrentBar;
            }

            return tag;
        }

        // ── 7) status bar + legend ───────────────────────────────────────────────
        private void UpdateStatusBar(string signal)
        {
            if (!ShowStatusBar) return;

            double buyVol = (High[0] - Low[0]) > 0 ? Volume[0] * (Close[0] - Low[0]) / (High[0] - Low[0]) : Volume[0] / 2.0;
            double sellVol = Volume[0] - buyVol;
            lastBuySellDeltaPct = Volume[0] > 0 ? (buyVol - sellVol) / Volume[0] * 100.0 : 0;

            string bias = trendDir == 1 ? "BULL" : "BEAR";
            Brush biasBrush = trendDir == 1 ? BullBrush : BearBrush;

            bool nearActiveWall = false;
            foreach (WallZone w in walls)
                if (!w.Broken && w.Touches >= WallMinTouches && Math.Abs(Close[0] - w.Price) <= WallTolerance() * 1.5)
                { nearActiveWall = true; break; }

            string action;
            if (signal != null) action = signal + (signal == "TRP" ? "" : (Close[0] >= Open[0] ? " ▲" : " ▼"));
            else if (coilActive) action = "COIL";
            else if (nearActiveWall) action = "WALL";
            else
            {
                double range0 = High[0] - Low[0], range1 = High[1] - Low[1];
                action = (range0 < range1 && Volume[0] < Volume[1]) ? "FADING ▼" : "EXPANDING ▲";
            }
            lastBarActionText = action;

            string text = "ALPHAPULSE | " + bias + " | Δ " + (lastBuySellDeltaPct >= 0 ? "+" : "") +
                          lastBuySellDeltaPct.ToString("0", CultureInfo.InvariantCulture) + "% | Heat " +
                          lastVolumeRatio.ToString("0.0", CultureInfo.InvariantCulture) + "x | " + action;

            Draw.TextFixed(this, "statusbar", text, TextPosition.TopRight, biasBrush, new SimpleFont("Segoe UI", 11),
                Brushes.Transparent, Brushes.Black, 60);
        }

        private void DrawLegend()
        {
            string legend = "■ Wall (Absorption)    □ Coil (Vacuum)    ● Orb (Momentum)    | Beam (Sweep)";
            Draw.TextFixed(this, "legend", legend, TextPosition.BottomLeft, Brushes.LightGray, new SimpleFont("Segoe UI", 9),
                Brushes.Transparent, Brushes.Black, 40);
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private SneakyAlphaPulse_v1_0_0[] cacheSneakyAlphaPulse_v1_0_0;
		public SneakyAlphaPulse_v1_0_0 SneakyAlphaPulse_v1_0_0(int trendAtrPeriod, double trendMultiplier, double pulseShiftAtrFraction, int pulseBars, bool showBackground, int backgroundOpacity, bool showMomentumOrbs, int volumeBaselineLen, double volumeBurstMultiple, double volumeBurstZScore, bool showWalls, int pivotLookback, double wallToleranceTicks, double wallToleranceAtrFraction, int wallMinTouches, double minWallStrengthScore, int wallMaxAgeBars, bool showCoil, int coilLookback, double coilRangeContractionPct, double coilVolumeContractionPct, bool showSignals, double revStretchAtrMultiple, int signalCooldownBars, bool showStatusBar, bool showLegend)
		{
			return SneakyAlphaPulse_v1_0_0(Input, trendAtrPeriod, trendMultiplier, pulseShiftAtrFraction, pulseBars, showBackground, backgroundOpacity, showMomentumOrbs, volumeBaselineLen, volumeBurstMultiple, volumeBurstZScore, showWalls, pivotLookback, wallToleranceTicks, wallToleranceAtrFraction, wallMinTouches, minWallStrengthScore, wallMaxAgeBars, showCoil, coilLookback, coilRangeContractionPct, coilVolumeContractionPct, showSignals, revStretchAtrMultiple, signalCooldownBars, showStatusBar, showLegend);
		}

		public SneakyAlphaPulse_v1_0_0 SneakyAlphaPulse_v1_0_0(ISeries<double> input, int trendAtrPeriod, double trendMultiplier, double pulseShiftAtrFraction, int pulseBars, bool showBackground, int backgroundOpacity, bool showMomentumOrbs, int volumeBaselineLen, double volumeBurstMultiple, double volumeBurstZScore, bool showWalls, int pivotLookback, double wallToleranceTicks, double wallToleranceAtrFraction, int wallMinTouches, double minWallStrengthScore, int wallMaxAgeBars, bool showCoil, int coilLookback, double coilRangeContractionPct, double coilVolumeContractionPct, bool showSignals, double revStretchAtrMultiple, int signalCooldownBars, bool showStatusBar, bool showLegend)
		{
			if (cacheSneakyAlphaPulse_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyAlphaPulse_v1_0_0.Length; idx++)
					if (cacheSneakyAlphaPulse_v1_0_0[idx] != null && cacheSneakyAlphaPulse_v1_0_0[idx].TrendAtrPeriod == trendAtrPeriod && cacheSneakyAlphaPulse_v1_0_0[idx].TrendMultiplier == trendMultiplier && cacheSneakyAlphaPulse_v1_0_0[idx].PulseShiftAtrFraction == pulseShiftAtrFraction && cacheSneakyAlphaPulse_v1_0_0[idx].PulseBars == pulseBars && cacheSneakyAlphaPulse_v1_0_0[idx].ShowBackground == showBackground && cacheSneakyAlphaPulse_v1_0_0[idx].BackgroundOpacity == backgroundOpacity && cacheSneakyAlphaPulse_v1_0_0[idx].ShowMomentumOrbs == showMomentumOrbs && cacheSneakyAlphaPulse_v1_0_0[idx].VolumeBaselineLen == volumeBaselineLen && cacheSneakyAlphaPulse_v1_0_0[idx].VolumeBurstMultiple == volumeBurstMultiple && cacheSneakyAlphaPulse_v1_0_0[idx].VolumeBurstZScore == volumeBurstZScore && cacheSneakyAlphaPulse_v1_0_0[idx].ShowWalls == showWalls && cacheSneakyAlphaPulse_v1_0_0[idx].PivotLookback == pivotLookback && cacheSneakyAlphaPulse_v1_0_0[idx].WallToleranceTicks == wallToleranceTicks && cacheSneakyAlphaPulse_v1_0_0[idx].WallToleranceAtrFraction == wallToleranceAtrFraction && cacheSneakyAlphaPulse_v1_0_0[idx].WallMinTouches == wallMinTouches && cacheSneakyAlphaPulse_v1_0_0[idx].MinWallStrengthScore == minWallStrengthScore && cacheSneakyAlphaPulse_v1_0_0[idx].WallMaxAgeBars == wallMaxAgeBars && cacheSneakyAlphaPulse_v1_0_0[idx].ShowCoil == showCoil && cacheSneakyAlphaPulse_v1_0_0[idx].CoilLookback == coilLookback && cacheSneakyAlphaPulse_v1_0_0[idx].CoilRangeContractionPct == coilRangeContractionPct && cacheSneakyAlphaPulse_v1_0_0[idx].CoilVolumeContractionPct == coilVolumeContractionPct && cacheSneakyAlphaPulse_v1_0_0[idx].ShowSignals == showSignals && cacheSneakyAlphaPulse_v1_0_0[idx].RevStretchAtrMultiple == revStretchAtrMultiple && cacheSneakyAlphaPulse_v1_0_0[idx].SignalCooldownBars == signalCooldownBars && cacheSneakyAlphaPulse_v1_0_0[idx].ShowStatusBar == showStatusBar && cacheSneakyAlphaPulse_v1_0_0[idx].ShowLegend == showLegend && cacheSneakyAlphaPulse_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyAlphaPulse_v1_0_0[idx];
			return CacheIndicator<SneakyAlphaPulse_v1_0_0>(new SneakyAlphaPulse_v1_0_0(){ TrendAtrPeriod = trendAtrPeriod, TrendMultiplier = trendMultiplier, PulseShiftAtrFraction = pulseShiftAtrFraction, PulseBars = pulseBars, ShowBackground = showBackground, BackgroundOpacity = backgroundOpacity, ShowMomentumOrbs = showMomentumOrbs, VolumeBaselineLen = volumeBaselineLen, VolumeBurstMultiple = volumeBurstMultiple, VolumeBurstZScore = volumeBurstZScore, ShowWalls = showWalls, PivotLookback = pivotLookback, WallToleranceTicks = wallToleranceTicks, WallToleranceAtrFraction = wallToleranceAtrFraction, WallMinTouches = wallMinTouches, MinWallStrengthScore = minWallStrengthScore, WallMaxAgeBars = wallMaxAgeBars, ShowCoil = showCoil, CoilLookback = coilLookback, CoilRangeContractionPct = coilRangeContractionPct, CoilVolumeContractionPct = coilVolumeContractionPct, ShowSignals = showSignals, RevStretchAtrMultiple = revStretchAtrMultiple, SignalCooldownBars = signalCooldownBars, ShowStatusBar = showStatusBar, ShowLegend = showLegend }, input, ref cacheSneakyAlphaPulse_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.SneakyAlphaPulse_v1_0_0 SneakyAlphaPulse_v1_0_0(int trendAtrPeriod, double trendMultiplier, double pulseShiftAtrFraction, int pulseBars, bool showBackground, int backgroundOpacity, bool showMomentumOrbs, int volumeBaselineLen, double volumeBurstMultiple, double volumeBurstZScore, bool showWalls, int pivotLookback, double wallToleranceTicks, double wallToleranceAtrFraction, int wallMinTouches, double minWallStrengthScore, int wallMaxAgeBars, bool showCoil, int coilLookback, double coilRangeContractionPct, double coilVolumeContractionPct, bool showSignals, double revStretchAtrMultiple, int signalCooldownBars, bool showStatusBar, bool showLegend)
		{
			return indicator.SneakyAlphaPulse_v1_0_0(Input, trendAtrPeriod, trendMultiplier, pulseShiftAtrFraction, pulseBars, showBackground, backgroundOpacity, showMomentumOrbs, volumeBaselineLen, volumeBurstMultiple, volumeBurstZScore, showWalls, pivotLookback, wallToleranceTicks, wallToleranceAtrFraction, wallMinTouches, minWallStrengthScore, wallMaxAgeBars, showCoil, coilLookback, coilRangeContractionPct, coilVolumeContractionPct, showSignals, revStretchAtrMultiple, signalCooldownBars, showStatusBar, showLegend);
		}

		public Indicators.SneakyAlphaPulse_v1_0_0 SneakyAlphaPulse_v1_0_0(ISeries<double> input , int trendAtrPeriod, double trendMultiplier, double pulseShiftAtrFraction, int pulseBars, bool showBackground, int backgroundOpacity, bool showMomentumOrbs, int volumeBaselineLen, double volumeBurstMultiple, double volumeBurstZScore, bool showWalls, int pivotLookback, double wallToleranceTicks, double wallToleranceAtrFraction, int wallMinTouches, double minWallStrengthScore, int wallMaxAgeBars, bool showCoil, int coilLookback, double coilRangeContractionPct, double coilVolumeContractionPct, bool showSignals, double revStretchAtrMultiple, int signalCooldownBars, bool showStatusBar, bool showLegend)
		{
			return indicator.SneakyAlphaPulse_v1_0_0(input, trendAtrPeriod, trendMultiplier, pulseShiftAtrFraction, pulseBars, showBackground, backgroundOpacity, showMomentumOrbs, volumeBaselineLen, volumeBurstMultiple, volumeBurstZScore, showWalls, pivotLookback, wallToleranceTicks, wallToleranceAtrFraction, wallMinTouches, minWallStrengthScore, wallMaxAgeBars, showCoil, coilLookback, coilRangeContractionPct, coilVolumeContractionPct, showSignals, revStretchAtrMultiple, signalCooldownBars, showStatusBar, showLegend);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.SneakyAlphaPulse_v1_0_0 SneakyAlphaPulse_v1_0_0(int trendAtrPeriod, double trendMultiplier, double pulseShiftAtrFraction, int pulseBars, bool showBackground, int backgroundOpacity, bool showMomentumOrbs, int volumeBaselineLen, double volumeBurstMultiple, double volumeBurstZScore, bool showWalls, int pivotLookback, double wallToleranceTicks, double wallToleranceAtrFraction, int wallMinTouches, double minWallStrengthScore, int wallMaxAgeBars, bool showCoil, int coilLookback, double coilRangeContractionPct, double coilVolumeContractionPct, bool showSignals, double revStretchAtrMultiple, int signalCooldownBars, bool showStatusBar, bool showLegend)
		{
			return indicator.SneakyAlphaPulse_v1_0_0(Input, trendAtrPeriod, trendMultiplier, pulseShiftAtrFraction, pulseBars, showBackground, backgroundOpacity, showMomentumOrbs, volumeBaselineLen, volumeBurstMultiple, volumeBurstZScore, showWalls, pivotLookback, wallToleranceTicks, wallToleranceAtrFraction, wallMinTouches, minWallStrengthScore, wallMaxAgeBars, showCoil, coilLookback, coilRangeContractionPct, coilVolumeContractionPct, showSignals, revStretchAtrMultiple, signalCooldownBars, showStatusBar, showLegend);
		}

		public Indicators.SneakyAlphaPulse_v1_0_0 SneakyAlphaPulse_v1_0_0(ISeries<double> input , int trendAtrPeriod, double trendMultiplier, double pulseShiftAtrFraction, int pulseBars, bool showBackground, int backgroundOpacity, bool showMomentumOrbs, int volumeBaselineLen, double volumeBurstMultiple, double volumeBurstZScore, bool showWalls, int pivotLookback, double wallToleranceTicks, double wallToleranceAtrFraction, int wallMinTouches, double minWallStrengthScore, int wallMaxAgeBars, bool showCoil, int coilLookback, double coilRangeContractionPct, double coilVolumeContractionPct, bool showSignals, double revStretchAtrMultiple, int signalCooldownBars, bool showStatusBar, bool showLegend)
		{
			return indicator.SneakyAlphaPulse_v1_0_0(input, trendAtrPeriod, trendMultiplier, pulseShiftAtrFraction, pulseBars, showBackground, backgroundOpacity, showMomentumOrbs, volumeBaselineLen, volumeBurstMultiple, volumeBurstZScore, showWalls, pivotLookback, wallToleranceTicks, wallToleranceAtrFraction, wallMinTouches, minWallStrengthScore, wallMaxAgeBars, showCoil, coilLookback, coilRangeContractionPct, coilVolumeContractionPct, showSignals, revStretchAtrMultiple, signalCooldownBars, showStatusBar, showLegend);
		}
	}
}

#endregion
