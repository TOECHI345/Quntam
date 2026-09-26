// =====================================================================================================
//  OrderFlowAuctionSuite — ATAS ADAPTER
//
//  This is the ONLY file that references ATAS assemblies. It:
//     * feeds candles + footprint (GetAllPriceLevels) into the pure-C# SuiteEngine,
//     * exposes the engine's config as organised indicator settings,
//     * renders the engine's snapshot (levels, zones, VWAP, signals, dashboard, debug),
//     * raises alerts on new confirmed signals.
//
//  Every ATAS API used here is one exercised by the uploaded OrderFlowKeyLevels indicator
//  (Indicator base, OnCalculate/OnRender, GetCandle + GetAllPriceLevels, IsNewSession, ChartInfo
//  GetXByBar/GetYByPrice/PriceChartContainer, RenderContext draw calls, AddAlert). No invented APIs.
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

using OrderFlowSuite;

using Color = System.Drawing.Color;
#if CROSS_PLATFORM
using CrossColor = System.Drawing.Color;
#else
using CrossColor = System.Windows.Media.Color;
#endif
using Rectangle = System.Drawing.Rectangle;

namespace OrderFlowAuctionSuiteAtas
{
    public enum SignalFilter { Off, All, ValidPlus, HighConfidenceOnly }

    public enum DashboardCorner { TopRight, TopLeft, BottomRight, BottomLeft }

    [DisplayName("Order Flow Auction Suite")]
    [Display(Name = "Order Flow Auction Suite",
        Description = "Explainable order-flow decision support: daily bias, long-term auction, composite/value levels, delta aggression, absorption, traps/failed auctions, reload/retreat/passover, balance vs trend, VWAP, and scored BUY/SELL triggers.")]
    public class OrderFlowAuctionSuite : Indicator
    {
        #region Fields

        private readonly object _sync = new();
        private readonly OfBar _bar = new();
        private SuiteEngine? _engine;
        private DisplaySnapshot? _snap;
        private int _next;
        private volatile bool _historyLoaded;
        private int _lastAlertCount;

        private RenderFont _font = new("Arial", 9);
        private RenderFont _small = new("Arial", 8);
        private int _fontSize = 9;

        // AddAlert (classic ATAS) takes System.Drawing.Color, not the WPF color type.
        private readonly Color _alertBg = Color.FromArgb(255, 30, 30, 40);
        private readonly Color _alertFg = Color.FromArgb(255, 245, 245, 245);

        #endregion

        #region ctor

        public OrderFlowAuctionSuite() : base(true)
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
        #region Settings — GENERAL / PROFILE
        [Range(50, 90)]
        [Display(Name = "Value area %", GroupName = "01. Profile", Order = 10)]
        public int ValueAreaPct { get; set; } = 70;

        [Range(1, 30)]
        [Display(Name = "Bias lookback sessions", GroupName = "01. Profile", Order = 20)]
        public int LookbackSessions { get; set; } = 5;

        [Display(Name = "Use composite profile", GroupName = "01. Profile", Order = 30,
            Description = "Long-term context from merged recent sessions (limited to loaded chart history).")]
        public bool UseComposite { get; set; } = true;

        [Range(5, 250)]
        [Display(Name = "Composite sessions", GroupName = "01. Profile", Order = 40,
            Description = "Sessions merged into the composite (~60 RTH days ≈ 3 months, if the chart holds that history).")]
        public int CompositeSessions { get; set; } = 60;

        [Range(0, 20)]
        [Display(Name = "Value shift tolerance (ticks)", GroupName = "01. Profile", Order = 50)]
        public int ValueShiftTicks { get; set; } = 2;

        [Display(Name = "HVN factor", GroupName = "01. Profile", Order = 60)]
        public decimal HvnFactor { get; set; } = 1.5m;

        [Display(Name = "LVN factor", GroupName = "01. Profile", Order = 70)]
        public decimal LvnFactor { get; set; } = 0.5m;
        #endregion

