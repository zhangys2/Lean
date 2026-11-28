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
using NUnit.Framework;
using QuantConnect.Algorithm.WLStyle;
using QuantConnect.Brokerages;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Algorithm
{
    [TestFixture]
    public class WLPhase4PortfolioTests
    {
        private static readonly Symbol Spy = Symbol.Create("SPY", SecurityType.Equity, Market.USA);
        private static readonly Symbol Aapl = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);
        private static readonly IReadOnlyList<Symbol> NoHoldings = Array.Empty<Symbol>();

        [Test]
        public void HandCheck_ThousandEquityAndSixHundredCommitted()
        {
            var tight = new WLPortfolioAdmission(new WLPortfolioRules(MarginFactor: 1m));
            var rejected = tight.Evaluate(Entry(1, Spy, 500m, equity: 1000m, filled: 600m));
            Assert.IsFalse(rejected.Admitted);
            Assert.AreEqual(WLAdmissionReason.BuyingPower, rejected.Reason);
            Assert.IsTrue(rejected.IsNsf);
            Assert.AreEqual(1, tight.NsfCount);

            var wide = new WLPortfolioAdmission(new WLPortfolioRules(MarginFactor: 1.2m));
            var admitted = wide.Evaluate(Entry(1, Spy, 500m, equity: 1000m, filled: 600m));
            Assert.IsTrue(admitted.Admitted);
            Assert.AreEqual(WLAdmissionReason.Admitted, admitted.Reason);
            Assert.IsFalse(admitted.IsNsf);
            Assert.AreEqual(500m, wide.ReservedExposure);
        }

        [Test]
        public void FeeEstimateCanExhaustRemainingCapacity()
        {
            var admission = new WLPortfolioAdmission(new WLPortfolioRules(MarginFactor: 1.2m));
            var rejected = admission.Evaluate(Entry(1, Spy, 500m, equity: 1000m, filled: 600m, fee: 101m));
            Assert.AreEqual(WLAdmissionReason.BuyingPower, rejected.Reason);
            Assert.AreEqual(0m, admission.ReservedExposure);
        }

        [Test]
        public void ReservationsConsumePartialFillsAndReleaseTheRemainder()
        {
            var admission = new WLPortfolioAdmission(new WLPortfolioRules());
            Assert.IsTrue(admission.Evaluate(Entry(1, Spy, 1000m, equity: 1000m, quantity: 10m)).Admitted);

            var blocked = admission.Evaluate(Entry(2, Aapl, 1m, equity: 1000m));
            Assert.AreEqual(WLAdmissionReason.BuyingPower, blocked.Reason);

            admission.NotePartialFill(1, 4m);
            Assert.AreEqual(600m, admission.ReservedExposure);
            var stillBlocked = admission.Evaluate(Entry(2, Aapl, 1m, equity: 1000m, filled: 400m));
            Assert.IsTrue(stillBlocked.IsNsf);
            Assert.AreEqual(1, admission.NsfCount);

            admission.Release(1);
            Assert.AreEqual(0m, admission.ReservedExposure);
            Assert.IsTrue(admission.Evaluate(Entry(3, Aapl, 400m, equity: 1000m, filled: 400m)).Admitted);
        }

        [Test]
        public void CancelInvalidExpiryAndBrokerVetoAreNotNsf()
        {
            var admission = new WLPortfolioAdmission(new WLPortfolioRules());
            Assert.IsTrue(admission.Evaluate(Entry(1, Spy, 100m, equity: 1000m)).Admitted);

            var canceled = admission.Evaluate(Entry(1, Spy, 100m, equity: 1000m, gate: WLAdmissionGate.Canceled));
            Assert.AreEqual(WLAdmissionReason.Canceled, canceled.Reason);
            Assert.IsFalse(canceled.IsNsf);
            Assert.AreEqual(0m, admission.ReservedExposure);

            var invalid = admission.Evaluate(Entry(2, Spy, 100m, equity: 1000m, gate: WLAdmissionGate.Invalid));
            var expired = admission.Evaluate(Entry(3, Spy, 100m, equity: 1000m, gate: WLAdmissionGate.Expired));
            var veto = admission.Evaluate(Entry(4, Spy, 5000m, equity: 1000m, nativeVeto: true));
            Assert.AreEqual(WLAdmissionReason.Invalid, invalid.Reason);
            Assert.AreEqual(WLAdmissionReason.Expired, expired.Reason);
            Assert.AreEqual(WLAdmissionReason.BrokerVeto, veto.Reason);
            Assert.IsFalse(veto.IsNsf);
            Assert.AreEqual(0, admission.NsfCount);
            Assert.IsEmpty(admission.OpenNsf(Spy));
        }

        [Test]
        public void MaxOpenCountsHoldingsAndPendingEntriesButNotSameDirectionAdds()
        {
            var admission = new WLPortfolioAdmission(new WLPortfolioRules(MaxOpenPositions: 1));
            Assert.IsTrue(admission.Evaluate(Entry(1, Spy, 100m, equity: 1000m)).Admitted);

            var secondSymbol = admission.Evaluate(Entry(2, Aapl, 100m, equity: 1000m));
            Assert.AreEqual(WLAdmissionReason.MaxOpen, secondSymbol.Reason);
            Assert.IsTrue(secondSymbol.IsNsf);
            Assert.AreEqual(1, admission.NsfCount);

            var add = admission.Evaluate(Entry(3, Spy, 50m, equity: 1000m, holdings: new[] { Spy }));
            Assert.IsTrue(add.Admitted);
            Assert.AreEqual(150m, admission.ReservedExposure);

            admission.Release(1);
            admission.Release(3);
            var afterFlat = admission.Evaluate(Entry(4, Aapl, 100m, equity: 1000m));
            Assert.IsTrue(afterFlat.Admitted);
        }

        [Test]
        public void SignalAndEntryThrottlesAreNotNsfAndExitsDoNotConsumeThem()
        {
            var signals = new WLPortfolioAdmission(new WLPortfolioRules(MaxSignalsPerBar: 1));
            Assert.IsTrue(signals.Evaluate(Entry(1, Spy, 10m, equity: 1000m, phase: WLAdmissionPhase.Signal)).Admitted);
            var throttled = signals.Evaluate(Entry(2, Aapl, 10m, equity: 1000m, phase: WLAdmissionPhase.Signal));
            Assert.AreEqual(WLAdmissionReason.MaxSignals, throttled.Reason);
            Assert.IsFalse(throttled.IsNsf);
            Assert.AreEqual(0, signals.NsfCount);
            Assert.AreEqual(0m, signals.ReservedExposure - 10m);

            var entries = new WLPortfolioAdmission(new WLPortfolioRules(MaxEntriesPerBar: 1));
            Assert.IsTrue(entries.Evaluate(Entry(1, Spy, 10m, equity: 1000m)).Admitted);
            var exit = entries.Evaluate(Entry(9, Spy, 10m, equity: 1000m, gate: WLAdmissionGate.Exit));
            Assert.IsTrue(exit.Admitted);
            var blocked = entries.Evaluate(Entry(2, Aapl, 10m, equity: 1000m));
            Assert.AreEqual(WLAdmissionReason.MaxEntries, blocked.Reason);
            Assert.IsFalse(blocked.IsNsf);
        }

        [Test]
        public void NsfIsCountedOnceAndRetentionDoesNotChangeTheOpenListUnlessEnabled()
        {
            var forgotten = new WLPortfolioAdmission(new WLPortfolioRules(RetainNsf: false));
            forgotten.Evaluate(Entry(7, Spy, 500m, equity: 100m));
            forgotten.Evaluate(Entry(7, Spy, 500m, equity: 100m));
            Assert.AreEqual(1, forgotten.NsfCount);
            Assert.IsEmpty(forgotten.OpenNsf(Spy));
            Assert.IsFalse(forgotten.CloseNsf(7));

            var retained = new WLPortfolioAdmission(new WLPortfolioRules(RetainNsf: true, MaxOpenPositions: 1));
            retained.Evaluate(Entry(7, Spy, 500m, equity: 100m));
            Assert.AreEqual(1, retained.NsfCount);
            Assert.IsTrue(retained.HasRetainedNsf(Spy));
            Assert.AreEqual(WLAdmissionReason.BuyingPower, retained.OpenNsf(Spy)[0].Reason);
            Assert.IsTrue(retained.Evaluate(Entry(8, Aapl, 10m, equity: 1000m)).Admitted);
            Assert.IsTrue(retained.CloseNsf(7));
            Assert.IsFalse(retained.HasRetainedNsf(Spy));
            Assert.AreEqual(1, retained.NsfCount);
            Assert.IsEmpty(retained.OpenNsf(Spy));
        }

        [Test]
        public void RepeatedNsfIntentDoesNotOpenTwoHypotheticals()
        {
            var admission = new WLPortfolioAdmission(new WLPortfolioRules(RetainNsf: true));
            admission.Evaluate(Entry(4, Spy, 50m, equity: 10m));
            admission.Evaluate(Entry(4, Spy, 50m, equity: 10m));
            Assert.AreEqual(1, admission.OpenNsf(Spy).Count);
            Assert.AreEqual(1, admission.NsfCount);
        }

        [Test]
        public void NextOpenMarkUsesAnEligibleOpenAndRejectsTheBarClose()
        {
            Assert.AreEqual(60m, WLPortfolioMarks.Select(50m, 60m, nextOpen: true));
            Assert.AreEqual(50m, WLPortfolioMarks.Select(50m, null, nextOpen: false));
            Assert.Throws<ArgumentException>(() => WLPortfolioMarks.Select(50m, null, nextOpen: true));
        }

        [Test]
        public void RestoredEntriesCountTowardMaxEntriesOnTheExecutionSlice()
        {
            var admission = new WLPortfolioAdmission(new WLPortfolioRules(MaxEntriesPerBar: 1));
            admission.RestorePending(new[]
            {
                new WLPendingOrder(1, Spy, PositionType.Long, 1m, 100m),
                new WLPendingOrder(2, Aapl, PositionType.Long, 1m, 100m)
            });

            Assert.IsTrue(admission.Evaluate(Entry(1, Spy, 100m, equity: 10000m)).Admitted);
            var blocked = admission.Evaluate(Entry(2, Aapl, 100m, equity: 10000m));
            Assert.AreEqual(WLAdmissionReason.MaxEntries, blocked.Reason);
            Assert.IsFalse(blocked.IsNsf);
            Assert.AreEqual(100m, admission.ReservedExposure);
        }

        [Test]
        public void PendingLeanOrdersOccupyASlotUntilReleased()
        {
            var admission = new WLPortfolioAdmission(new WLPortfolioRules(MaxOpenPositions: 1));
            admission.RestorePending(new[]
            {
                new WLPendingOrder(1, Spy, PositionType.Long, 1m, 100m)
            });
            var rejected = admission.Evaluate(Entry(2, Aapl, 10m, equity: 1000m));
            Assert.AreEqual(WLAdmissionReason.MaxOpen, rejected.Reason);
            admission.NotePartialFill(1, 1m);
            Assert.AreEqual(0m, admission.ReservedExposure);
            Assert.IsTrue(admission.Evaluate(Entry(2, Aapl, 10m, equity: 1000m)).Admitted);
        }

        [Test]
        public void ApplySetsCashOnceAndLeverageOnlyForMarginEquities()
        {
            var host = new AlgorithmStub();
            var equity = host.AddEquity("SPY");
            var forex = host.AddForex("EURUSD");
            var forexLeverage = forex.Leverage;

            var admission = new WLPortfolioAdmission(new WLPortfolioRules(StartingCapital: 25000m, MarginFactor: 4m));
            admission.Apply(host);
            Assert.AreEqual(25000m, host.Portfolio.Cash);
            Assert.AreEqual(4m, equity.Leverage);
            Assert.AreEqual(forexLeverage, forex.Leverage);

            host.SetCash(40000m);
            admission.Apply(host);
            Assert.AreEqual(40000m, host.Portfolio.Cash);
            Assert.AreEqual(4m, equity.Leverage);

            var cash = new AlgorithmStub();
            cash.SetBrokerageModel(BrokerageName.Default, AccountType.Cash);
            var unchanged = cash.Portfolio.Cash;
            var cashRules = new WLPortfolioAdmission(new WLPortfolioRules(StartingCapital: 1000m, MarginFactor: 1.2m));
            Assert.Throws<InvalidOperationException>(() => cashRules.Apply(cash));
            Assert.AreEqual(unchanged, cash.Portfolio.Cash);
        }

        private static WLAdmissionContext Entry(
            long intentId,
            Symbol symbol,
            decimal exposure,
            decimal equity,
            decimal filled = 0m,
            decimal fee = 0m,
            decimal quantity = 1m,
            WLAdmissionGate gate = WLAdmissionGate.Entry,
            WLAdmissionPhase phase = WLAdmissionPhase.Execution,
            bool nativeVeto = false,
            IReadOnlyList<Symbol> holdings = null)
        {
            return new WLAdmissionContext(
                symbol,
                intentId,
                gate,
                PositionType.Long,
                quantity,
                exposure,
                fee,
                equity,
                filled,
                holdings ?? NoHoldings,
                SliceId: 1,
                phase,
                nativeVeto);
        }

    }
}
