#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

// FORK NOTE (2026-10-26) -- TurtleTrendBreakout_v1_1_0
// Forked from TurtleTrendBreakout_v1_0_0. That file is unchanged.
// Prop-firm accounts commonly reject OCO orders. v1_0_0 rests a buy stop and a sell
// stop in one OCO group so a fill on one side cancels the other. This fork does not
// send that pair. The Donchian high and low are kept locally as targets. When price
// crosses one target, a single market order is sent for that side only. The other
// side is never submitted. The ATR protective stop and trail are unchanged and are
// still one stop order, not an OCO. Price must trade back inside the channel before
// a new breakout can arm, so enabling the strategy while price is already outside
// the channel does not immediately send a market order.
// 2026-10-13 -- OCO id now includes a GUID so it can't be reused after a restart.
// 2026-10-12 -- stop moves made from SneakyTradeHUD / chart drag are no longer reverted (see SyncManualStopMove).
//
// 2026-09-19 -- first cut of a classic trend-following breakout system, in the spirit of
// the Turtle Trading rules (Donchian-channel breakout entry, ATR-based initial stop,
// ATR-based TRAILING stop instead of a fixed profit target so winners aren't capped, no
// mean-reversion assumption anywhere in the logic). This is the opposite trade philosophy
// from KeltnerBandFade_TVKC: that strategy bets on price reverting to a middle band and
// gets hurt by strong trends; this one bets on trends continuing once confirmed and
// accepts a low win rate in exchange for occasional large winners. Consistency here comes
// from many small, capped losses plus a few big wins, NOT from a high hit rate -- don't
// judge this strategy's health by win rate the way you might judge a fade strategy.
//
// IMPORTANT SCOPE NOTE: the "spread risk across many uncorrelated markets" part of the
// classic Turtle approach is a PORTFOLIO decision, not something a single NinjaScript file
// can do by itself -- this file trades ONE instrument. To get the diversification benefit,
// run separate instances of this same strategy on multiple genuinely uncorrelated
// instruments (e.g., not NQ and ES together, since those move nearly in lockstep) rather
// than expecting one chart to do it. This file is the single-market building block.
//
// This has NOT been backtested yet -- treat every default below as a reasonable starting
// point pulled from the general shape of published trend-following systems, not a number
// tuned to your data. Run it through the Strategy Analyzer before trusting it with size.
//
// 2026-09-20 -- UNMANAGED ORDER REWRITE. The original version used the managed approach
// (EnterLongStopMarket/EnterShortStopMarket/SetStopLoss) and tried to keep a resting buy
// stop AND a resting sell stop working at the same time -- that's the whole point of a
// breakout straddle. NinjaTrader's managed order engine has a built-in safety feature
// called the "Internal Order Handling Rules that Reduce Unwanted Positions": while flat,
// if an Enter() order in one direction is already working, a second Enter() call for the
// OPPOSITE direction is silently ignored. Confirmed live via the Output window:
//   "An Enter() method to submit an entry order ... has been ignored. Please search on
//    the term 'Internal Order Handling Rules that Reduce Unwanted Positions'..."
// That's why only the long-side stop order was ever visible on the chart -- the short
// side was being dropped by NinjaTrader itself, every single bar, regardless of
// AllowShort being enabled. NinjaTrader's own documented fix for needing two genuinely
// simultaneous opposing entry orders is the UNMANAGED approach: submit orders directly
// via SubmitOrderUnmanaged with a shared OCO id so a fill on one side auto-cancels the
// other, and manage every order/position transition yourself instead of relying on
// Enter()/Exit()/SetStopLoss() helpers (which don't work once IsUnmanaged = true).
// Position.MarketPosition/Position.Quantity are still maintained automatically by
// NinjaTrader in unmanaged mode -- only the ORDER objects need manual tracking here.
// IsExitOnSessionCloseStrategy also still functions under IsUnmanaged = true (confirmed
// against NinjaTrader's own guidance), so that setting was left as-is.
namespace NinjaTrader.NinjaScript.Strategies
{
    public class TurtleTrendBreakout_v1_1_0 : Strategy
    {
        private ATR atr;

        private int tradesToday;

