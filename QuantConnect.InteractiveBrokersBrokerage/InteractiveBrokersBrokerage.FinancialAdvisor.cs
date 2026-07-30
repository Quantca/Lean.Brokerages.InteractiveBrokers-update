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
        /// Represents the allocation group managed by financial advisors.
        /// </summary>
        /// <remarks>
        /// The specific Advisor Account Group name that has already been created in TWS Global Configuration.
        /// </remarks>
        private string _financialAdvisorsGroupFilter;
        private bool _financialAdvisorGroupManagementEnabled;
        private bool _financialAdvisorUnifiedGroupsEnabled;
        private InteractiveBrokersFinancialAdvisorAccountState _financialAdvisorAccountState;

        internal bool FinancialAdvisorServiceOwnsStartupRequests =>
            _financialAdvisorUnifiedGroupsEnabled && IsFinancialAdvisor;

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
                if (additionalAccountIds?.Count > 0)
                {
                    return false;
                }

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
            Message += HandleFinancialAdvisorBrokerageMessage;
            _cancellationTokenSource.Token.Register(DisposeFinancialAdvisorAccountState);
        }

        private void DisposeFinancialAdvisorAccountState()
        {
            Message -= HandleFinancialAdvisorBrokerageMessage;
            _financialAdvisorAccountState?.Dispose();
        }

        private void HandleFinancialAdvisorBrokerageMessage(object sender, BrokerageMessageEvent message)
        {
            if (message.Type == BrokerageMessageType.Reconnect && IsConnected)
            {
                _financialAdvisorAccountState?.NotifyBrokerageConnected();
            }
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
                IsFinancialAdvisor &&
                eventArgs.AccountUpdatesMultiRequestId.HasValue &&
                _financialAdvisorAccountState?.IsServiceOwnedRequestId(
                    eventArgs.AccountUpdatesMultiRequestId.Value) == true;
        }

        private bool TryGetFinancialAdvisorPortfolioPosition(
            IB.UpdatePortfolioEventArgs eventArgs,
            out decimal position)
        {
            position = _financialAdvisorUnifiedGroupsEnabled && IsFinancialAdvisor
                ? eventArgs.PositionQuantity
                : eventArgs.Position;
            return !_financialAdvisorUnifiedGroupsEnabled ||
                !IsFinancialAdvisor ||
                !eventArgs.PositionsMultiRequestId.HasValue ||
                _financialAdvisorAccountState?.IsServiceOwnedRequestId(
                    eventArgs.PositionsMultiRequestId.Value) != true;
        }

        private IOrderProperties CreateRecoveredOrderProperties(IBApi.Order order)
        {
            if (!_financialAdvisorUnifiedGroupsEnabled || !IsFinancialAdvisor)
            {
                return null;
            }

            var group = order?.FaGroup?.Trim() ?? string.Empty;
            var account = order?.Account?.Trim() ?? string.Empty;
            var isGroupOrder = group.Length != 0;
            if (!isGroupOrder &&
                (account.Length == 0 ||
                    account.Equals(_account, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var properties = new InteractiveBrokersOrderProperties
            {
                Account = isGroupOrder ? string.Empty : account,
                FaGroup = isGroupOrder ? group : string.Empty,
                FaMethod = isGroupOrder ? order.FaMethod ?? string.Empty : string.Empty,
                OutsideRegularTradingHours = order.OutsideRth
            };
            if (isGroupOrder &&
                decimal.TryParse(order.FaPercentage, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var percentage))
            {
                if (percentage == decimal.Truncate(percentage) &&
                    percentage >= int.MinValue && percentage <= int.MaxValue)
                {
                    properties.FaPercentage = (int)percentage;
                }
                else
                {
                    properties.ExactFaPercentage = percentage;
                }
            }
            return properties;
        }

        internal void ValidateFinancialAdvisorOrderAdmission(Order order, bool isUpdate = false)
        {
            if (!_financialAdvisorUnifiedGroupsEnabled ||
                !IsFinancialAdvisor ||
                order?.Type == OrderType.OptionExercise)
            {
                return;
            }

            var ibOrder = new IBApi.Order { Account = _account };
            ConfigureFinancialAdvisorOrder(ibOrder, order);
            var properties = order.Properties as InteractiveBrokersOrderProperties;
            var isDirectAccountOrder =
                !string.IsNullOrWhiteSpace(properties?.Account);
            if (!isDirectAccountOrder)
            {
                var unsupportedConfigurationError =
                    _financialAdvisorAccountState?.UnsupportedConfigurationError;
                if (!isUpdate &&
                    !string.IsNullOrEmpty(unsupportedConfigurationError) &&
                    FAState.IsFinancialAdvisorGroupOrder(
                        order, _financialAdvisorsGroupFilter))
                {
                    throw new InvalidOperationException(
                        unsupportedConfigurationError);
                }
                ValidateStateIndependentFinancialAdvisorOrderAdmission(order);
            }
            if (!isUpdate)
            {
                PreflightFinancialAdvisorComboLegs(order, ibOrder);
            }
            if (isDirectAccountOrder || isUpdate)
            {
                return;
            }
            if (_financialAdvisorAccountState?.IsGroupTradingBlocked == true &&
                FAState.IsFinancialAdvisorGroupOrder(
                    order, _financialAdvisorsGroupFilter))
            {
                throw new InvalidOperationException(
                    "FA group orders are blocked while an FA configuration mutation is active or its broker outcome requires reconciliation.");
            }
            if (string.IsNullOrWhiteSpace(ibOrder.FaGroup))
            {
                return;
            }
            ValidateFinancialAdvisorAllocationMethod(
                ibOrder,
                GetAccountSnapshot(),
                order.Symbol,
                _algorithm?.Securities.TryGetValue(order.Symbol, out var security) == true
                    ? security.SymbolProperties.LotSize
                    : GetSymbolProperties(order.Symbol).LotSize);
        }

        private void PreflightFinancialAdvisorComboLegs(
            Order order,
            IBApi.Order currentRoute)
        {
            var group = order.GroupOrderManager;
            var orderProvider = _orderProvider;
            if (group == null || orderProvider == null)
            {
                return;
            }

            int[] orderIds;
            lock (group.OrderIds)
            {
                orderIds = group.OrderIds.ToArray();
            }

            foreach (var orderId in orderIds)
            {
                if (orderId == order.Id)
                {
                    continue;
                }

                // Never call an external order provider while holding the group-order lock.
                var leg = orderProvider.GetOrderById(orderId);
                if (leg == null)
                {
                    continue;
                }

                ValidateStateIndependentFinancialAdvisorOrderAdmission(leg);
                var legRoute = new IBApi.Order { Account = _account };
                ConfigureFinancialAdvisorOrder(legRoute, leg);
                if (!HaveEquivalentFinancialAdvisorRoutes(currentRoute, legRoute))
                {
                    throw new InvalidOperationException(
                        "All combo legs must use the same effective Financial Advisor " +
                        "Account, FaGroup, FaMethod, and percentage.");
                }
            }
        }

        private void ValidateStateIndependentFinancialAdvisorOrderAdmission(
            Order order)
        {
            var properties =
                order.Properties as InteractiveBrokersOrderProperties;
            if (!string.IsNullOrWhiteSpace(properties?.Account))
            {
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
        }

        private static bool HaveEquivalentFinancialAdvisorRoutes(
            IBApi.Order first,
            IBApi.Order second)
        {
            return string.Equals(
                    first.Account ?? string.Empty,
                    second.Account ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    first.FaGroup ?? string.Empty,
                    second.FaGroup ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    first.FaMethod ?? string.Empty,
                    second.FaMethod ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase) &&
                ParseFinancialAdvisorPercentage(first.FaPercentage) ==
                    ParseFinancialAdvisorPercentage(second.FaPercentage);
        }

        private static decimal ParseFinancialAdvisorPercentage(string value)
        {
            decimal.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var percentage);
            return percentage;
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
                ibOrder.TotalQuantity = Math.Abs(
                    leanOrder.GroupOrderManager?.Quantity ?? leanOrder.Quantity);
                return;
            }

            var hasExplicitGroup = !string.IsNullOrWhiteSpace(properties?.FaGroup);
            ibOrder.FaGroup = hasExplicitGroup
                ? properties.FaGroup.Trim()
                : _financialAdvisorsGroupFilter;
            ibOrder.FaMethod = ResolveFinancialAdvisorAllocationMethod(leanOrder);
            if (string.IsNullOrWhiteSpace(ibOrder.FaGroup))
            {
                return;
            }

            ibOrder.FaGroup = ibOrder.FaGroup.Trim();
            ibOrder.TotalQuantity = Math.Abs(
                leanOrder.GroupOrderManager?.Quantity ?? leanOrder.Quantity);
            if (ibOrder.FaMethod.Equals("PctChange", StringComparison.OrdinalIgnoreCase))
            {
                ibOrder.FaMethod = "PctChange";
                ibOrder.FaPercentage =
                    (properties.ExactFaPercentage ?? properties.FaPercentage).ToStringInvariant();
                ibOrder.TotalQuantity = 0m;
            }
        }

        private static string ResolveFinancialAdvisorAllocationMethod(Order order)
        {
            var properties =
                order?.Properties as InteractiveBrokersOrderProperties;
            return FAState.NormalizeFinancialAdvisorAllocationMethod(
                !string.IsNullOrWhiteSpace(properties?.FaGroup)
                    ? properties.FaMethod
                    : string.Empty);
        }

        internal static void ValidateFinancialAdvisorAllocationMethod(
            IBApi.Order order,
            BrokerageAccountSnapshot snapshot,
            Symbol symbol = null,
            decimal lotSize = 0m)
        {
            if (string.IsNullOrWhiteSpace(order?.FaGroup) ||
                snapshot?.Status != BrokerageAccountSnapshotStatus.Ready ||
                !snapshot.AllGroups.TryGetValue(order.FaGroup.Trim(), out var group))
            {
                return;
            }

            if (group.AccountIds.Any(accountId => !snapshot.AccountDirectory.TryGetValue(
                accountId, out var entry) || entry.Relationship != BrokerageAccountRelationship.Managed))
            {
                throw new InvalidOperationException($"Financial Advisor group '{group.Name}' contains an account that is not classified as managed; refresh after correcting the group membership in TWS.");
            }
            var savedMethod = FAState.NormalizeFinancialAdvisorAllocationMethod(group.AllocationMethod);
            var requestedMethod = FAState.NormalizeFinancialAdvisorAllocationMethod(order.FaMethod);
            if (savedMethod is not ("ContractsOrShares" or "Ratio" or "Percent" or
                "NetLiq" or "AvailableEquity" or "Equal"))
            {
                throw new NotSupportedException(
                    $"Financial Advisor group '{group.Name}' uses unsupported saved allocation method " +
                    $"'{group.AllocationMethod}'. Supported saved allocation methods are ContractsOrShares, " +
                    "Ratio, Percent, NetLiq, AvailableEquity, and Equal.");
            }
            if (requestedMethod.Equals("PctChange", StringComparison.OrdinalIgnoreCase))
            {
                if (!decimal.TryParse(order.FaPercentage, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    throw new InvalidOperationException(
                        $"Financial Advisor PctChange order for group '{group.Name}' requires a valid FaPercentage.");
                }
                if (savedMethod is not ("NetLiq" or "AvailableEquity" or "Equal"))
                {
                    throw new InvalidOperationException(
                        $"Financial Advisor PctChange cannot override group '{group.Name}' saved method " +
                        $"'{group.AllocationMethod}'. Change the saved method to NetLiq, AvailableEquity, or Equal " +
                        "and refresh the snapshot, or leave FaMethod empty.");
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
                    if (requiredQuantity > 0m &&
                        lotSize > 0m &&
                        requiredQuantity % lotSize != 0m)
                    {
                        throw new InvalidOperationException(
                            $"ContractsOrShares group '{group.Name}' has a saved allocation total of " +
                            $"{requiredQuantity.ToStringInvariant()}, which is not a valid parent quantity for " +
                            $"{symbol} (lot size {lotSize.ToStringInvariant()}). Adjust the saved vector so its " +
                            "total is a whole multiple of the lot size.");
                    }
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

            if (requestedMethod.Length != 0 &&
                !savedMethod.Equals(requestedMethod, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Saved Financial Advisor group '{group.Name}' uses '{group.AllocationMethod}', " +
                    $"so it cannot execute an order using '{order.FaMethod}'. Leave FaMethod empty or use the saved method.");
            }
        }

        private bool UsesExactFinancialAdvisorFillQuantity(Order order)
        {
            if (!_financialAdvisorUnifiedGroupsEnabled || !IsFinancialAdvisor)
            {
                return false;
            }

            var properties = order?.Properties as InteractiveBrokersOrderProperties;
            if (order?.Type == OrderType.OptionExercise)
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(properties?.Account))
            {
                return true;
            }
            return (!string.IsNullOrWhiteSpace(properties?.FaGroup) ||
                    !string.IsNullOrWhiteSpace(_financialAdvisorsGroupFilter)) &&
                !ResolveFinancialAdvisorAllocationMethod(order).Equals(
                    "PctChange", StringComparison.OrdinalIgnoreCase);
        }

    }
}
