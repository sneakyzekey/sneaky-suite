// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
//
// Copyright (c) 2026 silentsudo-io and the Sentinel Suite contributors.
// ─────────────────────────────────────────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════
//  SneakyCopierService — headless multi-account trade copier (NT8 AddOn)
//  File: SneakyCopierService_v0_1_0.cs
//  Service version: v0.1.0i   (in-place; NOT a skeleton — see "ACTUAL STATE" below)
// ─────────────────────────────────────────────────────────────────────────────
//  ACTUAL STATE AS OF v0.1.0i (correcting the stale "SKELETON" line above — it undersold
//  this file the same way an earlier stale header did on Council): this is a fully
//  developed fill-mirror copier with config persistence, manual-assist, Eye-gate,
//  same-provider policy, a consistency governor + session gate, copy-slippage capture,
//  a zero-crossing order split, and a live-only BRACKET MIRROR feature main does not have
//  at all. What WAS genuinely missing — ported in this pass — was any position-drift
//  detection: a rejected mirror, a partial, a hand-placed follower trade, or a restart
//  mid-flight could silently desync a follower and nothing would notice. See the POSITION
//  RECONCILER section below.
// ─────────────────────────────────────────────────────────────────────────────
//  WHAT THIS IS  (part of the "Sentinel Suite"; see Docs/ROADMAP.md)
//    A headless, always-on AddOn service — SAME architecture as MAECaptureService:
//    an AddOnBase singleton that lives for the NT process, exposes a static Instance
//    so a future dashboard/settings window ATTACHES to it, and never depends on any
//    window being open. Closing a UI must never stop copying.
//
//  WHAT IT DOES  (pure FILL-MIRROR copier — the "Horde" model)
//    • Subscribes to the LEADER account's ExecutionUpdate.
//    • On each LEADER fill (Filled/PartialFilled), for every enabled+connected FOLLOWER:
//        - size  = leaderFillQty × instrumentSizeRatio × followerMultiplier
//        - symbol = per-follower instrument map (e.g. GC→MGC), else same instrument
//        - submits a MARKET order on the follower account (unmanaged, account-level).
//    • Pure fill-mirror: it never reads the leader strategy's internal state. Entry AND
//      exit both arrive as fills, so each follower's NET position stays synced with the
//      leader automatically. Works with GTrader21, manual ChartTrader, or any strategy.
//
//  CREDIT / ATTRIBUTION  (required before any open-source release)
//    The fill-mirror engine (subscribe to master ExecutionUpdate → qty×multiplier → market
//    CreateOrder+Submit, Guid order name) is ADAPTED from the TickHunter NT "Horde" copier by
//    **Frosty** (TradeLikeAZombie community). The original "SentCopy_" order prefix echoed their
//    "TH-Horde_" (this port uses "SnkCopy_" to match Sneaky's own naming). Sentinel ADDS: same-provider prop rule, bidirectional GC↔MGC / CL↔MCL map,
//    SentinelCore kill-switch + feed-health gating, Eye-gate, and manual-assist mode. Thanks, Frosty.
//
//  LEADER MODEL (decided — see memory copier-samples-analysis)
//    The leader is a SIGNAL account (a dedicated SIM or small live acct trading native GC).
//    EVERY real account — including what the user calls "primary" — is an execution TARGET
//    with its own instrument map (GC→MGC) + size. This satisfies "primary cross-trades"
//    AND "SIM leader → prop accounts" with ZERO changes to GTrader21's order path.
//
//  SUITE CONTRACTS (how this ties into the rest of the Sentinel Suite)
//    • ORDER-NAME PREFIX  = "SnkCopy_"  → distinguishes copier orders from strategy orders
//      (GTrader21 uses "GTS_"; keep prefixes disjoint so tools never edit each other's orders).
//    • SHARED KILL-SWITCH = SneakyCore.KillSwitchEngaged (in SentinelCore_v1_0_0.cs). Any
//      suite tool (a risk monitor, the panel's lockout, the dashboard) flips it to halt ALL
//      mirroring. The copier no longer owns its own flag — it consults the suite Core.
//    • MIRROR GATE        = SneakyCore.CanAct() + per-follower connection health, the single
//      choke point every mirror passes through. Core's feed-health probe (once a health tool
//      registers one — e.g. the GTrader21 v0.1.2 lag metric) gates each account automatically.
//
//  VERIFIED API NOTES (from in-repo usage — AlightenButtonPanelv3, MAECaptureService)
//    • AddOnBase auto-starts as a singleton; OnStateChange fires SetDefaults/Active/Terminated.
//      GOTCHA: base ctor calls OnStateChange BEFORE the subclass ctor → init lazily in Start(),
//      never rely on field initializers for mutable state.
//    • Order submit (account-level, no strategy):
//        acct.CreateOrder(instr, OrderAction, OrderType, OrderEntry.Automated, TimeInForce,
//                         qty, limitPx, stopPx, ocoId, name, Core.Globals.MaxDate, null);
//        acct.Submit(new[]{ order });   // wrap in try/catch
//    • Instrument.GetInstrument(fullName) resolves a contract; returns null if not found.
//    • Execution carries the AUTHORITATIVE fill: e.Execution.{Account,Instrument,Price,
//      Quantity,ExecutionId,Order}. e.Execution.Order.{OrderState,OrderAction}.
//    • MUST unsubscribe every account event in Terminated (leak / dangling handler otherwise).
//
//  VERIFIED AGAINST IN-REPO USAGE (2026-07-01 — these compile, no longer VALIDATE):
//    • Account.Connection.Status == ConnectionStatus.Connected — Account.Connection is real
//      (@ProfitLoss.cs:89 position.Account.Connection); Connection.Status (@BarTimer.cs:50).
//    • Connection.Options.Name for provider identity — AutoReconnect.cs:247 (c.Options.Name).
//
//  STILL TO VALIDATE ON A SIM RUN (marked VALIDATE: inline — real behavior, not compile)
//    • Cross-instrument contract resolution for GC→MGC: the same-expiry heuristic assumes MGC
//      shares GC's month code. 2026-08-09 — checked against real, currently-listed CME contract
//      symbols (MGCZ2025, MGCG2026, MGCJ2026...) — same Feb/Apr/Jun/Aug/Oct/Dec cycle GC uses,
//      and CME documents 100% margin offsets between GC/MGC "for the same month," which only
//      makes sense if the months align. Strong evidence the assumption holds; not a live-run
//      substitute, but no longer an open question either.
//    • Account.Positions iteration for entry-vs-exit action (Positions exists; confirm it's fresh
//      enough at mirror time). 2026-08-09 — the zero-crossing split itself is now implemented
//      (see ResolveFollowerAction's currentQty + the split logic in MirrorToFollower) — still
//      genuinely untested against a real live crossing fill, same as everything else here.
//    • Provider grouping semantics: Options.Name is the CONNECTION name (firm-level, correct);
//      note the Provider ENUM would group by tech-provider (too coarse — two Rithmic firms collide).
//    • Whether ExecutionUpdate can re-fire the same ExecutionId. 2026-08-09 — confirmed via
//      NinjaTrader's own support forum: yes, with an Operation of "Update" (vs. the first "Add"),
//      carrying incremental info (e.g. Rate) about the same fill. The dedupe-by-ExecutionId guard
//      is the correct response to this, not overcautious — it's what stops the SAME fill from
//      being mirrored twice.
//
//  CHANGELOG
//    v0.1.1  — (in-place, 2026-09-07 — chat request) TWO independent additions:
//             • MULTI-LEADER. CopierConfig.LeaderAccount/.Followers (the original single-leader
//               slot) are UNCHANGED in shape on purpose — anything else reading them (a future
//               dashboard Copy tab) keeps compiling and behaving exactly as before for a
//               single-leader setup. Extra leaders live in the new CopierConfig.Leaders list
//               (each a LeaderConfig{AccountName, Followers}); CopierConfig.AllLeaders() yields
//               the primary + every extra as one sequence for internal iteration. Copy.conf:
//               the FIRST leader= line in the file is always the primary slot (even if blank);
//               every leader= line after that starts a new entry in Leaders, and follower= lines
//               apply to whichever leader= block they follow — so an existing single-leader
//               Copy.conf parses byte-identical to before, and adding a second/third/Nth leader
//               is just more leader=/follower= blocks appended to the file. NO COUNT CAP is
//               enforced here: NinjaTrader's AddOn API doesn't limit how many Account objects one
//               AddOn instance can subscribe ExecutionUpdate on — the real ceiling is whatever
//               your broker/data-feed CONNECTION TYPE allows logged in simultaneously (unlimited
//               for a Tradovate/NinjaTrader-brokerage connection; ONE at a time for a bare Rithmic
//               connection, unless Rithmic's own "Unlimited Prop Firm Connections" add-on is in
//               play) — a platform/provider constraint, not something this file should invent its
//               own smaller limit for. Every place that used to assume one leader now scopes by
//               leader account: the same-account-as-follower guard checks every configured leader,
//               not just the primary; the bracket-mirror tracking dictionaries (_leaderEntryPx/
//               _leaderStopTicks/_leaderTargetTicks/_pendingBrackets) are now keyed by
//               "leaderAccount|masterSymbol" instead of bare symbol, so two leaders trading the
//               same instrument (e.g. both signal on NQ) can no longer clobber each other's
//               tracked bracket distance — the follower-side fill handler recovers WHICH leader a
//               given mirror came from via the same _mirrorLeaderPx-style correlation map already
//               used for copy-slippage scoring (a new sibling, _mirrorLeaderAcct, keyed by the
//               follower's own mirror order name). MirrorClaim's dedupe already composite-keyed on
//               (leaderAccount, executionId) — needed no change. CanMirror now takes the firing
//               leader's Account directly (from the execution that triggered it) instead of
//               re-deriving "the" leader from config, since there can be more than one now.
//             • REJECT-IF-IN-TRADE (global, opt-in — rejectIfInTrade=on in Copy.conf, default off
//               so no existing Copy.conf changes behavior silently). Per-instrument, not
//               account-wide: checks ResolveFollowerAction's own currentQty for the SPECIFIC
//               instrument being mirrored, so a follower already in a trade on NQ can still take a
//               fresh entry on ES normally. When on: a mirror that would ADD to an existing
//               same-direction position, OR — after a zero-crossing close — OPEN the new opposite-
//               side leg, is skipped and logged instead of submitted. The existing "a follower must
//               never be trapped unable to exit" invariant is preserved exactly: the CLOSE/REDUCE
//               portion of any fill always still mirrors in full, unconditionally, exactly as
//               before — this only ever blocks the piece that would create NEW exposure while the
//               follower is already positioned in that instrument. Implemented as an early check
//               (isReducingAction = fAction is Sell-while-Long or BuyToCover-while-Short, the only
//               two actions ResolveFollowerAction ever returns for a genuine reduce) right after
//               ResolveFollowerAction runs, plus one more check inside the existing isCrossing
//               branch guarding just the post-close opening leg.
//    v0.1.0i — (in-place) ported from main's v0.2.0/v0.2.2, mapped deliberately rather than
//             copy-pasted (main uses "Sentinel"/"SentCopy_" naming; live keeps "Sneaky"/"SnkCopy_"):
//             • POSITION RECONCILER — a 30s System.Threading.Timer (_reconcileTimer) compares
//               each enabled AUTO follower's net position (per mapped instrument) against
//               leaderNet × ratio × multiplier. Sweeps the UNION of leader.Positions AND
//               follower.Positions symbols (not leader-only — NT removes a FLAT position from
//               Account.Positions entirely, so a leader-only sweep goes BLIND the instant the
//               leader is flat, which is exactly when a stray follower position is most worth
//               catching). Alerts only after 2 consecutive disagreeing samples 30s apart
//               (a mirror in flight looks like a divergence for a moment; a single-sample alert
//               would cry wolf on every trade). REPORTS ONLY — never places or cancels an order
//               to "fix" a difference; a wrong auto-correction on a real position costs more than
//               a delta the operator is told about. Also tracks recovery: a divergence that
//               resolves by BOTH sides going flat leaves the union sweep with nothing to visit,
//               so a separate per-tick `visited` set explicitly announces "RECONCILED ... left
//               BOTH books" for any stale streak instead of leaking it silently forever.
//             • COPY MODE — CopyMode {Fill, Order} added to FollowerConfig (Copy.conf gets a 5th
//               pipe field, APPENDED so an existing 4-field file still loads → defaults to Fill).
//               Order mode (mirror the leader's working orders instead of fills) is declared but
//               NOT implemented — it fails CLOSED at both Subscribe() and MirrorToFollower(), same
//               as main: an Order-mode copier without something watching for silent divergence is
//               worse than no Order mode at all, and the reconciler above is what that would need.
//               Ported for parity/future-proofing only; every existing follower stays Fill.
//             • MIRROR CLAIM — OnLeaderExecution's dedupe now also takes a process-wide,
//               AppDomain-hosted claim (MirrorClaim.TryClaim) on (leader, executionId), not just
//               the per-instance _seenExecIds HashSet. Ported precautionary: main built this after
//               MEASURING two live singleton instances running simultaneously post-assembly-reload
//               (NT never Terminates the orphan), which a per-instance set can't see. There's no
//               evidence live has hit this, but the underlying NT reload behavior isn't main-specific
//               — a per-instance guard has the same blind spot here. Fails CLOSED: a duplicate order
//               is unrecoverable, a missed mirror is visible and now the reconciler reports it.
//             • DELIBERATELY NOT PORTED: main's LoadConfig() now logs eyeGate= as an "OBSOLETE KEY"
//               because main removed the Eye-gate feature entirely 2026-08-11. Live's Eye-gate
//               (UseEyeGate / eyeGate=on|off) is an ACTIVE, working feature here, not an obsolete
//               one — porting that log line would misreport a live feature as dead. Left untouched.
//    (in-place, 2026-07-25) — RECORDED CATCHES: 5 empty `catch {}` -> SneakyCore.Swallow (Core >= v1.41.0).
//             Behaviour identical; a swallowed fault on the mirror path is now counted and logged.
//    v0.1.0h — (in-place) COPY-SLIPPAGE capture: also subscribe enabled AUTO followers' ExecutionUpdate
//             (OnFollowerExecution) to log each mirror FILL to SneakyCore.Ledger.Fill with intended =
//             the LEADER fill price we replicated (correlated by mirror order name at submit) vs the
//             follower's actual fill → adverse slip ticks in the dashboard Slippage view (how faithfully
//             followers track the signal — a real prop-copy quality metric). Same-symbol + GC→MGC/ES→MES
//             share a price scale (meaningful); exotic cross-maps would not (rare). CAPTURE ONLY — never
//             acts; bounded dedupe + correlation map; follower subs torn down in Unsubscribe/Stop.
//    v0.1.0g — (in-place) the flat-follower entry gate now ALSO honors the account profile's SESSION
//             window (SneakyCore.InAccountSession) — a flat follower opens no new trades outside its
//             session; open positions still mirror (manage/close). Composes with the governor gate.
//    v0.1.0f — (in-place) CONSISTENCY GOVERNOR: a follower that hit its daily cap/loss (SentinelCore
//             governor, hosted by Risk) opens NO new trades today — MirrorToFollower skips a mirror when
//             the follower is FLAT and !TradingAllowedToday. Non-flat followers always mirror (manage/
//             close), so the governor never traps a live position. Per-follower, per the spec.
//    v0.1.0e — (in-place) SCOPED KILL: CanMirror now gates via SneakyCore.CanActInstrument(leader
//             instrument) instead of CanAct, so a per-instrument kill (Risk on a lagging GC feed)
//             halts only GC mirrors — ES/NQ keep copying. Global kill-switch still halts everything.
//    v0.1.0d — (in-place) ATTRIBUTION: added explicit CREDIT to Frosty / TickHunter "Horde" (see
//             the CREDIT block above). Comment-only — no behavior change. Required before any
//             open-source release; previously the header only nodded at "the 'Horde' model".
//    v0.1.0c — (in-place) MANUAL-ASSIST mode. A follower can be 'manual' (follower=<label>|manual|…):
//             instead of auto-submitting, the Copier PUBLISHES a place-by-hand ticket to
//             SentinelCore's assist registry (dashboard Assist tab + state.json). Same map/size/
//             Eye-gate pipeline; mirrors the leader's exact action; the account name is just a label
//             (need not be an NT account). For prop firms that bar automated copy-trading (TPT eval/
//             PRO, Bulenox) — decision-support instead of auto-execution. Auto path unchanged.
//    v0.1.0b — (in-place) Eye-gate: when UseEyeGate, mirror only ENTRIES SentinelEye qualifies
//             (exits always mirror). eyeGate=on/off in Copy.conf.
//    v0.1.0 — initial SKELETON. Headless AddOnBase singleton; leader ExecutionUpdate
//             subscription; fill-mirror engine; per-follower instrument map + size ratio +
//             multiplier; same-provider policy (Off/Warn/Block) with SIM-leader exemption;
//             shared kill-switch + mirror gate; "SnkCopy_" order prefix. Config is EMPTY by
//             default (service is inert until a leader+followers are set) — safe to install.
//             Live order paths marked VALIDATE; dashboard + JSON config load are follow-ups.
//    v0.1.0a — (in-place, pre-freeze) consume SentinelCore: kill-switch + feed-health gate now
//             live in the shared Core (SentinelCore_v1_0_0.cs) instead of a copier-local flag;
//             logging routed through SneakyCore.Log("Copy", …). No behavior change to mirroring.
//    v0.1.0b — (in-place, pre-freeze) CONFIG PERSISTENCE: SaveConfig()/LoadConfig() to a simple
//             text file <UserDataDir>\Sentinel\Copy.conf (leader/policy/follower lines, follower
//             map reuses the "GC>MGC*10" DSL). Start() auto-loads it → the copier survives NT
//             recompiles/restarts instead of resetting to inert. Dashboard Apply saves it.
// ═════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections;              // non-generic Hashtable: the cross-assembly mirror claim (see MirrorClaim)
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.AddOns.Sneaky;   // shared suite core (kill-switch, health gate)

