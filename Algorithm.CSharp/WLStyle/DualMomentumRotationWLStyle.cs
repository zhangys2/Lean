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
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Statistics;

namespace QuantConnect.Algorithm.CSharp.WLStyle
{
    public static class DualMomentumSignal
    {
        public static Symbol SelectTopPositiveMomentum(
            IReadOnlyList<BarHistory> histories,
            IReadOnlyList<Symbol> rankingOrder,
            DateTime currentEndTime)
        {
            var sessionDate = currentEndTime.AddTicks(-1).Date;
            var lookbackDate = sessionDate.AddMonths(-12);
            var selectedSymbol = (Symbol)null;
            var selectedReturn = 0d;

            foreach (var symbol in rankingOrder)
            {
                BarHistory history = null;
                for (var historyIndex = 0; historyIndex < histories.Count; historyIndex++)
                {
                    if (histories[historyIndex].Symbol == symbol)
                    {
                        history = histories[historyIndex];
                        break;
                    }
                }

                if (history == null || history.Count == 0)
                {
                    continue;
                }

                var currentIndex = history.Count - 1;
                if (history.DateTimes[currentIndex] != currentEndTime || history.FillForward[currentIndex])
                {
                    continue;
                }

                var previousIndex = -1;
                for (var index = currentIndex - 1; index >= 0; index--)
                {
                    var previousSessionDate = history.DateTimes[index].AddTicks(-1).Date;
                    if (previousSessionDate.Year == lookbackDate.Year && previousSessionDate.Month == lookbackDate.Month)
                    {
                        if (!history.FillForward[index])
                        {
                            previousIndex = index;
                            break;
                        }
                    }
                }

                if (previousIndex < 0)
                {
                    continue;
                }

                var previousClose = history.Close[previousIndex];
                var currentClose = history.Close[currentIndex];
                if (previousClose <= 0 || !double.IsFinite(previousClose) || !double.IsFinite(currentClose))
                {
                    continue;
                }

                var momentum = currentClose / previousClose - 1d;
                if (momentum > selectedReturn)
                {
                    selectedSymbol = symbol;
                    selectedReturn = momentum;
                }
            }

            return selectedSymbol;
        }
    }

    /// <summary>
    /// Monthly relative and absolute momentum rotation using local ETF proxies and cash.
    /// </summary>
    public sealed class DualMomentumRotationWLStyle : WLStrategyBase, IRegressionAlgorithmDefinition
    {
        private const string OrderTagPrefix = "Dual momentum";
        private readonly List<Symbol> _rankingOrder = new(3);
        private Symbol _spy;
        private Symbol _desiredSymbol;
        private DateTime _lastDecisionMonth;
        private bool _rebalancePending;
        private int _switchCount;
        private int _filledOrderCount;
        private int _maximumFilledHoldings;

        public bool CanRunLocally => true;

        public List<Language> Languages => new() { Language.CSharp };

        public long DataPoints => 51001;

        public int AlgorithmHistoryDataPoints => 0;

        public AlgorithmStatus AlgorithmStatus => AlgorithmStatus.Completed;

        public Dictionary<string, string> ExpectedStatistics => Pin(
            ("Total Orders", "76"),
            ("Average Win", "0.01%"),
            ("Average Loss", "0.00%"),
            ("Compounding Annual Return", "0.012%"),
            ("Drawdown", "0.100%"),
            ("Expectancy", "1.101"),
            ("Start Equity", "100000"),
            ("End Equity", "100232.75"),
            ("Net Profit", "0.233%"),
            ("Sharpe Ratio", "-96.524"),
            ("Sortino Ratio", "-79.349"),
            ("Probabilistic Sharpe Ratio", "0%"),
            ("Loss Rate", "46%"),
            ("Win Rate", "54%"),
            ("Profit-Loss Ratio", "2.89"),
            ("Alpha", "-0.02"),
            ("Beta", "0.001"),
            ("Annual Standard Deviation", "0"),
            ("Annual Variance", "0"),
            ("Information Ratio", "-0.426"),
            ("Tracking Error", "0.16"),
            ("Treynor Ratio", "-25.44"),
            ("Total Fees", "$59.91"),
            ("Estimated Strategy Capacity", "$55000000000.00"),
            ("Lowest Capacity Asset", "QQQ RIWIV7K5Z9LX"),
            ("Portfolio Turnover", "0.00%"),
            ("Drawdown Recovery", "1149"),
            ("OrderListHash", "704e35d41efdce21e646710349e71782"));

        public override void Initialize()
        {
            SetStartDate(2001, 6, 1);
            SetEndDate(2021, 3, 31);
            SetCash(100000);
            SetWarmUp(252, Resolution.Daily);

            _spy = AddEquity("SPY", Resolution.Daily).Symbol;
            _rankingOrder.Add(_spy);
            _rankingOrder.Add(AddEquity("QQQ", Resolution.Daily).Symbol);
            _rankingOrder.Add(AddEquity("IWM", Resolution.Daily).Symbol);
        }

        protected override void Initialize(BarHistory bars)
        {
        }

        protected override void Execute(BarHistory bars, int idx)
        {
        }

