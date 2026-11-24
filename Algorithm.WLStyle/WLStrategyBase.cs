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
using System.Linq;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Orders.Fills;
using QuantConnect.Securities;
using QuantConnect.Securities.Positions;
using QuantConnect.Statistics;

namespace QuantConnect.Algorithm.WLStyle
{
    /// <summary>
    /// Describes the trade side independently of the positive order quantity.
    /// </summary>
    public enum TransactionType
    {
        Buy,
        Sell,
        Short,
        Cover
    }

    /// <summary>
    /// Describes the sign of an open holding.
    /// </summary>
    public enum PositionType
    {
        Long,
        Short
    }

    /// <summary>
    /// Adapts a per-symbol, per-bar strategy lifecycle to Lean's <see cref="QCAlgorithm"/> engine.
    /// </summary>
    public abstract class WLStrategyBase : QCAlgorithm
    {
        private readonly Dictionary<Symbol, BarHistory> _histories = new();
        private readonly Dictionary<string, ParameterDefinition> _parameterDefinitions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, object> _parameterValues = new(StringComparer.Ordinal);
        private readonly Dictionary<Symbol, List<(OrderTicket Ticket, DateTime SubmittedLocalTime)>> _expiringOrders = new();
        private readonly Dictionary<Symbol, WLStyleFillModel> _fillModels = new();
        private bool _backtestBegan;
        private bool _backtestCompleted;

        /// <summary>
        /// Initializes strategy state for a symbol when its first TradeBar is delivered.
        /// The history already contains that first bar.
        /// </summary>
        protected abstract void Initialize(BarHistory bars);

        /// <summary>
        /// Evaluates the current bar for one symbol. <paramref name="idx"/> is its zero-based history index.
        /// </summary>
        protected abstract void Execute(BarHistory bars, int idx);

        /// <summary>
        /// Called once before the first data slice is processed.
        /// </summary>
        protected virtual void BacktestBegin()
        {
        }

        /// <summary>
        /// Called once after the final algorithm event.
        /// </summary>
        protected virtual void BacktestComplete()
        {
        }

        /// <summary>
        /// Called before per-symbol histories are extended with the current slice's bars.
        /// </summary>
        protected virtual void PreExecute(DateTime time, List<BarHistory> bars)
        {
        }

        /// <summary>
        /// Called after all TradeBars in the current slice have been processed.
        /// </summary>
        protected virtual void PostExecute(DateTime time, List<BarHistory> bars)
        {
        }

        /// <summary>
        /// Gets the most recently closed trade for the supplied symbol.
        /// </summary>
        protected Trade LastPosition(BarHistory bars)
        {
            ArgumentNullException.ThrowIfNull(bars);
            return TradeBuilder.ClosedTrades.LastOrDefault(
                trade => trade.Symbols != null && trade.Symbols.Contains(bars.Symbol));
        }

        /// <summary>
        /// Gets the current non-zero holding for the supplied symbol, if any.
        /// </summary>
        protected IReadOnlyList<Position> OpenPositions(BarHistory bars)
        {
            ArgumentNullException.ThrowIfNull(bars);
            var security = Securities[bars.Symbol];
            return security.Holdings.Quantity == 0
                ? Array.Empty<Position>()
                : new[] { new Position(security) };
        }

        /// <summary>
        /// Submits a Lean-native order using a positive quantity and an explicit trade side.
        /// Orders become eligible on the next non-fill-forward TradeBar.
        /// </summary>
        protected OrderTicket PlaceTrade(
            BarHistory bars,
            TransactionType transactionType,
            OrderType orderType,
            decimal quantity,
            decimal price = 0m,
            string signalName = "")
        {
            ArgumentNullException.ThrowIfNull(bars);
            if (quantity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(quantity), "Trade quantity must be positive.");
            }

            var signedQuantity = transactionType switch
            {
                TransactionType.Buy or TransactionType.Cover => quantity,
                TransactionType.Sell or TransactionType.Short => -quantity,
                _ => throw new ArgumentOutOfRangeException(nameof(transactionType))
            };