namespace NinjaTrader.NinjaScript.AddOns.SneakyCopier
{
    // ── same-provider prop-firm policy ───────────────────────────────────────
    // Off   : copy across any provider freely.
    // Warn  : copy across providers but log a warning (audit trail).
    // Block : refuse a mirror when leader/follower providers differ (prop compliance).
    // NOTE: a SIM leader is EXEMPT (it's local, belongs to no firm) — the meaningful
    //       enforcement is among EXECUTION accounts. See copier-samples-analysis memory:
    //       SIM→multiple prop firms is desired, so "all targets share leader provider"
    //       is NOT the rule. This enum is the coarse first cut; per-firm grouping refines it.
    public enum ProviderPolicy { Off, Warn, Block }

    // Auto = mirror by submitting orders (default). Manual = emit a "place by hand" ASSIST ticket
    // instead of submitting — for prop firms that bar automated copy-trading (TPT eval/PRO, Bulenox).
    public enum FollowerMode { Auto, Manual }

    // ── COPY MODE — WHAT WE MIRROR. An axis independent of FollowerMode, which is HOW the mirror
    //    reaches the follower (submit vs assist ticket). Order+Manual is a real combination: mirror
    //    the leader's working order as a ticket for a firm you would rather hand-place into.
    //
    //  Fill  (default) — mirror EXECUTIONS. Only ever copies things that actually happened, so a
    //        follower can never hold a position the leader never took. Cannot mirror a resting order.
    //  Order — mirror ORDER events. Faster off the mark and mirrors working orders, but it copies
    //        INTENT: if the leader's limit never fills and the follower's does, they diverge and
    //        nothing in the fill path will ever notice. That is why Order mode is inseparable from
    //        the position reconciler below — an Order-mode copier without one looks fine for weeks.
    //  v0.1.0i — declared for parity with main; NOT IMPLEMENTED here either. Fails CLOSED (see
    //        Subscribe() and MirrorToFollower()). Every follower stays Fill until this ships.
    public enum CopyMode { Fill, Order }

    // one entry in a follower's instrument map: leader symbol → target symbol + size ratio.
    // e.g. GC → { MGC, 10 }  (1 GC ≈ 10 MGC); size = leaderQty × ratio × follower.Multiplier.
    public sealed class InstrumentMapEntry
    {
        public string TargetSymbol;      // master-instrument name, e.g. "MGC"
        public double SizeRatio = 1.0;   // contract multiplier vs leader (GC→MGC = 10)
    }

    public sealed class FollowerConfig
    {
        public string AccountName;
        public bool   Enabled = true;
        public FollowerMode Mode = FollowerMode.Auto;   // Auto = submit orders; Manual = emit assist ticket
        public CopyMode CopyMode = CopyMode.Fill;       // Fill = mirror executions; Order = mirror intent (not implemented)
        public double Multiplier = 1.0;  // extra per-follower size scaling on top of ratio
        // leader master symbol (e.g. "GC") → target (e.g. MGC ×10). Empty = same instrument.
        public Dictionary<string, InstrumentMapEntry> InstrumentMap =
            new Dictionary<string, InstrumentMapEntry>(StringComparer.OrdinalIgnoreCase);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  MirrorClaim — "this leader fill is MINE to mirror", process-wide.
    //
    //  WHY THIS EXISTS (measured on main 2026-08-11, not theorised): a single leader
    //  BuyToCover produced TWO `MIRROR ▶` lines 26 ms apart and drove the follower from
    //  -1 to LONG +1 — a side the leader never took. TWO service instances were alive in
    //  one NT: every copier log line emitted exactly twice, on one 30s timer period. The
    //  old guard was a per-instance HashSet, so neither instance could see the other's mirror.
    //
    //  WHY IT IS NOT A `static` FIELD — the trap that makes the obvious fix useless: the
    //  instances are suspected to come from an ASSEMBLY RELOAD, where the old assembly's
    //  AddOn is never Terminated and keeps running. Across a reload the new assembly gets
    //  its OWN copy of every static, so a static set is invisible to the orphan and cannot
    //  stop it. The claim must live somewhere that OUTLIVES the assembly:
    //    • the store hangs off AppDomain.CurrentDomain, which spans reloads; and
    //    • it MUST be a type from a stable assembly. A Hashtable from mscorlib is the same
    //      type to both assemblies; anything declared HERE would be a different type per
    //      load and the cast back would silently fail — which fails OPEN, i.e. straight back
    //      to double-mirroring. That is why this is a non-generic Hashtable, not HashSet<string>.
    //    • the creation lock is an INTERNED string for the same reason: the intern pool is
    //      process-wide, so both assemblies lock the identical object.
    //
    //  ⛔ FAILS CLOSED, deliberately: if the claim cannot be taken we do NOT mirror. A
    //  duplicate order is unrecoverable; a missed mirror is visible and the reconciler
    //  below is built to report it.
    //
    //  v0.1.0i — ported precautionary, not reactive: live has no measured case of this
    //  symptom, but the assembly-reload behavior that causes it is an NT-level trap, not
    //  something specific to main's code, so the same blind spot applies here too.
    // ═════════════════════════════════════════════════════════════════════════
    internal static class MirrorClaim
    {
        private const string SlotKey    = "Sneaky.Copier.MirroredExecutionIds.v1";
        private const int    PruneAbove = 4000;   // bound the store; ids are only useful while fresh

        // Interned → the SAME object in every assembly loaded into this AppDomain.
        private static readonly object CreateGate = string.Intern("Sneaky.Copier.MirrorClaim.CreateGate.v1");

        private static Hashtable Store()
        {
            AppDomain ad = AppDomain.CurrentDomain;
            Hashtable t = ad.GetData(SlotKey) as Hashtable;
            if (t != null) return t;
            lock (CreateGate)
            {
                t = ad.GetData(SlotKey) as Hashtable;      // re-check: another instance may have won
                if (t == null)
                {
                    t = new Hashtable();
                    ad.SetData(SlotKey, t);
                }
                return t;
            }
        }

        /// <summary>
        /// TRUE exactly once per (leader, executionId), for the whole NT process — including
        /// across an assembly reload. Every later caller, in any instance, gets FALSE.
        /// </summary>
        internal static bool TryClaim(string leaderAccount, string executionId)
        {
            if (string.IsNullOrEmpty(executionId)) return false;   // cannot dedupe it ⇒ do not mirror it
            string key = (leaderAccount ?? "?") + "|" + executionId;
            try
            {
                Hashtable t = Store();
                lock (t.SyncRoot)                       // check + set must be ONE atomic step
                {
                    if (t.ContainsKey(key)) return false;
                    if (t.Count >= PruneAbove) t.Clear();
                    t[key] = null;
                    return true;
                }
            }
            catch (Exception) { return false; }         // fail CLOSED — see the header
        }

        /// <summary>Diagnostics only: how many claims the process is currently holding.</summary>
        internal static int Held { get { try { Hashtable t = Store(); lock (t.SyncRoot) return t.Count; } catch { return -1; } } }
    }

    public sealed class CopierConfig
    {
        public string LeaderAccount;                 // PRIMARY leader — the SIGNAL account we mirror FROM.
                                                       // Kept as its own field (not folded into Leaders) so
                                                       // anything reading .LeaderAccount/.Followers directly
                                                       // (a future dashboard Copy tab) keeps compiling and
                                                       // behaving exactly as it does today for a single-leader
                                                       // setup. See v0.1.1 changelog + AllLeaders() below.
        public ProviderPolicy Policy = ProviderPolicy.Warn;
        public bool UseEyeGate = false;              // when true, mirror only ENTRIES SentinelEye qualifies (exits always mirror)
        public List<FollowerConfig> Followers = new List<FollowerConfig>();   // the PRIMARY leader's followers

        // v0.1.1 — EXTRA leaders beyond the primary one, each with its OWN follower list. Deliberately
        // NO count cap enforced anywhere in this file: NinjaTrader's AddOn API does not limit how many
        // Account objects one AddOn instance can subscribe ExecutionUpdate on — the real ceiling is
        // whatever your broker/data-feed CONNECTION TYPE allows simultaneously logged in (unlimited
        // concurrent connections for a Tradovate/NinjaTrader-brokerage connection type; only ONE
        // Rithmic-for-NinjaTrader-Brokerage connection at a time unless Rithmic's own "Unlimited Prop
        // Firm Connections" add-on is active). That's a platform/provider constraint to respect when
        // you actually wire up the accounts in NinjaTrader's Connections — not something to hardcode a
        // smaller limit for here.
        public List<LeaderConfig> Leaders = new List<LeaderConfig>();

        // v0.1.1 — GLOBAL, applies to every follower under every leader (not per-follower, per chat
        // request). Per-instrument: gates on the specific instrument being mirrored, not the whole
        // account — a follower already in a trade on NQ still takes a fresh ES entry normally. See
        // MirrorToFollower's isReducingAction check for the exact semantics: a close/reduce toward
        // flat ALWAYS still mirrors in full; only a fill that would ADD to or, after a zero-crossing
        // close, OPEN a brand-new position while already positioned in that instrument gets skipped.
        public bool RejectIfInTrade = false;

        // 2026-08-15 — BRACKET MIRROR safety net. If the leader's own stop/target can't be detected
        // within this many seconds of a follower's entry filling (leader trades naked, places brackets
        // unusually late, or something about their order stream isn't matching), apply this FIXED tick
        // distance instead so the follower is never left unprotected indefinitely. This is a safety net,
        // not the primary mechanism — the whole point of this feature is to copy the leader's ACTUAL
        // bracket when it's available. 0 disables the fallback entirely (NOT recommended — a follower
        // could then sit open with no bracket at all if the leader's own bracket is never detected).
        public int    BracketFallbackSeconds = 60;
        public double BracketFallbackStopTicks   = 100;   // conservative NQ-scale default — reconsider for other instruments
        public double BracketFallbackTargetTicks = 150;   // matches the ~1.5:1 ratio this session's own TP/Stop grading favored

