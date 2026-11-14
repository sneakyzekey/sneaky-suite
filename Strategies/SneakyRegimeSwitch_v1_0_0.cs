#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

// ═════════════════════════════════════════════════════════════════════════════
//  SneakyRegimeSwitch — a single self-contained Strategy that (1) classifies the
//  current market as Trending, Ranging, or Choppy every bar, using an ADX +
//  Bollinger-Band-width composite, and (2) runs a different, purpose-built
//  sub-bot depending on which regime it thinks it's in, handing off cleanly
//  when the regime changes.
//
//  DESIGN CHOICES (from the conversation that spec'd this out):
//   - ONE Strategy, not a meta-controller over separate Strategies. Easiest to
//     backtest as a single coherent unit in the Strategy Analyzer, and there's
//     no clean built-in NT8 API for one Strategy to enable/disable another.
//   - Regime detector = ADX (trend strength) + Bollinger Band width relative to
//     its own recent average (tight = consolidation, wide-without-trend = chop).
//     Deliberately NOT built from the Sentinel Lab's excursion-clustering
//     approach — this is a fresh, textbook-style composite, built fast so you
//     have something to point the Lab tooling AT, not something the Lab already
//     validated. See the honest-confidence note below.
//   - Each regime's trading logic is fresh, simple, purpose-built for this file
//     — not reused pieces of TurtleTrendBreakout / DrummondEnvelopeSqueeze /
//     etc. If you like one of those better for a given regime, swap the block.
//   - Target: NQ, matching the rest of Sneaky Suite, but nothing here is
//     NQ-specific — it's timeframe/instrument-agnostic like your other
//     strategies, tune the thresholds per instrument.
//
//  ⚠ HONEST CONFIDENCE NOTE: every threshold below (ADX 20/25, BB width ratios,
//  ATR multipliers, RSI 30/70) is a reasonable textbook starting point, NOT
//  fitted or validated against your data. Before trusting this with real size,
//  run it through the Strategy Analyzer across a real sample, and ideally
//  through your own Lab's uniqueness-weighted Wilson-CI treatment the way you
//  did with the ORB sweeps and sensor_truth.py output — a regime-switcher is
//  exactly the kind of thing that can look good in aggregate while each
//  individual regime's sample size is too small to trust on its own.
//
//  WHAT HAPPENS ON A REGIME SWITCH: by default (CloseOnRegimeChange = true) any
//  open position is flattened the instant a new regime is CONFIRMED (not just
//  detected — see RegimeConfirmBars below), and the new regime's bot waits one
//  full bar before it's allowed to enter, so you never get a flatten-and-
//  immediately-re-enter on the same bar. Turn CloseOnRegimeChange off if you'd
//  rather let a position ride and just stop taking NEW entries under the old
//  regime's logic once the regime changes underneath it — but see the note
//  right below if you do that.
//
//  KNOWN LIMITATION if CloseOnRegimeChange is off: each bot (Trending/Ranging/
//  Choppy) only manages its OWN open position while ITS OWN regime is the
//  active one. If the regime moves on while a position from a different bot
//  is still open, that position stops getting its trail/stop/target
//  resubmitted until the regime comes back around to whichever bot opened it.
//  A prior version of this file tried to fix that by tracking which bot
//  "owns" the open position and always managing it regardless of the active
//  regime — that attempt introduced a worse bug (a stale NaN could leak into
//  the stop price, silently leaving a position with NO stop at all) and was
//  reverted. If you want a real safety net that's completely immune to this
//  class of bug, turn on Use Hard Tick Stop/Target below — it's a fixed-tick
//  profit target and stop loss attached natively by NinjaTrader itself
//  (SetProfitTarget/SetStopLoss), independent of this file's own per-bar
//  regime dispatch logic entirely, so it can't be affected by any of the
//  above.
// ═════════════════════════════════════════════════════════════════════════════

