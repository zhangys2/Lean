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

using System.Collections.Generic;
using QuantConnect.Algorithm.WLStyle;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Orders;

namespace QuantConnect.Algorithm.CSharp.WLStyle
{
    /// <summary>
    /// Minimal parameter-driven order regression used to exercise Lean's parameter override path.
    /// </summary>
    public sealed class WLPhase4ParameterExportRegressionAlgorithm : WLStrategyBase, IRegressionAlgorithmDefinition
    {
        private bool _sent;
        private decimal _period;

        public bool CanRunLocally => true;
        public List<Language> Languages => new() { Language.CSharp };
        public long DataPoints => 48;
        public int AlgorithmHistoryDataPoints => 0;
        public AlgorithmStatus AlgorithmStatus => AlgorithmStatus.Completed;
        public Dictionary<string, string> ExpectedStatistics => new()
        {
            ["OrderListHash"] = "243faeb49aa36e88e492e9f648f69fa0"
        };

        public override void Initialize()
        {
            SetStartDate(2013, 10, 7);
            SetEndDate(2013, 10, 11);
            SetCash(100000);
            AddEquity("SPY", Resolution.Daily);
        }

        protected override void Initialize(BarHistory bars)
        {
            _period = AddParameter("period", 2m, 1m, 3m, 1m);
            _ = AddParameter("threshold", 0.2m, 0.1m, 0.3m, 0.2m);
            _ = AddParameter("fixed", 0.25m, 0.1m, 0.3m, 0.1m);
        }

        protected override void Execute(BarHistory bars, int idx)
        {
            if (!_sent && idx >= _period)
            {
                PlaceTrade(bars, TransactionType.Buy, OrderType.Market, 1, signalName: "WL parameter grid");
                _sent = true;
            }
        }
    }
}
