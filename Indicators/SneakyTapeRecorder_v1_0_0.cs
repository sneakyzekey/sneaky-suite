// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
//
// Copyright (c) 2026 silentsudo-io and the Sentinel Suite contributors.
// ─────────────────────────────────────────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════
//  SneakyTapeRecorder — capture a REAL order book, live, because the replay store has none
//  File: SneakyTapeRecorder_v1_0_0.cs   ·   Version v1.0.0   ·   Schema = gbNRDtoCSV L1/L2
//  namespace …Indicators.Sneaky
// ─────────────────────────────────────────────────────────────────────────────
//  WHAT THIS IS (ported from main 2026-08-17)
//    Execution research needs a book: the true touch, the size resting on it, and where you would
//    sit in the queue. NinjaTrader's MARKET REPLAY store cannot supply one — main measured this
//    directly (gbNRDtoCSV export of GC 08-26, memory `replay-depth-not-fit-for-execution`):
//      • 0.00% of 103,157 trades printed INSIDE the spread — structurally impossible in a real book
//        when three empty price levels sit between bid and ask;
//      • median size at the touch was 1 CONTRACT (real front-month GC holds dozens);
//      • the spread was 4-5 ticks and stayed 5 ticks in QUIET moments (530,310 samples, no trade for
//        >5 s), so staleness and row-ordering cannot explain it;
//      • the L1 Bid/Ask latch and the fully reconstructed L2 ladder agree exactly (20.1% of trades
//        outside the book either way) ⇒ both encodings share one degraded source.
//    The reader was not the problem — the DATA is the problem. Real-time depth is genuine; it is
//    only the recorded replay store that is degraded. So the fix is to capture the live tape
//    ourselves, because every day not recording is a day of book data that cannot be bought back.
//
//  ⚠ DELIBERATE: THIS ONE **IS** REALTIME-GATED — the opposite of SneakyBarDump
//    BarDump deliberately records historical bars, because a bar transcript contains nothing
//    forward-looking and the rebuild is exactly what its gate needs. The reasoning inverts here.
//    Historical/replay depth IS the degraded data this tool exists to escape; recording it would
//    re-poison the well with the very artifact main spent a day proving is unusable. Nothing is
//    written until `State == State.Realtime`, and the card says WAITING until then.
//
//  FORMAT — byte-identical to gbNRDtoCSV, on purpose (unchanged from main; this is a data-interchange
//  contract with main's own offline harness readers, not a Sneaky-specific convention to rename)
//    Semicolon-delimited, NO header row, NT-LOCAL timestamps, sub-second as 100-ns ticks:
//      L1;{mdType};{yyyyMMddHHmmss};{subsec100ns};{price};{volume}
//      L2;{kind};{yyyyMMddHHmmss};{subsec100ns};{op};{pos};{maker};{price};{volume}
//    mdType/kind = NT MarketDataType (0=Ask 1=Bid 2=Last).  op = NT Operation (0=Add 1=Update
//    2=Remove).  pos = depth position, 0 = top of book.
//    Line 1 is a `#META;` comment — parsers filter on the `L1;`/`L2;` prefix, so it stays invisible
//    to them while keeping the file self-describing.
//
//  LAYOUT + ROTATION
//    <settings>\Tape\<Instrument FullName>\yyyyMMdd.csv — one file per NT-LOCAL date.
//
//  ⚠ DISK. This is not a small file. GC alone produced 6.7M depth events in one day; expect roughly
//    **300 MB per instrument per day** uncompressed, and L2 is ~3.5x the row count of L1. Set
//    `RecordDepth = false` if you only need trades. Watch free space before leaving it unattended.
//
//  NOT A SENSOR — the …State publish protocol does NOT apply
//    This is a capture device: no opinion, nothing to fuse, no in-platform consumer, not a Council
//    voter. It writes files and draws a card.
//
//  HOW TO USE IT
//    1. Put it on a chart of the instrument you want taped. Any bar type — it never reads bars.
//    2. Leave the chart open and connected. The card shows LIVE + running row counts.
//    3. Point whatever reads Tape\ files at this instrument's folder.
//
//  CHANGELOG
//    v1.0.0 (2026-08-17) — ported from main (identical to main's 2026-07-26 build; capture logic,
//             CSV format, realtime gate, and day-rotation all unchanged), mapped to Sneaky
//             naming/namespace/SneakyCore/SneakySkin and re-plumbed onto live's SCROLL card
//             convention (ISneakyScrollCard / EnsureScrollHook / PlaceScroll / ConductorRender —
//             following SneakyBarDump's own precedent for converting this exact card mechanism,
//             see that file's 2026-08-07 note) instead of main's older SneakySkin.CardLayout.Place.
//             Standalone, purely-additive port — not referenced by any other Sneaky file, and this
//             was the one main-only indicator flagged in the earlier gap survey as deliberately not
//             auto-ported (real-time-gated, file-writing, ~300MB/instrument/day — a heavier tool
//             than the smoother ports) until explicitly requested.
// ═════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Text;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.AddOns.Sneaky;

