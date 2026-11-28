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
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Orders;

namespace QuantConnect.Algorithm.CSharp.WLStyle
{
    /// <summary>
    /// Ranks SPY ahead of IBM, submits only the admitted SPY round trip, and records one max-open NSF.
    /// The pending SPY order is read back from Lean before IBM is considered.
    /// </summary>
    public sealed class WLPhase4PortfolioRegressionAlgorithm : WLStrategyBase, IRegressionAlgorithmDefinition
    {
        private const long SpyIntent = 1;
        private const long IbmIntent = 2;
        private readonly WLPortfolioAdmission _admission = new(new WLPortfolioRules(
            StartingCapital: 100000m,
            MaxOpenPositions: 1));
        private readonly Dictionary<int, long> _entryIntents = new();
        private Symbol _spy;
        private Symbol _ibm;
        private bool _spySubmitted;
        private bool _ibmConsidered;
        private bool _exitSubmitted;
        private WLAdmissionReason _ibmReason;
        private bool _rejectedWhileSpyPending;

        public bool CanRunLocally => true;

        public List<Language> Languages => new() { Language.CSharp };

        public long DataPoints => 1575;

        public int AlgorithmHistoryDataPoints => 0;

        public AlgorithmStatus AlgorithmStatus => AlgorithmStatus.Completed;

        public Dictionary<string, string> ExpectedStatistics => new()
        {
            { "Total Orders", "2" },
            { "Average Win", "0%" },
            { "Average Loss", "0%" },
            { "Compounding Annual Return", "0%" },
            { "Drawdown", "0%" },
            { "Expectancy", "0" },
            { "Start Equity", "100000" },
            { "End Equity", "99997.91" },
            { "Net Profit", "0%" },
            { "Sharpe Ratio", "0" },
            { "Sortino Ratio", "0" },
            { "Probabilistic Sharpe Ratio", "0%" },
            { "Loss Rate", "0%" },
            { "Win Rate", "0%" },
            { "Profit-Loss Ratio", "0" },
            { "Alpha", "0" },
            { "Beta", "0" },
            { "Annual Standard Deviation", "0" },
            { "Annual Variance", "0" },
            { "Information Ratio", "0" },
            { "Tracking Error", "0" },
            { "Treynor Ratio", "0" },
            { "Total Fees", "$2.00" },
            { "Estimated Strategy Capacity", "$19000000.00" },
            { "Lowest Capacity Asset", "SPY R735QTJ8XC9X" },
            { "Portfolio Turnover", "0.29%" },
            { "Drawdown Recovery", "0" },
            { "OrderListHash", "44a8da83f019eb7b9e502bd23d547c6e" }
        };

        public override void Initialize()
        {
            SetStartDate(2013, 10, 7);
            SetEndDate(2013, 10, 7);
            _spy = AddEquity("SPY", Resolution.Minute).Symbol;
            _ibm = AddEquity("IBM", Resolution.Minute).Symbol;
            _admission.Apply(this);
        }

        protected override void Initialize(BarHistory bars)
        {
        }

        protected override void Execute(BarHistory bars, int idx)
        {
        }

