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
    /// Verifies an engine stop fill and an overnight market fill on standard SPY minute data.
    /// </summary>
    public class WLStyleOvernightStopRegressionAlgorithm : WLStrategyBase, IRegressionAlgorithmDefinition
    {
        private const string StopTag = "WL next-real-bar stop";
        private const string OvernightTag = "WL overnight market";
        private const string OvernightLimitTag = "WL overnight untouched limit";
        private bool _stopSubmitted;
        private bool _stopFilled;
        private bool _overnightSubmitted;
        private bool _overnightFilledAtNextOpen;
        private bool _overnightLimitCanceledAtNextOpen;
        private DateTime _stopSignalBarEndTime;
        private DateTime _overnightSignalDate;
        private OrderTicket _overnightLimit;

        public override void Initialize()
        {
            SetStartDate(2013, 10, 7);
            SetEndDate(2013, 10, 8);
            SetCash(100000);
            AddEquity("SPY", Resolution.Minute);
        }

        protected override void Initialize(BarHistory bars)
        {
        }

        protected override void Execute(BarHistory bars, int idx)
        {
            var barEndTime = bars.DateTimes[idx];
            if (!_stopSubmitted && barEndTime.Date == new DateTime(2013, 10, 7) && idx == 6)
            {
                _stopSubmitted = true;
                _stopSignalBarEndTime = barEndTime;
                var stopPrice = (decimal)bars.Close[idx] - 0.05m;
                PlaceTrade(bars, TransactionType.Sell, OrderType.StopMarket, 1m, stopPrice, StopTag);
            }

            if (!_overnightSubmitted
                && barEndTime.Date == new DateTime(2013, 10, 7)
                && barEndTime.TimeOfDay == new TimeSpan(16, 0, 0))
            {
                _overnightSubmitted = true;
                _overnightSignalDate = barEndTime.Date;
                PlaceTrade(bars, TransactionType.Buy, OrderType.Market, 1m, signalName: OvernightTag);
                _overnightLimit = PlaceTrade(
                    bars,
                    TransactionType.Buy,
                    OrderType.Limit,
                    1m,
                    (decimal)bars.Close[idx] * 0.50m,
                    OvernightLimitTag);
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            var order = Transactions.GetOrderById(orderEvent.OrderId);
            if (order == null)
            {
                return;
            }

            if (_overnightLimit != null && order.Id == _overnightLimit.OrderId)
            {
                if (orderEvent.Status == OrderStatus.Filled)
                {
                    throw new InvalidOperationException("The untouched overnight limit order unexpectedly filled.");
                }

                if (orderEvent.Status != OrderStatus.Canceled)
                {
                    return;
                }

                var limitSecurity = Securities[order.Symbol];
                var limitBar = limitSecurity.Cache.GetData<TradeBar>();
                if (limitBar == null || limitBar.IsFillForward
                    || limitBar.EndTime.Date != _overnightSignalDate.AddDays(1)
                    || limitBar.EndTime.TimeOfDay != new TimeSpan(9, 31, 0)
                    || orderEvent.UtcTime != limitBar.EndTime.ConvertToUtc(limitSecurity.Exchange.TimeZone))
                {
                    throw new InvalidOperationException(
                        $"Overnight limit did not expire on the next session's first real bar: " +
                        $"fill={orderEvent.UtcTime:o}, bar={limitBar?.EndTime:o}.");
                }
                _overnightLimitCanceledAtNextOpen = true;
                return;
            }

            if (orderEvent.Status != OrderStatus.Filled
                || order.Tag != StopTag && order.Tag != OvernightTag)
            {
                return;
            }

            var security = Securities[order.Symbol];
            var bar = security.Cache.GetData<TradeBar>();
            if (bar == null || bar.IsFillForward
                || orderEvent.UtcTime <= order.Time
                || bar.EndTime.ConvertToUtc(security.Exchange.TimeZone) != orderEvent.UtcTime)
            {
                throw new InvalidOperationException($"Order {order.Id} did not fill after submission on a real bar.");
            }

            if (order.Tag == StopTag)
            {
                if (bar.EndTime != _stopSignalBarEndTime.AddMinutes(1))
                {
                    throw new InvalidOperationException(
                        $"Stop-market order filled at {bar.EndTime}, not on the next real bar after {_stopSignalBarEndTime}.");
                }
                _stopFilled = true;
                return;
            }

            var localFillTime = bar.EndTime;
            if (localFillTime.Date != _overnightSignalDate.AddDays(1)
                || localFillTime.TimeOfDay != new TimeSpan(9, 31, 0)
                || orderEvent.UtcTime.Date != order.Time.Date.AddDays(1)
                || orderEvent.FillPrice != bar.Open)
            {
                throw new InvalidOperationException(
                    $"Overnight market order did not fill at the next session's first open: " +
                    $"submit={order.Time:o}, fill={orderEvent.UtcTime:o}, bar={bar.EndTime:o}, " +
                    $"price={orderEvent.FillPrice}, open={bar.Open}.");
            }
            _overnightFilledAtNextOpen = true;
        }

        public override void OnEndOfAlgorithm()
        {
            if (!_stopSubmitted || !_stopFilled || !_overnightSubmitted
                || !_overnightFilledAtNextOpen || !_overnightLimitCanceledAtNextOpen
                || _overnightLimit == null || _overnightLimit.Status != OrderStatus.Canceled)
            {
                throw new InvalidOperationException(
                    $"Missing order result: stop submitted={_stopSubmitted}, filled={_stopFilled}; " +
                    $"overnight submitted={_overnightSubmitted}, next-open fill={_overnightFilledAtNextOpen}, " +
                    $"limit canceled at next open={_overnightLimitCanceledAtNextOpen}.");
            }
            base.OnEndOfAlgorithm();
        }

        public bool CanRunLocally { get; } = true;

        public List<Language> Languages { get; } = new() { Language.CSharp };

        public long DataPoints => 1582;

        public int AlgorithmHistoryDataPoints => 0;

        public AlgorithmStatus AlgorithmStatus => AlgorithmStatus.Completed;

        public Dictionary<string, string> ExpectedStatistics => new()
        {
            { "Total Orders", "3" },
            { "Average Win", "0%" },
            { "Average Loss", "0%" },
            { "Compounding Annual Return", "0%" },
            { "Drawdown", "0%" },
            { "Expectancy", "0" },
            { "Start Equity", "100000" },
            { "End Equity", "99997.93" },
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
            { "Total Fees", "$2.00" },
            { "Estimated Strategy Capacity", "$21000000.00" },
            { "Lowest Capacity Asset", "SPY R735QTJ8XC9X" },
            { "Portfolio Turnover", "0.14%" },
            { "Drawdown Recovery", "0" },
            { "OrderListHash", "374889f72ee7ba04047be68429483f98" }
        };
    }
}
