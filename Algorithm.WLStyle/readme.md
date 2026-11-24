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
optimizer values.

Each symbol has independent history and initialization state. `Initialize`
receives its first bar already appended; `Execute` receives the index of the
latest appended bar. `PreExecute` sees histories before the current slice is
appended, and `PostExecute` sees them afterward. Bars are processed in the
slice's collection order. A missing symbol bar adds no history row; a Lean
fill-forward bar is included and marked in `BarHistory.FillForward`. A series
can only be indexed through its current history count, so it does not expose
future bars.

## Orders and positions

`PlaceTrade` requires a positive decimal quantity on every call. `TransactionType`
sets direction; there is no implicit quantity or strategy-level sizing policy.
Market, limit, and stop-market orders use Lean's native order tickets, fill models,
brokerage model, fee model, and portfolio accounting. Market orders submitted by
the strategy are asynchronous so the algorithm does not wait for a fill inside
`OnData`.

Market orders use an adapter fill-model wrapper to fill at the open of the next
non-fill-forward TradeBar, applying Lean's configured slippage model. Limit and
stop-market orders use the security's configured native fill model on that next
bar only; if unfilled, the adapter requests cancellation on the following real
bar, after the eligible bar has passed through Lean's transaction processing.
Fill-forward bars extend history but are not eligible for fills or order
expiration. A missing symbol bar does not advance its history or expiry. When a
tradable bar gaps through a stop, the native fill model applies its stop-market
gap pricing; a marketable limit uses the native limit fill price. Slippage, fees,
buying power, and brokerage restrictions remain Lean behavior.

`ClosePosition`, `HasOpenPosition`, and `OpenPositions` read current Lean portfolio
holdings, not order intent. `LastPosition` is Lean's most recently closed trade,
generated from fills; it is `null` until a position has closed. Partial fills,
trade grouping, and realized accounting follow Lean's trade builder. The adapter
wraps the security fill model only for orders submitted by `PlaceTrade`; all other
orders continue through the configured native fill model unchanged. These
next-bar and one-bar limit/stop rules are backtest adapter semantics; they do not
guarantee live brokerage execution or timing.

## Runnable example

`Algorithm.CSharp.WLStyle.SmaCrossWLStyle` demonstrates a per-bar SMA crossover
with Lean optimizer parameters `Fast` and `Slow`. It submits an explicit one-share
buy on a bullish cross and closes the actual filled holding on a bearish cross.
Run it with the normal C# Launcher by setting `algorithm-type-name` to
`SmaCrossWLStyle`; for example, `--parameters Fast:8,Slow:25` overrides the
defaults. The example's dates and SPY subscription are a small local-data smoke
scenario, not a recommendation or a new sizing policy.
