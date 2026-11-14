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
//  Sneaky LLMA — Low-Lag (Jurik-style) Moving Average (Sneaky smoother block) |  Version v1.0.0
//  File: SneakyLLMA_v1_0_0.cs  |  namespace …Indicators.Sneaky.Smoothers  |  display "Sneaky LLMA"
//
//  ⚠ NO ORDERS · NO STATE SEAM — a read-only SMOOTHER building block, not a Council voter. It draws a
//  smoothed low-lag line + a Sneaky glass card; it publishes nothing (a moving average has no verdict).
//
//  PROVENANCE / LICENSE: CLEAN-ROOM. The "AuLLMA" source ("Low Lag Moving Average", Length + Phase params)
//  is a JURIK-JMA-style adaptive filter: a 3-stage EMA cascade (preliminary EMA → detrend EMA → phase-
//  adjusted Kalman-style stage) whose smoothing factor is driven by a proprietary volatility-band estimator
//  (a sorted trimmed-mean "MidAvg" window). That volatility-adaptive stage is non-standard / proprietary.
//  This is a FRESH reimplementation from the CANONICAL public simplified JMA formula — no third-party code,
//  names, or structure were copied.
//
//  ASSUMPTION (noted per clean-room rule): I implemented the closest CANONICAL PUBLISHED form — the
//  simplified Jurik JMA with a FIXED smoothing factor (dynamic-volatility power = 1). I deliberately OMIT
//  the source's proprietary volatility-adaptive dynamic factor (its "MidAvg" trimmed-mean band + log/sqrt
//  scaling). Consequently output tracks the standard published JMA, not the exact Au/MidAvg curve. Length +
//  Phase are preserved with the standard JMA semantics: beta = 0.45·(Length−1) / (0.45·(Length−1) + 2);
//  phaseRatio = Phase<−100 ? 0.5 : Phase>100 ? 2.5 : Phase/100 + 1.5.
//
//  CHANGELOG
//    v1.0.0 (2026-08-17) — ported from main (identical to main's 2026-07-12 clean-room build, math
//             unchanged), mapped to Sneaky naming/namespace and re-plumbed onto live's SCROLL card
//             convention (ISneakyScrollCard / EnsureScrollHook / PlaceScroll / ConductorRender) instead of
//             main's older SneakySkin.CardLayout.Place. Standalone, purely-additive port — not referenced
//             by any other Sneaky file. Volatility-adaptive MidAvg stage omitted (see ASSUMPTION above,
//             carried over unchanged from main).
// ═════════════════════════════════════════════════════════════════════════════
namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Smoothers
{
    public class SneakyLLMA_v1_0_0 : Indicator, ISneakyScrollCard
    {
        private SneakySkin.Painter _sp;
        // cached for the card (data thread) — OnRender must never touch Value[]
        private double _cardVal;
        private int    _cardSlope;
        private bool   _cardHasData;
        private Series<double> _e0;   // preliminary EMA
        private Series<double> _e1;   // detrend EMA
        private Series<double> _e2;   // phase-adjusted stage

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Sneaky smoother library — Low-Lag (Jurik-style) Moving Average (clean-room, simplified JMA). Draws a low-lag smoothed line + a Sneaky glass card. A building block, not a Council voter (no State seam).";
                Name                     = "Sneaky LLMA v1.0.0";
                Calculate                = Calculate.OnPriceChange;
                IsOverlay                = true;
                IsSuspendedWhileInactive = true;
                DrawOnPricePanel         = true;

                Length                   = 14;
                Phase                    = 0;

                ShowCard           = true;
                CardCorner         = SneakyCardCorner.TopRight;
                ShowIndicatorLabel = false;

                AddPlot(new Stroke(Brushes.SkyBlue, 2), PlotStyle.Line, "LLMA");
            }
            else if (State == State.Configure)
            {
                _e0 = new Series<double>(this);
                _e1 = new Series<double>(this);
                _e2 = new Series<double>(this);
            }
            else if (State == State.DataLoaded)
            {
                if (!ShowIndicatorLabel) Name = string.Empty;   // Sneaky label remover
            }
            else if (State == State.Terminated)
            {
                if (_sp != null) { try { _sp.Dispose(); } catch (Exception _sx) { SneakyCore.Swallow("SneakyLLMA.OnStateChange", _sx); } _sp = null; }
                try { SneakySkin.CardLayout.Release(this); } catch (Exception _sx) { SneakyCore.Swallow("SneakyLLMA.OnStateChange", _sx); }
            }
        }

        protected override void OnBarUpdate()
        {
            // Clean-room simplified Jurik JMA (canonical public form; volatility-adaptive stage omitted).
            double price = Input[0];

            if (CurrentBar < 1)
            {
                _e0[0]   = price;
                _e1[0]   = 0.0;
                _e2[0]   = 0.0;
                Value[0] = price;
                return;
            }

            double len1  = 0.45 * (Length - 1);
            double beta  = len1 / (len1 + 2.0);   // detrend / EMA smoothing factor
            double alpha = beta;                  // simplified JMA: fixed factor (no volatility adaptation)
            double phaseRatio = Phase < -100 ? 0.5 : (Phase > 100 ? 2.5 : Phase / 100.0 + 1.5);

            _e0[0] = (1.0 - alpha) * price + alpha * _e0[1];
            _e1[0] = (price - _e0[0]) * (1.0 - beta) + beta * _e1[1];
            _e2[0] = (_e0[0] + phaseRatio * _e1[0] - Value[1]) * (1.0 - alpha) * (1.0 - alpha)
                     + (alpha * alpha) * _e2[1];
            Value[0] = Value[1] + _e2[0];
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
            try { RenderCard(); } catch (Exception _sx) { SneakyCore.Swallow("SneakyLLMA.OnRender", _sx); }
            try { _sp.End(); } catch (Exception _sx) { SneakyCore.Swallow("SneakyLLMA.OnRender", _sx); }   // ALWAYS runs — a skipped End() would silently kill the card
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
            _sp.Text("LLMA", r.Left + 16f, r.Top, r.Width - 70f, 16f, SneakySkin.CInk, 11f, true);
            _sp.Pill("L" + Length, r.Right, r.Top - 1f, SneakySkin.CMute);

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
        public Series<double> LLMA => Values[0];

        #region Properties
        [Range(1, int.MaxValue)]
        [NinjaScriptProperty]
        [Display(Name = "Length", Description = "Lookback length of the low-lag filter.", Order = 1, GroupName = "Parameters")]
        public int Length { get; set; }

        [Range(-100.0, 100.0)]
        [NinjaScriptProperty]
        [Display(Name = "Phase", Description = "Phase (lead/lag bias), −100 to 100. 0 = neutral.", Order = 2, GroupName = "Parameters")]
        public double Phase { get; set; }

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
		private Sneaky.Smoothers.SneakyLLMA_v1_0_0[] cacheSneakyLLMA_v1_0_0;
		public Sneaky.Smoothers.SneakyLLMA_v1_0_0 SneakyLLMA_v1_0_0(int length, double phase, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyLLMA_v1_0_0(Input, length, phase, showCard, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Smoothers.SneakyLLMA_v1_0_0 SneakyLLMA_v1_0_0(ISeries<double> input, int length, double phase, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyLLMA_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyLLMA_v1_0_0.Length; idx++)
					if (cacheSneakyLLMA_v1_0_0[idx] != null && cacheSneakyLLMA_v1_0_0[idx].Length == length && cacheSneakyLLMA_v1_0_0[idx].Phase == phase && cacheSneakyLLMA_v1_0_0[idx].ShowCard == showCard && cacheSneakyLLMA_v1_0_0[idx].CardCorner == cardCorner && cacheSneakyLLMA_v1_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyLLMA_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyLLMA_v1_0_0[idx];
			return CacheIndicator<Sneaky.Smoothers.SneakyLLMA_v1_0_0>(new Sneaky.Smoothers.SneakyLLMA_v1_0_0(){ Length = length, Phase = phase, ShowCard = showCard, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyLLMA_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Smoothers.SneakyLLMA_v1_0_0 SneakyLLMA_v1_0_0(int length, double phase, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLLMA_v1_0_0(Input, length, phase, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Smoothers.SneakyLLMA_v1_0_0 SneakyLLMA_v1_0_0(ISeries<double> input , int length, double phase, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLLMA_v1_0_0(input, length, phase, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Smoothers.SneakyLLMA_v1_0_0 SneakyLLMA_v1_0_0(int length, double phase, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLLMA_v1_0_0(Input, length, phase, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Smoothers.SneakyLLMA_v1_0_0 SneakyLLMA_v1_0_0(ISeries<double> input , int length, double phase, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyLLMA_v1_0_0(input, length, phase, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
