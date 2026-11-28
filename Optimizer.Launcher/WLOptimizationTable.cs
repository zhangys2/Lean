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
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Configuration;
using QuantConnect.Optimizer.Objectives;
using QuantConnect.Optimizer.Parameters;

namespace QuantConnect.Optimizer.Launcher
{
    internal static class WLTableConfig
    {
        internal const string DefaultObjectiveColumn = "Statistics.Sharpe Ratio";

        public static WLTableOptions ParseFromConfig()
        {
            var metricColumns = SplitConfigList(Config.Get("wl-optimization-table-metrics", string.Empty));
            var tieOrder = SplitConfigList(Config.Get("wl-optimization-table-parameter-tie-order", string.Empty));

            var defaultCriterion = $"{{\"target\":\"{DefaultObjectiveColumn}\", \"extremum\":\"max\"}}";
            var objective = Config.Get("optimization-criterion", defaultCriterion);
            var objectiveJson = JObject.Parse(objective);
            var objectiveColumn = objectiveJson["target"]?.Value<string>() ?? DefaultObjectiveColumn;
            var target = Newtonsoft.Json.JsonConvert.DeserializeObject<Target>(objective);
            var maximize = target?.Extremum is not Minimization;

            return new WLTableOptions
            {
                MetricColumns = metricColumns,
                ObjectiveColumn = objectiveColumn,
                MaximizeObjective = maximize,
                ParameterTieOrder = tieOrder.Count == 0 ? null : tieOrder
            };
        }

        private static List<string> SplitConfigList(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new List<string>();
            }

            return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
    }
    /// <summary>
    /// Configuration for writing WL optimization run tables. Numeric objective and metric values
    /// use invariant decimal formatting; a percent sign is removed without scaling, so 12% is
    /// represented as 12 percentage points. Currency symbols are removed by numeric parsing while
    /// the amount remains in the statistic's native currency unit. Non-numeric values are preserved.
    /// </summary>
    public sealed class WLTableOptions
    {
        public IReadOnlyList<string> MetricColumns { get; init; } = Array.Empty<string>();

        public string ObjectiveColumn { get; init; } = WLTableConfig.DefaultObjectiveColumn;

        public bool MaximizeObjective { get; init; } = true;

        /// <summary>
        /// Parameter names in tie-break order. When null, ordinal parameter name order is used.
        /// </summary>
        public IReadOnlyList<string> ParameterTieOrder { get; init; }
    }

    /// <summary>
    /// One row in the WL optimization table; JSON is never retained on the row beyond parsed scalars.
    /// </summary>
    public sealed class WLRunRow
    {
        public string RunId { get; init; }

        public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public string ProcessStatus { get; init; }

        public string OrderListHash { get; init; }

        public IReadOnlyDictionary<string, string> Metrics { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Parsed objective scalar in the statistic's native unit (percentage points for percent-formatted values).
        /// </summary>
        public decimal? ObjectiveValue { get; init; }

        public int Rank { get; set; }

        public static WLRunRow FromBacktestResult(
            string runId,
            ParameterSet parameterSet,
            string jsonBacktestResult,
            WLTableOptions options)
        {
            var parameters = ParseParameters(parameterSet);
            if (string.IsNullOrEmpty(jsonBacktestResult))
            {
                return new WLRunRow
                {
                    RunId = runId,
                    Parameters = parameters,
                    ProcessStatus = "failed",
                    Metrics = new Dictionary<string, string>(StringComparer.Ordinal)
                };
            }

            JObject parsed;
            try
            {
                parsed = JObject.Parse(jsonBacktestResult);
            }
            catch (JsonException)
            {
                return new WLRunRow
                {
                    RunId = runId,
                    Parameters = parameters,
                    ProcessStatus = "failed",
                    Metrics = new Dictionary<string, string>(StringComparer.Ordinal)
                };
            }

            var metrics = ExtractMetrics(parsed, options?.MetricColumns);
            var objective = TryParseMetric(parsed, options?.ObjectiveColumn);
            var hash = TryReadStatistic(parsed, "OrderListHash");

            return new WLRunRow
            {
                RunId = runId,
                Parameters = parameters,
                ProcessStatus = "completed",
                OrderListHash = hash,
                Metrics = metrics,
                ObjectiveValue = objective
            };
        }

        private static Dictionary<string, string> ParseParameters(ParameterSet parameterSet)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (parameterSet?.Value == null)
            {
                return result;
            }

            foreach (var pair in parameterSet.Value.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                result[pair.Key] = pair.Value;
            }

            return result;
        }

