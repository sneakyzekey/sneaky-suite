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
using NinjaTrader.Gui.Tools;
#endregion

// 2026-09-21 -- PHASE 1 of a from-scratch rebuild of a lost personal project ("Praedox
// Sentinel"). Nothing but marketing copy and screenshots survived -- no source, no written
// formulas -- so this is an ORIGINAL implementation of what the marketing copy describes,
// built from ordinary technical-analysis building blocks (EMA/ATR/RSI/Stochastics/volume,
// all NinjaTrader-built-in), not a recovered or reverse-engineered copy of anything. Where
// the copy was vague ("institutional energy entering," "multiple internal conditions must
// align"), I picked a concrete, defensible rule and documented the choice inline so it can
// be tuned or replaced once you see how it behaves on your charts.
//
// SCOPE OF THIS FILE: the 10-signal engine + the 5 signal-group toggles + priority
// selection (one fired signal per bar, highest priority wins on a tie), now WIRED to
// PraedoxFibEngine_v0_1_0 (2026-09-22 update -- see "FIB ENGINE WIRING" below) and to
// PraedoxVolatilityShield_v0_1_0 (2026-09-23 update -- see "VOLATILITY SHIELD WIRING"
// below). Still not included (later phases): Trade Protector/daily P&L limits, FibGuard
// Trade Manager, or the cockpit UI/ATM/sound-alert/report layer.
// This indicator does not place orders -- it's a signal source, same pattern as
// TurtleTrendBreakoutSignal_v1_0_0: plots for an automation bridge to read, plus on-chart
// labels for visual confirmation.
//
// FIB ENGINE WIRING (Phase 2 hookup): this file holds a private PraedoxFibEngine_v0_1_0
// instance (default settings) and reads its per-bar outputs to adjust the signals above,
// per that file's own header comment on how each output is meant to be consumed:
//   - InKillZone[0]==1 or ExhaustionActive[0]==1 SUPPRESS the breakout/continuation-family
//     signals (Slingshot, Zone Break, Continuation, EMA Breakout, Aggressive EMA, Breakout)
//     -- "price fighting a major level is a bad place to trust a breakout," and a stale
//     extreme means the move already played out. Reversal, CrossFire, Trend Cycle Wave,
//     and Volume Aggressive are NOT suppressed -- they're not breakout/continuation-shaped,
//     and Reversal specifically should stay live during exactly these conditions.
//   - NearKeyLevel[0] gives Reversal (#5) an alternate, fib-driven qualifying path: an
//     exhaustion candle sitting at a key fib level (61.8/65/78.6%) counts as reversal
//     confluence on its own, without also needing the RSI-extreme+structural-level path --
//     "a reversal AT a major level is exactly the instant confirmation" from the spec.
// Toggle EnableFibConfluence off to run the original Phase 1 behavior unchanged.
//
// VOLATILITY SHIELD WIRING (Phase 3 hookup): this file also holds a private
// PraedoxVolatilityShield_v0_1_0 instance (default settings) and reads its per-bar
// TradingAllowed[0] output. Unlike the fib engine's per-family suppression, this is a
// single TOP-LEVEL gate applied ahead of all 10 signal checks -- the shield's own job (per
// its header/marketing spec) is "should we be trading AT ALL in this volatility regime,"
// not "is this particular signal shape still valid," so when it says no, nothing here
// fires, full stop, regardless of rank or signal family. When TradingAllowed[0]==0 the
// whole priority chain below is skipped for the bar (rank/direction stay 0, plots emit
// 0/0, no label). Toggle EnableVolatilityShield off to run without this gate.
//
// STATUS OVERLAY (2026-09-23 update): a fixed top-left on-chart text readout, one
// Draw.TextFixed line per condition, matching the original product's reference
// screenshots (VR/suppression-flag text stacked top-left). Sourced entirely from
// fibEngine/volShield outputs already being read above -- no new state. Two items from
// those same reference screenshots are DELIBERATELY NOT included here: "Session N
// Active" and "IB" (Initial Balance) markers. Both need tracking this file has no
// concept of yet (session/day boundaries, first-hour opening-range price capture) --
// rather than fabricate placeholder values, they're left as a separate follow-up.
// Toggle ShowStatusOverlay off to hide it.
//
// THE 10 SIGNALS, priority order and the concrete rule each one got (all lengths/multiples
// are tunable properties, not hardcoded):
//
//  1. Slingshot     -- volatility COMPRESSION (N-bar high-low range shrinks below a
//                       fraction of ATR) followed immediately by a RELEASE bar (range
//                       expands past an ATR multiple, closes in the direction of the
//                       break, in the top/bottom third of its own range). This is the
//                       standard "squeeze then expansion" shape "compresses, then
//                       releases with aggressive momentum" describes.
//  2. Zone Break     -- close breaks a structural swing level (N-bar prior high/low, same
//                       Donchian-style construction as TurtleTrendBreakoutSignal, shorter
//                       lookback than signal #10 below since this is a "zone," not "major
//                       structure"), gated by its OWN three independent filters (EMA trend
//                       alignment, a Choppiness Index ceiling, and a minimum distance from
//                       the level so it isn't grabbed on a graze) -- matches "own
//                       independent filter system."
//  3. Trend Cycle Wave -- a slow Stochastic crossing UP out of its own oversold band while
//                       price is still above the trend EMA (or the mirror for short) --
//                       i.e. a pullback-within-an-uptrend troughing and turning, which is
//                       "the start of the next impulse leg" rather than a mid-trend
//                       coincidence.
//  4. CrossFire      -- requires 3 independent conditions on the SAME bar: trend EMA
//                       alignment, RSI on the correct side of 50, AND volume above its
//                       own average -- "multiple internal conditions must align
//                       simultaneously," deliberately the least frequent signal.
//  5. Reversal       -- RSI at a real extreme (configurable OB/OS) AND price touching a
//                       structural swing level from #2 AND the bar itself is an exhaustion
//                       candle (a long wick against the extreme, closing back inside range)
//                       -- "meaningful confluence of overextension and structural context,"
//                       not a bare RSI cross.
//  6. Continuation   -- established trend (price + EMA slope agree) pulls back to touch the
//                       trend EMA, then the CURRENT bar resumes in the trend direction --
//                       "pullback-to-trend... waiting for market to come back to them."
//  7. EMA Breakout   -- close crosses the trend EMA AND the following bar also closes on
//                       the same side (2-bar confirmation) with momentum agreeing (RSI same
//                       side of 50) -- "waits for confirmation to avoid fake-outs."
//  8. Aggressive EMA -- identical to #7 but fires on the FIRST close across the EMA, no
//                       confirmation bar required -- "fires earlier... before full
//                       confirmation."
//  9. Volume Aggressive -- directional bar (close beyond prior close in that direction)
//                       with volume above its own moving average by a configurable
//                       multiple -- "volume is the one thing that doesn't lie."
// 10. Breakout       -- same Donchian-style break as #2 but over a LONGER lookback ("major
//                       market structure" vs. a nearer "zone"), with a momentum filter
//                       (bar range vs. ATR) so a slow grind through the level doesn't count
//                       as "enough momentum to confirm the break is real."
//
// PRIORITY: signals are evaluated in the order above every bar; if more than one condition
// set is true on the same bar, only the HIGHEST-priority (lowest-numbered) one is reported
// -- matches "priority-ranked" in the spec, and avoids firing two conflicting/duplicate
// signals off one bar.
//
// OUTPUT (for an automation bridge, same pattern as TurtleTrendBreakoutSignal_v1_0_0):
//   Direction[0] ==  1 / -1   -> the winning signal's side, for exactly the bar it fired
//                                 on (one-bar pulse, then reverts to 0)
//   Direction[0] ==  0        -> nothing fired this bar
//   SignalRank[0] == 1..10    -> which of the 10 signals fired, same pulse bar as Direction
//                                 (0 when Direction is 0). Filter a bridge on a specific
//                                 rank to trade only one signal type, e.g. SignalRank == 1
//                                 for Slingshot-only.
// On-chart: a text tag with the signal's name is drawn at the fire bar for visual
// confirmation, colored by direction.
namespace NinjaTrader.NinjaScript.Indicators
{
    public class PraedoxSignalEngine_v0_1_0 : Indicator
    {
        private EMA trendEma;
        private EMA fastEma;
        private ATR atr;
        private RSI rsi;
        private SMA volAvg;

