# Sneaky Suite — Full Reference

Full description and entry/signal/trigger logic for every file in this repo (165 total: 124 Indicators, 20 Strategies, 21 AddOns), organized by folder and sorted alphabetically within each. This supplements the short-form tables in the main [README](README.md).

---

## Indicators (124)

## GexContextScore.cs
**Description:** Reads a CSV from the companion `fetch_gex_nq.py` script and converts GEX wall position, gamma flip, gamma zone, DEX magnets, skew/term structure and 0DTE pin into a single directional context score from -100 to +100, with VIX-based blocking/attenuating filters.
**Signal logic:** No discrete buy/sell signal; outputs a continuous -100..+100 directional score (positive = bullish gamma/dealer-positioning context, negative = bearish) meant to be read as context/bias by other indicators (e.g. the Council), not as a standalone trade trigger.

## GexLevels_v1_0_0.cs
**Description:** Plots gamma/DEX option-market levels (structural + intraday + 0DTE call/put walls, vanna flip, DEX magnets, pin/max-pain/charm, expected move) sourced from a CSV produced by `fetch_gex_nq.py`; it does not compute GEX/DEX itself.
**Signal logic:** No discrete signal — draws horizontal reference levels (walls/magnets/pin) for the trader to use as support/resistance/targets.

## GexReplayWindow.cs
**Description:** A small NinjaTrader 8 Indicator utility for stepping through/replaying historical GEX level snapshots on the chart (a review/playback aid for the GEX level files), rather than computing any new market signal itself.
**Signal logic:** No discrete signal — utility/review tool only.

## PraedoxFibEngine_v0_1_0.cs
**Description:** "Phase 2" of the Praedox rebuild: a rolling auto-Fibonacci grid (8 levels) with a Golden Zone/Pocket, kill-zone suppression and confluence detection, plus exhaustion suppression, built from a behavior spec (original implementation, no surviving source).
**Signal logic:** Does not itself fire buy/sell signals; it publishes gating state (InKillZone, ExhaustionActive, NearKeyLevel) that PraedoxSignalEngine consumes to suppress or qualify its own breakout/continuation/reversal signals.

## PraedoxSignalEngine_v0_1_0.cs
**Description:** "Phase 1" rebuild of the Praedox signal engine: ten priority-ranked trade signals (Slingshot, Zone Break, Trend Cycle Wave, CrossFire, Reversal, Continuation, EMA Breakout, Aggressive EMA/Volume, and a longer-lookback Breakout) with five signal-group toggles. Signal-only, no order management.
**Signal logic:** Each bar, every enabled signal's rule is evaluated (e.g. Donchian-style breakouts of varying lookback for Slingshot/Zone Break/Breakout, RSI extreme touching a level for Reversal, trend+EMA-slope pullback touch for Continuation, close crossing the trend EMA confirmed by the following bar for EMA Breakout); when multiple signals qualify on the same bar only the highest-priority (lowest-numbered) one is reported, giving one rank/direction output per bar. A Volatility Shield gate can disable the whole chain in certain regimes, and PraedoxFibEngine's kill-zone/exhaustion/near-key-level flags can suppress or add alternate paths for specific signals.

## PraedoxVolatilityShield_v0_1_0.cs
**Description:** "Phase 3" of the Praedox rebuild: a 4-state volatility-ratio (VR) based regime classifier ("Volatility Shield") plus a Smart Trader auto-trading gate and per-state minimum stop/target recommendations.
**Signal logic:** No buy/sell signal of its own — classifies volatility regime into 4 states and outputs a gate flag that other Praedox indicators (the Signal Engine) use to enable/disable their own signal logic, plus suggested stop/target sizing per state.

## SneakyADXPro_v1_2_0.cs
**Description:** ADX/DI trend-strength and directional-bias indicator with a Sneaky "flight instrument" glass card; optionally publishes its trend regime to the SneakyCore bus for the Council.
**Signal logic:** No discrete buy/sell arrow — plots ADX/+DI/-DI and derives a bias/regime (trending vs choppy, bullish vs bearish DI dominance) that is published as state for other indicators (e.g. the Council) to vote on, not a standalone entry trigger.

## SneakyADXVMA_v1_0_0.cs
**Description:** Clean-room ADX Volatility Moving Average: a Wilder directional-volatility index drives the smoothing constant of an adaptive moving average (adxvma = adxvma[1] + vi^K·(Close−adxvma[1])), combined with an ATR deadband and hysteresis for a trinary trend read; publishes AdxvmaState for the Council.
**Signal logic:** Plots an adaptive moving-average line; trend/Signal is trinary (+1 up / -1 down / 0 flat), determined by whether price is above/below the ADXVMA line by more than an ATR-based deadband (with hysteresis to avoid flip-flopping) — used as an adaptive-MA voter rather than a standalone entry signal.

## SneakyAOBLE.cs
**Description:** Port of an "Apex Order Block & Liquidity Engine": detects market structure, order blocks, breaker blocks, liquidity pools, fair value gaps (FVG), premium/discount zones, with multi-timeframe (MTF) analysis and a trade planner overlay.
**Signal logic:** Draws order blocks/breakers/FVGs/liquidity zones and structural shifts as zones/markers on the chart; a bullish read comes from price reacting to/breaking a bullish order block or sweeping liquidity into a bullish structural shift (and the mirror for bearish), feeding an on-chart trade planner rather than a single clean Signal series.

## SneakyAdaptiveLaguerreFilter_v1_0_0.cs
**Description:** Ehlers Adaptive Laguerre filter (clean-room port): a Laguerre IIR filter whose damping factor self-adjusts from the normalized recent tracking error (fast-tracking in trends, smooth in chop).
**Signal logic:** Plots a self-adjusting low-lag filter line; no discrete signal — used as a trend/smoothing reference.

## SneakyAdaptivePerformanceGrid_v1_0_0.cs
**Description:** Indicator-only adaptive strategy-performance grid that simulates GodTradesStrategy logic across multiple bar types/timeframes simultaneously and ranks which configuration is currently performing best.
**Signal logic:** No trading signal itself — it runs parallel simulations of another strategy's rules across configs and surfaces a ranking/grid of which bar-type/timeframe combination is currently the "best row," intended to inform which live configuration to use.

## SneakyAlphaForgeFlow.cs
**Description:** Port of TradingView's "Alpha Forge | Flow BUY SELL" (AF AURORA): an OHLCV flow-persistence signal that smooths a buy/sell pressure score and combines it with a trend EMA and range filter, with an optional mirrored short/cover side, aurora trend line, badges and dashboard.
**Signal logic:** BUY fires when the smoothed OHLCV pressure score is positive AND price is above the trend EMA AND the range filter is OK; the long position is exited (SELL) after N consecutive closes below the trend EMA. When the mirrored short side is enabled, SHORT fires on the inverse conditions (negative flow, price below trend EMA, falling broader trend) and COVER fires after N closes back above the trend EMA. Outputs a Signal plot of +1 (long entry) / -1 (short entry) / 0.

## SneakyBarDump_v1_0_0.cs
**Description:** Headless utility that writes every completed bar (time, OHLC, volume) to a JSONL file, used as an "answer key" reference dataset for other recorder/backtest tooling.
**Signal logic:** No signal — pure data-logging utility.

## SneakyBrickCounter_v1_0_0.cs
**Description:** Small utility indicator that reads SneakyCore.BrickState (published by any Sneaky "brick" bar type) and displays ticks remaining to the next brick close.
**Signal logic:** No signal — informational countdown utility.

## SneakyBsdBridge.cs
**Description:** A bridge/adapter utility that reads named plot values (by indicator name and plot index) from another indicator already on the chart (e.g. "Zone Entry Signals") and republishes them in a standard form for downstream automation.
**Signal logic:** No signal logic of its own — it relays whatever long/short plot values exist in the source indicator it is pointed at.

## SneakyButterworthFilter_v1_0_0.cs
**Description:** Ehlers Butterworth low-pass filter (clean-room port, 2-pole or 3-pole IIR smoother).
**Signal logic:** Plots a smoothed line; no discrete signal — used as a trend/smoothing reference.

## SneakyBuySellVolumePressureMountain_v1_0_0.cs
**Description:** Buy/sell volume "pressure mountain" visualization using hybrid tick/OHLC order-flow classification, with pressure-divergence flags and a Sneaky glass card; an order-flow/volume-delta style indicator (edge lane, places no orders).
**Signal logic:** No trade signal — plots buy vs. sell volume pressure as mountain areas and flags divergences between price and pressure dominance (e.g. dominant-side % crossing a configurable threshold, default 50%) as informational flags, not entries. It is also a data source other indicators (e.g. SneakyFlow/CVD) consume.

## SneakyBuySellXL.cs
**Description:** Port of the TradingView "Buy/Sell Signals XL" script: six independent signal engines (Open/Close, Breakout, Donchian, MA Cross, Supertrend, Aroon) each with their own stop-loss/take-profit logic, plus lookback statistics, an info box, and a multi-timeframe/engine scanner.
**Signal logic:** Whichever engine is selected generates a BUY (+1) or SELL (-1) on the signal bar per that engine's own rule (e.g. Breakout: price clears a lookback high/low; Donchian channel break; MA Cross: fast MA crosses slow MA; Supertrend flip; Aroon up/down cross). Once a trade is open it tracks an explicit entry/stop/target and reports exit via ExitCode (TP hit, SL hit, or opposite-signal reversal).

## SneakyBuyersSellersProfile.cs
**Description:** Port of Zeiierman's "Buyers/Sellers" rolling volume profile that builds profile-guided dynamic support/resistance levels from rolling buy/sell volume distribution.
**Signal logic:** BUY fires when a bar's low wicks into an active support level (within a "Touch ATR" distance) and the bar closes back above it as a bullish candle; SELL is the mirror condition at an active resistance level. Only levels that scored above a minimum score qualify, and a level cannot re-fire within a cooldown period. Signals are drawn as arrows and exposed via BuySignal/SellSignal plot series for strategy automation.

## SneakyCRT.cs
**Description:** "Candle Range Theory" v8 port with higher-timeframe confluence: detects a sweep of the previous candle's range that closes back inside it, scored 0-4 by HTF1/HTF2 bias alignment, whether the sweep also took a prior HTF extreme, and premium/discount location. Author notes it is UNPROVEN — no statistically reliable edge found across 27 NQ sessions tested.
**Signal logic:** Signal = +1 (buy) when price sweeps below the prior candle's low (or qualifying range) and closes back inside with bullish color confirmation; -1 (sell) is the mirror at the high. A 0-4 confluence Score is attached (HTF1 bias aligned, HTF2 bias aligned, the sweep also took the last HTF1 or prior-day extreme, and price sitting in discount for buys / premium for sells), with hidden plots for Target, Invalidation, and whether each was hit.

## SneakyCRTAlpha.cs
**Description:** A broader "Candle Range Theory" toolkit built around a higher-timeframe (HTF) reference candle, generating Sweep / FVG (Fair Value Gap) / Return / Break signals plus entry zones and key levels. Author notes: UNPROVEN edge.
**Signal logic:** Multiple signal types fire off the HTF reference candle's range: a Sweep signal when price takes out the HTF candle's high/low and reverses; an FVG signal when a fair value gap forms in the sweep's direction; a Return signal when price returns to retest the HTF level/zone; and a Break signal when price closes beyond the HTF range in trend-continuation fashion. Each draws its own entry zone/key level on the chart.

## SneakyCVD_v1_1_0.cs
**Description:** Session Cumulative Volume Delta (v1.1.0), ported from the Sentinel Suite original: accumulates buy-minus-sell volume delta for the session and computes a slope/z-score of that CVD trend.
**Signal logic:** No discrete buy/sell arrow; plots cumulative delta and a slope z-score intended as a leaning/momentum reference for order-flow direction, consumed by downstream indicators (e.g. SneakyFlow) rather than firing its own trade signal.

## SneakyCandidateRecorder_v1_0_0.cs
**Description:** A "clock-native candidate corpus" recorder: logs every brick bar close as a continuation-trade candidate (answer-key style dataset) for later ML/backtest grading, with an optional Sneaky glass-card readout.
**Signal logic:** No trading signal — pure recording/data-capture utility that treats every bar close as a labeled candidate for offline analysis.

## SneakyClock_v1_0_0.cs
**Description:** Session-phase and time-in-session context indicator; publishes ClockState (session phase, minutes since open/close, etc.) describing where in the trading day the current bar sits (e.g. open-drive phase, mid-session, close).
**Signal logic:** No trade signal — publishes time/session-phase context consumed by other indicators (e.g. gating logic in the Council or Confluence) rather than generating entries itself.

## SneakyCompressionBase_v1_3_0.cs
**Description:** Coil/consolidation "base" detector plus excursion recorder: identifies tight-range price compression boxes and measures the resulting breakout excursion (edge lane, places no orders).
**Signal logic:** No discrete buy/sell signal — detects and draws a compression "base" box when price range tightens below ATR/coil-ratio thresholds, and records the subsequent excursion (how far/fast price moved after the base) for analysis rather than issuing a trade trigger.

## SneakyConfluence_v1_0_0.cs
**Description:** A filter-cascade signal fuser sitting over the SneakyCore sensor bus: requires every enabled gate (structure, profile overextension, flow direction, counter-structure block, nearby structural level, etc.) to pass, and only then allows a trigger to fire a long or short signal.
**Signal logic:** Candidate bias direction is formed, then checked against a series of veto gates — e.g. must not be opposed by SneakyLocation's structural level, SneakyBuyersSellersProfile's profile signal, SneakyFlow's order-flow signal, or counter-trend market structure; a "profile overextended" veto can also block entries in the direction that already overextended. Only if ALL active gates pass does a long/short Signal/SellSignal fire (Values[0]/Values[1]) on that bar — an "every gate must pass, then a trigger must fire" AND-cascade design, not a simple threshold.

