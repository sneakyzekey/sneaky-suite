// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
//
// Copyright (c) 2026 silentsudo-io and the Sentinel Suite contributors.
// ─────────────────────────────────────────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════
//  SneakyExcursionRecorder — pure signal characterization for the Sentinel Suite (NT8)
//  File: SneakyExcursionRecorder_v2_0_0.cs   ·   Version v2.7.0   ·   Schema 1.5 (sidecar ctick.4)   ·   namespace …Indicators.Sneaky
// ─────────────────────────────────────────────────────────────────────────────
//  WHAT THIS IS
//    A pure, no-orders CHARACTERIZATION recorder. On each edge-detected COUNCIL verdict (an aligned
//    bias-flip, mirroring the SentinelBridge entry trigger) it opens a record and tracks, fire → EOD,
//    the max favourable/adverse excursion (MFE/MAE, ticks), 1/5/15/60-minute milestones, and the
//    schema-1.3 FIRST-TOUCH label (which ATR-scaled barrier — target or stop — is crossed first).
//    Rows are appended to Sentinel\Excursions\*.jsonl and consumed offline by the Observatory + the ML
//    Lab to grade the Council and fit its ConvictionFloor.
//
//  CLEAN-ROOM LINEAGE (2026-07-11) — this v2.0.0 is the OPEN-SOURCE cut of the recorder. It is the
//    private SentinelExcursionRecorder_v1_4 (schema 1.3) with the optional GodTrades21 hosting REMOVED:
//    v1_4 could also host JET's GodTrades21 as a "does the Council beat raw FC?" comparison baseline
//    (RecordGodTrades / ShowUnderlyingIndicator). That baseline embedded a third-party engine that is
//    not part of the open-source distribution, so this version records the COUNCIL verdict ONLY. The
//    JSONL schema is UNCHANGED (still 1.3) — Council rows are byte-identical to v1_4's; there are simply
//    no BG/FC/OBR rows. No SentinelCore change (CONSULTS CouncilState ≥ v1.7.0 + GetEyeVerdict, both seams).
//    v1_4 stays as the user's private tool (frozen); this is the shippable.
//
//  CHANGELOG
//    2026-08-17 — AUDIT vs main (SentinelExcursionRecorder_v2_0_0.cs, header "Version v2.5.0 · Schema 1.5",
//           SchemaVer const "1.6"). NO CODE CHANGE — this entry exists only so the finding is not re-litigated.
//           Requested port: (1) whatever Confluence+Eye capture main has that this file doesn't, (2) the
//           schema bump that produced main's SchemaVer "1.6", (3) whatever fix main's changelog describes for
//           the lane-loss / entry-backfill problem this file already fixed independently. Read main in full
//           (all ~1,110 lines, changelog + code) and found the PREMISE DOES NOT HOLD for this copy of main:
//             (1) Confluence+Eye — this file already carries the COMPLETE capture (EyeHad/EyeScore/EyeDir/
//                 EyeAligned from GetEyeVerdict, added pre-2026-08-17; the full Confluence block — CnfBias,
//                 CnfFired, all Cnf*Ok gates, CnfBlockedBy, CnfTriggerLabel — from GetConfluenceState, added
//                 2026-08-09). Main's Open() has only an EMPTY try/catch under an "Eye" comment (no
//                 GetEyeVerdict call, nothing latched) and NO Confluence code or fields at all. This file is
//                 a strict superset of main here; there was nothing to port in.
//             (2) SchemaVer — main's const reads "1.6", but main's own header banner still says "Schema 1.5",
//                 main's own changelog's newest entry (v2.5.0) says "Row schema UNCHANGED at 1.5", and main's
//                 ToJson emits nothing a 1.5 reader wouldn't already expect — no field, no shape change,
//                 anywhere in main's ~1,110 lines, documents what "1.6" is supposed to mean. That is the exact
//                 undocumented-drift failure mode this file's corpus-layout comment (and main's own banner
//                 note about RecVer/header drift) warns about, just hitting SchemaVer instead this time.
//                 Copying "1.6" onto this file's real, additive Eye+Confluence fields would silently split an
//                 otherwise-compatible corpus for a version bump that corresponds to no actual change — the
//                 opposite of what SchemaVer exists to prevent. SchemaVer stays "1.5" here, consistent with
//                 this file's own precedent (v2.3.0's brkUpper/brkLower, and the 2026-08-09 Confluence add,
//                 both additive and both left at 1.5).
//             (3) Lane-loss / EntryBackfillWindowMs — main has NEITHER fix. Main's BarTag() re-resolves
//                 LaneOf() fresh every call with no fallback (the exact bug this file's own v2.7.0 fixed via
//                 sticky _lastLane), and main's OnMarketData backfill is still the original hardcoded
//                 `ms == 0` exact-match (the exact defect this file's own v2.6.0 fixed via the
//                 EntryBackfillWindowMs setting, default 100ms). Main's changelog has no entry past v2.5.0
//                 (2026-07-31) describing either problem, let alone a further/different fix for it. This
//                 file's existing v2.6.0 + v2.7.0 fixes already fully cover — and exceed — what main has;
//                 both are preserved here EXACTLY as they were, untouched.
//           RecVer stays "2.7.0" (no logic changed, so bumping it would falsely imply a new batch). The
//           Sentinel→Sneaky naming throughout and the SneakySkin.CardLayout.PlaceScroll()/ISneakyScrollCard
//           shared-rotation card mechanism (vs. main's older single-slot Place()) are pre-existing, deliberate,
//           suite-wide conversions and are untouched by this audit.
//    v2.7.0 (2026-08-16) — 🔴 LANE LOST ON EVERY ROW STILL OPEN AT State.Terminated. Row schema UNCHANGED.
//           BarTag() re-resolved SneakyCore.LaneOf(ChartControl) fresh on every call, unlike Scope() (which
//           deliberately does the same, see its own 2026-08-04 comment). Council's Terminated handler calls
//           SneakyCore.ClearLane(ChartControl) on the SAME ChartControl object this recorder reads — and
//           indicator termination order across a chart is not guaranteed, so when this recorder's own
//           FlushAll("cutoff") ran AFTER Council's ClearLane, LaneOf() returned "" and every record still
//           open at that instant lost its @lane suffix in the row's `bartype` field. Confirmed 100% (2026-08-15
//           lab audit): every bare-bartype row in the schema-1.5 corpus had endReason:"cutoff"; zero had
//           "window" or "EOD". Not corruption — every dropped lane was recoverable from a laned sibling row
//           in the same session file or the file's own stamped filename — but it silently mislabelled ~9%
//           of the corpus by lane, which the Lab groups on.
//           FIX: BarTag() now remembers the LAST NON-EMPTY lane it saw (_lastLane) and falls back to it
//           when a read comes back empty, instead of trusting a bare fresh read. Still re-resolves LaneOf()
//           every call (never permanently caches on the FIRST read) — Scope()'s own comment already
//           documents why a naive one-time cache is wrong here: a lane can register minutes after this
//           indicator's first call on a slow reload, and caching an empty result then would freeze it wrong
//           for the rest of the session. Sticky-on-success gets both: self-heals a late-registering lane
//           during normal operation, and survives a late, empty read during teardown by keeping the last
//           good value instead of discarding it.
//    v2.6.0 (2026-08-15) — ENTRY BACKFILL WINDOW widened + exposed as a real setting. verify_firepx.py's own
//           non-circular check (the "last"-sourced rows, which don't beg the question the way "firsttick" rows
//           would) found NQ failing — median 2.0-tick offset, 46-58% adverse-skewed — while bfill% read 0% on
//           EVERY lane, both NQ and MNQ. Root cause: the backfill only adopted a fresh tick landing in the
//           EXACT SAME MILLISECOND as the fire (ms==0), which real tick timestamps essentially never do —
//           so the mechanism built specifically to solve this (v2.2.1) had been functionally dead the whole
//           time, on every instrument, not just NQ. EntryBackfillWindowMs (new setting, GroupName "Recorder",
//           default 100ms) replaces the hardcoded ms==0 check — wide enough to catch a real tape's normal
//           inter-print gaps, while staying ~10-20x smaller than the "seconds" of forward drift the original
//           guard was built to prevent. This banner + the Description property + two other property
//           descriptions still said "Sentinel\Excursions"/schema 1.3 until this pass — same header-drift
//           pattern this file's own v2.5.0 entry warned about; fixed alongside the real change so it doesn't
//           cause the same "is this even the right file" confusion again.
//    v2.5.0 (2026-07-31) — 🔴 THE TICK CORPUS COULD NEVER ANSWER D2. Row schema UNCHANGED at 1.5.
//           TickPathTailMs (how much path is kept AFTER a fire resolves) was a private const 30000 and is now a
//           SETTING, default deliberately unchanged so this cannot alter an existing run.
//           WHY: measured over the live Keel bake, 0 of 80 knockouts had a tick path reaching their own MFE bar
//           (p50 path 127s, max 1413s, NONE past 30 min). The D2 defect — stopped out while the move kept going —
//           lives entirely in the path AFTER the stop, i.e. exactly the segment this tail was discarding 30s in.
//           So a re-entry rule could not be designed from the tick corpus at ANY bake length. Not a bug (v2.1.1
//           deliberately wanted "the immediate post-R behavior"), but it silently bounded what the corpus can be
//           asked. A D2 study bake now sets the tail to ~3600000 (60 min, matching the row's MFE/MAE window).
//           ⚠ Costs memory + much larger sidecars, so it is a BAKE-TIME decision, never a new default.
//    v2.4.0 (2026-07-30) — 🔴 THE CORPUS COULD ONLY EVER SEE THE COUNCIL. Row schema UNCHANGED at 1.5.
//           This recorder's only intake was the `GetCouncilState` poll, so it opened on `Open("COUNCIL", …)`
//           and nothing else — which meant EVERY STRATEGY WAS INVISIBLE TO THE CORPUS BY CONSTRUCTION. The
//           system built to grade decisions could not see a strategy's decisions; you could run one for a
//           month and have nothing to grade it with. That is the actual blocker behind SentinelKeel, and it
//           was never an exit-policy problem.
//           Intake is now GENERIC: `SneakyCore.NoteSignalFire(scope,dir,tag,isHistorical,…)` (Core v1.46.0)
//           queues a decision, this drains it each bar and opens a fire under the publisher's own tag. The
//           Council becomes one caller among several rather than the only one.
//           ⚠ Schema deliberately NOT bumped. The row's `signal` field already carries the tag, so a KEEL row
//           and a COUNCIL row differ only in a value the Lab already reads — no reader changes, no corpus
//           split. The Council-specific columns record as NaN/null for an external fire, which is the honest
//           value for a fire the Council did not produce. Carrying strategy-side context (its own conviction,
//           its intended size) ONTO the row is a real schema change and is deliberately left for when there
//           is a strategy actually producing it.
//           ⚠ `Record external fires` is NOT a [NinjaScriptProperty] — a new constructor parameter would
//           regenerate the region and drop this indicator off every chart that already carries it.
//           Defaults ON: a fire only exists because a strategy deliberately published one, and defaulting OFF
//           would mean an author instruments their code, sees nothing, and cannot tell that from a broken
//           intake. Realtime-gated here AND rejected-if-historical inside Core — two locks on the same door,
//           because a caller passing the wrong flag must still not contaminate the corpus.
//    v2.3.0 (2026-07-23) — LATCHED BOUNDARIES AT FIRE (`brkUpper` / `brkLower`, from BrickState). Makes the
//           LIMIT-vs-MARKET entry question gradeable OFFLINE, on every Sentinel bar type at once, with no new
//           bar type. Why no new bar type: the limit-bar spec claimed TBars "re-derives its boundaries every
//           tick", which would make a resting order impossible to maintain -- that was WRONG. `barMax`/`barMin`
//           are assigned ONLY in CreateBreakoutBar / ForceTimeBrick / InitializeFirstBar, all bar-CREATION
//           events, and RefreshDynamicOffsets() is called from those three and nowhere else. TBars boundaries
//           are ALREADY immutable within a bar, and BrickState has been publishing them per tick all along.
//           So the level a limit could rest at already existed; only the RECORD of it was missing.
//           ⚠ Read on the BARE scope: bar-type seams are keyed by the shared bars series, so the laned Scope()
//           returns null for them on a laned chart (new BareScope() helper; the Council already did this).
//           ⚠ ACCEPTANCE: firePx must sit BETWEEN brkLower and brkUpper -- that proves the seam held the FORMING
//           bar's boundaries at fire, not the closed one's. Verify it in the data; do not assume the call order.
//           Schema stays 1.5 (purely additive fields); recVer separates the batches.
//    v2.2.1 (2026-07-22) — ENTRY BACKFILL. v2.2.0 priced a fire from `_lastPx`, but OnMarketData runs AFTER
//           OnBarUpdate for the tick that CLOSED the bar, so the latch held the trade BEFORE the triggering one.
//           Verification measured the residual: ~0 on Sentinel bar types, but on a jump-driven CASCADE it is the
//           whole jump (GC Renko printed 5 bricks off one 7-tick move — 5 fires sharing ONE stale entry price and
//           ONE forward tick path; 96% of Renko fires shared a firePx with another fire). Now: when a fire's FIRST
//           path tick lands in the SAME millisecond as the fire, it IS the triggering trade and the first
//           transactable price -> adopt it (`pxSrc="firsttick"`). ⚠ NOT unconditional -- a next-trade seconds away
//           would import real forward drift into the entry, which is lookahead; those keep `_lastPx`/`pxSrc="last"`.
//           One-shot per Rec (`PxFixed`), placed BEFORE every read of FirePx in the loop. Schema stays 1.5 (both
//           pxSrc values denote a real, fillable price — which is exactly what 1.5 asserts); recVer separates them.
//           ⚠ SEPARATE, UNFIXED, and bigger: those cascade rows are NOT independent observations. Any fit must
//           dedupe or cluster-weight them or it overstates n by ~5x on cascade-prone bar types.
//    v2.2.0 (2026-07-22) — 🔴 THE HONEST ENTRY PRICE. Row schema 1.4 → 1.5, sidecar ctick.3 → ctick.4.
//           `FirePx` was `Close[0]`. On EVERY Sentinel bars type (TBars/TbarsCount/Flux/Drift) that is the
//           HEIKIN-ASHI SYNTHETIC close — an average that NEVER TRADED — while the tick path was always the real
//           tape (`OnMarketData`/`Last`). This file's own v2.1.0 comment said so ("Close[0] is a synthetic brick
//           close on HA/TBars"); nothing downstream acted on it.
//           MEASURED 2026-07-22 over 3,710 replay sidecars, four independent ways:
//             • gap dir*(firePx − px[0]) = −9.36t mean, SYMMETRIC by side (LONG −9.30 / SHORT −9.42) — the HA
//               fingerprint (HA close sits below price on up bars, above on down bars ⇒ adverse BOTH ways)
//             • px[0] reconciles to the real traded price within 1 tick 95.7% of the time; firePx only 6.9%
//             • 99.6% of paths start at ms=0 ⇒ NO elapsed time for a "chase" — the gap is definitional, not cost
//             • positive control: non-Sentinel bar types show NO bias (GC 2016v2x8 +0.05t, 50/50) while every HA
//               Sentinel type is systematically negative ⇒ a property of OUR BAR TYPES, not of the market
//           ⚠ WHY IT MATTERED: FirePx is the single reference for MFE / MAE / barrier / first-touch. Every label
//           in the corpus was measured from an untradeable price — MFE +9.36t optimistic, MAE 9.36t understated,
//           and the ML target label disagreed with truth on 44.6% of fires (recorded "target-first" 52.3% vs
//           21.1% TRUE; of 1,940 rows labelled target-first only 36.9% were, 59.8% never resolved at all).
//           The corpus claimed trades work ~2.5× more often than they do, and every fit inherited that.
//           FIX: latch the true last trade in OnMarketData (BEFORE the tick-path guards — a fire must be priced
//           even when path capture is off) and use it as FirePx. `pxSrc` records how it resolved ("last" |
//           "barclose" fallback when the tape has not spoken), `barClosePx` keeps the HA close as its own field,
//           and `entryBid`/`entryAsk` are recorded so the Lab can price the crossing cost offline instead of
//           us guessing it now. ⚠ 1.5 rows land in `council\1.5\` and must NEVER be pooled with the 1.4 corpus;
//           1.4 is kept (a valid record of what the old logic saw), not deleted. Same fix in
//           SentinelCandidateRecorder v1.2.0. Full evidence: memory `firepx-is-synthetic-ha-close`.
//    v2.1.6 (2026-07-17) — ENTRY CONTEXT on the TICK SIDECAR HEADER (sidecar schema ctick.2 → ctick.3; ROW schema
//           UNCHANGED at 1.4). The offline path/exit analysis (Lab\pathlab.py) joins each tick-path back to its row
//           corpus by `episodeId` to get regime/clock/vote context — but only ~⅓ of paths matched a row (the sidecar
//           records more fires than the resolved-window row corpus retains), so two-thirds of paths were context-blind
//           and the regime/conviction GATE analysis ran on a biased third. Fix: stamp the entry-context block
//           (`regime, adx, clockPhase, rvol, mtfBias, netScore, activeW, voters, agree, disagree`) directly onto the
//           sidecar header — every field is ALREADY captured at Open() on the Rec, so this is emit-only, no new capture,
//           no order/Core change. Tick-paths are now SELF-DESCRIBING → 100% context coverage for the gate fit, no join.
//           Old ctick.2 sidecars stay valid (readers fall back to the join); mixed dir is fine (schema field distinguishes).
//    v2.1.5 (2026-07-16) — cnclVer (A1 provenance completed; Core ≥ v1.36.0 · Council ≥ v1.8.0). Each row + sidecar now
//           also stamps `cnclVer` — the exact COUNCIL version that produced the verdict (from CouncilState.CouncilVersion)
//           — finer than `coreVer`, which only moves on a SentinelCore bump and would miss a Council-only logic change.
//           Stays schema 1.4 (cnclVer completes the same-session provenance schema; a pre-cnclVer 1.4 row simply carries
//           it null). Closes the "no logic-version stamp" fidelity debt: recorder + core + council versions all on the row.
//    v2.1.4 (2026-07-16) — SCHEMA 1.4 = PROVENANCE + FAIL-LOUD (fidelity audit A1/A2; Core ≥ v1.35.0). Two fixes from
//           the "record truth" audit: (A1) every row + tick sidecar now carries a PROVENANCE block — `recVer` (this
//           recorder), `coreVer` (SneakyCore.Version), and `barLabel` (the human bartag "SentinelFlux 8"). Before
//           this, `schema:"1.3"` versioned only the row SHAPE, so a Council/recorder LOGIC change left old and new rows
//           pooling indistinguishably — a fit over the blend measured two systems as one. Shape changed ⇒ version bumps:
//           rows now land in `council\1.4\`, sidecars are `ctick.2`, and the pre-provenance `1.3` corpus stays frozen
//           beside it. (A2) the recorder no longer dies SILENTLY — a failed writer setup logs "WRITER DEAD" + shows a
//           red **NO REC** pill (it used to draw a healthy card while writing nothing), and a write exception logs ONCE
//           ("WRITE FAILED — rows being LOST") instead of being swallowed per-row. The card footer now shows the human
//           bartag. `cnclVer` (the exact Council version on the verdict) is a documented fast-follow — coreVer is the
//           working logic fingerprint today. Class/file identity kept _v2_0_0 (F5-safe mid-collection).
//    v2.1.3 (2026-07-14) — PER-CHART LANE (Core ≥ v1.32.0). Scope() now folds in the chart's lane via ChartControl,
//           so on a laned chart the recorder reads the LANED CouncilState (the Council publishes @lane, not bare) and
//           files the corpus under the laned scope → two same-bartype test lanes (A/B) record into SEPARATE corpora
//           and the Lab grades each independently. Bare scope when no lane (back-compat). Schema UNCHANGED (still 1.3).
//    v2.1.2 (2026-07-14) — RESOLUTION-BASED ROW flush (crash-safety). A row is now streamed to the 1.3 corpus the
//           moment its excursion window is COMPLETE (past the last milestone, 60 min post-fire) and released from
//           _open, instead of buffering the WHOLE session and flushing only at session-roll / Terminated. Before this,
//           a hard NT kill (force-close, hang) lost every un-flushed row — i.e. the whole session's VOTE VECTORS, the
//           ML gold — while the tick sidecars (OnMarketData, already resolution-flushed) survived. Now crash-loss is
//           bounded to the in-flight (< 60 min old) fires; completed rows carry endReason="window". Censored/in-flight
//           fires still flush "EOD"/"cutoff" at the session boundary exactly as before. Schema UNCHANGED (still 1.3).
//    v2.1.1 (2026-07-14) — RESOLUTION-BASED tick-path flush. A fire's sidecar is now written + its buffer released
//           ~30s after first-touch (or a 5-min hard cap for a censored fire), instead of only at the session-boundary
//           FlushAll. Keeps tick buffers from piling up to EOD on a fast-firing chart (e.g. an STF-only surface) and
//           lands sidecars within seconds of resolution. The ROW is unchanged (still tracks MFE/MAE to EOD via bar
//           High/Low). WriteTickPath is idempotent (PathWritten guard) since OnMarketData + FlushAll both call it.
//    v2.1.0 (2026-07-14) — RAW-TICK PATH capture (ML Phase 3). A new OnMarketData override records the true
//           last-trade tick path of every Council fire → Sentinel\Excursions\council\ticks\<fireId>.jsonl,
//           joined to the row by episodeId + fireTime (the JSONL ROW schema is UNCHANGED — still 1.3 — so the
//           Lab's existing corpus reader is untouched; the sidecars live in a subfolder it globs past). Each
//           sidecar carries tick-resolution MFE/MAE + a TICK-TRUE first-touch label (msToTargetR/msToStopR,
//           firstTouchTick) so conviction can be graded vs PATH QUALITY, not just the coarse brick first-touch
//           binary. Gated by RecordTickPath (default ON, non-NinjaScriptProperty → no codegen churn). Realtime/
//           replay only (OnMarketData never fires historical — same as-of guard the row recorder already honors).
//           Class/file identity kept _v2_0_0 so an F5 doesn't drop it off charts mid-collection.
//    v2.0.0 (2026-07-11) — clean-room fork of v1_4: GodTrades21 host removed (field, instantiation,
//           BG/FC/OBR signal path, RecordGodTrades + ShowUnderlyingIndicator properties, card tallies).
//           Council-only. New type identity (SneakyExcursionRecorder_v2_0_0) → add to charts fresh.
//    (schema 1.3 first-touch machinery + the Council/Eye seam capture are inherited verbatim from v1_4.)
// ═════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Text;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.AddOns.Sneaky;

