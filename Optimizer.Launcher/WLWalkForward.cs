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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using QuantConnect.Configuration;
using QuantConnect.Logging;
using QuantConnect.Util;

namespace QuantConnect.Optimizer.Launcher
{
  public enum WLWalkForwardIsMode
  {
    Sliding,
    Expanding
  }

  /// <summary>
  /// Walk-forward planning options. Calendar windows use half-open <c>[start, end)</c> intervals.
  /// </summary>
  public sealed class WLWalkForwardOptions
  {
    public DateTime Start { get; init; }

    /// <summary>Exclusive end of the total walk-forward range.</summary>
    public DateTime EndExclusive { get; init; }

    public int InSampleDays { get; init; }

    public int OutOfSampleDays { get; init; }

    public int StrideDays { get; init; }

    public WLWalkForwardIsMode Mode { get; init; } = WLWalkForwardIsMode.Sliding;

    /// <summary>When set, the first OOS length is <c>max(1, floor(firstWindowDays * percent / 100))</c>.</summary>
    public int? FirstOutOfSamplePercent { get; init; }
  }

  public sealed class WLWalkForwardWindow
  {
    public int Index { get; init; }

    public DateTime InSampleStart { get; init; }

    public DateTime InSampleEndExclusive { get; init; }

    public DateTime OutOfSampleStart { get; init; }

    public DateTime OutOfSampleEndExclusive { get; init; }

    public DateTime LeanInSampleEndInclusive => InSampleEndExclusive.AddDays(-1);

    public DateTime LeanOutOfSampleEndInclusive => OutOfSampleEndExclusive.AddDays(-1);

    public IReadOnlyDictionary<string, string> SelectedParameters { get; init; }

    public decimal? InSampleAnnualReturn { get; init; }

    public decimal? OutOfSampleAnnualReturn { get; init; }

    public decimal? EfficiencyRatio
    {
      get
      {
        if (!InSampleAnnualReturn.HasValue || InSampleAnnualReturn.Value <= 0m || !OutOfSampleAnnualReturn.HasValue)
        {
          return null;
        }

        return OutOfSampleAnnualReturn.Value / InSampleAnnualReturn.Value;
      }
    }
  }

  public sealed class WLWalkForwardFeasibilityResult
  {
    public bool Passed { get; init; }

    public string Reason { get; init; }
  }

  /// <summary>
  /// Plans walk-forward windows and optionally validates date isolation using child Lean processes.
  /// </summary>
  public static class WLWalkForward
  {
    internal const string StartParameter = "wl-wf-start";
    internal const string EndParameter = "wl-wf-end";

    public static IReadOnlyList<WLWalkForwardWindow> Plan(WLWalkForwardOptions options)
    {
      ArgumentNullException.ThrowIfNull(options);
      if (options.EndExclusive <= options.Start)
      {
        throw new ArgumentException("The walk-forward range must be positive.");
      }

      if (options.InSampleDays <= 0 || options.OutOfSampleDays <= 0 || options.StrideDays <= 0)
      {
        throw new ArgumentException("Walk-forward day counts must be positive.");
      }

      var windows = new List<WLWalkForwardWindow>();
      var index = 0;
      var isStart = options.Start;
      var fixedIsStart = options.Start;

      while (true)
      {
        DateTime isEndExclusive;
        if (options.Mode == WLWalkForwardIsMode.Expanding)
        {
          isEndExclusive = options.Start.AddDays(options.InSampleDays + index * options.StrideDays);
        }
        else
        {
          isEndExclusive = isStart.AddDays(options.InSampleDays);
        }

        var oosStart = isEndExclusive;
        var oosDays = index == 0
          ? ResolveFirstOutOfSampleDays(options)
          : options.OutOfSampleDays;
        var oosEndExclusive = oosStart.AddDays(oosDays);
        if (oosEndExclusive > options.EndExclusive)
        {
          break;
        }

        windows.Add(new WLWalkForwardWindow
        {
          Index = index,
          InSampleStart = options.Mode == WLWalkForwardIsMode.Expanding ? fixedIsStart : isStart,
          InSampleEndExclusive = isEndExclusive,
          OutOfSampleStart = oosStart,
          OutOfSampleEndExclusive = oosEndExclusive
        });

        index++;
        isStart = isStart.AddDays(options.StrideDays);
        if (options.Mode == WLWalkForwardIsMode.Sliding)
        {
          if (isStart.AddDays(options.InSampleDays + oosDays) > options.EndExclusive)
          {
            break;
          }
        }
        else if (fixedIsStart.AddDays(options.InSampleDays + index * options.StrideDays + oosDays) > options.EndExclusive)
        {
          break;
        }
      }

      return windows;
    }

    public static WLWalkForwardFeasibilityResult TryFeasibilityGate(string leanLocation, string algorithmArguments)
    {
      if (string.IsNullOrWhiteSpace(leanLocation) || !File.Exists(leanLocation))
      {
        return new WLWalkForwardFeasibilityResult
        {
          Passed = false,
          Reason = "Lean launcher binaries are not available to validate walk-forward date isolation."
        };
      }

      var plan = Plan(new WLWalkForwardOptions
      {
        Start = new DateTime(2013, 10, 7),
        EndExclusive = new DateTime(2013, 10, 11),
        InSampleDays = 2,
        OutOfSampleDays = 1,
        StrideDays = 1
      });

      if (plan.Count < 2)
      {
        return new WLWalkForwardFeasibilityResult
        {
          Passed = false,
          Reason = "The two-window fixture could not be planned."
        };
      }

      var hashes = new List<string>();
      foreach (var window in plan.Take(2))
      {
        var result = RunIsolatedChild(leanLocation, algorithmArguments, window.InSampleStart, window.InSampleEndExclusive);
        if (result == null)
        {
          return new WLWalkForwardFeasibilityResult
          {
            Passed = false,
            Reason = $"A walk-forward child backtest failed for in-sample window {window.Index}."
          };
        }

        if (string.IsNullOrEmpty(result.Value.Hash))
        {
          return new WLWalkForwardFeasibilityResult
          {
            Passed = false,
            Reason = $"Walk-forward child {window.Index} returned no order hash."
          };
        }

        hashes.Add(result.Value.Hash);
      }

      if (hashes.Distinct(StringComparer.Ordinal).Count() < 2)
      {
        return new WLWalkForwardFeasibilityResult
        {
          Passed = false,
          Reason = "Walk-forward windows produced identical child order hashes; date isolation failed."
        };
      }

      return new WLWalkForwardFeasibilityResult { Passed = true };
    }

