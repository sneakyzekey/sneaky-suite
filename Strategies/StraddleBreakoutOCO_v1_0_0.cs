// ─────────────────────────────────────────────────────────────────────────────
// StraddleBreakoutOCO_v1_0_0.cs
// ─────────────────────────────────────────────────────────────────────────────
//  Standalone NinjaTrader 8 strategy. No third-party dependencies, no other AddOns/services
//  required — this file compiles and runs entirely on its own.
//
//  WHAT THIS IS
//  ------------
//  A two-sided straddle, always on. The instant it's enabled (and any time it's flat after that),
//  it places a buy-stop OffsetTicks above and a sell-stop OffsetTicks below the current market
//  price, both for Contracts size, tagged with a shared OCO group so the instant one fills the
//  other is cancelled. Whichever side fills gets a StopTicks-tick protective stop that this file
//  then trails by hand (moves it in your favor every bar, never against you) — there is no
//  separate profit target, the trailing stop is the only exit by design. The instant a position
//  returns to flat, a fresh pair is placed immediately at the (new) current market price — this
//  repeats for as long as the strategy is enabled.
//
//  WHY UNMANAGED ORDERS (v2.0.0)
//  ------------------------------
//  v1.x used NinjaTrader's managed approach (EnterLongStopMarket/EnterShortStopMarket +
//  SetTrailStop). That turned out to be fundamentally incompatible with a genuine two-sided
//  straddle: NinjaTrader's managed engine has a built-in safety rule ("Internal Order Handling
//  Rules that Reduce Unwanted Positions") that SILENTLY IGNORES a second entry order submitted in
//  the opposite direction while another entry order is still pending, on the reasoning that a
//  netting account (which is what Sim/futures accounts are) can't hold both a long and a short
//  position at once. Confirmed against NinjaTrader's own Help Guide and support forum. In
//  practice this meant the sell-stop side was being dropped on every single arm cycle from
//  v1.0.0 onward — the strategy was never actually placing two-sided pairs, just a one-sided
//  buy-stop that happened to fill/trail/exit convincingly whenever the market trended up.
//  The fix is to bypass the managed engine entirely: submit both entries via SubmitOrderUnmanaged
//  (which isn't subject to that rule), track fills and order state by hand via OnOrderUpdate,
//  submit the protective stop ourselves the instant either side fills (SetTrailStop doesn't exist
//  for unmanaged orders), and trail it ourselves each bar via ChangeOrder.
//
//  SPEC
//    Direction: both. Entry: buy-stop +OffsetTicks / sell-stop -OffsetTicks from current market,
//    static once placed (not re-priced tick to tick), OCO on fill (shared OCO tag + a manual
//    cancel-the-sibling safety net), immediately replaced (both sides, fresh from current market)
//    after any exit. Stop: fixed StopTicks stop that this file trails by the same distance, by
//    hand, the instant it fills. No target. Size: Contracts (default 5). No time filter — trades
//    continuously from the moment it's enabled.
//
//  SELF-HEAL: if either pending entry dies (rejected/manually cancelled) without filling, this is
//  detected and healed on the next tick — whether BOTH sides die (re-arms a fresh pair immediately)
//  or just ONE side dies while the other is still genuinely working (the lone survivor is cancelled
//  so a naked, un-hedged single stop never sits there with no OCO partner; a clean pair re-arms the
//  tick after that). A null order reference (haven't heard back from the broker yet — OnOrderUpdate
//  is asynchronous) is never treated as "dead", only a reference we've actually heard back on.
//
//  ⚠ NOT YET LIVE-TESTED in its unmanaged form. Compile it, then run it in Sim before anything
//  live, and specifically watch the very first arm/fill/protective-stop/trail/re-arm cycle end to
//  end — confirm in the Log tab that BOTH entries actually get Accepted this time (not one silently
//  ignored), that a protective stop appears the instant one fills, and that it actually trails.
//
//  Changelog
//    v2.0.5 (2026-09-07) — fixed the same fast-market race as v2.0.4, but on the OTHER protective-
//           stop code path: "Order rejected" / "Sell stop or sell stop limit orders can't be placed
//           above the market. affected Order: Sell 5 StopMarket @ ...". v2.0.4 only guarded
//           UpdateTrailingStop() (the every-bar re-price of an already-working stop) against the
//           live bid/ask moving past a stale candidate price — it missed that the very FIRST
//           protective stop, submitted the instant an entry fills (in OnOrderUpdate), is computed
//           purely from averageFillPrice and has the identical race: if price keeps moving hard
//           against the new position between the fill and this callback actually running, that
//           fill-price-derived stop can already be on the wrong side of the market by submission
//           time. Skipping isn't an option here the way it is in UpdateTrailingStop() — there's no
//           earlier stop order to fall back on, this position needs a real stop immediately — so
//           this path now clamps the stop to the tightest price still valid against
//           GetCurrentBid()/GetCurrentAsk() instead of skipping or submitting blind. In the normal
//           (non-race) case this is a no-op and StopTicks is honored exactly.
//    v2.0.4 (2026-09-07) — fixed a crash on the trailing-stop path: "Unable to change order" /
//           "Stop price can't be changed below the market" on the protective BuyToCover stop of a
//           short position (same class of risk applies to the Sell stop of a long). Root cause:
//           UpdateTrailingStop() computed its candidate price from Close[0] (last trade), which can
//           be a tick or two stale by the time the ChangeOrder request actually reaches the matching
//           engine — in a fast-moving market the live bid/ask can cross past that candidate in the
//           meantime, and NinjaTrader rejects the change outright rather than clamping it. Because
//           RealtimeErrorHandling = StopCancelClose, that single rejected ChangeOrder call terminated
//           the entire strategy (cancelled everything, closed the position) even though the existing
//           stop order was still sitting there untouched and still protecting the position. Now
//           re-validates the candidate against GetCurrentBid()/GetCurrentAsk() (with a 1-tick safety
//           buffer) immediately before calling ChangeOrder, and simply skips that tick's update — no
//           ChangeOrder call at all — if the market's already too close. A skipped update just means
//           we retry with a fresh price on the very next tick; the old stop order is never touched.
//    v2.0.3 (2026-09-07) — fixed the OCO group id after a re-enable crashed the strategy: "The
//           OCO ID 'StraddleOCO1' cannot be reused. Please use a new OCO ID." NinjaTrader tracks
//           used OCO ids at the account level, not per strategy instance, but the id was built from
//           a counter field that always restarts at 1 whenever the strategy is (re)enabled — so the
//           very first arm cycle of a fresh run collided with a tag a previous run had already used,
//           the order was rejected, and RealtimeErrorHandling=StopCancelClose then terminated the
//           strategy outright. Each arm cycle's OCO id is now a fresh GUID, so it's unique across
//           restarts, not just within one instance's lifetime.
//    v2.0.2 (2026-09-07) — fixed a partial-fill gap found on the first real fill under v2.0.1:
//           both entries got Accepted correctly and the Short side filled (across two partial
//           executions, 2 then 3 contracts), the Long side got cancelled via the native OCO group
//           — but no protective stop was ever placed. Root cause: the fill-handling block in
//           OnOrderUpdate was gated on "!_inPosition", but _inPosition flips true the instant the
//           FIRST partial execution creates a non-flat position (via OnPositionUpdate), well before
//           OnOrderUpdate sees the order's terminal Filled state — so by the time the real fill
//           event arrived, the gate was already closed and the whole cancel-sibling/submit-stop
//           block was skipped. Now gated on "_stopOrder == null" (has a stop actually been placed
//           yet) instead, which is unaffected by how many partial fills got there first.
//    v2.0.1 (2026-09-07) — fixed a classic unmanaged-approach gotcha found right after enabling
//           v2.0.0: NinjaTrader replays historical bars to warm up (State.Historical) before a
//           strategy goes live, and unlike the managed approach, unmanaged orders are submitted
//           for real regardless of state. OnBarUpdate was arming pairs against ancient historical
//           bar prices during that warm-up, which the Sim engine rejected as invalid ("stop price
//           ... is invalid based on the price range of the bar... subsequent orders may also be
//           ignored") — and once it starts ignoring this strategy instance's orders, nothing ever
//           arms once real-time actually begins either, which is why enabling it did nothing. Now
//           OnBarUpdate returns immediately unless State == State.Realtime.
//    v2.0.0 (2026-09-07) — rewritten on the unmanaged order approach. Root cause found via the
//           NinjaTrader Log tab: "An Enter() method to submit an entry order... has been ignored.
//           ... Internal Order Handling Rules that Reduce Unwanted Positions" — confirmed via
//           NinjaTrader's Help Guide and support forum that the managed engine silently drops a
//           second opposite-direction entry order while one is already pending, which is why only
//           one side of the straddle was EVER actually live, going all the way back to v1.0.0.
//           IsUnmanaged is now true; entries go through SubmitOrderUnmanaged with a shared OCO
//           group string; the protective stop is submitted by hand on fill (no more SetTrailStop)
//           and trailed by hand each bar via ChangeOrder. The v1.1.1/v1.1.2 self-heal logic is
//           unchanged and still applies to the (now real) two-sided pending pair.
//    v1.1.2 (2026-09-07) — fixed a race in the v1.1.1 self-heal: right after arming, a fast
//           OnEachTick bar update could run before OnOrderUpdate had populated the order
//           references, reading both as null/"not working" and triggering an immediate same-tick
//           re-arm on top of the pair just submitted — NinjaTrader then treated the repeat as a
//           cancel-replace of the same signal names, over and over, every tick, so neither side
//           ever got the chance to fill (confirmed via the Log tab: continuous Submitted/
//           Accepted/Cancelled cycling with Filled=0 throughout). Now the self-heal check waits
//           until both order references are non-null (i.e. we've heard back at least once) before
//           judging either leg dead — a null reference means "still waiting to hear from the
//           broker", not "rejected".
//    v1.1.1 (2026-09-07) — fixed a self-heal gap: the old check only cleared the armed flag when
//           BOTH entries had died, so if exactly one leg died (e.g. rejected) while the other stayed
//           working, the strategy looked "still armed and waiting" forever, leaving a permanently
//           naked single pending stop with no OCO partner. Now detects that asymmetric case, cancels
//           the lone survivor, and re-arms a fresh pair next tick. Also removed the now-unused
//           AnyWorkingEntry() helper.
//    v1.1.0 (2026-09-07) — removed the session time filter entirely per request: no more
//           SessionStartTime/SessionEndTime, no time-zone caveat, no Trading Hours template
//           requirement. Enabling the strategy arms the first pair immediately and it stays armed/
//           re-arming continuously from then on — no window to fall outside of.
//    v1.0.1 (2026-09-07) — documented/confirmed after-hours & overnight-wrap session windows.
//    v1.0.0 (2026-09-07) — first cut.
// ─────────────────────────────────────────────────────────────────────────────
#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class StraddleBreakoutOCO_v1_0_0 : Strategy
    {
        private const string EntryLong  = "StraddleLong";
        private const string EntryShort = "StraddleShort";
        private const string ExitStop   = "StraddleStop";

        private Order _longEntry;
        private Order _shortEntry;
        private Order _stopOrder;
        private bool  _armed;        // a pending pair is currently working, or one side has filled
        private bool  _inPosition;   // true once one side has actually filled (non-flat)
        private MarketPosition _posDir = MarketPosition.Flat;   // which side filled, while in position
        private double _stopPrice;   // current protective stop price, for hand-rolled trailing

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Two-sided straddle, always on: buy-stop + sell-stop N ticks either side of "
                            + "market (unmanaged orders, real OCO group), hand-trailed stop-only exit, "
                            + "immediately re-armed after any exit. No time filter — starts arming the "
                            + "moment it's enabled and keeps going. Standalone — no other files required.";
                Name = "Straddle Breakout OCO";

                Calculate                          = Calculate.OnEachTick;   // spec: "signal evaluation: on each tick"
                IsExitOnSessionCloseStrategy         = false;   // spec: no exit rule besides stops/targets — never auto-flatten
                MaximumBarsLookBack                  = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution                  = OrderFillResolution.Standard;
                Slippage                             = 0;
                StartBehavior                        = StartBehavior.WaitUntilFlat;
                TimeInForce                          = TimeInForce.Gtc;
                TraceOrders                          = false;
                RealtimeErrorHandling                = RealtimeErrorHandling.StopCancelClose;
                BarsRequiredToTrade                  = 1;
                IsUnmanaged                          = true;   // required: two simultaneous opposite-direction
                                                                // pending entries are not possible in managed mode

                OffsetTicks = 40;
                StopTicks   = 20;
                Contracts   = 5;
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;
            if (CurrentBar < BarsRequiredToTrade) return;

            // Unmanaged orders are submitted for real the instant SubmitOrderUnmanaged is called —
            // unlike the managed approach, NinjaTrader does NOT automatically simulate historical
            // fills for you. Without this guard, the historical warm-up bars NinjaTrader replays
            // before going live (State.Historical) would each try to arm a pair using that old bar's
            // stale price, which the Sim engine correctly rejects as invalid — and once it rejects
            // one, it stops trusting further orders from this strategy instance entirely. Only ever
            // act once we're actually live.
            if (State != State.Realtime) return;

            try
            {
                if (_inPosition)
                {
                    UpdateTrailingStop();
                    return;
                }

                // Only judge either leg once we've actually heard back about it at least once —
                // a null order reference means "haven't heard from the broker yet" (OnOrderUpdate
                // is asynchronous and can lag the very next OnEachTick bar update), NOT "dead". Acting
                // on a still-null reference as "dead" causes an immediate, same-tick re-arm on top of
                // the pair that was just submitted, which NinjaTrader treats as a cancel-replace of
                // the same signal names — repeating every tick and never letting either side fill.
                if (_armed && _longEntry != null && _shortEntry != null)
                {
                    bool longWorking  = IsWorking(_longEntry);
                    bool shortWorking = IsWorking(_shortEntry);

                    if (!longWorking && !shortWorking)
                    {
                        // self-heal: both pending entries died without either filling — don't stay
                        // wedged armed. Fall through to re-arm below, same tick.
                        _armed = false;
                    }
                    else if (longWorking != shortWorking)
                    {
                        // self-heal: exactly one side died (rejected/cancelled) while the other is
                        // still genuinely working — never leave a naked, un-hedged single stop sitting
                        // armed with no OCO partner. Cancel the lone survivor and re-arm clean next tick.
                        CancelOrder(longWorking ? _longEntry : _shortEntry);
                        _armed = false;
                        return;
                    }
                }

                if (Position.MarketPosition == MarketPosition.Flat && !_armed)
                    ArmStraddle();
            }
            catch (Exception ex) { Print("StraddleBreakoutOCO OnBarUpdate error: " + ex.Message); }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity,
            int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            try
            {
                if (order == null) return;

                if (order.Name == EntryLong)        _longEntry  = order;
                else if (order.Name == EntryShort)  _shortEntry = order;
                else if (order.Name == ExitStop)    _stopOrder  = order;
                else return;

                if (orderState != OrderState.Filled) return;

                // Gate on "have we already placed the protective stop", NOT on !_inPosition — a
                // stop-market entry that fills across more than one partial execution flips
                // _inPosition true (via OnPositionUpdate, the instant the FIRST partial creates a
                // non-flat position) well before OnOrderUpdate ever sees this order's terminal
                // Filled state. Gating on _inPosition meant a partially-filled entry's real fill
                // event got ignored — sibling never cancelled by our own logic (it happened to get
                // cancelled via the native OCO group instead) and no protective stop ever placed.
                if ((order.Name == EntryLong || order.Name == EntryShort) && _stopOrder == null)
                {
                    // manual OCO safety net: the shared OCO group passed to both entries should
                    // already have NinjaTrader cancel the sibling, but belt-and-suspenders here too.
                    if (order.Name == EntryLong && IsWorking(_shortEntry))
                        CancelOrder(_shortEntry);
                    else if (order.Name == EntryShort && IsWorking(_longEntry))
                        CancelOrder(_longEntry);

                    _posDir = order.Name == EntryLong ? MarketPosition.Long : MarketPosition.Short;

                    double desiredStop = order.Name == EntryLong
                        ? Instrument.MasterInstrument.RoundToTickSize(averageFillPrice - StopTicks * TickSize)
                        : Instrument.MasterInstrument.RoundToTickSize(averageFillPrice + StopTicks * TickSize);

                    // v2.0.5: the same fast-market race UpdateTrailingStop() was fixed for in
                    // v2.0.4 ("Stop price can't be changed below/above the market") can hit this
                    // very first protective-stop submission too — if price keeps moving hard in
                    // the adverse direction between the entry's fill and this callback actually
                    // running (fast/volatile instruments especially), a stop computed purely from
                    // averageFillPrice can already be on the wrong side of the live market by the
                    // time SubmitOrderUnmanaged reaches the engine ("Sell stop or sell stop limit
                    // orders can't be placed above the market" / the BuyToCover equivalent below
                    // it), which gets the order rejected and — via
                    // RealtimeErrorHandling=StopCancelClose — terminates the whole strategy. This
                    // is worse here than in UpdateTrailingStop(): there is no earlier stop order
                    // still working to fall back on, so skipping isn't an option — this position
                    // must get SOME protective stop right now. Clamp to the tightest price that's
                    // still valid against the freshest bid/ask instead of trusting the
                    // fill-price-derived value blindly; when the desired distance is still valid
                    // (the normal case) this is a no-op and StopTicks is honored exactly.
                    double currentAsk = GetCurrentAsk();
                    double currentBid = GetCurrentBid();

                    _stopPrice = _posDir == MarketPosition.Long
                        ? (currentBid > 0 ? Math.Min(desiredStop, Instrument.MasterInstrument.RoundToTickSize(currentBid - TickSize)) : desiredStop)
                        : (currentAsk > 0 ? Math.Max(desiredStop, Instrument.MasterInstrument.RoundToTickSize(currentAsk + TickSize)) : desiredStop);

                    // No SetTrailStop in unmanaged mode — submit the protective stop ourselves.
                    _stopOrder = SubmitOrderUnmanaged(0,
                        _posDir == MarketPosition.Long ? OrderAction.Sell : OrderAction.BuyToCover,
                        OrderType.StopMarket, filled, 0, _stopPrice, string.Empty, ExitStop);

                    _inPosition = true;
                    Print("StraddleBreakoutOCO filled " + order.Name + " @ " + averageFillPrice.ToString("0.####")
                        + " — protective stop @ " + _stopPrice.ToString("0.####")
                        + (_stopPrice != desiredStop ? " (clamped from " + desiredStop.ToString("0.####") + " to stay valid vs. live market)" : "")
                        + ", trailing by " + StopTicks + " ticks.");
                }
            }
            catch (Exception ex) { Print("StraddleBreakoutOCO OnOrderUpdate error: " + ex.Message); }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            try
            {
                if (position == null || position.Instrument != Instrument) return;

                if (marketPosition != MarketPosition.Flat)
                {
                    _inPosition = true;
                    return;
                }

                // marketPosition == Flat. Only re-arm if we were actually IN a position (i.e. this is a
                // genuine stop exit, or a manual flatten) — ignore the initial flat state before anything fills.
                if (!_inPosition) return;

                if (IsWorking(_stopOrder))  CancelOrder(_stopOrder);
                if (IsWorking(_longEntry))  CancelOrder(_longEntry);
                if (IsWorking(_shortEntry)) CancelOrder(_shortEntry);

                _inPosition = false;
                _armed      = false;
                _posDir     = MarketPosition.Flat;
                _longEntry  = null;
                _shortEntry = null;
                _stopOrder  = null;

                ArmStraddle();   // spec: both new stops placed immediately, at the NEW current market price
            }
            catch (Exception ex) { Print("StraddleBreakoutOCO OnPositionUpdate error: " + ex.Message); }
        }

        // ── the one place both entries ever get (re-)placed from ──────────────────────────────
        private void ArmStraddle()
        {
            double mkt    = Close[0];
            double buyPx  = Instrument.MasterInstrument.RoundToTickSize(mkt + OffsetTicks * TickSize);
            double sellPx = Instrument.MasterInstrument.RoundToTickSize(mkt - OffsetTicks * TickSize);
            // A GUID, not a per-instance counter: NinjaTrader tracks used OCO IDs at the account
            // level, not per strategy instance, so a counter that restarts at 1 every time the
            // strategy is (re)enabled collides with an ID a previous run already used ("The OCO ID
            // ... cannot be reused") and gets the whole order rejected.
            string oco = "StraddleOCO-" + Guid.NewGuid().ToString("N");

            // Unmanaged submission (bypasses the managed engine's "ignore the opposite-direction
            // entry" rule) — both tagged with the same OCO group so NinjaTrader cancels whichever
            // one doesn't fill the instant the other does.
            _longEntry  = SubmitOrderUnmanaged(0, OrderAction.Buy,       OrderType.StopMarket, Contracts, 0, buyPx,  oco, EntryLong);
            _shortEntry = SubmitOrderUnmanaged(0, OrderAction.SellShort, OrderType.StopMarket, Contracts, 0, sellPx, oco, EntryShort);

            _armed = true;
            Print("StraddleBreakoutOCO armed @ mkt " + mkt.ToString("0.####") + " — buy-stop "
                + buyPx.ToString("0.####") + " / sell-stop " + sellPx.ToString("0.####") + ", "
                + Contracts + " each, " + StopTicks + "-tick trail (hand-rolled), no target. OCO group " + oco);
        }

        // ── called every bar while in position: moves the protective stop in our favor only ────
        private void UpdateTrailingStop()
        {
            if (_stopOrder == null || !IsWorking(_stopOrder)) return;

            double candidate = _posDir == MarketPosition.Long
                ? Instrument.MasterInstrument.RoundToTickSize(Close[0] - StopTicks * TickSize)
                : Instrument.MasterInstrument.RoundToTickSize(Close[0] + StopTicks * TickSize);

            bool improves = _posDir == MarketPosition.Long
                ? candidate > _stopPrice
                : candidate < _stopPrice;

            if (!improves) return;

            // v2.0.4: re-validate the candidate against the freshest bid/ask right before sending,
            // not just against the (possibly a tick or two stale) Close[0] used to compute it. By
            // the time a ChangeOrder request reaches the matching engine the market can have moved
            // past the price we computed a moment ago — NinjaTrader then rejects the change outright
            // ("Stop price can't be changed below/above the market") and, because
            // RealtimeErrorHandling = StopCancelClose, that rejection used to terminate the whole
            // strategy (cancel everything, close the position). A Sell stop must stay strictly below
            // the current bid; a BuyToCover stop must stay strictly above the current ask. A 1-tick
            // buffer beyond the bare minimum is added for the latency between this check and the
            // order actually reaching the engine. If the market's already too close (or bid/ask isn't
            // available yet), just skip this tick's update — the existing stop order is untouched and
            // still working, still protecting the position, and we'll try again with a fresh price
            // next tick.
            double currentAsk = GetCurrentAsk();
            double currentBid = GetCurrentBid();

            if (_posDir == MarketPosition.Long)
            {
                if (currentBid <= 0 || candidate >= currentBid - TickSize) return;
            }
            else
            {
                if (currentAsk <= 0 || candidate <= currentAsk + TickSize) return;
            }

            _stopPrice = candidate;
            ChangeOrder(_stopOrder, _stopOrder.Quantity, 0, _stopPrice);
        }

        private static bool IsWorking(Order o)
        {
            if (o == null) return false;
            OrderState s = o.OrderState;
            return s == OrderState.Working || s == OrderState.Accepted
                || s == OrderState.Submitted || s == OrderState.PartFilled;
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Offset (ticks)", Description = "Distance in ticks each pending stop entry is placed from current market price.", Order = 1, GroupName = "Parameters")]
        public int OffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Trailing stop (ticks)", Description = "Trailing-stop distance, applied the instant a side fills and trailed by hand every bar. No separate profit target.", Order = 2, GroupName = "Parameters")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Contracts", Description = "Size of each of the two pending stop entries.", Order = 3, GroupName = "Parameters")]
        public int Contracts { get; set; }
        #endregion
    }
}
