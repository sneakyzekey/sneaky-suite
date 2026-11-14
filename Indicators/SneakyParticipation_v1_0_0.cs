// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
//
// Copyright (c) 2026 silentsudo-io and the Sentinel Suite contributors.
// ─────────────────────────────────────────────────────────────────────────────
//
// ═════════════════════════════════════════════════════════════════════════════
//  SNEAKY PARTICIPATION v1.0.0 — relative volume, climax and dry-up.
//
//  Publishes ParticipationState against SneakyCore's contract (seam added v1.9.0,
//  migrated to SCOPE keying in v1.21.0).
//
//  ── THE ONE DECISION THAT MAKES THIS USEFUL OR USELESS ──────────────────────
//  RVOL is "this bar's volume ÷ TYPICAL volume". Everything hinges on what
//  "typical" means, and the obvious answer is the wrong one.
//
//  A plain rolling average over the last N bars is what most RVOL tools do, and
//  on a futures instrument it is close to worthless: index futures have a brutal
//  intraday volume profile. The RTH open and the close are many times heavier
//  than midday. Against a flat rolling mean, EVERY midday bar reads as dry-up and
//  EVERY opening bar reads as climax — the tool would just be re-reporting the
//  clock, dressed up as participation.
//
//  So the default baseline here is TIME-OF-DAY AWARE: this bar's volume is
//  compared against the typical volume for THIS TIME OF DAY, learned over the
//  last N sessions. A heavy midday bar then reads heavy — which is the entire
//  point, because unusual participation at an unusual time is the signal.
//
//  RollingBaseline=true reverts to the naive flat mean if you want it, and the
//  card says which mode is live and how warm the profile is. Until the buckets
//  fill it falls back to the rolling mean automatically and reports "warming".
//
//  ── WHY THIS IS NOT A COUNCIL VOTER ─────────────────────────────────────────
//  Participation has NO DIRECTION. Heavy volume does not mean up or down — it
//  means conviction behind whatever is happening. Wiring it as a voter would
//  require inventing a direction it does not have. It belongs as a MODULATOR
//  (damp conviction in a participation vacuum) and as context for other tools,
//  which is how the Council consumes it.
//
//  SCOPE-keyed, per the Core comment: "a 150-tick RVOL ≠ a TBars RVOL", so this
//  reading is genuinely per-chart. Load one on each chart that runs a Council.
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
	public class SneakyParticipation_v1_0_0 : Indicator, ISneakyScrollCard
	{
		private string _scope;
		private string Scope()
		{
			try { _scope = SneakyCore.ScopeOf(Instrument, BarsPeriod, ChartControl); } catch { }   // 2026-08-04 — ALWAYS recomputed, never permanently cached: a chart's lane can register AFTER this indicator's first call, so caching once risked freezing a stale, unlaned scope forever (confirmed happening for real tonight). Cheap to recompute every call; only overwrites _scope on success, so a transient throw keeps the previous good value.
			return _scope;
		}

		// time-of-day buckets: key = minutes since midnight / BucketMinutes
		private readonly Dictionary<int, List<double>> _tod = new Dictionary<int, List<double>>();
		private readonly List<double> _recent = new List<double>();   // flat rolling window

		private double _rvol = 1.0, _volZ, _typical;
		private bool   _climax, _dryUp, _todWarm;
		private int    _warmBuckets;

		private SneakySkin.Painter _sp;
		private DateTime _lastRenderExLog = DateTime.MinValue;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Relative volume, climax and dry-up — publishes ParticipationState. RVOL is measured "
					+ "against a TIME-OF-DAY baseline by default, not a flat rolling mean: index futures have a "
					+ "severe intraday volume profile, so against a flat mean every midday bar reads as dry-up and "
					+ "every opening bar as climax, which just re-reports the clock. Learning the typical volume "
					+ "for THIS time of day makes unusual participation at an unusual time visible, which is the "
					+ "actual signal. Has NO DIRECTION, so it is a modulator and context source rather than a "
					+ "Council voter. SCOPE-keyed (a 150-tick RVOL is not a TBars RVOL). Places no orders.";
				Name                     = "Sneaky Participation v1.0.0";
				Calculate                = Calculate.OnBarClose;
				IsOverlay                = true;
				DisplayInDataBox         = true;
				PaintPriceMarkers        = false;
				IsSuspendedWhileInactive = false;
				IsAutoScale              = false;
				ShowIndicatorLabel       = false;

				RollingBaseline   = false;   // false = time-of-day aware (recommended)
				BucketMinutes     = 15;
				BaselineSessions  = 10;
				RollingLookback   = 60;
				MinBucketSamples  = 3;

				ClimaxZ           = 2.0;
				DryUpRvol         = 0.60;

				PublishParticipation = true;

				ShowCard   = true;
				CardCorner = SneakyCardCorner.TopLeft;

				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Rvol");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "VolZ");
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

			double vol = Volume[0];
			if (vol <= 0) return;

			// ── flat rolling window (fallback + optional mode) ──
			_recent.Add(vol);
			if (_recent.Count > Math.Max(5, RollingLookback)) _recent.RemoveAt(0);

			// ── time-of-day bucket ──
			int key = (Time[0].Hour * 60 + Time[0].Minute) / Math.Max(1, BucketMinutes);
			List<double> bucket;
			if (!_tod.TryGetValue(key, out bucket)) { bucket = new List<double>(); _tod[key] = bucket; }
			bucket.Add(vol);
			// keep only the last N sessions' worth of samples for this slot
			int cap = Math.Max(1, BaselineSessions);
			while (bucket.Count > cap) bucket.RemoveAt(0);

			_warmBuckets = 0;
			foreach (var kv in _tod) if (kv.Value.Count >= MinBucketSamples) _warmBuckets++;

			// ── choose the baseline ──
			// The bucket EXCLUDES the current bar from its own baseline — otherwise a
			// climax bar inflates the very number it is being measured against.
			_todWarm = !RollingBaseline && bucket.Count > MinBucketSamples;
			if (_todWarm)
			{
				double s = 0; int n = 0;
				for (int i = 0; i < bucket.Count - 1; i++) { s += bucket[i]; n++; }
				_typical = n > 0 ? s / n : vol;
			}
			else
			{
				double s = 0; int n = 0;
				for (int i = 0; i < _recent.Count - 1; i++) { s += _recent[i]; n++; }
				_typical = n > 0 ? s / n : vol;
			}

			_rvol = _typical > 0 ? vol / _typical : 1.0;

			// ── z-score against the same population the baseline came from ──
			var pop = _todWarm ? bucket : _recent;
			double mean = 0; int cnt = 0;
			for (int i = 0; i < pop.Count - 1; i++) { mean += pop[i]; cnt++; }
			if (cnt > 1)
			{
				mean /= cnt;
				double ss = 0;
				for (int i = 0; i < pop.Count - 1; i++) { double d = pop[i] - mean; ss += d * d; }
				double sd = Math.Sqrt(ss / cnt);
				_volZ = sd > 0 ? (vol - mean) / sd : 0.0;
			}
			else _volZ = 0.0;

			_climax = _volZ >= ClimaxZ;
			_dryUp  = _rvol <= DryUpRvol;

			Values[0][0] = _rvol;
			Values[1][0] = _volZ;

			if (PublishParticipation)
			{
				try
				{
					string sc = Scope();
					if (!string.IsNullOrEmpty(sc))
						SneakyCore.SetParticipationState(sc, SneakyCore.BarTag(BarsPeriod),
							Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : null,
							_rvol, _volZ, _climax, _dryUp, _typical,
							_todWarm ? "SneakyParticipation/tod" : "SneakyParticipation/rolling");
				}
				catch (Exception ex) { try { Print("[SneakyParticipation] publish failed: " + ex.Message); } catch { } }
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
					try { Print("[SneakyParticipation] RenderCard threw: " + ex.Message); } catch { }
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
			const float cw = 250f, ch = 168f;
			SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
			var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
				ChartControl.ChartPanels[0].X, ChartControl.ChartPanels[0].Y, ChartControl.ChartPanels[0].W, ChartControl.ChartPanels[0].H, cw, ch);
			if (slot.Width <= 0f) return;   // not the currently-selected card in the rotation

			var col = _climax ? SneakySkin.CWarn
					: _dryUp  ? SneakySkin.CMute
					: _rvol >= 1.0 ? SneakySkin.CUp : SneakySkin.CInk2;

			var r = _sp.Card(slot.X, slot.Y, cw, ch, col);
			_sp.Dot(r.Left + 5f, r.Top + 8f, col, _climax || _rvol >= 1.0);
			_sp.Text("PARTICIPATION", r.Left + 16f, r.Top, r.Width - 20f, 16f, SneakySkin.CInk, 11f, true);

			_sp.Text(_climax ? "CLIMAX" : _dryUp ? "DRY UP" : _rvol >= 1.0 ? "BACKED" : "LIGHT",
				r.Left, r.Top + 24f, r.Width, 20f, col, 15f, true);

			_sp.Text("rvol " + _rvol.ToString("0.00") + "\u00d7    z " + _volZ.ToString("+0.00;-0.00;0.00"),
				r.Left, r.Top + 48f, r.Width, 14f, SneakySkin.CInk2, 10.5f);

			_sp.Text("typical " + _typical.ToString("#,0") + "   this bar " + Volume[0].ToString("#,0"),
				r.Left, r.Top + 66f, r.Width, 14f, SneakySkin.CMute, 9.5f);

			// Which baseline is actually live matters more than any other line here.
			_sp.Text(_todWarm ? "baseline: TIME-OF-DAY (" + BucketMinutes + "m buckets)"
							  : RollingBaseline ? "baseline: rolling mean (flat)"
												: "baseline: rolling \u2014 time-of-day WARMING",
				r.Left, r.Top + 86f, r.Width, 14f,
				_todWarm ? SneakySkin.CInk2 : SneakySkin.CWarn, 9.5f, !_todWarm);

			_sp.Text("warm slots " + _warmBuckets + "   samples this slot "
					 + (_tod.Count > 0 ? _tod[(Time[0].Hour * 60 + Time[0].Minute) / Math.Max(1, BucketMinutes)].Count : 0)
					 + "/" + BaselineSessions,
				r.Left, r.Top + 104f, r.Width, 14f, SneakySkin.CMute, 9f);

			_sp.Text("no direction \u2014 modulator/context, not a voter",
				r.Left, r.Top + 126f, r.Width, 14f, SneakySkin.CMute, 8f);
		}

		[Browsable(false)] [XmlIgnore] public Series<double> Rvol => Values[0];
		[Browsable(false)] [XmlIgnore] public Series<double> VolZ => Values[1];

		#region Properties

		[NinjaScriptProperty]
		[Display(Name = "Use Flat Rolling Baseline", Description = "OFF (recommended) compares this bar against the typical volume for THIS TIME OF DAY. ON reverts to a flat rolling mean — which on index futures makes every midday bar read dry-up and every opening bar read climax, because it is really just measuring the intraday volume profile.", GroupName = "1. Baseline", Order = 0)]
		public bool RollingBaseline { get; set; }

		[NinjaScriptProperty] [Range(1, 240)]
		[Display(Name = "Time-of-Day Bucket (minutes)", Description = "Width of each time slot. Smaller = finer profile but slower to warm.", GroupName = "1. Baseline", Order = 1)]
		public int BucketMinutes { get; set; }

		[NinjaScriptProperty] [Range(2, 200)]
		[Display(Name = "Baseline Sessions", Description = "How many prior samples per time slot to average. Roughly = sessions of history needed to be fully warm.", GroupName = "1. Baseline", Order = 2)]
		public int BaselineSessions { get; set; }

		[NinjaScriptProperty] [Range(2, 100)]
		[Display(Name = "Min Samples Before Trusting Slot", Description = "A slot with fewer samples than this falls back to the rolling mean, and the card says so.", GroupName = "1. Baseline", Order = 3)]
		public int MinBucketSamples { get; set; }

		[NinjaScriptProperty] [Range(5, 1000)]
		[Display(Name = "Rolling Lookback (bars)", Description = "Window for the flat rolling mean, used as fallback and when the flat mode is on.", GroupName = "1. Baseline", Order = 4)]
		public int RollingLookback { get; set; }

		[NinjaScriptProperty] [Range(0.5, 10.0)]
		[Display(Name = "Climax Z", Description = "Volume z-score at or above which Climax is set — blow-off participation.", GroupName = "2. Thresholds", Order = 0)]
		public double ClimaxZ { get; set; }

		[NinjaScriptProperty] [Range(0.05, 1.0)]
		[Display(Name = "Dry-Up RVOL", Description = "RVOL at or below which DryUp is set — a participation vacuum.", GroupName = "2. Thresholds", Order = 1)]
		public double DryUpRvol { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Publish Participation to Sneaky", Description = "SCOPE-keyed per the Core contract, because RVOL is bar-type dependent. Load one on each chart that runs a Council.", GroupName = "3. Sneaky", Order = 0)]
		public bool PublishParticipation { get; set; }

		[NinjaScriptProperty] [Display(Name = "Show Card", GroupName = "4. Display", Order = 0)] public bool ShowCard { get; set; }
		[NinjaScriptProperty] [Display(Name = "Card Corner", GroupName = "4. Display", Order = 1)] public SneakyCardCorner CardCorner { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show Indicator Label", GroupName = "4. Display", Order = 2)] public bool ShowIndicatorLabel { get; set; }

		#endregion
	}
}
