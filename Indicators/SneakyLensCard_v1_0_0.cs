// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
// ─────────────────────────────────────────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════
//  SNEAKY LENS CARD v1.0.0 — small, honestly-original wrapper
//
//  2026-08-08 — NOT a port. The real Sentinel Suite calls SneakyLens_v1_1_0's
//  LoadSummary() from a "Lens" tab inside its Deck/Dashboard UI — neither of
//  which is part of this build (whole separate subsystems, deliberately not
//  taken on tonight). This card exists purely so LoadSummary()'s output is
//  visible somewhere without needing Deck. It's original, small, and honest
//  about being original: no upstream file to diff against, because none exists.
//
//  WHAT IT DOES: on a throttled timer (default 60s — LoadSummary() re-reads
//  every *.jsonl under Sneaky\Log from scratch, so this is deliberately not
//  called every bar), shows Overall trade count / win rate / profit factor /
//  avg net ticks / MFE-capture %, plus the plain-English EyeVerdict string.
//  Read-only. Calls nothing but SneakyLens_v1_1_0.LoadSummary() — no seam
//  publish, no order placement, no Core dependency at all.
//
//  Uses the same shared scroll-card rotation as every other Sneaky card
//  (ISneakyScrollCard/ConductorRender), so it takes its turn like everything
//  else rather than claiming a fixed spot.
// ═════════════════════════════════════════════════════════════════════════════

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.AddOns.Sneaky;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors
{
    public class SneakyLensCard_v1_0_0 : Indicator, ISneakyScrollCard
    {
        private SneakySkin.Painter _sp;
        private DateTime _lastLoadUtc = DateTime.MinValue;
        private SneakyLens_v1_1_0.Summary _sum;
        private string _loadError;
        // BUGFIX (2026-08-22, P3 finding: LensCard stuck-loading): tracks consecutive failed
        // LoadSummary() attempts so RenderCard can tell "haven't tried yet / one transient blip"
        // apart from "tried repeatedly and it keeps throwing" (a permanent failure).
        private int _consecutiveFailures;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Small, original wrapper around SneakyLens_v1_1_0.LoadSummary() — shows " +
                    "overall trade count, win rate, profit factor, avg net ticks, MFE-capture %, and the " +
                    "plain-English Eye-verdict conclusion. Not a port (the real Deck/Dashboard aren't part " +
                    "of this build). Read-only, no order placement, refreshes on a throttled timer since " +
                    "LoadSummary() re-reads every log file from disk each call.";
                Name                     = "Sneaky Lens Card v1.0.0";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = false;
                IsSuspendedWhileInactive = true;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = false;
                ShowIndicatorLabel       = false;

                RefreshSeconds = 60.0;
                ShowCard       = true;
                CardCorner     = SneakyCardCorner.TopLeft;
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
            if (CurrentBar < 1) return;
            MaybeRefresh();
        }

        private void MaybeRefresh()
        {
            if ((DateTime.UtcNow - _lastLoadUtc).TotalSeconds < Math.Max(5.0, RefreshSeconds)) return;
            _lastLoadUtc = DateTime.UtcNow;
            try
            {
                _sum = SneakyLens_v1_1_0.LoadSummary();
                _loadError = _sum != null ? _sum.Error : "LoadSummary() returned null";
                // BUGFIX (2026-08-22, P3 finding: LensCard stuck-loading): any call that didn't throw
                // resets the failure streak, even if the summary carries its own soft Error string —
                // that path already renders a real message in RenderCard; only an unhandled exception
                // used to get masked as "loading…" forever.
                _consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                _sum = null;
                _loadError = ex.GetType().Name + ": " + ex.Message;
                _consecutiveFailures++;
                try { SneakyCore.Swallow("SneakyLensCard.LoadSummary", ex); } catch { }
            }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            MaybeRefresh();   // also retry on redraw — same "don't wait on a new bar" reasoning as Council's off-chart gate
            if (RenderTarget == null || ChartPanel == null) return;
            try
            {
                if (_sp == null) _sp = new SneakySkin.Painter();
                _sp.Begin(RenderTarget);
                if (ShowCard) RenderCard();
                _sp.End();
            }
            catch (Exception ex) { try { SneakyCore.Swallow("SneakyLensCard.OnRender", ex); } catch { } }
        }

        public void ConductorRender(SharpDX.Direct2D1.RenderTarget rt)
        {
            if (!ShowCard || rt == null) return;
            try { if (_sp == null) _sp = new SneakySkin.Painter(); _sp.Begin(rt); RenderCard(); }
            catch { }
            finally { try { _sp.End(); } catch { } }
        }

        private void RenderCard()
        {
            const float cw = 250f, ch = 168f;
            SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
            var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
                ChartControl.ChartPanels[0].X, ChartControl.ChartPanels[0].Y,
                ChartControl.ChartPanels[0].W, ChartControl.ChartPanels[0].H, cw, ch);
            if (slot.Width <= 0f) return;

            bool ok = _sum != null && string.IsNullOrEmpty(_sum.Error);
            var edge = !ok ? SneakySkin.CMute : (_sum.Overall.NetTicks >= 0 ? SneakySkin.CUp : SneakySkin.CDown);
            var r = _sp.Card(slot.X, slot.Y, cw, ch, edge);

            _sp.Dot(r.Left + 5f, r.Top + 8f, ok ? SneakySkin.CAccent : SneakySkin.CMute, ok);
            _sp.Text("LENS", r.Left + 16f, r.Top, r.Width - 20f, 16f, SneakySkin.CInk, 11f, true);

            if (_sum == null)
            {
                // BUGFIX (2026-08-22, P3 finding: LensCard stuck-loading): a null _sum used to always
                // render "loading…", even after LoadSummary() had thrown repeatedly (a permanent
                // failure — bad log path, corrupt file, IO error) — masking a genuinely broken feed as
                // merely slow to start. Once at least 2 consecutive attempts have actually failed (not
                // just "haven't tried yet" / a single transient blip), show the real error instead.
                if (_consecutiveFailures >= 2 && !string.IsNullOrEmpty(_loadError))
                {
                    _sp.Text("log read failed: " + _loadError, r.Left, r.Top + 26f, r.Width, 32f, SneakySkin.CMute, 9.5f);
                    return;
                }
                _sp.Text("loading…", r.Left, r.Top + 26f, r.Width, 14f, SneakySkin.CMute, 10f);
                return;
            }
            if (!ok)
            {
                _sp.Text(_loadError ?? _sum.Error ?? "no data", r.Left, r.Top + 26f, r.Width, 32f, SneakySkin.CMute, 9.5f);
                return;
            }

            var o = _sum.Overall;
            var netCol = o.NetTicks >= 0 ? SneakySkin.CUp : SneakySkin.CDown;
            _sp.Text((o.NetTicks >= 0 ? "+" : "") + o.AvgNet.ToString("0.00") + " avg   " + o.WinRate.ToString("0.0") + "% win",
                r.Left, r.Top + 24f, r.Width, 20f, netCol, 13f, true);

            _sp.Text(o.Trades + " trades   PF " + (double.IsInfinity(o.ProfitFactor) ? "∞" : o.ProfitFactor.ToString("0.00")) +
                "   MFE-cap " + o.MfeCapturePct.ToString("0") + "%",
                r.Left, r.Top + 50f, r.Width, 14f, SneakySkin.CInk2, 9.5f);

            _sp.Text("EYE: " + (_sum.EyeCoverage > 0 ? _sum.EyeCoverage + " with verdict" : "no coverage yet"),
                r.Left, r.Top + 70f, r.Width, 14f, SneakySkin.CMute, 9f, true);

            string ev = _sum.EyeVerdict ?? "(no verdict yet)";
            _sp.Text(ev, r.Left, r.Top + 86f, r.Width, 60f, SneakySkin.CInk2, 9f);

            _sp.Text("refreshes every " + RefreshSeconds.ToString("0") + "s",
                r.Left, r.Top + 148f, r.Width, 14f, SneakySkin.CMute, 8f);
        }

        [Browsable(false)] [XmlIgnore]
        public SneakyLens_v1_1_0.Summary LastSummary => _sum;

        #region Properties

        [NinjaScriptProperty] [Range(5.0, 3600.0)]
        [Display(Name = "Refresh Seconds", Description = "How often to re-call LoadSummary() (it re-reads every log file from disk, so this is deliberately throttled, not per-bar).", GroupName = "1. Sneaky", Order = 0)]
        public double RefreshSeconds { get; set; }

        [NinjaScriptProperty] [Display(Name = "Show Card", GroupName = "2. Display", Order = 0)]
        public bool ShowCard { get; set; }

        [NinjaScriptProperty] [Display(Name = "Card Corner", GroupName = "2. Display", Order = 1)]
        public SneakyCardCorner CardCorner { get; set; }

        [NinjaScriptProperty] [Display(Name = "Show Indicator Label", GroupName = "2. Display", Order = 2)]
        public bool ShowIndicatorLabel { get; set; }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Sneaky.Sensors.SneakyLensCard_v1_0_0[] cacheSneakyLensCard_v1_0_0;
		public Sneaky.Sensors.SneakyLensCard_v1_0_0 SneakyLensCard_v1_0_0(double refreshSeconds, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyLensCard_v1_0_0(Input, refreshSeconds, showCard, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Sensors.SneakyLensCard_v1_0_0 SneakyLensCard_v1_0_0(ISeries<double> input, double refreshSeconds, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyLensCard_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyLensCard_v1_0_0.Length; idx++)
					if (cacheSneakyLensCard_v1_0_0[idx] != null && cacheSneakyLensCard_v1_0_0[idx].RefreshSeconds == refreshSeconds && cacheSneakyLensCard_v1_0_0[idx].ShowCard == showCard && cacheSneakyLensCard_v1_0_0[idx].CardCorner == cardCorner && cacheSneakyLensCard_v1_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyLensCard_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyLensCard_v1_0_0[idx];
			return CacheIndicator<Sneaky.Sensors.SneakyLensCard_v1_0_0>(new Sneaky.Sensors.SneakyLensCard_v1_0_0(){ RefreshSeconds = refreshSeconds, ShowCard = showCard, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyLensCard_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Sensors.SneakyLensCard_v1_0_0 SneakyLensCard_v1_0_0(double refreshSeconds, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLensCard_v1_0_0(Input, refreshSeconds, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyLensCard_v1_0_0 SneakyLensCard_v1_0_0(ISeries<double> input , double refreshSeconds, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLensCard_v1_0_0(input, refreshSeconds, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Sensors.SneakyLensCard_v1_0_0 SneakyLensCard_v1_0_0(double refreshSeconds, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLensCard_v1_0_0(Input, refreshSeconds, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyLensCard_v1_0_0 SneakyLensCard_v1_0_0(ISeries<double> input , double refreshSeconds, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLensCard_v1_0_0(input, refreshSeconds, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
