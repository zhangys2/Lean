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
using System.Collections;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.WLStyle
{
    /// <summary>
    /// An index-addressable, dynamically growing numeric series aligned to one <see cref="BarHistory"/>.
    /// </summary>
    public sealed class TimeSeries : IReadOnlyList<double>
    {
        private readonly Func<int, double> _valueAt;

        internal TimeSeries(BarHistory history, string name, Func<int, double> valueAt)
        {
            History = history;
            Name = name;
            _valueAt = valueAt;
        }

        internal BarHistory History { get; }

        /// <summary>
        /// Gets the series name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the number of values currently available in the associated history.
        /// </summary>
        public int Count => History.Count;

        /// <summary>
        /// Gets the value at the specified bar index.
        /// </summary>
        public double this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }
                return _valueAt(index);
            }
        }

        /// <summary>
        /// Returns the current values as a new array.
        /// </summary>
        public double[] ToArray()
        {
            var values = new double[Count];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = this[index];
            }
            return values;
        }

        /// <summary>
        /// Returns true when this series moves from at or below another series to strictly above it.
        /// </summary>
        public bool CrossesOver(TimeSeries other, int index)
        {
            ArgumentNullException.ThrowIfNull(other);
            EnsureAligned(other);
            ValidateCurrentIndex(index);
            if (index == 0)
            {
                return false;
            }

            var previous = this[index - 1];
            var previousOther = other[index - 1];
            var current = this[index];
            var currentOther = other[index];
            return double.IsFinite(previous)
                && double.IsFinite(previousOther)
                && double.IsFinite(current)
                && double.IsFinite(currentOther)
                && previous <= previousOther
                && current > currentOther;
        }

        /// <summary>
        /// Returns true when this series moves from at or below a threshold to strictly above it.
        /// </summary>
        public bool CrossesOver(double threshold, int index)
        {
            ValidateCurrentIndex(index);
            return index > 0
                && double.IsFinite(this[index - 1])
                && double.IsFinite(this[index])
                && this[index - 1] <= threshold
                && this[index] > threshold;
        }

        /// <summary>
        /// Returns true when this series moves from at or above another series to strictly below it.
        /// </summary>
        public bool CrossesUnder(TimeSeries other, int index)
        {
            ArgumentNullException.ThrowIfNull(other);
            EnsureAligned(other);
            ValidateCurrentIndex(index);
            if (index == 0)
            {
                return false;
            }

            var previous = this[index - 1];
            var previousOther = other[index - 1];
            var current = this[index];
            var currentOther = other[index];
            return double.IsFinite(previous)
                && double.IsFinite(previousOther)
                && double.IsFinite(current)
                && double.IsFinite(currentOther)
                && previous >= previousOther
                && current < currentOther;
        }

        /// <summary>
        /// Returns true when this series moves from at or above a threshold to strictly below it.
        /// </summary>
        public bool CrossesUnder(double threshold, int index)
        {
            ValidateCurrentIndex(index);
            return index > 0
                && double.IsFinite(this[index - 1])
                && double.IsFinite(this[index])
                && this[index - 1] >= threshold
                && this[index] < threshold;
        }

        /// <summary>
        /// Returns true when the preceding slope was non-positive and the current slope is positive.
        /// </summary>
        public bool TurnsUp(int index)
        {
            ValidateCurrentIndex(index);
            if (index < 2)
            {
                return false;
            }

            var beforePrevious = this[index - 2];
            var previous = this[index - 1];
            var current = this[index];
            return double.IsFinite(beforePrevious)
                && double.IsFinite(previous)
                && double.IsFinite(current)
                && previous <= beforePrevious
                && current > previous;
        }

        /// <summary>
        /// Returns true when the preceding slope was non-negative and the current slope is negative.
        /// </summary>
        public bool TurnsDown(int index)
        {
            ValidateCurrentIndex(index);
            if (index < 2)
            {
                return false;
            }

            var beforePrevious = this[index - 2];
            var previous = this[index - 1];
            var current = this[index];
            return double.IsFinite(beforePrevious)
                && double.IsFinite(previous)
                && double.IsFinite(current)
                && previous >= beforePrevious
                && current < previous;
        }

        /// <summary>
        /// Adds two series aligned to the same symbol history.
        /// </summary>
        public static TimeSeries operator +(TimeSeries left, TimeSeries right)
        {
            EnsureAligned(left, right);
            return new TimeSeries(left.History, $"{left.Name}+{right.Name}", index => left[index] + right[index]);
        }

        /// <summary>
        /// Subtracts two series aligned to the same symbol history.
        /// </summary>
        public static TimeSeries operator -(TimeSeries left, TimeSeries right)
        {
            EnsureAligned(left, right);
            return new TimeSeries(left.History, $"{left.Name}-{right.Name}", index => left[index] - right[index]);
        }

        /// <summary>
        /// Multiplies two series aligned to the same symbol history.
        /// </summary>
        public static TimeSeries operator *(TimeSeries left, TimeSeries right)
        {
            EnsureAligned(left, right);
            return new TimeSeries(left.History, $"{left.Name}*{right.Name}", index => left[index] * right[index]);
        }

        /// <summary>
        /// Divides two series aligned to the same symbol history.
        /// </summary>
        public static TimeSeries operator /(TimeSeries left, TimeSeries right)
        {
            EnsureAligned(left, right);
            return new TimeSeries(left.History, $"{left.Name}/{right.Name}", index => left[index] / right[index]);
        }

        /// <summary>
        /// Adds a constant to every current and future value.
        /// </summary>
        public static TimeSeries operator +(TimeSeries series, double value)
        {
            ArgumentNullException.ThrowIfNull(series);
            return new TimeSeries(series.History, $"{series.Name}+{value}", index => series[index] + value);
        }

        /// <summary>
        /// Adds a constant to every current and future value.
        /// </summary>
        public static TimeSeries operator +(double value, TimeSeries series)
        {
            return series + value;
        }

        /// <summary>
        /// Subtracts a constant from every current and future value.
        /// </summary>
        public static TimeSeries operator -(TimeSeries series, double value)
        {
            ArgumentNullException.ThrowIfNull(series);
            return new TimeSeries(series.History, $"{series.Name}-{value}", index => series[index] - value);
        }

        /// <summary>
        /// Multiplies every current and future value by a constant.
        /// </summary>
        public static TimeSeries operator *(TimeSeries series, double value)
        {
            ArgumentNullException.ThrowIfNull(series);
            return new TimeSeries(series.History, $"{series.Name}*{value}", index => series[index] * value);
        }

        /// <summary>
        /// Multiplies every current and future value by a constant.
        /// </summary>
        public static TimeSeries operator *(double value, TimeSeries series)
        {
            return series * value;
        }

        /// <summary>
        /// Divides every current and future value by a constant.
        /// </summary>
        public static TimeSeries operator /(TimeSeries series, double value)
        {
            ArgumentNullException.ThrowIfNull(series);
            return new TimeSeries(series.History, $"{series.Name}/{value}", index => series[index] / value);
        }

        /// <summary>
        /// Adds another aligned series.
        /// </summary>
        public TimeSeries Add(TimeSeries other)
        {
            return this + other;
        }

        /// <summary>
        /// Adds a constant.
        /// </summary>
        public TimeSeries Add(double value)
        {
            return this + value;
        }

        /// <summary>
        /// Subtracts another aligned series.
        /// </summary>
        public TimeSeries Subtract(TimeSeries other)
        {
            return this - other;
        }

        /// <summary>
        /// Subtracts a constant.
        /// </summary>
        public TimeSeries Subtract(double value)
        {
            return this - value;
        }

        /// <summary>
        /// Multiplies by another aligned series.
        /// </summary>
        public TimeSeries Multiply(TimeSeries other)
        {
            return this * other;
        }

        /// <summary>
        /// Multiplies by a constant.
        /// </summary>
        public TimeSeries Multiply(double value)
        {
            return this * value;
        }

        /// <summary>
        /// Divides by another aligned series.
        /// </summary>
        public TimeSeries Divide(TimeSeries other)
        {
            return this / other;
        }

        /// <summary>
        /// Divides by a constant.
        /// </summary>
        public TimeSeries Divide(double value)
        {
            return this / value;
        }

        /// <inheritdoc />
        public IEnumerator<double> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        private void ValidateCurrentIndex(int index)
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        private void EnsureAligned(TimeSeries other)
        {
            EnsureAligned(this, other);
        }

        private static void EnsureAligned(TimeSeries left, TimeSeries right)
        {
            ArgumentNullException.ThrowIfNull(left);
            ArgumentNullException.ThrowIfNull(right);
            if (!ReferenceEquals(left.History, right.History))
            {
                throw new ArgumentException("TimeSeries operands must belong to the same BarHistory.");
            }
        }
    }
}
