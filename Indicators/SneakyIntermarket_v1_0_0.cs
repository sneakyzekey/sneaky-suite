// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
//
// Copyright (c) 2026 silentsudo-io and the Sentinel Suite contributors.
// ─────────────────────────────────────────────────────────────────────────────
//
// ═════════════════════════════════════════════════════════════════════════════
//  SNEAKY INTERMARKET v1.0.0 — correlated-instrument directional lean.
//
//  WHY THIS ONE MATTERS MORE THAN ANOTHER TREND TOOL
//  Nearly every voter on the Council reads the SAME OHLC stream through a
//  different filter. When nine of them agree that is not nine witnesses
//  confirming a fact, it is one witness saying the same thing nine ways. This
//  sensor is one of only three inputs in the whole suite that is NOT derived
//  from the chart instrument's own price:
//      PRS  — classified order flow (tick tape)
//      CVB  — Drift's flow-confirmed brick bias (tick tape)
//      IMKT — this: what OTHER, correlated markets are doing
//
//  WHAT IT DOES
//  Reads a configurable set of reference instruments, each with a POLARITY
//  (+1 correlated, -1 inverse), scores each one's own short-term direction,
//  and publishes a single net LEAN for the chart instrument.
//
//  Built against SneakyCore's published IntermarketState contract (seam added in
//  Core v1.12.0). Per that changelog the seam is INSTRUMENT-KEYED, not scope-
//  keyed, deliberately: "a macro lean derived from OTHER instruments" is a
//  property of the market, not of your 1-minute chart. One instance therefore
//  serves every consumer of that instrument, on any chart or timeframe.
//
//  ── THE HONEST CAVEAT, READ THIS ────────────────────────────────────────────
//  CORRELATION IS REGIME-DEPENDENT AND THE DEFAULT POLARITIES WILL BE WRONG
//  SOMETIMES. ES/YM/RTY track NQ closely enough that +1 is safe most days. ZN
//  and DXY are the interesting ones precisely because they are NOT stable: NQ
//  vs rates flips sign depending on whether the tape is trading growth or
//  inflation. A fixed polarity encodes a market view, so the defaults below
//  ship with only the equity indices ENABLED and the macro refs present but
//  OFF. Turning them on is a deliberate choice, not a free upgrade.
//
//  AdaptivePolarity (default OFF) measures realised correlation over a rolling
//  window and flips the sign when the market disagrees with your config. That
//  removes the stale-view problem and adds a lag problem in its place. Neither
//  is strictly better; it is unmeasured either way.
//
//  Places no orders. Publishes a seam and draws a card.
// ═════════════════════════════════════════════════════════════════════════════

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text;
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
	public class SneakyIntermarket_v1_0_0 : Indicator, ISneakyScrollCard
	{
		private sealed class Ref
		{
			public string Symbol;
			public int    Polarity;      // configured: +1 correlated, -1 inverse
			public int    Bip = -1;      // BarsInProgress index once resolved
			public double Weight = 1.0;
			public int    Dir;           // this ref's own short-term direction
			public double Corr;          // measured correlation vs chart instrument
			public int    EffPolarity;   // polarity actually used (adaptive may flip it)
			public bool   Warm;
			public int    Bars;          // bars available on that series
			public string Why = "";      // why it is cold, for the card
		}

		private readonly List<Ref> _refs = new List<Ref>();
		private readonly List<double> _ownRet = new List<double>();
		private readonly Dictionary<string, List<double>> _refRet = new Dictionary<string, List<double>>();

		private int    _lean;
		private double _score;
		private int    _refCount;
		private string _refSummary = "";
		private SneakySkin.Painter _sp;
		private DateTime _lastRenderExLog = DateTime.MinValue;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Correlated-instrument directional lean. Reads a configurable set of reference "
					+ "instruments with per-reference polarity, scores each one's short-term direction, and "
					+ "publishes a single net LEAN for the chart instrument via SneakyCore.SetIntermarketState. "
					+ "One of only three inputs in the suite NOT derived from the chart instrument's own price, "
					+ "so it adds information the trend bloc structurally cannot. INSTRUMENT-KEYED per the Core "
					+ "v1.12.0 contract, so one instance serves every consumer of that instrument on any chart. "
					+ "CAUTION: correlation is regime-dependent — the macro refs (ZN/DXY) ship DISABLED because "
					+ "their sign flips with the macro regime. Places no orders.";
				Name                     = "Sneaky Intermarket v1.0.0";
				Calculate                = Calculate.OnBarClose;
				IsOverlay                = true;
				DisplayInDataBox         = true;
				PaintPriceMarkers        = false;
				IsSuspendedWhileInactive = false;
				IsAutoScale              = false;
				ShowIndicatorLabel       = false;

				// Equity indices — safe defaults, ON.
				Ref1Symbol = "ES";  Ref1Polarity =  1; Ref1Weight = 1.0; UseRef1 = true;
				Ref2Symbol = "YM";  Ref2Polarity =  1; Ref2Weight = 0.8; UseRef2 = true;
				Ref3Symbol = "RTY"; Ref3Polarity =  1; Ref3Weight = 0.6; UseRef3 = true;
				// Macro — present but OFF. Their sign is NOT stable (see header).
				Ref4Symbol = "ZN";  Ref4Polarity = -1; Ref4Weight = 0.7; UseRef4 = false;
				Ref5Symbol = "DXY"; Ref5Polarity = -1; Ref5Weight = 0.5; UseRef5 = false;

				RefPeriodMinutes  = 1;
				DirLookback       = 5;
				LeanThreshold     = 0.25;
				AdaptivePolarity  = false;
				CorrWindow        = 120;
				MinCorrToCount    = 0.15;
				PublishIntermarket = true;
				StaleSec          = 90.0;

				ShowCard   = true;
				CardCorner = SneakyCardCorner.TopLeft;

				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Lean");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Score");
			}
			else if (State == State.Configure)
			{
				// Every reference series must be declared HERE. A hosted indicator cannot
				// call AddDataSeries itself, so if this is ever nested the host has to
				// declare the identical set in the identical order.
				_refs.Clear();
				_nextBip = 1;
				AddRef(UseRef1, Ref1Symbol, Ref1Polarity, Ref1Weight);
				AddRef(UseRef2, Ref2Symbol, Ref2Polarity, Ref2Weight);
				AddRef(UseRef3, Ref3Symbol, Ref3Polarity, Ref3Weight);
				AddRef(UseRef4, Ref4Symbol, Ref4Polarity, Ref4Weight);
				AddRef(UseRef5, Ref5Symbol, Ref5Polarity, Ref5Weight);
			}
			else if (State == State.DataLoaded)
			{
				// Bip was already assigned in Configure, by call order — AddDataSeries
				// always appends to BarsArray in the order it's called, so no name
				// matching is needed (and name matching proved unreliable: feeds format
				// MasterInstrument.Name/FullName inconsistently vs. what resolves a
				// working AddDataSeries call). Just sanity-check the slot is real here.
				foreach (var r in _refs)
				{
					bool ok = r.Bip > 0 && r.Bip < BarsArray.Length
						&& BarsArray[r.Bip] != null && BarsArray[r.Bip].Instrument != null;
					if (!ok) r.Bip = -1;
					if (!_refRet.ContainsKey(r.Symbol)) _refRet[r.Symbol] = new List<double>();
					r.EffPolarity = r.Polarity;
					try
					{
						Print("[SneakyIntermarket] " + r.Symbol + " -> "
							+ (r.Bip < 0 ? "NOT RESOLVED (check the symbol exists in your instrument list "
										 + "and that your data feed carries it)"
										 : "BarsInProgress " + r.Bip + " ("
										 + BarsArray[r.Bip].Instrument.FullName + ")"));
					}
					catch { }
				}
				if (!ShowIndicatorLabel) Name = string.Empty;
			}
			else if (State == State.Terminated)
			{
				if (_sp != null) { try { _sp.Dispose(); } catch { } _sp = null; }
				try { SneakySkin.CardLayout.Release(this); } catch { }
			}
		}

		private int _nextBip;

		private void AddRef(bool use, string sym, int pol, double w)
		{
			if (!use || string.IsNullOrWhiteSpace(sym)) return;
			try
			{
				AddDataSeries(sym.Trim(), BarsPeriodType.Minute, Math.Max(1, RefPeriodMinutes));
				_refs.Add(new Ref { Symbol = sym.Trim(), Polarity = pol >= 0 ? 1 : -1,
					Weight = Math.Max(0.0, w), Bip = _nextBip });
				_nextBip++;
			}
			catch (Exception ex)
			{
				// A bad or unsubscribed symbol must not take the whole indicator down —
				// it simply doesn't contribute, and RefCount on the card shows the shortfall.
				try { Print("[SneakyIntermarket] could not add '" + sym + "': " + ex.Message); } catch { }
			}
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0) return;
			if (CurrentBar < Math.Max(DirLookback, 2)) return;

			// rolling own-instrument returns, for the adaptive-polarity correlation
			if (CurrentBar > 0 && Close[1] > 0)
			{
				_ownRet.Add(Math.Log(Close[0] / Close[1]));
				if (_ownRet.Count > Math.Max(10, CorrWindow)) _ownRet.RemoveAt(0);
			}

			double num = 0.0, den = 0.0;
			int warm = 0;
			var sb = new StringBuilder();

			foreach (var r in _refs)
			{
				r.Warm = false;
				// Distinguish the failure modes. "not warm" can mean three completely
				// different things and the fix differs for each.
				if (r.Bip < 0 || r.Bip >= BarsArray.Length)
				{ r.Why = "no series (symbol did not resolve)"; r.Bars = -1; continue; }

				r.Bars = CurrentBars[r.Bip];
				if (r.Bars < 0)
				{ r.Why = "series added but NO DATA (not subscribed?)"; continue; }
				if (r.Bars < Math.Max(DirLookback, 2))
				{ r.Why = "warming (" + r.Bars + "/" + Math.Max(DirLookback, 2) + " bars)"; continue; }
				r.Why = "";

				var series = Closes[r.Bip];

				// this reference's own short-term direction
				double now = series[0], then = series[Math.Min(DirLookback, CurrentBars[r.Bip])];
				if (then <= 0) continue;
				double chg = (now - then) / then;
				r.Dir = chg > 0 ? 1 : chg < 0 ? -1 : 0;

				// rolling returns for correlation
				if (CurrentBars[r.Bip] > 0 && series[1] > 0)
				{
					var lst = _refRet[r.Symbol];
					lst.Add(Math.Log(series[0] / series[1]));
					if (lst.Count > Math.Max(10, CorrWindow)) lst.RemoveAt(0);
					r.Corr = Corr(_ownRet, lst);
				}

				// ADAPTIVE POLARITY — trust measured correlation over configured sign.
				// Removes the stale-view problem, adds a lag problem: the window has to
				// fill before a regime change shows up. Off by default.
				r.EffPolarity = r.Polarity;
				if (AdaptivePolarity && Math.Abs(r.Corr) >= MinCorrToCount)
					r.EffPolarity = r.Corr >= 0 ? 1 : -1;

				// A reference whose correlation has collapsed carries no information about
				// this instrument, so it abstains rather than voting noise.
				if (AdaptivePolarity && Math.Abs(r.Corr) < MinCorrToCount)
				{ r.Why = "decorrelated (|r|=" + Math.Abs(r.Corr).ToString("0.00") + " < " + MinCorrToCount.ToString("0.00") + ")"; continue; }

				r.Warm = true; warm++;
				num += r.Dir * r.EffPolarity * r.Weight;
				den += r.Weight;

				if (sb.Length > 0) sb.Append(' ');
				sb.Append(r.Symbol).Append(':').Append(r.Dir * r.EffPolarity > 0 ? '+' : r.Dir == 0 ? '0' : '-');
			}

			_refCount   = warm;
			_score      = den > 0.0 ? num / den : 0.0;          // -1..1
			_lean       = _score >  LeanThreshold ?  1
						: _score < -LeanThreshold ? -1 : 0;
			_refSummary = sb.ToString();

			Values[0][0] = _lean;
			Values[1][0] = _score;

			if (PublishIntermarket && Instrument != null && Instrument.MasterInstrument != null)
			{
				try
				{
					// INSTRUMENT-KEYED, per the Core v1.12.0 contract. Object-passing setter.
					SneakyCore.SetIntermarketState(new SneakyCore.IntermarketState
					{
						Instrument = Instrument.MasterInstrument.Name,
						Lean       = _lean,
						Score      = _score,
						RefCount   = _refCount,
						Refs       = _refSummary,
						Source     = "SneakyIntermarket"
					});
				}
				catch (Exception ex) { try { Print("[SneakyIntermarket] publish failed: " + ex.Message); } catch { } }
			}
		}

		/// <summary>Pearson correlation over the overlapping tail of two return series.</summary>
		private static double Corr(List<double> a, List<double> b)
		{
			int n = Math.Min(a.Count, b.Count);
			if (n < 10) return 0.0;
			double ma = 0, mb = 0;
			for (int i = 0; i < n; i++) { ma += a[a.Count - n + i]; mb += b[b.Count - n + i]; }
			ma /= n; mb /= n;
			double sab = 0, saa = 0, sbb = 0;
			for (int i = 0; i < n; i++)
			{
				double da = a[a.Count - n + i] - ma, db = b[b.Count - n + i] - mb;
				sab += da * db; saa += da * da; sbb += db * db;
			}
			double d = Math.Sqrt(saa * sbb);
			return d > 0 ? sab / d : 0.0;
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
				{
					_lastRenderExLog = DateTime.UtcNow;
					try { Print("[SneakyIntermarket] RenderCard threw: " + ex.Message); } catch { }
				}
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
			int rows = Math.Max(1, _refs.Count);
			const float cw = 250f;
			float ch = 118f + rows * 13f + 40f;

			SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
			var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
				ChartControl.ChartPanels[0].X, ChartControl.ChartPanels[0].Y, ChartControl.ChartPanels[0].W, ChartControl.ChartPanels[0].H, cw, ch);
			if (slot.Width <= 0f) return;   // not the currently-selected card in the rotation

			var col = _lean > 0 ? SneakySkin.CUp : _lean < 0 ? SneakySkin.CDown : SneakySkin.CMute;
			var r = _sp.Card(slot.X, slot.Y, cw, ch, col);

			_sp.Dot(r.Left + 5f, r.Top + 8f, col, _lean != 0);
			_sp.Text("INTERMARKET", r.Left + 16f, r.Top, r.Width - 20f, 16f, SneakySkin.CInk, 11f, true);

			_sp.Text(_lean > 0 ? "LEAN UP" : _lean < 0 ? "LEAN DOWN" : "NEUTRAL",
				r.Left, r.Top + 24f, r.Width, 20f, col, 15f, true);

			_sp.Text("score " + _score.ToString("0.00") + "   refs " + _refCount + "/" + _refs.Count,
				r.Left, r.Top + 48f, r.Width, 14f, SneakySkin.CInk2, 10.5f);

			_sp.Text(AdaptivePolarity ? "polarity: adaptive (measured)" : "polarity: fixed (configured)",
				r.Left, r.Top + 64f, r.Width, 14f, SneakySkin.CMute, 10f);

			if (_refCount == 0)
				_sp.Text("no warm references — check symbols/subscriptions",
					r.Left, r.Top + 80f, r.Width, 14f, SneakySkin.CWarn, 10f, true);

			_sp.Text("REFERENCES", r.Left, r.Top + 100f, r.Width, 12f, SneakySkin.CMute, 8f, true);
			for (int i = 0; i < _refs.Count; i++)
			{
				var rf = _refs[i];
				var rc = !rf.Warm ? SneakySkin.CMute
					   : (rf.Dir * rf.EffPolarity) > 0 ? SneakySkin.CUp
					   : (rf.Dir * rf.EffPolarity) < 0 ? SneakySkin.CDown : SneakySkin.CMute;
				string line = rf.Symbol
					+ (rf.EffPolarity < 0 ? " (inv)" : "")
					+ "  " + (rf.Dir > 0 ? "\u2191" : rf.Dir < 0 ? "\u2193" : "\u00b7")
					+ (rf.Warm
						? (AdaptivePolarity ? "  r=" + rf.Corr.ToString("0.00") : "")
						: "  " + rf.Why);
				_sp.Text(line, r.Left, r.Top + 114f + i * 13f, r.Width, 13f, rc, 8.5f, rf.Warm);
			}
		}

		[Browsable(false)] [XmlIgnore] public Series<double> Lean  => Values[0];
		[Browsable(false)] [XmlIgnore] public Series<double> Score => Values[1];

		#region Properties

		[NinjaScriptProperty] [Display(Name = "Use Ref 1", GroupName = "1. Reference 1", Order = 0)] public bool UseRef1 { get; set; }
		[NinjaScriptProperty] [Display(Name = "Ref 1 Symbol", GroupName = "1. Reference 1", Order = 1)] public string Ref1Symbol { get; set; }
		[NinjaScriptProperty] [Range(-1, 1)] [Display(Name = "Ref 1 Polarity (+1 / -1)", GroupName = "1. Reference 1", Order = 2)] public int Ref1Polarity { get; set; }
		[NinjaScriptProperty] [Range(0.0, 5.0)] [Display(Name = "Ref 1 Weight", GroupName = "1. Reference 1", Order = 3)] public double Ref1Weight { get; set; }

		[NinjaScriptProperty] [Display(Name = "Use Ref 2", GroupName = "2. Reference 2", Order = 0)] public bool UseRef2 { get; set; }
		[NinjaScriptProperty] [Display(Name = "Ref 2 Symbol", GroupName = "2. Reference 2", Order = 1)] public string Ref2Symbol { get; set; }
		[NinjaScriptProperty] [Range(-1, 1)] [Display(Name = "Ref 2 Polarity (+1 / -1)", GroupName = "2. Reference 2", Order = 2)] public int Ref2Polarity { get; set; }
		[NinjaScriptProperty] [Range(0.0, 5.0)] [Display(Name = "Ref 2 Weight", GroupName = "2. Reference 2", Order = 3)] public double Ref2Weight { get; set; }

		[NinjaScriptProperty] [Display(Name = "Use Ref 3", GroupName = "3. Reference 3", Order = 0)] public bool UseRef3 { get; set; }
		[NinjaScriptProperty] [Display(Name = "Ref 3 Symbol", GroupName = "3. Reference 3", Order = 1)] public string Ref3Symbol { get; set; }
		[NinjaScriptProperty] [Range(-1, 1)] [Display(Name = "Ref 3 Polarity (+1 / -1)", GroupName = "3. Reference 3", Order = 2)] public int Ref3Polarity { get; set; }
		[NinjaScriptProperty] [Range(0.0, 5.0)] [Display(Name = "Ref 3 Weight", GroupName = "3. Reference 3", Order = 3)] public double Ref3Weight { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use Ref 4", Description = "OFF by default. ZN/NQ correlation is REGIME-DEPENDENT — the sign flips depending on whether the tape is trading growth or rates. Enabling this encodes a macro view.", GroupName = "4. Reference 4 (macro)", Order = 0)]
		public bool UseRef4 { get; set; }
		[NinjaScriptProperty] [Display(Name = "Ref 4 Symbol", GroupName = "4. Reference 4 (macro)", Order = 1)] public string Ref4Symbol { get; set; }
		[NinjaScriptProperty] [Range(-1, 1)] [Display(Name = "Ref 4 Polarity (+1 / -1)", GroupName = "4. Reference 4 (macro)", Order = 2)] public int Ref4Polarity { get; set; }
		[NinjaScriptProperty] [Range(0.0, 5.0)] [Display(Name = "Ref 4 Weight", GroupName = "4. Reference 4 (macro)", Order = 3)] public double Ref4Weight { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use Ref 5", Description = "OFF by default — same regime caveat as Ref 4.", GroupName = "5. Reference 5 (macro)", Order = 0)]
		public bool UseRef5 { get; set; }
		[NinjaScriptProperty] [Display(Name = "Ref 5 Symbol", GroupName = "5. Reference 5 (macro)", Order = 1)] public string Ref5Symbol { get; set; }
		[NinjaScriptProperty] [Range(-1, 1)] [Display(Name = "Ref 5 Polarity (+1 / -1)", GroupName = "5. Reference 5 (macro)", Order = 2)] public int Ref5Polarity { get; set; }
		[NinjaScriptProperty] [Range(0.0, 5.0)] [Display(Name = "Ref 5 Weight", GroupName = "5. Reference 5 (macro)", Order = 3)] public double Ref5Weight { get; set; }

		[NinjaScriptProperty] [Range(1, 240)]
		[Display(Name = "Reference Period (minutes)", Description = "Bar size loaded for each reference instrument.", GroupName = "6. Calculation", Order = 0)]
		public int RefPeriodMinutes { get; set; }

		[NinjaScriptProperty] [Range(1, 200)]
		[Display(Name = "Direction Lookback (bars)", Description = "How far back each reference's own direction is measured.", GroupName = "6. Calculation", Order = 1)]
		public int DirLookback { get; set; }

		[NinjaScriptProperty] [Range(0.0, 1.0)]
		[Display(Name = "Lean Threshold", Description = "|score| must exceed this before Lean leaves 0. Higher = fewer, firmer leans.", GroupName = "6. Calculation", Order = 2)]
		public double LeanThreshold { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Adaptive Polarity", Description = "Measure realised correlation over a rolling window and OVERRIDE the configured sign when the market disagrees. Fixes the stale-view problem, introduces a lag problem: the window must refill after a regime change. References whose |correlation| falls below the minimum abstain entirely rather than vote noise. Unmeasured either way — off by default.", GroupName = "6. Calculation", Order = 3)]
		public bool AdaptivePolarity { get; set; }

		[NinjaScriptProperty] [Range(20, 2000)]
		[Display(Name = "Correlation Window (bars)", GroupName = "6. Calculation", Order = 4)]
		public int CorrWindow { get; set; }

		[NinjaScriptProperty] [Range(0.0, 1.0)]
		[Display(Name = "Min |Correlation| To Count", Description = "With Adaptive Polarity on, a reference below this abstains — a decorrelated market carries no information about this one.", GroupName = "6. Calculation", Order = 5)]
		public double MinCorrToCount { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Publish Intermarket to Sneaky", Description = "Broadcast the lean via SneakyCore.SetIntermarketState. INSTRUMENT-KEYED per the Core contract, so one instance serves every consumer of this instrument on any chart or timeframe.", GroupName = "7. Sneaky", Order = 0)]
		public bool PublishIntermarket { get; set; }

		[NinjaScriptProperty] [Range(1.0, 600.0)]
		[Display(Name = "Consult Stale (sec)", GroupName = "7. Sneaky", Order = 1)]
		public double StaleSec { get; set; }

		[NinjaScriptProperty] [Display(Name = "Show Card", GroupName = "8. Display", Order = 0)] public bool ShowCard { get; set; }
		[NinjaScriptProperty] [Display(Name = "Card Corner", GroupName = "8. Display", Order = 1)] public SneakyCardCorner CardCorner { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show Indicator Label", GroupName = "8. Display", Order = 2)] public bool ShowIndicatorLabel { get; set; }

		#endregion
	}
}
