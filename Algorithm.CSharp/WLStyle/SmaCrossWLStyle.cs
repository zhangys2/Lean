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
using QuantConnect.Orders;

namespace QuantConnect.Algorithm.CSharp.WLStyle
{
    /// <summary>
    /// Per-bar SMA crossover using Lean's native market orders and optimizer parameters.
    /// </summary>
    public class SmaCrossWLStyle : WLStrategyBase
    {
        private readonly Dictionary<Symbol, TimeSeries> _fast = new();
        private readonly Dictionary<Symbol, TimeSeries> _slow = new();

        public override void Initialize()
        {
            SetStartDate(2013, 10, 7);
            SetEndDate(2013, 10, 11);
            SetCash(100000);
            AddEquity("SPY", Resolution.Minute);
        }

        protected override void Initialize(BarHistory bars)
        {
            var fastPeriod = AddParameter("Fast", 10, 5, 15, 1);
            var slowPeriod = AddParameter("Slow", 30, 20, 50, 1);
            _fast.Add(bars.Symbol, WLSeries.SMA(bars.Close, fastPeriod));
            _slow.Add(bars.Symbol, WLSeries.SMA(bars.Close, slowPeriod));
        }

        protected override void Execute(BarHistory bars, int idx)
        {
            if (idx == 0)
            {
                return;
            }

            if (_fast[bars.Symbol].CrossesOver(_slow[bars.Symbol], idx)
                && !HasOpenPosition(bars, PositionType.Long))
            {
                PlaceTrade(bars, TransactionType.Buy, OrderType.Market, 1m, signalName: "SMA crossover");
            }
            else if (_fast[bars.Symbol].CrossesUnder(_slow[bars.Symbol], idx)
                && HasOpenPosition(bars, PositionType.Long))
            {
                ClosePosition(bars, "SMA crossunder");
            }
        }
    }
}
