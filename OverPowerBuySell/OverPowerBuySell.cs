// =====================================================================================================
//  OverPowerBuySell — ATAS custom indicator (C#, .NET 8/10, classic ATAS + ATAS X)
//
//  A focused BUY / SELL SIGNAL indicator that trades the "who is in control?" order-flow model:
//
//      Markets are a continuous liquidity auction. Price does not move because there are more buyers
//      than sellers — there are always equal numbers on both sides — it moves because one side is
//      MORE AGGRESSIVE (accepts worse prices to get filled now). The only decision that matters is:
//
//                    Is the aggressive side being ACCEPTED (continuation) or ABSORBED (reversal)?
//
//  This indicator establishes fair value from a session volume profile (POC / VAH / VAL), watches
//  price at the value extremes and at the opening range, measures which side is "over-powering" from
//  order-flow delta / footprint, and fires a confirmed signal only when one side clearly takes control.
//
//  It implements the three setups from the strategy:
//     1. Failed auction BELOW value  -> reversal LONG   (sellers aggressive, absorbed, buyers reclaim)
//     2. Failed auction ABOVE value  -> reversal SHORT  (buyers aggressive,  absorbed, sellers reclaim)
//     3. Breakout WITH ACCEPTANCE    -> continuation    (body close beyond the level, aggressor accepted)
//
//  Signals fire on CLOSED bars only (confirmation, never anticipation). Every signal carries a logical
//  stop (where the idea is invalidated) and a value/measured-move target, and an R:R estimate.
//
//  It is a decision-support tool: it marks where control shifts. It does not place orders or guarantee
//  that a level holds.
// =====================================================================================================
#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using ATAS.Indicators;

using OFT.Rendering.Context;
using OFT.Rendering.Tools;

using Utils.Common.Logging;

using Color = System.Drawing.Color;
#if CROSS_PLATFORM
// ATAS X native build (dotnet build -p:Platform=Cross): the platform uses System.Drawing.Color.
using CrossColor = System.Drawing.Color;
#else
// Classic ATAS build (default). The same DLL also loads in ATAS X, which converts WPF colors automatically.
using CrossColor = System.Windows.Media.Color;
#endif
using Rectangle = System.Drawing.Rectangle;

namespace OverPowerOF
{
    public enum ValueReference
    {
        /// <summary>Reference value area = previous completed session (stable, known at the open — recommended).</summary>
        PreviousSession,
        /// <summary>Reference value area = the developing (current) session, updated live.</summary>
        DevelopingSession
    }

    public enum BreakoutMode
    {
        /// <summary>Continuation breakouts are measured against the opening-range high/low (ORB).</summary>
        OpeningRange,
        /// <summary>Continuation breakouts are measured against the previous session's high/low.</summary>
        PreviousSessionExtreme
    }

    public enum DashCorner { TopRight, TopLeft, BottomRight, BottomLeft }

    [DisplayName("Over Power Buy/Sell")]
    [Display(Name = "Over Power Buy/Sell",
        Description = "Order-flow BUY/SELL signals: trades value extremes and opening-range breakouts only when one side over-powers the other (aggression accepted = continuation, absorbed = reversal).")]
    public class OverPowerBuySell : Indicator
    {
        // ------------------------------------------------------------------ signal record
        private struct Sig
        {
            public int Bar;
            public long EntryTick, StopTick, TargetTick, HighTick, LowTick;
            public bool Buy;
            public string Label;
            public double Rr;
            public double Power;
        }

        #region Fields

        private readonly object _sync = new();

        private decimal _tick = 0.01m;
        private int _priceDecimals = 2;
        private int _lastBar = -1;
        private volatile bool _historyLoaded;

        // reference value area (in ticks)
        private long _refPOC, _refVAH, _refVAL;
        private bool _haveRef;

        // developing session volume profile
        private readonly Dictionary<long, double> _devProfile = new();
        private long _devHigh = long.MinValue, _devLow = long.MaxValue;
        private long _prevHigh, _prevLow;
        private bool _havePrevHL;
        private DateTime _sessionStart;
        private bool _sessionInit;

        // opening range
        private long _orHigh, _orLow;
        private bool _orInit, _orComplete;

        // rolling average volume (EMA) for "strong participation"
        private double _avgVol;

        // previous closed bar (ticks)
        private long _pHigh, _pLow, _pClose;
        private bool _havePrev;

        // failed-auction excursion state — below value (long) / above value (short)
        private bool _belowActive;
        private long _belowLow, _protectLo;
        private bool _sellAggr, _absorbedLo;
        private int _belowStartBar;

        private bool _aboveActive;
        private long _aboveHigh, _protectHi;
        private bool _buyAggr, _absorbedHi;
        private int _aboveStartBar;

        // breakout latches
        private bool _brokeUp, _brokeDown;
        private int _lastSignalBar = -10000;

        // live dashboard state
        private double _power;            // current forming-bar control -1..+1
        private long _liveVAH, _liveVAL, _livePOC;
        private string _state = "waiting for a completed session";
        private int _sessLongs, _sessShorts;

