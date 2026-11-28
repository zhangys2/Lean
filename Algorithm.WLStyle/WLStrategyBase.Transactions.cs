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
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Orders.Fills;
using QuantConnect.Securities;

namespace QuantConnect.Algorithm.WLStyle
{
    /// <summary>Opaque intent identifier returned by <see cref="WLStrategyBase.QueueWLTrade"/>.</summary>
    public readonly record struct WLIntentId(long Value);

    /// <summary>Lifecycle states for a queued WL trade intent.</summary>
    public enum WLIntentStatus
    {
        Queued,
        Submitted,
        Partial,
        Filled,
        Canceled,
        Expired,
        Invalid,
        BrokerVeto,
        Nsf
    }

    /// <summary>Whether the intent opens or closes risk.</summary>
    public enum WLIntentSide
    {
        Entry,
        Exit
    }

    /// <summary>Snapshot of one intent visible to strategy code.</summary>
    public sealed record WLIntentState(
        WLIntentId Id,
        WLIntentStatus Status,
        int? OrderId,
        WLAdmissionReason? AdmissionReason);

    /// <summary>Opt-in transaction configuration. Defaults keep native <c>PlaceTrade</c> behavior.</summary>
    public sealed record WLTransactionOptions(
        WLPortfolioRules PortfolioRules,
        int EntryExpiryBars = 1,
        decimal? EntryVolumePercent = null);

    /// <summary>Per-trade specification submitted through <see cref="WLStrategyBase.QueueWLTrade"/>.</summary>
    public sealed record WLTradeIntent(
        WLIntentSide Side,
        TransactionType TransactionType,
        OrderType OrderType,
        decimal? Quantity = null,
        WLSizeRule? SizeRule = null,
        WLBasisPrice BasisPrice = WLBasisPrice.SignalClose,
        decimal LimitPrice = 0m,
        decimal StopPrice = 0m,
        decimal? RiskStop = null,
        decimal? BracketTakeProfit = null,
        decimal? BracketStopLoss = null,
        decimal? BracketStopLimit = null,
        decimal Weight = 0m,
        string Tag = "",
        long DeferredSliceId = 0);

    /// <summary>Marks WL transaction orders for fill admission and brokerage guards.</summary>
    public sealed class WLOrderProperties : OrderProperties, IWLTransactionOrderMarker
    {
        public WLOrderProperties(
            long intentId,
            DateTime signalUtcTime,
            DateTime submittedLocalTime,
            bool nextOpenFill,
            bool protectiveChild = false,
            int parentOrderId = 0)
        {
            IntentId = intentId;
            SignalUtcTime = signalUtcTime;
            SubmittedLocalTime = submittedLocalTime;
            NextOpenFill = nextOpenFill;
            ProtectiveChild = protectiveChild;
            ParentOrderId = parentOrderId;
        }

        public long IntentId { get; }
        public DateTime SignalUtcTime { get; }
        public DateTime SubmittedLocalTime { get; }
        public bool NextOpenFill { get; }
        public bool ProtectiveChild { get; }
        public int ParentOrderId { get; }

        public override IOrderProperties Clone()
        {
            return (WLOrderProperties)MemberwiseClone();
        }
    }

    public abstract partial class WLStrategyBase : IBacktestFillAdmission
    {
        private WLPortfolioAdmission _wlAdmission;
        private WLTransactionOptions _wlOptions;
        private long _wlNextIntentId = 1;
        private long _wlSliceId;
        private bool _wlTransactionsEnabled;
        private bool _wlWithinSignalSlice;
        private readonly List<WLQueuedIntent> _wlQueued = new();
        private readonly List<WLQueuedIntent> _wlFrozen = new();
        private readonly Dictionary<long, WLQueuedIntent> _wlIntents = new();
        private readonly Dictionary<int, long> _wlOrderToIntent = new();
        private readonly Dictionary<Symbol, decimal> _wlExitReserved = new();
        private readonly Dictionary<Symbol, List<(OrderTicket Ticket, DateTime SubmittedLocalTime)>> _wlExpiring = new();
        private readonly HashSet<int> _wlParentFilledThisScan = new();
        private readonly HashSet<Symbol> _wlFillWrapped = new();

        /// <summary>Enables the WL transaction queue for this strategy instance.</summary>
        protected void EnableWLTransactions(WLTransactionOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.PortfolioRules.Validate();
            if (options.EntryExpiryBars < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Entry expiry cannot be negative.");
            }

            _wlOptions = options;
            _wlAdmission = new WLPortfolioAdmission(options.PortfolioRules);
            _wlAdmission.Apply(this);
            _wlTransactionsEnabled = true;
            foreach (var security in Securities.Values)
            {
                EnsureWlFillModel(security.Symbol);
            }
        }

