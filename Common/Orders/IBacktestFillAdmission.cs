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

namespace QuantConnect.Orders
{
    /// <summary>
    /// Marks orders submitted through the WL transaction path. Unmarked orders keep native behavior.
    /// </summary>
    public interface IWLTransactionOrderMarker
    {
        /// <summary>Stable intent identifier returned by <c>QueueWLTrade</c>.</summary>
        long IntentId { get; }

        /// <summary>UTC time of the signal slice that queued the intent.</summary>
        DateTime SignalUtcTime { get; }

        /// <summary>Exchange-local submission instant carried to fill models.</summary>
        DateTime SubmittedLocalTime { get; }

        /// <summary>When true, market orders fill at the open of the next real bar.</summary>
        bool NextOpenFill { get; }

        /// <summary>Bracket or OCO child tied to a parent entry fill.</summary>
        bool ProtectiveChild { get; }

        /// <summary>Parent order id for protective children; zero when none.</summary>
        int ParentOrderId { get; }
    }

    /// <summary>
    /// Optional backtest callback invoked for marked WL transaction orders when a native fill
    /// candidate is known (price and fees) but before the brokerage books the event.
    /// </summary>
    public interface IBacktestFillAdmission
    {
        /// <summary>
        /// Returns whether the candidate fill may be booked. False publishes a terminal non-fill
        /// and releases any WL reservation tied to the intent.
        /// </summary>
        bool AdmitFill(Order order, in OrderEvent candidate);
    }
}