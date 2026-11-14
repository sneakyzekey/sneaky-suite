// SneakyFusion17 - NinjaTrader 8 port of the signal logic of the open-source TradingView "DrFX God Mode X Fusion17" (MPL 2.0 idea; code written from scratch).
// UNPROVEN: honest backtests on NQ 5m (Jun-Oct 2026) show no robust edge. Python vs C# parity: 563 signal events, 0 mismatches, 17 votes equal to 1e-14.
// Not verified against TradingView itself. Hidden plots: 0 Signal(+1/-1), 1 Entry, 2 Stop, 3 TP1, 4 TP2, 5 Bull, 6 Bear.
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

namespace NinjaTrader.NinjaScript.Indicators
{


internal class F17Ema
{
	readonly int n; readonly double a; readonly double[] buf; int cnt, run; double prev = double.NaN;
	public F17Ema(int len) { n = len; a = 2.0 / (len + 1); buf = new double[len]; }
	public double Update(double x)
	{
		if (double.IsNaN(x)) { prev = double.NaN; run = 0; return double.NaN; }
		buf[run % n] = x; run++;
		if (double.IsNaN(prev)) { if (run >= n) { double s = 0; for (int i = 0; i < n; i++) s += buf[i]; prev = s / n; } else return double.NaN; }
		else prev = a * x + (1 - a) * prev;
		return prev;
	}
}
internal class F17Rma
{
	readonly int n; readonly double[] buf; int run; double prev = double.NaN;
	public F17Rma(int len) { n = len; buf = new double[len]; }
	public double Update(double x)
	{
		if (double.IsNaN(x)) { prev = double.NaN; run = 0; return double.NaN; }
		buf[run % n] = x; run++;
		if (double.IsNaN(prev)) { if (run >= n) { double s = 0; for (int i = 0; i < n; i++) s += buf[i]; prev = s / n; } else return double.NaN; }
		else prev = (prev * (n - 1) + x) / n;
		return prev;
	}
}
internal class F17Sma
{
	readonly int n; readonly double[] buf; int run;
	public F17Sma(int len) { n = len; buf = new double[len]; }
	public double Update(double x)
	{
		if (double.IsNaN(x)) { run = 0; return double.NaN; }
		buf[run % n] = x; run++; if (run < n) return double.NaN;
		double s = 0; for (int i = 0; i < n; i++) s += buf[i]; return s / n;
	}
}
internal class F17Zone { public int Left; public double Top, Bot; }
internal class F17P
{
	public int Fast = 21, Slow = 55, Bias = 200, Pivot = 5, MinScore = 62, MinEdge = 20, Cooldown = 8, RegLen = 24, EntropyWin = 28, ChannelLen = 21, MinBreadth = 7;
	public int MaxGaps = 6, MaxZones = 4; public bool RequireTrend = true, DisallowChop = true, Adaptive = true, QualityGate = false; public double FvgAtr = 0.22, AtrStop = 1.8, Rr1 = 1.5, Rr2 = 3.0;
}
internal class F17Core
{
	public const double Tick = 0.25; public static double MinTick = 0.25;
	static readonly double[] W = { 1.25, 1.00, 1.10, 0.80, 1.00, 0.75, 1.20, 0.75, 0.90, 0.70, 0.90, 1.00, 0.55, 0.70, 0.75, 0.65, 0.60 };
	public static readonly string[] Names = { "APEX Control", "Crypto Hunter", "Dark Knight", "Fury FX", "FX Pro Adaptive", "GOD / Prop Hunt", "ICT Structure", "QMAE Entropy", "RAPTOR Wave", "SW Force", "Trader Scalper", "TRH Zones", "VENOM Fusion", "VP Vision", "Volume Trader", "Diamond HTF", "AtroNox VWAP" };
	readonly F17P p;
	// history
	readonly List<double> O = new List<double>(), H = new List<double>(), L = new List<double>(), C = new List<double>(), V = new List<double>(), Atr = new List<double>(), Reg = new List<double>(), KamaL = new List<double>(), EmaSL = new List<double>(), Dc = new List<double>(), Hlc3 = new List<double>(), Ent = new List<double>(), Sdir = new List<double>(), ChU = new List<double>(), UpP = new List<double>(), DnP = new List<double>();
	readonly List<double> HistL = new List<double>(); readonly F17Ema rMean = new F17Ema(34);
	readonly List<double> CxV = new List<double>(), UpV = new List<double>(), DnV = new List<double>(), PivH = new List<double>(), PivL = new List<double>(), Chg = new List<double>();
	// recursive state
	readonly F17Ema eF, eS, eB, e12, e26, eSig, eFlow, eVol, ePress, eSpec, eForce, e34;
	readonly F17Rma rAtr, rAtrSt, rUp, rDn, rTr, rPdm, rMdm, rAdx;
	readonly F17Sma sAtr50, sVol20, sDc24, sDc10;
	double prevHigh = double.NaN, prevLow = double.NaN, prevClose = double.NaN;
	double lastSH = double.NaN, lastSL = double.NaN; int ms = 0; double ldT = double.NaN, ldB = double.NaN, lsT = double.NaN, lsB = double.NaN;
	double plusFix = double.NaN, minusFix = double.NaN; double kamaPrev = double.NaN;
	double stUB = double.NaN, stLB = double.NaN, stST = double.NaN; double stAtrPrev = double.NaN;
	double sessV, sessPV, sessH, sessL, pdH = double.NaN, pdL = double.NaN; bool sessInit;
	public int Bar = -1, LastSig = int.MinValue;
	// outputs for the current bar
	public double[] E = new double[17]; public double Bull, Bear, Edge, AtrV = double.NaN, Adx = double.NaN, EntropyV = double.NaN, EmaF = double.NaN, EmaS = double.NaN, EmaB = double.NaN, Vwap = double.NaN;
	public bool Long, Short, Trending, Choppy, BullBreak, BearBreak, BullChoch, BearChoch, SweepH, SweepL, BullFVG, BearFVG; public int BullBr, BearBr, MS; public string Regime = "";
	public double Kama { get { return kamaPrev; } } public double SUp = double.NaN, SLo = double.NaN, RibM = double.NaN, RibS = double.NaN, Rg34 = double.NaN, Impulse; public double[] Radar = new double[8]; public List<F17Zone> BullGaps = new List<F17Zone>(), BearGaps = new List<F17Zone>(), Demand = new List<F17Zone>(), Supply = new List<F17Zone>(); public double LastSH2 { get { return lastSH; } } public double LastSL2 { get { return lastSL; } }
	public double VolPct = double.NaN, RsiV = double.NaN, EffV = double.NaN, RelVolV = double.NaN, MfiV = double.NaN, PdH = double.NaN, PdL = double.NaN; public double[] EW = new double[17]; public int Modules;

	public F17Core(F17P pp)
	{
		p = pp; eF = new F17Ema(p.Fast); eS = new F17Ema(p.Slow); eB = new F17Ema(p.Bias); e12 = new F17Ema(12); e26 = new F17Ema(26); eSig = new F17Ema(9); eFlow = new F17Ema(5); eVol = new F17Ema(5); ePress = new F17Ema(8); eSpec = new F17Ema(5); eForce = new F17Ema(5); e34 = new F17Ema(34);
		rAtr = new F17Rma(14); rAtrSt = new F17Rma(11); rUp = new F17Rma(14); rDn = new F17Rma(14); rTr = new F17Rma(14); rPdm = new F17Rma(14); rMdm = new F17Rma(14); rAdx = new F17Rma(14);
		sAtr50 = new F17Sma(50); sVol20 = new F17Sma(20); sDc24 = new F17Sma(p.RegLen); sDc10 = new F17Sma(10);
	}
	static double Nz(double x, double d = 0.0) { return double.IsNaN(x) ? d : x; }
	static double Clamp(double x, double lo, double hi) { return Math.Min(hi, Math.Max(lo, x)); }
	static double Vote(double v) { return double.IsNaN(v) ? 0.0 : Math.Max(-1.0, Math.Min(1.0, v)); }
	static double VoteN(double v) { return double.IsNaN(v) ? double.NaN : Math.Max(-1.0, Math.Min(1.0, v)); }
	double Back(List<double> s, int k) { int idx = s.Count - 1 - k; return idx >= 0 ? s[idx] : double.NaN; }
	double BackNz(List<double> s, int k, double dflt) { int idx = s.Count - 1 - k; return idx >= 0 && !double.IsNaN(s[idx]) ? s[idx] : dflt; }
	double Mx(List<double> s, int from, int to) { double m = double.NegativeInfinity; for (int i = from; i <= to; i++) if (s[i] > m) m = s[i]; return m; }
	double Mn(List<double> s, int from, int to) { double m = double.PositiveInfinity; for (int i = from; i <= to; i++) if (s[i] < m) m = s[i]; return m; }

