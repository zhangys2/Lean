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
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Exercises adapter order timing from PreExecute and across symbols.
    /// </summary>
    public class WLStylePreExecuteCrossSymbolRegressionAlgorithm : WLStrategyBase, IRegressionAlgorithmDefinition
    {
        private const string PreExecuteTag = "WL pre-execute order";
        private const string CrossSymbolTag = "WL cross-symbol order";
        private Symbol _spy;
        private Symbol _ibm;
        private BarHistory _ibmHistory;
        private bool _preExecuteSubmitted;
        private bool _crossSymbolSubmitted;
        private bool _dispatchingRemainingData;
        private bool _preExecuteFilled;
        private bool _crossSymbolFilled;
        private bool _preExecuteTimingValid;
        private bool _crossSymbolTimingValid;
        private bool _preExecuteBarAheadOfHistory;
        private bool _crossSymbolBarAheadOfHistory;
        private DateTime _preExecuteSignalBarEndTime;
        private DateTime _crossSymbolSignalBarEndTime;

        public override void Initialize()
        {
            SetStartDate(2013, 10, 7);
            SetEndDate(2013, 10, 11);
            SetCash(100000);
            _spy = AddEquity("SPY", Resolution.Minute).Symbol;
            _ibm = AddEquity("IBM", Resolution.Minute).Symbol;
        }

        protected override void Initialize(BarHistory bars)
        {
        }

        public override void OnData(Slice slice)
        {
            // Dispatch SPY before IBM to exercise a slice whose IBM bar is current while its
            // adapter history is still from the previous slice.
            if (!_preExecuteSubmitted && slice.Bars.TryGetValue(_spy, out var spyBar))
            {
                _preExecuteSignalBarEndTime = spyBar.EndTime;
            }
            if (!_crossSymbolSubmitted && slice.Bars.TryGetValue(_ibm, out var ibmBar))
            {
                _crossSymbolSignalBarEndTime = ibmBar.EndTime;
            }
            var data = slice.AllData.ToList();
            var spyData = data
                .Where(item => item.Symbol == _spy)
                .ToList();
            var remainingData = data
                .Where(item => item.Symbol != _spy)
                .ToList();
            base.OnData(new Slice(slice.Time, spyData, slice.UtcTime));
            _dispatchingRemainingData = true;
            try
            {
                base.OnData(new Slice(slice.Time, remainingData, slice.UtcTime));
            }
            finally
            {
                _dispatchingRemainingData = false;
            }
        }

        protected override void PreExecute(DateTime time, List<BarHistory> bars)
        {
            if (_preExecuteSubmitted || _dispatchingRemainingData)
            {
                return;
            }

            foreach (var history in bars)
            {
                if (history.Symbol == _spy && history.Count > 0)
                {
                    _preExecuteBarAheadOfHistory =
                        _preExecuteSignalBarEndTime > history.DateTimes[history.Count - 1];
                    _preExecuteSubmitted = true;
                    PlaceTrade(history, TransactionType.Buy, OrderType.Market, 2m, signalName: PreExecuteTag);
                    return;
                }
            }
        }

        protected override void Execute(BarHistory bars, int idx)
        {
            if (bars.Symbol == _ibm)
            {
                _ibmHistory = bars;
                return;
            }

            if (bars.Symbol == _spy && !_crossSymbolSubmitted && _ibmHistory != null)
            {
                _crossSymbolBarAheadOfHistory =
                    _crossSymbolSignalBarEndTime > _ibmHistory.DateTimes[_ibmHistory.Count - 1];
                _crossSymbolSubmitted = true;
                PlaceTrade(_ibmHistory, TransactionType.Buy, OrderType.Market, 3m, signalName: CrossSymbolTag);
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status != OrderStatus.Filled)
            {
                return;
            }

            var order = Transactions.GetOrderById(orderEvent.OrderId);
            if (order?.Tag != PreExecuteTag && order?.Tag != CrossSymbolTag)
            {
                return;
            }

            var security = Securities[order.Symbol];
            var bar = security.Cache.GetData<QuantConnect.Data.Market.TradeBar>();
            var timingValid = orderEvent.UtcTime > order.Time;
            var openValid = bar != null
                && !bar.IsFillForward
                && bar.EndTime.ConvertToUtc(security.Exchange.TimeZone) == orderEvent.UtcTime
                && orderEvent.FillPrice == bar.Open;

            if (order.Tag == PreExecuteTag)
            {
                _preExecuteTimingValid = timingValid && openValid && bar.EndTime > _preExecuteSignalBarEndTime;
                _preExecuteFilled = true;
            }
            else
            {
                _crossSymbolTimingValid = timingValid && openValid && bar.EndTime > _crossSymbolSignalBarEndTime;
                _crossSymbolFilled = true;
            }
        }

        public override void OnEndOfAlgorithm()
        {
            if (!_preExecuteFilled || !_crossSymbolFilled
                || !_preExecuteTimingValid || !_crossSymbolTimingValid
                || !_preExecuteBarAheadOfHistory || !_crossSymbolBarAheadOfHistory)
            {
                throw new InvalidOperationException(
                    $"Invalid engine order result: pre-execute filled={_preExecuteFilled}, timing/open={_preExecuteTimingValid}; " +
                    $"pre-execute bar-ahead={_preExecuteBarAheadOfHistory}, signal-bar={_preExecuteSignalBarEndTime:o}; " +
                    $"cross-symbol filled={_crossSymbolFilled}, timing/open={_crossSymbolTimingValid}, " +
                    $"bar-ahead={_crossSymbolBarAheadOfHistory}, signal-bar={_crossSymbolSignalBarEndTime:o}.");
            }

            base.OnEndOfAlgorithm();
        }

        public bool CanRunLocally { get; } = true;

        public List<Language> Languages { get; } = new() { Language.CSharp };

        public long DataPoints => -1;

        public int AlgorithmHistoryDataPoints => 0;

        public AlgorithmStatus AlgorithmStatus => AlgorithmStatus.Completed;

        public Dictionary<string, string> ExpectedStatistics => new();
    }
}
