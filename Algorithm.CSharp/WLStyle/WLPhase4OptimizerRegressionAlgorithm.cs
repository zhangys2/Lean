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
using QuantConnect.Data;
using QuantConnect.Algorithm.WLStyle;
using QuantConnect.Interfaces;
using QuantConnect.Orders;

namespace QuantConnect.Algorithm.CSharp.WLStyle
{
    /// <summary>
    /// Walk-forward-aware optimizer fixture. A higher parameter tuple must not win in-sample
    /// when an OOS-only spike is injected into a non-objective WL metric.
    /// </summary>
    public sealed class WLPhase4OptimizerRegressionAlgorithm : WLStrategyBase, IRegressionAlgorithmDefinition
    {
        private int _period;
        private bool _entrySent;
        private bool _sawWarmup;
        private DateTime? _firstTradeUtc;

        public bool CanRunLocally => true;

        public List<Language> Languages => new() { Language.CSharp };

        public long DataPoints => 1582;

        public int AlgorithmHistoryDataPoints => 0;

        public AlgorithmStatus AlgorithmStatus => AlgorithmStatus.Completed;

        public Dictionary<string, string> ExpectedStatistics => new()
        {
            { "Total Orders", "1" },
            { "Average Win", "0%" },
            { "Average Loss", "0%" },
            { "Compounding Annual Return", "0%" },
            { "Drawdown", "0%" },
            { "Expectancy", "0" },
            { "Start Equity", "100000" },
            { "End Equity", "99997.44" },
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
            { "Total Fees", "$1.00" },
            { "Estimated Strategy Capacity", "$17000000000.00" },
            { "Lowest Capacity Asset", "SPY R735QTJ8XC9X" },
            { "Portfolio Turnover", "0.07%" },
            { "Drawdown Recovery", "0" },
            { "OrderListHash", "7de31ddb8c1903c0ddaf338f9a46863b" }
        };

        public override void Initialize()
        {
            UseWLWalkForwardDates();
            if (string.IsNullOrWhiteSpace(GetParameter("wl-wf-start", string.Empty)))
            {
                SetStartDate(2013, 10, 7);
                SetEndDate(2013, 10, 8);
            }
            else
            {
                SetWarmUp(1, Resolution.Minute);
            }

            SetCash(100000);
            AddEquity("SPY", Resolution.Minute);
        }

        public override void OnData(Slice slice)
        {
            _sawWarmup |= IsWarmingUp;
            base.OnData(slice);
        }

        protected override void Initialize(BarHistory bars)
        {
            _period = AddParameter("Period", 2, 1, 3, 1);
        }

        protected override void Execute(BarHistory bars, int idx)
        {
            if (IsWarmingUp || _entrySent || idx == 0 || idx % Math.Max(1, _period) != 0)
            {
                return;
            }

            _entrySent = true;
            PlaceTrade(bars, TransactionType.Buy, OrderType.Market, 1m, signalName: "WL optimizer entry");
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status == OrderStatus.Filled && orderEvent.FillQuantity != 0 && !_firstTradeUtc.HasValue)
            {
                _firstTradeUtc = orderEvent.UtcTime;
            }

            base.OnOrderEvent(orderEvent);
        }

        public override void OnEndOfAlgorithm()
        {
            SetRuntimeStatistic("WL_GateMetric", StartDate.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
            SetRuntimeStatistic("WL_GateFirstTrade", _firstTradeUtc?.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            SetRuntimeStatistic("WL_GateWarmupObserved", _sawWarmup.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SetRuntimeStatistic("WL_OosSpike", GetParameter("wl-oos-spike", "0"));
            base.OnEndOfAlgorithm();
        }

    }
}