        protected override void PostExecute(DateTime time, List<BarHistory> bars)
        {
            FeedLeanOrders();
            var spy = bars.FirstOrDefault(history => history.Symbol == _spy);
            var ibm = bars.FirstOrDefault(history => history.Symbol == _ibm);
            if (spy == null || ibm == null)
            {
                return;
            }

            if (!_spySubmitted)
            {
                var decision = _admission.Evaluate(Context(spy, SpyIntent, WLAdmissionGate.Entry));
                Ensure(decision.Admitted, "SPY was not admitted.");
                var ticket = PlaceTrade(spy, TransactionType.Buy, OrderType.Market, 1m, signalName: "WL portfolio entry");
                _entryIntents.Add(ticket.OrderId, SpyIntent);
                _spySubmitted = true;
                FeedLeanOrders();
            }

            if (!_ibmConsidered)
            {
                var decision = _admission.Evaluate(Context(ibm, IbmIntent, WLAdmissionGate.Entry));
                _ibmReason = decision.Reason;
                _rejectedWhileSpyPending = !decision.Admitted
                    && Portfolio[_spy].Quantity == 0
                    && Transactions.GetOpenOrderTickets(_spy).Any();
                Ensure(!decision.Admitted, "IBM was submitted despite the open-position cap.");
                _ibmConsidered = true;
            }

            var holding = bars.First(history => history.Symbol == _spy);
            if (!_exitSubmitted && HasOpenPosition(holding, PositionType.Long))
            {
                var exit = _admission.Evaluate(Context(holding, 3, WLAdmissionGate.Exit));
                Ensure(exit.Admitted && !exit.IsNsf, "The exit was not admitted.");
                ClosePosition(holding, "WL portfolio exit");
                _exitSubmitted = true;
            }
        }

        public override void OnEndOfAlgorithm()
        {
            Ensure(_spySubmitted && _exitSubmitted, "The admitted round trip was not submitted.");
            Ensure(_ibmConsidered && _ibmReason == WLAdmissionReason.MaxOpen, "IBM was not rejected for max-open.");
            Ensure(_rejectedWhileSpyPending, "IBM was not rejected against a live SPY order.");
            Ensure(_admission.NsfCount == 1, $"Expected one NSF, observed {_admission.NsfCount}.");
            Ensure(_admission.OpenNsf(_ibm).Count == 0, "Retention was off, but an NSF position stayed open.");
            Ensure(!HasRetainedNsfLeak(), "A retained NSF changed Lean holdings.");
            Ensure(Portfolio[_spy].Quantity == 0 && Portfolio[_ibm].Quantity == 0, "The book was not flat.");
            Ensure(Transactions.GetOrders(order => true).Count() == 2, "A rejected symbol was submitted.");
            base.OnEndOfAlgorithm();
        }

        private WLAdmissionContext Context(BarHistory bars, long intentId, WLAdmissionGate gate)
        {
            var close = (decimal)bars.Close[bars.Count - 1];
            var mark = WLPortfolioMarks.Select(close, eligibleOpen: null, nextOpen: false);
            var holdings = new List<Symbol>();
            var filled = 0m;
            foreach (var security in Securities.Values)
            {
                if (security.Holdings.Quantity == 0)
                {
                    continue;
                }

                holdings.Add(security.Symbol);
                filled += WLPortfolioMarks.FilledExposure(security);
            }

            return new WLAdmissionContext(
                bars.Symbol,
                intentId,
                gate,
                PositionType.Long,
                Quantity: 1m,
                AccountCurrencyExposure: mark,
                EstimatedFee: 0m,
                Equity: Portfolio.TotalPortfolioValue,
                GrossFilledExposure: filled,
                HoldingSymbols: holdings,
                SliceId: bars.DateTimes[bars.Count - 1].Ticks,
                Phase: WLAdmissionPhase.Execution);
        }

        private void FeedLeanOrders()
        {
            var pending = new List<WLPendingOrder>();
            foreach (var ticket in Transactions.GetOpenOrderTickets())
            {
                if (!_entryIntents.TryGetValue(ticket.OrderId, out var intentId))
                {
                    continue;
                }

                var remaining = Math.Abs(ticket.QuantityRemaining);
                if (remaining == 0m)
                {
                    continue;
                }

                var mark = Securities[ticket.Symbol].Price;
                pending.Add(new WLPendingOrder(
                    intentId,
                    ticket.Symbol,
                    ticket.Quantity > 0 ? PositionType.Long : PositionType.Short,
                    remaining,
                    remaining * mark));
            }

            _admission.RestorePending(pending);
        }

        private bool HasRetainedNsfLeak()
        {
            return Portfolio[_ibm].Invested || _admission.HasRetainedNsf(_ibm);
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
