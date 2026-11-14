// SneakyPrecisionLevels: copy of the third-party "PrecisionLevels" indicator (levels/rendering untouched, types renamed so both can coexist)
// plus signal / level plots for automation tools (see "16. Signals"). The signal rules are UNVALIDATED: backtests on NQ 1-minute data showed no edge.
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using SharpDX;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
	[TypeConverter("NinjaTrader.NinjaScript.Indicators.SneakyPrecisionLevelsConverter")]
	public class SneakyPrecisionLevels : Indicator
	{
		#region Fields

		public override string DisplayName
		{
			get { return "Sneaky Precision Levels"; }
		}

		private int					calcBip;
		private bool				minuteSeriesAdded;
		private TimeSpan			barSpan;
		private TimeSpan			anchorTolerance;

		private TimeZoneInfo		chartTz;
		private TimeZoneInfo		refTz;
		private bool				sameTz;

		private SessionIterator		sessionIterator;
		private SPLSession			cur;
		private readonly List<SPLSession> sessions = new List<SPLSession>();
		private DateTime			firstTradingDaySeen	= DateTime.MinValue;
		private int					sessionsObserved;

		private double				prevRthVwap			= double.NaN;
		private double				prevRthVwapSd;
		private bool				prevRthVwapValid;
		private bool				prevRthVwapAnchored;

		private DateTime			weekKey				= DateTime.MinValue;
		private DateTime			weekBegin;
		private double				weekOpen			= double.NaN;
		private double				weekHigh			= double.MinValue;
		private double				weekLow				= double.MaxValue;
		private double				weekClose			= double.NaN;
		private double				weekCumPv;
		private double				weekCumVol;
		private double				weekCumPv2;
		private Dictionary<int, double> weekLadder		= new Dictionary<int, double>();
		private double				weekLadderMax		= double.MinValue;
		private int					weekLadderMaxKey;
		private bool				weekVaValid;
		private double				weekPoc;
		private double				weekVah;
		private double				weekVal;
		private bool				weekAnchorFound;
		private bool				weekAnchorPending;

		private DateTime			prevWeekKey			= DateTime.MinValue;
		private DateTime			prevWeekBegin;
		private double				prevWeekOpen		= double.NaN;
		private double				prevWeekHigh		= double.MinValue;
		private double				prevWeekLow			= double.MaxValue;
		private double				prevWeekClose		= double.NaN;
		private double				prevWeekVwap		= double.NaN;
		private double				prevWeekVwapSd;
		private bool				prevWeekVaValid;
		private double				prevWeekPoc;
		private double				prevWeekVah;
		private double				prevWeekVal;
		private bool				prevWeekAnchorFound;

		private int					monthKey			= -1;
		private DateTime			monthBegin;
		private DateTime			monthFirstDay		= DateTime.MinValue;
		private double				monthOpen			= double.NaN;
		private double				monthHigh			= double.MinValue;
		private double				monthLow			= double.MaxValue;
		private double				monthClose			= double.NaN;
		private bool				monthAnchorFound;
		private bool				monthAnchorPending;

		private int					prevMonthKey		= -1;
		private DateTime			prevMonthBegin;
		private DateTime			prevMonthFirstDay	= DateTime.MinValue;
		private double				prevMonthOpen		= double.NaN;
		private double				prevMonthHigh		= double.MinValue;
		private double				prevMonthLow		= double.MaxValue;
		private double				prevMonthClose		= double.NaN;
		private bool				prevMonthAnchorFound;

		private Dictionary<int, double>	compA			= new Dictionary<int, double>();
		private double				compAMax			= double.MinValue;
		private int					compAMaxKey;
		private DateTime			compABegin;
		private bool				compAValid;
		private int					compACount;

		private Dictionary<int, double>	compB			= new Dictionary<int, double>();
		private double				compBMax			= double.MinValue;
		private int					compBMaxKey;
		private DateTime			compBBegin;
		private bool				compBValid;
		private int					compBCount;

		private readonly List<SPLRow>	rows		= new List<SPLRow>();
		private readonly List<int>		mergeIdx	= new List<int>();
		private readonly List<float>	mergeY		= new List<float>();
		private readonly List<SPLChip>	chips		= new List<SPLChip>();
		private readonly List<SPLRow>	chipRows	= new List<SPLRow>();
		private static readonly System.Windows.Media.Brush cardBorderBrush = Hex("#9A96A8");

		private float				mouseX;
		private float				mouseY;
		private volatile bool		mouseValid;
		private bool				mouseHooked;
		private int					hoveredChip		= -1;
		private readonly List<int>		keyBuffer	= new List<int>();
		private readonly Dictionary<string, SharpDX.DirectWrite.TextLayout> layoutCache =
			new Dictionary<string, SharpDX.DirectWrite.TextLayout>();
		private SharpDX.DirectWrite.TextFormat	labelFormat;
		private SPLLabelAnchor		labelAnchor		= SPLLabelAnchor.RightEdge;
		private int					cachedFontSize	= -1;
		private string				cachedFontFamily;

		private double				rowSize			= 0.25;
		private bool				engineReady;
		private bool				vaDirty;

		#endregion

		#region State

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "Multi-timeframe key levels: daily, weekly and monthly structure, VWAP with standard deviation bands, developing and previous value areas, multi-day POC and time based intraday markers.";
				Name						= "SneakyPrecisionLevels";
				Calculate					= Calculate.OnEachTick;
				IsOverlay					= true;
				DrawOnPricePanel			= true;
				DisplayInDataBox			= true;
				PaintPriceMarkers			= false;
				IsSuspendedWhileInactive	= true;
				ArePlotsConfigurable		= true;
				IsAutoScale					= false;

				UseMinuteSeries			= true;
				StrictGating			= true;
				TimeReference			= SPLTimeReference.Eastern;
				MaxLookbackDays			= 20;
				ValueAreaPercent		= 70;
				ProfileScope			= SPLProfileScope.WholeSession;
				ProfileRowTicks			= 1;
				VolumeSpread			= SPLVolumeSpread.SpreadHighLow;

				ShowPrevDayHighLow		= true;
				ShowPrevDayClose		= true;
				ShowPrevDayOpen			= false;
				ShowPrevDayMid			= true;
				ShowDevelopingDay		= true;
				PrevDayCount			= 1;
				ShowDayAge				= true;

				ShowPrevWeek			= true;
				ShowPrevWeekMid			= false;
				ShowDevelopingWeek		= true;

				ShowPrevMonth			= true;
				ShowPrevMonthMid		= false;
				ShowDevelopingMonth		= false;

				ShowVwap				= true;
				VwapRthOnly				= false;
				ShowPrevRthVwap			= true;
				VwapSource				= SPLPriceSource.Typical;
				ShowVwapBand1			= true;
				VwapBand1Mult			= 1.0;
				ShowVwapBand2			= true;
				VwapBand2Mult			= 2.0;
				ShowVwapBand3			= false;
				VwapBand3Mult			= 3.0;

				ShowWeeklyVwap			= false;
				ShowWeeklyVwapBand1		= false;
				WeeklyVwapBand1Mult		= 1.0;
				ShowWeeklyVwapBand2		= false;
				WeeklyVwapBand2Mult		= 2.0;

				ShowDayValueArea		= true;
				ShowDayPoc				= true;
				ShowPrevDayValueArea	= true;
				ShowPrevDayPoc			= true;
				PrevValueAreaCount		= 1;
				ShowValueAreaAge		= true;
				ShowWeekValueArea		= false;
				ShowWeekPoc				= true;
				ShowPrevWeekValueArea	= false;
				ShowPrevWeekPoc			= false;

				ShowCompositeA			= true;
				CompositeADays			= 5;
				ShowCompositeB			= false;
				CompositeBDays			= 20;

				RthOpenHhmm				= 930;
				RthCloseHhmm			= 1600;
				ShowOpeningRange		= true;
				OpeningRangeMinutes		= 15;
				ShowInitialBalance		= true;
				InitialBalanceMinutes	= 60;
				ShowOvernight			= true;
				ShowLondon				= false;
				LondonOpenHhmm			= 300;
				LondonCloseHhmm			= 1130;

				ShowGap					= true;
				HideFilledGap			= true;

				LineStartMode			= SPLLineStart.LevelOrigin;
				LineExtensionPx			= 40;
				LineFillPercent			= 80;

				ShowLevelName			= true;
				ShowLevelValue			= true;
				LabelAnchor				= SPLLabelAnchor.RightEdge;
				LabelFontSize			= 11;
				LabelPaddingPx			= 4;
				LabelMaxColumns			= 6;
				StaircaseZigzag			= true;
				LabelOverlap			= SPLLabelOverlap.Merge;
				LabelMergePadPx			= 2;
				ShowMergeDots			= true;
				ShowMergeCard			= true;

				PrevDayHighLowStroke	= new Stroke(Hex("#E0A458"), DashStyleHelper.Solid, 1.4f);
				PrevDayCloseStroke		= new Stroke(Hex("#B98A4E"), DashStyleHelper.Dash,  1.2f);
				PrevDayOpenStroke		= new Stroke(Hex("#8C6D3F"), DashStyleHelper.Dot,   1f);
				PrevDayMidStroke		= new Stroke(Hex("#7A6340"), DashStyleHelper.Dot,   1f);
				DevelopingDayStroke		= new Stroke(Hex("#5B8DEF"), DashStyleHelper.Dot,   1.2f);

				WeekStroke				= new Stroke(Hex("#7BD389"), DashStyleHelper.Dot,   1.2f);
				PrevWeekStroke			= new Stroke(Hex("#4E9B5F"), DashStyleHelper.Dash,  1.4f);
				MonthStroke				= new Stroke(Hex("#C084FC"), DashStyleHelper.Dot,   1.2f);
				PrevMonthStroke			= new Stroke(Hex("#8B5CF6"), DashStyleHelper.Dash,  1.4f);

				VwapStroke				= new Stroke(Hex("#E8E4D9"), DashStyleHelper.Solid, 2f);
				VwapBand1Stroke			= new Stroke(Hex("#9A96A8"), DashStyleHelper.Dash,  1f);
				VwapBand2Stroke			= new Stroke(Hex("#77738A"), DashStyleHelper.Dash,  1f);
				VwapBand3Stroke			= new Stroke(Hex("#5C596E"), DashStyleHelper.Dot,   1f);
				PrevVwapStroke			= new Stroke(Hex("#A9A296"), DashStyleHelper.Dash,  1.4f);
				WeeklyVwapStroke		= new Stroke(Hex("#9AD1D4"), DashStyleHelper.Solid, 1.6f);
				WeeklyVwapBandStroke	= new Stroke(Hex("#5F8E90"), DashStyleHelper.Dot,   1f);

				PocStroke				= new Stroke(Hex("#FF6B4A"), DashStyleHelper.Solid, 1.8f);
				ValueAreaStroke			= new Stroke(Hex("#FF9F6B"), DashStyleHelper.Dash,  1.2f);
				PrevPocStroke			= new Stroke(Hex("#C4553B"), DashStyleHelper.Dash,  1.6f);
				PrevValueAreaStroke		= new Stroke(Hex("#9C5A45"), DashStyleHelper.Dot,   1.2f);
				WeekPocStroke			= new Stroke(Hex("#4ECDC4"), DashStyleHelper.Solid, 1.6f);
				WeekValueAreaStroke		= new Stroke(Hex("#3A9C95"), DashStyleHelper.Dot,   1.2f);
				CompositeAStroke		= new Stroke(Hex("#FFD166"), DashStyleHelper.Solid, 1.6f);
				CompositeBStroke		= new Stroke(Hex("#EF476F"), DashStyleHelper.Solid, 1.6f);

				OpeningRangeStroke		= new Stroke(Hex("#06D6A0"), DashStyleHelper.Dot,   1.2f);
				InitialBalanceStroke	= new Stroke(Hex("#118AB2"), DashStyleHelper.Dash,  1.2f);
				OvernightStroke			= new Stroke(Hex("#8D99AE"), DashStyleHelper.Dot,   1.2f);
				LondonStroke			= new Stroke(Hex("#A78BFA"), DashStyleHelper.Dot,   1.2f);
				GapStroke				= new Stroke(Hex("#FF477E"), DashStyleHelper.Dash,  1.4f);

					SigGroupPrevDay			= true;
					SigGroupPrevValueArea	= true;
					SigGroupOvernight		= true;
					SigGroupOpeningRange	= true;
					SigGroupInitialBalance	= true;
					SigGroupVwap			= true;
					SigGroupPrevWeek		= true;
					SigStartHhmm			= 935;
					SigEndHhmm				= 1530;
					SigVwapFilter			= false;
					SigBreakBufferAtr		= 0.25;
					SigAtLevelTicks			= 8;

					AddPlot(System.Windows.Media.Brushes.DodgerBlue,	"ReactSignal");
					AddPlot(System.Windows.Media.Brushes.Orange,		"BreakSignal");
					AddPlot(System.Windows.Media.Brushes.Gold,			"Break2Signal");
					AddPlot(System.Windows.Media.Brushes.Transparent,	"SignalLevel");
					AddPlot(System.Windows.Media.Brushes.Transparent,	"NearestAbove");
					AddPlot(System.Windows.Media.Brushes.Transparent,	"NearestBelow");
					AddPlot(System.Windows.Media.Brushes.Transparent,	"DistAboveTicks");
					AddPlot(System.Windows.Media.Brushes.Transparent,	"DistBelowTicks");
					AddPlot(System.Windows.Media.Brushes.Transparent,	"AtLevel");
			}
			else if (State == State.Configure)
			{
				minuteSeriesAdded = false;
				if (UseMinuteSeries)
				{
					AddDataSeries(BarsPeriodType.Minute, 1);
					minuteSeriesAdded = true;
				}
			}
			else if (State == State.DataLoaded)
			{
				calcBip = minuteSeriesAdded ? 1 : 0;
				ResetEngine();
					atrInd = ATR(14);
					BarsPeriod pbp = BarsArray[0].BarsPeriod;
					if (pbp != null && pbp.BarsPeriodType == BarsPeriodType.Minute)
						primarySpan = TimeSpan.FromMinutes(Math.Max(1, pbp.Value));
					else if (pbp != null && pbp.BarsPeriodType == BarsPeriodType.Second)
						primarySpan = TimeSpan.FromSeconds(Math.Max(1, pbp.Value));
					else
						primarySpan = TimeSpan.Zero;
					snapValid = false;
			}
			else if (State == State.Realtime)
			{
				vaDirty = true;
			}
			else if (State == State.Terminated)
			{
				UnhookMouse();
				ReleaseRenderResources();
			}
		}

		private static System.Windows.Media.Brush Hex(string value)
		{
			System.Windows.Media.SolidColorBrush b = new System.Windows.Media.SolidColorBrush(
				(System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value));
			b.Freeze();
			return b;
		}

		private void ResetEngine()
		{
			engineReady = false;

			if (BarsArray == null || calcBip >= BarsArray.Length || BarsArray[calcBip] == null)
				return;

			Bars calcBars = BarsArray[calcBip];

			BarsPeriod bp = calcBars.BarsPeriod;
			if (bp == null)
				barSpan = TimeSpan.Zero;
			else if (bp.BarsPeriodType == BarsPeriodType.Minute)
				barSpan = TimeSpan.FromMinutes(Math.Max(1, bp.Value));
			else if (bp.BarsPeriodType == BarsPeriodType.Second)
				barSpan = TimeSpan.FromSeconds(Math.Max(1, bp.Value));
			else if (bp.BarsPeriodType == BarsPeriodType.Day)
				barSpan = TimeSpan.FromDays(Math.Max(1, bp.Value));
			else
				barSpan = TimeSpan.Zero;

			anchorTolerance = barSpan + TimeSpan.FromMinutes(1);
			if (anchorTolerance < TimeSpan.FromMinutes(2))
				anchorTolerance = TimeSpan.FromMinutes(2);

			chartTz = null;
			try { chartTz = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo; }
			catch { chartTz = null; }
			if (chartTz == null)
				chartTz = TimeZoneInfo.Local;

			refTz = chartTz;
			if (TimeReference == SPLTimeReference.Exchange)
			{
				try
				{
					if (calcBars.TradingHours != null && calcBars.TradingHours.TimeZoneInfo != null)
						refTz = calcBars.TradingHours.TimeZoneInfo;
				}
				catch { refTz = chartTz; }
			}
			else if (TimeReference == SPLTimeReference.Eastern)
			{
				try { refTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
				catch { refTz = chartTz; }
			}
			if (refTz == null)
				refTz = chartTz;
			sameTz = refTz.Equals(chartTz);

			try { sessionIterator = new SessionIterator(calcBars); }
			catch { sessionIterator = null; }

			rowSize = TickSize * Math.Max(1, ProfileRowTicks);
			if (rowSize <= 0)
				rowSize = 0.01;

			sessions.Clear();
			cur					= null;
			sessionsObserved	= 0;
			firstTradingDaySeen	= DateTime.MinValue;

			prevRthVwapValid	= false;
			prevRthVwapAnchored	= false;

			weekKey					= DateTime.MinValue;
			prevWeekKey				= DateTime.MinValue;
			ResetWeek(DateTime.MinValue, DateTime.MinValue);
			weekAnchorPending		= false;
			prevWeekHigh			= double.MinValue;
			prevWeekLow				= double.MaxValue;
			prevWeekVaValid			= false;
			prevWeekVwap			= double.NaN;
			prevWeekAnchorFound		= false;

			monthKey				= -1;
			prevMonthKey			= -1;
			ResetMonth(-1, DateTime.MinValue, DateTime.MinValue);
			monthAnchorPending		= false;
			prevMonthHigh			= double.MinValue;
			prevMonthLow			= double.MaxValue;
			prevMonthAnchorFound	= false;

			compA.Clear(); compAMax = double.MinValue; compAValid = false; compACount = 0;
			compB.Clear(); compBMax = double.MinValue; compBValid = false; compBCount = 0;

			vaDirty		= false;
			engineReady	= sessionIterator != null;
		}

		#endregion

		#region Time helpers

		private DateTime ToRef(DateTime chartTime)
		{
			if (sameTz || refTz == null || chartTz == null)
				return chartTime;
			try { return TimeZoneInfo.ConvertTime(chartTime, chartTz, refTz); }
			catch { return chartTime; }
		}

		private static TimeSpan HhmmSpan(int value)
		{
			if (value < 0)
				value = 0;
			int h = value / 100;
			int m = value % 100;
			if (h > 23) h = 23;
			if (m > 59) m = 59;
			return new TimeSpan(h, m, 0);
		}

		private static DateTime WeekStart(DateTime day)
		{
			int back = ((int)day.DayOfWeek + 6) % 7;
			return day.Date.AddDays(-back);
		}

		private double SourcePrice(double o, double h, double l, double c)
		{
			switch (VwapSource)
			{
				case SPLPriceSource.Close:		return c;
				case SPLPriceSource.Median:		return (h + l) * 0.5;
				case SPLPriceSource.Weighted:	return (h + l + c + c) * 0.25;
				default:						return (h + l + c) / 3.0;
			}
		}

		private static double StdDevOf(double cumPv2, double cumVol, double mean)
		{
			if (cumVol <= 0)
				return 0;
			double variance = (cumPv2 / cumVol) - (mean * mean);
			if (variance < 0)
				variance = 0;
			return Math.Sqrt(variance);
		}

		private int KeyOf(double price)
		{
			return (int)Math.Round(price / rowSize, MidpointRounding.AwayFromZero);
		}

		private double PriceOfKey(int key)
		{
			return Instrument.MasterInstrument.RoundToTickSize(key * rowSize);
		}

		#endregion

		#region Bar processing

		protected override void OnBarUpdate()
		{
			if (!engineReady)
				return;

			// Signal plots are written on the primary series only.
			if (calcBip == 1 && BarsInProgress == 0)
			{
				if (IsFirstTickOfBar)
				{
					SignalEvaluate();
					SignalRoll();
				}
				return;
			}

			if (calcBip == 0 && BarsInProgress == 0 && IsFirstTickOfBar)
				SignalEvaluate();

			EngineStep();

			if (calcBip == 0 && BarsInProgress == 0 && IsFirstTickOfBar)
				SignalRoll();
		}

		private void EngineStep()
		{
			if (BarsInProgress != calcBip)
				return;
			if (CurrentBars == null || calcBip >= CurrentBars.Length || CurrentBars[calcBip] < 0)
				return;

			int closedIdx = -1;
			if (Calculate == Calculate.OnBarClose)
				closedIdx = 0;
			else if (IsFirstTickOfBar && CurrentBars[calcBip] >= 1)
				closedIdx = 1;

			if (closedIdx >= 0)
				ProcessClosedBar(closedIdx);

			ProcessLiveBar();
		}

		private void ProcessClosedBar(int idx)
		{
			DateTime tBar = Times[calcBip][idx];

			if (cur != null && cur.LastBarTime != DateTime.MinValue && tBar <= cur.LastBarTime)
				return;

			EnsureSession(tBar);
			if (cur == null)
				return;

			ApplyBar(tBar,
				Opens[calcBip][idx],
				Highs[calcBip][idx],
				Lows[calcBip][idx],
				Closes[calcBip][idx],
				Volumes[calcBip][idx],
				true);

			cur.LastBarTime = tBar;
			vaDirty = true;
		}

		private void ProcessLiveBar()
		{
			DateTime t = Times[calcBip][0];

			if (cur == null)
				EnsureSession(t);
			if (cur == null)
				return;
			if (t <= cur.Begin || t > cur.End)
				return;

			ApplyBar(t,
				Opens[calcBip][0],
				Highs[calcBip][0],
				Lows[calcBip][0],
				Closes[calcBip][0],
				0,
				false);
		}

		private void EnsureSession(DateTime tBar)
		{
			if (sessionIterator == null)
				return;
			if (cur != null && tBar <= cur.End)
				return;

			DateTime begin;
			DateTime end;
			try
			{
				sessionIterator.GetNextSession(tBar, true);
				begin	= sessionIterator.ActualSessionBegin;
				end		= sessionIterator.ActualSessionEnd;
			}
			catch
			{
				return;
			}

			if (end <= begin)
				return;

			if (cur != null && begin == cur.Begin)
			{
				cur.End = end;
				return;
			}

			StartSession(begin, end, tBar);
		}

		private void StartSession(DateTime begin, DateTime end, DateTime tBar)
		{
			FinalizeSession();

			DateTime tradingDay = ToRef(end).AddTicks(-1).Date;

			RollWeekAndMonth(tradingDay, begin);

			SPLSession s			= new SPLSession();
			s.Begin				= begin;
			s.End				= end;
			s.TradingDay		= tradingDay;
			s.FirstBarTime		= tBar;
			s.LastBarTime		= DateTime.MinValue;
			s.Ladder			= new Dictionary<int, double>();
			s.AnchorFound		= sessionsObserved > 0 || (tBar - begin) <= anchorTolerance;

			s.RthOpenRef		= tradingDay.Add(HhmmSpan(RthOpenHhmm));
			s.RthCloseRef		= tradingDay.Add(HhmmSpan(RthCloseHhmm));
			if (s.RthCloseRef <= s.RthOpenRef)
				s.RthCloseRef = s.RthCloseRef.AddDays(1);

			s.OrEndRef			= s.RthOpenRef.AddMinutes(Math.Max(1, OpeningRangeMinutes));
			s.IbEndRef			= s.RthOpenRef.AddMinutes(Math.Max(1, InitialBalanceMinutes));

			s.LondonBeginRef	= tradingDay.Add(HhmmSpan(LondonOpenHhmm));
			s.LondonEndRef		= tradingDay.Add(HhmmSpan(LondonCloseHhmm));
			if (s.LondonEndRef <= s.LondonBeginRef)
				s.LondonEndRef = s.LondonEndRef.AddDays(1);

			cur = s;
			sessionsObserved++;

			if (weekAnchorPending)
			{
				weekAnchorFound		= s.AnchorFound;
				weekAnchorPending	= false;
			}
			if (monthAnchorPending)
			{
				monthAnchorFound	= s.AnchorFound;
				monthAnchorPending	= false;
			}

			if (firstTradingDaySeen == DateTime.MinValue)
				firstTradingDaySeen = tradingDay;

			compACount = RebuildComposite(compA, CompositeADays, ref compAMax, ref compAMaxKey, ref compABegin);
			compBCount = RebuildComposite(compB, CompositeBDays, ref compBMax, ref compBMaxKey, ref compBBegin);
			compAValid = true;
			compBValid = true;

			vaDirty = true;
		}

		private void FinalizeSession()
		{
			if (cur == null)
				return;

			ComputeSessionValueArea(cur);
			cur.Complete = true;

			if (cur.RthCumVol > 0)
			{
				prevRthVwap			= cur.RthCumPv / cur.RthCumVol;
				prevRthVwapSd		= StdDevOf(cur.RthCumPv2, cur.RthCumVol, prevRthVwap);
				prevRthVwapValid	= true;
				prevRthVwapAnchored	= cur.AnchorFound && cur.RthAnchorFound;
			}
			else if (cur.CumVol > 0)
			{
				prevRthVwap			= cur.CumPv / cur.CumVol;
				prevRthVwapSd		= StdDevOf(cur.CumPv2, cur.CumVol, prevRthVwap);
				prevRthVwapValid	= true;
				prevRthVwapAnchored	= cur.AnchorFound;
			}
			else
				prevRthVwapValid	= false;

			sessions.Add(cur);
			TrimSessions();
			cur = null;
		}

		private void RollWeekAndMonth(DateTime tradingDay, DateTime sessionBegin)
		{
			DateTime wk = WeekStart(tradingDay);
			if (weekKey == DateTime.MinValue)
				ResetWeek(wk, sessionBegin);
			else if (wk != weekKey)
			{
				SnapshotWeek();
				ResetWeek(wk, sessionBegin);
			}

			int mk = tradingDay.Year * 100 + tradingDay.Month;
			DateTime firstOfMonth = new DateTime(tradingDay.Year, tradingDay.Month, 1);
			if (monthKey == -1)
				ResetMonth(mk, sessionBegin, firstOfMonth);
			else if (mk != monthKey)
			{
				SnapshotMonth();
				ResetMonth(mk, sessionBegin, firstOfMonth);
			}
		}

		private void ResetWeek(DateTime key, DateTime begin)
		{
			weekKey			= key;
			weekBegin		= begin;
			weekOpen		= double.NaN;
			weekHigh		= double.MinValue;
			weekLow			= double.MaxValue;
			weekClose		= double.NaN;
			weekCumPv		= 0;
			weekCumVol		= 0;
			weekCumPv2		= 0;
			weekLadder		= new Dictionary<int, double>();
			weekLadderMax	= double.MinValue;
			weekLadderMaxKey= 0;
			weekVaValid		= false;
			weekAnchorFound		= false;
			weekAnchorPending	= true;
		}

		private void SnapshotWeek()
		{
			prevWeekAnchorFound = weekAnchorFound;
			prevWeekKey		= weekKey;
			prevWeekBegin	= weekBegin;
			prevWeekOpen	= weekOpen;
			prevWeekHigh	= weekHigh;
			prevWeekLow		= weekLow;
			prevWeekClose	= weekClose;

			if (weekCumVol > 0)
			{
				prevWeekVwap	= weekCumPv / weekCumVol;
				prevWeekVwapSd	= StdDevOf(weekCumPv2, weekCumVol, prevWeekVwap);
			}
			else
			{
				prevWeekVwap	= double.NaN;
				prevWeekVwapSd	= 0;
			}

			int pk, vk, lk;
			if (ComputeValueArea(weekLadder, weekLadderMaxKey, ValueAreaFraction, out pk, out vk, out lk))
			{
				prevWeekPoc		= PriceOfKey(pk);
				prevWeekVah		= PriceOfKey(vk);
				prevWeekVal		= PriceOfKey(lk);
				prevWeekVaValid	= true;
			}
			else
				prevWeekVaValid = false;
		}

		private void ResetMonth(int key, DateTime begin, DateTime firstDay)
		{
			monthKey		= key;
			monthBegin		= begin;
			monthFirstDay	= firstDay;
			monthOpen		= double.NaN;
			monthHigh		= double.MinValue;
			monthLow		= double.MaxValue;
			monthClose		= double.NaN;
			monthAnchorFound	= false;
			monthAnchorPending	= true;
		}

		private void SnapshotMonth()
		{
			prevMonthAnchorFound = monthAnchorFound;
			prevMonthKey		= monthKey;
			prevMonthBegin		= monthBegin;
			prevMonthFirstDay	= monthFirstDay;
			prevMonthOpen		= monthOpen;
			prevMonthHigh		= monthHigh;
			prevMonthLow		= monthLow;
			prevMonthClose		= monthClose;
		}

		private void ApplyBar(DateTime tBar, double o, double h, double l, double c, double v, bool closed)
		{
			SPLSession s = cur;
			if (s == null)
				return;

			DateTime rEnd	= ToRef(tBar);
			DateTime rStart	= rEnd - barSpan;

			if (double.IsNaN(s.Open))
			{
				s.Open			= o;
				s.FirstBarTime	= tBar;
			}
			if (h > s.High) s.High = h;
			if (l < s.Low)  s.Low  = l;
			s.Close = c;

			if (double.IsNaN(weekOpen)) weekOpen = o;
			if (h > weekHigh) weekHigh = h;
			if (l < weekLow)  weekLow  = l;
			weekClose = c;

			if (double.IsNaN(monthOpen)) monthOpen = o;
			if (h > monthHigh) monthHigh = h;
			if (l < monthLow)  monthLow  = l;
			monthClose = c;

			bool inRth = rStart >= s.RthOpenRef && rStart < s.RthCloseRef;

			if (rStart < s.RthOpenRef)
			{
				s.HasOvernight = true;
				if (h > s.OvernightHigh) s.OvernightHigh = h;
				if (l < s.OvernightLow)  s.OvernightLow  = l;
			}

			if (inRth)
			{
				if (!s.RthStarted)
				{
					s.RthStarted		= true;
					s.RthOpen			= o;
					s.RthOpenBarTime	= tBar;
					s.RthAnchorFound	= s.AnchorFound && (rStart - s.RthOpenRef) <= anchorTolerance;

					SPLSession prior = GetPrevSession(1);
					if (prior != null)
					{
						double reference = !double.IsNaN(prior.RthClose) ? prior.RthClose : prior.Close;
						if (!double.IsNaN(reference))
						{
							s.GapReference	= reference;
							s.GapOpen		= o;
							s.GapValid		= prior.AnchorFound;
							s.GapFilled		= false;
						}
					}
				}

				if (h > s.RthHigh) s.RthHigh = h;
				if (l < s.RthLow)  s.RthLow  = l;
				s.RthClose = c;

				if (rStart < s.OrEndRef)
				{
					if (!s.HasOr)
					{
						s.HasOr			= true;
						s.OrAnchorFound	= s.RthAnchorFound;
					}
					if (h > s.OrHigh) s.OrHigh = h;
					if (l < s.OrLow)  s.OrLow  = l;
				}

				if (rStart < s.IbEndRef)
				{
					if (!s.HasIb)
					{
						s.HasIb			= true;
						s.IbAnchorFound	= s.RthAnchorFound;
					}
					if (h > s.IbHigh) s.IbHigh = h;
					if (l < s.IbLow)  s.IbLow  = l;
				}

				if (s.GapValid && !s.GapFilled && h >= s.GapReference && l <= s.GapReference)
					s.GapFilled = true;
			}
			else if (s.RthStarted && rStart >= s.RthCloseRef)
				s.RthEnded = true;

			if (rStart >= s.LondonBeginRef && rStart < s.LondonEndRef)
			{
				if (!s.HasLondon)
				{
					s.HasLondon			= true;
					s.LondonAnchorFound	= s.AnchorFound && (rStart - s.LondonBeginRef) <= anchorTolerance;
				}
				if (h > s.LondonHigh) s.LondonHigh = h;
				if (l < s.LondonLow)  s.LondonLow  = l;
			}

			if (!closed || v <= 0)
				return;

			double px = SourcePrice(o, h, l, c);

			s.CumPv		+= px * v;
			s.CumVol	+= v;
			s.CumPv2	+= px * px * v;

			weekCumPv	+= px * v;
			weekCumVol	+= v;
			weekCumPv2	+= px * px * v;

			if (inRth)
			{
				s.RthCumPv	+= px * v;
				s.RthCumVol	+= v;
				s.RthCumPv2	+= px * px * v;
			}

			if (ProfileScope == SPLProfileScope.RegularHoursOnly && !inRth)
				return;

			DistributeVolume(h, l, px, v);
		}

		#endregion

		#region Volume distribution and value area

		private void DistributeVolume(double h, double l, double px, double v)
		{
			if (VolumeSpread == SPLVolumeSpread.TypicalPrice)
			{
				AddLadderVolume(KeyOf(px), v);
				return;
			}

			int kh = KeyOf(h);
			int kl = KeyOf(l);
			if (kh < kl)
			{
				int swap = kh;
				kh = kl;
				kl = swap;
			}

			int count = kh - kl + 1;
			if (count <= 0 || count > 2000)
			{
				AddLadderVolume(KeyOf(px), v);
				return;
			}

			double slice = v / count;
			for (int k = kl; k <= kh; k++)
				AddLadderVolume(k, slice);
		}

		private void AddLadderVolume(int key, double vol)
		{
			if (cur != null && cur.Ladder != null)
				AddToLadder(cur.Ladder, key, vol, ref cur.LadderMaxVol, ref cur.LadderMaxKey);

			AddToLadder(weekLadder, key, vol, ref weekLadderMax, ref weekLadderMaxKey);

			if (compAValid)
				AddToLadder(compA, key, vol, ref compAMax, ref compAMaxKey);
			if (compBValid)
				AddToLadder(compB, key, vol, ref compBMax, ref compBMaxKey);
		}

		private static void AddToLadder(Dictionary<int, double> ladder, int key, double vol, ref double maxVol, ref int maxKey)
		{
			double stored;
			if (ladder.TryGetValue(key, out stored))
				stored += vol;
			else
				stored = vol;

			ladder[key] = stored;

			if (stored > maxVol)
			{
				maxVol = stored;
				maxKey = key;
			}
		}

		private int RebuildComposite(Dictionary<int, double> target, int days, ref double maxVol, ref int maxKey, ref DateTime begin)
		{
			target.Clear();
			maxVol	= double.MinValue;
			maxKey	= 0;
			begin	= cur != null ? cur.Begin : DateTime.MinValue;

			int need	= Math.Max(1, days) - 1;
			int taken	= 0;

			for (int i = sessions.Count - 1; i >= 0 && taken < need; i--)
			{
				SPLSession s = sessions[i];
				if (s.Ladder == null)
					break;

				foreach (KeyValuePair<int, double> kv in s.Ladder)
					AddToLadder(target, kv.Key, kv.Value, ref maxVol, ref maxKey);

				begin = s.Begin;
				taken++;
			}

			return taken + 1;
		}

		private void ComputeSessionValueArea(SPLSession s)
		{
			if (s == null || s.Ladder == null)
				return;

			int pk, vk, lk;
			if (ComputeValueArea(s.Ladder, s.LadderMaxKey, ValueAreaFraction, out pk, out vk, out lk))
			{
				s.Poc		= PriceOfKey(pk);
				s.Vah		= PriceOfKey(vk);
				s.Val		= PriceOfKey(lk);
				s.VaValid	= true;
			}
			else
				s.VaValid = false;
		}

		private void RefreshValueAreas()
		{
			if (!vaDirty)
				return;
			vaDirty = false;

			ComputeSessionValueArea(cur);

			int pk, vk, lk;
			if (ComputeValueArea(weekLadder, weekLadderMaxKey, ValueAreaFraction, out pk, out vk, out lk))
			{
				weekPoc		= PriceOfKey(pk);
				weekVah		= PriceOfKey(vk);
				weekVal		= PriceOfKey(lk);
				weekVaValid	= true;
			}
			else
				weekVaValid = false;
		}

		private double ValueAreaFraction
		{
			get { return Math.Max(10, Math.Min(95, ValueAreaPercent)) / 100.0; }
		}

		private bool ComputeValueArea(Dictionary<int, double> ladder, int pocKeyHint, double fraction,
			out int pocKey, out int vahKey, out int valKey)
		{
			pocKey = 0;
			vahKey = 0;
			valKey = 0;

			if (ladder == null || ladder.Count == 0)
				return false;

			keyBuffer.Clear();
			double total	= 0;
			double best		= double.MinValue;
			int bestKey		= pocKeyHint;

			foreach (KeyValuePair<int, double> kv in ladder)
			{
				keyBuffer.Add(kv.Key);
				total += kv.Value;
				if (kv.Value > best)
				{
					best	= kv.Value;
					bestKey	= kv.Key;
				}
			}

			if (total <= 0)
				return false;

			keyBuffer.Sort();

			int n		= keyBuffer.Count;
			int pocIdx	= keyBuffer.BinarySearch(bestKey);
			if (pocIdx < 0)
				return false;

			double target	= total * fraction;
			double acc		= ladder[bestKey];
			int up			= pocIdx;
			int dn			= pocIdx;

			while (acc < target && (up < n - 1 || dn > 0))
			{
				double above = 0;
				if (up + 1 <= n - 1) above += ladder[keyBuffer[up + 1]];
				if (up + 2 <= n - 1) above += ladder[keyBuffer[up + 2]];

				double below = 0;
				if (dn - 1 >= 0) below += ladder[keyBuffer[dn - 1]];
				if (dn - 2 >= 0) below += ladder[keyBuffer[dn - 2]];

				if (up >= n - 1)
				{
					acc += below;
					dn = Math.Max(0, dn - 2);
				}
				else if (dn <= 0)
				{
					acc += above;
					up = Math.Min(n - 1, up + 2);
				}
				else if (above >= below)
				{
					acc += above;
					up = Math.Min(n - 1, up + 2);
				}
				else
				{
					acc += below;
					dn = Math.Max(0, dn - 2);
				}
			}

			pocKey = bestKey;
			vahKey = keyBuffer[up];
			valKey = keyBuffer[dn];
			return true;
		}

		private SPLSession GetPrevSession(int age)
		{
			int idx = sessions.Count - age;
			if (idx < 0 || idx >= sessions.Count)
				return null;
			return sessions[idx];
		}

		private void TrimSessions()
		{
			int keepLadders = Math.Max(Math.Max(CompositeADays, CompositeBDays), Math.Max(2, PrevDayCount)) + 2;
			int keepSummary = Math.Max(MaxLookbackDays, keepLadders) + 2;

			int dropLaddersBefore = sessions.Count - keepLadders;
			for (int i = 0; i < dropLaddersBefore; i++)
				sessions[i].Ladder = null;

			while (sessions.Count > keepSummary)
				sessions.RemoveAt(0);
		}

		#endregion

		#region Signal plots

		// Level slots: fixed order, shared with the Python backtest.
		private const int NSlots = 19;
		private static readonly string[] SlotNames = { "PDH","PDL","PDC","pPOC","pVAH","pVAL","ONH","ONL","ORH","ORL","IBH","IBL","VWAP","V1U","V1L","V2U","V2L","PWH","PWL" };

		private ATR					atrInd;
		private TimeSpan			primarySpan;
		private readonly double[]	snapLv	= new double[NSlots];	// levels known at the START of the most recent primary bar
		private readonly bool[]		slotOn	= new bool[NSlots];
		private bool				snapValid;

		private void RefreshSlotFlags()
		{
			for (int i = 0; i < NSlots; i++)
			{
				bool on;
				if (i <= 2)			on = SigGroupPrevDay;
				else if (i <= 5)	on = SigGroupPrevValueArea;
				else if (i <= 7)	on = SigGroupOvernight;
				else if (i <= 9)	on = SigGroupOpeningRange;
				else if (i <= 11)	on = SigGroupInitialBalance;
				else if (i <= 16)	on = SigGroupVwap;
				else				on = SigGroupPrevWeek;
				slotOn[i] = on;
			}
		}

		private void ReadLevels(double[] a, DateTime barStartRef)
		{
			for (int i = 0; i < NSlots; i++)
				a[i] = double.NaN;

			SPLSession prior = GetPrevSession(1);
			if (prior != null && Gate(prior.AnchorFound))
			{
				if (prior.HasRange)
				{
					a[0] = prior.High;
					a[1] = prior.Low;
					if (!double.IsNaN(prior.Close))
						a[2] = prior.Close;
				}
				if (prior.VaValid)
				{
					a[3] = prior.Poc;
					a[4] = prior.Vah;
					a[5] = prior.Val;
				}
			}

			if (cur != null)
			{
				if (cur.HasOvernight && barStartRef >= cur.RthOpenRef && Gate(cur.AnchorFound)
					&& cur.OvernightHigh > double.MinValue && cur.OvernightLow < double.MaxValue)
				{
					a[6] = cur.OvernightHigh;
					a[7] = cur.OvernightLow;
				}
				if (cur.HasOr && barStartRef >= cur.OrEndRef && Gate(cur.OrAnchorFound))
				{
					a[8] = cur.OrHigh;
					a[9] = cur.OrLow;
				}
				if (cur.HasIb && barStartRef >= cur.IbEndRef && Gate(cur.IbAnchorFound))
				{
					a[10] = cur.IbHigh;
					a[11] = cur.IbLow;
				}
				if (cur.CumVol > 0 && Gate(cur.AnchorFound))
				{
					double vw	= cur.CumPv / cur.CumVol;
					double sd	= StdDevOf(cur.CumPv2, cur.CumVol, vw);
					a[12] = vw;
					if (sd > 0)
					{
						a[13] = vw + sd;
						a[14] = vw - sd;
						a[15] = vw + 2 * sd;
						a[16] = vw - 2 * sd;
					}
				}
			}

			if (prevWeekKey != DateTime.MinValue && prevWeekHigh > double.MinValue && prevWeekLow < double.MaxValue && Gate(prevWeekAnchorFound))
			{
				a[17] = prevWeekHigh;
				a[18] = prevWeekLow;
			}
		}

		// Pure function (unit-tested against the Python backtest): per-style +1 / -1 / 0 for one completed bar.
		internal static void EvalBar(double[] lv, bool[] on, double h, double l, double c, double pc1, double pc2,
			double atr, bool vwapFilter, double bufMult,
			out int react, out int brk, out int brk2, out double hitLevel)
		{
			double buf = bufMult * atr;
			int rl = 0, rs = 0, bl = 0, bs = 0, b2l = 0, b2s = 0;
			double rLv = 0, bLv = 0, b2Lv = 0;

			for (int i = 0; i < lv.Length; i++)
			{
				if (!on[i])
					continue;
				double L = lv[i];
				if (double.IsNaN(L))
					continue;

				if (pc1 > L && l <= L && c > L)					{ rl++;  if (rLv == 0)  rLv = L; }
				if (pc1 < L && h >= L && c < L)					{ rs++;  if (rLv == 0)  rLv = L; }
				if (pc1 <= L && c > L + buf)					{ bl++;  if (bLv == 0)  bLv = L; }
				if (pc1 >= L && c < L - buf)					{ bs++;  if (bLv == 0)  bLv = L; }
				if (pc2 <= L && pc1 > L + buf && c > L + buf)	{ b2l++; if (b2Lv == 0) b2Lv = L; }
				if (pc2 >= L && pc1 < L - buf && c < L - buf)	{ b2s++; if (b2Lv == 0) b2Lv = L; }
			}

			if (vwapFilter)
			{
				double vw	= lv[12];
				bool up		= !double.IsNaN(vw) && c > vw;
				bool dn		= !double.IsNaN(vw) && c < vw;
				if (!up) { rl = 0; bl = 0; b2l = 0; }
				if (!dn) { rs = 0; bs = 0; b2s = 0; }
			}

			react	= (rl > 0 && rs == 0) ? 1 : ((rs > 0 && rl == 0) ? -1 : 0);
			brk		= (bl > 0 && bs == 0) ? 1 : ((bs > 0 && bl == 0) ? -1 : 0);
			brk2	= (b2l > 0 && b2s == 0) ? 1 : ((b2s > 0 && b2l == 0) ? -1 : 0);
			hitLevel = react != 0 ? rLv : (brk != 0 ? bLv : (brk2 != 0 ? b2Lv : 0));
		}

		// Called on the first tick of a new primary bar: scores the bar that just closed ([1]) with the
		// levels that were known when it opened (snapLv), and writes every plot for the new bar [0].
		private void SignalEvaluate()
		{
			if (CurrentBar < 3 || atrInd == null)
				return;

			int react = 0, brk = 0, brk2 = 0;
			double hit = 0;

			RefreshSlotFlags();

			if (snapValid)
			{
				DateTime tEnd		= ToRef(Times[0][1]);
				int barEndHhmm		= tEnd.Hour * 100 + tEnd.Minute;
				double atr			= atrInd[2];

				if (barEndHhmm >= SigStartHhmm && barEndHhmm <= SigEndHhmm && atr > 0)
					EvalBar(snapLv, slotOn, High[1], Low[1], Close[1], Close[2], Close[3], atr, SigVwapFilter, SigBreakBufferAtr,
						out react, out brk, out brk2, out hit);
			}

			Values[0][0] = react;
			Values[1][0] = brk;
			Values[2][0] = brk2;
			Values[3][0] = hit;

			// Nearest levels around the close of the bar that just ended (= where this bar opens).
			double c1 = Close[1];
			double above = 0, below = 0;
			bool atLevel = false;
			if (snapValid)
			{
				double bestUp = double.MaxValue, bestDn = double.MaxValue;
				double touch = SigAtLevelTicks * TickSize;
				for (int i = 0; i < NSlots; i++)
				{
					double L = snapLv[i];
					if (double.IsNaN(L))
						continue;
					double d = L - c1;
					if (d > 0 && d < bestUp) { bestUp = d; above = L; }
					else if (d < 0 && -d < bestDn) { bestDn = -d; below = L; }
					if (slotOn[i] && Math.Abs(d) <= touch)
						atLevel = true;
				}
			}
			Values[4][0] = above;
			Values[5][0] = below;
			Values[6][0] = above > 0 ? (above - c1) / TickSize : 0;
			Values[7][0] = below > 0 ? (c1 - below) / TickSize : 0;
			Values[8][0] = atLevel ? 1 : 0;
		}

		// Snapshot of the engine state as the new primary bar opens.
		private void SignalRoll()
		{
			if (CurrentBar < 1)
				return;
			DateTime startRef = ToRef(Times[0][0] - primarySpan);
			ReadLevels(snapLv, startRef);
			snapValid = true;
		}

		#endregion

		#region Level assembly

		private bool Gate(bool anchored)
		{
			return !StrictGating || anchored;
		}

		private void AddRow(double price, string name, Stroke pen, DateTime origin, bool dim)
		{
			if (pen == null || pen.Brush == null)
				return;
			if (double.IsNaN(price) || double.IsInfinity(price) || price <= double.MinValue + 1 || price >= double.MaxValue - 1)
				return;

			string priceText = Instrument.MasterInstrument.FormatPrice(price);

			SPLRow row		= new SPLRow();
			row.Price		= price;
			row.Name		= name;
			row.PriceText	= priceText;
			row.Text		= ComposeText(name, priceText);
			row.Pen			= pen;
			row.Origin		= origin;
			row.Dim			= dim;
			row.Rank		= RankOf(pen);
			rows.Add(row);
		}

		private string ComposeText(string name, string priceText)
		{
			if (ShowLevelName && ShowLevelValue)
				return name + "  " + priceText;
			if (ShowLevelValue)
				return priceText;
			if (ShowLevelName)
				return name;
			return string.Empty;
		}

		private int RankOf(Stroke pen)
		{
			if (pen == PocStroke || pen == VwapStroke || pen == PrevVwapStroke)
				return 0;
			if (pen == PrevDayHighLowStroke || pen == PrevDayCloseStroke)
				return 1;
			if (pen == PrevPocStroke || pen == WeekPocStroke || pen == CompositeAStroke || pen == CompositeBStroke)
				return 2;
			if (pen == ValueAreaStroke || pen == PrevValueAreaStroke || pen == WeekValueAreaStroke)
				return 3;
			if (pen == PrevWeekStroke || pen == PrevMonthStroke)
				return 4;
			if (pen == OpeningRangeStroke || pen == InitialBalanceStroke || pen == OvernightStroke || pen == LondonStroke || pen == GapStroke)
				return 5;
			if (pen == DevelopingDayStroke || pen == WeekStroke || pen == MonthStroke || pen == PrevDayOpenStroke || pen == PrevDayMidStroke)
				return 6;
			if (pen == WeeklyVwapStroke)
				return 7;
			return 9;
		}

		private void AddRow(double price, string name, Stroke pen, DateTime origin)
		{
			AddRow(price, name, pen, origin, false);
		}

		private static string Age(int age, bool show)
		{
			if (!show || age <= 1)
				return string.Empty;
			return Subscript(age.ToString(CultureInfo.InvariantCulture));
		}

		private static string Subscript(string text)
		{
			char[] outChars = new char[text.Length];
			for (int i = 0; i < text.Length; i++)
			{
				char c = text[i];
				if (c >= '0' && c <= '9')
					outChars[i] = (char)(0x2080 + (c - '0'));
				else if (c == '+')
					outChars[i] = '\u208A';
				else if (c == '-')
					outChars[i] = '\u208B';
				else
					outChars[i] = c;
			}
			return new string(outChars);
		}

		private void BuildRows()
		{
			BuildDailyRows();
			BuildWeeklyRows();
			BuildMonthlyRows();
			BuildVwapRows();
			BuildValueAreaRows();
			BuildIntradayRows();
			BuildGapRow();
		}

		private void BuildDailyRows()
		{
			int wanted = Math.Max(0, Math.Min(PrevDayCount, MaxLookbackDays));

			for (int age = 1; age <= wanted; age++)
			{
				SPLSession s = GetPrevSession(age);
				if (s == null || !s.HasRange || !Gate(s.AnchorFound))
					continue;

				string suffix = Age(age, ShowDayAge);

				if (ShowPrevDayHighLow)
				{
					AddRow(s.High, "PDH" + suffix, PrevDayHighLowStroke, s.Begin);
					AddRow(s.Low,  "PDL" + suffix, PrevDayHighLowStroke, s.Begin);
				}
				if (ShowPrevDayClose && !double.IsNaN(s.Close))
					AddRow(s.Close, "PDC" + suffix, PrevDayCloseStroke, s.Begin);
				if (ShowPrevDayOpen && !double.IsNaN(s.Open))
					AddRow(s.Open, "PDO" + suffix, PrevDayOpenStroke, s.Begin);
				if (ShowPrevDayMid)
					AddRow((s.High + s.Low) * 0.5, "PDM" + suffix, PrevDayMidStroke, s.Begin);
			}

			if (ShowDevelopingDay && cur != null && cur.HasRange && Gate(cur.AnchorFound))
			{
				AddRow(cur.High, "DH", DevelopingDayStroke, cur.Begin);
				AddRow(cur.Low,  "DL", DevelopingDayStroke, cur.Begin);
			}
		}

		private void BuildWeeklyRows()
		{
			if (ShowPrevWeek && prevWeekKey != DateTime.MinValue
				&& prevWeekHigh > double.MinValue && prevWeekLow < double.MaxValue
				&& Gate(prevWeekAnchorFound))
			{
				AddRow(prevWeekHigh, "PWH", PrevWeekStroke, prevWeekBegin);
				AddRow(prevWeekLow,  "PWL", PrevWeekStroke, prevWeekBegin);
				if (!double.IsNaN(prevWeekClose))
					AddRow(prevWeekClose, "PWC", PrevWeekStroke, prevWeekBegin);
				if (ShowPrevWeekMid)
					AddRow((prevWeekHigh + prevWeekLow) * 0.5, "PWM", PrevWeekStroke, prevWeekBegin);
			}

			if (ShowDevelopingWeek && weekHigh > double.MinValue && weekLow < double.MaxValue
				&& Gate(weekAnchorFound))
			{
				AddRow(weekHigh, "WH", WeekStroke, weekBegin);
				AddRow(weekLow,  "WL", WeekStroke, weekBegin);
			}
		}

		private void BuildMonthlyRows()
		{
			if (ShowPrevMonth && prevMonthKey != -1
				&& prevMonthHigh > double.MinValue && prevMonthLow < double.MaxValue
				&& Gate(prevMonthAnchorFound))
			{
				AddRow(prevMonthHigh, "PMH", PrevMonthStroke, prevMonthBegin);
				AddRow(prevMonthLow,  "PML", PrevMonthStroke, prevMonthBegin);
				if (!double.IsNaN(prevMonthClose))
					AddRow(prevMonthClose, "PMC", PrevMonthStroke, prevMonthBegin);
				if (ShowPrevMonthMid)
					AddRow((prevMonthHigh + prevMonthLow) * 0.5, "PMM", PrevMonthStroke, prevMonthBegin);
			}

			if (ShowDevelopingMonth && monthHigh > double.MinValue && monthLow < double.MaxValue
				&& Gate(monthAnchorFound))
			{
				AddRow(monthHigh, "MH", MonthStroke, monthBegin);
				AddRow(monthLow,  "ML", MonthStroke, monthBegin);
			}
		}

		private void BuildVwapRows()
		{
			if (ShowVwap && cur != null)
			{
				double vwap		= double.NaN;
				double sd		= 0;
				string label	= "VWAP";
				DateTime origin	= cur.Begin;

				if (!VwapRthOnly)
				{
					if (cur.CumVol > 0 && Gate(cur.AnchorFound))
					{
						vwap	= cur.CumPv / cur.CumVol;
						sd		= StdDevOf(cur.CumPv2, cur.CumVol, vwap);
					}
				}
				else if (cur.RthStarted && cur.RthCumVol > 0 && Gate(cur.RthAnchorFound))
				{
					vwap	= cur.RthCumPv / cur.RthCumVol;
					sd		= StdDevOf(cur.RthCumPv2, cur.RthCumVol, vwap);
					origin	= cur.RthOpenBarTime != DateTime.MinValue ? cur.RthOpenBarTime : cur.Begin;
				}
				else if (ShowPrevRthVwap && prevRthVwapValid && Gate(prevRthVwapAnchored))
				{
					vwap	= prevRthVwap;
					sd		= prevRthVwapSd;
					label	= "PD VWAP";
					SPLSession prior = GetPrevSession(1);
					origin	= prior != null ? prior.Begin : cur.Begin;
				}

				if (!double.IsNaN(vwap))
				{
					bool frozen = label == "PD VWAP";
					AddRow(vwap, label, frozen ? PrevVwapStroke : VwapStroke, origin);

					if (sd > 0)
					{
						if (ShowVwapBand1)
						{
							AddRow(vwap + VwapBand1Mult * sd, label + " σ" + Subscript("+" + Fmt(VwapBand1Mult)), VwapBand1Stroke, origin);
							AddRow(vwap - VwapBand1Mult * sd, label + " σ" + Subscript("-" + Fmt(VwapBand1Mult)), VwapBand1Stroke, origin);
						}
						if (ShowVwapBand2)
						{
							AddRow(vwap + VwapBand2Mult * sd, label + " σ" + Subscript("+" + Fmt(VwapBand2Mult)), VwapBand2Stroke, origin);
							AddRow(vwap - VwapBand2Mult * sd, label + " σ" + Subscript("-" + Fmt(VwapBand2Mult)), VwapBand2Stroke, origin);
						}
						if (ShowVwapBand3)
						{
							AddRow(vwap + VwapBand3Mult * sd, label + " σ" + Subscript("+" + Fmt(VwapBand3Mult)), VwapBand3Stroke, origin);
							AddRow(vwap - VwapBand3Mult * sd, label + " σ" + Subscript("-" + Fmt(VwapBand3Mult)), VwapBand3Stroke, origin);
						}
					}
				}
			}

			if (ShowWeeklyVwap && weekCumVol > 0 && Gate(weekAnchorFound))
			{
				double wv = weekCumPv / weekCumVol;
				double sd = StdDevOf(weekCumPv2, weekCumVol, wv);
				AddRow(wv, "WVWAP", WeeklyVwapStroke, weekBegin);

				if (sd > 0)
				{
					if (ShowWeeklyVwapBand1)
					{
						AddRow(wv + WeeklyVwapBand1Mult * sd, "WVWAP σ" + Subscript("+" + Fmt(WeeklyVwapBand1Mult)), WeeklyVwapBandStroke, weekBegin);
						AddRow(wv - WeeklyVwapBand1Mult * sd, "WVWAP σ" + Subscript("-" + Fmt(WeeklyVwapBand1Mult)), WeeklyVwapBandStroke, weekBegin);
					}
					if (ShowWeeklyVwapBand2)
					{
						AddRow(wv + WeeklyVwapBand2Mult * sd, "WVWAP σ" + Subscript("+" + Fmt(WeeklyVwapBand2Mult)), WeeklyVwapBandStroke, weekBegin);
						AddRow(wv - WeeklyVwapBand2Mult * sd, "WVWAP σ" + Subscript("-" + Fmt(WeeklyVwapBand2Mult)), WeeklyVwapBandStroke, weekBegin);
					}
				}
			}
		}

		private static string Fmt(double multiplier)
		{
			return multiplier.ToString("0.#", CultureInfo.InvariantCulture);
		}

		private void BuildValueAreaRows()
		{
			if (cur != null && cur.VaValid && Gate(cur.AnchorFound))
			{
				if (ShowDayPoc)
					AddRow(cur.Poc, "POC", PocStroke, cur.Begin);
				if (ShowDayValueArea)
				{
					AddRow(cur.Vah, "VAH", ValueAreaStroke, cur.Begin);
					AddRow(cur.Val, "VAL", ValueAreaStroke, cur.Begin);
				}
			}

			int vaWanted = Math.Max(0, Math.Min(PrevValueAreaCount, MaxLookbackDays));
			for (int age = 1; age <= vaWanted; age++)
			{
				SPLSession prior = GetPrevSession(age);
				if (prior == null || !prior.VaValid || !Gate(prior.AnchorFound))
					continue;

				string suffix = Age(age, ShowValueAreaAge);

				if (ShowPrevDayPoc)
					AddRow(prior.Poc, "pPOC" + suffix, PrevPocStroke, prior.Begin);
				if (ShowPrevDayValueArea)
				{
					AddRow(prior.Vah, "pVAH" + suffix, PrevValueAreaStroke, prior.Begin);
					AddRow(prior.Val, "pVAL" + suffix, PrevValueAreaStroke, prior.Begin);
				}
			}

			if (weekVaValid && Gate(weekAnchorFound))
			{
				if (ShowWeekPoc)
					AddRow(weekPoc, "WPOC", WeekPocStroke, weekBegin);
				if (ShowWeekValueArea)
				{
					AddRow(weekVah, "WVAH", WeekValueAreaStroke, weekBegin);
					AddRow(weekVal, "WVAL", WeekValueAreaStroke, weekBegin);
				}
			}

			if (prevWeekVaValid && Gate(prevWeekAnchorFound))
			{
				if (ShowPrevWeekPoc)
					AddRow(prevWeekPoc, "pWPOC", WeekPocStroke, prevWeekBegin);
				if (ShowPrevWeekValueArea)
				{
					AddRow(prevWeekVah, "pWVAH", WeekValueAreaStroke, prevWeekBegin);
					AddRow(prevWeekVal, "pWVAL", WeekValueAreaStroke, prevWeekBegin);
				}
			}

			if (ShowCompositeA && compAValid && compAMax > double.MinValue && Gate(compACount >= CompositeADays))
				AddRow(PriceOfKey(compAMaxKey),
					CompositeADays.ToString(CultureInfo.InvariantCulture) + "D POC",
					CompositeAStroke, compABegin);

			if (ShowCompositeB && compBValid && compBMax > double.MinValue && Gate(compBCount >= CompositeBDays))
				AddRow(PriceOfKey(compBMaxKey),
					CompositeBDays.ToString(CultureInfo.InvariantCulture) + "D POC",
					CompositeBStroke, compBBegin);
		}

		private void BuildIntradayRows()
		{
			if (cur == null)
				return;

			DateTime rthOrigin = cur.RthOpenBarTime != DateTime.MinValue ? cur.RthOpenBarTime : cur.Begin;

			if (ShowOpeningRange && cur.HasOr && Gate(cur.OrAnchorFound))
			{
				AddRow(cur.OrHigh, "ORH", OpeningRangeStroke, rthOrigin);
				AddRow(cur.OrLow,  "ORL", OpeningRangeStroke, rthOrigin);
			}

			if (ShowInitialBalance && cur.HasIb && Gate(cur.IbAnchorFound))
			{
				AddRow(cur.IbHigh, "IBH", InitialBalanceStroke, rthOrigin);
				AddRow(cur.IbLow,  "IBL", InitialBalanceStroke, rthOrigin);
			}

			if (ShowOvernight && cur.HasOvernight && Gate(cur.AnchorFound))
			{
				AddRow(cur.OvernightHigh, "ONH", OvernightStroke, cur.Begin);
				AddRow(cur.OvernightLow,  "ONL", OvernightStroke, cur.Begin);
			}

			if (ShowLondon && cur.HasLondon && Gate(cur.LondonAnchorFound))
			{
				AddRow(cur.LondonHigh, "LONH", LondonStroke, cur.Begin);
				AddRow(cur.LondonLow,  "LONL", LondonStroke, cur.Begin);
			}
		}

		private void BuildGapRow()
		{
			if (!ShowGap || cur == null || !cur.GapValid || double.IsNaN(cur.GapReference))
				return;
			if (cur.GapFilled && HideFilledGap)
				return;

			double size	= cur.GapOpen - cur.GapReference;
			string tag	= "GAP " + (size >= 0 ? "▲" : "▼") + Math.Abs(size).ToString("0.##", CultureInfo.InvariantCulture);

			AddRow(cur.GapReference, tag, GapStroke,
				cur.RthOpenBarTime != DateTime.MinValue ? cur.RthOpenBarTime : cur.Begin,
				cur.GapFilled);
		}

		private static int ComparePriceDesc(SPLRow a, SPLRow b)
		{
			return b.Price.CompareTo(a.Price);
		}

		#endregion

		#region Rendering

		public override void OnRenderTargetChanged()
		{
			ReleaseRenderResources();
		}

		private void ReleaseRenderResources()
		{
			foreach (KeyValuePair<string, SharpDX.DirectWrite.TextLayout> kv in layoutCache)
			{
				if (kv.Value != null && !kv.Value.IsDisposed)
					kv.Value.Dispose();
			}
			layoutCache.Clear();

			if (labelFormat != null && !labelFormat.IsDisposed)
				labelFormat.Dispose();
			labelFormat		= null;
			cachedFontSize	= -1;
		}

		private void EnsureLabelFormat(ChartControl chartControl)
		{
			string family = "Arial";
			try
			{
				if (chartControl.Properties.LabelFont != null && chartControl.Properties.LabelFont.Family != null)
					family = chartControl.Properties.LabelFont.Family.Source;
			}
			catch { family = "Arial"; }

			if (labelFormat != null && !labelFormat.IsDisposed
				&& cachedFontSize == LabelFontSize && cachedFontFamily == family)
				return;

			ReleaseRenderResources();

			NinjaTrader.Gui.Tools.SimpleFont font =
				new NinjaTrader.Gui.Tools.SimpleFont(family, LabelFontSize);
			labelFormat			= font.ToDirectWriteTextFormat();
			cachedFontSize		= LabelFontSize;
			cachedFontFamily	= family;
		}

		private SharpDX.DirectWrite.TextLayout GetLayout(string text)
		{
			if (string.IsNullOrEmpty(text) || labelFormat == null || labelFormat.IsDisposed)
				return null;

			SharpDX.DirectWrite.TextLayout layout;
			if (layoutCache.TryGetValue(text, out layout) && layout != null && !layout.IsDisposed)
				return layout;

			if (layoutCache.Count > 512)
			{
				foreach (KeyValuePair<string, SharpDX.DirectWrite.TextLayout> kv in layoutCache)
				{
					if (kv.Value != null && !kv.Value.IsDisposed)
						kv.Value.Dispose();
				}
				layoutCache.Clear();
			}

			layout = new SharpDX.DirectWrite.TextLayout(
				NinjaTrader.Core.Globals.DirectWriteFactory, text, labelFormat, 400f, 40f);
			layoutCache[text] = layout;
			return layout;
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (IsInHitTest || RenderTarget == null || chartControl == null || chartScale == null)
				return;
			if (!engineReady || ChartBars == null || ChartPanel == null)
				return;
			if (CurrentBars == null || CurrentBars.Length == 0 || CurrentBars[0] < 0)
				return;

			RefreshValueAreas();

			chips.Clear();
			chipRows.Clear();

			rows.Clear();
			BuildRows();
			if (rows.Count == 0)
				return;

			EnsureLabelFormat(chartControl);
			if (labelFormat == null)
				return;

			rows.Sort(ComparePriceDesc);

			float panelLeft		= ChartPanel.X;
			float panelRight	= ChartPanel.X + ChartPanel.W;
			float panelTop		= ChartPanel.Y;
			float panelBottom	= ChartPanel.Y + ChartPanel.H;

			float lastBarX = panelRight;
			try { lastBarX = chartControl.GetXByBarIndex(ChartBars, ChartBars.ToIndex); }
			catch { lastBarX = panelRight; }

			float marginPx = 0f;
			try { marginPx = chartControl.Properties.BarMarginRight; }
			catch { marginPx = 0f; }
			if (marginPx <= 0f)
				marginPx = panelRight - lastBarX;
			if (marginPx < 8f)
				marginPx = 8f;
			if (marginPx > (panelRight - panelLeft) * 0.5f)
				marginPx = (panelRight - panelLeft) * 0.5f;
			float marginStartX = panelRight - marginPx;

			float currentBarX = marginStartX;
			try
			{
				int lastAbsolute = ChartBars.Bars != null ? ChartBars.Bars.Count - 1 : -1;
				if (lastAbsolute >= 0 && ChartBars.ToIndex >= lastAbsolute)
				{
					float x = chartControl.GetXByBarIndex(ChartBars, lastAbsolute);
					if (x <= panelRight)
						currentBarX = x;
				}
			}
			catch { currentBarX = marginStartX; }

			float colWidth	= 40f;
			float lineHeight= 14f;
			for (int i = 0; i < rows.Count; i++)
			{
				SharpDX.DirectWrite.TextLayout layout = GetLayout(rows[i].Text);
				if (layout == null)
					continue;
				float w = layout.Metrics.WidthIncludingTrailingWhitespace;
				if (w > colWidth) colWidth = w;
				float h = layout.Metrics.Height;
				if (h > lineHeight) lineHeight = h;
			}
			colWidth += LabelPaddingPx;

			bool rightSideMode		= LabelAnchorLocked;
			float lineAnchorX		= LineStartMode == SPLLineStart.RightMargin ? marginStartX : currentBarX;
			float rightSpan			= Math.Max(0f, panelRight - lineAnchorX);
			float rightLineX0		= panelRight - rightSpan * (Math.Max(10, Math.Min(100, LineFillPercent)) / 100f);

			bool labelsAbove		= LabelAnchor == SPLLabelAnchor.AboveLine;
			float labelRise			= lineHeight + 3f;

			float lineExtEndX		= Math.Min(panelRight, lastBarX + LineExtensionPx);

			bool alignRight;
			float edgeX;
			bool primaryLeft;
			if (labelsAbove)
			{
				alignRight	= !rightSideMode;
				edgeX		= rightSideMode ? rightLineX0 : lineExtEndX;
				primaryLeft	= true;
			}
			else if (LabelAnchor == SPLLabelAnchor.CurrentBar && lastBarX + LineExtensionPx + colWidth * 2f < panelRight)
			{
				alignRight	= false;
				edgeX		= lastBarX + LineExtensionPx;
				primaryLeft	= false;
			}
			else
			{
				alignRight	= true;
				edgeX		= panelRight - 2f;
				primaryLeft	= true;
			}

			SharpDX.Direct2D1.AntialiasMode savedAa = RenderTarget.AntialiasMode;
			RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;

			if (LabelOverlap == SPLLabelOverlap.Merge)
			{
				if (ShowMergeCard)
					HookMouse();

				SPLGeom g		= new SPLGeom();
				g.PanelLeft		= panelLeft;
				g.PanelRight	= panelRight;
				g.PanelTop		= panelTop;
				g.PanelBottom	= panelBottom;
				g.LineExtEndX	= lineExtEndX;
				g.RightLineX0	= rightLineX0;
				g.RightSideMode	= rightSideMode;
				g.LabelsAbove	= labelsAbove;
				g.AlignRight	= alignRight;
				g.EdgeX			= edgeX;
				g.LineHeight	= lineHeight;
				g.LabelRise		= labelRise;

				RenderMerged(chartControl, chartScale, g);
				RenderTarget.AntialiasMode = savedAa;
				return;
			}

			float pad			= Math.Max(0, LabelPaddingPx);
			int maxSteps		= Math.Max(1, LabelMaxColumns);
			int steps			= 0;
			float runLeft		= 0f;
			float runRight		= 0f;
			float lastBottom	= float.MinValue;

			for (int i = 0; i < rows.Count; i++)
			{
				SPLRow row = rows[i];

				float y = chartScale.GetYByValue(row.Price);
				if (y < panelTop - 2f || y > panelBottom + 2f)
					continue;

				Stroke pen = row.Pen;
				if (pen == null || pen.Brush == null)
					continue;

				pen.RenderTarget = RenderTarget;
				if (pen.BrushDX == null)
					continue;

				float labelX	= edgeX;
				float top		= labelsAbove ? y - labelRise : y - lineHeight * 0.5f;

				SharpDX.DirectWrite.TextLayout layout = row.Dim ? null : GetLayout(row.Text);

				if (layout != null)
				{
					float w = layout.Metrics.WidthIncludingTrailingWhitespace;

					if (top < lastBottom + 1f)
					{
						steps++;
						if (steps > maxSteps)
							steps = 0;
					}
					else
						steps = 0;

					if (steps == 0)
					{
						labelX		= alignRight ? edgeX - w : edgeX;
						runLeft		= labelX;
						runRight	= labelX + w;
					}
					else
					{
						bool goLeft = primaryLeft;
						if (StaircaseZigzag)
						{
							goLeft = (steps % 2 == 1) ? primaryLeft : !primaryLeft;
							if (goLeft && runLeft - pad - w < panelLeft)
								goLeft = false;
							else if (!goLeft && runRight + pad + w > panelRight)
								goLeft = true;
						}

						if (goLeft)
						{
							labelX	= runLeft - pad - w;
							runLeft	= labelX;
						}
						else
						{
							labelX		= runRight + pad;
							runRight	= labelX + w;
						}
					}

					if (labelX < panelLeft) labelX = panelLeft;
					if (labelX + w > panelRight) labelX = panelRight - w;
					lastBottom = top + lineHeight;
				}

				float x0 = LineStartX(chartControl, row, rightSideMode, rightLineX0, panelLeft);

				float x1;
				if (rightSideMode)
					x1 = panelRight;
				else if (labelsAbove || layout == null)
					x1 = Math.Min(panelRight, lastBarX + LineExtensionPx);
				else
					x1 = labelX - 3f;
				if (x1 > panelRight) x1 = panelRight;

				if (!labelsAbove && x1 - x0 < 20f) x0 = Math.Max(panelLeft, x1 - 20f);

				if (x1 > x0)
					RenderTarget.DrawLine(new Vector2(x0, y), new Vector2(x1, y),
						pen.BrushDX, pen.Width, pen.StrokeStyle);

				if (layout != null)
					RenderTarget.DrawTextLayout(new Vector2(labelX, top), layout, pen.BrushDX,
						SharpDX.Direct2D1.DrawTextOptions.None);
			}

			RenderTarget.AntialiasMode = savedAa;
		}

		private float LineStartX(ChartControl chartControl, SPLRow row, bool rightSideMode, float rightLineX0, float panelLeft)
		{
			if (rightSideMode)
				return rightLineX0;
			if (LineStartMode == SPLLineStart.FullWidth)
				return panelLeft;

			float x0 = panelLeft;
			if (row.Origin != DateTime.MinValue)
			{
				try
				{
					float ox = chartControl.GetXByTime(row.Origin);
					if (ox > x0) x0 = ox;
				}
				catch { x0 = panelLeft; }
			}
			return x0;
		}

		private void RenderMerged(ChartControl chartControl, ChartScale chartScale, SPLGeom g)
		{
			float thr = g.LineHeight + Math.Max(0, LabelMergePadPx);

			mergeIdx.Clear();
			mergeY.Clear();
			for (int i = 0; i < rows.Count; i++)
			{
				float y = chartScale.GetYByValue(rows[i].Price);
				if (y < g.PanelTop - 2f || y > g.PanelBottom + 2f)
					continue;

				Stroke pen = rows[i].Pen;
				if (pen == null || pen.Brush == null)
					continue;
				pen.RenderTarget = RenderTarget;
				if (pen.BrushDX == null)
					continue;

				mergeIdx.Add(i);
				mergeY.Add(y);
			}

			SharpDX.Direct2D1.Brush backdrop = null;
			try
			{
				if (chartControl.Properties.ChartBackground != null)
					backdrop = chartControl.Properties.ChartBackground.ToDxBrush(RenderTarget);
			}
			catch { backdrop = null; }

			int a = 0;
			while (a < mergeIdx.Count)
			{
				int b = a;
				while (b + 1 < mergeIdx.Count && mergeY[b + 1] - mergeY[b] < thr)
					b++;

				int prim = a;
				for (int k = a + 1; k <= b; k++)
					if (rows[mergeIdx[k]].Rank < rows[mergeIdx[prim]].Rank)
						prim = k;

				SPLRow primary	= rows[mergeIdx[prim]];
				int count		= b - a + 1;
				float py		= mergeY[prim];

				string text = count == 1
					? primary.Text
					: ComposeText(primary.Name + " +" + (count - 1).ToString(CultureInfo.InvariantCulture), primary.PriceText);

				SharpDX.DirectWrite.TextLayout layout = (primary.Dim && count == 1) ? null : GetLayout(text);
				float textW	= layout != null ? layout.Metrics.WidthIncludingTrailingWhitespace : 0f;
				float dotsW	= (layout != null && count > 1 && ShowMergeDots) ? count * 8f + 3f : 0f;
				float chipW	= dotsW + textW;

				float chipX = g.AlignRight ? g.EdgeX - chipW : g.EdgeX;
				if (chipX + chipW > g.PanelRight) chipX = g.PanelRight - chipW;
				if (chipX < g.PanelLeft) chipX = g.PanelLeft;
				float top = g.LabelsAbove ? py - g.LabelRise : py - g.LineHeight * 0.5f;

				for (int k = a; k <= b; k++)
				{
					SPLRow row	= rows[mergeIdx[k]];
					float y		= mergeY[k];
					float x0	= LineStartX(chartControl, row, g.RightSideMode, g.RightLineX0, g.PanelLeft);

					float x1;
					if (g.RightSideMode)
						x1 = g.PanelRight;
					else if (g.LabelsAbove || layout == null)
						x1 = g.LineExtEndX;
					else
						x1 = chipX - 3f;
					if (x1 > g.PanelRight) x1 = g.PanelRight;

					if (!g.LabelsAbove && x1 - x0 < 20f) x0 = Math.Max(g.PanelLeft, x1 - 20f);

					if (x1 > x0)
						RenderTarget.DrawLine(new Vector2(x0, y), new Vector2(x1, y),
							row.Pen.BrushDX, row.Pen.Width, row.Pen.StrokeStyle);
				}

				if (layout != null)
				{
					if (backdrop != null && count > 1)
						RenderTarget.FillRectangle(new RectangleF(chipX - 2f, top, chipW + 4f, g.LineHeight), backdrop);

					if (count > 1 && ShowMergeCard)
					{
						SPLChip chip	= new SPLChip();
						chip.L		= chipX - 2f;
						chip.T		= top;
						chip.R		= chipX + chipW + 2f;
						chip.B		= top + g.LineHeight;
						chip.First	= chipRows.Count;
						chip.Count	= count;
						for (int k = a; k <= b; k++)
							chipRows.Add(rows[mergeIdx[k]]);
						chips.Add(chip);
					}

					float x = chipX;
					if (dotsW > 0f)
					{
						float dy = top + g.LineHeight * 0.5f;
						for (int k = a; k <= b; k++)
						{
							RenderTarget.FillRectangle(new RectangleF(x, dy - 2.5f, 5f, 5f), rows[mergeIdx[k]].Pen.BrushDX);
							x += 8f;
						}
						x += 3f;
					}

					RenderTarget.DrawTextLayout(new Vector2(x, top), layout, primary.Pen.BrushDX,
						SharpDX.Direct2D1.DrawTextOptions.None);
				}

				a = b + 1;
			}

			if (backdrop != null && !backdrop.IsDisposed)
				backdrop.Dispose();

			if (ShowMergeCard && mouseValid)
			{
				hoveredChip = HitChip(mouseX, mouseY);
				if (hoveredChip >= 0)
					DrawMergeCard(chartControl, g, chips[hoveredChip]);
			}
		}

		private int HitChip(float x, float y)
		{
			for (int i = chips.Count - 1; i >= 0; i--)
			{
				SPLChip c = chips[i];
				if (x >= c.L && x <= c.R && y >= c.T && y <= c.B)
					return i;
			}
			return -1;
		}

		private string CardRowText(SPLRow r)
		{
			return ShowLevelValue ? r.Name + "  " + r.PriceText : r.Name;
		}

		private void DrawMergeCard(ChartControl chartControl, SPLGeom g, SPLChip chip)
		{
			float pad	= 6f;
			float lineH	= g.LineHeight;

			string header = chip.Count.ToString(CultureInfo.InvariantCulture) + " levels";
			SharpDX.DirectWrite.TextLayout hl = GetLayout(header);
			float w = hl != null ? hl.Metrics.WidthIncludingTrailingWhitespace : 0f;
			for (int i = 0; i < chip.Count; i++)
			{
				SPLRow r = chipRows[chip.First + i];
				SharpDX.DirectWrite.TextLayout l = GetLayout(CardRowText(r));
				if (l != null)
					w = Math.Max(w, l.Metrics.WidthIncludingTrailingWhitespace + 10f);
			}
			w += pad * 2f;
			float h = (chip.Count + 1) * lineH + pad * 2f;

			float x = chip.R - w;
			float y = chip.B + 4f;
			if (y + h > g.PanelBottom) y = chip.T - 4f - h;
			if (y < g.PanelTop) y = g.PanelTop + 2f;
			if (x + w > g.PanelRight) x = g.PanelRight - w - 2f;
			if (x < g.PanelLeft) x = g.PanelLeft + 2f;

			SharpDX.Direct2D1.Brush bg		= null;
			SharpDX.Direct2D1.Brush border	= null;
			try
			{
				if (chartControl.Properties.ChartBackground != null)
					bg = chartControl.Properties.ChartBackground.ToDxBrush(RenderTarget);
				border = cardBorderBrush.ToDxBrush(RenderTarget);

				SharpDX.Direct2D1.RoundedRectangle rr = new SharpDX.Direct2D1.RoundedRectangle();
				rr.Rect		= new RectangleF(x, y, w, h);
				rr.RadiusX	= 3f;
				rr.RadiusY	= 3f;
				if (bg != null)
					RenderTarget.FillRoundedRectangle(rr, bg);
				if (border != null)
					RenderTarget.DrawRoundedRectangle(rr, border, 1f);

				if (hl != null && border != null)
					RenderTarget.DrawTextLayout(new Vector2(x + pad, y + pad), hl, border,
						SharpDX.Direct2D1.DrawTextOptions.None);

				for (int i = 0; i < chip.Count; i++)
				{
					SPLRow r		= chipRows[chip.First + i];
					float ry	= y + pad + (i + 1) * lineH;
					if (r.Pen == null)
						continue;
					r.Pen.RenderTarget = RenderTarget;
					if (r.Pen.BrushDX == null)
						continue;

					RenderTarget.FillRectangle(new RectangleF(x + pad, ry + lineH * 0.5f - 2.5f, 5f, 5f), r.Pen.BrushDX);
					SharpDX.DirectWrite.TextLayout l = GetLayout(CardRowText(r));
					if (l != null)
						RenderTarget.DrawTextLayout(new Vector2(x + pad + 10f, ry), l, r.Pen.BrushDX,
							SharpDX.Direct2D1.DrawTextOptions.None);
				}
			}
			catch { }
			finally
			{
				if (bg != null && !bg.IsDisposed) bg.Dispose();
				if (border != null && !border.IsDisposed) border.Dispose();
			}
		}

		#endregion

		#region Mouse

		private void HookMouse()
		{
			if (mouseHooked || ChartControl == null)
				return;
			try
			{
				ChartControl.MouseMove	+= OnChartMouseMove;
				ChartControl.MouseLeave	+= OnChartMouseLeave;
				mouseHooked = true;
			}
			catch { mouseHooked = false; }
		}

		private void UnhookMouse()
		{
			if (!mouseHooked)
				return;
			mouseHooked = false;

			ChartControl cc = ChartControl;
			if (cc == null)
				return;
			try
			{
				cc.Dispatcher.InvokeAsync(new Action(delegate
				{
					try
					{
						cc.MouseMove	-= OnChartMouseMove;
						cc.MouseLeave	-= OnChartMouseLeave;
					}
					catch { }
				}));
			}
			catch { }
		}

		private void OnChartMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
		{
			try
			{
				ChartPanel panel = ChartPanel;
				if (panel == null)
					return;

				System.Windows.Point pt = e.GetPosition(panel);
				double sx = panel.ActualWidth  > 0 ? panel.W / panel.ActualWidth  : 1.0;
				double sy = panel.ActualHeight > 0 ? panel.H / panel.ActualHeight : 1.0;
				mouseX		= (float)(panel.X + pt.X * sx);
				mouseY		= (float)(panel.Y + pt.Y * sy);
				mouseValid	= true;

				int hit = HitChip(mouseX, mouseY);
				if (hit != hoveredChip)
				{
					hoveredChip = hit;
					ForceRefresh();
				}
			}
			catch { }
		}

		private void OnChartMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
		{
			mouseValid = false;
			if (hoveredChip != -1)
			{
				hoveredChip = -1;
				ForceRefresh();
			}
		}

		#endregion

		#region Properties – 16. Signals

		[Display(Name = "Prev Day H / L / C", Description = "Include PDH, PDL, PDC in the signal plots.", Order = 0, GroupName = "16. Signals")]
		public bool SigGroupPrevDay { get; set; }

		[Display(Name = "Prev Day POC / VAH / VAL", Description = "Include pPOC, pVAH, pVAL.", Order = 1, GroupName = "16. Signals")]
		public bool SigGroupPrevValueArea { get; set; }

		[Display(Name = "Overnight H / L", Description = "Include ONH, ONL (active from the RTH open).", Order = 2, GroupName = "16. Signals")]
		public bool SigGroupOvernight { get; set; }

		[Display(Name = "Opening Range H / L", Description = "Include ORH, ORL (active once the opening range window has ended).", Order = 3, GroupName = "16. Signals")]
		public bool SigGroupOpeningRange { get; set; }

		[Display(Name = "Initial Balance H / L", Description = "Include IBH, IBL (active once the initial balance window has ended).", Order = 4, GroupName = "16. Signals")]
		public bool SigGroupInitialBalance { get; set; }

		[Display(Name = "Session VWAP and 1 / 2 sd bands", Description = "Include VWAP and its +-1 and +-2 standard deviation bands.", Order = 5, GroupName = "16. Signals")]
		public bool SigGroupVwap { get; set; }

		[Display(Name = "Prev Week H / L", Description = "Include PWH, PWL.", Order = 6, GroupName = "16. Signals")]
		public bool SigGroupPrevWeek { get; set; }

		[Display(Name = "Signal Start (HHMM)", Description = "First bar (by bar end time, in the Time Reference zone) that may signal.", Order = 7, GroupName = "16. Signals")]
		[Range(0, 2359)]
		public int SigStartHhmm { get; set; }

		[Display(Name = "Signal End (HHMM)", Description = "Last bar (by bar end time) that may signal.", Order = 8, GroupName = "16. Signals")]
		[Range(0, 2359)]
		public int SigEndHhmm { get; set; }

		[Display(Name = "VWAP Side Filter", Description = "Longs only when the closed bar is above session VWAP, shorts only below.", Order = 9, GroupName = "16. Signals")]
		public bool SigVwapFilter { get; set; }

		[Display(Name = "Break Buffer (x ATR)", Description = "A break must close this many ATR(14) beyond the level (BreakSignal and Break2Signal).", Order = 10, GroupName = "16. Signals")]
		[Range(0, 5)]
		public double SigBreakBufferAtr { get; set; }

		[Display(Name = "At-Level Distance (ticks)", Description = "AtLevel plot is 1 when the last close is within this many ticks of an enabled level.", Order = 11, GroupName = "16. Signals")]
		[Range(0, 1000)]
		public int SigAtLevelTicks { get; set; }

		#endregion

		#region Plot series (for automation tools)

		[Browsable(false)] [XmlIgnore] public Series<double> ReactSignal	{ get { Update(); return Values[0]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> BreakSignal	{ get { Update(); return Values[1]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> Break2Signal	{ get { Update(); return Values[2]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> SignalLevel	{ get { Update(); return Values[3]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> NearestAbove	{ get { Update(); return Values[4]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> NearestBelow	{ get { Update(); return Values[5]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> DistAboveTicks	{ get { Update(); return Values[6]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> DistBelowTicks	{ get { Update(); return Values[7]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> AtLevel		{ get { Update(); return Values[8]; } }

		#endregion

		#region Properties – 00. Setup

		[Display(Name = "Precision Minute Series", Description = "Add a hidden 1-minute series so VWAP, value area and the time based levels (OR/IB/RTH/London) resolve to the exact minute regardless of the chart interval.", Order = 0, GroupName = "00. Setup")]
		public bool UseMinuteSeries { get; set; }

		[Display(Name = "Strict Gating", Description = "Hide any level whose session anchor was never observed in the loaded data. Prevents a partial first session from publishing misleading highs, lows, VWAP or value areas.", Order = 1, GroupName = "00. Setup")]
		public bool StrictGating { get; set; }

		[Display(Name = "Time Reference", Description = "Time zone the intraday clock times below are expressed in. Eastern matches the usual 9:30 / 16:00 convention, Exchange uses the trading hours template, Chart uses your global NinjaTrader time zone.", Order = 2, GroupName = "00. Setup")]
		public SPLTimeReference TimeReference { get; set; }

		[Display(Name = "Max Lookback (days)", Description = "How many completed sessions are retained. Older sessions and their volume ladders are released automatically.", Order = 3, GroupName = "00. Setup")]
		[Range(1, 400)]
		public int MaxLookbackDays { get; set; }

		[Display(Name = "Value Area (%)", Description = "Share of session volume enclosed by the value area.", Order = 4, GroupName = "00. Setup")]
		[Range(10, 95)]
		public int ValueAreaPercent { get; set; }

		[Display(Name = "  └─ Profile Scope", Description = "Whether the volume distribution behind POC / VAH / VAL covers the whole session or only regular trading hours.", Order = 5, GroupName = "00. Setup")]
		public SPLProfileScope ProfileScope { get; set; }

		[Display(Name = "  └─ Row Size (ticks)", Description = "Ticks per volume distribution row. Raise it on wide range instruments to keep the ladder compact.", Order = 6, GroupName = "00. Setup")]
		[Range(1, 100)]
		public int ProfileRowTicks { get; set; }

		[Display(Name = "  └─ Volume Spread", Description = "SpreadHighLow shares each bar's volume across every row it touched, TypicalPrice books it all at the source price.", Order = 7, GroupName = "00. Setup")]
		public SPLVolumeSpread VolumeSpread { get; set; }

		#endregion

		#region Properties – 01. Daily

		[Display(Name = "Previous High / Low", Description = "Plot the previous session high and low.", Order = 0, GroupName = "01. Daily")]
		public bool ShowPrevDayHighLow { get; set; }

		[Display(Name = "Previous Close", Description = "Plot the previous session close.", Order = 1, GroupName = "01. Daily")]
		public bool ShowPrevDayClose { get; set; }

		[Display(Name = "Previous Open", Description = "Plot the previous session open.", Order = 2, GroupName = "01. Daily")]
		public bool ShowPrevDayOpen { get; set; }

		[Display(Name = "Previous Mid", Description = "Plot the midpoint of the previous session range.", Order = 3, GroupName = "01. Daily")]
		public bool ShowPrevDayMid { get; set; }

		[Display(Name = "Previous Sessions", Description = "How many completed sessions back to plot. 1 shows yesterday only.", Order = 4, GroupName = "01. Daily")]
		[Range(0, 20)]
		public int PrevDayCount { get; set; }

		[Display(Name = "  └─ Show Age", Description = "Append -2, -3 to labels of sessions older than yesterday.", Order = 5, GroupName = "01. Daily")]
		public bool ShowDayAge { get; set; }

		[Display(Name = "Developing High / Low", Description = "Plot the high and low of the session in progress.", Order = 6, GroupName = "01. Daily")]
		public bool ShowDevelopingDay { get; set; }

		#endregion

		#region Properties – 02. Weekly

		[Display(Name = "Previous Week", Description = "Plot previous week high, low and close. Only plots once a full week of data has actually been observed.", Order = 0, GroupName = "02. Weekly")]
		public bool ShowPrevWeek { get; set; }

		[Display(Name = "  └─ Previous Mid", Description = "Plot the midpoint of the previous week range.", Order = 1, GroupName = "02. Weekly")]
		public bool ShowPrevWeekMid { get; set; }

		[Display(Name = "Developing Week", Description = "Plot the high and low of the week in progress.", Order = 2, GroupName = "02. Weekly")]
		public bool ShowDevelopingWeek { get; set; }

		#endregion

		#region Properties – 03. Monthly

		[Display(Name = "Previous Month", Description = "Plot previous month high, low and close. Only plots once a full month of data has actually been observed.", Order = 0, GroupName = "03. Monthly")]
		public bool ShowPrevMonth { get; set; }

		[Display(Name = "  └─ Previous Mid", Description = "Plot the midpoint of the previous month range.", Order = 1, GroupName = "03. Monthly")]
		public bool ShowPrevMonthMid { get; set; }

		[Display(Name = "Developing Month", Description = "Plot the high and low of the month in progress.", Order = 2, GroupName = "03. Monthly")]
		public bool ShowDevelopingMonth { get; set; }

		#endregion

		#region Properties – 04. VWAP – Daily

		[Display(Name = "Show VWAP", Description = "Plot the session volume weighted average price.", Order = 0, GroupName = "04. VWAP – Daily")]
		public bool ShowVwap { get; set; }

		[Display(Name = "  └─ RTH Only", Description = "Anchor VWAP at the regular hours open instead of the session open and accumulate only during regular hours.", Order = 1, GroupName = "04. VWAP – Daily")]
		public bool VwapRthOnly { get; set; }

		[Display(Name = "  └─ Show PD VWAP", Description = "In RTH Only mode, keep displaying the frozen previous regular hours VWAP while the overnight session runs.", Order = 2, GroupName = "04. VWAP – Daily")]
		public bool ShowPrevRthVwap { get; set; }

		[Display(Name = "  └─ Price Source", Description = "Price each bar contributes to the VWAP accumulation.", Order = 3, GroupName = "04. VWAP – Daily")]
		public SPLPriceSource VwapSource { get; set; }

		[Display(Name = "1st Bands", Description = "Plot the first pair of standard deviation bands.", Order = 4, GroupName = "04. VWAP – Daily")]
		public bool ShowVwapBand1 { get; set; }

		[Display(Name = "  └─ 1st Multiplier", Description = "Standard deviation multiplier for the first band pair.", Order = 5, GroupName = "04. VWAP – Daily")]
		[Range(0.1, 10)]
		public double VwapBand1Mult { get; set; }

		[Display(Name = "2nd Bands", Description = "Plot the second pair of standard deviation bands.", Order = 6, GroupName = "04. VWAP – Daily")]
		public bool ShowVwapBand2 { get; set; }

		[Display(Name = "  └─ 2nd Multiplier", Description = "Standard deviation multiplier for the second band pair.", Order = 7, GroupName = "04. VWAP – Daily")]
		[Range(0.1, 10)]
		public double VwapBand2Mult { get; set; }

		[Display(Name = "3rd Bands", Description = "Plot the third pair of standard deviation bands.", Order = 8, GroupName = "04. VWAP – Daily")]
		public bool ShowVwapBand3 { get; set; }

		[Display(Name = "  └─ 3rd Multiplier", Description = "Standard deviation multiplier for the third band pair.", Order = 9, GroupName = "04. VWAP – Daily")]
		[Range(0.1, 10)]
		public double VwapBand3Mult { get; set; }

		#endregion

		#region Properties – 05. VWAP – Weekly

		[Display(Name = "Show Weekly VWAP", Description = "Plot a VWAP anchored at the first session of the week.", Order = 0, GroupName = "05. VWAP – Weekly")]
		public bool ShowWeeklyVwap { get; set; }

		[Display(Name = "1st Bands", Description = "Plot the first pair of weekly standard deviation bands.", Order = 1, GroupName = "05. VWAP – Weekly")]
		public bool ShowWeeklyVwapBand1 { get; set; }

		[Display(Name = "  └─ 1st Multiplier", Description = "Standard deviation multiplier for the first weekly band pair.", Order = 2, GroupName = "05. VWAP – Weekly")]
		[Range(0.1, 10)]
		public double WeeklyVwapBand1Mult { get; set; }

		[Display(Name = "2nd Bands", Description = "Plot the second pair of weekly standard deviation bands.", Order = 3, GroupName = "05. VWAP – Weekly")]
		public bool ShowWeeklyVwapBand2 { get; set; }

		[Display(Name = "  └─ 2nd Multiplier", Description = "Standard deviation multiplier for the second weekly band pair.", Order = 4, GroupName = "05. VWAP – Weekly")]
		[Range(0.1, 10)]
		public double WeeklyVwapBand2Mult { get; set; }

		#endregion

		#region Properties – 06. Value Area

		[Display(Name = "Developing POC", Description = "Plot the point of control of the session in progress.", Order = 0, GroupName = "06. Value Area")]
		public bool ShowDayPoc { get; set; }

		[Display(Name = "Developing VAH / VAL", Description = "Plot the value area boundaries of the session in progress.", Order = 1, GroupName = "06. Value Area")]
		public bool ShowDayValueArea { get; set; }

		[Display(Name = "Previous POC", Description = "Plot the previous session point of control.", Order = 2, GroupName = "06. Value Area")]
		public bool ShowPrevDayPoc { get; set; }

		[Display(Name = "Previous VAH / VAL", Description = "Plot the previous session value area boundaries.", Order = 3, GroupName = "06. Value Area")]
		public bool ShowPrevDayValueArea { get; set; }

		[Display(Name = "  └─ Previous Sessions", Description = "How many completed sessions of POC and value area to plot. 1 shows yesterday only.", Order = 4, GroupName = "06. Value Area")]
		[Range(0, 20)]
		public int PrevValueAreaCount { get; set; }

		[Display(Name = "  └─ Show Age", Description = "Append -2, -3 to POC and value area labels of sessions older than yesterday.", Order = 5, GroupName = "06. Value Area")]
		public bool ShowValueAreaAge { get; set; }

		[Display(Name = "Week POC", Description = "Plot the point of control of the week in progress.", Order = 6, GroupName = "06. Value Area")]
		public bool ShowWeekPoc { get; set; }

		[Display(Name = "Week VAH / VAL", Description = "Plot the value area boundaries of the week in progress.", Order = 7, GroupName = "06. Value Area")]
		public bool ShowWeekValueArea { get; set; }

		[Display(Name = "Previous Week POC", Description = "Plot the previous week point of control.", Order = 8, GroupName = "06. Value Area")]
		public bool ShowPrevWeekPoc { get; set; }

		[Display(Name = "Previous Week VAH / VAL", Description = "Plot the previous week value area boundaries.", Order = 9, GroupName = "06. Value Area")]
		public bool ShowPrevWeekValueArea { get; set; }

		#endregion

		#region Properties – 07. Multi-Day POC

		[Display(Name = "Show Composite POC", Description = "Plot a rolling multi-session point of control. Updates incrementally on every bar.", Order = 0, GroupName = "07. Multi-Day POC")]
		public bool ShowCompositeA { get; set; }

		[Display(Name = "  └─ Sessions", Description = "Number of sessions in the composite window, including the one in progress.", Order = 1, GroupName = "07. Multi-Day POC")]
		[Range(2, 60)]
		public int CompositeADays { get; set; }

		[Display(Name = "Show 2nd Composite POC", Description = "Plot a second rolling multi-session point of control over a different window.", Order = 2, GroupName = "07. Multi-Day POC")]
		public bool ShowCompositeB { get; set; }

		[Display(Name = "  └─ Sessions", Description = "Number of sessions in the second composite window, including the one in progress.", Order = 3, GroupName = "07. Multi-Day POC")]
		[Range(2, 60)]
		public int CompositeBDays { get; set; }

		#endregion

		#region Properties – 08. Intraday

		[Display(Name = "RTH Open (HHMM)", Description = "Regular trading hours open in the chosen time reference, e.g. 930 for 09:30.", Order = 0, GroupName = "08. Intraday")]
		[Range(0, 2359)]
		public int RthOpenHhmm { get; set; }

		[Display(Name = "RTH Close (HHMM)", Description = "Regular trading hours close in the chosen time reference, e.g. 1600 for 16:00.", Order = 1, GroupName = "08. Intraday")]
		[Range(0, 2359)]
		public int RthCloseHhmm { get; set; }

		[Display(Name = "Opening Range", Description = "Plot the high and low of the opening range measured from the RTH open.", Order = 2, GroupName = "08. Intraday")]
		public bool ShowOpeningRange { get; set; }

		[Display(Name = "  └─ Minutes", Description = "Length of the opening range window.", Order = 3, GroupName = "08. Intraday")]
		[Range(1, 240)]
		public int OpeningRangeMinutes { get; set; }

		[Display(Name = "Initial Balance", Description = "Plot the high and low of the initial balance measured from the RTH open.", Order = 4, GroupName = "08. Intraday")]
		public bool ShowInitialBalance { get; set; }

		[Display(Name = "  └─ Minutes", Description = "Length of the initial balance window.", Order = 5, GroupName = "08. Intraday")]
		[Range(1, 480)]
		public int InitialBalanceMinutes { get; set; }

		[Display(Name = "Overnight Range", Description = "Plot the high and low traded between the session open and the RTH open.", Order = 6, GroupName = "08. Intraday")]
		public bool ShowOvernight { get; set; }

		[Display(Name = "London Range", Description = "Plot the high and low traded during the London window.", Order = 7, GroupName = "08. Intraday")]
		public bool ShowLondon { get; set; }

		[Display(Name = "  └─ London Open (HHMM)", Description = "Start of the London window in the chosen time reference.", Order = 8, GroupName = "08. Intraday")]
		[Range(0, 2359)]
		public int LondonOpenHhmm { get; set; }

		[Display(Name = "  └─ London Close (HHMM)", Description = "End of the London window in the chosen time reference.", Order = 9, GroupName = "08. Intraday")]
		[Range(0, 2359)]
		public int LondonCloseHhmm { get; set; }

		#endregion

		#region Properties – 09. Gap

		[Display(Name = "Show Gap Level", Description = "Plot the previous close as the gap fill reference from the regular hours open, labelled with the gap size.", Order = 0, GroupName = "09. Gap")]
		public bool ShowGap { get; set; }

		[Display(Name = "  └─ Hide When Filled", Description = "Remove the gap level once price has traded back through it.", Order = 1, GroupName = "09. Gap")]
		public bool HideFilledGap { get; set; }

		#endregion

		#region Properties – 10. Lines & Labels

		[Display(Name = "Line Start", Description = "Where each line begins. ToCurrentBar runs from the current candle to the right of the chart and falls back to the right margin when the current candle is scrolled off screen. RightMargin always confines lines to the margin. LevelOrigin starts at the bar the level was formed on. FullWidth spans the whole panel.", Order = 0, GroupName = "10. Lines & Labels")]
		[RefreshProperties(RefreshProperties.All)]
		public SPLLineStart LineStartMode { get; set; }

		[Display(Name = "Line Extension (px)", Description = "Pixels a line runs past the last bar when labels are anchored to the current bar.", Order = 1, GroupName = "10. Lines & Labels")]
		[Range(0, 400)]
		public int LineExtensionPx { get; set; }

		[Display(Name = "  └─ Margin Fill (%)", Description = "How much of the span between the line anchor and the right edge the line actually covers in the RightMargin and ToCurrentBar modes. Below 100 leaves a gap so the line stops just short of the current candle.", Order = 2, GroupName = "10. Lines & Labels")]
		[Range(10, 100)]
		public int LineFillPercent { get; set; }

		[Display(Name = "Label Anchor", Description = "RightEdge pins labels to the right of the panel, CurrentBar places them just past the current candle, AboveLine floats each label a few pixels above its own line at the free tip. Forced to AboveLine and locked while Line Start is RightMargin or ToCurrentBar.", Order = 3, GroupName = "10. Lines & Labels")]
		public SPLLabelAnchor LabelAnchor
		{
			get { return LabelAnchorLocked ? SPLLabelAnchor.AboveLine : labelAnchor; }
			set { labelAnchor = value; }
		}

		[Browsable(false)]
		[XmlIgnore]
		public bool LabelAnchorLocked
		{
			get { return LineStartMode == SPLLineStart.RightMargin || LineStartMode == SPLLineStart.ToCurrentBar; }
		}

		[Display(Name = "Show Level Name", Description = "Include the level name (PDH, POC, VWAP …) in the label.", Order = 4, GroupName = "10. Lines & Labels")]
		public bool ShowLevelName { get; set; }

		[Display(Name = "Show Level Value", Description = "Include the price in the label.", Order = 5, GroupName = "10. Lines & Labels")]
		public bool ShowLevelValue { get; set; }

		[Display(Name = "Font Size", Description = "Label font size.", Order = 6, GroupName = "10. Lines & Labels")]
		[Range(5, 30)]
		public int LabelFontSize { get; set; }

		[Display(Name = "  └─ Staircase Padding (px)", Description = "Gap between a label and the one it steps past. Steps are sized to the actual label width, so short labels pack tighter than long ones.", Order = 7, GroupName = "10. Lines & Labels")]
		[Range(0, 60)]
		public int LabelPaddingPx { get; set; }

		[Display(Name = "  └─ Staircase Max Steps", Description = "How many sideways steps a run of overlapping labels may take before it snaps back to the anchor.", Order = 8, GroupName = "10. Lines & Labels")]
		[Range(1, 12)]
		public int LabelMaxColumns { get; set; }

		[Display(Name = "  └─ Staircase Zigzag", Description = "Alternate sides on each step so a run stays clustered around the anchor instead of marching off in one direction. Falls back to one side where the other has no room.", Order = 9, GroupName = "10. Lines & Labels")]
		public bool StaircaseZigzag { get; set; }

		[Display(Name = "Label Overlap", Description = "What happens when labels would overlap. Staircase steps them sideways into columns. Merge draws one chip per cluster: a colour dot per member, the top ranked name, a +N count and that member's exact price. Lines are never moved.", Order = 10, GroupName = "10. Lines & Labels")]
		public SPLLabelOverlap LabelOverlap { get; set; }

		[Display(Name = "  └─ Merge Padding (px)", Description = "Extra vertical distance, on top of the text height, within which two labels count as overlapping.", Order = 11, GroupName = "10. Lines & Labels")]
		[Range(0, 20)]
		public int LabelMergePadPx { get; set; }

		[Display(Name = "  └─ Merge Dots", Description = "Draw one colour dot per member in front of a merged chip.", Order = 12, GroupName = "10. Lines & Labels")]
		public bool ShowMergeDots { get; set; }

		[Display(Name = "  └─ Hover Card", Description = "Hovering a merged chip lists every member with its exact price. The chart only repaints when the cursor enters or leaves a chip.", Order = 13, GroupName = "10. Lines & Labels")]
		public bool ShowMergeCard { get; set; }

		#endregion

		#region Properties – 11. Styles – Daily

		[Display(Name = "Previous High / Low", Order = 0, GroupName = "11. Styles – Daily")]
		public Stroke PrevDayHighLowStroke { get; set; }

		[Display(Name = "Previous Close", Order = 1, GroupName = "11. Styles – Daily")]
		public Stroke PrevDayCloseStroke { get; set; }

		[Display(Name = "Previous Open", Order = 2, GroupName = "11. Styles – Daily")]
		public Stroke PrevDayOpenStroke { get; set; }

		[Display(Name = "Previous Mid", Order = 3, GroupName = "11. Styles – Daily")]
		public Stroke PrevDayMidStroke { get; set; }

		[Display(Name = "Developing Day", Order = 4, GroupName = "11. Styles – Daily")]
		public Stroke DevelopingDayStroke { get; set; }

		#endregion

		#region Properties – 12. Styles – Weekly & Monthly

		[Display(Name = "Developing Week", Order = 0, GroupName = "12. Styles – Weekly & Monthly")]
		public Stroke WeekStroke { get; set; }

		[Display(Name = "Previous Week", Order = 1, GroupName = "12. Styles – Weekly & Monthly")]
		public Stroke PrevWeekStroke { get; set; }

		[Display(Name = "Developing Month", Order = 2, GroupName = "12. Styles – Weekly & Monthly")]
		public Stroke MonthStroke { get; set; }

		[Display(Name = "Previous Month", Order = 3, GroupName = "12. Styles – Weekly & Monthly")]
		public Stroke PrevMonthStroke { get; set; }

		#endregion

		#region Properties – 13. Styles – VWAP

		[Display(Name = "VWAP", Order = 0, GroupName = "13. Styles – VWAP")]
		public Stroke VwapStroke { get; set; }

		[Display(Name = "PD VWAP", Order = 1, GroupName = "13. Styles – VWAP")]
		public Stroke PrevVwapStroke { get; set; }

		[Display(Name = "1st Bands", Order = 2, GroupName = "13. Styles – VWAP")]
		public Stroke VwapBand1Stroke { get; set; }

		[Display(Name = "2nd Bands", Order = 3, GroupName = "13. Styles – VWAP")]
		public Stroke VwapBand2Stroke { get; set; }

		[Display(Name = "3rd Bands", Order = 4, GroupName = "13. Styles – VWAP")]
		public Stroke VwapBand3Stroke { get; set; }

		[Display(Name = "Weekly VWAP", Order = 5, GroupName = "13. Styles – VWAP")]
		public Stroke WeeklyVwapStroke { get; set; }

		[Display(Name = "Weekly Bands", Order = 6, GroupName = "13. Styles – VWAP")]
		public Stroke WeeklyVwapBandStroke { get; set; }

		#endregion

		#region Properties – 14. Styles – Value Area

		[Display(Name = "POC", Order = 0, GroupName = "14. Styles – Value Area")]
		public Stroke PocStroke { get; set; }

		[Display(Name = "VAH / VAL", Order = 1, GroupName = "14. Styles – Value Area")]
		public Stroke ValueAreaStroke { get; set; }

		[Display(Name = "Previous POC", Order = 2, GroupName = "14. Styles – Value Area")]
		public Stroke PrevPocStroke { get; set; }

		[Display(Name = "Previous VAH / VAL", Order = 3, GroupName = "14. Styles – Value Area")]
		public Stroke PrevValueAreaStroke { get; set; }

		[Display(Name = "Week POC", Order = 4, GroupName = "14. Styles – Value Area")]
		public Stroke WeekPocStroke { get; set; }

		[Display(Name = "Week VAH / VAL", Order = 5, GroupName = "14. Styles – Value Area")]
		public Stroke WeekValueAreaStroke { get; set; }

		[Display(Name = "Composite POC", Order = 6, GroupName = "14. Styles – Value Area")]
		public Stroke CompositeAStroke { get; set; }

		[Display(Name = "2nd Composite POC", Order = 7, GroupName = "14. Styles – Value Area")]
		public Stroke CompositeBStroke { get; set; }

		#endregion

		#region Properties – 15. Styles – Intraday

		[Display(Name = "Opening Range", Order = 0, GroupName = "15. Styles – Intraday")]
		public Stroke OpeningRangeStroke { get; set; }

		[Display(Name = "Initial Balance", Order = 1, GroupName = "15. Styles – Intraday")]
		public Stroke InitialBalanceStroke { get; set; }

		[Display(Name = "Overnight", Order = 2, GroupName = "15. Styles – Intraday")]
		public Stroke OvernightStroke { get; set; }

		[Display(Name = "London", Order = 3, GroupName = "15. Styles – Intraday")]
		public Stroke LondonStroke { get; set; }

		[Display(Name = "Gap", Order = 4, GroupName = "15. Styles – Intraday")]
		public Stroke GapStroke { get; set; }

		#endregion
	}

	#region Enums

	public enum SPLTimeReference
	{
		Eastern,
		Exchange,
		Chart
	}

	public enum SPLPriceSource
	{
		Typical,
		Weighted,
		Close,
		Median
	}

	public enum SPLVolumeSpread
	{
		TypicalPrice,
		SpreadHighLow
	}

	public enum SPLLineStart
	{
		LevelOrigin,
		FullWidth,
		RightMargin,
		ToCurrentBar
	}

	public enum SPLLabelAnchor
	{
		RightEdge,
		CurrentBar,
		AboveLine
	}

	public enum SPLProfileScope
	{
		WholeSession,
		RegularHoursOnly
	}

	public enum SPLLabelOverlap
	{
		Staircase,
		Merge
	}

	#endregion

	public class SneakyPrecisionLevelsConverter : NinjaTrader.NinjaScript.IndicatorBaseConverter
	{
		public override bool GetPropertiesSupported(ITypeDescriptorContext context)
		{
			return true;
		}

		public override PropertyDescriptorCollection GetProperties(ITypeDescriptorContext context, object component, Attribute[] attrs)
		{
			PropertyDescriptorCollection properties	= base.GetProperties(context, component, attrs);
			SneakyPrecisionLevels owner					= component as SneakyPrecisionLevels;

			if (properties == null || owner == null || !owner.LabelAnchorLocked)
				return properties;

			PropertyDescriptor[] replaced = new PropertyDescriptor[properties.Count];
			for (int i = 0; i < properties.Count; i++)
			{
				PropertyDescriptor property = properties[i];
				if (property.Name == "LabelAnchor")
					property = TypeDescriptor.CreateProperty(typeof(SneakyPrecisionLevels), property,
						new Attribute[] { new ReadOnlyAttribute(true) });
				replaced[i] = property;
			}

			return new PropertyDescriptorCollection(replaced);
		}
	}

	#region Session container

	internal class SPLSession
	{
		public DateTime	Begin;
		public DateTime	End;
		public DateTime	TradingDay;
		public DateTime	FirstBarTime;
		public DateTime	LastBarTime;

		public bool		AnchorFound;
		public bool		Complete;

		public double	Open	= double.NaN;
		public double	High	= double.MinValue;
		public double	Low		= double.MaxValue;
		public double	Close	= double.NaN;

		public DateTime	RthOpenRef;
		public DateTime	RthCloseRef;
		public bool		RthStarted;
		public bool		RthEnded;
		public bool		RthAnchorFound;
		public DateTime	RthOpenBarTime;
		public double	RthOpen		= double.NaN;
		public double	RthHigh		= double.MinValue;
		public double	RthLow		= double.MaxValue;
		public double	RthClose	= double.NaN;

		public bool		HasOvernight;
		public double	OvernightHigh	= double.MinValue;
		public double	OvernightLow	= double.MaxValue;

		public DateTime	OrEndRef;
		public bool		HasOr;
		public bool		OrAnchorFound;
		public double	OrHigh	= double.MinValue;
		public double	OrLow	= double.MaxValue;

		public DateTime	IbEndRef;
		public bool		HasIb;
		public bool		IbAnchorFound;
		public double	IbHigh	= double.MinValue;
		public double	IbLow	= double.MaxValue;

		public DateTime	LondonBeginRef;
		public DateTime	LondonEndRef;
		public bool		HasLondon;
		public bool		LondonAnchorFound;
		public double	LondonHigh	= double.MinValue;
		public double	LondonLow	= double.MaxValue;

		public double	CumPv;
		public double	CumVol;
		public double	CumPv2;
		public double	RthCumPv;
		public double	RthCumVol;
		public double	RthCumPv2;

		public Dictionary<int, double>	Ladder;
		public double	LadderMaxVol	= double.MinValue;
		public int		LadderMaxKey;

		public bool		VaValid;
		public double	Poc;
		public double	Vah;
		public double	Val;

		public bool		GapValid;
		public double	GapReference	= double.NaN;
		public double	GapOpen			= double.NaN;
		public bool		GapFilled;

		public bool HasRange
		{
			get { return High > double.MinValue && Low < double.MaxValue; }
		}

		public bool HasRthRange
		{
			get { return RthHigh > double.MinValue && RthLow < double.MaxValue; }
		}
	}

	internal struct SPLRow
	{
		public double		Price;
		public string		Name;
		public string		PriceText;
		public string		Text;
		public Stroke		Pen;
		public DateTime		Origin;
		public bool			Dim;
		public int			Rank;
	}

	internal struct SPLChip
	{
		public float	L;
		public float	T;
		public float	R;
		public float	B;
		public int		First;
		public int		Count;
	}

	internal struct SPLGeom
	{
		public float	PanelLeft;
		public float	PanelRight;
		public float	PanelTop;
		public float	PanelBottom;
		public float	LineExtEndX;
		public float	RightLineX0;
		public bool		RightSideMode;
		public bool		LabelsAbove;
		public bool		AlignRight;
		public float	EdgeX;
		public float	LineHeight;
		public float	LabelRise;
	}

	#endregion

}
