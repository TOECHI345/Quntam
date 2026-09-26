// =====================================================================================================
//  OrderFlowAuctionSuite — ENGINE  (pure C#, ZERO ATAS dependencies)
//
//  This file contains the whole decision-support pipeline as plain C# so it compiles and can be unit
//  tested without ATAS, and so every signal is explainable from observable data. The ATAS adapter
//  (OrderFlowAuctionSuite.cs) only feeds candles/footprint in and renders the snapshot out.
//
//  Pipeline (methodology):
//     DAILY PROFILE -> DAILY BIAS -> LONG-TERM AUCTION -> COMPOSITE PROFILE -> IMPORTANT LEVEL
//     -> PRICE/AUCTION CONTEXT -> DELTA/AGGRESSION -> ABSORPTION/TRAP -> BATTLE OF ORDERS
//     -> PRICE CONFIRMATION -> TRIGGER -> SIGNAL (with a transparent score breakdown)
//
//  Modular classes (as requested): VolumeProfileAnalyzer, DeltaAnalyzer, AbsorptionDetector,
//  TrapDetector, AuctionAnalyzer, BalanceTrendDetector, VwapCalc, ZoneManager, ScoreEngine, SignalEngine,
//  all orchestrated by SuiteEngine.
// =====================================================================================================
#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OrderFlowSuite
{
    #region Enums / small types

    public enum Bias { Bullish, Bearish, Neutral }

    public enum MarketState { Balance, Trend, Transition }

    public enum Side { None, Long, Short }

    public enum ZoneKind
    {
        Vah, Val, Poc, Hvn, Lvn,
        BuyAggression, SellAggression,
        TrappedBuyers, TrappedSellers,
        ReloadBuy, ReloadSell,
        Retreat, Passover,
        Compression, Stacked,
        CompositePoc, CompositeVah, CompositeVal,
        Vwap
    }

    public enum SignalKind
    {
        LongReversal, ShortReversal,
        LongReload, ShortReload,
        LongMomentum, ShortMomentum,
        LongRange, ShortRange
    }

    public enum SignalQuality { NoSignal, Weak, Valid, HighConfidence }

    /// <summary>One footprint row: aggressive buy (ask) / sell (bid) volume traded at a price (in ticks).</summary>
    public struct OfRow
    {
        public long Tick;
        public double Bid;   // aggressive sells
        public double Ask;   // aggressive buys
        public double Vol;
    }

    /// <summary>A completed or forming bar plus its footprint, in engine units (ticks).</summary>
    public sealed class OfBar
    {
        public int Index;
        public DateTime TimeUtc;
        public DateTime LastTimeUtc;
        public long OpenTick, HighTick, LowTick, CloseTick;
        public double Volume, Delta, Bid, Ask;
        public bool IsNewSession;
        public readonly List<OfRow> Rows = new();

        public void Reset()
        {
            Rows.Clear();
        }

        public void AddRow(long tick, double bid, double ask, double vol)
        {
            Rows.Add(new OfRow { Tick = tick, Bid = bid, Ask = ask, Vol = vol });
        }

        public long Mid => (HighTick + LowTick) / 2;
        public double Range => Math.Max(1, HighTick - LowTick);
        public double Body => Math.Abs(CloseTick - OpenTick);
        public double BodyFrac => Body / Range;
        public bool Up => CloseTick > OpenTick;
        public bool Down => CloseTick < OpenTick;
    }

    #endregion

    #region Config

    /// <summary>Everything tunable. The adapter fills this from indicator inputs and passes a clone to the engine.</summary>
    public sealed class EngineConfig
    {
        // profile
        public int ValueAreaPercent = 70;
        public int LookbackSessions = 5;
        public int CompositeSessions = 60;      // ~3 months of RTH days
        public bool UseComposite = true;

        // bias
        public double ValueShiftTicks = 2;      // min POC/value migration to count as a shift

        // nodes
        public double HvnFactor = 1.5;
        public double LvnFactor = 0.5;

        // delta / aggression
        public int AggressionLevelsPerSide = 3;
        public double MinAggressionVol = 100;
        public double MinAggressionDelta = 100;

        // absorption
        public int AbsorptionStrong = 80;
        public int AbsorptionModerate = 60;
        public double ControlFrac = 0.35;       // |delta|/vol to call a side "in control"
        public double VolFactor = 1.2;          // vol vs rolling avg for "strong participation"

        // balance / compression
        public int CompressionLookback = 20;
        public double StackedToleranceTicks = 4;

        // signals
        public int MinScoreToShow = 50;
        public double MinRewardRisk = 1.2;
        public int StopBufferTicks = 2;
        public int MinBarsBetweenSignals = 3;
        public int ValueBufferTicks = 2;
        public int MaxExcursionBars = 40;
        public bool EnableReversal = true, EnableReload = true, EnableMomentum = true, EnableRange = true;

        public EngineConfig Clone() => (EngineConfig)MemberwiseClone();
    }

    #endregion

    #region Profile result + analyzers

    public struct ProfileResult
    {
        public bool Valid;
        public long Poc, Vah, Val, Hi, Lo;
        public double Total, Delta;
    }

    public struct AggressionLevel
    {
        public long Tick;
        public double Buy, Sell, Delta, Vol;
        public bool IsBuy;   // dominant side
    }

    /// <summary>POC / VAH / VAL / HVN / LVN from a price->volume distribution.</summary>
    public static class VolumeProfileAnalyzer
    {
        public static ProfileResult Compute(Dictionary<long, double> vol, int vaPct, long hi, long lo, double totalDelta)
        {
            var r = new ProfileResult { Hi = hi, Lo = lo, Delta = totalDelta };

            if (vol.Count == 0)
                return r;

            double total = 0, best = -1;
            long poc = 0, mn = long.MaxValue, mx = long.MinValue;

            foreach (var kv in vol)
            {
                total += kv.Value;
                if (kv.Value > best) { best = kv.Value; poc = kv.Key; }
                if (kv.Key < mn) mn = kv.Key;
                if (kv.Key > mx) mx = kv.Key;
            }

            double target = total * vaPct / 100.0;
            double acc = best;
            long up = poc, dn = poc;

            while (acc < target && (up < mx || dn > mn))
            {
                double vUp = up + 1 <= mx ? Get(vol, up + 1) : -1;
                double vDn = dn - 1 >= mn ? Get(vol, dn - 1) : -1;
                if (vUp < 0 && vDn < 0) break;
                if (vUp >= vDn) { up++; if (vUp > 0) acc += vUp; }
                else { dn--; if (vDn > 0) acc += vDn; }
            }

            r.Valid = true;
            r.Poc = poc;
            r.Vah = up;
            r.Val = dn;
            r.Total = total;
            if (r.Hi == 0 && mx != long.MinValue) r.Hi = mx;
            if (r.Lo == 0 && mn != long.MaxValue) r.Lo = mn;
            return r;
        }

        /// <summary>Local peaks (HVN) and troughs (LVN) relative to the mean occupied volume.</summary>
        public static void DetectNodes(Dictionary<long, double> vol, long lo, long hi, double hvnFactor, double lvnFactor,
            List<long> hvn, List<long> lvn, int maxEach = 6)
        {
            hvn.Clear();
            lvn.Clear();
            if (vol.Count < 5 || hi <= lo)
                return;

            double sum = 0;
            int n = 0;
            for (long t = lo; t <= hi; t++) { sum += Get(vol, t); n++; }
            if (n == 0) return;
            double mean = sum / n;
            if (mean <= 0) return;

            for (long t = lo + 1; t < hi; t++)
            {
                double v = Get(vol, t), a = Get(vol, t - 1), b = Get(vol, t + 1);
                if (v >= a && v >= b && v > mean * hvnFactor && hvn.Count < maxEach)
                    hvn.Add(t);
                else if (v <= a && v <= b && v < mean * lvnFactor && v > 0 && lvn.Count < maxEach)
                    lvn.Add(t);
            }
        }

        public static double Get(Dictionary<long, double> d, long t) => d.TryGetValue(t, out var v) ? v : 0;
    }

    /// <summary>Finds the strongest buy / sell aggression price levels from footprint buy/sell dictionaries.</summary>
    public static class DeltaAnalyzer
    {
        public static void TopLevels(Dictionary<long, double> buy, Dictionary<long, double> sell,
            int perSide, double minVol, double minDelta, List<AggressionLevel> outBuy, List<AggressionLevel> outSell)
        {
            outBuy.Clear();
            outSell.Clear();

            var all = new List<AggressionLevel>();
            var keys = new HashSet<long>(buy.Keys);
            foreach (var k in sell.Keys) keys.Add(k);

            foreach (var t in keys)
            {
                double b = VolumeProfileAnalyzer.Get(buy, t);
                double s = VolumeProfileAnalyzer.Get(sell, t);
                double v = b + s;
                if (v < minVol) continue;
                double d = b - s;
                all.Add(new AggressionLevel { Tick = t, Buy = b, Sell = s, Delta = d, Vol = v, IsBuy = d >= 0 });
            }

            all.Sort((x, y) => y.Delta.CompareTo(x.Delta)); // most positive first
            for (int i = 0; i < all.Count && outBuy.Count < perSide; i++)
                if (all[i].Delta >= minDelta) outBuy.Add(all[i]);

            all.Sort((x, y) => x.Delta.CompareTo(y.Delta)); // most negative first
            for (int i = 0; i < all.Count && outSell.Count < perSide; i++)
                if (-all[i].Delta >= minDelta) outSell.Add(all[i]);
        }
    }

    /// <summary>Absorption = high effort (one-sided aggression + volume) with low result (price does not follow).</summary>
    public static class AbsorptionDetector
    {
        /// <returns>score 0-100; absorbedSide = the aggressor that failed (Long = buyers absorbed, Short = sellers absorbed).</returns>
        public static int Score(OfBar bar, double avgVol, double controlFrac, out Side absorbedSide)
        {
            absorbedSide = Side.None;
            if (bar.Volume <= 0) return 0;

            double ctrl = bar.Delta / bar.Volume;                    // -1..+1 one-sidedness
            double effort = Math.Min(1.0, Math.Abs(ctrl) / Math.Max(0.01, controlFrac));
            double volStrength = avgVol > 0 ? Math.Min(1.5, bar.Volume / avgVol) / 1.5 : 0.5;

            if (Math.Abs(ctrl) < controlFrac * 0.6)
                return 0;

            // aggressor pushed but price failed to close in their favour
            bool buyersAgg = ctrl > 0;
            double result;
            if (buyersAgg)
                result = (double)(bar.CloseTick - bar.OpenTick) / bar.Range; // want >0 for buyers
            else
                result = (double)(bar.OpenTick - bar.CloseTick) / bar.Range; // want >0 for sellers

            double fail = Math.Min(1.0, Math.Max(0.0, 0.5 - result) / 0.5);  // low/negative result -> high fail
            double score = 100.0 * effort * volStrength * fail;

            if (score >= 40)
                absorbedSide = buyersAgg ? Side.Long : Side.Short;

            return (int)Math.Round(Math.Min(100, score));
        }
    }

    #endregion

    #region Sessions / auction

    public sealed class SessionProfile
    {
        public int StartBar = -1, EndBar = -1;
        public DateTime StartTime, EndTime;
        public readonly Dictionary<long, double> Vol = new();
        public readonly Dictionary<long, double> Buy = new();
        public readonly Dictionary<long, double> Sell = new();
        public long Hi = long.MinValue, Lo = long.MaxValue;
        public double TotalVol, TotalDelta;
        public ProfileResult Result;

        public void Add(OfBar b)
        {
            if (StartBar < 0) { StartBar = b.Index; StartTime = b.TimeUtc; }
            EndBar = b.Index;
            EndTime = b.LastTimeUtc;
            Hi = Hi == long.MinValue ? b.HighTick : Math.Max(Hi, b.HighTick);
            Lo = Lo == long.MaxValue ? b.LowTick : Math.Min(Lo, b.LowTick);
            TotalVol += b.Volume;
            TotalDelta += b.Delta;

            if (b.Rows.Count > 0)
            {
                foreach (var r in b.Rows)
                {
                    Vol[r.Tick] = VolumeProfileAnalyzer.Get(Vol, r.Tick) + r.Vol;
                    Buy[r.Tick] = VolumeProfileAnalyzer.Get(Buy, r.Tick) + r.Ask;
                    Sell[r.Tick] = VolumeProfileAnalyzer.Get(Sell, r.Tick) + r.Bid;
                }
            }
            else
            {
                // fallback: no footprint -> spread bar volume/delta across its range
                long span = b.HighTick - b.LowTick + 1;
                if (span > 0 && span <= 5000)
                {
                    double per = b.Volume / span;
                    double dper = b.Delta / span;
                    for (long t = b.LowTick; t <= b.HighTick; t++)
                    {
                        Vol[t] = VolumeProfileAnalyzer.Get(Vol, t) + per;
                        double half = (per + dper) / 2;
                        Buy[t] = VolumeProfileAnalyzer.Get(Buy, t) + Math.Max(0, half);
                        Sell[t] = VolumeProfileAnalyzer.Get(Sell, t) + Math.Max(0, per - half);
                    }
                }
                else
                {
                    Vol[b.CloseTick] = VolumeProfileAnalyzer.Get(Vol, b.CloseTick) + b.Volume;
                }
            }
        }

        public void Finalise(int vaPct)
        {
            Result = VolumeProfileAnalyzer.Compute(Vol, vaPct, Hi == long.MinValue ? 0 : Hi, Lo == long.MaxValue ? 0 : Lo, TotalDelta);
        }
    }

    /// <summary>Daily bias, long-term auction, stacked profiles and composite context from completed sessions.</summary>
    public sealed class AuctionAnalyzer
    {
        public Bias DailyBias = Bias.Neutral;
        public Bias LongTermAuction = Bias.Neutral;
        public readonly List<(long lo, long hi)> StackedGroups = new();
        public int StackedPocDir; // -1 lower (bearish), +1 higher (bullish), 0 flat
        public ProfileResult Composite;

        public void Update(List<SessionProfile> sessions, EngineConfig cfg)
        {
            StackedGroups.Clear();
            StackedPocDir = 0;

            if (sessions.Count < 2)
            {
                DailyBias = LongTermAuction = Bias.Neutral;
                return;
            }

            var cur = sessions[sessions.Count - 1].Result;
            var prev = sessions[sessions.Count - 2].Result;
            DailyBias = ShiftBias(prev, cur, cfg.ValueShiftTicks);

            // long-term auction: net of value shifts across the lookback window
            int up = 0, dn = 0;
            int start = Math.Max(1, sessions.Count - cfg.LookbackSessions);
            for (int i = start; i < sessions.Count; i++)
            {
                var b = ShiftBias(sessions[i - 1].Result, sessions[i].Result, cfg.ValueShiftTicks);
                if (b == Bias.Bullish) up++;
                else if (b == Bias.Bearish) dn++;
            }
            LongTermAuction = up > dn ? Bias.Bullish : dn > up ? Bias.Bearish : Bias.Neutral;

            // stacked profiles: consecutive sessions with overlapping value areas within tolerance
            int gStart = -1;
            for (int i = 1; i < sessions.Count; i++)
            {
                var a = sessions[i - 1].Result;
                var c = sessions[i].Result;
                bool stacked = a.Valid && c.Valid
                    && Math.Abs(a.Vah - c.Vah) <= cfg.StackedToleranceTicks
                    && Math.Abs(a.Val - c.Val) <= cfg.StackedToleranceTicks;
                if (stacked) { if (gStart < 0) gStart = i - 1; }
                else { FlushStack(sessions, gStart, i - 1); gStart = -1; }
            }
            FlushStack(sessions, gStart, sessions.Count - 1);

            // composite profile: merge the last CompositeSessions volume distributions
            if (cfg.UseComposite)
            {
                var merged = new Dictionary<long, double>();
                long hi = long.MinValue, lo = long.MaxValue;
                double delta = 0;
                int cs = Math.Max(1, sessions.Count - cfg.CompositeSessions);
                for (int i = cs; i < sessions.Count; i++)
                {
                    var sp = sessions[i];
                    foreach (var kv in sp.Vol)
                        merged[kv.Key] = VolumeProfileAnalyzer.Get(merged, kv.Key) + kv.Value;
                    hi = Math.Max(hi, sp.Hi);
                    lo = Math.Min(lo, sp.Lo);
                    delta += sp.TotalDelta;
                }
                Composite = VolumeProfileAnalyzer.Compute(merged, cfg.ValueAreaPercent,
                    hi == long.MinValue ? 0 : hi, lo == long.MaxValue ? 0 : lo, delta);
            }
        }

        private void FlushStack(List<SessionProfile> s, int a, int b)
        {
            if (a < 0 || b <= a) return;
            long lo = long.MaxValue, hi = long.MinValue;
            for (int i = a; i <= b; i++) { lo = Math.Min(lo, s[i].Result.Val); hi = Math.Max(hi, s[i].Result.Vah); }
            StackedGroups.Add((lo, hi));
            // POC migration inside the group
            long first = s[a].Result.Poc, last = s[b].Result.Poc;
            StackedPocDir = last < first ? -1 : last > first ? 1 : 0;
        }

        private static Bias ShiftBias(ProfileResult a, ProfileResult b, double tol)
        {
            if (!a.Valid || !b.Valid) return Bias.Neutral;
            double am = (a.Vah + a.Val) / 2.0, bm = (b.Vah + b.Val) / 2.0;
            bool overlap = b.Val <= a.Vah && b.Vah >= a.Val;
            if (bm > am + tol && b.Poc >= a.Poc) return Bias.Bullish;
            if (bm < am - tol && b.Poc <= a.Poc) return Bias.Bearish;
            return overlap ? Bias.Neutral : (bm > am ? Bias.Bullish : bm < am ? Bias.Bearish : Bias.Neutral);
        }
    }

    #endregion

    #region VWAP + balance/trend

    public sealed class VwapCalc
    {
        private double _pv, _v, _pv2;
        public double Value, Upper1, Lower1, Upper2, Lower2;
        public bool Valid;
        public readonly List<(int bar, double v, double u1, double d1)> Points = new();

        public void Reset()
        {
            _pv = _v = _pv2 = 0;
            Valid = false;
            Points.Clear();
        }

        public void Add(OfBar b, double tick)
        {
            double typical = (double)(b.HighTick + b.LowTick + b.CloseTick) / 3.0 * tick;
            _pv += typical * b.Volume;
            _pv2 += typical * typical * b.Volume;
            _v += b.Volume;
            if (_v <= 0) return;
            Value = _pv / _v;
            double var = Math.Max(0, _pv2 / _v - Value * Value);
            double sd = Math.Sqrt(var);
            Upper1 = Value + sd; Lower1 = Value - sd;
            Upper2 = Value + 2 * sd; Lower2 = Value - 2 * sd;
            Valid = true;
            Points.Add((b.Index, Value, Upper1, Lower1));
            if (Points.Count > 5000) Points.RemoveAt(0);
        }
    }

    /// <summary>Market state: balance vs trend vs transition, plus a compression range when present.</summary>
    public sealed class BalanceTrendDetector
    {
        public MarketState State = MarketState.Transition;
        public bool HasCompression;
        public long CompLo, CompHi;

        public void Update(List<SessionProfile> sessions, SessionProfile dev, List<OfBar> recent, EngineConfig cfg)
        {
            // value migration over recent sessions
            int up = 0, dn = 0, overlap = 0;
            int start = Math.Max(1, sessions.Count - Math.Min(sessions.Count, cfg.LookbackSessions));
            for (int i = start; i < sessions.Count; i++)
            {
                var a = sessions[i - 1].Result; var b = sessions[i].Result;
                if (!a.Valid || !b.Valid) continue;
                bool ov = b.Val <= a.Vah && b.Vah >= a.Val;
                if (ov) overlap++;
                double am = (a.Vah + a.Val) / 2.0, bm = (b.Vah + b.Val) / 2.0;
                if (bm > am + cfg.ValueShiftTicks) up++; else if (bm < am - cfg.ValueShiftTicks) dn++;
            }

            bool directional = Math.Abs(up - dn) >= 2;
            bool overlapping = overlap >= Math.Max(1, (sessions.Count - start) / 2);

            // developing acceptance outside prior value?
            bool acceptedOutside = false;
            if (sessions.Count >= 1 && dev.Result.Valid)
            {
                var prior = sessions[sessions.Count - 1].Result;
                acceptedOutside = dev.Result.Val > prior.Vah || dev.Result.Vah < prior.Val;
            }

            if (directional || acceptedOutside) State = MarketState.Trend;
            else if (overlapping) State = MarketState.Balance;
            else State = MarketState.Transition;

            DetectCompression(recent, cfg);
        }

        private void DetectCompression(List<OfBar> recent, EngineConfig cfg)
        {
            HasCompression = false;
            int n = recent.Count;
            if (n < cfg.CompressionLookback) return;

            long hi = long.MinValue, lo = long.MaxValue;
            long hiRecent = long.MinValue, loRecent = long.MaxValue;
            int look = cfg.CompressionLookback;
            for (int i = n - look; i < n; i++)
            {
                hi = Math.Max(hi, recent[i].HighTick);
                lo = Math.Min(lo, recent[i].LowTick);
                if (i >= n - look / 2)
                {
                    hiRecent = Math.Max(hiRecent, recent[i].HighTick);
                    loRecent = Math.Min(loRecent, recent[i].LowTick);
                }
            }
            double full = hi - lo, half = hiRecent - loRecent;
            // range contracting in the more recent half -> compression
            if (full > 0 && half > 0 && half < full * 0.7)
            {
                HasCompression = true;
                CompLo = lo; CompHi = hi;
            }
        }
    }

    #endregion

    #region Zones + scoring

    public sealed class Zone
    {
        public ZoneKind Kind;
        public long Lo, Hi;
        public Side Side;
        public int CreatedBar;
        public int EndBar = -1;      // -1 = still active
        public string Label = "";
        public double Score;
        public long RefTick;         // the anchor level (e.g. broken high)
        public long Price => (Lo + Hi) / 2;
    }

    public sealed class ScoreEngine
    {
        public readonly List<(string reason, int pts)> Breakdown = new();
        public int Total;

        public void Reset() { Breakdown.Clear(); Total = 0; }

        public void Add(string reason, int pts)
        {
            if (pts == 0) return;
            Breakdown.Add((reason, pts));
            Total += pts;
        }

        public SignalQuality Quality =>
            Total >= 80 ? SignalQuality.HighConfidence :
            Total >= 65 ? SignalQuality.Valid :
            Total >= 50 ? SignalQuality.Weak : SignalQuality.NoSignal;

        public string Text(string header)
        {
            var sb = new StringBuilder(header);
            sb.Append("  SCORE ").Append(Total).Append(" (").Append(Quality).Append(")\n");
            foreach (var (r, p) in Breakdown)
                sb.Append(p >= 0 ? "+" : "").Append(p).Append("  ").Append(r).Append('\n');
            return sb.ToString();
        }

        public string[] Lines(string header)
        {
            var list = new List<string> { header + "  SCORE " + Total + " (" + Quality + ")" };
            foreach (var (r, p) in Breakdown)
                list.Add((p >= 0 ? "+" : "") + p + "  " + r);
            return list.ToArray();
        }
    }

    #endregion

    #region Signal record + snapshot

    public sealed class SignalMark
    {
        public int Bar;
        public long EntryTick, StopTick, TargetTick, HighTick, LowTick;
        public Side Side;
        public SignalKind Kind;
        public int Score;
        public SignalQuality Quality;
        public double Rr;
        public string[] Breakdown = Array.Empty<string>();
        public bool Developing;
    }

    public sealed class LevelLine
    {
        public long Tick;
        public ZoneKind Kind;
        public string Label = "";
    }

    /// <summary>A merged, scored support/resistance level with confluence reasons and a live test status.</summary>
    public sealed class KeyZone
    {
        public long Tick;
        public bool Resistance;
        public int Score;
        public readonly List<string> Reasons = new();
        public int Tests;
        public int CreatedBar, LastSeenBar = -1, LastTestBar = -1;
        public bool Broken, Flipped;

        public string Status =>
            Broken ? "BROKEN" :
            Flipped ? "FLIPPED" :
            Tests == 0 ? "FRESH" :
            Tests == 1 ? "1ST TEST" :
            Tests >= 3 ? "HOLDING" : "TESTED";

        public string ReasonText(int max)
        {
            var sb = new StringBuilder();
            int n = Math.Min(max, Reasons.Count);
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(" + "); sb.Append(Reasons[i]); }
            return sb.ToString();
        }
    }

    public sealed class DisplaySnapshot
    {
        public double TickSize = 0.01;
        public int PriceDecimals = 2;
        public Bias DailyBias, LongTermAuction;
        public MarketState State;
        public string StateNote = "";
        public LevelLine[] Levels = Array.Empty<LevelLine>();
        public KeyZone[] KeyZones = Array.Empty<KeyZone>();
        public Zone[] Zones = Array.Empty<Zone>();
        public SignalMark[] Signals = Array.Empty<SignalMark>();
        public (int bar, double v, double u1, double d1)[] Vwap = Array.Empty<(int, double, double, double)>();
        public bool VwapValid;
        // debug
        public double LiveDelta, LiveBuy, LiveSell, Power;
        public int LiveAbsorption;
        public long DevPoc, DevVah, DevVal;
        public long RefPoc, RefVah, RefVal;
        public string[] DebugLines = Array.Empty<string>();
    }

    #endregion

    // =================================================================================================
    //  SuiteEngine — orchestrator
    // =================================================================================================
    public sealed class SuiteEngine
    {
        private readonly EngineConfig _cfg;
        private readonly double _tick;
        private readonly int _decimals;

        private readonly List<SessionProfile> _sessions = new();
        private SessionProfile _dev = new();
        private readonly AuctionAnalyzer _auction = new();
        private readonly BalanceTrendDetector _balance = new();
        private readonly VwapCalc _vwap = new();
        private readonly ZoneList _zones = new();
        private readonly List<KeyZone> _keyZones = new();
        private readonly List<SignalMark> _signals = new();
        private readonly List<OfBar> _recent = new();          // rolling window (light copies)
        private readonly List<AggressionLevel> _buyLvls = new();
        private readonly List<AggressionLevel> _sellLvls = new();

        private double _avgVol;
        private int _lastSignalBar = -10000;

        // failed-auction excursion state (below/above reference value)
        private bool _belowActive, _aboveActive;
        private long _belowLow, _aboveHigh, _protectLo, _protectHi;
        private bool _sellAggr, _buyAggr, _absLo, _absHi;
        private int _belowStart, _aboveStart;

        // opening range within the developing session
        private long _orHi, _orLo;
        private bool _orInit, _orDone;
        private DateTime _sessionStart;

        // live
        private double _power, _liveDelta, _liveBuy, _liveSell;
        private int _liveAbs;

        public SuiteEngine(EngineConfig cfg, double tickSize)
        {
            _cfg = cfg;
            _tick = tickSize > 0 ? tickSize : 0.01;
            _decimals = Decimals(_tick);
        }

        public long ToTick(decimal price) => (long)Math.Round(price / (decimal)_tick, MidpointRounding.AwayFromZero);
        public decimal ToPrice(long t) => (decimal)(t * _tick);
        private string P(long t) => ((double)t * _tick).ToString("F" + _decimals, CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------------------- per closed bar
        public void ProcessClosedBar(OfBar bar)
        {
            // session boundary
            if (_dev.StartBar < 0 || bar.IsNewSession)
                RollSession(bar);

            _dev.Add(bar);
            _dev.Finalise(_cfg.ValueAreaPercent);
            _vwap.Add(bar, _tick);
            UpdateOpeningRange(bar);

            _avgVol = _avgVol <= 0 ? bar.Volume : _avgVol + (bar.Volume - _avgVol) * 0.1;
            bool strongVol = bar.Volume > 0 && bar.Volume > _avgVol * _cfg.VolFactor;
            double ctrl = bar.Volume > 0 ? bar.Delta / bar.Volume : 0;

            AddRecent(bar);
            _auction.Update(_sessions, _cfg);
            _balance.Update(_sessions, _dev, _recent, _cfg);

            // aggression levels from the developing session
            DeltaAnalyzer.TopLevels(_dev.Buy, _dev.Sell, _cfg.AggressionLevelsPerSide,
                _cfg.MinAggressionVol, _cfg.MinAggressionDelta, _buyLvls, _sellLvls);

            int absScore = AbsorptionDetector.Score(bar, _avgVol, _cfg.ControlFrac, out var absorbedSide);

            // reference value area = previous completed session (fallback: developing)
            ProfileResult refP = _sessions.Count > 0 ? _sessions[_sessions.Count - 1].Result : _dev.Result;
            if (!refP.Valid) refP = _dev.Result;

            DetectTraps(bar, ctrl, strongVol, refP);
            _zones.Expire(bar, refP);
            UpdateKeyZones(bar, refP);

            EvaluateSignals(bar, ctrl, strongVol, absScore, absorbedSide, refP);

            _pHigh = bar.HighTick; _pLow = bar.LowTick; _pClose = bar.CloseTick; _pOpen = bar.OpenTick; _havePrev = true;
        }

        private long _pHigh, _pLow, _pClose, _pOpen;
        private bool _havePrev;

        public void UpdateLive(OfBar bar)
        {
            _liveDelta = bar.Delta;
            _liveBuy = bar.Ask;
            _liveSell = bar.Bid;
            _power = bar.Volume > 0 ? Clamp(-1, 1, bar.Delta / bar.Volume) : 0;
            _liveAbs = AbsorptionDetector.Score(bar, _avgVol, _cfg.ControlFrac, out _);
        }

        // ---------------------------------------------------------------------------- sessions / OR
        private void RollSession(OfBar bar)
        {
            if (_dev.StartBar >= 0)
            {
                _dev.Finalise(_cfg.ValueAreaPercent);
                _sessions.Add(_dev);
                if (_sessions.Count > Math.Max(_cfg.CompositeSessions + 5, 130))
                    _sessions.RemoveAt(0);
            }
            _dev = new SessionProfile();
            _vwap.Reset();
            _orInit = _orDone = false;
            _sessionStart = bar.TimeUtc;
            _belowActive = _aboveActive = false;
        }

        private void UpdateOpeningRange(OfBar bar)
        {
            if (_orDone) return;
            if (!_orInit) { _orHi = bar.HighTick; _orLo = bar.LowTick; _orInit = true; }
            else { _orHi = Math.Max(_orHi, bar.HighTick); _orLo = Math.Min(_orLo, bar.LowTick); }
            if ((bar.TimeUtc - _sessionStart).TotalMinutes >= 15) _orDone = true;   // 15-min opening range
        }

        private void AddRecent(OfBar bar)
        {
            var copy = new OfBar
            {
                Index = bar.Index, TimeUtc = bar.TimeUtc, LastTimeUtc = bar.LastTimeUtc,
                OpenTick = bar.OpenTick, HighTick = bar.HighTick, LowTick = bar.LowTick, CloseTick = bar.CloseTick,
                Volume = bar.Volume, Delta = bar.Delta, Bid = bar.Bid, Ask = bar.Ask, IsNewSession = bar.IsNewSession
            };
            _recent.Add(copy);
            if (_recent.Count > 400) _recent.RemoveAt(0);
        }

        // ---------------------------------------------------------------------------- traps / failed auctions
        private void DetectTraps(OfBar bar, double ctrl, bool strongVol, ProfileResult refP)
        {
            if (!refP.Valid) return;

            // buyer trap: pushed above prior session high with buy aggression, closed back below
            long hiRef = refP.Hi, loRef = refP.Lo;
            if (bar.HighTick > hiRef && bar.CloseTick < hiRef && ctrl > _cfg.ControlFrac * 0.5)
                _zones.AddUnique(new Zone { Kind = ZoneKind.TrappedBuyers, Lo = hiRef, Hi = bar.HighTick, Side = Side.Short,
                    CreatedBar = bar.Index, RefTick = hiRef, Label = "TRAP BUYERS " + P(hiRef) });

            // seller trap: pushed below prior session low with sell aggression, closed back above
            if (bar.LowTick < loRef && bar.CloseTick > loRef && ctrl < -_cfg.ControlFrac * 0.5)
                _zones.AddUnique(new Zone { Kind = ZoneKind.TrappedSellers, Lo = bar.LowTick, Hi = loRef, Side = Side.Long,
                    CreatedBar = bar.Index, RefTick = loRef, Label = "TRAP SELLERS " + P(loRef) });

            // reload on stored aggression levels: acceptance THROUGH a level (deduped)
            foreach (var lv in _sellLvls)
                if (bar.CloseTick < lv.Tick && _havePrev && _pClose >= lv.Tick)
                    _zones.AddUnique(new Zone { Kind = ZoneKind.ReloadSell, Lo = lv.Tick - _cfg.StopBufferTicks, Hi = lv.Tick + _cfg.StopBufferTicks,
                        Side = Side.Short, CreatedBar = bar.Index, RefTick = lv.Tick, Label = "SELL RELOAD " + P(lv.Tick) });

            foreach (var lv in _buyLvls)
                if (bar.CloseTick > lv.Tick && _havePrev && _pClose <= lv.Tick)
                    _zones.AddUnique(new Zone { Kind = ZoneKind.ReloadBuy, Lo = lv.Tick - _cfg.StopBufferTicks, Hi = lv.Tick + _cfg.StopBufferTicks,
                        Side = Side.Long, CreatedBar = bar.Index, RefTick = lv.Tick, Label = "BUY RELOAD " + P(lv.Tick) });

            // retreat zone: when trending after leaving a compression zone, the far edge is the retreat
            if (_balance.State == MarketState.Trend && _balance.HasCompression)
            {
                long edge = _auction.LongTermAuction == Bias.Bearish ? _balance.CompHi : _balance.CompLo;
                _zones.AddUnique(new Zone { Kind = ZoneKind.Retreat, Lo = edge - _cfg.StopBufferTicks, Hi = edge + _cfg.StopBufferTicks,
                    Side = _auction.LongTermAuction == Bias.Bearish ? Side.Short : Side.Long,
                    CreatedBar = bar.Index, RefTick = edge, Label = "RETREAT " + P(edge) });
            }
        }

        // ---------------------------------------------------------------------------- scored key levels (confluence)
        private void UpdateKeyZones(OfBar bar, ProfileResult refP)
        {
            long tol = Math.Max(2, _cfg.ValueBufferTicks * 2);

            void Cand(long tick, string reason, int score)
            {
                if (tick <= 0) return;
                KeyZone? best = null;
                long bd = long.MaxValue;
                foreach (var z in _keyZones)
                {
                    long d = Math.Abs(z.Tick - tick);
                    if (d <= tol && d < bd) { bd = d; best = z; }
                }
                if (best == null)
                {
                    best = new KeyZone { Tick = tick, CreatedBar = bar.Index };
                    _keyZones.Add(best);
                }
                if (!best.Reasons.Contains(reason)) { best.Reasons.Add(reason); best.Score = Math.Min(100, best.Score + score); }
                best.LastSeenBar = bar.Index;
            }

            if (refP.Valid)
            {
                Cand(refP.Poc, "POC", 20);
                Cand(refP.Vah, "VAH", 16);
                Cand(refP.Val, "VAL", 16);
                Cand(refP.Hi, "PDH", 14);
                Cand(refP.Lo, "PDL", 14);
            }
            if (_dev.Result.Valid)
            {
                Cand(_dev.Result.Poc, "dPOC", 8);
                var hvn = new List<long>(); var lvn = new List<long>();
                VolumeProfileAnalyzer.DetectNodes(_dev.Vol, _dev.Result.Lo, _dev.Result.Hi, _cfg.HvnFactor, _cfg.LvnFactor, hvn, lvn, 4);
                foreach (var t in hvn) Cand(t, "HVN", 10);
                foreach (var t in lvn) Cand(t, "LVN", 6);
            }
            if (_orInit) { Cand(_orHi, "ORH", 12); Cand(_orLo, "ORL", 12); }
            foreach (var lv in _buyLvls) Cand(lv.Tick, "BUY AGG", 10);
            foreach (var lv in _sellLvls) Cand(lv.Tick, "SELL AGG", 10);
            if (_cfg.UseComposite && _auction.Composite.Valid)
            {
                Cand(_auction.Composite.Poc, "cPOC", 10);
                Cand(_auction.Composite.Vah, "cVAH", 8);
                Cand(_auction.Composite.Val, "cVAL", 8);
            }
            DetectSwing(out long sh, out long sl);
            if (sh > 0) Cand(sh, "SWING HIGH", 12);
            if (sl > 0) Cand(sl, "SWING LOW", 12);

            long approach = Math.Max(2, _cfg.ValueBufferTicks);
            foreach (var z in _keyZones)
            {
                bool wasRes = z.Resistance;
                z.Resistance = z.Tick >= bar.CloseTick;
                if (z.Tests > 0 && wasRes != z.Resistance) z.Flipped = true;

                bool touched = bar.HighTick >= z.Tick - approach && bar.LowTick <= z.Tick + approach;
                if (touched && (z.LastTestBar < 0 || bar.Index - z.LastTestBar > 2)) { z.Tests++; z.LastTestBar = bar.Index; }

                if (z.Resistance && bar.CloseTick > z.Tick + approach * 2) z.Broken = true;
                else if (!z.Resistance && bar.CloseTick < z.Tick - approach * 2) z.Broken = true;
                else if (Math.Abs(bar.CloseTick - z.Tick) <= approach * 2) z.Broken = false;
            }

            _keyZones.RemoveAll(z => z.Broken && bar.Index - z.LastSeenBar > 40);
            if (_keyZones.Count > 40)
            {
                _keyZones.Sort((a, b) => a.Score.CompareTo(b.Score));
                _keyZones.RemoveRange(0, _keyZones.Count - 40);
            }
        }

        /// <summary>Simple 2-bar pivot on the rolling window (the bar 2 back is a local high/low).</summary>
        private void DetectSwing(out long sh, out long sl)
        {
            sh = sl = 0;
            int n = _recent.Count;
            if (n < 5) return;
            var m = _recent[n - 3];
            if (m.HighTick >= _recent[n - 1].HighTick && m.HighTick >= _recent[n - 2].HighTick
                && m.HighTick >= _recent[n - 4].HighTick && m.HighTick >= _recent[n - 5].HighTick) sh = m.HighTick;
            if (m.LowTick <= _recent[n - 1].LowTick && m.LowTick <= _recent[n - 2].LowTick
                && m.LowTick <= _recent[n - 4].LowTick && m.LowTick <= _recent[n - 5].LowTick) sl = m.LowTick;
        }

        // ---------------------------------------------------------------------------- signal pipeline
        private void EvaluateSignals(OfBar bar, double ctrl, bool strongVol, int absScore, Side absorbedSide, ProfileResult refP)
        {
            if (!refP.Valid || refP.Vah <= refP.Val) return;
            long vah = refP.Vah, val = refP.Val, poc = refP.Poc;
            long buf = _cfg.ValueBufferTicks;

            // ---- Setup A: failed-auction reversals at value extremes -------------------------------
            if (_cfg.EnableReversal)
            {
                // long: below value
                if (bar.LowTick <= val + buf)
                {
                    if (!_belowActive) { _belowActive = true; _belowLow = bar.LowTick; _protectLo = bar.LowTick; _sellAggr = _absLo = false; _belowStart = bar.Index; }
                    _belowLow = Math.Min(_belowLow, bar.LowTick);
                    if (ctrl <= -_cfg.ControlFrac && strongVol && bar.LowTick <= _belowLow) { _sellAggr = true; _protectLo = Math.Min(_protectLo, bar.LowTick); }
                    if (_sellAggr && _havePrev && bar.LowTick >= _pLow && bar.CloseTick >= bar.Mid) { _absLo = true; _protectLo = Math.Min(_protectLo, bar.LowTick); }
                    if (_absLo && ctrl >= _cfg.ControlFrac && bar.Up && _havePrev && bar.CloseTick > _pHigh && bar.CloseTick > _belowLow)
                    {
                        EmitReversal(bar, Side.Long, poc, vah, val, absScore, "LONG REVERSAL · failed auction below value");
                        _belowActive = false;
                    }
                }
                else if (_belowActive && bar.CloseTick > val + buf) _belowActive = false;
                if (_belowActive && bar.Index - _belowStart > _cfg.MaxExcursionBars) _belowActive = false;

                // short: above value
                if (bar.HighTick >= vah - buf)
                {
                    if (!_aboveActive) { _aboveActive = true; _aboveHigh = bar.HighTick; _protectHi = bar.HighTick; _buyAggr = _absHi = false; _aboveStart = bar.Index; }
                    _aboveHigh = Math.Max(_aboveHigh, bar.HighTick);
                    if (ctrl >= _cfg.ControlFrac && strongVol && bar.HighTick >= _aboveHigh) { _buyAggr = true; _protectHi = Math.Max(_protectHi, bar.HighTick); }
                    if (_buyAggr && _havePrev && bar.HighTick <= _pHigh && bar.CloseTick <= bar.Mid) { _absHi = true; _protectHi = Math.Max(_protectHi, bar.HighTick); }
                    if (_absHi && ctrl <= -_cfg.ControlFrac && bar.Down && _havePrev && bar.CloseTick < _pLow && bar.CloseTick < _aboveHigh)
                    {
                        EmitReversal(bar, Side.Short, poc, vah, val, absScore, "SHORT REVERSAL · failed auction above value");
                        _aboveActive = false;
                    }
                }
                else if (_aboveActive && bar.CloseTick < vah - buf) _aboveActive = false;
                if (_aboveActive && bar.Index - _aboveStart > _cfg.MaxExcursionBars) _aboveActive = false;
            }

            // ---- Setup B: momentum / breakout with acceptance --------------------------------------
            if (_cfg.EnableMomentum && _orDone && _orInit)
            {
                long third = (long)(bar.Range * 0.34);
                bool upAcc = bar.CloseTick > _orHi && bar.BodyFrac >= 0.5 && bar.CloseTick >= bar.HighTick - third && ctrl >= _cfg.ControlFrac && strongVol;
                bool dnAcc = bar.CloseTick < _orLo && bar.BodyFrac >= 0.5 && bar.CloseTick <= bar.LowTick + third && ctrl <= -_cfg.ControlFrac && strongVol;
                if (upAcc) EmitMomentum(bar, Side.Long, poc, vah, val, "LONG MOMENTUM · breakout accepted");
                if (dnAcc) EmitMomentum(bar, Side.Short, poc, vah, val, "SHORT MOMENTUM · breakout accepted");
            }

            // ---- Setup C: reload continuation at reload zones --------------------------------------
            if (_cfg.EnableReload)
            {
                foreach (var z in _zones.Active)
                {
                    if (z.Kind == ZoneKind.ReloadSell && bar.HighTick >= z.Lo && bar.CloseTick < z.RefTick && ctrl <= -_cfg.ControlFrac && strongVol)
                        EmitReload(bar, Side.Short, z, poc, val, "SHORT RELOAD · sellers re-entered");
                    else if (z.Kind == ZoneKind.ReloadBuy && bar.LowTick <= z.Hi && bar.CloseTick > z.RefTick && ctrl >= _cfg.ControlFrac && strongVol)
                        EmitReload(bar, Side.Long, z, poc, vah, "LONG RELOAD · buyers re-entered");
                }
            }

            // ---- Setup D: range fades at balance boundaries ----------------------------------------
            if (_cfg.EnableRange && _balance.State == MarketState.Balance)
            {
                if (bar.LowTick <= val + buf && absorbedSide == Side.Short && ctrl >= _cfg.ControlFrac && bar.Up)
                    EmitRange(bar, Side.Long, poc, vah, val, "LONG RANGE · trapped sellers at VAL");
                else if (bar.HighTick >= vah - buf && absorbedSide == Side.Long && ctrl <= -_cfg.ControlFrac && bar.Down)
                    EmitRange(bar, Side.Short, poc, vah, val, "SHORT RANGE · trapped buyers at VAH");
            }
        }

        // ---------------------------------------------------------------------------- emit helpers (scoring)
        private ScoreEngine BuildScore(Side side, string zoneReason, bool aggression, int absScore, bool trap, bool priceConf, long entry)
        {
            var sc = new ScoreEngine();
            // bias alignment
            var b = _auction.DailyBias;
            if ((side == Side.Long && b == Bias.Bullish) || (side == Side.Short && b == Bias.Bearish)) sc.Add("daily bias aligned", 20);
            else if (b == Bias.Neutral) sc.Add("daily bias neutral", 5);
            // level
            sc.Add(zoneReason, 15);
            // long-term auction
            var lt = _auction.LongTermAuction;
            if ((side == Side.Long && lt == Bias.Bullish) || (side == Side.Short && lt == Bias.Bearish)) sc.Add("long-term auction aligned", 15);
            else if (lt == Bias.Neutral) sc.Add("long-term auction neutral", 5);
            // aggression / absorption / trap / price
            if (aggression) sc.Add(side == Side.Long ? "aggressive buyers in control" : "aggressive sellers in control", 15);
            if (absScore >= _cfg.AbsorptionStrong) sc.Add("strong absorption (" + absScore + ")", 10);
            else if (absScore >= _cfg.AbsorptionModerate) sc.Add("moderate absorption (" + absScore + ")", 6);
            if (trap) sc.Add("trapped participants / failed auction", 10);
            if (priceConf) sc.Add("price confirms (candle body)", 10);
            // vwap
            if (_vwap.Valid)
            {
                double px = (double)entry * _tick;
                if (side == Side.Long && px >= _vwap.Value) sc.Add("above VWAP", 5);
                else if (side == Side.Short && px <= _vwap.Value) sc.Add("below VWAP", 5);
            }
            // composite context
            if (_cfg.UseComposite && _auction.Composite.Valid)
            {
                var c = _auction.Composite;
                if (side == Side.Long && entry <= c.Val) sc.Add("below composite value (discount)", 5);
                else if (side == Side.Short && entry >= c.Vah) sc.Add("above composite value (premium)", 5);
            }
            return sc;
        }

        private void EmitReversal(OfBar bar, Side side, long poc, long vah, long val, int absScore, string label)
        {
            long entry = bar.CloseTick;
            long stop = side == Side.Long ? _protectLo - _cfg.StopBufferTicks : _protectHi + _cfg.StopBufferTicks;
            long target = side == Side.Long ? TargetUp(entry, poc, vah, stop) : TargetDown(entry, poc, val, stop);
            var sc = BuildScore(side, side == Side.Long ? "at value area low" : "at value area high", true, absScore, true, true, entry);
            Emit(bar, side, side == Side.Long ? SignalKind.LongReversal : SignalKind.ShortReversal, entry, stop, target, label, sc);
        }

        private void EmitMomentum(OfBar bar, Side side, long poc, long vah, long val, string label)
        {
            long entry = bar.CloseTick;
            long stop = side == Side.Long ? Math.Min(bar.LowTick, _orHi) - _cfg.StopBufferTicks : Math.Max(bar.HighTick, _orLo) + _cfg.StopBufferTicks;
            long move = _orHi - _orLo > 0 ? _orHi - _orLo : (long)(bar.Range * 3);
            long target = side == Side.Long ? entry + move : entry - move;
            var sc = BuildScore(side, "opening-range breakout", true, 0, false, true, entry);
            Emit(bar, side, side == Side.Long ? SignalKind.LongMomentum : SignalKind.ShortMomentum, entry, stop, target, label, sc);
        }

        private void EmitReload(OfBar bar, Side side, Zone z, long poc, long valOrVah, string label)
        {
            long entry = bar.CloseTick;
            long stop = side == Side.Long ? z.Lo - _cfg.StopBufferTicks : z.Hi + _cfg.StopBufferTicks;
            long target = side == Side.Long ? TargetUp(entry, poc, valOrVah, stop) : TargetDown(entry, poc, valOrVah, stop);
            var sc = BuildScore(side, "reload level", true, 0, true, true, entry);
            Emit(bar, side, side == Side.Long ? SignalKind.LongReload : SignalKind.ShortReload, entry, stop, target, label, sc);
        }

        private void EmitRange(OfBar bar, Side side, long poc, long vah, long val, string label)
        {
            long entry = bar.CloseTick;
            long stop = side == Side.Long ? bar.LowTick - _cfg.StopBufferTicks : bar.HighTick + _cfg.StopBufferTicks;
            long target = poc; // range fades target the POC magnet
            var sc = BuildScore(side, side == Side.Long ? "range low (VAL)" : "range high (VAH)", true, 0, true, true, entry);
            Emit(bar, side, side == Side.Long ? SignalKind.LongRange : SignalKind.ShortRange, entry, stop, target, label, sc);
        }

        private void Emit(OfBar bar, Side side, SignalKind kind, long entry, long stop, long target, string label, ScoreEngine sc)
        {
            if (bar.Index - _lastSignalBar < _cfg.MinBarsBetweenSignals) return;
            if (side == Side.Long && stop >= entry) stop = entry - Math.Max(1, _cfg.StopBufferTicks) * 2;
            if (side == Side.Short && stop <= entry) stop = entry + Math.Max(1, _cfg.StopBufferTicks) * 2;

            double risk = Math.Abs(entry - stop), reward = Math.Abs(target - entry);
            double rr = risk > 0 ? reward / risk : 0;
            if (rr < _cfg.MinRewardRisk) return;
            if (sc.Total < _cfg.MinScoreToShow) return;

            _signals.Add(new SignalMark
            {
                Bar = bar.Index, EntryTick = entry, StopTick = stop, TargetTick = target,
                HighTick = bar.HighTick, LowTick = bar.LowTick, Side = side, Kind = kind,
                Score = sc.Total, Quality = sc.Quality, Rr = rr, Breakdown = sc.Lines(label)
            });
            if (_signals.Count > 200) _signals.RemoveAt(0);
            _lastSignalBar = bar.Index;
        }

        private long TargetUp(long e, long poc, long vah, long stop)
        {
            if (poc > e + 1) return poc;
            if (vah > e + 1) return vah;
            return e + Math.Max(1, e - stop) * 2;
        }

        private long TargetDown(long e, long poc, long val, long stop)
        {
            if (poc < e - 1) return poc;
            if (val < e - 1) return val;
            return e - Math.Max(1, stop - e) * 2;
        }

        // ---------------------------------------------------------------------------- snapshot
        public DisplaySnapshot Snapshot()
        {
            var snap = new DisplaySnapshot
            {
                TickSize = _tick,
                PriceDecimals = _decimals,
                DailyBias = _auction.DailyBias,
                LongTermAuction = _auction.LongTermAuction,
                State = _balance.State,
                VwapValid = _vwap.Valid,
                Vwap = _vwap.Points.ToArray(),
                Signals = _signals.ToArray(),
                Zones = _zones.Active.ToArray(),
                LiveDelta = _liveDelta, LiveBuy = _liveBuy, LiveSell = _liveSell, Power = _power, LiveAbsorption = _liveAbs
            };

            // reference + developing levels
            ProfileResult refP = _sessions.Count > 0 ? _sessions[_sessions.Count - 1].Result : _dev.Result;
            var levels = new List<LevelLine>();
            if (refP.Valid)
            {
                levels.Add(new LevelLine { Tick = refP.Vah, Kind = ZoneKind.Vah, Label = "VAH " + P(refP.Vah) });
                levels.Add(new LevelLine { Tick = refP.Poc, Kind = ZoneKind.Poc, Label = "POC " + P(refP.Poc) });
                levels.Add(new LevelLine { Tick = refP.Val, Kind = ZoneKind.Val, Label = "VAL " + P(refP.Val) });
                snap.RefPoc = refP.Poc; snap.RefVah = refP.Vah; snap.RefVal = refP.Val;
            }
            if (_dev.Result.Valid)
            {
                snap.DevPoc = _dev.Result.Poc; snap.DevVah = _dev.Result.Vah; snap.DevVal = _dev.Result.Val;
                var hvn = new List<long>(); var lvn = new List<long>();
                VolumeProfileAnalyzer.DetectNodes(_dev.Vol, _dev.Result.Lo, _dev.Result.Hi, _cfg.HvnFactor, _cfg.LvnFactor, hvn, lvn);
                foreach (var t in hvn) levels.Add(new LevelLine { Tick = t, Kind = ZoneKind.Hvn, Label = "HVN " + P(t) });
                foreach (var t in lvn) levels.Add(new LevelLine { Tick = t, Kind = ZoneKind.Lvn, Label = "LVN " + P(t) });
            }
            foreach (var lv in _buyLvls) levels.Add(new LevelLine { Tick = lv.Tick, Kind = ZoneKind.BuyAggression, Label = "BUY AGG " + P(lv.Tick) });
            foreach (var lv in _sellLvls) levels.Add(new LevelLine { Tick = lv.Tick, Kind = ZoneKind.SellAggression, Label = "SELL AGG " + P(lv.Tick) });
            if (_cfg.UseComposite && _auction.Composite.Valid)
            {
                levels.Add(new LevelLine { Tick = _auction.Composite.Poc, Kind = ZoneKind.CompositePoc, Label = "cPOC " + P(_auction.Composite.Poc) });
                levels.Add(new LevelLine { Tick = _auction.Composite.Vah, Kind = ZoneKind.CompositeVah, Label = "cVAH " + P(_auction.Composite.Vah) });
                levels.Add(new LevelLine { Tick = _auction.Composite.Val, Kind = ZoneKind.CompositeVal, Label = "cVAL " + P(_auction.Composite.Val) });
            }
            snap.Levels = levels.ToArray();

            // scored key zones, strongest first
            var kz = new List<KeyZone>(_keyZones);
            kz.Sort((a, b) => b.Score.CompareTo(a.Score));
            snap.KeyZones = kz.ToArray();

            snap.StateNote = DescribeState();
            snap.DebugLines = BuildDebug(refP);
            return snap;
        }

        private string DescribeState()
        {
            string s = _balance.State.ToString();
            if (_belowActive) s += _absLo ? " · sellers absorbed below value" : _sellAggr ? " · sellers aggressive below value" : " · testing below value";
            else if (_aboveActive) s += _absHi ? " · buyers absorbed above value" : _buyAggr ? " · buyers aggressive above value" : " · testing above value";
            if (_balance.HasCompression) s += " · compression " + P(_balance.CompLo) + "-" + P(_balance.CompHi);
            return s;
        }

        private string[] BuildDebug(ProfileResult refP)
        {
            return new[]
            {
                "Daily bias:       " + _auction.DailyBias,
                "Long-term:        " + _auction.LongTermAuction,
                "Market state:     " + _balance.State,
                "Stacked groups:   " + _auction.StackedGroups.Count + (_auction.StackedPocDir != 0 ? (_auction.StackedPocDir < 0 ? " (POC↓)" : " (POC↑)") : ""),
                "Ref VA:           " + (refP.Valid ? P(refP.Val) + " / " + P(refP.Poc) + " / " + P(refP.Vah) : "n/a"),
                "Dev VA:           " + (_dev.Result.Valid ? P(_dev.Result.Val) + " / " + P(_dev.Result.Poc) + " / " + P(_dev.Result.Vah) : "n/a"),
                "Composite POC:    " + (_auction.Composite.Valid ? P(_auction.Composite.Poc) : "n/a"),
                "VWAP:             " + (_vwap.Valid ? P(ToTick((decimal)_vwap.Value)) : "n/a"),
                "Live delta:       " + _liveDelta.ToString("0"),
                "Buy/Sell vol:     " + _liveBuy.ToString("0") + " / " + _liveSell.ToString("0"),
                "Power:            " + (_power * 100).ToString("0") + "%",
                "Absorption:       " + _liveAbs,
                "Sessions stored:  " + _sessions.Count,
                "Active zones:     " + _zones.Active.Count,
                "Signals:          " + _signals.Count
            };
        }

        private static double Clamp(double lo, double hi, double v) => v < lo ? lo : v > hi ? hi : v;

        private static int Decimals(double tick)
        {
            int d = 0; var t = (decimal)tick;
            while (t != Math.Truncate(t) && d < 10) { t *= 10; d++; }
            return d;
        }
    }

    /// <summary>Keeps zones alive until price invalidates them; caps how many stay visible.</summary>
    public sealed class ZoneList
    {
        public readonly List<Zone> Active = new();

        public void Add(Zone z)
        {
            Active.Add(z);
            if (Active.Count > 120) Active.RemoveAt(0);
        }

        public void AddUnique(Zone z)
        {
            foreach (var e in Active)
                if (e.Kind == z.Kind && Math.Abs(e.RefTick - z.RefTick) <= 1 && e.EndBar < 0)
                    return;
            Add(z);
        }

        public void Expire(OfBar bar, ProfileResult refP)
        {
            foreach (var z in Active)
            {
                if (z.EndBar >= 0) continue;
                // a trap/rejection zone is invalidated once price closes decisively through its anchor in the "wrong" way
                if (z.Kind == ZoneKind.TrappedBuyers && bar.CloseTick > z.Hi) z.EndBar = bar.Index;
                else if (z.Kind == ZoneKind.TrappedSellers && bar.CloseTick < z.Lo) z.EndBar = bar.Index;
            }
            // prune long-dead zones
            for (int i = Active.Count - 1; i >= 0; i--)
                if (Active[i].EndBar >= 0 && bar.Index - Active[i].EndBar > 200)
                    Active.RemoveAt(i);
        }
    }
}