        // v0.1.1 — unifies the primary leader + every extra into one sequence for internal iteration
        // (Subscribe/Unsubscribe/OnLeaderExecution/OnLeaderOrderUpdate/ReconcileTick all use this
        // instead of touching LeaderAccount/Followers/Leaders separately three different ways).
        internal IEnumerable<LeaderConfig> AllLeaders()
        {
            if (!string.IsNullOrEmpty(LeaderAccount))
                yield return new LeaderConfig { AccountName = LeaderAccount, Followers = Followers };
            if (Leaders != null)
                foreach (LeaderConfig lc in Leaders)
                    if (lc != null && !string.IsNullOrEmpty(lc.AccountName))
                        yield return lc;
        }
    }

    // v0.1.1 — one EXTRA leader (beyond CopierConfig's own primary LeaderAccount/Followers slot),
    // with its own independent follower list. See CopierConfig.Leaders' header for why there is no
    // artificial cap on how many of these can exist.
    public sealed class LeaderConfig
    {
        public string AccountName;
        public List<FollowerConfig> Followers = new List<FollowerConfig>();
    }

    public class SneakyCopierService_v0_1_0 : NinjaTrader.NinjaScript.AddOnBase
    {
        private const string OrderPrefix = "SnkCopy_";     // suite contract: copier order tag

        // ── singleton so a future dashboard/settings window can attach ──
        public static SneakyCopierService_v0_1_0 Instance { get; private set; }

        // Kill-switch + feed-health now live in the shared SentinelCore (suite-wide), not here.
        // Flip via SneakyCore.SetKillSwitch(true, "..."); the mirror gate consults SneakyCore.

        // active config — EMPTY by default → service is inert (safe) until configured.
        private CopierConfig _config;

        private List<Account> _subscribed;      // leader account(s) we've hooked
        private HashSet<string> _seenExecIds;   // dedupe defensive guard on ExecutionUpdate
        private bool _started;
        private readonly object _lock = new object();

        // ── copy-slippage capture (follower fill vs the leader fill price we're replicating) ──
        private List<Account> _followerSubscribed;                  // follower accounts hooked for fill capture
        private HashSet<string> _seenFollowerExecIds;               // dedupe follower ExecutionUpdate
        private readonly Dictionary<string, double> _mirrorLeaderPx // mirror order name → leader fill px (intended)
            = new Dictionary<string, double>(StringComparer.Ordinal);
        // v0.1.1 — sibling to _mirrorLeaderPx, same key (the follower's own mirror order name), same
        // lifetime/bound. Records WHICH leader account this specific mirror order came from, so a
        // follower's OWN entry fill (OnFollowerExecution) — which has no other way to know which of
        // potentially several leaders caused it — can look up the right leader when queuing a bracket,
        // keeping bracket-distance tracking correctly scoped per-leader instead of colliding whenever
        // two leaders trade the same instrument.
        private readonly Dictionary<string, string> _mirrorLeaderAcct
            = new Dictionary<string, string>(StringComparer.Ordinal);

        // ── 2026-08-15 — BRACKET MIRROR: copy the LEADER's own stop/target distance onto followers.
        // The leader's bracket legs are SEPARATE orders from the entry, often placed a few seconds
        // (or more, if a human is doing it by hand) after the entry fills — so this can't be read
        // synchronously inside OnLeaderExecution. Instead: watch the leader account's own OrderUpdate
        // for Stop/Limit orders, record their distance in ticks from the leader's own last entry price,
        // and apply the SAME tick distance to each follower's own entry price once IT fills. If a
        // follower fills before the leader's bracket is visible yet, it's queued in _pendingBrackets
        // and placed retroactively the moment the leader's bracket appears — with a timed fallback so
        // a position is NEVER left unprotected indefinitely (see BracketFallbackSeconds / _fallbackTimer).
        private readonly Dictionary<string, double> _leaderEntryPx     // instrument master symbol → leader's last ENTRY fill price
            = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _leaderStopTicks   // instrument master symbol → leader's current protective-stop distance, in ticks
            = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _leaderTargetTicks // instrument master symbol → leader's current target distance, in ticks
            = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        private sealed class PendingBracket
        {
            public string     FollowerAccount;
            public Instrument Instrument;
            public string     MasterSymbol;
            // v0.1.1 — "leaderAccount|MasterSymbol" (leaderAccount "?" if it couldn't be recovered from
            // _mirrorLeaderAcct). Replaces bare MasterSymbol as the match key so two leaders trading the
            // same instrument track separate bracket distances instead of clobbering each other's.
            public string     BracketKey;
            public OrderAction EntryAction;   // Buy or SellShort — which side the follower is now positioned
            public double     EntryPrice;
            public int        Qty;
            public DateTime   FilledUtc;
        }
        private readonly List<PendingBracket> _pendingBrackets = new List<PendingBracket>();
        private Timer _fallbackTimer;   // periodic sweep — applies BracketFallbackTicks to anything still waiting past BracketFallbackSeconds

        // ── copy-mode refusal: log once per account, not once per fill ──
        private readonly HashSet<string> _orderModeWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── BRACKET-LEG REJECTION WATCHDOG (2026-08-28) ──────────────────────
        // A rejected stop leg (e.g. "sell stop can't be placed above market" — the level computed
        // from the leader's captured EntryPrice had already been crossed by live price by the time
        // the follower's order reached the engine) previously left the follower's position with NO
        // protective order and nothing here ever noticed: unlike a Strategy, an AddOnBase's manually
        // submitted (unmanaged) orders get NO platform-level RealtimeErrorHandling safety net — that
        // mechanism only exists for the managed-order framework strategies use. Dedup by order name
        // so a terminal OrderUpdate that re-fires doesn't submit a second flatten.
        private readonly HashSet<string> _rejectHandled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── POSITION RECONCILER (v0.1.0i) ───────────────────────────────────────
        // Answers the one question neither copy path can answer about itself: is the follower
        // actually where the leader is? A rejected mirror, a partial, a hand-placed trade on the
        // follower, or a restart mid-flight can all drift a follower and nothing here notices.
        //
        // ⭐ TWO CONSECUTIVE DISAGREEMENTS BEFORE IT SPEAKS. A mirror in flight IS a divergence for
        // a moment, so a single sample would cry wolf on every single trade — and an alert that
        // fires on healthy operation is one the operator learns to ignore, which is worse than no
        // alert. Same lesson this suite already applies to every NT state read.
        private Timer _reconcileTimer;
        private readonly Dictionary<string, int> _divergedStreak     // "acct|instrument" → consecutive bad samples
            = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private const int ReconcileSeconds = 30;
        private const int DivergeStreakToAlert = 2;

