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
using System.Globalization;

namespace QuantConnect.Algorithm.WLStyle
{
    public abstract partial class WLStrategyBase
    {
        internal const string WalkForwardStartParameter = "wl-wf-start";
        internal const string WalkForwardEndParameter = "wl-wf-end";

        private bool _walkForwardDatesApplied;

        /// <summary>
        /// Applies reserved walk-forward start/end overrides supplied per optimization or OOS run.
        /// Half-open calendar <c>[start, end)</c> values are converted to Lean's inclusive end (<c>end - 1 day</c>).
        /// </summary>
        protected void UseWLWalkForwardDates()
        {
            if (_walkForwardDatesApplied)
            {
                return;
            }

            var startText = GetParameter(WalkForwardStartParameter, string.Empty);
            var endExclusiveText = GetParameter(WalkForwardEndParameter, string.Empty);
            if (string.IsNullOrWhiteSpace(startText) || string.IsNullOrWhiteSpace(endExclusiveText))
            {
                return;
            }

            if (!TryParseWalkForwardDate(startText, out var start)
                || !TryParseWalkForwardDate(endExclusiveText, out var endExclusive))
            {
                throw new InvalidOperationException("Walk-forward override dates must use yyyyMMdd.");
            }

            if (endExclusive <= start)
            {
                throw new InvalidOperationException("Walk-forward override end must be after the start.");
            }

            var leanEnd = endExclusive.AddDays(-1);
            SetStartDate(start);
            SetEndDate(leanEnd);

            // SetEndDate stores the last tick of the inclusive calendar day, not midnight.
            if (StartDate.Date != start.Date || EndDate.Date != leanEnd.Date)
            {
                throw new InvalidOperationException(
                    $"Walk-forward overrides ({start:yyyy-MM-dd} to {leanEnd:yyyy-MM-dd}) did not match algorithm dates ({StartDate:yyyy-MM-dd} to {EndDate:yyyy-MM-dd}).");
            }

            _walkForwardDatesApplied = true;
        }

        private static bool TryParseWalkForwardDate(string text, out DateTime date)
        {
            if (DateTime.TryParseExact(text.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                return true;
            }

            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }
    }
}