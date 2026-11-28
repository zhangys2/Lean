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

namespace QuantConnect.Algorithm.WLStyle
{
    public enum WLSizeMode
    {
        Quantity,
        FixedValue,
        PercentEquity,
        MaxRiskPercent
    }

    public enum WLBasisPrice
    {
        SignalClose,
        NextRealOpen
    }

    public enum WLTradeSide
    {
        Long,
        Short
    }

    public sealed record WLSizeRule(WLSizeMode Mode, decimal Amount);

    /// <summary>
    /// Account-currency inputs used to calculate an unsigned order quantity.
    /// Cash is exposed to custom sizers; built-in equity allocations use equity.
    /// </summary>
    public readonly record struct WLSizeContext(
        WLTradeSide Side,
        decimal Equity,
        decimal? Cash,
        decimal BasisPrice,
        decimal? RiskStop = null,
        decimal FxRate = 1m,
        decimal ContractPointValue = 1m,
        decimal? FuturesInitialMarginPerContract = null,
        decimal ContractMultiplier = 1m);

    /// <summary>
    /// Pure position-sizing formulas and quantity constraints; contains no Lean engine dependencies.
    /// </summary>
    public static class WLPositionSizing
    {
        /// <summary>
        /// Calculates an unsigned quantity. Apply <see cref="Constrain"/> separately before submission.
        /// </summary>
        public static decimal Calculate(WLSizeRule rule, in WLSizeContext context)
        {
            return Calculate(rule, in context, null);
        }

        /// <summary>
        /// Calculates an unsigned quantity, optionally replacing the built-in formula with a custom sizer.
        /// The result remains subject to the same downstream <see cref="Constrain"/> operation.
        /// </summary>
        public static decimal Calculate(
            WLSizeRule rule,
            in WLSizeContext context,
            Func<WLSizeContext, decimal> customSizer)
        {
            ArgumentNullException.ThrowIfNull(rule);
            if (!Enum.IsDefined(rule.Mode))
            {
                throw new ArgumentOutOfRangeException(nameof(rule), "Unknown position-size mode.");
            }
            if (rule.Amount < 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(rule), "Position-size amount cannot be negative.");
            }
            if (!Enum.IsDefined(context.Side))
            {
                throw new ArgumentOutOfRangeException(nameof(context), "Unknown trade side.");
            }
            if (rule.Amount == 0m)
            {
                return 0m;
            }

            if (rule.Mode == WLSizeMode.MaxRiskPercent)
            {
                if (context.FxRate <= 0m || context.ContractPointValue <= 0m)
                {
                    throw new ArgumentOutOfRangeException(nameof(context), "FX and contract point value must be positive.");
                }
                if (context.RiskStop is null)
                {
                    throw new ArgumentException("Max-risk sizing requires an explicit risk stop.", nameof(context));
                }
                var stop = context.RiskStop.Value;
                var correctStopSide = context.Side == WLTradeSide.Long
                    ? stop < context.BasisPrice
                    : stop > context.BasisPrice;
                if (!correctStopSide)
                {
                    throw new ArgumentException("The risk stop must be below a long basis or above a short basis.", nameof(context));
                }
            }
            else if (rule.Mode is WLSizeMode.FixedValue or WLSizeMode.PercentEquity)
            {
                if (context.FxRate <= 0m || context.ContractMultiplier <= 0m)
                {
                    throw new ArgumentOutOfRangeException(nameof(context), "FX rate and contract multiplier must be positive.");
                }
                if (context.FuturesInitialMarginPerContract is <= 0m)
                {
                    throw new ArgumentOutOfRangeException(nameof(context), "Supplied futures margin must be positive.");
                }
            }
            if ((rule.Mode is WLSizeMode.PercentEquity or WLSizeMode.MaxRiskPercent)
                && context.Equity <= 0m)
            {
                return 0m;
            }

            if (customSizer is not null)
            {
                var customQuantity = customSizer(context);
                if (customQuantity < 0m)
                {
                    throw new ArgumentOutOfRangeException(nameof(customSizer), "A custom sizer cannot return a negative quantity.");
                }
                return customQuantity;
            }

            if (rule.Mode == WLSizeMode.Quantity)
            {
                return rule.Amount;
            }

            if (rule.Mode == WLSizeMode.MaxRiskPercent)
            {
                if (context.Equity <= 0m || context.BasisPrice <= 0m)
                {
                    return 0m;
                }

                var riskPerUnit = Math.Abs(context.BasisPrice - context.RiskStop!.Value)
                    * context.ContractPointValue
                    * context.FxRate;
                return riskPerUnit == 0m
                    ? 0m
                    : context.Equity * (rule.Amount / 100m) / riskPerUnit;
            }

            if (rule.Mode == WLSizeMode.PercentEquity && context.Equity <= 0m)
            {
                return 0m;
            }
            var allocation = rule.Mode == WLSizeMode.FixedValue ? rule.Amount : context.Equity * (rule.Amount / 100m);
            var unitCost = context.FuturesInitialMarginPerContract
                ?? context.BasisPrice * context.FxRate * context.ContractMultiplier;
            return unitCost <= 0m ? 0m : allocation / unitCost;
        }

        /// <summary>
        /// Applies an optional entry-volume cap, then rounds down to a valid lot increment.
        /// </summary>
        public static decimal Constrain(
            decimal quantity,
            decimal lotSize,
            decimal? roundLotSize,
            decimal? entryVolume,
            decimal? volumePercent)
        {
            if (quantity < 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity cannot be negative.");
            }
            if (lotSize <= 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(lotSize), "Native lot size must be positive.");
            }
            if (roundLotSize is <= 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(roundLotSize), "A supplied round lot must be positive.");
            }
            if (roundLotSize.HasValue && roundLotSize.Value % lotSize != 0m)
            {
                throw new ArgumentException("A round lot must be a multiple of the native lot size.", nameof(roundLotSize));
            }
            if (entryVolume is < 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(entryVolume), "Entry volume cannot be negative.");
            }
            if (volumePercent is < 0m or > 100m)
            {
                throw new ArgumentOutOfRangeException(nameof(volumePercent), "Volume percent must be between 0 and 100.");
            }
            if (entryVolume.HasValue != volumePercent.HasValue)
            {
                throw new ArgumentException("Entry volume and volume percent must be supplied together.");
            }

            if (entryVolume.HasValue)
            {
                quantity = Math.Min(quantity, entryVolume.Value * volumePercent!.Value / 100m);
            }

            var increment = Math.Max(lotSize, roundLotSize ?? lotSize);
            return decimal.Floor(quantity / increment) * increment;
        }
    }
}
