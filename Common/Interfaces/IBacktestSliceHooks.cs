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

using QuantConnect.Data;

namespace QuantConnect.Interfaces
{
    /// <summary>
    /// Optional backtest callbacks around the data-slice scan that brackets user code.
    /// Algorithms that do not implement this interface are unchanged.
    /// </summary>
    public interface IBacktestSliceHooks
    {
        /// <summary>
        /// Called after the pre-OnData transaction scan and corporate-action callbacks,
        /// immediately before user data dispatch.
        /// </summary>
        /// <returns>True when an opted-in dispatch needs one more synchronous transaction pass before user code.</returns>
        bool AfterOpeningScan(Slice slice);

        /// <summary>
        /// Called after the post-OnData transaction scan and before result sampling.
        /// </summary>
        void AfterClosingScan(Slice slice);
    }
}