	// hc1/he1/hc2/he2 = previous COMPLETED higher-timeframe close and EMA50 (NaN if unavailable); newSession = first bar of a new trading session.
	public void Step(double o, double h, double l, double c, double v, bool newSession, double hc1, double he1, double hc2, double he2, bool intradayVwap)
	{
		Bar++; int i = Bar; O.Add(o); H.Add(h); L.Add(l); C.Add(c); V.Add(v); double mt = MinTick;
		double tr1 = i == 0 ? h - l : Math.Max(h - l, Math.Max(Math.Abs(h - prevClose), Math.Abs(l - prevClose))); double trNa = i == 0 ? double.NaN : tr1;
		double atr = rAtr.Update(tr1); Atr.Add(atr); AtrV = atr; double atrAvg = sAtr50.Update(atr); VolPct = atrAvg > 0 ? atr / atrAvg * 100 : double.NaN;
		double emaF = eF.Update(c), emaS = eS.Update(c), emaB = eB.Update(c); EmaF = emaF; EmaS = emaS; EmaB = emaB; EmaSL.Add(emaS);
		double ch = i == 0 ? double.NaN : c - prevClose; double up = double.IsNaN(ch) ? double.NaN : Math.Max(ch, 0), dn = double.IsNaN(ch) ? double.NaN : -Math.Min(ch, 0);
		double ru = rUp.Update(up), rd = rDn.Update(dn); double rsi = double.NaN; if (!double.IsNaN(ru) && !double.IsNaN(rd)) rsi = rd == 0 ? 100.0 : (ru == 0 ? 0.0 : 100 - 100 / (1 + ru / rd)); RsiV = rsi;
		double m12 = e12.Update(c), m26 = e26.Update(c); double macdL = (double.IsNaN(m12) || double.IsNaN(m26)) ? double.NaN : m12 - m26; double sig = eSig.Update(macdL); double hist = macdL - sig;
		// DMI
		double upm = double.NaN, dnm = double.NaN; if (i > 0) { upm = h - H[i - 1]; dnm = -(l - L[i - 1]); }
		double pdm = double.IsNaN(upm) ? double.NaN : (upm > dnm && upm > 0 ? upm : 0.0), mdm = double.IsNaN(dnm) ? double.NaN : (dnm > upm && dnm > 0 ? dnm : 0.0);
		double trur = rTr.Update(trNa); double plus = 100 * rPdm.Update(pdm) / trur, minus = 100 * rMdm.Update(mdm) / trur;
		if (double.IsNaN(plus)) plus = plusFix; else plusFix = plus; if (double.IsNaN(minus)) minus = minusFix; else minusFix = minus;
		double ssum = plus + minus; double ratio = Math.Abs(plus - minus) / (ssum == 0 ? 1 : ssum); double adx = 100 * rAdx.Update(ratio); Adx = adx;
		double volMean = sVol20.Update(v); bool hasVol = volMean > 0; double relVol = hasVol ? v / volMean : double.NaN;
		double rng = Math.Max(h - l, mt); double vpr = hasVol ? v * (c - o) / rng : double.NaN; double flowS = eFlow.Update(vpr), volE = eVol.Update(v);
		double flowProxy = (hasVol && !double.IsNaN(flowS) && !double.IsNaN(volE)) ? 100 * flowS / Math.Max(volE, 1.0) : double.NaN;
		double dc = i == 0 ? double.NaN : Math.Abs(ch); Dc.Add(dc); double dist = Math.Abs(c - BackNz(C, p.RegLen, c)); double trav = sDc24.Update(dc) * p.RegLen; double eff = trav > 0 ? Clamp(dist / trav, 0, 1) : 0.0;
		EffV = eff; RelVolV = relVol; Trending = adx >= 23 && eff >= 0.28; Choppy = adx < 17 || eff < 0.17; Regime = Trending ? "DIRECTIONAL" : Choppy ? "RANGE / CHOP" : "TRANSITION";
		// session VWAP / previous-day range
		double hlc3 = (h + l + c) / 3; Hlc3.Add(hlc3);
		if (!sessInit) { sessInit = true; sessV = 0; sessPV = 0; sessH = h; sessL = l; }
		else if (newSession) { pdH = sessH; pdL = sessL; sessV = 0; sessPV = 0; sessH = h; sessL = l; }
		sessV += v; sessPV += v * hlc3; PdH = pdH; PdL = pdL; sessH = Math.Max(sessH, h); sessL = Math.Min(sessL, l); double vwap = sessV > 0 ? sessPV / sessV : double.NaN; Vwap = vwap;
		// pivots / structure
		double pivH = double.NaN, pivL = double.NaN; int pl = p.Pivot;
		if (i >= 2 * pl) { int m = i - pl; if (H[m] >= Mx(H, m - pl, m + pl)) pivH = H[m]; if (L[m] <= Mn(L, m - pl, m + pl)) pivL = L[m]; }
		if (!double.IsNaN(pivH)) lastSH = pivH; if (!double.IsNaN(pivL)) lastSL = pivL;
		bool bb = !double.IsNaN(lastSH) && i > 0 && c > lastSH && prevClose <= lastSH; bool sb = !double.IsNaN(lastSL) && i > 0 && c < lastSL && prevClose >= lastSL;
		BullBreak = bb; BearBreak = sb; BullChoch = bb && ms == -1; BearChoch = sb && ms == 1; if (bb) ms = 1; if (sb) ms = -1; MS = ms;
		SweepH = !double.IsNaN(lastSH) && h > lastSH && c < lastSH; SweepL = !double.IsNaN(lastSL) && l < lastSL && c > lastSL;
		for (int q = BullGaps.Count - 1; q >= 0; q--) if (l <= BullGaps[q].Bot) BullGaps.RemoveAt(q);
		for (int q = BearGaps.Count - 1; q >= 0; q--) if (h >= BearGaps[q].Top) BearGaps.RemoveAt(q);
		for (int q = Demand.Count - 1; q >= 0; q--) if (c < Demand[q].Bot) Demand.RemoveAt(q);
		for (int q = Supply.Count - 1; q >= 0; q--) if (c > Supply[q].Top) Supply.RemoveAt(q);
		BullFVG = BearFVG = false; if (i >= 2 && !double.IsNaN(atr)) { BullFVG = l > H[i - 2] && (l - H[i - 2]) >= atr * p.FvgAtr; BearFVG = h < L[i - 2] && (L[i - 2] - h) >= atr * p.FvgAtr; }
		if (BullFVG) { BullGaps.Add(new F17Zone { Left = i - 2, Top = l, Bot = H[i - 2] }); if (BullGaps.Count > p.MaxGaps) BullGaps.RemoveAt(0); }
		if (BearFVG) { BearGaps.Add(new F17Zone { Left = i - 2, Top = L[i - 2], Bot = h }); if (BearGaps.Count > p.MaxGaps) BearGaps.RemoveAt(0); }
		if (bb) { for (int k = 1; k <= 8; k++) if (i - k >= 0 && C[i - k] < O[i - k]) { double ot = Math.Max(O[i - k], C[i - k]); if (ot > L[i - k]) { Demand.Add(new F17Zone { Left = i - k, Top = ot, Bot = L[i - k] }); if (Demand.Count > p.MaxZones) Demand.RemoveAt(0); } break; } }
		if (sb) { for (int k = 1; k <= 8; k++) if (i - k >= 0 && C[i - k] > O[i - k]) { double ob = Math.Min(O[i - k], C[i - k]); if (H[i - k] > ob) { Supply.Add(new F17Zone { Left = i - k, Top = H[i - k], Bot = ob }); if (Supply.Count > p.MaxZones) Supply.RemoveAt(0); } break; } }
		if (bb) { ldT = ldB = double.NaN; for (int k = 1; k <= 8; k++) if (i - k >= 0 && double.IsNaN(ldT) && C[i - k] < O[i - k]) { ldT = Math.Max(O[i - k], C[i - k]); ldB = L[i - k]; } }
		if (sb) { lsT = lsB = double.NaN; for (int k = 1; k <= 8; k++) if (i - k >= 0 && double.IsNaN(lsT) && C[i - k] > O[i - k]) { lsT = H[i - k]; lsB = Math.Min(O[i - k], C[i - k]); } }
		if (!double.IsNaN(ldB) && c < ldB) ldT = ldB = double.NaN; if (!double.IsNaN(lsT) && c > lsT) lsT = lsB = double.NaN;
		for (int k = 0; k < 17; k++) E[k] = double.NaN;
		double A = Math.Max(atr, mt); // NaN stays NaN
		// 01
		if (!double.IsNaN(emaB) && !double.IsNaN(atr)) E[0] = Vote((emaF - emaS) / A * 0.65 + (c - emaB) / A * 0.25);
		// 02
		double chUp = double.NaN, chLo = double.NaN; if (i >= p.ChannelLen) { chUp = Mx(H, i - p.ChannelLen, i - 1); chLo = Mn(L, i - p.ChannelLen, i - 1); } // shifted by one
		ChU.Add(chUp); double chUprev = i >= 1 ? ChU[i - 1] : double.NaN; double chLoPrev = double.NaN; if (i >= p.ChannelLen + 1) chLoPrev = Mn(L, i - 1 - p.ChannelLen, i - 2);
		bool hb = !double.IsNaN(chUp) && c > chUp, lb = !double.IsNaN(chLo) && c < chLo;
		double roc7 = i >= 7 ? 100 * (c - C[i - 7]) / C[i - 7] : double.NaN;
		if (!double.IsNaN(atr) && !double.IsNaN(roc7)) E[1] = Vote((hb ? 0.60 : lb ? -0.60 : 0.0) + (roc7 / Math.Max(100.0 * atr / Math.Max(Math.Abs(c), mt), 0.1)) * 0.12);
		// 03 supertrend
		double hl2 = (h + l) / 2; double atrSt = rAtrSt.Update(tr1); double sd = double.NaN;
		if (!double.IsNaN(atrSt))
		{
			double u = hl2 + 2.7 * atrSt, lo = hl2 - 2.7 * atrSt; double pl2 = Nz(stLB, 0.0), pu = Nz(stUB, 0.0); double pc = i > 0 ? prevClose : double.NaN;
			lo = (lo > pl2 || pc < pl2) ? lo : pl2; u = (u < pu || pc > pu) ? u : pu; stUB = u; stLB = lo;
			if (i == 0 || double.IsNaN(stAtrPrev)) sd = 1; else if (stST == pu) sd = c > u ? -1 : 1; else sd = c < lo ? 1 : -1;
			stST = sd == -1 ? lo : u;
		}
		stAtrPrev = atrSt; Sdir.Add(sd); double sdPrev = i > 0 ? Sdir[i - 1] : double.NaN;
		if (!double.IsNaN(adx) && !double.IsNaN(sd)) E[2] = Vote((sd < 0 ? 1.0 : -1.0) * Clamp(adx / 27.0, 0.2, 1.0));
		// 04
		double candleP = (c - o) / rng; double pressure = ePress.Update(candleP);
		if (hasVol && !double.IsNaN(pressure)) E[3] = Vote(Nz(flowProxy, 0.0) / 55.0 * 0.6 + pressure * 0.4);
		// 05
		double kd = Math.Abs(c - BackNz(C, 10, c)); double kn = sDc10.Update(dc) * 10.0; double er = (!double.IsNaN(kn) && kn > 0) ? kd / kn : 0.0; double sc = Math.Pow(er * (2.0 / 3 - 2.0 / 31) + 2.0 / 31, 2);
		kamaPrev = double.IsNaN(kamaPrev) ? c : kamaPrev + sc * (c - kamaPrev); KamaL.Add(kamaPrev); double kama = kamaPrev;
		if (!double.IsNaN(atr)) { double k3 = i >= 3 ? KamaL[i - 3] : kama; E[4] = Vote((c - kama) / A * 0.35 + (kama - Nz(k3, kama)) / A * 0.8); }
		// 06
		if (!double.IsNaN(pdH)) { double mid = (pdH + pdL) / 2; E[5] = Vote((c > pdH ? 0.85 : c < pdL ? -0.85 : (c - mid) / Math.Max(pdH - pdL, mt)) * 0.95); }
		// 07
		{ double si = ms == 1 ? 0.48 : ms == -1 ? -0.48 : 0.0; double li = BullFVG ? 0.35 : BearFVG ? -0.35 : SweepL ? 0.30 : SweepH ? -0.30 : 0.0; E[6] = Vote(si + li); }
		// 08 entropy
		int ew = p.EntropyWin; double entropy = double.NaN, upP = 0, dnP = 0;
		if (i > ew)
		{
			double su = 0, sf = 0, sdn = 0; for (int q = 0; q < ew; q++) { double mv = C[i - q] - C[i - q - 1]; double bd = Math.Max(Nz(Atr[i - q], mt), mt) * 0.16; if (mv > bd) su++; else if (mv < -bd) sdn++; else sf++; }
			double u2 = su / ew, f2 = sf / ew, d2 = sdn / ew; double sh = -(u2 > 0 ? u2 * Math.Log(u2) : 0) - (f2 > 0 ? f2 * Math.Log(f2) : 0) - (d2 > 0 ? d2 * Math.Log(d2) : 0); entropy = Clamp(sh / Math.Log(3), 0, 1); upP = u2; dnP = d2;
		}
		EntropyV = entropy; Ent.Add(entropy); E[7] = Vote((upP - dnP) * (0.42 + (1.0 - Nz(entropy, 1.0))));
		// 09
		double c2 = BackNz(C, 2, c), c4 = BackNz(C, 4, c); double ws = (c - c2) / A, wl = (c2 - c4) / A; double spec = eSpec.Update(ws - wl);
		double reg = double.NaN; if (i >= 29) { double sx = 0, sy = 0, sxy = 0, sxx = 0; int n = 30; for (int k = 0; k < n; k++) { double yv = C[i - n + 1 + k]; sx += k; sy += yv; sxy += k * yv; sxx += (double)k * k; } double mx = sx / n, my = sy / n; double slope = (sxy - n * mx * my) / (sxx - n * mx * mx); double ic = my - slope * mx; reg = ic + slope * (n - 1); }
		Reg.Add(reg); double reg4 = BackNz(Reg, 4, reg); double rslope = (reg - reg4) / A; if (!double.IsNaN(spec) && !double.IsNaN(rslope)) E[8] = Vote(0.45 * spec + 0.55 * rslope);
		// 10
		double force = eForce.Update(candleP * (Clamp(Nz(relVol, 0.0), 0, 2.5) / 1.4)); E[9] = VoteN(force);
		// 11
		bool be = i > 0 && c > o && prevClose < O[i - 1] && c >= O[i - 1] && o <= prevClose; bool se = i > 0 && c < o && prevClose > O[i - 1] && c <= O[i - 1] && o >= prevClose;
		double ema34 = e34.Update(c); if (!double.IsNaN(ema34) && !double.IsNaN(atr)) E[10] = Vote((c - ema34) / A * 0.52 + (be ? 0.42 : se ? -0.42 : 0.0));
		// 12
		if (!double.IsNaN(atr)) { double disp = Math.Abs(c - o) / A; bool dtch = !double.IsNaN(ldT) && l <= ldT + atr * 0.5 && c > ldB; bool stch = !double.IsNaN(lsB) && h >= lsB - atr * 0.5 && c < lsT; E[11] = Vote((dtch ? 0.55 : stch ? -0.55 : 0.0) + (bb ? 0.45 : sb ? -0.45 : 0.0) * Clamp(disp, 0.25, 1.4)); }
		// 13
		if (!double.IsNaN(adx) && !double.IsNaN(sd) && !double.IsNaN(hist) && !double.IsNaN(emaS)) { double ts = emaF > emaS ? 1.0 : -1.0; E[12] = Vote((ts + (sd < 0 ? 1.0 : -1.0) + (hist > 0 ? 1.0 : -1.0)) / 3.0 * Clamp(adx / 23.0, 0.35, 1.0)); }
		// 14 VWMA(34)
		CxV.Add(c * v); double vw = double.NaN; if (i >= 33) { double a1 = 0, a2 = 0; for (int k = i - 33; k <= i; k++) { a1 += CxV[k]; a2 += V[k]; } vw = a2 > 0 ? a1 / a2 : double.NaN; }
		if (hasVol && !double.IsNaN(vw) && !double.IsNaN(atr)) E[13] = Vote((c - vw) / A * 0.58);
		// 15 MFI
		double chg = i == 0 ? double.NaN : hlc3 - Hlc3[i - 1]; UpV.Add(double.IsNaN(chg) ? double.NaN : v * (chg <= 0 ? 0.0 : hlc3)); DnV.Add(double.IsNaN(chg) ? double.NaN : v * (chg >= 0 ? 0.0 : hlc3));
		double mfi = double.NaN; MfiV = double.NaN; if (i >= 14) { double us = 0, ds = 0; for (int k = i - 13; k <= i; k++) { us += UpV[k]; ds += DnV[k]; } mfi = ds == 0 ? 100.0 : 100 - 100 / (1 + us / ds); }
		MfiV = mfi; if (hasVol && !double.IsNaN(mfi)) E[14] = Vote(((mfi - 50.0) / 50.0) * 0.8 + Clamp(Nz(relVol) - 1.0, -1.0, 1.0) * candleP * 0.2);
		// 16 HTF
		{ double s2 = 0, cn = 0; if (!double.IsNaN(hc1) && !double.IsNaN(he1)) { s2 += hc1 > he1 ? 1.0 : hc1 < he1 ? -1.0 : 0.0; cn++; } if (!double.IsNaN(hc2) && !double.IsNaN(he2)) { s2 += hc2 > he2 ? 1.0 : hc2 < he2 ? -1.0 : 0.0; cn++; } if (cn > 0) E[15] = Vote(s2 / cn); }
		// 17
		if (!double.IsNaN(rsi) && !double.IsNaN(emaS) && i >= 3) { double vv = (intradayVwap && !double.IsNaN(vwap)) ? (c > vwap ? 1.0 : -1.0) : (c > emaB ? 1.0 : -1.0); E[16] = Vote((vv + (rsi > 50 ? 1.0 : -1.0) + (emaS > EmaSL[i - 3] ? 1.0 : -1.0)) / 3.0); }
		// fusion
		double bp = 0, spp = 0, pp = 0, wi = 0; BullBr = BearBr = 0; Modules = 0; for (int q = 0; q < 17; q++) if (!double.IsNaN(E[q])) Modules++; double volExp = Clamp(Nz(VolPct, 100.0) / 100.0, 0.0, 3.0);
		for (int k = 0; k < 17; k++)
		{
			double vk = E[k]; EW[k] = double.NaN; if (double.IsNaN(vk)) continue; double w = W[k];
			if (p.Adaptive) { if (Choppy && (k == 0 || k == 1 || k == 2 || k == 8 || k == 12)) w *= 0.72; if (Trending && (k == 6 || k == 11)) w *= 1.14; if (Nz(entropy, 1.0) > 0.92 && (k == 1 || k == 8)) w *= 0.79; if (volExp > 1.55 && (k == 3 || k == 9)) w *= 0.85; }
			EW[k] = w; wi += vk * w; bp += Clamp((vk + 0.12) / 1.12, 0, 1) * w; spp += Clamp((-vk + 0.12) / 1.12, 0, 1) * w; pp += w; if (vk > 0.24) BullBr++; if (vk < -0.24) BearBr++;
		}
		Bull = pp > 0 ? Clamp(100 * bp / pp, 0, 100) : 0; Bear = pp > 0 ? Clamp(100 * spp / pp, 0, 100) : 0; Edge = Bull - Bear; Impulse = pp > 0 ? wi / pp : 0.0;
		// ---- extra visuals / radar features ----
		HistL.Add(hist); double AA = Math.Max(atr, mt);
		SUp = SLo = double.NaN; if (!double.IsNaN(atr)) { double ec = 1.0 - Nz(entropy, 1.0); SUp = kama + atr * (0.55 + ec * 0.70); SLo = kama - atr * (0.55 + ec * 0.70); }
		RibM = rMean.Update(hlc3); RibS = double.NaN; Rg34 = double.NaN; double rg1 = double.NaN;
		if (i >= 33) { double sm = 0; for (int k = i - 33; k <= i; k++) sm += Hlc3[k]; sm /= 34; double sq = 0; for (int k = i - 33; k <= i; k++) sq += (Hlc3[k] - sm) * (Hlc3[k] - sm); RibS = Math.Sqrt(sq / 34);
			double sx = 0, sy = 0, sxy = 0, sxx = 0; int n34 = 34; for (int k = 0; k < n34; k++) { double yv = C[i - n34 + 1 + k]; sx += k; sy += yv; sxy += k * yv; sxx += (double)k * k; } double mx = sx / n34, my = sy / n34; double slp = (sxy - n34 * mx * my) / (sxx - n34 * mx * mx); double icp = my - slp * mx; Rg34 = icp + slp * (n34 - 1); rg1 = icp + slp * (n34 - 2); }
		double ribZ = (!double.IsNaN(RibS) && RibS > 0 && !double.IsNaN(RibM)) ? Clamp((c - RibM) / RibS, -4, 4) : 0.0;
		double cl = ((c - l) / Math.Max(h - l, mt) - 0.5) * 2.0, wick = (Math.Min(o, c) - l - (h - Math.Max(o, c))) / Math.Max(h - l, mt);
		double msd = ((c - BackNz(C, 3, c)) + (c - BackNz(C, 8, c)) * 0.55 + (c - BackNz(C, 21, c)) * 0.28) / AA;
		double noiseSig = 0; if (i >= 19) { double sm2 = 0; for (int k = i - 17; k <= i; k++) sm2 += C[k] - C[k - 1]; sm2 /= 18; double sq2 = 0; for (int k = i - 17; k <= i; k++) { double dd = (C[k] - C[k - 1]) - sm2; sq2 += dd * dd; } double rn = Math.Sqrt(sq2 / 18); noiseSig = rn > 0 ? (c - BackNz(C, 18, c)) / rn / Math.Sqrt(18) : 0.0; }
		double chPos = double.NaN; if (i >= 54) { double h55 = Mx(H, i - 54, i), l55 = Mn(L, i - 54, i); chPos = Clamp((c - (h55 + l55) * 0.5) / Math.Max(h55 - l55, mt) * 2.0, -1.0, 1.0); }
		double h1b = i >= 1 ? HistL[i - 1] : hist, h2b = i >= 2 ? HistL[i - 2] : hist; double curv = hist - 2.0 * Nz(h1b, hist) + Nz(h2b, hist);
		double volImb = Vote(cl * Clamp(Nz(relVol, 1.0), 0, 3) / 2.0), liqSw = Vote((SweepL ? 1.0 : SweepH ? -1.0 : 0.0) + wick * 0.25);
		double trendPers = Vote(msd * 0.08 + (double.IsNaN(Rg34) ? double.NaN : (Rg34 - rg1) / AA * 1.7)), dirCnd = Vote((cl + candleP + wick) / 3.0);
		double structBal = Vote((ms == 1 ? 0.48 : ms == -1 ? -0.48 : 0.0) + liqSw * 0.35), intra = Vote((cl + volImb) / 2.0), valAcc = (hasVol && !double.IsNaN(vw)) ? Vote((c - vw) / AA) : 0.0;
		double meanRev = Vote(-ribZ * (Choppy ? 0.5 : 0.12)), disc = Vote(Nz(chPos, 0.0) * (Trending ? 1.0 : 0.38)), cyc = Vote(spec * 0.26 + curv / AA), brk = Vote((hb ? 0.8 : lb ? -0.8 : 0.0) + Nz(chPos, 0.0) * 0.2), stat = Vote(noiseSig / 2.5);
		double exh = Vote((!double.IsNaN(rsi) && rsi > 74 ? -0.45 : (!double.IsNaN(rsi) && rsi < 26 ? 0.45 : 0.0)) + wick * 0.4);
		Radar[0] = (E[0] + E[2] + trendPers) / 3.0; Radar[1] = (E[8] + E[9] + dirCnd) / 3.0; Radar[2] = (E[6] + structBal + liqSw) / 3.0; Radar[3] = (Nz(E[3]) + Nz(E[13]) + Nz(E[14]) + valAcc + intra) / 5.0;
		Radar[4] = (E[7] + E[12] + meanRev + disc) / 4.0; Radar[5] = (Nz(E[15]) + Nz(E[5])) * 0.5; Radar[6] = (E[1] + exh + brk) / 3.0; Radar[7] = (E[16] + E[4] + cyc + stat) / 4.0;
		// signals
		Long = Short = false;
		if (i >= 1 && !double.IsNaN(atr) && !double.IsNaN(adx) && !double.IsNaN(kama) && i > Math.Max(p.Bias, ew))
		{
			bool cooled = LastSig == int.MinValue || i - LastSig > p.Cooldown; bool chopVeto = p.DisallowChop && Choppy && Nz(entropy, 1.0) > 0.89;
			bool tL = !p.RequireTrend || (emaF > emaS && c >= emaB), tS = !p.RequireTrend || (emaF < emaS && c <= emaB);
			
			bool crossUp = c > emaF && prevClose <= EmaFPrev, crossDn = c < emaF && prevClose >= EmaFPrev; bool eUp = emaF > emaS && EmaFPrev <= EmaSPrev, eDn = emaF < emaS && EmaFPrev >= EmaSPrev;
			bool scL = !double.IsNaN(sdPrev) && sd < 0 && sdPrev >= 0, scS = !double.IsNaN(sdPrev) && sd > 0 && sdPrev <= 0;
			double pU = Nz(chUprev, chUp), pD = Nz(chLoPrev, chLo);
			bool lt = crossUp || eUp || bb || BullFVG || scL || (be && c > kama) || (hb && prevClose <= pU); bool stt = crossDn || eDn || sb || BearFVG || scS || (se && c < kama) || (lb && prevClose >= pD);
			bool gL = !p.QualityGate || BullBr >= p.MinBreadth, gS = !p.QualityGate || BearBr >= p.MinBreadth;
			Long = cooled && !chopVeto && lt && tL && Bull >= p.MinScore && Edge >= p.MinEdge && gL; Short = cooled && !chopVeto && stt && tS && Bear >= p.MinScore && Edge <= -p.MinEdge && gS;
			if (Long || Short) LastSig = i;
		}
		EmaFPrev = emaF; EmaSPrev = emaS; prevClose = c;
	}
	public double EmaFPrev = double.NaN, EmaSPrev = double.NaN; readonly List<double> EmaFL = new List<double>();
}

public enum F17Pos { TopRight, TopLeft, BottomRight, BottomLeft }
internal class F17Snap
{
	public double[] E = new double[17], EW = new double[17]; public double Bull, Bear, Edge, Adx, Atr, Ent, Eff, RelVol, Mfi, Entry, Stop, T1, T2; public string Regime = "", Ref = "NO REFERENCE", Last = "LAST: NONE"; public bool ChopVeto, SweepH, SweepL, BullFVG, BearFVG, NewLong, NewShort, Live; public int MS, BullBr, BearBr, Modules, H1, H2; public double Vwma, Poc, Vah, Val, ProfLow, BinSize, Close, SwH, SwL, PdH, PdL; public int PocIdx, VaLo, VaHi, LastIdx; public double[] Vp, Radar; public double[][] BG, BR, DZ, SZ;
}
public class SneakyFusion17 : Indicator
{
	F17Core core; F17P pr; List<double[]> h1 = new List<double[]>(), h2 = new List<double[]>(); F17Ema x1, x2; double eN = double.NaN, sN = double.NaN, t1N = double.NaN, t2N = double.NaN; int lastSide, refBar = -1, refCode; string refState = "NO REFERENCE"; bool errShown; volatile F17Snap snapObj; bool dReady;
	static SolidColorBrush Rgb(byte r, byte g, byte b, byte a) { var br = new SolidColorBrush(Color.FromArgb(a, r, g, b)); br.Freeze(); return br; }
	static readonly Brush Ink = Rgb(8, 13, 27, 255), White = Rgb(230, 244, 251, 255), Cyan = Rgb(35, 222, 234, 255), Green = Rgb(26, 231, 160, 255), Red = Rgb(255, 79, 122, 255), Gold = Rgb(253, 193, 85, 255), Violet = Rgb(147, 114, 255, 255), Amber = Rgb(255, 146, 67, 255);
	static readonly Brush CyanP = Rgb(35, 222, 234, 242), VioletP = Rgb(147, 114, 255, 242), GoldP = Rgb(253, 193, 85, 204), VwapP = Rgb(230, 244, 251, 168), PdP = Rgb(253, 193, 85, 110), GreenL = Rgb(26, 231, 160, 242), RedL = Rgb(255, 79, 122, 242);
	static readonly Brush SpecU = Rgb(60, 128, 253, 66), SpecL = Rgb(147, 114, 255, 66), RailP = Rgb(253, 193, 85, 199), SpecU2 = Rgb(60, 128, 253, 46), SpecL2 = Rgb(147, 114, 255, 46), RegP = Rgb(253, 193, 85, 135);
	static readonly Brush BarGreen = Rgb(26, 231, 160, 191), BarRed = Rgb(255, 79, 122, 191), BarGold = Rgb(253, 193, 85, 191);
	protected override void OnStateChange()
	{
		if (State == State.SetDefaults)
		{
			Description = "Port of the DrFX Fusion17 adaptive-consensus signal logic (17 engine votes). UNPROVEN: honest backtests show no robust edge. Hidden plots: 0 Signal (+1 long, -1 short, 0 none), 1 Entry, 2 Stop, 3 TP1, 4 TP2, 5 BullScore, 6 BearScore.";
			Name = "SneakyFusion17"; Calculate = Calculate.OnBarClose; IsOverlay = true; DisplayInDataBox = true; PaintPriceMarkers = false; IsSuspendedWhileInactive = true;
			Fast = 21; Slow = 55; Bias = 200; Pivot = 5; MinScore = 62; MinEdge = 20; Cooldown = 8; RegLen = 24; EntropyWin = 28; ChannelLen = 21; AtrStop = 1.8; Rr1 = 1.5; Rr2 = 3.0; FvgAtr = 0.22;
			RequireTrend = true; DisallowChop = true; Adaptive = true; QualityGate = false; MinBreadth = 7; ShowDash = true; DashPos = F17Pos.TopRight; DashFont = 9; ShowLabels = true; ShowLevels = true; ShowLines = true; DrawBarsBack = 2000;
			ShowStructure = true; ShowBoxes = true; ShowSweeps = true; Projection = 22; ColorEma = true; ShowCloud = true; ShowRibbon = true; ShowResearch = true; ShowZones = true; ShowFvg = true; ShowProfile = true; ShowRadar = true; ShowLadder = true; ColorBars = true; ShowPrevDay = true; ShowTrace = true;
			AddPlot(Brushes.Transparent, "Signal"); AddPlot(Brushes.Transparent, "Entry"); AddPlot(Brushes.Transparent, "Stop"); AddPlot(Brushes.Transparent, "TP1"); AddPlot(Brushes.Transparent, "TP2"); AddPlot(Brushes.Transparent, "BullScore"); AddPlot(Brushes.Transparent, "BearScore");
			AddPlot(new Stroke(CyanP, 2), PlotStyle.Line, "EMA Fast"); AddPlot(new Stroke(VioletP, 2), PlotStyle.Line, "EMA Slow"); AddPlot(new Stroke(GoldP, 2), PlotStyle.Line, "EMA Bias"); AddPlot(new Stroke(VwapP, 1), PlotStyle.Line, "VWAP");
			AddPlot(new Stroke(PdP, 1), PlotStyle.Line, "Prev Day High"); AddPlot(new Stroke(PdP, 1), PlotStyle.Line, "Prev Day Low");
			AddPlot(new Stroke(SpecU, 1), PlotStyle.Line, "Spectral Upper"); AddPlot(new Stroke(SpecL, 1), PlotStyle.Line, "Spectral Lower"); AddPlot(new Stroke(RailP, 2), PlotStyle.Line, "Adaptive Rail");
			AddPlot(new Stroke(SpecU2, 1), PlotStyle.Line, "Research Upper"); AddPlot(new Stroke(SpecL2, 1), PlotStyle.Line, "Research Lower"); AddPlot(new Stroke(RegP, 1), PlotStyle.Line, "Regression"); AddPlot(Brushes.Transparent, "FusionImpulse");
		}
		else if (State == State.Configure) { AddDataSeries(BarsPeriodType.Minute, 60); AddDataSeries(BarsPeriodType.Minute, 240); }
		else if (State == State.DataLoaded)
		{
			pr = new F17P { Fast = Fast, Slow = Slow, Bias = Bias, Pivot = Pivot, MinScore = MinScore, MinEdge = MinEdge, Cooldown = Cooldown, RegLen = RegLen, EntropyWin = EntropyWin, ChannelLen = ChannelLen, MinBreadth = MinBreadth, RequireTrend = RequireTrend, DisallowChop = DisallowChop, Adaptive = Adaptive, QualityGate = QualityGate, FvgAtr = FvgAtr, AtrStop = AtrStop, Rr1 = Rr1, Rr2 = Rr2 };
			core = new F17Core(pr); F17Core.MinTick = TickSize; x1 = new F17Ema(50); x2 = new F17Ema(50); h1.Clear(); h2.Clear(); eN = sN = t1N = t2N = double.NaN; lastSide = 0; refBar = -1; refCode = 0; refState = "NO REFERENCE";
		}
	}
	static double Lookup(List<double[]> l, DateTime open, int what)
	{
		for (int i = l.Count - 1; i >= 0; i--) if (l[i][0] <= open.Ticks) return l[i][what]; return double.NaN;
	}
	void Mark(string tag, string text, double y, int px, Brush b)
	{
		Draw.Text(this, tag, false, text, 0, y, px, b, new SimpleFont("Arial", 9), System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
	}
	void Box(string tag, string text, double y, int px, Brush fg, Brush bg, int fs)
	{
		Draw.Text(this, tag, false, text, 0, y, px, fg, new SimpleFont("Arial", fs) { Bold = true }, System.Windows.TextAlignment.Center, bg, bg, 95);
	}
	protected override void OnBarUpdate()
	{
		if (core == null) return;
		if (BarsInProgress == 1) { double e = x1.Update(Closes[1][0]); h1.Add(new[] { (double)Times[1][0].Ticks, Closes[1][0], e }); return; }
		if (BarsInProgress == 2) { double e = x2.Update(Closes[2][0]); h2.Add(new[] { (double)Times[2][0].Ticks, Closes[2][0], e }); return; }
		if (BarsInProgress != 0) return;
		DateTime open = Time[0].AddMinutes(-(BarsPeriod.BarsPeriodType == BarsPeriodType.Minute ? BarsPeriod.Value : 5));
		double hc1 = Lookup(h1, open, 1), he1 = Lookup(h1, open, 2), hc2 = Lookup(h2, open, 1), he2 = Lookup(h2, open, 2);
		bool newSess = Bars.IsFirstBarOfSession;
		core.Step(Open[0], High[0], Low[0], Close[0], Volume[0], newSess, hc1, he1, hc2, he2, true);
		double sig = core.Long ? 1 : core.Short ? -1 : 0; Values[0][0] = sig; double atr = core.AtrV;
		bool inWin = CurrentBar > Count - DrawBarsBack - 2;
		// reference lifecycle (observational, same as the original)
		if (refBar >= 0 && CurrentBar > refBar && refCode == 0 && !double.IsNaN(sN) && !double.IsNaN(t1N))
		{
			bool st = lastSide == 1 ? Low[0] <= sN : High[0] >= sN, tp = lastSide == 1 ? High[0] >= t1N : Low[0] <= t1N;
			if (st && tp) { refCode = 3; refState = "AMBIGUOUS PATH"; } else if (st) { refCode = 2; refState = "STOP REFERENCE TOUCHED"; } else if (tp) { refCode = 1; refState = "TP1 REFERENCE TOUCHED"; }
		}
		if (sig != 0 && !double.IsNaN(atr))
		{
			double d = sig * atr * AtrStop; eN = Close[0]; sN = Close[0] - d; t1N = Close[0] + d * Rr1; t2N = Close[0] + d * Rr2; lastSide = (int)sig; refBar = CurrentBar; refCode = 0; refState = "ACTIVE REFERENCE";
			if (ShowLabels && inWin)
			{
				string ch = sig > 0 ? (core.BullChoch ? "  ·  CHoCH" : "") : (core.BearChoch ? "  ·  CHoCH" : "");
				if (sig > 0) { Box("F17sm" + CurrentBar, "▲ LONG", Low[0], 16, Ink, Green, 9); Box("F17sg" + CurrentBar, "GOD X  |  LONG\n" + Math.Round(core.Bull) + "/100 rule score" + ch, Low[0] - atr * 0.75, 30, Ink, Green, 9); }
				else { Box("F17sm" + CurrentBar, "▼ SHORT", High[0], -16, White, Red, 9); Box("F17sg" + CurrentBar, "GOD X  |  SHORT\n" + Math.Round(core.Bear) + "/100 rule score" + ch, High[0] + atr * 0.75, -30, White, Red, 9); }
			}
		}
		if (inWin)
		{
			if (ShowSweeps && core.SweepL) Mark("F17w" + CurrentBar, "◆\nSWP", Low[0], 22, Cyan);
			else if (ShowSweeps && core.SweepH) Mark("F17w" + CurrentBar, "SWP\n◆", High[0], -22, Violet);
			if (ShowStructure)
			{
				if (core.BullChoch) Mark("F17c" + CurrentBar, "▲\nCHoCH", Low[0], 22, Green);
				else if (core.BearChoch) Mark("F17c" + CurrentBar, "CHoCH\n▼", High[0], -22, Red);
				else if (core.BullBreak) Mark("F17b" + CurrentBar, "●\nBOS", Low[0], 22, Cyan);
				else if (core.BearBreak) Mark("F17b" + CurrentBar, "BOS\n●", High[0], -22, Gold);
			}
			if (ShowTrace && sig != 0) Mark("F17t" + CurrentBar, sig > 0 ? "F17" : "F17", sig > 0 ? Low[0] : High[0], sig > 0 ? 70 : -70, sig > 0 ? Green : Red);
		}
		if (ShowBoxes && sig != 0 && inWin && !double.IsNaN(sN))
		{
			int pj = -Projection; Brush rb = Rgb(255, 79, 122, 90), gb = Rgb(26, 231, 160, 90);
			Draw.Rectangle(this, "F17risk", false, 0, Math.Max(eN, sN), pj, Math.Min(eN, sN), rb, rb, 9);
			Draw.Rectangle(this, "F17rew", false, 0, Math.Max(eN, t2N), pj, Math.Min(eN, t2N), gb, gb, 7);
			Draw.Line(this, "F17e", false, 0, eN, pj, eN, White, DashStyleHelper.Solid, 2);
			Draw.Line(this, "F17s", false, 0, sN, pj, sN, Red, DashStyleHelper.Dash, 2);
			Draw.Line(this, "F17t1", false, 0, t1N, pj, t1N, Green, DashStyleHelper.Dot, 2);
			Draw.Line(this, "F17t2", false, 0, t2N, pj, t2N, Cyan, DashStyleHelper.Dash, 2);
			Draw.Text(this, "F17lE", "ENTRY " + eN.ToString("0.00"), pj, eN, White);
			Draw.Text(this, "F17lS", "SL " + sN.ToString("0.00") + " (-1.0R)", pj, sN, Red);
			Draw.Text(this, "F17l1", "TP1 " + t1N.ToString("0.00") + " (" + Rr1.ToString("0.#") + "R)", pj, t1N, Green);
			Draw.Text(this, "F17l2", "TP2 " + t2N.ToString("0.00") + " (" + Rr2.ToString("0.#") + "R)", pj, t2N, Cyan);
		}
		if (ColorBars) { Brush bc = core.Edge > 18 ? BarGreen : core.Edge < -18 ? BarRed : BarGold; BarBrush = bc; CandleOutlineBrush = bc; }
		Values[1][0] = eN; Values[2][0] = sN; Values[3][0] = t1N; Values[4][0] = t2N; Values[5][0] = core.Bull; Values[6][0] = core.Bear;
		if (ShowLines) { Values[7][0] = core.EmaF; Values[8][0] = core.EmaS; Values[9][0] = core.EmaB; if (newSess) Values[10].Reset(); else Values[10][0] = core.Vwap; }
		if (ShowPrevDay && !double.IsNaN(core.PdH) && !newSess) { Values[11][0] = core.PdH; Values[12][0] = core.PdL; }
		if (ShowRibbon) { Values[13][0] = core.SUp; Values[14][0] = core.SLo; Values[15][0] = core.Kama; }
		if (ShowResearch && !double.IsNaN(core.RibS)) { Values[16][0] = core.RibM + 1.65 * core.RibS; Values[17][0] = core.RibM - 1.65 * core.RibS; Values[18][0] = core.Rg34; }
		Values[19][0] = core.Impulse;
		if (State == State.Realtime || CurrentBar >= Count - 3)
		{
			var ns = new F17Snap();
			Array.Copy(core.E, ns.E, 17); Array.Copy(core.EW, ns.EW, 17); ns.Bull = core.Bull; ns.Bear = core.Bear; ns.Edge = core.Edge; ns.Adx = core.Adx; ns.Atr = core.AtrV; ns.Ent = core.EntropyV; ns.Eff = core.EffV; ns.RelVol = core.RelVolV; ns.Mfi = core.MfiV;
			ns.Regime = (core.Choppy && DisallowChop && !double.IsNaN(core.EntropyV) && core.EntropyV > 0.89) ? "CHOP FILTER" : core.Trending ? "DIRECTIONAL" : core.Choppy ? "RANGE / CHOP" : "TRANSITION"; ns.ChopVeto = ns.Regime == "CHOP FILTER";
			ns.SweepH = core.SweepH; ns.SweepL = core.SweepL; ns.BullFVG = core.BullFVG; ns.BearFVG = core.BearFVG; ns.NewLong = core.Long; ns.NewShort = core.Short; ns.MS = core.MS; ns.BullBr = core.BullBr; ns.BearBr = core.BearBr; ns.Modules = core.Modules;
			ns.H1 = double.IsNaN(hc1) || double.IsNaN(he1) ? 0 : hc1 > he1 ? 1 : -1; ns.H2 = double.IsNaN(hc2) || double.IsNaN(he2) ? 0 : hc2 > he2 ? 1 : -1;
			ns.Entry = eN; ns.Stop = sN; ns.T1 = t1N; ns.T2 = t2N; ns.Ref = refState; ns.Last = lastSide == 1 ? "LAST: LONG" : lastSide == -1 ? "LAST: SHORT" : "LAST: NONE"; ns.Live = State == State.Realtime;
			ns.LastIdx = CurrentBar; ns.Close = Close[0]; ns.SwH = core.LastSH2; ns.SwL = core.LastSL2; ns.PdH = core.PdH; ns.PdL = core.PdL; ns.Radar = (double[])core.Radar.Clone();
			Func<List<F17Zone>, double[][]> cz = l => { var a = new double[l.Count][]; for (int q = 0; q < l.Count; q++) a[q] = new[] { (double)l[q].Left, l[q].Top, l[q].Bot }; return a; };
			ns.BG = cz(core.BullGaps); ns.BR = cz(core.BearGaps); ns.DZ = cz(core.Demand); ns.SZ = cz(core.Supply);
			const int PL = 110, PB = 28; int nb = Math.Min(PL, CurrentBar + 1); double pH = double.MinValue, pLw = double.MaxValue, vs = 0; for (int q = 0; q < nb; q++) { pH = Math.Max(pH, High[q]); pLw = Math.Min(pLw, Low[q]); vs += Volume[q]; }
			if (nb >= 20 && vs > 0 && pH > pLw)
			{
				double bin = Math.Max((pH - pLw) / PB, TickSize); double[] hv = new double[PB];
				for (int q = 0; q < nb; q++) { double rg = High[q] - Low[q]; if (rg <= TickSize * 0.001) { int ab = (int)Math.Min(PB - 1, Math.Max(0, Math.Floor((Close[q] - pLw) / bin))); hv[ab] += Volume[q]; } else for (int j = 0; j < PB; j++) { double pb = pLw + j * bin; double cov = Math.Max(0.0, Math.Min(High[q], pb + bin) - Math.Max(Low[q], pb)); hv[j] += Volume[q] * cov / rg; } }
				double pk = 0; int pi = 0; for (int j = 0; j < PB; j++) if (hv[j] > pk) { pk = hv[j]; pi = j; }
				if (pk > 0)
				{
					double tot = 0; for (int j = 0; j < PB; j++) tot += hv[j]; double target = tot * 0.70, cap = pk; int vl = pi, vh = pi;
					for (int j = 0; j < PB; j++) if (cap < target) { double dn = vl > 0 ? hv[vl - 1] : -1.0, upv = vh < PB - 1 ? hv[vh + 1] : -1.0; if (upv >= dn && upv >= 0) { vh++; cap += upv; } else if (dn >= 0) { vl--; cap += dn; } }
					ns.Vp = hv; ns.ProfLow = pLw; ns.BinSize = bin; ns.PocIdx = pi; ns.VaLo = vl; ns.VaHi = vh; ns.Poc = pLw + (pi + 0.5) * bin; ns.Vah = pLw + (vh + 1) * bin; ns.Val = pLw + vl * bin;
				}
			}
			snapObj = ns; dReady = true;
		}
	}
	static string Px(double v) { return double.IsNaN(v) ? "-" : v.ToString("0.00"); }
	protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
	{
		base.OnRender(chartControl, chartScale);
		if (RenderTarget == null || ChartPanel == null || core == null) return;
		var disp = new List<IDisposable>();
		try
		{
			if (ShowCloud && ChartBars != null)
			{
				var fa = Values[7]; var sa = Values[8]; int from = Math.Max(ChartBars.FromIndex, 1), to = ChartBars.ToIndex;
				for (int i = from; i <= to; i++)
				{
					if (!fa.IsValidDataPointAt(i) || !fa.IsValidDataPointAt(i - 1) || !sa.IsValidDataPointAt(i) || !sa.IsValidDataPointAt(i - 1)) continue;
					float x0 = chartControl.GetXByBarIndex(ChartBars, i - 1), x1p = chartControl.GetXByBarIndex(ChartBars, i);
					float yf0 = chartScale.GetYByValue(fa.GetValueAt(i - 1)), yf1 = chartScale.GetYByValue(fa.GetValueAt(i)), ys0 = chartScale.GetYByValue(sa.GetValueAt(i - 1)), ys1 = chartScale.GetYByValue(sa.GetValueAt(i));
					bool up = fa.GetValueAt(i) >= sa.GetValueAt(i);
					var br = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, up ? new SharpDX.Color4(26 / 255f, 231 / 255f, 160 / 255f, 0.13f) : new SharpDX.Color4(1f, 79 / 255f, 122 / 255f, 0.13f)); disp.Add(br);
					var geo = new SharpDX.Direct2D1.PathGeometry(NinjaTrader.Core.Globals.D2DFactory); disp.Add(geo);
					var sink = geo.Open(); sink.BeginFigure(new SharpDX.Vector2(x0, yf0), SharpDX.Direct2D1.FigureBegin.Filled); sink.AddLine(new SharpDX.Vector2(x1p, yf1)); sink.AddLine(new SharpDX.Vector2(x1p, ys1)); sink.AddLine(new SharpDX.Vector2(x0, ys0)); sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed); sink.Close();
					RenderTarget.FillGeometry(geo, br);
				}
			}
			F17Snap q0 = snapObj;
			if (q0 != null && ChartBars != null)
			{
				float pwid = ChartPanel.W; int tb = ChartBars.ToIndex; float bsz = Math.Max(1f, chartControl.GetXByBarIndex(ChartBars, tb) - chartControl.GetXByBarIndex(ChartBars, tb - 1));
				Func<double, float> Xi = idx => chartControl.GetXByBarIndex(ChartBars, tb) + (float)(idx - tb) * bsz;
				Func<byte, byte, byte, float, SharpDX.Direct2D1.SolidColorBrush> Bq = (r, g, b, a) => { var br = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(r / 255f, g / 255f, b / 255f, a)); disp.Add(br); return br; };
				Action<double[][], byte, byte, byte, float, float> zones = (arr, r, g, b, fa, ba) => { if (arr == null) return; foreach (var z in arr) { float xa = Xi(z[0]), yt = chartScale.GetYByValue(z[1]), yb = chartScale.GetYByValue(z[2]); if (pwid <= xa) continue; var rc = new SharpDX.RectangleF(Math.Max(0, xa), Math.Min(yt, yb), pwid - Math.Max(0, xa), Math.Max(1f, Math.Abs(yb - yt))); RenderTarget.FillRectangle(rc, Bq(r, g, b, fa)); RenderTarget.DrawRectangle(rc, Bq(r, g, b, ba), 1f); } };
				if (ShowFvg) { zones(q0.BG, 26, 231, 160, 0.14f, 0.52f); zones(q0.BR, 255, 79, 122, 0.14f, 0.52f); }
				if (ShowZones) { zones(q0.DZ, 35, 222, 234, 0.09f, 0.40f); zones(q0.SZ, 147, 114, 255, 0.09f, 0.40f); }
				if (ShowRibbon && ChartBars != null)
				{
					var ua = Values[13]; var la = Values[14]; var im = Values[19]; int f2 = Math.Max(ChartBars.FromIndex, 1);
					for (int i = f2; i <= tb; i++)
					{
						if (!ua.IsValidDataPointAt(i) || !ua.IsValidDataPointAt(i - 1) || !la.IsValidDataPointAt(i) || !la.IsValidDataPointAt(i - 1)) continue;
						float xa2 = chartControl.GetXByBarIndex(ChartBars, i - 1), xb2 = chartControl.GetXByBarIndex(ChartBars, i);
						bool pos = im.GetValueAt(i) >= 0; var br2 = Bq(pos ? (byte)35 : (byte)255, pos ? (byte)222 : (byte)79, pos ? (byte)234 : (byte)122, 0.05f);
						var g2 = new SharpDX.Direct2D1.PathGeometry(NinjaTrader.Core.Globals.D2DFactory); disp.Add(g2); var sk = g2.Open();
						sk.BeginFigure(new SharpDX.Vector2(xa2, chartScale.GetYByValue(ua.GetValueAt(i - 1))), SharpDX.Direct2D1.FigureBegin.Filled); sk.AddLine(new SharpDX.Vector2(xb2, chartScale.GetYByValue(ua.GetValueAt(i)))); sk.AddLine(new SharpDX.Vector2(xb2, chartScale.GetYByValue(la.GetValueAt(i)))); sk.AddLine(new SharpDX.Vector2(xa2, chartScale.GetYByValue(la.GetValueAt(i - 1)))); sk.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed); sk.Close();
						RenderTarget.FillGeometry(g2, br2);
					}
				}
				Func<float, SharpDX.DirectWrite.TextFormat> Tf = sz => { var f = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", sz); f.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center; f.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap; disp.Add(f); return f; };
				if (ShowProfile && q0.Vp != null)
				{
					for (int j = 0; j < q0.Vp.Length; j++)
					{
						double pw2 = q0.Vp[j] / q0.Vp[q0.PocIdx]; int bw = (int)Math.Max(1, Math.Round(pw2 * 20)); bool poc = j == q0.PocIdx, va = j >= q0.VaLo && j <= q0.VaHi;
						float xl = Xi(q0.LastIdx + 3), xr = Xi(q0.LastIdx + 3 + bw), yt = chartScale.GetYByValue(q0.ProfLow + (j + 1) * q0.BinSize), yb = chartScale.GetYByValue(q0.ProfLow + j * q0.BinSize);
						var rc = new SharpDX.RectangleF(xl, Math.Min(yt, yb), xr - xl, Math.Max(1f, Math.Abs(yb - yt) - 1f));
						RenderTarget.FillRectangle(rc, poc ? Bq(253, 193, 85, 0.82f) : va ? Bq(35, 222, 234, 0.32f) : Bq(147, 114, 255, 0.32f));
					}
					float yp = chartScale.GetYByValue(q0.Poc); RenderTarget.FillRectangle(new SharpDX.RectangleF(Xi(q0.LastIdx - 10), yp - 1f, Xi(q0.LastIdx + 23) - Xi(q0.LastIdx - 10), 2f), Bq(253, 193, 85, 0.7f));
				}
				if (ShowLadder)
				{
					double[] lp = { q0.SwH, q0.SwL, q0.PdH, q0.PdL, q0.Vah, q0.Val }; string[] lt = { "SWING H", "SWING L", "PREV D H", "PREV D L", "EST VAH", "EST VAL" };
					for (int j = 0; j < 6; j++)
					{
						if (double.IsNaN(lp[j]) || lp[j] == 0) continue; byte[] cc = j < 2 ? new byte[] { 35, 222, 234 } : j < 4 ? new byte[] { 253, 193, 85 } : new byte[] { 147, 114, 255 };
						float yl = chartScale.GetYByValue(lp[j]); float xa3 = Xi(q0.LastIdx - 2), xb3 = Xi(q0.LastIdx + 18);
						RenderTarget.FillRectangle(new SharpDX.RectangleF(xa3, yl - 0.5f, xb3 - xa3, 1f), Bq(cc[0], cc[1], cc[2], 0.48f));
						RenderTarget.DrawText(lt[j] + " | " + lp[j].ToString("0.00"), Tf(8f), new SharpDX.RectangleF(xb3 + 3, yl - 7, 130, 14), Bq(cc[0], cc[1], cc[2], 0.95f));
					}
				}
				if (ShowRadar && q0.Radar != null)
				{
					string[] rn = { "TREND", "MOM", "STRUCT", "VOLUME", "REGIME", "HTF", "FORCE", "ADAPT" }; double cx = q0.LastIdx + 60; float cX = Xi(cx), cY = chartScale.GetYByValue(q0.Close); double prad = Math.Max(q0.Atr * 2.2, TickSize * 15.0); float rxp = 16f * bsz;
					float ry = Math.Abs(chartScale.GetYByValue(q0.Close + prad) - cY);
					for (int j = 0; j < 8; j++)
					{
						int k2 = (j + 1) % 8; double aj = Math.PI * 2.0 * j / 8.0, ak = Math.PI * 2.0 * k2 / 8.0; double vj = double.IsNaN(q0.Radar[j]) ? 0 : q0.Radar[j], vk2 = double.IsNaN(q0.Radar[k2]) ? 0 : q0.Radar[k2]; double sj = Math.Min(1.0, Math.Max(0.02, Math.Abs(vj))), sk2 = Math.Min(1.0, Math.Max(0.02, Math.Abs(vk2)));
						SharpDX.Vector2 pj = new SharpDX.Vector2(cX + rxp * (float)Math.Cos(aj), cY - ry * (float)Math.Sin(aj)), pk2 = new SharpDX.Vector2(cX + rxp * (float)Math.Cos(ak), cY - ry * (float)Math.Sin(ak));
						SharpDX.Vector2 sj2 = new SharpDX.Vector2(cX + rxp * (float)(sj * Math.Cos(aj)), cY - ry * (float)(sj * Math.Sin(aj))), sk3 = new SharpDX.Vector2(cX + rxp * (float)(sk2 * Math.Cos(ak)), cY - ry * (float)(sk2 * Math.Sin(ak)));
						RenderTarget.DrawLine(pj, pk2, Bq(147, 114, 255, 0.42f), 1f); RenderTarget.DrawLine(new SharpDX.Vector2(cX, cY), pj, Bq(60, 80, 100, 0.75f), 1f);
						RenderTarget.DrawLine(sj2, sk3, vj >= 0 ? Bq(35, 222, 234, 1f) : Bq(255, 79, 122, 1f), 2f);
						RenderTarget.DrawText(rn[j] + " " + vj.ToString("0.00;-0.00"), Tf(8f), new SharpDX.RectangleF(cX + 1.25f * rxp * (float)Math.Cos(aj) - 35, cY - 1.22f * ry * (float)Math.Sin(aj) - 7, 80, 14), vj >= 0 ? Bq(26, 231, 160, 1f) : Bq(255, 79, 122, 1f));
					}
					RenderTarget.DrawText("F17 RADAR", Tf(9f), new SharpDX.RectangleF(cX - 35, cY - 7, 70, 14), Bq(253, 193, 85, 1f));
				}
			}
			if (!ShowDash || !dReady) return;
			F17Snap s = snapObj; if (s == null) return;
			float fs = DashFont, rh = fs * 1.55f, W = fs * 47f, pad = 6f; int rows = 34; float H = rows * rh + 6; float pw = ChartPanel.W, ph = ChartPanel.H;
			bool right = DashPos == F17Pos.TopRight || DashPos == F17Pos.BottomRight, top = DashPos == F17Pos.TopRight || DashPos == F17Pos.TopLeft;
			float x0h = right ? pw - W - 16f : 12f, y0 = top ? 40f : ph - H - 44f;
			Func<float, SharpDX.DirectWrite.TextFormat> F = sz => { var f = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", sz); f.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center; f.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap; disp.Add(f); return f; };
			Func<byte, byte, byte, float, SharpDX.Direct2D1.SolidColorBrush> B = (r, g, b, a) => { var br = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(r / 255f, g / 255f, b / 255f, a)); disp.Add(br); return br; };
			byte[] cG = { 26, 231, 160 }, cR = { 255, 79, 122 }, cGo = { 253, 193, 85 }, cC = { 35, 222, 234 }, cW = { 230, 244, 251 }, cM = { 120, 140, 160 }, cV = { 147, 114, 255 }, cA = { 255, 146, 67 };
			Func<double, byte[]> vc = v => v >= 0.24 ? cG : v <= -0.24 ? cR : cGo;
			RenderTarget.FillRectangle(new SharpDX.RectangleF(x0h, y0, W, H), B(8, 13, 27, 0.93f));
			RenderTarget.DrawRectangle(new SharpDX.RectangleF(x0h, y0, W, H), B(40, 67, 91, 1f), 1.5f);
			float y = y0 + 3;
			Action<float, float, float, string, byte[], float> T = (cx, cw, yy, t, c, sz) => RenderTarget.DrawText(t, F(sz), new SharpDX.RectangleF(x0h + cx, yy, cw, rh), B(c[0], c[1], c[2], 1f));
			Action<byte, byte, byte> bg = (r, g, b) => RenderTarget.FillRectangle(new SharpDX.RectangleF(x0h + 1, y, W - 2, rh), B(r, g, b, 1f));
			Action<string, byte[], bool> full = (t, c, shade) => { if (shade) bg(18, 33, 54); T(pad, W - 2 * pad, y, t, c, fs); y += rh; };
			Action<string, byte[], string, byte[]> two = (a, ca, b, cb) => { bg(13, 23, 41); T(pad, W / 2 - pad, y, a, ca, fs); T(W / 2 + pad, W / 2 - 2 * pad, y, b, cb, fs); y += rh; };
			bg(18, 33, 54); T(pad, W - 2 * pad, y, "DRFX / GOD MODE X / FUSION 17    (NinjaTrader port - UNPROVEN)", s.Live ? cC : cV, fs); y += rh;
			bg(8, 13, 27); T(pad, W - 2 * pad, y, Instrument.MasterInstrument.Name + " · " + BarsPeriod.Value + " · " + (s.Live ? "LIVE" : "HISTORICAL") + " · CLOSED BAR", cM, fs); y += rh;
			two("REGIME  " + s.Regime + "  |  ADX " + Px(s.Adx), s.ChopVeto ? cA : s.Regime == "DIRECTIONAL" ? cG : cGo, "ATR " + Px(s.Atr), cC);
			Action<string, double, byte[]> meter = (lab, pct, c) => { };
			bg(18, 33, 54); { int nb = (int)Math.Round(Math.Min(100, Math.Max(0, s.Bull)) / 10.0), nr = (int)Math.Round(Math.Min(100, Math.Max(0, s.Bear)) / 10.0); T(pad, 70, y, "BULL " + Math.Round(s.Bull), cG, fs); T(W / 2 + pad, 70, y, "BEAR " + Math.Round(s.Bear), cR, fs); for (int k = 0; k < 10; k++) { RenderTarget.FillRectangle(new SharpDX.RectangleF(x0h + 80 + k * 10, y + rh * 0.3f, 8, rh * 0.4f), B(cG[0], cG[1], cG[2], k < nb ? 1f : 0.2f)); RenderTarget.FillRectangle(new SharpDX.RectangleF(x0h + W / 2 + 80 + k * 10, y + rh * 0.3f, 8, rh * 0.4f), B(cR[0], cR[1], cR[2], k < nr ? 1f : 0.2f)); } y += rh; }
			two("BULL AGREEMENT  " + Math.Round(s.Bull) + "/100", cG, "BEAR AGREEMENT  " + Math.Round(s.Bear) + "/100", cR);
			full("17-ENGINE DIAGNOSTIC MATRIX   /   " + s.Modules + " / 17 COMPUTED  •  NON-PROBABILISTIC", cC, true);
			bg(13, 23, 41); T(pad, 24, y, "ID", cM, fs - 1); T(34, 120, y, "ANALYTICAL ENGINE", cM, fs - 1); T(160, 90, y, "SIGNAL INTENSITY", cM, fs - 1); T(W * 0.60f, 40, y, "VOTE", cM, fs - 1); T(W * 0.72f, 50, y, "WEIGHT", cM, fs - 1); T(W * 0.84f, 80, y, "DIRECTION", cM, fs - 1); y += rh;
			for (int k = 0; k < 17; k++)
			{
				double v = s.E[k]; bool na = double.IsNaN(v); if (k % 2 == 0) bg(13, 23, 41); else bg(8, 13, 27); byte[] c = na ? cM : vc(v);
				T(pad, 24, y, (k + 1).ToString("00"), cM, fs); T(34, 125, y, F17Core.Names[k], cW, fs);
				int n = na ? 0 : (int)Math.Round(Math.Min(1.0, Math.Abs(v)) * 8.0); for (int q = 0; q < 8; q++) RenderTarget.FillRectangle(new SharpDX.RectangleF(x0h + 160 + q * 10, y + rh * 0.3f, 8, rh * 0.4f), B(c[0], c[1], c[2], q < n ? 1f : 0.18f));
				T(W * 0.60f, 44, y, na ? "N/A" : v.ToString("0.00;-0.00"), c, fs); T(W * 0.72f, 44, y, na ? "--" : s.EW[k].ToString("0.00"), cM, fs); T(W * 0.84f, 90, y, na ? "OFF" : v >= 0.24 ? "▲ BULL" : v <= -0.24 ? "▼ BEAR" : "◆ NEUTRAL", c, fs); y += rh;
			}
			string bias = s.Edge > 18 ? "BULLISH CONFLUENCE" : s.Edge < -18 ? "BEARISH CONFLUENCE" : "MIXED / NEUTRAL"; byte[] bcl = s.Edge > 18 ? cG : s.Edge < -18 ? cR : cGo;
			full("COMPOSITE EDGE   " + s.Edge.ToString("0.0") + " pts   |   " + bias, bcl, true);
			full("▰▰▰  SIGNAL GATES  " + (s.ChopVeto ? "CHOP BLOCKED" : "REGIME ALLOWED") + "   ·   MIN " + MinScore + " / EDGE " + MinEdge + "  ▰▰▰", s.ChopVeto ? cR : cC, false);
			two("MARKET STRUCTURE   " + (s.MS == 1 ? "BULLISH" : s.MS == -1 ? "BEARISH" : "NEUTRAL") + " | " + (s.SweepL ? "SELL SIDE SWEEP" : s.SweepH ? "BUY SIDE SWEEP" : "NO NEW SWEEP"), s.MS == 1 ? cG : s.MS == -1 ? cR : cGo, "FVG   " + (s.BullFVG ? "NEW BULL FVG" : s.BearFVG ? "NEW BEAR FVG" : "STRUCTURE ACTIVE"), cC);
			full("HTF 60  " + (s.H1 == 0 ? "N/A" : s.H1 > 0 ? "▲ BULL" : "▼ BEAR") + "   /   HTF 240  " + (s.H2 == 0 ? "N/A" : s.H2 > 0 ? "▲ BULL" : "▼ BEAR"), s.E[15] > 0 ? cG : s.E[15] < 0 ? cR : cGo, false);
			full("ENTROPY " + (double.IsNaN(s.Ent) ? "N/A" : s.Ent.ToString("0.00")) + " | EFF " + s.Eff.ToString("0.00"), cGo, true);
			full("VOLUME   " + (double.IsNaN(s.RelVol) ? "NO VOLUME" : s.RelVol.ToString("0.00") + "x RVOL") + "  /  MFI " + (double.IsNaN(s.Mfi) ? "N/A" : s.Mfi.ToString("0.0")), cC, false);
			full("REFERENCE ENTRY " + Px(s.Entry) + " / STOP " + Px(s.Stop) + " / " + s.Ref, cW, true);
			full("REFERENCE TP1 " + Px(s.T1) + " / TP2 " + Px(s.T2) + " / BREADTH " + Math.Max(s.BullBr, s.BearBr), cC, false);
			full((s.NewLong ? "▲ NEW CONFIRMED LONG" : s.NewShort ? "▼ NEW CONFIRMED SHORT" : s.Last) + "   ·   LONG " + Math.Round(s.Bull, 1) + "  |  SHORT " + Math.Round(s.Bear, 1), s.NewLong ? cG : s.NewShort ? cR : cGo, true);
			full("INDICATOR ONLY | NO VERIFIED WIN RATE | SIGNALS CONFIRM AT BAR CLOSE", cM, false);
		}
		catch (Exception ex) { if (!errShown) { errShown = true; Print("SneakyFusion17 OnRender: " + ex.Message); } }
		finally { foreach (var d in disp) d.Dispose(); }
	}
	#region Properties
	[NinjaScriptProperty] [Range(2, 200)] [Display(Name = "Fast EMA", Order = 0, GroupName = "1. Signal rules")] public int Fast { get; set; }
	[NinjaScriptProperty] [Range(3, 400)] [Display(Name = "Slow EMA", Order = 1, GroupName = "1. Signal rules")] public int Slow { get; set; }
	[NinjaScriptProperty] [Range(20, 600)] [Display(Name = "Bias EMA", Order = 2, GroupName = "1. Signal rules")] public int Bias { get; set; }
	[NinjaScriptProperty] [Range(1, 30)] [Display(Name = "Pivot length", Order = 3, GroupName = "1. Signal rules")] public int Pivot { get; set; }
	[NinjaScriptProperty] [Range(30, 95)] [Display(Name = "Min score", Order = 4, GroupName = "1. Signal rules")] public int MinScore { get; set; }
	[NinjaScriptProperty] [Range(0, 80)] [Display(Name = "Min edge (bull-bear)", Order = 5, GroupName = "1. Signal rules")] public int MinEdge { get; set; }
	[NinjaScriptProperty] [Range(0, 100)] [Display(Name = "Cooldown bars", Order = 6, GroupName = "1. Signal rules")] public int Cooldown { get; set; }
	[NinjaScriptProperty] [Display(Name = "Require trend gate", Order = 7, GroupName = "1. Signal rules")] public bool RequireTrend { get; set; }
	[NinjaScriptProperty] [Display(Name = "Block chop", Order = 8, GroupName = "1. Signal rules")] public bool DisallowChop { get; set; }
	[NinjaScriptProperty] [Display(Name = "Adaptive weights", Order = 9, GroupName = "1. Signal rules")] public bool Adaptive { get; set; }
	[NinjaScriptProperty] [Display(Name = "Breadth gate", Order = 10, GroupName = "1. Signal rules")] public bool QualityGate { get; set; }
	[NinjaScriptProperty] [Range(1, 17)] [Display(Name = "Min breadth", Order = 11, GroupName = "1. Signal rules")] public int MinBreadth { get; set; }
	[NinjaScriptProperty] [Range(5, 100)] [Display(Name = "Regime length", Order = 0, GroupName = "2. Model")] public int RegLen { get; set; }
	[NinjaScriptProperty] [Range(10, 100)] [Display(Name = "Entropy window", Order = 1, GroupName = "2. Model")] public int EntropyWin { get; set; }
	[NinjaScriptProperty] [Range(5, 100)] [Display(Name = "Channel length", Order = 2, GroupName = "2. Model")] public int ChannelLen { get; set; }
	[NinjaScriptProperty] [Range(0.05, 2.0)] [Display(Name = "FVG ATR", Order = 3, GroupName = "2. Model")] public double FvgAtr { get; set; }
	[NinjaScriptProperty] [Range(0.5, 6.0)] [Display(Name = "Stop ATR x", Order = 0, GroupName = "3. Risk levels (plots only)")] public double AtrStop { get; set; }
	[NinjaScriptProperty] [Range(0.5, 10.0)] [Display(Name = "TP1 R", Order = 1, GroupName = "3. Risk levels (plots only)")] public double Rr1 { get; set; }
	[NinjaScriptProperty] [Range(0.5, 20.0)] [Display(Name = "TP2 R", Order = 2, GroupName = "3. Risk levels (plots only)")] public double Rr2 { get; set; }
	[NinjaScriptProperty] [Display(Name = "Dashboard", Order = 0, GroupName = "4. Appearance")] public bool ShowDash { get; set; }
	[NinjaScriptProperty] [Display(Name = "Dashboard position", Order = 1, GroupName = "4. Appearance")] public F17Pos DashPos { get; set; }
	[NinjaScriptProperty] [Range(7, 24)] [Display(Name = "Dashboard font", Order = 2, GroupName = "4. Appearance")] public int DashFont { get; set; }
	[NinjaScriptProperty] [Display(Name = "Signal labels", Order = 3, GroupName = "4. Appearance")] public bool ShowLabels { get; set; }
	[NinjaScriptProperty] [Display(Name = "Show levels (unused visual)", Order = 4, GroupName = "4. Appearance")] public bool ShowLevels { get; set; }
	[NinjaScriptProperty] [Display(Name = "EMA / VWAP lines", Order = 5, GroupName = "4. Appearance")] public bool ShowLines { get; set; }
	[NinjaScriptProperty] [Display(Name = "BOS / CHoCH markers", Order = 7, GroupName = "4. Appearance")] public bool ShowStructure { get; set; }
	[NinjaScriptProperty] [Display(Name = "Liquidity sweep markers (SWP)", Order = 8, GroupName = "4. Appearance")] public bool ShowSweeps { get; set; }
	[NinjaScriptProperty] [Display(Name = "Last-signal risk/reward boxes", Order = 9, GroupName = "4. Appearance")] public bool ShowBoxes { get; set; }
	[NinjaScriptProperty] [Range(5, 100)] [Display(Name = "Box projection bars", Order = 10, GroupName = "4. Appearance")] public int Projection { get; set; }
	[NinjaScriptProperty] [Display(Name = "(unused)", Order = 11, GroupName = "4. Appearance")] public bool ColorEma { get; set; }
	[NinjaScriptProperty] [Display(Name = "Adaptive trend cloud", Order = 12, GroupName = "4. Appearance")] public bool ShowCloud { get; set; }
	[NinjaScriptProperty] [Display(Name = "Adaptive score candles", Order = 13, GroupName = "4. Appearance")] public bool ColorBars { get; set; }
	[NinjaScriptProperty] [Display(Name = "Previous day range", Order = 14, GroupName = "4. Appearance")] public bool ShowPrevDay { get; set; }
	[NinjaScriptProperty] [Display(Name = "F17 setup trace labels", Order = 15, GroupName = "4. Appearance")] public bool ShowTrace { get; set; }
	[NinjaScriptProperty] [Display(Name = "Spectral ribbon", Order = 17, GroupName = "4. Appearance")] public bool ShowRibbon { get; set; }
	[NinjaScriptProperty] [Display(Name = "Statistical (research) ribbons", Order = 18, GroupName = "4. Appearance")] public bool ShowResearch { get; set; }
	[NinjaScriptProperty] [Display(Name = "Supply / demand zones", Order = 19, GroupName = "4. Appearance")] public bool ShowZones { get; set; }
	[NinjaScriptProperty] [Display(Name = "Fair value gaps", Order = 20, GroupName = "4. Appearance")] public bool ShowFvg { get; set; }
	[NinjaScriptProperty] [Display(Name = "Volume profile / POC", Order = 21, GroupName = "4. Appearance")] public bool ShowProfile { get; set; }
	[NinjaScriptProperty] [Display(Name = "F17 radar", Order = 22, GroupName = "4. Appearance")] public bool ShowRadar { get; set; }
	[NinjaScriptProperty] [Display(Name = "Liquidity ladder", Order = 23, GroupName = "4. Appearance")] public bool ShowLadder { get; set; }
	[NinjaScriptProperty] [Range(100, 200000)] [Display(Name = "Label bars back", Order = 16, GroupName = "4. Appearance")] public int DrawBarsBack { get; set; }
	[Browsable(false)] [XmlIgnore] public Series<double> SignalPlot { get { Update(); return Values[0]; } }
	[Browsable(false)] [XmlIgnore] public Series<double> EntryPlot { get { Update(); return Values[1]; } }
	[Browsable(false)] [XmlIgnore] public Series<double> StopPlot { get { Update(); return Values[2]; } }
	[Browsable(false)] [XmlIgnore] public Series<double> Tp1Plot { get { Update(); return Values[3]; } }
	[Browsable(false)] [XmlIgnore] public Series<double> Tp2Plot { get { Update(); return Values[4]; } }
	[Browsable(false)] [XmlIgnore] public Series<double> BullPlot { get { Update(); return Values[5]; } }
	[Browsable(false)] [XmlIgnore] public Series<double> BearPlot { get { Update(); return Values[6]; } }
	#endregion
}
}