namespace NinjaTrader.NinjaScript
{
	// Lives in the parent NinjaTrader.NinjaScript namespace (not ...Strategies) so
	// NT8's zip-import cache-accessor regen resolves the [NinjaScriptProperty] enum
	// type correctly — the same gotcha documented elsewhere in this project.
	public enum SneakyMarketRegime { Warmup = 0, Trending = 1, Ranging = 2, Choppy = 3 }
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public class SneakyRegimeSwitch_v1_0_0 : Strategy
	{
		// ── Regime detection state ──
		private ADX _adx;
		private Bollinger _bb;
		private ATR _atr;
		private RSI _rsi;
		private MAX _donchianHigh;
		private MIN _donchianLow;
		private Series<double> _bbWidthSeries;
		private SMA _bbWidthAvg;

		private SneakyMarketRegime _currentRegime = SneakyMarketRegime.Warmup;
		private SneakyMarketRegime _candidateRegime = SneakyMarketRegime.Warmup;
		private int _candidateStreak;
		private int _lastRegimeChangeBar = -1;

		// ── Trend sub-bot state (chandelier trail reference) ──
		private double _trendExtremeSinceEntry = double.NaN;
		private double _trendEntryPrice = double.NaN;

		// ── Ranging sub-bot state ──
		private double _rangeHardStop = double.NaN;

		// ── Session PnL tracking. "Session" here = 6:00 PM through 5:00 PM the next
		// day (the standard CME/Globex-style trade date), NOT NinjaTrader's own
		// session-template boundaries -- this is computed independently from the
		// bar timestamps so it matches regardless of how the instrument's session
		// template is configured. ──
		private static readonly TimeSpan SessionStartTimeOfDay = new TimeSpan(18, 0, 0); // 6:00 PM
		private DateTime _currentSessionStart = DateTime.MinValue;
		private double _sessionRealizedPnl;
		private int _lastTradeCountSeen;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Detects Trending / Ranging / Choppy regime each bar (ADX + Bollinger Band width "
					+ "composite) and runs a different, fresh, purpose-built sub-bot per regime, flattening and "
					+ "handing off cleanly when the regime changes. See the header comment for design choices and "
					+ "the honest-confidence caveat — thresholds here are reasonable starting points, not yet "
					+ "validated against real data. Run through the Strategy Analyzer and your Lab's Wilson-CI "
					+ "tooling before trusting live.";
				Name                                       = "SneakyRegimeSwitch v1.0.0";
				Calculate                                  = Calculate.OnBarClose;
				EntriesPerDirection                        = 1;
				EntryHandling                               = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy                = true;
				ExitOnSessionCloseSeconds                   = 30;
				IsFillLimitOnTouch                          = false;
				MaximumBarsLookBack                         = MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution                         = OrderFillResolution.Standard;
				Slippage                                    = 0;
				StartBehavior                               = StartBehavior.WaitUntilFlat;
				TimeInForce                                 = TimeInForce.Gtc;
				TraceOrders                                 = false;
				RealtimeErrorHandling                       = RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling                          = StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade                         = 20;
				IsInstantiatedOnEachOptimizationIteration    = true;
				IsOverlay                                   = true;
				PaintRegimeBarColors                        = true;

				// Regime detection
				AdxPeriod          = 14;
				AdxTrendThreshold  = 25;
				AdxRangeThreshold  = 20;   // must be < AdxTrendThreshold — the 20-25 gap is the hysteresis buffer
				BbPeriod           = 20;
				BbStdDevMult       = 2.0;
				BbWidthLookback    = 100;
				BbWidthTightRatio  = 0.75;
				RegimeConfirmBars  = 3;
				CloseOnRegimeChange = true;

				// Trending sub-bot
				DonchianPeriod = 20;
				AtrPeriod      = 14;
				AtrStopMult    = 1.5;  // tighter than the trail so it actually caps early risk (see Properties description)
				AtrTrailMult   = 2.5;

				// Ranging sub-bot
				RsiPeriod      = 14;
				RsiOversold    = 30;
				RsiOverbought  = 70;
				RangeAtrStopMult = 2.0;

				// Choppy sub-bot
				AllowChoppyTrading = false;
				ChoppyBbPeriod     = 10;
				ChoppyBbStdDevMult = 1.5;

				DefaultQuantity = 1;

				// Optional hard tick-based bracket (off by default) — see the
				// header comment for why this exists as an alternative safety net.
				UseHardTickStops      = false;
				HardStopLossTicks     = 40;
				HardProfitTargetTicks = 80;
			}
			else if (State == State.Configure)
			{
				// SetStopLoss/SetProfitTarget must be called here, not from
				// SetDefaults -- NinjaScript only applies the user's own edited
				// property values (from the Strategy Parameters dialog) to this
				// instance AFTER SetDefaults finishes, so reading
				// HardStopLossTicks/HardProfitTargetTicks any earlier would just
				// see the hardcoded defaults above no matter what the user set.
				// Calling these with no signal name applies them to every entry
				// this strategy submits, regardless of which bot or regime opened
				// it -- unlike everything else in this file, NinjaTrader manages
				// the resulting stop/target orders natively, so this can't be
				// broken by a dispatch bug in this strategy's own OnBarUpdate code.
				if (UseHardTickStops)
				{
					SetStopLoss(CalculationMode.Ticks, HardStopLossTicks);
					SetProfitTarget(CalculationMode.Ticks, HardProfitTargetTicks);
				}
			}
			else if (State == State.DataLoaded)
			{
				_adx          = ADX(Math.Max(1, AdxPeriod));
				_bb           = Bollinger(BbStdDevMult, Math.Max(1, BbPeriod));
				_atr          = ATR(Math.Max(1, AtrPeriod));
				_rsi          = RSI(Math.Max(1, RsiPeriod), 1);
				_donchianHigh = MAX(High, Math.Max(2, DonchianPeriod));
				_donchianLow  = MIN(Low, Math.Max(2, DonchianPeriod));
				_bbWidthSeries = new Series<double>(this);
				_bbWidthAvg    = SMA(_bbWidthSeries, Math.Max(2, BbWidthLookback));

				AddChartIndicator(_bb);

				if (AdxRangeThreshold >= AdxTrendThreshold)
					Print("[SneakyRegimeSwitch] WARNING: AdxRangeThreshold (" + AdxRangeThreshold
						+ ") should be LESS than AdxTrendThreshold (" + AdxTrendThreshold + ") — "
						+ "without a gap between them there's no hysteresis buffer and regime will flap "
						+ "on every ADX tick near the threshold.");
			}
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0)
				return;

