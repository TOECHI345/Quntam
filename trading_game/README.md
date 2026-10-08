# Quntam Trader

A full-fledged, self-contained **market trading game** that runs entirely in the browser.
No build step, no dependencies, no server — just open the file.

```
trading_game/index.html   ← open this in any modern browser
```

## Play

Open `trading_game/index.html` directly (double-click, or `file://…`), or serve the folder:

```bash
cd trading_game && python3 -m http.server 8000
# then visit http://localhost:8000
```

Pick your market conditions (Calm Bull / Open Market / High Volatility) and start trading.

## What's in it

- **Live simulated market** — 14 tickers across Tech, Crypto, Biotech, Energy, Finance and more,
  each priced with a geometric-Brownian-motion engine (per-asset volatility, drift and beta).
- **Real-time candlestick chart** on `<canvas>` with a 20-period moving average, a live
  last-price tag, your average-cost line, and pending-order markers. Range selector (1H/3H/1D/ALL).
- **Full order system** — market, limit and stop orders, processed every tick.
- **Long *and* short** selling with 2× margin buying power and live buying-power accounting.
- **Portfolio** — positions table with avg cost, market value, and realized/unrealized P/L;
  net worth, cash, day P/L and total return in the header.
- **Dynamic news feed** — asset-, sector- and market-wide events that actually shock prices
  (earnings, upgrades, probes, Fed moves, geopolitics…). Click a headline to jump to the ticker.
- **Objectives** — eight progressive milestones (first trade, diversify, go short, six figures…).
- **A trading clock** — 390-minute sessions roll into days with a closing-bell recap; pause, 1×, 2×, 4× speed.
- **Auto-save** to `localStorage` — your game persists across refreshes. Reset for a fresh run.

## Controls

| Key | Action |
|-----|--------|
| `Space` | Pause / resume the market |
| `B` | Switch to Buy |
| `S` | Switch to Sell / Short |

Quick-size buttons (25% / 50% / 75% / Max) size orders against your buying power or position.

## How it works

Everything lives in a single HTML file:

- A **tick loop** (`requestAnimationFrame`) advances market minutes based on the selected speed.
  Each tick walks every asset's price, decays news shocks, forms OHLC candles, fills resting
  orders, and checks objectives.
- The **matching engine** handles buying, selling, short-covering and position flips, tracking
  weighted average cost and realized P/L.
- State is serialized to `localStorage` on every trade and on a timer.

It's a game, not financial advice — prices are random. Have fun.
