// SneakyAlphaForgeFlow - NinjaTrader 8 port of TradingView "Alpha Forge | Flow BUY SELL v0.7" (AF AURORA)
// v1.1: optional mirrored SHORT side (SHORT/COVER). Flow-persistence signal: BUY when smoothed OHLCV pressure score is positive + price above trend EMA + range filter OK; SELL after N closes below trend EMA.
// Signals are evaluated on closed bars (Calculate.OnBarClose). Python vs C# parity: identical BUY/SELL bars on NQ 5m (106 events).
// Hidden plots for auto-traders: 0 BuySignal (1 on the signal bar), 1 SellSignal (long exit), 2 InLong, 3 FlowScore; 7 ShortSignal (1 on short entry bar), 8 CoverSignal (short exit), 9 InShort, 10 Signal (+1 long entry / -1 short entry, 0 otherwise).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
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
internal class AfEma
{
	int n; double a; List<double> last = new List<double>(); public double Val = double.NaN; int consec;
	public AfEma(int len) { n = len; a = 2.0 / (len + 1); }
	public double Step(double x)
	{
		if (double.IsNaN(x)) { Val = double.NaN; consec = 0; last.Clear(); return Val; }
		last.Add(x); if (last.Count > n) last.RemoveAt(0); consec++;
		if (double.IsNaN(Val)) { if (consec >= n) { double s = 0; foreach (double q in last) s += q; Val = s / n; } }
		else Val = a * x + (1 - a) * Val;
		return Val;
	}
}
internal class AfP
{
	public int Smooth = 3, EntryBars = 1, WarnBars = 2, TrendLen = 20, ExitBars = 2, Cool = 3, VolLen = 50, AtrLen = 14, RegLen = 50, RegSlope = 5, EffLen = 20;
	public double Entry = 10.0, Warn = 10.0, MinRel = 0.50, MinPress = 0.15, Cap = 5.0, Av = 0.60, Beta = 1.0, Ar = 0.40, Br = 1.60, Lam = 1.25, MinEff = 0.30;
	public bool Filt = true, AllowShort = true;
}
internal class AfCore
{
	public AfP P; public int Bar = -1;
	List<double> C = new List<double>(), V = new List<double>(), Rt = new List<double>(), Ch = new List<double>();
	double volSum, atrPrev = double.NaN, prevSma = double.NaN, trSum; int atrN; double prevClose;
	AfEma emaFs, emaTr, emaRt;
	public bool InShort, ShortEntry, Cover, CautionS; public int NegE, PosW, Abv, LastCover = int.MinValue, Shorts, Covers; public bool InLong, Buy, Sell, Caution, Warmed, Ready; public int Pos, Neg, Bel, LastSell = int.MinValue, Buys, Sells, LastSigBar = -1; public string LastSig = "NONE", Status = "WARMUP";
	public double Score = double.NaN, Trend = double.NaN, RelVol = double.NaN, RegTrend = double.NaN, Eff = double.NaN, Atr = double.NaN, LastClose;
	public bool FilterOk, BroadOk;
	public AfCore(AfP p) { P = p; emaFs = new AfEma(p.Smooth); emaTr = new AfEma(p.TrendLen); emaRt = new AfEma(p.RegLen); }
	public void Step(double o, double h, double l, double c, double v)
	{
		Bar++; int i = Bar; Buy = false; Sell = false; ShortEntry = false; Cover = false; Ready = false; LastClose = c;
		double pv = prevSma, pa = atrPrev;
		// ATR (RMA, SMA seed) computed for this bar after capturing prior values
		double tr = i == 0 ? h - l : Math.Max(h - l, Math.Max(Math.Abs(h - prevClose), Math.Abs(l - prevClose)));
		double atrNow;
		if (i < P.AtrLen - 1) { trSum += tr; atrNow = double.NaN; }
		else if (i == P.AtrLen - 1) { trSum += tr; atrNow = trSum / P.AtrLen; }
		else atrNow = (atrPrev * (P.AtrLen - 1) + tr) / P.AtrLen;
		C.Add(c); V.Add(v);
		double gated = double.NaN; bool pressAct = false; double rel = double.NaN;
		if (!double.IsNaN(pv) && pv > 0) rel = Math.Min(v / pv, P.Cap); RelVol = rel;
		if (i > 0 && !double.IsNaN(pa) && !double.IsNaN(rel) && pa > 0 && prevClose > 0 && c > 0 && v > 0)
		{
			double rg = h - l; double cl = rg > 0 ? Math.Max(-1, Math.Min(1, (2 * c - h - l) / rg)) : 0.0; double pr = cl * rel; double nr = Math.Log(c / prevClose) / (pa / prevClose); Ready = true;
			double q = Math.Sign(pr); double vt = P.Av * Math.Tanh(P.Beta * pr); double cor = q * (P.Ar - P.Br * Math.Tanh(P.Lam * Math.Abs(nr))); double raw = 100.0 * Math.Tanh(vt + cor);
			pressAct = Math.Abs(pr) >= P.MinPress; gated = pressAct ? raw : 0.0;
		}
		double fs = emaFs.Step(gated); Score = Ready ? fs : double.NaN; Trend = emaTr.Step(c); double rt = emaRt.Step(c); Rt.Add(rt); RegTrend = rt;
		Ch.Add(i == 0 ? double.NaN : Math.Abs(c - prevClose)); double eff = double.NaN;
		if (i >= P.EffLen) { double td = 0; for (int k = i - P.EffLen + 1; k <= i; k++) td += Ch[k]; eff = td > 0 ? Math.Max(-1, Math.Min(1, (c - C[i - P.EffLen]) / td)) : 0.0; }
		Eff = eff;
		Warmed = Ready && i >= Math.Max(P.VolLen, Math.Max(P.AtrLen, P.TrendLen)) + P.Smooth && !double.IsNaN(fs);
		bool trendUp = !double.IsNaN(Trend) && c > Trend; bool volOk = Ready && rel >= P.MinRel;
		bool rr = i - P.RegSlope >= 0 && !double.IsNaN(Rt[i - P.RegSlope]) && !double.IsNaN(eff);
		BroadOk = rr && c > rt && rt > Rt[i - P.RegSlope]; bool effOk = rr && eff >= P.MinEff; FilterOk = !P.Filt || (BroadOk && effOk);
		Pos = Warmed && pressAct && fs >= P.Entry ? Pos + 1 : 0; Neg = Warmed && pressAct && fs <= -P.Warn ? Neg + 1 : 0; Bel = !double.IsNaN(Trend) && c < Trend ? Bel + 1 : 0;
		bool trendDn = !double.IsNaN(Trend) && c < Trend; Abv = trendUp ? Abv + 1 : 0;
		NegE = Warmed && pressAct && fs <= -P.Entry ? NegE + 1 : 0; PosW = Warmed && pressAct && fs >= P.Warn ? PosW + 1 : 0;
		bool broadDn = rr && c < rt && rt < Rt[i - P.RegSlope]; bool effDn = rr && eff <= -P.MinEff; bool filterOkS = !P.Filt || (broadDn && effDn);
		bool cdS = LastCover == int.MinValue || i - LastCover > P.Cool; bool shortReady = P.AllowShort && Warmed && NegE >= P.EntryBars && trendDn && volOk && pressAct && cdS && filterOkS;
		bool cd = LastSell == int.MinValue || i - LastSell > P.Cool; bool flowEntry = Pos >= P.EntryBars; bool entryReady = Warmed && flowEntry && trendUp && volOk && pressAct && cd && FilterOk;
		bool flowWarn = Neg >= P.WarnBars; bool trendExit = Bel >= P.ExitBars; Caution = false;
		if (InLong)
		{
			if (trendExit) { Sell = true; InLong = false; LastSell = i; LastSig = "SELL"; LastSigBar = i; Sells++; Pos = 0; Status = "SELL / EXIT LONG"; }
			else { Caution = flowWarn; Status = flowWarn ? "HOLD / FLOW CAUTION" : "HOLD LONG"; }
		}
		else if (InShort)
		{
			CautionS = false;
			if (Abv >= P.ExitBars) { Cover = true; InShort = false; LastCover = i; LastSig = "COVER"; LastSigBar = i; Covers++; NegE = 0; Status = "COVER / EXIT SHORT"; }
			else { CautionS = PosW >= P.WarnBars; Status = CautionS ? "HOLD / FLOW CAUTION" : "HOLD SHORT"; }
		}
		else if (entryReady) { Buy = true; InLong = true; LastSig = "BUY"; LastSigBar = i; Buys++; Neg = 0; Bel = 0; Status = "BUY / ENTER LONG"; }
		else if (shortReady) { ShortEntry = true; InShort = true; LastSig = "SHORT"; LastSigBar = i; Shorts++; PosW = 0; Abv = 0; Status = "SHORT / ENTER SHORT"; }
		else Status = !Warmed ? "WARMUP / NO VOLUME" : !cd ? "WAIT / COOLDOWN" : P.Filt && !BroadOk ? "WAIT / BROADER TREND" : P.Filt && !effOk ? "WAIT / RANGE" : !trendUp ? "WAIT / ABOVE TREND" : (!pressAct || !volOk) ? "WAIT / PRESSURE" : !flowEntry ? "WAIT / FLOW CONFIRM" : "WAIT / SETUP";
		Atr = pa;
		// roll prior-state for next bar
		volSum += v; if (i >= P.VolLen) volSum -= V[i - P.VolLen]; prevSma = i >= P.VolLen - 1 ? volSum / P.VolLen : double.NaN;
		atrPrev = atrNow; prevClose = c;
	}
}

	public enum AfPos { BottomRight, TopRight, BottomLeft, TopLeft }

	public class SneakyAlphaForgeFlow : Indicator
	{
		private AfCore core; private readonly Dictionary<long, Brush> brushes = new Dictionary<long, Brush>(); private readonly Queue<string> tags = new Queue<string>(); private bool errShown;
		private readonly object tLock = new object(); private string dHero = "STANDBY", dDetail = "", dPulse = "", dScore = "-", dLast = "LAST NONE", dTime = "-", dExit = ""; private int[] dHeroCol = { 73, 223, 255 }; private int[] dLastCol = { 145, 165, 190 }; private int[] dPulseCol = { 145, 165, 190 }; private bool dReady;
		private static readonly int[] cBuy = { 69, 245, 176 }, cSell = { 255, 89, 122 }, cScan = { 73, 223, 255 }, cCaut = { 255, 212, 121 }, cNavy = { 8, 17, 31 }, cSurf = { 13, 26, 44 }, cBright = { 234, 245, 255 }, cMute = { 145, 165, 190 };

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Alpha Forge Flow BUY/SELL (AF AURORA) port: OHLCV flow-persistence BUY/SELL (long) plus optional mirrored SHORT/COVER signals with aurora trend line, badges and dashboard.";
				Name = "SneakyAlphaForgeFlow"; Calculate = Calculate.OnBarClose; IsOverlay = true; DisplayInDataBox = true; PaintPriceMarkers = false; IsSuspendedWhileInactive = true; ArePlotsConfigurable = true; DrawOnPricePanel = true;
				Smooth = 3; EntryLevel = 10.0; EntryBars = 1; WarnLevel = 10.0; WarnBars = 2; TrendLen = 20; ExitBars = 2; CoolBars = 3; MinRelVol = 0.50; MinPressure = 0.15;
				VolLen = 50; AtrLen = 14; VolCap = 5.0; Av = 0.60; Beta = 1.0; Ar = 0.40; Br = 1.60; Lambda = 1.25;
				UseFilter = true; RegLen = 50; RegSlope = 5; EffLen = 20; MinEff = 0.30;
				ShowTrend = true; ShowGlow = true; ShowBadges = true; BadgeFont = 12; ShowDash = true; DashPos = AfPos.BottomRight; DashFont = 12; TintCandles = false; DrawBarsBack = 3000; AlertsOn = true; AllowShort = true;
				AddPlot(Brushes.Transparent, "BuySignal"); AddPlot(Brushes.Transparent, "SellSignal"); AddPlot(Brushes.Transparent, "InLong"); AddPlot(Brushes.Transparent, "FlowScore");
				AddPlot(new Stroke(Brushes.Transparent, 12), PlotStyle.Line, "TrendGlowOuter"); AddPlot(new Stroke(Brushes.Transparent, 7), PlotStyle.Line, "TrendGlowInner"); AddPlot(new Stroke(Brushes.Transparent, 3), PlotStyle.Line, "TrendEMA");
				AddPlot(Brushes.Transparent, "ShortSignal"); AddPlot(Brushes.Transparent, "CoverSignal"); AddPlot(Brushes.Transparent, "InShort"); AddPlot(Brushes.Transparent, "Signal");
			}
			else if (State == State.DataLoaded)
			{
				var p = new AfP { Smooth = Smooth, Entry = EntryLevel, EntryBars = EntryBars, Warn = WarnLevel, WarnBars = WarnBars, TrendLen = TrendLen, ExitBars = ExitBars, Cool = CoolBars, MinRel = MinRelVol, MinPress = MinPressure, VolLen = VolLen, AtrLen = AtrLen, Cap = VolCap, Av = Av, Beta = Beta, Ar = Ar, Br = Br, Lam = Lambda, Filt = UseFilter, RegLen = RegLen, RegSlope = RegSlope, EffLen = EffLen, MinEff = MinEff, AllowShort = AllowShort };
				core = new AfCore(p);
			}
			else if (State == State.Terminated) brushes.Clear();
		}

		private Brush B(int[] c, int a = 255) { long key = ((long)c[0] << 24) | ((long)c[1] << 16) | ((long)c[2] << 8) | (long)a; Brush br; if (!brushes.TryGetValue(key, out br)) { br = new SolidColorBrush(Color.FromArgb((byte)a, (byte)c[0], (byte)c[1], (byte)c[2])); br.Freeze(); brushes[key] = br; } return br; }
		private static int[] Lerp(int[] a, int[] b, double t) { return new[] { (int)(a[0] + (b[0] - a[0]) * t), (int)(a[1] + (b[1] - a[1]) * t), (int)(a[2] + (b[2] - a[2]) * t) }; }

		protected override void OnBarUpdate()
		{
			try
			{
				if (core == null) return;
				core.Step(Open[0], High[0], Low[0], Close[0], Volume[0]);
				Values[0][0] = core.Buy ? 1 : 0; Values[1][0] = core.Sell ? 1 : 0; Values[2][0] = core.InLong ? 1 : 0; if (!double.IsNaN(core.Score)) Values[3][0] = core.Score;
				Values[7][0] = core.ShortEntry ? 1 : 0; Values[8][0] = core.Cover ? 1 : 0; Values[9][0] = core.InShort ? 1 : 0; Values[10][0] = core.Buy ? 1 : (core.ShortEntry ? -1 : 0);
				bool reset = !core.InLong && !core.InShort && ((core.LastSell != int.MinValue && core.Bar - core.LastSell <= CoolBars) || (core.LastCover != int.MinValue && core.Bar - core.LastCover <= CoolBars));
				int[] phase = core.InLong ? (core.Caution ? cCaut : cBuy) : core.InShort ? (core.CautionS ? cCaut : cSell) : reset ? cSell : cScan;
				if (ShowTrend && !double.IsNaN(core.Trend))
				{
					Values[6][0] = core.Trend; PlotBrushes[6][0] = B(phase);
					if (ShowGlow) { Values[4][0] = core.Trend; Values[5][0] = core.Trend; PlotBrushes[4][0] = B(phase, 40); PlotBrushes[5][0] = B(phase, 85); }
				}
				if (TintCandles)
				{
					double en = double.IsNaN(core.RelVol) ? 0.40 : Math.Max(0, Math.Min(1, core.RelVol / 2.0)); en = Math.Round(en * 10) / 10.0; bool up = Close[0] >= Open[0];
					int[] hue = up ? Lerp(core.InLong ? new[] { 23, 141, 116 } : new[] { 19, 116, 143 }, core.InLong ? new[] { 138, 255, 220 } : new[] { 131, 239, 255 }, en) : Lerp(core.InLong ? new[] { 155, 49, 94 } : new[] { 95, 62, 155 }, core.InLong ? new[] { 255, 141, 182 } : new[] { 205, 164, 255 }, en);
					BarBrush = B(hue, 150); CandleOutlineBrush = B(Lerp(hue, new[] { 255, 255, 255 }, 0.18));
				}
				bool recent = CurrentBar >= Count - 1 - DrawBarsBack;
				if (recent && ShowBadges && (core.Buy || core.Sell || core.ShortEntry || core.Cover))
				{
					bool buy = core.Buy || core.Cover; string lbl = core.Buy ? " BUY  ↗ " : core.Sell ? " SELL  ↘ " : core.ShortEntry ? " SHORT  ↘ " : " COVER  ↗ "; string tg = "AfBadge" + CurrentBar; var col = (core.Buy || core.Cover) ? cBuy : cSell;
					Draw.Text(this, tg, false, lbl, 0, buy ? Low[0] : High[0], buy ? 22 + BadgeFont : -(22 + BadgeFont), B(col), new SimpleFont("Consolas", BadgeFont) { Bold = true }, System.Windows.TextAlignment.Center, B(col), B(cNavy), 88);
					tags.Enqueue(tg); while (tags.Count > 400) RemoveDrawObject(tags.Dequeue());
				}
				if (AlertsOn && State == State.Realtime)
				{
					if (core.Buy) Alert("AfBuy" + CurrentBar, Priority.Medium, "AF Flow BUY: enter long. " + Instrument.MasterInstrument.Name + " close " + Close[0].ToString("0.00", CultureInfo.InvariantCulture), NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, Brushes.Black, Brushes.Lime);
					if (core.ShortEntry) Alert("AfShort" + CurrentBar, Priority.Medium, "AF Flow SHORT: enter short. " + Instrument.MasterInstrument.Name + " close " + Close[0].ToString("0.00", CultureInfo.InvariantCulture), NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, Brushes.Black, Brushes.Red);
					if (core.Cover) Alert("AfCover" + CurrentBar, Priority.Medium, "AF Flow COVER: close short. " + Instrument.MasterInstrument.Name + " close " + Close[0].ToString("0.00", CultureInfo.InvariantCulture), NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, Brushes.Black, Brushes.Lime);
					if (core.Sell) Alert("AfSell" + CurrentBar, Priority.Medium, "AF Flow SELL: close long. " + Instrument.MasterInstrument.Name + " close " + Close[0].ToString("0.00", CultureInfo.InvariantCulture), NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, Brushes.Black, Brushes.Red);
				}
				if (CurrentBar >= Count - 2 || State == State.Realtime) UpdateDash(phase, reset);
			}
			catch (Exception ex) { if (!errShown) { errShown = true; Print("SneakyAlphaForgeFlow error: " + ex); } }
		}

		private void UpdateDash(int[] phase, bool reset)
		{
			bool trendUp = !double.IsNaN(core.Trend) && Close[0] > core.Trend; string hero = core.InLong ? (core.Caution ? "LONG  /  CAUTION" : "LONG ACTIVE") : core.InShort ? (core.CautionS ? "SHORT  /  CAUTION" : "SHORT ACTIVE") : "STANDBY";
			string detail = core.InShort ? (core.CautionS ? "FLOW SOFTENING · DOWNTREND STILL ACTIVE" : "FOLLOWING THE CONFIRMED DOWNTREND") : core.InLong ? (core.Caution ? "FLOW SOFTENING · TREND STILL ACTIVE" : "FOLLOWING THE CONFIRMED TREND") : reset ? "REARMING AFTER EXIT" : !core.Warmed ? "WAITING FOR VALID DATA" : UseFilter && !core.BroadOk ? "WAITING FOR A RISING BROADER TREND" : UseFilter && !core.FilterOk ? "RANGE FILTER · WAITING FOR DIRECTION" : !trendUp ? "WAITING FOR PRICE ABOVE TREND" : "SCANNING FOR THE NEXT SETUP";
			int lit = double.IsNaN(core.Score) ? 0 : (int)Math.Min(10, Math.Floor(Math.Abs(core.Score) / 10.0)); string pulse = ""; for (int i = 0; i < 10; i++) pulse += i < lit ? "▰" : "▱";
			string sc = double.IsNaN(core.Score) ? "-" : (core.Score > 0 ? "+" : "") + core.Score.ToString("0.0", CultureInfo.InvariantCulture);
			string tm = core.LastSigBar < 0 ? "-" : Time[Math.Max(0, CurrentBar - core.LastSigBar)].ToString("MMM d  HH:mm", CultureInfo.InvariantCulture).ToUpperInvariant();
			lock (tLock) { dHero = hero; dHeroCol = phase; dDetail = detail; dPulse = pulse + "  " + sc; dPulseCol = double.IsNaN(core.Score) ? cMute : core.Score >= 0 ? cBuy : cSell; dLast = "LAST " + core.LastSig; dLastCol = (core.LastSig == "BUY" || core.LastSig == "COVER") ? cBuy : (core.LastSig == "SELL" || core.LastSig == "SHORT") ? cSell : cMute; dTime = tm; dExit = core.InShort ? ExitBars + " CLOSES > EMA " + TrendLen : ExitBars + " CLOSES < EMA " + TrendLen; dReady = true; }
			if (ChartControl != null) ChartControl.InvalidateVisual();
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (!ShowDash || RenderTarget == null || ChartPanel == null || !dReady) return;
			var disp = new List<IDisposable>();
			try
			{
				string hero, detail, pulse, last, tm, ex; int[] hc, lc, pc; lock (tLock) { hero = dHero; detail = dDetail; pulse = dPulse; last = dLast; tm = dTime; ex = dExit; hc = dHeroCol; lc = dLastCol; pc = dPulseCol; }
				float fs = DashFont; float W = fs * 22f, pad = 10f; var rows = new[] { fs * 2.0f, fs * 1.4f, fs * 2.6f, fs * 1.4f, fs * 1.7f, fs * 1.7f, fs * 1.7f, fs * 1.5f };
				float H = 0; foreach (float r in rows) H += r; float pw = ChartPanel.W, ph = ChartPanel.H; bool right = DashPos == AfPos.BottomRight || DashPos == AfPos.TopRight, top = DashPos == AfPos.TopRight || DashPos == AfPos.TopLeft;
				float x0 = right ? pw - W - 16f : 12f, y0 = top ? 46f : ph - H - 44f; float y = y0;
				Func<float, bool, bool, SharpDX.DirectWrite.TextFormat> F = (sz, bold, rt) => { var f = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", bold ? SharpDX.DirectWrite.FontWeight.Bold : SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal, sz) { ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center, TextAlignment = rt ? SharpDX.DirectWrite.TextAlignment.Trailing : SharpDX.DirectWrite.TextAlignment.Leading }; disp.Add(f); return f; };
				Func<int[], float, SharpDX.Direct2D1.SolidColorBrush> Br2 = (c, a) => { var b = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(c[0] / 255f, c[1] / 255f, c[2] / 255f, a)); disp.Add(b); return b; };
				Action<float, float, int[], float> fill = (yy, hh, c, a) => RenderTarget.FillRectangle(new SharpDX.RectangleF(x0, yy, W, hh), Br2(c, a));
				RenderTarget.FillRectangle(new SharpDX.RectangleF(x0 + 3, y0 + 3, W, H), Br2(new[] { 0, 0, 0 }, 0.5f));
				Action<float, float, string, int[], bool, bool, float> txt = (yy, hh, t, c, bold, rt, sz) => RenderTarget.DrawText(t, F(sz, bold, rt), new SharpDX.RectangleF(x0 + pad, yy, W - 2 * pad, hh), Br2(c, 1f));
				int ri = 0; float[] ys = new float[8]; for (int i = 0; i < 8; i++) { ys[i] = y; y += rows[i]; }
				fill(y0, H, cNavy, 0.96f); fill(ys[0], rows[0] + rows[1], cSurf, 1f); fill(ys[2], rows[2], hc, 0.14f); fill(ys[5], rows[5], cSurf, 1f); fill(ys[7], rows[7], cSurf, 1f);
				RenderTarget.DrawRectangle(new SharpDX.RectangleF(x0, y0, W, H), Br2(new[] { 40, 67, 91 }, 1f), 1.5f);
				txt(ys[0], rows[0], "▲  A L P H A   F O R G E", cScan, true, false, fs); txt(ys[1], rows[1], "FLOW PERSISTENCE  /  AURORA GLASS", cMute, false, false, fs * 0.72f);
				txt(ys[2], rows[2], hero, hc, true, false, fs * 1.6f); txt(ys[3], rows[3], detail, cMute, false, false, fs * 0.72f);
				txt(ys[4], rows[4], "FLOW PULSE", cMute, false, false, fs * 0.8f); txt(ys[4], rows[4], pulse, pc, true, true, fs * 0.95f);
				txt(ys[5], rows[5], last, lc, true, false, fs * 0.8f); txt(ys[5], rows[5], tm, cBright, true, true, fs * 0.95f);
				txt(ys[6], rows[6], "EXIT CONFIRMATION", cMute, false, false, fs * 0.8f); txt(ys[6], rows[6], ex, cBright, true, true, fs * 0.8f);
				txt(ys[7], rows[7], "● CONFIRMED BARS", cScan, false, false, fs * 0.72f); txt(ys[7], rows[7], AllowShort ? "LONG + SHORT" : "LONG ONLY", cMute, false, true, fs * 0.72f);
			}
			catch (Exception ex2) { if (!errShown) { errShown = true; Print("SneakyAlphaForgeFlow OnRender error: " + ex2); } }
			finally { foreach (var d in disp) d.Dispose(); }
		}

		#region Properties
		[NinjaScriptProperty] [Range(1, 50)] [Display(Name = "Flow smoothing", Order = 0, GroupName = "1. Signal rules")] public int Smooth { get; set; }
		[Display(Name = "Enable SHORT side (mirror of BUY/SELL)", Description = "Short entries use the same rules mirrored: negative flow, price below trend EMA, falling broader trend. Exit (COVER) after N closes above the trend EMA. Plots: ShortSignal, CoverSignal, InShort, Signal (+1 long / -1 short).", Order = 10, GroupName = "1. Signal rules")] public bool AllowShort { get; set; }
		[NinjaScriptProperty] [Range(1.0, 90.0)] [Display(Name = "BUY score threshold", Order = 1, GroupName = "1. Signal rules")] public double EntryLevel { get; set; }
		[NinjaScriptProperty] [Range(1, 10)] [Display(Name = "Positive flow closes required", Order = 2, GroupName = "1. Signal rules")] public int EntryBars { get; set; }
		[NinjaScriptProperty] [Range(1.0, 90.0)] [Display(Name = "Negative-flow warning threshold", Order = 3, GroupName = "1. Signal rules")] public double WarnLevel { get; set; }
		[NinjaScriptProperty] [Range(1, 10)] [Display(Name = "Negative flow closes for warning", Order = 4, GroupName = "1. Signal rules")] public int WarnBars { get; set; }
		[NinjaScriptProperty] [Range(5, 500)] [Display(Name = "Trend EMA length", Order = 5, GroupName = "1. Signal rules")] public int TrendLen { get; set; }
		[NinjaScriptProperty] [Range(1, 10)] [Display(Name = "Closes below trend to SELL", Order = 6, GroupName = "1. Signal rules")] public int ExitBars { get; set; }
		[NinjaScriptProperty] [Range(0, 100)] [Display(Name = "Bars to wait after SELL", Order = 7, GroupName = "1. Signal rules")] public int CoolBars { get; set; }
		[NinjaScriptProperty] [Range(0.0, 100.0)] [Display(Name = "Minimum relative volume to BUY", Order = 8, GroupName = "1. Signal rules")] public double MinRelVol { get; set; }
		[NinjaScriptProperty] [Range(0.01, 100.0)] [Display(Name = "Minimum absolute pressure to BUY", Order = 9, GroupName = "1. Signal rules")] public double MinPressure { get; set; }
		[NinjaScriptProperty] [Range(10, 500)] [Display(Name = "Prior volume baseline", Order = 0, GroupName = "2. OHLCV model")] public int VolLen { get; set; }
		[NinjaScriptProperty] [Range(2, 200)] [Display(Name = "Prior ATR baseline", Order = 1, GroupName = "2. OHLCV model")] public int AtrLen { get; set; }
		[NinjaScriptProperty] [Range(1.0, 50.0)] [Display(Name = "Relative volume cap", Order = 2, GroupName = "2. OHLCV model")] public double VolCap { get; set; }
		[NinjaScriptProperty] [Range(0.01, 10.0)] [Display(Name = "Volume amplitude (Av)", Order = 3, GroupName = "2. OHLCV model")] public double Av { get; set; }
		[NinjaScriptProperty] [Range(0.05, 10.0)] [Display(Name = "Volume saturation (beta)", Order = 4, GroupName = "2. OHLCV model")] public double Beta { get; set; }
		[NinjaScriptProperty] [Range(0.0, 10.0)] [Display(Name = "Persistence amplitude (Ar)", Order = 5, GroupName = "2. OHLCV model")] public double Ar { get; set; }
		[NinjaScriptProperty] [Range(0.0, 10.0)] [Display(Name = "Price-move attenuation (Br)", Order = 6, GroupName = "2. OHLCV model")] public double Br { get; set; }
		[NinjaScriptProperty] [Range(0.05, 10.0)] [Display(Name = "Price-move saturation (lambda)", Order = 7, GroupName = "2. OHLCV model")] public double Lambda { get; set; }
		[NinjaScriptProperty] [Display(Name = "Avoid ranges and falling trends", Order = 0, GroupName = "3. Entry filter")] public bool UseFilter { get; set; }
		[NinjaScriptProperty] [Range(5, 300)] [Display(Name = "Broader trend EMA", Order = 1, GroupName = "3. Entry filter")] public int RegLen { get; set; }
		[NinjaScriptProperty] [Range(1, 50)] [Display(Name = "Broader trend rise lookback", Order = 2, GroupName = "3. Entry filter")] public int RegSlope { get; set; }
		[NinjaScriptProperty] [Range(2, 100)] [Display(Name = "Directional efficiency lookback", Order = 3, GroupName = "3. Entry filter")] public int EffLen { get; set; }
		[NinjaScriptProperty] [Range(0.05, 0.95)] [Display(Name = "Minimum upward efficiency", Order = 4, GroupName = "3. Entry filter")] public double MinEff { get; set; }
		[NinjaScriptProperty] [Display(Name = "Trend line", Order = 0, GroupName = "4. Appearance")] public bool ShowTrend { get; set; }
		[NinjaScriptProperty] [Display(Name = "Neon line glow", Order = 1, GroupName = "4. Appearance")] public bool ShowGlow { get; set; }
		[NinjaScriptProperty] [Display(Name = "BUY / SELL badges", Order = 2, GroupName = "4. Appearance")] public bool ShowBadges { get; set; }
		[NinjaScriptProperty] [Range(8, 28)] [Display(Name = "Badge font size", Order = 3, GroupName = "4. Appearance")] public int BadgeFont { get; set; }
		[NinjaScriptProperty] [Display(Name = "Alpha Forge dashboard", Order = 4, GroupName = "4. Appearance")] public bool ShowDash { get; set; }
		[NinjaScriptProperty] [Display(Name = "Dashboard position", Order = 5, GroupName = "4. Appearance")] public AfPos DashPos { get; set; }
		[NinjaScriptProperty] [Range(8, 28)] [Display(Name = "Dashboard font size", Order = 6, GroupName = "4. Appearance")] public int DashFont { get; set; }
		[NinjaScriptProperty] [Display(Name = "Signature candle colours", Order = 7, GroupName = "4. Appearance")] public bool TintCandles { get; set; }
		[NinjaScriptProperty] [Range(100, 200000)] [Display(Name = "Draw badges this many bars back", Order = 8, GroupName = "4. Appearance")] public int DrawBarsBack { get; set; }
		[NinjaScriptProperty] [Display(Name = "Alerts (realtime)", Order = 9, GroupName = "4. Appearance")] public bool AlertsOn { get; set; }
		[Browsable(false)] [XmlIgnore] public Series<double> BuyPlot { get { Update(); return Values[0]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> SellPlot { get { Update(); return Values[1]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> InLongPlot { get { Update(); return Values[2]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> ShortPlot { get { Update(); return Values[7]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> CoverPlot { get { Update(); return Values[8]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> InShortPlot { get { Update(); return Values[9]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> SignalPlot { get { Update(); return Values[10]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> ScorePlot { get { Update(); return Values[3]; } }
		#endregion
	}
}
