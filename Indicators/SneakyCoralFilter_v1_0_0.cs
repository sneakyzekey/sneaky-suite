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
//  Sneaky CoralFilter — Tillson T3 triple-smoothed EMA ("Coral" trend filter)   |   Version v1.0.0
//  File: SneakyCoralFilter_v1_0_0.cs  |  namespace …Indicators.Sneaky.Smoothers  |  display "Sneaky CoralFilter"
//
//  ⚠ NO ORDERS · NO STATE SEAM — a read-only SMOOTHER building block, not a Council voter. It draws the
//  T3-smoothed line + a Sneaky glass card and publishes nothing (a moving average has no verdict). A
//  low-lag baseline the signal tools can consume, and a Sneaky-branded T3/Coral filter in its own right.
//
//  PROVENANCE / LICENSE: CLEAN-ROOM. Algorithm identified from a GPL LizardIndicators source
//  (amaCoralFilter.cs, GPL-3.0) but NO GPL code was used — reimplemented from the canonical PUBLIC
//  Tillson T3 formula (Tim Tillson, "Better Moving Averages", TASC 1998), a mathematical method that is
//  not copyrightable. The "Coral" filter is a T3 with volume-factor d and an EMA cascade whose smoothing
//  factor derives from the period. No third-party code/variable-names/structure were copied.
//
//    CANONICAL T3 (six cascaded EMAs e1..e6, α = 2/(1+k), k = 1 + (Period-1)/2, d = coefficient):
//        e1 = α·src + (1-α)·e1₋₁   … e6 = α·e5 + (1-α)·e6₋₁
//        T3 = c1·e6 + c2·e5 + c3·e4 + c4·e3
//        c1 = -d³ ,  c2 = 3d²+3d³ ,  c3 = -(3d+6d²+3d³) ,  c4 = 1+3d+3d²+d³
//
//  ASSUMPTIONS: (1) d default = 0.4 (the common Coral/T3 volume factor); exposed as CoefficientMultiplier.
//  (2) EMA smoothing factor derived via k = 1 + (Period-1)/2 → α = 2/(1+k) (the standard T3 period mapping).
//  (3) Dropped the source's trend-classification / neutral-threshold / paint-bar / sound-alert machinery —
//  this is a pure smoother, so only Period + CoefficientMultiplier survive. (4) Early bars seed the cascade
//  to the first input.
//
//  NOTE: mathematically near-identical to SneakyTillsonT3 (both are the canonical T3 6-EMA cascade — Coral's
//  k-derived alpha simplifies to the same 2/(Period+1) TillsonT3 uses directly). Kept as two indicators
//  because main ships them as two indicators with different defaults/branding (Coral: Period 34, d 0.4;
//  TillsonT3: Period 14, VFactor 0.7) — ported as-is rather than merged, to match main 1:1.
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
    public class SneakyCoralFilter_v1_0_0 : Indicator, ISneakyScrollCard
    {
        private SneakySkin.Painter _sp;
        // cached for the card (data thread) — OnRender must never touch Value[]
        private double _cardVal;
        private int    _cardSlope;
        private bool   _cardHasData;

        // T3 cascade state
        private double[] _e     = new double[6];   // current-bar cascade values e1..e6
        private double[] _ePrev = new double[6];   // committed previous-bar cascade values
        private double   _alpha, _beta;            // α and (1-α)
        private double   _c1, _c2, _c3, _c4;       // T3 combining coefficients

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Sneaky smoother library — Coral / Tillson-T3 triple-smoothed EMA (clean-room). Draws a low-lag T3 filter of the input + a Sneaky glass card. A building block, not a Council voter (no State seam).";
                Name                     = "Sneaky CoralFilter v1.0.0";
                Calculate                = Calculate.OnPriceChange;
                IsOverlay                = true;
                IsSuspendedWhileInactive = true;
                DrawOnPricePanel         = true;

                Period                   = 34;
                CoefficientMultiplier    = 0.4;

                ShowCard           = true;
                CardCorner         = SneakyCardCorner.TopRight;
                ShowIndicatorLabel = false;

                AddPlot(new Stroke(Brushes.SkyBlue, 2), PlotStyle.Line, "CoralFilter");
            }
            else if (State == State.Configure)
            {
                // Clean-room T3 intermediates (computed once; Period + CoefficientMultiplier are set by now).
                double k = 1.0 + (Period - 1.0) / 2.0;
                _alpha   = 2.0 / (1.0 + k);
                _beta    = 1.0 - _alpha;

                double d  = CoefficientMultiplier;
                double d2 = d * d;
                double d3 = d * d2;
                _c1 = -d3;
                _c2 = 3.0 * (d2 + d3);
                _c3 = -(3.0 * d + 6.0 * d2 + 3.0 * d3);
                _c4 = 1.0 + 3.0 * d + 3.0 * d2 + d3;
            }
            else if (State == State.DataLoaded)
            {
                if (!ShowIndicatorLabel) Name = string.Empty;   // Sneaky label remover
            }
            else if (State == State.Terminated)
            {
                if (_sp != null) { try { _sp.Dispose(); } catch (Exception _sx) { SneakyCore.Swallow("SneakyCoralFilter.OnStateChange", _sx); } _sp = null; }
                try { SneakySkin.CardLayout.Release(this); } catch (Exception _sx) { SneakyCore.Swallow("SneakyCoralFilter.OnStateChange", _sx); }
            }
        }

        protected override void OnBarUpdate()
        {
            double src = Input[0];

            if (CurrentBar == 0)
            {
                for (int i = 0; i < 6; i++) { _e[i] = src; _ePrev[i] = src; }
                Value[0] = src;
                return;
            }

            // On the first tick of a new bar, freeze last bar's cascade as the recurrence base.
            if (IsFirstTickOfBar)
                Array.Copy(_e, _ePrev, 6);

            _e[0] = _alpha * src   + _beta * _ePrev[0];
            _e[1] = _alpha * _e[0] + _beta * _ePrev[1];
            _e[2] = _alpha * _e[1] + _beta * _ePrev[2];
            _e[3] = _alpha * _e[2] + _beta * _ePrev[3];
            _e[4] = _alpha * _e[3] + _beta * _ePrev[4];
            _e[5] = _alpha * _e[4] + _beta * _ePrev[5];

            Value[0] = _c1 * _e[5] + _c2 * _e[4] + _c3 * _e[3] + _c4 * _e[2];
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
            try { RenderCard(); } catch (Exception _sx) { SneakyCore.Swallow("SneakyCoralFilter.OnRender", _sx); }
            try { _sp.End(); } catch (Exception _sx) { SneakyCore.Swallow("SneakyCoralFilter.OnRender", _sx); }   // ALWAYS runs — a skipped End() would silently kill the card
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
            _sp.Text("Coral", r.Left + 16f, r.Top, r.Width - 70f, 16f, SneakySkin.CInk, 11f, true);
            _sp.Pill("p" + Period, r.Right, r.Top - 1f, SneakySkin.CMute);

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
        public Series<double> CoralFilter => Values[0];

        #region Properties
        [Range(1, int.MaxValue)]
        [NinjaScriptProperty]
        [Display(Name = "Period", Description = "Lookback that sets the T3 EMA smoothing factor.", Order = 1, GroupName = "Parameters")]
        public int Period { get; set; }

        [Range(0.0, 1.0)]
        [NinjaScriptProperty]
        [Display(Name = "Coefficient Multiplier", Description = "T3 volume factor d (0..1). Higher = more responsive / more overshoot. Coral default 0.4.", Order = 2, GroupName = "Parameters")]
        public double CoefficientMultiplier { get; set; }

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
		private Sneaky.Smoothers.SneakyCoralFilter_v1_0_0[] cacheSneakyCoralFilter_v1_0_0;
		public Sneaky.Smoothers.SneakyCoralFilter_v1_0_0 SneakyCoralFilter_v1_0_0(int period, double coefficientMultiplier, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyCoralFilter_v1_0_0(Input, period, coefficientMultiplier, showCard, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Smoothers.SneakyCoralFilter_v1_0_0 SneakyCoralFilter_v1_0_0(ISeries<double> input, int period, double coefficientMultiplier, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyCoralFilter_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyCoralFilter_v1_0_0.Length; idx++)
					if (cacheSneakyCoralFilter_v1_0_0[idx] != null && cacheSneakyCoralFilter_v1_0_0[idx].Period == period && cacheSneakyCoralFilter_v1_0_0[idx].CoefficientMultiplier == coefficientMultiplier && cacheSneakyCoralFilter_v1_0_0[idx].ShowCard == showCard && cacheSneakyCoralFilter_v1_0_0[idx].CardCorner == cardCorner && cacheSneakyCoralFilter_v1_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyCoralFilter_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyCoralFilter_v1_0_0[idx];
			return CacheIndicator<Sneaky.Smoothers.SneakyCoralFilter_v1_0_0>(new Sneaky.Smoothers.SneakyCoralFilter_v1_0_0(){ Period = period, CoefficientMultiplier = coefficientMultiplier, ShowCard = showCard, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyCoralFilter_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Smoothers.SneakyCoralFilter_v1_0_0 SneakyCoralFilter_v1_0_0(int period, double coefficientMultiplier, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyCoralFilter_v1_0_0(Input, period, coefficientMultiplier, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Smoothers.SneakyCoralFilter_v1_0_0 SneakyCoralFilter_v1_0_0(ISeries<double> input , int period, double coefficientMultiplier, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyCoralFilter_v1_0_0(input, period, coefficientMultiplier, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Smoothers.SneakyCoralFilter_v1_0_0 SneakyCoralFilter_v1_0_0(int period, double coefficientMultiplier, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyCoralFilter_v1_0_0(Input, period, coefficientMultiplier, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Smoothers.SneakyCoralFilter_v1_0_0 SneakyCoralFilter_v1_0_0(ISeries<double> input , int period, double coefficientMultiplier, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyCoralFilter_v1_0_0(input, period, coefficientMultiplier, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
