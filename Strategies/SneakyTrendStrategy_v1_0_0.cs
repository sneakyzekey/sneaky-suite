// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
//
// Copyright (c) 2026 silentsudo-io and the Sentinel Suite contributors.
// ─────────────────────────────────────────────────────────────────────────────
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.AddOns.Sneaky;   // SneakyCore (gate/sizing/consult) + SneakyCardCorner
#endregion

// ═════════════════════════════════════════════════════════════════════════════
//  SneakyTrendStrategy — risk-managed trend-flip strategy on SneakyTrend   |   Version v1.0.0
//  File: SneakyTrendStrategy_v1_0_0.cs   |   namespace …Strategies
//
//  WHAT THIS IS — the single, risk-managed replacement for the five NT-Wizard TrendMagic strategies
//  (TMEntry / TMEntry50 / TMXEntryExit / TMSquared / TripleTM). Those either had NO stop (TMEntry),
//  a target with NO stop (TMEntry50 — tiny wins, unbounded losses), or only painted the background
//  and placed no trades at all (TMSquared / TripleTM). This one fixes the money side.
//
//  WHY IT IS SUPERIOR:
//    • Trades the CORRECTED SneakyTrend line (true ATR + CCI hysteresis) instead of the whipsawing
//      original — fewer, cleaner flips (see SneakyTrend_v1_0_0.cs header).
//    • REAL RISK MANAGEMENT: ATR-sized stop + R-multiple target, with an optional trail that rides the
//      SneakyTrend line. Every entry is bracketed; you can never sit in an unbounded loss.
//    • SNEAKY GATING: every entry asks SneakyCore.CanEnter (kill-switch + feed health + daily
//      governor + account-session + rollover + news) and is fail-CLOSED (automated → refuse on block).
//    • RISK-BASED SIZING: optionally size each entry to a fixed $ risk over the ATR stop
//      (SneakyCore.SizeForRisk), else profile-scaled base qty (SneakyCore.SizedQuantity).
//    • OPTIONAL ADX ALIGNMENT: require ADXPro to confirm trend ON + bias agrees before entering.
//    • Stop-and-reverse on the opposite flip, but ALWAYS flattens on a flip even if the reverse entry
//      is gated off — so a blocked reverse never strands you in a stale position.
//
//  Managed order framework (SetStopLoss / SetProfitTarget). Registers its traded instrument with
//  SneakyRisk's watch registry so a stalled feed is caught even while flat.
//
//  CHANGELOG
//    v1.0.0 — initial: SneakyTrend flip entries, ATR stop + R target + line trail, CanEnter gate,
//             risk sizing, ADX-align filter. Supersedes the TMEntry/TMEntry50/TMX/TMSquared/TripleTM set.
//    v1.0.1 — bugfix: TryEnter's entry gate short-circuited past CanEnter when Account was null (fail-
//             open instead of fail-closed); added Ledger.Order/Fill audit trail (RecordFire/
//             OnExecutionUpdate); StampCross records the tradeable quote at submission for slip tracking.
//    v1.0.2 — bugfix: TrailToLine's line-vs-Close[0] check only proved the new stop valid as of bar
//             close, not at actual order-modify time — added a live bid/ask + tick-buffer re-check
//             immediately before SetStopLoss; added OnOrderUpdate diagnostics for any order that goes
//             Rejected/Cancelled-with-error.
//    v1.0.3 — bugfix: the v1.0.2 buffer check fell through to submitting anyway when the live quote
//             couldn't be read — flipped to fail-safe (skip, don't gamble); removed OnOrderUpdate's
//             manual Exit call, which raced NinjaTrader's own StopCancelClose flatten and could double up.
//    v1.0.4 — bugfix: still failed live after v1.0.3 — a flat 2-tick buffer isn't real margin on a
//             fast 1-min NQ chart. Buffer widened to ATR-scaled (half-ATR, floored at 8 ticks); added
//             per-decision Output logging (submit vs skip, with the actual line/bid-ask/buffer values).
//    v1.0.5 — bugfix: still failed live after v1.0.4 — half-ATR commonly bottomed out at the 8-tick
//             floor on a quiet reading, which a real bounce could still beat before the modify landed.
//             Floor raised 8 -> 20 ticks, scale raised half-ATR -> full-ATR. Skipping a trail update
//             costs nothing (existing stop stays valid); only a too-narrow buffer costs anything.
// ═════════════════════════════════════════════════════════════════════════════
namespace NinjaTrader.NinjaScript.Strategies
{
    public class SneakyTrendStrategy_v1_0_0 : Strategy
    {
        private NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors.SneakyTrend_v1_0_0 st;
        private ATR atr;

