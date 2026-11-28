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

using Newtonsoft.Json;
using QuantConnect.Configuration;
using QuantConnect.Logging;
using QuantConnect.Optimizer;
using QuantConnect.Optimizer.Objectives;
using QuantConnect.Optimizer.Parameters;
using QuantConnect.Optimizer.Strategies;
using QuantConnect.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace QuantConnect.Optimizer.Launcher
{
    public class Program
    {
        public static int Main(string[] args)
        {
            // Parse report arguments and merge with config to use in the optimizer
            if (args.Length > 0)
            {
                var cliArguments = OptimizerArgumentParser.ParseArguments(args);
                // Unknown arguments throw, so arguments that parse to no options only requested help or version, already shown
                if (cliArguments.Count == 0)
                {
                    return 0;
                }
                Config.MergeCommandLineArgumentsWithConfiguration(cliArguments);
            }

            using var endedEvent = new ManualResetEvent(false);
            LeanOptimizer optimizer = null;

            try
            {
                Log.DebuggingEnabled = Config.GetBool("debug-mode");
                Log.FilePath = Path.Combine(Config.Get("results-destination-folder"), "log.txt");
                Log.LogHandler = Composer.Instance.GetExportedValueByTypeName<ILogHandler>(Config.Get("log-handler", "CompositeLogHandler"));

                // TODO(P4-T6): merge WLParameterExport snapshots into walk-forward summaries when export lands.
                var packet = BuildOptimizationPacket();
                var optimizerType = Config.Get("optimization-launcher", typeof(ConsoleLeanOptimizer).Name);
                optimizer = (LeanOptimizer)Activator.CreateInstance(Composer.Instance.GetExportedTypes<LeanOptimizer>().Single(x => x.Name == optimizerType), packet);

                if (Config.GetBool("estimate", false))
                {
                    var backtestsCount = optimizer.GetCurrentEstimate();
                    Log.Trace($"Optimization estimate: {backtestsCount}");

                    optimizer.DisposeSafely();
                    endedEvent.Set();
                }
                else if (Config.GetBool("wl-walk-forward", false))
                {
                    RunWalkForward(optimizer, packet, endedEvent);
                }
                else
                {
                    optimizer.Start();

                    optimizer.Ended += (s, e) =>
                    {
                        WriteOutputs(optimizer);
                        optimizer.DisposeSafely();
                        endedEvent.Set();
                    };
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
                optimizer?.DisposeSafely();
                return 1;
            }

            // Wait until the optimizer has stopped running before exiting
            endedEvent.WaitOne();
            return 0;
        }

        private static OptimizationNodePacket BuildOptimizationPacket()
        {
            var optimizationStrategyName = Config.Get("optimization-strategy",
                "QuantConnect.Optimizer.GridSearchOptimizationStrategy");
            var channel = Config.Get("data-channel");
            var optimizationId = Config.Get("optimization-id", Guid.NewGuid().ToString());
            var packet = new OptimizationNodePacket
            {
                OptimizationId = optimizationId,
                OptimizationStrategy = optimizationStrategyName,
                OptimizationStrategySettings = (OptimizationStrategySettings)JsonConvert.DeserializeObject(Config.Get(
                    "optimization-strategy-settings",
                    "{\"$type\":\"QuantConnect.Optimizer.Strategies.OptimizationStrategySettings, QuantConnect.Optimizer\"}"), new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.All }),
                Criterion = JsonConvert.DeserializeObject<Target>(Config.Get("optimization-criterion", "{\"target\":\"Statistics.TotalProfit\", \"extremum\": \"max\"}")),
                Constraints = JsonConvert.DeserializeObject<List<Constraint>>(Config.Get("constraints", "[]")).AsReadOnly(),
                OptimizationParameters = JsonConvert.DeserializeObject<HashSet<OptimizationParameter>>(Config.Get("parameters", "[]")),
                MaximumConcurrentBacktests = Config.GetInt("maximum-concurrent-backtests", Math.Max(1, Environment.ProcessorCount / 2)),
                Channel = channel,
            };

            var outOfSampleMaxEndDate = Config.Get("out-of-sample-max-end-date");
            if (!string.IsNullOrEmpty(outOfSampleMaxEndDate))
            {
                packet.OutOfSampleMaxEndDate = Time.ParseDate(outOfSampleMaxEndDate);
            }

            packet.OutOfSampleDays = Config.GetInt("out-of-sample-days");
            return packet;
        }

        private static void RunWalkForward(LeanOptimizer optimizerPrototype, OptimizationNodePacket packet, ManualResetEvent endedEvent)
        {
            var leanLocation = Config.Get("lean-binaries-location",
                Path.Combine(Directory.GetCurrentDirectory(), "../../../Launcher/bin/Debug/QuantConnect.Lean.Launcher.dll"));
            var feasibility = WLWalkForward.TryFeasibilityGate(leanLocation, BuildLeanAlgorithmArguments());
            if (!feasibility.Passed)
            {
                Log.Error($"WL walk-forward blocked: {feasibility.Reason}");
                RunStandardOptimization(optimizerPrototype, endedEvent);
                return;
            }

            var options = WLWalkForward.ParseFromConfig();
            var windows = WLWalkForward.Plan(options);
            var resultRoot = Config.Get("results-destination-folder", Directory.GetCurrentDirectory());
            var completed = new List<WLWalkForwardWindow>();

            foreach (var window in windows)
            {
                Config.Set("wl-active-wf-start", window.InSampleStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Config.Set("wl-active-wf-end", window.InSampleEndExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

                var windowOptimizer = CreateOptimizer(packet);
                var windowResult = WaitForOptimization(windowOptimizer);
                var selected = windowResult?.ParameterSet?.Value?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);

                Config.Set("wl-active-wf-start", window.OutOfSampleStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Config.Set("wl-active-wf-end", window.OutOfSampleEndExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

                var oosJson = RunSingleBacktest(leanLocation, selected);
                var oosApr = TryReadStatistic(oosJson, "Compounding Annual Return");
                var isApr = TryReadStatistic(windowResult?.JsonBacktestResult, "Compounding Annual Return");
                completed.Add(new WLWalkForwardWindow
                {
                    Index = window.Index,
                    InSampleStart = window.InSampleStart,
                    InSampleEndExclusive = window.InSampleEndExclusive,
                    OutOfSampleStart = window.OutOfSampleStart,
                    OutOfSampleEndExclusive = window.OutOfSampleEndExclusive,
                    SelectedParameters = selected,
                    InSampleAnnualReturn = isApr,
                    OutOfSampleAnnualReturn = oosApr
                });

                windowOptimizer.DisposeSafely();
            }

            Config.Set("wl-active-wf-start", string.Empty);
            Config.Set("wl-active-wf-end", string.Empty);
            WLWalkForward.WriteSummary(resultRoot, completed);
            optimizerPrototype.DisposeSafely();
            endedEvent.Set();
        }

        private static void RunStandardOptimization(LeanOptimizer optimizer, ManualResetEvent endedEvent)
        {
            optimizer.Start();
            optimizer.Ended += (s, e) =>
            {
                WriteOutputs(optimizer);
                optimizer.DisposeSafely();
                endedEvent.Set();
            };
        }

        private static LeanOptimizer CreateOptimizer(OptimizationNodePacket packet)
        {
            var optimizerType = Config.Get("optimization-launcher", typeof(ConsoleLeanOptimizer).Name);
            return (LeanOptimizer)Activator.CreateInstance(Composer.Instance.GetExportedTypes<LeanOptimizer>().Single(x => x.Name == optimizerType), packet);
        }

        private static OptimizationResult WaitForOptimization(LeanOptimizer optimizer)
        {
            OptimizationResult result = null;
            using var finished = new ManualResetEvent(false);
            optimizer.Ended += (s, e) =>
            {
                result = e;
                WriteOutputs(optimizer);
                finished.Set();
            };
            optimizer.Start();
            if (!finished.WaitOne(TimeSpan.FromHours(6)))
            {
                throw new TimeoutException("The walk-forward in-sample optimizer timed out.");
            }

            return result;
        }

        private static string RunSingleBacktest(string leanLocation, IReadOnlyDictionary<string, string> parameters)
        {
            if (string.IsNullOrWhiteSpace(leanLocation) || !File.Exists(leanLocation))
            {
                return null;
            }

            var root = Path.Combine(Path.GetTempPath(), $"wl-oos-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var parameterText = string.Join(',', parameters.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"));
                var activeStart = Config.Get("wl-active-wf-start");
                var activeEnd = Config.Get("wl-active-wf-end");
                if (!string.IsNullOrEmpty(activeStart) && !string.IsNullOrEmpty(activeEnd))
                {
                    parameterText += string.Create(CultureInfo.InvariantCulture,
                        $",{WLWalkForward.StartParameter}:{DateTime.Parse(activeStart, CultureInfo.InvariantCulture):yyyyMMdd},{WLWalkForward.EndParameter}:{DateTime.Parse(activeEnd, CultureInfo.InvariantCulture):yyyyMMdd}");
                }

                var arguments = $"--results-destination-folder \"{root}\" --algorithm-id \"oos\" {BuildLeanAlgorithmArguments()} --parameters {parameterText}";
                var process = System.Diagnostics.Process.Start(ConsoleLeanOptimizer.CreateLeanStartInfo(leanLocation, arguments));
                if (process == null || !process.WaitForExit((int)TimeSpan.FromMinutes(30).TotalMilliseconds))
                {
                    return null;
                }

                var jsonPath = Path.Combine(root, "oos.json");
                return File.Exists(jsonPath) ? File.ReadAllText(jsonPath) : null;
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        private static string BuildLeanAlgorithmArguments()
        {
            var arguments = string.Empty;
            var algorithmTypeName = Config.Get("algorithm-type-name");
            if (!string.IsNullOrEmpty(algorithmTypeName))
            {
                arguments += $" --algorithm-type-name \"{algorithmTypeName}\"";
            }

            var algorithmLanguage = Config.Get("algorithm-language");
            if (!string.IsNullOrEmpty(algorithmLanguage))
            {
                arguments += $" --algorithm-language \"{algorithmLanguage}\"";
            }

            var algorithmLocation = Config.Get("algorithm-location");
            if (!string.IsNullOrEmpty(algorithmLocation))
            {
                arguments += $" --algorithm-location \"{algorithmLocation}\"";
            }

            var closeLeanAutomatically = Config.GetBool("optimizer-close-automatically", true);
            arguments += $" --close-automatically {closeLeanAutomatically}";
            return arguments;
        }

        private static decimal? TryReadStatistic(string json, string name)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var token = Newtonsoft.Json.Linq.JObject.Parse(json);
            if (!token.TryGetValue("Statistics", StringComparison.OrdinalIgnoreCase, out var statisticsToken)
                || statisticsToken is not Newtonsoft.Json.Linq.JObject statistics
                || !statistics.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var valueToken))
            {
                return null;
            }

            var text = valueToken.ToString().Replace("%", string.Empty, StringComparison.Ordinal).Trim();
            return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;
        }

        private static void WriteOutputs(LeanOptimizer optimizer)
        {
            if (optimizer is ConsoleLeanOptimizer consoleOptimizer)
            {
                consoleOptimizer.WriteOptimizationTableIfEnabled();
            }
        }
    }
}