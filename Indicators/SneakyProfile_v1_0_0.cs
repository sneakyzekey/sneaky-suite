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
using NinjaTrader.NinjaScript.AddOns.Sneaky;   // SneakySkin (glass card) + SneakyCore (ProfileState seam) + SneakyCardCorner
using NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors;
#endregion

// ═════════════════════════════════════════════════════════════════════════════
//  Sneaky Profile — the DEVELOPING VOLUME PROFILE publisher (CLEAN-ROOM)   |   Version v1.0.0
//  File: SneakyProfile_v1_0_0.cs   |   namespace …Indicators.Sneaky.Sensors (Tier-③ SENSOR)   |   Name "Sneaky Profile"
//
//  ⚠ NO ORDERS — read-only advisory indicator. Safe to run anywhere.
//
//  CLEAN-ROOM ORIGINAL. Volume-at-price binning, point of control, and the value-area expansion
//  algorithm (build outward from POC by whichever adjacent bin has more volume, until ~70% of session
//  volume is bracketed) are all PUBLIC, non-copyrightable market-profile concepts — this is the same
//  method every mainstream volume-profile tool uses. Session boundaries come from NinjaTrader's own
//  SessionIterator, the same pattern SneakyClock/SneakyLocation already use.
//
//  WHY THIS WAS BUILT — SneakyCore has carried a fully-defined ProfileState contract (Poc/Vah/Val/
//  Location/Signal/DistPocTicks/NearHVN/NearLVN) since v1.26.0, its own doc comment already names the
//  publisher "SneakyProfile" and describes it as "the developing VOLUME PROFILE the suite lacked" — but
//  nothing ever published it. This is that publisher.
//
//  WHAT IT ADDS — SneakyLocation gives you VWAP/PDH-PDL/OR/IB: fixed reference levels. LiquidityWalls
//  gives you order-flow absorption zones. Neither tells you WHERE VOLUME ACTUALLY TRADED today — the
//  single most-traded price (POC) and the range that bracketed most of it (the value area). That's a
//  third, genuinely different kind of level: not a reference point, not a flow event, but where the
//  market actually spent its time.
//
//  THE PUBLIC METHOD:
//    • Session volume is binned into fixed-width price rows (RowSizeTicks wide), reset every session —
//      same session-boundary detection SneakyLocation uses.
//    • A bar's volume is split EVENLY across every row its High-Low range touches — the standard
//      approximation used whenever true tick-by-tick price/size prints aren't available (same honesty
//      as BuySellVolumePressureMountain's own OHLC-proxy fallback: this is disclosed, not hidden).
//    • POC = the row with the most accumulated volume so far this session.
//    • Value Area = starting at POC, add whichever neighboring row (above or below) has more volume,
//      repeating until the bracketed rows hold ValueAreaPct (default 70%) of session volume — the
//      textbook value-area algorithm.
//    • Location = -1 price below VAL / 0 accepted inside the value area / +1 above VAH.
//    • Signal = the POC-reversion lean: +1 stretched below VAL, -1 above VAH, 0 inside — a MEAN-
//      REVERSION voice, deliberately the opposite sense of a breakout signal.
//    • NearHVN = price within NearNodeTicks of the POC row (the clearest high-volume node).
//    • NearLVN = price within NearNodeTicks of the session's thinnest-traded row so far (a volume
//      "air gap" — the simplification here is real: it's the single thinnest row seen this session,
//      not a full local-minima scan of the whole profile shape).
//
//  THE SNEAKY PLUMBING (our own code — makes it a suite member):
//    • PUBLISHES SneakyCore.ProfileState (SetProfileState) — the seam already named for this tool.
//    • Heartbeat via OnMarketData + TouchProfileState so a quiet session doesn't age this out of any
//      consumer's roster between bar closes.
//    • A SneakySkin.Painter glass card in the scroll rotation + label remover + Release() on Terminated.
//    • CONTEXT MODULATOR by design (per the seam's own doc comment) — not wired as a lone directional
//      voter anywhere; Signal exists for a future consumer to weigh, same posture SneakyLocation and
//      SneakyMtf shipped with before their own Confluence/Council gates were added afterward.
//
//  CHANGELOG
//    v1.0.0 (2026-08-02) — NEW. Fills the ProfileState publisher gap left open since v1.26.0.
// ═════════════════════════════════════════════════════════════════════════════
namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors
{
	public class SneakyProfile_v1_0_0 : Indicator, ISneakyScrollCard
	{
		private SneakySkin.Painter _sp;
		private bool _hasData;

		// ── session tracking (same pattern as SneakyLocation) ──
		private SessionIterator _si;
		private DateTime _sessionStart, _sessionEnd, _prevSessionStart;
		private bool _haveSession, _sessionInited;

		// volume-at-price, keyed by row index (price / rowSize, rounded). Reset every session.
		private readonly Dictionary<long, double> _volByRow = new Dictionary<long, double>();
		private double _totalVol;

		// cached published state
		private double _poc = double.NaN, _vah = double.NaN, _val = double.NaN;
		private int _location, _signal;
		private double _distPocTicks = double.NaN;
		private bool _nearHvn, _nearLvn;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Publishes SneakyCore.ProfileState — a session-developing volume profile: point of "
					+ "control (POC), value area high/low (default 70% of session volume), a POC-reversion Signal, "
					+ "and near-high/low-volume-node flags. Fills the publisher side of the ProfileState seam, "
					+ "already named 'SneakyProfile' in its own doc comment since v1.26.0. Places no orders.";
				Name                     = "Sneaky Profile v1.0.0";
				Calculate                = Calculate.OnBarClose;
				IsOverlay                = true;
				DisplayInDataBox         = true;
				PaintPriceMarkers        = false;
				IsSuspendedWhileInactive = false;
				IsAutoScale              = false;
				ShowIndicatorLabel       = false;

				RowSizeTicks   = 4;
				ValueAreaPct   = 0.70;
				NearNodeTicks  = 4;

				PublishState       = true;
				ShowCard           = true;
				CardCorner         = SneakyCardCorner.TopRight;
				ShowIndicatorLabel = false;

				AddPlot(new Stroke(Brushes.Goldenrod, 2), PlotStyle.Line, "Poc");
				AddPlot(new Stroke(Brushes.Goldenrod, 1), PlotStyle.Line, "Vah");
				AddPlot(new Stroke(Brushes.Goldenrod, 1), PlotStyle.Line, "Val");
				// Hidden ±1 directional-pulse plot so generic consumers can read the POC-reversion
				// Signal without parsing the card — same convention SuperTrend/Mtf use.
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
			try { SneakyCore.TouchProfileState(Scope()); } catch { }
		}

		private long RowOf(double price) => (long)Math.Round(price / (TickSize * Math.Max(1, RowSizeTicks)));
		private double PriceOf(long row)  => row * TickSize * Math.Max(1, RowSizeTicks);

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 1) return;
			if (TickSize <= 0) return;

			// 1) session bounds — same pattern as SneakyLocation.
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

			bool newSession = !_sessionInited || _sessionStart != _prevSessionStart;
			if (newSession)
			{
				_sessionInited    = true;
				_prevSessionStart = _sessionStart;
				_volByRow.Clear();
				_totalVol = 0;
			}

			// 2) bin this bar's volume evenly across every row its High-Low range touches — the
			//    standard approximation without tick-by-tick price/size prints.
			long rowHi = RowOf(High[0]);
			long rowLo = RowOf(Low[0]);
			if (rowHi < rowLo) { var t = rowHi; rowHi = rowLo; rowLo = t; }
			int rowCount = (int)(rowHi - rowLo) + 1;
			if (rowCount < 1) rowCount = 1;
			double perRow = Volume[0] / rowCount;
			for (long row = rowLo; row <= rowHi; row++)
			{
				double cur;
				_volByRow.TryGetValue(row, out cur);
				_volByRow[row] = cur + perRow;
			}
			_totalVol += Volume[0];

			// 3) POC — the row with the most accumulated volume so far.
			long pocRow = 0; double pocVol = -1;
			foreach (var kv in _volByRow)
				if (kv.Value > pocVol) { pocVol = kv.Value; pocRow = kv.Key; }
			_poc = PriceOf(pocRow);

			// 4) value area — expand outward from POC, always adding whichever neighboring row (above
			//    or below the current bracket) holds more volume, until ValueAreaPct of session volume
			//    is bracketed. Textbook algorithm.
			long lo = pocRow, hi = pocRow;
			double covered = pocVol;
			double target = _totalVol * Math.Min(0.99, Math.Max(0.01, ValueAreaPct));
			while (covered < target)
			{
				double volAbove, volBelow;
				bool haveAbove = _volByRow.TryGetValue(hi + 1, out volAbove);
				bool haveBelow = _volByRow.TryGetValue(lo - 1, out volBelow);
				if (!haveAbove && !haveBelow) break;   // nothing left to add either side

				if (haveAbove && (!haveBelow || volAbove >= volBelow)) { hi++; covered += volAbove; }
				else { lo--; covered += volBelow; }
			}
			_vah = PriceOf(hi);
			_val = PriceOf(lo);

			// 5) location + POC-reversion signal + distance + node proximity.
			double price = Close[0];
			_location = price > _vah ? 1 : (price < _val ? -1 : 0);
			_signal   = price < _val ? 1 : (price > _vah ? -1 : 0);   // reversion: stretched below -> lean up
			_distPocTicks = (price - _poc) / TickSize;

			_nearHvn = Math.Abs(RowOf(price) - pocRow) * RowSizeTicks <= NearNodeTicks;

			long lvnRow = 0; double lvnVol = double.MaxValue; bool haveLvn = false;
			foreach (var kv in _volByRow)
				if (kv.Value < lvnVol) { lvnVol = kv.Value; lvnRow = kv.Key; haveLvn = true; }
			_nearLvn = haveLvn && Math.Abs(RowOf(price) - lvnRow) * RowSizeTicks <= NearNodeTicks;

			_hasData = true;

			Poc[0] = _poc; Vah[0] = _vah; Val[0] = _val; Signal[0] = _signal;

			if (PublishState && Instrument != null && Instrument.MasterInstrument != null)
			{
				try
				{
					SneakyCore.SetProfileState(new SneakyCore.ProfileState
					{
						Scope        = Scope(),
						Bartype      = SneakyCore.BarTag(BarsPeriod),
						Instrument   = Instrument.MasterInstrument.Name,
						Poc          = _poc,
						Vah          = _vah,
						Val          = _val,
						Location     = _location,
						Signal       = _signal,
						DistPocTicks = _distPocTicks,
						NearHVN      = _nearHvn,
						NearLVN      = _nearLvn,
						Source       = "PROFILE"
					});
				}
				catch { }
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
					try { Print("[SneakyProfile] RenderCard() threw: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); } catch { }
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
			const float cw = 244f, ch = 176f;
			SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
			var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
				ChartControl.ChartPanels[0].X, ChartControl.ChartPanels[0].Y, ChartControl.ChartPanels[0].W, ChartControl.ChartPanels[0].H, cw, ch);
			if (slot.Width <= 0f) return;   // not the currently-selected card in the rotation

			if (!_hasData)
			{
				var rw = _sp.Card(slot.X, slot.Y, cw, ch, SneakySkin.CLine);
				_sp.Dot(rw.Left + 5f, rw.Top + 8f, SneakySkin.CMute, false);
				_sp.Text("PROFILE", rw.Left + 16f, rw.Top, rw.Width - 20f, 16f, SneakySkin.CInk, 11f, true);
				_sp.Text("warming up…", rw.Left, rw.Top + 26f, rw.Width, 14f, SneakySkin.CMute, 10.5f);
				return;
			}

			var trail   = SharpDX.DirectWrite.TextAlignment.Trailing;
			var locCol  = _location > 0 ? SneakySkin.CUp : (_location < 0 ? SneakySkin.CDown : SneakySkin.CMute);
			var r = _sp.Card(slot.X, slot.Y, cw, ch, locCol);

			_sp.Dot(r.Left + 5f, r.Top + 8f, locCol, true);
			_sp.Text("PROFILE", r.Left + 16f, r.Top, r.Width - 90f, 16f, SneakySkin.CInk, 11f, true);
			_sp.Pill(_location > 0 ? "ABOVE VA" : (_location < 0 ? "BELOW VA" : "IN VALUE"), r.Right, r.Top - 1f, locCol);

			_sp.Text("POC", r.Left, r.Top + 24f, 60f, 12f, SneakySkin.CMute, 8.5f, true);
			_sp.Text(_poc.ToString("0.00"), r.Left, r.Top + 34f, r.Width, 22f, SneakySkin.CInk, 17f, false);
			string dt = (_distPocTicks >= 0 ? "+" : "") + _distPocTicks.ToString("0.0") + "t";
			_sp.Text(dt, r.Left, r.Top + 24f, r.Width, 20f, SneakySkin.CInk2, 12f, true, trail);

			_sp.Divider(r.Left, r.Top + 62f, r.Right);
			_sp.Text("VAH / VAL", r.Left, r.Top + 68f, 90f, 13f, SneakySkin.CMute, 9f, true);
			_sp.Text(_vah.ToString("0.00") + " / " + _val.ToString("0.00"), r.Left, r.Top + 68f, r.Width, 13f, SneakySkin.CInk2, 10.5f, false, trail);

			_sp.Text("value area " + (ValueAreaPct * 100.0).ToString("0") + "%", r.Left, r.Top + 84f, r.Width, 12f, SneakySkin.CMute, 8.5f);

			_sp.Divider(r.Left, r.Top + 100f, r.Right);
			string sigTxt = _signal > 0 ? "reversion UP (stretched below VAL)" : (_signal < 0 ? "reversion DOWN (stretched above VAH)" : "no reversion lean");
			_sp.Text(sigTxt, r.Left, r.Top + 106f, r.Width, 14f, _signal != 0 ? SneakySkin.CAccent : SneakySkin.CMute, 9.5f);

			var chip = SharpDX.DirectWrite.TextAlignment.Leading;
			_sp.Text(_nearHvn ? "near HVN" : "", r.Left, r.Top + 124f, r.Width * 0.5f, 14f, SneakySkin.CUp, 9.5f, true, chip);
			_sp.Text(_nearLvn ? "near LVN" : "", r.Left + r.Width * 0.5f, r.Top + 124f, r.Width * 0.5f, 14f, SneakySkin.CWarn, 9.5f, true, chip);

			_sp.Text("row " + RowSizeTicks + "t \u00b7 developing this session", r.Left, r.Top + 144f, r.Width, 14f, SneakySkin.CMute, 8.5f);
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Row Size (ticks)", Description = "Width of each volume-profile row, in ticks. Smaller = more precise POC/value area, more rows to track.", Order = 1, GroupName = "Parameters")]
		public int RowSizeTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0.10, 0.99)]
		[Display(Name = "Value Area %", Description = "Fraction of session volume the value area (VAH/VAL) brackets. Textbook default 0.70.", Order = 2, GroupName = "Parameters")]
		public double ValueAreaPct { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Near Node Distance (ticks)", Description = "How close (in ticks) price must be to the POC row / the session's thinnest row to flag NearHVN / NearLVN.", Order = 3, GroupName = "Parameters")]
		public int NearNodeTicks { get; set; }

		// ── Sneaky ──
		[NinjaScriptProperty]
		[Display(Name = "Publish Profile to Sneaky", Description = "Publish the developing volume profile as SneakyCore.ProfileState so consumers can consult it.", Order = 20, GroupName = "Sneaky")]
		public bool PublishState { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Card", Order = 21, GroupName = "Sneaky")]
		public bool ShowCard { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Card Corner", Description = "Which panel corner the Sneaky card docks to. Cards in the same corner auto-stack.", Order = 22, GroupName = "Sneaky")]
		public SneakyCardCorner CardCorner { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show indicator label", Description = "Show NinjaTrader's chart name label. Sneaky default = OFF; turn on to restore it.", Order = 100, GroupName = "Sneaky")]
		public bool ShowIndicatorLabel { get; set; }

		// ── plot series accessors ──
		[Browsable(false)] [XmlIgnore] public Series<double> Poc => Values[0];
		[Browsable(false)] [XmlIgnore] public Series<double> Vah => Values[1];
		[Browsable(false)] [XmlIgnore] public Series<double> Val => Values[2];
		[Browsable(false)] [XmlIgnore] public Series<double> Signal => Values[3];
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Sneaky.Sensors.SneakyProfile_v1_0_0[] cacheSneakyProfile_v1_0_0;
		public Sneaky.Sensors.SneakyProfile_v1_0_0 SneakyProfile_v1_0_0(int rowSizeTicks, double valueAreaPct, int nearNodeTicks, bool publishState, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyProfile_v1_0_0(Input, rowSizeTicks, valueAreaPct, nearNodeTicks, publishState, showCard, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Sensors.SneakyProfile_v1_0_0 SneakyProfile_v1_0_0(ISeries<double> input, int rowSizeTicks, double valueAreaPct, int nearNodeTicks, bool publishState, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyProfile_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyProfile_v1_0_0.Length; idx++)
					if (cacheSneakyProfile_v1_0_0[idx] != null && cacheSneakyProfile_v1_0_0[idx].RowSizeTicks == rowSizeTicks && cacheSneakyProfile_v1_0_0[idx].ValueAreaPct == valueAreaPct && cacheSneakyProfile_v1_0_0[idx].NearNodeTicks == nearNodeTicks && cacheSneakyProfile_v1_0_0[idx].PublishState == publishState && cacheSneakyProfile_v1_0_0[idx].ShowCard == showCard && cacheSneakyProfile_v1_0_0[idx].CardCorner == cardCorner && cacheSneakyProfile_v1_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyProfile_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyProfile_v1_0_0[idx];
			return CacheIndicator<Sneaky.Sensors.SneakyProfile_v1_0_0>(new Sneaky.Sensors.SneakyProfile_v1_0_0(){ RowSizeTicks = rowSizeTicks, ValueAreaPct = valueAreaPct, NearNodeTicks = nearNodeTicks, PublishState = publishState, ShowCard = showCard, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyProfile_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Sensors.SneakyProfile_v1_0_0 SneakyProfile_v1_0_0(int rowSizeTicks, double valueAreaPct, int nearNodeTicks, bool publishState, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyProfile_v1_0_0(Input, rowSizeTicks, valueAreaPct, nearNodeTicks, publishState, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyProfile_v1_0_0 SneakyProfile_v1_0_0(ISeries<double> input, int rowSizeTicks, double valueAreaPct, int nearNodeTicks, bool publishState, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyProfile_v1_0_0(input, rowSizeTicks, valueAreaPct, nearNodeTicks, publishState, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Sensors.SneakyProfile_v1_0_0 SneakyProfile_v1_0_0(int rowSizeTicks, double valueAreaPct, int nearNodeTicks, bool publishState, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyProfile_v1_0_0(Input, rowSizeTicks, valueAreaPct, nearNodeTicks, publishState, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyProfile_v1_0_0 SneakyProfile_v1_0_0(ISeries<double> input, int rowSizeTicks, double valueAreaPct, int nearNodeTicks, bool publishState, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyProfile_v1_0_0(input, rowSizeTicks, valueAreaPct, nearNodeTicks, publishState, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