        // ─────────────────────────────────────────────────────────────────────
        protected override void OnStateChange()
        {
            // base ctor calls this BEFORE our ctor — do not rely on field initializers.
            if (State == State.SetDefaults)
            {
                Name = "SneakyCopierService_v0_1_0";
                Description = "Sentinel Suite — headless multi-account fill-mirror trade copier. "
                            + "Mirrors a leader account's fills to enabled followers (with "
                            + "instrument cross-map + size). Inert until configured. Runs always.";
            }
            else if (State == State.Active)      Start();
            else if (State == State.Terminated)  Stop();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Start — hook the leader account. Idempotent (Active can re-fire).
        // ─────────────────────────────────────────────────────────────────────
        private void Start()
        {
            if (_started) return;
            _started = true;
            Instance = this;

            _subscribed  = new List<Account>();
            _seenExecIds = new HashSet<string>();
            _followerSubscribed = new List<Account>();
            _seenFollowerExecIds = new HashSet<string>();

            // CONFIG PERSISTENCE: reload the last-applied config so a recompile/restart doesn't
            // silently reset the copier to inert (the recompile footgun). Empty if no file yet.
            if (_config == null)
            {
                _config = LoadConfig();
                if (_config == null) _config = new CopierConfig();   // no saved config → inert
                else Log("Loaded persisted config from " + ConfigPath);
            }

            Subscribe();

            // 2026-08-15 — bracket-mirror fallback sweep. Runs every 5s regardless of BracketFallbackSeconds
            // (the sweep interval and the fallback threshold are different things — this just needs to be
            // frequent enough that the actual wait never overshoots the configured threshold by much).
            _fallbackTimer = new Timer(_ => SweepPendingBrackets(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

            // v0.1.0i — the reconciler runs regardless of copy mode: Fill can still drift (a rejected
            // mirror, a partial, a hand-placed follower trade, a restart mid-flight) and nothing noticed
            // before this. Reports only; never places or cancels an order.
            _reconcileTimer = new Timer(ReconcileTick, null, ReconcileSeconds * 1000, ReconcileSeconds * 1000);

            // v0.1.1 — summarize across EVERY configured leader, not just the primary slot.
            int leaderCount = 0, followerCount = 0;
            foreach (LeaderConfig lc0 in _config.AllLeaders()) { leaderCount++; followerCount += lc0.Followers.Count; }
            Log("SneakyCopierService v0.1.1 started. Leaders=" + leaderCount
                + ", followers=" + followerCount
                + ", rejectIfInTrade=" + (_config.RejectIfInTrade ? "on" : "off")
                + ", reconciler every " + ReconcileSeconds + "s"
                + (leaderCount == 0 ? " (inert until a leader is set)." : "."));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Stop — unsubscribe every hooked account.
        // ─────────────────────────────────────────────────────────────────────
        private void Stop()
        {
            if (!_started) return;
            _started = false;
            if (_reconcileTimer != null)
            {
                try { _reconcileTimer.Dispose(); }
                catch (Exception _sx) { SneakyCore.Swallow("SneakyCopier.Stop", _sx); }
                _reconcileTimer = null;
            }
            _divergedStreak.Clear();
            _orderModeWarned.Clear();
            Unsubscribe();
            if (Instance == this) Instance = null;
            Log("SneakyCopierService stopped; leader subscription released, reconciler disposed.");
        }

        // ── (re)subscribe to the configured leader account's executions ──────────
        private void Subscribe()
        {
            Unsubscribe();
            if (_config == null) return;

            // v0.1.1 — every configured leader, primary + extras. See CopierConfig.AllLeaders().
            List<LeaderConfig> leaderConfigs = _config.AllLeaders().ToList();
            if (leaderConfigs.Count == 0) return;

            // every leader account name, for the same-account-as-a-follower guard below — works no
            // matter how many leaders are configured (no cap; see CopierConfig.Leaders' header).
            var leaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LeaderConfig lc0 in leaderConfigs)
                if (!string.IsNullOrEmpty(lc0.AccountName)) leaderNames.Add(lc0.AccountName);

            foreach (LeaderConfig lc in leaderConfigs)
            {
                Account leader = FindAccount(lc.AccountName);
                if (leader == null)
                {
                    Log("WARNING: leader account '" + lc.AccountName + "' not found (yet). "
                        + "Will not mirror until it exists and Reconfigure() is called.");
                    continue;
                }
                leader.ExecutionUpdate += OnLeaderExecution;
                leader.OrderUpdate += OnLeaderOrderUpdate;   // 2026-08-15 — bracket-mirror: watch for the leader's own stop/target orders
                _subscribed.Add(leader);
                Log("Subscribed to leader executions: " + leader.Name);
            }

            // hook enabled AUTO followers too, across EVERY leader — only to CAPTURE their mirror
            // fills (copy slippage), never to act. (Manual followers submit no orders → nothing to
            // capture.)
            if (_followerSubscribed != null)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (LeaderConfig lc in leaderConfigs)
                {
                    foreach (FollowerConfig f in lc.Followers)
                    {
                        if (f == null || !f.Enabled || f.Mode == FollowerMode.Manual || string.IsNullOrEmpty(f.AccountName)) continue;
                        // 2026-09-05 — same leader==follower guard as OnLeaderExecution's mirror loop: don't
                        // even hook a follower account that's actually one of the configured leader accounts.
                        if (leaderNames.Contains(f.AccountName))
                        {
                            Log("REFUSING to subscribe follower '" + f.AccountName + "': same account as a configured leader. Fix Copy.conf.");
                            continue;
                        }
                        if (!seen.Add(f.AccountName)) continue;   // one subscription per account, even if it follows >1 leader
                        Account fa = FindAccount(f.AccountName);
                        if (fa == null) continue;
                        fa.ExecutionUpdate += OnFollowerExecution;
                        fa.OrderUpdate += OnFollowerOrderUpdate;   // 2026-08-28 — bracket-leg rejection watchdog (see OnFollowerOrderUpdate)
                        _followerSubscribed.Add(fa);
                    }
                }
                if (_followerSubscribed.Count > 0) Log("Subscribed to " + _followerSubscribed.Count + " follower account(s) for copy-slippage capture.");
            }

            // ⛔ ORDER MODE IS NOT BUILT YET, AND IT FAILS CLOSED RATHER THAN FALLING BACK.
            // Silently mirroring an Order-mode follower on the Fill path would give the operator a
            // copier that works — just not the one they configured — and nothing would ever say so.
            // Refusing is the honest state: they see it at startup, before a trade, not after one.
            foreach (LeaderConfig lc in leaderConfigs)
                foreach (FollowerConfig f in lc.Followers)
                {
                    if (f == null || !f.Enabled || f.CopyMode != CopyMode.Order) continue;
                    Log("REFUSING follower '" + f.AccountName + "': copy mode ORDER is not implemented "
                        + "(the mirror path and its position reconciler land together — an Order-mode "
                        + "copier without reconciliation diverges invisibly). This follower will NOT be "
                        + "mirrored at all. Set it to fill in Copy.conf to copy it now.");
                }
        }

        private void Unsubscribe()
        {
            if (_subscribed != null)
            {
                foreach (Account a in _subscribed)
                {
                    a.ExecutionUpdate -= OnLeaderExecution;
                    a.OrderUpdate -= OnLeaderOrderUpdate;
                }
                _subscribed.Clear();
            }
            if (_followerSubscribed != null)
            {
                foreach (Account a in _followerSubscribed)
                {
                    a.ExecutionUpdate -= OnFollowerExecution;
                    a.OrderUpdate -= OnFollowerOrderUpdate;
                }
                _followerSubscribed.Clear();
            }
            lock (_lock)
            {
                _mirrorLeaderPx.Clear();
                _leaderEntryPx.Clear();
                _leaderStopTicks.Clear();
                _leaderTargetTicks.Clear();
                _pendingBrackets.Clear();
            }
            if (_fallbackTimer != null) { _fallbackTimer.Dispose(); _fallbackTimer = null; }
        }

        // ── the reconciler (v0.1.0i): does each follower's NET position match the leader's, scaled? ──
        // Reports only. It never places or cancels an order to "fix" a difference: a wrong
        // correction on a real position is far more expensive than a delta the operator is told
        // about.
        private void ReconcileTick(object _)
        {
            try
            {
                CopierConfig cfg = _config;
                if (cfg == null) return;

                // v0.1.1 — reconcile against EVERY configured leader, each against only its OWN
                // followers (a follower's expected position is always relative to the specific leader
                // it's actually assigned to under, never mixed across leaders).
                foreach (LeaderConfig leaderCfg in cfg.AllLeaders())
                {
                Account leader = FindAccount(leaderCfg.AccountName);
                if (leader == null) continue;

                foreach (FollowerConfig f in leaderCfg.Followers)
                {
                    if (f == null || !f.Enabled || string.IsNullOrEmpty(f.AccountName)) continue;
                    if (f.Mode == FollowerMode.Manual) continue;   // we do not place these; not ours to reconcile
                    Account fa = FindAccount(f.AccountName);
                    if (fa == null || !IsConnected(fa)) continue;

                    // ⛔ THE BLIND SPOT THIS SWEEP AVOIDS, AND IT IS THE WORST CASE THERE IS.
                    //    Iterating only `leader.Positions` would derive everything from it — but NT
                    //    REMOVES a flat position from Account.Positions, so a FLAT LEADER iterates
                    //    ZERO times and every follower gets skipped entirely, right when a stray
                    //    follower position (leader flat, follower still holding) is most worth
                    //    catching. ⇒ SWEEP THE UNION OF BOTH SIDES. A symbol is reconcilable if
                    //    EITHER book holds it; expected is 0 when the leader is flat, which is the
                    //    whole point of the check.
                    var syms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (leader.Positions != null)
                        foreach (Position lp in leader.Positions)
                        {
                            if (lp == null || lp.Instrument == null || lp.Instrument.MasterInstrument == null) continue;
                            string ls = lp.Instrument.MasterInstrument.Name;
                            if (string.IsNullOrEmpty(ls)) continue;
                            string ts = ls;
                            InstrumentMapEntry m0;
                            if (f.InstrumentMap != null && f.InstrumentMap.TryGetValue(ls, out m0) && m0 != null)
                                ts = m0.TargetSymbol;
                            syms[ts] = ls;                       // target symbol -> the leader symbol it maps from
                        }
                    if (fa.Positions != null)
                        foreach (Position fp in fa.Positions)
                        {
                            if (fp == null || fp.Instrument == null || fp.Instrument.MasterInstrument == null) continue;
                            string ts = fp.Instrument.MasterInstrument.Name;
                            if (string.IsNullOrEmpty(ts) || syms.ContainsKey(ts)) continue;
                            // A follower-only symbol. Recover the LEADER symbol it should have come from,
                            // so ratio/multiplier still apply; default to same-symbol when unmapped.
                            string ls = ts;
                            if (f.InstrumentMap != null)
                                foreach (var kv in f.InstrumentMap)
                                    if (kv.Value != null && string.Equals(kv.Value.TargetSymbol, ts,
                                                                          StringComparison.OrdinalIgnoreCase))
                                    { ls = kv.Key; break; }
                            syms[ts] = ls;
                        }

                    // Keys examined this tick. Anything with a live streak that is NOT here has left
                    // BOTH books — see the sweep-back below.
                    var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var pair in syms)
                    {
                        string targetSym = pair.Key;
                        string leaderSym = pair.Value;
                        // v0.1.1 — leader-scoped key (was just f.AccountName + "|" + targetSym): if the
                        // same follower account is ever listed under more than one leader, its streak
                        // per leader stays independent instead of one leader's reconcile overwriting
                        // the other's.
                        visited.Add(leaderCfg.AccountName + "|" + f.AccountName + "|" + targetSym);

                        double ratio = 1.0;
                        InstrumentMapEntry me;
                        if (f.InstrumentMap != null && f.InstrumentMap.TryGetValue(leaderSym, out me) && me != null)
                            ratio = me.SizeRatio;

                        int leaderNet = 0;
                        if (leader.Positions != null)
                            foreach (Position lp in leader.Positions)
                            {
                                if (lp == null || lp.Instrument == null || lp.Instrument.MasterInstrument == null) continue;
                                if (!string.Equals(lp.Instrument.MasterInstrument.Name, leaderSym,
                                                   StringComparison.OrdinalIgnoreCase)) continue;
                                leaderNet += SignedQty(lp);      // 0 when flat — NT simply has no row
                            }

                        int expected = (int)Math.Round(leaderNet * ratio * f.Multiplier);
                        int actual = 0;
                        if (fa.Positions != null)
                            foreach (Position fp in fa.Positions)
                            {
                                if (fp == null || fp.Instrument == null || fp.Instrument.MasterInstrument == null) continue;
                                if (!string.Equals(fp.Instrument.MasterInstrument.Name, targetSym,
                                                   StringComparison.OrdinalIgnoreCase)) continue;
                                actual += SignedQty(fp);
                            }

                        string key = leaderCfg.AccountName + "|" + f.AccountName + "|" + targetSym;
                        if (expected == actual)
                        {
                            int had;
                            if (_divergedStreak.TryGetValue(key, out had) && had >= DivergeStreakToAlert)
                                Log("RECONCILED: " + key + " is back in line at " + actual + ".");
                            _divergedStreak.Remove(key);
                            continue;
                        }

                        int streak;
                        _divergedStreak.TryGetValue(key, out streak);
                        streak++;
                        _divergedStreak[key] = streak;
                        if (streak != DivergeStreakToAlert) continue;   // speak once, on crossing
                        Log("\U0001F534 POSITION DIVERGENCE" + (leaderNet == 0 && actual != 0
                                ? " (LEADER IS FLAT — the follower holds a position the leader never took)" : "")
                            + ": follower '" + f.AccountName + "' holds " + actual
                            + " " + targetSym + " but the leader implies " + expected
                            + " (leader " + leaderNet + " " + leaderSym + " × ratio " + ratio
                            + " × mult " + f.Multiplier + "). Seen on " + streak
                            + " consecutive checks " + ReconcileSeconds + "s apart. NOT auto-corrected — "
                            + "reconciling by hand is the operator's call.");
                        try { SneakyCore.Log("Copy", "DIVERGENCE " + key + " actual=" + actual + " expected=" + expected); }
                        catch (Exception _sx) { SneakyCore.Swallow("SneakyCopier.Reconcile", _sx); }
                    }

                    // ⛔ RECOVERY-INVISIBLE FIX: a divergence that resolves via BOTH sides going flat
                    //    removes the symbol from both books, so the union sweep above never revisits
                    //    it — RECONCILED would never be announced and the streak entry would leak
                    //    forever. A recovery you cannot see is the same defect as a divergence you
                    //    cannot see; both leave the operator reading silence as health.
                    string pfx = leaderCfg.AccountName + "|" + f.AccountName + "|";
                    foreach (string stale in _divergedStreak.Keys
                                 .Where(k => k.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)
                                             && !visited.Contains(k)).ToList())
                    {
                        int had2;
                        if (_divergedStreak.TryGetValue(stale, out had2) && had2 >= DivergeStreakToAlert)
                            Log("RECONCILED: " + stale + " is back in line at 0 — the symbol left BOTH "
                                + "books (leader flat and follower flat).");
                        _divergedStreak.Remove(stale);
                    }
                }
                }   // end foreach leaderCfg (v0.1.1)
            }
            catch (Exception ex) { Log("ReconcileTick error: " + ex.Message); }
        }

        // Net signed quantity of a position: long positive, short negative, flat zero.
        private static int SignedQty(Position p)
        {
            if (p == null || p.MarketPosition == MarketPosition.Flat) return 0;
            return p.MarketPosition == MarketPosition.Long ? p.Quantity : -p.Quantity;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  PUBLIC CONFIG API (a future dashboard / JSON loader calls these).
        //  Mirrors MAECaptureService's pattern of the service owning the truth.
        // ─────────────────────────────────────────────────────────────────────
        public void Reconfigure(CopierConfig cfg)
        {
            lock (_lock)
            {
                _config = cfg ?? new CopierConfig();
                if (_seenExecIds != null) _seenExecIds.Clear();
                if (_started) Subscribe();   // re-hook the (possibly new) leader
            }
            int leaderCount = 0, followerCount = 0;
            foreach (LeaderConfig lc0 in _config.AllLeaders()) { leaderCount++; followerCount += lc0.Followers.Count; }
            Log("Reconfigured. Leaders=" + leaderCount + ", followers=" + followerCount
                + ", policy=" + _config.Policy
                + ", rejectIfInTrade=" + (_config.RejectIfInTrade ? "on" : "off"));
        }

        public CopierConfig CurrentConfig { get { return _config; } }

        // ─────────────────────────────────────────────────────────────────────
        //  CONFIG PERSISTENCE — a simple, hand-editable text file (NOT JSON, so no
        //  serializer dependency + easy to read/edit outside NT). One key per line:
        //      leader=<account>
        //      policy=Off|Warn|Block
        //      eyeGate=on|off
        //      rejectIfInTrade=on|off      (v0.1.1 — GLOBAL: applies to every follower under every
        //                                   leader. See the v0.1.1 changelog entry above for the
        //                                   Sell/BuyToCover-vs-Buy/SellShort "reducing action" logic
        //                                   this uses so a follower is never trapped unable to exit.)
        //      follower=<account>|<on|off|manual>|<multiplier>|<mapDSL>|<fill|order>
        //        on = auto-submit · manual = emit a place-by-hand ASSIST ticket (no order) · off = disabled
        //  where mapDSL reuses the dashboard's "GC>MGC*10, CL>MCL" grammar.
        //
        //  MULTIPLE LEADERS (v0.1.1): the FIRST leader= line is the primary leader (back-compat
        //  slot). Every leader= line AFTER that OPENS A NEW BLOCK — every follower= line that
        //  follows it belongs to THAT leader, until the next leader= line or end of file. No cap on
        //  how many leader blocks a file may have; NT8 itself imposes none — the real ceiling, if
        //  any, is your broker/data-feed CONNECTION TYPE (e.g. bare Rithmic-for-NT-Brokerage allows
        //  one connection at a time unless you buy Rithmic's "Unlimited Prop Firm Connections"
        //  add-on; Tradovate/NT-brokerage connections already allow unlimited concurrent accounts).
        //  Example, two leaders:
        //      leader=Sim101
        //      follower=Sim102|on|1|
        //      leader=Sim201
        //      follower=Sim202|on|1|
        // ─────────────────────────────────────────────────────────────────────
        public static string ConfigPath { get { return Path.Combine(SneakyCore.SettingsDir, "Copy.conf"); } }

        // v0.1.1 — factored out of SaveConfig so the primary leader's followers and each additional
        // leader's followers are written with identical formatting.
        private static void AppendFollowerLines(StringBuilder sb, List<FollowerConfig> followers)
        {
            if (followers == null) return;
            foreach (FollowerConfig f in followers)
            {
                if (f == null || string.IsNullOrEmpty(f.AccountName)) continue;
                sb.AppendLine("follower=" + f.AccountName
                    + "|" + (!f.Enabled ? "off" : (f.Mode == FollowerMode.Manual ? "manual" : "on"))
                    + "|" + f.Multiplier.ToString(CultureInfo.InvariantCulture)
                    + "|" + MapToDsl(f.InstrumentMap)
                    // field 5 = copy mode (v0.1.0i). APPENDED, never inserted: a Copy.conf written
                    // by an earlier version has four fields and must keep loading, defaulting to Fill.
                    + "|" + (f.CopyMode == CopyMode.Order ? "order" : "fill"));
            }
        }

        public static void SaveConfig(CopierConfig cfg)
        {
            if (cfg == null) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Sentinel Copier config — auto-saved by the dashboard. Safe to hand-edit.");
                sb.AppendLine("leader=" + (cfg.LeaderAccount ?? ""));
                sb.AppendLine("policy=" + cfg.Policy);
                sb.AppendLine("eyeGate=" + (cfg.UseEyeGate ? "on" : "off"));
                // v0.1.1 — global gate: reject an entry/add on a follower already in that instrument's
                // trade. Written next to eyeGate= using the same on/off convention.
                sb.AppendLine("rejectIfInTrade=" + (cfg.RejectIfInTrade ? "on" : "off"));
                AppendFollowerLines(sb, cfg.Followers);
                // v0.1.1 — additional leaders. Each "leader=" line after the first OPENS A NEW BLOCK:
                // every "follower=" line that follows it belongs to THAT leader, until the next
                // "leader=" line (or EOF). No cap on how many of these blocks exist — see the
                // changelog entry above for why (NT8 itself has none; only the broker connection
                // type does, and that's the operator's problem, not this file's).
                if (cfg.Leaders != null)
                {
                    foreach (LeaderConfig lc in cfg.Leaders)
                    {
                        if (lc == null || string.IsNullOrEmpty(lc.AccountName)) continue;
                        sb.AppendLine("leader=" + lc.AccountName);
                        AppendFollowerLines(sb, lc.Followers);
                    }
                }
                File.WriteAllText(ConfigPath, sb.ToString(), Encoding.UTF8);
                SneakyCore.Log("Copy", "Config saved → " + ConfigPath);
            }
            catch (Exception ex) { SneakyCore.Log("Copy", "SaveConfig error: " + ex.Message); }
        }