        // The one entry order sent after a target is hit. Nothing rests on the other side.
        private Order entryOrder;
        private bool entryInFlight;

        // Armed only after price has traded back inside the channel. A cross of these
        // stored prices is what sends the order. Updating them only while inside keeps a
        // rolling Donchian level from jumping through price and firing by itself.
        private bool targetsArmed;
        private double armedLongTarget;
        private double armedShortTarget;
        private double lastPrice;
        private bool haveLastPrice;

        // Protective stop for whichever side is actually open. Only one of these is ever
        // live at a time, matching Position.MarketPosition.
        private Order longStopOrder;
        private Order shortStopOrder;

        private string debugLogPath;

        private int entryBar = -1;
        private double highestHighSinceEntry;
        private double lowestLowSinceEntry;
        private double trailStopPrice;
        private DateTime lastOwnStopChange = DateTime.MinValue;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "TurtleTrendBreakout_v1_1_0";
                Description = "Donchian breakout targets watched locally. One market entry when price crosses a target. ATR initial stop, ATR trailing stop, no OCO.";

                // This chart's shared NinjaScript Output window is flooded with unrelated
                // "[Sneaky:...]" diagnostic prints from the rest of the Sneaky Suite (dozens
                // of lines per second), which buried this strategy's own debug lines even
                // after routing them to a separate Output tab. Kept here as a harmless
                // secondary channel, but the real fix is LogDebug() below, which writes
                // straight to its own text file instead -- see debugLogPath in
                // State.DataLoaded and the LogDebug method for the primary channel.
                PrintTo = PrintTo.OutputTab1;

                Calculate = Calculate.OnEachTick;

                // EntriesPerDirection/EntryHandling are managed-approach concepts and have
                // no effect now that IsUnmanaged = true below -- left in place as a record
                // of the original intent (max one open position per direction at a time),
                // which the unmanaged logic below enforces itself via Position.MarketPosition.
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;

                // Classic trend-following normally holds positions across many sessions --
                // that's part of where the "let winners run" edge comes from, since the
                // biggest trends span days or weeks, not one session. Defaulting to
                // session-close flattening here anyway, to match how every other strategy
                // in this Lab has been run so far (day session, flatten and reset). Toggle
                // IsExitOnSessionCloseStrategy off in Properties if you want to test this
                // closer to how it's traditionally meant to run.
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = true;

                // Still unmanaged so the protective stop can be submitted and trailed
                // directly. Entries are no longer a resting OCO straddle.
                IsUnmanaged = true;

                EntryChannelLength = 20;
                AllowLong = true;
                AllowShort = true;
                ShowRangeBox = true;

                AtrLength = 20;
                InitialStopAtrMultiple = 2.0;
                TrailAtrMultiple = 3.0;
                UseVolatilityBasedSizing = false;
                RiskPerTradeDollars = 500;
                MaxContractsCap = 10;

                TradeQuantity = 1;
                MaxTradesPerDay = 5;
                StartTime = 0;
                EndTime = 235959;

                DebugPrints = true;
                RespectManualStopMoves = true;

