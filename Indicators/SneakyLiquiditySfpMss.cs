// SneakyLiquiditySfpMss: NinjaTrader 8 port of the TradingView script "Strong SFP MSS" (ProjectSyndicate).
// Price-only logic (pivot liquidity pools -> SWEEP / SFP / MSS classification, strength score, tracked signals with honest outcome stats).
// Source license is not stated in the file I was given; this port is for the user's own use. Credit to the original author.
// Plots (data only): 0 NearestSellSide 1 NearestBuySide 2 SignalStrength(+long/-short) 3 SignalDir(+1/-1 on the bar a tracked signal opens) 4 EventCode(thesis*(kind+1): SWEEP=1 SFP=2 MSS=3) 5 OpenSignals 6 SignalEntry 7 SignalStop 8 SignalTarget (prices, only on the bar a tracked signal opens; 0 otherwise)
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
// ---- SneakySfpCore: pure logic (no NinjaTrader types) ----
internal class SfpLevel { public int Id, Dir, PBar, Sess, Touches, PierceBar; public double Price, Score, Extreme; public bool Pierced; }
internal class SfpSig { public int Id, Dir, Kind, Sess, Bucket, EBar; public double Entry, Target, Invalid, Risk, Mfe, Mae; }
internal struct SfpEvent { public int Kind, Thesis, Dir, Sess, Touches, PBar, PierceBar, LevelId, SigId; public double Price, Extreme, DepthAtr, Strength, Atr; public bool WasPierced, SigOpened; public double Entry, Target, Invalid; }
internal struct SfpResolve { public int SigId, Kind, Dir, Res, Bars; public double Rr, ExitPx, Mfe, Mae; }
internal class SneakySfpCore
{
	// inputs
	public int Prd = 10, MaxAct = 5, MaxAge = 0, Patience = 6, MaxHold = 60;
	public bool EqMerge = true, Structural = true;
	public double EqTol = 0.25, MssDisp = 0, MinStr = 3, Buf = 0.15, MinRisk = 0.5, TgtR = 1.5, InvU = 1.0, MinTick = 0.25;
	// state
	public readonly System.Collections.Generic.List<SfpLevel> Lvls = new System.Collections.Generic.List<SfpLevel>();
	public readonly System.Collections.Generic.List<SfpSig> Sigs = new System.Collections.Generic.List<SfpSig>();
	public int[] EvKind = new int[3]; public double[] EvDepth = new double[3], EvAge = new double[3];
	public int[] SesLvl = new int[5], SesEv = new int[15], SesSigN = new int[5], SesSigW = new int[5];
	public int NCreated, NExpired, NMerged, NEvBull, NEvBear;
	public int[] KN = new int[3], KW = new int[3], KTO = new int[3]; public double[] KBar = new double[3], KMfe = new double[3], KMae = new double[3];
	public int[] SN = new int[2], SW = new int[2], BN = new int[3], BW = new int[3];
	public int TotN, TotW, TotTO, Streak; public double TotR, SumWinR, SumLossR, EqPeak, MaxDD;
	public int LastSigBar = -1, LastSigDir, LastSigKind; public double LastSigSc = double.NaN;
	// per-bar outputs
	public readonly System.Collections.Generic.List<SfpEvent> Events = new System.Collections.Generic.List<SfpEvent>();
	public readonly System.Collections.Generic.List<SfpResolve> Resolved = new System.Collections.Generic.List<SfpResolve>();
	public readonly System.Collections.Generic.List<SfpLevel> Created = new System.Collections.Generic.List<SfpLevel>();
	public readonly System.Collections.Generic.List<int> DroppedIds = new System.Collections.Generic.List<int>();
	public readonly System.Collections.Generic.List<SfpSig> OpenedSigs = new System.Collections.Generic.List<SfpSig>();
	public bool NewLvlBull, NewLvlBear, SigNow, SigStrongNow, OutTgtNow, OutInvNow, EvXBull, EvXBear, EvSfpBull, EvSfpBear, EvMssBull, EvMssBear;
	public double BestSigSc, Atr, AtrSafe, VolRatio; public bool HasVol;

	readonly System.Collections.Generic.List<double> hi = new System.Collections.Generic.List<double>(), lo = new System.Collections.Generic.List<double>(),
		cl = new System.Collections.Generic.List<double>(), vr = new System.Collections.Generic.List<double>(), wu = new System.Collections.Generic.List<double>(), wd = new System.Collections.Generic.List<double>(),
		vols = new System.Collections.Generic.List<double>();
	readonly System.Collections.Generic.List<int> ses = new System.Collections.Generic.List<int>();
	int nextId = 1, atrN; double atrV = double.NaN, atrSum, prevCl = double.NaN;

	static double Clamp(double v, double a, double b) { return System.Math.Max(a, System.Math.Min(b, v)); }
	static double ScoreSess(int s) { return s == 3 ? 1.5 : (s == 2 ? 1.0 : (s == 4 ? 1.0 : (s == 1 ? 0.5 : 0.0))); }
	static double LevelScore(int sess, int touch, double vr, double wick) { return Clamp(2.0 + ScoreSess(sess) + System.Math.Min(3.0, (touch - 1) * 1.5) + Clamp(vr - 1.0, 0.0, 1.5) + Clamp(wick, 0.0, 1.0) * 2.0, 0.0, 10.0); }
	static int Bucket(double s) { return s >= 7.5 ? 2 : (s >= 5.0 ? 1 : 0); }

