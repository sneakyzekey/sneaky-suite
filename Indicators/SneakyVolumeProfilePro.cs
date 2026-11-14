// SneakyVolumeProfilePro: NinjaTrader 8 port of the TradingView script "[blackcat] L3 Volume Profile Pro" (blackcat1402, MPL-2.0).
// The original needs TradingView's request.footprint(). This version builds each bar's volume-at-price rows from
// TRADE PRINTS (a 1-tick secondary series) and classifies them with the tick rule (uptick = buy, downtick = sell).
// No Level 2 / depth data and no bid/ask are needed. If a bar has no tick history it can fall back to a crude bar-based estimate.
// Original Pine quirks (POC migration never fires, divergence compares only to the previous bar) are FIXED by default;
// turn on "Replicate original script quirks" to get the original behaviour.
// Plots: 0 Poc 1 Vah 2 Val 3 Delta 4 Cvd 5 AnySignal(+1 buy/-1 sell) 6 BuyScore 7 SellScore 8 MigrationDir 9 TickData(1=ticks,0=estimate)
// Written on the CLOSED bar (Calculate = On bar close).
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
// Streaming core for SneakyVolumeProfilePro. One AddBar() per CLOSED bar. No NinjaTrader dependencies.
internal struct VpRow
{
	public long Key;      // price row index = floor(price / rowSize)
	public double Buy, Sell;
	public double Total { get { return Buy + Sell; } }
}

internal struct VpOut
{
	public bool Valid;
	public double Total, Buy, Sell, Delta, Cvd, Poc, Vah, Val, VolSma, BuySellRatio;
	public int MigDir;
	public bool PocBounce, PocReject, VahBreak, VahReject, ValBreak, ValSupport, BullDiv, BearDiv, Accum, Distrib;
	public int BuyScore, SellScore, Any;
}

internal class SneakyVpCore
{
	public double VaPct = 0.70, PocTouchMult = 0.1;
	public int DivLook = 5;
	public bool EnPoc = true, EnVa = true, EnDiv = true, EnMig = true;
	public bool Faithful = false;   // replicate quirks of the original Pine script

	private const double NaN = double.NaN;
	private readonly System.Collections.Generic.List<double> cl = new System.Collections.Generic.List<double>();
	private readonly System.Collections.Generic.List<double> dl = new System.Collections.Generic.List<double>();
	private readonly System.Collections.Generic.List<double> tv = new System.Collections.Generic.List<double>();
	private readonly System.Collections.Generic.List<double> cvdL = new System.Collections.Generic.List<double>();
	private readonly System.Collections.Generic.List<double> vahL = new System.Collections.Generic.List<double>();
	private double cvd, prevPoc = NaN, prevPrevPoc = NaN;
	private int migDir;
	// pivots
	private double lastPh = NaN, lastPhDelta = NaN, lastPl = NaN, lastPlDelta = NaN;
	private bool prevBarHadPh, prevBarHadPl; private double prevBarPh = NaN, prevBarPl = NaN;