    private static int ResolveFirstOutOfSampleDays(WLWalkForwardOptions options)
    {
      if (!options.FirstOutOfSamplePercent.HasValue)
      {
        return options.OutOfSampleDays;
      }

      var firstWindowDays = options.InSampleDays + options.OutOfSampleDays;
      var scaled = (int)Math.Floor(firstWindowDays * options.FirstOutOfSamplePercent.Value / 100m);
      return Math.Max(1, scaled);
    }

    private static (string Hash, string Metric)? RunIsolatedChild(
      string leanLocation,
      string algorithmArguments,
      DateTime inSampleStart,
      DateTime inSampleEndExclusive)
    {
      var root = Path.Combine(Path.GetTempPath(), $"wl-wfo-gate-{Guid.NewGuid():N}");
      Directory.CreateDirectory(root);
      try
      {
        var parameters = string.Join(',', new[]
        {
          $"{StartParameter}:{inSampleStart:yyyyMMdd}",
          $"{EndParameter}:{inSampleEndExclusive:yyyyMMdd}"
        });

        var startInfo = ConsoleLeanOptimizer.CreateLeanStartInfo(
          leanLocation,
          $"--results-destination-folder \"{root}\" --algorithm-id \"gate\" --parameters {parameters} {algorithmArguments}");

        using var process = Process.Start(startInfo);
        if (process == null)
        {
          return null;
        }

        if (!process.WaitForExit((int)TimeSpan.FromMinutes(5).TotalMilliseconds))
        {
          process.Kill(entireProcessTree: true);
          return null;
        }

        var jsonPath = Path.Combine(root, "gate.json");
        if (!File.Exists(jsonPath))
        {
          return null;
        }

        var json = File.ReadAllText(jsonPath);
        var parsed = JObject.Parse(json);
        parsed.TryGetValue("Statistics", StringComparison.OrdinalIgnoreCase, out var statisticsToken);
        if (statisticsToken is not JObject statistics)
        {
          return null;
        }

        statistics.TryGetValue("OrderListHash", StringComparison.OrdinalIgnoreCase, out var hashToken);
        statistics.TryGetValue("WL_GateMetric", StringComparison.OrdinalIgnoreCase, out var metricToken);
        return (hashToken?.ToString(), metricToken?.ToString());
      }
      catch (Exception exception)
      {
        Log.Error(exception, "WLWalkForward.TryFeasibilityGate(): child process failed");
        return null;
      }
      finally
      {
        try
        {
          Directory.Delete(root, recursive: true);
        }
        catch
        {
          // ignore cleanup failures in the gate
        }
      }
    }

    public static void WriteSummary(string directory, IReadOnlyList<WLWalkForwardWindow> windows)
    {
      ArgumentException.ThrowIfNullOrEmpty(directory);
      ArgumentNullException.ThrowIfNull(windows);
      Directory.CreateDirectory(directory);
      var payload = windows.Select(window => new
      {
        window.Index,
        inSample = new
        {
          start = window.InSampleStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
          endExclusive = window.InSampleEndExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
          leanEnd = window.LeanInSampleEndInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        },
        outOfSample = new
        {
          start = window.OutOfSampleStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
          endExclusive = window.OutOfSampleEndExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
          leanEnd = window.LeanOutOfSampleEndInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        },
        selectedParameters = window.SelectedParameters,
        inSampleAnnualReturn = window.InSampleAnnualReturn,
        outOfSampleAnnualReturn = window.OutOfSampleAnnualReturn,
        efficiencyRatio = window.EfficiencyRatio
      });
      File.WriteAllText(
        Path.Combine(directory, "wl-walk-forward-summary.json"),
        Newtonsoft.Json.JsonConvert.SerializeObject(payload, Newtonsoft.Json.Formatting.Indented));
    }

    public static WLWalkForwardOptions ParseFromConfig()
    {
      var start = Time.ParseDate(Config.Get("wl-walk-forward-start", "2013-10-07"));
      var endExclusive = Time.ParseDate(Config.Get("wl-walk-forward-end-exclusive", "2013-10-11"));
      var modeText = Config.Get("wl-walk-forward-mode", "sliding");
      Enum.TryParse<WLWalkForwardIsMode>(modeText, ignoreCase: true, out var mode);

      int? firstOosPercent = null;
      var percentText = Config.Get("wl-walk-forward-first-oos-percent");
      if (!string.IsNullOrEmpty(percentText) && int.TryParse(percentText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
      {
        firstOosPercent = percent;
      }

      return new WLWalkForwardOptions
      {
        Start = start,
        EndExclusive = endExclusive,
        InSampleDays = Config.GetInt("wl-walk-forward-in-sample-days", 31),
        OutOfSampleDays = Config.GetInt("wl-walk-forward-out-of-sample-days", 14),
        StrideDays = Config.GetInt("wl-walk-forward-stride-days", 14),
        Mode = mode,
        FirstOutOfSamplePercent = firstOosPercent
      };
    }
  }
}