namespace NinjaTrader.NinjaScript.Indicators.Sneaky
{
    public class SneakyTapeRecorder_v1_0_0 : Indicator, ISneakyScrollCard
    {
        private const string TapeVer = "1.0.0";

        // 2026-09-05 -- BUGFIX (file-append race): Flush() below calls File.AppendAllText(_path, ...)
        // with no synchronization. That's fine for exactly one chart instance recording one instrument,
        // but this suite is routinely run with MULTIPLE chart instances on the SAME instrument (this
        // codebase's own "instance E"/"instance F" A/B-testing convention for comparing weight
        // profiles) -- and every instance recording the same instrument lands on the identical
        // day-keyed path (_dir\{yyyyMMdd}.csv). Two instances flushing at once can then race on the
        // same file: at best each throws/retries (File.AppendAllText's default sharing doesn't allow
        // two concurrent writers, so the loser's buffered rows just wait for its next Flush -- not
        // silent corruption, but dropped-until-retry timing you'd have no way to notice), at worst
        // (depending on .NET/OS file-sharing behavior) interleaved partial writes. A single process-
        // wide lock keyed by the resolved file path serializes any two instances that land on the same
        // file, without blocking instances writing to genuinely different paths/instruments.
        private static readonly Dictionary<string, object> _pathLocks =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        private static object PathLock(string path)
        {
            lock (_pathLocks)
            {
                object l;
                if (!_pathLocks.TryGetValue(path, out l)) { l = new object(); _pathLocks[path] = l; }
                return l;
            }
        }

        private string        _dir;
        private string        _path;
        private string        _day;          // yyyyMMdd of the file currently open
        private StringBuilder _buf;
        private int           _pending;
        private long          _l1;
        private long          _l2;
        private bool          _writerDead;
        private bool          _writeFailed;
        private string        _lastErr;
        private bool          _live;
        private DateTime      _lastEvent;
        private double        _bid, _ask;    // top of book, for the card only

