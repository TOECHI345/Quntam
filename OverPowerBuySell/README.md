# Over Power Buy/Sell — ATAS order-flow signal indicator

A focused **BUY / SELL signal** indicator for futures day trading, built directly on the
"who is in control?" order-flow model from the strategy breakdown.

> Price does not move because there are more buyers than sellers — there are always equal
> numbers on both sides. It moves because one side is **more aggressive**. The only question
> that matters is: **is the aggressive side being *accepted* (continuation) or *absorbed* (reversal)?**

This indicator establishes fair value from a session volume profile, watches price at the value
extremes and the opening range, measures which side is **over-powering** the other from order-flow
delta / footprint, and fires a confirmed signal only when one side clearly takes control.

It is decision support — it marks where control shifts and where the idea is invalidated. It does
**not** place orders.

---

## The three setups it trades

| # | Setup | What it looks for | Signal |
|---|-------|-------------------|--------|
| 1 | **Failed auction below value** | Price at/below VAL → sellers aggressive → **absorbed** (no new low, closes back up) → buyers reclaim | **LONG** |
| 2 | **Failed auction above value** | Price at/above VAH → buyers aggressive → **absorbed** (no new high, closes back down) → sellers reclaim | **SHORT** |
| 3 | **Breakout with acceptance** | Body (not wick) closes beyond the opening range / prior-session extreme with the aggressor accepted | **Continuation** in break direction |

Every setup follows the strategy's execution rules:

- **Location** — trade only at value extremes or the breakout level.
- **Control** — enter only when one side is clearly in control and moving price (delta ÷ volume ≥ *Control threshold*).
- **Absorption filter** — reversals require aggression that *fails* (price doesn't follow).
- **Acceptance filter** — breakouts require the move to be in the **body**, upper/lower third, on strong volume.
- **Confirmation, never anticipation** — signals fire on **closed bars only**.
- **Invalidation stop** — placed where the opposite side would prove control (below the absorbed low / above the absorbed high, or back inside the broken level).
- **Value / measured-move targets** — reversals target POC → far value; breakouts target the measured move (range projection).

---

## What it draws

- **BUY ▲ / SELL ▼ tags** at each signal, with the R:R on the tag.
- **Entry / stop / target rails** projected to the right of each signal.
- **Value lines** — VAH, POC, VAL of the reference profile.
- **Opening range** high/low.
- **Over-power dashboard** — current location, live buyer/seller control meter, today's signal count, and the last signal.
- **Alerts** on every new signal (`AddAlert`), gated to the live bar so history doesn't spam.

---

## Key settings

**01. Value Area**
- `Value reference` — Previous session (stable, recommended) or Developing session.
- `Value area %` — 70 by default.
- `Opening range (min)` — length of the ORB window (15 by default).
- `Value tolerance (ticks)` — how close to VAH/VAL counts as "at the extreme".

**02. Order Flow**
- `Control threshold` — net delta ÷ volume to call one side in control (0.35).
- `Absorption threshold` — heavy one-sided delta that, when price fails, marks absorption (0.30).
- `Control (full power)` — delta ratio that scores 100% on the meter (0.60).
- `Volume factor` — volume vs its average to count as strong participation (1.2×).
- `Acceptance body %` — minimum body/range for a valid breakout (0.50).

**03. Setups** — toggle reversals / breakouts, breakout reference, minimum power score, excursion timeout, min bars between signals.

**04. Risk** — stop buffer (ticks), minimum reward:risk.

**05. Alerts** — enable + sound file.

**06. Appearance** — dashboard, value lines, opening range, signal rails, corner, font, colors.

> Tune `Control threshold`, `Volume factor` and `Acceptance body %` per instrument. ES/NQ tolerate
> higher thresholds than thinner contracts. Best results on a **footprint (cluster) chart** so the
> volume profile and delta are exact; on non-footprint charts it falls back to candle delta and a
> range-spread profile.

---

## Build & install

Requires the ATAS SDK assemblies (referenced from your ATAS install, not redistributed).

```bash
# classic ATAS (also loads in ATAS X)
dotnet build -c Release

# ATAS X–only machine, native build
dotnet build -c Release -p:Platform=Cross
```

If the platform runtime is `net8.0` (check `OFT.Platform.runtimeconfig.json` /
`OFT.PlatformX.runtimeconfig.json`), change `net10.0` → `net8.0` in the `.csproj`.

Copy the built `OverPowerBuySell.dll` into `Documents\ATAS\Indicators`, restart ATAS, then add
**"Over Power Buy/Sell"** from the indicator list onto your price chart.

---

## Notes & limitations

- Signals are confirmation-based, so entry prints on the **close** of the confirming bar.
- The reference value area needs one **completed** session before reversal setups arm (breakouts
  arm once the opening range is set).
- This is not financial advice and does not guarantee a level holds — it flags where order flow
  shows one side over-powering the other, per the strategy.