        /// <summary>Queues one trade for the end of the current signal slice. Requires opt-in.</summary>
        protected WLIntentId QueueWLTrade(BarHistory bars, WLTradeIntent intent)
        {
            EnsureTransactionsEnabled();
            ArgumentNullException.ThrowIfNull(bars);
            ArgumentNullException.ThrowIfNull(intent);
            if (!_wlWithinSignalSlice)
            {
                throw new InvalidOperationException("QueueWLTrade is only valid during the signal slice.");
            }

            if (intent.Side == WLIntentSide.Exit
                && intent.OrderType is OrderType.MarketOnOpen or OrderType.MarketOnClose)
            {
                throw new ArgumentException("Exits cannot use market-on-open or market-on-close.", nameof(intent));
            }

            if (intent.Side == WLIntentSide.Entry
                && intent.OrderType is OrderType.MarketOnOpen or OrderType.MarketOnClose
                && intent.BasisPrice == WLBasisPrice.NextRealOpen)
            {
                throw new ArgumentException("Next-open sizing is prohibited for market-on-open and market-on-close entries.", nameof(intent));
            }

            if (_wlOptions.EntryVolumePercent.HasValue
                && intent.Side == WLIntentSide.Entry
                && intent.OrderType is not (OrderType.Market or OrderType.Limit or OrderType.StopMarket or OrderType.StopLimit))
            {
                throw new ArgumentException("Entry-bar volume caps apply only to intraday entry types.", nameof(intent));
            }

            if (intent.Quantity is null or <= 0m && intent.SizeRule is null)
            {
                throw new ArgumentException("Supply a positive quantity or a size rule.", nameof(intent));
            }

            var id = _wlNextIntentId++;
            var queued = new WLQueuedIntent(
                id,
                bars.Symbol,
                intent,
                EmissionIndex: _wlQueued.Count,
                bars.DateTimes[bars.Count - 1]);
            _wlQueued.Add(queued);
            _wlIntents[id] = queued with { Status = WLIntentStatus.Queued };
            return new WLIntentId(id);
        }

        /// <summary>Reads the current lifecycle snapshot for one intent.</summary>
        protected WLIntentState GetWLIntent(WLIntentId id)
        {
            EnsureTransactionsEnabled();
            if (!_wlIntents.TryGetValue(id.Value, out var intent))
            {
                throw new KeyNotFoundException($"Unknown WL intent {id.Value}.");
            }

            return ToState(intent);
        }

        /// <summary>Requests cancellation of a queued or working intent.</summary>
        protected bool CancelWLIntent(WLIntentId id)
        {
            EnsureTransactionsEnabled();
            if (!_wlIntents.TryGetValue(id.Value, out var intent))
            {
                return false;
            }

            if (intent.Status is WLIntentStatus.Filled or WLIntentStatus.Nsf or WLIntentStatus.BrokerVeto
                or WLIntentStatus.Invalid or WLIntentStatus.Expired or WLIntentStatus.Canceled)
            {
                return false;
            }

            if (intent.Status == WLIntentStatus.Queued)
            {
                _wlQueued.RemoveAll(item => item.Id == id.Value);
                _wlIntents[id.Value] = intent with { Status = WLIntentStatus.Canceled };
                return true;
            }

            if (intent.OrderId is int orderId)
            {
                var ticket = Transactions.GetOrderTicket(orderId);
                if (ticket != null && !ticket.Status.IsClosed())
                {
                    ticket.Cancel("WL intent canceled.");
                }
            }

            ReleaseExitReservation(intent);
            _wlAdmission.Release(id.Value);
            _wlIntents[id.Value] = intent with { Status = WLIntentStatus.Canceled };
            return true;
        }

        /// <summary>Returns retained NSF hypotheticals for a symbol. Empty when retention is off.</summary>
        protected IReadOnlyList<WLNsfPosition> OpenWLNsfPositions(BarHistory bars)
        {
            EnsureTransactionsEnabled();
            ArgumentNullException.ThrowIfNull(bars);
            return _wlAdmission.OpenNsf(bars.Symbol);
        }

        /// <summary>Drops a retained NSF hypothetical without submitting an order.</summary>
        protected bool CloseWLNsfPosition(WLIntentId id)
        {
            EnsureTransactionsEnabled();
            return _wlAdmission.CloseNsf(id.Value);
        }

        bool IBacktestFillAdmission.AdmitFill(Order order, in OrderEvent candidate)
        {
            if (!_wlTransactionsEnabled || order.Properties is not WLOrderProperties marker)
            {
                return true;
            }

            if (candidate.FillQuantity == 0m)
            {
                return true;
            }

            if (!_wlIntents.TryGetValue(marker.IntentId, out var intent))
            {
                return true;
            }

            var gate = intent.Planned.Side == WLIntentSide.Exit ? WLAdmissionGate.Exit : WLAdmissionGate.Entry;
            var security = Securities[order.Symbol];
            var exposure = Math.Abs(candidate.FillQuantity) * candidate.FillPrice * security.QuoteCurrency.ConversionRate
                * security.SymbolProperties.ContractMultiplier;
            if (security.Type == SecurityType.Future)
            {
                exposure = Math.Abs(security.BuyingPowerModel.GetInitialMarginRequirement(security, candidate.FillQuantity));
            }

            var fee = candidate.OrderFee.Value.Amount;
            var context = BuildAdmissionContext(
                intent,
                security,
                Math.Abs(candidate.FillQuantity),
                exposure,
                fee,
                WLAdmissionPhase.Execution,
                gate);
            var decision = _wlAdmission.Evaluate(context);
            if (decision.Admitted)
            {
                return true;
            }

            PublishTerminalNonFill(order, intent, decision.Reason);
            return false;
        }

