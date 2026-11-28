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
using System.Linq;
using QuantConnect.Algorithm.WLStyle;
using QuantConnect.Interfaces;
using QuantConnect.Orders;

namespace QuantConnect.Algorithm.CSharp.WLStyle
{
    /// <summary>
    /// Queues simultaneous SPY and IBM entries with a one-position cap, then records NSF without orders.
    /// </summary>
    public sealed class WLPhase4TransactionsRegressionAlgorithm : WLStrategyBase, IRegressionAlgorithmDefinition
    {
        private Symbol _spy;
        private Symbol _ibm;
        private bool _queued;
        private bool _exited;
        private int _fills;

        public override void Initialize()
        {
            SetStartDate(2013, 10, 7);
            SetEndDate(2013, 10, 7);
            _spy = AddEquity("SPY", Resolution.Minute).Symbol;
            _ibm = AddEquity("IBM", Resolution.Minute).Symbol;
            EnableWLTransactions(new WLTransactionOptions(new WLPortfolioRules(
                StartingCapital: 100000m,
                MaxOpenPositions: 1,
                RetainNsf: true)));
        }

        protected override void Initialize(BarHistory bars)
        {
        }

        protected override void PostExecute(DateTime time, List<BarHistory> bars)
        {
            if (_queued)
            {
                return;
            }

            var spy = bars.FirstOrDefault(history => history.Symbol == _spy);
            var ibm = bars.FirstOrDefault(history => history.Symbol == _ibm);
            if (spy == null || ibm == null)
            {
                return;
            }

            _queued = true;
            QueueWLTrade(spy, new WLTradeIntent(
                WLIntentSide.Entry,
                TransactionType.Buy,
                OrderType.Market,
                1m,
                Weight: 10m,
                Tag: "wl-spy"));
            QueueWLTrade(ibm, new WLTradeIntent(
                WLIntentSide.Entry,
                TransactionType.Buy,
                OrderType.Market,
                1m,
                Weight: 1m,
                Tag: "wl-ibm"));
        }

        protected override void Execute(BarHistory bars, int idx)
        {
            if (!_exited && bars.Symbol == _spy && HasOpenPosition(bars, PositionType.Long))
            {
                QueueWLTrade(bars, new WLTradeIntent(
                    WLIntentSide.Exit,
                    TransactionType.Sell,
                    OrderType.Market,
                    1m,
                    Tag: "wl-exit"));
                _exited = true;
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status != OrderStatus.Filled)
            {
                return;
            }

            var order = Transactions.GetOrderById(orderEvent.OrderId);
            if (order?.Tag is "wl-spy" or "wl-exit")
            {
                _fills++;
            }
        }

        public override void OnEndOfAlgorithm()
        {
            Ensure(_fills == 2, $"Expected two fills, observed {_fills}.");
            Ensure(Transactions.GetOrders(order => order.Tag == "wl-spy").Count() == 1, "SPY was over-submitted.");
            Ensure(GetWLIntent(new WLIntentId(2)).Status == WLIntentStatus.Nsf, "IBM was not classified NSF.");
            Ensure(Transactions.GetOrders(order => order.Tag == "wl-ibm").Count() == 0, "NSF intent was submitted.");
            Ensure(Transactions.GetOrders(order => order.Tag == "wl-spy").Count() == 1, "SPY was not submitted.");
            base.OnEndOfAlgorithm();
        }

        public bool CanRunLocally => true;
        public List<Language> Languages => new() { Language.CSharp };
        public long DataPoints => 1575;
        public int AlgorithmHistoryDataPoints => 0;
        public AlgorithmStatus AlgorithmStatus => AlgorithmStatus.Completed;

        public Dictionary<string, string> ExpectedStatistics => new()
        {
            { "Total Orders", "2" },
            { "Total Fees", "$2.00" },
            { "OrderListHash", "39bdb4d7ab518355156ac4910ef2b6aa" }
        };

        private static void Ensure(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}