// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
//
// Copyright (c) 2026 silentsudo-io and the Sentinel Suite contributors.
// ─────────────────────────────────────────────────────────────────────────────
//
// ═════════════════════════════════════════════════════════════════════════════
//  SNEAKY CLOCK v1.0.0 — session phase and time-in-session context.
//
//  Publishes ClockState against SneakyCore's contract (seam v1.9.0):
//      Phase 0 Closed/pre-open · 1 Open-drive · 2 Midday · 3 Close
//      MinsSinceOpen · MinsToClose · DayOfWeek · InSession · InKillWindow
//
//  ── WHAT IT IS FOR ──────────────────────────────────────────────────────────
//  Time of day is the most reliably non-random thing about an index future. The
//  open drives, midday drifts, the close reprices. Nothing else in this suite
//  knows what time it is, so every other sensor treats 03:00 and 09:35 as the
//  same kind of bar. This publishes that context so consumers can stop doing so.
//
//  It pairs directly with Participation: "is this volume unusual" and "what phase
//  are we in" are two halves of one question, and neither answers it alone.
//
//  ── NOT A VOTER, AND NOT A FILTER ───────────────────────────────────────────
//  The clock has NO DIRECTION — 09:35 does not mean up. It is context, and it is
//  published as context. This tool deliberately does NOT block anything: it does
//  not decide that midday is bad or that the open is good, because that is a
//  trading opinion and it belongs to you, in the consumer, measured against data.
//  InKillWindow is the single exception, and it is a mechanical rule (do not open
//  new risk N minutes before the close) rather than an edge claim.
//
//  If you want "no entries midday", record first and find out whether midday is
//  actually worse on YOUR instrument. The recorder now writes clock phase on
//  every row, so that is a measurable question rather than a folk belief.
//
//  ── PHASE BOUNDARIES ARE CONFIGURABLE BECAUSE THEY ARE NOT UNIVERSAL ────────
//  The defaults below are RTH-shaped for US index futures. They are round-number
//  conventions, not measured boundaries — "the first 60 minutes drive" is a
//  reasonable prior, not a finding. Instruments with different session shapes
//  need different numbers.
//
//  Session bounds come from the chart's own TradingHours template, so the tool
//  follows whatever session you have configured rather than hard-coding a clock.
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
	public class SneakyClock_v1_0_0 : Indicator, ISneakyScrollCard
	{
		private int      _phase;
		private int      _sinceOpen = -1, _toClose = -1;
		private bool     _inSession, _inKill;
		private DateTime _sessionStart, _sessionEnd;
		private bool     _haveSession;
		private SessionIterator _si;

		private SneakySkin.Painter _sp;
		private DateTime _lastRenderExLog = DateTime.MinValue;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Session phase and time-in-session context — publishes ClockState (phase, minutes since "
					+ "open, minutes to close, day of week, in-session, kill window). Time of day is the most reliably "
					+ "non-random thing about an index future, and nothing else in the suite knows what time it is. "
					+ "Has NO DIRECTION so it never votes, and deliberately does NOT block anything except the "
					+ "mechanical near-close kill window — whether midday is actually worse on your instrument is a "
					+ "measurable question, not a folk belief. Session bounds come from the chart's TradingHours "
					+ "template. Instrument-keyed. Places no orders.";
				Name                     = "Sneaky Clock v1.0.0";
				Calculate                = Calculate.OnBarClose;
				IsOverlay                = true;
				DisplayInDataBox         = true;
				PaintPriceMarkers        = false;
				IsSuspendedWhileInactive = false;
				IsAutoScale              = false;
				ShowIndicatorLabel       = false;

				OpenDriveMinutes  = 60;
				CloseWindowMinutes = 60;
				KillWindowMinutes = 15;
				PublishClock      = true;

				ShowCard   = true;
				CardCorner = SneakyCardCorner.TopLeft;

				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Phase");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "MinsSinceOpen");
			}
			else if (State == State.DataLoaded)
			{
				// Instance SessionIterator — the same pattern SneakyDrift/SneakyEffort use.
				// (An earlier draft called a static GetTradingDayBeginLocal(); that was a
				// guess at an API that does not exist in this form.)
				try { _si = new SessionIterator(Bars); } catch { _si = null; }
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
			if (CurrentBar < 1) return;

			// Session bounds from the chart's OWN TradingHours template via SessionIterator,
			// so this follows whatever session is configured rather than hard-coding a clock
			// that would be wrong for half the instruments it might run on.
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

			DateTime now = Time[0];

			if (_haveSession && _sessionEnd > _sessionStart)
			{
				_inSession = now >= _sessionStart && now <= _sessionEnd;
				_sinceOpen = _inSession ? (int)(now - _sessionStart).TotalMinutes : -1;
				_toClose   = _inSession ? (int)(_sessionEnd - now).TotalMinutes  : -1;
			}
			else
			{
				// No usable session template — report honestly rather than inventing bounds.
				_inSession = false; _sinceOpen = -1; _toClose = -1;
			}

			if (!_inSession)                                _phase = 0;
			else if (_sinceOpen < OpenDriveMinutes)         _phase = 1;
			else if (_toClose  <= CloseWindowMinutes)       _phase = 3;
			else                                            _phase = 2;

			_inKill = _inSession && _toClose >= 0 && _toClose <= KillWindowMinutes;

			Values[0][0] = _phase;
			Values[1][0] = _sinceOpen;

			if (PublishClock)
			{
				try
				{
					// INSTRUMENT-keyed per the Core contract: what time it is does not depend
					// on your bar type, so one Clock serves every chart of this instrument.
					string inst = Instrument != null && Instrument.MasterInstrument != null
								? Instrument.MasterInstrument.Name : null;
					if (!string.IsNullOrEmpty(inst))
						SneakyCore.SetClockState(inst, _phase, _sinceOpen, _toClose,
							(int)now.DayOfWeek, _inSession, _inKill, "SneakyClock");
				}
				catch (Exception ex) { try { Print("[SneakyClock] publish failed: " + ex.Message); } catch { } }
			}
		}

		private string PhaseName()
		{
			switch (_phase)
			{
				case 1:  return "OPEN DRIVE";
				case 2:  return "MIDDAY";
				case 3:  return "CLOSE";
				default: return "CLOSED";
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
				{ _lastRenderExLog = DateTime.UtcNow; try { Print("[SneakyClock] card threw: " + ex.Message); } catch { } }
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
			const float cw = 250f, ch = 156f;
			SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
			var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
				ChartControl.ChartPanels[0].X, ChartControl.ChartPanels[0].Y, ChartControl.ChartPanels[0].W, ChartControl.ChartPanels[0].H, cw, ch);
			if (slot.Width <= 0f) return;   // not the currently-selected card in the rotation

			var col = !_inSession ? SneakySkin.CMute
					: _inKill     ? SneakySkin.CWarn
					: _phase == 1 ? SneakySkin.CUp
					: _phase == 3 ? SneakySkin.CInk2 : SneakySkin.CInk2;

			var r = _sp.Card(slot.X, slot.Y, cw, ch, col);
			_sp.Dot(r.Left + 5f, r.Top + 8f, col, _inSession);
			_sp.Text("CLOCK", r.Left + 16f, r.Top, r.Width - 20f, 16f, SneakySkin.CInk, 11f, true);

			_sp.Text(PhaseName(), r.Left, r.Top + 24f, r.Width, 20f, col, 15f, true);

			if (_inSession)
			{
				_sp.Text("+" + _sinceOpen + "m since open    " + _toClose + "m to close",
					r.Left, r.Top + 48f, r.Width, 14f, SneakySkin.CInk2, 10.5f);
				_sp.Text(_sessionStart.ToString("HH:mm") + " \u2192 " + _sessionEnd.ToString("HH:mm")
						 + "   " + Time[0].DayOfWeek.ToString().Substring(0, 3),
					r.Left, r.Top + 66f, r.Width, 14f, SneakySkin.CMute, 9.5f);
			}
			else
			{
				_sp.Text(_haveSession ? "outside session hours" : "no usable TradingHours template",
					r.Left, r.Top + 48f, r.Width, 14f,
					_haveSession ? SneakySkin.CMute : SneakySkin.CWarn, 10f, !_haveSession);
			}

			if (_inKill)
				_sp.Text("KILL WINDOW \u2014 no new risk (" + KillWindowMinutes + "m)",
					r.Left, r.Top + 88f, r.Width, 14f, SneakySkin.CWarn, 10f, true);

			_sp.Text("phases: drive <" + OpenDriveMinutes + "m \u00b7 close last " + CloseWindowMinutes + "m",
				r.Left, r.Top + 110f, r.Width, 14f, SneakySkin.CMute, 8.5f);
			_sp.Text("context only \u2014 no direction, blocks nothing",
				r.Left, r.Top + 128f, r.Width, 14f, SneakySkin.CMute, 8f);
		}

		[Browsable(false)] [XmlIgnore] public Series<double> Phase         => Values[0];
		[Browsable(false)] [XmlIgnore] public Series<double> MinsSinceOpen => Values[1];

		#region Properties

		[NinjaScriptProperty] [Range(1, 600)]
		[Display(Name = "Open-Drive Minutes", Description = "Minutes after the session open counted as phase 1. A round-number convention, not a measured boundary — worth checking against recorder data on your instrument.", GroupName = "1. Phases", Order = 0)]
		public int OpenDriveMinutes { get; set; }

		[NinjaScriptProperty] [Range(1, 600)]
		[Display(Name = "Close-Window Minutes", Description = "Minutes before the session close counted as phase 3.", GroupName = "1. Phases", Order = 1)]
		public int CloseWindowMinutes { get; set; }

		[NinjaScriptProperty] [Range(0, 240)]
		[Display(Name = "Kill Window Minutes", Description = "Minutes before the close flagged as InKillWindow — the one mechanical rule here (do not open new risk into the bell). This tool only PUBLISHES the flag; it does not enforce it.", GroupName = "1. Phases", Order = 2)]
		public int KillWindowMinutes { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Publish Clock to Sneaky", Description = "INSTRUMENT-keyed per the Core contract — what time it is does not depend on your bar type, so one Clock serves every chart of this instrument.", GroupName = "2. Sneaky", Order = 0)]
		public bool PublishClock { get; set; }

		[NinjaScriptProperty] [Display(Name = "Show Card", GroupName = "3. Display", Order = 0)] public bool ShowCard { get; set; }
		[NinjaScriptProperty] [Display(Name = "Card Corner", GroupName = "3. Display", Order = 1)] public SneakyCardCorner CardCorner { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show Indicator Label", GroupName = "3. Display", Order = 2)] public bool ShowIndicatorLabel { get; set; }

		#endregion
	}
}
