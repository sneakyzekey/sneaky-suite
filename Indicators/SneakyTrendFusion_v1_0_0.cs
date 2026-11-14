// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
//
// Copyright (c) 2026 silentsudo-io and the Sentinel Suite contributors.
// ─────────────────────────────────────────────────────────────────────────────
//
// ═════════════════════════════════════════════════════════════════════════════
//  SNEAKY TREND FUSION v1.0.0 — MFI + CCI + CVD + Hurst regime + KAMA fan
//
//  ⚠ THIS IS NOT A PRISM PORT. READ THIS FIRST.
//  SneakyCore.TrendArchitectState (v1.29.0) was originally scaffolding for a real,
//  named, third-party MPL-licensed Pine Script called PRISM, meant to be ported as
//  SneakyTrendArchitect. At the time this file was first written (confirmed
//  2026-08-06), that port had not happened and "ARCH" did not appear in Council's
//  vote list. CORRECTION (2026-09-05): that stopped being true on 2026-08-07 —
//  SneakyTrendArchitect_v1_0_0.cs now exists, publishes TrendArchitectState with
//  PublishState=true by default, and SneakyCouncil_v1_0_0.cs has consumed it as a
//  declared "ARCH" voter ever since (see SneakyCouncil_v1_0_0.cs's own ARCH AddVote
//  comment, dated the same day). This paragraph was never updated to say so — the
//  exact stale-header problem SneakyCouncil_v1_0_0.cs's own header comment calls
//  out about itself. TFUS below remains a real, independently-useful, honestly-
//  original indicator in its own right (a different fusion of the same input
//  FAMILY, not a claim to reproduce PRISM's actual math) — it is simply no longer
//  true that ARCH/PRISM was abandoned or unwired. Both TFUS and ARCH vote in the
//  live Council today, on separate seams (TrendFusionState / TrendArchitectState).
//
//  ── WHAT IT ACTUALLY DOES ────────────────────────────────────────────────────
//  Five sub-reads, each reduced to -1/0/+1, majority-voted into one Bias:
//      MFI    sign of (Money Flow Index − 50) — NinjaTrader's built-in MFI
//      CCI    SneakyCore.CciState.Bias — REUSED, not re-derived (SneakyWoodies-
//             CCIPro already publishes this; recomputing it here would risk two
//             CCI readings quietly disagreeing about the same instrument, the
//             same reason SneakyFlow reuses Pressure and SneakyCVD reuses it too)
//      CVD    SneakyCore.CvdState.Dir — REUSED, same reasoning, same seam
//             SneakyCVD (built 2026-08-05) already publishes
//      HURST  a standard rescaled-range (R/S) exponent, computed fresh here
//             (public-domain statistical method, nothing proprietary about it —
//             see any textbook on fractal market analysis). H above the
//             trending threshold ⇒ direction comes from price's own slope over
//             the same window; H below the mean-reverting threshold, or
//             in between, contributes NOTHING (0), because Hurst alone never
//             claims to know which way a trending market is trending — only
//             that it IS one right now
//      KAMA   a 3-period Kaufman Adaptive Moving Average fan (Kaufman 1998,
//             public-domain formula), direction from whether the fast/mid/slow
//             lines are stacked in a consistent order relative to price
//  Bias only carries a direction once at least MajorityNeeded of the five agree
//  (default 3) — Agree reports the actual count, so "3 of 5" and "5 of 5" are
//  visibly different, not collapsed into one number.
//
//  ── HONEST STATUS ───────────────────────────────────────────────────────────
//  Brand new, unvalidated, same as every sensor in this suite at birth. The
//  Hurst/KAMA math is standard textbook formula, correctly implemented as far
//  as testing can confirm, but the FUSION rule (majority vote, threshold
//  choices) is a reasoned default, not backtested. Enters Council at weight 0.0
//  — same audition discipline already used for Pressure and Flux — and
//  Confluence's gate ships off. Validate with grade_sneaky.py before trusting.
//
//  Places no orders.
// ═════════════════════════════════════════════════════════════════════════════

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.AddOns.Sneaky;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors
{
	public class SneakyTrendFusion_v1_0_0 : Indicator, ISneakyScrollCard
	{
		private string _scope;
		private string Scope()
		{
			try { _scope = SneakyCore.ScopeOf(Instrument, BarsPeriod, ChartControl); } catch { }   // ALWAYS recomputed, never cached — same lane reasoning as every file in this suite.
			return _scope;
		}

		// ── KAMA fan state (one running value per period, updated bar over bar) ──
		private double _kamaFast = double.NaN, _kamaMid = double.NaN, _kamaSlow = double.NaN;
		private const double KamaFastestSc = 2.0 / (2.0 + 1.0);    // Kaufman's standard fast constant (period 2)
		private const double KamaSlowestSc = 2.0 / (30.0 + 1.0);   // Kaufman's standard slow constant (period 30)

		private MFI _mfi;

		private int    _bias, _agree, _mfiDir, _cciDir, _cvdDir, _hurstDir, _hurstRegime, _kamaDir;
		private double _hurstValue;

		private SneakySkin.Painter _sp;
		private DateTime _lastRenderExLog = DateTime.MinValue;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "TREND FUSION — publishes TrendFusionState. NOT a port of PRISM (see this file's "
					+ "header for why) — an original design fusing the same family of inputs: MFI, CCI (reused "
					+ "from SneakyWoodiesCCIPro's CciState), CVD (reused from SneakyCVD's CvdState), a standard "
					+ "rescaled-range Hurst exponent, and a 3-period KAMA fan. Bias only carries a direction once "
					+ "a majority of the five agree. Enters Council at weight 0.0 — brand new, ungraded. Places "
					+ "no orders.";
				Name                     = "Sneaky Trend Fusion v1.0.0";
				Calculate                = Calculate.OnBarClose;
				IsOverlay                = false;
				DisplayInDataBox         = true;
				PaintPriceMarkers        = false;
				IsSuspendedWhileInactive = false;
				ShowIndicatorLabel       = false;

				MfiPeriod         = 14;
				CciKamaPeriod     = 10;    // efficiency-ratio lookback for the FAST kama line
				KamaMidPeriod     = 20;
				KamaSlowPeriod    = 40;
				HurstWindow       = 100;
				HurstTrendAbove   = 0.55;
				HurstRevertBelow  = 0.45;
				MajorityNeeded    = 3;
				StaleSec          = 90.0;
				PublishTrendFusion = true;

				ShowCard   = true;
				CardCorner = SneakyCardCorner.TopLeft;

				AddPlot(new Stroke(Brushes.Goldenrod, 2), PlotStyle.Line, "Bias");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Hurst");
			}
			else if (State == State.DataLoaded)
			{
				if (!ShowIndicatorLabel) Name = string.Empty;
				_mfi = MFI(MfiPeriod);
			}
			else if (State == State.Terminated)
			{
				if (_sp != null) { try { _sp.Dispose(); } catch { } _sp = null; }
				try { SneakySkin.CardLayout.Release(this); } catch { }
			}
		}

		// ── Kaufman Adaptive Moving Average — Kaufman (1998), public-domain formula.
		//    `prev` is the running KAMA value for THIS period's line; `erPeriod` is how far
		//    back the efficiency ratio looks. Returns the new KAMA value for this bar. ──
		private double Kama(double prev, int erPeriod)
		{
			if (CurrentBar < erPeriod) return Close[0];
			double change = Math.Abs(Close[0] - Close[erPeriod]);
			double volatility = 0.0;
			for (int i = 0; i < erPeriod; i++) volatility += Math.Abs(Close[i] - Close[i + 1]);
			double er = volatility > 0 ? change / volatility : 0.0;
			double sc = Math.Pow(er * (KamaFastestSc - KamaSlowestSc) + KamaSlowestSc, 2.0);
			if (double.IsNaN(prev)) return Close[0];
			return prev + sc * (Close[0] - prev);
		}

		// ── Hurst exponent via a single-scale rescaled-range (R/S) estimate over `window`
		//    log-returns. Standard, textbook, public-domain method — NOT multi-scale
		//    regression (that's more accurate but heavier; this lightweight single-scale
		//    form is the same simplification most free/public Hurst indicators use). ──
		private double HurstRs(int window)
		{
			if (CurrentBar < window + 1) return 0.5;   // 0.5 = "don't know yet" (random-walk default)
			var rets = new List<double>(window);
			for (int i = 0; i < window; i++)
			{
				double c0 = Close[i], c1 = Close[i + 1];
				if (c1 > 0 && c0 > 0) rets.Add(Math.Log(c0 / c1));
			}
			if (rets.Count < 10) return 0.5;
			double mean = 0; foreach (var r in rets) mean += r; mean /= rets.Count;
			double cum = 0, maxCum = double.MinValue, minCum = double.MaxValue;
			double ss = 0;
			foreach (var r in rets)
			{
				cum += (r - mean);
				if (cum > maxCum) maxCum = cum;
				if (cum < minCum) minCum = cum;
				ss += (r - mean) * (r - mean);
			}
			double range = maxCum - minCum;
			double sd = Math.Sqrt(ss / rets.Count);
			if (sd <= 0 || range <= 0) return 0.5;
			double rs = range / sd;
			double h = Math.Log(rs) / Math.Log(rets.Count);
			if (double.IsNaN(h) || double.IsInfinity(h)) return 0.5;
			return Math.Max(0.0, Math.Min(1.0, h));
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < Math.Max(KamaSlowPeriod, HurstWindow) + 2) return;

			// ── KAMA fan ──
			_kamaFast = Kama(_kamaFast, CciKamaPeriod);
			_kamaMid  = Kama(_kamaMid, KamaMidPeriod);
			_kamaSlow = Kama(_kamaSlow, KamaSlowPeriod);
			bool bullStack = Close[0] > _kamaFast && _kamaFast > _kamaMid && _kamaMid > _kamaSlow;
			bool bearStack = Close[0] < _kamaFast && _kamaFast < _kamaMid && _kamaMid < _kamaSlow;
			_kamaDir = bullStack ? 1 : bearStack ? -1 : 0;

			// ── Hurst ──
			_hurstValue = HurstRs(HurstWindow);
			_hurstRegime = _hurstValue >= HurstTrendAbove ? 1 : _hurstValue <= HurstRevertBelow ? -1 : 0;
			if (_hurstRegime > 0)
			{
				double priceChange = Close[0] - Close[HurstWindow];
				_hurstDir = priceChange > 0 ? 1 : priceChange < 0 ? -1 : 0;
			}
			else _hurstDir = 0;   // not trending (or unknown) ⇒ Hurst alone has no opinion on direction

			// ── MFI ──
			try { _mfiDir = _mfi[0] > 50 ? 1 : _mfi[0] < 50 ? -1 : 0; } catch { _mfiDir = 0; }

			// ── CCI, CVD — REUSED from their own published seams, not re-derived ──
			SneakyCore.CciState cci = null; SneakyCore.CvdState cvd = null;
			try { cci = SneakyCore.GetCciState(Scope(), StaleSec); } catch { }
			try { cvd = SneakyCore.GetCvdState(Scope(), StaleSec); } catch { }
			_cciDir = cci != null ? cci.Bias : 0;
			_cvdDir = cvd != null ? cvd.Dir : 0;

			// ── Fusion: majority vote across the five ──
			int up = 0, down = 0;
			foreach (int d in new[] { _mfiDir, _cciDir, _cvdDir, _hurstDir, _kamaDir })
			{
				if (d > 0) up++; else if (d < 0) down++;
			}
			_agree = Math.Max(up, down);
			_bias = (up >= MajorityNeeded && up > down) ? 1
				  : (down >= MajorityNeeded && down > up) ? -1 : 0;

			Values[0][0] = _bias;
			Values[1][0] = _hurstValue;

			if (PublishTrendFusion)
			{
				try
				{
					string sc = Scope();
					if (!string.IsNullOrEmpty(sc))
						SneakyCore.SetTrendFusionState(new SneakyCore.TrendFusionState
						{
							Scope      = sc,
							Bartype    = SneakyCore.BarTag(BarsPeriod),
							Instrument = Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : null,
							Bias       = _bias, Agree = _agree,
							MfiDir     = _mfiDir, CciDir = _cciDir, CvdDir = _cvdDir,
							HurstDir   = _hurstDir, HurstValue = _hurstValue, HurstRegime = _hurstRegime,
							KamaDir    = _kamaDir,
							Source     = "SneakyTrendFusion"
						});
				}
				catch (Exception ex) { try { Print("[SneakyTrendFusion] publish failed: " + ex.Message); } catch { } }
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
				{ _lastRenderExLog = DateTime.UtcNow; try { Print("[SneakyTrendFusion] card threw: " + ex.Message); } catch { } }
			}
			try { _sp.End(); } catch { }
		}
		public void ConductorRender(SharpDX.Direct2D1.RenderTarget rt)
		{
			if (!(ShowCard)) return;
			if (rt == null) return;
			try { if (_sp == null) _sp = new SneakySkin.Painter(); _sp.Begin(rt); RenderCard(); }
			catch { }
			finally { try { _sp.End(); } catch { } }
		}

		private void RenderCard()
		{
			const float cw = 250f, ch = 158f;
			SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
			var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
				ChartControl.ChartPanels[0].X, ChartControl.ChartPanels[0].Y, ChartControl.ChartPanels[0].W, ChartControl.ChartPanels[0].H, cw, ch);
			if (slot.Width <= 0f) return;

			var col = _bias > 0 ? SneakySkin.CUp : _bias < 0 ? SneakySkin.CDown : SneakySkin.CMute;
			var r = _sp.Card(slot.X, slot.Y, cw, ch, col);
			_sp.Dot(r.Left + 5f, r.Top + 8f, col, _bias != 0);
			_sp.Text("TREND FUSION  (not PRISM)", r.Left + 16f, r.Top, r.Width - 20f, 16f, SneakySkin.CInk, 10.5f, true);

			_sp.Text(_bias > 0 ? "UP" : _bias < 0 ? "DOWN" : "NO MAJORITY", r.Left, r.Top + 24f, r.Width, 20f, col, 14f, true);
			_sp.Text("agree " + _agree + "/5  (need " + MajorityNeeded + ")", r.Left, r.Top + 50f, r.Width, 14f, SneakySkin.CInk2, 10.5f);

			string chip(string label, int d) => label + (d > 0 ? "\u2191" : d < 0 ? "\u2193" : "\u2013");
			_sp.Text(chip("MFI", _mfiDir) + "   " + chip("CCI", _cciDir) + "   " + chip("CVD", _cvdDir),
				r.Left, r.Top + 70f, r.Width, 14f, SneakySkin.CInk2, 10f);
			_sp.Text(chip("KAMA", _kamaDir) + "   Hurst " + _hurstValue.ToString("0.00")
					+ (_hurstRegime > 0 ? " (trending)" : _hurstRegime < 0 ? " (reverting)" : " (neither)"),
				r.Left, r.Top + 88f, r.Width, 14f, SneakySkin.CInk2, 10f);

			_sp.Text("original fusion \u00b7 not a reconstruction of any 3rd-party script",
				r.Left, r.Top + 134f, r.Width, 14f, SneakySkin.CMute, 8f);
		}

		[Browsable(false)] [XmlIgnore] public Series<double> Bias  => Values[0];
		[Browsable(false)] [XmlIgnore] public Series<double> Hurst => Values[1];

		#region Properties

		[NinjaScriptProperty] [Range(2, 200)]
		[Display(Name = "MFI Period", GroupName = "1. Computation", Order = 0)]
		public int MfiPeriod { get; set; }

		[NinjaScriptProperty] [Range(2, 200)]
		[Display(Name = "KAMA Fan — Fast (ER lookback)", GroupName = "1. Computation", Order = 1)]
		public int CciKamaPeriod { get; set; }

		[NinjaScriptProperty] [Range(2, 300)]
		[Display(Name = "KAMA Fan — Mid (ER lookback)", GroupName = "1. Computation", Order = 2)]
		public int KamaMidPeriod { get; set; }

		[NinjaScriptProperty] [Range(2, 500)]
		[Display(Name = "KAMA Fan — Slow (ER lookback)", GroupName = "1. Computation", Order = 3)]
		public int KamaSlowPeriod { get; set; }

		[NinjaScriptProperty] [Range(20, 1000)]
		[Display(Name = "Hurst Window (bars)", Description = "Log-return lookback for the rescaled-range Hurst estimate.", GroupName = "1. Computation", Order = 4)]
		public int HurstWindow { get; set; }

		[NinjaScriptProperty] [Range(0.5, 1.0)]
		[Display(Name = "Hurst Trending Above", GroupName = "1. Computation", Order = 5)]
		public double HurstTrendAbove { get; set; }

		[NinjaScriptProperty] [Range(0.0, 0.5)]
		[Display(Name = "Hurst Mean-Reverting Below", GroupName = "1. Computation", Order = 6)]
		public double HurstRevertBelow { get; set; }

		[NinjaScriptProperty] [Range(1, 5)]
		[Display(Name = "Majority Needed (of 5)", Description = "How many of the five sub-reads must agree before Bias carries a direction.", GroupName = "1. Computation", Order = 7)]
		public int MajorityNeeded { get; set; }

		[NinjaScriptProperty] [Range(0.5, 300.0)]
		[Display(Name = "Seam Staleness (sec)", GroupName = "2. Sneaky", Order = 0)]
		public double StaleSec { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Publish Trend Fusion to Sneaky", Description = "SCOPE-keyed. Publishes SneakyCore.TrendFusionState — a NEW seam, unrelated to TrendArchitectState.", GroupName = "2. Sneaky", Order = 1)]
		public bool PublishTrendFusion { get; set; }

		[NinjaScriptProperty] [Display(Name = "Show Card", GroupName = "3. Display", Order = 0)] public bool ShowCard { get; set; }
		[NinjaScriptProperty] [Display(Name = "Card Corner", GroupName = "3. Display", Order = 1)] public SneakyCardCorner CardCorner { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show Indicator Label", GroupName = "3. Display", Order = 2)] public bool ShowIndicatorLabel { get; set; }

		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Sneaky.Sensors.SneakyTrendFusion_v1_0_0[] cacheSneakyTrendFusion_v1_0_0;
		public Sneaky.Sensors.SneakyTrendFusion_v1_0_0 SneakyTrendFusion_v1_0_0(int mfiPeriod, int cciKamaPeriod, int kamaMidPeriod, int kamaSlowPeriod, int hurstWindow, double hurstTrendAbove, double hurstRevertBelow, int majorityNeeded, double staleSec, bool publishTrendFusion, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyTrendFusion_v1_0_0(Input, mfiPeriod, cciKamaPeriod, kamaMidPeriod, kamaSlowPeriod, hurstWindow, hurstTrendAbove, hurstRevertBelow, majorityNeeded, staleSec, publishTrendFusion, showCard, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Sensors.SneakyTrendFusion_v1_0_0 SneakyTrendFusion_v1_0_0(ISeries<double> input, int mfiPeriod, int cciKamaPeriod, int kamaMidPeriod, int kamaSlowPeriod, int hurstWindow, double hurstTrendAbove, double hurstRevertBelow, int majorityNeeded, double staleSec, bool publishTrendFusion, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyTrendFusion_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyTrendFusion_v1_0_0.Length; idx++)
					if (cacheSneakyTrendFusion_v1_0_0[idx] != null && cacheSneakyTrendFusion_v1_0_0[idx].MfiPeriod == mfiPeriod && cacheSneakyTrendFusion_v1_0_0[idx].CciKamaPeriod == cciKamaPeriod && cacheSneakyTrendFusion_v1_0_0[idx].KamaMidPeriod == kamaMidPeriod && cacheSneakyTrendFusion_v1_0_0[idx].KamaSlowPeriod == kamaSlowPeriod && cacheSneakyTrendFusion_v1_0_0[idx].HurstWindow == hurstWindow && cacheSneakyTrendFusion_v1_0_0[idx].HurstTrendAbove == hurstTrendAbove && cacheSneakyTrendFusion_v1_0_0[idx].HurstRevertBelow == hurstRevertBelow && cacheSneakyTrendFusion_v1_0_0[idx].MajorityNeeded == majorityNeeded && cacheSneakyTrendFusion_v1_0_0[idx].StaleSec == staleSec && cacheSneakyTrendFusion_v1_0_0[idx].PublishTrendFusion == publishTrendFusion && cacheSneakyTrendFusion_v1_0_0[idx].ShowCard == showCard && cacheSneakyTrendFusion_v1_0_0[idx].CardCorner == cardCorner && cacheSneakyTrendFusion_v1_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyTrendFusion_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyTrendFusion_v1_0_0[idx];
			return CacheIndicator<Sneaky.Sensors.SneakyTrendFusion_v1_0_0>(new Sneaky.Sensors.SneakyTrendFusion_v1_0_0(){ MfiPeriod = mfiPeriod, CciKamaPeriod = cciKamaPeriod, KamaMidPeriod = kamaMidPeriod, KamaSlowPeriod = kamaSlowPeriod, HurstWindow = hurstWindow, HurstTrendAbove = hurstTrendAbove, HurstRevertBelow = hurstRevertBelow, MajorityNeeded = majorityNeeded, StaleSec = staleSec, PublishTrendFusion = publishTrendFusion, ShowCard = showCard, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyTrendFusion_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Sensors.SneakyTrendFusion_v1_0_0 SneakyTrendFusion_v1_0_0(int mfiPeriod, int cciKamaPeriod, int kamaMidPeriod, int kamaSlowPeriod, int hurstWindow, double hurstTrendAbove, double hurstRevertBelow, int majorityNeeded, double staleSec, bool publishTrendFusion, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyTrendFusion_v1_0_0(Input, mfiPeriod, cciKamaPeriod, kamaMidPeriod, kamaSlowPeriod, hurstWindow, hurstTrendAbove, hurstRevertBelow, majorityNeeded, staleSec, publishTrendFusion, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyTrendFusion_v1_0_0 SneakyTrendFusion_v1_0_0(ISeries<double> input , int mfiPeriod, int cciKamaPeriod, int kamaMidPeriod, int kamaSlowPeriod, int hurstWindow, double hurstTrendAbove, double hurstRevertBelow, int majorityNeeded, double staleSec, bool publishTrendFusion, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyTrendFusion_v1_0_0(input, mfiPeriod, cciKamaPeriod, kamaMidPeriod, kamaSlowPeriod, hurstWindow, hurstTrendAbove, hurstRevertBelow, majorityNeeded, staleSec, publishTrendFusion, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Sensors.SneakyTrendFusion_v1_0_0 SneakyTrendFusion_v1_0_0(int mfiPeriod, int cciKamaPeriod, int kamaMidPeriod, int kamaSlowPeriod, int hurstWindow, double hurstTrendAbove, double hurstRevertBelow, int majorityNeeded, double staleSec, bool publishTrendFusion, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyTrendFusion_v1_0_0(Input, mfiPeriod, cciKamaPeriod, kamaMidPeriod, kamaSlowPeriod, hurstWindow, hurstTrendAbove, hurstRevertBelow, majorityNeeded, staleSec, publishTrendFusion, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyTrendFusion_v1_0_0 SneakyTrendFusion_v1_0_0(ISeries<double> input , int mfiPeriod, int cciKamaPeriod, int kamaMidPeriod, int kamaSlowPeriod, int hurstWindow, double hurstTrendAbove, double hurstRevertBelow, int majorityNeeded, double staleSec, bool publishTrendFusion, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyTrendFusion_v1_0_0(input, mfiPeriod, cciKamaPeriod, kamaMidPeriod, kamaSlowPeriod, hurstWindow, hurstTrendAbove, hurstRevertBelow, majorityNeeded, staleSec, publishTrendFusion, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
