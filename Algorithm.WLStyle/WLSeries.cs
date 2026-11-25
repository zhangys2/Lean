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
using QuantConnect.Data.Market;
using QuantConnect.Indicators;

namespace QuantConnect.Algorithm.WLStyle
{
    /// <summary>
    /// Creates indicator series backed by Lean's streaming implementations.
    /// </summary>
    public static class WLSeries
    {
        /// <summary>
        /// Creates a simple moving average over a series.
        /// </summary>
        public static TimeSeries SMA(TimeSeries source, int period)
        {
            ValidateSourceAndPeriod(source, period);
            var indicator = new SimpleMovingAverage(IndicatorName(source, "SMA", period), period);
            var outputs = source.History.AddIndicatorSeries(
                UpdateFromSource(source, indicator),
                () => indicator.IsReady,
                new[] { indicator.Name },
                () => (double)indicator.Current.Value);
            return outputs[0];
        }

        /// <summary>
        /// Creates an exponential moving average over a series.
        /// </summary>
        public static TimeSeries EMA(TimeSeries source, int period)
        {
            ValidateSourceAndPeriod(source, period);
            var indicator = new ExponentialMovingAverage(IndicatorName(source, "EMA", period), period);
            var outputs = source.History.AddIndicatorSeries(
                UpdateFromSource(source, indicator),
                () => indicator.IsReady,
                new[] { indicator.Name },
                () => (double)indicator.Current.Value);
            return outputs[0];
        }

        /// <summary>
        /// Creates a relative strength index over a series.
        /// </summary>
        public static TimeSeries RSI(
            TimeSeries source,
            int period,
            MovingAverageType movingAverageType = MovingAverageType.Wilders)
        {
            ValidateSourceAndPeriod(source, period);
            var indicator = new RelativeStrengthIndex(
                IndicatorName(source, "RSI", period),
                period,
                movingAverageType);
            var outputs = source.History.AddIndicatorSeries(
                UpdateFromSource(source, indicator),
                () => indicator.IsReady,
                new[] { indicator.Name },
                () => (double)indicator.Current.Value);
            return outputs[0];
        }

        /// <summary>
        /// Creates an average true range over a bar history.
        /// </summary>
        public static TimeSeries ATR(
            BarHistory bars,
            int period,
            MovingAverageType movingAverageType = MovingAverageType.Wilders)
        {
            ArgumentNullException.ThrowIfNull(bars);
            ValidatePeriod(period);
            var indicator = new AverageTrueRange($"{bars.Symbol.Value}.ATR({period})", period, movingAverageType);
            var outputs = bars.AddIndicatorSeries(
                (bar, _) =>
                {
                    indicator.Update(bar);
                    return true;
                },
                () => indicator.IsReady,
                new[] { indicator.Name },
                () => (double)indicator.Current.Value);
            return outputs[0];
        }

        /// <summary>
        /// Creates the upper, middle, lower, bandwidth, and percent-B series for Lean's Bollinger Bands indicator.
        /// </summary>
        public static BollingerBandSeries BollingerBands(
            TimeSeries source,
            int period,
            decimal standardDeviations = 2m,
            MovingAverageType movingAverageType = MovingAverageType.Simple)
        {
            ValidateSourceAndPeriod(source, period);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(standardDeviations);

            var indicator = new QuantConnect.Indicators.BollingerBands(
                IndicatorName(source, "BB", period),
                period,
                standardDeviations,
                movingAverageType);
            var outputs = source.History.AddIndicatorSeries(
                UpdateFromSource(source, indicator),
                () => indicator.IsReady,
                new[]
                {
                    indicator.UpperBand.Name,
                    indicator.MiddleBand.Name,
                    indicator.LowerBand.Name,
                    indicator.BandWidth.Name,
                    indicator.PercentB.Name
                },
                () => (double)indicator.UpperBand.Current.Value,
                () => (double)indicator.MiddleBand.Current.Value,
                () => (double)indicator.LowerBand.Current.Value,
                () => (double)indicator.BandWidth.Current.Value,
                () => (double)indicator.PercentB.Current.Value);
            return new BollingerBandSeries(outputs);
        }

        /// <summary>
        /// Creates MACD, signal, and histogram series over a price series.
        /// </summary>
        public static MacdSeries MACD(
            TimeSeries source,
            int fastPeriod,
            int slowPeriod,
            int signalPeriod,
            MovingAverageType movingAverageType = MovingAverageType.Exponential)
        {
            ValidateSourceAndPeriod(source, fastPeriod);
            ValidatePeriod(slowPeriod);
            ValidatePeriod(signalPeriod);
            var indicator = new MovingAverageConvergenceDivergence(
                IndicatorName(source, "MACD", fastPeriod, slowPeriod, signalPeriod),
                fastPeriod,
                slowPeriod,
                signalPeriod,
                movingAverageType);
            var outputs = source.History.AddIndicatorSeries(
                UpdateFromSource(source, indicator),
                () => indicator.IsReady,
                new[] { indicator.Name, indicator.Signal.Name, indicator.Histogram.Name },
                () => (double)indicator.Current.Value,
                () => (double)indicator.Signal.Current.Value,
                () => (double)indicator.Histogram.Current.Value);
            return new MacdSeries(outputs);
        }

