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

namespace QuantConnect.Algorithm.CSharp.WLStyle
{
    /// <summary>
    /// Exercises pure max-risk sizing followed by explicit, Lean-native WL adapter orders.
    /// </summary>
    public class WLPhase4SizingRegressionAlgorithm : WLStrategyBase, IRegressionAlgorithmDefinition
    {
        private const string EntryTag = "P4 sizing entry";
        private const string ExitTag = "P4 sizing exit";
        private bool _entrySubmitted;
        private int _entries;
        private int _exits;

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
            if (!_entrySubmitted)
            {
                var context = new WLSizeContext(WLTradeSide.Long, 10000m, null, 50m, 45m);
                var quantity = WLPositionSizing.Calculate(
                    new WLSizeRule(WLSizeMode.MaxRiskPercent, 2m), in context);
                if (quantity != 40m)
                {
                    throw new InvalidOperationException($"The sizing regression expected 40 shares, got {quantity}.");
                }

                _entrySubmitted = true;
                PlaceTrade(bars, TransactionType.Buy, OrderType.Market, quantity, signalName: EntryTag);
            }
            else if (HasOpenPosition(bars, PositionType.Long) && _exits == 0)
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
            if (_entries != 1 || _exits != 1)
            {
                throw new InvalidOperationException(
                    $"Sizing regression did not complete one round trip: entries={_entries}, exits={_exits}.");
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
            { "OrderListHash", "06c4471301ef2ac9fd0e4f5c1627e559" }
        };
    }
}
