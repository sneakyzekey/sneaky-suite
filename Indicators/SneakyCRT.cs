#region Using declarations
using System;
using System.Collections.Generic;
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
#endregion

// SneakyCRT - CRT v8 (Candle Range Theory) signal logic from the open-source TradingView script, plus higher-timeframe confluence scoring.
// UNPROVEN: see Description.
namespace NinjaTrader.NinjaScript.Indicators
{
// ===== SneakyCRT core (pure C#, mirrors crtref.py) =====
public class CrtBias
{
	public int B; double lh = double.NaN, ll = double.NaN;
	System.Collections.Generic.List<double> H = new System.Collections.Generic.List<double>(), L = new System.Collections.Generic.List<double>();
	System.Collections.Generic.List<long> Tt = new System.Collections.Generic.List<long>(); System.Collections.Generic.List<int> Bb = new System.Collections.Generic.List<int>();
	public void Update(long endTicks, double h, double l, double c)
	{
		H.Add(h); L.Add(l); int n = H.Count;
		if (n >= 7)
		{
			int k = n - 1 - 3; double hk = H[k], lk = L[k]; bool ph = true, pl = true;
			for (int j = k - 3; j < k; j++) { if (!(hk > H[j])) ph = false; if (!(lk < L[j])) pl = false; }
			for (int j = k + 1; j < n; j++) { if (!(hk >= H[j])) ph = false; if (!(lk <= L[j])) pl = false; }
			if (ph) lh = hk; if (pl) ll = lk;
		}
		if (!double.IsNaN(lh) && c > lh) { B = 1; lh = double.NaN; }
		else if (!double.IsNaN(ll) && c < ll) { B = -1; ll = double.NaN; }
		Tt.Add(endTicks); Bb.Add(B);
		if (Tt.Count > 3000) { Tt.RemoveRange(0, 1000); Bb.RemoveRange(0, 1000); }
		if (H.Count > 3000) { H.RemoveRange(0, 1000); L.RemoveRange(0, 1000); }
	}
	public int At(long T) { for (int i = Tt.Count - 1; i >= 0; i--) if (Tt[i] < T) return Bb[i]; return 0; }
}
// Completed higher-timeframe candles; Before(T) = last candle whose END time is strictly before T (no lookahead, independent of series processing order).
public class CrtHtf
{
	public double H = double.NaN, L = double.NaN, C = double.NaN, O = double.NaN;
	System.Collections.Generic.List<long> Tt = new System.Collections.Generic.List<long>(); System.Collections.Generic.List<double[]> V = new System.Collections.Generic.List<double[]>();
	public void Add(long endTicks, double o, double h, double l, double c) { Tt.Add(endTicks); V.Add(new[] { o, h, l, c }); if (Tt.Count > 3000) { Tt.RemoveRange(0, 1000); V.RemoveRange(0, 1000); } }
	public void Before(long T)
	{
		H = L = C = O = double.NaN;
		for (int i = Tt.Count - 1; i >= 0; i--) if (Tt[i] < T) { O = V[i][0]; H = V[i][1]; L = V[i][2]; C = V[i][3]; return; }
	}
}
public class CrtOut { public int Sig, Score, A1, A4, Swp, Pdz, TpHit, SlHit; public double R1H = double.NaN, R1L = double.NaN, Tp = double.NaN, Sl = double.NaN; }
public class CrtEngine
{
	public bool UseA = true, UseB = true, ReqColor = true, Strict = true; public int MinScore = 0;
	int dir = 0; double tp = double.NaN, sl = double.NaN, dayH = double.NaN, dayL = double.NaN, pdh = double.NaN, pdl = double.NaN;
	System.Collections.Generic.List<double> O = new System.Collections.Generic.List<double>(), H = new System.Collections.Generic.List<double>(), L = new System.Collections.Generic.List<double>(), C = new System.Collections.Generic.List<double>();
	public double Pdh { get { return pdh; } } public double Pdl { get { return pdl; } }
	public CrtOut Step(bool newDay, double o, double h, double l, double c, bool active, int b1, int b4, double h1H, double h1L, double h2H, double h2L)
	{
		if (newDay) { if (!double.IsNaN(dayH)) { pdh = dayH; pdl = dayL; } dayH = h; dayL = l; }
		else { dayH = double.IsNaN(dayH) ? h : System.Math.Max(dayH, h); dayL = double.IsNaN(dayL) ? l : System.Math.Min(dayL, l); }
		O.Add(o); H.Add(h); L.Add(l); C.Add(c); int i = C.Count - 1; var r = new CrtOut();
		if (i < 1) return r;
		double r1H = H[i - 1], r1L = L[i - 1]; bool red = C[i - 1] < O[i - 1], grn = C[i - 1] > O[i - 1];
		bool sa = active && UseA && red && l < r1L && c > r1L && (!Strict || h < r1H) && (!ReqColor || c > o);
		bool sb = active && UseB && grn && h > r1H && c < r1H && (!Strict || l > r1L) && (!ReqColor || c < o);
		int side = sa ? 1 : (sb ? -1 : 0); int score = 0;
		if (side != 0)
		{
			int a1 = b1 == side ? 1 : 0, a4 = b4 == side ? 1 : 0, swp, pdz;
			if (side == 1) swp = ((!double.IsNaN(h1L) && l < h1L) || (!double.IsNaN(pdl) && l < pdl)) ? 1 : 0;
			else swp = ((!double.IsNaN(h1H) && h > h1H) || (!double.IsNaN(pdh) && h > pdh)) ? 1 : 0;
			if (double.IsNaN(h2H) || double.IsNaN(h2L)) pdz = 0; else { double mid = (h2H + h2L) / 2; pdz = side == 1 ? (c < mid ? 1 : 0) : (c > mid ? 1 : 0); }
			score = a1 + a4 + swp + pdz;
			if (score < MinScore) side = 0; else { r.A1 = a1; r.A4 = a4; r.Swp = swp; r.Pdz = pdz; }
		}
		bool sig = side != 0;
		bool tpHit = active && !sig && ((dir == 1 && h >= tp) || (dir == -1 && l <= tp));
		bool slHit = active && !sig && ((dir == 1 && c < sl) || (dir == -1 && c > sl));
		if (side == 1) { dir = 1; tp = r1H; sl = l; } else if (side == -1) { dir = -1; tp = r1L; sl = h; } else if (tpHit || slHit) dir = 0;
		r.Sig = side; r.Score = sig ? score : 0; r.TpHit = tpHit ? 1 : 0; r.SlHit = (slHit && !tpHit) ? 1 : 0; r.R1H = r1H; r.R1L = r1L; r.Tp = tp; r.Sl = sl; return r;
	}
}

// ===== SneakyCRT dashboard core: performance tracker + scanner row (pure C#, mirrored in dash_ref.py) =====
public class CrtStats
{
	public int[] N = new int[5], W = new int[5], L = new int[5], Rep = new int[5]; public double[] SumR = new double[5];
	bool open; int dir, score; double entry, tp, sl;
	public bool IsOpen { get { return open; } }
	public void Feed(CrtOut o, double close)
	{
		if (o.Sig != 0)
		{
			if (open) Rep[score]++;
			open = true; dir = o.Sig; score = o.Score; entry = close; tp = o.Tp; sl = o.Sl; N[score]++; return;
		}
		if (!open) return;
		if (o.TpHit == 1 || o.SlHit == 1)
		{
			double risk = System.Math.Abs(entry - sl); double r = 0;
			if (o.TpHit == 1) { W[score]++; if (risk > 0) r = dir * (tp - entry) / risk; }
			else { L[score]++; if (risk > 0) r = dir * (close - entry) / risk; }
			SumR[score] += r; open = false;
		}
	}
}
public class CrtScanRow
{
	public int Tf; public CrtEngine E; public int Dir, Score, SigBar = -1, Bars; public string Status = "";
	public void Feed(CrtOut o, int barIndex)
	{
		Bars = barIndex;
		if (o.Sig != 0) { Dir = o.Sig; Score = o.Score; SigBar = barIndex; Status = "OPEN"; }
		else if (Status == "OPEN" && o.TpHit == 1) Status = "TP";
		else if (Status == "OPEN" && o.SlHit == 1) Status = "SL";
	}
	public int Ago { get { return SigBar < 0 ? -1 : Bars - SigBar; } }
}
public enum SneakyCrtPos { TopLeft, TopRight, BottomLeft, BottomRight }
public class SneakyCRT : Indicator
{
	CrtEngine eng; CrtBias bias1, bias2; CrtHtf htf1, htf2; DateTime historyStart = DateTime.MinValue; SimpleFont labelFont; string tf1 = "1H", tf2 = "4H";
	CrtStats stats = new CrtStats(); CrtScanRow chartRow = new CrtScanRow(); List<CrtScanRow> rows = new List<CrtScanRow>(); List<int> scanTfs = new List<int>(); object lk = new object();
	static string TfName(int m) { return m % 60 == 0 ? (m / 60) + "H" : m + "m"; }
	protected override void OnStateChange()
	{
		if (State == State.SetDefaults)
		{
			Description = "CRT v8 (Candle Range Theory) with higher-timeframe confluence. Sweep of the previous candle's range that closes back inside = signal (same rules as the TradingView CRT v8). Confluence score 0-4: HTF1 bias aligned, HTF2 bias aligned, sweep also took the last HTF1 extreme or the previous day's extreme, price in discount (buys) / premium (sells) of the last HTF2 range. UNPROVEN: on 27 NQ sessions neither the plain signal nor the confluence filter showed a statistically reliable edge. Hidden plots: 0 Signal (+1 buy / -1 sell), 1 Score, 2 Target, 3 Invalidation, 4 TargetHit, 5 InvalidatedHit.";
			Name = "SneakyCRT"; Calculate = Calculate.OnBarClose; IsOverlay = true; DisplayInDataBox = true; DrawOnPricePanel = true; PaintPriceMarkers = false; IsSuspendedWhileInactive = false;
			HistoryDays = 5; UseModelA = true; UseModelB = true; RequireColor = true; StrictInside = true; ExtendBars = 10; Htf1Minutes = 60; Htf2Minutes = 240; MinConfluence = 0; ShowConfluenceTags = true;
			BullColor = System.Windows.Media.Brushes.Lime; BearColor = System.Windows.Media.Brushes.Red; LineColor = System.Windows.Media.Brushes.White;
			FontSize = 11; ShowCrtText = true; ShowManipLabel = true; SignalTextShift = 0; LabelSpacing = 18; HLLabelShift = 0; AlertOnSignals = true; AlertOnTargets = true; AlertSoundFile = "Alert1.wav";
			ShowScanner = true; ShowPerformance = true; DashPosition = SneakyCrtPos.TopRight; DashFontSize = 10; DashOpacity = 85; ScanTf1 = 5; ScanTf2 = 15; ScanTf3 = 30; ScanTf4 = 60; ScanTf5 = 240;
			AddPlot(System.Windows.Media.Brushes.Transparent, "Signal"); AddPlot(System.Windows.Media.Brushes.Transparent, "Score"); AddPlot(System.Windows.Media.Brushes.Transparent, "Target");
			AddPlot(System.Windows.Media.Brushes.Transparent, "Invalidation"); AddPlot(System.Windows.Media.Brushes.Transparent, "TargetHit"); AddPlot(System.Windows.Media.Brushes.Transparent, "InvalidatedHit");
		}
		else if (State == State.Configure)
		{
			AddDataSeries(BarsPeriodType.Minute, Math.Max(2, Htf1Minutes)); AddDataSeries(BarsPeriodType.Minute, Math.Max(2, Htf2Minutes)); tf1 = TfName(Htf1Minutes); tf2 = TfName(Htf2Minutes);
			scanTfs.Clear(); int[] want = { ScanTf1, ScanTf2, ScanTf3, ScanTf4, ScanTf5 };
			foreach (int m in want) { if (m < 1 || scanTfs.Contains(m)) continue; scanTfs.Add(m); AddDataSeries(BarsPeriodType.Minute, m); }
		}
		else if (State == State.DataLoaded)
		{
			eng = new CrtEngine { UseA = UseModelA, UseB = UseModelB, ReqColor = RequireColor, Strict = StrictInside, MinScore = MinConfluence };
			bias1 = new CrtBias(); bias2 = new CrtBias(); htf1 = new CrtHtf(); htf2 = new CrtHtf(); historyStart = DateTime.MinValue; labelFont = new SimpleFont("Arial", FontSize);
			stats = new CrtStats(); chartRow = new CrtScanRow(); rows.Clear();
			foreach (int m in scanTfs) rows.Add(new CrtScanRow { Tf = m, E = new CrtEngine { UseA = UseModelA, UseB = UseModelB, ReqColor = RequireColor, Strict = StrictInside, MinScore = 0 } });
		}
	}
	protected override void OnBarUpdate()
	{
		if (eng == null) return;
		if (BarsInProgress == 1) { long e = Times[1][0].Ticks; bias1.Update(e, Highs[1][0], Lows[1][0], Closes[1][0]); htf1.Add(e, Opens[1][0], Highs[1][0], Lows[1][0], Closes[1][0]); return; }
		if (BarsInProgress == 2) { long e = Times[2][0].Ticks; bias2.Update(e, Highs[2][0], Lows[2][0], Closes[2][0]); htf2.Add(e, Opens[2][0], Highs[2][0], Lows[2][0], Closes[2][0]); return; }
		if (BarsInProgress >= 3)
		{
			int ri = BarsInProgress - 3; if (ri >= rows.Count) return; var row = rows[ri];
			if (historyStart == DateTime.MinValue && BarsArray[0].Count > 0) historyStart = BarsArray[0].GetTime(BarsArray[0].Count - 1).Date.AddDays(-(HistoryDays - 1));
			long Tk = Times[BarsInProgress][0].Ticks; htf1.Before(Tk); double a1H = htf1.H, a1L = htf1.L; htf2.Before(Tk);
			var ro = row.E.Step(BarsArray[BarsInProgress].IsFirstBarOfSession, Opens[BarsInProgress][0], Highs[BarsInProgress][0], Lows[BarsInProgress][0], Closes[BarsInProgress][0], Times[BarsInProgress][0] >= historyStart, bias1.At(Tk), bias2.At(Tk), a1H, a1L, htf2.H, htf2.L);
			lock (lk) { row.Feed(ro, CurrentBars[BarsInProgress]); }
			return;
		}
		if (BarsInProgress != 0) return;
		if (historyStart == DateTime.MinValue && Bars.Count > 0) historyStart = Bars.GetTime(Bars.Count - 1).Date.AddDays(-(HistoryDays - 1));
		bool active = Time[0] >= historyStart; long T = Time[0].Ticks;
		htf1.Before(T); htf2.Before(T); double h1H = htf1.H, h1L = htf1.L; htf2.Before(T); double h2H = htf2.H, h2L = htf2.L;
		var o = eng.Step(Bars.IsFirstBarOfSession, Open[0], High[0], Low[0], Close[0], active, bias1.At(T), bias2.At(T), h1H, h1L, h2H, h2L);
		lock (lk) { stats.Feed(o, Close[0]); chartRow.Feed(o, CurrentBar); }
		Values[0][0] = o.Sig; Values[1][0] = o.Score; Values[4][0] = o.TpHit; Values[5][0] = o.SlHit;
		if (o.Sig != 0) { Values[2][0] = o.Tp; Values[3][0] = o.Sl; } else { Values[2].Reset(); Values[3].Reset(); }
		bool sigA = o.Sig == 1, sigB = o.Sig == -1;
		if (o.Sig != 0)
		{
			string id = CurrentBar.ToString(); int sh = SignalTextShift, sp = LabelSpacing; double r1H = o.R1H, r1L = o.R1L;
			string tags = ""; if (ShowConfluenceTags) tags = " " + o.Score + "/4"; string det = "";
			if (o.A1 == 1) det += tf1 + " "; if (o.A4 == 1) det += tf2 + " "; if (o.Swp == 1) det += "SWEEP "; if (o.Pdz == 1) det += (sigA ? "DISC" : "PREM"); det = det.Trim();
			if (sigA)
			{
				Draw.TriangleUp(this, "CRT_BuyTri_" + id, false, 0, Low[0] - 2 * TickSize, BullColor);
				if (ShowCrtText) Draw.Text(this, "CRT_BuyTxt_" + id, false, "CRT" + tags, 0, Low[0], -sp - sh, BullColor, labelFont, System.Windows.TextAlignment.Center, System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.Transparent, 0);
				if (ShowManipLabel) Draw.Text(this, "CRT_BuyMan_" + id, false, "Manipulation", 0, Low[0], (ShowCrtText ? -2 * sp : -sp) - sh, System.Windows.Media.Brushes.White, labelFont, System.Windows.TextAlignment.Center, BullColor, BullColor, 100);
				if (ShowConfluenceTags && det.Length > 0) Draw.Text(this, "CRT_BuyDet_" + id, false, det, 0, Low[0], (ShowCrtText ? -3 * sp : -2 * sp) - sh - 4, BullColor, new SimpleFont("Arial", Math.Max(7, FontSize - 3)), System.Windows.TextAlignment.Center, System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.Transparent, 0);
			}
			else
			{
				Draw.TriangleDown(this, "CRT_SellTri_" + id, false, 0, High[0] + 2 * TickSize, BearColor);
				if (ShowCrtText) Draw.Text(this, "CRT_SellTxt_" + id, false, "CRT" + tags, 0, High[0], sp - sh, BearColor, labelFont, System.Windows.TextAlignment.Center, System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.Transparent, 0);
				if (ShowManipLabel) Draw.Text(this, "CRT_SellMan_" + id, false, "Manipulation", 0, High[0], (ShowCrtText ? 2 * sp : sp) - sh, System.Windows.Media.Brushes.White, labelFont, System.Windows.TextAlignment.Center, BearColor, BearColor, 100);
				if (ShowConfluenceTags && det.Length > 0) Draw.Text(this, "CRT_SellDet_" + id, false, det, 0, High[0], (ShowCrtText ? 3 * sp : 2 * sp) - sh + 4, BearColor, new SimpleFont("Arial", Math.Max(7, FontSize - 3)), System.Windows.TextAlignment.Center, System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.Transparent, 0);
			}
			Draw.Line(this, "CRT_H_" + id, false, 1, r1H, -ExtendBars, r1H, LineColor, DashStyleHelper.Solid, 1);
			Draw.Line(this, "CRT_L_" + id, false, 1, r1L, -ExtendBars, r1L, LineColor, DashStyleHelper.Solid, 1);
			Draw.Text(this, "CRT_HL_" + id, false, sigA ? "CRT High (TP)" : "CRT High", -ExtendBars, r1H, 8 - HLLabelShift, LineColor, labelFont, System.Windows.TextAlignment.Left, System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.Transparent, 0);
			Draw.Text(this, "CRT_LL_" + id, false, sigB ? "CRT Low (TP)" : "CRT Low", -ExtendBars, r1L, -8 - HLLabelShift, LineColor, labelFont, System.Windows.TextAlignment.Left, System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.Transparent, 0);
		}
		if (State != State.Realtime) return;
		string where = Instrument.FullName + " " + BarsPeriod.ToString(); string snd = string.IsNullOrWhiteSpace(AlertSoundFile) ? string.Empty : NinjaTrader.Core.Globals.InstallDir + @"\sounds\" + AlertSoundFile;
		if (AlertOnSignals && o.Sig != 0) Alert("CRTSig" + CurrentBar, Priority.High, "CRT " + (sigA ? "BUY" : "SELL") + " on " + where + "  confluence " + o.Score + "/4", snd, 0, sigA ? System.Windows.Media.Brushes.DarkGreen : System.Windows.Media.Brushes.DarkRed, System.Windows.Media.Brushes.White);
		if (AlertOnTargets)
		{
			if (o.TpHit == 1) Alert("CRTTp" + CurrentBar, Priority.High, "CRT target hit on " + where, snd, 0, System.Windows.Media.Brushes.DarkGreen, System.Windows.Media.Brushes.White);
			if (o.SlHit == 1) Alert("CRTSl" + CurrentBar, Priority.High, "CRT invalidated on " + where, snd, 0, System.Windows.Media.Brushes.DimGray, System.Windows.Media.Brushes.White);
		}
	}