        private readonly List<Sig> _signals = new();

        // rendering
        private RenderFont _font = new("Arial", 9);
        private RenderFont _small = new("Arial", 8);
        private int _fontSize = 9;

        // alert colors
        // AddAlert (classic ATAS) takes System.Drawing.Color, not the WPF color type.
        private readonly Color _alertBuyBg = Color.FromArgb(255, 0, 120, 60);
        private readonly Color _alertSellBg = Color.FromArgb(255, 140, 20, 30);
        private readonly Color _alertFg = Color.FromArgb(255, 245, 245, 245);

        #endregion

        #region ctor

        public OverPowerBuySell()
            : base(true)
        {
            DenyToChangePanel = true;
            DataSeries[0].IsHidden = true;
            ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;

            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);
            DrawAbovePrice = true;
        }

        #endregion

        // =================================================================================================
        #region Settings

        // 01. Value area -------------------------------------------------------------------------------
        [Display(Name = "Value reference", GroupName = "01. Value Area", Order = 10,
            Description = "Which volume profile defines fair value. Previous session is stable and known at the open.")]
        public ValueReference ValueRef { get; set; } = ValueReference.PreviousSession;

        [Range(50, 90)]
        [Display(Name = "Value area %", GroupName = "01. Value Area", Order = 20,
            Description = "Percent of session volume that defines the value area (VAH/VAL). 70% is standard.")]
        public int ValueAreaPct { get; set; } = 70;

        [Range(1, 120)]
        [Display(Name = "Opening range (min)", GroupName = "01. Value Area", Order = 30,
            Description = "Length of the opening range used for breakout confirmation.")]
        public int OpeningRangeMinutes { get; set; } = 15;

        [Range(0, 20)]
        [Display(Name = "Value tolerance (ticks)", GroupName = "01. Value Area", Order = 40,
            Description = "How close to VAH/VAL price must trade to count as 'at the value extreme'.")]
        public int ValueBufferTicks { get; set; } = 2;

        // 02. Order flow -------------------------------------------------------------------------------
        [Display(Name = "Control threshold", GroupName = "02. Order Flow", Order = 10,
            Description = "Net delta / volume needed to call one side 'in control' (0.35 = 35% one-sided).")]
        public decimal ControlFrac { get; set; } = 0.35m;

        [Display(Name = "Absorption threshold", GroupName = "02. Order Flow", Order = 20,
            Description = "One-sided delta that counts as heavy aggression when price fails to follow (absorption).")]
        public decimal AbsorbFrac { get; set; } = 0.30m;

        [Display(Name = "Control (full power)", GroupName = "02. Order Flow", Order = 30,
            Description = "Delta/volume ratio that scores as 100% control on the power meter.")]
        public decimal ControlStrong { get; set; } = 0.60m;

        [Display(Name = "Volume factor", GroupName = "02. Order Flow", Order = 40,
            Description = "A bar's volume must exceed (average x this) to count as strong participation.")]
        public decimal VolFactor { get; set; } = 1.2m;

        [Display(Name = "Acceptance body %", GroupName = "02. Order Flow", Order = 50,
            Description = "Minimum body/range fraction for a breakout to count as accepted (activity in the body, not the wick).")]
        public decimal BodyFrac { get; set; } = 0.50m;

        // 03. Setups -----------------------------------------------------------------------------------
        [Display(Name = "Reversals (failed auctions)", GroupName = "03. Setups", Order = 10)]
        public bool EnableReversals { get; set; } = true;

        [Display(Name = "Breakouts (acceptance)", GroupName = "03. Setups", Order = 20)]
        public bool EnableBreakouts { get; set; } = true;

        [Display(Name = "Breakout reference", GroupName = "03. Setups", Order = 30,
            Description = "The level a continuation breakout must be accepted beyond.")]
        public BreakoutMode BreakoutRef { get; set; } = BreakoutMode.OpeningRange;

        [Range(1, 100)]
        [Display(Name = "Min power score", GroupName = "03. Setups", Order = 40,
            Description = "Reject signals below this over-power score (0-100).")]
        public int MinPower { get; set; } = 55;

        [Range(3, 500)]
        [Display(Name = "Max excursion bars", GroupName = "03. Setups", Order = 50,
            Description = "Abandon a failed-auction watch if it has not confirmed within this many bars.")]
        public int MaxExcursionBars { get; set; } = 40;

        [Range(0, 100)]
        [Display(Name = "Min bars between signals", GroupName = "03. Setups", Order = 60)]
        public int MinBarsBetween { get; set; } = 3;

        // 04. Risk -------------------------------------------------------------------------------------
        [Range(0, 50)]
        [Display(Name = "Stop buffer (ticks)", GroupName = "04. Risk", Order = 10,
            Description = "Padding beyond the invalidation point for the stop.")]
        public int StopBufferTicks { get; set; } = 2;

