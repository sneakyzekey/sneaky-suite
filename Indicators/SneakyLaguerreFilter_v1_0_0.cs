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
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.AddOns.Sneaky;             // SneakySkin (glass card) + SneakyCardCorner
using NinjaTrader.NinjaScript.Indicators.Sneaky.Smoothers; // own namespace (bare-enum codegen resolves here)
#endregion

// ═════════════════════════════════════════════════════════════════════════════
//  Sneaky Laguerre Filter — Ehlers 4-element Laguerre (Sneaky smoother building block)  |  Version v1.0.0
//  File: SneakyLaguerreFilter_v1_0_0.cs  |  namespace …Indicators.Sneaky.Smoothers  |  display "Sneaky LaguerreFilter"
//
//  ⚠ NO ORDERS · NO STATE SEAM — a read-only SMOOTHER building block, not a Council voter. It draws a
//  smoothed line + a Sneaky glass card; it publishes nothing (a moving average has no verdict).
//
//  PROVENANCE / LICENSE: CLEAN-ROOM. The algorithm (John Ehlers' 4-element Laguerre polynomial IIR filter)
//  was IDENTIFIED from a GPL-3.0 LizardIndicators source (amaLaguerreFilter.cs), but NO GPL code was used —
//  this is reimplemented FRESH from the canonical public formula (Ehlers, "Time Warp Without Space Travel"),
//  a mathematical method which is not copyrightable. No third-party code, variable names, or structure copied.
//
//  ASSUMPTIONS: the GPL source exposed only a Period and DERIVED gamma from it (γ = 1 − 2/(1.9 + 0.1·Period)).
//  Per the canonical Ehlers form (and this task's spec) the Sneaky version instead exposes the damping
//  factor Gamma DIRECTLY as the parameter (0..1, default 0.8) — higher gamma = smoother/laggier. Early bars
//  are seeded from the input value.
//
//  CHANGELOG
//    v1.0.0 (2026-08-17) — ported from main (identical to main's 2026-07-12 clean-room build, math
//             unchanged), mapped to Sneaky naming/namespace and re-plumbed onto live's SCROLL card
//             convention (ISneakyScrollCard / EnsureScrollHook / PlaceScroll / ConductorRender) instead of
//             main's older SneakySkin.CardLayout.Place. Standalone, purely-additive port — not referenced
//             by any other Sneaky file.
// ═════════════════════════════════════════════════════════════════════════════
namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Smoothers
{
    public class SneakyLaguerreFilter_v1_0_0 : Indicator, ISneakyScrollCard
    {
        private SneakySkin.Painter _sp;
        // cached for the card (data thread) — OnRender must never touch Value[]
        private double _cardVal;
        private int    _cardSlope;
        private bool   _cardHasData;

        private Series<double> l0, l1, l2, l3;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Sneaky smoother library — Ehlers 4-element Laguerre filter (clean-room). A smooth low-lag IIR filter tuned by the damping factor Gamma + a Sneaky glass card. A building block, not a Council voter (no State seam).";
                Name                     = "Sneaky LaguerreFilter v1.0.0";
                Calculate                = Calculate.OnPriceChange;
                IsOverlay                = true;
                IsSuspendedWhileInactive = true;
                DrawOnPricePanel         = true;

                Gamma                    = 0.8;

                ShowCard           = true;
                CardCorner         = SneakyCardCorner.TopRight;
                ShowIndicatorLabel = false;

                AddPlot(new Stroke(Brushes.MediumBlue, 2), PlotStyle.Line, "LaguerreFilter");
            }
            else if (State == State.Configure)
            {
                l0 = new Series<double>(this);
                l1 = new Series<double>(this);
                l2 = new Series<double>(this);
                l3 = new Series<double>(this);
            }
            else if (State == State.DataLoaded)
            {
                if (!ShowIndicatorLabel) Name = string.Empty;   // Sneaky label remover
            }
            else if (State == State.Terminated)
            {
                if (_sp != null) { try { _sp.Dispose(); } catch (Exception _sx) { SneakyCore.Swallow("SneakyLaguerreFilter.OnStateChange", _sx); } _sp = null; }
                try { SneakySkin.CardLayout.Release(this); } catch (Exception _sx) { SneakyCore.Swallow("SneakyLaguerreFilter.OnStateChange", _sx); }
            }
        }

        protected override void OnBarUpdate()
        {
            // Clean-room Ehlers 4-element Laguerre filter.
            double x = Input[0];
            if (CurrentBar == 0)
            {
                l0[0] = l1[0] = l2[0] = l3[0] = x;
                Value[0] = x;
                return;
            }

            double g = Gamma;
            l0[0] = (1.0 - g) * x     + g * l0[1];
            l1[0] = -g * l0[0] + l0[1] + g * l1[1];
            l2[0] = -g * l1[0] + l1[1] + g * l2[1];
            l3[0] = -g * l2[0] + l2[1] + g * l3[1];

            Value[0] = (l0[0] + 2.0 * l1[0] + 2.0 * l2[0] + l3[0]) / 6.0;
            // cache the card readout here (data thread), so OnRender never touches Value[]
            _cardVal    = Value[0];
            _cardSlope  = (CurrentBar >= 1) ? (Value[0] > Value[1] ? 1 : (Value[0] < Value[1] ? -1 : 0)) : 0;
            _cardHasData = true;
        }

        // ── Sneaky glass card ──
        protected override void OnRender(NinjaTrader.Gui.Chart.ChartControl chartControl, NinjaTrader.Gui.Chart.ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (!ShowCard || RenderTarget == null || ChartPanel == null) return;
            if (_sp == null) _sp = new SneakySkin.Painter();
            _sp.Begin(RenderTarget);
            try { RenderCard(); } catch (Exception _sx) { SneakyCore.Swallow("SneakyLaguerreFilter.OnRender", _sx); }
            try { _sp.End(); } catch (Exception _sx) { SneakyCore.Swallow("SneakyLaguerreFilter.OnRender", _sx); }   // ALWAYS runs — a skipped End() would silently kill the card
        }

        public void ConductorRender(SharpDX.Direct2D1.RenderTarget rt)
        {
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
            const float cw = 210f, ch = 92f;
            SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
            var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
                ChartControl.ChartPanels[0].X, ChartControl.ChartPanels[0].Y, ChartControl.ChartPanels[0].W, ChartControl.ChartPanels[0].H, cw, ch);
            if (slot.Width <= 0f) return;   // not the currently-selected card in the rotation

            var slopeCol = _cardSlope > 0 ? SneakySkin.CUp : _cardSlope < 0 ? SneakySkin.CDown : SneakySkin.CMute;
            string arrow = _cardSlope > 0 ? "▲" : _cardSlope < 0 ? "▼" : "▬";

            var r = _sp.Card(slot.X, slot.Y, cw, ch, SneakySkin.CLine);
            _sp.Dot(r.Left + 5f, r.Top + 8f, _cardHasData ? SneakySkin.CAccent : SneakySkin.CMute, _cardHasData);
            _sp.Text("Laguerre", r.Left + 16f, r.Top, r.Width - 70f, 16f, SneakySkin.CInk, 11f, true);
            _sp.Pill("γ" + Gamma.ToString("0.##"), r.Right, r.Top - 1f, SneakySkin.CMute);

            if (_cardHasData)
            {
                _sp.Text(arrow, r.Left, r.Top + 30f, 20f, 22f, slopeCol, 15f, false);
                _sp.Text(_cardVal.ToString("0.####"), r.Left + 22f, r.Top + 28f, r.Width - 22f, 24f, SneakySkin.CInk, 17f, false);
            }
            else
            {
                _sp.Text("loading…", r.Left, r.Top + 30f, r.Width, 16f, SneakySkin.CMute, 10.5f);
            }
        }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> LaguerreFilter => Values[0];

        #region Properties
        [Range(0.0, 1.0)]
        [NinjaScriptProperty]
        [Display(Name = "Gamma", Description = "Laguerre damping factor γ (0..1). Higher = smoother and laggier, lower = faster. Default 0.8.", Order = 1, GroupName = "Parameters")]
        public double Gamma { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Card", Order = 12, GroupName = "Sneaky")]
        public bool ShowCard { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Card Corner", Description = "Which chart corner the Sneaky card docks to. Cards in the same corner auto-stack.", Order = 13, GroupName = "Sneaky")]
        public SneakyCardCorner CardCorner { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show indicator label", Description = "Show NinjaTrader's chart name label. Sneaky default = OFF (clean chart); turn on to restore it.", Order = 100, GroupName = "Sneaky")]
        public bool ShowIndicatorLabel { get; set; }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Sneaky.Smoothers.SneakyLaguerreFilter_v1_0_0[] cacheSneakyLaguerreFilter_v1_0_0;
		public Sneaky.Smoothers.SneakyLaguerreFilter_v1_0_0 SneakyLaguerreFilter_v1_0_0(double gamma, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyLaguerreFilter_v1_0_0(Input, gamma, showCard, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Smoothers.SneakyLaguerreFilter_v1_0_0 SneakyLaguerreFilter_v1_0_0(ISeries<double> input, double gamma, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyLaguerreFilter_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyLaguerreFilter_v1_0_0.Length; idx++)
					if (cacheSneakyLaguerreFilter_v1_0_0[idx] != null && cacheSneakyLaguerreFilter_v1_0_0[idx].Gamma == gamma && cacheSneakyLaguerreFilter_v1_0_0[idx].ShowCard == showCard && cacheSneakyLaguerreFilter_v1_0_0[idx].CardCorner == cardCorner && cacheSneakyLaguerreFilter_v1_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyLaguerreFilter_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyLaguerreFilter_v1_0_0[idx];
			return CacheIndicator<Sneaky.Smoothers.SneakyLaguerreFilter_v1_0_0>(new Sneaky.Smoothers.SneakyLaguerreFilter_v1_0_0(){ Gamma = gamma, ShowCard = showCard, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyLaguerreFilter_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Smoothers.SneakyLaguerreFilter_v1_0_0 SneakyLaguerreFilter_v1_0_0(double gamma, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLaguerreFilter_v1_0_0(Input, gamma, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Smoothers.SneakyLaguerreFilter_v1_0_0 SneakyLaguerreFilter_v1_0_0(ISeries<double> input , double gamma, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLaguerreFilter_v1_0_0(input, gamma, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Smoothers.SneakyLaguerreFilter_v1_0_0 SneakyLaguerreFilter_v1_0_0(double gamma, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLaguerreFilter_v1_0_0(Input, gamma, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Smoothers.SneakyLaguerreFilter_v1_0_0 SneakyLaguerreFilter_v1_0_0(ISeries<double> input , double gamma, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLaguerreFilter_v1_0_0(input, gamma, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
