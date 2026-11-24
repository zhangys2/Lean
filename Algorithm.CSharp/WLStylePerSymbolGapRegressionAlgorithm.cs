/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Algorithm.WLStyle;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Positions;
using QuantConnect.Statistics;
using QuantConnect.Util;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Verifies symbol-scoped expiry and positions across an AIG data gap.
    /// </summary>
    public class WLStylePerSymbolGapRegressionAlgorithm : WLStrategyBase, IRegressionAlgorithmDefinition
    {
        private const string AigExpiryTag = "WL AIG expiry";
        private const string SpyEntryTag = "WL SPY entry";
        private const string AigEntryTag = "WL AIG entry";
        private const string SpyCloseTag = "WL SPY close";
        private const string AigCloseTag = "WL AIG close";
        private Symbol _spy;
        private Symbol _aig;
        private BarHistory _spyHistory;
        private BarHistory _aigHistory;
        private OrderTicket _aigExpiry;
        private DateTime _aigExpirySignalTime;
        private DateTime _aigExpiryTime;
        private int _aigRealBarsBeforeExpiry;
        private bool _spyStarted;
        private bool _aigStarted;
        private bool _aigExpirySubmitted;
        private bool _spyObservedAigGap;
        private bool _spyEntryFilled;
        private bool _aigEntryFilled;
        private bool _positionsChecked;
        private bool _spyCloseSubmitted;
        private bool _aigCloseSubmitted;
        private bool _spyCloseFilled;
        private bool _aigCloseFilled;
        private bool _lastPositionsChecked;

        public override void Initialize()
        {
            SetStartDate(2013, 10, 7);
            SetEndDate(2013, 10, 7);
            SetCash(100000);
            _spy = AddEquity("SPY", Resolution.Minute).Symbol;
            _aig = AddEquity("AIG", Resolution.Minute).Symbol;
        }

        protected override void Initialize(BarHistory bars)
        {
        }

        protected override void Execute(BarHistory bars, int idx)
        {
            var barEndTime = bars.DateTimes[idx];
            if (bars.Symbol == _spy)
            {
                _spyHistory = bars;
                if (barEndTime.Date == new DateTime(2013, 10, 7)
                    && barEndTime.TimeOfDay == new TimeSpan(13, 48, 0)
                    && !bars.FillForward[idx])
                {
                    if (_aigExpiry == null || _aigExpiry.Status.IsClosed())
                    {
                        throw new InvalidOperationException(
                            "SPY's real 13:48 bar canceled the AIG order during AIG's fill-forward gap.");
                    }
                    _spyObservedAigGap = true;
                }

                if (!_spyStarted)
                {
                    _spyStarted = true;
                    PlaceTrade(bars, TransactionType.Buy, OrderType.Market, 1m, signalName: SpyEntryTag);
                }
            }
            else if (bars.Symbol == _aig)
            {
                _aigHistory = bars;
                if (!_aigStarted)
                {
                    _aigStarted = true;
                    PlaceTrade(bars, TransactionType.Buy, OrderType.Market, 1m, signalName: AigEntryTag);
                }
                else if (barEndTime.Date == new DateTime(2013, 10, 7)
                    && barEndTime.TimeOfDay == new TimeSpan(13, 47, 0)
                    && !bars.FillForward[idx]
                    && !_aigExpirySubmitted)
                {
                    _aigExpirySubmitted = true;
                    _aigExpirySignalTime = barEndTime;
                    _aigExpiry = PlaceTrade(
                        bars,
                        TransactionType.Buy,
                        OrderType.Limit,
                        1m,
                        (decimal)bars.Close[idx] * 0.50m,
                        AigExpiryTag);
                }
                else if (_aigExpiry != null && !_aigExpiry.Status.IsClosed()
                    && barEndTime > _aigExpirySignalTime && !bars.FillForward[idx])
                {
                    _aigRealBarsBeforeExpiry++;
                }
            }

            if (_spyEntryFilled && _aigEntryFilled && !_positionsChecked)
            {
                if (!HasOnlyPosition(_spyHistory, _spy) || !HasOnlyPosition(_aigHistory, _aig))
                {
                    throw new InvalidOperationException(
                        "OpenPositions(BarHistory) returned a position for another symbol or omitted its own holding.");
                }
                _positionsChecked = true;
                _spyCloseSubmitted = ClosePosition(_spyHistory, SpyCloseTag) != null;
                _aigCloseSubmitted = ClosePosition(_aigHistory, AigCloseTag) != null;
            }

            if (_spyCloseFilled && _aigCloseFilled && !_lastPositionsChecked)
            {
                var spyTrade = LastPosition(_spyHistory);
                var aigTrade = LastPosition(_aigHistory);
                if (!HasOnlyTrade(spyTrade, _spy) || !HasOnlyTrade(aigTrade, _aig))
                {
                    throw new InvalidOperationException(
                        "LastPosition(BarHistory) returned a trade for another symbol or omitted its own closed trade.");
                }
                _lastPositionsChecked = true;
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            var order = Transactions.GetOrderById(orderEvent.OrderId);
            if (order == null)
            {
                return;
            }

            var isAigExpiry = _aigExpiry != null && order.Id == _aigExpiry.OrderId;
            if (isAigExpiry)
            {
                if (orderEvent.Status != OrderStatus.Canceled)
                {
                    if (orderEvent.Status == OrderStatus.Filled)
                    {
                        throw new InvalidOperationException($"Untouched AIG expiry order {order.Id} unexpectedly filled.");
                    }
                    return;
                }

                var security = Securities[order.Symbol];
                var bar = security.Cache.GetData<TradeBar>();
                if (bar == null || bar.IsFillForward
                    || orderEvent.UtcTime != bar.EndTime.ConvertToUtc(security.Exchange.TimeZone))
                {
                    throw new InvalidOperationException(
                        $"AIG limit was canceled on another symbol's bar, not AIG's next real bar: " +
                        $"event={orderEvent.UtcTime:o}, AIG cache={bar?.EndTime:o}.");
                }

                if (_aigRealBarsBeforeExpiry != 1 || bar.EndTime != _aigExpirySignalTime.AddMinutes(2)
                    || bar.EndTime <= _aigExpirySignalTime)
                {
                    throw new InvalidOperationException(
                        $"AIG expiry did not wait through the SPY-only real bar and cancel on AIG's next real bar: {bar.EndTime:o}.");
                }
                _aigExpiryTime = bar.EndTime;
                return;
            }

            if (orderEvent.Status != OrderStatus.Filled)
            {
                return;
            }

            if (order.Tag == SpyEntryTag)
            {
                _spyEntryFilled = true;
            }
            else if (order.Tag == AigEntryTag)
            {
                _aigEntryFilled = true;
            }
            else if (order.Tag == SpyCloseTag)
            {
                _spyCloseFilled = true;
            }
            else if (order.Tag == AigCloseTag)
            {
                _aigCloseFilled = true;
            }
        }

        public override void OnEndOfAlgorithm()
        {
            if (!_spyStarted || !_aigStarted
                || !_aigExpirySubmitted || !_spyObservedAigGap
                || _aigExpiryTime == default
                || !_spyEntryFilled || !_aigEntryFilled || !_positionsChecked
                || !_spyCloseSubmitted || !_aigCloseSubmitted
                || !_spyCloseFilled || !_aigCloseFilled || !_lastPositionsChecked)
            {
                throw new InvalidOperationException(
                    $"Missing per-symbol gap result: AIG expiry={_aigExpiryTime:o}, SPY observed gap={_spyObservedAigGap}, " +
                    $"entries={_spyEntryFilled}/{_aigEntryFilled}, positions={_positionsChecked}, " +
                    $"closes={_spyCloseFilled}/{_aigCloseFilled}, last positions={_lastPositionsChecked}.");
            }
            base.OnEndOfAlgorithm();
        }

        private bool HasOnlyPosition(BarHistory bars, Symbol symbol)
        {
            var positions = OpenPositions(bars);
            return positions.Count == 1 && positions[0].Symbol == symbol;
        }

        private static bool HasOnlyTrade(Trade trade, Symbol symbol)
        {
            return trade?.Symbols != null && trade.Symbols.Count == 1 && trade.Symbols.Contains(symbol);
        }

        public bool CanRunLocally { get; } = true;

        public List<Language> Languages { get; } = new() { Language.CSharp };

        public long DataPoints => 1575;

        public int AlgorithmHistoryDataPoints => 0;

        public AlgorithmStatus AlgorithmStatus => AlgorithmStatus.Completed;

        public Dictionary<string, string> ExpectedStatistics => new()
        {
            { "Total Orders", "5" },
            { "Average Win", "0%" },
            { "Average Loss", "0%" },
            { "Compounding Annual Return", "0%" },
            { "Drawdown", "0%" },
            { "Expectancy", "0" },
            { "Start Equity", "100000" },
            { "End Equity", "99997.04" },
            { "Net Profit", "0%" },
            { "Sharpe Ratio", "0" },
            { "Sortino Ratio", "0" },
            { "Probabilistic Sharpe Ratio", "0%" },
            { "Loss Rate", "0%" },
            { "Win Rate", "0%" },
            { "Profit-Loss Ratio", "0" },
            { "Alpha", "0" },
            { "Beta", "0" },
            { "Annual Standard Deviation", "0" },
            { "Annual Variance", "0" },
            { "Information Ratio", "0" },
            { "Tracking Error", "0" },
            { "Treynor Ratio", "0" },
            { "Total Fees", "$2.83" },
            { "Estimated Strategy Capacity", "$1300000.00" },
            { "Lowest Capacity Asset", "AIG R735QTJ8XC9X" },
            { "Portfolio Turnover", "0.37%" },
            { "Drawdown Recovery", "0" },
            { "OrderListHash", "ee53618be311cf4e4701972085605649" }
        };
    }
}
