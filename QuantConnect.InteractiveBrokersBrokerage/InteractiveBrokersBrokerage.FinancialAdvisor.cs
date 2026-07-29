/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Util;
using FAState = QuantConnect.Brokerages.InteractiveBrokers.InteractiveBrokersFinancialAdvisorAccountState;
using IB = QuantConnect.Brokerages.InteractiveBrokers.Client;

namespace QuantConnect.Brokerages.InteractiveBrokers
{
    public sealed partial class InteractiveBrokersBrokerage :
        IBrokerageAccountStateProvider,
        IBrokerageAccountGroupManager,
        IBrokerageAccountGroupAllocationManager
    {
        /// <summary>
        /// Stores skipped orders whose FA group does not match the configured filter.
        /// Key is the order ID; value is the FA group associated with the order.
        /// </summary>
        private readonly ConcurrentDictionary<int, string> _skippedOrdersByFaGroup = new();

        /// <summary>
        /// Represents the allocation group managed by financial advisors.
        /// </summary>
        /// <remarks>
        /// The specific Advisor Account Group name that has already been created in TWS Global Configuration.
        /// </remarks>
        private string _financialAdvisorsGroupFilter;
        private bool _financialAdvisorGroupManagementEnabled;
        private bool _financialAdvisorUnifiedGroupsEnabled;
        private InteractiveBrokersFinancialAdvisorAccountState _financialAdvisorAccountState;

        /// <inheritdoc/>
        public BrokerageAccountSnapshot GetAccountSnapshot()
        {
            return _financialAdvisorAccountState?.Snapshot ?? BrokerageAccountSnapshot.Unavailable;
        }

        /// <inheritdoc/>
        public bool RequestAccountSnapshotRefresh(
            IReadOnlyCollection<string> groupNames,
            IReadOnlyCollection<string> additionalAccountIds)
        {
            var state = _financialAdvisorAccountState;
            if (state == null)
            {
                return false;
            }

            ArgumentNullException.ThrowIfNull(groupNames);
            if (!string.IsNullOrWhiteSpace(_financialAdvisorsGroupFilter))
            {
                if (groupNames.Any(group => !string.Equals(
                    group?.Trim(), _financialAdvisorsGroupFilter, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                groupNames = new[] { _financialAdvisorsGroupFilter };
            }

            return state.RequestRefresh(groupNames, additionalAccountIds);
        }

        /// <inheritdoc/>
        public BrokerageAccountGroupAssignment GetAccountGroupAssignment()
        {
            return _financialAdvisorGroupManagementEnabled
                ? _financialAdvisorAccountState?.GroupAssignment ?? BrokerageAccountGroupAssignment.Unavailable
                : BrokerageAccountGroupAssignment.Unavailable;
        }

        /// <inheritdoc/>
        public bool RequestAccountGroupAssignment(
            string accountId,
            string targetGroupName,
            string expectedMembershipHash,
            string expectedGroupConfigurationVersion,
            decimal? targetAllocationValue = null)
        {
            var state = _financialAdvisorAccountState;
            if (!_financialAdvisorGroupManagementEnabled || state == null ||
                !string.IsNullOrEmpty(targetGroupName) &&
                FAState.IsOutsideFinancialAdvisorGroupFilter(
                    _financialAdvisorsGroupFilter, targetGroupName))
            {
                return false;
            }

            return state.RequestGroupAssignment(
                accountId,
                targetGroupName,
                expectedMembershipHash,
                expectedGroupConfigurationVersion,
                targetAllocationValue);
        }

        /// <inheritdoc/>
        public BrokerageAccountGroupAllocationUpdate GetAccountGroupAllocationUpdate()
        {
            return _financialAdvisorGroupManagementEnabled
                ? _financialAdvisorAccountState?.GroupAllocationUpdate ?? BrokerageAccountGroupAllocationUpdate.Unavailable
                : BrokerageAccountGroupAllocationUpdate.Unavailable;
        }

        /// <inheritdoc/>
        public bool RequestAccountGroupAllocationUpdate(
            string groupName,
            IReadOnlyDictionary<string, decimal> accountAllocationValues,
            string expectedMembershipHash,
            string expectedGroupConfigurationVersion)
        {
            var state = _financialAdvisorAccountState;
            if (!_financialAdvisorGroupManagementEnabled || state == null ||
                FAState.IsOutsideFinancialAdvisorGroupFilter(
                    _financialAdvisorsGroupFilter, groupName))
            {
                return false;
            }

            return state.RequestGroupAllocationUpdate(
                groupName,
                accountAllocationValues,
                expectedMembershipHash,
                expectedGroupConfigurationVersion);
        }

        private void InitializeFinancialAdvisorAccountState()
        {
            if (!_financialAdvisorUnifiedGroupsEnabled || !IsFinancialAdvisor)
            {
                return;
            }

            _financialAdvisorAccountState =
                new InteractiveBrokersFinancialAdvisorAccountState(
                    _client,
                    CheckRateLimiting,
                    () => IsConnected,
                    MapSymbol,
                    _account,
                    _financialAdvisorsGroupFilter,
                    hasOpenFinancialAdvisorOrders: () =>
                        _orderProvider.GetOpenOrders(order =>
                            FAState.IsFinancialAdvisorGroupOrder(
                                order,
                                _financialAdvisorsGroupFilter)).Count != 0,
                    reportUnsupported: message => OnMessage(
                        new BrokerageMessageEvent(
                            BrokerageMessageType.ActionRequired,
                            "UnsupportedFinancialAdvisorConfiguration",
                            message)));
            _cancellationTokenSource.Token.Register(DisposeFinancialAdvisorAccountState);
        }

        private void DisposeFinancialAdvisorAccountState()
        {
            _financialAdvisorAccountState?.Dispose();
        }

        private void ConfigureFinancialAdvisorFeatures(
            string financialAdvisorsGroupFilter,
            bool financialAdvisorGroupManagementEnabled,
            bool financialAdvisorUnifiedGroupsEnabled)
        {
            if (financialAdvisorGroupManagementEnabled &&
                !financialAdvisorUnifiedGroupsEnabled)
            {
                throw new ArgumentException(
                    "Financial Advisor group management requires unified groups.",
                    nameof(financialAdvisorGroupManagementEnabled));
            }

            _financialAdvisorsGroupFilter = financialAdvisorUnifiedGroupsEnabled
                ? financialAdvisorsGroupFilter?.Trim() ?? string.Empty
                : financialAdvisorsGroupFilter;
            _financialAdvisorGroupManagementEnabled =
                financialAdvisorGroupManagementEnabled;
            _financialAdvisorUnifiedGroupsEnabled =
                financialAdvisorUnifiedGroupsEnabled;

            if (!string.IsNullOrEmpty(_financialAdvisorsGroupFilter))
            {
                Log.Trace(
                    "InteractiveBrokersBrokerage.InteractiveBrokersBrokerage(): " +
                    $"Using Financial Advisor group filter: '{_financialAdvisorsGroupFilter}'");
            }
        }

        private bool IsFinancialAdvisorAccountUpdateServiceRow(
            IB.UpdateAccountValueEventArgs eventArgs)
        {
            return _financialAdvisorUnifiedGroupsEnabled &&
                eventArgs.AccountUpdatesMultiRequestId.HasValue &&
                InteractiveBrokersFinancialAdvisorAccountState.IsServiceRequestId(
                    eventArgs.AccountUpdatesMultiRequestId.Value);
        }

        private bool TryGetFinancialAdvisorPortfolioPosition(
            IB.UpdatePortfolioEventArgs eventArgs,
            out decimal position)
        {
            position = _financialAdvisorUnifiedGroupsEnabled && IsFinancialAdvisor
                ? eventArgs.PositionQuantity
                : eventArgs.Position;
            return !_financialAdvisorUnifiedGroupsEnabled ||
                !eventArgs.PositionsMultiRequestId.HasValue ||
                !InteractiveBrokersFinancialAdvisorAccountState.IsServiceRequestId(
                    eventArgs.PositionsMultiRequestId.Value);
        }

        internal void ValidateFinancialAdvisorOrderAdmission(Order order)
        {
            ConfigureFinancialAdvisorOrder(new IBApi.Order(), order);
        }

        private void ConfigureFinancialAdvisorOrder(
            IBApi.Order ibOrder,
            Order leanOrder)
        {
            if (!_financialAdvisorUnifiedGroupsEnabled ||
                !IsFinancialAdvisor ||
                leanOrder?.Type == OrderType.OptionExercise)
            {
                return;
            }
            var properties =
                leanOrder.Properties as InteractiveBrokersOrderProperties;
            if (!string.IsNullOrWhiteSpace(properties?.Account))
            {
                ibOrder.Account = properties.Account;
                ibOrder.FaGroup = string.Empty;
                ibOrder.FaMethod = string.Empty;
                return;
            }
            if (!string.IsNullOrWhiteSpace(properties?.FaProfile))
            {
                throw new NotSupportedException(
                    "Legacy Financial Advisor profiles are not supported when unified groups are enabled. Use FaGroup instead.");
            }
            if (!string.IsNullOrWhiteSpace(properties?.FaGroup) &&
                FAState.IsOutsideFinancialAdvisorGroupFilter(
                    _financialAdvisorsGroupFilter, properties.FaGroup))
            {
                throw new InvalidOperationException(
                    $"Order FA group '{properties.FaGroup}' does not match the configured " +
                    $"Financial Advisor group filter '{_financialAdvisorsGroupFilter}'.");
            }
            if (_financialAdvisorAccountState?.IsGroupTradingBlocked == true &&
                FAState.IsFinancialAdvisorGroupOrder(
                    leanOrder,
                    _financialAdvisorsGroupFilter))
            {
                throw new InvalidOperationException(
                    "FA group orders are blocked while account-group configuration is being updated or reconciled.");
            }

            var hasExplicitGroup = !string.IsNullOrWhiteSpace(properties?.FaGroup);
            ibOrder.FaGroup = hasExplicitGroup
                ? properties.FaGroup.Trim()
                : _financialAdvisorsGroupFilter;
            ibOrder.FaMethod = hasExplicitGroup
                ? properties.FaMethod
                : string.Empty;
            if (string.IsNullOrWhiteSpace(ibOrder.FaGroup))
            {
                return;
            }

            ibOrder.FaGroup = ibOrder.FaGroup.Trim();
            ibOrder.FaMethod =
                FAState.NormalizeFinancialAdvisorAllocationMethod(ibOrder.FaMethod);
            ibOrder.TotalQuantity = Math.Abs(
                leanOrder.GroupOrderManager?.Quantity ?? leanOrder.Quantity);
            if (ibOrder.FaMethod.Equals("PctChange", StringComparison.OrdinalIgnoreCase))
            {
                ibOrder.FaMethod = "PctChange";
                ibOrder.FaPercentage =
                    (properties.ExactFaPercentage ?? properties.FaPercentage).ToStringInvariant();
                ibOrder.TotalQuantity = 0m;
            }
            ValidateFinancialAdvisorAllocationMethod(
                ibOrder,
                GetAccountSnapshot());
        }

        internal static void ValidateFinancialAdvisorAllocationMethod(
            IBApi.Order order,
            BrokerageAccountSnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(order?.FaGroup) ||
                snapshot?.Status != BrokerageAccountSnapshotStatus.Ready ||
                !snapshot.AllGroups.TryGetValue(order.FaGroup.Trim(), out var group))
            {
                return;
            }

            var savedMethod = FAState.NormalizeFinancialAdvisorAllocationMethod(group.AllocationMethod);
            var requestedMethod = FAState.NormalizeFinancialAdvisorAllocationMethod(order.FaMethod);
            if (savedMethod.Equals("MonetaryAmount", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    $"Financial Advisor group '{group.Name}' uses the unsupported MonetaryAmount allocation method.");
            }
            if (requestedMethod.Equals("PctChange", StringComparison.OrdinalIgnoreCase))
            {
                if (!decimal.TryParse(order.FaPercentage, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    throw new InvalidOperationException(
                        $"Financial Advisor PctChange order for group '{group.Name}' requires a valid FaPercentage.");
                }
                return;
            }
            if (FAState.IsSupportedUserSpecifiedAllocationMethod(savedMethod))
            {
                if (requestedMethod.Length != 0)
                {
                    throw new InvalidOperationException(
                        $"Saved Financial Advisor group '{group.Name}' uses '{group.AllocationMethod}'. " +
                        "Set FaGroup and leave FaMethod empty so IB applies its saved allocation values.");
                }
                FAState.ValidateGroupAllocationUpdate(group.Name, group.AccountAllocationValues, snapshot.AllGroups);
                if (savedMethod.Equals("ContractsOrShares", StringComparison.OrdinalIgnoreCase))
                {
                    var requiredQuantity = group.AccountAllocationValues.Values.Sum();
                    if (requiredQuantity <= 0m || order.TotalQuantity != requiredQuantity)
                    {
                        throw new InvalidOperationException(
                            $"ContractsOrShares group '{group.Name}' requires a positive parent quantity equal " +
                            $"to its saved allocation total {requiredQuantity.ToStringInvariant()}; " +
                            $"received {order.TotalQuantity.ToStringInvariant()}.");
                    }
                }
                return;
            }

            if ((savedMethod is "NetLiq" or "AvailableEquity" or "Equal") &&
                requestedMethod.Length != 0 &&
                !savedMethod.Equals(requestedMethod, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Saved Financial Advisor group '{group.Name}' uses '{group.AllocationMethod}', " +
                    $"so it cannot execute an order using '{order.FaMethod}'. Leave FaMethod empty or use the saved method.");
            }
        }

        /// <summary>
        /// Determines whether the specified financial advisor group is allowed
        /// based on the current group filter. If no filter is set, all groups are allowed.
        /// </summary>
        /// <param name="groupName">The name of the financial advisor group to check.</param>
        /// <returns><c>true</c> if the group is allowed; otherwise, <c>false</c>.</returns>
        private bool IsFaGroupFlitterSet(string groupName)
        {
            return FAState.IsFinancialAdvisorGroupFilteredOut(
                _financialAdvisorsGroupFilter, groupName);
        }
    }
}