        #region Settings — DELTA / AGGRESSION
        [Range(1, 10)]
        [Display(Name = "Aggression levels / side", GroupName = "02. Delta / Aggression", Order = 10)]
        public int AggressionLevelsPerSide { get; set; } = 3;

        [Display(Name = "Min aggression volume", GroupName = "02. Delta / Aggression", Order = 20)]
        public decimal MinAggressionVol { get; set; } = 100;

        [Display(Name = "Min aggression delta", GroupName = "02. Delta / Aggression", Order = 30)]
        public decimal MinAggressionDelta { get; set; } = 100;

        [Display(Name = "Control threshold (delta/vol)", GroupName = "02. Delta / Aggression", Order = 40)]
        public decimal ControlFrac { get; set; } = 0.35m;

        [Display(Name = "Volume factor (vs avg)", GroupName = "02. Delta / Aggression", Order = 50)]
        public decimal VolFactor { get; set; } = 1.2m;
        #endregion

        #region Settings — ABSORPTION
        [Range(1, 100)]
        [Display(Name = "Strong absorption score", GroupName = "03. Absorption", Order = 10)]
        public int AbsorptionStrong { get; set; } = 80;

        [Range(1, 100)]
        [Display(Name = "Moderate absorption score", GroupName = "03. Absorption", Order = 20)]
        public int AbsorptionModerate { get; set; } = 60;
        #endregion

        #region Settings — BALANCE
        [Range(5, 100)]
        [Display(Name = "Compression lookback", GroupName = "04. Balance", Order = 10)]
        public int CompressionLookback { get; set; } = 20;

        [Range(1, 40)]
        [Display(Name = "Stacked tolerance (ticks)", GroupName = "04. Balance", Order = 20)]
        public int StackedToleranceTicks { get; set; } = 4;
        #endregion

        #region Settings — SIGNALS
        [Display(Name = "Reversal setups", GroupName = "05. Signals", Order = 10)]
        public bool EnableReversal { get; set; } = true;

        [Display(Name = "Reload setups", GroupName = "05. Signals", Order = 20)]
        public bool EnableReload { get; set; } = true;

        [Display(Name = "Momentum / breakout setups", GroupName = "05. Signals", Order = 30)]
        public bool EnableMomentum { get; set; } = true;

        [Display(Name = "Range setups", GroupName = "05. Signals", Order = 40)]
        public bool EnableRange { get; set; } = true;

        [Range(1, 100)]
        [Display(Name = "Min score to show", GroupName = "05. Signals", Order = 50)]
        public int MinScoreToShow { get; set; } = 50;

        [Display(Name = "Signal quality filter", GroupName = "05. Signals", Order = 60,
            Description = "Off = none, All = every signal, Valid+ = 65+, High only = 80+.")]
        public SignalFilter Filter { get; set; } = SignalFilter.All;

        [Display(Name = "Min reward:risk", GroupName = "05. Signals", Order = 70)]
        public decimal MinRewardRisk { get; set; } = 1.2m;

        [Range(0, 50)]
        [Display(Name = "Stop buffer (ticks)", GroupName = "05. Signals", Order = 80)]
        public int StopBufferTicks { get; set; } = 2;

        [Range(0, 100)]
        [Display(Name = "Min bars between signals", GroupName = "05. Signals", Order = 90)]
        public int MinBarsBetweenSignals { get; set; } = 3;

        [Range(0, 20)]
        [Display(Name = "Value tolerance (ticks)", GroupName = "05. Signals", Order = 100)]
        public int ValueBufferTicks { get; set; } = 2;

        [Range(3, 500)]
        [Display(Name = "Max excursion bars", GroupName = "05. Signals", Order = 110)]
        public int MaxExcursionBars { get; set; } = 40;

        [Display(Name = "Enable alerts", GroupName = "05. Signals", Order = 120)]
        public bool UseAlerts { get; set; } = true;

        [Display(Name = "Alert sound", GroupName = "05. Signals", Order = 130)]
        public string AlertSound { get; set; } = "alert1";
        #endregion