            // Backtesting scans new orders during submission, so the adapter marker must be
            // carried by the order itself rather than registered after PlaceTrade returns.
            // Use the exchange-local clock, not the symbol's last appended bar: in PreExecute, or when
            // trading another symbol from Execute, Lean's cache already holds a bar the history lacks.
            GetFillModel(bars.Symbol);
            var submittedLocalTime = Securities[bars.Symbol].LocalTime;
            var properties = new AdapterOrderProperties(submittedLocalTime)
            {
                TimeInForce = DefaultOrderProperties?.TimeInForce ?? TimeInForce.GoodTilCanceled
            };
            OrderTicket ticket = orderType switch
            {
                OrderType.Market when price == 0m => MarketOrder(bars.Symbol, signedQuantity, true, signalName, properties),
                OrderType.Limit => LimitOrder(bars.Symbol, signedQuantity, price, tag: signalName, orderProperties: properties),
                OrderType.StopMarket => StopMarketOrder(bars.Symbol, signedQuantity, price, tag: signalName, orderProperties: properties),
                OrderType.Market => throw new ArgumentOutOfRangeException(
                    nameof(price),
                    "Market orders do not accept a price."),
                _ => throw new NotSupportedException($"Order type {orderType} is not supported by the WL-style adapter.")
            };

            if (orderType != OrderType.Market)
            {
                if (!_expiringOrders.TryGetValue(bars.Symbol, out var orders))
                {
                    orders = new List<(OrderTicket, DateTime)>();
                    _expiringOrders.Add(bars.Symbol, orders);
                }
                orders.Add((ticket, submittedLocalTime));
            }
            return ticket;
        }

        /// <summary>
        /// Submits a market order for the actual current holding quantity, if any.
        /// </summary>
        protected OrderTicket ClosePosition(BarHistory bars, string signalName = "")
        {
            ArgumentNullException.ThrowIfNull(bars);
            var quantity = Portfolio[bars.Symbol].Quantity;
            if (quantity == 0)
            {
                return null;
            }

            return PlaceTrade(
                bars,
                quantity > 0 ? TransactionType.Sell : TransactionType.Cover,
                OrderType.Market,
                Math.Abs(quantity),
                signalName: signalName);
        }

        /// <summary>
        /// Returns whether the current Lean portfolio has a holding in the requested direction.
        /// </summary>
        protected bool HasOpenPosition(BarHistory bars, PositionType positionType)
        {
            ArgumentNullException.ThrowIfNull(bars);
            var quantity = Portfolio[bars.Symbol].Quantity;
            return positionType switch
            {
                PositionType.Long => quantity > 0,
                PositionType.Short => quantity < 0,
                _ => throw new ArgumentOutOfRangeException(nameof(positionType))
            };
        }

