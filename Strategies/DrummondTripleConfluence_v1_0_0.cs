// ─────────────────────────────────────────────────────────────────────────────
// DrummondTripleConfluence_v1_0_0.cs
// ─────────────────────────────────────────────────────────────────────────────
//  Standalone NinjaTrader 8 strategy. No third-party dependencies, no other AddOns/services
//  required — this file compiles and runs entirely on its own.
//
//  WHAT THIS IS
//  ------------
//  A from-scratch, original NinjaScript implementation of the FOURTH Drummond-Geometry-style
//  setup in this suite: trading immediately when three PL Dots — this timeframe plus two higher
//  timeframes — all stack in the same directional order at once. This was the second item on the
//  original three-option shortlist (alongside [[DrummondCAPBreakout_v1_0_0]]'s multi-timeframe node
//  breakout and [[DrummondPLDotPullback_v1_0_0]]'s pullback-to-the-dot trend continuation) and the
//  last one built, after [[DrummondEnvelopeSqueeze_v1_0_0]] took the third slot.
//
//  IMPORTANT SOURCING NOTE
//  ------------------------
//  This is NOT a reproduction of Drummond Geometry LLC's proprietary software, course material, or
//  exact trading rules — those are taught in a paid course under an NDA that prohibits public
//  discussion of the deeper pattern categorization. What IS publicly documented (the "Drummond
//  geometry" Wikipedia article, among other public sources) is the underlying math: the PL Dot is
//  "the average of the high, low, and close of the last three bars, displaced forward one bar."
//  This file implements exactly that public-domain formula, computed independently on three
//  separate bar series, plus an original, mechanical, first-cut definition of "confluence"
//  (spelled out below) so the entry condition is unambiguous and testable — it's one reasonable
//  interpretation of the general concept of multi-timeframe agreement, not a claim to replicate
//  Drummond/Hearne's actual proprietary trading rules.
//
//  THE PATTERN, MECHANICALLY DEFINED
//  ----------------------------------
//    PL Dot (any timeframe):  3-bar average of (H+L+C)/3, displaced forward one bar. Computed
//                              identically on three separate bar series: this chart's own timeframe
//                              (the "primary" dot), a middle timeframe (MidTimeframeMinutes,
//                              default 5m), and a higher timeframe (HighTimeframeMinutes,
//                              default 15m).
//    "Confluence" (up):        primary dot > mid dot > high dot — each faster series' dot has
//                              pulled above the slower ones', the same way a stack of moving
//                              averages ordering fast-over-slow confirms a trend has persisted long
//                              enough to show up at every timescale being checked, not just the
//                              fastest one.
//    "Confluence" (down):      primary dot < mid dot < high dot — the mirror image.
//    Entry:                    immediate, the moment confluence is true and the strategy is flat —
//                              confluence itself is the signal, no separate pullback or breakout
//                              trigger. (Three entry styles were considered — immediate-on-
//                              alignment, pullback-to-the-dot, and breakout-through-a-range — this
//                              file is the first, chosen to test whether three-timeframe agreement
//                              alone has an edge before layering another trigger on top of it.)
//    Exit:                     a StopTicks stop, trailed by hand off the PRIMARY PL Dot every bar
//                              (never loosened), OR an exit once confluence breaks (the three dots
//                              no longer strictly ordered in the trade's direction) — but that
//                              confluence-break exit is gated by MinBarsInTrade, exactly like
//                              [[DrummondPLDotPullback_v1_0_0]]'s v1.0.1 fix. That strategy's
//                              v1.0.0 shipped WITHOUT this gate and overtraded catastrophically
//                              (50,000+ trades/year, avg <1 bar/trade) because its exit condition
//                              could flip true again almost immediately after entry; this file
//                              starts with the fix already in place rather than rediscovering that
//                              bug. The trailing stop itself stays active immediately regardless of
//                              MinBarsInTrade, same as that fix.
//
//  WHY MANAGED ORDERS (unlike [[StraddleBreakoutOCO_v1_0_0]])
//  -------------------------------------------------------------
//  This strategy only ever wants ONE resting directional intent at a time (long OR short, never
//  both pending simultaneously) — the standard managed approach (EnterLong/EnterShort +
//  SetStopLoss) is simpler and sidesteps the straddle's whole class of unmanaged-order bugs (OCO id
//  reuse, historical-warmup races, ChangeOrder/market-side validity errors) from the start — this
//  file still defensively clamps its hand-trailed stop against the live bid/ask before resubmitting
//  it, the same lesson learned the hard way on the straddle strategy.
//
//  A diagnostic-only ShowSetupHighlight toggle is included from day one (default true) — marks any
//  bar where confluence is currently true (either direction) with a small dot below it. A
//  fixed-pixel-size Draw.Dot marker is used rather than a price-scaled Draw.Region, the same lesson
//  learned debugging [[DrummondCAPBreakout_v1_0_0]] v1.0.4 and already applied in
//  [[DrummondEnvelopeSqueeze_v1_0_0]] v1.0.0.
//
//  ⚠ FIRST CUT, NOT LIVE-TESTED. This is a mechanical simplification of a discretionary,
//  many-factor methodology — run it in Sim, on a small size, and watch how it actually behaves
//  before trusting it with anything real. The default parameter values below (timeframes, stop,
//  min bars) are untested first-cut guesses — run this through the Strategy Analyzer to get a real
//  number for THIS rule set before trusting any of them.
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
    public class DrummondTripleConfluence_v1_0_0 : Strategy
    {
        private const string EntryLong  = "ConfluenceLong";
        private const string EntryShort = "ConfluenceShort";

        private double _stopPrice;
        private int    _entryBar;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Mechanical, first-cut entry on three-timeframe PL Dot confluence "
                            + "(this timeframe plus two higher ones, all stacked the same direction) "
                            + "— an original implementation of the publicly documented Drummond "
                            + "Geometry PL Dot math, not a reproduction of Drummond Geometry LLC's "
                            + "proprietary course rules. Standalone — no other files required.";
                Name = "Drummond Triple Confluence";

                Calculate                    = Calculate.OnBarClose;   // bar-close confirmation only —
                                                                        // no OnEachTick race exposure
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = false;   // no separate exit rule besides the stop/confluence-break
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
                MidTimeframeMinutes  = 5;
                HighTimeframeMinutes = 15;
                MinBarsInTrade       = 5;
                StopTicks            = 5;
                Contracts            = 1;
                TradeLongs           = true;
                TradeShorts          = true;
                ShowSetupHighlight   = true;   // diagnostic only, see the property below

                // Visual verification only — this plot doesn't drive any trading logic, it just
                // lets you watch the same primary dot value the strategy is reading every bar.
                AddPlot(Brushes.Yellow, "PL Dot");
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, MidTimeframeMinutes);
                AddDataSeries(BarsPeriodType.Minute, HighTimeframeMinutes);
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;   // only act on the primary series' bar closes — the
                                                 // two higher-timeframe series just need to sit
                                                 // there and update themselves, we read them below
            if (CurrentBars[0] < BarsRequiredToTrade) return;
            if (CurrentBars[1] < 3 || CurrentBars[2] < 3) return;   // need 3 closed bars on each
                                                                      // series for its own dot formula

            try
            {
                double primaryDot = PLDot(0);
                double midDot     = PLDot(1);
                double highDot    = PLDot(2);

                // Visual verification only — see the changelog note above.
                Values[0][0] = primaryDot;

                bool confluenceUp   = primaryDot > midDot && midDot > highDot;
                bool confluenceDown = primaryDot < midDot && midDot < highDot;

                // Diagnostic only — see ShowSetupHighlight. Marks this bar whenever all three dots
                // are stacked the same direction, regardless of whether a position is open.
                if (ShowSetupHighlight && (confluenceUp || confluenceDown))
                    Draw.Dot(this, "ConfluenceSetup" + CurrentBar, false, 0,
                        Low[0] - 4 * TickSize, Brushes.Yellow);

                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    if (TradeLongs && confluenceUp)
                    {
                        EnterLong(Contracts, EntryLong);
                        _entryBar  = CurrentBar;
                        _stopPrice = Instrument.MasterInstrument.RoundToTickSize(Close[0] - StopTicks * TickSize);
                        SetStopLoss(EntryLong, CalculationMode.Price, _stopPrice, false);
                        Print("DrummondTripleConfluence LONG @ " + Close[0].ToString("0.####")
                            + " — 3-timeframe dot confluence, primary " + primaryDot.ToString("0.####")
                            + ", mid " + midDot.ToString("0.####")
                            + ", high " + highDot.ToString("0.####")
                            + ", stop @ " + _stopPrice.ToString("0.####"));
                    }
                    else if (TradeShorts && confluenceDown)
                    {
                        EnterShort(Contracts, EntryShort);
                        _entryBar  = CurrentBar;
                        _stopPrice = Instrument.MasterInstrument.RoundToTickSize(Close[0] + StopTicks * TickSize);
                        SetStopLoss(EntryShort, CalculationMode.Price, _stopPrice, false);
                        Print("DrummondTripleConfluence SHORT @ " + Close[0].ToString("0.####")
                            + " — 3-timeframe dot confluence, primary " + primaryDot.ToString("0.####")
                            + ", mid " + midDot.ToString("0.####")
                            + ", high " + highDot.ToString("0.####")
                            + ", stop @ " + _stopPrice.ToString("0.####"));
                    }
                    return;
                }

                // In position: confluence breaking is the ongoing arbiter, not just the entry
                // trigger — but gated by MinBarsInTrade so it can't fire the bar right after entry
                // (see the changelog note on why — this is the fix PLDotPullback needed reactively,
                // applied here from day one). The trailing stop stays active immediately regardless,
                // with the same live-bid/ask clamp learned the hard way on the straddle strategy (a
                // candidate computed off a bar close can be a tick or two stale by the time it
                // reaches the engine in a fast market).
                bool minBarsElapsed = (CurrentBar - _entryBar) >= MinBarsInTrade;

                if (Position.MarketPosition == MarketPosition.Long)
                {
                    if (minBarsElapsed && !confluenceUp) { ExitLong(EntryLong); return; }

                    double candidate = Instrument.MasterInstrument.RoundToTickSize(primaryDot - StopTicks * TickSize);
                    if (candidate > _stopPrice)
                    {
                        // Clamp against the live bid when available, otherwise fall back to the last
                        // close (always known, live or backtest) — never submit the raw, unclamped
                        // candidate, or a fast-dropping market can leave a sell stop above the market
                        // (NT8 rejects it, which auto-flattens and disables the strategy).
                        double currentBid = GetCurrentBid();
                        double refPrice = currentBid > 0 ? currentBid : Close[0];
                        double safe = Math.Min(candidate, Instrument.MasterInstrument.RoundToTickSize(refPrice - TickSize));
                        if (safe > _stopPrice)
                        {
                            _stopPrice = safe;
                            SetStopLoss(EntryLong, CalculationMode.Price, _stopPrice, false);
                        }
                    }
                }
                else if (Position.MarketPosition == MarketPosition.Short)
                {
                    if (minBarsElapsed && !confluenceDown) { ExitShort(EntryShort); return; }

                    double candidate = Instrument.MasterInstrument.RoundToTickSize(primaryDot + StopTicks * TickSize);
                    if (candidate < _stopPrice)
                    {
                        // Same fallback as the long side: clamp against the live ask when available,
                        // otherwise the last close, so a buy stop can never land below the market.
                        double currentAsk = GetCurrentAsk();
                        double refPrice = currentAsk > 0 ? currentAsk : Close[0];
                        double safe = Math.Max(candidate, Instrument.MasterInstrument.RoundToTickSize(refPrice + TickSize));
                        if (safe < _stopPrice)
                        {
                            _stopPrice = safe;
                            SetStopLoss(EntryShort, CalculationMode.Price, _stopPrice, false);
                        }
                    }
                }
            }
            catch (Exception ex) { Print("DrummondTripleConfluence OnBarUpdate error: " + ex.Message); }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (marketPosition == MarketPosition.Flat)
                _stopPrice = 0;
        }

        // ── PL Dot: 3-bar average of (H+L+C)/3, displaced forward one bar. Public-domain formula
        //    (see the sourcing note above), computed generically so it works for any of the three
        //    series. ──
        private double PLDot(int seriesIndex)
        {
            double sum = 0;
            for (int i = 0; i < 3; i++)
                sum += (Highs[seriesIndex][i] + Lows[seriesIndex][i] + Closes[seriesIndex][i]) / 3.0;
            return sum / 3.0;
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Mid timeframe (minutes)", Description = "Fixed-minute bar period for the middle of the three timeframes checked for confluence.", Order = 1, GroupName = "Parameters")]
        public int MidTimeframeMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "High timeframe (minutes)", Description = "Fixed-minute bar period for the highest of the three timeframes checked for confluence.", Order = 2, GroupName = "Parameters")]
        public int HighTimeframeMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Min bars in trade", Description = "Minimum bars before the confluence-break exit is eligible to fire; the trailing stop stays active immediately regardless. Prevents the exit condition from flipping true again right after entry.", Order = 3, GroupName = "Parameters")]
        public int MinBarsInTrade { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Stop (ticks)", Description = "Initial protective stop distance; trailed by hand off the primary-timeframe PL Dot every bar afterward, never loosened. No separate profit target.", Order = 4, GroupName = "Parameters")]
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
        [Display(Name = "Show setup highlight", Description = "Diagnostic only, no effect on trading: marks any bar where all three dots are currently stacked the same direction with a small yellow dot below it, regardless of position state.", Order = 8, GroupName = "Parameters")]
        public bool ShowSetupHighlight { get; set; }
        #endregion
    }
}