        #region Settings — VISUALS
        [Display(Name = "Show value lines", GroupName = "06. Visuals", Order = 10)]
        public bool ShowValueLines { get; set; } = true;

        [Display(Name = "Show composite lines", GroupName = "06. Visuals", Order = 20)]
        public bool ShowComposite { get; set; } = true;

        [Display(Name = "Show HVN / LVN", GroupName = "06. Visuals", Order = 30)]
        public bool ShowNodes { get; set; } = true;

        [Display(Name = "Show aggression levels", GroupName = "06. Visuals", Order = 40)]
        public bool ShowAggression { get; set; } = true;

        [Display(Name = "Show zones", GroupName = "06. Visuals", Order = 50)]
        public bool ShowZones { get; set; } = true;

        [Display(Name = "Show VWAP", GroupName = "06. Visuals", Order = 60)]
        public bool ShowVwap { get; set; } = true;

        [Display(Name = "Show VWAP bands", GroupName = "06. Visuals", Order = 70)]
        public bool ShowVwapBands { get; set; } = true;

        [Display(Name = "Show signal rails (entry/stop/tgt)", GroupName = "06. Visuals", Order = 80)]
        public bool ShowSignalLevels { get; set; } = true;

        [Display(Name = "Show dashboard", GroupName = "06. Visuals", Order = 90)]
        public bool ShowDashboard { get; set; } = true;

        [Display(Name = "Dashboard corner", GroupName = "06. Visuals", Order = 100)]
        public DashboardCorner Corner { get; set; } = DashboardCorner.TopRight;

        [Range(7, 20)]
        [Display(Name = "Font size", GroupName = "06. Visuals", Order = 110)]
        public int FontSize { get; set; } = 9;

        [Display(Name = "Bullish color", GroupName = "06. Visuals", Order = 120)]
        public CrossColor BullColor { get; set; } = CrossColor.FromArgb(255, 0, 200, 83);

        [Display(Name = "Bearish color", GroupName = "06. Visuals", Order = 130)]
        public CrossColor BearColor { get; set; } = CrossColor.FromArgb(255, 255, 82, 82);

        [Display(Name = "POC color", GroupName = "06. Visuals", Order = 140)]
        public CrossColor PocColor { get; set; } = CrossColor.FromArgb(255, 255, 152, 0);

        [Display(Name = "Node color", GroupName = "06. Visuals", Order = 150)]
        public CrossColor NodeColor { get; set; } = CrossColor.FromArgb(255, 100, 181, 246);

        [Display(Name = "Composite color", GroupName = "06. Visuals", Order = 160)]
        public CrossColor CompositeColor { get; set; } = CrossColor.FromArgb(255, 186, 104, 200);

        [Display(Name = "VWAP color", GroupName = "06. Visuals", Order = 170)]
        public CrossColor VwapColor { get; set; } = CrossColor.FromArgb(255, 236, 64, 122);

        [Display(Name = "Panel background", GroupName = "06. Visuals", Order = 180)]
        public CrossColor PanelBg { get; set; } = CrossColor.FromArgb(185, 16, 16, 22);

        [Display(Name = "Panel text", GroupName = "06. Visuals", Order = 190)]
        public CrossColor PanelText { get; set; } = CrossColor.FromArgb(255, 235, 235, 235);
        #endregion

        #region Settings — DEBUG
        [Display(Name = "Debug panel", GroupName = "07. Debug", Order = 10,
            Description = "Show bias/state/delta/absorption/score diagnostics to verify the methodology.")]
        public bool DebugMode { get; set; } = false;
        #endregion

        // =================================================================================================
        #region Calculation

