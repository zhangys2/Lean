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
using QuantConnect.Algorithm.WLStyle;
using QuantConnect.Interfaces;

namespace QuantConnect.Tests.Algorithm
{
    [TestFixture]
    public class WLPhase4SizingTests
    {
        private static WLSizeContext LongContext(
            decimal equity = 10000m,
            decimal basis = 50m,
            decimal? stop = null,
            decimal fx = 1m,
            decimal pointValue = 1m,
            decimal? futuresMargin = null)
        {
            return new WLSizeContext(
                WLTradeSide.Long, equity, null, basis, stop, fx, pointValue, futuresMargin);
        }

        [Test]
        public void CalculatesFixedQuantityAndValueAndEquityPercent()
        {
            var context = LongContext();
            Assert.AreEqual(12.5m, WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.Quantity, 12.5m), in context));
            Assert.AreEqual(40m, WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.FixedValue, 2000m), in context));
            Assert.AreEqual(40m, WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.PercentEquity, 20m), in context));
        }

        [Test]
        public void CalculatesMaxRiskForLongAndShortStopsAndCurrencyConversion()
        {
            var longContext = LongContext(stop: 45m);
            Assert.AreEqual(40m, WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.MaxRiskPercent, 2m), in longContext));

            var shortContext = new WLSizeContext(WLTradeSide.Short, 10000m, null, 50m, 55m);
            Assert.AreEqual(40m, WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.MaxRiskPercent, 2m), in shortContext));

            var foreignContext = LongContext(stop: 45m, fx: 2m);
            Assert.AreEqual(20m, WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.MaxRiskPercent, 2m), in foreignContext));
        }

        [Test]
        public void UsesFuturesMarginForAllocationsAndPointValueForRisk()
        {
            var futures = LongContext(basis: 5000m, stop: 4990m, pointValue: 10m, futuresMargin: 2500m);
            Assert.AreEqual(4m, WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.FixedValue, 10000m), in futures));
            Assert.AreEqual(4m, WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.PercentEquity, 100m), in futures));
            Assert.AreEqual(2m, WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.MaxRiskPercent, 2m), in futures));
        }

        [Test]
        public void ConvertsShareAllocationsByFxAndContractMultiplier()
        {
            var context = new WLSizeContext(
                WLTradeSide.Long, 10000m, null, 50m, FxRate: 2m, ContractMultiplier: 5m);
            Assert.AreEqual(4m, WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.FixedValue, 2000m), in context));
        }

        [Test]
        public void ConstrainCapsVolumeThenFloorsToTheLargerLotIncrement()
        {
            Assert.AreEqual(20m, WLPositionSizing.Constrain(40m, 1m, 5m, 23m, 100m));
            Assert.AreEqual(20m, WLPositionSizing.Constrain(40m, 1m, 5m, 21m, 100m));
            Assert.AreEqual(0m, WLPositionSizing.Constrain(40m, 1m, 5m, 0m, 100m));
            Assert.AreEqual(10m, WLPositionSizing.Constrain(11m, 2m, null, null, null));
        }

        [Test]
        public void ReturnsZeroForPercentSizingWithoutEquityAndValueWithoutBasis()
        {
            var noEquity = LongContext(equity: 0m);
            Assert.AreEqual(0m, WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.PercentEquity, 50m), in noEquity));
            Assert.AreEqual(40m, WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.FixedValue, 2000m), in noEquity));

            var noBasis = LongContext(basis: 0m);
            Assert.AreEqual(0m, WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.FixedValue, 500m), in noBasis));
            Assert.AreEqual(4m, WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.Quantity, 4m), in noEquity));
        }

        [Test]
        public void RejectsInvalidRulesStopsAndConstraintInputs()
        {
            var context = LongContext(stop: 50m);
            var missingStop = LongContext();
            Assert.Throws<ArgumentException>(() => WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.MaxRiskPercent, 2m), in context));
            Assert.Throws<ArgumentException>(() => WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.MaxRiskPercent, 2m), in missingStop));
            Assert.Throws<ArgumentOutOfRangeException>(() => WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.Quantity, -1m), in context));
            Assert.Throws<ArgumentOutOfRangeException>(() => WLPositionSizing.Constrain(4m, 0m, null, null, null));
            Assert.Throws<ArgumentException>(() => WLPositionSizing.Constrain(4m, 1m, null, 20m, null));
            Assert.Throws<ArgumentException>(() => WLPositionSizing.Constrain(4m, 2m, 3m, null, null));
        }

        [Test]
        public void CustomSizerReplacesTheFormulaAndMustReturnAnUnsignedQuantity()
        {
            var context = LongContext();
            Assert.AreEqual(7m, WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.FixedValue, 2000m), in context, _ => 7m));
            Assert.Throws<ArgumentOutOfRangeException>(() => WLPositionSizing.Calculate(
                new WLSizeRule(WLSizeMode.Quantity, 1m), in context, _ => -1m));
        }

        [Test]
        public void SizingRegressionAlgorithmCompletesOneRoundTripWithPinnedOrders()
        {
            var run = AlgorithmRunner.RunLocalBacktest(
                nameof(QuantConnect.Algorithm.CSharp.WLPhase4SizingRegressionAlgorithm),
                new Dictionary<string, string>(),
                Language.CSharp,
                AlgorithmStatus.Completed);

            Assert.AreEqual("06c4471301ef2ac9fd0e4f5c1627e559", run.Results.FinalStatistics["OrderListHash"]);
        }
    }
}
