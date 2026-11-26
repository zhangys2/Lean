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
using QuantConnect.Util;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Verifies AIG standard-data fill-forward bars neither fill nor expire adapter orders.
    /// </summary>
    public class WLStyleFillForwardRegressionAlgorithm : WLStrategyBase, IRegressionAlgorithmDefinition
    {
        private const string MarketTag = "WL fill-forward market";
        private const string LimitTag = "WL fill-forward untouched limit";
        private OrderTicket _marketOrder;
        private OrderTicket _limitOrder;
        private DateTime _signalBarEndTime;
        private int _fillForwardBars;
        private int _realBarsBeforeLimitExpiry;
        private bool _submitted;
        private bool _marketFilledOnRealBar;
        private bool _limitCanceledAfterGap;

        public override void Initialize()
        {
            SetStartDate(2013, 10, 7);
            SetEndDate(2013, 10, 7);
            SetCash(100000);
            AddEquity("AIG", Resolution.Minute);
        }

        protected override void Initialize(BarHistory bars)
        {
        }

        protected override void Execute(BarHistory bars, int idx)
        {
            var barEndTime = bars.DateTimes[idx];
            if (!_submitted && !bars.FillForward[idx]
                && barEndTime.Date == new DateTime(2013, 10, 7)
                && barEndTime.TimeOfDay == new TimeSpan(13, 47, 0))
            {
                _submitted = true;
                _signalBarEndTime = barEndTime;
                _marketOrder = PlaceTrade(
                    bars,
                    TransactionType.Buy,
                    OrderType.Market,
                    1m,
                    signalName: MarketTag);
                _limitOrder = PlaceTrade(
                    bars,
                    TransactionType.Buy,
                    OrderType.Limit,
                    1m,
                    (decimal)bars.Close[idx] * 0.50m,
                    LimitTag);
                return;
            }

            if (!_submitted)
            {
                return;
            }

            if (bars.FillForward[idx])
            {
                _fillForwardBars++;
                if ((!_marketFilledOnRealBar && _marketOrder.Status.IsClosed())
                    || (!_limitCanceledAfterGap && _limitOrder.Status.IsClosed()))
                {
                    throw new InvalidOperationException(
                        $"An adapter order filled or expired on fill-forward bar {barEndTime:o}.");
                }
            }
            else if (!_limitOrder.Status.IsClosed() && barEndTime > _signalBarEndTime)
            {
                _realBarsBeforeLimitExpiry++;
            }

        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            var order = Transactions.GetOrderById(orderEvent.OrderId);
            if (order == null)
            {
                return;
            }

            var isMarketOrder = _marketOrder != null && order.Id == _marketOrder.OrderId;
            var isLimitOrder = _limitOrder != null && order.Id == _limitOrder.OrderId;
            if (!isMarketOrder && !isLimitOrder)
            {
                return;
            }

            if (orderEvent.Status == OrderStatus.Filled)
            {
                var security = Securities[order.Symbol];
                var bar = security.Cache.GetData<TradeBar>();
                if (isLimitOrder)
                {
                    throw new InvalidOperationException("Untouched limit order unexpectedly filled.");
                }

                if (bar == null || bar.IsFillForward || orderEvent.UtcTime <= order.Time
                    || orderEvent.UtcTime != bar.EndTime.ConvertToUtc(security.Exchange.TimeZone))
                {
                    throw new InvalidOperationException(
                        $"Market order filled on a fill-forward or non-current bar at {orderEvent.UtcTime:o}.");
                }
                _marketFilledOnRealBar = true;
                return;
            }

            if (!isLimitOrder || orderEvent.Status != OrderStatus.Canceled)
            {
                return;
            }

            var limitSecurity = Securities[order.Symbol];
            var limitBar = limitSecurity.Cache.GetData<TradeBar>();
            if (_fillForwardBars == 0 || limitBar == null || limitBar.IsFillForward
                || limitBar.EndTime <= _signalBarEndTime
                || orderEvent.UtcTime != limitBar.EndTime.ConvertToUtc(limitSecurity.Exchange.TimeZone)
                || _realBarsBeforeLimitExpiry != 1)
            {
                throw new InvalidOperationException(
                    $"Limit expiry did not wait through fill-forward bars and expire on the first later real bar; " +
                    $"fill-forward bars={_fillForwardBars}, real bars before expiry={_realBarsBeforeLimitExpiry}, " +
                    $"bar={limitBar?.EndTime:o}.");
            }
            _limitCanceledAfterGap = true;
        }

        public override void OnEndOfAlgorithm()
        {
            if (!_submitted || _fillForwardBars == 0 || !_marketFilledOnRealBar || !_limitCanceledAfterGap)
            {
                throw new InvalidOperationException(
                    $"Missing fill-forward result: submitted={_submitted}, fill-forward bars={_fillForwardBars}, " +
                    $"market filled on real bar={_marketFilledOnRealBar}, limit canceled after gap={_limitCanceledAfterGap}.");
            }
            base.OnEndOfAlgorithm();
        }

        public bool CanRunLocally { get; } = true;

        public List<Language> Languages { get; } = new() { Language.CSharp };

        public long DataPoints => 795;

        public int AlgorithmHistoryDataPoints => 0;

        public AlgorithmStatus AlgorithmStatus => AlgorithmStatus.Completed;

        public Dictionary<string, string> ExpectedStatistics => new()
        {
            { "Total Orders", "2" },
            { "Average Win", "0%" },
            { "Average Loss", "0%" },
            { "Compounding Annual Return", "0%" },
            { "Drawdown", "0%" },
            { "Expectancy", "0" },
            { "Start Equity", "100000" },
            { "End Equity", "99999.41" },
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
            { "Total Fees", "$0.41" },
            { "Estimated Strategy Capacity", "$280000000.00" },
            { "Lowest Capacity Asset", "AIG R735QTJ8XC9X" },
            { "Portfolio Turnover", "0.04%" },
            { "Drawdown Recovery", "0" },
            { "OrderListHash", "1c66a41541c7397e1e019825d8cffbae" }
        };
    }
}
