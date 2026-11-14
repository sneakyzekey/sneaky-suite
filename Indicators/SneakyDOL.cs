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

// SneakyDOL - clean-room "Draw on Liquidity" ranker. Not the paid TradeAxis script; built from the public idea + an open-source scoring design.
// UNPROVEN: on 27 sessions of NQ the picked level is touched a bit more than similar-distance levels, but price does not drift toward it.
namespace NinjaTrader.NinjaScript.Indicators
{
// ===== SneakyDOL core (pure C#, no NinjaTrader types) — mirrors ref.py =====
public class DolP
{
	public int PivLen = 5; public bool UseFvg = true; public double FvgMinAtr = 0.25, EqTolAtr = 0.15; public int MaxPool = 40, MaxAge = 1500;
	public int AtrLen = 14; public double MaxAtr = 6.0; public int HalfLife = 150; public double ConfTol = 0.3;
	public double WProx = 35, WFresh = 20, WConf = 25, WDens = 20, MinScore = 40, Hyst = 8; public int IfvgMaxCloses = 2;
}
public class DolLv { public int Id; public double Top, Bot; public int Born, Upd, Created; public string Kind; public int Eq; public double Score; }
public class DolIfvg { public double Top, Bot; public int Born; public int Dir; public bool Alive; public int N; public int Id; }
public class DolGhost { public int Born; public double Mid; public int End; public bool Swept; }

// BOS bias of one series (pivots 3/3). At(T) = bias at the close of the last bar whose end time is strictly before T (no lookahead).
public class DolBias
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

public class DolOut
{
	public int Tid; public double Mid = double.NaN, Top = double.NaN, Bot = double.NaN, Score = double.NaN; public int Dir; public string Kind = "";
	public int New, Swept, IfNew; public DolGhost Ghost; public int Born;
}

public class DolEngine
{
	public DolP P; public System.Collections.Generic.List<DolLv> Lv = new System.Collections.Generic.List<DolLv>(); public System.Collections.Generic.List<DolIfvg> Ifv = new System.Collections.Generic.List<DolIfvg>();
	int nid = 0, tid = -1, tborn = -1, ifId = 0; double tmid = double.NaN, tscore = double.NaN, ttop = double.NaN, tbot = double.NaN; string tkind = ""; int tdir = 0;
	int i = -1;
	System.Collections.Generic.List<double> O = new System.Collections.Generic.List<double>(), H = new System.Collections.Generic.List<double>(), L = new System.Collections.Generic.List<double>(), C = new System.Collections.Generic.List<double>(), Trs = new System.Collections.Generic.List<double>();
	public double Atr = double.NaN, Pdh = double.NaN, Pdl = double.NaN, DOpen = double.NaN, DayH = double.NaN, DayL = double.NaN;
	public DolEngine(DolP p) { P = p; }

