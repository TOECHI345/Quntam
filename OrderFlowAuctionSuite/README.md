# Order Flow Auction Suite (ATAS)

An **explainable** order-flow decision-support indicator that implements the auction / battle-of-orders
methodology (Fabio boot-camp framework). It does **not** paint blind BUY/SELL arrows. Every signal is
produced by a transparent pipeline and shows *why* it fired:

```
DAILY PROFILE → DAILY BIAS → LONG-TERM AUCTION → COMPOSITE PROFILE → IMPORTANT LEVEL
→ PRICE/AUCTION CONTEXT → DELTA/AGGRESSION → ABSORPTION/TRAP → BATTLE OF ORDERS
→ PRICE CONFIRMATION → TRIGGER → SIGNAL (0–100 score breakdown)
```

> Core principle: **predict less, react more.** A level is only a *location*. A trade needs the market to
> prove — through delta, absorption and a confirming candle body — that one side is actually in control.

---

## Architecture (modular, as specified)

Two files, one DLL. The engine has **zero ATAS dependencies** so it is testable and every calculation is
plain, inspectable C#.

| File | Contains |
|------|----------|
| `SuiteEngine.cs` | Pure C# engine. Classes: `VolumeProfileAnalyzer`, `DeltaAnalyzer`, `AbsorptionDetector`, `AuctionAnalyzer`, `BalanceTrendDetector`, `VwapCalc`, `ZoneList` (zone manager), `ScoreEngine`, `SuiteEngine` (orchestrator). |
| `OrderFlowAuctionSuite.cs` | The ATAS `Indicator`: feeds candles + footprint into the engine, exposes settings, renders the snapshot, raises alerts. Only this file touches ATAS. |

Data collection (adapter) → calculation (analyzers) → signal logic (`SuiteEngine`) → visualization
(adapter render) are cleanly separated.

---

## Build & install

Requires the ATAS SDK assemblies from your **licensed ATAS install** (they are not redistributable and
are not on NuGet). Build on the Windows PC that has ATAS.

1. Install the free **.NET SDK**: https://dotnet.microsoft.com/download (match your ATAS — .NET 8 or 10).
2. Put `SuiteEngine.cs`, `OrderFlowAuctionSuite.cs`, `OrderFlowAuctionSuite.csproj` and `build.bat` in one folder.
3. **Double-click `build.bat`** (or run `dotnet build -c Release`). It compiles and copies
   `OrderFlowAuctionSuite.dll` into `Documents\ATAS\Indicators`.
4. Restart ATAS and add **"Order Flow Auction Suite"** to a chart.

ATAS X only: `dotnet build -c Release -p:Platform=Cross`. If the framework is `net8.0`, change `net10.0` →
`net8.0` in the `.csproj`.

**Best data:** run it on a **footprint / cluster chart** so per-price bid/ask and delta are exact. It also
works on plain candle charts using candle delta with a range-spread profile fallback (see limitations).

---

## The signal pipeline (what each stage does)

1. **Daily bias** — builds a volume profile per completed session (VAH/VAL/POC/high/low/volume/delta) and
   compares value migration. Value shifts higher = bullish, lower = bearish, overlap = neutral. One
   opposing session does **not** flip the long-term read.
2. **Long-term auction** — net direction of value shifts across `Bias lookback sessions` (default 5).
3. **Composite profile** — merges the last `Composite sessions` (~3 months of RTH days) into a long-term
   VAH/VAL/POC. Price re-entering composite value flags a possible auction transition; composite POC is a
   magnet target.
4. **Important levels** — VAH, VAL, POC, HVN, LVN, strongest buy/sell aggression levels.
5. **Price/auction context** — market state (Balance / Trend / Transition) and compression range.
6. **Delta / aggression** — the price levels that saw the largest one-sided delta (defence / reload levels).
7. **Absorption / trap** — absorption score 0–100 (effort vs result); trapped buyers/sellers = failed
   auctions become rejection zones.
8. **Battle of orders** — which side is winning: aggressor accepted (continuation) vs absorbed (reversal).
9. **Price confirmation** — the confirming candle must close with the winning side controlling the body.
10. **Trigger → signal** — only then does a signal fire, on the **closed** bar, with a score breakdown.

---

## Signals

| Signal | Fires when |
|--------|-----------|
| **LONG / SHORT REVERSAL** | Failed auction at VAL/VAH: aggressor pushes, gets absorbed, opposite side reclaims. |
| **LONG / SHORT MOMENTUM** | Opening-range breakout **accepted** (body close beyond, upper/lower third, strong one-sided delta). |
| **LONG / SHORT RELOAD** | Price accepts back through a prior aggression level; original side re-enters. |
| **LONG / SHORT RANGE** | In a balanced market, trapped participants at the boundary (VAL/VAH) fade back to POC. |

Zones drawn (visible until invalidated): **Trapped Buyers/Sellers, Reload Buy/Sell, Retreat, Passover**,
plus VAH/VAL/POC/HVN/LVN/aggression lines, composite lines and VWAP (±1σ bands).

### Score (transparent, NOT a win probability)

