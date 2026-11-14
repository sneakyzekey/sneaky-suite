// ─────────────────────────────────────────────────────────────────────────────
// DrummondPLDotPullback_v1_0_0.cs
// ─────────────────────────────────────────────────────────────────────────────
//  Standalone NinjaTrader 8 strategy. No third-party dependencies, no other AddOns/services
//  required — this file compiles and runs entirely on its own.
//
//  WHAT THIS IS
//  ------------
//  A from-scratch, original NinjaScript implementation of a SECOND Drummond-Geometry-style setup,
//  a companion to [[DrummondCAPBreakout_v1_0_0]]: instead of trading a breakout OUT of congestion,
//  this trades a pullback TO the PL Dot in the direction of a higher-timeframe trend — commonly
//  described in Drummond Geometry community discussion as "trading with the dot." Where the CAP
//  Breakout strategy is a counter-congestion, breakout-of-a-node setup, this one is a trend-
//  continuation setup: it only fires when a higher timeframe is already trending, and treats a
//  pullback into the dot as a lower-risk place to join that trend rather than chase the breakout.
//
//  IMPORTANT SOURCING NOTE
//  ------------------------
//  Same discipline as [[DrummondCAPBreakout_v1_0_0]]: this is NOT a reproduction of Drummond
//  Geometry LLC's proprietary software, course material, or exact trading rules. What's public
//  (the "Drummond geometry" Wikipedia article, among other public sources) is the PL Dot formula
//  itself — "the average of the high, low, and close of the last three bars, displaced forward one
//  bar" — and the general community-discussed idea that the dot acts as a trend line traders buy/
//  sell pullbacks against. This file implements exactly that public-domain formula plus an
//  original, mechanical, first-cut definition of "trend," "pullback," and "bounce confirmation"
//  (spelled out below) — one reasonable interpretation of the general concept, not a claim to
//  replicate Drummond/Hearne's actual proprietary trading rules.
//
//  THE PATTERN, MECHANICALLY DEFINED
//  ----------------------------------
//    PL Dot (either timeframe):  3-bar average of (H+L+C)/3, displaced forward one bar — same
//                                 generic formula as the CAP Breakout strategy, computed here at
//                                 an arbitrary number of bars back so a trend slope can be read.
//    Higher-timeframe trend:     compare the current HTF PL Dot to the HTF PL Dot
//                                 TrendLookbackBars bars ago. Up if it rose by more than
//                                 TrendSlopeTicks, down if it fell by more than that — otherwise
//                                 no trend, and no trades are taken.
//    Pullback:                   within the last PullbackLookback bars (this timeframe), price
//                                 traded at or through the CURRENT PL Dot, within
//                                 TouchToleranceTicks — i.e. it came back to test the dot.
//    Bounce confirmation (v1.0.3):  two-bar reversal (outside bar). Within the pullback lookback
//                                 window, find the most recent bar that actually touched the dot
//                                 (the "touch bar"). The confirmation bar must close beyond THAT
//                                 touch bar's own high (long) or low (short) — a break of a real,
//                                 specific prior level, not a fixed tick distance or bar-range
//                                 fraction — AND close on the trend side of the dot itself.
//    Entry:                      with an HTF trend, a recent pullback to the dot, AND a bounce
//                                 confirmation bar, enter in the trend's direction.
//    Exit:                       three independent ways out, whichever comes first. (1) An ATR-based
//                                 stop — AtrPeriod-bar ATR × StopAtrMultiple — off the entry price,
//                                 recalculated and trailed by hand off this-timeframe's PL Dot every
//                                 bar afterward (never loosened; see changelog v1.0.4). (2) A fixed
//                                 profit target — AtrPeriod-bar ATR × ProfitTargetAtrMultiple — set
//                                 once at entry and left alone, so a winning trade can bank profit at
//                                 a defined level instead of depending entirely on the trail giving
//                                 some of it back first (see changelog v1.0.5; set the multiple to 0
//                                 to disable and fall back to stop/dot-fail-only exits). (3) An
//                                 immediate exit if a later bar closes back through the PL Dot against
//                                 the position — same dot-as-ongoing-arbiter idea as the CAP Breakout
//                                 strategy, since a decisive close back through the dot means the
//                                 pullback bounce has failed.
//
//  WHY MANAGED ORDERS, WHY THE TRADELONGS/TRADESHORTS TOGGLES
//  -------------------------------------------------------------
//  Same reasoning as [[DrummondCAPBreakout_v1_0_0]] for both: only one resting directional intent
//  at a time, so the standard managed approach (EnterLong/EnterShort + SetStopLoss) applies
//  directly with no unmanaged-order machinery needed. The TradeLongs/TradeShorts toggles are
//  carried over from day one this time (added to the CAP Breakout only after the fact, in v1.0.2)
//  because backtesting that strategy surfaced a real, repeated long/short asymmetry on NQ — worth
//  being able to test for the same thing here immediately rather than adding it later.
//
//  ⚠ FIRST CUT, NOT LIVE-TESTED OR BACKTESTED. Run it through the Strategy Analyzer (with a real
//  commission template — see [[DrummondCAPBreakout_v1_0_0]]'s notes on this) to get an honest
//  number for THIS specific rule set before trusting it with anything real.
//
//  Changelog
//    v1.0.0 (2026-09-07) — first cut.
//    v1.0.1 (2026-09-07) — fixed a runaway-overtrading bug: v1.0.0 entered right next to the same
//                          PL Dot line used for the "close back through the dot" exit, with no
//                          minimum holding period — ordinary bar-to-bar noise near the dot flipped
//                          positions in and out constantly (50,000+ trades/year in testing, avg
//                          under 1 bar per trade, profit factor ~0.91). Added a MinBarsInTrade
//                          parameter (default 5) — the dot-fail exit is now only eligible once
//                          that many bars have passed since entry. The trailing stop is unaffected
//                          and stays active immediately, so risk control is unchanged; this only
//                          delays the instant dot-cross exit specifically.
//    v1.0.2 (2026-09-07) — genetic-algorithm optimization of v1.0.1 (Trend slope 2-10, Touch
//                          tolerance 2-10, Bounce confirmation 2-14, Min bars in trade 3-15, Stop
//                          10-40, ~2,000 combos, real Lucid NQ commission modeled) found NO
//                          profitable configuration — every one of the top 10 results by profit
//                          factor came back at exactly PF 0.95, across trade counts ranging
//                          29,244-62,722. Same long/short asymmetry seen in DrummondCAPBreakout
//                          persisted (top row: longs -$100,122.50/PF 0.94, shorts -$47,479/PF 0.97).
//                          Concluded the tick-distance "close N ticks past the dot" definition of a
//                          bounce is fundamentally too permissive — it fires on any drift past the
//                          dot, real reversal or not. Replaced ConfirmTicks with RejectionFraction:
//                          the confirmation bar must now show an actual rejection candle shape (close
//                          in the upper/lower RejectionFraction of its own bar range), not just close
//                          past the dot by some tick count.
//    v1.0.3 (2026-09-08) — rejection-candle-shape (v1.0.2) tested no better: first backtest at
//                          defaults still overtraded (48,260 trades, avg bars in trade 0.93, PF
//                          0.93 — confirmed the near-instant exits were the Stop=10 hard stop, not
//                          the entry rule). A genetic sweep then found a near-breakeven top combo
//                          (PF 1.00, net -$4,298.50 in-sample), but it collapsed out-of-sample (PF
//                          0.86, net -$30,160.50 over 2 unseen months). A second, refined sweep
//                          found an even better in-sample combo (PF 1.03, net +$16,391 — the first
//                          PF >1.0 result across either Drummond strategy) — it ALSO collapsed
//                          out-of-sample (PF 0.87, net -$15,452.50). Two independent optimizer
//                          "winners" both failing the same held-out window confirmed the
//                          rejection-candle definition was being overfit, not finding a real edge.
//                          Replaced it with a two-bar reversal (outside bar): the confirmation bar
//                          must close beyond the specific touch bar's own high/low, not a fixed
//                          fraction of its own range or a fixed tick distance from the dot.
//                          RejectionFraction removed (no longer used).
//                          Plain backtest at defaults (Stop 10): still overtraded (39,223 trades,
//                          avg bars in trade 0.95, PF 0.94, net -$95,790.50) — better than v1.0.2's
//                          equivalent but the SAME instant-exit pattern persisted at the same Stop
//                          value. A genetic sweep widening Stop up to 10-80 confirmed the cause
//                          directly: at Stop 60-80, avg bars in trade jumped to 8.89 and win rate
//                          jumped from ~16% to 39.19% — proof the fixed-tick stop, not the entry
//                          logic, was capping every version at near-instant exits. Still net
//                          negative even there (PF 0.96, net -$109,386), and worse than the
//                          rejection-candle sweep's in-sample bests — not worth an out-of-sample
//                          validation run. Three different entry definitions (tick-distance,
//                          rejection-candle, outside-bar) all showed this same stop-driven ceiling.
//    v1.0.4 (2026-09-08) — replaced the fixed StopTicks distance with an ATR-based adaptive stop:
//                          new AtrPeriod (default 14) and StopAtrMultiple (default 2.0) parameters,
//                          stop = ATR(AtrPeriod) × StopAtrMultiple, computed fresh at entry and
//                          every bar of the trailing logic (so the distance scales with current
//                          volatility, not a flat tick count that stayed too tight regardless of
//                          how the entry itself was defined). StopTicks removed.
//                          Plain backtest at defaults: real trade-profile change (21,352 trades,
//                          avg bars in trade 5.75, win rate 44.85%, PF 0.95, net -$161,557) — much
//                          healthier-looking than any fixed-tick version, still net negative. A
//                          genetic sweep (Trend slope, Touch tolerance, Min bars, ATR period 7-21,
//                          Stop ATR multiple 1-4) found a top combo (Trend slope 2, Touch tolerance
//                          8, Min bars 13, ATR period 7, Stop multiple 4) at PF 0.98/net -$54,996
//                          in-sample — out-of-sample it held up unusually well (PF 0.97/net
//                          -$21,316.50, the smallest in-sample/out-of-sample gap seen in this whole
//                          project), though still not profitable. Its short-side subset was near
//                          breakeven out-of-sample (PF 1.00), which motivated testing TradeLongs/
//                          TradeShorts isolation.
//                          Isolating shorts (TradeLongs=false) unexpectedly collapsed trade COUNT
//                          far below its share of the combined run (5,954→380 in-sample, 1,425→1,005
//                          out-of-sample) — investigated as a possible bug, ruled out via a fresh
//                          Analyzer window (same result) and a longs-only control test, which showed
//                          the SAME collapse pattern (6,852→1,086 in-sample). Conclusion: this is a
//                          general effect of isolating one direction (the strategy is far more
//                          selective without the other direction's setups to grab first), not a bug.
//                          Longs-only, isolated, was briefly exciting — PF 1.07/net +$12,819
//                          in-sample, the first standalone-positive result in this project — but it
//                          collapsed out-of-sample too (PF 0.93/net -$22,371), the third optimizer/
//                          isolation "winner" to fail that check. Shorts-only isolated was stable
//                          but never profitable either way (PF 0.95 in-sample → PF 0.94
//                          out-of-sample).
//    v1.0.5 (2026-09-08) — every exit so far depended entirely on the ATR stop or the dot-fail close;
//                          a winning trade had no way to bank profit at a defined level, only ride
//                          until the trail (or a reversal back through the dot) caught up — possibly
//                          giving back a lot of open profit first. Added a fixed profit target: new
//                          ProfitTargetAtrMultiple parameter (default 3.0), target = ATR(AtrPeriod) ×
//                          ProfitTargetAtrMultiple, set once at entry via SetProfitTarget and left
//                          alone (unlike the stop, it does not trail). Set to 0 to disable and fall
//                          back to the old stop/dot-fail-only behavior. Not yet backtested.
// ─────────────────────────────────────────────────────────────────────────────
#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class DrummondPLDotPullback_v1_0_0 : Strategy
    {
        private const string EntryLong  = "PullbackLong";
        private const string EntryShort = "PullbackShort";

        private double _stopPrice;
        private int    _entryBar;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Mechanical, first-cut PL Dot pullback / trend-continuation setup — "
                            + "an original implementation of the publicly documented Drummond "
                            + "Geometry PL Dot math, not a reproduction of Drummond Geometry LLC's "
                            + "proprietary course rules. Companion to Drummond CAP Breakout. "
                            + "Standalone — no other files required.";
                Name = "Drummond PL Dot Pullback";

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
                IsUnmanaged                  = false;   // single directional entry at a time

                // First-cut starting values — NOT yet backtested. Unlike DrummondCAPBreakout_v1_0_0,
                // there's no optimizer-evidenced combination to seed these with yet; treat these as
                // a reasonable starting point to run through the Strategy Analyzer, not a claim.
                HigherTimeframeMinutes = 15;
                TrendLookbackBars      = 5;
                TrendSlopeTicks        = 4;
                PullbackLookback       = 5;
                TouchToleranceTicks    = 3;
                MinBarsInTrade         = 5;
                AtrPeriod              = 14;
                StopAtrMultiple        = 2.0;
                ProfitTargetAtrMultiple = 3.0;
                Contracts              = 1;
                TradeLongs             = true;
                TradeShorts            = true;

                // Visual verification only — these plots don't drive any trading logic, they just
                // let you watch the same dot values the strategy is reading every bar.
                AddPlot(Brushes.Yellow, "PL Dot");
                AddPlot(Brushes.Orange, "HTF PL Dot");
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
            if (CurrentBars[0] < PullbackLookback + 3) return;         // dot needs 3 bars, plus lookback
            if (CurrentBars[0] < AtrPeriod + 1) return;                // ATR needs its own warm-up
            if (CurrentBars[1] < TrendLookbackBars + 3) return;        // same, on the HTF series

            try
            {
                double dot        = PLDot(0, 0);
                double htfDotNow   = PLDot(1, 0);
                double htfDotBack  = PLDot(1, TrendLookbackBars);

                // ATR-based stop distance (v1.0.4) — recalculated every bar (both at entry and
                // during trade management below) so it scales with current volatility instead of
                // a flat tick count. Guarded against a zero/near-zero ATR reading (e.g. very early
                // in the series, or a data gap) by flooring at one tick.
                double atr          = ATR(AtrPeriod)[0];
                double stopDistance = Math.Max(atr * StopAtrMultiple, TickSize);

                // Visual verification only — see the changelog note above.
                Values[0][0] = dot;
                Values[1][0] = htfDotNow;

                bool htfUptrend   = (htfDotNow - htfDotBack) > TrendSlopeTicks * TickSize;
                bool htfDowntrend = (htfDotBack - htfDotNow) > TrendSlopeTicks * TickSize;

                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    if (htfUptrend || htfDowntrend)
                    {
                        // Find the most recent bar (within the lookback window) that actually
                        // touched the dot — the "touch bar." touchBarIdx stays 0 (meaning "none
                        // found") until a match is hit, then we stop scanning further back.
                        int touchBarIdx = 0;
                        for (int i = 1; i <= PullbackLookback; i++)
                        {
                            if (htfUptrend && Low[i] <= dot + TouchToleranceTicks * TickSize)
                            {
                                touchBarIdx = i;
                                break;
                            }
                            if (htfDowntrend && High[i] >= dot - TouchToleranceTicks * TickSize)
                            {
                                touchBarIdx = i;
                                break;
                            }
                        }

                        if (touchBarIdx > 0)
                        {
                            // Two-bar reversal / outside-bar confirmation (v1.0.3): the current bar
                            // must close beyond the touch bar's OWN high/low — a break of a real,
                            // specific level rather than a fixed tick distance or bar-range
                            // fraction — and on the trend side of the dot itself.
                            bool longReversal  = htfUptrend   && Close[0] > dot && Close[0] > High[touchBarIdx];
                            bool shortReversal = htfDowntrend && Close[0] < dot && Close[0] < Low[touchBarIdx];

                            if (TradeLongs && htfUptrend && longReversal)
                            {
                                EnterLong(Contracts, EntryLong);
                                _stopPrice = Instrument.MasterInstrument.RoundToTickSize(Close[0] - stopDistance);
                                SetStopLoss(EntryLong, CalculationMode.Price, _stopPrice, false);
                                string targetMsg = "";
                                if (ProfitTargetAtrMultiple > 0)
                                {
                                    double targetPrice = Instrument.MasterInstrument.RoundToTickSize(
                                        Close[0] + atr * ProfitTargetAtrMultiple);
                                    SetProfitTarget(EntryLong, CalculationMode.Price, targetPrice);
                                    targetMsg = ", target @ " + targetPrice.ToString("0.####");
                                }
                                _entryBar = CurrentBar;
                                Print("DrummondPLDotPullback LONG @ " + Close[0].ToString("0.####")
                                    + " — HTF uptrend, pullback to dot " + dot.ToString("0.####")
                                    + ", broke touch bar high " + High[touchBarIdx].ToString("0.####")
                                    + ", ATR stop dist " + stopDistance.ToString("0.####")
                                    + ", stop @ " + _stopPrice.ToString("0.####") + targetMsg);
                            }
                            else if (TradeShorts && htfDowntrend && shortReversal)
                            {
                                EnterShort(Contracts, EntryShort);
                                _stopPrice = Instrument.MasterInstrument.RoundToTickSize(Close[0] + stopDistance);
                                SetStopLoss(EntryShort, CalculationMode.Price, _stopPrice, false);
                                string targetMsg = "";
                                if (ProfitTargetAtrMultiple > 0)
                                {
                                    double targetPrice = Instrument.MasterInstrument.RoundToTickSize(
                                        Close[0] - atr * ProfitTargetAtrMultiple);
                                    SetProfitTarget(EntryShort, CalculationMode.Price, targetPrice);
                                    targetMsg = ", target @ " + targetPrice.ToString("0.####");
                                }
                                _entryBar = CurrentBar;
                                Print("DrummondPLDotPullback SHORT @ " + Close[0].ToString("0.####")
                                    + " — HTF downtrend, pullback to dot " + dot.ToString("0.####")
                                    + ", broke touch bar low " + Low[touchBarIdx].ToString("0.####")
                                    + ", ATR stop dist " + stopDistance.ToString("0.####")
                                    + ", stop @ " + _stopPrice.ToString("0.####") + targetMsg);
                            }
                        }
                    }
                    return;
                }

                // In position: the dot is the ongoing arbiter, not just the entry trigger — a
                // close back through it against us means the bounce has failed, exit outright
                // rather than waiting for the (wider) stop. BUT only once MinBarsInTrade bars have
                // elapsed since entry — without this, the position is entered right next to the
                // same dot line that triggers the exit, so ordinary bar-to-bar noise flips it back
                // and forth constantly (this is what caused the runaway 50,000+ trade/year result
                // in testing — see changelog v1.0.1). The trailing stop stays active the whole
                // time regardless; this only delays the instant dot-fail exit specifically.
                bool minHoldMet = (CurrentBar - _entryBar) >= MinBarsInTrade;

                if (Position.MarketPosition == MarketPosition.Long)
                {
                    if (minHoldMet && Close[0] < dot) { ExitLong(EntryLong); return; }

                    double candidate = Instrument.MasterInstrument.RoundToTickSize(dot - stopDistance);
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
                    if (minHoldMet && Close[0] > dot) { ExitShort(EntryShort); return; }

                    double candidate = Instrument.MasterInstrument.RoundToTickSize(dot + stopDistance);
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
            catch (Exception ex) { Print("DrummondPLDotPullback OnBarUpdate error: " + ex.Message); }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (marketPosition == MarketPosition.Flat)
            {
                _stopPrice = 0;
                _entryBar  = 0;
            }
        }

        // ── PL Dot: 3-bar average of (H+L+C)/3, displaced forward one bar. Public-domain formula
        //    (see the sourcing note above), computed generically so it works for either series and
        //    at an arbitrary number of bars back — needed here to read a trend slope, unlike the
        //    CAP Breakout strategy which only ever needed the current value. ──
        private double PLDot(int seriesIndex, int barsAgo)
        {
            double sum = 0;
            for (int i = 0; i < 3; i++)
                sum += (Highs[seriesIndex][barsAgo + i] + Lows[seriesIndex][barsAgo + i] + Closes[seriesIndex][barsAgo + i]) / 3.0;
            return sum / 3.0;
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Higher timeframe (minutes)", Description = "Fixed-minute bar period for the second, higher timeframe used to read the trend.", Order = 1, GroupName = "Parameters")]
        public int HigherTimeframeMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Trend lookback (HTF bars)", Description = "How many higher-timeframe bars back to compare the HTF PL Dot against, to read its slope.", Order = 2, GroupName = "Parameters")]
        public int TrendLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Trend slope (ticks)", Description = "Minimum HTF PL Dot movement over the trend lookback to call it trending, not flat.", Order = 3, GroupName = "Parameters")]
        public int TrendSlopeTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Pullback lookback (bars)", Description = "How many recent bars (this timeframe) to check for a touch of the PL Dot.", Order = 4, GroupName = "Parameters")]
        public int PullbackLookback { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Touch tolerance (ticks)", Description = "How close price must come to the PL Dot to count as a pullback touch.", Order = 5, GroupName = "Parameters")]
        public int TouchToleranceTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Min bars in trade", Description = "Bars that must elapse after entry before the 'close back through the dot' exit is allowed to fire. Without this, entries right next to the dot get flipped out by ordinary bar-to-bar noise (see changelog v1.0.1). The trailing stop stays active regardless.", Order = 6, GroupName = "Parameters")]
        public int MinBarsInTrade { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "ATR period (bars)", Description = "Bars used to compute the ATR that the stop distance is based on (see changelog v1.0.4).", Order = 7, GroupName = "Parameters")]
        public int AtrPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Stop (ATR multiple)", Description = "Initial protective stop distance = ATR(AtrPeriod) × this multiple, recalculated every bar and trailed by hand off the PL Dot afterward (never loosened). Replaces the old fixed-tick Stop parameter (see changelog v1.0.4) so the distance scales with current volatility instead of a flat tick count.", Order = 8, GroupName = "Parameters")]
        public double StopAtrMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Profit target (ATR multiple)", Description = "Fixed profit target = ATR(AtrPeriod) × this multiple, set once at entry and left alone (does not trail, unlike the stop). Lets a winning trade bank profit at a defined level instead of depending entirely on the trail or a dot-fail exit. Set to 0 to disable and fall back to stop/dot-fail-only exits (see changelog v1.0.5).", Order = 9, GroupName = "Parameters")]
        public double ProfitTargetAtrMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Contracts", Description = "Size of each entry.", Order = 10, GroupName = "Parameters")]
        public int Contracts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trade longs", Description = "Allow long entries. Turn off to run a short-only variant without touching any other logic.", Order = 11, GroupName = "Parameters")]
        public bool TradeLongs { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trade shorts", Description = "Allow short entries. Turn off to run a long-only variant without touching any other logic.", Order = 12, GroupName = "Parameters")]
        public bool TradeShorts { get; set; }
        #endregion
    }
}