        private double _trailLong  = double.NaN;   // monotonic ratchet stops (price)
        private double _trailShort = double.NaN;
        private bool   _watchRegistered;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                 = "Risk-managed trend-flip strategy on the corrected SneakyTrend line (supersedes the TrendMagic strategy set): ATR stop + R target + line trail, SneakyCore entry gate + risk sizing + optional ADX alignment.";
                Name                        = "SneakyTrendStrategy_v1_0_0";
                Calculate                   = Calculate.OnBarClose;
                EntriesPerDirection         = 1;
                EntryHandling               = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy= true;
                ExitOnSessionCloseSeconds   = 30;
                StartBehavior               = StartBehavior.WaitUntilFlat;
                TimeInForce                 = TimeInForce.Gtc;
                RealtimeErrorHandling       = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling          = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade         = 20;
                IsInstantiatedOnEachOptimizationIteration = true;

                // trend engine (mirror SneakyTrend defaults)
                CciPeriod                   = 20;
                AtrPeriod                   = 14;
                AtrMult                     = 1.5;
                CciThreshold                = 15.0;

                // risk
                StopAtrMult                 = 1.5;
                MinStopTicks                = 8;
                RewardRisk                  = 2.0;
                UseLineTrail                = true;

                // sizing
                UseRiskSizing               = false;
                RiskDollars                 = 100.0;
                BaseQuantity                = 1;

                // gating
                RequireAdxAlign             = false;
                StaleSec                    = 90.0;
            }
            else if (State == State.DataLoaded)
            {
                // internal SneakyTrend instance (card/signals/publish OFF — this copy is headless compute only).
                st  = SneakyTrend_v1_0_0(CciPeriod, AtrPeriod, AtrMult, CciThreshold,
                                           false, 8, false, false, 12, false,
                                           SneakyCardCorner.TopLeft, false, StaleSec, false, false);
                atr = ATR(AtrPeriod);
            }
            else if (State == State.Realtime)
            {
                if (!_watchRegistered && Instrument != null)
                {
                    try { SneakyCore.RegisterWatchInstrument(Instrument); _watchRegistered = true; } catch (Exception _sx) { SneakyCore.Swallow("SneakyTrendStrategy.OnStateChange", _sx); }
                }
            }
            else if (State == State.Terminated)
            {
                if (_watchRegistered && Instrument != null)
                {
                    try { SneakyCore.UnregisterWatchInstrument(Instrument); } catch (Exception _sx) { SneakyCore.Swallow("SneakyTrendStrategy.OnStateChange", _sx); }
                    _watchRegistered = false;
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;
            if (CurrentBar < BarsRequiredToTrade) return;

            // reset trail ratchets whenever flat
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                _trailLong = double.NaN; _trailShort = double.NaN;
            }

            int dir     = (int)st.Direction[0];
            int prevDir = (int)st.Direction[1];
            bool flipUp = dir == 1  && prevDir == -1;
            bool flipDn = dir == -1 && prevDir == 1;

            // On a flip: try to reverse (a managed entry auto-closes the opposite position). If the
            // reverse is GATED OFF, still flatten explicitly so a blocked reverse never strands us.
            if (flipUp)
            {
                bool entered = TryEnter(1);
                if (!entered && Position.MarketPosition == MarketPosition.Short) ExitShort();
            }
            else if (flipDn)
            {
                bool entered = TryEnter(-1);
                if (!entered && Position.MarketPosition == MarketPosition.Long) ExitLong();
            }

            // optional trailing stop that rides the SneakyTrend line (monotonic — never loosens)
            if (UseLineTrail) TrailToLine();
        }

