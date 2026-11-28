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
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;

namespace QuantConnect.Tests.Algorithm
{
    [TestFixture]
    public class WLPhase4HooksTests
    {
        [Test]
        public void DefaultHooksDoNotRequestAScanAndRunOpeningThenUserCodeThenClosing()
        {
            var algorithm = new HookProbe();
            var hooks = (IBacktestSliceHooks)algorithm;
            var slice = Slice(algorithm);

            Assert.IsFalse(hooks.AfterOpeningScan(slice));
            algorithm.OnData(slice);
            hooks.AfterClosingScan(slice);

            CollectionAssert.AreEqual(new[] { "opening", "execute", "closing" }, algorithm.Calls);
            Assert.IsFalse(algorithm.OpeningRequestedScan);
        }

        [Test]
        public void ParameterSnapshotIsOrdinalAndIndependentAcrossCalls()
        {
            var algorithm = new RangeProbe();
            algorithm.OnData(Slice(algorithm));

            algorithm.CheckSnapshots();
        }

        private static Slice Slice(WLStrategyBase algorithm)
        {
            var symbol = Symbol.Create("SPY", SecurityType.Equity, Market.USA);
            var time = new DateTime(2013, 10, 7, 9, 31, 0);
            var bar = new TradeBar(time, symbol, 10m, 11m, 9m, 10m, 100, TimeSpan.FromMinutes(1));
            var utc = time.ConvertToUtc(TimeZones.NewYork);
            algorithm.SetDateTime(utc);
            var slice = new Slice(time, new BaseData[] { bar }, utc);
            algorithm.SetCurrentSlice(slice);
            return slice;
        }

        private sealed class HookProbe : WLStrategyBase
        {
            public List<string> Calls { get; } = new();

            public bool OpeningRequestedScan { get; private set; }

            public override void Initialize()
            {
            }

            protected override void Initialize(BarHistory bars)
            {
            }

            protected override bool AfterOpeningScan(Slice slice)
            {
                Calls.Add("opening");
                OpeningRequestedScan = base.AfterOpeningScan(slice);
                return OpeningRequestedScan;
            }

            protected override void Execute(BarHistory bars, int idx)
            {
                Calls.Add("execute");
            }

            protected override void AfterClosingScan(Slice slice)
            {
                Calls.Add("closing");
                base.AfterClosingScan(slice);
            }
        }

        private sealed class RangeProbe : WLStrategyBase
        {
            public override void Initialize()
            {
            }

            protected override void Initialize(BarHistory bars)
            {
                AddParameter("zeta", 9, 0, 9, 3);
                AddParameter("alpha", 1, 1, 3, 1);
            }

            protected override void Execute(BarHistory bars, int idx)
            {
            }

            public void CheckSnapshots()
            {
                var first = GetWLParameterRanges();
                var second = GetWLParameterRanges();

                Assert.AreNotSame(first, second);
                CollectionAssert.AreEqual(new[] { "alpha", "zeta" }, first.Select(range => range.Name).ToArray());
                Assert.AreEqual(typeof(int), first[0].Type);
                Assert.AreEqual(1m, first[0].Default);
                Assert.AreEqual(1m, first[0].Min);
                Assert.AreEqual(3m, first[0].Max);
                Assert.AreEqual(1m, first[0].Step);
                Assert.AreEqual(9m, first[1].Default);
                Assert.AreEqual(first[0], second[0]);
                Assert.AreEqual(first[1], second[1]);
            }
        }
    }
}
