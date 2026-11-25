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
using NUnit.Framework;
using QuantConnect.Algorithm.WLStyle;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using QuantConnect.Orders;
using QuantConnect.Orders.Fills;
using QuantConnect.Securities;
using QuantConnect.Tests.Common.Data;
using QuantConnect.Tests.Common.Securities;
using QuantConnect.Tests.Engine.DataFeeds;
using QuantConnect.Util;

namespace QuantConnect.Tests.Algorithm
{
    [TestFixture]
    public class WLStyleAdapterTests
    {
        [Test]
        public void OnDataKeepsPerSymbolHistoryAndCallsHooksOncePerSlice()
        {
            var algorithm = new ProbeStrategy();
            algorithm.SetParameters(new Dictionary<string, string> { ["Period"] = "3" });
            var start = new DateTime(2024, 1, 2, 14, 30, 0, DateTimeKind.Utc);
            var spy = Symbol.Create("SPY", SecurityType.Equity, Market.USA);
            var aapl = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);

            algorithm.Feed(Bar(start, spy, 10), Bar(start, aapl, 20));
            algorithm.Feed(Bar(start.AddMinutes(1), spy, 11));

            Assert.AreEqual(1, algorithm.BacktestBeginCount);
            Assert.AreEqual(2, algorithm.PreExecuteCount);
            Assert.AreEqual(2, algorithm.PostExecuteCount);
            Assert.IsEmpty(algorithm.PreHistoryCounts[0]);
            Assert.AreEqual(2, algorithm.PreHistoryCounts[1].Sum());
            Assert.AreEqual(2, algorithm.PostHistoryCounts[0].Sum());
            Assert.AreEqual(3, algorithm.PostHistoryCounts[1].Sum());
            Assert.AreEqual(1, algorithm.Initialized[spy]);
            Assert.AreEqual(1, algorithm.Initialized[aapl]);
            Assert.AreEqual(3, algorithm.Periods[spy]);
            Assert.AreEqual(3, algorithm.Periods[aapl]);
            Assert.AreEqual(new[] { 0, 1 }, algorithm.Executed[spy]);
            Assert.AreEqual(new[] { 0 }, algorithm.Executed[aapl]);
            Assert.AreEqual(2, algorithm.Histories[spy].Count);
            Assert.AreEqual(1, algorithm.Histories[aapl].Count);
            Assert.AreEqual(10d, algorithm.Histories[spy].Close[0]);
            Assert.AreEqual(11d, algorithm.Histories[spy].Close[1]);
            Assert.AreEqual(20d, algorithm.Histories[aapl].Close[0]);
            Assert.AreEqual(start.AddMinutes(2), algorithm.Histories[spy].DateTimes[1]);
            Assert.AreEqual(10d, algorithm.Histories[spy].Open[0]);
            Assert.AreEqual(100d, algorithm.Histories[spy].Volume[0]);
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = algorithm.Histories[spy].Close[2]);

