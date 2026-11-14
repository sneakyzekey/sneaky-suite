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
//  Sneaky LinReg — Linear Regression value (Sneaky smoother building block)   |   Version v1.0.0
//  File: SneakyLinReg_v1_0_0.cs  |  namespace …Indicators.Sneaky.Smoothers  |  display "Sneaky LinReg"
//
//  ⚠ NO ORDERS · NO STATE SEAM — a read-only SMOOTHER building block, not a Council voter. It draws the
//  least-squares regression VALUE (the fitted line evaluated at the current bar), not the slope. It plots a
//  smoothed line + a Sneaky glass card; it publishes nothing. A building block the signal tools can consume.
//
//  PROVENANCE / LICENSE: CLEAN-ROOM. Written from the public ordinary-least-squares linear regression
//  formula: fit y = a + b·t over the last N inputs (t = 0 oldest … N−1 newest) and return the line value
//  at the current bar (t = N−1). slope b = (N·Σt·y − Σt·Σy) / (N·Σt² − (Σt)²); intercept a = (Σy − b·Σt)/N.
//  A mathematical method, not copyrightable. No third-party code, variable names, or structure were copied.
//  (Sneaky port of the "Au" MA/filter pack; NOT copied. See repo NOTICE.)
//
//  CHANGELOG
//    v1.0.0 (2026-07-12) — clean-room least-squares Linear Regression value + Sneaky plumbing (naming law, glass card, label remover).
// ═════════════════════════════════════════════════════════════════════════════
namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Smoothers
{
    public class SneakyLinReg_v1_0_0 : Indicator, ISneakyScrollCard
    {
        private SneakySkin.Painter _sp;
        // cached for the card (data thread) — OnRender must never touch Value[]
        private double _cardVal;
        private int    _cardSlope;
        private bool   _cardHasData;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Sneaky smoother library — Linear Regression value (clean-room). Least-squares line fitted over the last N inputs, evaluated at the current bar (the regression VALUE, not the slope). Draws a smoothed line + a Sneaky glass card. A building block, not a Council voter (no State seam).";
                Name                     = "Sneaky LinReg v1.0.0";
                Calculate                = Calculate.OnPriceChange;
                IsOverlay                = true;
                IsSuspendedWhileInactive = true;
                DrawOnPricePanel         = true;

                Period                   = 14;

                ShowCard           = true;
                CardCorner         = SneakyCardCorner.TopRight;
                ShowIndicatorLabel = false;

                AddPlot(new Stroke(Brushes.SkyBlue, 2), PlotStyle.Line, "LinReg");
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

        protected override void OnBarUpdate()
        {
            // Clean-room least-squares linear regression, evaluated at the current bar.
            // Time index t runs oldest→newest: t = 0 … n-1, with y(t) = Input[n-1-t]. Current bar = t = n-1.
            int n = Math.Min(CurrentBar + 1, Period);
            if (n < 2)
            {
                Value[0] = Input[0];
                // BUGFIX (2026-08-22, P3 finding: LinReg warm-up card gate): n = Min(CurrentBar+1, Period)
                // is permanently 1 whenever Period=1 (n can never reach 2), so the old bare `return` here
                // meant _cardHasData was NEVER set and the glass card was stuck on "loading…" for the
                // whole session. n<2 is a valid degenerate case (a single-point regression is just that
                // point, flat slope) — populate the card from it instead of skipping it.
                _cardVal     = Value[0];
                _cardSlope   = 0;
                _cardHasData = true;
                return;
            }

            double sumT = 0.0, sumY = 0.0, sumTY = 0.0, sumT2 = 0.0;
            for (int t = 0; t < n; t++)
            {
                double y = Input[n - 1 - t];   // oldest at t=0, current at t=n-1
                sumT  += t;
                sumY  += y;
                sumTY += t * y;
                sumT2 += (double)t * t;
            }

            double denom = n * sumT2 - sumT * sumT;
            if (denom == 0.0)
            {
                Value[0] = sumY / n;   // degenerate — flat line
                return;
            }

            double slope     = (n * sumTY - sumT * sumY) / denom;
            double intercept = (sumY - slope * sumT) / n;
            Value[0] = intercept + slope * (n - 1);   // regression line value at the current bar
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
                    try { Print("[SneakyLinReg] RenderCard() threw: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); } catch { }
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
            _sp.Text("LinReg", r.Left + 16f, r.Top, r.Width - 70f, 16f, SneakySkin.CInk, 11f, true);
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
        public Series<double> LinReg => Values[0];

        #region Properties
        [Range(1, int.MaxValue)]
        [NinjaScriptProperty]
        [Display(Name = "Period", Description = "Regression window (N) — number of inputs fitted.", Order = 1, GroupName = "Parameters")]
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
		private Sneaky.Smoothers.SneakyLinReg_v1_0_0[] cacheSneakyLinReg_v1_0_0;
		public Sneaky.Smoothers.SneakyLinReg_v1_0_0 SneakyLinReg_v1_0_0(int period, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyLinReg_v1_0_0(Input, period, showCard, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Smoothers.SneakyLinReg_v1_0_0 SneakyLinReg_v1_0_0(ISeries<double> input, int period, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyLinReg_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyLinReg_v1_0_0.Length; idx++)
					if (cacheSneakyLinReg_v1_0_0[idx] != null && cacheSneakyLinReg_v1_0_0[idx].Period == period && cacheSneakyLinReg_v1_0_0[idx].ShowCard == showCard && cacheSneakyLinReg_v1_0_0[idx].CardCorner == cardCorner && cacheSneakyLinReg_v1_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyLinReg_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyLinReg_v1_0_0[idx];
			return CacheIndicator<Sneaky.Smoothers.SneakyLinReg_v1_0_0>(new Sneaky.Smoothers.SneakyLinReg_v1_0_0(){ Period = period, ShowCard = showCard, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyLinReg_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Smoothers.SneakyLinReg_v1_0_0 SneakyLinReg_v1_0_0(int period, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLinReg_v1_0_0(Input, period, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Smoothers.SneakyLinReg_v1_0_0 SneakyLinReg_v1_0_0(ISeries<double> input , int period, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLinReg_v1_0_0(input, period, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Smoothers.SneakyLinReg_v1_0_0 SneakyLinReg_v1_0_0(int period, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLinReg_v1_0_0(Input, period, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Smoothers.SneakyLinReg_v1_0_0 SneakyLinReg_v1_0_0(ISeries<double> input , int period, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLinReg_v1_0_0(input, period, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
