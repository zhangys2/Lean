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

using QuantConnect.Util;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using QuantConnect.Configuration;
using QuantConnect.Optimizer;
using QuantConnect.Optimizer.Parameters;
using Log = QuantConnect.Logging.Log;

namespace QuantConnect.Optimizer.Launcher
{
    /// <summary>
    /// Optimizer implementation that launches Lean as a local process
    /// </summary>
    public class ConsoleLeanOptimizer : LeanOptimizer
    {
        private readonly string _leanLocation;
        private readonly string _rootResultDirectory;
        private readonly string _extraLeanArguments;
        private readonly ConcurrentDictionary<string, Process> _processByBacktestId;
        private readonly bool _captureOptimizationTable;
        private readonly WLTableOptions _tableOptions;
        private readonly List<WLRunRow> _runRows = new();
        private readonly object _runRowsLock = new();

        /// <summary>
        /// Creates a new instance
        /// </summary>
        /// <param name="nodePacket">The optimization node packet to handle</param>
        public ConsoleLeanOptimizer(OptimizationNodePacket nodePacket) : base(nodePacket)
        {
            _processByBacktestId = new ConcurrentDictionary<string, Process>();

            _rootResultDirectory = Configuration.Config.Get("results-destination-folder",
                Path.Combine(Directory.GetCurrentDirectory(), $"opt-{nodePacket.OptimizationId}"));
            Directory.CreateDirectory(_rootResultDirectory);

            _leanLocation = Configuration.Config.Get("lean-binaries-location",
                Path.Combine(Directory.GetCurrentDirectory(), "../../../Launcher/bin/Debug/QuantConnect.Lean.Launcher.dll"));

            var closeLeanAutomatically = Configuration.Config.GetBool("optimizer-close-automatically", true);
            _extraLeanArguments = $"--close-automatically {closeLeanAutomatically}";

            var algorithmTypeName = Configuration.Config.Get("algorithm-type-name");
            if (!string.IsNullOrEmpty(algorithmTypeName))
            {
                _extraLeanArguments += $" --algorithm-type-name \"{algorithmTypeName}\"";
            }

            var algorithmLanguage = Configuration.Config.Get("algorithm-language");
            if (!string.IsNullOrEmpty(algorithmLanguage))
            {
                _extraLeanArguments += $" --algorithm-language \"{algorithmLanguage}\"";
            }

            var algorithmLocation = Configuration.Config.Get("algorithm-location");
            if (!string.IsNullOrEmpty(algorithmLocation))
            {
                _extraLeanArguments += $" --algorithm-location \"{algorithmLocation}\"";
            }

            _captureOptimizationTable = Config.GetBool("wl-optimization-table", false);
            if (_captureOptimizationTable)
            {
                _tableOptions = WLTableConfig.ParseFromConfig();
                Log.Trace("ConsoleLeanOptimizer: WL optimization table capture enabled.");
            }
        }

        /// <summary>
        /// Rows captured from completed child backtests when <c>wl-optimization-table</c> is enabled.
        /// </summary>
        public IReadOnlyList<WLRunRow> CapturedRunRows
        {
            get
            {
                lock (_runRowsLock)
                {
                    return _runRows.ToList();
                }
            }
        }

        /// <summary>
        /// Handles starting Lean for a given parameter set
        /// </summary>
        /// <param name="parameterSet">The parameter set for the backtest to run</param>
        /// <param name="backtestName">The backtest name to use</param>
        /// <returns>The new unique backtest id</returns>
        protected override string RunLean(ParameterSet parameterSet, string backtestName)
        {
            var backtestId = Guid.NewGuid().ToString();
            var optimizationId = NodePacket.OptimizationId;
            // start each lean instance in its own directory so they store their logs & results, else they fight for the log.txt file
            var resultDirectory = Path.Combine(_rootResultDirectory, backtestId);
            Directory.CreateDirectory(resultDirectory);

            var parameters = parameterSet.ToString();
            var activeWindow = ReadActiveWalkForwardWindow();
            if (activeWindow.HasValue)
            {
                parameters += string.Create(CultureInfo.InvariantCulture,
                    $",{WLWalkForward.StartParameter}:{activeWindow.Value.Start:yyyyMMdd},{WLWalkForward.EndParameter}:{activeWindow.Value.EndExclusive:yyyyMMdd}");
            }

            var arguments = $"--results-destination-folder \"{resultDirectory}\" --algorithm-id \"{backtestId}\" --optimization-id \"{optimizationId}\" --parameters {parameters} --backtest-name \"{backtestName}\" {_extraLeanArguments}";

            var process = new Process
            {
                StartInfo = CreateLeanStartInfo(_leanLocation, arguments),
                EnableRaisingEvents = true
            };
            _processByBacktestId[backtestId] = process;

            process.Exited += (sender, args) =>
            {
                if (Disposed)
                {
                    // handle abort
                    return;
                }

                _processByBacktestId.TryRemove(backtestId, out process);
                var backtestResult = $"{backtestId}.json";
                var resultJson = Path.Combine(_rootResultDirectory, backtestId, backtestResult);
                NewResult(File.Exists(resultJson) ? File.ReadAllText(resultJson) : null, backtestId);
                process.DisposeSafely();
            };

            process.Start();

            return backtestId;
        }