```
+20 daily bias aligned          +10 strong absorption
+15 important level             +10 trapped participants / failed auction
+15 long-term auction aligned   +10 price confirms (candle body)
+15 aggressive side in control  +5  VWAP alignment
                                +5  composite premium/discount
```
`≥80` High confidence · `65–79` Valid · `50–64` Weak · `<50` no signal. The dashboard shows the last
signal's score; turn on **Debug panel** to see the full running state. Each signal's arrow carries its
score, and the breakdown lines are stored on the signal for tooltips/inspection.

---

## Parameters (by group)

- **01. Profile** — value-area %, bias lookback, composite on/off + size, value-shift tolerance, HVN/LVN factors.
- **02. Delta / Aggression** — aggression levels per side, min volume, min delta, control threshold (delta/vol), volume factor.
- **03. Absorption** — strong / moderate absorption score thresholds.
- **04. Balance** — compression lookback, stacked-profile tolerance.
- **05. Signals** — enable each setup family, min score, quality filter (Off / All / Valid+ / High-only), min R:R, stop buffer, spacing, value tolerance, excursion timeout, alerts + sound.
- **06. Visuals** — toggles for value lines / composite / nodes / aggression / zones / VWAP / bands / rails / dashboard, corner, font, colors.
- **07. Debug** — diagnostic panel (bias, state, delta, buy/sell volume, absorption, score, sessions, zones).

Every parameter has a sensible default.

---

## Real-time behavior (no repaint / no look-ahead)

- Signals are evaluated **only on closed bars**; the forming bar updates the dashboard/power meter but
  never prints a locked signal. Once a bar closes its signal is fixed.
- On a full recalculation the engine rebuilds deterministically from bar 0 — no future data is used to
  decide a past bar.
- Alerts fire only for a signal on the just-closed live bar (`CurrentBar-2`), never replayed across history.

---

## Known limitations & honest API notes

These are the places where the exact methodology meets ATAS API reality. Nothing is silently substituted.

1. **Composite = loaded history, not a guaranteed 3 months.** ATAS gives the engine only the bars loaded on
   the chart. "60 sessions" is honored *if* that much history is loaded; otherwise the composite covers what
   exists. Load more history for a true 3-month composite. *Effect:* composite levels are accurate for the
   history present, approximate if history is short.
2. **Big single "prints" (the bubble trades) are not consumed.** Aggression is derived from **footprint
   per-price bid/ask** and **candle delta**, which is sufficient for aggression levels, absorption and
   traps. The cumulative-trades feed (individual large orders) is available in ATAS via
   `RequestForCumulativeTrades`, but is intentionally left out of v1 to keep the build dependency-light and
   verified. *Effect:* "immediate big-trade" timing is approximated by strong one-sided footprint delta,
   not by individual order size.
3. **No footprint on the chart → fallback.** If `GetAllPriceLevels()` returns nothing (non-footprint chart),
   the engine spreads each bar's volume/delta across its range to build the profile. *Effect:* value area
   and aggression levels are coarser; run a footprint chart for full fidelity.
4. **Session boundaries follow the chart's ATAS session (`IsNewSession`).** Configure your chart's session
   (RTH/cash) in ATAS to match the "regular session" the daily bias assumes.
5. **Stacked/retreat/passover are contextual, not predictions.** They mark structure and are inputs to the
   score; they never auto-fire a trade on their own.
6. **VWAP** is computed from typical price × volume with ±1σ/±2σ bands and resets each session.

---

## Example chart interpretation

Bullish long-term auction, price opens and rotates down to the previous session **VAL** which lines up
near the composite discount. Sellers push with a strong negative-delta bar but the next bar makes no new
low and closes in its upper half → **absorption**. A confirming bar closes up through the prior high with
positive delta → **LONG REVERSAL**, score e.g. 85 (High): +20 bias, +15 VAL, +15 long-term, +15 buyers in
control, +10 absorption, +10 price confirm. Entry at close, stop below the absorbed low, target the POC
magnet then VAH. If price instead accepts back **through** a marked sell-aggression level on the way up, a
**LONG RELOAD** can follow.

---

## Testing checklist

- [ ] Builds with `build.bat` / `dotnet build -c Release` (report any compile error to iterate).
- [ ] Loads in ATAS on a **footprint** chart; dashboard shows bias/state and updates live.
- [ ] VAH/VAL/POC lines match a manual session volume profile.
- [ ] HVN/LVN mark the high/low volume areas you'd pick by eye.
- [ ] Aggression levels sit where large one-sided delta occurred.
- [ ] A REVERSAL only prints after absorption **and** a confirming body — never on a bare touch.
- [ ] MOMENTUM only prints on an accepted breakout (body beyond the opening range), not a wick.
- [ ] No signal repaints after its bar closes; scrolling history shows locked signals.
- [ ] Quality filter (Valid+ / High-only) reduces signal count as expected.
- [ ] Alerts fire once per new signal on the live bar, not replayed over history.
- [ ] Debug panel numbers (delta, buy/sell, absorption, score) are self-consistent.

---

*Companion:* `../OverPowerBuySell/` is a lighter, single-file version of the same idea (value-extreme +
over-power reversals/breakouts) if you want a minimal starting point.
