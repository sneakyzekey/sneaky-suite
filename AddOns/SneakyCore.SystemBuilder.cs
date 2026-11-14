// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
//
// Copyright (c) 2026 silentsudo-io and the Sentinel Suite contributors.
// ─────────────────────────────────────────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════
//  SneakyCore — SYSTEM BUILDER layer  (partial)
//  File: SneakyCore.SystemBuilder.cs   |   part of `static partial class SneakyCore`
// ─────────────────────────────────────────────────────────────────────────────
//  2026-08-06 — NEW FILE, ported from the real Sentinel Suite's own
//  SentinelCore.SystemBuilder.cs as part of rebasing SneakyCouncil onto the real
//  v1.11.0. Confirmed via a real NinjaTrader export (uploaded this session) that
//  SneakyCore.Foundation.cs already exists and is current in this environment —
//  SettingsDir, Log, Swallow, Beacon/BeaconForeign are ALL already there, already
//  correctly Sneaky-branded (BeaconSlot = "Sneaky.Seam.Beacon", sneaky.log). This
//  file adds ONLY what that export confirmed was genuinely missing: RosterIO and
//  LaneIO (the "System Builder" rung — Roster.conf / Lane.conf hot-reload config,
//  never part of this install before). Nothing here touches or duplicates
//  anything Foundation.cs already defines.
//
//  DELIBERATELY NOT PORTED (yet): VoterCatalog / SensorRole — the rebased
//  Council calls VoterCatalog.Export() exactly once, wrapped in its own
//  try/Swallow, so it fails silently without this and nothing else needs it.
//  Only the shared VoterKind enum below is ported, because RosterLine.Kind and
//  the rebased Council's own roster-resolution code both reference it directly.
//
//  DEPENDENCY: Foundation-layer only (SettingsDir, Log) — confirmed present.
//  Touches no seam, no Gate. Places no orders.
// ═════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NinjaTrader.NinjaScript.AddOns.Sneaky
{
    public static partial class SneakyCore
    {
        // ═════════════════════════════════════════════════════════════════════
        //  VOTER KIND — the canonical, shared classification (mirrors the
        //  Council's own PRIVATE enum of the same shape; the Council converts
        //  explicitly at the boundary rather than sharing the type directly —
        //  see its own ApplyRoster-equivalent code). STATE voters always carry
        //  a reading and always dilute conviction; a TRIGGER reads 0 ("nothing
        //  to report") most bars and dilutes only when it fired or is absent.
        // ═════════════════════════════════════════════════════════════════════
        public enum VoterKind { State, Trigger }

        // ═════════════════════════════════════════════════════════════════════
        //  ROSTER MODEL — the in-memory form of a Roster.conf.
        // ═════════════════════════════════════════════════════════════════════
        public sealed class RosterLine
        {
            public string     Tag;      // upper-cased voter tag
            public double?    Weight;   // w= override, or null (use the Council's F6/base weight)
            public VoterKind? Kind;     // state/trigger override, or null (use the Council's DefaultKind)
            public string     Comment;  // inline "# …" trailing comment, preserved on round-trip
        }

        public sealed class RosterDoc
        {
            public readonly List<RosterLine> Lines = new List<RosterLine>();
            public string Source;   // the winning file path, or null when no file declared any voter

            public bool HasDeclarations { get { return Lines.Count > 0; } }

            public RosterLine Find(string tag)
            {
                if (string.IsNullOrEmpty(tag)) return null;
                string t = tag.ToUpperInvariant();
                foreach (var l in Lines) if (l.Tag == t) return l;
                return null;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  ROSTER I/O — the single reader/writer.
        //  Cascade (most-specific wins):
        //      Sentinel\Models\<INST>\<bartag>\Roster.conf
        //    ▸ Sentinel\Models\<INST>\Roster.conf
        //    ▸ Sentinel\Models\Roster.conf
        //  Parse grammar (identical to Council_v1_0_0.ParseRoster):
        //      TAG [w=<val>] [state|trigger|kind=<val>]   # comment
        // ═════════════════════════════════════════════════════════════════════
        public static class RosterIO
        {
            private static string ModelsDir { get { return Path.Combine(SettingsDir, "Models"); } }

            /// <summary>The candidate paths, most-specific first, for a scope's roster.</summary>
            public static List<string> Candidates(string inst, string barTag)
            {
                var c = new List<string>();
                string models = ModelsDir;
                if (!string.IsNullOrEmpty(inst))
                {
                    if (!string.IsNullOrEmpty(barTag))
                    {
                        c.Add(Path.Combine(Path.Combine(Path.Combine(models, inst), barTag), "Roster.conf"));
                        // v1.33.0 — a LANED tag ("212202v6x24@A") inherits its BAR-TYPE baseline ("212202v6x24")
                        // before falling to the instrument default, so a fresh lane reads as the bar type's roster
                        // until you fork it (System Builder spec §14.2). Only adds a rung; PathFor (write) is unchanged.
                        int at = barTag.IndexOf('@');
                        if (at > 0)
                            c.Add(Path.Combine(Path.Combine(Path.Combine(models, inst), barTag.Substring(0, at)), "Roster.conf"));
                    }
                    c.Add(Path.Combine(Path.Combine(models, inst), "Roster.conf"));
                }
                c.Add(Path.Combine(models, "Roster.conf"));
                return c;
            }

            /// <summary>The most-specific path a Write() targets for this scope.</summary>
            public static string PathFor(string inst, string barTag)
            {
                string models = ModelsDir;
                if (!string.IsNullOrEmpty(inst) && !string.IsNullOrEmpty(barTag))
                    return Path.Combine(Path.Combine(Path.Combine(models, inst), barTag), "Roster.conf");
                if (!string.IsNullOrEmpty(inst))
                    return Path.Combine(Path.Combine(models, inst), "Roster.conf");
                return Path.Combine(models, "Roster.conf");
            }

            /// <summary>v1.39.0 — the winning path in the cascade WITHOUT parsing, so a caller can watch its write
            /// time cheaply (the Council's live config poll). Mirrors Read's rule exactly: first candidate that
            /// exists AND declares at least one voter wins. Null when nothing declares one.</summary>
            public static string Resolve(string inst, string barTag)
            {
                try
                {
                    foreach (string p in Candidates(inst, barTag))
                    {
                        if (!File.Exists(p)) continue;
                        if (Parse(File.ReadAllLines(p)).Count > 0) return p;
                    }
                }
                catch (Exception _sx) { SneakyCore.Swallow("SentinelSysBuilder.Resolve", _sx); }
                return null;
            }

            /// <summary>
            /// Resolve + parse the scope's roster. Returns a RosterDoc whose Source is the winning
            /// path (null + empty when no file declared any voter). NEVER throws — a broken/missing
            /// file yields an empty doc, exactly as the Council falls back to its default declaration.
            /// </summary>
            public static RosterDoc Read(string inst, string barTag)
            {
                var doc = new RosterDoc();
                try
                {
                    foreach (string p in Candidates(inst, barTag))
                    {
                        if (!File.Exists(p)) continue;
                        var parsed = Parse(File.ReadAllLines(p));
                        if (parsed.Count > 0)
                        {
                            doc.Lines.AddRange(parsed);
                            doc.Source = p;
                            break;
                        }
                    }
                }
                catch { doc.Lines.Clear(); doc.Source = null; }
                return doc;
            }

            /// <summary>Parse Roster.conf lines. Mirrors Council_v1_0_0.ParseRoster exactly (first tag wins on dup).</summary>
            public static List<RosterLine> Parse(string[] lines)
            {
                var outLines = new List<RosterLine>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (lines == null) return outLines;

                foreach (string raw in lines)
                {
                    if (raw == null) continue;
                    string line = raw;
                    string comment = null;
                    int hash = line.IndexOf('#');
                    if (hash >= 0) { comment = line.Substring(hash + 1).Trim(); line = line.Substring(0, hash); }
                    line = line.Trim();
                    if (line.Length == 0) continue;

                    string[] parts = line.Split(new[] { ' ', '\t', '=' }, StringSplitOptions.RemoveEmptyEntries);
                    string tag = parts[0].ToUpperInvariant();
                    if (seen.Contains(tag)) continue;
                    seen.Add(tag);

                    var rl = new RosterLine { Tag = tag, Comment = string.IsNullOrEmpty(comment) ? null : comment };
                    for (int i = 1; i < parts.Length; i++)
                    {
                        string p = parts[i];
                        if (p.Equals("w", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                        {
                            double val;
                            if (double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out val))
                                rl.Weight = val;
                            continue;
                        }
                        if (p.Equals("state", StringComparison.OrdinalIgnoreCase))        rl.Kind = VoterKind.State;
                        else if (p.Equals("trigger", StringComparison.OrdinalIgnoreCase)) rl.Kind = VoterKind.Trigger;
                        else if (p.Equals("kind", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                        {
                            string kv = parts[i + 1];
                            if (kv.Equals("state", StringComparison.OrdinalIgnoreCase))        rl.Kind = VoterKind.State;
                            else if (kv.Equals("trigger", StringComparison.OrdinalIgnoreCase)) rl.Kind = VoterKind.Trigger;
                        }
                    }
                    outLines.Add(rl);
                }
                return outLines;
            }

            /// <summary>Serialise a RosterDoc to a Roster.conf text body (the exact grammar Parse reads back).</summary>
            public static string Serialize(RosterDoc doc, string headerNote)
            {
                var sb = new StringBuilder();
                sb.Append("# Sentinel Roster — the Council's declared voter set for this scope.").Append('\n');
                sb.Append("# Grammar:  TAG [w=<weight>] [state|trigger]   # comment").Append('\n');
                sb.Append("#   w=0 = the exploration primitive (recorded, contributes nothing to the fusion).").Append('\n');
                if (!string.IsNullOrEmpty(headerNote))
                    sb.Append("# ").Append(headerNote).Append('\n');
                sb.Append("# ⚠ Regenerated by the Sentinel System Builder — standalone comment lines are not preserved.").Append('\n');
                sb.Append('\n');

                if (doc != null)
                {
                    foreach (var l in doc.Lines)
                    {
                        if (l == null || string.IsNullOrEmpty(l.Tag)) continue;
                        sb.Append(l.Tag);
                        if (l.Weight.HasValue)
                            sb.Append(" w=").Append(l.Weight.Value.ToString("0.####", CultureInfo.InvariantCulture));
                        if (l.Kind.HasValue)
                            sb.Append(l.Kind.Value == VoterKind.Trigger ? " trigger" : " state");
                        if (!string.IsNullOrEmpty(l.Comment))
                            sb.Append("   # ").Append(l.Comment);
                        sb.Append('\n');
                    }
                }
                return sb.ToString();
            }

            /// <summary>
            /// Write the scope's roster to its most-specific path, ATOMICALLY (temp + File.Replace),
            /// creating the Models\&lt;INST&gt;\&lt;bartag&gt; directory as needed. Returns the path written.
            /// Throws on I/O failure (the caller — a UI Save — surfaces it; nothing trades on this path).
            /// </summary>
            public static string Write(string inst, string barTag, RosterDoc doc, string headerNote)
            {
                string dest = PathFor(inst, barTag);
                string dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string body = Serialize(doc, headerNote);
                string tmp = dest + ".tmp";
                File.WriteAllText(tmp, body, new UTF8Encoding(false));   // no BOM — matches the Council's File.ReadAllLines
                if (File.Exists(dest)) File.Replace(tmp, dest, null);
                else                   File.Move(tmp, dest);

                try { SneakyCore.Log("RosterIO", "wrote " + dest + " (" + (doc != null ? doc.Lines.Count : 0) + " voters)"); } catch (Exception _sx) { SneakyCore.Swallow("SentinelSysBuilder.Write", _sx); }
                return dest;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  LANE PROFILE (Lane.conf) — the per-lane SYSTEM overrides BEYOND the roster (v1.33.0)
        //  A lane's ROSTER (Roster.conf) is voters+weights+kind. A lane's PROFILE (Lane.conf, beside it) is the
        //  Council FUSION knobs the roster doesn't hold — ConvictionFloor, bias deadband, the consult toggles, the
        //  modulator damps. SPARSE: only keys PRESENT override; an absent key inherits the chart's F6 value. So a
        //  lane file lists ONLY what the experiment changes, and adding a knob later never breaks an old file.
        //  Grammar: one `key = value` per line, '#' comment, keys case-insensitive. Path (most-specific ONLY — a
        //  profile is deliberately not cascaded, so "what overrides my chart" is answerable from exactly one file):
        //      Sentinel\Models\<inst>\<bartag>@<lane>\Lane.conf   (beside Roster.conf)
        //  System Builder spec §14.7.
        // ─────────────────────────────────────────────────────────────────────
        public static class LaneIO
        {
            private static string ModelsDir { get { return Path.Combine(SettingsDir, "Models"); } }

            /// <summary>The Lane.conf path for a scope. This is the WRITE path — always the most specific location.
            /// Readers use <see cref="Resolve"/>, which cascades. </summary>
            public static string PathFor(string inst, string barTag)
            {
                string models = ModelsDir;
                if (!string.IsNullOrEmpty(inst) && !string.IsNullOrEmpty(barTag))
                    return Path.Combine(Path.Combine(Path.Combine(models, inst), barTag), "Lane.conf");
                if (!string.IsNullOrEmpty(inst))
                    return Path.Combine(Path.Combine(models, inst), "Lane.conf");
                return Path.Combine(models, "Lane.conf");
            }

            /// <summary>v1.39.0 — CASCADE: scope ▸ instrument ▸ global, FIRST MATCH WINS (identical rule to RosterIO,
            /// so the two config files finally behave the same way). Returns null when no file exists anywhere.
            ///
            /// WHY THIS CHANGED. Lane.conf used to be most-specific-ONLY, which meant a chart on a bar type nobody had
            /// used before found no file, silently kept its F6 ConvictionFloor, and recorded almost nothing — looking
            /// exactly like every sensor being dead. That one asymmetry (Roster cascades, Lane does not) is what made
            /// "new chart ▸ pick a bar type ▸ load the template ▸ run" impossible: every new bar type needed a
            /// hand-made directory first. Now one Models\&lt;INST&gt;\Lane.conf covers every bar type for that
            /// instrument, and a per-bartype file still overrides it when a test needs one.</summary>
            public static string Resolve(string inst, string barTag)
            {
                try
                {
                    if (!string.IsNullOrEmpty(inst) && !string.IsNullOrEmpty(barTag))
                    {
                        string p = PathFor(inst, barTag);
                        if (File.Exists(p)) return p;
                    }
                    if (!string.IsNullOrEmpty(inst))
                    {
                        string p = PathFor(inst, null);
                        if (File.Exists(p)) return p;
                    }
                    string g = PathFor(null, null);
                    if (File.Exists(g)) return g;
                }
                catch (Exception _sx) { SneakyCore.Swallow("SentinelSysBuilder.Resolve", _sx); }
                return null;
            }

            /// <summary>Read the lane's sparse override map (case-insensitive keys). Empty (never null) if no file
            /// exists anywhere in the cascade. Never throws.</summary>
            public static Dictionary<string,string> Read(string inst, string barTag)
            {
                var map = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string p = Resolve(inst, barTag);
                    if (p == null) return map;
                    foreach (var raw in File.ReadAllLines(p))
                    {
                        string line = raw ?? "";
                        int h = line.IndexOf('#'); if (h >= 0) line = line.Substring(0, h);
                        line = line.Trim();
                        if (line.Length == 0) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim();
                        string v = line.Substring(eq + 1).Trim();
                        if (k.Length > 0) map[k] = v;
                    }
                }
                catch (Exception _sx) { SneakyCore.Swallow("SentinelSysBuilder.Read", _sx); }
                return map;
            }

            /// <summary>Atomic write of the sparse override map (BOM-less UTF-8). Blank/null map still writes a header stub.</summary>
            public static string Write(string inst, string barTag, Dictionary<string,string> keys, string headerNote)
            {
                string dest = PathFor(inst, barTag);
                try
                {
                    string dir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    var sb = new StringBuilder();
                    sb.Append("# Sentinel Lane profile");
                    if (!string.IsNullOrEmpty(headerNote)) sb.Append(" — ").Append(headerNote);
                    sb.Append('\n');
                    sb.Append("# SPARSE overrides of the Council's F6 fusion knobs; absent keys inherit the chart's setting.\n");
                    if (keys != null)
                        foreach (var kv in keys)
                            if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null)
                                sb.Append(kv.Key).Append(" = ").Append(kv.Value).Append('\n');
                    string tmp = dest + ".tmp";
                    File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                    if (File.Exists(dest)) File.Replace(tmp, dest, null);
                    else                   File.Move(tmp, dest);
                    try { SneakyCore.Log("LaneIO", "wrote " + dest + " (" + (keys != null ? keys.Count : 0) + " overrides)"); } catch (Exception _sx) { SneakyCore.Swallow("SentinelSysBuilder.Write", _sx); }
                }
                catch (Exception _sx) { SneakyCore.Swallow("SentinelSysBuilder.Write", _sx); }
                return dest;
            }

            /// <summary>Parse a present key as a double (InvariantCulture). False (leaves val 0) when absent/unparseable — the
            /// caller then leaves its property at the F6 value. This is how "absent ⇒ inherit" is enforced at the seam.</summary>
            public static bool TryDouble(Dictionary<string,string> m, string key, out double val)
            {
                val = 0; string s;
                if (m == null || !m.TryGetValue(key, out s)) return false;
                return double.TryParse(s, System.Globalization.NumberStyles.Any,
                                       System.Globalization.CultureInfo.InvariantCulture, out val);
            }

            /// <summary>Parse a present key as a bool (true/1/yes/on · false/0/no/off). False when absent/unparseable.</summary>
            public static bool TryBool(Dictionary<string,string> m, string key, out bool val)
            {
                val = false; string s;
                if (m == null || !m.TryGetValue(key, out s)) return false;
                s = s.Trim().ToLowerInvariant();
                if (s == "true" || s == "1" || s == "yes" || s == "on")  { val = true;  return true; }
                if (s == "false" || s == "0" || s == "no" || s == "off") { val = false; return true; }
                return false;
            }
        }

        public static class LaneAssign
        {
            /// <summary>The single assignment file. One flat file, NOT under Models\ — the Models
            /// path already encodes the lane, so a lane-assignment map cannot live there.</summary>
            public static string PathFor() { return Path.Combine(SettingsDir, "Lanes.conf"); }

            /// <summary>Read the assignment map (case-insensitive keys). Empty, never null. Never throws.</summary>
            public static Dictionary<string,string> Read()
            {
                var map = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string p = PathFor();
                    if (!File.Exists(p)) return map;
                    foreach (string raw in File.ReadAllLines(p))
                    {
                        string line = raw ?? "";
                        int h = line.IndexOf('#'); if (h >= 0) line = line.Substring(0, h);
                        line = line.Trim();
                        if (line.Length == 0) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim();
                        string v = line.Substring(eq + 1).Trim();
                        if (k.Length > 0) map[k] = v;
                    }
                }
                catch (Exception _sx) { SneakyCore.Swallow("LaneAssign.Read", _sx); }
                return map;
            }

            /// <summary>Set ONE key's assignment, preserving every other line and any comments.
            /// Blank/null lane REMOVES the key. Returns false on IO failure (never throws).
            /// This is the Conductor's writer — it is what makes a job line's `lane=` real.</summary>
            public static bool Set(string key, string lane, string note)
            {
                if (string.IsNullOrEmpty(key)) return false;
                try
                {
                    string p = PathFor();
                    var kept = new List<string>();
                    if (File.Exists(p))
                        foreach (string raw in File.ReadAllLines(p))
                        {
                            string probe = raw ?? "";
                            int h = probe.IndexOf('#'); if (h >= 0) probe = probe.Substring(0, h);
                            int eq = probe.IndexOf('=');
                            // drop only the line that assigns THIS key; keep comments and everything else
                            if (eq > 0 && string.Equals(probe.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                                continue;
                            kept.Add(raw);
                        }

                    string clean = SanitizeLane(lane);
                    if (!string.IsNullOrEmpty(clean))
                        kept.Add(key + " = " + clean + (string.IsNullOrEmpty(note) ? "" : "   # " + note));

                    var sb = new StringBuilder();
                    sb.AppendLine("# Sentinel\\Lanes.conf — chart LANE ASSIGNMENT (SneakyCore.LaneAssign).");
                    sb.AppendLine("# Overrides the Council's F6 'Scope Lane'. Cascade: <inst>.<barTag> then <inst>.");
                    sb.AppendLine("# Written by the Conductor at job start; safe to hand-edit. ASCII only.");
                    bool anyBody = false;
                    foreach (string l in kept)
                    {
                        if (l != null && l.TrimStart().StartsWith("# Sentinel\\Lanes.conf")) continue;
                        if (l != null && l.TrimStart().StartsWith("# Overrides the Council")) continue;
                        if (l != null && l.TrimStart().StartsWith("# Written by the Conductor")) continue;
                        sb.AppendLine(l); anyBody = true;
                    }
                    if (!anyBody) sb.AppendLine();
                    File.WriteAllText(p, sb.ToString(), new UTF8Encoding(false));
                    return true;
                }
                catch (Exception _sx) { SneakyCore.Swallow("LaneAssign.Set", _sx); return false; }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  VOTER CATALOG — 2026-08-08, added for Cockpit's "unburied voters" section.
        //  NOT a mechanical rename of the real upstream's own catalog: that list is
        //  hardcoded to THEIR class names and THEIR default weights (e.g. WoodiesCCIPro_
        //  v1_0_0, WAE=0.7), neither of which match this build. Every entry below was
        //  cross-checked against this Council's ACTUAL current property defaults (the
        //  real_weights pass, ARCH/TFUS additions, etc.) and this build's ACTUAL class
        //  names (same mapping verified for the off-chart gate earlier), not copied from
        //  upstream. TFUS is a genuinely new entry — Sneaky-specific, no upstream
        //  equivalent. CVD is deliberately OMITTED as a voter: it's not wired into this
        //  Council at all (feeds Eye only, on purpose, to avoid the double-count problem
        //  Eye's v1.1.0 rebuild fixed) — listing it here would misrepresent it as active.
        // ═════════════════════════════════════════════════════════════════════
        public enum SensorRole
        {
            Voter,      // has a Council weight
            Modulator,  // consulted to damp/scale conviction (Clock / Participation / MTF)
            Veto,       // can zero conviction (LiquidityWalls absorption / Location level-in-path)
        }

        public sealed class CatalogEntry
        {
            public string     Tag;
            public string     Display;
            public string     TypeName;
            public SensorRole Role;
            public VoterKind  DefaultKind;
            public double     DefaultWeight;
            public string     Seam;
            public string     Notes;

            public bool IsVoter { get { return Role == SensorRole.Voter; } }
        }

        public static class VoterCatalog
        {
            private static readonly List<CatalogEntry> _all = new List<CatalogEntry>
            {
                V("EYE",  "Eye",            "SneakyEye_v1_1_0",                    VoterKind.Trigger, 0.0, "EyeVerdict",       "CVD-DIR/CVD-DIVERG qualifier (rebuilt v1.1.0). Off by default (IncludeEye)."),
                V("TRND", "Trend",          "SneakyTrend_v1_0_0",                  VoterKind.State,   1.0, "TrendState",       "Structural trailing-line trend."),
                V("CCI",  "Woodies CCI",    "SneakyWoodiesCCIPro_v1_0_0",          VoterKind.State,   0.8, "CciState",         "Woodies CCI trend bias."),
                V("ADX",  "ADX Pro",        "SneakyADXPro_v1_2_0",                 VoterKind.State,   0.6, "AdxState",         "Regime/strength confirmer."),
                V("ENV",  "Vol Envelope",   "SneakyVolEnvelope_v0_2_0",            VoterKind.State,   0.6, "EnvelopeState",    "Trend regime; also drives the squeeze modulator."),
                V("BRK",  "Brick",          "SneakyTBars (bar type)",              VoterKind.State,   0.5, "BrickState",       "Adaptive brick micro-trend."),
                V("CMP",  "Compression",    "SneakyCompressionBase_v1_3_0",        VoterKind.Trigger, 0.7, "CompressionState", "Held breakout direction off a compression base."),
                V("IMKT", "Intermarket",    "SneakyIntermarket_v1_0_0",            VoterKind.State,   0.6, "IntermarketState", "Correlated-instrument macro lean."),
                V("WAE",  "WAE",            "SneakyWAE_v2_0_0",                    VoterKind.Trigger, 0.2, "WaeState",         "Momentum-explosion breakout. Lowered from 0.7 -- negative standalone edge, heavy TRND/WAE redundancy."),
                V("GREV", "God Reversal",   "SneakyGodReversal_v1_0_0",            VoterKind.Trigger, 0.9, "GodReversalState", "Candle-grammar reversal -- mean-reversion voice."),
                V("STF",  "Stoch Filter",   "SneakyStochasticTripleFilter_v1_0_0", VoterKind.State,   0.0, "StfState",         "Gaussian-Channel midline slope. Also drives the CHOP veto."),
                V("FLOW", "Flow",           "SneakyFlow_v1_0_0",                   VoterKind.State,   0.9, "FlowState",        "Tick-rule CVD regime."),
                V("STRC", "Structure",      "SneakyStructure_v1_0_0",              VoterKind.State,   0.7, "StructureState",   "Swing HH/HL - LH/LL market structure."),
                V("EXH",  "Exhaustion",     "SneakyExhaustion_v1_0_0",             VoterKind.Trigger, 0.5, "ExhaustionState",  "Leledc reversal -- mean-reversion voice."),
                V("AVMA", "ADXVMA",         "SneakyADXVMA_v1_0_0",                 VoterKind.State,   0.6, "AdxvmaState",      "ADX-volatility adaptive-MA trend."),
                V("SPRT", "SuperTrend",     "SneakySuperTrend_v1_0_0",             VoterKind.State,   0.7, "SuperTrendState",  "ATR-band trailing-flip trend."),
                V("PSAR", "Parabolic SAR",  "SneakyParabolicSAR_v1_0_0",           VoterKind.State,   0.5, "SarState",         "Wilder Parabolic SAR trend/stop."),
                V("ZSC",  "Z-Score",        "SneakyZScore_v1_0_0",                 VoterKind.Trigger, 0.2, "ZScoreState",      "Mean-reversion fade voice. Lowered from 0.4 -- negative standalone edge, heavy redundancy."),
                V("ARCH", "Trend Architect","SneakyTrendArchitect_v1_0_0",         VoterKind.State,   0.0, "TrendArchitectState","The REAL ported PRISM (2026-08-07). Audition: brand new to this Council's grading history."),
                V("VDYA", "VIDYA",          "SneakyVIDYA_v1_0_0",                  VoterKind.State,   0.5, "VidyaState",       "Chande-CMO-modulated adaptive-MA trend."),
                V("HARM", "Harmonic",       "SneakyHarmonic_v1_0_0",               VoterKind.Trigger, 0.4, "HarmonicState",    "Harmonic XABCD pattern completions -- reversal voice."),
                V("FLUX", "Flux",           "SneakyFlux (bar type)",               VoterKind.State,   0.7, "FluxState",        "Net order-flow direction of the imbalance-driven bar close."),
                V("CVB",  "Conviction Bias","SneakyDrift (bar type)",              VoterKind.State,   0.6, "ConvictionState",  "Flow-confirmed structural brick direction. Coverage currently 0% -- SneakyDrift not attached anywhere yet."),
                V("TFUS", "Trend Fusion",   "SneakyTrendFusion_v1_0_0",            VoterKind.State,   0.0, "TrendFusionState", "2026-08-06 -- Sneaky-specific, no upstream equivalent. MFI/CCI/CVD/Hurst/KAMA majority vote, honest ARCH substitute, retired to audition now that real ARCH exists."),
                V("PRS",  "Buy/Sell Pressure","SneakyBuySellVolumePressureMountain_v1_0_0", VoterKind.State, 0.0, "PressureState", "Classified buy-vs-sell volume share."),

                C("CLOCK",  "Clock",         "SneakyClock_v1_0_0",         SensorRole.Modulator, "ClockState",        "Session phase / mins-to-close / kill window."),
                C("PARTIC", "Participation", "SneakyParticipation_v1_0_0", SensorRole.Modulator, "ParticipationState","Time-normalised RVOL + climax/dry-up."),
                C("MTF",    "MTF",           "SneakyMtf_v1_0_0",           SensorRole.Modulator, "MtfState",          "Higher-timeframe consensus ladder."),
                C("LOC",    "Location",      "SneakyLocation_v1_0_0",      SensorRole.Veto,       "LevelState",        "VWAP/PDH-PDL/OR/IB structural levels."),
                C("LIQ",    "Liquidity",     "SneakyLiquidityWalls_v1_0_0",SensorRole.Veto,       "LiquidityState",    "Order-flow absorption walls."),
            };

            private static CatalogEntry V(string tag, string disp, string type, VoterKind kind, double w, string seam, string notes)
            { return new CatalogEntry { Tag = tag, Display = disp, TypeName = type, Role = SensorRole.Voter, DefaultKind = kind, DefaultWeight = w, Seam = seam, Notes = notes }; }

            private static CatalogEntry C(string tag, string disp, string type, SensorRole role, string seam, string notes)
            { return new CatalogEntry { Tag = tag, Display = disp, TypeName = type, Role = role, DefaultKind = VoterKind.State, DefaultWeight = 0.0, Seam = seam, Notes = notes }; }

            public static IList<CatalogEntry> All { get { return _all.AsReadOnly(); } }

            public static IEnumerable<CatalogEntry> Voters
            { get { foreach (var e in _all) if (e.Role == SensorRole.Voter) yield return e; } }

            public static IEnumerable<CatalogEntry> Context
            { get { foreach (var e in _all) if (e.Role != SensorRole.Voter) yield return e; } }

            public static CatalogEntry ByTag(string tag)
            {
                if (string.IsNullOrEmpty(tag)) return null;
                string t = tag.ToUpperInvariant();
                foreach (var e in _all) if (e.Tag == t) return e;
                return null;
            }
        }

    }
}
