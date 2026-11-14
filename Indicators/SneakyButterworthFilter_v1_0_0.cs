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
//  Sneaky Butterworth Filter — Ehlers Butterworth low-pass (smoother block)  |   Version v1.0.0
//  File: SneakyButterworthFilter_v1_0_0.cs  |  namespace …Indicators.Sneaky.Smoothers  |  display "Sneaky Butterworth Filter"
//
//  ⚠ NO ORDERS · NO STATE SEAM — a read-only SMOOTHER building block, not a Council voter. It draws a
//  smoothed line + a Sneaky glass card and publishes nothing (a filter has no verdict). Signal tools may
//  consume its plot; it is also a Sneaky-branded Butterworth in its own right.
//
//  PROVENANCE / LICENSE: CLEAN-ROOM. Reimplemented from the PUBLIC John Ehlers Butterworth filter formula
//  (2-pole & 3-pole IIR low-pass, published in "Cybernetic Analysis for Stocks and Futures") — a mathematical
//  DSP method, not copyrightable. No third-party code, variable names, or structure were copied; the "Au"
//  source was read ONLY to confirm the pole count (2/3). See repo NOTICE.
//
//  MATH (Ehlers, radians; degree form 180/P° == π/P rad):
//    2-pole: a=exp(-√2·π/P); b=2a·cos(√2·π/P); c1=(1-b+a²)/4; c2=b; c3=-a²
//            y = c1·(x + 2·x[1] + x[2]) + c2·y[1] + c3·y[2]
//    3-pole: a=exp(-π/P); b=2a·cos(1.738·π/P); c=a²; d1=(1-b+c)(1-c)/8; d2=b+c; d3=-(c+b·c); d4=c²
//            y = d1·(x + 3·x[1] + 3·x[2] + x[3]) + d2·y[1] + d3·y[2] + d4·y[3]
//
//  ASSUMPTIONS:
//    • Poles is restricted to {2,3} (the two Ehlers Butterworth variants); other values clamp into range.
//    • 3-pole cosine argument uses the canonical Ehlers coefficient 1.738 (the "Au" source used √3≈1.732 —
//      a near-identical de-tuning; 1.738 is the published value).
//    • Recursion is computed live each tick from the current bar's Input and PRIOR-bar filter outputs
//      (Value[1..]); early bars (CurrentBar < Poles) seed to Input[0].
//
//  CHANGELOG
//    v1.0.0 (2026-07-12) — clean-room Butterworth filter + Sneaky plumbing (naming law, glass card, label remover).
// ═════════════════════════════════════════════════════════════════════════════
namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Smoothers
{
    public class SneakyButterworthFilter_v1_0_0 : Indicator, ISneakyScrollCard
    {
        private SneakySkin.Painter _sp;
        // cached for the card (data thread) — OnRender must never touch Value[]
        private double _cardVal;
        private int    _cardSlope;
        private bool   _cardHasData;
        private int    _poles = 3;
        private double _c1, _c2, _c3;           // 2-pole coefficients
        private double _d1, _d2, _d3, _d4;      // 3-pole coefficients

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Sneaky smoother library — Ehlers Butterworth low-pass filter (clean-room, 2-pole & 3-pole IIR). Draws the smoothed line + a Sneaky glass card. A building block, not a Council voter (no State seam).";
                Name                     = "Sneaky Butterworth Filter v1.0.0";
                Calculate                = Calculate.OnPriceChange;
                IsOverlay                = true;
                IsSuspendedWhileInactive = true;
                DrawOnPricePanel         = true;

                Period                   = 20;
                Poles                    = 3;

                ShowCard           = true;
                CardCorner         = SneakyCardCorner.TopRight;
                ShowIndicatorLabel = false;

                AddPlot(new Stroke(Brushes.DeepSkyBlue, 2), PlotStyle.Line, "Butterworth");
            }
            else if (State == State.DataLoaded)
            {
                if (!ShowIndicatorLabel) Name = string.Empty;   // Sneaky label remover
                ComputeCoefficients();
            }
            else if (State == State.Terminated)
            {
                if (_sp != null) { try { _sp.Dispose(); } catch { } _sp = null; }
                try { SneakySkin.CardLayout.Release(this); } catch { }
            }
        }

        private void ComputeCoefficients()
        {
            double pi = Math.PI;
            double sq2 = Math.Sqrt(2.0);   // 1.41421356…

            if (Poles == 2)
            {
                double a = Math.Exp(-sq2 * pi / Period);
                double b = 2.0 * a * Math.Cos(sq2 * pi / Period);
                _c1 = (1.0 - b + a * a) / 4.0;
                _c2 = b;
                _c3 = -a * a;
            }
            else // 3-pole
            {
                double a = Math.Exp(-pi / Period);
                double b = 2.0 * a * Math.Cos(1.738 * pi / Period);
                double c = a * a;
                _d1 = (1.0 - b + c) * (1.0 - c) / 8.0;
                _d2 = b + c;
                _d3 = -(c + b * c);
                _d4 = c * c;
            }
        }

        protected override void OnBarUpdate()
        {
            // Seed early bars until the recursion has the lags it needs.
            if (CurrentBar < Poles)
            {
                Value[0] = Input[0];
                return;
            }

            if (Poles == 2)
            {
                Value[0] = _c1 * (Input[0] + 2.0 * Input[1] + Input[2])
                         + _c2 * Value[1]
                         + _c3 * Value[2];
            }
            else // 3-pole
            {
                Value[0] = _d1 * (Input[0] + 3.0 * Input[1] + 3.0 * Input[2] + Input[3])
                         + _d2 * Value[1]
                         + _d3 * Value[2]
                         + _d4 * Value[3];
            }
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
            try { RenderCard(); }
            catch (Exception ex)
            {
                if (unchecked(Environment.TickCount - _lastRenderExLog) > 15000)
                {
                    _lastRenderExLog = Environment.TickCount;
                    try { Print("[SneakyButterworthFilter] RenderCard() threw: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); } catch { }
                }
            }
            try { _sp.End(); } catch { }   // ALWAYS runs — a skipped End() would silently kill the card
        }

        private int _lastRenderExLog = 0;   // FIXED 2026-08-03 — was int.MinValue, which overflows in unchecked(TickCount - X) and permanently evaluates negative, silently blocking every exception log for the whole session. 0 is a safe sentinel: TickCount for any system up more than a few seconds is comfortably past any real threshold on the first check.
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
            _sp.Text("Butterworth", r.Left + 16f, r.Top, r.Width - 70f, 16f, SneakySkin.CInk, 11f, true);
            _sp.Pill(Poles + "p·" + Period, r.Right, r.Top - 1f, SneakySkin.CMute);

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
        public Series<double> Butterworth => Values[0];

        #region Properties
        [Range(1, int.MaxValue)]
        [NinjaScriptProperty]
        [Display(Name = "Period", Description = "Filter period (cutoff).", Order = 1, GroupName = "Parameters")]
        public int Period { get; set; }

        [Range(2, 3)]
        [NinjaScriptProperty]
        [Display(Name = "Poles", Description = "Number of poles: 2 or 3 (Ehlers Butterworth variants).", Order = 2, GroupName = "Parameters")]
        public int Poles
        {
            get { return _poles; }
            set { _poles = Math.Min(3, Math.Max(2, value)); }
        }

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
		private Sneaky.Smoothers.SneakyButterworthFilter_v1_0_0[] cacheSneakyButterworthFilter_v1_0_0;
		public Sneaky.Smoothers.SneakyButterworthFilter_v1_0_0 SneakyButterworthFilter_v1_0_0(int period, int poles, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyButterworthFilter_v1_0_0(Input, period, poles, showCard, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Smoothers.SneakyButterworthFilter_v1_0_0 SneakyButterworthFilter_v1_0_0(ISeries<double> input, int period, int poles, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyButterworthFilter_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyButterworthFilter_v1_0_0.Length; idx++)
					if (cacheSneakyButterworthFilter_v1_0_0[idx] != null && cacheSneakyButterworthFilter_v1_0_0[idx].Period == period && cacheSneakyButterworthFilter_v1_0_0[idx].Poles == poles && cacheSneakyButterworthFilter_v1_0_0[idx].ShowCard == showCard && cacheSneakyButterworthFilter_v1_0_0[idx].CardCorner == cardCorner && cacheSneakyButterworthFilter_v1_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyButterworthFilter_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyButterworthFilter_v1_0_0[idx];
			return CacheIndicator<Sneaky.Smoothers.SneakyButterworthFilter_v1_0_0>(new Sneaky.Smoothers.SneakyButterworthFilter_v1_0_0(){ Period = period, Poles = poles, ShowCard = showCard, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyButterworthFilter_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Smoothers.SneakyButterworthFilter_v1_0_0 SneakyButterworthFilter_v1_0_0(int period, int poles, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyButterworthFilter_v1_0_0(Input, period, poles, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Smoothers.SneakyButterworthFilter_v1_0_0 SneakyButterworthFilter_v1_0_0(ISeries<double> input , int period, int poles, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyButterworthFilter_v1_0_0(input, period, poles, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Smoothers.SneakyButterworthFilter_v1_0_0 SneakyButterworthFilter_v1_0_0(int period, int poles, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyButterworthFilter_v1_0_0(Input, period, poles, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Smoothers.SneakyButterworthFilter_v1_0_0 SneakyButterworthFilter_v1_0_0(ISeries<double> input , int period, int poles, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyButterworthFilter_v1_0_0(input, period, poles, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