        public static CopierConfig LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return null;
                var cfg = new CopierConfig();
                // v0.1.1 — which Followers list a "follower=" line lands in. The FIRST "leader=" line
                // (even an empty one, e.g. "leader=" to disable the primary) targets the primary slot
                // (cfg.LeaderAccount / cfg.Followers, for back-compat with every file written before
                // this version). Each SUBSEQUENT non-empty "leader=" line opens a new LeaderConfig
                // block, and every "follower=" line after it targets THAT block until the next one.
                bool sawFirstLeader = false;
                List<FollowerConfig> currentTarget = cfg.Followers;
                foreach (string raw in File.ReadAllLines(ConfigPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();

                    if (key.Equals("leader", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!sawFirstLeader)
                        {
                            sawFirstLeader = true;
                            cfg.LeaderAccount = val.Length > 0 ? val : null;
                            currentTarget = cfg.Followers;
                        }
                        else if (val.Length > 0)
                        {
                            var lc = new LeaderConfig { AccountName = val };
                            cfg.Leaders.Add(lc);
                            currentTarget = lc.Followers;
                        }
                        // an EMPTY additional "leader=" line (after the first) is not a valid block
                        // opener — ignore it rather than silently swallow whatever follower= lines
                        // come after into the wrong bucket.
                    }
                    else if (key.Equals("policy", StringComparison.OrdinalIgnoreCase))
                    {
                        ProviderPolicy p;
                        if (Enum.TryParse(val, true, out p)) cfg.Policy = p;
                    }
                    else if (key.Equals("eyeGate", StringComparison.OrdinalIgnoreCase))
                        cfg.UseEyeGate = val.Equals("on", StringComparison.OrdinalIgnoreCase) || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                    else if (key.Equals("rejectIfInTrade", StringComparison.OrdinalIgnoreCase))
                        cfg.RejectIfInTrade = val.Equals("on", StringComparison.OrdinalIgnoreCase) || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                    else if (key.Equals("follower", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = val.Split('|');
                        if (parts.Length < 1 || parts[0].Trim().Length == 0) continue;
                        var f = new FollowerConfig { AccountName = parts[0].Trim() };
                        if (parts.Length > 1)
                        {
                            string mode = parts[1].Trim();
                            if (mode.Equals("off", StringComparison.OrdinalIgnoreCase)) f.Enabled = false;
                            else if (mode.Equals("manual", StringComparison.OrdinalIgnoreCase)) { f.Enabled = true; f.Mode = FollowerMode.Manual; }
                            else { f.Enabled = true; f.Mode = FollowerMode.Auto; }   // on / auto
                        }
                        double m;
                        if (parts.Length > 2 && double.TryParse(parts[2].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out m)) f.Multiplier = m;
                        if (parts.Length > 3) ParseMapDsl(parts[3], f.InstrumentMap);
                        // Absent field 5 (any pre-v0.1.0i file) => Fill. An UNRECOGNISED value is NOT
                        // silently treated as Fill: it is logged and left at Fill, because a config we
                        // cannot read is a config the operator believes says something else.
                        if (parts.Length > 4)
                        {
                            string cm = parts[4].Trim();
                            if (cm.Equals("order", StringComparison.OrdinalIgnoreCase)) f.CopyMode = CopyMode.Order;
                            else if (!cm.Equals("fill", StringComparison.OrdinalIgnoreCase) && cm.Length > 0)
                                SneakyCore.Log("Copy", "LoadConfig: follower '" + f.AccountName
                                    + "' has an unrecognised copy mode '" + cm + "' — using Fill. "
                                    + "Valid values: fill, order.");
                        }
                        currentTarget.Add(f);
                    }
                }
                return cfg;
            }
            catch (Exception ex) { SneakyCore.Log("Copy", "LoadConfig error: " + ex.Message); return null; }
        }

        // Canonical instrument-map DSL parse/format (the dashboard reuses these).
        //   "GC>MGC*10, CL>MCL"  ⇄  { GC:{MGC,10}, CL:{MCL,1} }
        public static void ParseMapDsl(string dsl, Dictionary<string, InstrumentMapEntry> into)
        {
            if (into == null || string.IsNullOrWhiteSpace(dsl)) return;
            foreach (string part in dsl.Split(','))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                int gt = p.IndexOf('>');
                if (gt <= 0) continue;
                string left = p.Substring(0, gt).Trim();
                string right = p.Substring(gt + 1).Trim();
                double ratio = 1.0;
                int star = right.IndexOf('*');
                if (star >= 0)
                {
                    double.TryParse(right.Substring(star + 1).Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out ratio);
                    right = right.Substring(0, star).Trim();
                }
                if (left.Length == 0 || right.Length == 0) continue;
                into[left] = new InstrumentMapEntry { TargetSymbol = right, SizeRatio = ratio <= 0 ? 1.0 : ratio };
            }
        }

