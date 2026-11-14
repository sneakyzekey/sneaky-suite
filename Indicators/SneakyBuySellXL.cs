// SneakyBuySellXL: NinjaTrader 8 port of the TradingView script "Buy/Sell Signals XL" (six signal engines: OC, Breakout, Donchian, MA Cross, Supertrend, Aroon).
// Signals, stop/target logic, lookback statistics, info box, multi-timeframe scanner and engine scanner are ported from the Pine source. The six engines are verified
// against a Python reference (identical signals, entries, stops, targets and exits on 20,000 NQ bars, default and 10 non-default configurations).
// Runs on closed bars (equivalent to the original's "Entries + Exits" non-repaint mode). Credit to the original author.
// Plots (data only): 0 Signal(+1 BUY / -1 SELL on the signal bar) 1 Entry 2 StopLoss 3 TakeProfit (prices on the signal bar) 4 TradeDir(+1/-1 while a trade is open) 5 ExitCode(+1 TP, -1 SL, 0.5 reversal on that bar; 0 none)
// Visual plots: 6 DonchianUpper 7 DonchianLower 8 DonchianMiddle 9 FastMA 10 SlowMA 11 TrendMA 12 SupertrendUp 13 SupertrendDown 14 BreakoutTrendEMA
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Text;
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
internal static class XlU { public static bool N(double x) { return double.IsNaN(x); } public static double Nz(double x, double d) { return double.IsNaN(x) ? d : x; } }
// ---- streaming Pine ta.* helpers (NaN semantics: windows containing NaN give NaN) ----
internal class XlWin
{
	public double[] B; public int Cnt, Pos, Len; public XlWin(int n) { Len = Math.Max(1, n); B = new double[Len]; }
	public void Push(double x) { B[Pos] = x; Pos = (Pos + 1) % Len; if (Cnt < Len) Cnt++; }
	public bool Full { get { return Cnt >= Len; } }
	public double At(int back) { return B[((Pos - 1 - back) % Len + Len) % Len]; }   // back=0 newest
	public bool AllValid() { if (!Full) return false; for (int i = 0; i < Len; i++) if (double.IsNaN(B[i])) return false; return true; }
}
internal class XlMa
{
	string kind; int n; XlWin w; double p = double.NaN; XlMa a, b, c; double alpha;
	public XlMa(string k, int len)
	{
		kind = k; n = Math.Max(1, len); w = new XlWin(n); alpha = 2.0 / (n + 1);
		if (k == "HMA") { a = new XlMa("WMA", Math.Max(1, n / 2)); b = new XlMa("WMA", n); c = new XlMa("WMA", Math.Max(1, (int)Math.Floor(Math.Sqrt(n) + 0.5))); }
	}
	public double Update(double x)
	{
		if (kind == "HMA") { double ya = a.Update(x), yb = b.Update(x); return c.Update(2 * ya - yb); }
		w.Push(x);
		if (kind == "SMA") return w.AllValid() ? Mean() : double.NaN;
		if (kind == "WMA") { if (!w.AllValid()) return double.NaN; double s = 0; for (int i = 0; i < n; i++) s += w.At(i) * (n - i); return s / (n * (n + 1) / 2.0); }
		bool ema = kind == "EMA";
		if (double.IsNaN(p)) { if (w.AllValid()) p = Mean(); else p = double.NaN; }
		else p = double.IsNaN(x) ? double.NaN : (ema ? alpha * x + (1 - alpha) * p : (x + (n - 1) * p) / n);
		return p;
	}
	double Mean() { double s = 0; for (int i = 0; i < n; i++) s += w.B[i]; return s / n; }
}
internal class XlHl // highest / lowest / bars-offset over a window
{
	XlWin w; bool hi; public XlHl(int n, bool high) { w = new XlWin(n); hi = high; }
	public double Val; public int Off;   // Off <= 0 offset of extreme (newest wins ties)
	public void Update(double x)
	{
		w.Push(x); if (!w.Full) { Val = double.NaN; Off = 0; return; }
		double m = w.At(0); int off = 0; for (int i = 1; i < w.Len; i++) { double y = w.At(i); if (hi ? y > m : y < m) { m = y; off = -i; } }
		Val = m; Off = off;
	}
}
internal class XlP
{
	public bool UseRisk = true, ExitClose = false; public int AtrLen = 14, Lookback = 20;
	public string SlMode, TpMode; public double SlBuf, SlMult, TpMult, TpR, TpB = 1.0; public bool Alt, NoOvl = true, NoBase = true; public double Vol; public int VolLen = 50;
	public int BodyLen = 50; public double BodyMult = 3.0;
	public bool BaseVar = true; public int BaseLen = 10, VarMin = 5, VarMax = 30; public double BaseMult = 2.0, MinBreak; public string BrkMode = "Bar Close (Edge)"; public bool Trend; public int TrendLen = 200;
	public int DonLen = 20; public bool EntEdge = true;
	public int Fast = 9, Slow = 21; public string MaType = "EMA", Src = "Close"; public double MinSep; public int SwingLen = 5; public double SlMin = 1.0;
	public int StLen = 10; public double StMult = 3.0;
	public int ArLen = 14; public bool ArStrong; public double ArStrongLvl = 70; public string ArSmooth = "None"; public int ArSmLen = 3;
	public static XlP For(string mode)
	{
		var p = new XlP();
		switch (mode)
		{
			case "OC": p.SlMode = "Candle wick"; p.TpMode = "SL multiple"; p.SlMult = 1.5; p.TpMult = 3.0; p.TpR = 1.0; p.Alt = true; break;
			case "Breakout": p.SlMode = "Base edge"; p.TpMode = "Base height"; p.SlMult = 1.5; p.TpMult = 3.0; p.TpR = 2.0; p.Alt = true; p.EntEdge = true; break;
			case "Donchian": p.SlMode = "Channel opposite"; p.TpMode = "SL multiple"; p.SlMult = 2.0; p.TpMult = 3.0; p.TpR = 0.5; p.Alt = true; p.EntEdge = true; break;
			case "MA Cross": p.SlMode = "ATR"; p.TpMode = "SL multiple"; p.SlMult = 2.0; p.TpMult = 3.0; p.TpR = 1.0; p.Alt = false; p.EntEdge = true; break;
			case "Supertrend": p.SlMode = "Supertrend line"; p.TpMode = "SL multiple"; p.SlMult = 1.5; p.TpMult = 3.0; p.TpR = 0.5; p.Alt = false; p.EntEdge = false; break;
			default: p.SlMode = "ATR"; p.TpMode = "SL multiple"; p.SlMult = 1.5; p.TpMult = 3.0; p.TpR = 1.0; p.Alt = true; break;
		}
		return p;
	}
	public XlP Clone() { return (XlP)MemberwiseClone(); }
}
internal class XlEngine
{
	public string Mode; public XlP P; public double SecPerBar = 60;
	// outputs for the bar just processed
	public bool Buy, Sell, SlHit, TpHit, Rev; public double ExitPx, ExitPnl;
	// state
	public int Bar = -1; public int Dir; public bool InTrade; public int LastSigBar = -1, LastExitBar = -1; public double EntryPx = double.NaN, SlPx = double.NaN, TpPx = double.NaN;
	public int EntryBar; public long EntryMs, LastSigMs = long.MinValue; public int LastRes; // 1 tp, -1 sl, 0 rev (of last closed trade)
	public List<double> Pnls = new List<double>(), Gaps = new List<double>(), GapsM = new List<double>(), Durs = new List<double>(), DursM = new List<double>(); public List<int> Reasons = new List<int>();
	public double SlAtrX = double.NaN, Atr = double.NaN, LastClose; public int SigBar; public int BaseN; public double BaseHi = double.NaN, BaseLo = double.NaN; public double LastBarMs; public int LastSigBarIdx = -1; public long LastBarTimeMs;
	public int ClosedTrades; public double DonUp = double.NaN, DonLoV = double.NaN, DonMidV = double.NaN, MaFast = double.NaN, MaSlow = double.NaN, MaTrend = double.NaN, StLineV = double.NaN, EmaTrend = double.NaN; public int StDirV;
	bool isOC, isBrk, isDon, isMA, isST, isAr; bool hasSl, hasTp, riskOn, both, slAtr, slWick, slBody, slEdge, slOpp, slMid, slSwing, slStl, slAroon, tpAtr, tpBase, tpRisk;
	List<double> H = new List<double>(), L = new List<double>();
	XlMa atrMa, ocBodyMa, ocVolMa, donVolMa, fast, slow, trendMa, maVolMa, brkEma, brkVolMa, arUpS, arDnS, arVolMa, stAtr; XlHl donHi, donLo, fixHi, fixLo, swLo, swHi, arHiW, arLoW, arHb, arLb;
	double prevClose = double.NaN, prevBodyHi = double.NaN, prevBodyLo = double.NaN, prevDonHi = double.NaN, prevDonLo = double.NaN, prevFixHi = double.NaN, prevFixLo = double.NaN;
	double prevFast = double.NaN, prevSlow = double.NaN, prevArUp = double.NaN, prevArDn = double.NaN; bool prevSB, prevSE;
	double stPrevLb = 0, stPrevUb = 0, stPrevSt = double.NaN, stPrevAtr = double.NaN, stLine = double.NaN, stPrevLine = double.NaN; int stDir = 1, stPrevDir = 1; double stLbNow, stUbNow;
	public XlEngine(string mode, XlP p, double secPerBar)
	{
		Mode = mode; P = p; SecPerBar = secPerBar; isOC = mode == "OC"; isBrk = mode == "Breakout"; isDon = mode == "Donchian"; isMA = mode == "MA Cross"; isST = mode == "Supertrend"; isAr = mode == "Aroon";
		string sl = p.SlMode, tp = p.TpMode; hasSl = sl != "None"; slAtr = sl == "ATR"; slWick = sl == "Candle wick"; slBody = sl == "Candle body"; slEdge = sl == "Base edge"; slOpp = sl == "Channel opposite"; slMid = sl == "Channel middle"; slSwing = sl == "Swing"; slStl = sl == "Supertrend line"; slAroon = sl == "Aroon extreme";
		tpAtr = tp == "ATR target"; tpBase = tp == "Base height"; tpRisk = tp == "SL multiple"; hasTp = tp != "Opposite signal" && !(tpRisk && !hasSl); riskOn = p.UseRisk && (hasSl || hasTp); both = hasSl && hasTp;
		atrMa = new XlMa("RMA", p.AtrLen);
		if (isOC) { ocBodyMa = new XlMa("SMA", p.BodyLen); ocVolMa = new XlMa("SMA", p.BodyLen); }
		if (isDon) { donHi = new XlHl(p.DonLen, true); donLo = new XlHl(p.DonLen, false); donVolMa = new XlMa("SMA", p.DonLen); }
		if (isMA) { fast = new XlMa(p.MaType, p.Fast); slow = new XlMa(p.MaType, p.Slow); trendMa = new XlMa(p.MaType, p.TrendLen); maVolMa = new XlMa("SMA", p.Slow); swLo = new XlHl(p.SwingLen, false); swHi = new XlHl(p.SwingLen, true); }
		if (isBrk) { brkEma = new XlMa("EMA", p.TrendLen); brkVolMa = new XlMa("SMA", p.VolLen); fixHi = new XlHl(p.BaseLen, true); fixLo = new XlHl(p.BaseLen, false); }
		if (isST) { stAtr = new XlMa("RMA", p.StLen); }
		if (isAr) { arHb = new XlHl(p.ArLen + 1, true); arLb = new XlHl(p.ArLen + 1, false); arHiW = new XlHl(p.ArLen, true); arLoW = new XlHl(p.ArLen, false); arVolMa = new XlMa("SMA", p.VolLen); string s = p.ArSmooth; if (s != "None") { arUpS = new XlMa(s, s == "HMA" ? Math.Max(p.ArSmLen, 2) : p.ArSmLen); arDnS = new XlMa(s, s == "HMA" ? Math.Max(p.ArSmLen, 2) : p.ArSmLen); } }
	}
	double Smooth(XlMa m, double x) { return m == null ? x : m.Update(x); }
	double H1(int k) { int j = H.Count - 1 - k; return j >= 0 ? H[j] : double.NaN; }   // high[k] relative to the CURRENT bar (already appended)
	double L1(int k) { int j = L.Count - 1 - k; return j >= 0 ? L[j] : double.NaN; }
	public void Step(long ms, double o, double h, double l, double c, double v)
	{
		Bar++; int i = Bar; Buy = Sell = SlHit = TpHit = Rev = false; H.Add(h); L.Add(l); if (H.Count > 700) { H.RemoveAt(0); L.RemoveAt(0); } LastClose = c; LastBarTimeMs = ms;
		double tr = i == 0 ? h - l : Math.Max(h - l, Math.Max(Math.Abs(h - prevClose), Math.Abs(l - prevClose)));
		double A = atrMa.Update(tr); Atr = A; var p = P; bool An = XlU.N(A);
		double body = Math.Abs(c - o), bodyHi = Math.Max(o, c), bodyLo = Math.Min(o, c);
		bool bu = false, be = false; double cHi = double.NaN, cLo = double.NaN; int baseN = 0; double buf = 0, upB = double.NaN, loB = double.NaN, midB = double.NaN, slowV = double.NaN, swLoV = double.NaN, swHiV = double.NaN, arLo = double.NaN, arHi = double.NaN, stEn = double.NaN;
		if (isOC)
		{
			double ab = ocBodyMa.Update(body), av = ocVolMa.Update(v); bool vok = p.Vol <= 0 || (!XlU.N(av) && v >= av * p.Vol);
			bu = c > o && i > 0 && c > prevBodyHi && body >= ab * p.BodyMult && vok; be = c < o && i > 0 && c < prevBodyLo && body >= ab * p.BodyMult && vok;
		}
		else if (isDon)
		{
			upB = prevDonHi; loB = prevDonLo; midB = (upB + loB) / 2; donHi.Update(h); donLo.Update(l); prevDonHi = donHi.Val; prevDonLo = donLo.Val; double av = donVolMa.Update(v);
			bool vok = p.Vol <= 0 || (!XlU.N(av) && v >= av * p.Vol); bu = c > upB && vok; be = c < loB && vok; DonUp = upB; DonLoV = loB; DonMidV = midB;
		}
		else if (isMA)
		{
			double sv = p.Src == "Open" ? o : p.Src == "High" ? h : p.Src == "Low" ? l : p.Src == "HL2" ? (h + l) / 2 : p.Src == "HLC3" ? (h + l + c) / 3 : p.Src == "OHLC4" ? (o + h + l + c) / 4 : c;
			double f = fast.Update(sv), s = slow.Update(sv), t = trendMa.Update(sv); slowV = s; double av = maVolMa.Update(v); swLo.Update(l); swHi.Update(h); swLoV = swLo.Val; swHiV = swHi.Val;
			bool vok = p.Vol <= 0 || (!XlU.N(av) && v >= av * p.Vol); bool sep = p.MinSep <= 0 || (!An && Math.Abs(f - s) >= A * p.MinSep);
			bool xu = !XlU.N(prevFast) && !XlU.N(prevSlow) && !XlU.N(f) && !XlU.N(s) && f > s && prevFast <= prevSlow; bool xd = !XlU.N(prevFast) && !XlU.N(prevSlow) && !XlU.N(f) && !XlU.N(s) && f < s && prevFast >= prevSlow;
			bu = xu && vok && sep && (!p.Trend || c > t); be = xd && vok && sep && (!p.Trend || c < t); prevFast = f; prevSlow = s; MaFast = f; MaSlow = s; MaTrend = t;
		}
		else if (isST)
		{
			double a2 = stAtr.Update(tr), src = (h + l) / 2; double up = src + p.StMult * a2, lo = src - p.StMult * a2; double pL = stPrevLb, pU = stPrevUb; double c1 = prevClose;
			lo = (lo > pL || c1 < pL) ? lo : pL; up = (up < pU || c1 > pU) ? up : pU; int d;
			if (i == 0 || XlU.N(stPrevAtr)) d = 1; else { if (!XlU.N(stPrevSt) && stPrevSt == pU) d = c > up ? -1 : 1; else d = c < lo ? 1 : -1; }
			stPrevLine = stLine; stLine = d == -1 ? lo : up; stPrevDir = stDir; stDir = d; stPrevLb = XlU.N(lo) ? 0 : lo; stPrevUb = XlU.N(up) ? 0 : up; stPrevSt = stLine; stPrevAtr = a2;
			bu = stDir < 0 && i > 0 && stPrevDir > 0; be = stDir > 0 && i > 0 && stPrevDir < 0; stEn = Math.Max(l, Math.Min(h, XlU.Nz(stPrevLine, c))); StLineV = stLine; StDirV = stDir;
		}
		else if (isAr)
		{
			arHb.Update(h); arLb.Update(l); arHiW.Update(h); arLoW.Update(l); arHi = arHiW.Val; arLo = arLoW.Val; double av = arVolMa.Update(v);
			double ru = XlU.N(arHb.Val) ? double.NaN : 100.0 * (p.ArLen + arHb.Off) / p.ArLen, rd = XlU.N(arLb.Val) ? double.NaN : 100.0 * (p.ArLen + arLb.Off) / p.ArLen;
			double up = Smooth(arUpS, ru), dn = Smooth(arDnS, rd); bool vok = p.Vol <= 0 || (!XlU.N(av) && v >= av * p.Vol); double S = p.ArStrongLvl;
			bool sb = !XlU.N(up) && !XlU.N(dn) && up >= S && dn <= 100 - S, se = !XlU.N(up) && !XlU.N(dn) && dn >= S && up <= 100 - S;
			if (!p.ArStrong)
			{
				bool v1 = !XlU.N(prevArUp) && !XlU.N(prevArDn) && !XlU.N(up) && !XlU.N(dn);
				bu = v1 && up > dn && prevArUp <= prevArDn && vok; be = v1 && dn > up && prevArDn <= prevArUp && vok;
			}
			else { bu = sb && !prevSB && vok; be = se && !prevSE && vok; }
			prevSB = sb; prevSE = se; prevArUp = up; prevArDn = dn;
		}
		else if (isBrk)
		{
			double av = brkVolMa.Update(v); double emaT = brkEma.Update(c); EmaTrend = emaT; bool vok = p.Vol <= 0 || (!XlU.N(av) && v >= av * p.Vol);
			double fh = prevFixHi, fl = prevFixLo; fixHi.Update(h); fixLo.Update(l); prevFixHi = fixHi.Val; prevFixLo = fixLo.Val;
			int cap = p.BaseVar ? Math.Max(p.VarMax, p.VarMin) : 0; if (LastSigBar >= 0) cap = Math.Min(cap, i - LastSigBar - 1);
			if (p.NoBase && riskOn) cap = InTrade ? 0 : (LastExitBar < 0 ? cap : Math.Min(cap, i - LastExitBar - 1));
			bool isBase;
			if (p.BaseVar)
			{
				double rangeCap = A * p.BaseMult; int lim = Math.Min(cap, i);
				if (lim >= p.VarMin && !XlU.N(rangeCap) && i >= 1)
				{
					double rHi = H1(1), rLo = L1(1);
					for (int k = 1; k <= lim; k++) { if (i - k < 0) break; rHi = Math.Max(rHi, H1(k)); rLo = Math.Min(rLo, L1(k)); if (rHi - rLo > rangeCap) break; if (k >= p.VarMin) { cHi = rHi; cLo = rLo; baseN = k; } }
				}
				isBase = baseN > 0;
			}
			else { cHi = fh; cLo = fl; baseN = p.BaseLen; isBase = (cHi - cLo) <= A * p.BaseMult; }
			buf = An ? 0.0 : p.MinBreak * A;
			bool fresh = p.BaseVar || ((LastSigBar < 0 || i - LastSigBar > p.BaseLen) && (!(p.NoBase && riskOn) || (!InTrade && (LastExitBar < 0 || i - LastExitBar > p.BaseLen))));
			bool wick = p.BrkMode == "Bar Wick (Edge)"; bool up = (wick ? h : c) > cHi + buf, dn = (wick ? l : c) < cLo - buf, bs = up && dn;
			bu = isBase && fresh && up && (!bs || c > o) && (!p.Trend || c > emaT) && vok; be = isBase && fresh && dn && (!bs || c < o) && (!p.Trend || c < emaT) && vok;
		}
		BaseHi = cHi; BaseLo = cLo; BaseN = baseN;
		double bufD = An ? 0.0 : p.SlBuf * A, minD = isMA && !An ? p.SlMin * A : 0.0;
		double mEnt = Math.Max(l, Math.Min(h, XlU.Nz(slowV, c)));
		bool edge = isBrk && p.BrkMode != "Bar Close (Entry)";
		double eB, eS;
		if (isBrk) { eB = edge ? cHi + buf : c; eS = edge ? cLo - buf : c; }
		else if (isDon) { eB = p.EntEdge ? XlU.Nz(upB, c) : c; eS = p.EntEdge ? XlU.Nz(loB, c) : c; }
		else if (isMA) { eB = eS = p.EntEdge ? mEnt : c; }
		else if (isST) { eB = eS = p.EntEdge ? stEn : c; }
		else { eB = eS = c; }
		double lvB = double.NaN, lvS = double.NaN;
		if (slWick) { lvB = l - bufD; lvS = h + bufD; } else if (slBody) { lvB = o - bufD; lvS = o + bufD; } else if (slSwing) { lvB = swLoV - bufD; lvS = swHiV + bufD; } else if (slEdge) { lvB = cLo - bufD; lvS = cHi + bufD; }
		else if (slOpp) { lvB = XlU.Nz(loB, l) - bufD; lvS = XlU.Nz(upB, h) + bufD; } else if (slMid) { lvB = XlU.Nz(midB, eB) - bufD; lvS = XlU.Nz(midB, eS) + bufD; } else if (slStl) { lvB = stLine - bufD; lvS = stLine + bufD; } else if (slAroon) { lvB = arLo - bufD; lvS = arHi + bufD; }
		double sB, sS;
		if (!hasSl) { sB = sS = double.NaN; } else if (slAtr) { sB = eB - A * p.SlMult; sS = eS + A * p.SlMult; } else if (isMA) { sB = Math.Min(lvB, eB - minD); sS = Math.Max(lvS, eS + minD); } else { sB = lvB; sS = lvS; }
		bool need = p.UseRisk && hasSl; bool okB = !isBrk || !need || (!XlU.N(sB) && sB < eB), okS = !isBrk || !need || (!XlU.N(sS) && sS > eS);
		bool stSlPre = isST && p.UseRisk && InTrade && HitSl(c, l, h); bool stTpPre = isST && p.UseRisk && InTrade && !stSlPre && HitTp(false, c, l, h);
		bool blk = p.NoOvl && riskOn && InTrade && !(stSlPre || stTpPre); bool alt = isST ? false : p.Alt;
		bool buy = bu && !(blk && (both || Dir == 1)) && (!alt || Dir != 1) && okB, sell = be && !(blk && (both || Dir == -1)) && (!alt || Dir != -1) && okS;
		if (p.UseRisk && InTrade)
		{
			bool s_ = HitSl(c, l, h), t_ = HitTp(s_, c, l, h);
			if (s_ || t_)
			{
				double ex = p.ExitClose ? c : (s_ ? SlPx : TpPx); double pn = Dir == 1 ? (ex - EntryPx) / EntryPx * 100 : (EntryPx - ex) / EntryPx * 100;
				Record(pn, s_ ? -1 : 1, ms, i); InTrade = false; LastExitBar = i; SlHit = s_; TpHit = t_; ExitPx = ex; ExitPnl = pn; LastRes = s_ ? -1 : 1;
			}
		}
		if (buy || sell)
		{
			if (LastSigMs != long.MinValue) { Gaps.Add(ms - LastSigMs); GapsM.Add((double)(i - LastSigBarIdx) * SecPerBar * 1000); while (Gaps.Count > p.Lookback) Gaps.RemoveAt(0); while (GapsM.Count > p.Lookback) GapsM.RemoveAt(0); }
			LastSigMs = ms; LastSigBarIdx = i;
			if (InTrade) { double pn = Dir == 1 ? (c - EntryPx) / EntryPx * 100 : (EntryPx - c) / EntryPx * 100; Record(pn, 0, ms, i); Rev = true; ExitPx = c; ExitPnl = pn; LastRes = 0; }
			Dir = buy ? 1 : -1; LastSigBar = i; EntryPx = Dir == 1 ? eB : eS; InTrade = true; EntryMs = ms; EntryBar = i; SlPx = !hasSl ? double.NaN : (Dir == 1 ? sB : sS);
			double rd = Math.Abs(EntryPx - SlPx); SlAtrX = hasSl && !slAtr && A > 0 ? rd / A : double.NaN;
			double td = tpAtr ? A * p.TpMult : tpBase ? (cHi - cLo) * p.TpB : rd * p.TpR; TpPx = !hasTp ? double.NaN : (Dir == 1 ? EntryPx + td : EntryPx - td); Buy = buy; Sell = sell;
		}
		prevClose = c; prevBodyHi = bodyHi; prevBodyLo = bodyLo;
	}
	bool HitSl(double c, double l, double h) { return hasSl && (P.ExitClose ? (Dir == 1 ? c <= SlPx : c >= SlPx) : (Dir == 1 ? l <= SlPx : h >= SlPx)); }
	bool HitTp(bool sl, double c, double l, double h) { return hasTp && !sl && (P.ExitClose ? (Dir == 1 ? c >= TpPx : c <= TpPx) : (Dir == 1 ? h >= TpPx : l <= TpPx)); }
	void Record(double pn, int reason, long ms, int i)
	{
		Pnls.Add(pn); Reasons.Add(reason); ClosedTrades++; Durs.Add(ms - EntryMs); DursM.Add((double)(i - EntryBar) * SecPerBar * 1000);
		while (Durs.Count > P.Lookback) Durs.RemoveAt(0); while (DursM.Count > P.Lookback) DursM.RemoveAt(0); while (Pnls.Count > P.Lookback) { Pnls.RemoveAt(0); Reasons.RemoveAt(0); }
	}
	// ---- stats over the lookback window
	public struct Stats { public int N, W, Lw, Tp, Sl, Rv; public double Net, WinRate, AvgW, AvgL, MaxDd, AvgP; }
	public Stats GetStats()
	{
		var s = new Stats(); s.N = Pnls.Count; double eq = 1, pk = 1, dd = 0, ws = 0, ls = 0, sum = 0;
		for (int k = 0; k < Pnls.Count; k++)
		{
			double q = Pnls[k]; int r = Reasons[k]; if (r == 1) s.Tp++; else if (r == -1) s.Sl++; else s.Rv++;
			if (q > 0) { s.W++; ws += q; } else if (q < 0) { s.Lw++; ls += q; } eq *= 1 + q / 100; pk = Math.Max(pk, eq); dd = Math.Min(dd, (eq / pk - 1) * 100); sum += q;
		}
		s.Net = s.N > 0 ? (eq - 1) * 100 : double.NaN; s.WinRate = s.N > 0 ? 100.0 * s.W / s.N : double.NaN; s.AvgW = s.W > 0 ? ws / s.W : double.NaN; s.AvgL = s.Lw > 0 ? ls / s.Lw : double.NaN; s.MaxDd = s.N > 0 ? dd : double.NaN; s.AvgP = s.N > 0 ? sum / s.N : double.NaN; return s;
	}
	public static double Avg(List<double> x) { if (x.Count == 0) return double.NaN; double s = 0; foreach (double v in x) s += v; return s / x.Count; }
}

	public enum XlMode { OC, Breakout, Donchian, MACross, Supertrend, Aroon }
	public enum XlExit { Wick, Close }
	public enum XlPos { TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight }
	public enum XlDetail { Micro, Low, Medium, High, Maximum }
	public enum XlSl { ATR, CandleWick, CandleBody, BaseEdge, ChannelOpposite, ChannelMiddle, Swing, SupertrendLine, AroonExtreme, None }
	public enum XlTp { ATRTarget, SLMultiple, OppositeSignal, BaseHeight }
	public enum XlBrkEntry { BarCloseEdge, BarCloseEntry, BarWickEdge }
	public enum XlMaType { SMA, EMA, WMA, HMA, RMA }
	public enum XlMaSrc { Close, Open, High, Low, HL2, HLC3, OHLC4 }
	public enum XlArSig { Crossover, StrongTrend }
	public enum XlArSm { None, SMA, EMA, RMA, WMA, HMA }

	internal class XlCell { public string T = ""; public int[] Fg, Bg; public int Span = 1; public bool Right, Center; }
	internal class XlTv { public int Id, StartBar, EndBar, Dir; public double Entry, Sl, Tp, ExitPx; public bool Open = true, HasSl, HasTp, Exited; public List<string> Tags = new List<string>(); }
	internal class XlTf { public BarsPeriodType Type; public int Value; public string Label; public double Sec; }

	public class SneakyBuySellXL : Indicator
	{
		private XlEngine main; private List<XlTf> tfs = new List<XlTf>(); private List<XlEngine> scan = new List<XlEngine>(); private List<XlEngine[]> eng = new List<XlEngine[]>();
		private static readonly string[] ModeNames = { "OC", "Breakout", "Donchian", "MA Cross", "Supertrend", "Aroon" };
		private readonly Dictionary<long, System.Windows.Media.Brush> brushes = new Dictionary<long, System.Windows.Media.Brush>();
		private XlTv cur; private readonly Queue<XlTv> closedQ = new Queue<XlTv>(); private readonly Queue<string> sigTags = new Queue<string>(); private int tradeSeq; private bool errShown;
		private List<XlCell[]> tInfo = new List<XlCell[]>(), tScan = new List<XlCell[]>(), tEng = new List<XlCell[]>(); private readonly object tLock = new object();
		private static readonly DateTime Epoch = new DateTime(2000, 1, 1);
		private static readonly int[] cWhite = { 250, 251, 255 }, cMute = { 170, 178, 195 }, cGreen = { 60, 235, 110 }, cRed = { 255, 85, 100 }, cHdr = { 44, 84, 160 }, cPanel = { 19, 23, 34 }, cRow = { 14, 18, 30 }, cRow2 = { 24, 30, 48 }, cBuy = { 76, 175, 80 }, cSell = { 242, 54, 69 }, cBlue = { 41, 98, 255 }, cOrange = { 255, 152, 0 };

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Buy/Sell Signals XL port: six signal engines (OC, Breakout, Donchian, MA Cross, Supertrend, Aroon) with SL/TP trade logic, lookback stats, multi-timeframe scanner and engine scanner.";
				Name = "SneakyBuySellXL"; Calculate = Calculate.OnBarClose; IsOverlay = true; DisplayInDataBox = false; PaintPriceMarkers = false; IsSuspendedWhileInactive = true; ArePlotsConfigurable = true; IsAutoScale = false; DrawOnPricePanel = true;
				Mode = XlMode.OC; UseRisk = true; ExitTrig = XlExit.Wick; AtrLen = 14; Lookback = 20; OcBodyLen = 50; OcBodyMult = 3.0; OcVol = 0.0; OcAlt = true; OcNoOvl = true; OcSl = XlSl.CandleWick; OcSlBuf = 0.0; OcTp = XlTp.SLMultiple; OcSlMult = 1.5; OcTpMult = 3.0; OcTpR = 1.0; BrkVar = true; BrkBaseLen = 10; BrkVarMin = 5; BrkVarMax = 30; BrkBaseMult = 2.0; BrkEntry = XlBrkEntry.BarCloseEdge; BrkMinBreak = 0.0; BrkVolX = 0.0; BrkVolLen = 50; BrkTrend = false; BrkTrendLen = 200; BrkAlt = true; BrkNoOvl = true; BrkNoBase = true; BrkShowBase = true; BrkSl = XlSl.BaseEdge; BrkSlBuf = 0.0; BrkTp = XlTp.BaseHeight; BrkSlMult = 1.5; BrkTpMult = 3.0; BrkTpR = 2.0; BrkTpB = 1.0; BrkShowEma = false; DonLen = 20; DonEdge = true; DonVol = 0.0; DonAlt = true; DonNoOvl = true; DonShow = true; DonShowMid = true; DonSl = XlSl.ChannelOpposite; DonSlBuf = 0.0; DonTp = XlTp.SLMultiple; DonSlMult = 2.0; DonTpMult = 3.0; DonTpR = 0.5; MaFast = 9; MaSlow = 21; MaKind = XlMaType.EMA; MaSrcSel = XlMaSrc.Close; MaMinSep = 0.0; MaTrend = false; MaTrendLen = 200; MaEdge = true; MaVol = 0.0; MaAlt = false; MaNoOvl = true; MaShow = true; MaSl = XlSl.ATR; MaSwingLen = 5; MaSlBuf = 0.0; MaSlMin = 1.0; MaTp = XlTp.SLMultiple; MaSlMult = 2.0; MaTpMult = 3.0; MaTpR = 1.0; StLen = 10; StMult = 3.0; StEdge = false; StNoOvl = true; StShow = true; StSl = XlSl.SupertrendLine; StSlBuf = 0.0; StTp = XlTp.SLMultiple; StSlMult = 1.5; StTpMult = 3.0; StTpR = 0.5; ArLen = 14; ArSig = XlArSig.Crossover; ArStrongLvl = 70.0; ArSm = XlArSm.None; ArSmLen = 3; ArVol = 0.0; ArVolLen = 50; ArAlt = true; ArNoOvl = true; ArSl = XlSl.ATR; ArSlBuf = 0.0; ArTp = XlTp.SLMultiple; ArSlMult = 1.5; ArTpMult = 3.0; ArTpR = 1.0; ShowLabels = true; LabelFont = 10; ShowEntryDot = true; ShowTradeLines = true; ShowTradeLbl = true; ShowX = true; ShowZone = true; ShowClosed = 0; DrawBarsBack = 3000; LineW = 1; LineExt = 25; ShowTable = true; TablePos = XlPos.TopRight; TableFont = 13; ShowScan = true; TfList = "5, 15, 30, 60, 4h, D"; ScanMax = 8; ScanPos = XlPos.TopCenter; ScanDetail = XlDetail.High; ShowEng = true; EngOnlyActive = false; EngOC = true; EngBrk = true; EngDon = true; EngMA = true; EngST = true; EngAr = true; EngPos = XlPos.BottomCenter; EngDetail = XlDetail.Low; AlertsOn = true;
				string[] pn = { "Signal", "Entry", "StopLoss", "TakeProfit", "TradeDir", "ExitCode", "DonchianUpper", "DonchianLower", "DonchianMiddle", "FastMA", "SlowMA", "TrendMA", "SupertrendUp", "SupertrendDown", "BreakoutTrendEMA" };
				for (int p = 0; p < pn.Length; p++) AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, p >= 6 ? 2 : 1), PlotStyle.Line, pn[p]);
			}
			else if (State == State.Configure)
			{
				tfs = ParseTfs(TfList, ScanMax); foreach (var t in tfs) AddDataSeries(t.Type, t.Value);
				for (int p = 0; p < 6; p++) Plots[p].Brush = System.Windows.Media.Brushes.Transparent;
				Plots[6].Brush = B(120, 123, 134); Plots[7].Brush = B(120, 123, 134); Plots[8].Brush = B(120, 123, 134, 150); Plots[9].Brush = B(255, 152, 0); Plots[10].Brush = B(156, 39, 176); Plots[11].Brush = B(120, 123, 134);
				Plots[12].Brush = B(76, 175, 80); Plots[13].Brush = B(242, 54, 69); Plots[14].Brush = B(255, 152, 0); Plots[12].Width = 2; Plots[13].Width = 2; Plots[9].Width = 2; Plots[10].Width = 2;
			}
			else if (State == State.DataLoaded)
			{
				double sec = SecOf(BarsPeriod.BarsPeriodType, BarsPeriod.Value); main = new XlEngine(ModeNames[(int)Mode], BuildP(Mode), sec);
				scan.Clear(); eng.Clear();
				foreach (var t in tfs)
				{
					scan.Add(new XlEngine(ModeNames[(int)Mode], BuildP(Mode), t.Sec)); var arr = new XlEngine[6];
					for (int m = 0; m < 6; m++) arr[m] = new XlEngine(ModeNames[m], BuildP((XlMode)m), t.Sec); eng.Add(arr);
				}
			}
			else if (State == State.Terminated) brushes.Clear();
		}

		#region Setup helpers
		private static double SecOf(BarsPeriodType t, int v) { switch (t) { case BarsPeriodType.Day: return 86400.0 * v; case BarsPeriodType.Week: return 604800.0 * v; case BarsPeriodType.Month: return 2629800.0 * v; case BarsPeriodType.Second: return v; default: return 60.0 * v; } }
		private static List<XlTf> ParseTfs(string s, int max)
		{
			var r = new List<XlTf>(); if (string.IsNullOrEmpty(s)) return r;
			foreach (string raw in s.Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
			{
				string t = raw.Trim(); if (t.Length == 0) continue; var f = new XlTf(); int k = 0; while (k < t.Length && char.IsDigit(t[k])) k++; int num = k > 0 ? int.Parse(t.Substring(0, k)) : 1; string suf = t.Substring(k);
				if (suf == "" || suf == "m") { f.Type = BarsPeriodType.Minute; f.Value = num; f.Label = num % 60 == 0 && num >= 60 ? (num / 60) + "h" : num + "m"; }
				else if (suf == "h" || suf == "H") { f.Type = BarsPeriodType.Minute; f.Value = num * 60; f.Label = num + "h"; }
				else if (suf == "d" || suf == "D") { f.Type = BarsPeriodType.Day; f.Value = num; f.Label = num == 1 ? "D" : num + "D"; }
				else if (suf == "w" || suf == "W") { f.Type = BarsPeriodType.Week; f.Value = num; f.Label = num == 1 ? "W" : num + "W"; }
				else if (suf == "M") { f.Type = BarsPeriodType.Month; f.Value = num; f.Label = num == 1 ? "M" : num + "M"; }
				else continue;
				f.Sec = SecOf(f.Type, f.Value); r.Add(f); if (r.Count >= max) break;
			}
			return r;
		}
		private static string SlStr(XlSl s) { switch (s) { case XlSl.ATR: return "ATR"; case XlSl.CandleWick: return "Candle wick"; case XlSl.CandleBody: return "Candle body"; case XlSl.BaseEdge: return "Base edge"; case XlSl.ChannelOpposite: return "Channel opposite"; case XlSl.ChannelMiddle: return "Channel middle"; case XlSl.Swing: return "Swing"; case XlSl.SupertrendLine: return "Supertrend line"; case XlSl.AroonExtreme: return "Aroon extreme"; default: return "None"; } }
		private static string TpStr(XlTp s) { switch (s) { case XlTp.ATRTarget: return "ATR target"; case XlTp.SLMultiple: return "SL multiple"; case XlTp.OppositeSignal: return "Opposite signal"; default: return "Base height"; } }
		private static bool SlOk(XlMode m, XlSl s)
		{
			switch (m) { case XlMode.OC: return s == XlSl.ATR || s == XlSl.CandleWick || s == XlSl.CandleBody || s == XlSl.None; case XlMode.Breakout: return s == XlSl.ATR || s == XlSl.BaseEdge;
				case XlMode.Donchian: return s == XlSl.ATR || s == XlSl.ChannelOpposite || s == XlSl.ChannelMiddle || s == XlSl.CandleWick || s == XlSl.CandleBody || s == XlSl.None;
				case XlMode.MACross: return s == XlSl.ATR || s == XlSl.Swing || s == XlSl.CandleWick || s == XlSl.CandleBody || s == XlSl.None;
				case XlMode.Supertrend: return s == XlSl.SupertrendLine || s == XlSl.ATR || s == XlSl.CandleWick || s == XlSl.CandleBody || s == XlSl.None;
				default: return s == XlSl.ATR || s == XlSl.AroonExtreme || s == XlSl.CandleWick || s == XlSl.CandleBody || s == XlSl.None; }
		}
		private static bool TpOk(XlMode m, XlTp t) { return t == XlTp.BaseHeight ? m == XlMode.Breakout : (m == XlMode.Breakout ? t != XlTp.OppositeSignal : true); }
		private XlP BuildP(XlMode m)
		{
			var p = XlP.For(ModeNames[(int)m]); p.UseRisk = UseRisk; p.ExitClose = ExitTrig == XlExit.Close; p.AtrLen = AtrLen; p.Lookback = Lookback;
			XlSl sl; XlTp tp;
			switch (m)
			{
				case XlMode.OC: p.BodyLen = OcBodyLen; p.BodyMult = OcBodyMult; p.Vol = OcVol; p.VolLen = OcBodyLen; p.Alt = OcAlt; p.NoOvl = OcNoOvl; sl = OcSl; tp = OcTp; p.SlBuf = OcSlBuf; p.SlMult = OcSlMult; p.TpMult = OcTpMult; p.TpR = OcTpR; break;
				case XlMode.Breakout:
					p.BaseVar = BrkVar; p.BaseLen = BrkBaseLen; p.VarMin = BrkVarMin; p.VarMax = BrkVarMax; p.BaseMult = BrkBaseMult; p.BrkMode = BrkEntry == XlBrkEntry.BarCloseEdge ? "Bar Close (Edge)" : BrkEntry == XlBrkEntry.BarCloseEntry ? "Bar Close (Entry)" : "Bar Wick (Edge)";
					p.MinBreak = BrkMinBreak; p.Vol = BrkVolX; p.VolLen = BrkVolLen; p.Trend = BrkTrend; p.TrendLen = BrkTrendLen; p.Alt = BrkAlt; p.NoOvl = BrkNoOvl; p.NoBase = BrkNoBase; sl = BrkSl; tp = BrkTp; p.SlBuf = BrkSlBuf; p.SlMult = BrkSlMult; p.TpMult = BrkTpMult; p.TpR = BrkTpR; p.TpB = BrkTpB; break;
				case XlMode.Donchian: p.DonLen = DonLen; p.EntEdge = DonEdge; p.Vol = DonVol; p.Alt = DonAlt; p.NoOvl = DonNoOvl; sl = DonSl; tp = DonTp; p.SlBuf = DonSlBuf; p.SlMult = DonSlMult; p.TpMult = DonTpMult; p.TpR = DonTpR; break;
				case XlMode.MACross: p.Fast = MaFast; p.Slow = MaSlow; p.MaType = MaKind.ToString(); p.Src = MaSrcSel.ToString(); p.MinSep = MaMinSep; p.Trend = MaTrend; p.TrendLen = MaTrendLen; p.EntEdge = MaEdge; p.Vol = MaVol; p.Alt = MaAlt; p.NoOvl = MaNoOvl; p.SwingLen = MaSwingLen; sl = MaSl; tp = MaTp; p.SlBuf = MaSlBuf; p.SlMin = MaSlMin; p.SlMult = MaSlMult; p.TpMult = MaTpMult; p.TpR = MaTpR; break;
				case XlMode.Supertrend: p.StLen = StLen; p.StMult = StMult; p.EntEdge = StEdge; p.NoOvl = StNoOvl; sl = StSl; tp = StTp; p.SlBuf = StSlBuf; p.SlMult = StSlMult; p.TpMult = StTpMult; p.TpR = StTpR; break;
				default: p.ArLen = ArLen; p.ArStrong = ArSig == XlArSig.StrongTrend; p.ArStrongLvl = ArStrongLvl; p.ArSmooth = ArSm.ToString(); p.ArSmLen = ArSmLen; p.Vol = ArVol; p.VolLen = ArVolLen; p.Alt = ArAlt; p.NoOvl = ArNoOvl; sl = ArSl; tp = ArTp; p.SlBuf = ArSlBuf; p.SlMult = ArSlMult; p.TpMult = ArTpMult; p.TpR = ArTpR; break;
			}
			if (SlOk(m, sl)) p.SlMode = SlStr(sl); if (TpOk(m, tp)) p.TpMode = TpStr(tp);
			return p;
		}
		private System.Windows.Media.Brush B(int r, int g, int b, int a = 255)
		{
			long key = ((long)r << 24) | ((long)g << 16) | ((long)b << 8) | (long)a; System.Windows.Media.Brush br;
			if (!brushes.TryGetValue(key, out br)) { br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b)); br.Freeze(); brushes[key] = br; }
			return br;
		}
		private System.Windows.Media.Brush Bc(int[] c, int a = 255) { return B(c[0], c[1], c[2], a); }
		private static long Ms(DateTime t) { return (long)(t - Epoch).TotalMilliseconds; }
		#endregion

		protected override void OnBarUpdate()
		{
			try { if (BarsInProgress == 0) Primary(); else Secondary(BarsInProgress - 1); }
			catch (Exception ex) { if (!errShown) { errShown = true; Print("SneakyBuySellXL error: " + ex); } }
		}

		private void Secondary(int k)
		{
			if (k >= scan.Count || main == null) return; long ms = Ms(Times[BarsInProgress][0]); double o = Opens[BarsInProgress][0], h = Highs[BarsInProgress][0], l = Lows[BarsInProgress][0], c = Closes[BarsInProgress][0], v = Volumes[BarsInProgress][0];
			scan[k].Step(ms, o, h, l, c, v); if (ShowEng) for (int m = 0; m < 6; m++) eng[k][m].Step(ms, o, h, l, c, v);
		}

		private void Primary()
		{
			if (main == null) return; long ms = Ms(Time[0]); main.Step(ms, Open[0], High[0], Low[0], Close[0], Volume[0]);
			bool recent = CurrentBar >= Count - 1 - DrawBarsBack; bool lastBar = CurrentBar >= Count - 2;
			for (int p = 0; p < 6; p++) Values[p][0] = 0;
			if (main.Buy || main.Sell)
			{
				Values[0][0] = main.Buy ? 1 : -1; Values[1][0] = main.EntryPx; Values[2][0] = double.IsNaN(main.SlPx) ? 0 : main.SlPx; Values[3][0] = double.IsNaN(main.TpPx) ? 0 : main.TpPx;
			}
			Values[4][0] = main.InTrade ? main.Dir : 0; Values[5][0] = main.TpHit ? 1 : main.SlHit ? -1 : main.Rev ? 0.5 : 0;
			VisualPlots();
			if (recent) Visuals(lastBar);
			if (AlertsOn && State == State.Realtime)
			{
				if (main.Buy || main.Sell) Alert("XlSig" + CurrentBar, Priority.Medium, "XL " + ModeNames[(int)Mode] + (main.Buy ? " BUY " : " SELL ") + Instrument.MasterInstrument.Name + " entry " + main.EntryPx.ToString("0.00", CultureInfo.InvariantCulture), NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, Brushes.Black, Brushes.Cyan);
				if (main.TpHit || main.SlHit) Alert("XlExit" + CurrentBar, Priority.Medium, "XL " + ModeNames[(int)Mode] + (main.TpHit ? " TAKE PROFIT" : " STOP LOSS"), NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, Brushes.Black, Brushes.Cyan);
			}
			if (lastBar || State == State.Realtime) BuildTables();
		}

		private void VisualPlots()
		{
			for (int q = 6; q <= 14; q++) Values[q].Reset();
			Action<int, double> set = (i, x) => { if (!double.IsNaN(x)) Values[i][0] = x; };
			if (Mode == XlMode.Donchian && DonShow) { set(6, main.DonUp); set(7, main.DonLoV); if (DonShowMid) set(8, main.DonMidV); }
			else if (Mode == XlMode.MACross && MaShow) { set(9, main.MaFast); set(10, main.MaSlow); if (MaTrend) set(11, main.MaTrend); }
			else if (Mode == XlMode.Supertrend && StShow && !double.IsNaN(main.StLineV)) { if (main.StDirV < 0) set(12, main.StLineV); else set(13, main.StLineV); }
			else if (Mode == XlMode.Breakout && BrkShowEma) set(14, main.EmaTrend);
		}

		#region Chart drawing
		private void Visuals(bool lastBar)
		{
			if (main.SlHit || main.TpHit)
			{
				if (cur != null && cur.Open)
				{
					cur.Open = false; cur.Exited = true; cur.EndBar = CurrentBar; cur.ExitPx = main.ExitPx; DrawTrade(cur);
					if (ShowX) { string tg = "XlX" + cur.Id; Draw.Text(this, tg, false, "X", 0, main.ExitPx, 0, Bc(cBlue), new SimpleFont("Arial", 16) { Bold = true }, System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0); cur.Tags.Add(tg); }
					if (ShowClosed > 0) { closedQ.Enqueue(cur); cur = null; while (closedQ.Count > ShowClosed) RemoveViz(closedQ.Dequeue()); }
					else if (!ShowX) { RemoveViz(cur); cur = null; }
				}
			}
			if (main.Buy || main.Sell)
			{
				if (cur != null)
				{
					if (ShowClosed > 0 && main.Rev && cur.Open) { cur.Open = false; cur.EndBar = CurrentBar; DrawTrade(cur); closedQ.Enqueue(cur); while (closedQ.Count > ShowClosed) RemoveViz(closedQ.Dequeue()); }
					else RemoveViz(cur);
					cur = null;
				}
				bool buy = main.Buy; var col = buy ? cBuy : cSell;
				if (Mode == XlMode.Breakout && BrkShowBase && main.BaseN > 0 && !double.IsNaN(main.BaseHi)) { string tg = "XlBase" + CurrentBar; Draw.Rectangle(this, tg, false, main.BaseN, main.BaseHi, 0, main.BaseLo, Brushes.Transparent, Bc(col), 20); sigTags.Enqueue(tg); }
				if (ShowLabels)
				{
					string tg = "XlSig" + CurrentBar; Draw.Text(this, tg, false, buy ? " BUY " : " SELL ", 0, buy ? Low[0] : High[0], buy ? 24 + LabelFont : -(24 + LabelFont), Bc(cWhite), new SimpleFont("Arial", LabelFont) { Bold = true }, System.Windows.TextAlignment.Center, Bc(col), Bc(col), 100); sigTags.Enqueue(tg);
				}
				if (ShowEntryDot) { string tg = "XlDot" + CurrentBar; Draw.Dot(this, tg, false, 0, main.EntryPx, Bc(cBlue)); sigTags.Enqueue(tg); }
				while (sigTags.Count > 600) RemoveDrawObject(sigTags.Dequeue());
				if (UseRisk) { cur = new XlTv { Id = ++tradeSeq, StartBar = CurrentBar, Dir = main.Dir, Entry = main.EntryPx, Sl = main.SlPx, Tp = main.TpPx, HasSl = !double.IsNaN(main.SlPx), HasTp = !double.IsNaN(main.TpPx) }; }
			}
			if (cur != null && cur.Open) DrawTrade(cur);
		}

		private void RemoveViz(XlTv v) { foreach (string t in v.Tags) RemoveDrawObject(t); v.Tags.Clear(); }

		private void DrawTrade(XlTv v)
		{
			if (!ShowTradeLines && !ShowTradeLbl && !ShowZone) return; int endAbs = v.Open ? CurrentBar + LineExt : v.EndBar; int s = CurrentBar - v.StartBar, e = CurrentBar - endAbs; string id = v.Id.ToString();
			Action<string, double, int[], DashStyleHelper, string> line = (tg, px, col, ds, txt) =>
			{
				if (ShowTradeLines) { Draw.Line(this, "XlL" + tg + id, false, s, px, e, px, Bc(col), ds, LineW); if (!v.Tags.Contains("XlL" + tg + id)) v.Tags.Add("XlL" + tg + id); }
				if (ShowTradeLbl) { Draw.Text(this, "XlT" + tg + id, false, " " + txt + " " + px.ToString("0.00", CultureInfo.InvariantCulture), e, px, 0, Bc(col), new SimpleFont("Arial", 10) { Bold = true }, System.Windows.TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0); if (!v.Tags.Contains("XlT" + tg + id)) v.Tags.Add("XlT" + tg + id); }
			};
			line("E", v.Entry, cBlue, DashStyleHelper.Solid, "Entry");
			if (v.HasSl) line("S", v.Sl, cSell, DashStyleHelper.Dash, "SL"); if (v.HasTp) line("P", v.Tp, cBuy, DashStyleHelper.Dash, "TP");
			if (ShowZone)
			{
				if (v.HasSl) { Draw.Rectangle(this, "XlZS" + id, false, s, Math.Max(v.Entry, v.Sl), e, Math.Min(v.Entry, v.Sl), Brushes.Transparent, Bc(cSell), 14); if (!v.Tags.Contains("XlZS" + id)) v.Tags.Add("XlZS" + id); }
				if (v.HasTp) { Draw.Rectangle(this, "XlZP" + id, false, s, Math.Max(v.Entry, v.Tp), e, Math.Min(v.Entry, v.Tp), Brushes.Transparent, Bc(cBuy), 14); if (!v.Tags.Contains("XlZP" + id)) v.Tags.Add("XlZP" + id); }
			}
		}
		#endregion

		#region Tables
		private static string num(double x, string f) { return x.ToString(f, CultureInfo.InvariantCulture); }
		private static string Fp(double x) { return double.IsNaN(x) ? "n/a" : (x >= 0 ? "+" : "") + x.ToString("0.00", CultureInfo.InvariantCulture) + "%"; }
		private static int[] PosCol(double x) { return double.IsNaN(x) ? cWhite : x >= 0 ? cGreen : cRed; }
		private static string Dur(double ms)
		{
			if (double.IsNaN(ms)) return "n/a"; double u = ms < 60000 ? 1000 : ms < 3600000 ? 60000 : ms < 86400000 ? 3600000 : ms < 604800000 ? 86400000 : ms < 2629800000.0 ? 604800000 : ms < 31557600000.0 ? 2629800000.0 : 31557600000.0;
			string t = ms < 60000 ? "sec" : ms < 3600000 ? "min" : ms < 86400000 ? "hr" : ms < 604800000 ? "days" : ms < 2629800000.0 ? "wks" : ms < 31557600000.0 ? "mo" : "yrs"; return (ms / u).ToString("0.0", CultureInfo.InvariantCulture) + " " + t;
		}
		private static string DurC(double ms)
		{
			double u = ms < 60000 ? 1000 : ms < 3600000 ? 60000 : ms < 86400000 ? 3600000 : ms < 604800000 ? 86400000 : ms < 2629800000.0 ? 604800000 : ms < 31557600000.0 ? 2629800000.0 : 31557600000.0;
			string t = ms < 60000 ? "s" : ms < 3600000 ? "m" : ms < 86400000 ? "h" : ms < 604800000 ? "d" : ms < 2629800000.0 ? "w" : ms < 31557600000.0 ? "mo" : "y"; double x = ms / u; return x.ToString(x >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture) + t;
		}
		private static string DurDual(double cal, double mkt) { return double.IsNaN(cal) ? "n/a" : DurC(cal) + " / " + (double.IsNaN(mkt) ? "n/a" : DurC(mkt)); }
		private static XlCell C(string t, int[] fg = null, int[] bg = null, bool right = false, bool center = false, int span = 1) { return new XlCell { T = t, Fg = fg ?? cWhite, Bg = bg ?? cRow, Right = right, Center = center, Span = span }; }
		private static XlCell[] Row2(string l, string v, int[] vc = null) { return new[] { C(l, cWhite, cRow), C(v, vc ?? cWhite, cRow, true) }; }
		private static string TfName(BarsPeriod bp) { return bp.BarsPeriodType == BarsPeriodType.Minute ? (bp.Value % 60 == 0 ? (bp.Value / 60) + "h" : bp.Value + "m") : bp.BarsPeriodType == BarsPeriodType.Day ? (bp.Value == 1 ? "D" : bp.Value + "D") : bp.BarsPeriodType == BarsPeriodType.Week ? "W" : bp.Value + " " + bp.BarsPeriodType; }

		private void BuildTables()
		{
			var info = new List<XlCell[]>(); var sc = new List<XlCell[]>(); var en = new List<XlCell[]>(); string mn = ModeNames[(int)Mode].ToUpperInvariant(); var P = main.P; var S = main.GetStats();
			if (ShowTable)
			{
				info.Add(new[] { C("XL - " + mn + " BUY/SELL SIGNALS", cWhite, cHdr, false, true, 2) });
				info.Add(Row2("Chart Type", "Standard")); info.Add(Row2("Timeframe / Session", TfName(BarsPeriod) + " / Regular")); info.Add(Row2("Non-Repaint", "Closed bars", cGreen));
				bool vol = P.Vol > 0;
				switch (Mode)
				{
					case XlMode.OC: info.Add(Row2("Avg Body Length", P.BodyLen.ToString())); info.Add(Row2("Min Body", num(P.BodyMult, "0.0") + "x")); break;
					case XlMode.Breakout: info.Add(Row2("Base Length", P.BaseVar ? P.VarMin + "-" + Math.Max(P.VarMax, P.VarMin) + " bars" : P.BaseLen + " bars")); info.Add(Row2("Breakout Entry Mode", P.BrkMode)); info.Add(Row2("Max Base Height", num(P.BaseMult, "0.0") + "x ATR")); if (P.MinBreak > 0) info.Add(Row2("Min Breakout", num(P.MinBreak, "0.0") + "x ATR")); break;
					case XlMode.Donchian: info.Add(Row2("Channel Length", P.DonLen.ToString())); info.Add(Row2("Entry Mode", P.EntEdge ? "Bar Close (Edge)" : "Bar Close (Entry)")); break;
					case XlMode.MACross: info.Add(Row2("Fast / Slow MA", P.MaType + " " + P.Fast + " / " + P.Slow)); info.Add(Row2("Entry Mode", P.EntEdge ? "Bar Close (Edge)" : "Bar Close (Entry)")); if (P.MinSep > 0) info.Add(Row2("Min Cross Strength", num(P.MinSep, "0.00") + "x ATR")); break;
					case XlMode.Aroon: info.Add(Row2("Aroon Len / Smoothing", P.ArLen + (P.ArSmooth != "None" ? " / " + P.ArSmooth + " " + P.ArSmLen : ""))); info.Add(Row2("Signal Mode", P.ArStrong ? "Strong " + num(P.ArStrongLvl, "0") : "Crossover")); break;
					default: info.Add(Row2("Supertrend Len / Factor", P.StLen + " / " + num(P.StMult, "0.0"))); info.Add(Row2("Entry Mode", P.EntEdge ? "Bar Close (Edge)" : "Bar Close (Entry)")); break;
				}
				if (vol) info.Add(Row2("Min Volume", num(P.Vol, "0.0") + "x")); if (P.Trend && (Mode == XlMode.Breakout || Mode == XlMode.MACross)) info.Add(Row2("Trend Filter", (Mode == XlMode.Breakout ? "EMA " + P.TrendLen : P.MaType + " " + P.TrendLen)));
				if (Mode != XlMode.Supertrend) info.Add(Row2("Alternate Signals", P.Alt ? "ON" : "OFF", P.Alt ? cGreen : cRed)); info.Add(Row2("Ignore Signals in Trade", P.NoOvl ? "ON" : "OFF", P.NoOvl ? cGreen : cRed)); if (Mode == XlMode.Breakout) info.Add(Row2("Ignore Base in Trade", P.NoBase ? "ON" : "OFF", P.NoBase ? cGreen : cRed));
				info.Add(Row2("Exit Trigger", P.UseRisk ? (P.ExitClose ? "Close" : "Wick") : "-"));
				info.Add(Row2("Stop Loss", P.UseRisk ? P.SlMode + (P.SlMode == "ATR" ? " " + num(P.SlMult, "0.0") + "x" : (double.IsNaN(main.SlAtrX) ? "" : " " + num(main.SlAtrX, "0.0") + "x ATR")) : "OFF", P.UseRisk ? cWhite : cRed));
				info.Add(Row2("Take Profit", P.UseRisk ? P.TpMode + (P.TpMode == "ATR target" ? " " + num(P.TpMult, "0.0") + "x" : P.TpMode == "SL multiple" ? " " + num(P.TpR, "0.0") + "x" : P.TpMode == "Base height" ? " " + num(P.TpB, "0.0") + "x" : "") : "OFF", P.UseRisk ? cWhite : cRed));
				info.Add(new[] { C("LOOKBACK P/L", cWhite, cHdr, false, true, 2) });
				info.Add(Row2("Trades Counted", S.N + " / " + P.Lookback)); info.Add(Row2("Exits TP / SL / Signal", S.Tp + " / " + S.Sl + " / " + S.Rv)); info.Add(Row2("Net P/L", Fp(S.Net), PosCol(S.Net)));
				info.Add(Row2("Win Rate", double.IsNaN(S.WinRate) ? "n/a" : S.WinRate.ToString("0", CultureInfo.InvariantCulture) + "% (" + S.W + "W/" + S.Lw + "L)")); info.Add(Row2("Avg Win / Loss", Fp(S.AvgW) + " / " + Fp(S.AvgL))); info.Add(Row2("Max Drawdown", S.N > 0 ? Fp(S.MaxDd) : "n/a", S.MaxDd < 0 ? cRed : cWhite));
				double open = main.InTrade ? (main.Dir == 1 ? (main.LastClose - main.EntryPx) / main.EntryPx * 100 : (main.EntryPx - main.LastClose) / main.EntryPx * 100) : double.NaN; info.Add(Row2("Open Trade P/L", Fp(open), PosCol(open)));
				double secMs = main.SecPerBar * 1000; double inCal = main.InTrade ? main.LastBarTimeMs - main.EntryMs : double.NaN, inMkt = main.InTrade ? (main.Bar - main.EntryBar) * secMs : double.NaN, sinceCal = main.LastSigMs == long.MinValue ? double.NaN : main.LastBarTimeMs - main.LastSigMs, sinceMkt = main.LastSigMs == long.MinValue ? double.NaN : (main.Bar - main.LastSigBarIdx) * secMs;
				info.Add(Row2("Avg Time/Sig (cal/mkt)", DurDual(XlEngine.Avg(main.Durs), XlEngine.Avg(main.DursM)))); info.Add(Row2("Time in Sig (cal/mkt)", DurDual(inCal, inMkt))); info.Add(Row2("Time btw Sig (cal/mkt)", DurDual(XlEngine.Avg(main.Gaps), XlEngine.Avg(main.GapsM)))); info.Add(Row2("Time from Sig (cal/mkt)", DurDual(sinceCal, sinceMkt)));
			}
			if (ShowScan && scan.Count > 0) BuildScan(sc, mn);
			if (ShowEng && eng.Count > 0) BuildEng(en);
			lock (tLock) { tInfo = info; tScan = sc; tEng = en; } if (ChartControl != null) ChartControl.InvalidateVisual();
		}

		private static string StatusOf(XlEngine e) { return e.InTrade ? "OPEN" : e.LastRes == 1 && e.ClosedTrades > 0 ? "TP" : e.LastRes == -1 && e.ClosedTrades > 0 ? "SL" : "-"; }
		private static XlCell StatusCell(XlEngine e) { string s = StatusOf(e); return C(s, s == "TP" ? cGreen : s == "SL" ? cRed : cWhite, null, false, true); }
		private static XlCell SigCell(XlEngine e) { return e.LastSigBarIdx < 0 ? C("-", cMute, null, false, true) : C(e.Dir == 1 ? "BUY" : "SELL", e.Dir == 1 ? cGreen : cRed, null, false, true); }
		private static string WinTxt(XlEngine.Stats s) { return double.IsNaN(s.WinRate) ? "n/a" : s.WinRate.ToString("0", CultureInfo.InvariantCulture) + "% (" + s.W + "W/" + s.Lw + "L)"; }
		private static string Px(double x) { return double.IsNaN(x) ? "-" : x.ToString("0.00", CultureInfo.InvariantCulture); }

		private void BuildScan(List<XlCell[]> sc, string mn)
		{
			var d = ScanDetail; bool hi = d == XlDetail.High || d == XlDetail.Maximum, med = hi || d == XlDetail.Medium, low = med || d == XlDetail.Low;
			var hdr = new List<XlCell> { C("Timeframe", cWhite, cHdr, false, true), C("Signal", cWhite, cHdr, false, true), C("Status", cWhite, cHdr, false, true) };
			if (low) hdr.Add(C("Avg Between", cWhite, cHdr, false, true)); if (d == XlDetail.Maximum) hdr.Add(C("Last Signal", cWhite, cHdr, false, true));
			if (hi) { hdr.Add(C("Entry", cWhite, cHdr, false, true)); hdr.Add(C("SL", cWhite, cHdr, false, true)); hdr.Add(C("TP", cWhite, cHdr, false, true)); }
			if (med && d != XlDetail.Low) { hdr.Add(C("Open P/L", cWhite, cHdr, false, true)); hdr.Add(C("Avg P/L", cWhite, cHdr, false, true)); }
			if (low) hdr.Add(C("Net P/L", cWhite, cHdr, false, true)); if (d == XlDetail.Maximum) hdr.Add(C("Max DD", cWhite, cHdr, false, true)); hdr.Add(C("Win Rate / " + main.P.Lookback, cWhite, cHdr, false, true));
			if (!(hi)) { /* Medium: High without Entry/SL/TP */ }
			sc.Add(new[] { C("XL " + mn + " BUY/SELL SCANNER (" + Instrument.MasterInstrument.Name + " / Standard)", cWhite, cHdr, false, true, hdr.Count) });
			// medium = high without entry/sl/tp; low = medium without open/avg
			var h2 = new List<XlCell>(); foreach (var h in hdr) { if (!hi && d != XlDetail.Maximum && (h.T == "Entry" || h.T == "SL" || h.T == "TP")) continue; h2.Add(h); }
			sc[sc.Count - 1][0].Span = h2.Count; sc.Add(h2.ToArray());
			for (int k = 0; k < scan.Count; k++)
			{
				var e = scan[k]; var s = e.GetStats(); var r = new List<XlCell> { C(tfs[k].Label, cWhite, null, false, true), SigCell(e), StatusCell(e) };
				if (low) r.Add(C(Dur(XlEngine.Avg(e.Gaps)), cWhite, null, true)); if (d == XlDetail.Maximum) r.Add(C(e.LastSigMs == long.MinValue ? "n/a" : Dur(e.LastBarTimeMs - e.LastSigMs), cWhite, null, true));
				if (hi) { r.Add(C(e.InTrade || e.ClosedTrades > 0 ? Px(e.EntryPx) : "-", cWhite, null, true)); r.Add(C(Px(e.SlPx), cRed, null, true)); r.Add(C(Px(e.TpPx), cGreen, null, true)); }
				if (med && d != XlDetail.Low) { double op = e.InTrade ? (e.Dir == 1 ? (e.LastClose - e.EntryPx) / e.EntryPx * 100 : (e.EntryPx - e.LastClose) / e.EntryPx * 100) : double.NaN; r.Add(C(double.IsNaN(op) ? "-" : Fp(op), PosCol(op), null, true)); r.Add(C(Fp(s.AvgP), PosCol(s.AvgP), null, true)); }
				if (low) r.Add(C(Fp(s.Net), PosCol(s.Net), null, true)); if (d == XlDetail.Maximum) r.Add(C(Fp(s.MaxDd), s.MaxDd < 0 ? cRed : cWhite, null, true)); r.Add(C(WinTxt(s), cWhite, null, true));
				sc.Add(r.ToArray());
			}
		}

		// Which of the six modes appear as columns in the engine scanner (indexes into ModeNames order).
		private List<int> EngModes()
		{
			var v = new List<int>();
			if (EngOnlyActive) { v.Add((int)Mode); return v; }
			if (EngOC) v.Add(0); if (EngBrk) v.Add(1); if (EngDon) v.Add(2); if (EngMA) v.Add(3); if (EngST) v.Add(4); if (EngAr) v.Add(5);
			if (v.Count == 0) v.Add((int)Mode); // nothing ticked: fall back to the active mode instead of an empty table
			return v;
		}

		private void BuildEng(List<XlCell[]> en)
		{
			var d = EngDetail; var names = new[] { "OC", "Breakout", "Donchian", "MA Cross", "Supertrend", "Aroon" };
			List<string> cols = d == XlDetail.Micro ? new List<string> { "Win Rate / " + Lookback } : d == XlDetail.Low ? new List<string> { "Status", "Win Rate / " + Lookback } : d == XlDetail.Medium ? new List<string> { "Signal", "Status", "Win Rate / " + Lookback } : d == XlDetail.High ? new List<string> { "Signal", "Status", "Net P/L", "Win Rate / " + Lookback } : new List<string> { "Signal", "Status", "Open P/L", "Net P/L", "Max DD", "Win Rate / " + Lookback };
			var vis = EngModes(); int total = 1 + vis.Count * cols.Count; en.Add(new[] { C("XL - Engine Scanner (" + Instrument.MasterInstrument.Name + " / Standard)", cWhite, cHdr, false, true, total) });
			var h1 = new List<XlCell> { C("", cWhite, cHdr) }; foreach (int m in vis) h1.Add(C(names[m], cWhite, cHdr, false, true, cols.Count)); en.Add(h1.ToArray());
			var h2 = new List<XlCell> { C("Timeframe", cWhite, cHdr) }; foreach (int m in vis) foreach (string c in cols) h2.Add(C(c, cWhite, cHdr, false, true)); en.Add(h2.ToArray());
			for (int k = 0; k < eng.Count; k++)
			{
				var r = new List<XlCell> { C(tfs[k].Label, cWhite, null, false, true) };
				foreach (int m in vis)
				{
					var e = eng[k][m]; var s = e.GetStats();
					foreach (string c in cols)
					{
						if (c == "Signal") r.Add(SigCell(e)); else if (c == "Status") r.Add(StatusCell(e)); else if (c == "Net P/L") r.Add(C(Fp(s.Net), PosCol(s.Net), null, true)); else if (c == "Max DD") r.Add(C(Fp(s.MaxDd), s.MaxDd < 0 ? cRed : cWhite, null, true));
						else if (c == "Open P/L") { double op = e.InTrade ? (e.Dir == 1 ? (e.LastClose - e.EntryPx) / e.EntryPx * 100 : (e.EntryPx - e.LastClose) / e.EntryPx * 100) : double.NaN; r.Add(C(double.IsNaN(op) ? "-" : Fp(op), PosCol(op), null, true)); }
						else r.Add(C(WinTxt(s), cWhite, null, true));
					}
				}
				en.Add(r.ToArray());
			}
		}
		#endregion

		#region Rendering
		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (RenderTarget == null || ChartPanel == null) return;
			try { List<XlCell[]> a, b, c; lock (tLock) { a = tInfo; b = tScan; c = tEng; } if (a != null && a.Count > 0) RenderTable(a, TablePos); if (b != null && b.Count > 0) RenderTable(b, ScanPos); if (c != null && c.Count > 0) RenderTable(c, EngPos); }
			catch (Exception ex) { if (!errShown) { errShown = true; Print("SneakyBuySellXL OnRender error: " + ex); } }
		}
		private static SharpDX.Color4 K(int[] c, float a = 1f) { return new SharpDX.Color4(c[0] / 255f, c[1] / 255f, c[2] / 255f, a); }
		private void RenderTable(List<XlCell[]> rows, XlPos pos)
		{
			var disp = new List<IDisposable>();
			try
			{
				float fs = TableFont; var fcache = new Dictionary<int, SharpDX.DirectWrite.TextFormat>();
				Func<bool, int, SharpDX.DirectWrite.TextFormat> gf = (bold, al) =>
				{
					int key = (bold ? 10 : 0) + al; SharpDX.DirectWrite.TextFormat f; if (fcache.TryGetValue(key, out f)) return f;
					f = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", bold ? SharpDX.DirectWrite.FontWeight.Bold : SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal, fs) { ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center, TextAlignment = al == 1 ? SharpDX.DirectWrite.TextAlignment.Center : al == 2 ? SharpDX.DirectWrite.TextAlignment.Trailing : SharpDX.DirectWrite.TextAlignment.Leading };
					fcache[key] = f; disp.Add(f); return f;
				};
				var fmt = gf(true, 0);
				int nc = 0; foreach (var r in rows) { int c = 0; foreach (var x in r) c += x.Span; nc = Math.Max(nc, c); }
				float[] w = new float[nc]; float pad = 10f, rh = fs * 2.0f;
				Func<string, float> meas = t => { if (string.IsNullOrEmpty(t)) return 0; using (var tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, t, fmt, 3000f, 100f)) return tl.Metrics.Width; };
				foreach (var r in rows) { int c = 0; foreach (var x in r) { if (x.Span == 1) w[c] = Math.Max(w[c], meas(x.T) + 2 * pad); c += x.Span; } }
				foreach (var r in rows) { int c = 0; foreach (var x in r) { if (x.Span > 1) { float have = 0; for (int q = c; q < c + x.Span && q < nc; q++) have += w[q]; float need = meas(x.T) + 2 * pad; if (need > have) { float add = (need - have) / x.Span; for (int q = c; q < c + x.Span && q < nc; q++) w[q] += add; } } c += x.Span; } }
				float tw = 0; foreach (float f in w) tw += f; float th = rows.Count * rh; float pw = ChartPanel.W, ph = ChartPanel.H; int p = (int)pos; int col = p % 3, rowp = p / 3;
				float x0 = col == 0 ? 10f : col == 1 ? (pw - tw) / 2f : pw - tw - 14f; float y0 = rowp == 0 ? 38f : rowp == 1 ? (ph - th) / 2f : ph - th - 12f;
				float y = y0; int ri = 0;
				{ var shadow = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, K(new[] { 0, 0, 0 }, 0.55f)); disp.Add(shadow); RenderTarget.FillRectangle(new SharpDX.RectangleF(x0 + 3, y0 + 3, tw, th), shadow); }
				foreach (var r in rows)
				{
					int c = 0; float x = x0;
					foreach (var cell in r)
					{
						float cw = 0; for (int q = c; q < c + cell.Span && q < nc; q++) cw += w[q];
						bool hdr = cell.Bg == cHdr; int[] bgc = cell.Bg == cRow && (ri % 2 == 1) ? cRow2 : (cell.Bg ?? cRow);
						var bg = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, K(bgc, 1f)); disp.Add(bg); RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, cw, rh), bg);
						var fr = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, K(new[] { 70, 80, 110 }, 1f)); disp.Add(fr); RenderTarget.DrawRectangle(new SharpDX.RectangleF(x, y, cw, rh), fr, 0.6f);
						var tb = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, K(cell.Fg ?? cWhite)); disp.Add(tb);
						var f2 = gf(hdr || cell.Right || cell.Center, cell.Center ? 1 : cell.Right ? 2 : 0);
						RenderTarget.DrawText(cell.T ?? "", f2, new SharpDX.RectangleF(x + pad, y, Math.Max(4f, cw - 2 * pad), rh), tb);
						x += cw; c += cell.Span;
					}
					y += rh; ri++;
				}
				var ob = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, K(cHdr, 1f)); disp.Add(ob); RenderTarget.DrawRectangle(new SharpDX.RectangleF(x0, y0, tw, th), ob, 2f);
			}
			finally { foreach (var d in disp) d.Dispose(); }
		}
		#endregion

		#region Properties
		[NinjaScriptProperty] [Display(Name = "Signal mode", Order = 0, GroupName = "01. General")] public XlMode Mode { get; set; }
		[NinjaScriptProperty] [Display(Name = "Enable SL/TP", Order = 1, GroupName = "01. General")] public bool UseRisk { get; set; }
		[NinjaScriptProperty] [Display(Name = "Exit trigger", Order = 2, GroupName = "01. General")] public XlExit ExitTrig { get; set; }
		[NinjaScriptProperty] [Range(1, 500)] [Display(Name = "ATR length", Order = 3, GroupName = "01. General")] public int AtrLen { get; set; }
		[NinjaScriptProperty] [Range(1, 500)] [Display(Name = "Lookback (last N closed trades)", Order = 4, GroupName = "01. General")] public int Lookback { get; set; }
		[NinjaScriptProperty] [Range(1, 500)] [Display(Name = "Average body length", Order = 0, GroupName = "02. OC Mode")] public int OcBodyLen { get; set; }
		[NinjaScriptProperty] [Range(0, 20)] [Display(Name = "Min body vs average (x)", Order = 1, GroupName = "02. OC Mode")] public double OcBodyMult { get; set; }
		[NinjaScriptProperty] [Range(0, 20)] [Display(Name = "Min volume vs average (x)", Order = 2, GroupName = "02. OC Mode")] public double OcVol { get; set; }
		[NinjaScriptProperty] [Display(Name = "Alternate signals", Order = 3, GroupName = "02. OC Mode")] public bool OcAlt { get; set; }
		[NinjaScriptProperty] [Display(Name = "Ignore signals while a trade is open", Order = 4, GroupName = "02. OC Mode")] public bool OcNoOvl { get; set; }
		[NinjaScriptProperty] [Display(Name = "Stop loss mode (ATR / CandleWick / CandleBody / None)", Order = 5, GroupName = "02. OC Mode")] public XlSl OcSl { get; set; }
		[NinjaScriptProperty] [Range(0, 5)] [Display(Name = "Stop buffer (x ATR)", Order = 6, GroupName = "02. OC Mode")] public double OcSlBuf { get; set; }
		[NinjaScriptProperty] [Display(Name = "Take profit mode", Order = 7, GroupName = "02. OC Mode")] public XlTp OcTp { get; set; }
		[NinjaScriptProperty] [Range(0.1, 20)] [Display(Name = "Stop loss (x ATR)", Order = 8, GroupName = "02. OC Mode")] public double OcSlMult { get; set; }
		[NinjaScriptProperty] [Range(0.1, 50)] [Display(Name = "Take profit (x ATR)", Order = 9, GroupName = "02. OC Mode")] public double OcTpMult { get; set; }
		[NinjaScriptProperty] [Range(0.1, 50)] [Display(Name = "Take profit (x SL)", Order = 10, GroupName = "02. OC Mode")] public double OcTpR { get; set; }
		[NinjaScriptProperty] [Display(Name = "Variable base (off = fixed length)", Order = 0, GroupName = "03. Breakout Mode")] public bool BrkVar { get; set; }
		[NinjaScriptProperty] [Range(3, 500)] [Display(Name = "Fixed base length (bars)", Order = 1, GroupName = "03. Breakout Mode")] public int BrkBaseLen { get; set; }
		[NinjaScriptProperty] [Range(3, 200)] [Display(Name = "Variable min base", Order = 2, GroupName = "03. Breakout Mode")] public int BrkVarMin { get; set; }
		[NinjaScriptProperty] [Range(3, 200)] [Display(Name = "Variable max base", Order = 3, GroupName = "03. Breakout Mode")] public int BrkVarMax { get; set; }
		[NinjaScriptProperty] [Range(0.1, 20)] [Display(Name = "Max base height (x ATR)", Order = 4, GroupName = "03. Breakout Mode")] public double BrkBaseMult { get; set; }
		[NinjaScriptProperty] [Display(Name = "Breakout entry mode", Order = 5, GroupName = "03. Breakout Mode")] public XlBrkEntry BrkEntry { get; set; }
		[NinjaScriptProperty] [Range(0, 10)] [Display(Name = "Min breakout strength (x ATR)", Order = 6, GroupName = "03. Breakout Mode")] public double BrkMinBreak { get; set; }
		[NinjaScriptProperty] [Range(0, 20)] [Display(Name = "Min volume vs average (x)", Order = 7, GroupName = "03. Breakout Mode")] public double BrkVolX { get; set; }
		[NinjaScriptProperty] [Range(1, 500)] [Display(Name = "Average volume length", Order = 8, GroupName = "03. Breakout Mode")] public int BrkVolLen { get; set; }
		[NinjaScriptProperty] [Display(Name = "Trend filter", Order = 9, GroupName = "03. Breakout Mode")] public bool BrkTrend { get; set; }
		[NinjaScriptProperty] [Range(2, 1000)] [Display(Name = "Trend EMA length", Order = 10, GroupName = "03. Breakout Mode")] public int BrkTrendLen { get; set; }
		[NinjaScriptProperty] [Display(Name = "Alternate signals", Order = 11, GroupName = "03. Breakout Mode")] public bool BrkAlt { get; set; }
		[NinjaScriptProperty] [Display(Name = "Ignore signals while a trade is open", Order = 12, GroupName = "03. Breakout Mode")] public bool BrkNoOvl { get; set; }
		[NinjaScriptProperty] [Display(Name = "Ignore base development while a trade is open", Order = 13, GroupName = "03. Breakout Mode")] public bool BrkNoBase { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show base zones", Order = 14, GroupName = "03. Breakout Mode")] public bool BrkShowBase { get; set; }
		[NinjaScriptProperty] [Display(Name = "Stop loss mode (ATR / BaseEdge)", Order = 15, GroupName = "03. Breakout Mode")] public XlSl BrkSl { get; set; }
		[NinjaScriptProperty] [Range(0, 5)] [Display(Name = "Stop buffer (x ATR)", Order = 16, GroupName = "03. Breakout Mode")] public double BrkSlBuf { get; set; }
		[NinjaScriptProperty] [Display(Name = "Take profit mode (ATRTarget / SLMultiple / BaseHeight)", Order = 17, GroupName = "03. Breakout Mode")] public XlTp BrkTp { get; set; }
		[NinjaScriptProperty] [Range(0.1, 20)] [Display(Name = "Stop loss (x ATR)", Order = 18, GroupName = "03. Breakout Mode")] public double BrkSlMult { get; set; }
		[NinjaScriptProperty] [Range(0.1, 50)] [Display(Name = "Take profit (x ATR)", Order = 19, GroupName = "03. Breakout Mode")] public double BrkTpMult { get; set; }
		[NinjaScriptProperty] [Range(0.1, 50)] [Display(Name = "Take profit (x SL)", Order = 20, GroupName = "03. Breakout Mode")] public double BrkTpR { get; set; }
		[NinjaScriptProperty] [Range(0.1, 50)] [Display(Name = "Take profit (x base height)", Order = 21, GroupName = "03. Breakout Mode")] public double BrkTpB { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show trend EMA", Order = 22, GroupName = "03. Breakout Mode")] public bool BrkShowEma { get; set; }
		[NinjaScriptProperty] [Range(2, 500)] [Display(Name = "Channel length", Order = 0, GroupName = "04. Donchian Mode")] public int DonLen { get; set; }
		[NinjaScriptProperty] [Display(Name = "Entry at the broken band (off = bar close)", Order = 1, GroupName = "04. Donchian Mode")] public bool DonEdge { get; set; }
		[NinjaScriptProperty] [Range(0, 20)] [Display(Name = "Min volume vs average (x)", Order = 2, GroupName = "04. Donchian Mode")] public double DonVol { get; set; }
		[NinjaScriptProperty] [Display(Name = "Alternate signals", Order = 3, GroupName = "04. Donchian Mode")] public bool DonAlt { get; set; }
		[NinjaScriptProperty] [Display(Name = "Ignore signals while a trade is open", Order = 4, GroupName = "04. Donchian Mode")] public bool DonNoOvl { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show Donchian channel", Order = 5, GroupName = "04. Donchian Mode")] public bool DonShow { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show middle line", Order = 6, GroupName = "04. Donchian Mode")] public bool DonShowMid { get; set; }
		[NinjaScriptProperty] [Display(Name = "Stop loss mode (ATR / ChannelOpposite / ChannelMiddle / CandleWick / CandleBody / None)", Order = 7, GroupName = "04. Donchian Mode")] public XlSl DonSl { get; set; }
		[NinjaScriptProperty] [Range(0, 5)] [Display(Name = "Stop buffer (x ATR)", Order = 8, GroupName = "04. Donchian Mode")] public double DonSlBuf { get; set; }
		[NinjaScriptProperty] [Display(Name = "Take profit mode", Order = 9, GroupName = "04. Donchian Mode")] public XlTp DonTp { get; set; }
		[NinjaScriptProperty] [Range(0.1, 20)] [Display(Name = "Stop loss (x ATR)", Order = 10, GroupName = "04. Donchian Mode")] public double DonSlMult { get; set; }
		[NinjaScriptProperty] [Range(0.1, 50)] [Display(Name = "Take profit (x ATR)", Order = 11, GroupName = "04. Donchian Mode")] public double DonTpMult { get; set; }
		[NinjaScriptProperty] [Range(0.1, 50)] [Display(Name = "Take profit (x SL)", Order = 12, GroupName = "04. Donchian Mode")] public double DonTpR { get; set; }
		[NinjaScriptProperty] [Range(1, 500)] [Display(Name = "Fast MA length", Order = 0, GroupName = "05. MA Cross Mode")] public int MaFast { get; set; }
		[NinjaScriptProperty] [Range(2, 1000)] [Display(Name = "Slow MA length", Order = 1, GroupName = "05. MA Cross Mode")] public int MaSlow { get; set; }
		[NinjaScriptProperty] [Display(Name = "MA type", Order = 2, GroupName = "05. MA Cross Mode")] public XlMaType MaKind { get; set; }
		[NinjaScriptProperty] [Display(Name = "MA source", Order = 3, GroupName = "05. MA Cross Mode")] public XlMaSrc MaSrcSel { get; set; }
		[NinjaScriptProperty] [Range(0, 10)] [Display(Name = "Min cross strength (x ATR)", Order = 4, GroupName = "05. MA Cross Mode")] public double MaMinSep { get; set; }
		[NinjaScriptProperty] [Display(Name = "Trend filter", Order = 5, GroupName = "05. MA Cross Mode")] public bool MaTrend { get; set; }
		[NinjaScriptProperty] [Range(2, 1000)] [Display(Name = "Trend MA length", Order = 6, GroupName = "05. MA Cross Mode")] public int MaTrendLen { get; set; }
		[NinjaScriptProperty] [Display(Name = "Entry at the slow MA (off = bar close)", Order = 7, GroupName = "05. MA Cross Mode")] public bool MaEdge { get; set; }
		[NinjaScriptProperty] [Range(0, 20)] [Display(Name = "Min volume vs average (x)", Order = 8, GroupName = "05. MA Cross Mode")] public double MaVol { get; set; }
		[NinjaScriptProperty] [Display(Name = "Alternate signals", Order = 9, GroupName = "05. MA Cross Mode")] public bool MaAlt { get; set; }
		[NinjaScriptProperty] [Display(Name = "Ignore signals while a trade is open", Order = 10, GroupName = "05. MA Cross Mode")] public bool MaNoOvl { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show moving averages", Order = 11, GroupName = "05. MA Cross Mode")] public bool MaShow { get; set; }
		[NinjaScriptProperty] [Display(Name = "Stop loss mode (ATR / Swing / CandleWick / CandleBody / None)", Order = 12, GroupName = "05. MA Cross Mode")] public XlSl MaSl { get; set; }
		[NinjaScriptProperty] [Range(1, 200)] [Display(Name = "Swing length (bars)", Order = 13, GroupName = "05. MA Cross Mode")] public int MaSwingLen { get; set; }
		[NinjaScriptProperty] [Range(0, 5)] [Display(Name = "Stop buffer (x ATR)", Order = 14, GroupName = "05. MA Cross Mode")] public double MaSlBuf { get; set; }
		[NinjaScriptProperty] [Range(0, 10)] [Display(Name = "Min stop distance (x ATR)", Order = 15, GroupName = "05. MA Cross Mode")] public double MaSlMin { get; set; }
		[NinjaScriptProperty] [Display(Name = "Take profit mode", Order = 16, GroupName = "05. MA Cross Mode")] public XlTp MaTp { get; set; }
		[NinjaScriptProperty] [Range(0.1, 20)] [Display(Name = "Stop loss (x ATR)", Order = 17, GroupName = "05. MA Cross Mode")] public double MaSlMult { get; set; }
		[NinjaScriptProperty] [Range(0.1, 50)] [Display(Name = "Take profit (x ATR)", Order = 18, GroupName = "05. MA Cross Mode")] public double MaTpMult { get; set; }
		[NinjaScriptProperty] [Range(0.1, 50)] [Display(Name = "Take profit (x SL)", Order = 19, GroupName = "05. MA Cross Mode")] public double MaTpR { get; set; }
		[NinjaScriptProperty] [Range(1, 200)] [Display(Name = "Supertrend ATR length", Order = 0, GroupName = "06. Supertrend Mode")] public int StLen { get; set; }
		[NinjaScriptProperty] [Range(0.1, 20)] [Display(Name = "Supertrend factor", Order = 1, GroupName = "06. Supertrend Mode")] public double StMult { get; set; }
		[NinjaScriptProperty] [Display(Name = "Entry at the broken line (off = bar close)", Order = 2, GroupName = "06. Supertrend Mode")] public bool StEdge { get; set; }
		[NinjaScriptProperty] [Display(Name = "Ignore signals while a trade is open", Order = 3, GroupName = "06. Supertrend Mode")] public bool StNoOvl { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show Supertrend line", Order = 4, GroupName = "06. Supertrend Mode")] public bool StShow { get; set; }
		[NinjaScriptProperty] [Display(Name = "Stop loss mode (SupertrendLine / ATR / CandleWick / CandleBody / None)", Order = 5, GroupName = "06. Supertrend Mode")] public XlSl StSl { get; set; }
		[NinjaScriptProperty] [Range(0, 5)] [Display(Name = "Stop buffer (x ATR)", Order = 6, GroupName = "06. Supertrend Mode")] public double StSlBuf { get; set; }
		[NinjaScriptProperty] [Display(Name = "Take profit mode", Order = 7, GroupName = "06. Supertrend Mode")] public XlTp StTp { get; set; }
		[NinjaScriptProperty] [Range(0.1, 20)] [Display(Name = "Stop loss (x ATR)", Order = 8, GroupName = "06. Supertrend Mode")] public double StSlMult { get; set; }
		[NinjaScriptProperty] [Range(0.1, 50)] [Display(Name = "Take profit (x ATR)", Order = 9, GroupName = "06. Supertrend Mode")] public double StTpMult { get; set; }
		[NinjaScriptProperty] [Range(0.1, 50)] [Display(Name = "Take profit (x SL)", Order = 10, GroupName = "06. Supertrend Mode")] public double StTpR { get; set; }
		[NinjaScriptProperty] [Range(2, 500)] [Display(Name = "Aroon length", Order = 0, GroupName = "07. Aroon Mode")] public int ArLen { get; set; }
		[NinjaScriptProperty] [Display(Name = "Signal mode", Order = 1, GroupName = "07. Aroon Mode")] public XlArSig ArSig { get; set; }
		[NinjaScriptProperty] [Range(50, 100)] [Display(Name = "Strong trend level", Order = 2, GroupName = "07. Aroon Mode")] public double ArStrongLvl { get; set; }
		[NinjaScriptProperty] [Display(Name = "Smoothing", Order = 3, GroupName = "07. Aroon Mode")] public XlArSm ArSm { get; set; }
		[NinjaScriptProperty] [Range(1, 100)] [Display(Name = "Smoothing length", Order = 4, GroupName = "07. Aroon Mode")] public int ArSmLen { get; set; }
		[NinjaScriptProperty] [Range(0, 20)] [Display(Name = "Min volume vs average (x)", Order = 5, GroupName = "07. Aroon Mode")] public double ArVol { get; set; }
		[NinjaScriptProperty] [Range(1, 500)] [Display(Name = "Average volume length", Order = 6, GroupName = "07. Aroon Mode")] public int ArVolLen { get; set; }
		[NinjaScriptProperty] [Display(Name = "Alternate signals", Order = 7, GroupName = "07. Aroon Mode")] public bool ArAlt { get; set; }
		[NinjaScriptProperty] [Display(Name = "Ignore signals while a trade is open", Order = 8, GroupName = "07. Aroon Mode")] public bool ArNoOvl { get; set; }
		[NinjaScriptProperty] [Display(Name = "Stop loss mode (ATR / AroonExtreme / CandleWick / CandleBody / None)", Order = 9, GroupName = "07. Aroon Mode")] public XlSl ArSl { get; set; }
		[NinjaScriptProperty] [Range(0, 5)] [Display(Name = "Stop buffer (x ATR)", Order = 10, GroupName = "07. Aroon Mode")] public double ArSlBuf { get; set; }
		[NinjaScriptProperty] [Display(Name = "Take profit mode", Order = 11, GroupName = "07. Aroon Mode")] public XlTp ArTp { get; set; }
		[NinjaScriptProperty] [Range(0.1, 20)] [Display(Name = "Stop loss (x ATR)", Order = 12, GroupName = "07. Aroon Mode")] public double ArSlMult { get; set; }
		[NinjaScriptProperty] [Range(0.1, 50)] [Display(Name = "Take profit (x ATR)", Order = 13, GroupName = "07. Aroon Mode")] public double ArTpMult { get; set; }
		[NinjaScriptProperty] [Range(0.1, 50)] [Display(Name = "Take profit (x SL)", Order = 14, GroupName = "07. Aroon Mode")] public double ArTpR { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show BUY/SELL labels", Order = 0, GroupName = "08. Display")] public bool ShowLabels { get; set; }
		[NinjaScriptProperty] [Range(7, 24)] [Display(Name = "BUY/SELL label font size", Order = 1, GroupName = "08. Display")] public int LabelFont { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show entry marker (dot)", Order = 2, GroupName = "08. Display")] public bool ShowEntryDot { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show trade lines", Order = 3, GroupName = "08. Display")] public bool ShowTradeLines { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show trade labels", Order = 4, GroupName = "08. Display")] public bool ShowTradeLbl { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show exit X", Order = 5, GroupName = "08. Display")] public bool ShowX { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show trade zone shading", Order = 6, GroupName = "08. Display")] public bool ShowZone { get; set; }
		[NinjaScriptProperty] [Range(0, 50)] [Display(Name = "Show closed trades (0 = off)", Order = 7, GroupName = "08. Display")] public int ShowClosed { get; set; }
		[NinjaScriptProperty] [Range(100, 200000)] [Display(Name = "Draw bars back", Order = 8, GroupName = "08. Display")] public int DrawBarsBack { get; set; }
		[NinjaScriptProperty] [Range(1, 5)] [Display(Name = "Trade line width", Order = 9, GroupName = "08. Display")] public int LineW { get; set; }
		[NinjaScriptProperty] [Range(1, 500)] [Display(Name = "Trade line extension (bars)", Order = 10, GroupName = "08. Display")] public int LineExt { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show info box", Order = 0, GroupName = "09. Info Box")] public bool ShowTable { get; set; }
		[NinjaScriptProperty] [Display(Name = "Info box position", Order = 1, GroupName = "09. Info Box")] public XlPos TablePos { get; set; }
		[NinjaScriptProperty] [Range(8, 28)] [Display(Name = "Table font size", Order = 2, GroupName = "09. Info Box")] public int TableFont { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show multi-timeframe scanner", Order = 0, GroupName = "10. Scanners")] public bool ShowScan { get; set; }
		[NinjaScriptProperty] [Display(Name = "Scanner timeframes (comma separated; plain number = minutes, 4h, D, W)", Order = 1, GroupName = "10. Scanners")] public string TfList { get; set; }
		[NinjaScriptProperty] [Range(1, 12)] [Display(Name = "Max timeframes", Order = 2, GroupName = "10. Scanners")] public int ScanMax { get; set; }
		[NinjaScriptProperty] [Display(Name = "Scanner position", Order = 3, GroupName = "10. Scanners")] public XlPos ScanPos { get; set; }
		[NinjaScriptProperty] [Display(Name = "Scanner detail", Order = 4, GroupName = "10. Scanners")] public XlDetail ScanDetail { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show engine scanner (all six engines)", Order = 5, GroupName = "10. Scanners")] public bool ShowEng { get; set; }
		[NinjaScriptProperty] [Display(Name = "Engine scanner position", Order = 6, GroupName = "10. Scanners")] public XlPos EngPos { get; set; }
		[NinjaScriptProperty] [Display(Name = "Engine scanner detail", Order = 7, GroupName = "10. Scanners")] public XlDetail EngDetail { get; set; }
		[NinjaScriptProperty] [Display(Name = "Engine scanner: only the active signal mode (ignores the ticks below)", Order = 8, GroupName = "10. Scanners")] public bool EngOnlyActive { get; set; }
		[NinjaScriptProperty] [Display(Name = "Engine scanner: show OC", Order = 9, GroupName = "10. Scanners")] public bool EngOC { get; set; }
		[NinjaScriptProperty] [Display(Name = "Engine scanner: show Breakout", Order = 10, GroupName = "10. Scanners")] public bool EngBrk { get; set; }
		[NinjaScriptProperty] [Display(Name = "Engine scanner: show Donchian", Order = 11, GroupName = "10. Scanners")] public bool EngDon { get; set; }
		[NinjaScriptProperty] [Display(Name = "Engine scanner: show MA Cross", Order = 12, GroupName = "10. Scanners")] public bool EngMA { get; set; }
		[NinjaScriptProperty] [Display(Name = "Engine scanner: show Supertrend", Order = 13, GroupName = "10. Scanners")] public bool EngST { get; set; }
		[NinjaScriptProperty] [Display(Name = "Engine scanner: show Aroon", Order = 14, GroupName = "10. Scanners")] public bool EngAr { get; set; }
		[NinjaScriptProperty] [Display(Name = "Alerts on signals and exits (realtime)", Order = 0, GroupName = "11. Alerts")] public bool AlertsOn { get; set; }

		[Browsable(false)] [XmlIgnore] public Series<double> SignalPlot { get { Update(); return Values[0]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> EntryPlot { get { Update(); return Values[1]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> StopPlot { get { Update(); return Values[2]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> TargetPlot { get { Update(); return Values[3]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> TradeDirPlot { get { Update(); return Values[4]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> ExitCodePlot { get { Update(); return Values[5]; } }
		#endregion
	}
}