        /// <summary>Attempt a gated, risk-bracketed entry. Returns true only if an entry order was placed.</summary>
        private bool TryEnter(int dir)
        {
            // fail-CLOSED entry gate: kill-switch + feed health + governor + session + rollover + news.
            // v1.0.1 — BUGFIX: this used to read `if (Account != null && !CanEnter(...))`, so when Account
            // was null the `&&` short-circuited to false and the whole block was skipped — CanEnter was
            // never even called, and the code fell straight through to sizing and entering. That is the
            // exact opposite of "fail-closed" (a missing Account silently meant "trade anyway"). A null
            // Account now blocks outright, matching what this method's own comment says it does.
            // 2026-09-05 — BUGFIX: this whole gate/sizing/entry body used to run with NO exception
            // handling at all, unlike every other method in this file (RecordFire, StampCross,
            // OnExecutionUpdate, OnOrderUpdate, TrailToLine all wrap themselves in try/catch +
            // SneakyCore.Swallow). An exception thrown by any SneakyCore call here would propagate
            // straight out of TryEnter uncaught, and NinjaTrader's engine typically responds to an
            // unhandled strategy exception by disabling the strategy outright — mid-position, if the
            // throw happened after EnterLong/EnterShort already fired. Wrapped now so a single bad
            // call can't take the whole strategy down; fails CLOSED (returns false, no entry) on any
            // exception raised BEFORE the order is placed, matching this method's own stated gate
            // philosophy. Safe even for an exception raised AFTER EnterLong/EnterShort fires (e.g. in
            // RecordFire): OnBarUpdate's caller only acts on a false `entered` by also re-checking the
            // ACTUAL Position.MarketPosition before flattening, so a false-negative here after a real
            // fill does not trigger a spurious ExitLong/ExitShort against the position we just took.
            try
            {
                string reason;
                if (Account == null) { Print(Time[0] + "  entry blocked: no Account"); return false; }
                if (!SneakyCore.CanEnter(Instrument != null ? Instrument.FullName : null, Account, out reason))
                {
                    Print(Time[0] + "  entry blocked: " + reason);
                    return false;
                }

                // optional ADX-regime confirmation
                if (RequireAdxAlign && Instrument != null && Instrument.MasterInstrument != null)
                {
                    // Consult the ADX on THIS chart (scope), not whichever chart wrote last (SneakyCore v1.18.0).
                    var a = SneakyCore.GetAdxState(SneakyCore.ScopeOf(Instrument, BarsPeriod)
                                                     ?? Instrument.MasterInstrument.Name, StaleSec);
                    if (a == null || !a.TrendOn || !a.Aligned(dir))
                    {
                        Print(Time[0] + "  entry skipped: ADX not aligned");
                        return false;
                    }
                }

                // ATR-sized stop + R target
                double atrTicks = TickSize > 0 ? atr[0] / TickSize : 0.0;
                int stopTicks   = Math.Max(MinStopTicks, (int)Math.Round(atrTicks * StopAtrMult));
                int targetTicks = Math.Max(1, (int)Math.Round(stopTicks * RewardRisk));

                // sizing
                int qty;
                if (UseRiskSizing)
                {
                    qty = SneakyCore.SizeForRisk(Account, Instrument, stopTicks, RiskDollars);
                    if (qty < 1)
                    {
                        Print(Time[0] + "  entry skipped: risk-size 0 (stop " + stopTicks + "t too wide for $" + RiskDollars + ")");
                        return false;
                    }
                }
                else
                {
                    qty = SneakyCore.SizedQuantity(Account, BaseQuantity);
                    if (qty < 1) qty = 1;
                }

                SetStopLoss(CalculationMode.Ticks, stopTicks);
                SetProfitTarget(CalculationMode.Ticks, targetTicks);

                StampCross(dir);   // v1.0.1 — reference price BEFORE the cross (buy lifts ask, sell hits bid)
                if (dir == 1) EnterLong(qty, "STL");
                else          EnterShort(qty, "STS");

                try { if (Account != null) SneakyCore.NoteOrderSubmitted(Account.Name); } catch (Exception _sx) { SneakyCore.Swallow("SneakyTrendStrategy.EnterShort", _sx); }
                RecordFire(dir, qty, stopTicks, targetTicks);   // v1.0.1 — Ledger.Order, so this strategy finally has an audit trail
                return true;
            }
            catch (Exception _sx)
            {
                SneakyCore.Swallow("SneakyTrendStrategy.TryEnter", _sx);
                return false;
            }
        }