        private static Dictionary<string, string> ExtractMetrics(JObject parsed, IReadOnlyList<string> metricColumns)
        {
            var metrics = new Dictionary<string, string>(StringComparer.Ordinal);
            if (metricColumns == null || metricColumns.Count == 0)
            {
                return metrics;
            }

            foreach (var column in metricColumns)
            {
                if (string.IsNullOrWhiteSpace(column))
                {
                    continue;
                }

                var text = TryReadPath(parsed, column);
                if (text == null)
                {
                    metrics[column] = null;
                }
                else
                {
                    var numericValue = TryParseMetric(parsed, column);
                    metrics[column] = numericValue.HasValue
                        ? numericValue.Value.ToString(CultureInfo.InvariantCulture)
                        : text;
                }
            }

            return metrics;
        }

        private static decimal? TryParseMetric(JObject parsed, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var token = ResolveToken(parsed, path);
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            var text = token.Type == JTokenType.String
                ? token.Value<string>()
                : token.ToString(Formatting.None);

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            text = text.Replace("%", string.Empty, StringComparison.Ordinal).Trim();
            if (!decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            return value;
        }

        private static string TryReadStatistic(JObject parsed, string statisticName)
        {
            if (parsed.TryGetValue("Statistics", StringComparison.OrdinalIgnoreCase, out var statisticsToken)
                && statisticsToken is JObject statistics
                && statistics.TryGetValue(statisticName, StringComparison.OrdinalIgnoreCase, out var valueToken))
            {
                return valueToken.Type == JTokenType.Null ? null : valueToken.ToString();
            }

            return null;
        }

        private static string TryReadPath(JObject parsed, string path)
        {
            var token = ResolveToken(parsed, path);
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            return token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);
        }

        private static JToken ResolveToken(JObject root, string path)
        {
            if (root == null || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            JToken current = root;
            foreach (var segment in segments)
            {
                if (current is not JObject currentObject)
                {
                    return null;
                }

                if (!currentObject.TryGetValue(segment, StringComparison.OrdinalIgnoreCase, out current))
                {
                    return null;
                }
            }

            return current;
        }
    }

    /// <summary>
    /// Writes immutable CSV and JSON tables for WL optimization runs.
    /// </summary>
    public static class WLOptimizationTable
    {
        public const string CsvFileName = "wl-optimization-table.csv";
        public const string JsonFileName = "wl-optimization-table.json";

        public static void Write(string directory, WLTableOptions options, IEnumerable<WLRunRow> rows)
        {
            ArgumentException.ThrowIfNullOrEmpty(directory);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(rows);

            Directory.CreateDirectory(directory);
            var ranked = RankRows(options, rows).ToList();

            WriteJson(Path.Combine(directory, JsonFileName), options, ranked);
            WriteCsv(Path.Combine(directory, CsvFileName), options, ranked);
        }

        internal static IReadOnlyList<WLRunRow> RankRows(WLTableOptions options, IEnumerable<WLRunRow> rows)
        {
            var materialized = rows.ToList();
            var parameterOrder = options.ParameterTieOrder?.ToList()
                ?? materialized
                    .SelectMany(row => row.Parameters.Keys)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList();

            var ordered = materialized
                .OrderBy(row => row, Comparer<WLRunRow>.Create((left, right) => CompareRows(options, parameterOrder, left, right)))
                .ToList();

            for (var index = 0; index < ordered.Count; index++)
            {
                ordered[index].Rank = index + 1;
            }

            return ordered;
        }

