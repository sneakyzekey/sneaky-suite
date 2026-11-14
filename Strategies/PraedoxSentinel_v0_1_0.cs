#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
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
using NinjaTrader.NinjaScript.Indicators;
#endregion

// 2026-09-23 -- UNIFIED REBUILD of "Praedox Sentinel" (previously built as 4 separate
// NinjaScript files -- PraedoxSignalEngine, PraedoxFibEngine, PraedoxVolatilityShield,
// PraedoxTradeProtector -- see those files' own header comments for the original
// per-phase design notes, all preserved below section by section). This file merges all
// four into ONE Strategy, matching the reference screenshots which show a single
// "Praedox Sentinel" script on the chart, not four separate indicators -- and adds
// automated order placement, which none of the standalone files could do (an Indicator
// has no order-management access; that's WHY this had to become a Strategy). Still an
// ORIGINAL implementation from surviving marketing copy/screenshots, no recovered source.
//
// *** THIS PLACES REAL ORDERS WHEN ENABLED. READ "ORDER EXECUTION" BELOW BEFORE TURNING
// EnableAutoTrading ON, ESPECIALLY ON A LIVE (NOT SIM) ACCOUNT. ***
//
// RELATIONSHIP TO THE 4 STANDALONE FILES: this is a fresh file, built alongside them --
// PraedoxSignalEngine_v0_1_0.cs, PraedoxFibEngine_v0_1_0.cs, PraedoxVolatilityShield_v0_1_0.cs,
// and PraedoxTradeProtector_v0_1_0.cs are UNTOUCHED and still on your machine, still
// compile, still work exactly as before. Nothing here depends on them or reads from them.
// Once this is tested and trusted, the old 4 can be removed from the chart (they're
// redundant with this one script) -- that's a separate step, not done automatically.
//
// SECTION 1 -- SIGNAL ENGINE (originally PraedoxSignalEngine_v0_1_0, Phase 1): the
// 10-signal engine + 5 signal-group toggles + priority selection, unchanged in behavior
// from the standalone file. See "THE 10 SIGNALS" further down for the per-signal rules.
//
// SECTION 2 -- FIB ENGINE (originally PraedoxFibEngine_v0_1_0, Phase 2): rolling
// auto-Fibonacci grid, unchanged in behavior. Was a sub-instantiated Indicator before;
// here its swing-detection/level/suppression logic is ported directly into this
// Strategy's own OnBarUpdate (UpdateFibEngine()) since a Strategy can't sub-instantiate
// another NinjaScript's Indicator any differently than before, but has no reason to now
// that it's not a separate script -- the logic is simply inlined instead.
//
// SECTION 3 -- VOLATILITY SHIELD / SMART TRADER (originally PraedoxVolatilityShield_v0_1_0,
// Phase 3): 4-state volatility regime + trading-allowed gate + min stop/target
// recommendations, unchanged in behavior, likewise inlined (UpdateVolatilityShield()).
//
// SECTION 4 -- TRADE PROTECTOR + DAILY P&L LIMITS (originally PraedoxTradeProtector_v0_1_0,
// Phase 4): consecutive win/loss streak limit + daily $ P&L limits, unchanged in behavior.
// The standalone file needed a separate static bridge class (PraedoxTradeProtectorState)
// to communicate its blocked flag to the Signal Engine, because they were different
// scripts. That bridge is GONE here -- tradeProtectorBlocked is just a private field read
// directly by the order-execution gate below, no cross-script plumbing needed anymore.
//
// SECTION 5 -- ORDER EXECUTION (NEW, not in any standalone file): when a signal fires AND
// Volatility Shield's TradingAllowed AND Trade Protector isn't blocked AND EnableAutoTrading
// is on AND the account is currently flat, submits a Managed-approach entry (EnterLong/
// EnterShort) sized by Contracts, with a stop-loss and profit-target attached BEFORE the
// entry (SetStopLoss/SetProfitTarget, both in ticks, sized from Volatility Shield's
// current-state MinStopTicks/MinTargetTicks recommendation) -- matches the SL/TP tick
// labels visible in the reference screenshots. Deliberately simple entry-management
// choices, since nothing in the surviving marketing copy specifies these mechanics:
//   - Only enters when FLAT. Does not add to a winning position or reverse on an opposite
//     signal while already in one -- a signal firing while a position is open is ignored.
//     Simplest, most conservative default; easy to change to auto-reverse if you want that.
//   - One entry per signal type, tagged "Praedox<rank>" (e.g. "Praedox1" for Slingshot) --
//     lets you see in the Trades tab which of the 10 signals produced which trade.
//   - EnableAutoTrading DEFAULTS TO FALSE. You have to turn it on explicitly, per instance,
//     after attaching this to a chart -- it does not start placing orders just because the
//     script is enabled/running, the way "Trading On" toggling live in the reference
//     screenshot's Control Center implies a human flips deliberately.
//   - FibGuard Trade Manager (managing an OPEN trade -- trailing off the fib grid) is now
//     built -- see SECTION 7 below. This section still only covers entry + the initial
//     fixed ATR stop/target; FibGuard layers on top of it once a trade is open.
//
// SECTION 6 -- SMART FILTERS (2026-09-23 update, next phase per the roadmap in
// /areas/praedox-sentinel.md: "Smart Filters (EMA/chop/S-R/large-bar/session-time)"). Chop
// is NOT reimplemented -- ChopOk()/ApplyChopFilterBroadly (Phase 1) already IS the chop
// component of Smart Filters; this section adds the other four, all independently
// toggleable, all OR'd into a single SmartFiltersPass(rank, direction) check:
//   - EMA (macro trend): a SEPARATE, longer/stricter EMA from any per-signal trend check
//     already inside the 10 signals -- only lets a signal fire aligned with price vs. this
//     macro EMA. Off by default since most signals already have their own trend logic;
//     this is an extra, broader filter on top, not a replacement.
//   - Large-bar: rejects a signal firing on a bar whose range is an outlier multiple of ATR
//     (news spike/gap) -- EXCEPT Slingshot and Breakout, which specifically require an
//     expansion/release bar by design (rejecting them there would fight their own purpose).
//   - S/R (structural support/resistance): rejects a signal sitting right at an untested
//     recent swing high/low -- but ONLY for the trend/momentum-CONTINUATION family (Trend
//     Cycle Wave, CrossFire, Continuation, EMA Breakout, Aggressive EMA, Volume Aggressive),
//     whose thesis ("the move keeps going") an imminent wall can invalidate. Deliberately
//     NOT applied to Reversal (whose thesis IS trading into a level) or to Zone
//     Break/Breakout/Slingshot (which trade THROUGH or off a level, not near one while
//     ranging) -- applying it there would suppress exactly the setups those signals exist
//     to catch.
//   - Session-time: only allows firing within a configured time-of-day window (handles an
//     overnight wrap, e.g. Start=18:00/End=17:00, by treating "outside [End,Start)" as
//     in-session instead of the reverse).
// WIRING: a Smart-Filters rejection is NOT a hard stop for the bar, unlike the Volatility
// Shield/Trade Protector gates above it -- it just disqualifies that one signal and the
// priority loop tries the next-ranked one instead (same treatment as a cooldown miss).
// This required restructuring the old if/else-if priority chain into an explicit loop
// (EvaluateSignal() dispatches to the right CheckX() by rank) since an if/else-if chain
// has no way to "fall through" past a rejected-but-otherwise-fired signal to the next one.
//
// SECTION 7 -- FIBGUARD TRADE MANAGER (2026-09-23 update, next phase per the roadmap in
// /areas/praedox-sentinel.md: "FibGuard Trade Manager (fib-based targets/stops/trailing,
// own ATM)"). Nothing in the surviving marketing copy specifies FibGuard's exact mechanics,
// so this is an original design built to the feature name and general description:
//   - LAYERS ON TOP of the existing ATR-based stop/target from SECTION 5 -- it does not
//     replace SetStopLoss/SetProfitTarget at entry. EnableFibGuard is a separate, independent
//     toggle (default OFF); when on, it only ever moves the stop AFTER entry, and only in
//     the trade's favor.
//   - Trails to the NEXT FIB LEVEL: at entry, the current 8-level fib grid (fibLevelPrices)
//     is copied into a frozen per-trade snapshot (fgEntryLevels) -- frozen, not the live grid,
//     because UpdateFibEngine() re-anchors to a new swing every bar and a live-recalculated
//     grid would make the trail jump around unpredictably mid-trade. Every bar afterward,
//     UpdateFibGuardTrail() finds the snapshotted level closest to (but not past, plus a
//     FibGuardMinAdvanceTicks buffer) the current close on the favorable side, and if that
//     level is better than the current FibGuard stop, ratchets SetStopLoss to it (Price mode,
//     same signal name as the entry). It only tightens -- never loosens, never overrides a
//     level with a worse one.
//   - Before the first level is cleared, the ORIGINAL ATR-based stop from entry still stands
//     untouched -- FibGuard has nothing to improve on yet.
//   - Resets on every flat transition (OnPositionUpdate) so the next trade starts from a
//     fresh snapshot, not the previous trade's levels.
//   - Deliberately does NOT touch the profit target -- only the stop. Surviving copy
//     mentions "targets" for FibGuard too, but with no further detail; a fib-level trailing
//     stop is the mechanic that most directly matches the "own ATM" framing without
//     guessing at target logic the copy never described. Can be extended later if a
//     screenshot surfaces showing target behavior specifically.
//
// SECTION 8 -- COCKPIT UI (2026-09-23 update, next phase per the roadmap in
// /areas/praedox-sentinel.md: "a full trading cockpit UI (manual entries, 4 ATM slots, 4
// trailing-stop methods, 18 sound alert slots)"). The surviving marketing copy names these
// four pieces but gives no mechanics, so every design choice below is original, documented
// as it's made rather than guessed at silently:
//   - ATM SLOTS GOVERN BOTH ENTRY PATHS: EnableCockpitAtm (off by default) is a separate
//     toggle from EnableFibGuard -- the two are MUTUALLY EXCLUSIVE by design (running both
//     would mean two systems calling SetStopLoss on the same signal every bar, fighting
//     each other). When EnableCockpitAtm is on, the active ATM slot's StopTicks/TargetTicks
//     replace the Volatility-Shield-derived sizing for AUTO-SIGNAL entries (TryEnterOnSignal)
//     -- this is what "governs both auto-signal entries and manual entries" (asked and
//     confirmed) means for entry SIZING. TRAILING (UpdateAtmTrail) also runs for manual
//     trades once EnableCockpitAtm is on, since the active slot is meant to be the one
//     consistent trade-management profile regardless of how the trade was opened.
//   - MANUAL ENTRY: unlike auto-signal entries, manual Buy/Sell use the panel's own
//     editable Qty/SL/TP textboxes (TrySubmitManualEntry), not the ATM slot's stop/target
//     -- per the explicit choice of "custom size/SL/TP inputs" over slot-driven sizing.
//     Manual entries skip EnableAutoTrading and the Volatility Shield TradingAllowed gate
//     (a human clicking Buy is an explicit override) but still respect Trade Protector's
//     daily block -- that's an account-risk circuit breaker, not a signal-quality filter,
//     so it applies regardless of how the entry was requested.
//   - MANUAL ENTRY THREADING: WPF Button.Click handlers run on the chart's UI dispatcher
//     thread. NinjaScript's order-management methods (EnterLong, SetStopLoss, etc.) must
//     only be called from the strategy's own engine thread -- calling them directly from a
//     Click handler is not safe. So the Click handlers only set small volatile fields
//     (manualBuyPending/manualSellPending/manualFlattenPending + the queued qty/stop/target),
//     and ProcessManualCockpitRequests() drains them at the top of the next OnBarUpdate.
//     Consequence: since this strategy runs Calculate.OnBarClose, a manual click takes
//     effect at the NEXT BAR CLOSE, not instantly on click. That's a deliberate, documented
//     tradeoff (switching to Calculate.OnEachTick would change bar timing for every other
//     check in the strategy) -- not an attempt to fake tick-instant manual trading.
//   - 4 TRAIL METHODS (PraedoxTrailMethod enum), one per ATM slot, dispatched in
//     UpdateAtmTrail(): AtrTrail (chandelier: TrailParam * ATR behind price), FixedTicksTrail
//     (constant TrailParam-tick distance), FibLevelTrail (same mechanic as Section 7's
//     FibGuard -- entry-frozen fib grid snapshot, TrailParam = min-advance-ticks buffer),
//     SwingPointTrail (most recent TrailParam-bar swing low/high). All four ratchet only,
//     same discipline as FibGuard.
//   - 18 SOUND ALERTS: one enable-toggle + one sound-file property per event (all default
//     ON, using NinjaTrader's bundled Alert1-4.wav, independently re-pointable to any file
//     under NinjaTrader 8's own sounds folder). Mapped to: the 10 signals firing, Volatility
//     Shield entering Elevated/Extreme (edge-triggered on the transition, not every bar it
//     stays there), Trade Protector's 4 block reasons (win streak/loss streak/max profit/max
//     loss), a fib flip, and order submission ("OrderFilled" -- fires at SUBMISSION via
//     FireAlert in OnTradeEntered, not off an execution-fill callback; for the Managed
//     approach's market-order entries these are effectively simultaneous, so this was kept
//     simple rather than adding an OnExecutionUpdate override for a difference that
//     wouldn't be visible in practice).
//   - COCKPIT PANEL LIFECYCLE: the on-chart WPF panel (4 ATM slot buttons, active-slot
//     readout, Qty/SL/TP textboxes, Buy/Sell/Flatten) is built in CreateCockpitPanel(),
//     called via ChartControl.Dispatcher.InvokeAsync from OnStateChange(State.Historical)
//     -- State.Historical is used (rather than SetDefaults/DataLoaded) because ChartControl
//     is only guaranteed non-null once the script is actually attached to a chart. Removed
//     the same way from OnStateChange(State.Terminated), so recompiling/removing the script
//     doesn't leave orphaned controls on the chart. ShowCockpitPanel (default on) can turn
//     the whole panel off. This is new territory for this project -- every other section
//     draws with the Draw.* chart-drawing API, not raw WPF controls added to the chart's
//     own Grid -- so it's the piece most likely to need a compile/behavior iteration pass.
//
// COCKPIT DASHBOARD (2026-09-23, second update) -- the panel above was a first pass built
// from the feature LIST in the marketing copy (ATM slots, manual entry, sound alerts) before
// a reference screenshot of the actual dashboard surfaced. That screenshot shows a much
// larger docked panel -- PnL summary, win-rate/W-L/max-profit/max-loss stats, an equity
// curve, a last-5-trades table, Trading-On/Raw-Signal-Mode/Chop-Filter status, an 11-button
// signal grid, and a full Trade Management block (Buy Up/Sell Down, Buy Click/Sell Click,
// Move-To-BE with a stepper, Stop/Tgt nudge, Stop/Tgt ATR, Flatten All) -- so this section was
// rebuilt to match it. Original design decisions made to fill the gaps the screenshot itself
// doesn't specify (colors/exact pixel layout aside, which are visual judgment calls, not
// logged individually):
//   - DOCKING VS. FLOATING OVERLAY: the screenshot shows the panel occupying its own column
//     to the right of price, not floating over candles. A TRUE dock (shrinking NinjaTrader's
//     own chart plot width via its host Grid's column definitions) was evaluated and rejected
//     -- it requires walking up ChartControl's parent chain into NinjaTrader's own internal
//     chart-window Grid (the same one the abandoned MainTabControl.Parent approach reached
//     earlier in this project) and inserting a column into UI NinjaTrader owns and can change
//     across versions/updates without notice; that's a materially different risk than adding
//     our own child to ChartControl.Children (confirmed-working, NinjaTrader-forum-sourced
//     pattern used everywhere else in this file). Instead: the panel is anchor-right
//     (HorizontalAlignment.Right, Stretch vertically) via the SAME ChartControl.Children.Add
//     approach as before, opaque (no need for transparency now that it isn't sitting over the
//     middle of the chart), and COLLAPSIBLE via the "<"/">" button in its title bar (collapses
//     to a 26px strip) -- so on a narrow chart or when full visibility of the right edge is
//     wanted, one click clears it instead of the strategy fighting NinjaTrader's own chrome.
//     This is a deliberate, disclosed compromise from true grid docking, not a silent one.
//   - TRADE STATS SOURCE: win rate, W-L record, max win/loss, the equity curve, and the
//     last-5-trades table all read from NinjaScript's own SystemPerformance.AllTrades
//     (TradeCollection of every closed trade, confirmed via NinjaTrader's own docs) rather
//     than a hand-rolled trade log -- reuses NT8's built-in bookkeeping instead of duplicating
//     it. Per-trade members (Entry.Name/Entry.Price/Exit.Price/ProfitCurrency) were confirmed
//     less exhaustively than most other APIs in this file (NinjaTrader's public docs page for
//     the Trade class wasn't reachable during this build), so this read is wrapped in its own
//     try/catch with a Print() on failure -- if the dashboard's trade stats don't populate,
//     check the Output window for "Cockpit trade-stats read failed" and treat it as the next
//     thing to fix, the same way the panel-build try/catch has worked all along.
//   - MAX+ / MAX-: mapped to the EXISTING MaxDailyProfitDollars/MaxDailyLossDollars (Trade
//     Protector's daily limits), shown as the configured limits -- not the largest single
//     win/loss ever taken. Reuses state that already existed rather than inventing a new
//     "biggest trade ever" concept the screenshot doesn't clearly specify either way.
//   - SIGNAL GRID: the screenshot's grid has 11 labels including "Volume Climax," which has
//     no equivalent among this rebuild's 10 real signals (see SECTION 1). Rather than invent
//     an 11th fake signal to hit the visual count, the grid here has exactly the 10 real
//     ones -- each button toggles that signal's own Enable* property live (green = on).
//   - RAW SIGNAL MODE: interpreted as "bypass Smart Filters entirely" (see SmartFiltersPass) --
//     cooldown, Volatility Shield, and Trade Protector are risk controls, not signal-quality
//     filters, and stay active even in Raw Signal Mode.
//   - CHOP FILTER READOUT: shows the live Choppiness Index value next to its ceiling
//     (ChopCeiling) -- "value / ceiling" is this rebuild's reading of the screenshot's
//     unlabeled "3 / 38.2"-style readout, since no other pairing in this codebase produces
//     two comparably-scaled chop numbers.
//   - BUY UP/SELL DOWN vs. BUY CLICK/SELL CLICK: two distinct one-click entry paths, an
//     original split for this rebuild. BUY UP/SELL DOWN (TrySubmitAtmQuickEntry) use the
//     ACTIVE ATM SLOT's preset stop/target -- fast entry at a known, pre-configured risk
//     profile. BUY CLICK/SELL CLICK are the original manual-entry path (TrySubmitManualEntry)
//     using the panel's own SL/TP textboxes for fully custom risk. Both read Qty from the
//     same textbox and both go through the same volatile-flag/ProcessManualCockpitRequests
//     hand-off as every other panel button -- see "MANUAL ENTRY THREADING" above.
//   - MOVE TO BE / STOP-TGT NUDGE / STOP-TGT ATR: all act on activeEntrySignalName, a new
//     field set by OnTradeEntered on EVERY entry path (auto, FibGuard/ATM-managed, manual,
//     quick) so these buttons work on whatever is currently open regardless of how it was
//     opened. Nudge buttons track activeStopTicks/activeTargetTicks (distance-from-entry in
//     ticks) because SetStopLoss(CalculationMode.Ticks) sets distance FROM ENTRY, not from
//     the current stop or current price -- so "nudge by N ticks" re-submits with the adjusted
//     distance rather than trying to read NinjaTrader's live order state back out. STOP
//     ATR/TGT ATR instead submit an absolute price (QuickAtrMultiple * ATR from the current
//     close, floored at QuickAtrMinTicks), since that button's intent is "distance from price
//     right now," not from entry.
//   - TRADING ON / RAW SIGNAL MODE buttons toggle EnableAutoTrading/RawSignalMode directly
//     from the UI thread (not queued through the volatile-flag hand-off) -- both are plain
//     bool auto-properties, the same kind NinjaScript's own property grid already lets a user
//     flip live on a running strategy (EnableCockpitAtm, EnableFibGuard, etc.), and a CLR bool
//     read/write can't tear, so the only consequence of the race is the new value taking
//     effect within about a bar -- acceptable for a manual toggle, unlike order-management
//     calls which must go through the engine thread.
//
// SESSION REPORT (2026-09-24, final roadmap phase) -- the surviving marketing copy names "a
// Session Trading Summary HTML report" with no further detail, so every mechanic below is an
// original choice, confirmed with the user via AskUserQuestion:
//   - TRIGGER: BOTH auto-generation AND a manual button. Auto-generation fires from two
//     places: CheckSessionReportBoundary() (called every OnBarUpdate) uses NinjaScript's own
//     Bars.IsFirstBarOfSession -- true on the first bar of every new trading session -- to
//     detect that the PREVIOUS session just ended and write its report before resetting the
//     trade-index baseline for the new one; and OnStateChange(State.Terminated) writes a
//     final flush of whatever the current (possibly still-open) session has, as a safety net
//     for when the strategy is stopped/removed mid-session and no "next session's first bar"
//     ever arrives to trigger the normal path. The Cockpit panel's REPORT button
//     (sessionReportManualPending, drained in ProcessManualCockpitRequests like every other
//     panel button) covers "just show me a snapshot right now." Note (from NinjaTrader's own
//     docs): IsFirstBarOfSession is always false on non-intraday chart periods (Day, Month,
//     etc.) -- on those the manual button and the Terminated flush are the only paths that
//     will ever produce a report.
//   - SCOPE: today's session only, not all-time -- sessionReportStartTradeIndex tracks the
//     index into SystemPerformance.AllTrades where the CURRENT session's trades begin
//     (same pattern as Trade Protector's own tpRealizedBaseline, but a trade-list index
//     rather than a running $ total, since the report needs the actual trade rows).
//   - SAVE LOCATION: a single fixed file (SessionReportPath, default under NinjaTrader's own
//     UserDataDir), overwritten every time rather than one dated file per session -- simplest
//     to find and always up to date, at the cost of not keeping a history; if a per-session
//     archive is ever wanted, that's a small follow-up (timestamp the filename instead of
//     overwriting), not a redesign.
//   - CONTENT/SOURCE: same SystemPerformance.AllTrades trade-stats source as the Cockpit
//     dashboard (see "TRADE STATS SOURCE" above) -- wins/losses/win%/record/avg-win/avg-loss/
//     max-win/max-loss plus a per-trade table (signal name, direction, entry/exit time and
//     price, $ P&L). Same unverified-API caveat and the same defensive try/catch-with-Print()
//     pattern as everywhere that reads it.
//
// THREE-PANEL DASHBOARD (2026-09-24) -- the user's original product had 3 separate
// workspace-tab concepts visible in the surviving reference material: "Praedox Market
// Snapshot," "Praedox Sentinel - A.I. Control Center," and "Praedox Sentinel - A.I. Trade
// Analyzer." No reference screenshots or video exist for the Snapshot or Analyzer tabs
// specifically (confirmed with the user) -- only the Control Center dashboard (the existing
// Cockpit panel, unchanged) has surviving material behind it. So Snapshot and Analyzer below
// are original designs, built to the feature NAMES only, same standard as the rest of this
// rebuild. LAYOUT: NinjaScript cannot create separate chart-WINDOW tabs from inside a
// Strategy -- that's a NinjaTrader workspace/UI feature, not something a script can add to.
// Confirmed with the user and built instead as ONE docked panel (unchanged position/sizing)
// with a 3-way tab-button row (SNAPSHOT/CONTROL/ANALYZER) at the top of the scrollable body,
// switching which of 3 StackPanels is visible (SetActiveCockpitTab) -- cockpitBodyStack is
// unchanged and IS the Control Center tab's content. The existing collapse ("<"/">") button
// was re-pointed from cockpitBodyStack to a new wrapping StackPanel (cockpitCollapsibleWrap,
// tab row + scroll host together) so it now collapses whichever tab is currently showing,
// not just Control Center specifically -- a correctness fix required by the architecture
// change, not a cosmetic one.
//
// MARKET SNAPSHOT TAB: a read-only current-conditions readout, no trading actions (confirmed
// with the user) -- 7 rows: instrument + last price; Volatility Shield's state name + raw VR
// value; ATR in price and ticks; the Chop Filter's value/ceiling pair (same reading as the
// Control tab's own chop readout, restated here for a single-glance snapshot); the Fib
// grid's direction plus Kill Zone/Exhaustion/Stale flags; the macro Trend EMA vs. Fast EMA
// vs. current price; and a bar-time + Trading-Allowed/Protector-block line. All values are
// cheap reads already computed elsewhere in OnBarUpdate (no new indicator instances), just
// surfaced in one place -- this tab duplicates nothing the Control tab's own status row
// doesn't already show individually, its value is having it all in one read-only glance
// without the trading controls mixed in.
//
// TRADE ANALYZER TAB: all-time (not session-scoped -- confirmed with the user, matching the
// existing Control tab's PnL/win-rate stats, which are also all-time) per-signal performance
// analytics, extending the SAME SystemPerformance.AllTrades loop the Control tab already
// runs (not a second, duplicate read) to additionally accumulate: a 100-point-capped equity
// curve (vs. the Control tab's 40-point one -- more room here since this tab isn't squeezed
// next to a stats grid), profit factor (gross win / gross loss, or gross win alone when there
// are no losses yet -- NinjaTrader's own Strategy Analyzer shows "infinite" in that case; a
// plain number reads better in a small tile), expectancy ($ per trade, signed), a running
// peak-to-trough max drawdown, and a per-signal breakdown (trade count, win %, $ P&L) via the
// new MapSignalDisplayName() helper, which maps a Trade's raw Entry.Name ("Praedox1".."10",
// "PraedoxManual", "PraedoxQuick") back to the same display names used in the signal grid and
// Trade Management section, so this tab reads consistently with the rest of the dashboard
// rather than showing raw internal tags. Breakdown rows are ordered rank 1-10 then Manual
// Entry/Quick Entry (ATM) -- a FIXED order, not sorted by P&L, so a signal's row doesn't jump
// position to position as trades close; signals with zero trades so far are omitted rather
// than shown as an empty row. Same unverified-Trade-field-access caveat and try/catch as the
// Control tab's own read of this collection -- see "TRADE STATS SOURCE" above.

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum PraedoxSmartTraderMode
    {
        GreenOnly,
        GreenAndYellow,
        GreenYellowAndRed
    }

    // Trailing-stop method for a Cockpit ATM slot -- see SECTION 8 header comment.
    public enum PraedoxTrailMethod
    {
        AtrTrail,
        FixedTicksTrail,
        FibLevelTrail,
        SwingPointTrail
    }

    public class PraedoxSentinel_v0_1_0 : Strategy
    {
        // ============================================================================
        // SECTION 1 FIELDS -- Signal Engine
        // ============================================================================
        private EMA trendEma;
        private EMA fastEma;
        private ATR atr;
        private RSI rsi;
        private SMA volAvg;
        private Series<double> stochKRaw;
        private SMA stochKSmooth;

        private int lastFireBar = -1;
        private int lastFireDirection;
        private int lastFireRank;
        private readonly int[] lastFireBarByRank = new int[11];
        private int lastLabelBar = -10000;
        private int labelStackCount;
        private double currentChop;

        // Smart Filters: EMA macro-trend filter -- see "SMART FILTERS WIRING" in the file
        // header and SmartFiltersPass() further down.
        private EMA macroEma;

        private static readonly string[] SignalNames =
        {
            "", // unused, ranks are 1-based
            "Slingshot", "Zone Break", "Trend Cycle Wave", "CrossFire", "Reversal",
            "Continuation", "EMA Breakout", "Aggressive EMA", "Volume Aggressive", "Breakout"
        };

        // ============================================================================
        // SECTION 2 FIELDS -- Fib Engine
        // ============================================================================
        private static readonly double[] FibLevelPercents = { 0, 23.6, 38.2, 50, 61.8, 65, 78.6, 100 };
        private static readonly string[] FibLevelLabels = { "0%", "23.6%", "38.2%", "50%", "61.8%", "65%", "78.6%", "100%" };
        private static readonly SimpleFont fibLabelFont = new SimpleFont("Arial", 12) { Bold = true };

        private double[] fibLevelPrices = new double[8];
        private int fibLastDirection;
        private int fibAnchorBarsAgo;
        private int fibDirection;
        private double fibNearKey;      // signed: >0 near key level while Up, <0 while Down, 0 = not near
        private bool fibInKillZone;
        private bool fibExhaustionActive;

        // ============================================================================
        // SECTION 3 FIELDS -- Volatility Shield / Smart Trader
        // ============================================================================
        private ATR vrAtr;
        private SMA vrAtrBaseline;
        private static readonly string[] VolStateNames = { "NORMAL", "LOWER VOL", "ELEVATED", "EXTREME" };
        private static readonly SimpleFont volLabelFont = new SimpleFont("Arial", 12) { Bold = true };
        private static readonly string[] VolStateMessages =
        {
            "NORMAL", "LOWER VOLATILITY - EXERCISE CAUTION", "ELEVATED VOLATILITY", "EXTREME VOLATILITY - TRADING BLOCKED"
        };

        private double currentVr;
        private int currentVolState;
        private bool currentVolAllowed;
        private double currentMinStopTicks;
        private double currentMinTargetTicks;
        private bool currentVolWarmedUp;

        // ============================================================================
        // SECTION 4 FIELDS -- Trade Protector / Daily P&L Limits
        // ============================================================================
        private static readonly SimpleFont protectorPanelFont = new SimpleFont("Arial", 12) { Bold = true };
        private static readonly SimpleFont protectorWarningFont = new SimpleFont("Arial", 14) { Bold = true };

        private bool tpStateLoaded;
        private string tpStateFilePath;
        private DateTime tpSessionDate;
        private double tpRealizedBaseline;
        private double tpLastFlatRealizedPnl;
        private double tpCurrentRealizedPnl;
        private int tpWinStreak;
        private int tpLossStreak;
        private bool tradeProtectorBlocked;
        private int tpBlockReasonCode; // 0=none, 1=win streak, 2=loss streak, 3=max profit, 4=max loss
        private bool tpBaselineInitialized;

        private static readonly string[] TpBlockReasonText =
        {
            "", "WIN STREAK LIMIT REACHED", "LOSS STREAK LIMIT REACHED", "MAX DAILY PROFIT REACHED", "MAX DAILY LOSS REACHED"
        };

        // ============================================================================
        // SECTION 5 FIELDS -- Order Execution
        // ============================================================================
        private static readonly SimpleFont overlayFont = new SimpleFont("Arial", 12) { Bold = true };

        // ============================================================================
        // SECTION 7 FIELDS -- FibGuard Trade Manager (see "FIBGUARD" header comment)
        // ============================================================================
        private readonly double[] fgEntryLevels = new double[8]; // snapshot of fibLevelPrices at entry -- frozen for the life of the trade, not live-recalculated, so the trail doesn't jump around as the grid re-anchors mid-trade
        private int fgEntryDirection;
        private string fgSignalName;
        private double fgCurrentStopPrice = double.NaN;
        private bool fgActive;

        // ============================================================================
        // SECTION 8 FIELDS -- Cockpit UI (ATM slots, manual entry panel, sound alerts)
        // WPF control types fully qualified (System.Windows.Controls.*) rather than a
        // blanket "using System.Windows.Controls;" -- defensive against a name collision
        // with something in the already-imported NinjaTrader.Gui.Chart/NinjaTrader.Gui
        // namespaces (same caution this project has applied elsewhere to uncertain
        // compiler behavior -- see the out-var avoidance in the Trade Protector file).
        // ============================================================================
        private System.Windows.Controls.Button flattenButton;
        private readonly System.Windows.Controls.Button[] atmSlotButtons = new System.Windows.Controls.Button[4];
        private System.Windows.Controls.TextBox qtyTextBox;
        private System.Windows.Controls.TextBox stopTextBox;
        private System.Windows.Controls.TextBox targetTextBox;
        private System.Windows.Controls.TextBlock activeSlotLabel;
        private ChartControl cockpitChartControl;

        // Cross-thread hand-off: button Click handlers run on the UI dispatcher thread,
        // NOT the strategy engine thread, so they must not call EnterLong/SetStopLoss/etc.
        // directly -- see "MANUAL ENTRY THREADING" in the SECTION 8 header comment. These
        // flags are just booleans (safe to set from either thread) and are drained at the
        // top of the next OnBarUpdate.
        private volatile bool manualBuyPending;
        private volatile bool manualSellPending;
        private volatile bool manualFlattenPending;
        private volatile int manualPendingQty;
        private volatile int manualPendingStopTicks;
        private volatile int manualPendingTargetTicks;

        private int activeAtmSlotRuntime = 1; // mirrors ActiveAtmSlot at startup, changeable live via the slot buttons without recompiling
        private string atmSignalName; // signal name of the currently-open ATM/manual-managed trade, for SetStopLoss(signalName, ...)
        private readonly double[] atmEntryFibLevels = new double[8]; // per-trade snapshot for TrailMethod.FibLevelTrail, independent of Section 7's fgEntryLevels (Section 7's EnableFibGuard and Section 8's EnableCockpitAtm are mutually exclusive -- see header comment)
        private int atmEntryDirection;
        private double atmCurrentStopPrice = double.NaN;
        private bool atmTradeActive;

        // ----------------------------------------------------------------------------
        // Cockpit DASHBOARD additions (full rebuild of the panel to match the surviving
        // marketing screenshot -- see "COCKPIT DASHBOARD" in the SECTION 8 header comment
        // for the full design rationale on every field/button below).
        // ----------------------------------------------------------------------------

        // Tracks whichever signal name most recently opened the current position,
        // regardless of entry path (auto-signal, FibGuard/ATM-managed, manual, or the new
        // quick-entry buttons) -- lets the Trade Management row's buttons (Move-To-BE,
        // Stop/Tgt nudge, Stop/Tgt ATR) act on "whatever is currently open" without needing
        // to know which of the 3 entry paths opened it. Set in OnTradeEntered, cleared on
        // the flat transition in OnPositionUpdate.
        private string activeEntrySignalName;
        private int activeStopTicks;   // current stop distance from entry, in ticks -- nudge buttons adjust this and re-submit
        private int activeTargetTicks; // current target distance from entry, in ticks -- nudge buttons adjust this and re-submit
        private volatile int moveToBeOffsetTicks; // ticks of locked-in profit the MOVE TO BE button applies; adjusted live by the -/+ stepper

        // Session Trading Summary report -- see "SESSION REPORT" in the SECTION 8 header
        // comment. sessionReportStartTradeIndex is the index into SystemPerformance.AllTrades
        // where the CURRENT session's trades begin (mirrors Trade Protector's own
        // tpRealizedBaseline pattern, but as a trade-list index rather than a $ baseline,
        // since the report needs the actual trade rows, not just a running total).
        private int sessionReportStartTradeIndex;
        private bool sessionReportSeenFirstBar; // guards against reporting on the very first Bars.IsFirstBarOfSession (CurrentBar 0), which has nothing prior to report
        private volatile bool sessionReportManualPending; // set by the Cockpit panel's REPORT button

        // Cross-thread hand-off for every Trade Management button added below -- same
        // volatile-flag-drained-in-OnBarUpdate pattern as the original manualBuyPending/
        // manualSellPending/manualFlattenPending (see "MANUAL ENTRY THREADING"). Two more
        // entry flags (quickAtmBuyPending/quickAtmSellPending) cover the new BUY UP/SELL
        // DOWN buttons, which are a second entry path distinct from BUY CLICK/SELL CLICK.
        private volatile bool quickAtmBuyPending;
        private volatile bool quickAtmSellPending;
        private volatile bool moveToBePending;
        private volatile bool nudgeStopTightenPending;
        private volatile bool nudgeStopWidenPending;
        private volatile bool nudgeTargetTightenPending;
        private volatile bool nudgeTargetWidenPending;
        private volatile bool quickStopAtrPending;
        private volatile bool quickTargetAtrPending;
        private volatile bool flattenAllPending; // account-wide FlattenAccount(), distinct from manualFlattenPending which only exits this instrument's position

        // Dashboard WPF controls -- header/stats/curve/history/status/signal-grid/trade-mgmt
        // sections, in the order they're built in CreateCockpitPanel().
        private System.Windows.Controls.Border cockpitOuterBorder;
        // Independent, wall-clock-paced self-heal for the "showed for a second then
        // disappeared" symptom -- see "COCKPIT DASHBOARD" header comment. The existing
        // per-bar re-attach check inside UpdateCockpitPanelState only runs on bar CLOSES
        // (this strategy is Calculate.OnBarClose), so on a slow timeframe a dropped panel
        // could stay gone for minutes before that check next ran. This timer ticks on the
        // UI thread every 2 seconds, independent of bar timing entirely, and does the same
        // Contains()-check-and-re-add.
        private System.Windows.Threading.DispatcherTimer cockpitHealTimer;
        private bool cockpitCollapsed;
        private System.Windows.Controls.Button collapseToggleButton;
        private System.Windows.Controls.StackPanel cockpitBodyStack;
        private System.Windows.Controls.TextBlock pnlValueText;
        private System.Windows.Controls.TextBlock realUnrlText;
        private System.Windows.Controls.TextBlock winRateText;
        private System.Windows.Controls.TextBlock wlRecordText;
        private System.Windows.Controls.TextBlock maxPlusText;
        private System.Windows.Controls.TextBlock maxMinusText;
        private System.Windows.Controls.Canvas equityCurveCanvas;
        private System.Windows.Controls.StackPanel lastTradesStack;
        private System.Windows.Controls.Button tradingOnButton;
        private System.Windows.Controls.Button rawSignalButton;
        private System.Windows.Controls.TextBlock chopFilterText;
        private readonly System.Windows.Controls.Button[] signalToggleButtons = new System.Windows.Controls.Button[11]; // 1-based, index 0 unused -- mirrors SignalNames
        private System.Windows.Controls.TextBlock moveToBeValueText;
        private System.Windows.Controls.TextBlock timeStatusText;

        // Three-panel tab switcher (Market Snapshot / Control Center / Trade Analyzer) --
        // see "THREE-PANEL DASHBOARD" in the SECTION 8 header comment. NinjaScript can't
        // create separate chart-window tabs from inside a Strategy (that's a NinjaTrader
        // workspace feature), so this is one docked panel whose body content swaps between
        // 3 StackPanels based on which tab button was last clicked -- cockpitBodyStack above
        // (unchanged) IS the Control Center tab's content; the two new stacks below are the
        // other two tabs.
        private int activeCockpitTab = 1; // 0 = Snapshot, 1 = Control (default), 2 = Analyzer
        private System.Windows.Controls.Button snapshotTabButton;
        private System.Windows.Controls.Button controlTabButton;
        private System.Windows.Controls.Button analyzerTabButton;
        // Wraps the tab row + scrollable content together -- the collapse ("<"/">") button
        // toggles THIS, not cockpitBodyStack, so collapsing hides whichever tab is showing
        // (not just the Control tab specifically).
        private System.Windows.Controls.StackPanel cockpitCollapsibleWrap;

        // -- Market Snapshot tab: read-only current market/indicator conditions, no trading
        // actions -- see "MARKET SNAPSHOT TAB" in the SECTION 8 header comment.
        private System.Windows.Controls.StackPanel cockpitSnapshotStack;
        private System.Windows.Controls.TextBlock snapshotPriceText;
        private System.Windows.Controls.TextBlock snapshotVrText;
        private System.Windows.Controls.TextBlock snapshotAtrText;
        private System.Windows.Controls.TextBlock snapshotChopText;
        private System.Windows.Controls.TextBlock snapshotFibText;
        private System.Windows.Controls.TextBlock snapshotTrendText;
        private System.Windows.Controls.TextBlock snapshotSessionText;

        // -- Trade Analyzer tab: all-time (not session-scoped) trade analytics, broken down
        // per signal -- see "TRADE ANALYZER TAB" in the SECTION 8 header comment.
        private System.Windows.Controls.StackPanel cockpitAnalyzerStack;
        private System.Windows.Controls.TextBlock analyzerSummaryText;
        private System.Windows.Controls.Canvas analyzerCurveCanvas;
        private System.Windows.Controls.StackPanel analyzerSignalBreakdownStack;

        // NinjaScript auto-generates the on-chart legend/label text by listing every
        // [NinjaScriptProperty] value in parentheses after the script name -- fine for an
        // indicator with a handful of properties, but with ~150 here (Cockpit's ATM slots
        // and 18x2 sound alert properties alone are most of that) it became a huge unreadable
        // wall of text overlapping the status overlay at top-left, as seen in the first
        // Cockpit UI compile. The chart label is controlled by DisplayName, NOT ToString()
        // -- a ToString() override (tried first) did nothing, confirmed by NinjaTrader's own
        // forum. Returning a non-empty fixed string (not "") avoids a known NT8 bug where a
        // blank DisplayName breaks the "Attach to" dropdown on drawing tools.
        public override string DisplayName
        {
            get { return "PraedoxSentinel_v0_1_0"; }
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "PraedoxSentinel_v0_1_0";
                Description = "Unified rebuild: Signal Engine (10 signals) + Fib Engine + Volatility Shield/Smart Trader + Trade Protector/Daily P&L Limits + automated order execution, all in one Strategy. Original implementation from behavior spec (no surviving source). EnableAutoTrading defaults OFF.";

                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = true;
                IsInstantiatedOnEachOptimizationIteration = false;

                // -- Signal Engine: Core --
                TrendEmaLength = 50;
                FastEmaLength = 20;
                AtrLength = 14;
                RsiLength = 14;
                StochLength = 14;
                StochSmooth = 3;
                VolumeAvgLength = 20;
                CooldownBars = 8;

                // -- Signal Engine: signal-specific --
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
                ApplyChopFilterBroadly = true;

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

                GroupZone = true;
                GroupReversal = true;
                GroupBreakout = true;
                GroupCycle = true;
                GroupMomentum = true;

                LabelDeclutterBars = 6;
                LabelOffsetAtrMultiple = 0.35;
                DrawLabels = true;

                // -- Fib Engine --
                EnableFibConfluence = true;
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

                // -- Volatility Shield / Smart Trader --
                EnableVolatilityShield = true;
                VrAtrLength = 14;
                VrBaselineLength = 50;
                YellowLowThreshold = 0.85;
                RedHighThreshold = 1.5;
                DarkRedThreshold = 2.5;
                GreenMinStopAtrMult = 1.0;
                GreenMinTargetAtrMult = 2.0;
                YellowMinStopAtrMult = 1.3;
                YellowMinTargetAtrMult = 2.2;
                RedMinStopAtrMult = 1.8;
                RedMinTargetAtrMult = 2.6;
                DarkRedMinStopAtrMult = 2.5;
                DarkRedMinTargetAtrMult = 3.2;
                EnableSmartTrader = true;
                Mode = PraedoxSmartTraderMode.GreenAndYellow;
                DrawVolShieldLabel = true;

                // -- Trade Protector / Daily P&L Limits --
                EnableTradeProtector = true;
                MaxConsecutiveWins = 5;
                MaxConsecutiveLosses = 3;
                EnableDailyPnLLimits = true;
                MaxDailyProfitDollars = 500;
                MaxDailyLossDollars = 500;
                AutoFlatten = true;
                ShowProtectorPanel = true;

                // -- Smart Filters --
                EnableMacroEmaFilter = false;
                MacroEmaLength = 100;
                EnableLargeBarFilter = true;
                LargeBarAtrMultiple = 3.0;
                EnableSrProximityFilter = true;
                SrLookback = 100;
                SrProximityTicks = 6;
                EnableSessionTimeFilter = false;
                SessionStartTime = new TimeSpan(9, 30, 0);
                SessionEndTime = new TimeSpan(16, 0, 0);

                // -- Order Execution --
                EnableAutoTrading = false; // SAFETY DEFAULT -- see "ORDER EXECUTION" header comment
                Contracts = 1;

                // -- FibGuard Trade Manager --
                EnableFibGuard = false; // off by default -- layers on top of the ATR stop/target above, see "FIBGUARD" header comment
                FibGuardMinAdvanceTicks = 4;

                // -- Cockpit: ATM Slots (4 preset SL/TP/trail configs -- see SECTION 8 header
                // comment; these starting numbers are original/arbitrary since the surviving
                // marketing copy names the feature but not its defaults -- tune to taste) --
                EnableCockpitAtm = false;
                ActiveAtmSlot = 1;
                Atm1StopTicks = 20; Atm1TargetTicks = 40; Atm1TrailMethod = PraedoxTrailMethod.AtrTrail; Atm1TrailParam = 1.5;
                Atm2StopTicks = 40; Atm2TargetTicks = 80; Atm2TrailMethod = PraedoxTrailMethod.FixedTicksTrail; Atm2TrailParam = 20;
                Atm3StopTicks = 40; Atm3TargetTicks = 100; Atm3TrailMethod = PraedoxTrailMethod.FibLevelTrail; Atm3TrailParam = 4;
                Atm4StopTicks = 60; Atm4TargetTicks = 150; Atm4TrailMethod = PraedoxTrailMethod.SwingPointTrail; Atm4TrailParam = 20;

                // -- Cockpit: Manual Entry --
                ShowCockpitPanel = true;
                ManualDefaultQty = 1;
                ManualDefaultStopTicks = 20;
                ManualDefaultTargetTicks = 40;

                // -- Cockpit: Dashboard (rebuilt 2026-09-23 to match the surviving marketing
                // screenshot -- see "COCKPIT DASHBOARD" in the SECTION 8 header comment) --
                RawSignalMode = false;
                QuickNudgeTicks = 10;
                QuickAtrMultiple = 0.5;
                QuickAtrMinTicks = 3;
                MoveToBeStepTicks = 5;

                // -- Session Trading Summary report (final roadmap phase) -- see "SESSION
                // REPORT" in the SECTION 8 header comment --
                EnableSessionReport = true;
                SessionReportPath = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "PraedoxSentinel_SessionReport.html");

                // -- Cockpit: Sound Alerts (18 slots, all on by default with NT8's own
                // bundled sounds -- each independently toggleable/re-pointable, see SECTION
                // 8). Default sound values use NinjaTrader's own documented Alert() example
                // pattern (InstallDir + "\sounds\AlertN.wav") rather than a bare filename --
                // a bare filename isn't confirmed to resolve, and there's no reason to guess
                // when the official example shows the full-path form.
                string soundDir = NinjaTrader.Core.Globals.InstallDir + @"\sounds\";
                EnableAlertSlingshot = true; AlertSlingshotSound = soundDir + "Alert1.wav";
                EnableAlertZoneBreak = true; AlertZoneBreakSound = soundDir + "Alert1.wav";
                EnableAlertTrendCycleWave = true; AlertTrendCycleWaveSound = soundDir + "Alert1.wav";
                EnableAlertCrossFire = true; AlertCrossFireSound = soundDir + "Alert1.wav";
                EnableAlertReversal = true; AlertReversalSound = soundDir + "Alert1.wav";
                EnableAlertContinuation = true; AlertContinuationSound = soundDir + "Alert1.wav";
                EnableAlertEmaBreakout = true; AlertEmaBreakoutSound = soundDir + "Alert1.wav";
                EnableAlertAggressiveEma = true; AlertAggressiveEmaSound = soundDir + "Alert1.wav";
                EnableAlertVolumeAggressive = true; AlertVolumeAggressiveSound = soundDir + "Alert1.wav";
                EnableAlertBreakout = true; AlertBreakoutSound = soundDir + "Alert1.wav";
                EnableAlertVolShieldRed = true; AlertVolShieldRedSound = soundDir + "Alert2.wav";
                EnableAlertVolShieldDarkRed = true; AlertVolShieldDarkRedSound = soundDir + "Alert3.wav";
                EnableAlertTpWinStreak = true; AlertTpWinStreakSound = soundDir + "Alert2.wav";
                EnableAlertTpLossStreak = true; AlertTpLossStreakSound = soundDir + "Alert3.wav";
                EnableAlertTpMaxProfit = true; AlertTpMaxProfitSound = soundDir + "Alert2.wav";
                EnableAlertTpMaxLoss = true; AlertTpMaxLossSound = soundDir + "Alert3.wav";
                EnableAlertFibFlip = true; AlertFibFlipSound = soundDir + "Alert1.wav";
                EnableAlertOrderFilled = true; AlertOrderFilledSound = soundDir + "Alert4.wav";

                // -- Display --
                ShowStatusOverlay = true;

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

                vrAtr = ATR(VrAtrLength);
                vrAtrBaseline = SMA(vrAtr, VrBaselineLength);

                macroEma = EMA(MacroEmaLength);

                activeAtmSlotRuntime = ActiveAtmSlot;

                // Session Trading Summary report -- see "SESSION REPORT" in the SECTION 8
                // header comment. Reset fresh each time the strategy (re)loads; the boundary
                // check in OnBarUpdate takes it from here.
                sessionReportStartTradeIndex = 0;
                sessionReportSeenFirstBar = false;
            }
            else if (State == State.Historical)
            {
                // Cockpit WPF panel is created here (not SetDefaults/DataLoaded) because
                // ChartControl is only guaranteed non-null once the script is actually
                // attached to a chart -- see "COCKPIT PANEL LIFECYCLE" in the SECTION 8
                // header comment.
                if (ShowCockpitPanel && ChartControl != null)
                {
                    cockpitChartControl = ChartControl;
                    cockpitChartControl.Dispatcher.InvokeAsync(new Action(CreateCockpitPanel));
                }
            }
            else if (State == State.Terminated)
            {
                if (cockpitChartControl != null)
                    cockpitChartControl.Dispatcher.InvokeAsync(new Action(RemoveCockpitPanel));

                // Safety-net flush: if the strategy is stopped/removed mid-session (no
                // following session's first bar ever arrives to trigger the normal
                // boundary-crossing write in OnBarUpdate), still capture whatever the
                // current session had so far -- see "SESSION REPORT" header comment.
                if (EnableSessionReport)
                    GenerateSessionReport(sessionReportStartTradeIndex);
            }
        }

        protected override void OnBarUpdate()
        {
            int warmup = Math.Max(
                Math.Max(Math.Max(TrendEmaLength, BreakoutLookback), Math.Max(MacroEmaLength, SrLookback + 1)),
                Math.Max(Math.Max(ChopLength, StochLength), Math.Max(FibLookback + 1, VrAtrLength + VrBaselineLength + 2))
            ) + 5;

            EnsureTradeProtectorStateLoaded();

            // Drained every bar, even during warmup -- see "MANUAL ENTRY THREADING" in the
            // SECTION 8 header comment. Manual entries use fixed textbox values, not
            // indicator output, so they don't need the warmup gate below.
            ProcessManualCockpitRequests();

            // Session Trading Summary boundary check -- also runs during warmup (bar
            // structure, not indicator-dependent) so a session that starts and ends within
            // the warmup window is never missed. See "SESSION REPORT" header comment.
            if (EnableSessionReport)
                CheckSessionReportBoundary();

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

            UpdateFibEngine();

            int volStateBefore = currentVolState;
            UpdateVolatilityShield();
            FireVolShieldTransitionAlert(volStateBefore, currentVolState);

            double currentFibNearKey = EnableFibConfluence ? fibNearKey : 0;
            bool currentFibSuppressed = EnableFibConfluence && (fibInKillZone || fibExhaustionActive);

            int rank = 0;
            int direction = 0;

            // Evaluated strictly in priority order -- first signal that (a) isn't on
            // cooldown, (b) fires (returns non-zero), AND (c) passes Smart Filters wins.
            // Unlike (a)/(b), a Smart-Filters rejection does NOT stop the loop -- it just
            // skips that one signal and tries the next-priority one, same as a cooldown
            // miss -- see "SMART FILTERS WIRING" in the file header. currentVolAllowed
            // gates the whole chain (Volatility Shield); tradeProtectorBlocked gates it too.
            if (currentVolAllowed && !tradeProtectorBlocked)
            {
                for (int r = 1; r <= 10; r++)
                {
                    if (!CooldownOk(r))
                        continue;

                    int dir = EvaluateSignal(r, currentFibSuppressed, currentFibNearKey);
                    if (dir == 0)
                        continue;

                    if (!SmartFiltersPass(r, dir))
                        continue;

                    rank = r;
                    direction = dir;
                    break;
                }
            }

            if (rank != 0)
            {
                lastFireBar = CurrentBar;
                lastFireDirection = direction;
                lastFireRank = rank;
                lastFireBarByRank[rank] = CurrentBar;

                if (DrawLabels)
                {
                    labelStackCount = (CurrentBar - lastLabelBar <= LabelDeclutterBars) ? labelStackCount + 1 : 0;
                    lastLabelBar = CurrentBar;

                    string tag = "Praedox" + CurrentBar;
                    string text = rank + ". " + SignalNames[rank];
                    Brush color = direction > 0 ? Brushes.Lime : Brushes.OrangeRed;
                    double offset = atr[0] * LabelOffsetAtrMultiple * (1 + labelStackCount * 0.8);
                    double y = direction > 0 ? Low[0] - offset : High[0] + offset;
                    Draw.Text(this, tag, text, 0, y, color);
                }

                FireSignalAlert(rank);
                TryEnterOnSignal(rank, direction);
            }

            Values[0][0] = (CurrentBar == lastFireBar) ? lastFireDirection : 0;
            Values[1][0] = (CurrentBar == lastFireBar) ? lastFireRank : 0;

            DrawStatusOverlay();
            DrawTradeProtectorPanel();
            UpdateCockpitPanelState();

            // Runs every bar while a position is open, independent of whether a new
            // signal fired this bar. EnableCockpitAtm and EnableFibGuard are mutually
            // exclusive by design -- when both are on, the Cockpit ATM slot's own trail
            // method takes over and Section 7's FibGuard trail is skipped, since having
            // both call SetStopLoss on the same signal every bar would fight each other --
            // see "FIBGUARD" and SECTION 8 header comments.
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                if (EnableCockpitAtm && atmTradeActive)
                    UpdateAtmTrail();
                else if (EnableFibGuard && fgActive)
                    UpdateFibGuardTrail();
            }
        }

        // ============================================================================
        // SECTION 5 -- ORDER EXECUTION (see file header for the design choices here)
        // ============================================================================
        private void TryEnterOnSignal(int rank, int direction)
        {
            if (!EnableAutoTrading)
                return;
            if (Position.MarketPosition != MarketPosition.Flat)
                return; // only enters when flat -- see file header

            string signalName = "Praedox" + rank;

            // Cockpit ATM (if on) governs auto-signal entry sizing too -- see SECTION 8
            // header comment ("ATM SLOTS GOVERN BOTH ENTRY PATHS"). Falls back to the
            // existing Volatility-Shield-derived sizing when Cockpit ATM is off, so
            // default behavior (EnableCockpitAtm=false) is unchanged from before this phase.
            int stopTicks, targetTicks;
            if (EnableCockpitAtm)
            {
                int atmStopTicks, atmTargetTicks; PraedoxTrailMethod atmTrailMethod; double atmTrailParam;
                GetAtmSlotConfig(activeAtmSlotRuntime, out atmStopTicks, out atmTargetTicks, out atmTrailMethod, out atmTrailParam);
                stopTicks = Math.Max(1, atmStopTicks);
                targetTicks = Math.Max(1, atmTargetTicks);
            }
            else
            {
                stopTicks = (int)Math.Max(1, currentMinStopTicks);
                targetTicks = (int)Math.Max(1, currentMinTargetTicks);
            }

            SetStopLoss(signalName, CalculationMode.Ticks, stopTicks, false);
            SetProfitTarget(signalName, CalculationMode.Ticks, targetTicks);

            if (direction > 0)
                EnterLong(Contracts, signalName);
            else
                EnterShort(Contracts, signalName);

            OnTradeEntered(signalName, direction, stopTicks, targetTicks);
        }

        /// <summary>
        /// Cockpit dashboard's BUY UP / SELL DOWN buttons -- a second manual entry path,
        /// distinct from BUY CLICK / SELL CLICK (TrySubmitManualEntry). Original design
        /// split for this rebuild: BUY CLICK/SELL CLICK use the panel's own SL/TP text
        /// boxes (fully custom risk per trade), while BUY UP/SELL DOWN use the active ATM
        /// slot's preset stop/target/trail (one-click entry at a known, pre-configured risk
        /// profile -- same sizing TryEnterOnSignal uses for auto-signal entries when Cockpit
        /// ATM is on). Both still read qty from the panel's Qty box. Ignores
        /// EnableAutoTrading and Volatility Shield's TradingAllowed for the same reason
        /// TrySubmitManualEntry does (a human click is an explicit override); still respects
        /// Trade Protector's block.
        /// </summary>
        private void TrySubmitAtmQuickEntry(int direction)
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return;
            if (tradeProtectorBlocked)
                return;

            int qty = Math.Max(1, ParseIntOr(qtyTextBox != null ? qtyTextBox.Text : "", ManualDefaultQty));

            int stopTicks, targetTicks; PraedoxTrailMethod trailMethodUnused; double trailParamUnused;
            GetAtmSlotConfig(activeAtmSlotRuntime, out stopTicks, out targetTicks, out trailMethodUnused, out trailParamUnused);
            stopTicks = Math.Max(1, stopTicks);
            targetTicks = Math.Max(1, targetTicks);

            string signalName = "PraedoxQuick";
            SetStopLoss(signalName, CalculationMode.Ticks, stopTicks, false);
            SetProfitTarget(signalName, CalculationMode.Ticks, targetTicks);

            if (direction > 0)
                EnterLong(qty, signalName);
            else
                EnterShort(qty, signalName);

            OnTradeEntered(signalName, direction, stopTicks, targetTicks);
        }

        /// <summary>
        /// Manual entry from the Cockpit panel's Buy/Sell button -- see SECTION 8 header
        /// comment ("MANUAL ENTRY"). Unlike TryEnterOnSignal, this ignores EnableAutoTrading
        /// and Volatility Shield's TradingAllowed (a human clicking Buy is an explicit
        /// override of the auto-signal gating) but still respects Trade Protector's daily
        /// block, since that is an account-risk circuit breaker meant to apply regardless
        /// of how the entry was requested.
        /// </summary>
        private void TrySubmitManualEntry(int direction, int qty, int stopTicks, int targetTicks)
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return;
            if (tradeProtectorBlocked)
                return;

            string signalName = "PraedoxManual";
            int safeQty = Math.Max(1, qty);
            int safeStopTicks = Math.Max(1, stopTicks);
            int safeTargetTicks = Math.Max(1, targetTicks);

            SetStopLoss(signalName, CalculationMode.Ticks, safeStopTicks, false);
            SetProfitTarget(signalName, CalculationMode.Ticks, safeTargetTicks);

            if (direction > 0)
                EnterLong(safeQty, signalName);
            else
                EnterShort(safeQty, signalName);

            OnTradeEntered(signalName, direction, safeStopTicks, safeTargetTicks);
        }

        /// <summary>
        /// Shared post-entry bookkeeping for BOTH entry paths (auto-signal and manual) --
        /// snapshots state needed by whichever trade-management system is active. FibGuard
        /// (Section 7) and Cockpit ATM (Section 8) are mutually exclusive -- see their
        /// respective header comments -- so only one of these blocks does anything on a
        /// given trade.
        /// </summary>
        private void OnTradeEntered(string signalName, int direction, int stopTicks, int targetTicks)
        {
            if (EnableFibGuard && !EnableCockpitAtm)
            {
                Array.Copy(fibLevelPrices, fgEntryLevels, fibLevelPrices.Length);
                fgEntryDirection = direction;
                fgSignalName = signalName;
                fgCurrentStopPrice = double.NaN;
                fgActive = true;
            }

            if (EnableCockpitAtm)
            {
                Array.Copy(fibLevelPrices, atmEntryFibLevels, fibLevelPrices.Length);
                atmEntryDirection = direction;
                atmSignalName = signalName;
                atmCurrentStopPrice = double.NaN;
                atmTradeActive = true;
            }

            // Dashboard Trade Management row (Move-To-BE, Stop/Tgt nudge, Stop/Tgt ATR) acts
            // on whichever signal most recently opened the position, independent of which of
            // the 3 entry paths (auto, manual, quick) opened it -- see the field comment.
            activeEntrySignalName = signalName;
            activeStopTicks = Math.Max(1, stopTicks);
            activeTargetTicks = Math.Max(1, targetTicks);

            FireAlert("PraedoxAlertOrderFilled", EnableAlertOrderFilled, AlertOrderFilledSound,
                "Praedox Sentinel: " + signalName + " entry submitted (" + (direction > 0 ? "long" : "short") + ")");
        }

        private void GetAtmSlotConfig(int slot, out int stopTicks, out int targetTicks, out PraedoxTrailMethod trailMethod, out double trailParam)
        {
            switch (slot)
            {
                case 2: stopTicks = Atm2StopTicks; targetTicks = Atm2TargetTicks; trailMethod = Atm2TrailMethod; trailParam = Atm2TrailParam; break;
                case 3: stopTicks = Atm3StopTicks; targetTicks = Atm3TargetTicks; trailMethod = Atm3TrailMethod; trailParam = Atm3TrailParam; break;
                case 4: stopTicks = Atm4StopTicks; targetTicks = Atm4TargetTicks; trailMethod = Atm4TrailMethod; trailParam = Atm4TrailParam; break;
                default: stopTicks = Atm1StopTicks; targetTicks = Atm1TargetTicks; trailMethod = Atm1TrailMethod; trailParam = Atm1TrailParam; break;
            }
        }

        /// <summary>
        /// Cockpit ATM trailing -- dispatches to whichever of the 4 trail methods the
        /// active slot specifies. Ratchets only (never loosens), same discipline as
        /// FibGuard's trail in Section 7. Runs every bar while a Cockpit-ATM-managed trade
        /// is open -- see SECTION 8 header comment for the per-method rationale.
        /// </summary>
        private void UpdateAtmTrail()
        {
            int stopTicksUnused, targetTicksUnused; PraedoxTrailMethod trailMethod; double trailParam;
            GetAtmSlotConfig(activeAtmSlotRuntime, out stopTicksUnused, out targetTicksUnused, out trailMethod, out trailParam);

            double candidate = double.NaN;

            switch (trailMethod)
            {
                case PraedoxTrailMethod.AtrTrail:
                    // Chandelier-style: stop trails trailParam*ATR behind price.
                    if (atmEntryDirection > 0)
                        candidate = Close[0] - atr[0] * trailParam;
                    else
                        candidate = Close[0] + atr[0] * trailParam;
                    break;

                case PraedoxTrailMethod.FixedTicksTrail:
                    // Constant tick distance behind price.
                    if (atmEntryDirection > 0)
                        candidate = Close[0] - TickSize * trailParam;
                    else
                        candidate = Close[0] + TickSize * trailParam;
                    break;

                case PraedoxTrailMethod.FibLevelTrail:
                    // Same mechanic as Section 7's FibGuard, using the entry-frozen
                    // atmEntryFibLevels snapshot instead of fgEntryLevels -- trailParam is
                    // the min-advance-ticks buffer (mirrors FibGuardMinAdvanceTicks).
                    for (int i = 0; i < atmEntryFibLevels.Length; i++)
                    {
                        double level = atmEntryFibLevels[i];
                        if (atmEntryDirection > 0)
                        {
                            if (level < Close[0] - TickSize * trailParam && (double.IsNaN(candidate) || level > candidate))
                                candidate = level;
                        }
                        else
                        {
                            if (level > Close[0] + TickSize * trailParam && (double.IsNaN(candidate) || level < candidate))
                                candidate = level;
                        }
                    }
                    break;

                case PraedoxTrailMethod.SwingPointTrail:
                    // Trails to the most recent trailParam-bar swing low (long) / high
                    // (short) -- trailParam is the lookback bar count.
                    int lookback = Math.Max(1, (int)trailParam);
                    if (atmEntryDirection > 0)
                        candidate = MIN(Low, lookback)[1];
                    else
                        candidate = MAX(High, lookback)[1];
                    break;
            }

            if (double.IsNaN(candidate))
                return;

            candidate = Instrument.MasterInstrument.RoundToTickSize(candidate);

            bool improves = double.IsNaN(atmCurrentStopPrice)
                || (atmEntryDirection > 0 && candidate > atmCurrentStopPrice)
                || (atmEntryDirection < 0 && candidate < atmCurrentStopPrice);

            if (!improves)
                return;

            atmCurrentStopPrice = candidate;
            SetStopLoss(atmSignalName, CalculationMode.Price, candidate, false);
        }

        /// <summary>
        /// Drains the manual Buy/Sell/Flatten button requests queued by the Cockpit
        /// panel's Click handlers -- see "MANUAL ENTRY THREADING" in the SECTION 8 header
        /// comment for why this hand-off exists instead of acting directly in the handler.
        /// Called at the top of every OnBarUpdate, so a click takes effect at the next bar
        /// close (this strategy runs Calculate.OnBarClose) -- not tick-instant. That is a
        /// deliberate, documented simplification, not an oversight.
        /// </summary>
        private void ProcessManualCockpitRequests()
        {
            if (manualFlattenPending)
            {
                manualFlattenPending = false;
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("PraedoxManualFlatten", "");
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("PraedoxManualFlatten", "");
            }

            if (manualBuyPending)
            {
                manualBuyPending = false;
                TrySubmitManualEntry(1, manualPendingQty, manualPendingStopTicks, manualPendingTargetTicks);
            }

            if (manualSellPending)
            {
                manualSellPending = false;
                TrySubmitManualEntry(-1, manualPendingQty, manualPendingStopTicks, manualPendingTargetTicks);
            }

            // -- Cockpit dashboard Trade Management row (see "COCKPIT DASHBOARD" in the
            // SECTION 8 header comment) -- same drain-on-next-bar-close pattern as the
            // original 3 flags above, for the same cross-thread-safety reason.
            if (quickAtmBuyPending) { quickAtmBuyPending = false; TrySubmitAtmQuickEntry(1); }
            if (quickAtmSellPending) { quickAtmSellPending = false; TrySubmitAtmQuickEntry(-1); }
            if (moveToBePending) { moveToBePending = false; ApplyMoveToBe(); }
            if (nudgeStopTightenPending) { nudgeStopTightenPending = false; NudgeStop(-QuickNudgeTicks); }
            if (nudgeStopWidenPending) { nudgeStopWidenPending = false; NudgeStop(QuickNudgeTicks); }
            if (nudgeTargetTightenPending) { nudgeTargetTightenPending = false; NudgeTarget(-QuickNudgeTicks); }
            if (nudgeTargetWidenPending) { nudgeTargetWidenPending = false; NudgeTarget(QuickNudgeTicks); }
            if (quickStopAtrPending) { quickStopAtrPending = false; ApplyQuickAtrStop(); }
            if (quickTargetAtrPending) { quickTargetAtrPending = false; ApplyQuickAtrTarget(); }
            if (flattenAllPending) { flattenAllPending = false; FlattenAccount(); }
            if (sessionReportManualPending) { sessionReportManualPending = false; GenerateSessionReport(sessionReportStartTradeIndex); }
        }

        /// <summary>
        /// Session Trading Summary boundary check -- see "SESSION REPORT" in the SECTION 8
        /// header comment. Bars.IsFirstBarOfSession is true on the first bar of every new
        /// trading session AND on the very first bar of the whole loaded dataset (CurrentBar
        /// 0) -- the sessionReportSeenFirstBar guard treats only the LATTER as "nothing to
        /// report yet" and generates the just-ended session's report on every occurrence
        /// after that. NinjaTrader's own docs note this property is always false on
        /// non-intraday chart periods (Day, Month, etc.) -- on those, only the manual REPORT
        /// button and the State.Terminated safety-net flush will ever produce a report.
        /// </summary>
        private void CheckSessionReportBoundary()
        {
            try
            {
                if (!Bars.IsFirstBarOfSession)
                    return;

                int currentCount = SystemPerformance.AllTrades != null ? SystemPerformance.AllTrades.Count : 0;

                if (!sessionReportSeenFirstBar)
                {
                    sessionReportSeenFirstBar = true;
                    sessionReportStartTradeIndex = currentCount;
                    return;
                }

                GenerateSessionReport(sessionReportStartTradeIndex);
                sessionReportStartTradeIndex = currentCount;
            }
            catch (Exception ex)
            {
                Print("PraedoxSentinel Session Report boundary check failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Writes the HTML Session Trading Summary -- every closed trade from startTradeIndex
        /// (inclusive) to the end of SystemPerformance.AllTrades, plus win rate/record/avg-
        /// win/avg-loss/max-win/max-loss. Same trade-stats source and the same unverified-
        /// API caveat as the Cockpit dashboard's own trade stats -- see "TRADE STATS SOURCE"
        /// in the SECTION 8 header comment; wrapped in its own try/catch for the same reason.
        /// Always overwrites SessionReportPath (this rebuild's [stated]-confirmed choice --
        /// simplest to find, only the latest report is kept, rather than one dated file per
        /// session).
        /// </summary>
        private void GenerateSessionReport(int startIndex)
        {
            try
            {
                var allTrades = SystemPerformance.AllTrades;
                int count = allTrades != null ? allTrades.Count : 0;
                int safeStart = Math.Max(0, Math.Min(startIndex, count));

                int wins = 0, losses = 0;
                double totalPnl = 0, maxWin = 0, maxLoss = 0, sumWin = 0, sumLoss = 0;
                var rows = new System.Text.StringBuilder();

                for (int i = safeStart; i < count; i++)
                {
                    var t = allTrades[i];
                    double pnl = t.ProfitCurrency;
                    totalPnl += pnl;
                    if (pnl > 0) { wins++; sumWin += pnl; if (pnl > maxWin) maxWin = pnl; }
                    else if (pnl < 0) { losses++; sumLoss += pnl; if (pnl < maxLoss) maxLoss = pnl; }

                    string name = t.Entry != null ? t.Entry.Name : "Trade";
                    string dir = (t.Entry != null && t.Entry.MarketPosition == MarketPosition.Long) ? "Long" : "Short";
                    double entryPx = t.Entry != null ? t.Entry.Price : 0;
                    double exitPx = t.Exit != null ? t.Exit.Price : 0;
                    DateTime entryTime = t.Entry != null ? t.Entry.Time : DateTime.MinValue;
                    DateTime exitTime = t.Exit != null ? t.Exit.Time : DateTime.MinValue;

                    rows.Append("<tr><td>").Append(System.Net.WebUtility.HtmlEncode(name)).Append("</td><td>")
                        .Append(dir).Append("</td><td>")
                        .Append(entryTime.ToString("HH:mm:ss")).Append("</td><td>")
                        .Append(entryPx.ToString("F2")).Append("</td><td>")
                        .Append(exitTime.ToString("HH:mm:ss")).Append("</td><td>")
                        .Append(exitPx.ToString("F2")).Append("</td><td class=\"")
                        .Append(pnl >= 0 ? "win" : "loss").Append("\">")
                        .Append(pnl >= 0 ? "+$" : "-$").Append(Math.Abs(pnl).ToString("F2"))
                        .Append("</td></tr>\n");
                }

                int totalTrades = wins + losses;
                int winPct = totalTrades > 0 ? (int)Math.Round(100.0 * wins / totalTrades) : 0;
                double avgWin = wins > 0 ? sumWin / wins : 0;
                double avgLoss = losses > 0 ? sumLoss / losses : 0;

                string html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Praedox Sentinel - Session Report</title><style>"
                    + "body{background:#121214;color:#e5e5e5;font-family:'Segoe UI',Arial,sans-serif;padding:24px;}"
                    + "h1{color:#fff;font-size:20px;margin:0 0 4px 0;}"
                    + ".sub{color:#888;margin-bottom:20px;font-size:12px;}"
                    + ".stats{display:flex;gap:16px;margin-bottom:24px;flex-wrap:wrap;}"
                    + ".stat{background:#1c1c20;border-radius:6px;padding:10px 16px;min-width:100px;}"
                    + ".stat .label{color:#888;font-size:10px;text-transform:uppercase;}"
                    + ".stat .value{font-size:18px;font-weight:bold;margin-top:4px;}"
                    + "table{border-collapse:collapse;width:100%;}"
                    + "th,td{padding:6px 10px;text-align:left;border-bottom:1px solid #2a2a2e;font-size:13px;}"
                    + "th{color:#888;font-weight:normal;text-transform:uppercase;font-size:11px;}"
                    + ".win{color:#22c55e;} .loss{color:#f97316;}"
                    + "</style></head><body>"
                    + "<h1>Praedox Sentinel &mdash; Session Trading Summary</h1>"
                    + "<div class=\"sub\">Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " &mdash; " + System.Net.WebUtility.HtmlEncode(Instrument != null ? Instrument.FullName : "") + "</div>"
                    + "<div class=\"stats\">"
                    + "<div class=\"stat\"><div class=\"label\">Total P&amp;L</div><div class=\"value\" style=\"color:" + (totalPnl >= 0 ? "#22c55e" : "#f97316") + "\">" + (totalPnl >= 0 ? "+$" : "-$") + Math.Abs(totalPnl).ToString("F2") + "</div></div>"
                    + "<div class=\"stat\"><div class=\"label\">Win Rate</div><div class=\"value\">" + winPct + "%</div></div>"
                    + "<div class=\"stat\"><div class=\"label\">Record</div><div class=\"value\">" + wins + "-" + losses + "</div></div>"
                    + "<div class=\"stat\"><div class=\"label\">Avg Win</div><div class=\"value\" style=\"color:#22c55e\">$" + avgWin.ToString("F2") + "</div></div>"
                    + "<div class=\"stat\"><div class=\"label\">Avg Loss</div><div class=\"value\" style=\"color:#f97316\">-$" + Math.Abs(avgLoss).ToString("F2") + "</div></div>"
                    + "<div class=\"stat\"><div class=\"label\">Max Win</div><div class=\"value\" style=\"color:#22c55e\">$" + maxWin.ToString("F2") + "</div></div>"
                    + "<div class=\"stat\"><div class=\"label\">Max Loss</div><div class=\"value\" style=\"color:#f97316\">-$" + Math.Abs(maxLoss).ToString("F2") + "</div></div>"
                    + "</div>"
                    + "<table><thead><tr><th>Signal</th><th>Dir</th><th>Entry Time</th><th>Entry</th><th>Exit Time</th><th>Exit</th><th>P&amp;L</th></tr></thead><tbody>"
                    + (totalTrades > 0 ? rows.ToString() : "<tr><td colspan=\"7\" style=\"color:#888;\">No closed trades this session</td></tr>")
                    + "</tbody></table>"
                    + "<div style=\"margin-top:24px;color:#555;font-size:11px;\">Praedox Sentinel v0.1.0 &mdash; original rebuild. Trading futures involves substantial risk of loss. Past performance is not indicative of future results.</div>"
                    + "</body></html>";

                if (!string.IsNullOrEmpty(SessionReportPath))
                {
                    string reportDir = Path.GetDirectoryName(SessionReportPath);
                    if (!string.IsNullOrEmpty(reportDir))
                        Directory.CreateDirectory(reportDir);
                    File.WriteAllText(SessionReportPath, html);
                }
            }
            catch (Exception ex)
            {
                Print("PraedoxSentinel Session Report generation failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Cockpit dashboard's MOVE TO BE button -- moves the live stop to entry price plus
        /// moveToBeOffsetTicks (signed by trade direction), adjustable live via the panel's
        /// -/+ stepper. A no-op with nothing open or if the entry's signal name was lost
        /// (shouldn't happen while a position is open -- defensive check only).
        /// </summary>
        private void ApplyMoveToBe()
        {
            if (Position.MarketPosition == MarketPosition.Flat || activeEntrySignalName == null)
                return;

            int dir = Position.MarketPosition == MarketPosition.Long ? 1 : -1;
            double bePrice = Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice + dir * moveToBeOffsetTicks * TickSize);
            SetStopLoss(activeEntrySignalName, CalculationMode.Price, bePrice, false);
            activeStopTicks = (int)Math.Round(Math.Abs(bePrice - Position.AveragePrice) / TickSize);
        }

        /// <summary>
        /// Cockpit dashboard's Stop -10/+10 buttons. NinjaScript's SetStopLoss(ticks) sets
        /// the stop N ticks FROM ENTRY, not from the current stop or current price -- so
        /// "nudge by 10 ticks" is implemented by tracking the live distance-from-entry
        /// (activeStopTicks, maintained by every entry/adjustment path) and re-submitting
        /// with the adjusted distance, floored at 1 tick so a run of "tighten" clicks can't
        /// submit a zero/negative stop distance.
        /// </summary>
        private void NudgeStop(int deltaTicks)
        {
            if (Position.MarketPosition == MarketPosition.Flat || activeEntrySignalName == null)
                return;

            activeStopTicks = Math.Max(1, activeStopTicks + deltaTicks);
            SetStopLoss(activeEntrySignalName, CalculationMode.Ticks, activeStopTicks, false);
        }

        /// <summary>Cockpit dashboard's Tgt -10/+10 buttons -- same mechanic as NudgeStop.</summary>
        private void NudgeTarget(int deltaTicks)
        {
            if (Position.MarketPosition == MarketPosition.Flat || activeEntrySignalName == null)
                return;

            activeTargetTicks = Math.Max(1, activeTargetTicks + deltaTicks);
            SetProfitTarget(activeEntrySignalName, CalculationMode.Ticks, activeTargetTicks);
        }

        /// <summary>
        /// Cockpit dashboard's STOP ATR button -- sets the live stop to QuickAtrMultiple*ATR
        /// from the current close (floored at QuickAtrMinTicks so a quiet-ATR moment can't
        /// place it unreasonably close), as an absolute price rather than a tick distance
        /// from entry, since the intent is "distance from price right now," not from entry.
        /// </summary>
        private void ApplyQuickAtrStop()
        {
            if (Position.MarketPosition == MarketPosition.Flat || activeEntrySignalName == null)
                return;

            int dir = Position.MarketPosition == MarketPosition.Long ? 1 : -1;
            double dist = Math.Max(atr[0] * QuickAtrMultiple, TickSize * QuickAtrMinTicks);
            double price = Instrument.MasterInstrument.RoundToTickSize(Close[0] - dir * dist);
            SetStopLoss(activeEntrySignalName, CalculationMode.Price, price, false);
            activeStopTicks = (int)Math.Round(dist / TickSize);
        }

        /// <summary>Cockpit dashboard's TGT ATR button -- same mechanic as ApplyQuickAtrStop, opposite side.</summary>
        private void ApplyQuickAtrTarget()
        {
            if (Position.MarketPosition == MarketPosition.Flat || activeEntrySignalName == null)
                return;

            int dir = Position.MarketPosition == MarketPosition.Long ? 1 : -1;
            double dist = Math.Max(atr[0] * QuickAtrMultiple, TickSize * QuickAtrMinTicks);
            double price = Instrument.MasterInstrument.RoundToTickSize(Close[0] + dir * dist);
            SetProfitTarget(activeEntrySignalName, CalculationMode.Price, price);
            activeTargetTicks = (int)Math.Round(dist / TickSize);
        }

        /// <summary>
        /// Central sound-alert dispatcher -- wraps NinjaScript's built-in Alert() so every
        /// one of the 18 slots is a single, consistent call. Tag includes CurrentBar so
        /// repeat calls on the same bar naturally dedupe without needing to track a
        /// separate "already fired" flag per event -- see SECTION 8 header comment.
        /// </summary>
        private void FireAlert(string id, bool enabled, string soundFile, string message)
        {
            if (!enabled)
                return;

            // Priority is deliberately unqualified -- NinjaScript's own documented Alert()
            // example (ninjatrader.com/support/helpguides/nt8/alert.htm) uses it bare, and
            // an explicit "NinjaTrader.Cbi.Priority" fully-qualified guess was WRONG (CS0234
            // -- Priority isn't in NinjaTrader.Cbi) and caused a compile error on the first
            // Cockpit UI push. Left unqualified so the compiler resolves it via whichever of
            // NinjaScript's own already-imported namespaces actually declares it.
            Alert(id + CurrentBar, Priority.Low, message, soundFile, 10, Brushes.Black, Brushes.White);
        }

        private void FireSignalAlert(int rank)
        {
            switch (rank)
            {
                case 1: FireAlert("PraedoxAlertSlingshot", EnableAlertSlingshot, AlertSlingshotSound, "Praedox Sentinel: Slingshot fired"); break;
                case 2: FireAlert("PraedoxAlertZoneBreak", EnableAlertZoneBreak, AlertZoneBreakSound, "Praedox Sentinel: Zone Break fired"); break;
                case 3: FireAlert("PraedoxAlertTrendCycleWave", EnableAlertTrendCycleWave, AlertTrendCycleWaveSound, "Praedox Sentinel: Trend Cycle Wave fired"); break;
                case 4: FireAlert("PraedoxAlertCrossFire", EnableAlertCrossFire, AlertCrossFireSound, "Praedox Sentinel: CrossFire fired"); break;
                case 5: FireAlert("PraedoxAlertReversal", EnableAlertReversal, AlertReversalSound, "Praedox Sentinel: Reversal fired"); break;
                case 6: FireAlert("PraedoxAlertContinuation", EnableAlertContinuation, AlertContinuationSound, "Praedox Sentinel: Continuation fired"); break;
                case 7: FireAlert("PraedoxAlertEmaBreakout", EnableAlertEmaBreakout, AlertEmaBreakoutSound, "Praedox Sentinel: EMA Breakout fired"); break;
                case 8: FireAlert("PraedoxAlertAggressiveEma", EnableAlertAggressiveEma, AlertAggressiveEmaSound, "Praedox Sentinel: Aggressive EMA fired"); break;
                case 9: FireAlert("PraedoxAlertVolumeAggressive", EnableAlertVolumeAggressive, AlertVolumeAggressiveSound, "Praedox Sentinel: Volume Aggressive fired"); break;
                case 10: FireAlert("PraedoxAlertBreakout", EnableAlertBreakout, AlertBreakoutSound, "Praedox Sentinel: Breakout fired"); break;
            }
        }

        /// <summary>
        /// Edge-triggered on entering (not merely being in) the Red/Elevated or Dark
        /// Red/Extreme volatility state -- fires once per transition, not once per bar.
        /// </summary>
        private void FireVolShieldTransitionAlert(int stateBefore, int stateAfter)
        {
            if (stateAfter == stateBefore)
                return;

            if (stateAfter == 2)
                FireAlert("PraedoxAlertVolShieldRed", EnableAlertVolShieldRed, AlertVolShieldRedSound, "Praedox Sentinel: Volatility Shield -> ELEVATED");
            else if (stateAfter == 3)
                FireAlert("PraedoxAlertVolShieldDarkRed", EnableAlertVolShieldDarkRed, AlertVolShieldDarkRedSound, "Praedox Sentinel: Volatility Shield -> EXTREME (trading blocked)");
        }

        /// <summary>
        /// FibGuard Trade Manager: ratchets the stop-loss forward to the nearest
        /// snapshotted fib level the trade has cleared, tightening only, never loosening.
        /// Layers on top of the ATR-based initial stop/target from TryEnterOnSignal --
        /// see "FIBGUARD" header comment for the full design rationale.
        /// </summary>
        private void UpdateFibGuardTrail()
        {
            double bestLevel = double.NaN;

            for (int i = 0; i < fgEntryLevels.Length; i++)
            {
                double level = fgEntryLevels[i];

                if (fgEntryDirection > 0)
                {
                    // Long: a level counts once price has cleared above it with at least
                    // FibGuardMinAdvanceTicks of room, so the stop never sits on top of
                    // (or ahead of) the current price.
                    if (level < Close[0] - TickSize * FibGuardMinAdvanceTicks && (double.IsNaN(bestLevel) || level > bestLevel))
                        bestLevel = level;
                }
                else
                {
                    if (level > Close[0] + TickSize * FibGuardMinAdvanceTicks && (double.IsNaN(bestLevel) || level < bestLevel))
                        bestLevel = level;
                }
            }

            if (double.IsNaN(bestLevel))
                return; // no snapshotted level has been cleared yet -- ATR stop from entry still stands

            bool improves = double.IsNaN(fgCurrentStopPrice)
                || (fgEntryDirection > 0 && bestLevel > fgCurrentStopPrice)
                || (fgEntryDirection < 0 && bestLevel < fgCurrentStopPrice);

            if (!improves)
                return; // ratchet only -- never move the stop backward

            fgCurrentStopPrice = bestLevel;
            SetStopLoss(fgSignalName, CalculationMode.Price, bestLevel, false);
        }

        // ============================================================================
        // SECTION 8 -- COCKPIT UI (WPF chart panel, rebuilt 2026-09-23 as a full docked
        // dashboard to match the surviving marketing screenshot -- see "COCKPIT DASHBOARD"
        // in the SECTION 8 header comment for the full design rationale)
        // ============================================================================

        /// <summary>
        /// Builds the docked-right Cockpit dashboard: title bar, PnL/win-rate/equity-curve
        /// stats, last-5-trades table, Trading-On/Raw-Signal-Mode/Chop-Filter status row, the
        /// 10-signal toggle grid, and the full Trade Management button block. Must run on
        /// the chart's UI dispatcher thread -- called via ChartControl.Dispatcher.InvokeAsync
        /// from OnStateChange(State.Historical), never directly. See "COCKPIT PANEL
        /// LIFECYCLE" in the SECTION 8 header comment.
        /// </summary>
        private void CreateCockpitPanel()
        {
            try
            {
                if (cockpitChartControl == null || cockpitOuterBorder != null)
                    return; // already built, or no chart to attach to

                var bg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 18, 20));
                var bannerBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235));
                var sectionLabelBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(140, 140, 150));
                var borderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 66));

                cockpitOuterBorder = new System.Windows.Controls.Border
                {
                    Name = "PraedoxCockpitBorder",
                    // Docked to the chart's right edge rather than floating over price --
                    // see "DOCKING VS. FLOATING OVERLAY" in the SECTION 8 header comment for
                    // why this is anchor-right + collapsible rather than a true grid-column
                    // dock that resizes NinjaTrader's own plot area.
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                    VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
                    Width = 250,
                    Background = bg,
                    BorderBrush = borderBrush,
                    BorderThickness = new System.Windows.Thickness(1, 0, 0, 0)
                };

                var outerStack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };

                // -- Title banner, with a collapse/expand toggle so the dashboard can be
                // shrunk to a thin strip on demand -- see "DOCKING VS. FLOATING OVERLAY". --
                var banner = new System.Windows.Controls.Grid { Background = bannerBrush, Height = 30 };
                banner.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "PRAEDOX SENTINEL",
                    Foreground = Brushes.White,
                    FontWeight = System.Windows.FontWeights.Bold,
                    FontSize = 13,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                });
                collapseToggleButton = new System.Windows.Controls.Button
                {
                    Content = "<",
                    Width = 22,
                    Height = 22,
                    Margin = new System.Windows.Thickness(0, 0, 4, 0),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    Background = Brushes.Transparent,
                    Foreground = Brushes.White,
                    BorderThickness = new System.Windows.Thickness(0)
                };
                collapseToggleButton.Click += OnCockpitCollapseToggleClick;
                banner.Children.Add(collapseToggleButton);
                outerStack.Children.Add(banner);

                // -- 3-way tab row (Snapshot / Control / Analyzer) -- see "THREE-PANEL
                // DASHBOARD" in the SECTION 8 header comment. --
                cockpitCollapsibleWrap = new System.Windows.Controls.StackPanel();
                var tabRow = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(4, 4, 4, 0) };
                tabRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
                tabRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
                tabRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
                snapshotTabButton = new System.Windows.Controls.Button { Content = "SNAPSHOT", Height = 22, FontSize = 9, Margin = new System.Windows.Thickness(1) };
                controlTabButton = new System.Windows.Controls.Button { Content = "CONTROL", Height = 22, FontSize = 9, Margin = new System.Windows.Thickness(1) };
                analyzerTabButton = new System.Windows.Controls.Button { Content = "ANALYZER", Height = 22, FontSize = 9, Margin = new System.Windows.Thickness(1) };
                snapshotTabButton.Click += (sender, args) => SetActiveCockpitTab(0);
                controlTabButton.Click += (sender, args) => SetActiveCockpitTab(1);
                analyzerTabButton.Click += (sender, args) => SetActiveCockpitTab(2);
                System.Windows.Controls.Grid.SetColumn(snapshotTabButton, 0);
                System.Windows.Controls.Grid.SetColumn(controlTabButton, 1);
                System.Windows.Controls.Grid.SetColumn(analyzerTabButton, 2);
                tabRow.Children.Add(snapshotTabButton);
                tabRow.Children.Add(controlTabButton);
                tabRow.Children.Add(analyzerTabButton);
                cockpitCollapsibleWrap.Children.Add(tabRow);

                var scrollHost = new System.Windows.Controls.ScrollViewer
                {
                    VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
                    MaxHeight = 900
                };
                cockpitBodyStack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical, Margin = new System.Windows.Thickness(8) };

                // -- PnL summary --
                pnlValueText = new System.Windows.Controls.TextBlock { Foreground = Brushes.LimeGreen, FontWeight = System.Windows.FontWeights.Bold, FontSize = 22, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Text = "+$0" };
                cockpitBodyStack.Children.Add(pnlValueText);
                realUnrlText = new System.Windows.Controls.TextBlock { Foreground = Brushes.Gray, FontSize = 11, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new System.Windows.Thickness(0, 0, 0, 6), Text = "Real: $0  Unrl: $0" };
                cockpitBodyStack.Children.Add(realUnrlText);

                // -- Win-rate / W-L / Max+ / Max- row --
                var statsGrid = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(0, 0, 0, 6) };
                for (int c = 0; c < 3; c++) statsGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
                winRateText = new System.Windows.Controls.TextBlock { Foreground = Brushes.White, FontWeight = System.Windows.FontWeights.Bold, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Text = "0%\nWIN" };
                wlRecordText = new System.Windows.Controls.TextBlock { Foreground = Brushes.White, FontWeight = System.Windows.FontWeights.Bold, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Text = "0-0\nW/L" };
                var maxStack = new System.Windows.Controls.StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
                maxPlusText = new System.Windows.Controls.TextBlock { Foreground = Brushes.LimeGreen, FontWeight = System.Windows.FontWeights.Bold, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Text = "$0" };
                maxMinusText = new System.Windows.Controls.TextBlock { Foreground = Brushes.OrangeRed, FontWeight = System.Windows.FontWeights.Bold, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Text = "$0" };
                maxStack.Children.Add(maxPlusText);
                maxStack.Children.Add(maxMinusText);
                System.Windows.Controls.Grid.SetColumn(winRateText, 0);
                System.Windows.Controls.Grid.SetColumn(wlRecordText, 1);
                System.Windows.Controls.Grid.SetColumn(maxStack, 2);
                statsGrid.Children.Add(winRateText);
                statsGrid.Children.Add(wlRecordText);
                statsGrid.Children.Add(maxStack);
                cockpitBodyStack.Children.Add(statsGrid);

                // -- Equity curve (last N closed trades, cumulative $) --
                cockpitBodyStack.Children.Add(BuildSectionLabel("PNL CURVE", sectionLabelBrush));
                equityCurveCanvas = new System.Windows.Controls.Canvas { Height = 60, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 28, 32)), Margin = new System.Windows.Thickness(0, 2, 0, 6) };
                cockpitBodyStack.Children.Add(equityCurveCanvas);

                // -- Last 5 trades --
                cockpitBodyStack.Children.Add(BuildSectionLabel("LAST 5 TRADES", sectionLabelBrush));
                lastTradesStack = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 2, 0, 6) };
                cockpitBodyStack.Children.Add(lastTradesStack);

                // -- Trading On / Raw Signal Mode / Chop Filter --
                tradingOnButton = new System.Windows.Controls.Button { Height = 26, Margin = new System.Windows.Thickness(0, 2, 0, 2), FontWeight = System.Windows.FontWeights.Bold };
                tradingOnButton.Click += OnCockpitTradingOnClick;
                cockpitBodyStack.Children.Add(tradingOnButton);
                rawSignalButton = new System.Windows.Controls.Button { Height = 24, Margin = new System.Windows.Thickness(0, 2, 0, 2) };
                rawSignalButton.Click += OnCockpitRawSignalClick;
                cockpitBodyStack.Children.Add(rawSignalButton);
                chopFilterText = new System.Windows.Controls.TextBlock { Foreground = Brushes.DeepSkyBlue, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new System.Windows.Thickness(0, 2, 0, 6), Text = "CHOP FILTER: -- / --" };
                cockpitBodyStack.Children.Add(chopFilterText);

                // -- Signal toggle grid (10 signals, 2 per row -- 11th "Volume Climax" label
                // from the reference screenshot has no equivalent signal in this rebuild's
                // 10-signal spec and is intentionally not recreated -- see "SIGNAL GRID" in
                // the SECTION 8 header comment). --
                var signalGrid = new System.Windows.Controls.Grid();
                signalGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
                signalGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
                for (int rank = 1; rank <= 10; rank++)
                {
                    int rowIndex = (rank - 1) / 2;
                    int colIndex = (rank - 1) % 2;
                    while (signalGrid.RowDefinitions.Count <= rowIndex)
                        signalGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());

                    int capturedRank = rank;
                    var sigButton = new System.Windows.Controls.Button
                    {
                        Content = SignalNames[rank],
                        Height = 24,
                        Margin = new System.Windows.Thickness(1),
                        FontSize = 10
                    };
                    sigButton.Click += (sender, args) => { SetSignalEnabled(capturedRank, !GetSignalEnabled(capturedRank)); UpdateSignalToggleButtonVisual(capturedRank); };
                    System.Windows.Controls.Grid.SetRow(sigButton, rowIndex);
                    System.Windows.Controls.Grid.SetColumn(sigButton, colIndex);
                    signalGrid.Children.Add(sigButton);
                    signalToggleButtons[rank] = sigButton;
                }
                cockpitBodyStack.Children.Add(signalGrid);
                for (int rank = 1; rank <= 10; rank++)
                    UpdateSignalToggleButtonVisual(rank);

                // -- Trade Management --
                cockpitBodyStack.Children.Add(BuildSectionLabel("-- TRADE MANAGEMENT --", sectionLabelBrush));

                var buyUpSellDownRow = BuildTwoButtonRow(
                    "BUY UP", Brushes.DarkGreen, (s, e) => { quickAtmBuyPending = true; },
                    "SELL DOWN", Brushes.DarkRed, (s, e) => { quickAtmSellPending = true; });
                cockpitBodyStack.Children.Add(buyUpSellDownRow);

                var buyClickSellClickRow = BuildTwoButtonRow(
                    "BUY CLICK", Brushes.SeaGreen, (s, e) => { QueueManualEntry(1); },
                    "SELL CLICK", Brushes.IndianRed, (s, e) => { QueueManualEntry(-1); });
                cockpitBodyStack.Children.Add(buyClickSellClickRow);

                // -- Qty / SL / TP row (feeds BUY CLICK / SELL CLICK only -- BUY UP / SELL
                // DOWN use the active ATM slot's own stop/target instead) --
                var inputRow = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new System.Windows.Thickness(0, 2, 0, 4) };
                qtyTextBox = new System.Windows.Controls.TextBox { Width = 38, Margin = new System.Windows.Thickness(2), Text = ManualDefaultQty.ToString() };
                stopTextBox = new System.Windows.Controls.TextBox { Width = 38, Margin = new System.Windows.Thickness(2), Text = ManualDefaultStopTicks.ToString() };
                targetTextBox = new System.Windows.Controls.TextBox { Width = 38, Margin = new System.Windows.Thickness(2), Text = ManualDefaultTargetTicks.ToString() };
                inputRow.Children.Add(new System.Windows.Controls.TextBlock { Text = "Qty", Foreground = Brushes.White, Margin = new System.Windows.Thickness(2), VerticalAlignment = System.Windows.VerticalAlignment.Center });
                inputRow.Children.Add(qtyTextBox);
                inputRow.Children.Add(new System.Windows.Controls.TextBlock { Text = "SL", Foreground = Brushes.White, Margin = new System.Windows.Thickness(2), VerticalAlignment = System.Windows.VerticalAlignment.Center });
                inputRow.Children.Add(stopTextBox);
                inputRow.Children.Add(new System.Windows.Controls.TextBlock { Text = "TP", Foreground = Brushes.White, Margin = new System.Windows.Thickness(2), VerticalAlignment = System.Windows.VerticalAlignment.Center });
                inputRow.Children.Add(targetTextBox);
                cockpitBodyStack.Children.Add(inputRow);

                // -- ATM slot row (which preset BUY UP/SELL DOWN and the trail use) --
                var slotRow = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new System.Windows.Thickness(0, 0, 0, 4) };
                for (int i = 0; i < 4; i++)
                {
                    int slotNumber = i + 1;
                    var slotButton = new System.Windows.Controls.Button { Content = "ATM " + slotNumber, Width = 55, Height = 22, Margin = new System.Windows.Thickness(1), FontSize = 10 };
                    slotButton.Click += (sender, args) => { activeAtmSlotRuntime = slotNumber; };
                    atmSlotButtons[i] = slotButton;
                    slotRow.Children.Add(slotButton);
                }
                cockpitBodyStack.Children.Add(slotRow);
                activeSlotLabel = new System.Windows.Controls.TextBlock { Foreground = Brushes.White, FontSize = 10, Margin = new System.Windows.Thickness(2, 0, 2, 6), TextWrapping = System.Windows.TextWrapping.Wrap, Text = "Active: ATM 1 | Auto-Trading: OFF" };
                cockpitBodyStack.Children.Add(activeSlotLabel);

                // -- Move To BE, with a -/+ stepper for the locked-in-profit offset --
                var beRow = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(0, 2, 0, 2) };
                beRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                beRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
                beRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
                beRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
                var moveToBeButton = new System.Windows.Controls.Button { Content = "MOVE TO BE", Height = 26, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)), Foreground = Brushes.White, FontWeight = System.Windows.FontWeights.Bold };
                moveToBeButton.Click += (sender, args) => { moveToBePending = true; };
                var beMinusButton = new System.Windows.Controls.Button { Content = "-", Width = 22, Height = 26, Margin = new System.Windows.Thickness(2, 0, 0, 0) };
                beMinusButton.Click += OnCockpitBeStepDownClick;
                moveToBeValueText = new System.Windows.Controls.TextBlock { Foreground = Brushes.White, VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new System.Windows.Thickness(4, 0, 4, 0), Text = "+0" };
                var bePlusButton = new System.Windows.Controls.Button { Content = "+", Width = 22, Height = 26 };
                bePlusButton.Click += OnCockpitBeStepUpClick;
                System.Windows.Controls.Grid.SetColumn(moveToBeButton, 0);
                System.Windows.Controls.Grid.SetColumn(beMinusButton, 1);
                System.Windows.Controls.Grid.SetColumn(moveToBeValueText, 2);
                System.Windows.Controls.Grid.SetColumn(bePlusButton, 3);
                beRow.Children.Add(moveToBeButton);
                beRow.Children.Add(beMinusButton);
                beRow.Children.Add(moveToBeValueText);
                beRow.Children.Add(bePlusButton);
                cockpitBodyStack.Children.Add(beRow);

                var stopNudgeRow = BuildTwoButtonRow(
                    "STOP -" + QuickNudgeTicks, Brushes.DimGray, (s, e) => { nudgeStopTightenPending = true; },
                    "TGT -" + QuickNudgeTicks, Brushes.DimGray, (s, e) => { nudgeTargetTightenPending = true; });
                cockpitBodyStack.Children.Add(stopNudgeRow);
                var stopNudgeRow2 = BuildTwoButtonRow(
                    "STOP +" + QuickNudgeTicks, Brushes.DimGray, (s, e) => { nudgeStopWidenPending = true; },
                    "TGT +" + QuickNudgeTicks, Brushes.DimGray, (s, e) => { nudgeTargetWidenPending = true; });
                cockpitBodyStack.Children.Add(stopNudgeRow2);
                var atrRow = BuildTwoButtonRow(
                    "STOP ATR\n" + QuickAtrMultiple + "x (" + QuickAtrMinTicks + "t)", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(55, 65, 81)),
                    (s, e) => { quickStopAtrPending = true; },
                    "TGT ATR\n" + QuickAtrMultiple + "x (" + QuickAtrMinTicks + "t)", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(55, 65, 81)),
                    (s, e) => { quickTargetAtrPending = true; });
                cockpitBodyStack.Children.Add(atrRow);

                flattenButton = new System.Windows.Controls.Button { Content = "FLATTEN ALL", Height = 28, Margin = new System.Windows.Thickness(0, 6, 0, 4), Background = Brushes.Crimson, Foreground = Brushes.White, FontWeight = System.Windows.FontWeights.Bold };
                flattenButton.Click += (sender, args) => { flattenAllPending = true; };
                cockpitBodyStack.Children.Add(flattenButton);

                // -- Session Trading Summary report, on demand -- see "SESSION REPORT" in
                // the SECTION 8 header comment. Auto-generates too (session-boundary and
                // strategy-stop hooks in OnBarUpdate/OnStateChange); this button is for an
                // on-the-spot snapshot without waiting for either of those.
                var reportButton = new System.Windows.Controls.Button { Content = "REPORT", Height = 24, Margin = new System.Windows.Thickness(0, 0, 0, 4), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(55, 65, 81)), Foreground = Brushes.White, FontSize = 10 };
                reportButton.Click += (sender, args) => { sessionReportManualPending = true; };
                cockpitBodyStack.Children.Add(reportButton);

                // -- Footer: time + Auto Fibs status, branding/disclaimer --
                timeStatusText = new System.Windows.Controls.TextBlock { Foreground = Brushes.White, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, FontWeight = System.Windows.FontWeights.Bold, Margin = new System.Windows.Thickness(0, 6, 0, 0), Text = "--:--:-- --" };
                cockpitBodyStack.Children.Add(timeStatusText);
                var footerText = new System.Windows.Controls.TextBlock
                {
                    Foreground = Brushes.Gray,
                    FontSize = 9,
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    TextAlignment = System.Windows.TextAlignment.Center,
                    Margin = new System.Windows.Thickness(0, 6, 0, 0),
                    Text = "(c) 2026 Praedox Sentinel - original rebuild v0.1.0\nTrading futures involves substantial risk of loss. Past performance is not indicative of future results."
                };
                cockpitBodyStack.Children.Add(footerText);

                // -- Market Snapshot tab: read-only current market/indicator conditions --
                // see "MARKET SNAPSHOT TAB" in the SECTION 8 header comment.
                cockpitSnapshotStack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical, Margin = new System.Windows.Thickness(8), Visibility = System.Windows.Visibility.Collapsed };
                cockpitSnapshotStack.Children.Add(BuildSectionLabel("MARKET SNAPSHOT", sectionLabelBrush));
                snapshotPriceText = BuildSnapshotRow();
                snapshotVrText = BuildSnapshotRow();
                snapshotAtrText = BuildSnapshotRow();
                snapshotChopText = BuildSnapshotRow();
                snapshotFibText = BuildSnapshotRow();
                snapshotTrendText = BuildSnapshotRow();
                snapshotSessionText = BuildSnapshotRow();
                cockpitSnapshotStack.Children.Add(snapshotPriceText);
                cockpitSnapshotStack.Children.Add(snapshotVrText);
                cockpitSnapshotStack.Children.Add(snapshotAtrText);
                cockpitSnapshotStack.Children.Add(snapshotChopText);
                cockpitSnapshotStack.Children.Add(snapshotFibText);
                cockpitSnapshotStack.Children.Add(snapshotTrendText);
                cockpitSnapshotStack.Children.Add(snapshotSessionText);
                var snapshotFooter = new System.Windows.Controls.TextBlock
                {
                    Foreground = Brushes.Gray,
                    FontSize = 9,
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                    Margin = new System.Windows.Thickness(0, 10, 0, 0),
                    Text = "Read-only -- reflects the last processed bar's indicator state. No trading actions here; use the Control tab for that."
                };
                cockpitSnapshotStack.Children.Add(snapshotFooter);

                // -- Trade Analyzer tab: all-time, per-signal trade analytics -- see "TRADE
                // ANALYZER TAB" in the SECTION 8 header comment.
                cockpitAnalyzerStack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical, Margin = new System.Windows.Thickness(8), Visibility = System.Windows.Visibility.Collapsed };
                cockpitAnalyzerStack.Children.Add(BuildSectionLabel("TRADE ANALYZER (ALL-TIME)", sectionLabelBrush));
                analyzerSummaryText = new System.Windows.Controls.TextBlock { Foreground = Brushes.White, FontSize = 11, TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new System.Windows.Thickness(0, 2, 0, 6), Text = "No closed trades yet" };
                cockpitAnalyzerStack.Children.Add(analyzerSummaryText);
                cockpitAnalyzerStack.Children.Add(BuildSectionLabel("EQUITY CURVE (ALL TRADES)", sectionLabelBrush));
                analyzerCurveCanvas = new System.Windows.Controls.Canvas { Height = 70, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 28, 32)), Margin = new System.Windows.Thickness(0, 2, 0, 6) };
                cockpitAnalyzerStack.Children.Add(analyzerCurveCanvas);
                cockpitAnalyzerStack.Children.Add(BuildSectionLabel("PER-SIGNAL PERFORMANCE", sectionLabelBrush));
                analyzerSignalBreakdownStack = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 2, 0, 6) };
                cockpitAnalyzerStack.Children.Add(analyzerSignalBreakdownStack);
                var analyzerFooter = new System.Windows.Controls.TextBlock
                {
                    Foreground = Brushes.Gray,
                    FontSize = 9,
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                    Margin = new System.Windows.Thickness(0, 4, 0, 0),
                    Text = "Read-only -- covers every trade this strategy instance has taken, not just today's session (see the Control tab's own stats for session-only numbers)."
                };
                cockpitAnalyzerStack.Children.Add(analyzerFooter);

                var tabContentHost = new System.Windows.Controls.StackPanel();
                tabContentHost.Children.Add(cockpitSnapshotStack);
                tabContentHost.Children.Add(cockpitBodyStack);
                tabContentHost.Children.Add(cockpitAnalyzerStack);

                scrollHost.Content = tabContentHost;
                cockpitCollapsibleWrap.Children.Add(scrollHost);
                outerStack.Children.Add(cockpitCollapsibleWrap);
                cockpitOuterBorder.Child = outerStack;
                UpdateCockpitTabButtonVisuals();

                // ChartControl.Children.Add() + Panel.SetZIndex() is the confirmed pattern
                // from NinjaTrader's own forum for a WPF element that renders ON TOP of the
                // price panel -- the earlier MainTabControl/hostGrid approach compiled fine
                // but the panel was invisible, because that grid sits behind the chart's own
                // render surface, not above it. See "COCKPIT PANEL LIFECYCLE" in the SECTION
                // 8 header comment. Known caveat from that same thread: a control added this
                // way can disappear if another indicator/strategy is added/removed on the
                // chart afterward -- removing and re-adding PraedoxSentinel recovers it.
                cockpitChartControl.Children.Add(cockpitOuterBorder);
                System.Windows.Controls.Panel.SetZIndex(cockpitOuterBorder, 100);

                // Wall-clock self-heal timer -- see the cockpitHealTimer field comment for
                // why the per-bar check inside UpdateCockpitPanelState isn't sufficient by
                // itself. Runs entirely on the UI thread (DispatcherTimer), so it's safe to
                // touch ChartControl.Children directly here without another InvokeAsync hop.
                if (cockpitHealTimer == null)
                {
                    cockpitHealTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(2)
                    };
                    cockpitHealTimer.Tick += OnCockpitHealTimerTick;
                }
                cockpitHealTimer.Start();
            }
            catch (Exception ex)
            {
                Print("PraedoxSentinel Cockpit panel failed to build: " + ex.Message);
            }
        }

        /// <summary>Small gray section-divider label, matching the reference screenshot's "-- TRADE MANAGEMENT --" style headings.</summary>
        private System.Windows.Controls.TextBlock BuildSectionLabel(string text, Brush foreground)
        {
            return new System.Windows.Controls.TextBlock
            {
                Text = text,
                Foreground = foreground,
                FontSize = 10,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new System.Windows.Thickness(0, 4, 0, 2)
            };
        }

        /// <summary>Single-line white TextBlock used for each labeled row on the Market Snapshot tab -- built empty, filled in per-bar by UpdateCockpitPanelState.</summary>
        private System.Windows.Controls.TextBlock BuildSnapshotRow()
        {
            return new System.Windows.Controls.TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 2, 0, 2)
            };
        }

        /// <summary>Switches which of the 3 tabs' content is visible and restyles the 3 tab buttons to show which is active. See the activeCockpitTab field comment.</summary>
        private void SetActiveCockpitTab(int tab)
        {
            activeCockpitTab = tab;
            if (cockpitSnapshotStack != null) cockpitSnapshotStack.Visibility = tab == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (cockpitBodyStack != null) cockpitBodyStack.Visibility = tab == 1 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (cockpitAnalyzerStack != null) cockpitAnalyzerStack.Visibility = tab == 2 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            UpdateCockpitTabButtonVisuals();
        }

        private void UpdateCockpitTabButtonVisuals()
        {
            var activeBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235));
            var inactiveBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 50));
            if (snapshotTabButton != null) { snapshotTabButton.Background = activeCockpitTab == 0 ? activeBrush : inactiveBrush; snapshotTabButton.Foreground = Brushes.White; }
            if (controlTabButton != null) { controlTabButton.Background = activeCockpitTab == 1 ? activeBrush : inactiveBrush; controlTabButton.Foreground = Brushes.White; }
            if (analyzerTabButton != null) { analyzerTabButton.Background = activeCockpitTab == 2 ? activeBrush : inactiveBrush; analyzerTabButton.Foreground = Brushes.White; }
        }

        /// <summary>
        /// Maps a Trade's Entry signal name ("Praedox1".."Praedox10", "PraedoxManual",
        /// "PraedoxQuick") to the human-readable label used throughout the dashboard (the
        /// same names in SignalNames, or "Manual Entry"/"Quick Entry (ATM)") -- used by the
        /// Trade Analyzer tab's per-signal breakdown so its rows read the same as the
        /// Control tab's signal grid rather than showing raw internal tag strings. Falls
        /// through to the raw name unchanged for anything unrecognized (defensive -- e.g. if
        /// a future entry path adds a new tag before this map is updated to match).
        /// </summary>
        private string MapSignalDisplayName(string entryName)
        {
            if (string.IsNullOrEmpty(entryName))
                return "Trade";
            if (entryName == "PraedoxManual")
                return "Manual Entry";
            if (entryName == "PraedoxQuick")
                return "Quick Entry (ATM)";
            if (entryName.StartsWith("Praedox"))
            {
                string rankPart = entryName.Substring("Praedox".Length);
                int rank;
                if (int.TryParse(rankPart, out rank) && rank >= 1 && rank <= 10)
                    return SignalNames[rank];
            }
            return entryName;
        }

        /// <summary>Shared builder for the many side-by-side two-button rows in the Trade Management section (Buy Up/Sell Down, Stop-10/Tgt-10, etc).</summary>
        private System.Windows.Controls.Grid BuildTwoButtonRow(string leftText, Brush leftBg, System.Windows.RoutedEventHandler leftClick, string rightText, Brush rightBg, System.Windows.RoutedEventHandler rightClick)
        {
            var row = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
            var left = new System.Windows.Controls.Button { Content = leftText, Height = 30, Margin = new System.Windows.Thickness(1), Background = leftBg, Foreground = Brushes.White, FontWeight = System.Windows.FontWeights.Bold, FontSize = 10 };
            var right = new System.Windows.Controls.Button { Content = rightText, Height = 30, Margin = new System.Windows.Thickness(1), Background = rightBg, Foreground = Brushes.White, FontWeight = System.Windows.FontWeights.Bold, FontSize = 10 };
            left.Click += leftClick;
            right.Click += rightClick;
            System.Windows.Controls.Grid.SetColumn(left, 0);
            System.Windows.Controls.Grid.SetColumn(right, 1);
            row.Children.Add(left);
            row.Children.Add(right);
            return row;
        }

        /// <summary>
        /// Ticks every 2 seconds on the UI thread, independent of bar closes -- catches a
        /// dropped panel far sooner than the per-bar check alone could on anything slower
        /// than a 1-2 second chart. See the cockpitHealTimer field comment.
        /// </summary>
        private void OnCockpitHealTimerTick(object sender, EventArgs e)
        {
            try
            {
                if (cockpitOuterBorder != null && cockpitChartControl != null && !cockpitChartControl.Children.Contains(cockpitOuterBorder))
                {
                    cockpitChartControl.Children.Add(cockpitOuterBorder);
                    System.Windows.Controls.Panel.SetZIndex(cockpitOuterBorder, 100);
                }
            }
            catch (Exception ex)
            {
                Print("PraedoxSentinel Cockpit heal-timer tick failed: " + ex.Message);
            }
        }

        private void RemoveCockpitPanel()
        {
            try
            {
                if (cockpitHealTimer != null)
                {
                    cockpitHealTimer.Stop();
                    cockpitHealTimer.Tick -= OnCockpitHealTimerTick;
                    cockpitHealTimer = null;
                }

                if (cockpitChartControl == null || cockpitOuterBorder == null)
                    return;

                cockpitChartControl.Children.Remove(cockpitOuterBorder);
                cockpitOuterBorder = null;
                cockpitBodyStack = null;
                cockpitSnapshotStack = null;
                cockpitAnalyzerStack = null;
                cockpitCollapsibleWrap = null;
            }
            catch (Exception ex)
            {
                Print("PraedoxSentinel Cockpit panel failed to remove cleanly: " + ex.Message);
            }
        }

        private void OnCockpitCollapseToggleClick(object sender, System.Windows.RoutedEventArgs e)
        {
            cockpitCollapsed = !cockpitCollapsed;
            if (cockpitCollapsibleWrap != null)
                cockpitCollapsibleWrap.Visibility = cockpitCollapsed ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            if (cockpitOuterBorder != null)
                cockpitOuterBorder.Width = cockpitCollapsed ? 26 : 250;
            if (collapseToggleButton != null)
                collapseToggleButton.Content = cockpitCollapsed ? ">" : "<";
        }

        private void OnCockpitTradingOnClick(object sender, System.Windows.RoutedEventArgs e)
        {
            // Direct property toggle from the UI thread, not queued -- same non-volatile
            // convention NinjaScript's own property grid already uses for a bool
            // [NinjaScriptProperty] changed live on a running strategy (EnableCockpitAtm,
            // EnableFibGuard, etc. all work this way already). A torn read isn't possible
            // for a CLR bool, so the only race is "takes effect within about a bar," which
            // is acceptable for a manual toggle -- see "COCKPIT DASHBOARD" header comment.
            EnableAutoTrading = !EnableAutoTrading;
            UpdateTradingOnButtonVisual();
        }

        private void OnCockpitRawSignalClick(object sender, System.Windows.RoutedEventArgs e)
        {
            RawSignalMode = !RawSignalMode;
            UpdateRawSignalButtonVisual();
        }

        private void OnCockpitBeStepDownClick(object sender, System.Windows.RoutedEventArgs e)
        {
            moveToBeOffsetTicks -= MoveToBeStepTicks;
            if (moveToBeValueText != null)
                moveToBeValueText.Text = (moveToBeOffsetTicks >= 0 ? "+" : "") + moveToBeOffsetTicks;
        }

        private void OnCockpitBeStepUpClick(object sender, System.Windows.RoutedEventArgs e)
        {
            moveToBeOffsetTicks += MoveToBeStepTicks;
            if (moveToBeValueText != null)
                moveToBeValueText.Text = (moveToBeOffsetTicks >= 0 ? "+" : "") + moveToBeOffsetTicks;
        }

        private void UpdateTradingOnButtonVisual()
        {
            if (tradingOnButton == null) return;
            tradingOnButton.Content = "TRADING " + (EnableAutoTrading ? "ON" : "OFF");
            tradingOnButton.Background = EnableAutoTrading
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 163, 74))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(75, 75, 82));
            tradingOnButton.Foreground = Brushes.White;
        }

        private void UpdateRawSignalButtonVisual()
        {
            if (rawSignalButton == null) return;
            rawSignalButton.Content = "RAW SIGNAL MODE: " + (RawSignalMode ? "ON" : "OFF");
            rawSignalButton.Background = RawSignalMode
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(202, 138, 4))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 50));
            rawSignalButton.Foreground = Brushes.White;
        }

        /// <summary>Maps a signal rank (1-10) to its Enable* property -- switch instead of reflection, consistent with this file's avoidance of anything not confirmed to compile cleanly against NinjaScript's build.</summary>
        private bool GetSignalEnabled(int rank)
        {
            switch (rank)
            {
                case 1: return EnableSlingshot;
                case 2: return EnableZoneBreak;
                case 3: return EnableTrendCycleWave;
                case 4: return EnableCrossFire;
                case 5: return EnableReversal;
                case 6: return EnableContinuation;
                case 7: return EnableEmaBreakout;
                case 8: return EnableAggressiveEma;
                case 9: return EnableVolumeAggressive;
                case 10: return EnableBreakout;
                default: return false;
            }
        }

        private void SetSignalEnabled(int rank, bool value)
        {
            switch (rank)
            {
                case 1: EnableSlingshot = value; break;
                case 2: EnableZoneBreak = value; break;
                case 3: EnableTrendCycleWave = value; break;
                case 4: EnableCrossFire = value; break;
                case 5: EnableReversal = value; break;
                case 6: EnableContinuation = value; break;
                case 7: EnableEmaBreakout = value; break;
                case 8: EnableAggressiveEma = value; break;
                case 9: EnableVolumeAggressive = value; break;
                case 10: EnableBreakout = value; break;
            }
        }

        private void UpdateSignalToggleButtonVisual(int rank)
        {
            if (rank < 1 || rank > 10 || signalToggleButtons[rank] == null)
                return;

            bool on = GetSignalEnabled(rank);
            signalToggleButtons[rank].Background = on
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 163, 74))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(55, 55, 60));
            signalToggleButtons[rank].Foreground = Brushes.White;
        }

        /// <summary>
        /// Called once per bar from OnBarUpdate (the strategy thread) -- reads only, so it
        /// still needs to marshal onto the UI thread to touch the WPF controls. Refreshes
        /// every stat on the dashboard: PnL/win-rate/equity-curve/last-5-trades (from
        /// SystemPerformance.AllTrades -- see "TRADE STATS SOURCE" in the SECTION 8 header
        /// comment), the ATM/Auto-Trading/Raw-Signal-Mode/Chop-Filter readouts, and the
        /// clock/Auto-Fibs footer.
        /// </summary>
        private void UpdateCockpitPanelState()
        {
            if (cockpitChartControl == null || cockpitOuterBorder == null)
                return;

            int slotSnapshot = activeAtmSlotRuntime;
            bool autoOn = EnableAutoTrading;
            bool atmOn = EnableCockpitAtm;
            bool rawOn = RawSignalMode;
            double chopSnapshot = currentChop;
            double chopCeilingSnapshot = ChopCeiling;
            int fibDirSnapshot = fibDirection;
            bool fibDynamicSnapshot = fibAnchorBarsAgo < StaleBarsThreshold;
            DateTime timeSnapshot = Time[0];
            double unrealizedSnapshot = Position.MarketPosition != MarketPosition.Flat ? Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]) : 0.0;
            double maxProfitLimit = MaxDailyProfitDollars;
            double maxLossLimit = MaxDailyLossDollars;

            // Market Snapshot tab -- see "MARKET SNAPSHOT TAB" in the SECTION 8 header
            // comment. Cheap reads, snapshotted here (strategy thread) same as everything
            // else this method hands across to the UI thread below.
            string instrumentNameSnapshot = Instrument != null ? Instrument.FullName : "";
            double priceSnapshot = Close[0];
            double vrSnapshot = currentVr;
            int volStateSnapshot = currentVolState;
            double atrSnapshot = atr[0];
            int atrTicksSnapshot = TickSize > 0 ? (int)Math.Round(atrSnapshot / TickSize) : 0;
            double trendEmaSnapshot = trendEma[0];
            double fastEmaSnapshot = fastEma[0];
            bool fibKillZoneSnapshot = fibInKillZone;
            bool fibExhaustionSnapshot = fibExhaustionActive;
            int trendEmaLenSnapshot = TrendEmaLength;

            // Trade history/stats come from NinjaScript's own SystemPerformance.AllTrades
            // rather than a hand-rolled trade log -- reuses NT8's built-in record of every
            // closed trade (entry/exit price, signal name, $ profit) instead of duplicating
            // that bookkeeping. Wrapped in try/catch: this is the one Cockpit API surface
            // this rebuild couldn't fully confirm against NinjaTrader's own docs offline --
            // see "TRADE STATS SOURCE" in the SECTION 8 header comment.
            int wins = 0, losses = 0;
            double realizedTotal = 0, maxWin = 0, maxLoss = 0;
            var curvePoints = new List<double>();
            var recentTrades = new List<string>();
            var recentWins = new List<bool>();

            // Trade Analyzer tab (all-time, per-signal) -- see "TRADE ANALYZER TAB" in the
            // SECTION 8 header comment. Same SystemPerformance.AllTrades loop as the Control
            // tab's stats above (deliberately not re-read a second time), extended to also
            // accumulate: a larger-capped equity curve (100 points vs. the Control tab's 40,
            // since the Analyzer curve isn't squeezed next to a stats grid and has room to
            // show more history), signed win/loss sums for profit factor + expectancy, a
            // running peak-to-trough drawdown, and a per-signal (rank name, or "Manual
            // Entry"/"Quick Entry (ATM)") breakdown of count/wins/pnl via MapSignalDisplayName.
            var analyzerCurvePoints = new List<double>();
            double sumWinAll = 0, sumLossAll = 0; // sumLossAll kept negative (signed), for profit factor below
            double equityPeak = 0, maxDrawdownAll = 0; // maxDrawdownAll stored as a positive $ amount (peak - trough)
            var signalCounts = new Dictionary<string, int>();
            var signalWins = new Dictionary<string, int>();
            var signalPnl = new Dictionary<string, double>();
            try
            {
                var allTrades = SystemPerformance.AllTrades;
                if (allTrades != null)
                {
                    double running = 0;
                    int count = allTrades.Count;
                    int curveStart = Math.Max(0, count - 40);
                    int analyzerCurveStart = Math.Max(0, count - 100);
                    for (int i = 0; i < count; i++)
                    {
                        var t = allTrades[i];
                        double pnl = t.ProfitCurrency;
                        realizedTotal += pnl;
                        if (pnl > 0) { wins++; if (pnl > maxWin) maxWin = pnl; sumWinAll += pnl; }
                        else if (pnl < 0) { losses++; if (pnl < maxLoss) maxLoss = pnl; sumLossAll += pnl; }
                        running += pnl;
                        if (i >= curveStart)
                            curvePoints.Add(running);
                        if (i >= analyzerCurveStart)
                            analyzerCurvePoints.Add(running);
                        if (running > equityPeak)
                            equityPeak = running;
                        double drawdownHere = equityPeak - running;
                        if (drawdownHere > maxDrawdownAll)
                            maxDrawdownAll = drawdownHere;
                        if (i >= count - 5)
                        {
                            string label = t.Entry != null ? t.Entry.Name : "Trade";
                            double entryPx = t.Entry != null ? t.Entry.Price : 0;
                            double exitPx = t.Exit != null ? t.Exit.Price : 0;
                            recentTrades.Add(label + "|" + entryPx.ToString("F2") + "|" + exitPx.ToString("F2"));
                            recentWins.Add(pnl >= 0);
                        }

                        string signalDisplay = MapSignalDisplayName(t.Entry != null ? t.Entry.Name : "");
                        if (!signalCounts.ContainsKey(signalDisplay))
                        {
                            signalCounts[signalDisplay] = 0;
                            signalWins[signalDisplay] = 0;
                            signalPnl[signalDisplay] = 0;
                        }
                        signalCounts[signalDisplay]++;
                        if (pnl > 0) signalWins[signalDisplay]++;
                        signalPnl[signalDisplay] += pnl;
                    }
                }
            }
            catch (Exception ex)
            {
                Print("PraedoxSentinel Cockpit trade-stats read failed: " + ex.Message);
            }

            int totalTrades = wins + losses;
            int winPct = totalTrades > 0 ? (int)Math.Round(100.0 * wins / totalTrades) : 0;
            double totalPnl = realizedTotal + unrealizedSnapshot;

            // Profit factor / expectancy -- standard definitions, computed from the signed
            // sums above. Profit factor guards against a divide-by-zero (no losses yet) by
            // reporting it as the gross win total itself (NinjaTrader's own Strategy
            // Analyzer shows "infinite" in this case; a plain number is friendlier in a
            // small dashboard tile than an infinity symbol).
            double grossWin = sumWinAll;
            double grossLoss = -sumLossAll; // make positive for the ratio
            double profitFactorAll = grossLoss > 0 ? grossWin / grossLoss : grossWin;
            double expectancyAll = totalTrades > 0 ? realizedTotal / totalTrades : 0;

            cockpitChartControl.Dispatcher.InvokeAsync(new Action(() =>
            {
              try
              {
                // Self-healing re-attach: NT8's chart redraws (most reliably, the
                // historical -> realtime transition) can silently drop a manually-added
                // WPF child without any event firing -- this is the "showed for a second
                // then disappeared" behavior seen on the first render. Checked every bar
                // rather than relying on a one-time OnStateChange hook -- see "COCKPIT
                // PANEL LIFECYCLE" in the SECTION 8 header comment. A second, independent
                // check on cockpitHealTimer (started in CreateCockpitPanel) backs this one
                // up on a wall-clock interval instead of a per-bar one -- see that timer's
                // own comment for why a per-bar-only check wasn't enough on slower timeframes.
                if (cockpitOuterBorder != null && cockpitChartControl != null && !cockpitChartControl.Children.Contains(cockpitOuterBorder))
                {
                    cockpitChartControl.Children.Add(cockpitOuterBorder);
                    System.Windows.Controls.Panel.SetZIndex(cockpitOuterBorder, 100);
                }

                if (activeSlotLabel != null)
                    activeSlotLabel.Text = "Active: ATM " + slotSnapshot + " (" + (atmOn ? "governing trail" : "trail off") + ") | Auto-Trading: " + (autoOn ? "ON" : "OFF");

                if (pnlValueText != null)
                {
                    pnlValueText.Text = (totalPnl >= 0 ? "+$" : "-$") + Math.Abs(totalPnl).ToString("N0");
                    pnlValueText.Foreground = totalPnl >= 0 ? Brushes.LimeGreen : Brushes.OrangeRed;
                }
                if (realUnrlText != null)
                    realUnrlText.Text = "Real: " + (realizedTotal >= 0 ? "$" : "-$") + Math.Abs(realizedTotal).ToString("N0") + "  Unrl: " + (unrealizedSnapshot >= 0 ? "$" : "-$") + Math.Abs(unrealizedSnapshot).ToString("N0");
                if (winRateText != null)
                    winRateText.Text = winPct + "%\nWIN";
                if (wlRecordText != null)
                    wlRecordText.Text = wins + "-" + losses + "\nW/L";
                if (maxPlusText != null)
                    maxPlusText.Text = "$" + maxProfitLimit.ToString("N0");
                if (maxMinusText != null)
                    maxMinusText.Text = "$" + maxLossLimit.ToString("N0");

                if (chopFilterText != null)
                    chopFilterText.Text = "CHOP FILTER: " + chopSnapshot.ToString("F1") + " / " + chopCeilingSnapshot.ToString("F1");

                if (timeStatusText != null)
                    timeStatusText.Text = timeSnapshot.ToString("h:mm:ss tt") + "\nAUTO FIBS " + (fibDirSnapshot < 0 ? "DOWN" : "UP") + " · " + (fibDynamicSnapshot ? "DYNAMIC" : "STALE");

                if (equityCurveCanvas != null)
                {
                    equityCurveCanvas.Children.Clear();
                    if (curvePoints.Count >= 2)
                    {
                        double w = equityCurveCanvas.ActualWidth > 0 ? equityCurveCanvas.ActualWidth : 220;
                        double h = equityCurveCanvas.Height;
                        double min = curvePoints[0], max = curvePoints[0];
                        for (int i = 1; i < curvePoints.Count; i++) { if (curvePoints[i] < min) min = curvePoints[i]; if (curvePoints[i] > max) max = curvePoints[i]; }
                        double range = Math.Max(1e-6, max - min);
                        var poly = new System.Windows.Shapes.Polyline { Stroke = totalPnl >= 0 ? Brushes.LimeGreen : Brushes.OrangeRed, StrokeThickness = 1.5 };
                        var points = new System.Windows.Media.PointCollection();
                        for (int i = 0; i < curvePoints.Count; i++)
                        {
                            double x = curvePoints.Count > 1 ? w * i / (curvePoints.Count - 1) : 0;
                            double y = h - (h * (curvePoints[i] - min) / range);
                            points.Add(new System.Windows.Point(x, y));
                        }
                        poly.Points = points;
                        equityCurveCanvas.Children.Add(poly);
                    }
                }

                if (lastTradesStack != null)
                {
                    lastTradesStack.Children.Clear();
                    for (int i = recentTrades.Count - 1; i >= 0; i--)
                    {
                        string[] parts = recentTrades[i].Split('|');
                        var row = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(0, 1, 0, 1) };
                        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(2, System.Windows.GridUnitType.Star) });
                        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
                        var nameTb = new System.Windows.Controls.TextBlock { Text = parts.Length > 0 ? parts[0] : "", Foreground = Brushes.White, FontSize = 10 };
                        var entryTb = new System.Windows.Controls.TextBlock { Text = parts.Length > 1 ? parts[1] : "", Foreground = Brushes.Gray, FontSize = 10 };
                        var exitTb = new System.Windows.Controls.TextBlock { Text = parts.Length > 2 ? parts[2] : "", Foreground = Brushes.Gray, FontSize = 10 };
                        var wlTb = new System.Windows.Controls.TextBlock { Text = (i < recentWins.Count && recentWins[i]) ? "W" : "L", Foreground = (i < recentWins.Count && recentWins[i]) ? Brushes.LimeGreen : Brushes.OrangeRed, FontWeight = System.Windows.FontWeights.Bold, FontSize = 10 };
                        System.Windows.Controls.Grid.SetColumn(nameTb, 0);
                        System.Windows.Controls.Grid.SetColumn(entryTb, 1);
                        System.Windows.Controls.Grid.SetColumn(exitTb, 2);
                        System.Windows.Controls.Grid.SetColumn(wlTb, 3);
                        row.Children.Add(nameTb);
                        row.Children.Add(entryTb);
                        row.Children.Add(exitTb);
                        row.Children.Add(wlTb);
                        lastTradesStack.Children.Add(row);
                    }
                    if (recentTrades.Count == 0)
                        lastTradesStack.Children.Add(new System.Windows.Controls.TextBlock { Text = "No closed trades yet", Foreground = Brushes.Gray, FontSize = 10, HorizontalAlignment = System.Windows.HorizontalAlignment.Center });
                }

                if (tradingOnButton != null) UpdateTradingOnButtonVisual();
                if (rawSignalButton != null) UpdateRawSignalButtonVisual();

                // -- Market Snapshot tab -- read-only current-conditions readout, no trading
                // actions. See "MARKET SNAPSHOT TAB" in the SECTION 8 header comment.
                if (snapshotPriceText != null)
                    snapshotPriceText.Text = "Price: " + priceSnapshot.ToString("F2") + (instrumentNameSnapshot.Length > 0 ? "  (" + instrumentNameSnapshot + ")" : "");
                if (snapshotVrText != null)
                    snapshotVrText.Text = "Volatility: " + VolStateNames[volStateSnapshot] + "  (VR " + vrSnapshot.ToString("F2") + ")";
                if (snapshotAtrText != null)
                    snapshotAtrText.Text = "ATR: " + atrSnapshot.ToString("F2") + "  (" + atrTicksSnapshot + " ticks)";
                if (snapshotChopText != null)
                    snapshotChopText.Text = "Chop: " + chopSnapshot.ToString("F1") + " / " + chopCeilingSnapshot.ToString("F1") + (chopSnapshot < chopCeilingSnapshot ? "  (trending)" : "  (choppy)");
                if (snapshotFibText != null)
                    snapshotFibText.Text = "Fib Grid: " + (fibDirSnapshot < 0 ? "DOWN" : "UP") + (fibKillZoneSnapshot ? "  · KILL ZONE" : "") + (fibExhaustionSnapshot ? "  · EXHAUSTION" : "") + (fibDynamicSnapshot ? "" : "  · STALE");
                if (snapshotTrendText != null)
                    snapshotTrendText.Text = "Trend EMA(" + trendEmaLenSnapshot + "): " + trendEmaSnapshot.ToString("F2") + "  |  Fast EMA: " + fastEmaSnapshot.ToString("F2") + "  |  Price is " + (priceSnapshot >= trendEmaSnapshot ? "ABOVE" : "BELOW");
                if (snapshotSessionText != null)
                    snapshotSessionText.Text = "Bar Time: " + timeSnapshot.ToString("h:mm:ss tt") + "  |  Trading Allowed: " + (currentVolAllowed ? "YES" : "NO") + (tradeProtectorBlocked ? "  · PROTECTOR BLOCK" : "");

                // -- Trade Analyzer tab -- all-time (not session-scoped) analytics. See
                // "TRADE ANALYZER TAB" in the SECTION 8 header comment.
                if (analyzerSummaryText != null)
                {
                    if (totalTrades > 0)
                    {
                        analyzerSummaryText.Text =
                            "Trades: " + totalTrades + "   Win%: " + winPct + "%\n" +
                            "Profit Factor: " + profitFactorAll.ToString("F2") + "\n" +
                            "Expectancy: " + (expectancyAll >= 0 ? "+$" : "-$") + Math.Abs(expectancyAll).ToString("F2") + " / trade\n" +
                            "Max Drawdown: -$" + maxDrawdownAll.ToString("N0") + "\n" +
                            "Realized Total: " + (realizedTotal >= 0 ? "+$" : "-$") + Math.Abs(realizedTotal).ToString("N0");
                    }
                    else
                    {
                        analyzerSummaryText.Text = "No closed trades yet";
                    }
                }

                if (analyzerCurveCanvas != null)
                {
                    analyzerCurveCanvas.Children.Clear();
                    if (analyzerCurvePoints.Count >= 2)
                    {
                        double w = analyzerCurveCanvas.ActualWidth > 0 ? analyzerCurveCanvas.ActualWidth : 220;
                        double h = analyzerCurveCanvas.Height;
                        double min = analyzerCurvePoints[0], max = analyzerCurvePoints[0];
                        for (int i = 1; i < analyzerCurvePoints.Count; i++) { if (analyzerCurvePoints[i] < min) min = analyzerCurvePoints[i]; if (analyzerCurvePoints[i] > max) max = analyzerCurvePoints[i]; }
                        double range = Math.Max(1e-6, max - min);
                        var poly = new System.Windows.Shapes.Polyline { Stroke = totalPnl >= 0 ? Brushes.LimeGreen : Brushes.OrangeRed, StrokeThickness = 1.5 };
                        var points = new System.Windows.Media.PointCollection();
                        for (int i = 0; i < analyzerCurvePoints.Count; i++)
                        {
                            double x = analyzerCurvePoints.Count > 1 ? w * i / (analyzerCurvePoints.Count - 1) : 0;
                            double y = h - (h * (analyzerCurvePoints[i] - min) / range);
                            points.Add(new System.Windows.Point(x, y));
                        }
                        poly.Points = points;
                        analyzerCurveCanvas.Children.Add(poly);
                    }
                }

                if (analyzerSignalBreakdownStack != null)
                {
                    analyzerSignalBreakdownStack.Children.Clear();
                    // Ordered rank 1-10 (matching the signal grid's own order) then Manual
                    // Entry / Quick Entry (ATM) last -- a fixed, predictable order rather than
                    // sorting by P&L, so a signal's row doesn't jump around position to
                    // position as trades close. Signals with zero trades so far are skipped
                    // rather than shown as an empty "0 trades" row.
                    var orderedNames = new List<string>();
                    for (int r = 1; r <= 10; r++) orderedNames.Add(SignalNames[r]);
                    orderedNames.Add("Manual Entry");
                    orderedNames.Add("Quick Entry (ATM)");

                    bool anyRow = false;
                    foreach (var name in orderedNames)
                    {
                        if (!signalCounts.ContainsKey(name) || signalCounts[name] <= 0)
                            continue;
                        anyRow = true;
                        int sCount = signalCounts[name];
                        int sWins = signalWins.ContainsKey(name) ? signalWins[name] : 0;
                        double sPnl = signalPnl.ContainsKey(name) ? signalPnl[name] : 0;
                        int sWinPct = sCount > 0 ? (int)Math.Round(100.0 * sWins / sCount) : 0;

                        var row = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(0, 1, 0, 1) };
                        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(2, System.Windows.GridUnitType.Star) });
                        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                        var nameTb = new System.Windows.Controls.TextBlock { Text = name, Foreground = Brushes.White, FontSize = 10, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis };
                        var recTb = new System.Windows.Controls.TextBlock { Text = sCount + " (" + sWinPct + "%)", Foreground = Brushes.Gray, FontSize = 10 };
                        var pnlTb = new System.Windows.Controls.TextBlock { Text = (sPnl >= 0 ? "+$" : "-$") + Math.Abs(sPnl).ToString("N0"), Foreground = sPnl >= 0 ? Brushes.LimeGreen : Brushes.OrangeRed, FontSize = 10, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
                        System.Windows.Controls.Grid.SetColumn(nameTb, 0);
                        System.Windows.Controls.Grid.SetColumn(recTb, 1);
                        System.Windows.Controls.Grid.SetColumn(pnlTb, 2);
                        row.Children.Add(nameTb);
                        row.Children.Add(recTb);
                        row.Children.Add(pnlTb);
                        analyzerSignalBreakdownStack.Children.Add(row);
                    }
                    if (!anyRow)
                        analyzerSignalBreakdownStack.Children.Add(new System.Windows.Controls.TextBlock { Text = "No closed trades yet", Foreground = Brushes.Gray, FontSize = 10, HorizontalAlignment = System.Windows.HorizontalAlignment.Center });
                }
              }
              catch (Exception ex)
              {
                  // This lambda runs on the UI dispatcher thread via InvokeAsync -- an
                  // unhandled exception here does NOT surface through the try/catch that
                  // wraps the rest of OnBarUpdate (different thread, different call stack),
                  // so without this catch a bad frame here could silently fail every bar
                  // with no diagnostic at all. Added after the dashboard rebuild once this
                  // became the far larger, more failure-prone UI update than before.
                  Print("PraedoxSentinel Cockpit UI update failed: " + ex.Message);
              }
            }));
        }

        // Click handlers (BUY CLICK/SELL CLICK/FLATTEN ALL, wired inline in
        // CreateCockpitPanel) only set volatile flags or call QueueManualEntry below, never
        // order-management methods directly. See "MANUAL ENTRY THREADING" in the SECTION 8
        // header comment.
        private void QueueManualEntry(int direction)
        {
            int qty = ParseIntOr(qtyTextBox != null ? qtyTextBox.Text : "", ManualDefaultQty);
            int stopTicks = ParseIntOr(stopTextBox != null ? stopTextBox.Text : "", ManualDefaultStopTicks);
            int targetTicks = ParseIntOr(targetTextBox != null ? targetTextBox.Text : "", ManualDefaultTargetTicks);

            manualPendingQty = qty;
            manualPendingStopTicks = stopTicks;
            manualPendingTargetTicks = targetTicks;

            if (direction > 0)
                manualBuyPending = true;
            else
                manualSellPending = true;
        }

        private static int ParseIntOr(string text, int fallback)
        {
            int result;
            bool ok = int.TryParse(text, out result);
            return ok ? result : fallback;
        }

        // ============================================================================
        // SECTION 2 -- FIB ENGINE (ported from PraedoxFibEngine_v0_1_0 -- see that file's
        // header, preserved conceptually here, for the full design rationale on swing
        // detection, the 8 levels, kill zone/confluence, and exhaustion suppression)
        // ============================================================================
        private void UpdateFibEngine()
        {
            if (CurrentBar < FibLookback + 1)
            {
                fibDirection = 0;
                fibNearKey = 0;
                fibInKillZone = false;
                fibExhaustionActive = false;
                return;
            }

            double swingHigh, swingLow;
            int swingHighBarsAgo, swingLowBarsAgo;
            FindSwingExtremes(FibLookback, out swingHigh, out swingHighBarsAgo, out swingLow, out swingLowBarsAgo);

            int direction;
            if (swingHighBarsAgo < swingLowBarsAgo)
            {
                direction = -1;
                fibAnchorBarsAgo = swingHighBarsAgo;
            }
            else if (swingLowBarsAgo < swingHighBarsAgo)
            {
                direction = 1;
                fibAnchorBarsAgo = swingLowBarsAgo;
            }
            else
            {
                direction = fibLastDirection != 0 ? fibLastDirection : 1;
                fibAnchorBarsAgo = Math.Min(swingHighBarsAgo, swingLowBarsAgo);
            }

            bool flipped = fibLastDirection != 0 && direction != fibLastDirection;
            fibLastDirection = direction;

            double range = swingHigh - swingLow;
            for (int i = 0; i < FibLevelPercents.Length; i++)
            {
                double pct = FibLevelPercents[i] / 100.0;
                double price = direction > 0 ? swingLow + range * pct : swingHigh - range * pct;
                fibLevelPrices[i] = Instrument.MasterInstrument.RoundToTickSize(price);
            }

            double level618 = fibLevelPrices[4];
            double level650 = fibLevelPrices[5];
            double level786 = fibLevelPrices[6];

            double tolerance = TickSize * KillZoneTicks;
            bool nearAny =
                Math.Abs(Close[0] - level618) <= tolerance ||
                Math.Abs(Close[0] - level650) <= tolerance ||
                Math.Abs(Close[0] - level786) <= tolerance;

            bool exhaustion = fibAnchorBarsAgo >= StaleBarsThreshold;

            fibDirection = direction;
            fibNearKey = nearAny ? direction : 0;
            fibInKillZone = nearAny;
            fibExhaustionActive = exhaustion;

            if (DrawFibLines || DrawGoldenZone || DrawGoldenPocket || DrawKillZones || DrawExhaustionZone)
                DrawFibGrid(swingHigh, swingLow, direction, level618, level650, level786, exhaustion);

            if (DrawFlipAlerts && flipped)
            {
                string text = direction > 0 ? "Fib flip -> UP" : "Fib flip -> DOWN";
                Brush color = direction > 0 ? Brushes.Lime : Brushes.OrangeRed;
                Draw.Text(this, "PraedoxFibFlip" + CurrentBar, text, 0, direction > 0 ? Low[0] - 4 * TickSize : High[0] + 4 * TickSize, color);
            }

            if (flipped)
                FireAlert("PraedoxAlertFibFlip", EnableAlertFibFlip, AlertFibFlipSound, "Praedox Sentinel: fib flip -> " + (direction > 0 ? "UP" : "DOWN"));
        }

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

        private void DrawFibGrid(double swingHigh, double swingLow, int direction, double level618, double level650, double level786, bool exhaustion)
        {
            int anchorSpan = Math.Max(fibAnchorBarsAgo, 1);
            int displaySpan = Math.Min(anchorSpan, ZoneDisplayBars);

            if (DrawFibLines)
            {
                for (int i = 0; i < FibLevelPercents.Length; i++)
                {
                    double price = fibLevelPrices[i];
                    string tag = "PraedoxFibLevel" + i;
                    Brush lineBrush = (i == 0 || i == FibLevelPercents.Length - 1) ? Brushes.DimGray : Brushes.Goldenrod;
                    Draw.Rectangle(this, tag, false, displaySpan, price + TickSize * 0.5, 0, price - TickSize * 0.5, lineBrush, lineBrush, 40);

                    int pixelNudge = (i % 2 == 0) ? -7 : 7;
                    Draw.Text(this, tag + "Label", false, FibLevelLabels[i], 0, price, pixelNudge, Brushes.White, fibLabelFont, TextAlignment.Left, Brushes.Black, Brushes.Black, 80);
                }
            }

            if (DrawGoldenZone)
            {
                double a = fibLevelPrices[3];
                double b = fibLevelPrices[4];
                Draw.Rectangle(this, "PraedoxGoldenZone", false, displaySpan, Math.Max(a, b), 0, Math.Min(a, b), Brushes.SteelBlue, Brushes.SteelBlue, 15);
            }

            if (DrawGoldenPocket)
            {
                double a = fibLevelPrices[4];
                double b = fibLevelPrices[5];
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

        // ============================================================================
        // SECTION 3 -- VOLATILITY SHIELD / SMART TRADER (ported from
        // PraedoxVolatilityShield_v0_1_0 -- see that file's header for the full design
        // rationale on the VR formula, the 4 directional bands, and Smart Trader's modes)
        // ============================================================================
        private void UpdateVolatilityShield()
        {
            currentVolWarmedUp = CurrentBar >= VrAtrLength + VrBaselineLength + 2;
            if (!currentVolWarmedUp)
            {
                currentVr = 0;
                currentVolState = 0;
                currentVolAllowed = true; // safe default during warmup -- see field comment pattern used throughout this project
                currentMinStopTicks = 0;
                currentMinTargetTicks = 0;
                return;
            }

            double vr = vrAtrBaseline[0] > 0 ? vrAtr[0] / vrAtrBaseline[0] : 1.0;

            int state;
            if (vr < YellowLowThreshold)
                state = 1;
            else if (vr <= RedHighThreshold)
                state = 0;
            else if (vr <= DarkRedThreshold)
                state = 2;
            else
                state = 3;

            bool allowed;
            if (!EnableSmartTrader)
            {
                allowed = true;
            }
            else
            {
                switch (state)
                {
                    case 3: allowed = false; break;
                    case 2: allowed = Mode == PraedoxSmartTraderMode.GreenYellowAndRed; break;
                    case 1: allowed = Mode == PraedoxSmartTraderMode.GreenAndYellow || Mode == PraedoxSmartTraderMode.GreenYellowAndRed; break;
                    default: allowed = true; break;
                }
            }

            if (!EnableVolatilityShield)
                allowed = true;

            double stopMult, targetMult;
            switch (state)
            {
                case 1: stopMult = YellowMinStopAtrMult; targetMult = YellowMinTargetAtrMult; break;
                case 2: stopMult = RedMinStopAtrMult; targetMult = RedMinTargetAtrMult; break;
                case 3: stopMult = DarkRedMinStopAtrMult; targetMult = DarkRedMinTargetAtrMult; break;
                default: stopMult = GreenMinStopAtrMult; targetMult = GreenMinTargetAtrMult; break;
            }

            currentVr = vr;
            currentVolState = state;
            currentVolAllowed = allowed;
            currentMinStopTicks = TickSize > 0 ? Math.Round(vrAtr[0] * stopMult / TickSize) : 0;
            currentMinTargetTicks = TickSize > 0 ? Math.Round(vrAtr[0] * targetMult / TickSize) : 0;

            if (DrawVolShieldLabel)
            {
                string text = "VR " + vr.ToString("0.00") + " - " + VolStateNames[state];
                Draw.Text(this, "PraedoxVolShieldLabel", false, text, 0, High[0], 20, Brushes.White, volLabelFont, TextAlignment.Left, Brushes.Black, Brushes.Black, 80);
            }
        }

        // ============================================================================
        // STATUS OVERLAY (Fib + Volatility Shield combined readout, top-left)
        // ============================================================================
        private void DrawStatusOverlay()
        {
            if (!ShowStatusOverlay)
            {
                RemoveDrawObject("PraedoxOverlay");
                return;
            }

            var lines = new System.Collections.Generic.List<string>();
            bool severe = false;
            bool caution = false;

            if (EnableVolatilityShield && currentVolWarmedUp)
            {
                string stateMsg = (currentVolState >= 0 && currentVolState < VolStateMessages.Length) ? VolStateMessages[currentVolState] : "NORMAL";
                lines.Add("VR: " + currentVr.ToString("0.00") + " | " + stateMsg);
                if (currentVolState == 3) severe = true;
                else if (currentVolState != 0) caution = true;
            }

            if (EnableFibConfluence && fibInKillZone)
            {
                lines.Add("⚠ FIB SUPPRESSION ACTIVE");
                caution = true;
            }

            if (EnableFibConfluence && fibExhaustionActive)
            {
                lines.Add("⚠ EXHAUSTION SUPPRESSION ACTIVE (ALWAYS 0%)");
                caution = true;
            }

            int atrTicks = TickSize > 0 ? (int)Math.Round(atr[0] / TickSize) : 0;
            lines.Add("ATR: " + atrTicks + "t");

            if (EnableFibConfluence && fibLevelPrices[0] > 0)
                lines.Add("Fib Anchored @ " + fibLevelPrices[0].ToString("0.00"));

            Brush color = severe ? Brushes.Red : (caution ? Brushes.Gold : Brushes.White);
            Draw.TextFixed(this, "PraedoxOverlay", string.Join("\n", lines), TextPosition.TopLeft, color, overlayFont, Brushes.Transparent, Brushes.Black, 60);
        }

        // ============================================================================
        // SECTION 1 -- SIGNAL ENGINE helpers + the 10 signal checks (ported from
        // PraedoxSignalEngine_v0_1_0 -- unchanged behavior, see that file's header for
        // the full per-signal design rationale)
        // ============================================================================
        private bool CooldownOk(int rank)
        {
            return CurrentBar - lastFireBarByRank[rank] >= CooldownBars;
        }

        private bool ChopOk()
        {
            return !ApplyChopFilterBroadly || currentChop <= ChopCeiling;
        }

        /// <summary>
        /// Dispatches to the numbered CheckX() method for a given rank -- lets the
        /// priority loop in OnBarUpdate call any of the 10 signals uniformly instead of
        /// the old hand-written if/else-if chain, which is what made it impossible for a
        /// Smart-Filters rejection to fall through to the next-priority signal instead of
        /// stopping the whole bar cold.
        /// </summary>
        private int EvaluateSignal(int rank, bool fibSuppressed, double fibNearKeySigned)
        {
            switch (rank)
            {
                case 1: return CheckSlingshot(fibSuppressed);
                case 2: return CheckZoneBreak(fibSuppressed);
                case 3: return CheckTrendCycleWave();
                case 4: return CheckCrossFire();
                case 5: return CheckReversal(fibNearKeySigned);
                case 6: return CheckContinuation(fibSuppressed);
                case 7: return CheckEmaBreakout(fibSuppressed);
                case 8: return CheckAggressiveEma(fibSuppressed);
                case 9: return CheckVolumeAggressive();
                case 10: return CheckBreakout(fibSuppressed);
                default: return 0;
            }
        }

        // ============================================================================
        // SMART FILTERS (ported design intent from the surviving marketing copy's "Smart
        // Filters: EMA/chop/S-R/large-bar/session-time" -- see "SMART FILTERS WIRING" in
        // the file header for the full per-filter rationale). Chop is NOT re-implemented
        // here -- ChopOk()/ApplyChopFilterBroadly above already IS the chop component,
        // built back in Phase 1; this section adds the other four.
        // ============================================================================

        /// <summary>
        /// True if a prospective fire (this rank, this direction) survives every enabled
        /// Smart Filter. A signal that fails here is treated exactly like a cooldown miss
        /// by the caller -- skipped, not a hard stop for the bar.
        /// </summary>
        private bool SmartFiltersPass(int rank, int direction)
        {
            // Cockpit dashboard's RAW SIGNAL MODE toggle -- an explicit, live-switchable
            // bypass of every Smart Filter below (macro-EMA / large-bar / S-R-proximity /
            // session-time). Named and requested by the reference screenshot; this rebuild
            // interprets it as "signals fire exactly as detected, unfiltered" rather than
            // touching cooldown or the Volatility Shield / Trade Protector gates, which are
            // risk controls rather than signal-quality filters and stay active regardless.
            if (RawSignalMode)
                return true;

            if (EnableMacroEmaFilter)
            {
                bool macroUp = Close[0] > macroEma[0];
                if (direction > 0 && !macroUp) return false;
                if (direction < 0 && macroUp) return false;
            }

            // Large-bar filter: Slingshot (1) and Breakout (10) specifically WANT an
            // expansion/release bar by design (see their own header comments) -- exempt,
            // or this filter would fight its own purpose. Applies to the other 8.
            if (EnableLargeBarFilter && rank != 1 && rank != 10)
            {
                double barRange = High[0] - Low[0];
                if (barRange > atr[0] * LargeBarAtrMultiple)
                    return false;
            }

            // S/R proximity filter: scoped to the trend/momentum-CONTINUATION family only
            // (Trend Cycle Wave, CrossFire, Continuation, EMA Breakout, Aggressive EMA,
            // Volume Aggressive) -- their thesis is "the move keeps going," which an
            // untested wall sitting right in front of price can invalidate. Deliberately
            // NOT applied to Reversal (whose whole thesis IS trading into a level), nor to
            // Zone Break/Breakout/Slingshot (which trade THROUGH or off a level, not near
            // one while ranging) -- applying it there would suppress the exact setups
            // those signals exist to catch.
            if (EnableSrProximityFilter && IsMomentumContinuationSignal(rank))
            {
                double srTolerance = TickSize * SrProximityTicks;
                double srHigh = MAX(High, SrLookback)[1];
                double srLow = MIN(Low, SrLookback)[1];
                bool nearSr = Math.Abs(Close[0] - srHigh) <= srTolerance || Math.Abs(Close[0] - srLow) <= srTolerance;
                if (nearSr)
                    return false;
            }

            if (EnableSessionTimeFilter)
            {
                TimeSpan barTime = Time[0].TimeOfDay;
                bool inSession = SessionStartTime <= SessionEndTime
                    ? (barTime >= SessionStartTime && barTime <= SessionEndTime)
                    : (barTime >= SessionStartTime || barTime <= SessionEndTime); // overnight wrap (e.g. 18:00-17:00)
                if (!inSession)
                    return false;
            }

            return true;
        }

        private static bool IsMomentumContinuationSignal(int rank)
        {
            // Trend Cycle Wave(3), CrossFire(4), Continuation(6), EMA Breakout(7),
            // Aggressive EMA(8), Volume Aggressive(9) -- see SmartFiltersPass comment.
            return rank == 3 || rank == 4 || rank == 6 || rank == 7 || rank == 8 || rank == 9;
        }

        private int CheckSlingshot(bool fibSuppressed)
        {
            if (!EnableSlingshot || !GroupZone || !ChopOk() || fibSuppressed)
                return 0;

            double compressedRange = MAX(High, CompressionLookback)[1] - MIN(Low, CompressionLookback)[1];
            bool wasCompressed = compressedRange < atr[1] * CompressionAtrFraction;
            if (!wasCompressed)
                return 0;

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

        private int CheckZoneBreak(bool fibSuppressed)
        {
            if (!EnableZoneBreak || !GroupZone || fibSuppressed)
                return 0;

            double zoneHigh = MAX(High, ZoneBreakLookback)[1];
            double zoneLow = MIN(Low, ZoneBreakLookback)[1];

            bool brokeUp = Close[0] > zoneHigh && Close[0] - zoneHigh > TickSize * MinBreakDistanceTicks;
            bool brokeDown = Close[0] < zoneLow && zoneLow - Close[0] > TickSize * MinBreakDistanceTicks;
            if (!brokeUp && !brokeDown)
                return 0;

            bool trendUp = Close[0] > trendEma[0];
            if (brokeUp && !trendUp) return 0;
            if (brokeDown && trendUp) return 0;

            if (currentChop > ChopCeiling)
                return 0;

            return brokeUp ? 1 : -1;
        }

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

        private int CheckReversal(double fibNearKeySigned)
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

            double upperWick = High[0] - Math.Max(Open[0], Close[0]);
            double lowerWick = Math.Min(Open[0], Close[0]) - Low[0];

            if (atStructuralHigh && rsi[0] >= RsiOverbought && upperWick / barRange > 0.5)
                return -1;
            if (atStructuralLow && rsi[0] <= RsiOversold && lowerWick / barRange > 0.5)
                return 1;

            if (fibNearKeySigned > 0 && upperWick / barRange > 0.5)
                return -1;
            if (fibNearKeySigned < 0 && lowerWick / barRange > 0.5)
                return 1;
            return 0;
        }

        private int CheckContinuation(bool fibSuppressed)
        {
            if (!EnableContinuation || !GroupReversal || !ChopOk() || fibSuppressed)
                return 0;

            bool trendUp = trendEma[0] > trendEma[5];
            bool trendDown = trendEma[0] < trendEma[5];

            bool touchedEmaLong = Low[1] <= trendEma[1] && Close[1] >= trendEma[1];
            bool touchedEmaShort = High[1] >= trendEma[1] && Close[1] <= trendEma[1];

            if (trendUp && touchedEmaLong && Close[0] > trendEma[0] && Close[0] > Close[1])
                return 1;
            if (trendDown && touchedEmaShort && Close[0] < trendEma[0] && Close[0] < Close[1])
                return -1;
            return 0;
        }

        private int CheckEmaBreakout(bool fibSuppressed)
        {
            if (!EnableEmaBreakout || !GroupBreakout || !ChopOk() || fibSuppressed)
                return 0;

            bool crossedUp = Close[1] > trendEma[1] && Close[2] <= trendEma[2];
            bool crossedDown = Close[1] < trendEma[1] && Close[2] >= trendEma[2];

            if (crossedUp && Close[0] > trendEma[0] && rsi[0] > 50)
                return 1;
            if (crossedDown && Close[0] < trendEma[0] && rsi[0] < 50)
                return -1;
            return 0;
        }

        private int CheckAggressiveEma(bool fibSuppressed)
        {
            if (!EnableAggressiveEma || !GroupBreakout || !ChopOk() || fibSuppressed)
                return 0;

            bool crossedUp = Close[0] > fastEma[0] && Close[1] <= fastEma[1];
            bool crossedDown = Close[0] < fastEma[0] && Close[1] >= fastEma[1];

            if (crossedUp) return 1;
            if (crossedDown) return -1;
            return 0;
        }

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

        private int CheckBreakout(bool fibSuppressed)
        {
            if (!EnableBreakout || !GroupBreakout || !ChopOk() || fibSuppressed)
                return 0;

            double structHigh = MAX(High, BreakoutLookback)[1];
            double structLow = MIN(Low, BreakoutLookback)[1];

            bool brokeUp = Close[0] > structHigh;
            bool brokeDown = Close[0] < structLow;
            if (!brokeUp && !brokeDown)
                return 0;

            if (High[0] - Low[0] < atr[0])
                return 0;

            return brokeUp ? 1 : -1;
        }

        private double ChoppinessIndex(int length)
        {
            double sumTr = 0;
            for (int i = 0; i < length; i++)
                sumTr += TrueRangeAt(i);

            double rangeHigh = MAX(High, length)[0];
            double rangeLow = MIN(Low, length)[0];
            double range = rangeHigh - rangeLow;
            if (range <= 0 || sumTr <= 0)
                return 0;

            return 100.0 * Math.Log10(sumTr / range) / Math.Log10(length);
        }

        private double TrueRangeAt(int barsAgo)
        {
            if (CurrentBar - barsAgo < 1)
                return High[barsAgo] - Low[barsAgo];

            double h = High[barsAgo];
            double l = Low[barsAgo];
            double pc = Close[barsAgo + 1];
            return Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
        }

        // ============================================================================
        // SECTION 4 -- TRADE PROTECTOR / DAILY P&L LIMITS (ported from
        // PraedoxTradeProtector_v0_1_0 -- see that file's header for the full design
        // rationale on the win/loss streak basis, P&L source, "session" definition, and
        // exactly what "auto-stops trading" can and can't guarantee)
        // ============================================================================
        private void EnsureTradeProtectorStateLoaded()
        {
            if (tpStateLoaded || Account == null)
                return;

            tpStateFilePath = BuildTpStateFilePath(Account.Name);
            LoadTpState();
            tpStateLoaded = true;

            double currentRealized = Account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
            tpCurrentRealizedPnl = currentRealized;

            if (!tpBaselineInitialized)
            {
                tpRealizedBaseline = currentRealized;
                tpLastFlatRealizedPnl = currentRealized;
                tpBaselineInitialized = true;
                SaveTpState();
            }
        }

        protected override void OnAccountItemUpdate(Account account, AccountItem accountItem, double value)
        {
            if (account != Account || accountItem != AccountItem.RealizedProfitLoss)
                return;

            EnsureTradeProtectorStateLoaded();
            tpCurrentRealizedPnl = value;

            CheckDailyPnLLimits();
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (position.Account != Account || marketPosition != MarketPosition.Flat)
                return;

            // FibGuard / Cockpit ATM: trade is closed -- clear both frozen snapshots so
            // the next entry starts fresh (see "FIBGUARD" and SECTION 8 header comments).
            fgActive = false;
            fgCurrentStopPrice = double.NaN;
            atmTradeActive = false;
            atmCurrentStopPrice = double.NaN;
            activeEntrySignalName = null;

            EnsureTradeProtectorStateLoaded();

            double delta = tpCurrentRealizedPnl - tpLastFlatRealizedPnl;
            tpLastFlatRealizedPnl = tpCurrentRealizedPnl;

            if (delta > 0)
            {
                tpWinStreak++;
                tpLossStreak = 0;
            }
            else if (delta < 0)
            {
                tpLossStreak++;
                tpWinStreak = 0;
            }

            SaveTpState();
            CheckTradeProtectorLimits();
        }

        private void CheckDailyPnLLimits()
        {
            if (!EnableDailyPnLLimits || tradeProtectorBlocked || !tpBaselineInitialized)
                return;

            double sessionPnl = tpCurrentRealizedPnl - tpRealizedBaseline;

            if (sessionPnl >= MaxDailyProfitDollars)
                TriggerTpBlock(3);
            else if (sessionPnl <= -MaxDailyLossDollars)
                TriggerTpBlock(4);
        }

        private void CheckTradeProtectorLimits()
        {
            if (!EnableTradeProtector || tradeProtectorBlocked)
                return;

            if (tpWinStreak >= MaxConsecutiveWins)
                TriggerTpBlock(1);
            else if (tpLossStreak >= MaxConsecutiveLosses)
                TriggerTpBlock(2);
        }

        private void TriggerTpBlock(int reasonCode)
        {
            tradeProtectorBlocked = true;
            tpBlockReasonCode = reasonCode;
            SaveTpState();

            switch (reasonCode)
            {
                case 1: FireAlert("PraedoxAlertTpWinStreak", EnableAlertTpWinStreak, AlertTpWinStreakSound, "Praedox Sentinel: win streak limit reached"); break;
                case 2: FireAlert("PraedoxAlertTpLossStreak", EnableAlertTpLossStreak, AlertTpLossStreakSound, "Praedox Sentinel: loss streak limit reached"); break;
                case 3: FireAlert("PraedoxAlertTpMaxProfit", EnableAlertTpMaxProfit, AlertTpMaxProfitSound, "Praedox Sentinel: max daily profit reached"); break;
                case 4: FireAlert("PraedoxAlertTpMaxLoss", EnableAlertTpMaxLoss, AlertTpMaxLossSound, "Praedox Sentinel: max daily loss reached"); break;
            }

            if (AutoFlatten)
                FlattenAccount();
        }

        private void FlattenAccount()
        {
            try
            {
                var instruments = Account.Positions
                    .Where(p => p.MarketPosition != MarketPosition.Flat)
                    .Select(p => p.Instrument)
                    .Distinct()
                    .ToList();

                if (instruments.Count > 0)
                    Account.Flatten(instruments);
            }
            catch (Exception ex)
            {
                Log("PraedoxSentinel: flatten failed -- " + ex.Message, LogLevel.Error);
            }
        }

        private void DrawTradeProtectorPanel()
        {
            if (!ShowProtectorPanel)
            {
                RemoveDrawObject("PraedoxProtectorPanel");
                RemoveDrawObject("PraedoxProtectorWarning");
                return;
            }

            double sessionPnl = tpBaselineInitialized ? tpCurrentRealizedPnl - tpRealizedBaseline : 0;
            string panelText = "Session P&L: " + sessionPnl.ToString("+$0.00;-$0.00;$0.00")
                + "\nWin Streak: " + tpWinStreak + " / " + MaxConsecutiveWins
                + "\nLoss Streak: " + tpLossStreak + " / " + MaxConsecutiveLosses;
            Brush panelColor = tradeProtectorBlocked ? Brushes.Red : Brushes.White;
            Draw.TextFixed(this, "PraedoxProtectorPanel", panelText, TextPosition.TopRight, panelColor, protectorPanelFont, Brushes.Transparent, Brushes.Black, 60);

            if (tradeProtectorBlocked)
            {
                string reasonText = (tpBlockReasonCode >= 0 && tpBlockReasonCode < TpBlockReasonText.Length) ? TpBlockReasonText[tpBlockReasonCode] : "TRADING BLOCKED";
                Draw.TextFixed(this, "PraedoxProtectorWarning", "⛔ TRADING BLOCKED: " + reasonText, TextPosition.BottomRight, Brushes.Red, protectorWarningFont, Brushes.Transparent, Brushes.Black, 70);
            }
            else
            {
                RemoveDrawObject("PraedoxProtectorWarning");
            }
        }

        #region Trade Protector persistence
        private string BuildTpStateFilePath(string accountName)
        {
            string safeName = accountName;
            foreach (char c in Path.GetInvalidFileNameChars())
                safeName = safeName.Replace(c, '_');

            string dir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "PraedoxTradeProtectorState");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, safeName + "_Sentinel.txt");
        }

        private void LoadTpState()
        {
            tpSessionDate = DateTime.Now.Date;
            tpBaselineInitialized = false;
            tpWinStreak = 0;
            tpLossStreak = 0;
            tradeProtectorBlocked = false;
            tpBlockReasonCode = 0;

            if (string.IsNullOrEmpty(tpStateFilePath) || !File.Exists(tpStateFilePath))
                return;

            try
            {
                var values = new System.Collections.Generic.Dictionary<string, string>();
                foreach (string line in File.ReadAllLines(tpStateFilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    values[line.Substring(0, eq)] = line.Substring(eq + 1);
                }

                string dateStr;
                DateTime storedDate;
                if (!values.TryGetValue("Date", out dateStr) || !DateTime.TryParse(dateStr, out storedDate) || storedDate.Date != tpSessionDate)
                    return; // stale (different day) -- fresh defaults above, resets each session

                tpRealizedBaseline = ParseTpDoubleOr(values, "RealizedBaseline", 0);
                tpLastFlatRealizedPnl = ParseTpDoubleOr(values, "LastFlatRealizedPnl", 0);
                tpWinStreak = (int)ParseTpDoubleOr(values, "WinStreak", 0);
                tpLossStreak = (int)ParseTpDoubleOr(values, "LossStreak", 0);
                string blockedStr;
                tradeProtectorBlocked = values.TryGetValue("Blocked", out blockedStr) && blockedStr == "1";
                tpBlockReasonCode = (int)ParseTpDoubleOr(values, "BlockReason", 0);
                tpBaselineInitialized = true;
            }
            catch (Exception ex)
            {
                Log("PraedoxSentinel: trade protector state load failed -- " + ex.Message, LogLevel.Warning);
            }
        }

        private double ParseTpDoubleOr(System.Collections.Generic.Dictionary<string, string> values, string key, double fallback)
        {
            string s;
            double result;
            return (values.TryGetValue(key, out s) && double.TryParse(s, out result)) ? result : fallback;
        }

        private void SaveTpState()
        {
            if (string.IsNullOrEmpty(tpStateFilePath))
                return;

            try
            {
                string[] lines =
                {
                    "Date=" + tpSessionDate.ToString("yyyy-MM-dd"),
                    "RealizedBaseline=" + tpRealizedBaseline,
                    "LastFlatRealizedPnl=" + tpLastFlatRealizedPnl,
                    "WinStreak=" + tpWinStreak,
                    "LossStreak=" + tpLossStreak,
                    "Blocked=" + (tradeProtectorBlocked ? "1" : "0"),
                    "BlockReason=" + tpBlockReasonCode
                };
                File.WriteAllLines(tpStateFilePath, lines);
            }
            catch (Exception ex)
            {
                Log("PraedoxSentinel: trade protector state save failed -- " + ex.Message, LogLevel.Warning);
            }
        }
        #endregion

        #region Properties -- Signal Engine: Core
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Trend EMA Length", GroupName = "01 Signal Engine - Core", Order = 1)]
        public int TrendEmaLength { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Fast EMA Length", GroupName = "01 Signal Engine - Core", Order = 2)]
        public int FastEmaLength { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "ATR Length", GroupName = "01 Signal Engine - Core", Order = 3)]
        public int AtrLength { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "RSI Length", GroupName = "01 Signal Engine - Core", Order = 4)]
        public int RsiLength { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Stochastics Length", GroupName = "01 Signal Engine - Core", Order = 5)]
        public int StochLength { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Stochastics Smooth", GroupName = "01 Signal Engine - Core", Order = 6)]
        public int StochSmooth { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Volume Average Length", GroupName = "01 Signal Engine - Core", Order = 7)]
        public int VolumeAvgLength { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Cooldown Bars (per signal)", GroupName = "01 Signal Engine - Core", Order = 8)]
        public int CooldownBars { get; set; }
        #endregion

        #region Properties -- Signal Engine: per-signal parameters
        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "Compression Lookback", GroupName = "02 Signal Engine - Slingshot", Order = 1)]
        public int CompressionLookback { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Compression ATR Fraction", GroupName = "02 Signal Engine - Slingshot", Order = 2)]
        public double CompressionAtrFraction { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Release ATR Multiple", GroupName = "02 Signal Engine - Slingshot", Order = 3)]
        public double ReleaseAtrMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "Zone Break Lookback", GroupName = "03 Signal Engine - Zone/Reversal", Order = 1)]
        public int ZoneBreakLookback { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Min Break Distance (ticks)", GroupName = "03 Signal Engine - Zone/Reversal", Order = 2)]
        public int MinBreakDistanceTicks { get; set; }

        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "Choppiness Length", GroupName = "03 Signal Engine - Zone/Reversal", Order = 3)]
        public int ChopLength { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Choppiness Ceiling", GroupName = "03 Signal Engine - Zone/Reversal", Order = 4)]
        public double ChopCeiling { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Apply Chop Filter Broadly", GroupName = "03 Signal Engine - Zone/Reversal", Order = 5)]
        public bool ApplyChopFilterBroadly { get; set; }

        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "Breakout Lookback (major structure)", GroupName = "04 Signal Engine - Breakout", Order = 1)]
        public int BreakoutLookback { get; set; }

        [NinjaScriptProperty]
        [Range(50, 100)]
        [Display(Name = "RSI Overbought", GroupName = "05 Signal Engine - Reversal", Order = 1)]
        public double RsiOverbought { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "RSI Oversold", GroupName = "05 Signal Engine - Reversal", Order = 2)]
        public double RsiOversold { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Stochastics Oversold", GroupName = "06 Signal Engine - Cycle", Order = 1)]
        public double StochOversold { get; set; }

        [NinjaScriptProperty]
        [Range(50, 100)]
        [Display(Name = "Stochastics Overbought", GroupName = "06 Signal Engine - Cycle", Order = 2)]
        public double StochOverbought { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, double.MaxValue)]
        [Display(Name = "Volume Surge Multiple", GroupName = "07 Signal Engine - Momentum", Order = 1)]
        public double VolumeSurgeMultiple { get; set; }
        #endregion

        #region Properties -- Signal Engine: toggles/groups
        [NinjaScriptProperty]
        [Display(Name = "Enable: Slingshot", GroupName = "08 Signal Engine - Toggles", Order = 1)]
        public bool EnableSlingshot { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: Zone Break", GroupName = "08 Signal Engine - Toggles", Order = 2)]
        public bool EnableZoneBreak { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: Trend Cycle Wave", GroupName = "08 Signal Engine - Toggles", Order = 3)]
        public bool EnableTrendCycleWave { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: CrossFire", GroupName = "08 Signal Engine - Toggles", Order = 4)]
        public bool EnableCrossFire { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: Reversal", GroupName = "08 Signal Engine - Toggles", Order = 5)]
        public bool EnableReversal { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: Continuation", GroupName = "08 Signal Engine - Toggles", Order = 6)]
        public bool EnableContinuation { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: EMA Breakout", GroupName = "08 Signal Engine - Toggles", Order = 7)]
        public bool EnableEmaBreakout { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: Aggressive EMA", GroupName = "08 Signal Engine - Toggles", Order = 8)]
        public bool EnableAggressiveEma { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: Volume Aggressive", GroupName = "08 Signal Engine - Toggles", Order = 9)]
        public bool EnableVolumeAggressive { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: Breakout", GroupName = "08 Signal Engine - Toggles", Order = 10)]
        public bool EnableBreakout { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Group: Zone (Slingshot + Zone Break)", GroupName = "09 Signal Engine - Groups", Order = 1)]
        public bool GroupZone { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Group: Reversal (Reversal + Continuation)", GroupName = "09 Signal Engine - Groups", Order = 2)]
        public bool GroupReversal { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Group: Breakout (EMA Breakout + Breakout + Aggressive EMA)", GroupName = "09 Signal Engine - Groups", Order = 3)]
        public bool GroupBreakout { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Group: Cycle (Trend Cycle Wave)", GroupName = "09 Signal Engine - Groups", Order = 4)]
        public bool GroupCycle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Group: Momentum (CrossFire + Volume Aggressive)", GroupName = "09 Signal Engine - Groups", Order = 5)]
        public bool GroupMomentum { get; set; }
        #endregion

        #region Properties -- Fib Engine
        [NinjaScriptProperty]
        [Display(Name = "Enable Fib Confluence", GroupName = "10 Fib Engine", Order = 1, Description = "Wires the Fib Engine's kill-zone/exhaustion outputs to suppress breakout/continuation signals, and near-key-level to give Reversal an alternate confluence path.")]
        public bool EnableFibConfluence { get; set; }

        [NinjaScriptProperty]
        [Range(5, int.MaxValue)]
        [Display(Name = "Fib Lookback (bars)", GroupName = "10 Fib Engine", Order = 2)]
        public int FibLookback { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Stale/Exhaustion Threshold (bars)", GroupName = "10 Fib Engine", Order = 3)]
        public int StaleBarsThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Kill Zone Width (ticks)", GroupName = "10 Fib Engine", Order = 4)]
        public int KillZoneTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Zone Display Width (bars)", GroupName = "10 Fib Engine", Order = 5)]
        public int ZoneDisplayBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw Fib Lines", GroupName = "11 Fib Engine - Display", Order = 1)]
        public bool DrawFibLines { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw Golden Zone (50-61.8%)", GroupName = "11 Fib Engine - Display", Order = 2)]
        public bool DrawGoldenZone { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw Golden Pocket (61.8-65%)", GroupName = "11 Fib Engine - Display", Order = 3)]
        public bool DrawGoldenPocket { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw Kill Zones", GroupName = "11 Fib Engine - Display", Order = 4)]
        public bool DrawKillZones { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw Exhaustion Zone", GroupName = "11 Fib Engine - Display", Order = 5)]
        public bool DrawExhaustionZone { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw Flip Alerts", GroupName = "11 Fib Engine - Display", Order = 6)]
        public bool DrawFlipAlerts { get; set; }
        #endregion

        #region Properties -- Volatility Shield / Smart Trader
        [NinjaScriptProperty]
        [Display(Name = "Enable Volatility Shield", GroupName = "12 Volatility Shield", Order = 1, Description = "Gates the entire signal chain on TradingAllowed -- when the current volatility regime isn't allowed under Smart Trader's Mode, nothing fires this bar.")]
        public bool EnableVolatilityShield { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "VR ATR Length", GroupName = "12 Volatility Shield", Order = 2)]
        public int VrAtrLength { get; set; }

        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "VR Baseline Length (bars)", GroupName = "12 Volatility Shield", Order = 3)]
        public int VrBaselineLength { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, double.MaxValue)]
        [Display(Name = "Yellow Threshold (VR below = quiet)", GroupName = "13 Volatility Shield - Thresholds", Order = 1)]
        public double YellowLowThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, double.MaxValue)]
        [Display(Name = "Red Threshold (VR above = elevated)", GroupName = "13 Volatility Shield - Thresholds", Order = 2)]
        public double RedHighThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, double.MaxValue)]
        [Display(Name = "Dark Red Threshold (VR above = extreme)", GroupName = "13 Volatility Shield - Thresholds", Order = 3)]
        public double DarkRedThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Green: Min Stop (x ATR)", GroupName = "14 Volatility Shield - Stop/Target", Order = 1)]
        public double GreenMinStopAtrMult { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Green: Min Target (x ATR)", GroupName = "14 Volatility Shield - Stop/Target", Order = 2)]
        public double GreenMinTargetAtrMult { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Yellow: Min Stop (x ATR)", GroupName = "14 Volatility Shield - Stop/Target", Order = 3)]
        public double YellowMinStopAtrMult { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Yellow: Min Target (x ATR)", GroupName = "14 Volatility Shield - Stop/Target", Order = 4)]
        public double YellowMinTargetAtrMult { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Red: Min Stop (x ATR)", GroupName = "14 Volatility Shield - Stop/Target", Order = 5)]
        public double RedMinStopAtrMult { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Red: Min Target (x ATR)", GroupName = "14 Volatility Shield - Stop/Target", Order = 6)]
        public double RedMinTargetAtrMult { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Dark Red: Min Stop (x ATR)", GroupName = "14 Volatility Shield - Stop/Target", Order = 7)]
        public double DarkRedMinStopAtrMult { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Dark Red: Min Target (x ATR)", GroupName = "14 Volatility Shield - Stop/Target", Order = 8)]
        public double DarkRedMinTargetAtrMult { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Smart Trader", GroupName = "15 Smart Trader", Order = 1)]
        public bool EnableSmartTrader { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Smart Trader Mode", GroupName = "15 Smart Trader", Order = 2)]
        public PraedoxSmartTraderMode Mode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw Volatility State Label", GroupName = "16 Display", Order = 1)]
        public bool DrawVolShieldLabel { get; set; }
        #endregion

        #region Properties -- Trade Protector / Daily P&L Limits
        [NinjaScriptProperty]
        [Display(Name = "Enable Trade Protector", GroupName = "17 Trade Protector", Order = 1)]
        public bool EnableTradeProtector { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max Consecutive Wins", GroupName = "17 Trade Protector", Order = 2, Description = "Stops trading once this many winning trades in a row occur.")]
        public int MaxConsecutiveWins { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max Consecutive Losses", GroupName = "17 Trade Protector", Order = 3, Description = "Stops trading once this many losing trades in a row occur.")]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Daily P&L Limits", GroupName = "18 Daily P&L Limits", Order = 1)]
        public bool EnableDailyPnLLimits { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Max Daily Profit ($)", GroupName = "18 Daily P&L Limits", Order = 2)]
        public double MaxDailyProfitDollars { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Max Daily Loss ($)", GroupName = "18 Daily P&L Limits", Order = 3, Description = "Enter as a positive number.")]
        public double MaxDailyLossDollars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Auto-Flatten On Breach", GroupName = "19 Trade Protector - Enforcement", Order = 1, Description = "When a limit is hit, immediately flatten every open position on this account (all instruments) and cancel their working orders.")]
        public bool AutoFlatten { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Trade Protector Panel", GroupName = "16 Display", Order = 2)]
        public bool ShowProtectorPanel { get; set; }
        #endregion

        #region Properties -- Smart Filters
        [NinjaScriptProperty]
        [Display(Name = "Enable: EMA Macro Trend Filter", GroupName = "20 Smart Filters", Order = 1, Description = "Only allows a signal to fire in the direction aligned with price vs. a longer-term EMA. Off by default -- most of the 10 signals already have their own trend logic; this adds a broader, stricter macro filter on top.")]
        public bool EnableMacroEmaFilter { get; set; }

        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "Macro EMA Length", GroupName = "20 Smart Filters", Order = 2)]
        public int MacroEmaLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: Large-Bar Filter", GroupName = "20 Smart Filters", Order = 3, Description = "Suppresses a signal firing on an abnormally large bar (news spike/gap) relative to ATR. Slingshot and Breakout are exempt -- they specifically require an expansion bar by design.")]
        public bool EnableLargeBarFilter { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, double.MaxValue)]
        [Display(Name = "Large Bar Threshold (x ATR)", GroupName = "20 Smart Filters", Order = 4)]
        public double LargeBarAtrMultiple { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: S/R Proximity Filter", GroupName = "20 Smart Filters", Order = 5, Description = "Suppresses trend/momentum-continuation signals (Trend Cycle Wave, CrossFire, Continuation, EMA Breakout, Aggressive EMA, Volume Aggressive) when price sits right at an untested structural high/low -- not applied to Reversal, Zone Break, Breakout, or Slingshot, whose theses depend on trading at/through such a level.")]
        public bool EnableSrProximityFilter { get; set; }

        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "S/R Lookback (bars)", GroupName = "20 Smart Filters", Order = 6)]
        public int SrLookback { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "S/R Proximity Tolerance (ticks)", GroupName = "20 Smart Filters", Order = 7)]
        public int SrProximityTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable: Session-Time Filter", GroupName = "20 Smart Filters", Order = 8, Description = "Only allows signals to fire between Session Start/End (instrument's chart timezone). Handles an overnight window (start later than end) by wrapping past midnight.")]
        public bool EnableSessionTimeFilter { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "Session Start Time", GroupName = "20 Smart Filters", Order = 9)]
        public TimeSpan SessionStartTime { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "Session End Time", GroupName = "20 Smart Filters", Order = 10)]
        public TimeSpan SessionEndTime { get; set; }
        #endregion

        #region Properties -- Order Execution
        [NinjaScriptProperty]
        [Display(Name = "*** ENABLE AUTO TRADING ***", GroupName = "20 Order Execution", Order = 1, Description = "PLACES REAL ORDERS WHEN ON. Defaults OFF -- turn on deliberately, per instance, after testing. A qualifying signal (subject to all suppression/gating above) submits a Managed entry with a stop-loss and profit-target attached, sized from Volatility Shield's current min stop/target recommendation. Only enters when flat -- does not add to or reverse an existing position.")]
        public bool EnableAutoTrading { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Contracts", GroupName = "20 Order Execution", Order = 2)]
        public int Contracts { get; set; }
        #endregion

        #region Properties -- FibGuard Trade Manager
        [NinjaScriptProperty]
        [Display(Name = "Enable FibGuard Trade Manager", GroupName = "21 FibGuard Trade Manager", Order = 1, Description = "Layers a fib-level trailing stop on top of the ATR-based stop/target above -- see FIBGUARD in the file header. Off by default.")]
        public bool EnableFibGuard { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Min Advance (ticks)", GroupName = "21 FibGuard Trade Manager", Order = 2, Description = "How many ticks beyond a fib level price must clear before that level counts as \"reached\" and the stop ratchets to it -- keeps the stop from landing right on top of current price.")]
        public int FibGuardMinAdvanceTicks { get; set; }
        #endregion

        #region Properties -- Cockpit ATM Slots
        [NinjaScriptProperty]
        [Display(Name = "Enable Cockpit ATM", GroupName = "22 Cockpit ATM Slots", Order = 1, Description = "When on, the active ATM slot's stop/target/trail replace the Volatility-Shield-derived sizing for auto-signal entries, and govern trailing for manual entries too. Mutually exclusive with EnableFibGuard -- see SECTION 8 header comment.")]
        public bool EnableCockpitAtm { get; set; }

        [NinjaScriptProperty]
        [Range(1, 4)]
        [Display(Name = "Active ATM Slot (startup)", GroupName = "22 Cockpit ATM Slots", Order = 2, Description = "Which of the 4 slots is active when the strategy starts -- can be changed live via the Cockpit panel's ATM 1-4 buttons without recompiling.")]
        public int ActiveAtmSlot { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Slot 1: Stop (ticks)", GroupName = "22 Cockpit ATM Slots", Order = 10)]
        public int Atm1StopTicks { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Slot 1: Target (ticks)", GroupName = "22 Cockpit ATM Slots", Order = 11)]
        public int Atm1TargetTicks { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Slot 1: Trail Method", GroupName = "22 Cockpit ATM Slots", Order = 12)]
        public PraedoxTrailMethod Atm1TrailMethod { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Slot 1: Trail Param", GroupName = "22 Cockpit ATM Slots", Order = 13, Description = "Meaning depends on Trail Method: ATR multiple (AtrTrail), ticks (FixedTicksTrail), min-advance ticks (FibLevelTrail), or lookback bars (SwingPointTrail).")]
        public double Atm1TrailParam { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Slot 2: Stop (ticks)", GroupName = "22 Cockpit ATM Slots", Order = 20)]
        public int Atm2StopTicks { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Slot 2: Target (ticks)", GroupName = "22 Cockpit ATM Slots", Order = 21)]
        public int Atm2TargetTicks { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Slot 2: Trail Method", GroupName = "22 Cockpit ATM Slots", Order = 22)]
        public PraedoxTrailMethod Atm2TrailMethod { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Slot 2: Trail Param", GroupName = "22 Cockpit ATM Slots", Order = 23)]
        public double Atm2TrailParam { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Slot 3: Stop (ticks)", GroupName = "22 Cockpit ATM Slots", Order = 30)]
        public int Atm3StopTicks { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Slot 3: Target (ticks)", GroupName = "22 Cockpit ATM Slots", Order = 31)]
        public int Atm3TargetTicks { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Slot 3: Trail Method", GroupName = "22 Cockpit ATM Slots", Order = 32)]
        public PraedoxTrailMethod Atm3TrailMethod { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Slot 3: Trail Param", GroupName = "22 Cockpit ATM Slots", Order = 33)]
        public double Atm3TrailParam { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Slot 4: Stop (ticks)", GroupName = "22 Cockpit ATM Slots", Order = 40)]
        public int Atm4StopTicks { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Slot 4: Target (ticks)", GroupName = "22 Cockpit ATM Slots", Order = 41)]
        public int Atm4TargetTicks { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Slot 4: Trail Method", GroupName = "22 Cockpit ATM Slots", Order = 42)]
        public PraedoxTrailMethod Atm4TrailMethod { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Slot 4: Trail Param", GroupName = "22 Cockpit ATM Slots", Order = 43)]
        public double Atm4TrailParam { get; set; }
        #endregion

        #region Properties -- Cockpit Manual Entry
        [NinjaScriptProperty]
        [Display(Name = "Show Cockpit Panel", GroupName = "23 Cockpit Manual Entry", Order = 1, Description = "On-chart panel: ATM slot buttons, active-slot readout, Qty/SL/TP fields, Buy/Sell/Flatten. Off hides the panel entirely (ATM slots and auto-signal trading still work without it).")]
        public bool ShowCockpitPanel { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Manual Default Qty", GroupName = "23 Cockpit Manual Entry", Order = 2, Description = "Starting value in the panel's Qty box -- editable live on the chart before clicking Buy/Sell.")]
        public int ManualDefaultQty { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Manual Default Stop (ticks)", GroupName = "23 Cockpit Manual Entry", Order = 3)]
        public int ManualDefaultStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Manual Default Target (ticks)", GroupName = "23 Cockpit Manual Entry", Order = 4)]
        public int ManualDefaultTargetTicks { get; set; }
        #endregion

        #region Properties -- Cockpit Dashboard
        [NinjaScriptProperty]
        [Display(Name = "Raw Signal Mode", GroupName = "25 Cockpit Dashboard", Order = 1, Description = "When on, a firing signal skips the Smart Filters pass entirely (macro-EMA / large-bar / S-R-proximity / session-time). Toggleable live via the panel's RAW SIGNAL MODE button. Cooldown, Volatility Shield, and Trade Protector still apply -- this only bypasses signal-quality filtering. Off by default.")]
        public bool RawSignalMode { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Quick Nudge (ticks)", GroupName = "25 Cockpit Dashboard", Order = 2, Description = "How many ticks the panel's Stop -N/+N and Tgt -N/+N buttons move the live stop/target distance-from-entry per click.")]
        public int QuickNudgeTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "Quick ATR Multiple", GroupName = "25 Cockpit Dashboard", Order = 3, Description = "ATR multiple the panel's STOP ATR / TGT ATR buttons use to set the live stop/target that many ATRs from the current close.")]
        public double QuickAtrMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Quick ATR Min (ticks)", GroupName = "25 Cockpit Dashboard", Order = 4, Description = "Floor, in ticks, under the ATR-scaled distance from STOP ATR / TGT ATR -- keeps a very quiet-ATR moment from placing the stop unreasonably close to price.")]
        public int QuickAtrMinTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Move-To-BE Step (ticks)", GroupName = "25 Cockpit Dashboard", Order = 5, Description = "Increment the panel's Move-To-BE -/+ stepper adjusts the locked-in-profit offset by, per click.")]
        public int MoveToBeStepTicks { get; set; }
        #endregion

        #region Properties -- Session Report
        [NinjaScriptProperty]
        [Display(Name = "Enable Session Report", GroupName = "26 Session Report", Order = 1, Description = "Writes an HTML summary (win rate, P&L, per-trade table) of the CURRENT session's closed trades to Session Report Path. Auto-generated for the session that just ended when a new session's first bar arrives, and as a final safety-net flush when the strategy stops -- plus on demand any time via the Cockpit panel's REPORT button. Overwrites the same file each time rather than dating a new one per session -- see SESSION REPORT in the file header.")]
        public bool EnableSessionReport { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session Report Path", GroupName = "26 Session Report", Order = 2, Description = "Full file path the HTML report is written to (overwritten each time). Defaults under NinjaTrader's own user data folder.")]
        public string SessionReportPath { get; set; }
        #endregion

        #region Properties -- Cockpit Sound Alerts
        [NinjaScriptProperty]
        [Display(Name = "Alert: Slingshot", GroupName = "24 Cockpit Sound Alerts", Order = 1)]
        public bool EnableAlertSlingshot { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Alert: Slingshot Sound", GroupName = "24 Cockpit Sound Alerts", Order = 2)]
        public string AlertSlingshotSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: Zone Break", GroupName = "24 Cockpit Sound Alerts", Order = 3)]
        public bool EnableAlertZoneBreak { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Alert: Zone Break Sound", GroupName = "24 Cockpit Sound Alerts", Order = 4)]
        public string AlertZoneBreakSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: Trend Cycle Wave", GroupName = "24 Cockpit Sound Alerts", Order = 5)]
        public bool EnableAlertTrendCycleWave { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Alert: Trend Cycle Wave Sound", GroupName = "24 Cockpit Sound Alerts", Order = 6)]
        public string AlertTrendCycleWaveSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: CrossFire", GroupName = "24 Cockpit Sound Alerts", Order = 7)]
        public bool EnableAlertCrossFire { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Alert: CrossFire Sound", GroupName = "24 Cockpit Sound Alerts", Order = 8)]
        public string AlertCrossFireSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: Reversal", GroupName = "24 Cockpit Sound Alerts", Order = 9)]
        public bool EnableAlertReversal { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Alert: Reversal Sound", GroupName = "24 Cockpit Sound Alerts", Order = 10)]
        public string AlertReversalSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: Continuation", GroupName = "24 Cockpit Sound Alerts", Order = 11)]
        public bool EnableAlertContinuation { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Alert: Continuation Sound", GroupName = "24 Cockpit Sound Alerts", Order = 12)]
        public string AlertContinuationSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: EMA Breakout", GroupName = "24 Cockpit Sound Alerts", Order = 13)]
        public bool EnableAlertEmaBreakout { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Alert: EMA Breakout Sound", GroupName = "24 Cockpit Sound Alerts", Order = 14)]
        public string AlertEmaBreakoutSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: Aggressive EMA", GroupName = "24 Cockpit Sound Alerts", Order = 15)]
        public bool EnableAlertAggressiveEma { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Alert: Aggressive EMA Sound", GroupName = "24 Cockpit Sound Alerts", Order = 16)]
        public string AlertAggressiveEmaSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: Volume Aggressive", GroupName = "24 Cockpit Sound Alerts", Order = 17)]
        public bool EnableAlertVolumeAggressive { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Alert: Volume Aggressive Sound", GroupName = "24 Cockpit Sound Alerts", Order = 18)]
        public string AlertVolumeAggressiveSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: Breakout", GroupName = "24 Cockpit Sound Alerts", Order = 19)]
        public bool EnableAlertBreakout { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Alert: Breakout Sound", GroupName = "24 Cockpit Sound Alerts", Order = 20)]
        public string AlertBreakoutSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: Vol Shield -> Elevated", GroupName = "24 Cockpit Sound Alerts", Order = 21)]
        public bool EnableAlertVolShieldRed { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Alert: Vol Shield -> Elevated Sound", GroupName = "24 Cockpit Sound Alerts", Order = 22)]
        public string AlertVolShieldRedSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: Vol Shield -> Extreme", GroupName = "24 Cockpit Sound Alerts", Order = 23)]
        public bool EnableAlertVolShieldDarkRed { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Alert: Vol Shield -> Extreme Sound", GroupName = "24 Cockpit Sound Alerts", Order = 24)]
        public string AlertVolShieldDarkRedSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: Win Streak Limit", GroupName = "24 Cockpit Sound Alerts", Order = 25)]
        public bool EnableAlertTpWinStreak { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Alert: Win Streak Limit Sound", GroupName = "24 Cockpit Sound Alerts", Order = 26)]
        public string AlertTpWinStreakSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: Loss Streak Limit", GroupName = "24 Cockpit Sound Alerts", Order = 27)]
        public bool EnableAlertTpLossStreak { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Alert: Loss Streak Limit Sound", GroupName = "24 Cockpit Sound Alerts", Order = 28)]
        public string AlertTpLossStreakSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: Max Daily Profit", GroupName = "24 Cockpit Sound Alerts", Order = 29)]
        public bool EnableAlertTpMaxProfit { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Alert: Max Daily Profit Sound", GroupName = "24 Cockpit Sound Alerts", Order = 30)]
        public string AlertTpMaxProfitSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: Max Daily Loss", GroupName = "24 Cockpit Sound Alerts", Order = 31)]
        public bool EnableAlertTpMaxLoss { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Alert: Max Daily Loss Sound", GroupName = "24 Cockpit Sound Alerts", Order = 32)]
        public string AlertTpMaxLossSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: Fib Flip", GroupName = "24 Cockpit Sound Alerts", Order = 33)]
        public bool EnableAlertFibFlip { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Alert: Fib Flip Sound", GroupName = "24 Cockpit Sound Alerts", Order = 34)]
        public string AlertFibFlipSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: Order Filled", GroupName = "24 Cockpit Sound Alerts", Order = 35)]
        public bool EnableAlertOrderFilled { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Alert: Order Filled Sound", GroupName = "24 Cockpit Sound Alerts", Order = 36)]
        public string AlertOrderFilledSound { get; set; }
        #endregion

        #region Properties -- Display
        [NinjaScriptProperty]
        [Display(Name = "Show Status Overlay", GroupName = "16 Display", Order = 3, Description = "Fixed top-left on-chart text readout: volatility ratio/state, fib kill-zone/exhaustion suppression flags, ATR, fib anchor price.")]
        public bool ShowStatusOverlay { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw Signal Labels", GroupName = "16 Display", Order = 4)]
        public bool DrawLabels { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Label Declutter Window (bars)", GroupName = "16 Display", Order = 5)]
        public int LabelDeclutterBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, double.MaxValue)]
        [Display(Name = "Label Offset (x ATR)", GroupName = "16 Display", Order = 6)]
        public double LabelOffsetAtrMultiple { get; set; }
        #endregion

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
    }
}