        partial void DispatchWLTransactions(Slice slice, ref bool needsOpeningScan)
        {
            if (!_wlTransactionsEnabled)
            {
                return;
            }

            _wlParentFilledThisScan.Clear();
            var due = new List<WLQueuedIntent>();
            foreach (var intent in _wlIntents.Values)
            {
                if (intent.SliceId == 0)
                {
                    continue;
                }

                if (intent.Status is WLIntentStatus.Nsf or WLIntentStatus.Invalid or WLIntentStatus.BrokerVeto
                    or WLIntentStatus.Canceled or WLIntentStatus.Expired or WLIntentStatus.Filled
                    or WLIntentStatus.Submitted or WLIntentStatus.Partial)
                {
                    continue;
                }

                if (!slice.Bars.TryGetValue(intent.Symbol, out var bar) || bar.IsFillForward)
                {
                    continue;
                }

                if (intent.Planned.OrderType is OrderType.MarketOnOpen or OrderType.MarketOnClose)
                {
                    continue;
                }

                if (intent.DeferredUntilSliceId > intent.SliceId && intent.DeferredUntilSliceId > _wlSliceId)
                {
                    continue;
                }

                due.Add(intent);
            }

            foreach (var intent in due)
            {
                if (TrySubmitIntent(intent, slice, out var submittedScan))
                {
                    needsOpeningScan |= submittedScan;
                }
            }
        }

        partial void ExpireWLOrdersBeforeUserCode(Slice slice)
        {
            _wlWithinSignalSlice = _wlTransactionsEnabled;
            if (!_wlTransactionsEnabled)
            {
                return;
            }

            foreach (var pair in slice.Bars)
            {
                if (pair.Value.IsFillForward || !_histories.TryGetValue(pair.Key, out var history))
                {
                    continue;
                }

                ExpireWlConditionalOrders(history);
            }
        }

        partial void CloseWLSignals(Slice slice)
        {
            if (!_wlTransactionsEnabled)
            {
                _wlWithinSignalSlice = false;
                return;
            }

            _wlSliceId = slice.Time.Ticks;
            FreezeQueuedIntents(slice);
            SubmitSessionOrders(slice);
            _wlQueued.Clear();
            _wlWithinSignalSlice = false;
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            base.OnOrderEvent(orderEvent);
            if (!_wlTransactionsEnabled || orderEvent.OrderId == 0)
            {
                return;
            }

            if (!_wlOrderToIntent.TryGetValue(orderEvent.OrderId, out var intentId)
                || !_wlIntents.TryGetValue(intentId, out var intent))
            {
                return;
            }

            if (orderEvent.Status == OrderStatus.Filled || orderEvent.Status == OrderStatus.PartiallyFilled)
            {
                if (orderEvent.FillQuantity != 0m)
                {
                    _wlAdmission.NotePartialFill(intentId, Math.Abs(orderEvent.FillQuantity));
                }

                if (orderEvent.Status == OrderStatus.Filled)
                {
                    _wlAdmission.Release(intentId);
                    ReleaseExitReservation(intent);
                    _wlIntents[intentId] = intent with
                    {
                        Status = WLIntentStatus.Filled,
                        OrderId = orderEvent.OrderId
                    };
                    _wlParentFilledThisScan.Add(orderEvent.OrderId);
                    SubmitBracketChildrenIfNeeded(intent, orderEvent);
                }
                else
                {
                    _wlIntents[intentId] = intent with
                    {
                        Status = WLIntentStatus.Partial,
                        OrderId = orderEvent.OrderId
                    };
                }

                return;
            }

            if (orderEvent.Status == OrderStatus.Canceled)
            {
                _wlAdmission.Evaluate(BuildAdmissionContext(intent, Securities[intent.Symbol], intent.SizedQuantity,
                    0m, 0m, WLAdmissionPhase.Execution, WLAdmissionGate.Canceled));
                ReleaseExitReservation(intent);
                _wlIntents[intentId] = intent with { Status = WLIntentStatus.Canceled, OrderId = orderEvent.OrderId };
                return;
            }

            if (orderEvent.Status == OrderStatus.Invalid)
            {
                var gate = intent.HadExecutablePrice ? WLAdmissionGate.Invalid : WLAdmissionGate.BrokerVeto;
                _wlAdmission.Evaluate(BuildAdmissionContext(intent, Securities[intent.Symbol], intent.SizedQuantity,
                    0m, 0m, WLAdmissionPhase.Execution, gate));
                ReleaseExitReservation(intent);
                _wlIntents[intentId] = intent with
                {
                    Status = gate == WLAdmissionGate.BrokerVeto ? WLIntentStatus.BrokerVeto : WLIntentStatus.Invalid,
                    OrderId = orderEvent.OrderId
                };
            }
        }

