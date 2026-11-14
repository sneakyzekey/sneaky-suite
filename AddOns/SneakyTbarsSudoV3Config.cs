// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
//
// Copyright (c) 2026 silentsudo-io and the Sentinel Suite contributors.
// ─────────────────────────────────────────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════
//  SneakyTbarsSudoV3Config / SneakyTbarsSudoV3Registry — shared tuning surface for SneakyTBars
//  File: SneakyTbarsSudoV3Config.cs   ·   Install to: bin\Custom\Shared\
// ─────────────────────────────────────────────────────────────────────────────
//  WHAT THIS IS
//    A compile-time dependency of SneakyTBars_v1_0_0.cs (and the frozen
//    TbarsSudoV0003 bars type). SneakyTBars READS this registry once per session
//    (LatchConfig → SneakyTbarsSudoV3Registry.TryGetForInstrument) as an OPTIONAL
//    live-tuning surface; when the registry is empty it falls back to its own F6
//    defaults, so the bars type works with or without a writer present.
//    The writer (TbarsSudoV3Controller, an optional indicator) is not required.
// ═════════════════════════════════════════════════════════════════════════════
using System;
using System.Collections.Concurrent;

namespace NinjaTrader.NinjaScript
{
    // Tunable settings V3 (controller -> bars type)
    public class SneakyTbarsSudoV3Config
    {
        // Confirmation
        public bool   UseBreakoutConfirmation { get; set; }
        public int    ConfirmTicksBeyond { get; set; }
        public int    ConfirmMilliseconds { get; set; }
        public double MinSpeedTicksPerSecond { get; set; }
        public double MaxWickGivebackRatio { get; set; }
        public long   MinVolumeInWindow { get; set; }

        // Adaptive
        public int    AtrLength { get; set; }
        public double AtrMultTrend { get; set; }
        public double AtrMultReversal { get; set; }
        public int    ConfirmTrendBricks { get; set; }
        public double HysteresisReversalMult { get; set; }

        // Quiet hours
        public bool   EnableQuietHoursGating { get; set; }
        public int    QuietStartHour { get; set; }
        public int    QuietEndHour { get; set; }
        public double QuietTicksAdd { get; set; }
        public double QuietMsMult { get; set; }
        public double QuietSpeedMult { get; set; }

        // Density / timing
        public int    TargetBarsPerSession { get; set; }
        public double AssumedSessionHours { get; set; }
        public double MinScale { get; set; }
        public double MaxScale { get; set; }
        public double ScaleSmoothing { get; set; }
        public int    ForceStagnationSeconds { get; set; }
        public int    MinBarLifeSeconds { get; set; }
        public double MicroSplitRatio { get; set; }
        public bool   EnableMicroSplit { get; set; }

        // Speed settings
        public int    SpeedBase { get; set; }
        public int    SpeedTrend { get; set; }
        public int    SpeedReversal { get; set; }
    }

    // Thread-safe registry keyed by instrument + scope for TbarsSudoV0003
    public static class SneakyTbarsSudoV3Registry
    {
        private static readonly ConcurrentDictionary<string, SneakyTbarsSudoV3Config> store =
            new ConcurrentDictionary<string, SneakyTbarsSudoV3Config>(StringComparer.OrdinalIgnoreCase);

        private const string BarsTypeName = "TbarsSudoV0003";

        public static string BuildKey(string instrumentFullName, string scopeId)
        {
            var scope = string.IsNullOrWhiteSpace(scopeId) ? "default" : scopeId.Trim();
            var inst  = string.IsNullOrWhiteSpace(instrumentFullName) ? "UnknownInstrument" : instrumentFullName;
            return $"{inst}|{scope}|{BarsTypeName}";
        }

        public static string BuildDefaultKey(string instrumentFullName) => BuildKey(instrumentFullName, "default");

        public static void Set(string key, SneakyTbarsSudoV3Config cfg)
        {
            if (!string.IsNullOrWhiteSpace(key) && cfg != null)
                store[key] = cfg;
        }

        public static void SetForInstrument(string instrumentFullName, string scopeId, SneakyTbarsSudoV3Config cfg)
        {
            if (cfg == null) return;
            Set(BuildDefaultKey(instrumentFullName), cfg);
            Set(BuildKey(instrumentFullName, scopeId), cfg);
        }

        public static bool TryGetForInstrument(string instrumentFullName, out SneakyTbarsSudoV3Config cfg, string scopeId = null)
        {
            cfg = null;
            if (string.IsNullOrWhiteSpace(instrumentFullName)) return false;

            // BUGFIX (2026-08-22, P2 finding: TryGetForInstrument scope): this always read
            // BuildDefaultKey(...) regardless of scope, so it could never see the scope-specific
            // entry SetForInstrument writes via BuildKey(instrumentFullName, scopeId) — differently
            // scoped writers for the same instrument would silently share/clobber the one default
            // entry. Look up the same scope-specific key SetForInstrument uses first (BuildKey
            // already collapses a null/blank scopeId to "default", matching prior behavior when no
            // scope is given), then fall back to the shared per-instrument default bucket if no
            // scoped entry exists yet.
            if (store.TryGetValue(BuildKey(instrumentFullName, scopeId), out cfg))
                return true;

            return store.TryGetValue(BuildDefaultKey(instrumentFullName), out cfg);
        }
    }
}
