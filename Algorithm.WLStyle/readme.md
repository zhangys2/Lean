# WL-style C# strategy adapter

`QuantConnect.Algorithm.WLStyle` provides a per-symbol, per-bar API over Lean's
existing `QCAlgorithm` lifecycle. It is a clean-room adapter, not a WealthLab
runtime and does not promise binary or arbitrary source compatibility.

Derive a strategy from `WLStrategyBase`, implement `Initialize(BarHistory)` and
`Execute(BarHistory, int)`, and use `BarHistory` plus `TimeSeries` for the
delivered bars. Lean's `PlotIndicator` remains available through
`QCAlgorithm`; `PlotTimeSeries` plots the current value of a history-aligned
series through Lean's chart pipeline. `WLSeries` creates native Lean-backed
SMA, EMA, RSI, ATR, Bollinger Bands, MACD (MACD, signal, histogram), ADX
(ADX, +DI, -DI), and Stochastic (fast %K, slow %K, slow %D)
series. Indicators consume each delivered bar once, including fill-forward bars,
and expose values when the native indicator is ready; every output of a
composite (all three MACD, ADX, and Stochastic outputs) stays NaN until the
whole indicator is ready, even if that component is ready earlier. `AddParameter` reads
values through `GetParameter`, so Lean's existing parameter overrides remain
the source of values. The declared minimum, maximum, and step validate
overrides locally; they are not exported to Lean's optimizer configuration.

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
Market, limit, stop-market, stop-limit, market-on-open, and market-on-close
orders use Lean's native order tickets, fill models,
brokerage model, fee model, and portfolio accounting. Market orders are marked
asynchronous, but Lean's backtesting transaction handler processes the request
and scans orders synchronously during the submission call. The adapter's
signal-bar marker prevents a same-bar fill; the next real bar's scan may fill
the order at that bar's open. Do not assume backtest order submission is
nonblocking.

Market orders use an adapter fill-model wrapper to fill at the open of the next
non-fill-forward TradeBar, applying Lean's configured slippage model. Limit,
stop-market, and stop-limit orders use the security's configured native fill
model on the next real bar. For stop-limit, `stopPrice` is the trigger and
`price` is the limit; it shares the one-real-bar expiry rule. Stop-market uses
`price` as its stop trigger. Lean scans that bar before `OnData`; if the order
is still open,
the adapter requests cancellation in that bar's callback before the following
bar can be scanned. This remains true across sessions: a market order submitted
on the last real bar of a session fills at the open of the next session's
first real bar, with a fill time on that next day. For limit and stop-market
orders, that first real bar of the next session is the single eligible bar and
counts toward expiry; Lean scans it for a fill before the adapter cancels an
untouched order in that bar's callback. Fill-forward bars extend history and
invoke `Execute`, but are not eligible for fills or expiration (Lean does not
cache fill-forward bars, so the fill model sees the last real bar, which is not
later than the order's submission time). The fill model's `IsFillForward` check
is kept as a defence only; the engine cannot reach it. A missing
symbol bar does not advance its history or expiry. When a tradable bar gaps
through a stop, the native fill model applies its stop-market gap pricing; a
marketable limit uses the native limit fill price. Slippage, fees, buying power,
and brokerage restrictions remain Lean behavior.

Market-on-open and market-on-close orders map directly to Lean's corresponding
native order types and are not subject to adapter one-bar expiry. Lean controls
their eligible session open/close and submission cut-offs: an MOO submitted
before a weekend or holiday remains pending until the next session open. Lean
rejects a market-on-open order submitted while the regular session is open
(unless the symbol has only daily data) and returns an invalid ticket; the adapter
does not pre-check this. Submit it from the session's closing bar or from an
extended-hours bar. A
MOO submitted on a date with no session has no `Execute` call to submit it.
Lean rejects a market-on-close order submitted after its configured cutoff;
the adapter does not defer it.

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

## Portfolio strategies

Use `Execute` to collect per-symbol signals and `PostExecute` to coordinate
them after all bars in the current slice have been appended. `Execute` runs
symbol by symbol and is not a cross-symbol barrier: while one symbol's
`Execute` runs, a later symbol's history may still lack the current bar even
though Lean's security cache already holds it. Do not compute a cross-symbol
signal or submit a pairs leg from `Execute`.

A rotation can rank the latest aligned, real bars in `PostExecute` and submit
target changes there. For cross-symbol strategies, compare each history's
latest `DateTimes` and skip fill-forward or stale symbols.

`Portfolio`, `OpenPositions`, and `ClosePosition` see filled holdings only.
`PostExecute` can allocate a portfolio-level exposure cap, but the strategy
must count its own pending orders (for example through
`Transactions.GetOpenOrderTickets()`); otherwise a repeated signal can stack a
second order, and `ClosePosition` does nothing for an entry that has not filled.
Pairs can submit both legs from one `PostExecute` call when both real bars are
aligned. The legs are separate Lean orders, not an atomic fill: each leg needs
its own next real bar, so a market leg on an untraded symbol fills later at a
different price, and limit or stop legs expire independently, which can leave
one leg filled and the other canceled. Check both legs' fills and unwind or
complete the pair yourself.

For a symbol with only daily data, Lean converts a market order submitted while
the market is closed (including from the bar ending at the close) into a
market-on-open order, so it follows market-on-open rules and fills at the next
session open.

## Runnable example

`Algorithm.CSharp.WLStyle.SmaCrossWLStyle` demonstrates a per-bar SMA crossover
with Lean optimizer parameters `Fast` and `Slow`. It submits an explicit one-share
buy on a bullish cross and closes the actual filled holding on a bearish cross.
Run it with the normal C# Launcher by setting `algorithm-type-name` to
`SmaCrossWLStyle`; for example, `--parameters Fast:8,Slow:25` overrides the
defaults. The example's dates and SPY subscription are a small local-data smoke
scenario, not a recommendation or a new sizing policy.

## Example: dual momentum

`Algorithm.CSharp.WLStyle.DualMomentumRotationWLStyle` ranks SPY, QQQ, and IWM
by trailing 12-month adjusted return at each month end, holds the strongest
positive-return ETF, and otherwise stays in cash. It reimplements the rules in
Gary Antonacci, “Risk Premia Harvesting Through Dual Momentum” (2013,
[SSRN 2042750](https://papers.ssrn.com/sol3/papers.cfm?abstract_id=2042750));
it uses ETF proxies plus cash, not the paper's MSCI indices or Treasury bills.

Rule details and limits of this example:

- Returns compare Lean's default adjusted (split- and dividend-adjusted) daily
  closes: the last real close of the decision month against the last real close
  of the same month a year earlier. This approximates total return.
- The absolute-momentum test is a 12-month return above zero, because cash earns
  nothing in this backtest; the paper compares against Treasury-bill returns.
  Ties go to the earlier symbol in the order SPY, QQQ, IWM.
- The decision runs after the close of each month's last session. Lean converts
  the market orders to market-on-open orders. The old holding is exited first,
  and the new entry is submitted only after the exit fills, so it fills one
  session later.
- The local QQQ daily file has no bars from 2004-12-01 to 2011-03-22. QQQ is
  excluded from the decisions for December 2004 through February 2012, so those
  months rank only SPY and IWM.
- Positions are one share, so the statistics are not a performance estimate.
  The final decision submits an MOO for 2021-04-01, after the data ends, so it
  never fills (76 orders, 75 fills).