namespace NinjaTrader.NinjaScript.Indicators.Sneaky
{
    public class SneakyExcursionRecorder_v2_0_0 : Indicator, ISneakyScrollCard
    {
        private static readonly int[] Milestones = { 1, 5, 15, 60 };   // minutes

        // schema 1.3 — the FIRST-TOUCH barrier. The label needs to know whether the TARGET or the STOP was
        // hit FIRST. R is ATR-scaled and floored well above the noise (a 20t barrier on gold is inside the
        // ~60t maxMAE noise), so a too-small brick ATR can't produce a sub-noise barrier.
        private const double BarrierAtrMult = 1.0;    // R = this × ATR(14), in ticks
        private const double BarrierMinTicks = 20.0;  // …but never below this (noise floor)

        // v2.1.1 — RESOLUTION-BASED tick-path flush (keep the pipes clear). A fire's raw-tick sidecar is written +
        // its buffer released TickPathTailMs after first-touch (captures the immediate post-R behavior), or at a hard
        // TickPathMaxMs cap for a censored fire that never resolves — so buffers never accumulate to EOD on a fast-
        // firing chart, and sidecars land within seconds (not only at the session boundary). The ROW still tracks
        // MFE/MAE to EOD independently (bar High/Low), so this only changes WHEN the tick path is written, not the row.
        // v2.5.0 — the TAIL is now a SETTING too, and it is the one that gates the D2 (knockout) study.
        // MEASURED 2026-07-31 over the running Keel bake: 0 of 80 knockouts had a tick path reaching their
        // own MFE bar — not 27%, ALL of them. Tick paths ran p50 127s / max 1413s and NONE reached 30 min,
        // because this tail (30s past first-touch) ends the path almost immediately after the stop is hit.
        // D2 lives ENTIRELY in the path AFTER the stop ("we were knocked out and the move kept going"), so
        // the exact segment a re-entry rule must be designed from is the segment being discarded by design.
        // That is not a bug — v2.1.1 wanted "the immediate post-R behavior" — but it means the tick corpus
        // can never answer D2, at any bake length, until this is raised.
        // ⚠ Raising it has the same memory cost as the cap above (buffers held longer, bigger sidecars), so
        // it is a BAKE-TIME decision. Default is unchanged at 30s precisely so this cannot alter an
        // existing run: a D2 study bake sets it deliberately (~3600000 = 60 min, matching the row window).
        // v2.4.0 — the hard cap is now a SETTING (integration spec §2.2). It was a const 300000, and on
        // Tide 25 NQ a trade routinely runs past five minutes — so the tick-true path was being truncated
        // exactly where the exit design needs it: "did MFE precede MAE, and by how much" is unanswerable
        // past the cut. ⚠ The cap exists for a real reason (buffers must not accumulate to EOD on a
        // fast-firing chart), so raising it is a BAKE-TIME decision with a memory cost, not a new default.
        // Default is unchanged at 5 min precisely so this change cannot alter an existing run.

