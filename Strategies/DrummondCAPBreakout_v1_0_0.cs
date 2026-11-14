// ─────────────────────────────────────────────────────────────────────────────
// DrummondCAPBreakout_v1_0_0.cs
// ─────────────────────────────────────────────────────────────────────────────
//  Standalone NinjaTrader 8 strategy. No third-party dependencies, no other AddOns/services
//  required — this file compiles and runs entirely on its own.
//
//  WHAT THIS IS
//  ------------
//  A from-scratch, original NinjaScript implementation of ONE Drummond-Geometry-style setup:
//  trading a breakout out of a multi-timeframe "node" (what Drummond Geometry calls a Congestion
//  Action Point) — the setup most often pointed to, in community discussion, as the cleaner,
//  higher-conviction Drummond Geometry trade, versus a raw single-timeframe PL Dot cross (widely
//  regarded as the noisiest signal on its own). See the chat discussion for why "most talked
//  about" isn't the same claim as "most profitable" — nobody publishes real backtested numbers
//  broken out by pattern, this file exists so an actual number can be produced for THIS specific
//  rule set via the Strategy Analyzer, instead of trusting community reputation.
//
//  IMPORTANT SOURCING NOTE
//  ------------------------
//  This is NOT a reproduction of Drummond Geometry LLC's proprietary software, course material, or
//  exact trading rules — those are taught in a paid course, and at least one licensed affiliate's
//  own published disclaimer states they're bound by an NDA that prohibits public discussion of the
//  deeper pattern categorization. What IS publicly documented (the "Drummond geometry" Wikipedia
//  article, among other public sources) is the underlying math: the PL Dot is "the average of the
//  high, low, and close of the last three bars, displaced forward one bar," and the envelope is a
//  pair of trading bands built the same way from just the highs and just the lows. This file
//  implements exactly that public-domain formula, plus an original, mechanical, first-cut
//  definition of "node" and "congestion" (spelled out below) so the breakout condition is
//  unambiguous and testable — it's one reasonable interpretation of the general concept, not a
//  claim to replicate Drummond/Hearne's actual proprietary trading rules.
//
//  THE PATTERN, MECHANICALLY DEFINED
//  ----------------------------------
//    PL Dot (this timeframe):    3-bar average of (H+L+C)/3, displaced forward one bar.
//    Envelope (this timeframe):  upper band = 3-bar average of highs, lower band = 3-bar
//                                 average of lows — same displaced-forward idea as the dot.
//    PL Dot (higher timeframe):  the identical 3-bar dot formula, computed on a second, higher-
//                                 timeframe bar series (HigherTimeframeMinutes, default 15m).
//    "Node":            |this-timeframe dot − higher-timeframe dot| <= NodeToleranceTicks — the
//                        two timeframes' reference levels are sitting close together.
//    "Congestion":      this-timeframe closes have stayed within a CongestionRangeTicks-wide band
//                        over the last CongestionLookback bars — price has been going sideways
//                        near the node, not already trending through it.
//    Breakout trigger:  with a node AND congestion both currently true, a bar closes more than
//                       BreakoutTicks beyond the envelope's upper (long) or lower (short) band.
//    Exit:              a StopTicks stop, trailed by hand off this-timeframe's PL Dot every bar
//                       (never loosened), OR an immediate exit if a later bar closes back through
//                       the PL Dot against the position. No separate profit target, by design —
//                       matching how Drummond Geometry traders generally treat the dot/envelope as
//                       the ongoing, bar-by-bar arbiter of whether a trade is still working, not
//                       just an entry trigger.
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
//  ⚠ FIRST CUT, NOT LIVE-TESTED. This is a mechanical simplification of a discretionary,
//  many-factor methodology — run it in Sim, on a small size, and watch how it actually behaves
//  before trusting it with anything real. Run it through the Strategy Analyzer to get a real
//  win-rate/expectancy number for THIS rule set on your actual instrument — that number, not
//  community reputation, is what "most profitable" would actually have to mean.
//
//  Changelog
//    v1.0.0 (2026-09-07) — first cut.
//    v1.0.1 (2026-09-07) — plot the PL Dot, envelope upper/lower, and higher-timeframe PL Dot
//                          on the chart every bar, purely for visual verification. No effect on
//                          trading logic — same entries/exits/stops as v1.0.0.
//    v1.0.2 (2026-09-07) — added TradeLongs/TradeShorts toggles so either direction can be
//                          disabled for testing (e.g. a short-only variant) without touching the
//                          rest of the logic. Both default to true, so behavior is unchanged
//                          unless you explicitly flip one off.
//    v1.0.3 (2026-09-07) — updated the default parameter values (Congestion lookback 5, Congestion
//                          range 20, Node tolerance 5, Breakout confirmation 2, Stop 5) to match
//                          the best-evidenced combination found via the Strategy Analyzer's
//                          genetic optimizer — a 920-trade sample over 1 year of NQ, net of the
//                          real Lucid Trading NQ commission ($1.75/side). Previous defaults
//                          (10/15/8/4/20) were first-cut guesses, never validated against data.
//                          TradeLongs/TradeShorts both left at true — that same 920-trade sample
//                          showed longs net negative (PF 0.97) and shorts carrying the entire
//                          edge (PF 1.47), but the short-only variant hasn't been tested yet.
//    v1.0.4 (2026-09-08) — added a diagnostic-only ShowSetupHighlight toggle (default true): marks
//                          any bar where node + congestion is true, so you can see exactly which
//                          bars qualified as a setup even if no breakout followed (and therefore no
//                          trade fired). Purely visual — no effect on entries, exits, or stops.
//                          First cut used a 7-argument Draw.Region call that doesn't exist in NT8
//                          (fixed to the real 9-argument two-series overload), then switched from a
//                          Region (shaded between that one bar's own High/Low) to a Draw.Dot marker
//                          below the bar — a Region sized to a single 1-minute bar's price range is
//                          imperceptible once you're zoomed out to look at several days of data; a
//                          dot has a fixed pixel size and stays visible at any zoom level.
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
    public class DrummondCAPBreakout_v1_0_0 : Strategy
    {
        private const string EntryLong  = "CAPLong";
        private const string EntryShort = "CAPShort";

        private double _stopPrice;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Mechanical, first-cut breakout off a multi-timeframe PL Dot 'node' "
                            + "(Congestion Action Point) — an original implementation of the publicly "
                            + "documented Drummond Geometry PL Dot/envelope math, not a reproduction of "
                            + "Drummond Geometry LLC's proprietary course rules. Standalone — no other "
                            + "files required.";
                Name = "Drummond CAP Breakout";

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

                // Defaults below reflect the best-evidenced combination found via the Strategy
                // Analyzer's genetic optimizer (920-trade sample over 1 year of NQ, net of the
                // real Lucid Trading NQ commission — see changelog v1.0.3). Still change these
                // per-instrument/per-account — this isn't a claim that it's optimal everywhere.
                HigherTimeframeMinutes = 15;
                CongestionLookback     = 5;
                CongestionRangeTicks   = 20;
                NodeToleranceTicks     = 5;
                BreakoutTicks          = 2;
                StopTicks              = 5;
                Contracts              = 1;
                TradeLongs             = true;   // that 920-trade sample showed longs net negative
                                                  // (PF 0.97) and shorts carrying the whole edge (PF
                                                  // 1.47) — left both on since the short-only variant
                                                  // hasn't been tested yet; flip this off to try it.
                TradeShorts            = true;
                ShowSetupHighlight     = true;   // diagnostic only, see the property below

                // Visual verification only — these plots don't drive any trading logic, they just
                // let you watch the same dot/envelope values the strategy is reading every bar.
                AddPlot(Brushes.Yellow,      "PL Dot");
                AddPlot(Brushes.DodgerBlue,  "Envelope Upper");
                AddPlot(Brushes.DodgerBlue,  "Envelope Lower");
                AddPlot(Brushes.Orange,      "HTF PL Dot");
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, HigherTimeframeMinutes);
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;   // only act on the primary series' bar closes —
                                                 // the higher-timeframe series just needs to sit
                                                 // there and update itself, we read it below
            if (CurrentBars[0] < BarsRequiredToTrade) return;
            if (CurrentBars[1] < 3) return;     // need 3 closed HTF bars for its own dot formula

            try
            {
                double dot      = PLDot(0);
                double envUpper = EnvelopeUpper(0);
                double envLower = EnvelopeLower(0);
                double htfDot   = PLDot(1);

                // Visual verification only — see the changelog note above.
                Values[0][0] = dot;
                Values[1][0] = envUpper;
                Values[2][0] = envLower;
                Values[3][0] = htfDot;

                bool isNode      = Math.Abs(dot - htfDot) <= NodeToleranceTicks * TickSize;
                double rangeHigh = MAX(Close, CongestionLookback)[0];
                double rangeLow  = MIN(Close, CongestionLookback)[0];
                bool isCongested = (rangeHigh - rangeLow) <= CongestionRangeTicks * TickSize;

                // Diagnostic only — see ShowSetupHighlight. Shades this bar whenever node +
                // congestion is true, regardless of whether a breakout (and therefore a trade)
                // ever follows, so a "why didn't it trade here" question can be answered by eye.
                if (ShowSetupHighlight && isNode && isCongested)
                    Draw.Dot(this, "CAPSetup" + CurrentBar, false, 0,
                        Low[0] - 4 * TickSize, Brushes.Yellow);

                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    if (isNode && isCongested)
                    {
                        if (TradeLongs && Close[0] > envUpper + BreakoutTicks * TickSize)
                        {
                            EnterLong(Contracts, EntryLong);
                            _stopPrice = Instrument.MasterInstrument.RoundToTickSize(Close[0] - StopTicks * TickSize);
                            SetStopLoss(EntryLong, CalculationMode.Price, _stopPrice, false);
                            Print("DrummondCAPBreakout LONG @ " + Close[0].ToString("0.####")
                                + " — node/congestion breakout, dot " + dot.ToString("0.####")
                                + ", HTF dot " + htfDot.ToString("0.####")
                                + ", stop @ " + _stopPrice.ToString("0.####"));
                        }
                        else if (TradeShorts && Close[0] < envLower - BreakoutTicks * TickSize)
                        {
                            EnterShort(Contracts, EntryShort);
                            _stopPrice = Instrument.MasterInstrument.RoundToTickSize(Close[0] + StopTicks * TickSize);
                            SetStopLoss(EntryShort, CalculationMode.Price, _stopPrice, false);
                            Print("DrummondCAPBreakout SHORT @ " + Close[0].ToString("0.####")
                                + " — node/congestion breakout, dot " + dot.ToString("0.####")
                                + ", HTF dot " + htfDot.ToString("0.####")
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
            catch (Exception ex) { Print("DrummondCAPBreakout OnBarUpdate error: " + ex.Message); }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (marketPosition == MarketPosition.Flat)
                _stopPrice = 0;
        }

        // ── PL Dot: 3-bar average of (H+L+C)/3, displaced forward one bar. Public-domain formula
        //    (see the sourcing note above), computed generically so it works for either series. ──
        private double PLDot(int seriesIndex)
        {
            double sum = 0;
            for (int i = 0; i < 3; i++)
                sum += (Highs[seriesIndex][i] + Lows[seriesIndex][i] + Closes[seriesIndex][i]) / 3.0;
            return sum / 3.0;
        }

        // ── Envelope: upper band = 3-bar average of highs, lower band = 3-bar average of lows ──
        private double EnvelopeUpper(int seriesIndex)
        {
            double sum = 0;
            for (int i = 0; i < 3; i++) sum += Highs[seriesIndex][i];
            return sum / 3.0;
        }

        private double EnvelopeLower(int seriesIndex)
        {
            double sum = 0;
            for (int i = 0; i < 3; i++) sum += Lows[seriesIndex][i];
            return sum / 3.0;
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Higher timeframe (minutes)", Description = "Fixed-minute bar period for the second, higher timeframe used to detect a multi-timeframe node.", Order = 1, GroupName = "Parameters")]
        public int HigherTimeframeMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "Congestion lookback (bars)", Description = "How many recent bars' closes must stay within the congestion range for a node to count as valid.", Order = 2, GroupName = "Parameters")]
        public int CongestionLookback { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Congestion range (ticks)", Description = "Max spread between the highest and lowest close over the lookback window to call price 'congested'.", Order = 3, GroupName = "Parameters")]
        public int CongestionRangeTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Node tolerance (ticks)", Description = "Max distance between this timeframe's PL Dot and the higher timeframe's PL Dot to call it a node.", Order = 4, GroupName = "Parameters")]
        public int NodeToleranceTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Breakout confirmation (ticks)", Description = "How far beyond the envelope band a bar must close to count as a breakout, not noise.", Order = 5, GroupName = "Parameters")]
        public int BreakoutTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Stop (ticks)", Description = "Initial protective stop distance; trailed by hand off the PL Dot every bar afterward, never loosened. No separate profit target.", Order = 6, GroupName = "Parameters")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Contracts", Description = "Size of each entry.", Order = 7, GroupName = "Parameters")]
        public int Contracts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trade longs", Description = "Allow long entries. Turn off to run a short-only variant without touching any other logic.", Order = 8, GroupName = "Parameters")]
        public bool TradeLongs { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trade shorts", Description = "Allow short entries. Turn off to run a long-only variant without touching any other logic.", Order = 9, GroupName = "Parameters")]
        public bool TradeShorts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show setup highlight", Description = "Diagnostic only, no effect on trading: marks any bar where node + congestion is currently true with a small yellow dot below it, even if no breakout follows. A fixed-size marker (not a price-scaled region) so it stays visible zoomed out over many days.", Order = 10, GroupName = "Parameters")]
        public bool ShowSetupHighlight { get; set; }
        #endregion
    }
}
