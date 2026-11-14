// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
//
// Copyright (c) 2026 silentsudo-io and the Sentinel Suite contributors.
// ─────────────────────────────────────────────────────────────────────────────
#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;
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
using NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors;
#endregion

// ═════════════════════════════════════════════════════════════════════════════
//  SneakyConfluence — a filter-cascade signal fuser over the SneakyCore seam   |   v1.0.0
//  File: SneakyConfluence_v1_0_0.cs   |   namespace …Indicators.Sneaky.Sensors
//
//  ⚠ NO ORDERS — read-only signal generator. Draws tagged markers + plots a 3rd-party
//  order-automation tool (e.g. PredatorXOrderEntryLT) can key off; it never calls
//  EnterLong/EnterShort itself.
//
//  WHAT THIS IS — the Sneaky Suite's own docs extensively reference a planned
//  "Council" that fuses every sensor's published vote (SneakyCore.VoterCatalog,
//  the various SetXState() seams) into one decision — but no such fusion engine
//  ships in this repo; every sensor publishes, nothing here reads. This is a real,
//  if intentionally SIMPLE, first cut at that missing piece: not a weighted-voting
//  ensemble (that's a genuinely bigger project — see the note at the bottom), but a
//  hand-reasoned filter cascade over SIX of the sensors, chosen for being
//  complementary rather than redundant:
//
//    BIAS       SneakyTrend.Direction   — which way are we even allowed to trade?
//    STRENGTH   ADXPro.Bias + .Strong     — is that trend actually strong, not noise?
//    RISK GATE  SneakyRegime.Regime     — dampen/block entries in chaotic high-vol
//                                            tape (this is the sensor's OWN stated
//                                            purpose — "Council conviction/size
//                                            MODULATOR, not directional").
//    TRIGGER    SneakyWAE.Signal        — confirmed momentum-explosion breakout
//               CompressionBase.Signal    — confirmed coiled-base breakout
//               (either can fire the trigger; a coiled-base break and a WAE
//               explosion are two different ways of saying "a move just started",
//               not competing opinions)
//    OPTIONAL   SneakyGodReversal       — reversal-catching voice, OFF by default;
//                                            it's explicitly the suite's mean-
//                                            reversion signal, and blending it into a
//                                            trend-following cascade unconditionally
//                                            would make the two halves fight each
//                                            other. Opt in deliberately, see below.
//
//  HOW A SIGNAL FIRES — on the bar every REQUIRED gate is true AND a trigger fires,
//  debounced (fires once on the rising edge, not every bar the condition holds).
//  Requires SneakyTrend + ADXPro + (SneakyWAE and/or CompressionBase) to be
//  ATTACHED to this SAME chart with "Publish … to Sneaky" on — this reads the
//  shared SneakyCore seam, it does not instantiate its own copies of those
//  sensors. If a required sensor is missing/stale, its gate fails closed (no
//  signal), never open — this indicator will simply never fire rather than firing
//  on incomplete information. SneakyRegime and SneakyGodReversal are optional;
//  their gates no-op (pass through) when not present.
//
//  OUTPUTS (for 3rd-party bar-tag automation, e.g. PredatorXOrderEntryLT):
//    Plots:  BuySignal / SellSignal — 1.0 on the firing bar, 0.0 otherwise.
//    Tags:   Draw.Text markers — literal "SSCB"/"SSCS" text (lime/red) — named "<Prefix>_Buy_<bar>" /
//            "<Prefix>_Sell_<bar>" — was Draw.Dot, then briefly triangles, until 2026-08-19; settled
//            on text since other indicators on the chart already plot triangles/arrows. Same text
//            and color as Council's own tags — deliberately not distinguished from Council's here.
//
//  A NOTE ON "OPTIMIZED" — this cascade's thresholds (ADX strong-level, regime
//  block, GodReversal quality floor) are reasoned defaults, not backtested ones.
//  Treat this as a sensible STARTING configuration to validate in Strategy
//  Analyzer / Playback before trusting it live, the same way every other signal
//  generator in this conversation has needed real testing — I have no way to run
//  NinjaTrader myself to verify actual win rate, drawdown, or trigger frequency.
// ═════════════════════════════════════════════════════════════════════════════

namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors
{
	public class SneakyConfluence_v1_0_0 : Indicator
	{
		private string _scope;
		private string Scope()
		{
			try { _scope = SneakyCore.ScopeOf(Instrument, BarsPeriod, ChartControl); } catch { }   // 2026-08-04 — ALWAYS recomputed, never permanently cached: a chart's lane can register AFTER this indicator's first call, so caching once risked freezing a stale, unlaned scope forever (confirmed happening for real tonight). Cheap to recompute every call; only overwrites _scope on success, so a transient throw keeps the previous good value.
			return _scope;
		}

		// 2026-08-04 — SELF-HEALING LANE ASSERT — see SneakyCouncil's identical AssertLane for the
		// full rationale. RegisterLane used to fire once, in State.DataLoaded; sentinel.log showed a
		// lane sit unregistered for ~11 minutes across a reload burst. Re-assert on a throttle instead
		// of relying on DataLoaded's timing.
		private int _lastLaneAssertMs;
		private const int LaneAssertMs = 2000;
		private void AssertLane()
		{
			int now = Environment.TickCount;
			if (unchecked(now - _lastLaneAssertMs) < LaneAssertMs) return;
			_lastLaneAssertMs = now;
			try { SneakyCore.RegisterLane(ChartControl, InstanceTag); }
			catch (Exception _sx) { SneakyCore.Swallow("SneakyConfluence.AssertLane", _sx); }
		}

		private bool _prevLongOk, _prevShortOk;
		// 2026-08-18 — edge-detect state for the VIDYA/ADXVMA triggers below. Unlike SAR/SuperTrend,
		// VidyaState/AdxvmaState publish no Flip pulse of their own (State-kind: Signal is continuously
		// ±1/0, always the current bias, never a one-bar event) — so the sign-change has to be detected
		// here instead, bar over bar.
		private int _prevVidyaSignal, _prevAdxvmaSignal;

		// Last-evaluated factor states, kept only for the card readout (not the trading logic itself).
		private int    _lastBias;
		private bool   _haveTrend, _haveAdx, _haveRegime, _haveWae, _haveComp, _haveGrev;
		private bool   _regimeOk, _adxOk;
		private bool   _liqOk, _volOk, _zscoreOk;
		private bool   _levelOk = true;
		private string _lastLevelLabel = "n/a";
		private int _lastImktDiagLog = 0;   // FIXED 2026-08-03 — was int.MinValue, which overflows in
		// unchecked(TickCount - X) and permanently evaluates negative for the whole session, silently
		// blocking every single log attempt forever. 0 is a safe sentinel: TickCount for any system
		// that's been up more than 5 seconds is comfortably > 5000 on the very first real check.
		private int _lastMtfLaneDiagLog = 0;
		private bool   _haveLiq, _haveVol, _haveZscore;
		private string _lastLiqLabel = "n/a";
		private string _lastVolLabel = "n/a";
		private string _lastZscoreLabel = "n/a";
		private string _lastRegimeLabel = "n/a";
		private string _lastTriggerLabel = "—";
		private bool   _cvbOk = true, _haveCvb;
		private string _lastCvbLabel = "n/a";
		private bool   _chopOk = true, _cciOk = true, _exhOk = true, _harmOk = true;
		private bool   _imktOk = true, _eyeOk = true, _flowOk = true, _partOk = true, _structOk = true, _clockOk = true, _mtfOk = true, _profOk = true, _newsOk = true;
		private bool   _profOverextOk = true;
		private bool   _fluxOk = true;
		private bool   _tfusOk = true;
		private bool   _archOk = true;
		private string _lastGateNote = "";
		private double _sarLevel = double.NaN, _stLevel = double.NaN;
		private bool   _brkOk = true, _persistOk = true, _absorbOk = true, _effOk = true;
		private string _lastBrickLabel = "n/a";
		private readonly List<string> _setupNotes = new List<string>();
		private bool _setupPrinted;

		// BuySellVolumePressureMountain doesn't publish to the SneakyCore seam (unlike the other five
		// components below), so it's read as a direct sub-indicator instead — same as how any NinjaScript
		// indicator composes another (e.g. calling ATR()/EMA()). Defaults match its own SetDefaults, so
		// if the user already has one attached with default settings, NT8's CacheIndicator dedupes this
		// to the SAME instance rather than computing it twice.
		private SneakyBuySellVolumePressureMountain_v1_0_0 _bsv;

		private SneakySkin.Painter _sp;
		private int _lastRenderExLog = 0;   // FIXED 2026-08-03 — was int.MinValue, which overflows in unchecked(TickCount - X) and permanently evaluates negative, silently blocking every exception log for the whole session. 0 is a safe sentinel: TickCount for any system up more than a few seconds is comfortably past any real threshold on the first check.

		// 2026-08-19 — card health, same pattern/reasoning as Council's own (see its comments near
		// _lastGoodBarAt): the card redrawing only proves OnRender() runs, not that OnBarUpdate()'s
		// actual gate computation is still alive, and SetConfluenceState's own catch block used to be
		// a totally silent `catch { }` — a failed publish here was invisible even in the Output tab.
		private DateTime _lastGoodBarAt = DateTime.MinValue;
		private const double HeartbeatWarnMinutes = 10.0;
		private bool _lastPublishOk = true;
		private int _lastPublishFailLog = 0;   // same 0-sentinel fix as _lastRenderExLog above

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Filter-cascade signal fuser over the SneakyCore seam. EVERY gate must pass, THEN a trigger must fire — so "
					+ "adding gates makes signals RARER, not better. REQUIRED: SneakyTrend (bias) + ADXPro (strength) + SneakyWAE / "
					+ "CompressionBase (breakout trigger) + SneakyBuySellVolumePressureMountain (order-flow confirmation). OPTIONAL "
					+ "VETOES, all fail-open (absent sensor = gate does nothing): SneakyRegime (high-vol), LiquidityWalls (wall in "
					+ "path), SneakyLocation (structural level in path), SneakyZScore (don't-chase-an-extended-move), SneakyStochasticTripleFilter (chop), WoodiesCCIPro "
					+ "(weakening momentum), SneakyExhaustion / SneakyHarmonic (counter-trend), VolEnvelope (squeeze-qualifies the "
					+ "compression trigger), Intermarket (other-markets lean), Eye (orthogonal qualifier), Flow (CVD-regression "
					+ "confirmed direction), Participation (dry-up), Structure (confirmed swing structure), Clock (kill window), "
					+ "Mtf (higher-timeframe not opposed), Profile (POC-reversion not opposed), "
					+ "and the bar-type gates (brick agreement / run length, plus absorption + efficiency on SneakyEffort bars only). "
					+ "The last five are the suite's NON-price-derived / independent sources — unlike most gates here, they add "
					+ "genuinely new information rather than re-reading this instrument's own OHLC through another filter. "
					+ "SneakyGodReversal is an optional EXTRA TRIGGER. SneakyParabolicSAR / SneakySuperTrend / "
					+ "SneakyVIDYA / SneakyADXVMA are four optional ADDITIONAL TRIGGER sources (same OR-mechanism "
					+ "as WAE/Compression, not the independent GodReversal mechanism) — all off by default; see the "
					+ "'2. Trigger' property group. SneakyDrift's CVB flow gate needs "
					+ "Drift as the chart BAR TYPE and only publishes on live ticks. HOW IT READS THEM: all components are "
					+ "read as published seam state — including SneakyBuySellVolumePressureMountain, which now publishes a "
					+ "PressureState seam of its own (bias, buy/sell %, delta, dominance ratio, and a UsedTicks flag saying whether "
					+ "the bar was classified from real ticks or the OHLC fallback). A direct sub-indicator read is retained ONLY as "
					+ "a fallback, so an older non-publishing build still works. Draws tagged markers and plots BuySignal/SellSignal "
					+ "(transparent; the visible markers are the draw tags) for 3rd-party bar-tag or plot-value automation. Places "
					+ "no orders itself. ";
				Name                     = "Sneaky Confluence v1.0.0";
				Calculate                = Calculate.OnBarClose;
				IsOverlay                = true;
				DisplayInDataBox         = true;
				DrawOnPricePanel         = true;
				PaintPriceMarkers        = false;
				IsSuspendedWhileInactive = false;
				ShowIndicatorLabel       = false;   // Sneaky standard: clean chart (NT name label removed)

				RequireAdxStrong       = true;
				// 2026-08-11 — turned OFF by default. train_confluence.py's gate audit (after-lane-fix
				// corpus) found cnfRegimeOk scoring -18.10 ticks WORSE on its own True (allowed) bucket
				// vs False (blocked), disagreeing in ALL 3 folds it had data for — not noise. Root
				// cause: this gate blocks Regime==2 (high vol), but grade_sneaky.py's own REGIME table
				// (run separately, same corpus) shows Regime==2 is the BEST bucket (+21.66 vs base) and
				// Regime==1 is the WORST (-27.56 vs base) — this gate has been blocking its best regime
				// and letting its worst one through the whole time. Leaving the property here (in case
				// a future/different corpus supports it again) but off until re-validated — same
				// audition discipline as every other gate in this suite. See BlockNormalVolRegime below
				// for the gate that actually targets the regime the data says is the problem.
				BlockHighVolRegime     = false;
				// 2026-08-11 — NEW. The correctly-targeted version of the finding above: Council
				// already has an analogous VetoNormalRegime (off by default, same reasoning) — this is
				// Confluence's equivalent. Off by default, same audition discipline, until validated on
				// its own fresh post-fix corpus rather than assumed correct just because the OTHER
				// gate's target was proven wrong.
				BlockNormalVolRegime   = false;
				UseWaeTrigger          = true;
				UseCompressionTrigger  = true;
				// 2026-08-18 — four more trigger sources, same OR-mechanism as WAE/Compression (any one
				// firing satisfies the trigger requirement). All off by default — opt in per chart; see
				// the "2. Trigger" property group descriptions for what each actually fires on.
				UseSarTrigger          = false;
				UseSuperTrendTrigger   = false;
				UseVidyaTrigger        = false;
				UseAdxvmaTrigger       = false;
				UseGodReversalAsExtra  = false;
				MinGodReversalQuality  = 0.60;
				SignalMarkerOffsetTicks = 4;
				StaleSec               = 90.0;  // was 5.0 — matches Trend/Intermarket/Eye's convention. Safe when
												  // every scope-keyed source lives on the same chart (bar closes
												  // are near-simultaneous); only matters if a source runs on a
												  // slower/different chart. Costs nothing here.

				BlockNearLiquidityWalls = true;
				WallVetoTicks           = 8;
				BlockNearLevel          = true;
				LevelNearAtr            = 0.5;
				// 2026-08-14 — was TRUE. Turned OFF: consistently the most negative gate in every
				// grade_sneaky.py / train_confluence.py pull all project (-15 to -57 ticks edge-gap,
				// never once positive). Root cause: this requires the dominant volume side to AGREE
				// with bias ("volume confirms direction") — but the standalone PRESSURE sensor table
				// this reads from consistently shows 'opposes' outperforming 'agrees' (this week:
				// agrees +27.16 vs opposes +34.59; cost-adjusted full corpus: +13.17 vs +17.57).
				// Requiring confirmation filters FOR the merely-good case while filtering OUT the
				// better one. Not as extreme as the Profile finding (agreement isn't actively bad
				// here), but the same shape of premise-vs-data mismatch. Same conservative treatment
				// as the regime-gate fix: turn off the harmful requirement rather than build an
				// unvalidated inverted version on top of it — re-check after this has run without
				// the requirement for a while, same as VetoProfileOverextended's own audition.
				RequireVolumeConfirm    = false;
				BlockExtendedZScore     = true;
				RequireDriftConviction  = false;   // OFF by default — needs SneakyDrift as the bar type

				// ── all OFF by default: each one can only ever REMOVE signals, so none of
				//    them should change this indicator's behaviour until you switch it on.
				BlockChop                    = false;
				BlockWeakeningCci            = false;
				BlockCounterExhaustion       = false;
				BlockCounterHarmonic         = false;
				BlockIntermarketOpposed      = false;
				RequireEyeNotOpposed         = false;
				RequireMtfNotOpposed         = false;
			RequireFluxNotOpposed        = false;   // 2026-08-06 — new, off by default until validated
			RequireTrendFusionNotOpposed = false;   // 2026-08-06 — new, off by default until validated
			RequireArchNotOpposed        = false;   // 2026-08-07 — new, off by default until validated
				RequireProfileNotOpposed     = false;
			VetoProfileOverextended      = false;
				RequireFlowNotOpposed        = false;
				BlockOnDryUp                 = false;
				BlockCounterStructure        = false;
				BlockInKillWindow            = false;
				BlockInNewsLockout           = false;   // 2026-08-08 — same discipline: off by default until validated
				RequireSqueezeForCompression = false;
				MaxBandwidthPctile           = 25.0;
				ShowStopLevels               = false;
				ShowSetupHelp                = true;

				// ── bar-type (BrickState) gates — all OFF, all fail-open ──
				RequireBrickAgreement = false;
				MinConsecutiveBricks  = 0;      // 0 = disabled
				BlockOnAbsorption     = false;  // SneakyEffort only
				MinBrickEfficiency    = 0.0;    // 0 = disabled; SneakyEffort only

				DrawSignalTags   = true;
				SignalTagPrefix  = "SConf";
				InstanceTag      = "";
				// Signal values are independently settable so this can speak EITHER convention:
				//   +1 / +1  → this suite's own (SneakyWAE, CompressionBase, …)
				//   +1 / -1  → PredatorX's LongEntryPlotValue=1 / ShortEntryPlotValue=-1
				BuySignalValue   =  1.0;
				SellSignalValue  =  1.0;
				ShowCard         = true;
				CardCorner       = SneakyCardCorner.TopLeft;

				// Values[0]/[1]: 1.0 on the firing bar, 0.0 otherwise — same convention as every other
				// signal series in this suite (SneakyWAE.Signal, CompressionBase.Signal, etc.).
				// Transparent ON PURPOSE. These carry a SIGNAL VALUE (0 / ±1), not a price,
				// so NinjaTrader plots them at y=0 and y=±1 — thousands of points below price
				// on the panel. The values stay readable by PredatorX and any plot-reader;
				// the VISIBLE markers come from the Draw.Text signal tags — literal "SSCB"/"SSCS"
				// text (2026-08-19 — was Draw.Dot, then briefly triangles; settled on text so it
				// doesn't collide with other indicators on the chart that already plot triangles),
				// which are placed at Low[0]-offset / High[0]+offset — real price levels, next to
				// the bar. Same text and color as Council's own tags — deliberately not
				// distinguished from Confluence's here.
				AddPlot(new Stroke(Brushes.Transparent, 3), PlotStyle.Dot, "SneakyConfluenceBuy");
				AddPlot(new Stroke(Brushes.Transparent, 3), PlotStyle.Dot, "SneakyConfluenceSell");
			}
			else if (State == State.DataLoaded)
			{
				// ShowIndicatorLabel isn't an NT8 built-in — it's this suite's own convention (see
				// SneakyTrend etc.): the "label" NT8 draws per-indicator is literally its Name, so
				// blanking Name is how every Sneaky tool suppresses it for a clean chart.
				if (!ShowIndicatorLabel) Name = string.Empty;
				// v2026-08-04 — registers this chart's lane (if InstanceTag is set) so every OTHER
				// seam publisher/consumer on this SAME chart automatically resolves to the same
				// lane-qualified scope, with zero per-indicator config on their end.
				// (Backstopped by AssertLane() in OnBarUpdate — see its comment.)
				try { SneakyCore.RegisterLane(ChartControl, InstanceTag); }
				catch (Exception _sx) { SneakyCore.Swallow("SneakyConfluence.DataLoaded.RegisterLane", _sx); }

				// Parameters match BuySellVolumePressureMountain's own SetDefaults exactly — if the user
				// already has one attached with default settings, NT8's CacheIndicator dedupes this to
				// that SAME instance rather than computing it twice. ShowInfo=false here regardless (this
				// internal reference never draws its own card — only the user's own separately-attached
				// instance, if any, would).
				_bsv = SneakyBuySellVolumePressureMountain_v1_0_0(17, 17, true, 1.75, 2.0, 3, 100.0,
					false, true, true, 3, 60, false, SneakyCardCorner.TopRight, false);
			}
			else if (State == State.Terminated)
			{
				// Confluence never had Terminated cleanup at all before this — the Painter below was
				// leaking on every chart removal/reload, and a registered lane would have stuck
				// around pointing at a dead ChartControl forever.
				if (_sp != null) { try { _sp.Dispose(); } catch { } _sp = null; }
				try { SneakySkin.CardLayout.Release(this); } catch { }
				try { SneakyCore.ClearLane(ChartControl); } catch { }
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 2) return;

			AssertLane();

			Values[0][0] = 0.0;
			Values[1][0] = 0.0;
			// Belt AND braces. AddPlot's transparent stroke only applies to a NEWLY added
			// instance: NinjaTrader SERIALIZES plot colour per chart instance, so an
			// indicator already on the chart keeps whatever colour it was saved with and
			// ignores the code change. PlotBrushes is a RUNTIME override that beats the
			// saved colour, so setting it every bar guarantees these never paint —
			// existing instances included, with no remove-and-re-add needed.
			PlotBrushes[0][0] = Brushes.Transparent;
			PlotBrushes[1][0] = Brushes.Transparent;
			// Value kept at 0.0 for automation; brush hidden so PlotStyle.Dot doesn't paint
			// a row of zeros on the price panel far below price.

			SneakyCore.TrendState      trend  = null;
			SneakyCore.AdxState        adx    = null;
			SneakyCore.WaeState        wae    = null;
			SneakyCore.CompressionState comp  = null;
			SneakyCore.RegimeState     regime = null;
			SneakyCore.GodReversalState grev  = null;
			SneakyCore.LiquidityState  liq    = null;
			SneakyCore.ZScoreState     zscore = null;
			SneakyCore.ConvictionState cvb    = null;
			SneakyCore.StfState        stf    = null; SneakyCore.CciState        cci  = null;
			SneakyCore.ExhaustionState exh    = null; SneakyCore.HarmonicState   harm = null;
			SneakyCore.EnvelopeState   env    = null; SneakyCore.SarState        sar  = null;
			SneakyCore.SuperTrendState sprt   = null; SneakyCore.BrickState      brick = null;
			SneakyCore.VidyaState      vdya   = null; SneakyCore.AdxvmaState     avma  = null;
			try { trend  = SneakyCore.GetTrendState(Scope(), StaleSec); }       catch { }
			try { adx    = SneakyCore.GetAdxState(Scope(), StaleSec); }         catch { }
			try { wae    = SneakyCore.GetWaeState(Scope(), StaleSec); }         catch { }
			try { comp   = SneakyCore.GetCompressionState(Scope(), StaleSec); } catch { }
			try { regime = SneakyCore.GetRegimeState(Scope(), StaleSec); }      catch { }
			try { grev   = SneakyCore.GetGodReversalState(Scope(), StaleSec); } catch { }
			try { liq    = SneakyCore.GetLiquidityState(Scope(), StaleSec); }   catch { }
			SneakyCore.LevelState lv = null;
			try { lv     = SneakyCore.GetLevelState(Scope(), StaleSec); }       catch { }
			try { zscore = SneakyCore.GetZScoreState(Scope(), StaleSec); }      catch { }
			// Drift's CVB seam. Only present when SneakyDrift is the chart's BAR TYPE — on a
			// v1.36.0 core it resolves through the compat shim. Absent ⇒ this gate no-ops.
			try { cvb    = SneakyCore.GetConvictionState(Scope(), StaleSec); } catch { }
			// All optional. Every one of these fails OPEN when its sensor isn't attached —
			// same contract as the regime/wall/z-score gates already here.
			try { stf   = SneakyCore.GetStfState(Scope(), StaleSec); }         catch { }
			try { cci   = SneakyCore.GetCciState(Scope(), StaleSec); }         catch { }
			try { exh   = SneakyCore.GetExhaustionState(Scope(), StaleSec); }  catch { }
			try { harm  = SneakyCore.GetHarmonicState(Scope(), StaleSec); }    catch { }
			try { env   = SneakyCore.GetEnvelopeState(Scope(), StaleSec); }    catch { }
			try { sar   = SneakyCore.GetSarState(Scope(), StaleSec); }         catch { }
			try { sprt  = SneakyCore.GetSuperTrendState(Scope(), StaleSec); }  catch { }
			try { vdya  = SneakyCore.GetVidyaState(Scope(), StaleSec); }       catch { }
			try { avma  = SneakyCore.GetAdxvmaState(Scope(), StaleSec); }      catch { }
			// Published by SneakyTBars / Drift / Lattice / Effort. Absent on an ordinary
			// bar type ⇒ every gate below fails open.
			try { brick = SneakyCore.GetBrickState(Scope(), StaleSec); }       catch { }

			// ── the suite's genuinely NON-price-derived / independent sources ──
			SneakyCore.IntermarketState  imkt = null; SneakyCore.EyeVerdict     eye = null;
			SneakyCore.FlowState         flow = null; SneakyCore.ParticipationState part = null;
			SneakyCore.StructureState    strc = null; SneakyCore.ClockState     clk = null;
			// Intermarket is INSTRUMENT-keyed (see SneakyIntermarket/Council), not scope-keyed —
			// one instance anywhere on this instrument's chart feeds every consumer.
			try { if (Instrument != null && Instrument.MasterInstrument != null)
					imkt = SneakyCore.GetIntermarketState(Instrument.MasterInstrument.Name, StaleSec); } catch { }
			try { eye  = SneakyCore.GetEyeVerdict(Scope(), StaleSec); }         catch { }
			// MTF is price-derived (EMA cross on this instrument's OWN price, just on OTHER timeframes) —
			// a different ANGLE (the bigger picture) rather than genuinely independent evidence, so it's
			// kept out of the non-price-derived block above even though it lives in the same optional
			// gate bucket for cascade purposes.
			SneakyCore.MtfState mtf = null;
			try { mtf  = SneakyCore.GetMtfState(Scope(), StaleSec); }           catch { }
			// FLUX — the order-flow-imbalance bar type's own seam. Only exists on a chart running
			// SneakyFlux; abstains everywhere else, same as CVB/BRK. Added 2026-08-06 alongside
			// the Council FLUX voter — this seam existed in Core since v1.31.0 and the SneakyFlux
			// bar-type engine has always published it, but nothing ever consulted it.
			SneakyCore.FluxState flux = null;
			try { flux = SneakyCore.GetFluxState(Scope(), StaleSec); }          catch { }
			// TFUS — SneakyTrendFusion's composite read. NOT PRISM/ARCH — see that file's header.
			SneakyCore.TrendFusionState tfus = null;
			try { tfus = SneakyCore.GetTrendFusionState(Scope(), StaleSec); }   catch { }
			SneakyCore.TrendArchitectState arch = null;
			try { arch = SneakyCore.GetTrendArchitectState(Scope(), StaleSec); } catch { }
			// Same reasoning as MTF above — Profile's POC-reversion Signal is price/volume-derived,
			// just a different structural angle (today's traded volume shape) than the trend tools.
			SneakyCore.ProfileState prof = null;
			try { prof = SneakyCore.GetProfileState(Scope(), StaleSec); }       catch { }
			try { flow = SneakyCore.GetFlowState(Scope(), StaleSec); }         catch { }
			try { part = SneakyCore.GetParticipationState(Scope(), StaleSec); } catch { }
			try { strc = SneakyCore.GetStructureState(Scope(), StaleSec); }    catch { }
			// Clock is INSTRUMENT-keyed, same reasoning as Intermarket — session phase doesn't
			// depend on bar type.
			try { if (Instrument != null && Instrument.MasterInstrument != null)
					clk = SneakyCore.GetClockState(Instrument.MasterInstrument.Name, StaleSec); } catch { }

			_haveTrend = trend != null; _haveAdx = adx != null; _haveRegime = regime != null;
			_haveWae = wae != null; _haveComp = comp != null; _haveGrev = grev != null;
			_haveLiq = liq != null; _haveVol = _bsv != null; _haveZscore = zscore != null;

			// BIAS — the required directional gate. No SneakyTrend attached/publishing ⇒ bias stays 0
			// ⇒ neither longOk nor shortOk can ever become true. Fails closed, not open.
			int bias = trend != null ? trend.Direction : 0;
			_lastBias = bias;

			// RISK GATE — SneakyRegime's own stated purpose is dampening conviction in chaotic tape,
			// not picking direction. Missing/stale regime data no-ops this gate (passes through) rather
			// than blocking trades just because the optional sensor isn't attached.
			// 2026-08-11 — now checks BOTH regimes independently (see the SetDefaults comments above
			// BlockHighVolRegime / BlockNormalVolRegime for why there are two). Either toggle alone
			// can fail this gate; regime==null still passes through untouched either way.
			bool blockedHigh   = BlockHighVolRegime   && regime != null && regime.Regime == 2;
			bool blockedNormal = BlockNormalVolRegime && regime != null && regime.Regime == 1;
			_regimeOk = regime == null || !(blockedHigh || blockedNormal);
			_lastRegimeLabel = regime == null ? "n/a"
				: (regime.Regime == 0 ? "LOW vol"
					: regime.Regime == 1 ? (blockedNormal ? "MED vol (blocked)" : "MED vol")
					: (blockedHigh ? "HIGH vol (blocked)" : "HIGH vol"));

			// STRENGTH — requires ADXPro to actually agree with the SAME bias direction, not just be
			// "strong" in the abstract. Missing ADXPro ⇒ adxOk stays false when RequireAdxStrong is on,
			// so (like Trend) an absent required sensor fails closed rather than silently passing.
			_adxOk = !RequireAdxStrong || (adx != null && adx.Strong && bias != 0 && adx.Bias == bias);

			// VETO — LiquidityWalls.BlocksEntry(dir, ticks) is the sensor's own purpose-built helper:
			// true when a resistance wall sits above (for a long) or a support wall sits below (for a
			// short) within WallVetoTicks — exactly the "about to run straight into a wall" case. Missing
			// LiquidityWalls no-ops this gate (optional context, like Regime), rather than blocking trades
			// just because that sensor isn't attached.
			_liqOk = !BlockNearLiquidityWalls || liq == null || !liq.BlocksEntry(bias, WallVetoTicks);
			_lastLiqLabel = liq == null ? "n/a"
				: (liq.BlocksEntry(bias, WallVetoTicks) ? "wall within " + WallVetoTicks + "t (blocked)"
					: (liq.ResistanceAbove || liq.SupportBelow ? "wall present, clear" : "no nearby wall"));

			// VETO — SneakyLocation.InPath(dir, atr) is the seam's own purpose-built helper: true when
			// the NEAREST structural level (VWAP/PDH/PDL/OR/IB/session H-L) sits in the PATH of this
			// bias within LevelNearAtr — the same "about to run straight into a level" case LiquidityWalls
			// covers for order-flow walls, just for the classic reference levels instead. Missing
			// SneakyLocation no-ops this gate (optional context), same as LiquidityWalls being absent.
			_levelOk = !BlockNearLevel || lv == null || !lv.InPath(bias, LevelNearAtr);
			_lastLevelLabel = lv == null ? "n/a"
				: (lv.InPath(bias, LevelNearAtr) ? (lv.NearestName ?? "level") + " within " + LevelNearAtr.ToString("0.00") + " ATR (blocked)"
					: ((lv.NearestName ?? "—") + " clear"));

			// CONFIRMATION — requires actual order-flow pressure to agree with bias, not just price
			// structure. This is a REQUIRED confirmation tier (like ADX) when enabled, not optional
			// context — a structurally-clean breakout with no real volume behind it is a classic fakeout.
			// Prefer the SEAM (same contract + staleness rule as every other sensor);
			// fall back to the direct sub-indicator so this still works if an older
			// BuySellVolumePressureMountain that doesn't publish is on the chart.
			double dom = double.NaN;
			SneakyCore.PressureState press = null;
			try { press = SneakyCore.GetPressureState(Scope(), StaleSec); } catch { }
			if (press != null) dom = press.Dir;
			else { try { if (_bsv != null) dom = _bsv.DominantSide[0]; } catch { } }
			_volOk = !RequireVolumeConfirm || (!double.IsNaN(dom) && bias != 0
				&& ((bias > 0 && dom > 0) || (bias < 0 && dom < 0)));
			_lastVolLabel = double.IsNaN(dom) ? "n/a" : (dom > 0 ? "buy-dominant" : dom < 0 ? "sell-dominant" : "neutral");

			// DAMPENER — same role as Regime: don't chase a move that's already extended in the SAME
			// direction as bias (Z stretched positive while bias is up, or negative while bias is down).
			// Note this is the OPPOSITE polarity from ZScoreState.Aligned(dir), which is GodReversal's own
			// fade-signal check — here we WANT to avoid the extension, not trade the fade.
			bool zExtended = zscore != null && zscore.Extreme
				&& ((bias > 0 && zscore.Z > 0) || (bias < 0 && zscore.Z < 0));
			_zscoreOk = !BlockExtendedZScore || zscore == null || !zExtended;
			_lastZscoreLabel = zscore == null ? "n/a" : "Z=" + zscore.Z.ToString("0.00") + (zscore.Extreme ? " (extreme)" : "");

			// ORDER-FLOW CONVICTION — Drift's CVB bias must agree with the trend bias. Unlike the
			// volume gate (which fails CLOSED), this one is OPTIONAL and fails OPEN: Drift is a BAR
			// TYPE, so demanding it would silently disable this indicator on every chart not using
			// Drift bars. Off by default for the same reason.
			_haveCvb = cvb != null;
			_cvbOk = !RequireDriftConviction || cvb == null
				|| (bias != 0 && cvb.Bias == bias && !cvb.IsDiverging);
			_lastCvbLabel = cvb == null ? "n/a"
				: (cvb.Bias == 0 ? "flat"
					: (cvb.Bias > 0 ? "up" : "down") + " " + (cvb.Conviction * 100.0).ToString("0") + "%"
					  + (cvb.IsDiverging ? " (absorbing)" : ""));

			// ── OPTIONAL VETOES (all fail-open) ──────────────────────────────────────
			// CHOP — StochasticTripleFilter's chop index. This is ORTHOGONAL to the regime
			// gate above: regime vetoes on VOLATILITY, this vetoes on DIRECTIONLESSNESS.
			// Quiet-but-ranging tape passes the vol gate and dies here instead.
			_chopOk = !BlockChop || stf == null || stf.Trending;

			// WEAKENING — Woodies' momentum is fading. Only blocks when the fade is in the
			// direction we'd be trading: entering a trend that is visibly running out.
			_cciOk = !BlockWeakeningCci || cci == null || !cci.Weakening
				|| bias == 0 || cci.Bias != bias;

			// EXHAUSTION / HARMONIC — counter-trend voices. Used here as VETOES rather than
			// triggers: a completed reversal pattern pointing AGAINST the trade is a reason
			// not to take it. As vetoes they can only reduce signals, never add any.
			_exhOk  = !BlockCounterExhaustion || exh  == null || bias == 0 || exh.Dir  == 0 || exh.Dir  == bias;
			_harmOk = !BlockCounterHarmonic   || harm == null || bias == 0 || harm.Dir == 0 || harm.Dir == bias;

			// ── NON-PRICE-DERIVED VETOES — genuinely independent evidence, not another read of
			// this instrument's own OHLC. All fail-open, same contract as everything above. ──
			// INTERMARKET — correlated markets actively leaning against the trade.
			_imktOk = !BlockIntermarketOpposed || imkt == null || bias == 0 || imkt.Lean == 0 || imkt.Lean == bias;

			// EYE — the orthogonal qualifier's own verdict. A neutral read isn't opposition, so this
			// only blocks when Eye has actively qualified the OTHER direction.
			_eyeOk = !RequireEyeNotOpposed || eye == null || bias == 0 || eye.Direction == 0 || eye.Direction == bias;

			_mtfOk = !RequireMtfNotOpposed || mtf == null || bias == 0 || mtf.Bias == 0 || mtf.Bias == bias;

			// FLUX — same "not opposed" shape as MTF above. flux.FlowDir is the just-closed Flux
			// bar's net signed imbalance; a FlowDir opposite the panel's bias is real order flow
			// actively fighting the trade, not just a quiet/neutral reading (FlowDir==0 doesn't
			// block, same "neutral abstains" convention every gate here uses).
			_fluxOk = !RequireFluxNotOpposed || flux == null || bias == 0 || flux.FlowDir == 0 || flux.FlowDir == bias;

			// TFUS — same "not opposed" shape. tfus.Bias only ever carries a direction once its
			// own internal majority agreed, so a nonzero Bias here is already a stronger signal
			// than a single sensor's raw reading.
			_tfusOk = !RequireTrendFusionNotOpposed || tfus == null || bias == 0 || tfus.Bias == 0 || tfus.Bias == bias;
			_archOk = !RequireArchNotOpposed || arch == null || bias == 0 || arch.Signal == 0 || arch.Signal == bias;

			_profOk = !RequireProfileNotOpposed || prof == null || bias == 0 || prof.Signal == 0 || prof.Signal == bias;

			// 2026-08-05 — DATA-SUPPORTED INVERSE of the gate above. grade_sneaky.py's "PROFILE REVERSION
			// SIGNAL" table showed "agrees" as the WORST bucket and "opposes" as the BEST, across every
			// grading run — the opposite of what RequireProfileNotOpposed assumes. That's not a bug in
			// Signal: SneakyProfile's own header documents it as a DELIBERATE mean-reversion voice ("+1
			// stretched below VAL... a MEAN-REVERSION voice, deliberately the opposite sense of a breakout
			// signal"), paired with a panel that's mostly momentum-following. "Signal agrees with bias"
			// means price is ALREADY stretched away from value in the direction you're about to trade —
			// an overextension risk, not confirmation. This is a NEW property, not a repurposed one, so it
			// can be A/B tested against doing nothing rather than silently flipping behavior under the old
			// property's name. Off by default, same as its counterpart, until it's actually validated on
			// fresh data (re-check table 4/14 in grade_sneaky.py after this has recorded a session).
			_profOverextOk = !VetoProfileOverextended || prof == null || bias == 0 || prof.Signal == 0 || prof.Signal != bias;

			// FLOW — CVD-regression CONFIRMED direction (Signal, not the always-on Bias — Signal is
			// already R²/strength-gated, so an incoherent flow abstains here rather than blocking).
			_flowOk = !RequireFlowNotOpposed || flow == null || bias == 0 || flow.Signal == 0 || flow.Signal == bias;

			// PARTICIPATION — a dry-up is a reason to distrust ANY breakout, regardless of direction,
			// so unlike the others this doesn't check bias agreement.
			_partOk = !BlockOnDryUp || part == null || !part.DryUp;

			// STRUCTURE — confirmed swing structure (HH/HL vs LH/LL) opposing the trade. A genuinely
			// different read than Trend/ADX: this comes from swing geometry, not indicator math.
			_structOk = !BlockCounterStructure || strc == null || bias == 0 || strc.Signal == 0 || strc.Signal == bias;

			// CLOCK — the near-close kill window. Direction-agnostic, like dry-up: bad execution
			// conditions block either side equally.
			_clockOk = !BlockInKillWindow || clk == null || !clk.InKillWindow;

			// 2026-08-08 — same "direction-agnostic, fails open when absent" idiom as the kill-window
			// gate just above, reading NewsLockout instead of Clock. Instrument-keyed (SneakyNewsService
			// publishes suite-wide, doesn't need to be on THIS chart) via ActiveNewsLockoutFor directly
			// -- not the broader SneakyCore.CanEnter (also folds in kill-switch/governor/drawdown), for
			// the same reason as Council's own version of this gate.
			string cnfInstName = Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : null;
			var cnfNewsLockout = !string.IsNullOrEmpty(cnfInstName) ? SneakyCore.ActiveNewsLockoutFor(cnfInstName) : null;
			_newsOk = !BlockInNewsLockout || cnfNewsLockout == null;

			// ── BAR-TYPE (BrickState) GATES ─────────────────────────────────────────
			// Direction — honestly redundant with the trend gate on any Renko-family clock,
			// since a brick's direction IS recent price direction. Offered for completeness;
			// expect it to filter very little on its own.
			_brkOk = !RequireBrickAgreement || brick == null || bias == 0 || brick.Aligned(bias);

			// Persistence — a COUNT, not a direction, so this is not redundant. Skips the
			// first brick or two of a turn and waits for the move to prove itself.
			_persistOk = MinConsecutiveBricks <= 0 || brick == null || brick.SameDirCount >= MinConsecutiveBricks;

			// ── EFFORT-ONLY SLOT REINTERPRETATION ───────────────────────────────────
			// SneakyEffort reuses two BrickState slots (documented in its header):
			//     DensityScale    ← efficiency  (1.0 typical · <1 expensive/absorbing · >1 cheap/swept)
			//     PendingBreakout ← the last brick closed on EFFORT, i.e. absorption
			// On Drift/Lattice/TBars those slots carry their ORIGINAL meaning — a real density
			// multiplier and a real mid-confirmation breakout — so reading them as absorption
			// there would be flatly wrong. Hence the Source check: these two gates no-op on
			// every bar type except Effort.
			bool isEffort = brick != null && string.Equals(brick.Source, "SneakyEffort", StringComparison.Ordinal);

			_absorbOk = !BlockOnAbsorption || !isEffort || !brick.PendingBreakout;
			_effOk    = MinBrickEfficiency <= 0.0 || !isEffort || brick.DensityScale >= MinBrickEfficiency;

			_lastBrickLabel = brick == null ? "n/a"
				: (brick.Direction > 0 ? "up" : brick.Direction < 0 ? "down" : "flat")
				  + " x" + brick.SameDirCount
				  + (isEffort ? "  eff " + brick.DensityScale.ToString("0.00")
				                + (brick.PendingBreakout ? " ABSORB" : "") : "");

			if (ShowSetupHelp)
				BuildSetupNotes(trend, adx, stf, cci, exh, harm, env, brick, cvb, double.IsNaN(dom),
								 imkt, eye, flow, part, strc, clk, mtf, prof, flux, tfus, arch);
			else _setupNotes.Clear();

			var gn = new StringBuilder();
			if (!_chopOk) gn.Append("chop ");
			if (!_cciOk)  gn.Append("cci-weak ");
			if (!_exhOk)  gn.Append("exhaustion ");
			if (!_harmOk) gn.Append("harmonic ");
			if (!_brkOk)     gn.Append("brick-dir ");
			if (!_persistOk) gn.Append("brick-run ");
			if (!_absorbOk)  gn.Append("absorption ");
			if (!_effOk)     gn.Append("inefficient ");
			if (!_imktOk)    gn.Append("intermarket ");
			if (!_eyeOk)     gn.Append("eye ");
			if (!_mtfOk)     gn.Append("mtf ");
			if (!_fluxOk)    gn.Append("flux ");
			if (!_tfusOk)    gn.Append("trendfusion ");
			if (!_archOk)    gn.Append("arch ");
			if (!_profOk)    gn.Append("profile ");
			if (!_profOverextOk) gn.Append("profile-overext ");
			if (!_flowOk)    gn.Append("flow ");
			if (!_partOk)    gn.Append("dry-up ");
			if (!_structOk)  gn.Append("structure ");
			if (!_clockOk)   gn.Append("kill-window ");
			if (!_newsOk)    gn.Append("news-lockout ");
			_lastGateNote = gn.ToString().Trim();

			// Stop-placement levels — published for reference, never gates.
			_sarLevel = sar  != null ? sar.Sar   : double.NaN;
			_stLevel  = sprt != null ? sprt.Line : double.NaN;

			// TRIGGER — either a confirmed WAE explosion or a confirmed compression breakout, in the
			// SAME direction as bias. Both are momentum-confirmation triggers, not competing opinions.
			bool triggerLong = false, triggerShort = false;
			string trigLabel = "—";
			// BUGFIX (2026-08-22, P2 finding: RequireSqueezeForCompression): these two track whether
			// the Compression block below is (one of) the source(s) that set triggerLong/triggerShort,
			// so the squeeze gate further down can withdraw ONLY compression's own contribution instead
			// of overwriting triggerLong/triggerShort outright. See the squeeze gate for the full note.
			bool compressionContributedLong = false, compressionContributedShort = false;
			if (UseWaeTrigger && wae != null && wae.Signal != 0)
			{
				if (wae.Signal > 0) { triggerLong = true; trigLabel = "WAE↑"; }
				else                { triggerShort = true; trigLabel = "WAE↓"; }
			}
			if (UseCompressionTrigger && comp != null && comp.Signal != 0)
			{
				if (comp.Signal > 0) { triggerLong = true; compressionContributedLong = true; trigLabel = trigLabel == "—" ? "Compression↑" : trigLabel + " + Compression↑"; }
				else                 { triggerShort = true; compressionContributedShort = true; trigLabel = trigLabel == "—" ? "Compression↓" : trigLabel + " + Compression↓"; }
			}

			// 2026-08-18 — four more trigger sources, same OR-mechanism as WAE/Compression above (any
			// one firing satisfies the trigger requirement; adding these only INCREASES fire frequency,
			// never restricts it — same audition discipline as everything else in this cascade). All
			// off by default; see "2. Trigger" property descriptions. SAR/SuperTrend are State-kind
			// voters that publish their OWN reversal-flip pulse (Flip: ±1 on the flip bar only, else 0
			// — using their continuous Signal/Bias instead would fire on literally every trending bar,
			// which isn't a "trigger" at all). VIDYA/ADXVMA publish no Flip field, so the sign-change of
			// their Signal is edge-detected right here, bar over bar.
			if (UseSarTrigger && sar != null && sar.Flip != 0)
			{
				if (sar.Flip > 0) { triggerLong = true; trigLabel = trigLabel == "—" ? "SAR↑" : trigLabel + " + SAR↑"; }
				else              { triggerShort = true; trigLabel = trigLabel == "—" ? "SAR↓" : trigLabel + " + SAR↓"; }
			}
			if (UseSuperTrendTrigger && sprt != null && sprt.Flip != 0)
			{
				if (sprt.Flip > 0) { triggerLong = true; trigLabel = trigLabel == "—" ? "SuperTrend↑" : trigLabel + " + SuperTrend↑"; }
				else               { triggerShort = true; trigLabel = trigLabel == "—" ? "SuperTrend↓" : trigLabel + " + SuperTrend↓"; }
			}
			bool vdyaFlipUp = false, vdyaFlipDown = false;
			if (vdya != null)
			{
				if (vdya.Signal != 0 && vdya.Signal != _prevVidyaSignal)
				{
					if (vdya.Signal > 0) vdyaFlipUp = true; else vdyaFlipDown = true;
				}
				_prevVidyaSignal = vdya.Signal;
			}
			if (UseVidyaTrigger && vdyaFlipUp)   { triggerLong  = true; trigLabel = trigLabel == "—" ? "VIDYA↑" : trigLabel + " + VIDYA↑"; }
			if (UseVidyaTrigger && vdyaFlipDown) { triggerShort = true; trigLabel = trigLabel == "—" ? "VIDYA↓" : trigLabel + " + VIDYA↓"; }
			bool avmaFlipUp = false, avmaFlipDown = false;
			if (avma != null)
			{
				if (avma.Signal != 0 && avma.Signal != _prevAdxvmaSignal)
				{
					if (avma.Signal > 0) avmaFlipUp = true; else avmaFlipDown = true;
				}
				_prevAdxvmaSignal = avma.Signal;
			}
			if (UseAdxvmaTrigger && avmaFlipUp)   { triggerLong  = true; trigLabel = trigLabel == "—" ? "ADXVMA↑" : trigLabel + " + ADXVMA↑"; }
			if (UseAdxvmaTrigger && avmaFlipDown) { triggerShort = true; trigLabel = trigLabel == "—" ? "ADXVMA↓" : trigLabel + " + ADXVMA↓"; }

			// SQUEEZE GATE — VolEnvelope qualifies the COMPRESSION trigger rather than voting
			// beside it. A compression breakout is only interesting out of a genuinely coiled
			// book; firing one from an already-expanded band is the classic false break. Regime
			// 0 = Squeeze, or bandwidth below the percentile floor, either qualifies.
			// Fails OPEN: no VolEnvelope attached ⇒ the gate does nothing.
			//
			// BUGFIX (2026-08-25, seam audit): env.BandwidthPctile is published as a 0..1 FRACTION
			// (SneakyCore_v1_0_0.cs's EnvelopeState doc comment; computed as BwPercentile() = a ring-
			// buffer rank / count in SneakyVolEnvelope_v0_2_0.cs — genuinely 0..1). MaxBandwidthPctile
			// is a [Range(0,100)] UI slider the user sets as a PERCENTAGE (default 25.0, "25th
			// percentile"). Comparing them directly meant env.BandwidthPctile (max possible 1.0) was
			// being checked against a value like 25.0 — always true for any realistic slider setting,
			// so this half of the OR silently made the whole "coiled" check trivially true and the
			// slider had no effect no matter what the user set it to. Scale BandwidthPctile to a
			// percentage before comparing so the slider's stated 0-100 range actually means something.
			if (RequireSqueezeForCompression && env != null && comp != null && comp.Signal != 0)
			{
				bool coiled = env.IsSqueeze || env.Regime == 0 || (env.BandwidthPctile * 100.0) <= MaxBandwidthPctile;
				if (!coiled)
				{
					// Withdraw ONLY the compression contribution; a WAE explosion on the same
					// bar still stands on its own.
					bool waeLong = false, waeShort = false;
					if (wae != null && wae.IsExploding)
					{
						if (wae.Bias > 0) waeLong = true;
						else if (wae.Bias < 0) waeShort = true;
					}
					// BUGFIX (2026-08-22, P2 finding: RequireSqueezeForCompression): this used to do
					// `triggerLong = waeLong; triggerShort = waeShort;` here — an unconditional OVERWRITE
					// that discarded any SAR/SuperTrend/VIDYA/ADXVMA (and even the plain WAE-signal)
					// trigger that had already fired independently on this same bar, contradicting this
					// very comment's promise to withdraw ONLY the compression contribution. Fix: use
					// compressionContributedLong/Short (set where the Compression block above sets
					// triggerLong/triggerShort true) to clear ONLY compression's own contribution, and
					// only when no other independent source fired in that same direction on this bar.
					bool otherContributedLong = waeLong
						|| (UseWaeTrigger && wae != null && wae.Signal > 0)
						|| (UseSarTrigger && sar != null && sar.Flip > 0)
						|| (UseSuperTrendTrigger && sprt != null && sprt.Flip > 0)
						|| (UseVidyaTrigger && vdyaFlipUp)
						|| (UseAdxvmaTrigger && avmaFlipUp);
					bool otherContributedShort = waeShort
						|| (UseWaeTrigger && wae != null && wae.Signal < 0)
						|| (UseSarTrigger && sar != null && sar.Flip < 0)
						|| (UseSuperTrendTrigger && sprt != null && sprt.Flip < 0)
						|| (UseVidyaTrigger && vdyaFlipDown)
						|| (UseAdxvmaTrigger && avmaFlipDown);
					if (compressionContributedLong  && !otherContributedLong)  triggerLong  = false;
					if (compressionContributedShort && !otherContributedShort) triggerShort = false;
					if (!triggerLong && !triggerShort) trigLabel = "—";
					_lastGateNote = (_lastGateNote + " uncoiled").Trim();
				}
			}
			_lastTriggerLabel = trigLabel;

			bool optOk = _chopOk && _cciOk && _exhOk && _harmOk
					  && _brkOk && _persistOk && _absorbOk && _effOk
					  && _imktOk && _eyeOk && _flowOk && _partOk && _structOk && _clockOk && _mtfOk && _profOk && _profOverextOk && _fluxOk && _tfusOk && _archOk && _newsOk;

			bool longOk  = bias > 0 && _regimeOk && _adxOk && _liqOk && _levelOk && _volOk && _zscoreOk && _cvbOk && optOk && triggerLong;
			bool shortOk = bias < 0 && _regimeOk && _adxOk && _liqOk && _levelOk && _volOk && _zscoreOk && _cvbOk && optOk && triggerShort;

			// OPTIONAL — GodReversal as an extra, independent trigger. Off by default (see header note).
			// When enabled, it can ALSO satisfy longOk/shortOk on its own — but still gated by regime, and
			// still requires bias to not be actively AGAINST it (bias>=0 for a long reversal call, so a
			// flat/undetermined trend doesn't block a fresh reversal, but an active downtrend does). Also
			// still subject to the wall veto and volume/extension gates — a reversal call into a wall or
			// against real order flow is just as much a bad idea as a trend-following one would be.
			// BUGFIX (2026-08-22, P1 finding #1): this block re-checks _regimeOk/_liqOk/_volOk/_zscoreOk/
			// _cvbOk/_chopOk/_cciOk but was missing _clockOk (kill-window veto) and _newsOk (news-lockout
			// veto) — both universal, direction-agnostic safety gates that the main path folds into optOk.
			// Without them, enabling UseGodReversalAsExtra let a qualifying GodReversal read set longOk/
			// shortOk = true directly, bypassing the kill-window and news-lockout vetoes entirely. Added
			// both here so the GodReversal-extra path is held to the same safety floor as the main path.
			if (UseGodReversalAsExtra && grev != null && grev.Signal != 0 && grev.Quality >= MinGodReversalQuality
				&& _regimeOk && _liqOk && _volOk && _zscoreOk && _cvbOk && _chopOk && _cciOk && _clockOk && _newsOk)
			{
				if (grev.Signal > 0 && bias >= 0) { longOk = true; _lastTriggerLabel += (trigLabel == "—" ? "GodReversal↑" : " + GodReversal↑"); }
				if (grev.Signal < 0 && bias <= 0) { shortOk = true; _lastTriggerLabel += (trigLabel == "—" ? "GodReversal↓" : " + GodReversal↓"); }
			}

			// Debounced — fire once on the rising edge, not every bar the condition continues to hold.
			// 2026-08-19 — was Draw.Dot, IDENTICAL shape+color to Council's own marker — indistinguishable
			// circles when both indicators are on one chart. Tried triangles next, but other indicators
			// on the chart already plot triangles/arrows, so shape alone wasn't a reliable tell either.
			// Settled on literal text tags instead: "SSCB"/"SSCS" (buy/sell), same text AND same color
			// as Council's own tags — the two sources are meant to read as one unified "the suite fired"
			// signal here, not as visually distinct sources.
			if (longOk && !_prevLongOk)
			{
				Values[0][0] = BuySignalValue;
				if (DrawSignalTags)
					Draw.Text(this, SignalTagPrefix + "_Buy_" + CurrentBar, "SSCB", 0,
						Low[0] - SignalMarkerOffsetTicks * TickSize, Brushes.Lime);
			}
			if (shortOk && !_prevShortOk)
			{
				Values[1][0] = SellSignalValue;
				if (DrawSignalTags)
					Draw.Text(this, SignalTagPrefix + "_Sell_" + CurrentBar, "SSCS", 0,
						High[0] + SignalMarkerOffsetTicks * TickSize, Brushes.Red);
			}
			_prevLongOk = longOk;
			_prevShortOk = shortOk;

			// Publish the full cascade result — see SneakyCore v1.37.0. Fired/Signal reflect THIS bar's
			// debounced edge (matches Values[0]/[1]); the *Ok flags reflect the gate state regardless of
			// whether a trigger happened to fire, so a grading pass can separate "gates were fine, no
			// trigger yet" from "a specific gate is what's blocking this."
			try
			{
				int firedSignal = (longOk && Values[0][0] != 0.0) ? 1 : (shortOk && Values[1][0] != 0.0) ? -1 : 0;

				// DIAGNOSTIC (2026-08-03) — logs the EXACT bar Confluence itself computes a real fire,
				// straight from the source, before anything downstream (Recorder, the seam, timing) can
				// possibly interfere. Compare this timestamp directly against sneaky.log / the recorder
				// JSONL's own "time" field for a row with cnfFired=true near the same moment. If this
				// fires but no matching recorded row shows fired=true, that proves Recorder is genuinely
				// missing real fires rather than the gates just never clearing.
				if (firedSignal != 0)
				{
					try { SneakyCore.Log("ConfluenceFireDiag", Time[0].ToString("yyyy-MM-dd HH:mm:ss") + "  signal=" + firedSignal + "  bar=" + CurrentBar); }
					catch { }
					// 2026-08-09 — feeds Excursions' OPTIONAL second intake path (NoteSignalFire/
					// DrainSignalFires), added tonight alongside the same wiring for SneakyV1_0 and
					// Bridge. Confluence's own primary verdict is NOT polled directly by
					// ExcursionRecorder the way Council's is -- this is the ONLY path that gets
					// Confluence's own fires into MFE/MAE tracking at all. No extra edge-detection
					// needed here: Values[0]/[1] reset to 0.0 every bar (see above) and are only set
					// on the bar a trigger genuinely fires, so firedSignal is already one-shot.
					try
					{
						double firePx = Close[0];
						SneakyCore.NoteSignalFire(Scope(), firedSignal, "CONFLUENCE", State == State.Historical, firePx, InstanceTag);
					}
					catch (Exception _sx) { SneakyCore.Swallow("SneakyConfluence.NoteSignalFire", _sx); }
				}

				SneakyCore.SetConfluenceState(new SneakyCore.ConfluenceState
				{
					Scope        = Scope(),
					Bartype      = SneakyCore.BarTag(BarsPeriod),
					Instrument   = Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : null,
					InstanceTag  = InstanceTag,
					Bias         = bias,
					RegimeOk     = _regimeOk, AdxOk = _adxOk, LiqOk = _liqOk, VolOk = _volOk,
					ZscoreOk     = _zscoreOk, CvbOk = _cvbOk, OptOk = optOk, LevelOk = _levelOk,
					ImktOk       = _imktOk, EyeOk = _eyeOk, FlowOk = _flowOk, MtfOk = _mtfOk, ProfOk = _profOk, ProfOverextOk = _profOverextOk, ChopOk = _chopOk,
				FluxOk       = _fluxOk,
				TfusOk       = _tfusOk,
				ArchOk       = _archOk,
					PartOk       = _partOk, StructOk = _structOk, ClockOk = _clockOk,
					NewsOk       = _newsOk,
					TriggerLong  = triggerLong, TriggerShort = triggerShort,
					Fired        = firedSignal != 0,
					Signal       = firedSignal,
					BlockedBy    = _lastGateNote,
					TriggerLabel = _lastTriggerLabel,
					Source       = "SneakyConfluence"
				});
				_lastPublishOk = true;
			}
			catch (Exception ex)
			{
				_lastPublishOk = false;
				// 2026-08-19 — was a totally silent `catch { }`. Throttled (not one-shot-forever,
				// like Recorder's file-write failure) since a publish attempt runs fresh every bar
				// and could be transient — but it needs to be visible in the Output tab AT ALL,
				// which it never was before.
				if (unchecked(Environment.TickCount - _lastPublishFailLog) > 15000)
				{
					_lastPublishFailLog = Environment.TickCount;
					try { Print("[SneakyConfluence] publish failed — downstream (Recorder/Cockpit) is reading a STALE seam: " + ex.Message); } catch { }
				}
			}

			// 2026-08-19 — heartbeat: LAST statement of a fully-completed OnBarUpdate pass, on
			// purpose, same reasoning as Council's own (see its comments). If anything above threw
			// uncaught, this line never runs.
			_lastGoodBarAt = DateTime.Now;
		}

		// ── glass card (diagnostic readout — which factors are aligned/blocking right now) ──
		// ═══════════════════════════════════════════════════════════════════════
		//  SETUP HELP — the silent-failure catcher.
		//  Nearly every optional gate here FAILS OPEN: if its sensor isn't on the
		//  chart, the gate quietly does nothing. That is the safe behaviour, but it
		//  is confusing — you can tick "Block Chop", see no change at all, and have
		//  no way to know whether the tape is trending or whether STF was simply
		//  never attached. This names that difference out loud.
		// ═══════════════════════════════════════════════════════════════════════
		private void BuildSetupNotes(object trend, object adx, object stf, object cci,
									 object exh, object harm, object env,
									 SneakyCore.BrickState brick, object cvb, bool volMissing,
									 object imkt, object eye, object flow, object part, object strc, object clk, object mtf, object prof,
									object flux, object tfus, object arch)
		{
			_setupNotes.Clear();

			if (trend == null) _setupNotes.Add("MISSING SneakyTrend — nothing can fire without it");
			if (RequireAdxStrong && adx == null) _setupNotes.Add("MISSING ADXPro — required while 'Require ADX Strong' is on");
			if (RequireVolumeConfirm && volMissing) _setupNotes.Add("MISSING volume pressure — required while 'Require Volume Confirmation' is on");

			if (BlockChop && stf == null)                   _setupNotes.Add("'Block Chop' ON but SneakyStochasticTripleFilter not attached — inactive");
			if (BlockWeakeningCci && cci == null)            _setupNotes.Add("'Block Weakening Momentum' ON but WoodiesCCIPro not attached — inactive");
			if (BlockCounterExhaustion && exh == null)       _setupNotes.Add("'Block Counter-Trend Exhaustion' ON but SneakyExhaustion not attached — inactive");
			if (BlockCounterHarmonic && harm == null)        _setupNotes.Add("'Block Counter-Trend Harmonic' ON but SneakyHarmonic not attached — inactive");
			if (RequireSqueezeForCompression && env == null) _setupNotes.Add("'Require Squeeze' ON but VolEnvelope not attached — inactive");
			if (BlockIntermarketOpposed && imkt == null)
			{
				_setupNotes.Add("'Block Intermarket Opposed' ON but no SneakyIntermarket publishing for this instrument — inactive (remember: instrument-keyed, needs to run somewhere, not necessarily THIS chart)");
				// DIAGNOSTIC (2026-08-02) — peeks past the freshness filter to see WHY imkt read null:
				// genuinely absent (no entry for this instrument at all) vs present-but-stale (an entry
				// exists, just older than StaleSec). Throttled 5s so it doesn't flood the log.
				if (unchecked(Environment.TickCount - _lastImktDiagLog) > 5000)
				{
					_lastImktDiagLog = Environment.TickCount;
					try
					{
						string instName = Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : "?";
						var all = SneakyCore.AllIntermarketStates();
						var mine = all.Find(s => s.Instrument == instName);
						string msg = mine == null
							? "GENUINELY ABSENT — no IntermarketState entry exists for '" + instName + "' at all"
							: "PRESENT BUT STALE — age " + (DateTime.UtcNow - mine.UpdatedUtc).TotalSeconds.ToString("0.0")
							  + "s (StaleSec=" + StaleSec.ToString("0") + "), last Lean=" + mine.Lean;
						SneakyCore.Log("ImktDiag", instName + "  " + msg);
					}
					catch { }
				}
			}
			if (RequireEyeNotOpposed && eye == null)         _setupNotes.Add("'Require Eye Not Opposed' ON but SneakyEye not attached to this chart/bar-type — inactive");
			if (RequireFluxNotOpposed && flux == null)       _setupNotes.Add("'Require Flux Not Opposed' ON but this chart isn't running the SneakyFlux bar type — inactive");
			if (RequireTrendFusionNotOpposed && tfus == null) _setupNotes.Add("'Require Trend Fusion Not Opposed' ON but SneakyTrendFusion not attached to this chart/bar-type — inactive");
			if (RequireArchNotOpposed && arch == null) _setupNotes.Add("'Require Arch Not Opposed' ON but SneakyTrendArchitect not attached to this chart/bar-type — inactive");
			if (RequireMtfNotOpposed && mtf == null)
			{
				_setupNotes.Add("'Require MTF Not Opposed' ON but SneakyMtf not attached to this chart/bar-type — inactive");
				// DIAGNOSTIC (2026-08-04) — compares Confluence's OWN resolved scope (lane included)
				// against whatever scope(s) SneakyMtf actually published under. If Confluence's scope
				// ends "@<lane>" but every published MtfState is missing that suffix, MTF cached its
				// own scope BEFORE this chart's lane registration took effect — a real ordering bug
				// in the lane rollout, not a "not attached" situation.
				if (unchecked(Environment.TickCount - _lastMtfLaneDiagLog) > 5000)
				{
					_lastMtfLaneDiagLog = Environment.TickCount;
					try
					{
						string myScope = Scope();
						var all = SneakyCore.AllMtfStates();
						string found = all.Count == 0 ? "(none published anywhere)"
							: string.Join(" | ", all.ConvertAll(m => m.Scope + " (age " +
								(DateTime.UtcNow - m.UpdatedUtc).TotalSeconds.ToString("0.0") + "s)"));
						SneakyCore.Log("MtfLaneDiag", "Confluence scope='" + myScope + "'  published MTF scopes: " + found);
					}
					catch { }
				}
			}
			if (RequireProfileNotOpposed && prof == null)    _setupNotes.Add("'Require Profile Not Opposed' ON but SneakyProfile not attached to this chart/bar-type — inactive");
			if (VetoProfileOverextended && prof == null)     _setupNotes.Add("'Veto Profile Overextended' ON but SneakyProfile not attached to this chart/bar-type — inactive");
			if (RequireFlowNotOpposed && flow == null)       _setupNotes.Add("'Require Flow Not Opposed' ON but SneakyFlow not attached — inactive");
			if (BlockOnDryUp && part == null)                _setupNotes.Add("'Block On Dry-Up' ON but SneakyParticipation not attached — inactive");
			if (BlockCounterStructure && strc == null)       _setupNotes.Add("'Block Counter-Trend Structure' ON but SneakyStructure not attached — inactive");
			if (BlockInKillWindow && clk == null)            _setupNotes.Add("'Block In Kill Window' ON but SneakyClock not publishing for this instrument — inactive");
			if (BlockInNewsLockout && SneakyCore.ActiveNewsLockouts().Count == 0)
				_setupNotes.Add("'Block In News Lockout' ON but SneakyNewsService hasn't published any active lockouts yet — inactive until it runs");
			if (RequireDriftConviction && cvb == null)
			{
				// Distinguish "wrong bar type" from "right bar type, nothing published yet".
				// Drift's PublishBrickTick is gated:
				//     if (!ReplayMode && (Globals.Now - barTime).TotalMinutes > 5) return;
				// and it only runs from TICK processing. So on a correctly configured Drift
				// chart the seam still reads null whenever the market isn't ticking — closed
				// session, weekend, CME maintenance break. Blaming the bar type there sends
				// you hunting a setting that was never wrong.
				int bpt = 0;
				try { bpt = (int)BarsPeriod.BarsPeriodType; } catch { }
				bool driftBars = bpt == 212204 || bpt == 219204;

				_setupNotes.Add(driftBars
					? "Drift bars loaded but no CVB in the last " + StaleSec.ToString("0") + "s — Drift publishes only on LIVE TICKS within 5 min of now. Normal when the market isn't ticking; gate is inactive meanwhile."
					: "'Require Drift Flow' ON but chart BAR TYPE isn't Drift — gate inactive");
			}

			bool anyBrickGate = RequireBrickAgreement || MinConsecutiveBricks > 0 || BlockOnAbsorption || MinBrickEfficiency > 0.0;
			if (anyBrickGate && brick == null)
				_setupNotes.Add("Bar-type gate ON but no Sneaky bar type loaded — set Data Series > Bar Type");

			if ((BlockOnAbsorption || MinBrickEfficiency > 0.0) && brick != null
				&& !string.Equals(brick.Source, "SneakyEffort", StringComparison.Ordinal))
				_setupNotes.Add("Absorption/Efficiency gates need bar type SneakyEffort (current: " + (brick.Source ?? "?") + ") — inactive");

			if (!_setupPrinted)
			{
				_setupPrinted = true;
				try { Print(SetupGuideText()); } catch { }
			}
		}

		/// <summary>Plain-English setup guide, printed once to the Output window on load.</summary>
		private static string SetupGuideText()
		{
			var b = new StringBuilder();
			b.AppendLine("════════════ SENTINEL CONFLUENCE — SETUP ════════════");
			b.AppendLine("HOW IT DECIDES: every gate must pass, THEN a trigger must fire.");
			b.AppendLine("  bias AND regime AND adx AND wall AND volume AND z-score AND optional AND trigger");
			b.AppendLine("More gates = FEWER signals. Turn them on ONE AT A TIME.");
			b.AppendLine("");
			b.AppendLine("MINIMUM CHART SETUP (add these indicators to the SAME chart):");
			b.AppendLine("  1. SneakyTrend  — REQUIRED. Sets direction. Nothing fires without it.");
			b.AppendLine("  2. ADXPro         — required while 'Require ADX Strong' is on.");
			b.AppendLine("  3. BuySellVolumePressureMountain — required while 'Require Volume Confirmation' is on.");
			b.AppendLine("  4. SneakyWAE and/or CompressionBase — the TRIGGERS. Neither = nothing fires.");
			b.AppendLine("  All other sensors are optional and FAIL OPEN (absent = gate does nothing).");
			b.AppendLine("");
			b.AppendLine("BAR TYPES — set on the CHART (Data Series > Bar Type), never in this dialog.");
			b.AppendLine("  ONE bar type per chart. Each answers a different question:");
			b.AppendLine("   - SneakyTBars   general purpose. Start here.");
			b.AppendLine("   - SneakyDrift   hard to reverse a trend. The ONLY bar type feeding the");
			b.AppendLine("                     'Require Drift Flow (CVB)' gate. Reads real order flow.");
			b.AppendLine("   - SneakyLattice fixed price grid. Use when backtests must be repeatable");
			b.AppendLine("                     (same result no matter when you loaded the chart).");
			b.AppendLine("   - SneakyEffort  measures what a move COST. The ONLY bar type feeding");
			b.AppendLine("                     'Block On Absorption' and 'Min Brick Efficiency'.");
			b.AppendLine("  WARNING Drift draws Heikin-Ashi bodies: CANDLE COLOUR IS NOT BRICK DIRECTION.");
			b.AppendLine("  WARNING switching bar type keeps the OLD type's numbers. Re-check them.");
			b.AppendLine("");
			b.AppendLine("IF NOTHING EVER FIRES — check in this order:");
			b.AppendLine("  1. The card's 'Blocked:' row names the gate that stopped it.");
			b.AppendLine("  2. Any 'SETUP:' line on the card = a MISSING SENSOR, not a market condition.");
			b.AppendLine("  3. Turn every optional gate OFF, confirm signals appear, re-enable one by one.");
			b.AppendLine("═════════════════════════════════════════════════════");
			return b.ToString();
		}

		protected override void OnRender(NinjaTrader.Gui.Chart.ChartControl chartControl, NinjaTrader.Gui.Chart.ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (RenderTarget == null || ChartPanel == null) return;
			if (_sp == null) _sp = new SneakySkin.Painter();
			_sp.Begin(RenderTarget);
			try { if (ShowCard) RenderCard(); }
			catch (Exception ex)
			{
				if (unchecked(Environment.TickCount - _lastRenderExLog) > 15000)
				{
					_lastRenderExLog = Environment.TickCount;
					try { Print("[SneakyConfluence] RenderCard() threw: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); } catch { }
				}
			}
			try { _sp.End(); } catch { }
		}

		private void RenderCard()
		{
			int noteRows = Math.Min(_setupNotes.Count, 3);
			const float cw = 250f;
			// 2026-08-19 — +18f for the heartbeat/publish-health line anchored to the card bottom
			// (see _lastGoodBarAt/_lastPublishOk comments near their declarations).
			float ch = 308f + noteRows * 13f + 18f;
			var slot = SneakySkin.CardLayout.Place(this, ChartPanel,
				ChartPanel.X, ChartPanel.Y, ChartPanel.W, ChartPanel.H, CardCorner, cw, ch);

			var biasCol = _lastBias > 0 ? SneakySkin.CUp : _lastBias < 0 ? SneakySkin.CDown : SneakySkin.CMute;
			var r = _sp.Card(slot.X, slot.Y, cw, ch, biasCol);
			_sp.Dot(r.Left + 5f, r.Top + 8f, biasCol, _prevLongOk || _prevShortOk);
			// 2026-08-19 — width trimmed from -20f to -66f (matches Council/Recorder's own header
			// layout) to leave room for the health pill.
			_sp.Text("CONFLUENCE", r.Left + 16f, r.Top, r.Width - 66f, 16f, SneakySkin.CInk, 11f, true);

			// 2026-08-19 — health pill: NO PUB beats STALE beats LIVE, same precedence and same
			// reasoning as Council's own (see its RenderCard comments).
			double sinceGoodMin = _lastGoodBarAt == DateTime.MinValue
				? double.PositiveInfinity : (DateTime.Now - _lastGoodBarAt).TotalMinutes;
			bool stale = sinceGoodMin > HeartbeatWarnMinutes;
			string hst = !_lastPublishOk ? "NO PUB" : stale ? "STALE" : "LIVE";
			var hstCol = !_lastPublishOk ? SneakySkin.CDown : stale ? SneakySkin.CWarn : SneakySkin.CAccent;
			_sp.Pill(hst, r.Right, r.Top - 1f, hstCol);

			string biasTxt = !_haveTrend ? "Trend: not attached" : _lastBias > 0 ? "Trend: UP" : _lastBias < 0 ? "Trend: DOWN" : "Trend: flat";
			_sp.Text(biasTxt, r.Left, r.Top + 24f, r.Width, 14f, _haveTrend ? SneakySkin.CInk2 : SneakySkin.CWarn, 10.5f);

			string adxTxt = !_haveAdx ? "ADX: not attached" : (_adxOk ? "ADX: aligned + strong" : "ADX: not confirming");
			_sp.Text(adxTxt, r.Left, r.Top + 40f, r.Width, 14f, _adxOk ? SneakySkin.CUp : SneakySkin.CMute, 10.5f);

			_sp.Text("Regime: " + _lastRegimeLabel, r.Left, r.Top + 56f, r.Width, 14f,
				_regimeOk ? SneakySkin.CInk2 : SneakySkin.CWarn, 10.5f);

			_sp.Text("Wall: " + _lastLiqLabel, r.Left, r.Top + 72f, r.Width, 14f,
				_liqOk ? SneakySkin.CInk2 : SneakySkin.CWarn, 10.5f);

			_sp.Text("Level: " + _lastLevelLabel, r.Left, r.Top + 88f, r.Width, 14f,
				_levelOk ? SneakySkin.CInk2 : SneakySkin.CWarn, 10.5f);

			_sp.Text("Volume: " + _lastVolLabel, r.Left, r.Top + 104f, r.Width, 14f,
				_volOk ? SneakySkin.CInk2 : SneakySkin.CWarn, 10.5f);

			_sp.Text("Extension: " + _lastZscoreLabel, r.Left, r.Top + 120f, r.Width, 14f,
				_zscoreOk ? SneakySkin.CInk2 : SneakySkin.CWarn, 10.5f);

			_sp.Text("Flow (Drift): " + _lastCvbLabel, r.Left, r.Top + 136f, r.Width, 14f,
				_cvbOk ? (_haveCvb ? SneakySkin.CInk2 : SneakySkin.CMute) : SneakySkin.CWarn, 10.5f);

			_sp.Text("Trigger: " + _lastTriggerLabel, r.Left, r.Top + 152f, r.Width, 14f, SneakySkin.CInk2, 10.5f);

			_sp.Text("Brick: " + _lastBrickLabel, r.Left, r.Top + 168f, r.Width, 14f,
				(_brkOk && _persistOk && _absorbOk && _effOk) ? SneakySkin.CMute : SneakySkin.CWarn, 10f);

			if (_lastGateNote.Length > 0)
				_sp.Text("Blocked: " + _lastGateNote, r.Left, r.Top + 184f, r.Width, 14f, SneakySkin.CWarn, 10f, true);
			else if (ShowStopLevels)
			{
				string lv = (double.IsNaN(_sarLevel) ? "" : "SAR " + Instrument.MasterInstrument.FormatPrice(_sarLevel))
						  + (double.IsNaN(_stLevel)  ? "" : (double.IsNaN(_sarLevel) ? "" : "   ") + "ST " + Instrument.MasterInstrument.FormatPrice(_stLevel));
				if (lv.Length > 0)
					_sp.Text("Stops: " + lv, r.Left, r.Top + 184f, r.Width, 14f, SneakySkin.CMute, 10f);
			}

			string verdict = _prevLongOk ? "LONG signal this bar" : _prevShortOk ? "SHORT signal this bar" : "waiting";
			_sp.Text(verdict, r.Left, r.Top + 208f, r.Width, 16f,
				_prevLongOk ? SneakySkin.CUp : _prevShortOk ? SneakySkin.CDown : SneakySkin.CMute, 11f, true);

			_sp.Text("requires Trend+ADX attached & publishing; Wall/Level/Volume/Extension optional gates", r.Left, r.Top + 234f, r.Width, 40f, SneakySkin.CMute, 8.5f);

			// Setup warnings last, so a misconfigured chart is the most visible thing here.
			if (ShowSetupHelp && _setupNotes.Count > 0)
				RenderSetupNotes(r, r.Top + 240f);

			// 2026-08-19 — heartbeat line: see _lastGoodBarAt/hst comments above. Anchored to the
			// card's own bottom (ch - 22f) rather than chained off the fixed-line offsets above,
			// so it never collides with the variable-height setup-notes block.
			string agoText = _lastGoodBarAt == DateTime.MinValue ? "no update yet"
				: sinceGoodMin < 1.0 ? "updated just now"
				: sinceGoodMin < 60.0 ? "updated " + sinceGoodMin.ToString("0") + "m ago"
				: "updated " + (sinceGoodMin / 60.0).ToString("0.0") + "h ago";
			string pubText = _lastPublishOk ? "" : "   ·   seam publish failing (see Output tab)";
			_sp.Text(agoText + pubText, r.Left, r.Top + ch - 22f, r.Width, 14f,
				stale || !_lastPublishOk ? SneakySkin.CWarn : SneakySkin.CMute, 9.5f);

			// ── scroll-mode conductor (mirrors Council's) ──
			// The conductor pattern lives wherever it's called from — originally only Council. On charts
			// that run Confluence WITHOUT Council (a supported setup), nothing was driving the scroll
			// rotation's guaranteed render path, so the old invisible-card bug came back for that setup.
			// Confluence renders every bit as reliably as Council (real-time, always-on), so it can serve
			// the same role. Safe to have both present on one chart — DrawSelectedCard is idempotent per
			// frame, worst case is a harmless redundant draw.
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

		[Browsable(false)] [XmlIgnore] public Series<double> BuySignal  => Values[0];
		[Browsable(false)] [XmlIgnore] public Series<double> SellSignal => Values[1];

		private void RenderSetupNotes(SharpDX.RectangleF r, float y)
		{
			for (int i = 0; i < _setupNotes.Count && i < 3; i++)
				_sp.Text("SETUP: " + _setupNotes[i], r.Left, y + i * 13f, r.Width, 13f, SneakySkin.CWarn, 8.5f, true);
		}

		#region Properties

		[NinjaScriptProperty]
		[Display(Name = "Require ADX Strong + Aligned", GroupName = "1. Filters", Order = 0)]
		public bool RequireAdxStrong { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Block Entries In High-Vol Regime", GroupName = "1. Filters", Order = 1)]
		public bool BlockHighVolRegime { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Block Entries In Normal-Vol Regime (2026-08-11, off by default — see SetDefaults comment)",
			GroupName = "1. Filters", Order = 2)]
		public bool BlockNormalVolRegime { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use WAE Breakout As Trigger", GroupName = "2. Trigger", Order = 0)]
		public bool UseWaeTrigger { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Block Near Liquidity Walls", GroupName = "1a. Extra Gates", Order = 0)]
		public bool BlockNearLiquidityWalls { get; set; }

		[NinjaScriptProperty]
		[Range(1, 200)]
		[Display(Name = "Wall Veto Distance (ticks)", GroupName = "1a. Extra Gates", Order = 1)]
		public int WallVetoTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Block Near Structural Level", Description = "Vetoes when SneakyLocation's nearest level (VWAP/PDH/PDL/OR/IB/session H-L) sits in the trade's path within 'Level Veto Distance'. Fails open when SneakyLocation isn't attached.", GroupName = "1a. Extra Gates", Order = 5)]
		public bool BlockNearLevel { get; set; }

		[NinjaScriptProperty]
		[Range(0.05, 5.0)]
		[Display(Name = "Level Veto Distance (ATR)", GroupName = "1a. Extra Gates", Order = 6)]
		public double LevelNearAtr { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Require Volume Pressure Confirmation (BuySellVolumePressureMountain)",
			Description = "Requires SneakyBuySellVolumePressureMountain's DominantSide to agree with the trend bias. This is the ONLY setting for that sensor — there is nothing else to switch on. UNLIKE every gate in groups 1b/1c/1d, this one FAILS CLOSED: if DominantSide reads NaN (sensor not attached, or no classified volume yet) it blocks EVERY signal rather than passing through. If Confluence has gone completely silent, check this first. Reads the PressureState seam, falling back to a direct sub-indicator read for older non-publishing builds.",
			GroupName = "1a. Extra Gates", Order = 2)]
		public bool RequireVolumeConfirm { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Block Entries On Extended Z-Score", GroupName = "1a. Extra Gates", Order = 3)]
		public bool BlockExtendedZScore { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use Compression Breakout As Trigger", GroupName = "2. Trigger", Order = 1)]
		public bool UseCompressionTrigger { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Also Use GodReversal (independent, off by default)", GroupName = "2. Trigger", Order = 2)]
		public bool UseGodReversalAsExtra { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, 1.0)]
		[Display(Name = "Min GodReversal Quality", GroupName = "2. Trigger", Order = 3)]
		public double MinGodReversalQuality { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use Parabolic SAR Flip As Trigger",
			Description = "Fires on SneakyParabolicSAR's own reversal-flip pulse (Flip, not the continuous Signal/Bias) — same OR-mechanism as WAE/Compression above. Classically the chattiest of the four State-kind trend voters below, so if you want more frequency, start here. Fails open (does nothing) when SneakyParabolicSAR isn't attached. Off by default.",
			GroupName = "2. Trigger", Order = 4)]
		public bool UseSarTrigger { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use SuperTrend Flip As Trigger",
			Description = "Fires on SneakySuperTrend's own ATR-band flip pulse (Flip, not the continuous Signal/Bias) — same OR-mechanism as WAE/Compression above. Moderate frequency, a notch calmer than SAR. Fails open when SneakySuperTrend isn't attached. Off by default.",
			GroupName = "2. Trigger", Order = 5)]
		public bool UseSuperTrendTrigger { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use VIDYA Slope Flip As Trigger",
			Description = "Fires when SneakyVIDYA's CMO-modulated Signal changes sign. VidyaState publishes no Flip pulse of its own (unlike SAR/SuperTrend), so the sign-change is edge-detected here, bar over bar. Moderate frequency, adapts speed to trend strength. Fails open when SneakyVIDYA isn't attached. Off by default.",
			GroupName = "2. Trigger", Order = 6)]
		public bool UseVidyaTrigger { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use ADXVMA Flip As Trigger",
			Description = "Fires when SneakyADXVMA's Signal changes sign (edge-detected here, same as VIDYA above — AdxvmaState publishes no Flip pulse either). SneakyADXVMA's own ATR-deadband + hysteresis already exists specifically to SUPPRESS whipsaw, so this is the least likely of the four to meaningfully add frequency — closer in spirit to WAE's dead-zone than a fix for it. Fails open when SneakyADXVMA isn't attached. Off by default.",
			GroupName = "2. Trigger", Order = 7)]
		public bool UseAdxvmaTrigger { get; set; }

		[NinjaScriptProperty]
		[Range(0.5, 120.0)]  // was 0.5-60 — too tight to hold a 90s default (see StaleSec default comment).
							  // A 60s ceiling couldn't clear the ~60s bar-close republish cycle it exists
							  // to tolerate in the first place.
		[Display(Name = "Sensor Staleness Tolerance (sec)", GroupName = "3. Seam", Order = 0)]
		public double StaleSec { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Draw Signal Tags (for automation)", GroupName = "4. Signals", Order = 0)]
		public bool DrawSignalTags { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Signal Tag Prefix", GroupName = "4. Signals", Order = 1)]
		public string SignalTagPrefix { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Instance Tag (Lane)",
			Description = "Optional label, e.g. 'A' / 'B'. Registers a real lane for this chart — every seam on THIS chart (this Confluence, and every sensor it or anything else on this chart reads from) resolves to a separate, lane-qualified scope automatically, so two charts sharing the same instrument+bar-type genuinely stop colliding. Leave blank if you only run one Confluence per scope — unchanged behavior either way.",
			GroupName = "4. Signals", Order = 2)]
		public string InstanceTag { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Signal Marker Offset (ticks)", GroupName = "4. Signals", Order = 2)]
		public int SignalMarkerOffsetTicks { get; set; }

		[Display(Name = "Require Drift Flow Confirmation (CVB)",
			Description = "Requires SneakyDrift's flow-confirmed conviction bias to agree with the trend bias, and to not be absorbing against it. Needs SneakyDrift as the chart's BAR TYPE — fails OPEN when absent, so it is safe to leave on.",
			GroupName = "1a. Extra Gates", Order = 4)]
		public bool RequireDriftConviction { get; set; }

		[Display(Name = "Block Chop (STF chop index)",
			Description = "Vetoes entries when StochasticTripleFilter reports a non-trending tape. ORTHOGONAL to the regime gate: regime vetoes on VOLATILITY, this vetoes on DIRECTIONLESSNESS. Fails open when STF isn't attached.",
			GroupName = "1b. Optional Vetoes", Order = 0)]
		public bool BlockChop { get; set; }

		[Display(Name = "Block Weakening Momentum (Woodies CCI)",
			Description = "Vetoes when WoodiesCCIPro flags the trend as weakening AND that fade is in the direction being traded. Fails open when the sensor isn't attached.",
			GroupName = "1b. Optional Vetoes", Order = 1)]
		public bool BlockWeakeningCci { get; set; }

		[Display(Name = "Block On Counter-Trend Exhaustion",
			Description = "Vetoes when SneakyExhaustion points AGAINST the trend bias. Does NOT apply to the GodReversal extra trigger, where a counter-trend read is the premise rather than a contradiction.",
			GroupName = "1b. Optional Vetoes", Order = 2)]
		public bool BlockCounterExhaustion { get; set; }

		[Display(Name = "Block On Counter-Trend Harmonic",
			Description = "Vetoes when a completed SneakyHarmonic XABCD pattern points AGAINST the trend bias. Same GodReversal exemption as above.",
			GroupName = "1b. Optional Vetoes", Order = 3)]
		public bool BlockCounterHarmonic { get; set; }

		[Display(Name = "Block Intermarket Opposed",
			Description = "Vetoes when SneakyIntermarket's net correlated-market lean actively OPPOSES the trend bias (neutral does not block). INSTRUMENT-keyed — the publishing SneakyIntermarket instance doesn't need to be on THIS chart, just running somewhere on this instrument. Fails open when absent.",
			GroupName = "1b. Optional Vetoes", Order = 4)]
		public bool BlockIntermarketOpposed { get; set; }

		[Display(Name = "Require Eye Not Opposed",
			Description = "Vetoes when SneakyEye's orthogonal verdict actively qualifies the OPPOSITE direction (neutral/not-qualified does not block). Fails open when Eye isn't attached to this chart/bar-type.",
			GroupName = "1b. Optional Vetoes", Order = 5)]
		public bool RequireEyeNotOpposed { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Require MTF Not Opposed",
			Description = "Vetoes when SneakyMtf's higher-timeframe consensus actively leans the OPPOSITE direction (neutral/mixed does not block). Fails open when SneakyMtf isn't attached to this chart/bar-type.",
			GroupName = "1b. Optional Vetoes", Order = 10)]
		public bool RequireMtfNotOpposed { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Require Flux Not Opposed",
			Description = "2026-08-06 — new. Vetoes when the SneakyFlux bar type's just-closed net signed order-flow imbalance (FlowDir) actively opposes the panel's bias. Neutral/flat flow does not block. Only exists on a chart running the SneakyFlux bar type — fails open everywhere else.",
			GroupName = "1b. Optional Vetoes", Order = 14)]
		public bool RequireFluxNotOpposed { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Require Trend Fusion Not Opposed",
			Description = "2026-08-06 — new. Vetoes when SneakyTrendFusion's Bias (a majority-vote composite of MFI/CCI/CVD/Hurst regime/KAMA fan — NOT a PRISM port, see that file's header) actively opposes the panel's bias. Neutral (no majority reached) does not block. Fails open when SneakyTrendFusion isn't attached.",
			GroupName = "1b. Optional Vetoes", Order = 15)]
		public bool RequireTrendFusionNotOpposed { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Require Arch Not Opposed",
			Description = "2026-08-07 — new. Vetoes when SneakyTrendArchitect's Signal (the REAL ported PRISM/Trend Architect — see that file's own header for full attribution) actively opposes the panel's bias. Neutral does not block. Fails open when SneakyTrendArchitect isn't attached.",
			GroupName = "1b. Optional Vetoes", Order = 16)]
		public bool RequireArchNotOpposed { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Require Profile Not Opposed",
			Description = "Vetoes when SneakyProfile's POC-reversion Signal actively leans the OPPOSITE direction (neutral/inside-value does not block). Fails open when SneakyProfile isn't attached.",
			GroupName = "1b. Optional Vetoes", Order = 11)]
		public bool RequireProfileNotOpposed { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Veto Profile Overextended",
			Description = "2026-08-05 — the data-supported INVERSE of 'Require Profile Not Opposed'. Vetoes when SneakyProfile's POC-reversion Signal AGREES with the trade direction — i.e. price is already stretched away from value in the direction you're about to trade. grade_sneaky.py's PROFILE REVERSION SIGNAL table showed 'agrees' as the worst bucket and 'opposes' as the best, consistent with Signal being a deliberate mean-reversion voice paired with a mostly-momentum panel. A NEW property, not a repurposing of the one above, specifically so you can A/B this against doing nothing. Fails open when SneakyProfile isn't attached. Off by default — validate on fresh recorded data before relying on it.",
			GroupName = "1b. Optional Vetoes", Order = 12)]
		public bool VetoProfileOverextended { get; set; }

		[Display(Name = "Require Flow Not Opposed",
			Description = "Vetoes when SneakyFlow's CONFIRMED (R²/strength-gated) CVD-regression direction opposes the trend bias. Uses Signal, not the always-on Bias, so an incoherent flow reading abstains rather than blocking. Fails open when Flow isn't attached.",
			GroupName = "1b. Optional Vetoes", Order = 6)]
		public bool RequireFlowNotOpposed { get; set; }

		[Display(Name = "Block On Dry-Up (Participation)",
			Description = "Vetoes ANY direction when SneakyParticipation flags a volume dry-up — a breakout without participation behind it, regardless of which way it's pointing. Fails open when Participation isn't attached.",
			GroupName = "1b. Optional Vetoes", Order = 7)]
		public bool BlockOnDryUp { get; set; }

		[Display(Name = "Block Counter-Trend Structure",
			Description = "Vetoes when SneakyStructure's confirmed swing structure (HH/HL vs LH/LL) opposes the trend bias. A genuinely different read than Trend/ADX — derived from swing geometry, not indicator math. Fails open when Structure isn't attached.",
			GroupName = "1b. Optional Vetoes", Order = 8)]
		public bool BlockCounterStructure { get; set; }

		[Display(Name = "Block In Kill Window",
			Description = "Vetoes ANY direction when SneakyClock reports the near-close kill window, regardless of which side you'd be trading. INSTRUMENT-keyed, same as Intermarket. Fails open when Clock isn't publishing for this instrument.",
			GroupName = "1b. Optional Vetoes", Order = 9)]
		public bool BlockInKillWindow { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Block In News Lockout",
			Description = "2026-08-08 — new. Vetoes ANY direction when SneakyNewsService reports an active high-impact economic-calendar lockout for this instrument (FOMC/NFP/CPI etc.), regardless of which side you'd be trading. Suite-wide published (not chart-gated) via SneakyCore.ActiveNewsLockoutFor. Fails open when SneakyNewsService isn't running. Off by default until validated.",
			GroupName = "1b. Optional Vetoes", Order = 13)]
		public bool BlockInNewsLockout { get; set; }

		[Display(Name = "Require Squeeze For Compression Trigger",
			Description = "Qualifies the COMPRESSION trigger with VolEnvelope: the breakout only counts out of a genuinely coiled book (Regime=Squeeze, or bandwidth percentile at/below the floor below). A WAE explosion on the same bar still stands on its own. Fails open without VolEnvelope.",
			GroupName = "1c. Squeeze Gate", Order = 0)]
		public bool RequireSqueezeForCompression { get; set; }

		[Range(0.0, 100.0)]
		[Display(Name = "Max Bandwidth Percentile (coiled)",
			Description = "Bandwidth percentile at or below which the book counts as coiled. Lower = stricter.",
			GroupName = "1c. Squeeze Gate", Order = 1)]
		public double MaxBandwidthPctile { get; set; }

		[Display(Name = "Require Brick Agreement (bar type)",
			Description = "Requires the bar type's last brick direction to agree with the trend bias. Honestly redundant with the trend gate on any Renko-family clock — a brick's direction IS recent price direction — so expect little filtering. Fails open on a non-Sneaky bar type.",
			GroupName = "1d. Bar-Type Gates", Order = 0)]
		public bool RequireBrickAgreement { get; set; }

		[Range(0, 50)]
		[Display(Name = "Min Consecutive Bricks (0 = off)",
			Description = "Requires at least N consecutive same-direction bricks. A COUNT rather than a direction, so unlike brick agreement this is NOT redundant — it skips the first bricks of a turn and waits for the move to prove itself.",
			GroupName = "1d. Bar-Type Gates", Order = 1)]
		public int MinConsecutiveBricks { get; set; }

		[Display(Name = "Block On Absorption (SneakyEffort only)",
			Description = "Blocks when Effort's last brick closed on EFFORT rather than displacement — i.e. size was resting and the movement was paid for but not delivered. NO-OPS on every other bar type, because Drift/Lattice/TBars use that same slot for a genuine mid-confirmation breakout flag.",
			GroupName = "1d. Bar-Type Gates", Order = 2)]
		public bool BlockOnAbsorption { get; set; }

		[Range(0.0, 5.0)]
		[Display(Name = "Min Brick Efficiency (0 = off, Effort only)",
			Description = "Effort publishes efficiency in the DensityScale slot: 1.0 typical, <1 expensive/absorbing, >1 cheap/swept. Blocks below this floor — e.g. 0.80 means don't enter into movement costing 25% more than normal. NO-OPS on every other bar type, where DensityScale is a real density multiplier.",
			GroupName = "1d. Bar-Type Gates", Order = 3)]
		public double MinBrickEfficiency { get; set; }

		[Display(Name = "Show Setup Help (recommended ON)",
			Description = "Prints a plain-English setup guide to the Output window on load, and shows a SETUP line on the card whenever a gate is switched on but its sensor isn't attached. Because optional gates FAIL OPEN, without this a missing sensor looks exactly like a calm market. Leave on until the chart is configured.",
			GroupName = "5. Display", Order = 8)]
		public bool ShowSetupHelp { get; set; }

		[Display(Name = "Show Stop Levels (SAR / SuperTrend)",
			Description = "Shows ParabolicSAR's stop price and SuperTrend's line on the card. Reference only — these are real price levels for stop placement and are never used as gates.",
			GroupName = "5. Display", Order = 9)]
		public bool ShowStopLevels { get; set; }

		// NOT [NinjaScriptProperty] on purpose: this file ships a NinjaScript generated-code
		// region, and adding constructor parameters would put that region out of sync. A plain
		// [Display] property still shows in the indicator dialog and still serializes with the
		// chart — it simply isn't a constructor argument, which nothing here needs it to be.
		[Range(-1.0, 1.0)]
		[Display(Name = "Buy Signal Value (+1)",
			Description = "Value written to the BuySignal plot on a firing bar. +1.0 matches both this suite and PredatorX's LongEntryPlotValue=1.",
			GroupName = "4. Signals", Order = 3)]
		public double BuySignalValue { get; set; }

		[Range(-1.0, 1.0)]
		[Display(Name = "Sell Signal Value (+1 suite / -1 PredatorX)",
			Description = "Value written to the SellSignal plot on a firing bar. This suite's own convention is +1.0 on BOTH sides. Set -1.0 to match PredatorX's ShortEntryPlotValue=-1, the convention the Nebula/TMO plots use.",
			GroupName = "4. Signals", Order = 4)]
		public double SellSignalValue { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Card", GroupName = "Sneaky", Order = 0)]
		public bool ShowCard { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Card Corner", GroupName = "Sneaky", Order = 1)]
		public SneakyCardCorner CardCorner { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Indicator Label", GroupName = "Sneaky", Order = 2)]
		public bool ShowIndicatorLabel { get; set; }

		#endregion
	}
}