        /// <summary>
        /// Registers a numeric strategy parameter and returns its Lean-supplied value or the default.
        /// The same name must be registered with the same type and bounds for every symbol.
        /// </summary>
        protected T AddParameter<T>(string name, T defaultValue, T minValue, T maxValue, T step)
            where T : struct, IConvertible
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Parameter name cannot be empty.", nameof(name));
            }
            if (typeof(T) != typeof(int) && typeof(T) != typeof(double) && typeof(T) != typeof(decimal))
            {
                throw new NotSupportedException($"Parameter type {typeof(T).Name} is not a supported numeric type.");
            }

            var definition = new ParameterDefinition(
                typeof(T),
                ToDecimal(defaultValue, name),
                ToDecimal(minValue, name),
                ToDecimal(maxValue, name),
                ToDecimal(step, name));
            if (definition.MinValue > definition.MaxValue)
            {
                throw new ArgumentException($"Parameter '{name}' has a minimum greater than its maximum.");
            }
            if (definition.DefaultValue < definition.MinValue || definition.DefaultValue > definition.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(defaultValue),
                    defaultValue,
                    $"Parameter '{name}' default must be between {minValue} and {maxValue}.");
            }
            if (definition.Step <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(step), $"Parameter '{name}' must have a positive step.");
            }

            if (_parameterDefinitions.TryGetValue(name, out var existing))
            {
                if (existing != definition)
                {
                    throw new InvalidOperationException(
                        $"Parameter '{name}' was registered with different type or bounds for another symbol.");
                }
                return (T)_parameterValues[name];
            }

            var defaultText = Convert.ToString(defaultValue, CultureInfo.InvariantCulture);
            var parameterText = GetParameter(name, defaultText);
            T value;
            try
            {
                value = (T)Convert.ChangeType(parameterText, typeof(T), CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (
                exception is FormatException
                || exception is InvalidCastException
                || exception is OverflowException)
            {
                throw new ArgumentException(
                    $"Parameter '{name}' value '{parameterText}' is not a valid {typeof(T).Name}.",
                    nameof(name),
                    exception);
            }

            var numericValue = ToDecimal(value, name);
            if (numericValue < definition.MinValue || numericValue > definition.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    name,
                    value,
                    $"Parameter '{name}' must be between {minValue} and {maxValue}.");
            }

            _parameterDefinitions.Add(name, definition);
            _parameterValues.Add(name, value);
            return value;
        }

        /// <summary>
        /// Plots the latest value of a time series using Lean's chart and series pipeline.
        /// </summary>
        protected void PlotTimeSeries(
            BarHistory bars,
            string chartName,
            string seriesName,
            TimeSeries series)
        {
            ArgumentNullException.ThrowIfNull(bars);
            ArgumentNullException.ThrowIfNull(series);
            if (string.IsNullOrWhiteSpace(chartName))
            {
                throw new ArgumentException("Chart name cannot be empty.", nameof(chartName));
            }
            if (string.IsNullOrWhiteSpace(seriesName))
            {
                throw new ArgumentException("Series name cannot be empty.", nameof(seriesName));
            }
            if (!ReferenceEquals(bars, series.History))
            {
                throw new ArgumentException("The plotted series must belong to the supplied BarHistory.", nameof(series));
            }
            if (bars.Count == 0)
            {
                return;
            }
            Plot(chartName, seriesName, series[bars.Count - 1]);
        }

        /// <summary>
        /// Processes delivered TradeBars, maintaining one independent history per symbol.
        /// </summary>
        public override void OnData(Slice slice)
        {
            ArgumentNullException.ThrowIfNull(slice);

            if (!_backtestBegan)
            {
                _backtestBegan = true;
                BacktestBegin();
            }

            PreExecute(slice.Time, CurrentHistories());
            foreach (var entry in slice.Bars)
            {
                if (!_histories.TryGetValue(entry.Key, out var history))
                {
                    history = new BarHistory(entry.Key);
                    _histories.Add(entry.Key, history);
                    history.Add(entry.Value);
                    Initialize(history);
                }
                else
                {
                    history.Add(entry.Value);
                }

                ExpireUntouchedOrders(history);
                Execute(history, history.Count - 1);
            }
            PostExecute(slice.Time, CurrentHistories());
        }

        /// <summary>
        /// Invokes the completion hook after Lean's final algorithm event.
        /// </summary>
        public override void OnEndOfAlgorithm()
        {
            if (!_backtestCompleted)
            {
                _backtestCompleted = true;
                BacktestComplete();
            }
            base.OnEndOfAlgorithm();
        }

        private List<BarHistory> CurrentHistories()
        {
            return _histories.Values.ToList();
        }

        private void ExpireUntouchedOrders(BarHistory bars)
        {
            if (bars.FillForward[bars.Count - 1])
            {
                return;
            }

            if (!_expiringOrders.TryGetValue(bars.Symbol, out var orders))
            {
                return;
            }

            for (var index = orders.Count - 1; index >= 0; index--)
            {
                var order = orders[index];
                if (order.Ticket.Status.IsClosed())
                {
                    orders.RemoveAt(index);
                    continue;
                }

                if (bars.DateTimes[bars.Count - 1] <= order.SubmittedLocalTime)
                {
                    continue;
                }

                // The engine scans orders against this bar before OnData; cancel here before
                // the next bar's scan can evaluate the order a second time.
                order.Ticket.Cancel("Not filled on the next bar.");
                orders.RemoveAt(index);
            }

            if (orders.Count == 0)
            {
                _expiringOrders.Remove(bars.Symbol);
            }
        }

        private WLStyleFillModel GetFillModel(Symbol symbol)
        {
            if (_fillModels.TryGetValue(symbol, out var fillModel))
            {
                return fillModel;
            }

            var security = Securities[symbol];
            fillModel = new WLStyleFillModel(security.FillModel);
            security.SetFillModel(fillModel);
            _fillModels.Add(symbol, fillModel);
            return fillModel;
        }

        /// <summary>
        /// Identifies adapter orders and stores the exchange-local submission time.
        /// </summary>
        private sealed class AdapterOrderProperties : OrderProperties
        {
            public AdapterOrderProperties(DateTime submittedLocalTime)
            {
                SubmittedLocalTime = submittedLocalTime;
            }

            public DateTime SubmittedLocalTime { get; }
        }

        private sealed class WLStyleFillModel : FillModel
        {
            private readonly IFillModel _nativeFillModel;

            public WLStyleFillModel(IFillModel nativeFillModel)
            {
                _nativeFillModel = nativeFillModel;
            }

            public override Fill Fill(FillModelParameters parameters)
            {
                Parameters = parameters;
                if (parameters.Order.Properties is not AdapterOrderProperties properties)
                {
                    return _nativeFillModel.Fill(parameters);
                }

                var tradeBar = parameters.Security.Cache.GetData<TradeBar>();
                if (tradeBar == null
                    || tradeBar.IsFillForward
                    || tradeBar.EndTime <= properties.SubmittedLocalTime)
                {
                    var utcTime = parameters.Security.LocalTime.ConvertToUtc(parameters.Security.Exchange.TimeZone);
                    return new Fill(new OrderEvent(parameters.Order, utcTime, OrderFee.Zero));
                }

                return parameters.Order.Type == OrderType.Market
                    ? new Fill(FillMarketAtOpen(parameters.Security, (MarketOrder)parameters.Order))
                    : _nativeFillModel.Fill(parameters);
            }

            private OrderEvent FillMarketAtOpen(Security security, MarketOrder order)
            {
                var utcTime = security.LocalTime.ConvertToUtc(security.Exchange.TimeZone);
                var fill = new OrderEvent(order, utcTime, OrderFee.Zero);
                if (order.Status == OrderStatus.Canceled || !IsExchangeOpen(security, false))
                {
                    return fill;
                }

                fill.FillPrice = security.Cache.GetData<TradeBar>().Open;
                var slippage = security.SlippageModel.GetSlippageApproximation(security, order);
                switch (order.Direction)
                {
                    case OrderDirection.Buy:
                        fill.FillPrice += slippage;
                        break;
                    case OrderDirection.Sell:
                        fill.FillPrice -= slippage;
                        break;
                }

                fill.FillQuantity = order.Quantity;
                fill.Status = OrderStatus.Filled;
                return fill;
            }
        }

        private static decimal ToDecimal<T>(T value, string name)
            where T : struct, IConvertible
        {
            try
            {
                if (value is double doubleValue && !double.IsFinite(doubleValue))
                {
                    throw new ArgumentOutOfRangeException(name, "Parameter values must be finite.");
                }
                return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            }
            catch (OverflowException exception)
            {
                throw new ArgumentException(
                    $"Parameter '{name}' value is outside the decimal range.",
                    name,
                    exception);
            }
        }

        private sealed record ParameterDefinition(
            Type Type,
            decimal DefaultValue,
            decimal MinValue,
            decimal MaxValue,
            decimal Step);
    }
}