			// Runs every bar, including warmup, so a 6pm session rollover during
			// warmup isn't missed.
			UpdateSessionPnl();

			int warmup = Math.Max(BarsRequiredToTrade,
				Math.Max(AdxPeriod, Math.Max(BbPeriod, Math.Max(BbWidthLookback,
				Math.Max(AtrPeriod, Math.Max(RsiPeriod, DonchianPeriod))))) + 2);

			if (CurrentBar < warmup)
			{
				_bbWidthSeries[0] = 0.0;
				return;
			}

			// ── 1. Compute the raw regime inputs this bar ───────────────────────────
			double adxVal   = _adx[0];
			double bbUpper  = _bb.Upper[0];
			double bbLower  = _bb.Lower[0];
			double bbBasis  = _bb.Middle[0];
			double bbWidth  = bbBasis != 0 ? (bbUpper - bbLower) / Math.Abs(bbBasis) : 0.0;
			_bbWidthSeries[0] = bbWidth;
			double bbWidthAvg = _bbWidthAvg[0];

			// ── 2. Classify this bar's RAW regime (before debounce) ─────────────────
			// The ADX 20-25 gap is a deliberate dead zone: a reading inside it doesn't
			// push toward Trending OR toward Range/Chop — it just keeps whatever the
			// bar-by-bar candidate already was, so ADX oscillating right at one fixed
			// threshold can't flap the classification back and forth by itself.
			SneakyMarketRegime rawRegime;
			if (adxVal > AdxTrendThreshold)
			{
				rawRegime = SneakyMarketRegime.Trending;
			}
			else if (adxVal < AdxRangeThreshold)
			{
				bool isTight = bbWidthAvg > 0 && bbWidth < bbWidthAvg * BbWidthTightRatio;
				rawRegime = isTight ? SneakyMarketRegime.Ranging : SneakyMarketRegime.Choppy;
			}
			else
			{
				rawRegime = _candidateRegime == SneakyMarketRegime.Warmup ? SneakyMarketRegime.Choppy : _candidateRegime;
			}

