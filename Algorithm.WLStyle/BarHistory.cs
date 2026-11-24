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
using System.Collections.ObjectModel;
using QuantConnect.Data.Market;

namespace QuantConnect.Algorithm.WLStyle
{
    /// <summary>
    /// Per-symbol TradeBar history delivered to a <see cref="WLStrategyBase"/>.
    /// </summary>
    public sealed class BarHistory
    {
        private readonly List<TradeBar> _bars = new();
        private readonly List<DateTime> _dateTimes = new();
        private readonly List<bool> _fillForward = new();
        private readonly List<IndicatorSeriesBuffer> _indicatorSeries = new();
        private readonly ReadOnlyCollection<DateTime> _dateTimesView;
        private readonly ReadOnlyCollection<bool> _fillForwardView;

        internal BarHistory(Symbol symbol)
        {
            Symbol = symbol;
            _dateTimesView = _dateTimes.AsReadOnly();
            _fillForwardView = _fillForward.AsReadOnly();
            Open = CreatePriceSeries(nameof(Open), bar => (double)bar.Open);
            High = CreatePriceSeries(nameof(High), bar => (double)bar.High);
            Low = CreatePriceSeries(nameof(Low), bar => (double)bar.Low);
            Close = CreatePriceSeries(nameof(Close), bar => (double)bar.Close);
            Volume = CreatePriceSeries(nameof(Volume), bar => (double)bar.Volume);
        }

        /// <summary>
        /// Gets the symbol for this history.
        /// </summary>
        public Symbol Symbol { get; }

        /// <summary>
        /// Gets the number of TradeBars delivered for this symbol.
        /// </summary>
        public int Count => _bars.Count;

        /// <summary>
        /// Gets the end time of each delivered TradeBar.
        /// </summary>
        public IReadOnlyList<DateTime> DateTimes => _dateTimesView;

        /// <summary>
        /// Gets whether each delivered TradeBar is a Lean fill-forward bar.
        /// </summary>
        public IReadOnlyList<bool> FillForward => _fillForwardView;

        /// <summary>
        /// Gets the open prices as an index-addressable series.
        /// </summary>
        public TimeSeries Open { get; }

        /// <summary>
        /// Gets the high prices as an index-addressable series.
        /// </summary>
        public TimeSeries High { get; }

        /// <summary>
        /// Gets the low prices as an index-addressable series.
        /// </summary>
        public TimeSeries Low { get; }

        /// <summary>
        /// Gets the close prices as an index-addressable series.
        /// </summary>
        public TimeSeries Close { get; }

        /// <summary>
        /// Gets the volumes as an index-addressable series.
        /// </summary>
        public TimeSeries Volume { get; }

        internal void Add(TradeBar bar)
        {
            if (bar.Symbol != Symbol)
            {
                throw new ArgumentException($"TradeBar symbol {bar.Symbol} does not match history symbol {Symbol}.", nameof(bar));
            }
            if (_dateTimes.Count > 0 && bar.EndTime <= _dateTimes[_dateTimes.Count - 1])
            {
                throw new ArgumentException("TradeBars must be appended in strictly increasing end-time order.", nameof(bar));
            }

            var index = _bars.Count;
            _bars.Add(bar);
            _dateTimes.Add(bar.EndTime);
            _fillForward.Add(bar.IsFillForward);
            foreach (var series in _indicatorSeries)
            {
                series.Add(bar, index);
            }
        }

        internal TimeSeries[] AddIndicatorSeries(
            Func<TradeBar, int, bool> update,
            Func<bool> isReady,
            string[] outputNames,
            params Func<double>[] valueSelectors)
        {
            ArgumentNullException.ThrowIfNull(outputNames);
            if (outputNames.Length != valueSelectors.Length)
            {
                throw new ArgumentException("Each indicator output must have a name.", nameof(outputNames));
            }
            var buffer = new IndicatorSeriesBuffer(update, isReady, valueSelectors);
            for (var index = 0; index < _bars.Count; index++)
            {
                buffer.Add(_bars[index], index);
            }
            _indicatorSeries.Add(buffer);

            var series = new TimeSeries[valueSelectors.Length];
            for (var selectorIndex = 0; selectorIndex < series.Length; selectorIndex++)
            {
                var capturedIndex = selectorIndex;
                series[selectorIndex] = new TimeSeries(
                    this,
                    outputNames[selectorIndex],
                    index => buffer.Values[capturedIndex][index]);
            }
            return series;
        }

        private TimeSeries CreatePriceSeries(string name, Func<TradeBar, double> selector)
        {
            return new TimeSeries(this, $"{Symbol.Value}.{name}", index => selector(_bars[index]));
        }

        private sealed class IndicatorSeriesBuffer
        {
            private readonly Func<TradeBar, int, bool> _update;
            private readonly Func<bool> _isReady;
            private readonly Func<double>[] _valueSelectors;

            public IndicatorSeriesBuffer(
                Func<TradeBar, int, bool> update,
                Func<bool> isReady,
                Func<double>[] valueSelectors)
            {
                _update = update;
                _isReady = isReady;
                _valueSelectors = valueSelectors;
                Values = new List<double>[valueSelectors.Length];
                for (var index = 0; index < Values.Length; index++)
                {
                    Values[index] = new List<double>();
                }
            }

            public List<double>[] Values { get; }

            public void Add(TradeBar bar, int index)
            {
                var validInput = _update(bar, index);
                var ready = validInput && _isReady();
                for (var valueIndex = 0; valueIndex < _valueSelectors.Length; valueIndex++)
                {
                    Values[valueIndex].Add(ready ? _valueSelectors[valueIndex]() : double.NaN);
                }
            }
        }
    }
}