        /// <summary>
        /// Creates ADX, positive directional index, and negative directional index series.
        /// </summary>
        public static AdxSeries ADX(BarHistory bars, int period)
        {
            ArgumentNullException.ThrowIfNull(bars);
            ValidatePeriod(period);
            var indicator = new AverageDirectionalIndex($"{bars.Symbol.Value}.ADX({period})", period);
            var outputs = bars.AddIndicatorSeries(
                (bar, _) =>
                {
                    indicator.Update(bar);
                    return true;
                },
                () => indicator.IsReady,
                new[]
                {
                    indicator.Name,
                    indicator.PositiveDirectionalIndex.Name,
                    indicator.NegativeDirectionalIndex.Name
                },
                () => (double)indicator.Current.Value,
                () => (double)indicator.PositiveDirectionalIndex.Current.Value,
                () => (double)indicator.NegativeDirectionalIndex.Current.Value);
            return new AdxSeries(outputs);
        }

        /// <summary>
        /// Creates fast %K, slow %K, and slow %D stochastic series.
        /// </summary>
        public static StochasticSeries Stochastic(BarHistory bars, int period, int kPeriod, int dPeriod)
        {
            ArgumentNullException.ThrowIfNull(bars);
            ValidatePeriod(period);
            ValidatePeriod(kPeriod);
            ValidatePeriod(dPeriod);
            var indicator = new Stochastic(
                $"{bars.Symbol.Value}.STO({period},{kPeriod},{dPeriod})",
                period,
                kPeriod,
                dPeriod);
            var outputs = bars.AddIndicatorSeries(
                (bar, _) =>
                {
                    indicator.Update(bar);
                    return true;
                },
                () => indicator.IsReady,
                new[] { indicator.FastStoch.Name, indicator.StochK.Name, indicator.StochD.Name },
                () => (double)indicator.FastStoch.Current.Value,
                () => (double)indicator.StochK.Current.Value,
                () => (double)indicator.StochD.Current.Value);
            return new StochasticSeries(outputs);
        }

        private static Func<TradeBar, int, bool> UpdateFromSource(
            TimeSeries source,
            IndicatorBase<IndicatorDataPoint> indicator)
        {
            return (bar, index) =>
            {
                var value = source[index];
                if (!double.IsFinite(value))
                {
                    return false;
                }
                indicator.Update(new IndicatorDataPoint(
                    source.History.Symbol,
                    bar.EndTime,
                    Convert.ToDecimal(value)));
                return true;
            };
        }

        private static string IndicatorName(TimeSeries source, string type, int period)
        {
            return $"{source.Name}.{type}({period})";
        }

        private static string IndicatorName(TimeSeries source, string type, int firstPeriod, int secondPeriod, int thirdPeriod)
        {
            return $"{source.Name}.{type}({firstPeriod},{secondPeriod},{thirdPeriod})";
        }

        private static void ValidateSourceAndPeriod(TimeSeries source, int period)
        {
            ArgumentNullException.ThrowIfNull(source);
            ValidatePeriod(period);
        }

        private static void ValidatePeriod(int period)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        }
    }

    /// <summary>
    /// The component series produced by Lean's Bollinger Bands indicator.
    /// </summary>
    public sealed class BollingerBandSeries
    {
        internal BollingerBandSeries(TimeSeries[] series)
        {
            Upper = series[0];
            Middle = series[1];
            Lower = series[2];
            Bandwidth = series[3];
            PercentB = series[4];
        }

        /// <summary>Gets the upper band.</summary>
        public TimeSeries Upper { get; }

        /// <summary>Gets the middle band.</summary>
        public TimeSeries Middle { get; }

        /// <summary>Gets the lower band.</summary>
        public TimeSeries Lower { get; }

        /// <summary>Gets the band width.</summary>
        public TimeSeries Bandwidth { get; }

        /// <summary>Gets percent B.</summary>
        public TimeSeries PercentB { get; }
    }

    /// <summary>
    /// The MACD value, signal, and histogram series.
    /// </summary>
    public sealed class MacdSeries
    {
        internal MacdSeries(TimeSeries[] series)
        {
            Macd = series[0];
            Signal = series[1];
            Histogram = series[2];
        }

        /// <summary>Gets the MACD value.</summary>
        public TimeSeries Macd { get; }

        /// <summary>Gets the signal value.</summary>
        public TimeSeries Signal { get; }

        /// <summary>Gets the histogram value.</summary>
        public TimeSeries Histogram { get; }
    }

    /// <summary>
    /// The ADX value and directional index series.
    /// </summary>
    public sealed class AdxSeries
    {
        internal AdxSeries(TimeSeries[] series)
        {
            Value = series[0];
            PositiveDirectionalIndex = series[1];
            NegativeDirectionalIndex = series[2];
        }

        /// <summary>Gets the ADX value.</summary>
        public TimeSeries Value { get; }

        /// <summary>Gets the positive directional index.</summary>
        public TimeSeries PositiveDirectionalIndex { get; }

        /// <summary>Gets the negative directional index.</summary>
        public TimeSeries NegativeDirectionalIndex { get; }
    }

    /// <summary>
    /// The fast %K, slow %K, and slow %D stochastic series.
    /// </summary>
    public sealed class StochasticSeries
    {
        internal StochasticSeries(TimeSeries[] series)
        {
            FastK = series[0];
            K = series[1];
            D = series[2];
        }

        /// <summary>Gets the fast %K value.</summary>
        public TimeSeries FastK { get; }

        /// <summary>Gets the slow %K value.</summary>
        public TimeSeries K { get; }

        /// <summary>Gets the slow %D value.</summary>
        public TimeSeries D { get; }
    }
}
