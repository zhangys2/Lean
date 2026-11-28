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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Algorithm.WLStyle;
using QuantConnect.Optimizer;
using QuantConnect.Optimizer.Objectives;
using QuantConnect.Optimizer.Parameters;
using QuantConnect.Optimizer.Strategies;

namespace QuantConnect.Tests.Algorithm
{
    [TestFixture]
    public class WLPhase4ParameterExportTests
    {
        private static string ScratchDirectory
        {
            get
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "tmp", "lean-p4", "t6");
                Directory.CreateDirectory(directory);
                return directory;
            }
        }

        [Test]
        public void WritesInvariantGridAndDisabledDefaultAndDeserializesForLean()
        {
            var originalCulture = CultureInfo.CurrentCulture;
            var path = Path.Combine(ScratchDirectory, Guid.NewGuid().ToString("N") + ".json");
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                WLParameterExport.Write(path, new[]
                {
                    new WLParameterSpec("period", typeof(int), 2, 1, 3, 1),
                    new WLParameterSpec("threshold", typeof(decimal), 0.2m, 0.1m, 0.3m, 0.2m),
                    new WLParameterSpec("fixed", typeof(decimal), 0.25m, 0.1m, 0.3m, 0.1m, Enabled: false)
                }, new WLOptimizationExportOptions { MaximumGridRuns = 6 });

                var config = JObject.Parse(File.ReadAllText(path));
                Assert.AreEqual(
                    "QuantConnect.Optimizer.Strategies.GridSearchOptimizationStrategy",
                    (string)config["optimization-strategy"]);
                Assert.AreEqual("Statistics.Net Profit", (string)config["optimization-criterion"]["target"]);
                Assert.AreEqual(3, config["parameters"].Count());
                Assert.AreEqual("0.1", (string)config["parameters"][1]["min"]);
                Assert.AreEqual("0.3", (string)config["parameters"][1]["max"]);
                Assert.AreEqual("0.25", (string)config["parameters"][2]["min"]);
                Assert.AreEqual("0.25", (string)config["parameters"][2]["max"]);
                Assert.AreEqual("0.2", (string)config["parameters"][1]["minStep"]);

                var parsed = JsonConvert.DeserializeObject<HashSet<OptimizationParameter>>(config["parameters"].ToString());
                Assert.AreEqual(3, parsed.Count);
                Assert.IsTrue(parsed.All(parameter => parameter is OptimizationStepParameter));

                var grid = new GridSearchOptimizationStrategy();
                grid.Initialize(
                    new Target("Statistics.Net Profit", new Maximization(), null),
                    new List<Constraint>(),
                    parsed,
                    new StepBaseOptimizationStrategySettings());
                var candidates = new List<ParameterSet>();
                grid.NewParameterSet += (_, candidate) => candidates.Add(candidate);
                grid.PushNewResults(OptimizationResult.Initial);
                Assert.AreEqual(6, candidates.Count);
                Assert.AreEqual(6, candidates.Select(candidate => string.Join(",", candidate.Value.OrderBy(pair => pair.Key))).Distinct().Count());
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Test]
        public void RejectsInvalidObjectiveNameAndCliParameterSyntax()
        {
            var range = new[] { new WLParameterSpec("period", typeof(int), 2, 1, 3, 1) };
            var path = Path.Combine(ScratchDirectory, Guid.NewGuid().ToString("N") + ".json");
            Assert.Throws<ArgumentException>(() => WLParameterExport.Write(
                path, range, new WLOptimizationExportOptions { Objective = "Statistics.NotARealMetric" }));
            Assert.Throws<ArgumentException>(() => WLParameterExport.Write(
                path, new[] { range[0] with { Name = "bad,name" } }, new WLOptimizationExportOptions()));
            Assert.Throws<ArgumentException>(() => WLParameterExport.Write(
                path, new[] { range[0] with { Name = "bad'name" } }, new WLOptimizationExportOptions()));
        }

        [Test]
        public void RefusesOversizedCartesianGridBeforeWriting()
        {
            var path = Path.Combine(ScratchDirectory, Guid.NewGuid().ToString("N") + ".json");
            Assert.Throws<InvalidOperationException>(() => WLParameterExport.Write(path, new[]
            {
                new WLParameterSpec("a", typeof(int), 1, 1, 3, 1),
                new WLParameterSpec("b", typeof(decimal), 0.1m, 0.1m, 0.3m, 0.1m)
            }, new WLOptimizationExportOptions { MaximumGridRuns = 8 }));
            Assert.IsFalse(File.Exists(path));
        }

        [Test]
        public void RejectsEmptyDiscoveredRangesAndBaseConfigOverwrite()
        {
            var path = Path.Combine(ScratchDirectory, Guid.NewGuid().ToString("N") + ".json");
            Assert.Throws<InvalidOperationException>(() => WLParameterExport.Write(
                path, Array.Empty<WLParameterSpec>(), new WLOptimizationExportOptions()));
            Assert.Throws<ArgumentException>(() => WLParameterExport.Write(
                path,
                new[] { new WLParameterSpec("period", typeof(int), 2, 1, 3, 1) },
                new WLOptimizationExportOptions { BaseConfigPath = path }));
        }

        [Test]
        public void RejectsStaleRequestedNames()
        {
            var path = Path.Combine(ScratchDirectory, Guid.NewGuid().ToString("N") + ".json");
            Assert.Throws<ArgumentException>(() => WLParameterExport.Write(
                path,
                new[] { new WLParameterSpec("period", typeof(int), 2, 1, 3, 1) },
                new WLOptimizationExportOptions { RequestedNames = new[] { "missing" } }));
        }

        [Test]
        public void DoesNotOverwriteAnExistingConfigFile()
        {
            var path = Path.Combine(ScratchDirectory, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, "caller-config");
            try
            {
                Assert.Throws<IOException>(() => WLParameterExport.Write(
                    path,
                    new[] { new WLParameterSpec("period", typeof(int), 2, 1, 3, 1) },
                    new WLOptimizationExportOptions()));
                Assert.AreEqual("caller-config", File.ReadAllText(path));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
