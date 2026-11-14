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
using System.Linq;
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
using NinjaTrader.NinjaScript.AddOns.Sneaky;   // SneakySkin (glass card) + SneakyCore (AdxvmaState seam) + SneakyCardCorner
using NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors;
#endregion

// ═════════════════════════════════════════════════════════════════════════════
//  Sneaky ADXVMA — the ADX Volatility Moving Average axis (CLEAN-ROOM)      |   Version v1.0.0
//  File: SneakyADXVMA_v1_0_0.cs   |   namespace …Indicators.Sneaky.Sensors (Tier-③ SENSOR)   |   Name "Sneaky ADXVMA"
//
//  ⚠ NO ORDERS — read-only advisory indicator. Safe to run anywhere.
//
//  CLEAN-ROOM ORIGINAL. Written from scratch off the PUBLIC ADXVMA (ADX Volatility Moving Average)
//  method — a published, non-copyrightable adaptive-moving-average formula. It uses NO third-party code.
//  The installed AuADXVMA.cs / auADXVMASignalMod.cs (an unlicensed "Au" pack) were surveyed as design
//  references for the CONCEPT ONLY — the recursion below is a fresh implementation of the public formula;
//  no code was copied. See the provenance audit + NOTICE.
//
//  WHY IT MATTERS — a plain EMA lags equally in trend and in chop. ADXVMA drives its smoothing constant
//  from a Wilder-smoothed directional-volatility index: when direction is strong the MA snaps toward price;
//  when the tape is indecisive the MA nearly freezes. The result is a self-adaptive trend rail the Council
//  can lean with, plus a clean chop read (the MA goes flat) that context modulators care about.
//
//  THE PUBLIC FORMULA:
//    • up / down moves    : upMove = max(Close−Close[1], 0) · downMove = max(Close[1]−Close, 0)
//    • Wilder smooth (k=1/Period, i.e. rma): up  = (1−k)·up[1]  + k·upMove
//                                            down = (1−k)·down[1]+ k·downMove
//    • directional idx    : DI+ = up/(up+down) · DI− = down/(up+down)
//    • DX                 : |DI+ − DI−| / (DI+ + DI−)                                   [in 0…1]
//    • volatility index vi: Wilder-smoothed DX → vi = (1−k)·vi[1] + k·DX                 [in 0…1]
//    • adaptive MA        : adxvma = adxvma[1] + (vi^K)·(Close − adxvma[1])              [K≈2 sharpens response]
//    • TREND (trinary, ATR-deadband + hysteresis): let band = ATR(AtrLength)·DeadbandMult;
//        slope = adxvma − adxvma[1].  slope > +band → +1 (rising)  ·  slope < −band → −1 (falling) ·
//        inside the band → HOLD the last non-flat side (hysteresis carries the trend through minor
//        pullbacks) UNLESS the MA has gone genuinely flat over the ATR window → 0 (CHOP).
//        A reversal must therefore cross the OPPOSITE band, never flip +1↔−1 directly.
//    • Bias / Signal      = that trinary trend (−1/0/+1). STATE voter (always a reading; neutral in chop).
//
//  THE SENTINEL PLUMBING (our own code — makes it a suite member):
//    • PUBLISHES SneakyCore.AdxvmaState (Bias / Value = the MA / Signal).
//    • WIRED INTO THE COUNCIL as the ADXVMA voter (a STATE voter on AdxvmaState.Signal).
//    • Hidden ±1 "Signal" PLOT (Values[1], transparent) for the Deck SIGNAL ARM / generic consumers.
//    • Trend-colored adaptive-MA line on the price panel + a SneakySkin.Painter glass card +
//      label remover + roster heartbeat.
//
//  CHANGELOG
//    v1.0.0 (2026-07-12) — NEW. Clean-room ADXVMA axis (Wilder directional-volatility index → vi^K adaptive
//             MA + ATR-deadband hysteresis trend). AdxvmaState publish, Council ADXVMA voter, hidden Signal
//             plot, trend-colored MA line, glass card, scope key + heartbeat.
// ═════════════════════════════════════════════════════════════════════════════
namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors
{
	public class SneakyADXVMA_v1_0_0 : Indicator, ISneakyScrollCard
	{
		private SneakySkin.Painter _sp;
		private int _lastRenderExLog = 0;   // FIXED 2026-08-03 — was int.MinValue, which overflows in unchecked(TickCount - X) and permanently evaluates negative, silently blocking every exception log for the whole session. 0 is a safe sentinel: TickCount for any system up more than a few seconds is comfortably past any real threshold on the first check.
		private bool _hasData;

		// internal recursive buffers (public-formula ADXVMA)
		private Series<double> _up;
		private Series<double> _down;
		private Series<double> _vi;

		// cached state (computed in OnBarUpdate; drawn in OnRender)
		private int    _bias;          // -1/0/+1 trend
		private double _value;         // the adaptive MA
		private double _vidx;          // volatility index vi (0..1) — shown on the card
		private int    _sig;           // = bias
		private int    _lastLoggedSig = -999;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description              = "Clean-room ADX Volatility Moving Average: a Wilder directional-volatility index drives the smoothing constant of an adaptive MA (adxvma = adxvma[1] + vi^K·(Close−adxvma[1])), with an ATR-deadband + hysteresis trinary trend. Publishes SneakyCore.AdxvmaState (bias / MA value / trend Signal) so the Council gains an adaptive-MA voter.";
				Name                     = "Sneaky ADXVMA v1.0.0";
				Calculate                = Calculate.OnBarClose;
				IsOverlay                = true;
				DisplayInDataBox         = true;
				DrawOnPricePanel         = true;
				DrawHorizontalGridLines  = false;
				DrawVerticalGridLines    = false;
				PaintPriceMarkers        = true;
				ScaleJustification       = NinjaTrader.Gui.Chart.ScaleJustification.Right;
				IsSuspendedWhileInactive = true;

				Period       = 14;
				K            = 2.0;
				AtrLength    = 14;
				DeadbandMult = 0.10;

				PublishState       = true;
				LogChanges         = true;
				ShowCard           = true;
				CardCorner         = SneakyCardCorner.TopRight;
				ShowIndicatorLabel = false;

				// visible adaptive-MA line (recolored per bar by trend)
				AddPlot(new Stroke(Brushes.Goldenrod, 2), PlotStyle.Line, "Adxvma");
				// hidden ±1 trend signal (Values[1]) — transparent; readable by the Deck SIGNAL ARM.
				AddPlot(new Stroke(Brushes.Transparent, 1f), PlotStyle.Line, "Signal");
			}
			else if (State == State.Configure)
			{
				_up   = new Series<double>(this);
				_down = new Series<double>(this);
				_vi   = new Series<double>(this);
			}
			else if (State == State.DataLoaded)
			{
				if (!ShowIndicatorLabel) Name = string.Empty;   // Sneaky label remover
			}
			else if (State == State.Terminated)
			{
				if (_sp != null) { try { _sp.Dispose(); } catch { } _sp = null; }
				try { SneakySkin.CardLayout.Release(this); } catch { }
			}
		}

		// ── scope key ("<masterInstrument>.<barTag>" — ONE CHART's worth of context). Lazily resolved + cached. ──
		private string _scope;
		private string Scope()
		{
			try { _scope = SneakyCore.ScopeOf(Instrument, BarsPeriod, ChartControl); } catch { }   // 2026-08-04 — ALWAYS recomputed, never permanently cached: a chart's lane can register AFTER this indicator's first call, so caching once risked freezing a stale, unlaned scope forever (confirmed happening for real tonight). Cheap to recompute every call; only overwrites _scope on success, so a transient throw keeps the previous good value.
			return _scope;
		}

		// ── HEARTBEAT — re-stamp the cached seam on incoming quotes so a healthy voter doesn't age out of the
		//    Council roster in a quiet market. No recompute, realtime only, throttled. ──
		private DateTime _lastHeartbeatUtc;
		private const double HeartbeatSec = 5.0;
		protected override void OnMarketData(NinjaTrader.Data.MarketDataEventArgs marketDataUpdate)
		{
			if (!PublishState || State != State.Realtime) return;
			DateTime now = DateTime.UtcNow;
			if ((now - _lastHeartbeatUtc).TotalSeconds < HeartbeatSec) return;
			_lastHeartbeatUtc = now;
			try { SneakyCore.TouchAdxvmaState(Scope()); } catch { }
		}

		private int _trendPrev;   // last emitted trinary trend (for hysteresis)

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0) return;

			double k = 1.0 / Math.Max(1, Period);

			// ── warmup / seed ──
			if (CurrentBar < 1)
			{
				_up[0] = 0; _down[0] = 0; _vi[0] = 0;
				Adxvma[0] = Close[0];
				Signal[0] = 0;
				return;
			}

			// ── 1) Wilder-smoothed up/down moves ──
			double cu = Math.Max(Close[0] - Close[1], 0);
			double cd = Math.Max(Close[1] - Close[0], 0);
			_up[0]   = (1 - k) * _up[1]   + k * cu;
			_down[0] = (1 - k) * _down[1] + k * cd;

			// ── 2) directional index → DX (0..1) ──
			double s       = _up[0] + _down[0];
			double diPlus  = s > 1e-12 ? _up[0]   / s : 0;
			double diMinus = s > 1e-12 ? _down[0] / s : 0;
			double dsum    = diPlus + diMinus;
			double dx      = dsum > 1e-12 ? Math.Abs(diPlus - diMinus) / dsum : 0;

			// ── 3) Wilder-smoothed volatility index vi (0..1) ──
			_vi[0] = (1 - k) * _vi[1] + k * dx;
			double vi = _vi[0]; if (vi < 0) vi = 0; else if (vi > 1) vi = 1;

			// ── 4) adaptive MA: smoothing constant = vi^K ──
			double sc = Math.Pow(vi, K);
			Adxvma[0] = Adxvma[1] + sc * (Close[0] - Adxvma[1]);

			// ── 5) trinary trend: ATR deadband + hysteresis hold ──
			double band  = ATR(AtrLength)[0] * DeadbandMult;
			double slope = Adxvma[0] - Adxvma[1];

			int trend = _trendPrev;                 // default: HOLD the last non-flat side (hysteresis)
			if (slope > band)        trend = 1;     // rising past the band
			else if (slope < -band)  trend = -1;    // falling past the band
			else
			{
				// inside the deadband: only call CHOP if the MA has gone genuinely flat over the ATR window;
				// otherwise keep holding the last side through the minor pullback.
				int look = Math.Min(CurrentBar, AtrLength);
				double drift = Adxvma[0] - Adxvma[look];
				if (Math.Abs(drift) <= band) trend = 0;
			}
			_trendPrev = trend;

			// trend-colored MA line
			PlotBrushes[0][0] = trend > 0 ? Brushes.LimeGreen : (trend < 0 ? Brushes.Crimson : Brushes.Goldenrod);

			int sig = trend;
			Signal[0] = sig;

			_bias = trend; _value = Adxvma[0]; _vidx = vi; _sig = sig; _hasData = true;

			// BUGFIX (2026-08-22, P2 finding: ADXVMA publish warm-up): no warm-up gate meant every attach
			// published a real Council vote computed from a still-converging accumulator — _up/_down and
			// vi are Wilder-smoothed recursions seeded at 0 (same shape as Wilder's DI→DX→ADX chain: vi
			// here IS that ADX-equivalent line), and Adxvma[0] itself recurses on vi^K. None of that is
			// settled in the first ~10-20 bars. Gate the publish on CurrentBar < 2*Period — the standard
			// Wilder convergence window (mirrors the "25 ≈ 2×ADX-period" warmup used elsewhere in this
			// suite for the same Period=14 default) — so Council never sees a vote before this indicator's
			// own accumulator has actually converged. Everything above (the recursion itself, Signal[0],
			// plot coloring) still runs every bar during warm-up; only the publish is suppressed.
			if (PublishState && CurrentBar >= 2 * Period && Instrument != null && Instrument.MasterInstrument != null)
			{
				try
				{
					SneakyCore.SetAdxvmaState(new SneakyCore.AdxvmaState
					{
						Scope      = Scope(),
						Bartype    = SneakyCore.BarTag(BarsPeriod),
						Instrument = Instrument.MasterInstrument.Name,
						Bias       = trend,
						Value      = Adxvma[0],
						Signal     = sig,
						Source     = "AVMA"
					});
				}
				catch { }
			}

			if (LogChanges && State == State.Realtime && sig != _lastLoggedSig)
			{
				_lastLoggedSig = sig;
				try
				{
					string inst = Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : "?";
					SneakyCore.Log("AVMA", inst + " " +
						(sig > 0 ? "trend ▲ (rising)" : sig < 0 ? "trend ▼ (falling)" : "chop ~") +
						" ma=" + Adxvma[0].ToString("0.##") + " vi=" + vi.ToString("0.00"));
				}
				catch { }
			}
		}

		// ── glass card (the MA line renders via NT's normal overlay plot) ──
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
					try { Print("[SneakyADXVMA] RenderCard() threw: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); } catch { }
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
			const float cw = 228f, ch = 132f;
			SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
			var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
				ChartControl.ChartPanels[0].X, ChartControl.ChartPanels[0].Y, ChartControl.ChartPanels[0].W, ChartControl.ChartPanels[0].H, cw, ch);
			if (slot.Width <= 0f) return;   // not the currently-selected card in the rotation

			if (!_hasData)
			{
				var rw = _sp.Card(slot.X, slot.Y, cw, ch, SneakySkin.CLine);
				_sp.Dot(rw.Left + 5f, rw.Top + 8f, SneakySkin.CMute, false);
				_sp.Text("ADXVMA", rw.Left + 16f, rw.Top, rw.Width - 20f, 16f, SneakySkin.CInk, 11f, true);
				_sp.Text("warming up…", rw.Left, rw.Top + 26f, rw.Width, 14f, SneakySkin.CMute, 10.5f);
				return;
			}

			var trail   = SharpDX.DirectWrite.TextAlignment.Trailing;
			bool live   = _sig != 0;
			var dirCol  = _bias > 0 ? SneakySkin.CUp : _bias < 0 ? SneakySkin.CDown : SneakySkin.CMute;
			var heroCol = live ? dirCol : SneakySkin.CMute;
			var edge    = live ? SneakySkin.CAccent : SneakySkin.CLine;
			var r = _sp.Card(slot.X, slot.Y, cw, ch, edge);

			_sp.Dot(r.Left + 5f, r.Top + 8f, live ? SneakySkin.CAccent : SneakySkin.CMute, live);
			_sp.Text("ADXVMA", r.Left + 16f, r.Top, r.Width - 70f, 16f, SneakySkin.CInk, 11f, true);
			_sp.Pill(_bias > 0 ? "UP" : _bias < 0 ? "DOWN" : "CHOP", r.Right, r.Top - 1f, dirCol);

			_sp.Text("ADAPTIVE TREND", r.Left, r.Top + 24f, 140f, 12f, SneakySkin.CMute, 8.5f, true);
			_sp.Text(_bias > 0 ? "UP ▲" : _bias < 0 ? "DOWN ▼" : "chop",
				r.Left, r.Top + 34f, r.Width, 24f, heroCol, 17f, false);

			_sp.Divider(r.Left, r.Top + 66f, r.Right);
			_sp.Text("ma " + _value.ToString("0.##"), r.Left, r.Top + 72f, r.Width, 14f, SneakySkin.CInk2, 10f);
			_sp.Text("vi " + _vidx.ToString("0.00"), r.Left, r.Top + 72f, r.Width, 14f, SneakySkin.CInk2, 10f, true, trail);
			_sp.Text("volatility index → smoothing", r.Left, r.Top + 90f, r.Width, 14f, SneakySkin.CMute, 10f);
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Period", Description="Wilder smoothing period for the directional-volatility index (public default 14).", Order=1, GroupName="Parameters")]
		public int Period { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name="K", Description="Response exponent on the volatility index (smoothing constant = vi^K; K≈2 sharpens response).", Order=2, GroupName="Parameters")]
		public double K { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="ATR Length", Description="ATR length for the trend deadband.", Order=3, GroupName="Parameters")]
		public int AtrLength { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name="Deadband Mult", Description="Trend deadband = ATR(AtrLength) × this. The MA slope must exceed it to register a direction.", Order=4, GroupName="Parameters")]
		public double DeadbandMult { get; set; }

		// ── Sneaky ──
		[NinjaScriptProperty]
		[Display(Name="Publish ADXVMA to Sneaky", Description="Publish the adaptive-MA read as SneakyCore.AdxvmaState so the Council can vote on it.", Order=20, GroupName="Sneaky")]
		public bool PublishState { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Log Signal Changes", Description="Write trend-signal transitions to sneaky.log.", Order=21, GroupName="Sneaky")]
		public bool LogChanges { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Show Card", Order=22, GroupName="Sneaky")]
		public bool ShowCard { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Card Corner", Description="Which panel corner the Sneaky card docks to. Cards in the same corner auto-stack.", Order=23, GroupName="Sneaky")]
		public SneakyCardCorner CardCorner { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Show indicator label", Description="Show NinjaTrader's chart name label. Sneaky default = OFF; turn on to restore it.", Order=100, GroupName="Sneaky")]
		public bool ShowIndicatorLabel { get; set; }

		// ── plot series accessors ──
		[Browsable(false)] [XmlIgnore] public Series<double> Adxvma => Values[0];
		[Browsable(false)] [XmlIgnore] public Series<double> Signal => Values[1];   // ±1 trend / 0
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Sneaky.Sensors.SneakyADXVMA_v1_0_0[] cacheSneakyADXVMA_v1_0_0;
		public Sneaky.Sensors.SneakyADXVMA_v1_0_0 SneakyADXVMA_v1_0_0(int period, double k, int atrLength, double deadbandMult, bool publishState, bool logChanges, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyADXVMA_v1_0_0(Input, period, k, atrLength, deadbandMult, publishState, logChanges, showCard, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Sensors.SneakyADXVMA_v1_0_0 SneakyADXVMA_v1_0_0(ISeries<double> input, int period, double k, int atrLength, double deadbandMult, bool publishState, bool logChanges, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyADXVMA_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyADXVMA_v1_0_0.Length; idx++)
					if (cacheSneakyADXVMA_v1_0_0[idx] != null && cacheSneakyADXVMA_v1_0_0[idx].Period == period && cacheSneakyADXVMA_v1_0_0[idx].K == k && cacheSneakyADXVMA_v1_0_0[idx].AtrLength == atrLength && cacheSneakyADXVMA_v1_0_0[idx].DeadbandMult == deadbandMult && cacheSneakyADXVMA_v1_0_0[idx].PublishState == publishState && cacheSneakyADXVMA_v1_0_0[idx].LogChanges == logChanges && cacheSneakyADXVMA_v1_0_0[idx].ShowCard == showCard && cacheSneakyADXVMA_v1_0_0[idx].CardCorner == cardCorner && cacheSneakyADXVMA_v1_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyADXVMA_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyADXVMA_v1_0_0[idx];
			return CacheIndicator<Sneaky.Sensors.SneakyADXVMA_v1_0_0>(new Sneaky.Sensors.SneakyADXVMA_v1_0_0(){ Period = period, K = k, AtrLength = atrLength, DeadbandMult = deadbandMult, PublishState = publishState, LogChanges = logChanges, ShowCard = showCard, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyADXVMA_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Sensors.SneakyADXVMA_v1_0_0 SneakyADXVMA_v1_0_0(int period, double k, int atrLength, double deadbandMult, bool publishState, bool logChanges, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyADXVMA_v1_0_0(Input, period, k, atrLength, deadbandMult, publishState, logChanges, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyADXVMA_v1_0_0 SneakyADXVMA_v1_0_0(ISeries<double> input , int period, double k, int atrLength, double deadbandMult, bool publishState, bool logChanges, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyADXVMA_v1_0_0(input, period, k, atrLength, deadbandMult, publishState, logChanges, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Sensors.SneakyADXVMA_v1_0_0 SneakyADXVMA_v1_0_0(int period, double k, int atrLength, double deadbandMult, bool publishState, bool logChanges, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyADXVMA_v1_0_0(Input, period, k, atrLength, deadbandMult, publishState, logChanges, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyADXVMA_v1_0_0 SneakyADXVMA_v1_0_0(ISeries<double> input , int period, double k, int atrLength, double deadbandMult, bool publishState, bool logChanges, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyADXVMA_v1_0_0(input, period, k, atrLength, deadbandMult, publishState, logChanges, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