        // card
        private SneakySkin.Painter _sp;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "Sneaky Tape Recorder v1.0.0";
                Description = "Captures the LIVE tape — every L1 trade/quote and every L2 depth event — to "
                            + "Tape\\ in gbNRDtoCSV format, so main's offline harness reads it unchanged. "
                            + "Exists because NT's replay depth store is not a usable order book. Realtime only. "
                            + "⚠ ~300 MB per instrument per day.";
                IsOverlay                = true;
                Calculate                = Calculate.OnBarClose;   // bars are irrelevant here; keep OnBarUpdate cheap
                DrawOnPricePanel         = true;
                IsSuspendedWhileInactive = false;                  // MUST stay false or capture stops when unfocused
                ShowInfo                 = true;
                CardCorner               = SneakyCardCorner.TopRight;
                RecordDepth              = true;
                FlushEveryRows           = 4000;
                ShowIndicatorLabel       = false;
            }
            else if (State == State.DataLoaded)
            {
                if (!ShowIndicatorLabel) Name = string.Empty;

                _buf = new StringBuilder(1 << 18);
                try
                {
                    _dir = Path.Combine(SneakyCore.SettingsDir, "Tape", SafeFullName());
                    Directory.CreateDirectory(_dir);
                }
                catch (Exception ex)
                {
                    _dir = null; _writerDead = true; _lastErr = ex.Message;
                    try
                    {
                        SneakyCore.Log("TapeRec", "WRITER DEAD — could not create Tape\\" + SafeFullName()
                            + "; NOTHING will be recorded this session (" + ex.Message + "). The card shows NO REC.");
                    }
                    catch (Exception _sx) { SneakyCore.Swallow("SneakyTapeRecorder.OnStateChange", _sx); }
                }
            }
            else if (State == State.Terminated)
            {
                Flush();
                if (_sp != null)
                {
                    try { _sp.Dispose(); } catch (Exception _sx) { SneakyCore.Swallow("SneakyTapeRecorder.OnStateChange", _sx); }
                    _sp = null;
                }
                try { SneakySkin.CardLayout.Release(this); } catch (Exception _sx) { SneakyCore.Swallow("SneakyTapeRecorder.OnStateChange", _sx); }
            }
        }

        protected override void OnBarUpdate() { /* deliberately empty — this tool never reads bars */ }

        // ── L1: trades and top-of-book quotes ───────────────────────────────────────────────────
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (_writerDead || e == null) return;
            // THE GATE. Replay/historical depth is the degraded data this tool exists to escape.
            if (State != State.Realtime) return;
            _live = true;

            try
            {
                int kind = (int)e.MarketDataType;
                if (kind == 1) _bid = e.Price;
                else if (kind == 0) _ask = e.Price;
                _lastEvent = e.Time;

                var sb = new StringBuilder(48);
                sb.Append("L1;").Append(kind.ToString(CultureInfo.InvariantCulture)).Append(';');
                Stamp(sb, e.Time);
                sb.Append(P(e.Price)).Append(';').Append(e.Volume.ToString(CultureInfo.InvariantCulture));
                Append(sb.ToString());
                _l1++;
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyTapeRecorder.OnMarketData", _sx); }
        }

        // ── L2: the whole point — real depth, with position and size at every level ─────────────
        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (_writerDead || !RecordDepth || e == null) return;
            if (State != State.Realtime) return;
            _live = true;

            try
            {
                var sb = new StringBuilder(64);
                sb.Append("L2;").Append(((int)e.MarketDataType).ToString(CultureInfo.InvariantCulture)).Append(';');
                Stamp(sb, e.Time);
                sb.Append(((int)e.Operation).ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(e.Position.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(e.MarketMaker == null ? "" : e.MarketMaker).Append(';')
                  .Append(P(e.Price)).Append(';')
                  .Append(e.Volume.ToString(CultureInfo.InvariantCulture));
                Append(sb.ToString());
                _l2++;
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyTapeRecorder.OnMarketDepth", _sx); }
        }

        /// <summary>`yyyyMMddHHmmss;{subsec in 100-ns ticks};` — gbNRDtoCSV's exact stamp encoding.
        /// NT-LOCAL time on purpose: the export is local, and emitting UTC here would silently shift
        /// every row by the offset.</summary>
        private static void Stamp(StringBuilder sb, DateTime t)
        {
            sb.Append(t.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)).Append(';')
              .Append((t.Ticks % TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture)).Append(';');
        }

        private static string P(double v)
        {
            return v.ToString("0.##########", CultureInfo.InvariantCulture);
        }

        private void Append(string row)
        {
            if (_dir == null || _buf == null) return;

            // Roll at the NT-LOCAL date boundary so files line up with the export's own day layout.
            string d = _lastEvent.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (_day == null || d != _day)
            {
                Flush();
                _day  = d;
                _path = Path.Combine(_dir, d + ".csv");
                WriteMeta();
            }

            _buf.Append(row).Append('\n');
            _pending++;
            if (_pending >= Math.Max(200, FlushEveryRows)) Flush();
        }

        /// <summary>A `#META;` line. Both harness parsers filter on the `L1;`/`L2;` prefix, so this is
        /// invisible to them — the file stays format-compatible while still saying what produced it.</summary>
        private void WriteMeta()
        {
            if (_path != null && File.Exists(_path)) return;   // appending to today's file after a restart
            _buf.Append("#META;tapeVer=").Append(TapeVer)
                .Append(";inst=").Append(SafeFullName())
                .Append(";tickSize=").Append(P(SafeTickSize()))
                .Append(";tz=local")
                .Append(";openedUtc=").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                .Append('\n');
        }

        private void Flush()
        {
            if (_path == null || _buf == null || _buf.Length == 0) return;
            try
            {
                // See _pathLocks' comment above: serializes any two chart instances that happen to
                // resolve to the SAME day-keyed path (same instrument, more than one instance running).
                lock (PathLock(_path))
                {
                    File.AppendAllText(_path, _buf.ToString());
                }
                _buf.Length = 0;
                _pending = 0;
                _writeFailed = false;
            }
            catch (Exception ex)
            {
                // Say so on the card. A silently short tape that looks complete is the worst outcome —
                // it is exactly how the replay store's degraded book went unnoticed for months on main.
                _writeFailed = true;
                _lastErr = ex.Message;
                SneakyCore.Swallow("SneakyTapeRecorder.Flush", ex);
            }
        }

        // ── the Sneaky glass card ───────────────────────────────────────────────────────────────
        // 2026-08-17 — ported onto the shared PlaceScroll()/ISneakyScrollCard rotation (see
        // SneakyBarDump's 2026-08-07 note for why every Sneaky card takes its turn in the shared
        // rotation rather than claiming a fixed spot) instead of main's SneakySkin.CardLayout.Place().
        protected override void OnRender(NinjaTrader.Gui.Chart.ChartControl chartControl, NinjaTrader.Gui.Chart.ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (!ShowInfo || RenderTarget == null || ChartPanel == null) return;
            try
            {
                if (_sp == null) _sp = new SneakySkin.Painter();
                _sp.Begin(RenderTarget);
                RenderCard();
                _sp.End();
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyTapeRecorder.OnRender", _sx); }
        }

        public void ConductorRender(SharpDX.Direct2D1.RenderTarget rt)
        {
            if (!ShowInfo || rt == null) return;
            try { if (_sp == null) _sp = new SneakySkin.Painter(); _sp.Begin(rt); RenderCard(); }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyTapeRecorder.ConductorRender", _sx); }
            finally { try { _sp.End(); } catch { } }
        }

        private void RenderCard()
        {
            const float cw = 262f, ch = 118f;
            SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
            var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
                ChartPanel.X, ChartPanel.Y, ChartPanel.W, ChartPanel.H, cw, ch);
            if (slot.Width <= 0f) return;   // not the currently-selected card in the rotation

            bool dead = _writerDead || _writeFailed;
            var edge = dead ? SneakySkin.CDown : (_live ? SneakySkin.CAccent : SneakySkin.CLine);
            var r = _sp.Card(slot.X, slot.Y, cw, ch, edge);

            _sp.Dot(r.Left + 5f, r.Top + 8f, _live && !dead ? SneakySkin.CAccent : SneakySkin.CMute, _live && !dead);
            _sp.Text("SNEAKY TAPE RECORDER", r.Left + 16f, r.Top, r.Width - 66f, 16f, SneakySkin.CInk, 11f, true);
            string st = dead ? "NO REC" : (_live ? "LIVE" : "WAITING");
            var stCol = dead ? SneakySkin.CDown : (_live ? SneakySkin.CAccent : SneakySkin.CInk2);
            _sp.Pill(st, r.Right, r.Top - 1f, stCol);

            var lead = SharpDX.DirectWrite.TextAlignment.Leading;

            _sp.Text("L1 ROWS", r.Left, r.Top + 24f, 120f, 12f, SneakySkin.CMute, 9f, true);
            _sp.Text(_l1.ToString("N0", CultureInfo.InvariantCulture),
                r.Left, r.Top + 32f, 120f, 24f, dead ? SneakySkin.CDown : SneakySkin.CAccent, 19f);

            _sp.Text(RecordDepth ? "L2 DEPTH" : "L2 OFF", r.Left + 132f, r.Top + 24f, 120f, 12f, SneakySkin.CMute, 9f, true);
            _sp.Text(_l2.ToString("N0", CultureInfo.InvariantCulture),
                r.Left + 132f, r.Top + 32f, 120f, 24f,
                RecordDepth ? SneakySkin.CInk2 : SneakySkin.CMute, 19f);

            if (dead)
            {
                _sp.Text(_lastErr == null ? "write failed" : Trunc(_lastErr, 46),
                    r.Left, r.Top + 60f, r.Width, 14f, SneakySkin.CDown, 9.5f, false, lead, true);
            }
            else if (!_live)
            {
                _sp.Text("realtime only — nothing written during a rebuild",
                    r.Left, r.Top + 60f, r.Width, 14f, SneakySkin.CInk2, 9f, false, lead, true);
            }
            else
            {
                string top = (_bid > 0 && _ask > 0)
                    ? P(_bid) + " / " + P(_ask) + "   (" + Spread() + "t)"
                    : "book not yet seen";
                _sp.Text(top, r.Left, r.Top + 60f, r.Width, 14f, SneakySkin.CInk2, 9.5f, false, lead, true);
            }

            _sp.Text("buffered " + _pending.ToString(CultureInfo.InvariantCulture)
                     + "   file " + (_day == null ? "—" : _day),
                r.Left, r.Top + 78f, r.Width, 12f, SneakySkin.CMute, 9f, false, lead, true);
            _sp.Text(SafeFullName(), r.Left, r.Top + 94f, r.Width, 12f, SneakySkin.CMute, 9f, false, lead, true);
        }

        private string Spread()
        {
            try
            {
                double ts = SafeTickSize();
                if (ts <= 0 || _ask <= _bid) return "?";
                return Math.Round((_ask - _bid) / ts).ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyTapeRecorder.Spread", _sx); return "?"; }
        }

        private string SafeFullName()
        {
            try { return Instrument != null ? Instrument.FullName : "unknown"; }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyTapeRecorder.FullName", _sx); return "unknown"; }
        }
        private double SafeTickSize()
        {
            try { return Instrument.MasterInstrument.TickSize; }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyTapeRecorder.TickSize", _sx); return double.NaN; }
        }
        private static string Trunc(string s, int n) { return s == null ? "" : (s.Length <= n ? s : s.Substring(0, n)); }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Record L2 depth", Description = "Capture the full depth ladder. This is the whole point — turn off only if you need trades alone (L2 is ~3.5x the rows).", GroupName = "Tape Recorder", Order = 1)]
        public bool RecordDepth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show info card", Description = "Draw the Sneaky glass-card readout (off = pure headless recorder).", GroupName = "Tape Recorder", Order = 2)]
        public bool ShowInfo { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Card corner", Description = "Which chart corner the card docks to. Cards in the same corner auto-stack (never overlap).", GroupName = "Tape Recorder", Order = 3)]
        public SneakyCardCorner CardCorner { get; set; }

        // NOT a [NinjaScriptProperty] — serializes to the workspace + shows in F6 but stays OUT of the
        // generated constructor (no codegen churn).
        [Display(Name = "Flush every N rows", Description = "Write the buffer to disk every N rows. Lower = less lost on a crash, more disk churn. Minimum 200.", GroupName = "Tape Recorder", Order = 4)]
        public int FlushEveryRows { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show indicator label", Description = "Show NinjaTrader's chart name label. Sneaky default = OFF (clean chart); turn on to restore it.", GroupName = "Sneaky", Order = 100)]
        public bool ShowIndicatorLabel { get; set; }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Sneaky.SneakyTapeRecorder_v1_0_0[] cacheSneakyTapeRecorder_v1_0_0;
		public Sneaky.SneakyTapeRecorder_v1_0_0 SneakyTapeRecorder_v1_0_0(bool recordDepth, bool showInfo, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyTapeRecorder_v1_0_0(Input, recordDepth, showInfo, cardCorner, showIndicatorLabel);
		}

		public Sneaky.SneakyTapeRecorder_v1_0_0 SneakyTapeRecorder_v1_0_0(ISeries<double> input, bool recordDepth, bool showInfo, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyTapeRecorder_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyTapeRecorder_v1_0_0.Length; idx++)
					if (cacheSneakyTapeRecorder_v1_0_0[idx] != null && cacheSneakyTapeRecorder_v1_0_0[idx].RecordDepth == recordDepth && cacheSneakyTapeRecorder_v1_0_0[idx].ShowInfo == showInfo && cacheSneakyTapeRecorder_v1_0_0[idx].CardCorner == cardCorner && cacheSneakyTapeRecorder_v1_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyTapeRecorder_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyTapeRecorder_v1_0_0[idx];
			return CacheIndicator<Sneaky.SneakyTapeRecorder_v1_0_0>(new Sneaky.SneakyTapeRecorder_v1_0_0(){ RecordDepth = recordDepth, ShowInfo = showInfo, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyTapeRecorder_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.SneakyTapeRecorder_v1_0_0 SneakyTapeRecorder_v1_0_0(bool recordDepth, bool showInfo, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyTapeRecorder_v1_0_0(Input, recordDepth, showInfo, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.SneakyTapeRecorder_v1_0_0 SneakyTapeRecorder_v1_0_0(ISeries<double> input , bool recordDepth, bool showInfo, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyTapeRecorder_v1_0_0(input, recordDepth, showInfo, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.SneakyTapeRecorder_v1_0_0 SneakyTapeRecorder_v1_0_0(bool recordDepth, bool showInfo, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyTapeRecorder_v1_0_0(Input, recordDepth, showInfo, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.SneakyTapeRecorder_v1_0_0 SneakyTapeRecorder_v1_0_0(ISeries<double> input , bool recordDepth, bool showInfo, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyTapeRecorder_v1_0_0(input, recordDepth, showInfo, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