        // Corpus layout (Docs/SENTINEL_REPLAY_SPEC + memory corpus-hygiene-and-fill-fidelity): the training corpus
        // is Sentinel\Excursions\council\<schema>\ — signal-scoped + schema-versioned, so a schema bump can't muddy
        // the old data and it stays self-documenting. SchemaVer drives BOTH the folder and the JSON "schema" field
        // so they can never drift.
        // v2.1.4 — schema 1.4 = 1.3 + the PROVENANCE block (recVer/coreVer/barLabel). A shape change earns a version
        // bump (the corpus's own rule), which is ALSO the point of the fix: pre-provenance rows stay in council\1.3\,
        // provenance-stamped rows land in council\1.4\ — so old and new logic can never pool indistinguishably.
        // v2.2.0 — schema 1.5 = 1.4 + the HONEST ENTRY PRICE. FirePx changed MEANING (HA synthetic close → real
        // last trade), which silently re-bases every MFE/MAE/barrier/firstTouch in the row. That is precisely the
        // shape change SchemaVer exists to separate: 1.5 rows land in council\1.5\ and can NEVER pool with the
        // ~9-tick-optimistic 1.4 corpus. The 1.4 rows are kept, not deleted — they are still a valid record of
        // what the old logic saw; they are simply not labels you can trade.
        private const string SchemaVer = "1.5";
        private const string RecVer    = "2.7.0";   // v2.7.0 = sticky lane in BarTag() (schema stays 1.5; recVer separates the batches --
                                                     // rows stamped 2.7.0+ are the ones where a cutoff-flushed row's lane can be trusted)

        private ADX adx;
        private ATR atr;   // for the ATR-scaled first-touch barrier
        private List<Rec> _open;
        // 2026-08-22 — PRE-FIRE LOOKBACK BUFFER. Every tick sidecar this file has ever written starts AT the
        // fire and runs forward only (TickPathMaxMs/TickPathTailMs both gate the TAIL, nothing gated the head)
        // — confirmed the hard way: replaying a real fire's sidecar into a from-scratch Python port of
        // SneakyTrend/ADXPro/WAE/CompressionBase produced NaN/neutral on every one of them, because those
        // indicators need 20-100 bars of run-up BEFORE they have a real reading and the sidecar simply never
        // recorded any. This buffer fixes that going FORWARD ONLY: it is a plain rolling window of recent
        // last-trade ticks, always running (independent of whether any fire is currently open), trimmed to
        // PreFireBufferMs. When a fire opens, Open() copies whatever is in the buffer into that fire's own
        // TickBuf as negative-ms rows (same {"ms":...,"px":...} shape the live post-fire path already writes,
        // so nothing downstream needs to change to read them — a consumer doing `fireTimeMs + ms` naturally
        // places a negative-ms row before the fire and sorts correctly). Does NOT retroactively help any
        // already-recorded sidecar — only fires recorded after this change carries lookback.
        private struct PreTick { public DateTime Time; public double Px; }
        private Queue<PreTick> _preFireBuf;
        private const int PreFireBufferMaxTicks = 200_000;   // hard safety cap independent of PreFireBufferMs,
                                                               // so a pathological tick-rate burst can't runaway
                                                               // memory regardless of the duration setting
        private string _logPath;
        private string _ticksDir;              // v2.1.0 — per-fire raw-tick path sidecar dir (council\ticks\)
        private int  _fireSeq;                 // v2.1.0 — per-session fire counter → unique FireId
        private int  _lastCouncilBias;         // edge-detect the Council verdict (0 = none/flat)

        // Sentinel glass-card readout state (drawn in OnRender via SneakySkin.Painter)
        private SneakySkin.Painter _sp;
        private int    _nCouncil;              // council fires this session
        private int    _nExternal;             // NoteSignalFire fires this session (strategies, Bridge, Keel)
        private int    _recorded;              // records written this session (bumps at each flush)
        private bool   _writerDead;            // A2 — corpus dir failed to open → card shows NO REC + a loud log line
        private bool   _writeFailed;           // A2 — a file write threw → logged ONCE (not per-row), card flags it
        private double _curAdx;                // latest ADX (for the card)
        private string _curRegime = "?";       // latest regime tag (for the card)
        private string _lastSig;               // last fired signal + its direction (for the card)
        private int    _lastDir;
        private int    _lastBar = -1;

        // ── v2.2.0 TAPE LATCH — the honest entry price ────────────────────────────────────────────────
        // PROVEN 2026-07-22 over 3,710 sidecars: Close[0] on every Sentinel bars type is the HEIKIN-ASHI
        // SYNTHETIC close — a price that NEVER TRADED. Using it as FirePx put a systematic, direction-
        // symmetric ~9-tick offset on the entry (LONG -9.30t / SHORT -9.42t) which propagated into EVERY
        // excursion + first-touch label (recorded "target-first" 52.3% vs 21.1% true; labels disagreed on
        // 44.6% of fires). The true last trade is visible ONLY in OnMarketData — latched here.
        private double _lastPx;                // last TRADE price seen on the tape (the entry reference)
        private double _lastBid, _lastAsk;     // book, recorded alongside so the spread can be measured offline

        private sealed class Rec
        {
            public string   Signal;
            public int      Dir;
            public DateTime FireTime;
            public double   FirePx;       // v2.2.0 — the REAL last-trade price at fire (was the HA synthetic Close[0])
            public string   PxSrc;        // v2.2.0 — "last" | "barclose" — how FirePx was resolved; never guess which
            public double   BarClosePx;   // v2.2.0 — the bar's own close (HA synthetic on Sentinel types); NOT tradeable
            public double   EntryBid, EntryAsk;   // v2.2.0 — book at fire (0 = unseen); lets the Lab price the spread
            public bool     PxFixed;      // v2.2.1 — the one-shot entry-price backfill has been decided for this Rec
            // v2.3.0 — the FORMING bar's LATCHED boundaries at fire (BrickState.Upper/LowerPrice, 0 = seam absent).
            // These are the only prices in the system KNOWN BEFORE THEY ARE REACHED, so they are the levels a
            // resting LIMIT could have been posted at. Stamped so the Lab can grade limit-vs-market entry offline.
            public double   BrkUpper, BrkLower;
            public int      FireBar;
            public double   MaxMFE, MaxMAE;
            public int      BarsToMFE, BarsToMAE;
            public long     MsToMFE, MsToMAE;
            public DateTime LastTime;
            public double[] MfeAt;
            public double[] MaeAt;
            public string   Regime;      // "trend" / "mid" / "chop"
            public double   Adx;
            public bool     EyeHad;
            public double   EyeScore;
            public int      EyeDir;
            public bool     EyeAligned;
            // Council verdict snapshot at fire
            public bool     Council;
            public double   Conviction;
            public string   ConvBucket;  // "LOW" / "MID" / "HIGH"
            public double   SizeMult;
            public int      Voters, Agree, Disagree;
            public string   Reasons;
            // schema 1.3 vote vector (ML spec §2.1) — the decision INPUTS the Council fused
            public Dictionary<string,int>    Votes;
            public Dictionary<string,double> VoteW;
            public double   NetScore, ActiveW;
            public int      ClockPhase;
            public double   Rvol;
            public int      MtfBias;
            public bool     LvlInPath;
            public string   LvlName;
            public string   EpisodeId;   // ML spec §10.2 — the join key (fills → episode → verdict → outcome)
            public string   CouncilVer;  // v2.1.5 (cnclVer) — the Council version that produced this verdict
            // 2026-08-09 — Confluence verdict snapshot at fire, the same treatment Council gets above.
            // Only ever populated for a Tag == "CONFLUENCE" external fire (see the external-fires drain
            // in OnBarUpdate) — a Council-sourced fire's Confluence fields stay at their false/NaN/null
            // defaults, the same honest-absence convention this file already uses for a non-Council
            // external fire's Council fields. Every gate mirrors Recorder's own complete Cnf*Ok field
            // list (SneakyRecorder_v1_0_0.cs) so an excursion row and a Recorder row carry the same
            // vocabulary — including cnfOptOk/cnfChopOk, the two gates missed in the ML scripts' own
            // lists until they were caught the same night this was added.
            public bool     Confluence;
            public int      CnfBias;
            public bool     CnfFired;
            public bool     CnfRegimeOk, CnfAdxOk, CnfLiqOk, CnfVolOk, CnfZscoreOk, CnfCvbOk, CnfOptOk;
            public bool     CnfImktOk, CnfEyeOk, CnfFlowOk, CnfPartOk, CnfStructOk, CnfClockOk;
            public bool     CnfLevelOk, CnfMtfOk, CnfProfOk, CnfChopOk, CnfProfOverextOk;
            public bool     CnfFluxOk, CnfTfusOk, CnfArchOk, CnfNewsOk;
            public string   CnfBlockedBy, CnfTriggerLabel;
            // schema 1.3 — first-touch label resolution
            public double   BarrierTicks;   // the R barrier (ATR-scaled) resolved at fire
            public int      FtFavBar;        // bars-from-fire when FAV first crossed R (-1 = never)
            public int      FtAdvBar;        // bars-from-fire when ADV first crossed R (-1 = never)
            // v2.1.0 — RAW-TICK PATH (ML Phase 3). Captured in OnMarketData, flushed to a per-fire sidecar.
            public string        FireId;          // sidecar filename + join key
            public StringBuilder TickBuf;         // {ms,px} lines, one per raw last-trade
            public int           TickCount;
            public bool          PathTrunc;        // buffer hit the size cap → path truncated
            public double        MaxFavTick, MaxAdvTick;   // tick-resolution MFE/MAE (more precise than bar High/Low)
            public long          MsToMaxFavTick, MsToMaxAdvTick;
            public long          FtFavMs, FtAdvMs;  // ms-from-fire when each barrier first crossed at TICK resolution (-1 = never)
            public long          ResolvedMs;        // ms-from-fire when the FIRST barrier latched (-1 = unresolved/censored)
            public bool          PathWritten;       // sidecar already flushed (resolution+tail or max-cap) → frozen
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "Sneaky Excursion Recorder v2.0.0";
                Description = "Council-only signal characterization: per-verdict max MAE/MFE (fire → EOD), "
                            + "conviction bucket, and the schema-1.5 tick-true first-touch label, on a Sneaky glass "
                            + "card. Pure characterization, no orders. Feeds the Observatory + ML Lab. "
                            + "Writes Sneaky\\Excursions\\*.jsonl (schema 1.5).";
                IsOverlay             = true;
                Calculate             = Calculate.OnBarClose;
                DrawOnPricePanel      = true;
                IsSuspendedWhileInactive = false;
                ShowInfo                 = true;
                CardCorner               = SneakyCardCorner.TopRight;
                RecordCouncil            = true;   // record the Council verdict as a "COUNCIL" signal
                RecordExternalFires      = true;   // v2.4.0 — and anything published via NoteSignalFire
                TickPathMaxMs            = 300000; // v2.4.0 — was a const; default deliberately unchanged
                TickPathTailMs           = 30000;  // v2.5.0 — was a const; default deliberately unchanged (see D2 note)
                PreFireBufferMs          = 7200000; // 2026-08-22 — new: 2h of rolling pre-fire lookback (see property's own note)
                EntryBackfillWindowMs    = 100;    // 2026-08-15 — was a hardcoded 0ms exact-match; see the property's own note
                RecordBelowFloor         = true;   // record the FULL conviction range (so the Lab can FIT the floor)
                RecordTickPath           = true;   // v2.1.0 — capture the raw-tick path of every fire (the Phase-3 payoff)
                CouncilStaleSec          = 90;     // ignore a Council verdict older than this
                ExcursionRetentionDays   = 0;      // 0 = keep everything; >0 = delete Excursion .jsonl older than N days on load
                ShowIndicatorLabel       = false;  // Sentinel standard: clean chart (NT name label removed)
            }
            else if (State == State.DataLoaded)
            {
                if (!ShowIndicatorLabel) Name = string.Empty;   // Sentinel label remover — NT draws the chart panel label from Name (see LabelRemover.cs)

                adx = ADX(14);
                atr = ATR(14);   // ATR-scaled first-touch barrier

                _open = new List<Rec>();
                _preFireBuf = new Queue<PreTick>();
                try
                {
                    string dir = Path.Combine(NinjaTrader.NinjaScript.AddOns.Sneaky.SneakyCore.SettingsDir, "Excursions", "council", SchemaVer);
                    Directory.CreateDirectory(dir);
                    // B4 (fidelity audit) — the council\<schema>\ corpus is the GOLDEN training record ("record raw,
                    // derive labels late" only holds while it survives). The old retention-prune pointed HERE, so
                    // enabling it would silently delete training data. The corpus is now PROTECTED: retention never
                    // prunes it. The property is kept (no codegen churn) but is a NO-OP on the corpus; if someone set
                    // it, say so loudly rather than eat the data.
                    if (ExcursionRetentionDays > 0)
                        try { SneakyCore.Log("Recorder", "retention=" + ExcursionRetentionDays + "d IGNORED — the "
                            + "council training corpus is protected and is never auto-pruned (back it up externally)."); } catch (Exception _sx) { SneakyCore.Swallow("SneakyExcursionRec.OnStateChange", _sx); }
                    string stamp = DateTime.Now.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
                    // BUGFIX (2026-08-22, P2 finding: filename collision): stamp is SECOND-precision, so two chart
                    // instances of this recorder finishing setup within the same wall-clock second (common at
                    // workspace load, when several charts init near-simultaneously) computed the IDENTICAL path
                    // and could clobber/interleave each other's writes. A per-instance GUID suffix is used rather
                    // than just widening the stamp to millisecond precision because workspace load can start
                    // multiple charts within the same millisecond too -- only a per-instance random token is robust.
                    string disambig = Guid.NewGuid().ToString("N").Substring(0, 6);
                    _logPath = Path.Combine(dir, stamp + "__" + InstName() + "__" + BarTag() + "__" + disambig + ".jsonl");
                    // v2.1.0 — raw-tick path sidecars live in council\ticks\ (a subdir the Lab globs PAST when
                    // loading rows, so they never pollute the row corpus). Joined to a row by episodeId/fireTime.
                    _ticksDir = Path.Combine(NinjaTrader.NinjaScript.AddOns.Sneaky.SneakyCore.SettingsDir, "Excursions", "council", "ticks");
                    if (RecordTickPath) Directory.CreateDirectory(_ticksDir);
                }
                catch (Exception ex)
                {
                    // A2 — do NOT die silently. Before this, a failed setup left _logPath null and the recorder
                    // ran + drew its card while writing NOTHING — a whole session could vanish looking healthy.
                    _logPath = null; _ticksDir = null; _writerDead = true;
                    try { SneakyCore.Log("Recorder", "WRITER DEAD — could not open the corpus dir; NOTHING will be "
                        + "recorded this session (" + ex.Message + "). The card shows NO REC."); } catch (Exception _sx) { SneakyCore.Swallow("SneakyExcursionRec.OnStateChange", _sx); }
                }
            }
            else if (State == State.Terminated)
            {
                FlushAll("cutoff");
                if (_sp != null) { try { _sp.Dispose(); } catch (Exception _sx) { SneakyCore.Swallow("SneakyExcursionRec.OnStateChange", _sx); } _sp = null; }
                try { SneakySkin.CardLayout.Release(this); } catch (Exception _sx) { SneakyCore.Swallow("SneakyExcursionRec.OnStateChange", _sx); }
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 25) return;   // ADX warmup