			// ── 3. Debounce: require RegimeConfirmBars consecutive bars agreeing ────
			// before an actual switch happens. A regime switch has real costs (it
			// flattens a position by default) so we don't want to react to one noisy
			// bar's ADX/BB reading.
			if (rawRegime == _candidateRegime)
				_candidateStreak++;
			else
			{
				_candidateRegime = rawRegime;
				_candidateStreak = 1;
			}

			bool justSwitched = false;
			if (_candidateStreak >= Math.Max(1, RegimeConfirmBars) && _candidateRegime != _currentRegime)
			{
				if (CloseOnRegimeChange && Position.MarketPosition != MarketPosition.Flat)
				{
					if (Position.MarketPosition == MarketPosition.Long)
						ExitLong("RegimeFlatten");
					else
						ExitShort("RegimeFlatten");
				}
				_currentRegime = _candidateRegime;
				_lastRegimeChangeBar = CurrentBar;
				justSwitched = true;
			}

			UpdateRegimeVisuals(_currentRegime, adxVal, bbWidth, bbWidthAvg);

			// One full bar of cooldown after a flatten-on-switch before any sub-bot
			// is allowed a fresh entry — avoids a same-bar flatten-then-re-enter.
			bool entriesAllowed = !justSwitched && CurrentBar > _lastRegimeChangeBar;

			// Clear each bot's own entry/trail bookkeeping once we're ACTUALLY
			// flat — gated on Flat, not just on a switch being confirmed. Wiping
			// these unconditionally the instant a switch fires (regardless of
			// whether the old position actually closed) is what caused a real
			// regression in an earlier version of this file: with
			// CloseOnRegimeChange off, a still-open position's _trendEntryPrice
			// could get wiped to NaN out from under it, and Math.Max/Math.Min
			// against a NaN entry price propagates NaN straight into the next
			// stop order's price, which NinjaTrader can't place — leaving that
			// position with literally no stop for the rest of its life. Gating
			// on Flat avoids that regardless of which architecture is above it.
			if (Position.MarketPosition == MarketPosition.Flat)
			{
				_trendExtremeSinceEntry = double.NaN;
				_trendEntryPrice = double.NaN;
				_rangeHardStop = double.NaN;
			}