        protected override void PostExecute(DateTime time, List<BarHistory> histories)
        {
            if (IsWarmingUp)
            {
                return;
            }

            var spyHistory = FindHistory(histories, _spy);
            if (spyHistory == null || spyHistory.Count == 0)
            {
                return;
            }

            var currentEndTime = spyHistory.DateTimes[spyHistory.Count - 1];
            if (!IsCurrentRealBar(spyHistory, currentEndTime))
            {
                return;
            }

            var sessionDate = currentEndTime.AddTicks(-1).Date;
            var nextMarketOpen = Securities[_spy].Exchange.Hours.GetNextMarketOpen(currentEndTime.AddTicks(-1), false);
            if ((nextMarketOpen.Year == sessionDate.Year && nextMarketOpen.Month == sessionDate.Month)
                || (_lastDecisionMonth.Year == sessionDate.Year && _lastDecisionMonth.Month == sessionDate.Month))
            {
                AdvanceRebalance(histories, currentEndTime, sessionDate);
                return;
            }

            _lastDecisionMonth = sessionDate;
            var selected = DualMomentumSignal.SelectTopPositiveMomentum(histories, _rankingOrder, currentEndTime);
            Log($"DUAL-MOMENTUM {sessionDate:yyyy-MM-dd} target={selected?.Value ?? "CASH"}");

            if (selected != _desiredSymbol)
            {
                _desiredSymbol = selected;
                _rebalancePending = true;
                _switchCount++;
            }

            AdvanceRebalance(histories, currentEndTime, sessionDate);
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            var order = Transactions.GetOrderById(orderEvent.OrderId);
            if (order == null || !order.Tag.StartsWith(OrderTagPrefix, StringComparison.Ordinal))
            {
                return;
            }

            Ensure(order.Type == OrderType.MarketOnOpen,
                $"Daily rotation order {order.Id} was {order.Type}, not the expected native MOO.");
            Ensure(orderEvent.Status != OrderStatus.Invalid,
                $"Dual-momentum order {order.Id} was rejected: {orderEvent.Message}");

            if (orderEvent.Status != OrderStatus.Filled)
            {
                return;
            }

            _filledOrderCount++;
            var holdings = 0;
            foreach (var symbol in _rankingOrder)
            {
                if (Portfolio[symbol].Quantity != 0)
                {
                    holdings++;
                }
            }

            _maximumFilledHoldings = Math.Max(_maximumFilledHoldings, holdings);
            Ensure(holdings <= 1, $"Found {holdings} simultaneous filled ETF holdings.");
        }

        public override void OnEndOfAlgorithm()
        {
            var filledHoldings = 0;
            foreach (var symbol in _rankingOrder)
            {
                if (Portfolio[symbol].Quantity != 0)
                {
                    filledHoldings++;
                }
            }

            Ensure(_switchCount is >= 20 and <= 80,
                $"Expected 20-80 target switches, observed {_switchCount}.");
            Ensure(_maximumFilledHoldings <= 1 && filledHoldings <= 1,
                $"Maximum filled holdings was {_maximumFilledHoldings}; final holdings {filledHoldings}.");
            Log($"DUAL-MOMENTUM switches={_switchCount} fills={_filledOrderCount} maxHoldings={_maximumFilledHoldings}");
            base.OnEndOfAlgorithm();
        }

        private void AdvanceRebalance(List<BarHistory> histories, DateTime currentEndTime, DateTime sessionDate)
        {
            if (!_rebalancePending)
            {
                return;
            }

            foreach (var symbol in _rankingOrder)
            {
                if (symbol == _desiredSymbol || Portfolio[symbol].Quantity == 0)
                {
                    continue;
                }

                if (HasOpenAdapterOrder(symbol))
                {
                    return;
                }

                var exitHistory = FindHistory(histories, symbol);
                Ensure(exitHistory != null, $"Missing history for held symbol {symbol}.");
                ClosePosition(exitHistory, $"{OrderTagPrefix} exit {sessionDate:yyyyMMdd}");
                return;
            }

            if (HasOpenAdapterOrder())
            {
                return;
            }

            if (_desiredSymbol == null || Portfolio[_desiredSymbol].Quantity != 0)
            {
                _rebalancePending = false;
                return;
            }

            var entryHistory = FindHistory(histories, _desiredSymbol);
            if (!IsCurrentRealBar(entryHistory, currentEndTime))
            {
                return;
            }

            PlaceTrade(
                entryHistory,
                TransactionType.Buy,
                OrderType.Market,
                1m,
                signalName: $"{OrderTagPrefix} entry {sessionDate:yyyyMMdd}");
            _rebalancePending = false;
        }

        private bool HasOpenAdapterOrder(Symbol symbol = null)
        {
            return Transactions.GetOpenOrderTickets().Any(ticket =>
                (symbol == null || ticket.Symbol == symbol)
                && ticket.Tag.StartsWith(OrderTagPrefix, StringComparison.Ordinal));
        }

        private static bool IsCurrentRealBar(BarHistory history, DateTime endTime)
        {
            return history != null
                && history.Count > 0
                && history.DateTimes[history.Count - 1] == endTime
                && !history.FillForward[history.Count - 1];
        }

        private static BarHistory FindHistory(List<BarHistory> histories, Symbol symbol)
        {
            foreach (var history in histories)
            {
                if (history.Symbol == symbol)
                {
                    return history;
                }
            }
            return null;
        }

        private static Dictionary<string, string> Pin(params (string Key, string Value)[] statistics)
        {
            var result = new Dictionary<string, string>(statistics.Length);
            foreach (var (key, value) in statistics)
            {
                result.Add(key, value);
            }
            return result;
        }

        private static void Ensure(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
