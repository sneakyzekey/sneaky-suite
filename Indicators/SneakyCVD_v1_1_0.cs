// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
//
// Copyright (c) 2026 silentsudo-io and the Sentinel Suite contributors.
// ─────────────────────────────────────────────────────────────────────────────
//
// ═════════════════════════════════════════════════════════════════════════════
//  SNEAKY CVD v1.1.0 — session cumulative volume delta, ported from real upstream
//
//  v1.0.0 (built 2026-08-05) reused PressureState.BuyVolume/SellVolume rather than
//  re-classifying ticks — a defensible call, matching SneakyFlow's own "don't let
//  two things disagree about the same tape" principle, but it meant CVD could not
//  produce a reading unless SneakyBuySellVolumePressureMountain was also attached.
//
//  2026-08-06 — got hold of the REAL Sentinel Suite source (SentinelCVD_v1_0_0.cs).
//  Its design deliberately does NOT reuse Pressure — it has its OWN independent
//  quote-rule/tick-rule classifier, block-print winsorization, and a degrade-not-
//  abstain fallback when there is no tape at all. That is a genuinely more capable
//  design for a prop-firm feed that sometimes lacks true tick data (this account's
//  own situation), so v1.1.0 ports it wholesale rather than re-deriving it blind.
//  Credit: this computation is the real project's own, adapted from Sentinel
//  namespace/skin conventions to Sneaky's, not independently invented.
//
//  ── WHAT ACTUALLY CHANGED FROM v1.0.0 ────────────────────────────────────────
//  1. TICK CLASSIFICATION — no longer reads PressureState at all. OnMarketData
//     tracks live bid/ask and applies the QUOTE RULE (price at/above ask = buy,
//     at/below bid = sell) with the TICK RULE as fallback when no two-sided quote
//     is available. This makes CVD fully self-sufficient — SneakyBuySellVolume-
//     PressureMountain is no longer a dependency.
//  2. WINSORIZATION — one block print can no longer dominate a whole session's
//     delta. Each print's volume is capped at WinsorMult × its own EWMA.
//  3. NO-TAPE FALLBACK — if a session sees zero real prints (historical rebuild
//     without tick replay, or a feed with no tick data on this instrument), the
//     bar's delta is estimated from its OHLC body instead of going dark, and the
//     row is honestly flagged TickBacked=false — same shape number, degraded
//     confidence, rather than nothing at all.
//  4. DIVERGENCE STRENGTH GATE — v1.0.0 called it a divergence on direction
//     disagreement alone. v1.1.0 additionally requires |SlopeZ| >= DivergenceMin
//     — a marginal flow reading that barely cleared the deadband no longer counts.
//  5. CONTINUOUS EWMA instead of rolling-window lists for SlopeZ/EfficiencyZ —
//     O(1) per bar, no stored history, and it adapts continuously rather than
//     jumping when an old value ages out of a fixed window.
//  6. SCOPE — briefly considered switching to instrument-level (the real Council's
//     own source classifies CVD that way: "no chart of its own; requiring
//     attachment would be meaningless"). REJECTED, deliberately: this project
//     keeps every indicator chart-specific ON PURPOSE, so D/E/F can each run and
//     be graded as genuinely separate, independently-testable setups — a single
//     shared publisher defeats that regardless of how "correct" it looks from
//     upstream's own design intent. Scope stays lane-qualified (chart-specific),
//     unchanged from v1.0.0. Run one SneakyCVD per chart, same as everything else.
//
//  ── STILL TRUE FROM v1.0.0 ──────────────────────────────────────────────────
//  Not wired into Council or Confluence — still exists for SneakyEye specifically,
//  still deliberately avoiding the double-count problem that motivated the Eye
//  rebuild in the first place. Same CvdState contract, same field meanings.
//
//  ── HONEST STATUS ───────────────────────────────────────────────────────────
//  The COMPUTATION is the real project's own, battle-tested design — trust that
//  part more than a from-scratch build. The WIRING into this suite (Sneaky's
//  card system) is new tonight and
//  as unvalidated as everything else at birth. Re-check grade_sneaky.py's table
//  4 (Eye vs Council) once this has recorded real data, same as before.
//
//  Places no orders.
// ═════════════════════════════════════════════════════════════════════════════