            if (Bars.IsFirstBarOfSession)
            {
                if (_open != null && _open.Count > 0) FlushAll("EOD");
                _nCouncil = 0;             // session tally resets with the session
                _nExternal = 0;
                _lastCouncilBias = 0;      // a sustained council bias re-fires next session
            }

            double tick = TickSize;
            DateTime now = Time[0];

            _curAdx = 0; try { _curAdx = adx[0]; } catch (Exception _sx) { SneakyCore.Swallow("SneakyExcursionRec.OnBarUpdate", _sx); }
            _curRegime = Regime(_curAdx);

            // iterate BACKWARDS so a completed row can be removed in-place (v2.1.2 resolution-based row flush)
            for (int i = _open.Count - 1; i >= 0; i--)
            {
                Rec r = _open[i];
                double fav, adv;
                if (r.Dir > 0) { fav = (High[0] - r.FirePx) / tick; adv = (r.FirePx - Low[0]) / tick; }
                else           { fav = (r.FirePx - Low[0]) / tick;  adv = (High[0] - r.FirePx) / tick; }
                if (fav > r.MaxMFE) { r.MaxMFE = fav; r.BarsToMFE = CurrentBar - r.FireBar; r.MsToMFE = (long)(now - r.FireTime).TotalMilliseconds; }
                if (adv > r.MaxMAE) { r.MaxMAE = adv; r.BarsToMAE = CurrentBar - r.FireBar; r.MsToMAE = (long)(now - r.FireTime).TotalMilliseconds; }

                // FIRST-TOUCH latch: the first bar each barrier is crossed (resolves target-vs-stop ORDER).
                // ⚠ FILL REALISM: fav/adv come from THIS bar's High/Low — if a single bar's range spans BOTH
                // barriers, order within that bar is unknowable; the Lab detects that (same barsTo*).
                if (r.FtFavBar < 0 && fav >= r.BarrierTicks) r.FtFavBar = CurrentBar - r.FireBar;
                if (r.FtAdvBar < 0 && adv >= r.BarrierTicks) r.FtAdvBar = CurrentBar - r.FireBar;

                double elapsedMin = (now - r.FireTime).TotalMinutes;
                for (int m = 0; m < Milestones.Length; m++)
                    if (double.IsNaN(r.MfeAt[m]) && elapsedMin >= Milestones[m]) { r.MfeAt[m] = r.MaxMFE; r.MaeAt[m] = r.MaxMAE; }

                r.LastTime = now;

                // v2.1.2 — the excursion window is COMPLETE once past the last milestone (all MfeAt/MaeAt captured):
                // stream this row to disk NOW and release it, rather than holding every fire in _open until the
                // session-roll / Terminated FlushAll (a hard NT kill before that loses the whole session's vote
                // vectors). Crash-loss is thus bounded to the in-flight (< last-milestone) fires. The tick sidecar is
                // already flushed (resolution/5-min cap) — WriteRow re-calls WriteTickPath idempotently for safety.
                if (elapsedMin >= Milestones[Milestones.Length - 1])
                {
                    WriteRow(r, "window");
                    _open.RemoveAt(i);
                }
            }

            // ── COUNCIL fire — consult the fused verdict; mirror the Bridge trigger ───────────────
            // THE AS-OF GUARD. The CouncilState seam is a LIVE singleton with no history: SetCouncilState
            // stamps UpdatedUtc = UtcNow even while the Council replays historical bars, so the freshness
            // gate cannot tell a replayed verdict from a live one. Recording on historical bars would stamp
            // whatever the seam held at load time onto old bars — silent lookahead contamination. Realtime
            // only; v.IsHistorical is belt-and-braces for a publisher that outruns this guard.
            if (RecordCouncil && State == State.Realtime)
            {
                try
                {
                    var v = NinjaTrader.NinjaScript.AddOns.Sneaky.SneakyCore.GetCouncilState(Scope(), CouncilStaleSec);
                    if (v != null && v.IsHistorical) v = null;
                    // FLOOR CALIBRATION — record the FULL conviction range, not just floor-clearing verdicts.
                    // The floor can only be FIT from data that spans BELOW it: HasEdge requires SizeMult>0, which
                    // requires conviction ≥ floor, so an HasEdge-gated corpus is CENSORED at the floor and can never
                    // show whether the floor is drawn too high. RecordBelowFloor ON (default) fires on any aligned,
                    // non-hard-vetoed verdict regardless of SizeMult; each row carries its real conviction + SizeMult
                    // so the Lab bins outcomes by conviction and solves the breakeven floor. OFF = legacy (only
                    // HasEdge / floor-clearing verdicts — what the Bridge would actually trade).
                    bool aligned = v != null && v.Bias != 0 && (RecordBelowFloor ? !v.Vetoed : v.HasEdge);
                    if (aligned && v.Bias != _lastCouncilBias)   // edge-detect: one-shot per new aligned verdict
                    {
                        Open("COUNCIL", v.Bias, v);
                        _lastCouncilBias = v.Bias;
                    }
                    else if (!aligned)
                    {
                        _lastCouncilBias = 0;   // flat/veto → next aligned edge re-fires
                    }
                }
                catch (Exception _sx) { SneakyCore.Swallow("SneakyExcursionRec.OnBarUpdate", _sx); }
            }

            // ── EXTERNAL fires — anything that is not the Council ─────────────────────────────────
            // v2.4.0. Until now this recorder could only ever see COUNCIL verdicts, because the only
            // intake was the GetCouncilState poll above. That made every strategy invisible to the
            // corpus BY CONSTRUCTION — the system built to grade decisions could not see a strategy's
            // decisions. SneakyCore.NoteSignalFire is the generic door; this drains it.
            //
            // The queue is drained even when nothing is listening upstream, so a strategy on a scope
            // with no recorder is capped and counted rather than leaking. Realtime-gated for the same
            // reason the Council path is: Core rejects historical fires at the door, and this is the
            // second lock on the same door rather than a redundant one — a caller that passes the
            // wrong isHistorical must still not contaminate the corpus.
            if (RecordExternalFires && State == State.Realtime)
            {
                try
                {
                    var fires = NinjaTrader.NinjaScript.AddOns.Sneaky.SneakyCore.DrainSignalFires(Scope());
                    // v2.4.0 — SAY SO WHEN A FIRE ARRIVES, with the intake's own counters attached.
                    // Without this, a run that produces no corpus rows cannot distinguish "the strategy
                    // never fired" from "fires were dropped before the queue" from "the recorder never
                    // drained this scope" — three different bugs with one identical symptom, and finding
                    // out which would cost a whole second run. Keel logs its own fire, so the pair of
                    // lines localises the break to one hop.
                    if (fires.Count > 0)
                    {
                        SneakyCore.Log("SneakyExcursionRec", "drained " + fires.Count
                            + " external fire(s) on " + Scope() + " | "
                            + NinjaTrader.NinjaScript.AddOns.Sneaky.SneakyCore.SignalFireStats());
                    }
                    for (int i = 0; i < fires.Count; i++)
                    {
                        var f = fires[i];
                        if (f == null || f.Dir == 0) continue;
                        // council = null → the Council columns record as NaN/null, which is the honest
                        // value for a fire the Council did not produce. The row's `signal` field carries
                        // f.Tag, and that is what separates the cohorts in the Lab.
                        // 2026-08-09 — for a Confluence-sourced fire specifically, ALSO fetch Confluence's
                        // own verdict snapshot (was previously discarded — this row got Council's honest
                        // null, but no Confluence detail either, even though Confluence was literally the
                        // one that fired). f.Scope is the exact scope the fire was published under, not
                        // ExcursionRecorder's own Scope() — the two are normally the same, but using the
                        // fire's own scope is the more precise, unambiguous choice for this specific
                        // lookup. 90s staleness window matches other GetConfluenceState reads tonight.
                        NinjaTrader.NinjaScript.AddOns.Sneaky.SneakyCore.ConfluenceState cnfSnap = null;
                        if (f.Tag == "CONFLUENCE")
                        {
                            try { cnfSnap = NinjaTrader.NinjaScript.AddOns.Sneaky.SneakyCore.GetConfluenceState(f.Scope, 90.0); }
                            catch (Exception _sx) { SneakyCore.Swallow("SneakyExcursionRec.ExternalFires.Cnf", _sx); }
                        }
                        Open(f.Tag, f.Dir, null, cnfSnap);
                        _nExternal++;
                    }
                }
                catch (Exception _sx) { SneakyCore.Swallow("SneakyExcursionRec.ExternalFires", _sx); }
            }
        }

        // v2.1.0 — RAW-TICK PATH (ML Phase 3). OnMarketData is the only place the true last-trade price is visible
        // (Close[0] is a synthetic brick close on HA/TBars). Fires realtime/replay only (never historical), same
        // data thread as OnBarUpdate → no lock. Appends {ms,px} to every open fire's path + latches tick-true
        // MFE/MAE and the first-touch ms; the sidecar is written when the fire flushes.
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            // v2.2.0 — TAPE LATCH, deliberately BEFORE every guard below. A fire must be priced from the real
            // tape even when tick-path capture is OFF or no fire is currently open — those guards exist to skip
            // path WORK, not to skip knowing the price. (Latching after them was never a bug we had, because
            // FirePx used Close[0]; it would have become one the moment we started reading _lastPx.)
            if      (e.MarketDataType == MarketDataType.Last) _lastPx  = e.Price;
            else if (e.MarketDataType == MarketDataType.Ask)  _lastAsk = e.Price;
            else if (e.MarketDataType == MarketDataType.Bid)  _lastBid = e.Price;

            if (e.MarketDataType != MarketDataType.Last) return;

            // PRE-FIRE LOOKBACK BUFFER — runs independent of whether a fire is open (unlike the per-fire
            // path loop below, which still requires _open.Count > 0). Trimmed by both AGE (PreFireBufferMs)
            // and COUNT (PreFireBufferMaxTicks) so neither a long duration nor a fast tape can run memory away.
            if (RecordTickPath && _preFireBuf != null)
            {
                _preFireBuf.Enqueue(new PreTick { Time = e.Time, Px = e.Price });
                while (_preFireBuf.Count > 0
                       && (_preFireBuf.Count > PreFireBufferMaxTicks
                           || (e.Time - _preFireBuf.Peek().Time).TotalMilliseconds > PreFireBufferMs))
                {
                    _preFireBuf.Dequeue();
                }
            }

