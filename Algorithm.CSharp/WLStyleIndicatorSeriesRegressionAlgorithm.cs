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
using QuantConnect.Securities;
using QuantConnect.Statistics;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Exercises WLSeries MACD, ADX, and Stochastic in a native backtest.
    /// </summary>
    public class WLStyleIndicatorSeriesRegressionAlgorithm : WLStrategyBase, IRegressionAlgorithmDefinition
    {
        private const string EntryTag = "WL indicator entry";
        private const string ExitTag = "WL indicator exit";
        private MacdSeries _macd;
        private AdxSeries _adx;
        private StochasticSeries _stochastic;
        private bool _entrySubmitted;
        private int _entries;
        private int _exits;
        private int _crossesBlockedByAdx;
        private int _crossesBlockedByStochastic;

        public override void Initialize()
        {
            SetStartDate(2013, 10, 7);
            SetEndDate(2013, 10, 11);
            SetCash(100000);
            AddEquity("SPY", Resolution.Minute);
        }

        protected override void Initialize(BarHistory bars)
        {
            _macd = WLSeries.MACD(bars.Close, 5, 20, 6);
            _adx = WLSeries.ADX(bars, 14);
            _stochastic = WLSeries.Stochastic(bars, 14, 3, 3);
        }

        protected override void Execute(BarHistory bars, int idx)
        {
            if (idx == 0
                || !double.IsFinite(_macd.Histogram[idx])
                || !double.IsFinite(_adx.Adx[idx])
                || !double.IsFinite(_stochastic.K[idx])
                || !double.IsFinite(_stochastic.D[idx]))
            {
                return;
            }

            var hasLong = HasOpenPosition(bars, PositionType.Long);
            var macdCross = _macd.Macd.CrossesOver(_macd.Signal, idx);
            var adxOk = _adx.Adx[idx] >= 20;
            var stochasticOk = _stochastic.K[idx] > _stochastic.D[idx]
                && _stochastic.K[idx] < 80;
            if (macdCross && !_entrySubmitted)
            {
                _crossesBlockedByAdx += adxOk ? 0 : 1;
                _crossesBlockedByStochastic += adxOk && !stochasticOk ? 1 : 0;
            }
            var enter = macdCross && adxOk && stochasticOk;
            var exit = _macd.Macd.CrossesUnder(_macd.Signal, idx);

            if (enter && !hasLong && !_entrySubmitted)
            {
                _entrySubmitted = true;
                PlaceTrade(bars, TransactionType.Buy, OrderType.Market, 1m, signalName: EntryTag);
            }
            else if (exit && hasLong && _entries > 0 && _exits == 0)
            {
                ClosePosition(bars, ExitTag);
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status != OrderStatus.Filled)
            {
                return;
            }

            var order = Transactions.GetOrderById(orderEvent.OrderId);
            if (order?.Tag == EntryTag)
            {
                _entries++;
            }
            else if (order?.Tag == ExitTag)
            {
                _exits++;
            }
        }

        public override void OnEndOfAlgorithm()
        {
            if (_entries == 0 || _exits == 0)
            {
                throw new InvalidOperationException(
                    $"Indicator strategy did not complete round-trip trades: entries={_entries}, exits={_exits}.");
            }
            if (_crossesBlockedByAdx == 0 || _crossesBlockedByStochastic == 0)
            {
                throw new InvalidOperationException(
                    $"ADX and Stochastic must each reject a MACD cross before entry: adx={_crossesBlockedByAdx}, stochastic={_crossesBlockedByStochastic}.");
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
            { "Total Orders", "2" },
            { "Average Win", "0%" },
            { "Average Loss", "0.00%" },
            { "Compounding Annual Return", "-0.156%" },
            { "Drawdown", "0.000%" },
            { "Expectancy", "-1" },
            { "Start Equity", "100000" },
            { "End Equity", "99998.01" },
            { "Net Profit", "-0.002%" },
            { "Sharpe Ratio", "0" },
            { "Sortino Ratio", "0" },
            { "Probabilistic Sharpe Ratio", "0%" },
            { "Loss Rate", "100%" },
            { "Win Rate", "0%" },
            { "Profit-Loss Ratio", "0" },
            { "Alpha", "0" },
            { "Beta", "0" },
            { "Annual Standard Deviation", "0" },
            { "Annual Variance", "0" },
            { "Information Ratio", "-8.91" },
            { "Tracking Error", "0.223" },
            { "Treynor Ratio", "0" },
            { "Total Fees", "$2.00" },
            { "Estimated Strategy Capacity", "$6300000.00" },
            { "Lowest Capacity Asset", "SPY R735QTJ8XC9X" },
            { "Portfolio Turnover", "0.06%" },
            { "Drawdown Recovery", "0" },
            { "OrderListHash", "774943f536505c3ea41a9b4ef2a050c4" }
        };
    }
}