        private static int CompareRows(
            WLTableOptions options,
            IReadOnlyList<string> parameterOrder,
            WLRunRow left,
            WLRunRow right)
        {
            var objectiveCompare = CompareObjective(options, left.ObjectiveValue, right.ObjectiveValue);
            if (objectiveCompare != 0)
            {
                return objectiveCompare;
            }

            foreach (var name in parameterOrder)
            {
                left.Parameters.TryGetValue(name, out var leftValue);
                right.Parameters.TryGetValue(name, out var rightValue);
                var parameterCompare = string.CompareOrdinal(leftValue, rightValue);
                if (parameterCompare != 0)
                {
                    return parameterCompare;
                }
            }

            return string.CompareOrdinal(left.RunId, right.RunId);
        }

        private static int CompareObjective(WLTableOptions options, decimal? left, decimal? right)
        {
            if (!left.HasValue && !right.HasValue)
            {
                return 0;
            }

            if (!left.HasValue)
            {
                return 1;
            }

            if (!right.HasValue)
            {
                return -1;
            }

            var delta = left.Value.CompareTo(right.Value);
            if (delta == 0)
            {
                return 0;
            }

            return options.MaximizeObjective ? -delta : delta;
        }

        private static void WriteJson(string path, WLTableOptions options, IReadOnlyList<WLRunRow> rows)
        {
            var payload = new
            {
                objective = options.ObjectiveColumn,
                maximize = options.MaximizeObjective,
                metricColumns = options.MetricColumns,
                numericUnitConvention = "Invariant decimal; percent signs removed without scaling; currency remains in the source statistic's native unit; non-numeric values preserved.",
                rows = rows.Select(row => new
                {
                    rank = row.Rank,
                    runId = row.RunId,
                    processStatus = row.ProcessStatus,
                    orderListHash = row.OrderListHash,
                    parameters = row.Parameters,
                    metrics = row.Metrics,
                    objective = row.ObjectiveValue
                })
            };

            File.WriteAllText(path, JsonConvert.SerializeObject(payload, Formatting.Indented));
        }

        private static void WriteCsv(string path, WLTableOptions options, IReadOnlyList<WLRunRow> rows)
        {
            var parameterNames = rows
                .SelectMany(row => row.Parameters.Keys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            var metricColumns = options.MetricColumns?.ToList() ?? new List<string>();
            var builder = new StringBuilder();
            builder.Append("rank,run_id,process_status,order_list_hash");
            foreach (var name in parameterNames)
            {
                builder.Append($",param_{name}");
            }

            builder.Append(",objective");
            foreach (var metric in metricColumns)
            {
                builder.Append($",metric_{SanitizeHeader(metric)}");
            }

            builder.AppendLine();

            foreach (var row in rows)
            {
                builder.Append($"{row.Rank},{Escape(row.RunId)},{Escape(row.ProcessStatus)},{Escape(row.OrderListHash)}");
                foreach (var name in parameterNames)
                {
                    row.Parameters.TryGetValue(name, out var value);
                    builder.Append($",{Escape(value)}");
                }

                builder.Append($",{FormatDecimal(row.ObjectiveValue)}");
                foreach (var metric in metricColumns)
                {
                    row.Metrics.TryGetValue(metric, out var value);
                    builder.Append($",{Escape(value)}");
                }

                builder.AppendLine();
            }

            File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
        }

        private static string SanitizeHeader(string metric) => metric.Replace(".", "_", StringComparison.Ordinal);

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            if (value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal))
            {
                return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
            }

            return value;
        }

        private static string FormatDecimal(decimal? value) =>
            value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }
}