        public static string MapToDsl(Dictionary<string, InstrumentMapEntry> map)
        {
            if (map == null || map.Count == 0) return "";
            var parts = new List<string>();
            foreach (var kv in map)
                parts.Add(kv.Key + ">" + kv.Value.TargetSymbol
                    + (Math.Abs(kv.Value.SizeRatio - 1.0) > 1e-9 ? "*" + kv.Value.SizeRatio.ToString(CultureInfo.InvariantCulture) : ""));
            return string.Join(", ", parts);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  MIRROR GATE — the single choke point. kill-switch now; feed-health next.
        // ─────────────────────────────────────────────────────────────────────
        // v0.1.1 — takes the firing leader's Account directly instead of re-deriving "the" leader
        // from _config.LeaderAccount, since there can be more than one configured leader now. The
        // caller (OnLeaderExecution) already has exec.Account, which IS the leader that just fired.
        private bool CanMirror(Instrument instr, Account leader, out string reason)
        {
            // Global kill-switch + PER-INSTRUMENT scoped kill (v0.1.0e) + leader feed health. Scoping
            // by the leader's instrument means a lagging GC feed halts only GC mirrors, not ES/NQ.
            return SneakyCore.CanActInstrument(instr != null ? instr.FullName : null, leader, out reason);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  LEADER FILL → mirror to followers. This is the whole engine.
        // ─────────────────────────────────────────────────────────────────────
        private void OnLeaderExecution(object sender, ExecutionEventArgs e)
        {
            try
            {
                var cfg = _config;
                if (cfg == null) return;

                Execution exec = e.Execution;
                if (exec == null || exec.Account == null || exec.Order == null) return;

                // v0.1.1 — which CONFIGURED leader is this? (was a single string-equality check against
                // cfg.LeaderAccount; now a lookup across every leader, since there can be more than one).
                // Only a fill from an account that's actually a configured leader drives copying.
                LeaderConfig firingLeader = null;
                foreach (LeaderConfig lc in cfg.AllLeaders())
                    if (string.Equals(exec.Account.Name, lc.AccountName, StringComparison.OrdinalIgnoreCase))
                    { firingLeader = lc; break; }
                if (firingLeader == null) return;

                // act on FILLS only (entry AND exit both arrive here → net stays synced)
                OrderState st = exec.Order.OrderState;
                if (st != OrderState.Filled && st != OrderState.PartFilled) return;

                int qty = exec.Quantity;                 // qty of THIS event → partials handled
                if (qty <= 0) return;

                // DEDUPE — PROCESS-WIDE, not per-instance (v0.1.0i). A per-instance HashSet cannot see
                // another instance's mirror if two are ever alive at once (assembly-reload orphan) — see
                // MirrorClaim's header for why this is a real, measured risk and not just theory.
                // BUGFIX (2026-08-22, P1 finding #2): this used to be gated on `exec.ExecutionId != null`,
                // which skipped BOTH the process-wide MirrorClaim dedupe and the local _seenExecIds add
                // entirely whenever ExecutionId was null/empty — exactly the case MirrorClaim.TryClaim is
                // explicitly designed to fail closed on (see its header). That fail-closed logic never
                // ran. A null/empty id is now its own explicit, always-refuse branch (logged, consistent
                // with this file's Log()/SneakyCore.Swallow conventions) instead of a silent exemption.
                if (string.IsNullOrEmpty(exec.ExecutionId))
                {
                    Log("Mirror BLOCKED (missing ExecutionId — cannot dedupe) — leader fill " + qty + " "
                        + exec.Instrument.FullName + " not copied.");
                    return;
                }
                // v0.1.1 — was cfg.LeaderAccount; MirrorClaim already composite-keys on (leader,
                // executionId), so passing the ACTUAL firing account (not always the same "the
                // leader") is what makes that composite key correct once there's more than one.
                if (!MirrorClaim.TryClaim(exec.Account.Name, exec.ExecutionId)) return;
                lock (_lock) { _seenExecIds.Add(exec.ExecutionId); }   // local copy, diagnostics only

                string reason;
                if (!CanMirror(exec.Instrument, exec.Account, out reason))
                {
                    Log("Mirror BLOCKED (" + reason + ") — leader fill " + qty + " "
                        + exec.Instrument.FullName + " not copied.");
                    return;
                }

                bool leaderIsBuy = exec.Order.OrderAction == OrderAction.Buy
                                || exec.Order.OrderAction == OrderAction.BuyToCover;
                string leaderSym = exec.Instrument.MasterInstrument.Name;
                // v0.1.1 — leader-scoped key for the bracket-tracking dictionaries below, so two
                // leaders trading the same instrument don't clobber each other's tracked distance.
                string leaderKey = exec.Account.Name + "|" + leaderSym;

                foreach (FollowerConfig f in firingLeader.Followers)
                {
                    if (f == null || !f.Enabled || string.IsNullOrEmpty(f.AccountName)) continue;
                    // 2026-09-05 — GUARD ADDED: nothing here previously stopped a follower entry whose
                    // AccountName is the SAME account as a configured leader (a Copy.conf copy-paste
                    // error, or a template reused across accounts) from mirroring a leader's own fill
                    // back onto itself, doubling size on every trade. Fails CLOSED and loud, same
                    // discipline as the ORDER-mode refusal in Subscribe() above. v0.1.1 — checks EVERY
                    // configured leader, not just the one that just fired, so a follower accidentally
                    // matching a DIFFERENT leader's account is still caught.
                    bool isAnyLeader = false;
                    foreach (LeaderConfig lc2 in cfg.AllLeaders())
                        if (string.Equals(f.AccountName, lc2.AccountName, StringComparison.OrdinalIgnoreCase))
                        { isAnyLeader = true; break; }
                    if (isAnyLeader)
                    {
                        Log("REFUSING follower '" + f.AccountName + "': same account as a configured leader"
                            + " — this would mirror a leader's own fill onto itself. Fix Copy.conf.");
                        continue;
                    }
                    MirrorToFollower(f, exec, leaderSym, leaderIsBuy, qty);
                }

                // 2026-08-15 — BRACKET MIRROR: track the leader's own entry price so a bracket order
                // that shows up later (via OnLeaderOrderUpdate) can be measured as a tick distance from
                // it. Check the leader's OWN resulting position, same pattern as ResolveFollowerAction
                // uses for followers: flat afterward → this fill was (part of) an exit, so clear the
                // tracked state for this symbol (a later new entry should get a FRESH bracket read, not
                // stale ticks from the trade that just closed). Non-flat → treat as entry, record price.
                MarketPosition leaderMp = MarketPosition.Flat;
                try
                {
                    if (exec.Account.Positions != null)
                    {
                        foreach (Position p in exec.Account.Positions)
                        {
                            if (p != null && p.Instrument == exec.Instrument) { leaderMp = p.MarketPosition; break; }
                        }
                    }
                }
                catch { /* leave Flat — treated as exit below, which just clears tracked state; safe default */ }

                lock (_lock)
                {
                    if (leaderMp == MarketPosition.Flat)
                    {
                        _leaderEntryPx.Remove(leaderKey);
                        _leaderStopTicks.Remove(leaderKey);
                        _leaderTargetTicks.Remove(leaderKey);
                    }
                    else
                    {
                        _leaderEntryPx[leaderKey] = exec.Price;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("OnLeaderExecution error: " + ex.Message);
            }
        }

        // 2026-08-15 — BRACKET MIRROR: the leader's stop/target are SEPARATE orders from the entry,
        // often placed seconds (or, for a human placing them by hand, much longer) after the entry
        // fires OnLeaderExecution above — so this can't be read synchronously there. Watch the leader
        // account's own order stream instead, and whenever a Stop or Limit order shows up WORKING for
        // an instrument we have a tracked entry price for, record its distance in ticks. Fires on every
        // update, not just the first — if the leader moves their stop/target, the tracked distance
        // updates too, though only NEW follower fills benefit (this doesn't reach back and adjust a
        // bracket already placed on a follower).
        private void OnLeaderOrderUpdate(object sender, OrderEventArgs e)
        {
            try
            {
                var cfg = _config;
                if (cfg == null) return;

                Order o = e.Order;
                if (o == null || o.Account == null || o.Instrument == null) return;
                // v0.1.1 — was a single string-equality check against cfg.LeaderAccount; now checks
                // against every configured leader, since there can be more than one.
                bool isConfiguredLeader = false;
                foreach (LeaderConfig lc in cfg.AllLeaders())
                    if (string.Equals(o.Account.Name, lc.AccountName, StringComparison.OrdinalIgnoreCase))
                    { isConfiguredLeader = true; break; }
                if (!isConfiguredLeader) return;

                // only actively-working orders — a filled/cancelled one is no longer "the current bracket"
                if (o.OrderState != OrderState.Working && o.OrderState != OrderState.Accepted) return;

                bool isStopLeg   = o.OrderType == OrderType.StopMarket || o.OrderType == OrderType.StopLimit;
                bool isTargetLeg = o.OrderType == OrderType.Limit;
                if (!isStopLeg && !isTargetLeg) return;

                string sym = o.Instrument.MasterInstrument.Name;
                // v0.1.1 — leader-scoped key, matching OnLeaderExecution's leaderKey, so this leader's
                // own bracket distance can't be read/written under a different leader's tracked entry.
                string leaderKey = o.Account.Name + "|" + sym;
                double entryPx;
                lock (_lock) { if (!_leaderEntryPx.TryGetValue(leaderKey, out entryPx) || entryPx <= 0) return; }

                double tick = o.Instrument.MasterInstrument != null ? o.Instrument.MasterInstrument.TickSize : 0;
                if (tick <= 0) return;

                double legPrice = isStopLeg ? (o.StopPrice != 0 ? o.StopPrice : o.LimitPrice) : o.LimitPrice;
                if (legPrice <= 0) return;

                double distanceTicks = Math.Abs(entryPx - legPrice) / tick;
                if (distanceTicks <= 0) return;

                lock (_lock)
                {
                    if (isStopLeg) _leaderStopTicks[leaderKey]   = distanceTicks;
                    else           _leaderTargetTicks[leaderKey] = distanceTicks;
                }
                Log("Leader bracket leg detected \u25b6 " + o.Account.Name + " " + sym + " " + (isStopLeg ? "stop" : "target")
                    + " " + distanceTicks.ToString("0.#") + " ticks from entry " + entryPx.ToString("0.####"));

                // a follower may already be filled and sitting in _pendingBrackets waiting on exactly
                // this leg — place it now rather than waiting for the fallback timer.
                TryPlacePendingBrackets(leaderKey);
            }
            catch (Exception ex) { Log("OnLeaderOrderUpdate error: " + ex.Message); }
        }

        // 2026-08-28 — BRACKET-LEG REJECTION WATCHDOG. PlaceFollowerBracket computes the follower's
        // stop/target price from the LEADER's captured EntryPrice (see PendingBracket.EntryPrice),
        // which can already be stale by the time the order reaches the engine — most visibly when
        // SweepPendingBrackets fires the FALLBACK bracket after waiting BracketFallbackSeconds for the
        // leader's own bracket to show up, but it can happen on the immediate path too (fill → order
        // round trip). If price has moved enough in that window, the computed stop lands on the wrong
        // side of the market and NinjaTrader rejects it outright ("sell stop can't be placed above the
        // market" / equivalent for a short). Nothing else in this file watches FOLLOWER order state
        // (only the leader's, for bracket-mirroring — see OnLeaderOrderUpdate above), so a rejected
        // stop leg previously left the follower's position with NO protective order and no alert.
        // This is a materially different situation from a Strategy's managed orders: NinjaTrader's own
        // RealtimeErrorHandling (stop/cancel/close) only backstops the MANAGED order framework — an
        // AddOn's unmanaged CreateOrder/Submit orders (like PlaceFollowerBracket's) get no such safety
        // net from the platform. So, unlike SneakyTrendStrategy.OnOrderUpdate (deliberately diagnostics
        // -only as of v1.0.4, because an extra flatten there raced NinjaTrader's own), this handler is
        // the ONLY thing that will ever protect a follower left naked by a rejected bracket leg — it
        // must actually act, not just log.
        private void OnFollowerOrderUpdate(object sender, OrderEventArgs e)
        {
            try
            {
                Order o = e.Order;
                if (o == null || o.Account == null || o.Instrument == null || o.Name == null) return;
                if (!o.Name.StartsWith(OrderPrefix + "stop_", StringComparison.Ordinal)) return;   // only the protective leg

                bool rejected = o.OrderState == OrderState.Rejected
                                || (o.OrderState == OrderState.Cancelled && e.Error != ErrorCode.NoError);
                if (!rejected) return;

                lock (_lock) { if (!_rejectHandled.Add(o.Name)) return; }   // one flatten per rejected order

                Log("🔴 CRITICAL: follower stop-leg REJECTED ▶ " + o.Account.Name + " " + o.Instrument.FullName
                    + " (order '" + o.Name + "', error " + e.Error + ") — this follower may now be UNPROTECTED. "
                    + "Attempting an emergency flatten now.");

                // find the follower's current position in this instrument — the rejected stop tells us
                // it's naked, but not which side/size (the sibling target order may itself have raced
                // ahead or been cancelled by the same OCO group in the meantime).
                MarketPosition mp = MarketPosition.Flat;
                int qty = 0;
                try
                {
                    if (o.Account.Positions != null)
                        foreach (Position p in o.Account.Positions)
                            if (p != null && p.Instrument == o.Instrument && p.MarketPosition != MarketPosition.Flat)
                            { mp = p.MarketPosition; qty = Math.Abs(p.Quantity); break; }
                }
                catch (Exception _sx) { SneakyCore.Swallow("SneakyCopier.OnFollowerOrderUpdate.Positions", _sx); }

                if (mp == MarketPosition.Flat || qty <= 0)
                {
                    Log("Bracket-leg reject on " + o.Account.Name + " " + o.Instrument.FullName
                        + " but the account is already flat there — nothing to protect. No action taken.");
                    return;
                }

                OrderAction flattenAction = mp == MarketPosition.Long ? OrderAction.Sell : OrderAction.BuyToCover;
                string flattenName = OrderPrefix + "emrgflat_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                Order flatten = o.Account.CreateOrder(
                    o.Instrument, flattenAction, OrderType.Market, OrderEntry.Automated, TimeInForce.Day,
                    qty, 0, 0, string.Empty, flattenName, Core.Globals.MaxDate, null);
                o.Account.Submit(new[] { flatten });

                Log("EMERGENCY FLATTEN submitted ▶ " + o.Account.Name + " " + o.Instrument.FullName
                    + " " + flattenAction + " " + qty + " (order '" + flattenName + "') — was left unprotected "
                    + "by rejected stop '" + o.Name + "'.");

                try { SneakyCore.Ledger.Action("bracket-reject-flatten", o.Account.Name,
                    o.Instrument.FullName + " " + flattenAction + " " + qty + " after rejected stop " + o.Name, null);
                } catch (Exception _sx) { SneakyCore.Swallow("SneakyCopier.OnFollowerOrderUpdate.Ledger", _sx); }

                // the sibling target (same OCO group) is now orphaned relative to a position we just
                // closed by hand — cancel it so it doesn't sit there and open a fresh, unintended
                // position if price later trades through it.
                if (!string.IsNullOrEmpty(o.Oco))
                {
                    try
                    {
                        var toCancel = new List<Order>();
                        foreach (Order sib in o.Account.Orders)
                            if (sib != null && sib != o && sib.Oco == o.Oco
                                && (sib.OrderState == OrderState.Working || sib.OrderState == OrderState.Accepted))
                                toCancel.Add(sib);
                        if (toCancel.Count > 0)
                        {
                            o.Account.Cancel(toCancel.ToArray());
                            Log("Cancelled " + toCancel.Count + " orphaned sibling order(s) sharing OCO '" + o.Oco + "'.");
                        }
                    }
                    catch (Exception _sx) { SneakyCore.Swallow("SneakyCopier.OnFollowerOrderUpdate.CancelSibling", _sx); }
                }
            }
            catch (Exception ex) { Log("OnFollowerOrderUpdate error: " + ex.Message); }
        }

        // A follower's mirror order FILLED → record copy slippage (follower fill vs the leader price we
        // replicated). CAPTURE ONLY — never acts on the account. Intended = the leader fill price
        // correlated at mirror-submit time; 0/unknown → slip omitted (still logged as a fill).
        private void OnFollowerExecution(object sender, ExecutionEventArgs e)
        {
            try
            {
                Execution exec = e.Execution;
                if (exec == null || exec.Order == null || exec.Instrument == null) return;
                if (!IsCopierOrder(exec.Order.Name)) return;   // only our SnkCopy_ mirror orders

                OrderState st = exec.Order.OrderState;
                if (st != OrderState.Filled && st != OrderState.PartFilled) return;
                int qty = exec.Quantity;
                if (qty <= 0) return;

                if (!string.IsNullOrEmpty(exec.ExecutionId))
                {
                    lock (_lock)
                    {
                        if (_seenFollowerExecIds.Count > 4000) _seenFollowerExecIds.Clear();
                        if (!_seenFollowerExecIds.Add(exec.ExecutionId)) return;
                    }
                }

                double leaderPx;
                lock (_lock) { if (!_mirrorLeaderPx.TryGetValue(exec.Order.Name, out leaderPx)) leaderPx = 0; }

                double tick = exec.Instrument.MasterInstrument != null ? exec.Instrument.MasterInstrument.TickSize : 0;
                SneakyCore.Ledger.Fill(
                    exec.Account != null ? exec.Account.Name : "?",
                    exec.Instrument.FullName, exec.Order.OrderAction.ToString(), qty, leaderPx, exec.Price, tick, "Copier:mirror");

                // 2026-08-15 — BRACKET MIRROR: only a follower's ENTRY mirror order (Market, Buy/SellShort)
                // should queue a new bracket. A bracket LEG filling (Stop/Limit — the position closing out)
                // arrives here too since it's also a SnkCopy_ order on the same subscribed account, and
                // must NOT re-trigger this — that would try to bracket a position that's already flat.
                bool isEntryFill = exec.Order.OrderType == OrderType.Market
                    && (exec.Order.OrderAction == OrderAction.Buy || exec.Order.OrderAction == OrderAction.SellShort);
                if (isEntryFill)
                {
                    // v0.1.1 — recover WHICH leader's fill this mirror order came from (same map
                    // SubmitMirrorLeg wrote _mirrorLeaderPx into, by the same order-name key) so the
                    // bracket queued below tracks against THAT leader's own stop/target distance, not
                    // whichever leader happened to trade this symbol most recently.
                    string leaderAcct;
                    lock (_lock) { if (!_mirrorLeaderAcct.TryGetValue(exec.Order.Name, out leaderAcct)) leaderAcct = "?"; }
                    QueueBracket(exec.Account, exec.Instrument, exec.Order.OrderAction, exec.Price, qty, leaderAcct);
                }
            }
            catch (Exception ex) { Log("OnFollowerExecution error: " + ex.Message); }
        }

        // 2026-08-15 — a follower's entry just filled. If the leader's own bracket distance is ALREADY
        // known for this symbol, place the follower's bracket immediately; otherwise queue it and let
        // OnLeaderOrderUpdate (when the leader's bracket appears) or the fallback sweep (if it never
        // does, within BracketFallbackSeconds) place it instead. Either way this position WILL get a
        // bracket — the only question is whether it's the leader's real distance or the fallback default.
        private void QueueBracket(Account fAcct, Instrument tInstr, OrderAction entryAction, double entryPrice, int qty, string leaderAcct)
        {
            string sym = tInstr.MasterInstrument != null ? tInstr.MasterInstrument.Name : tInstr.FullName;
            // v0.1.1 — composite key, matching OnLeaderExecution/OnLeaderOrderUpdate's leaderKey.
            string bracketKey = (leaderAcct ?? "?") + "|" + sym;
            lock (_lock)
            {
                _pendingBrackets.Add(new PendingBracket
                {
                    FollowerAccount = fAcct.Name,
                    Instrument      = tInstr,
                    MasterSymbol    = sym,
                    BracketKey      = bracketKey,
                    EntryAction     = entryAction,
                    EntryPrice      = entryPrice,
                    Qty             = qty,
                    FilledUtc       = DateTime.UtcNow,
                });
            }
            Log("Bracket queued \u25b6 " + fAcct.Name + " " + tInstr.FullName + " " + entryAction + " " + qty
                + " @ " + entryPrice.ToString("0.####") + " — waiting on leader's bracket (or fallback in "
                + (_config != null ? _config.BracketFallbackSeconds : 60) + "s)");
            TryPlacePendingBrackets(bracketKey);
        }

        // place brackets for any queued follower position on this LEADER+symbol where that leader's
        // OWN stop AND target distances are now both known. Leaves anything not yet ready in the queue.
        private void TryPlacePendingBrackets(string bracketKey)
        {
            double stopTicks, targetTicks;
            lock (_lock)
            {
                if (!_leaderStopTicks.TryGetValue(bracketKey, out stopTicks)) return;
                if (!_leaderTargetTicks.TryGetValue(bracketKey, out targetTicks)) return;
            }

            List<PendingBracket> ready = new List<PendingBracket>();
            lock (_lock)
            {
                for (int i = _pendingBrackets.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(_pendingBrackets[i].BracketKey, bracketKey, StringComparison.OrdinalIgnoreCase))
                    {
                        ready.Add(_pendingBrackets[i]);
                        _pendingBrackets.RemoveAt(i);
                    }
                }
            }
            foreach (PendingBracket pb in ready)
                PlaceFollowerBracket(pb, stopTicks, targetTicks, "leader");
        }

        // 2026-08-15 — safety-net sweep, ticks every 5s (see the Timer set up in Start()). Anything still
        // waiting past BracketFallbackSeconds gets the configured fallback distance instead of the
        // leader's real one — the position is NEVER left unprotected indefinitely, even if the leader's
        // bracket is never detected at all (naked leader trade, an order-type this doesn't recognize, etc.).
        private void SweepPendingBrackets()
        {
            try
            {
                var cfg = _config;
                if (cfg == null) return;
                if (cfg.BracketFallbackSeconds <= 0) return;   // fallback explicitly disabled

                List<PendingBracket> overdue = new List<PendingBracket>();
                DateTime cutoff = DateTime.UtcNow.AddSeconds(-cfg.BracketFallbackSeconds);
                lock (_lock)
                {
                    for (int i = _pendingBrackets.Count - 1; i >= 0; i--)
                    {
                        if (_pendingBrackets[i].FilledUtc <= cutoff)
                        {
                            overdue.Add(_pendingBrackets[i]);
                            _pendingBrackets.RemoveAt(i);
                        }
                    }
                }
                foreach (PendingBracket pb in overdue)
                {
                    Log("Bracket FALLBACK \u25b6 " + pb.FollowerAccount + " " + pb.Instrument.FullName
                        + " — leader's bracket not detected within " + cfg.BracketFallbackSeconds
                        + "s, using fallback " + cfg.BracketFallbackStopTicks + "/" + cfg.BracketFallbackTargetTicks + " ticks.");
                    PlaceFollowerBracket(pb, cfg.BracketFallbackStopTicks, cfg.BracketFallbackTargetTicks, "fallback");
                }
            }
            catch (Exception ex) { Log("SweepPendingBrackets error: " + ex.Message); }
        }

        // 2026-08-15 — the actual bracket submission: a protective Stop and a Limit target, sharing one
        // OCO id so filling either cancels the other. TICK-DISTANCE is applied in the FOLLOWER's own
        // instrument's tick size (not the leader's) — correct for same-scale cross-maps (GC/MGC both
        // 0.10, ES/MES both 0.25) but worth checking manually for anything with a different tick size,
        // since a tick-count mirror assumes the follower's ticks are worth a comparable "distance".
        private void PlaceFollowerBracket(PendingBracket pb, double stopTicks, double targetTicks, string source)
        {
            try
            {
                Account fAcct = FindAccount(pb.FollowerAccount);
                if (fAcct == null) { Log("Bracket skip: follower account '" + pb.FollowerAccount + "' not found."); return; }

                double tick = pb.Instrument.MasterInstrument != null ? pb.Instrument.MasterInstrument.TickSize : 0;
                if (tick <= 0) { Log("Bracket skip: no tick size for " + pb.Instrument.FullName); return; }

                bool isLong = pb.EntryAction == OrderAction.Buy;
                double stopPx   = isLong ? pb.EntryPrice - stopTicks   * tick : pb.EntryPrice + stopTicks   * tick;
                double targetPx = isLong ? pb.EntryPrice + targetTicks * tick : pb.EntryPrice - targetTicks * tick;
                OrderAction exitAction = isLong ? OrderAction.Sell : OrderAction.BuyToCover;

                string ocoId = OrderPrefix + "oco_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string stopName   = OrderPrefix + "stop_"   + Guid.NewGuid().ToString("N").Substring(0, 8);
                string targetName = OrderPrefix + "target_" + Guid.NewGuid().ToString("N").Substring(0, 8);

                Order stopOrder = fAcct.CreateOrder(
                    pb.Instrument, exitAction, OrderType.StopMarket, OrderEntry.Automated, TimeInForce.Day,
                    pb.Qty, 0, stopPx, ocoId, stopName, Core.Globals.MaxDate, null);
                Order targetOrder = fAcct.CreateOrder(
                    pb.Instrument, exitAction, OrderType.Limit, OrderEntry.Automated, TimeInForce.Day,
                    pb.Qty, targetPx, 0, ocoId, targetName, Core.Globals.MaxDate, null);

                fAcct.Submit(new[] { stopOrder, targetOrder });
                try { SneakyCore.Ledger.Order(fAcct.Name, pb.Instrument.FullName, "Bracket", "Stop+Limit", pb.Qty, 0,
                    "Copier:bracket(" + source + ")"); } catch (Exception _sx) { SneakyCore.Swallow("SneakyCopier.PlaceFollowerBracket", _sx); }

                Log("BRACKET \u25b6 " + fAcct.Name + " " + pb.Instrument.FullName + " qty " + pb.Qty
                    + " stop " + stopPx.ToString("0.####") + " (" + stopTicks.ToString("0.#") + "t)"
                    + " / target " + targetPx.ToString("0.####") + " (" + targetTicks.ToString("0.#") + "t)"
                    + " [" + source + "]");
            }
            catch (Exception ex)
            {
                Log("PlaceFollowerBracket('" + pb.FollowerAccount + "') error: " + ex.Message);
            }
        }

        private void MirrorToFollower(FollowerConfig f, Execution exec, string leaderSym,
                                      bool leaderIsBuy, int leaderQty)
        {
            try
            {
                // ⛔ v0.1.0i — the refusal is enforced HERE too, not only announced at Subscribe(). A
                // config reload, a Reconfigure() or a hand-edit between startup and this fill must not
                // be able to sneak an Order-mode follower onto the Fill path.
                if (f.CopyMode == CopyMode.Order)
                {
                    if (_orderModeWarned.Add(f.AccountName ?? ""))
                        Log("skip: follower '" + f.AccountName + "' is copy mode ORDER, which is not "
                            + "implemented — NOT mirroring (it is not silently copied on the Fill path).");
                    return;
                }

                // MANUAL mode: don't submit — publish a "place by hand" assist ticket instead
                // (for prop accounts that bar automated copy-trading). Self-contained path.
                if (f.Mode == FollowerMode.Manual)
                {
                    EmitAssistTicket(f, exec, leaderSym, leaderIsBuy, leaderQty);
                    return;
                }

                Account fAcct = FindAccount(f.AccountName);
                if (fAcct == null) { Log("skip: follower '" + f.AccountName + "' not found"); return; }

                if (!IsConnected(fAcct))
                {
                    Log("skip: follower '" + f.AccountName + "' not connected — no mirror (fill lost, "
                        + "by design; a disconnected account can't be kept in sync).");
                    return;
                }

                // per-follower feed health via the shared Core probe (no-op until a health tool
                // registers one). Gates each execution account on ITS OWN feed, per the design.
                if (!SneakyCore.IsAccountHealthy(fAcct))
                {
                    Log("skip: follower '" + f.AccountName + "' feed unhealthy (Core gate).");
                    return;
                }

                // consistency governor (v0.1.0f): a follower that hit its daily cap/loss opens NO new
                // trades today. Only block when the follower is FLAT (a fill on a flat account = a new
                // entry); a follower with an open position is always allowed to manage/close it, so the
                // governor never traps a follower in a live trade.
                // BUGFIX (2026-08-22, P1 finding #3): this check is ALSO re-run below, after the
                // zero-crossing split, for the newly-opened opposite-side leg — see the isCrossing
                // branch further down. Extracted into GovernorAllowsEntry() so both call sites share
                // the exact same logic (see that method's header for why the second call site exists).
                if (IsAccountFlat(fAcct))   // a fill on a flat account = a NEW entry → gate it
                {
                    string blockReason;
                    if (!GovernorAllowsEntry(fAcct, out blockReason))
                    {
                        Log("skip: follower '" + f.AccountName + "' — " + blockReason + " — no new entries (open positions still mirror).");
                        return;
                    }
                }

                // same-provider prop policy (SIM leader exempt) ───────────────────
                string provReason;
                if (!ProviderAllowed(exec.Account, fAcct, out provReason))
                {
                    Log("BLOCKED by provider policy: " + provReason + " → '" + f.AccountName + "'");
                    return;
                }

                // resolve target instrument + size ────────────────────────────────
                InstrumentMapEntry map;
                f.InstrumentMap.TryGetValue(leaderSym, out map);   // null → same instrument, ratio 1
                double ratio = map != null ? map.SizeRatio : 1.0;
                int fQty = (int)Math.Round(leaderQty * ratio * f.Multiplier);
                if (fQty <= 0) { Log("skip: '" + f.AccountName + "' computed qty <= 0"); return; }

                Instrument tInstr = ResolveTargetInstrument(exec.Instrument, map);
                if (tInstr == null)
                {
                    Log("skip: '" + f.AccountName + "' could not resolve target instrument for "
                        + leaderSym + (map != null ? "→" + map.TargetSymbol : ""));
                    return;
                }

                int currentQty;
                OrderAction fAction = ResolveFollowerAction(leaderIsBuy, fAcct, tInstr, out currentQty);

                // v0.1.1 — REJECT-IF-IN-TRADE (global, opt-in via Copy.conf: rejectIfInTrade=on).
                // Per-instrument: currentQty above is already scoped to tInstr specifically, so a
                // follower already in a trade on a DIFFERENT instrument is untouched by this. isReducingAction
                // is exactly the two fActions ResolveFollowerAction ever returns while closing/reducing
                // toward flat (Sell while Long, BuyToCover while Short) — Buy while Long and SellShort
                // while Short are always ADDS, never reduces, regardless of quantity. A follower must
                // never be left unable to exit (see the "exits always mirror" rule throughout this
                // method), so a genuine reduce is untouched here; this only rejects the piece that would
                // create NEW exposure while the follower is already positioned in this instrument. The
                // other half of this feature — blocking just the OPENING leg of a zero-crossing reversal
                // — lives further down, right after the closing leg's unconditional SubmitMirrorLeg call.
                bool isReducingAction = fAction == OrderAction.Sell || fAction == OrderAction.BuyToCover;
                if (_config != null && _config.RejectIfInTrade && currentQty > 0 && !isReducingAction)
                {
                    Log("skip: follower '" + f.AccountName + "' already in a trade on " + tInstr.FullName
                        + " (" + currentQty + ") — RejectIfInTrade is on, not adding to it (leader "
                        + leaderQty + " " + leaderSym + (leaderIsBuy ? " buy" : " sell") + ").");
                    return;
                }

                // 2026-08-09 — ZERO-CROSSING SPLIT. If the mirrored quantity exceeds what's needed to
                // just close the follower's existing position, fAction alone can't represent both halves
                // (Sell != SellShort, Buy != BuyToCover — NinjaTrader requires one explicit action per
                // order). Split into a closing order (currentQty, unconditional — exits always mirror,
                // matching the existing entry-gate comment below) and an opening order for the remainder
                // (subject to the SAME Eye-gate/Order-gate checks a normal new entry gets). The common,
                // non-crossing case (currentQty == 0, or fQty <= currentQty) is completely unchanged —
                // this only branches when a genuine crossing is detected.
                bool isCrossing = currentQty > 0 && fQty > currentQty;
                int closeQty = isCrossing ? currentQty : fQty;
                int openQty  = isCrossing ? (fQty - currentQty) : 0;

                if (isCrossing)
                {
                    Log("ZERO-CROSS ▶ '" + f.AccountName + "' " + tInstr.FullName + " close " + closeQty
                        + " (" + fAction + ") + open " + openQty + " — mirrored qty " + fQty + " > current " + currentQty);
                    // closing leg: always mirrors, no gate (matches "Exits always mirror" below).
                    SubmitMirrorLeg(f, fAcct, tInstr, fAction, closeQty, exec, leaderSym, leaderQty);

                    // v0.1.1 — REJECT-IF-IN-TRADE, second half (see the early check right after
                    // ResolveFollowerAction above for the plain-add case). The closing leg above always
                    // mirrors regardless — the follower just went flat, exactly as "never trap a
                    // follower" requires. What gets refused here is only the brand-new OPENING leg this
                    // zero-crossing fill implies beyond flat: the follower was already in a trade at the
                    // moment this signal arrived, so under this flag it stops at flat instead of
                    // reversing into a new position.
                    if (_config != null && _config.RejectIfInTrade)
                    {
                        Log("skip: follower '" + f.AccountName + "' closed to flat on " + tInstr.FullName
                            + " — RejectIfInTrade is on, not opening the new opposite-side position "
                            + "this leader fill implies.");
                        return;
                    }

                    // BUGFIX (2026-08-22, P1 finding #3): the governor/daily-loss gate above only runs
                    // when IsAccountFlat(fAcct) is true, evaluated BEFORE this crossing split exists —
                    // so a follower that already hit its daily-loss/governor limit but still held a
                    // residual position skipped that gate entirely, including for this brand-new
                    // opposite-side leg. Re-run the SAME governor/session check here, scoped to the
                    // OPENING leg only — the close leg above stays unconditional (an exit must always
                    // be allowed, per the "never trap a follower in a live trade" rule documented above).
                    string crossBlockReason;
                    if (!GovernorAllowsEntry(fAcct, out crossBlockReason))
                    {
                        Log("skip: follower '" + f.AccountName + "' — " + crossBlockReason
                            + " — opposite-side open leg blocked by governor (close leg already mirrored).");
                        return;
                    }

                    // the opening leg now takes over fAction/fQty for the EXISTING gate logic below,
                    // which was already written for "is this an entry" — SellShort/Buy for the new side.
                    fAction = fAction == OrderAction.Sell ? OrderAction.SellShort : OrderAction.Buy;
                    fQty = openQty;
                }

                // ── EYE-GATE (opt-in): only mirror ENTRIES that SentinelEye qualifies for this leader
                //    instrument+direction. Exits (Sell/BuyToCover) ALWAYS mirror to keep followers synced.
                if (_config != null && _config.UseEyeGate
                    && (fAction == OrderAction.Buy || fAction == OrderAction.SellShort))
                {
                    int wantDir = fAction == OrderAction.Buy ? 1 : -1;
                    // 2026-08-28 — was GetEyeVerdict(leaderSym, 30): a BARE-instrument lookup that fails
                    // closed the instant more than one scope publishes EyeState for this instrument (the
                    // normal state once SneakyEye runs on more than one timeframe — see the "AMBIGUOUS
                    // SEAM" log line). Switched to the any-qualifying lookup: does ANY currently-live
                    // scope for this instrument qualify in the wanted direction right now?
                    var verdict = SneakyCore.GetEyeVerdictAnyQualifying(leaderSym, 30, wantDir);   // 30s staleness window
                    if (verdict == null)
                    {
                        Log("Eye-gate BLOCKED entry ▶ " + f.AccountName + " " + fAction + " " + tInstr.FullName
                            + " — Eye for " + leaderSym + " = no scope currently qualifies dir " + wantDir + " (no/stale verdict)");
                        return;
                    }
                    Log("Eye-gate OK ▶ " + leaderSym + " qualified dir " + verdict.Direction + " score " + verdict.Score.ToString("0") + " (scope " + verdict.Scope + ")");
                }

                // ── ORDER GATE (v1.1.0): automated mirror = fail CLOSED. Gate only NEW entries (flat
                //    follower); exits always mirror so followers stay synced. Adds kill/feed/rollover/
                //    news/rate/qty-cap on top of the governor/session check above (the single choke point).
                // 2026-08-09 — after a zero-cross split, IsAccountFlat(fAcct) reflects the account's REAL
                // current state, which still shows the pre-close position (the closing leg was just
                // submitted, not yet filled) — so this correctly still gates the opening leg as a new
                // entry rather than skipping the gate because the account "looks" already positioned.
                if (isCrossing || IsAccountFlat(fAcct))
                {
                    var gd = SneakyCore.GateEntry(fAcct, tInstr.FullName, fQty);
                    if (!gd.IsClear)
                    {
                        Log("gate BLOCKED entry ▶ " + f.AccountName + " " + fAction + " " + tInstr.FullName + " — " + gd.Reason);
                        return;
                    }
                }

                SubmitMirrorLeg(f, fAcct, tInstr, fAction, fQty, exec, leaderSym, leaderQty);
            }
            catch (Exception ex)
            {
                Log("MirrorToFollower('" + (f != null ? f.AccountName : "?") + "') error: " + ex.Message);
            }
        }

        // 2026-08-09 — the actual order construction/submission, extracted verbatim from what used to be
        // the tail of MirrorToFollower (name generation, price-correlation entry, submit, ledger, log)
        // so the zero-crossing split can call it twice (closing leg, then opening leg) using EXACTLY the
        // same, already-proven logic as the single-order case — nothing about the submission mechanics
        // themselves changed, only how many times this gets called and with what quantity/action.
        private void SubmitMirrorLeg(FollowerConfig f, Account fAcct, Instrument tInstr, OrderAction action,
                                      int qty, Execution exec, string leaderSym, int leaderQty)
        {
            string name = OrderPrefix + Guid.NewGuid().ToString("N").Substring(0, 8);

            Order o = fAcct.CreateOrder(
                tInstr, action, OrderType.Market, OrderEntry.Automated, TimeInForce.Day,
                qty, 0, 0, string.Empty, name, Core.Globals.MaxDate, null);

            // correlate this mirror to the LEADER's fill price so OnFollowerExecution can score copy
            // slippage (follower fill vs the price we tried to replicate). Same-symbol + GC→MGC/ES→MES
            // share a price scale so the tick diff is meaningful; exotic cross-maps would not (rare).
            lock (_lock)
            {
                if (_mirrorLeaderPx.Count > 4000) _mirrorLeaderPx.Clear();   // bounded
                _mirrorLeaderPx[name] = exec.Price;
                // v0.1.1 — record WHICH leader this mirror came from, keyed the same way, so
                // OnFollowerExecution can recover it when this follower's own fill comes back in
                // (needed to keep bracket-distance tracking scoped per-leader, not per-symbol alone).
                if (_mirrorLeaderAcct.Count > 4000) _mirrorLeaderAcct.Clear();
                _mirrorLeaderAcct[name] = exec.Account != null ? exec.Account.Name : "?";
            }

            try
            {
                fAcct.Submit(new[] { o });
                try { SneakyCore.NoteOrderSubmitted(fAcct.Name); } catch (Exception _sx) { SneakyCore.Swallow("SneakyCopier.SubmitMirrorLeg", _sx); }   // feed the rate guard
                try { SneakyCore.Ledger.Order(fAcct.Name, tInstr.FullName, action.ToString(), "Market", qty, 0, "Copier:mirror"); } catch (Exception _sx) { SneakyCore.Swallow("SneakyCopier.SubmitMirrorLeg", _sx); }
                Log("MIRROR ▶ " + fAcct.Name + " " + action + " " + qty + " "
                    + tInstr.FullName + " (leader " + leaderQty + " " + exec.Instrument.FullName + ")");
            }
            catch (Exception ex)
            {
                Log("submit FAILED for '" + f.AccountName + "': " + ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  MANUAL-ASSIST: reuse the map/size/Eye-gate pipeline, but PUBLISH a "place this by hand"
        //  ticket instead of submitting an order. Mirrors the leader's exact action (fill-mirror), so
        //  no follower-account lookup is needed — the account name is just a label the user places on.
        // ─────────────────────────────────────────────────────────────────────
        private void EmitAssistTicket(FollowerConfig f, Execution exec, string leaderSym,
                                      bool leaderIsBuy, int leaderQty)
        {
            try
            {
                InstrumentMapEntry map;
                f.InstrumentMap.TryGetValue(leaderSym, out map);
                double ratio = map != null ? map.SizeRatio : 1.0;
                int fQty = (int)Math.Round(leaderQty * ratio * f.Multiplier);
                if (fQty <= 0) { Log("assist skip: '" + f.AccountName + "' computed qty <= 0"); return; }

                Instrument tInstr = ResolveTargetInstrument(exec.Instrument, map);
                string tName = tInstr != null ? tInstr.FullName
                    : (map != null && !string.IsNullOrEmpty(map.TargetSymbol) ? map.TargetSymbol : leaderSym);

                OrderAction fAction = exec.Order.OrderAction;   // fill-mirror: place exactly what the leader did
                bool isEntry = fAction == OrderAction.Buy || fAction == OrderAction.SellShort;

                // Eye-gate entries, same policy as the auto path (exits always pass so you stay in sync).
                if (_config != null && _config.UseEyeGate && isEntry)
                {
                    int wantDir = fAction == OrderAction.Buy ? 1 : -1;
                    // 2026-08-28 — see the matching change in MirrorToFollower's Eye-gate above: bare-
                    // instrument GetEyeVerdict fails closed once SneakyEye runs on more than one scope
                    // for this instrument. Any-qualifying lookup instead.
                    var verdict = SneakyCore.GetEyeVerdictAnyQualifying(leaderSym, 30, wantDir);
                    if (verdict == null)
                    {
                        Log("Assist Eye-gate BLOCKED ▶ " + f.AccountName + " " + fAction + " " + tName
                            + " — Eye " + leaderSym + " = no scope currently qualifies dir " + wantDir + " (no/stale verdict)");
                        return;
                    }
                }

                var v = SneakyCore.GetEyeVerdict(leaderSym, 60);
                string eyeCtx = (v != null && v.Direction != 0) ? " · Eye " + (v.Direction > 0 ? "L" : "S") + v.Score.ToString("0") : "";

                SneakyCore.PublishAssistTicket(new SneakyCore.AssistTicket
                {
                    TimeUtc    = DateTime.UtcNow,
                    Account    = f.AccountName,
                    Action     = fAction.ToString(),
                    Qty        = fQty,
                    Instrument = tName,
                    IsEntry    = isEntry,
                    Context    = leaderQty + " " + leaderSym + " " + (leaderIsBuy ? "buy" : "sell") + eyeCtx
                });
                Log("ASSIST ▶ PLACE " + fAction + " " + fQty + " " + tName + " on '" + f.AccountName
                    + "'  (leader " + leaderQty + " " + leaderSym + (leaderIsBuy ? " buy" : " sell") + ")" + eyeCtx);
            }
            catch (Exception ex)
            {
                Log("EmitAssistTicket('" + (f != null ? f.AccountName : "?") + "') error: " + ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers (several marked VALIDATE — confirm exact API on first NT compile/run)
        // ─────────────────────────────────────────────────────────────────────

        private static Account FindAccount(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (Account.All)
            {
                foreach (Account a in Account.All)
                    if (a != null && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                        return a;
            }
            return null;
        }

        // VERIFIED: Account.Connection (@ProfitLoss.cs:89) + Connection.Status (@BarTimer.cs:50).
        private static bool IsConnected(Account a)
        {
            try
            {
                return a != null && a.Connection != null
                    && a.Connection.Status == ConnectionStatus.Connected;
            }
            catch { return true; }   // fail-open: a lookup hiccup shouldn't silently drop mirrors
        }

        // v0.1.0f: is the account entirely flat? (used by the governor gate — a fill on a flat account
        // is a NEW entry; a non-flat account is managing/closing and is never governor-blocked). Fail-open.
        private static bool IsAccountFlat(Account a)
        {
            try { foreach (Position p in a.Positions) if (p != null && p.MarketPosition != MarketPosition.Flat) return false; }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyCopier.IsAccountFlat", _sx); }
            return true;
        }

        // BUGFIX (2026-08-22, P1 finding #3): extracted so MirrorToFollower can run the SAME
        // governor/daily-loss + session check from TWO places — once for a plain new entry
        // (follower already flat) and again for a zero-crossing fill's newly-opened opposite-side
        // leg (follower was NOT flat, so the first call site's IsAccountFlat(fAcct) guard never
        // fired for it). Before this, that second case skipped the governor gate entirely, so a
        // follower that had already hit its daily-loss limit could still get a brand-new
        // opposite-side position opened via a crossing mirror. Returns true iff a NEW entry is
        // currently allowed; false + reason otherwise.
        private static bool GovernorAllowsEntry(Account fAcct, out string reason)
        {
            reason = null;
            if (!SneakyCore.TradingAllowedToday(fAcct))
            {
                var gs = SneakyCore.GetGovernorState(fAcct.Name);
                reason = "governor " + (gs != null ? gs.Status + " (" + gs.Reason + ")" : "day done");
                return false;
            }
            string sr;
            if (!SneakyCore.InAccountSession(SneakyCore.GetAccountProfile(fAcct.Name), out sr))
            {
                reason = sr;
                return false;
            }
            return true;
        }

        // Same-provider prop rule. SIM leader is EXEMPT (local, no firm). Coarse first cut:
        // compare leader vs follower provider identity. Refine to per-firm grouping later.
        private bool ProviderAllowed(Account leader, Account follower, out string reason)
        {
            reason = null;
            if (_config.Policy == ProviderPolicy.Off) return true;
            if (IsSimAccount(leader)) return true;   // SIM leader exempt (see memory)

            string lp = ProviderKey(leader);
            string fp = ProviderKey(follower);
            if (string.Equals(lp, fp, StringComparison.OrdinalIgnoreCase)) return true;

            reason = "cross-provider (" + lp + " → " + fp + ")";
            if (_config.Policy == ProviderPolicy.Warn) { Log("WARN " + reason); return true; }
            return false;   // Block
        }

        // VERIFIED: Connection.Options.Name (AutoReconnect.cs:247) — the CONNECTION name, which is
        // the firm-level key we want (Provider enum would be too coarse: two Rithmic firms collide).
        private static string ProviderKey(Account a)
        {
            try
            {
                if (a != null && a.Connection != null && a.Connection.Options != null)
                    return a.Connection.Options.Name ?? "?";
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyCopier.ProviderKey", _sx); }
            return "?";
        }

        private static bool IsSimAccount(Account a)
        {
            // heuristic (matches MAECaptureService): name contains "Sim". VALIDATE: prefer a
            // real Simulation flag / Provider.Simulation if this build exposes one.
            return a != null && a.Name != null
                && a.Name.IndexOf("Sim", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Resolve the follower's target contract. Same symbol → same instrument object.
        // Cross-map (GC→MGC) → resolve the mapped master symbol, keeping the source expiry.
        // VALIDATE: front-month vs same-expiry resolution + exact full-name format ("MGC 08-26").
        private Instrument ResolveTargetInstrument(Instrument source, InstrumentMapEntry map)
        {
            if (source == null) return null;
            if (map == null || string.IsNullOrEmpty(map.TargetSymbol)
                || string.Equals(map.TargetSymbol, source.MasterInstrument.Name, StringComparison.OrdinalIgnoreCase))
                return source;   // no cross-map → mirror on the same instrument

            // try same-expiry contract first (keep the leader's month), then bare symbol.
            try
            {
                string suffix = source.FullName.Contains(" ")
                    ? source.FullName.Substring(source.FullName.IndexOf(' '))   // " 08-26"
                    : "";
                Instrument byExpiry = Instrument.GetInstrument(map.TargetSymbol + suffix);
                if (byExpiry != null) return byExpiry;
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyCopier.ResolveTargetInstrument", _sx); }
            try { return Instrument.GetInstrument(map.TargetSymbol); } catch { return null; }
        }

        // Choose entry-vs-exit action so brokers that distinguish SellShort/BuyToCover accept it.
        // VALIDATE: Account.Positions surface (name/type). LIMITATION: a fill that crosses zero
        // (cover + flip in one event) needs an order split — deferred; skeleton uses one action.
        // 2026-08-09 — now also returns the follower's CURRENT quantity in tInstr (was discarded before,
        // keeping only direction). The caller needs this to detect a zero-crossing fill: if the mirrored
        // quantity exceeds what's needed to just close the existing position, one OrderAction can't
        // represent both halves (Sell and SellShort are different enum values; so are Buy and
        // BuyToCover) — the caller splits into two orders using this quantity as the split point.
        private static OrderAction ResolveFollowerAction(bool leaderIsBuy, Account fAcct, Instrument tInstr, out int currentQty)
        {
            MarketPosition mp = MarketPosition.Flat;
            currentQty = 0;
            try
            {
                if (fAcct != null && fAcct.Positions != null)
                {
                    foreach (Position p in fAcct.Positions)
                    {
                        if (p != null && p.Instrument == tInstr) { mp = p.MarketPosition; currentQty = Math.Abs(p.Quantity); break; }
                    }
                }
            }
            catch { /* fall back to Flat → Buy/SellShort, currentQty already 0 */ }

            if (leaderIsBuy) return mp == MarketPosition.Short ? OrderAction.BuyToCover : OrderAction.Buy;
            return mp == MarketPosition.Long ? OrderAction.Sell : OrderAction.SellShort;
        }

        // suite helper: is this order one of ours? (so other tools never touch copier orders)
        public static bool IsCopierOrder(string orderName)
        {
            return orderName != null && orderName.StartsWith(OrderPrefix, StringComparison.Ordinal);
        }

        private void Log(string msg)
        {
            SneakyCore.Log("Copy", msg);   // "[Sentinel:Copy] ..." in the Output window
        }
    }
}
