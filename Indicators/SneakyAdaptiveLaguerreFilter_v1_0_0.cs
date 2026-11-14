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
//  Sneaky Adaptive Laguerre Filter — Ehlers self-adjusting Laguerre (Sneaky smoother building block)  |  Version v1.0.0
//  File: SneakyAdaptiveLaguerreFilter_v1_0_0.cs  |  namespace …Indicators.Sneaky.Smoothers  |  display "Sneaky AdaptiveLaguerreFilter"
//
//  ⚠ NO ORDERS · NO STATE SEAM — a read-only SMOOTHER building block, not a Council voter. It draws a
//  smoothed line + a Sneaky glass card; it publishes nothing (a moving average has no verdict).
//
//  PROVENANCE / LICENSE: CLEAN-ROOM. The algorithm (John Ehlers' ADAPTIVE 4-element Laguerre filter — the
//  same Laguerre polynomial IIR filter, but its damping factor self-adjusts each bar from the normalized
//  recent tracking error) was IDENTIFIED from a GPL-3.0 LizardIndicators source (amaAdaptiveLaguerreFilter.cs),
//  but NO GPL code was used — this is reimplemented FRESH from the canonical public formula (Ehlers, "Time
//  Warp Without Space Travel"), a mathematical method which is not copyrightable. No third-party code,
//  variable names, or structure copied.
//
//  ADAPTATION (canonical Ehlers): each bar,  diff = |price − filt[1]| ;  over the last Length diffs find
//  HH (max) and LL (min) ;  ratio = (diff − LL)/(HH − LL)  (carry prior alpha when HH == LL) ;  the adaptive
//  alpha α = MEDIAN of the last 5 ratios ;  gamma = 1 − α ;  then the standard 4-element Laguerre recursion
//  runs with that α:  L0 = α·price + γ·L0[1] ,  L1 = −γ·L0 + L0[1] + γ·L1[1] , … ,  Value = (L0+2L1+2L2+L3)/6.
//
//  ASSUMPTIONS: the median window is fixed at 5 (Ehlers' canonical value; the GPL source used the same). The
//  HH/LL search window is the last `Period` diffs. Early bars are seeded (diff=0, ratio=alpha=0.5, filter=input).
//
//  CHANGELOG
//    v1.0.0 (2026-08-17) — ported from main (identical to main's 2026-07-12 clean-room build, math
//             unchanged), mapped to Sneaky naming/namespace and re-plumbed onto live's SCROLL card
//             convention (ISneakyScrollCard / EnsureScrollHook / PlaceScroll / ConductorRender) instead of
//             main's older SneakySkin.CardLayout.Place — matching every other already-ported Sneaky
//             smoother, not main's card mechanism. This was one of 7 smoother indicators main had that
//             live did not; none are referenced by any other file in either codebase (standalone,
//             purely-additive port — no other Sneaky file touched).
// ═════════════════════════════════════════════════════════════════════════════
namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Smoothers
{
    public class SneakyAdaptiveLaguerreFilter_v1_0_0 : Indicator, ISneakyScrollCard
    {
        private SneakySkin.Painter _sp;
        // cached for the card (data thread) — OnRender must never touch Value[]
        private double _cardVal;
        private int    _cardSlope;
        private bool   _cardHasData;

        private const int MedianWindow = 5;

        private Series<double> diff, ratio, alpha, l0, l1, l2, l3;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Sneaky smoother library — Ehlers Adaptive Laguerre filter (clean-room). A Laguerre IIR filter whose damping factor self-adjusts from the normalized recent tracking error (fast in trends, smooth in chop) + a Sneaky glass card. A building block, not a Council voter (no State seam).";
                Name                     = "Sneaky AdaptiveLaguerreFilter v1.0.0";
                Calculate                = Calculate.OnPriceChange;
                IsOverlay                = true;
                IsSuspendedWhileInactive = true;
                DrawOnPricePanel         = true;

                Period                   = 20;

                ShowCard           = true;
                CardCorner         = SneakyCardCorner.TopRight;
                ShowIndicatorLabel = false;

                AddPlot(new Stroke(Brushes.MediumBlue, 2), PlotStyle.Line, "AdaptiveLaguerreFilter");
            }
            else if (State == State.Configure)
            {
                diff  = new Series<double>(this);
                ratio = new Series<double>(this);
                alpha = new Series<double>(this);
                l0    = new Series<double>(this);
                l1    = new Series<double>(this);
                l2    = new Series<double>(this);
                l3    = new Series<double>(this);
            }
            else if (State == State.DataLoaded)
            {
                if (!ShowIndicatorLabel) Name = string.Empty;   // Sneaky label remover
            }
            else if (State == State.Terminated)
            {
                if (_sp != null) { try { _sp.Dispose(); } catch (Exception _sx) { SneakyCore.Swallow("SneakyAdaptiveLaguerreFilter.OnStateChange", _sx); } _sp = null; }
                try { SneakySkin.CardLayout.Release(this); } catch (Exception _sx) { SneakyCore.Swallow("SneakyAdaptiveLaguerreFilter.OnStateChange", _sx); }
            }
        }

        protected override void OnBarUpdate()
        {
            double x = Input[0];

            // Seed: first bar (or degenerate Period=1) → pass-through, neutral adaptation state.
            if (CurrentBar == 0 || Period == 1)
            {
                diff[0]  = 0.0;
                ratio[0] = 0.5;
                alpha[0] = 0.5;
                l0[0] = l1[0] = l2[0] = l3[0] = x;
                Value[0] = x;
                // BUGFIX (2026-08-22, P3 finding: AdaptiveLaguerre warm-up card gate): Period==1 makes
                // this branch permanently true (not just a one-time first-bar seed), so the old bare
                // `return` here meant _cardHasData was NEVER set and the glass card was stuck on
                // "loading…" for the whole session whenever Period=1. This branch already computes a
                // valid pass-through value every bar, so populate the card from it too.
                _cardVal     = Value[0];
                _cardSlope   = (CurrentBar >= 1) ? (Value[0] > Value[1] ? 1 : (Value[0] < Value[1] ? -1 : 0)) : 0;
                _cardHasData = true;
                return;
            }

            // Tracking error vs. the previous filtered value.
            diff[0] = Math.Abs(x - Value[1]);

            // HH / LL of the error over the last `Period` bars.
            int win = Math.Min(CurrentBar + 1, Period);
            double hh = diff[0], ll = diff[0];
            for (int i = 0; i < win; i++)
            {
                double d = diff[i];
                if (d > hh) hh = d;
                if (d < ll) ll = d;
            }

            // Normalized error → adaptive alpha = median of the last 5 ratios.
            if (hh > ll)
            {
                ratio[0] = (diff[0] - ll) / (hh - ll);
                alpha[0] = MedianRatio(Math.Min(CurrentBar + 1, MedianWindow));
            }
            else
            {
                ratio[0] = ratio[1];
                alpha[0] = alpha[1];   // carry prior alpha when the error window is flat
            }

            double a = alpha[0];
            double g = 1.0 - a;

            l0[0] = a * x     + g * l0[1];
            l1[0] = -g * l0[0] + l0[1] + g * l1[1];
            l2[0] = -g * l1[0] + l1[1] + g * l2[1];
            l3[0] = -g * l2[0] + l2[1] + g * l3[1];

            Value[0] = (l0[0] + 2.0 * l1[0] + 2.0 * l2[0] + l3[0]) / 6.0;
            // cache the card readout here (data thread), so OnRender never touches Value[]
            _cardVal    = Value[0];
            _cardSlope  = (CurrentBar >= 1) ? (Value[0] > Value[1] ? 1 : (Value[0] < Value[1] ? -1 : 0)) : 0;
            _cardHasData = true;
        }

        // Median of the last `n` ratio values (clean-room; small window, sort a local copy).
        private double MedianRatio(int n)
        {
            if (n <= 1) return ratio[0];
            var buf = new double[n];
            for (int i = 0; i < n; i++) buf[i] = ratio[i];
            Array.Sort(buf);
            int mid = n / 2;
            return (n % 2 == 1) ? buf[mid] : 0.5 * (buf[mid - 1] + buf[mid]);
        }

        // ── Sneaky glass card ──
        protected override void OnRender(NinjaTrader.Gui.Chart.ChartControl chartControl, NinjaTrader.Gui.Chart.ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (!ShowCard || RenderTarget == null || ChartPanel == null) return;
            if (_sp == null) _sp = new SneakySkin.Painter();
            _sp.Begin(RenderTarget);
            try { RenderCard(); } catch (Exception _sx) { SneakyCore.Swallow("SneakyAdaptiveLaguerreFilter.OnRender", _sx); }
            try { _sp.End(); } catch (Exception _sx) { SneakyCore.Swallow("SneakyAdaptiveLaguerreFilter.OnRender", _sx); }   // ALWAYS runs — a skipped End() would silently kill the card
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
            _sp.Text("AdaLaguerre", r.Left + 16f, r.Top, r.Width - 70f, 16f, SneakySkin.CInk, 11f, true);
            _sp.Pill("p" + Period + (_cardHasData ? " α" + alpha[0].ToString("0.##") : ""), r.Right, r.Top - 1f, SneakySkin.CMute);

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
        public Series<double> AdaptiveLaguerreFilter => Values[0];

        #region Properties
        [Range(1, int.MaxValue)]
        [NinjaScriptProperty]
        [Display(Name = "Period", Description = "Length of the error window (HH/LL search) that drives the self-adjusting alpha. Default 20.", Order = 1, GroupName = "Parameters")]
        public int Period { get; set; }

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
		private Sneaky.Smoothers.SneakyAdaptiveLaguerreFilter_v1_0_0[] cacheSneakyAdaptiveLaguerreFilter_v1_0_0;
		public Sneaky.Smoothers.SneakyAdaptiveLaguerreFilter_v1_0_0 SneakyAdaptiveLaguerreFilter_v1_0_0(int period, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyAdaptiveLaguerreFilter_v1_0_0(Input, period, showCard, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Smoothers.SneakyAdaptiveLaguerreFilter_v1_0_0 SneakyAdaptiveLaguerreFilter_v1_0_0(ISeries<double> input, int period, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyAdaptiveLaguerreFilter_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyAdaptiveLaguerreFilter_v1_0_0.Length; idx++)
					if (cacheSneakyAdaptiveLaguerreFilter_v1_0_0[idx] != null && cacheSneakyAdaptiveLaguerreFilter_v1_0_0[idx].Period == period && cacheSneakyAdaptiveLaguerreFilter_v1_0_0[idx].ShowCard == showCard && cacheSneakyAdaptiveLaguerreFilter_v1_0_0[idx].CardCorner == cardCorner && cacheSneakyAdaptiveLaguerreFilter_v1_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyAdaptiveLaguerreFilter_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyAdaptiveLaguerreFilter_v1_0_0[idx];
			return CacheIndicator<Sneaky.Smoothers.SneakyAdaptiveLaguerreFilter_v1_0_0>(new Sneaky.Smoothers.SneakyAdaptiveLaguerreFilter_v1_0_0(){ Period = period, ShowCard = showCard, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyAdaptiveLaguerreFilter_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Smoothers.SneakyAdaptiveLaguerreFilter_v1_0_0 SneakyAdaptiveLaguerreFilter_v1_0_0(int period, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyAdaptiveLaguerreFilter_v1_0_0(Input, period, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Smoothers.SneakyAdaptiveLaguerreFilter_v1_0_0 SneakyAdaptiveLaguerreFilter_v1_0_0(ISeries<double> input , int period, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyAdaptiveLaguerreFilter_v1_0_0(input, period, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Smoothers.SneakyAdaptiveLaguerreFilter_v1_0_0 SneakyAdaptiveLaguerreFilter_v1_0_0(int period, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyAdaptiveLaguerreFilter_v1_0_0(Input, period, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Smoothers.SneakyAdaptiveLaguerreFilter_v1_0_0 SneakyAdaptiveLaguerreFilter_v1_0_0(ISeries<double> input , int period, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyAdaptiveLaguerreFilter_v1_0_0(input, period, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