	public void AddBar(double O, double H, double L, double C, double V, int sess)
	{
		int i = cl.Count;
		Events.Clear(); Resolved.Clear(); Created.Clear(); DroppedIds.Clear(); OpenedSigs.Clear();
		NewLvlBull = NewLvlBear = SigNow = SigStrongNow = OutTgtNow = OutInvNow = EvXBull = EvXBear = EvSfpBull = EvSfpBear = EvMssBull = EvMssBear = false; BestSigSc = 0;
		double tr = double.IsNaN(prevCl) ? H - L : System.Math.Max(H - L, System.Math.Max(System.Math.Abs(H - prevCl), System.Math.Abs(L - prevCl)));
		atrN++; if (atrN < 14) atrSum += tr; else if (atrN == 14) { atrSum += tr; atrV = atrSum / 14; } else atrV = (atrV * 13 + tr) / 14;
		Atr = atrV; AtrSafe = (double.IsNaN(atrV) || atrV <= 0) ? MinTick * 10.0 : atrV;
		hi.Add(H); lo.Add(L); cl.Add(C); vols.Add(V); ses.Add(sess);
		double va = double.NaN; if (i >= 19) { double t = 0; for (int j = i - 19; j <= i; j++) t += vols[j]; va = t / 20.0; }
		HasVol = !double.IsNaN(va) && va > 0; VolRatio = HasVol ? V / va : 1.0;
		double rng = System.Math.Max(H - L, MinTick);
		vr.Add(VolRatio); wu.Add((H - System.Math.Max(O, C)) / rng); wd.Add((System.Math.Min(O, C) - L) / rng);
		double closePrev = i > 0 ? cl[i - 1] : double.NaN;
		prevCl = C;
		// ---- pivots ----
		double ph = double.NaN, pl = double.NaN;
		if (i >= 2 * Prd)
		{
			double ch = hi[i - Prd], cLo = lo[i - Prd]; bool isH = true, isL = true;
			for (int j = i - 2 * Prd; j <= i; j++)
			{
				if (j == i - Prd) continue;
				if (j < i - Prd) { if (!(ch > hi[j])) isH = false; if (!(cLo < lo[j])) isL = false; }
				else { if (!(ch >= hi[j])) isH = false; if (!(cLo <= lo[j])) isL = false; }
			}
			if (isH) ph = ch; if (isL) pl = cLo;
		}
		int pivBar = i - Prd;
		if (!double.IsNaN(ph))
		{
			bool broken = false; for (int j = i - Prd + 1; j <= i; j++) if (hi[j] > ph) broken = true;
			if (!broken) MakeLevel(1, ph, pivBar, i);
		}
		if (!double.IsNaN(pl))
		{
			bool broken = false; for (int j = i - Prd + 1; j <= i; j++) if (lo[j] < pl) broken = true;
			if (!broken) MakeLevel(-1, pl, pivBar, i);
		}
		// ---- level resolution ----
		for (int k = Lvls.Count - 1; k >= 0; k--)
		{
			var Lv = Lvls[k]; int d = Lv.Dir; double lvl = Lv.Price;
			bool beyond = d == 1 ? H >= lvl : L <= lvl, closeBeyond = d == 1 ? C > lvl : C < lvl, closeInside = d == 1 ? C < lvl : C > lvl;
			double wickExt = d == 1 ? H : L; int kind = -1;
			if (MaxAge > 0 && i - Lv.PBar > MaxAge) { DroppedIds.Add(Lv.Id); Lvls.RemoveAt(k); NExpired++; continue; }
			if (Lv.Pierced)
			{
				Lv.Extreme = d == 1 ? System.Math.Max(Lv.Extreme, wickExt) : System.Math.Min(Lv.Extreme, wickExt);
				bool momentum = d == 1 ? C > closePrev : C < closePrev;
				bool dispOk = MssDisp <= 0.0 || System.Math.Abs(C - lvl) >= MssDisp * AtrSafe;
				if (closeInside) kind = 1;
				else if (closeBeyond && momentum && dispOk) kind = 2;
				else if (i - Lv.PierceBar >= Patience && closeBeyond) kind = 2;
			}
			else
			{
				if (beyond && closeInside) { Lv.Extreme = wickExt; kind = 0; }
				else if (closeBeyond) { Lv.Pierced = true; Lv.PierceBar = i; Lv.Extreme = wickExt; }
			}
			if (kind >= 0)
			{
				int thesis = kind == 2 ? d : -d; double depthAtr = System.Math.Abs(Lv.Extreme - lvl) / AtrSafe;
				double bs = Lv.Score * 0.4, dp = Clamp(depthAtr, 0.0, 1.0) * 2.0, vl = HasVol ? Clamp(VolRatio - 1.0, 0.0, 1.0) * 2.0 : 1.0;
				double bd = kind == 2 ? Clamp(System.Math.Abs(C - closePrev) / AtrSafe, 0.0, 0.5) * 4.0 : Clamp(System.Math.Abs(C - Lv.Price) / AtrSafe, 0.0, 0.5) * 4.0;
				double sc = Clamp(bs + dp + vl + bd, 0.0, 10.0);
				var ev = new SfpEvent { Kind = kind, Thesis = thesis, Dir = d, Sess = Lv.Sess, Touches = Lv.Touches, PBar = Lv.PBar, PierceBar = Lv.PierceBar, LevelId = Lv.Id, Price = Lv.Price, Extreme = Lv.Extreme, DepthAtr = depthAtr, Strength = sc, Atr = AtrSafe, WasPierced = Lv.Pierced };
				EvKind[kind]++; EvDepth[kind] += depthAtr; EvAge[kind] += (i - Lv.PBar); SesEv[Lv.Sess * 3 + kind]++;
				if (thesis == 1) NEvBull++; else NEvBear++;
				if (kind == 0) { EvXBull |= thesis == 1; EvXBear |= thesis == -1; }
				else if (kind == 1) { EvSfpBull |= thesis == 1; EvSfpBear |= thesis == -1; }
				else { EvMssBull |= thesis == 1; EvMssBear |= thesis == -1; }
				if (sc >= MinStr)
				{
					double risk = InvU * AtrSafe;
					if (Structural) { double sd = kind == 2 ? System.Math.Abs(C - Lv.Price) : System.Math.Abs(C - Lv.Extreme); risk = sd + Buf * AtrSafe; }
					risk = System.Math.Max(risk, MinRisk * AtrSafe);
					var sg = new SfpSig { Id = nextId++, Dir = thesis, Kind = kind, Sess = sess, Bucket = Bucket(sc), Entry = C, Target = C + thesis * TgtR * risk, Invalid = C - thesis * risk, Risk = risk, EBar = i };
					Sigs.Add(sg); OpenedSigs.Add(sg);
					ev.SigOpened = true; ev.SigId = sg.Id; ev.Entry = sg.Entry; ev.Target = sg.Target; ev.Invalid = sg.Invalid;
					SigNow = true; SigStrongNow |= sc >= 7.5; BestSigSc = System.Math.Max(BestSigSc, sc);
					LastSigBar = i; LastSigDir = thesis; LastSigKind = kind; LastSigSc = sc;
				}
				Events.Add(ev); DroppedIds.Add(Lv.Id); Lvls.RemoveAt(k);
			}
		}
		// ---- open signal tracking ----
		for (int k = Sigs.Count - 1; k >= 0; k--)
		{
			var s = Sigs[k]; if (i <= s.EBar) continue;
			double toward = s.Dir == 1 ? (H - s.Entry) / s.Risk : (s.Entry - L) / s.Risk, against = s.Dir == 1 ? (s.Entry - L) / s.Risk : (H - s.Entry) / s.Risk;
			s.Mfe = System.Math.Max(s.Mfe, toward); s.Mae = System.Math.Max(s.Mae, against);
			bool hitT = s.Dir == 1 ? H >= s.Target : L <= s.Target, hitX = s.Dir == 1 ? L <= s.Invalid : H >= s.Invalid;
			bool done = false; int res = 0; double rr = 0;
			if (hitX) { done = true; res = 2; rr = -1.0; }
			else if (hitT) { done = true; res = 1; rr = TgtR; }
			else if (i - s.EBar >= MaxHold) { done = true; res = 3; rr = (C - s.Entry) * s.Dir / s.Risk; }
			if (!done) continue;
			int sd = s.Dir == 1 ? 0 : 1; bool win = res == 1;
			KN[s.Kind]++; SN[sd]++; BN[s.Bucket]++; SesSigN[s.Sess]++; KMfe[s.Kind] += s.Mfe; KMae[s.Kind] += s.Mae; TotN++; TotR += rr;
			if (win) { KW[s.Kind]++; SW[sd]++; BW[s.Bucket]++; SesSigW[s.Sess]++; KBar[s.Kind] += (i - s.EBar); TotW++; SumWinR += rr; }
			else SumLossR += -System.Math.Min(rr, 0.0);
			if (res == 3) { KTO[s.Kind]++; TotTO++; }
			EqPeak = System.Math.Max(EqPeak, TotR); MaxDD = System.Math.Max(MaxDD, EqPeak - TotR);
			Streak = win ? (Streak > 0 ? Streak + 1 : 1) : (Streak < 0 ? Streak - 1 : -1);
			OutTgtNow |= res == 1; OutInvNow |= res == 2;
			Resolved.Add(new SfpResolve { SigId = s.Id, Kind = s.Kind, Dir = s.Dir, Res = res, Rr = rr, ExitPx = res == 1 ? s.Target : (res == 2 ? s.Invalid : C), Mfe = s.Mfe, Mae = s.Mae, Bars = i - s.EBar });
			Sigs.RemoveAt(k);
		}
	}

