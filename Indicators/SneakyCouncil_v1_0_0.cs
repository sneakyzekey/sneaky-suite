// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
//
// Copyright (c) 2026 silentsudo-io and the Sentinel Suite contributors.
// ─────────────────────────────────────────────────────────────────────────────
//
// ═════════════════════════════════════════════════════════════════════════════
//  SNEAKY COUNCIL v1.1.1 — the chart CONFLUENCE ARBITER.
//
//  WHAT THIS IS
//  A clean-room implementation of the Council described in SENTINEL_SUITE_MANUAL.md
//  ("How the vote actually fuses"), built against the seams that ACTUALLY EXIST in
//  this installation. It reads every published …State on the SneakyCore bus for
//  this chart's scope, runs a WEIGHTED VOTE, applies modulators and hard vetoes,
//  and publishes exactly ONE CouncilState via SneakyCore.SetCouncilState().
//
//  THE FUSION (verbatim from the manual):
//      per voter:   vote = sign(dir) x weight       (neutral/absent -> no contribution)
//      netScore = SUM(vote)         activeW = SUM(weight) over DIRECTIONAL voters
//      Bias        = +1 if netScore >  deadband*activeW
//                    -1 if netScore < -deadband*activeW    else 0 (FLAT)
//      Conviction  = |netScore| / activeW  in [0,1]
//                    x breadth x squeeze  -> clamp [0,1]
//      SizeMult    = (Vetoed OR Conviction < floor) ? 0 : Conviction
//
//  ⚠ 2026-08-16 — CORRECTED. Everything below this line through "WEIGHTS" used to
//  say this Council was missing Eye/Intermarket/Clock/Participation/Location/Mtf/
//  Brick because none of those sensors existed "in this install" when v1.0.0 was
//  first written (2026-08-01/02). That stopped being true in pieces across
//  2026-08-04 through 2026-08-12 (off-chart gate, Intermarket/Clock/Participation/
//  Location/Mtf/Brick all wired, ARCH ported, TFUS/FLUX added) but this header was
//  never updated to say so — the exact "file says vX, code has been vY for days"
//  lie the real Sentinel Suite's own Council v1.11.0 changelog called out about
//  ITSELF. Caught 2026-08-16 doing a voter-roster audit against the real Council —
//  fixed here rather than left to mislead the next reader the way it misled the
//  audit that found it.
//
//  ACTUAL STATE AS OF v1.1.1 — panel of 25 declared voters (TRND CCI ADX STRC SPRT
//  STF PSAR AVMA VDYA COMP WAE GREV EXH HARM ZSC BRK CVB FLOW PRS FLUX TFUS ARCH
//  IMKT VENV CVD), one experimental/off (EYE). Checked voter-for-voter against the
//  real Sentinel Suite's own Council_v1_11_0.cs (24 declared voters): every one of
//  its voters is now present here EXCEPT CVD, which THIS version adds (see the CVD
//  AddVote site + CHANGELOG below) — that closes the roster gap. TFUS and the
//  experimental EYE toggle exist only here, not in the real Council (TFUS is this
//  build's own original design; EYE was deliberately left OUT of the real Council
//  too — its own Eye is built FROM Pressure/Intermarket/Drift-Conviction, so voting
//  it separately double-counts evidence already cast as PRS/IMKT/CVB, which is
//  exactly why it stays off by default here). Clock/Intermarket are hard vetoes
//  and a voter respectively (not both a vote AND a damp, unlike the real Council's
//  fuller modulator chain); Participation/Location are conviction damps, not votes,
//  matching the real Council's own classification of them as non-directional.
//
//  WEIGHTS — WHERE THEY CAME FROM, AND WHAT THAT MEANS
//  Voters that appear in the manual's table use the manual's published defaults
//  (Trend 1.0, CCI 0.8 x1.5-if-strong, ADX 0.6 x1.25-if-strong, VolEnvelope 0.6,
//  Compression 0.7, WAE 0.7). Every OTHER weight below is MINE — a reasoned guess,
//  not a published or backtested value, and deliberately set LOW (0.3-0.5) because
//  they are mostly redundant trend restatements. The manual is blunt about this and
//  it bears repeating: most voters here are price-derived, so when they agree that
//  is not many witnesses confirming a fact, it is one witness saying the same thing
//  many ways. Redundant agreement inflates conviction without adding information.
//  Treat every weight as a HYPOTHESIS to be graded against real results.
//
//  CHANGELOG
//    v1.1.0 (in-place, 2026-08-16) — CVD VOTER ADDED, closing the last voter-roster gap against the
//             real Sentinel Suite's own Council v1.11.0 (audited voter-for-voter, see the corrected
//             header above). New tag "CVD": SneakyCVD's session-scale cumulative-delta direction
//             (SneakyCore.CvdState, Core v1.43.0 — Slope/SlopeZ/Dir/Divergence/EfficiencyZ). Distinct
//             from FLOW (a tick-rule regression REGIME read) and FLUX (ONE forming bar, resets every
//             close) — this is the whole SESSION's accumulated delta. Same non-price-derived family as
//             PRS/CVB/FLUX: tape-confirmed, not price-derived. TickBacked gates the vote (same
//             discipline as PRS/BSP — a historical rebuild's OHLC-proxy fallback IS price-derived,
//             voting it would launder a price signal in as order flow). Damped on Divergence (flow
//             opposing the bar's own price direction), same idiom as FLUX/PRS — deliberately NOT
//             cvd.Absorbing(cvd.Dir): PRS's own Absorbing() helper was found dead-code-by-construction
//             (Core v1.45.1) and replaced with a direct Divergence check; reusing the untested sibling
//             helper here rather than the already-fixed pattern would risk the same class of bug.
//             Scope-keyed (CvdState's own Core doc comment: "CVD accrues per bar, so it IS bar-type
//             dependent") → off-chart-gated like every other chart-derived voter, prefix "SneakyCVD_".
//             Enters at WeightCvd = 0.0 — brand new, never voted on this Council before, same audition
//             discipline as every other freshly-wired voter (Flux/TrendFusion/Arch all also ship at
//             0.0) — and matches the real Council's own CVD default, which ships at 0.0 too pending a
//             graded audition. Not touched by ApplyWeightProfile() (Prior or Learned): no training
//             data exists for a voter that has never cast a vote, same treatment as WeightBrick/
//             WeightConviction/WeightFlux. NOTHING ELSE in the fusion, veto, or publish path changed.
//    v1.1.1 (in-place, 2026-08-16) — REWIRED onto CouncilFusion.Fuse() for the core denomW/bias/raw-
//             conviction computation (see the FUSE section's own comment for the full reasoning).
//             This build's own AddOns/CouncilFusion.cs — a byte-for-byte port of the real Council's
//             shared fusion core, sitting unused since 2026-08-06 — is now actually CALLED, so a bug
//             fix to that shared math benefits this Council and any future replay harness built on
//             it at once, instead of drifting the way two hand-copies of the same formula always
//             eventually do. BEHAVIOUR-PRESERVING BY CONSTRUCTION, not by re-test: Inputs.Declared is
//             built to reproduce this file's own prior STATE-always / TRIGGER-fired-only dilution
//             rule exactly (see the FUSE comment point 1 — this file's actual code and its own prose
//             comment disagreed on that rule; the code's behaviour is what's preserved), and every
//             modulator + veto downstream of the fuse call is untouched, still hand-written, still
//             operating on the same two numbers (bias, conviction) it always did. No NinjaTrader
//             compiler available in this environment to verify the build — inspect for syntax errors
//             on first load same as any hand-edited NinjaScript file.
//    v1.0.0 (2026-08-01/02) — initial clean-room build (see the corrected header above for the real,
//             current state of what's wired — this entry is kept for the record, not as current truth).
//
//  PLACES NO ORDERS. Read-only. Publishes a seam and draws a card.
// ═════════════════════════════════════════════════════════════════════════════

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.AddOns.Sneaky;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors
{
	// 2026-09-06 -- Conditional Properties-grid visibility for the per-profile Flip Override
	// groups (0b/0c/0d/0e): only the group matching the currently-selected WeightProfile is
	// shown, the other three stay hidden. Uses NinjaTrader's own documented, production-proven
	// pattern for this -- a TypeConverter deriving from IndicatorBaseConverter, the same
	// technique the platform's own built-in Pivots indicator uses -- referenced here by string
	// per NinjaTrader's own IndicatorBaseConverter/StrategyBaseConverter docs, since NinjaScript's
	// dynamic-compile model doesn't reliably resolve a typeof() reference here. Deliberately NOT
	// the separate reflection-based "dynamic properties" sample -- NinjaTrader's own support forum
	// has flagged that one as unstable across platform updates (properties throwing "Unable to
	// create instance" / indicators disappearing from charts after a recompile). See
	// WeightProfileConverter at the bottom of this class for the actual filtering logic.
	[TypeConverter("NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors.WeightProfileConverter")]
	public class SneakyCouncil_v1_0_0 : Indicator
	{
		private const string COUNCIL_VERSION = "1.1.1";   // 2026-08-16 — CVD voter (1.1.0), then rewired onto
		                                                   // CouncilFusion.Fuse() (1.1.1). See file header CHANGELOG.
		                                                   // Filename/class stay SneakyCouncil_v1_0_0 on purpose: a
		                                                   // real rename would force every chart to re-add the
		                                                   // indicator and lose its saved WeightProfile/InstanceTag
		                                                   // settings. This const is what actually publishes into
		                                                   // CouncilState.CouncilVersion and the card, so consumers
		                                                   // (and Lens, when it grades this) see the true version.
		private const int   CHIPS_PER_ROW = 5;
		private const float CHIP_H        = 13f;

		private string _scope;
		private string Scope()
		{
			try { _scope = SneakyCore.ScopeOf(Instrument, BarsPeriod, ChartControl); } catch { }   // 2026-08-04 — ALWAYS recomputed, never permanently cached: a chart's lane can register AFTER this indicator's first call, so caching once risked freezing a stale, unlaned scope forever (confirmed happening for real tonight). Cheap to recompute every call; only overwrites _scope on success, so a transient throw keeps the previous good value.
			return _scope;
		}

		// 2026-08-04 — SELF-HEALING LANE ASSERT. RegisterLane used to fire exactly ONCE, in
		// State.DataLoaded. Fine when DataLoaded fires promptly — but a recompile/chart-reload can
		// delay it arbitrarily (sentinel.log showed one lane sit unregistered for ~11 minutes across
		// a reload burst while this chart's own sensors correctly, but uselessly, kept recomputing
		// Scope() against an empty lane). Re-asserting here closes that window to one throttle tick
		// no matter how slow DataLoaded is or how many times it re-fires. Cheap (a lock + dict set);
		// throttled anyway so a hot bar stream never lock-contends on it.
		private int _lastLaneAssertMs;
		private const int LaneAssertMs = 2000;
		private void AssertLane()
		{
			int now = Environment.TickCount;
			if (unchecked(now - _lastLaneAssertMs) < LaneAssertMs) return;
			_lastLaneAssertMs = now;
			try { SneakyCore.RegisterLane(ChartControl, InstanceTag); }
			catch (Exception _sx) { SneakyCore.Swallow("SneakyCouncil.AssertLane", _sx); }
		}

		// ═══════════════════════════════════════════════════════════════════════
		//  OFF-CHART GATE — 2026-08-07, ported from the real Sentinel Suite's own
		//  Council v1.11.0 (2026-07-29 "REAL FORK" — their own words: "a standard
		//  OHLC sensor... is voting on Council without being on the chart. It is
		//  not weighted and poisoning the Council."). Confirmed via direct source
		//  check the SAME night this was ported that Sneaky's chart-derived
		//  sensors currently all publish under lane-qualified Scope() (not a bare
		//  instrument key), so this specific leak is NOT currently firing here —
		//  but that protection depends entirely on every sensor's scope string
		//  staying correct forever, with no independent backstop. This gate is
		//  that backstop: even if a future edit reintroduces a bare-scope publish
		//  (exactly the class of mistake already found and fixed three times
		//  tonight elsewhere), a chart-derived sensor still cannot vote/veto/damp
		//  unless its OWN publisher is physically attached to THIS chart.
		//
		//  DELIBERATELY NOT a full rebase onto v1.11.0's own architecture — that
		//  was tried, reintroduced regressions (missing CouncilFusion/Voter-
		//  Catalog, silently-dropped InstanceTag/PreVetoBias/KillWindowActive,
		//  and a real UI regression: the invisible-scroll-card bug this suite
		//  already fixed once via ConductorRender, lost because that fix lived
		//  only in Sneaky's own build). This port takes ONLY the gate itself,
		//  self-contained, no RosterIO/LaneIO/Roster.conf dependency, onto the
		//  CURRENT working v1.0.0 base — everything else stays exactly as it was.
		//
		//  Wiring: RefreshOnChartTypes() is called once per bar (see OnBarUpdate).
		//  Every chart-derived consult is wrapped as
		//  `Local("TAG", SneakyCore.GetXState(...))` instead of a bare call — a
		//  one-line change per site reusing the null-check every consult already
		//  had, exactly the real project's own design ("nulling it here
		//  suppresses the vote AND every veto/damp that hangs off the same null
		//  check").
		// ═══════════════════════════════════════════════════════════════════════
		private static readonly Dictionary<string, string> ChartDerivedPublisher = new Dictionary<string, string>
		{
			{ "TRND",   "SneakyTrend_" },          // trailing _ : "SneakyTrend" alone would also prefix-match
			                                        // a future SneakyTrendArchitect, same collision the real
			                                        // project's own dictionary comment warns about
			{ "CCI",    "SneakyWoodiesCCIPro" },
			{ "ADX",    "SneakyADXPro" },
			{ "ENV",    "SneakyVolEnvelope" },      // also drives DampenOnSqueeze
			{ "CMP",    "SneakyCompressionBase" },
			{ "WAE",    "SneakyWAE" },
			{ "GREV",   "SneakyGodReversal" },
			{ "STF",    "SneakyStochasticTripleFilter" },   // also drives VetoOnChop — the real project's own
			                                                 // originally-reported bug
			{ "STRC",   "SneakyStructure" },
			{ "EXH",    "SneakyExhaustion" },
			{ "AVMA",   "SneakyADXVMA" },
			{ "SPRT",   "SneakySuperTrend" },
			{ "PSAR",   "SneakyParabolicSAR" },
			{ "ZSC",    "SneakyZScore" },
			{ "VDYA",   "SneakyVIDYA" },
			{ "HARM",   "SneakyHarmonic" },
			{ "RGME",   "SneakyRegime" },
			{ "PARTIC", "SneakyParticipation_" },
			{ "MTF",    "SneakyMtf_" },
			{ "TFUS",   "SneakyTrendFusion_" },     // Sneaky-specific — plays ARCH's role (a genuine per-chart
			                                        // composite reading), not part of the real dictionary
			{ "ARCH",   "SneakyTrendArchitect" },   // 2026-08-07 — the real PRISM/Trend Architect, now ported.
			                                        // No trailing underscore needed: "SneakyTrendArchitect..."
			                                        // never collides with "SneakyTrend_" (TRND) or
			                                        // "SneakyTrendFusion_" (TFUS) — different characters right
			                                        // after "SneakyTrend" in every case.
			{ "CVD",    "SneakyCVD_" },             // 2026-08-16 — new. CvdState is scope-keyed (accrues per bar,
			                                        // per its own Core doc comment), so it goes through the same
			                                        // gate as every other chart-derived voter. No known collision
			                                        // risk, but the trailing underscore costs nothing and matches
			                                        // the defensive style used for TRND/PARTIC/MTF/TFUS above.
		};

		private readonly HashSet<string> _onChartTypes = new HashSet<string>(StringComparer.Ordinal);
		private readonly object _onChartGate = new object();
		private bool     _onChartResolved;          // has a scan STABILIZED? (before that we cannot judge)
		private DateTime _onChartScanUtc = DateTime.MinValue;
		private readonly HashSet<string> _skippedOffChart = new HashSet<string>(StringComparer.Ordinal);
		// 2026-08-09 — one prior non-empty scan, kept ONLY to compare against the next one. NinjaTrader
		// attaches a chart's ~20+ indicators one at a time on load, not atomically -- a scan that
		// happens to run while that's still in progress is genuinely non-empty, just incomplete, so
		// the empty-scan guard alone doesn't catch it. Requiring two CONSECUTIVE scans to agree before
		// trusting the result (rather than trusting the first non-empty one outright) waits out that
		// startup window instead of locking in whichever partial snapshot happened to land first.
		private HashSet<string> _onChartPending;

		/// <summary>Rebuild the set of indicator TYPE names attached to this chart. UI thread only —
		/// enumerating ChartControl.Indicators off the UI thread is the documented way to throw here.
		/// Fired async from the data thread; never blocks a bar.</summary>
		private void RefreshOnChartTypes()
		{
			var cc = ChartControl;
			if (cc == null) return;
			if ((DateTime.UtcNow - _onChartScanUtc).TotalSeconds < 5.0) return;
			_onChartScanUtc = DateTime.UtcNow;
			try
			{
				cc.Dispatcher.InvokeAsync(() =>
				{
					try
					{
						var found = new HashSet<string>(StringComparer.Ordinal);
						foreach (var ind in cc.Indicators)
						{
							if (ind == null) continue;
							found.Add(ind.GetType().Name);
						}
						lock (_onChartGate)
						{
							// Council itself is ALWAYS in cc.Indicators (it's the code asking the
							// question), so a genuinely empty result is suspect on every scan, not
							// just the first one -- almost certainly caught mid-reload, not truth.
							if (found.Count == 0) { return; }

							// 2026-08-09 — require TWO CONSECUTIVE scans to agree before committing.
							// A non-empty-but-incomplete snapshot (chart still attaching its other
							// ~20 indicators one at a time, whether on initial load or after adding
							// one mid-session) would otherwise get accepted as truth immediately, the
							// same failure shape as the empty case but not caught by that guard. This
							// waits out the startup/reload window instead of trusting whichever
							// partial snapshot happened to land first.
							if (_onChartPending != null && _onChartPending.SetEquals(found))
							{
								_onChartTypes.Clear();
								foreach (var s in found) _onChartTypes.Add(s);
								_onChartResolved = true;
								_onChartPending = null;
								// 2026-08-09 DIAGNOSTIC — three rounds of timing-based fixes haven't resolved a
								// STABLE wrong answer (two agreeing scans, still missing most sensors), which
								// rules out timing/race explanations entirely. Logging exactly what cc.Indicators
								// actually returns on a confirmed commit, so the next fix is based on real
								// evidence instead of another guess. Safe to remove once the real cause is found.
								try
								{
									var all = new string[found.Count];
									found.CopyTo(all);
									Array.Sort(all, StringComparer.Ordinal);
									SneakyCore.Log("Council.OnChartDiag", "resolved " + found.Count + " types: " + string.Join(",", all));
								}
								catch { }
							}
							else
							{
								_onChartPending = found;
							}
						}
					}
					catch (Exception _sx) { SneakyCore.Swallow("Council.RefreshOnChartTypes.ui", _sx); }
				});
			}
			catch (Exception _sx) { SneakyCore.Swallow("Council.RefreshOnChartTypes", _sx); }
		}

		/// <summary>Is the publisher of this chart-derived voter actually attached to THIS chart?</summary>
		private bool OnChart(string tag)
		{
			if (!RequireSensorOnChart) return true;              // opt-out switch; gate itself defaults ON
			string prefix;
			if (!ChartDerivedPublisher.TryGetValue(tag, out prefix)) return true;   // instrument-level / bar-type
			// No chart at all (headless / harness): attachment is UNDEFINED, not false. Failing closed here
			// would silently mute a Council that has no chart to be attached to.
			if (ChartControl == null) return true;
			lock (_onChartGate)
			{
				// Not scanned yet: fail CLOSED for a beat rather than let an off-chart sensor vote once.
				if (!_onChartResolved) { _skippedOffChart.Add(tag); return false; }
				foreach (var t in _onChartTypes)
					if (t.StartsWith(prefix, StringComparison.Ordinal)) return true;
				_skippedOffChart.Add(tag);
				return false;
			}
		}

		/// <summary>Wrap a seam read: hand back the state only if its publisher is on this chart.
		/// Nulling it here suppresses the vote AND every veto/damp that hangs off the same null check.</summary>
		private T Local<T>(string tag, T state) where T : class
		{
			return OnChart(tag) ? state : null;
		}

		// ── verdict, kept for the card + the plots ──
		private int    _bias;
		private double _conviction, _sizeMult, _netScore, _activeW;
		private int    _agree, _disagree, _voters;
		private bool   _vetoed;
		private string _vetoReason;
		private string _reasons = "";
		private readonly Dictionary<string,int>    _votes = new Dictionary<string,int>();
		private readonly Dictionary<string,double> _voteW = new Dictionary<string,double>();
		private readonly List<KeyValuePair<string,int>> _chips = new List<KeyValuePair<string,int>>();
		// 2026-08-07 — signed-conviction sparkline history, ported from the real Council's own card
		// (confirmed it's a real, deliberate feature there, not a screenshot artifact — its own
		// SneakySkin.Painter.Sparkline() helper already exists in this build, nothing to port for
		// that part). Signed so the line reads above the midline on long conviction, below on short.
		private readonly List<double> _convHist = new List<double>();
		private const int HistMax = 48;

		// Debounce state for the pulse plots: the last ACTIONABLE bias we fired on.
		// 0 = not currently actionable, so the next actionable verdict in either direction fires.
		private int _prevFiredBias;
		private string _modLabel = "—";
		private bool _setupPrinted;

		private SneakySkin.Painter _sp;
		private DateTime _lastRenderExLog = DateTime.MinValue;

		// 2026-08-19 — card health, added after a live incident where the corpus that would have
		// CONFIRMED Council's Buy/Sell computation kept updating correctly during a specific window
		// had itself gone dark for unrelated reasons (a stale offline sync, not a Council problem) —
		// but there was no way to tell that from the chart alone; the card just kept rendering,
		// which proves OnRender() is being called, not that OnBarUpdate()'s actual computation is.
		// _lastGoodBarAt is wall-clock, set as the LAST statement of a fully-completed OnBarUpdate
		// pass — an uncaught exception anywhere above it means this bar's stamp never lands, so
		// "stale" here means the real thing: this indicator hasn't finished a full per-bar pass
		// recently, not just "no new bar has formed yet" (which during quiet/closed hours is normal
		// and expected, not a fault — this is informational, the trader judges which it is).
		private DateTime _lastGoodBarAt = DateTime.MinValue;
		private const double HeartbeatWarnMinutes = 10.0;
		// _lastPublishOk mirrors the suite's "NO REC" pattern for the ONE thing Council itself writes:
		// its published seam state (SetCouncilState), which Confluence/Recorder/Cockpit/everything
		// else reads. Unlike Recorder's file-write failure (usually permanent — a broken sink stays
		// broken), a publish attempt runs fresh every bar, so this self-heals the moment a publish
		// succeeds again rather than latching forever off one transient failure.
		private bool _lastPublishOk = true;
		private DateTime _lastPublishFailLog = DateTime.MinValue;
		private void NotePublishFail(Exception ex)
		{
			_lastPublishOk = false;
			if ((DateTime.UtcNow - _lastPublishFailLog).TotalMilliseconds < 15000) return;
			_lastPublishFailLog = DateTime.UtcNow;
			try { Print("[SneakyCouncil] publish failed — downstream (Confluence/Recorder/Cockpit) is reading a STALE seam: " + ex.Message); } catch { }
		}

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "The Sneaky Council — chart confluence arbiter. Reads every published sensor "
					+ "seam on the SneakyCore bus for this scope, runs a weighted vote (manual's fusion math), "
					+ "applies squeeze/breadth/in-value modulators and hard vetoes (liquidity wall, structural level, MTF opposed, chop, high-vol regime, kill window), "
					+ "and publishes ONE CouncilState (bias / conviction / sizeMult) that any executor can consult "
					+ "instead of re-deriving confluence. Places no orders.";
				Name                     = "Sneaky Council v1.0.0";   // 2026-08-21 — pinned to a hardcoded literal
				                                                       // matching the class/file name on purpose.
				                                                       // Building this from COUNCIL_VERSION made the
				                                                       // indicator-picker list show "v1.1.1" while
				                                                       // every chart's Configured list still showed
				                                                       // "SneakyCouncil_v1_0_0" — same indicator, two
				                                                       // different-looking names, confusing to read.
				                                                       // COUNCIL_VERSION keeps tracking the real
				                                                       // logical version (see file header CHANGELOG,
				                                                       // and the seam-state provenance fields below)
				                                                       // — it just no longer feeds the on-screen Name.
				Calculate                = Calculate.OnBarClose;
				IsOverlay                = true;
				DisplayInDataBox         = true;
				DrawOnPricePanel         = true;
				PaintPriceMarkers        = false;
				IsSuspendedWhileInactive = false;
				IsAutoScale              = false;   // Bias/Conviction plots must never drag the price scale
				ShowIndicatorLabel       = false;

				StaleSec        = 90.0;  // was 5.0 — matches Trend/Intermarket/Eye's convention. Safe when every
										  // scope-keyed source lives on the same chart (bar closes are near-
										  // simultaneous); only matters if a source runs on a slower/different
										  // chart. Costs nothing here, removes a latent fragility either way.
				Deadband        = 0.15;   // manual default
				ConvictionFloor = 0.2;   // 2026-08-07 — updated to the real reference build's value (was 0.35).
				// 2026-08-12 -- this single property now also carries the Learned-profile floor, same as
				// every Weight* property already does -- ApplyWeightProfile() (called from State.Configure)
				// overwrites it to 0.10 under Learned. No separate ConvictionFloorLearned property anymore;
				// having two labeled fields for one runtime value was inconsistent with how every other
				// profile-dependent setting in this file already works, and confusing for exactly that reason.
				MinVoters       = 3;   // 2026-08-07 — updated to the real reference build's value (was 4)
				// Signal values are independently settable so this can speak EITHER convention:
				//   +1 / +1  → the Sneaky suite's own (SneakyConfluence, SneakyWAE)
				//   +1 / -1  → PredatorX's LongEntryPlotValue=1 / ShortEntryPlotValue=-1
				BuySignalValue  =  1.0;
				SellSignalValue =  1.0;

				DrawSignalTags          = true;
				SignalTagPrefix         = "SCncl";
				InstanceTag             = "";
				SignalMarkerOffsetTicks = 4;

				// ── Weights: manual-published / reference-build values — the safe PRIOR profile.
				// This is the baseline SetDefaults value regardless of WeightProfile below: a brand
				// new chart attachment, or any chart that's never touched WeightProfile, always starts
				// here. See ApplyWeightProfile() (called from State.Configure) for how WeightProfile
				// actually switches a given chart instance onto the LEARNED numbers instead.
				WeightTrend       = 1.0;
				WeightCci         = 0.8;
				WeightAdx         = 0.6;
				WeightEnvelope    = 0.6;
				WeightCompression = 0.7;
				WeightWae         = 0.2;

				WeightBrick       = 0.5;
				WeightConviction  = 0.6;
				WeightPressure    = 0.0;
				WeightIntermarket = 0.6;
				WeightFlux        = 0.7;
				WeightTrendFusion = 0.0;
				WeightArch        = 0.0;
				WeightCvd         = 0.0;   // 2026-08-16 — new, audition mode (see CVD AddVote site)
				WeightFlow        = 0.9;
				WeightStructure   = 0.7;
				WeightSuperTrend  = 0.7;
				WeightStf         = 0.0;
				WeightSar         = 0.5;
				WeightAdxvma      = 0.6;
				WeightVidya       = 0.5;
				WeightGodReversal = 0.9;
				WeightExhaustion  = 0.5;
				WeightHarmonic    = 0.4;
				WeightZScore      = 0.2;

				// PER-CHART PROFILE (2026-08-11): lets one chart run the 2026-08-11 LEARNED weights
				// (train_council_weights.py, after-lane-fix corpus, learned beat prior in all 4
				// out-of-sample folds) while another chart keeps running the PRIOR values above —
				// exactly the point of running multiple Council instances to compare configurations.
				// This is a per-instance NinjaScript property like any other: each chart's own saved
				// value is independent, so setting this to Learned on ONE chart's Properties dialog
				// does not touch any other chart, and never requires a second compiled copy of this
				// file. To go back to the safe baseline on a chart: reopen its Properties, set this
				// back to Prior, OK/Apply — no recompile needed either way.
				WeightProfile = SneakyWeightProfile.Prior;

				// 2026-08-29 — PER-VOTER FLIP OVERRIDE defaults. All true so an existing chart
				// (E, or any other already-saved Learned-profile instance) behaves IDENTICALLY
				// after this recompile — these are brand-new properties, so NinjaTrader initializes
				// them from SetDefaults() on load rather than from old saved XML that doesn't have
				// them yet. See the LearnedFlip* properties below for why: the lab's 2026-08-28
				// flip-check (sneaky_lab_check, section 3) found 8 of these 11 flips currently read
				// CONTRARIAN on live-Learned data (TRND, ADX, PSAR, WAE, ZSC, TFUS, ARCH, VENV) —
				// only STRC, STF, VDYA read correctly signed. That diagnostic's own effN (795) is
				// "directional, not decisive," so this does NOT change any flip's default — it only
				// adds the capability to test un-flipping specific voters on a Learned-profile chart
				// without touching the others or committing to the whole diagnosis at once.
				LearnedFlipTrnd = true; LearnedFlipAdx  = true; LearnedFlipStrc = true; LearnedFlipStf  = true;
				LearnedFlipPsar = true; LearnedFlipVdya = true; LearnedFlipWae  = true; LearnedFlipZsc  = true;
				LearnedFlipTfus = true; LearnedFlipArch = true; LearnedFlipVenv = true;

				// Learned3 (2026-09-05) flip defaults: NOT a new fit — same Learned weight magnitudes
				// as above, applied to a SEPARATE profile so it can be A/B'd against Learned on its own
				// chart without touching Learned's own saved instances. The 8 tags the lab's 2026-08-28
				// flip-check (sneaky_lab_check, section 3) found reading CONTRARIAN under Learned's
				// current flip=true default (TRND, ADX, PSAR, WAE, ZSC, TFUS, ARCH, VENV) default to
				// false here instead — i.e. the opposite of Learned's own default for those 8. STRC/
				// STF/VDYA read correctly signed under Learned already, so they keep flip=true here too.
				// Repeating that diagnostic's own caveat: effN=795 is "directional, not decisive" — this
				// is an untested alternative to compare on sim/small size, not a proven correction.
				Learned3FlipTrnd = false; Learned3FlipAdx  = false; Learned3FlipStrc = true;  Learned3FlipStf  = true;
				Learned3FlipPsar = false; Learned3FlipVdya = true;  Learned3FlipWae  = false; Learned3FlipZsc  = false;
				Learned3FlipTfus = false; Learned3FlipArch = false; Learned3FlipVenv = false;

				// Learned1 (2026-08-29) flip defaults: true where that voter's F-only fitted coefficient
				// came back negative (needs inverting relative to Prior's raw, un-flipped direction),
				// false where it was already positive. TRND and ZSC were both near-zero/negligible
				// (+0.104, -0.004) — defaulted false (no flip) since the fitted edge is too small to
				// act on either way. See ApplyWeightProfile()'s Learned1 branch for the magnitudes.
				Learned1FlipTrnd = false; Learned1FlipAdx  = true;  Learned1FlipStrc = true;  Learned1FlipStf  = true;
				Learned1FlipPsar = false; Learned1FlipVdya = false; Learned1FlipWae  = true;  Learned1FlipZsc  = false;
				Learned1FlipTfus = true;  Learned1FlipArch = false; Learned1FlipVenv = false;
				Learned1FlipSprt = true;  Learned1FlipCci  = true;  Learned1FlipComp = true;
				Learned1FlipGrev = true;  Learned1FlipExh  = true;  Learned1FlipPrs  = true;

				// Learned2 flip defaults: identical to Learned1's, above — same underlying fit, this
				// profile only changes WHICH voters are active and how much weight the survivors get.
				Learned2FlipTrnd = false; Learned2FlipAdx  = true;  Learned2FlipStrc = true;  Learned2FlipStf  = true;
				Learned2FlipPsar = false; Learned2FlipVdya = false; Learned2FlipWae  = true;  Learned2FlipZsc  = false;
				Learned2FlipTfus = true;  Learned2FlipArch = false; Learned2FlipVenv = false;
				Learned2FlipSprt = true;  Learned2FlipCci  = true;  Learned2FlipComp = true;
				Learned2FlipGrev = true;  Learned2FlipExh  = true;  Learned2FlipPrs  = true;

				// Learned4 flip defaults: identical to Learned2's, above — Learned4 only changes WHICH
				// voters are active (3 more redundant-pair members zeroed: SPRT, VDYA, and — no toggle
				// exists for FLOW, it never had a sign ambiguity — see the enum doc-comment above and
				// ApplyWeightProfile()'s Learned4 branch). A flip choice on a zero-weighted voter is inert
				// (AddVote's weight<=0 guard drops the vote regardless of sign) but copied anyway so the
				// property panel always shows a concrete saved value, same discipline as every other profile.
				Learned4FlipTrnd = false; Learned4FlipAdx  = true;  Learned4FlipStrc = true;  Learned4FlipStf  = true;
				Learned4FlipPsar = false; Learned4FlipVdya = false; Learned4FlipWae  = true;  Learned4FlipZsc  = false;
				Learned4FlipTfus = true;  Learned4FlipArch = false; Learned4FlipVenv = false;
				Learned4FlipSprt = true;  Learned4FlipCci  = true;  Learned4FlipComp = true;
				Learned4FlipGrev = true;  Learned4FlipExh  = true;  Learned4FlipPrs  = true;

				// NOT applied — no matching AddVote/AddVoteTrigger tag exists anywhere in this file,
				// so there is no property to set: BSP (learned -45.885, the single largest weight in
				// the whole table — likely SneakyBuySellVolumePressureMountain publishing into
				// recorded cnlVotes through some path outside this file's own voting logic, worth
				// tracking down separately given its size), ENV (learned -1.400, distinct discovered
				// tag from this file's own VENV), CMP (learned +0.000, distinct from this file's own
				// COMP tag).

				// ── Vetoes / modulators ──
				VetoLiquidityWall = true;
				WallVetoTicks     = 8;
				VetoNearLevel     = true;
				LevelVetoAtr      = 0.25;   // 2026-08-07 — updated to the real reference build's value (was 0.5)
				VetoMtfOpposed    = false;
				VetoChop          = true;
				VetoHighVolRegime = false;   // a damp in the manual, not a veto — off by default here
				RequireSensorOnChart = true;   // 2026-08-07 — ON by default. This is closing a real leak
				                                // (a bug fix), not an experimental gate — see property doc.
				VetoNormalRegime  = false;   // 2026-08-06 — new, off by default until validated on fresh data
				VetoKillWindow    = false;   // new — off by default, same as every newly-added gate
				VetoNews          = false;   // 2026-08-08 — same discipline: off by default until validated
				SqueezeDamp       = 0.6;
				DryUpDamp         = 0.75;
				ProfileValueAreaDamp = 1.00;   // 2026-08-07 — disabled, matches the real reference build (was 0.85)
				ProfileOverextendedDamp = 1.0;   // OFF by default (1.00 disables) — new 2026-08-06, validate before enabling
				ActiveWeightCap     = 999.0;   // 2026-09-16 — new, effectively unreachable (unused until lowered)
				ActiveWeightCapDamp = 1.0;     // OFF by default (1.00 disables) — unvalidated on held-out data, see property doc

				// EYE — off by default (see the AddVote comment for why). Half PRS/IMKT/CVB/FLOW's
				// 0.8 to reflect that it's a recombination of them, not a 5th independent voice.
				IncludeEye = false;
				WeightEye  = 0.0;   // 2026-08-07 — updated to the real reference build's value

				ShowSetupHelp = true;
				ShowCard   = true;
				CardCorner = SneakyCardCorner.TopRight;

				// Transparent so the Deck's generic plot-reader can arm off them without
				// painting anything on the price panel.
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Bias");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Conviction");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SizeMult");

				// Appended AFTER the state plots so Bias/Conviction/SizeMult keep indices 0/1/2.
				// Transparent ON PURPOSE. These carry a SIGNAL VALUE (0 / ±1), not a price,
				// so NinjaTrader plots them at y=0 and y=±1 — thousands of points below price
				// on the panel. The values stay readable by PredatorX and any plot-reader;
				// the VISIBLE markers come from the Draw.Text signal tags — literal "SSCB"/"SSCS"
				// text (2026-08-19 — was Draw.Dot, then briefly arrows; settled on text so it
				// doesn't collide with other indicators on the chart that already plot triangles),
				// which are placed at Low[0]-offset / High[0]+offset — real price levels, next to
				// the bar. Same text and color as Confluence's own tags — deliberately not
				// distinguished from Council's here.
				AddPlot(new Stroke(Brushes.Transparent, 3), PlotStyle.Dot, "SneakyCouncilBuy");
				AddPlot(new Stroke(Brushes.Transparent, 3), PlotStyle.Dot, "SneakyCouncilSell");
			}
			else if (State == State.Configure)
			{
				// Runs after THIS chart's own saved properties (including WeightProfile) are loaded,
				// so it correctly picks up whichever profile this specific instance has saved —
				// independent of every other chart running this same indicator.
				ApplyWeightProfile();
			}
			else if (State == State.DataLoaded)
			{
				if (!ShowIndicatorLabel) Name = string.Empty;
				// v2026-08-04 — registers this chart's lane (if InstanceTag is set) so every OTHER
				// seam publisher/consumer on this SAME chart automatically resolves to the same
				// lane-qualified scope, with zero per-indicator config on their end. A blank
				// InstanceTag registers nothing — ScopeOf then behaves exactly as it always has.
				// (Backstopped by AssertLane() in OnBarUpdate — see its comment — so a slow or
				// re-fired DataLoaded no longer leaves the lane unregistered for the interim.)
				try { SneakyCore.RegisterLane(ChartControl, InstanceTag); }
				catch (Exception _sx) { SneakyCore.Swallow("SneakyCouncil.DataLoaded.RegisterLane", _sx); }
			}
			else if (State == State.Terminated)
			{
				// Council never had Terminated cleanup at all before this — the Painter below was
				// leaking on every chart removal/reload, and the lane (once registered) would have
				// stuck around pointing at a dead ChartControl forever.
				if (_sp != null) { try { _sp.Dispose(); } catch { } _sp = null; }
				try { SneakySkin.CardLayout.Release(this); } catch { }
				try { SneakyCore.ClearLane(ChartControl); } catch { }
			}
		}

		// ── per-chart weight profile (2026-08-11) ──────────────────────────────
		/// <summary>Sets every Weight* property to WeightProfile's numbers. Called from State.Configure,
		/// so it runs once per (re)configure using THIS instance's own saved WeightProfile — safe to
		/// call as often as Configure fires, always idempotent for a given profile. The magnitudes here
		/// intentionally pair with the CONDITIONAL sign flips at the 11 dual-mode AddVote call sites
		/// below (search "profileFlip") — Learned's numbers are meaningless without those flips, and
		/// Prior's numbers are meaningless WITH them, so profile and sign always move together.</summary>
		private void ApplyWeightProfile()
		{
			if (WeightProfile == SneakyWeightProfile.Learned)
			{
				// train_council_weights.py, after-lane-fix corpus, last fold (5,788 rows). Learned beat
				// Prior in every one of 4 out-of-sample folds — pooled: +1.93 edge/50.7% win vs
				// -15.90 edge/45.9% win. Magnitudes only — see the profileFlip AddVote sites for sign.
				WeightTrend = 10.128; WeightAdx = 4.682; WeightEnvelope = 7.210; WeightWae = 8.056;
				WeightTrendFusion = 35.381; WeightArch = 2.519; WeightStructure = 5.574;
				WeightStf = 4.678; WeightSar = 4.198; WeightVidya = 1.563; WeightZScore = 14.227;
				// same-sign voters — magnitude only, no AddVote flip needed either profile
				WeightCci = 1.000; WeightCompression = 4.842; WeightPressure = 4.816;
				WeightIntermarket = 1.916; WeightFlow = 10.061; WeightSuperTrend = 26.416;
				WeightAdxvma = 20.127; WeightGodReversal = 16.142; WeightExhaustion = 5.452;
				WeightHarmonic = 22.565;
				// no learned data this run — leave at whatever Prior/SetDefaults already put here
				// (WeightBrick, WeightConviction, WeightFlux, WeightCvd untouched — WeightCvd added
				// 2026-08-16, same reasoning: it has never cast a vote, so there is nothing to fit)
				// 2026-08-12 — check_conviction.py against instance E's own recorded data (33 non-vetoed
				// bars): mean conviction 8.7%, median 7.1%, 94% below Prior's 0.2 floor. Learned's largest
				// weights partially invert a highly-correlated voter bloc (real out-of-sample edge, but a
				// structurally weaker conviction ratio) — 0.10 is a starting point, re-check against your
				// own chart's distribution as more data accumulates.
				ConvictionFloor = 0.10;
			}
			else if (WeightProfile == SneakyWeightProfile.Learned1)
			{
				// 2026-08-29 — train_council_weights.py --instance F, clean single-instance corpus
				// (no cross-instance sign-convention confound), fold 4 / 10,784 rows. Magnitudes are
				// the ABSOLUTE VALUE of the fitted coefficient — AddVote's weight<=0 guard silently
				// drops a voter rather than inverting it (see the profileFlip comment on TRND below),
				// so sign lives in the matching Learned1FlipXxx toggle at the AddVote call site instead,
				// never in the weight itself. CAVEAT (2026-08-29, told to the user): per-fold OOS edge
				// for instance F showed the pooled "learned beats current" advantage is driven almost
				// entirely by fold 1 (+15.05 learned vs -21.09 current); folds 2-3 favor the CURRENT
				// (Prior) weights. Treat this profile as "worth testing on sim," not "known better."
				WeightAdxvma = 17.193; WeightStructure = 12.298; WeightHarmonic = 12.280;
				WeightEnvelope = 10.870; WeightSuperTrend = 10.030; WeightVidya = 8.749;
				WeightWae = 8.498; WeightCvd = 7.290; WeightCci = 7.252;
				WeightTrendFusion = 5.662; WeightCompression = 4.081; WeightGodReversal = 3.150;
				WeightSar = 2.282; WeightFlow = 2.208; WeightArch = 1.556;
				WeightAdx = 1.377; WeightStf = 1.367; WeightIntermarket = 1.308;
				WeightExhaustion = 1.082; WeightPressure = 0.764; WeightTrend = 0.104;
				WeightZScore = 0.004;
				// no learned data this run (same as Learned) — leave WeightBrick/WeightConviction/WeightFlux
				// at whatever Prior/SetDefaults already put here.
				// NOT calibrated yet — this is Prior's conservative floor as a placeholder. Re-run
				// check_conviction.py against this chart's own recorded (non-vetoed) bars once Learned1
				// has accumulated some history, same as Learned's 0.10 was derived on 2026-08-12.
				ConvictionFloor = 0.2;
			}
			else if (WeightProfile == SneakyWeightProfile.Learned2)
			{
				// 2026-08-29 — "de-redundant" POLICY variant (item 6 of the 2026-08-28 optimization
				// report), NOT a new training run. Built mechanically from Learned1's own numbers above:
				// the pairwise-agreement analysis found TRND/PSAR/TFUS/ARCH/WAE/ZSC/STRC agreeing with
				// each other 85-99% of the time — one trend-following opinion counted 7 times over — and
				// found IMKT/CCI/CVD/AVMA/HARM the consistently strong, under-weighted voices. This profile
				// zeroes (excludes, not just down-weights — a clean test of the hypothesis) the redundant
				// 7's Learned1 weights and hands their combined total (30.404) to the strong 5, split in
				// proportion to each one's OWN Learned1 weight (so AVMA/HARM, already Learned1's biggest
				// winners, get most of the redistribution). Every other voter's weight is untouched from
				// Learned1. Flip choices for the voters kept active are also copied from Learned1 — see
				// the Learned2Flip* toggles below.
				WeightTrend = 0.0; WeightSar = 0.0; WeightTrendFusion = 0.0; WeightArch = 0.0;
				WeightWae = 0.0; WeightZScore = 0.0; WeightStructure = 0.0;   // redundant cluster — excluded
				WeightIntermarket = 2.185; WeightCci = 12.117; WeightCvd = 12.180;
				WeightAdxvma = 28.727; WeightHarmonic = 20.518;               // strong cluster — boosted
				WeightAdx = 1.377; WeightEnvelope = 10.870; WeightSuperTrend = 10.030;
				WeightVidya = 8.749; WeightCompression = 4.081; WeightGodReversal = 3.150;
				WeightFlow = 2.208; WeightStf = 1.367; WeightExhaustion = 1.082;
				WeightPressure = 0.764;                                        // unchanged from Learned1
				// NOT calibrated — same placeholder caveat as Learned1's ConvictionFloor above.
				ConvictionFloor = 0.2;
			}
			else if (WeightProfile == SneakyWeightProfile.Learned3)
			{
				// 2026-09-05 — same magnitudes as Learned above (identical fit; this profile only
				// exists to carry corrected sign-flips on a separate, independently-selectable
				// profile so an existing Learned-profile chart is never touched by this). See the
				// Learned3Flip* defaults in SetDefaults() for which flips changed and why.
				WeightTrend = 10.128; WeightAdx = 4.682; WeightEnvelope = 7.210; WeightWae = 8.056;
				WeightTrendFusion = 35.381; WeightArch = 2.519; WeightStructure = 5.574;
				WeightStf = 4.678; WeightSar = 4.198; WeightVidya = 1.563; WeightZScore = 14.227;
				WeightCci = 1.000; WeightCompression = 4.842; WeightPressure = 4.816;
				WeightIntermarket = 1.916; WeightFlow = 10.061; WeightSuperTrend = 26.416;
				WeightAdxvma = 20.127; WeightGodReversal = 16.142; WeightExhaustion = 5.452;
				WeightHarmonic = 22.565;
				// Same "directional, not decisive" caveat as Learned's own floor — unchanged pending
				// enough live Learned3 history to re-derive it the way check_conviction.py did for Learned.
				ConvictionFloor = 0.10;
			}
			else if (WeightProfile == SneakyWeightProfile.Learned4)
			{
				// 2026-09-12 — built directly on Learned2's own numbers, NOT a new training run. The
				// 2026-08-28 pairwise-agreement analysis behind Learned2 zeroed one 7-voter redundant
				// cluster (TRND/PSAR/TFUS/ARCH/WAE/ZSC/STRC, all already 0.0 below, copied unchanged from
				// Learned2) but a separate, earlier pairwise-correlation pass this same engagement found 3
				// MORE redundant pairs that Learned2 left fully double-counted: CCI/SPRT (94-95% agree),
				// VDYA/VENV (94.7%), CVD/FLOW (91.7%). This profile applies the exact same rule Learned2
				// already used on its own cluster to these 3 pairs too: zero the pair's weaker member
				// (SPRT/VDYA/FLOW) and hand its FULL weight to the partner Learned2's analysis already
				// flagged as the strong one (CCI/VENV/CVD) — nothing lost, no new fit, no new sign claim.
				// Every other voter's weight (and every flip choice, see Learned4Flip* in SetDefaults())
				// is copied from Learned2 unchanged.
				WeightTrend = 0.0; WeightSar = 0.0; WeightTrendFusion = 0.0; WeightArch = 0.0;
				WeightWae = 0.0; WeightZScore = 0.0; WeightStructure = 0.0;   // Learned2's redundant cluster — unchanged
				WeightSuperTrend = 0.0; WeightVidya = 0.0; WeightFlow = 0.0;   // SPRT/VDYA/FLOW — newly zeroed (this profile)
				WeightCci = 22.147;          // was 12.117; +10.030 from SPRT
				WeightEnvelope = 19.619;     // was 10.870; +8.749 from VDYA (VENV)
				WeightCvd = 12.180 + 2.208;  // was 12.180; +2.208 from FLOW = 14.388
				WeightAdxvma = 28.727; WeightHarmonic = 20.518;               // unchanged from Learned2
				WeightIntermarket = 2.185; WeightAdx = 1.377; WeightCompression = 4.081;
				WeightGodReversal = 3.150; WeightStf = 1.367; WeightExhaustion = 1.082;
				WeightPressure = 0.764;                                        // unchanged from Learned2
				// Same "not calibrated" placeholder as Learned1/Learned2's floor — re-run check_conviction.py
				// against Learned4's own recorded (non-vetoed) bars once it has accumulated some history,
				// same discipline as Learned's 0.10 was derived on 2026-08-12. The 3 weight increases above
				// (CCI/VENV/CVD roughly +80-180% each) will shift the conviction distribution meaningfully
				// higher than Learned2's own — do not assume Learned2's distribution still applies here.
				ConvictionFloor = 0.2;
			}
			else   // Prior — the original manual/reference-build values
			{
				WeightTrend = 1.0; WeightAdx = 0.6; WeightEnvelope = 0.6; WeightWae = 0.2;
				WeightTrendFusion = 0.0; WeightArch = 0.0; WeightStructure = 0.7;
				WeightStf = 0.0; WeightSar = 0.5; WeightVidya = 0.5; WeightZScore = 0.2;
				WeightCci = 0.8; WeightCompression = 0.7; WeightPressure = 0.0;
				WeightIntermarket = 0.6; WeightFlow = 0.9; WeightSuperTrend = 0.7;
				WeightAdxvma = 0.6; WeightGodReversal = 0.9; WeightExhaustion = 0.5;
				WeightHarmonic = 0.4;
				ConvictionFloor = 0.2;   // 2026-08-07 reference-build value
			}
		}

		// ── the vote primitive (mirrors the manual's AddVote contract) ────────────
		/// <summary>TRIGGER-kind vote: only adds to the declared denominator when it fired.</summary>
		private void AddVoteTrigger(string tag, int dir, double weight)
		{
			if (Math.Sign(dir) != 0 && weight > 0.0) _declaredTriggerFired += weight;
			AddVote(tag, dir, weight);
		}

		private void AddVote(string tag, int dir, double weight)
		{
			int s = Math.Sign(dir);
			_votes[tag] = s;
			_voteW[tag] = weight;
			if (s == 0 || weight <= 0.0) { _chips.Add(new KeyValuePair<string,int>(tag, 0)); return; }
			_netScore += s * weight;
			_activeW  += weight;
			_voters++;
			_chips.Add(new KeyValuePair<string,int>(tag, s));
		}

		// ── CouncilFusion roster helpers (2026-08-16, see the FUSE section below) ──────────────
		/// <summary>Every TRIGGER-kind vote tag this Council knows about, incl. EYE (inert unless
		/// IncludeEye is on and it fires — see TriggerWeight/AddVoteTrigger call sites).</summary>
		private static readonly string[] TriggerTags =
			{ "COMP", "WAE", "GREV", "EXH", "HARM", "ZSC", "FLOW", "EYE" };

		/// <summary>Adds one tag to a CouncilFusion.Inputs roster (Declared + WeightOf + KindOf [+ Spoke]).
		/// A weight&lt;=0 declared tag is still added — Fuse's own WeightOf/eff logic already treats a
		/// w&lt;=0 declared voter as contributing nothing, same as this file's own DeclaredWeight() sum
		/// always did (WeightCvd/WeightTrendFusion/WeightArch enter at 0.0 and have always been summed
		/// in, contributing zero, not skipped).</summary>
		private static void AddDeclared(CouncilFusion.Inputs x, string tag, double weight, CouncilFusion.Kind kind, bool spoke = false)
		{
			if (x.Declared == null) x.Declared = new List<string>();
			x.Declared.Add(tag);
			x.WeightOf[tag] = weight;
			x.KindOf[tag]   = kind;
			if (spoke) x.Spoke.Add(tag);
		}

		/// <summary>Weight lookup for the TRIGGER-kind tags only — kept as a switch rather than a
		/// dictionary since it only ever runs for the (at most 8) tags that fired this bar.</summary>
		private double TriggerWeight(string tag)
		{
			switch (tag)
			{
				case "COMP": return WeightCompression;
				case "WAE":  return WeightWae;
				case "GREV": return WeightGodReversal;
				case "EXH":  return WeightExhaustion;
				case "HARM": return WeightHarmonic;
				case "ZSC":  return WeightZScore;
				case "FLOW": return WeightFlow;
				case "EYE":  return WeightEye;
				default:     return 0.0;
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 2) return;

			AssertLane();
			RefreshOnChartTypes();   // 2026-08-07 — off-chart gate, ported from real Council v1.11.0 (see
			                         // header note near OnChart() for why this exists)

			_netScore = 0.0; _activeW = 0.0; _voters = 0;
			_votes.Clear(); _voteW.Clear(); _chips.Clear();
			_declaredTriggerFired = 0.0;
			_vetoed = false; _vetoReason = null;
			lock (_onChartGate) _skippedOffChart.Clear();   // 2026-08-07 — per-bar audit reset, off-chart gate

			string sc = Scope();

			SneakyCore.TrendState       trend   = null; SneakyCore.AdxState        adx     = null;
			SneakyCore.CciState         cci     = null; SneakyCore.EnvelopeState   env     = null;
			SneakyCore.CompressionState comp    = null; SneakyCore.WaeState        wae     = null;
			SneakyCore.StructureState   struc   = null; SneakyCore.SuperTrendState sprt    = null;
			SneakyCore.StfState         stf     = null; SneakyCore.SarState        sar     = null;
			SneakyCore.AdxvmaState      avma    = null; SneakyCore.VidyaState      vdya    = null;
			SneakyCore.GodReversalState grev    = null; SneakyCore.ExhaustionState exh     = null;
			SneakyCore.HarmonicState    harm    = null; SneakyCore.ZScoreState     zsc     = null;
			SneakyCore.RegimeState      regime  = null; SneakyCore.LiquidityState  liq     = null;
			SneakyCore.LevelState       lv      = null;
			SneakyCore.BrickState       brick   = null; SneakyCore.ConvictionState cvb     = null;
			SneakyCore.PressureState    press   = null;
			SneakyCore.IntermarketState imkt   = null;
			SneakyCore.ParticipationState part = null; SneakyCore.FlowState flow = null;
			SneakyCore.EyeVerdict eye = null; SneakyCore.ClockState clk = null;
			SneakyCore.FluxState flux = null;
			SneakyCore.CvdState cvd = null;   // 2026-08-16 — new (see CHANGELOG)
			SneakyCore.TrendFusionState tfus = null;
			SneakyCore.TrendArchitectState arch = null;   // 2026-08-07 — the REAL PRISM/Trend Architect, now that
			                                               // the actual source exists in this build (see its own
			                                               // header for full provenance/license). Separate from
			                                               // TFUS, not a replacement for it in this file's code.

			try { trend  = Local("TRND", SneakyCore.GetTrendState(sc, StaleSec)); }        catch { }
			try { adx    = Local("ADX", SneakyCore.GetAdxState(sc, StaleSec)); }          catch { }
			try { cci    = Local("CCI", SneakyCore.GetCciState(sc, StaleSec)); }          catch { }
			try { env    = Local("ENV", SneakyCore.GetEnvelopeState(sc, StaleSec)); }     catch { }
			try { comp   = Local("CMP", SneakyCore.GetCompressionState(sc, StaleSec)); }  catch { }
			try { wae    = Local("WAE", SneakyCore.GetWaeState(sc, StaleSec)); }          catch { }
			try { struc  = Local("STRC", SneakyCore.GetStructureState(sc, StaleSec)); }    catch { }
			try { sprt   = Local("SPRT", SneakyCore.GetSuperTrendState(sc, StaleSec)); }   catch { }
			try { stf    = Local("STF", SneakyCore.GetStfState(sc, StaleSec)); }          catch { }
			try { sar    = Local("PSAR", SneakyCore.GetSarState(sc, StaleSec)); }          catch { }
			try { avma   = Local("AVMA", SneakyCore.GetAdxvmaState(sc, StaleSec)); }       catch { }
			try { vdya   = Local("VDYA", SneakyCore.GetVidyaState(sc, StaleSec)); }        catch { }
			try { grev   = Local("GREV", SneakyCore.GetGodReversalState(sc, StaleSec)); }  catch { }
			try { exh    = Local("EXH", SneakyCore.GetExhaustionState(sc, StaleSec)); }   catch { }
			try { harm   = Local("HARM", SneakyCore.GetHarmonicState(sc, StaleSec)); }     catch { }
			try { zsc    = Local("ZSC", SneakyCore.GetZScoreState(sc, StaleSec)); }       catch { }
			try { regime = Local("RGME", SneakyCore.GetRegimeState(sc, StaleSec)); }       catch { }
			try { liq    = SneakyCore.GetLiquidityState(sc, StaleSec); }    catch { }
			try { lv     = SneakyCore.GetLevelState(sc, StaleSec); }        catch { }
			SneakyCore.MtfState mtf = null;
			try { mtf    = Local("MTF", SneakyCore.GetMtfState(sc, StaleSec)); }          catch { }
			SneakyCore.ProfileState prof = null;
			try { prof   = SneakyCore.GetProfileState(sc, StaleSec); }      catch { }
			// Bar-type seams — published by SneakyTBars / Drift / Effort / Lattice.
			// ConvictionState is Drift's CVB voter (flow-confirmed bias); on a v1.36.0 core
			// it resolves through the compat shim. Absent bar type ⇒ null ⇒ simply abstains.
			try { brick  = SneakyCore.GetBrickState(sc, StaleSec); }        catch { }
			try { cvb    = SneakyCore.GetConvictionState(sc, StaleSec); }   catch { }
			// FLUX — the order-flow-imbalance bar type's own signed-delta read. SCOPE-keyed like
			// CVB/BRK, and same reasoning: only exists on a chart actually running the SneakyFlux
			// bar type, so an absent seam here is a bar-type choice, not a fault. Added 2026-08-06:
			// this seam was defined in Core (v1.31.0) and the SneakyFlux bar-type engine has always
			// published it, but nothing ever consulted it — pure unused evidence, not a bug.
			try { flux   = SneakyCore.GetFluxState(sc, StaleSec); }         catch { }
			// CVD — SneakyCVD's session-scale cumulative volume delta (Core CvdState, v1.43.0).
			// Distinct from FLUX (one forming bar, resets every close) and from FLOW (a tick-rule
			// REGRESSION regime read, not a raw cumulative total) — see the file header CHANGELOG
			// for the full disambiguation. Scope-keyed per CvdState's own Core doc comment, so it
			// goes through the same off-chart gate as every other chart-derived voter.
			try { cvd    = Local("CVD", SneakyCore.GetCvdState(sc, StaleSec)); } catch { }
			// TFUS — SneakyTrendFusion's composite read (2026-08-06). NOT the abandoned PRISM/
			// ARCH scaffolding (TrendArchitectState) — a genuinely new, original seam. See
			// SneakyTrendFusion's own header for why this exists instead of a PRISM port.
			try { tfus   = Local("TFUS", SneakyCore.GetTrendFusionState(sc, StaleSec)); }  catch { }
			try { arch   = Local("ARCH", SneakyCore.GetTrendArchitectState(sc, StaleSec)); } catch { }
			// Order-flow pressure. Now a seam, so the Council can finally vote on it —
			// previously unreachable here because it was sub-indicator-only.
			try { press  = SneakyCore.GetPressureState(sc, StaleSec); }      catch { }
			// Intermarket is INSTRUMENT-KEYED, not scope-keyed — a macro lean derived from
			// OTHER markets is a property of the instrument, not of this timeframe. That is
			// the Core v1.12.0 contract, not an oversight.
			try { if (Instrument != null && Instrument.MasterInstrument != null)
					imkt = SneakyCore.GetIntermarketState(Instrument.MasterInstrument.Name, StaleSec); } catch { }
			// Participation is SCOPE-keyed — RVOL is bar-type dependent, so a 150-tick
			// RVOL is not a TBars RVOL. Load one Participation per Council chart.
			try { part = Local("PARTIC", SneakyCore.GetParticipationState(sc, StaleSec)); }   catch { }
			try { flow = SneakyCore.GetFlowState(sc, StaleSec); }            catch { }
			// Eye is SCOPE-keyed (chart-specific verdict) — only read at all when IncludeEye is
			// on, since it's an optional experiment, not part of the panel by default.
			if (IncludeEye) { try { eye = SneakyCore.GetEyeVerdict(sc, StaleSec); } catch { } }
			// Clock is INSTRUMENT-keyed, same reasoning as Intermarket — session phase doesn't
			// depend on bar type.
			try { if (Instrument != null && Instrument.MasterInstrument != null)
					clk = SneakyCore.GetClockState(Instrument.MasterInstrument.Name, StaleSec); } catch { }

			// ── TREND BLOC (price-derived; heavily correlated — see header) ───────
			// PROFILE FLIP (2026-08-11): under Learned, this voter's fitted weight was negative in
			// the Python model (net = sum(dir*weight), no positivity filter there) — meaning "invert
			// this voter." AddVote here can't express a negative weight the same way (weight<=0 just
			// disables the voter), so the inversion happens at the direction argument instead,
			// conditional on WeightProfile so Prior keeps its original, non-inverted meaning. Same
			// pattern on every voter tagged "profileFlip" below.
			if (trend != null) AddVote("TRND", (WeightProfile == SneakyWeightProfile.Learned && LearnedFlipTrnd) || (WeightProfile == SneakyWeightProfile.Learned1 && Learned1FlipTrnd) || (WeightProfile == SneakyWeightProfile.Learned2 && Learned2FlipTrnd) || (WeightProfile == SneakyWeightProfile.Learned3 && Learned3FlipTrnd) || (WeightProfile == SneakyWeightProfile.Learned4 && Learned4FlipTrnd) ? -trend.Direction : trend.Direction, WeightTrend);

			// CCI/ADX fold "strength" into the weight, exactly as the manual specifies.
			if (cci   != null) AddVote("CCI",  (WeightProfile == SneakyWeightProfile.Learned1 && Learned1FlipCci) || (WeightProfile == SneakyWeightProfile.Learned2 && Learned2FlipCci) || (WeightProfile == SneakyWeightProfile.Learned4 && Learned4FlipCci) ? -cci.Bias : cci.Bias,
				WeightCci * (Math.Abs(cci.TrendState) >= 2 ? 1.5 : 1.0) * (cci.Weakening ? 0.5 : 1.0));
			if (adx   != null) AddVote("ADX",  (WeightProfile == SneakyWeightProfile.Learned && LearnedFlipAdx) || (WeightProfile == SneakyWeightProfile.Learned1 && Learned1FlipAdx) || (WeightProfile == SneakyWeightProfile.Learned2 && Learned2FlipAdx) || (WeightProfile == SneakyWeightProfile.Learned3 && Learned3FlipAdx) || (WeightProfile == SneakyWeightProfile.Learned4 && Learned4FlipAdx) ? -adx.Bias : adx.Bias,  WeightAdx * (adx.Strong ? 1.25 : 1.0));   // profileFlip, see TRND comment above

			if (struc != null) AddVote("STRC", (WeightProfile == SneakyWeightProfile.Learned && LearnedFlipStrc) || (WeightProfile == SneakyWeightProfile.Learned1 && Learned1FlipStrc) || (WeightProfile == SneakyWeightProfile.Learned2 && Learned2FlipStrc) || (WeightProfile == SneakyWeightProfile.Learned3 && Learned3FlipStrc) || (WeightProfile == SneakyWeightProfile.Learned4 && Learned4FlipStrc) ? -struc.Signal : struc.Signal, WeightStructure);   // profileFlip, see TRND comment above
			if (sprt  != null) AddVote("SPRT", (WeightProfile == SneakyWeightProfile.Learned1 && Learned1FlipSprt) || (WeightProfile == SneakyWeightProfile.Learned2 && Learned2FlipSprt) || (WeightProfile == SneakyWeightProfile.Learned4 && Learned4FlipSprt) ? -sprt.Signal : sprt.Signal,  WeightSuperTrend);
			if (stf   != null) AddVote("STF",  (WeightProfile == SneakyWeightProfile.Learned && LearnedFlipStf) || (WeightProfile == SneakyWeightProfile.Learned1 && Learned1FlipStf) || (WeightProfile == SneakyWeightProfile.Learned2 && Learned2FlipStf) || (WeightProfile == SneakyWeightProfile.Learned3 && Learned3FlipStf) || (WeightProfile == SneakyWeightProfile.Learned4 && Learned4FlipStf) ? -stf.Bias : stf.Bias,     WeightStf);   // profileFlip, see TRND comment above
			if (sar   != null) AddVote("PSAR", (WeightProfile == SneakyWeightProfile.Learned && LearnedFlipPsar) || (WeightProfile == SneakyWeightProfile.Learned1 && Learned1FlipPsar) || (WeightProfile == SneakyWeightProfile.Learned2 && Learned2FlipPsar) || (WeightProfile == SneakyWeightProfile.Learned3 && Learned3FlipPsar) || (WeightProfile == SneakyWeightProfile.Learned4 && Learned4FlipPsar) ? -sar.Signal : sar.Signal,   WeightSar);   // profileFlip, see TRND comment above
			if (avma  != null) AddVote("AVMA", avma.Signal,  WeightAdxvma);
			if (vdya  != null) AddVote("VDYA", (WeightProfile == SneakyWeightProfile.Learned && LearnedFlipVdya) || (WeightProfile == SneakyWeightProfile.Learned1 && Learned1FlipVdya) || (WeightProfile == SneakyWeightProfile.Learned2 && Learned2FlipVdya) || (WeightProfile == SneakyWeightProfile.Learned3 && Learned3FlipVdya) || (WeightProfile == SneakyWeightProfile.Learned4 && Learned4FlipVdya) ? -vdya.Signal : vdya.Signal,  WeightVidya);   // profileFlip, see TRND comment above

			// ── TRIGGER / EVENT BLOC ─────────────────────────────────────────────
			if (comp != null) AddVoteTrigger("COMP", (WeightProfile == SneakyWeightProfile.Learned1 && Learned1FlipComp) || (WeightProfile == SneakyWeightProfile.Learned2 && Learned2FlipComp) || (WeightProfile == SneakyWeightProfile.Learned4 && Learned4FlipComp) ? -comp.BreakDir : comp.BreakDir, WeightCompression);
			if (wae  != null) AddVoteTrigger("WAE",  wae.IsExploding ? ((WeightProfile == SneakyWeightProfile.Learned && LearnedFlipWae) || (WeightProfile == SneakyWeightProfile.Learned1 && Learned1FlipWae) || (WeightProfile == SneakyWeightProfile.Learned2 && Learned2FlipWae) || (WeightProfile == SneakyWeightProfile.Learned3 && Learned3FlipWae) || (WeightProfile == SneakyWeightProfile.Learned4 && Learned4FlipWae) ? -wae.Bias : wae.Bias) : 0, WeightWae);   // profileFlip, see TRND comment above
			if (grev != null) AddVoteTrigger("GREV", (WeightProfile == SneakyWeightProfile.Learned1 && Learned1FlipGrev) || (WeightProfile == SneakyWeightProfile.Learned2 && Learned2FlipGrev) || (WeightProfile == SneakyWeightProfile.Learned4 && Learned4FlipGrev) ? -grev.Dir : grev.Dir, WeightGodReversal * Math.Max(0.0, Math.Min(1.0, grev.Quality)));
			if (exh  != null) AddVoteTrigger("EXH",  (WeightProfile == SneakyWeightProfile.Learned1 && Learned1FlipExh) || (WeightProfile == SneakyWeightProfile.Learned2 && Learned2FlipExh) || (WeightProfile == SneakyWeightProfile.Learned4 && Learned4FlipExh) ? -exh.Dir : exh.Dir,  WeightExhaustion * (exh.Major ? 1.25 : 1.0));
			if (harm != null) AddVoteTrigger("HARM", harm.Dir, WeightHarmonic);

			// ZScore is a MEAN-REVERSION voice: an extreme HIGH z is a vote DOWN, not up (hence the
			// -Math.Sign below, under Prior). LEARNED (2026-08-11): the fitted weight was ALSO
			// negative, i.e. a second inversion on top of that first one — the two cancel out to
			// Math.Sign (no explicit flip) under Learned specifically, same profileFlip reasoning
			// as the TRND comment above.
			if (zsc != null) AddVoteTrigger("ZSC", zsc.Extreme
				? ((WeightProfile == SneakyWeightProfile.Learned && LearnedFlipZsc) || (WeightProfile == SneakyWeightProfile.Learned1 && Learned1FlipZsc) || (WeightProfile == SneakyWeightProfile.Learned2 && Learned2FlipZsc) || (WeightProfile == SneakyWeightProfile.Learned3 && Learned3FlipZsc) || (WeightProfile == SneakyWeightProfile.Learned4 && Learned4FlipZsc) ? Math.Sign(zsc.Z) : -Math.Sign(zsc.Z))
				: 0, WeightZScore);

			// BRK — the bar type's own brick direction. The manual lists Brick as a 0.5 voter.
			// SameDirCount is trend persistence, so a lone brick counts for less than a run.
			if (brick != null)
				AddVote("BRK", brick.Direction,
					WeightBrick * (brick.SameDirCount >= 3 ? 1.25 : brick.SameDirCount <= 1 ? 0.75 : 1.0));

			// CVB — Drift's flow-confirmed conviction bias. This is the ONLY voter on the panel
			// that is not price-derived: it is confirmed by tape delta, which makes it the closest
			// thing here to an orthogonal axis (the manual's load-bearing gap). Scaled by its own
			// conviction, and halved when flow is ABSORBING against the brick direction.
			if (cvb != null)
				AddVote("CVB", cvb.Bias,
					WeightConviction * Math.Max(0.0, Math.Min(1.0, cvb.Conviction)) * (cvb.IsDiverging ? 0.5 : 1.0));

			// FLOW — CVD regression. Votes on Signal, NOT Bias: Signal is already gated by
			// R² and strength, so an incoherent lean abstains instead of voting noise. That
			// gating is the sensor's whole contribution, and voting Bias would discard it.
			// Tape-derived, so it sits with PRS/CVB/IMKT rather than the trend bloc.
			if (flow != null)
				AddVoteTrigger("FLOW", flow.Signal,
					WeightFlow * Math.Max(0.25, flow.Strength) * (flow.Divergence != 0 ? 0.7 : 1.0));

			// PRS — classified buy/sell pressure. Like CVB this is TAPE-derived, not price-
			// derived, so it adds information the trend bloc structurally cannot. Scaled by
			// Strength (how far past 50/50 the dominant side sits) and halved when the bar
			// fell back to OHLC classification instead of real ticks.
			if (press != null)
				AddVote("PRS", (WeightProfile == SneakyWeightProfile.Learned1 && Learned1FlipPrs) || (WeightProfile == SneakyWeightProfile.Learned2 && Learned2FlipPrs) || (WeightProfile == SneakyWeightProfile.Learned4 && Learned4FlipPrs) ? -press.Dir : press.Dir,
					WeightPressure * Math.Max(0.15, press.Strength)
					* (press.TickBacked ? 1.0 : 0.5)      // OHLC proxy is price-derived — halve it
					* (press.Strong ? 1.15 : 1.0)         // clear dominance, not a coin flip
					// was press.Absorbing(press.Dir) — that compared Dir against itself and could
					// never be true by construction (same dead-code pattern found in the CVB damp
					// below). PressureState doesn't carry a second directional field to diverge
					// against; Divergence already IS the "pressure vs price disagree" signal.
					* (press.Divergence != 0 ? 0.6 : 1.0));

			// FLUX — net signed order-flow imbalance from the just-closed Flux bar. Same
			// non-price-derived family as PRS/CVB, so weighted alongside them rather than with
			// the trend bloc. Every closed Flux bar already crossed its adaptive imbalance
			// threshold by construction (that's what closes the bar) — unlike a fixed-time bar,
			// there's no "weak vs strong" reading to separately scale by; Divergence (flow
			// opposing the bar's own price direction — absorption) is still a meaningful damp.
			// Enters at WeightFlux = 0.0 by default: brand new, ungraded on this corpus, same
			// audition discipline this suite already used once for Pressure before trusting it.
			if (flux != null)
				AddVote("FLUX", flux.FlowDir, WeightFlux * (flux.Divergence != 0 ? 0.6 : 1.0));

			// CVD — SneakyCVD's session cumulative-delta direction. Same non-price-derived family
			// as PRS/CVB/FLUX: tape-confirmed, not derived from this instrument's own price.
			// TickBacked gates the vote, same discipline as PRS/BSP — a historical rebuild's
			// OHLC-proxy fallback IS price-derived, so voting it would launder a price signal in
			// as order flow. Damped on Divergence (flow opposing the bar's own price direction),
			// same idiom as FLUX/PRS above — deliberately a direct Divergence check rather than
			// cvd.Absorbing(cvd.Dir): PRS's own Absorbing() helper was found dead-code-by-
			// construction and replaced with exactly this pattern (Core v1.45.1); reusing the
			// untested sibling helper here would risk the same class of bug. Enters at
			// WeightCvd = 0.0 — brand new, ungraded, same audition discipline as Flux/TFUS/ARCH,
			// and matches the real Sentinel Suite's own Council, which also ships CVD at 0.0.
			if (cvd != null)
				AddVote("CVD", cvd.TickBacked ? cvd.Dir : 0, WeightCvd * (cvd.Divergence != 0 ? 0.6 : 1.0));

			// TFUS — SneakyTrendFusion's majority-vote composite (MFI/CCI/CVD/Hurst/KAMA fan).
			// Scaled by how much of its OWN internal panel agreed (Agree/5) — a bare 3-of-5
			// squeaker gets less weight here than a clean 5-of-5, same "scale by confidence"
			// idiom already used for IMKT (|Score|) and PRS (Strength). Enters at weight 0.0.
			if (tfus != null)
				AddVote("TFUS", (WeightProfile == SneakyWeightProfile.Learned && LearnedFlipTfus) || (WeightProfile == SneakyWeightProfile.Learned1 && Learned1FlipTfus) || (WeightProfile == SneakyWeightProfile.Learned2 && Learned2FlipTfus) || (WeightProfile == SneakyWeightProfile.Learned3 && Learned3FlipTfus) || (WeightProfile == SneakyWeightProfile.Learned4 && Learned4FlipTfus) ? -tfus.Bias : tfus.Bias, WeightTrendFusion * (tfus.Agree / 5.0));   // profileFlip, see TRND comment above

			// ARCH — the real PRISM/Trend Architect (2026-08-07, the genuine ported source, not
			// TFUS's honest substitute). Straight vote, no extra scaling — PRISM's own Signal
			// already reflects its internal confidence handling. Enters at weight 0.0: brand new
			// to THIS Council's own grading history, same audition discipline as every other new
			// voter tonight, even though the reference build's own tuning suggests ~0.7 eventually.
			if (arch != null)
				AddVote("ARCH", (WeightProfile == SneakyWeightProfile.Learned && LearnedFlipArch) || (WeightProfile == SneakyWeightProfile.Learned1 && Learned1FlipArch) || (WeightProfile == SneakyWeightProfile.Learned2 && Learned2FlipArch) || (WeightProfile == SneakyWeightProfile.Learned3 && Learned3FlipArch) || (WeightProfile == SneakyWeightProfile.Learned4 && Learned4FlipArch) ? -arch.Signal : arch.Signal, WeightArch);   // profileFlip, see TRND comment above

			// IMKT — what CORRELATED markets are doing. Along with PRS and CVB this is one of
			// only three inputs on the panel not derived from this instrument's own price, so
			// it is weighted with the other two rather than with the trend bloc. Scaled by
			// |Score| (a 5-of-5 agreement should outvote a 3-of-5), and damped when few
			// references are warm — two markets agreeing is not a macro reading.
			if (imkt != null)
				AddVote("IMKT", imkt.Lean,
					WeightIntermarket * Math.Max(0.25, Math.Abs(imkt.Score))
					* (imkt.RefCount >= 3 ? 1.0 : 0.7));

			// VolEnvelope: Regime 2=TrendUp / 3=TrendDown are directional; others abstain.
			// PROFILE FLIP (2026-08-11): under Learned the +1/-1 mapping swaps, see TRND comment above.
			if (env != null) AddVote("VENV", (WeightProfile == SneakyWeightProfile.Learned && LearnedFlipVenv) || (WeightProfile == SneakyWeightProfile.Learned1 && Learned1FlipVenv) || (WeightProfile == SneakyWeightProfile.Learned2 && Learned2FlipVenv) || (WeightProfile == SneakyWeightProfile.Learned3 && Learned3FlipVenv) || (WeightProfile == SneakyWeightProfile.Learned4 && Learned4FlipVenv)
				? (env.Regime == 2 ? -1 : env.Regime == 3 ? 1 : 0)
				: (env.Regime == 2 ? 1 : env.Regime == 3 ? -1 : 0), WeightEnvelope);

			// EYE — OPTIONAL, off by default. CAUTION: Eye's own verdict is built FROM Pressure,
			// Intermarket, and Drift Conviction — the same three sources already voting above as
			// PRS/IMKT/CVB. Turning this on double-counts that evidence: once directly, once again
			// bundled inside Eye's combined read. It is offered as a togglable EXPERIMENT (does the
			// bundled/thresholded combination add anything the three raw votes don't already say),
			// not a recommended default — hence the reduced weight (half of PRS/IMKT/CVB/FLOW's 0.8)
			// even when enabled. TRIGGER-kind: a not-qualified Eye abstains rather than voting 0.
			if (IncludeEye && eye != null)
				AddVoteTrigger("EYE", eye.Direction, WeightEye);

			// ── FUSE ─────────────────────────────────────────────────────────────
			// 2026-08-16 — REWIRED onto CouncilFusion.Fuse(), the shared pure-function fusion core
			// this build already carries (AddOns/CouncilFusion.cs, ported 2026-08-06 from the real
			// Sentinel Suite's own Council v1.8.0/v1.8.1 fuse block — "unchanged math" — and never
			// actually called by anything until now). Scoped NARROWLY on purpose: only the denomW /
			// bias / raw-conviction computation below routes through Fuse(). Every modulator
			// (breadth/squeeze/dry-up/in-value/overextended) and every veto (chop/high-vol/normal-
			// vol/wall/near-level/mtf/kill-window/news) stays exactly as hand-written inline code
			// below, UNCHANGED, now operating on Fuse's bias/conviction instead of the old inline
			// numbers. Three real reasons it's scoped narrowly rather than routing the whole
			// modulator/veto chain through Fuse's own Config/Inputs damp fields:
			//   1. TRIGGER DILUTION SEMANTICS DIFFER. CouncilFusion's own documented rule is "absent
			//      dilutes, quiet doesn't" (the real Council's behaviour). This file's ACTUAL prior
			//      behaviour — verified against DeclaredWeight()'s CODE, not its comment; the two
			//      disagreed with each other — was "fired dilutes; both absent AND quiet don't."
			//      Reproduced below by only adding a TRIGGER tag to Declared when it fired this bar.
			//   2. BREADTH-DAMP VOTER COUNT DIFFERS. Fuse's internal `voters` counts every Counted
			//      (weight>0) vote regardless of direction; this file's own _voters (the breadth damp
			//      a few lines down) counts only DIRECTIONAL weight>0 votes. Routing MinVoters through
			//      Fuse would silently soften every breadth damp here. Left external, unchanged.
			//   3. THIS BUILD'S CONTEXT GATES ARE MOSTLY HARD VETOES, NOT SOFT DAMPS (VetoKillWindow /
			//      VetoMtfOpposed / VetoHighVolRegime / VetoNormalRegime all zero the bar outright).
			//      Converting them to CouncilFusion's soft contextMult multipliers would be a real
			//      behaviour change to a live, currently-traded chart, not a refactor — out of scope.
			// r.Bias / r.Conviction are computed by Fuse() BEFORE any context-damp multiplier is
			// applied (ContextMult/SizeMult are separate Result fields this file never reads) — the
			// exact same two numbers the old inline math produced, from a shared core instead of a
			// second hand-maintained copy of the same formula. DeclaredWeight() itself is now dead
			// code (nothing else called it) — left in place as a reference, not deleted, since
			// _declaredTriggerFired is still (harmlessly) fed by AddVoteTrigger.
			var fusionVotes = new List<CouncilFusion.Vote>(_votes.Count);
			foreach (var kv in _votes)
				fusionVotes.Add(new CouncilFusion.Vote(kv.Key, kv.Value, _voteW[kv.Key]));

			var fusionIn = new CouncilFusion.Inputs { Votes = fusionVotes };
			// STATE voters — always declared, always dilute, whether or not the seam read anything
			// this bar (off-chart / absent included). Matches DeclaredWeight()'s own unconditional sum.
			AddDeclared(fusionIn, "TRND", WeightTrend,       CouncilFusion.Kind.State);
			AddDeclared(fusionIn, "CCI",  WeightCci,         CouncilFusion.Kind.State);
			AddDeclared(fusionIn, "ADX",  WeightAdx,         CouncilFusion.Kind.State);
			AddDeclared(fusionIn, "VENV", WeightEnvelope,    CouncilFusion.Kind.State);
			AddDeclared(fusionIn, "STRC", WeightStructure,   CouncilFusion.Kind.State);
			AddDeclared(fusionIn, "SPRT", WeightSuperTrend,  CouncilFusion.Kind.State);
			AddDeclared(fusionIn, "STF",  WeightStf,         CouncilFusion.Kind.State);
			AddDeclared(fusionIn, "PSAR", WeightSar,         CouncilFusion.Kind.State);
			AddDeclared(fusionIn, "AVMA", WeightAdxvma,      CouncilFusion.Kind.State);
			AddDeclared(fusionIn, "VDYA", WeightVidya,       CouncilFusion.Kind.State);
			AddDeclared(fusionIn, "BRK",  WeightBrick,       CouncilFusion.Kind.State);
			AddDeclared(fusionIn, "CVB",  WeightConviction,  CouncilFusion.Kind.State);
			AddDeclared(fusionIn, "PRS",  WeightPressure,    CouncilFusion.Kind.State);
			AddDeclared(fusionIn, "IMKT", WeightIntermarket, CouncilFusion.Kind.State);
			AddDeclared(fusionIn, "FLUX", WeightFlux,        CouncilFusion.Kind.State);
			AddDeclared(fusionIn, "TFUS", WeightTrendFusion, CouncilFusion.Kind.State);
			AddDeclared(fusionIn, "ARCH", WeightArch,        CouncilFusion.Kind.State);
			AddDeclared(fusionIn, "CVD",  WeightCvd,         CouncilFusion.Kind.State);
			// TRIGGER voters — declared ONLY when they actually fired this bar (see point 1 above). A
			// trigger not in _votes at all (sensor absent/off-chart) or present with sign 0 (quiet) is
			// simply never added, which is what makes both cases contribute nothing to denomW.
			foreach (string t in TriggerTags)
			{
				int d;
				if (_votes.TryGetValue(t, out d) && d != 0)
					AddDeclared(fusionIn, t, TriggerWeight(t), CouncilFusion.Kind.Trigger, spoke: true);
			}

			var fusionCfg = new CouncilFusion.Config { BiasDeadband = Deadband };
			var fused = CouncilFusion.Fuse(fusionIn, fusionCfg);

			_bias       = fused.Bias;
			_conviction = fused.Conviction;

			// BUGFIX (2026-08-22, P2 finding: Council Agree/Disagree weight filter): the old loop below
			// tallied every entry in _votes by DIRECTION only, ignoring weight — a disabled voter
			// (Weight*==0) whose underlying sensor still cast a directional read (kv.Value != 0, e.g. via
			// AddVote's `_votes[tag] = s` line, which runs before the weight<=0 early-return) still counted
			// toward Agree/Disagree even though its chip renders grey/neutral on the card. CouncilFusion.
			// Fuse() already computes the correctly weight-filtered tally (Result.Agree/Disagree, built from
			// Vote.Counted = w>0 against the SAME fused.Bias assigned to _bias above) as part of `fused`
			// above — source the card numbers from that instead of re-deriving an unfiltered count here.
			// Bias/Conviction/SizeMult are untouched; this only changes the two counts shown on the card.
			_agree    = fused.Agree;
			_disagree = fused.Disagree;

			// ── MODULATORS (multiply conviction; all fail-open) ──────────────────
			var mods = new List<string>();
			bool breadthDamped = false, squeezeDamped = false, dryUpDamped = false, inValueDamped = false;
			bool profOverextDamped = false, activeWeightDamped = false;
			if (_voters < MinVoters && MinVoters > 0)
			{
				double breadth = (double)_voters / MinVoters;
				_conviction *= breadth;
				mods.Add("breadth x" + breadth.ToString("0.00"));
				breadthDamped = true;
			}
			if (env != null && env.IsSqueeze)
			{
				_conviction *= SqueezeDamp;
				mods.Add("squeeze x" + SqueezeDamp.ToString("0.00"));
				squeezeDamped = true;
			}

			// PARTICIPATION — a MODULATOR, never a voter. Heavy volume does not mean up or
			// down, it means conviction behind whatever is happening, so there is no
			// direction to cast. What it can legitimately do is scale how much the panel's
			// agreement is worth: the same unanimous read means less in a participation
			// vacuum than it does on backed volume.
			if (part != null && DryUpDamp < 1.0 && part.DryUp)
			{
				_conviction *= DryUpDamp;
				mods.Add("dry-up x" + DryUpDamp.ToString("0.00"));
				dryUpDamped = true;
			}

			// PROFILE — also a MODULATOR, per its own seam doc comment: "acceptance inside VA = chop
			// → damp; at the edges → support". Price ACCEPTED inside the value area (Location == 0)
			// is the chop case — the panel agreeing on a direction means less when the market itself
			// hasn't rejected either side of today's fair-value range yet. Outside the value area
			// (Location != 0) is deliberately left un-modulated here — that's the "support" half of
			// the seam's own description, and Profile's Signal field (POC-reversion) already speaks
			// to it more directly than a blanket conviction boost would.
			if (prof != null && ProfileValueAreaDamp < 1.0 && prof.Location == 0)
			{
				_conviction *= ProfileValueAreaDamp;
				mods.Add("in-value x" + ProfileValueAreaDamp.ToString("0.00"));
				inValueDamped = true;
			}

			// PROFILE OVERTEXTENSION — 2026-08-06 — a second, separate Profile modulator, using the
			// Signal field (POC-reversion) rather than Location (in-value). grade_sneaky.py's PROFILE
			// REVERSION SIGNAL table showed Signal AGREEING with the panel's own bias as the WORST
			// bucket, not confirmation — price is already stretched away from value in the direction
			// you're about to trade. Confirmed non-buggy: SneakyProfile's own header documents Signal
			// as a DELIBERATE mean-reversion voice, paired here with a mostly-momentum panel. Off by
			// default (ProfileOverextendedDamp = 1.0) until validated on fresh recorded data — same
			// caution as every new modulator in this file.
			if (prof != null && ProfileOverextendedDamp < 1.0 && prof.Signal != 0 && _bias != 0 && prof.Signal == _bias)
			{
				_conviction *= ProfileOverextendedDamp;
				mods.Add("overext x" + ProfileOverextendedDamp.ToString("0.00"));
				profOverextDamped = true;
			}

			// ACTIVE-WEIGHT CAP — 2026-09-16 — crowding modulator, see property doc above for the finding
			// and its caveats. Uses _activeW directly (the same value Recorder already publishes as
			// cnlActive), so this is gradeable with zero Recorder/SneakyCore changes even before the
			// audit-string wiring below is added anywhere else.
			if (ActiveWeightCapDamp < 1.0 && _activeW > ActiveWeightCap)
			{
				_conviction *= ActiveWeightCapDamp;
				mods.Add("active-weight x" + ActiveWeightCapDamp.ToString("0.00"));
				activeWeightDamped = true;
			}

			_conviction = Math.Max(0.0, Math.Min(1.0, _conviction));
			_modLabel = mods.Count == 0 ? "none" : string.Join(" · ", mods.ToArray());

			// ── HARD VETOES (first match wins; each zeroes conviction) ───────────
			if (!_vetoed && VetoChop && stf != null && !stf.Trending)
			{ _vetoed = true; _vetoReason = "chop (STF chop index)"; }

			if (!_vetoed && VetoHighVolRegime && regime != null && regime.Regime == 2)
			{ _vetoed = true; _vetoReason = "high-vol regime"; }

			// 2026-08-06 — the data-supported COMPANION to the veto above, added after
			// grade_sneaky.py's tables 9b/9c (checked across the full corpus AND both E and F
			// instances independently) showed REGIME=normal negative in EVERY conviction bucket
			// and EVERY voter-count bucket — ruling out both "conviction is just confounded by
			// regime" and "normal-regime rows are just thin panels in disguise" as explanations.
			// SneakyRegime's own source groups Regime<=1 as "Trending" together with LOW — but
			// regime 1 measurably does NOT behave like a stable, tradeable condition the way LOW
			// does; the working theory is that it's a TRANSITIONAL state (the belief model passing
			// between low and high) rather than a steady one, which would explain why neither
			// conviction nor panel size rescues it. Off by default — same "reasoned, not yet
			// switched on live" caution as every new gate added tonight. This shares Council's
			// existing generic cnlVeto/cnlPreVetoBias recording, so it's already gradeable via
			// grade_sneaky.py's table 17b with no Recorder change needed.
			if (!_vetoed && VetoNormalRegime && regime != null && regime.Regime == 1)
			{ _vetoed = true; _vetoReason = "normal-vol regime"; }

			if (!_vetoed && VetoLiquidityWall && liq != null && _bias != 0 && liq.BlocksEntry(_bias, WallVetoTicks))
			{ _vetoed = true; _vetoReason = "liquidity wall within " + WallVetoTicks + "t"; }

			if (!_vetoed && VetoNearLevel && lv != null && _bias != 0 && lv.InPath(_bias, LevelVetoAtr))
			{ _vetoed = true; _vetoReason = (lv.NearestName ?? "level") + " within " + LevelVetoAtr.ToString("0.00") + " ATR"; }

			if (!_vetoed && VetoMtfOpposed && mtf != null && _bias != 0 && mtf.Bias != 0 && mtf.Bias != _bias)
			{ _vetoed = true; _vetoReason = "MTF opposed (" + (mtf.Dirs ?? "") + ")"; }

			if (!_vetoed && VetoKillWindow && clk != null && clk.InKillWindow)
			{ _vetoed = true; _vetoReason = "kill window (near session close)"; }

			// 2026-08-08 — same pattern as the kill-window veto above, reading a DIFFERENT source:
			// NewsLockout/ActiveNewsLockoutFor already existed in Core (unpublished until tonight's
			// SneakyNewsService port; unconsulted by anything until now). Deliberately uses this
			// specific, single-purpose check rather than the broader SneakyCore.CanEnter (which also
			// folds in kill-switch/governor/drawdown/session-window/rollover) -- that would silently
			// pull in blocking behavior around drawdown/governor that hasn't been reviewed or asked
			// for, and would double up confusingly with the kill-window veto just above.
			string instName = Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : null;
			var newsLockout = !string.IsNullOrEmpty(instName) ? SneakyCore.ActiveNewsLockoutFor(instName) : null;
			if (!_vetoed && VetoNews && newsLockout != null)
			{ _vetoed = true; _vetoReason = "news lockout: " + (newsLockout.Event ?? "?"); }

			// Captured BEFORE the veto zeroes _bias — otherwise a vetoed bar's original
			// directional read is lost entirely, and a grading pass can never ask whether
			// the veto was blocking a winner or a loser (same reasoning as Confluence's
			// per-gate Ok tracking). killWindowActive is the RAW Clock condition,
			// independent of whether 'Veto — Kill Window' is even switched on.
			int preVetoBias = _bias;
			bool killWindowActive = clk != null && clk.InKillWindow;

			if (_vetoed) { _conviction = 0.0; _bias = 0; }

			// PER-PROFILE CONVICTION FLOOR (2026-08-12): confirmed via check_conviction.py against
			// instance E's own recorded data (70 bars, 33 non-vetoed) that Learned's conviction runs
			// structurally weaker than Prior's — mean 8.7%, median 7.1%, 97% below the 0.35 test floor
			// and still 94% below the live 0.2 default. Not a bug: Learned's largest weights sit on
			// voters that partially INVERT a highly-correlated trend bloc (see the profileFlip comments
			// above), which is exactly what gives the regression its real out-of-sample edge — but it
			// also means net score rarely builds up to a large fraction of activeW the way Prior's
			// same-signed voters do. A single shared floor tuned for Prior's scale was leaving Learned
			// at essentially 0 trades (1/70 bars actionable). Same "yield to the profile in effect"
			// pattern as the WeightProfile-conditional AddVote flips — just for the floor, not the vote.
			_sizeMult = (_vetoed || _conviction < ConvictionFloor) ? 0.0 : _conviction;

			// 2026-08-07 — sparkline history, same point in the sequence as the real Council's own
			// (after veto resolution, so a vetoed bar correctly logs as flat/zero, not its pre-veto lean).
			_convHist.Add(_bias >= 0 ? _conviction : -_conviction);
			if (_convHist.Count > HistMax) _convHist.RemoveAt(0);

			// ── audit string ─────────────────────────────────────────────────────
			var sb = new StringBuilder();
			foreach (var c in _chips)
			{
				if (sb.Length > 0) sb.Append('|');
				sb.Append(c.Key).Append(c.Value > 0 ? ":+" : c.Value < 0 ? ":-" : ":0");
			}
			// 2026-08-07 — name what the off-chart gate excluded. Without this a declared voter
			// that's simply not attached looks identical to one that's attached and abstaining.
			if (_skippedOffChart.Count > 0)
			{
				if (sb.Length > 0) sb.Append('|');
				var off = new string[_skippedOffChart.Count];
				_skippedOffChart.CopyTo(off);
				Array.Sort(off, StringComparer.Ordinal);
				sb.Append("off-chart:").Append(string.Join(",", off));
			}
			_reasons = sb.ToString();
			if (activeWeightDamped) _reasons += " · active-weight-cap";

			Values[0][0] = _bias;
			Values[1][0] = _conviction;
			Values[2][0] = _sizeMult;

			// ── DEBOUNCED PULSE PLOTS ────────────────────────────────────────────
			// The Council's native output is a HELD state (Bias persists every bar the
			// verdict holds). Bar-tag automation needs an EDGE instead, or it re-arms on
			// every bar of the same regime. These fire once, on the bar the verdict
			// BECOMES actionable in a direction, then stay quiet while it holds — the
			// same debounce contract as SneakyConfluence's BuySignal/SellSignal.
			Values[3][0] = 0.0;
			Values[4][0] = 0.0;
			// Belt AND braces. AddPlot's transparent stroke only applies to a NEWLY added
			// instance: NinjaTrader SERIALIZES plot colour per chart instance, so an
			// indicator already on the chart keeps whatever colour it was saved with and
			// ignores the code change. PlotBrushes is a RUNTIME override that beats the
			// saved colour, so setting it every bar guarantees these never paint —
			// existing instances included, with no remove-and-re-add needed.
			PlotBrushes[3][0] = Brushes.Transparent;
			PlotBrushes[4][0] = Brushes.Transparent;
			// The VALUE stays 0.0 for automation, but a PlotStyle.Dot draws that zero as a
			// dot on the price panel — a solid row of them thousands of points below price.
			// Hiding the brush on non-firing bars kills the artifact without touching the
			// numbers any executor reads.

			int actionable = (!_vetoed && _bias != 0 && _sizeMult > 0.0) ? _bias : 0;
			if (actionable != _prevFiredBias)
			{
				if (actionable > 0)
				{
					Values[3][0] = BuySignalValue;
					// 2026-08-19 — was Draw.Dot in Brushes.Lime, IDENTICAL shape+color to Confluence's
					// own marker — indistinguishable circles when both indicators are on one chart.
					// Tried arrows (cyan/orange) next, but the chart already has OTHER indicators
					// plotting triangles, so shape alone wasn't a reliable tell either. Settled on
					// literal text tags instead: "SSCB"/"SSCS" (buy/sell), same text AND same color
					// as Confluence's own tags below — the two sources are meant to read as one
					// unified "the suite fired" signal here, not as visually distinct sources.
					if (DrawSignalTags)
						Draw.Text(this, SignalTagPrefix + "_Buy_" + CurrentBar, "SSCB", 0,
							Low[0] - SignalMarkerOffsetTicks * TickSize, Brushes.Lime);
				}
				else if (actionable < 0)
				{
					Values[4][0] = SellSignalValue;
					if (DrawSignalTags)
						Draw.Text(this, SignalTagPrefix + "_Sell_" + CurrentBar, "SSCS", 0,
							High[0] + SignalMarkerOffsetTicks * TickSize, Brushes.Red);
				}
				_prevFiredBias = actionable;
			}

			if (ShowSetupHelp && !_setupPrinted)
			{
				_setupPrinted = true;
				try { Print(SetupGuideText()); } catch { }
			}

			// ── PUBLISH — one CouncilState for this scope ────────────────────────
			// ClockPhase/Rvol/MtfBias/LevelInPath/LevelName were hardcoded "unknown" sentinels until
			// 2026-08-02 — those axes genuinely weren't built yet, and inventing values would have
			// silently misled any consumer (esp. the offline ML trainer) reading the decision vector.
			// SneakyClock/SneakyParticipation/SneakyLocation/SneakyMtf now exist, so these are real
			// reads — still falling back to the same documented sentinels when a given sensor is absent.
			int    clockPhase = clk != null ? clk.Phase : -1;
			double rvol       = part != null ? part.Rvol : double.NaN;
			int    mtfBias    = mtf != null ? mtf.Bias : 0;
			bool   levelInPath = lv != null && _bias != 0 && lv.InPath(_bias, LevelVetoAtr);
			string levelNameForVector = levelInPath ? lv.NearestName : null;

			try
			{
				SneakyCore.SetCouncilState(
					sc,
					SneakyCore.BarTag(BarsPeriod),
					Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : null,
					_bias, _conviction, _sizeMult,
					_agree, _disagree, _voters, _vetoed, _vetoReason,
					_reasons, "SneakyCouncil_v" + COUNCIL_VERSION,
					Time[0].ToUniversalTime(), State == State.Historical,
					null,
					_votes, _voteW,
					_netScore, _activeW,
					clockPhase, rvol, mtfBias, levelInPath, levelNameForVector,
					null, COUNCIL_VERSION,
					preVetoBias, killWindowActive,
					breadthDamped, squeezeDamped, dryUpDamped, inValueDamped,
					InstanceTag, profOverextDamped);
				_lastPublishOk = true;
			}
			catch (Exception ex) { NotePublishFail(ex); }

			// 2026-08-19 — heartbeat: LAST statement of a fully-completed OnBarUpdate pass, on
			// purpose. If anything above threw uncaught, this line never runs, so the card's
			// "last update" reading genuinely reflects whether the per-bar computation is still
			// alive, not just whether a bar happened to close.
			_lastGoodBarAt = DateTime.Now;
		}

		/// <summary>Plain-English setup guide, printed once to the Output window on load.</summary>
		private static string SetupGuideText()
		{
			var b = new System.Text.StringBuilder();
			b.AppendLine("════════════ SENTINEL COUNCIL — SETUP ════════════");
			b.AppendLine("HOW IT DECIDES: this is a WEIGHTED VOTE, not an all-must-agree cascade.");
			b.AppendLine("  Each attached sensor casts sign(direction) x weight. They are summed.");
			b.AppendLine("  Bias flips only once the net exceeds the Deadband share of active weight.");
			b.AppendLine("  Conviction = |net| / activeWeight. SizeMult = 0 below the Conviction Floor.");
			b.AppendLine("  UNLIKE SneakyConfluence, adding sensors here does NOT reduce signals.");
			b.AppendLine("");
			b.AppendLine("NOTHING IS REQUIRED. Absent sensor = it abstains, and activeWeight shrinks.");
			b.AppendLine("  With 1-2 voters attached, conviction is meaningless. Aim for 6+.");
			b.AppendLine("  The card's 'voters / agree / against' and 'net / active' rows show this.");
			b.AppendLine("");
			b.AppendLine("READ THE CHIPS: green = agrees with the verdict, red = disagrees, grey = neutral.");
			b.AppendLine("");
			b.AppendLine("IMPORTANT — WHY HIGH CONVICTION CAN STILL BE WEAK:");
			b.AppendLine("  Most voters (TRND, ADX, CCI, STRC, SPRT, PSAR, AVMA, VDYA, STF) are all");
			b.AppendLine("  derived from the SAME price stream. When they agree that is not many");
			b.AppendLine("  witnesses confirming a fact - it is one witness saying it many ways.");
			b.AppendLine("  CVB is the exception: it is confirmed by actual order flow.");
			b.AppendLine("");
			b.AppendLine("BAR TYPES — set on the CHART (Data Series > Bar Type), never in this dialog.");
			b.AppendLine("  ONE per chart. They feed two voters here:");
			b.AppendLine("   - BRK  works with ANY Sneaky bar type (TBars / Drift / Lattice / Effort).");
			b.AppendLine("   - CVB  works with SneakyDrift ONLY. It is the flow-confirmed voter and");
			b.AppendLine("          the only non-price-derived voice on the panel.");
			b.AppendLine("  So: on a Drift chart you get BRK + CVB. On any other, BRK only.");
			b.AppendLine("  WARNING Drift draws Heikin-Ashi bodies: CANDLE COLOUR IS NOT BRICK DIRECTION.");
			b.AppendLine("");
			b.AppendLine("WEIGHTS: group 3 are the published defaults. GROUP 4 ARE UNVALIDATED GUESSES.");
			b.AppendLine("  Treat every weight as a hypothesis to be graded, not a tuned parameter.");
			b.AppendLine("═════════════════════════════════════════════════");
			return b.ToString();
		}


		// ── DECLARED ROSTER ──────────────────────────────────────────────────────
		// Base weight of every voter this Council knows about, kind-aware.
		// STATE voters always contribute to the denominator (present or not) so that a
		// missing sensor dilutes conviction. TRIGGER voters contribute only when they
		// actually fired — a quiet or absent trigger is absence of evidence, not
		// evidence against, and must not drag the reading down every quiet bar.
		private double _declaredTriggerFired;

		private double DeclaredWeight()
		{
			// STATE voters — always counted.
			double w = WeightTrend + WeightCci + WeightAdx + WeightEnvelope
					 + WeightStructure + WeightSuperTrend + WeightStf + WeightSar
					 + WeightAdxvma + WeightVidya + WeightBrick
					 + WeightConviction + WeightPressure + WeightIntermarket + WeightFlux + WeightTrendFusion + WeightArch
					 + WeightCvd;   // 2026-08-16 — new (see CHANGELOG)

			// TRIGGER voters — counted only for the ones that fired this bar.
			w += _declaredTriggerFired;
			return w;
		}

		// ── glass card ───────────────────────────────────────────────────────────
		protected override void OnRender(NinjaTrader.Gui.Chart.ChartControl chartControl, NinjaTrader.Gui.Chart.ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			// 2026-08-09 — also retry the off-chart scan from OnRender, not just OnBarUpdate.
			// OnBarUpdate only fires when a new bar forms — once the market's closed and the
			// chart is caught up to "now," it stops firing entirely, so if the scan's one shot
			// during historical replay ever failed to resolve, there was no other trigger to
			// retry it until a new bar eventually arrived (potentially a whole weekend away).
			// OnRender fires on any redraw/pan/zoom regardless of new bars, so this closes that
			// gap. Cheap to call here too -- RefreshOnChartTypes() is already throttled to once
			// per 5 seconds internally, so this doesn't add real scanning overhead.
			RefreshOnChartTypes();
			// 2026-08-09 — the scan mechanism above resolves correctly (confirmed via diagnostic log:
			// every scan finds all 30 real indicator types). The actual remaining bug: _skippedOffChart
			// -- the set the CARD displays -- only ever gets recomputed once per bar, inside
			// OnBarUpdate. On a closed weekend with no new bars, that never runs again after the
			// initial historical replay, so the card stays frozen on whatever it evaluated on the
			// very first bar -- before the scan had cycled enough times to stabilize -- even though
			// _onChartTypes itself has long since resolved correctly underneath it. Recomputing the
			// DISPLAY here too decouples the off-chart diagnostic (which is really "is this sensor
			// correctly wired right now," not "what happened last bar") from bar formation, while
			// leaving the actual trading verdict (conviction/voters/bias) correctly bar-gated as
			// every other NinjaTrader indicator's verdict should be.
			if (_onChartResolved)
			{
				lock (_onChartGate)
				{
					_skippedOffChart.Clear();
					foreach (var kv in ChartDerivedPublisher)
						if (!OnChart(kv.Key)) _skippedOffChart.Add(kv.Key);
				}
				// 2026-08-09 — the chart's OWN card display was fixed above, but Cockpit (and any other
				// consumer reading published CouncilState via AllCouncilStates()) reads _reasons off the
				// PUBLISHED state, which is only ever rebuilt inside OnBarUpdate -- confirmed via a real
				// screenshot showing Cockpit displaying a stale, near-total off-chart list while the
				// chart's own card correctly showed only 1-2 genuinely missing sensors. A full
				// SetCouncilState re-publish from here would need several new cached fields and risks
				// tripping the same-publisher-twice contention logic, so instead: surgically replace
				// just the off-chart segment of the already-published Reasons string (preserving the
				// vote-chip portion, which SHOULD stay bar-gated -- votes genuinely don't change between
				// bars, only chart-attachment status can meaningfully change independent of one).
				try
				{
					string sc2 = Scope();
					if (!string.IsNullOrEmpty(sc2))
					{
						var parts = (_reasons ?? "").Split('|');
						var kept = new List<string>(parts.Length);
						foreach (var p in parts)
							if (!string.IsNullOrEmpty(p) && !p.StartsWith("off-chart:", StringComparison.Ordinal)) kept.Add(p);
						if (_skippedOffChart.Count > 0)
						{
							var off2 = new string[_skippedOffChart.Count];
							_skippedOffChart.CopyTo(off2);
							Array.Sort(off2, StringComparer.Ordinal);
							kept.Add("off-chart:" + string.Join(",", off2));
						}
						SneakyCore.UpdateCouncilReasons(sc2, string.Join("|", kept));
					}
				}
				catch (Exception _sx) { SneakyCore.Swallow("SneakyCouncil.PublishFreshReasons", _sx); }
			}
			if (RenderTarget == null || ChartPanel == null) return;
			if (_sp == null) _sp = new SneakySkin.Painter();
			_sp.Begin(RenderTarget);
			try { if (ShowCard) RenderCard(); }
			catch (Exception ex)
			{
				if ((DateTime.UtcNow - _lastRenderExLog).TotalMilliseconds > 15000)
				{
					_lastRenderExLog = DateTime.UtcNow;
					try { Print("[SneakyCouncil] RenderCard() threw: " + ex.GetType().Name + ": " + ex.Message); } catch { }
				}
			}
			try { _sp.End(); } catch { }
		}

		private void RenderCard()
		{
			// Height grows with the chip grid so 18 voters never overflow the card.
			int rows = (_chips.Count + CHIPS_PER_ROW - 1) / CHIPS_PER_ROW;
			float cw = 300f;
			// 146f = where the chip grid starts inside the card, +10 bottom margin,
			// +30 for Card()'s own 15px padding top and bottom. The previous formula
			// was 22px short and silently clipped the final chip row.
			// 2026-08-07 — +34f more for the new sparkline row (20f tall + margin above/below).
			// 2026-08-19 — +18f more for the heartbeat/publish-health line at the bottom (see
			// _lastGoodBarAt / _lastPublishOk comments near their declarations).
			float ch = 146f + Math.Max(1, rows) * CHIP_H + 10f + 30f + 34f + 18f;
			var slot = SneakySkin.CardLayout.Place(this, ChartPanel,
				ChartPanel.X, ChartPanel.Y, ChartPanel.W, ChartPanel.H, CardCorner, cw, ch);

			var col = _vetoed ? SneakySkin.CWarn
					: _bias > 0 ? SneakySkin.CUp
					: _bias < 0 ? SneakySkin.CDown : SneakySkin.CMute;

			var r = _sp.Card(slot.X, slot.Y, cw, ch, col);
			_sp.Dot(r.Left + 5f, r.Top + 8f, col, _sizeMult > 0.0);
			// 2026-08-19 — width trimmed from -20f to -66f (matches Recorder's own header/pill
			// layout) to leave room for the health pill below, so a long InstanceTag never runs
			// underneath it.
			_sp.Text("COUNCIL" + (!string.IsNullOrEmpty(InstanceTag) ? "   " + InstanceTag : ""),
				r.Left + 16f, r.Top, r.Width - 66f, 16f, SneakySkin.CInk, 11f, true);

			// 2026-08-19 — health pill: NO PUB beats STALE beats LIVE, same "worse thing wins"
			// precedence as everywhere else in the suite. NO PUB means the seam everything else
			// reads (Confluence/Recorder/Cockpit) is stale RIGHT NOW even if this card looks fine.
			// STALE means this indicator hasn't finished a full bar pass in a while — could be a
			// genuinely quiet/closed market, could be stuck; the trader judges which from context,
			// this just makes the fact visible instead of requiring after-the-fact corpus digging.
			double sinceGoodMin = _lastGoodBarAt == DateTime.MinValue
				? double.PositiveInfinity : (DateTime.Now - _lastGoodBarAt).TotalMinutes;
			bool stale = sinceGoodMin > HeartbeatWarnMinutes;
			string hst = !_lastPublishOk ? "NO PUB" : stale ? "STALE" : "LIVE";
			var hstCol = !_lastPublishOk ? SneakySkin.CDown : stale ? SneakySkin.CWarn : SneakySkin.CAccent;
			_sp.Pill(hst, r.Right, r.Top - 1f, hstCol);

			string pill = _vetoed ? "VETOED" : _bias > 0 ? "LONG" : _bias < 0 ? "SHORT" : "FLAT";
			_sp.Text(pill, r.Left, r.Top + 24f, r.Width, 20f, col, 15f, true);

			_sp.Text("conviction " + (_conviction * 100.0).ToString("0") + "%   ·   size x" + _sizeMult.ToString("0.00")
				+ "   ·   floor " + ConvictionFloor.ToString("0.00") + " [" + WeightProfile + "]",
				r.Left, r.Top + 48f, r.Width, 14f, SneakySkin.CInk2, 10.5f);

			_sp.Text("voters " + _voters + "   agree " + _agree + "   against " + _disagree,
				r.Left, r.Top + 66f, r.Width, 14f, SneakySkin.CInk2, 10.5f);

			_sp.Text("net " + _netScore.ToString("0.00") + " / declared " + DeclaredWeight().ToString("0.00")
				+ "  (awake " + _activeW.ToString("0.00") + ")",
				r.Left, r.Top + 82f, r.Width, 14f, SneakySkin.CMute, 10f);

			// 2026-08-07 — append what the off-chart gate excluded onto this same line (reuses the
			// existing slot rather than growing the card again) so "why is my voter count low" is
			// answerable by looking at the chart, not by digging through recorded cnlReasons data.
			// 2026-08-09 — distinguish "scan hasn't resolved yet" from "confirmed genuinely off-chart"
			// -- both used to look identical (a full list of every gated tag), which read as alarming
			// during the ordinary first few seconds after a compile/restart, indistinguishable from a
			// real problem. Checked _onChartResolved directly rather than infer it from the tag count.
			// 2026-08-09 — truncate rather than let a long off-chart list overflow into the next
			// line's space. With ~20 gateable tags, a bad scan could list all of them at once,
			// which is far too long for one line at this width/font and was overlapping the
			// veto/conviction-floor line directly below it -- illegible, not just untidy.
			string offChartSuffix;
			if (!_onChartResolved) offChartSuffix = "   off-chart: scanning...";
			else if (_skippedOffChart.Count == 0) offChartSuffix = "";
			else
			{
				const int maxShown = 4;
				var off = new string[_skippedOffChart.Count];
				_skippedOffChart.CopyTo(off);
				Array.Sort(off, StringComparer.Ordinal);
				string shown = string.Join(",", off, 0, Math.Min(maxShown, off.Length));
				int extra = off.Length - maxShown;
				offChartSuffix = "   off-chart: " + shown + (extra > 0 ? " +" + extra + " more" : "");
			}
			_sp.Text("damp: " + _modLabel + offChartSuffix, r.Left, r.Top + 98f, r.Width, 14f, SneakySkin.CMute, 10f);

			if (_vetoed)
				_sp.Text("VETO — " + _vetoReason, r.Left, r.Top + 114f, r.Width, 14f, SneakySkin.CWarn, 10.5f, true);
			else if (_sizeMult <= 0.0)
				_sp.Text("below conviction floor (" + ConvictionFloor.ToString("0.00") + ")",
					r.Left, r.Top + 114f, r.Width, 14f, SneakySkin.CMute, 10.5f);

			// ── voter chips ──────────────────────────────────────────────────────
			// Manual spec: green = agrees with the fused bias, red = disagrees,
			// grey = neutral/abstaining. When the verdict is FLAT or vetoed there is no
			// bias to agree WITH, so chips fall back to their own direction's colour.
			_sp.Text("VOTERS   green=agrees  red=against  \u2191long \u2193short",
				r.Left, r.Top + 132f, r.Width, 12f, SneakySkin.CMute, 8f, true);

			float chipW = r.Width / CHIPS_PER_ROW;
			for (int i = 0; i < _chips.Count; i++)
			{
				var kv = _chips[i];
				SharpDX.Color4 cc;
				if (kv.Value == 0)                cc = SneakySkin.CMute;
				else if (_bias == 0)              cc = kv.Value > 0 ? SneakySkin.CUp : SneakySkin.CDown;
				else if (kv.Value == _bias)       cc = SneakySkin.CUp;
				else                              cc = SneakySkin.CDown;

				// COLOUR = agreement with the verdict (green agrees, red disagrees, grey
				// neutral) — that is the manual's spec. But on a SHORT verdict a green chip
				// is voting SHORT, which reads backwards to anyone expecting green=bullish.
				// The arrow carries the DIRECTION so both are legible at a glance:
				//   TRND↓ green  = voting short, agrees with a short verdict
				//   ADX↑  red    = voting long,  disagrees
				string glyph = kv.Value > 0 ? "\u2191" : kv.Value < 0 ? "\u2193" : "\u00b7";
				float cx = r.Left + (i % CHIPS_PER_ROW) * chipW;
				float cy = r.Top + 146f + (i / CHIPS_PER_ROW) * CHIP_H;
				_sp.Text(kv.Key + glyph, cx, cy, chipW, CHIP_H, cc, 8.5f, kv.Value != 0);
			}

			// 2026-08-07 — signed-conviction sparkline, same idea as the real Council's own card:
			// positive (long conviction) plots above the midline, negative (short) below, using the
			// SAME colour as the header/card border so it reads as one continuous verdict, not a
			// separate widget. Placed just below the last chip row, growing with it automatically.
			_sp.Sparkline(r.Left, r.Top + 146f + Math.Max(1, rows) * CHIP_H + 6f, r.Width, 20f, _convHist, col);

			// 2026-08-19 — heartbeat line: see _lastGoodBarAt/hst comments above. "no update yet"
			// covers the first bar of a session, before _lastGoodBarAt has ever been stamped.
			string agoText = _lastGoodBarAt == DateTime.MinValue ? "no update yet"
				: sinceGoodMin < 1.0 ? "updated just now"
				: sinceGoodMin < 60.0 ? "updated " + sinceGoodMin.ToString("0") + "m ago"
				: "updated " + (sinceGoodMin / 60.0).ToString("0.0") + "h ago";
			string pubText = _lastPublishOk ? "" : "   ·   seam publish failing (see Output tab)";
			_sp.Text(agoText + pubText,
				r.Left, r.Top + 146f + Math.Max(1, rows) * CHIP_H + 30f, r.Width, 14f,
				stale || !_lastPublishOk ? SneakySkin.CWarn : SneakySkin.CMute, 9.5f);

			// ── scroll-mode conductor ──
			// Council renders correctly on every observed frame across extensive testing, unlike some
			// scroll-mode sub-panel indicators whose OnRender intermittently doesn't get invoked at all.
			// Diagnostic logs (2026-08-02) showed a chart-wide redraw heartbeat helps but doesn't fully
			// close the gap. Real fix: Council draws the selected card DIRECTLY via ISneakyScrollCard,
			// so it no longer depends on that card's own native panel redrawing at all. The old
			// staleness-placeholder stays as a safety net for the rare case a card doesn't implement the
			// interface yet, or its DrawScrollCard itself throws.
			try
			{
				bool drew = SneakySkin.CardLayout.DrawSelectedCard(ChartControl, RenderTarget);

				if (!drew)
				{
					string fallback = SneakySkin.CardLayout.CheckScrollFallback(ChartControl);
					if (fallback != null)
					{
						float fx = ChartControl.ChartPanels[0].X + 12f, fy = ChartControl.ChartPanels[0].Y + 12f;
						var fr = _sp.Card(fx, fy, 300f, 56f, SneakySkin.CMute);
						_sp.Text(fallback, fr.Left + 12f, fr.Top + 10f, fr.Width - 24f, 16f, SneakySkin.CInk, 11f, true);
						_sp.Text("hasn't rendered recently \u2014 rotation still alive", fr.Left + 12f, fr.Top + 30f, fr.Width - 24f, 14f, SneakySkin.CMute, 9f);
					}
				}
			}
			catch { }
		}

		[Browsable(false)] [XmlIgnore] public Series<double> Bias       => Values[0];
		[Browsable(false)] [XmlIgnore] public Series<double> Conviction => Values[1];
		[Browsable(false)] [XmlIgnore] public Series<double> SizeMult   => Values[2];
		[Browsable(false)] [XmlIgnore] public Series<double> BuySignal  => Values[3];
		[Browsable(false)] [XmlIgnore] public Series<double> SellSignal => Values[4];

		#region Properties

		[NinjaScriptProperty] [Range(0.5, 120.0)]
		[Display(Name = "Seam Staleness (sec)", GroupName = "1. Fusion", Order = 0)]
		public double StaleSec { get; set; }

		[NinjaScriptProperty] [Range(0.0, 0.9)]
		[Display(Name = "Deadband", Description = "FLAT unless |netScore| exceeds this fraction of activeW. Manual default 0.15.", GroupName = "1. Fusion", Order = 1)]
		public double Deadband { get; set; }

		[NinjaScriptProperty] [Range(0.0, 1.0)]
		[Display(Name = "Conviction Floor", Description = "SizeMult is 0 below this. Value shown is whatever this chart's own Weight Profile last set it to — same as every Weight* property below: pick Prior or Learned above, this (and they) update automatically. Prior default 0.2, Learned default 0.10 (see ApplyWeightProfile() for the derivation of the Learned figure — calibrated 2026-08-12 from check_conviction.py against real recorded data).", GroupName = "1. Fusion", Order = 2)]
		public double ConvictionFloor { get; set; }

		[NinjaScriptProperty] [Range(0.0, 500.0)]
		[Display(Name = "Active-Weight Cap", Description = "Above this total ACTIVE voter weight (sum of every "
			+ "voter's weight whose vote this bar was non-zero, i.e. Recorder's own cnlActive field — a MAGNITUDE, "
			+ "not the net/activeWeight RATIO conviction already is), conviction is damped by Active-Weight Cap Damp. "
			+ "2026-09-16 — grade_sneaky.py's cnlActive breakdown WITHIN Learned2's own already-actionable "
			+ "(cnlSize>0) rows showed 45-60 total active weight scoring +19.80 edge (n=175) vs 60-80 scoring "
			+ "-5.81 (n=381) vs >=80 scoring -16.31 (n=98) — i.e. once enough total voter weight piles onto one "
			+ "side at once, the panel looks like it's catching a move already extended rather than confirming "
			+ "one. Per-voter diagnostic ruled out any single voter's math as the cause. UNVALIDATED ON HELD-OUT "
			+ "DATA — same historical rows the split was found on. 999 (default) is effectively unreachable given "
			+ "current weight totals, so this is inert until deliberately lowered.", GroupName = "1. Fusion", Order = 4)]
		public double ActiveWeightCap { get; set; }

		[NinjaScriptProperty] [Range(0, 30)]
		[Display(Name = "Min Voters (breadth damp)", Description = "Below this, conviction is scaled by voters/MinVoters.", GroupName = "1. Fusion", Order = 3)]
		public int MinVoters { get; set; }

		[NinjaScriptProperty] [Display(Name = "Veto — Liquidity Wall", GroupName = "2. Vetoes", Order = 0)]
		public bool VetoLiquidityWall { get; set; }

		[NinjaScriptProperty] [Range(1, 200)]
		[Display(Name = "Wall Veto Distance (ticks)", GroupName = "2. Vetoes", Order = 1)]
		public int WallVetoTicks { get; set; }

		[NinjaScriptProperty] [Display(Name = "Veto — Near Structural Level", Description = "Vetoes when SneakyLocation's nearest level (VWAP/PDH/PDL/OR/IB/session H-L) sits in the trade's path within 'Level Veto Distance'. Fails open when SneakyLocation isn't attached.", GroupName = "2. Vetoes", Order = 7)]
		public bool VetoNearLevel { get; set; }

		[NinjaScriptProperty] [Range(0.05, 5.0)]
		[Display(Name = "Level Veto Distance (ATR)", GroupName = "2. Vetoes", Order = 8)]
		public double LevelVetoAtr { get; set; }

		[NinjaScriptProperty] [Display(Name = "Veto — MTF Opposed", Description = "Vetoes when SneakyMtf's higher-timeframe consensus actively leans the OPPOSITE direction (neutral/mixed does not block). Fails open when SneakyMtf isn't attached.", GroupName = "2. Vetoes", Order = 9)]
		public bool VetoMtfOpposed { get; set; }

		[NinjaScriptProperty] [Display(Name = "Veto — Chop (STF)", GroupName = "2. Vetoes", Order = 2)]
		public bool VetoChop { get; set; }

		[NinjaScriptProperty] [Display(Name = "Veto — High-Vol Regime", GroupName = "2. Vetoes", Order = 3)]
		public bool VetoHighVolRegime { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Require Sensor On Chart",
			Description = "2026-08-07 — ported from the real Sentinel Suite's own Council v1.11.0. When ON (default), a chart-derived sensor (Trend/CCI/ADX/VolEnvelope/CompressionBase/WAE/GodReversal/STF/Structure/Exhaustion/ADXVMA/SuperTrend/ParabolicSAR/ZScore/VIDYA/Harmonic/Regime/Participation/MTF/TrendFusion/CVD) must actually be attached to THIS chart to vote, veto, or damp — closes a real leak where a sensor on any OTHER chart sharing the same instrument+bar-type could otherwise silently influence this Council. Instrument/bar-type-level seams (Liquidity, Level, Profile, Brick, CVB, Flux, Pressure, Intermarket, Eye, Flow, Clock) are never gated — they have no chart of their own.",
			GroupName = "2. Vetoes", Order = 12)]
		public bool RequireSensorOnChart { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Veto — Normal-Vol Regime",
			Description = "2026-08-06 — data-supported companion to the veto above. Vetoes when SneakyRegime reports Regime==1 (\"normal\"/MED vol). grade_sneaky.py's tables 9b/9c showed this regime negative across every conviction AND voter-count bucket, on the full corpus and on two separate Council instances independently — neither conviction nor panel size rescues it, ruling out both as the explanation. Working theory: regime 1 is a TRANSITIONAL belief state, not a stable one, despite SneakyRegime's own code grouping it with LOW as \"Trending\". Fails open when SneakyRegime isn't attached. Off by default — validate on fresh recorded data (grade_sneaky.py table 17b) before relying on it.",
			GroupName = "2. Vetoes", Order = 13)]
		public bool VetoNormalRegime { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Veto — Kill Window (Clock)",
			Description = "Vetoes ANY direction when SneakyClock reports the near-close kill window. Direction-agnostic, like the dry-up damp: bad execution conditions hurt either side equally. INSTRUMENT-keyed — SneakyClock doesn't need to be on THIS chart specifically, just publishing somewhere for this instrument. Fails open (does nothing) when Clock isn't attached anywhere.",
			GroupName = "2. Vetoes", Order = 6)]
		public bool VetoKillWindow { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Veto — News Lockout",
			Description = "2026-08-08 — new. Vetoes ANY direction when SneakyNewsService reports an active high-impact economic-calendar lockout window for this instrument (FOMC/NFP/CPI etc.). Direction-agnostic. Reads SneakyCore.ActiveNewsLockoutFor directly (not the broader CanEnter, which also folds in kill-switch/governor/drawdown checks). Fails open when SneakyNewsService isn't running or hasn't published anything yet. Off by default until validated.",
			GroupName = "2. Vetoes", Order = 14)]
		public bool VetoNews { get; set; }

		[NinjaScriptProperty] [Range(0.0, 1.0)]
		[Display(Name = "Squeeze Damp", GroupName = "2. Vetoes", Order = 4)]
		public double SqueezeDamp { get; set; }

		[NinjaScriptProperty] [Range(0.0, 1.0)]
		[Display(Name = "Dry-Up Damp (Participation)",
			Description = "Multiplies conviction when SneakyParticipation reports a participation vacuum. Participation has NO DIRECTION so it never votes — but the same unanimous panel means less on dead volume than on backed volume. 1.00 disables.",
			GroupName = "2. Vetoes", Order = 5)]
		public double DryUpDamp { get; set; }

		[NinjaScriptProperty] [Range(0.0, 1.0)]
		[Display(Name = "In-Value Damp (Profile)",
			Description = "Multiplies conviction when SneakyProfile reports price ACCEPTED inside today's value area (Location == 0) — the chop case per the seam's own design comment. Fails open (does nothing) when SneakyProfile isn't attached. 1.00 disables.",
			GroupName = "2. Vetoes", Order = 10)]
		public double ProfileValueAreaDamp { get; set; }

		[NinjaScriptProperty] [Range(0.0, 1.0)]
		[Display(Name = "Overextension Damp (Profile)",
			Description = "2026-08-06 — data-supported companion to the In-Value damp above, using Signal (POC-reversion) instead of Location. Multiplies conviction when SneakyProfile's Signal AGREES with the panel's bias — price already stretched away from value in the trade's own direction. grade_sneaky.py's PROFILE REVERSION SIGNAL table showed this is the WORSE condition, not confirmation. Fails open when SneakyProfile isn't attached. 1.00 disables — off by default until validated on fresh data.",
			GroupName = "2. Vetoes", Order = 11)]
		public double ProfileOverextendedDamp { get; set; }

		[NinjaScriptProperty] [Range(0.0, 1.0)]
		[Display(Name = "Active-Weight Cap Damp", Description = "Multiplies conviction when activeW exceeds "
			+ "Active-Weight Cap. 1.00 disables (default) — same off-by-default discipline as every other damp "
			+ "here. Do not enable on a live/traded chart until re-confirmed on data recorded AFTER this was added.",
			GroupName = "2. Vetoes", Order = 15)]
		public double ActiveWeightCapDamp { get; set; }

		// ── manual-published weights ──
		// 2026-08-11 — every Weight* Range below widened from (0.0, 5.0) to (0.0, 50.0). The old cap
		// matched the manual weights' 0-1-ish scale; train_council_weights.py's LEARNED magnitudes are
		// raw ridge-regression coefficients on a completely different scale (largest currently 35.381,
		// WeightTrendFusion) — NinjaScript enforces [Range] on ANY assignment, not just UI entry, so
		// ApplyWeightProfile() setting a Learned value above 5.0 was failing outright (confirmed live:
		// "Value of property 'WeightTrend' ... is 10.128 and not in valid range between 0 and 5" in the
		// NinjaScript log, repeating on every Configure). 50.0 gives real headroom above today's
		// largest learned value for future re-fits without needing another Range bump every time.
		[NinjaScriptProperty] [Range(0.0, 50.0)] [Display(Name = "Weight — Trend (manual 1.0)",       GroupName = "3. Weights (published)", Order = 0)] public double WeightTrend { get; set; }
		[NinjaScriptProperty] [Range(0.0, 50.0)] [Display(Name = "Weight — Woodies CCI (manual 0.8)", GroupName = "3. Weights (published)", Order = 1)] public double WeightCci { get; set; }
		[NinjaScriptProperty] [Range(0.0, 50.0)] [Display(Name = "Weight — ADX (manual 0.6)",         GroupName = "3. Weights (published)", Order = 2)] public double WeightAdx { get; set; }
		[NinjaScriptProperty] [Range(0.0, 50.0)] [Display(Name = "Weight — VolEnvelope (manual 0.6)", GroupName = "3. Weights (published)", Order = 3)] public double WeightEnvelope { get; set; }
		[NinjaScriptProperty] [Range(0.0, 50.0)] [Display(Name = "Weight — Compression (manual 0.7)", GroupName = "3. Weights (published)", Order = 4)] public double WeightCompression { get; set; }
		[NinjaScriptProperty] [Range(0.0, 50.0)] [Display(Name = "Weight — WAE (manual 0.7)",         GroupName = "3. Weights (published)", Order = 5)] public double WeightWae { get; set; }
		[NinjaScriptProperty] [Range(0.0, 50.0)] [Display(Name = "Weight — Brick / bar type (manual 0.5)", GroupName = "3. Weights (published)", Order = 6)] public double WeightBrick { get; set; }

		// ── per-chart weight profile (2026-08-11) — see the SetDefaults comment above WeightProfile
		// for the full story. Its own group so it's impossible to miss in the Properties dialog —
		// this is the one setting to change to compare Prior vs Learned on this specific chart.
		[NinjaScriptProperty]
		[Display(Name = "Weight Profile",
			Description = "Prior = the original manual/reference-build weights (safe default). Learned = train_council_weights.py's 2026-08-11 fitted weights (after-lane-fix corpus), which beat Prior in every out-of-sample fold. Per-chart setting — change freely on one chart to test Learned while another chart stays on Prior. Takes effect on next Configure (reattach the indicator, or just OK the Properties dialog).",
			GroupName = "0. Weight Profile", Order = 0)]
		[RefreshProperties(RefreshProperties.All)]   // 2026-09-06 -- forces the Properties grid to re-query GetProperties() (via WeightProfileConverter) the instant this changes, so the matching Learned*/Learned1*/Learned2*/Learned3*/Learned4* Flip Override group shows immediately, without needing to remove/re-add the indicator.
		public SneakyWeightProfile WeightProfile { get; set; }

		// ── 2026-08-29 — per-voter flip override, Learned profile only ──
		// Ignored entirely when WeightProfile == Prior. When Learned, each of these 11 voters'
		// direction is inverted UNLESS its own toggle here is turned OFF — so switching one off
		// puts that single voter back to its Prior (non-inverted) direction while everything else
		// on the chart stays on Learned. See the SetDefaults comment above for why this exists:
		// the 2026-08-28 flip-check found 8 of these 11 currently read backwards on live data.
		[NinjaScriptProperty] [Display(Name = "Learned Flip — TRND", GroupName = "0b. Learned Flip Overrides", Order = 0)] public bool LearnedFlipTrnd { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned Flip — ADX",  GroupName = "0b. Learned Flip Overrides", Order = 1)] public bool LearnedFlipAdx  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned Flip — STRC", GroupName = "0b. Learned Flip Overrides", Order = 2)] public bool LearnedFlipStrc { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned Flip — STF",  GroupName = "0b. Learned Flip Overrides", Order = 3)] public bool LearnedFlipStf  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned Flip — PSAR", GroupName = "0b. Learned Flip Overrides", Order = 4)] public bool LearnedFlipPsar { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned Flip — VDYA", GroupName = "0b. Learned Flip Overrides", Order = 5)] public bool LearnedFlipVdya { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned Flip — WAE",  GroupName = "0b. Learned Flip Overrides", Order = 6)] public bool LearnedFlipWae  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned Flip — ZSC",  GroupName = "0b. Learned Flip Overrides", Order = 7)] public bool LearnedFlipZsc  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned Flip — TFUS", GroupName = "0b. Learned Flip Overrides", Order = 8)] public bool LearnedFlipTfus { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned Flip — ARCH", GroupName = "0b. Learned Flip Overrides", Order = 9)] public bool LearnedFlipArch { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned Flip — VENV", GroupName = "0b. Learned Flip Overrides", Order = 10)] public bool LearnedFlipVenv { get; set; }

		// ── Learned1 (2026-08-29, instance-F-only retrain) flip overrides ──────────────────────
		// Same mechanism as the 11 above, but a SEPARATE set of toggles: Learned1's fitted signs
		// don't necessarily agree with Learned's (different training data, different chart), so
		// sharing one toggle per voter across both profiles would be wrong. Covers all 11 voters
		// above PLUS 6 more (SPRT/CCI/COMP/GREV/EXH/PRS) whose Learned1 fit came back negative but
		// that never had a flip mechanism before — Learned's own numbers never needed one for them.
		[NinjaScriptProperty] [Display(Name = "Learned1 Flip — TRND", GroupName = "0c. Learned1 Flip Overrides", Order = 0)] public bool Learned1FlipTrnd { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned1 Flip — ADX",  GroupName = "0c. Learned1 Flip Overrides", Order = 1)] public bool Learned1FlipAdx  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned1 Flip — STRC", GroupName = "0c. Learned1 Flip Overrides", Order = 2)] public bool Learned1FlipStrc { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned1 Flip — STF",  GroupName = "0c. Learned1 Flip Overrides", Order = 3)] public bool Learned1FlipStf  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned1 Flip — PSAR", GroupName = "0c. Learned1 Flip Overrides", Order = 4)] public bool Learned1FlipPsar { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned1 Flip — VDYA", GroupName = "0c. Learned1 Flip Overrides", Order = 5)] public bool Learned1FlipVdya { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned1 Flip — WAE",  GroupName = "0c. Learned1 Flip Overrides", Order = 6)] public bool Learned1FlipWae  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned1 Flip — ZSC",  GroupName = "0c. Learned1 Flip Overrides", Order = 7)] public bool Learned1FlipZsc  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned1 Flip — TFUS", GroupName = "0c. Learned1 Flip Overrides", Order = 8)] public bool Learned1FlipTfus { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned1 Flip — ARCH", GroupName = "0c. Learned1 Flip Overrides", Order = 9)] public bool Learned1FlipArch { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned1 Flip — VENV", GroupName = "0c. Learned1 Flip Overrides", Order = 10)] public bool Learned1FlipVenv { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned1 Flip — SPRT", GroupName = "0c. Learned1 Flip Overrides", Order = 11)] public bool Learned1FlipSprt { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned1 Flip — CCI",  GroupName = "0c. Learned1 Flip Overrides", Order = 12)] public bool Learned1FlipCci  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned1 Flip — COMP", GroupName = "0c. Learned1 Flip Overrides", Order = 13)] public bool Learned1FlipComp { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned1 Flip — GREV", GroupName = "0c. Learned1 Flip Overrides", Order = 14)] public bool Learned1FlipGrev { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned1 Flip — EXH",  GroupName = "0c. Learned1 Flip Overrides", Order = 15)] public bool Learned1FlipExh  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned1 Flip — PRS",  GroupName = "0c. Learned1 Flip Overrides", Order = 16)] public bool Learned1FlipPrs  { get; set; }

		// ── Learned2 (2026-08-29, de-redundant policy variant of Learned1) flip overrides ──────
		// Learned2 is Learned1's own fit with a weight-reallocation policy layered on top (see the
		// ApplyWeightProfile() branch and the enum doc comment) — it is NOT a separate training run,
		// so these toggles default to the SAME sign choices as Learned1's above. Kept as a fully
		// separate set anyway (rather than reusing Learned1FlipXxx) so a future edit to one profile's
		// flip choice can never silently change the other's.
		[NinjaScriptProperty] [Display(Name = "Learned2 Flip — TRND", GroupName = "0d. Learned2 Flip Overrides", Order = 0)] public bool Learned2FlipTrnd { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned2 Flip — ADX",  GroupName = "0d. Learned2 Flip Overrides", Order = 1)] public bool Learned2FlipAdx  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned2 Flip — STRC", GroupName = "0d. Learned2 Flip Overrides", Order = 2)] public bool Learned2FlipStrc { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned2 Flip — STF",  GroupName = "0d. Learned2 Flip Overrides", Order = 3)] public bool Learned2FlipStf  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned2 Flip — PSAR", GroupName = "0d. Learned2 Flip Overrides", Order = 4)] public bool Learned2FlipPsar { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned2 Flip — VDYA", GroupName = "0d. Learned2 Flip Overrides", Order = 5)] public bool Learned2FlipVdya { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned2 Flip — WAE",  GroupName = "0d. Learned2 Flip Overrides", Order = 6)] public bool Learned2FlipWae  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned2 Flip — ZSC",  GroupName = "0d. Learned2 Flip Overrides", Order = 7)] public bool Learned2FlipZsc  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned2 Flip — TFUS", GroupName = "0d. Learned2 Flip Overrides", Order = 8)] public bool Learned2FlipTfus { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned2 Flip — ARCH", GroupName = "0d. Learned2 Flip Overrides", Order = 9)] public bool Learned2FlipArch { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned2 Flip — VENV", GroupName = "0d. Learned2 Flip Overrides", Order = 10)] public bool Learned2FlipVenv { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned2 Flip — SPRT", GroupName = "0d. Learned2 Flip Overrides", Order = 11)] public bool Learned2FlipSprt { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned2 Flip — CCI",  GroupName = "0d. Learned2 Flip Overrides", Order = 12)] public bool Learned2FlipCci  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned2 Flip — COMP", GroupName = "0d. Learned2 Flip Overrides", Order = 13)] public bool Learned2FlipComp { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned2 Flip — GREV", GroupName = "0d. Learned2 Flip Overrides", Order = 14)] public bool Learned2FlipGrev { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned2 Flip — EXH",  GroupName = "0d. Learned2 Flip Overrides", Order = 15)] public bool Learned2FlipExh  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned2 Flip — PRS",  GroupName = "0d. Learned2 Flip Overrides", Order = 16)] public bool Learned2FlipPrs  { get; set; }

		// ── Learned3 (2026-09-05) flip overrides ──────────────────────────────────────────────
		// Separate profile, same Learned weight magnitudes (see ApplyWeightProfile()'s Learned3
		// branch) — only these 11 flips differ from Learned's own defaults, corrected per the
		// lab's 2026-08-28 flip-check (see the Learned3Flip* defaults in SetDefaults()).
		[NinjaScriptProperty] [Display(Name = "Learned3 Flip — TRND", GroupName = "0e. Learned3 Flip Overrides", Order = 0)] public bool Learned3FlipTrnd { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned3 Flip — ADX",  GroupName = "0e. Learned3 Flip Overrides", Order = 1)] public bool Learned3FlipAdx  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned3 Flip — STRC", GroupName = "0e. Learned3 Flip Overrides", Order = 2)] public bool Learned3FlipStrc { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned3 Flip — STF",  GroupName = "0e. Learned3 Flip Overrides", Order = 3)] public bool Learned3FlipStf  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned3 Flip — PSAR", GroupName = "0e. Learned3 Flip Overrides", Order = 4)] public bool Learned3FlipPsar { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned3 Flip — VDYA", GroupName = "0e. Learned3 Flip Overrides", Order = 5)] public bool Learned3FlipVdya { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned3 Flip — WAE",  GroupName = "0e. Learned3 Flip Overrides", Order = 6)] public bool Learned3FlipWae  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned3 Flip — ZSC",  GroupName = "0e. Learned3 Flip Overrides", Order = 7)] public bool Learned3FlipZsc  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned3 Flip — TFUS", GroupName = "0e. Learned3 Flip Overrides", Order = 8)] public bool Learned3FlipTfus { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned3 Flip — ARCH", GroupName = "0e. Learned3 Flip Overrides", Order = 9)] public bool Learned3FlipArch { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned3 Flip — VENV", GroupName = "0e. Learned3 Flip Overrides", Order = 10)] public bool Learned3FlipVenv { get; set; }

		// ── Learned4 (2026-09-12, further de-redundant policy variant of Learned2) flip overrides ──
		// Learned4 is Learned2's own numbers with 3 more redundant-pair members zeroed (SPRT/VDYA/FLOW —
		// see the ApplyWeightProfile() branch and the enum doc comment) — it is NOT a separate training
		// run and changes NO sign convention, so these toggles default to the SAME choices as Learned2's
		// above. Kept as a fully separate set anyway (rather than reusing Learned2FlipXxx) so a future
		// edit to one profile's flip choice can never silently change the other's.
		[NinjaScriptProperty] [Display(Name = "Learned4 Flip — TRND", GroupName = "0f. Learned4 Flip Overrides", Order = 0)] public bool Learned4FlipTrnd { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned4 Flip — ADX",  GroupName = "0f. Learned4 Flip Overrides", Order = 1)] public bool Learned4FlipAdx  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned4 Flip — STRC", GroupName = "0f. Learned4 Flip Overrides", Order = 2)] public bool Learned4FlipStrc { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned4 Flip — STF",  GroupName = "0f. Learned4 Flip Overrides", Order = 3)] public bool Learned4FlipStf  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned4 Flip — PSAR", GroupName = "0f. Learned4 Flip Overrides", Order = 4)] public bool Learned4FlipPsar { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned4 Flip — VDYA", GroupName = "0f. Learned4 Flip Overrides", Order = 5)] public bool Learned4FlipVdya { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned4 Flip — WAE",  GroupName = "0f. Learned4 Flip Overrides", Order = 6)] public bool Learned4FlipWae  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned4 Flip — ZSC",  GroupName = "0f. Learned4 Flip Overrides", Order = 7)] public bool Learned4FlipZsc  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned4 Flip — TFUS", GroupName = "0f. Learned4 Flip Overrides", Order = 8)] public bool Learned4FlipTfus { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned4 Flip — ARCH", GroupName = "0f. Learned4 Flip Overrides", Order = 9)] public bool Learned4FlipArch { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned4 Flip — VENV", GroupName = "0f. Learned4 Flip Overrides", Order = 10)] public bool Learned4FlipVenv { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned4 Flip — SPRT", GroupName = "0f. Learned4 Flip Overrides", Order = 11)] public bool Learned4FlipSprt { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned4 Flip — CCI",  GroupName = "0f. Learned4 Flip Overrides", Order = 12)] public bool Learned4FlipCci  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned4 Flip — COMP", GroupName = "0f. Learned4 Flip Overrides", Order = 13)] public bool Learned4FlipComp { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned4 Flip — GREV", GroupName = "0f. Learned4 Flip Overrides", Order = 14)] public bool Learned4FlipGrev { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned4 Flip — EXH",  GroupName = "0f. Learned4 Flip Overrides", Order = 15)] public bool Learned4FlipExh  { get; set; }
		[NinjaScriptProperty] [Display(Name = "Learned4 Flip — PRS",  GroupName = "0f. Learned4 Flip Overrides", Order = 16)] public bool Learned4FlipPrs  { get; set; }

		// ── unpublished weights — guesses, deliberately low ──
		[NinjaScriptProperty]
		[Display(Name = "Include Eye As Voter (experimental)",
			Description = "OFF by default and recommended to stay off for normal use. SneakyEye's own verdict is built FROM Pressure, Intermarket, and Drift Conviction — the same three sources already voting above as PRS/IMKT/CVB. Turning this on double-counts that evidence rather than adding a genuinely independent 5th voice. Offered as a togglable EXPERIMENT — does Eye's bundled/thresholded combination change the panel's read at all vs. the three raw votes alone — not a recommended default.",
			GroupName = "4. Weights (unvalidated)", Order = 0)]
		public bool IncludeEye { get; set; }

		[NinjaScriptProperty] [Range(0.0, 50.0)]
		[Display(Name = "Weight — Eye (experimental, half-weighted)",
			Description = "Only applies when 'Include Eye As Voter' is on. Defaults to HALF of PRS/IMKT/CVB/FLOW's 0.8 — reflects that this is a recombination of votes already cast, not an independent 5th one. TRIGGER-kind: a not-qualified Eye abstains from the denominator rather than voting a flat 0.",
			GroupName = "4. Weights (unvalidated)", Order = 1)]
		public double WeightEye { get; set; }

		[NinjaScriptProperty] [Range(0.0, 50.0)]
		[Display(Name = "Weight — Flow / CVD",
			Description = "SneakyFlow's CVD-regression Signal, which is already gated by R-squared and strength — an incoherent lean abstains rather than voting noise. Tape-derived, so weighted with PRS/CVB/IMKT rather than the trend bloc. Damped when price/CVD diverge.",
			GroupName = "4. Weights (unvalidated)", Order = 2)]
		public double WeightFlow { get; set; }

		[NinjaScriptProperty] [Range(0.0, 50.0)]
		[Display(Name = "Weight — Intermarket / IMKT",
			Description = "Correlated-market directional lean. Weighted with PRS and CVB rather than with the trend voters, because like them it is not derived from this instrument's own price. Scaled by |Score| and damped below 3 warm references.",
			GroupName = "4. Weights (unvalidated)", Order = 3)]
		public double WeightIntermarket { get; set; }

		[NinjaScriptProperty] [Range(0.0, 50.0)]
		[Display(Name = "Weight — Order-Flow Pressure / PRS",
			Description = "BuySellVolumePressureMountain's dominant side. Weighted above the trend voters on purpose: it is classified from TICKS, so with CVB it is one of only two non-price-derived readings on the panel. Halved automatically when the bar fell back to OHLC classification.",
			GroupName = "4. Weights (unvalidated)", Order = 4)]
		public double WeightPressure { get; set; }

		[NinjaScriptProperty] [Range(0.0, 50.0)]
		[Display(Name = "Weight — Flux (order-flow imbalance bars)",
			Description = "2026-08-06 — new. Net signed imbalance of the just-closed SneakyFlux bar (FlowDir). Only exists on a chart running the SneakyFlux bar type — abstains everywhere else. Enters at 0.0: ungraded on this corpus, same audition discipline this suite already used once for Pressure. Damped when Divergence shows flow opposing the bar's own price direction (absorption).",
			GroupName = "4. Weights (unvalidated)", Order = 16)]
		public double WeightFlux { get; set; }

		[NinjaScriptProperty] [Range(0.0, 50.0)]
		[Display(Name = "Weight — Trend Fusion",
			Description = "2026-08-06 — new. SneakyTrendFusion's majority-vote composite (MFI/CCI/CVD/Hurst regime/KAMA fan) — NOT a PRISM port, an original design (see SneakyTrendFusion's own header). Scaled by how many of its own five sub-reads agreed. Enters at 0.0: brand new, ungraded on this corpus.",
			GroupName = "4. Weights (unvalidated)", Order = 17)]
		public double WeightTrendFusion { get; set; }

		[NinjaScriptProperty] [Range(0.0, 50.0)]
		[Display(Name = "Weight — Trend Architect (PRISM)",
			Description = "2026-08-07 — new. The REAL PRISM/Trend Architect (ported from the real Sentinel Suite's own SentinelTrendArchitect — see that file's own header for full attribution/license). Straight vote, no extra scaling. Enters at 0.0: brand new to this Council's own grading history.",
			GroupName = "4. Weights (unvalidated)", Order = 18)]
		public double WeightArch { get; set; }

		[NinjaScriptProperty] [Range(0.0, 50.0)]
		[Display(Name = "Weight — CVD (session cumulative delta)",
			Description = "2026-08-16 — new. SneakyCVD's session-scale cumulative volume delta direction (Core CvdState). Distinct from FLOW (a tick-rule regression regime read) and FLUX (one forming bar, resets every close) — this is the whole session's accumulated delta. Tape-confirmed like PRS/CVB/FLUX, not price-derived. TickBacked gates the vote; damped on Divergence. Enters at 0.0: brand new, ungraded — closes the last voter-roster gap against the real Sentinel Suite's own Council v1.11.0 (see file header CHANGELOG).",
			GroupName = "4. Weights (unvalidated)", Order = 19)]
		public double WeightCvd { get; set; }

		[NinjaScriptProperty] [Range(0.0, 50.0)]
		[Display(Name = "Weight — Drift Conviction / CVB",
			Description = "Drift's flow-confirmed conviction bias. Weighted ABOVE the other unvalidated voters on purpose: it is the only voter on this panel confirmed by tape delta rather than derived from price, so it adds information the trend bloc cannot.",
			GroupName = "4. Weights (unvalidated)", Order = 5)]
		public double WeightConviction { get; set; }

		[NinjaScriptProperty] [Range(0.0, 50.0)] [Display(Name = "Weight — Structure",    GroupName = "4. Weights (unvalidated)", Order = 6)] public double WeightStructure { get; set; }
		[NinjaScriptProperty] [Range(0.0, 50.0)] [Display(Name = "Weight — SuperTrend",   GroupName = "4. Weights (unvalidated)", Order = 7)] public double WeightSuperTrend { get; set; }
		[NinjaScriptProperty] [Range(0.0, 50.0)] [Display(Name = "Weight — StochTripleFilter", GroupName = "4. Weights (unvalidated)", Order = 8)] public double WeightStf { get; set; }
		[NinjaScriptProperty] [Range(0.0, 50.0)] [Display(Name = "Weight — Parabolic SAR", GroupName = "4. Weights (unvalidated)", Order = 9)] public double WeightSar { get; set; }
		[NinjaScriptProperty] [Range(0.0, 50.0)] [Display(Name = "Weight — ADXVMA",        GroupName = "4. Weights (unvalidated)", Order = 10)] public double WeightAdxvma { get; set; }
		[NinjaScriptProperty] [Range(0.0, 50.0)] [Display(Name = "Weight — VIDYA",         GroupName = "4. Weights (unvalidated)", Order = 11)] public double WeightVidya { get; set; }
		[NinjaScriptProperty] [Range(0.0, 50.0)] [Display(Name = "Weight — GodReversal",   GroupName = "4. Weights (unvalidated)", Order = 12)] public double WeightGodReversal { get; set; }
		[NinjaScriptProperty] [Range(0.0, 50.0)] [Display(Name = "Weight — Exhaustion",    GroupName = "4. Weights (unvalidated)", Order = 13)] public double WeightExhaustion { get; set; }
		[NinjaScriptProperty] [Range(0.0, 50.0)] [Display(Name = "Weight — Harmonic",      GroupName = "4. Weights (unvalidated)", Order = 14)] public double WeightHarmonic { get; set; }
		[NinjaScriptProperty] [Range(0.0, 50.0)] [Display(Name = "Weight — ZScore",        GroupName = "4. Weights (unvalidated)", Order = 15)] public double WeightZScore { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Draw Signal Tags (for automation)",
			Description = "Draws a named Draw.Text tag — literal \"SSCB\" (buy) / \"SSCS\" (sell), lime/red — on each firing bar. Was a plain Draw.Dot, then briefly arrows, until 2026-08-19; settled on text since other indicators on the chart already plot triangles/arrows. Same text and color as SneakyConfluence's own tags — the two sources read as one signal, not distinct ones. Bar-tag automation tools key off the tag NAME; plot-value tools key off BuySignal/SellSignal instead.",
			GroupName = "4. Signals", Order = 0)]
		public bool DrawSignalTags { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Signal Tag Prefix",
			Description = "Tags are drawn as <Prefix>_Buy_<bar> / <Prefix>_Sell_<bar>. Keep it distinct from SneakyConfluence's prefix so the two tools' tags never collide on one chart.",
			GroupName = "4. Signals", Order = 1)]
		public string SignalTagPrefix { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Instance Tag (Lane)",
			Description = "Optional label, e.g. 'A' / 'B'. As of 2026-08-04 this REGISTERS A REAL LANE for this chart — every seam on THIS chart (this Council, and every sensor it and anything else on this chart reads from) resolves to a separate, lane-qualified scope automatically, so two charts sharing the same instrument+bar-type genuinely stop colliding, not just in what Recorder tags. Leave blank if you only run one Council per scope — unchanged behavior either way.",
			GroupName = "4. Signals", Order = 2)]
		public string InstanceTag { get; set; }

		[NinjaScriptProperty] [Range(0, 200)]
		[Display(Name = "Signal Marker Offset (ticks)",
			Description = "Distance below the bar low (buy) or above the bar high (sell) at which the marker is drawn.",
			GroupName = "4. Signals", Order = 2)]
		public int SignalMarkerOffsetTicks { get; set; }

		[NinjaScriptProperty] [Range(-1.0, 1.0)]
		[Display(Name = "Buy Signal Value (+1)",
			Description = "Value written to the BuySignal plot on a firing bar. +1.0 matches both the Sneaky suite and PredatorX's LongEntryPlotValue=1.",
			GroupName = "4. Signals", Order = 3)]
		public double BuySignalValue { get; set; }

		[NinjaScriptProperty] [Range(-1.0, 1.0)]
		[Display(Name = "Sell Signal Value (+1 suite / -1 PredatorX)",
			Description = "Value written to the SellSignal plot on a firing bar. The Sneaky suite's own convention (SneakyConfluence, SneakyWAE) is +1.0 on BOTH sides. Set -1.0 to match PredatorX's ShortEntryPlotValue=-1, the convention the Nebula/TMO plots use.",
			GroupName = "4. Signals", Order = 4)]
		public double SellSignalValue { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Setup Help (recommended ON)",
			Description = "Prints a plain-English guide to the Output window on load: how the weighted vote works, why it behaves differently from SneakyConfluence, which bar type feeds which voter, and why agreement among price-derived voters overstates conviction.",
			GroupName = "5. Display", Order = 3)]
		public bool ShowSetupHelp { get; set; }

		[NinjaScriptProperty] [Display(Name = "Show Card", GroupName = "5. Display", Order = 0)]
		public bool ShowCard { get; set; }

		[NinjaScriptProperty] [Display(Name = "Card Corner", GroupName = "5. Display", Order = 1)]
		public SneakyCardCorner CardCorner { get; set; }

		[NinjaScriptProperty] [Display(Name = "Show Indicator Label", GroupName = "5. Display", Order = 2)]
		public bool ShowIndicatorLabel { get; set; }

		#endregion
	}

	// 2026-09-06 -- see the comment above the [TypeConverter] attribute on SneakyCouncil_v1_0_0
	// for why this exists and why this specific NinjaTrader-documented pattern was chosen.
	// Filters the Properties grid down to just the Flip Override group matching the currently
	// selected WeightProfile; does not touch the underlying properties, ApplyWeightProfile(), or
	// any AddVote wiring -- purely a display filter.
	public class WeightProfileConverter : NinjaTrader.NinjaScript.IndicatorBaseConverter
	{
		public override PropertyDescriptorCollection GetProperties(ITypeDescriptorContext context, object component, Attribute[] attributes)
		{
			SneakyCouncil_v1_0_0 council = component as SneakyCouncil_v1_0_0;
			PropertyDescriptorCollection properties = GetPropertiesSupported(context)
				? base.GetProperties(context, component, attributes)
				: TypeDescriptor.GetProperties(component, attributes);
			if (council == null) return properties;

			List<PropertyDescriptor> keep = new List<PropertyDescriptor>();
			foreach (PropertyDescriptor pd in properties)
			{
				bool isLearnedGroup  = pd.Name.StartsWith("LearnedFlip");
				bool isLearned1Group = pd.Name.StartsWith("Learned1Flip");
				bool isLearned2Group = pd.Name.StartsWith("Learned2Flip");
				bool isLearned3Group = pd.Name.StartsWith("Learned3Flip");
				bool isLearned4Group = pd.Name.StartsWith("Learned4Flip");

				if (isLearnedGroup  && council.WeightProfile != SneakyWeightProfile.Learned)  continue;
				if (isLearned1Group && council.WeightProfile != SneakyWeightProfile.Learned1) continue;
				if (isLearned2Group && council.WeightProfile != SneakyWeightProfile.Learned2) continue;
				if (isLearned3Group && council.WeightProfile != SneakyWeightProfile.Learned3) continue;
				if (isLearned4Group && council.WeightProfile != SneakyWeightProfile.Learned4) continue;

				keep.Add(pd);
			}
			return new PropertyDescriptorCollection(keep.ToArray());
		}

		public override bool GetPropertiesSupported(ITypeDescriptorContext context)
		{
			return true;
		}
	}
}

