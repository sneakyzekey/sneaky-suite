// ─────────────────────────────────────────────────────────────────────────────
// GodTradesStrategy_v1_0_0.cs
// ─────────────────────────────────────────────────────────────────────────────
//  Standalone NinjaTrader 8 strategy. No third-party dependencies, no other AddOns/services
//  required — this file compiles and runs entirely on its own.
//
//  SOURCE
//  ------
//  Built from the "GOD TRADES" Canva slide deck (presented by TraderOracle, 14 slides, viewed
//  2026-09-12). This is a FRESH, self-contained implementation of that deck's rules — it does not
//  reference or depend on the existing GodTrades4/GodTrades15/GodTrades21 indicator files already
//  in this Custom folder, even though the concepts (candle gaps, Bollinger Gap, Fill & Reverse,
//  Fill & Continue, Spiderwebs, the Final Line) are the same ones those indicators visualize. This
//  file trades those concepts directly rather than reading their plots.
//
//  WHAT THIS IS
//  ------------
//  One strategy, FOUR independently toggleable setups, each with its own Enable switch, contract
//  size, stop, and target — so you can run any single one alone, or all four at once on one chart:
//
//    1. Bollinger Gap    — 2 same-color candles with a real gap between their bodies, touching (or
//                           very near) the 20/2 Bollinger Band on that side. Entry once the 2nd
//                           candle closes, stop at that candle's own high/low, target = opposite
//                           band (or a fixed-tick target).
//    2. Fill and Reverse  — an old, aged candle-gap line gets touched again; price is expected to
//                           reverse back to what it was doing before the gap ever formed. Tiny fixed
//                           stop, NEVER moved to break-even (see the warning in EnterTrade() below —
//                           this is a deliberate, deck-mandated omission, not an oversight).
//    3. Fill and Continue — the exact same gap-touch trigger as #2, but only taken when it agrees
//                           with the prevailing trend (a rising/falling SMA) — i.e. a pullback-fill
//                           inside a trend, not a counter-trend reversal. The deck itself describes
//                           this as "Same as Trade 2, but only in a trend continuation", so this
//                           file treats #2 and #3 as the SAME trigger, routed to one or the other by
//                           trend alignment, never both on the same event.
//    4. Final Line        — when several gap-touches in a row have resolved the same direction (a
//                           "grind"), the next touch on the last remaining older line in that chain
//                           is treated as the higher-conviction Final Line signal and traded in the
//                           OPPOSITE direction, per the deck's "insane rush the other way" framing.
//                           This preempts #2/#3 for that one event (it does not also fire as a
//                           separate Fill and Reverse/Continue trade).
//
//  Plus one shared filter, not a trade of its own:
//    Spiderweb filter     — when too many unfilled gap lines are clustered near the current price
//                           (the deck's "shitload of lines everywhere" warning), ALL new entries are
//                           vetoed until the cluster clears. On by default.
//
//  HOW "CANDLE GAP" IS DETECTED (two different meanings, matching the deck)
//  --------------------------------------------------------------------------
//  Setup 1 (Bollinger Gap) uses a strict 2-CANDLE BODY gap: the deck's own picture is two same-
//  color candles with visible white space between their bodies (High[1] < Low[0] for the bullish
//  case). This is checked directly in EvaluateBollingerGap() and does NOT go through the tracked
//  gap-line list below.
//
//  Setups 2/3/4 use the deck's other sense of "gap": a close-to-open PRICE SKIP between consecutive
//  bars (the same "gap engine" idea already coded into your GodTrades4 indicator — "the gap-size
//  floor: smallest close-to-open gap in ticks that creates a gap line"). Each qualifying skip is
//  recorded as a GapLine at the skipped range's midpoint and tracked until price later trades back
//  through that price (a "touch"/"fill"). A line must survive MinBarsBeforeValid bars unfilled
//  before a touch counts as a real signal — matching the deck's own strict rule ("candle gaps must
//  be at least 3 candles long before getting filled") — an earlier touch just invalidates the line
//  silently, same as the source material.
//
//  KNOWN LIMITATION — OPPOSITE-DIRECTION SETUPS ON A NETTING ACCOUNT
//  --------------------------------------------------------------------------
//  This file uses NinjaTrader's MANAGED order approach (EnterLong/EnterShort + SetStopLoss/
//  SetProfitTarget, one unique signal name per trade) so that multiple setups can hold independent,
//  simultaneously-open positions with their own stop/target — this works cleanly as long as they
//  agree on direction. If two enabled setups disagree in direction on the same bar (e.g. Bollinger
//  Gap wants long while Fill and Reverse wants short), a standard Sim/futures NETTING account can
//  only hold one net position, so NinjaTrader's managed engine will net/reduce/reverse the combined
//  position rather than truly hold both sides at once — this is normal NinjaTrader behavior, not a
//  bug in this file, and is a DIFFERENT situation from the "opposite-direction PENDING entries get
//  silently dropped" bug documented at length in StraddleBreakoutOCO_v1_0_0.cs (that bug is specific
//  to two resting stop entries never having filled yet; here, entries fire at bar-close against the
//  current market, so they execute/net normally rather than vanish). If you need true simultaneous
//  long+short across setups, that would need the unmanaged-order rewrite pattern already proven in
//  StraddleBreakoutOCO — flag it and it can be built as a v2. TEST THIS SCENARIO ON SIM FIRST if you
//  intend to run more than one setup at a time, exactly like every other file in this project.
//
//  Changelog
//    v1.0.0 (2026-09-12) — first cut, built directly from the 14-slide "GOD TRADES" deck.
//    v1.0.1 (2026-09-12) — fixed a same-bar self-invalidation bug: DetectNewGap() was registering
//      a bar's own new gap line BEFORE CheckGapFills() tested existing lines against that same bar,
//      so a freshly-formed line (priced near that bar's own Open, always inside that bar's own
//      High/Low) almost always "touched itself" at age=0 and got invalidated before it could ever
//      age past MinBarsBeforeValid. Reordered so CheckGapFills() runs first each bar.
//    v1.0.2 (2026-09-12) — added independent long/short enable switches to all four setups
//      (BgEnableLong/Short, FrEnableLong/Short, FcEnableLong/Short, FlEnableLong/Short, all default
//      true) so a single setup's long and short sides can be toggled separately, not just the whole
//      setup at once — e.g. keep Final Line's long side on while disabling its short side.
//    v1.0.3 (2026-10-20) — protection watchdog: if a position is open but fewer stop-loss / profit-target
//      orders are working than the position size (orders cancelled, rejected, lost on a disconnect or
//      strategy restart), the strategy re-places them (original stop/target prices when known, otherwise
//      the fallback ticks) or flattens if the stop is already through the market. Runs on a 1-second
//      secondary series so it still reacts on slow tick bars. Also logs every rejected order and every
//      exit order cancelled while a position is open to the NinjaScript Output window.
// ─────────────────────────────────────────────────────────────────────────────
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript
{
	public enum GodTradesStrategyTargetMode
	{
		FixedTicks,
		OppositeBand
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public class GodTradesStrategy_v1_0_0 : Strategy
	{
		// ── shared gap-line engine (feeds Setups 2/3/4 + the Spiderweb filter) ─────────────────
		private class GapLine
		{
			public double Price;
			public bool   Bullish;     // true = an up-gap (price skipped upward when this line formed)
			public int    FormedBar;
			public bool   Filled;
		}

		private List<GapLine> _gapLines = new List<GapLine>();
		private bool?         _lastFillDirLong = null;   // direction of the most recent valid gap-touch
		private int           _consecutiveSameDirFills = 0;
		private int           _signalSeq = 0;             // makes every SetStopLoss/EnterX signal name unique
		private bool          _announcedLive = false;      // one-time "I'm actually running" log line

		// diagnostic tallies only — printed as one summary line when the strategy stops, so the
		// full gap-line funnel (formed -> touched -> too-young vs valid) is visible without having
		// to count individual log lines by hand
		private int _totalGapsFormed = 0;
		private int _totalTouches    = 0;
		private int _totalTooYoung   = 0;
		private int _totalValidFills = 0;

		// ── protection watchdog (v1.0.3) ────────────────────────────────────────────────────────
		private class Prot { public string Signal; public bool IsLong; public double Stop; public double Target; public bool Filled; public DateTime FilledAt; }
		private readonly Dictionary<string, Prot> _prot = new Dictionary<string, Prot>();
		private readonly List<Order> _exitOrders = new List<Order>();   // this strategy's stop / target / watchdog orders
		private DateTime _unprotSince = DateTime.MinValue;
		private int      _wdSeq = 0;

		private Bollinger bollinger;
		private SMA       trendMa;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Four independently-toggleable setups from the 'GOD TRADES' slide deck — "
					+ "Bollinger Gap, Fill and Reverse, Fill and Continue, and Final Line — plus a shared "
					+ "Spiderweb veto filter. Enable any single one or all four together. Fresh, "
					+ "self-contained implementation; no other Sneaky/GodTrades files required.";
				Name = "GodTradesStrategy_v1_0_0";

				Calculate                    = Calculate.OnBarClose;   // every setup triggers "once this candle closes"
				EntriesPerDirection           = 20;                    // room for all 4 setups + both directions
				EntryHandling                 = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy  = false;
				IsFillLimitOnTouch            = false;
				MaximumBarsLookBack           = MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution           = OrderFillResolution.Standard;
				Slippage                      = 0;
				StartBehavior                 = StartBehavior.WaitUntilFlat;
				StopTargetHandling            = StopTargetHandling.PerEntryExecution;
				TimeInForce                   = TimeInForce.Gtc;
				TraceOrders                   = false;
				RealtimeErrorHandling         = RealtimeErrorHandling.StopCancelClose;
				BarsRequiredToTrade           = 20;

				EnforceTradingWindow = false;
				StartTimeHHMM        = 1015;
				EndTimeHHMM          = 1500;
				FlattenAtWindowEnd   = false;

				MinGapTicks         = 4;
				MinBarsBeforeValid  = 1;
				MaxGapLinesTracked  = 40;
				MaxGapLineAgeBars   = 500;

				BollingerPeriod = 20;
				BollingerStdDev = 2.0;

				EnableBollingerGap  = true;
				BgEnableLong        = true;
				BgEnableShort       = true;
				BgNearBandTicks     = 3;
				BgStopBufferTicks   = 2;
				BgTargetMode        = GodTradesStrategyTargetMode.OppositeBand;
				BgFixedTargetTicks  = 40;
				BgContracts         = 1;

				EnableFillReverse           = true;
				FrEnableLong                = true;
				FrEnableShort               = true;
				FrRequireLowVolumeApproach  = true;
				FrRequireBigPriorPush       = true;
				FrVolumeLookback            = 10;
				FrBigPushAtrMultiple        = 1.5;
				FrStopTicks                 = 8;
				FrTargetMode                = GodTradesStrategyTargetMode.FixedTicks;
				FrFixedTargetTicks          = 24;
				FrContracts                 = 1;

				EnableFillContinue     = true;
				FcEnableLong           = true;
				FcEnableShort          = true;
				FcTrendMaPeriod        = 50;
				FcTrendSlopeLookback   = 10;
				FcStopTicks            = 4;
				FcTargetMode           = GodTradesStrategyTargetMode.FixedTicks;
				FcFixedTargetTicks     = 20;
				FcContracts            = 1;

				EnableFinalLine             = true;
				FlEnableLong                = true;
				FlEnableShort               = true;
				FlMinPriorFillsInSequence   = 2;
				FlStopTicks                 = 4;
				FlTargetMode                = GodTradesStrategyTargetMode.FixedTicks;
				FlFixedTargetTicks          = 20;
				FlContracts                 = 1;

				EnableSpiderwebFilter    = true;
				SpiderwebProximityTicks  = 15;
				SpiderwebMinLines        = 5;
				SpiderwebLookbackBars    = 100;

				ShowGapLines      = true;
				ShowSignalMarkers = true;

				ProtectionWatchdog     = true;
				WatchdogGraceSeconds   = 10;
				FallbackStopTicks      = 60;
				FallbackTargetTicks    = 60;
			}
			else if (State == State.Configure)
			{
				// 1-second series used ONLY to run the protection watchdog (BarsInProgress == 1)
				if (ProtectionWatchdog)
					AddDataSeries(BarsPeriodType.Second, 1);
			}
			else if (State == State.DataLoaded)
			{
				bollinger = Bollinger(BollingerStdDev, BollingerPeriod);
				trendMa   = SMA(FcTrendMaPeriod);

				AddChartIndicator(bollinger);
				AddChartIndicator(trendMa);
			}
			else if (State == State.Terminated)
			{
				// one-line funnel summary so the whole "why so few trades" question can be answered
				// from a single log line instead of counting individual Print()s by hand
				Print("GodTradesStrategy funnel summary: gaps formed=" + _totalGapsFormed
					+ ", touched=" + _totalTouches + ", touched-too-young(invalidated)=" + _totalTooYoung
					+ ", valid fills(passed to setups)=" + _totalValidFills
					+ " [MinBarsBeforeValid=" + MinBarsBeforeValid + "]");
			}
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress == 1) { RunWatchdog(); return; }
			if (BarsInProgress != 0) return;
			if (CurrentBar < Math.Max(BollingerPeriod, FcTrendMaPeriod + FcTrendSlopeLookback) + 5) return;

			// One-time confirmation that OnBarUpdate is actually executing at all — if this line
			// never appears in the NinjaScript Output/Log window, the strategy isn't Enabled (or
			// isn't past its warm-up bar count yet), which is a different problem than "no setups
			// have fired" and is the first thing to check before assuming the gap/signal logic itself
			// is at fault.
			if (!_announcedLive)
			{
				_announcedLive = true;
				Print(Time[0] + " GodTradesStrategy is live and evaluating bars (State=" + State + ").");
			}

			try
			{
				// IMPORTANT ORDERING: check existing (previously-formed) gap lines against this bar
				// BEFORE registering a new gap line for this bar. A freshly-formed gap line sits close
				// to this bar's own Open, which is always within this bar's own High/Low range — so if
				// DetectNewGap() ran first, the line it just created would immediately "touch" itself
				// on the same bar (age=0) and get invalidated before it ever had a chance to age. Doing
				// CheckGapFills() first means a new line isn't tested until the NEXT bar at the earliest.
				List<GapLine> fills = CheckGapFills();

				DetectNewGap();

				if (EnableBollingerGap)
					EvaluateBollingerGap();

				foreach (GapLine fill in fills)
				{
					bool goLong = fill.Bullish;   // direction that continues this line's own original bias

					if (_lastFillDirLong.HasValue && _lastFillDirLong.Value == goLong)
						_consecutiveSameDirFills++;
					else
						_consecutiveSameDirFills = 1;
					_lastFillDirLong = goLong;

					bool isFinalLine = EnableFinalLine && _consecutiveSameDirFills >= FlMinPriorFillsInSequence;
					if (isFinalLine)
					{
						// deck: "insane rush to the OPPOSITE direction" once the final line fills
						Print(Time[0] + " GodTradesStrategy gap-fill @ " + fill.Price.ToString("0.####")
							+ " dir=" + (goLong ? "up" : "down") + " -> FINAL LINE (seq=" + _consecutiveSameDirFills
							+ "), trading " + (!goLong ? "long" : "short"));
						EvaluateFinalLineEntry(!goLong);
						_consecutiveSameDirFills = 0;
						continue;
					}

					bool trendUp         = trendMa[0] > trendMa[FcTrendSlopeLookback];
					bool trendDown       = trendMa[0] < trendMa[FcTrendSlopeLookback];
					bool continuesTrend  = (goLong && trendUp) || (!goLong && trendDown);

					// These Print lines are the diagnostic trail for "why didn't this gap-fill become a
					// trade" — every valid fill now says exactly where it was routed, and
					// EntryAllowed()/PassesFillReverseHelpers() each log their own specific block reason
					// below, so a quiet stretch of price with only gap dots and no trades can be explained
					// from the Log tab instead of guessed at.
					if (continuesTrend && EnableFillContinue)
					{
						Print(Time[0] + " GodTradesStrategy gap-fill @ " + fill.Price.ToString("0.####")
							+ " dir=" + (goLong ? "up" : "down") + " -> routed to FILL CONTINUE (trend-aligned)");
						EvaluateFillContinueEntry(goLong);
					}
					else if (!continuesTrend && EnableFillReverse)
					{
						Print(Time[0] + " GodTradesStrategy gap-fill @ " + fill.Price.ToString("0.####")
							+ " dir=" + (goLong ? "up" : "down") + " -> routed to FILL REVERSE (counter-trend)");
						EvaluateFillReverseEntry(goLong);
					}
					else
					{
						Print(Time[0] + " GodTradesStrategy gap-fill @ " + fill.Price.ToString("0.####")
							+ " dir=" + (goLong ? "up" : "down") + " -> no setup handles this branch (continuesTrend="
							+ continuesTrend + ", FillContinue enabled=" + EnableFillContinue
							+ ", FillReverse enabled=" + EnableFillReverse + ")");
					}
				}

				if (FlattenAtWindowEnd && EnforceTradingWindow && !IsInTradingWindow()
					&& Position.MarketPosition != MarketPosition.Flat)
				{
					if (Position.MarketPosition == MarketPosition.Long)
						ExitLong("WindowFlatten");
					else
						ExitShort("WindowFlatten");
				}
			}
			catch (Exception ex) { Print("GodTradesStrategy OnBarUpdate error: " + ex.Message); }
		}

		// ── gap-line engine ─────────────────────────────────────────────────────────────────────
		private void DetectNewGap()
		{
			double gapSize = Open[0] - Close[1];
			if (Math.Abs(gapSize) < MinGapTicks * TickSize) return;

			double gapPrice = (Open[0] + Close[1]) / 2.0;

			_gapLines.Add(new GapLine
			{
				Price     = gapPrice,
				Bullish   = gapSize > 0,
				FormedBar = CurrentBar,
				Filled    = false
			});
			_totalGapsFormed++;

			if (ShowGapLines)
				Draw.Dot(this, "GTGapForm" + CurrentBar, false, 0, gapPrice, Brushes.Yellow);

			PruneGapLines();
		}

		private void PruneGapLines()
		{
			for (int i = _gapLines.Count - 1; i >= 0; i--)
			{
				if (CurrentBar - _gapLines[i].FormedBar > MaxGapLineAgeBars)
					_gapLines.RemoveAt(i);
			}

			while (_gapLines.Count > MaxGapLinesTracked)
				_gapLines.RemoveAt(0);
		}

		// Checks every currently-unfilled line against this bar's range. Lines younger than
		// MinBarsBeforeValid are marked Filled (and removed) WITHOUT being returned as a signal —
		// matching the deck's own rule that an early fill is invalid and never signals. Every
		// touch (valid or not) is tallied and, if too young, logged with its actual age — this is
		// the visibility that was missing before: a trade count can look low either because setups
		// are being vetoed (window/spiderweb/helpers — all already logged elsewhere) or because most
		// gap lines simply never survive to MinBarsBeforeValid before getting re-touched. Only this
		// second path was previously invisible in the log.
		private List<GapLine> CheckGapFills()
		{
			List<GapLine> validFills = new List<GapLine>();

			for (int i = _gapLines.Count - 1; i >= 0; i--)
			{
				GapLine g = _gapLines[i];
				if (g.Filled) continue;

				bool touched = High[0] >= g.Price && Low[0] <= g.Price;
				if (!touched) continue;

				g.Filled = true;
				int age = CurrentBar - g.FormedBar;
				_totalTouches++;

				if (age >= MinBarsBeforeValid)
				{
					validFills.Add(g);
					_totalValidFills++;
				}
				else
				{
					_totalTooYoung++;
					Print(Time[0] + " GodTradesStrategy gap-line @ " + g.Price.ToString("0.####")
						+ " touched too early: age=" + age + " bar(s), needs " + MinBarsBeforeValid
						+ " — invalidated, no signal.");
				}

				_gapLines.RemoveAt(i);
			}

			return validFills;
		}

		private bool IsSpiderwebZone()
		{
			int count = 0;
			foreach (GapLine g in _gapLines)
			{
				if (g.Filled) continue;
				if (CurrentBar - g.FormedBar > SpiderwebLookbackBars) continue;
				if (Math.Abs(g.Price - Close[0]) <= SpiderwebProximityTicks * TickSize)
					count++;
			}
			return count >= SpiderwebMinLines;
		}

		private bool IsInTradingWindow()
		{
			int t     = ToTime(Time[0]);          // HHMMSS, assumes chart is on Eastern time per the deck's own rule
			int start = StartTimeHHMM * 100;
			int end   = EndTimeHHMM * 100;
			return t >= start && t <= end;
		}

		private bool EntryAllowed(string context)
		{
			if (EnforceTradingWindow && !IsInTradingWindow())
			{
				Print(Time[0] + " GodTradesStrategy " + context + " blocked: outside trading window (bar time="
					+ ToTime(Time[0]) + ", window=" + (StartTimeHHMM * 100) + "-" + (EndTimeHHMM * 100) + ")");
				return false;
			}
			if (EnableSpiderwebFilter && IsSpiderwebZone())
			{
				Print(Time[0] + " GodTradesStrategy " + context + " blocked: spiderweb filter active");
				return false;
			}
			return true;
		}

		// ── Setup 1: Bollinger Gap ──────────────────────────────────────────────────────────────
		private void EvaluateBollingerGap()
		{
			if (!EntryAllowed("BG")) return;

			bool bothGreenUp = Close[1] > Open[1] && Close[0] > Open[0] && High[1] < Low[0];
			bool bothRedDown = Close[1] < Open[1] && Close[0] < Open[0] && Low[1] > High[0];

			if (bothGreenUp)
			{
				if (!BgEnableLong) return;

				double nearBand = bollinger.Lower[0] + BgNearBandTicks * TickSize;
				if (Low[0] > nearBand && Low[1] > nearBand) return;   // deck: must be touching/very near the lower band

				double stopPx = Instrument.MasterInstrument.RoundToTickSize(Low[0] - BgStopBufferTicks * TickSize);
				double targetPx = BgTargetMode == GodTradesStrategyTargetMode.OppositeBand
					? bollinger.Upper[0]
					: Instrument.MasterInstrument.RoundToTickSize(Close[0] + BgFixedTargetTicks * TickSize);

				EnterTrade("BG", true, stopPx, targetPx, BgContracts);
			}
			else if (bothRedDown)
			{
				if (!BgEnableShort) return;

				double nearBand = bollinger.Upper[0] - BgNearBandTicks * TickSize;
				if (High[0] < nearBand && High[1] < nearBand) return;   // must be touching/very near the upper band

				double stopPx = Instrument.MasterInstrument.RoundToTickSize(High[0] + BgStopBufferTicks * TickSize);
				double targetPx = BgTargetMode == GodTradesStrategyTargetMode.OppositeBand
					? bollinger.Lower[0]
					: Instrument.MasterInstrument.RoundToTickSize(Close[0] - BgFixedTargetTicks * TickSize);

				EnterTrade("BG", false, stopPx, targetPx, BgContracts);
			}
		}

		// ── Setup 2: Fill and Reverse ────────────────────────────────────────────────────────────
		private bool PassesFillReverseHelpers()
		{
			if (FrRequireLowVolumeApproach)
			{
				double avgVol = SMA(Volume, FrVolumeLookback)[0];
				if (Volume[0] > avgVol)   // deck: "the first 2 green candles will contain VERY little volume"
				{
					Print(Time[0] + " GodTradesStrategy FR blocked: volume " + Volume[0] + " > " + FrVolumeLookback
						+ "-bar avg " + avgVol.ToString("0.##"));
					return false;
				}
			}

			if (FrRequireBigPriorPush)
			{
				double atr        = ATR(14)[1];
				double priorRange = High[1] - Low[1];
				if (priorRange < atr * FrBigPushAtrMultiple)   // deck: "big red candles... a final push then giving up"
				{
					Print(Time[0] + " GodTradesStrategy FR blocked: prior bar range " + priorRange.ToString("0.####")
						+ " < " + FrBigPushAtrMultiple + "x ATR(" + atr.ToString("0.####") + ")");
					return false;
				}
			}

			return true;
		}

		private void EvaluateFillReverseEntry(bool goLong)
		{
			if (!EntryAllowed("FR")) return;
			if (goLong && !FrEnableLong) return;
			if (!goLong && !FrEnableShort) return;
			if (!PassesFillReverseHelpers()) return;

			double stopPx, targetPx;
			if (goLong)
			{
				stopPx   = Instrument.MasterInstrument.RoundToTickSize(Close[0] - FrStopTicks * TickSize);
				targetPx = FrTargetMode == GodTradesStrategyTargetMode.OppositeBand
					? bollinger.Upper[0]
					: Instrument.MasterInstrument.RoundToTickSize(Close[0] + FrFixedTargetTicks * TickSize);
			}
			else
			{
				stopPx   = Instrument.MasterInstrument.RoundToTickSize(Close[0] + FrStopTicks * TickSize);
				targetPx = FrTargetMode == GodTradesStrategyTargetMode.OppositeBand
					? bollinger.Lower[0]
					: Instrument.MasterInstrument.RoundToTickSize(Close[0] - FrFixedTargetTicks * TickSize);
			}

			EnterTrade("FR", goLong, stopPx, targetPx, FrContracts);
		}

		// ── Setup 3: Fill and Continue ───────────────────────────────────────────────────────────
		private void EvaluateFillContinueEntry(bool goLong)
		{
			if (!EntryAllowed("FC")) return;
			if (goLong && !FcEnableLong) return;
			if (!goLong && !FcEnableShort) return;

			double stopPx, targetPx;
			if (goLong)
			{
				stopPx   = Instrument.MasterInstrument.RoundToTickSize(Close[0] - FcStopTicks * TickSize);
				targetPx = FcTargetMode == GodTradesStrategyTargetMode.OppositeBand
					? bollinger.Upper[0]
					: Instrument.MasterInstrument.RoundToTickSize(Close[0] + FcFixedTargetTicks * TickSize);
			}
			else
			{
				stopPx   = Instrument.MasterInstrument.RoundToTickSize(Close[0] + FcStopTicks * TickSize);
				targetPx = FcTargetMode == GodTradesStrategyTargetMode.OppositeBand
					? bollinger.Lower[0]
					: Instrument.MasterInstrument.RoundToTickSize(Close[0] - FcFixedTargetTicks * TickSize);
			}

			EnterTrade("FC", goLong, stopPx, targetPx, FcContracts);
		}

		// ── Setup 4: Final Line ──────────────────────────────────────────────────────────────────
		private void EvaluateFinalLineEntry(bool goLong)
		{
			if (!EntryAllowed("FL")) return;
			if (goLong && !FlEnableLong) return;
			if (!goLong && !FlEnableShort) return;

			double stopPx, targetPx;
			if (goLong)
			{
				stopPx   = Instrument.MasterInstrument.RoundToTickSize(Close[0] - FlStopTicks * TickSize);
				targetPx = FlTargetMode == GodTradesStrategyTargetMode.OppositeBand
					? bollinger.Upper[0]
					: Instrument.MasterInstrument.RoundToTickSize(Close[0] + FlFixedTargetTicks * TickSize);
			}
			else
			{
				stopPx   = Instrument.MasterInstrument.RoundToTickSize(Close[0] + FlStopTicks * TickSize);
				targetPx = FlTargetMode == GodTradesStrategyTargetMode.OppositeBand
					? bollinger.Lower[0]
					: Instrument.MasterInstrument.RoundToTickSize(Close[0] - FlFixedTargetTicks * TickSize);
			}

			EnterTrade("FL", goLong, stopPx, targetPx, FlContracts);
		}

		// ── the one place every setup's entry actually gets placed from ────────────────────────
		// NOTE: per the deck's explicit warning on Fill and Reverse ("moving to break-even is
		// suicidal on this trade, ensure you have a tiny stop and leave it"), this file deliberately
		// contains NO break-even / stop-management logic anywhere for ANY setup. Every stop is set
		// once, here, at entry, and then left alone until it or the target fills. Do not add a
		// trailing/break-even routine without re-reading that slide first.
		private void EnterTrade(string tag, bool isLong, double stopPrice, double targetPrice, int contracts)
		{
			string signal = tag + "_" + (_signalSeq++);

			_prot[signal] = new Prot { Signal = signal, IsLong = isLong, Stop = stopPrice, Target = targetPrice };
			SetStopLoss(signal, CalculationMode.Price, stopPrice, false);
			SetProfitTarget(signal, CalculationMode.Price, targetPrice);

			if (isLong) EnterLong(contracts, signal);
			else        EnterShort(contracts, signal);

			if (ShowSignalMarkers)
			{
				if (isLong) Draw.ArrowUp(this, "GTSig" + signal, false, 0, Low[0] - 2 * TickSize, Brushes.Lime);
				else        Draw.ArrowDown(this, "GTSig" + signal, false, 0, High[0] + 2 * TickSize, Brushes.Red);
			}

			Print(Time[0] + " GodTradesStrategy " + tag + " " + (isLong ? "LONG" : "SHORT")
				+ " qty=" + contracts + " stop=" + stopPrice.ToString("0.####") + " target=" + targetPrice.ToString("0.####"));
		}

		// ── protection watchdog + order diagnostics (v1.0.3) ───────────────────────────────────
		private static bool IsWorking(Order o)
		{
			return o != null && (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted || o.OrderState == OrderState.Submitted
				|| o.OrderState == OrderState.PartFilled || o.OrderState == OrderState.ChangePending || o.OrderState == OrderState.ChangeSubmitted);
		}
		private static bool IsStopOrder(Order o)   { return o.Name == "Stop loss"     || o.Name.StartsWith("GT_WD_SL"); }
		private static bool IsTargetOrder(Order o) { return o.Name == "Profit target" || o.Name.StartsWith("GT_WD_TP"); }

		private int LiveQty(bool stops)
		{
			int q = 0;
			foreach (Order o in _exitOrders)
				if (IsWorking(o) && (stops ? IsStopOrder(o) : IsTargetOrder(o)))
					q += Math.Max(o.Quantity - o.Filled, 0);
			return q;
		}

		protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
		{
			try
			{
				if (order == null || order.Name == null) return;
				bool isExit = order.Name == "Stop loss" || order.Name == "Profit target" || order.Name.StartsWith("GT_WD_");
				if (isExit)
				{
					if (!_exitOrders.Contains(order)) _exitOrders.Add(order);
					if (orderState == OrderState.Filled || orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
						_exitOrders.Remove(order);
				}
				if (orderState == OrderState.Rejected)
					Print(time + " GodTradesStrategy ORDER REJECTED: " + order.Name + " " + order.OrderAction + " qty=" + quantity + " stop=" + stopPrice + " limit=" + limitPrice + " error=" + error + " " + nativeError);
				else if (orderState == OrderState.Cancelled && isExit && Position.MarketPosition != MarketPosition.Flat)
					Print(time + " GodTradesStrategy exit order CANCELLED while position open: " + order.Name + " stop=" + stopPrice + " limit=" + limitPrice + " " + nativeError);
			}
			catch (Exception ex) { Print("GodTradesStrategy OnOrderUpdate error: " + ex.Message); }
		}

		protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
		{
			try
			{
				if (execution == null || execution.Order == null) return;
				Prot p;
				if (execution.Order.Name != null && _prot.TryGetValue(execution.Order.Name, out p) && !p.Filled) { p.Filled = true; p.FilledAt = time; }
				if (Position.MarketPosition == MarketPosition.Flat)
				{
					_prot.Clear();
					CancelWatchdogOrders();
				}
			}
			catch (Exception ex) { Print("GodTradesStrategy OnExecutionUpdate error: " + ex.Message); }
		}

		private void CancelWatchdogOrders()
		{
			foreach (Order o in new List<Order>(_exitOrders))
				if (o.Name.StartsWith("GT_WD_") && IsWorking(o)) CancelOrder(o);
		}

		// Runs on the 1-second secondary series. Realtime only. Never touches the entry logic.
		private void RunWatchdog()
		{
			try
			{
				if (!ProtectionWatchdog || State != State.Realtime || CurrentBars[1] < 1) return;
				MarketPosition mp = Position.MarketPosition;
				if (mp == MarketPosition.Flat) { _unprotSince = DateTime.MinValue; return; }

				int qty = Position.Quantity;
				int slQ = LiveQty(true), tpQ = LiveQty(false);
				if (slQ >= qty && tpQ >= qty) { _unprotSince = DateTime.MinValue; return; }

				if (_unprotSince == DateTime.MinValue) { _unprotSince = DateTime.Now; return; }   // grace period: new orders are still being acknowledged
				if ((DateTime.Now - _unprotSince).TotalSeconds < WatchdogGraceSeconds) return;

				bool isLong = mp == MarketPosition.Long;
				double last = Closes[1][0];
				double avg  = Position.AveragePrice;

				// original stop/target if exactly one filled entry is being tracked; otherwise the fallback distances
				Prot only = null; int n = 0;
				foreach (Prot p in _prot.Values) if (p.Filled) { only = p; n++; }
				if (n != 1) only = null;

				double stopPx = only != null ? only.Stop   : (isLong ? avg - FallbackStopTicks   * TickSize : avg + FallbackStopTicks   * TickSize);
				double tgtPx  = only != null ? only.Target : (isLong ? avg + FallbackTargetTicks * TickSize : avg - FallbackTargetTicks * TickSize);
				stopPx = Instrument.MasterInstrument.RoundToTickSize(stopPx);
				tgtPx  = Instrument.MasterInstrument.RoundToTickSize(tgtPx);

				Print(Time[0] + " GodTradesStrategy WATCHDOG: position " + mp + " x" + qty + " @ " + avg.ToString("0.00") + " has stops=" + slQ + " targets=" + tpQ + " -> repairing");

				bool stopThroughMarket = isLong ? stopPx >= last - TickSize : stopPx <= last + TickSize;
				if (slQ < qty && stopThroughMarket)
				{
					Print(Time[0] + " GodTradesStrategy WATCHDOG: stop " + stopPx + " is already through the market (" + last + ") -> flattening");
					if (isLong) ExitLong(0, qty, "GT_WD_Flat" + (++_wdSeq), ""); else ExitShort(0, qty, "GT_WD_Flat" + (++_wdSeq), "");
					_unprotSince = DateTime.MinValue;
					return;
				}
				if (slQ < qty)
				{
					if (isLong) ExitLongStopMarket(0, true, qty - slQ, stopPx, "GT_WD_SL" + (++_wdSeq), "");
					else        ExitShortStopMarket(0, true, qty - slQ, stopPx, "GT_WD_SL" + (++_wdSeq), "");
				}
				if (tpQ < qty)
				{
					if (isLong) ExitLongLimit(0, true, qty - tpQ, tgtPx, "GT_WD_TP" + (++_wdSeq), "");
					else        ExitShortLimit(0, true, qty - tpQ, tgtPx, "GT_WD_TP" + (++_wdSeq), "");
				}
				_unprotSince = DateTime.MinValue;
			}
			catch (Exception ex) { Print("GodTradesStrategy watchdog error: " + ex.Message); }
		}

		#region Properties

		[NinjaScriptProperty]
		[Display(Name = "Protection watchdog", Description = "Re-place missing stop/target orders (or flatten) if a position is open without protection. Adds a 1-second data series.", GroupName = "98. Safety", Order = 1)]
		public bool ProtectionWatchdog { get; set; }

		[NinjaScriptProperty]
		[Range(2, 300)]
		[Display(Name = "Watchdog grace (seconds)", Description = "How long a position may be under-protected before the watchdog acts.", GroupName = "98. Safety", Order = 2)]
		public int WatchdogGraceSeconds { get; set; }

		[NinjaScriptProperty]
		[Range(1, 2000)]
		[Display(Name = "Fallback stop (ticks)", Description = "Used only when the original stop price is unknown.", GroupName = "98. Safety", Order = 3)]
		public int FallbackStopTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, 2000)]
		[Display(Name = "Fallback target (ticks)", Description = "Used only when the original target price is unknown.", GroupName = "98. Safety", Order = 4)]
		public int FallbackTargetTicks { get; set; }
		[NinjaScriptProperty]
		[Display(Name = "Enforce trading window", GroupName = "01. Trading Window", Order = 1)]
		public bool EnforceTradingWindow { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Window start (HHMM)", Description = "Deck default: 1015 (10:15am). Assumes chart is on Eastern time.", GroupName = "01. Trading Window", Order = 2)]
		public int StartTimeHHMM { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Window end (HHMM)", Description = "Deck default: 1500 (3:00pm). Assumes chart is on Eastern time.", GroupName = "01. Trading Window", Order = 3)]
		public int EndTimeHHMM { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Flatten open positions at window end", Description = "Not in the deck; off by default. If on, force-exits any open position once the trading window ends (new entries are already blocked outside the window regardless).", GroupName = "01. Trading Window", Order = 4)]
		public bool FlattenAtWindowEnd { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Min gap size (ticks)", Description = "Smallest close-to-open price skip that registers a tracked gap line for Setups 2/3/4.", GroupName = "02. Gap Engine", Order = 1)]
		public int MinGapTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Min bars before valid", Description = "Deck rule: a gap line must survive this many bars unfilled before a touch counts as a real signal. Course rule: 3.", GroupName = "02. Gap Engine", Order = 2)]
		public int MinBarsBeforeValid { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Max gap lines tracked", GroupName = "02. Gap Engine", Order = 3)]
		public int MaxGapLinesTracked { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Max gap line age (bars)", Description = "Untouched lines older than this are dropped as stale.", GroupName = "02. Gap Engine", Order = 4)]
		public int MaxGapLineAgeBars { get; set; }

		[NinjaScriptProperty]
		[Range(2, int.MaxValue)]
		[Display(Name = "Bollinger period", GroupName = "03. Bollinger Bands", Order = 1)]
		public int BollingerPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Bollinger std dev", GroupName = "03. Bollinger Bands", Order = 2)]
		public double BollingerStdDev { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable Bollinger Gap", GroupName = "04. Setup 1 - Bollinger Gap", Order = 1)]
		public bool EnableBollingerGap { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Trade long", Description = "Take the long-side (bottom-band) version of this setup.", GroupName = "04. Setup 1 - Bollinger Gap", Order = 2)]
		public bool BgEnableLong { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Trade short", Description = "Take the short-side (top-band) version of this setup.", GroupName = "04. Setup 1 - Bollinger Gap", Order = 3)]
		public bool BgEnableShort { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Near-band tolerance (ticks)", Description = "How close to the band counts as 'touching (or VERY NEAR)'.", GroupName = "04. Setup 1 - Bollinger Gap", Order = 4)]
		public int BgNearBandTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Stop buffer (ticks)", Description = "Extra ticks beyond the entry candle's own high/low.", GroupName = "04. Setup 1 - Bollinger Gap", Order = 5)]
		public int BgStopBufferTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Target mode", Description = "Deck default: opposite Bollinger band ('Band to Band Profits').", GroupName = "04. Setup 1 - Bollinger Gap", Order = 6)]
		public GodTradesStrategyTargetMode BgTargetMode { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Fixed target (ticks)", Description = "Used only when Target mode = FixedTicks.", GroupName = "04. Setup 1 - Bollinger Gap", Order = 7)]
		public int BgFixedTargetTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Contracts", GroupName = "04. Setup 1 - Bollinger Gap", Order = 8)]
		public int BgContracts { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable Fill and Reverse", GroupName = "05. Setup 2 - Fill and Reverse", Order = 1)]
		public bool EnableFillReverse { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Trade long", Description = "Take Fill and Reverse trades on the long side.", GroupName = "05. Setup 2 - Fill and Reverse", Order = 2)]
		public bool FrEnableLong { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Trade short", Description = "Take Fill and Reverse trades on the short side.", GroupName = "05. Setup 2 - Fill and Reverse", Order = 3)]
		public bool FrEnableShort { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Require low-volume approach", Description = "Helper from the deck: the reversal candles should show very little volume/delta.", GroupName = "05. Setup 2 - Fill and Reverse", Order = 4)]
		public bool FrRequireLowVolumeApproach { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Require big prior push", Description = "Helper from the deck: a big candle just before the touch (a final push then giving up).", GroupName = "05. Setup 2 - Fill and Reverse", Order = 5)]
		public bool FrRequireBigPriorPush { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Volume average lookback", GroupName = "05. Setup 2 - Fill and Reverse", Order = 6)]
		public int FrVolumeLookback { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Big-push ATR multiple", GroupName = "05. Setup 2 - Fill and Reverse", Order = 7)]
		public double FrBigPushAtrMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Stop (ticks)", Description = "Deck: keep this TINY and never move it to break-even.", GroupName = "05. Setup 2 - Fill and Reverse", Order = 8)]
		public int FrStopTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Target mode", GroupName = "05. Setup 2 - Fill and Reverse", Order = 9)]
		public GodTradesStrategyTargetMode FrTargetMode { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Fixed target (ticks)", GroupName = "05. Setup 2 - Fill and Reverse", Order = 10)]
		public int FrFixedTargetTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Contracts", GroupName = "05. Setup 2 - Fill and Reverse", Order = 11)]
		public int FrContracts { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable Fill and Continue", GroupName = "06. Setup 3 - Fill and Continue", Order = 1)]
		public bool EnableFillContinue { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Trade long", Description = "Take Fill and Continue trades on the long side.", GroupName = "06. Setup 3 - Fill and Continue", Order = 2)]
		public bool FcEnableLong { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Trade short", Description = "Take Fill and Continue trades on the short side.", GroupName = "06. Setup 3 - Fill and Continue", Order = 3)]
		public bool FcEnableShort { get; set; }

		[NinjaScriptProperty]
		[Range(2, int.MaxValue)]
		[Display(Name = "Trend MA period", GroupName = "06. Setup 3 - Fill and Continue", Order = 4)]
		public int FcTrendMaPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Trend slope lookback (bars)", Description = "Trend MA now vs. this many bars ago decides trend direction.", GroupName = "06. Setup 3 - Fill and Continue", Order = 5)]
		public int FcTrendSlopeLookback { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Stop (ticks)", Description = "Deck: keep this TINY and never move it to break-even.", GroupName = "06. Setup 3 - Fill and Continue", Order = 6)]
		public int FcStopTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Target mode", GroupName = "06. Setup 3 - Fill and Continue", Order = 7)]
		public GodTradesStrategyTargetMode FcTargetMode { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Fixed target (ticks)", GroupName = "06. Setup 3 - Fill and Continue", Order = 8)]
		public int FcFixedTargetTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Contracts", GroupName = "06. Setup 3 - Fill and Continue", Order = 9)]
		public int FcContracts { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable Final Line", GroupName = "07. Setup 4 - Final Line", Order = 1)]
		public bool EnableFinalLine { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Trade long", Description = "Take Final Line trades on the long side.", GroupName = "07. Setup 4 - Final Line", Order = 2)]
		public bool FlEnableLong { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Trade short", Description = "Take Final Line trades on the short side.", GroupName = "07. Setup 4 - Final Line", Order = 3)]
		public bool FlEnableShort { get; set; }

		[NinjaScriptProperty]
		[Range(2, int.MaxValue)]
		[Display(Name = "Min prior fills in sequence", Description = "How many same-direction gap-touches in a row before the next one is treated as the Final Line (traded in reverse).", GroupName = "07. Setup 4 - Final Line", Order = 4)]
		public int FlMinPriorFillsInSequence { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Stop (ticks)", GroupName = "07. Setup 4 - Final Line", Order = 5)]
		public int FlStopTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Target mode", GroupName = "07. Setup 4 - Final Line", Order = 6)]
		public GodTradesStrategyTargetMode FlTargetMode { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Fixed target (ticks)", Description = "Deck frames the Final Line as an 'insane rush' move — default is set larger than the other setups.", GroupName = "07. Setup 4 - Final Line", Order = 7)]
		public int FlFixedTargetTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Contracts", GroupName = "07. Setup 4 - Final Line", Order = 8)]
		public int FlContracts { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable Spiderweb filter", Description = "Deck: 'use EXTREME CAUTION' when lines cluster — vetoes ALL new entries while active.", GroupName = "08. Spiderweb Filter", Order = 1)]
		public bool EnableSpiderwebFilter { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Cluster proximity (ticks)", GroupName = "08. Spiderweb Filter", Order = 2)]
		public int SpiderwebProximityTicks { get; set; }

		[NinjaScriptProperty]
		[Range(2, int.MaxValue)]
		[Display(Name = "Min lines to call it a cluster", GroupName = "08. Spiderweb Filter", Order = 3)]
		public int SpiderwebMinLines { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Cluster lookback (bars)", GroupName = "08. Spiderweb Filter", Order = 4)]
		public int SpiderwebLookbackBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show gap lines", GroupName = "09. Visuals", Order = 1)]
		public bool ShowGapLines { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show signal markers", GroupName = "09. Visuals", Order = 2)]
		public bool ShowSignalMarkers { get; set; }
		#endregion
	}
}
