// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
//
// Copyright (c) 2026 silentsudo-io and the Sentinel Suite contributors.
// ─────────────────────────────────────────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════
//  SneakyBridge — the automated Council/Confluence-consumer + on-chart control surface (Sentinel Suite, NT8)
//  File: SneakyBridge_v0_2_0.cs   ·   Version v0.4.0   ·   namespace …Strategies (BASE — sub-ns hides strategies)
// ─────────────────────────────────────────────────────────────────────────────
//  WHAT  (see Docs/BRIDGE_SPEC.md — Phase 2, the control surface)
//    The autopilot counterpart to the manual Deck. Trades a published seam verdict — Council (fused
//    multi-voter bias/conviction/size) OR, as of v0.4.0, Confluence (gate-cascade trigger fire) — picked
//    per-chart via the new "Signal source" property — through the authoritative GateEntry (fail-CLOSED),
//    managed bracket, and RECORDS the verdict on every fire so Lens can grade the weights. v0.2.0 adds the
//    ON-CHART CONTROL SURFACE: a Sentinel glass card (live verdict + strategy state) with a clickable
//    **ARM BRIDGE** button.
//
//    SAFETY — automation is OFF on load and can ONLY be armed by a deliberate CLICK on the card's ARM
//    BRIDGE button (never silently auto-arm — the Deck lesson; the armed flag is runtime-only, never
//    persisted). DISARMED = the card shows the live verdict but the engine places NO orders (behaves like
//    the headless v0.1.0). ARMED = it fires per the P1 logic. No chart (headless) = no button = stays
//    disarmed = safe no-trade (this is a CHART trader by design).
//
//    Engine (unchanged from v0.1.0 for Council; see v0.4.0 below for what Confluence maps to): edge-detected
//    bias flip into an aligned edge (one-shot per verdict episode, flat-only), size baseQty × SizeMult,
//    GateEntry (enter only on IsClear), managed bracket (SetStopLoss/SetProfitTarget in ticks), record
//    verdict (Ledger.Order + "bridge-fire" Action). v0.2.0 adds an optional flip EXIT (close if the source
//    stops supporting the open side). TP/SL hand-set from the COUNCIL ◆ in the Excursion tab (still
//    Council-derived regardless of Signal source — see v0.4.0 note). MANAGED (never close a managed
//    position with raw orders — ExitLong/ExitShort is the managed-safe close).
//
//  CHANGELOG
//    v0.4.0 (in-place, 2026-08-21) — CONFLUENCE AS AN ALTERNATE SIGNAL SOURCE. New "Signal source" property
//             (Council / Confluence, default Council — existing charts keep TODAY'S behaviour byte-for-byte
//             unchanged) picks which published seam the engine trades. The two states are NOT the same shape
//             and this is not glossed over:
//               • Council publishes a HELD verdict (Bias/Conviction/SizeMult persist bar-to-bar until it
//                 flips). Confluence publishes a DEBOUNCED FIRE (Signal is 0 every bar except the exact bar a
//                 trigger fires — the same one-shot pulse the "SSCB"/"SSCS" chart tags and Custom Signals
//                 automation already key off) plus a separate, persistent trend-context Bias field.
//               • Confluence has NO conviction/size number. Every Confluence-sourced entry sizes at the full
//                 Base contracts (or full $-risk size) — MinConviction is therefore a no-op under Confluence
//                 (there is nothing for it to floor).
//               • "Exit on flip" (renamed from "Exit on Council flip") checks Confluence's persistent trend
//                 Bias field for "does the source still support this side" — NOT the momentary Signal pulse,
//                 which would otherwise read every non-firing bar as "flipped away" and exit almost instantly.
//               • "Use Sneaky config (COUNCIL ◆)" is UNCHANGED and still reads the Council-derived
//                 `<inst>_COUNCIL_<dir>.conf` file regardless of Signal source — there is no Confluence-specific
//                 lab config yet, so picking Confluence for entries while this is checked still brackets with
//                 Council's tuned TP/SL numbers.
//             Internally: a new private `Verdict` adapter normalizes CouncilState/ConfluenceState into the one
//             shape TryEnter/the exit checks/the card need, so neither has to know which source produced it.
//             No ctor churn (SignalSource is [Display]-only, same precedent as ScopeLane/ObeyHelm) — saved
//             chart instances keep loading and keep trading Council exactly as before unless changed by hand.
//    v0.3.1 (in-place, INSTRUMENTATION 2026-07-28) — THE FILL-COST REFERENCE PRICE. Measurement only: the order
//             path, sizing, gate, bracket and Helm logic are byte-for-byte v0.3.0. Fixes the reason Question 1
//             ("what does a fill actually cost us?") could not be answered from the corpus we already own.
//             TWO defects, both of which recorded a number that LOOKED like a measurement and was not:
//               (1) `OnExecutionUpdate` set `intended = price` for a MARKET order, so `slip = fill − fill = 0`
//                   IDENTICALLY. All 39 market entries in the Ledger read slip 0 — an artifact of this line, not
//                   an observation of the market. Entry crossing cost had never once been measured.
//               (2) `RecordFire` logged `Close[0]` as the order's decision price. On a Heikin-Ashi bar type
//                   (SneakyTBars, which is what the Bridge runs on) Close[0] is the (O+H+L+C)/4 AVERAGE — a
//                   price that NEVER TRADED. Joining order→fill over the 39 live entries showed the signature
//                   exactly: longs "filled" a median +11.0 ticks above and shorts −11.0 ticks below the recorded
//                   price, symmetric by direction. Same defect, same magnitude as the excursion recorder's
//                   "9-tick bleed" (memory firepx-is-synthetic-ha-close), fixed there at schema 1.5 and still
//                   live here. So BOTH the fill row and the order row were unusable, for two different reasons.
//             FIX: capture the TRADEABLE quote on the side being crossed (buy → ASK, sell → BID) at the moment
//             of SUBMISSION, at all five market-order sites (entry · council-flip exit · Helm flatten · Helm
//             scale-down ×2), and use it as the reference for both the Ledger order row and `intended`.
//             Stop/limit exits are UNCHANGED — their `intended` was already the real trigger/limit price and is
//             the one honest execution number the Ledger has ever held (prop accounts: median +1.0 tick adverse).
//             Fails soft: no quote ⇒ 0 ⇒ falls back to the v0.3.0 behaviour rather than record an indefensible
//             number. Historical/replay returns the bar close, so the measurement is meaningful REALTIME only.
//             ⚠ FORWARD-MEASURING ONLY — this recovers nothing about past fills; those 39 entries stay unusable.
//             ⚠ KNOWN, DELIBERATELY NOT CHANGED: the `_liveStopPrice`/`_liveTargetPrice` seeds in TryEnter still
//             use Close[0] and so inherit the same HA offset. They are transient (recomputed exactly from
//             Position.AveragePrice once filled) and feed only the card + Helm's tighten/widen classify, so
//             correcting them would alter interdiction behaviour — out of scope for an instrumentation bump.
//    (in-place, 2026-07-25) — RECORDED CATCHES: 28 empty `catch {}` migrated to SneakyCore.Swallow
//             (Core >= v1.41.0). Runtime behaviour is IDENTICAL — Swallow never rethrows — but a fault in
//             the order path is now counted, rate-limited and logged instead of vanishing. Silent catches
//             are the proven mechanism of every expensive bug in this suite; see Docs/SENTINEL_ADVERSARIAL_REVIEW.md §2.
//    v0.3.0 (in-place, 2026-07-15) — HELM INTERDICTION CONSUMER (Phase 5; SentinelCore ≥ v1.34.0; memory
//             helm-interdiction-layer). The Bridge is now the FIRST owner that obeys Helm intents — a human can grab
//             the wheel of this running autopilot WITHOUT stopping it. It drains SneakyCore.TakeHelmIntent(InstanceKey())
//             on every tick (new OnMarketData, realtime) + a bar-close backstop, executes the intent with its OWN order
//             handles (never a raw order), publishes HelmState back so a Helm surface renders reality, and writes EVERY
//             intent to the Ledger ("helm-intent" Action, stamped with the Council EpisodeId + instanceKey) + marks the
//             episode HumanOverride so the Lab can exclude/model interdicted trades (recording the human keeps the model
//             honest). ASYMMETRIC GATE: risk-REDUCING verbs (FlattenNow/Pause/SkipNext/BreakevenNow/tighten-stop/
//             scale-down) are fail-OPEN; risk-ADDING (Resume/widen-stop/HandBack) validate through GateEntry fail-CLOSED.
//             MANAGED-MODE HONESTY: Scale-UP is REFUSED (single-entry managed can't scale-in without desync — use the
//             Deck); TakeOver/HandBack map to stand-down/resume (a managed position can't transfer order ownership
//             without disable/re-enable). New [Display]-only "Obey Helm intents" toggle (default ON). No ctor churn, no
//             change to the entry engine when no intent is pending ⇒ byte-for-byte the v0.2.4 trade path. Keeps the
//             v0_2_0 class/file identity (no serialization break for the live chart instance).
//    v0.2.4 (in-place, 2026-07-14) — PER-CHART LANE (Core ≥ v1.32.0). New [Display]-only "Scope Lane" param: set it to
//             match the "Scope Lane" on THIS chart's Council so the Bridge reads the correct lane's CouncilState
//             (via ScopeOfLane — a strategy has no shared ChartControl, so it targets the lane explicitly). Blank =
//             bare scope (back-compat). Lets the Bridge auto-trade one of two same-bartype A/B test lanes without
//             consuming the other's brain. No ctor churn (not [NinjaScriptProperty]).
//    v0.2.3 (in-place, IDENTITY 2026-07-11) — ACTOR IDENTITY + EPISODE JOIN (ML spec §10; SentinelCore ≥ v1.25.0).
//             (1) Derived `InstanceKey()` = "SneakyBridge#<scope>@<account>". (2) The ARM button is now an INTERLOCK:
//             it `RegisterActor`s the key and REFUSES to arm on a collision ("NAME TAKEN") — two live Bridges on one
//             scope+account is the managed-position desync hazard, so the ambiguous config is blocked, not merely
//             warned. Released (reference-checked) on disarm + Terminated. (3) Every fire now stamps the Council's
//             `EpisodeId` + the instanceKey onto the Ledger Order/Action/Fill, so Lens can join a FILL → its EPISODE →
//             the verdict that caused it. Behaviour of the trade path itself is UNCHANGED (sizing/gate/bracket as v0.2.2).
//    v0.2.2 (in-place, SAFETY 2026-07-10) — SIZING ROUTES THROUGH `SneakyCore.SizedQuantity()`. The Bridge stopped
//             at `Math.Max(1, baseQty × SizeMult)` and never called it, so THREE things were silently ignored:
//               • the account profile's `size=` (SizeScale) from Profiles.conf;
//               • the governor's `RecommendedSize()` — a governor telling this strategy to size DOWN was NOT OBEYED;
//               • `ContractLimit`, which hard-BLOCKED every entry when BaseContracts exceeded it, instead of clamping.
//             Now: baseQty → × SizeMult → SizedQuantity (SizeScale × RecommendedSize, clamped to ContractLimit,
//             never < 1) → GateEntry(riskDollars=0) VALIDATES. That is the order SentinelCore's own header
//             prescribes — "SizedQuantity is the one place sizing math lives." A resize is logged.
//             ⚠ RESOLUTION CAVEAT, now documented at the call site: with BaseContracts = 1 the Council's SizeMult
//             CANNOT scale a position down — 1 × 0.19 rounds to 0 and the Max(1,…) floor restores a 1-lot, because
//             a fifth of a contract does not exist. SizeMult only has resolution at BaseContracts ≥ 2 (≥ 4 to
//             resolve its typical 0.2–1.0 range). ConvictionFloor (SizeMult = 0) is what expresses "do not trade";
//             SizeMult is not a substitute for it. Pair this with Council v1.2.1's floor of 0.20.
//    v0.2.1 (in-place, SAFETY 2026-07-09) — the card is now `pinned: true` in CardLayout.Place. It carries the
//             ARM BRIDGE button (`_armBtn` is hit-tested against this rect), so if a crowded column overflowed and
//             the card were hidden, the ONLY on-chart arm/disarm control for an order-placing strategy would
//             silently vanish. CardLayout never hides a pinned card. No order-logic change.
//    v0.2.1 (in-place, COSMETIC 2026-07-09) — LABEL REMOVER. NT drew this strategy's top-left chart label from
//             Name, dumping the whole parameter list over the chart. Adopted the suite's indicator standard:
//             new "Show strategy label" toggle (default OFF) blanks Name in State.DataLoaded. Order routing is
//             UNAFFECTED — order tags come from the separate `_tag` const, never from Name. The toggle is
//             [Display]-only (not [NinjaScriptProperty]) so the generated-region ctor signature is unchanged and
//             saved workspaces keep loading. ⚠ If the Control Center Strategies grid ever shows a blank row,
//             flip it ON — you must always be able to find and disable a running strategy.
//    v0.2.1 (in-place, additive 2026-07-08) — GOD REVERSAL ENTRY TRIGGER (opt-in, default OFF; the Council-bias ×
//             reversal-trigger loop from the God Reversal doctrine §7). When UseGodReversalTrigger is ON, an entry
//             also requires a FRESH SneakyCore.GodReversalState (≤ GrevMaxAgeSec) whose held Dir is ALIGNED with
//             the Council bias and whose Quality ≥ GrevMinQuality — the Council supplies bias/edge/size, the reversal
//             supplies the entry TIMING (the doctrine's "reversal at a predictable place"). The trigger read (dir/
//             setup/quality) is appended to the Ledger fire record + sentinel.log so Lens can grade the reversal
//             weight, and shows on the card when armed+flat. Default OFF ⇒ byte-for-byte the v0.2.0 engine. Keeps
//             the v0_2_0 class/file identity (no serialization break for the live chart instance) — Council-GREV
//             in-place-additive precedent. Needs Sentinel God Reversal on the chart + SentinelCore ≥ v1.14.0.
//             Also (observability): the fire record now carries the BRACKET (tp=/sl= ticks) so a grader can
//             classify each trade TP-hit / SL-hit / flatten by comparing the exit fill to entry ± the bracket.
//             HONEST STALE MESSAGE (in-place 2026-07-08): the card now distinguishes a genuinely ABSENT Council
//             ("add the Council to this chart") from a STALE one ("Council verdict STALE — no fresh read for Ns —
//             slow bars"). Pairs with the Council's OnMarketData heartbeat: the old flat "no verdict" was misleading
//             in dry-up markets where the Council IS present but its published state aged past StaleSec.
//    v0.2.0 — on-chart glass card + clickable ARM BRIDGE button (ChartControl mouse hit-test); arming gates
//             all firing; optional ExitOnCouncilFlip; ShowCard/CardCorner. Supersedes v0.1.0 (archived).
//             FIXES (post first live render): (a) the card reads CouncilState LIVE every render (was bar-close only
//             → "no verdict" on slow bars); (b) gate on SizeMult>0 (SizeMult=0 = Council floor/veto = no trade),
//             else min a 1-lot; (c) pass riskDollars=0 to GateEntry — the Bridge sizes itself, the Gate only
//             VALIDATES; passing RiskDollars flipped it into risk-sizing → "risk too small for a 1-lot" Advisory →
//             fail-closed block (the first live test never fired). Base `…Strategies` ns (sub-ns hid the strategy).
//             FEATURE — .conf AUTO-READ: UseSneakyConfig reads <inst>_COUNCIL_<Long|Short>.conf (written by the
//             Excursion tab's Apply ◆, fed by SentinelExcursionRecorder_v1_4) for per-direction TP/SL, overriding
//             the manual ticks (re-reads on enable/recompile). Closes the lab → Bridge "real numbers" loop.
//    v0.1.0 — [archived] headless engine: consume CouncilState → GateEntry (fail-closed) → managed bracket
//             → record verdict on fire.
// ═════════════════════════════════════════════════════════════════════════════

