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
//  Sneaky TSMA — Triple Simple Moving Average (Sneaky smoother block)     |   Version v1.0.0
//  File: SneakyTSMA_v1_0_0.cs  |  namespace …Indicators.Sneaky.Smoothers  |  display "Sneaky TSMA"
//
//  ⚠ NO ORDERS · NO STATE SEAM — a read-only SMOOTHER building block, not a Council voter. It draws a
//  smoothed line + a Sneaky glass card; it publishes nothing (a moving average has no verdict).
//
//  PROVENANCE / LICENSE: CLEAN-ROOM. Written from the public triple-cascade lag-reduction formula (the
//  TEMA construction applied to a Simple MA) — a mathematical method, not copyrightable. No third-party
//  code, names, or structure copied. (Sneaky port of the "Au" MA pack; the Au code was NOT copied.)
//
//  ALGORITHM (Triple SMA — TEMA form over SMA, confirmed from source):
//    s1 = SMA(price, Period)
//    s2 = SMA(s1,    Period)
//    s3 = SMA(s2,    Period)
//    Value = 3·s1 − 3·s2 + s3
//  Each SMA = arithmetic mean over the available window k = min(CurrentBar+1, Period).
//
//  CHANGELOG
//    v1.0.0 (2026-07-12) — clean-room Triple Simple MA (3·SMA − 3·SMA² + SMA³) + Sneaky plumbing
//             (naming law, glass card, label remover).
// ═════════════════════════════════════════════════════════════════════════════
namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Smoothers
{
    public class SneakyTSMA_v1_0_0 : Indicator, ISneakyScrollCard
    {
        private SneakySkin.Painter _sp;
        // cached for the card (data thread) — OnRender must never touch Value[]
        private double _cardVal;
        private int    _cardSlope;
        private bool   _cardHasData;
        private Series<double> _s1;   // SMA(price)
        private Series<double> _s2;   // SMA(s1)

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Sneaky smoother library — Triple Simple Moving Average (clean-room, 3·SMA − 3·SMA² + SMA³). Draws a lag-reduced smoothed line + a Sneaky glass card. A building block, not a Council voter (no State seam).";
                Name                     = "Sneaky TSMA v1.0.0";
                Calculate                = Calculate.OnPriceChange;
                IsOverlay                = true;
                IsSuspendedWhileInactive = true;
                DrawOnPricePanel         = true;

                Period                   = 14;

                ShowCard           = true;
                CardCorner         = SneakyCardCorner.TopRight;
                ShowIndicatorLabel = false;

                AddPlot(new Stroke(Brushes.Orange, 2), PlotStyle.Line, "TSMA");
            }
            else if (State == State.Configure)
            {
                _s1 = new Series<double>(this);
                _s2 = new Series<double>(this);
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

        // Clean-room SMA of the current bar of a series, over the available window.
        private double Sma(ISeries<double> src, int period)
        {
            int n = Math.Min(CurrentBar + 1, period);
            double sum = 0.0;
            for (int i = 0; i < n; i++) sum += src[i];
            return sum / n;
        }

        protected override void OnBarUpdate()
        {
            // Clean-room Triple SMA: cascade three simple means, combine TEMA-style.
            _s1[0] = Sma(Input, Period);
            _s2[0] = Sma(_s1,   Period);
            double s3 = Sma(_s2, Period);
            Value[0] = 3.0 * _s1[0] - 3.0 * _s2[0] + s3;
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
                    try { Print("[SneakyTSMA] RenderCard() threw: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); } catch { }
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
            _sp.Text("TSMA", r.Left + 16f, r.Top, r.Width - 70f, 16f, SneakySkin.CInk, 11f, true);
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
        public Series<double> TSMA => Values[0];

        #region Properties
        [Range(1, int.MaxValue)]
        [NinjaScriptProperty]
        [Display(Name = "Period", Description = "Length of each cascaded SMA stage.", Order = 1, GroupName = "Parameters")]
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
		private Sneaky.Smoothers.SneakyTSMA_v1_0_0[] cacheSneakyTSMA_v1_0_0;
		public Sneaky.Smoothers.SneakyTSMA_v1_0_0 SneakyTSMA_v1_0_0(int period, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyTSMA_v1_0_0(Input, period, showCard, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Smoothers.SneakyTSMA_v1_0_0 SneakyTSMA_v1_0_0(ISeries<double> input, int period, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyTSMA_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyTSMA_v1_0_0.Length; idx++)
					if (cacheSneakyTSMA_v1_0_0[idx] != null && cacheSneakyTSMA_v1_0_0[idx].Period == period && cacheSneakyTSMA_v1_0_0[idx].ShowCard == showCard && cacheSneakyTSMA_v1_0_0[idx].CardCorner == cardCorner && cacheSneakyTSMA_v1_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyTSMA_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyTSMA_v1_0_0[idx];
			return CacheIndicator<Sneaky.Smoothers.SneakyTSMA_v1_0_0>(new Sneaky.Smoothers.SneakyTSMA_v1_0_0(){ Period = period, ShowCard = showCard, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyTSMA_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Smoothers.SneakyTSMA_v1_0_0 SneakyTSMA_v1_0_0(int period, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyTSMA_v1_0_0(Input, period, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Smoothers.SneakyTSMA_v1_0_0 SneakyTSMA_v1_0_0(ISeries<double> input , int period, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyTSMA_v1_0_0(input, period, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Smoothers.SneakyTSMA_v1_0_0 SneakyTSMA_v1_0_0(int period, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyTSMA_v1_0_0(Input, period, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Smoothers.SneakyTSMA_v1_0_0 SneakyTSMA_v1_0_0(ISeries<double> input , int period, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyTSMA_v1_0_0(input, period, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