// 2026-08-16 — MOVED out of the Indicators.Sneaky.Sensors namespace above and into AddOns.Sneaky
// (a SEPARATE namespace block in this same file, which C# allows). Root cause of the CS0246 "type
// or namespace name 'SneakyWeightProfile' could not be found" compile errors: NinjaTrader's own
// auto-generated wrapper code (the "#region NinjaScript generated code" block it appends and
// regenerates on every compile — confirmed by watching the exact same error survive deleting that
// block and letting NinjaTrader rebuild it) lives in NinjaTrader.NinjaScript.Indicators /
// .Strategies / .MarketAnalyzerColumns, and references every [NinjaScriptProperty] type BARE, with
// no qualification. SneakyCardCorner (this file's OTHER custom enum property type) resolved fine
// from those namespaces because it already lives in AddOns.Sneaky (see SneakySkin.cs) — a namespace
// NinjaTrader's generator evidently already knows how to reach. SneakyWeightProfile did not, because
// it was declared inside Indicators.Sneaky.Sensors — THIS file's own namespace, not a shared one.
// Moving it here (same namespace as SneakyCardCorner/SneakyCore/CouncilFusion) fixes the generated
// code without touching NinjaTrader's own machinery, and needs no other change in this file: the
// class above already carries "using NinjaTrader.NinjaScript.AddOns.Sneaky;", so every existing
// WeightProfile/SneakyWeightProfile reference inside SneakyCouncil_v1_0_0 keeps resolving exactly
// as before.
namespace NinjaTrader.NinjaScript.AddOns.Sneaky
{
	/// <summary>Prior = original manual/reference-build Council weights. Learned = train_council_weights.py's
	/// 2026-08-11 fitted weights (after-lane-fix corpus). Learned1 = 2026-08-29 instance-F-only retrain
	/// (train_council_weights.py --instance F, clean single-instance corpus, fold 4/10,784 rows) — kept as
	/// its OWN profile rather than overwriting Prior's numbers, specifically so Prior stays available for
	/// side-by-side comparison. Learned2 = 2026-08-29 "de-redundant" POLICY variant, not a new training
	/// run: starts from Learned1's fitted numbers and mechanically (a) zeroes the 7 voters the pairwise-
	/// agreement analysis flagged as one overcounted trend opinion (TRND/PSAR/TFUS/ARCH/WAE/ZSC/STRC), and
	/// (b) redistributes their combined removed weight onto the 5 voters that analysis flagged as
	/// consistently strong and under-weighted (IMKT/CCI/CVD/AVMA/HARM), proportional to each one's existing
	/// Learned1 weight. Every other voter's weight and flip choice is copied from Learned1 unchanged. See
	/// ApplyWeightProfile()'s Learned2 branch for the exact numbers and the redistribution arithmetic.
	/// Learned3 = 2026-09-05, same magnitudes as Learned (not Learned1/2's per-instance fit) but with the
	/// 2026-08-28 flip-check's corrected signs for the 8 voters it found reading contrarian under Learned's
	/// own (all-true, undiagnosed) flip defaults — an untested alternative, not a proven correction.
	/// Learned4 = 2026-09-12 — builds on Learned2 (not a new training run): the 2026-08-28 pairwise-
	/// agreement analysis that produced Learned2 caught ONE 7-voter redundant cluster, but a separate,
	/// earlier pairwise-correlation pass this same engagement found 3 MORE 2-voter redundant pairs that
	/// survived into Learned2 with BOTH members still actively (double-)counted: CCI/SPRT (94-95% agree),
	/// VDYA/VENV (94.7%), CVD/FLOW (91.7%). Learned4 starts from Learned2's numbers unchanged and applies
	/// the exact same rule Learned2 already used on its own cluster: zero the redundant pair's weaker/
	/// non-"strong-cluster" member (SPRT, VDYA, FLOW) and hand its full weight to the survivor already
	/// flagged strong by Learned2's analysis (CCI, VENV, CVD respectively) — nothing lost, no new fit, no
	/// new sign claims. Every flip choice and every other voter's weight is copied from Learned2 unchanged.
	/// ConvictionFloor ships at the same uncalibrated 0.2 placeholder Learned1/Learned2 used — re-run
	/// check_conviction.py against Learned4's own recorded (non-vetoed) bars once it has history, same as
	/// Learned's 0.10 was derived on 2026-08-12. See ApplyWeightProfile()'s Learned4 branch for the numbers.
	/// Add Learned5, etc. the same way for future retrains or policy variants: one more enum value, one
	/// more ApplyWeightProfile() branch, and (only for voters whose fitted sign needs it) one more set of
	/// per-voter LearnedNFlipXxx toggles. See WeightProfile property + ApplyWeightProfile().</summary>
	public enum SneakyWeightProfile { Prior, Learned, Learned1, Learned2, Learned3, Learned4 }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Sneaky.Sensors.SneakyCouncil_v1_0_0[] cacheSneakyCouncil_v1_0_0;
		public Sneaky.Sensors.SneakyCouncil_v1_0_0 SneakyCouncil_v1_0_0(double staleSec, double deadband, double convictionFloor, double activeWeightCap, int minVoters, bool vetoLiquidityWall, int wallVetoTicks, bool vetoNearLevel, double levelVetoAtr, bool vetoMtfOpposed, bool vetoChop, bool vetoHighVolRegime, bool requireSensorOnChart, bool vetoNormalRegime, bool vetoKillWindow, bool vetoNews, double squeezeDamp, double dryUpDamp, double profileValueAreaDamp, double profileOverextendedDamp, double activeWeightCapDamp, double weightTrend, double weightCci, double weightAdx, double weightEnvelope, double weightCompression, double weightWae, double weightBrick, SneakyWeightProfile weightProfile, bool learnedFlipTrnd, bool learnedFlipAdx, bool learnedFlipStrc, bool learnedFlipStf, bool learnedFlipPsar, bool learnedFlipVdya, bool learnedFlipWae, bool learnedFlipZsc, bool learnedFlipTfus, bool learnedFlipArch, bool learnedFlipVenv, bool learned1FlipTrnd, bool learned1FlipAdx, bool learned1FlipStrc, bool learned1FlipStf, bool learned1FlipPsar, bool learned1FlipVdya, bool learned1FlipWae, bool learned1FlipZsc, bool learned1FlipTfus, bool learned1FlipArch, bool learned1FlipVenv, bool learned1FlipSprt, bool learned1FlipCci, bool learned1FlipComp, bool learned1FlipGrev, bool learned1FlipExh, bool learned1FlipPrs, bool learned2FlipTrnd, bool learned2FlipAdx, bool learned2FlipStrc, bool learned2FlipStf, bool learned2FlipPsar, bool learned2FlipVdya, bool learned2FlipWae, bool learned2FlipZsc, bool learned2FlipTfus, bool learned2FlipArch, bool learned2FlipVenv, bool learned2FlipSprt, bool learned2FlipCci, bool learned2FlipComp, bool learned2FlipGrev, bool learned2FlipExh, bool learned2FlipPrs, bool learned3FlipTrnd, bool learned3FlipAdx, bool learned3FlipStrc, bool learned3FlipStf, bool learned3FlipPsar, bool learned3FlipVdya, bool learned3FlipWae, bool learned3FlipZsc, bool learned3FlipTfus, bool learned3FlipArch, bool learned3FlipVenv, bool learned4FlipTrnd, bool learned4FlipAdx, bool learned4FlipStrc, bool learned4FlipStf, bool learned4FlipPsar, bool learned4FlipVdya, bool learned4FlipWae, bool learned4FlipZsc, bool learned4FlipTfus, bool learned4FlipArch, bool learned4FlipVenv, bool learned4FlipSprt, bool learned4FlipCci, bool learned4FlipComp, bool learned4FlipGrev, bool learned4FlipExh, bool learned4FlipPrs, bool includeEye, double weightEye, double weightFlow, double weightIntermarket, double weightPressure, double weightFlux, double weightTrendFusion, double weightArch, double weightCvd, double weightConviction, double weightStructure, double weightSuperTrend, double weightStf, double weightSar, double weightAdxvma, double weightVidya, double weightGodReversal, double weightExhaustion, double weightHarmonic, double weightZScore, bool drawSignalTags, string signalTagPrefix, string instanceTag, int signalMarkerOffsetTicks, double buySignalValue, double sellSignalValue, bool showSetupHelp, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyCouncil_v1_0_0(Input, staleSec, deadband, convictionFloor, activeWeightCap, minVoters, vetoLiquidityWall, wallVetoTicks, vetoNearLevel, levelVetoAtr, vetoMtfOpposed, vetoChop, vetoHighVolRegime, requireSensorOnChart, vetoNormalRegime, vetoKillWindow, vetoNews, squeezeDamp, dryUpDamp, profileValueAreaDamp, profileOverextendedDamp, activeWeightCapDamp, weightTrend, weightCci, weightAdx, weightEnvelope, weightCompression, weightWae, weightBrick, weightProfile, learnedFlipTrnd, learnedFlipAdx, learnedFlipStrc, learnedFlipStf, learnedFlipPsar, learnedFlipVdya, learnedFlipWae, learnedFlipZsc, learnedFlipTfus, learnedFlipArch, learnedFlipVenv, learned1FlipTrnd, learned1FlipAdx, learned1FlipStrc, learned1FlipStf, learned1FlipPsar, learned1FlipVdya, learned1FlipWae, learned1FlipZsc, learned1FlipTfus, learned1FlipArch, learned1FlipVenv, learned1FlipSprt, learned1FlipCci, learned1FlipComp, learned1FlipGrev, learned1FlipExh, learned1FlipPrs, learned2FlipTrnd, learned2FlipAdx, learned2FlipStrc, learned2FlipStf, learned2FlipPsar, learned2FlipVdya, learned2FlipWae, learned2FlipZsc, learned2FlipTfus, learned2FlipArch, learned2FlipVenv, learned2FlipSprt, learned2FlipCci, learned2FlipComp, learned2FlipGrev, learned2FlipExh, learned2FlipPrs, learned3FlipTrnd, learned3FlipAdx, learned3FlipStrc, learned3FlipStf, learned3FlipPsar, learned3FlipVdya, learned3FlipWae, learned3FlipZsc, learned3FlipTfus, learned3FlipArch, learned3FlipVenv, learned4FlipTrnd, learned4FlipAdx, learned4FlipStrc, learned4FlipStf, learned4FlipPsar, learned4FlipVdya, learned4FlipWae, learned4FlipZsc, learned4FlipTfus, learned4FlipArch, learned4FlipVenv, learned4FlipSprt, learned4FlipCci, learned4FlipComp, learned4FlipGrev, learned4FlipExh, learned4FlipPrs, includeEye, weightEye, weightFlow, weightIntermarket, weightPressure, weightFlux, weightTrendFusion, weightArch, weightCvd, weightConviction, weightStructure, weightSuperTrend, weightStf, weightSar, weightAdxvma, weightVidya, weightGodReversal, weightExhaustion, weightHarmonic, weightZScore, drawSignalTags, signalTagPrefix, instanceTag, signalMarkerOffsetTicks, buySignalValue, sellSignalValue, showSetupHelp, showCard, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Sensors.SneakyCouncil_v1_0_0 SneakyCouncil_v1_0_0(ISeries<double> input, double staleSec, double deadband, double convictionFloor, double activeWeightCap, int minVoters, bool vetoLiquidityWall, int wallVetoTicks, bool vetoNearLevel, double levelVetoAtr, bool vetoMtfOpposed, bool vetoChop, bool vetoHighVolRegime, bool requireSensorOnChart, bool vetoNormalRegime, bool vetoKillWindow, bool vetoNews, double squeezeDamp, double dryUpDamp, double profileValueAreaDamp, double profileOverextendedDamp, double activeWeightCapDamp, double weightTrend, double weightCci, double weightAdx, double weightEnvelope, double weightCompression, double weightWae, double weightBrick, SneakyWeightProfile weightProfile, bool learnedFlipTrnd, bool learnedFlipAdx, bool learnedFlipStrc, bool learnedFlipStf, bool learnedFlipPsar, bool learnedFlipVdya, bool learnedFlipWae, bool learnedFlipZsc, bool learnedFlipTfus, bool learnedFlipArch, bool learnedFlipVenv, bool learned1FlipTrnd, bool learned1FlipAdx, bool learned1FlipStrc, bool learned1FlipStf, bool learned1FlipPsar, bool learned1FlipVdya, bool learned1FlipWae, bool learned1FlipZsc, bool learned1FlipTfus, bool learned1FlipArch, bool learned1FlipVenv, bool learned1FlipSprt, bool learned1FlipCci, bool learned1FlipComp, bool learned1FlipGrev, bool learned1FlipExh, bool learned1FlipPrs, bool learned2FlipTrnd, bool learned2FlipAdx, bool learned2FlipStrc, bool learned2FlipStf, bool learned2FlipPsar, bool learned2FlipVdya, bool learned2FlipWae, bool learned2FlipZsc, bool learned2FlipTfus, bool learned2FlipArch, bool learned2FlipVenv, bool learned2FlipSprt, bool learned2FlipCci, bool learned2FlipComp, bool learned2FlipGrev, bool learned2FlipExh, bool learned2FlipPrs, bool learned3FlipTrnd, bool learned3FlipAdx, bool learned3FlipStrc, bool learned3FlipStf, bool learned3FlipPsar, bool learned3FlipVdya, bool learned3FlipWae, bool learned3FlipZsc, bool learned3FlipTfus, bool learned3FlipArch, bool learned3FlipVenv, bool learned4FlipTrnd, bool learned4FlipAdx, bool learned4FlipStrc, bool learned4FlipStf, bool learned4FlipPsar, bool learned4FlipVdya, bool learned4FlipWae, bool learned4FlipZsc, bool learned4FlipTfus, bool learned4FlipArch, bool learned4FlipVenv, bool learned4FlipSprt, bool learned4FlipCci, bool learned4FlipComp, bool learned4FlipGrev, bool learned4FlipExh, bool learned4FlipPrs, bool includeEye, double weightEye, double weightFlow, double weightIntermarket, double weightPressure, double weightFlux, double weightTrendFusion, double weightArch, double weightCvd, double weightConviction, double weightStructure, double weightSuperTrend, double weightStf, double weightSar, double weightAdxvma, double weightVidya, double weightGodReversal, double weightExhaustion, double weightHarmonic, double weightZScore, bool drawSignalTags, string signalTagPrefix, string instanceTag, int signalMarkerOffsetTicks, double buySignalValue, double sellSignalValue, bool showSetupHelp, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyCouncil_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyCouncil_v1_0_0.Length; idx++)
					if (cacheSneakyCouncil_v1_0_0[idx] != null && cacheSneakyCouncil_v1_0_0[idx].StaleSec == staleSec && cacheSneakyCouncil_v1_0_0[idx].Deadband == deadband && cacheSneakyCouncil_v1_0_0[idx].ConvictionFloor == convictionFloor && cacheSneakyCouncil_v1_0_0[idx].ActiveWeightCap == activeWeightCap && cacheSneakyCouncil_v1_0_0[idx].MinVoters == minVoters && cacheSneakyCouncil_v1_0_0[idx].VetoLiquidityWall == vetoLiquidityWall && cacheSneakyCouncil_v1_0_0[idx].WallVetoTicks == wallVetoTicks && cacheSneakyCouncil_v1_0_0[idx].VetoNearLevel == vetoNearLevel && cacheSneakyCouncil_v1_0_0[idx].LevelVetoAtr == levelVetoAtr && cacheSneakyCouncil_v1_0_0[idx].VetoMtfOpposed == vetoMtfOpposed && cacheSneakyCouncil_v1_0_0[idx].VetoChop == vetoChop && cacheSneakyCouncil_v1_0_0[idx].VetoHighVolRegime == vetoHighVolRegime && cacheSneakyCouncil_v1_0_0[idx].RequireSensorOnChart == requireSensorOnChart && cacheSneakyCouncil_v1_0_0[idx].VetoNormalRegime == vetoNormalRegime && cacheSneakyCouncil_v1_0_0[idx].VetoKillWindow == vetoKillWindow && cacheSneakyCouncil_v1_0_0[idx].VetoNews == vetoNews && cacheSneakyCouncil_v1_0_0[idx].SqueezeDamp == squeezeDamp && cacheSneakyCouncil_v1_0_0[idx].DryUpDamp == dryUpDamp && cacheSneakyCouncil_v1_0_0[idx].ProfileValueAreaDamp == profileValueAreaDamp && cacheSneakyCouncil_v1_0_0[idx].ProfileOverextendedDamp == profileOverextendedDamp && cacheSneakyCouncil_v1_0_0[idx].ActiveWeightCapDamp == activeWeightCapDamp && cacheSneakyCouncil_v1_0_0[idx].WeightTrend == weightTrend && cacheSneakyCouncil_v1_0_0[idx].WeightCci == weightCci && cacheSneakyCouncil_v1_0_0[idx].WeightAdx == weightAdx && cacheSneakyCouncil_v1_0_0[idx].WeightEnvelope == weightEnvelope && cacheSneakyCouncil_v1_0_0[idx].WeightCompression == weightCompression && cacheSneakyCouncil_v1_0_0[idx].WeightWae == weightWae && cacheSneakyCouncil_v1_0_0[idx].WeightBrick == weightBrick && cacheSneakyCouncil_v1_0_0[idx].WeightProfile == weightProfile && cacheSneakyCouncil_v1_0_0[idx].LearnedFlipTrnd == learnedFlipTrnd && cacheSneakyCouncil_v1_0_0[idx].LearnedFlipAdx == learnedFlipAdx && cacheSneakyCouncil_v1_0_0[idx].LearnedFlipStrc == learnedFlipStrc && cacheSneakyCouncil_v1_0_0[idx].LearnedFlipStf == learnedFlipStf && cacheSneakyCouncil_v1_0_0[idx].LearnedFlipPsar == learnedFlipPsar && cacheSneakyCouncil_v1_0_0[idx].LearnedFlipVdya == learnedFlipVdya && cacheSneakyCouncil_v1_0_0[idx].LearnedFlipWae == learnedFlipWae && cacheSneakyCouncil_v1_0_0[idx].LearnedFlipZsc == learnedFlipZsc && cacheSneakyCouncil_v1_0_0[idx].LearnedFlipTfus == learnedFlipTfus && cacheSneakyCouncil_v1_0_0[idx].LearnedFlipArch == learnedFlipArch && cacheSneakyCouncil_v1_0_0[idx].LearnedFlipVenv == learnedFlipVenv && cacheSneakyCouncil_v1_0_0[idx].Learned1FlipTrnd == learned1FlipTrnd && cacheSneakyCouncil_v1_0_0[idx].Learned1FlipAdx == learned1FlipAdx && cacheSneakyCouncil_v1_0_0[idx].Learned1FlipStrc == learned1FlipStrc && cacheSneakyCouncil_v1_0_0[idx].Learned1FlipStf == learned1FlipStf && cacheSneakyCouncil_v1_0_0[idx].Learned1FlipPsar == learned1FlipPsar && cacheSneakyCouncil_v1_0_0[idx].Learned1FlipVdya == learned1FlipVdya && cacheSneakyCouncil_v1_0_0[idx].Learned1FlipWae == learned1FlipWae && cacheSneakyCouncil_v1_0_0[idx].Learned1FlipZsc == learned1FlipZsc && cacheSneakyCouncil_v1_0_0[idx].Learned1FlipTfus == learned1FlipTfus && cacheSneakyCouncil_v1_0_0[idx].Learned1FlipArch == learned1FlipArch && cacheSneakyCouncil_v1_0_0[idx].Learned1FlipVenv == learned1FlipVenv && cacheSneakyCouncil_v1_0_0[idx].Learned1FlipSprt == learned1FlipSprt && cacheSneakyCouncil_v1_0_0[idx].Learned1FlipCci == learned1FlipCci && cacheSneakyCouncil_v1_0_0[idx].Learned1FlipComp == learned1FlipComp && cacheSneakyCouncil_v1_0_0[idx].Learned1FlipGrev == learned1FlipGrev && cacheSneakyCouncil_v1_0_0[idx].Learned1FlipExh == learned1FlipExh && cacheSneakyCouncil_v1_0_0[idx].Learned1FlipPrs == learned1FlipPrs && cacheSneakyCouncil_v1_0_0[idx].Learned2FlipTrnd == learned2FlipTrnd && cacheSneakyCouncil_v1_0_0[idx].Learned2FlipAdx == learned2FlipAdx && cacheSneakyCouncil_v1_0_0[idx].Learned2FlipStrc == learned2FlipStrc && cacheSneakyCouncil_v1_0_0[idx].Learned2FlipStf == learned2FlipStf && cacheSneakyCouncil_v1_0_0[idx].Learned2FlipPsar == learned2FlipPsar && cacheSneakyCouncil_v1_0_0[idx].Learned2FlipVdya == learned2FlipVdya && cacheSneakyCouncil_v1_0_0[idx].Learned2FlipWae == learned2FlipWae && cacheSneakyCouncil_v1_0_0[idx].Learned2FlipZsc == learned2FlipZsc && cacheSneakyCouncil_v1_0_0[idx].Learned2FlipTfus == learned2FlipTfus && cacheSneakyCouncil_v1_0_0[idx].Learned2FlipArch == learned2FlipArch && cacheSneakyCouncil_v1_0_0[idx].Learned2FlipVenv == learned2FlipVenv && cacheSneakyCouncil_v1_0_0[idx].Learned2FlipSprt == learned2FlipSprt && cacheSneakyCouncil_v1_0_0[idx].Learned2FlipCci == learned2FlipCci && cacheSneakyCouncil_v1_0_0[idx].Learned2FlipComp == learned2FlipComp && cacheSneakyCouncil_v1_0_0[idx].Learned2FlipGrev == learned2FlipGrev && cacheSneakyCouncil_v1_0_0[idx].Learned2FlipExh == learned2FlipExh && cacheSneakyCouncil_v1_0_0[idx].Learned2FlipPrs == learned2FlipPrs && cacheSneakyCouncil_v1_0_0[idx].Learned3FlipTrnd == learned3FlipTrnd && cacheSneakyCouncil_v1_0_0[idx].Learned3FlipAdx == learned3FlipAdx && cacheSneakyCouncil_v1_0_0[idx].Learned3FlipStrc == learned3FlipStrc && cacheSneakyCouncil_v1_0_0[idx].Learned3FlipStf == learned3FlipStf && cacheSneakyCouncil_v1_0_0[idx].Learned3FlipPsar == learned3FlipPsar && cacheSneakyCouncil_v1_0_0[idx].Learned3FlipVdya == learned3FlipVdya && cacheSneakyCouncil_v1_0_0[idx].Learned3FlipWae == learned3FlipWae && cacheSneakyCouncil_v1_0_0[idx].Learned3FlipZsc == learned3FlipZsc && cacheSneakyCouncil_v1_0_0[idx].Learned3FlipTfus == learned3FlipTfus && cacheSneakyCouncil_v1_0_0[idx].Learned3FlipArch == learned3FlipArch && cacheSneakyCouncil_v1_0_0[idx].Learned3FlipVenv == learned3FlipVenv && cacheSneakyCouncil_v1_0_0[idx].Learned4FlipTrnd == learned4FlipTrnd && cacheSneakyCouncil_v1_0_0[idx].Learned4FlipAdx == learned4FlipAdx && cacheSneakyCouncil_v1_0_0[idx].Learned4FlipStrc == learned4FlipStrc && cacheSneakyCouncil_v1_0_0[idx].Learned4FlipStf == learned4FlipStf && cacheSneakyCouncil_v1_0_0[idx].Learned4FlipPsar == learned4FlipPsar && cacheSneakyCouncil_v1_0_0[idx].Learned4FlipVdya == learned4FlipVdya && cacheSneakyCouncil_v1_0_0[idx].Learned4FlipWae == learned4FlipWae && cacheSneakyCouncil_v1_0_0[idx].Learned4FlipZsc == learned4FlipZsc && cacheSneakyCouncil_v1_0_0[idx].Learned4FlipTfus == learned4FlipTfus && cacheSneakyCouncil_v1_0_0[idx].Learned4FlipArch == learned4FlipArch && cacheSneakyCouncil_v1_0_0[idx].Learned4FlipVenv == learned4FlipVenv && cacheSneakyCouncil_v1_0_0[idx].Learned4FlipSprt == learned4FlipSprt && cacheSneakyCouncil_v1_0_0[idx].Learned4FlipCci == learned4FlipCci && cacheSneakyCouncil_v1_0_0[idx].Learned4FlipComp == learned4FlipComp && cacheSneakyCouncil_v1_0_0[idx].Learned4FlipGrev == learned4FlipGrev && cacheSneakyCouncil_v1_0_0[idx].Learned4FlipExh == learned4FlipExh && cacheSneakyCouncil_v1_0_0[idx].Learned4FlipPrs == learned4FlipPrs && cacheSneakyCouncil_v1_0_0[idx].IncludeEye == includeEye && cacheSneakyCouncil_v1_0_0[idx].WeightEye == weightEye && cacheSneakyCouncil_v1_0_0[idx].WeightFlow == weightFlow && cacheSneakyCouncil_v1_0_0[idx].WeightIntermarket == weightIntermarket && cacheSneakyCouncil_v1_0_0[idx].WeightPressure == weightPressure && cacheSneakyCouncil_v1_0_0[idx].WeightFlux == weightFlux && cacheSneakyCouncil_v1_0_0[idx].WeightTrendFusion == weightTrendFusion && cacheSneakyCouncil_v1_0_0[idx].WeightArch == weightArch && cacheSneakyCouncil_v1_0_0[idx].WeightCvd == weightCvd && cacheSneakyCouncil_v1_0_0[idx].WeightConviction == weightConviction && cacheSneakyCouncil_v1_0_0[idx].WeightStructure == weightStructure && cacheSneakyCouncil_v1_0_0[idx].WeightSuperTrend == weightSuperTrend && cacheSneakyCouncil_v1_0_0[idx].WeightStf == weightStf && cacheSneakyCouncil_v1_0_0[idx].WeightSar == weightSar && cacheSneakyCouncil_v1_0_0[idx].WeightAdxvma == weightAdxvma && cacheSneakyCouncil_v1_0_0[idx].WeightVidya == weightVidya && cacheSneakyCouncil_v1_0_0[idx].WeightGodReversal == weightGodReversal && cacheSneakyCouncil_v1_0_0[idx].WeightExhaustion == weightExhaustion && cacheSneakyCouncil_v1_0_0[idx].WeightHarmonic == weightHarmonic && cacheSneakyCouncil_v1_0_0[idx].WeightZScore == weightZScore && cacheSneakyCouncil_v1_0_0[idx].DrawSignalTags == drawSignalTags && cacheSneakyCouncil_v1_0_0[idx].SignalTagPrefix == signalTagPrefix && cacheSneakyCouncil_v1_0_0[idx].InstanceTag == instanceTag && cacheSneakyCouncil_v1_0_0[idx].SignalMarkerOffsetTicks == signalMarkerOffsetTicks && cacheSneakyCouncil_v1_0_0[idx].BuySignalValue == buySignalValue && cacheSneakyCouncil_v1_0_0[idx].SellSignalValue == sellSignalValue && cacheSneakyCouncil_v1_0_0[idx].ShowSetupHelp == showSetupHelp && cacheSneakyCouncil_v1_0_0[idx].ShowCard == showCard && cacheSneakyCouncil_v1_0_0[idx].CardCorner == cardCorner && cacheSneakyCouncil_v1_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyCouncil_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyCouncil_v1_0_0[idx];
			return CacheIndicator<Sneaky.Sensors.SneakyCouncil_v1_0_0>(new Sneaky.Sensors.SneakyCouncil_v1_0_0(){ StaleSec = staleSec, Deadband = deadband, ConvictionFloor = convictionFloor, ActiveWeightCap = activeWeightCap, MinVoters = minVoters, VetoLiquidityWall = vetoLiquidityWall, WallVetoTicks = wallVetoTicks, VetoNearLevel = vetoNearLevel, LevelVetoAtr = levelVetoAtr, VetoMtfOpposed = vetoMtfOpposed, VetoChop = vetoChop, VetoHighVolRegime = vetoHighVolRegime, RequireSensorOnChart = requireSensorOnChart, VetoNormalRegime = vetoNormalRegime, VetoKillWindow = vetoKillWindow, VetoNews = vetoNews, SqueezeDamp = squeezeDamp, DryUpDamp = dryUpDamp, ProfileValueAreaDamp = profileValueAreaDamp, ProfileOverextendedDamp = profileOverextendedDamp, ActiveWeightCapDamp = activeWeightCapDamp, WeightTrend = weightTrend, WeightCci = weightCci, WeightAdx = weightAdx, WeightEnvelope = weightEnvelope, WeightCompression = weightCompression, WeightWae = weightWae, WeightBrick = weightBrick, WeightProfile = weightProfile, LearnedFlipTrnd = learnedFlipTrnd, LearnedFlipAdx = learnedFlipAdx, LearnedFlipStrc = learnedFlipStrc, LearnedFlipStf = learnedFlipStf, LearnedFlipPsar = learnedFlipPsar, LearnedFlipVdya = learnedFlipVdya, LearnedFlipWae = learnedFlipWae, LearnedFlipZsc = learnedFlipZsc, LearnedFlipTfus = learnedFlipTfus, LearnedFlipArch = learnedFlipArch, LearnedFlipVenv = learnedFlipVenv, Learned1FlipTrnd = learned1FlipTrnd, Learned1FlipAdx = learned1FlipAdx, Learned1FlipStrc = learned1FlipStrc, Learned1FlipStf = learned1FlipStf, Learned1FlipPsar = learned1FlipPsar, Learned1FlipVdya = learned1FlipVdya, Learned1FlipWae = learned1FlipWae, Learned1FlipZsc = learned1FlipZsc, Learned1FlipTfus = learned1FlipTfus, Learned1FlipArch = learned1FlipArch, Learned1FlipVenv = learned1FlipVenv, Learned1FlipSprt = learned1FlipSprt, Learned1FlipCci = learned1FlipCci, Learned1FlipComp = learned1FlipComp, Learned1FlipGrev = learned1FlipGrev, Learned1FlipExh = learned1FlipExh, Learned1FlipPrs = learned1FlipPrs, Learned2FlipTrnd = learned2FlipTrnd, Learned2FlipAdx = learned2FlipAdx, Learned2FlipStrc = learned2FlipStrc, Learned2FlipStf = learned2FlipStf, Learned2FlipPsar = learned2FlipPsar, Learned2FlipVdya = learned2FlipVdya, Learned2FlipWae = learned2FlipWae, Learned2FlipZsc = learned2FlipZsc, Learned2FlipTfus = learned2FlipTfus, Learned2FlipArch = learned2FlipArch, Learned2FlipVenv = learned2FlipVenv, Learned2FlipSprt = learned2FlipSprt, Learned2FlipCci = learned2FlipCci, Learned2FlipComp = learned2FlipComp, Learned2FlipGrev = learned2FlipGrev, Learned2FlipExh = learned2FlipExh, Learned2FlipPrs = learned2FlipPrs, Learned3FlipTrnd = learned3FlipTrnd, Learned3FlipAdx = learned3FlipAdx, Learned3FlipStrc = learned3FlipStrc, Learned3FlipStf = learned3FlipStf, Learned3FlipPsar = learned3FlipPsar, Learned3FlipVdya = learned3FlipVdya, Learned3FlipWae = learned3FlipWae, Learned3FlipZsc = learned3FlipZsc, Learned3FlipTfus = learned3FlipTfus, Learned3FlipArch = learned3FlipArch, Learned3FlipVenv = learned3FlipVenv, Learned4FlipTrnd = learned4FlipTrnd, Learned4FlipAdx = learned4FlipAdx, Learned4FlipStrc = learned4FlipStrc, Learned4FlipStf = learned4FlipStf, Learned4FlipPsar = learned4FlipPsar, Learned4FlipVdya = learned4FlipVdya, Learned4FlipWae = learned4FlipWae, Learned4FlipZsc = learned4FlipZsc, Learned4FlipTfus = learned4FlipTfus, Learned4FlipArch = learned4FlipArch, Learned4FlipVenv = learned4FlipVenv, Learned4FlipSprt = learned4FlipSprt, Learned4FlipCci = learned4FlipCci, Learned4FlipComp = learned4FlipComp, Learned4FlipGrev = learned4FlipGrev, Learned4FlipExh = learned4FlipExh, Learned4FlipPrs = learned4FlipPrs, IncludeEye = includeEye, WeightEye = weightEye, WeightFlow = weightFlow, WeightIntermarket = weightIntermarket, WeightPressure = weightPressure, WeightFlux = weightFlux, WeightTrendFusion = weightTrendFusion, WeightArch = weightArch, WeightCvd = weightCvd, WeightConviction = weightConviction, WeightStructure = weightStructure, WeightSuperTrend = weightSuperTrend, WeightStf = weightStf, WeightSar = weightSar, WeightAdxvma = weightAdxvma, WeightVidya = weightVidya, WeightGodReversal = weightGodReversal, WeightExhaustion = weightExhaustion, WeightHarmonic = weightHarmonic, WeightZScore = weightZScore, DrawSignalTags = drawSignalTags, SignalTagPrefix = signalTagPrefix, InstanceTag = instanceTag, SignalMarkerOffsetTicks = signalMarkerOffsetTicks, BuySignalValue = buySignalValue, SellSignalValue = sellSignalValue, ShowSetupHelp = showSetupHelp, ShowCard = showCard, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyCouncil_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Sensors.SneakyCouncil_v1_0_0 SneakyCouncil_v1_0_0(double staleSec, double deadband, double convictionFloor, double activeWeightCap, int minVoters, bool vetoLiquidityWall, int wallVetoTicks, bool vetoNearLevel, double levelVetoAtr, bool vetoMtfOpposed, bool vetoChop, bool vetoHighVolRegime, bool requireSensorOnChart, bool vetoNormalRegime, bool vetoKillWindow, bool vetoNews, double squeezeDamp, double dryUpDamp, double profileValueAreaDamp, double profileOverextendedDamp, double activeWeightCapDamp, double weightTrend, double weightCci, double weightAdx, double weightEnvelope, double weightCompression, double weightWae, double weightBrick, SneakyWeightProfile weightProfile, bool learnedFlipTrnd, bool learnedFlipAdx, bool learnedFlipStrc, bool learnedFlipStf, bool learnedFlipPsar, bool learnedFlipVdya, bool learnedFlipWae, bool learnedFlipZsc, bool learnedFlipTfus, bool learnedFlipArch, bool learnedFlipVenv, bool learned1FlipTrnd, bool learned1FlipAdx, bool learned1FlipStrc, bool learned1FlipStf, bool learned1FlipPsar, bool learned1FlipVdya, bool learned1FlipWae, bool learned1FlipZsc, bool learned1FlipTfus, bool learned1FlipArch, bool learned1FlipVenv, bool learned1FlipSprt, bool learned1FlipCci, bool learned1FlipComp, bool learned1FlipGrev, bool learned1FlipExh, bool learned1FlipPrs, bool learned2FlipTrnd, bool learned2FlipAdx, bool learned2FlipStrc, bool learned2FlipStf, bool learned2FlipPsar, bool learned2FlipVdya, bool learned2FlipWae, bool learned2FlipZsc, bool learned2FlipTfus, bool learned2FlipArch, bool learned2FlipVenv, bool learned2FlipSprt, bool learned2FlipCci, bool learned2FlipComp, bool learned2FlipGrev, bool learned2FlipExh, bool learned2FlipPrs, bool learned3FlipTrnd, bool learned3FlipAdx, bool learned3FlipStrc, bool learned3FlipStf, bool learned3FlipPsar, bool learned3FlipVdya, bool learned3FlipWae, bool learned3FlipZsc, bool learned3FlipTfus, bool learned3FlipArch, bool learned3FlipVenv, bool learned4FlipTrnd, bool learned4FlipAdx, bool learned4FlipStrc, bool learned4FlipStf, bool learned4FlipPsar, bool learned4FlipVdya, bool learned4FlipWae, bool learned4FlipZsc, bool learned4FlipTfus, bool learned4FlipArch, bool learned4FlipVenv, bool learned4FlipSprt, bool learned4FlipCci, bool learned4FlipComp, bool learned4FlipGrev, bool learned4FlipExh, bool learned4FlipPrs, bool includeEye, double weightEye, double weightFlow, double weightIntermarket, double weightPressure, double weightFlux, double weightTrendFusion, double weightArch, double weightCvd, double weightConviction, double weightStructure, double weightSuperTrend, double weightStf, double weightSar, double weightAdxvma, double weightVidya, double weightGodReversal, double weightExhaustion, double weightHarmonic, double weightZScore, bool drawSignalTags, string signalTagPrefix, string instanceTag, int signalMarkerOffsetTicks, double buySignalValue, double sellSignalValue, bool showSetupHelp, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyCouncil_v1_0_0(Input, staleSec, deadband, convictionFloor, activeWeightCap, minVoters, vetoLiquidityWall, wallVetoTicks, vetoNearLevel, levelVetoAtr, vetoMtfOpposed, vetoChop, vetoHighVolRegime, requireSensorOnChart, vetoNormalRegime, vetoKillWindow, vetoNews, squeezeDamp, dryUpDamp, profileValueAreaDamp, profileOverextendedDamp, activeWeightCapDamp, weightTrend, weightCci, weightAdx, weightEnvelope, weightCompression, weightWae, weightBrick, weightProfile, learnedFlipTrnd, learnedFlipAdx, learnedFlipStrc, learnedFlipStf, learnedFlipPsar, learnedFlipVdya, learnedFlipWae, learnedFlipZsc, learnedFlipTfus, learnedFlipArch, learnedFlipVenv, learned1FlipTrnd, learned1FlipAdx, learned1FlipStrc, learned1FlipStf, learned1FlipPsar, learned1FlipVdya, learned1FlipWae, learned1FlipZsc, learned1FlipTfus, learned1FlipArch, learned1FlipVenv, learned1FlipSprt, learned1FlipCci, learned1FlipComp, learned1FlipGrev, learned1FlipExh, learned1FlipPrs, learned2FlipTrnd, learned2FlipAdx, learned2FlipStrc, learned2FlipStf, learned2FlipPsar, learned2FlipVdya, learned2FlipWae, learned2FlipZsc, learned2FlipTfus, learned2FlipArch, learned2FlipVenv, learned2FlipSprt, learned2FlipCci, learned2FlipComp, learned2FlipGrev, learned2FlipExh, learned2FlipPrs, learned3FlipTrnd, learned3FlipAdx, learned3FlipStrc, learned3FlipStf, learned3FlipPsar, learned3FlipVdya, learned3FlipWae, learned3FlipZsc, learned3FlipTfus, learned3FlipArch, learned3FlipVenv, learned4FlipTrnd, learned4FlipAdx, learned4FlipStrc, learned4FlipStf, learned4FlipPsar, learned4FlipVdya, learned4FlipWae, learned4FlipZsc, learned4FlipTfus, learned4FlipArch, learned4FlipVenv, learned4FlipSprt, learned4FlipCci, learned4FlipComp, learned4FlipGrev, learned4FlipExh, learned4FlipPrs, includeEye, weightEye, weightFlow, weightIntermarket, weightPressure, weightFlux, weightTrendFusion, weightArch, weightCvd, weightConviction, weightStructure, weightSuperTrend, weightStf, weightSar, weightAdxvma, weightVidya, weightGodReversal, weightExhaustion, weightHarmonic, weightZScore, drawSignalTags, signalTagPrefix, instanceTag, signalMarkerOffsetTicks, buySignalValue, sellSignalValue, showSetupHelp, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyCouncil_v1_0_0 SneakyCouncil_v1_0_0(ISeries<double> input , double staleSec, double deadband, double convictionFloor, double activeWeightCap, int minVoters, bool vetoLiquidityWall, int wallVetoTicks, bool vetoNearLevel, double levelVetoAtr, bool vetoMtfOpposed, bool vetoChop, bool vetoHighVolRegime, bool requireSensorOnChart, bool vetoNormalRegime, bool vetoKillWindow, bool vetoNews, double squeezeDamp, double dryUpDamp, double profileValueAreaDamp, double profileOverextendedDamp, double activeWeightCapDamp, double weightTrend, double weightCci, double weightAdx, double weightEnvelope, double weightCompression, double weightWae, double weightBrick, SneakyWeightProfile weightProfile, bool learnedFlipTrnd, bool learnedFlipAdx, bool learnedFlipStrc, bool learnedFlipStf, bool learnedFlipPsar, bool learnedFlipVdya, bool learnedFlipWae, bool learnedFlipZsc, bool learnedFlipTfus, bool learnedFlipArch, bool learnedFlipVenv, bool learned1FlipTrnd, bool learned1FlipAdx, bool learned1FlipStrc, bool learned1FlipStf, bool learned1FlipPsar, bool learned1FlipVdya, bool learned1FlipWae, bool learned1FlipZsc, bool learned1FlipTfus, bool learned1FlipArch, bool learned1FlipVenv, bool learned1FlipSprt, bool learned1FlipCci, bool learned1FlipComp, bool learned1FlipGrev, bool learned1FlipExh, bool learned1FlipPrs, bool learned2FlipTrnd, bool learned2FlipAdx, bool learned2FlipStrc, bool learned2FlipStf, bool learned2FlipPsar, bool learned2FlipVdya, bool learned2FlipWae, bool learned2FlipZsc, bool learned2FlipTfus, bool learned2FlipArch, bool learned2FlipVenv, bool learned2FlipSprt, bool learned2FlipCci, bool learned2FlipComp, bool learned2FlipGrev, bool learned2FlipExh, bool learned2FlipPrs, bool learned3FlipTrnd, bool learned3FlipAdx, bool learned3FlipStrc, bool learned3FlipStf, bool learned3FlipPsar, bool learned3FlipVdya, bool learned3FlipWae, bool learned3FlipZsc, bool learned3FlipTfus, bool learned3FlipArch, bool learned3FlipVenv, bool learned4FlipTrnd, bool learned4FlipAdx, bool learned4FlipStrc, bool learned4FlipStf, bool learned4FlipPsar, bool learned4FlipVdya, bool learned4FlipWae, bool learned4FlipZsc, bool learned4FlipTfus, bool learned4FlipArch, bool learned4FlipVenv, bool learned4FlipSprt, bool learned4FlipCci, bool learned4FlipComp, bool learned4FlipGrev, bool learned4FlipExh, bool learned4FlipPrs, bool includeEye, double weightEye, double weightFlow, double weightIntermarket, double weightPressure, double weightFlux, double weightTrendFusion, double weightArch, double weightCvd, double weightConviction, double weightStructure, double weightSuperTrend, double weightStf, double weightSar, double weightAdxvma, double weightVidya, double weightGodReversal, double weightExhaustion, double weightHarmonic, double weightZScore, bool drawSignalTags, string signalTagPrefix, string instanceTag, int signalMarkerOffsetTicks, double buySignalValue, double sellSignalValue, bool showSetupHelp, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyCouncil_v1_0_0(input, staleSec, deadband, convictionFloor, activeWeightCap, minVoters, vetoLiquidityWall, wallVetoTicks, vetoNearLevel, levelVetoAtr, vetoMtfOpposed, vetoChop, vetoHighVolRegime, requireSensorOnChart, vetoNormalRegime, vetoKillWindow, vetoNews, squeezeDamp, dryUpDamp, profileValueAreaDamp, profileOverextendedDamp, activeWeightCapDamp, weightTrend, weightCci, weightAdx, weightEnvelope, weightCompression, weightWae, weightBrick, weightProfile, learnedFlipTrnd, learnedFlipAdx, learnedFlipStrc, learnedFlipStf, learnedFlipPsar, learnedFlipVdya, learnedFlipWae, learnedFlipZsc, learnedFlipTfus, learnedFlipArch, learnedFlipVenv, learned1FlipTrnd, learned1FlipAdx, learned1FlipStrc, learned1FlipStf, learned1FlipPsar, learned1FlipVdya, learned1FlipWae, learned1FlipZsc, learned1FlipTfus, learned1FlipArch, learned1FlipVenv, learned1FlipSprt, learned1FlipCci, learned1FlipComp, learned1FlipGrev, learned1FlipExh, learned1FlipPrs, learned2FlipTrnd, learned2FlipAdx, learned2FlipStrc, learned2FlipStf, learned2FlipPsar, learned2FlipVdya, learned2FlipWae, learned2FlipZsc, learned2FlipTfus, learned2FlipArch, learned2FlipVenv, learned2FlipSprt, learned2FlipCci, learned2FlipComp, learned2FlipGrev, learned2FlipExh, learned2FlipPrs, learned3FlipTrnd, learned3FlipAdx, learned3FlipStrc, learned3FlipStf, learned3FlipPsar, learned3FlipVdya, learned3FlipWae, learned3FlipZsc, learned3FlipTfus, learned3FlipArch, learned3FlipVenv, learned4FlipTrnd, learned4FlipAdx, learned4FlipStrc, learned4FlipStf, learned4FlipPsar, learned4FlipVdya, learned4FlipWae, learned4FlipZsc, learned4FlipTfus, learned4FlipArch, learned4FlipVenv, learned4FlipSprt, learned4FlipCci, learned4FlipComp, learned4FlipGrev, learned4FlipExh, learned4FlipPrs, includeEye, weightEye, weightFlow, weightIntermarket, weightPressure, weightFlux, weightTrendFusion, weightArch, weightCvd, weightConviction, weightStructure, weightSuperTrend, weightStf, weightSar, weightAdxvma, weightVidya, weightGodReversal, weightExhaustion, weightHarmonic, weightZScore, drawSignalTags, signalTagPrefix, instanceTag, signalMarkerOffsetTicks, buySignalValue, sellSignalValue, showSetupHelp, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Sensors.SneakyCouncil_v1_0_0 SneakyCouncil_v1_0_0(double staleSec, double deadband, double convictionFloor, double activeWeightCap, int minVoters, bool vetoLiquidityWall, int wallVetoTicks, bool vetoNearLevel, double levelVetoAtr, bool vetoMtfOpposed, bool vetoChop, bool vetoHighVolRegime, bool requireSensorOnChart, bool vetoNormalRegime, bool vetoKillWindow, bool vetoNews, double squeezeDamp, double dryUpDamp, double profileValueAreaDamp, double profileOverextendedDamp, double activeWeightCapDamp, double weightTrend, double weightCci, double weightAdx, double weightEnvelope, double weightCompression, double weightWae, double weightBrick, SneakyWeightProfile weightProfile, bool learnedFlipTrnd, bool learnedFlipAdx, bool learnedFlipStrc, bool learnedFlipStf, bool learnedFlipPsar, bool learnedFlipVdya, bool learnedFlipWae, bool learnedFlipZsc, bool learnedFlipTfus, bool learnedFlipArch, bool learnedFlipVenv, bool learned1FlipTrnd, bool learned1FlipAdx, bool learned1FlipStrc, bool learned1FlipStf, bool learned1FlipPsar, bool learned1FlipVdya, bool learned1FlipWae, bool learned1FlipZsc, bool learned1FlipTfus, bool learned1FlipArch, bool learned1FlipVenv, bool learned1FlipSprt, bool learned1FlipCci, bool learned1FlipComp, bool learned1FlipGrev, bool learned1FlipExh, bool learned1FlipPrs, bool learned2FlipTrnd, bool learned2FlipAdx, bool learned2FlipStrc, bool learned2FlipStf, bool learned2FlipPsar, bool learned2FlipVdya, bool learned2FlipWae, bool learned2FlipZsc, bool learned2FlipTfus, bool learned2FlipArch, bool learned2FlipVenv, bool learned2FlipSprt, bool learned2FlipCci, bool learned2FlipComp, bool learned2FlipGrev, bool learned2FlipExh, bool learned2FlipPrs, bool learned3FlipTrnd, bool learned3FlipAdx, bool learned3FlipStrc, bool learned3FlipStf, bool learned3FlipPsar, bool learned3FlipVdya, bool learned3FlipWae, bool learned3FlipZsc, bool learned3FlipTfus, bool learned3FlipArch, bool learned3FlipVenv, bool learned4FlipTrnd, bool learned4FlipAdx, bool learned4FlipStrc, bool learned4FlipStf, bool learned4FlipPsar, bool learned4FlipVdya, bool learned4FlipWae, bool learned4FlipZsc, bool learned4FlipTfus, bool learned4FlipArch, bool learned4FlipVenv, bool learned4FlipSprt, bool learned4FlipCci, bool learned4FlipComp, bool learned4FlipGrev, bool learned4FlipExh, bool learned4FlipPrs, bool includeEye, double weightEye, double weightFlow, double weightIntermarket, double weightPressure, double weightFlux, double weightTrendFusion, double weightArch, double weightCvd, double weightConviction, double weightStructure, double weightSuperTrend, double weightStf, double weightSar, double weightAdxvma, double weightVidya, double weightGodReversal, double weightExhaustion, double weightHarmonic, double weightZScore, bool drawSignalTags, string signalTagPrefix, string instanceTag, int signalMarkerOffsetTicks, double buySignalValue, double sellSignalValue, bool showSetupHelp, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyCouncil_v1_0_0(Input, staleSec, deadband, convictionFloor, activeWeightCap, minVoters, vetoLiquidityWall, wallVetoTicks, vetoNearLevel, levelVetoAtr, vetoMtfOpposed, vetoChop, vetoHighVolRegime, requireSensorOnChart, vetoNormalRegime, vetoKillWindow, vetoNews, squeezeDamp, dryUpDamp, profileValueAreaDamp, profileOverextendedDamp, activeWeightCapDamp, weightTrend, weightCci, weightAdx, weightEnvelope, weightCompression, weightWae, weightBrick, weightProfile, learnedFlipTrnd, learnedFlipAdx, learnedFlipStrc, learnedFlipStf, learnedFlipPsar, learnedFlipVdya, learnedFlipWae, learnedFlipZsc, learnedFlipTfus, learnedFlipArch, learnedFlipVenv, learned1FlipTrnd, learned1FlipAdx, learned1FlipStrc, learned1FlipStf, learned1FlipPsar, learned1FlipVdya, learned1FlipWae, learned1FlipZsc, learned1FlipTfus, learned1FlipArch, learned1FlipVenv, learned1FlipSprt, learned1FlipCci, learned1FlipComp, learned1FlipGrev, learned1FlipExh, learned1FlipPrs, learned2FlipTrnd, learned2FlipAdx, learned2FlipStrc, learned2FlipStf, learned2FlipPsar, learned2FlipVdya, learned2FlipWae, learned2FlipZsc, learned2FlipTfus, learned2FlipArch, learned2FlipVenv, learned2FlipSprt, learned2FlipCci, learned2FlipComp, learned2FlipGrev, learned2FlipExh, learned2FlipPrs, learned3FlipTrnd, learned3FlipAdx, learned3FlipStrc, learned3FlipStf, learned3FlipPsar, learned3FlipVdya, learned3FlipWae, learned3FlipZsc, learned3FlipTfus, learned3FlipArch, learned3FlipVenv, learned4FlipTrnd, learned4FlipAdx, learned4FlipStrc, learned4FlipStf, learned4FlipPsar, learned4FlipVdya, learned4FlipWae, learned4FlipZsc, learned4FlipTfus, learned4FlipArch, learned4FlipVenv, learned4FlipSprt, learned4FlipCci, learned4FlipComp, learned4FlipGrev, learned4FlipExh, learned4FlipPrs, includeEye, weightEye, weightFlow, weightIntermarket, weightPressure, weightFlux, weightTrendFusion, weightArch, weightCvd, weightConviction, weightStructure, weightSuperTrend, weightStf, weightSar, weightAdxvma, weightVidya, weightGodReversal, weightExhaustion, weightHarmonic, weightZScore, drawSignalTags, signalTagPrefix, instanceTag, signalMarkerOffsetTicks, buySignalValue, sellSignalValue, showSetupHelp, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyCouncil_v1_0_0 SneakyCouncil_v1_0_0(ISeries<double> input , double staleSec, double deadband, double convictionFloor, double activeWeightCap, int minVoters, bool vetoLiquidityWall, int wallVetoTicks, bool vetoNearLevel, double levelVetoAtr, bool vetoMtfOpposed, bool vetoChop, bool vetoHighVolRegime, bool requireSensorOnChart, bool vetoNormalRegime, bool vetoKillWindow, bool vetoNews, double squeezeDamp, double dryUpDamp, double profileValueAreaDamp, double profileOverextendedDamp, double activeWeightCapDamp, double weightTrend, double weightCci, double weightAdx, double weightEnvelope, double weightCompression, double weightWae, double weightBrick, SneakyWeightProfile weightProfile, bool learnedFlipTrnd, bool learnedFlipAdx, bool learnedFlipStrc, bool learnedFlipStf, bool learnedFlipPsar, bool learnedFlipVdya, bool learnedFlipWae, bool learnedFlipZsc, bool learnedFlipTfus, bool learnedFlipArch, bool learnedFlipVenv, bool learned1FlipTrnd, bool learned1FlipAdx, bool learned1FlipStrc, bool learned1FlipStf, bool learned1FlipPsar, bool learned1FlipVdya, bool learned1FlipWae, bool learned1FlipZsc, bool learned1FlipTfus, bool learned1FlipArch, bool learned1FlipVenv, bool learned1FlipSprt, bool learned1FlipCci, bool learned1FlipComp, bool learned1FlipGrev, bool learned1FlipExh, bool learned1FlipPrs, bool learned2FlipTrnd, bool learned2FlipAdx, bool learned2FlipStrc, bool learned2FlipStf, bool learned2FlipPsar, bool learned2FlipVdya, bool learned2FlipWae, bool learned2FlipZsc, bool learned2FlipTfus, bool learned2FlipArch, bool learned2FlipVenv, bool learned2FlipSprt, bool learned2FlipCci, bool learned2FlipComp, bool learned2FlipGrev, bool learned2FlipExh, bool learned2FlipPrs, bool learned3FlipTrnd, bool learned3FlipAdx, bool learned3FlipStrc, bool learned3FlipStf, bool learned3FlipPsar, bool learned3FlipVdya, bool learned3FlipWae, bool learned3FlipZsc, bool learned3FlipTfus, bool learned3FlipArch, bool learned3FlipVenv, bool learned4FlipTrnd, bool learned4FlipAdx, bool learned4FlipStrc, bool learned4FlipStf, bool learned4FlipPsar, bool learned4FlipVdya, bool learned4FlipWae, bool learned4FlipZsc, bool learned4FlipTfus, bool learned4FlipArch, bool learned4FlipVenv, bool learned4FlipSprt, bool learned4FlipCci, bool learned4FlipComp, bool learned4FlipGrev, bool learned4FlipExh, bool learned4FlipPrs, bool includeEye, double weightEye, double weightFlow, double weightIntermarket, double weightPressure, double weightFlux, double weightTrendFusion, double weightArch, double weightCvd, double weightConviction, double weightStructure, double weightSuperTrend, double weightStf, double weightSar, double weightAdxvma, double weightVidya, double weightGodReversal, double weightExhaustion, double weightHarmonic, double weightZScore, bool drawSignalTags, string signalTagPrefix, string instanceTag, int signalMarkerOffsetTicks, double buySignalValue, double sellSignalValue, bool showSetupHelp, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyCouncil_v1_0_0(input, staleSec, deadband, convictionFloor, activeWeightCap, minVoters, vetoLiquidityWall, wallVetoTicks, vetoNearLevel, levelVetoAtr, vetoMtfOpposed, vetoChop, vetoHighVolRegime, requireSensorOnChart, vetoNormalRegime, vetoKillWindow, vetoNews, squeezeDamp, dryUpDamp, profileValueAreaDamp, profileOverextendedDamp, activeWeightCapDamp, weightTrend, weightCci, weightAdx, weightEnvelope, weightCompression, weightWae, weightBrick, weightProfile, learnedFlipTrnd, learnedFlipAdx, learnedFlipStrc, learnedFlipStf, learnedFlipPsar, learnedFlipVdya, learnedFlipWae, learnedFlipZsc, learnedFlipTfus, learnedFlipArch, learnedFlipVenv, learned1FlipTrnd, learned1FlipAdx, learned1FlipStrc, learned1FlipStf, learned1FlipPsar, learned1FlipVdya, learned1FlipWae, learned1FlipZsc, learned1FlipTfus, learned1FlipArch, learned1FlipVenv, learned1FlipSprt, learned1FlipCci, learned1FlipComp, learned1FlipGrev, learned1FlipExh, learned1FlipPrs, learned2FlipTrnd, learned2FlipAdx, learned2FlipStrc, learned2FlipStf, learned2FlipPsar, learned2FlipVdya, learned2FlipWae, learned2FlipZsc, learned2FlipTfus, learned2FlipArch, learned2FlipVenv, learned2FlipSprt, learned2FlipCci, learned2FlipComp, learned2FlipGrev, learned2FlipExh, learned2FlipPrs, learned3FlipTrnd, learned3FlipAdx, learned3FlipStrc, learned3FlipStf, learned3FlipPsar, learned3FlipVdya, learned3FlipWae, learned3FlipZsc, learned3FlipTfus, learned3FlipArch, learned3FlipVenv, learned4FlipTrnd, learned4FlipAdx, learned4FlipStrc, learned4FlipStf, learned4FlipPsar, learned4FlipVdya, learned4FlipWae, learned4FlipZsc, learned4FlipTfus, learned4FlipArch, learned4FlipVenv, learned4FlipSprt, learned4FlipCci, learned4FlipComp, learned4FlipGrev, learned4FlipExh, learned4FlipPrs, includeEye, weightEye, weightFlow, weightIntermarket, weightPressure, weightFlux, weightTrendFusion, weightArch, weightCvd, weightConviction, weightStructure, weightSuperTrend, weightStf, weightSar, weightAdxvma, weightVidya, weightGodReversal, weightExhaustion, weightHarmonic, weightZScore, drawSignalTags, signalTagPrefix, instanceTag, signalMarkerOffsetTicks, buySignalValue, sellSignalValue, showSetupHelp, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
