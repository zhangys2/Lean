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
using QuantConnect.Interfaces;
using QuantConnect.Orders;

namespace QuantConnect.Algorithm.CSharp.WLStyle
{
    /// <summary>
    /// One explicit market round trip. The buy fills on the next bar's opening scan,
    /// before that bar's opening hook and user code.
    /// </summary>
    public sealed class WLPhase4HooksRegressionAlgorithm : WLStrategyBase, IRegressionAlgorithmDefinition
    {
        private const string EntryTag = "WL hooks entry";
        private const string ExitTag = "WL hooks exit";
        private readonly List<string> _trace = new();
        private bool _entrySent;
        private bool _exitSent;
        private int _fills;
        private bool _loggedBoundary;

        public bool CanRunLocally => true;

        public List<Language> Languages => new() { Language.CSharp };

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
            { "End Equity", "99997.91" },
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
            { "Estimated Strategy Capacity", "$19000000.00" },
            { "Lowest Capacity Asset", "SPY R735QTJ8XC9X" },
            { "Portfolio Turnover", "0.29%" },
            { "Drawdown Recovery", "0" },
            { "OrderListHash", "27c6e808d0632619589440508a33798a" }
        };

        public override void Initialize()
        {
            SetStartDate(2013, 10, 7);
            SetEndDate(2013, 10, 7);
            SetCash(100000);
            AddEquity("SPY", Resolution.Minute);
        }

        protected override void Initialize(BarHistory bars)
        {
        }

        protected override bool AfterOpeningScan(Slice slice)
        {
            if (!_loggedBoundary && _trace.Count > 0 && _trace[^1] == "fill")
            {
                Log("WL-HOOKS late-fill boundary: fill then opening");
                _loggedBoundary = true;
            }

            _trace.Add("opening");
            var needsScan = base.AfterOpeningScan(slice);
            Ensure(!needsScan, "The default opening hook requested a transaction pass.");
            return needsScan;
        }

        protected override void Execute(BarHistory bars, int idx)
        {
            _trace.Add("execute");
            if (!_entrySent)
            {
                _entrySent = true;
                PlaceTrade(bars, TransactionType.Buy, OrderType.Market, 1m, signalName: EntryTag);
                return;
            }

            if (!_exitSent && HasOpenPosition(bars, PositionType.Long))
            {
                _exitSent = true;
                ClosePosition(bars, ExitTag);
            }
        }

        protected override void AfterClosingScan(Slice slice)
        {
            _trace.Add("closing");
            base.AfterClosingScan(slice);
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status != OrderStatus.Filled)
            {
                return;
            }

            _fills++;
            _trace.Add("fill");
        }

        public override void OnEndOfAlgorithm()
        {
            Ensure(_entrySent && _exitSent, "The market round trip was not submitted.");
            Ensure(_fills == 2, $"Expected 2 fills, observed {_fills}.");
            Ensure(_loggedBoundary, "The late fill was not observed before the opening hook.");

            var phase = "closing";
            var fills = 0;
            for (var index = 0; index < _trace.Count; index++)
            {
                var step = _trace[index];
                if (step == "fill")
                {
                    Ensure(index + 1 < _trace.Count && _trace[index + 1] == "opening",
                        "A fill was not immediately before the next opening hook.");
                    phase = "fill";
                    fills++;
                    continue;
                }

                if (step == "opening")
                {
                    Ensure(phase is "closing" or "fill", $"Opening hook followed {phase}.");
                    phase = "opening";
                    continue;
                }

                if (step == "execute")
                {
                    Ensure(phase == "opening", "User code ran before the opening hook.");
                    phase = "execute";
                    continue;
                }

                Ensure(step == "closing" && phase == "execute", "Closing hook did not follow user code.");
                phase = "closing";
            }

            Ensure(fills == 2 && phase == "closing", "Hook boundaries did not cover the round trip.");
            base.OnEndOfAlgorithm();
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