        protected override void NewResult(string jsonBacktestResult, string backtestId)
        {
            if (_captureOptimizationTable
                && RunningParameterSetForBacktest.TryGetValue(backtestId, out var parameterSet))
            {
                var row = WLRunRow.FromBacktestResult(backtestId, parameterSet, jsonBacktestResult, _tableOptions);
                lock (_runRowsLock)
                {
                    _runRows.Add(row);
                }
            }

            base.NewResult(jsonBacktestResult, backtestId);
        }

        /// <summary>
        /// Writes the captured optimization table when enabled.
        /// </summary>
        public void WriteOptimizationTableIfEnabled()
        {
            if (!_captureOptimizationTable)
            {
                return;
            }

            List<WLRunRow> snapshot;
            lock (_runRowsLock)
            {
                snapshot = _runRows.ToList();
            }

            WLOptimizationTable.Write(_rootResultDirectory, _tableOptions, snapshot);
        }

        /// <summary>
        /// Creates the start info to launch Lean from the given location
        /// </summary>
        /// <param name="leanLocation">The Lean launcher assembly (.dll) or executable to run</param>
        /// <param name="arguments">The Lean command line arguments</param>
        /// <returns>The start info for the Lean process</returns>
        internal static ProcessStartInfo CreateLeanStartInfo(string leanLocation, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = leanLocation,
                WorkingDirectory = Directory.GetParent(leanLocation).FullName,
                Arguments = arguments,
                WindowStyle = ProcessWindowStyle.Minimized
            };

            // Run assemblies through the dotnet host: the native apphost only runs on the architecture it was built for
            if (leanLocation.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.FileName = "dotnet";
                startInfo.Arguments = $"\"{leanLocation}\" {arguments}";
            }

            return startInfo;
        }

        /// <summary>
        /// Stops lean process
        /// </summary>
        /// <param name="backtestId">Specified backtest id</param>
        protected override void AbortLean(string backtestId)
        {
            Process process;
            if (_processByBacktestId.TryRemove(backtestId, out process))
            {
                process.Kill();
                process.DisposeSafely();
            }
        }

        private static (DateTime Start, DateTime EndExclusive)? ReadActiveWalkForwardWindow()
        {
            var startText = Config.Get("wl-active-wf-start");
            var endText = Config.Get("wl-active-wf-end");
            if (string.IsNullOrWhiteSpace(startText) || string.IsNullOrWhiteSpace(endText))
            {
                return null;
            }

            return (QuantConnect.Time.ParseDate(startText), QuantConnect.Time.ParseDate(endText));
        }

        /// <summary>
        /// Sends an update of the current optimization status to the user
        /// </summary>
        protected override void SendUpdate()
        {
            // end handler will already log a nice message on end
            if (Status != OptimizationStatus.Completed && Status != OptimizationStatus.Aborted)
            {
                var currentEstimate = GetCurrentEstimate();
                var stats = GetRuntimeStatistics();
                var message = $"ConsoleLeanOptimizer.SendUpdate(): {currentEstimate} {string.Join(", ", stats.Select(pair => $"{pair.Key}:{pair.Value}"))}";
                var currentBestBacktest = Strategy.Solution;
                if (currentBestBacktest != null)
                {
                    message += $". Best id:'{currentBestBacktest.BacktestId}'. {OptimizationTarget}. Parameters ({currentBestBacktest.ParameterSet})";
                }
                Log.Trace(message);
            }
        }
    }
}