            if (!RecordTickPath || _open == null || _open.Count == 0) return;
            double px = e.Price, tick = TickSize;
            if (tick <= 0) return;
            for (int i = 0; i < _open.Count; i++)
            {
                Rec r = _open[i];
                if (r.PathWritten) continue;   // sidecar already flushed → this fire's path is frozen
                long ms = (long)(e.Time - r.FireTime).TotalMilliseconds;
                if (ms < 0) ms = 0;

                // v2.2.1 — ENTRY BACKFILL, one-shot, and it must precede every use of r.FirePx below.
                // OnMarketData runs AFTER OnBarUpdate for the tick that closed the bar, so `_lastPx` (latched at
                // Open) is the trade BEFORE the triggering one. When this first path tick lands in the SAME
                // millisecond as the fire it IS that triggering trade — the first price actually transactable —
                // so adopt it. ⚠ DELIBERATELY NOT UNCONDITIONAL: if the next trade is seconds away (illiquid, or
                // a session gap) adopting it would import real forward drift into the entry, which is lookahead.
                // In that case we keep _lastPx and say so via pxSrc. Measured impact: ~0 on Sentinel bar types
                // (already sub-tick), but on a jump-driven CASCADE (Renko printed 5 bricks off one 7-tick move,
                // all sharing one stale entry) it was the whole jump. Safe here: Open() is called AFTER the
                // bar-level excursion loop, so no High/Low measurement has used FirePx yet.
                //
                // 2026-08-15 — was `ms == 0` exactly. verify_firepx.py's own non-circular check ("last"-sourced
                // rows only) found NQ failing (median 2.0t off, 46-58% adverse-skewed) while bfill% read 0% on
                // EVERY lane, both NQ and MNQ — meaning this backfill essentially never fired at all, on either
                // instrument, because real tick timestamps almost never land in the exact same millisecond as
                // the fire. Widened to a bounded window (now a proper setting, EntryBackfillWindowMs, default
                // 100ms — see its own [Display] note above for the reasoning): still far smaller than the
                // "seconds" of drift this guard was actually built to prevent, but wide enough to catch a real
                // tape's normal inter-print gaps instead of requiring an essentially-impossible exact match.
                if (!r.PxFixed)
                {
                    r.PxFixed = true;
                    if (ms <= EntryBackfillWindowMs) { r.FirePx = px; r.PxSrc = "firsttick"; }
                }

                double favT = r.Dir > 0 ? (px - r.FirePx) / tick : (r.FirePx - px) / tick;
                double advT = -favT;
                if (favT > r.MaxFavTick) { r.MaxFavTick = favT; r.MsToMaxFavTick = ms; }
                if (advT > r.MaxAdvTick) { r.MaxAdvTick = advT; r.MsToMaxAdvTick = ms; }
                if (r.FtFavMs < 0 && favT >= r.BarrierTicks) r.FtFavMs = ms;   // tick-true first-touch (target)
                if (r.FtAdvMs < 0 && advT >= r.BarrierTicks) r.FtAdvMs = ms;   // tick-true first-touch (stop)
                if (r.ResolvedMs < 0 && (r.FtFavMs >= 0 || r.FtAdvMs >= 0)) r.ResolvedMs = ms;   // first barrier = resolution
                if (r.TickBuf != null && r.TickBuf.Length < 4000000)
                {
                    r.TickBuf.Append("{\"ms\":").Append(ms).Append(",\"px\":")
                             .Append(px.ToString("0.#####", CultureInfo.InvariantCulture)).Append("}\n");
                    r.TickCount++;
                }
                else if (r.TickBuf != null) r.PathTrunc = true;

                // RESOLUTION-BASED FLUSH — write + release this fire's path once it has resolved and settled
                // (TailMs past first-touch), or hit the hard cap for a censored fire. Keeps buffers from piling up.
                bool tailDone = r.ResolvedMs >= 0 && (ms - r.ResolvedMs) >= TickPathTailMs;
                if (tailDone || ms >= TickPathMaxMs) WriteTickPath(r);
            }
        }

        private void Open(string signal, int dir, NinjaTrader.NinjaScript.AddOns.Sneaky.SneakyCore.CouncilState council = null,
                           NinjaTrader.NinjaScript.AddOns.Sneaky.SneakyCore.ConfluenceState confluence = null)
        {
            double a = 0; try { a = adx[0]; } catch (Exception _sx) { SneakyCore.Swallow("SneakyExcursionRec.Open", _sx); }
            bool eyeHad = false; double eyeScore = double.NaN; int eyeDir = 0; bool eyeAligned = false;
            if (State == State.Realtime)   // live Eye registry has no historical value → realtime fires only
            {
                try
                {
                    var ev = NinjaTrader.NinjaScript.AddOns.Sneaky.SneakyCore.GetEyeVerdict(InstName(), 0);
                    if (ev != null) { eyeHad = true; eyeScore = ev.Score; eyeDir = ev.Direction; eyeAligned = (eyeDir == dir); }
                }
                catch (Exception _sx) { SneakyCore.Swallow("SneakyExcursionRec.Open", _sx); }
            }

            // v2.3.0 — LATCHED BOUNDARIES of the bar now forming. Read on the BARE scope (bar-type seam) right at
            // the fire: the bar that just closed created the next one, so BrickState already holds ITS boundaries —
            // i.e. exactly the two prices a resting limit could be posted at, known before they are reached.
            // ⚠ ACCEPTANCE CHECK (verify, don't assume the ordering): firePx must sit BETWEEN BrkLower and
            // BrkUpper. If it does not, the recorder is reading the CLOSED bar's boundaries, not the forming one.
            double brkUp = 0, brkDn = 0;
            try
            {
                var bs = NinjaTrader.NinjaScript.AddOns.Sneaky.SneakyCore.GetBrickState(BareScope(), 90);
                if (bs != null) { brkUp = bs.UpperPrice; brkDn = bs.LowerPrice; }
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyExcursionRec.Open", _sx); }

            var r = new Rec
            {
                BrkUpper = brkUp, BrkLower = brkDn,
                // v2.2.0 — FirePx is the REAL last trade, not Close[0]. See the tape-latch note above: on every
                // Sentinel bars type Close[0] is a Heikin-Ashi synthetic that never trades, and it was the single
                // reference for MFE / MAE / barrier / first-touch — so the whole corpus's labels inherited a
                // ~9-tick optimistic offset. Close[0] is still recorded, as BarClosePx, but it is not the entry.
                Signal = signal, Dir = dir, FireTime = Time[0],
                FirePx = (_lastPx > 0 ? _lastPx : Close[0]),
                PxSrc  = (_lastPx > 0 ? "last" : "barclose"),   // "barclose" = tape silent → NOT a tradeable entry
                BarClosePx = Close[0], EntryBid = _lastBid, EntryAsk = _lastAsk,
                FireBar = CurrentBar,
                MaxMFE = 0, MaxMAE = 0, LastTime = Time[0],
                MfeAt = new double[Milestones.Length], MaeAt = new double[Milestones.Length],
                Regime = Regime(a), Adx = a,
                EyeHad = eyeHad, EyeScore = eyeScore, EyeDir = eyeDir, EyeAligned = eyeAligned,
                Council    = council != null,
                Conviction = council != null ? council.Conviction : double.NaN,
                ConvBucket = council != null ? Bucket(council.Conviction) : null,
                SizeMult   = council != null ? council.SizeMult : double.NaN,
                Voters     = council != null ? council.Voters : 0,
                Agree      = council != null ? council.Agree : 0,
                Disagree   = council != null ? council.Disagree : 0,
                Reasons    = council != null ? Trunc(council.Reasons, 120) : null,
                // schema 1.3 vote vector (ML spec §2.1) — copy the decision inputs (Core already snapshotted the dicts)
                Votes      = council != null ? council.Votes : null,
                VoteW      = council != null ? council.VoteW : null,
                NetScore   = council != null ? council.NetScore : double.NaN,
                ActiveW    = council != null ? council.ActiveW : double.NaN,
                ClockPhase = council != null ? council.ClockPhase : -1,
                Rvol       = council != null ? council.Rvol : double.NaN,
                MtfBias    = council != null ? council.MtfBias : 0,
                LvlInPath  = council != null && council.LevelInPath,
                LvlName    = council != null ? council.LevelName : null,
                EpisodeId  = council != null ? council.EpisodeId : null,
                CouncilVer = council != null ? council.CouncilVersion : null,   // v2.1.5 — cnclVer provenance
                // 2026-08-09 — Confluence snapshot, same null-means-honestly-absent convention as Council above.
                Confluence     = confluence != null,
                CnfBias        = confluence != null ? confluence.Signal : 0,
                CnfFired       = confluence != null && confluence.Fired,
                CnfRegimeOk    = confluence != null && confluence.RegimeOk,
                CnfAdxOk       = confluence != null && confluence.AdxOk,
                CnfLiqOk       = confluence != null && confluence.LiqOk,
                CnfVolOk       = confluence != null && confluence.VolOk,
                CnfZscoreOk    = confluence != null && confluence.ZscoreOk,
                CnfCvbOk       = confluence != null && confluence.CvbOk,
                CnfOptOk       = confluence != null && confluence.OptOk,
                CnfImktOk      = confluence != null && confluence.ImktOk,
                CnfEyeOk       = confluence != null && confluence.EyeOk,
                CnfFlowOk      = confluence != null && confluence.FlowOk,
                CnfPartOk      = confluence != null && confluence.PartOk,
                CnfStructOk    = confluence != null && confluence.StructOk,
                CnfClockOk     = confluence != null && confluence.ClockOk,
                CnfLevelOk     = confluence != null && confluence.LevelOk,
                CnfMtfOk       = confluence != null && confluence.MtfOk,
                CnfProfOk      = confluence != null && confluence.ProfOk,
                CnfChopOk      = confluence != null && confluence.ChopOk,
                CnfProfOverextOk = confluence != null && confluence.ProfOverextOk,
                CnfFluxOk      = confluence != null && confluence.FluxOk,
                CnfTfusOk      = confluence != null && confluence.TfusOk,
                CnfArchOk      = confluence != null && confluence.ArchOk,
                CnfNewsOk      = confluence != null && confluence.NewsOk,
                CnfBlockedBy   = confluence != null ? confluence.BlockedBy : null,
                CnfTriggerLabel = confluence != null ? confluence.TriggerLabel : null,
                // first-touch barrier (ATR-scaled, floored above the noise), touch bars not yet resolved
                BarrierTicks = FirstTouchBarrier(),
                FtFavBar = -1, FtAdvBar = -1,
                // v2.1.0 raw-tick path — buffer + tick-true first-touch latches (-1 = never)
                TickBuf = RecordTickPath ? new StringBuilder(8192) : null,
                MaxFavTick = 0, MaxAdvTick = 0, FtFavMs = -1, FtAdvMs = -1, ResolvedMs = -1
            };
            for (int m = 0; m < Milestones.Length; m++) { r.MfeAt[m] = double.NaN; r.MaeAt[m] = double.NaN; }
            // v2.1.0 — stable per-fire id (fireTime + inst + dir + session seq) = sidecar filename + row join key
            r.FireId = Time[0].ToUniversalTime().ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture)
                     + "_" + InstName() + "_" + (dir > 0 ? "L" : "S") + "_" + (++_fireSeq);

            // 2026-08-22 — seed this fire's tick path with whatever the pre-fire buffer is holding, as
            // negative-ms rows (same {"ms":...,"px":...} shape as the live post-fire loop in OnMarketData,
            // so no downstream consumer needs to change). Same truncation guard (TickBuf.Length cap) as the
            // live path uses, for consistency — a bounded pre-fire buffer realistically never gets close to
            // it, but this fails the same safe way if it ever did.
            if (r.TickBuf != null && _preFireBuf != null)
            {
                foreach (var pt in _preFireBuf)
                {
                    if (r.TickBuf.Length >= 4000000) { r.PathTrunc = true; break; }
                    long preMs = (long)(pt.Time - r.FireTime).TotalMilliseconds;
                    r.TickBuf.Append("{\"ms\":").Append(preMs).Append(",\"px\":")
                             .Append(pt.Px.ToString("0.#####", CultureInfo.InvariantCulture)).Append("}\n");
                    r.TickCount++;
                }
            }
            _open.Add(r);

            // feed the on-chart card
            if (signal == "COUNCIL") _nCouncil++;
            _lastSig = signal; _lastDir = dir; _lastBar = CurrentBar;
        }

        private static string Regime(double a)
        {
            if (double.IsNaN(a) || a <= 0) return "?";
            if (a >= 25) return "trend";
            if (a <= 18) return "chop";
            return "mid";
        }

        // conviction bucket (LOW<0.50 / MID 0.50–0.70 / HIGH ≥0.70)
        private static string Bucket(double c)
        {
            if (double.IsNaN(c)) return null;
            if (c >= 0.70) return "HIGH";
            if (c >= 0.50) return "MID";
            return "LOW";
        }

        private static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max);
        }

        // the ATR-scaled first-touch barrier R (ticks), floored above the noise. Resolved at fire.
        private double FirstTouchBarrier()
        {
            double atrTicks = 0;
            try { if (atr != null && TickSize > 0) atrTicks = atr[0] / TickSize; } catch (Exception _sx) { SneakyCore.Swallow("SneakyExcursionRec.FirstTouchBarrier", _sx); }
            return Math.Max(BarrierMinTicks, BarrierAtrMult * atrTicks);
        }

        private void FlushAll(string reason)
        {
            if (_open == null || _open.Count == 0) return;
            var sb = new StringBuilder(4096);
            foreach (Rec r in _open) { sb.Append(ToJson(r, reason)).Append(Environment.NewLine); WriteTickPath(r); }
            int n = _open.Count;
            _open.Clear();
            if (_logPath == null) return;
            try { File.AppendAllText(_logPath, sb.ToString()); _recorded += n; } catch (Exception ex) { NoteWriteFail(ex); }
        }

        // A2 — a file write threw. Surface it ONCE (not per-row spam) so a disk-full / locked-file / permission
        // failure can't silently drain the corpus while the card still says REC.
        private void NoteWriteFail(Exception ex)
        {
            if (_writeFailed) return;
            _writeFailed = true;
            try { SneakyCore.Log("Recorder", "WRITE FAILED — corpus rows are being LOST (" + ex.Message
                + "). Fix the sink; the card flags it."); } catch (Exception _sx) { SneakyCore.Swallow("SneakyExcursionRec.NoteWriteFail", _sx); }
        }

        // v2.1.2 — stream ONE completed row to the 1.3 corpus (crash-safety: don't hold the whole session in _open).
        // Ensures the fire's tick sidecar is flushed alongside the row (WriteTickPath is idempotent via PathWritten).
        private void WriteRow(Rec r, string reason)
        {
            WriteTickPath(r);
            if (_logPath == null) return;
            try { File.AppendAllText(_logPath, ToJson(r, reason) + Environment.NewLine); _recorded++; } catch (Exception ex) { NoteWriteFail(ex); }
        }

        // v2.1.0 — write one fire's raw-tick path to council\ticks\<fireId>.jsonl. Header carries the join keys
        // (episodeId + fireTime), denormalized context, tick-resolution MFE/MAE, and a TICK-TRUE first-touch label
        // (msToTargetR/msToStopR/firstTouchTick) — the fill-honest counterpart to the row's brick first-touch.
        private void WriteTickPath(Rec r)
        {
            if (!RecordTickPath || r == null || r.PathWritten || r.TickBuf == null || r.TickCount == 0 || _ticksDir == null) return;
            try
            {
                Directory.CreateDirectory(_ticksDir);   // robust if RecordTickPath was toggled ON after DataLoaded
                int ftT; bool ftAmbigT = false;
                if (r.FtFavMs >= 0 && r.FtAdvMs >= 0)
                {
                    if (r.FtFavMs < r.FtAdvMs) ftT = 1;
                    else if (r.FtAdvMs < r.FtFavMs) ftT = -1;
                    else { ftT = 0; ftAmbigT = true; }   // same ms (rare at tick resolution) ⇒ order unknown
                }
                else if (r.FtFavMs >= 0) ftT = 1;
                else if (r.FtAdvMs >= 0) ftT = -1;
                else ftT = 0;                            // censored — neither barrier hit before flush

                var sb = new StringBuilder(4096);
                sb.Append("{\"schema\":\"ctick.4\",\"kind\":\"council_tickpath\"")
                  .Append(",\"recVer\":").Append(Q(RecVer))                                  // v2.1.4 provenance (A1)
                  .Append(",\"coreVer\":").Append(Q(SneakyCore.Version))
                  .Append(",\"cnclVer\":").Append(Q(r.CouncilVer))                           // v2.1.5 cnclVer
                  .Append(",\"barLabel\":").Append(Q(SneakyCore.FriendlyBartag(BarTag())))
                  .Append(",\"fireId\":").Append(Q(r.FireId))
                  .Append(",\"episodeId\":").Append(Q(r.EpisodeId))
                  .Append(",\"scope\":").Append(Q(Scope()))
                  .Append(",\"inst\":").Append(Q(InstName()))
                  .Append(",\"bartype\":").Append(Q(BarTag()))
                  .Append(",\"signal\":").Append(Q(r.Signal))
                  .Append(",\"dir\":").Append(r.Dir)
                  .Append(",\"fireTime\":").Append(Q(Iso(r.FireTime)))
                  .Append(",\"firePx\":").Append(F(r.FirePx))
                  // v2.2.0 (ctick.4) — firePx is now the REAL last trade. pxSrc says how it resolved, barClosePx
                  // keeps the (untradeable) HA synthetic, and the book lets the Lab price the crossing cost.
                  .Append(",\"pxSrc\":").Append(Q(r.PxSrc))
                  .Append(",\"barClosePx\":").Append(F(r.BarClosePx))
                  .Append(",\"entryBid\":").Append(F(r.EntryBid))
                  .Append(",\"entryAsk\":").Append(F(r.EntryAsk))
                  // v2.3.0 — the forming bar's LATCHED boundaries: the levels a resting LIMIT could have used
                  .Append(",\"brkUpper\":").Append(F(r.BrkUpper))
                  .Append(",\"brkLower\":").Append(F(r.BrkLower))
                  .Append(",\"conviction\":").Append(F(r.Conviction))
                  .Append(",\"sizeMult\":").Append(F(r.SizeMult))
                  // v2.1.6 (ctick.3) — ENTRY CONTEXT on the sidecar header so every tick-path is SELF-DESCRIBING
                  // (no lossy episodeId join to the row corpus; ~⅔ of paths had no matching row). Fields mirror the
                  // row's context block and are already captured at Open(); this is display-only, no new capture.
                  .Append(",\"regime\":").Append(Q(r.Regime))
                  .Append(",\"adx\":").Append(F(r.Adx))
                  .Append(",\"clockPhase\":").Append(r.ClockPhase)
                  .Append(",\"rvol\":").Append(F(r.Rvol))
                  .Append(",\"mtfBias\":").Append(r.MtfBias)
                  .Append(",\"netScore\":").Append(F(r.NetScore))
                  .Append(",\"activeW\":").Append(F(r.ActiveW))
                  .Append(",\"voters\":").Append(r.Voters)
                  .Append(",\"agree\":").Append(r.Agree)
                  .Append(",\"disagree\":").Append(r.Disagree)
                  .Append(",\"barrierTicks\":").Append(F(r.BarrierTicks))
                  .Append(",\"maxFavTicks\":").Append(F(r.MaxFavTick))
                  .Append(",\"maxAdvTicks\":").Append(F(r.MaxAdvTick))
                  .Append(",\"msToMaxFav\":").Append(r.MsToMaxFavTick)
                  .Append(",\"msToMaxAdv\":").Append(r.MsToMaxAdvTick)
                  .Append(",\"msToTargetR\":").Append(r.FtFavMs)
                  .Append(",\"msToStopR\":").Append(r.FtAdvMs)
                  .Append(",\"firstTouchTick\":").Append(ftT)
                  .Append(",\"ftAmbigTick\":").Append(ftAmbigT ? "true" : "false")
                  .Append(",\"ticks\":").Append(r.TickCount)
                  .Append(",\"trunc\":").Append(r.PathTrunc ? "true" : "false")
                  .Append("}\n");
                sb.Append(r.TickBuf);
                File.AppendAllText(Path.Combine(_ticksDir, r.FireId + ".jsonl"), sb.ToString());
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyExcursionRec.WriteTickPath", _sx); }
            r.PathWritten = true;   // idempotent: OnMarketData + FlushAll both call this
            r.TickBuf = null;       // release the buffer (keeps memory bounded on a fast-firing chart)
        }

        private string ToJson(Rec r, string reason)
        {
            var sb = new StringBuilder(720);
            sb.Append('{')
              .Append("\"schema\":\"").Append(SchemaVer).Append("\",\"kind\":\"excursion\"")
              // v2.1.4 PROVENANCE (schema 1.4, A1) — which LOGIC wrote this row, so old/new fusions never pool
              // indistinguishably. barLabel = the human bartag ("SentinelFlux 8") denormalized for readers.
              .Append(",\"recVer\":").Append(Q(RecVer))
              .Append(",\"coreVer\":").Append(Q(SneakyCore.Version))
              .Append(",\"cnclVer\":").Append(Q(r.CouncilVer))
              .Append(",\"barLabel\":").Append(Q(SneakyCore.FriendlyBartag(BarTag())))
              .Append(",\"inst\":").Append(Q(InstName()))
              .Append(",\"bartype\":").Append(Q(BarTag()))
              .Append(",\"signal\":").Append(Q(r.Signal))
              .Append(",\"dir\":").Append(r.Dir)
              .Append(",\"regime\":").Append(Q(r.Regime))
              .Append(",\"adx\":").Append(F(r.Adx))
              .Append(",\"eyeHad\":").Append(r.EyeHad ? "true" : "false")
              .Append(",\"eyeScore\":").Append(F(r.EyeScore))
              .Append(",\"eyeDir\":").Append(r.EyeHad ? r.EyeDir.ToString(CultureInfo.InvariantCulture) : "null")
              .Append(",\"eyeAligned\":").Append(r.EyeHad ? (r.EyeAligned ? "true" : "false") : "null")
              // council fields
              .Append(",\"council\":").Append(r.Council ? "true" : "false")
              .Append(",\"conviction\":").Append(F(r.Conviction))
              .Append(",\"convBucket\":").Append(Q(r.ConvBucket))
              .Append(",\"sizeMult\":").Append(F(r.SizeMult))
              .Append(",\"voters\":").Append(r.Voters)
              .Append(",\"agree\":").Append(r.Agree)
              .Append(",\"disagree\":").Append(r.Disagree)
              .Append(",\"reasons\":").Append(Q(r.Reasons))
              // schema 1.3 vote vector (ML spec §2.1) — the decision INPUTS, for offline WEIGHT fitting
              .Append(",\"netScore\":").Append(F(r.NetScore))
              .Append(",\"activeW\":").Append(F(r.ActiveW))
              .Append(",\"clockPhase\":").Append(r.ClockPhase)
              .Append(",\"rvol\":").Append(F(r.Rvol))
              .Append(",\"mtfBias\":").Append(r.MtfBias)
              .Append(",\"levelInPath\":").Append(r.LvlInPath ? "true" : "false")
              .Append(",\"levelName\":").Append(Q(r.LvlName))
              .Append(",\"votes\":").Append(JVotes(r.Votes))
              .Append(",\"voteW\":").Append(JVoteW(r.VoteW))
              .Append(",\"episodeId\":").Append(Q(r.EpisodeId))
              // 2026-08-09 — Confluence verdict snapshot, same honest-absence convention as the Council
              // fields above (false/0/null when this row wasn't a Confluence-sourced fire). Mirrors
              // Recorder's own complete Cnf*Ok field set, so a row from either file uses the same
              // vocabulary. See the Rec struct's own comment for why this only ever populates on a
              // Tag == "CONFLUENCE" external fire.
              // BUGFIX (2026-08-22, P2 finding: Cnf* null-vs-zero): this comment always claimed a
              // "false/0/null" honest-absence convention, but cnfBias/cnfFired/every cnfXxxOk gate below
              // was emitted UNCONDITIONALLY as 0/false even on a non-Confluence row -- contradicting the
              // suite's "null means absent, not zero" rule (already correctly enforced a few lines up for
              // eyeDir/eyeAligned, gated on r.EyeHad). r.Confluence IS this row's "Have" flag (identical
              // role to HaveEye/EyeHad); every Cnf* field below is now gated on it and null when absent,
              // exactly matching the eyeDir/eyeAligned pattern. cnfBlockedBy/cnfTriggerLabel were already
              // correct (Q() nulls a null string) and are unchanged.
              .Append(",\"confluence\":").Append(r.Confluence ? "true" : "false")
              .Append(",\"cnfBias\":").Append(r.Confluence ? r.CnfBias.ToString(CultureInfo.InvariantCulture) : "null")
              .Append(",\"cnfFired\":").Append(r.Confluence ? (r.CnfFired ? "true" : "false") : "null")
              .Append(",\"cnfRegimeOk\":").Append(r.Confluence ? (r.CnfRegimeOk ? "true" : "false") : "null")
              .Append(",\"cnfAdxOk\":").Append(r.Confluence ? (r.CnfAdxOk ? "true" : "false") : "null")
              .Append(",\"cnfLiqOk\":").Append(r.Confluence ? (r.CnfLiqOk ? "true" : "false") : "null")
              .Append(",\"cnfVolOk\":").Append(r.Confluence ? (r.CnfVolOk ? "true" : "false") : "null")
              .Append(",\"cnfZscoreOk\":").Append(r.Confluence ? (r.CnfZscoreOk ? "true" : "false") : "null")
              .Append(",\"cnfCvbOk\":").Append(r.Confluence ? (r.CnfCvbOk ? "true" : "false") : "null")
              .Append(",\"cnfOptOk\":").Append(r.Confluence ? (r.CnfOptOk ? "true" : "false") : "null")
              .Append(",\"cnfImktOk\":").Append(r.Confluence ? (r.CnfImktOk ? "true" : "false") : "null")
              .Append(",\"cnfEyeOk\":").Append(r.Confluence ? (r.CnfEyeOk ? "true" : "false") : "null")
              .Append(",\"cnfFlowOk\":").Append(r.Confluence ? (r.CnfFlowOk ? "true" : "false") : "null")
              .Append(",\"cnfPartOk\":").Append(r.Confluence ? (r.CnfPartOk ? "true" : "false") : "null")
              .Append(",\"cnfStructOk\":").Append(r.Confluence ? (r.CnfStructOk ? "true" : "false") : "null")
              .Append(",\"cnfClockOk\":").Append(r.Confluence ? (r.CnfClockOk ? "true" : "false") : "null")
              .Append(",\"cnfLevelOk\":").Append(r.Confluence ? (r.CnfLevelOk ? "true" : "false") : "null")
              .Append(",\"cnfMtfOk\":").Append(r.Confluence ? (r.CnfMtfOk ? "true" : "false") : "null")
              .Append(",\"cnfProfOk\":").Append(r.Confluence ? (r.CnfProfOk ? "true" : "false") : "null")
              .Append(",\"cnfChopOk\":").Append(r.Confluence ? (r.CnfChopOk ? "true" : "false") : "null")
              .Append(",\"cnfProfOverextOk\":").Append(r.Confluence ? (r.CnfProfOverextOk ? "true" : "false") : "null")
              .Append(",\"cnfFluxOk\":").Append(r.Confluence ? (r.CnfFluxOk ? "true" : "false") : "null")
              .Append(",\"cnfTfusOk\":").Append(r.Confluence ? (r.CnfTfusOk ? "true" : "false") : "null")
              .Append(",\"cnfArchOk\":").Append(r.Confluence ? (r.CnfArchOk ? "true" : "false") : "null")
              .Append(",\"cnfNewsOk\":").Append(r.Confluence ? (r.CnfNewsOk ? "true" : "false") : "null")
              .Append(",\"cnfBlockedBy\":").Append(Q(r.CnfBlockedBy))
              .Append(",\"cnfTriggerLabel\":").Append(Q(r.CnfTriggerLabel))
              .Append(",\"fireTime\":").Append(Q(Iso(r.FireTime)))
              .Append(",\"firePx\":").Append(F(r.FirePx))
              // v2.2.0 (schema 1.5) — the entry is the REAL tape price; every MFE/MAE/barrier/firstTouch below is
              // now measured from a price that can actually be filled. pxSrc="barclose" marks the rare fallback.
              .Append(",\"pxSrc\":").Append(Q(r.PxSrc))
              .Append(",\"barClosePx\":").Append(F(r.BarClosePx))
              .Append(",\"entryBid\":").Append(F(r.EntryBid))
              .Append(",\"entryAsk\":").Append(F(r.EntryAsk))
              // v2.3.0 — latched boundaries at fire; limit-vs-market entry is gradeable from these + the tick path
              .Append(",\"brkUpper\":").Append(F(r.BrkUpper))
              .Append(",\"brkLower\":").Append(F(r.BrkLower))
              .Append(",\"maxMFE\":").Append(F(r.MaxMFE))
              .Append(",\"maxMAE\":").Append(F(r.MaxMAE))
              .Append(",\"barsToMFE\":").Append(r.BarsToMFE)
              .Append(",\"barsToMAE\":").Append(r.BarsToMAE)
              .Append(",\"msToMFE\":").Append(r.MsToMFE)
              .Append(",\"msToMAE\":").Append(r.MsToMAE)
              .Append(",\"bars\":").Append(r.FireBar >= 0 ? (CurrentBar - r.FireBar) : 0);
            for (int m = 0; m < Milestones.Length; m++)
                sb.Append(",\"mfe").Append(Milestones[m]).Append("\":").Append(F(r.MfeAt[m]))
                  .Append(",\"mae").Append(Milestones[m]).Append("\":").Append(F(r.MaeAt[m]));
            // schema 1.3 — the FIRST-TOUCH label. barsToTargetR/barsToStopR are raw (−1 = never), so the Lab
            // can re-derive the label or detect intrabar ambiguity itself; firstTouch is the resolved convenience:
            // +1 target-first · −1 stop-first · 0 neither by end · ftAmbig = both crossed on the SAME bar (order unknown).
            int firstTouch; bool ftAmbig = false;
            if (r.FtFavBar >= 0 && r.FtAdvBar >= 0)
            {
                if (r.FtFavBar < r.FtAdvBar) firstTouch = 1;
                else if (r.FtAdvBar < r.FtFavBar) firstTouch = -1;
                else { firstTouch = 0; ftAmbig = true; }   // same bar ⇒ unresolved
            }
            else if (r.FtFavBar >= 0) firstTouch = 1;
            else if (r.FtAdvBar >= 0) firstTouch = -1;
            else firstTouch = 0;                            // censored — neither barrier hit by end
            sb.Append(",\"barrierTicks\":").Append(F(r.BarrierTicks))
              .Append(",\"barsToTargetR\":").Append(r.FtFavBar)
              .Append(",\"barsToStopR\":").Append(r.FtAdvBar)
              .Append(",\"firstTouch\":").Append(firstTouch)
              .Append(",\"ftAmbig\":").Append(ftAmbig ? "true" : "false");
            sb.Append(",\"endReason\":").Append(Q(reason))
              .Append(",\"endTime\":").Append(Q(Iso(r.LastTime)))
              .Append('}');
            return sb.ToString();
        }

        // ── the Sentinel "flight-instrument" glass card (SneakySkin.Painter) ──────────────
        // 2026-08-08 — converted from SneakySkin.CardLayout.Place() (single, non-scrolling
        // placement — what the real upstream source actually uses) to the shared PlaceScroll()/
        // ISneakyScrollCard rotation every other Sneaky card participates in, same conversion
        // already applied to BarDump earlier tonight for the same reason.
        protected override void OnRender(NinjaTrader.Gui.Chart.ChartControl chartControl, NinjaTrader.Gui.Chart.ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (!ShowInfo || RenderTarget == null || ChartPanel == null) return;
            try
            {
                if (_sp == null) _sp = new SneakySkin.Painter();
                _sp.Begin(RenderTarget);
                RenderCard();
                _sp.End();
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyExcursionRec.OnRender", _sx); }
        }

        public void ConductorRender(SharpDX.Direct2D1.RenderTarget rt)
        {
            if (!ShowInfo || rt == null) return;
            try { if (_sp == null) _sp = new SneakySkin.Painter(); _sp.Begin(rt); RenderCard(); }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyExcursionRec.ConductorRender", _sx); }
            finally { try { _sp.End(); } catch { } }
        }

        private void RenderCard()
        {
                const float cw = 258f, ch = 150f;
                // dock via the shared scroll rotation so this card never covers another Sneaky card
                SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
                var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
                    ChartPanel.X, ChartPanel.Y, ChartPanel.W, ChartPanel.H, cw, ch);
                if (slot.Width <= 0f) return;

                int openCount = _open != null ? _open.Count : 0;
                bool live = RecordCouncil;
                var edge = openCount > 0 ? SneakySkin.CAccent : (live ? SneakySkin.CLine : SneakySkin.CDim);
                var r = _sp.Card(slot.X, slot.Y, cw, ch, edge);

                // header: live dot + title + state pill
                _sp.Dot(r.Left + 5f, r.Top + 8f, live ? SneakySkin.CAccent : SneakySkin.CMute, live);
                _sp.Text("SNEAKY EXCURSION", r.Left + 16f, r.Top, r.Width - 66f, 16f, SneakySkin.CInk, 11f, true);
                bool dead = _writerDead || _writeFailed;                         // A2 — writer failure is VISIBLE
                string st = dead ? "NO REC" : (openCount > 0 ? "REC" : "IDLE");
                var stCol = dead ? SneakySkin.CDown : (openCount > 0 ? SneakySkin.CAccent : SneakySkin.CMute);
                _sp.Pill(st, r.Right, r.Top - 1f, stCol);

                var lead = SharpDX.DirectWrite.TextAlignment.Leading;

                // hero: tracking count (records open toward EOD)
                _sp.Text("TRACKING", r.Left, r.Top + 24f, 90f, 12f, SneakySkin.CMute, 9f, true);
                _sp.Text(openCount.ToString(CultureInfo.InvariantCulture), r.Left, r.Top + 32f, 90f, 28f, SneakySkin.CAccent, 24f);

                // regime + ADX block
                var regCol = _curRegime == "trend" ? SneakySkin.CAccent
                           : _curRegime == "chop"  ? SneakySkin.CMute
                           : _curRegime == "mid"   ? SneakySkin.CInk2 : SneakySkin.CMute;
                _sp.Text("REGIME", r.Left + 96f, r.Top + 24f, 120f, 12f, SneakySkin.CMute, 9f, true);
                _sp.Text(_curRegime, r.Left + 96f, r.Top + 33f, 120f, 20f, regCol, 15f, true);
                _sp.Text("ADX " + _curAdx.ToString("0.0", CultureInfo.InvariantCulture),
                    r.Left + 96f, r.Top + 53f, 120f, 12f, SneakySkin.CMute, 9.5f, false, lead, true);

                // ADX strength track (0..40 → 0..1); cyan in trend, faint otherwise
                float frac = (float)Math.Max(0, Math.Min(1, _curAdx / 40.0));
                _sp.Track(r.Left, r.Top + 68f, r.Width, frac, _curRegime == "trend" ? SneakySkin.CAccent : SneakySkin.CFaint, 5f);

                // session tally — council fires
                // Externals only appear once something has actually published one, so the card stays
                // identical on a Council-only chart rather than growing a permanent "0".
                _sp.Text("COUNCIL fires  " + _nCouncil + (_nExternal > 0 ? "   EXT  " + _nExternal : ""),
                    r.Left, r.Top + 78f, r.Width, 14f, SneakySkin.CInk2, 10.5f, false, lead, true);

                // latest record: signal · dir · running MFE/MAE
                if (openCount > 0 && _lastSig != null)
                {
                    Rec last = _open[_open.Count - 1];
                    var dirCol = _lastDir > 0 ? SneakySkin.CUp : SneakySkin.CDown;
                    _sp.Text(_lastSig + (_lastDir > 0 ? " ▲" : " ▼"),
                        r.Left, r.Top + 94f, 62f, 16f, dirCol, 10.5f, true, lead, true);
                    _sp.Text("MFE", r.Left + 66f, r.Top + 96f, 26f, 14f, SneakySkin.CMute, 9f, true);
                    _sp.Text(last.MaxMFE.ToString("0", CultureInfo.InvariantCulture) + "t",
                        r.Left + 92f, r.Top + 94f, 40f, 16f, SneakySkin.CUp, 11f, false, lead, true);
                    _sp.Text("MAE", r.Left + 136f, r.Top + 96f, 26f, 14f, SneakySkin.CMute, 9f, true);
                    _sp.Text(last.MaxMAE.ToString("0", CultureInfo.InvariantCulture) + "t",
                        r.Left + 162f, r.Top + 94f, 40f, 16f, SneakySkin.CDown, 11f, false, lead, true);
                }
                else
                {
                    _sp.Text("— no open records", r.Left, r.Top + 94f, r.Width, 16f, SneakySkin.CMute, 10f, false, lead, true);
                }

                // footer: recorded count · instrument · bartag
                _sp.Text("rec " + _recorded + "   " + InstName() + " · " + SneakyCore.FriendlyBartag(BarTag()),
                    r.Left, r.Top + 110f, r.Width, 12f, SneakySkin.CMute, 9f, false, lead, true);
        }

        private string InstName()
        {
            try { return Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : "unknown"; }
            catch { return "unknown"; }
        }

        // THE SCOPE IS THE DATA PATH. The filename tag and the Council's seam key must be the SAME string,
        // or an excursion row cannot be joined to the verdict that produced it. Both come from SneakyCore.
        private string _scope;
        // v2.3.0 — the BARE (un-laned) scope. BAR-TYPE seams (BrickState/FluxState/ConvictionState) are published
        // by the bars series, which is SHARED by every chart on the same instrument+bartype+size, so they are
        // keyed BARE. Reading them with the laned Scope() above silently returns null on a laned chart — the
        // Council already learned this and uses its own BareScope() for exactly these seams.
        private string BareScope()
        {
            try { return NinjaTrader.NinjaScript.AddOns.Sneaky.SneakyCore.ScopeOf(Instrument, BarsPeriod); }
            catch { return null; }
        }

        private string Scope()
        {
            if (_scope == null)
            {
                // v2.1.3 — LANED scope (Core ≥ v1.32.0): inherit this chart's lane via ChartControl so the recorder
                // reads the LANED CouncilState (the Council publishes @lane, not bare) and files the corpus under the
                // laned scope → two same-bartype test lanes record into SEPARATE corpora. Bare when no lane (back-compat).
                try { _scope = NinjaTrader.NinjaScript.AddOns.Sneaky.SneakyCore.ScopeOf(Instrument, BarsPeriod, ChartControl); }
                catch (Exception _sx) { SneakyCore.Swallow("SneakyExcursionRec.Scope", _sx); }
            }
            return _scope;
        }

        // Delegates to SneakyCore.BarTag (numeric (int)BarsPeriodType id + BarsPeriod.Value2 — a CUSTOM bar
        // type's NAME resolves inconsistently by load state, which once made two recorders write two tags).
        // v2.1.3 — the LANED bartag: bare bartag + "@lane" when this chart has a lane (Core registry, keyed by
        // ChartControl). Drives BOTH the corpus filename and the row's "bartype" field, so two same-bartype test
        // lanes (A/B) land in SEPARATE files AND the Lab groups them apart (else it would merge them under the bare
        // tag and the whole point of a lane — grading A vs B on identical bars — would be lost). Bare when no lane.
        //
        // v2.7.0 — STICKY lane, not a one-time cache. LaneOf(ChartControl) is still read fresh every call (never
        // permanently locked in on the first call — Scope()'s own 2026-08-04 comment already documents why: a
        // lane can register minutes after this indicator's first call on a slow reload, and freezing an empty
        // read then would be wrong for the rest of the session). But _lastLane only ever UPDATES on a non-empty
        // read, so a LATE empty read — specifically State.Terminated's FlushAll("cutoff"), which can run after
        // Council's own Terminated handler has already called SneakyCore.ClearLane on the same ChartControl,
        // indicator teardown order across a chart being unguaranteed — falls back to the last good value instead
        // of silently stripping the lane off whatever row is still open at that instant.
        private string _lastLane = "";
        private string BarTag()
        {
            try
            {
                string t = NinjaTrader.NinjaScript.AddOns.Sneaky.SneakyCore.BarTag(BarsPeriod);
                string ln = "";
                try { ln = NinjaTrader.NinjaScript.AddOns.Sneaky.SneakyCore.LaneOf(ChartControl) ?? ""; }
                catch (Exception _lx) { SneakyCore.Swallow("SneakyExcursionRec.BarTag.LaneOf", _lx); }
                if (!string.IsNullOrEmpty(ln)) _lastLane = ln;
                return string.IsNullOrEmpty(_lastLane) ? t : t + "@" + _lastLane;
            }
            catch { return "unknown"; }
        }

        // opt-in housekeeping — on load, delete Excursion .jsonl files older than ExcursionRetentionDays.
        // Best-effort and self-contained: never throws into OnStateChange, only touches *.jsonl in the Excursions dir.
        private void PruneOldExcursions(string dir)
        {
            try
            {
                DateTime cutoff = DateTime.Now.AddDays(-ExcursionRetentionDays);
                foreach (string p in Directory.GetFiles(dir, "*.jsonl"))
                {
                    try { if (File.GetLastWriteTime(p) < cutoff) File.Delete(p); }
                    catch { /* file locked / in use — skip it */ }
                }
            }
            catch { /* dir enumeration failed — housekeeping is best-effort */ }
        }

        private static string F(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return Math.Round(v, 4).ToString(CultureInfo.InvariantCulture);
        }
        private static string Iso(DateTime dt) { return dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }
        private static string Q(string s)
        {
            if (s == null) return "null";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        // schema 1.3 vote-vector serializers (ML spec §2.1). A null dict ⇒ JSON null (a non-council record); the Lab
        // treats an ABSENT tag as abstain, never a zero vote — so a voter that did not read is simply not a key here.
        private static string JVotes(Dictionary<string,int> d)
        {
            if (d == null) return "null";
            var sb = new StringBuilder(64).Append('{'); bool first = true;
            foreach (var kv in d) { if (!first) sb.Append(','); first = false;
                sb.Append(Q(kv.Key)).Append(':').Append(kv.Value.ToString(CultureInfo.InvariantCulture)); }
            return sb.Append('}').ToString();
        }
        private static string JVoteW(Dictionary<string,double> d)
        {
            if (d == null) return "null";
            var sb = new StringBuilder(64).Append('{'); bool first = true;
            foreach (var kv in d) { if (!first) sb.Append(','); first = false;
                sb.Append(Q(kv.Key)).Append(':').Append(F(kv.Value)); }
            return sb.Append('}').ToString();
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Record Council fires", Description = "Record the consulted SneakyCore.CouncilState verdict as a \"COUNCIL\" signal (edge-detected bias flip; conviction-bucketed). The Council must be on this chart. Needs SentinelCore ≥ v1.7.0.", GroupName = "Recorder", Order = 1)]
        public bool RecordCouncil { get; set; }

        // NOT a [NinjaScriptProperty] on purpose — serializes to the workspace + shows in F6 but stays OUT of the
        // generated constructor (no codegen churn / no region regen). Default TRUE = record the full conviction range.
        [Display(Name = "Record below floor (full range)", Description = "ON (default): record EVERY aligned, non-hard-vetoed Council verdict regardless of the conviction floor, so the Lab can FIT the floor from data that spans below it. OFF: record only floor-clearing (HasEdge) verdicts — what the Bridge would actually trade.", GroupName = "Recorder", Order = 2)]
        public bool RecordBelowFloor { get; set; }

        // NOT a [NinjaScriptProperty] — same reason as the one above, and here it also matters that adding a
        // constructor parameter would change the generated region and drop this indicator off every chart that
        // already carries it. Default TRUE: a fire only ever arrives because a strategy deliberately called
        // SneakyCore.NoteSignalFire, so defaulting OFF would mean a strategy author instruments their code,
        // sees nothing recorded, and has no way to tell that from "the intake is broken".
        [Display(Name = "Record external fires", Description = "ON (default): record decisions published by any tool via SneakyCore.NoteSignalFire (strategies, the Bridge, Keel) as their own signal tag, alongside COUNCIL. This is what makes a strategy visible to the excursion corpus at all. Realtime only. Needs SentinelCore ≥ v1.46.0.", GroupName = "Recorder", Order = 3)]
        public bool RecordExternalFires { get; set; }

        // NOT a [NinjaScriptProperty] — same generated-region reasoning as the others here.
        [Display(Name = "Pre-fire lookback (ms)", Description = "How far BACK before a fire the tick sidecar reaches, via a continuously-running rolling buffer (independent of TickPathMaxMs/TickPathTailMs, which only gate the path AFTER a fire). Default 7200000 (2 hours). WHY THIS EXISTS: every sidecar recorded before 2026-08-22 starts exactly at the fire and has zero lookback, which is fine for excursion/MFE-MAE study but means an indicator needing real warmup (ADX needs ~28 bars, WAE's dead zone defaults to 100) reads NaN/neutral for the entire fire when replayed standalone -- there was nothing recorded before it to warm up from. Raising this gives future fires more true pre-fire history at the cost of a bigger per-fire sidecar and a buffer held continuously in memory (bounded separately by PreFireBufferMaxTicks regardless of this setting). Does not affect any sidecar already on disk.", GroupName = "Recorder", Order = 10)]
        public long PreFireBufferMs { get; set; }

        // NOT a [NinjaScriptProperty] — same generated-region reasoning as the others here.
        [Display(Name = "Tick path max (ms)", Description = "Hard cap on how long a fire's raw-tick buffer is held before it is flushed, for a fire that never resolves to a barrier. Default 300000 (5 min). RAISE FOR A BAKE when trades routinely run longer (Tide 25 NQ does) — otherwise the tick path is truncated exactly where an exit study needs it. Costs memory on a fast-firing chart, which is why it is not a higher default.", GroupName = "Recorder", Order = 7)]
        public long TickPathMaxMs { get; set; }

        // NOT a [NinjaScriptProperty] — same generated-region reasoning as the others here.
        [Display(Name = "Tick path tail (ms)", Description = "How much raw-tick path is kept AFTER a fire resolves at its barrier. Default 30000 (30s) = the immediate post-R behaviour only. ⚠ THIS IS THE KNOB THAT GATES THE D2 (KNOCKOUT) STUDY, not Tick path max: measured 2026-07-31, 0 of 80 knockouts had a tick path reaching their own MFE bar, because the path ends 30s after the stop is touched — and D2 lives entirely in what happens AFTER that. Set ~3600000 (60 min, matching the row's MFE/MAE window) for a re-entry study bake. Costs memory and much larger sidecars, which is why the default is low.", GroupName = "Recorder", Order = 8)]
        public long TickPathTailMs { get; set; }

        // NOT a [NinjaScriptProperty] — same generated-region reasoning as the others here.
        [Display(Name = "Entry backfill window (ms)", Description = "How soon after a fire the next real trade tick may still be adopted as the honest entry price (pxSrc=\"firsttick\") instead of falling back to the last-known trade beforehand (pxSrc=\"last\"). Default 100ms. ⚠ Was a hardcoded exact-0ms match until 2026-08-15 — verify_firepx.py's own non-circular check found this essentially never fired on ANY instrument (bfill%=0% everywhere) because a real tick almost never lands in the exact same millisecond as the fire, leaving NQ specifically with a real ~2-tick, mildly adverse-skewed entry-price offset that MNQ's naturally-fresher tape didn't show. 100ms is roughly 10-20x smaller than the \"seconds\" of forward drift this guard exists to prevent — raise cautiously if real fires still show a stale-entry pattern after a bake at 100ms, but each step further away from 0 trades a little more look-ahead risk for a little more backfill coverage.", GroupName = "Recorder", Order = 9)]
        public long EntryBackfillWindowMs { get; set; }

        // NOT a [NinjaScriptProperty] — serializes to the workspace + shows in F6, stays OUT of the generated
        // constructor (no codegen churn). Default TRUE = capture the raw-tick path of every fire (ML Phase 3).
        [Display(Name = "Record tick path", Description = "ON (default): capture the raw last-trade tick PATH of every Council fire to Sneaky\\Excursions\\council\\ticks\\<fireId>.jsonl (joined to the row by episodeId/fireTime) — the tick-true path + a tick-resolution first-touch label, for grading conviction vs PATH quality. Realtime/replay only.", GroupName = "Recorder", Order = 6)]
        public bool RecordTickPath { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Council stale (sec)", Description = "Ignore a Council verdict older than this many seconds (fail-open if the Council is absent/stale).", GroupName = "Recorder", Order = 2)]
        public double CouncilStaleSec { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show info card", Description = "Draw the Sentinel glass-card readout (off = pure headless recorder).", GroupName = "Recorder", Order = 3)]
        public bool ShowInfo { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Card corner", Description = "Which chart corner the card docks to. Cards in the same corner auto-stack (never overlap).", GroupName = "Recorder", Order = 4)]
        public SneakyCardCorner CardCorner { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show indicator label", Description = "Show NinjaTrader's chart name label. Sneaky default = OFF (clean chart); turn on to restore it.", GroupName = "Sneaky", Order = 100)]
        public bool ShowIndicatorLabel { get; set; }

        // NOT a [NinjaScriptProperty] on purpose — serializes to the workspace but stays OUT of the generated
        // constructor (no codegen churn). 0 = keep everything (default); >0 = on load, prune Excursion .jsonl older than N days.
        [Display(Name = "Excursion retention (days)", Description = "0 = keep every Excursion .jsonl (default). >0 = on load, delete files in Sneaky\\Excursions older than this many days. Housekeeping only — never deletes today's file.", GroupName = "Recorder", Order = 5)]
        [Range(0, int.MaxValue)]
        public int ExcursionRetentionDays { get; set; }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Sneaky.SneakyExcursionRecorder_v2_0_0[] cacheSneakyExcursionRecorder_v2_0_0;
		public Sneaky.SneakyExcursionRecorder_v2_0_0 SneakyExcursionRecorder_v2_0_0(bool recordCouncil, double councilStaleSec, bool showInfo, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyExcursionRecorder_v2_0_0(Input, recordCouncil, councilStaleSec, showInfo, cardCorner, showIndicatorLabel);
		}

		public Sneaky.SneakyExcursionRecorder_v2_0_0 SneakyExcursionRecorder_v2_0_0(ISeries<double> input, bool recordCouncil, double councilStaleSec, bool showInfo, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyExcursionRecorder_v2_0_0 != null)
				for (int idx = 0; idx < cacheSneakyExcursionRecorder_v2_0_0.Length; idx++)
					if (cacheSneakyExcursionRecorder_v2_0_0[idx] != null && cacheSneakyExcursionRecorder_v2_0_0[idx].RecordCouncil == recordCouncil && cacheSneakyExcursionRecorder_v2_0_0[idx].CouncilStaleSec == councilStaleSec && cacheSneakyExcursionRecorder_v2_0_0[idx].ShowInfo == showInfo && cacheSneakyExcursionRecorder_v2_0_0[idx].CardCorner == cardCorner && cacheSneakyExcursionRecorder_v2_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyExcursionRecorder_v2_0_0[idx].EqualsInput(input))
						return cacheSneakyExcursionRecorder_v2_0_0[idx];
			return CacheIndicator<Sneaky.SneakyExcursionRecorder_v2_0_0>(new Sneaky.SneakyExcursionRecorder_v2_0_0(){ RecordCouncil = recordCouncil, CouncilStaleSec = councilStaleSec, ShowInfo = showInfo, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyExcursionRecorder_v2_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.SneakyExcursionRecorder_v2_0_0 SneakyExcursionRecorder_v2_0_0(bool recordCouncil, double councilStaleSec, bool showInfo, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyExcursionRecorder_v2_0_0(Input, recordCouncil, councilStaleSec, showInfo, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.SneakyExcursionRecorder_v2_0_0 SneakyExcursionRecorder_v2_0_0(ISeries<double> input , bool recordCouncil, double councilStaleSec, bool showInfo, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyExcursionRecorder_v2_0_0(input, recordCouncil, councilStaleSec, showInfo, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.SneakyExcursionRecorder_v2_0_0 SneakyExcursionRecorder_v2_0_0(bool recordCouncil, double councilStaleSec, bool showInfo, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyExcursionRecorder_v2_0_0(Input, recordCouncil, councilStaleSec, showInfo, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.SneakyExcursionRecorder_v2_0_0 SneakyExcursionRecorder_v2_0_0(ISeries<double> input , bool recordCouncil, double councilStaleSec, bool showInfo, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyExcursionRecorder_v2_0_0(input, recordCouncil, councilStaleSec, showInfo, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
