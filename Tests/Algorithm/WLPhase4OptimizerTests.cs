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
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Algorithm.CSharp.WLStyle;
using QuantConnect.Algorithm.WLStyle;
using QuantConnect.Configuration;
using QuantConnect.Optimizer.Launcher;
using QuantConnect.Optimizer.Parameters;
using QuantConnect.Tests;

namespace QuantConnect.Tests.Algorithm
{
    [TestFixture]
    public class WLPhase4OptimizerTests
    {
        [Test]
        public void WalkForwardPlansJanToFebSlidingWindows()
        {
            var windows = WLWalkForward.Plan(new WLWalkForwardOptions
            {
                Start = new DateTime(2024, 1, 1),
                EndExclusive = new DateTime(2024, 3, 2),
                InSampleDays = 31,
                OutOfSampleDays = 14,
                StrideDays = 14
            });

            Assert.GreaterOrEqual(windows.Count, 2);
            var first = windows[0];
            var second = windows[1];
            Assert.AreEqual(new DateTime(2024, 1, 1), first.InSampleStart);
            Assert.AreEqual(new DateTime(2024, 2, 1), first.InSampleEndExclusive);
            Assert.AreEqual(new DateTime(2024, 1, 31), first.LeanInSampleEndInclusive);
            Assert.AreEqual(new DateTime(2024, 2, 1), first.OutOfSampleStart);
            Assert.AreEqual(new DateTime(2024, 2, 15), first.OutOfSampleEndExclusive);
            Assert.AreEqual(new DateTime(2024, 2, 14), first.LeanOutOfSampleEndInclusive);
            Assert.AreEqual(new DateTime(2024, 2, 15), second.OutOfSampleStart);
        }

        [Test]
        public void WalkForwardExpandingGrowsInSampleFromFixedStart()
        {
            var windows = WLWalkForward.Plan(new WLWalkForwardOptions
            {
                Start = new DateTime(2024, 1, 1),
                EndExclusive = new DateTime(2024, 3, 20),
                InSampleDays = 10,
                OutOfSampleDays = 5,
                StrideDays = 5,
                Mode = WLWalkForwardIsMode.Expanding
            });

            Assert.GreaterOrEqual(windows.Count, 2);
            Assert.AreEqual(new DateTime(2024, 1, 1), windows[0].InSampleStart);
            Assert.AreEqual(new DateTime(2024, 1, 1), windows[1].InSampleStart);
            Assert.Less(windows[0].InSampleEndExclusive, windows[1].InSampleEndExclusive);
        }

        [Test]
        public void OptimizationTableHandlesMissingFailureAndNonNumericMetrics()
        {
            var options = new WLTableOptions
            {
                MetricColumns = new[] { "Statistics.Net Profit", "Statistics.WL_OosSpike", "Statistics.Total Orders" },
                ObjectiveColumn = "Statistics.Sharpe Ratio",
                MaximizeObjective = true
            };

            var okJson = "{\"Statistics\":{\"Sharpe Ratio\":\"1.2\",\"Net Profit\":\"5%\",\"WL_OosSpike\":\"42\",\"Total Orders\":\"2\",\"OrderListHash\":\"abc\"}}";
            var badObjectiveJson = "{\"Statistics\":{\"Sharpe Ratio\":\"not-a-number\",\"OrderListHash\":\"def\"}}";
            var rows = new[]
            {
                WLRunRow.FromBacktestResult("run-b", new ParameterSet(2, new Dictionary<string, string> { { "Period", "3" } }), null, options),
                WLRunRow.FromBacktestResult("run-a", new ParameterSet(1, new Dictionary<string, string> { { "Period", "1" } }), okJson, options),
                WLRunRow.FromBacktestResult("run-c", new ParameterSet(3, new Dictionary<string, string> { { "Period", "2" } }), badObjectiveJson, options),
            };

            var ranked = WLOptimizationTable.RankRows(options, rows);
            Assert.AreEqual("run-a", ranked[0].RunId);
            Assert.AreEqual("failed", ranked.Single(row => row.RunId == "run-b").ProcessStatus);
            Assert.IsNull(ranked.Single(row => row.RunId == "run-c").ObjectiveValue);
            Assert.AreEqual("42", ranked[0].Metrics["Statistics.WL_OosSpike"]);
        }

