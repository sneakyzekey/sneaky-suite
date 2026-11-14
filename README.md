# Sneaky Suite

A collection of original NinjaTrader 8 indicators, strategies, and add-ons —
built and maintained by [sneakyzekey](https://github.com/sneakyzekey).

This repo covers order flow and volume tools, moving-average/filter families,
market-structure and liquidity tools, automated strategies, and the add-on
services (risk, logging, alerts, dashboards, HUDs) that tie them together on
a NinjaTrader 8 chart.

## Screenshots

| | |
|---|---|
| **SneakyTradeHUD** — on-chart AUTO TRADE HUD<br>![SneakyTradeHUD](screenshots/SneakyTradeHUD.png) | **SneakyTradeHUD + SuperTrend Cluster**<br>![SneakyTradeHUD + SuperTrendCluster](screenshots/SneakyTradeHUD_SuperTrendCluster.png) |
| **SneakyTradeHUD + RSI Strength Zones**<br>![SneakyTradeHUD + RsiStrengthZones](screenshots/SneakyTradeHUD_RsiStrengthZones.png) | **GexLevels** — gamma/GEX levels panel<br>![GexLevels](screenshots/GexLevels.png) |
| **SneakyADXPro + Council** — ADX panel + Council vote panel<br>![SneakyADXPro + Council](screenshots/SneakyADXPro_Council.png) | **SneakyMarketStructure** — BMS/CHoCH structure table<br>![SneakyMarketStructure](screenshots/SneakyMarketStructure.png) |
| **SneakyInsaneToOsc** — oscillator<br>![SneakyInsaneToOsc](screenshots/SneakyInsaneToOsc.png) | **SneakyRegimeSwitch** — regime-aware strategy<br>![SneakyRegimeSwitch](screenshots/SneakyRegimeSwitch.png) |
| **SneakyWix9ineRanges**<br>![SneakyWix9ineRanges](screenshots/SneakyWix9ineRanges.png) | **SneakyEquityGate** — SIM-to-LIVE controller<br>![SneakyEquityGate](screenshots/SneakyEquityGate.png) |
| **LinRegUTBotReversal + Sneaky Order Flow Dashboard**<br>![LinRegUTBotReversal + OrderFlowDashboard](screenshots/LinRegUTBotReversal_OrderFlowDashboard.png) | **PraedoxSentinel + TradeProtector**<br>![PraedoxSentinel + TradeProtector](screenshots/PraedoxSentinel_TradeProtector.png) |
| **GodTradesStrategy**<br>![GodTradesStrategy](screenshots/GodTradesStrategy.png) | **TurtleTrendBreakout**<br>![TurtleTrendBreakout](screenshots/TurtleTrendBreakout.png) |
| **TONebula RealFlow**<br>![TONebula RealFlow](screenshots/TONebula_RealFlow.png) | **SneakyLevelsLikelyToBreak**<br>![SneakyLevelsLikelyToBreak](screenshots/SneakyLevelsLikelyToBreak.png) |
| **SneakyReversion** — Bollinger-based reversion<br>![SneakyReversion](screenshots/SneakyReversion.png) | **SneakyIctUnicorn**<br>![SneakyIctUnicorn](screenshots/SneakyIctUnicorn.png) |
| **SneakyLiquiditySfpMss** — liquidity map panel<br>![SneakyLiquiditySfpMss](screenshots/SneakyLiquiditySfpMss.png) | **SneakyAlphaForgeFlow**<br>![SneakyAlphaForgeFlow](screenshots/SneakyAlphaForgeFlow.png) |
| **SneakyVolumeImbalancePro**<br>![SneakyVolumeImbalancePro](screenshots/SneakyVolumeImbalancePro.png) | **SneakyVolumeProfilePro**<br>![SneakyVolumeProfilePro](screenshots/SneakyVolumeProfilePro.png) |
| **SneakyFusion17** — God Mode X Fusion17 panel<br>![SneakyFusion17](screenshots/SneakyFusion17.png) | **SneakyCRT** — Candle Range Theory scanner<br>![SneakyCRT](screenshots/SneakyCRT.png) |
| **SneakyDashBridge** — local trading dashboard<br>![SneakyDashBridge local dashboard](screenshots/SneakyDashBridge_LocalDashboard.png) | **SneakyAOBLE** — order blocks / liquidity sweeps<br>![SneakyAOBLE](screenshots/SneakyAOBLE.png) |
| **SneakyAOBLE panel** (with HUD)<br>![SneakyAOBLE panel](screenshots/SneakyAOBLE_panel.png) | **SneakyKineticPanel + SneakyGexRadar**<br>![SneakyKineticPanel + GexRadar](screenshots/SneakyKineticPanel_GexRadar.png) |
| **SneakyCRTAlpha** — CRT dashboard<br>![SneakyCRTAlpha](screenshots/SneakyCRTAlpha.png) | **SneakyGexRadarStrategy**<br>![SneakyGexRadarStrategy](screenshots/SneakyGexRadarStrategy.png) |
| **SneakyTradeHUD** — order management panel<br>![SneakyTradeHUD order panel](screenshots/SneakyTradeHUD_OrderPanel.png) | |

## Contents

- **`Indicators/`** — 124 chart indicators
- **`Strategies/`** — 20 automated strategies
- **`AddOns/`** — 21 NinjaTrader AddOn services (risk/logging/alerts/dashboard/HUD infrastructure)

### Indicators

| File | Notes |
|---|---|
| `SneakyTradeHUD.cs` | On-chart HUD with AUTO TRADE (multi-condition OR-combined long/short slots) and optional Chart Trader docking |
| `SneakyCore_v1_0_0.cs` family (`SneakyCore.Foundation.cs`, `SneakyCore.Safety.cs`, `SneakyCore.SystemBuilder.cs`) | Shared core framework used across the suite |
| `SneakyADXPro_v1_2_0.cs`, `SneakyADXVMA_v1_0_0.cs` | ADX-based trend tools |
| `SneakyDEMA/DSMA/DTMA/DWMA/EMA/HMA/LLMA/RWMA/SMA/SWMA/TEMA/TMA/TSMA/TWMA/VWMA/WMA/ZLEMA_v1_0_0.cs` | Moving-average family (one file per MA type) |
| `SneakyAdaptiveLaguerreFilter`, `SneakyButterworthFilter`, `SneakyCoralFilter`, `SneakyEhlersFilter`, `SneakyGaussianFilter`, `SneakyLaguerreFilter`, `SneakySuperSmootherFilter_v1_0_0.cs` | Digital/adaptive smoothing filter family |
| `SneakyCVD_v1_1_0.cs`, `SneakyBuySellVolumePressureMountain_v1_0_0.cs`, `SneakyBuySellXL.cs`, `SneakyBuyersSellersProfile.cs`, `SneakyVolumeImbalancePro.cs`, `SneakyVolumeProfilePro.cs`, `SneakyFootprintPSMCVWAP.cs`, `SneakyOrderFlowDashboard_v1_0_0.cs`, `SneakyParticipation_v1_0_0.cs` | Order flow / volume / delta tools |
| `SneakyIctMarketStructure.cs`, `SneakyIctUnicorn.cs`, `SneakyLiquiditySfpMss.cs`, `SneakyLiquidityWalls_v1_0_0.cs`, `SneakyStructure_v1_0_0.cs`, `SneakyDOL.cs`, `SneakyCRT.cs`, `SneakyCRTAlpha.cs` | Market-structure / liquidity / ICT-style tools |
| `SneakyGexProfile.cs`, `SneakyGexRadar.cs`, `GexContextScore.cs`, `GexLevels_v1_0_0.cs`, `GexReplayWindow.cs` | Gamma-exposure (GEX) tools |
| `TONebula.cs`, `TONebula_RealFlow_v1_0_0.cs` | Nebula flow indicators |
| `TurtleTrendBreakoutSignal_v1_0_0.cs` | Turtle-style breakout signal |
| `PraedoxFibEngine_v0_1_0.cs`, `PraedoxSignalEngine_v0_1_0.cs`, `PraedoxVolatilityShield_v0_1_0.cs` | Praedox family indicators |
| Remaining `Sneaky*.cs` files | Supporting signals, oscillators, panels, recorders, and chart utilities — see each file's in-code `Description` attribute for details |

### Strategies

| File | Notes |
|---|---|
| `SneakyGexRadarStrategy.cs` | GEX-radar-driven automated strategy |
| `SneakyFtkaTrendFollower.cs` | Trend-following strategy |
| `SneakyRegimeSwitch_v1_0_0.cs` | Regime-aware strategy switcher |
| `SneakyTrendStrategy_v1_0_0.cs` | Core trend strategy |
| `SneakyBridge_v0_2_0.cs` | Bridge/integration strategy |
| `SneakyKeel_v0_1_0.cs`, `SneakyTBarsEdgeProbe_v1_0_0.cs`, `SneakyWix9ineRangesStrategy.cs`, `SneakyLivewirePropBusterStrategy.cs`, `SneakySecretMindsetStrategy.cs` | Additional automated strategies |
| `DrummondCAPBreakout_v1_0_0.cs`, `DrummondEnvelopeSqueeze_v1_0_0.cs`, `DrummondPLDotPullback_v1_0_0.cs`, `DrummondTripleConfluence_v1_0_0.cs` | Drummond Geometry strategy family |
| `GodTradesStrategy_v1_0_0.cs` | Automated strategy |
| `LinRegUTBotReversalStrategy.cs` | Linear-regression / UT Bot reversal strategy |
| `StraddleBreakoutOCO_v1_0_0.cs` | OCO straddle-breakout strategy |
| `TurtleTrendBreakout_v1_1_0.cs` | Turtle-style trend breakout strategy |
| `PraedoxSentinel_v0_1_0.cs`, `PraedoxTradeProtector_v0_1_0.cs` | Praedox family strategies |

### AddOns

| File | Notes |
|---|---|
| `SneakyCore_v1_0_0.cs`, `SneakyCore.Foundation.cs`, `SneakyCore.Safety.cs`, `SneakyCore.SystemBuilder.cs` | Shared core service framework |
| `SneakyRiskService_v1_0_0.cs`, `SneakyEquityGate_v1_0_0.cs` | Risk management services |
| `SneakyAlertService_v1_0_0.cs`, `SneakyNewsService_v1_0_0.cs` | Alerts / news services |
| `SneakyLogService_v1_0_0.cs`, `SneakyLogEngine_v1_0_0.cs`, `SneakyStateService_v1_0_0.cs` | Logging / state tracking services |
| `SneakyDashboard_v1_0_0.cs`, `SneakyDashBridge.cs`, `SneakyCockpit_v0_1_0.cs`, `SneakyLens_v1_1_0.cs` | Dashboard / control-panel UI |
| `SneakyCopierService_v0_1_0.cs` | Trade-copier service |
| `SneakyArcService_v0_1_0.cs`, `SneakyBinds_v0_1_0.cs`, `SneakyExcursions_v1_0.cs`, `SneakySkin.cs`, `SneakyTbarsSudoV3Config.cs` | Supporting services and UI theming |

## Installation (NinjaTrader 8)

1. In NinjaTrader 8, go to **Tools → Import → NinjaScript Add-On...** for `.zip` imports, or copy the `.cs` files directly:
   - `Indicators/*.cs` → `Documents\NinjaTrader 8\bin\Custom\Indicators\`
   - `Strategies/*.cs` → `Documents\NinjaTrader 8\bin\Custom\Strategies\`
   - `AddOns/*.cs` → `Documents\NinjaTrader 8\bin\Custom\AddOns\`
2. Open NinjaScript Editor (**New → NinjaScript Editor**) and compile (**F5**), or just restart NinjaTrader — it compiles custom scripts on startup.
3. Several items depend on the shared `SneakyCore` files in `AddOns/` — install the full `AddOns/` folder alongside any `Sneaky*` indicator or strategy that references it, rather than individual files in isolation.

## Disclaimer

This code is provided as-is for the author's own use and reference. Nothing
here is financial advice, and nothing in this repo guarantees trading
performance. Trading futures, options, and other leveraged instruments
carries substantial risk of loss. Use at your own risk, and test thoroughly
in a sim/demo environment before using any of this with real money.

## License

See [LICENSE](LICENSE). All rights reserved — this is not open-source
software; see the license file for what is and isn't permitted.
