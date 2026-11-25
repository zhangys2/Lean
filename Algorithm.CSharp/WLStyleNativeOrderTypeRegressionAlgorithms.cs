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
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Statistics;
using QuantConnect.Util;

namespace QuantConnect.Algorithm.CSharp
{
    public abstract class WLStyleOrderTypeRegressionBase : WLStrategyBase, IRegressionAlgorithmDefinition
    {
        public bool CanRunLocally { get; } = true;

        public List<Language> Languages { get; } = new() { Language.CSharp };

        public abstract long DataPoints { get; }

        public int AlgorithmHistoryDataPoints => 0;

        public AlgorithmStatus AlgorithmStatus => AlgorithmStatus.Completed;

        public abstract Dictionary<string, string> ExpectedStatistics { get; }

        protected override void Initialize(BarHistory bars)
        {
        }

        protected static Dictionary<string, string> Pin(params (string Key, string Value)[] statistics)
        {
            var result = new Dictionary<string, string>(statistics.Length);
            foreach (var (key, value) in statistics)
            {
                result.Add(key, value);
            }
            return result;
        }

        protected static void Ensure(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    public sealed class WLStyleStopLimitRegressionAlgorithm : WLStyleOrderTypeRegressionBase
    {
        private const string FillTag = "StopLimit fill";
        private const string ExpireTag = "StopLimit expire";
        private OrderTicket _fillTicket;
        private OrderTicket _expireTicket;
        private OrderEvent _fillEvent;
        private OrderEvent _cancelEvent;
        private DateTime _submittedUtc;
        private DateTime _eligibleUtc;
        private decimal _eligibleHigh;
        private bool _submitted;

        public override void Initialize()
        {
            SetStartDate(2013, 10, 7);
            SetEndDate(2013, 10, 7);
            SetCash(100000);
            AddEquity("SPY", Resolution.Minute);
        }

        protected override void Execute(BarHistory bars, int idx)
        {
            var endTime = bars.DateTimes[idx];
            if (!_submitted && endTime.TimeOfDay == new TimeSpan(9, 33, 0))
            {
                var close = (decimal)bars.Close[idx];
                var stopPrice = close + 0.01m;
                _submittedUtc = UtcTime;
                _fillTicket = PlaceTrade(bars, TransactionType.Buy, OrderType.StopLimit, 1m,
                    close + 0.50m, FillTag, stopPrice);
                _expireTicket = PlaceTrade(bars, TransactionType.Buy, OrderType.StopLimit, 1m,
                    close + 0.02m, ExpireTag, stopPrice);
                _submitted = true;
            }
            else if (_submitted && _eligibleUtc == default && endTime.TimeOfDay == new TimeSpan(9, 34, 0))
            {
                _eligibleUtc = endTime.ConvertToUtc(Securities[bars.Symbol].Exchange.TimeZone);
                _eligibleHigh = (decimal)bars.High[idx];
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (_fillTicket != null && orderEvent.OrderId == _fillTicket.OrderId && orderEvent.Status == OrderStatus.Filled)
            {
                _fillEvent = orderEvent;
            }
            else if (_expireTicket != null && orderEvent.OrderId == _expireTicket.OrderId && orderEvent.Status == OrderStatus.Canceled)
            {
                _cancelEvent = orderEvent;
            }
        }

        public override void OnEndOfAlgorithm()
        {
            Ensure(_fillTicket != null && _expireTicket != null, "StopLimit orders were not submitted.");
            Ensure(_fillEvent != null && _fillTicket.Status == OrderStatus.Filled, "StopLimit did not fill on its first eligible bar.");
            Ensure(_cancelEvent != null && _expireTicket.Status == OrderStatus.Canceled, "Triggered StopLimit did not expire after its first eligible bar.");
            var fillOrder = Transactions.GetOrderById(_fillTicket.OrderId) as StopLimitOrder;
            var expireOrder = Transactions.GetOrderById(_expireTicket.OrderId) as StopLimitOrder;
            Ensure(fillOrder?.StopTriggered == true, "Filling StopLimit did not trigger.");
            Ensure(expireOrder?.StopTriggered == true, "Expiring StopLimit did not trigger before cancellation.");
            Ensure(_fillEvent.UtcTime == _eligibleUtc && _cancelEvent.UtcTime == _eligibleUtc,
                "StopLimit fill and expiry did not occur on the first eligible bar.");
            Ensure(_fillEvent.UtcTime > _submittedUtc && _cancelEvent.UtcTime > _submittedUtc,
                "StopLimit events did not occur after submission.");
            Ensure(_fillEvent.FillPrice == Math.Min(_eligibleHigh, _fillTicket.SubmitRequest.LimitPrice),
                "StopLimit fill price did not follow Lean's native fill model.");
            Ensure(expireOrder.StopTriggeredTime == _eligibleUtc, "StopLimit trigger time did not match its one eligible bar.");
            base.OnEndOfAlgorithm();
        }

        public override long DataPoints => 795;

        public override Dictionary<string, string> ExpectedStatistics =>
            Pin(
                ("Total Orders", "2"),
                ("Average Win", "0%"),
                ("Average Loss", "0%"),
                ("Compounding Annual Return", "0%"),
                ("Drawdown", "0%"),
                ("Expectancy", "0"),
                ("Start Equity", "100000"),
                ("End Equity", "99998.99"),
                ("Net Profit", "0%"),
                ("Sharpe Ratio", "0"),
                ("Sortino Ratio", "0"),
                ("Probabilistic Sharpe Ratio", "0%"),
                ("Loss Rate", "0%"),
                ("Win Rate", "0%"),
                ("Profit-Loss Ratio", "0"),
                ("Alpha", "0"),
                ("Beta", "0"),
                ("Annual Standard Deviation", "0"),
                ("Annual Variance", "0"),
                ("Information Ratio", "0"),
                ("Tracking Error", "0"),
                ("Treynor Ratio", "0"),
                ("Total Fees", "$1.00"),
                ("Estimated Strategy Capacity", "$10000000000.00"),
                ("Lowest Capacity Asset", "SPY R735QTJ8XC9X"),
                ("Portfolio Turnover", "0.14%"),
                ("Drawdown Recovery", "0"),
                ("OrderListHash", "ae368cfd9a5aec97e36cc3a338c5604e"));
    }

    public sealed class WLStyleMarketOnOpenRegressionAlgorithm : WLStyleOrderTypeRegressionBase
    {
        private readonly DateTime _signalDate = new(2013, 3, 28);
        private readonly DateTime _nextSessionDate = new(2013, 4, 1);
        private OrderTicket _ticket;
        private OrderEvent _fillEvent;
        private DateTime _nextSessionEndUtc;
        private decimal _nextSessionOpen;

        public override void Initialize()
        {
            SetStartDate(2013, 3, 28);
            SetEndDate(2013, 4, 1);
            SetCash(100000);
            AddEquity("SPY", Resolution.Hour);
        }

        protected override void Execute(BarHistory bars, int idx)
        {
            var endTime = bars.DateTimes[idx];
            if (_ticket == null && endTime.Date == _signalDate && endTime.TimeOfDay == new TimeSpan(16, 0, 0))
            {
                _ticket = PlaceTrade(bars, TransactionType.Buy, OrderType.MarketOnOpen, 2m, signalName: "Holiday MOO");
            }
            else if (_ticket != null && endTime.Date == _nextSessionDate && _nextSessionEndUtc == default)
            {
                _nextSessionEndUtc = endTime.ConvertToUtc(Securities[bars.Symbol].Exchange.TimeZone);
                _nextSessionOpen = (decimal)bars.Open[idx];
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status == OrderStatus.Filled)
            {
                _fillEvent = orderEvent;
            }
        }

        public override void OnEndOfAlgorithm()
        {
            Ensure(_ticket != null && _ticket.Status == OrderStatus.Filled, "MOO did not fill at a subsequent session open.");
            Ensure(_fillEvent != null && _nextSessionEndUtc != default, "MOO did not see the next real session after the holiday closure.");
            Ensure(_ticket.SubmitRequest.Time.Date == _signalDate, "MOO was not submitted on the pre-holiday session.");
            Ensure(_fillEvent.UtcTime.Date == _nextSessionDate && _fillEvent.UtcTime == _nextSessionEndUtc,
                "MOO did not fill on the next real session bar.");
            Ensure(_fillEvent.FillPrice == _nextSessionOpen && _fillEvent.FillQuantity == 2m,
                "MOO fill did not use the next session's native bar open and explicit quantity.");
            base.OnEndOfAlgorithm();
        }

        public override long DataPoints => 36;

        public override Dictionary<string, string> ExpectedStatistics =>
            Pin(
                ("Total Orders", "1"),
                ("Average Win", "0%"),
                ("Average Loss", "0%"),
                ("Compounding Annual Return", "-0.152%"),
                ("Drawdown", "0.000%"),
                ("Expectancy", "0"),
                ("Start Equity", "100000"),
                ("End Equity", "99998.06"),
                ("Net Profit", "-0.002%"),
                ("Sharpe Ratio", "-56.602"),
                ("Sortino Ratio", "0"),
                ("Probabilistic Sharpe Ratio", "0.033%"),
                ("Loss Rate", "0%"),
                ("Win Rate", "0%"),
                ("Profit-Loss Ratio", "0"),
                ("Alpha", "-0.008"),
                ("Beta", "0.006"),
                ("Annual Standard Deviation", "0"),
                ("Annual Variance", "0"),
                ("Information Ratio", "7.188"),
                ("Tracking Error", "0.025"),
                ("Treynor Ratio", "-1.435"),
                ("Total Fees", "$1.00"),
                ("Estimated Strategy Capacity", "$12000000000.00"),
                ("Lowest Capacity Asset", "SPY R735QTJ8XC9X"),
                ("Portfolio Turnover", "0.05%"),
                ("Drawdown Recovery", "0"),
                ("OrderListHash", "ce894bc642abfb5804422c522b959fbc"));
    }

    public sealed class WLStyleMarketOnCloseRegressionAlgorithm : WLStyleOrderTypeRegressionBase
    {
        private OrderTicket _ticket;
        private OrderEvent _fillEvent;
        private DateTime _closeEndUtc;
        private decimal _closePrice;

        public override void Initialize()
        {
            SetStartDate(2013, 10, 7);
            SetEndDate(2013, 10, 7);
            SetCash(100000);
            AddEquity("SPY", Resolution.Minute);
        }

        protected override void Execute(BarHistory bars, int idx)
        {
            var endTime = bars.DateTimes[idx];
            if (_ticket == null && endTime.TimeOfDay == new TimeSpan(10, 0, 0))
            {
                _ticket = PlaceTrade(bars, TransactionType.Sell, OrderType.MarketOnClose, 1m, signalName: "MOC close");
            }
            if (_ticket != null && endTime.TimeOfDay == new TimeSpan(16, 0, 0))
            {
                _closeEndUtc = endTime.ConvertToUtc(Securities[bars.Symbol].Exchange.TimeZone);
                _closePrice = (decimal)bars.Close[idx];
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status == OrderStatus.Filled)
            {
                _fillEvent = orderEvent;
            }
        }

        public override void OnEndOfAlgorithm()
        {
            Ensure(_ticket != null && _ticket.Status == OrderStatus.Filled, "MOC did not fill at the session close.");
            Ensure(_fillEvent != null && _closeEndUtc != default, "MOC close bar was not observed.");
            Ensure(_fillEvent.UtcTime == _closeEndUtc && _fillEvent.FillPrice == _closePrice,
                "MOC did not fill at the native session close.");
            Ensure(_fillEvent.FillQuantity == -1m, "MOC did not preserve the signed explicit quantity.");
            base.OnEndOfAlgorithm();
        }

        public override long DataPoints => 795;

        public override Dictionary<string, string> ExpectedStatistics =>
            Pin(
                ("Total Orders", "1"),
                ("Average Win", "0%"),
                ("Average Loss", "0%"),
                ("Compounding Annual Return", "0%"),
                ("Drawdown", "0%"),
                ("Expectancy", "0"),
                ("Start Equity", "100000"),
                ("End Equity", "99999"),
                ("Net Profit", "0%"),
                ("Sharpe Ratio", "0"),
                ("Sortino Ratio", "0"),
                ("Probabilistic Sharpe Ratio", "0%"),
                ("Loss Rate", "0%"),
                ("Win Rate", "0%"),
                ("Profit-Loss Ratio", "0"),
                ("Alpha", "0"),
                ("Beta", "0"),
                ("Annual Standard Deviation", "0"),
                ("Annual Variance", "0"),
                ("Information Ratio", "0"),
                ("Tracking Error", "0"),
                ("Treynor Ratio", "0"),
                ("Total Fees", "$1.00"),
                ("Estimated Strategy Capacity", "$67000000000.00"),
                ("Lowest Capacity Asset", "SPY R735QTJ8XC9X"),
                ("Portfolio Turnover", "0.14%"),
                ("Drawdown Recovery", "0"),
                ("OrderListHash", "7547fa953e761e712a2325cd9b422518"));
    }

    public sealed class WLStyleMarketOnCloseCutoffRegressionAlgorithm : WLStyleOrderTypeRegressionBase
    {
        private OrderTicket _ticket;

        public override void Initialize()
        {
            SetStartDate(2013, 10, 7);
            SetEndDate(2013, 10, 7);
            SetCash(100000);
            AddEquity("SPY", Resolution.Minute);
        }

        protected override void Execute(BarHistory bars, int idx)
        {
            if (_ticket == null && bars.DateTimes[idx].TimeOfDay == new TimeSpan(15, 45, 0))
            {
                _ticket = PlaceTrade(bars, TransactionType.Buy, OrderType.MarketOnClose, 1m, signalName: "MOC after cutoff");
            }
        }

        public override void OnEndOfAlgorithm()
        {
            Ensure(_ticket != null && _ticket.Status == OrderStatus.Invalid, "Late MOC was not rejected by Lean.");
            Ensure(_ticket.SubmitRequest.Response.ErrorCode == OrderResponseErrorCode.MarketOnCloseOrderTooLate,
                "Late MOC did not retain Lean's native cutoff rejection.");
            base.OnEndOfAlgorithm();
        }

        public override long DataPoints => 795;

        public override Dictionary<string, string> ExpectedStatistics =>
            Pin(
                ("Total Orders", "0"),
                ("Average Win", "0%"),
                ("Average Loss", "0%"),
                ("Compounding Annual Return", "0%"),
                ("Drawdown", "0%"),
                ("Expectancy", "0"),
                ("Start Equity", "100000"),
                ("End Equity", "100000"),
                ("Net Profit", "0%"),
                ("Sharpe Ratio", "0"),
                ("Sortino Ratio", "0"),
                ("Probabilistic Sharpe Ratio", "0%"),
                ("Loss Rate", "0%"),
                ("Win Rate", "0%"),
                ("Profit-Loss Ratio", "0"),
                ("Alpha", "0"),
                ("Beta", "0"),
                ("Annual Standard Deviation", "0"),
                ("Annual Variance", "0"),
                ("Information Ratio", "0"),
                ("Tracking Error", "0"),
                ("Treynor Ratio", "0"),
                ("Total Fees", "$0.00"),
                ("Estimated Strategy Capacity", "$0"),
                ("Lowest Capacity Asset", ""),
                ("Portfolio Turnover", "0%"),
                ("Drawdown Recovery", "0"),
                ("OrderListHash", "d41d8cd98f00b204e9800998ecf8427e"));
    }
}
