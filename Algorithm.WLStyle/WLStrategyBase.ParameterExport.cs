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
using System.Linq;

namespace QuantConnect.Algorithm.WLStyle
{
    public abstract partial class WLStrategyBase
    {
        /// <summary>
        /// Exports the ranges discovered by this completed discovery backtest.
        /// Call from <see cref="BacktestComplete"/> after all symbols have registered parameters.
        /// </summary>
        /// <param name="path">Destination for the standalone optimizer config fragment.</param>
        /// <param name="options">Grid budget and objective settings.</param>
        protected void ExportWLParameters(string path, WLOptimizationExportOptions options)
        {
            var ranges = GetWLParameterRanges()
                .Select(range => new WLParameterSpec(range.Name, range.Type, range.Default, range.Min, range.Max, range.Step))
                .ToArray();
            WLParameterExport.Write(path, ranges, options);
        }
    }
}