	void MakeLevel(int dir, double price, int pbar, int i)
	{
		double wk = dir == 1 ? wu[pbar] : wd[pbar];
		if (EqMerge)
		{
			for (int k = 0; k < Lvls.Count; k++)
			{
				var Lv = Lvls[k];
				if (Lv.Dir == dir && !Lv.Pierced && System.Math.Abs(Lv.Price - price) <= EqTol * AtrSafe)
				{
					Lv.Touches++; Lv.Score = System.Math.Max(Lv.Score, LevelScore(Lv.Sess, Lv.Touches, vr[pbar], wk));
					if (dir == 1 && price > Lv.Price) Lv.Price = price; else if (dir == -1 && price < Lv.Price) Lv.Price = price;
					Lv.Extreme = Lv.Price; NMerged++; return;
				}
			}
		}
		int cnt = 0; for (int k = 0; k < Lvls.Count; k++) if (Lvls[k].Dir == dir) cnt++;
		if (cnt >= MaxAct) { for (int k = 0; k < Lvls.Count; k++) if (Lvls[k].Dir == dir) { DroppedIds.Add(Lvls[k].Id); Lvls.RemoveAt(k); break; } NExpired++; }
		int sess = ses[pbar];
		var n = new SfpLevel { Id = nextId++, Dir = dir, Price = price, PBar = pbar, Sess = sess, Touches = 1, Score = LevelScore(sess, 1, vr[pbar], wk), Pierced = false, PierceBar = -1, Extreme = price };
		Lvls.Add(n); Created.Add(n); NCreated++; SesLvl[sess]++;
		if (dir == 1) NewLvlBear = true; else NewLvlBull = true;
	}
}


	public enum SfpTz { NewYork, UTC, London, Bangkok, Tokyo }
	public enum SfpDashPos { TopRight, TopLeft, BottomRight, BottomLeft }

	public class SneakyLiquiditySfpMss : Indicator
	{
		private class DRow { public string[] T; public SharpDX.Color4[] C; public float[] W; public bool[] R; public SharpDX.Color4 Bg, Strip; public bool Sep; }

		private SneakySfpCore core;
		private readonly object dashLock = new object();
		private List<DRow> dash = new List<DRow>();
		private int idxD = -1, idxW = -1; private readonly int[] idxM = { -1, -1, -1 };
		private EMA[] emaM = new EMA[3]; private ATR[] atrM = new ATR[3]; private EMA emaP;
		private readonly double[] biasM = { double.NaN, double.NaN, double.NaN };
		private double pdH = double.NaN, pdL = double.NaN, pwH = double.NaN, pwL = double.NaN; private bool newDay, newWeek;
		private double dayHi = double.NaN, dayLo = double.NaN, wkHi = double.NaN, wkLo = double.NaN;
		private bool pdhSw, pdlSw, pwhSw, pwlSw, pdhSwPrev, pdlSwPrev, pwhSwPrev, pwlSwPrev;
		private double distUpPrev = double.NaN, distDnPrev = double.NaN;
		private int[] sessA = new int[4], sessB = new int[4];
		private TimeZoneInfo tzi, chartTz;
		private readonly List<string> lvlTags = new List<string>(), sigTags = new List<string>(), keyTags = new List<string>();
		private readonly Queue<string[]> histEvents = new Queue<string[]>(); private readonly Queue<string> outTags = new Queue<string>();
		private readonly Dictionary<long, System.Windows.Media.Brush> brushes = new Dictionary<long, System.Windows.Media.Brush>();
		private int curSess; private bool errShown;
		private double nearUp, nearDn, distUp, distDn; private string nearUpTag = "", nearDnTag = "";
		private int actBear, actBull; private double sumBear, sumBull;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description		= "Liquidity pools from swing pivots, classified as SWEEP / SFP / MSS with strength score, tracked signals and honest outcome stats; port of ProjectSyndicate 'Strong SFP MSS'.";
				Name			= "SneakyLiquiditySfpMss";
				Calculate		= Calculate.OnBarClose;
				IsOverlay		= true;
				DisplayInDataBox = true;
				PaintPriceMarkers = false;
				IsSuspendedWhileInactive = true;
				ArePlotsConfigurable = true;
				IsAutoScale		= false;
				DrawOnPricePanel = true;
				PivotPeriod = 10; MaxActive = 5; MaxAge = 0; EqMerge = true; EqTol = 0.25;
				AsiaSess = "2000-0000"; LondonSess = "0200-0500"; NyAmSess = "0830-1100"; NyPmSess = "1330-1600"; Tz = SfpTz.NewYork;
				ShowKey = true; KeyDay = true; KeyWeek = true; KeyLen = 60;
				ShowSweep = true; ShowSfp = true; ShowMss = true; MssDisp = 0; Patience = 6; Prox = 0.5;
				MinStrength = 3; StructuralStop = true; Buffer = 0.15; MinRisk = 0.5; TargetR = 1.5; InvAtr = 1.0; MaxHold = 60; ShowOutcomes = true; ShowTargetLines = true;
				LineWidth = 2; Glow = true; Ext = 15; TextOffsetAtr = 0.6; FlipText = false; ShowPriceTags = true; ShowSignalTags = true; ShowBoxes = true; MaxHist = 80; DrawBarsBack = 5000;
				ShowLegend = true; OutcomeFont = 11; ShowDebug = false; DashScale = 140; ShowDash = true; DashPosition = SfpDashPos.TopRight; ShowP1 = true; ShowP2 = true; ShowP3 = true; ShowP4 = true; ShowP5 = true; ShowMtf = true;
				EnableAlerts = false; AlLevels = false; AlEvents = true; AlSignals = true; AlOutcomes = false; AlProx = false;
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "NearestSellSide");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "NearestBuySide");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "SignalStrength");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "SignalDir");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "EventCode");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "OpenSignals");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "SignalEntry");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "SignalStop");
				AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, "SignalTarget");
			}
			else if (State == State.Configure)
			{
				for (int pi = 0; pi < 6; pi++) Plots[pi].Brush = System.Windows.Media.Brushes.Transparent;
				int n = 1;
				if (ShowMtf) { AddDataSeries(BarsPeriodType.Minute, 15); idxM[0] = n++; AddDataSeries(BarsPeriodType.Minute, 60); idxM[1] = n++; AddDataSeries(BarsPeriodType.Minute, 240); idxM[2] = n++; }
				if (ShowKey && KeyDay) { AddDataSeries(BarsPeriodType.Day, 1); idxD = n++; }
				if (ShowKey && KeyWeek) { AddDataSeries(BarsPeriodType.Week, 1); idxW = n++; }
			}
			else if (State == State.DataLoaded)
			{
				core = new SneakySfpCore
				{
					Prd = PivotPeriod, MaxAct = MaxActive, MaxAge = MaxAge, Patience = Patience, MaxHold = MaxHold, EqMerge = EqMerge, Structural = StructuralStop,
					EqTol = EqTol, MssDisp = MssDisp, MinStr = MinStrength, Buf = Buffer, MinRisk = MinRisk, TgtR = TargetR, InvU = InvAtr, MinTick = TickSize
				};
				emaP = EMA(50);
				for (int k = 0; k < 3; k++) if (idxM[k] > 0) { emaM[k] = EMA(Closes[idxM[k]], 50); atrM[k] = ATR(BarsArray[idxM[k]], 14); }
				ParseSess();
				chartTz = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;
				string id = Tz == SfpTz.NewYork ? "Eastern Standard Time" : (Tz == SfpTz.London ? "GMT Standard Time" : (Tz == SfpTz.Bangkok ? "SE Asia Standard Time" : (Tz == SfpTz.Tokyo ? "Tokyo Standard Time" : "UTC")));
				try { tzi = TimeZoneInfo.FindSystemTimeZoneById(id); } catch { tzi = TimeZoneInfo.Utc; }
			}
		}

		private int extUse = -1; private bool extFell; private string dbg = "";
		private readonly HashSet<string> guardShown = new HashSet<string>();
		private void Guard(string name, Action act)
		{
			try { act(); }
			catch (Exception ex) { dbg += name + ": " + ex.GetType().Name + " " + ex.Message + " | "; if (guardShown.Add(name)) Print("SneakyLiquiditySfpMss " + name + " error at bar " + CurrentBar + ": " + ex); }
		}

		private void ParseSess()
		{
			string[] s = { AsiaSess, LondonSess, NyAmSess, NyPmSess };
			for (int k = 0; k < 4; k++)
			{
				sessA[k] = -1; sessB[k] = -1;
				try { var p = s[k].Trim().Split('-'); sessA[k] = int.Parse(p[0].Substring(0, 2)) * 60 + int.Parse(p[0].Substring(2, 2)); sessB[k] = int.Parse(p[1].Substring(0, 2)) * 60 + int.Parse(p[1].Substring(2, 2)); } catch { }
			}
		}

		private bool InS(int k, int m) { int a = sessA[k], b = sessB[k]; if (a < 0) return false; if (a == b) return true; return a < b ? (m >= a && m < b) : (m >= a || m < b); }

		private int SessNow()
		{
			DateTime t = CurrentBar > 0 ? Time[1] : Time[0];
			try { t = TimeZoneInfo.ConvertTime(t, chartTz, tzi); } catch { }
			int m = t.Hour * 60 + t.Minute;
			return InS(3, m) ? 4 : (InS(2, m) ? 3 : (InS(1, m) ? 2 : (InS(0, m) ? 1 : 0)));
		}

		protected override void OnBarUpdate()
		{
			try { BarUpdateInner(); }
			catch (Exception ex) { dbg += "BarUpdate@" + CurrentBar + ": " + ex.GetType().Name + " " + ex.Message + " | "; if (!errShown) { errShown = true; Print("SneakyLiquiditySfpMss OnBarUpdate error at bar " + CurrentBar + ": " + ex); } }
		}

		private void BarUpdateInner()
		{
			if (core == null) return;
			int bip = BarsInProgress;
			if (bip > 0)
			{
				for (int k = 0; k < 3; k++)
					if (bip == idxM[k] && CurrentBars[bip] >= 50) { double a = atrM[k][0]; biasM[k] = a > 0 ? (Closes[bip][0] - emaM[k][0]) / a : double.NaN; }
				if (bip == idxD) { pdH = Highs[bip][0]; pdL = Lows[bip][0]; newDay = true; }
				if (bip == idxW) { pwH = Highs[bip][0]; pwL = Lows[bip][0]; newWeek = true; }
				return;
			}
			if (newDay || double.IsNaN(dayHi)) { dayHi = High[0]; dayLo = Low[0]; newDay = false; } else { dayHi = Math.Max(dayHi, High[0]); dayLo = Math.Min(dayLo, Low[0]); }
			if (newWeek || double.IsNaN(wkHi)) { wkHi = High[0]; wkLo = Low[0]; newWeek = false; } else { wkHi = Math.Max(wkHi, High[0]); wkLo = Math.Min(wkLo, Low[0]); }
			bool keyDayOk = ShowKey && KeyDay && idxD > 0, keyWeekOk = ShowKey && KeyWeek && idxW > 0;
			if (!keyDayOk) { pdH = double.NaN; pdL = double.NaN; }
			if (!keyWeekOk) { pwH = double.NaN; pwL = double.NaN; }
			pdhSwPrev = pdhSw; pdlSwPrev = pdlSw; pwhSwPrev = pwhSw; pwlSwPrev = pwlSw;
			pdhSw = !double.IsNaN(pdH) && dayHi >= pdH; pdlSw = !double.IsNaN(pdL) && dayLo <= pdL; pwhSw = !double.IsNaN(pwH) && wkHi >= pwH; pwlSw = !double.IsNaN(pwL) && wkLo <= pwL;

			curSess = SessNow();
			core.AddBar(Open[0], High[0], Low[0], Close[0], Volume[0], curSess);
			double atrS = core.AtrSafe;
			bool lastBar = CurrentBar >= Count - 2; // the newest bar is still forming and never closes while the market is shut
			bool recent = CurrentBar >= Count - 1 - DrawBarsBack;

			if (recent)
			{
				foreach (var ev in core.Events) DrawEvent(ev);
				foreach (var rs in core.Resolved) DrawOutcome(rs);
			}
			ComputeNearest(atrS);
			double bs = core.BestSigSc * (core.LastSigDir == 0 ? 0 : core.LastSigDir);
			Values[0][0] = nearUp; Values[1][0] = nearDn;
			Values[2][0] = core.SigNow ? bs : 0;
			Values[3][0] = core.OpenedSigs.Count > 0 ? core.OpenedSigs[core.OpenedSigs.Count - 1].Dir : 0;
			Values[4][0] = core.Events.Count > 0 ? core.Events[core.Events.Count - 1].Thesis * (core.Events[core.Events.Count - 1].Kind + 1) : 0;
			Values[5][0] = core.Sigs.Count;
			{ SfpSig ns = (core.SigNow && core.OpenedSigs.Count > 0) ? core.OpenedSigs[core.OpenedSigs.Count - 1] : null;
			  Values[6][0] = ns != null ? ns.Entry : 0; Values[7][0] = ns != null ? ns.Invalid : 0; Values[8][0] = ns != null ? ns.Target : 0; }

			if (lastBar || State == State.Realtime)
			{
				Guard("BuildDash", () => BuildDash(atrS));
				if (extUse < 0) extUse = Ext;
				bool ok = true;
				try { RedrawActive(atrS); } catch (Exception ex) { ok = false; dbg += "Redraw: " + ex.GetType().Name + " " + ex.Message + " | "; if (guardShown.Add("Redraw")) Print("SneakyLiquiditySfpMss RedrawActive error (retrying without future extension): " + ex); }
				if (!ok && extUse != 0) { extUse = 0; extFell = true; Guard("RedrawActive2", () => RedrawActive(atrS)); }
			}
			if (EnableAlerts && State == State.Realtime) RaiseAlerts();
			if (ShowDebug) { int dc; lock (dashLock) dc = dash.Count; Draw.TextFixed(this, "SfpDbg", "SfpMss debug: bar " + CurrentBar + "/" + (Count - 1) + " " + State + " lastBar=" + lastBar + " lvls=" + core.Lvls.Count + " dashRows=" + dc + " err=" + (dbg.Length == 0 ? "none" : dbg), TextPosition.BottomLeft); }
			distUpPrev = distUp; distDnPrev = distDn;
		}

		#region Colours / drawing helpers

		private System.Windows.Media.Brush B(int r, int g, int b, int a = 255)
		{
			long key = ((long)r << 24) | ((long)g << 16) | ((long)b << 8) | (long)a; System.Windows.Media.Brush br;
			if (!brushes.TryGetValue(key, out br)) { var sb = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b)); sb.Freeze(); br = sb; brushes[key] = br; }
			return br;
		}
		private System.Windows.Media.Color Cl(int r, int g, int b) { return System.Windows.Media.Color.FromRgb((byte)r, (byte)g, (byte)b); }

		private int[] SessRgb(int s) { return s == 1 ? new[] { 255, 230, 0 } : (s == 2 ? new[] { 0, 220, 255 } : (s == 3 ? new[] { 255, 140, 0 } : (s == 4 ? new[] { 255, 0, 200 } : new[] { 150, 160, 190 }))); }
		private static string SessName(int s) { return s == 1 ? "ASIA" : (s == 2 ? "LONDON" : (s == 3 ? "NY AM" : (s == 4 ? "NY PM" : "OFF"))); }
		private static string SessShort(int s) { return s == 1 ? "AS" : (s == 2 ? "LN" : (s == 3 ? "NA" : (s == 4 ? "NP" : ".."))); }
		private static string KindName(int k) { return k == 0 ? "SWEEP" : (k == 1 ? "SFP" : "MSS"); }
		private static string Stars(double sc) { return sc >= 8 ? "****" : (sc >= 6.5 ? "***" : (sc >= 5 ? "**" : (sc >= 3 ? "*" : "."))); }
		private static string Tier(double s) { return s >= 7.5 ? "EXTREME" : (s >= 5.0 ? "STRONG" : "MILD"); }
		private string Px(double v) { return double.IsNaN(v) ? "--" : Instrument.MasterInstrument.FormatPrice(v); }
		private static string Sn(double v, string f) { return (v > 0 ? "+" : "") + v.ToString(f); }
		private int[] BearAcc = { 128, 32, 255 }, BullAcc = { 0, 230, 96 }, BearSoft = { 214, 160, 255 }, BullSoft = { 170, 255, 196 };

		private int[] KindRgb(int k) { return k == 0 ? new[] { 236, 236, 255 } : (k == 1 ? new[] { 255, 47, 160 } : new[] { 0, 229, 255 }); }
		private DashStyleHelper KindDash(int k) { return k == 0 ? DashStyleHelper.Dot : (k == 1 ? DashStyleHelper.Dash : DashStyleHelper.Solid); }
		private bool KindShown(int k) { return k == 0 ? ShowSweep : (k == 1 ? ShowSfp : ShowMss); }

		private void DrawEvent(SfpEvent ev)
		{
			int[] kc = KindRgb(ev.Kind), ac = ev.Thesis == 1 ? BullAcc : BearAcc; string id = ev.LevelId.ToString(); int ago = CurrentBar - ev.PBar;
			var tags = new List<string>();
			if (KindShown(ev.Kind))
			{
				if (Glow) { string g = "SfpHG" + id; Draw.Line(this, g, false, ago, ev.Price, 0, ev.Price, B(kc[0], kc[1], kc[2], 46), DashStyleHelper.Solid, LineWidth + 4); tags.Add(g); }
				string ln = "SfpHL" + id; Draw.Line(this, ln, false, ago, ev.Price, 0, ev.Price, B(kc[0], kc[1], kc[2]), KindDash(ev.Kind), ev.Kind == 2 ? LineWidth + 1 : LineWidth); tags.Add(ln);
				int mid = (int)Math.Round((ev.PBar + CurrentBar) / 2.0, MidpointRounding.AwayFromZero);
				double mult = ev.Dir == 1 ? (FlipText ? -1.0 : 1.0) : (FlipText ? 1.0 : -1.0);
				string lt = "SfpHT" + id;
				Draw.Text(this, lt, false, KindName(ev.Kind), CurrentBar - mid, ev.Price + TextOffsetAtr * ev.Atr * mult, 0, B(kc[0], kc[1], kc[2]), new SimpleFont("Arial", 14),
					System.Windows.TextAlignment.Center, System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.Transparent, 0); tags.Add(lt);
				if (ShowBoxes && ev.Kind != 2 && ev.Extreme != ev.Price)
				{
					int bx1 = ev.WasPierced ? CurrentBar - ev.PierceBar : 0; double top = ev.Dir == 1 ? ev.Extreme : ev.Price, bot = ev.Dir == 1 ? ev.Price : ev.Extreme; string bt = "SfpHB" + id;
					Draw.Rectangle(this, bt, false, bx1, top, 0, bot, B(ac[0], ac[1], ac[2], 140), B(ac[0], ac[1], ac[2]), 14); tags.Add(bt);
				}
			}
			if (ShowSignalTags && ev.Strength >= MinStrength)
			{
				string st = "SfpHS" + id; double y = ev.Thesis == 1 ? Low[0] - 0.3 * ev.Atr : High[0] + 0.3 * ev.Atr;
				double lum = 0.299 * ac[0] + 0.587 * ac[1] + 0.114 * ac[2]; var txt = lum >= 150 ? B(8, 8, 12) : B(226, 228, 238);
				Draw.Text(this, st, false, KindName(ev.Kind) + " " + Stars(ev.Strength) + " " + ev.Strength.ToString("0.0"), 0, y, 0, txt, new SimpleFont("Arial", 9),
					System.Windows.TextAlignment.Center, System.Windows.Media.Brushes.Transparent, B(ac[0], ac[1], ac[2]), 85); tags.Add(st);
			}
			histEvents.Enqueue(tags.ToArray());
			while (histEvents.Count > MaxHist) foreach (string t in histEvents.Dequeue()) RemoveDrawObject(t);
		}

		private void DrawOutcome(SfpResolve rs)
		{
			RemoveDrawObject("SfpSigT" + rs.SigId); RemoveDrawObject("SfpSigX" + rs.SigId);
			if (!ShowOutcomes) return;
			int[] c = rs.Res == 1 ? (rs.Dir == 1 ? BullAcc : BearAcc) : (rs.Res == 2 ? (rs.Dir == 1 ? BearAcc : BullAcc) : new[] { 125, 133, 151 });
			string tag = "SfpOut" + rs.SigId, ek = rs.Res == 1 ? "TGT " : (rs.Res == 2 ? "INV " : "END ");
			bool up = (rs.Res == 1) == (rs.Dir == 1); if (rs.Res == 0) up = rs.Dir == 1;
			Draw.Text(this, tag, false, " " + ek + Sn(rs.Rr, "0.0") + "R ", 0, rs.ExitPx, up ? -16 : 16, B(255, 255, 255), new SimpleFont("Arial", Math.Max(8, OutcomeFont)) { Bold = true },
				System.Windows.TextAlignment.Left, B(255, 255, 255, 200), B(c[0], c[1], c[2]), 100);
			outTags.Enqueue(tag); while (outTags.Count > MaxHist * 2) RemoveDrawObject(outTags.Dequeue());
		}

		private void RedrawActive(double atrS)
		{
			foreach (string t in lvlTags) RemoveDrawObject(t); lvlTags.Clear();
			foreach (string t in sigTags) RemoveDrawObject(t); sigTags.Clear();
			foreach (string t in keyTags) RemoveDrawObject(t); keyTags.Clear();
			foreach (var L in core.Lvls)
			{
				int[] sc = SessRgb(L.Sess); int ago = CurrentBar - L.PBar; string id = L.Id.ToString();
				if (Glow) { string g = "SfpAG" + id; Draw.Line(this, g, false, ago, L.Price, -extUse, L.Price, B(sc[0], sc[1], sc[2], 46), DashStyleHelper.Solid, LineWidth + 4); lvlTags.Add(g); }
				string ln = "SfpAL" + id; Draw.Line(this, ln, false, ago, L.Price, -extUse, L.Price, B(sc[0], sc[1], sc[2]), DashStyleHelper.Solid, LineWidth); lvlTags.Add(ln);
				if (ShowPriceTags)
				{
					string tg = "SfpAT" + id; double lum = 0.299 * sc[0] + 0.587 * sc[1] + 0.114 * sc[2]; var txt = lum >= 150 ? B(8, 8, 12) : B(226, 228, 238);
					string text = (L.Dir == 1 ? "SSL ^ " : "BSL v ") + Px(L.Price) + " " + Stars(L.Score) + (L.Touches > 1 ? " x" + L.Touches : "") + " " + SessShort(L.Sess) + " " + (CurrentBar - L.PBar) + "b" + (L.Pierced ? " *" : "");
					Draw.Text(this, tg, false, " " + text, -extUse, L.Price, 0, txt, new SimpleFont("Arial", 9), System.Windows.TextAlignment.Left,
						System.Windows.Media.Brushes.Transparent, B(sc[0], sc[1], sc[2], L.Pierced ? 140 : 255), 100); lvlTags.Add(tg);
				}
			}
			if (ShowTargetLines)
				foreach (var s in core.Sigs)
				{
					int ago = CurrentBar - s.EBar; int[] ta = s.Dir == 1 ? BullAcc : BearAcc, xa = s.Dir == 1 ? BearAcc : BullAcc;
					string t1 = "SfpSigT" + s.Id, t2 = "SfpSigX" + s.Id;
					Draw.Line(this, t1, false, ago, s.Target, 0, s.Target, B(ta[0], ta[1], ta[2], 180), DashStyleHelper.Dot, 1); sigTags.Add(t1);
					Draw.Line(this, t2, false, ago, s.Invalid, 0, s.Invalid, B(xa[0], xa[1], xa[2], 180), DashStyleHelper.Dot, 1); sigTags.Add(t2);
				}
			if (ShowKey)
			{
				KeyLine("PDH", pdH, BearSoft, pdhSw); KeyLine("PDL", pdL, BullSoft, pdlSw); KeyLine("PWH", pwH, BearAcc, pwhSw); KeyLine("PWL", pwL, BullAcc, pwlSw);
			}
		}

		private void KeyLine(string nm, double y, int[] c, bool swept)
		{
			if (double.IsNaN(y)) return; int a = swept ? 100 : 255; int x1 = Math.Min(KeyLen, CurrentBar);
			string t = "SfpK" + nm, t2 = "SfpKT" + nm;
			Draw.Line(this, t, false, x1, y, -extUse, y, B(c[0], c[1], c[2], a), DashStyleHelper.Dash, 1); keyTags.Add(t);
			Draw.Text(this, t2, false, " " + nm + (swept ? " *" : ""), -extUse, y, 0, B(c[0], c[1], c[2], a), new SimpleFont("Arial", 8), System.Windows.TextAlignment.Left,
				System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.Transparent, 0); keyTags.Add(t2);
		}

		private void ComputeNearest(double atrS)
		{
			double close = Close[0]; nearUp = double.NaN; nearDn = double.NaN; nearUpTag = ""; nearDnTag = ""; actBear = 0; actBull = 0; sumBear = 0; sumBull = 0;
			foreach (var L in core.Lvls)
			{
				if (L.Dir == 1) { actBear++; sumBear += L.Score; if (L.Price > close && (double.IsNaN(nearUp) || L.Price < nearUp)) { nearUp = L.Price; nearUpTag = SessShort(L.Sess) + " " + Stars(L.Score); } }
				else { actBull++; sumBull += L.Score; if (L.Price < close && (double.IsNaN(nearDn) || L.Price > nearDn)) { nearDn = L.Price; nearDnTag = SessShort(L.Sess) + " " + Stars(L.Score); } }
			}
			if (!double.IsNaN(pdH) && !pdhSw && pdH > close && (double.IsNaN(nearUp) || pdH < nearUp)) { nearUp = pdH; nearUpTag = "PDH"; }
			if (!double.IsNaN(pwH) && !pwhSw && pwH > close && (double.IsNaN(nearUp) || pwH < nearUp)) { nearUp = pwH; nearUpTag = "PWH"; }
			if (!double.IsNaN(pdL) && !pdlSw && pdL < close && (double.IsNaN(nearDn) || pdL > nearDn)) { nearDn = pdL; nearDnTag = "PDL"; }
			if (!double.IsNaN(pwL) && !pwlSw && pwL < close && (double.IsNaN(nearDn) || pwL > nearDn)) { nearDn = pwL; nearDnTag = "PWL"; }
			distUp = double.IsNaN(nearUp) ? double.NaN : (nearUp - close) / atrS; distDn = double.IsNaN(nearDn) ? double.NaN : (close - nearDn) / atrS;
		}

		private void RaiseAlerts()
		{
			string snd = NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav"; string t = CurrentBar.ToString();
			Action<string, string> al = (id, msg) => Alert("sfp" + id + t, Priority.Medium, msg, snd, 10, System.Windows.Media.Brushes.Black, System.Windows.Media.Brushes.MediumPurple);
			if (AlLevels && core.NewLvlBear) al("NB", "New sell-side liquidity pool"); if (AlLevels && core.NewLvlBull) al("NU", "New buy-side liquidity pool");
			if (AlEvents)
			{
				if (core.EvXBull) al("XB", "SWEEP bullish"); if (core.EvXBear) al("XS", "SWEEP bearish");
				if (core.EvSfpBull) al("FB", "SFP bullish"); if (core.EvSfpBear) al("FS", "SFP bearish");
				if (core.EvMssBull) al("MB", "MSS bullish"); if (core.EvMssBear) al("MS", "MSS bearish");
			}
			if (AlSignals && core.SigNow) al("SG", "Tracked liquidity signal fired" + (core.SigStrongNow ? " (EXTREME)" : ""));
			if (AlOutcomes && core.OutTgtNow) al("OT", "Signal target hit"); if (AlOutcomes && core.OutInvNow) al("OI", "Signal invalidated");
			if (AlProx)
			{
				if (!double.IsNaN(distUp) && distUp <= Prox && (double.IsNaN(distUpPrev) || distUpPrev > Prox)) al("PU", "Near sell-side liquidity");
				if (!double.IsNaN(distDn) && distDn <= Prox && (double.IsNaN(distDnPrev) || distDnPrev > Prox)) al("PD", "Near buy-side liquidity");
			}
		}

		#endregion

		#region Dashboard model

		private static SharpDX.Color4 C4(int r, int g, int b, float a = 1f) { return new SharpDX.Color4(r / 255f, g / 255f, b / 255f, a); }
		private SharpDX.Color4 CC(int[] c) { return C4(c[0], c[1], c[2]); }
		private SharpDX.Color4 TXT { get { return C4(217, 220, 230); } } private SharpDX.Color4 MUTED { get { return C4(125, 133, 151); } } private SharpDX.Color4 DIM { get { return C4(74, 81, 99); } }
		private SharpDX.Color4 GOLD { get { return C4(245, 185, 66); } } private SharpDX.Color4 WHITE { get { return C4(232, 230, 240); } }
		private const float PW = 580f;
		private static string Pct(int n, int d) { return d > 0 ? Math.Round((double)n / d * 100.0).ToString("0") + "%" : "--"; }
		private static double Frac(int n, int d) { return d > 0 ? (double)n / d : 0.0; }
		private static string Gauge(double f, int n) { int k = Math.Max(0, Math.Min(n, (int)Math.Round(f * n))); return new string('#', k) + new string('-', n - k); }
		private DRow Head(string title, string sub, SharpDX.Color4 ac) { return new DRow { T = new[] { "* " + title, sub }, C = new[] { TXT, ac }, W = new[] { 330f, PW - 330f }, R = new[] { false, true }, Bg = C4(24, 30, 44), Strip = ac }; }
		private DRow KV(string k, string v, SharpDX.Color4 vc, SharpDX.Color4 ac) { return new DRow { T = new[] { k, v }, C = new[] { MUTED, vc }, W = new[] { 150f, PW - 150f }, R = new[] { false, true }, Bg = C4(13, 17, 26), Strip = new SharpDX.Color4(ac.Red, ac.Green, ac.Blue, 0.55f) }; }
		private DRow Cols(string[] n, float[] w, SharpDX.Color4 ac) { var c = new SharpDX.Color4[n.Length]; var r = new bool[n.Length]; for (int i = 0; i < n.Length; i++) { c[i] = MUTED; r[i] = i > 0; } return new DRow { T = n, C = c, W = w, R = r, Bg = C4(19, 24, 38), Strip = new SharpDX.Color4(ac.Red, ac.Green, ac.Blue, 0.55f) }; }
		private DRow Note(string t, SharpDX.Color4 ac) { return new DRow { T = new[] { t }, C = new[] { DIM }, W = new[] { PW }, R = new[] { false }, Bg = C4(11, 14, 21), Strip = new SharpDX.Color4(ac.Red, ac.Green, ac.Blue, 0.55f) }; }
		private DRow Sep() { return new DRow { Sep = true }; }
		private SharpDX.Color4 WinCol(int n, double wr) { double be = TargetR > 0 ? 100.0 / (1.0 + TargetR) : 50.0; return n == 0 ? DIM : (wr >= be + 5 ? CC(BullAcc) : (wr <= be - 5 ? CC(BearAcc) : TXT)); }

		private DRow ARow(string nm, int n, int w, int to, double sumB, double mfe, double mae, SharpDX.Color4 lc, SharpDX.Color4 ac)
		{
			double wr = n > 0 ? (double)w / n * 100.0 : 0.0; var wc = WinCol(n, wr);
			return new DRow { T = new[] { nm, n.ToString(), n > 0 ? wr.ToString("0.0") + "%" : "--", n > 0 ? to.ToString() : "--", w > 0 ? (sumB / w).ToString("0.0") : "--", n > 0 ? (mfe / n).ToString("0.00") + "/" + (mae / n).ToString("0.00") : "--", n > 0 ? Gauge(wr / 100.0, 5) : "" },
				C = new[] { n == 0 ? DIM : lc, n == 0 ? DIM : TXT, wc, n == 0 ? DIM : (to > 0 ? GOLD : MUTED), n == 0 ? DIM : TXT, n == 0 ? DIM : TXT, wc },
				W = new[] { 130f, 45f, 65f, 45f, 55f, 105f, 135f }, R = new[] { false, true, true, true, true, true, false }, Bg = C4(13, 17, 26), Strip = new SharpDX.Color4(ac.Red, ac.Green, ac.Blue, 0.55f) };
		}

		private string BiasTxt(double b) { return double.IsNaN(b) ? "--" : (b >= 1.0 ? "^^" : (b >= 0.25 ? "^" : (b <= -1.0 ? "vv" : (b <= -0.25 ? "v" : ".")))); }

		private void BuildDash(double atrS)
		{
			var rows = new List<DRow>(); double close = Close[0]; bool need = false; int sessNow = curSess;
			if (ShowP1)
			{
				var ac = GOLD; int sg = core.Sigs.Count;
				rows.Add(Head("SFP MSS - LIQUIDITY STATE", Instrument.MasterInstrument.Name + " " + BarsPeriod.Value + " " + BarsPeriod.BarsPeriodType, ac));
				rows.Add(KV("Session", "* " + SessName(sessNow) + " - " + Tz, CC(SessRgb(sessNow)), ac));
				rows.Add(KV("Active Pools", "^ " + actBear + " sell-side " + Gauge(Frac(actBear, MaxActive), 5) + "  v " + actBull + " buy-side " + Gauge(Frac(actBull, MaxActive), 5), TXT, ac));
				rows.Add(KV("Nearest ^ Target", double.IsNaN(nearUp) ? "--" : Px(nearUp) + " - " + distUp.ToString("0.00") + " ATR - " + nearUpTag, double.IsNaN(nearUp) ? MUTED : (distUp <= Prox ? GOLD : CC(BearSoft)), ac));
				rows.Add(KV("Nearest v Target", double.IsNaN(nearDn) ? "--" : Px(nearDn) + " - " + distDn.ToString("0.00") + " ATR - " + nearDnTag, double.IsNaN(nearDn) ? MUTED : (distDn <= Prox ? GOLD : CC(BullSoft)), ac));
				double aB = actBear > 0 ? sumBear / actBear : 0, aU = actBull > 0 ? sumBull / actBull : 0;
				string mag = actBear == 0 && actBull == 0 ? "--" : (aB > aU + 0.5 ? "^ SELL-SIDE heavier - " + aB.ToString("0.0") + " vs " + aU.ToString("0.0") : (aU > aB + 0.5 ? "v BUY-SIDE heavier - " + aU.ToString("0.0") + " vs " + aB.ToString("0.0") : "BALANCED - " + aB.ToString("0.0") + " / " + aU.ToString("0.0")));
				rows.Add(KV("Liquidity Magnet", mag, aB > aU + 0.5 ? CC(BearAcc) : (aU > aB + 0.5 ? CC(BullAcc) : TXT), ac));
				string bias = "chart " + BiasTxt(emaP.Count > 0 && atrS > 0 ? (close - emaP[0]) / atrS : double.NaN);
				string[] nm = { "15m", "1H", "4H" }; for (int k = 0; k < 3; k++) if (idxM[k] > 0) bias += " - " + nm[k] + " " + BiasTxt(biasM[k]);
				double b0 = emaP.Count > 0 && atrS > 0 ? (close - emaP[0]) / atrS : double.NaN;
				rows.Add(KV("MTF Bias", bias, double.IsNaN(b0) ? DIM : (b0 >= 0.25 ? CC(BullAcc) : (b0 <= -0.25 ? CC(BearAcc) : MUTED)), ac));
				rows.Add(KV("Sweep Flow", core.NEvBull + core.NEvBear == 0 ? "--" : "^ " + core.NEvBull + " bullish - v " + core.NEvBear + " bearish theses", core.NEvBull > core.NEvBear ? CC(BullAcc) : (core.NEvBear > core.NEvBull ? CC(BearAcc) : TXT), ac));
				string ls = core.LastSigBar < 0 ? "--" : KindName(core.LastSigKind) + (core.LastSigDir == 1 ? " ^ " : " v ") + core.LastSigSc.ToString("0.0") + " " + Stars(core.LastSigSc) + " - " + (CurrentBar - core.LastSigBar) + " bars ago";
				rows.Add(KV("Last Signal - Open", ls + " - " + sg + " open", core.LastSigBar < 0 ? MUTED : (core.LastSigDir == 1 ? CC(BullAcc) : CC(BearAcc)), ac));
				need = true;
			}
			if (ShowP2)
			{
				if (need) rows.Add(Sep()); var ac = WHITE;
				rows.Add(Head("LIQUIDITY MAP", core.Lvls.Count + " pools", ac));
				float[] mw = { 90f, 85f, 60f, 50f, 50f, 50f, 40f, 155f };
				rows.Add(Cols(new[] { "LEVEL", "PRICE", "ATR", "SESS", "STR", "AGE", "EQ", "RANK" }, mw, ac));
				var items = new List<double[]>(); // price, idx(-1..-4 key, >=0 level)
				for (int i = 0; i < core.Lvls.Count; i++) items.Add(new[] { core.Lvls[i].Price, i });
				if (ShowKey) { if (!double.IsNaN(pdH)) items.Add(new[] { pdH, -1 }); if (!double.IsNaN(pdL)) items.Add(new[] { pdL, -2 }); if (!double.IsNaN(pwH)) items.Add(new[] { pwH, -3 }); if (!double.IsNaN(pwL)) items.Add(new[] { pwL, -4 }); }
				if (items.Count == 0) rows.Add(Note("no liquidity mapped yet - waiting for confirmed pivots", ac));
				else
				{
					items.Sort((a, b) => b[0].CompareTo(a[0])); bool done = false;
					foreach (var it in items)
					{
						if (!done && close >= it[0]) { rows.Add(PriceRow(atrS, ac, mw)); done = true; }
						int ix = (int)it[1];
						if (ix >= 0) { var L = core.Lvls[ix]; rows.Add(MapRow(L.Dir == 1 ? "SSL ^" : "BSL v", L.Price, L.Sess, L.Score, CurrentBar - L.PBar, L.Touches, L.Pierced, false, false, L.Dir == 1 ? CC(BearSoft) : CC(BullSoft), ac, atrS, mw)); }
						else if (ix == -1) rows.Add(MapRow("PDH", pdH, 0, 0, 0, 0, false, true, pdhSw, CC(BearSoft), ac, atrS, mw));
						else if (ix == -2) rows.Add(MapRow("PDL", pdL, 0, 0, 0, 0, false, true, pdlSw, CC(BullSoft), ac, atrS, mw));
						else if (ix == -3) rows.Add(MapRow("PWH", pwH, 0, 0, 0, 0, false, true, pwhSw, CC(BearAcc), ac, atrS, mw));
						else rows.Add(MapRow("PWL", pwL, 0, 0, 0, 0, false, true, pwlSw, CC(BullAcc), ac, atrS, mw));
					}
					if (!done) rows.Add(PriceRow(atrS, ac, mw));
				}
				need = true;
			}
			if (ShowP3)
			{
				if (need) rows.Add(Sep()); var ac = CC(BullAcc);
				rows.Add(Head("SETUP ANALYTICS", TargetR.ToString("0.##") + "R - " + (StructuralStop ? "structural stop" : InvAtr.ToString("0.##") + "xATR stop") + " - INV-first", ac));
				rows.Add(Cols(new[] { "SETUP", "N", "WIN%", "T/O", "BARS", "MFE/MAE", "EDGE" }, new[] { 130f, 45f, 65f, 45f, 55f, 105f, 135f }, ac));
				rows.Add(ARow("SWEEP - RAID", core.KN[0], core.KW[0], core.KTO[0], core.KBar[0], core.KMfe[0], core.KMae[0], CC(KindRgb(0)), ac));
				rows.Add(ARow("SFP - RECLAIM", core.KN[1], core.KW[1], core.KTO[1], core.KBar[1], core.KMfe[1], core.KMae[1], CC(KindRgb(1)), ac));
				rows.Add(ARow("MSS - SHIFT", core.KN[2], core.KW[2], core.KTO[2], core.KBar[2], core.KMfe[2], core.KMae[2], CC(KindRgb(2)), ac));
				rows.Add(ARow("ALL", core.TotN, core.TotW, core.TotTO, core.KBar[0] + core.KBar[1] + core.KBar[2], core.KMfe[0] + core.KMfe[1] + core.KMfe[2], core.KMae[0] + core.KMae[1] + core.KMae[2], TXT, ac));
				rows.Add(KV("Exit Mix", core.TotN > 0 ? "TARGET " + core.TotW + " - INVALIDATED " + (core.TotN - core.TotW - core.TotTO) + " - TIMEOUT " + core.TotTO : "--", TXT, ac));
				rows.Add(KV("By Side", "LONG ^ " + Pct(core.SW[0], core.SN[0]) + " (" + core.SN[0] + ") - SHORT v " + Pct(core.SW[1], core.SN[1]) + " (" + core.SN[1] + ")", Frac(core.SW[0], core.SN[0]) > Frac(core.SW[1], core.SN[1]) ? CC(BullAcc) : (Frac(core.SW[1], core.SN[1]) > Frac(core.SW[0], core.SN[0]) ? CC(BearAcc) : TXT), ac));
				rows.Add(KV("By Strength", "MILD " + Pct(core.BW[0], core.BN[0]) + " (" + core.BN[0] + ") - STRONG " + Pct(core.BW[1], core.BN[1]) + " (" + core.BN[1] + ") - EXTREME " + Pct(core.BW[2], core.BN[2]) + " (" + core.BN[2] + ")", TXT, ac));
				string pf = core.SumLossR > 0 ? (core.SumWinR / core.SumLossR).ToString("0.00") : (core.SumWinR > 0 ? "inf" : "--");
				rows.Add(KV("Net R - PF - Max DD", Sn(core.TotR, "0.0") + "R - " + pf + " - -" + core.MaxDD.ToString("0.0") + "R", core.TotR >= 0 ? CC(BullAcc) : CC(BearAcc), ac));
				rows.Add(KV("Expectancy - Streak", (core.TotN > 0 ? Sn(core.TotR / core.TotN, "0.00") + "R / signal" : "--") + " - " + (core.Streak > 0 ? "W" + core.Streak : (core.Streak < 0 ? "L" + (-core.Streak) : "--")), core.TotN == 0 ? MUTED : (core.TotR >= 0 ? CC(BullAcc) : CC(BearAcc)), ac));
				rows.Add(Note("every resolved signal counted - same-bar ties booked as losses - timeouts never hidden", ac));
				need = true;
			}
			if (ShowP4)
			{
				if (need) rows.Add(Sep()); var ac = CC(BearAcc);
				rows.Add(Head("SESSION ANALYTICS", "where liquidity is born - how it dies", ac));
				float[] sw = { 110f, 55f, 55f, 55f, 55f, 55f, 65f, 130f };
				rows.Add(Cols(new[] { "SESSION", "LVLS", "SWP%", "SFP%", "MSS%", "SIG", "WIN%", "" }, sw, ac));
				foreach (int s in new[] { 1, 2, 3, 4, 0 })
				{
					int nl = core.SesLvl[s], nx = core.SesEv[s * 3], nf = core.SesEv[s * 3 + 1], nm = core.SesEv[s * 3 + 2], ne = nx + nf + nm, gn = core.SesSigN[s], gw = core.SesSigW[s]; double wr = gn > 0 ? (double)gw / gn * 100.0 : 0.0; var scc = CC(SessRgb(s));
					rows.Add(new DRow { T = new[] { (s == sessNow ? "* " : "  ") + SessName(s), nl.ToString(), ne > 0 ? Pct(nx, ne) : "--", ne > 0 ? Pct(nf, ne) : "--", ne > 0 ? Pct(nm, ne) : "--", gn.ToString(), gn > 0 ? wr.ToString("0") + "%" : "--", gn > 0 ? Gauge(wr / 100.0, 5) : "" },
						C = new[] { scc, nl == 0 ? DIM : TXT, ne == 0 ? DIM : CC(KindRgb(0)), ne == 0 ? DIM : CC(KindRgb(1)), ne == 0 ? DIM : CC(KindRgb(2)), gn == 0 ? DIM : TXT, WinCol(gn, wr), gn == 0 ? DIM : TXT },
						W = sw, R = new[] { false, true, true, true, true, true, true, false }, Bg = C4(13, 17, 26), Strip = new SharpDX.Color4(scc.Red, scc.Green, scc.Blue, 0.6f) });
				}
				need = true;
			}
			if (ShowP5)
			{
				if (need) rows.Add(Sep()); var ac = CC(BearSoft); int nEv = core.EvKind[0] + core.EvKind[1] + core.EvKind[2];
				rows.Add(Head("SWEEP STATISTICS", nEv + " classified", ac));
				rows.Add(KV("Levels", core.NCreated + " born - " + core.NMerged + " merged - " + core.NExpired + " expired - " + nEv + " swept", TXT, ac));
				rows.Add(KV("Resolution Mix", nEv > 0 ? "SWEEP " + Pct(core.EvKind[0], nEv) + " - SFP " + Pct(core.EvKind[1], nEv) + " - MSS " + Pct(core.EvKind[2], nEv) : "--", TXT, ac));
				int nRev = core.EvKind[0] + core.EvKind[1];
				rows.Add(KV("Reclaim Rate", nEv > 0 ? Pct(nRev, nEv) + " reclaimed (SWEEP + SFP) - " + Pct(core.EvKind[2], nEv) + " accepted (MSS)" : "--", nEv == 0 ? MUTED : (Frac(nRev, nEv) >= 0.55 ? CC(BullAcc) : (Frac(nRev, nEv) <= 0.45 ? CC(BearAcc) : TXT)), ac));
				Func<double[], string> avg = a => nEv > 0 ? "SWEEP " + (core.EvKind[0] > 0 ? (a[0] / core.EvKind[0]).ToString("0.00") : "--") + " - SFP " + (core.EvKind[1] > 0 ? (a[1] / core.EvKind[1]).ToString("0.00") : "--") + " - MSS " + (core.EvKind[2] > 0 ? (a[2] / core.EvKind[2]).ToString("0.00") : "--") : "--";
				rows.Add(KV("Avg Sweep Depth (ATR)", avg(core.EvDepth), TXT, ac)); rows.Add(KV("Avg Level Age (bars)", avg(core.EvAge), TXT, ac));
				string cal = core.NCreated < 20 ? "collecting..." : (Frac(core.NExpired, core.NCreated) > 0.5 ? "MANY EXPIRE UNSWEPT - raise Max Active or Pivot Period" : (Frac(nEv, core.NCreated) > 0.9 ? "ALMOST EVERYTHING SWEPT - lower Pivot Period" : "CALIBRATED"));
				rows.Add(KV("Calibration", cal, core.NCreated < 20 ? MUTED : (cal == "CALIBRATED" ? CC(BullAcc) : GOLD), ac));
			}
			lock (dashLock) dash = rows;
		}

		private DRow PriceRow(double atrS, SharpDX.Color4 ac, float[] w)
		{
			var bg = C4(29, 35, 51);
			return new DRow { T = new[] { "PRICE <", Px(Close[0]), "ATR " + Px(core.Atr), "", "", "", "", "^ " + (double.IsNaN(distUp) ? "--" : distUp.ToString("0.0") + "a") + " - v " + (double.IsNaN(distDn) ? "--" : distDn.ToString("0.0") + "a") },
				C = new[] { WHITE, WHITE, MUTED, MUTED, MUTED, MUTED, MUTED, TXT }, W = w, R = new[] { false, true, false, false, false, false, false, true }, Bg = bg, Strip = ac };
		}

		private DRow MapRow(string nm, double px, int sess, double sc, int age, int touch, bool pierced, bool isKey, bool swept, SharpDX.Color4 lc, SharpDX.Color4 ac, double atrS, float[] w)
		{
			double dist = (px - Close[0]) / atrS; var nc = swept ? new SharpDX.Color4(lc.Red, lc.Green, lc.Blue, 0.45f) : lc;
			return new DRow { T = new[] { nm + (pierced ? " *" : (swept ? " o" : "")), Px(px), Sn(dist, "0.00"), isKey ? "KEY" : SessShort(sess), isKey ? "--" : sc.ToString("0.0"), isKey ? "--" : age.ToString(), isKey ? "--" : (touch > 1 ? "x" + touch : "1"), isKey ? (swept ? "SWEPT" : "INTACT") : Stars(sc) },
				C = new[] { nc, swept ? MUTED : TXT, Math.Abs(dist) <= Prox ? GOLD : MUTED, isKey ? WHITE : CC(SessRgb(sess)), isKey ? DIM : (sc >= 7.5 ? GOLD : (sc >= 5 ? TXT : MUTED)), isKey ? DIM : MUTED, isKey ? DIM : (touch > 1 ? GOLD : MUTED), isKey ? (swept ? MUTED : nc) : lc },
				W = w, R = new[] { false, true, true, true, true, true, true, true }, Bg = C4(13, 17, 26), Strip = pierced ? GOLD : new SharpDX.Color4(ac.Red, ac.Green, ac.Blue, 0.55f) };
		}

		#endregion

		#region Rendering

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (!ShowDash || RenderTarget == null || core == null) return;
			try { RenderLegend(); RenderDash(); }
			catch (Exception ex)
			{
				if (!errShown) { errShown = true; Print("SneakyLiquiditySfpMss OnRender error: " + ex); }
				dbg += "Render: " + ex.GetType().Name + " " + ex.Message + " | ";
			}
		}

		private void RenderLegend()
		{
			if (!ShowLegend) return;
			var disp = new List<IDisposable>();
			try
			{
				string[] nm = { "Asia", "London", "NY AM", "NY PM", "Off" }; int[] ids = { 1, 2, 3, 4, 0 };
				var fmt = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial", 11f); disp.Add(fmt);
				float x = 10f, y = (float)ChartPanel.H - 40f;
				var bg = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, C4(10, 12, 20, 0.8f)); disp.Add(bg);
				RenderTarget.FillRectangle(new SharpDX.RectangleF(x - 4f, y - 3f, 5 * 78f + 8f, 20f), bg);
				for (int i = 0; i < 5; i++)
				{
					var c = SessRgb(ids[i]); var b = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, C4(c[0], c[1], c[2])); disp.Add(b);
					RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y + 2f, 10f, 10f), b);
					RenderTarget.DrawText(nm[i], fmt, new SharpDX.RectangleF(x + 14f, y - 1f, 60f, 16f), b);
					x += 78f;
				}
			}
			finally { foreach (var d in disp) d.Dispose(); }
		}

		private void RenderDash()
		{
			bool scaled = false;
			List<DRow> rows; lock (dashLock) rows = dash;
			if (rows == null || rows.Count == 0) return;
			var disp = new List<IDisposable>();
			try
			{
				const float rh = 14f, sepH = 5f; float th = 0; foreach (var r in rows) th += r.Sep ? sepH : rh;
				float k = Math.Max(0.6f, DashScale / 100f);
				float pw = (float)ChartPanel.W / k, ph = (float)ChartPanel.H / k;
				RenderTarget.Transform = SharpDX.Matrix3x2.Scaling(k); scaled = true;
				bool right = DashPosition == SfpDashPos.TopRight || DashPosition == SfpDashPos.BottomRight, top = DashPosition == SfpDashPos.TopRight || DashPosition == SfpDashPos.TopLeft;
				float x0 = right ? pw - PW - 14f : 14f, y = top ? 8f : Math.Max(4f, ph - th - 8f);
				var fmt = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", 10f); disp.Add(fmt);
				var fmtR = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", 10f) { TextAlignment = SharpDX.DirectWrite.TextAlignment.Trailing }; disp.Add(fmtR);
				foreach (var r in rows)
				{
					if (r.Sep) { y += sepH; continue; }
					var bg = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(r.Bg.Red, r.Bg.Green, r.Bg.Blue, 0.92f)); disp.Add(bg);
					RenderTarget.FillRectangle(new SharpDX.RectangleF(x0, y, PW, rh), bg);
					var st = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, r.Strip); disp.Add(st);
					RenderTarget.FillRectangle(new SharpDX.RectangleF(x0, y, 3f, rh), st);
					float cx = x0 + 6f;
					for (int c = 0; c < r.T.Length; c++)
					{
						float w = r.W[c] - (c == 0 ? 6f : 0f);
						var tb = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, r.C[c]); disp.Add(tb);
						RenderTarget.DrawText(r.T[c] ?? "", r.R[c] ? fmtR : fmt, new SharpDX.RectangleF(cx, y + 0.5f, Math.Max(10f, w - 6f), rh), tb);
						cx += w;
					}
					y += rh;
				}
			}
			finally { if (scaled) RenderTarget.Transform = SharpDX.Matrix3x2.Identity; foreach (var d in disp) d.Dispose(); }
		}

		#endregion

		#region Properties

		[Display(Name = "Pivot Period", Description = "Swing strength: a pivot needs this many lower highs/higher lows on both sides.", Order = 0, GroupName = "01. Levels")] [Range(2, 100)] public int PivotPeriod { get; set; }
		[Display(Name = "Max Active Levels Per Side", Order = 1, GroupName = "01. Levels")] [Range(1, 10)] public int MaxActive { get; set; }
		[Display(Name = "Max Level Age (bars, 0 = unlimited)", Order = 2, GroupName = "01. Levels")] [Range(0, 5000)] public int MaxAge { get; set; }
		[Display(Name = "Merge Equal Highs / Lows Into Pools", Order = 3, GroupName = "01. Levels")] public bool EqMerge { get; set; }
		[Display(Name = "Equal-Level Tolerance (x ATR)", Order = 4, GroupName = "01. Levels")] [Range(0.05, 2.0)] public double EqTol { get; set; }

		[Display(Name = "Timezone", Order = 0, GroupName = "02. Sessions")] public SfpTz Tz { get; set; }
		[Display(Name = "Asia (HHMM-HHMM)", Order = 1, GroupName = "02. Sessions")] public string AsiaSess { get; set; }
		[Display(Name = "London", Order = 2, GroupName = "02. Sessions")] public string LondonSess { get; set; }
		[Display(Name = "NY AM", Order = 3, GroupName = "02. Sessions")] public string NyAmSess { get; set; }
		[Display(Name = "NY PM", Order = 4, GroupName = "02. Sessions")] public string NyPmSess { get; set; }

		[Display(Name = "Show Key Liquidity (PDH/PDL/PWH/PWL)", Order = 0, GroupName = "03. Key Levels")] public bool ShowKey { get; set; }
		[Display(Name = "Prior Day High / Low", Order = 1, GroupName = "03. Key Levels")] public bool KeyDay { get; set; }
		[Display(Name = "Prior Week High / Low", Order = 2, GroupName = "03. Key Levels")] public bool KeyWeek { get; set; }
		[Display(Name = "Key Line Length (bars)", Order = 3, GroupName = "03. Key Levels")] [Range(10, 500)] public int KeyLen { get; set; }

		[Display(Name = "Show SWEEP", Order = 0, GroupName = "04. Classification")] public bool ShowSweep { get; set; }
		[Display(Name = "Show SFP", Order = 1, GroupName = "04. Classification")] public bool ShowSfp { get; set; }
		[Display(Name = "Show MSS", Order = 2, GroupName = "04. Classification")] public bool ShowMss { get; set; }
		[Display(Name = "MSS Displacement Filter (x ATR, 0 = off)", Order = 3, GroupName = "04. Classification")] [Range(0, 3)] public double MssDisp { get; set; }
		[Display(Name = "Pierce Patience (bars)", Order = 4, GroupName = "04. Classification")] [Range(1, 100)] public int Patience { get; set; }
		[Display(Name = "Proximity Alert (x ATR)", Order = 5, GroupName = "04. Classification")] [Range(0.1, 5)] public double Prox { get; set; }

		[Display(Name = "Min Signal Strength (0-10)", Order = 0, GroupName = "05. Signals")] [Range(0, 10)] public double MinStrength { get; set; }
		[Display(Name = "Structural Stop (off = ATR stop)", Order = 1, GroupName = "05. Signals")] public bool StructuralStop { get; set; }
		[Display(Name = "Structural Buffer (x ATR)", Order = 2, GroupName = "05. Signals")] [Range(0, 2)] public double Buffer { get; set; }
		[Display(Name = "Min Risk (x ATR)", Order = 3, GroupName = "05. Signals")] [Range(0.1, 5)] public double MinRisk { get; set; }
		[Display(Name = "Target (R)", Order = 4, GroupName = "05. Signals")] [Range(0.25, 10)] public double TargetR { get; set; }
		[Display(Name = "Invalidation (x ATR, ATR stop model)", Order = 5, GroupName = "05. Signals")] [Range(0.25, 5)] public double InvAtr { get; set; }
		[Display(Name = "Outcome Max Hold (bars)", Order = 6, GroupName = "05. Signals")] [Range(5, 1000)] public int MaxHold { get; set; }
		[Display(Name = "Mark Signal Outcomes", Order = 7, GroupName = "05. Signals")] public bool ShowOutcomes { get; set; }
		[Display(Name = "Target / Invalidation Lines", Order = 8, GroupName = "05. Signals")] public bool ShowTargetLines { get; set; }

		[Display(Name = "Active Line Width", Order = 0, GroupName = "06. Visuals")] [Range(1, 4)] public int LineWidth { get; set; }
		[Display(Name = "Neon Glow", Order = 1, GroupName = "06. Visuals")] public bool Glow { get; set; }
		[Display(Name = "Line Extension (bars)", Order = 2, GroupName = "06. Visuals")] [Range(0, 100)] public int Ext { get; set; }
		[Display(Name = "Text Distance (x ATR)", Order = 3, GroupName = "06. Visuals")] [Range(0, 5)] public double TextOffsetAtr { get; set; }
		[Display(Name = "Flip Text Position", Order = 4, GroupName = "06. Visuals")] public bool FlipText { get; set; }
		[Display(Name = "Price Tags (active levels)", Order = 5, GroupName = "06. Visuals")] public bool ShowPriceTags { get; set; }
		[Display(Name = "Signal Tags (strength)", Order = 6, GroupName = "06. Visuals")] public bool ShowSignalTags { get; set; }
		[Display(Name = "Sweep Boxes", Order = 7, GroupName = "06. Visuals")] public bool ShowBoxes { get; set; }
		[Display(Name = "Event History Kept", Order = 8, GroupName = "06. Visuals")] [Range(10, 400)] public int MaxHist { get; set; }
		[Display(Name = "Draw Only Last N Bars", Description = "Statistics always cover every loaded bar; only drawing is limited.", Order = 9, GroupName = "06. Visuals")] [Range(100, 100000)] public int DrawBarsBack { get; set; }

		[Display(Name = "Show Debug Line (bottom-left)", Description = "Shows internal status text; turn off once everything works.", Order = 99, GroupName = "07. Dashboard")] public bool ShowDebug { get; set; }
		[Range(8, 20)] [Display(Name = "Outcome Tag Font Size", Order = 10, GroupName = "07. Dashboard")] public int OutcomeFont { get; set; }
		[Display(Name = "Show Session Colour Key", Order = 9, GroupName = "07. Dashboard")] public bool ShowLegend { get; set; }
		[Display(Name = "Show Dashboard", Order = 0, GroupName = "07. Dashboard")] public bool ShowDash { get; set; }
		[Display(Name = "Dashboard Size (%)", Description = "100 = original small size.", Order = 8, GroupName = "07. Dashboard")] [Range(60, 300)] public int DashScale { get; set; }
		[Display(Name = "Position", Order = 1, GroupName = "07. Dashboard")] public SfpDashPos DashPosition { get; set; }
		[Display(Name = "Panel I - Liquidity State", Order = 2, GroupName = "07. Dashboard")] public bool ShowP1 { get; set; }
		[Display(Name = "Panel II - Liquidity Map", Order = 3, GroupName = "07. Dashboard")] public bool ShowP2 { get; set; }
		[Display(Name = "Panel III - Setup Analytics", Order = 4, GroupName = "07. Dashboard")] public bool ShowP3 { get; set; }
		[Display(Name = "Panel IV - Session Analytics", Order = 5, GroupName = "07. Dashboard")] public bool ShowP4 { get; set; }
		[Display(Name = "Panel V - Sweep Statistics", Order = 6, GroupName = "07. Dashboard")] public bool ShowP5 { get; set; }
		[Display(Name = "Higher-TF Bias (15m / 1H / 4H series)", Order = 7, GroupName = "07. Dashboard")] public bool ShowMtf { get; set; }

		[Display(Name = "Alerts (realtime)", Order = 0, GroupName = "08. Alerts")] public bool EnableAlerts { get; set; }
		[Display(Name = "New Levels", Order = 1, GroupName = "08. Alerts")] public bool AlLevels { get; set; }
		[Display(Name = "Sweep / SFP / MSS Events", Order = 2, GroupName = "08. Alerts")] public bool AlEvents { get; set; }
		[Display(Name = "Tracked Signals", Order = 3, GroupName = "08. Alerts")] public bool AlSignals { get; set; }
		[Display(Name = "Signal Outcomes", Order = 4, GroupName = "08. Alerts")] public bool AlOutcomes { get; set; }
		[Display(Name = "Approaching Liquidity", Order = 5, GroupName = "08. Alerts")] public bool AlProx { get; set; }

		#endregion

		#region Plot series (for automation tools)

		[Browsable(false)] [XmlIgnore] public Series<double> NearestSellSide { get { Update(); return Values[0]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> NearestBuySide { get { Update(); return Values[1]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> SignalStrength { get { Update(); return Values[2]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> SignalDir { get { Update(); return Values[3]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> EventCode { get { Update(); return Values[4]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> OpenSignals { get { Update(); return Values[5]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> SignalEntry { get { Update(); return Values[6]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> SignalStop { get { Update(); return Values[7]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> SignalTarget { get { Update(); return Values[8]; } }

		#endregion
	}
}
