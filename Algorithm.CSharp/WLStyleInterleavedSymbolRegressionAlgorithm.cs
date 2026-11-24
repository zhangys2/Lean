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
    /// Verifies independent expiry and symbol-scoped positions with interleaved SPY and IBM bars.
    /// </summary>
    public class WLStyleInterleavedSymbolRegressionAlgorithm : WLStrategyBase, IRegressionAlgorithmDefinition
    {
        private const string SpyExpiryTag = "WL SPY expiry";
        private const string IbmExpiryTag = "WL IBM expiry";
        private const string SpyEntryTag = "WL SPY entry";
        private const string IbmEntryTag = "WL IBM entry";
        private const string SpyCloseTag = "WL SPY close";
        private const string IbmCloseTag = "WL IBM close";
        private Symbol _spy;
        private Symbol _ibm;
        private BarHistory _spyHistory;
        private BarHistory _ibmHistory;
        private OrderTicket _spyExpiry;
        private OrderTicket _ibmExpiry;
        private DateTime _spyExpirySignalTime;
        private DateTime _ibmExpirySignalTime;
        private DateTime _spyExpiryTime;
        private DateTime _ibmExpiryTime;
        private int _spyRealBarsBeforeExpiry;
        private int _ibmRealBarsBeforeExpiry;
        private bool _spyStarted;
        private bool _ibmStarted;
        private bool _ibmExpirySubmitted;
        private bool _spyEntryFilled;
        private bool _ibmEntryFilled;
        private bool _positionsChecked;
        private bool _spyCloseSubmitted;
        private bool _ibmCloseSubmitted;
        private bool _spyCloseFilled;
        private bool _ibmCloseFilled;
        private bool _lastPositionsChecked;

        public override void Initialize()
        {
            SetStartDate(2013, 10, 7);
            SetEndDate(2013, 10, 7);
            SetCash(100000);
            _spy = AddEquity("SPY", Resolution.Minute).Symbol;
            _ibm = AddEquity("IBM", Resolution.Minute).Symbol;
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
                if (!_spyStarted)
                {
                    _spyStarted = true;
                    _spyExpirySignalTime = barEndTime;
                    _spyExpiry = PlaceTrade(
                        bars,
                        TransactionType.Buy,
                        OrderType.Limit,
                        1m,
                        (decimal)bars.Close[idx] * 0.50m,
                        SpyExpiryTag);
                    PlaceTrade(bars, TransactionType.Buy, OrderType.Market, 1m, signalName: SpyEntryTag);
                }
                else if (_spyExpiry != null && !_spyExpiry.Status.IsClosed()
                    && barEndTime > _spyExpirySignalTime && !bars.FillForward[idx])
                {
                    _spyRealBarsBeforeExpiry++;
                }

                if (!_ibmExpirySubmitted && idx > 0 && _ibmHistory != null)
                {
                    _ibmExpirySubmitted = true;
                    _ibmExpirySignalTime = Securities[_ibm].LocalTime;
                    _ibmExpiry = PlaceTrade(
                        _ibmHistory,
                        TransactionType.Buy,
                        OrderType.Limit,
                        1m,
                        (decimal)_ibmHistory.Close[_ibmHistory.Count - 1] * 0.50m,
                        IbmExpiryTag);
                }
            }
            else if (bars.Symbol == _ibm)
            {
                _ibmHistory = bars;
                if (!_ibmStarted)
                {
                    _ibmStarted = true;
                    PlaceTrade(bars, TransactionType.Buy, OrderType.Market, 1m, signalName: IbmEntryTag);
                }
                else if (_ibmExpiry != null && !_ibmExpiry.Status.IsClosed()
                    && barEndTime > _ibmExpirySignalTime && !bars.FillForward[idx])
                {
                    _ibmRealBarsBeforeExpiry++;
                }
            }

            if (_spyEntryFilled && _ibmEntryFilled && !_positionsChecked)
            {
                if (!HasOnlyPosition(_spyHistory, _spy) || !HasOnlyPosition(_ibmHistory, _ibm))
                {
                    throw new InvalidOperationException(
                        "OpenPositions(BarHistory) returned a position for another symbol or omitted its own holding.");
                }
                _positionsChecked = true;
                _spyCloseSubmitted = ClosePosition(_spyHistory, SpyCloseTag) != null;
                _ibmCloseSubmitted = ClosePosition(_ibmHistory, IbmCloseTag) != null;
            }

            if (_spyCloseFilled && _ibmCloseFilled && !_lastPositionsChecked)
            {
                var spyTrade = LastPosition(_spyHistory);
                var ibmTrade = LastPosition(_ibmHistory);
                if (!HasOnlyTrade(spyTrade, _spy) || !HasOnlyTrade(ibmTrade, _ibm))
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

            var isSpyExpiry = _spyExpiry != null && order.Id == _spyExpiry.OrderId;
            var isIbmExpiry = _ibmExpiry != null && order.Id == _ibmExpiry.OrderId;
            if (isSpyExpiry || isIbmExpiry)
            {
                if (orderEvent.Status != OrderStatus.Canceled)
                {
                    if (orderEvent.Status == OrderStatus.Filled)
                    {
                        throw new InvalidOperationException($"Untouched expiry order {order.Id} unexpectedly filled.");
                    }
                    return;
                }

                var security = Securities[order.Symbol];
                var bar = security.Cache.GetData<TradeBar>();
                if (bar == null || bar.IsFillForward
                    || orderEvent.UtcTime != bar.EndTime.ConvertToUtc(security.Exchange.TimeZone))
                {
                    throw new InvalidOperationException(
                        $"Expiry for {order.Symbol} was not processed on that symbol's current real bar.");
                }

                if (isSpyExpiry)
                {
                    if (_spyRealBarsBeforeExpiry != 1 || bar.EndTime <= _spyExpirySignalTime)
                    {
                        throw new InvalidOperationException("SPY expiry did not count exactly its first real bar.");
                    }
                    _spyExpiryTime = bar.EndTime;
                }
                else
                {
                    if (_ibmRealBarsBeforeExpiry != 1 || bar.EndTime <= _ibmExpirySignalTime)
                    {
                        throw new InvalidOperationException("IBM expiry did not count exactly its first real bar.");
                    }
                    _ibmExpiryTime = bar.EndTime;
                }
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
            else if (order.Tag == IbmEntryTag)
            {
                _ibmEntryFilled = true;
            }
            else if (order.Tag == SpyCloseTag)
            {
                _spyCloseFilled = true;
            }
            else if (order.Tag == IbmCloseTag)
            {
                _ibmCloseFilled = true;
            }
        }

        public override void OnEndOfAlgorithm()
        {
            if (!_spyStarted || !_ibmStarted
                || !_ibmExpirySubmitted
                || _spyExpiryTime == default || _ibmExpiryTime == default
                || _spyExpiryTime >= _ibmExpiryTime
                || !_spyEntryFilled || !_ibmEntryFilled || !_positionsChecked
                || !_spyCloseSubmitted || !_ibmCloseSubmitted
                || !_spyCloseFilled || !_ibmCloseFilled || !_lastPositionsChecked)
            {
                throw new InvalidOperationException(
                    $"Missing interleaved symbol result: SPY expiry={_spyExpiryTime:o}, IBM expiry={_ibmExpiryTime:o}, " +
                    $"entries={_spyEntryFilled}/{_ibmEntryFilled}, positions={_positionsChecked}, " +
                    $"closes={_spyCloseFilled}/{_ibmCloseFilled}, last positions={_lastPositionsChecked}.");
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
            { "Total Orders", "6" },
            { "Average Win", "0%" },
            { "Average Loss", "0%" },
            { "Compounding Annual Return", "0%" },
            { "Drawdown", "0%" },
            { "Expectancy", "0" },
            { "Start Equity", "100000" },
            { "End Equity", "99996.23" },
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
            { "Total Fees", "$4.00" },
            { "Estimated Strategy Capacity", "$2200000.00" },
            { "Lowest Capacity Asset", "IBM R735QTJ8XC9X" },
            { "Portfolio Turnover", "0.56%" },
            { "Drawdown Recovery", "0" },
            { "OrderListHash", "8d9608c08f0c4e8bb9a6f6a05ac1197c" }
        };
    }
}
