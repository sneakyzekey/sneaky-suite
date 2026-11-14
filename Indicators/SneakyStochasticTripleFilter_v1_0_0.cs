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
using NinjaTrader.NinjaScript.AddOns.Sneaky;              // SneakySkin (glass card) + SneakyCore (StfState seam) + SneakyCardCorner
using NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors;  // own namespace — lets NT's generated wrapper (in …Indicators) resolve bare Sneaky types/enums
#endregion

// ═════════════════════════════════════════════════════════════════════════════
//  SneakyStochasticTripleFilter — the Sneaky STOCHASTIC-TRIPLE-FILTER sensor   |   Version v1.0.0
//  File: SneakyStochasticTripleFilter_v1_0_0.cs   |   namespace …Indicators.Sneaky.Sensors   |   display "Sneaky Stochastic Triple Filter v1.0.0"
//  ⚠ NAME FIDELITY (naming law, 2026-07-10): the Sneaky port keeps the FULL original name
//     ("Stochastic Triple Filter") so the derivation from the standard StochasticTripleFilter is never lost.
//
//  ⚠ NO ORDERS — read-only advisory indicator. Safe to run anywhere.
//
//  LICENSE / PROVENANCE (MPL-2.0): ported from "Stochastic Triple Filter [ATP]" © AlgoTrade_Pro (Pine v6, MPL-2.0);
//     Gaussian Channel math © DonovanWall; Choppiness Index (Dreiss) + Stochastic (Lane) are public-domain formulas.
//     A port is a DERIVATIVE WORK — this file KEEPS MPL-2.0. Attribution recorded in the repo NOTICE.
//
//  📁 TIER-③ SENSOR — lives in Indicators.Sneaky.Sensors (picker "Sneaky ▸ Sensors"). This is the FIRST tool
//     into the Sensors subfolder — the folder-split pathfinder (Docs/SENTINEL_BOUNDARY_INVENTORY.md §1a).
//
//  WHAT THIS IS — the Sneaky-plumbed port of "Stochastic Triple Filter [ATP]". The raw indicator fires a
//  Stochastic %K/%D crossover only when a DonovanWall multi-pole Gaussian midline agrees on direction AND a
//  Choppiness Index says the market is TRENDING. Two of those three are exactly the seams the Council was missing:
//    • the GAUSSIAN-CHANNEL SLOPE is an independent TREND voice (a smoothed-price regime, not another CCI/ADX echo)
//    • the CHOPPINESS INDEX is a genuine REGIME veto — "don't trade a ranging tape" — which the Council had no
//      dedicated sensor for (only the VolEnvelope squeeze damp).
//
//  THE STATE (SneakyCore.StfState, SneakyCore ≥ v1.22.0):
//    • Bias      -1/0/+1  the Gaussian-Channel midline slope (the TREND vote: rising=+1 / falling=-1) — RAW, always published
//    • Trending  bool     Choppiness Index below threshold (regime OK; false ⇒ choppy = the Council's chop veto) — RAW, always published
//    • Chop      double   the Choppiness Index value (0..100)
//    • Zone      -1/0/+1  Stochastic zone (oversold=-1 / mid=0 / overbought=+1)
//    • Signal    -1/0/+1  the FULLY-FILTERED discrete signal this bar (long=+1 / short=-1) — also a hidden plot
//  ⚠ UseGC / UseChop gate only THIS sensor's own Signal + on-chart marks — Bias and Trending are published RAW so the
//    Council's own STF voter / VetoOnChop stay in charge of how the regime is used (turning a filter off here never
//    silently disarms the Council).
//
//  SENTINEL WIRING (Docs/SENTINEL_DESIGN_SYSTEM.md §6/§9):
//    • PUBLISH: SetStfState(...) each update (default ON). Wired into the Council as a TREND VOTER ("STF",
//      enters at weight 0 — the exploration primitive; promote via F6 "Weight — STF" or Roster.conf) plus a
//      CHOP VETO (VetoOnChop, default ON — active the moment this sensor is loaded).
//    • A hidden ±1 "Signal" plot (transparent) so Deck SIGNAL ARM / generic consumers read it (CompressionBase pattern).
//    • A SneakySkin.Painter glass card + Sneaky palette + label remover. cyan = live; green/red = direction.
//
//  Faithfulness: the Pine true-range band path (trdata/filttr/mult) is DEAD CODE there (computed, never used)
//  and is omitted with zero behavioral change. Gaussian pole coefficients use C(i,j) (identical values; also
//  fixes the harmless _f7 guard typo in the original f_pole).
//
//  CHANGELOG
//    v1.0.0 (2026-07-12) — Sneaky port: Stochastic + DonovanWall Gaussian slope + Choppiness Index, published as
//             SneakyCore.StfState (Core v1.22.0) and wired into the Council (trend voter "STF" at w=0 exploration
//             + chop veto). Glass card, hidden Signal plot, label remover, versioned Name, scope-keyed publish + heartbeat.
//             Carries UseGC/UseChop for full parity with the source (and logic parity with the plain StochasticTripleFilter
//             baseline). Landed in Indicators.Sneaky.Sensors — the Tier-③ folder pathfinder. Supersedes an earlier
//             broken cut (dropped the toggles, sat in Indicators.Sneaky) → archived. DEV until live-validated.
// ═════════════════════════════════════════════════════════════════════════════
namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors
{
    public class SneakyStochasticTripleFilter_v1_0_0 : Indicator, ISneakyScrollCard
    {
        private static readonly Brush Green = Sb(37, 208, 139);
        private static readonly Brush Red   = Sb(255, 92, 106);
        private static readonly Brush Grey  = Sb(120, 120, 120);

        private SneakySkin.Painter _sp;

        // stochastic working series
        private Series<double> _rawStoch, _kS, _dS;
        // gaussian filter histories (order 1 and order N) + midline
        private Series<double> _f1, _fN, _filt;
        private Series<double> _tr;

        private double _alpha;
        private int    _lag;

        // cached state (computed in OnBarUpdate; drawn in OnRender)
        private bool   _warming = true;
        private double _k, _d, _hist, _chop;
        private int    _gcBias, _zone, _signal;
        private bool   _trending, _flash;
        private readonly List<double> _kHist = new List<double>();
        private const int HistMax = 48;
        private int _lastHistBar = -1;
        private int _lastLoggedSignal = -999;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Sneaky Stoch-Triple-Filter sensor — a Stochastic %K/%D trigger gated by a DonovanWall Gaussian-Channel slope (trend) and a Choppiness Index (regime). Publishes SneakyCore.StfState so the Council gains an independent trend voter + a chop veto.";
                Name        = "Sneaky Stochastic Triple Filter v1.0.0";
                Calculate   = Calculate.OnPriceChange;
                IsOverlay   = false;
                IsSuspendedWhileInactive = true;
                DrawOnPricePanel = false;
                DisplayInDataBox = true;

                PeriodK  = 21;
                SmoothK  = 3;
                PeriodD  = 5;
                ObLevel  = 80;
                OsLevel  = 20;

                UseGC    = true;
                Poles    = 4;
                Per      = 144;
                ModeLag  = false;
                ModeFast = false;

                UseChop       = true;
                ChopLength    = 14;
                ChopThreshold = 50;

                PublishState = true;
                LogChanges   = true;
                ShowCard     = true;
                CardCorner   = SneakyCardCorner.TopRight;
                ShowIndicatorLabel = false;

                AddPlot(new Stroke(Green, 2), PlotStyle.Line, "K");
                AddPlot(new Stroke(Grey,  1), PlotStyle.Line, "D");
                AddPlot(new Stroke(Grey,  1), PlotStyle.Bar,  "Hist");
                AddPlot(Brushes.Transparent, "Signal");                 // hidden ±1 — generic consumers (Deck SIGNAL ARM)
            }
            else if (State == State.Configure)
            {
                AddLine(new Stroke(Red,   DashStyleHelper.Dash, 1), ObLevel, "Overbought");
                AddLine(new Stroke(Green, DashStyleHelper.Dash, 1), OsLevel, "Oversold");
                AddLine(new Stroke(Grey,  DashStyleHelper.Dot,  1), 50,      "Midline");
            }
            else if (State == State.DataLoaded)
            {
                if (!ShowIndicatorLabel) Name = string.Empty;   // Sneaky label remover

                _rawStoch = new Series<double>(this);
                _kS       = new Series<double>(this);
                _dS       = new Series<double>(this);
                _f1       = new Series<double>(this);
                _fN       = new Series<double>(this);
                _filt     = new Series<double>(this);
                _tr       = new Series<double>(this);

                double beta = (1 - Math.Cos(4 * Math.Asin(1) / Per)) / (Math.Pow(1.414, 2.0 / Poles) - 1);
                _alpha = -beta + Math.Sqrt(beta * beta + 2 * beta);
                _lag   = (int)((Per - 1) / (2.0 * Poles));
            }
            else if (State == State.Terminated)
            {
                if (_sp != null) { try { _sp.Dispose(); } catch { } _sp = null; }
                try { SneakySkin.CardLayout.Release(this); } catch { }
                if (_scope != null) { try { SneakyCore.ClearStfScope(_scope); } catch { } }
            }
        }

        private static Brush Sb(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        private static double Binom(int n, int k)
        {
            if (k < 0 || k > n) return 0;
            double r = 1;
            for (int i = 1; i <= k; i++) r = r * (n - k + i) / i;
            return Math.Round(r);
        }

        // DonovanWall N-pole Gaussian filter step; f holds this order's own output history (nz→0 before it exists).
        private double GaussFilt(double a, double s, int i, Series<double> f)
        {
            double x   = 1 - a;
            double res = Math.Pow(a, i) * s;
            for (int j = 1; j <= i; j++)
            {
                if (CurrentBar < j) break;
                double term = Binom(i, j) * Math.Pow(x, j) * f[j];
                res += ((j % 2 == 1) ? 1.0 : -1.0) * term;
            }
            f[0] = res;
            return res;
        }

        // ── HEARTBEAT (mirrors SneakyTrend): re-stamp the seam on quotes so a quiet market doesn't age it out ──
        private DateTime _lastHeartbeatUtc;
        private const double HeartbeatSec = 5.0;
        protected override void OnMarketData(NinjaTrader.Data.MarketDataEventArgs marketDataUpdate)
        {
            if (!PublishState || State != State.Realtime) return;
            DateTime now = DateTime.UtcNow;
            if ((now - _lastHeartbeatUtc).TotalSeconds < HeartbeatSec) return;
            _lastHeartbeatUtc = now;
            try { SneakyCore.TouchStfState(Scope()); } catch { }
        }

        protected override void OnBarUpdate()
        {
            K[0] = 0; D[0] = 0; Hist[0] = 0; Signal[0] = 0;

            // ── True Range ──
            _tr[0] = CurrentBar == 0
                ? High[0] - Low[0]
                : Math.Max(High[0] - Low[0], Math.Max(Math.Abs(High[0] - Close[1]), Math.Abs(Low[0] - Close[1])));

            // ── Gaussian Channel midline ──
            double srcVal = (High[0] + Low[0] + Close[0]) / 3.0;
            double src    = (ModeLag && CurrentBar >= _lag)
                            ? srcVal + srcVal - ((High[_lag] + Low[_lag] + Close[_lag]) / 3.0)
                            : srcVal;
            double filt1 = GaussFilt(_alpha, src, 1,     _f1);
            double filtN = GaussFilt(_alpha, src, Poles, _fN);
            double filt  = ModeFast ? (filtN + filt1) / 2.0 : filtN;
            _filt[0] = filt;

            bool gcUp   = CurrentBar > 0 && filt > _filt[1];
            bool gcDown = CurrentBar > 0 && filt < _filt[1];
            int  gcBias = gcUp ? 1 : gcDown ? -1 : 0;

            // ── Choppiness Index ──
            bool trending = true;
            double chop = 0;
            if (CurrentBar >= ChopLength && ChopLength > 1)
            {
                double hh  = MAX(High, ChopLength)[0];
                double ll  = MIN(Low,  ChopLength)[0];
                double rng = hh - ll;
                double atrSum = SUM(_tr, ChopLength)[0];
                chop = rng > 0 ? 100.0 * Math.Log10(atrSum / rng) / Math.Log10(ChopLength) : 100.0;
                trending = chop < ChopThreshold;
            }

            // ── Stochastic ──
            double hhK = MAX(High, PeriodK)[0];
            double llK = MIN(Low,  PeriodK)[0];
            double rngK = hhK - llK;
            _rawStoch[0] = rngK > 0 ? 100.0 * (Close[0] - llK) / rngK : 0.0;
            double k = SMA(_rawStoch, SmoothK)[0];
            _kS[0] = k;
            double d = SMA(_kS, PeriodD)[0];
            _dS[0] = d;

            bool inOb = k >= ObLevel;
            bool inOs = k <= OsLevel;
            int  zone = inOs ? -1 : inOb ? 1 : 0;

            // ── signal logic (UseGC / UseChop toggles = full parity with the source + the plain baseline) ──
            bool crossUp   = CurrentBar > 0 && _kS[0] > _dS[0] && _kS[1] <= _dS[1];
            bool crossDown = CurrentBar > 0 && _kS[0] < _dS[0] && _kS[1] >= _dS[1];
            bool longSignal  = crossUp   && inOs && (!UseGC || gcUp)   && (!UseChop || trending);
            bool shortSignal = crossDown && inOb && (!UseGC || gcDown) && (!UseChop || trending);
            int  signal = longSignal ? 1 : shortSignal ? -1 : 0;

            // ── plots ──
            K[0] = k;
            D[0] = d;
            _hist = k - d;
            Hist[0] = _hist;
            Signal[0] = signal;
            PlotBrushes[0][0] = inOs ? Green : inOb ? Red : Grey;
            PlotBrushes[2][0] = _hist >= 0 ? Green : Red;

            if (inOb)      BackBrush = Trans(Red,   8);
            else if (inOs) BackBrush = Trans(Green, 8);
            else           BackBrush = null;

            // ── cache for the card ──
            _k = k; _d = d; _chop = chop; _gcBias = gcBias; _zone = zone;
            _signal = signal; _trending = trending; _flash = signal != 0;
            _warming = false;
            if (CurrentBar != _lastHistBar)
            {
                _kHist.Add(k);
                if (_kHist.Count > HistMax) _kHist.RemoveAt(0);
                _lastHistBar = CurrentBar;
            }

            // ── publish (scope-keyed, SneakyCore ≥ v1.22.0). Bias/Trending are RAW (toggles don't gate the seam) ──
            if (PublishState && Instrument != null && Instrument.MasterInstrument != null)
            {
                try
                {
                    SneakyCore.SetStfState(Scope(), SneakyCore.BarTag(BarsPeriod), Instrument.MasterInstrument.Name,
                                             gcBias, trending, chop, zone, signal, "SneakyStf");
                }
                catch { }
            }

            if (LogChanges && signal != _lastLoggedSignal && State != State.Historical)
            {
                _lastLoggedSignal = signal;
                if (signal != 0)
                {
                    try
                    {
                        SneakyCore.Log("SneakyStf", (Instrument != null ? Instrument.MasterInstrument.Name : "?") +
                            (signal > 0 ? " LONG" : " SHORT") + " (gc " + (gcUp ? "▲" : gcDown ? "▼" : "─") +
                            ", chop " + chop.ToString("0") + (trending ? " trending" : " CHOPPY") + ")");
                    }
                    catch { }
                }
            }
        }

        // ── scope (SneakyCore v1.15.0) ──
        private string _scope;
        private string Scope()
        {
            try { _scope = SneakyCore.ScopeOf(Instrument, BarsPeriod, ChartControl); } catch { }   // 2026-08-04 — ALWAYS recomputed, never permanently cached: a chart's lane can register AFTER this indicator's first call, so caching once risked freezing a stale, unlaned scope forever (confirmed happening for real tonight). Cheap to recompute every call; only overwrites _scope on success, so a transient throw keeps the previous good value.
            return _scope;
        }

        private static Brush Trans(Brush src, int opacityPercent)
        {
            Brush b = src.Clone();
            b.Opacity = Math.Max(0, Math.Min(100, opacityPercent)) / 100.0;
            b.Freeze();
            return b;
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
					try { Print("[StochasticTripleFilter] OnRender threw: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); } catch { }
				}
			}
			try { _sp.End(); } catch { }
		}

		public void ConductorRender(SharpDX.Direct2D1.RenderTarget rt)
		{
			if (!ShowCard || rt == null) return;
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


                const float cw = 240f, ch = 176f;
                SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
                var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
                	ChartControl.ChartPanels[0].X, ChartControl.ChartPanels[0].Y, ChartControl.ChartPanels[0].W, ChartControl.ChartPanels[0].H, cw, ch);
                if (slot.Width <= 0f) return;   // not the currently-selected card in the rotation

                if (_warming)
                {
                    var rw = _sp.Card(slot.X, slot.Y, cw, ch, SneakySkin.CLine);
                    _sp.Dot(rw.Left + 5f, rw.Top + 8f, SneakySkin.CMute, false);
                    _sp.Text("STOCH TRIPLE FILTER", rw.Left + 16f, rw.Top, rw.Width - 20f, 16f, SneakySkin.CInk, 11f, true);
                    _sp.Text("warming up…", rw.Left, rw.Top + 24f, rw.Width, 14f, SneakySkin.CMute, 10.5f);
                    return;
                }

                var trail  = SharpDX.DirectWrite.TextAlignment.Trailing;
                var gcCol  = _gcBias > 0 ? SneakySkin.CUp : (_gcBias < 0 ? SneakySkin.CDown : SneakySkin.CMute);
                var sigCol = _signal > 0 ? SneakySkin.CUp : (_signal < 0 ? SneakySkin.CDown : SneakySkin.CMute);
                var edge   = _flash ? SneakySkin.CAccent : (_trending ? SneakySkin.CLine : SneakySkin.CWarn);
                var r = _sp.Card(slot.X, slot.Y, cw, ch, edge);

                // header — live dot (cyan on a signal bar), title, GC-trend pill
                _sp.Dot(r.Left + 5f, r.Top + 8f, _flash ? SneakySkin.CAccent : gcCol, true);
                _sp.Text("STOCH TRIPLE FILTER", r.Left + 16f, r.Top, r.Width - 84f, 16f, SneakySkin.CInk, 11f, true);
                _sp.Pill(_gcBias > 0 ? "GC ▲" : (_gcBias < 0 ? "GC ▼" : "GC ─"), r.Right, r.Top - 1f, gcCol);

                // hero — the fully-filtered signal
                _sp.Text("SIGNAL", r.Left, r.Top + 24f, 80f, 12f, SneakySkin.CMute, 8.5f, true);
                _sp.Text(_signal > 0 ? "LONG" : (_signal < 0 ? "SHORT" : "—"),
                         r.Left, r.Top + 34f, r.Width, 24f, sigCol, 18f, false);

                // stoch K / D + zone chip
                _sp.Text("K " + _k.ToString("0") + "  D " + _d.ToString("0"),
                         r.Left, r.Top + 30f, r.Width, 16f, SneakySkin.CInk2, 11f, false, trail);
                string zoneTxt = _zone < 0 ? "oversold" : _zone > 0 ? "overbought" : "mid";
                var zoneCol = _zone < 0 ? SneakySkin.CUp : _zone > 0 ? SneakySkin.CDown : SneakySkin.CMute;
                _sp.Text(zoneTxt, r.Left, r.Top + 48f, r.Width, 14f, zoneCol, 9.5f, false, trail);

                // stoch sparkline
                _sp.Sparkline(r.Left, r.Top + 66f, r.Width, 22f, _kHist, gcCol);

                // footer — chop regime (the veto surface)
                _sp.Divider(r.Left, r.Top + 96f, r.Right);
                _sp.Text("Chop " + _chop.ToString("0.0"), r.Left, r.Top + 100f, r.Width, 14f, SneakySkin.CInk2, 10.5f);
                var chopCol = _trending ? SneakySkin.CUp : SneakySkin.CWarn;
                _sp.Text(_trending ? "✔ TRENDING" : "✖ CHOPPY (veto)", r.Left, r.Top + 100f, r.Width, 14f, chopCol, 10.5f, true, trail);
		}

        private int _lastRenderExLog = 0;   // FIXED 2026-08-03 — was int.MinValue, which overflows in unchecked(TickCount - X) and permanently evaluates negative, silently blocking every exception log for the whole session. 0 is a safe sentinel: TickCount for any system up more than a few seconds is comfortably past any real threshold on the first check.

        #region Plot accessors
        [Browsable(false)] [XmlIgnore] public Series<double> K      { get { return Values[0]; } }
        [Browsable(false)] [XmlIgnore] public Series<double> D      { get { return Values[1]; } }
        [Browsable(false)] [XmlIgnore] public Series<double> Hist   { get { return Values[2]; } }
        [Browsable(false)] [XmlIgnore] public Series<double> Signal { get { return Values[3]; } }
        #endregion

        #region Properties
        [Range(1, int.MaxValue)] [NinjaScriptProperty]
        [Display(Name = "%K Length", Order = 1, GroupName = "Stochastic")]
        public int PeriodK { get; set; }

        [Range(1, int.MaxValue)] [NinjaScriptProperty]
        [Display(Name = "%K Smoothing", Order = 2, GroupName = "Stochastic")]
        public int SmoothK { get; set; }

        [Range(1, int.MaxValue)] [NinjaScriptProperty]
        [Display(Name = "%D Smoothing", Order = 3, GroupName = "Stochastic")]
        public int PeriodD { get; set; }

        [Range(50, 99)] [NinjaScriptProperty]
        [Display(Name = "Overbought", Order = 4, GroupName = "Stochastic")]
        public int ObLevel { get; set; }

        [Range(1, 50)] [NinjaScriptProperty]
        [Display(Name = "Oversold", Order = 5, GroupName = "Stochastic")]
        public int OsLevel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable GC Filter", Description = "Require the DonovanWall Gaussian-Channel midline slope to AGREE with direction (rising=long / falling=short) for THIS sensor's own signal. The raw slope is still published to the Council regardless.", Order = 1, GroupName = "Gaussian Channel")]
        public bool UseGC { get; set; }

        [Range(1, 9)] [NinjaScriptProperty]
        [Display(Name = "Poles", Description = "DonovanWall Gaussian filter poles (1–9). Higher = smoother but more lag.", Order = 2, GroupName = "Gaussian Channel")]
        public int Poles { get; set; }

        [Range(2, int.MaxValue)] [NinjaScriptProperty]
        [Display(Name = "Sampling Period", Order = 3, GroupName = "Gaussian Channel")]
        public int Per { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Reduced Lag Mode", Order = 4, GroupName = "Gaussian Channel")]
        public bool ModeLag { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Fast Response Mode", Order = 5, GroupName = "Gaussian Channel")]
        public bool ModeFast { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Chop Filter", Description = "Block THIS sensor's own signal during a choppy tape (Choppiness Index above threshold). The raw regime is still published to the Council (its VetoOnChop stays in charge).", Order = 1, GroupName = "Choppiness")]
        public bool UseChop { get; set; }

        [Range(1, int.MaxValue)] [NinjaScriptProperty]
        [Display(Name = "Chop Length", Order = 2, GroupName = "Choppiness")]
        public int ChopLength { get; set; }

        [Range(1, 100)] [NinjaScriptProperty]
        [Display(Name = "Chop Threshold", Description = "Below = trending (regime OK); above = choppy (the Council chop veto fires). Classic 61.8; 50 = stricter.", Order = 3, GroupName = "Choppiness")]
        public int ChopThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Publish STF to Sneaky", Description = "Publish the GC-slope trend + chop regime as SneakyCore.StfState so the Council gains a trend voter + chop veto. Needs SneakyCore ≥ v1.22.0.", Order = 10, GroupName = "Sneaky")]
        public bool PublishState { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Log Signal Changes", Description = "Write filtered long/short signals to sneaky.log.", Order = 11, GroupName = "Sneaky")]
        public bool LogChanges { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Card", Order = 12, GroupName = "Sneaky")]
        public bool ShowCard { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Card Corner", Description = "Which chart corner the Sneaky card docks to. Cards in the same corner auto-stack.", Order = 13, GroupName = "Sneaky")]
        public SneakyCardCorner CardCorner { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show indicator label", Description = "Show NinjaTrader's chart name label. Sneaky default = OFF (clean chart); turn on to restore it.", GroupName = "Sneaky", Order = 100)]
        public bool ShowIndicatorLabel { get; set; }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

#endregion