	public DolOut Step(bool newDay, double o, double h, double l, double c, int biasH, int biasS)
	{
		var p = P; i++; O.Add(o); H.Add(h); L.Add(l); C.Add(c);
		if (newDay) { if (!double.IsNaN(DayH)) { Pdh = DayH; Pdl = DayL; } DayH = h; DayL = l; DOpen = o; }
		else { DayH = double.IsNaN(DayH) ? h : System.Math.Max(DayH, h); DayL = double.IsNaN(DayL) ? l : System.Math.Min(DayL, l); if (double.IsNaN(DOpen)) DOpen = o; }
		double tr = i == 0 ? h - l : System.Math.Max(h - l, System.Math.Max(System.Math.Abs(h - C[i - 1]), System.Math.Abs(l - C[i - 1]))); Trs.Add(tr);
		if (i + 1 == p.AtrLen) { double s = 0; for (int q = 0; q < Trs.Count; q++) s += Trs[q]; Atr = s / p.AtrLen; }
		else if (i + 1 > p.AtrLen) Atr = (Atr * (p.AtrLen - 1) + tr) / p.AtrLen;
		var res = new DolOut(); int nw = 0, sw = 0, ifnew = 0; DolGhost ghost = null;
		if (!double.IsNaN(Atr))
		{
			double atr = Atr, tol = p.EqTolAtr * atr; int pv = p.PivLen; double ph = double.NaN, pl = double.NaN;
			if (i >= 2 * pv)
			{
				int k = i - pv; double hk = H[k], lk = L[k]; bool a = true, b = true;
				for (int j = k - pv; j < k; j++) { if (!(hk > H[j])) a = false; if (!(lk < L[j])) b = false; }
				for (int j = k + 1; j <= i; j++) { if (!(hk >= H[j])) a = false; if (!(lk <= L[j])) b = false; }
				if (a) ph = hk; if (b) pl = lk;
			}
			if (!double.IsNaN(ph)) AddSwing(ph, "BSL", tol, pv);
			if (!double.IsNaN(pl)) AddSwing(pl, "SSL", tol, pv);
			if (p.UseFvg && i >= 2)
			{
				if (l > H[i - 2] && (l - H[i - 2]) >= p.FvgMinAtr * atr) AddFvg(l, H[i - 2], "Bull FVG");
				if (h < L[i - 2] && (L[i - 2] - h) >= p.FvgMinAtr * atr) AddFvg(L[i - 2], h, "Bear FVG");
			}
			while (Lv.Count > p.MaxPool) Lv.RemoveAt(0);
			for (int q = 0; q < Ifv.Count; q++)
			{
				var z = Ifv[q];
				if (z.Alive && System.Math.Min(o, c) >= z.Bot && System.Math.Max(o, c) <= z.Top) { z.N++; if (z.N >= p.IfvgMaxCloses) z.Alive = false; }
			}
			DolLv best = null, cur = null; bool sweptf = false; double wsum = System.Math.Max(p.WProx + p.WFresh + p.WConf + p.WDens, 1e-9);
			for (int idx = Lv.Count - 1; idx >= 0; idx--)
			{
				var v = Lv[idx]; bool touched = i > v.Created && h >= v.Bot && l <= v.Top;
				if (touched || i - v.Born > p.MaxAge)
				{
					if (touched && (v.Kind == "Bull FVG" || v.Kind == "Bear FVG"))
					{
						bool flipped = (v.Kind == "Bull FVG" && c < v.Bot) || (v.Kind == "Bear FVG" && c > v.Top);
						if (flipped) { int dd = v.Kind == "Bull FVG" ? -1 : 1; Ifv.Add(new DolIfvg { Top = v.Top, Bot = v.Bot, Born = i, Dir = dd, Alive = true, N = 0, Id = ifId++ }); ifnew = dd; }
					}
					if (touched && v.Id == tid) sweptf = true;
					Lv.RemoveAt(idx); continue;
				}
				bool above = v.Bot > c; double dist = above ? (v.Bot - c) / atr : (c - v.Top) / atr;
				double prox = System.Math.Max(0.0, 1.0 - dist / p.MaxAtr); double fresh = System.Math.Pow(0.5, (i - v.Upd) / (double)p.HalfLife);
				double mid = (v.Top + v.Bot) / 2, t2 = p.ConfTol * atr, cc = 0.0;
				if (!double.IsNaN(Pdh) && System.Math.Abs(mid - Pdh) <= t2) cc += 1.0;
				if (!double.IsNaN(Pdl) && System.Math.Abs(mid - Pdl) <= t2) cc += 1.0;
				if (!double.IsNaN(DOpen) && System.Math.Abs(mid - DOpen) <= t2) cc += 1.0;
				if ((above && biasH == 1) || (!above && biasH == -1)) cc += 1.0;
				if ((above && biasS == 1) || (!above && biasS == -1)) cc += 0.5;
				double conf = System.Math.Min(1.0, cc / 2.0);
				double dens = (v.Kind == "BSL" || v.Kind == "SSL") ? System.Math.Min(1.0, v.Eq / 3.0) : 0.25;
				v.Score = 100.0 * (p.WProx * prox + p.WFresh * fresh + p.WConf * conf + p.WDens * dens) / wsum;
				if (dist <= p.MaxAtr) { if (best == null || v.Score > best.Score) best = v; if (v.Id == tid) cur = v; }
			}
			DolLv pick = best;
			if (cur != null && best != null && best.Id != cur.Id && best.Score < cur.Score + p.Hyst) pick = cur;
			if (pick != null && pick.Score < p.MinScore) pick = null;
			int pid = pick == null ? -1 : pick.Id;
			if (sweptf) sw = 1;
			if (pid != tid)
			{
				if (tid != -1 && tborn >= 0) ghost = new DolGhost { Born = tborn, Mid = tmid, End = i, Swept = sweptf };
				if (pid != -1) nw = 1;
				tid = pid;
			}
			if (pick != null) { tmid = (pick.Top + pick.Bot) / 2; tborn = pick.Born; tscore = pick.Score; tkind = pick.Kind; ttop = pick.Top; tbot = pick.Bot; tdir = pick.Bot > c ? 1 : -1; }
			else { tscore = double.NaN; tkind = ""; tdir = 0; }
		}
		res.Tid = tid; res.Mid = tid != -1 ? tmid : double.NaN; res.Score = tid != -1 ? tscore : double.NaN; res.Dir = tid != -1 ? tdir : 0; res.Kind = tid != -1 ? tkind : "";
		res.Top = tid != -1 ? ttop : double.NaN; res.Bot = tid != -1 ? tbot : double.NaN; res.Born = tborn; res.New = nw; res.Swept = sw; res.IfNew = ifnew; res.Ghost = ghost;
		return res;
	}
	void AddSwing(double price, string kind, double tol, int pv)
	{
		for (int q = 0; q < Lv.Count; q++) { var v = Lv[q]; if (v.Kind == kind && price >= v.Bot - tol && price <= v.Top + tol) { v.Top = System.Math.Max(v.Top, price); v.Bot = System.Math.Min(v.Bot, price); v.Eq++; v.Upd = i - pv; return; } }
		Lv.Add(new DolLv { Id = nid++, Top = price, Bot = price, Born = i - pv, Upd = i - pv, Created = i, Kind = kind, Eq = 0, Score = 0 });
	}
	void AddFvg(double top, double bot, string kind) { Lv.Add(new DolLv { Id = nid++, Top = top, Bot = bot, Born = i - 1, Upd = i - 1, Created = i, Kind = kind, Eq = 0, Score = 0 }); }
}

public enum DolMode { Swing, DayTrading, Scalping }
public class SneakyDOL : Indicator
{
	DolEngine eng; DolBias bH, bS, bE; DolP dp;
	System.Collections.Generic.Queue<string> ghostTags = new System.Collections.Generic.Queue<string>(); int ghostSeq; string tfH = "", tfS = "", tfE = "";
	System.Collections.Generic.HashSet<int> ifDrawn = new System.Collections.Generic.HashSet<int>(); bool hadTarget;
	int biasHv, biasSv, biasEv; DolOut last;
	protected override void OnStateChange()
	{
		if (State == State.SetDefaults)
		{
			Description = "Open 'Draw on Liquidity' ranker (clean-room, NOT the paid TradeAxis script). Pools swing highs/lows (equal levels merged) and fair value gaps, scores each on proximity/freshness/confluence/density, and boxes the top one as the DOL target. UNPROVEN: no demonstrated forward edge. Hidden plots: 3 TargetMid, 4 TargetDir (+1 above price, -1 below), 5 Score, 6 NewTarget(1), 7 Swept(1), 8 iFVG (+1 bullish, -1 bearish).";
			Name = "SneakyDOL"; Calculate = Calculate.OnBarClose; IsOverlay = true; DisplayInDataBox = true; PaintPriceMarkers = false; IsSuspendedWhileInactive = true;
			Mode = DolMode.DayTrading; PivLen = 5; UseFvg = true; FvgMinAtr = 0.25; EqTolAtr = 0.15; MaxPool = 40; MaxAge = 1500; AtrLen = 14; MaxAtr = 6.0; HalfLife = 150; ConfTol = 0.3;
			WProx = 35; WFresh = 20; WConf = 25; WDens = 20; MinScore = 40; Hyst = 8; ShowGhost = true; MaxGhost = 10; ShowAnchors = true; ShowTable = true; ShowIfvg = true; IfvgCloses = 2; IfvgMaxAge = 400; AlertsOn = true; DrawBarsBack = 3000;
			BuyCol = System.Windows.Media.Brushes.Cyan; SellCol = System.Windows.Media.Brushes.HotPink;
			AddPlot(new Stroke(System.Windows.Media.Brushes.Gray, 1), PlotStyle.Hash, "PDH"); AddPlot(new Stroke(System.Windows.Media.Brushes.Gray, 1), PlotStyle.Hash, "PDL"); AddPlot(new Stroke(System.Windows.Media.Brushes.Orange, 1), PlotStyle.Hash, "DailyOpen");
			AddPlot(System.Windows.Media.Brushes.Transparent, "TargetMid"); AddPlot(System.Windows.Media.Brushes.Transparent, "TargetDir"); AddPlot(System.Windows.Media.Brushes.Transparent, "TargetScore");
			AddPlot(System.Windows.Media.Brushes.Transparent, "NewTarget"); AddPlot(System.Windows.Media.Brushes.Transparent, "Swept"); AddPlot(System.Windows.Media.Brushes.Transparent, "IFVG");
		}
		else if (State == State.Configure)
		{
			if (Mode == DolMode.Swing) { AddDataSeries(BarsPeriodType.Week, 1); AddDataSeries(BarsPeriodType.Day, 1); AddDataSeries(BarsPeriodType.Minute, 240); tfH = "Weekly"; tfS = "Daily"; tfE = "4H"; }
			else if (Mode == DolMode.Scalping) { AddDataSeries(BarsPeriodType.Minute, 60); AddDataSeries(BarsPeriodType.Minute, 15); AddDataSeries(BarsPeriodType.Minute, 5); tfH = "1H"; tfS = "15m"; tfE = "5m"; }
			else { AddDataSeries(BarsPeriodType.Minute, 240); AddDataSeries(BarsPeriodType.Minute, 60); AddDataSeries(BarsPeriodType.Minute, 15); tfH = "4H"; tfS = "1H"; tfE = "15m"; }
		}
		else if (State == State.DataLoaded)
		{
			dp = new DolP { PivLen = PivLen, UseFvg = UseFvg, FvgMinAtr = FvgMinAtr, EqTolAtr = EqTolAtr, MaxPool = MaxPool, MaxAge = MaxAge, AtrLen = AtrLen, MaxAtr = MaxAtr, HalfLife = HalfLife, ConfTol = ConfTol, WProx = WProx, WFresh = WFresh, WConf = WConf, WDens = WDens, MinScore = MinScore, Hyst = Hyst, IfvgMaxCloses = IfvgCloses };
			eng = new DolEngine(dp); bH = new DolBias(); bS = new DolBias(); bE = new DolBias(); ghostTags.Clear(); ifDrawn.Clear(); ghostSeq = 0; hadTarget = false; last = null;
		}
	}
	static string BTxt(int b) { return b == 1 ? "Bullish" : b == -1 ? "Bearish" : "Neutral"; }
	protected override void OnBarUpdate()
	{
		if (eng == null) return;
		if (BarsInProgress == 1) { bH.Update(Times[1][0].Ticks, Highs[1][0], Lows[1][0], Closes[1][0]); return; }
		if (BarsInProgress == 2) { bS.Update(Times[2][0].Ticks, Highs[2][0], Lows[2][0], Closes[2][0]); return; }
		if (BarsInProgress == 3) { bE.Update(Times[3][0].Ticks, Highs[3][0], Lows[3][0], Closes[3][0]); return; }
		if (BarsInProgress != 0) return;
		long T = Time[0].Ticks; biasHv = bH.At(T); biasSv = bS.At(T); biasEv = bE.At(T);
		var o = eng.Step(Bars.IsFirstBarOfSession, Open[0], High[0], Low[0], Close[0], biasHv, biasSv); last = o;
		if (!ShowAnchors || double.IsNaN(eng.Pdh)) Values[0].Reset(); else Values[0][0] = eng.Pdh; if (!ShowAnchors || double.IsNaN(eng.Pdl)) Values[1].Reset(); else Values[1][0] = eng.Pdl; if (!ShowAnchors || double.IsNaN(eng.DOpen)) Values[2].Reset(); else Values[2][0] = eng.DOpen;
		if (o.Tid != -1) { Values[3][0] = o.Mid; Values[4][0] = o.Dir; Values[5][0] = o.Score; } else { Values[3].Reset(); Values[4][0] = 0; Values[5].Reset(); }
		Values[6][0] = o.New; Values[7][0] = o.Swept; Values[8][0] = o.IfNew;
		bool inWin = CurrentBar > Count - DrawBarsBack - 2;
		if (inWin)
		{
			if (o.Tid != -1)
			{
				var col = o.Dir == 1 ? BuyCol : SellCol; double atr = eng.Atr; double pad = System.Math.Max(0.0, atr * 0.06 - (o.Top - o.Bot) / 2.0);
				Draw.Rectangle(this, "DOLBox", false, CurrentBar - o.Born, o.Top + pad, -15, o.Bot - pad, col, col, 18);
				Draw.Text(this, "DOLLbl", false, "DOL " + o.Kind + " " + o.Score.ToString("0"), -15, o.Mid, 0, System.Windows.Media.Brushes.White, new SimpleFont("Arial", 9), System.Windows.TextAlignment.Left, col, col, 60);
				hadTarget = true;
			}
			else if (hadTarget) { RemoveDrawObject("DOLBox"); RemoveDrawObject("DOLLbl"); hadTarget = false; }
			if (o.Ghost != null && ShowGhost)
			{
				string tg = "DOLG" + (ghostSeq++); Draw.Line(this, tg, false, CurrentBar - o.Ghost.Born, o.Ghost.Mid, 0, o.Ghost.Mid, System.Windows.Media.Brushes.Gray, o.Ghost.Swept ? DashStyleHelper.Solid : DashStyleHelper.Dot, 1);
				ghostTags.Enqueue(tg); while (ghostTags.Count > MaxGhost) RemoveDrawObject(ghostTags.Dequeue());
			}
			if (ShowIfvg)
			{
				while (eng.Ifv.Count > 30) { RemoveDrawObject("DOLIF" + eng.Ifv[0].Id); ifDrawn.Remove(eng.Ifv[0].Id); eng.Ifv.RemoveAt(0); }
				for (int q = 0; q < eng.Ifv.Count; q++)
				{
					var z = eng.Ifv[q]; string tg = "DOLIF" + z.Id;
					if (z.Alive && CurrentBar - z.Born > IfvgMaxAge) z.Alive = false;
					if (z.Alive) { var col = z.Dir == 1 ? BuyCol : SellCol; Draw.Rectangle(this, tg, false, CurrentBar - z.Born, z.Top, -5, z.Bot, col, col, 30); ifDrawn.Add(z.Id); }
					else if (ifDrawn.Contains(z.Id)) { RemoveDrawObject(tg); ifDrawn.Remove(z.Id); }
				}
			}
		}
		if (ShowTable && (CurrentBar >= Count - 2 || State == State.Realtime))
		{
			string t = "OPEN DOL (" + (Mode == DolMode.Swing ? "Swing" : Mode == DolMode.Scalping ? "Scalping" : "Day Trading") + ")\nHTF " + tfH + ": " + BTxt(biasHv) + "\nSTF " + tfS + ": " + BTxt(biasSv) + "\nExec " + tfE + ": " + BTxt(biasEv) + "\nDOL: " + (o.Tid == -1 ? "none" : o.Kind + " " + o.Score.ToString("0") + (o.Dir == 1 ? " above " : " below ") + o.Mid.ToString("0.00"));
			Draw.TextFixed(this, "DOLTbl", t, TextPosition.TopRight);
		}
		if (State == State.Realtime && AlertsOn)
		{
			if (o.New == 1) Alert("DOLnew" + CurrentBar, Priority.Medium, "New DOL target: " + o.Kind + " @ " + o.Mid.ToString("0.00") + " (score " + o.Score.ToString("0") + ")", "", 10, System.Windows.Media.Brushes.Black, System.Windows.Media.Brushes.Cyan);
			if (o.Swept == 1) Alert("DOLswept" + CurrentBar, Priority.Medium, "DOL target swept", "", 10, System.Windows.Media.Brushes.Black, System.Windows.Media.Brushes.Cyan);
			if (o.IfNew != 0) Alert("DOLifvg" + CurrentBar, Priority.Low, "New " + (o.IfNew == 1 ? "bullish" : "bearish") + " iFVG", "", 10, System.Windows.Media.Brushes.Black, System.Windows.Media.Brushes.Cyan);
		}
	}
	#region Properties
	[Display(Name = "Trading mode", Order = 0, GroupName = "1. Mode (HTF / STF / Exec)", Description = "Swing: W/D/4H. Day Trading: 4H/1H/15m. Scalping: 1H/15m/5m. Keep the chart timeframe at or below Exec.")] public DolMode Mode { get; set; }
	[Range(2, 50)] [Display(Name = "Swing pivot length", Order = 0, GroupName = "2. Liquidity levels")] public int PivLen { get; set; }
	[Display(Name = "Include fair value gaps", Order = 1, GroupName = "2. Liquidity levels")] public bool UseFvg { get; set; }
	[Range(0.0, 5.0)] [Display(Name = "Min FVG size (x ATR)", Order = 2, GroupName = "2. Liquidity levels")] public double FvgMinAtr { get; set; }
	[Range(0.0, 2.0)] [Display(Name = "Equal highs/lows tolerance (x ATR)", Order = 3, GroupName = "2. Liquidity levels")] public double EqTolAtr { get; set; }
	[Range(5, 100)] [Display(Name = "Max levels in pool", Order = 4, GroupName = "2. Liquidity levels")] public int MaxPool { get; set; }
	[Range(50, 20000)] [Display(Name = "Max level age (bars)", Order = 5, GroupName = "2. Liquidity levels")] public int MaxAge { get; set; }
	[Range(1, 200)] [Display(Name = "ATR length", Order = 0, GroupName = "3. Scoring")] public int AtrLen { get; set; }
	[Range(0.5, 50.0)] [Display(Name = "Max ATR distance", Order = 1, GroupName = "3. Scoring")] public double MaxAtr { get; set; }
	[Range(5, 5000)] [Display(Name = "Freshness half-life (bars)", Order = 2, GroupName = "3. Scoring")] public int HalfLife { get; set; }
	[Range(0.0, 5.0)] [Display(Name = "Confluence tolerance (x ATR)", Order = 3, GroupName = "3. Scoring")] public double ConfTol { get; set; }
	[Range(0.0, 100.0)] [Display(Name = "Weight: proximity", Order = 4, GroupName = "3. Scoring")] public double WProx { get; set; }
	[Range(0.0, 100.0)] [Display(Name = "Weight: freshness", Order = 5, GroupName = "3. Scoring")] public double WFresh { get; set; }
	[Range(0.0, 100.0)] [Display(Name = "Weight: confluence", Order = 6, GroupName = "3. Scoring")] public double WConf { get; set; }
	[Range(0.0, 100.0)] [Display(Name = "Weight: density", Order = 7, GroupName = "3. Scoring")] public double WDens { get; set; }
	[Range(0.0, 100.0)] [Display(Name = "Min score to show target", Order = 8, GroupName = "3. Scoring")] public double MinScore { get; set; }
	[Range(0.0, 100.0)] [Display(Name = "Switch threshold (score pts)", Order = 9, GroupName = "3. Scoring")] public double Hyst { get; set; }
	[Display(Name = "Show ghost trail", Order = 0, GroupName = "4. Visuals")] public bool ShowGhost { get; set; }
	[Range(1, 50)] [Display(Name = "Max ghost lines", Order = 1, GroupName = "4. Visuals")] public int MaxGhost { get; set; }
	[Display(Name = "Show PDH / PDL / daily open", Order = 2, GroupName = "4. Visuals")] public bool ShowAnchors { get; set; }
	[Display(Name = "Show bias table", Order = 3, GroupName = "4. Visuals")] public bool ShowTable { get; set; }
	[Display(Name = "Show inversion FVGs", Order = 4, GroupName = "4. Visuals")] public bool ShowIfvg { get; set; }
	[Range(1, 20)] [Display(Name = "iFVG removed after N body closes inside", Order = 5, GroupName = "4. Visuals")] public int IfvgCloses { get; set; }
	[Range(10, 20000)] [Display(Name = "iFVG removed after N bars", Order = 6, GroupName = "4. Visuals")] public int IfvgMaxAge { get; set; }
	[Range(100, 200000)] [Display(Name = "Draw bars back", Order = 7, GroupName = "4. Visuals")] public int DrawBarsBack { get; set; }
	[XmlIgnore] [Display(Name = "Buy-side target color (above price)", Order = 8, GroupName = "4. Visuals")] public System.Windows.Media.Brush BuyCol { get; set; }
	[Browsable(false)] public string BuyColSerializable { get { return Serialize.BrushToString(BuyCol); } set { BuyCol = Serialize.StringToBrush(value); } }
	[XmlIgnore] [Display(Name = "Sell-side target color (below price)", Order = 9, GroupName = "4. Visuals")] public System.Windows.Media.Brush SellCol { get; set; }
	[Browsable(false)] public string SellColSerializable { get { return Serialize.BrushToString(SellCol); } set { SellCol = Serialize.StringToBrush(value); } }
	[Display(Name = "Alerts (realtime)", Order = 0, GroupName = "5. Alerts")] public bool AlertsOn { get; set; }
	#endregion
}

}