        // Manually-computed "slow stochastic" %K, rather than the built-in Stochastics()
        // helper -- its exact constructor signature (period/smooth argument order) isn't
        // something I could verify without a compiler in hand, and getting that wrong is
        // exactly what broke an earlier build in this project. This is the same %K formula
        // (close position within the N-bar high/low range, 0-100), built from MAX/MIN which
        // are already proven to compile correctly elsewhere in this file, then smoothed
        // with the built-in SMA() applied to that custom series -- a standard, low-risk
        // NinjaScript idiom.
        private Series<double> stochKRaw;
        private SMA stochKSmooth;

        // Phase 2 wiring: a sub-indicator instance (default settings) whose per-bar
        // outputs suppress/confirm the signals below -- see "FIB ENGINE WIRING" above.
        private PraedoxFibEngine_v0_1_0 fibEngine;
        private double currentFibNearKey;
        private bool currentFibSuppressed;

        // Phase 3 wiring: a sub-indicator instance (default settings) whose TradingAllowed
        // output gates the whole priority chain -- see "VOLATILITY SHIELD WIRING" above.
        private PraedoxVolatilityShield_v0_1_0 volShield;
        private bool currentVolAllowed;
        private bool currentVolWarmedUp;

        private static readonly SimpleFont overlayFont = new SimpleFont("Arial", 12) { Bold = true };
        private static readonly string[] VolStateMessages =
        {
            "NORMAL", "LOWER VOLATILITY - EXERCISE CAUTION", "ELEVATED VOLATILITY", "EXTREME VOLATILITY - TRADING BLOCKED"
        };

        private int lastFireBar = -1;
        private int lastFireDirection;
        private int lastFireRank;

        // Per-signal cooldown: index 1-10 tracks the bar each rank last fired on, so a
        // condition that stays technically true for many consecutive bars (e.g. Zone Break
        // during a steady grind, where every new high requalifies as "breaking the zone")
        // doesn't re-fire every single bar. Initialized far in the past so nothing is
        // blocked on the first legitimate fire.
        private readonly int[] lastFireBarByRank = new int[11];

        // Label decluttering: when fires land close together in time, each new label is
        // pushed progressively farther from price than the last one instead of stacking on
        // top of it at the same distance.
        private int lastLabelBar = -10000;
        private int labelStackCount;

        // Computed once per bar (ChoppinessIndex is a small loop, not worth recomputing
        // per signal check) and read by every trend/momentum/breakout-style signal via
        // ChopOk() below. Reversal and Volume Aggressive deliberately do NOT gate on this
        // -- reversals are expected to cluster in exactly the rangebound conditions this
        // filter is built to detect, and a volume surge is its own confirmation regardless
        // of whether the broader tape is trending or chopping.
        private double currentChop;

        private static readonly string[] SignalNames =
        {
            "", // unused, ranks are 1-based
            "Slingshot", "Zone Break", "Trend Cycle Wave", "CrossFire", "Reversal",
            "Continuation", "EMA Breakout", "Aggressive EMA", "Volume Aggressive", "Breakout"
        };

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "PraedoxSignalEngine_v0_1_0";
                Description = "Phase 1 rebuild: 10 priority-ranked signals + 5 group toggles. Original implementation from behavior spec (no surviving source). Signal-only, no order management.";

                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;

                // -- Core parameters --
                TrendEmaLength = 50;
                FastEmaLength = 20;
                AtrLength = 14;
                RsiLength = 14;
                StochLength = 14;
                StochSmooth = 3;
                VolumeAvgLength = 20;

                // -- Signal-specific parameters --
                CompressionLookback = 10;
                CompressionAtrFraction = 1.5;
                ReleaseAtrMultiple = 1.2;
                ZoneBreakLookback = 30;
                MinBreakDistanceTicks = 4;
                BreakoutLookback = 55;
                ChopLength = 14;
                ChopCeiling = 55.0;
                RsiOverbought = 70;
                RsiOversold = 30;
                StochOversold = 20;
                StochOverbought = 80;
                VolumeSurgeMultiple = 1.5;

                // A signal that stays technically true for many bars in a row (e.g. Zone
                // Break during a steady grind) won't re-fire until this many bars pass,
                // even while its raw condition keeps re-qualifying -- see CooldownOk().
                CooldownBars = 8;

                // Label declutter: fires landing within this many bars of the previous
                // label push the new one progressively farther from price instead of
                // overlapping it.
                LabelDeclutterBars = 6;
                LabelOffsetAtrMultiple = 0.35;

                // Extends Zone Break's chop filter to the other trend/momentum/breakout
                // signals (Slingshot, Trend Cycle Wave, CrossFire, Continuation, EMA
                // Breakout, Aggressive EMA, Breakout) so they go quiet during choppy/
                // rangebound stretches instead of firing on noise. Reversal and Volume
                // Aggressive are deliberately exempt -- see ChopOk() comment.
                ApplyChopFilterBroadly = true;

                // -- Individual signal toggles (all on by default) --
                EnableSlingshot = true;
                EnableZoneBreak = true;
                EnableTrendCycleWave = true;
                EnableCrossFire = true;
                EnableReversal = true;
                EnableContinuation = true;
                EnableEmaBreakout = true;
                EnableAggressiveEma = true;
                EnableVolumeAggressive = true;
                EnableBreakout = true;

                // -- Group toggles (spec: Zone = ZoneBreak+Slingshot, Reversal =
                // Reversal+Continuation, Breakout = EmaBreakout+Breakout+AggressiveEma,
                // Cycle = TrendCycleWave, Momentum = CrossFire+VolumeAggressive) --
                GroupZone = true;
                GroupReversal = true;
                GroupBreakout = true;
                GroupCycle = true;
                GroupMomentum = true;

                // Phase 2 wiring -- see "FIB ENGINE WIRING" in the file header comment.
                EnableFibConfluence = true;

                // Phase 3 wiring -- see "VOLATILITY SHIELD WIRING" in the file header comment.
                EnableVolatilityShield = true;

                // Status overlay -- see "STATUS OVERLAY" in the file header comment.
                ShowStatusOverlay = true;

                DrawLabels = true;