        [Display(Name = "Min reward:risk", GroupName = "04. Risk", Order = 20,
            Description = "Reject signals whose target/stop ratio is below this.")]
        public decimal MinRR { get; set; } = 1.2m;

        // 05. Alerts -----------------------------------------------------------------------------------
        [Display(Name = "Enable alerts", GroupName = "05. Alerts", Order = 10)]
        public bool UseAlerts { get; set; } = true;

        [Display(Name = "Alert sound", GroupName = "05. Alerts", Order = 20,
            Description = "Sound file name from the ATAS Sounds folder (e.g. alert1).")]
        public string AlertSound { get; set; } = "alert1";

        // 06. Appearance -------------------------------------------------------------------------------
        [Display(Name = "Show dashboard", GroupName = "06. Appearance", Order = 10)]
        public bool ShowDashboard { get; set; } = true;

        [Display(Name = "Show value lines", GroupName = "06. Appearance", Order = 20)]
        public bool ShowValueLines { get; set; } = true;

        [Display(Name = "Show opening range", GroupName = "06. Appearance", Order = 30)]
        public bool ShowOpeningRange { get; set; } = true;

        [Display(Name = "Show signal stop/target", GroupName = "06. Appearance", Order = 40)]
        public bool ShowSignalLevels { get; set; } = true;

        [Display(Name = "Dashboard corner", GroupName = "06. Appearance", Order = 50)]
        public DashCorner Corner { get; set; } = DashCorner.TopRight;

        [Range(7, 20)]
        [Display(Name = "Font size", GroupName = "06. Appearance", Order = 60)]
        public int FontSize { get; set; } = 9;

        [Display(Name = "Buy color", GroupName = "06. Appearance", Order = 70)]
        public CrossColor BuyColor { get; set; } = CrossColor.FromArgb(255, 0, 200, 83);

        [Display(Name = "Sell color", GroupName = "06. Appearance", Order = 80)]
        public CrossColor SellColor { get; set; } = CrossColor.FromArgb(255, 255, 82, 82);

        [Display(Name = "VAH color", GroupName = "06. Appearance", Order = 90)]
        public CrossColor VahColor { get; set; } = CrossColor.FromArgb(255, 239, 83, 80);

        [Display(Name = "VAL color", GroupName = "06. Appearance", Order = 100)]
        public CrossColor ValColor { get; set; } = CrossColor.FromArgb(255, 38, 166, 154);

        [Display(Name = "POC color", GroupName = "06. Appearance", Order = 110)]
        public CrossColor PocColor { get; set; } = CrossColor.FromArgb(255, 255, 152, 0);

        [Display(Name = "Opening range color", GroupName = "06. Appearance", Order = 120)]
        public CrossColor OrColor { get; set; } = CrossColor.FromArgb(255, 120, 144, 156);

        [Display(Name = "Panel background", GroupName = "06. Appearance", Order = 130)]
        public CrossColor PanelBg { get; set; } = CrossColor.FromArgb(180, 18, 18, 22);

        [Display(Name = "Panel text", GroupName = "06. Appearance", Order = 140)]
        public CrossColor PanelText { get; set; } = CrossColor.FromArgb(255, 235, 235, 235);

        #endregion

        // =================================================================================================
        #region Calculation

        protected override void OnCalculate(int bar, decimal value)
        {
            if (InstrumentInfo is null)
                return;

            _tick = InstrumentInfo.TickSize > 0 ? InstrumentInfo.TickSize : 0.01m;
            _priceDecimals = DecimalsFromTick(_tick);

            if (bar == 0 || bar < _lastBar)
                ResetAll();

            _historyLoaded = bar >= CurrentBar - 1;

            try
            {
                lock (_sync)
                {
                    // a bar has advanced -> the previous bar is now closed -> process it once
                    if (bar != _lastBar)
                    {
                        if (_lastBar >= 0)
                            ProcessClosed(_lastBar);

                        _lastBar = bar;
                    }

                    // live snapshot for the dashboard (forming bar, no signal)
                    var c = GetCandle(bar);
                    var vol = (double)c.Volume;
                    _power = vol > 0 ? Clamp(-1, 1, (double)c.Delta / vol) : 0;

                    if (GetReference(out var poc, out var vah, out var val))
                    {
                        _livePOC = poc;
                        _liveVAH = vah;
                        _liveVAL = val;
                    }
                }
            }
            catch (Exception ex)
            {
                this.LogError("OverPowerBuySell: " + ex.Message, ex);
            }
        }