#region Using declarations
using System;
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
	public class SneakyCVD_v1_1_0 : Indicator, ISneakyScrollCard
	{
		// v1.1.0 — lane-qualified, chart-specific scope. See header §6 for why an
		// instrument-level design was considered and rejected.
		// ALWAYS recomputed, never cached — same lane-registration reasoning as every
		// file in this suite (2026-08-04 sweep), just without the lane component here.
		private string Scope()
		{
			// 2026-08-06 — REVERTED to lane-qualified (chart-specific), same as every other file in
			// this suite. v1.1.0 briefly switched this to instrument-level (2-arg, no ChartControl),
			// matching the real upstream Council's own classification of CVD as having "no chart of
			// its own." That was the WRONG call for this project specifically: indicators here are
			// deliberately kept chart-specific on purpose, so multiple charts (D/E/F) can each run
			// and be graded as genuinely separate setups — a single shared instrument-level publisher
			// defeats that, no matter how "correct" it looks from upstream's own design intent.
			try { return SneakyCore.ScopeOf(Instrument, BarsPeriod, ChartControl); } catch { return null; }
		}

		// ── tape state (OnMarketData) ──
		private double _prevBid, _prevAsk, _lastPrice;
		private int    _lastTickSign;
		private bool   _sawTape;
		private double _volEwma;
		private double _cvd, _barDelta;

		// ── continuous EWMA slope/efficiency + their own variance trackers ──
		private double _slopeEma, _slopeVar;
		private double _effEma, _effVar;

		// ── session bookkeeping ──
		private int      _barsThisSession;
		private double   _sessHi, _sessLo;

		// ── cached display values (set in OnBarUpdate, only read in OnRender) ──
		private double _dSlope, _dSlopeZ, _dEff, _dEffZ, _dCvd;
		private int    _dDir, _dPriceDir, _dDiverge;
		private bool   _dTickBacked, _hasData;
		private int    _lastLoggedDir = -999;

		private SneakySkin.Painter _sp;
		private DateTime _lastRenderExLog = DateTime.MinValue;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "SESSION CUMULATIVE VOLUME DELTA — v1.1.0, ported from the real Sentinel Suite's "
					+ "own SentinelCVD (see this file's header for what changed from v1.0.0). Independent "
					+ "quote-rule/tick-rule tape classifier — no longer depends on any Pressure publisher. "
					+ "Publishes Slope/SlopeZ (direction), Divergence (flow vs price disagreement, strength-"
					+ "gated), and Efficiency (ticks of price per 1,000 contracts of net delta — market impact). "
					+ "Chart-specific scope (unchanged from v1.0.0): each chart runs its own instance, not "
					+ "per-chart. Built for SneakyEye's orthogonal evidence, not wired into Council/Confluence "
					+ "on purpose. Places no orders.";
				Name                     = "Sneaky CVD v1.1.0";
				Calculate                = Calculate.OnEachTick;   // real signing needs the tape; re-asserted in Configure below
				IsOverlay                = false;
				IsSuspendedWhileInactive = true;
				DisplayInDataBox         = true;
				DrawOnPricePanel         = false;
				IsAutoScale              = true;
				ShowIndicatorLabel       = false;

				SlopePeriod    = 14;
				Deadband       = 0.35;
				DivergenceMin  = 0.50;
				WinsorMult     = 4.0;

				PublishCvd  = true;
				LogChanges  = true;
				ShowCard    = true;
				CardCorner  = SneakyCardCorner.TopLeft;
				StaleSec    = 90.0;

				AddPlot(new Stroke(Brushes.DeepSkyBlue, 2), PlotStyle.Line, "Cvd");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Signal");
			}
			else if (State == State.Configure)
			{
				// Real signing needs the tape. Without OnEachTick here, historical loads
				// would silently degrade to bar-proxy for the whole corpus, not just the
				// sessions that genuinely lack tick data.
				Calculate = Calculate.OnEachTick;
			}
			else if (State == State.DataLoaded)
			{
				if (!ShowIndicatorLabel) Name = string.Empty;
				Plots[1].Brush = Brushes.Transparent;   // hidden ±1 signal series
			}
			else if (State == State.Terminated)
			{
				if (_sp != null) { try { _sp.Dispose(); } catch { } _sp = null; }
				try { SneakySkin.CardLayout.Release(this); } catch { }
				try { SneakyCore.ClearCvdScope(Scope()); } catch { }
			}
		}

		// ─────────────────────────────────────────────────────────────────────
		//  TAPE — quote rule first (a real bid/ask is the better estimator), tick rule
		//  as fallback. Ported verbatim from the real SentinelCVD — see header §1.
		// ─────────────────────────────────────────────────────────────────────
		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (e == null) return;
			try
			{
				if (e.MarketDataType == MarketDataType.Bid) { _prevBid = e.Price; return; }
				if (e.MarketDataType == MarketDataType.Ask) { _prevAsk = e.Price; return; }
				if (e.MarketDataType != MarketDataType.Last) return;

				_sawTape = true;
				double vol = e.Volume;
				if (vol <= 0) return;

				// Winsorize the print. One block print must not own the session's delta — see header §2.
				_volEwma = _volEwma <= 0 ? vol : _volEwma + 0.02 * (vol - _volEwma);
				double cap = _volEwma * WinsorMult;
				if (cap > 0 && vol > cap) vol = cap;

				int sign;
				if (_prevAsk > 0 && _prevBid > 0 && _prevAsk > _prevBid)
				{
					// QUOTE RULE — at/above ask = aggressive buy, at/below bid = aggressive sell.
					if (e.Price >= _prevAsk)      sign =  1;
					else if (e.Price <= _prevBid) sign = -1;
					else                          sign =  0;   // inside the spread: genuinely ambiguous, count neither
				}
				else
				{
					// TICK RULE fallback — direction of the price change, carrying the last sign on no change.
					if (_lastPrice > 0 && e.Price > _lastPrice)      sign =  1;
					else if (_lastPrice > 0 && e.Price < _lastPrice) sign = -1;
					else                                             sign = _lastTickSign;
				}
				if (sign != 0) _lastTickSign = sign;
				_lastPrice = e.Price;

				_cvd      += sign * vol;
				_barDelta += sign * vol;
			}
			catch (Exception ex) { try { Print("[SneakyCVD] OnMarketData failed: " + ex.Message); } catch { } }
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 2 || Instrument == null || Instrument.MasterInstrument == null) return;

			try
			{
				// BUGFIX (2026-08-22, P2 finding: session reset keyed on calendar-date rollover):
				// this used to reset on Times[0][0].Date != _sessionDate — i.e. midnight calendar-
				// date rollover — instead of the instrument's actual configured trading-session
				// boundary. Most futures sessions don't roll at midnight (e.g. 6pm-5pm next day),
				// so that spuriously split one real session's cumulative delta into two pieces
				// mid-session. Bars.IsFirstBarOfSession reflects the instrument's real session
				// template and is the convention this suite already uses elsewhere for the same
				// purpose (see SneakyFlow_v1_0_0's ResetCvdOnSession block).
				// ── session roll ──
				if (Bars.IsFirstBarOfSession)
				{
					_cvd = 0; _barDelta = 0; _barsThisSession = 0;
					_sessHi = 0; _sessLo = 0;
				}

				if (IsFirstTickOfBar && CurrentBar > 2)
				{
					// ── close out the PREVIOUS bar: this is where the derivatives are computed ──
					double dCvd = _barDelta;

					// Degraded path: no tape at all this bar (historical rebuild without tick replay,
					// or a feed with no tick data on this instrument). Estimate from the OHLC body
					// as a PROXY and flag it — see header §3 — rather than silently reporting zero.
					bool tickBacked = _sawTape;
					if (!tickBacked)
					{
						double rng = Math.Max(High[1] - Low[1], TickSize);
						double body = Close[1] - Open[1];
						dCvd = Volume[1] * Math.Max(-1.0, Math.Min(1.0, body / rng));
						_cvd += dCvd;
					}

					_barsThisSession++;
					_sessHi = _barsThisSession == 1 ? _cvd : Math.Max(_sessHi, _cvd);
					_sessLo = _barsThisSession == 1 ? _cvd : Math.Min(_sessLo, _cvd);

					// slope: EMA of per-bar ΔCVD + an EWMA variance for the z-score (see header §5)
					double a = 2.0 / (Math.Max(2, SlopePeriod) + 1.0);
					_slopeEma += a * (dCvd - _slopeEma);
					double dev = dCvd - _slopeEma;
					_slopeVar += a * (dev * dev - _slopeVar);
					double sd  = Math.Sqrt(Math.Max(_slopeVar, 1e-9));
					double slopeZ = sd > 0 ? _slopeEma / sd : 0;

					// ── EFFICIENCY: ticks of price per 1,000 contracts of NET delta over the window ──
					double lookback = Math.Min(CurrentBar - 1, Math.Max(2, SlopePeriod));
					double dPriceTicks = (Close[1] - Close[(int)lookback]) / TickSize;
					double netFlow = Math.Abs(_slopeEma) * lookback;
					double eff = netFlow > 1 ? Math.Abs(dPriceTicks) / (netFlow / 1000.0) : 0;
					_effEma += a * (eff - _effEma);
					double edev = eff - _effEma;
					_effVar += a * (edev * edev - _effVar);
					double esd = Math.Sqrt(Math.Max(_effVar, 1e-9));
					double effZ = esd > 0 ? (eff - _effEma) / esd : 0;

					int dir = Math.Abs(slopeZ) < Deadband ? 0 : (slopeZ > 0 ? 1 : -1);
					int priceDir = dPriceTicks > 0 ? 1 : (dPriceTicks < 0 ? -1 : 0);

					// Divergence — strength-gated (see header §4): direction disagreement alone
					// is not enough, the flow read itself must clear DivergenceMin too.
					int diverge = 0;
					if (Math.Abs(slopeZ) >= DivergenceMin && dir != 0 && priceDir != 0 && dir != priceDir)
						diverge = dir > 0 ? 1 : -1;

					_dCvd = _cvd; _dSlope = _slopeEma; _dSlopeZ = slopeZ; _dDir = dir;
					_dPriceDir = priceDir; _dDiverge = diverge; _dEff = eff; _dEffZ = effZ;
					_dTickBacked = tickBacked; _hasData = true;

					if (PublishCvd)
					{
						string sc = Scope();
						if (!string.IsNullOrEmpty(sc))
							SneakyCore.SetCvdState(sc, SneakyCore.BarTag(BarsPeriod),
								Instrument.MasterInstrument.Name, _cvd, _slopeEma, slopeZ, dir, priceDir,
								diverge, eff, effZ, _sessHi, _sessLo, _barsThisSession, tickBacked,
								tickBacked ? "SneakyCVD/tick" : "SneakyCVD/proxy");
					}

					if (LogChanges && State == State.Realtime && dir != _lastLoggedDir)
					{
						_lastLoggedDir = dir;
						try
						{
							SneakyCore.Log("CVD", Instrument.MasterInstrument.Name +
								" " + (dir > 0 ? "flow UP" : dir < 0 ? "flow DOWN" : "flow flat") +
								" cvd=" + _cvd.ToString("F0") + " z=" + slopeZ.ToString("F2") +
								" eff=" + eff.ToString("F1") + "t/1k" +
								(diverge != 0 ? (diverge > 0 ? " DIVERGENCE(bull)" : " DIVERGENCE(bear)") : "") +
								(tickBacked ? "" : " [bar-proxy]"));
						}
						catch { }
					}

					_barDelta = 0;
				}

				Values[0][0] = _cvd;
				Values[1][0] = _dDir;
			}
			catch (Exception ex) { try { Print("[SneakyCVD] OnBarUpdate failed: " + ex.Message); } catch { } }
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
				{ _lastRenderExLog = DateTime.UtcNow; try { Print("[SneakyCVD] card threw: " + ex.Message); } catch { } }
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
			const float cw = 250f, ch = 178f;
			SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
			var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
				ChartControl.ChartPanels[0].X, ChartControl.ChartPanels[0].Y, ChartControl.ChartPanels[0].W, ChartControl.ChartPanels[0].H, cw, ch);
			if (slot.Width <= 0f) return;

			if (!_hasData)
			{
				var rw = _sp.Card(slot.X, slot.Y, cw, ch, SneakySkin.CLine);
				_sp.Dot(rw.Left + 5f, rw.Top + 8f, SneakySkin.CMute, false);
				_sp.Text("CVD  (session delta)", rw.Left + 16f, rw.Top, rw.Width - 20f, 16f, SneakySkin.CInk, 10.5f, true);
				_sp.Text("reading tape\u2026", rw.Left, rw.Top + 26f, rw.Width, 14f, SneakySkin.CMute, 10f);
				return;
			}

			var dirCol = _dDir > 0 ? SneakySkin.CUp : _dDir < 0 ? SneakySkin.CDown : SneakySkin.CMute;
			var edge   = _dDiverge != 0 ? SneakySkin.CWarn : (_dDir != 0 ? SneakySkin.CAccent : SneakySkin.CLine);
			var r = _sp.Card(slot.X, slot.Y, cw, ch, edge);

			_sp.Dot(r.Left + 5f, r.Top + 8f, _dDir != 0 ? SneakySkin.CAccent : SneakySkin.CMute, _dDir != 0);
			_sp.Text("CVD", r.Left + 16f, r.Top, r.Width - 20f, 16f, SneakySkin.CInk, 10.5f, true);

			_sp.Text((_dCvd >= 0 ? "+" : "") + _dCvd.ToString("N0") + "   " + (_dDir > 0 ? "flow up" : _dDir < 0 ? "flow down" : "flat"),
				r.Left, r.Top + 24f, r.Width, 20f, dirCol, 13f, true);

			_sp.Text("slope z " + _dSlopeZ.ToString("0.00") + "   impact " + _dEff.ToString("0.0") + "t/1k  z " + _dEffZ.ToString("0.00"),
				r.Left, r.Top + 50f, r.Width, 14f, SneakySkin.CInk2, 10f);

			string read = _dDiverge > 0 ? "DIVERGENCE \u2014 price down, flow up"
						: _dDiverge < 0 ? "DIVERGENCE \u2014 price up, flow down"
						: _dEffZ < -1.0 ? "ABSORBING \u2014 heavy flow, little price"
						: _dEffZ >  1.0 ? "THIN \u2014 price travelling on light flow"
						: _dDir != 0    ? "flow and price agree"
										: "no net pressure";
			var readCol = _dDiverge != 0 ? SneakySkin.CWarn : Math.Abs(_dEffZ) > 1.0 ? SneakySkin.CAccent : SneakySkin.CMute;
			_sp.Text(read, r.Left, r.Top + 70f, r.Width, 14f, readCol, 9.5f, true);

			if (!_dTickBacked)
				_sp.Text("BAR PROXY \u2014 no tick data, signing degraded", r.Left, r.Top + 92f, r.Width, 14f, SneakySkin.CWarn, 9f, true);
			else
				_sp.Text("session " + _sessLo.ToString("N0") + " \u2026 " + _sessHi.ToString("N0"), r.Left, r.Top + 92f, r.Width, 14f, SneakySkin.CMute, 9f);

			_sp.Text("v1.1.0 \u00b7 independent tape, no Pressure dependency",
				r.Left, r.Top + 142f, r.Width, 14f, SneakySkin.CMute, 8f);
		}

		[Browsable(false)] [XmlIgnore] public Series<double> Cvd    => Values[0];
		[Browsable(false)] [XmlIgnore] public Series<double> Signal => Values[1];

		#region Properties

		[NinjaScriptProperty] [Range(2, 500)]
		[Display(Name = "Slope Period", Description = "Bars in the CVD slope EMA and its z-score window.", GroupName = "1. Computation", Order = 0)]
		public int SlopePeriod { get; set; }

		[NinjaScriptProperty] [Range(0.0, 5.0)]
		[Display(Name = "Deadband (z)", Description = "|slope z| below this reads as no direction. Keeps a drifting tape from voting.", GroupName = "1. Computation", Order = 1)]
		public double Deadband { get; set; }

		[NinjaScriptProperty] [Range(0.0, 5.0)]
		[Display(Name = "Divergence Min (z)", Description = "2026-08-06 — new in v1.1.0. |slope z| required before a flow-vs-price disagreement counts as a divergence, not just direction disagreement alone.", GroupName = "1. Computation", Order = 2)]
		public double DivergenceMin { get; set; }

		[NinjaScriptProperty] [Range(1.0, 50.0)]
		[Display(Name = "Winsor Multiple", Description = "2026-08-06 — new in v1.1.0. Clamp one print's volume at N x its EWMA. Block-trade guard — a single print must not own the session's delta.", GroupName = "1. Computation", Order = 3)]
		public double WinsorMult { get; set; }

		[NinjaScriptProperty] [Range(0.5, 300.0)]
		[Display(Name = "Seam Staleness (sec)", GroupName = "2. Sneaky", Order = 0)]
		public double StaleSec { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Publish CVD to Sneaky", Description = "Chart-specific scope, same as every other file in this suite. Nothing in Council or Confluence reads this seam on purpose — it exists for SneakyEye.", GroupName = "2. Sneaky", Order = 1)]
		public bool PublishCvd { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Log Direction Changes", Description = "Write flow-direction changes to sneaky.log (realtime only).", GroupName = "2. Sneaky", Order = 2)]
		public bool LogChanges { get; set; }

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
		private Sneaky.Sensors.SneakyCVD_v1_1_0[] cacheSneakyCVD_v1_1_0;
		public Sneaky.Sensors.SneakyCVD_v1_1_0 SneakyCVD_v1_1_0(int slopePeriod, double deadband, double divergenceMin, double winsorMult, double staleSec, bool publishCvd, bool logChanges, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyCVD_v1_1_0(Input, slopePeriod, deadband, divergenceMin, winsorMult, staleSec, publishCvd, logChanges, showCard, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Sensors.SneakyCVD_v1_1_0 SneakyCVD_v1_1_0(ISeries<double> input, int slopePeriod, double deadband, double divergenceMin, double winsorMult, double staleSec, bool publishCvd, bool logChanges, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyCVD_v1_1_0 != null)
				for (int idx = 0; idx < cacheSneakyCVD_v1_1_0.Length; idx++)
					if (cacheSneakyCVD_v1_1_0[idx] != null && cacheSneakyCVD_v1_1_0[idx].SlopePeriod == slopePeriod && cacheSneakyCVD_v1_1_0[idx].Deadband == deadband && cacheSneakyCVD_v1_1_0[idx].DivergenceMin == divergenceMin && cacheSneakyCVD_v1_1_0[idx].WinsorMult == winsorMult && cacheSneakyCVD_v1_1_0[idx].StaleSec == staleSec && cacheSneakyCVD_v1_1_0[idx].PublishCvd == publishCvd && cacheSneakyCVD_v1_1_0[idx].LogChanges == logChanges && cacheSneakyCVD_v1_1_0[idx].ShowCard == showCard && cacheSneakyCVD_v1_1_0[idx].CardCorner == cardCorner && cacheSneakyCVD_v1_1_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyCVD_v1_1_0[idx].EqualsInput(input))
						return cacheSneakyCVD_v1_1_0[idx];
			return CacheIndicator<Sneaky.Sensors.SneakyCVD_v1_1_0>(new Sneaky.Sensors.SneakyCVD_v1_1_0(){ SlopePeriod = slopePeriod, Deadband = deadband, DivergenceMin = divergenceMin, WinsorMult = winsorMult, StaleSec = staleSec, PublishCvd = publishCvd, LogChanges = logChanges, ShowCard = showCard, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyCVD_v1_1_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Sensors.SneakyCVD_v1_1_0 SneakyCVD_v1_1_0(int slopePeriod, double deadband, double divergenceMin, double winsorMult, double staleSec, bool publishCvd, bool logChanges, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyCVD_v1_1_0(Input, slopePeriod, deadband, divergenceMin, winsorMult, staleSec, publishCvd, logChanges, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyCVD_v1_1_0 SneakyCVD_v1_1_0(ISeries<double> input , int slopePeriod, double deadband, double divergenceMin, double winsorMult, double staleSec, bool publishCvd, bool logChanges, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyCVD_v1_1_0(input, slopePeriod, deadband, divergenceMin, winsorMult, staleSec, publishCvd, logChanges, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Sensors.SneakyCVD_v1_1_0 SneakyCVD_v1_1_0(int slopePeriod, double deadband, double divergenceMin, double winsorMult, double staleSec, bool publishCvd, bool logChanges, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyCVD_v1_1_0(Input, slopePeriod, deadband, divergenceMin, winsorMult, staleSec, publishCvd, logChanges, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyCVD_v1_1_0 SneakyCVD_v1_1_0(ISeries<double> input , int slopePeriod, double deadband, double divergenceMin, double winsorMult, double staleSec, bool publishCvd, bool logChanges, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyCVD_v1_1_0(input, slopePeriod, deadband, divergenceMin, winsorMult, staleSec, publishCvd, logChanges, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