        private EngineConfig BuildConfig() => new()
        {
            ValueAreaPercent = ValueAreaPct,
            LookbackSessions = LookbackSessions,
            CompositeSessions = CompositeSessions,
            UseComposite = UseComposite,
            ValueShiftTicks = ValueShiftTicks,
            HvnFactor = (double)HvnFactor,
            LvnFactor = (double)LvnFactor,
            AggressionLevelsPerSide = AggressionLevelsPerSide,
            MinAggressionVol = (double)MinAggressionVol,
            MinAggressionDelta = (double)MinAggressionDelta,
            AbsorptionStrong = AbsorptionStrong,
            AbsorptionModerate = AbsorptionModerate,
            ControlFrac = (double)ControlFrac,
            VolFactor = (double)VolFactor,
            CompressionLookback = CompressionLookback,
            StackedToleranceTicks = StackedToleranceTicks,
            MinScoreToShow = MinScoreToShow,
            MinRewardRisk = (double)MinRewardRisk,
            StopBufferTicks = StopBufferTicks,
            MinBarsBetweenSignals = MinBarsBetweenSignals,
            ValueBufferTicks = ValueBufferTicks,
            MaxExcursionBars = MaxExcursionBars,
            EnableReversal = EnableReversal,
            EnableReload = EnableReload,
            EnableMomentum = EnableMomentum,
            EnableRange = EnableRange
        };

        protected override void OnCalculate(int bar, decimal value)
        {
            if (InstrumentInfo is null)
                return;

            try
            {
                lock (_sync)
                {
                    if (_engine == null || bar == 0)
                    {
                        _engine = new SuiteEngine(BuildConfig(), (double)InstrumentInfo.TickSize);
                        _next = 0;
                        _historyLoaded = false;
                        _lastAlertCount = 0;
                    }

                    var engine = _engine;

                    for (int b = _next; b < bar; b++)
                    {
                        FillBar(b);
                        engine.ProcessClosedBar(_bar);
                        _next = b + 1;
                    }

                    if (bar == CurrentBar - 1)
                    {
                        FillBar(bar);
                        engine.UpdateLive(_bar);
                        _historyLoaded = true;
                    }

                    _snap = engine.Snapshot();
                    EmitAlerts();
                }
            }
            catch (Exception ex)
            {
                this.LogError("OrderFlowAuctionSuite: " + ex.Message, ex);
            }
        }

        private void FillBar(int bar)
        {
            var c = GetCandle(bar);
            var e = _engine!;
            _bar.Reset();
            _bar.Index = bar;
            _bar.TimeUtc = c.Time;
            _bar.LastTimeUtc = c.LastTime;
            _bar.OpenTick = e.ToTick(c.Open);
            _bar.HighTick = e.ToTick(c.High);
            _bar.LowTick = e.ToTick(c.Low);
            _bar.CloseTick = e.ToTick(c.Close);
            _bar.Volume = (double)c.Volume;
            _bar.Delta = (double)c.Delta;
            _bar.Bid = (double)c.Bid;
            _bar.Ask = (double)c.Ask;
            _bar.IsNewSession = IsNewSession(bar);

            var levels = c.GetAllPriceLevels();
            if (levels != null)
            {
                foreach (var pv in levels)
                {
                    if (pv == null) continue;
                    _bar.AddRow(e.ToTick(pv.Price), (double)pv.Bid, (double)pv.Ask, (double)pv.Volume);
                }
            }
        }

        private void EmitAlerts()
        {
            if (!UseAlerts || _snap == null || !_historyLoaded)
                return;

            var sigs = _snap.Signals;
            if (sigs.Length <= _lastAlertCount)
            {
                _lastAlertCount = sigs.Length;
                return;
            }

            for (int i = _lastAlertCount; i < sigs.Length; i++)
            {
                var s = sigs[i];
                if (s.Bar < CurrentBar - 2) continue;         // only the freshly closed bar
                if (!PassesFilter(s.Quality)) continue;
                string msg = $"{s.Side} {s.Kind}  @ {Fmt(s.EntryTick)}  stop {Fmt(s.StopTick)}  tgt {Fmt(s.TargetTick)}  R:R {s.Rr:0.0}  score {s.Score} ({s.Quality})";
                AddAlert(AlertSound, InstrumentInfo?.Instrument ?? string.Empty, msg, _alertBg, _alertFg);
            }
            _lastAlertCount = sigs.Length;
        }