## SneakyCoralFilter_v1_0_0.cs
**Description:** Sneaky smoother library — Coral / Tillson T3 triple-smoothed EMA (clean-room): a low-lag T3 filter of the input with a configurable volume-factor coefficient (default Coral 0.4).
**Signal logic:** Plots a low-lag triple-smoothed line; no discrete signal — used as a trend/smoothing reference.

## SneakyCotScreener.cs
**Description:** COT (Commitments of Traders) multi-symbol screener using CFTC Legacy commercial net positions and a COT index computed over two lookback windows.
**Signal logic:** No price-bar trade signal — it's a positioning screener that ranks/flags instruments by extreme commercial net positioning (COT index), used as macro/contextual bias rather than an entry trigger.

## SneakyCouncil_v1_0_0.cs
**Description:** The "Sneaky Council" — a chart-level confluence arbiter that reads every sensor published on the SneakyCore bus for its scope, runs a weighted vote (per the manual's fusion math), applies squeeze/breadth/in-value modulators and hard vetoes (liquidity wall, structural level, MTF opposed, chop, high-vol regime, kill window), and publishes a single CouncilState (bias/conviction/sizeMult). Places no orders itself.
**Signal logic:** Each registered sensor (ADXVMA, Harmonic, Exhaustion, Profile, Flow, structure, etc.) casts a weighted vote toward long/short/flat; votes are summed into a net score, which is compared against a deadband (default 0.15 × active weight) — the Council stays FLAT unless |netScore| exceeds that deadband. Multiple hard vetoes (opposing MTF trend, structural level in the trade's path, chop regime, high volatility, kill-window time, liquidity wall) can force FLAT even if the weighted vote clears the deadband. The resulting bias/conviction is published for consumers like SneakyConfluence/Deck/Recorder.

## SneakyDDBarsPerSessionAdvisor.cs
**Description:** A bars-per-session advisor utility for picking tick-chart bar size: estimates whether the current bar size produces too few, too many, or an acceptable number of bars per session.
**Signal logic:** No trading signal — advisory utility recommending a bar-size setting (rounded to a configurable step) based on historical bars-per-session counts.

## SneakyDEMA_v1_0_0.cs
**Description:** Sneaky smoother library — Double Exponential Moving Average (clean-room): DEMA = 2·EMA − EMA(EMA), drawn with a Sneaky glass card. A building-block smoother, not a Council voter.
**Signal logic:** Plots a lag-reduced EMA-based moving average; no discrete signal — used as a trend/smoothing reference.

## SneakyDOL.cs
**Description:** "Draw on Liquidity" (DOL) target ranker (clean-room, not the paid TradeAxis script): pools swing highs/lows (merging equal levels) and fair value gaps, scores each on proximity, freshness, confluence and density, and boxes the top-scoring one as the current DOL target. Author notes it is UNPROVEN with no demonstrated forward edge.
**Signal logic:** No standalone entry signal — identifies and ranks liquidity/FVG targets and outputs plots for target midpoint, target direction (+1 above price / -1 below), target score, whether a new target was set, whether a target was swept, and whether an iFVG (inverted fair value gap) formed bullish (+1) or bearish (-1); meant to guide where price is likely headed next, not when to enter.

## SneakyDSMA_v1_0_0.cs
**Description:** Sneaky smoother library port described in-file as implementing a "Double SMA" twicing method (y = 2·SMA − SMA(SMA)) — a de-lagged double simple moving average, the SMA analogue of DEMA (despite being commonly called "Deviation-Scaled Moving Average" elsewhere).
**Signal logic:** Plots a lag-reduced double-SMA line; no discrete signal — used as a trend/smoothing reference.

## SneakyDTMA_v1_0_0.cs
**Description:** Sneaky smoother library port of "Double Triangular Moving Average": y = 2·TMA − TMA(TMA), a de-lagged Triangular MA (itself a double SMA), the TMA analogue of DEMA.
**Signal logic:** Plots a lag-reduced triangular moving-average line; no discrete signal — used as a trend/smoothing reference.

## SneakyDWMA_v1_0_0.cs
**Description:** Sneaky smoother library — Double Weighted Moving Average (clean-room, 2·WMA − WMA(WMA)): a lag-reduced smoothed line with a Sneaky glass card.
**Signal logic:** Plots a lag-reduced weighted moving-average line; no discrete signal — used as a trend/smoothing reference.

## SneakyDeck_v0_2_6.cs
**Description:** A large on-chart "trade deck"/risk-card overlay that draws live ENTRY/STOP/TARGET lines (with R-multiple, dollar, and tick-distance labels) and other dashboard cards, acting as the visual order-management/readout layer for other Sneaky signal indicators ("the Deck SIGNAL ARM" referenced elsewhere in the suite).
**Signal logic:** Does not generate its own trade signal — it consumes a "SIGNAL ARM" input (another indicator's Signal plot) and renders that trade's entry/stop/target lines and risk metrics; it is a visualization/risk-display layer, not a signal source.

## SneakyDeltaLevels.cs
**Description:** Tracks price levels with the largest per-bar buy/sell volume imbalance, keeping them as persistent "ghost levels" on the chart.
**Signal logic:** Draws a touch/retest signal when price returns to one of these high-imbalance ghost levels (treated as support/resistance); the exact direction follows whether the level was originally a buy- or sell-dominant imbalance.

## SneakyDynamicSwingVwap.cs
**Description:** Port of Zeiierman's swing-anchored VWAP: computes a VWAP anchored to the most recent detected swing high/low with adaptive tracking as new swings form.
**Signal logic:** No discrete buy/sell signal — plots an adaptive, swing-anchored VWAP line used as a dynamic support/resistance/mean reference.

## SneakyEMA_v1_0_0.cs
**Description:** Sneaky smoother library — plain Exponential Moving Average (alpha = 2/(Period+1)) with a Sneaky glass card.
**Signal logic:** Plots a standard EMA line; no discrete signal — used as a trend/smoothing reference.

## SneakyEhlersFilter_v1_0_0.cs
**Description:** Ehlers Non-Linear Filter (clean-room): weights each input in the lookback window by its squared distance from the current bar, then averages — a smoothing filter.
**Signal logic:** Plots a smoothed line; no discrete signal — used as a trend/smoothing reference.

## SneakyEmaBounce.cs
**Description:** Port of "TO_EMA_Bounce": detects 21-EMA trend-pullback bounce scenarios (S1 through S8 plus a combined "COMBO" case), signaling on closed bars with +1/-1 plots suitable for automation.
**Signal logic:** Each scenario (S1-S8) encodes a specific candle/EMA interaction indicating a pullback rejection off the 21-EMA in the direction of the prevailing trend (e.g. a bar that touches/pierces the EMA and closes back on the trend side). COMBO fires specifically when an S1 rejection also happens to be the first pullback of a new trend leg. Signals plot as +1 (bullish bounce) / -1 (bearish bounce), with optional arrows on COMBO bars.

## SneakyEmaPullback.cs
**Description:** Trend-pullback-to-the-EMA strategy-style indicator: signals a pullback entry to the EMA with a swing-based stop and an R-multiple target, with an optional overnight-only filter and an on-chart stats box.
**Signal logic:** Signal fires when price, in an established EMA-defined trend, pulls back to touch/approach the EMA and shows a resuming move in the trend direction; a swing high/low sets the stop, and the target is set as a multiple (R-multiple) of that initial risk.

## SneakyExcursionRecorder_v2_0_0.cs
**Description:** Council-only signal characterization recorder: for each Council verdict (edge-detected bias flip, bucketed by conviction) it records the maximum adverse/favorable excursion (MAE/MFE) from the fire bar through end of day, as an offline grading dataset.
**Signal logic:** No trading signal of its own — passively records the outcome statistics (MAE/MFE) of signals fired elsewhere (the Council) for later analysis; "Record Council fires" option listens for Council bias flips as the event to log.

## SneakyExhaustion_v1_0_0.cs
**Description:** Clean-room Leledc-style exhaustion-bar reversal detector: counts consecutive same-direction closes and confirms exhaustion when a directional run pokes a high/low extreme and then stalls; publishes ExhaustionState for the Council and draws reversal triangles.
**Signal logic:** Bearish pulse: a run of up-closes exceeding a "Major/Minor Qual" count that pokes a new N-bar high, interpreted as the up-move spending itself → expect a down reversal (Signal = -1). Bullish pulse is the mirror: a run of down-closes poking a new N-bar low → expect an up reversal (Signal = +1). Signal is the one-bar pulse (+1/-1/0); Dir holds the last non-zero signal for a configurable number of bars before decaying back to 0.

## SneakyExportAll.cs
**Description:** Utility that exports this chart's bars plus every other indicator's plot values and drawing objects (arrows, text, lines) to CSV, for offline backtesting/analysis.
**Signal logic:** No signal — pure data-export utility.

## SneakyEye_v1_1_0.cs
**Description:** "Orthogonal-Evidence Qualifier" publishing EyeVerdict: deliberately uses only evidence NOT derived from this instrument's own price and not already cast as a separate Council vote (CVD slope/direction, CVD-price divergence, liquidity walls), meant to run alongside the Council as a corroboration/disagreement check. Weights are reasoned, not backtested. Places no orders.
**Signal logic:** Combines its narrow evidence set (CVD slope direction, divergence, liquidity-wall proximity) into a score; when the absolute score exceeds a Qualify Threshold (default 0.20) and evidence sufficiency clears a Min Evidence floor (0.40), it "qualifies" a direction as Endorsed (agrees) or flags disagreement versus the Council's own bias — it is a corroboration check, not an independent trade trigger.

## SneakyFlow_v1_0_0.cs
**Description:** CVD regression indicator publishing FlowState: fits an OLS regression line to cumulative volume delta and reports the slope together with its R-squared, answering whether order flow has been leaning and whether that lean is a coherent trend or noise. Requires SneakyBuySellVolumePressureMountain on the chart for its tick-classified volume. Places no orders.
**Signal logic:** Signal is the regression slope's direction (bias), gated so it only registers when the fit quality (R-squared) clears a minimum threshold and the slope's strength clears a minimum — a steep slope through scattered data is not counted as a lean. This gated Signal is what the Council votes on.

## SneakyFootprintPSMCVWAP.cs
**Description:** Port of "FootPrint PSMC VWAP 2027": combines VWAP bands, a footprint-style supply/demand volume profile, and Smart Money Concepts (SMC) market structure on one chart.
**Signal logic:** No single unified Signal plot — structural bullish/bearish reads come from SMC structure breaks (break of structure / change of character) combined with footprint supply/demand profile reactions at VWAP bands, used as a discretionary confluence display rather than an automated trigger.

## SneakyFusion17.cs
**Description:** Port of DrFX's "Fusion17" adaptive-consensus signal logic: aggregates 17 engine votes into bull/bear scores to decide direction. Author notes: UNPROVEN, honest backtests show no robust edge.
**Signal logic:** Signal = +1 (long) when the internal engine consensus (`core.Long`) fires, -1 (short) when `core.Short` fires, else 0; also plots Entry, Stop, TP1, TP2, and the underlying BullScore/BearScore from the 17-engine vote that drives the Long/Short decision.

## SneakyGaussianFilter_v1_0_0.cs
**Description:** Ehlers Gaussian low-pass filter (clean-room, selectable 1-4 pole IIR) smoother.
**Signal logic:** Plots a smoothed line; no discrete signal — used as a trend/smoothing reference.

## SneakyGexProfile.cs
**Description:** Per-strike GEX (gamma exposure) histogram showing calls/puts/net totals, a magnet level and barycenter, read from a `GexProfile_<ticker>.csv` file produced by an external data script.
**Signal logic:** No trading signal — visualizes the per-strike gamma distribution and derived magnet/barycenter levels as context (e.g. for option-pinning expectations), not an entry trigger.

## SneakyGexRadar.cs
**Description:** Dashboard-style indicator that reads a GexLevels CSV (auto-selected by chart instrument, e.g. NQ/ES/YM) and renders GEX walls, flip levels and related "Kinetic Cockpit"-style radar panels on the chart.
**Signal logic:** No discrete buy/sell signal — draws GEX-derived level lines and a radar/cockpit-style readout panel for discretionary reference.

## SneakyGodReversal_v1_0_0.cs
**Description:** Candle-grammar reversal recognizer (shaved candle / engulfing / equal-high / doji-exhaustion / VI patterns) gated at a Bollinger Band edge; marks triggers with a single-candle stop and publishes GodReversalState for the Council. Places no orders.
**Signal logic:** Signal[0] defaults to 0 each bar; when one of the recognized candle-grammar reversal patterns completes while price is at/near the outer Bollinger Band (within a configurable tick proximity — the "location gate"), Signal[0] is set to the fired direction (+1 bullish reversal / -1 bearish reversal) on that trigger bar.

## SneakyHMA_v1_0_0.cs
**Description:** Sneaky smoother library — Hull Moving Average (clean-room): fast, low-lag smoother computed as WMA(2·WMA(N/2) − WMA(N), √N).
**Signal logic:** Plots a low-lag smoothed line; no discrete signal — used as a trend/smoothing reference.

## SneakyHarmonic_v1_0_0.cs
**Description:** Clean-room XABCD harmonic pattern reversal detector (Gartley / Bat / Butterfly / Crab) built from public Fibonacci ratio tables and a self-contained fractal pivot detector; publishes HarmonicState for the Council and draws the XABCD skeleton plus a labelled reversal marker at the D point.
**Signal logic:** When price completes a valid XABCD harmonic ratio pattern, direction is read from the D pivot: D forming as a swing low → bullish (Signal +1, expect a move up); D forming as a swing high → bearish (Signal -1). Only the most recent valid match fires, one-shot per new D pivot; Dir holds the last non-zero signal for a configurable number of bars before decaying.

## SneakyHoltEMA_v1_0_0.cs
**Description:** Sneaky smoother library — Holt double-exponential smoothing (clean-room, level + trend components), plotting the smoothed level.
**Signal logic:** Plots a double-exponentially smoothed level line; no discrete signal — used as a trend/smoothing reference.

## SneakyIctMarketStructure.cs
**Description:** Port of Zeiierman's ICT market-structure indicator: detects Change of Character (CHoCH), Shift in Market Structure (SMS) and Break in Market Structure (BMS) events, with premium/discount zone context and a multi-symbol screener.
**Signal logic:** The first structural reversal in a new direction is tagged CHoCH; if the prior extreme had held unchanged for a configurable "Response" period, the next new high/low in that direction is tagged SMS, and each further one after that is tagged BMS. Each event carries a bull/bear flag (Bull = true for a bullish shift, false for bearish) and is plotted/labelled (StructurePos: +1 CHoCH / +2 SMS / +3+ BMS, negative for bearish) plus optional alert notifications; premium/discount context flags whether price sits above or below the midpoint of the current structural range.

## SneakyIctUnicorn.cs
**Description:** Port of LunqFX's "ICT Unicorn Model, Breaker Block & FVG Zone": detects a liquidity sweep plus a market structure shift (MSS) plus a breaker block overlapping a fair value gap, with zone-tap tracking, simulated trade stats, and automation plots.
**Signal logic:** A "unicorn zone" forms (and Values[0][0] is set to +1/-1) when, after a liquidity sweep and an MSS, a breaker block is found to overlap a fair value gap in the same direction; direction e.Dir = +1 marks a bullish unicorn zone, -1 a bearish one, each with an on-chart notification and zone box drawn between the zone bounds.

## SneakyInsaneToOsc_v1_0_0.cs
**Description:** NinjaTrader port of "Insane Oscillator v4" (TradingView Pine v5): a composite oscillator combining a WaveTrend-style core (EMA-channel + CI normalization), Stochastic RSI, multiple trend meters, a confluence engine, and divergence detection, with configurable color themes.
**Signal logic:** A WaveTrend buy signal fires when wt1/wt2 cross AND wt2 is at/below wt1 (bearish-to-bullish cross) AND the oscillator was oversold; the mirrored sell signal fires on a bullish-to-bearish cross while overbought. Separately, regular bearish divergence is flagged when a new price high fails to produce a new oscillator high (and the bullish mirror for lows), feeding a divergence-flag overlay alongside the WaveTrend cross signals and the broader multi-meter confluence read.

## SneakyIntermarket_v1_0_0.cs
**Description:** Correlated-instrument directional-lean indicator: reads a configurable set of reference instruments (up to 4, including an optional macro reference like ZN for rates) and derives a directional lean for the primary instrument from their correlation.
**Signal logic:** No discrete entry signal — outputs a continuous directional lean/bias derived from how correlated reference instruments are currently trading (e.g. a macro reference like ZN/NQ correlation, noted in-code as regime-dependent and sign-flipping, so it's off by default), used as context rather than a trade trigger.

## SneakyKineticPanel.cs
**Description:** A "Kinetic Cockpit"-style dashboard panel that measures each tracked signal's distance from its EMA in ATR units (tile strength) and rolls that into a composite bias score, displayed as a visual tile grid.
**Signal logic:** Each tile's Signal plot is set to +1 when the bias score is at/above a configurable threshold, -1 when at/below its negative, and 0 otherwise — a simple threshold-based bias readout per tracked metric rather than a trade-entry trigger.

## SneakyLLMA_v1_0_0.cs
**Description:** Sneaky smoother library — Low-Lag (Jurik-style) Moving Average (clean-room, simplified JMA) with adjustable phase (lead/lag bias).
**Signal logic:** Plots a low-lag smoothed line; no discrete signal — used as a trend/smoothing reference.

## SneakyLagCheck_v1_0_0.cs
**Description:** A diagnostic "Lag Generator" utility indicator used to measure/visualize the lag of other indicators or plots on the chart (a testing/calibration tool rather than a market indicator).
**Signal logic:** No trading signal — diagnostic/testing utility.


## SneakyLaguerreFilter_v1_0_0.cs
**Description:** Clean-room implementation of Ehlers' 4-element Laguerre IIR filter — a smooth, low-lag moving-average-style line driven by a damping factor Gamma, plus a Sneaky glass card readout. A smoother-library building block, not a Council voter.
**Signal logic:** Plots a Laguerre-filtered moving average/filter; no discrete signal — used as a trend/smoothing reference (the glass card shows only an up/down slope arrow, not a trade signal).

## SneakyLensCard_v1_0_0.cs
**Description:** A small original glass-card wrapper that periodically calls SneakyLens's LoadSummary() and displays overall trade count, win rate, profit factor, average net ticks, MFE-capture %, and the Eye verdict text. Read-only analytics display, no computation of its own.
**Signal logic:** No discrete signal — it is a reporting/dashboard card that surfaces existing trade-log statistics, not a market indicator.

## SneakyLevelsLikelyToBreak.cs
**Description:** Port of Zeiierman's "Levels Likely to Break": finds swing-high/low pivots, pairs nearby ones into support/resistance levels with a zone depth (ATR-based), and scores each level's "pressure" (0–100%) from proximity, approach speed, a delta proxy, absorption, compression and touch frequency.
**Signal logic:** A level "breaks" when price wicks (or closes, per setting) beyond it plus a Break-ATR buffer; a breakout triangle is drawn on the break bar and BreakSignal plots +1 (resistance broken, bullish) or -1 (support broken, bearish). The first retest of a broken level afterward is marked with a diamond.

## SneakyLinReg_v1_0_0.cs
**Description:** Clean-room ordinary-least-squares linear-regression line, fitted over the last N inputs and evaluated at the current bar (the regression value, not the slope), with a Sneaky glass card. A smoother-library building block, not a Council voter.
**Signal logic:** Plots a linear-regression moving average/filter; no discrete signal — used as a trend/smoothing reference (slope arrow shown on the card only).

## SneakyLiquiditySfpMss.cs
**Description:** Port of "Strong SFP MSS" — tracks pivot-based liquidity pools (sell-side/buy-side) and classifies price interactions with them as SWEEP, SFP (swing failure pattern), or MSS (market structure shift), producing a strength score and tracked long/short setups with entry/stop/target and win/loss outcome stats.
**Signal logic:** When a liquidity level is pierced and price closes back inside it with a strength score above a minimum threshold, a signal fires in the reversal direction (SignalDir +1 long / -1 short) at the pierce bar, with SignalStrength, SignalEntry/Stop/Target plotted on that bar; an EventCode plot encodes whether the triggering event was a SWEEP, SFP, or MSS.

## SneakyLiquidityWalls_v1_0_0.cs
**Description:** Port of TradingIQ's "Liquidity Walls" — classifies 1-tick order flow (tick rule) into per-bar delta, runs a rolling 100-bar OLS regression of expected price-move-vs-delta, and z-scores the shortfall between actual and expected move to detect order-flow absorption.
**Signal logic:** When the z-scored shortfall exceeds a threshold, it flags an absorption event and drops an ATR-thick liquidity "wall" one tick above the bar's high (resistance, if up-flow was absorbed) or below the low (support, if down-flow was absorbed); a hidden Signal plot carries +1 (resistance wall formed) / -1 (support wall formed) / 0 on the absorption bar, and walls persist until price trades cleanly through their far edge.

## SneakyLivewirePropBuster.cs
**Description:** Indicator port of the TradingView strategy "Livewire Prop Buster" for 5-minute NQ/MNQ — simulates an ORB-fade strategy plus a Brooks H2/L2 pullback strategy (filtered by Trend Magic, PSAR-on-LSMA, and ADX/DMI), with a built-in virtual broker, prop-firm evaluation layer, and on-chart trade visuals.
**Signal logic:** ORB Fade: after the first 5-minute close outside the 9:30–9:45 ET opening range, the first opposite-colored candle forms "leg 1"; when price breaks that leg's extreme ("leg 2"), a stop order is placed fading the breakout back toward the range, with a fixed stop and 1R target. Brooks H2/L2: counts pullback legs filtered by Trend Magic direction, PSAR-on-LSMA, and ADX/DMI; a buy/sell-stop order triggers 1 tick through the signal candle with a stop beyond the PSAR dot and a 2R target (max 2 trades/day per setup).

## SneakyLocation_v1_0_0.cs
**Description:** Clean-room publisher of classic structural reference levels — session VWAP (with volume-weighted std-dev bands), prior-day high/low, opening range, initial balance, and the current session's high/low — plus a "nearest level" summary with signed distance in ticks and ATR.
**Signal logic:** No discrete signal — it publishes a VwapSide (price above/below VWAP) and a "nearest reference level" context for other tools (e.g. SneakyGodReversal) to consult; not a standalone buy/sell trigger.

## SneakyMovingMedian_v1_0_0.cs
**Description:** Clean-room rolling median (outlier-robust central-tendency line) over the last N inputs, with a Sneaky glass card. A smoother-library building block, not a Council voter.
**Signal logic:** Plots a rolling-median moving average/filter; no discrete signal — used as a trend/smoothing reference.

## SneakyMtf_v1_0_0.cs
**Description:** Clean-room higher-timeframe alignment publisher — runs an independent fast/slow EMA cross on up to 4 configurable higher timeframes of the same instrument (via AddDataSeries) and fuses their directions into a weighted consensus Bias, an AlignmentScore (-1..1), and an AllAgree flag.
**Signal logic:** Each timeframe's own direction is +1 (fast EMA > slow EMA), -1 (fast < slow), or 0 (not yet warm); overall Bias is the sign of the weighted sum of all warm timeframes' directions — a multi-timeframe trend-alignment read, not a stand-alone entry trigger.

## SneakyNexgenProbe.cs
**Description:** A passive CSV recorder/utility — scans the chart for other indicators matching a name filter and writes every one of their plot values, every bar's OHLCV, and each matched indicator's settings to CSV files, purely for offline comparison/debugging. Draws nothing and computes nothing of its own.
**Signal logic:** No discrete signal — pure data-logging/recorder utility.

## SneakyOrderFlowDashboard_v1_0_0.cs
**Description:** A consolidation of five separate Sneaky Suite dashboard modules (Market Context, Confluence Zones, Liquidity Zones, Aggression Split, Flow Events) into one single scrollable floating panel, covering trend/momentum context, prior-session/VWAP/POC confluence zones, Level-2 liquidity walls, buy/sell aggression split, and sweep/absorption flow events, plus an added "Stat Walls" (absorption-inferred walls needing only trade prints) and a CVD-regression section.
**Signal logic:** Aggregates multiple underlying signal concepts (confluence-zone proximity, liquidity-wall/absorption flags, aggressor buy/sell imbalance, sweep/absorption flow events) into one dashboard display; it does not itself add new trade logic beyond what each of the five source modules already computes — see those modules for their individual trigger conditions.

## SneakyParabolicSAR_v1_0_0.cs
**Description:** Clean-room implementation of J. Welles Wilder's Parabolic SAR — a trailing, accelerating stop-and-reverse dot that sits below price in an uptrend and above it in a downtrend, publishing SneakyCore.SarState for Council use.
**Signal logic:** Bias is +1 while the SAR dot trails below price (uptrend) and -1 while it trails above price (downtrend); Flip fires a one-bar ±1 pulse the instant price crosses the SAR dot (classic Wilder flip: SAR resets to the extreme point, acceleration factor resets, and the trend reverses).

## SneakyParticipation_v1_0_0.cs
**Description:** Clean-room relative-volume (RVOL) sensor that compares each bar's volume against a time-of-day-aware learned baseline (not a flat rolling mean, since futures have a strong intraday volume profile), flagging volume "climax" and "dry-up" conditions. Publishes ParticipationState as a context modulator, not a directional voter (volume has no inherent direction).
**Signal logic:** No discrete buy/sell signal — participation is unsigned; the Council uses its RVOL/climax/dry-up reading to dampen or support conviction from other directional voters, and it falls back to a flat rolling mean while its time-of-day buckets are still warming up.

## SneakyPinPayoff.cs
**Description:** Illustrative pin-target panel that draws a long-butterfly option payoff diagram centered on a GEX pin/magnet strike, overlaid with a modeled "value now" curve (simple normal-distribution option-value model showing theta decay), risk/reward numbers, and an Eastern-time timeline. Explicitly a visualization/illustration, not a live option quote.
**Signal logic:** No discrete trading signal — it visualizes an assumed options payoff structure around a pin strike; it places no orders and is not based on real-time option pricing.

## SneakyPrecisionLevels.cs
**Description:** A large composite levels/analytics indicator (3000+ lines) drawing and scoring multiple categories of structural price levels (likely session reference levels, pivots, and related zones) with pressure/quality scoring — similar in spirit to SneakyLevelsLikelyToBreak and SneakyLocation but as its own standalone, more elaborate levels engine.
**Signal logic:** Produces level-break and/or level-touch signals analogous to SneakyLevelsLikelyToBreak (break of a scored level in a direction = directional signal); see the file's own plot list for exact signal/plot indices, as the specific trigger thresholds are configured through its own extensive property set.

## SneakyProfile_v1_0_0.cs
**Description:** Clean-room developing session volume profile — bins session volume into fixed-width price rows (splitting each bar's volume evenly across the rows its range touches), computes the Point of Control (POC) and a value area (VAH/VAL) via the standard textbook value-area-expansion algorithm, and flags proximity to high-volume (HVN) and low-volume (LVN) nodes.
**Signal logic:** Signal is a mean-reversion lean: +1 when price is stretched below VAL (expect reversion up toward POC), -1 when stretched above VAH (expect reversion down), 0 while inside the value area — published as a context modulator rather than wired as a standalone Council voter.

## SneakyRWMA_v1_0_0.cs
**Description:** Clean-room range-weighted moving average — each bar's weight in the average is (1 + its high-low range in ticks)², so wide/high-conviction bars pull the average more than narrow ones, with a Sneaky glass card. A smoother-library building block.
**Signal logic:** Plots a range-weighted moving average/filter; no discrete signal — used as a trend/smoothing reference.

## SneakyRecorder_v1_0_0.cs
**Description:** A deliberately "dumb" data logger that writes one JSONL row per observation, capturing the full fused Council/Eye/sensor state at decision time plus the raw, unsigned forward MFE/MAE/close outcomes at fixed future horizons. Exists purely to let every other voter's weight/threshold assumption be empirically checked later; computes no edge or judgment of its own.
**Signal logic:** No discrete signal — pure recorder/utility, writes files and renders a status card; forward outcomes are deliberately held open and flushed late once their horizon elapses.

## SneakyRegime_v1_0_0.cs
**Description:** Clean-room volatility-regime classifier — computes rolling return volatility, fits a 3-cluster (low/med/high) 1-D K-means model over a sample window, builds a Markov transition matrix between regimes, and runs a forward Markov filter to produce a smoothed regime probability. A context modulator, not a directional voter.
**Signal logic:** No discrete buy/sell signal — Regime (0 low-vol/1 med-vol/2 high-vol) and RegimeProb are published so the Council can dampen conviction in a high-volatility (regime 2, "Trending" = false) tape and let orderly regimes run; both plots are hidden (card-only readout).

## SneakyReversion.cs
**Description:** Independent implementation of a "High-Conviction Reversion" concept combining Heikin-Ashi direction with Bollinger-band and rolling value-area (POC/VAH/VAL) mean-reversion setups. The author's own notes state backtests on NQ 1-minute/2000-tick data showed no measurable edge.
**Signal logic:** Three setups, each gated by the last Heikin-Ashi candle's color/direction: Setup 1A (PullbackSignal) fires when price wicks beyond an outer Bollinger band and closes back inside, in the HA direction; Setup 1B (MidRetestSignal) fires when price pushed through the Bollinger midline within N bars then closes back across it, in the HA direction; Setup 2 (ValueAreaSignal) fires when price left the rolling value area (above VAH/below VAL) within N bars then closes back through the POC, in the HA direction. AnySignal is the first non-zero of the three, each ±1 for long/short.

## SneakyRsiStrengthZones.cs
**Description:** Port of Zeiierman's "RSI Strength & Consolidation Zones" — an enhanced RSI (blend of simple and Wilder RSI, Kalman-filtered) with a strength fill pushed away from 50 by ADX, and ADX-based consolidation boxes drawn on the price panel while ADX stays below a threshold.
**Signal logic:** While Wilder ADX ≤ 20, a box tracks the high/low of that quiet consolidation stretch; once the box closes (ADX rises above 20), the first subsequent bar that closes through the box's high fires a BreakoutUp (bullish) signal, and the first close through its low fires a BreakoutDown (bearish) signal — one signal per box. TrendUp is also published as +DI > -DI.

## SneakySMA_v1_0_0.cs
**Description:** Clean-room Simple Moving Average (arithmetic mean of the last N inputs) with a Sneaky glass card. A smoother-library building block/baseline for other tools (e.g. SuperTrend), not a Council voter.
**Signal logic:** Plots a simple moving average; no discrete signal — used as a trend/smoothing reference.

## SneakySRIProMTF.cs
**Description:** Port of "Trinity Capital SRI PRO 2027 & MTF" — a smoothed RSI histogram combined with a DSS (double-smoothed stochastic)/trigger oscillator on the chart timeframe, plus a 5-timeframe trend table with consensus direction, composite strength %, and loss-of-strength %.
**Signal logic:** Buy fires when DSS crosses above its trigger line while below the oversold level AND the multi-timeframe consensus is up; Sell fires when DSS crosses below its trigger line while above the overbought level AND the consensus is down.

## SneakySWMA_v1_0_0.cs
**Description:** Clean-room sine-weighted moving average — weights each input in the window by sin(π·(i+1)/(n+1)), peaking in the middle of the window and tapering at the edges, with a Sneaky glass card. A smoother-library building block.
**Signal logic:** Plots a sine-weighted moving average/filter; no discrete signal — used as a trend/smoothing reference.

## SneakySqueezeCounter.cs
**Description:** A Bollinger-Bands-inside-Keltner-Channels "squeeze" detector: flags a squeeze whenever the Bollinger band width is narrower than the Keltner channel width, counts how many consecutive bars the squeeze lasts, and plots a TTM-Squeeze-style linear-regression momentum histogram. The author notes 193 fires tested on 5-minute NQ showed no measurable edge; treated as a context/analysis tool, not a standalone entry signal.
**Signal logic:** FireSignal fires +1 (bullish) or -1 (bearish) on the first bar after a squeeze releases (Bollinger band width expands back outside the Keltner channel), sign taken from the momentum histogram's value at release, provided the squeeze lasted at least MinBars.

## SneakyStochasticTripleFilter_v1_0_0.cs
**Description:** Port of "Stochastic Triple Filter [ATP]" — a Stochastic %K/%D crossover gated by a DonovanWall multi-pole Gaussian-channel midline slope (trend agreement) and a Choppiness Index (regime filter), publishing SneakyCore.StfState so the Council gains both a trend voter and an independent chop veto.
**Signal logic:** A long signal fires when %K crosses above %D while %K is in the oversold zone, the Gaussian-channel midline is rising (if the GC filter is enabled), and the Choppiness Index indicates a trending (non-choppy) market (if the chop filter is enabled); a short signal mirrors this with %K crossing below %D in the overbought zone, midline falling, and trending regime. The raw Gaussian-slope bias and Choppiness trending flag are always published to the Council regardless of these per-sensor toggles, so the Council's own filter stays in control.

## SneakyStructure_v1_0_0.cs
**Description:** Clean-room self-contained market-structure axis: detects fractal swing-high/low pivots, classifies them as Higher-High/Higher-Low/Lower-High/Lower-Low versus the prior confirmed swing of the same kind, and tracks break-of-structure events. Publishes SneakyCore.StructureState and is wired into the Council as a market-structure voter.
**Signal logic:** Bias is +1 for an up-structure (HH and HL both confirmed), -1 for a down-structure (LH and LL both confirmed), 0 for mixed/unknown. Bos is a one-shot ±1 pulse: +1 when Close closes above the last confirmed swing-high (break of structure to the upside), -1 when Close closes below the last confirmed swing-low — each latches until the opposing level is subsequently taken out.

## SneakySuperSmootherFilter_v1_0_0.cs
**Description:** Clean-room 2-pole Ehlers Super Smoother filter — a near-zero-noise, low-lag recursive filter using canonical Ehlers coefficients, with a Sneaky glass card. A smoother-library building block.
**Signal logic:** Plots a low-lag smoothing filter; no discrete signal — used as a trend/smoothing reference.

## SneakySuperTrendCluster.cs
**Description:** Port of Zeiierman's "SuperTrend Cluster" — combines five independently-configured SuperTrend lines (each on its own ATR length, factor, moving-average type, and weight) into one weighted consensus cluster, with a cloud fill and candle coloring by cluster strength.
**Signal logic:** BuySignal (+1) fires on the bar the weighted bullish-member share crosses above the configured threshold AND the designated "base" SuperTrend is itself bullish; SellSignal (-1) mirrors this for bearish. The cluster keeps its last regime (Regime +1/-1) until the opposite side qualifies; BaseFlip separately marks ±1 when the single base SuperTrend flips.

## SneakySuperTrend_v1_0_0.cs
**Description:** Clean-room ATR-band SuperTrend — trailing bands at HL2 ± Multiplier×ATR with the standard clamp-and-flip logic, publishing SneakyCore.SuperTrendState and wired into the Council as a trend voter.
**Signal logic:** Direction flips to +1 (bullish, line = lower band) when Close closes above the prior upper band; flips to -1 (bearish, line = upper band) when Close closes below the prior lower band; otherwise holds its prior side. Flip emits a one-bar ±1 pulse exactly on the direction-change bar.

## SneakyTEMA_v1_0_0.cs
**Description:** Clean-room Triple Exponential Moving Average (Mulloy) — TEMA = 3·EMA1 − 3·EMA2 + EMA3 cascaded EMAs, with a Sneaky glass card. A smoother-library building block.
**Signal logic:** Plots a TEMA moving average/filter; no discrete signal — used as a trend/smoothing reference.

## SneakyTSMA_v1_0_0.cs
**Description:** Clean-room Triple Simple Moving Average — the TEMA construction (3·s1 − 3·s2 + s3) applied to cascaded Simple Moving Averages instead of EMAs, with a Sneaky glass card. A smoother-library building block.
**Signal logic:** Plots a triple-SMA moving average/filter; no discrete signal — used as a trend/smoothing reference.

## SneakyTWMA_v1_0_0.cs
**Description:** Clean-room Triple Weighted Moving Average — the TEMA construction (3·w1 − 3·w2 + w3) applied to cascaded linearly-Weighted Moving Averages, with a Sneaky glass card. Despite the "Triangular" label in some task descriptions, the source's actual formula and description are the triple-WMA cascade, not a triangular kernel. A smoother-library building block.
**Signal logic:** Plots a triple-WMA moving average/filter; no discrete signal — used as a trend/smoothing reference.

## SneakyTapeRecorder_v1_0_0.cs
**Description:** A live-only (realtime-gated) Level-1/Level-2 order-book tape recorder — writes every bid/ask/last quote and depth-book event to a byte-identical gbNRDtoCSV-format CSV file per instrument per day, because NinjaTrader's Market Replay store was found to contain structurally degraded (unusable) depth data. Pure capture device, not a Council sensor.
**Signal logic:** No discrete signal — pure data-capture utility; writes nothing until State.Realtime (never records historical/replay depth, since that data is the exact degraded artifact it exists to avoid).

## SneakyTillsonT3_v1_0_0.cs
**Description:** Clean-room Tillson T3 — a 6-pole cascade of EMAs combined via a volume-factor (VFactor) weighting into a low-lag, low-overshoot smoother, with a Sneaky glass card. A smoother-library building block.
**Signal logic:** Plots a T3 moving average/filter; no discrete signal — used as a trend/smoothing reference.

## SneakyTMA_v1_0_0.cs
**Description:** Clean-room Triangular Moving Average — a double-smoothed SMA (SMA of an SMA) that weights the middle of the window most heavily, with a Sneaky glass card. A smoother-library building block.
**Signal logic:** Plots a triangular moving average/filter; no discrete signal — used as a trend/smoothing reference.

## SneakyTradeHUD.cs
**Description:** An on-chart trade HUD with manual order buttons (stop/target/trail controls) plus an "AUTO TRADE" feature where the user configures up to 10 independent OR-combined long conditions and 10 OR-combined short conditions (each referencing other indicators' plot values), and it auto-submits a market order when any enabled condition in a direction is true. Optionally docks into NinjaTrader's native Chart Trader panel.
**Signal logic:** Submits a market order in a direction as soon as any one of that direction's up-to-10 enabled conditions evaluates true (OR logic across all enabled slots in that direction), where each condition compares a referenced indicator's plot value against a configured threshold/operator.

## SneakyTrendArchitect_v1_0_0.cs
**Description:** NinjaScript port of the Pine Script "Trend Architect" indicator (by Jason/@_hawkeye_13, RedTail Indicators) — an elaborate multi-component trend-strength/consensus system combining Keltner/Bollinger/CCO ratios, an MFI/CCI-based consensus score, fractal-adaptive Heikin-Ashi candles, trend-cloud forecasting, and multiple theming/candle-coloring modes. Publishes SneakyCore.TrendArchitectState and is consumed by the Council as the "ARCH" voter.
**Signal logic:** Draws MQB (bullish) / MQS (bearish) marker dots on the chart when its internal multi-component consensus score crosses into a bullish or bearish signal condition respectively (the consensus blends Keltner-ratio, Bollinger-ratio, and CCO-ratio sub-scores against high/low thresholds); these MQB/MQS signals are the basis several other Sneaky tools (e.g. SneakyV1_0's trade panel) watch for auto-arming.

## SneakyTrendFusion_v1_0_0.cs
**Description:** An original (not a PRISM/TrendArchitect port) fusion sensor that majority-votes five independent directional reads into one Bias: sign of (MFI − 50), a reused CCI bias from SneakyWoodiesCCIPro, a reused CVD direction from SneakyCVD, a fresh Hurst exponent (rescaled-range) regime/trend read, and a 3-period KAMA fan stacking check. Publishes SneakyCore.TrendFusionState and enters the Council at weight 0 pending validation.
**Signal logic:** Bias only carries a direction once at least MajorityNeeded (default 3) of the five sub-reads agree on sign; Agree reports the actual count of agreeing sub-reads. The Hurst sub-read contributes 0 unless its exponent is above the trending threshold, in which case its direction comes from price's own slope over the same window.

## SneakyTrend_v1_0_0.cs
**Description:** The corrected, unified replacement for the old "TrendMagic" family — an ATR-band trailing line whose side is chosen by CCI, fixing TrendMagic's known flaws (true bar-range ATR instead of ATR-of-close, a CCI hysteresis deadband instead of a naked zero-cross, and dot rendering instead of a streaking line). Optionally consults ADX regime and publishes SneakyCore's TrendState.
**Signal logic:** The trailing line flips to the "up" floor only when CCI rises above +CciThreshold, and to the "down" ceiling only when CCI falls below -CciThreshold; between those bounds it holds its last side (a deadband that suppresses whipsaw versus a naked zero-cross). Direction (+1/-1/0) is published as the confirmed trend bias, optionally gated by agreement with an ADX-regime consult.

## SneakyTrendlinesHoldRate.cs
**Description:** Port of "Trendlines with Breakouts, Touches & Hold Rate [LunqFX]" — automatically draws trendlines through confirmed swing-high/low pivot pairs (the best-touch pair wins), tracks touches, breaks and retests, and keeps a running scoreboard of touch-hold rate, break-follow-through rate, and break-retest rate. All decisions are made on closed bars (no repaint).
**Signal logic:** A touch event fires when price approaches a live trendline and reverses off it without closing through (a "hold"); a break event fires when a closed bar closes through a trendline, which then "dies" (is retired) — each break is subsequently checked for a retest (price returning to re-test the broken level) and the scoreboard tallies held/failed touches and followed/failed/retested breaks as percentages, not as forward-looking buy/sell calls.

## SneakyV1_0.cs
**Description:** "Sentinel" V1.0 — a floating/embedded trade-management panel (forked from TrendArchitectMQPanel) offering manual ARM BULL/ARM BEAR buttons plus configurable automatic signal arming from TrendArchitect's MQB/MQS draw-object tags, any indicator's tagged draw objects, or any indicator's named output series, alongside trade management, trailing stop, and session risk-monitor features.
**Signal logic:** When armed, it auto-enters on the next qualifying signal from the selected source: TrendArchitectMQ mode watches for TrendArchitect's MQB (bullish) / MQS (bearish) draw-object tags; DrawObjectTag mode watches for any indicator's draw object matching a configured tag prefix; IndicatorSeries mode treats a nonzero value in a named indicator's output series index as the signal; ManualOnly disables auto-arm entirely (buttons only).

## SneakyVIDYA_v1_0_0.cs
**Description:** Clean-room VIDYA (Variable Index Dynamic Average, Chande) — a Chande-Momentum-Oscillator-modulated EMA whose smoothing speeds up during strong momentum and slows during chop, publishing SneakyCore.VidyaState and wired into the Council as an adaptive-MA trend voter.
**Signal logic:** Bias/Signal is +1 when the VIDYA line's slope rises beyond a small tick-based hysteresis deadband (bullish/long), -1 when it falls beyond the deadband (bearish/short), and holds its last side while the slope stays inside the deadband — a continuously-live ±1 state vote, not a one-bar pulse.

## SneakyVWMA_v1_0_0.cs
**Description:** Clean-room Volume-Weighted Moving Average — Σ(price·volume)/Σvolume over the last N bars (falling back to a plain SMA if total volume is ~0, e.g. a synthetic feed), with a Sneaky glass card. A smoother-library building block.
**Signal logic:** Plots a volume-weighted moving average; no discrete signal — used as a trend/smoothing reference.

## SneakyVolEnvelope_v0_2_0.cs
**Description:** A ground-up rewrite of the Bollinger Band concept: EWMA-based center line, Yang-Zhang (or Rogers-Satchell fallback) range-based volatility estimate instead of close-only stdev, empirically-calibrated per-side (asymmetric) band widths, a native regime classifier (Squeeze/Range/TrendUp/TrendDown/Expansion), and a trend-aware %b that reads a band breach as "extreme" in a range but "riding" in a trend. Advisory-only; optionally publishes SneakyCore.EnvelopeState.
**Signal logic:** No single ±1 buy/sell signal — it classifies the current bar into one of 5 regimes (Squeeze/Range/TrendUp/TrendDown/Expansion) and a %b stretch reading (extreme vs. riding), which downstream tools (the Copier/Arc/strategies) can consult to gate entries (e.g. avoid adding to a position during a Squeeze regime).

## SneakyVolumeImbalancePro.cs
**Description:** Port of "[blackcat] L3 Volume Imbalance Pro" — builds each bar's volume-at-price rows from 1-tick trade prints (tick-rule/quote-rule classified buy vs. sell volume), then flags "diagonal" imbalances where one price row's buy volume dominates the row below's sell volume (or vice versa) by a configured ratio, stacking consecutive imbalanced rows into a count, alongside RSI/MACD/VWAP context.
**Signal logic:** AnySignal fires +1 (bullish) when stacked buy-imbalance layers reach the minimum stack count (optionally confirmed by RSI/MACD/VWAP conditions per BuyScore), and -1 (bearish) for the mirrored stacked sell-imbalance condition; BuyScore/SellScore accumulate how many of the confirming sub-conditions (stacked imbalance, VWAP breakout/breakdown, RSI buy/sell, MACD buy/sell, absorption) are true.

## SneakyVolumeProfilePro.cs
**Description:** Port of "[blackcat] L3 Volume Profile Pro" — builds a per-bar/session volume profile from 1-tick trade prints (tick-rule buy/sell classification, falling back to a crude OHLC estimate without tick history), computing POC/VAH/VAL, cumulative delta (CVD), and POC-migration direction.
**Signal logic:** AnySignal fires +1 (bullish) on conditions such as a POC bounce, VAL support hold, bullish divergence between price and CVD, or accumulation, scored into BuyScore; -1 (bearish) fires on the mirrored conditions (POC rejection, VAH rejection, bearish divergence, distribution) scored into SellScore — the specific trigger set depends on which EnPoc/EnVa/EnDiv/EnMig toggles are enabled.

## SneakyWMA_v1_0_0.cs
**Description:** Clean-room Weighted Moving Average — linear weights 1..n with the newest bar weighted largest, with a Sneaky glass card. A smoother-library building block.
**Signal logic:** Plots a linearly-weighted moving average/filter; no discrete signal — used as a trend/smoothing reference.

## SneakyWAE_v2_0_0.cs
**Description:** Clean-room rewrite of Waddah Attar Explosion from the public formula (replacing an earlier version with unclear licensing lineage) — a MACD-difference momentum histogram compared against a Bollinger-band-width "explosion" line and an ATR-based "dead zone," publishing SneakyCore.WaeState and wired into the Council as a momentum-breakout voter.
**Signal logic:** IsExploding is true when the histogram's magnitude (Power) exceeds both the explosion line and the dead zone; Signal then equals the histogram's sign (Bias: +1 bullish momentum breakout / -1 bearish) while exploding, and 0 otherwise — the classic WAE trigger turned into a directional breakout signal.

## SneakyWallpaper_v1_0_0.cs
**Description:** A purely decorative indicator that draws a faint, theme-matched, vector "ghosted" Sneaky brand-mark (helmet logo) at low opacity centered/anchored on the price panel, with optional engrave/bevel effect. Explicitly exempt from the suite's signal/State-seam convention since it has no market opinion.
**Signal logic:** No signal — pure chart decoration/branding, no plots, no computation on price or volume.

## SneakyWickFill.cs
**Description:** Marks unfilled candle wicks (the gap between a finished candle's body and its high/low) from four timeframes (5-minute, 1-hour, 4-hour, daily) as zones on the chart; a zone is removed once price trades back into it ("wick fill"), ages beyond the lookback, or is superseded by a newer zone. Explicitly described as a drawing aid, not a tested trading signal.
**Signal logic:** No discrete signal — purely a visual drawing aid flagging unfilled wick zones for the trader's own discretionary use.

## SneakyWix9ineRanges.cs
**Description:** Port of the "Wix9ine Ranges" Pine v6 script — tracks completed candles on a higher "story" timeframe (default 60-minute) as sequential ranges (R1, R2, …), determines directional bias from the story-timeframe close relative to the prior range, and detects failed breakouts (wick-through-and-close-back-inside, or a brief close-outside-then-back-in "trap").
**Signal logic:** An entry signal (+1 long / -1 short) fires either on the failed-breakout bar itself (Rejection mode) or after price retests the rejected level and holds (Retest mode); an optional higher-timeframe wick filter restricts longs to when price is at/below the story-candle's open (shorts at/above), and an optional bias filter can require the signal to align with (or at least not oppose) the current story-timeframe bias.

## SneakyWoodiesCCIPro_v1_0_0.cs
**Description:** Sneaky-skinned fork of "WoodiesCCIProV002" — preserves the original Woodies CCI/Turbo-CCI trend-state-machine engine verbatim (raw and persisted trend states, turbo/slope/persistence/strict confirmation logic, weakening/strengthening detection) while re-homing it into the Sneaky palette/card/namespace and publishing SneakyCore.CciState.
**Signal logic:** A ZLR (zero-line reject) or Hook signal fires +1 (long) or -1 (short) per the original Woodies CCI rules — a ZLR is a CCI pullback toward the zero line that reverses without crossing it in the trend's direction, a Hook is a reversal in Turbo CCI's slope while Main CCI remains aligned with the persisted trend state; the persisted trend state itself (strong bull/bull/neutral/bear/strong bear, plus weakening flags) is tracked across bars via a candidate-state confirmation counter before a new state is accepted.

## SneakyZLEMA_v1_0_0.cs
**Description:** Clean-room Zero-Lag EMA — de-lags the input (d = 2·price − price[lag], lag=(N−1)/2) before feeding it into a standard EMA recurrence, reducing the moving average's lag, with a Sneaky glass card. A smoother-library building block.
**Signal logic:** Plots a zero-lag EMA/filter; no discrete signal — used as a trend/smoothing reference.

## SneakyZScore_v1_0_0.cs
**Description:** Clean-room statistical z-score (standard score) sensor — z = (Price − SMA) / StdDev over a rolling period — read as a mean-reversion trigger (a large |z| flags an overextended move likely to snap back). Publishes SneakyCore.ZScoreState as a counter-trend voter, orthogonal to the suite's trend/momentum sensors.
**Signal logic:** Signal is -1 (expect reversion down) when z ≥ +Band, +1 (expect reversion up) when z ≤ -Band, and 0 otherwise; Extreme flags whenever |z| ≥ Band.

## SneakyZeroLagHATEMA_v1_0_0.cs
**Description:** Clean-room Zero-Lag TEMA computed on canonical Heikin-Ashi close (rather than raw price) — applies the TEMA-of-TEMA error-correction de-lag construction (2·TEMA − TEMA∘TEMA) to the HA close series, with a Sneaky glass card. A smoother-library building block.
**Signal logic:** Plots a zero-lag HA-TEMA moving average/filter; no discrete signal — used as a trend/smoothing reference.

## SneakyZeroLagTEMA_v1_0_0.cs
**Description:** Clean-room Zero-Lag Triple EMA — applies the TEMA-of-TEMA error-correction de-lag construction (2·TEMA − TEMA∘TEMA) directly to the raw input, with a Sneaky glass card. A smoother-library building block.
**Signal logic:** Plots a zero-lag TEMA moving average/filter; no discrete signal — used as a trend/smoothing reference.

## TONebula.cs
**Description:** NinjaScript port of TraderOracle's "Nebula v3" (Pine, MPL-2.0) — a large multi-feature indicator offering 14 candle-coloring modes (Vector, Waddah, Squeeze, Volume Delta, Trend Agreement, Tidal Wave, Signal Confluence, RSI Heat, Kernel Slope, Relative Volume, ADX Strength, Heikin-Ashi Trend, Stretch-From-Mean, Supertrend), a trend cloud, take-profit confluence badges, RSI divergence, session levels, and a status dashboard, with 19 selectable color themes.
**Signal logic:** "Tidal Wave" signals: a basic BUY marker fires on a new up-wave (a green bar opening above a prior green close); a basic SELL marker fires on a new down-wave (a red bar opening below a prior red close). A "STRONG BUY/SELL" badge additionally requires an "Ultimate Buy/Sell" confluence condition to have fired within the last 4 bars, combining the Tidal Wave wave-direction signal with the broader take-profit/confluence scoring built into the indicator. A published TidalWave plot carries +1 up / -1 down / 0 none-yet, and a separate buy/sell plot carries +1/-1 on any Tidal Wave buy/sell (basic or strong).

## TONebula_RealFlow_v1_0_0.cs
**Description:** Variant of TONebula that keeps all the same candle-coloring modes, trend cloud, Tidal Wave signals, confluence badges, and dashboard, but replaces the "Volume Delta" mode's OHLC-geometry volume estimate with a real tick-classified buy/sell delta when tick data is available (falling back to the same bar-geometry estimate otherwise) — kept as a separate file alongside the original rather than altering TONebula's behavior.
**Signal logic:** Identical Tidal Wave BUY/SELL and STRONG BUY/SELL confluence-badge signal logic to TONebula.cs (new up-wave / down-wave plus an Ultimate Buy/Sell confluence within the last 4 bars); the only behavioral difference is that its Volume Delta candle-coloring mode is driven by real tick-rule-classified order flow rather than an OHLC-based volume-delta proxy when tick history is present.

## TurtleTrendBreakoutSignal_v1_0_0.cs
**Description:** Signal-only indicator port of the TurtleTrendBreakout_v1_0_0 Strategy — exposes a Donchian-channel breakout (Turtle Trading style) as two numeric plots for external automation to poll, with all order-management/position-sizing/stop logic stripped out.
**Signal logic:** Signal is a persistent +1 (long bias) when price breaks out above the prior N-bar Donchian high, -1 (short bias) when it breaks below the prior N-bar Donchian low, holding that value until the opposite breakout occurs (or a session reset). Trigger is a one-bar pulse version of the same event: +1/-1 only on the bar a NEW breakout happens (and, if AllowContinuationTriggers is on, also re-pulses on every subsequent bar that makes a fresh N-bar extreme in the same direction), intended as the condition an auto-entry bridge should actually watch instead of the stateful Signal plot.

---

## Strategies (20)

## DrummondCAPBreakout_v1_0_0.cs
**Description:** An original, mechanical implementation of a Drummond-Geometry-style "Congestion Action Point" (CAP) breakout — trading a breakout out of a multi-timeframe PL Dot "node" where congestion has formed. Designed for NQ but instrument/timeframe-agnostic; uses the chart's own timeframe plus a second higher timeframe (default 15m).
**Entry logic:** A "PL Dot" (3-bar average of H+L+C/3, displaced forward one bar) is computed on this timeframe and on a higher timeframe. A "node" exists when the two dots are within NodeToleranceTicks of each other, and "congestion" exists when recent closes (CongestionLookback bars) have stayed within CongestionRangeTicks. With both node and congestion true and flat, enters long if Close breaks more than BreakoutTicks above the envelope's upper band (3-bar average of highs), or short if Close breaks more than BreakoutTicks below the envelope's lower band (3-bar average of lows). TradeLongs/TradeShorts toggles can disable either side.
**Exit logic:** No profit target; a stop trailed by hand off the PL Dot every bar (never loosened), and an immediate exit if price closes back through the PL Dot against the position.

## DrummondEnvelopeSqueeze_v1_0_0.cs
**Description:** An original Drummond-Geometry-style strategy trading a breakout that follows a squeeze (contraction then expansion) in the PL Dot envelope's width — a volatility-squeeze/breakout approach. Instrument/timeframe-agnostic, single timeframe only (no multi-timeframe confluence).
**Entry logic:** Envelope width = upper band (3-bar avg of highs) minus lower band (3-bar avg of lows). A "squeeze" is confirmed when envelope width has stayed at/below SqueezeThresholdTicks for each of the last SqueezeLookbackBars bars. An "expansion breakout" fires when, with that squeeze active, the current bar's width pushes back above the threshold AND the same bar closes more than ExpansionTicks beyond the upper band (long) or lower band (short). Enters long or short accordingly when flat.
**Exit logic:** No profit target; stop trailed off the PL Dot every bar (never loosened), with an immediate exit if a bar closes back through the dot against the position.

## DrummondPLDotPullback_v1_0_0.cs
**Description:** An original Drummond-Geometry trend-continuation strategy: trades a pullback to the PL Dot in the direction of a higher-timeframe trend ("trading with the dot"), companion to the CAP Breakout strategy. Instrument/timeframe-agnostic with a second higher timeframe (default 15m).
**Entry logic:** Higher-timeframe trend is up/down if the HTF PL Dot has moved by more than TrendSlopeTicks over TrendLookbackBars. A "pullback" is recorded if, within PullbackLookback bars, price touched the current-timeframe PL Dot within TouchToleranceTicks. Entry requires an HTF trend, a recent pullback touch, and a two-bar reversal confirmation: the current bar closes beyond the touch bar's own high (long, in an uptrend) or low (short, in a downtrend) and on the trend side of the dot.
**Exit logic:** Three independent exits: an ATR-based stop (ATR × StopAtrMultiple) trailed off the PL Dot each bar (never loosened, only eligible once MinBarsInTrade have elapsed for the dot-fail exit), an optional fixed ATR-based profit target set once at entry, and an immediate exit if the price closes back through the dot against the position (gated by MinBarsInTrade).

## DrummondTripleConfluence_v1_0_0.cs
**Description:** An original Drummond-Geometry strategy trading immediate entry when three PL Dots (this timeframe plus two higher ones) all stack in the same directional order — multi-timeframe confluence as the entry trigger itself, no separate breakout or pullback trigger. Instrument/timeframe-agnostic with mid (default 5m) and high (default 15m) timeframes.
**Entry logic:** Computes a PL Dot on the primary, mid, and high timeframe series. "Confluence up" = primary dot > mid dot > high dot; "confluence down" = primary dot < mid dot < high dot. Enters long immediately on confluence-up while flat (if TradeLongs), or short immediately on confluence-down (if TradeShorts) — no additional pullback/breakout trigger needed.
**Exit logic:** A stop trailed off the primary-timeframe PL Dot every bar (never loosened), plus an exit once confluence breaks (gated by MinBarsInTrade so it can't fire right after entry); no separate profit target.

## GodTradesStrategy_v1_0_0.cs
**Description:** A mechanical implementation of four candle-gap trading setups from the "GOD TRADES" slide deck (TraderOracle) — a gap-fill/gap-line trading methodology combining Bollinger Bands, trend filters, and a tracked gap-line engine. Designed to run within a configurable trading window (default 10:15am–3:00pm ET).
**Entry logic:** Tracks "gap lines" (midpoint of any close-to-open price skip ≥ MinGapTicks) that must age MinBarsBeforeValid bars unfilled before a touch counts. Four independently-toggleable setups: (1) **Bollinger Gap** — two consecutive same-direction candles gapping clear of each other, with the second touching/near the Bollinger lower band (long) or upper band (short); (2)/(3) **Fill and Reverse / Fill and Continue** — when a valid gap-line is touched/filled, trades the reversal direction if counter to a trend-MA slope (Fill and Reverse) or the continuation direction if aligned with it (Fill and Continue); (4) **Final Line** — after several consecutive same-direction gap-fills in a row (≥ FlMinPriorFillsInSequence), trades the opposite direction on the theory of an "insane rush" reversal. An optional "Spiderweb filter" vetoes all new entries when multiple untouched gap lines cluster near price.
**Exit logic:** Each setup uses its own stop (candle extreme ± buffer, kept tiny per the deck) and target (opposite Bollinger band or a fixed-tick target depending on setup/mode); optional flatten at trading-window end.

## LinRegUTBotReversalStrategy.cs
**Description:** An auto-trading reversal strategy that filters UT Bot Alert signals through a "Humble Linear Regression" (LinReg) candle signal line — a trend-following reversal/confirmation system. Supports an optional order-flow (cumulative-delta slope) confluence filter.
**Entry logic:** Reads the UT Bot indicator's exported Signal series (ATR-trailing-stop crossover: buy on Close crossing above the UT trailing stop, sell on crossing below). That UT direction is held "pending" until the Humble LinReg's smoothed candle close confirms it: a pending long is validated when the LinReg close is above its own signal line (SMA/EMA of LinReg close), and a pending short when below. An optional line-price filter additionally requires actual Close to be on the correct side of the LinReg signal line, and an optional order-flow filter requires the cumulative-delta regression slope (when confident, R² above threshold) to agree with the trade direction. On a valid signal the strategy reverses or enters.
**Exit logic:** Stop can be a LinReg-signal-line-based trailing stop (clamped to stay on the valid side of the market) or a fixed-tick stop; profit target is fixed-tick; supports break-even stop movement.

## PraedoxSentinel_v0_1_0.cs
**Description:** A large, unified rebuild of a lost personal trading product ("Praedox Sentinel") — an original from-scratch implementation based only on surviving marketing copy/screenshots, not recovered source. Combines a 10-signal rule engine, a Fibonacci engine, volatility-based position sizing/filters, trade-protector/daily-P&L limits, and an on-chart "cockpit" dashboard with manual and ATM-based order entry, all inside one Strategy.
**Entry logic:** Only auto-enters when flat and EnableAutoTrading is on. Ten independently-toggleable signals (grouped Zone/Cycle/Momentum/Reversal/Breakout), each returning +1/-1/0 per bar: (1) **Slingshot** — price compresses (range < ATR×fraction) then releases on an expansion bar closing in the top/bottom third of its range; (2) **Zone Break** — close breaks beyond a recent high/low zone by a minimum tick distance, aligned with the trend EMA, with chop filtered out; (3) **Trend Cycle Wave** — trend-EMA direction plus a Stochastic %K crossing back from oversold/overbought; (4) **CrossFire** — a volume surge plus trend-EMA direction agreeing with RSI above/below 50; (5) **Reversal** — price at a structural high/low (or near a key Fibonacci level) with RSI extreme and a long wick showing rejection; (6) **Continuation** — trend-EMA sloping, price touches the EMA then closes back through it in the trend direction; (7) **EMA Breakout** — price crosses the trend EMA with RSI confirming side; (8) **Aggressive EMA** — price crosses a fast EMA; (9) **Volume Aggressive** — a volume surge with Close higher/lower than the prior bar; (10) **Breakout** — close breaks a longer-lookback structural high/low on an expansion bar. Many signals are also filtered by a chop index, S/R proximity, large-bar/news filters, and macro EMA trend alignment. Manual entry paths (chart-click buttons, ATM-slot quick buttons) bypass most of these gates.
**Exit logic:** Signal-fired trades use a stop/target sized either from Volatility-Shield-derived ticks or from the active Cockpit "ATM slot" preset, with an optional trailing-stop method per slot; separately, a ported Trade-Protector module (consecutive win/loss streaks) and Daily P&L limits can auto-flatten the account and block further entries.

## PraedoxTradeProtector_v0_1_0.cs
**Description:** Not a trading strategy with its own entries — a risk-management/account-protection Strategy that stops automated trading and flattens the account when a session win/loss streak limit or a daily $ profit/loss limit is hit. Rebuilt from marketing copy for a lost personal product ("Praedox Sentinel"); state persists across NinjaTrader restarts via a small per-account text file, keyed to the calendar day.
**Entry logic:** Places no entries at all; it only monitors account-level realized P&L (via OnAccountItemUpdate) and flat-to-flat trade results (via OnPositionUpdate) to track consecutive win/loss streaks and daily session P&L.
**Exit logic:** When MaxConsecutiveWins or MaxConsecutiveLosses is hit, or daily P&L reaches MaxDailyProfitDollars/MaxDailyLossDollars, it flattens every open position on the account (all instruments, via Account.Flatten) and sets a static flag (PraedoxTradeProtectorState.TradingBlocked) that other scripts can read to stop new automated entries for the rest of the session.

## SneakyBridge_v0_2_0.cs
**Description:** An automated "Council-consumer" strategy with an on-chart control surface — trades a fused multi-voter bias/conviction signal ("Council") or a simpler debounced "Confluence" trigger published by other indicators in the Sneaky Suite, with risk-gating, sizing, and a Helm manual-override layer. Part of the Sneaky Suite architecture rather than a standalone technical setup.
**Entry logic:** Reads a published "verdict" each bar (from either the Council or Confluence indicator, selectable). An entry fires when the verdict has an actionable edge (HasEdge, non-zero EntryDir) different from the last-fired bias, the strategy is armed (not disarmed/paused), conviction clears a MinConviction floor, SizeMult is > 0, and (optionally) a fresh "God Reversal" trigger aligns with the direction for entry timing. Position size is derived from BaseContracts (or $-risk sizing) scaled by the verdict's SizeMult, then clamped by SneakyCore's account-profile/governor sizing rules, and validated by a risk gate (SneakyCore.GateEntry) before EnterLong/EnterShort.
**Exit logic:** Managed fixed-tick stop/target (from a lab-derived config file or manual properties); optional exit-on-flip (closes when the source's held bias stops supporting the open side) and optional reverse-on-flip (reverses on an aligned opposing signal).

## SneakyFtkaTrendFollower.cs
**Description:** A NinjaTrader 8 port of a TradingView Pine v6 strategy ("FTKA Trend Follower") — a trend-following system combining an Adaptive Trend Flow (ATF) band, SMA pullbacks, and a Waddah Attar Explosion (WAE) momentum filter, with staged take-profits. Designed and backtested on NQ at 15m/30m timeframes; author notes it lost money on 1-5m charts.
**Entry logic:** Three switchable entry types, each arming a stop order at the signal bar's high+buffer (long) or low-buffer (short), valid for N bars: (1) **Reversal** — a bar in the trend color after N consecutive opposite-colored candles; (2) **ATF color-flip** — the first green/red candle after the Adaptive Trend Flow band flips direction; (3) **SMA pullback** — price touches (or comes within a proximity) of the SMA, then closes back in the trend direction (up to MaxPullbacks per ATF phase). All entries additionally require: close on the correct side of the SMA (and the SMA must not sit inside the ATF band), optional ATF mid-line filter, and optional "WAE momentum active" requirement.
**Exit logic:** Hard stop (65 ticks default); TP1 takes 75% of the position at 44 ticks, moving the stop to a breakeven-plus-buffer floor; the runner (TP2, 106 ticks) exits early if WAE momentum is lost for 2 bars while price is on the profit side of the floor; flattens at session end and during a no-trade window; optional daily profit/loss cap.

## SneakyGexRadarStrategy.cs
**Description:** A standalone GEX (gamma exposure) stop-hunt radar strategy with built-in entries and exits, fed by options-derived level data (call/put walls, gamma flip, pin strikes, etc.) from a companion CSV file — a GEX/options-flow-driven strategy with several selectable setup types, plus order-block/FVG zone confluence scoring. Built-in Kinetic Cockpit/Pin Payoff/GEX Profile panels for NQ/ES/YM-style instruments.
**Entry logic:** Six independently-toggleable setup types, each producing a scored candidate and only the highest-scoring one per bar is traded (subject to a MinTradeScore/MinScore floor): (1) **Stop-hunt sweep** (original) — a wick sweeps through a GEX level (within MaxDepthAtr) and the bar closes back across it; (2) **Gamma breakout** — close crosses through a call/put wall by ≥0.2×ATR, trading continuation; (3) **Wall-touch fade** — price touches a wall without closing through, then closes back ≥0.2×ATR away, trading the fade; (4) **Flip breakdown** — close crosses through a gamma-flip/vol-trigger/zero-gamma level by ≥0.2×ATR, continuation; (5) **Pin-trap reversal** — price stretches ≥2.5×ATR from a pin-strike/charm-magnet level with a reversal candle back toward it; (6) **GEX chop range** — in positive net-GEX chop, fades the edges of the nearest bracketing levels toward their midpoint. An additional, independent **FVG retest** entry triggers when price retraces into a fresh fair-value gap and a candle closes back out in the trend direction. Score is boosted by order-block/FVG proximity, trend-EMA alignment, and candle-close position in its range.
**Exit logic:** Managed stop/target set at the detected setup's level-based stop and either the nearest qualifying GEX level/strike or an R-multiple target; optional exit on an opposite-direction setup printing; optional flatten at a configured time of day.

## SneakyKeel_v0_1_0.cs
**Description:** An instrumented version of a "range filter" fresh-flip stop-and-reverse strategy (functionally equivalent to a known RangeFilterATRStrategy), adding Ledger/audit logging, excursion recording, and an advisory risk gate on top of unchanged trading rules. Trend-following, always in the market (stop-and-reverse), instrument/timeframe-agnostic.
**Entry logic:** Builds a "range filter" line from a two-stage EMA-smoothed absolute close-to-close range: the filter can only move up on rising price and down on falling price (a ratcheting smoothed line). A trend flips to +1/-1 when the filter itself moves up/down versus the prior bar. A "fresh" flip (trend changes from the previous bar's trend) triggers a stop-and-reverse: if currently in the opposite direction, it exits that side first then (if the flip is enabled for that direction) enters the new direction; if flat, it enters directly in the new trend direction.
**Exit logic:** ATR-based (or optional fixed-tick) stop and target bracket set at entry via SetStopLoss/SetProfitTarget; otherwise exits only via the next opposite fresh flip (stop-and-reverse).

## SneakyLivewirePropBusterStrategy.cs
**Description:** A NinjaTrader 8 port of a TradingView strategy combining an Opening-Range-Breakout (ORB) midline fade with Brooks-style H1/H2 (and L1/L2) pullback-leg continuation entries, plus an optional "eval bot" layer that sizes/paces trading to pass a prop-firm evaluation account. Designed for intraday futures trading with a defined ORB window and flatten time.
**Entry logic:** Two families of setups: (1) **ORB midline fade** — after price breaks the opening range high/low, waits for a first pullback candle (phase 1→2), then a break of that pullback leg's extreme (phase 2→3), then a 5-minute close back inside the opening range before the midline is reached — fires a fade entry (short after an up-break, long after a down-break) at the close of the entry candle, optionally skipping on a trend filter or news days; (2) **Brooks H1/H2 (L1/L2) legs** — counts consecutive higher-high (for longs) or lower-low (for shorts) pullback/break sequences after the ORB window, entering a stop order one tick beyond the high/low on the 1st (H1/L1) or 2nd (H2/L2) qualifying leg, filtered by Trend Magic (CCI/ATR trend line) direction, optional PSAR and ADX/DMI filters, and per-day/per-trend trade caps.
**Exit logic:** Fade trades use an R-multiple or midline-percentage target with a stop beyond the setup's extreme, with optional move-to-breakeven after a trigger R; leg trades use an R-multiple target with a stop from PSAR, the pullback extreme, or the bar low/high; all positions flatten at a configured end-of-day time, and separate daily max-profit/max-loss limits (or the eval bot's pacing/target/drawdown rules) can stop trading for the day.

## SneakyRegimeSwitch_v1_0_0.cs
**Description:** A single self-contained strategy that classifies the current market regime (Trending, Ranging, or Choppy) every bar using an ADX + Bollinger-Band-width composite, then runs a different purpose-built sub-strategy depending on the detected regime, flattening and handing off cleanly on a regime change. Designed for NQ but timeframe/instrument-agnostic; author notes thresholds are textbook defaults, not validated.
**Entry logic:** Regime classification: Trending if ADX > AdxTrendThreshold; Ranging if ADX < AdxRangeThreshold and Bollinger-Band width is "tight" (below BbWidthTightRatio × its own rolling average); otherwise Choppy (with a hysteresis dead-zone between the two ADX thresholds, and a debounce requiring RegimeConfirmBars consecutive bars before an actual regime switch). Per regime: **Trending** enters long when Close breaks above the prior N-bar Donchian high, short when it breaks below the Donchian low; **Ranging** enters long when Close touches/exceeds the lower Bollinger Band with RSI oversold, short at the upper band with RSI overbought (mean-reversion); **Choppy** is flat by default (optional experimental mirrored mean-reversion logic on a shorter Bollinger Band if explicitly enabled).
**Exit logic:** Trending uses an ATR chandelier trailing stop combined with a tighter fixed ATR-multiple hard stop from entry (tighter of the two); Ranging targets the Bollinger mid-band with a hard ATR stop beyond the touched band; any open position is flattened immediately when a new regime is confirmed (if CloseOnRegimeChange is on); an optional hard fixed-tick stop/target bracket can run in parallel as a backstop.

## SneakySecretMindsetStrategy.cs
**Description:** Mechanical tests of five scalping/swing setups attributed to "thesecretmindset.com," run on a 1-minute or 5-minute NQ/MNQ chart, selectable via a Mode parameter (signals confirmed on bar close, entries at next bar).
**Entry logic:** Five modes: (1) **ScalpBreakout** — fast EMA (200) above/below slow EMA (1000) bias, enters on a close breaking the prior N-bar high/low in that direction, fixed stop/target; (2) **ScalpDivergence** — hidden divergence (price makes a higher pivot low / lower pivot high while Stochastic %K makes a lower/higher reading) in the EMA-trend direction, confirmed by the first Stochastic %K/%D cross, fixed stop/target; (3) **ObvBreakout** — OBV vs its 200-EMA sets side, requires a recent Bollinger-Band-width squeeze, enters on a close breaking the prior range in the OBV-confirmed direction, swing stop/N:1 target; (4) **DoubleDoji** — two consecutive doji candles (small body/range), enters on a breakout of their combined range in the OBV trend direction, swing stop; (5) **EmaPullback** — 50/200 EMA trend, enters on a pullback to the 50 EMA followed by a break of the prior bar's high/low back in trend direction, swing stop/N:1 target.
**Exit logic:** Scalp modes use fixed-tick stop/target; swing-stop modes use an N:1 reward:risk target off a swing-based stop (min/max risk ticks clamp); no trailing.

## SneakyTBarsEdgeProbe_v1_0_0.cs
**Description:** A deliberately simple, "Council-free" diagnostic strategy meant to test whether an observed ~66% first-touch win rate on a particular bar type (TBars) survives honest, high-resolution order fills, isolating the bar type itself as the source of any edge — not intended as a production trading strategy. Instrument-agnostic.
**Entry logic:** Reads the brick/bar direction as the sign of Close minus Open for the completed bar. Enters only when flat; by default (EntryOnFlipOnly) requires the brick direction to flip versus the prior bar before entering in that new direction; optionally (toggle off) enters in the brick direction on every bar regardless of flip.
**Exit logic:** Symmetric R target/stop (R = max of MinTicks or AtrMult × ATR, in ticks) set via SetProfitTarget/SetStopLoss — a fixed bracket, no trailing.

## SneakyTrendStrategy_v1_0_0.cs
**Description:** A risk-managed trend-flip strategy built on the "SneakyTrend" indicator (an ATR+CCI-hysteresis trend line), designed to supersede a set of unmanaged TrendMagic-based strategies by adding real stop/target risk control, Sneaky Suite entry gating (kill-switch, feed health, daily governor, session/rollover/news checks), and optional $-risk-based position sizing and ADX trend-alignment confirmation.
**Entry logic:** Enters (stop-and-reverse) whenever the SneakyTrend Direction series flips from -1 to +1 (long) or +1 to -1 (short). Before submitting, the entry must pass SneakyCore.CanEnter (fails closed on any gate block or missing Account) and, if enabled, an ADX-alignment check requiring ADXPro to show "trend on" and bias agreeing with the flip direction. On a flip where the reverse entry is gated off, the strategy still flattens the existing opposite position so it's never left stranded.
**Exit logic:** ATR-sized stop (ATR × StopAtrMult, floored at MinStopTicks) with an R-multiple profit target (RewardRisk × stop distance); optional trailing stop that rides the SneakyTrend line itself (monotonic, only ever tightens), clamped against the live bid/ask with an ATR-scaled safety buffer before each trail update.

## SneakyWix9ineRangesStrategy.cs
**Description:** Trades the Signal output of a companion "SneakyWix9ineRanges" indicator — a failed-breakout-of-a-range setup (a higher "story" timeframe range is tracked, and a failed break of it on the lower chart timeframe signals a trade), via Rejection or Retest entry modes. Requires the chart timeframe to be lower than the indicator's story timeframe.
**Entry logic:** Entirely delegated to the SneakyWix9ineRanges indicator's Signal value (computed from story-timeframe range tracking, a bias filter, wick filter, fail-bar count, and retest window — configuration passed straight through as strategy parameters): Signal > 0 enters long (if AllowLong) and Signal < 0 enters short (if AllowShort), at market on the bar after the signal bar; a Reverse option lets an opposing signal flip an open position.
**Exit logic:** Stop placed beyond the extreme of the last N bars (including the signal bar) plus a tick buffer (clamped between MinStopTicks and MaxStopTicks); target = RewardRisk × stop distance; optional session-close flatten.

## StraddleBreakoutOCO_v1_0_0.cs
**Description:** A continuous, always-on two-sided breakout straddle: the moment it's enabled (and anytime it goes flat), it rests a buy-stop and a sell-stop equidistant (OffsetTicks) from the current market price in a real OCO group via unmanaged orders, so whichever side the market breaks toward fills and the other is cancelled. No directional bias or indicator — pure symmetric breakout-of-current-price.
**Entry logic:** No technical trigger beyond price simply trading through one of the two resting stop orders placed OffsetTicks above/below the market; a fill on either side auto-cancels the other via the shared OCO group (with manual self-heal logic for asymmetric order failures).
**Exit logic:** A fixed-tick trailing stop (StopTicks) applied the instant a side fills and hand-trailed every bar thereafter (clamped to stay valid against the live bid/ask); no profit target — the trail is the only exit, and a fresh straddle pair is re-armed immediately at the new market price the instant the position returns to flat.

## TurtleTrendBreakout_v1_1_0.cs
**Description:** A classic trend-following Donchian-channel breakout system in the spirit of the Turtle Trading rules — ATR-based initial stop and ATR-based trailing stop instead of a fixed profit target, so winners aren't capped. This v1.1.0 fork drops the OCO straddle-order pair of v1.0.0 (for prop-firm accounts that reject OCO) in favor of watching Donchian levels locally and sending a single market order only on the side price actually crosses.
**Entry logic:** Tracks the prior N-bar (EntryChannelLength) Donchian high/low as local targets, only "arming" them once price has traded back inside the channel (so enabling the strategy while already outside the channel does not fire immediately). When price crosses the armed long target (prior Donchian high) a market buy is sent (if AllowLong); crossing the armed short target (prior Donchian low) sends a market sell-short (if AllowShort). No resting OCO order pair — only one side is ever actually submitted.
**Exit logic:** ATR-based initial stop plus an ATR-based trailing stop (no fixed profit target by design, consistent with letting trend trades run); respects manually-dragged/HUD stop moves instead of reverting them.

---

## AddOns (21)


## SneakyAlertService_v1_0_0.cs
**Description:** A headless, always-on AddOnBase singleton that subscribes to `SneakyCore.Alerts.Raised` and turns each suite alert into something noticeable away from the chart — sound (a `.wav` via SoundPlayer, or a System sound fallback) and/or an optional push notification via a user-configured shell command (e.g. curl to Pushover/ntfy/Slack). It has no WPF panel of its own; the dashboard's Test tab provides a live config API (`GetConfig`/`Apply`/`Reload`) to edit `Alerts.conf` without restarting NT. Configurable settings (in `Sentinel\Alerts.conf`): `enabled`, `playInfo` (play a sound on Info-level alerts too), `throttleSec` (min seconds between same-level sounds), `critWav`/`infoWav` (custom sound paths), `pushCommand`/`pushOnInfo`.
**Key behavior/trigger:** Purely a notification channel — it never acts on the account; sound/push run only in response to alerts raised elsewhere (the actual risk/gate logic lives in other tools such as SneakyRiskService).

## SneakyArcService_v0_1_0.cs
**Description:** A headless orchestration/supervision AddOn for the suite's "leader" signal-engine fleet. Since an AddOn cannot start/stop a chart strategy, Arc instead publishes a per-instrument fleet plan (enable/size/session window, read from `Arc.conf` or the dashboard) to `SneakyCore` for any Sentinel-aware strategy to consult via `SlotLive()`, and separately supervises the leader account's per-slot position, day P&L, fills-today, and a health verdict (OFF/CLOSED/IDLE/LIVE/DARK). No dedicated WPF panel is shown in this version; it is configured via `Arc.conf` (`leader=`, `slot=<inst>|<strategy>|on/off|size|session`).
**Key behavior/trigger:** Publishes the fleet plan and health status but does not itself place or block trades in this version — that control half is deferred until a consuming strategy opts in to `SlotLive()`.

## SneakyBinds_v0_1_0.cs
**Description:** A pure UI-convenience AddOn providing window snapping (drag/resize NT windows to click into alignment with each other and the monitor edges on release) and named multi-window layouts (capture an arrangement of open windows under a name and re-apply it in one click). It operates entirely via Win32 HWND calls (GetWindowRect/SetWindowPos) rather than WPF properties, because NT's multi-threaded windows throw cross-thread exceptions otherwise. Opens from Control Center ▸ New ▸ "Sneaky Binds"; settings persist to `Sneaky\Binds\binds.conf` and layouts to `Sentinel\Binds\<name>.layout`.
**Key behavior/trigger:** None — explicitly documented as having no market data, no orders, and no SentinelCore/Council wiring; it is a desktop window-management utility only.

## SneakyCockpit_v0_1_0.cs
**Description:** A dockable/floatable, always-on-top WPF rail ("command surface") that re-reads SneakyCore's published `…State` seams so the operator can see at a glance whether the brain is alive and why it isn't trading, without opening the Indicator dialog or grepping logs. Its panel shows Decision (Council verdict + "why no trade"), Gate (kill-switch + per-account Governor), Context (modulators), and Voters (confluence detail), plus a BUILD mode roster/lane editor and a HELM/Interdict section that lets an operator grab the wheel of a running automated actor by publishing intents (Pause/Resume/Skip/Flatten/Breakeven/MoveStop/MoveTarget/Scale-down/TakeOver).
**Key behavior/trigger:** The Cockpit itself never touches an order — it only reads `Get…State` seams and, for Helm, publishes `HelmIntent` objects via `SneakyCore.SetHelmIntent`; the owning actor (elsewhere) executes each intent with its own order handles, with risk-adding verbs required to pass `GateEntry`. So any trading action it triggers (e.g. a manually-issued Flatten) is executed by a separate downstream component, not by this file.

## SneakyCopierService_v0_1_0.cs
**Description:** A headless, always-on multi-account trade-copier service. It subscribes to a designated LEADER account's `ExecutionUpdate` and, on each leader fill, submits a market order on every enabled/connected FOLLOWER account sized by `leaderFillQty × instrumentSizeRatio × followerMultiplier` (with optional instrument remapping, e.g. GC→MGC). It is a pure fill-mirror (never reads strategy internals), has config persistence, manual-assist mode, a position-drift reconciler to detect desync, a same-provider policy, a consistency governor/session gate, and a live-only bracket-mirror feature. Order names use the `"SnkCopy_"` prefix.
**Key behavior/trigger:** Every mirrored order passes through `CanMirror()` → `SneakyCore.CanActInstrument()`, i.e. it is blocked if the shared kill-switch is engaged, an instrument-scoped kill is active, or feed health fails. Two optional config toggles directly gate copying: `UseEyeGate` (mirror only entries that `SneakyEye` currently qualifies; exits always mirror) and `RejectIfInTrade` (refuse to add to a follower's existing position on that instrument, though a genuine exit/reduce always still mirrors).

## SneakyCore.Foundation.cs
**Description:** The lowest "Foundation" layer of the suite's shared static-partial `SneakyCore` class (no WPF panel — pure library code). It provides the settings directory (`<UserDataDir>\Sneaky\`), the shared logging helper (`Log`/`WriteLogFile`), a generic `SeamStore<T>` publish/consult registry, bar-tag/instrument-root/scope helpers, the `Conditions` debounced-alert mechanism, the `Alerts` two-tier (Critical/Info) alert bus, and the context-level vetoes (kill-switch, instrument-kill, news, rollover) that every other tool consults. It deliberately depends on nothing above it (no Bus/Safety references), so a minimal distribution can ship with only this layer.
**Key behavior/trigger:** Hosts the kill-switch/instrument-kill/news/rollover veto flags that other tools (Risk, Copier, strategies) read before acting, but this file itself never issues an order — it is the data/plumbing substrate the gating logic in `SneakyCore.Safety.cs` and consumer AddOns build on.

## SneakyCore.Safety.cs
**Description:** The "Safety" partial of `SneakyCore` — the account-risk DECISION layer (feed-health, `CanEnter`, the per-account Consistency Governor, drawdown, account profiles, session windows, sizing, order guards, and `GateEntry`, the single pre-submit choke point). This is the one Core file that bundles omitting it must exclude, because it is the only layer that ever influences whether an order is allowed to go out. The Governor tracks each account's daily realized P&L against a prop-firm cap (`best_day ≤ R×target`) plus a loss-stop and publishes a `GovernorState` (Status: "Trading"/"DayComplete"/"DayHalted") that consumers check via `TradingAllowedToday()`; an account with no published governor state fails OPEN (trades normally).
**Key behavior/trigger:** `GateEntry`/`CanEnter` are consulted by strategies and other AddOns before any entry; an account is blocked from further entries once its Governor state reports `DayComplete` or `DayHalted` (daily P&L cap hit or loss-stop breached), though this file itself does not flatten positions — it only withholds permission for new entries.

## SneakyCore.SystemBuilder.cs
**Description:** A "System Builder" partial of `SneakyCore` adding `RosterIO` and `LaneIO` — hot-reloadable config readers/writers for `Roster.conf` and `Lane.conf`, the files that let an operator author which "voters" (signal components) participate in the Council's fusion for a given scope/lane, with per-voter weight and State/Trigger kind overrides. It defines the shared `VoterKind` enum (`State`/`Trigger`) and the `RosterLine` model. No UI of its own; it is consumed by the Cockpit's BUILD mode editor and the Council indicator.
**Key behavior/trigger:** None — explicitly documented as touching no seam and no Gate, and placing no orders; it is pure configuration I/O.

## SneakyCore_v1_0_0.cs
**Description:** The main (and largest) file of the suite's one intentional shared dependency — the stable, unversioned `SneakyCore` static class that every other Sneaky tool depends on for the shared kill-switch, the pluggable feed-health gate, the settings directory, and the common log helper. Over many versions it has accreted the suite's full seam registry (dozens of `…State` publish/consult stores for Council voters, Eye, Flux, Drift, Harmonic, TrendArchitect, VIDYA, etc.), the Helm interdiction-intent seam, the Ledger, and governor/account-profile plumbing. It has no WPF panel of its own (it is a library consumed by indicators, strategies, and the other AddOns/Dashboard/Cockpit).
**Key behavior/trigger:** Defines `SetKillSwitch`, `CanAct`/`CanActInstrument`, and `GateEntry`/`CanEnter` — the central chokepoints that every trading AddOn or strategy in the suite is expected to call before acting. It does not itself decide to flatten or halt anything proactively; it is the shared mechanism other tools (Risk, Copier, Equity Gate) use to engage/consult the kill-switch.

## SneakyDashBridge.cs
**Description:** A compact headless AddOn that feeds the separate local-web "Sneaky Dashboard" page. Every ~2 seconds it writes `<NT data folder>\SneakyDash\state.json` (accounts, positions, working orders, per-strategy trades with MAE/MFE) and appends every execution to `executions.csv`. It also defines the static `SneakyGate` class, which strategies opt into with two lines (`SneakyGate.Allow(Name, true/false)`) to check a pause/long-only/short-only gate read from `commands.json` (written by the dashboard).
**Key behavior/trigger:** It places exactly one kind of order itself: pressing the dashboard's Flatten button sends a flatten (cancel working orders + close positions) on one account, and only fires if the file `SneakyDash\allow_flatten.txt` exists — an explicit opt-in safety file gating that single manual action. Separately, `SneakyGate.Allow()` returns false (blocking a strategy's entry) when the dashboard's `commands.json` marks that strategy (or all strategies) paused, or marks that specific direction (long/short) disallowed.

## SneakyDashboard_v1_0_0.cs
**Description:** The suite's single unified configuration/control window — a WPF `NTWindow` opened via Control Center ▸ New ▸ "Sentinel Suite", built as a `TabControl` with one tab per Sentinel tool (Copy, Log, Risk, Lens, Excursions, Accounts, Home, etc.). Each tab attaches to that tool's already-running headless service singleton (e.g. `SneakyCopierService_v0_1_0.Instance`) rather than owning it, so closing the dashboard never stops a service; a shared top bar shows the suite-wide kill-switch and live status from every tab. The Accounts tab exposes the full Consistency Governor (manual daily cap, daily-reset hour, trailing/static/EOD drawdown + flatten buffer, and an "Auto-flatten" / `hardEnforce` checkbox) so account risk profiles can be edited without hand-editing `Profiles.conf`.
**Key behavior/trigger:** The dashboard itself is a configuration/monitoring UI and does not place orders directly; it writes settings (e.g. drawdown caps, the `hardEnforce` auto-flatten flag, Eye-gate/RejectIfInTrade toggles) consumed by other services (Core's Safety layer, the Copier) which then perform the actual gating/flattening.

## SneakyEquityGate_v1_0_0.cs
**Description:** A WPF AddOn window implementing a SIM-to-LIVE equity-curve routing controller. It watches a MASTER (SIM) account, builds a trade-by-trade equity curve from completed round-trips, computes an EMA of that curve, and uses an EMA-cross state machine to decide whether LIVE routing is permitted: warm-up (needs EMA-period points) → optional wait for a cross-down first → cross back above the EMA enables LIVE → a subsequent cross below disables it (SIM only) until the next cross-above. It is a standalone fill-mirror engine with its own ARM/DISARM toggle button and per-target daily LIVE loss limit, independent of (and not to be run alongside) `SneakyCopierService`.
**Key behavior/trigger:** While LIVE routing is enabled by the EMA-cross state AND the operator has pressed ARM (confirmed via a warning dialog; the engine always starts DISARMED on NT launch), every master-account fill is mirrored as a market order to the checked LIVE target accounts, sized by `fill × multiplier`. Separately, each LIVE target has a daily loss limit: once a target's realized+unrealized P&L since the daily reset reaches that limit, it stops receiving further copies for the day and, if "Flatten on limit" is checked, is automatically flattened.

## SneakyExcursions_v1_0.cs
**Description:** A static, on-demand analytics class (no AddOnBase service, no persistent window — called by the Dashboard's Excursion tab on a button click) that reads raw signal-excursion JSONL records written by a companion indicator recorder and converts them into actionable per-(instrument × signal × direction) distributions: percentiles of max MFE/MAE and fixed-horizon (1/5/15/60 min) milestone curves, fire-rate per day, conviction-bucket partitioning, and an Eye-endorsement "does the filter add edge?" verdict code.
**Key behavior/trigger:** None — purely a read-only statistical analyzer over historical excursion data; it takes no action on any account.

## SneakyLens_v1_1_0.cs
**Description:** A static, read-only analytics class (not an always-on service; called on-demand by the Dashboard's "Lens" tab) that parses the MAE/MFE trade-record JSONL files written by `SneakyLogEngine`/`SneakyLogService` and aggregates win rate, profit factor, net/average ticks, average heat (MAE) and favorable excursion (MFE), and MFE-capture efficiency — overall and broken down by strategy/instrument. It also partitions trades by the `SneakyEye` verdict that stood at entry (Endorsed/NotEndorsed/NoVerdict) and by Eye score-band, to answer whether Eye-endorsed trades actually out-earn the rest.
**Key behavior/trigger:** None — a read-only trade-history analyzer; no order or gating action.

## SneakyLogEngine_v1_0_0.cs
**Description:** A standalone, strategy-agnostic engine (a plain class, not an AddOnBase window) that records per-trade MAE/MFE excursion to JSONL in two tiers: Tier 1 (zero-touch, price-only, fed by an AddOn from account/market data) and Tier 2 (rich, fed by an instrumented strategy with per-bar context and ATR). Callers drive it with `OnEntry`/`OnBar`/`OnExit`; it also snapshots the `SneakyEye` verdict at entry so later analysis (Lens) can check whether Eye-endorsed trades perform better.
**Key behavior/trigger:** None — a passive logging/recording engine with no market-data subscriptions, no indicator/bar knowledge, and no trading action; it only writes JSONL records.

## SneakyLogService_v1_0_0.cs
**Description:** A headless, always-on AddOnBase singleton (no window of its own) providing "tier-1 zero-touch" MAE/MFE capture: it subscribes to every account's `PositionUpdate`/`ExecutionUpdate`, and whenever a position goes flat→open it starts a raw-tick market-data subscription for that instrument, tracks running MAE/MFE against the entry price via `SneakyLogEngine`, and finalizes/writes one JSONL record when the position goes open→flat.
**Key behavior/trigger:** None — purely an observational logger of existing positions/fills; it submits no orders and gates nothing.

## SneakyNewsService_v1_0_0.cs
**Description:** A headless AddOn that, on an internal timer, fetches the ForexFactory weekly high-impact economic calendar (no Python dependency) and writes the managed block of `Sentinel\News.conf` that `SneakyRiskService` reads to compute news blackout windows (`SetNewsLockouts`). It filters to configured currencies (default USD) at/above a minimum impact level, converts event times to local wall-clock time, and writes `before/after` minute buffers per event. Config (`Sentinel\NewsService.conf`): `enabled`, `minImpact`, `currencies`, `beforeMin`, `afterMin`, `refreshMinutes`, optional `url` override. Fail-safe: any fetch/parse error leaves the existing `News.conf` untouched and logs a warning rather than blanking the blackout data.
**Key behavior/trigger:** The service itself never blocks or places a trade — it only produces the data file; the actual news-based entry veto is enforced downstream by `SneakyRiskService`'s `CanEnter`/Council consultation of that file.

## SneakyRiskService_v1_0_0.cs
**Description:** A headless, always-on AddOnBase "safety" watchdog that monitors data-feed health (per-instrument tick lag and stall, auto-subscribing to instruments with an open position) and connection status, and registers a `FeedHealthProbe` other tools can consult per account. It also runs a naked/orphan-position sweep (debounced condition alerts when a position lacks a protective stop) and a news-calendar freshness guard (warns if `News.conf` is stale/missing so the news veto doesn't silently fail open) and a rollover countdown for key root instruments (default ES, GC, SI, CL).
**Key behavior/trigger:** With Auto-Kill enabled (default ON), if any monitored feed's lag exceeds `MaxLagSeconds` (default 5.0s) OR its stall exceeds `MaxStallSeconds` (default 15.0s), the service calls `SneakyCore.SetKillSwitch(true)`, halting every suite tool that trades/mirrors (e.g. the Copier). When feeds recover and `AutoReleaseWhenHealthy` is true, it releases the switch — but only a kill it itself engaged, never overriding a kill a human or another tool set. It will also re-assert the kill switch during a live breach even if a human manually clears it, by design (documented as "safety-first").

## SneakySkin.cs
**Description:** A shared SharpDX drawing/"flight-instrument" framework library (a `Painter` helper class), not an AddOnBase service, that every Sneaky on-chart indicator/strategy uses in `OnRender` to draw cohesive cards, text, dots, pills, gauges, sparklines and tracks so all suite tools visually match. It owns the DirectWrite factory and brush/text-format caching, plus a `CardLayout` system with hysteresis-based collapse/expand to avoid UI flicker when multiple cards compete for screen space.
**Key behavior/trigger:** None — purely a rendering/visual-styling utility library; no trading logic.

## SneakyStateService_v1_0_0.cs
**Description:** A headless, always-on AddOnBase service that writes a live JSON snapshot of NinjaTrader state (`<UserDataDir>\Sneaky\state.json`) every couple of seconds — accounts, positions, working orders, P&L, kill-switch state, copier config, risk/feed-health, Arc fleet status, Governor/account-profile blocks, per-instrument Eye verdicts, and GTrader21 config-use registry — so the platform's live picture is readable from outside NT without a screenshot. Written atomically (temp file then move) and kept compact by detailing only accounts with a position or working order.
**Key behavior/trigger:** None — a pure observability/state-dump service; it reads and republishes existing state but takes no trading action itself.

## SneakyTbarsSudoV3Config.cs
**Description:** Not an AddOnBase service at all, but a plain shared configuration/registry class (`SneakyTbarsSudoV3Config` + `SneakyTbarsSudoV3Registry`), installed to `bin\Custom\Shared\`, that defines the tunable parameters for the `SneakyTBars` custom bars type (confirmation thresholds like breakout ticks/ms/speed/volume, adaptive ATR multipliers, quiet-hours gating, and target bar density/scale settings). The bars type reads this registry once per session via `LatchConfig`, falling back to its own built-in defaults when no writer (an optional controller indicator) has populated it.
**Key behavior/trigger:** None — a configuration-value container with no market access, UI, or trading logic of its own.
