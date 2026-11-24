# WL-style C# strategy adapter

`QuantConnect.Algorithm.WLStyle` provides a per-symbol, per-bar API over Lean's
existing `QCAlgorithm` lifecycle. It is a clean-room adapter, not a WealthLab
runtime and does not promise binary or arbitrary source compatibility.

Derive a strategy from `WLStrategyBase`, implement `Initialize(BarHistory)` and
`Execute(BarHistory, int)`, and use `BarHistory` plus `TimeSeries` for the
delivered bars. Lean's `PlotIndicator` remains available through
`QCAlgorithm`; `PlotTimeSeries` plots the current value of a history-aligned
series through Lean's chart pipeline. `WLSeries` creates native Lean-backed
SMA, EMA, RSI, ATR, and Bollinger Bands series. `AddParameter` reads values through
`GetParameter`, so Lean's existing parameter overrides remain the source of
values. The declared minimum, maximum, and step validate overrides locally;
they are not exported to Lean's optimizer configuration.

Each symbol has independent history and initialization state. `Initialize`
receives its first bar already appended; `Execute` receives the index of the
latest appended bar. `PreExecute` sees histories before the current slice is
appended, and `PostExecute` sees them afterward. Bars are processed in the
slice's collection order. A missing symbol bar adds no history row; a Lean
fill-forward bar is included and marked in `BarHistory.FillForward`. Fill-forward
bars also update streaming indicators and invoke `Execute`; check the current
flag before making decisions from a stale bar. A series can only be indexed
through its current history count, so it does not expose future bars.

## Orders and positions

`PlaceTrade` requires a positive decimal quantity on every call. `TransactionType`
sets direction; there is no implicit quantity or strategy-level sizing policy.
Market, limit, and stop-market orders use Lean's native order tickets, fill models,
brokerage model, fee model, and portfolio accounting. Market orders are marked
asynchronous, but Lean's backtesting transaction handler processes the request
and scans orders synchronously during the submission call. The adapter's
signal-bar marker prevents a same-bar fill; the next real bar's scan may fill
the order at that bar's open. Do not assume backtest order submission is
nonblocking.

Market orders use an adapter fill-model wrapper to fill at the open of the next
non-fill-forward TradeBar, applying Lean's configured slippage model. Limit and
stop-market orders use the security's configured native fill model on the next
real bar. Lean scans that bar before `OnData`; if the order is still open,
the adapter requests cancellation in that bar's callback before the following
bar can be scanned. This remains true across sessions: a market order submitted
on the last real bar of a session fills at the open of the next session's
first real bar, with a fill time on that next day. For limit and stop-market
orders, that first real bar of the next session is the single eligible bar and
counts toward expiry; Lean scans it for a fill before the adapter cancels an
untouched order in that bar's callback. Fill-forward bars extend history and
invoke `Execute`, but are not eligible for fills or expiration (Lean does not
cache fill-forward bars, so the fill model sees the last real bar, which is not
later than the order's submission time). A missing
symbol bar does not advance its history or expiry. When a tradable bar gaps
through a stop, the native fill model applies its stop-market gap pricing; a
marketable limit uses the native limit fill price. Slippage, fees, buying power,
and brokerage restrictions remain Lean behavior.

`ClosePosition`, `HasOpenPosition`, and `OpenPositions(bars)` read current Lean
holdings for that symbol, not order intent. `LastPosition(bars)` is Lean's most
recently closed trade for that symbol, generated from fills; it is `null` until
that symbol has a closed trade. Partial fills, trade grouping, and realized
accounting follow Lean's trade builder. The adapter wraps the security fill
model only for orders submitted by `PlaceTrade`; all other orders continue
through the configured native fill model unchanged. It copies only
`DefaultOrderProperties.TimeInForce`; extra fields from a
brokerage-specific order-properties subclass are not preserved. The wrapper
captures the security's current fill model on the first `PlaceTrade` call for
that symbol. Call `SetFillModel` before the first adapter trade; calling it
afterward replaces the adapter wrapper, and later adapter orders then use
Lean's native same-bar timing. These next-bar and one-bar limit/stop rules are
backtest adapter semantics; they do not guarantee live brokerage execution or
timing.

## Runnable example

`Algorithm.CSharp.WLStyle.SmaCrossWLStyle` demonstrates a per-bar SMA crossover
with Lean optimizer parameters `Fast` and `Slow`. It submits an explicit one-share
buy on a bullish cross and closes the actual filled holding on a bearish cross.
Run it with the normal C# Launcher by setting `algorithm-type-name` to
`SmaCrossWLStyle`; for example, `--parameters Fast:8,Slow:25` overrides the
defaults. The example's dates and SPY subscription are a small local-data smoke
scenario, not a recommendation or a new sizing policy.