                BarsRequiredToTrade = 25;
            }
            else if (State == State.DataLoaded)
            {
                atr = ATR(AtrLength);

                // Plain text file instead of the NinjaScript Output window -- this chart's
                // Output window is flooded by ~40 other unrelated indicators/strategies
                // (the Sneaky Suite) printing dozens of lines per second, which made this
                // strategy's own debug lines effectively unreadable even after trying
                // PrintTo to move them to a separate Output tab. Writing straight to a
                // dedicated file sidesteps the Output window entirely -- open it directly
                // in Notepad, or just tell me the path and I can walk through it with you.
                debugLogPath = System.IO.Path.Combine(
                    NinjaTrader.Core.Globals.UserDataDir, "log", "TurtleTrendBreakout_v1_1_0_Debug.txt");
            }
        }

        /// <summary>
        /// Writes one line to this strategy's own dedicated debug log file (see
        /// debugLogPath above) instead of the shared, flooded NinjaScript Output window.
        /// Swallows any file I/O error so a locked/unavailable log file can never take
        /// down the strategy itself -- logging is a nice-to-have, not something that
        /// should ever be allowed to interrupt real order management.
        /// </summary>
        private void LogDebug(string message)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(debugLogPath));
                System.IO.File.AppendAllText(debugLogPath, message + Environment.NewLine);
            }
            catch (Exception)
            {
                // Logging is best-effort only -- never let a file I/O problem interrupt
                // live order management.
            }
        }

        protected override void OnBarUpdate()
        {
            // Never let historical replay compute a "virtual" position on enable -- that's
            // what was causing the Control Center row to sit yellow and, with StartBehavior
            // set to "Immediately Submit, Sync Account," what caused an unwanted real market
            // order to fire the instant this strategy was turned on. Skipping all entry/exit
            // logic during State.Historical guarantees this strategy is always flat the
            // moment it goes live, so it comes up green immediately with no forced sync
            // order -- at the cost of never trying to reconcile a real pre-existing account
            // position for you (there won't be one to reconcile, since it never enters
            // anything until real-time bars start arriving).
            if (State == State.Historical)
                return;

            if (CurrentBar < Math.Max(EntryChannelLength, AtrLength) + 1)
                return;

            if (Bars.IsFirstBarOfSession)
            {
                tradesToday = 0;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                ManageTrailingStop();
                return;
            }

            // Flat: no protective stop should be resting -- defensive cleanup in case one
            // is somehow still working from a race with the exit that just flattened us.
            CancelWorkingOrder(ref longStopOrder);
            CancelWorkingOrder(ref shortStopOrder);

            double px = Close[0];
            int currentTime = ToTime(Time[0]);
            bool windowOpen = currentTime >= StartTime && currentTime <= EndTime;

            // Donchian channel over the PRIOR N bars -- [1] deliberately excludes the bar
            // that's still forming so the breakout level isn't computed from data that
            // includes the very bar being tested against it.
            double donchianHigh = Instrument.MasterInstrument.RoundToTickSize(MAX(High, EntryChannelLength)[1]);
            double donchianLow = Instrument.MasterInstrument.RoundToTickSize(MIN(Low, EntryChannelLength)[1]);

            // Highlight the exact lookback window that produced the levels above -- the
            // same "prior N bars, [1] excludes the forming bar" window used just above.
            // Re-drawing with the SAME tag every bar (rather than a fresh tag per bar)
            // makes NinjaTrader move this one box forward instead of stacking a new one,
            // so watching it live shows the window sliding/rolling as each bar closes --
            // exactly the "playing out in real time" effect, not a permanent trail of
            // boxes cluttering the chart across days of history.
            if (ShowRangeBox && IsFirstTickOfBar)
            {
                Draw.Rectangle(this, "TurtleRangeBox", false,
                    EntryChannelLength, donchianHigh, 1, donchianLow,
                    Brushes.DodgerBlue, Brushes.DodgerBlue, 15);
            }

            int quantity = ComputeQuantity();
            if (!windowOpen || tradesToday >= MaxTradesPerDay || quantity < 1)
            {
                // Drop the arm so the first tick back inside the rules cannot fire on a
                // level price already crossed while entries were blocked.
                targetsArmed = false;
                CancelWorkingOrder(ref entryOrder);
                lastPrice = px;
                haveLastPrice = true;
                return;
            }

            if (entryInFlight || (entryOrder != null && IsWorking(entryOrder)))
            {
                lastPrice = px;
                haveLastPrice = true;
                return;
            }

            bool inside = px < donchianHigh && px > donchianLow;
            if (inside)
            {
                armedLongTarget = donchianHigh;
                armedShortTarget = donchianLow;
                targetsArmed = true;
            }
            else if (targetsArmed && haveLastPrice)
            {
                bool crossedLong = lastPrice < armedLongTarget && px >= armedLongTarget;
                bool crossedShort = lastPrice > armedShortTarget && px <= armedShortTarget;

                if (crossedLong && crossedShort)
                {
                    // One tick jumped the whole channel. Either side would be a guess.
                    targetsArmed = false;
                    if (DebugPrints)
                        LogDebug(Time[0] + " | TARGET AMBIGUOUS | Px: " + px.ToString("0.00")
                            + " | Long: " + armedLongTarget.ToString("0.00")
                            + " | Short: " + armedShortTarget.ToString("0.00"));
                }
                else if (crossedLong && AllowLong)
                    SubmitBreakoutEntry(OrderAction.Buy, quantity, armedLongTarget, "TrendLong");
                else if (crossedShort && AllowShort)
                    SubmitBreakoutEntry(OrderAction.SellShort, quantity, armedShortTarget, "TrendShort");
            }

            if (DebugPrints && IsFirstTickOfBar)
            {
                LogDebug(Time[0]
                    + " | TARGETS"
                    + " | Qty: " + quantity
                    + " | Armed: " + targetsArmed
                    + " | Long: " + (targetsArmed ? armedLongTarget : donchianHigh).ToString("0.00")
                    + " | Short: " + (targetsArmed ? armedShortTarget : donchianLow).ToString("0.00")
                    + " | Px: " + px.ToString("0.00")
                    + " | ATR: " + atr[0].ToString("0.00"));
            }

            lastPrice = px;
            haveLastPrice = true;
        }

        /// <summary>
        /// Sends the single entry for the target price just crossed. No OCO id is passed.
        /// The arm is consumed so later ticks while price stays through the level do not
        /// send another order. A new arm happens only after price trades back inside.
        /// </summary>
        private void SubmitBreakoutEntry(OrderAction action, int quantity, double target, string signalName)
        {
            targetsArmed = false;
            entryInFlight = true;
            try
            {
                entryOrder = SubmitOrderUnmanaged(0, action, OrderType.Market, quantity, 0, 0, string.Empty, signalName);
                if (entryOrder == null)
                {
                    entryInFlight = false;
                    LogDebug(Time[0] + " | ENTRY FAILED | Name: " + signalName
                        + " | Target: " + target.ToString("0.00")
                        + " | Error: Submit returned no order");
                    return;
                }
                if (DebugPrints)
                    LogDebug(Time[0] + " | TARGET HIT | Name: " + signalName
                        + " | Target: " + target.ToString("0.00")
                        + " | Px: " + Close[0].ToString("0.00")
                        + " | Qty: " + quantity);
            }
            catch (Exception ex)
            {
                entryInFlight = false;
                entryOrder = null;
                LogDebug(Time[0] + " | ENTRY FAILED | Name: " + signalName
                    + " | Target: " + target.ToString("0.00")
                    + " | Error: " + ex.Message);
            }
        }

        /// <summary>
        /// 2026-09-29 -- the try/catch this used to rely on (see git history / debug log
        /// notes below) NEVER ACTUALLY CAUGHT ANYTHING. Confirmed live: the strategy still
        /// hit "Sim105, Stop price can't be changed above the market" and got auto-
        /// terminated by NinjaTrader ("submitted an order that generated the following
        /// error ... Strategy has sent cancel requests, attempted to close the position and
        /// terminated itself") with this exact try/catch already in place around the call
        /// that supposedly triggered it. Root cause: NinjaTrader validates a stop price
        /// against the market ASYNCHRONOUSLY, on its own order-processing thread, well
        /// after SubmitOrderUnmanaged/ChangeOrder already returned -- not synchronously
        /// inside that call. A try/catch around the call can only ever catch a throw from
        /// that call itself, so it was structurally incapable of catching this.
        ///
        /// Real fix: never SEND an invalid price in the first place. "Stop price can't be
        /// changed above the market" for a SellShort order is a Donchian-channel artifact:
        /// donchianLow = MIN(Low, Length)[1] is a ROLLING window, so when the bar holding
        /// the current window's lowest low finally ages out of the lookback, donchianLow
        /// can jump UP even though price itself hasn't moved up at all -- if price is
        /// currently trading below that new, higher donchianLow, re-quoting the short-entry
        /// sell-stop to it is asking for a sell-stop ABOVE the market, which is invalid by
        /// definition (a sell-stop only makes sense below market; it triggers on price
        /// FALLING through it). Same failure mode in the other direction for donchianHigh
        /// dropping below market and a buy-stop entry. IsStopSideValid below checks the
        /// correct side of GetCurrentAsk/GetCurrentBid (falling back to Close[0] if either
        /// reads 0, e.g. between ticks) before ever calling Submit/ChangeOrder, and both
        /// requote methods now just skip the requote for this tick when it fails -- the
        /// existing resting order (or no order yet) stays exactly as-is, and the next tick's
        /// fresh recompute gets another chance once price and the breakout level agree on a
        /// side again. The try/catch stays too, as a backstop for any other kind of
        /// rejection this check doesn't anticipate (e.g. a broker minimum-distance rule) --
        /// see each method's original comment for why that path is still worth keeping.
        /// </summary>
        // 2026-09-29 (third pass) -- IsStopSideValid was checking the right side of the
        // market with ZERO margin (a bare stopPrice > reference / < reference), which is
        // still a race: NinjaTrader validates the actual submit/change ASYNCHRONOUSLY, on
        // its own thread, a short time after this check runs and returns. Confirmed live --
        // "Sim105, Stop price can't be changed above the market" on a Sell (protective long
        // stop) that this check would have called valid moments earlier, while the trailing
        // stop was chasing a fast-moving trend tick by tick. A few ticks of buffer costs
        // nothing on an ordinary, slower requote and gives the async validation enough room
        // to still agree with this check by the time it actually runs.
        private const int StopSideSafetyTicks = 3;

        private bool IsStopSideValid(OrderAction action, double stopPrice)
        {
            bool isBuySide = action == OrderAction.Buy || action == OrderAction.BuyToCover;
            double reference = isBuySide
                ? (GetCurrentAsk(0) > 0 ? GetCurrentAsk(0) : Close[0])
                : (GetCurrentBid(0) > 0 ? GetCurrentBid(0) : Close[0]);
            double buffer = TickSize * StopSideSafetyTicks;

            // Buy-side stops (short-cover protective stop) must sit ABOVE the market.
            // Sell-side stops (long protective stop) must sit BELOW it, by at least
            // StopSideSafetyTicks, not just barely. Entry orders are markets, not stops.
            return isBuySide ? stopPrice > reference + buffer : stopPrice < reference - buffer;
        }

        /// <summary>
        /// Protective stop for the open position. One order, no OCO id.
        /// </summary>
        private void SubmitOrRequoteStop(ref Order order, OrderAction action, int quantity, double stopPrice, string signalName)
        {
            if (!IsStopSideValid(action, stopPrice))
            {
                LogDebug(Time[0] + " | REQUOTE SKIPPED (wrong side of market) | Name: " + signalName
                    + " | RequestedStop: " + stopPrice.ToString("0.00")
                    + " | Close: " + Close[0].ToString("0.00"));
                return;
            }

            try
            {
                if (order == null || !IsWorking(order))
                {
                    lastOwnStopChange = DateTime.UtcNow;
                    order = SubmitOrderUnmanaged(0, action, OrderType.StopMarket, quantity, 0, stopPrice, string.Empty, signalName);
                }
                else if (Math.Abs(order.StopPrice - stopPrice) >= TickSize * 0.5)
                {
                    lastOwnStopChange = DateTime.UtcNow;
                    ChangeOrder(order, quantity, order.LimitPrice, stopPrice);
                }
            }
            catch (Exception ex)
            {
                // Backstop for a rejection IsStopSideValid does not anticipate.
                LogDebug(Time[0] + " | REQUOTE FAILED | Name: " + signalName
                    + " | RequestedStop: " + stopPrice.ToString("0.00")
                    + " | Close: " + Close[0].ToString("0.00")
                    + " | Error: " + ex.Message);
            }
        }

        private void CancelWorkingOrder(ref Order order)
        {
            if (order != null && IsWorking(order))
                CancelOrder(order);
        }

        private static bool IsWorking(Order order)
        {
            // ChangePending/ChangeSubmitted MUST count as "already alive" here. Missing
            // these caused a real bug: with Calculate.OnEachTick, OnBarUpdate fires many
            // times per bar, and a re-quote (ChangeOrder) briefly passes through these two
            // states before landing back on Accepted. Without them, a tick that happened
            // to land mid-change saw IsWorking() return false and fired a brand new
            // SubmitOrderUnmanaged call on top of the order still being changed -- exactly
            // what produced two simultaneous "Buy STP" lines on the chart (the original
            // order, orphaned and frozen the moment this bug first fired, plus the
            // accidental duplicate that the stop ref kept re-quoting
            // afterward).
            return order.OrderState == OrderState.Working
                || order.OrderState == OrderState.Accepted
                || order.OrderState == OrderState.Submitted
                || order.OrderState == OrderState.ChangePending
                || order.OrderState == OrderState.ChangeSubmitted;
        }

        /// <summary>
        /// 2026-10-12 -- FIX: stop moves made from outside the strategy (SneakyTradeHUD
        /// Lock 50% / BE / Trail now / SL nudge, or dragging the stop on the chart) were being
        /// undone. ManageTrailingStop only compared its candidate against its OWN remembered
        /// trailStopPrice, never against the live order, so after the HUD tightened the stop
        /// the strategy's next trail improvement (still looser than the HUD's price) was sent
        /// via ChangeOrder and overwrote it. Now, whenever the working protective stop differs
        /// from what this strategy last set (and the strategy did not touch it in the last
        /// 2 s), the live price is adopted as the new baseline -- tighter OR looser -- and the
        /// trail continues from there, only ever tightening past it.
        /// </summary>
        private void SyncManualStopMove()
        {
            if (!RespectManualStopMoves)
                return;
            if ((DateTime.UtcNow - lastOwnStopChange).TotalSeconds < 2.0)
                return;

            Order o = Position.MarketPosition == MarketPosition.Long ? longStopOrder
                    : Position.MarketPosition == MarketPosition.Short ? shortStopOrder : null;
            if (o == null)
                return;
            if (o.OrderState != OrderState.Working && o.OrderState != OrderState.Accepted)
                return;

            double live = Instrument.MasterInstrument.RoundToTickSize(o.StopPrice);
            if (live <= 0 || Math.Abs(live - trailStopPrice) < TickSize * 0.5)
                return;

            if (DebugPrints)
                LogDebug(Time[0] + " | MANUAL STOP MOVE ADOPTED | " + trailStopPrice.ToString("0.00")
                    + " -> " + live.ToString("0.00"));
            trailStopPrice = live;
        }

        /// <summary>
        /// No SetProfitTarget/fixed target anywhere in this file -- that's deliberate. The
        /// edge this strategy is built around depends on letting a real trend run
        /// uncapped; a fixed target would cut off exactly the large winners that are
        /// supposed to pay for all the small, capped losses. The only exit is this
        /// trailing stop (or the initial ATR stop before the trail has moved past it).
        /// </summary>
        private void ManageTrailingStop()
        {
            if (entryBar < 0)
                return;

            SyncManualStopMove();

            if (Position.MarketPosition == MarketPosition.Long)
            {
                highestHighSinceEntry = Math.Max(highestHighSinceEntry, High[0]);
                // Round BEFORE comparing -- see "TRAIL COMPARE ROUNDING BUG" fix note below.
                double candidateStop = Instrument.MasterInstrument.RoundToTickSize(highestHighSinceEntry - TrailAtrMultiple * atr[0]);

                // Ratchet only -- never loosen a stop that's already better than this. Fixed
                // 2026-09-24: this used to compare the RAW (unrounded) candidateStop against
                // the already-rounded trailStopPrice. Since atr[0]/highestHighSinceEntry are
                // often unchanged from one tick to the next within a bar, the raw candidate
                // recomputes to the exact same value every tick -- and because RoundToTickSize
                // rounds that value DOWN slightly more often than not, "candidateStop >
                // trailStopPrice" kept evaluating true for a price that hadn't actually
                // improved, re-entering this block (and re-writing the debug log line) on
                // nearly every tick. SubmitOrRequoteStop's own half-tick threshold check
                // stopped this from spamming the broker with real order changes, but it still
                // spammed the debug log -- confirmed in TurtleTrendBreakout_Debug.txt, where a
                // single unchanged trail price ("LONG TRAIL -> 30614.25") was logged 8,000+
                // times in a row at the identical timestamp, ballooning that file to 9MB/180k+
                // lines in under 4 days. Comparing two already-rounded values below fixes it:
                // a rounded price only differs from the last rounded price when the market has
                // actually moved at least half a tick.
                if (candidateStop > trailStopPrice)
                {
                    trailStopPrice = candidateStop;
                    SubmitOrRequoteStop(ref longStopOrder, OrderAction.Sell, Position.Quantity, trailStopPrice, "TrendLongStop");

                    if (DebugPrints)
                        LogDebug(Time[0] + " | LONG TRAIL -> " + trailStopPrice.ToString("0.00"));
                }
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                lowestLowSinceEntry = Math.Min(lowestLowSinceEntry, Low[0]);
                double candidateStop = Instrument.MasterInstrument.RoundToTickSize(lowestLowSinceEntry + TrailAtrMultiple * atr[0]);

                if (candidateStop < trailStopPrice)
                {
                    trailStopPrice = candidateStop;
                    SubmitOrRequoteStop(ref shortStopOrder, OrderAction.BuyToCover, Position.Quantity, trailStopPrice, "TrendShortStop");

                    if (DebugPrints)
                        LogDebug(Time[0] + " | SHORT TRAIL -> " + trailStopPrice.ToString("0.00"));
                }
            }
        }

        /// <summary>
        /// Fixed contract count by default (TradeQuantity), matching the rest of this
        /// Lab's strategies. UseVolatilityBasedSizing switches to the classic Turtle "N"
        /// approach instead: size so that a full stop-out costs roughly RiskPerTradeDollars,
        /// regardless of current volatility -- a quiet market gets more contracts, a wild
        /// one gets fewer, so risk per trade stays roughly constant rather than position
        /// size staying constant while risk swings with volatility. MaxContractsCap is a
        /// safety rail in case a very low ATR reading would otherwise produce an
        /// unreasonably large size.
        /// </summary>
        private int ComputeQuantity()
        {
            if (!UseVolatilityBasedSizing)
                return TradeQuantity;

            double stopDistancePoints = InitialStopAtrMultiple * atr[0];
            if (stopDistancePoints <= 0)
                return 0;

            double dollarsPerContract = stopDistancePoints * Instrument.MasterInstrument.PointValue;
            if (dollarsPerContract <= 0)
                return 0;

            int qty = (int)Math.Floor(RiskPerTradeDollars / dollarsPerContract);
            return Math.Max(1, Math.Min(qty, MaxContractsCap));
        }

        protected override void OnExecutionUpdate(
            Execution execution,
            string executionId,
            double price,
            int quantity,
            MarketPosition marketPosition,
            string orderId,
            DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            if (execution.Order.OrderState != OrderState.Filled)
                return;

            if (execution.Order.Name == "TrendLong")
            {
                if (entryBar >= 0)
                    return;

                entryBar = CurrentBar;
                highestHighSinceEntry = price;
                trailStopPrice = Instrument.MasterInstrument.RoundToTickSize(
                    price - InitialStopAtrMultiple * atr[0]);
                SubmitOrRequoteStop(ref longStopOrder, OrderAction.Sell, quantity, trailStopPrice, "TrendLongStop");

                tradesToday++;

                if (DebugPrints)
                {
                    LogDebug(Time[0]
                        + " | LONG FILL"
                        + " | Fill: " + price.ToString("0.00")
                        + " | Initial Stop: " + trailStopPrice.ToString("0.00"));
                }
            }
            else if (execution.Order.Name == "TrendShort")
            {
                if (entryBar >= 0)
                    return;

                entryBar = CurrentBar;
                lowestLowSinceEntry = price;
                trailStopPrice = Instrument.MasterInstrument.RoundToTickSize(
                    price + InitialStopAtrMultiple * atr[0]);
                SubmitOrRequoteStop(ref shortStopOrder, OrderAction.BuyToCover, quantity, trailStopPrice, "TrendShortStop");

                tradesToday++;

                if (DebugPrints)
                {
                    LogDebug(Time[0]
                        + " | SHORT FILL"
                        + " | Fill: " + price.ToString("0.00")
                        + " | Initial Stop: " + trailStopPrice.ToString("0.00"));
                }
            }
        }

        /// <summary>
        /// Prints to the Output window any time an order for this strategy is rejected or
        /// reports an error (this is what surfaced the "Internal Order Handling Rules"
        /// message that explained the missing short orders under the old managed version).
        /// Also clears our tracked Order references the moment an order reaches a terminal
        /// state (Filled/Cancelled/Rejected), which is how OnBarUpdate/ManageTrailingStop
        /// know it's safe to submit a fresh one instead of trying to re-quote a dead order.
        /// </summary>
        protected override void OnOrderUpdate(
            Order order,
            double limitPrice,
            double stopPrice,
            int quantity,
            int filled,
            double averageFillPrice,
            OrderState orderState,
            DateTime time,
            ErrorCode error,
            string nativeError)
        {
            if (orderState == OrderState.Rejected || error != ErrorCode.NoError)
            {
                LogDebug(time
                    + " | ORDER ISSUE"
                    + " | Name: " + order.Name
                    + " | Action: " + order.OrderAction
                    + " | State: " + orderState
                    + " | Error: " + error
                    + " | Native: " + nativeError
                    + " | StopPrice: " + stopPrice.ToString("0.00"));
            }
            else if (DebugPrints && (order.Name == "TrendLong" || order.Name == "TrendShort"
                || order.Name == "TrendLongStop" || order.Name == "TrendShortStop"))
            {
                LogDebug(time
                    + " | ORDER STATE"
                    + " | Name: " + order.Name
                    + " | State: " + orderState
                    + " | StopPrice: " + stopPrice.ToString("0.00"));
            }

            if (orderState == OrderState.Filled || orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
            {
                if (order == entryOrder)
                {
                    entryOrder = null;
                    entryInFlight = false;
                }
                else if (order == longStopOrder)
                    longStopOrder = null;
                else if (order == shortStopOrder)
                    shortStopOrder = null;
            }
        }

        protected override void OnPositionUpdate(
            Position position,
            double averagePrice,
            int quantity,
            MarketPosition marketPosition)
        {
            if (marketPosition == MarketPosition.Flat)
            {
                entryBar = -1;
                highestHighSinceEntry = 0;
                lowestLowSinceEntry = double.MaxValue;
                trailStopPrice = 0;

                // Defensive -- OnOrderUpdate normally clears these as each order hits a
                // terminal state, but a flatten from IsExitOnSessionCloseStrategy or the
                // Flat button submits its own order outside our tracking, so make sure we
                // don't try to re-quote/cancel anything stale next time we're flat.
                entryOrder = null;
                entryInFlight = false;
                targetsArmed = false;
                longStopOrder = null;
                shortStopOrder = null;
            }
        }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Entry Channel Length", GroupName = "Entry", Order = 1)]
        public int EntryChannelLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow Long", GroupName = "Entry", Order = 2)]
        public bool AllowLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow Short", GroupName = "Entry", Order = 3)]
        public bool AllowShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Range Box", GroupName = "Entry", Order = 4)]
        public bool ShowRangeBox { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "ATR Length", GroupName = "Risk", Order = 1)]
        public int AtrLength { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Initial Stop (ATR multiple)", GroupName = "Risk", Order = 2)]
        public double InitialStopAtrMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Trail Stop (ATR multiple)", GroupName = "Risk", Order = 3)]
        public double TrailAtrMultiple { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Volatility-Based Sizing", GroupName = "Risk", Order = 4)]
        public bool UseVolatilityBasedSizing { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "Risk Per Trade ($)", GroupName = "Risk", Order = 5)]
        public double RiskPerTradeDollars { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max Contracts Cap", GroupName = "Risk", Order = 6)]
        public int MaxContractsCap { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Trade Quantity (fixed)", GroupName = "Parameters", Order = 1)]
        public int TradeQuantity { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max Trades Per Day", GroupName = "Parameters", Order = 2)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Start Time HHMMSS", GroupName = "Parameters", Order = 3)]
        public int StartTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "End Time HHMMSS", GroupName = "Parameters", Order = 4)]
        public int EndTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Respect manual stop moves (HUD / chart drag)", Description = "If on, a stop you move by hand is adopted as the new trail baseline and is not reverted by the strategy.", GroupName = "Parameters", Order = 6)]
        public bool RespectManualStopMoves { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Debug Prints", GroupName = "Parameters", Order = 5)]
        public bool DebugPrints { get; set; }
    }
}