			switch (_currentRegime)
			{
				case SneakyMarketRegime.Trending:
					RunTrendingBot(entriesAllowed);
					break;
				case SneakyMarketRegime.Ranging:
					RunRangingBot(entriesAllowed);
					break;
				case SneakyMarketRegime.Choppy:
					RunChoppyBot(entriesAllowed);
					break;
			}
		}

		// ── Trending sub-bot: Donchian breakout + ATR chandelier trail ──────────────
		// NOTE: only manages the position while Trending is the ACTIVE regime — see
		// the "KNOWN LIMITATION" note in the header comment. If you want a position
		// protected no matter what the regime dispatch is doing, turn on Use Hard
		// Tick Stop/Target instead of trying to work around this here.
		private void RunTrendingBot(bool entriesAllowed)
		{
			double atrVal = _atr[0];

			if (Position.MarketPosition == MarketPosition.Flat)
			{
				if (!entriesAllowed)
					return;

				double priorHigh = _donchianHigh[1];
				double priorLow  = _donchianLow[1];

				if (Close[0] > priorHigh)
				{
					EnterLong(DefaultQuantity, "TrendLong");
					_trendExtremeSinceEntry = High[0];
					_trendEntryPrice = Close[0];
				}
				else if (Close[0] < priorLow)
				{
					EnterShort(DefaultQuantity, "TrendShort");
					_trendExtremeSinceEntry = Low[0];
					_trendEntryPrice = Close[0];
				}
				return;
			}

			if (UseHardTickStops)
				return; // the native fixed-tick bracket is already handling this

			// In a position: maintain an ATR chandelier trail, floored (long) /
			// capped (short) by a fixed hard stop measured from the ENTRY price,
			// not the extreme. Set AtrStopMult tighter than AtrTrailMult for a
			// smaller initial risk that only loosens (via the trail taking over)
			// once price has moved favorably enough for the trail to sit tighter
			// than the hard stop on its own. Re-issuing the exit order each bar
			// with an updated stop price is the standard NinjaScript idiom for a
			// trailing stop — NT8 modifies the existing working order to the new
			// price rather than stacking duplicate orders.
			if (Position.MarketPosition == MarketPosition.Long)
			{
				_trendExtremeSinceEntry = double.IsNaN(_trendExtremeSinceEntry)
					? High[0] : Math.Max(_trendExtremeSinceEntry, High[0]);
				double trailStop = _trendExtremeSinceEntry - AtrTrailMult * atrVal;
				double hardStop  = _trendEntryPrice - AtrStopMult * atrVal;
				double stopPrice = Math.Max(trailStop, hardStop); // tighter (higher) of the two wins
				ExitLongStopMarket(0, true, Position.Quantity, stopPrice, "TrendTrail", "TrendLong");
			}
			else if (Position.MarketPosition == MarketPosition.Short)
			{
				_trendExtremeSinceEntry = double.IsNaN(_trendExtremeSinceEntry)
					? Low[0] : Math.Min(_trendExtremeSinceEntry, Low[0]);
				double trailStop = _trendExtremeSinceEntry + AtrTrailMult * atrVal;
				double hardStop  = _trendEntryPrice + AtrStopMult * atrVal;
				double stopPrice = Math.Min(trailStop, hardStop); // tighter (lower) of the two wins
				ExitShortStopMarket(0, true, Position.Quantity, stopPrice, "TrendTrail", "TrendShort");
			}
		}

		// ── Ranging sub-bot: Bollinger Band + RSI mean reversion ───────────────────
		// Targets the current basis (moving), protected by a hard ATR stop beyond
		// the touched band — that stop matters most exactly when the regime read
		// turns out to be wrong and a "range" breaks into a real trend.
		private void RunRangingBot(bool entriesAllowed)
		{
			double bbUpper = _bb.Upper[0];
			double bbLower = _bb.Lower[0];
			double bbBasis = _bb.Middle[0];
			double rsiVal  = _rsi.Value[0];
			double atrVal  = _atr[0];

			if (Position.MarketPosition == MarketPosition.Flat)
			{
				if (!entriesAllowed)
					return;

				if (Close[0] <= bbLower && rsiVal <= RsiOversold)
				{
					EnterLong(DefaultQuantity, "RangeLong");
					_rangeHardStop = Close[0] - RangeAtrStopMult * atrVal;
				}
				else if (Close[0] >= bbUpper && rsiVal >= RsiOverbought)
				{
					EnterShort(DefaultQuantity, "RangeShort");
					_rangeHardStop = Close[0] + RangeAtrStopMult * atrVal;
				}
				return;
			}

			if (UseHardTickStops)
				return; // the native fixed-tick bracket is already handling this

			if (Position.MarketPosition == MarketPosition.Long)
			{
				ExitLongLimit(0, true, Position.Quantity, bbBasis, "RangeTarget", "RangeLong");
				if (!double.IsNaN(_rangeHardStop))
					ExitLongStopMarket(0, true, Position.Quantity, _rangeHardStop, "RangeStop", "RangeLong");
			}
			else if (Position.MarketPosition == MarketPosition.Short)
			{
				ExitShortLimit(0, true, Position.Quantity, bbBasis, "RangeTarget", "RangeShort");
				if (!double.IsNaN(_rangeHardStop))
					ExitShortStopMarket(0, true, Position.Quantity, _rangeHardStop, "RangeStop", "RangeShort");
			}
		}

		// ── Choppy sub-bot: flat by default. Chop is the regime where every
		// backtest-friendly heuristic tends to bleed on real fills (wide spreads
		// relative to range, frequent false breaks) — the safe, defensible default
		// is to sit out and preserve capital rather than force a signal. If you set
		// AllowChoppyTrading = true, this runs a smaller, tighter mirror of the
		// ranging logic on a shorter Bollinger Band — genuinely experimental, not
		// something to trust without running it through the Lab first.
		private void RunChoppyBot(bool entriesAllowed)
		{
			if (!AllowChoppyTrading)
			{
				// Still manage an existing position defensively if one is somehow open
				// (e.g. CloseOnRegimeChange was turned off) — a wide ATR stop only,
				// no new entries, no profit-chasing logic in a regime we don't trust.
				if (!UseHardTickStops)
				{
					if (Position.MarketPosition == MarketPosition.Long && !double.IsNaN(_rangeHardStop))
						ExitLongStopMarket(_rangeHardStop, "ChoppyGuard");
					else if (Position.MarketPosition == MarketPosition.Short && !double.IsNaN(_rangeHardStop))
						ExitShortStopMarket(_rangeHardStop, "ChoppyGuard");
				}
				return;
			}

			// Optional minimal scalp — off by default, see the caveat above.
			double basis = SMA(Math.Max(2, ChoppyBbPeriod))[0];
			double dev   = ChoppyBbStdDevMult * StdDev(Math.Max(2, ChoppyBbPeriod))[0];
			double upper = basis + dev;
			double lower = basis - dev;
			double atrVal = _atr[0];

			if (Position.MarketPosition == MarketPosition.Flat)
			{
				if (!entriesAllowed)
					return;

				if (Close[0] <= lower)
				{
					EnterLong(DefaultQuantity, "ChoppyLong");
					_rangeHardStop = Close[0] - RangeAtrStopMult * atrVal;
				}
				else if (Close[0] >= upper)
				{
					EnterShort(DefaultQuantity, "ChoppyShort");
					_rangeHardStop = Close[0] + RangeAtrStopMult * atrVal;
				}
				return;
			}

			if (UseHardTickStops)
				return; // the native fixed-tick bracket is already handling this

			if (Position.MarketPosition == MarketPosition.Long)
			{
				ExitLongLimit(0, true, Position.Quantity, basis, "ChoppyTarget", "ChoppyLong");
				if (!double.IsNaN(_rangeHardStop))
					ExitLongStopMarket(0, true, Position.Quantity, _rangeHardStop, "ChoppyStop", "ChoppyLong");
			}
			else if (Position.MarketPosition == MarketPosition.Short)
			{
				ExitShortLimit(0, true, Position.Quantity, basis, "ChoppyTarget", "ChoppyShort");
				if (!double.IsNaN(_rangeHardStop))
					ExitShortStopMarket(0, true, Position.Quantity, _rangeHardStop, "ChoppyStop", "ChoppyShort");
			}
		}

		// ── Visuals: bar coloring + a stable-tag corner label. Deliberately NOT using
		// custom OnRender/Direct2D code for this (that's the riskiest, least-portable
		// part of any NinjaScript file in this project when it hasn't been verified
		// against a real compile) — BarBrushes and Draw.TextFixed are both patterns
		// already proven working elsewhere in Sneaky Suite.
		private void UpdateRegimeVisuals(SneakyMarketRegime regime, double adxVal, double bbWidth, double bbWidthAvg)
		{
			if (PaintRegimeBarColors)
			{
				Brush brush;
				switch (regime)
				{
					case SneakyMarketRegime.Trending: brush = new SolidColorBrush(Color.FromArgb(140, 0, 200, 0)); break;
					case SneakyMarketRegime.Ranging:  brush = new SolidColorBrush(Color.FromArgb(140, 30, 130, 220)); break;
					case SneakyMarketRegime.Choppy:   brush = new SolidColorBrush(Color.FromArgb(140, 150, 150, 150)); break;
					default:                          brush = Brushes.Gray; break;
				}
				brush.Freeze();
				BarBrushes[0] = brush;
				CandleOutlineBrushes[0] = brush;
			}

			Draw.TextFixed(this, "SneakyRegimeLabel",
				"Regime: " + regime + "   ADX " + adxVal.ToString("F1")
				+ "   BBw " + bbWidth.ToString("F3") + " / avg " + bbWidthAvg.ToString("F3"),
				TextPosition.TopLeft);

			double positionPnl = Position.MarketPosition == MarketPosition.Flat
				? 0.0
				: Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]);
			double sessionTotalPnl = _sessionRealizedPnl + positionPnl;

			Draw.TextFixed(this, "SneakyPnlLabel",
				"Position PnL: " + FormatPnl(positionPnl)
				+ "   Session PnL (6pm–5pm): " + FormatPnl(sessionTotalPnl),
				TextPosition.TopRight);
		}

		// Realized P&L for trades this Strategy instance closed since the start of
		// the current session (6:00 PM through 5:00 PM the next day). Computed from
		// SystemPerformance.AllTrades rather than the account's overall realized
		// P&L, so it reflects only THIS strategy's own trades on THIS instrument --
		// not anything else happening on the same account.
		//
		// On a rollover (the bar's session-start timestamp differs from the one we
		// last saw) the running total resets to zero and we fast-forward
		// _lastTradeCountSeen to the current trade count, so trades that closed in
		// the PRIOR session are never picked up here. Every other bar we just add
		// in whatever trade(s) closed since the last time we looked -- almost
		// always zero or one, but the loop handles multiple cleanly too.
		private void UpdateSessionPnl()
		{
			DateTime barTime = Time[0];
			DateTime sessionStart = barTime.TimeOfDay >= SessionStartTimeOfDay
				? barTime.Date.Add(SessionStartTimeOfDay)
				: barTime.Date.AddDays(-1).Add(SessionStartTimeOfDay);

			if (sessionStart != _currentSessionStart)
			{
				_currentSessionStart = sessionStart;
				_sessionRealizedPnl   = 0.0;
				_lastTradeCountSeen   = SystemPerformance.AllTrades.Count;
			}

			int tradeCount = SystemPerformance.AllTrades.Count;
			for (int i = _lastTradeCountSeen; i < tradeCount; i++)
				_sessionRealizedPnl += SystemPerformance.AllTrades[i].ProfitCurrency;
			_lastTradeCountSeen = tradeCount;
		}

		private static string FormatPnl(double pnl)
		{
			return (pnl >= 0 ? "+$" : "-$") + Math.Abs(pnl).ToString("N2");
		}

		#region Properties

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "ADX Period", GroupName = "1. Regime Detection", Order = 0)]
		public int AdxPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "ADX Trend Threshold", Description = "ADX above this = Trending.", GroupName = "1. Regime Detection", Order = 1)]
		public int AdxTrendThreshold { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "ADX Range Threshold", Description = "ADX below this = candidate Ranging/Choppy. Must be LESS than ADX Trend Threshold — the gap between them is a deliberate hysteresis buffer.", GroupName = "1. Regime Detection", Order = 2)]
		public int AdxRangeThreshold { get; set; }

		[NinjaScriptProperty]
		[Range(2, int.MaxValue)]
		[Display(Name = "Bollinger Band Period", GroupName = "1. Regime Detection", Order = 3)]
		public int BbPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Bollinger Band Stdev Multiplier", GroupName = "1. Regime Detection", Order = 4)]
		public double BbStdDevMult { get; set; }

		[NinjaScriptProperty]
		[Range(2, int.MaxValue)]
		[Display(Name = "BB Width Lookback (bars)", Description = "Rolling window used to compute this instrument's own recent average band width, so 'tight' and 'wide' are relative, not a fixed number.", GroupName = "1. Regime Detection", Order = 5)]
		public int BbWidthLookback { get; set; }

		[NinjaScriptProperty]
		[Range(0.01, 1.0)]
		[Display(Name = "BB Width Tight Ratio", Description = "When ADX is low, width < (recent average * this ratio) reads as Ranging; otherwise Choppy.", GroupName = "1. Regime Detection", Order = 6)]
		public double BbWidthTightRatio { get; set; }

		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Regime Confirm Bars", Description = "How many consecutive bars the same raw regime must be read before an actual switch (and flatten) happens. Higher = fewer, more confident switches; lower = faster but noisier reaction.", GroupName = "1. Regime Detection", Order = 7)]
		public int RegimeConfirmBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Close Position On Regime Change", Description = "If true, flattens immediately when a new regime is confirmed. If false, an open position rides and only new entries stop under the old regime's logic.", GroupName = "1. Regime Detection", Order = 8)]
		public bool CloseOnRegimeChange { get; set; }

		[NinjaScriptProperty]
		[Range(2, int.MaxValue)]
		[Display(Name = "Donchian Breakout Period", GroupName = "2. Trending Bot", Order = 0)]
		public int DonchianPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "ATR Period", GroupName = "2. Trending Bot", Order = 1)]
		public int AtrPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "ATR Hard Stop Multiplier", Description = "Fixed initial stop, measured from the ENTRY price, ATR times this multiplier away. Caps the worst case on a fresh trade regardless of where the chandelier trail happens to sit yet. Set it tighter than the Trail Multiplier for real protection early in the trade; the trail takes back over once it moves tighter than this on its own.", GroupName = "2. Trending Bot", Order = 2)]
		public double AtrStopMult { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "ATR Chandelier Trail Multiplier", Description = "Trailing stop distance from the highest high (long) / lowest low (short) since entry. Only tightens over time.", GroupName = "2. Trending Bot", Order = 3)]
		public double AtrTrailMult { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RSI Period", GroupName = "3. Ranging Bot", Order = 0)]
		public int RsiPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, 49)]
		[Display(Name = "RSI Oversold", GroupName = "3. Ranging Bot", Order = 1)]
		public int RsiOversold { get; set; }

		[NinjaScriptProperty]
		[Range(51, 99)]
		[Display(Name = "RSI Overbought", GroupName = "3. Ranging Bot", Order = 2)]
		public int RsiOverbought { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Range Hard-Stop ATR Multiplier", Description = "Protects against the regime read being wrong and a 'range' turning into a real breakout.", GroupName = "3. Ranging Bot", Order = 3)]
		public double RangeAtrStopMult { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Allow Choppy Trading", Description = "Off by default — chop is where heuristics tend to bleed on real fills. Turn on only after validating in the Lab.", GroupName = "4. Choppy Bot", Order = 0)]
		public bool AllowChoppyTrading { get; set; }

		[NinjaScriptProperty]
		[Range(2, int.MaxValue)]
		[Display(Name = "Choppy Scalp BB Period", GroupName = "4. Choppy Bot", Order = 1)]
		public int ChoppyBbPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Choppy Scalp BB Stdev Multiplier", GroupName = "4. Choppy Bot", Order = 2)]
		public double ChoppyBbStdDevMult { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Default Quantity", GroupName = "5. General", Order = 0)]
		public int DefaultQuantity { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Paint Regime Bar Colors", Description = "Colors bars green/blue/gray for Trending/Ranging/Choppy. Turn off if you'd rather rely only on the corner label.", GroupName = "5. General", Order = 1)]
		public bool PaintRegimeBarColors { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use Hard Tick Stop/Target", Description = "When true, every entry (regardless of which bot or regime opened it) gets a single native fixed-tick profit target and stop loss, attached directly by NinjaTrader (SetProfitTarget/SetStopLoss) instead of each bot's own ATR-based trail/target/stop. Because NinjaTrader manages the resulting orders itself, this can't be broken by this strategy's own regime dispatch logic -- see the header comment's KNOWN LIMITATION note for why that matters. When false (default), each bot manages its own exits exactly as documented on each one.", GroupName = "5. General", Order = 2)]
		public bool UseHardTickStops { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Hard Stop Loss (Ticks)", Description = "Only used when Use Hard Tick Stop/Target is true.", GroupName = "5. General", Order = 3)]
		public int HardStopLossTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Hard Profit Target (Ticks)", Description = "Only used when Use Hard Tick Stop/Target is true.", GroupName = "5. General", Order = 4)]
		public int HardProfitTargetTicks { get; set; }

		#endregion
	}
}