        private void FreezeQueuedIntents(Slice slice)
        {
            if (_wlQueued.Count == 0)
            {
                return;
            }

            var ordered = _wlQueued
                .OrderBy(item => item.Planned.Side == WLIntentSide.Exit ? 0 : 1)
                .ThenByDescending(item => item.Planned.Weight)
                .ThenBy(item => item.Symbol.ID.ToString(), StringComparer.Ordinal)
                .ThenBy(item => item.EmissionIndex)
                .ToList();

            foreach (var item in ordered)
            {
                if (!_histories.TryGetValue(item.Symbol, out var history))
                {
                    MarkIntent(item with { Status = WLIntentStatus.Invalid }, WLAdmissionReason.Invalid);
                    continue;
                }

                var idx = history.Count - 1;
                if (history.FillForward[idx])
                {
                    var deferred = item with { DeferredUntilSliceId = slice.Time.Ticks + 1 };
                    _wlFrozen.Add(deferred);
                    _wlIntents[item.Id] = deferred with { Status = WLIntentStatus.Queued };
                    continue;
                }

                if (!TrySizeIntent(history, item, slice, out var sized))
                {
                    continue;
                }

                var signalGate = sized.Planned.Side == WLIntentSide.Exit ? WLAdmissionGate.Exit : WLAdmissionGate.Entry;
                var security = Securities[sized.Symbol];
                var decision = _wlAdmission.Evaluate(BuildAdmissionContext(
                    sized,
                    security,
                    sized.SizedQuantity,
                    sized.Exposure,
                    sized.EstimatedFee,
                    WLAdmissionPhase.Signal,
                    signalGate));

                if (!decision.Admitted)
                {
                    MarkIntent(sized with { Status = decision.IsNsf ? WLIntentStatus.Nsf : WLIntentStatus.Invalid }, decision.Reason);
                    continue;
                }

                if (sized.SizedQuantity == 0m)
                {
                    MarkIntent(sized with { Status = WLIntentStatus.Invalid }, WLAdmissionReason.Invalid);
                    continue;
                }

                _wlFrozen.Add(sized with { Status = WLIntentStatus.Queued, SignalUtc = UtcTime, SliceId = _wlSliceId });
                _wlIntents[sized.Id] = sized with { Status = WLIntentStatus.Queued, SignalUtc = UtcTime, SliceId = _wlSliceId };
            }
        }

        private bool TrySizeIntent(BarHistory history, WLQueuedIntent item, Slice slice, out WLQueuedIntent sized)
        {
            sized = item;
            var idx = history.Count - 1;
            var close = (decimal)history.Close[idx];
            decimal? nextOpen = null;
            if (idx + 1 < history.Count)
            {
                nextOpen = (decimal)history.Open[idx + 1];
            }
            else if (slice.Bars.TryGetValue(item.Symbol, out var current) && !current.IsFillForward)
            {
                nextOpen = current.Open;
            }

            decimal basis;
            try
            {
                basis = WLPortfolioMarks.Select(close, nextOpen, item.Planned.BasisPrice == WLBasisPrice.NextRealOpen);
            }
            catch (Exception)
            {
                MarkIntent(item with { Status = WLIntentStatus.Invalid }, WLAdmissionReason.Invalid);
                return false;
            }

            var side = item.Planned.TransactionType is TransactionType.Buy or TransactionType.Cover
                ? WLTradeSide.Long
                : WLTradeSide.Short;
            decimal quantity;
            if (item.Planned.Quantity is decimal explicitQuantity)
            {
                quantity = explicitQuantity;
            }
            else
            {
                var security = Securities[item.Symbol];
                var rule = item.Planned.SizeRule ?? throw new InvalidOperationException("Size rule required.");
                var context = new WLSizeContext(
                    side,
                    item.FrozenEquity ?? Portfolio.TotalPortfolioValue,
                    Portfolio.Cash,
                    basis,
                    item.Planned.RiskStop,
                    security.QuoteCurrency.ConversionRate,
                    security.SymbolProperties.ContractMultiplier,
                    security.Type == SecurityType.Future
                        ? security.BuyingPowerModel.GetInitialMarginRequirement(security, 1)
                        : null,
                    security.SymbolProperties.ContractMultiplier);
                quantity = WLPositionSizing.Calculate(rule, in context);
            }

            decimal? volumeCap = null;
            if (item.Planned.Side == WLIntentSide.Entry && _wlOptions.EntryVolumePercent is decimal volumePercent)
            {
                if (history.FillForward[idx])
                {
                    throw new InvalidOperationException("Entry-bar volume is not a valid signal input.");
                }

                volumeCap = (decimal)history.Volume[idx];
            }

            quantity = WLPositionSizing.Constrain(
                quantity,
                Securities[item.Symbol].SymbolProperties.LotSize,
                null,
                volumeCap,
                volumeCap.HasValue ? _wlOptions.EntryVolumePercent : null);

            if (item.Planned.Side == WLIntentSide.Exit)
            {
                quantity = ClipExitQuantity(item.Symbol, item.Planned.TransactionType, quantity);
                if (quantity == 0m)
                {
                    MarkIntent(item with { Status = WLIntentStatus.Invalid }, WLAdmissionReason.Invalid);
                    return false;
                }

                ReserveExitQuantity(item.Symbol, quantity);
            }

            var exposure = quantity * basis * Securities[item.Symbol].QuoteCurrency.ConversionRate
                * Securities[item.Symbol].SymbolProperties.ContractMultiplier;
            sized = item with
            {
                SizedQuantity = quantity,
                Exposure = exposure,
                BasisPrice = basis,
                FrozenEquity = item.FrozenEquity ?? Portfolio.TotalPortfolioValue,
                EstimatedFee = EstimateFee(item.Symbol, quantity, basis)
            };
            return true;
        }

