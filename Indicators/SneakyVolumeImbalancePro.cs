// SneakyVolumeImbalancePro: NinjaTrader 8 port of the TradingView script "[blackcat] L3 Volume Imbalance Pro" (blackcat1402, MPL-2.0).
// The original needs TradingView's request.footprint(). This version builds each bar's volume-at-price rows from TRADE PRINTS
// (1-tick secondary series). History: tick rule (uptick buy / downtick sell). Live bars: quote rule (>=ask buy, <=bid sell, else tick rule).
// Imbalance = diagonal: buy volume at a row > sell volume of the row below x ratio; sell volume at a row > buy volume of the row above x ratio.
// (TradingView does not publish its exact internal rule, so this is the standard footprint definition.)
// Plots: 0 Vwap 1 Poc 2 Rsi 3 MacdHist 4 BuyCount 5 SellCount 6 StackedBuyLayers 7 StackedSellLayers 8 Score 9 AnySignal(+1/-1) 10 BuyScore 11 SellScore 12 TickData
// Written on the closed bar (Calculate = On bar close).
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
// ---- SneakyImbCore: pure logic (no NinjaTrader types) ----
internal struct ImbRow { public long Key; public double Buy, Sell; }
internal struct ImbOut
{
	public bool Valid;
	public int BuyCount, SellCount, MaxBuyStreak, MaxSellStreak;
	public double StrongBuy, StrongSell, Rsi, MacdLine, MacdSig, MacdHist, Vwap, Atr;
	public bool HasBuy, HasSell, StackedBuy, StackedSell;
	public bool StackedBuySig, StackedSellSig, BullDivConf, BearDivConf, RsiBullDiv, RsiBearDiv;
	public bool VwapBreakout, VwapBreakdown, RsiBuy, RsiSell, MacdBuy, MacdSell, BuyAbs, SellAbs;
	public int Score, BuyScore, SellScore, Any;
	public long[] BuyKeys, SellKeys; public double[] BuyStr, SellStr;
}
internal class SneakyImbCore
{
	public double ImbPct = 300, MinRowVol = 1, Ob = 70, Os = 30;
	public bool ZeroOpp = true, Faithful = false;
	public int MinStack = 3, RsiLen = 14, MacdF = 12, MacdS = 26, MacdSg = 9, PivL = 5;
	const double NaN = double.NaN;
	readonly System.Collections.Generic.List<double> cl = new System.Collections.Generic.List<double>(), rs = new System.Collections.Generic.List<double>(),
		mh = new System.Collections.Generic.List<double>(), vw = new System.Collections.Generic.List<double>(),
		pPH = new System.Collections.Generic.List<double>(), pPL = new System.Collections.Generic.List<double>(),
		rPH = new System.Collections.Generic.List<double>(), rPL = new System.Collections.Generic.List<double>(),
		mPH = new System.Collections.Generic.List<double>(), mPL = new System.Collections.Generic.List<double>();
	double prevClose = NaN, sumG, sumL, avgG = NaN, avgL = NaN; int nChg;
	double trPrevClose = NaN, atr = NaN, atrSum; int atrN;
	Ema eF, eS, eSg; double prevMacd = NaN, prevSig = NaN;
	double cumPV, cumV;
	double lastPH = NaN, lastPHr = NaN, lastPHm = NaN, lastPL = NaN, lastPLr = NaN, lastPLm = NaN;
	double prevO = NaN;

	class Ema
	{
		readonly int len; int n; double sum, val = NaN; readonly double a;
		public Ema(int l) { len = l; a = 2.0 / (l + 1); }
		public double Update(double x)
		{
			if (double.IsNaN(x)) return val;
			n++;
			if (n < len) { sum += x; val = double.NaN; }
			else if (n == len) { sum += x; val = sum / len; }
			else val = a * x + (1 - a) * val;
			return val;
		}
	}

	bool Piv(System.Collections.Generic.List<double> s, int i, bool high, out double v)
	{
		v = NaN; int L = PivL; if (i < 2 * L) return false;
		double c = s[i - L]; if (double.IsNaN(c)) return false;
		for (int j = i - 2 * L; j <= i; j++)
		{
			if (j == i - L) continue; double x = s[j]; if (double.IsNaN(x)) return false;
			if (j < i - L) { if (high ? !(c > x) : !(c < x)) return false; }
			else { if (high ? !(c >= x) : !(c <= x)) return false; }
		}
		v = c; return true;
	}