	#region Dashboard
	private sealed class DCell { public string T; public int[] Fg; public int W; public bool Right; }
	private sealed class DRow { public List<DCell> C = new List<DCell>(); public int[] Bg; }
	private static readonly int[] dWhite = { 240, 242, 250 }, dMute = { 150, 158, 175 }, dGreen = { 60, 220, 110 }, dRed = { 255, 90, 100 }, dYel = { 255, 200, 60 }, dHdr = { 44, 84, 160 }, dRow = { 16, 20, 32 }, dRow2 = { 26, 32, 50 };
	private static DCell Dc(string t, int[] fg, int w, bool right) { return new DCell { T = t, Fg = fg, W = w, Right = right }; }
	private DRow Hdr(string title, int width) { var r = new DRow { Bg = dHdr }; r.C.Add(Dc(title, dWhite, width, false)); return r; }
	private List<DRow> BuildDash()
	{
		var L = new List<DRow>(); bool alt = false;
		lock (lk)
		{
			if (ShowScanner)
			{
				var h = Hdr("CRT SCANNER  (score uses " + tf1 + "/" + tf2 + ")", 38); L.Add(h);
				var c = new DRow { Bg = dRow2 }; c.C.Add(Dc("TF", dMute, 7, false)); c.C.Add(Dc("Signal", dMute, 7, false)); c.C.Add(Dc("Ago", dMute, 5, true)); c.C.Add(Dc("Score", dMute, 7, true)); c.C.Add(Dc("Status", dMute, 8, true)); L.Add(c);
				var all = new List<CrtScanRow>(); chartRow.Tf = 0; all.Add(chartRow); all.AddRange(rows);
				foreach (var r in all)
				{
					var d = new DRow { Bg = alt ? dRow2 : dRow }; alt = !alt;
					string tfn = r.Tf == 0 ? "Chart" : TfName(r.Tf); int tfm = r.Tf == 0 ? BarsArray[0].BarsPeriod.Value : r.Tf; bool scoreOk = tfm < Htf1Minutes;
					d.C.Add(Dc(tfn, dWhite, 7, false));
					d.C.Add(r.Dir == 0 ? Dc("-", dMute, 7, false) : Dc(r.Dir == 1 ? "BUY" : "SELL", r.Dir == 1 ? dGreen : dRed, 7, false));
					d.C.Add(Dc(r.Dir == 0 ? "" : r.Ago.ToString(), dWhite, 5, true));
					d.C.Add(Dc(r.Dir == 0 ? "" : (scoreOk ? r.Score + "/4" : "n/a"), dWhite, 7, true));
					d.C.Add(Dc(r.Status, r.Status == "OPEN" ? dYel : (r.Status == "TP" ? dGreen : dMute), 8, true)); L.Add(d);
				}
			}
			if (ShowPerformance)
			{
				if (L.Count > 0) L.Add(new DRow { Bg = dRow });
				L.Add(Hdr("CHART SIGNALS BY SCORE  (last " + HistoryDays + "d)", 38));
				var c = new DRow { Bg = dRow2 }; string[] hd = { "Score", "N", "W", "L", "Win%", "+/-", "AvgR" }; int[] wd = { 6, 4, 4, 4, 6, 5, 6 };
				for (int i = 0; i < hd.Length; i++) c.C.Add(Dc(hd[i], dMute, wd[i], i > 0)); L.Add(c);
				alt = false;
				for (int s = 5; s >= 0; s--)
				{
					int n, w, l; double sr; if (s == 5) { n = w = l = 0; sr = 0; for (int k = 0; k < 5; k++) { n += stats.N[k]; w += stats.W[k]; l += stats.L[k]; sr += stats.SumR[k]; } } else { n = stats.N[s]; w = stats.W[s]; l = stats.L[s]; sr = stats.SumR[s]; }
					var d = new DRow { Bg = alt ? dRow2 : dRow }; alt = !alt; int dn = w + l; string win = "", pm = "", ar = ""; int[] arc = dWhite;
					if (dn > 0) { double p = (double)w / dn; win = (p * 100).ToString("0") + "%"; pm = (196 * Math.Sqrt(p * (1 - p) / dn)).ToString("0"); ar = (sr / dn).ToString("+0.00;-0.00;0.00"); arc = sr >= 0 ? dGreen : dRed; }
					d.C.Add(Dc(s == 5 ? "All" : s + "/4", s == 5 ? dYel : dWhite, wd[0], false)); d.C.Add(Dc(n.ToString(), dWhite, wd[1], true)); d.C.Add(Dc(w.ToString(), dWhite, wd[2], true)); d.C.Add(Dc(l.ToString(), dWhite, wd[3], true));
					d.C.Add(Dc(win, dWhite, wd[4], true)); d.C.Add(Dc(pm, dMute, wd[5], true)); d.C.Add(Dc(ar, arc, wd[6], true)); L.Add(d);
				}
				var f = new DRow { Bg = dRow }; f.C.Add(Dc("Open/replaced signals excluded. Own TP/SL rules,", dMute, 38, false)); L.Add(f);
				var g = new DRow { Bg = dRow }; g.C.Add(Dc("not realistic fills. Small samples = noise.", dMute, 38, false)); L.Add(g);
			}
		}
		return L;
	}
	protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
	{
		base.OnRender(chartControl, chartScale);
		if ((!ShowScanner && !ShowPerformance) || eng == null || RenderTarget == null || ChartPanel == null) return;
		var L = BuildDash(); if (L.Count == 0) return;
		float fs = DashFontSize, cw = fs * 0.62f, rh = fs * 1.7f, pad = 6f; int totalChars = 0;
		foreach (var r in L) { int w = 0; foreach (var c in r.C) w += c.W; if (r.C.Count == 1) w = 0; if (w > totalChars) totalChars = w; }
		if (totalChars < 38) totalChars = 38;
		float W = totalChars * cw + 2 * pad, H = L.Count * rh; float x0, y0 = 8f;
		bool right = DashPosition == SneakyCrtPos.TopRight || DashPosition == SneakyCrtPos.BottomRight, bottom = DashPosition == SneakyCrtPos.BottomLeft || DashPosition == SneakyCrtPos.BottomRight;
		x0 = right ? ChartPanel.X + ChartPanel.W - W - 70f : ChartPanel.X + 8f; y0 = bottom ? ChartPanel.Y + ChartPanel.H - H - 30f : ChartPanel.Y + 8f;
		float op = DashOpacity / 100f; var cache = new Dictionary<int, SharpDX.Direct2D1.SolidColorBrush>();
		Func<int[], float, SharpDX.Direct2D1.SolidColorBrush> br = delegate (int[] c, float a) { int key = (c[0] << 16) | (c[1] << 8) | c[2] | ((int)(a * 100) << 24); SharpDX.Direct2D1.SolidColorBrush b; if (!cache.TryGetValue(key, out b)) { b = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(c[0] / 255f, c[1] / 255f, c[2] / 255f, a)); cache[key] = b; } return b; };
		var tf = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", fs);
		try
		{
			tf.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap; tf.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
			float y = y0;
			foreach (var r in L)
			{
				RenderTarget.FillRectangle(new SharpDX.RectangleF(x0, y, W, rh), br(r.Bg, op));
				float x = x0 + pad;
				foreach (var c in r.C)
				{
					float w = c.W * cw; if (r.C.Count == 1) w = W - 2 * pad;
					tf.TextAlignment = c.Right ? SharpDX.DirectWrite.TextAlignment.Trailing : SharpDX.DirectWrite.TextAlignment.Leading;
					RenderTarget.DrawText(c.T, tf, new SharpDX.RectangleF(x, y, w, rh), br(c.Fg, 1f)); x += w;
				}
				y += rh;
			}
		}
		finally { tf.Dispose(); foreach (var b in cache.Values) b.Dispose(); }
	}
	#endregion
	#region Properties
	[Range(1, 30)] [Display(Name = "Show history (calendar days)", Order = 1, GroupName = "1. History")] public int HistoryDays { get; set; }
	[Display(Name = "Model A: bullish (red range, sweep low, close back inside)", Order = 1, GroupName = "2. CRT")] public bool UseModelA { get; set; }
	[Display(Name = "Model B: bearish (green range, sweep high, close back inside)", Order = 2, GroupName = "2. CRT")] public bool UseModelB { get; set; }
	[Display(Name = "Require candle color (green for A, red for B)", Order = 3, GroupName = "2. CRT")] public bool RequireColor { get; set; }
	[Display(Name = "Strict: sweep candle must not break the opposite side", Order = 4, GroupName = "2. CRT")] public bool StrictInside { get; set; }
	[Range(1, 500)] [Display(Name = "Extend lines (bars)", Order = 5, GroupName = "2. CRT")] public int ExtendBars { get; set; }
	[Range(2, 1440)] [Display(Name = "HTF 1 minutes (bias + sweep level)", Order = 1, GroupName = "3. Higher-timeframe confluence", Description = "Must be larger than the chart timeframe. Default 60.")] public int Htf1Minutes { get; set; }
	[Range(2, 1440)] [Display(Name = "HTF 2 minutes (bias + discount/premium range)", Order = 2, GroupName = "3. Higher-timeframe confluence", Description = "Must be larger than HTF 1. Default 240.")] public int Htf2Minutes { get; set; }
	[Range(0, 4)] [Display(Name = "Min confluence to show signal (0 = all)", Order = 3, GroupName = "3. Higher-timeframe confluence")] public int MinConfluence { get; set; }
	[Display(Name = "Show score and which confluences hit", Order = 4, GroupName = "3. Higher-timeframe confluence")] public bool ShowConfluenceTags { get; set; }
	[XmlIgnore] [Display(Name = "Bullish color", Order = 1, GroupName = "4. Look")] public System.Windows.Media.Brush BullColor { get; set; }
	[Browsable(false)] public string BullColorSerializable { get { return Serialize.BrushToString(BullColor); } set { BullColor = Serialize.StringToBrush(value); } }
	[XmlIgnore] [Display(Name = "Bearish color", Order = 2, GroupName = "4. Look")] public System.Windows.Media.Brush BearColor { get; set; }
	[Browsable(false)] public string BearColorSerializable { get { return Serialize.BrushToString(BearColor); } set { BearColor = Serialize.StringToBrush(value); } }
	[XmlIgnore] [Display(Name = "CRT H/L line color", Order = 3, GroupName = "4. Look")] public System.Windows.Media.Brush LineColor { get; set; }
	[Browsable(false)] public string LineColorSerializable { get { return Serialize.BrushToString(LineColor); } set { LineColor = Serialize.StringToBrush(value); } }
	[Range(6, 40)] [Display(Name = "Font size", Order = 4, GroupName = "4. Look")] public int FontSize { get; set; }
	[Display(Name = "Show CRT text", Order = 5, GroupName = "4. Look")] public bool ShowCrtText { get; set; }
	[Display(Name = "Show Manipulation label", Order = 6, GroupName = "4. Look")] public bool ShowManipLabel { get; set; }
	[Range(-500, 500)] [Display(Name = "Signal text shift (px, + = lower)", Order = 7, GroupName = "4. Look")] public int SignalTextShift { get; set; }
	[Range(0, 200)] [Display(Name = "Signal text spacing (px)", Order = 8, GroupName = "4. Look")] public int LabelSpacing { get; set; }
	[Range(-500, 500)] [Display(Name = "CRT High/Low label shift (px, + = lower)", Order = 9, GroupName = "4. Look")] public int HLLabelShift { get; set; }
	[Display(Name = "Alert on signals", Order = 1, GroupName = "5. Alerts")] public bool AlertOnSignals { get; set; }
	[Display(Name = "Alert on target hit / invalidation", Order = 2, GroupName = "5. Alerts")] public bool AlertOnTargets { get; set; }
	[Display(Name = "Sound file (NinjaTrader\\sounds, empty = none)", Order = 3, GroupName = "5. Alerts")] public string AlertSoundFile { get; set; }
	[Display(Name = "Show multi-timeframe scanner", Order = 1, GroupName = "6. Dashboard")] public bool ShowScanner { get; set; }
	[Display(Name = "Show performance by score", Order = 2, GroupName = "6. Dashboard")] public bool ShowPerformance { get; set; }
	[Display(Name = "Position", Order = 3, GroupName = "6. Dashboard")] public SneakyCrtPos DashPosition { get; set; }
	[Range(7, 20)] [Display(Name = "Font size", Order = 4, GroupName = "6. Dashboard")] public int DashFontSize { get; set; }
	[Range(10, 100)] [Display(Name = "Opacity %", Order = 5, GroupName = "6. Dashboard")] public int DashOpacity { get; set; }
	[Range(0, 1440)] [Display(Name = "Scan timeframe 1 (min, 0 = off)", Order = 6, GroupName = "6. Dashboard")] public int ScanTf1 { get; set; }
	[Range(0, 1440)] [Display(Name = "Scan timeframe 2 (min, 0 = off)", Order = 7, GroupName = "6. Dashboard")] public int ScanTf2 { get; set; }
	[Range(0, 1440)] [Display(Name = "Scan timeframe 3 (min, 0 = off)", Order = 8, GroupName = "6. Dashboard")] public int ScanTf3 { get; set; }
	[Range(0, 1440)] [Display(Name = "Scan timeframe 4 (min, 0 = off)", Order = 9, GroupName = "6. Dashboard")] public int ScanTf4 { get; set; }
	[Range(0, 1440)] [Display(Name = "Scan timeframe 5 (min, 0 = off)", Order = 10, GroupName = "6. Dashboard")] public int ScanTf5 { get; set; }
	#endregion
}

}