using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows.Input;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.AddOns.Sneaky;

namespace NinjaTrader.NinjaScript.Strategies
{
    // NB: BASE Strategies namespace (NOT .Strategies.Sentinel). NT's Strategy selector does NOT surface
    // sub-namespaced strategies (only indicators fold into picker sub-folders) — a sub-namespaced strategy
    // compiles clean but never appears in the Strategies list. The "Sentinel" identity is carried by the
    // class-name prefix + display Name instead. (Federated naming law amended 2026-07-07; GTraderStrategy
    // stayed in base for the same reason.)

    /// <summary>v0.4.0 — which published seam the Bridge trades. See the file-header CHANGELOG entry for
    /// exactly what each source can and cannot supply (Confluence has no conviction/size number).</summary>
    public enum SneakyBridgeSource { Council, Confluence }

    public class SneakyBridge_v0_2_0 : Strategy
    {
        private const string _tag = "SneakyBridge";   // stable order-tag identity (decoupled from Name)
        private string _scope;                          // v1.15.0: "<inst>.<barTag>" — this chart's seam key

        /// <summary>This chart's Council scope. Lazily resolved (Instrument/BarsPeriod live from DataLoaded on),
        /// then cached. Null until configured — GetCouncilState(null) returns null, so the Bridge simply
        /// never fires, which is the correct fail-closed default.</summary>
        private string Scope()
        {
            // v0.2.4 — LANE-aware (Core ≥ v1.32.0). A strategy has no shared ChartControl with the chart's Council,
            // so it targets a lane EXPLICITLY: set ScopeLane to match the Council's "Scope Lane" on this chart.
            // Blank ⇒ bare scope (back-compat). This is how the Bridge reads the RIGHT lane's CouncilState when two
            // same-bartype charts run distinct Councils (GC.212202v6x24@A vs @B).
            if (_scope == null) { try { _scope = SneakyCore.ScopeOfLane(Instrument, BarsPeriod, ScopeLane); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.Scope", _sx); } }
            return _scope;
        }

        // v0.2.3 — this actor's IDENTITY (ML spec §10.9): <class>#<scope>@<account>. The delimiters '#' and '@' never
        // appear in a scope ('.') or a SIM-<LANE>-<SLOT> account, so the composite is unambiguous. Derived + cached;
        // it is the key the actor registry gates arming on and the 'instance' the Ledger records on every fire.
        private string _instanceKey;
        private string InstanceKey()
        {
            if (_instanceKey == null)
            {
                string scope = Scope();
                if (string.IsNullOrEmpty(scope))
                    scope = Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : "?";
                string acct = Account != null ? Account.Name : "?";
                _instanceKey = _tag + "#" + scope + "@" + acct;
            }
            return _instanceKey;
        }

        // ═══ v0.4.0 — SOURCE ADAPTER ═══════════════════════════════════════════════════════════════
        // CouncilState and ConfluenceState are genuinely different shapes (a held fused verdict vs. a
        // debounced gate-cascade fire), not two views of the same data. This adapter normalizes either into
        // the one shape the entry engine / exit checks / card actually need, so nothing downstream has to
        // know or care which source produced it. See the file-header v0.4.0 changelog entry for the exact
        // mapping and what each field means for each source.
        private sealed class Verdict
        {
            public bool   HasEdge;        // may we act on THIS bar (already gate/veto-aware for both sources)
            public int    EntryDir;       // the ONE-SHOT direction to act on this bar (0 = nothing to do NOW).
                                           // Council: the held Bias (same as SustainedBias). Confluence: the
                                           // debounced Signal pulse — 0 every bar except the exact firing bar.
            public int    SustainedBias;  // the ONGOING directional read, for the "does the source still
                                           // support this side" exit check. Council: the held Bias (identical
                                           // to EntryDir). Confluence: the persistent trend-context Bias field
                                           // (NOT the momentary Signal pulse — that would read "flipped away"
                                           // on every non-firing bar and exit almost immediately).
            public double Conviction;     // Council: 0..1 agreement. Confluence: pinned 1.0 (no such number).
            public double SizeMult;       // Council: 0..1 suggested size. Confluence: pinned 1.0 (full size).
            public int    Agree, Disagree, Voters;   // Confluence: always 0 (no voter panel)
            public bool   Vetoed;
            public string VetoReason, Reasons, EpisodeId;   // Confluence: EpisodeId always null (no concept)
            public string SourceTag;      // "Council" / "Confluence" — logs, Ledger detail, the card
        }

        private static Verdict FromCouncil(SneakyCore.CouncilState v)
        {
            if (v == null) return null;
            return new Verdict
            {
                HasEdge = v.HasEdge, EntryDir = v.Bias, SustainedBias = v.Bias,
                Conviction = v.Conviction, SizeMult = v.SizeMult,
                Agree = v.Agree, Disagree = v.Disagree, Voters = v.Voters,
                Vetoed = v.Vetoed, VetoReason = v.VetoReason, Reasons = v.Reasons,
                EpisodeId = v.EpisodeId, SourceTag = "Council"
            };
        }

        private static Verdict FromConfluence(SneakyCore.ConfluenceState v)
        {
            if (v == null) return null;
            // Signal is already the fully gate-cascaded, debounced one-shot fire (identical contract to the
            // on-chart "SSCB"/"SSCS" tags and the Custom Signals automation that already keys off this same
            // plot) — Fired is just (Signal != 0), so no extra gating is needed here. AllRequiredOk is the
            // closest Confluence equivalent to Council's Vetoed: a REQUIRED gate is currently failing.
            return new Verdict
            {
                HasEdge = v.Fired, EntryDir = v.Signal, SustainedBias = v.Bias,
                Conviction = 1.0, SizeMult = 1.0,
                Agree = 0, Disagree = 0, Voters = 0,
                Vetoed = !v.AllRequiredOk, VetoReason = v.BlockedBy, Reasons = v.TriggerLabel,
                EpisodeId = null, SourceTag = "Confluence"
            };
        }

        /// <summary>The live verdict for THIS chart's configured Signal source (Scope()-consulted, StaleSec-gated),
        /// normalized to the one shape the rest of the engine consumes. Null = absent or stale — the correct
        /// fail-closed "stand down" input either way.</summary>
        private Verdict CurrentVerdict()
        {
            return SignalSource == SneakyBridgeSource.Confluence
                ? FromConfluence(SneakyCore.GetConfluenceState(Scope(), StaleSec))
                : FromCouncil(SneakyCore.GetCouncilState(Scope(), StaleSec));
        }

        private int    _lastFiredBias;                   // one-shot per verdict episode (0 = none/flat)
        private int    _lastLoggedBlockBar = -1;
        private int    _cfgTpLong, _cfgSlLong, _cfgTpShort, _cfgSlShort;   // per-dir TP/SL from <inst>_COUNCIL_<dir>.conf (0 = not loaded)

        // — control surface (chart-only) —
        private volatile bool _armed;                    // runtime-only; NEVER persisted (no silent auto-arm)
        private SneakySkin.Painter _sp;
        private SharpDX.RectangleF   _armBtn;            // ARM button hit-rect (ChartControl coords)
        private bool   _hooked;
        private string _lastBlock = "";
        private string _armBlocked = "";                 // v0.2.3 — set when an arm click was refused (instanceKey collision)

        // — verdict snapshot for the card (written on the data thread, read on the render thread; cosmetic) —
        private bool   _sHasData, _sHasEdge, _sVetoed;
        private int    _sBias, _sVoters, _sAgree, _sDisagree;
        private double _sConv, _sSize, _lastClose;
        private string _sReasons = "", _sVetoReason = "";
        private string _sSource = "Council";              // v0.4.0 — SourceTag of the last snapshot, for the card/messages
        private int    _lastFireDir;                     // last fired side for the card (0 = none)
        private bool     _sEverSeen;                     // have we EVER read a CouncilState? (distinguish absent vs stale)
        private DateTime _sLastSeenUtc;                  // when we last read a FRESH verdict (for the stale-age readout)
        private double   _crossRefPx;                    // v0.3.1 — tradeable quote on the side we crossed, stamped at submission (0 = none)

        // — God Reversal entry-trigger snapshot (opt-in; written data thread, read render thread; cosmetic) —
        private bool   _sGrevFresh;
        private int    _sGrevDir;
        private double _sGrevQ;
        private string _sGrevSetup = "";

        // — Helm interdiction (v0.3.0) — a human grabs the wheel WITHOUT stopping the car. State set on the data
        //   thread (OnMarketData/OnBarUpdate drain), read on the render thread for the card (cosmetic). volatile on
        //   the two that gate the OnBarClose entry path.
        private volatile bool _paused;          // Helm Pause/TakeOver: no NEW entries (open position + bracket kept)
        private volatile bool _skipNext;        // Helm SkipNext: skip exactly the next entry, then auto-re-arm
        private bool     _humanOverride;        // a Helm intent moved something THIS episode → the sample is interdicted
        private double   _liveStopPrice;        // tracked managed stop price (card + MoveStop tighten/widen classify)
        private double   _liveTargetPrice;      // tracked managed target price (card)
        private string   _sEpisodeId = "";      // current Council episode id (stamped on every helm-intent Ledger row)
        private string   _lastIntentId = "";    // last consumed intent id (echoed in HelmState — idempotency proof)
        private string   _lastHelmMsg = "";     // last interdiction outcome, for the card + log
        private DateTime _lastHelmPubUtc;       // throttle HelmState publish off the per-tick path

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "Sneaky Bridge";
                Description = "Automated Council-consumer with an on-chart control surface: trades the fused "
                            + "CouncilState verdict through GateEntry (fail-closed), managed bracket, records "
                            + "the verdict on every fire (Lens grading). ARM via the on-chart button (off on "
                            + "load). Pairs with the manual Deck. Needs the Council on this chart + SentinelCore ≥ v1.7.0.";
                Calculate                     = Calculate.OnBarClose;
                EntriesPerDirection           = 1;
                EntryHandling                 = EntryHandling.AllEntries;
                IsUnmanaged                   = false;
                IsExitOnSessionCloseStrategy  = true;
                ExitOnSessionCloseSeconds     = 30;
                BarsRequiredToTrade           = 20;
                StartBehavior                 = StartBehavior.WaitUntilFlat;
                TimeInForce                   = TimeInForce.Gtc;

                BaseContracts     = 1;
                UseRiskSizing     = false;
                RiskDollars       = 200;
                ProfitTargetTicks = 40;
                StopLossTicks     = 30;
                UseSneakyConfig = false;
                StaleSec          = 90;
                MinConviction     = 0.0;
                ReverseOnFlip     = false;
                ExitOnCouncilFlip = false;
                UseGodReversalTrigger = false;
                GrevMaxAgeSec     = 90;
                GrevMinQuality    = 0.0;
                RecordVerdict     = true;
                LogChanges        = true;
                ShowCard          = true;
                CardCorner        = SneakyCardCorner.TopRight;
                ShowStrategyLabel = false;
                ScopeLane         = "";     // v0.2.4 — per-chart lane; blank = bare scope. Match the chart's Council/Confluence lane.
                ObeyHelm          = true;   // v0.3.0 — obey Helm interdiction intents (a human grabbing the wheel)
                SignalSource      = SneakyBridgeSource.Council;   // v0.4.0 — explicit default: existing charts trade Council unchanged
            }
            else if (State == State.DataLoaded)
            {
                // Sentinel label-remover (indicator standard, applied to this strategy): NT draws the chart's
                // top-left label from Name, parameter list and all. Blanking it here keeps the chart clean; the
                // SetDefaults value is what identity/serialization use. Order tags are the separate _tag const,
                // so this can NEVER affect order routing. ⚠ If the Control Center's Strategies grid shows a blank
                // row, set "Show strategy label" = true — you must always be able to find and disable a strategy.
                // ⚠ A DISABLED strategy never reaches DataLoaded, so its "(D) Sentinel Bridge(…)" label persists
                // until it is ENABLED (verified 2026-07-09). That is correct and deliberate: the label is only on
                // screen while the strategy is inert. Blanking Name in SetDefaults would hide it — and would also
                // blank the Control Center row you need in order to find and stop it. Don't.
                if (!ShowStrategyLabel) Name = string.Empty;
                if (UseSneakyConfig) LoadCouncilConfigs();   // read the lab-derived COUNCIL ◆ TP/SL (re-reads on enable/recompile)
            }
            else if (State == State.Terminated)
            {
                if (_hooked && ChartControl != null) { try { ChartControl.PreviewMouseLeftButtonDown -= OnChartMouseDown; } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.OnStateChange", _sx); } }
                _hooked = false;
                if (_sp != null) { try { _sp.Dispose(); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.OnStateChange", _sx); } _sp = null; }
                try { SneakySkin.CardLayout.Release(this); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.OnStateChange", _sx); }
                // v0.2.3 — release the actor key (reference-checked in Core, so the NT re-enable race can't free a
                // freshly-registered replacement instance's claim).
                if (_instanceKey != null) { try { SneakyCore.UnregisterActor(_instanceKey, this); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.OnStateChange", _sx); } }
                // v0.3.0 — drop this actor's Helm intents + published state so a Helm surface never renders a ghost.
                if (_instanceKey != null) { try { SneakyCore.ClearHelm(_instanceKey); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.OnStateChange", _sx); } }
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;
            if (State != State.Realtime) return;
            if (CurrentBar < BarsRequiredToTrade) return;
            if (Instrument == null || Instrument.MasterInstrument == null) return;

            _lastClose = Close[0];
            // v0.3.0 — drain Helm intents on the bar-close path too (a backstop to the per-tick OnMarketData drain);
            // both run on the one data thread, so the idempotent consume needs no lock. Also republishes HelmState.
            ServiceHelm();
            // v1.15.0: consult THIS CHART's scope, never the bare instrument. A bare-instrument lookup with
            // two charts on one instrument is ambiguous and now fails closed; a scope lookup is exact.
            // v0.4.0: CurrentVerdict() reads whichever source SignalSource picks and normalizes it — see the
            // Verdict adapter above. For Council this is byte-for-byte the old GetCouncilState(Scope(), StaleSec).
            var v = CurrentVerdict();
            UpdateSnapshot(v);

            // v0.4.1 — BUGFIX: the flip-exit now fires ONLY on a genuine OPPOSING read, never merely
            // because the verdict is null/stale/vetoed. The old "supports = v!=null && !Vetoed &&
            // SustainedBias==cur" definition treated a data outage (dry-up market, slow bars, a brief
            // indicator hiccup, a chart reload) identically to a real bias flip — a stale read force-closed
            // a perfectly good position with no way to tell the two apart. A missing/stale/vetoed verdict
            // now just leaves the managed TP/SL bracket in charge, exactly as if ExitOnCouncilFlip were off.
            if (_armed && ExitOnCouncilFlip && Position.MarketPosition != MarketPosition.Flat)
            {
                int cur = Position.MarketPosition == MarketPosition.Long ? 1 : -1;
                bool opposes = v != null && !v.Vetoed && v.SustainedBias == -cur;
                if (opposes)
                {
                    StampCross(-cur);                                   // v0.3.1 — closing a long SELLS (bid), a short BUYS (ask)
                    if (Position.MarketPosition == MarketPosition.Long) ExitLong(_tag + "Flip", _tag);
                    else                                                ExitShort(_tag + "Flip", _tag);
                    string srcLabel = SignalSource == SneakyBridgeSource.Confluence ? "Confluence" : "Council";
                    if (LogChanges) { try { SneakyCore.Log("Bridge", srcLabel + "-flip exit (" + (cur > 0 ? "LONG" : "SHORT") + ")"); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.ExitShort", _sx); } }
                    _lastFiredBias = 0;
                    return;
                }
            }

            bool edge = v != null && v.HasEdge && v.EntryDir != 0;
            if (!edge) { _lastFiredBias = 0; return; }
            int dir = v.EntryDir;
            if (dir == _lastFiredBias) return;
            if (!_armed) return;                                  // DISARMED: verdict shown, no trading
            if (_paused) { Block("Helm PAUSE — not entering (position + bracket kept)"); return; }   // v0.3.0

            var mp = Position.MarketPosition;
            if (mp == MarketPosition.Flat)
            {
                // v0.3.0 — Helm SkipNext: consume exactly this entry (mark the episode handled), then auto-re-arm.
                if (_skipNext) { _skipNext = false; _lastFiredBias = dir; Block("Helm SKIP-NEXT — skipped this entry"); PublishHelm(true); return; }
                if (TryEnter(dir, v)) _lastFiredBias = dir;
            }
            else if (ReverseOnFlip)
            {
                int cur = mp == MarketPosition.Long ? 1 : -1;
                if (dir == -cur && TryEnter(dir, v)) _lastFiredBias = dir;
            }
        }

        private bool TryEnter(int dir, Verdict v)
        {
            // v0.4.0: under Confluence, Conviction/SizeMult are pinned at 1.0 (see the Verdict adapter), so
            // both checks below are structural no-ops there — every Confluence-sourced entry sizes at the
            // full Base contracts / full $-risk size, by design (Confluence has no conviction number to floor).
            if (v.Conviction < MinConviction) { Block("conviction " + v.Conviction.ToString("0.00") + " < floor " + MinConviction.ToString("0.00")); return false; }
            if (v.SizeMult <= 0.0) { Block("size 0 — conviction below the floor / vetoed (no actionable size)"); return false; }

            // Optional God Reversal ENTRY TRIGGER: the source gives bias/edge(/size); the reversal gives the TIMING.
            // Require a fresh GodReversalState whose HELD dir is aligned with the source's bias (+ a quality floor).
            if (UseGodReversalTrigger)
            {
                var g = CurrentGrev();
                UpdateGrevSnapshot(g);
                if (g == null || g.Dir != dir)
                { Block("god-reversal: no fresh aligned trigger (need " + (dir > 0 ? "▲ long" : "▼ short") + ")"); return false; }
                if (g.Quality < GrevMinQuality)
                { Block("god-reversal q " + g.Quality.ToString("0.00") + " < floor " + GrevMinQuality.ToString("0.00")); return false; }
            }

            // Effective TP/SL: lab-derived <inst>_COUNCIL_<dir>.conf (when UseSneakyConfig + loaded), else the properties.
            int tpTicks = ProfitTargetTicks, slTicks = StopLossTicks;
            if (UseSneakyConfig)
            {
                if      (dir > 0 && _cfgTpLong  > 0 && _cfgSlLong  > 0) { tpTicks = _cfgTpLong;  slTicks = _cfgSlLong;  }
                else if (dir < 0 && _cfgTpShort > 0 && _cfgSlShort > 0) { tpTicks = _cfgTpShort; slTicks = _cfgSlShort; }
            }

            int baseQty = BaseContracts;
            if (UseRiskSizing)
            {
                // v0.4.1 — was a bare `catch { baseQty = 0; }` (no log, no trace) — a direct miss of the
                // 2026-07-25 Swallow migration. baseQty=0 already fails closed via the check right below,
                // this just makes the failure visible instead of silent.
                try { baseQty = SneakyCore.SizeForRisk(Account, Instrument, slTicks, RiskDollars); }
                catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.TryEnter.SizeForRisk", _sx); baseQty = 0; }
                if (baseQty <= 0) { Block("risk-size = 0 ($" + RiskDollars.ToString("0") + " / " + slTicks + "t)"); return false; }
            }
            // ── SIZING, in the order Core's own header prescribes: size → SizedQuantity (clamps) → GateEntry (validates)
            //
            // v0.2.2: the Bridge used to stop at `Math.Max(1, baseQty × SizeMult)` and never call SizedQuantity(),
            // so the account profile's `size=` (SizeScale) AND the governor's RecommendedSize were both silently
            // ignored — a governor telling this strategy to size DOWN was not obeyed — and a BaseContracts above the
            // profile's ContractLimit hard-BLOCKED every entry instead of clamping to it. SizedQuantity is, in
            // SentinelCore's words, "the one place sizing math lives."
            int councilQty = Math.Max(1, (int)Math.Round(baseQty * v.SizeMult));

            // × SizeScale × governor RecommendedSize, clamped to ContractLimit, never below 1. Fail-open: an
            // unprofiled account returns councilQty unchanged.
            int wantQty = councilQty;
            // v0.4.1 — was a bare `catch { wantQty = councilQty; }`: a thrown SizedQuantity silently
            // resurrected the exact v0.2.2 bug (governor RecommendedSize / profile SizeScale / ContractLimit
            // silently ignored) with zero log trace. SizedQuantity is safety-critical, so a throw here now
            // fails CLOSED (no trade) instead of quietly trading at an unclamped size.
            bool sizedQtyThrew = false;
            try { wantQty = SneakyCore.SizedQuantity(Account, councilQty); }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.TryEnter.SizedQuantity", _sx); sizedQtyThrew = true; }
            if (sizedQtyThrew) { Block("sizing gate threw — fail-closed (governor/SizeScale/ContractLimit unverifiable)"); return false; }
            if (wantQty != councilQty)
                try { SneakyCore.Log("Bridge", "size " + councilQty + " → " + wantQty
                          + " (profile SizeScale / governor / ContractLimit) on " + Account.Name); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.TryEnter", _sx); }

            // ⚠ RESOLUTION: with BaseContracts = 1, SizeMult cannot scale a position DOWN — 1 × 0.19 rounds to 0 and
            // the Max(1,…) floor restores a 1-lot, because you cannot trade a fifth of a contract. SizeMult only has
            // resolution when BaseContracts ≥ 2 (≥ 4 to resolve the Council's typical 0.2-1.0 range). The floor
            // (SizeMult = 0) is what expresses "do not trade"; SizeMult is not a substitute for it.

            // v0.4.1 — REVERSAL ORDER SIZE. When dir opposes an already-open position, NinjaTrader does
            // NOT submit `wantQty` — a managed EnterLong while short (or EnterShort while long) combines
            // the close and the new open into ONE order sized wantQty + |current position| (confirmed
            // NinjaTrader 8 platform behaviour: a support-forum report of exactly this pattern submitting
            // more contracts than requested and being rejected as "quantity too high for broker"). The Gate
            // must validate the number that actually reaches the broker/exchange, not just the target net
            // size, or a ContractLimit/max-order check here can pass while the real order exceeds it.
            bool isReversal = Position.MarketPosition != MarketPosition.Flat &&
                               ((dir > 0 && Position.MarketPosition == MarketPosition.Short) ||
                                (dir < 0 && Position.MarketPosition == MarketPosition.Long));
            int trueOrderQty = isReversal ? wantQty + Math.Abs(Position.Quantity) : wantQty;

            // The Bridge has now sized. The Gate only VALIDATES (kill/scoped/loss-stop/rate/qty/contract/
            // session/feed/rollover/news) — pass riskDollars=0 so it does NOT re-risk-size and reject our qty
            // as "risk too small" (that flip cost the first live test — GateEntry line ~1979/2007).
            SneakyCore.GateDecision gate;
            try { gate = SneakyCore.GateEntry(Account, Instrument.FullName, trueOrderQty, slTicks, 0, Instrument); }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.TryEnter.GateEntry", _sx); Block("gate threw — fail-closed"); return false; }
            if (gate == null || !gate.IsClear) { Block("gate " + (gate != null ? gate.Level + ": " + gate.Reason : "null")); return false; }

            // GateEntry only VALIDATES here (riskDollars=0 — it never re-sizes), so gate.Size just echoes
            // back whatever was passed in (trueOrderQty). The NET position we ask NinjaTrader for is still
            // wantQty; NinjaTrader itself adds the reversal's cover quantity on top when it submits the order.
            int qty = wantQty;

            SetStopLoss(CalculationMode.Ticks, slTicks);
            SetProfitTarget(CalculationMode.Ticks, tpTicks);

            StampCross(dir);                                            // v0.3.1 — reference price BEFORE the cross
            if (dir > 0) EnterLong(qty, _tag); else EnterShort(qty, _tag);
            try { SneakyCore.NoteOrderSubmitted(Account.Name); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.TryEnter", _sx); }
            // 2026-08-09 — feeds Excursions' optional NoteSignalFire intake, same wiring added to
            // SneakyV1_0 and Confluence tonight. TryEnter only ever runs on Bridge's own edge-detected
            // decision to enter (not every bar), so this call is already naturally one-shot -- no
            // extra edge-detection needed here either.
            try { SneakyCore.NoteSignalFire(Scope(), dir, "Bridge", State == State.Historical, Close[0], ScopeLane); }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.NoteSignalFire", _sx); }

            // v0.3.0 — a fresh entry starts a clean episode: clear the per-episode interdiction flag and seed the
            // tracked bracket prices from the entry proxy (Close[0]) ± ticks. Helm MoveStop/BreakevenNow recompute
            // exactly from Position.AveragePrice once filled; these seed the card + the tighten/widen classify.
            _humanOverride = false;
            _liveStopPrice   = dir > 0 ? Close[0] - slTicks * TickSize : Close[0] + slTicks * TickSize;
            _liveTargetPrice = dir > 0 ? Close[0] + tpTicks * TickSize : Close[0] - tpTicks * TickSize;

            _lastFireDir = dir; _lastBlock = "";
            RecordFire(dir, qty, tpTicks, slTicks, v);
            return true;
        }

        // v0.3.1 — THE FILL-COST REFERENCE. Stamp the TRADEABLE price on the side we are about to cross, at the
        // moment of submission. This is the only reference an execution-cost measurement can use: `slip` is then
        // (fill − this), the real price of crossing, instead of the tautology `fill − fill = 0`.
        //   buying  (entry long / cover a short) lifts the ASK
        //   selling (entry short / close a long) hits the BID
        // ⚠ NEVER Close[0] here. The Bridge runs on SneakyTBars, a Heikin-Ashi type whose Close is the
        // (O+H+L+C)/4 average — a price that never traded — which is what made the recorded entry price wrong by
        // a direction-symmetric ~11 ticks (memory firepx-is-synthetic-ha-close).
        // Fails SOFT: 0 means "no defensible quote", and every caller falls back to the old behaviour rather than
        // record a number it cannot stand behind. Historical/replay returns the bar close ⇒ realtime-only meaning.
        private void StampCross(int crossDir)
        {
            try
            {
                double q = crossDir > 0 ? GetCurrentAsk() : GetCurrentBid();
                _crossRefPx = q > 0 ? q : 0.0;
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.StampCross", _sx); _crossRefPx = 0.0; }
        }

        private void RecordFire(int dir, int qty, int tpTicks, int slTicks, Verdict v)
        {
            string side = dir > 0 ? "LONG" : "SHORT";
            string detail = "src=" + v.SourceTag + " inst=" + Instrument.MasterInstrument.Name + " " + side + " qty=" + qty
                          + " tp=" + tpTicks + " sl=" + slTicks
                          + " bias=" + dir + " conv=" + v.Conviction.ToString("0.00", CultureInfo.InvariantCulture)
                          + " size=" + v.SizeMult.ToString("0.00", CultureInfo.InvariantCulture)
                          + " voters=" + v.Voters + " (" + v.Agree + "/" + v.Disagree + ")"
                          + " | " + (v.Reasons ?? "");
            if (UseGodReversalTrigger)
                detail += "  || GR " + (_sGrevDir > 0 ? "▲" : _sGrevDir < 0 ? "▼" : "·") + " "
                        + (string.IsNullOrEmpty(_sGrevSetup) ? "none" : _sGrevSetup)
                        + " q" + _sGrevQ.ToString("0.00", CultureInfo.InvariantCulture);
            try
            {
                // v0.2.3 — stamp the EPISODE join key (ML spec §10.2) + this actor's instanceKey on the fire, so Lens
                // can join the resulting FILL back to the exact Council verdict that caused it.
                // v0.3.1 — the DECISION price must be a price that could actually be traded. Close[0] is the
                // Heikin-Ashi average on this bar type and was wrong by a direction-symmetric ~11 ticks; the
                // stamped cross-side quote is the real one. Falls back to Close[0] only when no quote was available.
                double refPx = _crossRefPx > 0 ? _crossRefPx : Close[0];
                SneakyCore.Ledger.Order(Account.Name, Instrument.FullName, dir > 0 ? "Buy" : "SellShort", "Market", qty, refPx, _tag, v.EpisodeId, InstanceKey());
                if (RecordVerdict) SneakyCore.Ledger.Action("bridge-fire", Account.Name, detail, v.EpisodeId, InstanceKey());
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.RecordFire", _sx); }
            if (LogChanges) { try { SneakyCore.Log("Bridge", detail); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.RecordFire", _sx); } }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
                                                  MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null) return;
            try
            {
                var o = execution.Order;
                // v0.3.1 — a MARKET fill used to set `intended = price`, making slip identically 0 (fill − fill).
                // That is not a measurement; it is a tautology, and it is why entry crossing cost had never once
                // been recorded. Use the quote we stamped on the side we crossed, at submission. Stop/limit are
                // UNCHANGED — their trigger/limit price was always the correct reference.
                double intended = price;
                if (o.OrderType == OrderType.StopMarket || o.OrderType == OrderType.StopLimit) intended = o.StopPrice;
                else if (o.OrderType == OrderType.Limit) intended = o.LimitPrice;
                else if (_crossRefPx > 0) intended = _crossRefPx;
                SneakyCore.Ledger.Fill(Account.Name, Instrument.FullName, o.OrderAction.ToString(),
                                         quantity, intended, price, TickSize, _tag, null, InstanceKey());
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.OnExecutionUpdate", _sx); }
        }

        // ── HELM interdiction (v0.3.0) ───────────────────────────────────────────────
        // A human grabs the wheel of this RUNNING autopilot without stopping it. Helm never touches an order — it
        // publishes an INTENT addressed to this actor's instanceKey; the Bridge executes it with its OWN managed
        // order handles and stays the sole owner (the three managed-order lessons in CLAUDE.md are why). Drained on
        // the data thread (OnMarketData for latency + an OnBarUpdate backstop); order calls therefore run in a valid
        // order context. Every intent is Ledgered with the episode + instanceKey and marks the episode interdicted.

        // Per-tick drain — FlattenNow/Pause must not wait for a slow bar to close. Realtime only (no historical/replay
        // interdiction), primary series only. OnMarketData is on the same data thread as OnBarUpdate, so the
        // idempotent TakeHelmIntent needs no lock.
        protected override void OnMarketData(NinjaTrader.Data.MarketDataEventArgs marketDataUpdate)
        {
            if (State != State.Realtime || BarsInProgress != 0) return;
            ServiceHelm();
        }

        // Drain the queue for THIS actor and apply each intent, then publish HelmState. Cheap when the queue is empty.
        private void ServiceHelm()
        {
            if (!ObeyHelm) { PublishHelm(false); return; }   // still publish truth so a Helm surface sees the Bridge
            string key = InstanceKey();
            if (string.IsNullOrEmpty(key)) return;
            SneakyCore.HelmIntent it;
            try { while ((it = SneakyCore.TakeHelmIntent(key)) != null) ApplyHelmIntent(it); }
            catch (Exception ex) { try { SneakyCore.Log("Bridge", "helm drain threw: " + ex.Message); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.ServiceHelm", _sx); } }
            PublishHelm(false);
        }

        // Execute one intent with the Bridge's own order handles. ASYMMETRIC GATE: risk-reducing = fail-OPEN;
        // risk-adding = validate through GateEntry fail-CLOSED. MoveStop is classified by direction vs the live stop.
        private void ApplyHelmIntent(SneakyCore.HelmIntent it)
        {
            if (it == null) return;
            _lastIntentId = it.Id ?? "";
            string why = "";
            bool applied = true;
            string gateReason = "";
            var mp = Position.MarketPosition;
            int posDir = mp == MarketPosition.Long ? 1 : (mp == MarketPosition.Short ? -1 : 0);

            switch (it.Verb)
            {
                case SneakyCore.HelmVerb.Pause:
                    _paused = true; why = "paused — no new entries (position + bracket kept)"; break;

                case SneakyCore.HelmVerb.Resume:
                    // risk-ADDING → the Gate must still allow trading (kill / governor / session). Fail-CLOSED.
                    if (HelmGateOk(out gateReason)) { _paused = false; why = "resumed"; }
                    else { applied = false; why = "resume REFUSED by gate — " + gateReason; }
                    break;

                case SneakyCore.HelmVerb.SkipNext:
                    _skipNext = true; why = "will skip the next entry, then auto-re-arm"; break;

                case SneakyCore.HelmVerb.FlattenNow:
                    if (posDir != 0)
                    {
                        StampCross(-posDir);                            // v0.3.1 — reference price BEFORE the cross
                        if (posDir > 0) ExitLong(_tag + "Helm", _tag); else ExitShort(_tag + "Helm", _tag);
                        // don't immediately jump back into the same side: hold this episode's bias as "already fired"
                        // so re-entry needs a genuine Council flip, not the still-standing verdict.
                        _lastFiredBias = _sBias != 0 ? _sBias : posDir;
                        why = "FLATTEN — closing " + (posDir > 0 ? "LONG" : "SHORT");
                    }
                    else why = "flatten: already flat";
                    break;

                case SneakyCore.HelmVerb.MoveStop:
                    if (posDir == 0)      { applied = false; why = "move-stop: flat, nothing to move"; }
                    else if (it.Price <= 0) { applied = false; why = "move-stop: no price given"; }
                    else
                    {
                        bool tightening = posDir > 0 ? it.Price > _liveStopPrice : it.Price < _liveStopPrice;
                        if (!tightening && !HelmGateOk(out gateReason))
                        { applied = false; why = "WIDEN-stop REFUSED by gate — " + gateReason; }
                        else
                        {
                            try { SetStopLoss(_tag, CalculationMode.Price, it.Price, false); _liveStopPrice = it.Price;
                                  why = (tightening ? "tightened" : "widened") + " stop → " + it.Price.ToString("0.#####", CultureInfo.InvariantCulture); }
                            catch (Exception ex) { applied = false; why = "move-stop failed: " + ex.Message; }
                        }
                    }
                    break;

                case SneakyCore.HelmVerb.MoveTarget:
                    // A target move never increases max loss (the stop bounds risk) → fail-OPEN.
                    if (posDir == 0)      { applied = false; why = "move-target: flat"; }
                    else if (it.Price <= 0) { applied = false; why = "move-target: no price given"; }
                    else
                    {
                        try { SetProfitTarget(_tag, CalculationMode.Price, it.Price); _liveTargetPrice = it.Price;
                              why = "target → " + it.Price.ToString("0.#####", CultureInfo.InvariantCulture); }
                        catch (Exception ex) { applied = false; why = "move-target failed: " + ex.Message; }
                    }
                    break;

                case SneakyCore.HelmVerb.BreakevenNow:
                    if (posDir == 0) { applied = false; why = "breakeven: flat"; }
                    else
                    {
                        double be = Position.AveragePrice;
                        try { SetStopLoss(_tag, CalculationMode.Price, be, false); _liveStopPrice = be;
                              why = "breakeven — stop → entry " + be.ToString("0.#####", CultureInfo.InvariantCulture); }
                        catch (Exception ex) { applied = false; why = "breakeven failed: " + ex.Message; }
                    }
                    break;

                case SneakyCore.HelmVerb.Scale:
                    if (it.QtyDelta < 0)
                    {
                        // scale DOWN — partial exit, risk-REDUCING → fail-OPEN.
                        if (posDir == 0) { applied = false; why = "scale-down: flat"; }
                        else
                        {
                            int reduce = Math.Min(-it.QtyDelta, Math.Abs(Position.Quantity));
                            if (reduce <= 0) { applied = false; why = "scale-down: nothing to reduce"; }
                            else
                            {
                                StampCross(-posDir);                    // v0.3.1 — reference price BEFORE the cross
                                if (posDir > 0) ExitLong(reduce, _tag + "HelmScale", _tag);
                                else            ExitShort(reduce, _tag + "HelmScale", _tag);
                                why = "scaled DOWN " + reduce + " contract" + (reduce == 1 ? "" : "s");
                            }
                        }
                    }
                    else if (it.QtyDelta > 0)
                    {
                        // scale UP adds risk AND a managed scale-in is blocked by EntriesPerDirection=1 — faking it
                        // risks the position→account desync this whole seam exists to avoid. Refuse honestly.
                        applied = false;
                        why = "scale-UP not supported in the single-entry managed Bridge — add discretionarily on the Deck";
                    }
                    else { applied = false; why = "scale: zero delta"; }
                    break;

                case SneakyCore.HelmVerb.TakeOver:
                    // A managed position cannot transfer ORDER OWNERSHIP to manual without disable/re-enable (the exact
                    // desync hazard). Honest interdiction: STAND DOWN from new entries + mark the episode interdicted;
                    // manage exits by hand via the Deck/Chart Trader. The Bridge keeps its managed bracket.
                    _paused = true;
                    why = "TAKE OVER — Bridge stood down (keeps its managed bracket; manage exits by hand)";
                    break;

                case SneakyCore.HelmVerb.HandBack:
                    // resume automated control — risk-ADDING, so it passes the Gate fail-CLOSED.
                    if (HelmGateOk(out gateReason)) { _paused = false; why = "HAND BACK — Bridge resumes control"; }
                    else { applied = false; why = "hand-back REFUSED by gate — " + gateReason; }
                    break;
            }

            if (applied) _humanOverride = true;
            _lastHelmMsg = (applied ? "" : "(refused) ") + it.Verb + " — " + why;

            // Ledger EVERY intent (applied or refused), stamped with the Council episode + instanceKey, so the Lab can
            // find interdicted episodes and exclude/model them — recording the human is what keeps the model honest.
            try
            {
                string detail = "verb=" + it.Verb + " applied=" + applied + " · " + why
                              + (string.IsNullOrEmpty(it.Reason) ? "" : " · " + it.Reason) + " · id=" + (it.Id ?? "");
                SneakyCore.Ledger.Action("helm-intent", Account != null ? Account.Name : "?", detail, _sEpisodeId, InstanceKey());
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.ExitShort", _sx); }
            try { SneakyCore.Log("Bridge", "HELM " + _lastHelmMsg); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.ExitShort", _sx); }
            PublishHelm(true);
        }

        // VALIDATE-ONLY gate probe for risk-adding intents (riskDollars=0 ⇒ no re-risk-sizing). Fail-CLOSED on throw.
        private bool HelmGateOk(out string reason)
        {
            reason = "";
            try
            {
                // v0.4.1 — was probed with the raw BaseContracts/StopLossTicks properties, not the actual
                // SizedQuantity-clamped size a real entry would use — so a Resume/HandBack/widen-stop could
                // be approved (or refused) against a different risk profile than what TryEnter would really
                // submit next. Now uses the same SizeScale/governor/ContractLimit clamp TryEnter does.
                // (The stop side still uses the plain property: which side's UseSneakyConfig stop applies
                // isn't knowable here without a direction, so this remains a best-effort approximation, not
                // a byte-for-byte replay of TryEnter's sizing.)
                int probeQty = BaseContracts;
                try { probeQty = SneakyCore.SizedQuantity(Account, BaseContracts); }
                catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.HelmGateOk.SizedQuantity", _sx); }
                var g = SneakyCore.GateEntry(Account, Instrument.FullName, probeQty, StopLossTicks, 0, Instrument);
                if (g != null && g.IsClear) return true;
                reason = g != null ? (g.Level + ": " + g.Reason) : "null gate";
                return false;
            }
            catch (Exception ex) { reason = "gate threw: " + ex.Message; return false; }
        }

        // Publish this actor's live truth so a Helm surface renders reality, not a guess. Throttled off the per-tick
        // path (force=true bypasses the throttle — used right after an intent is applied).
        private void PublishHelm(bool force)
        {
            DateTime now = DateTime.UtcNow;
            if (!force && (now - _lastHelmPubUtc).TotalMilliseconds < 1000) return;
            _lastHelmPubUtc = now;
            try
            {
                var mp = Position.MarketPosition;
                int posQty = mp == MarketPosition.Long ? Position.Quantity : (mp == MarketPosition.Short ? -Position.Quantity : 0);
                bool flat = mp == MarketPosition.Flat;
                var hs = new SneakyCore.HelmState
                {
                    Instrument    = Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : "?",
                    Account       = Account != null ? Account.Name : "?",
                    Scope         = Scope(),
                    PositionQty   = posQty,
                    AvgPrice      = flat ? 0 : Position.AveragePrice,
                    StopPrice     = flat ? 0 : _liveStopPrice,
                    TargetPrice   = flat ? 0 : _liveTargetPrice,
                    Paused        = _paused,
                    SkipArmed     = _skipNext,
                    HumanOverride = _humanOverride,
                    LastIntentId  = _lastIntentId,
                    Status        = !_armed ? "disarmed" : (_paused ? "paused" : (flat ? "watching" : "in trade"))
                };
                SneakyCore.SetHelmState(InstanceKey(), hs);
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.PublishHelm", _sx); }
        }

        private void UpdateSnapshot(Verdict v)
        {
            if (v == null) { _sHasData = false; return; }
            _sHasData = true; _sEverSeen = true; _sLastSeenUtc = DateTime.UtcNow;
            _sHasEdge = v.HasEdge; _sVetoed = v.Vetoed;
            // v0.4.0 — the card shows the ONGOING read (SustainedBias), not the one-shot EntryDir: identical to
            // the old `v.Bias` for Council, but avoids the card flickering FLAT on every non-firing Confluence bar.
            _sBias = v.SustainedBias; _sConv = v.Conviction; _sSize = v.SizeMult;
            _sVoters = v.Voters; _sAgree = v.Agree; _sDisagree = v.Disagree;
            _sReasons = v.Reasons ?? ""; _sVetoReason = v.VetoReason ?? "";
            _sEpisodeId = v.EpisodeId ?? "";       // v0.3.0 — stamp Helm-intent Ledger rows with the live episode
            _sSource = v.SourceTag;                // v0.4.0 — which source this snapshot came from
        }

        // Latest God Reversal read for this instrument (fresh ≤ GrevMaxAgeSec), or null.
        private SneakyCore.GodReversalState CurrentGrev()
        {
            // Consult the GodReversal on THIS chart (scope), not whichever chart wrote last (SentinelCore v1.19.0).
            // v0.4.1 — was a bare `catch { return null; }`: a thrown read was indistinguishable from a
            // genuinely absent reversal (both surfaced as "no fresh aligned trigger" on the card/log). Still
            // returns null either way (correct fail-closed default) — now it also leaves a trace of WHY.
            try { return SneakyCore.GetGodReversalState(Scope()
                ?? (Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : null),
                GrevMaxAgeSec); }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.CurrentGrev", _sx); return null; }
        }

        private void UpdateGrevSnapshot(SneakyCore.GodReversalState g)
        {
            if (g == null) { _sGrevFresh = false; _sGrevDir = 0; _sGrevQ = 0; _sGrevSetup = ""; return; }
            _sGrevFresh = true; _sGrevDir = g.Dir; _sGrevQ = g.Quality; _sGrevSetup = g.Setup ?? "";
        }

        private void Block(string why)
        {
            _lastBlock = why;
            if (!LogChanges || CurrentBar == _lastLoggedBlockBar) return;
            _lastLoggedBlockBar = CurrentBar;
            try { SneakyCore.Log("Bridge", "blocked: " + why); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.Block", _sx); }
        }

        // Read the lab-derived <inst>_COUNCIL_<Long|Short>.conf (written by the Excursion tab's Apply ◆) for
        // per-direction TP/SL. 0 = not found → TryEnter falls back to the ProfitTargetTicks/StopLossTicks properties.
        private void LoadCouncilConfigs()
        {
            _cfgTpLong = _cfgSlLong = _cfgTpShort = _cfgSlShort = 0;
            if (Instrument == null || Instrument.MasterInstrument == null) return;
            string inst = Instrument.MasterInstrument.Name;
            ReadCouncilConf(inst, "Long",  ref _cfgTpLong,  ref _cfgSlLong);
            ReadCouncilConf(inst, "Short", ref _cfgTpShort, ref _cfgSlShort);
            if (LogChanges)
            {
                try { SneakyCore.Log("Bridge", "config " + inst + "_COUNCIL: Long TP" + _cfgTpLong + "/SL" + _cfgSlLong
                    + "  Short TP" + _cfgTpShort + "/SL" + _cfgSlShort + "  (0 = not found → manual ticks)"); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.LoadCouncilConfigs", _sx); }
            }
        }

        private void ReadCouncilConf(string inst, string dir, ref int tp, ref int sl)
        {
            try
            {
                string path = System.IO.Path.Combine(SneakyCore.SettingsDir, "GTraderConfigs", inst + "_COUNCIL_" + dir + ".conf");
                if (!System.IO.File.Exists(path)) return;
                foreach (string raw in System.IO.File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    int iv;
                    if (key == "takeprofitticks" && int.TryParse(val, out iv) && iv > 0) tp = iv;
                    else if (key == "stoplossticks" && int.TryParse(val, out iv) && iv > 0) sl = iv;
                }
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.ReadCouncilConf", _sx); }
        }

        // ── the on-chart control surface ─────────────────────────────────────────────
        private void OnChartMouseDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (!ShowCard || ChartControl == null) return;
                var p = e.GetPosition(ChartControl);
                if (_armBtn.Width > 0 && _armBtn.Contains((float)p.X, (float)p.Y))
                {
                    if (!_armed)
                    {
                        // v0.2.3 — the name is an INTERLOCK (ML spec §10.10): claim the instanceKey BEFORE arming. A
                        // collision is another live Bridge on this same scope+account — the exact managed-position
                        // hazard — so REFUSE to arm rather than desync the account. (Realtime-only: the button is live.)
                        if (!SneakyCore.RegisterActor(InstanceKey(),
                                                        Account != null ? Account.Name : null,
                                                        Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : null,
                                                        this))
                        {
                            _armBlocked = "NAME TAKEN — " + InstanceKey() + " is already armed";
                            try { SneakyCore.Log("Bridge", "ARM REFUSED — " + _armBlocked); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.OnChartMouseDown", _sx); }
                            try { ChartControl.InvalidateVisual(); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.OnChartMouseDown", _sx); }
                            e.Handled = true;
                            return;
                        }
                        _armed = true; _armBlocked = ""; _lastFiredBias = 0;   // arming re-fires the current verdict
                        try { SneakyCore.Log("Bridge", "ARMED by click (" + InstanceKey() + ")"); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.OnChartMouseDown", _sx); }
                    }
                    else
                    {
                        _armed = false;
                        SneakyCore.UnregisterActor(InstanceKey(), this);
                        try { SneakyCore.Log("Bridge", "DISARMED by click"); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.OnChartMouseDown", _sx); }
                    }
                    try { ChartControl.InvalidateVisual(); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.OnChartMouseDown", _sx); }
                    e.Handled = true;
                }
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.OnChartMouseDown", _sx); }
        }

        protected override void OnRender(NinjaTrader.Gui.Chart.ChartControl chartControl, NinjaTrader.Gui.Chart.ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (!ShowCard || RenderTarget == null || ChartPanel == null) return;
            // hook the click handler here (guaranteed UI thread + chart present); _hooked guards double-subscribe
            if (!_hooked && ChartControl != null)
            {
                ChartControl.PreviewMouseLeftButtonDown += OnChartMouseDown;
                _hooked = true;
            }
            try
            {
                if (_sp == null) _sp = new SneakySkin.Painter();
                _sp.Begin(RenderTarget);

                // Refresh the verdict LIVE every render (the OnBarClose engine reads its own copy for firing;
                // the card must not wait for a bar to close to show the current verdict).
                try { UpdateSnapshot(CurrentVerdict()); }
                catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.OnRender", _sx); }
                if (UseGodReversalTrigger) { try { UpdateGrevSnapshot(CurrentGrev()); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.OnRender", _sx); } }

                const float cw = 266f, ch = 194f;
                // v0.4.3 — BUGFIX: a bottom-docked card used CardLayout's default 12px margin, which put this
                // card's own last text row (status footer) inside roughly the bottom 12-26px of the chart panel.
                // SneakyLagCheck's independent performance bar (ATR/GPU/CPU/FPS) anchors to ChartPanel.H-10-22,
                // i.e. roughly the bottom 10-32px -- the SAME band -- and boosts its own Z-order hard
                // (ChartBars.ZOrder + 659), so it painted over this card's status line. Neither indicator knows
                // the other exists; there is no shared reservation for a fixed bottom overlay like LagCheck's bar.
                // Reported live as "text cut off at the bottom of the card" (it wasn't cut -- it was covered).
                // Fix: bottom-anchored corners request extra clearance from CardLayout so this card's lowest row
                // sits above LagCheck's band even when LagCheck is loaded on the same chart. Top corners are
                // unaffected (LagCheck only ever docks to the panel's bottom edge).
                // v0.4.4 — BUGFIX (round 2): confirmed live that the status text was never actually clipped or
                // covered -- every character was present -- it just sat only 12px above the card's OWN bottom
                // border (r.Top+ch-26, 14px tall, against a card that ends at r.Top+ch), which read as "crowded"
                // / "cut off" against the edge on both Council and Confluence cards alike, independent of the
                // LagCheck collision above. Grew the card 16px taller and pinned the last two text rows to fixed
                // offsets from the TOP (see below) instead of "ch - X", so the extra height becomes breathing
                // room below the footer rather than shifting the footer down with it.
                bool bottomCorner = CardCorner == SneakyCardCorner.BottomLeft || CardCorner == SneakyCardCorner.BottomRight;
                float cardMargin = bottomCorner ? 40f : 12f;
                // pinned: this card carries the ARM BRIDGE button (_armBtn is hit-tested against this rect). If a
                // crowded column ever overflowed and CardLayout hid it, the ONLY on-chart arm/disarm control for an
                // order-placing strategy would silently vanish. Pinned cards are never hidden. See CardLayout policy.
                var slot = SneakySkin.CardLayout.Place(this, ChartPanel,
                    ChartPanel.X, ChartPanel.Y, ChartPanel.W, ChartPanel.H, CardCorner, cw, ch, cardMargin, pinned: true);

                bool armed = _armed;
                var edgeCol = armed ? SneakySkin.CAccent : SneakySkin.CLine;
                var r = _sp.Card(slot.X, slot.Y, cw, ch, edgeCol);
                var lead = SharpDX.DirectWrite.TextAlignment.Leading;
                var ctr  = SharpDX.DirectWrite.TextAlignment.Center;

                // header — Spartan brand mark (accent when armed, mute when disarmed)
                _sp.Helmet(r.Left + 9f, r.Top + 8f, 16f, armed ? SneakySkin.CAccent : SneakySkin.CMute, armed);
                _sp.Text("SNEAKY BRIDGE", r.Left + 20f, r.Top, r.Width - 94f, 16f, SneakySkin.CInk, 11f, true);
                _sp.Pill(armed ? "ARMED" : "DISARMED", r.Right, r.Top - 1f, armed ? SneakySkin.CAccent : SneakySkin.CMute);

                // ── ARM BRIDGE button (clickable) ──
                float by = r.Top + 22f, bh = 30f;
                var btnBorder = armed ? SneakySkin.CAccent : SneakySkin.CMute;
                _sp.Card(r.Left, by, r.Width, bh, btnBorder, 8f, 2f);           // glass button bg
                _armBtn = new SharpDX.RectangleF(r.Left, by, r.Width, bh);      // hit-rect (ChartControl coords)
                _sp.Text(armed ? "●  ARMED — click to DISARM" : "▶  ARM BRIDGE",
                    r.Left, by + 7f, r.Width, 18f, SneakySkin.CAccent, 12.5f, true, ctr);

                float y = by + bh + 8f;
                _sp.Divider(r.Left, y - 4f, r.Right);

                // verdict — v0.4.0: labelled by whichever source is actually configured, not hardcoded "Council".
                string srcName = SignalSource == SneakyBridgeSource.Confluence ? "Confluence" : "Council";
                if (!_sHasData)
                {
                    // Distinguish GENUINELY ABSENT (source not on this chart) from STALE (source present but its
                    // published state aged past StaleSec — normal in thin/dry-up markets where bars close slowly).
                    string msg;
                    if (_sEverSeen)
                    {
                        int ageSec = (int)Math.Max(0, (DateTime.UtcNow - _sLastSeenUtc).TotalSeconds);
                        msg = "— " + srcName + " verdict STALE (no fresh read for " + ageSec.ToString(CultureInfo.InvariantCulture) + "s — slow bars)";
                    }
                    else
                        msg = "— no " + srcName + " verdict (add " + srcName + " to this chart)";
                    _sp.Text(msg, r.Left, y + 6f, r.Width, 16f, SneakySkin.CMute, 10f, false, lead, true);
                }
                else if (SignalSource == SneakyBridgeSource.Confluence)
                {
                    // Confluence has no conviction/size/voter panel — show the gate-cascade read instead of
                    // manufacturing numbers that don't exist. _sBias here is the persistent trend-context field
                    // (SustainedBias), NOT the momentary fire pulse — see UpdateSnapshot's v0.4.0 comment.
                    int b = _sBias;
                    string side = b > 0 ? "LONG" : (b < 0 ? "SHORT" : "FLAT");
                    var sideCol = b > 0 ? SneakySkin.CUp : (b < 0 ? SneakySkin.CDown : SneakySkin.CMute);
                    _sp.Pill(side, r.Left + 62f, y, sideCol);
                    _sp.Text("TREND", r.Left, y + 2f, 60f, 12f, SneakySkin.CMute, 9f, true);
                    _sp.Text("full size ×1.00 (no conviction #)",
                        r.Left + 74f, y + 1f, r.Width - 74f, 14f, SneakySkin.CInk2, 10.5f, false, lead, true);

                    // gate-cascade readout in place of the voter tally (Confluence has no voter panel)
                    string gtally = _sVetoed ? "GATES BLOCKED" : "GATES OK";
                    if (_sVetoed && !string.IsNullOrEmpty(_sVetoReason)) gtally += " · " + Trunc(_sVetoReason, 24);
                    _sp.Text(gtally, r.Left, y + 28f, r.Width, 14f, _sVetoed ? SneakySkin.CWarn : SneakySkin.CInk2, 10f, false, lead, true);
                    _sp.Text(Trunc(_sReasons, 40), r.Left, y + 44f, r.Width, 14f, SneakySkin.CMute, 9f, false, lead, true);
                }
                else
                {
                    int b = _sBias;
                    string side = b > 0 ? "LONG" : (b < 0 ? "SHORT" : "FLAT");
                    var sideCol = b > 0 ? SneakySkin.CUp : (b < 0 ? SneakySkin.CDown : SneakySkin.CMute);
                    _sp.Pill(side, r.Left + 62f, y, sideCol);
                    _sp.Text("VERDICT", r.Left, y + 2f, 60f, 12f, SneakySkin.CMute, 9f, true);
                    _sp.Text("conv " + Math.Round(_sConv * 100).ToString(CultureInfo.InvariantCulture) + "%",
                        r.Left + 74f, y + 1f, 90f, 14f, SneakySkin.CInk2, 10.5f, false, lead, true);
                    _sp.Text("size ×" + _sSize.ToString("0.00", CultureInfo.InvariantCulture),
                        r.Left + 160f, y + 1f, 80f, 14f, _sSize > 0 ? SneakySkin.CAccent : SneakySkin.CMute, 10.5f, false, lead, true);

                    // conviction track + tally
                    _sp.Track(r.Left, y + 20f, r.Width, (float)Math.Max(0, Math.Min(1, _sConv)), armed ? SneakySkin.CAccent : SneakySkin.CFaint, 5f);
                    string tally = "▲" + _sAgree + " ▼" + _sDisagree + " · " + _sVoters + "v";
                    if (_sVetoed) tally += "   VETO" + (string.IsNullOrEmpty(_sVetoReason) ? "" : ":" + Trunc(_sVetoReason, 14));
                    _sp.Text(tally, r.Left, y + 28f, r.Width, 14f, _sVetoed ? SneakySkin.CWarn : SneakySkin.CInk2, 10f, false, lead, true);
                    _sp.Text(Trunc(_sReasons, 40), r.Left, y + 44f, r.Width, 14f, SneakySkin.CMute, 9f, false, lead, true);
                }

                // position + last fire
                string pos;
                var posCol = SneakySkin.CMute;
                var mp = Position.MarketPosition;
                if (mp == MarketPosition.Flat) pos = "flat";
                else
                {
                    int d = mp == MarketPosition.Long ? 1 : -1;
                    double openTicks = TickSize > 0 ? (_lastClose - Position.AveragePrice) / TickSize * d : 0;
                    pos = (d > 0 ? "LONG " : "SHORT ") + Math.Abs(Position.Quantity) + "   " + (openTicks >= 0 ? "+" : "") + Math.Round(openTicks) + "t";
                    posCol = openTicks >= 0 ? SneakySkin.CUp : SneakySkin.CDown;
                }
                // v0.4.4: fixed offset from the TOP, not "ch - 42f" -- see note above where ch is declared.
                _sp.Text(pos, r.Left, r.Top + 136f, r.Width, 14f, posCol, 10.5f, true, lead, true);

                // status footer
                string status = armed ? (mp == MarketPosition.Flat ? "watching — will fire on an aligned edge" : "in trade")
                                      : "disarmed — click ARM BRIDGE to trade";
                // when the God Reversal trigger gates entry, show its live read so you can see WHY it's waiting
                if (UseGodReversalTrigger && armed && mp == MarketPosition.Flat)
                    status = (_sGrevFresh && _sGrevDir != 0)
                        ? "GR " + (_sGrevDir > 0 ? "▲" : "▼") + " " + (string.IsNullOrEmpty(_sGrevSetup) ? "reversal" : _sGrevSetup)
                          + " q" + _sGrevQ.ToString("0.00") + (_sHasData && _sGrevDir == _sBias ? " · aligned" : " · waiting align")
                        : "GR trigger: waiting for a reversal";
                // v0.3.0 — Helm interdiction takes visual priority (a human is driving): show pause / last override.
                if (_paused) status = "PAUSED by Helm — no new entries";
                else if (_humanOverride && !string.IsNullOrEmpty(_lastHelmMsg)) status = "Helm: " + Trunc(_lastHelmMsg, 34);
                if (!string.IsNullOrEmpty(_lastBlock)) status = "blocked: " + Trunc(_lastBlock, 34);
                // v0.4.4: fixed offset from the TOP, not "ch - 26f" -- leaves 28px of clear space below this
                // row to the card's bottom border (was 12px), so it no longer reads as crowded/"cut off".
                _sp.Text(status, r.Left, r.Top + 152f, r.Width, 14f,
                    _paused ? SneakySkin.CWarn : SneakySkin.CMute, 9f, false, lead, true);

                _sp.End();
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBridge.OnRender", _sx); }
        }

        private static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.Length <= max) return s;
            // v0.4.2 — BUGFIX: a raw Substring(0, max) landed mid-word with no visual
            // indication anything was cut (e.g. the God Reversal block message read
            // "...no fresh aligned tri" instead of "trigger" on the card — reported
            // live, looked like a broken/clipped render, not a truncation). Back up to
            // the last space within budget (reserving one char for the ellipsis) and
            // append "…" so a shortened message always reads as deliberately shortened.
            int cut = max - 1;
            if (cut < 1) return s.Substring(0, Math.Max(0, max));
            int lastSpace = s.LastIndexOf(' ', Math.Min(cut, s.Length - 1));
            int end = lastSpace > 0 ? lastSpace : cut;
            return s.Substring(0, end).TrimEnd() + "…";
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Base contracts", Description = "Base position size before the Council SizeMult scales it (Fixed sizing).", GroupName = "1 Sizing", Order = 1)]
        public int BaseContracts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use $-risk sizing", Description = "Size from RiskDollars ÷ (StopLossTicks × $/tick) instead of Base contracts, then × SizeMult.", GroupName = "1 Sizing", Order = 2)]
        public bool UseRiskSizing { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "Risk $ (per trade)", Description = "Dollar risk per trade when 'Use $-risk sizing' is on.", GroupName = "1 Sizing", Order = 3)]
        public double RiskDollars { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Profit target (ticks)", Description = "Managed profit target. Hand-set from the ◆ of the COUNCIL group in the Excursion tab.", GroupName = "2 Bracket", Order = 1)]
        public int ProfitTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Stop loss (ticks)", Description = "Managed stop. Hand-set from the ◆ of the COUNCIL group in the Excursion tab.", GroupName = "2 Bracket", Order = 2)]
        public int StopLossTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Sneaky config (COUNCIL ◆)", Description = "Auto-read <inst>_COUNCIL_<dir>.conf (written by the Excursion tab's Apply ◆) for per-direction TP/SL, overriding the manual ticks above. Re-reads on enable/recompile. ⚠ v0.4.0: this is COUNCIL-derived regardless of Signal source below — there is no Confluence-specific lab config yet, so leaving this checked while Signal source = Confluence still brackets with Council's tuned TP/SL numbers.", GroupName = "2 Bracket", Order = 3)]
        public bool UseSneakyConfig { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "Verdict stale (sec)", Description = "Ignore a verdict (Council or Confluence, whichever is selected below) older than this — a frozen source can't fire a stale trade.", GroupName = "3 Council / Confluence", Order = 1)]
        public double StaleSec { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "Min conviction", Description = "Extra conviction floor ABOVE the source's own (0 = trust it). Set from the ⑤ Conviction referee. ⚠ Confluence has no conviction number — this floor is always satisfied (a no-op) when Signal source = Confluence.", GroupName = "3 Council / Confluence", Order = 2)]
        public double MinConviction { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Reverse on flip", Description = "If in a position and the source fires an aligned edge the other way, reverse. Council: fires on its held bias flipping. Confluence: fires on its next debounced trigger opposing the open side. Default OFF.", GroupName = "3 Council / Confluence", Order = 3)]
        public bool ReverseOnFlip { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Exit on flip", Description = "Close the position when the source stops supporting the open side. Council: its held Bias no longer agrees (loses edge / flips / vetoed). Confluence: its persistent trend-context Bias field no longer agrees (NOT the momentary fire pulse — that would read every non-firing bar as \"flipped\" and exit almost instantly). Default OFF.", GroupName = "3 Council / Confluence", Order = 4)]
        public bool ExitOnCouncilFlip { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use God Reversal trigger", Description = "Also require a FRESH SneakyGodReversal read aligned with the source's bias before entering — the source gives bias/edge(/size), the reversal gives entry timing (doctrine §7). Needs Sneaky God Reversal on the chart + SentinelCore ≥ v1.14.0. Default OFF.", GroupName = "3 Council / Confluence", Order = 5)]
        public bool UseGodReversalTrigger { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, double.MaxValue)]
        [Display(Name = "God Reversal stale (sec)", Description = "Ignore a God Reversal read older than this when the trigger is on.", GroupName = "3 Council / Confluence", Order = 6)]
        public double GrevMaxAgeSec { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "God Reversal min quality", Description = "Minimum reversal confluence quality (0..1) required to trigger. 0 = any aligned reversal.", GroupName = "3 Council / Confluence", Order = 7)]
        public double GrevMinQuality { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Record verdict on fire", Description = "Write the verdict to the Ledger on every fire so Lens can grade the weights. Keep ON.", GroupName = "4 Sneaky", Order = 1)]
        public bool RecordVerdict { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Log fires/blocks", Description = "Write fires and gate-blocks to sentinel.log (blocks rate-limited to once/bar).", GroupName = "4 Sneaky", Order = 2)]
        public bool LogChanges { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show card", Description = "Draw the on-chart Sneaky glass card + the clickable ARM BRIDGE button.", GroupName = "4 Sneaky", Order = 3)]
        public bool ShowCard { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Card corner", Description = "Which chart corner the card docks to. Cards in the same corner auto-stack.", GroupName = "4 Sneaky", Order = 4)]
        public SneakyCardCorner CardCorner { get; set; }

        // NOT [NinjaScriptProperty] on purpose: it still serializes to the workspace/template and shows in the F6
        // grid, but adds NO parameter to NT's generated-region constructor — so existing saved charts keep loading.
        [Display(Name = "Show strategy label", Description = "Show NT's top-left chart label (name + full parameter list). Off = clean Sneaky chart. Turn ON if the Control Center's Strategies grid shows a blank name.", GroupName = "4 Sneaky", Order = 5)]
        public bool ShowStrategyLabel { get; set; }

        // v0.4.0 — NOT [NinjaScriptProperty] (same reason as ScopeLane/ObeyHelm below): serializes + shows, no
        // ctor churn — so every existing saved Bridge instance loads unchanged and keeps trading Council (the
        // default) exactly as it does today, until this is changed by hand.
        [Display(Name = "Signal source", Description = "Which published verdict drives entries: COUNCIL (fused multi-voter bias + conviction/size — the original engine, unchanged) or CONFLUENCE (gate-cascade debounced trigger fire — no conviction number, every entry sizes at full Base contracts / full $-risk size). Default Council. See the file-header v0.4.0 changelog for exactly how MinConviction / Reverse on flip / Exit on flip behave under each.", GroupName = "0 Signal Source", Order = 0)]
        public SneakyBridgeSource SignalSource { get; set; }

        // v0.2.4 — NOT [NinjaScriptProperty] (same reason as above): serializes + shows, no ctor churn.
        [Display(Name = "Scope Lane", Description = "Per-chart lane — match the 'Scope Lane' / 'Instance Tag' set on THIS chart's Council or Confluence (whichever is selected above) so the Bridge reads the right lane's verdict. BLANK = bare scope (default). Needed only when two charts share instrument + bar type + size (e.g. A/B test lanes). Letters/digits only.", GroupName = "0 Signal Source", Order = 1)]
        public string ScopeLane { get; set; }

        // v0.3.0 — NOT [NinjaScriptProperty] (serializes + shows, no ctor churn). When ON, the Bridge obeys Helm
        // interdiction intents (a human grabbing the wheel via a Helm surface): Pause/SkipNext/FlattenNow/MoveStop/
        // MoveTarget/BreakevenNow/Scale-down, with risk-adding verbs (Resume/widen/HandBack) validated by the Gate.
        // OFF = the Bridge ignores all intents (still publishes HelmState so a surface can see it). Default ON.
        [Display(Name = "Obey Helm intents", Description = "Obey Helm interdiction intents addressed to this Bridge (a human grabbing the wheel without stopping it). Risk-reducing moves are fail-open; risk-adding moves pass GateEntry. Default ON.", GroupName = "5 Helm", Order = 1)]
        public bool ObeyHelm { get; set; }
        #endregion
    }
}