        private void SubmitSessionOrders(Slice slice)
        {
            foreach (var intent in _wlFrozen.ToList())
            {
                if (intent.Planned.OrderType is not (OrderType.MarketOnOpen or OrderType.MarketOnClose))
                {
                    continue;
                }

                if (intent.Status != WLIntentStatus.Queued || intent.SliceId != _wlSliceId)
                {
                    continue;
                }

                TrySubmitIntent(intent, slice, out _);
            }
        }

        private bool TrySubmitIntent(WLQueuedIntent intent, Slice slice, out bool needsScan)
        {
            needsScan = false;
            var security = Securities[intent.Symbol];
            var signedQuantity = SignedQuantity(intent.Planned.TransactionType, intent.SizedQuantity);
            var properties = new WLOrderProperties(
                intent.Id,
                intent.SignalUtc == default ? UtcTime : intent.SignalUtc,
                security.LocalTime,
                nextOpenFill: intent.Planned.OrderType == OrderType.Market
                    && intent.Planned.BasisPrice == WLBasisPrice.NextRealOpen);

            var gate = intent.Planned.Side == WLIntentSide.Exit ? WLAdmissionGate.Exit : WLAdmissionGate.Entry;
            var decision = _wlAdmission.Evaluate(BuildAdmissionContext(
                intent,
                security,
                intent.SizedQuantity,
                intent.Exposure,
                intent.EstimatedFee,
                WLAdmissionPhase.Execution,
                gate));
            if (!decision.Admitted)
            {
                MarkIntent(intent with { Status = decision.IsNsf ? WLIntentStatus.Nsf : WLIntentStatus.Invalid }, decision.Reason);
                ReleaseExitReservation(intent);
                return false;
            }

            var request = BuildSubmitRequest(intent, signedQuantity, properties, intent.SignalUtc == default ? UtcTime : intent.SignalUtc);
            EnsureWlFillModel(intent.Symbol);
            var ticket = SubmitOrderRequest(request);
            if (ticket.Status == OrderStatus.Invalid)
            {
                var vetoGate = intent.HadExecutablePrice ? WLAdmissionGate.Invalid : WLAdmissionGate.BrokerVeto;
                _wlAdmission.Evaluate(BuildAdmissionContext(intent, security, intent.SizedQuantity, 0m, 0m,
                    WLAdmissionPhase.Execution, vetoGate));
                ReleaseExitReservation(intent);
                MarkIntent(intent with
                {
                    Status = vetoGate == WLAdmissionGate.BrokerVeto ? WLIntentStatus.BrokerVeto : WLIntentStatus.Invalid,
                    OrderId = ticket.OrderId
                }, vetoGate == WLAdmissionGate.BrokerVeto ? WLAdmissionReason.BrokerVeto : WLAdmissionReason.Invalid);
                return false;
            }

            _wlOrderToIntent[ticket.OrderId] = intent.Id;
            TrackConditionalExpiry(intent, ticket, security.LocalTime);
            _wlIntents[intent.Id] = intent with
            {
                Status = WLIntentStatus.Submitted,
                OrderId = ticket.OrderId,
                HadExecutablePrice = HasExecutablePrice(intent.Planned.OrderType)
            };
            needsScan = intent.Planned.OrderType == OrderType.Market;
            return true;
        }

