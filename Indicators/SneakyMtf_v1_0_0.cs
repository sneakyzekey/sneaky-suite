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
using NinjaTrader.NinjaScript.AddOns.Sneaky;   // SneakySkin (glass card) + SneakyCore (MtfState seam) + SneakyCardCorner
using NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors;
#endregion

// ═════════════════════════════════════════════════════════════════════════════
//  Sneaky Mtf — the HIGHER-TIMEFRAME ALIGNMENT publisher (CLEAN-ROOM)      |   Version v1.0.0
//  File: SneakyMtf_v1_0_0.cs   |   namespace …Indicators.Sneaky.Sensors (Tier-③ SENSOR)   |   Name "Sneaky Mtf"
//
//  ⚠ NO ORDERS — read-only advisory indicator. Safe to run anywhere.
//
//  CLEAN-ROOM ORIGINAL. A dual-EMA cross per timeframe (fast vs slow) is a public, non-copyrightable
//  trend-direction technique. Higher timeframes are added via NinjaTrader's own AddDataSeries, the
//  same mechanism SneakyIntermarket already uses for its reference instruments — here for the SAME
//  instrument at LARGER periods instead of different instruments.
//
//  WHY THIS WAS BUILT — SneakyCore has carried a fully-defined MtfState contract (Bias/AlignmentScore/
//  AlignedCount/TfCount/AllAgree/Dirs) since v1.10.0, and SneakyCouncil's own CouncilState "decision
//  vector" (the ML training record) has carried an MtfBias FIELD since the same release — but
//  SetCouncilState was always called with a hardcoded 0 there, because nothing ever published a real
//  MtfState to read. This is that publisher; SneakyCouncil is updated alongside it to pass the real
//  value through instead of the placeholder.
//
//  WHAT IT ADDS — every directional tool in this suite (Trend, ADXPro, SuperTrend, the whole moving-
//  average family…) reads THIS chart's own timeframe. None of them tell you whether that reading
//  agrees with the BIGGER picture — is your 1-minute long WITH the 60-minute trend, or fighting it?
//  This is the suite's only genuinely multi-timeframe voice.
//
//  THE PUBLIC METHOD:
//    • Up to 4 higher timeframes (each independently enabled, minutes configurable) are added as extra
//      data series of the SAME instrument via AddDataSeries — no separate symbols needed.
//    • Each timeframe's own direction = a simple fast-EMA vs slow-EMA cross on ITS OWN bars: +1 fast >
//      slow, -1 fast < slow, 0 while that series doesn't have SlowLen bars yet ("not warm").
//      Deliberately lightweight — SneakyTrend's own CCI+ATR method is the suite's precision trend
//      read; this tool's job is BREADTH across several timeframes at once, not precision on any one.
//    • Bias = sign of the weighted net direction across every WARM timeframe.
//    • AlignmentScore = that weighted net ÷ total warm weight (-1..1, magnitude = agreement strength).
//    • AllAgree = every warm timeframe shares one non-zero direction (a genuinely stacked ladder).
//    • Dirs = compact human summary, e.g. "5:+ 15:+ 60:- 240:+".
//
//  THE SNEAKY PLUMBING (our own code — makes it a suite member):
//    • PUBLISHES SneakyCore.MtfState (SetMtfState) — the seam Council's decision vector already reserves
//      a slot for (MtfBias), previously always 0.
//    • Heartbeat via OnMarketData + TouchMtfState so a quiet market doesn't age this out of any
//      consumer's roster between bar closes.
//    • A SneakySkin.Painter glass card in the scroll rotation + label remover + Release() on Terminated.
//
//  CHANGELOG
//    v1.0.0 (2026-08-02) — NEW. Fills the MtfState publisher gap left open since v1.10.0.
// ═════════════════════════════════════════════════════════════════════════════
namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors
{
	public class SneakyMtf_v1_0_0 : Indicator, ISneakyScrollCard
	{
		private SneakySkin.Painter _sp;
		private bool _hasData;

		// One entry per ENABLED higher timeframe, in the fixed TF1..TF4 order. Bip = its BarsInProgress
		// index once resolved in DataLoaded (-1 if that series never actually attached).
		private sealed class Tf
		{
			public int Minutes;
			public double Weight;
			public int Bip = -1;
			public int Dir;        // -1/0/+1, last computed direction
			public bool Warm;
		}
		private readonly List<Tf> _tfs = new List<Tf>();

		// cached published state (recomputed in OnBarUpdate; drawn in OnRender)
		private int _bias;
		private double _alignmentScore;
		private int _alignedCount, _tfCount;
		private bool _allAgree;
		private string _dirs = "";

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Publishes SneakyCore.MtfState — a fast/slow EMA cross computed independently on up "
					+ "to 4 higher timeframes of THIS instrument, fused into a weighted consensus Bias, an "
					+ "AlignmentScore (-1..1), and AllAgree. Fills the publisher side of the MtfState seam "
					+ "SneakyCouncil's decision vector already reserves a slot for. Places no orders.";
				Name                     = "Sneaky Mtf v1.0.0";
				Calculate                = Calculate.OnBarClose;
				IsOverlay                = false;
				DisplayInDataBox         = true;
				DrawOnPricePanel         = false;
				DrawHorizontalGridLines  = true;
				DrawVerticalGridLines    = false;
				PaintPriceMarkers        = true;
				ScaleJustification       = NinjaTrader.Gui.Chart.ScaleJustification.Right;
				IsSuspendedWhileInactive = true;

				UseTf1 = true;  Tf1Minutes = 5;   WeightTf1 = 1.0;
				UseTf2 = true;  Tf2Minutes = 15;  WeightTf2 = 1.0;
				UseTf3 = true;  Tf3Minutes = 60;  WeightTf3 = 1.0;
				UseTf4 = true;  Tf4Minutes = 240; WeightTf4 = 1.0;

				FastLen = 9;
				SlowLen = 21;

				PublishState       = true;
				ShowCard           = true;
				CardCorner         = SneakyCardCorner.TopRight;
				ShowIndicatorLabel = false;

				// CARD-ONLY, like Regime: Bias is a -1/0/1 consensus, AlignmentScore is -1..1 — neither
				// shares a usable scale with any series it might share a panel with. Both plots hidden.
				AddPlot(new Stroke(Brushes.Transparent, 3), PlotStyle.Dot,  "Bias");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "AlignmentScore");
			}
			else if (State == State.Configure)
			{
				// Every higher-TF series must be declared HERE, in a fixed order — same rule
				// SneakyIntermarket's reference series follow. Same instrument, just a larger period.
				_tfs.Clear();
				AddTf(UseTf1, Tf1Minutes, WeightTf1);
				AddTf(UseTf2, Tf2Minutes, WeightTf2);
				AddTf(UseTf3, Tf3Minutes, WeightTf3);
				AddTf(UseTf4, Tf4Minutes, WeightTf4);
			}
			else if (State == State.DataLoaded)
			{
				if (!ShowIndicatorLabel) Name = string.Empty;
				// Bip was assigned in Configure by call order; just sanity-check the slot resolved,
				// same defensive pattern SneakyIntermarket uses for its reference series.
				foreach (var tf in _tfs)
				{
					bool ok = tf.Bip > 0 && tf.Bip < BarsArray.Length && BarsArray[tf.Bip] != null;
					if (!ok) tf.Bip = -1;
				}
			}
			else if (State == State.Terminated)
			{
				if (_sp != null) { try { _sp.Dispose(); } catch { } _sp = null; }
				try { SneakySkin.CardLayout.Release(this); } catch { }
			}
		}

		private int _nextBip = 1;
		private void AddTf(bool use, int minutes, double weight)
		{
			if (!use || minutes < 1) return;
			try
			{
				AddDataSeries(BarsPeriodType.Minute, minutes);
				_tfs.Add(new Tf { Minutes = minutes, Weight = Math.Max(0.0, weight), Bip = _nextBip });
				_nextBip++;
			}
			catch (Exception ex)
			{
				// A bad period must not take the whole indicator down — it simply doesn't
				// contribute, and TfCount on the card shows the shortfall.
				try { Print("[SneakyMtf] could not add " + minutes + "-minute series: " + ex.Message); } catch { }
			}
		}

		// ── scope key ("<masterInstrument>.<barTag>" — ONE CHART's worth of context). Lazily resolved. ──
		private string _scope;
		private string Scope()
		{
			try { _scope = SneakyCore.ScopeOf(Instrument, BarsPeriod, ChartControl); } catch { }   // 2026-08-04 — ALWAYS recomputed, never permanently cached: a chart's lane can register AFTER this indicator's first call, so caching once risked freezing a stale, unlaned scope forever (confirmed happening for real tonight). Cheap to recompute every call; only overwrites _scope on success, so a transient throw keeps the previous good value.
			return _scope;
		}

		// ── HEARTBEAT — re-stamp the cached seam on incoming quotes so a quiet market doesn't age it out
		//    of a consumer's roster between bar closes. No recompute, realtime only, throttled. ──
		private DateTime _lastHeartbeatUtc;
		private const double HeartbeatSec = 5.0;
		protected override void OnMarketData(NinjaTrader.Data.MarketDataEventArgs marketDataUpdate)
		{
			if (!PublishState || State != State.Realtime) return;
			DateTime now = DateTime.UtcNow;
			if ((now - _lastHeartbeatUtc).TotalSeconds < HeartbeatSec) return;
			_lastHeartbeatUtc = now;
			try { SneakyCore.TouchMtfState(Scope()); } catch { }
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0) return;   // only act on the primary (this chart's own) series closing
			if (CurrentBar < 1) return;

			double weightedNet = 0.0, totalWeight = 0.0;
			int agreeCount = 0, warmCount = 0;
			var dirsSb = new StringBuilder();

			foreach (var tf in _tfs)
			{
				if (tf.Bip < 0) { tf.Warm = false; tf.Dir = 0; continue; }

				tf.Warm = CurrentBars[tf.Bip] >= SlowLen;
				if (!tf.Warm) { tf.Dir = 0; continue; }

				double fast = EMA(Closes[tf.Bip], FastLen)[0];
				double slow = EMA(Closes[tf.Bip], SlowLen)[0];
				tf.Dir = fast > slow ? 1 : (fast < slow ? -1 : 0);

				weightedNet += tf.Dir * tf.Weight;
				totalWeight += tf.Weight;
				warmCount++;

				if (dirsSb.Length > 0) dirsSb.Append(' ');
				dirsSb.Append(tf.Minutes).Append(':').Append(tf.Dir > 0 ? '+' : (tf.Dir < 0 ? '-' : '0'));
			}

			_alignmentScore = totalWeight > 0 ? weightedNet / totalWeight : 0.0;
			_bias           = _alignmentScore > 0.0001 ? 1 : (_alignmentScore < -0.0001 ? -1 : 0);
			_tfCount        = warmCount;
			_dirs           = dirsSb.ToString();

			agreeCount = 0;
			bool allAgree = warmCount > 0 && _bias != 0;
			foreach (var tf in _tfs)
			{
				if (!tf.Warm) continue;
				if (tf.Dir == _bias && _bias != 0) agreeCount++;
				if (tf.Dir != _bias || tf.Dir == 0) allAgree = false;
			}
			_alignedCount = agreeCount;
			_allAgree     = allAgree;
			_hasData      = true;

			Bias[0]           = _bias;
			AlignmentScore[0] = _alignmentScore;

			if (PublishState && Instrument != null && Instrument.MasterInstrument != null)
			{
				string diagScope = null;
				try
				{
					diagScope = Scope();
					SneakyCore.SetMtfState(new SneakyCore.MtfState
					{
						Scope          = diagScope,
						Bartype        = SneakyCore.BarTag(BarsPeriod),
						Instrument     = Instrument.MasterInstrument.Name,
						Bias           = _bias,
						AlignmentScore = _alignmentScore,
						AlignedCount   = _alignedCount,
						TfCount        = _tfCount,
						AllAgree       = _allAgree,
						Dirs           = _dirs,
						Source         = "MTF"
					});
					// DIAGNOSTIC (2026-08-04) — confirms the publish call itself was REACHED and did
					// NOT throw, with the exact scope string it just published under. If this line
					// never appears in sentinel.log despite the card showing live data, the failure is
					// somewhere between here and SetMtfState's own internals (or the string.IsNullOrEmpty
					// guard inside it silently swallowing a null/empty Scope()).
					if (unchecked(Environment.TickCount - _lastPublishDiagLog) > 5000)
					{
						_lastPublishDiagLog = Environment.TickCount;
						try { SneakyCore.Log("MtfPublishDiag", "published OK, scope='" + (diagScope ?? "NULL") + "'"); } catch { }
					}
				}
				catch (Exception ex)
				{
					// Previously a bare catch { } — this is exactly the kind of silent failure that
					// cost real time to track down tonight. Now it logs instead of hiding.
					if (unchecked(Environment.TickCount - _lastPublishDiagLog) > 5000)
					{
						_lastPublishDiagLog = Environment.TickCount;
						try { SneakyCore.Log("MtfPublishDiag", "PUBLISH THREW: " + ex.GetType().Name + ": " + ex.Message
							+ "  (Scope() returned '" + (diagScope ?? "NULL") + "' before the throw)"); } catch { }
					}
				}
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
					try { Print("[SneakyMtf] RenderCard() threw: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); } catch { }
				}
			}
			try { _sp.End(); } catch { }
		}
		private int _lastRenderExLog = 0;   // FIXED 2026-08-03 — was int.MinValue, which overflows in unchecked(TickCount - X) and permanently evaluates negative, silently blocking every exception log for the whole session. 0 is a safe sentinel: TickCount for any system up more than a few seconds is comfortably past any real threshold on the first check.
		private int _lastPublishDiagLog = 0;

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
			const float cw = 240f, ch = 168f;
			SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
			var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
				ChartControl.ChartPanels[0].X, ChartControl.ChartPanels[0].Y, ChartControl.ChartPanels[0].W, ChartControl.ChartPanels[0].H, cw, ch);
			if (slot.Width <= 0f) return;   // not the currently-selected card in the rotation

			if (!_hasData)
			{
				var rw = _sp.Card(slot.X, slot.Y, cw, ch, SneakySkin.CLine);
				_sp.Dot(rw.Left + 5f, rw.Top + 8f, SneakySkin.CMute, false);
				_sp.Text("MTF", rw.Left + 16f, rw.Top, rw.Width - 20f, 16f, SneakySkin.CInk, 11f, true);
				_sp.Text("warming up…", rw.Left, rw.Top + 26f, rw.Width, 14f, SneakySkin.CMute, 10.5f);
				return;
			}

			var trail   = SharpDX.DirectWrite.TextAlignment.Trailing;
			var biasCol = _bias > 0 ? SneakySkin.CUp : (_bias < 0 ? SneakySkin.CDown : SneakySkin.CMute);
			var r = _sp.Card(slot.X, slot.Y, cw, ch, biasCol);

			_sp.Dot(r.Left + 5f, r.Top + 8f, biasCol, _tfCount > 0);
			_sp.Text("MTF", r.Left + 16f, r.Top, r.Width - 90f, 16f, SneakySkin.CInk, 11f, true);
			_sp.Pill(_allAgree ? "ALL AGREE" : (_bias > 0 ? "LEAN UP" : (_bias < 0 ? "LEAN DOWN" : "MIXED")), r.Right, r.Top - 1f, biasCol);

			_sp.Text("ALIGNMENT", r.Left, r.Top + 24f, 120f, 12f, SneakySkin.CMute, 8.5f, true);
			_sp.Text(_alignmentScore.ToString("+0.00;-0.00; 0.00"), r.Left, r.Top + 34f, r.Width, 22f, biasCol, 17f, false);
			_sp.Text(_alignedCount + " / " + _tfCount + " agree", r.Left, r.Top + 24f, r.Width, 20f, SneakySkin.CInk2, 12f, true, trail);

			_sp.Divider(r.Left, r.Top + 62f, r.Right);
			_sp.Text(string.IsNullOrEmpty(_dirs) ? "no warm timeframes yet" : _dirs, r.Left, r.Top + 68f, r.Width, 14f, SneakySkin.CInk2, 9.5f, false, SharpDX.DirectWrite.TextAlignment.Leading, true);

			_sp.Divider(r.Left, r.Top + 90f, r.Right);
			_sp.Text("fast/slow EMA " + FastLen + "/" + SlowLen + " per timeframe", r.Left, r.Top + 96f, r.Width, 14f, SneakySkin.CMute, 9f);
			_sp.Text(_tfCount + " of " + _tfs.Count + " timeframes warm", r.Left, r.Top + 112f, r.Width, 14f, SneakySkin.CMute, 9f);
		}

		#region Properties
		[NinjaScriptProperty]
		[Display(Name = "Use TF1", GroupName = "1. Timeframes", Order = 0)]
		public bool UseTf1 { get; set; }
		[NinjaScriptProperty] [Range(1, int.MaxValue)]
		[Display(Name = "TF1 Minutes", GroupName = "1. Timeframes", Order = 1)]
		public int Tf1Minutes { get; set; }
		[NinjaScriptProperty] [Range(0.0, 5.0)]
		[Display(Name = "TF1 Weight", GroupName = "1. Timeframes", Order = 2)]
		public double WeightTf1 { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use TF2", GroupName = "1. Timeframes", Order = 3)]
		public bool UseTf2 { get; set; }
		[NinjaScriptProperty] [Range(1, int.MaxValue)]
		[Display(Name = "TF2 Minutes", GroupName = "1. Timeframes", Order = 4)]
		public int Tf2Minutes { get; set; }
		[NinjaScriptProperty] [Range(0.0, 5.0)]
		[Display(Name = "TF2 Weight", GroupName = "1. Timeframes", Order = 5)]
		public double WeightTf2 { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use TF3", GroupName = "1. Timeframes", Order = 6)]
		public bool UseTf3 { get; set; }
		[NinjaScriptProperty] [Range(1, int.MaxValue)]
		[Display(Name = "TF3 Minutes", GroupName = "1. Timeframes", Order = 7)]
		public int Tf3Minutes { get; set; }
		[NinjaScriptProperty] [Range(0.0, 5.0)]
		[Display(Name = "TF3 Weight", GroupName = "1. Timeframes", Order = 8)]
		public double WeightTf3 { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use TF4", GroupName = "1. Timeframes", Order = 9)]
		public bool UseTf4 { get; set; }
		[NinjaScriptProperty] [Range(1, int.MaxValue)]
		[Display(Name = "TF4 Minutes", GroupName = "1. Timeframes", Order = 10)]
		public int Tf4Minutes { get; set; }
		[NinjaScriptProperty] [Range(0.0, 5.0)]
		[Display(Name = "TF4 Weight", GroupName = "1. Timeframes", Order = 11)]
		public double WeightTf4 { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Fast EMA Length", Description = "Fast EMA period used for each timeframe's own direction read.", GroupName = "2. Parameters", Order = 0)]
		public int FastLen { get; set; }

		[NinjaScriptProperty]
		[Range(2, int.MaxValue)]
		[Display(Name = "Slow EMA Length", Description = "Slow EMA period used for each timeframe's own direction read; also the warm-up bar count required before that timeframe contributes.", GroupName = "2. Parameters", Order = 1)]
		public int SlowLen { get; set; }

		// ── Sneaky ──
		[NinjaScriptProperty]
		[Display(Name = "Publish Mtf to Sneaky", Description = "Publish the higher-timeframe alignment as SneakyCore.MtfState so consumers (Council, strategies) can consult it.", Order = 20, GroupName = "Sneaky")]
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
		[Browsable(false)] [XmlIgnore] public Series<double> Bias           => Values[0];   // -1/0/1 for display; MtfState.Bias carries the same int
		[Browsable(false)] [XmlIgnore] public Series<double> AlignmentScore => Values[1];   // -1..1
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Sneaky.Sensors.SneakyMtf_v1_0_0[] cacheSneakyMtf_v1_0_0;
		public Sneaky.Sensors.SneakyMtf_v1_0_0 SneakyMtf_v1_0_0(bool useTf1, int tf1Minutes, double weightTf1, bool useTf2, int tf2Minutes, double weightTf2, bool useTf3, int tf3Minutes, double weightTf3, bool useTf4, int tf4Minutes, double weightTf4, int fastLen, int slowLen, bool publishState, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyMtf_v1_0_0(Input, useTf1, tf1Minutes, weightTf1, useTf2, tf2Minutes, weightTf2, useTf3, tf3Minutes, weightTf3, useTf4, tf4Minutes, weightTf4, fastLen, slowLen, publishState, showCard, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Sensors.SneakyMtf_v1_0_0 SneakyMtf_v1_0_0(ISeries<double> input, bool useTf1, int tf1Minutes, double weightTf1, bool useTf2, int tf2Minutes, double weightTf2, bool useTf3, int tf3Minutes, double weightTf3, bool useTf4, int tf4Minutes, double weightTf4, int fastLen, int slowLen, bool publishState, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyMtf_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyMtf_v1_0_0.Length; idx++)
					if (cacheSneakyMtf_v1_0_0[idx] != null && cacheSneakyMtf_v1_0_0[idx].UseTf1 == useTf1 && cacheSneakyMtf_v1_0_0[idx].Tf1Minutes == tf1Minutes && cacheSneakyMtf_v1_0_0[idx].WeightTf1 == weightTf1 && cacheSneakyMtf_v1_0_0[idx].UseTf2 == useTf2 && cacheSneakyMtf_v1_0_0[idx].Tf2Minutes == tf2Minutes && cacheSneakyMtf_v1_0_0[idx].WeightTf2 == weightTf2 && cacheSneakyMtf_v1_0_0[idx].UseTf3 == useTf3 && cacheSneakyMtf_v1_0_0[idx].Tf3Minutes == tf3Minutes && cacheSneakyMtf_v1_0_0[idx].WeightTf3 == weightTf3 && cacheSneakyMtf_v1_0_0[idx].UseTf4 == useTf4 && cacheSneakyMtf_v1_0_0[idx].Tf4Minutes == tf4Minutes && cacheSneakyMtf_v1_0_0[idx].WeightTf4 == weightTf4 && cacheSneakyMtf_v1_0_0[idx].FastLen == fastLen && cacheSneakyMtf_v1_0_0[idx].SlowLen == slowLen && cacheSneakyMtf_v1_0_0[idx].PublishState == publishState && cacheSneakyMtf_v1_0_0[idx].ShowCard == showCard && cacheSneakyMtf_v1_0_0[idx].CardCorner == cardCorner && cacheSneakyMtf_v1_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyMtf_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyMtf_v1_0_0[idx];
			return CacheIndicator<Sneaky.Sensors.SneakyMtf_v1_0_0>(new Sneaky.Sensors.SneakyMtf_v1_0_0(){ UseTf1 = useTf1, Tf1Minutes = tf1Minutes, WeightTf1 = weightTf1, UseTf2 = useTf2, Tf2Minutes = tf2Minutes, WeightTf2 = weightTf2, UseTf3 = useTf3, Tf3Minutes = tf3Minutes, WeightTf3 = weightTf3, UseTf4 = useTf4, Tf4Minutes = tf4Minutes, WeightTf4 = weightTf4, FastLen = fastLen, SlowLen = slowLen, PublishState = publishState, ShowCard = showCard, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyMtf_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Sensors.SneakyMtf_v1_0_0 SneakyMtf_v1_0_0(bool useTf1, int tf1Minutes, double weightTf1, bool useTf2, int tf2Minutes, double weightTf2, bool useTf3, int tf3Minutes, double weightTf3, bool useTf4, int tf4Minutes, double weightTf4, int fastLen, int slowLen, bool publishState, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyMtf_v1_0_0(Input, useTf1, tf1Minutes, weightTf1, useTf2, tf2Minutes, weightTf2, useTf3, tf3Minutes, weightTf3, useTf4, tf4Minutes, weightTf4, fastLen, slowLen, publishState, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyMtf_v1_0_0 SneakyMtf_v1_0_0(ISeries<double> input, bool useTf1, int tf1Minutes, double weightTf1, bool useTf2, int tf2Minutes, double weightTf2, bool useTf3, int tf3Minutes, double weightTf3, bool useTf4, int tf4Minutes, double weightTf4, int fastLen, int slowLen, bool publishState, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyMtf_v1_0_0(input, useTf1, tf1Minutes, weightTf1, useTf2, tf2Minutes, weightTf2, useTf3, tf3Minutes, weightTf3, useTf4, tf4Minutes, weightTf4, fastLen, slowLen, publishState, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Sensors.SneakyMtf_v1_0_0 SneakyMtf_v1_0_0(bool useTf1, int tf1Minutes, double weightTf1, bool useTf2, int tf2Minutes, double weightTf2, bool useTf3, int tf3Minutes, double weightTf3, bool useTf4, int tf4Minutes, double weightTf4, int fastLen, int slowLen, bool publishState, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyMtf_v1_0_0(Input, useTf1, tf1Minutes, weightTf1, useTf2, tf2Minutes, weightTf2, useTf3, tf3Minutes, weightTf3, useTf4, tf4Minutes, weightTf4, fastLen, slowLen, publishState, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyMtf_v1_0_0 SneakyMtf_v1_0_0(ISeries<double> input, bool useTf1, int tf1Minutes, double weightTf1, bool useTf2, int tf2Minutes, double weightTf2, bool useTf3, int tf3Minutes, double weightTf3, bool useTf4, int tf4Minutes, double weightTf4, int fastLen, int slowLen, bool publishState, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyMtf_v1_0_0(input, useTf1, tf1Minutes, weightTf1, useTf2, tf2Minutes, weightTf2, useTf3, tf3Minutes, weightTf3, useTf4, tf4Minutes, weightTf4, fastLen, slowLen, publishState, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
