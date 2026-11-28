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
using System.Linq;
using QuantConnect.Algorithm.WLStyle;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Algorithm.CSharp.WLStyle
{
    /// <summary>
    /// Long-only Bollinger squeeze breakout. The rules are reimplemented from the published
    /// source cited in the header comment; nothing was copied from WealthLab.
    /// </summary>
    public static class BollingerSqueezeSignal
    {
        public const int BandPeriod = 20;
        public const decimal BandDeviations = 2m;
        public const int AtrPeriod = 14;
        public const int SqueezeWindow = 126;
        public const decimal LimitAtrMultiple = 0.5m;

        public static double BandWidth(double upper, double middle, double lower)
        {
            if (!double.IsFinite(upper) || !double.IsFinite(middle) || !double.IsFinite(lower) || middle == 0d)
            {
                return double.NaN;
            }

            return (upper - lower) / middle;
        }

        public static bool IsInsideBands(double close, double lower, double upper)
        {
            return double.IsFinite(close)
                && double.IsFinite(lower)
                && double.IsFinite(upper)
                && close > lower
                && close < upper;
        }

        /// <summary>
        /// The current value is a minimum of the inclusive window when it is less than or equal to
        /// every earlier finite value in that window. An equal earlier value is still a minimum.
        /// </summary>
        public static bool IsRollingMinimum(IReadOnlyList<double> bandWidth, int index, int window)
        {
            if (bandWidth == null || window <= 0 || index < window - 1 || index >= bandWidth.Count)
            {
                return false;
            }

            var current = bandWidth[index];
            if (!double.IsFinite(current))
            {
                return false;
            }

            var start = index - window + 1;
            for (var i = start; i < index; i++)
            {
                var earlier = bandWidth[i];
                if (!double.IsFinite(earlier) || earlier < current)
                {
                    return false;
                }
            }

            return true;
        }

        public static bool IsSetup(
            IReadOnlyList<double> bandWidth,
            int index,
            double close,
            double lower,
            double upper)
        {
            return IsRollingMinimum(bandWidth, index, SqueezeWindow)
                && IsInsideBands(close, lower, upper);
        }

        public static (decimal Stop, decimal Limit) BuyStopLimitPrices(
            decimal high,
            decimal tick,
            decimal averageTrueRange)
        {
            if (tick <= 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(tick), "The price tick must be positive.");
            }

            if (averageTrueRange < 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(averageTrueRange), "ATR cannot be negative.");
            }

            var stop = high + tick;
            var limit = stop + LimitAtrMultiple * averageTrueRange;
            return (stop, limit);
        }
    }

    /// <summary>
    /// Daily SPY Bollinger squeeze: buy one share on a stop-limit above a 126-session bandwidth low,
    /// and sell after a later close below the middle band.
    /// </summary>
    // Rules-only reimplementation of John Bollinger, Bollinger on Bollinger Bands (2001),
    // and the public Bollinger Band Rules, rules 6-9 and 19:
    // https://www.bollingerbands.com/bollinger-band-rules
    // The 126-session window, stop, limit, and middle-band exit are the candidate's testable
    // adaptation of those principles. No WealthLab code or assets were copied.
    public sealed class BollingerSqueezeWLStyle : WLStrategyBase, IRegressionAlgorithmDefinition
    {
        private const string OrderTagPrefix = "BB squeeze";
        private readonly List<double> _bandWidth = new();
        private Symbol _spy;
        private BollingerBandSeries _bands;
        private TimeSeries _atr;
        private bool _filledOnThisBar;
        private bool _entryPending;
        private DateTime _entrySignalEnd;
        private decimal _entryStop;
        private decimal _entryLimit;
        private int _filledEntries;
        private int _untouchedExpiries;
        private int _triggeredUnfilled;

        public bool CanRunLocally => true;

        public List<Language> Languages => new() { Language.CSharp };

        public long DataPoints => 42956;

        public int AlgorithmHistoryDataPoints => 0;

        public AlgorithmStatus AlgorithmStatus => AlgorithmStatus.Completed;

        public Dictionary<string, string> ExpectedStatistics => Pin(
            ("Total Orders", "115"),
            ("Average Win", "0.00%"),
            ("Average Loss", "0.00%"),
            ("Compounding Annual Return", "-0.006%"),
            ("Drawdown", "0.100%"),
            ("Expectancy", "-0.896"),
            ("Start Equity", "100000"),
            ("End Equity", "99879.80"),
            ("Net Profit", "-0.120%"),
            ("Sharpe Ratio", "-477.536"),
            ("Sortino Ratio", "-138.794"),
            ("Probabilistic Sharpe Ratio", "0%"),
            ("Loss Rate", "92%"),
            ("Win Rate", "8%"),
            ("Profit-Loss Ratio", "0.28"),
            ("Alpha", "-0.02"),
            ("Beta", "0"),
            ("Annual Standard Deviation", "0"),
            ("Annual Variance", "0"),
            ("Information Ratio", "-0.373"),
            ("Tracking Error", "0.162"),
            ("Treynor Ratio", "-776.17"),
            ("Total Fees", "$73.31"),
            ("Estimated Strategy Capacity", "$130000000000.00"),
            ("Lowest Capacity Asset", "SPY R735QTJ8XC9X"),
            ("Portfolio Turnover", "0.00%"),
            ("Drawdown Recovery", "0"),
            ("OrderListHash", "4797c863ab1791045a4b34a029b3ff78"));

        public override void Initialize()
        {
            SetStartDate(2000, 1, 3);
            SetEndDate(2021, 3, 31);
            SetCash(100000);
            SetWarmUp(BollingerSqueezeSignal.SqueezeWindow, Resolution.Daily);
            _spy = AddEquity(
                "SPY",
                Resolution.Daily,
                dataNormalizationMode: DataNormalizationMode.Raw).Symbol;
        }

        protected override void Initialize(BarHistory bars)
        {
            if (bars.Symbol != _spy)
            {
                return;
            }

            _bands = WLSeries.BollingerBands(
                bars.Close,
                BollingerSqueezeSignal.BandPeriod,
                BollingerSqueezeSignal.BandDeviations);
            _atr = WLSeries.ATR(bars, BollingerSqueezeSignal.AtrPeriod);
        }

        protected override void Execute(BarHistory bars, int idx)
        {
            if (bars.Symbol != _spy || _bands == null)
            {
                return;
            }

            var upper = _bands.Upper[idx];
            var middle = _bands.Middle[idx];
            var lower = _bands.Lower[idx];
            _bandWidth.Add(BollingerSqueezeSignal.BandWidth(upper, middle, lower));
            Ensure(_bandWidth.Count == idx + 1, "Bandwidth history is not aligned with bars.");

            if (IsWarmingUp || bars.FillForward[idx])
            {
                return;
            }

            ResolveEntry(bars, idx);
            var close = bars.Close[idx];
            if (HasOpenPosition(bars, PositionType.Long))
            {
                if (!_filledOnThisBar
                    && double.IsFinite(close)
                    && double.IsFinite(middle)
                    && close < middle
                    && !HasOpenStrategyOrder())
                {
                    ClosePosition(bars, $"{OrderTagPrefix} exit {SessionDate(bars, idx):yyyyMMdd}");
                }

                _filledOnThisBar = false;
                return;
            }

            _filledOnThisBar = false;
            if (HasOpenStrategyOrder()
                || !BollingerSqueezeSignal.IsSetup(_bandWidth, idx, close, lower, upper))
            {
                return;
            }

            var atr = _atr[idx];
            if (!double.IsFinite(atr))
            {
                return;
            }

            var security = Securities[bars.Symbol];
            var tradeBar = security.Cache.GetData<TradeBar>();
            Ensure(tradeBar != null && tradeBar.EndTime == bars.DateTimes[idx],
                "The cached trade bar is not the signal bar.");
            var tick = security.PriceVariationModel.GetMinimumPriceVariation(
                new GetMinimumPriceVariationParameters(security, tradeBar.High));
            var (stop, limit) = BollingerSqueezeSignal.BuyStopLimitPrices(tradeBar.High, tick, (decimal)atr);
            var sessionDate = SessionDate(bars, idx);
            Log(FormattableString.Invariant(
                $"BB-SQUEEZE setup {sessionDate:yyyy-MM-dd} high={tradeBar.High} tick={tick} stop={stop} limit={limit} atr={atr:R} close={tradeBar.Close} upper={upper:R} middle={middle:R} lower={lower:R} bw={_bandWidth[idx]:R}"));
            PlaceTrade(
                bars,
                TransactionType.Buy,
                OrderType.StopLimit,
                1m,
                limit,
                $"{OrderTagPrefix} entry {sessionDate:yyyyMMdd}",
                stop);
            _entryPending = true;
            _entrySignalEnd = bars.DateTimes[idx];
            _entryStop = stop;
            _entryLimit = limit;
        }

        private void ResolveEntry(BarHistory bars, int idx)
        {
            if (!_entryPending || bars.DateTimes[idx] <= _entrySignalEnd)
            {
                return;
            }

            var tradeBar = Securities[bars.Symbol].Cache.GetData<TradeBar>();
            Ensure(tradeBar != null && tradeBar.EndTime == bars.DateTimes[idx],
                "The cached trade bar is not the entry's eligible bar.");
            _entryPending = false;
            if (_filledOnThisBar)
            {
                return;
            }

            if (tradeBar.High > _entryStop)
            {
                Ensure(tradeBar.Close >= _entryLimit,
                    "The stop traded through its limit without a fill.");
                _triggeredUnfilled++;
                return;
            }

            _untouchedExpiries++;
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            var order = Transactions.GetOrderById(orderEvent.OrderId);
            if (order == null || !order.Tag.StartsWith(OrderTagPrefix, StringComparison.Ordinal))
            {
                return;
            }

            Ensure(orderEvent.Status != OrderStatus.Invalid,
                $"Bollinger squeeze order {order.Id} was rejected: {orderEvent.Message}");

            var stopTriggered = order is StopLimitOrder stop && stop.StopTriggered;
            Log(FormattableString.Invariant(
                $"BB-SQUEEZE order {order.Id} {orderEvent.Status} {order.Type} dir={order.Direction} stopTriggered={stopTriggered} fill={orderEvent.FillPrice} qty={orderEvent.FillQuantity} time={orderEvent.UtcTime:yyyy-MM-dd HH:mm:ss} tag={order.Tag}"));

            if (orderEvent.Status != OrderStatus.Filled || order.Direction != OrderDirection.Buy)
            {
                return;
            }

            _filledOnThisBar = true;
            _filledEntries++;
            Ensure(Portfolio[_spy].Quantity > 0, "Buy fill did not open a long position.");
            Ensure(Portfolio[_spy].Quantity == 1, $"Expected a one-share long position, found {Portfolio[_spy].Quantity}.");
        }

        public override void OnEndOfAlgorithm()
        {
            Ensure(_filledEntries is >= 15 and <= 100,
                $"Expected 15-100 filled entries, observed {_filledEntries}.");
            Ensure(_filledEntries == 37 && _untouchedExpiries == 33 && _triggeredUnfilled == 8,
                $"Expected 37 filled entries, 33 untouched expiries, and 8 triggered-but-unfilled stop-limits; observed {_filledEntries}, {_untouchedExpiries}, {_triggeredUnfilled}.");
            Ensure(!_entryPending, "An entry stop-limit was still waiting for its eligible bar.");
            Ensure(Portfolio[_spy].Quantity >= 0, "Final position is short.");
            Log($"BB-SQUEEZE filledEntries={_filledEntries} untouchedExpiries={_untouchedExpiries} triggeredUnfilled={_triggeredUnfilled}");
            base.OnEndOfAlgorithm();
        }

        private static Dictionary<string, string> Pin(params (string Key, string Value)[] statistics)
        {
            var result = new Dictionary<string, string>(statistics.Length);
            foreach (var (key, value) in statistics)
            {
                result.Add(key, value);
            }
            return result;
        }

        private bool HasOpenStrategyOrder()
        {
            return Transactions.GetOpenOrderTickets().Any(ticket =>
                ticket.Symbol == _spy
                && ticket.Tag.StartsWith(OrderTagPrefix, StringComparison.Ordinal));
        }

        private static DateTime SessionDate(BarHistory bars, int idx)
        {
            return bars.DateTimes[idx].AddTicks(-1).Date;
        }

        private static void Ensure(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