        private SubmitOrderRequest BuildSubmitRequest(
            WLQueuedIntent intent,
            decimal signedQuantity,
            WLOrderProperties properties,
            DateTime signalUtc)
        {
            var security = Securities[intent.Symbol];
            var asynchronous = intent.Planned.OrderType != OrderType.Market;
            properties.TimeInForce = DefaultOrderProperties?.TimeInForce ?? TimeInForce.GoodTilCanceled;
            return intent.Planned.OrderType switch
            {
                OrderType.Market => new SubmitOrderRequest(
                    OrderType.Market,
                    security.Type,
                    intent.Symbol,
                    signedQuantity,
                    0,
                    0,
                    0,
                    0,
                    false,
                    signalUtc,
                    intent.Planned.Tag,
                    properties,
                    asynchronous: true),
                OrderType.Limit => new SubmitOrderRequest(
                    OrderType.Limit,
                    security.Type,
                    intent.Symbol,
                    signedQuantity,
                    0,
                    intent.Planned.LimitPrice,
                    0,
                    0,
                    false,
                    signalUtc,
                    intent.Planned.Tag,
                    properties,
                    asynchronous: asynchronous),
                OrderType.StopMarket => new SubmitOrderRequest(
                    OrderType.StopMarket,
                    security.Type,
                    intent.Symbol,
                    signedQuantity,
                    intent.Planned.StopPrice,
                    0,
                    0,
                    0,
                    false,
                    signalUtc,
                    intent.Planned.Tag,
                    properties,
                    asynchronous: asynchronous),
                OrderType.StopLimit => new SubmitOrderRequest(
                    OrderType.StopLimit,
                    security.Type,
                    intent.Symbol,
                    signedQuantity,
                    intent.Planned.StopPrice,
                    intent.Planned.LimitPrice,
                    0,
                    0,
                    false,
                    signalUtc,
                    intent.Planned.Tag,
                    properties,
                    asynchronous: asynchronous),
                OrderType.MarketOnOpen => new SubmitOrderRequest(
                    OrderType.MarketOnOpen,
                    security.Type,
                    intent.Symbol,
                    signedQuantity,
                    0,
                    0,
                    0,
                    0,
                    false,
                    signalUtc,
                    intent.Planned.Tag,
                    properties),
                OrderType.MarketOnClose => new SubmitOrderRequest(
                    OrderType.MarketOnClose,
                    security.Type,
                    intent.Symbol,
                    signedQuantity,
                    0,
                    0,
                    0,
                    0,
                    false,
                    signalUtc,
                    intent.Planned.Tag,
                    properties),
                _ => throw new NotSupportedException($"WL transaction path does not support {intent.Planned.OrderType}.")
            };
        }

        private void SubmitBracketChildrenIfNeeded(WLQueuedIntent parent, OrderEvent fill)
        {
            if (parent.Planned.BracketStopLoss is null && parent.Planned.BracketTakeProfit is null)
            {
                return;
            }

            var security = Securities[parent.Symbol];
            var exitQuantity = -Math.Sign(fill.FillQuantity) * Math.Abs(fill.FillQuantity);
            var signalUtc = parent.SignalUtc == default ? UtcTime : parent.SignalUtc;
            var parentId = fill.OrderId;
            var childProps = new WLOrderProperties(parent.Id, signalUtc, security.LocalTime, false, true, parentId);
            SubmitOrderRequest entry;
            if (parent.Planned.BracketTakeProfit is decimal takeProfit && parent.Planned.BracketStopLoss is decimal stopLoss)
            {
                entry = new SubmitOrderRequest(
                    OrderType.Limit,
                    security.Type,
                    parent.Symbol,
                    exitQuantity,
                    0,
                    takeProfit,
                    0,
                    0,
                    false,
                    signalUtc,
                    parent.Planned.Tag,
                    childProps);
                entry = entry.Bracket(takeProfit, stopLoss, parent.Planned.BracketStopLimit);
            }
            else if (parent.Planned.BracketTakeProfit is decimal takeOnly)
            {
                entry = new SubmitOrderRequest(
                    OrderType.Limit,
                    security.Type,
                    parent.Symbol,
                    exitQuantity,
                    0,
                    takeOnly,
                    0,
                    0,
                    false,
                    signalUtc,
                    parent.Planned.Tag,
                    childProps);
            }
            else if (parent.Planned.BracketStopLoss is decimal stopOnly)
            {
                entry = parent.Planned.BracketStopLimit is decimal stopLimit
                    ? new SubmitOrderRequest(
                        OrderType.StopLimit,
                        security.Type,
                        parent.Symbol,
                        exitQuantity,
                        stopOnly,
                        stopLimit,
                        0,
                        0,
                        false,
                        signalUtc,
                        parent.Planned.Tag,
                        childProps)
                    : new SubmitOrderRequest(
                        OrderType.StopMarket,
                        security.Type,
                        parent.Symbol,
                        exitQuantity,
                        stopOnly,
                        0,
                        0,
                        0,
                        false,
                        signalUtc,
                        parent.Planned.Tag,
                        childProps);
            }
            else
            {
                return;
            }

            foreach (var ticket in Order(entry))
            {
                if (ticket.OrderId > 0)
                {
                    _wlOrderToIntent[ticket.OrderId] = parent.Id;
                }
            }
        }

