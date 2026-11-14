// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
//
// Copyright (c) 2026 silentsudo-io and the Sentinel Suite contributors.
// ─────────────────────────────────────────────────────────────────────────────
//
// ═════════════════════════════════════════════════════════════════════════════
//  SNEAKY EYE v1.1.0 — ORTHOGONAL-EVIDENCE QUALIFIER (re-sourced)
//
//  ⚠ THIS IS **NOT** A RECONSTRUCTION OF SentinelEye. READ THIS FIRST.
//  SentinelEye (Eye_v1_1_0.cs) is the author's adaptive GodTrades scanner. It is
//  unpublished, and nothing about its internals is inferable from the seam alone.
//  What IS published is the CONTRACT — EyeVerdict — plus an explicit invitation
//  in SENTINEL_SUITE_MANUAL §173:
//
//      "Independent evolution — you can rewrite the Eye's internals completely;
//       as long as it still publishes an EyeVerdict, nothing downstream cares."
//
//  So this is a DIFFERENT qualifier speaking the same contract. It does not claim
//  his method, does not approximate it, and will not agree with it. If you later
//  obtain the real SentinelEye, run that instead — both publish EyeVerdict, so
//  nothing downstream needs changing.
//
//  ── 2026-08-05 — WHY v1.0.0 WASN'T ACTUALLY ORTHOGONAL, AND WHAT CHANGED ────
//  v1.0.0's four sources were PRS, IMKT, CVB, LIQ. The problem, found by grading
//  recorded data against grade_sneaky.py's own "EYE vs COUNCIL" orthogonality
//  test (table 4): "Eye AGREES" scored BELOW base rate, "Eye neutral" scored
//  ABOVE it — the opposite of what an orthogonal qualifier should show. Root
//  cause, confirmed by reading SneakyCouncil_v1_0_0.cs directly: THREE of those
//  four sources — PRS, IMKT, CVB — are ALSO independently cast as Council votes
//  (AddVote("PRS",...), AddVote("IMKT",...), AddVote("CVB",...)). "Eye agrees"
//  mostly meant "three votes that were already counted once in Council happen to
//  agree with each other a second time" — not corroboration, the exact echo
//  problem this indicator exists to avoid. (CVB was additionally near-dead in
//  practice: SENSOR COVERAGE in every grading run tonight showed Drift CVB at
//  0.0% — SneakyDrift wasn't even attached/publishing, so v1.0.0 was really
//  running on PRS+IMKT+LIQ most of the time, not four sources.)
//
//  v1.1.0 replaces PRS/IMKT/CVB with CvdState (SneakyCore's session cumulative
//  volume-delta seam) — NOT the same thing as CVB (Drift's bar-type-specific
//  conviction read): CVD is computed by a separate indicator, is genuinely
//  session-scale, and — per SneakyCore's own doc comment on CvdState — is
//  "genuinely orthogonal to every price-derived voter in the Council." Nothing
//  in Council or Confluence reads CvdState anywhere; it's unused evidence, not
//  a re-vote. Two independent reads come out of the one seam:
//      CVD-DIR    the slope of net signed delta (Dir) — real net aggression,
//                 not price
//      CVD-DIVERG divergence between that flow and price (Divergence) — a
//                 distinct "is price winning against real flow, or is real
//                 flow being ignored" tell, not just the same number twice
//  LIQ is unchanged — it was already genuinely unique (no Council vote reads
//  the order book), so there was nothing to fix there.
//
//  DEPENDENCY, STATED PLAINLY: this needs SneakyCVD attached and publishing on
//  THIS SAME CHART (chart-specific scope, same as every indicator in this suite
//  — deliberately never shared across D/E/F). As of SneakyCVD v1.1.0 (2026-08-06,
//  ported from the real Sentinel Suite's own implementation) it has its own
//  independent quote-rule tape classifier and no longer depends on any Pressure
//  publisher — but you still need one SneakyCVD instance per chart, same as
//  before. (v1.0.0 did depend on PRS too; if you're still on that version, see
//  its own header.) If no SneakyCVD is publishing on this chart, GetCvdState
//  returns null and this source abstains — same honest degrade-and-report
//  behavior this file has always had for a missing source, never a silent
//  failure. Check SENSOR COVERAGE in grade_sneaky.py's output after recording a
//  session on this version to confirm CVD is actually
//  present, the same way you'd check any other sensor's attach status.
//
//  ── WHAT THIS ONE DOES, AND WHY (unchanged from v1.0.0) ─────────────────────
//  The manual states the Eye's PURPOSE plainly (§95):
//
//      "the other three are all price-derived, so they tend to nod along with
//       each other. The Eye is your qualifier — it's what makes the opening card
//       more than three echoes."
//
//  That is the gap this fills. Trend, ADX, CCI, Structure, SuperTrend, SAR,
//  ADXVMA, VIDYA and STF all read the SAME OHLC stream through different filters.
//  Agreement among them is one witness repeating itself, not corroboration.
//
//  So this qualifier uses ONLY evidence NOT derived from this instrument's price,
//  AND not already cast as a separate Council vote:
//      CVD-DIR     net signed volume-delta slope   (real aggression, not price)
//      CVD-DIVERG  flow/price divergence            (is price winning against flow?)
//      LIQ         resting size in the path          (the book)
//
//  It is NOT a Council. The Council fuses everything and is numerically dominated
//  by the price bloc. This asks a narrower question — "does the non-price,
//  non-double-counted evidence qualify a direction?" — and answers it alone. Run
//  BOTH: agreement is genuine corroboration; disagreement means the price bloc
//  is out ahead of the tape, which is itself worth knowing.
//
//  ── HONEST STATUS ───────────────────────────────────────────────────────────
//  Weights and thresholds are REASONED, NOT BACKTESTED — same caveat as v1.0.0,
//  now applied to a different evidence set that hasn't been graded yet at all.
//  CVD needs its publisher attached AND live ticks (TickBacked); LIQ needs L2.
//  The qualifier degrades to whatever is present and REPORTS how much evidence
//  it actually had. Read Score as "how much non-price, non-redundant evidence
//  agreed", never as a probability. Re-run grade_sneaky.py's table 4 after
//  recording fresh data on this version — that's the real test of whether this
//  fix worked, not this comment.
//
//  ── 2026-09-15 — PER-SOURCE EVIDENCE PUBLISHED (diagnosing the Eye-vs-Council inversion) ──
//  grade_sneaky.py's table 4 kept showing "Eye AGREES" scoring WORSE than "Eye neutral" even
//  on this v1.1.0 evidence set (reproduced on two independent slices) -- so the v1.0.0 echo
//  problem this file was rewritten to fix isn't the explanation this time. Traced CVD-DIR and
//  CVD-DIVERG's actual math directly: Divergence's sign is, by construction in SneakyCVD, ALWAYS
//  identical to Dir's sign whenever it fires (it publishes dir's own sign, gated on dir/price
//  disagreeing) -- so despite this header's claim above that it's "not just the same number
//  twice," it structurally can only ever REINFORCE CVD-DIR, never oppose it, specifically on the
//  bars where price and flow are fighting hardest. That's a real, deliberate "bullish/bearish
//  divergence predicts reversal" bet (documented in CvdState's own contract) -- and this project's
//  OTHER independent findings (REGIME=normal as the worst bucket, Profile's mean-reversion Signal
//  as the worst "agrees" bucket) all say reversal/contrarian bets lose on this corpus. Plausible
//  mechanism, NOT yet proven -- EyeVerdict never published which source actually drove a given
//  verdict, so the existing recorded corpus can't be split to test it. Fixed the visibility gap:
//  EyeVerdict now also carries CvdDir/CvdDivergence/CvdTickBacked/LiqBlock/Evidence (raw, honest-
//  null snapshots of the exact values already computed below -- zero change to qualification
//  logic itself). SneakyRecorder wired to capture them. Once fresh data accumulates, the actual
//  test is: does "Eye agrees, Divergence!=0" score worse than "Eye agrees, Divergence==0"? That
//  tells us whether CVD-DIVERG specifically is the drag, not just CVD-DIR.
//
//  Places no orders.
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
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.AddOns.Sneaky;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors
{
	public class SneakyEye_v1_1_0 : Indicator, ISneakyScrollCard
	{
		private string _scope;
		private string Scope()
		{
			try { _scope = SneakyCore.ScopeOf(Instrument, BarsPeriod, ChartControl); } catch { }   // 2026-08-04 — ALWAYS recomputed, never permanently cached: a chart's lane can register AFTER this indicator's first call, so caching once risked freezing a stale, unlaned scope forever (confirmed happening for real tonight). Cheap to recompute every call; only overwrites _scope on success, so a transient throw keeps the previous good value.
			return _scope;
		}

		private int    _dir;
		private double _score;
		private double _evidence;
		private bool   _thin, _opposed;
		private readonly List<KeyValuePair<string,int>> _chips = new List<KeyValuePair<string,int>>();

		private SneakySkin.Painter _sp;
		private DateTime _lastRenderExLog = DateTime.MinValue;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "ORTHOGONAL-EVIDENCE QUALIFIER — publishes EyeVerdict. NOT a reconstruction of "
					+ "SentinelEye (the author's unpublished GodTrades scanner); this is a different qualifier "
					+ "speaking the same contract, which SENTINEL_SUITE_MANUAL §173 explicitly permits. v1.1.0 uses "
					+ "ONLY evidence not derived from this instrument's own price AND not already cast as a separate "
					+ "Council vote — CVD slope (net signed volume-delta direction), CVD/price divergence, liquidity "
					+ "walls — because every other sensor reads the same OHLC stream (and v1.0.0's PRS/IMKT/CVB "
					+ "sources turned out to double-count Council's own separate votes on those same three things). "
					+ "Run alongside the Council: agreement is corroboration, disagreement means the price bloc is "
					+ "ahead of the tape. Weights are reasoned, not backtested. Places no orders.";
				Name                     = "Sneaky Eye v1.1.0";
				Calculate                = Calculate.OnBarClose;
				IsOverlay                = true;
				DisplayInDataBox         = true;
				PaintPriceMarkers        = false;
				IsSuspendedWhileInactive = false;
				IsAutoScale              = false;
				ShowIndicatorLabel       = false;

				StaleSec               = 90.0;  // was 5.0 — too tight for scope-keyed sources like Intermarket,
												 // which only republish once per bar close (~60s on a 1-min
												 // chart). 90s gives margin above that cycle without
												 // approaching the 120s hard ceiling on this property.
				QualifyThreshold       = 0.20;   // 2026-08-28 — lowered from 0.30: the score-band curve showed
												 // expectancy rising well before 0.30, and every captured trade
												 // so far scored under 0.25 |score| — at 0.30 the "Endorsed"
												 // category was structurally empty (eyeAligned could never be
												 // true). NOTE: this only sets the default for a NEW SneakyEye
												 // instance — existing chart instances keep whatever value is
												 // already set in their own Properties panel and must be
												 // changed there by hand.
				MinEvidence            = 0.40;
				WallVetoTicks          = 8;

				WeightCvdDir           = 1.0;
				WeightCvdDivergence    = 0.8;
				WeightLiquidity        = 0.6;

				RequirePriceNotOpposed = true;
				PublishVerdict         = true;

				ShowCard   = true;
				CardCorner = SneakyCardCorner.TopLeft;

				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "EyeDirection");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "EyeScore");
			}
			else if (State == State.DataLoaded)
			{
				if (!ShowIndicatorLabel) Name = string.Empty;
			}
			else if (State == State.Terminated)
			{
				if (_sp != null) { try { _sp.Dispose(); } catch { } _sp = null; }
				try { SneakySkin.CardLayout.Release(this); } catch { }
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 2) return;

			string sc = Scope();
			_chips.Clear();

			SneakyCore.CvdState         cvd   = null;
			SneakyCore.LiquidityState   liq   = null;
			SneakyCore.TrendState       trend = null;

			// CVD stays lane-qualified/chart-specific (2026-08-06 — reverted from a brief
			// instrument-level experiment; this project keeps every indicator chart-specific
			// on purpose, so D/E/F can each be tested as genuinely separate setups). Eye's own
			// `sc` already matches SneakyCVD's publish key exactly — no special-case needed.
			try { cvd   = SneakyCore.GetCvdState(sc, StaleSec); }        catch { }
			try { liq   = SneakyCore.GetLiquidityState(sc, StaleSec); }  catch { }
			try { trend = SneakyCore.GetTrendState(sc, StaleSec); }      catch { }

			double num = 0.0, have = 0.0, declared = 0.0;

			// v1.46.0 -- raw per-source evidence, captured alongside the fused num/have/declared math
			// below purely so the verdict can be PUBLISHED with a breakdown (see EyeVerdict.CvdDir/
			// CvdDivergence/CvdTickBacked/LiqBlock in SneakyCore). Zero effect on qualification logic --
			// these are read-only snapshots of the same values already used just below.
			int  pubCvdDir = 0, pubCvdDivergence = 0, pubLiqBlock = 0;
			bool pubCvdTickBacked = false;

			// ── CVD-DIR — real net signed volume-delta slope. Genuinely orthogonal: computed
			//    by a separate indicator (SneakyCVD, session-scale), and unlike PRS/IMKT/CVB in
			//    v1.0.0, nothing in Council or Confluence reads this seam at all — no double-count.
			//    Confidence-scaled by |SlopeZ| (how far the slope is from noise) and halved when
			//    not tick-backed, mirroring how v1.0.0 scaled PRS by strength/TickBacked.
			declared += WeightCvdDir;
			if (cvd != null)
			{
				double conf = Math.Max(0.2, Math.Min(1.0, Math.Abs(cvd.SlopeZ) / 2.0));
				num  += cvd.Dir * WeightCvdDir * conf * (cvd.TickBacked ? 1.0 : 0.5);
				have += WeightCvdDir;
				_chips.Add(new KeyValuePair<string,int>("CVD", cvd.Dir));
				pubCvdDir = cvd.Dir; pubCvdTickBacked = cvd.TickBacked;
			}
			else _chips.Add(new KeyValuePair<string,int>("CVD", 0));

			// ── CVD-DIVERG — flow/price divergence: is real net aggression fighting price, or
			//    riding along with it? A DIFFERENT read than raw direction above, from the same
			//    seam — not the same evidence counted twice. +1 bullish (price down, flow up),
			//    -1 bearish (price up, flow down), 0 none. Damped when the tape isn't real
			//    (TickBacked false), same reasoning as CVD-DIR.
			declared += WeightCvdDivergence;
			if (cvd != null)
			{
				num  += cvd.Divergence * WeightCvdDivergence * (cvd.TickBacked ? 1.0 : 0.5);
				have += WeightCvdDivergence;
				_chips.Add(new KeyValuePair<string,int>("DIVERG", cvd.Divergence));
				pubCvdDivergence = cvd.Divergence; pubCvdTickBacked = cvd.TickBacked;
			}
			else _chips.Add(new KeyValuePair<string,int>("DIVERG", 0));

			// ── LIQ — the book. A wall in the path is evidence AGAINST that side, so this
			//    contributes negatively rather than picking a direction of its own. (Unchanged
			//    from v1.0.0 — already genuinely unique, no Council vote reads the order book.)
			declared += WeightLiquidity;
			if (liq != null)
			{
				have += WeightLiquidity;
				int block = 0;
				try
				{
					bool up = liq.BlocksEntry(1, WallVetoTicks);
					bool dn = liq.BlocksEntry(-1, WallVetoTicks);
					if (up && !dn) block = -1;
					else if (dn && !up) block =  1;
				}
				catch { }
				num += block * WeightLiquidity;
				_chips.Add(new KeyValuePair<string,int>("LIQ", block));
				pubLiqBlock = block;
			}
			else _chips.Add(new KeyValuePair<string,int>("LIQ", 0));

			_evidence = declared > 0.0 ? have / declared : 0.0;
			_score    = declared > 0.0 ? num  / declared : 0.0;

			int raw = _score >  QualifyThreshold ?  1
					: _score < -QualifyThreshold ? -1 : 0;

			// The ONE price-derived input, used strictly as a VETO and never as evidence.
			// A qualification the trend flatly contradicts isn't a qualification — but
			// letting trend CONTRIBUTE would defeat the purpose of an orthogonal tool.
			_opposed = RequirePriceNotOpposed && trend != null && raw != 0
					&& trend.Direction != 0 && trend.Direction != raw;

			// Guards against a confident-looking verdict built on one surviving source:
			// with only pressure attached, 100% agreement is one witness, not four.
			_thin = _evidence < MinEvidence;

			_dir = (_opposed || _thin) ? 0 : raw;

			Values[0][0] = _dir;
			Values[1][0] = _score;

			if (PublishVerdict && !string.IsNullOrEmpty(sc))
			{
				try
				{
					// SCOPE-keyed per the Core contract: "the Eye's simulated rows are
					// chart-specific, so two charts on one instrument must not overwrite
					// each other." Source names THIS implementation so any consumer can
					// tell whose Eye produced the verdict.
					SneakyCore.SetEyeVerdict(sc, SneakyCore.BarTag(BarsPeriod),
						Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : null,
						_dir, _score, "SneakyEye/orthogonal",
						pubCvdDir, pubCvdDivergence, pubCvdTickBacked, pubLiqBlock, _evidence);
				}
				catch (Exception ex) { try { Print("[SneakyEye] publish failed: " + ex.Message); } catch { } }
			}
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (RenderTarget == null || ChartPanel == null) return;
			if (_sp == null) _sp = new SneakySkin.Painter();
			_sp.Begin(RenderTarget);
			try { if (ShowCard) RenderCard(); }
			catch (Exception ex)
			{
				if ((DateTime.UtcNow - _lastRenderExLog).TotalMilliseconds > 15000)
				{
					_lastRenderExLog = DateTime.UtcNow;
					try { Print("[SneakyEye] RenderCard threw: " + ex.Message); } catch { }
				}
			}
			try { _sp.End(); } catch { }
		}
        public void ConductorRender(SharpDX.Direct2D1.RenderTarget rt)
        {
            if (!(ShowCard)) return;
            if (rt == null) return;
            try
            {
                if (_sp == null) _sp = new SneakySkin.Painter();
                _sp.Begin(rt);
                RenderCard();
            }
            catch { }
            finally { try { _sp.End(); } catch { } }
        }


		private void RenderCard()
		{
			const float cw = 250f;
			const float ch = 206f;

			SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
			var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
				ChartControl.ChartPanels[0].X, ChartControl.ChartPanels[0].Y, ChartControl.ChartPanels[0].W, ChartControl.ChartPanels[0].H, cw, ch);
			if (slot.Width <= 0f) return;   // not the currently-selected card in the rotation

			var col = _dir > 0 ? SneakySkin.CUp : _dir < 0 ? SneakySkin.CDown : SneakySkin.CMute;
			var r = _sp.Card(slot.X, slot.Y, cw, ch, col);

			_sp.Dot(r.Left + 5f, r.Top + 8f, col, _dir != 0);
			_sp.Text("EYE  (orthogonal)", r.Left + 16f, r.Top, r.Width - 20f, 16f, SneakySkin.CInk, 11f, true);

			_sp.Text(_dir > 0 ? "QUALIFIED LONG" : _dir < 0 ? "QUALIFIED SHORT" : "NOT QUALIFIED",
				r.Left, r.Top + 24f, r.Width, 20f, col, 14f, true);

			_sp.Text("score " + _score.ToString("0.00") + "   evidence " + (_evidence * 100.0).ToString("0") + "%",
				r.Left, r.Top + 48f, r.Width, 14f, SneakySkin.CInk2, 10.5f);

			_sp.Text("non-price, non-double-counted: net flow \u00b7 flow/price divergence \u00b7 book",
				r.Left, r.Top + 66f, r.Width, 14f, SneakySkin.CMute, 9f);

			if (_thin)
				_sp.Text("THIN \u2014 under " + (MinEvidence * 100.0).ToString("0") + "% evidence present",
					r.Left, r.Top + 84f, r.Width, 14f, SneakySkin.CWarn, 9.5f, true);
			else if (_opposed)
				_sp.Text("TREND-OPPOSED \u2014 qualification dropped",
					r.Left, r.Top + 84f, r.Width, 14f, SneakySkin.CWarn, 9.5f, true);

			_sp.Text("SOURCES   \u25b2long  \u25bcshort  ~absent",
				r.Left, r.Top + 108f, r.Width, 12f, SneakySkin.CMute, 8f, true);

			float chipW = r.Width / Math.Max(1, _chips.Count);
			for (int i = 0; i < _chips.Count && i < 4; i++)
			{
				var kv = _chips[i];
				var cc = kv.Value == 0 ? SneakySkin.CMute : kv.Value > 0 ? SneakySkin.CUp : SneakySkin.CDown;
				string g = kv.Value > 0 ? "\u25b2" : kv.Value < 0 ? "\u25bc" : "~";
				_sp.Text(kv.Key + g, r.Left + i * chipW, r.Top + 122f, chipW, 13f, cc, 8.5f, kv.Value != 0);
			}

			_sp.Text("NOT SentinelEye \u2014 different method, same seam",
				r.Left, r.Top + 146f, r.Width, 13f, SneakySkin.CMute, 7.5f);
		}

		[Browsable(false)] [XmlIgnore] public Series<double> EyeDirection => Values[0];
		[Browsable(false)] [XmlIgnore] public Series<double> EyeScore     => Values[1];

		#region Properties

		[NinjaScriptProperty] [Range(0.5, 120.0)]
		[Display(Name = "Seam Staleness (sec)", GroupName = "1. Qualification", Order = 0)]
		public double StaleSec { get; set; }

		[NinjaScriptProperty] [Range(0.0, 1.0)]
		[Display(Name = "Qualify Threshold", Description = "|score| must exceed this before a direction is qualified. Higher = fewer, firmer verdicts.", GroupName = "1. Qualification", Order = 1)]
		public double QualifyThreshold { get; set; }

		[NinjaScriptProperty] [Range(0.0, 1.0)]
		[Display(Name = "Min Evidence Fraction", Description = "Fraction of the DECLARED evidence weight that must actually be present before anything can qualify. Guards against a confident-looking verdict built on one surviving source: with only CVD attached, a 100% score is one witness, not three.", GroupName = "1. Qualification", Order = 2)]
		public double MinEvidence { get; set; }

		[NinjaScriptProperty] [Range(1, 200)]
		[Display(Name = "Wall Veto Distance (ticks)", GroupName = "1. Qualification", Order = 3)]
		public int WallVetoTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Veto When Trend Opposes", Description = "SentinelTrend is the ONLY price-derived input here and is used strictly as a veto, never as evidence. A qualification the trend flatly contradicts is dropped — but letting trend CONTRIBUTE would defeat the purpose of an orthogonal qualifier.", GroupName = "1. Qualification", Order = 4)]
		public bool RequirePriceNotOpposed { get; set; }

		[NinjaScriptProperty] [Range(0.0, 5.0)] [Display(Name = "Weight \u2014 CVD Direction",   GroupName = "2. Evidence Weights (unvalidated)", Order = 0)] public double WeightCvdDir { get; set; }
		[NinjaScriptProperty] [Range(0.0, 5.0)] [Display(Name = "Weight \u2014 CVD Divergence", GroupName = "2. Evidence Weights (unvalidated)", Order = 1)] public double WeightCvdDivergence { get; set; }
		[NinjaScriptProperty] [Range(0.0, 5.0)] [Display(Name = "Weight \u2014 Liquidity (book)", GroupName = "2. Evidence Weights (unvalidated)", Order = 2)] public double WeightLiquidity { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Publish EyeVerdict to Sneaky", Description = "SCOPE-keyed per the Core contract. Source is tagged 'SneakyEye/orthogonal' so a consumer can tell whose Eye produced the verdict. CompressionBase and VolEnvelope both READ EyeVerdict, but as telemetry only \u2014 neither gates on it.", GroupName = "3. Sneaky", Order = 0)]
		public bool PublishVerdict { get; set; }

		[NinjaScriptProperty] [Display(Name = "Show Card", GroupName = "4. Display", Order = 0)] public bool ShowCard { get; set; }
		[NinjaScriptProperty] [Display(Name = "Card Corner", GroupName = "4. Display", Order = 1)] public SneakyCardCorner CardCorner { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show Indicator Label", GroupName = "4. Display", Order = 2)] public bool ShowIndicatorLabel { get; set; }

		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Sneaky.Sensors.SneakyEye_v1_1_0[] cacheSneakyEye_v1_1_0;
		public Sneaky.Sensors.SneakyEye_v1_1_0 SneakyEye_v1_1_0(double staleSec, double qualifyThreshold, double minEvidence, int wallVetoTicks, bool requirePriceNotOpposed, double weightCvdDir, double weightCvdDivergence, double weightLiquidity, bool publishVerdict, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyEye_v1_1_0(Input, staleSec, qualifyThreshold, minEvidence, wallVetoTicks, requirePriceNotOpposed, weightCvdDir, weightCvdDivergence, weightLiquidity, publishVerdict, showCard, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Sensors.SneakyEye_v1_1_0 SneakyEye_v1_1_0(ISeries<double> input, double staleSec, double qualifyThreshold, double minEvidence, int wallVetoTicks, bool requirePriceNotOpposed, double weightCvdDir, double weightCvdDivergence, double weightLiquidity, bool publishVerdict, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyEye_v1_1_0 != null)
				for (int idx = 0; idx < cacheSneakyEye_v1_1_0.Length; idx++)
					if (cacheSneakyEye_v1_1_0[idx] != null && cacheSneakyEye_v1_1_0[idx].StaleSec == staleSec && cacheSneakyEye_v1_1_0[idx].QualifyThreshold == qualifyThreshold && cacheSneakyEye_v1_1_0[idx].MinEvidence == minEvidence && cacheSneakyEye_v1_1_0[idx].WallVetoTicks == wallVetoTicks && cacheSneakyEye_v1_1_0[idx].RequirePriceNotOpposed == requirePriceNotOpposed && cacheSneakyEye_v1_1_0[idx].WeightCvdDir == weightCvdDir && cacheSneakyEye_v1_1_0[idx].WeightCvdDivergence == weightCvdDivergence && cacheSneakyEye_v1_1_0[idx].WeightLiquidity == weightLiquidity && cacheSneakyEye_v1_1_0[idx].PublishVerdict == publishVerdict && cacheSneakyEye_v1_1_0[idx].ShowCard == showCard && cacheSneakyEye_v1_1_0[idx].CardCorner == cardCorner && cacheSneakyEye_v1_1_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyEye_v1_1_0[idx].EqualsInput(input))
						return cacheSneakyEye_v1_1_0[idx];
			return CacheIndicator<Sneaky.Sensors.SneakyEye_v1_1_0>(new Sneaky.Sensors.SneakyEye_v1_1_0(){ StaleSec = staleSec, QualifyThreshold = qualifyThreshold, MinEvidence = minEvidence, WallVetoTicks = wallVetoTicks, RequirePriceNotOpposed = requirePriceNotOpposed, WeightCvdDir = weightCvdDir, WeightCvdDivergence = weightCvdDivergence, WeightLiquidity = weightLiquidity, PublishVerdict = publishVerdict, ShowCard = showCard, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyEye_v1_1_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Sensors.SneakyEye_v1_1_0 SneakyEye_v1_1_0(double staleSec, double qualifyThreshold, double minEvidence, int wallVetoTicks, bool requirePriceNotOpposed, double weightCvdDir, double weightCvdDivergence, double weightLiquidity, bool publishVerdict, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyEye_v1_1_0(Input, staleSec, qualifyThreshold, minEvidence, wallVetoTicks, requirePriceNotOpposed, weightCvdDir, weightCvdDivergence, weightLiquidity, publishVerdict, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyEye_v1_1_0 SneakyEye_v1_1_0(ISeries<double> input , double staleSec, double qualifyThreshold, double minEvidence, int wallVetoTicks, bool requirePriceNotOpposed, double weightCvdDir, double weightCvdDivergence, double weightLiquidity, bool publishVerdict, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyEye_v1_1_0(input, staleSec, qualifyThreshold, minEvidence, wallVetoTicks, requirePriceNotOpposed, weightCvdDir, weightCvdDivergence, weightLiquidity, publishVerdict, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Sensors.SneakyEye_v1_1_0 SneakyEye_v1_1_0(double staleSec, double qualifyThreshold, double minEvidence, int wallVetoTicks, bool requirePriceNotOpposed, double weightCvdDir, double weightCvdDivergence, double weightLiquidity, bool publishVerdict, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyEye_v1_1_0(Input, staleSec, qualifyThreshold, minEvidence, wallVetoTicks, requirePriceNotOpposed, weightCvdDir, weightCvdDivergence, weightLiquidity, publishVerdict, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyEye_v1_1_0 SneakyEye_v1_1_0(ISeries<double> input , double staleSec, double qualifyThreshold, double minEvidence, int wallVetoTicks, bool requirePriceNotOpposed, double weightCvdDir, double weightCvdDivergence, double weightLiquidity, bool publishVerdict, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyEye_v1_1_0(input, staleSec, qualifyThreshold, minEvidence, wallVetoTicks, requirePriceNotOpposed, weightCvdDir, weightCvdDivergence, weightLiquidity, publishVerdict, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
