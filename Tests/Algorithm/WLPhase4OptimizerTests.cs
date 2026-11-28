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
using QuantConnect.Logging;
using QuantConnect.Optimizer;
using QuantConnect.Optimizer.Launcher;
using QuantConnect.Optimizer.Parameters;
using QuantConnect.Tests;

namespace QuantConnect.Tests.Algorithm
{
    [TestFixture]
    [NonParallelizable]
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
        public void OptimizationTableWritesPercentMetricsAndObjectiveInConsistentUnits()
        {
            var directory = Path.Combine(TestContext.CurrentContext.TestDirectory, $"wl-table-units-{Guid.NewGuid():N}");
            try
            {
                var options = new WLTableOptions
                {
                    ObjectiveColumn = "Statistics.Sharpe Ratio",
                    MetricColumns = new[] { "Statistics.Sharpe Ratio", "Statistics.Net Profit" }
                };
                var rows = new[]
                {
                    WLRunRow.FromBacktestResult(
                        "r1",
                        new ParameterSet(1, new Dictionary<string, string>()),
                        "{\"Statistics\":{\"Sharpe Ratio\":\"0.25\",\"Net Profit\":\"12%\",\"OrderListHash\":\"h\"}}",
                        options)
                };

                WLOptimizationTable.Write(directory, options, rows);
                var csv = File.ReadAllText(Path.Combine(directory, WLOptimizationTable.CsvFileName));
                StringAssert.Contains("1,r1,completed,h,0.25,0.25,12", csv);
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
        public void WalkForwardRejectsUnknownMode()
        {
            var previousMode = Config.Get("wl-walk-forward-mode", "sliding");
            try
            {
                Config.Set("wl-walk-forward-mode", "rolling");
                Assert.Throws<ArgumentException>(() => WLWalkForward.ParseFromConfig());
            }
            finally
            {
                Config.Set("wl-walk-forward-mode", previousMode);
            }
        }

        [Test]
        public void OptimizerDefaultsUseOneValidObjectiveAndPreserveLeanStrategyDefault()
        {
            Assert.AreEqual(
                new OptimizationNodePacket().OptimizationStrategy,
                Program.ResolveOptimizationStrategy(null, wlOptimizationEnabled: false));
            Assert.AreEqual(
                "QuantConnect.Optimizer.Strategies.GridSearchOptimizationStrategy",
                Program.ResolveOptimizationStrategy(null, wlOptimizationEnabled: true));
            Assert.AreEqual(
                "Configured.Strategy",
                Program.ResolveOptimizationStrategy("Configured.Strategy", wlOptimizationEnabled: true));
            Assert.AreEqual(WLTableConfig.DefaultObjectiveColumn, new WLTableOptions().ObjectiveColumn);
            Assert.AreEqual("Statistics.Sharpe Ratio", WLTableConfig.DefaultObjectiveColumn);
        }

        [Test]
        public void WalkForwardFeasibilityGateValidatesFreshIsAndOosChildren()
        {
            var testDirectory = TestContext.CurrentContext.TestDirectory;
            var leanLocation = Path.GetFullPath(Path.Combine(
                testDirectory,
                "../../../Launcher/bin/Release/QuantConnect.Lean.Launcher.dll"));
            var algorithmLocation = Path.Combine(testDirectory, "QuantConnect.Algorithm.CSharp.dll");
            Assume.That(File.Exists(leanLocation), $"Lean launcher not found: {leanLocation}");
            Assume.That(File.Exists(algorithmLocation), $"C# algorithm assembly not found: {algorithmLocation}");
            var arguments = $"--algorithm-type-name {nameof(WLPhase4OptimizerRegressionAlgorithm)} --algorithm-location \"{algorithmLocation}\"";

            var result = WLWalkForward.TryFeasibilityGate(leanLocation, arguments);

            Assert.IsTrue(result.Passed, result.Reason);
        }

        [Test]
        public void RunWalkForwardWritesDistinctInSampleAndOutOfSampleWindows()
        {
            var testDirectory = TestContext.CurrentContext.TestDirectory;
            var resultsDirectory = Path.Combine(testDirectory, $"wl-walk-forward-{Guid.NewGuid():N}");
            var leanLocation = Path.GetFullPath(Path.Combine(
                testDirectory,
                "../../../Launcher/bin/Release/QuantConnect.Lean.Launcher.dll"));
            var savedConfig = new Dictionary<string, string>
            {
                ["wl-walk-forward"] = Config.Get("wl-walk-forward", "false"),
                ["wl-walk-forward-start"] = Config.Get("wl-walk-forward-start", "2013-10-07"),
                ["wl-walk-forward-end-exclusive"] = Config.Get("wl-walk-forward-end-exclusive", "2013-10-11"),
                ["wl-walk-forward-in-sample-days"] = Config.Get("wl-walk-forward-in-sample-days", "31"),
                ["wl-walk-forward-out-of-sample-days"] = Config.Get("wl-walk-forward-out-of-sample-days", "14"),
                ["wl-walk-forward-stride-days"] = Config.Get("wl-walk-forward-stride-days", "14"),
                ["wl-walk-forward-mode"] = Config.Get("wl-walk-forward-mode", "sliding"),
                ["optimization-strategy"] = Config.Get("optimization-strategy", new OptimizationNodePacket().OptimizationStrategy),
                ["optimization-strategy-settings"] = Config.Get("optimization-strategy-settings", ""),
                ["optimization-criterion"] = Config.Get("optimization-criterion", ""),
                ["parameters"] = Config.Get("parameters", "[]"),
                ["constraints"] = Config.Get("constraints", "[]"),
                ["maximum-concurrent-backtests"] = Config.Get("maximum-concurrent-backtests", "1"),
                ["algorithm-type-name"] = Config.Get("algorithm-type-name", ""),
                ["algorithm-language"] = Config.Get("algorithm-language", ""),
                ["algorithm-location"] = Config.Get("algorithm-location", ""),
                ["lean-binaries-location"] = Config.Get("lean-binaries-location", ""),
                ["results-destination-folder"] = Config.Get("results-destination-folder", ""),
                ["wl-active-wf-start"] = Config.Get("wl-active-wf-start", ""),
                ["wl-active-wf-end"] = Config.Get("wl-active-wf-end", ""),
                ["optimization-launcher"] = Config.Get("optimization-launcher", nameof(ConsoleLeanOptimizer)),
                ["wl-optimization-table"] = Config.Get("wl-optimization-table", "false"),
                ["optimizer-close-automatically"] = Config.Get("optimizer-close-automatically", "true"),
                ["estimate"] = Config.Get("estimate", "false")
            };
            var oldLogHandler = Log.LogHandler;
            var oldLogFilePath = Log.FilePath;
            var oldDebuggingEnabled = Log.DebuggingEnabled;
            try
            {
                Assume.That(File.Exists(leanLocation), $"Lean launcher not found: {leanLocation}");
                Config.Set("wl-walk-forward", "true");
                Config.Set("wl-walk-forward-start", "2013-10-07");
                Config.Set("wl-walk-forward-end-exclusive", "2013-10-11");
                Config.Set("wl-walk-forward-in-sample-days", "2");
                Config.Set("wl-walk-forward-out-of-sample-days", "1");
                Config.Set("wl-walk-forward-stride-days", "1");
                Config.Set("wl-walk-forward-mode", "sliding");
                Config.Set("optimization-strategy", "QuantConnect.Optimizer.Strategies.GridSearchOptimizationStrategy");
                Config.Set("optimization-strategy-settings",
                    "{\"$type\":\"QuantConnect.Optimizer.Strategies.StepBaseOptimizationStrategySettings, QuantConnect.Optimizer\",\"default-segment-amount\":10}");
                Config.Set("optimization-criterion", "{\"target\":\"Statistics.Sharpe Ratio\",\"extremum\":\"max\"}");
                Config.Set("parameters", "[{\"name\":\"Period\",\"min\":1,\"max\":1,\"step\":1}]");
                Config.Set("maximum-concurrent-backtests", "1");
                Config.Set("algorithm-type-name", nameof(WLPhase4OptimizerRegressionAlgorithm));
                Config.Set("algorithm-language", "CSharp");
                Config.Set("algorithm-location", Path.Combine(testDirectory, "QuantConnect.Algorithm.CSharp.dll"));
                Config.Set("lean-binaries-location", leanLocation);
                Config.Set("results-destination-folder", resultsDirectory);

                Assert.AreEqual(0, Program.Main(Array.Empty<string>()));

                var windows = JArray.Parse(File.ReadAllText(Path.Combine(resultsDirectory, "wl-walk-forward-summary.json")));
                Assert.AreEqual(2, windows.Count);
                Assert.AreEqual("2013-10-07", windows[0]["inSample"]["start"].ToString());
                Assert.AreEqual("2013-10-09", windows[0]["inSample"]["endExclusive"].ToString());
                Assert.AreEqual("2013-10-09", windows[0]["outOfSample"]["start"].ToString());
                Assert.AreEqual("2013-10-10", windows[0]["outOfSample"]["endExclusive"].ToString());
                Assert.IsNotNull(windows[0]["outOfSampleAnnualReturn"]);
                Assert.IsEmpty(Config.Get("wl-active-wf-start"));
                Assert.IsEmpty(Config.Get("wl-active-wf-end"));
            }
            finally
            {
                foreach (var pair in savedConfig)
                {
                    Config.Set(pair.Key, pair.Value);
                }
                Log.LogHandler = oldLogHandler;
                Log.FilePath = oldLogFilePath;
                Log.DebuggingEnabled = oldDebuggingEnabled;
                if (Directory.Exists(resultsDirectory))
                {
                    Directory.Delete(resultsDirectory, recursive: true);
                }
            }
        }

        [Test]
        public void ClearsStaleWalkForwardDateOverrides()
        {
            Config.Set("wl-active-wf-start", "2013-10-09");
            Config.Set("wl-active-wf-end", "2013-10-10");

            Program.ClearActiveWalkForwardWindow();

            Assert.IsEmpty(Config.Get("wl-active-wf-start"));
            Assert.IsEmpty(Config.Get("wl-active-wf-end"));
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