        private void ExpireWlConditionalOrders(BarHistory history)
        {
            if (history.FillForward[history.Count - 1])
            {
                return;
            }

            if (!_wlExpiring.TryGetValue(history.Symbol, out var orders))
            {
                return;
            }

            for (var index = orders.Count - 1; index >= 0; index--)
            {
                var tracked = orders[index];
                if (tracked.Ticket.Status.IsClosed())
                {
                    orders.RemoveAt(index);
                    continue;
                }

                if (history.DateTimes[history.Count - 1] <= tracked.SubmittedLocalTime)
                {
                    continue;
                }

                tracked.Ticket.Cancel("WL conditional not filled on the next bar.");
                orders.RemoveAt(index);
                if (_wlOrderToIntent.TryGetValue(tracked.Ticket.OrderId, out var intentId)
                    && _wlIntents.TryGetValue(intentId, out var intent))
                {
                    _wlAdmission.Evaluate(BuildAdmissionContext(intent, Securities[intent.Symbol], intent.SizedQuantity,
                        0m, 0m, WLAdmissionPhase.Execution, WLAdmissionGate.Expired));
                    _wlIntents[intentId] = intent with { Status = WLIntentStatus.Expired };
                }
            }

            if (orders.Count == 0)
            {
                _wlExpiring.Remove(history.Symbol);
            }
        }

        private void TrackConditionalExpiry(WLQueuedIntent intent, OrderTicket ticket, DateTime submittedLocal)
        {
            if (intent.Planned.OrderType is OrderType.Market or OrderType.MarketOnOpen or OrderType.MarketOnClose)
            {
                return;
            }

            if (intent.Planned.Side == WLIntentSide.Entry && _wlOptions.EntryExpiryBars != 1)
            {
                return;
            }

            if (!_wlExpiring.TryGetValue(intent.Symbol, out var list))
            {
                list = new List<(OrderTicket, DateTime)>();
                _wlExpiring.Add(intent.Symbol, list);
            }

            list.Add((ticket, submittedLocal));
        }

        private WLAdmissionContext BuildAdmissionContext(
            WLQueuedIntent intent,
            Security security,
            decimal quantity,
            decimal exposure,
            decimal fee,
            WLAdmissionPhase phase,
            WLAdmissionGate gate)
        {
            var holdings = new List<Symbol>();
            var filled = 0m;
            foreach (var position in Securities.Values)
            {
                if (position.Holdings.Quantity == 0)
                {
                    continue;
                }

                holdings.Add(position.Symbol);
                filled += WLPortfolioMarks.FilledExposure(position);
            }

            var side = intent.Planned.TransactionType is TransactionType.Buy or TransactionType.Cover
                ? PositionType.Long
                : PositionType.Short;
            return new WLAdmissionContext(
                intent.Symbol,
                intent.Id,
                gate,
                side,
                quantity,
                exposure,
                fee,
                Portfolio.TotalPortfolioValue,
                filled,
                holdings,
                intent.SliceId == 0 ? _wlSliceId : intent.SliceId,
                phase);
        }

        private void MarkIntent(WLQueuedIntent intent, WLAdmissionReason reason)
        {
            var status = reason switch
            {
                WLAdmissionReason.BuyingPower or WLAdmissionReason.MaxOpen => WLIntentStatus.Nsf,
                WLAdmissionReason.BrokerVeto => WLIntentStatus.BrokerVeto,
                WLAdmissionReason.Canceled => WLIntentStatus.Canceled,
                WLAdmissionReason.Expired => WLIntentStatus.Expired,
                _ => WLIntentStatus.Invalid
            };

            _wlIntents[intent.Id] = intent with { Status = status, AdmissionReason = reason };
            if (!_wlFrozen.Any(item => item.Id == intent.Id))
            {
                _wlFrozen.Add(intent with { Status = status, AdmissionReason = reason });
            }
        }

        private void PublishTerminalNonFill(Order order, WLQueuedIntent intent, WLAdmissionReason reason)
        {
            _wlAdmission.Release(intent.Id);
            ReleaseExitReservation(intent);
            _wlIntents[intent.Id] = intent with
            {
                Status = WLIntentStatus.Nsf,
                OrderId = order.Id,
                AdmissionReason = reason
            };

            if (order.Status is not OrderStatus.Canceled and not OrderStatus.Invalid)
            {
                Transactions.CancelOrder(order.Id, "WL fill admission rejected.");
            }
        }

        private void ReleaseExitReservation(WLQueuedIntent intent)
        {
            if (intent.Planned.Side != WLIntentSide.Exit)
            {
                return;
            }

            if (_wlExitReserved.TryGetValue(intent.Symbol, out var reserved))
            {
                reserved = Math.Max(0m, reserved - intent.SizedQuantity);
                if (reserved == 0m)
                {
                    _wlExitReserved.Remove(intent.Symbol);
                }
                else
                {
                    _wlExitReserved[intent.Symbol] = reserved;
                }
            }
        }

