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
using NinjaTrader.NinjaScript.AddOns.Sneaky;   // SneakySkin (glass card) + SneakyCore (LevelState seam) + SneakyCardCorner
using NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors;
#endregion

// ═════════════════════════════════════════════════════════════════════════════
//  Sneaky Location — the STRUCTURAL-LEVELS publisher (CLEAN-ROOM)          |   Version v1.0.0
//  File: SneakyLocation_v1_0_0.cs   |   namespace …Indicators.Sneaky.Sensors (Tier-③ SENSOR)   |   Name "Sneaky Location"
//
//  ⚠ NO ORDERS — read-only advisory indicator. Safe to run anywhere.
//
//  CLEAN-ROOM ORIGINAL. Session VWAP (+ volume-weighted std-dev bands), prior-day high/low, opening
//  range, initial balance, and current-session high/low are all PUBLIC, non-copyrightable market
//  reference concepts. Session boundaries come from NinjaTrader's own SessionIterator against the
//  chart's configured TradingHours template — the same pattern SneakyClock/SneakyDrift/SneakyEffort use.
//
//  WHY THIS WAS BUILT — SneakyCore has carried a fully-defined LevelState contract (Vwap/VwapUpper/
//  VwapLower, Pdh/Pdl, Orh/Orl, Ibh/Ibl, SessHigh/SessLow, NearestPrice/NearestName/NearestDistTicks/
//  NearestDistAtr, VwapSide) since v1.10.0, and SneakyGodReversal already CONSUMES it as an optional
//  "is price near a structural level" booster (GetLevelState → LocationNear). But nothing in the suite
//  ever called SetLevelState — the consumer existed, the publisher never did. This is that publisher.
//
//  WHAT IT ADDS — LiquidityWalls gives you order-flow-derived absorption walls; Structure gives you
//  swing-pivot market structure. Neither gives you the classic reference levels institutions actually
//  watch: session VWAP, yesterday's high/low, the opening range, the initial balance. This does.
//
//  THE PUBLIC METHOD:
//    • VWAP        = cumulative(typicalPrice × volume) ÷ cumulative(volume), reset every session.
//                    typicalPrice = (High+Low+Close)/3 per bar.
//    • VWAP bands  = volume-weighted standard deviation of typicalPrice around VWAP, ± BandMult σ.
//    • PDH/PDL     = the just-completed session's high/low, captured the instant a new session begins.
//    • Opening Range / Initial Balance = running high/low from session start, FROZEN once
//                    OpeningRangeMinutes / InitialBalanceMinutes has elapsed — never updated again
//                    that session.
//    • Session H/L = running high/low of the CURRENT session, live all session.
//    • Nearest     = whichever of the above (that's actually valid yet) sits closest to price, with
//                    signed distance in ticks and in ATR (scale-free, for cross-instrument comparison).
//
//  THE SNEAKY PLUMBING (our own code — makes it a suite member):
//    • PUBLISHES SneakyCore.LevelState (SetLevelState) — the seam GodReversal already reads from.
//    • Heartbeat via OnMarketData + TouchLevelState so a quiet session doesn't age this out of any
//      consumer's roster between bar closes.
//    • VWAP/VwapUpper/VwapLower are REAL visible plots (IsOverlay=true) — unlike Regime's abstract
//      0..1 modulator, these are genuine price-panel levels worth actually drawing on the chart.
//    • A SneakySkin.Painter glass card in the scroll rotation + label remover + Release() on Terminated.
//
//  CHANGELOG
//    v1.0.0 (2026-08-02) — NEW. Fills the LevelState publisher gap left open since v1.10.0.
// ═════════════════════════════════════════════════════════════════════════════
namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors
{
	public class SneakyLocation_v1_0_0 : Indicator, ISneakyScrollCard
	{
		private SneakySkin.Painter _sp;
		private bool _hasData;

		// ── session tracking ──
		private SessionIterator _si;
		private DateTime _sessionStart, _sessionEnd, _prevSessionStart;
		private bool _haveSession, _sessionInited;

		// current-session running high/low
		private double _sessHigh, _sessLow;

		// prior-completed-session high/low (NaN until the first session completes)
		private double _pdh = double.NaN, _pdl = double.NaN;

		// session VWAP accumulators (reset every new session)
		private double _cumTpv, _cumTpv2, _cumVol;
		private double _vwap = double.NaN, _vwapUpper = double.NaN, _vwapLower = double.NaN;
		private int _vwapSide;

		// opening range / initial balance — running while open, frozen once their window elapses
		private double _orh = double.NaN, _orl = double.NaN; private bool _orDone;
		private double _ibh = double.NaN, _ibl = double.NaN; private bool _ibDone;

		// nearest-level summary (recomputed every bar from whichever levels are currently valid)
		private double _nearestPrice = double.NaN;
		private string _nearestName;
		private double _nearestDistTicks = double.NaN, _nearestDistAtr = double.NaN;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Publishes SneakyCore.LevelState — session VWAP (+ volume-weighted std-dev bands), "
					+ "prior-day high/low, opening range, initial balance, and current-session high/low, plus a "
					+ "nearest-level summary (signed ticks + ATR-normalized distance). Fills the publisher side of "
					+ "the LevelState seam SneakyGodReversal already consults. Places no orders.";
				Name                     = "Sneaky Location v1.0.0";
				Calculate                = Calculate.OnBarClose;
				IsOverlay                = true;
				DisplayInDataBox         = true;
				PaintPriceMarkers        = false;
				IsSuspendedWhileInactive = false;
				IsAutoScale              = false;
				ShowIndicatorLabel       = false;

				OpeningRangeMinutes  = 15;
				InitialBalanceMinutes = 60;
				VwapBandMult         = 1.0;
				AtrPeriod            = 14;

				PublishState       = true;
				LogNewSession      = true;
				ShowCard           = true;
				CardCorner         = SneakyCardCorner.TopRight;
				ShowIndicatorLabel = false;

				AddPlot(new Stroke(Brushes.DodgerBlue,   2), PlotStyle.Line, "Vwap");
				AddPlot(new Stroke(Brushes.DodgerBlue,   1), PlotStyle.Line, "VwapUpper");
				AddPlot(new Stroke(Brushes.DodgerBlue,   1), PlotStyle.Line, "VwapLower");
				// Hidden ±1 directional-pulse plot (VwapSide) so generic consumers can read it
				// without parsing the card — same convention SuperTrend/Mtf use.
				AddPlot(new Stroke(Brushes.Transparent, 1f), PlotStyle.Line, "Signal");
			}
			else if (State == State.DataLoaded)
			{
				if (!ShowIndicatorLabel) Name = string.Empty;
				try { _si = new SessionIterator(Bars); } catch { _si = null; }
			}
			else if (State == State.Terminated)
			{
				if (_sp != null) { try { _sp.Dispose(); } catch { } _sp = null; }
				try { SneakySkin.CardLayout.Release(this); } catch { }
			}
		}

		// ── scope key ("<masterInstrument>.<barTag>" — ONE CHART's worth of context). Lazily resolved. ──
		private string _scope;
		private string Scope()
		{
			try { _scope = SneakyCore.ScopeOf(Instrument, BarsPeriod, ChartControl); } catch { }   // 2026-08-04 — ALWAYS recomputed, never permanently cached: a chart's lane can register AFTER this indicator's first call, so caching once risked freezing a stale, unlaned scope forever (confirmed happening for real tonight). Cheap to recompute every call; only overwrites _scope on success, so a transient throw keeps the previous good value.
			return _scope;
		}

		// ── HEARTBEAT — re-stamp the cached seam on incoming quotes so a quiet session doesn't age it out
		//    of a consumer's roster between bar closes. No recompute, realtime only, throttled. ──
		private DateTime _lastHeartbeatUtc;
		private const double HeartbeatSec = 5.0;
		protected override void OnMarketData(NinjaTrader.Data.MarketDataEventArgs marketDataUpdate)
		{
			if (!PublishState || State != State.Realtime) return;
			DateTime now = DateTime.UtcNow;
			if ((now - _lastHeartbeatUtc).TotalSeconds < HeartbeatSec) return;
			_lastHeartbeatUtc = now;
			try { SneakyCore.TouchLevelState(Scope()); } catch { }
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 1) return;

			// 1) session bounds from the chart's OWN TradingHours template — same pattern as SneakyClock.
			try
			{
				if (_si != null)
				{
					_si.GetNextSession(Time[0], true);
					_sessionStart = _si.ActualSessionBegin;
					_sessionEnd   = _si.ActualSessionEnd;
					_haveSession  = _sessionEnd > _sessionStart;
				}
				else _haveSession = false;
			}
			catch { _haveSession = false; }

			if (!_haveSession) return;

			// 2) new session began? Capture PDH/PDL from the just-ended session, then reset everything
			//    that's scoped to a single session (VWAP accumulators, session H/L, OR, IB).
			bool newSession = !_sessionInited || _sessionStart != _prevSessionStart;
			if (newSession)
			{
				if (_sessionInited)   // don't stamp PDH/PDL off a partial first session
				{
					_pdh = _sessHigh;
					_pdl = _sessLow;
				}
				_sessionInited     = true;
				_prevSessionStart  = _sessionStart;

				_sessHigh = High[0];
				_sessLow  = Low[0];

				_cumTpv = _cumTpv2 = _cumVol = 0;
				_vwap = _vwapUpper = _vwapLower = double.NaN;

				_orh = _orl = double.NaN; _orDone = false;
				_ibh = _ibl = double.NaN; _ibDone = false;

				if (LogNewSession)
				{
					try
					{
						string inst = Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : "?";
						SneakyCore.Log("LOCATION", inst + " new session " + _sessionStart.ToString("yyyy-MM-dd HH:mm")
							+ (double.IsNaN(_pdh) ? "" : "   PDH " + _pdh.ToString("0.00") + "   PDL " + _pdl.ToString("0.00")));
					}
					catch { }
				}
			}
			else
			{
				_sessHigh = Math.Max(_sessHigh, High[0]);
				_sessLow  = Math.Min(_sessLow, Low[0]);
			}

			// 3) session VWAP + volume-weighted std-dev bands.
			double typical = (High[0] + Low[0] + Close[0]) / 3.0;
			double vol     = Volume[0];
			_cumTpv  += typical * vol;
			_cumTpv2 += vol * typical * typical;
			_cumVol  += vol;
			if (_cumVol > 0)
			{
				_vwap = _cumTpv / _cumVol;
				double variance = (_cumTpv2 / _cumVol) - _vwap * _vwap;
				double std      = variance > 0 ? Math.Sqrt(variance) : 0.0;
				_vwapUpper = _vwap + VwapBandMult * std;
				_vwapLower = _vwap - VwapBandMult * std;
				_vwapSide  = Close[0] > _vwap ? 1 : (Close[0] < _vwap ? -1 : 0);
			}

			// 4) opening range / initial balance — extend while inside the window, freeze once past it.
			double sinceOpenMin = (Time[0] - _sessionStart).TotalMinutes;
			if (!_orDone)
			{
				_orh = double.IsNaN(_orh) ? High[0] : Math.Max(_orh, High[0]);
				_orl = double.IsNaN(_orl) ? Low[0]  : Math.Min(_orl, Low[0]);
				if (sinceOpenMin >= OpeningRangeMinutes) _orDone = true;
			}
			if (!_ibDone)
			{
				_ibh = double.IsNaN(_ibh) ? High[0] : Math.Max(_ibh, High[0]);
				_ibl = double.IsNaN(_ibl) ? Low[0]  : Math.Min(_ibl, Low[0]);
				if (sinceOpenMin >= InitialBalanceMinutes) _ibDone = true;
			}

			// 5) nearest-level scan across whatever's currently valid.
			double atr = ATR(AtrPeriod)[0];   // TRUE ATR on the bar — NOT ATR(Close, n)
			double price = Close[0];
			_nearestPrice = double.NaN; _nearestName = null;
			ConsiderLevel(price, _vwap, "VWAP", ref _nearestPrice, ref _nearestName);
			ConsiderLevel(price, _pdh,  "PDH",  ref _nearestPrice, ref _nearestName);
			ConsiderLevel(price, _pdl,  "PDL",  ref _nearestPrice, ref _nearestName);
			ConsiderLevel(price, _orh,  "ORH",  ref _nearestPrice, ref _nearestName);
			ConsiderLevel(price, _orl,  "ORL",  ref _nearestPrice, ref _nearestName);
			ConsiderLevel(price, _ibh,  "IBH",  ref _nearestPrice, ref _nearestName);
			ConsiderLevel(price, _ibl,  "IBL",  ref _nearestPrice, ref _nearestName);
			ConsiderLevel(price, _sessHigh, "SESS HI", ref _nearestPrice, ref _nearestName);
			ConsiderLevel(price, _sessLow,  "SESS LO", ref _nearestPrice, ref _nearestName);

			if (!double.IsNaN(_nearestPrice) && TickSize > 0)
			{
				_nearestDistTicks = (price - _nearestPrice) / TickSize;
				_nearestDistAtr   = atr > 0 ? Math.Abs(price - _nearestPrice) / atr : double.NaN;
			}
			else { _nearestDistTicks = double.NaN; _nearestDistAtr = double.NaN; }

			_hasData = true;

			Vwap[0]      = _vwap;
			VwapUpper[0] = _vwapUpper;
			VwapLower[0] = _vwapLower;
			Signal[0]    = _vwapSide;

			// 6) publish.
			if (PublishState && Instrument != null && Instrument.MasterInstrument != null)
			{
				try
				{
					SneakyCore.SetLevelState(new SneakyCore.LevelState
					{
						Scope            = Scope(),
						Bartype          = SneakyCore.BarTag(BarsPeriod),
						Instrument       = Instrument.MasterInstrument.Name,
						Vwap             = _vwap,
						VwapUpper        = _vwapUpper,
						VwapLower        = _vwapLower,
						Pdh              = _pdh,
						Pdl              = _pdl,
						Orh              = _orh,
						Orl              = _orl,
						Ibh              = _ibh,
						Ibl              = _ibl,
						SessHigh         = _sessHigh,
						SessLow          = _sessLow,
						NearestPrice     = _nearestPrice,
						NearestName      = _nearestName,
						NearestDistTicks = _nearestDistTicks,
						NearestDistAtr   = _nearestDistAtr,
						VwapSide         = _vwapSide,
						Source           = "LOCATION"
					});
				}
				catch { }
			}
		}

		// picks the level closest to price among candidates that are actually valid (non-NaN) so far.
		private static void ConsiderLevel(double price, double levelPrice, string name, ref double bestPrice, ref string bestName)
		{
			if (double.IsNaN(levelPrice)) return;
			double d = Math.Abs(price - levelPrice);
			if (double.IsNaN(bestPrice) || d < Math.Abs(price - bestPrice))
			{
				bestPrice = levelPrice;
				bestName  = name;
			}
		}

		// ── glass card ──
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
					try { Print("[SneakyLocation] RenderCard() threw: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); } catch { }
				}
			}
			try { _sp.End(); } catch { }
		}
		private int _lastRenderExLog = 0;   // FIXED 2026-08-03 — was int.MinValue, which overflows in unchecked(TickCount - X) and permanently evaluates negative, silently blocking every exception log for the whole session. 0 is a safe sentinel: TickCount for any system up more than a few seconds is comfortably past any real threshold on the first check.

		public void ConductorRender(SharpDX.Direct2D1.RenderTarget rt)
		{
			if (!ShowCard) return;
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
			const float cw = 256f, ch = 224f;
			SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
			var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
				ChartControl.ChartPanels[0].X, ChartControl.ChartPanels[0].Y, ChartControl.ChartPanels[0].W, ChartControl.ChartPanels[0].H, cw, ch);
			if (slot.Width <= 0f) return;   // not the currently-selected card in the rotation

			if (!_hasData)
			{
				var rw = _sp.Card(slot.X, slot.Y, cw, ch, SneakySkin.CLine);
				_sp.Dot(rw.Left + 5f, rw.Top + 8f, SneakySkin.CMute, false);
				_sp.Text("LOCATION", rw.Left + 16f, rw.Top, rw.Width - 20f, 16f, SneakySkin.CInk, 11f, true);
				_sp.Text("warming up…", rw.Left, rw.Top + 26f, rw.Width, 14f, SneakySkin.CMute, 10.5f);
				return;
			}

			var trail   = SharpDX.DirectWrite.TextAlignment.Trailing;
			var sideCol = _vwapSide > 0 ? SneakySkin.CUp : (_vwapSide < 0 ? SneakySkin.CDown : SneakySkin.CMute);
			var r = _sp.Card(slot.X, slot.Y, cw, ch, SneakySkin.CLine);

			_sp.Dot(r.Left + 5f, r.Top + 8f, sideCol, true);
			_sp.Text("LOCATION", r.Left + 16f, r.Top, r.Width - 90f, 16f, SneakySkin.CInk, 11f, true);
			_sp.Pill(_vwapSide > 0 ? "ABOVE VWAP" : (_vwapSide < 0 ? "BELOW VWAP" : "AT VWAP"), r.Right, r.Top - 1f, sideCol);

			// hero — nearest level + signed distance
			_sp.Text("NEAREST", r.Left, r.Top + 24f, 120f, 12f, SneakySkin.CMute, 8.5f, true);
			_sp.Text(_nearestName ?? "—", r.Left, r.Top + 34f, r.Width * 0.55f, 22f, SneakySkin.CInk, 16f, true);
			string dt = double.IsNaN(_nearestDistTicks) ? "—" : (_nearestDistTicks >= 0 ? "+" : "") + _nearestDistTicks.ToString("0.0") + "t";
			_sp.Text(dt, r.Left, r.Top + 24f, r.Width, 20f, SneakySkin.CInk2, 12f, true, trail);
			_sp.Text(double.IsNaN(_nearestDistAtr) ? "" : _nearestDistAtr.ToString("0.00") + " ATR",
				r.Left, r.Top + 42f, r.Width, 12f, SneakySkin.CMute, 8.5f, false, trail);

			_sp.Divider(r.Left, r.Top + 64f, r.Right);

			// VWAP row
			_sp.Text("VWAP", r.Left, r.Top + 70f, 60f, 13f, SneakySkin.CMute, 9f, true);
			_sp.Text(double.IsNaN(_vwap) ? "—" : _vwap.ToString("0.00"), r.Left, r.Top + 70f, r.Width, 13f, SneakySkin.CInk2, 10.5f, false, trail);
			_sp.Text("band \u00b1" + VwapBandMult.ToString("0.0") + "\u03c3", r.Left, r.Top + 84f, r.Width, 12f, SneakySkin.CMute, 8.5f);

			// PDH/PDL row
			_sp.Text("PDH / PDL", r.Left, r.Top + 100f, 90f, 13f, SneakySkin.CMute, 9f, true);
			_sp.Text((double.IsNaN(_pdh) ? "—" : _pdh.ToString("0.00")) + " / " + (double.IsNaN(_pdl) ? "—" : _pdl.ToString("0.00")),
				r.Left, r.Top + 100f, r.Width, 13f, SneakySkin.CInk2, 10.5f, false, trail);

			// OR row
			_sp.Text("OR " + OpeningRangeMinutes + "m", r.Left, r.Top + 118f, 90f, 13f, SneakySkin.CMute, 9f, true);
			_sp.Text(!_orDone ? "forming…" : _orh.ToString("0.00") + " / " + _orl.ToString("0.00"),
				r.Left, r.Top + 118f, r.Width, 13f, _orDone ? SneakySkin.CInk2 : SneakySkin.CMute, 10.5f, false, trail);

			// IB row
			_sp.Text("IB " + InitialBalanceMinutes + "m", r.Left, r.Top + 136f, 90f, 13f, SneakySkin.CMute, 9f, true);
			_sp.Text(!_ibDone ? "forming…" : _ibh.ToString("0.00") + " / " + _ibl.ToString("0.00"),
				r.Left, r.Top + 136f, r.Width, 13f, _ibDone ? SneakySkin.CInk2 : SneakySkin.CMute, 10.5f, false, trail);

			_sp.Divider(r.Left, r.Top + 158f, r.Right);
			_sp.Text("session hi/lo   " + _sessHigh.ToString("0.00") + " / " + _sessLow.ToString("0.00"),
				r.Left, r.Top + 164f, r.Width, 14f, SneakySkin.CInk2, 9.5f);
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Opening Range Minutes", Description = "Minutes from session start over which the opening-range high/low is tracked before freezing for the rest of the session.", Order = 1, GroupName = "Parameters")]
		public int OpeningRangeMinutes { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Initial Balance Minutes", Description = "Minutes from session start over which the initial-balance high/low is tracked before freezing for the rest of the session. Classic IB = 60.", Order = 2, GroupName = "Parameters")]
		public int InitialBalanceMinutes { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, 5.0)]
		[Display(Name = "VWAP Band Multiplier", Description = "Standard-deviation multiplier for the VWAP upper/lower bands (volume-weighted).", Order = 3, GroupName = "Parameters")]
		public double VwapBandMult { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "ATR Period", Description = "Period for the TRUE ATR used to scale-normalize the nearest-level distance.", Order = 4, GroupName = "Parameters")]
		public int AtrPeriod { get; set; }

		// ── Sneaky ──
		[NinjaScriptProperty]
		[Display(Name = "Publish Location to Sneaky", Description = "Publish structural levels as SneakyCore.LevelState so consumers (SneakyGodReversal, Council, strategies) can consult them.", Order = 20, GroupName = "Sneaky")]
		public bool PublishState { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Log New Session", Description = "Write a line to sneaky.log at the start of every new session, including the just-captured PDH/PDL.", Order = 21, GroupName = "Sneaky")]
		public bool LogNewSession { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Card", Order = 22, GroupName = "Sneaky")]
		public bool ShowCard { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Card Corner", Description = "Which panel corner the Sneaky card docks to. Cards in the same corner auto-stack.", Order = 23, GroupName = "Sneaky")]
		public SneakyCardCorner CardCorner { get; set; }


		[NinjaScriptProperty]
		[Display(Name = "Show indicator label", Description = "Show NinjaTrader's chart name label. Sneaky default = OFF; turn on to restore it.", Order = 100, GroupName = "Sneaky")]
		public bool ShowIndicatorLabel { get; set; }

		// ── plot series accessors ──
		[Browsable(false)] [XmlIgnore] public Series<double> Vwap      => Values[0];
		[Browsable(false)] [XmlIgnore] public Series<double> VwapUpper => Values[1];
		[Browsable(false)] [XmlIgnore] public Series<double> VwapLower => Values[2];
		[Browsable(false)] [XmlIgnore] public Series<double> Signal    => Values[3];
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Sneaky.Sensors.SneakyLocation_v1_0_0[] cacheSneakyLocation_v1_0_0;
		public Sneaky.Sensors.SneakyLocation_v1_0_0 SneakyLocation_v1_0_0(int openingRangeMinutes, int initialBalanceMinutes, double vwapBandMult, int atrPeriod, bool publishState, bool logNewSession, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyLocation_v1_0_0(Input, openingRangeMinutes, initialBalanceMinutes, vwapBandMult, atrPeriod, publishState, logNewSession, showCard, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Sensors.SneakyLocation_v1_0_0 SneakyLocation_v1_0_0(ISeries<double> input, int openingRangeMinutes, int initialBalanceMinutes, double vwapBandMult, int atrPeriod, bool publishState, bool logNewSession, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyLocation_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyLocation_v1_0_0.Length; idx++)
					if (cacheSneakyLocation_v1_0_0[idx] != null && cacheSneakyLocation_v1_0_0[idx].OpeningRangeMinutes == openingRangeMinutes && cacheSneakyLocation_v1_0_0[idx].InitialBalanceMinutes == initialBalanceMinutes && cacheSneakyLocation_v1_0_0[idx].VwapBandMult == vwapBandMult && cacheSneakyLocation_v1_0_0[idx].AtrPeriod == atrPeriod && cacheSneakyLocation_v1_0_0[idx].PublishState == publishState && cacheSneakyLocation_v1_0_0[idx].LogNewSession == logNewSession && cacheSneakyLocation_v1_0_0[idx].ShowCard == showCard && cacheSneakyLocation_v1_0_0[idx].CardCorner == cardCorner && cacheSneakyLocation_v1_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyLocation_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyLocation_v1_0_0[idx];
			return CacheIndicator<Sneaky.Sensors.SneakyLocation_v1_0_0>(new Sneaky.Sensors.SneakyLocation_v1_0_0(){ OpeningRangeMinutes = openingRangeMinutes, InitialBalanceMinutes = initialBalanceMinutes, VwapBandMult = vwapBandMult, AtrPeriod = atrPeriod, PublishState = publishState, LogNewSession = logNewSession, ShowCard = showCard, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyLocation_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Sensors.SneakyLocation_v1_0_0 SneakyLocation_v1_0_0(int openingRangeMinutes, int initialBalanceMinutes, double vwapBandMult, int atrPeriod, bool publishState, bool logNewSession, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLocation_v1_0_0(Input, openingRangeMinutes, initialBalanceMinutes, vwapBandMult, atrPeriod, publishState, logNewSession, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyLocation_v1_0_0 SneakyLocation_v1_0_0(ISeries<double> input, int openingRangeMinutes, int initialBalanceMinutes, double vwapBandMult, int atrPeriod, bool publishState, bool logNewSession, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLocation_v1_0_0(input, openingRangeMinutes, initialBalanceMinutes, vwapBandMult, atrPeriod, publishState, logNewSession, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Sensors.SneakyLocation_v1_0_0 SneakyLocation_v1_0_0(int openingRangeMinutes, int initialBalanceMinutes, double vwapBandMult, int atrPeriod, bool publishState, bool logNewSession, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLocation_v1_0_0(Input, openingRangeMinutes, initialBalanceMinutes, vwapBandMult, atrPeriod, publishState, logNewSession, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyLocation_v1_0_0 SneakyLocation_v1_0_0(ISeries<double> input, int openingRangeMinutes, int initialBalanceMinutes, double vwapBandMult, int atrPeriod, bool publishState, bool logNewSession, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLocation_v1_0_0(input, openingRangeMinutes, initialBalanceMinutes, vwapBandMult, atrPeriod, publishState, logNewSession, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