        /// <summary>Processes one CLOSED bar: profile, session/opening-range bookkeeping, and the three setups.</summary>
        private void ProcessClosed(int b)
        {
            if (b < 0)
                return;

            var c = GetCandle(b);
            long o = ToTick(c.Open), h = ToTick(c.High), l = ToTick(c.Low), cl = ToTick(c.Close);
            double vol = (double)c.Volume;
            double delta = (double)c.Delta;

            if (!_sessionInit || IsNewSession(b))
                RollSession(b);

            AddToProfile(c, h, l, vol);
            _devHigh = _devHigh == long.MinValue ? h : Math.Max(_devHigh, h);
            _devLow = _devLow == long.MaxValue ? l : Math.Min(_devLow, l);

            UpdateOpeningRange(c, h, l);

            _avgVol = _avgVol <= 0 ? vol : _avgVol + (vol - _avgVol) * 0.1;
            bool strongVol = vol > 0 && vol > _avgVol * (double)VolFactor;
            double ctrl = vol > 0 ? delta / vol : 0; // -1..+1

            bool haveRef = GetReference(out var poc, out var vah, out var val);
            if (haveRef)
            {
                _livePOC = poc;
                _liveVAH = vah;
                _liveVAL = val;
            }

            UpdateStateText(haveRef, cl, vah, val);

            if (EnableReversals && haveRef && vah > val && val > 0)
            {
                EvaluateReversalLong(b, o, h, l, cl, ctrl, strongVol, poc, vah, val);
                EvaluateReversalShort(b, o, h, l, cl, ctrl, strongVol, poc, vah, val);
            }

            if (EnableBreakouts)
                EvaluateBreakout(b, o, h, l, cl, ctrl, strongVol, poc, vah, val);

            _pHigh = h;
            _pLow = l;
            _pClose = cl;
            _havePrev = true;
        }

        private void RollSession(int b)
        {
            // finalise the just-completed session into the reference value area
            if (_devProfile.Count > 0)
            {
                ComputeValueArea(out _refPOC, out _refVAH, out _refVAL);
                _haveRef = _refVAH > _refVAL;

                if (_devHigh != long.MinValue)
                {
                    _prevHigh = _devHigh;
                    _prevLow = _devLow;
                    _havePrevHL = true;
                }
            }

            _devProfile.Clear();
            _devHigh = long.MinValue;
            _devLow = long.MaxValue;
            _orInit = false;
            _orComplete = false;
            _brokeUp = _brokeDown = false;
            _belowActive = _aboveActive = false;
            _sellAggr = _buyAggr = _absorbedLo = _absorbedHi = false;
            _sessLongs = _sessShorts = 0;
            _sessionStart = GetCandle(b).Time;
            _sessionInit = true;
        }

        private void AddToProfile(IndicatorCandle c, long h, long l, double vol)
        {
            var levels = c.GetAllPriceLevels();

            if (levels != null)
            {
                bool any = false;

                foreach (var pv in levels)
                {
                    if (pv == null)
                        continue;

                    double v = (double)pv.Volume;
                    if (v <= 0)
                        continue;

                    any = true;
                    long t = ToTick(pv.Price);
                    _devProfile[t] = GetVol(t) + v;
                }

                if (any)
                    return;
            }

            // fallback for charts without footprint: spread the bar's volume across its range
            long span = h - l + 1;

            if (span <= 0 || span > 5000)
            {
                _devProfile[ToTick(c.Close)] = GetVol(ToTick(c.Close)) + vol;
                return;
            }

            double per = vol / span;
            for (long t = l; t <= h; t++)
                _devProfile[t] = GetVol(t) + per;
        }

        private void UpdateOpeningRange(IndicatorCandle c, long h, long l)
        {
            if (_orComplete)
                return;

            if (!_orInit)
            {
                _orHigh = h;
                _orLow = l;
                _orInit = true;
            }
            else
            {
                _orHigh = Math.Max(_orHigh, h);
                _orLow = Math.Min(_orLow, l);
            }

            if ((c.Time - _sessionStart).TotalMinutes >= OpeningRangeMinutes)
                _orComplete = true;
        }

        // -------------------------------------------------------------------------- Setup 1: failed auction below value
        private void EvaluateReversalLong(int b, long o, long h, long l, long cl,
            double ctrl, bool strongVol, long poc, long vah, long val)
        {
            long buf = ValueBufferTicks;
            bool atBelow = l <= val + buf;

            if (atBelow)
            {
                if (!_belowActive)
                {
                    _belowActive = true;
                    _belowLow = l;
                    _protectLo = l;
                    _sellAggr = false;
                    _absorbedLo = false;
                    _belowStartBar = b;
                }

                _belowLow = Math.Min(_belowLow, l);

                // sellers aggressive at the lows
                if (ctrl <= -(double)ControlFrac && strongVol && l <= _belowLow)
                {
                    _sellAggr = true;
                    _protectLo = Math.Min(_protectLo, l);
                }

                // absorption: aggression present, but price fails to extend down and closes back up
                bool noNewLow = _havePrev && l >= _pLow;
                bool closeUp = cl >= (h + l) / 2;
                if (_sellAggr && noNewLow && closeUp)
                {
                    _absorbedLo = true;
                    _protectLo = Math.Min(_protectLo, l);
                }

                // confirmation: buyers take control and reclaim
                bool buyersControl = ctrl >= (double)ControlFrac && cl > o;
                bool reclaim = _havePrev && cl > _pHigh;
                if (_absorbedLo && buyersControl && reclaim && cl > _belowLow)
                {
                    long stop = _protectLo - StopBufferTicks;
                    long tgt = TargetUp(cl, poc, vah, stop);
                    double power = ScoreLong(ctrl, strongVol, 1.0);
                    FireSignal(b, true, cl, stop, tgt, h, l, "LONG · failed auction below value", power);
                    _belowActive = false;
                }
            }
            else if (_belowActive && cl > val + buf)
            {
                _belowActive = false; // returned to value without confirming
            }

            if (_belowActive && b - _belowStartBar > MaxExcursionBars)
                _belowActive = false;
        }