        private void ReserveExitQuantity(Symbol symbol, decimal quantity)
        {
            _wlExitReserved[symbol] = _wlExitReserved.GetValueOrDefault(symbol) + quantity;
        }

        private decimal ClipExitQuantity(Symbol symbol, TransactionType side, decimal quantity)
        {
            var holding = Portfolio[symbol].Quantity;
            var available = side switch
            {
                TransactionType.Sell => Math.Max(0m, holding - _wlExitReserved.GetValueOrDefault(symbol)),
                TransactionType.Cover => Math.Max(0m, Math.Abs(holding) - _wlExitReserved.GetValueOrDefault(symbol)),
                _ => 0m
            };
            return Math.Min(quantity, available);
        }

        private static decimal SignedQuantity(TransactionType side, decimal quantity)
        {
            return side switch
            {
                TransactionType.Buy or TransactionType.Cover => quantity,
                TransactionType.Sell or TransactionType.Short => -quantity,
                _ => throw new ArgumentOutOfRangeException(nameof(side))
            };
        }

        private static bool HasExecutablePrice(OrderType orderType)
        {
            return orderType is not (OrderType.Market or OrderType.MarketOnOpen or OrderType.MarketOnClose);
        }

        private decimal EstimateFee(Symbol symbol, decimal quantity, decimal price)
        {
            var security = Securities[symbol];
            var template = new MarketOrder(symbol, quantity, UtcTime);
            return security.FeeModel.GetOrderFee(new OrderFeeParameters(security, template, price)).Value.Amount;
        }

        private static WLIntentState ToState(WLQueuedIntent intent)
        {
            return new WLIntentState(
                new WLIntentId(intent.Id),
                intent.Status,
                intent.OrderId,
                intent.AdmissionReason);
        }

        private void EnsureTransactionsEnabled()
        {
            if (!_wlTransactionsEnabled)
            {
                throw new InvalidOperationException("Call EnableWLTransactions before using WL transaction APIs.");
            }
        }

        private void EnsureWlFillModel(Symbol symbol)
        {
            if (!_wlFillWrapped.Add(symbol))
            {
                return;
            }

            var security = Securities[symbol];
            security.SetFillModel(new WLTransactionFillModel(security.FillModel));
        }

        private sealed class WLTransactionFillModel : FillModel
        {
            private readonly IFillModel _inner;

            public WLTransactionFillModel(IFillModel inner)
            {
                _inner = inner;
            }

            public override Fill Fill(FillModelParameters parameters)
            {
                if (parameters.Order.Properties is WLOrderProperties wl && wl.NextOpenFill
                    && parameters.Order.Type == OrderType.Market)
                {
                    var tradeBar = parameters.Security.Cache.GetData<TradeBar>();
                    if (tradeBar == null
                        || tradeBar.IsFillForward
                        || tradeBar.EndTime <= wl.SubmittedLocalTime)
                    {
                        var utcTime = parameters.Security.LocalTime.ConvertToUtc(parameters.Security.Exchange.TimeZone);
                        return new Fill(new OrderEvent(parameters.Order, utcTime, OrderFee.Zero));
                    }

                    var utc = parameters.Security.LocalTime.ConvertToUtc(parameters.Security.Exchange.TimeZone);
                    var fill = new OrderEvent(parameters.Order, utc, OrderFee.Zero);
                    if (parameters.Order.Status == OrderStatus.Canceled || !IsExchangeOpen(parameters.Security, false))
                    {
                        return new Fill(fill);
                    }

                    fill.FillPrice = tradeBar.Open;
                    var slippage = parameters.Security.SlippageModel.GetSlippageApproximation(parameters.Security, parameters.Order);
                    if (parameters.Order.Direction == OrderDirection.Buy)
                    {
                        fill.FillPrice += slippage;
                    }
                    else
                    {
                        fill.FillPrice -= slippage;
                    }

                    fill.FillQuantity = parameters.Order.Quantity;
                    fill.Status = OrderStatus.Filled;
                    return new Fill(fill);
                }

                return _inner.Fill(parameters);
            }
        }

        private sealed record WLQueuedIntent(
            long Id,
            Symbol Symbol,
            WLTradeIntent Planned,
            int EmissionIndex,
            DateTime SignalBarEnd,
            WLIntentStatus Status = WLIntentStatus.Queued,
            decimal SizedQuantity = 0m,
            decimal Exposure = 0m,
            decimal EstimatedFee = 0m,
            decimal BasisPrice = 0m,
            decimal? FrozenEquity = null,
            DateTime SignalUtc = default,
            long SliceId = 0,
            long DeferredUntilSliceId = 0,
            int? OrderId = null,
            WLAdmissionReason? AdmissionReason = null,
            bool HadExecutablePrice = false);
    }
}