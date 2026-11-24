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
using QuantConnect.Algorithm.WLStyle;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Exercises WL-style order timing through the backtesting transaction handler.
    /// </summary>
    public class WLStyleOrderTimingRegressionAlgorithm : WLStrategyBase, IRegressionAlgorithmDefinition
    {
        private const string MarketTag = "WL regression market";
        private const string MarketableLimitTag = "WL regression marketable limit";
        private const string UntouchedLimitTag = "WL regression untouched limit";
        private bool _ordersSubmitted;
        private DateTime _signalBarEndTime;
        private int _untouchedLimitOrderId;
        private bool _marketFilled;
        private bool _marketableLimitFilled;
        private bool _untouchedLimitCancelled;

        public override void Initialize()
        {
            SetStartDate(2013, 10, 7);
            SetEndDate(2013, 10, 11);
            SetCash(100000);
            AddEquity("SPY", Resolution.Minute);
        }

        protected override void Initialize(BarHistory bars)
        {
        }

        protected override void Execute(BarHistory bars, int idx)
        {
            if (_ordersSubmitted)
            {
                return;
            }

            _ordersSubmitted = true;
            _signalBarEndTime = bars.DateTimes[idx];
            var close = (decimal)bars.Close[idx];

            PlaceTrade(bars, TransactionType.Buy, OrderType.Market, 1m, signalName: MarketTag);
            PlaceTrade(bars, TransactionType.Buy, OrderType.Limit, 1m, close * 1.10m, MarketableLimitTag);
            var untouchedLimit = PlaceTrade(
                bars,
                TransactionType.Buy,
                OrderType.Limit,
                1m,
                close * 0.50m,
                UntouchedLimitTag);
            _untouchedLimitOrderId = untouchedLimit.OrderId;
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            var order = Transactions.GetOrderById(orderEvent.OrderId);
            if (order == null
                || (order.Id != _untouchedLimitOrderId
                    && order.Tag != MarketTag
                    && order.Tag != MarketableLimitTag
                    && order.Tag != UntouchedLimitTag))
            {
                return;
            }

            if (orderEvent.Status == OrderStatus.Filled)
            {
                if (orderEvent.UtcTime <= order.Time)
                {
                    throw new InvalidOperationException(
                        $"Adapter order {order.Id} filled at {orderEvent.UtcTime}, not after submit time {order.Time}.");
                }

                var security = Securities[order.Symbol];
                var bar = security.Cache.GetData<TradeBar>();
                if (bar == null || bar.IsFillForward
                    || bar.EndTime.ConvertToUtc(security.Exchange.TimeZone) != orderEvent.UtcTime)
                {
                    throw new InvalidOperationException(
                        $"Adapter order {order.Id} did not fill at the current real bar's end time.");
                }

                if (order.Tag == MarketTag)
                {
                    if (orderEvent.FillPrice != bar.Open)
                    {
                        throw new InvalidOperationException(
                            $"Market fill {orderEvent.FillPrice} did not equal next-bar open {bar.Open}.");
                    }
                    _marketFilled = true;
                }
                else if (order.Tag == MarketableLimitTag)
                {
                    _marketableLimitFilled = true;
                }
            }
            else if (orderEvent.Status == OrderStatus.Canceled && order.Id == _untouchedLimitOrderId)
            {
                var security = Securities[order.Symbol];
                var bar = security.Cache.GetData<TradeBar>();
                var expectedEndTime = _signalBarEndTime.AddMinutes(1);
                if (bar == null || bar.IsFillForward || bar.EndTime != expectedEndTime)
                {
                    throw new InvalidOperationException(
                        $"Untouched limit was not canceled on its single eligible real bar; expected {expectedEndTime}, got {bar?.EndTime}.");
                }
                _untouchedLimitCancelled = true;
            }
        }

        public override void OnEndOfAlgorithm()
        {
            if (!_marketFilled || !_marketableLimitFilled || !_untouchedLimitCancelled)
            {
                throw new InvalidOperationException(
                    $"Missing engine order result: market={_marketFilled}, marketable limit={_marketableLimitFilled}, untouched cancellation={_untouchedLimitCancelled}.");
            }

            base.OnEndOfAlgorithm();
        }

        public bool CanRunLocally { get; } = true;

        public List<Language> Languages { get; } = new() { Language.CSharp };

        public long DataPoints => 3943;

        public int AlgorithmHistoryDataPoints => 0;

        public AlgorithmStatus AlgorithmStatus => AlgorithmStatus.Completed;

        public Dictionary<string, string> ExpectedStatistics => new()
        {
            { "Total Orders", "3" },
            { "Average Win", "0%" },
            { "Average Loss", "0%" },
            { "Compounding Annual Return", "0.227%" },
            { "Drawdown", "0.000%" },
            { "Expectancy", "0" },
            { "Start Equity", "100000" },
            { "End Equity", "100002.89" },
            { "Net Profit", "0.003%" },
            { "Sharpe Ratio", "-6.811" },
            { "Sortino Ratio", "0" },
            { "Probabilistic Sharpe Ratio", "21.197%" },
            { "Loss Rate", "0%" },
            { "Win Rate", "0%" },
            { "Profit-Loss Ratio", "0" },
            { "Alpha", "-0.01" },
            { "Beta", "0.003" },
            { "Annual Standard Deviation", "0.001" },
            { "Annual Variance", "0" },
            { "Information Ratio", "-8.922" },
            { "Tracking Error", "0.222" },
            { "Treynor Ratio", "-1.516" },
            { "Total Fees", "$2.00" },
            { "Estimated Strategy Capacity", "$950000000.00" },
            { "Lowest Capacity Asset", "SPY R735QTJ8XC9X" },
            { "Portfolio Turnover", "0.06%" },
            { "Drawdown Recovery", "3" },
            { "OrderListHash", "bef165b8ea25307e44f2818149ae7c0d" }
        };
    }
}
