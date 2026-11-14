// ─────────────────────────────────────────────────────────────────────────────
// DrummondEnvelopeSqueeze_v1_0_0.cs
// ─────────────────────────────────────────────────────────────────────────────
//  Standalone NinjaTrader 8 strategy. No third-party dependencies, no other AddOns/services
//  required — this file compiles and runs entirely on its own.
//
//  WHAT THIS IS
//  ------------
//  A from-scratch, original NinjaScript implementation of a THIRD Drummond-Geometry-style setup:
//  trading a breakout that follows a squeeze in the PL Dot envelope's width — the envelope going
//  unusually narrow (contraction, low volatility, indecision) and then snapping back wide again
//  (expansion) as price commits to a direction. This is the third of the three patterns originally
//  shortlisted alongside [[DrummondCAPBreakout_v1_0_0]] (multi-timeframe node breakout) and
//  [[DrummondPLDotPullback_v1_0_0]] (pullback-to-the-dot trend continuation) — the last one built.
//
//  IMPORTANT SOURCING NOTE
//  ------------------------
//  This is NOT a reproduction of Drummond Geometry LLC's proprietary software, course material, or
//  exact trading rules — those are taught in a paid course under an NDA that prohibits public
//  discussion of the deeper pattern categorization. What IS publicly documented (the "Drummond
//  geometry" Wikipedia article, among other public sources) is the underlying math: the PL Dot is
//  "the average of the high, low, and close of the last three bars, displaced forward one bar," and
//  the envelope is a pair of trading bands built the same way from just the highs and just the
//  lows. This file implements exactly that public-domain formula, plus an original, mechanical,
//  first-cut definition of "squeeze" and "expansion" (spelled out below) so the breakout condition
//  is unambiguous and testable — it's one reasonable interpretation of the general concept of a
//  volatility squeeze applied to the Drummond envelope, not a claim to replicate Drummond/Hearne's
//  actual proprietary trading rules.
//
//  THE PATTERN, MECHANICALLY DEFINED
//  ----------------------------------
//    PL Dot:              3-bar average of (H+L+C)/3, displaced forward one bar.
//    Envelope:             upper band = 3-bar average of highs, lower band = 3-bar average of
//                          lows — same displaced-forward idea as the dot.
//    Envelope width:       upper band minus lower band, as of any given bar.
//    "Squeeze":            envelope width has stayed at or below SqueezeThresholdTicks for each of
//                          the last SqueezeLookbackBars bars — price has gone quiet, the envelope
//                          has pinched in.
//    "Expansion breakout": with a squeeze active over that lookback, the CURRENT bar's envelope
//                          width pushes back above SqueezeThresholdTicks (the squeeze is releasing
//                          right now, not just being poked at) AND that same bar closes more than
//                          ExpansionTicks beyond the envelope's upper (long) or lower (short) band.
//                          Both conditions are checked on the same bar so a genuine expansion and a
//                          genuine directional close have to coincide — a bar that merely closes
//                          outside the band while the envelope is still pinched tight doesn't count.
//    Exit:                 a StopTicks stop, trailed by hand off the PL Dot every bar (never
//                          loosened), OR an immediate exit if a later bar closes back through the
//                          PL Dot against the position. No separate profit target, by design — same
//                          philosophy as [[DrummondCAPBreakout_v1_0_0]]: the dot is the ongoing,
//                          bar-by-bar arbiter of whether the trade is still working, not just an
//                          entry trigger. (A fixed profit target was tried on the pullback strategy
//                          and did not improve results there — see that file's changelog — so this
//                          one starts without one rather than repeating that experiment blind.)
//
//  WHY NO SECOND TIMEFRAME
//  -------------------------
//  Unlike the other two Drummond strategies in this suite, this one deliberately does NOT reference
//  a higher-timeframe series. The squeeze itself — this timeframe's own envelope going quiet then
//  releasing — IS the setup; there's no multi-timeframe confluence concept being tested here, so
//  adding an unused second data series would just be complexity for its own sake.
//
//  WHY MANAGED ORDERS (unlike [[StraddleBreakoutOCO_v1_0_0]])
//  -------------------------------------------------------------
//  This strategy only ever wants ONE resting directional intent at a time (long OR short, never
//  both pending simultaneously) — none of the straddle's "two opposite entries pending at once"
//  design that forced it onto the unmanaged order approach. The standard managed approach
//  (EnterLong/EnterShort + SetStopLoss) is simpler and sidesteps that entire class of bug (OCO id
//  reuse, unmanaged-order historical-warmup races, ChangeOrder/market-side validity errors) from
//  the start — this file still defensively clamps its hand-trailed stop against the live bid/ask
//  before resubmitting it, the same lesson learned the hard way on the straddle strategy.
//
//  A diagnostic-only ShowSetupHighlight toggle is included from day one (default true) — marks any
//  bar where a squeeze is currently active with a small dot below it, even if no expansion breakout
//  follows, so a "why didn't it trade here" question can be answered by eye instead of guessed at.
//  A fixed-pixel-size Draw.Dot marker is used rather than a price-scaled Draw.Region, a lesson
//  learned debugging [[DrummondCAPBreakout_v1_0_0]] v1.0.4: a region shaded to one bar's own
//  high/low is imperceptible once you're zoomed out to look at several days of data.
//
//  ⚠ FIRST CUT, NOT LIVE-TESTED. This is a mechanical simplification of a discretionary,
//  many-factor methodology — run it in Sim, on a small size, and watch how it actually behaves
//  before trusting it with anything real. The default parameter values below (SqueezeLookbackBars,
//  SqueezeThresholdTicks, ExpansionTicks, StopTicks) are untested first-cut guesses, exactly like
//  DrummondCAPBreakout's original v1.0.0 defaults were — run this through the Strategy Analyzer to
//  get a real number for THIS rule set before trusting any of them.
//
//  Changelog
//    v1.0.0 (2026-09-08) — first cut.
// ─────────────────────────────────────────────────────────────────────────────
#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class DrummondEnvelopeSqueeze_v1_0_0 : Strategy
    {
        private const string EntryLong  = "SqueezeLong";
        private const string EntryShort = "SqueezeShort";

        private double _stopPrice;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Mechanical, first-cut breakout off a PL Dot envelope squeeze "
                            + "(contraction then expansion) — an original implementation of the "
                            + "publicly documented Drummond Geometry PL Dot/envelope math, not a "
                            + "reproduction of Drummond Geometry LLC's proprietary course rules. "
                            + "Standalone — no other files required.";
                Name = "Drummond Envelope Squeeze";

                Calculate                    = Calculate.OnBarClose;   // bar-close confirmation only —
                                                                        // no OnEachTick race exposure
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = false;   // no separate exit rule besides the stop/dot-fail
                MaximumBarsLookBack          = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution          = OrderFillResolution.Standard;
                Slippage                     = 0;
                StartBehavior                = StartBehavior.WaitUntilFlat;
                TimeInForce                  = TimeInForce.Gtc;
                TraceOrders                  = false;
                RealtimeErrorHandling        = RealtimeErrorHandling.StopCancelClose;
                BarsRequiredToTrade          = 20;
                IsUnmanaged                  = false;   // single directional entry at a time —
                                                          // the straddle's unmanaged machinery doesn't apply

                // Untested first-cut guesses — see the file header. Change these per-instrument/
                // per-account once real data says otherwise.
                SqueezeLookbackBars   = 8;
                SqueezeThresholdTicks = 15;
                ExpansionTicks        = 2;
                StopTicks             = 5;
                Contracts             = 1;
                TradeLongs            = true;
                TradeShorts           = true;
                ShowSetupHighlight    = true;   // diagnostic only, see the property below

                // Visual verification only — these plots don't drive any trading logic, they just
                // let you watch the same dot/envelope values the strategy is reading every bar.
                AddPlot(Brushes.Yellow,      "PL Dot");
                AddPlot(Brushes.DodgerBlue,  "Envelope Upper");
                AddPlot(Brushes.DodgerBlue,  "Envelope Lower");
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade) return;
            if (CurrentBar < SqueezeLookbackBars + 3) return;   // need the lookback window plus 3
                                                                  // bars of context for the envelope
                                                                  // formula itself

            try
            {
                double dot      = PLDot(0);
                double envUpper = EnvelopeUpper(0);
                double envLower = EnvelopeLower(0);
                double width    = envUpper - envLower;

                // Visual verification only — see the changelog note above.
                Values[0][0] = dot;
                Values[1][0] = envUpper;
                Values[2][0] = envLower;

                bool isSqueezed = true;
                for (int i = 1; i <= SqueezeLookbackBars; i++)
                {
                    if (EnvelopeWidth(i) > SqueezeThresholdTicks * TickSize)
                    {
                        isSqueezed = false;
                        break;
                    }
                }

                // Diagnostic only — see ShowSetupHighlight. Marks this bar whenever a squeeze has
                // been sitting there over the lookback window, regardless of whether an expansion
                // breakout (and therefore a trade) ever follows.
                if (ShowSetupHighlight && isSqueezed)
                    Draw.Dot(this, "SqueezeSetup" + CurrentBar, false, 0,
                        Low[0] - 4 * TickSize, Brushes.Yellow);

                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    if (isSqueezed && width > SqueezeThresholdTicks * TickSize)
                    {
                        if (TradeLongs && Close[0] > envUpper + ExpansionTicks * TickSize)
                        {
                            EnterLong(Contracts, EntryLong);
                            _stopPrice = Instrument.MasterInstrument.RoundToTickSize(Close[0] - StopTicks * TickSize);
                            SetStopLoss(EntryLong, CalculationMode.Price, _stopPrice, false);
                            Print("DrummondEnvelopeSqueeze LONG @ " + Close[0].ToString("0.####")
                                + " — squeeze/expansion breakout, dot " + dot.ToString("0.####")
                                + ", width " + width.ToString("0.####")
                                + ", stop @ " + _stopPrice.ToString("0.####"));
                        }
                        else if (TradeShorts && Close[0] < envLower - ExpansionTicks * TickSize)
                        {
                            EnterShort(Contracts, EntryShort);
                            _stopPrice = Instrument.MasterInstrument.RoundToTickSize(Close[0] + StopTicks * TickSize);
                            SetStopLoss(EntryShort, CalculationMode.Price, _stopPrice, false);
                            Print("DrummondEnvelopeSqueeze SHORT @ " + Close[0].ToString("0.####")
                                + " — squeeze/expansion breakout, dot " + dot.ToString("0.####")
                                + ", width " + width.ToString("0.####")
                                + ", stop @ " + _stopPrice.ToString("0.####"));
                        }
                    }
                    return;
                }

                // In position: the dot is the ongoing arbiter, not just the entry trigger — a
                // close back through it against us means the setup's no longer working, exit
                // outright rather than waiting for the (wider) stop. Otherwise trail the stop off
                // the dot every bar, never loosening it, with the same live-bid/ask clamp learned
                // the hard way on the straddle strategy (a candidate computed off a bar close can
                // be a tick or two stale by the time it reaches the engine in a fast market).
                if (Position.MarketPosition == MarketPosition.Long)
                {
                    if (Close[0] < dot) { ExitLong(EntryLong); return; }

                    double candidate = Instrument.MasterInstrument.RoundToTickSize(dot - StopTicks * TickSize);
                    if (candidate > _stopPrice)
                    {
                        double currentBid = GetCurrentBid();
                        double safe = currentBid > 0
                            ? Math.Min(candidate, Instrument.MasterInstrument.RoundToTickSize(currentBid - TickSize))
                            : candidate;
                        if (safe > _stopPrice)
                        {
                            _stopPrice = safe;
                            SetStopLoss(EntryLong, CalculationMode.Price, _stopPrice, false);
                        }
                    }
                }
                else if (Position.MarketPosition == MarketPosition.Short)
                {
                    if (Close[0] > dot) { ExitShort(EntryShort); return; }

                    double candidate = Instrument.MasterInstrument.RoundToTickSize(dot + StopTicks * TickSize);
                    if (candidate < _stopPrice)
                    {
                        double currentAsk = GetCurrentAsk();
                        double safe = currentAsk > 0
                            ? Math.Max(candidate, Instrument.MasterInstrument.RoundToTickSize(currentAsk + TickSize))
                            : candidate;
                        if (safe < _stopPrice)
                        {
                            _stopPrice = safe;
                            SetStopLoss(EntryShort, CalculationMode.Price, _stopPrice, false);
                        }
                    }
                }
            }
            catch (Exception ex) { Print("DrummondEnvelopeSqueeze OnBarUpdate error: " + ex.Message); }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (marketPosition == MarketPosition.Flat)
                _stopPrice = 0;
        }

        // ── PL Dot: 3-bar average of (H+L+C)/3, displaced forward one bar. Public-domain formula
        //    (see the sourcing note above). barsAgo lets the squeeze lookback re-evaluate the
        //    envelope as of any earlier bar, not just the current one. ──
        private double PLDot(int barsAgo)
        {
            double sum = 0;
            for (int i = 0; i < 3; i++)
                sum += (High[barsAgo + i] + Low[barsAgo + i] + Close[barsAgo + i]) / 3.0;
            return sum / 3.0;
        }

        // ── Envelope: upper band = 3-bar average of highs, lower band = 3-bar average of lows ──
        private double EnvelopeUpper(int barsAgo)
        {
            double sum = 0;
            for (int i = 0; i < 3; i++) sum += High[barsAgo + i];
            return sum / 3.0;
        }

        private double EnvelopeLower(int barsAgo)
        {
            double sum = 0;
            for (int i = 0; i < 3; i++) sum += Low[barsAgo + i];
            return sum / 3.0;
        }

        private double EnvelopeWidth(int barsAgo)
        {
            return EnvelopeUpper(barsAgo) - EnvelopeLower(barsAgo);
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "Squeeze lookback (bars)", Description = "How many consecutive prior bars must all show a narrow envelope for a squeeze to count as valid.", Order = 1, GroupName = "Parameters")]
        public int SqueezeLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Squeeze threshold (ticks)", Description = "Max envelope width, in ticks, to call price 'squeezed' -- the same absolute-ticks style as CAP Breakout's congestion range, for testability.", Order = 2, GroupName = "Parameters")]
        public int SqueezeThresholdTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Expansion confirmation (ticks)", Description = "How far beyond the envelope band a bar must close, while the envelope is expanding, to count as a genuine breakout, not noise.", Order = 3, GroupName = "Parameters")]
        public int ExpansionTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Stop (ticks)", Description = "Initial protective stop distance; trailed by hand off the PL Dot every bar afterward, never loosened. No separate profit target.", Order = 4, GroupName = "Parameters")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Contracts", Description = "Size of each entry.", Order = 5, GroupName = "Parameters")]
        public int Contracts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trade longs", Description = "Allow long entries. Turn off to run a short-only variant without touching any other logic.", Order = 6, GroupName = "Parameters")]
        public bool TradeLongs { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trade shorts", Description = "Allow short entries. Turn off to run a long-only variant without touching any other logic.", Order = 7, GroupName = "Parameters")]
        public bool TradeShorts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show setup highlight", Description = "Diagnostic only, no effect on trading: marks any bar where a squeeze is currently active with a small yellow dot below it, even if no expansion breakout follows.", Order = 8, GroupName = "Parameters")]
        public bool ShowSetupHighlight { get; set; }
        #endregion
    }
}