            algorithm.Complete();
            Assert.AreEqual(1, algorithm.BacktestCompleteCount);
        }

        [Test]
        public void IndicatorSeriesUseLeanSmaValuesAndCrossesUseOnlyPastAndCurrentBars()
        {
            var algorithm = new ProbeStrategy();
            var start = new DateTime(2024, 1, 2, 14, 30, 0, DateTimeKind.Utc);
            var symbol = Symbol.Create("SPY", SecurityType.Equity, Market.USA);

            algorithm.Feed(Bar(start, symbol, 3));
            algorithm.Feed(Bar(start.AddMinutes(1), symbol, 1));
            algorithm.Feed(Bar(start.AddMinutes(2), symbol, 2));

            var history = algorithm.Histories[symbol];
            var sma = algorithm.Sma[symbol];
            Assert.AreEqual(2d, sma[1], 1e-12);
            Assert.AreEqual(1.5, sma[2], 1e-12);
            Assert.IsTrue(double.IsFinite(algorithm.Ema[symbol][2]));
            Assert.IsTrue(double.IsFinite(algorithm.Rsi[symbol][2]));
            Assert.IsTrue(double.IsFinite(algorithm.Atr[symbol][2]));
            Assert.IsTrue(double.IsFinite(algorithm.Bands[symbol].Upper[2]));
            Assert.IsTrue(history.Close.CrossesOver(sma, 2));
            Assert.IsFalse(history.Close.CrossesOver(sma, 0));
            Assert.IsTrue(history.Close.TurnsUp(2));
            Assert.IsFalse(history.Close.TurnsDown(2));
            Assert.AreEqual(3d, (history.Close + 1d)[2], 1e-12);
        }

        [Test]
        public void MacdAdxAndStochasticSeriesMatchLeanForEveryDeliveredBar()
        {
            var algorithm = new ProbeStrategy();
            var start = new DateTime(2024, 1, 2, 14, 30, 0, DateTimeKind.Utc);
            var symbol = Symbol.Create("SPY", SecurityType.Equity, Market.USA);
            var closes = new[] { 10m, 12m, 9m, 13m, 8m, 14m, 11m, 15m, 9m, 16m, 12m, 17m, 10m };
            var bars = closes
                .Select((close, index) => Bar(start.AddMinutes(index), symbol, close))
                .ToArray();
            bars[5] = (TradeBar)bars[4].Clone(true);
            bars[5].Time = start.AddMinutes(5);

            algorithm.Feed(bars[0]);
            var history = algorithm.Histories[symbol];
            var macd = WLSeries.MACD(history.Close, 2, 4, 2);
            var adx = WLSeries.ADX(history, 3);
            var stochastic = WLSeries.Stochastic(history, 3, 2, 2);

            var nativeMacd = new MovingAverageConvergenceDivergence(2, 4, 2);
            var nativeAdx = new AverageDirectionalIndex(3);
            var nativeStochastic = new Stochastic(3, 2, 2);
            for (var index = 0; index < bars.Length; index++)
            {
                var bar = bars[index];
                if (index > 0)
                {
                    algorithm.Feed(bar);
                }
                nativeMacd.Update(new IndicatorDataPoint(symbol, bar.EndTime, bar.Close));
                nativeAdx.Update(bar);
                nativeStochastic.Update(bar);

                Assert.AreEqual(index + 1, nativeMacd.Samples);
                Assert.AreEqual(index + 1, nativeAdx.Samples);
                Assert.AreEqual(index + 1, nativeStochastic.Samples);
                Assert.AreEqual(history.Count, macd.Macd.Count);
                Assert.AreEqual(history.Count, adx.Adx.Count);
                Assert.AreEqual(history.Count, stochastic.K.Count);
                Assert.AreEqual(nativeMacd.IsReady, double.IsFinite(macd.Macd[index]));
                Assert.AreEqual(nativeMacd.IsReady, double.IsFinite(macd.Signal[index]));
                Assert.AreEqual(nativeMacd.IsReady, double.IsFinite(macd.Histogram[index]));
                Assert.AreEqual(nativeAdx.IsReady, double.IsFinite(adx.Adx[index]));
                Assert.AreEqual(nativeAdx.IsReady, double.IsFinite(adx.PositiveDirectionalIndex[index]));
                Assert.AreEqual(nativeAdx.IsReady, double.IsFinite(adx.NegativeDirectionalIndex[index]));
                Assert.AreEqual(nativeStochastic.IsReady, double.IsFinite(stochastic.FastK[index]));
                Assert.AreEqual(nativeStochastic.IsReady, double.IsFinite(stochastic.K[index]));
                Assert.AreEqual(nativeStochastic.IsReady, double.IsFinite(stochastic.D[index]));

                if (nativeMacd.IsReady)
                {
                    Assert.AreEqual((double)nativeMacd.Current.Value, macd.Macd[index], 1e-12);
                    Assert.AreEqual((double)nativeMacd.Signal.Current.Value, macd.Signal[index], 1e-12);
                    Assert.AreEqual((double)nativeMacd.Histogram.Current.Value, macd.Histogram[index], 1e-12);
                }
                if (nativeAdx.IsReady)
                {
                    Assert.AreEqual((double)nativeAdx.Current.Value, adx.Adx[index], 1e-12);
                    Assert.AreEqual((double)nativeAdx.PositiveDirectionalIndex.Current.Value, adx.PositiveDirectionalIndex[index], 1e-12);
                    Assert.AreEqual((double)nativeAdx.NegativeDirectionalIndex.Current.Value, adx.NegativeDirectionalIndex[index], 1e-12);
                }
                if (nativeStochastic.IsReady)
                {
                    Assert.AreEqual((double)nativeStochastic.FastStoch.Current.Value, stochastic.FastK[index], 1e-12);
                    Assert.AreEqual((double)nativeStochastic.StochK.Current.Value, stochastic.K[index], 1e-12);
                    Assert.AreEqual((double)nativeStochastic.StochD.Current.Value, stochastic.D[index], 1e-12);
                }
            }

            Assert.IsTrue(history.FillForward[5]);
            Assert.AreEqual(bars.Length, nativeMacd.Samples);
            Assert.AreEqual(bars.Length, nativeAdx.Samples);
            Assert.AreEqual(bars.Length, nativeStochastic.Samples);
        }

        [Test]
        public void FillForwardFlagsRemainAlignedWithBars()
        {
            var algorithm = new ProbeStrategy();
            var start = new DateTime(2024, 1, 2, 14, 30, 0, DateTimeKind.Utc);
            var symbol = Symbol.Create("SPY", SecurityType.Equity, Market.USA);
            var real = Bar(start, symbol, 10);
            var fillForward = (TradeBar)real.Clone(true);
            fillForward.Time = start.AddMinutes(1);

            algorithm.Feed(real);
            algorithm.Feed(fillForward);

            var history = algorithm.Histories[symbol];
            Assert.AreEqual(new[] { false, true }, history.FillForward);
            Assert.AreEqual(2, history.Close.Count);
        }

        [Test]
        public void PlotTimeSeriesUsesLeanChartPipeline()
        {
            var algorithm = new ProbeStrategy();
            var start = new DateTime(2024, 1, 2, 14, 30, 0, DateTimeKind.Utc);
            var symbol = Symbol.Create("SPY", SecurityType.Equity, Market.USA);
            algorithm.Feed(Bar(start, symbol, 12));

            algorithm.PlotCurrentClose(symbol, "WL Close", "Close");

            var chart = algorithm.GetChartUpdates().Single(update => update.Name == "WL Close");
            var point = chart.Series["Close"].GetValues<ChartPoint>().Single();
            Assert.AreEqual(12d, point.y);
        }

        [Test]
        public void PlaceTradeKeepsQuantityPositiveAndMapsDirectionToLeanOrderQuantity()
        {
            var algorithm = new ProbeStrategy();
            var processor = algorithm.ConfigureTrading();
            var start = new DateTime(2024, 1, 2, 14, 30, 0, DateTimeKind.Utc);
            var symbol = Symbol.Create("SPY", SecurityType.Equity, Market.USA);
            algorithm.Feed(Bar(start, symbol, 10));
            var history = algorithm.Histories[symbol];

            var buy = algorithm.Submit(history, TransactionType.Buy, OrderType.Market, 2.5m);
            var sell = algorithm.Submit(history, TransactionType.Sell, OrderType.Limit, 3.25m, 11m);
            var shortOrder = algorithm.Submit(history, TransactionType.Short, OrderType.StopMarket, 4m, 9m);
            var cover = algorithm.Submit(history, TransactionType.Cover, OrderType.Market, 1.75m);

            Assert.AreEqual(2.5m, buy.SubmitRequest.Quantity);
            Assert.AreEqual(-3.25m, sell.SubmitRequest.Quantity);
            Assert.AreEqual(-4m, shortOrder.SubmitRequest.Quantity);
            Assert.AreEqual(1.75m, cover.SubmitRequest.Quantity);
            Assert.AreEqual(OrderType.Limit, sell.SubmitRequest.OrderType);
            Assert.AreEqual(OrderType.StopMarket, shortOrder.SubmitRequest.OrderType);
            Assert.AreEqual(4, processor.ProcessedOrdersRequests.Count);
            Assert.Throws<ArgumentOutOfRangeException>(
                () => algorithm.Submit(history, TransactionType.Buy, OrderType.Market, 0m));
        }

        [Test]
        public void UntouchedLimitAndStopOrdersAreCanceledAfterTheNextDeliveredBar()
        {
            var algorithm = new ExpiringOrderStrategy();
            var processor = algorithm.ConfigureTrading();
            var start = new DateTime(2024, 1, 2, 14, 30, 0, DateTimeKind.Utc);
            var symbol = Symbol.Create("SPY", SecurityType.Equity, Market.USA);

            algorithm.Feed(Bar(start, symbol, 10));
            Assert.AreEqual(2, processor.ProcessedOrdersRequests.Count);
            var tickets = processor.GetOrderTickets().ToArray();
            var fillForward = (TradeBar)Bar(start.AddMinutes(1), symbol, 10).Clone(true);
            algorithm.Feed(fillForward);
            Assert.IsTrue(processor.GetOrderTickets().All(ticket => ticket.CancelRequest == null));
            Assert.IsTrue(tickets.All(ticket => algorithm.Fill(ticket, fillForward).FillQuantity == 0m));
            Assert.IsTrue(tickets.All(ticket => algorithm.Fill(ticket, Bar(start.AddMinutes(2), symbol, 10)).FillQuantity == 0m));
            // The engine scans the eligible bar before OnData, so OnData of that same bar must cancel.
            algorithm.Feed(Bar(start.AddMinutes(2), symbol, 10));
            var cancellations = processor.GetOrderTickets()
                .Where(ticket => ticket.CancelRequest != null)
                .Select(ticket => ticket.CancelRequest)
                .ToArray();

            Assert.AreEqual(2, cancellations.Length);
            Assert.IsTrue(cancellations.All(request => request.Tag == "Not filled on the next bar."));
        }

        [Test]
        public void AdapterOrdersFillAtNextRealBarOpenOrTouchWithoutSameBarOrFillForwardFills()
        {
            var algorithm = new ProbeStrategy();
            algorithm.ConfigureTrading();
            var start = new DateTime(2024, 1, 2, 14, 30, 0, DateTimeKind.Utc);
            var symbol = Symbol.Create("SPY", SecurityType.Equity, Market.USA);
            algorithm.Feed(Bar(start, symbol, 10));
            algorithm.Securities[symbol].SetFillModel(new EquityFillModel());
            var history = algorithm.Histories[symbol];
            var market = algorithm.Submit(history, TransactionType.Buy, OrderType.Market, 2.5m);
            var limit = algorithm.Submit(history, TransactionType.Buy, OrderType.Limit, 3.25m, 10m);
            var stop = algorithm.Submit(history, TransactionType.Sell, OrderType.StopMarket, 4m, 9m);

            var sameBar = Bar(start, symbol, 10);
            Assert.AreEqual(0m, algorithm.Fill(market, sameBar).FillQuantity);
            Assert.AreEqual(0m, algorithm.Fill(limit, sameBar).FillQuantity);
            Assert.AreEqual(0m, algorithm.Fill(stop, sameBar).FillQuantity);

            var fillForward = (TradeBar)Bar(start.AddMinutes(1), symbol, 10).Clone(true);
            Assert.AreEqual(0m, algorithm.Fill(market, fillForward).FillQuantity);
            Assert.AreEqual(0m, algorithm.Fill(limit, fillForward).FillQuantity);
            Assert.AreEqual(0m, algorithm.Fill(stop, fillForward).FillQuantity);

            var nextBar = new TradeBar(
                start.AddMinutes(2),
                symbol,
                11m,
                13m,
                8m,
                10m,
                100,
                TimeSpan.FromMinutes(1));
            var marketFill = algorithm.Fill(market, nextBar);
            var limitFill = algorithm.Fill(limit, nextBar);
            var stopFill = algorithm.Fill(stop, nextBar);

            Assert.AreEqual(OrderStatus.Filled, marketFill.Status);
            Assert.AreEqual(2.5m, marketFill.FillQuantity);
            Assert.AreEqual(11m, marketFill.FillPrice);
            Assert.AreEqual(OrderStatus.Filled, limitFill.Status);
            Assert.AreEqual(3.25m, limitFill.FillQuantity);
            Assert.AreEqual(10m, limitFill.FillPrice);
            Assert.AreEqual(OrderStatus.Filled, stopFill.Status);
            Assert.AreEqual(-4m, stopFill.FillQuantity);
            Assert.AreEqual(9m, stopFill.FillPrice);
        }

        private static TradeBar Bar(DateTime time, Symbol symbol, decimal close)
        {
            return new TradeBar(time, symbol, close, close + 1, close - 1, close, 100, TimeSpan.FromMinutes(1));
        }

        private class ProbeStrategy : WLStrategyBase
        {
            public readonly Dictionary<Symbol, int> Initialized = new();
            public readonly Dictionary<Symbol, List<int>> Executed = new();
            public readonly Dictionary<Symbol, BarHistory> Histories = new();
            public readonly Dictionary<Symbol, int> Periods = new();
            public readonly Dictionary<Symbol, TimeSeries> Sma = new();
            public readonly Dictionary<Symbol, TimeSeries> Ema = new();
            public readonly Dictionary<Symbol, TimeSeries> Rsi = new();
            public readonly Dictionary<Symbol, TimeSeries> Atr = new();
            public readonly Dictionary<Symbol, BollingerBandSeries> Bands = new();
            public readonly List<int[]> PreHistoryCounts = new();
            public readonly List<int[]> PostHistoryCounts = new();
            public int BacktestBeginCount { get; private set; }
            public int BacktestCompleteCount { get; private set; }
            public int PreExecuteCount { get; private set; }
            public int PostExecuteCount { get; private set; }

            public override void Initialize()
            {
            }

            protected override void Initialize(BarHistory bars)
            {
                Initialized[bars.Symbol] = Initialized.GetValueOrDefault(bars.Symbol) + 1;
                Histories[bars.Symbol] = bars;
                Periods[bars.Symbol] = AddParameter("Period", 2, 1, 5, 1);
                Sma[bars.Symbol] = WLSeries.SMA(bars.Close, 2);
                Ema[bars.Symbol] = WLSeries.EMA(bars.Close, 2);
                Rsi[bars.Symbol] = WLSeries.RSI(bars.Close, 2);
                Atr[bars.Symbol] = WLSeries.ATR(bars, 2);
                Bands[bars.Symbol] = WLSeries.BollingerBands(bars.Close, 2);
            }

            protected override void Execute(BarHistory bars, int idx)
            {
                if (!Executed.TryGetValue(bars.Symbol, out var indices))
                {
                    indices = new List<int>();
                    Executed[bars.Symbol] = indices;
                }
                indices.Add(idx);
            }

            protected override void BacktestBegin()
            {
                BacktestBeginCount++;
            }

            protected override void BacktestComplete()
            {
                BacktestCompleteCount++;
            }

            protected override void PreExecute(DateTime time, List<BarHistory> bars)
            {
                PreExecuteCount++;
                PreHistoryCounts.Add(bars.Select(history => history.Count).ToArray());
            }

            protected override void PostExecute(DateTime time, List<BarHistory> bars)
            {
                PostExecuteCount++;
                PostHistoryCounts.Add(bars.Select(history => history.Count).ToArray());
            }

            public void Feed(params TradeBar[] bars)
            {
                // Data timestamps are New York exchange-local while the algorithm clock is UTC.
                var time = bars.Max(bar => bar.EndTime);
                var utcTime = time.ConvertToUtc(TimeZones.NewYork);
                var slice = new Slice(time, bars.Cast<BaseData>(), utcTime);
                SetDateTime(utcTime);
                SetCurrentSlice(slice);
                foreach (var bar in bars)
                {
                    if (Securities.TryGetValue(bar.Symbol, out var security))
                    {
                        security.SetMarketPrice(bar);
                    }
                }
                OnData(slice);
            }

            public void Complete()
            {
                OnEndOfAlgorithm();
            }

            public void PlotCurrentClose(Symbol symbol, string chartName, string seriesName)
            {
                PlotTimeSeries(Histories[symbol], chartName, seriesName, Histories[symbol].Close);
            }

            public FakeOrderProcessor ConfigureTrading()
            {
                SubscriptionManager.SetDataManager(new DataManagerStub(this));
                AddEquity("SPY");
                SetCash(100000);
                SetLiveMode(false);
                SetFinishedWarmingUp();
                var processor = new FakeOrderProcessor { TransactionManager = Transactions };
                Transactions.SetOrderProcessor(processor);
                return processor;
            }

            public OrderTicket Submit(
                BarHistory bars,
                TransactionType transactionType,
                OrderType orderType,
                decimal quantity,
                decimal price = 0m)
            {
                return PlaceTrade(bars, transactionType, orderType, quantity, price);
            }

            public OrderEvent Fill(OrderTicket ticket, TradeBar bar)
            {
                var security = Securities[ticket.Symbol];
                security.SetLocalTimeKeeper(TimeKeeper.GetLocalTimeKeeper(TimeZones.NewYork));
                security.SetMarketPrice(bar);
                var config = new SubscriptionDataConfig(
                    typeof(TradeBar),
                    ticket.Symbol,
                    Resolution.Minute,
                    TimeZones.NewYork,
                    TimeZones.NewYork,
                    true,
                    false,
                    false);
                var provider = new MockSubscriptionDataConfigProvider(config);
                var order = QuantConnect.Orders.Order.CreateOrder(ticket.SubmitRequest);
                var parameters = new FillModelParameters(
                    security,
                    order,
                    provider,
                    TimeSpan.FromHours(1),
                    null);
                return security.FillModel.Fill(parameters).Single();
            }
        }

        private sealed class ExpiringOrderStrategy : ProbeStrategy
        {
            protected override void Execute(BarHistory bars, int idx)
            {
                if (idx == 0)
                {
                    PlaceTrade(bars, TransactionType.Buy, OrderType.Limit, 1m, 8m);
                    PlaceTrade(bars, TransactionType.Short, OrderType.StopMarket, 2m, 8m);
                }
            }
        }
    }
}
