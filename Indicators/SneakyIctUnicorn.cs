// SneakyIctUnicorn: NinjaTrader 8 port of the TradingView script "ICT Unicorn Model, Breaker Block & FVG Zone" (LunqFX, open source).
// Logic ported 1:1 from the Pine source (swing/sweep -> MSS close -> breaker block + overlapping FVG = unicorn zone -> tap -> T1/T2 or sweep-broken failure). Verified against a Python reference (0 event mismatches).
// Credit to the original author. Written on the closed bar (Calculate = On bar close).
// Plots (data only, prices or flags; 0 = none): 0 ZoneFormed(+1/-1 on that bar) 1 ZoneTap(+1/-1 on tap bar) 2 Stage(0 scan,2 MSS,3 armed,4 tapped) 3 ZoneTop 4 ZoneBot 5 EntryLimit(zone edge while armed) 6 StopPrice(sweep extreme -/+ 1 tick) 7 T1Price(live displacement extreme while armed) 8 T2Price 9 TargetHit(dir*1 or dir*2) 10 Failed(dir on the bar the sweep breaks)
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
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
internal class SuModel { public int Dir, SwB, MssB, BosB, BBar, ArmB, TapB = -1, EndB; public double SwX, MssP, BTop, BBot, FTop, FBot, ZTop, ZBot, DExt, DExtPrev, T1, T2; public int Hit; public bool Fail; public string End = ""; public int Stage; public int Id; }
internal class SuEvent { public int Bar, Kind, Dir, Hit; public string End; public SuModel M; public double ZBot, ZTop; } // Kind 0 form 1 tap 2 end 3 hit
internal class SuTrade { public int Dir, TapB, Id; public double Entry, Stop, T1, T2, Risk; public bool Open, Filled; public double R1, R2; public bool Done1, Done2; public int Out1, Out2; }
internal class SuCore
{
	public int PivLen = 5, FvgWait = 5, MaxWait = 60, MaxRun = 120; public double MinSweep = 0.1, MinZone = 0.15, Tick = 0.25, CostPts = 0.5;
	List<int> sT = new List<int>(); List<double> sP = new List<double>(); List<int> sB = new List<int>();
	List<double> O = new List<double>(), H = new List<double>(), Lo = new List<double>(), C = new List<double>();
	public double[] Atr; List<double> trs = new List<double>(); double atrV = double.NaN;
	public SuModel Cur; public int Stage, Dir; int usedSw = int.MinValue, nextId = 1;
	public List<SuEvent> Events = new List<SuEvent>(); public List<SuTrade> Trades = new List<SuTrade>(), NewTrades = new List<SuTrade>();
	public int[] Form = new int[2], Tap = new int[2], T1n = new int[2], T2n = new int[2], FailN = new int[2], WaitSum = new int[2];
	public int TrN1, TrN2; public double SumR1, SumR2; public int Win1, Win2; public double GrossW1, GrossL1, GrossW2, GrossL2;
	public double LastAtr => atrV;
	int i = -1;
	public void AddBar(double o, double h, double l, double c)
	{
		Events.Clear(); NewTrades.Clear(); O.Add(o); H.Add(h); Lo.Add(l); C.Add(c); i++;
		double tr = i == 0 ? h - l : Math.Max(h - l, Math.Max(Math.Abs(h - C[i - 1]), Math.Abs(l - C[i - 1])));
		trs.Add(tr);
		if (i == 13) { double s = 0; for (int k = 0; k < 14; k++) s += trs[k]; atrV = s / 14; } else if (i > 13) atrV = (atrV * 13 + tr) / 14;
		int L = PivLen; bool hasPh = false, hasPl = false; double ph = 0, pl = 0;
		if (i >= 2 * L)
		{
			int k = i - L; bool a = true, b = true;
			for (int j = 1; j <= L; j++) { if (!(H[k] > H[k - j])) a = false; if (!(H[k] >= H[k + j])) a = false; if (!(Lo[k] < Lo[k - j])) b = false; if (!(Lo[k] <= Lo[k + j])) b = false; }
			if (a) { hasPh = true; ph = H[k]; } if (b) { hasPl = true; pl = Lo[k]; }
		}
		int pB = i - L;
		if (hasPh && hasPl) { if (Math.Abs(ph - C[i - L]) > Math.Abs(C[i - L] - pl)) AddSwing(1, ph, pB); else AddSwing(-1, pl, pB); }
		else if (hasPh) AddSwing(1, ph, pB); else if (hasPl) AddSwing(-1, pl, pB);
		StepTrades(o, h, l, c);
		if (double.IsNaN(atrV)) return;
		double A = atrV; bool born = false;
		if (Stage == 4)
		{
			var S = Cur; bool fail = Dir == 1 ? c < S.SwX : c > S.SwX;
			if (fail) S.Fail = true;
			else for (int k = 1; k <= 2; k++) { double tk = k == 1 ? S.T1 : S.T2; if (S.Hit == k - 1 && (Dir == 1 ? h >= tk : l <= tk)) { S.Hit = k; Events.Add(new SuEvent { Bar = i, Kind = 3, Dir = Dir, Hit = k, M = S }); } }
			if (S.Fail || S.Hit == 2 || i - S.TapB > MaxRun)
			{
				int d = Dir == 1 ? 0 : 1; if (S.Hit >= 1) T1n[d]++; if (S.Hit >= 2) T2n[d]++; if (S.Fail) FailN[d]++;
				S.End = S.Fail ? "fail" : (S.Hit == 2 ? "done" : "exp"); S.EndB = i; Events.Add(new SuEvent { Bar = i, Kind = 2, Dir = Dir, End = S.End, Hit = S.Hit, M = S });
				Stage = 0; Dir = 0; Cur = null;
			}
		}
		if (Stage == 3)
		{
			var S = Cur; S.DExt = Dir == 1 ? Math.Max(S.DExt, h) : Math.Min(S.DExt, l);
			bool swept = Dir == 1 ? c < S.SwX : c > S.SwX; bool tapped = Dir == 1 ? l <= S.ZTop : h >= S.ZBot;
			if (tapped && !swept)
			{
				Stage = 4; S.TapB = i; S.Hit = 0; S.T1 = S.DExt; S.T2 = S.DExt + (S.DExt - S.SwX) * 0.5;
				int d = Dir == 1 ? 0 : 1; Tap[d]++; WaitSum[d] += i - S.ArmB;
				Events.Add(new SuEvent { Bar = i, Kind = 1, Dir = Dir, M = S });
				OpenTrade(S, o, h, l, c);
			}
			else if (swept || i - S.ArmB > MaxWait)
			{ S.End = swept ? "broken" : "missed"; S.EndB = i; Events.Add(new SuEvent { Bar = i, Kind = 2, Dir = Dir, End = S.End, M = S }); Stage = 0; Dir = 0; Cur = null; }
			else S.DExtPrev = S.DExt;
		}
		if (Stage == 2)
		{
			var S = Cur; S.DExt = Dir == 1 ? Math.Max(S.DExt, h) : Math.Min(S.DExt, l);
			double gT, gB; bool isGap;
			if (Dir == 1) { gT = l; gB = H[i - 2]; isGap = l > H[i - 2]; } else { gT = Lo[i - 2]; gB = h; isGap = h < Lo[i - 2]; }
			double oT = isGap ? OvTop(S, gT, gB, A) : double.NaN;
			if (!double.IsNaN(oT)) { S.FTop = gT; S.FBot = gB; S.ZTop = oT; S.ZBot = Math.Max(gB, S.BBot); Stage = 3; born = true; }
			else if (i - S.BosB > FvgWait) { Stage = 0; Dir = 0; Cur = null; }
		}
		if (Stage == 0 && sT.Count >= 3)
		{
			int n = sT.Count; int tA = sT[n - 3]; double pA = sP[n - 3], pM = sP[n - 2], pS = sP[n - 1]; int bM = sB[n - 2], bS = sB[n - 1];
			bool fresh = usedSw == int.MinValue || bS != usedSw;
			bool bull = tA == -1 && pM > pA && pS < pA - MinSweep * A && c > pM;
			bool bear = tA == 1 && pM < pA && pS > pA + MinSweep * A && c < pM;
			if (fresh && (bull || bear) && i - bM < 400)
			{
				usedSw = bS; Dir = bull ? 1 : -1;
				var S = new SuModel { Dir = Dir, SwX = pS, SwB = bS, MssP = pM, MssB = bM, BosB = i, DExt = bull ? h : l, Id = nextId++ };
				bool found = false;
				for (int q = i - bM; q <= Math.Min(i - bM + 15, 450); q++)
				{
					if (i - q < 0) break;
					if (bull ? C[i - q] > O[i - q] : C[i - q] < O[i - q]) { S.BTop = H[i - q]; S.BBot = Lo[i - q]; S.BBar = i - q; found = true; break; }
				}
				if (!found) { S.BTop = H[bM]; S.BBot = Lo[bM]; S.BBar = bM; }
				bool fg = false;
				for (int k = 0; k <= Math.Min(i - bS - 2, 60); k++)
				{
					if (i - k - 2 < 0) break;
					bool isG = bull ? Lo[i - k] > H[i - k - 2] : H[i - k] < Lo[i - k - 2];
					if (isG)
					{
						double gT = bull ? Lo[i - k] : Lo[i - k - 2], gB = bull ? H[i - k - 2] : H[i - k]; double oT = OvTop(S, gT, gB, A);
						if (!double.IsNaN(oT)) { S.FTop = gT; S.FBot = gB; S.ZTop = oT; S.ZBot = Math.Max(gB, S.BBot); fg = true; break; }
					}
				}
				Cur = S; Stage = fg ? 3 : 2; born = fg;
			}
		}
		if (born) { Cur.ArmB = i; Cur.DExtPrev = Cur.DExt; Form[Dir == 1 ? 0 : 1]++; Events.Add(new SuEvent { Bar = i, Kind = 0, Dir = Dir, M = Cur, ZBot = Cur.ZBot, ZTop = Cur.ZTop }); }
	}
	double OvTop(SuModel S, double gT, double gB, double A) { double oT = Math.Min(gT, S.BTop), oB = Math.Max(gB, S.BBot); return oT - oB >= Math.Max(MinZone * A, Tick) ? oT : double.NaN; }
	void AddSwing(int t, double p, int b)
	{
		int n = sT.Count;
		if (n > 0 && sT[n - 1] == t) { if (t == 1 ? p > sP[n - 1] : p < sP[n - 1]) { sP[n - 1] = p; sB[n - 1] = b; } }
		else { sT.Add(t); sP.Add(p); sB.Add(b); if (sT.Count > 20) { sT.RemoveAt(0); sP.RemoveAt(0); sB.RemoveAt(0); } }
	}
	// ---- realistic trade tracker: limit at zone edge, filled only if traded through by 1 tick; stop at sweep extreme -1 tick; exit at T1 (mode 1) / T2 (mode 2); time stop MaxRun bars
	void OpenTrade(SuModel S, double o, double h, double l, double c)
	{
		double zE = S.Dir == 1 ? S.ZTop : S.ZBot; bool through = S.Dir == 1 ? l <= zE - Tick : h >= zE + Tick; if (!through) return;
		double ep = S.Dir == 1 ? Math.Min(o, zE) : Math.Max(o, zE); double stop = S.SwX - S.Dir * Tick; double risk = (ep - stop) * S.Dir; if (risk <= 2 * Tick) return;
		double t1 = S.DExtPrev, t2 = t1 + (t1 - S.SwX) * 0.5;
		bool stopHit = S.Dir == 1 ? l <= stop : h >= stop;
		var t = new SuTrade { Dir = S.Dir, TapB = i, Id = S.Id, Entry = ep, Stop = stop, T1 = t1, T2 = t2, Risk = risk, Open = true };
		Trades.Add(t); NewTrades.Add(t);
		if (stopHit) { Fin(t, 1, ((stop - ep) * S.Dir - Tick - CostPts) / risk, -1); Fin(t, 2, ((stop - ep) * S.Dir - Tick - CostPts) / risk, -1); t.Open = false; }
	}
	void StepTrades(double o, double h, double l, double c)
	{
		foreach (var t in Trades)
		{
			if (!t.Open || t.TapB == i) continue;
			bool sh = t.Dir == 1 ? l <= t.Stop : h >= t.Stop; bool timeout = i - t.TapB >= 120;
			for (int m = 1; m <= 2; m++)
			{
				if (m == 1 ? t.Done1 : t.Done2) continue; double tg = m == 1 ? t.T1 : t.T2;
				if (sh) Fin(t, m, ((t.Stop - t.Entry) * t.Dir - Tick - CostPts) / t.Risk, -1);
				else if (t.Dir == 1 ? h >= tg : l <= tg) Fin(t, m, ((tg - t.Entry) * t.Dir - CostPts) / t.Risk, 1);
				else if (timeout) Fin(t, m, ((c - t.Entry) * t.Dir - CostPts) / t.Risk, 0);
			}
			if (t.Done1 && t.Done2) t.Open = false;
		}
		if (Trades.Count > 400) Trades.RemoveAll(x => !x.Open && x.TapB < i - 500);
	}
	void Fin(SuTrade t, int m, double r, int outc)
	{
		if (m == 1) { t.Done1 = true; t.R1 = r; t.Out1 = outc; TrN1++; SumR1 += r; if (r > 0) { Win1++; GrossW1 += r; } else GrossL1 -= r; }
		else { t.Done2 = true; t.R2 = r; t.Out2 = outc; TrN2++; SumR2 += r; if (r > 0) { Win2++; GrossW2 += r; } else GrossL2 -= r; }
	}
}

	public class SneakyIctUnicorn : Indicator
	{
		private SuCore core; private readonly Dictionary<long, System.Windows.Media.Brush> brushes = new Dictionary<long, System.Windows.Media.Brush>();
		private readonly List<SuModel> drawn = new List<SuModel>(); private readonly Dictionary<int, List<string>> tagsOf = new Dictionary<int, List<string>>();
		private readonly Dictionary<int, string> resultOf = new Dictionary<int, string>();
		private bool errShown; private int alertSeq;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "ICT Unicorn model (liquidity sweep + MSS + breaker block overlapping a fair value gap) with zone-tap tracking, honest simulated trade stats and automation plots. Port of LunqFX 'ICT Unicorn Model, Breaker Block & FVG Zone'.";
				Name = "SneakyIctUnicorn"; Calculate = Calculate.OnBarClose; IsOverlay = true; DisplayInDataBox = true; PaintPriceMarkers = false;
				IsSuspendedWhileInactive = true; ArePlotsConfigurable = true; IsAutoScale = false; DrawOnPricePanel = true;
				PivLen = 5; MinSweep = 0.1; FvgWait = 5; MinZone = 0.15; MaxWait = 60; MaxRun = 120;
				ShowParts = true; ShowStructure = true; ShowTargets = true; ModelsDrawn = 4; DrawBarsBack = 3000; LabelFont = 10;
				ShowCard = true; CardPos = SuCardPos.BottomLeft; CardFont = 12; CostPoints = 0.5; AlertsOn = true;
				for (int p = 0; p < 11; p++) { string[] nm = { "ZoneFormed", "ZoneTap", "Stage", "ZoneTop", "ZoneBot", "EntryLimit", "StopPrice", "T1Price", "T2Price", "TargetHit", "Failed" }; AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line, nm[p]); }
			}
			else if (State == State.Configure)
			{
				for (int p = 0; p < 11; p++) Plots[p].Brush = System.Windows.Media.Brushes.Transparent;
			}
			else if (State == State.DataLoaded)
			{
				core = new SuCore { PivLen = PivLen, MinSweep = MinSweep, FvgWait = FvgWait, MinZone = MinZone, MaxWait = MaxWait, MaxRun = MaxRun, Tick = TickSize, CostPts = CostPoints };
			}
			else if (State == State.Terminated) { brushes.Clear(); }
		}

		private System.Windows.Media.Brush B(int r, int g, int b, int a = 255)
		{
			long key = ((long)r << 24) | ((long)g << 16) | ((long)b << 8) | (long)a; System.Windows.Media.Brush br;
			if (!brushes.TryGetValue(key, out br)) { br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b)); br.Freeze(); brushes[key] = br; }
			return br;
		}
		private string Px(double v) { return v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture); }
		private string Pc(int a, int b) { return b > 0 ? (a * 100.0 / b).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%" : "-"; }
		private int[] Mc(int dir) { return dir == 1 ? new[] { 230, 241, 255 } : new[] { 61, 123, 255 }; }
		private int[] Uni { get { return new[] { 255, 79, 216 }; } }

		protected override void OnBarUpdate()
		{
			try { Inner(); }
			catch (Exception ex) { if (!errShown) { errShown = true; Print("SneakyIctUnicorn error: " + ex); } }
		}

		private List<string> Tags(int id) { List<string> l; if (!tagsOf.TryGetValue(id, out l)) { l = new List<string>(); tagsOf[id] = l; } return l; }

		private void Inner()
		{
			if (CurrentBar < 0) return;
			core.AddBar(Open[0], High[0], Low[0], Close[0]);
			bool lastBar = CurrentBar >= Count - 2; bool recent = CurrentBar >= Count - 1 - DrawBarsBack;
			var cur = core.Cur; int stage = core.Stage;
			for (int p = 0; p < 11; p++) Values[p][0] = 0;
			Values[2][0] = stage;
			foreach (var e in core.Events)
			{
				if (e.Kind == 0) { Values[0][0] = e.Dir; if (recent) { if (!drawn.Contains(e.M)) drawn.Add(e.M); } Notify(e.Dir == 1 ? "Bullish unicorn zone formed " + Px(e.ZBot) + " - " + Px(e.ZTop) : "Bearish unicorn zone formed " + Px(e.ZBot) + " - " + Px(e.ZTop)); }
				else if (e.Kind == 1) { Values[1][0] = e.Dir; Notify("Unicorn zone tapped, T1 " + Px(e.M.T1) + " T2 " + Px(e.M.T2) + " invalid beyond " + Px(e.M.SwX)); }
				else if (e.Kind == 3) { Values[9][0] = e.Dir * e.Hit; Notify("Unicorn T" + e.Hit + " hit"); }
				else if (e.Kind == 2)
				{
					if (e.End == "fail") { Values[10][0] = e.Dir; Notify("Unicorn failed: close beyond the sweep"); }
					string t = e.End == "done" ? "COMPLETE " + (e.M.EndB - e.M.TapB) + " bars" : e.End == "fail" ? "FAILED sweep broken" + (e.Hit == 1 ? " (T1 reached)" : "") : e.End == "exp" ? "EXPIRED" + (e.Hit == 1 ? " (T1 reached)" : "") : e.End == "broken" ? "SWEEP BROKEN no tap" : "ZONE MISSED";
					resultOf[e.M.Id] = t;
				}
			}
			if (cur != null && stage >= 3)
			{
				Values[3][0] = cur.ZTop; Values[4][0] = cur.ZBot; Values[6][0] = cur.SwX - cur.Dir * TickSize; Values[7][0] = stage == 4 ? cur.T1 : cur.DExt;
				Values[8][0] = stage == 4 ? cur.T2 : cur.DExt + (cur.DExt - cur.SwX) * 0.5; if (stage == 3) Values[5][0] = cur.Dir == 1 ? cur.ZTop : cur.ZBot;
			}
			if (recent)
			{
				foreach (var e in core.Events) if ((e.Kind == 0 || e.Kind == 1 || e.Kind == 2 || e.Kind == 3) && e.M != null && !drawn.Contains(e.M)) drawn.Add(e.M);
				foreach (var m in drawn.ToArray())
				{
					bool running = (m == cur); bool changed = false;
					foreach (var e in core.Events) if (e.M == m) changed = true;
					if (running || changed) DrawModel(m, running);
				}
				while (drawn.Count > ModelsDrawn)
				{
					var old = drawn[0]; if (old == cur) break; drawn.RemoveAt(0);
					List<string> l; if (tagsOf.TryGetValue(old.Id, out l)) { foreach (string t in l) RemoveDrawObject(t); tagsOf.Remove(old.Id); }
				}
			}
			if ((lastBar || State == State.Realtime) && ShowCard) DrawCard(cur, stage);
		}

		private void Notify(string msg)
		{
			if (AlertsOn && State == State.Realtime) Alert("SuAlert" + CurrentBar + "_" + (alertSeq++), Priority.Medium, "Unicorn: " + msg, NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, System.Windows.Media.Brushes.Black, System.Windows.Media.Brushes.Magenta);
		}

		private void DrawModel(SuModel m, bool running)
		{
			var tg = Tags(m.Id); var mc = Mc(m.Dir); var un = Uni; int id = m.Id; bool bull = m.Dir == 1;
			int endAbs = running ? CurrentBar + 2 : (m.EndB > 0 ? m.EndB : CurrentBar);
			if (!(m.ZTop > 0)) { if (ShowStructure) DrawStructure(m, tg, mc); return; }
			if (ShowStructure) DrawStructure(m, tg, mc);
			if (ShowParts)
			{
				Rect(tg, "SuBrk" + id, CurrentBar - m.BBar, m.BTop, CurrentBar - (m.ArmB + 1), m.BBot, B(148, 163, 184, 90), B(148, 163, 184), 14);
				Rect(tg, "SuFvg" + id, CurrentBar - (m.ArmB - 1), m.FTop, CurrentBar - (m.ArmB + 1), m.FBot, B(34, 211, 238, 110), B(34, 211, 238), 14);
			}
			int zStart = Math.Max(m.BBar, m.ArmB - 1); int endAgo = CurrentBar - endAbs;
			Rect(tg, "SuZone" + id, CurrentBar - zStart, m.ZTop, endAgo, m.ZBot, B(un[0], un[1], un[2]), B(un[0], un[1], un[2]), 30);
			string tx;
			if (m.End != "" && resultOf.ContainsKey(id)) tx = resultOf[id] + " | ZONE " + Px(m.ZBot) + " - " + Px(m.ZTop);
			else if (core.Stage == 4 && running) tx = (bull ? "^ " : "v ") + "UNICORN " + Px(m.ZBot) + " - " + Px(m.ZTop) + " | INVALID " + Px(m.SwX);
			else tx = (bull ? "^ " : "v ") + "UNICORN " + Px(m.ZBot) + " - " + Px(m.ZTop);
			bool done = m.End != "" && resultOf.ContainsKey(id); var lc = done ? B(110, 112, 128) : B(un[0], un[1], un[2]);
			Draw.Text(this, "SuLbl" + id, false, " " + tx + " ", running ? 0 : endAgo, (m.ZTop + m.ZBot) / 2, 0, B(255, 255, 255), new SimpleFont("Arial", LabelFont) { Bold = true }, running ? System.Windows.TextAlignment.Right : System.Windows.TextAlignment.Left, B(255, 255, 255, 120), lc, 100);
			if (!tg.Contains("SuLbl" + id)) tg.Add("SuLbl" + id);
			if (m.TapB >= 0)
			{
				Draw.Text(this, "SuTap" + id, false, "ZONE TAP", CurrentBar - m.TapB, bull ? Low[CurrentBar - m.TapB] : High[CurrentBar - m.TapB], bull ? 14 : -14, B(255, 255, 255), new SimpleFont("Arial", LabelFont) { Bold = true }, System.Windows.TextAlignment.Center, B(255, 255, 255, 120), B(un[0], un[1], un[2]), 100);
				if (!tg.Contains("SuTap" + id)) tg.Add("SuTap" + id);
				if (ShowTargets)
				{
					Seg(tg, "SuT1" + id, m.TapB, m.T1, endAbs, B(mc[0], mc[1], mc[2], 190), DashStyleHelper.Dash, 1);
					Seg(tg, "SuT2" + id, m.TapB, m.T2, endAbs, B(mc[0], mc[1], mc[2]), DashStyleHelper.Solid, 2);
					Draw.Text(this, "SuT1L" + id, false, " " + (m.Hit >= 1 ? "OK " : "") + "T1 " + Px(m.T1) + " ", -(endAbs - CurrentBar), m.T1, 0, B(230, 240, 255), new SimpleFont("Arial", Math.Max(8, LabelFont - 1)), System.Windows.TextAlignment.Left, B(255, 255, 255, 0), B(20, 25, 40), 90);
					Draw.Text(this, "SuT2L" + id, false, " " + (m.Hit >= 2 ? "OK " : "") + "T2 " + Px(m.T2) + " ", -(endAbs - CurrentBar), m.T2, 0, bull ? B(8, 8, 12) : B(255, 255, 255), new SimpleFont("Arial", Math.Max(8, LabelFont - 1)) { Bold = true }, System.Windows.TextAlignment.Left, B(255, 255, 255, 0), B(mc[0], mc[1], mc[2]), 100);
					foreach (string s in new[] { "SuT1L" + id, "SuT2L" + id }) if (!tg.Contains(s)) tg.Add(s);
				}
			}
		}

		private void DrawStructure(SuModel m, List<string> tg, int[] mc)
		{
			int id = m.Id; bool bull = m.Dir == 1;
			Draw.Text(this, "SuSw" + id, false, "SWEEP", CurrentBar - m.SwB, m.SwX, bull ? 16 : -16, B(mc[0], mc[1], mc[2]), new SimpleFont("Arial", Math.Max(8, LabelFont - 1)) { Bold = true }, System.Windows.TextAlignment.Center, B(255, 255, 255, 0), B(10, 12, 18), 90);
			Draw.Line(this, "SuMss" + id, false, CurrentBar - m.MssB, m.MssP, CurrentBar - m.BosB, m.MssP, B(mc[0], mc[1], mc[2]), DashStyleHelper.Dash, 2);
			Draw.Text(this, "SuMssL" + id, false, "MSS", CurrentBar - m.BosB - 1, m.MssP, 0, B(mc[0], mc[1], mc[2]), new SimpleFont("Arial", Math.Max(8, LabelFont - 1)) { Bold = true }, System.Windows.TextAlignment.Left, B(255, 255, 255, 0), B(255, 255, 255, 0), 0);
			foreach (string s in new[] { "SuSw" + id, "SuMss" + id, "SuMssL" + id }) if (!tg.Contains(s)) tg.Add(s);
		}

		private void Rect(List<string> tg, string tag, int startAgo, double top, int endAgo, double bot, System.Windows.Media.Brush outline, System.Windows.Media.Brush area, int op)
		{
			Draw.Rectangle(this, tag, false, startAgo, top, endAgo, bot, outline, area, op); if (!tg.Contains(tag)) tg.Add(tag);
		}
		private void Seg(List<string> tg, string tag, int fromAbs, double px, int toAbs, System.Windows.Media.Brush br, DashStyleHelper ds, int w)
		{
			Draw.Line(this, tag, false, CurrentBar - fromAbs, px, CurrentBar - toAbs, px, br, ds, w); if (!tg.Contains(tag)) tg.Add(tag);
		}

		private class CRow { public string L = "", R = ""; public int[] LFg, RFg, RBg, LBg; public int Kind; public int Step; public bool Center; }
		private List<CRow> card = new List<CRow>(); private readonly object cardLock = new object();

		private static readonly int[] cPanel = { 10, 12, 18 }, cRow = { 20, 24, 38 }, cMute = { 143, 150, 166 }, cTxt = { 232, 234, 240 }, cUni = { 255, 79, 216 }, cUnix = { 255, 155, 234 }, cGreen = { 60, 220, 130 }, cRed = { 255, 90, 100 };

		private CRow Row(string l, string r, int[] lf = null, int[] rf = null, int[] bg = null) { return new CRow { L = l, R = r, LFg = lf ?? cMute, RFg = rf ?? cTxt, LBg = bg ?? cRow, RBg = bg ?? cRow }; }

		private CRow TrRow(string name, int n, int win, double sum, double gw, double gl)
		{
			if (n == 0) return Row(name, "no trades yet", null, cMute, cPanel);
			double avg = sum / n; var col = avg > 0.005 ? cGreen : avg < -0.005 ? cRed : cTxt;
			return Row(name, string.Format(System.Globalization.CultureInfo.InvariantCulture, "n={0}  win {1}  avg {2:+0.00;-0.00}R  total {3:+0.0;-0.0}R  PF {4:0.00}", n, Pc(win, n), avg, sum, gl > 0 ? gw / gl : 0), null, col, cPanel);
		}

		private void DrawCard(SuModel cur, int stage)
		{
			var rows = new List<CRow>(); var mc = cur != null ? Mc(cur.Dir) : cTxt; bool lng = cur != null && cur.Dir == 1;
			string st = stage == 0 ? "SCANNING" : stage == 2 ? "MSS \u00b7 LOOKING FOR FVG" : stage == 3 ? "ZONE ARMED" : cur != null && cur.Hit >= 1 ? "T1 \u2713 \u00b7 RUNNING" : "IN ZONE";
			int step = stage == 0 ? 0 : stage == 2 ? 2 : stage == 3 ? 3 : 4 + (cur != null ? cur.Hit : 0);
			rows.Add(new CRow { Kind = 1, L = "\u25c6 ICT UNICORN", R = stage == 0 ? st : (lng ? "\u25b2 " : "\u25bc ") + st, LFg = cUnix, LBg = cPanel, RFg = stage == 0 ? cTxt : (lng ? new[] { 10, 12, 18 } : new[] { 255, 255, 255 }), RBg = stage == 0 ? cRow : mc, Center = true });
			rows.Add(new CRow { Kind = 2, Step = step, L = step + " / 6", R = "sweep \u00b7 MSS \u00b7 FVG \u00b7 tap \u00b7 T1 \u00b7 T2", LFg = stage == 0 ? cMute : cUnix, RFg = cMute, LBg = cRow, RBg = cRow });
			bool z = cur != null && stage >= 3;
			double dist = z && stage == 3 && core.LastAtr > 0 ? Math.Abs(Close[0] - (cur.Dir == 1 ? cur.ZTop : cur.ZBot)) / core.LastAtr : -1;
			rows.Add(Row("Unicorn zone", z ? Px(cur.ZBot) + " \u2013 " + Px(cur.ZTop) + (dist >= 0 ? "  \u00b7  " + dist.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ATR away" : "") : "\u2014", null, z ? cUnix : cMute));
			rows.Add(Row("Breaker \u00b7 FVG", z ? Px(cur.BBot) + "\u2013" + Px(cur.BTop) + "  \u00b7  " + Px(cur.FBot) + "\u2013" + Px(cur.FTop) : "\u2014", null, z ? cTxt : cMute));
			rows.Add(Row("T1 \u00b7 T2", stage == 4 ? (cur.Hit >= 1 ? "\u2713 " : "") + Px(cur.T1) + "  \u00b7  " + (cur.Hit >= 2 ? "\u2713 " : "") + Px(cur.T2) : "\u2014", null, stage == 4 ? mc : cMute));
			rows.Add(Row("Invalid", z ? "close beyond the sweep " + Px(cur.SwX) : "\u2014", null, z ? cTxt : cMute));
			int nF = core.Form[0] + core.Form[1], nT = core.Tap[0] + core.Tap[1]; int nW = core.WaitSum[0] + core.WaitSum[1];
			rows.Add(Row("TRACK RECORD", nF + " unicorns on this chart", cUnix, cMute, cPanel));
			rows.Add(Row("Zone tapped", Pc(nT, nF) + "   \u25b2 " + Pc(core.Tap[0], core.Form[0]) + " \u00b7 \u25bc " + Pc(core.Tap[1], core.Form[1]) + " \u00b7 avg " + (nT > 0 ? ((double)nW / nT).ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "\u2014") + " bars", null, cTxt, cPanel));
			rows.Add(Row("After the tap", "T1 " + Pc(core.T1n[0] + core.T1n[1], nT) + " \u00b7 T2 " + Pc(core.T2n[0] + core.T2n[1], nT), null, cTxt, cPanel));
			rows.Add(Row("Failed (sweep broken)", Pc(core.FailN[0] + core.FailN[1], nT) + " of tapped zones", null, cMute, cPanel));
			rows.Add(Row("SIM TRADES", "limit at zone edge \u00b7 stop beyond sweep \u00b7 cost " + CostPoints.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + "pt", cUnix, cMute, cPanel));
			rows.Add(TrRow("Exit all at T1", core.TrN1, core.Win1, core.SumR1, core.GrossW1, core.GrossL1));
			rows.Add(TrRow("Exit all at T2", core.TrN2, core.Win2, core.SumR2, core.GrossW2, core.GrossL2));
			lock (cardLock) card = rows;
			if (ChartControl != null) ChartControl.InvalidateVisual();
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (!ShowCard || RenderTarget == null || ChartPanel == null) return;
			try { RenderCard(); } catch (Exception ex) { if (!errShown) { errShown = true; Print("SneakyIctUnicorn OnRender error: " + ex); } }
		}

		private static SharpDX.Color4 K(int[] c, float a = 1f) { return new SharpDX.Color4(c[0] / 255f, c[1] / 255f, c[2] / 255f, a); }

		private void RenderCard()
		{
			List<CRow> rows; lock (cardLock) rows = card; if (rows == null || rows.Count == 0) return;
			var disp = new List<IDisposable>(); bool scaled = false;
			try
			{
				float k = Math.Max(0.6f, CardFont / 12f); float pw = (float)ChartPanel.W / k, ph = (float)ChartPanel.H / k;
				const float w0 = 150f, w1 = 300f, rh = 26f, pad = 10f; float tw = w0 + w1, th = rows.Count * rh;
				RenderTarget.Transform = SharpDX.Matrix3x2.Scaling(k); scaled = true;
				bool right = CardPos == SuCardPos.TopRight || CardPos == SuCardPos.BottomRight, top = CardPos == SuCardPos.TopLeft || CardPos == SuCardPos.TopRight;
				float x0 = right ? pw - tw - 16f : 16f, y0 = top ? 12f : Math.Max(4f, ph - th - 34f);
				var fmt = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", 12f) { ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center }; disp.Add(fmt);
				var fmtB = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI Semibold", 12f) { ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center }; disp.Add(fmtB);
				var fmtC = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI Semibold", 12f) { ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center, TextAlignment = SharpDX.DirectWrite.TextAlignment.Center }; disp.Add(fmtC);
				var bgAll = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, K(cPanel, 0.96f)); disp.Add(bgAll);
				RenderTarget.FillRectangle(new SharpDX.RectangleF(x0 - 3f, y0 - 3f, tw + 6f, th + 6f), bgAll);
				float y = y0;
				foreach (var r in rows)
				{
					var lb = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, K(r.LBg ?? cRow, 0.96f)); disp.Add(lb);
					var rb = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, K(r.RBg ?? cRow, 0.96f)); disp.Add(rb);
					RenderTarget.FillRectangle(new SharpDX.RectangleF(x0, y + 1f, w0 - 1f, rh - 2f), lb);
					RenderTarget.FillRectangle(new SharpDX.RectangleF(x0 + w0, y + 1f, w1, rh - 2f), rb);
					var lf = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, K(r.LFg ?? cMute)); disp.Add(lf);
					var rf = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, K(r.RFg ?? cTxt)); disp.Add(rf);
					float lx = x0 + pad;
					if (r.Kind == 2)
					{
						var on = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, K(cUni)); disp.Add(on); var off = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, K(new[] { 60, 66, 90 })); disp.Add(off);
						for (int q = 0; q < 6; q++) RenderTarget.FillRectangle(new SharpDX.RectangleF(lx + q * 13f, y + 8f, 10f, 10f), q < r.Step ? on : off);
						lx += 6 * 13f + 6f;
					}
					RenderTarget.DrawText(r.L, r.Kind == 1 || r.LFg == cUnix ? fmtB : fmt, new SharpDX.RectangleF(lx, y, w0 - (lx - x0), rh), lf);
					RenderTarget.DrawText(r.R, r.Center ? fmtC : (r.RFg == cTxt || r.RFg == cGreen || r.RFg == cRed ? fmtB : fmt), new SharpDX.RectangleF(x0 + w0 + (r.Center ? 0f : pad), y, w1 - (r.Center ? 0f : pad), rh), rf);
					y += rh;
				}
				var fr = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, K(cUni, 0.55f)); disp.Add(fr);
				RenderTarget.DrawRectangle(new SharpDX.RectangleF(x0 - 3f, y0 - 3f, tw + 6f, th + 6f), fr, 1.5f);
			}
			finally { if (scaled) RenderTarget.Transform = SharpDX.Matrix3x2.Identity; foreach (var d in disp) d.Dispose(); }
		}

		#region Properties
		[Range(2, 20)] [Display(Name = "Swing strength", Order = 0, GroupName = "01. Model")] public int PivLen { get; set; }
		[Range(0, 5)] [Display(Name = "Min liquidity sweep (x ATR)", Order = 1, GroupName = "01. Model")] public double MinSweep { get; set; }
		[Range(0, 30)] [Display(Name = "Bars to find FVG after MSS", Order = 2, GroupName = "01. Model")] public int FvgWait { get; set; }
		[Range(0, 3)] [Display(Name = "Min zone height (x ATR)", Order = 3, GroupName = "01. Model")] public double MinZone { get; set; }
		[Range(5, 500)] [Display(Name = "Bars to wait for the tap", Order = 4, GroupName = "01. Model")] public int MaxWait { get; set; }
		[Range(5, 1000)] [Display(Name = "Bars after the tap", Order = 5, GroupName = "01. Model")] public int MaxRun { get; set; }
		[Display(Name = "Breaker and FVG boxes", Order = 0, GroupName = "02. Drawing")] public bool ShowParts { get; set; }
		[Display(Name = "Sweep and MSS", Order = 1, GroupName = "02. Drawing")] public bool ShowStructure { get; set; }
		[Display(Name = "Targets", Order = 2, GroupName = "02. Drawing")] public bool ShowTargets { get; set; }
		[Range(1, 50)] [Display(Name = "Models drawn", Order = 3, GroupName = "02. Drawing")] public int ModelsDrawn { get; set; }
		[Range(100, 100000)] [Display(Name = "Draw bars back", Order = 4, GroupName = "02. Drawing")] public int DrawBarsBack { get; set; }
		[Range(8, 20)] [Display(Name = "Label font size", Order = 5, GroupName = "02. Drawing")] public int LabelFont { get; set; }
		[Display(Name = "Show card", Order = 0, GroupName = "03. Card")] public bool ShowCard { get; set; }
		[Display(Name = "Card position", Order = 1, GroupName = "03. Card")] public SuCardPos CardPos { get; set; }
		[Range(8, 20)] [Display(Name = "Card font size", Order = 2, GroupName = "03. Card")] public int CardFont { get; set; }
		[Range(0, 20)] [Display(Name = "Sim cost (points round trip)", Order = 3, GroupName = "03. Card")] public double CostPoints { get; set; }
		[Display(Name = "Alerts (realtime)", Order = 0, GroupName = "04. Alerts")] public bool AlertsOn { get; set; }

		[Browsable(false)] [XmlIgnore] public Series<double> ZoneFormed { get { Update(); return Values[0]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> ZoneTap { get { Update(); return Values[1]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> StageNow { get { Update(); return Values[2]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> ZoneTop { get { Update(); return Values[3]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> ZoneBot { get { Update(); return Values[4]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> EntryLimit { get { Update(); return Values[5]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> StopPrice { get { Update(); return Values[6]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> T1Price { get { Update(); return Values[7]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> T2Price { get { Update(); return Values[8]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> TargetHit { get { Update(); return Values[9]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> Failed { get { Update(); return Values[10]; } }
		#endregion
	}
	public enum SuCardPos { BottomLeft, TopLeft, TopRight, BottomRight }
}
