// SneakyEmaBounce: NinjaTrader 8 port of "TO_EMA_Bounce" (Sierra Chart ACSIL study).
// Scenario logic S1-S8 + COMBO(S1+S4) is a direct port of the original; signals are evaluated on CLOSED bars only.
// Plots (+1 long / -1 short / 0 none), written on the first tick of the bar AFTER the signal bar (Calculate = OnEachTick):
//   0 ComboSignal  1 S1Signal  2 S2Signal  3 S4Signal  4 S5Signal  5 S6Signal  6 S7Signal  7 TrendState  8 HugFlag  9 StandAsideFlag  10 EmaLine
// NOTE: backtests on NQ 1-minute data showed NO edge for COMBO or the other scenarios. Forward-test on SIM only.
#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
	// Streaming port of TO_EMA_Bounce (Sierra Chart ACSIL). One call to Add() per CLOSED bar.
	internal struct EbOut
	{
		public int Trend, S1, S2, S3, S4, S5, S6, S7, S8, Combo;
	}

	internal class SneakyEmaBounceCore
	{
		public int EmaLen = 21, SlopeBars = 5, TouchTicks = 1, AvgLen = 20;
		public double MinSlope = 3.0, Tick = 0.25;
		public bool S1On = true, S1Color = true, S2On = true, S3On = true, S3Suppress = true, S4On = true,
					S5On = true, S6On = true, S7On = true, S8On = true, S8Suppress = true;
		public double S1MinPct = 60.0, S6Mult = 1.8, S6BodyPct = 60.0, S7Mult = 3.0;
		public int S1Sep = 10, S1Reset = 5, S2Look = 5, S2Reset = 5, HugWin = 8, HugMin = 6, S3Reset = 10,
				   MaxPB = 2, S4Reset = 5, S5Window = 10, S5Beyond = 1, S5Reset = 10, S6Reset = 10,
				   S7Reset = 10, CrossWin = 12, MaxCrosses = 3, S8Reset = 10;

		private const int NEVER = -1000000;
		private readonly System.Collections.Generic.List<double> o = new System.Collections.Generic.List<double>();
		private readonly System.Collections.Generic.List<double> h = new System.Collections.Generic.List<double>();
		private readonly System.Collections.Generic.List<double> l = new System.Collections.Generic.List<double>();
		private readonly System.Collections.Generic.List<double> c = new System.Collections.Generic.List<double>();
		private readonly System.Collections.Generic.List<double> ema = new System.Collections.Generic.List<double>();

		private int lastS1 = NEVER, lastS2 = NEVER, lastS3 = NEVER, lastS4 = NEVER, lastS5 = NEVER, lastS6 = NEVER, lastS7 = NEVER, lastS8 = NEVER;
		private int legDir, pbCount, prevHug, prevAside, prevExt, zoneActive, hugEnd = -1;
		private double zoneHigh, zoneLow;

		public double LastEma { get { return ema.Count > 0 ? ema[ema.Count - 1] : double.NaN; } }

		private static int Max(int a, int b) { return a > b ? a : b; }

		public EbOut Add(double O, double H, double L, double C)
		{
			EbOut r = new EbOut();
			o.Add(O); h.Add(H); l.Add(L); c.Add(C);
			double alpha = 2.0 / (EmaLen + 1);
			ema.Add(ema.Count == 0 ? C : alpha * C + (1 - alpha) * ema[ema.Count - 1]);

			int i = c.Count - 1;
			int minIndex = Max(Max(Max(EmaLen, SlopeBars), Max(HugWin, CrossWin + 1)), Max(Max(AvgLen, S1Sep), S2Look)) + 1;
			if (i < minIndex)
				return r;

			double tol = TouchTicks * Tick;
			int hugMin = HugMin > HugWin ? HugWin : HugMin;
			double oo = o[i], hh = h[i], ll = l[i], cc = c[i];
			double rng = hh - ll, em = ema[i];

			double slope = (ema[i] - ema[i - SlopeBars]) / Tick;
			int trend = 0;
			if (MinSlope > 0)
			{
				if (slope >= MinSlope) trend = 1;
				else if (slope <= -MinSlope) trend = -1;
			}
			else
			{
				if (slope > 0) trend = 1; else if (slope < 0) trend = -1;
			}
			r.Trend = trend;

			double sumR = 0;
			for (int j = i - AvgLen; j < i; j++) sumR += h[j] - l[j];
			double avgRange = sumR / AvgLen;

			int touching = 0;
			for (int j = i - HugWin + 1; j <= i; j++)
				if (l[j] <= ema[j] + tol && h[j] >= ema[j] - tol) touching++;
			bool hug = touching >= hugMin;

			int crosses = 0;
			for (int j = i - CrossWin + 1; j <= i; j++)
			{
				double d0 = c[j] - ema[j], d1 = c[j - 1] - ema[j - 1];
				if ((d0 > 0 && d1 < 0) || (d0 < 0 && d1 > 0)) crosses++;
			}
			bool aside = crosses >= MaxCrosses;

			if (trend != 0 && trend != legDir) { legDir = trend; pbCount = 0; }

			// S5 hug breakout
			if (S5On)
			{
				if (zoneActive != 0)
				{
					int d = 0;
					if (cc > zoneHigh + S5Beyond * Tick) d = 1;
					else if (cc < zoneLow - S5Beyond * Tick) d = -1;
					if (d != 0)
					{
						zoneActive = 0; legDir = d; pbCount = 0;
						if (i - lastS5 >= S5Reset) { lastS5 = i; r.S5 = d; }
					}
				}
				if (hug && prevHug == 0)
				{
					zoneHigh = double.MinValue; zoneLow = double.MaxValue;
					for (int j = i - HugWin + 1; j <= i; j++)
					{
						if (h[j] > zoneHigh) zoneHigh = h[j];
						if (l[j] < zoneLow) zoneLow = l[j];
					}
					zoneActive = 1; hugEnd = -1;
				}
				else if (hug && zoneActive != 0)
				{
					if (hh > zoneHigh) zoneHigh = hh;
					if (ll < zoneLow) zoneLow = ll;
				}
				else if (!hug && prevHug != 0 && zoneActive != 0)
					hugEnd = i;

				if (zoneActive != 0 && hugEnd >= 0 && (i - hugEnd) > S5Window)
					zoneActive = 0;
			}

			// S6 big candle through EMA
			if (S6On && avgRange > 0 && rng > 0)
			{
				bool big = rng >= S6Mult * avgRange && Math.Abs(cc - oo) >= (S6BodyPct / 100.0) * rng;
				int d = 0;
				if (big && oo < em && cc > em) d = 1;
				else if (big && oo > em && cc < em) d = -1;
				if (d != 0)
				{
					legDir = d; pbCount = 0;
					if (i - lastS6 >= S6Reset) { lastS6 = i; r.S6 = d; }
				}
			}

			// S1 EMA rejection
			int s1 = 0;
			if (S1On && trend != 0 && rng > 0)
			{
				if (trend == 1 && ll <= em + tol && cc > em)
				{
					double pct = 100.0 * (hh - (ll > em ? ll : em)) / rng;
					bool colorOk = !S1Color || cc > oo;
					bool sep = false;
					for (int j = i - S1Sep; j < i && !sep; j++) if (l[j] > ema[j] + tol) sep = true;
					if (pct >= S1MinPct && colorOk && sep) s1 = 1;
				}
				else if (trend == -1 && hh >= em - tol && cc < em)
				{
					double pct = 100.0 * ((hh < em ? hh : em) - ll) / rng;
					bool colorOk = !S1Color || cc < oo;
					bool sep = false;
					for (int j = i - S1Sep; j < i && !sep; j++) if (h[j] < ema[j] - tol) sep = true;
					if (pct >= S1MinPct && colorOk && sep) s1 = -1;
				}
			}

			// S2 cross + engulf
			int s2 = 0;
			if (S2On && trend != 0)
			{
				double po = o[i - 1], pc = c[i - 1];
				if (trend == 1 && cc > em && cc > oo && pc < po && cc >= po && oo <= pc)
				{
					bool crossed = false;
					for (int j = i - S2Look; j < i && !crossed; j++) if (c[j] < ema[j]) crossed = true;
					if (crossed) s2 = 1;
				}
				else if (trend == -1 && cc < em && cc < oo && pc > po && cc <= po && oo >= pc)
				{
					bool crossed = false;
					for (int j = i - S2Look; j < i && !crossed; j++) if (c[j] > ema[j]) crossed = true;
					if (crossed) s2 = -1;
				}
			}

			if ((S3On && S3Suppress && hug) || (S8On && S8Suppress && aside)) { s1 = 0; s2 = 0; }
			if (s1 != 0 && !(i - lastS1 >= S1Reset)) s1 = 0;
			if (s2 != 0 && !(i - lastS2 >= S2Reset)) s2 = 0;

			int pb = s1 != 0 ? s1 : s2;
			if (pb != 0)
			{
				if (s1 != 0) lastS1 = i;
				if (s2 != 0) lastS2 = i;
				if (pb != legDir) { legDir = pb; pbCount = 0; }
				pbCount++;

				bool over = S4On && MaxPB > 0 && pbCount > MaxPB;
				if (!over)
				{
					bool s4f = S4On && pbCount == 1 && (i - lastS4 >= S4Reset);
					bool comboBar = s4f && s1 != 0;
					if (comboBar) r.Combo = s1;
					if (s1 != 0) r.S1 = s1;
					if (s2 != 0) r.S2 = s2;
					if (s4f) { lastS4 = i; r.S4 = pb; }
				}
			}

			// S7 overextended
			int ext = 0;
			if (avgRange > 0)
			{
				double dist = cc - em;
				if (dist >= S7Mult * avgRange) ext = 1;
				else if (dist <= -S7Mult * avgRange) ext = -1;
			}
			if (S7On && ext != 0 && ext != prevExt && (i - lastS7 >= S7Reset)) { lastS7 = i; r.S7 = ext; }
			prevExt = ext;

			// S8 stand aside
			if (S8On && aside)
			{
				r.S8 = 1;
				if (prevAside == 0 && (i - lastS8 >= S8Reset)) lastS8 = i;
			}
			prevAside = aside ? 1 : 0;

			// S3 hug
			if (S3On && hug)
			{
				r.S3 = 1;
				if (prevHug == 0 && (i - lastS3 >= S3Reset)) lastS3 = i;
			}
			prevHug = hug ? 1 : 0;

			return r;
		}
	}


	public class SneakyEmaBounce : Indicator
	{
		private SneakyEmaBounceCore core;
		private EMA emaInd;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description		= "21 EMA trend-pullback scenarios S1-S8 and COMBO (port of TO_EMA_Bounce). Signals on closed bars; +1/-1 plots for automation.";
				Name			= "SneakyEmaBounce";
				Calculate		= Calculate.OnEachTick;
				IsOverlay		= true;
				DisplayInDataBox = true;
				PaintPriceMarkers = false;
				IsSuspendedWhileInactive = true;
				ArePlotsConfigurable = true;
				IsAutoScale		= false;
				ShowComboArrows	= true;
				ShowEmaLine		= true;
				MarkerOffsetTicks = 4;
				EmaLength = 21;
				SlopeLookback = 5;
				MinSlopeTicks = 3.0;
				TouchTolTicks = 1;
				AvgRangeBars = 20;
				S1Enable = true;
				S1MinTrendSidePct = 60.0;
				S1SeparationBars = 10;
				S1RequireColor = true;
				S1ResetBars = 5;
				S2Enable = true;
				S2CrossLookback = 5;
				S2ResetBars = 5;
				S3Enable = true;
				S3Window = 8;
				S3MinTouching = 6;
				S3Suppress = true;
				S3ResetBars = 10;
				S4Enable = true;
				S4MaxPullbacks = 2;
				S4ResetBars = 5;
				S5Enable = true;
				S5Window = 10;
				S5BeyondTicks = 1;
				S5ResetBars = 10;
				S6Enable = true;
				S6RangeMult = 1.8;
				S6MinBodyPct = 60.0;
				S6ResetBars = 10;
				S7Enable = true;
				S7RangeMult = 3.0;
				S7ResetBars = 10;
				S8Enable = true;
				S8Window = 12;
				S8MaxCrosses = 3;
				S8Suppress = true;
				S8ResetBars = 10;
				AddPlot(System.Windows.Media.Brushes.DodgerBlue, "ComboSignal");
				AddPlot(System.Windows.Media.Brushes.Transparent, "S1Signal");
				AddPlot(System.Windows.Media.Brushes.Transparent, "S2Signal");
				AddPlot(System.Windows.Media.Brushes.Transparent, "S4Signal");
				AddPlot(System.Windows.Media.Brushes.Transparent, "S5Signal");
				AddPlot(System.Windows.Media.Brushes.Transparent, "S6Signal");
				AddPlot(System.Windows.Media.Brushes.Transparent, "S7Signal");
				AddPlot(System.Windows.Media.Brushes.Transparent, "TrendState");
				AddPlot(System.Windows.Media.Brushes.Transparent, "HugFlag");
				AddPlot(System.Windows.Media.Brushes.Transparent, "StandAsideFlag");
				AddPlot(System.Windows.Media.Brushes.LimeGreen, "EmaLine");
			}
			else if (State == State.DataLoaded)
			{
				core = new SneakyEmaBounceCore
				{
					Tick = TickSize,
					EmaLen = EmaLength,
					SlopeBars = SlopeLookback,
					MinSlope = MinSlopeTicks,
					TouchTicks = TouchTolTicks,
					AvgLen = AvgRangeBars,
					S1On = S1Enable,
					S1MinPct = S1MinTrendSidePct,
					S1Sep = S1SeparationBars,
					S1Color = S1RequireColor,
					S1Reset = S1ResetBars,
					S2On = S2Enable,
					S2Look = S2CrossLookback,
					S2Reset = S2ResetBars,
					S3On = S3Enable,
					HugWin = S3Window,
					HugMin = S3MinTouching,
					S3Suppress = S3Suppress,
					S3Reset = S3ResetBars,
					S4On = S4Enable,
					MaxPB = S4MaxPullbacks,
					S4Reset = S4ResetBars,
					S5On = S5Enable,
					S5Window = S5Window,
					S5Beyond = S5BeyondTicks,
					S5Reset = S5ResetBars,
					S6On = S6Enable,
					S6Mult = S6RangeMult,
					S6BodyPct = S6MinBodyPct,
					S6Reset = S6ResetBars,
					S7On = S7Enable,
					S7Mult = S7RangeMult,
					S7Reset = S7ResetBars,
					S8On = S8Enable,
					CrossWin = S8Window,
					MaxCrosses = S8MaxCrosses,
					S8Suppress = S8Suppress,
					S8Reset = S8ResetBars,
				};
				emaInd = EMA(Close, EmaLength);
			}
		}

		protected override void OnBarUpdate()
		{
			if (core == null)
				return;

			Values[10][0] = ShowEmaLine ? emaInd[0] : double.NaN;

			// Default: score the bar that just closed ([1]) on the first tick of the new bar. OnBarClose: score [0].
			bool onClose = Calculate == Calculate.OnBarClose;
			if (!onClose && !IsFirstTickOfBar)
				return;
			int ago = onClose ? 0 : 1;
			if (CurrentBar < ago)
				return;

			EbOut r = core.Add(Open[ago], High[ago], Low[ago], Close[ago]);

			Values[0][0] = r.Combo;
			Values[1][0] = r.S1;
			Values[2][0] = r.S2;
			Values[3][0] = r.S4;
			Values[4][0] = r.S5;
			Values[5][0] = r.S6;
			Values[6][0] = r.S7;
			Values[7][0] = r.Trend;
			Values[8][0] = r.S3;
			Values[9][0] = r.S8;

			if (ShowComboArrows && r.Combo != 0)
			{
				double off = MarkerOffsetTicks * TickSize;
				if (r.Combo > 0)
					Draw.ArrowUp(this, "EbL" + CurrentBar, false, ago, Low[ago] - off, System.Windows.Media.Brushes.DodgerBlue);
				else
					Draw.ArrowDown(this, "EbS" + CurrentBar, false, ago, High[ago] + off, System.Windows.Media.Brushes.OrangeRed);
			}
		}

		#region Properties

		[Display(Name = "Show COMBO Arrows", Description = "Draw an arrow on bars flagged COMBO (S1 rejection that is also the first pullback of a leg).", Order = 0, GroupName = "00. Display")]
		public bool ShowComboArrows { get; set; }

		[Display(Name = "Show EMA Line", Order = 1, GroupName = "00. Display")]
		public bool ShowEmaLine { get; set; }

		[Display(Name = "Marker Offset (ticks)", Order = 2, GroupName = "00. Display")]
		[Range(0, 100)]
		public int MarkerOffsetTicks { get; set; }

		[Display(Name = "EMA Length", Description = "EMA period.", Order = 0, GroupName = "01. General")]
		[Range(2, 500)]
		public int EmaLength { get; set; }

		[Display(Name = "Trend: EMA Slope Lookback (bars)", Description = "Bars over which the EMA slope is measured.", Order = 1, GroupName = "01. General")]
		[Range(1, 200)]
		public int SlopeLookback { get; set; }

		[Display(Name = "Trend: Min EMA Slope (ticks)", Description = "EMA must rise/fall at least this many ticks over the lookback to define a trend. Tuned for ES 2000-tick bars; NQ 1-minute bars need roughly 4x.", Order = 2, GroupName = "01. General")]
		[Range(0, 1000)]
		public double MinSlopeTicks { get; set; }

		[Display(Name = "EMA Touch Tolerance (ticks)", Description = "How close counts as touching the EMA.", Order = 3, GroupName = "01. General")]
		[Range(0, 100)]
		public int TouchTolTicks { get; set; }

		[Display(Name = "Average Bar Range Length (bars)", Description = "Bars used for the average range (S6, S7).", Order = 4, GroupName = "01. General")]
		[Range(2, 500)]
		public int AvgRangeBars { get; set; }

		[Display(Name = "Enable", Description = "Trend pullback touches the EMA and closes back on the trend side.", Order = 0, GroupName = "02. S1 EMA Rejection")]
		public bool S1Enable { get; set; }

		[Display(Name = "Min % of Candle on Trend Side", Description = "", Order = 1, GroupName = "02. S1 EMA Rejection")]
		[Range(0, 100)]
		public double S1MinTrendSidePct { get; set; }

		[Display(Name = "Prior Separation Lookback (bars)", Description = "Price must have been clear of the EMA within this many bars.", Order = 2, GroupName = "02. S1 EMA Rejection")]
		[Range(1, 200)]
		public int S1SeparationBars { get; set; }

		[Display(Name = "Require Trend-Colored Candle", Description = "", Order = 3, GroupName = "02. S1 EMA Rejection")]
		public bool S1RequireColor { get; set; }

		[Display(Name = "Reset Bars", Description = "Cooldown after a signal.", Order = 4, GroupName = "02. S1 EMA Rejection")]
		[Range(0, 1000)]
		public int S1ResetBars { get; set; }

		[Display(Name = "Enable", Description = "Wrong-side close, then an engulfing candle back on the trend side.", Order = 0, GroupName = "03. S2 Cross + Engulf")]
		public bool S2Enable { get; set; }

		[Display(Name = "Wrong-Side Close Lookback (bars)", Description = "", Order = 1, GroupName = "03. S2 Cross + Engulf")]
		[Range(1, 100)]
		public int S2CrossLookback { get; set; }

		[Display(Name = "Reset Bars", Description = "", Order = 2, GroupName = "03. S2 Cross + Engulf")]
		[Range(0, 1000)]
		public int S2ResetBars { get; set; }

		[Display(Name = "Enable", Description = "Chop warning: price sits on the EMA.", Order = 0, GroupName = "04. S3 EMA Hug")]
		public bool S3Enable { get; set; }

		[Display(Name = "Hug Window (bars)", Description = "", Order = 1, GroupName = "04. S3 EMA Hug")]
		[Range(2, 200)]
		public int S3Window { get; set; }

		[Display(Name = "Min Bars Touching EMA in Window", Description = "", Order = 2, GroupName = "04. S3 EMA Hug")]
		[Range(1, 200)]
		public int S3MinTouching { get; set; }

		[Display(Name = "Suppress S1/S2 During Hug", Description = "", Order = 3, GroupName = "04. S3 EMA Hug")]
		public bool S3Suppress { get; set; }

		[Display(Name = "Reset Bars", Description = "", Order = 4, GroupName = "04. S3 EMA Hug")]
		[Range(0, 1000)]
		public int S3ResetBars { get; set; }

		[Display(Name = "Enable (also limits pullbacks per leg)", Description = "Required for COMBO.", Order = 0, GroupName = "05. S4 First Pullback")]
		public bool S4Enable { get; set; }

		[Display(Name = "Max Pullback Signals per Trend Leg (0 = unlimited)", Description = "", Order = 1, GroupName = "05. S4 First Pullback")]
		[Range(0, 100)]
		public int S4MaxPullbacks { get; set; }

		[Display(Name = "Reset Bars", Description = "", Order = 2, GroupName = "05. S4 First Pullback")]
		[Range(0, 1000)]
		public int S4ResetBars { get; set; }

		[Display(Name = "Enable", Description = "Close beyond the hug zone starts a new trend leg.", Order = 0, GroupName = "06. S5 Hug Breakout")]
		public bool S5Enable { get; set; }

		[Display(Name = "Breakout Window After Hug Ends (bars)", Description = "", Order = 1, GroupName = "06. S5 Hug Breakout")]
		[Range(0, 500)]
		public int S5Window { get; set; }

		[Display(Name = "Close Beyond Hug Zone (ticks)", Description = "", Order = 2, GroupName = "06. S5 Hug Breakout")]
		[Range(0, 100)]
		public int S5BeyondTicks { get; set; }

		[Display(Name = "Reset Bars", Description = "", Order = 3, GroupName = "06. S5 Hug Breakout")]
		[Range(0, 1000)]
		public int S5ResetBars { get; set; }

		[Display(Name = "Enable", Description = "Wide candle that opens on one side of the EMA and closes on the other.", Order = 0, GroupName = "07. S6 Big Candle")]
		public bool S6Enable { get; set; }

		[Display(Name = "Min Range (x Avg Bar Range)", Description = "", Order = 1, GroupName = "07. S6 Big Candle")]
		[Range(0.1, 50)]
		public double S6RangeMult { get; set; }

		[Display(Name = "Min Body % of Range", Description = "", Order = 2, GroupName = "07. S6 Big Candle")]
		[Range(0, 100)]
		public double S6MinBodyPct { get; set; }

		[Display(Name = "Reset Bars", Description = "", Order = 3, GroupName = "07. S6 Big Candle")]
		[Range(0, 1000)]
		public int S6ResetBars { get; set; }

		[Display(Name = "Enable", Description = "Close far from the EMA (do not chase).", Order = 0, GroupName = "08. S7 Overextended")]
		public bool S7Enable { get; set; }

		[Display(Name = "Close-to-EMA Distance (x Avg Bar Range)", Description = "", Order = 1, GroupName = "08. S7 Overextended")]
		[Range(0.1, 100)]
		public double S7RangeMult { get; set; }

		[Display(Name = "Reset Bars", Description = "", Order = 2, GroupName = "08. S7 Overextended")]
		[Range(0, 1000)]
		public int S7ResetBars { get; set; }

		[Display(Name = "Enable", Description = "Repeated EMA crosses in a short window.", Order = 0, GroupName = "09. S8 Stand Aside")]
		public bool S8Enable { get; set; }

		[Display(Name = "Cross Count Window (bars)", Description = "", Order = 1, GroupName = "09. S8 Stand Aside")]
		[Range(2, 500)]
		public int S8Window { get; set; }

		[Display(Name = "Crosses in Window to Stand Aside", Description = "", Order = 2, GroupName = "09. S8 Stand Aside")]
		[Range(1, 100)]
		public int S8MaxCrosses { get; set; }

		[Display(Name = "Suppress S1/S2 While Standing Aside", Description = "", Order = 3, GroupName = "09. S8 Stand Aside")]
		public bool S8Suppress { get; set; }

		[Display(Name = "Reset Bars", Description = "", Order = 4, GroupName = "09. S8 Stand Aside")]
		[Range(0, 1000)]
		public int S8ResetBars { get; set; }

		#endregion

		#region Plot series (for automation tools)

		[Browsable(false)] [XmlIgnore] public Series<double> ComboSignal { get { Update(); return Values[0]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> S1Signal { get { Update(); return Values[1]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> S2Signal { get { Update(); return Values[2]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> S4Signal { get { Update(); return Values[3]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> S5Signal { get { Update(); return Values[4]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> S6Signal { get { Update(); return Values[5]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> S7Signal { get { Update(); return Values[6]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> TrendState { get { Update(); return Values[7]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> HugFlag { get { Update(); return Values[8]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> StandAsideFlag { get { Update(); return Values[9]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> EmaLine { get { Update(); return Values[10]; } }

		#endregion
	}
}
