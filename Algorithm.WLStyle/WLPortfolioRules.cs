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
using QuantConnect.Securities;

namespace QuantConnect.Algorithm.WLStyle
{
    /// <summary>
    /// Opt-in portfolio limits. Nothing here runs unless a caller constructs it.
    /// T4 is the strategy integration; this type does not change <c>PlaceTrade</c> or <c>HasOpenPosition</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gross capacity for positive equity is <c>max(0, equity × factor − gross filled exposure − reserved pending exposure)</c>.
    /// A null margin factor uses 1. The factor widens that dollar capacity and is never multiplied into quantity.
    /// </para>
    /// <para>
    /// Deviation from unpublished WealthLab debit, short-netting, fee, and settlement sequencing:
    /// filled and reserved exposure are absolute account-currency amounts, with no long/short netting.
    /// Futures use Lean initial margin instead of notional. The candidate cost is exposure plus the fee estimate,
    /// and that whole cost is reserved. Settled-cash and other native buying-power failures stay broker vetoes, not NSF.
    /// Marks come from the caller: last observable price, or an eligible open when the basis is the next open.
    /// The forming bar's close is not a next-open mark.
    /// </para>
    /// </remarks>
    public sealed record WLPortfolioRules(
        decimal? StartingCapital = null,
        decimal? MarginFactor = null,
        int MaxOpenPositions = 0,
        int MaxSignalsPerBar = 0,
        int MaxEntriesPerBar = 0,
        bool RetainNsf = false)
    {
        /// <summary>Overlay factor. Null means unlevered capacity.</summary>
        public decimal OverlayFactor => MarginFactor ?? 1m;

        /// <summary>Rejects a factor below 1 or a negative capital or cap.</summary>
        public void Validate()
        {
            if (StartingCapital is < 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(StartingCapital), "Starting capital cannot be negative.");
            }

            if (MarginFactor is < 1m)
            {
                throw new ArgumentOutOfRangeException(nameof(MarginFactor), "Margin factor must be at least 1.");
            }

            if (MaxOpenPositions < 0 || MaxSignalsPerBar < 0 || MaxEntriesPerBar < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxOpenPositions), "Position and entry caps cannot be negative. Zero means unlimited.");
            }
        }
    }

    /// <summary>Why an intent was admitted or rejected. Only buying power and max-open are NSF.</summary>
    public enum WLAdmissionReason
    {
        /// <summary>The intent may be submitted.</summary>
        Admitted,

        /// <summary>The gross-capacity check failed. This is NSF.</summary>
        BuyingPower,

        /// <summary>A new symbol would exceed the open-position cap. This is NSF.</summary>
        MaxOpen,

        /// <summary>The signal-slice entry throttle. Not NSF.</summary>
        MaxSignals,

        /// <summary>The execution-slice entry throttle. Not NSF.</summary>
        MaxEntries,

        /// <summary>The intent is not a valid order. Not NSF.</summary>
        Invalid,

        /// <summary>The intent expired. Not NSF.</summary>
        Expired,

        /// <summary>The intent was canceled. Not NSF.</summary>
        Canceled,

        /// <summary>The native model vetoed. Not NSF, and it is not reclassified by the message text.</summary>
        BrokerVeto
    }

    /// <summary>Which cap a slice is counting.</summary>
    public enum WLAdmissionPhase
    {
        /// <summary>Emitted entry intents after ranking.</summary>
        Signal,

        /// <summary>Entries accepted for submission.</summary>
        Execution
    }

    /// <summary>What the caller is asking the policy to do with one intent.</summary>
    public enum WLAdmissionGate
    {
        /// <summary>An entry. Caps, capacity, and NSF apply.</summary>
        Entry,

        /// <summary>An exit. It does not consume entry caps or become NSF.</summary>
        Exit,

        /// <summary>Invalid order. Not NSF. Releases a reservation.</summary>
        Invalid,

        /// <summary>Expired intent. Not NSF. Releases a reservation.</summary>
        Expired,

        /// <summary>Canceled intent. Not NSF. Releases a reservation.</summary>
        Canceled,

        /// <summary>Native veto supplied by the caller. Not NSF.</summary>
        BrokerVeto
    }

    /// <summary>
    /// One candidate. Filled exposure is Lean holdings only; pending exposure is tracked by <see cref="WLPortfolioAdmission"/>.
    /// </summary>
    public readonly record struct WLAdmissionContext(
        Symbol Symbol,
        long IntentId,
        WLAdmissionGate Gate,
        PositionType Side,
        decimal Quantity,
        decimal AccountCurrencyExposure,
        decimal EstimatedFee,
        decimal Equity,
        decimal GrossFilledExposure,
        IReadOnlyList<Symbol> HoldingSymbols,
        long SliceId,
        WLAdmissionPhase Phase,
        bool NativeVeto = false);

    /// <summary>The policy result. <see cref="IsNsf"/> is the classification, independent of whether the counter already moved.</summary>
    public readonly record struct WLAdmissionDecision(bool Admitted, WLAdmissionReason Reason, bool IsNsf);

    /// <summary>A hypothetical position. It is not a Lean holding and closing it submits nothing.</summary>
    public sealed record WLNsfPosition(
        long IntentId,
        Symbol Symbol,
        PositionType Side,
        decimal Quantity,
        decimal AccountCurrencyExposure,
        WLAdmissionReason Reason);

    /// <summary>An open entry order the caller read from Lean, already mapped to its intent.</summary>
    public readonly record struct WLPendingOrder(
        long IntentId,
        Symbol Symbol,
        PositionType Side,
        decimal RemainingQuantity,
        decimal ReservedExposure);

    /// <summary>
    /// Admission, reservations, and the NSF ledger.
    /// Retention changes only this ledger. It does not change Lean holdings, equity, fills, or <c>HasOpenPosition</c>.
    /// </summary>
    public sealed class WLPortfolioAdmission
    {
        private readonly Dictionary<long, Reservation> _reservations = new();
        private readonly Dictionary<long, WLNsfPosition> _openNsf = new();
        private readonly HashSet<long> _countedNsf = new();
        private long _sliceId = long.MinValue;
        private int _signals;
        private int _entries;
        private bool _cashApplied;

        /// <summary>Creates an admission book for one rule set.</summary>
        public WLPortfolioAdmission(WLPortfolioRules rules)
        {
            ArgumentNullException.ThrowIfNull(rules);
            rules.Validate();
            Rules = rules;
        }

        /// <summary>The rules supplied at construction.</summary>
        public WLPortfolioRules Rules { get; }

        /// <summary>Executable intents rejected for buying power or max-open, once each.</summary>
        public int NsfCount { get; private set; }

        /// <summary>Exposure still reserved for accepted pending entries, including fee estimates.</summary>
        public decimal ReservedExposure
        {
            get
            {
                var reserved = 0m;
                foreach (var reservation in _reservations.Values)
                {
                    reserved += reservation.ReservedExposure;
                }

                return reserved;
            }
        }

        /// <summary>
        /// Sets starting capital once, then sets equity leverage on a margin account.
        /// Call this after the brokerage model and the securities exist. Futures are skipped.
        /// A cash account with a factor above 1 is rejected.
        /// </summary>
        public void Apply(QCAlgorithm algorithm)
        {
            ArgumentNullException.ThrowIfNull(algorithm);
            Rules.Validate();
            if (Rules.StartingCapital is decimal capital && !_cashApplied)
            {
                algorithm.SetCash(capital);
                _cashApplied = true;
            }

            if (Rules.MarginFactor is not decimal factor)
            {
                return;
            }

            if (factor > 1m && algorithm.BrokerageModel.AccountType != AccountType.Margin)
            {
                throw new InvalidOperationException(
                    "A margin factor above 1 requires a margin account. Futures are not given a leverage setting.");
            }

            if (algorithm.BrokerageModel.AccountType != AccountType.Margin)
            {
                return;
            }

            foreach (var security in algorithm.Securities.Values)
            {
                if (security.Type != SecurityType.Equity)
                {
                    continue;
                }

                security.SetLeverage(factor);
            }
        }

        /// <summary>Replaces pending reservations with the caller's current Lean entry orders.</summary>
        public void RestorePending(IReadOnlyList<WLPendingOrder> orders)
        {
            ArgumentNullException.ThrowIfNull(orders);
            _reservations.Clear();
            for (var index = 0; index < orders.Count; index++)
            {
                var order = orders[index];
                if (order.RemainingQuantity <= 0m || order.ReservedExposure < 0m)
                {
                    throw new ArgumentOutOfRangeException(nameof(orders), "A pending order needs a positive remaining quantity and a non-negative reserve.");
                }

                _reservations[order.IntentId] = new Reservation(
                    order.IntentId,
                    order.Symbol,
                    order.Side,
                    order.RemainingQuantity,
                    order.ReservedExposure,
                    CountedSignal: true,
                    CountedEntry: true);
            }
        }

        /// <summary>Shrinks a reservation by a partial fill. The filled part is no longer reserved.</summary>
        public void NotePartialFill(long intentId, decimal filledQuantity)
        {
            if (filledQuantity <= 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(filledQuantity), "A partial fill quantity must be positive.");
            }

            if (!_reservations.TryGetValue(intentId, out var reservation))
            {
                throw new InvalidOperationException($"Intent {intentId} has no pending reservation to fill.");
            }

            if (filledQuantity > reservation.RemainingQuantity)
            {
                throw new ArgumentOutOfRangeException(nameof(filledQuantity), "A fill cannot exceed the remaining reservation.");
            }

            var remaining = reservation.RemainingQuantity - filledQuantity;
            if (remaining == 0m)
            {
                _reservations.Remove(intentId);
                return;
            }

            var reserved = reservation.ReservedExposure * remaining / reservation.RemainingQuantity;
            _reservations[intentId] = reservation with
            {
                RemainingQuantity = remaining,
                ReservedExposure = reserved
            };
        }

        /// <summary>Drops any remainder after a terminal event. This does not record NSF.</summary>
        public void Release(long intentId)
        {
            _reservations.Remove(intentId);
        }

        /// <summary>
        /// Drops a retained hypothetical position. No order is submitted and the NSF count stays.
        /// </summary>
        public bool CloseNsf(long intentId)
        {
            return _openNsf.Remove(intentId);
        }

        /// <summary>True when retention is holding an open hypothetical for the symbol.</summary>
        public bool HasRetainedNsf(Symbol symbol)
        {
            ArgumentNullException.ThrowIfNull(symbol);
            foreach (var position in _openNsf.Values)
            {
                if (position.Symbol == symbol)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Retained hypotheticals for one symbol, in intent order. Empty when retention is off.</summary>
        public IReadOnlyList<WLNsfPosition> OpenNsf(Symbol symbol)
        {
            ArgumentNullException.ThrowIfNull(symbol);
            var matches = new List<WLNsfPosition>();
            foreach (var position in _openNsf.Values)
            {
                if (position.Symbol == symbol)
                {
                    matches.Add(position);
                }
            }

            matches.Sort(static (left, right) => left.IntentId.CompareTo(right.IntentId));
            return matches;
        }

        /// <summary>Admits or rejects one intent and updates reservations and the NSF ledger.</summary>
        public WLAdmissionDecision Evaluate(in WLAdmissionContext candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate.Symbol);
            ArgumentNullException.ThrowIfNull(candidate.HoldingSymbols);
            if (!Enum.IsDefined(candidate.Gate) || !Enum.IsDefined(candidate.Phase) || !Enum.IsDefined(candidate.Side))
            {
                throw new ArgumentOutOfRangeException(nameof(candidate), "Unknown admission gate, phase, or side.");
            }

            if (candidate.Quantity < 0m || candidate.AccountCurrencyExposure < 0m || candidate.EstimatedFee < 0m || candidate.GrossFilledExposure < 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(candidate), "Quantity, exposure, fees, and filled exposure cannot be negative.");
            }

            ResetSlice(candidate.SliceId);
            if (candidate.NativeVeto || candidate.Gate == WLAdmissionGate.BrokerVeto)
            {
                Release(candidate.IntentId);
                return Reject(WLAdmissionReason.BrokerVeto);
            }

            if (candidate.Gate is WLAdmissionGate.Invalid or WLAdmissionGate.Expired or WLAdmissionGate.Canceled)
            {
                Release(candidate.IntentId);
                return Reject(ReasonFor(candidate.Gate));
            }

            if (candidate.Gate == WLAdmissionGate.Exit)
            {
                return Accept();
            }

            if (candidate.Quantity == 0m)
            {
                return Reject(WLAdmissionReason.Invalid);
            }

            if (_reservations.TryGetValue(candidate.IntentId, out var existing))
            {
                return EvaluateReserved(candidate, existing);
            }

            if (candidate.Phase == WLAdmissionPhase.Signal && OverCap(Rules.MaxSignalsPerBar, _signals))
            {
                return Reject(WLAdmissionReason.MaxSignals);
            }

            if (candidate.Phase == WLAdmissionPhase.Execution && OverCap(Rules.MaxEntriesPerBar, _entries))
            {
                return Reject(WLAdmissionReason.MaxEntries);
            }

            var occupied = SymbolIsOccupied(candidate.Symbol, candidate.HoldingSymbols);
            if (!occupied && OverCap(Rules.MaxOpenPositions, OccupiedCount(candidate.HoldingSymbols)))
            {
                return RecordNsf(candidate, WLAdmissionReason.MaxOpen);
            }

            var cost = candidate.AccountCurrencyExposure + candidate.EstimatedFee;
            if (cost > RemainingCapacity(candidate.Equity, candidate.GrossFilledExposure))
            {
                return RecordNsf(candidate, WLAdmissionReason.BuyingPower);
            }

            _reservations.Add(candidate.IntentId, new Reservation(
                candidate.IntentId,
                candidate.Symbol,
                candidate.Side,
                candidate.Quantity,
                cost,
                CountedSignal: candidate.Phase == WLAdmissionPhase.Signal,
                CountedEntry: candidate.Phase == WLAdmissionPhase.Execution));
            Count(candidate.Phase);
            return Accept();
        }

        private WLAdmissionDecision EvaluateReserved(in WLAdmissionContext candidate, Reservation existing)
        {
            if (candidate.Phase == WLAdmissionPhase.Execution && !existing.CountedEntry && OverCap(Rules.MaxEntriesPerBar, _entries))
            {
                Release(candidate.IntentId);
                return Reject(WLAdmissionReason.MaxEntries);
            }

            if (candidate.Phase == WLAdmissionPhase.Signal && !existing.CountedSignal)
            {
                existing = existing with { CountedSignal = true };
                _reservations[candidate.IntentId] = existing;
                _signals++;
            }

            if (candidate.Phase == WLAdmissionPhase.Execution && !existing.CountedEntry)
            {
                _reservations[candidate.IntentId] = existing with { CountedEntry = true };
                _entries++;
            }

            return Accept();
        }

        private WLAdmissionDecision RecordNsf(in WLAdmissionContext candidate, WLAdmissionReason reason)
        {
            if (_countedNsf.Add(candidate.IntentId))
            {
                NsfCount++;
            }

            if (Rules.RetainNsf && !_openNsf.ContainsKey(candidate.IntentId))
            {
                _openNsf.Add(candidate.IntentId, new WLNsfPosition(
                    candidate.IntentId,
                    candidate.Symbol,
                    candidate.Side,
                    candidate.Quantity,
                    candidate.AccountCurrencyExposure,
                    reason));
            }

            return new WLAdmissionDecision(false, reason, true);
        }

        private decimal RemainingCapacity(decimal equity, decimal grossFilled)
        {
            if (equity <= 0m)
            {
                return 0m;
            }

            return Math.Max(0m, equity * Rules.OverlayFactor - grossFilled - ReservedExposure);
        }

        private bool SymbolIsOccupied(Symbol symbol, IReadOnlyList<Symbol> holdings)
        {
            for (var index = 0; index < holdings.Count; index++)
            {
                if (holdings[index] == symbol)
                {
                    return true;
                }
            }

            foreach (var reservation in _reservations.Values)
            {
                if (reservation.RemainingQuantity > 0m && reservation.Symbol == symbol)
                {
                    return true;
                }
            }

            return false;
        }

        private int OccupiedCount(IReadOnlyList<Symbol> holdings)
        {
            var symbols = new HashSet<Symbol>();
            for (var index = 0; index < holdings.Count; index++)
            {
                symbols.Add(holdings[index]);
            }

            foreach (var reservation in _reservations.Values)
            {
                if (reservation.RemainingQuantity > 0m)
                {
                    symbols.Add(reservation.Symbol);
                }
            }

            return symbols.Count;
        }

        private void Count(WLAdmissionPhase phase)
        {
            if (phase == WLAdmissionPhase.Signal)
            {
                _signals++;
                return;
            }

            _entries++;
        }

        private void ResetSlice(long sliceId)
        {
            if (sliceId == _sliceId)
            {
                return;
            }

            _sliceId = sliceId;
            _signals = 0;
            _entries = 0;
        }

        private static WLAdmissionDecision Accept()
        {
            return new WLAdmissionDecision(true, WLAdmissionReason.Admitted, false);
        }

        private static WLAdmissionDecision Reject(WLAdmissionReason reason)
        {
            return new WLAdmissionDecision(false, reason, false);
        }

        private static WLAdmissionReason ReasonFor(WLAdmissionGate gate)
        {
            return gate switch
            {
                WLAdmissionGate.Invalid => WLAdmissionReason.Invalid,
                WLAdmissionGate.Expired => WLAdmissionReason.Expired,
                WLAdmissionGate.Canceled => WLAdmissionReason.Canceled,
                WLAdmissionGate.BrokerVeto => WLAdmissionReason.BrokerVeto,
                _ => throw new ArgumentOutOfRangeException(nameof(gate))
            };
        }

        private static bool OverCap(int cap, int used)
        {
            return cap > 0 && used >= cap;
        }

        private readonly record struct Reservation(
            long IntentId,
            Symbol Symbol,
            PositionType Side,
            decimal RemainingQuantity,
            decimal ReservedExposure,
            bool CountedSignal,
            bool CountedEntry);
    }

    /// <summary>Mark selection and Lean exposure reads shared by callers of the admission policy.</summary>
    public static class WLPortfolioMarks
    {
        /// <summary>
        /// Uses the last observable mark, or an eligible open for a next-open basis.
        /// A missing open is an error rather than a silent substitution of the bar close.
        /// </summary>
        public static decimal Select(decimal lastObservable, decimal? eligibleOpen, bool nextOpen)
        {
            if (nextOpen)
            {
                if (eligibleOpen is not decimal open || open <= 0m)
                {
                    throw new ArgumentException(
                        "Next-open exposure uses an eligible open. The bar close is not a substitute.",
                        nameof(eligibleOpen));
                }

                return open;
            }

            if (lastObservable <= 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(lastObservable), "The last observable mark must be positive.");
            }

            return lastObservable;
        }

        /// <summary>
        /// Account-currency filled exposure. Futures contribute Lean initial margin, not gross notional.
        /// </summary>
        public static decimal FilledExposure(Security security)
        {
            ArgumentNullException.ThrowIfNull(security);
            var quantity = security.Holdings.Quantity;
            if (quantity == 0m)
            {
                return 0m;
            }

            if (security.Type == SecurityType.Future)
            {
                return Math.Abs(security.BuyingPowerModel.GetInitialMarginRequirement(security, quantity));
            }

            return security.Holdings.AbsoluteHoldingsValue;
        }
    }
}