        // -------------------------------------------------------------------------- Setup 2: failed auction above value
        private void EvaluateReversalShort(int b, long o, long h, long l, long cl,
            double ctrl, bool strongVol, long poc, long vah, long val)
        {
            long buf = ValueBufferTicks;
            bool atAbove = h >= vah - buf;

            if (atAbove)
            {
                if (!_aboveActive)
                {
                    _aboveActive = true;
                    _aboveHigh = h;
                    _protectHi = h;
                    _buyAggr = false;
                    _absorbedHi = false;
                    _aboveStartBar = b;
                }

                _aboveHigh = Math.Max(_aboveHigh, h);

                if (ctrl >= (double)ControlFrac && strongVol && h >= _aboveHigh)
                {
                    _buyAggr = true;
                    _protectHi = Math.Max(_protectHi, h);
                }

                bool noNewHigh = _havePrev && h <= _pHigh;
                bool closeDn = cl <= (h + l) / 2;
                if (_buyAggr && noNewHigh && closeDn)
                {
                    _absorbedHi = true;
                    _protectHi = Math.Max(_protectHi, h);
                }

                bool sellersControl = ctrl <= -(double)ControlFrac && cl < o;
                bool reclaim = _havePrev && cl < _pLow;
                if (_absorbedHi && sellersControl && reclaim && cl < _aboveHigh)
                {
                    long stop = _protectHi + StopBufferTicks;
                    long tgt = TargetDown(cl, poc, val, stop);
                    double power = ScoreShort(ctrl, strongVol, 1.0);
                    FireSignal(b, false, cl, stop, tgt, h, l, "SHORT · failed auction above value", power);
                    _aboveActive = false;
                }
            }
            else if (_aboveActive && cl < vah - buf)
            {
                _aboveActive = false;
            }

            if (_aboveActive && b - _aboveStartBar > MaxExcursionBars)
                _aboveActive = false;
        }

        // -------------------------------------------------------------------------- Setup 3: breakout with acceptance
        private void EvaluateBreakout(int b, long o, long h, long l, long cl,
            double ctrl, bool strongVol, long poc, long vah, long val)
        {
            long refUp, refDn;

            if (BreakoutRef == BreakoutMode.OpeningRange)
            {
                if (!_orComplete || !_orInit)
                    return;

                refUp = _orHigh;
                refDn = _orLow;
            }
            else
            {
                if (!_havePrevHL)
                    return;

                refUp = _prevHigh;
                refDn = _prevLow;
            }

            double range = Math.Max(1, h - l);
            double bodyFrac = Math.Abs(cl - o) / range;
            long third = (long)(range * 0.34);

            // up breakout accepted: body closes above the level, in the upper third, buyers over-power
            bool upAccept = cl > refUp && bodyFrac >= (double)BodyFrac && cl >= h - third
                && ctrl >= (double)ControlFrac && strongVol;
            if (upAccept && !_brokeUp)
            {
                long stop = Math.Min(l, refUp) - StopBufferTicks;
                long tgt = cl + (refUp - refDn > 0 ? refUp - refDn : (long)(range * 3));
                double power = ScoreLong(ctrl, strongVol, Clamp01(bodyFrac));
                FireSignal(b, true, cl, stop, tgt, h, l, "LONG · breakout accepted", power);
                _brokeUp = true;
            }
            if (cl < refUp)
                _brokeUp = false; // re-arm when price returns inside

            // down breakout accepted
            bool dnAccept = cl < refDn && bodyFrac >= (double)BodyFrac && cl <= l + third
                && ctrl <= -(double)ControlFrac && strongVol;
            if (dnAccept && !_brokeDown)
            {
                long stop = Math.Max(h, refDn) + StopBufferTicks;
                long tgt = cl - (refUp - refDn > 0 ? refUp - refDn : (long)(range * 3));
                double power = ScoreShort(ctrl, strongVol, Clamp01(bodyFrac));
                FireSignal(b, false, cl, stop, tgt, h, l, "SHORT · breakout accepted", power);
                _brokeDown = true;
            }
            if (cl > refDn)
                _brokeDown = false;
        }

