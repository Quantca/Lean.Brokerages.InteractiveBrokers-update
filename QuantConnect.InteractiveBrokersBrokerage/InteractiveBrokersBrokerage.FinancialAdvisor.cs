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
using System.Linq;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using IB = QuantConnect.Brokerages.InteractiveBrokers.Client;

namespace QuantConnect.Brokerages.InteractiveBrokers
{
    public sealed partial class InteractiveBrokersBrokerage :
        IBrokerageAccountStateProvider,
        IBrokerageAccountGroupManager,
        IBrokerageAccountGroupAllocationManager
    {
        private bool _sentFAOrderPropertiesWarning;

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
        private int _financialAdvisorAccountUpdateRequestId;
        private int _financialAdvisorPositionRequestId;

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
        public bool RequestConfiguredAccountSnapshotRefresh()
        {
            var state = _financialAdvisorAccountState;
            return state != null && (state.HasConfiguredScope
                ? state.RequestConfiguredRefreshNow()
                : RequestAccountSnapshotRefresh(Array.Empty<string>(), Array.Empty<string>()));
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
                IsOutsideFinancialAdvisorGroupFilter(targetGroupName))
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
                IsOutsideFinancialAdvisorGroupFilter(groupName))
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

            if (!string.IsNullOrEmpty(_financialAdvisorsGroupFilter))
            {
                _client.AccountUpdateMultiWithRequestId += HandleFinancialAdvisorAccountUpdateRow;
            }

            if (_algorithm != null)
            {
                _client.PositionMulti += HandleFinancialAdvisorPositionRow;
            }

            _financialAdvisorAccountState =
                new InteractiveBrokersFinancialAdvisorAccountState(
                    _client,
                    CheckRateLimiting,
                    () => IsConnected,
                    MapSymbol,
                    _account,
                    _financialAdvisorsGroupFilter,
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

        private void HandleFinancialAdvisorAccountUpdateRow(object sender, IB.AccountUpdateMultiEventArgs e)
        {
            _financialAdvisorAccountUpdateRequestId = e.RequestId;
        }

        private void HandleFinancialAdvisorPositionRow(object sender, IB.PositionMultiEventArgs e)
        {
            _financialAdvisorPositionRequestId = e.RequestId;
        }

        private bool IsFinancialAdvisorAccountUpdateServiceRow()
        {
            var requestId = _financialAdvisorAccountUpdateRequestId;
            _financialAdvisorAccountUpdateRequestId = 0;
            return _financialAdvisorUnifiedGroupsEnabled &&
                InteractiveBrokersFinancialAdvisorAccountState.IsServiceRequestId(requestId);
        }

        private bool TryGetFinancialAdvisorPortfolioPosition(
            IB.UpdatePortfolioEventArgs eventArgs,
            out decimal position)
        {
            var requestId = _financialAdvisorPositionRequestId;
            _financialAdvisorPositionRequestId = 0;
            position = _financialAdvisorUnifiedGroupsEnabled
                ? eventArgs.PositionQuantity
                : eventArgs.Position;
            return !_financialAdvisorUnifiedGroupsEnabled ||
                !InteractiveBrokersFinancialAdvisorAccountState.IsServiceRequestId(requestId);
        }

        private bool IsOutsideFinancialAdvisorGroupFilter(string groupName)
        {
            return !string.IsNullOrWhiteSpace(_financialAdvisorsGroupFilter) &&
                !string.Equals(
                    groupName?.Trim(),
                    _financialAdvisorsGroupFilter,
                    StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether the specified financial advisor group is allowed
        /// based on the current group filter. If no filter is set, all groups are allowed.
        /// </summary>
        /// <param name="groupName">The name of the financial advisor group to check.</param>
        /// <returns><c>true</c> if the group is allowed; otherwise, <c>false</c>.</returns>
        private bool IsFaGroupFlitterSet(string groupName)
        {
            return !string.IsNullOrEmpty(_financialAdvisorsGroupFilter)
                && !string.IsNullOrEmpty(groupName)
                && !groupName.Equals(
                    _financialAdvisorsGroupFilter,
                    StringComparison.InvariantCultureIgnoreCase);
        }
    }
}
