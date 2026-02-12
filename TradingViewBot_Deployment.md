# TradingView Reversal Scalping Bot - Real Money Deployment

This strategy script is in:

- `ReversalScalpingFuturesTradingViewBot.pine`

## Important architecture note

TradingView strategy code can generate orders/alerts, but **real money automation** requires either:

1. A TradingView-supported broker integration that accepts strategy order execution, or
2. A webhook bridge service/server that receives TradingView alerts and routes orders to your futures broker API.

The script includes `alert_message` JSON on entries/exits to support webhook automation.

---

## 1) Chart and symbol setup

1. Open your futures symbol (e.g., `CME_MINI:ES1!`, `CME_MINI:NQ1!`).
2. Use a **1-minute chart** (default logic assumes this).
3. Add the script as a **Strategy**.
4. In script inputs:
   - `SessionTemplate`: keep ETH template (`1800-1700`) or your own.
   - `SessionOpenOffsetMinutes`: set open offset to your cash-open location (default 930 minutes from 18:00 session start = 09:30 ET).
   - Set `Zone`, `Volatility`, `Pattern`, `Risk`, and `Management` inputs per your market.

---

## 2) Realistic backtest settings

The script already applies realistic defaults:

- `commission_type = cash_per_contract`
- `commission_value = 2.40`
- `slippage = 1 tick`
- `pyramiding = 0`

Still verify in Strategy Properties:

- Initial capital
- Currency
- Order fill assumptions
- Recalculate behavior (prefer stable bar-close behavior for this strategy style)

---

## 3) Webhook automation flow

Create TradingView alert from strategy:

1. Alert condition: this strategy
2. Trigger: **Order fills** (or equivalent strategy order event)
3. Webhook URL: your execution bridge endpoint
4. Message: use `{{strategy.order.alert_message}}`

Entry/exit JSON payloads are emitted by the script, e.g.:

```json
{
  "event":"entry",
  "bot":"ReversalScalpTV",
  "ticker":"ES1!",
  "side":"long",
  "qty":2,
  "entry":5123.25,
  "stop":5118.75,
  "target":5132.25,
  "timing":30
}
```

```json
{
  "event":"exit",
  "bot":"ReversalScalpTV",
  "ticker":"ES1!",
  "side":"long",
  "reason":"stop_or_target"
}
```

Your webhook bridge should:

- Validate auth/signature/IP allowlist
- Enforce symbol mapping (`ES1!` -> broker tradable contract)
- Enforce hard risk limits before sending orders
- Place bracket/OCO orders when possible
- Handle retries, idempotency, and duplicate alert protection

---

## 4) Recommended live risk controls

For real-money use, apply these safeguards:

- Max daily loss lockout
- Max consecutive losses lockout
- Max position size hard cap (independent of strategy qty)
- Session kill switch
- Reject orders outside configured trading window
- Kill switch on data/API disconnect

---

## 5) Operational checklist before going live

1. Run at least 2-4 weeks in paper.
2. Compare broker fills vs TradingView assumptions (slippage/commission).
3. Validate timezone/session alignment around DST transitions.
4. Confirm exact contract mapping during rollover weeks.
5. Start with minimum size before scaling.