        private void FireSignal(int b, bool buy, long entry, long stop, long target,
            long high, long low, string label, double power)
        {
            if (b - _lastSignalBar < MinBarsBetween)
                return;

            // guarantee the stop sits on the invalidation side
            if (buy && stop >= entry)
                stop = entry - Math.Max(1, StopBufferTicks) * 2;
            if (!buy && stop <= entry)
                stop = entry + Math.Max(1, StopBufferTicks) * 2;

            double risk = Math.Abs(entry - stop);
            double reward = Math.Abs(target - entry);
            double rr = risk > 0 ? reward / risk : 0;

            if (rr < (double)MinRR || power < MinPower)
                return;

            _signals.Add(new Sig
            {
                Bar = b,
                EntryTick = entry,
                StopTick = stop,
                TargetTick = target,
                HighTick = high,
                LowTick = low,
                Buy = buy,
                Label = label,
                Rr = rr,
                Power = power
            });

            if (_signals.Count > 60)
                _signals.RemoveAt(0);

            _lastSignalBar = b;
            if (buy)
                _sessLongs++;
            else
                _sessShorts++;

            _state = (buy ? "BUY " : "SELL ") + label;

            if (_historyLoaded && b >= CurrentBar - 2 && UseAlerts)
            {
                string msg = $"{(buy ? "BUY" : "SELL")}  {label}  @ {P(entry)}  stop {P(stop)}  tgt {P(target)}  R:R {rr:0.0}  pwr {power:0}";
                AddAlert(AlertSound, InstrumentInfo?.Instrument ?? string.Empty, msg,
                    buy ? _alertBuyBg : _alertSellBg, _alertFg);
            }
        }

        #endregion

        // =================================================================================================
        #region Value-area helpers

        private bool GetReference(out long poc, out long vah, out long val)
        {
            poc = vah = val = 0;

            if (ValueRef == ValueReference.PreviousSession)
            {
                if (_haveRef)
                {
                    poc = _refPOC;
                    vah = _refVAH;
                    val = _refVAL;
                    return true;
                }

                if (_devProfile.Count > 20)
                {
                    ComputeValueArea(out poc, out vah, out val);
                    return vah > val;
                }

                return false;
            }

            if (_devProfile.Count > 20)
            {
                ComputeValueArea(out poc, out vah, out val);
                return vah > val;
            }

            if (_haveRef)
            {
                poc = _refPOC;
                vah = _refVAH;
                val = _refVAL;
                return true;
            }

            return false;
        }

        /// <summary>Standard volume value-area: POC + expansion to the heavier side until % of volume is covered.</summary>
        private void ComputeValueArea(out long poc, out long vah, out long val)
        {
            poc = 0;
            vah = 0;
            val = 0;

            if (_devProfile.Count == 0)
                return;

            double total = 0, best = -1;
            long bestTick = 0, lo = long.MaxValue, hi = long.MinValue;

            foreach (var kv in _devProfile)
            {
                total += kv.Value;

                if (kv.Value > best)
                {
                    best = kv.Value;
                    bestTick = kv.Key;
                }

                if (kv.Key < lo) lo = kv.Key;
                if (kv.Key > hi) hi = kv.Key;
            }

            poc = bestTick;
            double target = total * ValueAreaPct / 100.0;
            double acc = best;
            long up = bestTick, dn = bestTick;

            while (acc < target && (up < hi || dn > lo))
            {
                double vUp = up + 1 <= hi ? GetVol(up + 1) : -1;
                double vDn = dn - 1 >= lo ? GetVol(dn - 1) : -1;

                if (vUp < 0 && vDn < 0)
                    break;

                if (vUp >= vDn)
                {
                    up++;
                    if (vUp > 0) acc += vUp;
                }
                else
                {
                    dn--;
                    if (vDn > 0) acc += vDn;
                }
            }

            vah = up;
            val = dn;
        }

        private long TargetUp(long entry, long poc, long vah, long stop)
        {
            if (poc > entry + 1) return poc;
            if (vah > entry + 1) return vah;
            return entry + Math.Max(1, entry - stop) * 2;
        }

        private long TargetDown(long entry, long poc, long val, long stop)
        {
            if (poc < entry - 1) return poc;
            if (val < entry - 1) return val;
            return entry - Math.Max(1, stop - entry) * 2;
        }

        private double GetVol(long t) => _devProfile.TryGetValue(t, out var v) ? v : 0;

        private double ScoreLong(double ctrl, bool strongVol, double conf) =>
            100.0 * (0.5 * Clamp01(ctrl / (double)ControlStrong) + 0.3 * (strongVol ? 1 : 0.4) + 0.2 * Clamp01(conf));

        private double ScoreShort(double ctrl, bool strongVol, double conf) =>
            100.0 * (0.5 * Clamp01(-ctrl / (double)ControlStrong) + 0.3 * (strongVol ? 1 : 0.4) + 0.2 * Clamp01(conf));