	public VpOut AddBar(double O, double H, double L, double C, double atr, VpRow[] rows, double rowSize)
	{
		VpOut r = new VpOut();
		int n = rows == null ? 0 : rows.Length;
		double tot = 0, buy = 0, sell = 0;
		for (int k = 0; k < n; k++) { tot += rows[k].Total; buy += rows[k].Buy; sell += rows[k].Sell; }
		bool valid = n > 0 && tot > 0;
		r.Valid = valid;
		r.Total = valid ? tot : 0; r.Buy = valid ? buy : 0; r.Sell = valid ? sell : 0;
		r.Delta = valid ? buy - sell : 0;
		r.BuySellRatio = r.Total > 0 ? r.Buy / r.Total : 0.5;

		int i = cl.Count;               // index of this bar
		cvd = i == 0 ? r.Delta : cvd + r.Delta;
		r.Cvd = cvd;

		// ---- POC / VAH / VAL (rows assumed ascending by Key; empty rows between keys count as zero rows) ----
		double poc = NaN, vah = NaN, val = NaN;
		if (valid)
		{
			long k0 = rows[0].Key, k1 = rows[n - 1].Key; int len = (int)(k1 - k0 + 1);
			double[] seg = new double[len];
			for (int k = 0; k < n; k++) seg[(int)(rows[k].Key - k0)] = rows[k].Total;
			int p = 0; double best = seg[0];
			for (int k = 1; k < len; k++) if (seg[k] > best) { best = seg[k]; p = k; }
			double sum = seg[p]; int lo = p, hi = p;
			while (sum < VaPct * tot && (lo > 0 || hi < len - 1))
			{
				double up = hi < len - 1 ? seg[hi + 1] : -1, dn = lo > 0 ? seg[lo - 1] : -1;
				if (up >= dn) { hi++; sum += seg[hi]; } else { lo--; sum += seg[lo]; }
			}
			poc = (k0 + p + 1) * rowSize;       // up price of the POC row
			vah = (k0 + hi + 1) * rowSize;      // up price of the VAH row
			val = (k0 + lo) * rowSize;          // down price of the VAL row
		}
		r.Poc = poc; r.Vah = vah; r.Val = val;
		double prevVah = i > 0 ? vahL[i - 1] : NaN;
		double prevClose = i > 0 ? cl[i - 1] : NaN;

		cl.Add(C); dl.Add(r.Delta); tv.Add(r.Total); cvdL.Add(cvd); vahL.Add(vah);

		// ---- volume SMA(20) of total volume ----
		double volSma = NaN;
		if (i >= 19) { double s = 0; for (int j = i - 19; j <= i; j++) s += tv[j]; volSma = s / 20.0; }
		r.VolSma = volSma;

		// ---- POC migration ----
		if (!double.IsNaN(poc))
		{
			if (Faithful)
			{
				prevPrevPoc = prevPoc; prevPoc = poc;
				if (!double.IsNaN(prevPoc) && !double.IsNaN(prevPrevPoc))
				{
					if (poc > prevPoc && prevPoc > prevPrevPoc) migDir = 1;
					else if (poc < prevPoc && prevPoc < prevPrevPoc) migDir = -1;
					else migDir = 0;
				}
			}
			else
			{
				if (!double.IsNaN(prevPoc) && !double.IsNaN(prevPrevPoc))
				{
					if (poc > prevPoc && prevPoc > prevPrevPoc) migDir = 1;
					else if (poc < prevPoc && prevPoc < prevPrevPoc) migDir = -1;
					else migDir = 0;
				}
				prevPrevPoc = prevPoc; prevPoc = poc;
			}
		}
		r.MigDir = migDir;

		// ---- signals ----
		double delta = r.Delta;
		bool touch = !double.IsNaN(poc) && System.Math.Abs(C - poc) < atr * PocTouchMult;
		r.PocBounce = EnPoc && touch && C > poc && delta > 0;
		r.PocReject = EnPoc && touch && C < poc && delta < 0;
		r.VahBreak = EnVa && !double.IsNaN(vah) && C > vah && prevClose <= prevVah && delta > 0;
		r.VahReject = EnVa && !double.IsNaN(vah) && H >= vah && C < vah && delta < 0;
		r.ValBreak = EnVa && !double.IsNaN(val) && C < val && prevClose >= PrevVal(i) && delta < 0;
		r.ValSupport = EnVa && !double.IsNaN(val) && L <= val && C > val && delta > 0;
		valL.Add(val);

		// divergence: pivots of close with DivLook bars each side, confirmed at bar i
		bool phNow = false, plNow = false; double ph = NaN, pl = NaN;
		int Lk = DivLook;
		if (i >= 2 * Lk)
		{
			double c0 = cl[i - Lk]; bool isH = true, isL = true;
			for (int j = i - 2 * Lk; j <= i; j++)
			{
				if (j == i - Lk) continue;
				if (j < i - Lk) { if (!(c0 > cl[j])) isH = false; if (!(c0 < cl[j])) isL = false; }
				else { if (!(c0 >= cl[j])) isH = false; if (!(c0 <= cl[j])) isL = false; }
			}
			if (isH) { phNow = true; ph = c0; }
			if (isL) { plNow = true; pl = c0; }
		}
		bool bear = false, bull = false;
		if (EnDiv)
		{
			if (phNow)
			{
				double cmpPrice = Faithful ? (prevBarHadPh ? prevBarPh : NaN) : lastPh;
				bear = !double.IsNaN(cmpPrice) && ph > cmpPrice && !double.IsNaN(lastPhDelta) && delta < lastPhDelta;
			}
			if (plNow)
			{
				double cmpPrice = Faithful ? (prevBarHadPl ? prevBarPl : NaN) : lastPl;
				bull = !double.IsNaN(cmpPrice) && pl < cmpPrice && !double.IsNaN(lastPlDelta) && delta > lastPlDelta;
			}
		}
		r.BearDiv = bear; r.BullDiv = bull;
		if (phNow) { lastPh = ph; lastPhDelta = delta; }
		if (plNow) { lastPl = pl; lastPlDelta = delta; }
		prevBarHadPh = phNow; prevBarPh = ph; prevBarHadPl = plNow; prevBarPl = pl;

		r.Accum = EnMig && migDir == 1 && r.Total > volSma;
		r.Distrib = EnMig && migDir == -1 && r.Total > volSma;

		if (!valid) { r.PocBounce = r.PocReject = r.VahBreak = r.VahReject = r.ValBreak = r.ValSupport = r.BullDiv = r.BearDiv = r.Accum = r.Distrib = false; }
		r.BuyScore = (r.PocBounce ? 1 : 0) + (r.ValSupport ? 1 : 0) + (r.VahBreak ? 1 : 0) + (r.BullDiv ? 1 : 0) + (r.Accum ? 1 : 0);
		r.SellScore = (r.PocReject ? 1 : 0) + (r.VahReject ? 1 : 0) + (r.ValBreak ? 1 : 0) + (r.BearDiv ? 1 : 0) + (r.Distrib ? 1 : 0);
		r.Any = r.BuyScore > 0 ? 1 : (r.SellScore > 0 ? -1 : 0);
		return r;
	}

