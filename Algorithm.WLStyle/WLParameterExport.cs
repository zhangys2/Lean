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
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace QuantConnect.Algorithm.WLStyle
{
    /// <summary>
    /// One AddParameter declaration prepared for Lean's step-grid optimizer.
    /// </summary>
    public sealed record WLParameterSpec(
        string Name,
        Type Type,
        decimal Default,
        decimal Min,
        decimal Max,
        decimal Step,
        bool Enabled = true);

    /// <summary>
    /// Options for writing a standalone Lean optimizer configuration.
    /// </summary>
    public sealed record WLOptimizationExportOptions
    {
        /// <summary>Maximum permitted Cartesian grid size.</summary>
        public long MaximumGridRuns { get; init; } = 100_000;

        /// <summary>Valid Lean result path used to rank runs.</summary>
        public string Objective { get; init; } = "Statistics.Net Profit";

        /// <summary>Whether to maximize or minimize the objective.</summary>
        public string Extremum { get; init; } = "max";

        /// <summary>Optional caller config path, which must not be the export destination.</summary>
        public string BaseConfigPath { get; init; }

        /// <summary>Optional subset of discovered names; all other registered dimensions are omitted.</summary>
        public IReadOnlyCollection<string> RequestedNames { get; init; }
    }

    /// <summary>
    /// Writes validated parameter ranges as a Lean grid-search configuration.
    /// </summary>
    public static class WLParameterExport
    {
        private const string GridStrategy = "QuantConnect.Optimizer.Strategies.GridSearchOptimizationStrategy";
        private static readonly HashSet<string> ValidObjectives = new(StringComparer.Ordinal)
        {
            "Statistics.Net Profit",
            "Statistics.Sharpe Ratio",
            "Statistics.Sortino Ratio",
            "Statistics.Total Orders",
            "Statistics.Total Fees",
            "Statistics.End Equity",
            "Statistics.Drawdown",
            "Statistics.WL_APR",
            "Statistics.WL_NetProfit",
            "Statistics.WL_ProfitFactor",
            "Statistics.WL_Sharpe"
        };

        /// <summary>
        /// Writes a standalone optimizer config fragment to <paramref name="path"/>.
        /// </summary>
        public static void Write(string path, IReadOnlyList<WLParameterSpec> ranges, WLOptimizationExportOptions options)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(ranges);
            ArgumentNullException.ThrowIfNull(options);
            if (ranges.Count == 0)
            {
                throw new InvalidOperationException("Cannot export an optimizer grid without discovered parameters.");
            }
            if (options.MaximumGridRuns <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options.MaximumGridRuns), "The grid run budget must be positive.");
            }
            if (!ValidObjectives.Contains(options.Objective))
            {
                throw new ArgumentException($"Unsupported optimizer objective '{options.Objective}'.", nameof(options));
            }
            if (options.Extremum != "max" && options.Extremum != "min")
            {
                throw new ArgumentException("Objective extremum must be 'max' or 'min'.", nameof(options));
            }
            if (!string.IsNullOrWhiteSpace(options.BaseConfigPath)
                && string.Equals(Path.GetFullPath(path), Path.GetFullPath(options.BaseConfigPath), StringComparison.Ordinal))
            {
                throw new ArgumentException("The optimizer export cannot overwrite the caller's base config.", nameof(path));
            }

            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            var requestedNames = options.RequestedNames == null
                ? null
                : new HashSet<string>(options.RequestedNames, StringComparer.Ordinal);
            if (requestedNames != null && requestedNames.Count != options.RequestedNames.Count)
            {
                throw new ArgumentException("Requested parameter names must be unique.", nameof(options));
            }
            var parameters = new JArray();
            long combinations = 1;
            foreach (var range in ranges)
            {
                ValidateRange(range, seenNames);
                if (requestedNames != null && !requestedNames.Contains(range.Name))
                {
                    continue;
                }
                var min = range.Enabled ? range.Min : range.Default;
                var max = range.Enabled ? range.Max : range.Default;
                var count = GetGridCount(min, max, range.Step);
                if (count <= 0 || combinations > options.MaximumGridRuns / count)
                {
                    throw new InvalidOperationException(
                        $"Optimizer grid exceeds the configured limit of {options.MaximumGridRuns} runs.");
                }
                combinations *= count;
                parameters.Add(new JObject
                {
                    ["name"] = range.Name,
                    ["min"] = min,
                    ["max"] = max,
                    ["step"] = range.Step,
                    ["minStep"] = range.Step
                });
            }
            if (requestedNames != null && requestedNames.Except(seenNames, StringComparer.Ordinal).Any())
            {
                throw new ArgumentException("One or more requested parameter names were not discovered.", nameof(options));
            }
            if (parameters.Count == 0)
            {
                throw new InvalidOperationException("Cannot export an optimizer grid without discovered parameters.");
            }

            var config = new JObject
            {
                ["optimization-strategy"] = GridStrategy,
                ["optimization-criterion"] = new JObject
                {
                    ["target"] = options.Objective,
                    ["extremum"] = options.Extremum
                },
                ["parameters"] = parameters
            };
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.Write(config.ToString(Formatting.Indented));
        }

        private static long GetGridCount(decimal min, decimal max, decimal step)
        {
            try
            {
                return decimal.ToInt64(decimal.Floor((max - min) / step) + 1);
            }
            catch (OverflowException exception)
            {
                throw new InvalidOperationException("Optimizer grid size exceeds the supported count.", exception);
            }
        }

        private static void ValidateRange(WLParameterSpec range, HashSet<string> seenNames)
        {
            if (range == null)
            {
                throw new ArgumentException("Parameter ranges cannot contain null entries.", nameof(range));
            }
            if (string.IsNullOrWhiteSpace(range.Name)
                || range.Name.Any(character => char.IsWhiteSpace(character) || character is ':' or ',' or '"' ))
            {
                throw new ArgumentException(
                    $"Parameter name '{range.Name}' cannot be represented by Lean's --parameters syntax.",
                    nameof(range));
            }
            if (!seenNames.Add(range.Name))
            {
                throw new ArgumentException($"Duplicate parameter name '{range.Name}'.", nameof(range));
            }
            if (range.Type != typeof(int) && range.Type != typeof(double) && range.Type != typeof(decimal))
            {
                throw new ArgumentException($"Parameter '{range.Name}' has unsupported type {range.Type?.Name}.", nameof(range));
            }
            if (range.Min > range.Max || range.Default < range.Min || range.Default > range.Max || range.Step <= 0)
            {
                throw new ArgumentException($"Parameter '{range.Name}' has invalid default or range bounds.", nameof(range));
            }
            if (range.Type == typeof(int)
                && (decimal.Truncate(range.Default) != range.Default
                    || decimal.Truncate(range.Min) != range.Min
                    || decimal.Truncate(range.Max) != range.Max
                    || decimal.Truncate(range.Step) != range.Step))
            {
                throw new ArgumentException($"Integer parameter '{range.Name}' must have integral values.", nameof(range));
            }
        }
    }
}