                AddPlot(new Stroke(Brushes.DodgerBlue, 2), PlotStyle.Bar, "Direction");
                AddPlot(new Stroke(Brushes.Gold, 1), PlotStyle.Dot, "SignalRank");
            }
            else if (State == State.DataLoaded)
            {
                trendEma = EMA(TrendEmaLength);
                fastEma = EMA(FastEmaLength);
                atr = ATR(AtrLength);
                rsi = RSI(RsiLength, 3);
                volAvg = SMA(Volume, VolumeAvgLength);

                stochKRaw = new Series<double>(this);
                stochKSmooth = SMA(stochKRaw, StochSmooth);

                // Default-settings sub-instance -- not plotted for its own sake here, just
                // read for its InKillZone/NearKeyLevel/ExhaustionActive outputs each bar.
                // NinjaTrader doesn't generate a bare parameterless overload once a script
                // has [NinjaScriptProperty] fields, so this spells out PraedoxFibEngine's
                // own SetDefaults values explicitly (50, 35, 8 ticks, 15-bar zone width,
                // all draw toggles on).
                fibEngine = PraedoxFibEngine_v0_1_0(50, 35, 8, 15, true, true, true, true, true, true);

                // Default-settings sub-instance -- spells out PraedoxVolatilityShield's own
                // SetDefaults values explicitly (same reason as fibEngine above: no bare
                // parameterless overload once a script has [NinjaScriptProperty] fields).
                // 14/50 ATR+baseline lengths, 0.85/1.5/2.5 Yellow/Red/DarkRed thresholds,
                // the 8 stop/target ATR multiples, Smart Trader on in GreenAndYellow mode,
                // label drawing on.
                volShield = PraedoxVolatilityShield_v0_1_0(14, 50, 0.85, 1.5, 2.5, 1.0, 2.0, 1.3, 2.2, 1.8, 2.6, 2.5, 3.2, true, PraedoxSmartTraderMode.GreenAndYellow, true);
            }
        }

        protected override void OnBarUpdate()
        {
            int warmup = Math.Max(Math.Max(TrendEmaLength, BreakoutLookback), Math.Max(ChopLength, StochLength)) + 5;
            if (CurrentBar < warmup)
            {
                Values[0][0] = 0;
                Values[1][0] = 0;
                return;
            }

            // Raw %K (close position within the N-bar high/low range, 0-100) -- must be
            // set before CheckTrendCycleWave() runs below, since it reads stochKSmooth
            // which wraps this series.
            double stochRange = MAX(High, StochLength)[0] - MIN(Low, StochLength)[0];
            stochKRaw[0] = stochRange > 0 ? 100.0 * (Close[0] - MIN(Low, StochLength)[0]) / stochRange : 50.0;

            currentChop = ChoppinessIndex(ChopLength);

            // Phase 2 wiring: pull this bar's fib-engine reading once, same pattern as
            // currentChop above. Before the fib engine's own warmup completes, its outputs
            // are all zero, which resolves to "no suppression, no confluence" here -- safe
            // default, nothing to special-case.
            currentFibNearKey = EnableFibConfluence ? fibEngine.NearKeyLevel[0] : 0;
            currentFibSuppressed = EnableFibConfluence && (fibEngine.InKillZone[0] == 1 || fibEngine.ExhaustionActive[0] == 1);

            // Phase 3 wiring: pull this bar's Smart Trader reading. Before the shield's own
            // warmup completes, TradingAllowed defaults to 0 (see that file's OnBarUpdate
            // warmup guard), which would wrongly block every signal during warmup -- so,
            // same "safe default" pattern as the fib engine, treat "not warmed up yet" as
            // "allowed" rather than inheriting its warmed-up-only semantics.
            currentVolWarmedUp = CurrentBar >= volShield.VrAtrLength + volShield.VrBaselineLength + 2;
            currentVolAllowed = !EnableVolatilityShield || !currentVolWarmedUp || volShield.TradingAllowed[0] == 1;

            int rank = 0;
            int direction = 0;

            // Evaluated strictly in priority order -- first non-zero result wins. Each
            // check function already ANDs in its own toggle + group toggle; CooldownOk()
            // additionally blocks a rank that fired too recently from re-firing while its
            // raw condition is still (re-)qualifying, which is what was flooding Zone
            // Break/Reversal during a steady grind before this was added. currentVolAllowed
            // gates the ENTIRE chain -- see "VOLATILITY SHIELD WIRING" in the file header.
            if (currentVolAllowed)
            {
                if (CooldownOk(1) && (direction = CheckSlingshot()) != 0) rank = 1;
                else if (CooldownOk(2) && (direction = CheckZoneBreak()) != 0) rank = 2;
                else if (CooldownOk(3) && (direction = CheckTrendCycleWave()) != 0) rank = 3;
                else if (CooldownOk(4) && (direction = CheckCrossFire()) != 0) rank = 4;
                else if (CooldownOk(5) && (direction = CheckReversal()) != 0) rank = 5;
                else if (CooldownOk(6) && (direction = CheckContinuation()) != 0) rank = 6;
                else if (CooldownOk(7) && (direction = CheckEmaBreakout()) != 0) rank = 7;
                else if (CooldownOk(8) && (direction = CheckAggressiveEma()) != 0) rank = 8;
                else if (CooldownOk(9) && (direction = CheckVolumeAggressive()) != 0) rank = 9;
                else if (CooldownOk(10) && (direction = CheckBreakout()) != 0) rank = 10;
            }

            if (rank != 0)
            {
                lastFireBar = CurrentBar;
                lastFireDirection = direction;
                lastFireRank = rank;
                lastFireBarByRank[rank] = CurrentBar;

                if (DrawLabels)
                {
                    // Stack labels farther from price each time fires land within
                    // LabelDeclutterBars of the previous one, instead of overlapping at a
                    // fixed distance -- see field comments above.
                    labelStackCount = (CurrentBar - lastLabelBar <= LabelDeclutterBars) ? labelStackCount + 1 : 0;
                    lastLabelBar = CurrentBar;

                    string tag = "Praedox" + CurrentBar;
                    string text = rank + ". " + SignalNames[rank];
                    Brush color = direction > 0 ? Brushes.Lime : Brushes.OrangeRed;
                    double offset = atr[0] * LabelOffsetAtrMultiple * (1 + labelStackCount * 0.8);
                    double y = direction > 0 ? Low[0] - offset : High[0] + offset;
                    Draw.Text(this, tag, text, 0, y, color);
                }
            }

            // One-bar pulse, same pattern as TurtleTrendBreakoutSignal_v1_0_0's Trigger --
            // re-derived from lastFireBar every pass so it's stable across multiple ticks
            // of the same bar and reverts to 0 automatically the bar after.
            Values[0][0] = (CurrentBar == lastFireBar) ? lastFireDirection : 0;
            Values[1][0] = (CurrentBar == lastFireBar) ? lastFireRank : 0;

            DrawStatusOverlay();
        }

        /// <summary>
        /// Fixed top-left on-chart text readout -- see "STATUS OVERLAY" in the file header
        /// comment. NinjaTrader does NOT auto-stack separate Draw.TextFixed calls anchored
        /// to the same TextPosition -- they render on top of each other at the identical
        /// pixel spot (confirmed 2026-09-23: multiple single-line calls overlapped into
        /// unreadable mush). So this builds one multi-line string from whichever lines are
        /// currently active and draws it with a SINGLE Draw.TextFixed call -- the embedded
        /// "\n"s stack it correctly on their own. Trade-off: one text block means one brush,
        /// so per-line color (green/gold/red in the reference screenshots) isn't possible
        /// this way -- color instead reflects the single most severe condition present.
        /// </summary>
        private void DrawStatusOverlay()
        {
            if (!ShowStatusOverlay)
            {
                RemoveDrawObject("PraedoxOverlay");
                return;
            }

            var lines = new System.Collections.Generic.List<string>();
            bool severe = false;   // extreme volatility -- red
            bool caution = false;  // anything else worth calling out -- gold

            if (EnableVolatilityShield && currentVolWarmedUp)
            {
                double vr = volShield.VolatilityRatio[0];
                int state = (int)volShield.VolatilityState[0];
                string stateMsg = (state >= 0 && state < VolStateMessages.Length) ? VolStateMessages[state] : "NORMAL";
                lines.Add("VR: " + vr.ToString("0.00") + " | " + stateMsg);
                if (state == 3) severe = true;
                else if (state != 0) caution = true;
            }

            if (EnableFibConfluence && fibEngine.InKillZone[0] == 1)
            {
                lines.Add("⚠ FIB SUPPRESSION ACTIVE");
                caution = true;
            }

            if (EnableFibConfluence && fibEngine.ExhaustionActive[0] == 1)
            {
                lines.Add("⚠ EXHAUSTION SUPPRESSION ACTIVE (ALWAYS 0%)");
                caution = true;
            }

            int atrTicks = TickSize > 0 ? (int)Math.Round(atr[0] / TickSize) : 0;
            lines.Add("ATR: " + atrTicks + "t");

            if (EnableFibConfluence && fibEngine.Level0[0] > 0)
                lines.Add("Fib Anchored @ " + fibEngine.Level0[0].ToString("0.00"));

            Brush color = severe ? Brushes.Red : (caution ? Brushes.Gold : Brushes.White);
            Draw.TextFixed(this, "PraedoxOverlay", string.Join("\n", lines), TextPosition.TopLeft, color, overlayFont, Brushes.Transparent, Brushes.Black, 60);
        }

        private bool CooldownOk(int rank)
        {
            return CurrentBar - lastFireBarByRank[rank] >= CooldownBars;
        }

        /// <summary>
        /// True when the tape isn't too choppy to trust a trend/momentum/breakout signal
        /// (or when ApplyChopFilterBroadly is off). Zone Break has always checked chop
        /// unconditionally as one of its "own independent filters" from the original spec;
        /// this extends the same check to the other directional signals, gated behind a
        /// toggle since it's a broader behavior change from the original per-signal design.
        /// </summary>
        private bool ChopOk()
        {
            return !ApplyChopFilterBroadly || currentChop <= ChopCeiling;
        }

        #region Signal 1 -- Slingshot (compression -> release)
        private int CheckSlingshot()
        {
            if (!EnableSlingshot || !GroupZone || !ChopOk() || currentFibSuppressed)
                return 0;

            // Compression: the N-bar range immediately BEFORE this bar was unusually tight
            // relative to ATR -- a squeeze. [1] so the range being measured doesn't include
            // the release bar itself.
            double compressedRange = MAX(High, CompressionLookback)[1] - MIN(Low, CompressionLookback)[1];
            bool wasCompressed = compressedRange < atr[1] * CompressionAtrFraction;
            if (!wasCompressed)
                return 0;

            // Release: THIS bar's range expands past an ATR multiple, and closes strongly
            // in one direction (top or bottom third of its own range) -- a real thrust, not
            // just a slightly-bigger bar.
            double barRange = High[0] - Low[0];
            if (barRange < atr[0] * ReleaseAtrMultiple || barRange <= 0)
                return 0;

            double closePosition = (Close[0] - Low[0]) / barRange;
            if (closePosition >= 0.66)
                return 1;
            if (closePosition <= 0.33)
                return -1;
            return 0;
        }
        #endregion

        #region Signal 2 -- Zone Break (structural level + 3 independent filters)
        private int CheckZoneBreak()
        {
            if (!EnableZoneBreak || !GroupZone || currentFibSuppressed)
                return 0;

            double zoneHigh = MAX(High, ZoneBreakLookback)[1];
            double zoneLow = MIN(Low, ZoneBreakLookback)[1];

            bool brokeUp = Close[0] > zoneHigh && Close[0] - zoneHigh > TickSize * MinBreakDistanceTicks;
            bool brokeDown = Close[0] < zoneLow && zoneLow - Close[0] > TickSize * MinBreakDistanceTicks;
            if (!brokeUp && !brokeDown)
                return 0;

            // Filter 1: EMA trend alignment.
            bool trendUp = Close[0] > trendEma[0];
            if (brokeUp && !trendUp) return 0;
            if (brokeDown && trendUp) return 0;

            // Filter 2: Choppiness ceiling -- don't take a "structural" break out of a
            // choppy/rangebound tape. Unconditional (not gated by ApplyChopFilterBroadly)
            // since this was always one of Zone Break's "own independent filters."
            if (currentChop > ChopCeiling)
                return 0;

            // Filter 3 is the minimum-distance check already applied above (brokeUp/
            // brokeDown), kept separate from the raw cross so a graze doesn't count.

            return brokeUp ? 1 : -1;
        }
        #endregion

        #region Signal 3 -- Trend Cycle Wave (pullback troughing, start of next impulse leg)
        private int CheckTrendCycleWave()
        {
            if (!EnableTrendCycleWave || !GroupCycle || !ChopOk())
                return 0;

            bool trendUp = Close[0] > trendEma[0];
            bool trendDown = Close[0] < trendEma[0];

            bool stochTurnUp = CrossAbove(stochKSmooth, StochOversold, 1);
            bool stochTurnDown = CrossBelow(stochKSmooth, StochOverbought, 1);

            if (trendUp && stochTurnUp)
                return 1;
            if (trendDown && stochTurnDown)
                return -1;
            return 0;
        }
        #endregion

        #region Signal 4 -- CrossFire (3-way convergence, low frequency)
        private int CheckCrossFire()
        {
            if (!EnableCrossFire || !GroupMomentum || !ChopOk())
                return 0;

            bool volSurge = Volume[0] > volAvg[0] * VolumeSurgeMultiple;
            if (!volSurge)
                return 0;

            bool trendUp = Close[0] > trendEma[0];
            bool rsiUp = rsi[0] > 50;
            bool trendDown = Close[0] < trendEma[0];
            bool rsiDown = rsi[0] < 50;

            if (trendUp && rsiUp)
                return 1;
            if (trendDown && rsiDown)
                return -1;
            return 0;
        }
        #endregion

        #region Signal 5 -- Reversal (exhaustion at structural extreme)
        private int CheckReversal()
        {
            if (!EnableReversal || !GroupReversal)
                return 0;

            double zoneHigh = MAX(High, ZoneBreakLookback)[1];
            double zoneLow = MIN(Low, ZoneBreakLookback)[1];
            double barRange = High[0] - Low[0];
            if (barRange <= 0)
                return 0;

            bool atStructuralHigh = High[0] >= zoneHigh;
            bool atStructuralLow = Low[0] <= zoneLow;

            // Exhaustion candle: a long wick against the extreme, closing back inside --
            // upper wick > 50% of range for a top, lower wick > 50% of range for a bottom.
            double upperWick = High[0] - Math.Max(Open[0], Close[0]);
            double lowerWick = Math.Min(Open[0], Close[0]) - Low[0];

            if (atStructuralHigh && rsi[0] >= RsiOverbought && upperWick / barRange > 0.5)
                return -1;
            if (atStructuralLow && rsi[0] <= RsiOversold && lowerWick / barRange > 0.5)
                return 1;

            // Phase 2 wiring: fib confluence as an ALTERNATE qualifying path, on top of
            // (not replacing) the RSI+structural-level path above. NearKeyLevel > 0 means
            // the fib grid is anchored Up (price retracing into resistance); < 0 means
            // anchored Down (retracing into support) -- see PraedoxFibEngine's header
            // comment. An exhaustion candle at the matching key level is treated as its own
            // "instant confirmation," independent of where RSI happens to sit.
            if (currentFibNearKey > 0 && upperWick / barRange > 0.5)
                return -1;
            if (currentFibNearKey < 0 && lowerWick / barRange > 0.5)
                return 1;
            return 0;
        }
        #endregion

        #region Signal 6 -- Continuation (pullback to trend EMA, then resume)
        private int CheckContinuation()
        {
            if (!EnableContinuation || !GroupReversal || !ChopOk() || currentFibSuppressed)
                return 0;

            bool trendUp = trendEma[0] > trendEma[5];
            bool trendDown = trendEma[0] < trendEma[5];

            // Pullback touched the EMA on the PRIOR bar, current bar resumes in trend
            // direction (closes back above/below the EMA, in the trend's favor).
            bool touchedEmaLong = Low[1] <= trendEma[1] && Close[1] >= trendEma[1];
            bool touchedEmaShort = High[1] >= trendEma[1] && Close[1] <= trendEma[1];

            if (trendUp && touchedEmaLong && Close[0] > trendEma[0] && Close[0] > Close[1])
                return 1;
            if (trendDown && touchedEmaShort && Close[0] < trendEma[0] && Close[0] < Close[1])
                return -1;
            return 0;
        }
        #endregion

        #region Signal 7 -- EMA Breakout (2-bar confirmation)
        private int CheckEmaBreakout()
        {
            if (!EnableEmaBreakout || !GroupBreakout || !ChopOk() || currentFibSuppressed)
                return 0;

            bool crossedUp = Close[1] > trendEma[1] && Close[2] <= trendEma[2];
            bool crossedDown = Close[1] < trendEma[1] && Close[2] >= trendEma[2];

            // Confirmation: THIS bar also closes on the same side, with RSI agreeing.
            if (crossedUp && Close[0] > trendEma[0] && rsi[0] > 50)
                return 1;
            if (crossedDown && Close[0] < trendEma[0] && rsi[0] < 50)
                return -1;
            return 0;
        }
        #endregion

        #region Signal 8 -- Aggressive EMA (no confirmation bar)
        private int CheckAggressiveEma()
        {
            if (!EnableAggressiveEma || !GroupBreakout || !ChopOk() || currentFibSuppressed)
                return 0;

            bool crossedUp = Close[0] > fastEma[0] && Close[1] <= fastEma[1];
            bool crossedDown = Close[0] < fastEma[0] && Close[1] >= fastEma[1];

            if (crossedUp) return 1;
            if (crossedDown) return -1;
            return 0;
        }
        #endregion

        #region Signal 9 -- Volume Aggressive
        private int CheckVolumeAggressive()
        {
            if (!EnableVolumeAggressive || !GroupMomentum)
                return 0;

            bool volSurge = Volume[0] > volAvg[0] * VolumeSurgeMultiple;
            if (!volSurge)
                return 0;

            if (Close[0] > Close[1])
                return 1;
            if (Close[0] < Close[1])
                return -1;
            return 0;
        }
        #endregion

        #region Signal 10 -- Breakout (major structure, longer lookback + momentum filter)
        private int CheckBreakout()
        {
            if (!EnableBreakout || !GroupBreakout || !ChopOk() || currentFibSuppressed)
                return 0;

            double structHigh = MAX(High, BreakoutLookback)[1];
            double structLow = MIN(Low, BreakoutLookback)[1];

            bool brokeUp = Close[0] > structHigh;
            bool brokeDown = Close[0] < structLow;
            if (!brokeUp && !brokeDown)
                return 0;

            // Momentum filter: the breakout bar's own range must be a real push, not a
            // slow grind that happened to close past the level.
            if (High[0] - Low[0] < atr[0])
                return 0;

            return brokeUp ? 1 : -1;
        }
        #endregion

        /// <summary>
        /// Choppiness Index over the given length: 100 * log10(sum(TR, n) / (max(High,n) -
        /// min(Low,n))) / log10(n). High values (near 100) = choppy/rangebound; low values
        /// (near 0) = trending. Not a NinjaTrader built-in, so computed inline. Used only
        /// by Zone Break's chop filter.
        /// </summary>
        private double ChoppinessIndex(int length)
        {
            double sumTr = 0;
            for (int i = 0; i < length; i++)
                sumTr += TrueRange(i);

            double rangeHigh = MAX(High, length)[0];
            double rangeLow = MIN(Low, length)[0];
            double range = rangeHigh - rangeLow;
            if (range <= 0 || sumTr <= 0)
                return 0;

            return 100.0 * Math.Log10(sumTr / range) / Math.Log10(length);
        }

        private double TrueRange(int barsAgo)
        {
            if (CurrentBar - barsAgo < 1)
                return High[barsAgo] - Low[barsAgo];

            double h = High[barsAgo];
            double l = Low[barsAgo];
            double pc = Close[barsAgo + 1];
            return Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Trend EMA Length", GroupName = "Core", Order = 1)]
        public int TrendEmaLength { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Fast EMA Length", GroupName = "Core", Order = 2)]
        public int FastEmaLength { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "ATR Length", GroupName = "Core", Order = 3)]
        public int AtrLength { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "RSI Length", GroupName = "Core", Order = 4)]
        public int RsiLength { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Stochastics Length", GroupName = "Core", Order = 5)]
        public int StochLength { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Stochastics Smooth", GroupName = "Core", Order = 6)]
        public int StochSmooth { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Volume Average Length", GroupName = "Core", Order = 7)]
        public int VolumeAvgLength { get; set; }

        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "Compression Lookback", GroupName = "Slingshot", Order = 1)]
        public int CompressionLookback { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Compression ATR Fraction", GroupName = "Slingshot", Order = 2)]
        public double CompressionAtrFraction { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Release ATR Multiple", GroupName = "Slingshot", Order = 3)]
        public double ReleaseAtrMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "Zone Break Lookback", GroupName = "Zone/Reversal", Order = 1)]
        public int ZoneBreakLookback { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Min Break Distance (ticks)", GroupName = "Zone/Reversal", Order = 4)]
        public int MinBreakDistanceTicks { get; set; }

        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "Breakout Lookback (major structure)", GroupName = "Breakout", Order = 1)]
        public int BreakoutLookback { get; set; }

        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "Choppiness Length", GroupName = "Zone/Reversal", Order = 2)]
        public int ChopLength { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Choppiness Ceiling", GroupName = "Zone/Reversal", Order = 3)]
        public double ChopCeiling { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Apply Chop Filter Broadly", GroupName = "Zone/Reversal", Order = 5)]
        public bool ApplyChopFilterBroadly { get; set; }

        [NinjaScriptProperty]
        [Range(50, 100)]
        [Display(Name = "RSI Overbought", GroupName = "Reversal", Order = 1)]
        public double RsiOverbought { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "RSI Oversold", GroupName = "Reversal", Order = 2)]
        public double RsiOversold { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Stochastics Oversold", GroupName = "Cycle", Order = 1)]
        public double StochOversold { get; set; }

        [NinjaScriptProperty]
        [Range(50, 100)]
        [Display(Name = "Stochastics Overbought", GroupName = "Cycle", Order = 2)]
        public double StochOverbought { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, double.MaxValue)]
        [Display(Name = "Volume Surge Multiple", GroupName = "Momentum", Order = 1)]
        public double VolumeSurgeMultiple { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: Slingshot", GroupName = "Signal Toggles", Order = 1)]
        public bool EnableSlingshot { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: Zone Break", GroupName = "Signal Toggles", Order = 2)]
        public bool EnableZoneBreak { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: Trend Cycle Wave", GroupName = "Signal Toggles", Order = 3)]
        public bool EnableTrendCycleWave { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: CrossFire", GroupName = "Signal Toggles", Order = 4)]
        public bool EnableCrossFire { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: Reversal", GroupName = "Signal Toggles", Order = 5)]
        public bool EnableReversal { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: Continuation", GroupName = "Signal Toggles", Order = 6)]
        public bool EnableContinuation { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: EMA Breakout", GroupName = "Signal Toggles", Order = 7)]
        public bool EnableEmaBreakout { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: Aggressive EMA", GroupName = "Signal Toggles", Order = 8)]
        public bool EnableAggressiveEma { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: Volume Aggressive", GroupName = "Signal Toggles", Order = 9)]
        public bool EnableVolumeAggressive { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: Breakout", GroupName = "Signal Toggles", Order = 10)]
        public bool EnableBreakout { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Group: Zone (Slingshot + Zone Break)", GroupName = "Signal Groups", Order = 1)]
        public bool GroupZone { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Group: Reversal (Reversal + Continuation)", GroupName = "Signal Groups", Order = 2)]
        public bool GroupReversal { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Group: Breakout (EMA Breakout + Breakout + Aggressive EMA)", GroupName = "Signal Groups", Order = 3)]
        public bool GroupBreakout { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Group: Cycle (Trend Cycle Wave)", GroupName = "Signal Groups", Order = 4)]
        public bool GroupCycle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Group: Momentum (CrossFire + Volume Aggressive)", GroupName = "Signal Groups", Order = 5)]
        public bool GroupMomentum { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Fib Confluence (Phase 2)", GroupName = "Core", Order = 9, Description = "Wires PraedoxFibEngine's InKillZone/ExhaustionActive to suppress breakout/continuation signals, and NearKeyLevel to give Reversal an alternate fib-confluence qualifying path. Off = original Phase 1 behavior.")]
        public bool EnableFibConfluence { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Volatility Shield (Phase 3)", GroupName = "Core", Order = 10, Description = "Wires PraedoxVolatilityShield's TradingAllowed output as a top-level gate ahead of all 10 signals -- when the current volatility regime isn't allowed under the shield's Mode setting, nothing fires this bar. Off = ungated.")]
        public bool EnableVolatilityShield { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Status Overlay", GroupName = "Display", Order = 3, Description = "Fixed top-left on-chart text readout: volatility ratio/state, fib kill-zone/exhaustion suppression flags, ATR, fib anchor price.")]
        public bool ShowStatusOverlay { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Cooldown Bars (per signal)", GroupName = "Core", Order = 8)]
        public int CooldownBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw Labels", GroupName = "Display", Order = 1)]
        public bool DrawLabels { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Label Declutter Window (bars)", GroupName = "Display", Order = 2)]
        public int LabelDeclutterBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, double.MaxValue)]
        [Display(Name = "Label Offset (x ATR)", GroupName = "Display", Order = 3)]
        public double LabelOffsetAtrMultiple { get; set; }

        [Browsable(false)]
        [XmlIgnore()]
        public Series<double> Direction
        {
            get { return Values[0]; }
        }

        [Browsable(false)]
        [XmlIgnore()]
        public Series<double> SignalRank
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
		private PraedoxSignalEngine_v0_1_0[] cachePraedoxSignalEngine_v0_1_0;
		public PraedoxSignalEngine_v0_1_0 PraedoxSignalEngine_v0_1_0(int trendEmaLength, int fastEmaLength, int atrLength, int rsiLength, int stochLength, int stochSmooth, int volumeAvgLength, int compressionLookback, double compressionAtrFraction, double releaseAtrMultiple, int zoneBreakLookback, int minBreakDistanceTicks, int breakoutLookback, int chopLength, double chopCeiling, bool applyChopFilterBroadly, double rsiOverbought, double rsiOversold, double stochOversold, double stochOverbought, double volumeSurgeMultiple, bool enableSlingshot, bool enableZoneBreak, bool enableTrendCycleWave, bool enableCrossFire, bool enableReversal, bool enableContinuation, bool enableEmaBreakout, bool enableAggressiveEma, bool enableVolumeAggressive, bool enableBreakout, bool groupZone, bool groupReversal, bool groupBreakout, bool groupCycle, bool groupMomentum, bool enableFibConfluence, bool enableVolatilityShield, bool showStatusOverlay, int cooldownBars, bool drawLabels, int labelDeclutterBars, double labelOffsetAtrMultiple)
		{
			return PraedoxSignalEngine_v0_1_0(Input, trendEmaLength, fastEmaLength, atrLength, rsiLength, stochLength, stochSmooth, volumeAvgLength, compressionLookback, compressionAtrFraction, releaseAtrMultiple, zoneBreakLookback, minBreakDistanceTicks, breakoutLookback, chopLength, chopCeiling, applyChopFilterBroadly, rsiOverbought, rsiOversold, stochOversold, stochOverbought, volumeSurgeMultiple, enableSlingshot, enableZoneBreak, enableTrendCycleWave, enableCrossFire, enableReversal, enableContinuation, enableEmaBreakout, enableAggressiveEma, enableVolumeAggressive, enableBreakout, groupZone, groupReversal, groupBreakout, groupCycle, groupMomentum, enableFibConfluence, enableVolatilityShield, showStatusOverlay, cooldownBars, drawLabels, labelDeclutterBars, labelOffsetAtrMultiple);
		}

		public PraedoxSignalEngine_v0_1_0 PraedoxSignalEngine_v0_1_0(ISeries<double> input, int trendEmaLength, int fastEmaLength, int atrLength, int rsiLength, int stochLength, int stochSmooth, int volumeAvgLength, int compressionLookback, double compressionAtrFraction, double releaseAtrMultiple, int zoneBreakLookback, int minBreakDistanceTicks, int breakoutLookback, int chopLength, double chopCeiling, bool applyChopFilterBroadly, double rsiOverbought, double rsiOversold, double stochOversold, double stochOverbought, double volumeSurgeMultiple, bool enableSlingshot, bool enableZoneBreak, bool enableTrendCycleWave, bool enableCrossFire, bool enableReversal, bool enableContinuation, bool enableEmaBreakout, bool enableAggressiveEma, bool enableVolumeAggressive, bool enableBreakout, bool groupZone, bool groupReversal, bool groupBreakout, bool groupCycle, bool groupMomentum, bool enableFibConfluence, bool enableVolatilityShield, bool showStatusOverlay, int cooldownBars, bool drawLabels, int labelDeclutterBars, double labelOffsetAtrMultiple)
		{
			if (cachePraedoxSignalEngine_v0_1_0 != null)
				for (int idx = 0; idx < cachePraedoxSignalEngine_v0_1_0.Length; idx++)
					if (cachePraedoxSignalEngine_v0_1_0[idx] != null && cachePraedoxSignalEngine_v0_1_0[idx].TrendEmaLength == trendEmaLength && cachePraedoxSignalEngine_v0_1_0[idx].FastEmaLength == fastEmaLength && cachePraedoxSignalEngine_v0_1_0[idx].AtrLength == atrLength && cachePraedoxSignalEngine_v0_1_0[idx].RsiLength == rsiLength && cachePraedoxSignalEngine_v0_1_0[idx].StochLength == stochLength && cachePraedoxSignalEngine_v0_1_0[idx].StochSmooth == stochSmooth && cachePraedoxSignalEngine_v0_1_0[idx].VolumeAvgLength == volumeAvgLength && cachePraedoxSignalEngine_v0_1_0[idx].CompressionLookback == compressionLookback && cachePraedoxSignalEngine_v0_1_0[idx].CompressionAtrFraction == compressionAtrFraction && cachePraedoxSignalEngine_v0_1_0[idx].ReleaseAtrMultiple == releaseAtrMultiple && cachePraedoxSignalEngine_v0_1_0[idx].ZoneBreakLookback == zoneBreakLookback && cachePraedoxSignalEngine_v0_1_0[idx].MinBreakDistanceTicks == minBreakDistanceTicks && cachePraedoxSignalEngine_v0_1_0[idx].BreakoutLookback == breakoutLookback && cachePraedoxSignalEngine_v0_1_0[idx].ChopLength == chopLength && cachePraedoxSignalEngine_v0_1_0[idx].ChopCeiling == chopCeiling && cachePraedoxSignalEngine_v0_1_0[idx].ApplyChopFilterBroadly == applyChopFilterBroadly && cachePraedoxSignalEngine_v0_1_0[idx].RsiOverbought == rsiOverbought && cachePraedoxSignalEngine_v0_1_0[idx].RsiOversold == rsiOversold && cachePraedoxSignalEngine_v0_1_0[idx].StochOversold == stochOversold && cachePraedoxSignalEngine_v0_1_0[idx].StochOverbought == stochOverbought && cachePraedoxSignalEngine_v0_1_0[idx].VolumeSurgeMultiple == volumeSurgeMultiple && cachePraedoxSignalEngine_v0_1_0[idx].EnableSlingshot == enableSlingshot && cachePraedoxSignalEngine_v0_1_0[idx].EnableZoneBreak == enableZoneBreak && cachePraedoxSignalEngine_v0_1_0[idx].EnableTrendCycleWave == enableTrendCycleWave && cachePraedoxSignalEngine_v0_1_0[idx].EnableCrossFire == enableCrossFire && cachePraedoxSignalEngine_v0_1_0[idx].EnableReversal == enableReversal && cachePraedoxSignalEngine_v0_1_0[idx].EnableContinuation == enableContinuation && cachePraedoxSignalEngine_v0_1_0[idx].EnableEmaBreakout == enableEmaBreakout && cachePraedoxSignalEngine_v0_1_0[idx].EnableAggressiveEma == enableAggressiveEma && cachePraedoxSignalEngine_v0_1_0[idx].EnableVolumeAggressive == enableVolumeAggressive && cachePraedoxSignalEngine_v0_1_0[idx].EnableBreakout == enableBreakout && cachePraedoxSignalEngine_v0_1_0[idx].GroupZone == groupZone && cachePraedoxSignalEngine_v0_1_0[idx].GroupReversal == groupReversal && cachePraedoxSignalEngine_v0_1_0[idx].GroupBreakout == groupBreakout && cachePraedoxSignalEngine_v0_1_0[idx].GroupCycle == groupCycle && cachePraedoxSignalEngine_v0_1_0[idx].GroupMomentum == groupMomentum && cachePraedoxSignalEngine_v0_1_0[idx].EnableFibConfluence == enableFibConfluence && cachePraedoxSignalEngine_v0_1_0[idx].EnableVolatilityShield == enableVolatilityShield && cachePraedoxSignalEngine_v0_1_0[idx].ShowStatusOverlay == showStatusOverlay && cachePraedoxSignalEngine_v0_1_0[idx].CooldownBars == cooldownBars && cachePraedoxSignalEngine_v0_1_0[idx].DrawLabels == drawLabels && cachePraedoxSignalEngine_v0_1_0[idx].LabelDeclutterBars == labelDeclutterBars && cachePraedoxSignalEngine_v0_1_0[idx].LabelOffsetAtrMultiple == labelOffsetAtrMultiple && cachePraedoxSignalEngine_v0_1_0[idx].EqualsInput(input))
						return cachePraedoxSignalEngine_v0_1_0[idx];
			return CacheIndicator<PraedoxSignalEngine_v0_1_0>(new PraedoxSignalEngine_v0_1_0(){ TrendEmaLength = trendEmaLength, FastEmaLength = fastEmaLength, AtrLength = atrLength, RsiLength = rsiLength, StochLength = stochLength, StochSmooth = stochSmooth, VolumeAvgLength = volumeAvgLength, CompressionLookback = compressionLookback, CompressionAtrFraction = compressionAtrFraction, ReleaseAtrMultiple = releaseAtrMultiple, ZoneBreakLookback = zoneBreakLookback, MinBreakDistanceTicks = minBreakDistanceTicks, BreakoutLookback = breakoutLookback, ChopLength = chopLength, ChopCeiling = chopCeiling, ApplyChopFilterBroadly = applyChopFilterBroadly, RsiOverbought = rsiOverbought, RsiOversold = rsiOversold, StochOversold = stochOversold, StochOverbought = stochOverbought, VolumeSurgeMultiple = volumeSurgeMultiple, EnableSlingshot = enableSlingshot, EnableZoneBreak = enableZoneBreak, EnableTrendCycleWave = enableTrendCycleWave, EnableCrossFire = enableCrossFire, EnableReversal = enableReversal, EnableContinuation = enableContinuation, EnableEmaBreakout = enableEmaBreakout, EnableAggressiveEma = enableAggressiveEma, EnableVolumeAggressive = enableVolumeAggressive, EnableBreakout = enableBreakout, GroupZone = groupZone, GroupReversal = groupReversal, GroupBreakout = groupBreakout, GroupCycle = groupCycle, GroupMomentum = groupMomentum, EnableFibConfluence = enableFibConfluence, EnableVolatilityShield = enableVolatilityShield, ShowStatusOverlay = showStatusOverlay, CooldownBars = cooldownBars, DrawLabels = drawLabels, LabelDeclutterBars = labelDeclutterBars, LabelOffsetAtrMultiple = labelOffsetAtrMultiple }, input, ref cachePraedoxSignalEngine_v0_1_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.PraedoxSignalEngine_v0_1_0 PraedoxSignalEngine_v0_1_0(int trendEmaLength, int fastEmaLength, int atrLength, int rsiLength, int stochLength, int stochSmooth, int volumeAvgLength, int compressionLookback, double compressionAtrFraction, double releaseAtrMultiple, int zoneBreakLookback, int minBreakDistanceTicks, int breakoutLookback, int chopLength, double chopCeiling, bool applyChopFilterBroadly, double rsiOverbought, double rsiOversold, double stochOversold, double stochOverbought, double volumeSurgeMultiple, bool enableSlingshot, bool enableZoneBreak, bool enableTrendCycleWave, bool enableCrossFire, bool enableReversal, bool enableContinuation, bool enableEmaBreakout, bool enableAggressiveEma, bool enableVolumeAggressive, bool enableBreakout, bool groupZone, bool groupReversal, bool groupBreakout, bool groupCycle, bool groupMomentum, bool enableFibConfluence, bool enableVolatilityShield, bool showStatusOverlay, int cooldownBars, bool drawLabels, int labelDeclutterBars, double labelOffsetAtrMultiple)
		{
			return indicator.PraedoxSignalEngine_v0_1_0(Input, trendEmaLength, fastEmaLength, atrLength, rsiLength, stochLength, stochSmooth, volumeAvgLength, compressionLookback, compressionAtrFraction, releaseAtrMultiple, zoneBreakLookback, minBreakDistanceTicks, breakoutLookback, chopLength, chopCeiling, applyChopFilterBroadly, rsiOverbought, rsiOversold, stochOversold, stochOverbought, volumeSurgeMultiple, enableSlingshot, enableZoneBreak, enableTrendCycleWave, enableCrossFire, enableReversal, enableContinuation, enableEmaBreakout, enableAggressiveEma, enableVolumeAggressive, enableBreakout, groupZone, groupReversal, groupBreakout, groupCycle, groupMomentum, enableFibConfluence, enableVolatilityShield, showStatusOverlay, cooldownBars, drawLabels, labelDeclutterBars, labelOffsetAtrMultiple);
		}

		public Indicators.PraedoxSignalEngine_v0_1_0 PraedoxSignalEngine_v0_1_0(ISeries<double> input , int trendEmaLength, int fastEmaLength, int atrLength, int rsiLength, int stochLength, int stochSmooth, int volumeAvgLength, int compressionLookback, double compressionAtrFraction, double releaseAtrMultiple, int zoneBreakLookback, int minBreakDistanceTicks, int breakoutLookback, int chopLength, double chopCeiling, bool applyChopFilterBroadly, double rsiOverbought, double rsiOversold, double stochOversold, double stochOverbought, double volumeSurgeMultiple, bool enableSlingshot, bool enableZoneBreak, bool enableTrendCycleWave, bool enableCrossFire, bool enableReversal, bool enableContinuation, bool enableEmaBreakout, bool enableAggressiveEma, bool enableVolumeAggressive, bool enableBreakout, bool groupZone, bool groupReversal, bool groupBreakout, bool groupCycle, bool groupMomentum, bool enableFibConfluence, bool enableVolatilityShield, bool showStatusOverlay, int cooldownBars, bool drawLabels, int labelDeclutterBars, double labelOffsetAtrMultiple)
		{
			return indicator.PraedoxSignalEngine_v0_1_0(input, trendEmaLength, fastEmaLength, atrLength, rsiLength, stochLength, stochSmooth, volumeAvgLength, compressionLookback, compressionAtrFraction, releaseAtrMultiple, zoneBreakLookback, minBreakDistanceTicks, breakoutLookback, chopLength, chopCeiling, applyChopFilterBroadly, rsiOverbought, rsiOversold, stochOversold, stochOverbought, volumeSurgeMultiple, enableSlingshot, enableZoneBreak, enableTrendCycleWave, enableCrossFire, enableReversal, enableContinuation, enableEmaBreakout, enableAggressiveEma, enableVolumeAggressive, enableBreakout, groupZone, groupReversal, groupBreakout, groupCycle, groupMomentum, enableFibConfluence, enableVolatilityShield, showStatusOverlay, cooldownBars, drawLabels, labelDeclutterBars, labelOffsetAtrMultiple);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.PraedoxSignalEngine_v0_1_0 PraedoxSignalEngine_v0_1_0(int trendEmaLength, int fastEmaLength, int atrLength, int rsiLength, int stochLength, int stochSmooth, int volumeAvgLength, int compressionLookback, double compressionAtrFraction, double releaseAtrMultiple, int zoneBreakLookback, int minBreakDistanceTicks, int breakoutLookback, int chopLength, double chopCeiling, bool applyChopFilterBroadly, double rsiOverbought, double rsiOversold, double stochOversold, double stochOverbought, double volumeSurgeMultiple, bool enableSlingshot, bool enableZoneBreak, bool enableTrendCycleWave, bool enableCrossFire, bool enableReversal, bool enableContinuation, bool enableEmaBreakout, bool enableAggressiveEma, bool enableVolumeAggressive, bool enableBreakout, bool groupZone, bool groupReversal, bool groupBreakout, bool groupCycle, bool groupMomentum, bool enableFibConfluence, bool enableVolatilityShield, bool showStatusOverlay, int cooldownBars, bool drawLabels, int labelDeclutterBars, double labelOffsetAtrMultiple)
		{
			return indicator.PraedoxSignalEngine_v0_1_0(Input, trendEmaLength, fastEmaLength, atrLength, rsiLength, stochLength, stochSmooth, volumeAvgLength, compressionLookback, compressionAtrFraction, releaseAtrMultiple, zoneBreakLookback, minBreakDistanceTicks, breakoutLookback, chopLength, chopCeiling, applyChopFilterBroadly, rsiOverbought, rsiOversold, stochOversold, stochOverbought, volumeSurgeMultiple, enableSlingshot, enableZoneBreak, enableTrendCycleWave, enableCrossFire, enableReversal, enableContinuation, enableEmaBreakout, enableAggressiveEma, enableVolumeAggressive, enableBreakout, groupZone, groupReversal, groupBreakout, groupCycle, groupMomentum, enableFibConfluence, enableVolatilityShield, showStatusOverlay, cooldownBars, drawLabels, labelDeclutterBars, labelOffsetAtrMultiple);
		}

		public Indicators.PraedoxSignalEngine_v0_1_0 PraedoxSignalEngine_v0_1_0(ISeries<double> input , int trendEmaLength, int fastEmaLength, int atrLength, int rsiLength, int stochLength, int stochSmooth, int volumeAvgLength, int compressionLookback, double compressionAtrFraction, double releaseAtrMultiple, int zoneBreakLookback, int minBreakDistanceTicks, int breakoutLookback, int chopLength, double chopCeiling, bool applyChopFilterBroadly, double rsiOverbought, double rsiOversold, double stochOversold, double stochOverbought, double volumeSurgeMultiple, bool enableSlingshot, bool enableZoneBreak, bool enableTrendCycleWave, bool enableCrossFire, bool enableReversal, bool enableContinuation, bool enableEmaBreakout, bool enableAggressiveEma, bool enableVolumeAggressive, bool enableBreakout, bool groupZone, bool groupReversal, bool groupBreakout, bool groupCycle, bool groupMomentum, bool enableFibConfluence, bool enableVolatilityShield, bool showStatusOverlay, int cooldownBars, bool drawLabels, int labelDeclutterBars, double labelOffsetAtrMultiple)
		{
			return indicator.PraedoxSignalEngine_v0_1_0(input, trendEmaLength, fastEmaLength, atrLength, rsiLength, stochLength, stochSmooth, volumeAvgLength, compressionLookback, compressionAtrFraction, releaseAtrMultiple, zoneBreakLookback, minBreakDistanceTicks, breakoutLookback, chopLength, chopCeiling, applyChopFilterBroadly, rsiOverbought, rsiOversold, stochOversold, stochOverbought, volumeSurgeMultiple, enableSlingshot, enableZoneBreak, enableTrendCycleWave, enableCrossFire, enableReversal, enableContinuation, enableEmaBreakout, enableAggressiveEma, enableVolumeAggressive, enableBreakout, groupZone, groupReversal, groupBreakout, groupCycle, groupMomentum, enableFibConfluence, enableVolatilityShield, showStatusOverlay, cooldownBars, drawLabels, labelDeclutterBars, labelOffsetAtrMultiple);
		}
	}
}

#endregion
