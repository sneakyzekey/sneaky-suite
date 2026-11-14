// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
//
// Copyright (c) 2026 silentsudo-io and the Sentinel Suite contributors.
// ─────────────────────────────────────────────────────────────────────────────
#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.AddOns.Sneaky;
using NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors;
#endregion

// ═════════════════════════════════════════════════════════════════════════════
//  Buy/Sell Volume Pressure Mountain — Sneaky-homed rebuild of BuySellVolumePressureMountainV001/V002
//  File: SneakyBuySellVolumePressureMountain_v1_0_0.cs   |   Version: v1.0.0   |   namespace …Indicators.Sneaky
// ─────────────────────────────────────────────────────────────────────────────
//  WHAT IT IS
//    Sub-panel oscillator that splits each bar's volume into buy vs sell "mountains" (green up / red down),
//    smooths them into a buy% / sell% pressure reading, and surfaces the state in a Sneaky glass card.
//    Edge lane — a discretionary READ tool, submits no orders.
//
//  IMPROVEMENTS OVER V001/V002
//    • HYBRID order-flow source: classifies TRUE bid/ask volume via OnMarketData when live ticks are present,
//      and falls back to the OHLC candle-shape proxy on historical/backtest bars (or when forced off). The card
//      lights TICK (cyan) vs OHLC (mute) so you always know which you're reading.
//    • PRESSURE DIVERGENCE flags: marks a swing high where buy-pressure fails to confirm (bearish) or a swing
//      low where sell-pressure fails to confirm (bullish) — a reversal tell the originals never surfaced.
//    • Full Sneaky styling: SneakySkin.Painter glass card (CardLayout anti-overlap + CardCorner), suite
//      palette (green=buy, red=sell, cyan=live), label remover, Indicators.Sneaky picker folder.
//    • Simplified: dropped V001's fiddly absolute-volume Long/Short signal block (instrument-specific, rarely
//      useful). The ratio-based STRONG dominance markers are kept (optional).
//
//  DESIGN-SYSTEM NOTES  (Docs/SENTINEL_DESIGN_SYSTEM.md)
//    §4b Painter glass card + CardLayout + mandatory label remover · §7 namespace/naming.
//    OnMarketData only fires realtime / tick-replay, so historical load auto-uses the proxy (accumulator == 0).
//
//  CHANGELOG
//    v1.0.0 (in-place 2026-07-07) — SENTINEL PLOT SKIN: OnRender paints a glass PanelWash (covers stock plots)
//             + zero baseline + two-sided gradient HISTOBARS (buy = CUp above zero, sell = CDown below). Toggle
//             SneakyPlotSkin (default ON); stock gridlines off. Design system §4c. No logic change.
//    v1.0.0 (2026-07-05) — first cut. Fresh Sneaky identity; hybrid tick/OHLC classification; divergence
//                          flags; glass-card readout; V001 long/short absolute-volume signals removed.
//                          FIX: freeze all WPF brushes (defaults + deserialized) — unfrozen brushes threw
//                          "calling thread cannot access this object" in OnBarUpdate → nothing plotted.
//                          Mountains recolored to the candle skin: buy = teal #009999, sell = grey #8E8E8E.
// ═════════════════════════════════════════════════════════════════════════════

namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors
{
	public class SneakyBuySellVolumePressureMountain_v1_0_0 : Indicator, ISneakyScrollCard
	{
		#region Private fields

		// Derived pressure series
		private Series<double> buyVolume;
		private Series<double> sellVolume;
		private Series<double> buyVolumeAvg;
		private Series<double> sellVolumeAvg;
		private Series<double> buyMountainStrength;
		private Series<double> sellMountainStrength;

		// Latest snapshot (for the glass card)
		private double latestBuyPct;
		private double latestSellPct;
		private double latestDelta;
		private double latestDomRatio;
		private bool   barUsedTicks;

		// Hybrid order-flow accumulator (OnMarketData → classified per-bar volume)
		private double curBid, curAsk, lastTrade;
		private long   barBuyVol, barSellVol;
		private int    accumBar = -1;

		// Divergence pivot memory
		private bool   havePrevHigh, havePrevLow;
		private double prevHighPrice, prevHighPressure, prevLowPrice, prevLowPressure;
		private int    prevHighBar, prevLowBar;
		private int    lastDivBar = -1000;

		// Sneaky glass-card readout
		private SneakySkin.Painter _sp;
		private int _lastRenderExLog = 0;   // FIXED 2026-08-03 — was int.MinValue, which overflows in unchecked(TickCount - X) and permanently evaluates negative, silently blocking every exception log for the whole session. 0 is a safe sentinel: TickCount for any system up more than a few seconds is comfortably past any real threshold on the first check.

		#endregion

		#region OnStateChange

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "Buy/Sell volume pressure mountains with hybrid tick/OHLC order-flow classification, pressure-divergence flags, and a Sneaky glass card. Edge lane (no orders).";
				Name						= "Sneaky Buy Sell Volume Pressure Mountain v1.0.0";
				Calculate					= Calculate.OnEachTick;
				IsOverlay					= false;
				DisplayInDataBox			= true;
				DrawOnPricePanel			= false;
				DrawHorizontalGridLines		= false;   // plot skin paints its own wash + baseline
				DrawVerticalGridLines		= false;
				PaintPriceMarkers			= false;
				IsSuspendedWhileInactive	= true;

				// Detection
				BuyVolumeAvgLength			= 17;
				SellVolumeAvgLength			= 17;
				UseRealOrderFlow			= true;
				BuySellDominanceRatio		= 1.75;

				// Mountain visual
				NeutralZonePercent			= 2.0;
				MountainSmoothingLength		= 3;
				MountainScale				= 100.0;
				ShowStrongMarkers			= false;
				ShowZeroLine				= true;

				// Divergence
				ShowDivergences				= true;
				DivergenceStrength			= 3;
				DivergenceLookbackBars		= 60;

				// Sneaky
				ShowInfo					= true;
				SneakyPlotSkin			= true;   // render the panel to the Sneaky plot standard
				CardCorner					= SneakyCardCorner.TopRight;
				ShowIndicatorLabel			= false;   // Sneaky standard: clean chart (NT name label removed)
				PublishState				= true;    // design system §9 item 6: publish default ON

				// Brushes (suite palette) — FROZEN so they're safe to touch on the calc/render thread
				// (an unfrozen WPF SolidColorBrush throws "calling thread cannot access this object").
				// Mountains match the Sneaky CANDLE skin: buy = teal, sell = grey (user pick 2026-07-05).
				BuyMountainBrush			= Frozen(0, 153, 153);    // teal — candle up / buy
				SellMountainBrush			= Frozen(142, 142, 142);  // grey — candle down / sell
				ZeroLineBrush				= Frozen(108, 122, 146);  // mute
				StrongBuyBrush				= Frozen(37, 208, 139);
				StrongSellBrush				= Frozen(255, 92, 106);
				BullishDivBrush				= Frozen(37, 208, 139);
				BearishDivBrush				= Frozen(255, 92, 106);

				AddLine(new Stroke(Brushes.Gray, 1), 0, "Zero Line");

				AddPlot(new Stroke(Frozen(0, 153, 153), 3), PlotStyle.Bar, "BuyMountainPlot");
				AddPlot(new Stroke(Frozen(142, 142, 142), 3), PlotStyle.Bar, "SellMountainPlot");

				AddPlot(Brushes.Transparent, "BuyPercent");
				AddPlot(Brushes.Transparent, "SellPercent");
				AddPlot(Brushes.Transparent, "PressureDelta");
				AddPlot(Brushes.Transparent, "DominantSide");
				AddPlot(Brushes.Transparent, "BuyVolumeClassified");
				AddPlot(Brushes.Transparent, "SellVolumeClassified");
			}
			else if (State == State.DataLoaded)
			{
				if (!ShowIndicatorLabel) Name = string.Empty;   // Sneaky label remover — NT draws the chart panel label from Name

				// Freeze any user-edited / deserialized brush so the calc + render threads can touch it safely.
				BuyMountainBrush	= FreezeBrush(BuyMountainBrush);
				SellMountainBrush	= FreezeBrush(SellMountainBrush);
				ZeroLineBrush		= FreezeBrush(ZeroLineBrush);
				StrongBuyBrush		= FreezeBrush(StrongBuyBrush);
				StrongSellBrush		= FreezeBrush(StrongSellBrush);
				BullishDivBrush		= FreezeBrush(BullishDivBrush);
				BearishDivBrush		= FreezeBrush(BearishDivBrush);

				buyVolume				= new Series<double>(this);
				sellVolume				= new Series<double>(this);
				buyVolumeAvg			= new Series<double>(this);
				sellVolumeAvg			= new Series<double>(this);
				buyMountainStrength		= new Series<double>(this);
				sellMountainStrength	= new Series<double>(this);
			}
			else if (State == State.Terminated)
			{
				if (_sp != null) { try { _sp.Dispose(); } catch { } _sp = null; }
				try { SneakySkin.CardLayout.Release(this); } catch { }
			}
		}

		#endregion

		#region OnMarketData — true bid/ask classification (hybrid source)

		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (!UseRealOrderFlow) return;

			if (e.MarketDataType == MarketDataType.Ask) { curAsk = e.Price; return; }
			if (e.MarketDataType == MarketDataType.Bid) { curBid = e.Price; return; }
			if (e.MarketDataType != MarketDataType.Last) return;

			// Reset the per-bar accumulator on the first trade of a new bar (keeps that first tick).
			if (CurrentBar != accumBar) { barBuyVol = 0; barSellVol = 0; accumBar = CurrentBar; }

			long   v = (long)e.Volume;
			double p = e.Price;

			if (curAsk > 0 && p >= curAsk)      barBuyVol  += v;   // lifted the offer → buy
			else if (curBid > 0 && p <= curBid) barSellVol += v;   // hit the bid → sell
			else if (p > lastTrade)             barBuyVol  += v;   // uptick fallback
			else if (p < lastTrade)             barSellVol += v;   // downtick fallback
			else { barBuyVol += v / 2; barSellVol += v - v / 2; }  // flat print → split

			lastTrade = p;

			// Heartbeat: SetPressureState only publishes on bar close, so on a tick/volume bar
			// type in a quiet market the seam would age out and read ABSENT when the sensor is
			// simply waiting. Re-stamp on the tape. [[state-seam-freshness-heartbeat]] — "say
			// STALE, never ABSENT". Ported from main 2026-08-17.
			if (PublishState && State == State.Realtime)
			{
				try { SneakyCore.TouchPressureState(SneakyCore.ScopeOf(Instrument, BarsPeriod, ChartControl)); }
				catch { }
			}
		}

		#endregion

		#region OnBarUpdate

		private int  _prsDivergence;
		private bool _prsStrong;

		// NOT [NinjaScriptProperty]: adding a constructor arg would break the generated
		// sub-indicator call Confluence uses. A plain [Display] still shows in the dialog.
		[System.ComponentModel.DataAnnotations.Range(50.0, 100.0)]
		[System.ComponentModel.DataAnnotations.Display(Name = "Strong Pressure Threshold (%)",
			Description = "Dominant-side percentage at or above which PressureState.Strong is set. 50% is a coin flip; this is where dominance stops being sampling noise.",
			GroupName = "Seam", Order = 90)]
		public double StrongPressurePercent { get; set; } = 60.0;

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 1 || Volume[0] == 0 || Math.Abs(High[0] - Low[0]) < TickSize * 0.0001)
			{
				SetAllValuesToZero();
				return;
			}

			// ── classify this bar's volume: real ticks when present, OHLC proxy otherwise ──
			double rawBuy, rawSell;
			bool haveTicks = UseRealOrderFlow && accumBar == CurrentBar && (barBuyVol + barSellVol) > 0;

			if (haveTicks)
			{
				rawBuy       = barBuyVol;
				rawSell      = barSellVol;
				barUsedTicks = true;
			}
			else
			{
				double range = High[0] - Low[0];
				rawBuy       = Math.Round(((High[0] - Open[0]) + (Close[0] - Low[0])) / 2.0 / range * Volume[0], 0);
				rawSell      = Math.Round(((Low[0] - Open[0]) + (Close[0] - High[0])) / 2.0 / range * Volume[0], 0);
				barUsedTicks = false;
			}

			buyVolume[0]  = Math.Max(0, rawBuy);
			sellVolume[0] = Math.Abs(rawSell);

			buyVolumeAvg[0]  = EMA(buyVolume, BuyVolumeAvgLength)[0];
			sellVolumeAvg[0] = EMA(sellVolume, SellVolumeAvgLength)[0];

			double totalSmoothed = buyVolumeAvg[0] + sellVolumeAvg[0];
			double buyPct  = totalSmoothed > 0 ? buyVolumeAvg[0]  / totalSmoothed * 100.0 : 0;
			double sellPct = totalSmoothed > 0 ? sellVolumeAvg[0] / totalSmoothed * 100.0 : 0;

			latestBuyPct   = buyPct;
			latestSellPct  = sellPct;
			latestDelta    = buyPct - sellPct;

			// ── mountain strength: how far a side clears the neutral-band threshold, normalized + smoothed ──
			double threshold = 50.0 + NeutralZonePercent / 2.0;

			double rawBuyStr  = buyPct  > threshold ? (buyPct  - threshold) / Math.Max(1.0, 100.0 - threshold) : 0;
			double rawSellStr = sellPct > threshold ? (sellPct - threshold) / Math.Max(1.0, 100.0 - threshold) : 0;

			rawBuyStr  = Math.Max(0, Math.Min(1, rawBuyStr));
			rawSellStr = Math.Max(0, Math.Min(1, rawSellStr));

			if (MountainSmoothingLength <= 1 || CurrentBar < 2)
			{
				buyMountainStrength[0]  = rawBuyStr;
				sellMountainStrength[0] = rawSellStr;
			}
			else
			{
				double a = 2.0 / (MountainSmoothingLength + 1.0);
				buyMountainStrength[0]  = a * rawBuyStr  + (1.0 - a) * buyMountainStrength[1];
				sellMountainStrength[0] = a * rawSellStr + (1.0 - a) * sellMountainStrength[1];
			}

			double dominantSide = 0;
			if (latestDelta > NeutralZonePercent)       dominantSide = 1;
			else if (latestDelta < -NeutralZonePercent) dominantSide = -1;

			bool strongBuy  = buyVolumeAvg[0]  >= sellVolumeAvg[0] * BuySellDominanceRatio;
			bool strongSell = sellVolumeAvg[0] >= buyVolumeAvg[0]  * BuySellDominanceRatio;
			latestDomRatio  = sellVolumeAvg[0] > 0 && buyVolumeAvg[0] > 0
				? Math.Max(buyVolumeAvg[0] / sellVolumeAvg[0], sellVolumeAvg[0] / buyVolumeAvg[0]) : 1.0;

			// ── publish plot values ──
			Values[0][0] =  buyMountainStrength[0]  * MountainScale;
			Values[1][0] = -sellMountainStrength[0] * MountainScale;
			Values[2][0] = buyPct;
			Values[3][0] = sellPct;
			Values[4][0] = latestDelta;
			Values[5][0] = dominantSide;
			Values[6][0] = buyVolume[0];
			Values[7][0] = sellVolume[0];

			// Divergence — THEIR convention (FlowState): a 0/1 FLAG, not a signed
			// direction, and it requires "meaningful θ" rather than any disagreement at
			// all. Dominance barely off 50/50 pointing the other way is noise, not
			// absorption, so the same Strong threshold gates it.
			int _barDir = Math.Sign(Close[0] - Open[0]);
			bool _meaningful = Math.Max(buyPct, sellPct) >= StrongPressurePercent;
			_prsDivergence = ((int)dominantSide != 0 && _barDir != 0
							  && (int)dominantSide != _barDir && _meaningful) ? 1 : 0;

			// Strong — THEIR convention (AdxState/CciState): "past this tool's own strong
			// threshold". For pressure that is dominance clear of the coin-flip band.
			_prsStrong = _meaningful;

			// ── SEAM PUBLISH ──────────────────────────────────────────────────────
			// Was the ONE sensor in the cascade that didn't publish, forcing consumers
			// to construct a second instance of it just to read DominantSide. Now it
			// speaks the same contract as the other seventeen: scope-keyed, age-gated.
			// UsedTicks rides along so a consumer can tell a real tick classification
			// from the OHLC fallback rather than trusting both equally.
			if (PublishState)
			{
				try
				{
					string _scope = SneakyCore.ScopeOf(Instrument, BarsPeriod, ChartControl);
					if (!string.IsNullOrEmpty(_scope))
						SneakyCore.SetPressureState(new SneakyCore.PressureState
						{
							Scope      = _scope,
							Bartype    = SneakyCore.BarTag(BarsPeriod),
							Instrument = Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : null,
							Dir        = (int)dominantSide,
							BuyPct     = buyPct, SellPct = sellPct, Delta = latestDelta, DomRatio = latestDomRatio,
							BuyVolume  = buyVolume[0], SellVolume = sellVolume[0],
							TickBacked = barUsedTicks, Divergence = _prsDivergence, Strong = _prsStrong,
							Source     = "SneakyBuySellVolumePressureMountain"
						});
				}
				catch { }
			}

			PlotBrushes[0][0] = BuyMountainBrush;
			PlotBrushes[1][0] = SellMountainBrush;

			Lines[0].Brush = ShowZeroLine ? ZeroLineBrush : Brushes.Transparent;

			if (ShowStrongMarkers)
				DrawStrongMarkers(strongBuy, strongSell);

			if (ShowDivergences)
				DetectDivergence();
		}

		#endregion

		#region Divergence detection

		// Pivot-based: a confirmed swing high where buy-pressure printed a LOWER high than the prior swing high
		// (price up, pressure down) = bearish; a swing low where sell-pressure printed a HIGHER low = bullish.
		private void DetectDivergence()
		{
			int s = DivergenceStrength;
			if (CurrentBar < 2 * s + 1)
				return;

			double ph = High[s], pl = Low[s];
			bool isHigh = true, isLow = true;
			for (int i = 0; i <= 2 * s; i++)
			{
				if (i == s) continue;
				if (High[i] >= ph) isHigh = false;
				if (Low[i]  <= pl) isLow  = false;
			}

			double pressureAtPivot = Values[4][s];   // PressureDelta at the pivot bar

			if (isHigh)
			{
				if (havePrevHigh && (CurrentBar - s) - prevHighBar <= DivergenceLookbackBars
					&& ph > prevHighPrice && pressureAtPivot < prevHighPressure)
				{
					Draw.TriangleDown(this, "bspDivBear" + (CurrentBar - s), false, s, MountainScale * 0.92, BearishDivBrush);
					lastDivBar = CurrentBar;
				}
				prevHighPrice = ph; prevHighPressure = pressureAtPivot; prevHighBar = CurrentBar - s; havePrevHigh = true;
			}

			if (isLow)
			{
				if (havePrevLow && (CurrentBar - s) - prevLowBar <= DivergenceLookbackBars
					&& pl < prevLowPrice && pressureAtPivot > prevLowPressure)
				{
					Draw.TriangleUp(this, "bspDivBull" + (CurrentBar - s), false, s, -MountainScale * 0.92, BullishDivBrush);
					lastDivBar = CurrentBar;
				}
				prevLowPrice = pl; prevLowPressure = pressureAtPivot; prevLowBar = CurrentBar - s; havePrevLow = true;
			}
		}

		#endregion

		#region Strong dominance markers

		private void DrawStrongMarkers(bool strongBuy, bool strongSell)
		{
			if (strongBuy)
				Draw.Dot(this, "bspStrongBuy" + CurrentBar, false, 0, MountainScale * 0.8, StrongBuyBrush);
			if (strongSell)
				Draw.Dot(this, "bspStrongSell" + CurrentBar, false, 0, -MountainScale * 0.8, StrongSellBrush);
		}

		#endregion

		#region Sneaky glass card

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (RenderTarget == null || ChartPanel == null) return;
			if (_sp == null) _sp = new SneakySkin.Painter();
			_sp.Begin(RenderTarget);
			try { if (SneakyPlotSkin) RenderPlotSkin(chartControl, chartScale); }
			catch (Exception ex)
			{
				if (unchecked(Environment.TickCount - _lastRenderExLog) > 15000)
				{
					_lastRenderExLog = Environment.TickCount;
					try { Print("[BuySellVolumePressureMountain] RenderPlotSkin() threw: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); } catch { }
				}
			}
			try { if (ShowInfo) RenderCard(); }
			catch (Exception ex)
			{
				if (unchecked(Environment.TickCount - _lastRenderExLog) > 15000)
				{
					_lastRenderExLog = Environment.TickCount;
					try { Print("[BuySellVolumePressureMountain] RenderCard() threw: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); } catch { }
				}
			}
			try { _sp.End(); } catch { }
		}

		// Sneaky PLOT STANDARD: glass wash + zero baseline + two-sided pressure HISTOBARS (buy = CUp above
		// zero, sell = CDown below zero). Series read by ABSOLUTE bar index (render-safe).
		private void RenderPlotSkin(ChartControl chartControl, ChartScale chartScale)
		{
			if (Bars == null || Bars.Count < 2 || ChartBars == null) return;
			float px = ChartPanel.X, py = ChartPanel.Y, pw = ChartPanel.W, ph = ChartPanel.H;
			_sp.PanelWash(px, py, pw, ph);

			int from = ChartBars.FromIndex, to = ChartBars.ToIndex;
			if (from < 0) from = 0;
			if (to > Bars.Count - 1) to = Bars.Count - 1;
			if (to < from) return;

			float yZero = chartScale.GetYByValue(0);
			_sp.Baseline(px, px + pw, yZero, SneakySkin.CInk);

			float dx = to > from ? (chartControl.GetXByBarIndex(ChartBars, to) - chartControl.GetXByBarIndex(ChartBars, to - 1)) : 6f;
			if (dx <= 0f || float.IsNaN(dx) || float.IsInfinity(dx)) dx = 6f;
			float halfW = Math.Max(0.8f, dx * 0.34f);

			for (int idx = from; idx <= to; idx++)
			{
				float x = chartControl.GetXByBarIndex(ChartBars, idx);
				if (Values[0].IsValidDataPointAt(idx))
				{
					double bv = Values[0].GetValueAt(idx);   // buy (>= 0, above zero)
					if (bv > 0) _sp.HistoBar(x, yZero, chartScale.GetYByValue(bv), halfW, SneakySkin.CUp, false);
				}
				if (Values[1].IsValidDataPointAt(idx))
				{
					double sv = Values[1].GetValueAt(idx);   // sell (<= 0, below zero)
					if (sv < 0) _sp.HistoBar(x, yZero, chartScale.GetYByValue(sv), halfW, SneakySkin.CDown, false);
				}
			}
		}

		// Sneaky glass card (content unchanged; painted inside the shared Begin/End frame).
        public void ConductorRender(SharpDX.Direct2D1.RenderTarget rt)
        {
            if (!(ShowInfo)) return;
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
			const float cw = 252f, ch = 150f;
				SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
				var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
					ChartControl.ChartPanels[0].X, ChartControl.ChartPanels[0].Y, ChartControl.ChartPanels[0].W, ChartControl.ChartPanels[0].H, cw, ch);
				if (slot.Width <= 0f) return;   // not the currently-selected card in the rotation

				bool neutral  = Math.Abs(latestDelta) <= NeutralZonePercent;
				bool buyDom   = latestDelta > 0;
				var  sideCol  = neutral ? SneakySkin.CMute : (buyDom ? SneakySkin.CUp : SneakySkin.CDown);
				bool divFresh = CurrentBar - lastDivBar <= 3;

				var edge = divFresh ? SneakySkin.CWarn : SneakySkin.CLine;
				var r    = _sp.Card(slot.X, slot.Y, cw, ch, edge);

				// header: live dot (cyan when reading REAL ticks) + title + state pill + source pill
				_sp.Dot(r.Left + 5f, r.Top + 8f, barUsedTicks ? SneakySkin.CAccent : SneakySkin.CMute, barUsedTicks);
				_sp.Text("BUY/SELL PRESSURE", r.Left + 16f, r.Top, r.Width - 70f, 16f, SneakySkin.CInk, 11f, true);
				string st = neutral ? "NEUTRAL" : (buyDom ? "BUY" : "SELL");
				float stW = _sp.Pill(st, r.Right, r.Top - 1f, sideCol);
				_sp.Pill(barUsedTicks ? "TICK" : "OHLC", r.Right - stW - 6f, r.Top - 1f,
					barUsedTicks ? SneakySkin.CAccent : SneakySkin.CMute);

				// hero: dominant pressure %
				double domPct = neutral ? 50.0 : Math.Max(latestBuyPct, latestSellPct);
				_sp.Text("DOMINANT PRESSURE", r.Left, r.Top + 26f, 160f, 12f, SneakySkin.CMute, 9f, true);
				_sp.Text(domPct.ToString("0") + "%", r.Left, r.Top + 35f, 120f, 28f, sideCol, 24f);
				_sp.Text("Δ " + (latestDelta >= 0 ? "+" : "") + latestDelta.ToString("0.0"),
					r.Left + 96f, r.Top + 45f, 120f, 16f, SneakySkin.CInk2, 12f);

				// buy-share track (0.5 = balanced; colored by dominant side)
				float frac = (float)Math.Max(0, Math.Min(1, latestBuyPct / 100.0));
				_sp.Track(r.Left, r.Top + 66f, r.Width, frac, sideCol, 5f);
				var lead = SharpDX.DirectWrite.TextAlignment.Leading;
				_sp.Text("BUY " + latestBuyPct.ToString("0") + "%     SELL " + latestSellPct.ToString("0") + "%",
					r.Left, r.Top + 76f, r.Width, 14f, SneakySkin.CInk2, 10.5f, false, lead, true);

				// delta sparkline over recent bars
				var hist = new System.Collections.Generic.List<double>();
				int n = Math.Min(40, CurrentBar);
				for (int i = n - 1; i >= 0; i--) hist.Add(Values[4][i]);
				_sp.Sparkline(r.Left, r.Top + 92f, r.Width, 18f, hist, sideCol);

				// footer stats
				_sp.Text("src " + (barUsedTicks ? "TICKS" : "OHLC") + "     dom " + latestDomRatio.ToString("0.0") + "x",
					r.Left, r.Top + 114f, r.Width, 12f, SneakySkin.CMute, 10f, false, lead, true);
		}

		#endregion

		#region Helpers

		// Frozen brush = cross-thread safe (calc/render touch these). Created once on the config thread.
		private static SolidColorBrush Frozen(byte r, byte g, byte b)
		{
			var br = new SolidColorBrush(Color.FromRgb(r, g, b));
			br.Freeze();
			return br;
		}

		private static Brush FreezeBrush(Brush b)
		{
			if (b != null && b.CanFreeze && !b.IsFrozen)
			{
				b = b.Clone();
				b.Freeze();
			}
			return b;
		}

		private void SetAllValuesToZero()
		{
			if (buyVolume != null)           buyVolume[0] = 0;
			if (sellVolume != null)          sellVolume[0] = 0;
			if (buyVolumeAvg != null)        buyVolumeAvg[0] = 0;
			if (sellVolumeAvg != null)       sellVolumeAvg[0] = 0;
			if (buyMountainStrength != null) buyMountainStrength[0] = 0;
			if (sellMountainStrength != null)sellMountainStrength[0] = 0;

			latestBuyPct = latestSellPct = latestDelta = 0;
			latestDomRatio = 1.0;

			for (int i = 0; i < Values.Length; i++)
				Values[i][0] = 0;
		}

		#endregion

		#region Consumable series (Strategy Builder / data box)

		[Browsable(false)] [XmlIgnore] public Series<double> BuyMountainPlot       => Values[0];
		[Browsable(false)] [XmlIgnore] public Series<double> SellMountainPlot      => Values[1];
		[Browsable(false)] [XmlIgnore] public Series<double> BuyPercent            => Values[2];
		[Browsable(false)] [XmlIgnore] public Series<double> SellPercent           => Values[3];
		[Browsable(false)] [XmlIgnore] public Series<double> PressureDelta         => Values[4];
		[Browsable(false)] [XmlIgnore] public Series<double> DominantSide          => Values[5];
		[Browsable(false)] [XmlIgnore] public Series<double> BuyVolumeClassified   => Values[6];
		[Browsable(false)] [XmlIgnore] public Series<double> SellVolumeClassified  => Values[7];

		#endregion

		#region Parameters — detection

		[NinjaScriptProperty] [Range(1, int.MaxValue)]
		[Display(Name = "Buy volume avg length", Order = 1, GroupName = "01. Detection")]
		public int BuyVolumeAvgLength { get; set; }

		[NinjaScriptProperty] [Range(1, int.MaxValue)]
		[Display(Name = "Sell volume avg length", Order = 2, GroupName = "01. Detection")]
		public int SellVolumeAvgLength { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use real order flow (ticks)", Description = "Classify true bid/ask volume from live ticks when present; fall back to the OHLC candle-shape proxy on historical/backtest bars. Off = proxy only.", Order = 3, GroupName = "01. Detection")]
		public bool UseRealOrderFlow { get; set; }

		[NinjaScriptProperty] [Range(0.0, 10.0)]
		[Display(Name = "Buy/Sell dominance ratio", Order = 4, GroupName = "01. Detection")]
		public double BuySellDominanceRatio { get; set; }

		#endregion

		#region Parameters — mountain visual

		[NinjaScriptProperty] [Range(0.0, 50.0)]
		[Display(Name = "Neutral zone percent", Order = 1, GroupName = "02. Mountain Visual")]
		public double NeutralZonePercent { get; set; }

		[NinjaScriptProperty] [Range(1, 100)]
		[Display(Name = "Mountain smoothing length", Order = 2, GroupName = "02. Mountain Visual")]
		public int MountainSmoothingLength { get; set; }

		[NinjaScriptProperty] [Range(1.0, 1000.0)]
		[Display(Name = "Mountain scale", Order = 3, GroupName = "02. Mountain Visual")]
		public double MountainScale { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show strong dominance markers", Order = 4, GroupName = "02. Mountain Visual")]
		public bool ShowStrongMarkers { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show zero line", Order = 5, GroupName = "02. Mountain Visual")]
		public bool ShowZeroLine { get; set; }

		#endregion

		#region Parameters — divergence

		[NinjaScriptProperty]
		[Display(Name = "Show pressure divergences", Order = 1, GroupName = "03. Divergence")]
		public bool ShowDivergences { get; set; }

		[NinjaScriptProperty] [Range(1, 20)]
		[Display(Name = "Divergence pivot strength", Description = "Bars on each side that define a confirmed swing pivot.", Order = 2, GroupName = "03. Divergence")]
		public int DivergenceStrength { get; set; }

		[NinjaScriptProperty] [Range(2, int.MaxValue)]
		[Display(Name = "Divergence lookback (bars)", Description = "Max bars back to compare the prior pivot against.", Order = 3, GroupName = "03. Divergence")]
		public int DivergenceLookbackBars { get; set; }

		#endregion

		#region Parameters — Sneaky / display

		[NinjaScriptProperty]
		[Display(Name = "Show info card", Order = 1, GroupName = "04. Sneaky")]
		public bool ShowInfo { get; set; }

		// Not [NinjaScriptProperty] — serializes without a constructor param (no generated-region churn).
		[Display(Name = "Sneaky Plot Skin", Description = "Render the panel to the Sneaky plot standard (glass wash + two-sided gradient histobars) instead of NT's stock plots.", Order = 2, GroupName = "04. Sneaky")]
		public bool SneakyPlotSkin { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Card corner", Order = 3, GroupName = "04. Sneaky")]
		public SneakyCardCorner CardCorner { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show indicator label", Description = "Show NinjaTrader's chart name label. Sneaky default = OFF (clean chart); turn on to restore it.", Order = 100, GroupName = "04. Sneaky")]
		public bool ShowIndicatorLabel { get; set; }

		// NOT a [NinjaScriptProperty] — serializes to the workspace + shows in F6 but stays OUT of the
		// generated constructor, so adding it causes no codegen churn on existing charts. Ported from main.
		[Display(Name = "Publish state seam", Description = "Publish PressureState so the Council and other tools can consult this reading. Sentinel standard = ON.", GroupName = "Sneaky", Order = 101)]
		public bool PublishState { get; set; }

		#endregion

		#region Brushes

		[XmlIgnore] [Display(Name = "Buy mountain", Order = 1, GroupName = "05. Brushes")]
		public Brush BuyMountainBrush { get; set; }
		[Browsable(false)] public string BuyMountainBrushSerializable { get { return Serialize.BrushToString(BuyMountainBrush); } set { BuyMountainBrush = Serialize.StringToBrush(value); } }

		[XmlIgnore] [Display(Name = "Sell mountain", Order = 2, GroupName = "05. Brushes")]
		public Brush SellMountainBrush { get; set; }
		[Browsable(false)] public string SellMountainBrushSerializable { get { return Serialize.BrushToString(SellMountainBrush); } set { SellMountainBrush = Serialize.StringToBrush(value); } }

		[XmlIgnore] [Display(Name = "Zero line", Order = 3, GroupName = "05. Brushes")]
		public Brush ZeroLineBrush { get; set; }
		[Browsable(false)] public string ZeroLineBrushSerializable { get { return Serialize.BrushToString(ZeroLineBrush); } set { ZeroLineBrush = Serialize.StringToBrush(value); } }

		[XmlIgnore] [Display(Name = "Strong buy marker", Order = 4, GroupName = "05. Brushes")]
		public Brush StrongBuyBrush { get; set; }
		[Browsable(false)] public string StrongBuyBrushSerializable { get { return Serialize.BrushToString(StrongBuyBrush); } set { StrongBuyBrush = Serialize.StringToBrush(value); } }

		[XmlIgnore] [Display(Name = "Strong sell marker", Order = 5, GroupName = "05. Brushes")]
		public Brush StrongSellBrush { get; set; }
		[Browsable(false)] public string StrongSellBrushSerializable { get { return Serialize.BrushToString(StrongSellBrush); } set { StrongSellBrush = Serialize.StringToBrush(value); } }

		[XmlIgnore] [Display(Name = "Bullish divergence", Order = 6, GroupName = "05. Brushes")]
		public Brush BullishDivBrush { get; set; }
		[Browsable(false)] public string BullishDivBrushSerializable { get { return Serialize.BrushToString(BullishDivBrush); } set { BullishDivBrush = Serialize.StringToBrush(value); } }

		[XmlIgnore] [Display(Name = "Bearish divergence", Order = 7, GroupName = "05. Brushes")]
		public Brush BearishDivBrush { get; set; }
		[Browsable(false)] public string BearishDivBrushSerializable { get { return Serialize.BrushToString(BearishDivBrush); } set { BearishDivBrush = Serialize.StringToBrush(value); } }

		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Sneaky.Sensors.SneakyBuySellVolumePressureMountain_v1_0_0[] cacheSneakyBuySellVolumePressureMountain_v1_0_0;
		public Sneaky.Sensors.SneakyBuySellVolumePressureMountain_v1_0_0 SneakyBuySellVolumePressureMountain_v1_0_0(int buyVolumeAvgLength, int sellVolumeAvgLength, bool useRealOrderFlow, double buySellDominanceRatio, double neutralZonePercent, int mountainSmoothingLength, double mountainScale, bool showStrongMarkers, bool showZeroLine, bool showDivergences, int divergenceStrength, int divergenceLookbackBars, bool showInfo, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyBuySellVolumePressureMountain_v1_0_0(Input, buyVolumeAvgLength, sellVolumeAvgLength, useRealOrderFlow, buySellDominanceRatio, neutralZonePercent, mountainSmoothingLength, mountainScale, showStrongMarkers, showZeroLine, showDivergences, divergenceStrength, divergenceLookbackBars, showInfo, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Sensors.SneakyBuySellVolumePressureMountain_v1_0_0 SneakyBuySellVolumePressureMountain_v1_0_0(ISeries<double> input, int buyVolumeAvgLength, int sellVolumeAvgLength, bool useRealOrderFlow, double buySellDominanceRatio, double neutralZonePercent, int mountainSmoothingLength, double mountainScale, bool showStrongMarkers, bool showZeroLine, bool showDivergences, int divergenceStrength, int divergenceLookbackBars, bool showInfo, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyBuySellVolumePressureMountain_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyBuySellVolumePressureMountain_v1_0_0.Length; idx++)
					if (cacheSneakyBuySellVolumePressureMountain_v1_0_0[idx] != null && cacheSneakyBuySellVolumePressureMountain_v1_0_0[idx].BuyVolumeAvgLength == buyVolumeAvgLength && cacheSneakyBuySellVolumePressureMountain_v1_0_0[idx].SellVolumeAvgLength == sellVolumeAvgLength && cacheSneakyBuySellVolumePressureMountain_v1_0_0[idx].UseRealOrderFlow == useRealOrderFlow && cacheSneakyBuySellVolumePressureMountain_v1_0_0[idx].BuySellDominanceRatio == buySellDominanceRatio && cacheSneakyBuySellVolumePressureMountain_v1_0_0[idx].NeutralZonePercent == neutralZonePercent && cacheSneakyBuySellVolumePressureMountain_v1_0_0[idx].MountainSmoothingLength == mountainSmoothingLength && cacheSneakyBuySellVolumePressureMountain_v1_0_0[idx].MountainScale == mountainScale && cacheSneakyBuySellVolumePressureMountain_v1_0_0[idx].ShowStrongMarkers == showStrongMarkers && cacheSneakyBuySellVolumePressureMountain_v1_0_0[idx].ShowZeroLine == showZeroLine && cacheSneakyBuySellVolumePressureMountain_v1_0_0[idx].ShowDivergences == showDivergences && cacheSneakyBuySellVolumePressureMountain_v1_0_0[idx].DivergenceStrength == divergenceStrength && cacheSneakyBuySellVolumePressureMountain_v1_0_0[idx].DivergenceLookbackBars == divergenceLookbackBars && cacheSneakyBuySellVolumePressureMountain_v1_0_0[idx].ShowInfo == showInfo && cacheSneakyBuySellVolumePressureMountain_v1_0_0[idx].CardCorner == cardCorner && cacheSneakyBuySellVolumePressureMountain_v1_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyBuySellVolumePressureMountain_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyBuySellVolumePressureMountain_v1_0_0[idx];
			return CacheIndicator<Sneaky.Sensors.SneakyBuySellVolumePressureMountain_v1_0_0>(new Sneaky.Sensors.SneakyBuySellVolumePressureMountain_v1_0_0(){ BuyVolumeAvgLength = buyVolumeAvgLength, SellVolumeAvgLength = sellVolumeAvgLength, UseRealOrderFlow = useRealOrderFlow, BuySellDominanceRatio = buySellDominanceRatio, NeutralZonePercent = neutralZonePercent, MountainSmoothingLength = mountainSmoothingLength, MountainScale = mountainScale, ShowStrongMarkers = showStrongMarkers, ShowZeroLine = showZeroLine, ShowDivergences = showDivergences, DivergenceStrength = divergenceStrength, DivergenceLookbackBars = divergenceLookbackBars, ShowInfo = showInfo, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyBuySellVolumePressureMountain_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Sensors.SneakyBuySellVolumePressureMountain_v1_0_0 SneakyBuySellVolumePressureMountain_v1_0_0(int buyVolumeAvgLength, int sellVolumeAvgLength, bool useRealOrderFlow, double buySellDominanceRatio, double neutralZonePercent, int mountainSmoothingLength, double mountainScale, bool showStrongMarkers, bool showZeroLine, bool showDivergences, int divergenceStrength, int divergenceLookbackBars, bool showInfo, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyBuySellVolumePressureMountain_v1_0_0(Input, buyVolumeAvgLength, sellVolumeAvgLength, useRealOrderFlow, buySellDominanceRatio, neutralZonePercent, mountainSmoothingLength, mountainScale, showStrongMarkers, showZeroLine, showDivergences, divergenceStrength, divergenceLookbackBars, showInfo, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyBuySellVolumePressureMountain_v1_0_0 SneakyBuySellVolumePressureMountain_v1_0_0(ISeries<double> input , int buyVolumeAvgLength, int sellVolumeAvgLength, bool useRealOrderFlow, double buySellDominanceRatio, double neutralZonePercent, int mountainSmoothingLength, double mountainScale, bool showStrongMarkers, bool showZeroLine, bool showDivergences, int divergenceStrength, int divergenceLookbackBars, bool showInfo, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyBuySellVolumePressureMountain_v1_0_0(input, buyVolumeAvgLength, sellVolumeAvgLength, useRealOrderFlow, buySellDominanceRatio, neutralZonePercent, mountainSmoothingLength, mountainScale, showStrongMarkers, showZeroLine, showDivergences, divergenceStrength, divergenceLookbackBars, showInfo, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Sensors.SneakyBuySellVolumePressureMountain_v1_0_0 SneakyBuySellVolumePressureMountain_v1_0_0(int buyVolumeAvgLength, int sellVolumeAvgLength, bool useRealOrderFlow, double buySellDominanceRatio, double neutralZonePercent, int mountainSmoothingLength, double mountainScale, bool showStrongMarkers, bool showZeroLine, bool showDivergences, int divergenceStrength, int divergenceLookbackBars, bool showInfo, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyBuySellVolumePressureMountain_v1_0_0(Input, buyVolumeAvgLength, sellVolumeAvgLength, useRealOrderFlow, buySellDominanceRatio, neutralZonePercent, mountainSmoothingLength, mountainScale, showStrongMarkers, showZeroLine, showDivergences, divergenceStrength, divergenceLookbackBars, showInfo, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyBuySellVolumePressureMountain_v1_0_0 SneakyBuySellVolumePressureMountain_v1_0_0(ISeries<double> input , int buyVolumeAvgLength, int sellVolumeAvgLength, bool useRealOrderFlow, double buySellDominanceRatio, double neutralZonePercent, int mountainSmoothingLength, double mountainScale, bool showStrongMarkers, bool showZeroLine, bool showDivergences, int divergenceStrength, int divergenceLookbackBars, bool showInfo, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyBuySellVolumePressureMountain_v1_0_0(input, buyVolumeAvgLength, sellVolumeAvgLength, useRealOrderFlow, buySellDominanceRatio, neutralZonePercent, mountainSmoothingLength, mountainScale, showStrongMarkers, showZeroLine, showDivergences, divergenceStrength, divergenceLookbackBars, showInfo, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
