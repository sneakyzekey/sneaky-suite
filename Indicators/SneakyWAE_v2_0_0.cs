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
using NinjaTrader.NinjaScript.AddOns.Sneaky;   // SneakySkin (glass card) + SneakyCore (WaeState seam) + SneakyCardCorner
using NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors;
#endregion

// ═════════════════════════════════════════════════════════════════════════════
//  Sneaky WAE — Waddah Attar Explosion (CLEAN-ROOM)                        |   Version v2.0.0
//  File: SneakyWAE_v2_0_0.cs   |   namespace …Indicators.Sneaky   |   display Name "Sneaky WAE"
//
//  ⚠ NO ORDERS — read-only advisory indicator. Safe to run anywhere.
//
//  CLEAN-ROOM ORIGINAL. This is written from scratch off the PUBLIC Waddah Attar Explosion method — a
//  published, non-copyrightable trading formula — using NinjaTrader's own EMA / StdDev / ATR. It uses NO
//  third-party code. It REPLACES SneakyWAE_v1_0_0, which descended from unlicensed NT-community /
//  TradingView code (LazyBear → shayankm → donto → karmic913) and is therefore not clearable for
//  open-source release. See the provenance audit + NOTICE.
//
//  THE PUBLIC FORMULA (canonical parameters: fast 20 / slow 40 / channel 20 / mult 2.0):
//    • momentum  t1        = ( MACD(now) − MACD(prev) ) × Sensitivity,  MACD = EMA(fast) − EMA(slow)
//    • explosion (BB width)= BBupper − BBlower = 2 · Mult · StdDev(channel)     [the "explosion" line]
//    • dead zone           = ATR(deadZoneLength) × DeadZoneMult                 [Wilder ATR = rma(TR,n)]
//    • histogram split      = TrendUp = max(t1,0), TrendDown = max(−t1,0)
//  The classic WAE trigger — the colored histogram exceeds BOTH the explosion line AND the dead zone —
//  becomes a directional momentum-BREAKOUT signal the Council can vote on.
//  (The NT-port's extra fast/slow double-smoothing is DROPPED here; this is the plain public formula.)
//
//  THE SENTINEL PLUMBING (our own code — makes it a suite member):
//    • PUBLISHES SneakyCore.WaeState: Bias (histogram side), Power (|histogram|), Explosion (BB width),
//      DeadZone (ATR), IsExploding (Power > Explosion > DeadZone), Signal (= IsExploding ? Bias : 0).
//    • WIRED INTO THE COUNCIL as the WAE momentum-trigger voter on WaeState.Signal.
//    • Hidden ±1 "Signal" PLOT (Values[4], transparent) for the Deck SIGNAL ARM / generic consumers.
//    • A SneakySkin.Painter glass card + sub-panel plot skin + label remover.
//
//  CHANGELOG
//    v2.0.0 (2026-07-11) — CLEAN-ROOM rewrite of the WAE math from the public formula (original C# via
//             NT EMA/StdDev/ATR; no double-smoothing; canonical 20/40/20 defaults). Sneaky plumbing
//             (WaeState publish, Council voter, hidden Signal plot, glass card, plot skin, label remover,
//             scope key + heartbeat) carried over from v1.0.0 (our own code). v1.0.0 retired (unlicensed lineage).
// ═════════════════════════════════════════════════════════════════════════════
namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors
{
	public class SneakyWAE_v2_0_0 : Indicator, ISneakyScrollCard
	{
		private SneakySkin.Painter _sp;
		private int _lastRenderExLog = 0;   // FIXED 2026-08-03 — was int.MinValue, which overflows in unchecked(TickCount - X) and permanently evaluates negative, silently blocking every exception log for the whole session. 0 is a safe sentinel: TickCount for any system up more than a few seconds is comfortably past any real threshold on the first check.
		private bool   _hasData;
		// cached state (computed in OnBarUpdate; drawn in OnRender)
		private int    _bias;        // -1/0/+1 histogram side (always-on lean)
		private double _power;       // |histogram|
		private double _explosion;   // BB width
		private double _deadzone;    // ATR dead zone
		private bool   _exploding;   // power > explosion && power > deadzone
		private int    _sig;         // exploding ? bias : 0 (confirmed breakout direction)
		private int    _lastLoggedSig = -999;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description              = "Clean-room Waddah Attar Explosion — MACD-difference momentum histogram + Bollinger-width explosion line + ATR dead zone, implemented from the public formula. Publishes SneakyCore.WaeState (bias / power / explosion / dead zone / confirmed breakout Signal) so the Council gains a momentum-breakout voter.";
				Name                     = "Sneaky Waddah Attar Explosion v2.0.0";
				Calculate                = Calculate.OnBarClose;
				IsOverlay                = false;
				DisplayInDataBox         = true;
				DrawOnPricePanel         = true;
				DrawHorizontalGridLines  = false;   // plot skin paints its own themed baseline/wash
				DrawVerticalGridLines    = false;
				PaintPriceMarkers        = true;
				ScaleJustification       = NinjaTrader.Gui.Chart.ScaleJustification.Right;
				IsSuspendedWhileInactive = true;

				Sensitivity    = 150;
				FastLength     = 20;
				SlowLength     = 40;
				ChannelLength  = 20;
				Mult           = 2.0;
				DeadZoneLength = 100;
				DeadZoneMult   = 3.7;

				PublishState       = true;
				LogChanges         = true;
				ShowCard           = true;
				CardCorner         = SneakyCardCorner.TopRight;
				ShowIndicatorLabel = false;
				SneakyPlotSkin   = true;

				AddPlot(new Stroke(Brushes.Green, 2), PlotStyle.Bar, "TrendUp");
				AddPlot(new Stroke(Brushes.Red,   2), PlotStyle.Bar, "TrendDown");
				AddPlot(Brushes.Yellow,                             "ExplosionLine");
				AddPlot(new Stroke(Brushes.Cyan, DashStyleHelper.Dot, 2), PlotStyle.Line, "DeadZonePlot");
				// hidden ±1 confirmed-breakout signal (Values[4]) — transparent; readable by the Deck SIGNAL ARM.
				AddPlot(new Stroke(Brushes.Transparent, 1f), PlotStyle.Line, "Signal");
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
		    try { SneakyCore.TouchWaeState(Scope()); } catch { }
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < Math.Max(SlowLength + 1, DeadZoneLength))
				return;

			// 1) MACD-difference momentum histogram (× sensitivity). MACD = EMA(fast) − EMA(slow); the one-bar
			//    change in MACD is the raw momentum impulse.
			double macdNow  = EMA(FastLength)[0] - EMA(SlowLength)[0];
			double macdPrev = EMA(FastLength)[1] - EMA(SlowLength)[1];
			double t1       = (macdNow - macdPrev) * Sensitivity;

			// 2) Explosion line = Bollinger-band WIDTH = (basis+kσ) − (basis−kσ) = 2·mult·StdDev.
			double explosion = 2.0 * Mult * StdDev(ChannelLength)[0];
			ExplosionLine[0] = explosion;

			// 3) TrendUp / TrendDown histogram split.
			TrendUp[0]   = (t1 >= 0) ?  t1 : 0;
			TrendDown[0] = (t1 <  0) ? -t1 : 0;

			// 4) two-tone histogram color (brighter when the momentum is strengthening).
			PlotBrushes[0][0] = (CurrentBar > 0 && TrendUp[0]   < TrendUp[1])   ? Brushes.Lime   : Brushes.Green;
			PlotBrushes[1][0] = (CurrentBar > 0 && TrendDown[0] < TrendDown[1]) ? Brushes.Orange : Brushes.Red;

			// 5) ATR-based dead zone (Wilder ATR = rma(TrueRange, n)).
			double deadzone = ATR(DeadZoneLength)[0] * DeadZoneMult;
			DeadZonePlot[0] = deadzone;

			// ── Sneaky derivation ──
			int    bias      = t1 > 0 ? 1 : (t1 < 0 ? -1 : 0);
			double power     = Math.Abs(t1);
			bool   exploding = power > explosion && power > deadzone;   // classic WAE trigger
			int    sig       = exploding ? bias : 0;

			Signal[0] = sig;   // hidden plot for the Deck SIGNAL ARM

			_bias = bias; _power = power; _explosion = explosion; _deadzone = deadzone;
			_exploding = exploding; _sig = sig; _hasData = true;

			if (PublishState && Instrument != null && Instrument.MasterInstrument != null)
			{
				try
				{
					SneakyCore.SetWaeState(new SneakyCore.WaeState
					{
						Scope       = Scope(),
						Bartype     = SneakyCore.BarTag(BarsPeriod),
						Instrument  = Instrument.MasterInstrument.Name,
						Bias        = bias,
						Power       = power,
						Explosion   = explosion,
						DeadZone    = deadzone,
						IsExploding = exploding,
						Signal      = sig,
						Source      = "WAE"
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
					SneakyCore.Log("WAE", inst + " " +
						(sig > 0 ? "EXPLOSION ▲ (long)" : sig < 0 ? "EXPLOSION ▼ (short)" : "quiet") +
						" pow=" + power.ToString("0.#") + " exp=" + explosion.ToString("0.#") + " dz=" + deadzone.ToString("0.#"));
				}
				catch { }
			}
		}

		// ── Sneaky plot skin + glass card (both painted in OnRender, one Begin/End frame) ──
		protected override void OnRender(NinjaTrader.Gui.Chart.ChartControl chartControl, NinjaTrader.Gui.Chart.ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (RenderTarget == null || ChartPanel == null) return;
			if (_sp == null) _sp = new SneakySkin.Painter();
			_sp.Begin(RenderTarget);
			try { if (SneakyPlotSkin) RenderPlotSkin(chartControl, chartScale); }
			catch (Exception ex)
			{
				if (unchecked(Environment.TickCount - _lastRenderExLog) > 15000)
				{
					_lastRenderExLog = Environment.TickCount;
					try { Print("[SneakyWAE] RenderPlotSkin() threw: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); } catch { }
				}
			}
			try { if (ShowCard) RenderCard(); }
			catch (Exception ex)
			{
				if (unchecked(Environment.TickCount - _lastRenderExLog) > 15000)
				{
					_lastRenderExLog = Environment.TickCount;
					try { Print("[SneakyWAE] RenderCard() threw: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); } catch { }
				}
			}
			try { _sp.End(); } catch { }
		}

		private void RenderPlotSkin(NinjaTrader.Gui.Chart.ChartControl chartControl, NinjaTrader.Gui.Chart.ChartScale chartScale)
		{
			if (Bars == null || Bars.Count < 2 || ChartBars == null) return;
			float px = ChartPanel.X, py = ChartPanel.Y, pw = ChartPanel.W, ph = ChartPanel.H;

			_sp.PanelWash(px, py, pw, ph);
			if (_exploding)
				_sp.RegimeShade(px, py, pw, ph, _bias > 0 ? SneakySkin.CUp : SneakySkin.CDown, 0.05f);

			int from = ChartBars.FromIndex, to = ChartBars.ToIndex;
			if (from < 0) from = 0;
			if (to > Bars.Count - 1) to = Bars.Count - 1;
			if (to < from) return;

			float yZero = chartScale.GetYByValue(0);
			_sp.Baseline(px, px + pw, yZero, SneakySkin.CInk);

			float dx = to > from ? (chartControl.GetXByBarIndex(ChartBars, to) - chartControl.GetXByBarIndex(ChartBars, to - 1)) : 6f;
			if (dx <= 0f || float.IsNaN(dx) || float.IsInfinity(dx)) dx = 6f;
			float halfW = Math.Max(0.8f, dx * 0.34f);

			var expPts = new List<SharpDX.Vector2>();
			var dzPts  = new List<SharpDX.Vector2>();

			for (int idx = from; idx <= to; idx++)
			{
				if (!Values[0].IsValidDataPointAt(idx) || !Values[1].IsValidDataPointAt(idx)) continue;
				float x = chartControl.GetXByBarIndex(ChartBars, idx);

				double up = Values[0].GetValueAt(idx);   // TrendUp
				double dn = Values[1].GetValueAt(idx);   // TrendDown
				double pwr = Math.Max(up, dn);
				int side = (up <= 0 && dn <= 0) ? 0 : (up >= dn ? 1 : -1);
				if (pwr > 0 && side != 0)
				{
					double prev = side > 0
						? (idx > 0 && Values[0].IsValidDataPointAt(idx - 1) ? Values[0].GetValueAt(idx - 1) : up)
						: (idx > 0 && Values[1].IsValidDataPointAt(idx - 1) ? Values[1].GetValueAt(idx - 1) : dn);
					bool rising = pwr >= prev;   // strengthening momentum → brighter
					var col = side > 0 ? SneakySkin.CUp : SneakySkin.CDown;
					var barCol = rising ? col : SneakySkin.Alpha(col, 0.55f);
					bool barExploding = Values[2].IsValidDataPointAt(idx) && Values[3].IsValidDataPointAt(idx)
						&& pwr > Values[2].GetValueAt(idx) && pwr > Values[3].GetValueAt(idx);
					_sp.HistoBar(x, yZero, chartScale.GetYByValue(pwr), halfW, barCol, barExploding);
				}

				if (Values[2].IsValidDataPointAt(idx)) expPts.Add(new SharpDX.Vector2(x, chartScale.GetYByValue(Values[2].GetValueAt(idx))));
				if (Values[3].IsValidDataPointAt(idx)) dzPts.Add(new SharpDX.Vector2(x, chartScale.GetYByValue(Values[3].GetValueAt(idx))));
			}

			if (dzPts.Count  > 1) _sp.GlowLine(dzPts,  SneakySkin.Alpha(SneakySkin.CAccent, 0.6f), 1.1f, 0.10f);   // dead zone (cyan, subtle)
			if (expPts.Count > 1) _sp.GlowLine(expPts, SneakySkin.CWarn, 1.8f, 0.20f);                                // explosion line (amber)
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
			const float cw = 228f, ch = 138f;
			SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
			var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
				ChartControl.ChartPanels[0].X, ChartControl.ChartPanels[0].Y, ChartControl.ChartPanels[0].W, ChartControl.ChartPanels[0].H, cw, ch);
			if (slot.Width <= 0f) return;   // not the currently-selected card in the rotation

			if (!_hasData)
			{
				var rw = _sp.Card(slot.X, slot.Y, cw, ch, SneakySkin.CLine);
				_sp.Dot(rw.Left + 5f, rw.Top + 8f, SneakySkin.CMute, false);
				_sp.Text("WAE", rw.Left + 16f, rw.Top, rw.Width - 20f, 16f, SneakySkin.CInk, 11f, true);
				_sp.Text("warming up…", rw.Left, rw.Top + 26f, rw.Width, 14f, SneakySkin.CMute, 10.5f);
				return;
			}

			var trail   = SharpDX.DirectWrite.TextAlignment.Trailing;
			var dirCol  = _bias > 0 ? SneakySkin.CUp : _bias < 0 ? SneakySkin.CDown : SneakySkin.CMute;
			var heroCol = _exploding ? dirCol : SneakySkin.CMute;
			var edge    = _exploding ? SneakySkin.CAccent : SneakySkin.CLine;
			var r = _sp.Card(slot.X, slot.Y, cw, ch, edge);

			_sp.Dot(r.Left + 5f, r.Top + 8f, _exploding ? SneakySkin.CAccent : SneakySkin.CMute, _exploding);
			_sp.Text("WAE", r.Left + 16f, r.Top, r.Width - 70f, 16f, SneakySkin.CInk, 11f, true);
			_sp.Pill(_bias > 0 ? "LONG" : _bias < 0 ? "SHORT" : "FLAT", r.Right, r.Top - 1f, dirCol);

			_sp.Text("MOMENTUM", r.Left, r.Top + 24f, 120f, 12f, SneakySkin.CMute, 8.5f, true);
			_sp.Text(_exploding ? (_bias > 0 ? "EXPLOSION ▲" : "EXPLOSION ▼") : "quiet",
				r.Left, r.Top + 34f, r.Width, 24f, heroCol, 17f, false);

			_sp.Divider(r.Left, r.Top + 66f, r.Right);
			_sp.Text("pow " + _power.ToString("0.#"), r.Left, r.Top + 72f, r.Width, 14f, SneakySkin.CInk2, 10f);
			_sp.Text("exp " + _explosion.ToString("0.#"), r.Left, r.Top + 72f, r.Width, 14f, SneakySkin.CInk2, 10f, true, trail);
			_sp.Text("dead zone " + _deadzone.ToString("0.#"), r.Left, r.Top + 90f, r.Width, 14f, SneakySkin.CMute, 10f);
			if (_exploding && _explosion > _deadzone)
				_sp.Text("breakout confirmed", r.Left, r.Top + 90f, r.Width, 14f, SneakySkin.CAccent, 10f, true, trail);
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Sensitivity", Description="Momentum sensitivity multiplier.", Order=1, GroupName="Parameters")]
		public int Sensitivity { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Fast Length", Description="Fast EMA length (public default 20).", Order=2, GroupName="Parameters")]
		public int FastLength { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Slow Length", Description="Slow EMA length (public default 40).", Order=3, GroupName="Parameters")]
		public int SlowLength { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Channel Length", Description="Bollinger channel length for the explosion line (public default 20).", Order=4, GroupName="Parameters")]
		public int ChannelLength { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name="Mult", Description="Bollinger StdDev multiplier.", Order=5, GroupName="Parameters")]
		public double Mult { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Dead-Zone Length", Description="ATR length for the dynamic dead zone (Wilder ATR).", Order=6, GroupName="Parameters")]
		public int DeadZoneLength { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name="Dead-Zone Mult", Description="Dead zone = ATR × this multiplier.", Order=7, GroupName="Parameters")]
		public double DeadZoneMult { get; set; }

		// ── Sneaky ──
		[NinjaScriptProperty]
		[Display(Name="Publish WAE to Sneaky", Description="Publish momentum/explosion as SneakyCore.WaeState so the Council/strategies can vote on it.", Order=20, GroupName="Sneaky")]
		public bool PublishState { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Log Signal Changes", Description="Write explosion-signal transitions to sneaky.log.", Order=21, GroupName="Sneaky")]
		public bool LogChanges { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Show Card", Order=22, GroupName="Sneaky")]
		public bool ShowCard { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Card Corner", Description="Which panel corner the Sneaky card docks to. Cards in the same corner auto-stack.", Order=23, GroupName="Sneaky")]
		public SneakyCardCorner CardCorner { get; set; }

		// Deliberately NOT [NinjaScriptProperty] — serializes to the workspace + shows in F6 without adding a
		// constructor param (no generated-region churn). Off = fall back to NT's stock plot rendering.
		[Display(Name="Sneaky Plot Skin", Description="Render the panel to the Sneaky plot standard (glass wash + card-material histobars + glowing lines) instead of NT's stock plots.", Order=24, GroupName="Sneaky")]
		public bool SneakyPlotSkin { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Show indicator label", Description="Show NinjaTrader's chart name label. Sneaky default = OFF; turn on to restore it.", Order=100, GroupName="Sneaky")]
		public bool ShowIndicatorLabel { get; set; }

		// ── plot series accessors ──
		[Browsable(false)] [XmlIgnore] public Series<double> TrendUp       => Values[0];
		[Browsable(false)] [XmlIgnore] public Series<double> TrendDown     => Values[1];
		[Browsable(false)] [XmlIgnore] public Series<double> ExplosionLine => Values[2];
		[Browsable(false)] [XmlIgnore] public Series<double> DeadZonePlot  => Values[3];
		[Browsable(false)] [XmlIgnore] public Series<double> Signal        => Values[4];   // ±1 confirmed breakout / 0
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Sneaky.Sensors.SneakyWAE_v2_0_0[] cacheSneakyWAE_v2_0_0;
		public Sneaky.Sensors.SneakyWAE_v2_0_0 SneakyWAE_v2_0_0(int sensitivity, int fastLength, int slowLength, int channelLength, double mult, int deadZoneLength, double deadZoneMult, bool publishState, bool logChanges, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyWAE_v2_0_0(Input, sensitivity, fastLength, slowLength, channelLength, mult, deadZoneLength, deadZoneMult, publishState, logChanges, showCard, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Sensors.SneakyWAE_v2_0_0 SneakyWAE_v2_0_0(ISeries<double> input, int sensitivity, int fastLength, int slowLength, int channelLength, double mult, int deadZoneLength, double deadZoneMult, bool publishState, bool logChanges, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyWAE_v2_0_0 != null)
				for (int idx = 0; idx < cacheSneakyWAE_v2_0_0.Length; idx++)
					if (cacheSneakyWAE_v2_0_0[idx] != null && cacheSneakyWAE_v2_0_0[idx].Sensitivity == sensitivity && cacheSneakyWAE_v2_0_0[idx].FastLength == fastLength && cacheSneakyWAE_v2_0_0[idx].SlowLength == slowLength && cacheSneakyWAE_v2_0_0[idx].ChannelLength == channelLength && cacheSneakyWAE_v2_0_0[idx].Mult == mult && cacheSneakyWAE_v2_0_0[idx].DeadZoneLength == deadZoneLength && cacheSneakyWAE_v2_0_0[idx].DeadZoneMult == deadZoneMult && cacheSneakyWAE_v2_0_0[idx].PublishState == publishState && cacheSneakyWAE_v2_0_0[idx].LogChanges == logChanges && cacheSneakyWAE_v2_0_0[idx].ShowCard == showCard && cacheSneakyWAE_v2_0_0[idx].CardCorner == cardCorner && cacheSneakyWAE_v2_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyWAE_v2_0_0[idx].EqualsInput(input))
						return cacheSneakyWAE_v2_0_0[idx];
			return CacheIndicator<Sneaky.Sensors.SneakyWAE_v2_0_0>(new Sneaky.Sensors.SneakyWAE_v2_0_0(){ Sensitivity = sensitivity, FastLength = fastLength, SlowLength = slowLength, ChannelLength = channelLength, Mult = mult, DeadZoneLength = deadZoneLength, DeadZoneMult = deadZoneMult, PublishState = publishState, LogChanges = logChanges, ShowCard = showCard, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyWAE_v2_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Sensors.SneakyWAE_v2_0_0 SneakyWAE_v2_0_0(int sensitivity, int fastLength, int slowLength, int channelLength, double mult, int deadZoneLength, double deadZoneMult, bool publishState, bool logChanges, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyWAE_v2_0_0(Input, sensitivity, fastLength, slowLength, channelLength, mult, deadZoneLength, deadZoneMult, publishState, logChanges, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyWAE_v2_0_0 SneakyWAE_v2_0_0(ISeries<double> input , int sensitivity, int fastLength, int slowLength, int channelLength, double mult, int deadZoneLength, double deadZoneMult, bool publishState, bool logChanges, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyWAE_v2_0_0(input, sensitivity, fastLength, slowLength, channelLength, mult, deadZoneLength, deadZoneMult, publishState, logChanges, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Sensors.SneakyWAE_v2_0_0 SneakyWAE_v2_0_0(int sensitivity, int fastLength, int slowLength, int channelLength, double mult, int deadZoneLength, double deadZoneMult, bool publishState, bool logChanges, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyWAE_v2_0_0(Input, sensitivity, fastLength, slowLength, channelLength, mult, deadZoneLength, deadZoneMult, publishState, logChanges, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyWAE_v2_0_0 SneakyWAE_v2_0_0(ISeries<double> input , int sensitivity, int fastLength, int slowLength, int channelLength, double mult, int deadZoneLength, double deadZoneMult, bool publishState, bool logChanges, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyWAE_v2_0_0(input, sensitivity, fastLength, slowLength, channelLength, mult, deadZoneLength, deadZoneMult, publishState, logChanges, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