	public ImbOut AddBar(double O, double H, double L, double C, double V, ImbRow[] rows, double rowSize, bool newSession)
	{
		int i = cl.Count;
		// ---- indicators ----
		if (eF == null) { eF = new Ema(MacdF); eS = new Ema(MacdS); eSg = new Ema(MacdSg); }
		double rsi = NaN;
		if (!double.IsNaN(prevClose))
		{
			double ch = C - prevClose, g = ch > 0 ? ch : 0, l = ch < 0 ? -ch : 0; nChg++;
			if (nChg < RsiLen) { sumG += g; sumL += l; }
			else if (nChg == RsiLen) { sumG += g; sumL += l; avgG = sumG / RsiLen; avgL = sumL / RsiLen; }
			else { avgG = (avgG * (RsiLen - 1) + g) / RsiLen; avgL = (avgL * (RsiLen - 1) + l) / RsiLen; }
			if (nChg >= RsiLen) rsi = avgL == 0 ? 100 : (avgG == 0 ? 0 : 100 - 100 / (1 + avgG / avgL));
		}
		double tr = double.IsNaN(trPrevClose) ? H - L : System.Math.Max(H - L, System.Math.Max(System.Math.Abs(H - trPrevClose), System.Math.Abs(L - trPrevClose)));
		atrN++; if (atrN < 14) atrSum += tr; else if (atrN == 14) { atrSum += tr; atr = atrSum / 14; } else atr = (atr * 13 + tr) / 14;
		trPrevClose = C;
		double f = eF.Update(C), s = eS.Update(C);
		double macd = (double.IsNaN(f) || double.IsNaN(s)) ? NaN : f - s;
		double sig = eSg.Update(macd);
		double hist = (double.IsNaN(macd) || double.IsNaN(sig)) ? NaN : macd - sig;
		if (newSession || i == 0) { cumPV = 0; cumV = 0; }
		cumPV += (H + L + C) / 3.0 * V; cumV += V;
		double vwap = cumV > 0 ? cumPV / cumV : C;
		cl.Add(C); rs.Add(rsi); mh.Add(hist); vw.Add(vwap);
		// pivots
		double ph, pl, rh, rl, mhh, mll;
		bool bPH = Piv(cl, i, true, out ph), bPL = Piv(cl, i, false, out pl);
		bool bRH = Piv(rs, i, true, out rh), bRL = Piv(rs, i, false, out rl);
		bool bMH = Piv(mh, i, true, out mhh), bML = Piv(mh, i, false, out mll);
		pPH.Add(ph); pPL.Add(pl); rPH.Add(rh); rPL.Add(rl); mPH.Add(mhh); mPL.Add(mll);
		double rsiPrev = i > 0 ? rs[i - 1] : NaN, histPrev = i > 0 ? mh[i - 1] : NaN, vwPrev = i > 0 ? vw[i - 1] : NaN, clPrev = i > 0 ? cl[i - 1] : NaN;
		bool bearR, bullR, bearM, bullM;
		if (Faithful)
		{
			double pph1 = i > 0 ? pPH[i - 1] : NaN, ppl1 = i > 0 ? pPL[i - 1] : NaN;
			bearR = bPH && bRH && ph > pph1 && rsi < rsiPrev && rsi > Ob;
			bullR = bPL && bRL && pl < ppl1 && rsi > rsiPrev && rsi < Os;
			bearM = bPH && bMH && ph > pph1 && hist < histPrev;
			bullM = bPL && bML && pl < ppl1 && hist > histPrev;
		}
		else
		{
			int k = i - PivL;
			double rK = k >= 0 ? rs[k] : NaN, mK = k >= 0 ? mh[k] : NaN;
			bool hiNew = bPH && !double.IsNaN(lastPH) && ph > lastPH, loNew = bPL && !double.IsNaN(lastPL) && pl < lastPL;
			bearR = hiNew && rK < lastPHr; bearM = hiNew && mK < lastPHm;
			bullR = loNew && rK > lastPLr; bullM = loNew && mK > lastPLm;
			if (bPH) { lastPH = ph; lastPHr = rK; lastPHm = mK; }
			if (bPL) { lastPL = pl; lastPLr = rK; lastPLm = mK; }
		}
		// ---- imbalance ----
		var o = new ImbOut { Rsi = rsi, MacdLine = macd, MacdSig = sig, MacdHist = hist, Vwap = vwap, Atr = atr };
		bool valid = rows != null && rows.Length > 0; double tot = 0;
		if (valid) foreach (var r in rows) tot += r.Buy + r.Sell;
		valid = valid && tot > 0; o.Valid = valid;
		var bk = new System.Collections.Generic.List<long>(); var sk = new System.Collections.Generic.List<long>();
		var bs = new System.Collections.Generic.List<double>(); var ss = new System.Collections.Generic.List<double>();
		if (valid)
		{
			long k0 = rows[0].Key, k1 = rows[rows.Length - 1].Key; int n = (int)(k1 - k0 + 1);
			var buy = new double[n]; var sell = new double[n];
			foreach (var r in rows) { buy[r.Key - k0] += r.Buy; sell[r.Key - k0] += r.Sell; }
			double ratio = ImbPct / 100.0; int cbs = 0, css = 0;
			for (int q = 0; q < n; q++)
			{
				bool fb = false, fs = false;
				if (q > 0 && buy[q] > 0) { double sb = sell[q - 1]; fb = sb > 0 ? buy[q] > sb * ratio : (ZeroOpp && buy[q] >= MinRowVol); }
				if (q < n - 1 && sell[q] > 0) { double ba = buy[q + 1]; fs = ba > 0 ? sell[q] > ba * ratio : (ZeroOpp && sell[q] >= MinRowVol); }
				if (fb)
				{
					o.BuyCount++; bk.Add(k0 + q); cbs++; css = 0;
					double st = sell[q] > 0 ? buy[q] / sell[q] - 1 : (buy[q] > 0 ? 10 : 0); bs.Add(st); if (st > o.StrongBuy) o.StrongBuy = st;
				}
				else { if (cbs > o.MaxBuyStreak) o.MaxBuyStreak = cbs; cbs = 0; }
				if (fs)
				{
					o.SellCount++; sk.Add(k0 + q); css++; cbs = 0;
					double st = buy[q] > 0 ? sell[q] / buy[q] - 1 : (sell[q] > 0 ? 10 : 0); ss.Add(st); if (st > o.StrongSell) o.StrongSell = st;
				}
				else { if (css > o.MaxSellStreak) o.MaxSellStreak = css; css = 0; }
			}
			if (cbs > o.MaxBuyStreak) o.MaxBuyStreak = cbs;
			if (css > o.MaxSellStreak) o.MaxSellStreak = css;
		}
		o.BuyKeys = bk.ToArray(); o.SellKeys = sk.ToArray(); o.BuyStr = bs.ToArray(); o.SellStr = ss.ToArray();
		o.HasBuy = o.BuyCount > 0; o.HasSell = o.SellCount > 0;
		o.StackedBuy = o.MaxBuyStreak >= MinStack; o.StackedSell = o.MaxSellStreak >= MinStack;
		// ---- signals (all need footprint data) ----
		if (valid)
		{
			o.StackedBuySig = o.StackedBuy && C > O; o.StackedSellSig = o.StackedSell && C < O;
			o.RsiBullDiv = bullR; o.RsiBearDiv = bearR;
			o.BullDivConf = (bullR || bullM) && o.HasBuy; o.BearDivConf = (bearR || bearM) && o.HasSell;
			o.VwapBreakout = C > vwap && clPrev <= vwPrev && o.HasBuy; o.VwapBreakdown = C < vwap && clPrev >= vwPrev && o.HasSell;
			o.RsiBuy = rsi < Os && o.HasBuy && rsi > rsiPrev; o.RsiSell = rsi > Ob && o.HasSell && rsi < rsiPrev;
			o.MacdBuy = !double.IsNaN(prevMacd) && !double.IsNaN(prevSig) && macd > sig && prevMacd <= prevSig && o.HasBuy;
			o.MacdSell = !double.IsNaN(prevMacd) && !double.IsNaN(prevSig) && macd < sig && prevMacd >= prevSig && o.HasSell;
			o.BuyAbs = C < O && o.HasBuy && (C - L) > (H - C) * 2; o.SellAbs = C > O && o.HasSell && (H - C) > (C - L) * 2;
		}
		prevMacd = macd; prevSig = sig;
		o.BuyScore = (o.StackedBuySig ? 1 : 0) + (o.BullDivConf ? 1 : 0) + (o.VwapBreakout ? 1 : 0) + (o.RsiBuy ? 1 : 0) + (o.MacdBuy ? 1 : 0) + (o.BuyAbs ? 1 : 0);
		o.SellScore = (o.StackedSellSig ? 1 : 0) + (o.BearDivConf ? 1 : 0) + (o.VwapBreakdown ? 1 : 0) + (o.RsiSell ? 1 : 0) + (o.MacdSell ? 1 : 0) + (o.SellAbs ? 1 : 0);
		o.Any = o.BuyScore > 0 ? 1 : (o.SellScore > 0 ? -1 : 0);
		// composite score (as in the table)
		int sc = 0;
		sc += o.HasBuy ? 20 : 0; sc += o.StackedBuy ? 20 : 0; sc += (rsi < Os && o.HasBuy) ? 15 : 0; sc += (hist > 0 && o.HasBuy) ? 15 : 0; sc += (C > vwap && o.HasBuy) ? 15 : 0; sc += o.BuyAbs ? 15 : 0;
		sc -= o.HasSell ? 20 : 0; sc -= o.StackedSell ? 20 : 0; sc -= (rsi > Ob && o.HasSell) ? 15 : 0; sc -= (hist < 0 && o.HasSell) ? 15 : 0; sc -= (C < vwap && o.HasSell) ? 15 : 0; sc -= o.SellAbs ? 15 : 0;
		o.Score = sc;
		prevClose = C;
		return o;
	}
}


	public enum SimbTablePos { TopRight, TopLeft, BottomRight, BottomLeft }
	public enum SimbLabelMode { All, StackedDivergenceAbsorption, StackedDivergenceOnly }

	public class SneakyVolumeImbalancePro : Indicator
	{
		private struct TickRec { public DateTime Time; public double Price, Size; public int Side; }
		private class BarInfo { public ImbRow[] Rows; public long[] BuyKeys, SellKeys; public double[] BuyStr, SellStr; public int MaxBuy, MaxSell; public double Atr, StrongBuy, StrongSell; }

		private SneakyImbCore core;
		private readonly List<TickRec> pending = new List<TickRec>();
		private readonly object pendLock = new object();
		private double lastTickPrice; private int lastSide = 1; private bool haveTick;
		private ImbOut last; private bool haveLast;
		private readonly Dictionary<int, BarInfo> info = new Dictionary<int, BarInfo>();
		private double rowSize; private bool errShown;
		private int buyAboveVwap, sellBelowVwap; private double lastVwap, lastAtr;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description		= "Footprint-style volume imbalance (diagonal, stacked) from trade prints with RSI/MACD/VWAP confirmation; port of blackcat1402 L3 Volume Imbalance Pro.";
				Name			= "SneakyVolumeImbalancePro";
				Calculate		= Calculate.OnBarClose;
				IsOverlay		= true;
				DisplayInDataBox = true;
				PaintPriceMarkers = false;
				IsSuspendedWhileInactive = true;
				ArePlotsConfigurable = true;
				IsAutoScale		= false;
				DrawOnPricePanel = true;
				TicksPerRow = 10; ImbalancePct = 300; MinStackedRows = 3; MinRowVolume = 1; CountZeroOpposite = true;
				RsiLength = 14; RsiOverbought = 70; RsiOversold = 30; MacdFast = 12; MacdSlow = 26; MacdSignalLen = 9;
				UseTickData = true; QuoteRuleLive = true; FallbackEstimate = true; ReplicateQuirks = false;
				ShowHeatmap = true; ShowTower = true; ShowArrowFlow = true; ShowVwapBar = true; ShowRsiMarkers = true; ShowSignalLabels = true; ShowStatusTable = true;
				TablePosition = SimbTablePos.TopRight; HeatmapBarsBack = 0; LabelMode = SimbLabelMode.All; LabelBarsBack = 300;
				AlStacked = true; AlDiv = true; AlVwap = true; AlRsi = true; AlMacd = true; AlAbs = true; AlGeneral = false; EnableAlerts = false;
				AddPlot(new Stroke(System.Windows.Media.Brushes.Orange, 2), PlotStyle.Line, "Vwap");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Gold, 2), PlotStyle.Line, "Poc");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "Rsi");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "MacdHist");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "BuyCount");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "SellCount");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "StackedBuyLayers");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "StackedSellLayers");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "Score");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "AnySignal");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "BuyScore");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "SellScore");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "TickData");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "CoreSignal");
			}
			else if (State == State.Configure)
			{
				for (int pi = 2; pi < 14; pi++) Plots[pi].Brush = System.Windows.Media.Brushes.Transparent;
				if (UseTickData)
					AddDataSeries(BarsPeriodType.Tick, 1);
			}
			else if (State == State.DataLoaded)
			{
				core = new SneakyImbCore
				{
					ImbPct = ImbalancePct, MinRowVol = MinRowVolume, ZeroOpp = CountZeroOpposite, MinStack = MinStackedRows,
					RsiLen = RsiLength, Ob = RsiOverbought, Os = RsiOversold, MacdF = MacdFast, MacdS = MacdSlow, MacdSg = MacdSignalLen,
					Faithful = ReplicateQuirks
				};
				rowSize = TicksPerRow * TickSize;
			}
			else if (State == State.Terminated)
			{
				lock (pendLock) pending.Clear();
				info.Clear();
			}
		}

		protected override void OnBarUpdate()
		{
			try { BarUpdateInner(); }
			catch (Exception ex)
			{
				if (!errShown) { errShown = true; Print("SneakyVolumeImbalancePro OnBarUpdate error at bar " + CurrentBar + ": " + ex); }
			}
		}

		private void BarUpdateInner()
		{
			if (core == null) return;

			if (BarsInProgress == 1)
			{
				if (QuoteRuleLive && State == State.Realtime) return; // live prints come from OnMarketData (quote rule)
				double p = Closes[1][0], sz = Volumes[1][0];
				if (sz <= 0) return;
				int side = lastSide;
				if (haveTick) { if (p > lastTickPrice) side = 1; else if (p < lastTickPrice) side = -1; }
				lastTickPrice = p; haveTick = true; lastSide = side;
				lock (pendLock) pending.Add(new TickRec { Time = Times[1][0], Price = p, Size = sz, Side = side });
				return;
			}
			if (BarsInProgress != 0) return;

			DateTime barEnd = Time[0];
			int take = 0; TickRec[] taken;
			lock (pendLock)
			{
				while (take < pending.Count && pending[take].Time < barEnd) take++;
				taken = pending.GetRange(0, take).ToArray();
				pending.RemoveRange(0, take);
			}
			ImbRow[] rows; bool fromTicks = take > 0;
			if (fromTicks)
			{
				var d = new SortedDictionary<long, double[]>();
				for (int k = 0; k < take; k++)
				{
					TickRec t = taken[k];
					long key = (long)Math.Floor(t.Price / rowSize + 1e-9);
					double[] v; if (!d.TryGetValue(key, out v)) { v = new double[2]; d[key] = v; }
					v[t.Side > 0 ? 0 : 1] += t.Size;
				}
				rows = new ImbRow[d.Count]; int n = 0;
				foreach (var kv in d) { rows[n].Key = kv.Key; rows[n].Buy = kv.Value[0]; rows[n].Sell = kv.Value[1]; n++; }
			}
			else if (FallbackEstimate && Volume[0] > 0)
				rows = EstimateRows(Open[0], High[0], Low[0], Close[0], Volume[0]);
			else
				rows = new ImbRow[0];

			double vol = 0; for (int k = 0; k < rows.Length; k++) vol += rows[k].Buy + rows[k].Sell;
			ImbOut r = core.AddBar(Open[0], High[0], Low[0], Close[0], vol > 0 ? vol : Volume[0], rows, rowSize, Bars.IsFirstBarOfSession);
			last = r; haveLast = true;
			double atrv = double.IsNaN(r.Atr) ? Math.Max(High[0] - Low[0], TickSize) : r.Atr;
			info[CurrentBar] = new BarInfo { Rows = rows, BuyKeys = r.BuyKeys, SellKeys = r.SellKeys, BuyStr = r.BuyStr, SellStr = r.SellStr, MaxBuy = r.MaxBuyStreak, MaxSell = r.MaxSellStreak, Atr = atrv, StrongBuy = r.StrongBuy, StrongSell = r.StrongSell };
			info.Remove(CurrentBar - 800);

			Values[0][0] = r.Vwap;
			Values[1][0] = PocPrice(rows);
			Values[2][0] = r.Rsi;
			Values[3][0] = r.MacdHist;
			Values[4][0] = r.BuyCount;
			Values[5][0] = r.SellCount;
			Values[6][0] = r.MaxBuyStreak;
			Values[7][0] = r.MaxSellStreak;
			Values[8][0] = r.Score;
			Values[9][0] = r.Any;
			Values[10][0] = r.BuyScore;
			Values[11][0] = r.SellScore;
			Values[12][0] = fromTicks ? 1 : 0;
			Values[13][0] = (r.StackedBuySig || r.BullDivConf) ? 1 : ((r.StackedSellSig || r.BearDivConf) ? -1 : 0);

			bool lastBar = CurrentBar == Count - 1;
			if (lastBar)
			{
				buyAboveVwap = 0; sellBelowVwap = 0; lastVwap = r.Vwap; lastAtr = atrv;
				foreach (long k in r.BuyKeys) if ((k + 1) * rowSize > r.Vwap) buyAboveVwap++;
				foreach (long k in r.SellKeys) if ((k + 1) * rowSize < r.Vwap) sellBelowVwap++;
				if (ShowArrowFlow) DrawFlow(r, atrv); else { foreach (string t in new[] { "SimbFlowL1", "SimbFlowL2", "SimbFlowT1", "SimbFlowT2" }) RemoveDrawObject(t); }
			}
			if (CurrentBar >= Count - 1 - LabelBarsBack)
			{
				if (ShowSignalLabels) DrawSignals(r, atrv);
				if (ShowRsiMarkers && r.Valid)
				{
					if (r.Rsi < RsiOversold && r.HasBuy) Draw.TriangleUp(this, "SimbRB" + CurrentBar, false, 0, Low[0] - TickSize * 3, System.Windows.Media.Brushes.LimeGreen);
					if (r.Rsi > RsiOverbought && r.HasSell) Draw.TriangleDown(this, "SimbRS" + CurrentBar, false, 0, High[0] + TickSize * 3, System.Windows.Media.Brushes.Red);
				}
			}
			if (EnableAlerts && State == State.Realtime) RaiseAlerts(r);
		}

		private double PocPrice(ImbRow[] rows)
		{
			if (rows == null || rows.Length == 0) return double.NaN;
			int bi = 0; double best = -1;
			for (int k = 0; k < rows.Length; k++) { double t = rows[k].Buy + rows[k].Sell; if (t > best) { best = t; bi = k; } }
			return (rows[bi].Key + 1) * rowSize;
		}

		// Live prints: Lee & Ready quote rule (>= ask buy, <= bid sell, in between -> tick rule).
		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (!QuoteRuleLive || !UseTickData || State != State.Realtime || e.MarketDataType != MarketDataType.Last || e.Volume <= 0) return;
			lock (pendLock)
			{
				int side = lastSide;
				if (e.Ask > 0 && e.Price >= e.Ask) side = 1;
				else if (e.Bid > 0 && e.Price <= e.Bid) side = -1;
				else if (haveTick) { if (e.Price > lastTickPrice) side = 1; else if (e.Price < lastTickPrice) side = -1; }
				lastTickPrice = e.Price; haveTick = true; lastSide = side;
				pending.Add(new TickRec { Time = e.Time, Price = e.Price, Size = e.Volume, Side = side });
			}
		}

		private ImbRow[] EstimateRows(double o, double h, double l, double c, double vol)
		{
			long k0 = (long)Math.Floor(l / rowSize + 1e-9), k1 = (long)Math.Floor(h / rowSize + 1e-9);
			int n = (int)(k1 - k0 + 1); if (n < 1) n = 1;
			double rr = h > l ? (c - o) / (h - l) : 0; if (rr > 1) rr = 1; if (rr < -1) rr = -1;
			double buyShare = (1 + rr) / 2.0;
			var rows = new ImbRow[n];
			for (int k = 0; k < n; k++) { double v = vol / n; rows[k].Key = k0 + k; rows[k].Buy = v * buyShare; rows[k].Sell = v * (1 - buyShare); }
			return rows;
		}

		private void DrawFlow(ImbOut r, double a)
		{
			foreach (string t in new[] { "SimbFlowL1", "SimbFlowL2", "SimbFlowT1", "SimbFlowT2" }) RemoveDrawObject(t);
			if (!r.Valid) return;
			if (r.HasBuy) FlowArrow("1", Close[0] + a * (r.HasSell ? 0.15 : 0.2), true);
			if (r.HasSell) FlowArrow("2", Close[0] - a * (r.HasBuy ? 0.15 : 0.2), false);
		}

		private void FlowArrow(string id, double tip, bool up)
		{
			System.Windows.Media.Brush b = up ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Red;
			Draw.Line(this, "SimbFlowL" + id, false, 0, Close[0], 0, tip, b, DashStyleHelper.Solid, 3);
			if (up) Draw.TriangleUp(this, "SimbFlowT" + id, false, 0, tip, b); else Draw.TriangleDown(this, "SimbFlowT" + id, false, 0, tip, b);
		}

		private void Lbl(string tag, string text, double y, System.Windows.Media.Brush color)
		{
			Draw.Text(this, tag + CurrentBar, false, text, 0, y, 0, color, new SimpleFont("Arial", 9),
				System.Windows.TextAlignment.Center, System.Windows.Media.Brushes.Transparent, color, 18);
		}

		private void DrawSignals(ImbOut r, double a)
		{
			if (!r.Valid) return;
			bool all = LabelMode == SimbLabelMode.All, abs = LabelMode != SimbLabelMode.StackedDivergenceOnly;
			if (r.StackedBuySig)  Lbl("SimbSB", "Stacked Buy Imbalance\n" + r.MaxBuyStreak + " Layers", Low[0] - a * 0.3, System.Windows.Media.Brushes.LimeGreen);
			if (r.BullDivConf)    Lbl("SimbBD", "RSI+Imbalance Bull Div\nReversal", Low[0] - a * 0.4, System.Windows.Media.Brushes.DodgerBlue);
			if (all && r.VwapBreakout)   Lbl("SimbVB", "VWAP Breakout+Imb\nInstitutional Confirmed", Low[0] - a * 0.25, System.Windows.Media.Brushes.MediumAquamarine);
			if (all && r.RsiBuy)         Lbl("SimbRB", "RSI Oversold+Imb\nBounce Warning", Low[0] - a * 0.35, System.Windows.Media.Brushes.Cyan);
			if (all && r.MacdBuy)        Lbl("SimbMB", "MACD Golden+Imb\nTrend Starting", Low[0] - a * 0.2, System.Windows.Media.Brushes.LimeGreen);
			if (abs && r.BuyAbs)         Lbl("SimbAB", "Buy Absorption\nAccumulation", Low[0] - a * 0.45, System.Windows.Media.Brushes.White);
			if (r.StackedSellSig) Lbl("SimbSS", "Stacked Sell Imbalance\n" + r.MaxSellStreak + " Layers", High[0] + a * 0.3, System.Windows.Media.Brushes.Red);
			if (r.BearDivConf)    Lbl("SimbSD", "RSI+Imbalance Bear Div\nReversal", High[0] + a * 0.4, System.Windows.Media.Brushes.MediumPurple);
			if (all && r.VwapBreakdown)  Lbl("SimbVD", "VWAP Breakdown+Imb\nInstitutional Confirmed", High[0] + a * 0.25, System.Windows.Media.Brushes.Orange);
			if (all && r.RsiSell)        Lbl("SimbRS", "RSI Overbought+Imb\nPullback Warning", High[0] + a * 0.35, System.Windows.Media.Brushes.HotPink);
			if (all && r.MacdSell)       Lbl("SimbMS", "MACD Death+Imb\nTrend Reversal", High[0] + a * 0.2, System.Windows.Media.Brushes.Red);
			if (abs && r.SellAbs)        Lbl("SimbAS", "Sell Absorption\nDistribution", High[0] + a * 0.45, System.Windows.Media.Brushes.White);
		}

		private void RaiseAlerts(ImbOut r)
		{
			string snd = NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav"; string t = CurrentBar.ToString();
			Action<string, string> al = (id, msg) => Alert("simb" + id + t, Priority.Medium, msg, snd, 10, System.Windows.Media.Brushes.Black, System.Windows.Media.Brushes.DodgerBlue);
			if (AlStacked && r.StackedBuySig) al("SB", "Stacked buy imbalances");
			if (AlStacked && r.StackedSellSig) al("SS", "Stacked sell imbalances");
			if (AlDiv && r.BullDivConf) al("BD", "Bullish divergence + buy imbalance");
			if (AlDiv && r.BearDivConf) al("SD", "Bearish divergence + sell imbalance");
			if (AlVwap && r.VwapBreakout) al("VB", "VWAP breakout with buy imbalance");
			if (AlVwap && r.VwapBreakdown) al("VD", "VWAP breakdown with sell imbalance");
			if (AlRsi && r.RsiBuy) al("RB", "RSI oversold + buy imbalance");
			if (AlRsi && r.RsiSell) al("RS", "RSI overbought + sell imbalance");
			if (AlMacd && r.MacdBuy) al("MB", "MACD golden cross + buy imbalance");
			if (AlMacd && r.MacdSell) al("MS", "MACD death cross + sell imbalance");
			if (AlAbs && r.BuyAbs) al("AB", "Buy absorption");
			if (AlAbs && r.SellAbs) al("AS", "Sell absorption");
			if (AlGeneral && r.HasBuy && !r.HasSell) al("GB", "Unidirectional buy imbalance");
			if (AlGeneral && r.HasSell && !r.HasBuy) al("GS", "Unidirectional sell imbalance");
			if (AlGeneral && r.HasBuy && r.HasSell) al("GC", "Buy/Sell imbalance conflict");
		}

		#region Rendering

		private static SharpDX.Color4 C4(byte r, byte g, byte b, float a) { return new SharpDX.Color4(r / 255f, g / 255f, b / 255f, a); }

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (Bars == null || ChartBars == null || RenderTarget == null || core == null) return;
			try { RenderInner(chartControl, chartScale); }
			catch (Exception ex) { if (!errShown) { errShown = true; Print("SneakyVolumeImbalancePro OnRender error: " + ex); } }
		}

		private void RenderInner(ChartControl chartControl, ChartScale chartScale)
		{
			int from = ChartBars.FromIndex, to = ChartBars.ToIndex;
			if (to < 0) return;
			var disp = new List<IDisposable>();
			try
			{
				for (int i = from; i <= to; i++)
				{
					BarInfo bi; if (!info.TryGetValue(i, out bi)) continue;
					float x = chartControl.GetXByBarIndex(ChartBars, i), x2 = chartControl.GetXByBarIndex(ChartBars, i + 1);
					float slot = Math.Max(2f, Math.Abs(x2 - x)), half = slot / 2f - 1f;
					// heatmap cloud: one box per imbalance row (last HeatBars bars)
					if (ShowHeatmap && (HeatmapBarsBack <= 0 || i > Count - 1 - HeatmapBarsBack))
					{
						DrawRows(bi.BuyKeys, bi.StrongBuy, C4(0, 255, 65, 1f), x, half, chartScale, disp);
						DrawRows(bi.SellKeys, bi.StrongSell, C4(255, 0, 64, 1f), x, half, chartScale, disp);
					}
					// stacked towers
					if (ShowTower)
					{
						if (bi.MaxBuy >= MinStackedRows)
						{
							float y0 = chartScale.GetYByValue(Bars.GetLow(i) - bi.Atr * 0.5);
							for (int k = 0; k < bi.MaxBuy; k++) Tri(x, y0 - k * 7f, true, C4(0, 255, 65, Math.Max(0.35f, 0.9f - k * 0.08f)), chartScale, disp);
						}
						if (bi.MaxSell >= MinStackedRows)
						{
							float y0 = chartScale.GetYByValue(Bars.GetHigh(i) + bi.Atr * 0.5);
							for (int k = 0; k < bi.MaxSell; k++) Tri(x, y0 + k * 7f, false, C4(255, 0, 64, Math.Max(0.35f, 0.9f - k * 0.08f)), chartScale, disp);
						}
					}
				}
				if (ShowVwapBar && haveLast && Count > 0) DrawVwapBar(chartControl, chartScale, disp);
				if (ShowStatusTable && haveLast) DrawTable(chartControl, disp);
			}
			finally { foreach (var d in disp) d.Dispose(); }
		}

		private void DrawRows(long[] keys, double strongest, SharpDX.Color4 col, float x, float half, ChartScale cs, List<IDisposable> disp)
		{
			if (keys == null || keys.Length == 0) return;
			float alpha = (float)(0.28 + Math.Min(strongest, 5.0) * 0.09);
			var br = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(col.Red, col.Green, col.Blue, alpha)); disp.Add(br);
			foreach (long k in keys)
			{
				float yt = cs.GetYByValue((k + 1) * rowSize), yb = cs.GetYByValue(k * rowSize);
				var rc = new SharpDX.RectangleF(x - half, Math.Min(yt, yb), half * 2f, Math.Max(1f, Math.Abs(yb - yt)));
				RenderTarget.FillRectangle(rc, br); RenderTarget.DrawRectangle(rc, br, 1f);
			}
		}

		private void Tri(float x, float y, bool up, SharpDX.Color4 col, ChartScale cs, List<IDisposable> disp)
		{
			var br = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, col); disp.Add(br);
			var g = new SharpDX.Direct2D1.PathGeometry(RenderTarget.Factory); disp.Add(g);
			var sink = g.Open(); const float s = 4f;
			if (up) { sink.BeginFigure(new SharpDX.Vector2(x, y - s), FigureBegin.Filled); sink.AddLine(new SharpDX.Vector2(x + s, y + s)); sink.AddLine(new SharpDX.Vector2(x - s, y + s)); }
			else { sink.BeginFigure(new SharpDX.Vector2(x, y + s), FigureBegin.Filled); sink.AddLine(new SharpDX.Vector2(x + s, y - s)); sink.AddLine(new SharpDX.Vector2(x - s, y - s)); }
			sink.EndFigure(FigureEnd.Closed); sink.Close(); sink.Dispose();
			RenderTarget.FillGeometry(g, br);
		}

		private void DrawVwapBar(ChartControl cc, ChartScale cs, List<IDisposable> disp)
		{
			if (double.IsNaN(lastVwap) || lastAtr <= 0) return;
			int li = Count - 1;
			float x = cc.GetXByBarIndex(ChartBars, li) + 2 * Math.Max(4f, Math.Abs(cc.GetXByBarIndex(ChartBars, li + 1) - cc.GetXByBarIndex(ChartBars, li)));
			float yTop = cs.GetYByValue(lastVwap + lastAtr * 0.3), yBot = cs.GetYByValue(lastVwap - lastAtr * 0.3), yMid = cs.GetYByValue(lastVwap);
			var gray = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, C4(140, 140, 140, 0.35f)); disp.Add(gray);
			RenderTarget.FillRectangle(new SharpDX.RectangleF(x - 5f, yTop, 10f, Math.Max(2f, yBot - yTop)), gray);
			float fa = (float)Math.Min(buyAboveVwap / 5.0, 1.0), fb = (float)Math.Min(sellBelowVwap / 5.0, 1.0);
			var g = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, C4(0, 255, 65, 0.6f)); disp.Add(g);
			var r = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, C4(255, 0, 64, 0.6f)); disp.Add(r);
			if (fa > 0) RenderTarget.FillRectangle(new SharpDX.RectangleF(x - 5f, yMid - (yMid - yTop) * fa, 10f, (yMid - yTop) * fa), g);
			if (fb > 0) RenderTarget.FillRectangle(new SharpDX.RectangleF(x - 5f, yMid, 10f, (yBot - yMid) * fb), r);
			var wb = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, C4(255, 255, 255, 1f)); disp.Add(wb);
			var fmt = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial", 9f); disp.Add(fmt);
			RenderTarget.DrawText("VWAP\nValidation", fmt, new SharpDX.RectangleF(x + 8f, yMid - 12f, 70f, 28f), wb);
		}

		private void DrawTable(ChartControl cc, List<IDisposable> disp)
		{
			ImbOut r = last; const int R = 12, Cn = 4;
			string[,] t = new string[R, Cn]; SharpDX.Color4[,] col = new SharpDX.Color4[R, Cn];
			SharpDX.Color4 cyan = C4(0, 255, 255, 1), gray = C4(150, 150, 150, 1), green = C4(0, 255, 65, 1), red = C4(255, 0, 64, 1), orange = C4(255, 107, 0, 1), yellow = C4(255, 255, 0, 1), white = C4(255, 255, 255, 1);
			string[] hd = { "IMBALANCE", "CURRENT", "STATUS", "SIGNAL" }; for (int c = 0; c < Cn; c++) { t[0, c] = hd[c]; col[0, c] = cyan; }
			double close = Close.Count > 0 ? Close[0] : 0;
			for (int i = 1; i < R; i++) col[i, 0] = gray;
			// 1 buy
			t[1, 0] = "Buy Imbalance"; t[1, 1] = r.BuyCount + " count"; col[1, 1] = r.BuyCount > 0 ? green : gray;
			t[1, 2] = r.StackedBuy ? "Stacked x" + r.MaxBuyStreak : (r.BuyCount > 0 ? "Active" : "None"); col[1, 2] = r.StackedBuy ? orange : (r.BuyCount > 0 ? green : gray);
			t[1, 3] = r.BuyCount > 0 ? "Long Pressure" : "No Pressure"; col[1, 3] = r.BuyCount > 0 ? green : gray;
			// 2 sell
			t[2, 0] = "Sell Imbalance"; t[2, 1] = r.SellCount + " count"; col[2, 1] = r.SellCount > 0 ? red : gray;
			t[2, 2] = r.StackedSell ? "Stacked x" + r.MaxSellStreak : (r.SellCount > 0 ? "Active" : "None"); col[2, 2] = r.StackedSell ? orange : (r.SellCount > 0 ? red : gray);
			t[2, 3] = r.SellCount > 0 ? "Short Pressure" : "No Pressure"; col[2, 3] = r.SellCount > 0 ? red : gray;
			// 3 strength
			t[3, 0] = "Imbalance Strength"; t[3, 1] = "B:" + r.StrongBuy.ToString("0.#") + " S:" + r.StrongSell.ToString("0.#"); col[3, 1] = r.StrongBuy > r.StrongSell ? green : red;
			t[3, 2] = r.StrongBuy > r.StrongSell ? "Buy Dominant" : (r.StrongSell > r.StrongBuy ? "Sell Dominant" : "Balanced"); col[3, 2] = r.StrongBuy > r.StrongSell ? green : (r.StrongSell > r.StrongBuy ? red : gray);
			bool ext = Math.Abs(r.StrongBuy - r.StrongSell) > 2; t[3, 3] = ext ? "Extreme" : "Normal"; col[3, 3] = ext ? orange : gray;
			// 4 RSI
			double rsi = r.Rsi; bool rn = double.IsNaN(rsi);
			t[4, 0] = "RSI"; t[4, 1] = rn ? "N/A" : rsi.ToString("0.#"); col[4, 1] = rn ? gray : (rsi > RsiOverbought ? red : (rsi < RsiOversold ? green : white));
			t[4, 2] = rn ? "N/A" : (rsi > RsiOverbought ? "Overbought" : (rsi < RsiOversold ? "Oversold" : "Neutral")); col[4, 2] = rn ? gray : (rsi > RsiOverbought ? red : (rsi < RsiOversold ? green : gray));
			bool rb = !rn && rsi < RsiOversold && r.HasBuy, rsl = !rn && rsi > RsiOverbought && r.HasSell;
			t[4, 3] = rb ? "Buy" : (rsl ? "Sell" : "-"); col[4, 3] = rb ? green : (rsl ? red : gray);
			// 5 MACD
			double mh = r.MacdHist; bool mn = double.IsNaN(mh);
			t[5, 0] = "MACD"; t[5, 1] = mn ? "N/A" : mh.ToString("0.##"); col[5, 1] = mh > 0 ? green : red;
			t[5, 2] = mn ? "N/A" : (mh > 0 ? "Bullish" : "Bearish"); col[5, 2] = mh > 0 ? green : red;
			bool al = (mh > 0 && r.HasBuy) || (mh < 0 && r.HasSell); t[5, 3] = al ? "Align" : "Conflict"; col[5, 3] = al ? green : orange;
			// 6 VWAP
			double vd = r.Vwap > 0 ? (close - r.Vwap) / r.Vwap * 100.0 : 0; bool near = Math.Abs(vd) < 0.5;
			t[6, 0] = "VWAP Position"; t[6, 1] = vd.ToString("0.##") + "%"; col[6, 1] = close > r.Vwap ? green : red;
			t[6, 2] = near ? "Near" : (close > r.Vwap ? "Above" : "Below"); col[6, 2] = near ? orange : (close > r.Vwap ? green : red);
			bool vbo = close > r.Vwap && r.HasBuy, vbd = close < r.Vwap && r.HasSell;
			t[6, 3] = vbo ? "Breakout" : (vbd ? "Breakdown" : "-"); col[6, 3] = vbo ? green : (vbd ? red : gray);
			// 7 divergence
			int dc = (r.BullDivConf ? 1 : 0) + (r.BearDivConf ? 1 : 0) + (r.RsiBullDiv && !r.BullDivConf ? 1 : 0) + (r.RsiBearDiv && !r.BearDivConf ? 1 : 0);
			t[7, 0] = "Divergence"; t[7, 1] = dc + " detected"; col[7, 1] = dc > 0 ? orange : gray;
			bool dcf = r.BullDivConf || r.BearDivConf; t[7, 2] = dcf ? "Confirmed" : (dc > 0 ? "Pending" : "None"); col[7, 2] = dcf ? green : (dc > 0 ? orange : gray);
			t[7, 3] = r.BearDivConf ? "Bear Confirmed" : (r.BullDivConf ? "Bull Confirmed" : (r.RsiBearDiv ? "RSI Bear Div" : (r.RsiBullDiv ? "RSI Bull Div" : "None")));
			col[7, 3] = r.BearDivConf ? red : (r.BullDivConf ? green : (dc > 0 ? orange : gray));
			// 8 absorption
			t[8, 0] = "Absorption"; t[8, 1] = r.BuyAbs ? "Buy Absorb" : (r.SellAbs ? "Sell Absorb" : "None"); col[8, 1] = r.BuyAbs ? green : (r.SellAbs ? red : gray);
			t[8, 2] = r.BuyAbs ? "Accum" : (r.SellAbs ? "Dist" : "-"); col[8, 2] = col[8, 1];
			t[8, 3] = (r.BuyAbs || r.SellAbs) ? "High Val" : "Wait"; col[8, 3] = (r.BuyAbs || r.SellAbs) ? orange : gray;
			// 9 trend
			t[9, 0] = "Trend Direction"; t[9, 1] = r.StackedBuy ? "Strong Bull" : (r.StackedSell ? "Strong Bear" : (r.HasBuy ? "Bullish" : (r.HasSell ? "Bearish" : "Neutral")));
			col[9, 1] = (r.StackedBuy || (r.HasBuy && !r.StackedSell)) ? green : ((r.StackedSell || r.HasSell) ? red : gray);
			t[9, 2] = mh > 0 ? "MACD up" : "MACD down"; col[9, 2] = mh > 0 ? green : red;
			t[9, 3] = al ? "Align" : "Conflict"; col[9, 3] = al ? green : red;
			// 10 score
			int sc = r.Score, asc = Math.Abs(sc);
			t[10, 0] = "Strength Score"; t[10, 1] = asc.ToString(); col[10, 1] = sc > 0 ? green : (sc < 0 ? red : gray);
			t[10, 2] = sc > 60 ? "Extreme" : (sc > 30 ? "Strong" : (sc > 0 ? "Bullish" : (sc < -60 ? "Extreme" : (sc < -30 ? "Weak" : (sc < 0 ? "Bearish" : "Neutral")))));
			col[10, 2] = sc > 0 ? green : (sc < 0 ? red : gray);
			t[10, 3] = asc > 50 ? "Act Now" : (asc > 25 ? "Watch" : "Wait"); col[10, 3] = asc > 50 ? orange : (asc > 25 ? yellow : gray);
			// 11 recommendation (merged)
			string rec = "Wait for Signal"; SharpDX.Color4 rc = gray;
			if (sc > 80) { rec = "STRONG BUY (Multi-Confirmed)"; rc = green; }
			else if (sc < -80) { rec = "STRONG SELL (Multi-Confirmed)"; rc = red; }
			else if (sc > 50) { rec = "ACTIVE BUY (Trend Confirmed)"; rc = green; }
			else if (sc < -50) { rec = "ACTIVE SELL (Trend Confirmed)"; rc = red; }
			else if (r.BullDivConf) { rec = "BUY OPPORTUNITY (Divergence)"; rc = C4(0, 128, 255, 1); }
			else if (r.BearDivConf) { rec = "SELL OPPORTUNITY (Divergence)"; rc = C4(157, 0, 255, 1); }
			else if (r.BuyAbs) { rec = "ACCUMULATION BUY (Absorption)"; rc = C4(0, 255, 170, 1); }
			else if (r.SellAbs) { rec = "DISTRIBUTION SELL (Absorption)"; rc = orange; }
			t[11, 0] = rec; col[11, 0] = rc;

			float[] cw = { 135f, 105f, 100f, 105f }; float rh = 18f, tw = cw[0] + cw[1] + cw[2] + cw[3], th = rh * R;
			float pw = (float)ChartPanel.W, ph = (float)ChartPanel.H;
			bool right = TablePosition == SimbTablePos.TopRight || TablePosition == SimbTablePos.BottomRight, top = TablePosition == SimbTablePos.TopRight || TablePosition == SimbTablePos.TopLeft;
			float x0 = right ? pw - tw - 10f : 10f, y0 = top ? 10f : ph - th - 10f;
			var bg = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, C4(10, 10, 21, 0.85f)); disp.Add(bg);
			var fr = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, C4(0, 255, 255, 1f)); disp.Add(fr);
			RenderTarget.FillRectangle(new SharpDX.RectangleF(x0, y0, tw, th), bg);
			RenderTarget.DrawRectangle(new SharpDX.RectangleF(x0, y0, tw, th), fr, 1.5f);
			var fmt = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial", 11f); disp.Add(fmt);
			for (int i = 0; i < R; i++)
			{
				float cx = x0;
				for (int c = 0; c < Cn; c++)
				{
					if (i == 11 && c > 0) break;
					var tb = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, col[i, c]); disp.Add(tb);
					float w = (i == 11) ? tw : cw[c];
					RenderTarget.DrawText(t[i, c] ?? "", fmt, new SharpDX.RectangleF(cx + 5f, y0 + i * rh + 2f, w - 8f, rh), tb);
					cx += cw[c];
				}
			}
		}

		#endregion

		#region Properties

		[Display(Name = "Ticks Per Row", Description = "Price rows are this many ticks tall (original default 10).", Order = 0, GroupName = "01. Footprint")]
		[Range(1, 200)]
		public int TicksPerRow { get; set; }
		[Display(Name = "Imbalance Threshold (%)", Description = "300 = 3x. Diagonal comparison.", Order = 1, GroupName = "01. Footprint")]
		[Range(150, 1000)]
		public double ImbalancePct { get; set; }
		[Display(Name = "Min Stacked Imbalance Rows", Order = 2, GroupName = "01. Footprint")]
		[Range(2, 10)]
		public int MinStackedRows { get; set; }
		[Display(Name = "Count Row With Zero Opposite Volume", Description = "If the diagonal opposite row has zero volume, count it as an imbalance when volume >= min below.", Order = 3, GroupName = "01. Footprint")]
		public bool CountZeroOpposite { get; set; }
		[Display(Name = "Min Row Volume (zero-opposite case)", Order = 4, GroupName = "01. Footprint")]
		[Range(1, 10000)]
		public double MinRowVolume { get; set; }
		[Display(Name = "Use Trade Prints (1-tick series)", Description = "Builds rows from tick history/live ticks. Needs tick data for the chart's date range.", Order = 5, GroupName = "01. Footprint")]
		public bool UseTickData { get; set; }
		[Display(Name = "Quote Rule On Live Bars", Description = "Realtime prints use bid/ask; history uses the tick rule, so live and historical bars differ slightly.", Order = 6, GroupName = "01. Footprint")]
		public bool QuoteRuleLive { get; set; }
		[Display(Name = "Fallback: Estimate From Bar When No Ticks", Description = "Crude: volume spread evenly over the bar range. Imbalances from it are not meaningful.", Order = 7, GroupName = "01. Footprint")]
		public bool FallbackEstimate { get; set; }
		[Display(Name = "Replicate Original Script Quirks", Description = "The Pine divergence check compares pivots only to the previous bar, so it almost never fires. Off = classic divergence vs previous pivot.", Order = 8, GroupName = "01. Footprint")]
		public bool ReplicateQuirks { get; set; }

		[Display(Name = "RSI Period", Order = 0, GroupName = "02. RSI / MACD")] [Range(5, 50)] public int RsiLength { get; set; }
		[Display(Name = "RSI Overbought", Order = 1, GroupName = "02. RSI / MACD")] [Range(55, 95)] public int RsiOverbought { get; set; }
		[Display(Name = "RSI Oversold", Order = 2, GroupName = "02. RSI / MACD")] [Range(5, 45)] public int RsiOversold { get; set; }
		[Display(Name = "MACD Fast", Order = 3, GroupName = "02. RSI / MACD")] [Range(2, 50)] public int MacdFast { get; set; }
		[Display(Name = "MACD Slow", Order = 4, GroupName = "02. RSI / MACD")] [Range(3, 100)] public int MacdSlow { get; set; }
		[Display(Name = "MACD Signal", Order = 5, GroupName = "02. RSI / MACD")] [Range(2, 50)] public int MacdSignalLen { get; set; }

		[Display(Name = "Show Imbalance Heatmap", Order = 0, GroupName = "03. Display")] public bool ShowHeatmap { get; set; }
		[Display(Name = "Show Stacked Towers", Order = 1, GroupName = "03. Display")] public bool ShowTower { get; set; }
		[Display(Name = "Show Arrow Flow (last bar)", Order = 2, GroupName = "03. Display")] public bool ShowArrowFlow { get; set; }
		[Display(Name = "Show VWAP Validation Bar", Order = 3, GroupName = "03. Display")] public bool ShowVwapBar { get; set; }
		[Display(Name = "Show RSI Extreme Markers", Order = 4, GroupName = "03. Display")] public bool ShowRsiMarkers { get; set; }
		[Display(Name = "Show Signal Labels", Order = 5, GroupName = "03. Display")] public bool ShowSignalLabels { get; set; }
		[Display(Name = "Show Status Table", Order = 6, GroupName = "03. Display")] public bool ShowStatusTable { get; set; }
		[Display(Name = "Table Position", Order = 7, GroupName = "03. Display")] public SimbTablePos TablePosition { get; set; }
		[Display(Name = "Heatmap: Last N Bars (0 = whole chart, max 800 bars kept)", Order = 8, GroupName = "03. Display")] [Range(0, 800)] public int HeatmapBarsBack { get; set; }
		[Display(Name = "Label Filter", Description = "All = every signal; the other modes hide the frequent MACD/VWAP/RSI labels (and optionally absorption).", Order = 10, GroupName = "03. Display")] public SimbLabelMode LabelMode { get; set; }
		[Display(Name = "Draw Labels On Last N Bars", Order = 9, GroupName = "03. Display")] [Range(10, 5000)] public int LabelBarsBack { get; set; }

		[Display(Name = "Alerts On Signals (realtime)", Order = 0, GroupName = "04. Alerts")] public bool EnableAlerts { get; set; }
		[Display(Name = "Stacked Imbalance", Order = 1, GroupName = "04. Alerts")] public bool AlStacked { get; set; }
		[Display(Name = "Divergence", Order = 2, GroupName = "04. Alerts")] public bool AlDiv { get; set; }
		[Display(Name = "VWAP", Order = 3, GroupName = "04. Alerts")] public bool AlVwap { get; set; }
		[Display(Name = "RSI Extreme", Order = 4, GroupName = "04. Alerts")] public bool AlRsi { get; set; }
		[Display(Name = "MACD Cross", Order = 5, GroupName = "04. Alerts")] public bool AlMacd { get; set; }
		[Display(Name = "Absorption", Order = 6, GroupName = "04. Alerts")] public bool AlAbs { get; set; }
		[Display(Name = "General Imbalance", Order = 7, GroupName = "04. Alerts")] public bool AlGeneral { get; set; }

		#endregion

		#region Plot series (for automation tools)

		[Browsable(false)] [XmlIgnore] public Series<double> Vwap { get { Update(); return Values[0]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> Poc { get { Update(); return Values[1]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> Rsi { get { Update(); return Values[2]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> MacdHist { get { Update(); return Values[3]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> BuyCount { get { Update(); return Values[4]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> SellCount { get { Update(); return Values[5]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> StackedBuyLayers { get { Update(); return Values[6]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> StackedSellLayers { get { Update(); return Values[7]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> Score { get { Update(); return Values[8]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> AnySignal { get { Update(); return Values[9]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> BuyScore { get { Update(); return Values[10]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> SellScore { get { Update(); return Values[11]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> TickData { get { Update(); return Values[12]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> CoreSignal { get { Update(); return Values[13]; } }

		#endregion
	}
}