        private bool PassesFilter(SignalQuality q) => Filter switch
        {
            SignalFilter.Off => false,
            SignalFilter.All => q != SignalQuality.NoSignal,
            SignalFilter.ValidPlus => q is SignalQuality.Valid or SignalQuality.HighConfidence,
            SignalFilter.HighConfidenceOnly => q == SignalQuality.HighConfidence,
            _ => true
        };

        private string Fmt(long tick) => (_snap == null ? tick.ToString() : ((double)tick * _snap.TickSize).ToString("F" + _snap.PriceDecimals));

        #endregion

        // =================================================================================================
        #region Rendering

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo is null || InstrumentInfo is null)
                return;

            DisplaySnapshot? snap;
            lock (_sync) snap = _snap;
            if (snap == null) return;

            try
            {
                if (FontSize != _fontSize)
                {
                    _fontSize = FontSize;
                    _font = new RenderFont("Arial", FontSize);
                    _small = new RenderFont("Arial", Math.Max(7, FontSize - 1));
                }

                var region = ChartInfo.PriceChartContainer.Region;
                int barW = Math.Max(1, (int)ChartInfo.PriceChartContainer.BarsWidth);

                if (ShowVwap && snap.VwapValid) DrawVwap(context, region, snap);
                DrawLevels(context, region, snap);
                if (ShowZones) DrawZones(context, region, barW, snap);
                DrawSignals(context, region, barW, snap);
                if (ShowDashboard) DrawDashboard(context, region, snap);
            }
            catch (Exception ex)
            {
                this.LogError("OrderFlowAuctionSuite render: " + ex.Message, ex);
            }
        }

        private void DrawVwap(RenderContext ctx, Rectangle region, DisplaySnapshot snap)
        {
            var pts = snap.Vwap;
            if (pts.Length < 2) return;
            var pen = new RenderPen(D(VwapColor), 1);
            var band = new RenderPen(Alpha(D(VwapColor), 60), 1);
            for (int i = 1; i < pts.Length; i++)
            {
                int x1 = ChartInfo.GetXByBar(pts[i - 1].bar), x2 = ChartInfo.GetXByBar(pts[i].bar);
                if (x2 < region.Left || x1 > region.Right) continue;
                ctx.DrawLine(pen, x1, ChartInfo.GetYByPrice((decimal)pts[i - 1].v, false), x2, ChartInfo.GetYByPrice((decimal)pts[i].v, false));
                if (ShowVwapBands)
                {
                    ctx.DrawLine(band, x1, ChartInfo.GetYByPrice((decimal)pts[i - 1].u1, false), x2, ChartInfo.GetYByPrice((decimal)pts[i].u1, false));
                    ctx.DrawLine(band, x1, ChartInfo.GetYByPrice((decimal)pts[i - 1].d1, false), x2, ChartInfo.GetYByPrice((decimal)pts[i].d1, false));
                }
            }
        }

        private void DrawLevels(RenderContext ctx, Rectangle region, DisplaySnapshot snap)
        {
            foreach (var lv in snap.Levels)
            {
                if (!WantLevel(lv.Kind)) continue;
                Color c = LevelColor(lv.Kind);
                HLine(ctx, region, lv.Tick, snap, c, lv.Label, dashed: lv.Kind is ZoneKind.Hvn or ZoneKind.Lvn or ZoneKind.BuyAggression or ZoneKind.SellAggression);
            }
        }

        private bool WantLevel(ZoneKind k) => k switch
        {
            ZoneKind.Vah or ZoneKind.Val or ZoneKind.Poc => ShowValueLines,
            ZoneKind.Hvn or ZoneKind.Lvn => ShowNodes,
            ZoneKind.BuyAggression or ZoneKind.SellAggression => ShowAggression,
            ZoneKind.CompositePoc or ZoneKind.CompositeVah or ZoneKind.CompositeVal => ShowComposite,
            _ => true
        };

        private Color LevelColor(ZoneKind k) => k switch
        {
            ZoneKind.Vah => D(BearColor),
            ZoneKind.Val => D(BullColor),
            ZoneKind.Poc => D(PocColor),
            ZoneKind.Hvn => D(NodeColor),
            ZoneKind.Lvn => Alpha(D(NodeColor), 150),
            ZoneKind.BuyAggression => D(BullColor),
            ZoneKind.SellAggression => D(BearColor),
            ZoneKind.CompositePoc or ZoneKind.CompositeVah or ZoneKind.CompositeVal => D(CompositeColor),
            _ => D(PanelText)
        };

        private void DrawZones(RenderContext ctx, Rectangle region, int barW, DisplaySnapshot snap)
        {
            int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
            foreach (var z in snap.Zones)
            {
                if (z.CreatedBar > last) continue;
                int x1 = ChartInfo.GetXByBar(Math.Max(z.CreatedBar, first));
                int x2 = z.EndBar >= 0 ? ChartInfo.GetXByBar(z.EndBar) + barW : region.Right - 4;
                if (x2 < region.Left) continue;
                int yTop = ChartInfo.GetYByPrice((decimal)((double)z.Hi * snap.TickSize), false);
                int yBot = ChartInfo.GetYByPrice((decimal)((double)z.Lo * snap.TickSize), false);
                int y = Math.Min(yTop, yBot), h = Math.Max(2, Math.Abs(yBot - yTop));
                Color c = ZoneColor(z.Kind);
                ctx.FillRectangle(Alpha(c, z.EndBar >= 0 ? 22 : 40), new Rectangle(x1, y, Math.Max(2, x2 - x1), h));
                ctx.FillRectangle(Alpha(c, 120), new Rectangle(x1, y, 3, h));
                if (z.CreatedBar >= first - 2)
                    ctx.DrawString(z.Label, _small, c, x1 + 5, y + 1);
            }
        }

        private Color ZoneColor(ZoneKind k) => k switch
        {
            ZoneKind.TrappedBuyers or ZoneKind.ReloadSell => D(BearColor),
            ZoneKind.TrappedSellers or ZoneKind.ReloadBuy => D(BullColor),
            ZoneKind.Retreat => D(NodeColor),
            ZoneKind.Passover => D(CompositeColor),
            ZoneKind.Compression => Alpha(D(PanelText), 120),
            _ => D(PanelText)
        };

        private void DrawSignals(RenderContext ctx, Rectangle region, int barW, DisplaySnapshot snap)
        {
            int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
            foreach (var s in snap.Signals)
            {
                if (!PassesFilter(s.Quality)) continue;
                if (s.Bar < first - 2 || s.Bar > last + 2) continue;

                bool buy = s.Side == Side.Long;
                int x = ChartInfo.GetXByBar(s.Bar) + barW / 2;
                Color c = buy ? D(BullColor) : D(BearColor);

                if (ShowSignalLevels)
                {
                    int xEnd = Math.Min(region.Right - 4, x + barW * 14 + 40);
                    int yE = ChartInfo.GetYByPrice((decimal)((double)s.EntryTick * snap.TickSize), false);
                    int yS = ChartInfo.GetYByPrice((decimal)((double)s.StopTick * snap.TickSize), false);
                    int yT = ChartInfo.GetYByPrice((decimal)((double)s.TargetTick * snap.TickSize), false);
                    ctx.DrawLine(new RenderPen(Alpha(Color.Gray, 130), 1), x, yE, xEnd, yE);
                    ctx.DrawLine(new RenderPen(Alpha(D(BearColor), 120), 1), x, yS, xEnd, yS);
                    ctx.DrawLine(new RenderPen(Alpha(D(BullColor), 120), 1), x, yT, xEnd, yT);
                }

                string tag = (buy ? "▲ " : "▼ ") + s.Side + " " + s.Score;
                var sz = ctx.MeasureString(tag, _small);
                int refTick = buy ? (int)s.LowTick : (int)s.HighTick;
                int yRef = ChartInfo.GetYByPrice((decimal)((double)refTick * snap.TickSize), false);
                int off = buy ? 8 : -8 - (int)sz.Height;
                int tx = x - (int)sz.Width / 2, ty = yRef + off;
                ctx.FillRectangle(Alpha(c, 235), new Rectangle(tx - 3, ty - 2, (int)sz.Width + 6, (int)sz.Height + 4), 3);
                ctx.DrawString(tag, _small, Color.White, tx, ty);
            }
        }

        private void HLine(RenderContext ctx, Rectangle region, long tick, DisplaySnapshot snap, Color color, string label, bool dashed)
        {
            int y = ChartInfo.GetYByPrice((decimal)((double)tick * snap.TickSize), false);
            if (y < region.Top - 2 || y > region.Bottom + 2) return;

            if (dashed)
            {
                for (int x = region.Left; x < region.Right - 8; x += 10)
                    ctx.DrawLine(new RenderPen(Alpha(color, 140), 1), x, y, x + 5, y);
            }
            else
            {
                ctx.DrawLine(new RenderPen(Alpha(color, 160), 1), region.Left, y, region.Right - 4, y);
            }

            var sz = ctx.MeasureString(label, _small);
            int lx = region.Right - (int)sz.Width - 8;
            ctx.FillRectangle(Alpha(Color.Black, 140), new Rectangle(lx - 2, y - (int)sz.Height / 2, (int)sz.Width + 4, (int)sz.Height));
            ctx.DrawString(label, _small, color, lx, y - (int)sz.Height / 2);
        }

        private void DrawDashboard(RenderContext ctx, Rectangle region, DisplaySnapshot snap)
        {
            var lines = new List<(string, Color)>();
            var txt = D(PanelText);

            lines.Add(("ORDER FLOW AUCTION SUITE", txt));
            lines.Add(("Daily bias:   " + snap.DailyBias, BiasColor(snap.DailyBias)));
            lines.Add(("Long-term:    " + snap.LongTermAuction, BiasColor(snap.LongTermAuction)));
            lines.Add(("State:        " + snap.State, txt));
            lines.Add((snap.StateNote, txt));

            double buyers = (snap.Power + 1) / 2 * 100;
            lines.Add(($"Control: buyers {buyers:0}% / sellers {100 - buyers:0}%   abs {snap.LiveAbsorption}", snap.Power >= 0 ? D(BullColor) : D(BearColor)));

            if (snap.Signals.Length > 0)
            {
                var s = snap.Signals[snap.Signals.Length - 1];
                lines.Add(($"Last: {s.Side} {s.Kind}  {s.Score} ({s.Quality})  R:R {s.Rr:0.0}", s.Side == Side.Long ? D(BullColor) : D(BearColor)));
            }

            if (DebugMode)
                foreach (var d in snap.DebugLines)
                    lines.Add((d, Alpha(txt, 210)));

            int pad = 8, lh = _fontSize + 6, w = 170;
            foreach (var (t, _) in lines)
                w = Math.Max(w, (int)ctx.MeasureString(t, _font).Width + pad * 2);
            int h = pad * 2 + lh * lines.Count;

            int x = Corner is DashboardCorner.TopLeft or DashboardCorner.BottomLeft ? region.Left + 8 : region.Right - w - 12;
            int y = Corner is DashboardCorner.TopLeft or DashboardCorner.TopRight ? region.Top + 8 : region.Bottom - h - 8;

            ctx.FillRectangle(D(PanelBg), new Rectangle(x, y, w, h), 4);
            ctx.DrawRectangle(new RenderPen(Alpha(txt, 60), 1), new Rectangle(x, y, w, h));

            int cy = y + pad;
            foreach (var (t, col) in lines)
            {
                ctx.DrawString(t, _font, col, x + pad, cy);
                cy += lh;
            }
        }

        private Color BiasColor(Bias b) => b == Bias.Bullish ? D(BullColor) : b == Bias.Bearish ? D(BearColor) : D(PanelText);

        #endregion

        #region Utils
        private static Color D(CrossColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
        private static Color Alpha(Color c, int a) => Color.FromArgb(a, c.R, c.G, c.B);
        #endregion
    }
}
