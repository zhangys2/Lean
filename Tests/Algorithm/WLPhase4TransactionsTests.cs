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
using NUnit.Framework;
using QuantConnect.Algorithm.CSharp.WLStyle;
using QuantConnect.Algorithm.WLStyle;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Tests.Common.Securities;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Algorithm
{
    [TestFixture]
    public class WLPhase4TransactionsTests
    {
        [Test]
        public void HandCheck_SignalCloseSizesSixShares_AndRejectsAtHigherOpen()
        {
            var equity = 300m;
            var closeBasis = 50m;
            var openMark = 60m;
            var closeContext = LongContext(equity, closeBasis);
            var quantity = WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.FixedValue, equity), in closeContext);
            Assert.AreEqual(6m, quantity);
            var admission = new WLPortfolioAdmission(new WLPortfolioRules(MarginFactor: 1m));
            Assert.IsTrue(admission.Evaluate(Entry(1, quantity, equity, closeBasis)).Admitted);
            var blocked = admission.Evaluate(Entry(2, quantity, equity, openMark));
            Assert.IsFalse(blocked.Admitted);
            Assert.AreEqual(WLAdmissionReason.BuyingPower, blocked.Reason);
        }

        [Test]
        public void HandCheck_NextOpenBasisSizesFiveShares_AtThreeHundred()
        {
            var equity = 300m;
            var openBasis = 60m;
            var openContext = new WLSizeContext(WLTradeSide.Long, equity, null, openBasis);
            var quantity = WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.FixedValue, equity),
                in openContext);
            Assert.AreEqual(5m, quantity);
            var admission = new WLPortfolioAdmission(new WLPortfolioRules());
            Assert.IsTrue(admission.Evaluate(Entry(1, quantity, equity, openBasis)).Admitted);
        }

        [Test]
        public void QueueRequiresOptIn()
        {
            var algorithm = new TransactionProbe();
            algorithm.InitializeAlgorithm();
            algorithm.ArmQueueOnExecute();
            Assert.Throws<InvalidOperationException>(() => algorithm.RunOnce());
        }

        [Test]
        public void RejectsMooWithNextOpenBasis()
        {
            var algorithm = new TransactionProbe();
            algorithm.InitializeAlgorithm();
            algorithm.EnableTransactions();
            Assert.Throws<ArgumentException>(() => algorithm.QueueMooNextOpen());
        }

        [Test]
        public void TransactionsRegressionCompletesWithPinnedOrders()
        {
            var run = AlgorithmRunner.RunLocalBacktest(
                nameof(WLPhase4TransactionsRegressionAlgorithm),
                new Dictionary<string, string>(),
                Language.CSharp,
                AlgorithmStatus.Completed);
            var expected = ((WLPhase4TransactionsRegressionAlgorithm)AlgorithmRunner.RegressionSetupHandlerWrapper.Algorithm)
                .ExpectedStatistics["OrderListHash"];
            Assert.AreEqual(expected, run.Results.FinalStatistics["OrderListHash"]);
        }

        private static WLSizeContext LongContext(decimal equity, decimal basis)
        {
            return new WLSizeContext(WLTradeSide.Long, equity, null, basis);
        }

        private static WLAdmissionContext Entry(long id, decimal quantity, decimal equity, decimal mark)
        {
            return new WLAdmissionContext(
                Symbol.Create("SPY", SecurityType.Equity, Market.USA),
                id,
                WLAdmissionGate.Entry,
                PositionType.Long,
                quantity,
                quantity * mark,
                0m,
                equity,
                0m,
                Array.Empty<Symbol>(),
                1,
                WLAdmissionPhase.Execution);
        }

        private sealed class TransactionProbe : WLStrategyBase
        {
            private bool _queued;
            private bool _armQueue;

            public Symbol Symbol { get; private set; }

            public void ArmQueueOnExecute()
            {
                _armQueue = true;
            }

            public void InitializeAlgorithm()
            {
                SetStartDate(2013, 10, 7);
                SetEndDate(2013, 10, 7);
                SubscriptionManager.SetDataManager(new DataManagerStub(this));
                SetCash(100000);
                SetLiveMode(false);
                SetFinishedWarmingUp();
                Symbol = AddEquity("SPY", Resolution.Minute).Symbol;
                var processor = new FakeOrderProcessor { TransactionManager = Transactions };
                Transactions.SetOrderProcessor(processor);
            }

            public void EnableTransactions()
            {
                EnableWLTransactions(new WLTransactionOptions(new WLPortfolioRules()));
            }

            public void RunOnce()
            {
                var time = new DateTime(2013, 10, 7, 9, 31, 0);
                var bar = new TradeBar(time, Symbol, 100, 100, 100, 100, 1000, TimeSpan.FromMinutes(1));
                var utc = time.ConvertToUtc(TimeZones.NewYork);
                SetDateTime(utc);
                var slice = new Slice(time, new BaseData[] { bar }, utc);
                SetCurrentSlice(slice);
                Securities[Symbol].SetMarketPrice(bar);
                OnData(slice);
            }

            public void QueueMooNextOpen()
            {
                _queued = false;
                ArmQueueOnExecute();
                RunOnce();
            }

            protected override void Initialize(BarHistory bars)
            {
            }

            protected override void Execute(BarHistory bars, int idx)
            {
                if (_queued || !_armQueue)
                {
                    return;
                }

                _queued = true;
                QueueWLTrade(bars, new WLTradeIntent(
                    WLIntentSide.Entry,
                    TransactionType.Buy,
                    OrderType.MarketOnOpen,
                    1m,
                    BasisPrice: WLBasisPrice.NextRealOpen,
                    Tag: "moo"));
            }
        }
    }
}