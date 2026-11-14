// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
//
// Copyright (c) 2026 silentsudo-io and the Sentinel Suite contributors.
// ─────────────────────────────────────────────────────────────────────────────
//
// ═════════════════════════════════════════════════════════════════════════════
//  SNEAKY FLOW v1.0.0 — CVD regression: is order flow LEANING, and convincingly?
//
//  Publishes FlowState against SneakyCore's contract (seam v1.26.0):
//      Bias · Cvd · Slope · RSquared · Strength · Divergence · Signal
//
//  ── WHAT IT ADDS OVER PRESSURE ──────────────────────────────────────────────
//  PressureState answers "who is dominant RIGHT NOW" — a per-bar snapshot.
//  This answers "has flow been leaning, and is the lean coherent or noise?" It
//  fits an OLS regression to cumulative volume delta over a window and reports
//  the slope WITH its R². The R² is the whole point: a steep slope through a
//  scatter of noise is not a lean, and reporting slope without fit quality is
//  how you end up trading randomness that happened to trend.
//
//  Signal is Bias GATED by fit — it only carries a direction when R² and
//  strength both clear their thresholds. Bias is the always-on lean; Signal is
//  the one the Council votes on.
//
//  ── IT REUSES CLASSIFIED VOLUME RATHER THAN RE-CLASSIFYING ──────────────────
//  CVD needs signed volume, which needs each trade classified buy or sell.
//  BuySellVolumePressureMountain already does that, already publishes the result,
//  and already carries a TickBacked flag saying whether the classification came
//  from real ticks or an OHLC proxy. Re-implementing tick classification here
//  would duplicate that work AND risk the two disagreeing about the same tape.
//  So this consumes PressureState.BuyVolume/SellVolume and inherits its honesty
//  flag — if pressure fell back to the OHLC proxy, this says so too, because a
//  CVD built on a price-derived proxy is not order flow.
//
//  DEPENDENCY: SneakyBuySellVolumePressureMountain must be on the chart. Without
//  it there is no classified volume and this publishes nothing. The card says so
//  rather than quietly reporting a flat CVD.
//
//  ── DIVERGENCE ──────────────────────────────────────────────────────────────
//  Price making a higher high while CVD does not (or the mirror) — the classic
//  absorption read. Uses the house convention from FlowState's own docs:
//  "-1/0/+1 — price vs CVD swing divergence", signed by the PRICE swing.
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
using NinjaTrader.NinjaScript.AddOns.Sneaky;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors
{
	public class SneakyFlow_v1_0_0 : Indicator, ISneakyScrollCard
	{
		private string _scope;
		private string Scope()
		{
			try { _scope = SneakyCore.ScopeOf(Instrument, BarsPeriod, ChartControl); } catch { }   // 2026-08-04 — ALWAYS recomputed, never permanently cached: a chart's lane can register AFTER this indicator's first call, so caching once risked freezing a stale, unlaned scope forever (confirmed happening for real tonight). Cheap to recompute every call; only overwrites _scope on success, so a transient throw keeps the previous good value.
			return _scope;
		}

		private readonly List<double> _cvdWin = new List<double>();
		private readonly List<double> _slopeHist = new List<double>();
		private double _cvd, _slope, _r2, _strength;
		private int    _bias, _signal, _divergence;
		private bool   _haveSource, _tickBacked;
		private int    _sessionDay = -1;

		// swing tracking for divergence
		private double _lastSwingHigh = double.MinValue, _lastSwingLow = double.MaxValue;
		private double _cvdAtSwingHigh, _cvdAtSwingLow;
		private bool   _haveSwingHigh, _haveSwingLow;

		private SneakySkin.Painter _sp;
		private DateTime _lastRenderExLog = DateTime.MinValue;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "CVD regression — publishes FlowState. Answers 'has order flow been LEANING, and is the "
					+ "lean coherent or noise?' by fitting OLS to cumulative volume delta and reporting slope WITH its "
					+ "R-squared: a steep slope through scatter is not a lean, and slope without fit quality is how you "
					+ "trade randomness that happened to trend. Signal is Bias gated by fit and strength — that is the "
					+ "one the Council votes on. REQUIRES SneakyBuySellVolumePressureMountain on the chart: it consumes "
					+ "that sensor's already-classified volume rather than re-classifying ticks, and inherits its "
					+ "TickBacked flag, because a CVD built on an OHLC proxy is not order flow. Places no orders.";
				Name                     = "Sneaky Flow v1.0.0";
				Calculate                = Calculate.OnBarClose;
				IsOverlay                = false;
				DisplayInDataBox         = true;
				PaintPriceMarkers        = false;
				IsSuspendedWhileInactive = false;
				ShowIndicatorLabel       = false;

				RegWindow       = 40;
				MinRSquared     = 0.35;
				MinStrength     = 0.25;
				SlopeZWindow    = 120;
				ResetCvdOnSession = true;
				SwingStrength   = 5;
				StaleSec        = 90.0;  // was 5.0 — matches Trend/Intermarket/Eye's convention.
				PublishFlow     = true;

				ShowCard   = true;
				CardCorner = SneakyCardCorner.TopLeft;

				AddPlot(new Stroke(Brushes.DeepSkyBlue, 2), PlotStyle.Line, "Cvd");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Slope");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "RSquared");
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

			// ── session reset: CVD is a SESSION cumulative, per the contract ──
			if (ResetCvdOnSession && Bars.IsFirstBarOfSession)
			{
				_cvd = 0; _cvdWin.Clear(); _slopeHist.Clear();
				_haveSwingHigh = _haveSwingLow = false;
				_lastSwingHigh = double.MinValue; _lastSwingLow = double.MaxValue;
			}

			// ── classified volume from the pressure seam (see header: NOT re-classified) ──
			SneakyCore.PressureState prs = null;
			try { prs = SneakyCore.GetPressureState(Scope(), StaleSec); } catch { }

			_haveSource = prs != null;
			if (!_haveSource)
			{
				// No classified volume available. Publish nothing rather than a flat CVD
				// that would read as "no flow lean" when it actually means "no data".
				Values[0][0] = _cvd;
				return;
			}
			_tickBacked = prs.TickBacked;

			_cvd += (prs.BuyVolume - prs.SellVolume);

			_cvdWin.Add(_cvd);
			if (_cvdWin.Count > Math.Max(5, RegWindow)) _cvdWin.RemoveAt(0);

			// ── OLS fit of CVD against bar index ──
			int n = _cvdWin.Count;
			if (n >= 5)
			{
				double sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;
				for (int i = 0; i < n; i++)
				{
					double x = i, y = _cvdWin[i];
					sx += x; sy += y; sxx += x * x; sxy += x * y; syy += y * y;
				}
				double d = n * sxx - sx * sx;
				_slope = d != 0 ? (n * sxy - sx * sy) / d : 0.0;

				// R² — the gate. A slope without this number is not evidence.
				double denom = (n * sxx - sx * sx) * (n * syy - sy * sy);
				double num = (n * sxy - sx * sy);
				_r2 = denom > 0 ? (num * num) / denom : 0.0;
				if (_r2 > 1.0) _r2 = 1.0;

				// slope z-score over a longer memory, so "steep" is relative to this
				// instrument's own recent behaviour rather than an absolute number
				_slopeHist.Add(_slope);
				if (_slopeHist.Count > Math.Max(20, SlopeZWindow)) _slopeHist.RemoveAt(0);
				double z = 0.0;
				if (_slopeHist.Count > 10)
				{
					double m = 0; foreach (var v in _slopeHist) m += v; m /= _slopeHist.Count;
					double ss = 0; foreach (var v in _slopeHist) { double dd = v - m; ss += dd * dd; }
					double sd = Math.Sqrt(ss / _slopeHist.Count);
					z = sd > 0 ? (_slope - m) / sd : 0.0;
				}
				_strength = Math.Min(1.0, Math.Abs(z) * _r2);
				_bias = _slope > 0 ? 1 : _slope < 0 ? -1 : 0;
			}

			// BUGFIX (2026-08-22, P2 finding: Divergence field latches and never resets): _divergence
			// was only ever SET (to +1/-1) when a swing confirmed a price/CVD mismatch, and was never
			// reset back to neutral — so it stayed published as a stale "current bar" reading
			// indefinitely (even across sessions) until the opposite divergence happened to occur.
			// This does not touch Signal/Bias (the Council's vote), only the separately-published
			// Divergence field. Reset to neutral before each bar's re-evaluation below so a nonzero
			// value reflects only the bar it actually fired on.
			_divergence = 0;

			// ── divergence: price swing vs CVD at that swing ──
			int sw = Math.Max(2, SwingStrength);
			if (CurrentBar > sw * 2)
			{
				if (High[sw] == MAX(High, sw * 2 + 1)[0])
				{
					if (_haveSwingHigh && High[sw] > _lastSwingHigh && _cvd <= _cvdAtSwingHigh)
						_divergence = 1;    // higher price high, CVD did not confirm
					_lastSwingHigh = High[sw]; _cvdAtSwingHigh = _cvd; _haveSwingHigh = true;
				}
				if (Low[sw] == MIN(Low, sw * 2 + 1)[0])
				{
					if (_haveSwingLow && Low[sw] < _lastSwingLow && _cvd >= _cvdAtSwingLow)
						_divergence = -1;   // lower price low, CVD did not confirm
					_lastSwingLow = Low[sw]; _cvdAtSwingLow = _cvd; _haveSwingLow = true;
				}
			}

			// ── Signal = Bias GATED by fit. Bias is the always-on lean; Signal is the
			//    one the Council votes on, and it stays 0 until the fit earns it.
			_signal = (_r2 >= MinRSquared && _strength >= MinStrength) ? _bias : 0;

			Values[0][0] = _cvd;
			Values[1][0] = _slope;
			Values[2][0] = _r2;

			if (PublishFlow)
			{
				try
				{
					string sc = Scope();
					if (!string.IsNullOrEmpty(sc))
						// Object-passing setter, like SetIntermarketState (not an arg list).
						SneakyCore.SetFlowState(new SneakyCore.FlowState
						{
							Scope      = sc,
							Bartype    = SneakyCore.BarTag(BarsPeriod),
							Instrument = Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : null,
							Bias       = _bias,
							Cvd        = _cvd,
							Slope      = _slope,
							RSquared   = _r2,
							Strength   = _strength,
							Divergence = _divergence,
							Signal     = _signal,
							Source     = _tickBacked ? "SneakyFlow/tick" : "SneakyFlow/proxy"
						});
				}
				catch (Exception ex) { try { Print("[SneakyFlow] publish failed: " + ex.Message); } catch { } }
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
				{ _lastRenderExLog = DateTime.UtcNow; try { Print("[SneakyFlow] card threw: " + ex.Message); } catch { } }
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
			const float cw = 250f, ch = 170f;
			SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
			var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
				ChartControl.ChartPanels[0].X, ChartControl.ChartPanels[0].Y, ChartControl.ChartPanels[0].W, ChartControl.ChartPanels[0].H, cw, ch);
			if (slot.Width <= 0f) return;   // not the currently-selected card in the rotation

			var col = !_haveSource ? SneakySkin.CWarn
					: _signal > 0 ? SneakySkin.CUp
					: _signal < 0 ? SneakySkin.CDown : SneakySkin.CMute;

			var r = _sp.Card(slot.X, slot.Y, cw, ch, col);
			_sp.Dot(r.Left + 5f, r.Top + 8f, col, _signal != 0);
			_sp.Text("FLOW  (CVD regression)", r.Left + 16f, r.Top, r.Width - 20f, 16f, SneakySkin.CInk, 10.5f, true);

			if (!_haveSource)
			{
				_sp.Text("NO SOURCE", r.Left, r.Top + 24f, r.Width, 20f, SneakySkin.CWarn, 14f, true);
				_sp.Text("needs BuySellVolumePressureMountain on this chart",
					r.Left, r.Top + 50f, r.Width, 28f, SneakySkin.CWarn, 9f);
				return;
			}

			_sp.Text(_signal > 0 ? "FLOW UP (confirmed)" : _signal < 0 ? "FLOW DOWN (confirmed)"
					: _bias > 0 ? "leaning up (unconfirmed)" : _bias < 0 ? "leaning down (unconfirmed)" : "FLAT",
				r.Left, r.Top + 24f, r.Width, 20f, col, 13f, true);

			_sp.Text("cvd " + _cvd.ToString("#,0") + "    slope " + _slope.ToString("0.0"),
				r.Left, r.Top + 50f, r.Width, 14f, SneakySkin.CInk2, 10.5f);

			// R² is the number that decides whether the slope means anything, so it is
			// shown with its own threshold rather than buried.
			_sp.Text("R\u00b2 " + _r2.ToString("0.00") + " / " + MinRSquared.ToString("0.00")
					 + "    strength " + _strength.ToString("0.00") + " / " + MinStrength.ToString("0.00"),
				r.Left, r.Top + 68f, r.Width, 14f,
				(_r2 >= MinRSquared && _strength >= MinStrength) ? SneakySkin.CInk2 : SneakySkin.CMute, 9.5f);

			if (_divergence != 0)
				_sp.Text(_divergence > 0 ? "DIVERGENCE \u2014 higher high, CVD did not confirm"
										 : "DIVERGENCE \u2014 lower low, CVD did not confirm",
					r.Left, r.Top + 90f, r.Width, 14f, SneakySkin.CWarn, 9f, true);

			if (!_tickBacked)
				_sp.Text("OHLC PROXY \u2014 not true order flow",
					r.Left, r.Top + 112f, r.Width, 14f, SneakySkin.CWarn, 9.5f, true);

			_sp.Text("window " + RegWindow + " bars \u00b7 session cvd",
				r.Left, r.Top + 134f, r.Width, 14f, SneakySkin.CMute, 8f);
		}

		[Browsable(false)] [XmlIgnore] public Series<double> Cvd      => Values[0];
		[Browsable(false)] [XmlIgnore] public Series<double> Slope    => Values[1];
		[Browsable(false)] [XmlIgnore] public Series<double> RSquared => Values[2];

		#region Properties

		[NinjaScriptProperty] [Range(5, 1000)]
		[Display(Name = "Regression Window (bars)", GroupName = "1. Fit", Order = 0)]
		public int RegWindow { get; set; }

		[NinjaScriptProperty] [Range(0.0, 1.0)]
		[Display(Name = "Min R-Squared", Description = "Fit quality below which Signal stays 0 regardless of slope. This is the gate that separates a lean from noise that happened to trend.", GroupName = "1. Fit", Order = 1)]
		public double MinRSquared { get; set; }

		[NinjaScriptProperty] [Range(0.0, 1.0)]
		[Display(Name = "Min Strength", Description = "|slope z-score| x R-squared. Makes 'steep' relative to this instrument's own recent behaviour rather than an absolute number.", GroupName = "1. Fit", Order = 2)]
		public double MinStrength { get; set; }

		[NinjaScriptProperty] [Range(20, 2000)]
		[Display(Name = "Slope Z Window (bars)", GroupName = "1. Fit", Order = 3)]
		public int SlopeZWindow { get; set; }

		[NinjaScriptProperty] [Range(2, 50)]
		[Display(Name = "Swing Strength (bars)", Description = "Bars each side required to confirm a swing, for the price-vs-CVD divergence read.", GroupName = "1. Fit", Order = 4)]
		public int SwingStrength { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Reset CVD On Session", Description = "CVD is a SESSION cumulative per the seam contract. Turning this off makes it run continuously, which changes what the number means.", GroupName = "1. Fit", Order = 5)]
		public bool ResetCvdOnSession { get; set; }

		[NinjaScriptProperty] [Range(0.5, 300.0)]
		[Display(Name = "Seam Staleness (sec)", GroupName = "2. Sneaky", Order = 0)]
		public double StaleSec { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Publish Flow to Sneaky", GroupName = "2. Sneaky", Order = 1)]
		public bool PublishFlow { get; set; }

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
		private Sneaky.Sensors.SneakyFlow_v1_0_0[] cacheSneakyFlow_v1_0_0;
		public Sneaky.Sensors.SneakyFlow_v1_0_0 SneakyFlow_v1_0_0(int regWindow, double minRSquared, double minStrength, int slopeZWindow, int swingStrength, bool resetCvdOnSession, double staleSec, bool publishFlow, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyFlow_v1_0_0(Input, regWindow, minRSquared, minStrength, slopeZWindow, swingStrength, resetCvdOnSession, staleSec, publishFlow, showCard, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Sensors.SneakyFlow_v1_0_0 SneakyFlow_v1_0_0(ISeries<double> input, int regWindow, double minRSquared, double minStrength, int slopeZWindow, int swingStrength, bool resetCvdOnSession, double staleSec, bool publishFlow, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyFlow_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyFlow_v1_0_0.Length; idx++)
					if (cacheSneakyFlow_v1_0_0[idx] != null && cacheSneakyFlow_v1_0_0[idx].RegWindow == regWindow && cacheSneakyFlow_v1_0_0[idx].MinRSquared == minRSquared && cacheSneakyFlow_v1_0_0[idx].MinStrength == minStrength && cacheSneakyFlow_v1_0_0[idx].SlopeZWindow == slopeZWindow && cacheSneakyFlow_v1_0_0[idx].SwingStrength == swingStrength && cacheSneakyFlow_v1_0_0[idx].ResetCvdOnSession == resetCvdOnSession && cacheSneakyFlow_v1_0_0[idx].StaleSec == staleSec && cacheSneakyFlow_v1_0_0[idx].PublishFlow == publishFlow && cacheSneakyFlow_v1_0_0[idx].ShowCard == showCard && cacheSneakyFlow_v1_0_0[idx].CardCorner == cardCorner && cacheSneakyFlow_v1_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyFlow_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyFlow_v1_0_0[idx];
			return CacheIndicator<Sneaky.Sensors.SneakyFlow_v1_0_0>(new Sneaky.Sensors.SneakyFlow_v1_0_0(){ RegWindow = regWindow, MinRSquared = minRSquared, MinStrength = minStrength, SlopeZWindow = slopeZWindow, SwingStrength = swingStrength, ResetCvdOnSession = resetCvdOnSession, StaleSec = staleSec, PublishFlow = publishFlow, ShowCard = showCard, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyFlow_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Sensors.SneakyFlow_v1_0_0 SneakyFlow_v1_0_0(int regWindow, double minRSquared, double minStrength, int slopeZWindow, int swingStrength, bool resetCvdOnSession, double staleSec, bool publishFlow, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyFlow_v1_0_0(Input, regWindow, minRSquared, minStrength, slopeZWindow, swingStrength, resetCvdOnSession, staleSec, publishFlow, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyFlow_v1_0_0 SneakyFlow_v1_0_0(ISeries<double> input , int regWindow, double minRSquared, double minStrength, int slopeZWindow, int swingStrength, bool resetCvdOnSession, double staleSec, bool publishFlow, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyFlow_v1_0_0(input, regWindow, minRSquared, minStrength, slopeZWindow, swingStrength, resetCvdOnSession, staleSec, publishFlow, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Sensors.SneakyFlow_v1_0_0 SneakyFlow_v1_0_0(int regWindow, double minRSquared, double minStrength, int slopeZWindow, int swingStrength, bool resetCvdOnSession, double staleSec, bool publishFlow, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyFlow_v1_0_0(Input, regWindow, minRSquared, minStrength, slopeZWindow, swingStrength, resetCvdOnSession, staleSec, publishFlow, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyFlow_v1_0_0 SneakyFlow_v1_0_0(ISeries<double> input , int regWindow, double minRSquared, double minStrength, int slopeZWindow, int swingStrength, bool resetCvdOnSession, double staleSec, bool publishFlow, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyFlow_v1_0_0(input, regWindow, minRSquared, minStrength, slopeZWindow, swingStrength, resetCvdOnSession, staleSec, publishFlow, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