        // v1.0.1 — was missing entirely: every other Sneaky order-placer (Bridge, Keel) writes a full
        // Ledger order/fill trail on every entry and exit; this strategy only ever called
        // NoteOrderSubmitted, so its live trades left no way to reconstruct what it did, when, at what
        // size, or what it made/lost, short of NinjaTrader's own native Trades/Executions view.
        private string _scope, _instanceKey;
        private double _crossRefPx;
        private const string Tag = "SneakyTrendStrategy";

        private string Scope()
        {
            if (_scope == null)
            {
                try { _scope = SneakyCore.ScopeOf(Instrument, BarsPeriod); }
                catch (Exception _sx) { SneakyCore.Swallow("SneakyTrendStrategy.Scope", _sx); _scope = ""; }
            }
            return _scope;
        }

        private string InstanceKey()
        {
            if (_instanceKey == null)
            {
                string s = Scope();
                if (string.IsNullOrEmpty(s))
                    s = Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : "?";
                string acct = Account != null ? Account.Name : "?";
                _instanceKey = Tag + "#" + s + "@" + acct;
            }
            return _instanceKey;
        }

        /// <summary>Stamp the TRADEABLE quote on the side about to be crossed, at submission — the same
        /// `intended`-price contract every other Sneaky order-placer uses, so slip is measured against a
        /// price that could actually trade rather than the fill itself (which would make slip == 0).</summary>
        private void StampCross(int crossDir)
        {
            try
            {
                double q = crossDir > 0 ? GetCurrentAsk() : GetCurrentBid();
                _crossRefPx = q > 0 ? q : 0.0;
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyTrendStrategy.StampCross", _sx); _crossRefPx = 0.0; }
        }

        private void RecordFire(int dir, int qty, int stopTicks, int targetTicks)
        {
            if (Account == null || Instrument == null) return;
            try
            {
                double refPx = _crossRefPx > 0 ? _crossRefPx : Close[0];
                SneakyCore.Ledger.Order(Account.Name, Instrument.FullName, dir > 0 ? "Buy" : "SellShort", "Market",
                                          qty, refPx, Tag, null, InstanceKey());
                SneakyCore.Ledger.Action("trend-fire", Account.Name,
                    "dir=" + dir + " qty=" + qty + " stop=" + stopTicks + "t target=" + targetTicks + "t", null, InstanceKey());
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyTrendStrategy.RecordFire", _sx); }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
                                                  MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null) return;
            try
            {
                var o = execution.Order;
                double intended = price;
                if (o.OrderType == OrderType.StopMarket || o.OrderType == OrderType.StopLimit) intended = o.StopPrice;
                else if (o.OrderType == OrderType.Limit) intended = o.LimitPrice;
                else if (_crossRefPx > 0) intended = _crossRefPx;
                if (Account != null && Instrument != null)
                    SneakyCore.Ledger.Fill(Account.Name, Instrument.FullName, o.OrderAction.ToString(),
                                             quantity, intended, price, TickSize, Tag, null, InstanceKey());
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyTrendStrategy.OnExecutionUpdate", _sx); }
        }

        private void TrailToLine()
        {
            double line = st.Trend[0];
            if (line <= 0) return;

            // v1.0.2 — BUGFIX: "line < Close[0]" / "line > Close[0]" only proves the new stop was valid
            // AS OF the bar CLOSE this method is reacting to (Calculate.OnBarClose). By the time
            // SetStopLoss(Price,...) actually reaches the order engine, live price may already have
            // traded through that level — a fast move, a gap, or just the gap between "bar closed" and
            // "order modification submitted". NinjaTrader then rejects the modify as invalid (on the
            // wrong side of the market). That rejection arrives ASYNCHRONOUSLY via the order-update
            // pipeline, not as a C# exception here, so the surrounding try/catch never sees it — and
            // RealtimeErrorHandling.StopCancelClose (set in OnStateChange) treats the rejection as a
            // runtime error: it stops this strategy and cancels the (now nonexistent) stop order, but it
            // does NOT flatten the position — exactly how a real position got left with zero protection.
            // Fix here is prevention: re-check against the LIVE bid/ask with a tick buffer immediately
            // before submitting, and skip the update (keeping the existing, still-valid stop) if the
            // market has already caught up to the line. See OnOrderUpdate below for the recovery side —
            // a guaranteed-safe backstop in case a rejection still slips through this check.
            //
            // v1.0.3 — BUGFIX: the original version of this check FELL THROUGH to submitting anyway
            // ("liveBid <= 0 || ...") whenever the live quote couldn't be read — i.e. exactly when we
            // could NOT verify safety, it gambled instead of skipping. Flipped to fail-safe: only submit
            // when we positively verified the price against a real quote; otherwise leave the existing
            // (already-valid) stop alone and try again next bar.
            //
            // v1.0.4 — this STILL failed live after v1.0.3 with no change in outcome. A 2-tick buffer is
            // not real safety margin on a fast-moving 1-min NQ chart — it only covers noise, not an
            // actual directional run, and Calculate.OnBarClose means this check runs once per closed bar,
            // so there can easily be several ticks of real movement between the check and the order
            // reaching the engine. Buffer widened substantially (ATR-scaled, floored at 8 ticks) so it
            // has to survive more than a single quote's worth of drift. Also logging every decision here
            // (submitted vs skipped, with the actual line/bid-ask/buffer values) so if this fires again,
            // the NinjaScript Output window shows exactly what happened instead of us guessing from a
            // popup screenshot.
            //
            // v1.0.5 — happened AGAIN (screenshot: BuyToCover stop rejected "below the market", short
            // side). The v1.0.4 buffer (half-ATR, floored at 8 ticks) still wasn't wide enough — on NQ
            // with a quiet ATR reading, half-ATR often bottoms out at exactly that 8-tick (2-point) floor,
            // which a real bounce during a live short can still beat in the gap between this check and the
            // modify actually landing at Sim110. There is no real downside to erring wide here: skipping a
            // trail update for one bar just keeps the existing, still-valid, still-protective stop in
            // place — it never removes protection. Only a too-NARROW buffer costs anything (this exact
            // strategy-terminating rejection). So: floor raised 8 -> 20 ticks, and the ATR scale raised
            // from half-ATR to full-ATR (matching the same ATR weight already trusted for the INITIAL
            // stop's own distance, not an arbitrary new number).
            double safetyBuffer = TickSize * Math.Max(20, (int)Math.Round(atr[0] / Math.Max(TickSize, 1e-9)));

            if (Position.MarketPosition == MarketPosition.Long && line < Close[0])
            {
                if (double.IsNaN(_trailLong) || line > _trailLong)   // only ever raise
                {
                    double liveBid = GetCurrentBid();
                    if (liveBid > 0 && line < liveBid - safetyBuffer)
                    {
                        _trailLong = line;
                        try { SetStopLoss(CalculationMode.Price, _trailLong); }
                        catch (Exception _sx) { SneakyCore.Swallow("SneakyTrendStrategy.TrailToLine", _sx); }
                    }
                    else
                    {
                        Print(Time[0] + "  trail SKIPPED (long): line=" + line.ToString("0.00")
                              + " bid=" + liveBid.ToString("0.00") + " buffer=" + safetyBuffer.ToString("0.00")
                              + " -- too close to live market, keeping existing stop.");
                    }
                }
            }
            else if (Position.MarketPosition == MarketPosition.Short && line > Close[0])
            {
                if (double.IsNaN(_trailShort) || line < _trailShort) // only ever lower
                {
                    double liveAsk = GetCurrentAsk();
                    if (liveAsk > 0 && line > liveAsk + safetyBuffer)
                    {
                        _trailShort = line;
                        try { SetStopLoss(CalculationMode.Price, _trailShort); }
                        catch (Exception _sx) { SneakyCore.Swallow("SneakyTrendStrategy.TrailToLine", _sx); }
                    }
                    else
                    {
                        Print(Time[0] + "  trail SKIPPED (short): line=" + line.ToString("0.00")
                              + " ask=" + liveAsk.ToString("0.00") + " buffer=" + safetyBuffer.ToString("0.00")
                              + " -- too close to live market, keeping existing stop.");
                    }
                }
            }
        }

        // v1.0.2 — SAFETY NET: if a stop-loss order still comes back Rejected (or is Cancelled with an
        // error) despite the pre-check above, RealtimeErrorHandling.StopCancelClose will stop THIS
        // strategy and cancel the dead order — but it will NOT re-protect an open position. Left alone,
        // that is exactly the "2 contracts open, no stop, strategy disabled" state that got reported live.
        //
        // v1.0.3 — BUGFIX: the first version of this safety net tried to re-arm using
        // SetStopLoss(CalculationMode.Ticks, ...) on the theory that ticks mode "can't be on the wrong
        // side of the market". That's wrong — NinjaTrader computes Ticks mode as an offset from the
        // position's AVERAGE ENTRY price, not from the current market, so it too can be rejected in a
        // fast reversal. Replaced with an emergency market-order flatten instead.
        //
        // v1.0.4 — that ALSO produced no visible change live ("did it again", same outcome). Re-reading
        // the actual error dialog text: "Strategy has sent cancel requests, attempted to close the
        // position and terminated itself" IS NinjaTrader's own stock message for
        // RealtimeErrorHandling.StopCancelClose (Stop the strategy / Cancel orders / Close the position —
        // that's what the three words in the enum name mean). NinjaTrader was ALREADY attempting to
        // flatten the position natively, on its own, the moment the stop-loss order was rejected. This
        // strategy's OnOrderUpdate handler calling ExitLong/ExitShort at the same moment was a SECOND,
        // independent close attempt racing the platform's own — a very plausible way to get a repeat
        // "Unable to change order" and no improvement in outcome (two exits colliding rather than one
        // clean one). Removed that call entirely; NinjaTrader's own RealtimeErrorHandling is left to do
        // the flattening it already does. This handler is now diagnostics-only: it logs exactly what
        // order failed, its name/state/error/comment, and the position at that moment, so if this
        // happens again the NinjaScript Output window (not a screenshot of a popup) shows the real
        // sequence of events instead of us inferring it after the fact.
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity,
            int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            try
            {
                if (order == null) return;

                bool wentBad = orderState == OrderState.Rejected
                               || (orderState == OrderState.Cancelled && error != ErrorCode.NoError);

                if (wentBad)
                {
                    Print(time + "  ORDER " + orderState + ": name=\"" + order.Name + "\" action=" + order.OrderAction
                          + " type=" + order.OrderType + " err=" + error + " comment=\"" + comment + "\" -- position now "
                          + Position.MarketPosition + " " + Position.Quantity + " (NinjaTrader's own "
                          + "RealtimeErrorHandling will cancel/close on its own if this trips the runtime-error handler).");
                    try
                    {
                        SneakyCore.Ledger.Action("order-error", Account != null ? Account.Name : "?",
                            "name=" + order.Name + " state=" + orderState + " err=" + error
                            + " pos=" + Position.MarketPosition + " " + Position.Quantity, null, InstanceKey());
                    }
                    catch (Exception _sx) { SneakyCore.Swallow("SneakyTrendStrategy.OnOrderUpdate.log", _sx); }
                }
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyTrendStrategy.OnOrderUpdate", _sx); }
        }

        #region Properties
        [Range(1, int.MaxValue)]
        [NinjaScriptProperty]
        [Display(Name = "CCI Period", Order = 1, GroupName = "1. Trend Engine")]
        public int CciPeriod { get; set; }

        [Range(1, int.MaxValue)]
        [NinjaScriptProperty]
        [Display(Name = "ATR Period", Order = 2, GroupName = "1. Trend Engine")]
        public int AtrPeriod { get; set; }

        [Range(0.00001, double.MaxValue)]
        [NinjaScriptProperty]
        [Display(Name = "ATR Multiplier", Order = 3, GroupName = "1. Trend Engine")]
        public double AtrMult { get; set; }

        [Range(0.0, double.MaxValue)]
        [NinjaScriptProperty]
        [Display(Name = "CCI Hysteresis Threshold", Order = 4, GroupName = "1. Trend Engine")]
        public double CciThreshold { get; set; }

        [Range(0.0, double.MaxValue)]
        [NinjaScriptProperty]
        [Display(Name = "Stop ATR Multiplier", Description = "Initial stop distance = this × ATR (in ticks).", Order = 10, GroupName = "2. Risk")]
        public double StopAtrMult { get; set; }

        [Range(1, int.MaxValue)]
        [NinjaScriptProperty]
        [Display(Name = "Min Stop (ticks)", Description = "Floor for the ATR stop so a quiet ATR can't place the stop on top of price.", Order = 11, GroupName = "2. Risk")]
        public int MinStopTicks { get; set; }

        [Range(0.1, double.MaxValue)]
        [NinjaScriptProperty]
        [Display(Name = "Reward : Risk", Description = "Profit target = this × the stop distance.", Order = 12, GroupName = "2. Risk")]
        public double RewardRisk { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trail Stop to Trend Line", Description = "Ride the stop up/down along the SneakyTrend line once in a trade (monotonic — never loosens).", Order = 13, GroupName = "2. Risk")]
        public bool UseLineTrail { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use $-Risk Sizing", Description = "Size each entry to RiskDollars over the ATR stop (SneakyCore.SizeForRisk). Off = profile-scaled base qty.", Order = 20, GroupName = "3. Sizing")]
        public bool UseRiskSizing { get; set; }

        [Range(1.0, double.MaxValue)]
        [NinjaScriptProperty]
        [Display(Name = "Risk $ per Trade", Order = 21, GroupName = "3. Sizing")]
        public double RiskDollars { get; set; }

        [Range(1, int.MaxValue)]
        [NinjaScriptProperty]
        [Display(Name = "Base Quantity", Description = "Used when $-risk sizing is off (profile-scaled by SneakyCore.SizedQuantity).", Order = 22, GroupName = "3. Sizing")]
        public int BaseQuantity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require ADX Alignment", Description = "Only enter when ADXPro publishes trend ON + bias agreeing with the flip (needs SneakyCore ≥ v1.2.0 + ADXPro publishing).", Order = 30, GroupName = "4. Gating")]
        public bool RequireAdxAlign { get; set; }

        [Range(0.0, double.MaxValue)]
        [NinjaScriptProperty]
        [Display(Name = "Consult Stale (sec)", Description = "Ignore a published ADX state older than this many seconds (0 = never stale).", Order = 31, GroupName = "4. Gating")]
        public double StaleSec { get; set; }
        #endregion
    }
}