        [Test]
        public void OptimizationTableRankingIsStableOnParametersThenRunId()
        {
            var options = new WLTableOptions
            {
                ObjectiveColumn = "Statistics.Net Profit",
                MaximizeObjective = true,
                ParameterTieOrder = new[] { "alpha", "beta" }
            };
            var json = "{\"Statistics\":{\"Net Profit\":\"1\",\"OrderListHash\":\"x\"}}";
            var rows = new[]
            {
                WLRunRow.FromBacktestResult("2", new ParameterSet(1, new Dictionary<string, string> { { "alpha", "2" }, { "beta", "1" } }), json, options),
                WLRunRow.FromBacktestResult("1", new ParameterSet(2, new Dictionary<string, string> { { "alpha", "1" }, { "beta", "2" } }), json, options),
            };
            var ranked = WLOptimizationTable.RankRows(options, rows);
            Assert.AreEqual("1", ranked[0].RunId);
            Assert.AreEqual("2", ranked[1].RunId);
        }

        [Test]
        public void OptimizationTableWritesCsvAndJson()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"wl-table-{Guid.NewGuid():N}");
            try
            {
                var options = new WLTableOptions
                {
                    MetricColumns = new[] { "Statistics.Total Orders" },
                    ObjectiveColumn = "Statistics.Sharpe Ratio",
                    MaximizeObjective = true
                };
                var rows = new[]
                {
                    WLRunRow.FromBacktestResult("r1", new ParameterSet(1, new Dictionary<string, string> { { "Period", "2" } }),
                        "{\"Statistics\":{\"Sharpe Ratio\":\"2\",\"Total Orders\":\"1\",\"OrderListHash\":\"h1\"}}", options)
                };
                WLOptimizationTable.Write(directory, options, rows);

                var csv = File.ReadAllText(Path.Combine(directory, WLOptimizationTable.CsvFileName));
                var json = File.ReadAllText(Path.Combine(directory, WLOptimizationTable.JsonFileName));
                StringAssert.Contains("order_list_hash", csv);
                StringAssert.Contains("h1", json);
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        [Test]
        public void UseWLWalkForwardDatesAppliesInclusiveLeanEnd()
        {
            var algorithm = new WalkForwardDateProbe();
            algorithm.SetParameters(new Dictionary<string, string>
            {
                { "wl-wf-start", "20130101" },
                { "wl-wf-end", "20130110" }
            });
            algorithm.Initialize();

            Assert.AreEqual(new DateTime(2013, 1, 1), algorithm.StartDate);
            Assert.AreEqual(new DateTime(2013, 1, 9), algorithm.EndDate.Date);
        }

        [Test]
        public void OosOnlyMetricSpikeDoesNotChangeIsWinnerChoice()
        {
            var options = new WLTableOptions
            {
                ObjectiveColumn = "Statistics.Sharpe Ratio",
                MaximizeObjective = true,
                MetricColumns = new[] { "Statistics.WL_OosSpike" }
            };

            var isWinnerJson = "{\"Statistics\":{\"Sharpe Ratio\":\"1.0\",\"WL_OosSpike\":\"0\",\"OrderListHash\":\"is\"}}";
            var spikeJson = "{\"Statistics\":{\"Sharpe Ratio\":\"0.1\",\"WL_OosSpike\":\"9999\",\"OrderListHash\":\"oos\"}}";
            var ranked = WLOptimizationTable.RankRows(options, new[]
            {
                WLRunRow.FromBacktestResult("spike", new ParameterSet(2, new Dictionary<string, string> { { "Period", "3" } }), spikeJson, options),
                WLRunRow.FromBacktestResult("winner", new ParameterSet(1, new Dictionary<string, string> { { "Period", "1" } }), isWinnerJson, options),
            });

            Assert.AreEqual("winner", ranked[0].RunId);
        }

        [Test, Category("RegressionTests")]
        public void OptimizerRegressionAlgorithmPinsChildHash()
        {
            Config.Reset();
            Config.Set("forward-console-messages", "false");
            var instance = new WLPhase4OptimizerRegressionAlgorithm();
            AlgorithmRunner.RunLocalBacktest(
                instance.GetType().Name,
                instance.ExpectedStatistics,
                Language.CSharp,
                instance.AlgorithmStatus);
        }

        private sealed class WalkForwardDateProbe : WLStrategyBase
        {
            public override void Initialize()
            {
                UseWLWalkForwardDates();
            }

            protected override void Initialize(BarHistory bars)
            {
            }

            protected override void Execute(BarHistory bars, int idx)
            {
            }
        }
    }
}