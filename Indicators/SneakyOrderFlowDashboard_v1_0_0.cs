// ─────────────────────────────────────────────────────────────────────────────
// SneakyOrderFlowDashboard_v1_0_0.cs
// ─────────────────────────────────────────────────────────────────────────────
//  Standalone NinjaTrader 8 indicator. No third-party dependencies, no other AddOns/services
//  required — this file compiles and runs entirely on its own.
//
//  WHAT THIS IS
//  ------------
//  A CONSOLIDATION of the five separate Sneaky Suite dashboard modules built earlier
//  ([[SneakyMarketContext]], [[SneakyConfluenceZones]], [[SneakyLiquidityZones]],
//  [[SneakyAggressionSplit]], [[SneakyFlowEvents]]) into ONE indicator with ONE floating panel,
//  sectioned internally — matching the original reference screenshot's layout (one compact
//  "BOOKMAP ORDER FLOW"-style sidebar, not five separate stacked boxes covering half the chart).
//  All the math/logic is UNCHANGED from the five standalone files (same formulas, same thresholds,
//  same live-data caveats) — this file only changes how it's PACKAGED and DISPLAYED. The five
//  standalone files still exist and still work if run individually; this is the "run all five at
//  once, compactly" option.
//
//  WHAT'S NEW IN THIS FILE (vs. the five standalone modules)
//  --------------------------------------------------------------
//    1. ONE panel instead of five, wrapped in a scrollable area (title bar always visible/draggable,
//       the sections below it scroll if they don't fit — same technique already proven elsewhere in
//       the Sneaky Suite project: capping a ScrollViewer's MaxHeight to the actual chart height via
//       a SizeChanged handler) — so it behaves like a single sidebar, not five things to reposition.
//    2. Confluence Zones' top zones AND Liquidity Zones' top walls/clusters are now ALSO drawn as
//       horizontal lines across the FULL chart width, each with a text label anchored at the current
//       bar (right side of the chart) showing the price and what it is — e.g. "29571.40 LOW(2)" for
//       a zone, "BID 29520.00 x120" for a wall. The standalone modules only drew the lines
//       themselves (Confluence Zones) or nothing (Liquidity Zones) — neither had a label, which is
//       what actually shows what a line means at a glance. Both Draw.HorizontalLine's and
//       Draw.Text's exact overload lists were verified against NinjaTrader's own help-guide pages
//       before use (continuing this project's established discipline).
//    3. Aggression Split and Flow Events now share ONE trade classification (quote rule + tick-rule
//       fallback, Lee & Ready 1991) computed once per trade print instead of being duplicated across
//       two separate files — a genuine simplification that merging made possible, not just a
//       repackaging.
//
//  EVERYTHING BELOW IS UNCHANGED FROM THE FIVE STANDALONE FILES — see each one's own header comment
//  (still present in each of those files) for the full research/reasoning behind every formula,
//  threshold, and live-data limitation. Summarized here for convenience:
//
//  ⚠ LIVE-ONLY SECTIONS: Liquidity/Walls, Aggression Split, and Flow Events all read Level 2 depth
//  and/or the trade tape (OnMarketDepth / OnMarketData), neither of which NinjaTrader delivers on
//  historical bars without Tick Replay + real tick data (not assumed available, per this project's
//  known prop-firm-feed tick-data limitation). Those three sections say "waiting..." honestly until
//  real live data arrives; Market Context and Confluence Zones work from ordinary bar data and
//  populate immediately, live or historical.
//
//  WHY AN INDICATOR, NOT A STRATEGY
//  -----------------------------------
//  Read-only display — no orders, no position management, same as all five standalone modules.
//
//  A NOTE ON THREADING
//  ---------------------
//  OnMarketDepth and OnMarketData both run on their own real-time thread(s), separate from
//  OnBarUpdate — all shared mutable state across every section lives behind ONE lock (dataLock).
//  Exactly like the standalone Liquidity Zones / Flow Events files, actual Draw.* chart-object calls
//  are kept OUT of OnMarketDepth/OnMarketData entirely and only ever issued from OnBarUpdate, reading
//  a lock-protected snapshot of whatever the depth/tape threads most recently computed.
//
//  ⚠ FIRST CUT, NOT YET VISUALLY TUNED. Same caveat as every module folded into this file — periods,
//  thresholds, and window lengths are first-cut placeholders. Watch it live for a while before
//  trusting any of it, and expect to want to resize/reposition the panel and its line labels.
//
//  Changelog
//    v1.0.0 (2026-09-08) — first cut. Combines all five Sneaky Suite dashboard modules into one
//                          sectioned, scrollable panel; adds text labels to Confluence Zones' and
//                          (new) Liquidity Zones' chart lines.
//    v1.0.0 patch (2026-09-08) — (1) each individual Confluence Zones level (PS High/Low/Close/
//                          VWAP/POC/VAH/VAL, running Session VWAP, OR High/Low) now gets its own
//                          labeled chart line via DrawConfluenceLevelLines(), not just the clustered
//                          "zone" lines. (2) ALL chart-line labels (Confluence levels, Confluence
//                          zones, Liquidity walls/clusters) are now drawn via a new OnRender()
//                          override that pins each label near the right edge of the chart PANEL in
//                          screen/pixel space (chartScale.GetYByValue for the Y, ChartPanel.X/W for
//                          the X) instead of Draw.Text anchored to a bar index — the old approach
//                          made every label scroll off-screen the instant the chart was panned away
//                          from the current bar. Horizontal lines are unchanged (Draw.HorizontalLine
//                          already spans the full visible width regardless of scroll).
//    v1.0.0 patch 2 (2026-09-08) — NEW section 3b "Stat Walls": absorption-inferred wall levels that
//                          need NO Level 2 market depth at all -- a concept ported from the TradingIQ
//                          "Liquidity Walls" Pine v6 study (see the separate SneakyLiquidityWalls_v1_0_0
//                          file). Adds a 1-tick data series (BarsInProgress==2), classifies each tick
//                          buy/sell via the tick rule, accumulates per-bar order-flow delta, and runs a
//                          rolling OLS regression of price-move-vs-delta; when a bar's actual move falls
//                          far short of what the regression predicts (z-score >= threshold), infers an
//                          ATR-thick wall at that price. This exists specifically because the real
//                          Liquidity/Walls section (3) needs Level 2 depth the user's feed (Lucid) does
//                          not provide -- Stat Walls only needs trade prints, which do flow on that feed.
//                          Drawn as filled rectangles (Draw.Rectangle) + pinned labels (reusing the
//                          OnRender label system above) with a new "Show Stat Wall chart boxes" toggle.
//    v1.0.0 patch 3 (2026-09-08) — Section 3's BID/ASK and IMBALANCE lines no longer sit stuck on
//                          "—" forever on a no-L2 feed: BID/ASK now falls back to the plain Level 1
//                          quote (already flowing into Flow Events) when no L2 top-of-book has
//                          arrived, tagged "(L1)"; IMBALANCE falls back to the rolling aggressor
//                          buy/sell split already computed for Aggression Split, tagged "(flow, no
//                          L2)". Real resting-size WALLS/CLUSTERS in section 3 still cannot be shown
//                          without genuine L2 depth -- no tape-only proxy can reconstruct resting
//                          order size -- Stat Walls (3b) remains the intended substitute for those.
//    v1.0.0 patch 4 (2026-09-08) — NEW section 3c "CVD Regression": another absorption/order-flow
//                          technique that needs no Level 2 depth, this time built from CUMULATIVE
//                          VOLUME DELTA rather than per-bar delta. Reuses the SAME Lee & Ready trade
//                          classification already computed once per print for Aggression Split/Flow
//                          Events (no second classifier) to accumulate a session-running CVD, fits a
//                          rolling OLS regression of CVD against bar sequence over a lookback window,
//                          and publishes slope + R² (a fit-quality gate -- a low-R² slope is noise,
//                          not a real read). Flags a DIVERGENCE when price's own direction over that
//                          same window disagrees with the CVD slope's direction (gated on R² clearing
//                          a minimum). Concept requested from the user-shared SneakyFlow_v1_0_0 file,
//                          but reimplemented self-contained here: that file's classification comes
//                          from an external "PressureState" seam published by a separate, unshared
//                          companion indicator (SneakyBuySellVolumePressureMountain); this dashboard
//                          has its own trade classification already flowing for Aggression Split, so
//                          there is no dependency on any file not already in this dashboard. Divergence
//                          fires a one-off triangle marker at the bar it occurred on (same pattern as
//                          the PressureMountain file's own divergence markers) -- unlike the Confluence/
//                          Liquidity/Stat-Wall labels, this is a point-in-time event, not a persisting
//                          current level, so it doesn't need the OnRender screen-pinning treatment.
//    v1.0.0 patch 5 (2026-09-09) — COMPILE FIX: removed the file's only remaining dependency on
//                          NinjaTrader's stock VWAP indicator (Market Context's `VWAP()` call, used
//                          for the vwapBull vote). Reported by another user in the community this file
//                          was shared with: "SneakyOrderFlowDashboard requires VWAP by name to compile
//                          ... I have several VWAPs, but none of them are by that name" -- their
//                          NinjaTrader install has no indicator resolvable as exactly "VWAP" (custom
//                          VWAP variants installed under other names, stock one missing/renamed/not
//                          present), so the auto-generated `VWAP()` factory-method call failed to
//                          resolve at compile time. Fix removes the dependency entirely rather than
//                          asking every user to have a specific stock indicator present: Market
//                          Context's vwapBull check now reads the dashboard's OWN already-computed
//                          running session VWAP (czSessionVwapPriceVolSum/czSessionVwapVolSum -- the
//                          exact same number Confluence Zones already builds by hand every bar for its
//                          own "SESSION VWAP" line/level) instead of instantiating a second, separate
//                          VWAP object. Removed the now-unused `mcSessionVwap` field and its
//                          `VWAP()` assignment in State.DataLoaded. No behavior change -- the value
//                          read is identical, just no longer re-derived through a second indicator
//                          instance -- and this makes the file's own "no third-party dependencies,
//                          compiles and runs entirely on its own" claim (see WHAT THIS IS above)
//                          literally true of every built-in indicator call it makes, not just VWAP.
//    v1.0.0 patch 6 (2026-09-10) — PORTED THE REST OF THE REFERENCE DASHBOARD'S FEATURE SET (the
//                          "BOOKMAP ORDER FLOW" screenshot this file was originally modeled on).
//                          Six additions, all bar-based except Stack/Pull:
//                            (a) MARKET STATE + EFF/FLIP/POC(t)/BAL, folded into section 1 (Market
//                                Context): a Choppiness-Index-vs-ADX composite label (TRENDING/
//                                CHOPPY/TRANSITIONAL), Kaufman's Efficiency Ratio, TODAY's still-
//                                forming POC/VAH/VAL (reuses Confluence Zones' own
//                                ComputeProfileLevels() against the live czProfileVolume -- no new
//                                profile logic), a POC(t) crossing/"flip" counter, and a balanced-
//                                vs-imbalanced-value-area state with a bar counter.
//                            (b) SEQUENCE / EXHAUSTION (new section 3d): counts consecutive higher-
//                                high/higher-low (or lower-low/lower-high) bars as an impulse
//                                "sequence"; flags EXHAUSTION when the sequence is long and the
//                                current bar's range has contracted sharply; labels whether the CVD
//                                Regression slope (3c) CONFIRMS or DIVERGES from the sequence's
//                                direction (LEAD-LAG) -- a live-impulse-leg read, distinct from 3c's
//                                own whole-window price-vs-CVD divergence marker.
//                            (c) LIQUIDITY REACTION/HOLD (new section, tallied inside Stat Walls'
//                                existing UpdateStatWalls): every bar a Stat Wall is tested now
//                                counts as either a HOLD (price enters the wall's zone but closes
//                                back out -- a defended test) or a BREAK (already-existing removal
//                                logic) -- session-cumulative HOLDS/BREAKS + hold%.
//                            (d) FAILED AUCTION / TRAPPED TRADERS (new section 3f): watches Opening
//                                Range High/Low and Previous-Session High/Low for a breakout, then
//                                whether it holds -- a close back inside the level within
//                                FaFailBars bars is a FAILED AUCTION; a harder reversal back through
//                                the level by FaTrappedTicks within FaTrappedBars more bars is
//                                TRAPPED TRADERS (breakout traders' stops likely feeding the move).
//                                Chart markers ("FAILED"/"TRAPPED") + session counts.
//                            (e) STACK / PULL (new section 3g) -- the ONE addition in this patch that
//                                genuinely needs real Level 2 depth (unlike every other "(no L2)"
//                                section in this file): tracks a rolling add-vs-cancel size delta at
//                                the best bid/ask (position 0) from OnMarketDepth, labels each side
//                                STACKING/PULLING/neutral. Honestly shows "NO DEPTH" when
//                                lzHasReceivedDepth is false, same gate as section 3's own real
//                                walls/clusters.
//                            (f) VISUAL POLISH: a two-color (buy/sell) proportional bar under
//                                Aggression Split's rolling split row (Star-sized Grid columns, no
//                                manual pixel math); slider-style gauge markers next to Trend/
//                                Momentum/Confluence in Market Context; a small Polyline sparkline of
//                                recent CVD history under the CVD Regression section. New WPF shape
//                                types (Rectangle/Ellipse/Polyline, from System.Windows.Shapes) are
//                                fully qualified inline everywhere rather than adding a blanket
//                                `using System.Windows.Shapes;`, which would collide with
//                                NinjaTrader.NinjaScript.DrawingTools' own "Rectangle" drawing-tool
//                                class already imported for Draw.Rectangle -- same discipline this
//                                file already uses for SharpDX types in OnRender.
//                          All new sections follow this file's established conventions: manual
//                          rolling buffers instead of stock indicator-by-name calls (see patch 5),
//                          dataLock-guarded bar-logic methods, one-off Draw.Text/marker events for
//                          point-in-time signals vs. the OnRender screen-pinning system for
//                          persisting levels, and honest "—"/"disabled"/"NO DEPTH" states instead of
//                          ever fabricating a read the current feed can't support. Not yet watched
//                          live by the user -- first cut, like every other addition to this file.
//    v1.0.0 patch 7 (2026-09-10) — TUNING: default "Trapped-reversal buffer (ticks)" (FaTrappedTicks)
//                          raised from 4 to 12. Watching this live (patch 6) showed FAILED AUCTION
//                          and TRAPPED TRADERS locked at the same count every time (7/7, then 11/11)
//                          -- on a 2-minute chart's real bar ranges, 4 ticks (1 point on NQ) was
//                          cleared by nearly every failed-auction reversal, so TRAPPED TRADERS was
//                          just echoing FAILED AUCTION instead of being the rarer, harder-confirming
//                          signal it's meant to be. 12 ticks (3 points on NQ) gives real separation.
//                          Existing chart instances keep whatever value they were saved with --
//                          NinjaTrader serializes property values per instance, so this default only
//                          applies to a freshly-added copy of the indicator. To pick up the new value
//                          on an already-running instance, open its Properties and change "Trapped-
//                          reversal buffer (ticks)" under "3f Failed Auction / Trapped (no L2)" to 12
//                          (or whatever you'd rather use) by hand.
//    v1.0.0 patch 8 (2026-09-10) — Two fixes from watching a real-L2 run on MNQ (Micro Nasdaq):
//                          WALLS/CLUSTERS were coming up empty and BID/ASK was blank even with
//                          "LIVE DEPTH: active". (1) NEW property "Auto-scale by instrument"
//                          (LzAutoScaleByInstrument, default ON, under "3 Liquidity Zones") --
//                          the wall/cluster size thresholds (50/75) were calibrated for full-size
//                          NQ-scale resting liquidity, which MNQ's much thinner book rarely clears.
//                          When on, State.DataLoaded now auto-picks 12/20 instead of 50/75 for known
//                          CME micro roots (MNQ, MES, MYM, M2K, MCL, MGC) and leaves full-size
//                          instruments at 50/75 -- this OVERRIDES a saved instance's values on
//                          every load by design (that's what "auto" means here); set it to false
//                          in Properties to pin your own numbers instead. (2) The BID/ASK line no
//                          longer blanks both sides just because one side has no known price (e.g.
//                          a thin/one-sided micro book, consistent with that run's own IMBALANCE
//                          0% ASK-HEAVY reading) -- each side now independently shows its own L2 or
//                          L1 price, or "—" only for the side that's genuinely unknown; SPREAD
//                          still requires both sides.
//    v1.0.0 patch 9 (2026-09-10) — NEW property "Gate LEAD-LAG by CVD R²" (SqGateLeadLagByR2,
//                          default ON, under "3d Sequence / Exhaustion (no L2)"). SEQUENCE's CVD
//                          CONFIRMS/DIVERGES read previously compared its direction against the
//                          CVD Regression slope's sign with no quality check at all, while 3c's own
//                          DIVERGENCE marker requires R² >= CfMinR2 (0.30) before it'll fire -- the
//                          two could legitimately disagree on a noisy/low-R² slope. When on, LEAD-
//                          LAG now also requires cfLastR2 >= CfMinR2 (the same threshold, reused --
//                          no new number to configure) before showing CONFIRMS/DIVERGES; below it,
//                          it reads "CVD —" same as when CVD Regression is disabled. Turn off to
//                          restore the old always-on read. Unlike patch 7's FaTrappedTicks change,
//                          this is a brand-new property with nothing saved for it on an existing
//                          chart instance, so it picks up the ON default immediately -- no manual
//                          Properties edit needed.
//    v1.0.0 patch 10 (2026-09-10) — DIAGNOSTIC: user reported FAILED AUCTION / TRAPPED TRADERS
//                          reading "—" all session on a 1-minute chart despite hours of price action
//                          straddling OR HIGH/LOW and PS HIGH/LOW (visible in their own panel: Stat
//                          Walls holding 470-500+ bars either side of a range that straddles OR
//                          HIGH). Read through CheckBreakoutLevel/RunFailedAuctionBarLogicInner line
//                          by line -- found no logic bug, and nothing in patches 7-9 touched this
//                          section at all (Liquidity Zones, BID/ASK, and Sequence's R² gate are
//                          unrelated code paths) -- so "—" here reads as a genuine zero-events state,
//                          same convention as every other "no data yet" read in this file, not a
//                          removed feature. But there was no way to tell FROM THE PANEL ALONE
//                          whether that's because breakout watches never arm (would point at a real
//                          bug) or they arm fine but keep taking longer than FaFailBars/FaTrappedBars
//                          bars to resolve either way (a tuning question -- FaFailBars=5 bars is 5
//                          real minutes on a 1-minute chart vs. 10 on a 2-minute chart, so the same
//                          setting is a much tighter wall-clock window on faster timeframes). Added
//                          two new session-cumulative diagnostic counters -- faArmedCount (every
//                          fresh breakout watch armed) and faExpiredCount (every watch that held
//                          past the window without failing) -- surfaced as "(armed n / expired n)"
//                          next to the FAILED AUCTION line. If armed stays 0 all session, the arming
//                          logic itself needs another look; if armed grows while failed stays low,
//                          FaFailBars is likely just too tight for this timeframe/instrument, not a
//                          bug. Diagnostic only -- no behavior change to when FAILED AUCTION/TRAPPED
//                          TRADERS actually fire. Not yet confirmed against the user's live panel.
//    v1.0.0 patch 11 (2026-09-15) — NEW section 3h "Footprint Grid": classic per-price bid×ask
//                          footprint cells drawn directly on each bar, the one piece of the "free
//                          order flow, no Level 2" community-tool category (e.g. the FlowPrint NT8
//                          indicator, which reads every tick and classifies it bid/ask with no Tick
//                          Replay needed) this dashboard didn't already cover -- a per-price volume
//                          ladder on each bar, not just a single delta/imbalance number. Needs NO
//                          Level 2 depth: reuses the SAME Lee & Ready trade classification already
//                          computed once per print in OnMarketData (shared with Aggression Split/
//                          Flow Events/CVD Regression), just bucketed by (bar, tick-rounded price)
//                          instead of summed into one running total. Rendering uses plain per-row
//                          Draw.Text calls issued exactly once per finalized bar (tagged uniquely per
//                          bar+price row) rather than the OnRender screen-pinning system above --
//                          each row is meant to stay anchored to its OWN specific historical bar (not
//                          re-anchored to "the current bar" every tick the way the old, buggy zone/
//                          wall labels once were, see the v1.0.0 patch entry above), which is exactly
//                          what a plain bar-anchored Draw.Text already does on its own once issued --
//                          this project's existing one-off EXH/FAILED/TRAPPED markers already rely on
//                          the same behavior. Each row is colored by the classic "diagonal imbalance"
//                          test (this price's ask volume vs. the bid volume one tick below, and vice
//                          versa) once it clears "Imbalance ratio". Rows below "Min row volume" are
//                          skipped to cut single-lot dust. Only the most recent "Max bars shown" bars
//                          keep their footprint on screen -- older ones are cleaned up via
//                          RemoveDrawObject as new bars finalize, same bounded-window discipline as
//                          Stat Walls/Liquidity Zones' positional slot tags. Same live-only caveat as
//                          every tape-based section in this file: a bar's footprint only fills in
//                          from the moment real tick data starts flowing, never reconstructed for
//                          bars that closed before the indicator was running. Tick-bucketing
//                          (fpCurrentBar/fpCurrentBarIndex) is written under dataLock from
//                          OnMarketData (tape thread) exactly like cfCumulativeDelta; the finalize-
//                          and-draw half (RunFootprintBarLogic + DrawFootprintGrid, new calls in
//                          OnBarUpdate/DrawChartLines) takes a locked, deep-copied snapshot of
//                          whichever bar just closed, then draws unlocked from the OnBarUpdate
//                          thread, same producer/consumer split as Stat Walls/CVD Regression above --
//                          this file's one dataLock exception (Draw.* calls only ever issued from
//                          OnBarUpdate, never from OnMarketData/OnMarketDepth directly) is preserved,
//                          not broken, by this section. Not yet watched live by the user -- first
//                          cut, like every other addition to this file.
//    v1.0.0 patch 12 (2026-09-15) — REAL-BUG FIX: user's live panel showed "FOOTPRINT (no L2)  0
//                          bars shown" after hundreds of classified trades and dozens of closed bars
//                          (Aggression Split was reading 507/289 trades on the same feed) -- the
//                          feature compiled and ran but never actually drew anything. Root cause:
//                          RunFootprintBarLogic() tried to finalize a bar by checking
//                          "fpCurrentBarIndex == CurrentBar" -- comparing OnMarketData's real-time
//                          view of CurrentBar (tape thread) against OnBarUpdate's own view (main
//                          calc thread) at an arbitrary later moment, with no guaranteed ordering
//                          between the two. In practice they never lined up, so every bar's
//                          footprint was silently skipped forever. FIX: replaced the equality check
//                          with a producer-pushed queue (fpPendingFinalized) -- the instant
//                          OnMarketData itself detects a bar rollover (CurrentBar != fpCurrentBarIndex),
//                          it pushes the JUST-COMPLETED bar's cells onto the queue right there, under
//                          the same dataLock, needing no cross-thread agreement about "what bar is it
//                          right now." RunFootprintBarLogic (OnBarUpdate thread) now just drains
//                          whatever landed in that queue (almost always one bar, occasionally zero,
//                          rarely more than one) into fpDrawQueue for DrawFootprintGrid to draw, via
//                          a new DrawOneFootprintBar(barIndex, cells) helper (unchanged rendering
//                          logic, just now callable per-queue-entry instead of for a single bar).
//                          This is a real, previously-undiscovered bug in patch 11's first cut, not
//                          a tuning issue -- flagging it plainly rather than as a minor tweak.
//                          Balance-checked (386/386 braces, 1179/1179 parens, 424/424 brackets, 3273
//                          lines) and re-delivered to
//                          D:\NinjaTrader 8\bin\Custom\Indicators\SneakyOrderFlowDashboard_v1_0_0.cs.
//                          Not yet confirmed against the user's live panel post-fix.
//    v1.0.0 patch 13 (2026-09-15) — COMPILE FIX: NinjaTrader's compile grid reported
//                          "The type 'Indicator' already contains a definition for
//                          'cacheSneakyOrderFlowDashboard_v1_0_0'" (CS0102) plus a long run of
//                          CS0229 ambiguity errors. Cause: the file on disk had picked up a stray
//                          SECOND "#region NinjaScript generated code" block back-to-back with the
//                          first -- the first (stale) block predated the Footprint Grid properties
//                          (its factory-method parameter lists were missing fpEnabled/fpMaxBarsShown/
//                          fpImbalanceRatio/fpMinRowVolume entirely), the second was current. Since
//                          this whole region is NinjaTrader-owned boilerplate (auto-regenerated on
//                          every successful compile, never hand-edited here), the fix was simply
//                          deleting the stale first copy and keeping the current second one --
//                          no hand-written logic touched. Balance-checked and re-delivered to
//                          D:\NinjaTrader 8\bin\Custom\Indicators\SneakyOrderFlowDashboard_v1_0_0.cs.
//                          Not yet confirmed the user's compile is clean post-fix.
//    v1.0.0 patch 14 (2026-09-15) — COMPILE FIX (recurrence): the duplicate-generated-code
//                          problem came back WORSE after patch 13 -- this time TWO full,
//                          content-IDENTICAL copies of the generated-code block (both already
//                          up to date, both listing fpEnabled/etc.), plus new CS0111
//                          "already defines a member" and CS0121 "ambiguous call" errors on top
//                          of the same CS0102/CS0229 pattern. This confirms the duplication is
//                          NOT stale-vs-current content -- it is NinjaTrader's own compiler
//                          appending a freshly (re)generated copy of this block at the end of
//                          the file on every compile, without first detecting/removing whatever
//                          copy (correct or not) is already sitting there. Practically: every
//                          time this file is edited from outside the NinjaScript Editor and then
//                          compiled, expect this to recur unless the editor is given a genuinely
//                          clean load of the file first. Fix applied here is mechanical and
//                          identical to patch 13's: strip back down to exactly one copy of the
//                          block, touching no hand-written logic. Balance-checked and
//                          re-delivered to
//                          D:\NinjaTrader 8\bin\Custom\Indicators\SneakyOrderFlowDashboard_v1_0_0.cs.
//                          Recommended to the user: fully close NinjaTrader (not just the file's
//                          editor tab) before the next compile attempt, so no leftover in-memory
//                          copy of the old file content can be re-saved or re-merged over this fix.
//    v1.0.0 patch 16 (2026-09-15) — CORRECTED DIAGNOSIS: the file that came back broken this time
//                          was missing this changelog's own patch 15 entry entirely -- meaning
//                          what got compiled was NOT patch 15 (or even patch 14 cleanly), it was
//                          an OLDER snapshot of this file that got saved back to disk (wiping out
//                          patch 15's hand-written text) and then had one more duplicate
//                          generated-code block appended on top by the compiler. That rules out
//                          "the region-dedup fix itself is wrong" -- the fix has been correct every
//                          time; it just never reached the compiler, because whatever is doing the
//                          compiling (almost certainly a NinjaScript Editor tab left open on an old
//                          in-memory copy of this file) keeps re-saving that old copy over any
//                          externally-delivered fix before compiling it. This file's line endings
//                          were also normalized to Windows CRLF throughout in this pass (previously
//                          plain LF from being written outside NinjaTrader), in case that was also
//                          contributing to NinjaTrader's own codegen failing to recognize and
//                          cleanly replace its previous generated-code block. Balance-checked and
//                          re-delivered to
//                          D:\NinjaTrader 8\bin\Custom\Indicators\SneakyOrderFlowDashboard_v1_0_0.cs.
// ─────────────────────────────────────────────────────────────────────────────
//    v1.0.0 patch 17 (2026-09-15) -- RE-VERIFIED, NO CODE CHANGES: re-inspected this file fresh
//                          today and confirmed it is exactly as patch 16 left it -- one single
//                          generated-code region, full Windows CRLF line endings throughout, braces/
//                          parens/brackets balanced. (A side investigation earlier today into whether
//                          C:\Users\Administrator\Documents\NinjaTrader 8 and D:\NinjaTrader 8 were
//                          two separate installs turned out to be a dead end -- direct testing shows
//                          both paths resolve to this same file, so that was never the issue.) If the
//                          compile grid still shows the duplicate-region errors after this, the
//                          problem is not this file's content -- try a full close/reopen of the
//                          NinjaScript Editor tab for this file specifically before recompiling, since
//                          NinjaTrader can hold an old in-memory copy of a file open in an editor tab
//                          and recompile THAT instead of what's on disk.
//    v1.0.0 patch 18 (2026-09-15) -- REAL BUG FIX: user reported nearly every single bar showing a
//                          CVD-divergence triangle. Root cause: RunCvdBarLogicInner() recomputed
//                          "does price's 200-bar direction disagree with the CVD regression slope"
//                          fresh every bar with no memory of the previous bar's result -- once price
//                          and CVD landed on opposite sides (which is common and often persists for
//                          long stretches, not just one bar), it kept re-arming cfLastDivergence and
//                          DrawCvdDivergence() kept re-drawing the same triangle on every bar of that
//                          entire stretch, instead of marking just the bar the mismatch began. Fixed
//                          by tracking the raw ongoing state in a new cfPrevDivergenceState field and
//                          only setting cfLastDivergence (what actually gets drawn) on the bar the
//                          state changes -- none-to-bullish, none-to-bearish, or a direct flip. No
//                          change to the underlying CVD/regression math, only to how often the marker
//                          fires. Balance-checked, delivered to
//                          D:\NinjaTrader 8\bin\Custom\Indicators\SneakyOrderFlowDashboard_v1_0_0.cs.
// ─────────────────────────────────────────────────────────────────────────────
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SneakyOrderFlowDashboard_v1_0_0 : Indicator
    {
        // ===================================================================================
        // Shared small types
        // ===================================================================================
        private class LevelRef
        {
            public string Label;
            public double Price;
            public LevelRef(string label, double price) { Label = label; Price = price; }
        }

        private class Zone
        {
            public double PriceLow;
            public double PriceHigh;
            public List<string> Labels = new List<string>();
            public double MidPrice { get { return (PriceLow + PriceHigh) / 2.0; } }
        }

        private class DepthRow
        {
            public double Price;
            public long   Volume;
        }

        private class WallHit
        {
            public string Side;
            public double Price;
            public long   Volume;
        }

        private class ClusterHit
        {
            public string Side;
            public double PriceLow;
            public double PriceHigh;
            public long   TotalVolume;
            public int    LevelCount;
            public double MidPriceOf() { return (PriceLow + PriceHigh) / 2.0; }
        }

        private class FlowEvent
        {
            public DateTime Time;
            public string   Side;
            public string   Type;
            public double   Price;
            public long     Volume;
        }

        // One label = one price level that should get a screen-pinned text tag near the right edge
        // of the chart panel (see OnRender). Brush is a WPF System.Windows.Media.Brush -- converted
        // to a SharpDX color at render time.
        private class ChartLabel
        {
            public double Price;
            public string Text;
            public Brush  Brush;
        }

        // One lock guards every field touched from OnMarketDepth / OnMarketData (both real-time,
        // separate threads from OnBarUpdate). See file header.
        private readonly object dataLock = new object();

        // Built fresh every bar in DrawChartLines() (OnBarUpdate thread), read every frame by
        // OnRender() (UI/render thread) -- guarded by dataLock like everything else shared across
        // those two thread groups.
        private List<ChartLabel> chartLabelsSnapshot = new List<ChartLabel>();

        // ===================================================================================
        // 1) MARKET CONTEXT -- bar-based, unchanged from SneakyMarketContext_v1_0_0, EXCEPT the
        // VWAP-bull check no longer instantiates NinjaTrader's stock VWAP indicator via VWAP() --
        // see the v1.0.0 patch 5 changelog entry for why. It now reads the dashboard's OWN
        // running session VWAP (czSessionVwapPriceVolSum/czSessionVwapVolSum, already computed
        // for Confluence Zones every bar) instead -- the exact same number, just not re-derived
        // through a second indicator instance.
        // ===================================================================================
        private ADX  mcAdx;
        private DM   mcDm;
        private RSI  mcRsi;
        private MACD mcMacd;
        private EMA  mcTrendEma;
        private EMA  mcHtfTrendEma;

        // ===================================================================================
        // 1b) MARKET STATE + EFF/FLIP/POC(t)/BAL -- ported from the reference "BOOKMAP ORDER FLOW"
        // dashboard the user compared this file against. All bar-based, no Level 2 needed.
        //   MARKET STATE: Choppiness Index (Bill Dreiss's formula: 100*log10(sum(TR,n)/range(n))/
        //   log10(n)) crossed with ADX -> TRENDING / CHOPPY / TRANSITIONAL label.
        //   EFF: Kaufman's Efficiency Ratio (net directional move / total path length over the same
        //   window) -- 1.0 = pure trend, near 0 = pure noise.
        //   POC(t): TODAY's still-forming point of control -- reuses Confluence Zones' own
        //   ComputeProfileLevels() against czProfileVolume, which is already live/current for this
        //   bar (it only gets cleared+rebuilt at newSession, same object this dashboard already
        //   maintains for the PREVIOUS session's PS POC/VAH/VAL) -- no new profile-building logic.
        //   FLIP: counts how many times price has crossed from one side of POC(t) to the other this
        //   session -- a rotation/auction-activity counter.
        //   BAL: whether price is currently inside today's developing value area (VAL(t)-VAH(t)) --
        //   "balanced" (rotational) vs "imbalanced" (trending away from value), plus how many bars
        //   it's held that state.
        // Rolling buffers are hand-maintained (append+cap) rather than stock MAX()/MIN() indicator
        // instances -- consistent with this file's own established style elsewhere (cfHCvd, lsHDelta)
        // and avoids any repeat of the "requires X by name to compile" class of issue fixed in patch 5.
        // ===================================================================================
        private readonly List<double> mcHClose = new List<double>();
        private readonly List<double> mcHHigh  = new List<double>();
        private readonly List<double> mcHLow   = new List<double>();
        private readonly List<double> mcHTr    = new List<double>();
        private string mcMarketStateLabel = "—";
        private double mcChoppiness, mcEfficiency;
        private double mcPocT = double.NaN, mcVahT = double.NaN, mcValT = double.NaN;
        private int    mcPocFlipCount;
        private int    mcPocFlipPrevSide;   // +1 close above POC(t), -1 below, 0 = not yet known
        private bool   mcBalanced;
        private int    mcBalanceBarCount;

        // ===================================================================================
        // 2) CONFLUENCE ZONES -- bar-based, unchanged from SneakyConfluenceZones_v1_0_0
        // ===================================================================================
        private Dictionary<double, double> czProfileVolume = new Dictionary<double, double>();
        private double czSessionVwapPriceVolSum;
        private double czSessionVwapVolSum;
        private DateTime czSessionStartTime;
        private bool czSessionStarted;
        private double czCurHigh, czCurLow;
        private double czOrHigh, czOrLow;
        private bool czOrLocked;

        private bool czPsEstablished;
        private double czPsHigh = double.NaN, czPsLow = double.NaN, czPsClose = double.NaN;
        private double czPsVwap = double.NaN, czPsPoc = double.NaN, czPsVah = double.NaN, czPsVal = double.NaN;
        private List<Zone> czLastZones = new List<Zone>();
        // Cached copy of Close[0] as of the last bar close -- RefreshPanel can run on the depth/tape
        // thread, and NinjaScript's own Bars series (Close[0]) is only safe to read from the main
        // processing thread, so OnBarUpdate snapshots it here rather than RefreshPanel reading it live.
        private double czLastClose = double.NaN;

        // ===================================================================================
        // 3) LIQUIDITY / WALLS -- depth-based, unchanged from SneakyLiquidityZones_v1_0_0
        // ===================================================================================
        private List<DepthRow> lzBidRows = new List<DepthRow>();
        private List<DepthRow> lzAskRows = new List<DepthRow>();
        private bool     lzHasReceivedDepth;
        private DateTime lzLastPanelUpdate = DateTime.MinValue;
        private List<WallHit>    lzLastWalls = new List<WallHit>();
        private List<ClusterHit> lzLastClusters = new List<ClusterHit>();
        // CME "micro" root symbols -- see LzAutoScaleByInstrument in State.DataLoaded. These trade
        // at roughly 1/10th to 1/20th the resting size of their full-size counterparts (MNQ vs NQ,
        // MES vs ES, MYM vs YM, M2K vs RTY), so wall/cluster thresholds calibrated for full-size
        // liquidity need to come down sharply to ever trigger on a micro book.
        private static readonly HashSet<string> lzMicroRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MNQ", "MES", "MYM", "M2K", "MCL", "MGC"
        };

        // ===================================================================================
        // 3g) STACK / PULL -- ported from the reference dashboard. GENUINELY needs real Level 2
        // depth (it reads Add/Update/Remove operations at the best bid/ask, position 0, from
        // OnMarketDepth) -- unlike every other "(no L2)" section in this file, this one is honestly
        // gated behind lzHasReceivedDepth and shows "NO DEPTH" otherwise, same as the real
        // Liquidity/Walls section (3) it lives alongside. Tracks a rolling SpWindowSeconds sum of
        // size added vs size removed/decreased at the top of book, per side: net positive = size is
        // being STACKED (refreshed/added, the level is being defended or built up); net negative =
        // size is being PULLED (cancelled, the level's support may be fake/spoofed). This is a
        // classic tape-reading read distinct from the wall/cluster SIZE snapshot Liquidity/Walls
        // already shows -- it's about the RATE of change at the best price, not the resting total.
        // ===================================================================================
        private readonly Queue<KeyValuePair<DateTime, long>> spBidDeltas = new Queue<KeyValuePair<DateTime, long>>();
        private readonly Queue<KeyValuePair<DateTime, long>> spAskDeltas = new Queue<KeyValuePair<DateTime, long>>();
        private long spBidNetFlow, spAskNetFlow;

        // ===================================================================================
        // 3b) STAT WALLS -- absorption-inferred walls that need NO Level 2 depth at all. Concept
        // ported from the TradingIQ "Liquidity Walls" Pine v6 study (see SneakyLiquidityWalls_v1_0_0,
        // a separate file the user shared): classify trade ticks buy/sell via the TICK RULE (uptick =
        // buy, downtick = sell, zero-tick carries the last side), accumulate that into a per-bar order-
        // flow DELTA, and run a rolling OLS regression of "how far price actually moved" against
        // "what the delta was." When a bar's delta strongly favors one side but price barely moves,
        // the shortfall is z-scored; a high z means aggressive flow got absorbed by something passive
        // -- infer a wall at that price (ATR-thick, extends right until price trades through it).
        // This is a STATISTICAL INFERENCE from trade prints, never a read of resting book size, which
        // is exactly why it works on feeds (like the user's, on Lucid) that don't deliver Level 2
        // depth at all: it only needs Last trade prints, already confirmed flowing on this feed.
        // ⚠ Same historical-tick caveat as any tick-series approach: historical delta is only as
        // granular as the provider's historical tick data; live is exact.
        // Runs entirely on the ordinary bar-processing thread (the added 1-tick series' ticks arrive
        // through OnBarUpdate like any other data series, NOT a separate real-time thread the way
        // OnMarketDepth/OnMarketData are) -- but RefreshPanel can still be called from those other
        // threads, so writes here go under dataLock same as everything else it reads.
        // ===================================================================================
        private class StatWall
        {
            public int    Side;        // +1 resistance (above) / -1 support (below)
            public double Top, Bottom; // price bounds, Top > Bottom always
            public double Near;        // the edge price that must be broken to invalidate the wall
            public int    StartBar;
        }
        private readonly List<double> lsHDelta = new List<double>();
        private readonly List<double> lsHMove  = new List<double>();
        private readonly List<double> lsHFail  = new List<double>();
        private Series<double> lsDeltaSeries;
        private double lsCurBarDelta;
        private int    lsAccBar = -1;
        private double lsPrevTickPx = double.NaN;
        private int    lsLastTickDir = 1;
        private ATR    lsAtr;
        private List<StatWall> lsWalls = new List<StatWall>();
        private double lsLastZ, lsLastDelta, lsLastExpTicks, lsLastMoveTicks;
        private int    lsLastAbsorbSide;
        private bool   lsAbsorbedThisBar;

        // Liquidity Reaction/Hold -- session-cumulative tally of how price reacts every time it
        // tests a Stat Wall: HOLD (touches the wall's zone but closes back out the same bar -- the
        // wall defended) vs BREAK (price closes through it, already detected by UpdateStatWalls'
        // existing wall-removal check below). Ported from the reference dashboard's "LIQUIDITY
        // REACTION/HOLD" row. Ticked inside UpdateStatWalls(), which already runs under dataLock (via
        // RunStatWallBarLogic's wrapper) -- no extra locking needed here.
        private long   lrHoldCount, lrBreakCount;
        private string lrLastEvent = "—";

        // ===================================================================================
        // 3c) CVD REGRESSION -- another no-Level-2 technique, this time from CUMULATIVE volume
        // delta rather than per-bar delta. Reuses the SAME Lee & Ready trade classification
        // computed once per print in OnMarketData (below, shared with Aggression Split/Flow
        // Events) to accumulate a session-running CVD; a rolling OLS regression of CVD against
        // bar sequence over CfLookback bars publishes a slope + R² (fit-quality gate). A
        // divergence is flagged when price's own direction over that same window disagrees
        // with the CVD slope's direction, gated on R² clearing CfMinR2 -- an unfit slope is
        // noise, not a real read. See the file-header changelog entry for the full rationale
        // and how this differs from the user-shared SneakyFlow_v1_0_0 file it was requested from.
        // cfCumulativeDelta is written under dataLock from OnMarketData (tape thread); everything
        // else here (cfHCvd, cfLast*) is only ever touched from OnBarUpdate (RunCvdBarLogic),
        // same producer/consumer split as Stat Walls above.
        // ===================================================================================
        private double cfCumulativeDelta;
        private readonly List<double> cfHCvd = new List<double>();
        private double cfLastSlope, cfLastR2, cfLastCvd;
        private int    cfLastDivergence;       // +1 bullish, -1 bearish, 0 = none -- EDGE-TRIGGERED:
                                                // only non-zero on the bar the divergence state changes
                                                // (see cfPrevDivergenceState below), so the chart marker
                                                // fires once per divergence rather than every bar it persists.
        private int    cfPrevDivergenceState;  // raw ongoing state as of the just-processed bar (+1/-1/0).
                                                // Two jobs: (1) compared against next bar's fresh state to
                                                // detect the edge that sets cfLastDivergence, and (2) read
                                                // directly for the PANEL text (see RefreshPanel/cfDivergenceSnap)
                                                // so the panel shows "BULLISH/BEARISH" for as long as the
                                                // mismatch actually persists, not just the one bar it started --
                                                // unlike the edge-triggered chart marker, the panel text going
                                                // back to "none" the very next bar (patch 18's original bug
                                                // report: divergence "went from every bar to not showing any
                                                // at all") would make an ongoing divergence look like it never
                                                // happened at all.
        private bool   cfHasClassifiedTrade;   // mirrors asHasReceivedTrades -- true once real tape flows

        // Diagnostic counters (session-scoped, reset on newSession alongside cfHCvd) -- added after a
        // user reported zero divergence markers for an entire ~24hr session and understandably doubted
        // it. This distinguishes "the window/R² gate never even opened" from "the gate opened but price
        // and CVD's fitted trend genuinely never disagreed" from "a mismatch DID occur but something
        // downstream still isn't surfacing it" -- same "diagnose before re-patching blind" discipline as
        // the FAILED AUCTION armed/expired counters.
        private long   cfGateOpenBars;    // bars where n>=CfLookback && r2>=CfMinR2 (divergence actually evaluated)
        private long   cfMismatchBars;    // of those, bars where the RAW priceDir/slopeDir mismatch was true

        // ===================================================================================
        // 3h) FOOTPRINT GRID -- classic per-price bid×ask footprint cells, drawn directly at each
        // bar's location on the chart. Needs NO Level 2 depth -- reuses the SAME Lee & Ready trade
        // classification already computed once per print in OnMarketData (shared with Aggression
        // Split/Flow Events/CVD Regression above), just bucketed by (bar, tick-rounded price)
        // instead of summed into one running number. See the file-header changelog entry for the
        // full rationale (this is the one thing "free order flow, no Level 2" community tools like
        // the FlowPrint NT8 indicator show that this dashboard didn't already have).
        // fpCurrentBar/fpCurrentBarIndex/fpPendingFinalized are written under dataLock from
        // OnMarketData (tape thread) on every classified print -- same producer as cfCumulativeDelta
        // above. fpDrawQueue/fpBarOrder/fpBarPrices are only ever touched from the OnBarUpdate thread
        // (RunFootprintBarLogic drains fpPendingFinalized into fpDrawQueue under lock; DrawFootprintGrid,
        // called from DrawChartLines() right after, draws it unlocked) -- same producer/consumer split
        // as Stat Walls/CVD Regression's own bar-logic halves.
        // ===================================================================================
        private class FootprintCell { public long BidVol; public long AskVol; }
        private Dictionary<double, FootprintCell> fpCurrentBar = new Dictionary<double, FootprintCell>();
        private int    fpCurrentBarIndex = -1;
        private bool   fpHasClassifiedTrade;   // mirrors cfHasClassifiedTrade -- true once real tape flows
        // fpPendingFinalized -- bars whose tick collection is DONE (a later print already rolled
        // fpCurrentBarIndex past them), pushed here by OnMarketData the instant it detects the
        // rollover, under dataLock. This replaces an earlier first-cut design that instead tried to
        // match fpCurrentBarIndex == CurrentBar from inside RunFootprintBarLogic (OnBarUpdate thread)
        // -- that equality check turned out to never reliably hold: OnMarketData's real-time view of
        // CurrentBar and OnBarUpdate's own view of CurrentBar are updated by two different threads
        // with no guaranteed ordering between them, so the two consistently disagreed by the time
        // RunFootprintBarLogic checked, and the feature silently drew nothing ("0 bars shown" even
        // with hundreds of classified trades flowing) despite compiling and running cleanly. Pushing
        // a bar into this queue the moment ITS OWN THREAD detects it is complete needs no cross-
        // thread agreement about "what bar is OnBarUpdate on right now" at all -- RunFootprintBarLogic
        // just drains whatever has accumulated here (almost always exactly one bar, occasionally zero
        // on a quiet OnBarUpdate tick, rarely more than one if several bars closed between OnBarUpdate
        // calls) rather than trying to line up with a specific CurrentBar value.
        private readonly List<KeyValuePair<int, Dictionary<double, FootprintCell>>> fpPendingFinalized = new List<KeyValuePair<int, Dictionary<double, FootprintCell>>>();
        private List<KeyValuePair<int, Dictionary<double, FootprintCell>>> fpDrawQueue;   // set by RunFootprintBarLogic, consumed by DrawFootprintGrid, same OnBarUpdate call
        private readonly List<int> fpBarOrder = new List<int>();                       // finalized bar indices, oldest first (OnBarUpdate-owned)
        private readonly Dictionary<int, List<double>> fpBarPrices = new Dictionary<int, List<double>>(); // per-bar drawn price rows, for eviction cleanup (OnBarUpdate-owned)

        // ===================================================================================
        // 3d) SEQUENCE / EXHAUSTION (CHANGE/LEAD-LAG) -- ported from the reference dashboard. Bar-
        // based, no Level 2 needed. A "sequence" is a run of consecutive higher-high+higher-low bars
        // (up) or lower-low+lower-high bars (down) -- classic impulse-leg counting. EXHAUSTION fires
        // when the sequence has run at least SqExhaustionBars bars AND the current bar's range has
        // contracted to <= SqExhaustionRangeFactor of the sequence's own average range so far -- the
        // classic "momentum petering out" signature. LEAD-LAG reuses the already-computed CVD
        // Regression slope (cfLastSlope, from 3c immediately before this in the same OnBarUpdate
        // call) to say whether order flow CONFIRMS or DIVERGES from the current sequence's direction
        // -- distinct from (and complementary to) the CVD section's own price-vs-CVD divergence
        // marker, which looks at the whole regression window rather than the live impulse leg.
        // ===================================================================================
        private int    sqDirection;            // +1 up sequence, -1 down sequence, 0 = none active
        private int    sqCount;
        private readonly List<double> sqHRange = new List<double>();
        private int    sqLastExhaustion;        // +1 bullish exhaustion this bar, -1 bearish, 0 = none
        private string sqLeadLagLabel = "—";

        // ===================================================================================
        // 3f) FAILED AUCTION / TRAPPED TRADERS -- ported from the reference dashboard. Bar-based, no
        // Level 2 needed -- watches four existing boundaries this dashboard already computes (OR
        // HIGH/LOW from Confluence Zones' opening range, PS HIGH/LOW from the previous session) for
        // a breakout, then checks whether it holds:
        //   FAILED AUCTION: price closes beyond a boundary (a breakout attempt is "armed"), then
        //   closes back inside it within FaFailBars bars -- classic false breakout / failed auction.
        //   TRAPPED TRADERS: after a failed auction, price then closes back through the SAME boundary
        //   by more than FaTrappedTicks ticks within FaTrappedBars more bars -- a harder reversal
        //   implying the breakout traders are now trapped and their stops are feeding the move.
        // A BreakoutWatch is armed once per fresh crossing (faPrevSide tracks which side of each
        // level price closed on last bar, so re-arming only happens on a NEW crossing, not every bar
        // price happens to sit beyond the level).
        // ===================================================================================
        private class BreakoutWatch { public string Level; public double Price; public int Side; public int ArmedBar; }
        private class TrappedWatch  { public string Level; public double Price; public int Side; public int FailedBar; }
        private readonly List<BreakoutWatch> faActiveWatches  = new List<BreakoutWatch>();
        private readonly List<TrappedWatch>  faPendingTrapped = new List<TrappedWatch>();
        private readonly Dictionary<string, int> faPrevSide = new Dictionary<string, int>();
        private long   faFailedCount, faTrappedCount;
        private string faLastFailedEvent = "—", faLastTrappedEvent = "—";
        // Diagnostic-only counters (v1.0.0 patch 10) -- faFailedCount alone can't tell a user whether
        // "0x all day" means the arming logic never sees a fresh breakout (a real bug) or it arms
        // fine but every watch takes longer than FaFailBars to resolve one way or the other (a
        // tuning issue, not a bug) -- so this session-cumulative armed/expired tally is surfaced on
        // the panel next to FAILED AUCTION to make the difference visible without guesswork.
        private long   faArmedCount, faExpiredCount;
        private int    faFiredFailedSide, faFiredTrappedSide;   // set only on the bar an event fires, for the chart marker

        // ===================================================================================
        // 4) AGGRESSION SPLIT -- tape-based, unchanged from SneakyAggressionSplit_v1_0_0
        // ===================================================================================
        private long asSessionBuyVolume;
        private long asSessionSellVolume;
        private Queue<FlowEvent> asRollingTrades = new Queue<FlowEvent>();   // reuse FlowEvent shape (Time/Side/Volume)
        private long asRollingBuyVolume;
        private long asRollingSellVolume;
        private long   asLargestPrintVolume;
        private double asLargestPrintPrice;
        private string asLargestPrintSide;
        private bool     asHasReceivedTrades;
        private DateTime asLastPanelUpdate = DateTime.MinValue;

        // ===================================================================================
        // 5) FLOW EVENTS -- tape-based, unchanged from SneakyFlowEvents_v1_0_0
        // ===================================================================================
        private double   feBidTouchPrice = double.NaN;
        private DateTime feBidTouchStart;
        private long     feBidTouchVolume;
        private bool     feBidAbsorptionFired;
        private double   feAskTouchPrice = double.NaN;
        private DateTime feAskTouchStart;
        private long     feAskTouchVolume;
        private bool     feAskAbsorptionFired;
        private List<FlowEvent> feRecentEvents = new List<FlowEvent>();

        // Shared trade classification (used by BOTH Aggression Split and Flow Events -- merging
        // let this be one implementation instead of two copies of the same Lee & Ready logic).
        private double lastTradePrice = double.NaN;
        private string lastTradeSide;

        // ===================================================================================
        // Panel plumbing
        // ===================================================================================
        private Grid           panelOuter;
        private Grid           chartHostGrid;
        private ScrollViewer    contentScroll;
        private Grid            contentGrid;
        private TextBlock       panelTitle;
        private TranslateTransform panelDragTransform;
        private Point           panelDragStart;
        private bool            panelDragging;

        // Market Context rows
        private TextBlock mcControlText, mcHtfTrendText, mcTrendText, mcMomentumText, mcConfluenceText;
        private TextBlock mcMarketStateText, mcEffFlipBalText;
        // Visual-polish gauge markers for Trend/Momentum/Confluence (System.Windows.Shapes types are
        // fully qualified everywhere in this file rather than adding a blanket `using
        // System.Windows.Shapes;` -- that would collide with NinjaTrader.NinjaScript.DrawingTools'
        // own "Rectangle" drawing-tool class, already imported above for Draw.Rectangle elsewhere in
        // this file. Same discipline this project already uses for SharpDX types in OnRender.)
        private System.Windows.Shapes.Ellipse mcTrendMarker, mcMomentumMarker, mcConfluenceMarker;
        // Confluence Zones rows
        private TextBlock czLocationText, czPsHlcText, czPsVwapPocText, czPsValueText, czSessionVwapText, czOrText, czZonesHeaderText;
        private List<TextBlock> czZoneRowTexts = new List<TextBlock>();
        // Liquidity Zones rows
        private TextBlock lzStatusText, lzBidAskText, lzImbalanceText, lzWallsHeaderText, lzClustersHeaderText;
        private TextBlock spHeaderText;
        private List<TextBlock> lzWallRowTexts = new List<TextBlock>();
        private List<TextBlock> lzClusterRowTexts = new List<TextBlock>();
        // Stat Walls rows (no Level 2 needed)
        private TextBlock lsHeaderText;
        private List<TextBlock> lsWallRowTexts = new List<TextBlock>();
        // Liquidity Reaction/Hold row (no Level 2 needed)
        private TextBlock lrHeaderText;
        // CVD Regression rows (no Level 2 needed)
        private TextBlock cfHeaderText, cfDivergenceText;
        private System.Windows.Shapes.Polyline cfSparkline;
        private const double CfSparklineWidth = 240, CfSparklineHeight = 28;
        // Sequence / Exhaustion rows (no Level 2 needed)
        private TextBlock sqHeaderText, sqExhaustionText;
        // Footprint Grid row (no Level 2 needed)
        private TextBlock fpHeaderText;
        // Failed Auction / Trapped Traders rows (no Level 2 needed)
        private TextBlock faHeaderText, faTrappedText;
        // Aggression Split rows
        private TextBlock asStatusText, asSessionSplitText, asRollingSplitText, asPressureText, asLargestPrintText;
        private ColumnDefinition asBarBuyCol, asBarSellCol;
        // Flow Events rows
        private TextBlock feStatusText, feWatchingBidText, feWatchingAskText, feEventsHeaderText;
        private List<TextBlock> feEventRowTexts = new List<TextBlock>();

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Combined Sneaky Suite order-flow dashboard: Market Context (+ Market "
                            + "State, Efficiency/Flip/POC(t)/Balance), Confluence Zones, "
                            + "Liquidity/Walls (+ Stack/Pull), Stat Walls (+ Liquidity Reaction/Hold), "
                            + "CVD Regression (+ Sequence/Exhaustion), Failed Auction/Trapped Traders, "
                            + "Footprint Grid (per-price bid x ask cells on each bar), "
                            + "Aggression Split, and Flow Events -- ONE scrollable panel, plus chart "
                            + "lines/markers with labels. Consolidates the five standalone modules "
                            + "plus the reference dashboard's full feature set -- see file header. "
                            + "Liquidity/Walls and Stack/Pull need real Level 2 depth and are "
                            + "real-time only; every other section infers reads from ordinary bars "
                            + "and trade prints alone and works even without a Level 2 depth feed.";
                Name                     = "Sneaky Order Flow Dashboard";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = true;
                DrawOnPricePanel         = true;
                DisplayInDataBox         = false;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;

                // Market Context
                McTrendPeriod            = 14;
                McMomentumPeriod         = 14;
                McMacdFast               = 12;
                McMacdSlow               = 26;
                McMacdSignal             = 9;
                McHigherTimeframeMinutes = 30;
                McContestedThreshold     = 60;
                McMarketStateLookback    = 14;
                McChoppyThreshold        = 61.8;
                McTrendyThreshold        = 38.2;

                // Confluence Zones
                CzProfileBucketTicks  = 4;
                CzValueAreaPercent    = 70;
                CzOpeningRangeMinutes = 15;
                CzZoneToleranceTicks  = 8;
                CzMaxZonesShown       = 3;

                // Liquidity Zones
                LzLevelsAnalyzed         = 10;
                LzWallSizeThreshold      = 50;
                LzClusterSizeThreshold  = 75;
                LzZoneToleranceTicks     = 4;
                LzMaxWallsShown          = 2;
                LzMaxClustersShown       = 2;
                LzAutoScaleByInstrument  = true;

                // Stack / Pull (needs real Level 2 depth)
                SpEnabled         = true;
                SpWindowSeconds   = 10;
                SpStackThreshold  = 50;

                // Stat Walls (no Level 2 needed)
                LsEnabled         = true;
                LsLookback        = 100;
                LsZThreshold      = 2.0;
                LsAtrPeriod       = 14;
                LsAtrMult         = 1.0;
                LsMaxWalls        = 6;
                LsMaxWallsShown   = 3;

                // CVD Regression (no Level 2 needed)
                CfEnabled  = true;
                CfLookback = 200;
                CfMinR2    = 0.30;
                CfDivMarkerHeightTicks = 6;

                // Sequence / Exhaustion (no Level 2 needed)
                SqEnabled             = true;
                SqExhaustionBars      = 5;
                SqExhaustionRangeFactor = 0.6;
                SqGateLeadLagByR2     = true;

                // Failed Auction / Trapped Traders (no Level 2 needed)
                FaEnabled     = true;
                FaFailBars    = 5;
                FaTrappedBars = 5;
                FaTrappedTicks = 12;

                // Footprint Grid (no Level 2 needed)
                FpEnabled         = true;
                FpMaxBarsShown    = 10;
                FpImbalanceRatio  = 3.0;
                FpMinRowVolume    = 1;

                // Aggression Split
                AsRollingWindowSeconds = 60;
                AsLargePrintThreshold  = 25;
                AsPressureThreshold    = 55;

                // Flow Events
                FeSweepVolumeThreshold      = 40;
                FeSweepWindowSeconds        = 3;
                FeAbsorptionVolumeThreshold = 100;
                FeAbsorptionWindowSeconds   = 30;
                FeMaxEventsShown            = 3;

                // Panel & lines
                PanelRefreshMs        = 250;
                ShowPanel             = true;
                ShowConfluenceLines   = true;
                ShowLiquidityLines    = true;
                ShowStatWallLines     = true;
                ShowCvdMarkers        = true;
                ShowSequenceMarkers   = true;
                ShowFailedAuctionMarkers = true;
                PanelLeftOffset       = 10;
                PanelTopOffset        = 35;
                PanelMaxHeight        = 700;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, McHigherTimeframeMinutes);
                // Series index 2 (added second) -- feeds Stat Walls' tick-rule delta. Ticks on this
                // series arrive through the ordinary OnBarUpdate dispatch (BarsInProgress == 2), same
                // as the minute series above -- NOT a separate real-time thread like OnMarketDepth.
                AddDataSeries(BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                mcAdx         = ADX(McTrendPeriod);
                mcDm          = DM(McTrendPeriod);
                mcRsi         = RSI(McMomentumPeriod, 3);
                mcMacd        = MACD(McMacdFast, McMacdSlow, McMacdSignal);
                mcTrendEma    = EMA(McTrendPeriod);
                mcHtfTrendEma = EMA(Closes[1], McTrendPeriod);
                lsDeltaSeries = new Series<double>(this);
                lsAtr         = ATR(LsAtrPeriod);

                // Liquidity Zones -- per-instrument auto-scale (see LzAutoScaleByInstrument).
                // Instrument/MasterInstrument is reliably populated by DataLoaded. This runs on
                // every load (including an already-saved chart instance), so it intentionally
                // overrides whatever LzWallSizeThreshold/LzClusterSizeThreshold were saved with --
                // that's the point of "auto": it tracks the instrument, not a one-time default.
                // Set LzAutoScaleByInstrument = false in Properties to pin manual values instead.
                if (LzAutoScaleByInstrument && Instrument != null && Instrument.MasterInstrument != null)
                {
                    bool isMicro = lzMicroRoots.Contains(Instrument.MasterInstrument.Name);
                    LzWallSizeThreshold    = isMicro ? 12 : 50;
                    LzClusterSizeThreshold = isMicro ? 20 : 75;
                }
            }
            else if (State == State.Historical)
            {
                if (ShowPanel && ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(() => AddPanel());
            }
            else if (State == State.Terminated)
            {
                if (ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(() => RemovePanel());
            }
        }

        // ===================================================================================
        // OnBarUpdate -- runs the two bar-based sections, resets session-scoped tape counters,
        // draws all chart lines, and does the panel heartbeat refresh. Everything here runs on the
        // normal single-threaded bar-processing path -- safe for Draw.* calls.
        // ===================================================================================
        protected override void OnBarUpdate()
        {
            // Stat Walls' 1-tick series (index 2) -- accumulate order-flow delta into the forming
            // primary bar. Runs on the same ordinary bar-processing dispatch as everything else here,
            // just for a different series -- not a real-time-only thread like OnMarketDepth.
            if (BarsInProgress == 2)
            {
                try { RunStatWallTickLogic(); }
                catch (Exception ex) { Print("SneakyOrderFlowDashboard StatWall tick logic error: " + ex.Message); }
                return;
            }
            if (BarsInProgress != 0) return;

            bool newSession = Bars.IsFirstBarOfSession;

            try { RunConfluenceZonesBarLogic(newSession); }
            catch (Exception ex) { Print("SneakyOrderFlowDashboard ConfluenceZones bar logic error: " + ex.Message); }

            try { RunMarketContextBarLogic(); }
            catch (Exception ex) { Print("SneakyOrderFlowDashboard MarketContext bar logic error: " + ex.Message); }

            try { RunMarketStateBarLogic(newSession); }
            catch (Exception ex) { Print("SneakyOrderFlowDashboard MarketState bar logic error: " + ex.Message); }

            try { RunStatWallBarLogic(); }
            catch (Exception ex) { Print("SneakyOrderFlowDashboard StatWall bar logic error: " + ex.Message); }

            if (newSession)
            {
                lock (dataLock)
                {
                    asSessionBuyVolume = 0; asSessionSellVolume = 0; cfCumulativeDelta = 0; cfHCvd.Clear();
                    cfGateOpenBars = 0; cfMismatchBars = 0;
                    lrHoldCount = 0; lrBreakCount = 0; lrLastEvent = "—";
                    faFailedCount = 0; faTrappedCount = 0; faLastFailedEvent = "—"; faLastTrappedEvent = "—";
                    faArmedCount = 0; faExpiredCount = 0;
                    faActiveWatches.Clear(); faPendingTrapped.Clear(); faPrevSide.Clear();
                }
            }

            try { RunCvdBarLogic(); }
            catch (Exception ex) { Print("SneakyOrderFlowDashboard CVD Regression bar logic error: " + ex.Message); }

            try { RunSequenceBarLogic(); }
            catch (Exception ex) { Print("SneakyOrderFlowDashboard Sequence/Exhaustion bar logic error: " + ex.Message); }

            try { RunFailedAuctionBarLogic(); }
            catch (Exception ex) { Print("SneakyOrderFlowDashboard Failed Auction/Trapped bar logic error: " + ex.Message); }

            try { RunFootprintBarLogic(); }
            catch (Exception ex) { Print("SneakyOrderFlowDashboard Footprint Grid bar logic error: " + ex.Message); }

            try { DrawChartLines(); }
            catch (Exception ex) { Print("SneakyOrderFlowDashboard DrawChartLines error: " + ex.Message); }

            try { RefreshPanel(); }
            catch (Exception ex) { Print("SneakyOrderFlowDashboard RefreshPanel error: " + ex.Message); }
        }

        // -----------------------------------------------------------------------------------
        // MARKET CONTEXT bar logic (unchanged formulas from SneakyMarketContext_v1_0_0)
        // -----------------------------------------------------------------------------------
        private double mcTrendScore, mcMomentumScore, mcConfluenceScore;
        private bool mcTrendUp, mcMomentumUp, mcConfluenceBull, mcHtfBull;
        private int mcMajorityVotes;
        private bool mcReady;

        private void RunMarketContextBarLogic()
        {
            int minBars = Math.Max(McMacdSlow + McMacdSignal, Math.Max(McTrendPeriod, McMomentumPeriod)) + 5;
            if (CurrentBar < minBars) { lock (dataLock) { mcReady = false; } return; }
            if (CurrentBars[1] < McTrendPeriod + 5) { lock (dataLock) { mcReady = false; } return; }

            double trendScore = mcAdx[0];
            bool   trendUp    = mcDm.DiPlus[0] > mcDm.DiMinus[0];

            double momentumScore = mcRsi[0];
            bool   momentumUp    = mcRsi[0] >= 50;

            // Reuses Confluence Zones' own already-computed running session VWAP instead of a
            // second stock VWAP() indicator instance -- see the field-block comment above section
            // 1's declarations and the v1.0.0 patch 5 changelog entry. RunConfluenceZonesBarLogic()
            // runs immediately before this in the same OnBarUpdate call (same thread), so
            // czSessionVwapPriceVolSum/czSessionVwapVolSum are already current for this bar --
            // reading them here unlocked is safe, same reasoning as elsewhere in this file for a
            // same-thread, sequential read.
            double mcRunningSessionVwap = czSessionVwapVolSum > 0 ? czSessionVwapPriceVolSum / czSessionVwapVolSum : Close[0];
            bool vwapBull = Close[0] >= mcRunningSessionVwap;
            bool emaBull  = Close[0] >= mcTrendEma[0];
            bool macdBull = mcMacd[0] >= mcMacd.Avg[0];
            bool htfBull  = mcHtfTrendEma[0] >= mcHtfTrendEma[McTrendPeriod];

            int bullVotes = (vwapBull ? 1 : 0) + (emaBull ? 1 : 0) + (macdBull ? 1 : 0)
                          + (trendUp ? 1 : 0) + (momentumUp ? 1 : 0) + (htfBull ? 1 : 0);
            int bearVotes = 6 - bullVotes;
            int majorityVotes    = Math.Max(bullVotes, bearVotes);
            bool confluenceBull  = bullVotes >= bearVotes;
            double confluenceScore = majorityVotes / 6.0 * 100.0;

            // Fields below are also read from RefreshPanel, which can run on the depth/tape thread --
            // written together under one lock to avoid a torn cross-thread read of this bar's result.
            lock (dataLock)
            {
                mcTrendScore = trendScore; mcTrendUp = trendUp;
                mcMomentumScore = momentumScore; mcMomentumUp = momentumUp;
                mcHtfBull = htfBull;
                mcMajorityVotes = majorityVotes; mcConfluenceBull = confluenceBull; mcConfluenceScore = confluenceScore;
                mcReady = true;
            }
        }

        // -----------------------------------------------------------------------------------
        // MARKET STATE + EFF/FLIP/POC(t)/BAL -- see the field-block comment above for the full
        // rationale. Fields this writes are also read from RefreshPanel, which can run on the
        // depth/tape thread -- runs under dataLock, same pattern as Stat Walls/CVD Regression above.
        // -----------------------------------------------------------------------------------
        private void RunMarketStateBarLogic(bool newSession)
        {
            lock (dataLock) { RunMarketStateBarLogicInner(newSession); }
        }

        private void RunMarketStateBarLogicInner(bool newSession)
        {
            if (newSession)
            {
                mcPocFlipCount = 0; mcPocFlipPrevSide = 0;
                mcBalanced = false; mcBalanceBarCount = 0;
            }

            double tr = mcHClose.Count > 0
                ? Math.Max(High[0] - Low[0], Math.Max(Math.Abs(High[0] - mcHClose[mcHClose.Count - 1]), Math.Abs(Low[0] - mcHClose[mcHClose.Count - 1])))
                : High[0] - Low[0];

            mcHClose.Add(Close[0]); mcHHigh.Add(High[0]); mcHLow.Add(Low[0]); mcHTr.Add(tr);
            int cap = McMarketStateLookback + 1;
            while (mcHClose.Count > cap) mcHClose.RemoveAt(0);
            while (mcHHigh.Count  > cap) mcHHigh.RemoveAt(0);
            while (mcHLow.Count   > cap) mcHLow.RemoveAt(0);
            while (mcHTr.Count    > cap) mcHTr.RemoveAt(0);

            int n = McMarketStateLookback;
            double choppiness = 0, efficiency = 0;
            bool haveWindow = mcHClose.Count > n;
            if (haveWindow)
            {
                int start = mcHClose.Count - n;
                double sumTr = 0, hi = double.MinValue, lo = double.MaxValue;
                for (int i = start; i < mcHClose.Count; i++)
                {
                    sumTr += mcHTr[i];
                    hi = Math.Max(hi, mcHHigh[i]);
                    lo = Math.Min(lo, mcHLow[i]);
                }
                double range = hi - lo;
                if (range > 0 && sumTr > 0)
                    choppiness = 100.0 * Math.Log10(sumTr / range) / Math.Log10(n);

                double netChange = Math.Abs(mcHClose[mcHClose.Count - 1] - mcHClose[start - 1]);
                double pathSum = 0;
                for (int i = start; i < mcHClose.Count; i++)
                    pathSum += Math.Abs(mcHClose[i] - mcHClose[i - 1]);
                efficiency = pathSum > 0 ? netChange / pathSum : 0;
            }

            string stateLabel;
            if (!haveWindow) stateLabel = "—";
            else if (mcAdx[0] >= 25 && choppiness <= McTrendyThreshold) stateLabel = "TRENDING";
            else if (choppiness >= McChoppyThreshold) stateLabel = "CHOPPY";
            else stateLabel = "TRANSITIONAL";

            // Developing (still-forming) session profile -- reuses Confluence Zones' own
            // ComputeProfileLevels() against czProfileVolume, which RunConfluenceZonesBarLogic()
            // (runs immediately before this in the same OnBarUpdate call) has already updated for
            // this bar -- no separate profile-building logic needed.
            double pocT, vahT, valT;
            ComputeProfileLevels(out pocT, out vahT, out valT);

            int flipSide = mcPocFlipPrevSide;
            if (!double.IsNaN(pocT))
            {
                flipSide = Close[0] > pocT ? 1 : (Close[0] < pocT ? -1 : mcPocFlipPrevSide);
                if (mcPocFlipPrevSide != 0 && flipSide != 0 && flipSide != mcPocFlipPrevSide)
                    mcPocFlipCount++;
            }

            bool balanced = !double.IsNaN(vahT) && !double.IsNaN(valT) && Close[0] >= valT && Close[0] <= vahT;

            mcChoppiness = choppiness; mcEfficiency = efficiency; mcMarketStateLabel = stateLabel;
            mcPocT = pocT; mcVahT = vahT; mcValT = valT;
            if (flipSide != 0) mcPocFlipPrevSide = flipSide;
            if (balanced == mcBalanced) mcBalanceBarCount++; else { mcBalanced = balanced; mcBalanceBarCount = 1; }
        }

        // -----------------------------------------------------------------------------------
        // STAT WALLS -- tick logic (runs on the 1-tick series, BarsInProgress == 2) + bar logic
        // (runs on the primary series bar close). No Level 2 depth involved anywhere in this
        // section -- see the field-block comment above for the full method (tick-rule delta +
        // rolling OLS regression + absorption z-score), ported from the TradingIQ "Liquidity Walls"
        // concept in SneakyLiquidityWalls_v1_0_0.
        // -----------------------------------------------------------------------------------
        private void RunStatWallTickLogic()
        {
            if (!LsEnabled) return;
            int pb = CurrentBars[0];
            if (pb < 0) return;
            if (pb != lsAccBar) { lsAccBar = pb; lsCurBarDelta = 0; }   // primary rolled -> fresh accumulator

            double px  = Closes[2][0];
            double vol = Volumes[2][0];
            if (!double.IsNaN(lsPrevTickPx))
            {
                if      (px > lsPrevTickPx) lsLastTickDir = 1;
                else if (px < lsPrevTickPx) lsLastTickDir = -1;
                // zero-tick: carry the last direction (tick-rule convention)
            }
            lsPrevTickPx = px;
            lsCurBarDelta += vol * lsLastTickDir;
            if (lsDeltaSeries != null) lsDeltaSeries[0] = lsCurBarDelta;
        }

        // Fields this writes (lsWalls, lsLastZ, etc.) are also read from RefreshPanel, which can run
        // on the depth/tape thread -- the whole method runs under dataLock, same pattern as Confluence
        // Zones above. DrawStatWalls() (called only from OnBarUpdate, right after this) reads lsWalls
        // without a lock -- safe, since both run on the same thread in the same OnBarUpdate call.
        private void RunStatWallBarLogic()
        {
            lock (dataLock) { RunStatWallBarLogicInner(); }
        }

        private void RunStatWallBarLogicInner()
        {
            if (!LsEnabled) return;

            double delta = lsDeltaSeries != null ? lsDeltaSeries[0] : 0.0;
            double moveTicks = (Close[0] - Open[0]) / TickSize;

            // Rolling OLS regression over the previous LsLookback closed bars: expTicks = alpha + beta*delta
            double expTicks = moveTicks;   // degenerate default before warmup
            int c = lsHDelta.Count;
            if (c >= LsLookback)
            {
                double sd = 0, sm = 0;
                for (int i = c - LsLookback; i < c; i++) { sd += lsHDelta[i]; sm += lsHMove[i]; }
                double md = sd / LsLookback, mm = sm / LsLookback;
                double vd = 0, cov = 0;
                for (int i = c - LsLookback; i < c; i++)
                {
                    double dd = lsHDelta[i] - md, dmv = lsHMove[i] - mm;
                    vd += dd * dd; cov += dd * dmv;
                }
                double beta  = vd > 0 ? cov / vd : 0;
                double alpha = mm - beta * md;
                expTicks = alpha + beta * delta;
            }

            // Signed shortfall (absorption): price failed to follow the flow
            double failTicks = Math.Sign(expTicks) * (expTicks - moveTicks);

            // z-score of failTicks over the last LsLookback bars (including current)
            lsHFail.Add(failTicks);
            while (lsHFail.Count > LsLookback) lsHFail.RemoveAt(0);
            double z = 0;
            if (lsHFail.Count >= 2)
            {
                double s = 0; for (int i = 0; i < lsHFail.Count; i++) s += lsHFail[i];
                double m = s / lsHFail.Count, v = 0;
                for (int i = 0; i < lsHFail.Count; i++) { double d = lsHFail[i] - m; v += d * d; }
                double sdv = Math.Sqrt(v / lsHFail.Count);
                if (sdv > 0) z = (failTicks - m) / sdv;
            }

            int absorbSide = Math.Sign(expTicks);   // +1 = up-flow absorbed -> resistance; -1 = down-flow -> support
            double atr = (CurrentBar > LsAtrPeriod) ? lsAtr[0] * LsAtrMult : 0;

            lsAbsorbedThisBar = false;
            UpdateStatWalls();
            if (z >= LsZThreshold && absorbSide != 0 && atr > 0)
            {
                SpawnStatWall(absorbSide, atr);
                lsAbsorbedThisBar = true;
            }

            lsHDelta.Add(delta); lsHMove.Add(moveTicks);
            while (lsHDelta.Count > LsLookback) lsHDelta.RemoveAt(0);
            while (lsHMove.Count  > LsLookback) lsHMove.RemoveAt(0);

            lsLastZ = z; lsLastDelta = delta; lsLastExpTicks = expTicks; lsLastMoveTicks = moveTicks;
            lsLastAbsorbSide = absorbSide;
        }

        // Break-through check on existing walls -- pure data, no Draw.* here (drawing happens in
        // DrawStatWalls, called from DrawChartLines, matching this file's compute/draw separation).
        private void UpdateStatWalls()
        {
            for (int i = lsWalls.Count - 1; i >= 0; i--)
            {
                StatWall w = lsWalls[i];
                bool broken = w.Side > 0 ? High[0] > w.Top : Low[0] < w.Bottom;
                if (broken)
                {
                    lrBreakCount++;
                    lrLastEvent = "BREAK " + w.Near.ToString("0.00") + " " + (w.Side > 0 ? "RESIST" : "SUPPORT");
                    lsWalls.RemoveAt(i);
                    continue;
                }

                // Touched the wall's zone this bar but didn't break through it, and closed back
                // outside the zone -- a defended test. Counted per bar (a wall can rack up multiple
                // HOLDs over its lifetime as price repeatedly probes it), matching how a human
                // reading the tape would count each fresh test of a level.
                bool touched  = w.Side > 0 ? High[0] >= w.Bottom : Low[0] <= w.Top;
                bool rejected = w.Side > 0 ? Close[0] < w.Bottom : Close[0] > w.Top;
                if (touched && rejected)
                {
                    lrHoldCount++;
                    lrLastEvent = "HOLD " + w.Near.ToString("0.00") + " " + (w.Side > 0 ? "RESIST" : "SUPPORT");
                }
            }
        }

        private void SpawnStatWall(int side, double atr)
        {
            while (lsWalls.Count >= LsMaxWalls && lsWalls.Count > 0) lsWalls.RemoveAt(0);
            StatWall w = new StatWall { Side = side, StartBar = CurrentBar };
            if (side > 0) { w.Bottom = High[0]; w.Top = High[0] + atr; w.Near = w.Bottom; }   // resistance above
            else          { w.Top    = Low[0];  w.Bottom = Low[0] - atr; w.Near = w.Top; }    // support below
            lsWalls.Add(w);
        }

        // -----------------------------------------------------------------------------------
        // CVD REGRESSION -- bar logic (runs once per primary-series bar close). Snapshots the
        // tape-thread-accumulated cfCumulativeDelta, appends it to the rolling window, fits an
        // OLS regression of CVD (y) against sequential bar index within the window (x = 0..n-1),
        // and derives slope/R². A divergence is flagged only once the window is full AND R²
        // clears CfMinR2 -- both guard against reading a slope out of noise. Fields this writes
        // are also read from RefreshPanel, which can run on the depth/tape thread -- runs under
        // dataLock, same pattern as Confluence Zones/Stat Walls above.
        // -----------------------------------------------------------------------------------
        private void RunCvdBarLogic()
        {
            lock (dataLock) { RunCvdBarLogicInner(); }
        }

        private void RunCvdBarLogicInner()
        {
            if (!CfEnabled) return;

            double cvdNow = cfCumulativeDelta;
            cfHCvd.Add(cvdNow);
            while (cfHCvd.Count > CfLookback) cfHCvd.RemoveAt(0);

            int n = cfHCvd.Count;
            double slope = 0, r2 = 0;
            if (n >= 2)
            {
                double sx = 0, sy = 0;
                for (int i = 0; i < n; i++) { sx += i; sy += cfHCvd[i]; }
                double mx = sx / n, my = sy / n;
                double vx = 0, cov = 0;
                for (int i = 0; i < n; i++)
                {
                    double dx = i - mx, dy = cfHCvd[i] - my;
                    vx += dx * dx; cov += dx * dy;
                }
                slope = vx > 0 ? cov / vx : 0;
                double intercept = my - slope * mx;

                double ssTot = 0, ssRes = 0;
                for (int i = 0; i < n; i++)
                {
                    double resid = cfHCvd[i] - (intercept + slope * i);
                    ssRes += resid * resid;
                    double diffFromMean = cfHCvd[i] - my;
                    ssTot += diffFromMean * diffFromMean;
                }
                r2 = ssTot > 0 ? 1.0 - (ssRes / ssTot) : 0;
            }

            int divergenceState = 0;
            if (n >= CfLookback && r2 >= CfMinR2)
            {
                cfGateOpenBars++;
                int lookBarsAgo = Math.Min(CfLookback - 1, CurrentBar);
                double priceChange = Close[0] - Close[lookBarsAgo];
                int priceDir = Math.Sign(priceChange);
                int slopeDir = Math.Sign(slope);
                // price up while CVD's fitted trend is flat/down -> bearish; price down while CVD's
                // fitted trend is flat/up -> bullish. This is the RAW ongoing state -- it stays true
                // for as long as price and the CVD trend remain on opposite sides, which is often many
                // bars in a row, not just the one bar it started on.
                if (priceDir != 0 && slopeDir != 0 && priceDir != slopeDir)
                {
                    divergenceState = priceDir > 0 ? -1 : 1;
                    cfMismatchBars++;
                }
            }

            // Edge-triggered: only report a divergence on the bar where the raw state actually CHANGES
            // (none -> bullish, none -> bearish, or a direct flip) -- not on every bar it continues to
            // hold. Without this, DrawCvdDivergence() re-draws the same triangle on every single bar of
            // a long-running mismatch instead of marking the one bar it began, which is what made the
            // chart look like every bar had a triangle under it.
            int divergence = (divergenceState != cfPrevDivergenceState) ? divergenceState : 0;
            cfPrevDivergenceState = divergenceState;

            cfLastSlope = slope; cfLastR2 = r2; cfLastCvd = cvdNow; cfLastDivergence = divergence;
        }

        // -----------------------------------------------------------------------------------
        // SEQUENCE / EXHAUSTION -- see the field-block comment above for the full rationale. Runs
        // once per primary-series bar close, right after CVD Regression (so cfLastSlope is fresh for
        // this bar) -- under dataLock like the sections above it, since RefreshPanel can read these
        // fields from the depth/tape thread.
        // -----------------------------------------------------------------------------------
        private void RunSequenceBarLogic()
        {
            lock (dataLock) { RunSequenceBarLogicInner(); }
        }

        private void RunSequenceBarLogicInner()
        {
            if (!SqEnabled) { sqDirection = 0; sqCount = 0; sqHRange.Clear(); sqLastExhaustion = 0; sqLeadLagLabel = "—"; return; }
            if (CurrentBar < 1) { sqDirection = 0; sqCount = 0; sqHRange.Clear(); sqLastExhaustion = 0; sqLeadLagLabel = "—"; return; }

            bool higherHighLow = High[0] > High[1] && Low[0] > Low[1];
            bool lowerLowHigh  = Low[0]  < Low[1]  && High[0] < High[1];
            int barDir = higherHighLow ? 1 : (lowerLowHigh ? -1 : 0);

            if (barDir != 0 && barDir == sqDirection)
                sqCount++;
            else if (barDir != 0)
            {
                sqDirection = barDir; sqCount = 1; sqHRange.Clear();
            }
            else
            {
                sqDirection = 0; sqCount = 0; sqHRange.Clear();
            }

            sqLastExhaustion = 0;
            if (sqDirection != 0)
            {
                double range = High[0] - Low[0];
                sqHRange.Add(range);
                while (sqHRange.Count > SqExhaustionBars) sqHRange.RemoveAt(0);

                if (sqCount >= SqExhaustionBars && sqHRange.Count >= 2)
                {
                    double avgPrior = 0;
                    for (int i = 0; i < sqHRange.Count - 1; i++) avgPrior += sqHRange[i];
                    avgPrior /= (sqHRange.Count - 1);
                    if (avgPrior > 0 && range <= avgPrior * SqExhaustionRangeFactor)
                        sqLastExhaustion = sqDirection;
                }

                int slopeDir = Math.Sign(cfLastSlope);
                bool r2Ok = !SqGateLeadLagByR2 || cfLastR2 >= CfMinR2;
                sqLeadLagLabel = (!CfEnabled || slopeDir == 0 || !r2Ok) ? "CVD —" : (slopeDir == sqDirection ? "CVD CONFIRMS" : "CVD DIVERGES");
            }
            else
            {
                sqLeadLagLabel = "—";
            }
        }

        // -----------------------------------------------------------------------------------
        // FAILED AUCTION / TRAPPED TRADERS -- see the field-block comment above for the full
        // rationale. Reads czOrHigh/czOrLow/czPsHigh/czPsLow, all already current for this bar
        // (RunConfluenceZonesBarLogic runs first in the same OnBarUpdate call). Under dataLock like
        // the sections above it, since RefreshPanel can read these fields from the depth/tape thread.
        // -----------------------------------------------------------------------------------
        private void RunFailedAuctionBarLogic()
        {
            lock (dataLock) { RunFailedAuctionBarLogicInner(); }
        }

        private void RunFailedAuctionBarLogicInner()
        {
            faFiredFailedSide = 0; faFiredTrappedSide = 0;
            if (!FaEnabled) return;

            CheckBreakoutLevel("OR HIGH", czOrHigh,  1, czOrLocked);
            CheckBreakoutLevel("OR LOW",  czOrLow,  -1, czOrLocked);
            CheckBreakoutLevel("PS HIGH", czPsHigh,  1, czPsEstablished);
            CheckBreakoutLevel("PS LOW",  czPsLow,  -1, czPsEstablished);

            for (int i = faActiveWatches.Count - 1; i >= 0; i--)
            {
                BreakoutWatch w = faActiveWatches[i];
                bool failed = w.Side > 0 ? Close[0] < w.Price : Close[0] > w.Price;
                if (failed)
                {
                    faFailedCount++;
                    faLastFailedEvent = w.Level + " " + w.Price.ToString("0.00");
                    faFiredFailedSide = -w.Side;
                    faPendingTrapped.Add(new TrappedWatch { Level = w.Level, Price = w.Price, Side = w.Side, FailedBar = CurrentBar });
                    faActiveWatches.RemoveAt(i);
                }
                else if (CurrentBar - w.ArmedBar > FaFailBars)
                {
                    faExpiredCount++;
                    faActiveWatches.RemoveAt(i);   // breakout held -- expire the watch quietly, no event
                }
            }

            double trappedBuffer = FaTrappedTicks * TickSize;
            for (int i = faPendingTrapped.Count - 1; i >= 0; i--)
            {
                TrappedWatch t = faPendingTrapped[i];
                bool trapped = t.Side > 0 ? Close[0] < t.Price - trappedBuffer : Close[0] > t.Price + trappedBuffer;
                if (trapped)
                {
                    faTrappedCount++;
                    faLastTrappedEvent = t.Level + " " + t.Price.ToString("0.00");
                    faFiredTrappedSide = -t.Side;
                    faPendingTrapped.RemoveAt(i);
                }
                else if (CurrentBar - t.FailedBar > FaTrappedBars)
                {
                    faPendingTrapped.RemoveAt(i);
                }
            }
        }

        // Arms a breakout watch the first time price closes across `price` in direction `side` (+1 =
        // closes above, for an upper boundary; -1 = closes below, for a lower boundary) -- faPrevSide
        // tracks which side price closed on last bar so this only arms on a FRESH crossing, not every
        // bar price happens to sit beyond the level. MUST be called with dataLock already held.
        private void CheckBreakoutLevel(string levelName, double price, int side, bool levelActive)
        {
            if (!levelActive || double.IsNaN(price)) { faPrevSide.Remove(levelName); return; }

            int curSide = Close[0] > price ? 1 : (Close[0] < price ? -1 : 0);
            int prevSide;
            faPrevSide.TryGetValue(levelName, out prevSide);

            if (curSide == side && prevSide != side && prevSide != 0)
            {
                faActiveWatches.Add(new BreakoutWatch { Level = levelName, Price = price, Side = side, ArmedBar = CurrentBar });
                faArmedCount++;
            }

            if (curSide != 0) faPrevSide[levelName] = curSide;
        }

        // -----------------------------------------------------------------------------------
        // 3h) FOOTPRINT GRID bar logic -- drains whatever bars OnMarketData has finished bucketing
        // (fpPendingFinalized, written from the tape thread on rollover) into fpDrawQueue for
        // DrawFootprintGrid (called right after this from DrawChartLines, same OnBarUpdate call) to
        // actually draw -- same producer/consumer split as Stat Walls/CVD Regression's bar logic.
        // Only ever called from OnBarUpdate.
        // -----------------------------------------------------------------------------------
        private void RunFootprintBarLogic()
        {
            fpDrawQueue = null;
            if (!FpEnabled) return;

            // Drain whatever OnMarketData has finished bucketing since the last time we checked --
            // see fpPendingFinalized's field comment for why this replaced an earlier CurrentBar-
            // equality check that never actually matched.
            lock (dataLock)
            {
                if (fpPendingFinalized.Count > 0)
                {
                    fpDrawQueue = new List<KeyValuePair<int, Dictionary<double, FootprintCell>>>(fpPendingFinalized);
                    fpPendingFinalized.Clear();
                }
            }
        }

        // -----------------------------------------------------------------------------------
        // CONFLUENCE ZONES bar logic (unchanged formulas from SneakyConfluenceZones_v1_0_0)
        // -----------------------------------------------------------------------------------
        // Fields this method writes are also read from RefreshPanel, which can run on the depth/tape
        // thread -- the whole method runs under dataLock (cheap: this only runs once per bar close).
        private void RunConfluenceZonesBarLogic(bool newSession)
        {
            lock (dataLock) { RunConfluenceZonesBarLogicInner(newSession); }
        }

        private void RunConfluenceZonesBarLogicInner(bool newSession)
        {
            if (newSession)
            {
                if (czSessionStarted && czProfileVolume.Count > 0)
                {
                    czPsHigh  = czCurHigh;
                    czPsLow   = czCurLow;
                    czPsClose = Close[1];
                    czPsVwap  = czSessionVwapVolSum > 0 ? czSessionVwapPriceVolSum / czSessionVwapVolSum : double.NaN;

                    double poc, vah, val;
                    ComputeProfileLevels(out poc, out vah, out val);
                    czPsPoc = poc; czPsVah = vah; czPsVal = val;
                    czPsEstablished = true;
                }

                czProfileVolume.Clear();
                czSessionVwapPriceVolSum = 0;
                czSessionVwapVolSum      = 0;
                czSessionStartTime = Time[0];
                czSessionStarted   = true;
                czCurHigh  = High[0];
                czCurLow   = Low[0];
                czOrHigh   = High[0];
                czOrLow    = Low[0];
                czOrLocked = false;
            }
            else
            {
                czCurHigh = Math.Max(czCurHigh, High[0]);
                czCurLow  = Math.Min(czCurLow, Low[0]);
            }

            double typicalPrice = (High[0] + Low[0] + Close[0]) / 3.0;
            double bucketSize   = CzProfileBucketTicks * TickSize;
            double bucket       = Math.Round(typicalPrice / bucketSize) * bucketSize;
            double vol          = Volume[0];

            double existingVol;
            czProfileVolume[bucket] = (czProfileVolume.TryGetValue(bucket, out existingVol) ? existingVol : 0) + vol;

            czSessionVwapPriceVolSum += typicalPrice * vol;
            czSessionVwapVolSum      += vol;

            if (!czOrLocked)
            {
                czOrHigh = Math.Max(czOrHigh, High[0]);
                czOrLow  = Math.Min(czOrLow, Low[0]);
                if ((Time[0] - czSessionStartTime).TotalMinutes >= CzOpeningRangeMinutes)
                    czOrLocked = true;
            }

            double runningSessionVwap = czSessionVwapVolSum > 0 ? czSessionVwapPriceVolSum / czSessionVwapVolSum : double.NaN;
            czLastZones = BuildConfluenceZones(runningSessionVwap);
            czLastClose = Close[0];
        }

        private void ComputeProfileLevels(out double poc, out double vah, out double val)
        {
            poc = double.NaN; vah = double.NaN; val = double.NaN;
            if (czProfileVolume.Count == 0) return;

            double totalVolume = 0;
            foreach (double v in czProfileVolume.Values) totalVolume += v;
            if (totalVolume <= 0) return;

            double pocPrice = double.NaN;
            double pocVol   = -1;
            foreach (KeyValuePair<double, double> kv in czProfileVolume)
                if (kv.Value > pocVol) { pocVol = kv.Value; pocPrice = kv.Key; }

            List<double> buckets = new List<double>(czProfileVolume.Keys);
            buckets.Sort();
            int pocIndex = buckets.IndexOf(pocPrice);

            int lowIdx = pocIndex, highIdx = pocIndex;
            double covered = czProfileVolume[pocPrice];
            double target  = totalVolume * (CzValueAreaPercent / 100.0);

            while (covered < target && (lowIdx > 0 || highIdx < buckets.Count - 1))
            {
                double volBelow = lowIdx > 0 ? czProfileVolume[buckets[lowIdx - 1]] : -1;
                double volAbove = highIdx < buckets.Count - 1 ? czProfileVolume[buckets[highIdx + 1]] : -1;

                if (highIdx < buckets.Count - 1 && volAbove >= volBelow)
                {
                    highIdx++;
                    covered += czProfileVolume[buckets[highIdx]];
                }
                else if (lowIdx > 0)
                {
                    lowIdx--;
                    covered += czProfileVolume[buckets[lowIdx]];
                }
                else break;
            }

            poc = pocPrice;
            val = buckets[lowIdx];
            vah = buckets[highIdx];
        }

        private List<Zone> BuildConfluenceZones(double runningSessionVwap)
        {
            List<LevelRef> levels = new List<LevelRef>();
            if (czPsEstablished)
            {
                levels.Add(new LevelRef("PS HIGH", czPsHigh));
                levels.Add(new LevelRef("PS LOW", czPsLow));
                levels.Add(new LevelRef("PS CLOSE", czPsClose));
                if (!double.IsNaN(czPsVwap)) levels.Add(new LevelRef("PS VWAP", czPsVwap));
                if (!double.IsNaN(czPsPoc))  levels.Add(new LevelRef("PS POC", czPsPoc));
                if (!double.IsNaN(czPsVah))  levels.Add(new LevelRef("PS VAH", czPsVah));
                if (!double.IsNaN(czPsVal))  levels.Add(new LevelRef("PS VAL", czPsVal));
            }
            if (!double.IsNaN(runningSessionVwap))
                levels.Add(new LevelRef("SESSION VWAP", runningSessionVwap));
            if (czOrLocked)
            {
                levels.Add(new LevelRef("OR HIGH", czOrHigh));
                levels.Add(new LevelRef("OR LOW", czOrLow));
            }

            levels.Sort((a, b) => a.Price.CompareTo(b.Price));

            double tolerance = CzZoneToleranceTicks * TickSize;
            List<Zone> zones = new List<Zone>();
            Zone current = null;
            foreach (LevelRef lvl in levels)
            {
                if (current != null && (lvl.Price - current.PriceHigh) <= tolerance)
                {
                    current.PriceHigh = Math.Max(current.PriceHigh, lvl.Price);
                    current.PriceLow  = Math.Min(current.PriceLow, lvl.Price);
                    current.Labels.Add(lvl.Label);
                }
                else
                {
                    current = new Zone { PriceLow = lvl.Price, PriceHigh = lvl.Price };
                    current.Labels.Add(lvl.Label);
                    zones.Add(current);
                }
            }

            zones.RemoveAll(z => z.Labels.Count < 2);
            zones.Sort((a, b) => b.Labels.Count.CompareTo(a.Labels.Count));
            if (zones.Count > CzMaxZonesShown) zones.RemoveRange(CzMaxZonesShown, zones.Count - CzMaxZonesShown);
            return zones;
        }

        // ===================================================================================
        // OnMarketDepth -- LIQUIDITY / WALLS book maintenance (unchanged from SneakyLiquidityZones)
        // ===================================================================================
        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            try
            {
                lock (dataLock)
                {
                    MaintainBook(e);
                    lzHasReceivedDepth = true;
                    RecomputeLiquiditySnapshot();
                }

                DateTime now = DateTime.Now;
                if ((now - lzLastPanelUpdate).TotalMilliseconds < PanelRefreshMs) return;
                lzLastPanelUpdate = now;
                RefreshPanel();
            }
            catch (Exception ex) { Print("SneakyOrderFlowDashboard OnMarketDepth error: " + ex.Message); }
        }

        private void MaintainBook(MarketDepthEventArgs e)
        {
            List<DepthRow> rows = (e.MarketDataType == MarketDataType.Ask) ? lzAskRows : lzBidRows;
            bool isBid = e.MarketDataType != MarketDataType.Ask;

            // Stack/Pull only watches the best bid/ask (position 0) -- see the field-block comment
            // above. spDelta captures the size CHANGE this single event represents at that level;
            // recorded below, after the book itself is updated, so RecordStackPullDelta never has to
            // know about Add/Update/Remove semantics itself.
            bool spTrackThis = SpEnabled && e.Position == 0;
            long spDelta = 0;

            if (e.Operation == Operation.Add)
            {
                int insertAt = Math.Max(0, Math.Min(e.Position, rows.Count));
                rows.Insert(insertAt, new DepthRow { Price = e.Price, Volume = e.Volume });
                if (spTrackThis) spDelta = e.Volume;
            }
            else if (e.Operation == Operation.Update)
            {
                if (e.Position >= 0 && e.Position < rows.Count)
                {
                    if (spTrackThis) spDelta = e.Volume - rows[e.Position].Volume;
                    rows[e.Position].Price  = e.Price;
                    rows[e.Position].Volume = e.Volume;
                }
                else
                {
                    int insertAt = Math.Max(0, Math.Min(e.Position, rows.Count));
                    rows.Insert(insertAt, new DepthRow { Price = e.Price, Volume = e.Volume });
                    if (spTrackThis) spDelta = e.Volume;
                }
            }
            else if (e.Operation == Operation.Remove)
            {
                if (e.Position >= 0 && e.Position < rows.Count)
                {
                    if (spTrackThis) spDelta = -rows[e.Position].Volume;
                    rows.RemoveAt(e.Position);
                }
            }

            if (spTrackThis && spDelta != 0) RecordStackPullDelta(isBid, spDelta, DateTime.Now);
        }

        // Appends this event's top-of-book size delta into the rolling per-side window and keeps a
        // running net sum, trimming entries older than SpWindowSeconds -- same rolling-window shape
        // as Aggression Split's TrimRollingWindow. MUST be called with dataLock already held (from
        // OnMarketDepth, via MaintainBook).
        private void RecordStackPullDelta(bool isBid, long delta, DateTime now)
        {
            Queue<KeyValuePair<DateTime, long>> q = isBid ? spBidDeltas : spAskDeltas;
            q.Enqueue(new KeyValuePair<DateTime, long>(now, delta));
            if (isBid) spBidNetFlow += delta; else spAskNetFlow += delta;

            while (q.Count > 0 && (now - q.Peek().Key).TotalSeconds > SpWindowSeconds)
            {
                KeyValuePair<DateTime, long> old = q.Dequeue();
                if (isBid) spBidNetFlow -= old.Value; else spAskNetFlow -= old.Value;
            }
        }

        // Recomputes and CACHES the wall/cluster snapshot so OnBarUpdate can draw chart lines from
        // it without touching the depth thread directly. MUST be called with dataLock already held.
        private void RecomputeLiquiditySnapshot()
        {
            int bidLevels = Math.Min(LzLevelsAnalyzed, lzBidRows.Count);
            int askLevels = Math.Min(LzLevelsAnalyzed, lzAskRows.Count);

            List<WallHit> walls = new List<WallHit>();
            CollectWalls(lzBidRows, "BID", bidLevels, walls);
            CollectWalls(lzAskRows, "ASK", askLevels, walls);
            walls.Sort((a, b) => b.Volume.CompareTo(a.Volume));
            if (walls.Count > LzMaxWallsShown) walls.RemoveRange(LzMaxWallsShown, walls.Count - LzMaxWallsShown);

            List<ClusterHit> clusters = new List<ClusterHit>();
            CollectClusters(lzBidRows, "BID", bidLevels, clusters);
            CollectClusters(lzAskRows, "ASK", askLevels, clusters);
            clusters.Sort((a, b) => b.TotalVolume.CompareTo(a.TotalVolume));
            if (clusters.Count > LzMaxClustersShown) clusters.RemoveRange(LzMaxClustersShown, clusters.Count - LzMaxClustersShown);

            lzLastWalls = walls;
            lzLastClusters = clusters;
        }

        private void CollectWalls(List<DepthRow> rows, string side, int levels, List<WallHit> into)
        {
            for (int i = 0; i < levels; i++)
                if (rows[i].Volume >= LzWallSizeThreshold)
                    into.Add(new WallHit { Side = side, Price = rows[i].Price, Volume = rows[i].Volume });
        }

        private void CollectClusters(List<DepthRow> rows, string side, int levels, List<ClusterHit> into)
        {
            double tolerance = LzZoneToleranceTicks * TickSize;
            int i = 0;
            while (i < levels)
            {
                double lowPrice = rows[i].Price, highPrice = rows[i].Price;
                long total = rows[i].Volume;
                int count = 1;
                int j = i + 1;
                while (j < levels && Math.Abs(rows[j].Price - rows[j - 1].Price) <= tolerance)
                {
                    lowPrice  = Math.Min(lowPrice, rows[j].Price);
                    highPrice = Math.Max(highPrice, rows[j].Price);
                    total    += rows[j].Volume;
                    count++;
                    j++;
                }

                if (count >= 2 && total >= LzClusterSizeThreshold)
                    into.Add(new ClusterHit { Side = side, PriceLow = lowPrice, PriceHigh = highPrice, TotalVolume = total, LevelCount = count });

                i = j;
            }
        }

        // ===================================================================================
        // OnMarketData -- AGGRESSION SPLIT + FLOW EVENTS (unchanged formulas, now sharing one
        // trade classification instead of two duplicated copies)
        // ===================================================================================
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            try
            {
                DateTime now = DateTime.Now;

                if (e.MarketDataType == MarketDataType.Bid)
                {
                    lock (dataLock) { UpdateTouch(true, e.Price, now); }
                }
                else if (e.MarketDataType == MarketDataType.Ask)
                {
                    lock (dataLock) { UpdateTouch(false, e.Price, now); }
                }
                else if (e.MarketDataType == MarketDataType.Last)
                {
                    double price = e.Price;
                    long   vol   = e.Volume;

                    lock (dataLock)
                    {
                        string side = ClassifyTrade(price, e.Bid, e.Ask);
                        lastTradePrice = price;
                        lastTradeSide  = side;

                        // -- Aggression Split bookkeeping --
                        if (side == "BUY") asSessionBuyVolume += vol; else asSessionSellVolume += vol;

                        // -- CVD Regression bookkeeping -- same classified print, no second classifier.
                        if (CfEnabled)
                        {
                            cfCumulativeDelta += (side == "BUY" ? vol : -vol);
                            cfHasClassifiedTrade = true;
                        }

                        // -- Footprint Grid bookkeeping -- same classified print as CVD above, just
                        // bucketed by (bar, tick-rounded price) instead of summed into one running
                        // number. See the 3h field-block comment for the full producer/consumer story.
                        if (FpEnabled)
                        {
                            if (CurrentBar != fpCurrentBarIndex)
                            {
                                // Roll over: the bar we were bucketing (fpCurrentBarIndex) is done --
                                // push it to the pending queue for RunFootprintBarLogic to pick up,
                                // rather than requiring OnBarUpdate to independently agree this is the
                                // same bar (see fpPendingFinalized's field comment for why the old
                                // CurrentBar-equality check never actually worked).
                                if (fpCurrentBarIndex >= 0 && fpCurrentBar.Count > 0)
                                    fpPendingFinalized.Add(new KeyValuePair<int, Dictionary<double, FootprintCell>>(fpCurrentBarIndex, fpCurrentBar));
                                fpCurrentBar = new Dictionary<double, FootprintCell>();
                                fpCurrentBarIndex = CurrentBar;
                            }
                            double fpPrice = Math.Round(price / TickSize) * TickSize;
                            FootprintCell fpCell;
                            if (!fpCurrentBar.TryGetValue(fpPrice, out fpCell))
                            {
                                fpCell = new FootprintCell();
                                fpCurrentBar[fpPrice] = fpCell;
                            }
                            if (side == "BUY") fpCell.AskVol += vol; else fpCell.BidVol += vol;
                            fpHasClassifiedTrade = true;
                        }

                        asRollingTrades.Enqueue(new FlowEvent { Time = now, Side = side, Volume = vol });
                        if (side == "BUY") asRollingBuyVolume += vol; else asRollingSellVolume += vol;
                        TrimRollingWindow(now);
                        if (vol >= AsLargePrintThreshold && vol > asLargestPrintVolume)
                        {
                            asLargestPrintVolume = vol;
                            asLargestPrintPrice  = price;
                            asLargestPrintSide   = side;
                        }
                        asHasReceivedTrades = true;

                        // -- Flow Events bookkeeping --
                        double tolerance = TickSize / 2.0;
                        if (side == "BUY" && !double.IsNaN(feAskTouchPrice) && Math.Abs(price - feAskTouchPrice) <= tolerance)
                            feAskTouchVolume += vol;
                        else if (side == "SELL" && !double.IsNaN(feBidTouchPrice) && Math.Abs(price - feBidTouchPrice) <= tolerance)
                            feBidTouchVolume += vol;
                        CheckAbsorption(now);
                    }

                    if ((now - asLastPanelUpdate).TotalMilliseconds >= PanelRefreshMs)
                    {
                        asLastPanelUpdate = now;
                        RefreshPanel();
                    }
                }
            }
            catch (Exception ex) { Print("SneakyOrderFlowDashboard OnMarketData error: " + ex.Message); }
        }

        // Quote-rule + tick-rule-fallback trade classification (Lee & Ready, 1991). MUST be called
        // with dataLock already held.
        private string ClassifyTrade(double price, double bid, double ask)
        {
            if (ask > 0 && price >= ask) return "BUY";
            if (bid > 0 && price <= bid) return "SELL";
            if (!double.IsNaN(lastTradePrice))
            {
                if (price > lastTradePrice) return "BUY";
                if (price < lastTradePrice) return "SELL";
                if (lastTradeSide != null) return lastTradeSide;
            }
            return "BUY";
        }

        // Drops rolling-window entries older than AsRollingWindowSeconds using wall-clock time. MUST
        // be called with dataLock already held.
        private void TrimRollingWindow(DateTime asOf)
        {
            while (asRollingTrades.Count > 0 && (asOf - asRollingTrades.Peek().Time).TotalSeconds > AsRollingWindowSeconds)
            {
                FlowEvent old = asRollingTrades.Dequeue();
                if (old.Side == "BUY") asRollingBuyVolume -= old.Volume; else asRollingSellVolume -= old.Volume;
            }
        }

        // Updates one side's touch price; if it changed, checks whether the level being left behind
        // qualifies as a SWEEP. MUST be called with dataLock already held.
        private void UpdateTouch(bool isBid, double price, DateTime now)
        {
            double tolerance = TickSize / 2.0;

            if (isBid)
            {
                bool changed = double.IsNaN(feBidTouchPrice) || Math.Abs(price - feBidTouchPrice) > tolerance;
                if (changed)
                {
                    if (!double.IsNaN(feBidTouchPrice))
                    {
                        double elapsed = (now - feBidTouchStart).TotalSeconds;
                        if (feBidTouchVolume >= FeSweepVolumeThreshold && elapsed <= FeSweepWindowSeconds)
                            RecordFlowEvent("BID", "SWEEP", feBidTouchPrice, feBidTouchVolume, now);
                    }
                    feBidTouchPrice = price;
                    feBidTouchStart = now;
                    feBidTouchVolume = 0;
                    feBidAbsorptionFired = false;
                }
            }
            else
            {
                bool changed = double.IsNaN(feAskTouchPrice) || Math.Abs(price - feAskTouchPrice) > tolerance;
                if (changed)
                {
                    if (!double.IsNaN(feAskTouchPrice))
                    {
                        double elapsed = (now - feAskTouchStart).TotalSeconds;
                        if (feAskTouchVolume >= FeSweepVolumeThreshold && elapsed <= FeSweepWindowSeconds)
                            RecordFlowEvent("ASK", "SWEEP", feAskTouchPrice, feAskTouchVolume, now);
                    }
                    feAskTouchPrice = price;
                    feAskTouchStart = now;
                    feAskTouchVolume = 0;
                    feAskAbsorptionFired = false;
                }
            }
        }

        private void CheckAbsorption(DateTime now)
        {
            if (!feBidAbsorptionFired && !double.IsNaN(feBidTouchPrice)
                && feBidTouchVolume >= FeAbsorptionVolumeThreshold
                && (now - feBidTouchStart).TotalSeconds <= FeAbsorptionWindowSeconds)
            {
                RecordFlowEvent("BID", "ABSORPTION", feBidTouchPrice, feBidTouchVolume, now);
                feBidAbsorptionFired = true;
            }

            if (!feAskAbsorptionFired && !double.IsNaN(feAskTouchPrice)
                && feAskTouchVolume >= FeAbsorptionVolumeThreshold
                && (now - feAskTouchStart).TotalSeconds <= FeAbsorptionWindowSeconds)
            {
                RecordFlowEvent("ASK", "ABSORPTION", feAskTouchPrice, feAskTouchVolume, now);
                feAskAbsorptionFired = true;
            }
        }

        private void RecordFlowEvent(string side, string type, double price, long volume, DateTime time)
        {
            feRecentEvents.Insert(0, new FlowEvent { Time = time, Side = side, Type = type, Price = price, Volume = volume });
            if (feRecentEvents.Count > FeMaxEventsShown)
                feRecentEvents.RemoveRange(FeMaxEventsShown, feRecentEvents.Count - FeMaxEventsShown);
        }

        // ===================================================================================
        // CHART LINES -- new in this file. Confluence Zones' top zones + Liquidity Zones' top
        // walls/clusters, each drawn as a horizontal line across the chart with a text label
        // anchored at the current bar (right side). Only ever called from OnBarUpdate (the safe,
        // single, non-depth/non-tape thread) -- see file header.
        // ===================================================================================
        // Draws (or removes, when the level isn't active/available) one horizontal line for a
        // single named Confluence Zones level, and appends its label to `labels` (rendered later,
        // screen-pinned, by OnRender -- see below). Only ever called from DrawChartLines(), which
        // only ever runs from OnBarUpdate — same thread that writes the czPs*/czOr*/czSessionVwap*
        // fields, so reading them directly here (no lock) is safe, same as the rest of this method.
        //
        // NOTE: labels used to be drawn with Draw.Text anchored at barsAgo=0 (the current bar). That
        // meant the label scrolled off-screen with the bars the instant the user panned the chart
        // away from the right edge -- exactly the "why don't the labels stay on the right" feedback.
        // RemoveDrawObject(textTag) below cleans up any such leftover Draw.Text object from before
        // this change; new labels are never drawn via Draw.Text at all.
        private void DrawOrRemoveLevel(string tagBase, bool active, double price, string label, Brush brush, List<ChartLabel> labels)
        {
            string lineTag = tagBase + "Line";
            string textTag = tagBase + "Text";
            RemoveDrawObject(textTag);
            if (active && !double.IsNaN(price))
            {
                Draw.HorizontalLine(this, lineTag, price, brush);
                labels.Add(new ChartLabel { Price = price, Text = " " + price.ToString("0.00") + " " + label, Brush = brush });
            }
            else
            {
                RemoveDrawObject(lineTag);
            }
        }

        // Draws each individual named Confluence Zones level as its own labeled chart line, in
        // addition to the clustered "zone" lines below. This is what was missing before: PS POC,
        // PS VAH/VAL, PS High/Low/Close, PS VWAP, running Session VWAP, and Opening Range High/Low
        // were only ever shown in the panel text — they never got a chart line unless they happened
        // to cluster with another level into a "zone."
        private void DrawConfluenceLevelLines(List<ChartLabel> labels)
        {
            double runningSessionVwap = czSessionVwapVolSum > 0 ? czSessionVwapPriceVolSum / czSessionVwapVolSum : double.NaN;

            DrawOrRemoveLevel("CzPsHigh",  czPsEstablished, czPsHigh,  "PS HIGH",  Brushes.DodgerBlue, labels);
            DrawOrRemoveLevel("CzPsLow",   czPsEstablished, czPsLow,   "PS LOW",   Brushes.DodgerBlue, labels);
            DrawOrRemoveLevel("CzPsClose", czPsEstablished, czPsClose, "PS CLOSE", Brushes.DodgerBlue, labels);
            DrawOrRemoveLevel("CzPsVwap",  czPsEstablished && !double.IsNaN(czPsVwap), czPsVwap, "PS VWAP", Brushes.Turquoise, labels);
            DrawOrRemoveLevel("CzPsPoc",   czPsEstablished && !double.IsNaN(czPsPoc),  czPsPoc,  "PS POC",  Brushes.Gold, labels);
            DrawOrRemoveLevel("CzPsVah",   czPsEstablished && !double.IsNaN(czPsVah),  czPsVah,  "PS VAH",  Brushes.Goldenrod, labels);
            DrawOrRemoveLevel("CzPsVal",   czPsEstablished && !double.IsNaN(czPsVal),  czPsVal,  "PS VAL",  Brushes.Goldenrod, labels);
            DrawOrRemoveLevel("CzSessVwap", !double.IsNaN(runningSessionVwap), runningSessionVwap, "SESSION VWAP", Brushes.Turquoise, labels);
            DrawOrRemoveLevel("CzOrHigh",  czOrLocked, czOrHigh, "OR HIGH", Brushes.MediumPurple, labels);
            DrawOrRemoveLevel("CzOrLow",   czOrLocked, czOrLow,  "OR LOW",  Brushes.MediumPurple, labels);
        }

        private void DrawChartLines()
        {
            List<ChartLabel> labels = new List<ChartLabel>();

            if (ShowConfluenceLines)
            {
                DrawConfluenceLevelLines(labels);

                for (int i = 0; i < CzMaxZonesShown; i++)
                {
                    string lineTag = "CzZoneLine" + i;
                    string textTag = "CzZoneText" + i;
                    RemoveDrawObject(textTag);
                    if (i < czLastZones.Count)
                    {
                        Zone z = czLastZones[i];
                        string strength = z.Labels.Count >= 4 ? "HIGH" : (z.Labels.Count == 3 ? "MED" : "LOW");
                        Brush brush = z.Labels.Count >= 4 ? Brushes.Yellow : (z.Labels.Count == 3 ? Brushes.Orange : Brushes.Gray);
                        Draw.HorizontalLine(this, lineTag, z.MidPrice, brush);
                        labels.Add(new ChartLabel { Price = z.MidPrice, Text = " " + z.MidPrice.ToString("0.00") + " " + strength + "(" + z.Labels.Count + ")", Brush = brush });
                    }
                    else
                    {
                        RemoveDrawObject(lineTag);
                    }
                }
            }

            if (ShowLiquidityLines)
            {
                List<WallHit> walls;
                List<ClusterHit> clusters;
                lock (dataLock)
                {
                    walls = new List<WallHit>(lzLastWalls);
                    clusters = new List<ClusterHit>(lzLastClusters);
                }

                for (int i = 0; i < LzMaxWallsShown; i++)
                {
                    string lineTag = "LzWallLine" + i;
                    string textTag = "LzWallText" + i;
                    RemoveDrawObject(textTag);
                    if (i < walls.Count)
                    {
                        WallHit w = walls[i];
                        Brush brush = w.Side == "BID" ? Brushes.LimeGreen : Brushes.Red;
                        Draw.HorizontalLine(this, lineTag, w.Price, brush);
                        labels.Add(new ChartLabel { Price = w.Price, Text = " " + w.Side + " " + w.Price.ToString("0.00") + " x" + w.Volume, Brush = brush });
                    }
                    else
                    {
                        RemoveDrawObject(lineTag);
                    }
                }

                for (int i = 0; i < LzMaxClustersShown; i++)
                {
                    string lineTag = "LzClusterLine" + i;
                    string textTag = "LzClusterText" + i;
                    RemoveDrawObject(textTag);
                    if (i < clusters.Count)
                    {
                        ClusterHit c = clusters[i];
                        Brush brush = c.Side == "BID" ? Brushes.Cyan : Brushes.Magenta;
                        Draw.HorizontalLine(this, lineTag, c.MidPriceOf(), brush);
                        labels.Add(new ChartLabel { Price = c.MidPriceOf(), Text = " " + c.Side + " CLUSTER " + c.PriceLow.ToString("0.00") + "-" + c.PriceHigh.ToString("0.00") + " x" + c.TotalVolume, Brush = brush });
                    }
                    else
                    {
                        RemoveDrawObject(lineTag);
                    }
                }
            }

            if (ShowStatWallLines) DrawStatWalls(labels);

            if (ShowCvdMarkers) DrawCvdDivergence();

            if (ShowSequenceMarkers) DrawSequenceExhaustion();

            if (ShowFailedAuctionMarkers) DrawFailedAuctionMarkers();

            if (FpEnabled) DrawFootprintGrid();

            lock (dataLock) { chartLabelsSnapshot = labels; }
        }

        // CVD divergence is a POINT-IN-TIME event (price's window-direction disagreed with the CVD
        // regression's fitted trend on this specific bar), not a persisting current level -- so
        // unlike the Confluence/Liquidity/Stat-Wall labels it is drawn as a one-off marker anchored
        // to the bar it fired on (same pattern as SneakyBuySellVolumePressureMountain's own
        // Draw.TriangleUp/Down divergence markers) rather than needing OnRender's screen-pinning.
        // Only ever called from DrawChartLines() (OnBarUpdate thread) -- cfLastDivergence is written
        // by RunCvdBarLogic() on that same thread, immediately before this, in the same OnBarUpdate call.
        // 2026-09-17 -- switched from Draw.TriangleUp/Down (NinjaTrader's built-in chart-marker
        // triangles) to a custom 3-point Draw.Triangle per user request ("make the triangles a lil
        // bigger"). The built-in marker tools render at a fixed pixel size with no exposed size
        // property, so the only way to size them up is to draw the triangle ourselves. Apex stays
        // pinned to (barsAgo 0, this exact signal bar) so timing/meaning is unchanged; the base
        // widens 1 bar to the left (barsAgo 1) since a live/current bar has no "future" bar to widen
        // into. CfDivMarkerHeightTicks controls how tall the triangle is (ticks from tip to base).
        private void DrawCvdDivergence()
        {
            double h = CfDivMarkerHeightTicks * TickSize;
            if (cfLastDivergence > 0)
            {
                double tipY  = Low[0] - 3 * TickSize;
                double baseY = tipY - h;
                Draw.Triangle(this, "CfDivBull" + CurrentBar, false,
                    0, tipY, 0, baseY, 1, baseY,
                    Brushes.LimeGreen, Brushes.LimeGreen, 100);
            }
            else if (cfLastDivergence < 0)
            {
                double tipY  = High[0] + 3 * TickSize;
                double baseY = tipY + h;
                Draw.Triangle(this, "CfDivBear" + CurrentBar, false,
                    0, tipY, 0, baseY, 1, baseY,
                    Brushes.Red, Brushes.Red, 100);
            }
        }

        // SEQUENCE/EXHAUSTION marker -- also a point-in-time event (the sequence's momentum petered
        // out on THIS bar), same reasoning and same one-off Draw.Text pattern as UTBotAlertsNT8's own
        // Buy/Sell labels. Only ever called from DrawChartLines() (OnBarUpdate thread) -- sqLastExhaustion
        // is written by RunSequenceBarLogic() on that same thread, immediately before this.
        private void DrawSequenceExhaustion()
        {
            if (sqLastExhaustion > 0)
                Draw.Text(this, "SqExhBull" + CurrentBar, false, "EXH", 0, Low[0] - 6 * TickSize, 0, Brushes.White,
                    new NinjaTrader.Gui.Tools.SimpleFont("Arial", 9), System.Windows.TextAlignment.Center, Brushes.DeepSkyBlue, Brushes.DeepSkyBlue, 100);
            else if (sqLastExhaustion < 0)
                Draw.Text(this, "SqExhBear" + CurrentBar, false, "EXH", 0, High[0] + 6 * TickSize, 0, Brushes.White,
                    new NinjaTrader.Gui.Tools.SimpleFont("Arial", 9), System.Windows.TextAlignment.Center, Brushes.DarkOrange, Brushes.DarkOrange, 100);
        }

        // FAILED AUCTION / TRAPPED TRADERS markers -- also point-in-time events, same one-off
        // Draw.Text pattern as the markers above. Only ever called from DrawChartLines() (OnBarUpdate
        // thread) -- faFiredFailedSide/faFiredTrappedSide are written by RunFailedAuctionBarLogic()
        // on that same thread, immediately before this.
        private void DrawFailedAuctionMarkers()
        {
            if (faFiredFailedSide > 0)
                Draw.Text(this, "FaFailBull" + CurrentBar, false, "FAILED", 0, Low[0] - 8 * TickSize, 0, Brushes.White,
                    new NinjaTrader.Gui.Tools.SimpleFont("Arial", 9), System.Windows.TextAlignment.Center, Brushes.MediumPurple, Brushes.MediumPurple, 100);
            else if (faFiredFailedSide < 0)
                Draw.Text(this, "FaFailBear" + CurrentBar, false, "FAILED", 0, High[0] + 8 * TickSize, 0, Brushes.White,
                    new NinjaTrader.Gui.Tools.SimpleFont("Arial", 9), System.Windows.TextAlignment.Center, Brushes.MediumPurple, Brushes.MediumPurple, 100);

            if (faFiredTrappedSide > 0)
                Draw.Text(this, "FaTrapBull" + CurrentBar, false, "TRAPPED", 0, Low[0] - 10 * TickSize, 0, Brushes.White,
                    new NinjaTrader.Gui.Tools.SimpleFont("Arial", 9), System.Windows.TextAlignment.Center, Brushes.Crimson, Brushes.Crimson, 100);
            else if (faFiredTrappedSide < 0)
                Draw.Text(this, "FaTrapBear" + CurrentBar, false, "TRAPPED", 0, High[0] + 10 * TickSize, 0, Brushes.White,
                    new NinjaTrader.Gui.Tools.SimpleFont("Arial", 9), System.Windows.TextAlignment.Center, Brushes.Crimson, Brushes.Crimson, 100);
        }

        // FOOTPRINT GRID -- draws one Draw.Text per price row for every bar RunFootprintBarLogic just
        // drained from fpPendingFinalized (fpDrawQueue -- almost always exactly one bar, occasionally
        // zero, rarely more than one), tagged uniquely per (bar, price) so each row is issued exactly
        // ONCE and then stays anchored to that specific historical bar as the chart scrolls/zooms --
        // unlike the old, buggy zone/wall labels (see changelog), this never re-issues the same tag
        // against a moving "current bar" reference. Each row is colored by the classic "diagonal
        // imbalance" test: this price's ask volume vs. the bid volume one tick BELOW it (a buy
        // imbalance), or this price's bid volume vs. the ask volume one tick ABOVE it (a sell
        // imbalance) -- both gated by FpImbalanceRatio and requiring a real neighbor print to compare
        // against, so a lone print at an untouched price never falsely paints green/red.
        // Only ever called from DrawChartLines() (OnBarUpdate thread) -- fpDrawQueue is written by
        // RunFootprintBarLogic() on that same thread, immediately before this, in the same OnBarUpdate
        // call; fpBarOrder/fpBarPrices (eviction bookkeeping) are only ever touched from here, also on
        // this thread.
        private void DrawFootprintGrid()
        {
            if (fpDrawQueue == null || fpDrawQueue.Count == 0) return;

            foreach (KeyValuePair<int, Dictionary<double, FootprintCell>> entry in fpDrawQueue)
                DrawOneFootprintBar(entry.Key, entry.Value);
        }

        private void DrawOneFootprintBar(int barIndex, Dictionary<double, FootprintCell> cells)
        {
            int barsAgo = CurrentBar - barIndex;
            if (barsAgo < 0) barsAgo = 0;

            List<double> pricesDrawn = new List<double>();
            foreach (KeyValuePair<double, FootprintCell> kv in cells)
            {
                double price = kv.Key;
                FootprintCell cell = kv.Value;
                if (cell.BidVol + cell.AskVol < FpMinRowVolume) continue;

                FootprintCell up, down;
                cells.TryGetValue(Math.Round((price + TickSize) / TickSize) * TickSize, out up);
                cells.TryGetValue(Math.Round((price - TickSize) / TickSize) * TickSize, out down);
                bool buyImbalance  = down != null && cell.AskVol > 0 && cell.AskVol >= FpImbalanceRatio * Math.Max(1, down.BidVol);
                bool sellImbalance = up   != null && cell.BidVol > 0 && cell.BidVol >= FpImbalanceRatio * Math.Max(1, up.AskVol);
                Brush brush = buyImbalance ? Brushes.LimeGreen : (sellImbalance ? Brushes.OrangeRed : Brushes.Silver);

                string tag = "FpRow" + barIndex + "_" + price.ToString("F6");
                Draw.Text(this, tag, false, cell.BidVol + " x " + cell.AskVol, barsAgo, price, 0, brush,
                    new NinjaTrader.Gui.Tools.SimpleFont("Arial", 9), System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
                pricesDrawn.Add(price);
            }

            // fpBarPrices is only ever touched from this method (OnBarUpdate thread) so it needs no
            // lock; fpBarOrder's Count is also read from RefreshPanel (which can run on the tape
            // thread) for the panel's "N bars" readout, so its mutation is wrapped in dataLock here
            // to stay consistent with that cross-thread read -- same reasoning as every other
            // OnBarUpdate-owned field this file still locks around for RefreshPanel's benefit.
            fpBarPrices[barIndex] = pricesDrawn;
            List<int> evictedBars = new List<int>();
            lock (dataLock)
            {
                fpBarOrder.Add(barIndex);
                while (fpBarOrder.Count > FpMaxBarsShown)
                {
                    evictedBars.Add(fpBarOrder[0]);
                    fpBarOrder.RemoveAt(0);
                }
            }
            foreach (int oldBar in evictedBars)
            {
                List<double> oldPrices;
                if (fpBarPrices.TryGetValue(oldBar, out oldPrices))
                {
                    foreach (double p in oldPrices)
                        RemoveDrawObject("FpRow" + oldBar + "_" + p.ToString("F6"));
                    fpBarPrices.Remove(oldBar);
                }
            }
        }

        // Draws each active Stat Wall as a filled rectangle (ATR-thick, extending right from its
        // spawn bar) plus a pinned label at its near/defended edge, using positional slot tags
        // ("LsWallLineN") exactly like the zone/wall/cluster loops above -- a slot beyond the current
        // wall count gets RemoveDrawObject'd, so a broken/aged-out wall's box disappears on its own.
        // Only ever called from DrawChartLines() (OnBarUpdate thread) -- reading lsWalls here without
        // a lock is safe because RunStatWallBarLogic() (the only writer) runs on that same thread,
        // immediately before this, in the same OnBarUpdate call.
        private void DrawStatWalls(List<ChartLabel> labels)
        {
            for (int i = 0; i < LsMaxWalls; i++)
            {
                string boxTag = "LsWallBox" + i;
                if (i < lsWalls.Count)
                {
                    StatWall w = lsWalls[i];
                    int startAgo = CurrentBar - w.StartBar; if (startAgo < 0) startAgo = 0;
                    Brush brush = w.Side > 0 ? Brushes.DeepPink : Brushes.Aquamarine;
                    Draw.Rectangle(this, boxTag, false, startAgo, w.Bottom, 0, w.Top, brush, brush, 12);
                    string sideTxt = w.Side > 0 ? "STAT RESIST" : "STAT SUPPORT";
                    labels.Add(new ChartLabel { Price = w.Near, Text = " " + w.Near.ToString("0.00") + " " + sideTxt, Brush = brush });
                }
                else
                {
                    RemoveDrawObject(boxTag);
                }
            }
        }

        // ===================================================================================
        // OnRender -- draws every label from chartLabelsSnapshot pinned near the right edge of the
        // chart PANEL (screen/pixel space), at the Y height matching its price (via
        // chartScale.GetYByValue). Unlike Draw.Text (which anchors to a bar index and scrolls off
        // with the bars), this keeps each label visible within the current viewport no matter how
        // far the user pans the chart -- same idea as NinjaTrader's own price-axis tags. A label is
        // skipped only when its price is scrolled outside the panel's visible price range, same as
        // the axis tags. Runs on the chart's UI/render thread -- separate from OnBarUpdate, so the
        // shared label list is read under dataLock like every other cross-thread field in this file.
        // API verified against NinjaTrader's SharpDX custom-rendering docs/forum examples: RenderTarget
        // and ChartPanel are available directly on the indicator base class inside OnRender;
        // chartScale.GetYByValue(price) returns the pixel Y for a price; RenderTarget.DrawText takes a
        // SharpDX.RectangleF layout rect (float-based) plus a SharpDX.Direct2D1 brush.
        // ===================================================================================
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            List<ChartLabel> labels;
            lock (dataLock) { labels = chartLabelsSnapshot; }

            if (labels != null && labels.Count > 0)
            {
                const int textHeight = 18;
                const int textWidth = 170;

                foreach (ChartLabel lbl in labels)
                {
                    float y = chartScale.GetYByValue(lbl.Price);

                    // Fully outside the visible panel vertically -- skip, same as an axis tag would.
                    if (y < ChartPanel.Y - textHeight || y > ChartPanel.Y + ChartPanel.H + textHeight)
                        continue;

                    System.Windows.Media.Color wpfColor = Colors.White;
                    SolidColorBrush wpfSolid = lbl.Brush as SolidColorBrush;
                    if (wpfSolid != null) wpfColor = wpfSolid.Color;
                    SharpDX.Color dxColor = new SharpDX.Color(wpfColor.R, wpfColor.G, wpfColor.B, wpfColor.A);

                    float x = ChartPanel.X + ChartPanel.W - textWidth - 4;
                    SharpDX.RectangleF box = new SharpDX.RectangleF(x, y - (textHeight / 2f), textWidth, textHeight);

                    using (SharpDX.DirectWrite.TextFormat textFormat = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial", 12f))
                    using (SharpDX.Direct2D1.SolidColorBrush dxBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, dxColor))
                    {
                        RenderTarget.DrawText(lbl.Text, textFormat, box, dxBrush);
                    }
                }
            }

            base.OnRender(chartControl, chartScale);
        }

        // ===================================================================================
        // PANEL REFRESH -- reads every section's latest snapshot and writes the WPF TextBlocks.
        // Safe to call from any thread (always marshals via ChartControl.Dispatcher).
        // ===================================================================================
        private void RefreshPanel()
        {
            if (!ShowPanel || panelOuter == null) return;

            bool mcReadySnapshot; double trendScore = 0, momentumScore = 0, confluenceScore = 0;
            bool trendUp = false, momentumUp = false, confluenceBull = false, htfBull = false;
            int majorityVotes = 0;
            string mkStateSnap; double mkChopSnap = 0, mkEffSnap = 0, mkPocTSnap = double.NaN;
            int mkFlipSnap = 0; bool mkBalSnap = false; int mkBalBarsSnap = 0;

            bool psEstablishedSnap; double psHighSnap = 0, psLowSnap = 0, psCloseSnap = 0;
            double psVwapSnap = 0, psPocSnap = 0, psVahSnap = 0, psValSnap = 0;
            double orHighSnap = 0, orLowSnap = 0;
            bool orLockedSnap;
            double sessionVwapSnap;
            List<Zone> zonesSnap;
            double closeSnap = double.NaN;

            // ---- All sections snapshot together under one lock -- Market Context/Confluence Zones
            // fields are written under this same lock in OnBarUpdate; Liquidity/Aggression/Flow
            // fields are written under it from the depth/tape threads. See file header.
            bool lzReceivedSnap; double lzBestBid = double.NaN, lzBestAsk = double.NaN; double lzImbalancePct = double.NaN;
            List<WallHit> wallsSnap; List<ClusterHit> clustersSnap;
            bool spEnabledSnap; long spBidNetSnap = 0, spAskNetSnap = 0;
            bool asReceivedSnap; long asSBuy, asSSell, asRBuy, asRSell; long asPrintVol; double asPrintPrice = double.NaN; string asPrintSide = null;
            bool feReceivedSnap; double feBidP, feAskP; long feBidV, feAskV; double feBidElapsed, feAskElapsed; List<FlowEvent> feEventsSnap;
            List<StatWall> lsWallsSnap; double lsZSnap; int lsAbsorbSideSnap; bool lsAbsorbedSnap; int currentBarSnap;
            long lrHoldSnap, lrBreakSnap; string lrLastEventSnap;
            bool cfHasTradesSnap; double cfCvdSnap = 0, cfSlopeSnap = 0, cfR2Snap = 0; int cfDivergenceSnap = 0;
            long cfGateOpenSnap = 0, cfMismatchSnap = 0;
            List<double> cfSparkSnap;
            bool sqEnabledSnap; int sqDirSnap = 0, sqCountSnap = 0, sqExhSnap = 0; string sqLeadLagSnap = "—";
            bool faEnabledSnap; long faFailedSnap = 0, faTrappedSnap = 0; string faLastFailedSnap = "—", faLastTrappedSnap = "—";
            long faArmedSnap = 0, faExpiredSnap = 0;
            bool fpHasTradesSnap; int fpBarsShownSnap = 0;

            lock (dataLock)
            {
                mcReadySnapshot = mcReady;
                trendScore = mcTrendScore; momentumScore = mcMomentumScore; confluenceScore = mcConfluenceScore;
                trendUp = mcTrendUp; momentumUp = mcMomentumUp; confluenceBull = mcConfluenceBull; htfBull = mcHtfBull;
                majorityVotes = mcMajorityVotes;

                mkStateSnap = mcMarketStateLabel; mkChopSnap = mcChoppiness; mkEffSnap = mcEfficiency;
                mkPocTSnap = mcPocT; mkFlipSnap = mcPocFlipCount; mkBalSnap = mcBalanced; mkBalBarsSnap = mcBalanceBarCount;

                psEstablishedSnap = czPsEstablished;
                psHighSnap = czPsHigh; psLowSnap = czPsLow; psCloseSnap = czPsClose;
                psVwapSnap = czPsVwap; psPocSnap = czPsPoc; psVahSnap = czPsVah; psValSnap = czPsVal;
                orHighSnap = czOrHigh; orLowSnap = czOrLow;
                orLockedSnap = czOrLocked;
                sessionVwapSnap = czSessionVwapVolSum > 0 ? czSessionVwapPriceVolSum / czSessionVwapVolSum : double.NaN;
                zonesSnap = czLastZones;
                closeSnap = czLastClose;

                lzReceivedSnap = lzHasReceivedDepth;
                if (lzBidRows.Count > 0) lzBestBid = lzBidRows[0].Price;
                if (lzAskRows.Count > 0) lzBestAsk = lzAskRows[0].Price;
                int bidLevels = Math.Min(LzLevelsAnalyzed, lzBidRows.Count);
                int askLevels = Math.Min(LzLevelsAnalyzed, lzAskRows.Count);
                long bidVolSum = 0; for (int i = 0; i < bidLevels; i++) bidVolSum += lzBidRows[i].Volume;
                long askVolSum = 0; for (int i = 0; i < askLevels; i++) askVolSum += lzAskRows[i].Volume;
                long totalVolSum = bidVolSum + askVolSum;
                if (totalVolSum > 0) lzImbalancePct = (bidVolSum / (double)totalVolSum) * 100.0;
                wallsSnap = new List<WallHit>(lzLastWalls);
                clustersSnap = new List<ClusterHit>(lzLastClusters);

                spEnabledSnap = SpEnabled;
                spBidNetSnap = spBidNetFlow; spAskNetSnap = spAskNetFlow;

                asReceivedSnap = asHasReceivedTrades;
                asSBuy = asSessionBuyVolume; asSSell = asSessionSellVolume;
                asRBuy = asRollingBuyVolume; asRSell = asRollingSellVolume;
                asPrintVol = asLargestPrintVolume; asPrintPrice = asLargestPrintPrice; asPrintSide = asLargestPrintSide;

                feReceivedSnap = asHasReceivedTrades; // touch data arrives alongside trade data
                feBidP = feBidTouchPrice; feAskP = feAskTouchPrice;
                feBidV = feBidTouchVolume; feAskV = feAskTouchVolume;
                feBidElapsed = double.IsNaN(feBidP) ? 0 : (DateTime.Now - feBidTouchStart).TotalSeconds;
                feAskElapsed = double.IsNaN(feAskP) ? 0 : (DateTime.Now - feAskTouchStart).TotalSeconds;
                feEventsSnap = new List<FlowEvent>(feRecentEvents);

                lsWallsSnap = new List<StatWall>(lsWalls);
                lsZSnap = lsLastZ; lsAbsorbSideSnap = lsLastAbsorbSide; lsAbsorbedSnap = lsAbsorbedThisBar;
                lrHoldSnap = lrHoldCount; lrBreakSnap = lrBreakCount; lrLastEventSnap = lrLastEvent;
                // CurrentBar is a plain int (atomic to read), but snapshotting it here alongside
                // everything else keeps this method's threading story uniform -- same reasoning as
                // czLastClose above for Close[0].
                currentBarSnap = CurrentBar;

                cfHasTradesSnap = cfHasClassifiedTrade;
                cfCvdSnap = cfCumulativeDelta; cfSlopeSnap = cfLastSlope; cfR2Snap = cfLastR2;
                // Panel text uses the ONGOING raw state (cfPrevDivergenceState), not the edge-triggered
                // cfLastDivergence the chart marker uses -- see that field's comment. DrawCvdDivergence()
                // still reads cfLastDivergence directly and is unaffected by this.
                cfDivergenceSnap = cfPrevDivergenceState;
                cfGateOpenSnap = cfGateOpenBars; cfMismatchSnap = cfMismatchBars;
                int sparkN = Math.Min(40, cfHCvd.Count);
                cfSparkSnap = sparkN > 0 ? cfHCvd.GetRange(cfHCvd.Count - sparkN, sparkN) : new List<double>();

                sqEnabledSnap = SqEnabled;
                sqDirSnap = sqDirection; sqCountSnap = sqCount; sqExhSnap = sqLastExhaustion; sqLeadLagSnap = sqLeadLagLabel;

                faEnabledSnap = FaEnabled;
                faFailedSnap = faFailedCount; faTrappedSnap = faTrappedCount;
                faLastFailedSnap = faLastFailedEvent; faLastTrappedSnap = faLastTrappedEvent;
                faArmedSnap = faArmedCount; faExpiredSnap = faExpiredCount;

                fpHasTradesSnap = fpHasClassifiedTrade;
                fpBarsShownSnap = fpBarOrder.Count;
            }

            // ---- Format all strings up front, dispatch once ----
            string mcControlLine, mcHtfLine, mcTrendLine, mcMomentumLine, mcConfluenceLine;
            if (mcReadySnapshot)
            {
                string controlLabel = confluenceScore < McContestedThreshold
                    ? "CONTESTED"
                    : (confluenceBull ? "LONG" : "SHORT") + "  •  " + confluenceScore.ToString("0") + "/100";
                mcControlLine = "CONTROL: " + controlLabel;
                mcHtfLine = McHigherTimeframeMinutes + "M TREND: " + (htfBull ? "BULLISH ↑" : "BEARISH ↓");
                mcTrendLine = "TREND  " + trendScore.ToString("0") + "  " + (trendUp ? "↑" : "↓");
                mcMomentumLine = "MOMENTUM  " + momentumScore.ToString("0") + "  " + (momentumUp ? "↑" : "↓");
                mcConfluenceLine = "CONFLUENCE  " + confluenceScore.ToString("0") + "  (" + majorityVotes + "/6 " + (confluenceBull ? "bull" : "bear") + ")";
            }
            else
            {
                mcControlLine = "CONTROL: —"; mcHtfLine = "TREND: —"; mcTrendLine = "TREND  —"; mcMomentumLine = "MOMENTUM  —"; mcConfluenceLine = "CONFLUENCE  —";
            }

            string mkStateLine = mkStateSnap == "—"
                ? "MARKET STATE: —"
                : "MARKET STATE: " + mkStateSnap + "  (ADX " + trendScore.ToString("0") + ", CI " + mkChopSnap.ToString("0") + ")";
            string mkEffFlipBalLine;
            if (double.IsNaN(mkPocTSnap))
                mkEffFlipBalLine = "EFF " + mkEffSnap.ToString("0.00") + "  FLIP " + mkFlipSnap + "  POC(t) —";
            else
                mkEffFlipBalLine = "EFF " + mkEffSnap.ToString("0.00") + "  FLIP " + mkFlipSnap + "  POC(t) " + mkPocTSnap.ToString("0.00")
                                 + "   BAL: " + (mkBalSnap ? "BALANCED" : "IMBALANCED") + " (" + mkBalBarsSnap + " bars)";

            string czPsHlcLine = psEstablishedSnap
                ? "PS H/L/C  " + psHighSnap.ToString("0.00") + " / " + psLowSnap.ToString("0.00") + " / " + psCloseSnap.ToString("0.00")
                : "PS H/L/C  — (waiting on first full session)";
            string czPsVwapPocLine = psEstablishedSnap
                ? "PS VWAP  " + psVwapSnap.ToString("0.00") + "   PS POC  " + psPocSnap.ToString("0.00")
                : "PS VWAP / POC  —";
            string czPsValueLine = psEstablishedSnap
                ? "PS VALUE  " + psValSnap.ToString("0.00") + " - " + psVahSnap.ToString("0.00")
                : "PS VALUE (VAH-VAL)  —";
            string czLocationLine = "LOCATION: —";
            if (psEstablishedSnap && !double.IsNaN(psVahSnap) && !double.IsNaN(psValSnap) && !double.IsNaN(closeSnap))
            {
                if (closeSnap > psVahSnap)      czLocationLine = "LOCATION: ABOVE PS VALUE";
                else if (closeSnap < psValSnap) czLocationLine = "LOCATION: BELOW PS VALUE";
                else                             czLocationLine = "LOCATION: INSIDE PS VALUE";
            }
            string czSessionVwapLine = "SESSION VWAP  " + (double.IsNaN(sessionVwapSnap) ? "—" : sessionVwapSnap.ToString("0.00"));
            string czOrLine = "OPEN RANGE  " + orLowSnap.ToString("0.00") + " - " + orHighSnap.ToString("0.00") + (orLockedSnap ? "" : "  (forming)");

            string lzStatusLine = lzReceivedSnap ? "LIVE DEPTH: active" : "LIVE DEPTH: waiting... (needs L2 feed)";

            // BID/ASK: prefer real L2 top-of-book (lzBestBid/Ask, from OnMarketDepth) when it's
            // arrived; otherwise fall back to the plain Level 1 quote (feBidP/feAskP, from
            // OnMarketData -- the SAME feed already confirmed working for Flow Events) so this line
            // isn't stuck on "—" forever just because the feed has no L2 depth. Best bid/ask price is
            // Level 1 information; only the RESTING SIZE at each level (walls/clusters/imbalance
            // below) actually requires Level 2.
            // Per-side fallback: an L2 book can be genuinely one-sided (e.g. a thin micro-contract
            // book with zero resting bid volume in the scanned levels -- consistent with an
            // IMBALANCE reading of 0%/100%) without the whole line needing to blank out. Each side
            // independently prefers its own L2 top-of-book price and falls back to the L1 quote;
            // only a side with neither known renders as "—". SPREAD requires both sides known.
            double lzBidPrice = !double.IsNaN(lzBestBid) ? lzBestBid : feBidP;
            double lzAskPrice = !double.IsNaN(lzBestAsk) ? lzBestAsk : feAskP;
            bool   lzBidKnown = !double.IsNaN(lzBidPrice);
            bool   lzAskKnown = !double.IsNaN(lzAskPrice);
            bool   lzAnyL2    = !double.IsNaN(lzBestBid) || !double.IsNaN(lzBestAsk);
            string lzBidAskLine;
            if (!lzBidKnown && !lzAskKnown)
            {
                lzBidAskLine = "BID / ASK  —";
            }
            else
            {
                string bidStr    = lzBidKnown ? lzBidPrice.ToString("0.00") : "—";
                string askStr    = lzAskKnown ? lzAskPrice.ToString("0.00") : "—";
                string spreadStr = (lzBidKnown && lzAskKnown) ? ((lzAskPrice - lzBidPrice) / TickSize).ToString("0") + "t" : "—";
                lzBidAskLine = "BID " + bidStr + "   ASK " + askStr + "   SPREAD " + spreadStr + (lzAnyL2 ? "" : "  (L1)");
            }

            // IMBALANCE: real book imbalance (resting bid size vs ask size) needs Level 2 -- there is
            // no way to reconstruct RESTING size from Level 1 quotes or trade prints, so when L2
            // hasn't arrived this instead shows a TAPE-based proxy: the rolling aggressor buy/sell
            // split already computed for Aggression Split (asRBuy/asRSell) -- clearly labeled "(flow,
            // no L2)" so it's never confused with a real book-imbalance read.
            string lzImbalanceLine;
            if (!double.IsNaN(lzImbalancePct))
                lzImbalanceLine = "IMBALANCE  " + lzImbalancePct.ToString("0") + "% " + (lzImbalancePct >= 50 ? "BID-HEAVY ↑" : "ASK-HEAVY ↓") + "  (L2 book)";
            else if (asRBuy + asRSell > 0)
            {
                double flowBuyPct = (asRBuy / (double)(asRBuy + asRSell)) * 100.0;
                lzImbalanceLine = "IMBALANCE  " + flowBuyPct.ToString("0") + "% " + (flowBuyPct >= 50 ? "BUY-HEAVY ↑" : "SELL-HEAVY ↓") + "  (flow, no L2)";
            }
            else lzImbalanceLine = "IMBALANCE  —";

            string spLine; Brush spBrush;
            if (!spEnabledSnap) { spLine = "STACK/PULL  disabled"; spBrush = Brushes.Gray; }
            else if (!lzReceivedSnap) { spLine = "STACK/PULL  NO DEPTH"; spBrush = Brushes.Gray; }
            else
            {
                string bidLabel = spBidNetSnap >= SpStackThreshold ? "STACKING" : (spBidNetSnap <= -SpStackThreshold ? "PULLING" : "neutral");
                string askLabel = spAskNetSnap >= SpStackThreshold ? "STACKING" : (spAskNetSnap <= -SpStackThreshold ? "PULLING" : "neutral");
                spLine = "BID " + bidLabel + " " + (spBidNetSnap >= 0 ? "+" : "") + spBidNetSnap
                       + "   ASK " + askLabel + " " + (spAskNetSnap >= 0 ? "+" : "") + spAskNetSnap;
                bool anyStacking = bidLabel == "STACKING" || askLabel == "STACKING";
                bool anyPulling  = bidLabel == "PULLING"  || askLabel == "PULLING";
                spBrush = anyStacking && !anyPulling ? Brushes.LimeGreen : (anyPulling && !anyStacking ? Brushes.OrangeRed : Brushes.White);
            }

            long asSessionTotal = asSBuy + asSSell;
            long asRollingTotal = asRBuy + asRSell;
            double asSessionBuyPct = asSessionTotal > 0 ? (asSBuy / (double)asSessionTotal) * 100.0 : double.NaN;
            double asRollingBuyPct = asRollingTotal > 0 ? (asRBuy / (double)asRollingTotal) * 100.0 : double.NaN;
            string asStatusLine = asReceivedSnap ? "LIVE TRADES: active" : "LIVE TRADES: waiting...";
            string asSessionLine = asSessionTotal > 0
                ? "SESSION  BUY " + asSBuy + "  SELL " + asSSell + "  (" + asSessionBuyPct.ToString("0") + "% BUY)"
                : "SESSION  BUY —  SELL —";
            string asRollingLine = asRollingTotal > 0
                ? "LAST " + AsRollingWindowSeconds + "s  BUY " + asRBuy + "  SELL " + asRSell + "  (" + asRollingBuyPct.ToString("0") + "% BUY)"
                : "LAST " + AsRollingWindowSeconds + "s  BUY —  SELL —";
            string asPressureLine; Brush asPressureBrush;
            if (asRollingTotal == 0) { asPressureLine = "PRESSURE: —"; asPressureBrush = Brushes.Gray; }
            else if (asRollingBuyPct >= AsPressureThreshold) { asPressureLine = "PRESSURE: BUYERS AGGRESSIVE ↑  " + asRollingBuyPct.ToString("0") + "%"; asPressureBrush = Brushes.LimeGreen; }
            else if ((100.0 - asRollingBuyPct) >= AsPressureThreshold) { asPressureLine = "PRESSURE: SELLERS AGGRESSIVE ↓  " + (100.0 - asRollingBuyPct).ToString("0") + "%"; asPressureBrush = Brushes.Red; }
            else { asPressureLine = "PRESSURE: BALANCED"; asPressureBrush = Brushes.Orange; }
            string asPrintLine = asPrintVol > 0 ? "LARGEST PRINT  " + asPrintVol + " @ " + asPrintPrice.ToString("0.00") + "  " + asPrintSide : "LARGEST PRINT  —";

            string feStatusLine = feReceivedSnap ? "LIVE TRADES: active" : "LIVE TRADES: waiting...";
            string feBidLine = double.IsNaN(feBidP) ? "BID  —" : "BID  " + feBidP.ToString("0.00") + "  vol " + feBidV + "/" + FeSweepVolumeThreshold + "  (" + feBidElapsed.ToString("0") + "s)";
            string feAskLine = double.IsNaN(feAskP) ? "ASK  —" : "ASK  " + feAskP.ToString("0.00") + "  vol " + feAskV + "/" + FeSweepVolumeThreshold + "  (" + feAskElapsed.ToString("0") + "s)";

            List<string> lzWallLines = new List<string>();
            foreach (WallHit w in wallsSnap) lzWallLines.Add(w.Side + "  " + w.Price.ToString("0.00") + "  x" + w.Volume);
            List<string> lzClusterLines = new List<string>();
            foreach (ClusterHit c in clustersSnap) lzClusterLines.Add(c.Side + "  " + c.PriceLow.ToString("0.00") + " - " + c.PriceHigh.ToString("0.00") + "  (" + c.LevelCount + " lvl, x" + c.TotalVolume + ")");

            List<string> feEventLines = new List<string>();
            List<Brush> feEventBrushes = new List<Brush>();
            foreach (FlowEvent fe in feEventsSnap)
            {
                feEventLines.Add(fe.Time.ToString("HH:mm:ss") + "  " + fe.Side + " " + fe.Type + "  " + fe.Price.ToString("0.00") + "  x" + fe.Volume);
                feEventBrushes.Add(fe.Type == "SWEEP" ? Brushes.OrangeRed : Brushes.Cyan);
            }

            string lsHeaderLine = LsEnabled
                ? "STAT WALLS (no L2)  z " + lsZSnap.ToString("0.00") + "/" + LsZThreshold.ToString("0.0") + (lsAbsorbedSnap ? "  ABSORB!" : "")
                : "STAT WALLS (no L2)  disabled";
            List<string> lsWallLines = new List<string>();
            foreach (StatWall w in lsWallsSnap)
            {
                int ageBars = currentBarSnap - w.StartBar;
                lsWallLines.Add((w.Side > 0 ? "RESIST " : "SUPPORT ") + w.Near.ToString("0.00") + "  (" + ageBars + " bars)");
            }

            string cfHeaderLine, cfDivergenceLine; Brush cfDivergenceBrush;
            if (!CfEnabled)
            {
                cfHeaderLine = "CVD (no L2)  disabled";
                cfDivergenceLine = "DIVERGENCE: —";
                cfDivergenceBrush = Brushes.Gray;
            }
            else if (!cfHasTradesSnap)
            {
                cfHeaderLine = "CVD (no L2)  waiting on tape...";
                cfDivergenceLine = "DIVERGENCE: —";
                cfDivergenceBrush = Brushes.Gray;
            }
            else
            {
                cfHeaderLine = "CVD (no L2)  Δ " + cfCvdSnap.ToString("0") + "  slope " + cfSlopeSnap.ToString("+0.00;-0.00;0.00") + "  R² " + cfR2Snap.ToString("0.00");
                // "(checked N, mismatch M)" -- diagnostic added 2026-09-16 after a user understandably
                // doubted a full session with zero divergence markers. N = bars where the window was full
                // AND R² cleared the gate (divergence was actually being evaluated, not just warming up);
                // M = of those, bars where price/CVD genuinely pointed opposite ways. If N grows all
                // session while M stays 0, "none" is a real read of the market (price and CVD agreed the
                // whole time), not a stuck/broken gate -- if M grows too but this still never flips off
                // "none", that would point at a remaining bug downstream of this counter.
                string cfDiagSuffix = "  (checked " + cfGateOpenSnap + ", mismatch " + cfMismatchSnap + ")";
                if (cfDivergenceSnap > 0)      { cfDivergenceLine = "DIVERGENCE: BULLISH ↑" + cfDiagSuffix; cfDivergenceBrush = Brushes.LimeGreen; }
                else if (cfDivergenceSnap < 0) { cfDivergenceLine = "DIVERGENCE: BEARISH ↓" + cfDiagSuffix; cfDivergenceBrush = Brushes.Red; }
                else                             { cfDivergenceLine = "DIVERGENCE: none" + cfDiagSuffix; cfDivergenceBrush = Brushes.Gray; }
            }

            string sqHeaderLine; Brush sqHeaderBrush;
            if (!sqEnabledSnap)
            {
                sqHeaderLine = "SEQUENCE  disabled"; sqHeaderBrush = Brushes.Gray;
            }
            else if (sqDirSnap == 0)
            {
                sqHeaderLine = "SEQUENCE  —"; sqHeaderBrush = Brushes.Gray;
            }
            else
            {
                sqHeaderLine = "SEQUENCE " + (sqDirSnap > 0 ? "↑" : "↓") + " x" + sqCountSnap + "  " + sqLeadLagSnap;
                sqHeaderBrush = sqLeadLagSnap == "CVD CONFIRMS" ? (sqDirSnap > 0 ? Brushes.LimeGreen : Brushes.Red)
                              : sqLeadLagSnap == "CVD DIVERGES" ? Brushes.Orange : Brushes.White;
            }
            string sqExhaustionLine = sqExhSnap > 0 ? "EXHAUSTION: ↑ (possible top)"
                                     : sqExhSnap < 0 ? "EXHAUSTION: ↓ (possible bottom)"
                                     : "EXHAUSTION: none";

            // "(armed n / expired n)" is a diagnostic tail, not a trading read -- it distinguishes
            // "the arming logic never sees a fresh breakout on this level" (armed stays 0 -- would
            // point at a real bug) from "watches arm fine but keep taking longer than FaFailBars to
            // resolve" (armed grows, expired tracks it, failed stays low -- a tuning question, not a
            // bug) whenever FAILED AUCTION itself is reading 0x/-- for longer than expected.
            string faLine = !faEnabledSnap ? "FAILED AUCTION  disabled"
                           : faFailedSnap > 0 ? "FAILED AUCTION  " + faFailedSnap + "x  last " + faLastFailedSnap + "  (armed " + faArmedSnap + " / expired " + faExpiredSnap + ")"
                           : "FAILED AUCTION  —  (armed " + faArmedSnap + " / expired " + faExpiredSnap + ")";
            string faTrappedLine = !faEnabledSnap ? "TRAPPED TRADERS  disabled"
                                  : faTrappedSnap > 0 ? "TRAPPED TRADERS  " + faTrappedSnap + "x  last " + faLastTrappedSnap
                                  : "TRAPPED TRADERS  —";

            string fpLine = !FpEnabled ? "FOOTPRINT (no L2)  disabled"
                           : !fpHasTradesSnap ? "FOOTPRINT (no L2)  waiting on tape..."
                           : "FOOTPRINT (no L2)  " + fpBarsShownSnap + " bar" + (fpBarsShownSnap == 1 ? "" : "s") + " shown";

            long lrTotal = lrHoldSnap + lrBreakSnap;
            string lrLine = lrTotal > 0
                ? "REACTION  HOLDS " + lrHoldSnap + " / BREAKS " + lrBreakSnap + "  (" + ((lrHoldSnap / (double)lrTotal) * 100.0).ToString("0") + "% hold)"
                : "REACTION  —  (no wall tests yet)";

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                mcControlText.Text = mcControlLine;
                mcControlText.Foreground = !mcReadySnapshot ? Brushes.Gray : (confluenceScore < McContestedThreshold ? Brushes.Orange : (confluenceBull ? Brushes.LimeGreen : Brushes.Red));
                mcHtfTrendText.Text = mcHtfLine;
                mcTrendText.Text = mcTrendLine;
                mcMomentumText.Text = mcMomentumLine;
                mcConfluenceText.Text = mcConfluenceLine;
                mcMarketStateText.Text = mkStateLine;
                mcMarketStateText.Foreground = mkStateSnap == "TRENDING" ? Brushes.LimeGreen : (mkStateSnap == "CHOPPY" ? Brushes.Orange : Brushes.White);
                mcEffFlipBalText.Text = mkEffFlipBalLine;
                mcEffFlipBalText.Foreground = mkBalSnap ? Brushes.White : Brushes.Yellow;

                UpdateGaugeMarker(mcTrendMarker, mcReadySnapshot ? trendScore : 0, 200, 10, !mcReadySnapshot ? Brushes.Gray : (trendUp ? Brushes.LimeGreen : Brushes.Red));
                UpdateGaugeMarker(mcMomentumMarker, mcReadySnapshot ? momentumScore : 0, 200, 10, !mcReadySnapshot ? Brushes.Gray : (momentumUp ? Brushes.LimeGreen : Brushes.Red));
                UpdateGaugeMarker(mcConfluenceMarker, mcReadySnapshot ? confluenceScore : 0, 200, 10, !mcReadySnapshot ? Brushes.Gray : (confluenceBull ? Brushes.LimeGreen : Brushes.Red));

                czLocationText.Text = czLocationLine;
                czPsHlcText.Text = czPsHlcLine;
                czPsVwapPocText.Text = czPsVwapPocLine;
                czPsValueText.Text = czPsValueLine;
                czSessionVwapText.Text = czSessionVwapLine;
                czOrText.Text = czOrLine;
                for (int i = 0; i < czZoneRowTexts.Count; i++)
                {
                    if (i < zonesSnap.Count)
                    {
                        Zone z = zonesSnap[i];
                        string strength = z.Labels.Count >= 4 ? "HIGH" : (z.Labels.Count == 3 ? "MED" : "LOW");
                        czZoneRowTexts[i].Text = (i + 1) + ". " + z.MidPrice.ToString("0.00") + "  " + strength + " (" + z.Labels.Count + ")  " + string.Join(", ", z.Labels.ToArray());
                        czZoneRowTexts[i].Foreground = z.Labels.Count >= 4 ? Brushes.Yellow : (z.Labels.Count == 3 ? Brushes.Orange : Brushes.Gray);
                    }
                    else { czZoneRowTexts[i].Text = (i + 1) + ". —"; czZoneRowTexts[i].Foreground = Brushes.Gray; }
                }

                lzStatusText.Text = lzStatusLine;
                lzStatusText.Foreground = lzReceivedSnap ? Brushes.LimeGreen : Brushes.Orange;
                lzBidAskText.Text = lzBidAskLine;
                lzImbalanceText.Text = lzImbalanceLine;
                spHeaderText.Text = spLine;
                spHeaderText.Foreground = spBrush;
                for (int i = 0; i < lzWallRowTexts.Count; i++)
                    lzWallRowTexts[i].Text = i < lzWallLines.Count ? lzWallLines[i] : "—";
                for (int i = 0; i < lzClusterRowTexts.Count; i++)
                    lzClusterRowTexts[i].Text = i < lzClusterLines.Count ? lzClusterLines[i] : "—";

                lsHeaderText.Text = lsHeaderLine;
                lsHeaderText.Foreground = lsAbsorbedSnap ? Brushes.HotPink : Brushes.White;
                for (int i = 0; i < lsWallRowTexts.Count; i++)
                    lsWallRowTexts[i].Text = i < lsWallLines.Count ? lsWallLines[i] : "—";
                lrHeaderText.Text = lrLine;
                lrHeaderText.Foreground = lrTotal == 0 ? Brushes.Gray : (lrHoldSnap >= lrBreakSnap ? Brushes.LimeGreen : Brushes.OrangeRed);

                cfHeaderText.Text = cfHeaderLine;
                cfHeaderText.Foreground = cfHasTradesSnap ? Brushes.White : Brushes.Orange;
                cfDivergenceText.Text = cfDivergenceLine;
                cfDivergenceText.Foreground = cfDivergenceBrush;

                cfSparkline.Points.Clear();
                if (cfSparkSnap.Count >= 2)
                {
                    double minV = cfSparkSnap[0], maxV = cfSparkSnap[0];
                    for (int i = 1; i < cfSparkSnap.Count; i++) { minV = Math.Min(minV, cfSparkSnap[i]); maxV = Math.Max(maxV, cfSparkSnap[i]); }
                    double span = maxV - minV; if (span <= 0) span = 1;
                    for (int i = 0; i < cfSparkSnap.Count; i++)
                    {
                        double x = (i / (double)(cfSparkSnap.Count - 1)) * CfSparklineWidth;
                        double y = CfSparklineHeight - ((cfSparkSnap[i] - minV) / span) * CfSparklineHeight;
                        cfSparkline.Points.Add(new Point(x, y));
                    }
                }

                sqHeaderText.Text = sqHeaderLine;
                sqHeaderText.Foreground = sqHeaderBrush;
                sqExhaustionText.Text = sqExhaustionLine;
                sqExhaustionText.Foreground = sqExhSnap != 0 ? Brushes.DeepSkyBlue : Brushes.Gray;

                faHeaderText.Text = faLine;
                faHeaderText.Foreground = faFailedSnap > 0 ? Brushes.MediumPurple : Brushes.Gray;
                faTrappedText.Text = faTrappedLine;
                faTrappedText.Foreground = faTrappedSnap > 0 ? Brushes.Crimson : Brushes.Gray;

                fpHeaderText.Text = fpLine;
                fpHeaderText.Foreground = !FpEnabled ? Brushes.Gray : (fpHasTradesSnap ? Brushes.White : Brushes.Orange);

                asStatusText.Text = asStatusLine;
                asStatusText.Foreground = asReceivedSnap ? Brushes.LimeGreen : Brushes.Orange;
                asSessionSplitText.Text = asSessionLine;
                asRollingSplitText.Text = asRollingLine;
                double asBarBuyPct = asRollingTotal > 0 ? asRollingBuyPct : 50.0;
                asBarBuyCol.Width  = new GridLength(Math.Max(0.001, asBarBuyPct), GridUnitType.Star);
                asBarSellCol.Width = new GridLength(Math.Max(0.001, 100.0 - asBarBuyPct), GridUnitType.Star);
                asPressureText.Text = asPressureLine;
                asPressureText.Foreground = asPressureBrush;
                asLargestPrintText.Text = asPrintLine;

                feStatusText.Text = feStatusLine;
                feStatusText.Foreground = feReceivedSnap ? Brushes.LimeGreen : Brushes.Orange;
                feWatchingBidText.Text = feBidLine;
                feWatchingAskText.Text = feAskLine;
                for (int i = 0; i < feEventRowTexts.Count; i++)
                {
                    if (i < feEventLines.Count) { feEventRowTexts[i].Text = feEventLines[i]; feEventRowTexts[i].Foreground = feEventBrushes[i]; }
                    else { feEventRowTexts[i].Text = "—"; feEventRowTexts[i].Foreground = Brushes.Gray; }
                }
            });
        }

        // ===================================================================================
        // PANEL CONSTRUCTION -- one outer Grid (title bar, draggable) + one ScrollViewer (all five
        // sections). AddRow() grows RowDefinitions as needed instead of a precomputed count, so
        // there's no way for the row count to drift out of sync with what's actually added.
        // ===================================================================================
        private void AddRow(Grid grid, FrameworkElement el)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(el, grid.RowDefinitions.Count - 1);
            grid.Children.Add(el);
        }

        private void AddPanel()
        {
            if (panelOuter != null || ChartPanel == null) return;

            panelOuter = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(PanelLeftOffset, PanelTopOffset, 0, 0)
            };
            panelOuter.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panelOuter.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            panelTitle = new TextBlock
            {
                Text = "SNEAKY ORDER FLOW   ↔ DRAG", FontSize = 12, FontWeight = FontWeights.Bold,
                Foreground = Brushes.LimeGreen, Background = new SolidColorBrush(Color.FromArgb(235, 5, 20, 18)),
                Margin = new Thickness(7, 4, 7, 3), Cursor = Cursors.SizeAll, ToolTip = "Drag to move panel"
            };
            Grid.SetRow(panelTitle, 0);
            panelOuter.Children.Add(panelTitle);

            contentGrid = new Grid { Width = 280, Background = new SolidColorBrush(Color.FromArgb(235, 5, 20, 18)) };

            AddSectionHeader(contentGrid, "1  MARKET CONTEXT", Brushes.LimeGreen);
            mcControlText    = AddDataRow(contentGrid, "CONTROL: —", Brushes.White, true);
            mcHtfTrendText   = AddDataRow(contentGrid, "TREND: —", Brushes.White, false);
            mcTrendText      = AddDataRow(contentGrid, "TREND  —", Brushes.White, false);
            CreateGaugeRow(contentGrid, out mcTrendMarker, 200, 10);
            mcMomentumText   = AddDataRow(contentGrid, "MOMENTUM  —", Brushes.White, false);
            CreateGaugeRow(contentGrid, out mcMomentumMarker, 200, 10);
            mcConfluenceText = AddDataRow(contentGrid, "CONFLUENCE  —", Brushes.White, false);
            CreateGaugeRow(contentGrid, out mcConfluenceMarker, 200, 10);
            mcMarketStateText = AddDataRow(contentGrid, "MARKET STATE: —", Brushes.White, true);
            mcEffFlipBalText  = AddDataRow(contentGrid, "EFF —  FLIP —  POC(t) —", Brushes.White, false);

            AddSectionHeader(contentGrid, "2  CONFLUENCE ZONES", Brushes.Cyan);
            czLocationText    = AddDataRow(contentGrid, "LOCATION: —", Brushes.White, true);
            czPsHlcText       = AddDataRow(contentGrid, "PS H/L/C  —", Brushes.White, false);
            czPsVwapPocText   = AddDataRow(contentGrid, "PS VWAP / POC  —", Brushes.White, false);
            czPsValueText     = AddDataRow(contentGrid, "PS VALUE (VAH-VAL)  —", Brushes.White, false);
            czSessionVwapText = AddDataRow(contentGrid, "SESSION VWAP  —", Brushes.White, false);
            czOrText          = AddDataRow(contentGrid, "OPEN RANGE  —", Brushes.White, false);
            czZonesHeaderText = AddDataRow(contentGrid, "ZONES:", Brushes.White, true);
            czZoneRowTexts.Clear();
            for (int i = 0; i < CzMaxZonesShown; i++)
                czZoneRowTexts.Add(AddDataRow(contentGrid, (i + 1) + ". —", Brushes.Gray, false));

            AddSectionHeader(contentGrid, "3  LIQUIDITY / WALLS", Brushes.Yellow);
            lzStatusText    = AddDataRow(contentGrid, "LIVE DEPTH: waiting...", Brushes.Orange, true);
            lzBidAskText    = AddDataRow(contentGrid, "BID / ASK  —", Brushes.White, false);
            lzImbalanceText = AddDataRow(contentGrid, "IMBALANCE  —", Brushes.White, false);
            spHeaderText = AddDataRow(contentGrid, "STACK/PULL  NO DEPTH", Brushes.Gray, true);
            lzWallsHeaderText = AddDataRow(contentGrid, "WALLS:", Brushes.White, true);
            lzWallRowTexts.Clear();
            for (int i = 0; i < LzMaxWallsShown; i++)
                lzWallRowTexts.Add(AddDataRow(contentGrid, "—", Brushes.Yellow, false));
            lzClustersHeaderText = AddDataRow(contentGrid, "CLUSTERS:", Brushes.White, true);
            lzClusterRowTexts.Clear();
            for (int i = 0; i < LzMaxClustersShown; i++)
                lzClusterRowTexts.Add(AddDataRow(contentGrid, "—", Brushes.Orange, false));
            lsHeaderText = AddDataRow(contentGrid, "STAT WALLS (no L2)  —", Brushes.White, true);
            lsWallRowTexts.Clear();
            for (int i = 0; i < LsMaxWallsShown; i++)
                lsWallRowTexts.Add(AddDataRow(contentGrid, "—", Brushes.HotPink, false));
            lrHeaderText = AddDataRow(contentGrid, "REACTION  —  (no wall tests yet)", Brushes.Gray, true);
            cfHeaderText     = AddDataRow(contentGrid, "CVD (no L2)  waiting on tape...", Brushes.Orange, true);
            cfDivergenceText = AddDataRow(contentGrid, "DIVERGENCE: —", Brushes.Gray, false);
            cfSparkline      = CreateSparklineRow(contentGrid, CfSparklineWidth, CfSparklineHeight);
            sqHeaderText     = AddDataRow(contentGrid, "SEQUENCE  —", Brushes.White, true);
            sqExhaustionText = AddDataRow(contentGrid, "EXHAUSTION: none", Brushes.Gray, false);
            faHeaderText     = AddDataRow(contentGrid, "FAILED AUCTION  —", Brushes.Gray, true);
            faTrappedText    = AddDataRow(contentGrid, "TRAPPED TRADERS  —", Brushes.Gray, false);
            fpHeaderText     = AddDataRow(contentGrid, "FOOTPRINT (no L2)  —", Brushes.Gray, true);

            AddSectionHeader(contentGrid, "4  AGGRESSION SPLIT", Brushes.Orange);
            asStatusText       = AddDataRow(contentGrid, "LIVE TRADES: waiting...", Brushes.Orange, true);
            asSessionSplitText = AddDataRow(contentGrid, "SESSION  BUY —  SELL —", Brushes.White, false);
            asRollingSplitText = AddDataRow(contentGrid, "LAST " + AsRollingWindowSeconds + "s  BUY —  SELL —", Brushes.White, false);
            CreateTwoColorBar(contentGrid, out asBarBuyCol, out asBarSellCol, Brushes.LimeGreen, Brushes.Red);
            asPressureText     = AddDataRow(contentGrid, "PRESSURE: —", Brushes.Gray, true);
            asLargestPrintText = AddDataRow(contentGrid, "LARGEST PRINT  —", Brushes.Yellow, false);

            AddSectionHeader(contentGrid, "5  FLOW EVENTS", Brushes.OrangeRed);
            feStatusText      = AddDataRow(contentGrid, "LIVE TRADES: waiting...", Brushes.Orange, true);
            feWatchingBidText = AddDataRow(contentGrid, "BID  —", Brushes.White, false);
            feWatchingAskText = AddDataRow(contentGrid, "ASK  —", Brushes.White, false);
            feEventsHeaderText = AddDataRow(contentGrid, "EVENTS:", Brushes.White, true);
            feEventRowTexts.Clear();
            for (int i = 0; i < FeMaxEventsShown; i++)
                feEventRowTexts.Add(AddDataRow(contentGrid, "—", Brushes.Gray, false));

            contentScroll = new ScrollViewer
            {
                Content = contentGrid,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = PanelMaxHeight
            };
            Grid.SetRow(contentScroll, 1);
            panelOuter.Children.Add(contentScroll);

            panelTitle.MouseLeftButtonDown += PanelTitle_MouseLeftButtonDown;
            panelTitle.MouseMove           += PanelTitle_MouseMove;
            panelTitle.MouseLeftButtonUp   += PanelTitle_MouseLeftButtonUp;

            panelDragTransform = new TranslateTransform();
            panelOuter.RenderTransform = panelDragTransform;

            chartHostGrid = FindChartHostGrid();
            if (chartHostGrid == null) { panelOuter = null; return; }
            chartHostGrid.Children.Add(panelOuter);
            chartHostGrid.SizeChanged += ChartHostGrid_SizeChanged;
        }

        private void AddSectionHeader(Grid grid, string text, Brush brush)
        {
            TextBlock header = new TextBlock
            {
                Text = text, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = brush,
                Margin = new Thickness(7, 8, 7, 2)
            };
            AddRow(grid, header);
        }

        private TextBlock AddDataRow(Grid grid, string text, Brush brush, bool bold)
        {
            TextBlock t = new TextBlock
            {
                Text = text, FontSize = bold ? 11 : 10, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                Foreground = brush, Margin = new Thickness(9, 1, 7, 1), TextWrapping = TextWrapping.Wrap
            };
            AddRow(grid, t);
            return t;
        }

        // ---- Visual-polish helpers (two-color bar, gauge sliders, sparkline) ----------------------

        // A thin two-column bar (buy% / sell%) -- the columns are Star-sized so RefreshPanel can just
        // change GridLength ratios each refresh rather than doing manual pixel-width math.
        private void CreateTwoColorBar(Grid parent, out ColumnDefinition buyCol, out ColumnDefinition sellCol, Brush buyBrush, Brush sellBrush)
        {
            Grid bar = new Grid { Height = 8, Margin = new Thickness(9, 1, 7, 4) };
            buyCol  = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            sellCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            bar.ColumnDefinitions.Add(buyCol);
            bar.ColumnDefinitions.Add(sellCol);

            System.Windows.Shapes.Rectangle buyRect  = new System.Windows.Shapes.Rectangle { Fill = buyBrush };
            System.Windows.Shapes.Rectangle sellRect = new System.Windows.Shapes.Rectangle { Fill = sellBrush };
            Grid.SetColumn(buyRect, 0);
            Grid.SetColumn(sellRect, 1);
            bar.Children.Add(buyRect);
            bar.Children.Add(sellRect);

            AddRow(parent, bar);
        }

        // A horizontal track + a round marker positioned by a 0-100 score -- the slider-style
        // gauge rows next to Trend/Momentum/Confluence. The track itself is fixed/gray and its
        // reference isn't kept; only the marker (position + color) changes on refresh.
        private void CreateGaugeRow(Grid parent, out System.Windows.Shapes.Ellipse marker, double width, double markerSize)
        {
            Canvas canvas = new Canvas { Width = width, Height = markerSize, Margin = new Thickness(9, 0, 7, 4) };

            System.Windows.Shapes.Rectangle track = new System.Windows.Shapes.Rectangle { Width = width, Height = 3, Fill = Brushes.DimGray };
            Canvas.SetLeft(track, 0);
            Canvas.SetTop(track, (markerSize - 3) / 2.0);
            canvas.Children.Add(track);

            marker = new System.Windows.Shapes.Ellipse { Width = markerSize, Height = markerSize, Fill = Brushes.White };
            Canvas.SetTop(marker, 0);
            canvas.Children.Add(marker);

            AddRow(parent, canvas);
        }

        // Moves a gauge marker to reflect a 0-100 score and recolors it. UI-thread only (always
        // called from inside RefreshPanel's ChartControl.Dispatcher.InvokeAsync block).
        private void UpdateGaugeMarker(System.Windows.Shapes.Ellipse marker, double score, double width, double markerSize, Brush brush)
        {
            double clamped = Math.Max(0, Math.Min(100, score));
            Canvas.SetLeft(marker, (clamped / 100.0) * (width - markerSize));
            marker.Fill = brush;
        }

        // A small Canvas + Polyline for the CVD mini sparkline -- points are rebuilt each refresh from
        // a snapshot of the tail of cfHCvd (see RefreshPanel), normalized to fit the canvas height.
        private System.Windows.Shapes.Polyline CreateSparklineRow(Grid parent, double width, double height)
        {
            Canvas canvas = new Canvas { Width = width, Height = height, Margin = new Thickness(9, 2, 7, 4) };
            System.Windows.Shapes.Polyline line = new System.Windows.Shapes.Polyline { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 1.5 };
            canvas.Children.Add(line);
            AddRow(parent, canvas);
            return line;
        }

        private void ChartHostGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (contentScroll == null || chartHostGrid == null) return;
            double avail = chartHostGrid.ActualHeight - PanelTopOffset - 40;
            contentScroll.MaxHeight = Math.Max(100, Math.Min(PanelMaxHeight, avail));
        }

        private void RemovePanel()
        {
            if (panelOuter == null) return;
            panelTitle.MouseLeftButtonDown -= PanelTitle_MouseLeftButtonDown;
            panelTitle.MouseMove           -= PanelTitle_MouseMove;
            panelTitle.MouseLeftButtonUp   -= PanelTitle_MouseLeftButtonUp;
            if (chartHostGrid != null)
            {
                chartHostGrid.SizeChanged -= ChartHostGrid_SizeChanged;
                chartHostGrid.Children.Remove(panelOuter);
            }
            panelOuter = null;
            chartHostGrid = null;
            contentScroll = null;
            contentGrid = null;
            czZoneRowTexts.Clear();
            lzWallRowTexts.Clear();
            lzClusterRowTexts.Clear();
            lsWallRowTexts.Clear();
            feEventRowTexts.Clear();
        }

        private Grid FindChartHostGrid()
        {
            if (ChartControl == null) return null;
            Grid direct = ChartControl.Parent as Grid;
            if (direct != null) return direct;
            DependencyObject current = System.Windows.Media.VisualTreeHelper.GetParent(ChartControl);
            while (current != null)
            {
                Grid grid = current as Grid;
                if (grid != null) return grid;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void PanelTitle_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
        {
            panelDragging = true;
            panelDragStart = e.GetPosition(chartHostGrid ?? panelOuter);
            panelTitle.CaptureMouse();
            e.Handled = true;
        }

        private void PanelTitle_MouseMove(object s, MouseEventArgs e)
        {
            if (!panelDragging) return;
            Point p = e.GetPosition(chartHostGrid ?? panelOuter);
            Vector d = p - panelDragStart;
            panelDragTransform.X += d.X;
            panelDragTransform.Y += d.Y;
            panelDragStart = p;
            e.Handled = true;
        }

        private void PanelTitle_MouseLeftButtonUp(object s, MouseButtonEventArgs e)
        {
            if (!panelDragging) return;
            panelDragging = false;
            panelTitle.ReleaseMouseCapture();
            e.Handled = true;
        }

        #region Properties
        // ---- 1 Market Context ----
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "Trend period", Order = 1, GroupName = "1 Market Context")]
        public int McTrendPeriod { get; set; }
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "Momentum period", Order = 2, GroupName = "1 Market Context")]
        public int McMomentumPeriod { get; set; }
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "MACD fast", Order = 3, GroupName = "1 Market Context")]
        public int McMacdFast { get; set; }
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "MACD slow", Order = 4, GroupName = "1 Market Context")]
        public int McMacdSlow { get; set; }
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "MACD signal", Order = 5, GroupName = "1 Market Context")]
        public int McMacdSignal { get; set; }
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "Higher timeframe (minutes)", Order = 6, GroupName = "1 Market Context")]
        public int McHigherTimeframeMinutes { get; set; }
        [NinjaScriptProperty] [Range(50, 100)]
        [Display(Name = "Contested threshold", Order = 7, GroupName = "1 Market Context")]
        public int McContestedThreshold { get; set; }
        [NinjaScriptProperty] [Range(5, int.MaxValue)]
        [Display(Name = "Market state lookback (bars)", Description = "Window for the Choppiness Index and Efficiency Ratio.", Order = 8, GroupName = "1 Market Context")]
        public int McMarketStateLookback { get; set; }
        [NinjaScriptProperty] [Range(0.0, 100.0)]
        [Display(Name = "Choppy threshold (CI)", Description = "Choppiness Index at/above this labels MARKET STATE as CHOPPY. 61.8 is the common default.", Order = 9, GroupName = "1 Market Context")]
        public double McChoppyThreshold { get; set; }
        [NinjaScriptProperty] [Range(0.0, 100.0)]
        [Display(Name = "Trendy threshold (CI)", Description = "Choppiness Index at/below this (with ADX >= 25) labels MARKET STATE as TRENDING. 38.2 is the common default.", Order = 10, GroupName = "1 Market Context")]
        public double McTrendyThreshold { get; set; }

        // ---- 2 Confluence Zones ----
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "Profile bucket (ticks)", Order = 1, GroupName = "2 Confluence Zones")]
        public int CzProfileBucketTicks { get; set; }
        [NinjaScriptProperty] [Range(50, 100)]
        [Display(Name = "Value area %", Order = 2, GroupName = "2 Confluence Zones")]
        public int CzValueAreaPercent { get; set; }
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "Opening range (minutes)", Order = 3, GroupName = "2 Confluence Zones")]
        public int CzOpeningRangeMinutes { get; set; }
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "Zone tolerance (ticks)", Order = 4, GroupName = "2 Confluence Zones")]
        public int CzZoneToleranceTicks { get; set; }
        [NinjaScriptProperty] [Range(1, 10)]
        [Display(Name = "Max zones shown", Order = 5, GroupName = "2 Confluence Zones")]
        public int CzMaxZonesShown { get; set; }

        // ---- 3 Liquidity Zones ----
        [NinjaScriptProperty] [Range(1, 50)]
        [Display(Name = "Levels analyzed", Order = 1, GroupName = "3 Liquidity Zones")]
        public int LzLevelsAnalyzed { get; set; }
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "Wall size threshold", Order = 2, GroupName = "3 Liquidity Zones")]
        public int LzWallSizeThreshold { get; set; }
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "Cluster size threshold", Order = 3, GroupName = "3 Liquidity Zones")]
        public int LzClusterSizeThreshold { get; set; }
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "Zone tolerance (ticks)", Order = 4, GroupName = "3 Liquidity Zones")]
        public int LzZoneToleranceTicks { get; set; }
        [NinjaScriptProperty] [Range(1, 10)]
        [Display(Name = "Max walls shown", Order = 5, GroupName = "3 Liquidity Zones")]
        public int LzMaxWallsShown { get; set; }
        [NinjaScriptProperty] [Range(1, 10)]
        [Display(Name = "Max clusters shown", Order = 6, GroupName = "3 Liquidity Zones")]
        public int LzMaxClustersShown { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Auto-scale by instrument", Description = "Full-size futures (NQ, ES, YM, RTY...) typically rest 50-75+ contracts per level; their CME 'micro' counterparts (MNQ, MES, MYM, M2K...) trade at roughly 1/10th to 1/20th that size, so the wall/cluster thresholds above rarely clear on a micro book. When on, this overrides both thresholds at load time based on the chart's instrument. Turn off to pin your own values.", Order = 7, GroupName = "3 Liquidity Zones")]
        public bool LzAutoScaleByInstrument { get; set; }

        // ---- 3g Stack / Pull (needs real Level 2 depth) ----
        [NinjaScriptProperty]
        [Display(Name = "Enabled", Description = "Rolling add-vs-cancel volume at the best bid/ask, from real Level 2 depth. Shows NO DEPTH honestly when the feed doesn't supply it (same gate as section 3).", Order = 1, GroupName = "3g Stack / Pull (L2 only)")]
        public bool SpEnabled { get; set; }
        [NinjaScriptProperty] [Range(1, 600)]
        [Display(Name = "Rolling window (seconds)", Order = 2, GroupName = "3g Stack / Pull (L2 only)")]
        public int SpWindowSeconds { get; set; }
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "Stack/pull threshold", Description = "Net top-of-book size change over the window must clear this (either direction) to label STACKING/PULLING instead of neutral.", Order = 3, GroupName = "3g Stack / Pull (L2 only)")]
        public int SpStackThreshold { get; set; }

        // ---- 3b Stat Walls (no Level 2 needed -- tick-rule delta + regression absorption) ----
        [NinjaScriptProperty]
        [Display(Name = "Enabled", Description = "Absorption-inferred walls built from trade prints (tick rule + rolling regression) -- works even when the feed provides no Level 2 depth.", Order = 1, GroupName = "3b Stat Walls (no L2)")]
        public bool LsEnabled { get; set; }
        [NinjaScriptProperty] [Range(20, int.MaxValue)]
        [Display(Name = "Regression + z lookback (bars)", Order = 2, GroupName = "3b Stat Walls (no L2)")]
        public int LsLookback { get; set; }
        [NinjaScriptProperty] [Range(0.5, 10.0)]
        [Display(Name = "Absorption z threshold", Order = 3, GroupName = "3b Stat Walls (no L2)")]
        public double LsZThreshold { get; set; }
        [NinjaScriptProperty] [Range(2, int.MaxValue)]
        [Display(Name = "ATR period (wall height)", Order = 4, GroupName = "3b Stat Walls (no L2)")]
        public int LsAtrPeriod { get; set; }
        [NinjaScriptProperty] [Range(0.1, 10.0)]
        [Display(Name = "ATR multiplier (wall thickness)", Order = 5, GroupName = "3b Stat Walls (no L2)")]
        public double LsAtrMult { get; set; }
        [NinjaScriptProperty] [Range(1, 40)]
        [Display(Name = "Max active walls", Order = 6, GroupName = "3b Stat Walls (no L2)")]
        public int LsMaxWalls { get; set; }
        [NinjaScriptProperty] [Range(1, 10)]
        [Display(Name = "Max walls shown in panel", Order = 7, GroupName = "3b Stat Walls (no L2)")]
        public int LsMaxWallsShown { get; set; }

        // ---- 3c CVD Regression (no Level 2 needed -- cumulative volume delta + OLS regression) ----
        [NinjaScriptProperty]
        [Display(Name = "Enabled", Description = "Session-cumulative volume delta regressed against bar sequence (reuses the trade classification already computed for Aggression Split) -- works even when the feed provides no Level 2 depth.", Order = 1, GroupName = "3c CVD Regression (no L2)")]
        public bool CfEnabled { get; set; }
        [NinjaScriptProperty] [Range(20, int.MaxValue)]
        [Display(Name = "Regression + divergence lookback (bars)", Order = 2, GroupName = "3c CVD Regression (no L2)")]
        public int CfLookback { get; set; }
        [NinjaScriptProperty] [Range(0.0, 1.0)]
        [Display(Name = "Min R² (fit-quality gate)", Description = "Divergence only fires once the CVD-vs-bar regression's R² clears this -- below it, the slope is noise, not a real read.", Order = 3, GroupName = "3c CVD Regression (no L2)")]
        public double CfMinR2 { get; set; }
        [NinjaScriptProperty] [Range(1, 100)]
        [Display(Name = "Divergence triangle height (ticks)", Description = "2026-09-17 -- how tall the chart-marker triangles (DrawCvdDivergence) are drawn, tip to base, in ticks. Raise this to make them easier to spot on a busy chart.", Order = 4, GroupName = "3c CVD Regression (no L2)")]
        public int CfDivMarkerHeightTicks { get; set; }

        // ---- 3d Sequence / Exhaustion (no Level 2 needed) ----
        [NinjaScriptProperty]
        [Display(Name = "Enabled", Description = "Consecutive-bar impulse-leg counting (SEQUENCE) with a range-contraction EXHAUSTION flag and a CVD-confirms/diverges LEAD-LAG read -- works even when the feed provides no Level 2 depth.", Order = 1, GroupName = "3d Sequence / Exhaustion (no L2)")]
        public bool SqEnabled { get; set; }
        [NinjaScriptProperty] [Range(2, int.MaxValue)]
        [Display(Name = "Exhaustion bars", Description = "Minimum sequence length before an exhaustion flag can fire.", Order = 2, GroupName = "3d Sequence / Exhaustion (no L2)")]
        public int SqExhaustionBars { get; set; }
        [NinjaScriptProperty] [Range(0.1, 1.0)]
        [Display(Name = "Exhaustion range factor", Description = "Exhaustion fires when the current bar's range shrinks to at/below this fraction of the sequence's own average range so far.", Order = 3, GroupName = "3d Sequence / Exhaustion (no L2)")]
        public double SqExhaustionRangeFactor { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Gate LEAD-LAG by CVD R²", Description = "When on, CVD CONFIRMS/DIVERGES only shows once the CVD regression's own R² clears \"Min R² (fit-quality gate)\" under 3c CVD Regression (same threshold, so this label agrees with the CVD section's own DIVERGENCE marker instead of firing on a noisy/unfit slope). Below the gate it reads \"CVD —\" instead. Turn off to restore the old always-on read.", Order = 4, GroupName = "3d Sequence / Exhaustion (no L2)")]
        public bool SqGateLeadLagByR2 { get; set; }

        // ---- 3f Failed Auction / Trapped Traders (no Level 2 needed) ----
        [NinjaScriptProperty]
        [Display(Name = "Enabled", Description = "Watches Opening Range and Previous Session High/Low for failed breakouts and trapped-trader reversals -- works even when the feed provides no Level 2 depth.", Order = 1, GroupName = "3f Failed Auction / Trapped (no L2)")]
        public bool FaEnabled { get; set; }
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "Failed-auction window (bars)", Description = "A breakout must close back inside the level within this many bars to count as a failed auction.", Order = 2, GroupName = "3f Failed Auction / Trapped (no L2)")]
        public int FaFailBars { get; set; }
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "Trapped-reversal window (bars)", Description = "After a failed auction, price must reverse through the level within this many more bars to count as trapped traders.", Order = 3, GroupName = "3f Failed Auction / Trapped (no L2)")]
        public int FaTrappedBars { get; set; }
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "Trapped-reversal buffer (ticks)", Description = "How far beyond the level price must close to confirm a trapped-trader reversal, not just a re-test.", Order = 4, GroupName = "3f Failed Auction / Trapped (no L2)")]
        public int FaTrappedTicks { get; set; }

        // ---- 3h Footprint Grid (no Level 2 needed -- per-price bid×ask cells from tick-rule classification) ----
        [NinjaScriptProperty]
        [Display(Name = "Enabled", Description = "Classic per-price bid×ask footprint cells drawn on each bar, from the same trade classification already computed for Aggression Split/CVD Regression -- works even when the feed provides no Level 2 depth.", Order = 1, GroupName = "3h Footprint Grid (no L2)")]
        public bool FpEnabled { get; set; }
        [NinjaScriptProperty] [Range(1, 200)]
        [Display(Name = "Max bars shown", Description = "How many of the most recently finalized bars keep their footprint drawn on the chart. Older bars' rows are removed as new ones finalize.", Order = 2, GroupName = "3h Footprint Grid (no L2)")]
        public int FpMaxBarsShown { get; set; }
        [NinjaScriptProperty] [Range(1.0, 50.0)]
        [Display(Name = "Imbalance ratio", Description = "A price row is highlighted as a buy/sell imbalance once its ask (or bid) volume is at least this many times the bid (or ask) volume one tick below (or above) it -- the classic footprint \"diagonal imbalance\" test.", Order = 3, GroupName = "3h Footprint Grid (no L2)")]
        public double FpImbalanceRatio { get; set; }
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "Min row volume (filter noise)", Description = "Price rows with total volume below this are skipped, to cut single-lot dust out of busy bars.", Order = 4, GroupName = "3h Footprint Grid (no L2)")]
        public int FpMinRowVolume { get; set; }

        // ---- 4 Aggression Split ----
        [NinjaScriptProperty] [Range(1, 3600)]
        [Display(Name = "Rolling window (seconds)", Order = 1, GroupName = "4 Aggression Split")]
        public int AsRollingWindowSeconds { get; set; }
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "Large print threshold", Order = 2, GroupName = "4 Aggression Split")]
        public int AsLargePrintThreshold { get; set; }
        [NinjaScriptProperty] [Range(50, 100)]
        [Display(Name = "Pressure threshold (%)", Order = 3, GroupName = "4 Aggression Split")]
        public int AsPressureThreshold { get; set; }

        // ---- 5 Flow Events ----
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "Sweep volume threshold", Order = 1, GroupName = "5 Flow Events")]
        public int FeSweepVolumeThreshold { get; set; }
        [NinjaScriptProperty] [Range(1, 60)]
        [Display(Name = "Sweep window (seconds)", Order = 2, GroupName = "5 Flow Events")]
        public int FeSweepWindowSeconds { get; set; }
        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name = "Absorption volume threshold", Order = 3, GroupName = "5 Flow Events")]
        public int FeAbsorptionVolumeThreshold { get; set; }
        [NinjaScriptProperty] [Range(1, 600)]
        [Display(Name = "Absorption window (seconds)", Order = 4, GroupName = "5 Flow Events")]
        public int FeAbsorptionWindowSeconds { get; set; }
        [NinjaScriptProperty] [Range(1, 15)]
        [Display(Name = "Max events shown", Order = 5, GroupName = "5 Flow Events")]
        public int FeMaxEventsShown { get; set; }

        // ---- 6 Panel & Lines ----
        [NinjaScriptProperty] [Range(50, 5000)]
        [Display(Name = "Panel refresh (ms)", Order = 1, GroupName = "6 Panel & Lines")]
        public int PanelRefreshMs { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show panel", Order = 2, GroupName = "6 Panel & Lines")]
        public bool ShowPanel { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Confluence Zones chart lines", Order = 3, GroupName = "6 Panel & Lines")]
        public bool ShowConfluenceLines { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Liquidity Zones chart lines", Order = 4, GroupName = "6 Panel & Lines")]
        public bool ShowLiquidityLines { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Stat Wall chart boxes", Order = 5, GroupName = "6 Panel & Lines")]
        public bool ShowStatWallLines { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show CVD divergence markers", Order = 6, GroupName = "6 Panel & Lines")]
        public bool ShowCvdMarkers { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show sequence exhaustion markers", Order = 7, GroupName = "6 Panel & Lines")]
        public bool ShowSequenceMarkers { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show failed-auction/trapped markers", Order = 8, GroupName = "6 Panel & Lines")]
        public bool ShowFailedAuctionMarkers { get; set; }
        [NinjaScriptProperty] [Range(0, 2000)]
        [Display(Name = "Panel left offset", Order = 9, GroupName = "6 Panel & Lines")]
        public int PanelLeftOffset { get; set; }
        [NinjaScriptProperty] [Range(0, 2000)]
        [Display(Name = "Panel top offset", Order = 10, GroupName = "6 Panel & Lines")]
        public int PanelTopOffset { get; set; }
        [NinjaScriptProperty] [Range(200, 3000)]
        [Display(Name = "Panel max height", Description = "Caps how tall the scrollable section area can get before it scrolls.", Order = 11, GroupName = "6 Panel & Lines")]
        public int PanelMaxHeight { get; set; }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private SneakyOrderFlowDashboard_v1_0_0[] cacheSneakyOrderFlowDashboard_v1_0_0;
		public SneakyOrderFlowDashboard_v1_0_0 SneakyOrderFlowDashboard_v1_0_0(int mcTrendPeriod, int mcMomentumPeriod, int mcMacdFast, int mcMacdSlow, int mcMacdSignal, int mcHigherTimeframeMinutes, int mcContestedThreshold, int mcMarketStateLookback, double mcChoppyThreshold, double mcTrendyThreshold, int czProfileBucketTicks, int czValueAreaPercent, int czOpeningRangeMinutes, int czZoneToleranceTicks, int czMaxZonesShown, int lzLevelsAnalyzed, int lzWallSizeThreshold, int lzClusterSizeThreshold, int lzZoneToleranceTicks, int lzMaxWallsShown, int lzMaxClustersShown, bool lzAutoScaleByInstrument, bool spEnabled, int spWindowSeconds, int spStackThreshold, bool lsEnabled, int lsLookback, double lsZThreshold, int lsAtrPeriod, double lsAtrMult, int lsMaxWalls, int lsMaxWallsShown, bool cfEnabled, int cfLookback, double cfMinR2, bool sqEnabled, int sqExhaustionBars, double sqExhaustionRangeFactor, bool sqGateLeadLagByR2, bool faEnabled, int faFailBars, int faTrappedBars, int faTrappedTicks, bool fpEnabled, int fpMaxBarsShown, double fpImbalanceRatio, int fpMinRowVolume, int asRollingWindowSeconds, int asLargePrintThreshold, int asPressureThreshold, int feSweepVolumeThreshold, int feSweepWindowSeconds, int feAbsorptionVolumeThreshold, int feAbsorptionWindowSeconds, int feMaxEventsShown, int panelRefreshMs, bool showPanel, bool showConfluenceLines, bool showLiquidityLines, bool showStatWallLines, bool showCvdMarkers, bool showSequenceMarkers, bool showFailedAuctionMarkers, int panelLeftOffset, int panelTopOffset, int panelMaxHeight)
		{
			return SneakyOrderFlowDashboard_v1_0_0(Input, mcTrendPeriod, mcMomentumPeriod, mcMacdFast, mcMacdSlow, mcMacdSignal, mcHigherTimeframeMinutes, mcContestedThreshold, mcMarketStateLookback, mcChoppyThreshold, mcTrendyThreshold, czProfileBucketTicks, czValueAreaPercent, czOpeningRangeMinutes, czZoneToleranceTicks, czMaxZonesShown, lzLevelsAnalyzed, lzWallSizeThreshold, lzClusterSizeThreshold, lzZoneToleranceTicks, lzMaxWallsShown, lzMaxClustersShown, lzAutoScaleByInstrument, spEnabled, spWindowSeconds, spStackThreshold, lsEnabled, lsLookback, lsZThreshold, lsAtrPeriod, lsAtrMult, lsMaxWalls, lsMaxWallsShown, cfEnabled, cfLookback, cfMinR2, sqEnabled, sqExhaustionBars, sqExhaustionRangeFactor, sqGateLeadLagByR2, faEnabled, faFailBars, faTrappedBars, faTrappedTicks, fpEnabled, fpMaxBarsShown, fpImbalanceRatio, fpMinRowVolume, asRollingWindowSeconds, asLargePrintThreshold, asPressureThreshold, feSweepVolumeThreshold, feSweepWindowSeconds, feAbsorptionVolumeThreshold, feAbsorptionWindowSeconds, feMaxEventsShown, panelRefreshMs, showPanel, showConfluenceLines, showLiquidityLines, showStatWallLines, showCvdMarkers, showSequenceMarkers, showFailedAuctionMarkers, panelLeftOffset, panelTopOffset, panelMaxHeight);
		}

		public SneakyOrderFlowDashboard_v1_0_0 SneakyOrderFlowDashboard_v1_0_0(ISeries<double> input, int mcTrendPeriod, int mcMomentumPeriod, int mcMacdFast, int mcMacdSlow, int mcMacdSignal, int mcHigherTimeframeMinutes, int mcContestedThreshold, int mcMarketStateLookback, double mcChoppyThreshold, double mcTrendyThreshold, int czProfileBucketTicks, int czValueAreaPercent, int czOpeningRangeMinutes, int czZoneToleranceTicks, int czMaxZonesShown, int lzLevelsAnalyzed, int lzWallSizeThreshold, int lzClusterSizeThreshold, int lzZoneToleranceTicks, int lzMaxWallsShown, int lzMaxClustersShown, bool lzAutoScaleByInstrument, bool spEnabled, int spWindowSeconds, int spStackThreshold, bool lsEnabled, int lsLookback, double lsZThreshold, int lsAtrPeriod, double lsAtrMult, int lsMaxWalls, int lsMaxWallsShown, bool cfEnabled, int cfLookback, double cfMinR2, bool sqEnabled, int sqExhaustionBars, double sqExhaustionRangeFactor, bool sqGateLeadLagByR2, bool faEnabled, int faFailBars, int faTrappedBars, int faTrappedTicks, bool fpEnabled, int fpMaxBarsShown, double fpImbalanceRatio, int fpMinRowVolume, int asRollingWindowSeconds, int asLargePrintThreshold, int asPressureThreshold, int feSweepVolumeThreshold, int feSweepWindowSeconds, int feAbsorptionVolumeThreshold, int feAbsorptionWindowSeconds, int feMaxEventsShown, int panelRefreshMs, bool showPanel, bool showConfluenceLines, bool showLiquidityLines, bool showStatWallLines, bool showCvdMarkers, bool showSequenceMarkers, bool showFailedAuctionMarkers, int panelLeftOffset, int panelTopOffset, int panelMaxHeight)
		{
			if (cacheSneakyOrderFlowDashboard_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyOrderFlowDashboard_v1_0_0.Length; idx++)
					if (cacheSneakyOrderFlowDashboard_v1_0_0[idx] != null && cacheSneakyOrderFlowDashboard_v1_0_0[idx].McTrendPeriod == mcTrendPeriod && cacheSneakyOrderFlowDashboard_v1_0_0[idx].McMomentumPeriod == mcMomentumPeriod && cacheSneakyOrderFlowDashboard_v1_0_0[idx].McMacdFast == mcMacdFast && cacheSneakyOrderFlowDashboard_v1_0_0[idx].McMacdSlow == mcMacdSlow && cacheSneakyOrderFlowDashboard_v1_0_0[idx].McMacdSignal == mcMacdSignal && cacheSneakyOrderFlowDashboard_v1_0_0[idx].McHigherTimeframeMinutes == mcHigherTimeframeMinutes && cacheSneakyOrderFlowDashboard_v1_0_0[idx].McContestedThreshold == mcContestedThreshold && cacheSneakyOrderFlowDashboard_v1_0_0[idx].McMarketStateLookback == mcMarketStateLookback && cacheSneakyOrderFlowDashboard_v1_0_0[idx].McChoppyThreshold == mcChoppyThreshold && cacheSneakyOrderFlowDashboard_v1_0_0[idx].McTrendyThreshold == mcTrendyThreshold && cacheSneakyOrderFlowDashboard_v1_0_0[idx].CzProfileBucketTicks == czProfileBucketTicks && cacheSneakyOrderFlowDashboard_v1_0_0[idx].CzValueAreaPercent == czValueAreaPercent && cacheSneakyOrderFlowDashboard_v1_0_0[idx].CzOpeningRangeMinutes == czOpeningRangeMinutes && cacheSneakyOrderFlowDashboard_v1_0_0[idx].CzZoneToleranceTicks == czZoneToleranceTicks && cacheSneakyOrderFlowDashboard_v1_0_0[idx].CzMaxZonesShown == czMaxZonesShown && cacheSneakyOrderFlowDashboard_v1_0_0[idx].LzLevelsAnalyzed == lzLevelsAnalyzed && cacheSneakyOrderFlowDashboard_v1_0_0[idx].LzWallSizeThreshold == lzWallSizeThreshold && cacheSneakyOrderFlowDashboard_v1_0_0[idx].LzClusterSizeThreshold == lzClusterSizeThreshold && cacheSneakyOrderFlowDashboard_v1_0_0[idx].LzZoneToleranceTicks == lzZoneToleranceTicks && cacheSneakyOrderFlowDashboard_v1_0_0[idx].LzMaxWallsShown == lzMaxWallsShown && cacheSneakyOrderFlowDashboard_v1_0_0[idx].LzMaxClustersShown == lzMaxClustersShown && cacheSneakyOrderFlowDashboard_v1_0_0[idx].LzAutoScaleByInstrument == lzAutoScaleByInstrument && cacheSneakyOrderFlowDashboard_v1_0_0[idx].SpEnabled == spEnabled && cacheSneakyOrderFlowDashboard_v1_0_0[idx].SpWindowSeconds == spWindowSeconds && cacheSneakyOrderFlowDashboard_v1_0_0[idx].SpStackThreshold == spStackThreshold && cacheSneakyOrderFlowDashboard_v1_0_0[idx].LsEnabled == lsEnabled && cacheSneakyOrderFlowDashboard_v1_0_0[idx].LsLookback == lsLookback && cacheSneakyOrderFlowDashboard_v1_0_0[idx].LsZThreshold == lsZThreshold && cacheSneakyOrderFlowDashboard_v1_0_0[idx].LsAtrPeriod == lsAtrPeriod && cacheSneakyOrderFlowDashboard_v1_0_0[idx].LsAtrMult == lsAtrMult && cacheSneakyOrderFlowDashboard_v1_0_0[idx].LsMaxWalls == lsMaxWalls && cacheSneakyOrderFlowDashboard_v1_0_0[idx].LsMaxWallsShown == lsMaxWallsShown && cacheSneakyOrderFlowDashboard_v1_0_0[idx].CfEnabled == cfEnabled && cacheSneakyOrderFlowDashboard_v1_0_0[idx].CfLookback == cfLookback && cacheSneakyOrderFlowDashboard_v1_0_0[idx].CfMinR2 == cfMinR2 && cacheSneakyOrderFlowDashboard_v1_0_0[idx].SqEnabled == sqEnabled && cacheSneakyOrderFlowDashboard_v1_0_0[idx].SqExhaustionBars == sqExhaustionBars && cacheSneakyOrderFlowDashboard_v1_0_0[idx].SqExhaustionRangeFactor == sqExhaustionRangeFactor && cacheSneakyOrderFlowDashboard_v1_0_0[idx].SqGateLeadLagByR2 == sqGateLeadLagByR2 && cacheSneakyOrderFlowDashboard_v1_0_0[idx].FaEnabled == faEnabled && cacheSneakyOrderFlowDashboard_v1_0_0[idx].FaFailBars == faFailBars && cacheSneakyOrderFlowDashboard_v1_0_0[idx].FaTrappedBars == faTrappedBars && cacheSneakyOrderFlowDashboard_v1_0_0[idx].FaTrappedTicks == faTrappedTicks && cacheSneakyOrderFlowDashboard_v1_0_0[idx].FpEnabled == fpEnabled && cacheSneakyOrderFlowDashboard_v1_0_0[idx].FpMaxBarsShown == fpMaxBarsShown && cacheSneakyOrderFlowDashboard_v1_0_0[idx].FpImbalanceRatio == fpImbalanceRatio && cacheSneakyOrderFlowDashboard_v1_0_0[idx].FpMinRowVolume == fpMinRowVolume && cacheSneakyOrderFlowDashboard_v1_0_0[idx].AsRollingWindowSeconds == asRollingWindowSeconds && cacheSneakyOrderFlowDashboard_v1_0_0[idx].AsLargePrintThreshold == asLargePrintThreshold && cacheSneakyOrderFlowDashboard_v1_0_0[idx].AsPressureThreshold == asPressureThreshold && cacheSneakyOrderFlowDashboard_v1_0_0[idx].FeSweepVolumeThreshold == feSweepVolumeThreshold && cacheSneakyOrderFlowDashboard_v1_0_0[idx].FeSweepWindowSeconds == feSweepWindowSeconds && cacheSneakyOrderFlowDashboard_v1_0_0[idx].FeAbsorptionVolumeThreshold == feAbsorptionVolumeThreshold && cacheSneakyOrderFlowDashboard_v1_0_0[idx].FeAbsorptionWindowSeconds == feAbsorptionWindowSeconds && cacheSneakyOrderFlowDashboard_v1_0_0[idx].FeMaxEventsShown == feMaxEventsShown && cacheSneakyOrderFlowDashboard_v1_0_0[idx].PanelRefreshMs == panelRefreshMs && cacheSneakyOrderFlowDashboard_v1_0_0[idx].ShowPanel == showPanel && cacheSneakyOrderFlowDashboard_v1_0_0[idx].ShowConfluenceLines == showConfluenceLines && cacheSneakyOrderFlowDashboard_v1_0_0[idx].ShowLiquidityLines == showLiquidityLines && cacheSneakyOrderFlowDashboard_v1_0_0[idx].ShowStatWallLines == showStatWallLines && cacheSneakyOrderFlowDashboard_v1_0_0[idx].ShowCvdMarkers == showCvdMarkers && cacheSneakyOrderFlowDashboard_v1_0_0[idx].ShowSequenceMarkers == showSequenceMarkers && cacheSneakyOrderFlowDashboard_v1_0_0[idx].ShowFailedAuctionMarkers == showFailedAuctionMarkers && cacheSneakyOrderFlowDashboard_v1_0_0[idx].PanelLeftOffset == panelLeftOffset && cacheSneakyOrderFlowDashboard_v1_0_0[idx].PanelTopOffset == panelTopOffset && cacheSneakyOrderFlowDashboard_v1_0_0[idx].PanelMaxHeight == panelMaxHeight && cacheSneakyOrderFlowDashboard_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyOrderFlowDashboard_v1_0_0[idx];
			return CacheIndicator<SneakyOrderFlowDashboard_v1_0_0>(new SneakyOrderFlowDashboard_v1_0_0(){ McTrendPeriod = mcTrendPeriod, McMomentumPeriod = mcMomentumPeriod, McMacdFast = mcMacdFast, McMacdSlow = mcMacdSlow, McMacdSignal = mcMacdSignal, McHigherTimeframeMinutes = mcHigherTimeframeMinutes, McContestedThreshold = mcContestedThreshold, McMarketStateLookback = mcMarketStateLookback, McChoppyThreshold = mcChoppyThreshold, McTrendyThreshold = mcTrendyThreshold, CzProfileBucketTicks = czProfileBucketTicks, CzValueAreaPercent = czValueAreaPercent, CzOpeningRangeMinutes = czOpeningRangeMinutes, CzZoneToleranceTicks = czZoneToleranceTicks, CzMaxZonesShown = czMaxZonesShown, LzLevelsAnalyzed = lzLevelsAnalyzed, LzWallSizeThreshold = lzWallSizeThreshold, LzClusterSizeThreshold = lzClusterSizeThreshold, LzZoneToleranceTicks = lzZoneToleranceTicks, LzMaxWallsShown = lzMaxWallsShown, LzMaxClustersShown = lzMaxClustersShown, LzAutoScaleByInstrument = lzAutoScaleByInstrument, SpEnabled = spEnabled, SpWindowSeconds = spWindowSeconds, SpStackThreshold = spStackThreshold, LsEnabled = lsEnabled, LsLookback = lsLookback, LsZThreshold = lsZThreshold, LsAtrPeriod = lsAtrPeriod, LsAtrMult = lsAtrMult, LsMaxWalls = lsMaxWalls, LsMaxWallsShown = lsMaxWallsShown, CfEnabled = cfEnabled, CfLookback = cfLookback, CfMinR2 = cfMinR2, SqEnabled = sqEnabled, SqExhaustionBars = sqExhaustionBars, SqExhaustionRangeFactor = sqExhaustionRangeFactor, SqGateLeadLagByR2 = sqGateLeadLagByR2, FaEnabled = faEnabled, FaFailBars = faFailBars, FaTrappedBars = faTrappedBars, FaTrappedTicks = faTrappedTicks, FpEnabled = fpEnabled, FpMaxBarsShown = fpMaxBarsShown, FpImbalanceRatio = fpImbalanceRatio, FpMinRowVolume = fpMinRowVolume, AsRollingWindowSeconds = asRollingWindowSeconds, AsLargePrintThreshold = asLargePrintThreshold, AsPressureThreshold = asPressureThreshold, FeSweepVolumeThreshold = feSweepVolumeThreshold, FeSweepWindowSeconds = feSweepWindowSeconds, FeAbsorptionVolumeThreshold = feAbsorptionVolumeThreshold, FeAbsorptionWindowSeconds = feAbsorptionWindowSeconds, FeMaxEventsShown = feMaxEventsShown, PanelRefreshMs = panelRefreshMs, ShowPanel = showPanel, ShowConfluenceLines = showConfluenceLines, ShowLiquidityLines = showLiquidityLines, ShowStatWallLines = showStatWallLines, ShowCvdMarkers = showCvdMarkers, ShowSequenceMarkers = showSequenceMarkers, ShowFailedAuctionMarkers = showFailedAuctionMarkers, PanelLeftOffset = panelLeftOffset, PanelTopOffset = panelTopOffset, PanelMaxHeight = panelMaxHeight }, input, ref cacheSneakyOrderFlowDashboard_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.SneakyOrderFlowDashboard_v1_0_0 SneakyOrderFlowDashboard_v1_0_0(int mcTrendPeriod, int mcMomentumPeriod, int mcMacdFast, int mcMacdSlow, int mcMacdSignal, int mcHigherTimeframeMinutes, int mcContestedThreshold, int mcMarketStateLookback, double mcChoppyThreshold, double mcTrendyThreshold, int czProfileBucketTicks, int czValueAreaPercent, int czOpeningRangeMinutes, int czZoneToleranceTicks, int czMaxZonesShown, int lzLevelsAnalyzed, int lzWallSizeThreshold, int lzClusterSizeThreshold, int lzZoneToleranceTicks, int lzMaxWallsShown, int lzMaxClustersShown, bool lzAutoScaleByInstrument, bool spEnabled, int spWindowSeconds, int spStackThreshold, bool lsEnabled, int lsLookback, double lsZThreshold, int lsAtrPeriod, double lsAtrMult, int lsMaxWalls, int lsMaxWallsShown, bool cfEnabled, int cfLookback, double cfMinR2, bool sqEnabled, int sqExhaustionBars, double sqExhaustionRangeFactor, bool sqGateLeadLagByR2, bool faEnabled, int faFailBars, int faTrappedBars, int faTrappedTicks, bool fpEnabled, int fpMaxBarsShown, double fpImbalanceRatio, int fpMinRowVolume, int asRollingWindowSeconds, int asLargePrintThreshold, int asPressureThreshold, int feSweepVolumeThreshold, int feSweepWindowSeconds, int feAbsorptionVolumeThreshold, int feAbsorptionWindowSeconds, int feMaxEventsShown, int panelRefreshMs, bool showPanel, bool showConfluenceLines, bool showLiquidityLines, bool showStatWallLines, bool showCvdMarkers, bool showSequenceMarkers, bool showFailedAuctionMarkers, int panelLeftOffset, int panelTopOffset, int panelMaxHeight)
		{
			return indicator.SneakyOrderFlowDashboard_v1_0_0(Input, mcTrendPeriod, mcMomentumPeriod, mcMacdFast, mcMacdSlow, mcMacdSignal, mcHigherTimeframeMinutes, mcContestedThreshold, mcMarketStateLookback, mcChoppyThreshold, mcTrendyThreshold, czProfileBucketTicks, czValueAreaPercent, czOpeningRangeMinutes, czZoneToleranceTicks, czMaxZonesShown, lzLevelsAnalyzed, lzWallSizeThreshold, lzClusterSizeThreshold, lzZoneToleranceTicks, lzMaxWallsShown, lzMaxClustersShown, lzAutoScaleByInstrument, spEnabled, spWindowSeconds, spStackThreshold, lsEnabled, lsLookback, lsZThreshold, lsAtrPeriod, lsAtrMult, lsMaxWalls, lsMaxWallsShown, cfEnabled, cfLookback, cfMinR2, sqEnabled, sqExhaustionBars, sqExhaustionRangeFactor, sqGateLeadLagByR2, faEnabled, faFailBars, faTrappedBars, faTrappedTicks, fpEnabled, fpMaxBarsShown, fpImbalanceRatio, fpMinRowVolume, asRollingWindowSeconds, asLargePrintThreshold, asPressureThreshold, feSweepVolumeThreshold, feSweepWindowSeconds, feAbsorptionVolumeThreshold, feAbsorptionWindowSeconds, feMaxEventsShown, panelRefreshMs, showPanel, showConfluenceLines, showLiquidityLines, showStatWallLines, showCvdMarkers, showSequenceMarkers, showFailedAuctionMarkers, panelLeftOffset, panelTopOffset, panelMaxHeight);
		}

		public Indicators.SneakyOrderFlowDashboard_v1_0_0 SneakyOrderFlowDashboard_v1_0_0(ISeries<double> input , int mcTrendPeriod, int mcMomentumPeriod, int mcMacdFast, int mcMacdSlow, int mcMacdSignal, int mcHigherTimeframeMinutes, int mcContestedThreshold, int mcMarketStateLookback, double mcChoppyThreshold, double mcTrendyThreshold, int czProfileBucketTicks, int czValueAreaPercent, int czOpeningRangeMinutes, int czZoneToleranceTicks, int czMaxZonesShown, int lzLevelsAnalyzed, int lzWallSizeThreshold, int lzClusterSizeThreshold, int lzZoneToleranceTicks, int lzMaxWallsShown, int lzMaxClustersShown, bool lzAutoScaleByInstrument, bool spEnabled, int spWindowSeconds, int spStackThreshold, bool lsEnabled, int lsLookback, double lsZThreshold, int lsAtrPeriod, double lsAtrMult, int lsMaxWalls, int lsMaxWallsShown, bool cfEnabled, int cfLookback, double cfMinR2, bool sqEnabled, int sqExhaustionBars, double sqExhaustionRangeFactor, bool sqGateLeadLagByR2, bool faEnabled, int faFailBars, int faTrappedBars, int faTrappedTicks, bool fpEnabled, int fpMaxBarsShown, double fpImbalanceRatio, int fpMinRowVolume, int asRollingWindowSeconds, int asLargePrintThreshold, int asPressureThreshold, int feSweepVolumeThreshold, int feSweepWindowSeconds, int feAbsorptionVolumeThreshold, int feAbsorptionWindowSeconds, int feMaxEventsShown, int panelRefreshMs, bool showPanel, bool showConfluenceLines, bool showLiquidityLines, bool showStatWallLines, bool showCvdMarkers, bool showSequenceMarkers, bool showFailedAuctionMarkers, int panelLeftOffset, int panelTopOffset, int panelMaxHeight)
		{
			return indicator.SneakyOrderFlowDashboard_v1_0_0(input, mcTrendPeriod, mcMomentumPeriod, mcMacdFast, mcMacdSlow, mcMacdSignal, mcHigherTimeframeMinutes, mcContestedThreshold, mcMarketStateLookback, mcChoppyThreshold, mcTrendyThreshold, czProfileBucketTicks, czValueAreaPercent, czOpeningRangeMinutes, czZoneToleranceTicks, czMaxZonesShown, lzLevelsAnalyzed, lzWallSizeThreshold, lzClusterSizeThreshold, lzZoneToleranceTicks, lzMaxWallsShown, lzMaxClustersShown, lzAutoScaleByInstrument, spEnabled, spWindowSeconds, spStackThreshold, lsEnabled, lsLookback, lsZThreshold, lsAtrPeriod, lsAtrMult, lsMaxWalls, lsMaxWallsShown, cfEnabled, cfLookback, cfMinR2, sqEnabled, sqExhaustionBars, sqExhaustionRangeFactor, sqGateLeadLagByR2, faEnabled, faFailBars, faTrappedBars, faTrappedTicks, fpEnabled, fpMaxBarsShown, fpImbalanceRatio, fpMinRowVolume, asRollingWindowSeconds, asLargePrintThreshold, asPressureThreshold, feSweepVolumeThreshold, feSweepWindowSeconds, feAbsorptionVolumeThreshold, feAbsorptionWindowSeconds, feMaxEventsShown, panelRefreshMs, showPanel, showConfluenceLines, showLiquidityLines, showStatWallLines, showCvdMarkers, showSequenceMarkers, showFailedAuctionMarkers, panelLeftOffset, panelTopOffset, panelMaxHeight);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.SneakyOrderFlowDashboard_v1_0_0 SneakyOrderFlowDashboard_v1_0_0(int mcTrendPeriod, int mcMomentumPeriod, int mcMacdFast, int mcMacdSlow, int mcMacdSignal, int mcHigherTimeframeMinutes, int mcContestedThreshold, int mcMarketStateLookback, double mcChoppyThreshold, double mcTrendyThreshold, int czProfileBucketTicks, int czValueAreaPercent, int czOpeningRangeMinutes, int czZoneToleranceTicks, int czMaxZonesShown, int lzLevelsAnalyzed, int lzWallSizeThreshold, int lzClusterSizeThreshold, int lzZoneToleranceTicks, int lzMaxWallsShown, int lzMaxClustersShown, bool lzAutoScaleByInstrument, bool spEnabled, int spWindowSeconds, int spStackThreshold, bool lsEnabled, int lsLookback, double lsZThreshold, int lsAtrPeriod, double lsAtrMult, int lsMaxWalls, int lsMaxWallsShown, bool cfEnabled, int cfLookback, double cfMinR2, bool sqEnabled, int sqExhaustionBars, double sqExhaustionRangeFactor, bool sqGateLeadLagByR2, bool faEnabled, int faFailBars, int faTrappedBars, int faTrappedTicks, bool fpEnabled, int fpMaxBarsShown, double fpImbalanceRatio, int fpMinRowVolume, int asRollingWindowSeconds, int asLargePrintThreshold, int asPressureThreshold, int feSweepVolumeThreshold, int feSweepWindowSeconds, int feAbsorptionVolumeThreshold, int feAbsorptionWindowSeconds, int feMaxEventsShown, int panelRefreshMs, bool showPanel, bool showConfluenceLines, bool showLiquidityLines, bool showStatWallLines, bool showCvdMarkers, bool showSequenceMarkers, bool showFailedAuctionMarkers, int panelLeftOffset, int panelTopOffset, int panelMaxHeight)
		{
			return indicator.SneakyOrderFlowDashboard_v1_0_0(Input, mcTrendPeriod, mcMomentumPeriod, mcMacdFast, mcMacdSlow, mcMacdSignal, mcHigherTimeframeMinutes, mcContestedThreshold, mcMarketStateLookback, mcChoppyThreshold, mcTrendyThreshold, czProfileBucketTicks, czValueAreaPercent, czOpeningRangeMinutes, czZoneToleranceTicks, czMaxZonesShown, lzLevelsAnalyzed, lzWallSizeThreshold, lzClusterSizeThreshold, lzZoneToleranceTicks, lzMaxWallsShown, lzMaxClustersShown, lzAutoScaleByInstrument, spEnabled, spWindowSeconds, spStackThreshold, lsEnabled, lsLookback, lsZThreshold, lsAtrPeriod, lsAtrMult, lsMaxWalls, lsMaxWallsShown, cfEnabled, cfLookback, cfMinR2, sqEnabled, sqExhaustionBars, sqExhaustionRangeFactor, sqGateLeadLagByR2, faEnabled, faFailBars, faTrappedBars, faTrappedTicks, fpEnabled, fpMaxBarsShown, fpImbalanceRatio, fpMinRowVolume, asRollingWindowSeconds, asLargePrintThreshold, asPressureThreshold, feSweepVolumeThreshold, feSweepWindowSeconds, feAbsorptionVolumeThreshold, feAbsorptionWindowSeconds, feMaxEventsShown, panelRefreshMs, showPanel, showConfluenceLines, showLiquidityLines, showStatWallLines, showCvdMarkers, showSequenceMarkers, showFailedAuctionMarkers, panelLeftOffset, panelTopOffset, panelMaxHeight);
		}

		public Indicators.SneakyOrderFlowDashboard_v1_0_0 SneakyOrderFlowDashboard_v1_0_0(ISeries<double> input , int mcTrendPeriod, int mcMomentumPeriod, int mcMacdFast, int mcMacdSlow, int mcMacdSignal, int mcHigherTimeframeMinutes, int mcContestedThreshold, int mcMarketStateLookback, double mcChoppyThreshold, double mcTrendyThreshold, int czProfileBucketTicks, int czValueAreaPercent, int czOpeningRangeMinutes, int czZoneToleranceTicks, int czMaxZonesShown, int lzLevelsAnalyzed, int lzWallSizeThreshold, int lzClusterSizeThreshold, int lzZoneToleranceTicks, int lzMaxWallsShown, int lzMaxClustersShown, bool lzAutoScaleByInstrument, bool spEnabled, int spWindowSeconds, int spStackThreshold, bool lsEnabled, int lsLookback, double lsZThreshold, int lsAtrPeriod, double lsAtrMult, int lsMaxWalls, int lsMaxWallsShown, bool cfEnabled, int cfLookback, double cfMinR2, bool sqEnabled, int sqExhaustionBars, double sqExhaustionRangeFactor, bool sqGateLeadLagByR2, bool faEnabled, int faFailBars, int faTrappedBars, int faTrappedTicks, bool fpEnabled, int fpMaxBarsShown, double fpImbalanceRatio, int fpMinRowVolume, int asRollingWindowSeconds, int asLargePrintThreshold, int asPressureThreshold, int feSweepVolumeThreshold, int feSweepWindowSeconds, int feAbsorptionVolumeThreshold, int feAbsorptionWindowSeconds, int feMaxEventsShown, int panelRefreshMs, bool showPanel, bool showConfluenceLines, bool showLiquidityLines, bool showStatWallLines, bool showCvdMarkers, bool showSequenceMarkers, bool showFailedAuctionMarkers, int panelLeftOffset, int panelTopOffset, int panelMaxHeight)
		{
			return indicator.SneakyOrderFlowDashboard_v1_0_0(input, mcTrendPeriod, mcMomentumPeriod, mcMacdFast, mcMacdSlow, mcMacdSignal, mcHigherTimeframeMinutes, mcContestedThreshold, mcMarketStateLookback, mcChoppyThreshold, mcTrendyThreshold, czProfileBucketTicks, czValueAreaPercent, czOpeningRangeMinutes, czZoneToleranceTicks, czMaxZonesShown, lzLevelsAnalyzed, lzWallSizeThreshold, lzClusterSizeThreshold, lzZoneToleranceTicks, lzMaxWallsShown, lzMaxClustersShown, lzAutoScaleByInstrument, spEnabled, spWindowSeconds, spStackThreshold, lsEnabled, lsLookback, lsZThreshold, lsAtrPeriod, lsAtrMult, lsMaxWalls, lsMaxWallsShown, cfEnabled, cfLookback, cfMinR2, sqEnabled, sqExhaustionBars, sqExhaustionRangeFactor, sqGateLeadLagByR2, faEnabled, faFailBars, faTrappedBars, faTrappedTicks, fpEnabled, fpMaxBarsShown, fpImbalanceRatio, fpMinRowVolume, asRollingWindowSeconds, asLargePrintThreshold, asPressureThreshold, feSweepVolumeThreshold, feSweepWindowSeconds, feAbsorptionVolumeThreshold, feAbsorptionWindowSeconds, feMaxEventsShown, panelRefreshMs, showPanel, showConfluenceLines, showLiquidityLines, showStatWallLines, showCvdMarkers, showSequenceMarkers, showFailedAuctionMarkers, panelLeftOffset, panelTopOffset, panelMaxHeight);
		}
	}
}

#endregion