        private void UpdateStateText(bool haveRef, long cl, long vah, long val)
        {
            if (!haveRef || vah <= val)
            {
                _state = "waiting for a completed session (value area)";
                return;
            }

            string where = cl > vah ? "ABOVE value" : cl < val ? "BELOW value" : "inside value";

            if (_belowActive)
                _state = _absorbedLo ? "sellers ABSORBED below value — waiting for buyers"
                    : _sellAggr ? "sellers aggressive below value — watching for failure"
                    : "testing below value";
            else if (_aboveActive)
                _state = _absorbedHi ? "buyers ABSORBED above value — waiting for sellers"
                    : _buyAggr ? "buyers aggressive above value — watching for failure"
                    : "testing above value";
            else
                _state = "price " + where;
        }

        #endregion

        // =================================================================================================
        #region Rendering

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo is null || InstrumentInfo is null)
                return;

            try
            {
                if (FontSize != _fontSize)
                {
                    _fontSize = FontSize;
                    _font = new RenderFont("Arial", FontSize);
                    _small = new RenderFont("Arial", Math.Max(7, FontSize - 1));
                }

                lock (_sync)
                {
                    var region = ChartInfo.PriceChartContainer.Region;
                    int barW = Math.Max(1, (int)ChartInfo.PriceChartContainer.BarsWidth);

                    if (ShowValueLines)
                        DrawValueLines(context, region);

                    if (ShowOpeningRange)
                        DrawOpeningRange(context, region);

                    DrawSignals(context, region, barW);

                    if (ShowDashboard)
                        DrawDashboard(context, region);
                }
            }
            catch (Exception ex)
            {
                this.LogError("OverPowerBuySell render: " + ex.Message, ex);
            }
        }

        private void DrawValueLines(RenderContext ctx, Rectangle region)
        {
            if (_liveVAH <= _liveVAL || _liveVAL <= 0)
                return;

            HLine(ctx, region, _liveVAH, D(VahColor), "VAH " + P(_liveVAH));
            HLine(ctx, region, _livePOC, D(PocColor), "POC " + P(_livePOC));
            HLine(ctx, region, _liveVAL, D(ValColor), "VAL " + P(_liveVAL));
        }

        private void DrawOpeningRange(RenderContext ctx, Rectangle region)
        {
            if (!_orInit)
                return;

            var col = D(OrColor);
            HLine(ctx, region, _orHigh, col, "OR H " + P(_orHigh));
            HLine(ctx, region, _orLow, col, "OR L " + P(_orLow));
        }

        private void HLine(RenderContext ctx, Rectangle region, long tick, Color color, string label)
        {
            int y = ChartInfo.GetYByPrice(ToPrice(tick), false);

            if (y < region.Top - 2 || y > region.Bottom + 2)
                return;

            ctx.DrawLine(new RenderPen(Alpha(color, 150), 1), region.Left, y, region.Right - 4, y);
            var sz = ctx.MeasureString(label, _small);
            int lx = region.Right - (int)sz.Width - 8;
            ctx.FillRectangle(Alpha(Color.Black, 130), new Rectangle(lx - 2, y - (int)sz.Height / 2, (int)sz.Width + 4, (int)sz.Height));
            ctx.DrawString(label, _small, color, lx, y - (int)sz.Height / 2);
        }

        private void DrawSignals(RenderContext ctx, Rectangle region, int barW)
        {
            int first = FirstVisibleBarNumber;
            int last = LastVisibleBarNumber;

            foreach (var s in _signals)
            {
                if (s.Bar < first - 2 || s.Bar > last + 2)
                    continue;

                int x = ChartInfo.GetXByBar(s.Bar) + barW / 2;
                var col = D(s.Buy ? BuyColor : SellColor);

                // entry / stop / target rails to the right of the signal
                if (ShowSignalLevels)
                {
                    int xEnd = Math.Min(region.Right - 4, x + barW * 14 + 40);
                    int yE = ChartInfo.GetYByPrice(ToPrice(s.EntryTick), false);
                    int yS = ChartInfo.GetYByPrice(ToPrice(s.StopTick), false);
                    int yT = ChartInfo.GetYByPrice(ToPrice(s.TargetTick), false);
                    ctx.DrawLine(new RenderPen(Alpha(Color.Gray, 130), 1), x, yE, xEnd, yE);
                    ctx.DrawLine(new RenderPen(Alpha(D(SellColor), 120), 1), x, yS, xEnd, yS);
                    ctx.DrawLine(new RenderPen(Alpha(D(BuyColor), 120), 1), x, yT, xEnd, yT);
                }

                // marker tag at the bar
                string tag = (s.Buy ? "▲ BUY " : "▼ SELL ") + s.Rr.ToString("0.0") + "R";
                var sz = ctx.MeasureString(tag, _small);
                int refTick = s.Buy ? s.LowTick : s.HighTick;
                int yRef = ChartInfo.GetYByPrice(ToPrice(refTick), false);
                int off = s.Buy ? 8 : -8 - (int)sz.Height;
                int tx = x - (int)sz.Width / 2;
                int ty = yRef + off;
                var rect = new Rectangle(tx - 3, ty - 2, (int)sz.Width + 6, (int)sz.Height + 4);
                ctx.FillRectangle(Alpha(col, 235), rect, 3);
                ctx.DrawString(tag, _small, Color.White, tx, ty);
            }
        }

        private void DrawDashboard(RenderContext ctx, Rectangle region)
        {
            var lines = new List<(string, Color)>();
            var txt = D(PanelText);

            lines.Add(("OVER POWER  ·  order-flow control", txt));
            lines.Add((_state, txt));

            if (_liveVAH > _liveVAL && _liveVAL > 0)
                lines.Add(($"VAH {P(_liveVAH)}   POC {P(_livePOC)}   VAL {P(_liveVAL)}", D(PocColor)));

            if (_orInit)
                lines.Add(($"OR  {P(_orLow)} – {P(_orHigh)}{(_orComplete ? " (set)" : " …")}", D(OrColor)));

            double buyers = (_power + 1) / 2 * 100;
            lines.Add(($"Control:  buyers {buyers:0}%   sellers {100 - buyers:0}%", _power >= 0 ? D(BuyColor) : D(SellColor)));
            lines.Add(($"Signals today:  {_sessLongs} long   {_sessShorts} short", txt));

            if (_signals.Count > 0)
            {
                var s = _signals[_signals.Count - 1];
                lines.Add(($"Last: {(s.Buy ? "BUY" : "SELL")} {P(s.EntryTick)}  R:R {s.Rr:0.0}  pwr {s.Power:0}", D(s.Buy ? BuyColor : SellColor)));
            }

            int pad = 8, lh = _fontSize + 6;
            int w = 150;
            foreach (var (t, _) in lines)
                w = Math.Max(w, (int)ctx.MeasureString(t, _font).Width + pad * 2);

            int meterH = 12;
            int h = pad * 2 + lh * lines.Count + meterH + 6;

            int x = Corner is DashCorner.TopLeft or DashCorner.BottomLeft ? region.Left + 8 : region.Right - w - 12;
            int y = Corner is DashCorner.TopLeft or DashCorner.TopRight ? region.Top + 8 : region.Bottom - h - 8;

            ctx.FillRectangle(D(PanelBg), new Rectangle(x, y, w, h), 4);
            ctx.DrawRectangle(new RenderPen(Alpha(txt, 60), 1), new Rectangle(x, y, w, h));

            int cy = y + pad;
            foreach (var (t, col) in lines)
            {
                ctx.DrawString(t, _font, col, x + pad, cy);
                cy += lh;
            }

            // power meter: centre = balanced, right = buyers, left = sellers
            int mx = x + pad, mw = w - pad * 2, my = cy + 2;
            ctx.FillRectangle(Alpha(Color.Gray, 60), new Rectangle(mx, my, mw, meterH), 2);
            int mid = mx + mw / 2;
            int fillW = (int)(Math.Abs(_power) * (mw / 2));
            var mcol = _power >= 0 ? D(BuyColor) : D(SellColor);
            if (_power >= 0)
                ctx.FillRectangle(Alpha(mcol, 220), new Rectangle(mid, my, fillW, meterH), 2);
            else
                ctx.FillRectangle(Alpha(mcol, 220), new Rectangle(mid - fillW, my, fillW, meterH), 2);
            ctx.DrawLine(new RenderPen(Alpha(txt, 160), 1), mid, my - 1, mid, my + meterH + 1);
        }

        #endregion

        // =================================================================================================
        #region Utilities

        private void ResetAll()
        {
            lock (_sync)
            {
                _lastBar = -1;
                _haveRef = false;
                _refPOC = _refVAH = _refVAL = 0;
                _devProfile.Clear();
                _devHigh = long.MinValue;
                _devLow = long.MaxValue;
                _havePrevHL = false;
                _sessionInit = false;
                _orInit = _orComplete = false;
                _avgVol = 0;
                _havePrev = false;
                _belowActive = _aboveActive = false;
                _sellAggr = _buyAggr = _absorbedLo = _absorbedHi = false;
                _brokeUp = _brokeDown = false;
                _lastSignalBar = -10000;
                _sessLongs = _sessShorts = 0;
                _signals.Clear();
                _state = "waiting for a completed session";
            }
        }

        private long ToTick(decimal price) => (long)Math.Round(price / _tick, MidpointRounding.AwayFromZero);
        private decimal ToPrice(long t) => t * _tick;
        private string P(long t) => ToPrice(t).ToString("F" + _priceDecimals);

        private static int DecimalsFromTick(decimal tick)
        {
            int d = 0;
            var t = tick;
            while (t != Math.Truncate(t) && d < 10)
            {
                t *= 10;
                d++;
            }
            return d;
        }

        private static double Clamp(double lo, double hi, double v) => v < lo ? lo : v > hi ? hi : v;
        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
        private static Color D(CrossColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
        private static Color Alpha(Color c, int a) => Color.FromArgb(a, c.R, c.G, c.B);

        #endregion
    }
}
