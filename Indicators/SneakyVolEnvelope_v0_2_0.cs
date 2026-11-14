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
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors;   // so NT's generated region resolves the bare custom enums (VolMode) — see sentinel-namespace-and-naming
using NinjaTrader.NinjaScript.AddOns.Sneaky;
#endregion

// ═════════════════════════════════════════════════════════════════════════════
//  VolEnvelope — honest volatility envelope (a ground-up Bollinger rewrite)   [Edge lane, no orders]
//  File: SneakyVolEnvelope_v0_2_0.cs   |   Version: v0.2.0   |   namespace …Indicators.Sneaky
// ─────────────────────────────────────────────────────────────────────────────
//  WHY: Bollinger draws SMA ± 2σ — Gaussian, symmetric, close-only, regime-blind, and drawn as if
//  the vol estimate were exact. This closes all of that (see Docs/SENTINEL_VOLENVELOPE_SPEC.md):
//    • CENTER  = EWMA of typical price (no SMA "drop-off" jerk).
//    • VOL     = range-based Yang-Zhang (Rogers-Satchell fallback for gapless tick/Renko bars) —
//                uses the whole bar, reacts to expansion sooner than close-only stdev.
//    • WIDTH   = empirically calibrated PER SIDE — the multiplier is the real quantile of this
//                instrument's standardized returns, computed separately up/down → asymmetric + fat-tail-honest.
//    • REGIME  = native Squeeze / Range / TrendUp / TrendDown / Expansion.
//    • %b      = TREND-AWARE — a band breach in RANGE reads EXTREME (fade); the same breach in a
//                TREND reads RIDING (follow). The one thing classic BB structurally cannot do.
//    • ERROR   = faint band-of-band from SE(σ) ≈ σ/√(2P), widened right after a regime flip. The fuzz is the honesty.
//    • CONE    = √t-growing forward projection right of the last bar.
//  Advisory-only (Edge lane, submits nothing). Consults the Eye verdict for trend context, and — when
//  Publish regime is on — PUBLISHES its regime/stretch via SneakyCore.SetEnvelopeState so the Copier /
//  Arc / strategies can gate on it (e.g. "don't ADD in a squeeze"). Consume via GetEnvelopeState(instr, age).
//  Sneaky-homed: Indicators.Sneaky namespace (→ "Sneaky" picker folder), glass card via SneakySkin.Painter,
//  CardLayout anti-overlap docking, label-remover (clean chart by default).
//
//  CHANGELOG
//    v0.2.0a (2026-07-07) — PublishRegime now DEFAULTS ON so VolEnvelope feeds the Council's EnvelopeState
//             voter out of the box. In-place patch (no rename); existing placements keep their serialized value.
//    v0.2.0 — PUBLISH SEAM wired: Publish regime (opt-in) → SneakyCore.SetEnvelopeState(instr, regime,
//             stretch, bwPctile, multUp, multDown, source); new SneakyCore.EnvelopeState +
//             Get/AllEnvelopeStates consult API (regime as int; publish/consult mirrors EyeVerdict).
//             v0_1_0 ARCHIVED out of the tree (…\_archive\Indicators) — new type identity, re-add on charts.
//    v0.1.0 — [frozen, archived as VolEnvelope_v0_1_0] Initial. EWMA center + YZ/RS vol + asymmetric empirical
//             bands (per-side quantile) + regime + trend-aware %b + error band + forward cone + glass card.
//             Live-GC fixes: cone/card in SEPARATE try/catch (first-fail → sneaky.log); cone uses
//             Bars.GetTime ABSOLUTE indexing (barsAgo Time[1] throws in render); Mid plot → dashed.
// ═════════════════════════════════════════════════════════════════════════════

namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors
{
	/// <summary>Volatility regime classification exposed by VolEnvelope.</summary>
	public enum EnvRegime { Squeeze, Range, TrendUp, TrendDown, Expansion }

	/// <summary>Volatility estimator selection. Auto = Yang-Zhang, falling back to Rogers-Satchell on gapless bars.</summary>
	public enum VolMode { Auto, YangZhang, RogersSatchell }

	public class SneakyVolEnvelope_v0_2_0 : Indicator, ISneakyScrollCard
	{
		// FROZEN brushes (created on the config thread → safe on the render thread). Bands stay neutral:
		// green/red are reserved for money+direction (design §0), so the envelope structure is ink-toned.
		private static readonly System.Windows.Media.Brush SB_Band = SFreeze(174, 186, 206);  // ink2 — band lines
		private static readonly System.Windows.Media.Brush SB_Mid  = SFreeze(108, 122, 146);  // mute — center line
		private static readonly System.Windows.Media.Brush SB_Err  = SFreeze(38, 52, 76);     // faint — error band
		private static System.Windows.Media.Brush SFreeze(byte r, byte g, byte b)
		{ var br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)); br.Freeze(); return br; }

		// ── engine state ──
		private double emaMid;
		private bool   midSeeded;
		private double[] zRing;                 // standardized returns, circular, size CalibrationLookback
		private int    zHead, zCount;
		private double[] bwRing;                // bandwidth history, circular, size BandwidthWindow
		private int    bwHead, bwCount;
		private double prevBandwidth;
		private EnvRegime prevRegime = EnvRegime.Range;
		private int    lastRegimeChangeBar;

		// ── last-bar snapshot for OnRender (cone + card) ──
		private bool      lValid;
		private double    lMid, lVol, lMultUp, lMultDown, lSlopePx;
		private double    lPercentB, lStretch, lBandwidth, lBwPct;
		private EnvRegime lRegime = EnvRegime.Range;
		private bool      lExtreme, lRiding;
		private int       lEyeDir; private bool lEyeHad;

		// Sneaky glass-card readout (SharpDX via SneakySkin.Painter, drawn in OnRender)
		private SneakySkin.Painter _sp;
		private int _lastPublishDiagLog = 0;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "Honest volatility envelope — a Bollinger rewrite (EWMA center, range-based vol, asymmetric empirical width, native regime, trend-aware %b, error band, forward cone). Edge lane, no orders.";
				Name						= "Sneaky Vol Envelope v0.2.0";
				Calculate					= Calculate.OnBarClose;
				IsOverlay					= true;
				DisplayInDataBox			= true;
				DrawOnPricePanel			= true;
				PaintPriceMarkers			= false;
				IsSuspendedWhileInactive	= false;

				Period				= 20;
				RobustCenter		= false;
				VolModeParam		= VolMode.Auto;
				CalibrationLookback	= 500;
				Quantile			= 0.95;
				SqueezePctile		= 0.20;
				BandwidthWindow		= 125;
				TrendAdx			= 25;
				AdxPeriod			= 14;
				ShowCone			= true;
				ForecastBars		= 10;
				ConeFlat			= false;
				ShowErrorBand		= true;
				ShowInfo			= true;
				CardCorner			= SneakyCardCorner.TopRight;
				PublishRegime		= true;    // default ON — feed the Council out of the box
				ShowIndicatorLabel	= false;   // Sneaky standard: clean chart (NT name label removed)

				AddPlot(new Stroke(SB_Band, 1.8f), PlotStyle.Line, "Upper");
				AddPlot(new Stroke(SB_Band, 1.8f), PlotStyle.Line, "Lower");
				AddPlot(new Stroke(SB_Mid, DashStyleHelper.Dash, 1.2f), PlotStyle.Line, "Mid");   // dashed center line
				AddPlot(new Stroke(SB_Err,  1f),   PlotStyle.Line, "UpperHi");
				AddPlot(new Stroke(SB_Err,  1f),   PlotStyle.Line, "UpperLo");
				AddPlot(new Stroke(SB_Err,  1f),   PlotStyle.Line, "LowerHi");
				AddPlot(new Stroke(SB_Err,  1f),   PlotStyle.Line, "LowerLo");
			}
			else if (State == State.DataLoaded)
			{
				if (!ShowIndicatorLabel) Name = string.Empty;   // Sneaky label remover — NT draws the chart panel label from Name (see LabelRemover.cs)
				MidSeries       = new Series<double>(this);
				RegimeSeries    = new Series<double>(this);
				StretchSeries   = new Series<double>(this);
				BandwidthSeries = new Series<double>(this);
				zRing  = new double[Math.Max(50, CalibrationLookback)];
				bwRing = new double[Math.Max(10, BandwidthWindow)];
				zHead = zCount = bwHead = bwCount = 0;
				emaMid = 0; midSeeded = false; prevBandwidth = 0; lValid = false;
			}
			else if (State == State.Terminated)
			{
				if (_sp != null) { try { _sp.Dispose(); } catch { } _sp = null; }
				try { SneakySkin.CardLayout.Release(this); } catch { }
			}
		}

		// typical price of the bar `i` bars ago
		private double Tp(int i) => (High[i] + Low[i] + Close[i]) / 3.0;
		private static double Ln(double a, double b) => (a > 0 && b > 0) ? Math.Log(a / b) : 0.0;
		private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

		protected override void OnBarUpdate()
		{
			// need Period+1 bars (overnight term reads Close[Period]) plus one for the return
			if (CurrentBar < Period + 2)
			{
				Upper[0] = Lower[0] = Mid[0] = double.NaN;
				UpperHi[0] = UpperLo[0] = LowerHi[0] = LowerLo[0] = double.NaN;
				MidSeries[0] = double.NaN; RegimeSeries[0] = 0; StretchSeries[0] = 0; BandwidthSeries[0] = double.NaN;
				return;
			}

			double tp = Tp(0), tpPrev = Tp(1);
			double r  = (tp > 0 && tpPrev > 0) ? Math.Log(tp / tpPrev) : 0.0;

			// ── center: EWMA of typical price (optionally median-blended) ──
			double alpha = 2.0 / (Period + 1);
			if (!midSeeded)
			{
				double s = 0; for (int i = 0; i < Period; i++) s += Tp(i);
				emaMid = s / Period; midSeeded = true;
			}
			else
				emaMid = alpha * tp + (1.0 - alpha) * emaMid;

			double mid = emaMid;
			if (RobustCenter) mid = 0.5 * emaMid + 0.5 * MedianTp(Period);
			MidSeries[0] = mid;

			// ── volatility: Yang-Zhang / Rogers-Satchell (per-bar return sigma) ──
			double vol = ComputeVol();

			// ── standardized return → calibration ring ──
			if (vol > 1e-9) PushZ(r / vol);
			double multUp, multDown;
			CalcMults(out multUp, out multDown);

			// ── bands (asymmetric: per-side empirical multiplier) ──
			double upper = mid + mid * vol * multUp;
			double lower = mid - mid * vol * multDown;

			Upper[0] = upper; Lower[0] = lower; Mid[0] = mid;

			// ── error band: SE(σ) ≈ σ/√(2P), widened right after a regime flip ──
			if (ShowErrorBand)
			{
				double se = vol / Math.Sqrt(2.0 * Math.Max(1, Period));
				int barsSinceFlip = CurrentBar - lastRegimeChangeBar;
				double flipFuzz = barsSinceFlip < Period ? 1.0 + (double)(Period - barsSinceFlip) / Period : 1.0;
				se *= flipFuzz;
				double upErr = mid * multUp * se, dnErr = mid * multDown * se;
				UpperHi[0] = upper + upErr; UpperLo[0] = upper - upErr;
				LowerHi[0] = lower + dnErr; LowerLo[0] = lower - dnErr;
			}
			else
			{
				UpperHi[0] = UpperLo[0] = LowerHi[0] = LowerLo[0] = double.NaN;
			}

			// ── bandwidth + its rolling percentile ──
			double bandwidth = mid > 0 ? (upper - lower) / mid : 0;
			bool bwRising = bandwidth > prevBandwidth;
			PushBw(bandwidth);
			double bwPct = BwPercentile(bandwidth);
			BandwidthSeries[0] = bandwidth;

			// ── center slope (price per bar over Period/2) ──
			int lb = Math.Max(1, Period / 2);
			double mPrev = CurrentBar >= lb ? MidSeries[lb] : double.NaN;
			double slopePx = !double.IsNaN(mPrev) ? (mid - mPrev) / lb : 0;

			// ── ADX (regime tag only) ──
			double adx = CurrentBar > AdxPeriod ? ADX(AdxPeriod)[0] : 0;

			// ── regime ──
			EnvRegime regime;
			if (bwPct <= SqueezePctile)                        regime = EnvRegime.Squeeze;
			else if (bwPct >= 0.80 && bwRising)                regime = EnvRegime.Expansion;
			else if (adx >= TrendAdx && slopePx > 0)           regime = EnvRegime.TrendUp;
			else if (adx >= TrendAdx && slopePx < 0)           regime = EnvRegime.TrendDown;
			else                                               regime = EnvRegime.Range;

			if (regime != prevRegime) { lastRegimeChangeBar = CurrentBar; prevRegime = regime; }
			RegimeSeries[0] = (int)regime;

			// ── trend-aware %b + stretch (σ beyond the near band) ──
			double denom = upper - lower;
			double percentB = denom > 1e-9 ? (Close[0] - lower) / denom : 0.5;
			double oneSigma = mid * vol;
			double stretch = 0;
			if (oneSigma > 1e-12)
			{
				if (Close[0] > upper)      stretch =  (Close[0] - upper) / oneSigma;
				else if (Close[0] < lower) stretch = -(lower - Close[0]) / oneSigma;
			}
			StretchSeries[0] = stretch;
			bool extreme = Math.Abs(stretch) > 0 && regime == EnvRegime.Range;
			bool riding  = Math.Abs(stretch) > 0 && (regime == EnvRegime.TrendUp || regime == EnvRegime.TrendDown);

			// ── Eye consult (trend context; advisory only) ──
			lEyeHad = false; lEyeDir = 0;
			try
			{
				var v = SneakyCore.GetEyeVerdict(Scope(), 0);   // 2026-08-04 — was bare Instrument.MasterInstrument.Name;
				// hit Core's AMBIGUOUS SEAM fail-closed path with multiple laned charts on one instrument and
				// silently returned null every time. Scope() picks THIS chart's own Eye reading.
				if (v != null) { lEyeHad = true; lEyeDir = v.Direction; }
			}
			catch { }

			// ── PUBLISH regime/stretch (opt-in) so Copier/Arc/strategies can gate on it (e.g. "don't add in a squeeze") ──
			// SneakyCore ≥ v1.18.0 — keyed by SCOPE, not instrument. NOTE the two different "bar tags" here: this
			// file's own BarTag() is a human SOURCE label ("TBC50-1-0"); the seam key needs SneakyCore.BarTag().
			if (PublishRegime)
			{
				string diagScope = null;
				try
				{
					diagScope = Scope();
					SneakyCore.SetEnvelopeState(diagScope, SneakyCore.BarTag(BarsPeriod), InstName(),
					                              (int)regime, stretch, bwPct, multUp, multDown, BarTag());
					if (unchecked(Environment.TickCount - _lastPublishDiagLog) > 5000)
					{
						_lastPublishDiagLog = Environment.TickCount;
						try { SneakyCore.Log("VolEnvelopePublishDiag", "published OK, scope='" + (diagScope ?? "NULL") + "'"); } catch { }
					}
				}
				catch (Exception pubEx)
				{
					if (unchecked(Environment.TickCount - _lastPublishDiagLog) > 5000)
					{
						_lastPublishDiagLog = Environment.TickCount;
						try { SneakyCore.Log("VolEnvelopePublishDiag", "PUBLISH THREW: " + pubEx.GetType().Name + ": " + pubEx.Message
							+ "  (Scope() returned '" + (diagScope ?? "NULL") + "' before the throw)"); } catch { }
					}
				}
			}

			// ── snapshot for OnRender ──
			lValid = true;
			lMid = mid; lVol = vol; lMultUp = multUp; lMultDown = multDown; lSlopePx = slopePx;
			lPercentB = percentB; lStretch = stretch; lBandwidth = bandwidth; lBwPct = bwPct;
			lRegime = regime; lExtreme = extreme; lRiding = riding;

			prevBandwidth = bandwidth;
		}

		// ── Yang-Zhang / Rogers-Satchell per-bar volatility over `Period` ──
		private double ComputeVol()
		{
			int P = Period;
			double sumO = 0, sumO2 = 0, sumC = 0, sumC2 = 0, sumRS = 0, sumAbsO = 0;
			for (int i = 0; i < P; i++)
			{
				double o  = Ln(Open[i], Close[i + 1]);   // overnight / inter-bar gap
				double cc = Ln(Close[i], Open[i]);        // open→close
				double u  = Ln(High[i], Open[i]);
				double d  = Ln(Low[i],  Open[i]);
				double rs = u * (u - cc) + d * (d - cc);  // Rogers-Satchell (drift-free)
				sumO += o; sumO2 += o * o; sumAbsO += Math.Abs(o);
				sumC += cc; sumC2 += cc * cc; sumRS += rs;
			}
			double meanO = sumO / P, meanC = sumC / P;
			double varO  = (sumO2 - P * meanO * meanO) / Math.Max(1, P - 1);
			double varC  = (sumC2 - P * meanC * meanC) / Math.Max(1, P - 1);
			double meanRS = sumRS / P;
			if (varO < 0) varO = 0; if (varC < 0) varC = 0; if (meanRS < 0) meanRS = 0;

			double k = 0.34 / (1.34 + (double)(P + 1) / (P - 1));
			bool gap = (sumAbsO / P) > 1e-7;   // do the bars actually carry gaps?

			double var;
			if (VolModeParam == VolMode.RogersSatchell || (VolModeParam == VolMode.Auto && !gap))
				var = meanRS;
			else
				var = varO + k * varC + (1.0 - k) * meanRS;

			return Math.Sqrt(Math.Max(var, 0));
		}

		private double MedianTp(int P)
		{
			var a = new double[P];
			for (int i = 0; i < P; i++) a[i] = Tp(i);
			Array.Sort(a);
			return P % 2 == 1 ? a[P / 2] : 0.5 * (a[P / 2 - 1] + a[P / 2]);
		}

		private void PushZ(double z)
		{
			zRing[zHead] = z;
			zHead = (zHead + 1) % zRing.Length;
			if (zCount < zRing.Length) zCount++;
		}

		// per-side empirical multiplier = q-quantile of standardized returns on that side (fat-tail + asymmetry honest)
		private void CalcMults(out double multUp, out double multDown)
		{
			multUp = multDown = 2.0;   // fallback until enough calibration data
			if (zCount < 40) return;

			var ups = new List<double>();
			var dns = new List<double>();
			for (int i = 0; i < zCount; i++)
			{
				double z = zRing[i];
				if (z > 0) ups.Add(z);
				else if (z < 0) dns.Add(-z);
			}
			if (ups.Count >= 15) multUp   = Clamp(Quantile2(ups, Quantile), 0.5, 6.0);
			if (dns.Count >= 15) multDown = Clamp(Quantile2(dns, Quantile), 0.5, 6.0);
		}

		private static double Quantile2(List<double> s, double q)
		{
			if (s.Count == 0) return 2.0;
			s.Sort();
			double idx = q * (s.Count - 1);
			int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
			if (lo < 0) lo = 0; if (hi >= s.Count) hi = s.Count - 1;
			double frac = idx - lo;
			return s[lo] * (1 - frac) + s[hi] * frac;
		}

		private void PushBw(double bw)
		{
			bwRing[bwHead] = bw;
			bwHead = (bwHead + 1) % bwRing.Length;
			if (bwCount < bwRing.Length) bwCount++;
		}

		private double BwPercentile(double bw)
		{
			if (bwCount < 5) return 0.5;
			int le = 0;
			for (int i = 0; i < bwCount; i++) if (bwRing[i] <= bw) le++;
			return (double)le / bwCount;
		}

		private static string RegimeLabel(EnvRegime r)
		{
			switch (r)
			{
				case EnvRegime.Squeeze:   return "SQUEEZE";
				case EnvRegime.Expansion: return "EXPAND";
				case EnvRegime.TrendUp:   return "TREND ↑";
				case EnvRegime.TrendDown: return "TREND ↓";
				default:                  return "RANGE";
			}
		}
		private static SharpDX.Color4 RegimeColor(EnvRegime r)
		{
			switch (r)
			{
				case EnvRegime.Squeeze:   return SneakySkin.CWarn;    // amber — coiled / caution
				case EnvRegime.Expansion: return SneakySkin.CAccent;  // cyan — active expansion
				case EnvRegime.TrendUp:   return SneakySkin.CUp;      // green — direction
				case EnvRegime.TrendDown: return SneakySkin.CDown;    // red — direction
				default:                  return SneakySkin.CMute;
			}
		}

		/// <summary>A human SOURCE label for the seam ("TBC50-1-0"). NOT the seam key — see Scope().</summary>
		private string BarTag() => "TBC" + BarsPeriod.Value + "-" + BarsPeriod.Value2 + "-" + ((int)BarsPeriod.BarsPeriodType);
		private string InstName() => (Instrument != null && Instrument.MasterInstrument != null) ? Instrument.MasterInstrument.Name : "";

		// ── scope (SneakyCore v1.18.0 · execution plan 1.4) ──
		// "<masterInstrument>.<barTag>" — ONE CHART's worth of context. Resolved lazily and cached; a null scope
		// no-ops the publish, the right fail-silent for an indicator that is not yet configured.
		private string _scope;
		private string Scope()
		{
			try { _scope = SneakyCore.ScopeOf(Instrument, BarsPeriod, ChartControl); } catch { }   // 2026-08-04 — ALWAYS recomputed, never permanently cached: a chart's lane can register AFTER this indicator's first call, so caching once risked freezing a stale, unlaned scope forever (confirmed happening for real tonight). Cheap to recompute every call; only overwrites _scope on success, so a transient throw keeps the previous good value.
			return _scope;
		}

		private bool _loggedCone, _loggedCard;
		private void LogOnce(ref bool flag, string what, Exception ex)
		{
			if (flag) return; flag = true;
			try { SneakyCore.Log("VOLENV", what + ": " + ex.GetType().Name + " " + ex.Message); } catch { }
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (RenderTarget == null || ChartPanel == null) return;

			if (_sp == null) _sp = new SneakySkin.Painter();
			_sp.Begin(RenderTarget);

			// cone + card in SEPARATE try blocks — a cone failure must never suppress the card (or vice-versa).
			// First failure of each is logged to sneaky.log so we can diagnose without a silent swallow.
			if (ShowCone && lValid && CurrentBar > 1 && ForecastBars > 0)
			{
				try { RenderCone(chartControl, chartScale); }
				catch (Exception ex) { LogOnce(ref _loggedCone, "cone", ex); }
			}

			if (ShowInfo)
			{
				try { RenderCard(); }
				catch (Exception ex) { LogOnce(ref _loggedCard, "card", ex); }
			}

			try { _sp.End(); } catch { }
		}

		private void RenderCone(ChartControl chartControl, ChartScale chartScale)
		{
			// degenerate-input guards (a bad value here would throw and, pre-split, killed the whole frame)
			if (lMid <= 0 || double.IsNaN(lMid) || double.IsNaN(lVol) || double.IsNaN(lSlopePx)) return;
			if (Bars == null || Bars.Count < 2) return;

			// ABSOLUTE bar indexing — barsAgo indexing (Time[1]) throws in the OnRender context
			// ('barsAgo needed to be between 0 and N'); Bars.GetTime(absoluteIndex) is render-safe.
			float x0 = chartControl.GetXByTime(Bars.GetTime(Bars.Count - 1));
			float x1 = chartControl.GetXByTime(Bars.GetTime(Bars.Count - 2));
			float dx = x0 - x1;
			if (dx <= 0 || float.IsNaN(dx) || float.IsInfinity(dx)) dx = 6f;

			int h = ForecastBars;
			var pts = new List<SharpDX.Vector2>(h * 2);
			for (int j = 1; j <= h; j++)   // upper edge forward
			{
				double midF = ConeFlat ? lMid : lMid + lSlopePx * j;
				double hw = lMid * lVol * lMultUp * Math.Sqrt(j);
				pts.Add(new SharpDX.Vector2(x0 + dx * j, chartScale.GetYByValue(midF + hw)));
			}
			for (int j = h; j >= 1; j--)   // lower edge back
			{
				double midF = ConeFlat ? lMid : lMid + lSlopePx * j;
				double hw = lMid * lVol * lMultDown * Math.Sqrt(j);
				pts.Add(new SharpDX.Vector2(x0 + dx * j, chartScale.GetYByValue(midF - hw)));
			}
			if (pts.Count < 3) return;

			var geo = new SharpDX.Direct2D1.PathGeometry(RenderTarget.Factory);
			using (var sink = geo.Open())
			{
				sink.BeginFigure(pts[0], SharpDX.Direct2D1.FigureBegin.Filled);
				for (int i = 1; i < pts.Count; i++) sink.AddLine(pts[i]);
				sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
				sink.Close();
			}
			try
			{
				RenderTarget.FillGeometry(geo, _sp.B(SneakySkin.Alpha(SneakySkin.CAccent, 0.09f)));
				RenderTarget.DrawGeometry(geo, _sp.B(SneakySkin.Alpha(SneakySkin.CAccent, 0.42f)), 1f);
			}
			finally { geo.Dispose(); }
		}
        public void ConductorRender(SharpDX.Direct2D1.RenderTarget rt)
        {
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
			const float cw = 254f, ch = 150f;
			SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
			var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
				ChartControl.ChartPanels[0].X, ChartControl.ChartPanels[0].Y, ChartControl.ChartPanels[0].W, ChartControl.ChartPanels[0].H, cw, ch);
			if (slot.Width <= 0f) return;   // not the currently-selected card in the rotation

			var edge = lRegime == EnvRegime.Squeeze ? SneakySkin.CWarn
					 : (lRegime == EnvRegime.Expansion ? SneakySkin.CAccent : SneakySkin.CLine);
			var r = _sp.Card(slot.X, slot.Y, cw, ch, edge);

			// header: watching dot (always cyan — the tool is live/watching) + title + regime pill
			_sp.Dot(r.Left + 5f, r.Top + 8f, SneakySkin.CAccent, true);
			_sp.Text("VOL ENVELOPE", r.Left + 16f, r.Top, r.Width - 84f, 16f, SneakySkin.CInk, 11f, true);
			_sp.Pill(RegimeLabel(lRegime), r.Right, r.Top - 1f, RegimeColor(lRegime));

			// hero: %b (cyan = watching). When price is outside the band, show the σ-stretch instead.
			bool outside = Math.Abs(lStretch) > 0;
			string heroLabel = outside ? "STRETCH" : "%b";
			string heroVal   = outside ? (lStretch >= 0 ? "+" : "") + lStretch.ToString("0.0") + "σ"
									   : lPercentB.ToString("0.00");
			var heroCol = SneakySkin.CAccent;
			if (outside) heroCol = lRiding ? (lStretch >= 0 ? SneakySkin.CUp : SneakySkin.CDown)
										   : (lExtreme ? SneakySkin.CWarn : SneakySkin.CInk2);
			_sp.Text(heroLabel, r.Left, r.Top + 26f, 80f, 12f, SneakySkin.CMute, 9f, true);
			_sp.Text(heroVal, r.Left, r.Top + 35f, 130f, 26f, heroCol, 22f);

			// signal tag on the right of the hero
			string sig = lRiding ? "RIDING" : (lExtreme ? "EXTREME" : "");
			if (sig.Length > 0)
				_sp.Text(sig, r.Left + 120f, r.Top + 45f, r.Width - 120f, 14f,
					lRiding ? SneakySkin.CAccent : SneakySkin.CWarn, 10f, true);

			// bandwidth-percentile track (low = coiled/squeeze; cyan when squeezed, faint otherwise)
			_sp.Track(r.Left, r.Top + 66f, r.Width, (float)lBwPct,
				lRegime == EnvRegime.Squeeze ? SneakySkin.CAccent : SneakySkin.CFaint, 5f);

			// stat rows (mono)
			var lead = SharpDX.DirectWrite.TextAlignment.Leading;
			_sp.Text("σ " + lVol.ToString("0.0000") + "   mult ↑" + lMultUp.ToString("0.0") + " ↓" + lMultDown.ToString("0.0"),
				r.Left, r.Top + 78f, r.Width, 14f, SneakySkin.CInk2, 10.5f, false, lead, true);
			_sp.Text("bw " + lBandwidth.ToString("0.000") + "  pct " + (lBwPct * 100).ToString("0") + "   eye " + (lEyeHad ? (lEyeDir > 0 ? "↑" : lEyeDir < 0 ? "↓" : "·") : "–"),
				r.Left, r.Top + 92f, r.Width, 14f, SneakySkin.CMute, 10f, false, lead, true);
			_sp.Text(InstName() + "  " + BarTag(), r.Left, r.Top + 106f, r.Width, 12f, SneakySkin.CMute, 9f, false, lead, true);
		}

		#region Consumable "current" surface
		[Browsable(false)] [XmlIgnore] public double CurrentUpper => Values[0][0];
		[Browsable(false)] [XmlIgnore] public double CurrentLower => Values[1][0];
		[Browsable(false)] [XmlIgnore] public double CurrentMid   => Values[2][0];
		[Browsable(false)] [XmlIgnore] public double Bandwidth       => lBandwidth;
		[Browsable(false)] [XmlIgnore] public double BandwidthPctile => lBwPct;
		[Browsable(false)] [XmlIgnore] public double PctB            => lPercentB;   // NOT "PercentB" — that hides Indicator.PercentB(int)
		[Browsable(false)] [XmlIgnore] public double Stretch         => lStretch;
		[Browsable(false)] [XmlIgnore] public double SigmaReturn     => lVol;
		[Browsable(false)] [XmlIgnore] public double MultUp          => lMultUp;
		[Browsable(false)] [XmlIgnore] public double MultDown        => lMultDown;
		[Browsable(false)] [XmlIgnore] public EnvRegime Regime       => lRegime;
		[Browsable(false)] [XmlIgnore] public bool IsSqueeze         => lRegime == EnvRegime.Squeeze;
		[Browsable(false)] [XmlIgnore] public bool IsExtreme         => lExtreme;
		[Browsable(false)] [XmlIgnore] public bool IsRiding          => lRiding;
		#endregion

		#region Plots & historical series
		[Browsable(false)] [XmlIgnore] public Series<double> Upper   => Values[0];
		[Browsable(false)] [XmlIgnore] public Series<double> Lower   => Values[1];
		[Browsable(false)] [XmlIgnore] public Series<double> Mid     => Values[2];
		[Browsable(false)] [XmlIgnore] public Series<double> UpperHi => Values[3];
		[Browsable(false)] [XmlIgnore] public Series<double> UpperLo => Values[4];
		[Browsable(false)] [XmlIgnore] public Series<double> LowerHi => Values[5];
		[Browsable(false)] [XmlIgnore] public Series<double> LowerLo => Values[6];
		[Browsable(false)] [XmlIgnore] public Series<double> MidSeries       { get; private set; }
		[Browsable(false)] [XmlIgnore] public Series<double> RegimeSeries    { get; private set; }
		[Browsable(false)] [XmlIgnore] public Series<double> StretchSeries   { get; private set; }
		[Browsable(false)] [XmlIgnore] public Series<double> BandwidthSeries { get; private set; }
		#endregion

		#region Parameters
		[NinjaScriptProperty] [Range(2, int.MaxValue)]
		[Display(Name = "Period", Order = 1, GroupName = "1 Center")]
		public int Period { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Robust center (EWMA+median)", Order = 2, GroupName = "1 Center")]
		public bool RobustCenter { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Vol mode", Order = 1, GroupName = "2 Volatility")]
		public VolMode VolModeParam { get; set; }

		[NinjaScriptProperty] [Range(50, int.MaxValue)]
		[Display(Name = "Calibration lookback", Order = 1, GroupName = "3 Calibration")]
		public int CalibrationLookback { get; set; }

		[NinjaScriptProperty] [Range(0.80, 0.999)]
		[Display(Name = "Quantile (per side)", Order = 2, GroupName = "3 Calibration")]
		public double Quantile { get; set; }

		[NinjaScriptProperty] [Range(0.05, 0.50)]
		[Display(Name = "Squeeze percentile", Order = 1, GroupName = "4 Regime")]
		public double SqueezePctile { get; set; }

		[NinjaScriptProperty] [Range(20, int.MaxValue)]
		[Display(Name = "Bandwidth window", Order = 2, GroupName = "4 Regime")]
		public int BandwidthWindow { get; set; }

		[NinjaScriptProperty] [Range(10, 50)]
		[Display(Name = "Trend ADX", Order = 3, GroupName = "4 Regime")]
		public int TrendAdx { get; set; }

		[NinjaScriptProperty] [Range(2, int.MaxValue)]
		[Display(Name = "ADX period", Order = 4, GroupName = "4 Regime")]
		public int AdxPeriod { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show cone", Order = 1, GroupName = "5 Forecast")]
		public bool ShowCone { get; set; }

		[NinjaScriptProperty] [Range(1, 100)]
		[Display(Name = "Forecast bars", Order = 2, GroupName = "5 Forecast")]
		public int ForecastBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Cone flat (no drift)", Order = 3, GroupName = "5 Forecast")]
		public bool ConeFlat { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show error band", Order = 1, GroupName = "6 Display")]
		public bool ShowErrorBand { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show info card", Order = 2, GroupName = "6 Display")]
		public bool ShowInfo { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Card corner", Order = 3, GroupName = "6 Display")]
		public SneakyCardCorner CardCorner { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Publish regime", Description = "Broadcast this instrument's regime/stretch via SneakyCore.SetEnvelopeState so the Copier/Arc/strategies can gate on it (advisory).", Order = 1, GroupName = "7 Sneaky")]
		public bool PublishRegime { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show indicator label", Description = "Show NinjaTrader's chart name label. Sneaky default = OFF (clean chart); turn on to restore it.", GroupName = "7 Sneaky", Order = 100)]
		public bool ShowIndicatorLabel { get; set; }
		#endregion

		// ── HEARTBEAT (SneakyCore v1.19.0) ─────────────────────────────────────────────────
		// An OnBarClose publisher only refreshes its seam when a bar closes. In a quiet market bars close
		// slowly, the seam ages past the Council's StaleSec, and a perfectly healthy voter silently drops
		// out of the roster — observed live as a FULLY LOADED chart reporting "roster 3/10". The Council
		// already heartbeats its own verdict; its sensors need the same. Re-stamp the cached reading on
		// incoming quotes: no recompute, realtime only (a historical re-stamp would fake freshness onto a
		// replayed bar), throttled.
		private DateTime _lastHeartbeatUtc;
		private const double HeartbeatSec = 5.0;
		protected override void OnMarketData(NinjaTrader.Data.MarketDataEventArgs marketDataUpdate)
		{
		    if (!PublishRegime || State != State.Realtime) return;
		    DateTime now = DateTime.UtcNow;
		    if ((now - _lastHeartbeatUtc).TotalSeconds < HeartbeatSec) return;
		    _lastHeartbeatUtc = now;
		    try { SneakyCore.TouchEnvelopeState(Scope()); } catch { }
		}
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Sneaky.Sensors.SneakyVolEnvelope_v0_2_0[] cacheSneakyVolEnvelope_v0_2_0;
		public Sneaky.Sensors.SneakyVolEnvelope_v0_2_0 SneakyVolEnvelope_v0_2_0(int period, bool robustCenter, VolMode volModeParam, int calibrationLookback, double quantile, double squeezePctile, int bandwidthWindow, int trendAdx, int adxPeriod, bool showCone, int forecastBars, bool coneFlat, bool showErrorBand, bool showInfo, SneakyCardCorner cardCorner, bool publishRegime, bool showIndicatorLabel)
		{
			return SneakyVolEnvelope_v0_2_0(Input, period, robustCenter, volModeParam, calibrationLookback, quantile, squeezePctile, bandwidthWindow, trendAdx, adxPeriod, showCone, forecastBars, coneFlat, showErrorBand, showInfo, cardCorner, publishRegime, showIndicatorLabel);
		}

		public Sneaky.Sensors.SneakyVolEnvelope_v0_2_0 SneakyVolEnvelope_v0_2_0(ISeries<double> input, int period, bool robustCenter, VolMode volModeParam, int calibrationLookback, double quantile, double squeezePctile, int bandwidthWindow, int trendAdx, int adxPeriod, bool showCone, int forecastBars, bool coneFlat, bool showErrorBand, bool showInfo, SneakyCardCorner cardCorner, bool publishRegime, bool showIndicatorLabel)
		{
			if (cacheSneakyVolEnvelope_v0_2_0 != null)
				for (int idx = 0; idx < cacheSneakyVolEnvelope_v0_2_0.Length; idx++)
					if (cacheSneakyVolEnvelope_v0_2_0[idx] != null && cacheSneakyVolEnvelope_v0_2_0[idx].Period == period && cacheSneakyVolEnvelope_v0_2_0[idx].RobustCenter == robustCenter && cacheSneakyVolEnvelope_v0_2_0[idx].VolModeParam == volModeParam && cacheSneakyVolEnvelope_v0_2_0[idx].CalibrationLookback == calibrationLookback && cacheSneakyVolEnvelope_v0_2_0[idx].Quantile == quantile && cacheSneakyVolEnvelope_v0_2_0[idx].SqueezePctile == squeezePctile && cacheSneakyVolEnvelope_v0_2_0[idx].BandwidthWindow == bandwidthWindow && cacheSneakyVolEnvelope_v0_2_0[idx].TrendAdx == trendAdx && cacheSneakyVolEnvelope_v0_2_0[idx].AdxPeriod == adxPeriod && cacheSneakyVolEnvelope_v0_2_0[idx].ShowCone == showCone && cacheSneakyVolEnvelope_v0_2_0[idx].ForecastBars == forecastBars && cacheSneakyVolEnvelope_v0_2_0[idx].ConeFlat == coneFlat && cacheSneakyVolEnvelope_v0_2_0[idx].ShowErrorBand == showErrorBand && cacheSneakyVolEnvelope_v0_2_0[idx].ShowInfo == showInfo && cacheSneakyVolEnvelope_v0_2_0[idx].CardCorner == cardCorner && cacheSneakyVolEnvelope_v0_2_0[idx].PublishRegime == publishRegime && cacheSneakyVolEnvelope_v0_2_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyVolEnvelope_v0_2_0[idx].EqualsInput(input))
						return cacheSneakyVolEnvelope_v0_2_0[idx];
			return CacheIndicator<Sneaky.Sensors.SneakyVolEnvelope_v0_2_0>(new Sneaky.Sensors.SneakyVolEnvelope_v0_2_0(){ Period = period, RobustCenter = robustCenter, VolModeParam = volModeParam, CalibrationLookback = calibrationLookback, Quantile = quantile, SqueezePctile = squeezePctile, BandwidthWindow = bandwidthWindow, TrendAdx = trendAdx, AdxPeriod = adxPeriod, ShowCone = showCone, ForecastBars = forecastBars, ConeFlat = coneFlat, ShowErrorBand = showErrorBand, ShowInfo = showInfo, CardCorner = cardCorner, PublishRegime = publishRegime, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyVolEnvelope_v0_2_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Sensors.SneakyVolEnvelope_v0_2_0 SneakyVolEnvelope_v0_2_0(int period, bool robustCenter, VolMode volModeParam, int calibrationLookback, double quantile, double squeezePctile, int bandwidthWindow, int trendAdx, int adxPeriod, bool showCone, int forecastBars, bool coneFlat, bool showErrorBand, bool showInfo, SneakyCardCorner cardCorner, bool publishRegime, bool showIndicatorLabel)
		{
			return indicator.SneakyVolEnvelope_v0_2_0(Input, period, robustCenter, volModeParam, calibrationLookback, quantile, squeezePctile, bandwidthWindow, trendAdx, adxPeriod, showCone, forecastBars, coneFlat, showErrorBand, showInfo, cardCorner, publishRegime, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyVolEnvelope_v0_2_0 SneakyVolEnvelope_v0_2_0(ISeries<double> input , int period, bool robustCenter, VolMode volModeParam, int calibrationLookback, double quantile, double squeezePctile, int bandwidthWindow, int trendAdx, int adxPeriod, bool showCone, int forecastBars, bool coneFlat, bool showErrorBand, bool showInfo, SneakyCardCorner cardCorner, bool publishRegime, bool showIndicatorLabel)
		{
			return indicator.SneakyVolEnvelope_v0_2_0(input, period, robustCenter, volModeParam, calibrationLookback, quantile, squeezePctile, bandwidthWindow, trendAdx, adxPeriod, showCone, forecastBars, coneFlat, showErrorBand, showInfo, cardCorner, publishRegime, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Sensors.SneakyVolEnvelope_v0_2_0 SneakyVolEnvelope_v0_2_0(int period, bool robustCenter, VolMode volModeParam, int calibrationLookback, double quantile, double squeezePctile, int bandwidthWindow, int trendAdx, int adxPeriod, bool showCone, int forecastBars, bool coneFlat, bool showErrorBand, bool showInfo, SneakyCardCorner cardCorner, bool publishRegime, bool showIndicatorLabel)
		{
			return indicator.SneakyVolEnvelope_v0_2_0(Input, period, robustCenter, volModeParam, calibrationLookback, quantile, squeezePctile, bandwidthWindow, trendAdx, adxPeriod, showCone, forecastBars, coneFlat, showErrorBand, showInfo, cardCorner, publishRegime, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyVolEnvelope_v0_2_0 SneakyVolEnvelope_v0_2_0(ISeries<double> input , int period, bool robustCenter, VolMode volModeParam, int calibrationLookback, double quantile, double squeezePctile, int bandwidthWindow, int trendAdx, int adxPeriod, bool showCone, int forecastBars, bool coneFlat, bool showErrorBand, bool showInfo, SneakyCardCorner cardCorner, bool publishRegime, bool showIndicatorLabel)
		{
			return indicator.SneakyVolEnvelope_v0_2_0(input, period, robustCenter, volModeParam, calibrationLookback, quantile, squeezePctile, bandwidthWindow, trendAdx, adxPeriod, showCone, forecastBars, coneFlat, showErrorBand, showInfo, cardCorner, publishRegime, showIndicatorLabel);
		}
	}
}

#endregion