	private readonly System.Collections.Generic.List<double> valL = new System.Collections.Generic.List<double>();
	private double PrevVal(int i) { return i > 0 ? valL[i - 1] : NaN; }

	public double CvdAgo(int bars) { int i = cvdL.Count - 1 - bars; return i >= 0 ? cvdL[i] : NaN; }
}


	public enum SvpTablePos { TopRight, TopLeft, BottomRight, BottomLeft }

	public class SneakyVolumeProfilePro : Indicator
	{
		private struct TickRec { public DateTime Time; public double Price, Size; public int Side; }

		private SneakyVpCore core;
		private ATR atr;
		private readonly List<TickRec> pending = new List<TickRec>();
		private double lastTickPrice; private int lastSide = 1; private bool haveTick;
		private VpOut last; private bool haveLast;
		private readonly Dictionary<int, VpRow[]> barRows = new Dictionary<int, VpRow[]>();
		private Series<double> conc;
		private readonly Queue<string> magnetTags = new Queue<string>();
		private readonly Dictionary<int, System.Windows.Media.Brush> heatBrushes = new Dictionary<int, System.Windows.Media.Brush>();
		private double rowSize;
		private readonly object pendLock = new object();

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description		= "Per-bar volume profile (POC/VAH/VAL), delta and signals from trade prints; port of blackcat1402 L3 Volume Profile Pro.";
				Name			= "SneakyVolumeProfilePro";
				Calculate		= Calculate.OnBarClose;
				IsOverlay		= true;
				DisplayInDataBox = true;
				PaintPriceMarkers = false;
				IsSuspendedWhileInactive = true;
				ArePlotsConfigurable = true;
				IsAutoScale		= false;
				DrawOnPricePanel = true;
				TicksPerRow = 10; VaPercent = 70; PocTouchMult = 0.1; DivergenceLookback = 5;
				EnablePocSignals = true; EnableVaSignals = true; EnableDivergenceSignals = true; EnableMigrationSignals = true;
				ShowPocLines = true; ShowVaCloud = true; ShowHistogram = true; ShowMigrationArrows = true; ShowMagnetLines = true;
				ShowDeltaHeatmap = true; ShowVaPosition = true; ShowSignalLabels = true; ShowStatusTable = true;
				TablePosition = SvpTablePos.TopRight;
				UseTickData = true; QuoteRuleLive = true; FallbackEstimate = true; ReplicateQuirks = false;
				LabelBarsBack = 300; HistogramBars = 30; HistogramWidthPx = 30; EnableAlerts = false;
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "Poc");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "Vah");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "Val");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "Delta");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "Cvd");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "AnySignal");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "BuyScore");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "SellScore");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "MigrationDir");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "TickData");
			}
			else if (State == State.Configure)
			{
				// base plots 0-2 are data only (levels are drawn per bar in OnRender); force transparent even if a saved chart template had colours
				for (int pi = 0; pi < 3; pi++) Plots[pi].Brush = System.Windows.Media.Brushes.Transparent;
				if (UseTickData)
					AddDataSeries(BarsPeriodType.Tick, 1);
			}
			else if (State == State.DataLoaded)
			{
				core = new SneakyVpCore
				{
					VaPct = VaPercent / 100.0, PocTouchMult = PocTouchMult, DivLook = DivergenceLookback,
					EnPoc = EnablePocSignals, EnVa = EnableVaSignals, EnDiv = EnableDivergenceSignals, EnMig = EnableMigrationSignals,
					Faithful = ReplicateQuirks
				};
				atr = ATR(14);
				conc = new Series<double>(this);
				rowSize = TicksPerRow * TickSize;
			}
			else if (State == State.Terminated)
			{
				pending.Clear(); barRows.Clear();
			}
		}

		private bool errShown;
		protected override void OnBarUpdate()
		{
			try { BarUpdateInner(); }
			catch (Exception ex)
			{
				if (!errShown) { errShown = true; Print("SneakyVolumeProfilePro OnBarUpdate error at bar " + CurrentBar + ": " + ex); }
			}
		}

		private void BarUpdateInner()
		{
			if (core == null)
				return;

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

			// ---- rows for this bar ----
			DateTime barEnd = Time[0];
			int take = 0;
			TickRec[] taken;
			lock (pendLock)
			{
				while (take < pending.Count && pending[take].Time < barEnd) take++;
				taken = pending.GetRange(0, take).ToArray();
				pending.RemoveRange(0, take);
			}
			VpRow[] rows; bool fromTicks = take > 0;
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
				rows = new VpRow[d.Count]; int n = 0;
				foreach (var kv in d) { rows[n].Key = kv.Key; rows[n].Buy = kv.Value[0]; rows[n].Sell = kv.Value[1]; n++; }
			}
			else if (FallbackEstimate && Volume[0] > 0)
				rows = EstimateRows(Open[0], High[0], Low[0], Close[0], Volume[0]);
			else
				rows = new VpRow[0];

			double a = atr[0];
			VpOut r = core.AddBar(Open[0], High[0], Low[0], Close[0], a, rows, rowSize);
			last = r; haveLast = true;
			barRows[CurrentBar] = rows; barRows.Remove(CurrentBar - 800);

			Values[0][0] = r.Valid ? r.Poc : double.NaN;
			Values[1][0] = r.Valid ? r.Vah : double.NaN;
			Values[2][0] = r.Valid ? r.Val : double.NaN;
			Values[3][0] = r.Delta;
			Values[4][0] = r.Cvd;
			Values[5][0] = r.Any;
			Values[6][0] = r.BuyScore;
			Values[7][0] = r.SellScore;
			Values[8][0] = r.MigDir;
			Values[9][0] = fromTicks ? 1 : 0;
			conc[0] = !double.IsNaN(r.VolSma) && r.VolSma > 0 ? r.Total / r.VolSma : 1;

			if (!ShowPocLines) Values[0].Reset();
			if (!ShowVaCloud) { Values[1].Reset(); Values[2].Reset(); }

			// ---- heatmap background ----
			if (ShowDeltaHeatmap && r.Valid && r.Total > 0)
			{
				double inten = Math.Min(Math.Abs(r.Delta) / r.Total * 2.0, 1.0);
				int q = (int)Math.Round(inten * 20) * (r.Delta > 0 ? 1 : -1);
				System.Windows.Media.Brush hb;
				if (!heatBrushes.TryGetValue(q, out hb))
				{
					byte alpha = (byte)(255 * (Math.Abs(q) / 20.0) * 0.20);
					var sb = r.Delta > 0 ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, 0, 160, 80)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, 210, 40, 50));
					sb.Freeze(); hb = sb; heatBrushes[q] = hb;
				}
				BackBrushes[0] = hb;
			}

			bool recent = CurrentBar >= Count - 1 - LabelBarsBack;
			if (recent)
			{
				if (ShowSignalLabels) DrawSignals(r, a);
				if (ShowMigrationArrows && r.Valid && r.MigDir != 0)
					Draw.Text(this, "SvpMig" + CurrentBar, false, r.MigDir > 0 ? "▲" : "▼", 0, r.Poc, 0,
						r.MigDir > 0 ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Red, new SimpleFont("Arial", 10),
						System.Windows.TextAlignment.Center, System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.Transparent, 0);
				if (ShowMagnetLines && r.Valid && CurrentBar >= 5 && !double.IsNaN(Values[0][5]))
				{
					string tag = "SvpMag" + CurrentBar;
					Draw.Line(this, tag, false, 5, Values[0][5], 0, Close[0], System.Windows.Media.Brushes.Gray, DashStyleHelper.Dot, 1);
					magnetTags.Enqueue(tag);
					while (magnetTags.Count > 6) RemoveDrawObject(magnetTags.Dequeue());
				}
			}

			if (ShowVaPosition && CurrentBar == Count - 1 && r.Valid)
			{
				string txt; System.Windows.Media.Brush col;
				if (Close[0] > r.Vah) { txt = "  Above VAH"; col = System.Windows.Media.Brushes.LimeGreen; }
				else if (Close[0] < r.Val) { txt = "  Below VAL"; col = System.Windows.Media.Brushes.Red; }
				else { txt = "  Inside VA"; col = System.Windows.Media.Brushes.DodgerBlue; }
				Draw.Text(this, "SvpVaPos", false, txt, 0, Close[0], 0, col, new SimpleFont("Arial", 10),
					System.Windows.TextAlignment.Left, System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.Black, 50);
			}

			if (EnableAlerts && State == State.Realtime)
				RaiseAlerts(r);
		}

		// Live prints: Lee & Ready quote rule (>= ask buy, <= bid sell, in between -> tick rule), same method as the Sneaky dashboard.
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

		private VpRow[] EstimateRows(double o, double h, double l, double c, double vol)
		{
			long k0 = (long)Math.Floor(l / rowSize + 1e-9), k1 = (long)Math.Floor(h / rowSize + 1e-9);
			int n = (int)(k1 - k0 + 1); if (n < 1) n = 1;
			double rr = h > l ? (c - o) / (h - l) : 0; if (rr > 1) rr = 1; if (rr < -1) rr = -1;
			double buyShare = (1 + rr) / 2.0;
			var rows = new VpRow[n];
			for (int k = 0; k < n; k++) { double v = vol / n; rows[k].Key = k0 + k; rows[k].Buy = v * buyShare; rows[k].Sell = v * (1 - buyShare); }
			return rows;
		}

		private void Lbl(string tag, string text, double y, System.Windows.Media.Brush color, int barsAgo)
		{
			Draw.Text(this, tag + (CurrentBar - barsAgo), false, text, barsAgo, y, 0, color, new SimpleFont("Arial", 9),
				System.Windows.TextAlignment.Center, System.Windows.Media.Brushes.Transparent, color, 18);
		}

		private void DrawSignals(VpOut r, double a)
		{
			string d = r.Delta.ToString("0.#");
			string v = r.Total >= 1000 ? (r.Total / 1000.0).ToString("0.#") + "K" : r.Total.ToString("0");
			if (r.PocBounce)  Lbl("SvpPB", "POC Bounce\nΔ: " + d, Low[0] - a * 0.3, System.Windows.Media.Brushes.LimeGreen, 0);
			if (r.ValSupport) Lbl("SvpVS", "VAL Support\nΔ: " + d, Low[0] - a * 0.3, System.Windows.Media.Brushes.DodgerBlue, 0);
			if (r.VahBreak)   Lbl("SvpVB", "VAH Breakout\nVol: " + v, Low[0] - a * 0.3, System.Windows.Media.Brushes.Gold, 0);
			if (r.Accum)      Lbl("SvpAC", "Accumulation\nPOC ▲", Low[0] - a * 0.35, System.Windows.Media.Brushes.Teal, 0);
			if (r.PocReject)  Lbl("SvpPR", "POC Rejection\nΔ: " + d, High[0] + a * 0.3, System.Windows.Media.Brushes.Red, 0);
			if (r.VahReject)  Lbl("SvpVR", "VAH Resistance\nΔ: " + d, High[0] + a * 0.3, System.Windows.Media.Brushes.Orange, 0);
			if (r.ValBreak)   Lbl("SvpVD", "VAL Breakdown\nVol: " + v, High[0] + a * 0.3, System.Windows.Media.Brushes.Crimson, 0);
			if (r.Distrib)    Lbl("SvpDI", "Distribution\nPOC ▼", High[0] + a * 0.35, System.Windows.Media.Brushes.Maroon, 0);
			int L = DivergenceLookback;
			if (CurrentBar >= L)
			{
				if (r.BullDiv) Lbl("SvpBD", "Bullish Div\nCVD: " + r.Cvd.ToString("0.#"), Low[L] - atr[L] * 0.4, System.Windows.Media.Brushes.MediumPurple, L);
				if (r.BearDiv) Lbl("SvpSD", "Bearish Div\nCVD: " + r.Cvd.ToString("0.#"), High[L] + atr[L] * 0.4, System.Windows.Media.Brushes.MediumPurple, L);
			}
		}

		private void RaiseAlerts(VpOut r)
		{
			string snd = NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav";
			if (r.PocBounce)  Alert("svpPB" + CurrentBar, Priority.Medium, "POC Bounce: bounced from POC with buying pressure", snd, 10, System.Windows.Media.Brushes.Black, System.Windows.Media.Brushes.LimeGreen);
			if (r.PocReject)  Alert("svpPR" + CurrentBar, Priority.Medium, "POC Rejection: rejected at POC with selling pressure", snd, 10, System.Windows.Media.Brushes.Black, System.Windows.Media.Brushes.Red);
			if (r.VahBreak)   Alert("svpVB" + CurrentBar, Priority.Medium, "VAH Breakout with positive delta", snd, 10, System.Windows.Media.Brushes.Black, System.Windows.Media.Brushes.Gold);
			if (r.VahReject)  Alert("svpVR" + CurrentBar, Priority.Medium, "VAH Rejection: resistance with negative delta", snd, 10, System.Windows.Media.Brushes.Black, System.Windows.Media.Brushes.Orange);
			if (r.ValBreak)   Alert("svpVD" + CurrentBar, Priority.Medium, "VAL Breakdown with negative delta", snd, 10, System.Windows.Media.Brushes.Black, System.Windows.Media.Brushes.Crimson);
			if (r.ValSupport) Alert("svpVS" + CurrentBar, Priority.Medium, "VAL Support with positive delta", snd, 10, System.Windows.Media.Brushes.Black, System.Windows.Media.Brushes.DodgerBlue);
			if (r.BullDiv)    Alert("svpBD" + CurrentBar, Priority.Medium, "Bullish delta divergence", snd, 10, System.Windows.Media.Brushes.Black, System.Windows.Media.Brushes.MediumPurple);
			if (r.BearDiv)    Alert("svpSD" + CurrentBar, Priority.Medium, "Bearish delta divergence", snd, 10, System.Windows.Media.Brushes.Black, System.Windows.Media.Brushes.MediumPurple);
			if (r.Accum)      Alert("svpAC" + CurrentBar, Priority.Medium, "Accumulation: POC migrating up on rising volume", snd, 10, System.Windows.Media.Brushes.Black, System.Windows.Media.Brushes.Teal);
			if (r.Distrib)    Alert("svpDI" + CurrentBar, Priority.Medium, "Distribution: POC migrating down on rising volume", snd, 10, System.Windows.Media.Brushes.Black, System.Windows.Media.Brushes.Maroon);
		}

		#region Rendering (value-area cloud, volume histogram, status table)

		private static SharpDX.Color4 C4(byte r, byte g, byte b, float a) { return new SharpDX.Color4(r / 255f, g / 255f, b / 255f, a); }

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (Bars == null || ChartBars == null || RenderTarget == null || core == null) return;
			try { RenderInner(chartControl, chartScale); }
			catch (Exception ex) { if (!errShown) { errShown = true; Print("SneakyVolumeProfilePro OnRender error: " + ex); } }
		}

		private void RenderInner(ChartControl chartControl, ChartScale chartScale)
		{
			int from = ChartBars.FromIndex, to = ChartBars.ToIndex;
			if (to < 0) return;
			var disp = new List<IDisposable>();
			try
			{
				// ---- Value area cloud + per-bar POC / VAH / VAL segments ----
				for (int i = from; i <= to; i++)
				{
					if (i < 0 || i >= Bars.Count) continue;
					if (!Values[1].IsValidDataPointAt(i) || !Values[2].IsValidDataPointAt(i)) continue;
					double vah = Values[1].GetValueAt(i), val = Values[2].GetValueAt(i), poc = Values[0].GetValueAt(i);
					if (double.IsNaN(vah) || double.IsNaN(val)) continue;
					float x = chartControl.GetXByBarIndex(ChartBars, i), x2 = chartControl.GetXByBarIndex(ChartBars, i + 1);
					float slot = Math.Max(2f, Math.Abs(x2 - x)), half = slot / 2f - 1f;
					float y1 = chartScale.GetYByValue(vah), y2 = chartScale.GetYByValue(val);
					if (ShowVaCloud)
					{
						var cb = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, C4(70, 120, 255, 0.08f)); disp.Add(cb);
						RenderTarget.FillRectangle(new SharpDX.RectangleF(x - half, Math.Min(y1, y2), half * 2f, Math.Max(1f, Math.Abs(y2 - y1))), cb);
						var gb = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, C4(60, 200, 90, 0.9f)); disp.Add(gb);
						var rb = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, C4(220, 60, 70, 0.9f)); disp.Add(rb);
						RenderTarget.DrawLine(new SharpDX.Vector2(x - half, y1), new SharpDX.Vector2(x + half, y1), gb, 1.2f);
						RenderTarget.DrawLine(new SharpDX.Vector2(x - half, y2), new SharpDX.Vector2(x + half, y2), rb, 1.2f);
					}
					if (ShowPocLines && !double.IsNaN(poc))
					{
						var ob = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, C4(255, 150, 20, 1f)); disp.Add(ob);
						float yp = chartScale.GetYByValue(poc);
						RenderTarget.DrawLine(new SharpDX.Vector2(x - half, yp), new SharpDX.Vector2(x + half, yp), ob, 2.5f);
					}
				}

				// ---- Mini volume histogram (all rows, right half of each bar slot) ----
				if (ShowHistogram)
				{
					int h0 = Math.Max(from, to - HistogramBars + 1);
					for (int i = h0; i <= to; i++)
					{
						VpRow[] rows; if (!barRows.TryGetValue(i, out rows) || rows.Length == 0) continue;
						double mx = 0; for (int k = 0; k < rows.Length; k++) if (rows[k].Total > mx) mx = rows[k].Total;
						if (mx <= 0) continue;
						float x = chartControl.GetXByBarIndex(ChartBars, i);
						float slot = Math.Max(2f, Math.Abs(chartControl.GetXByBarIndex(ChartBars, i + 1) - x));
						float maxw = Math.Min((float)HistogramWidthPx, slot * 0.48f);
						for (int k = 0; k < rows.Length; k++)
						{
							var row = rows[k];
							float yt = chartScale.GetYByValue((row.Key + 1) * rowSize), yb = chartScale.GetYByValue(row.Key * rowSize);
							float wpx = (float)(row.Total / mx) * maxw;
							var br = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, row.Buy >= row.Sell ? C4(0, 200, 100, 0.40f) : C4(235, 55, 65, 0.40f)); disp.Add(br);
							RenderTarget.FillRectangle(new SharpDX.RectangleF(x + slot / 2f - 1f - wpx, yt + 0.5f, Math.Max(1f, wpx), Math.Max(1f, Math.Abs(yb - yt) - 1f)), br);
						}
					}
				}

				// ---- Status table ----
				if (ShowStatusTable && haveLast)
					DrawTable(chartControl, disp);
			}
			finally { foreach (var d in disp) d.Dispose(); }
		}

		private void DrawTable(ChartControl cc, List<IDisposable> disp)
		{
			VpOut r = last;
			string[,] cells = new string[9, 3]; SharpDX.Color4[,] cols = new SharpDX.Color4[9, 3];
			SharpDX.Color4 white = C4(255, 255, 255, 1), gray = C4(150, 150, 150, 1), green = C4(60, 210, 120, 1), red = C4(240, 70, 80, 1), orange = C4(255, 150, 40, 1), yellow = C4(250, 220, 60, 1);
			Func<double, string> f = x => double.IsNaN(x) ? "N/A" : Instrument.MasterInstrument.FormatPrice(x);
			cells[0, 0] = "Volume Profile Status"; cells[0, 1] = "Value"; cells[0, 2] = "Signal"; for (int c = 0; c < 3; c++) cols[0, c] = white;
			double close = Close.Count > 0 ? Close[0] : 0;
			double pd = (!double.IsNaN(r.Poc) && close > 0) ? (close - r.Poc) / close * 100.0 : 0;
			cells[1, 0] = "POC Price"; cells[1, 1] = f(r.Poc); cells[1, 2] = pd.ToString("0.##") + "%"; cols[1, 1] = orange; cols[1, 2] = pd > 0 ? green : red;
			bool inside = !double.IsNaN(r.Val) && !double.IsNaN(r.Vah) && close > r.Val && close < r.Vah;
			cells[2, 0] = "Value Area"; cells[2, 1] = f(r.Val) + " - " + f(r.Vah); cells[2, 2] = inside ? "Inside" : "Outside"; cols[2, 1] = white; cols[2, 2] = inside ? green : red;
			cells[3, 0] = "Volume Delta"; cells[3, 1] = r.Delta.ToString("0.#"); cells[3, 2] = r.Delta > 0 ? "Bullish" : "Bearish"; cols[3, 1] = cols[3, 2] = r.Delta > 0 ? green : red;
			bool accum = r.Cvd > core.CvdAgo(5);
			cells[4, 0] = "CVD"; cells[4, 1] = r.Cvd.ToString("0.#"); cells[4, 2] = accum ? "Accumulating" : "Distributing"; cols[4, 1] = r.Cvd > 0 ? green : red; cols[4, 2] = accum ? green : red;
			cells[5, 0] = "POC Migration"; cells[5, 1] = r.MigDir == 1 ? "Upward" : (r.MigDir == -1 ? "Downward" : "Neutral");
			cells[5, 2] = r.MigDir == 1 ? "Accumulation" : (r.MigDir == -1 ? "Distribution" : "Neutral"); cols[5, 1] = cols[5, 2] = r.MigDir == 1 ? green : (r.MigDir == -1 ? red : gray);
			cells[6, 0] = "Buy/Sell Ratio"; cells[6, 1] = (r.BuySellRatio * 100).ToString("0.#") + "%"; cells[6, 2] = new string('█', 1 + (r.BuySellRatio > 0.6 ? 1 : 0) + (r.BuySellRatio > 0.8 ? 1 : 0));
			cols[6, 1] = r.BuySellRatio > 0.5 ? green : red; cols[6, 2] = green;
			int sc = r.BuyScore + r.SellScore;
			cells[7, 0] = "Active Signal"; cells[7, 1] = r.BuyScore > 0 ? "BUY" : (r.SellScore > 0 ? "SELL" : "None"); cells[7, 2] = sc > 0 ? sc + " Active" : "None";
			cols[7, 1] = r.BuyScore > 0 ? green : (r.SellScore > 0 ? red : gray); cols[7, 2] = sc > 0 ? yellow : gray;
			double w = (!double.IsNaN(r.Vah) && !double.IsNaN(r.Val)) ? r.Vah - r.Val : 0;
			cells[8, 0] = "VA Percentage"; cells[8, 1] = VaPercent.ToString("0") + "%"; cells[8, 2] = "Width: " + f(w); cols[8, 1] = white; cols[8, 2] = gray;
			for (int i = 1; i < 9; i++) cols[i, 0] = gray;

			float[] cw = { 150f, 130f, 110f }; float rh = 20f, tw = cw[0] + cw[1] + cw[2], th = rh * 9f;
			float pw = (float)ChartPanel.W, ph = (float)ChartPanel.H;
			float x0 = (TablePosition == SvpTablePos.TopRight || TablePosition == SvpTablePos.BottomRight) ? pw - tw - 10f : 10f;
			float y0 = (TablePosition == SvpTablePos.TopRight || TablePosition == SvpTablePos.TopLeft) ? 10f : ph - th - 10f;
			var bg = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, C4(0, 0, 0, 0.82f)); disp.Add(bg);
			var fr = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, C4(120, 120, 120, 1)); disp.Add(fr);
			RenderTarget.FillRectangle(new SharpDX.RectangleF(x0, y0, tw, th), bg);
			RenderTarget.DrawRectangle(new SharpDX.RectangleF(x0, y0, tw, th), fr, 1f);
			var fmt = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial", 12f); disp.Add(fmt);
			for (int i = 0; i < 9; i++)
			{
				float cx = x0;
				for (int c = 0; c < 3; c++)
				{
					var tb = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, cols[i, c]); disp.Add(tb);
					RenderTarget.DrawText(cells[i, c] ?? "", fmt, new SharpDX.RectangleF(cx + 6f, y0 + i * rh + 2f, cw[c] - 8f, rh), tb);
					cx += cw[c];
				}
			}
		}

		#endregion

		#region Properties

		[Display(Name = "Ticks Per Row", Description = "Price rows are this many ticks tall (original default 10).", Order = 0, GroupName = "01. Profile")]
		[Range(1, 200)]
		public int TicksPerRow { get; set; }

		[Display(Name = "Value Area %", Order = 1, GroupName = "01. Profile")]
		[Range(50, 95)]
		public double VaPercent { get; set; }

		[Display(Name = "Use Trade Prints (1-tick series)", Description = "Builds rows from tick history/live ticks, classified by the tick rule. Needs tick data for the chart's date range.", Order = 2, GroupName = "01. Profile")]
		public bool UseTickData { get; set; }

		[Display(Name = "Quote Rule On Live Bars", Description = "Realtime prints are classified with bid/ask (Lee & Ready, as in the Sneaky dashboard). History always uses the tick rule, so live and historical bars differ slightly.", Order = 2, GroupName = "01. Profile")]
		public bool QuoteRuleLive { get; set; }

		[Display(Name = "Fallback: Estimate From Bar When No Ticks", Description = "Crude: spreads bar volume evenly over its range, delta from close vs open. Mixing it with real data can mislead.", Order = 3, GroupName = "01. Profile")]
		public bool FallbackEstimate { get; set; }

		[Display(Name = "Replicate Original Script Quirks", Description = "The Pine script never fires POC-migration signals and only checks divergence against the previous bar. Off = fixed logic.", Order = 4, GroupName = "01. Profile")]
		public bool ReplicateQuirks { get; set; }

		[Display(Name = "POC Signals", Order = 0, GroupName = "02. Signals")]
		public bool EnablePocSignals { get; set; }
		[Display(Name = "Value Area Signals", Order = 1, GroupName = "02. Signals")]
		public bool EnableVaSignals { get; set; }
		[Display(Name = "Divergence Signals", Order = 2, GroupName = "02. Signals")]
		public bool EnableDivergenceSignals { get; set; }
		[Display(Name = "Migration Signals", Order = 3, GroupName = "02. Signals")]
		public bool EnableMigrationSignals { get; set; }
		[Display(Name = "POC Touch Threshold (x ATR)", Order = 4, GroupName = "02. Signals")]
		[Range(0.01, 2)]
		public double PocTouchMult { get; set; }
		[Display(Name = "Divergence Lookback", Order = 5, GroupName = "02. Signals")]
		[Range(2, 30)]
		public int DivergenceLookback { get; set; }
		[Display(Name = "Alerts On Signals (realtime)", Order = 6, GroupName = "02. Signals")]
		public bool EnableAlerts { get; set; }

		[Display(Name = "Show POC Line", Order = 0, GroupName = "03. Display")]
		public bool ShowPocLines { get; set; }
		[Display(Name = "Show Value Area Lines + Cloud", Order = 1, GroupName = "03. Display")]
		public bool ShowVaCloud { get; set; }
		[Display(Name = "Show Volume Histogram", Order = 2, GroupName = "03. Display")]
		public bool ShowHistogram { get; set; }
		[Display(Name = "Show POC Migration Arrows", Order = 3, GroupName = "03. Display")]
		public bool ShowMigrationArrows { get; set; }
		[Display(Name = "Show POC Magnet Lines", Order = 4, GroupName = "03. Display")]
		public bool ShowMagnetLines { get; set; }
		[Display(Name = "Show Delta Heatmap Background", Order = 5, GroupName = "03. Display")]
		public bool ShowDeltaHeatmap { get; set; }
		[Display(Name = "Show VA Position Marker", Order = 6, GroupName = "03. Display")]
		public bool ShowVaPosition { get; set; }
		[Display(Name = "Show Signal Labels", Order = 7, GroupName = "03. Display")]
		public bool ShowSignalLabels { get; set; }
		[Display(Name = "Show Status Table", Order = 8, GroupName = "03. Display")]
		public bool ShowStatusTable { get; set; }
		[Display(Name = "Table Position", Order = 9, GroupName = "03. Display")]
		public SvpTablePos TablePosition { get; set; }
		[Display(Name = "Draw Labels On Last N Bars", Order = 10, GroupName = "03. Display")]
		[Range(10, 5000)]
		public int LabelBarsBack { get; set; }
		[Display(Name = "Histogram: Last N Bars", Order = 11, GroupName = "03. Display")]
		[Range(1, 500)]
		public int HistogramBars { get; set; }
		[Display(Name = "Histogram Max Width (px)", Order = 12, GroupName = "03. Display")]
		[Range(5, 200)]
		public int HistogramWidthPx { get; set; }

		#endregion

		#region Plot series (for automation tools)

		[Browsable(false)] [XmlIgnore] public Series<double> Poc { get { Update(); return Values[0]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> Vah { get { Update(); return Values[1]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> Val { get { Update(); return Values[2]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> Delta { get { Update(); return Values[3]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> Cvd { get { Update(); return Values[4]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> AnySignal { get { Update(); return Values[5]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> BuyScore { get { Update(); return Values[6]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> SellScore { get { Update(); return Values[7]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> MigrationDir { get { Update(); return Values[8]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> TickData { get { Update(); return Values[9]; } }

		#endregion
	}
}
