/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using IBApi;
using QuantConnect.Brokerages.InteractiveBrokers.Client;
using QuantConnect.Logging;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.InteractiveBrokers
{
    /// <summary>
    /// Serializes and publishes read-only Financial Advisor account snapshots.
    /// </summary>
    internal sealed partial class InteractiveBrokersFinancialAdvisorAccountState : IDisposable
    {
        private const int GroupsFaDataType = 1;
        private const int AliasesFaDataType = 3;
        private const int QueueCapacity = 8;

        private readonly InteractiveBrokersClient _client;
        private readonly Action _paceRequest;
        private readonly Func<bool> _isConnected;
        private readonly Func<Contract, Symbol> _mapSymbol;
        private readonly Action<string> _reportUnsupported;
        private readonly string _masterAccountId;
        private readonly string _configuredGroup;
        private readonly TimeSpan _requestTimeout;
        private readonly RequestActions _requests;
        private readonly Channel<WorkItem> _work;
        private readonly SemaphoreSlim _operationLock = new(1, 1);
        private readonly CancellationTokenSource _disposeTokenSource = new();
        private readonly RateGate _requestRateGate = new(10, TimeSpan.FromSeconds(1));
        private readonly object _callbackStateLock = new();

        private volatile BrokerageAccountSnapshot _snapshot = BrokerageAccountSnapshot.Unavailable;
        private Task _worker;
        private SnapshotScope _activeRefresh;
        private WorkItem _queuedRefresh;
        private PendingRequest _pendingRequest;
        private string _handshakeManagedAccounts;
        private bool _connected;
        private bool _unkeyedRequestsPoisoned;
        private bool _disposed;
        private int _nextRequestId = int.MinValue;
        private long _requestVersion;

        internal BrokerageAccountSnapshot Snapshot => _snapshot;
        internal BrokerageAccountGroupAssignment GroupAssignment =>
            BrokerageAccountGroupAssignment.Unavailable;
        internal BrokerageAccountGroupAllocationUpdate GroupAllocationUpdate =>
            BrokerageAccountGroupAllocationUpdate.Unavailable;
        internal bool IsGroupTradingBlocked => false;
        internal bool HasConfiguredScope => true;

        internal InteractiveBrokersFinancialAdvisorAccountState(
            InteractiveBrokersClient client,
            Action paceRequest,
            Func<bool> isConnected,
            Func<Contract, Symbol> mapSymbol,
            string masterAccountId,
            string financialAdvisorGroupFilter = "",
            TimeSpan? requestTimeout = null,
            Action<string> reportUnsupported = null,
            RequestActions requestActions = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _paceRequest = paceRequest ?? (() => { });
            _isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
            _mapSymbol = mapSymbol ?? throw new ArgumentNullException(nameof(mapSymbol));
            _masterAccountId = masterAccountId?.Trim() ?? string.Empty;
            _configuredGroup = financialAdvisorGroupFilter?.Trim() ?? string.Empty;
            _requestTimeout = requestTimeout > TimeSpan.Zero
                ? requestTimeout.Value
                : TimeSpan.FromSeconds(30);
            _reportUnsupported = reportUnsupported;
            _requests = requestActions ?? new RequestActions(client);
            _work = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            _connected = isConnected();
            AttachCallbacks();
        }

        internal bool RequestRefresh(IReadOnlyCollection<string> groupNames) =>
            RequestRefresh(groupNames, Array.Empty<string>());

        internal bool RequestRefresh(
            IReadOnlyCollection<string> groupNames,
            IReadOnlyCollection<string> additionalAccountIds)
        {
            ArgumentNullException.ThrowIfNull(groupNames);
            ArgumentNullException.ThrowIfNull(additionalAccountIds);
            var groups = NormalizeNames(groupNames, "group");
            var additional = NormalizeAccountIds(additionalAccountIds);
            if (_configuredGroup.Length != 0)
            {
                if (groups.Count != 0 && groups.Any(group =>
                    !group.Equals(_configuredGroup, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        $"Requested FA groups are outside configured group '{_configuredGroup}'.");
                }
                if (additional.Length != 0)
                {
                    throw new InvalidOperationException(
                        "Additional account collection is unavailable when an FA group filter is configured.");
                }
                groups = new[] { _configuredGroup };
            }
            return QueueRefresh(new SnapshotScope(
                groups,
                additional,
                _configuredGroup.Length == 0 && groups.Count == 0,
                0));
        }

        internal bool RequestConfiguredRefreshNow() =>
            RequestRefresh(_configuredGroup.Length == 0
                ? Array.Empty<string>()
                : new[] { _configuredGroup });

        internal void MarkConnected()
        {
            lock (_callbackStateLock)
            {
                if (_disposed)
                {
                    return;
                }
                _connected = true;
                _unkeyedRequestsPoisoned = false;
                _handshakeManagedAccounts = null;
            }
        }

        internal void MarkDisconnected(string reason = null)
        {
            PendingRequest pending;
            long disconnectVersion;
            lock (_callbackStateLock)
            {
                if (_disposed)
                {
                    return;
                }
                _connected = false;
                _unkeyedRequestsPoisoned = true;
                _handshakeManagedAccounts = null;
                ++_requestVersion;
                disconnectVersion = _requestVersion;
                _activeRefresh = null;
                _queuedRefresh = null;
                pending = _pendingRequest;
                _pendingRequest = null;
            }
            pending?.Completion.TrySetException(new InvalidOperationException(
                reason ?? "Interactive Brokers disconnected."));
            PublishFailure(
                reason ?? "Interactive Brokers disconnected.",
                forceStale: true,
                expectedRequestVersion: disconnectVersion);
        }

        internal static bool IsServiceRequestId(int requestId) => requestId <= -2;

        // Tier 2 intentionally starts from unavailable mutation DTOs.
        internal bool RequestGroupAssignment(string accountId, string targetGroupName,
            string expectedMembershipHash, string expectedGroupConfigurationVersion,
            decimal? targetAllocationValue = null) => false;
        internal bool RequestGroupAllocationUpdate(string groupName,
            IReadOnlyDictionary<string, decimal> accountAllocationValues,
            string expectedMembershipHash, string expectedGroupConfigurationVersion) => false;

        public void Dispose()
        {
            PendingRequest pending;
            Task worker;
            lock (_callbackStateLock)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _connected = false;
                pending = _pendingRequest;
                _pendingRequest = null;
                worker = _worker;
            }
            DetachCallbacks();
            _work.Writer.TryComplete();
            _disposeTokenSource.Cancel();
            pending?.Completion.TrySetCanceled(_disposeTokenSource.Token);
            _ = (worker ?? Task.CompletedTask).ContinueWith(
                _ =>
                {
                    _requestRateGate.Dispose();
                    _operationLock.Dispose();
                    _disposeTokenSource.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private bool QueueRefresh(SnapshotScope requested)
        {
            lock (_callbackStateLock)
            {
                if (_disposed || !_connected || _unkeyedRequestsPoisoned)
                {
                    return false;
                }
                if ((_queuedRefresh != null &&
                     ScopesEqual(_queuedRefresh.Scope, requested)) ||
                    (_queuedRefresh == null &&
                     _activeRefresh != null &&
                     _snapshot.Status == BrokerageAccountSnapshotStatus.Refreshing &&
                     ScopesEqual(_activeRefresh, requested)))
                {
                    return true;
                }
                var requestVersion = ++_requestVersion;
                if (_queuedRefresh != null)
                {
                    _queuedRefresh.Scope = MergeScopes(
                        _queuedRefresh.Scope, requested, requestVersion);
                }
                else
                {
                    var scope = new SnapshotScope(requested.GroupNames,
                        requested.AdditionalAccountIds, requested.CompleteDiscovery,
                        requestVersion);
                    var item = new WorkItem(scope);
                    if (!_work.Writer.TryWrite(item))
                    {
                        return false;
                    }
                    _queuedRefresh = item;
                    _worker ??= Task.Run(WorkerLoopAsync);
                }
                _snapshot = CreateStatusSnapshot(
                    _snapshot, BrokerageAccountSnapshotStatus.Refreshing, string.Empty);
            }
            return true;
        }

        private async Task WorkerLoopAsync()
        {
            try
            {
                await foreach (var item in _work.Reader.ReadAllAsync(_disposeTokenSource.Token)
                    .ConfigureAwait(false))
                {
                    SnapshotScope scope;
                    lock (_callbackStateLock)
                    {
                        if (ReferenceEquals(_queuedRefresh, item))
                        {
                            _queuedRefresh = null;
                        }
                        scope = item.Scope;
                        if (_disposed || !_connected || _unkeyedRequestsPoisoned ||
                            scope.RequestVersion != _requestVersion)
                        {
                            continue;
                        }
                        _activeRefresh = scope;
                    }

                    // The worker loop provides operation serialization. The mandated
                    // semaphore is an admission turnstile and never spans an external call.
                    await _operationLock.WaitAsync(_disposeTokenSource.Token)
                        .ConfigureAwait(false);
                    _operationLock.Release();
                    Exception failure = null;
                    try
                    {
                        await RefreshAsync(scope).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        failure = exception;
                    }
                    finally
                    {
                        lock (_callbackStateLock)
                        {
                            if (ReferenceEquals(_activeRefresh, scope))
                            {
                                _activeRefresh = null;
                            }
                        }
                    }
                    if (failure != null)
                    {
                        var published = PublishFailure(
                            failure.Message,
                            forceStale: failure is UnkeyedRequestTimeoutException,
                            expectedRequestVersion: scope.RequestVersion);
                        if (published &&
                            failure is UnsupportedFinancialAdvisorConfigurationException)
                        {
                            ReportUnsupported(failure.Message);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static bool ScopesEqual(SnapshotScope left, SnapshotScope right) =>
            left.CompleteDiscovery == right.CompleteDiscovery &&
            left.GroupNames.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(right.GroupNames) &&
            left.AdditionalAccountIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(right.AdditionalAccountIds);

        private static SnapshotScope MergeScopes(
            SnapshotScope first, SnapshotScope second, long requestVersion) =>
            new(
                UnionFirstSeen(first.GroupNames, second.GroupNames),
                UnionFirstSeen(first.AdditionalAccountIds, second.AdditionalAccountIds),
                first.CompleteDiscovery || second.CompleteDiscovery,
                requestVersion);

        private static IReadOnlyCollection<string> UnionFirstSeen(
            IEnumerable<string> first, IEnumerable<string> second)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var values = new List<string>();
            foreach (var value in first.Concat(second))
            {
                if (seen.Add(value))
                {
                    values.Add(value);
                }
            }
            return values.AsReadOnly();
        }

        private sealed class WorkItem
        {
            internal SnapshotScope Scope;
            internal WorkItem(SnapshotScope scope) => Scope = scope;
        }

        private sealed class SnapshotScope
        {
            internal IReadOnlyCollection<string> GroupNames { get; }
            internal IReadOnlyCollection<string> AdditionalAccountIds { get; }
            internal bool CompleteDiscovery { get; }
            internal long RequestVersion { get; }
            internal SnapshotScope(IReadOnlyCollection<string> groupNames,
                IReadOnlyCollection<string> additionalAccountIds,
                bool completeDiscovery, long requestVersion)
            {
                GroupNames = groupNames;
                AdditionalAccountIds = additionalAccountIds;
                CompleteDiscovery = completeDiscovery;
                RequestVersion = requestVersion;
            }
        }

        private static IReadOnlyCollection<string> NormalizeNames(
            IEnumerable<string> values, string description)
        {
            var result = new List<string>();
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException($"A {description} name cannot be blank.");
                }
                if (!result.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(value.Trim());
                }
            }
            return result.AsReadOnly();
        }

        private async Task RefreshAsync(SnapshotScope scope)
        {
            var collectionStartedUtc = DateTime.UtcNow;
            var managedAccountIds = ParseManagedAccounts(await RequestManagedAccountsAsync(scope)
                .ConfigureAwait(false));
            var primaryAccountId = GetCanonicalPrimaryAccountId(
                managedAccountIds, _masterAccountId);
            var groupsXml = await RequestFinancialAdvisorXmlAsync(scope, GroupsFaDataType)
                .ConfigureAwait(false);
            var groupConfigurationVersion = ComputeConfigurationHash(groupsXml);
            var aliases = ParseAliases(await RequestFinancialAdvisorXmlAsync(scope, AliasesFaDataType)
                .ConfigureAwait(false));
            var familyCodes = ToFamilyCodeDictionary(await RequestFamilyCodesAsync(scope)
                .ConfigureAwait(false));
            var allGroups = CanonicalizeGroups(
                ParseGroups(groupsXml, validateAllocationConfiguration: false),
                managedAccountIds);
            var selectedGroups = scope.CompleteDiscovery
                ? allGroups
                : SelectGroups(allGroups, scope.GroupNames);

            ValidateAdditionalAccountIds(
                scope.AdditionalAccountIds, allGroups, managedAccountIds, primaryAccountId);
            try
            {
                ValidateManagedGroupMembers(
                    allGroups, managedAccountIds, primaryAccountId, allGroups.Keys);
            }
            catch (InvalidOperationException exception)
            {
                throw new UnsupportedFinancialAdvisorConfigurationException(
                    exception.Message + " Correct the group membership in TWS and refresh.");
            }
            var topologyDirectory = BuildAccountDirectory(
                primaryAccountId, managedAccountIds, allGroups, familyCodes,
                new Dictionary<string, BrokerageAccountState>(), aliases);
            ValidateSupportedGroupAccountRelationships(allGroups, topologyDirectory);

            var managed = managedAccountIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var accountsToCollect = (scope.CompleteDiscovery
                    ? managedAccountIds
                    : selectedGroups.Values.SelectMany(group => group.AccountIds)
                        .Concat(scope.AdditionalAccountIds))
                .Where(accountId => managed.Contains(accountId) &&
                    !IsPrimaryOrAggregateAccount(accountId, primaryAccountId))
                .Select(accountId => GetCanonicalManagedAccountId(managedAccountIds, accountId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(accountId => accountId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var positions =
                new Dictionary<string, Dictionary<(Symbol, string), BrokerageAccountPosition>>(
                    StringComparer.OrdinalIgnoreCase);
            var unmapped =
                new Dictionary<string, Dictionary<string, BrokerageAccountUnmappedPosition>>(
                    StringComparer.OrdinalIgnoreCase);
            var positionAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in selectedGroups.Values
                .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase))
            {
                var expected = group.AccountIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var row in await RequestPositionsAsync(scope, group.Name)
                    .ConfigureAwait(false))
                {
                    if (!expected.Contains(row.Account))
                    {
                        throw new InvalidOperationException(
                            $"Position response for FA group '{group.Name}' contained " +
                            $"non-member account '{row.Account}'.");
                    }
                    if (!positionAccounts.Contains(row.Account))
                    {
                        AddPosition(row, positions, unmapped);
                    }
                }
                positionAccounts.UnionWith(expected);
            }
            foreach (var accountId in accountsToCollect.Where(
                accountId => !positionAccounts.Contains(accountId)))
            {
                foreach (var row in await RequestPositionsAsync(scope, accountId)
                    .ConfigureAwait(false))
                {
                    if (!row.Account.Equals(accountId, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Position response for '{accountId}' contained account '{row.Account}'.");
                    }
                    AddPosition(row, positions, unmapped);
                }
            }

            var builders = accountsToCollect.ToDictionary(
                accountId => accountId,
                accountId => new AccountValueBuilder(
                    accountId, GetAccountGroupNames(allGroups, accountId)),
                StringComparer.OrdinalIgnoreCase);
            foreach (var accountId in accountsToCollect)
            {
                var rows = await RequestAccountUpdatesAsync(scope, accountId)
                    .ConfigureAwait(false);
                var returned = rows
                    .Where(row => !IsAllowedSummaryAccount(row.Account, primaryAccountId))
                    .Select(row => row.Account)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (returned.Length == 0 || returned.Any(
                    id => !id.Equals(accountId, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        $"Account update response did not match managed account '{accountId}'.");
                }
                foreach (var row in rows.Where(row =>
                    row.Account.Equals(accountId, StringComparison.OrdinalIgnoreCase)))
                {
                    builders[accountId].Apply(row.Key, row.Value, row.Currency);
                }
            }

            var endingGroupsXml = await RequestFinancialAdvisorXmlAsync(
                scope, GroupsFaDataType)
                .ConfigureAwait(false);
            if (!groupConfigurationVersion.Equals(
                ComputeConfigurationHash(endingGroupsXml), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "FA group configuration changed while the account snapshot was collected.");
            }

            var accountStates = builders.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Build(
                    positions.TryGetValue(pair.Key, out var mapped)
                        ? mapped.Values
                        : Array.Empty<BrokerageAccountPosition>(),
                    unmapped.TryGetValue(pair.Key, out var unknown)
                        ? unknown.Values
                        : Array.Empty<BrokerageAccountUnmappedPosition>()),
                StringComparer.OrdinalIgnoreCase);
            var accountDirectory = BuildAccountDirectory(
                primaryAccountId, managedAccountIds, allGroups, familyCodes,
                accountStates, aliases);
            var unassigned = ComputeUnassignedAccountIds(
                allGroups, managedAccountIds, primaryAccountId);
            var membershipHash = ComputeMembershipHash(
                selectedGroups, managedAccountIds, aliases, familyCodes);
            var completedUtc = DateTime.UtcNow;

            var current = Snapshot;
            PublishReady(new BrokerageAccountSnapshot(
                BrokerageAccountSnapshotStatus.Ready,
                current.Generation + 1,
                completedUtc,
                completedUtc,
                selectedGroups,
                accountStates,
                unassigned,
                membershipHash,
                groupConfigurationVersion,
                string.Empty,
                primaryAccountId,
                managedAccountIds.ToArray(),
                allGroups,
                accountDirectory,
                scope.CompleteDiscovery && accountsToCollect.Length ==
                    managedAccountIds.Count(id =>
                        !IsPrimaryOrAggregateAccount(id, primaryAccountId)),
                collectionStartedUtc),
                scope.RequestVersion);
        }

        private static bool IsAllowedSummaryAccount(string accountId, string primaryAccountId) =>
            "All".Equals(accountId, StringComparison.OrdinalIgnoreCase) ||
            IsPrimaryOrAggregateAccount(accountId, primaryAccountId);

        private async Task<string> RequestManagedAccountsAsync(SnapshotScope scope)
        {
            lock (_callbackStateLock)
            {
                if (!IsCurrentScopeLocked(scope))
                {
                    throw new RequestInvalidatedException(
                        "The account-state refresh became obsolete before managed-account discovery.");
                }
                if (!_unkeyedRequestsPoisoned &&
                    !string.IsNullOrWhiteSpace(_handshakeManagedAccounts))
                {
                    var cached = _handshakeManagedAccounts;
                    _handshakeManagedAccounts = null;
                    return cached;
                }
            }
            return (await SendUnkeyedAsync(
                scope, PendingKind.ManagedAccounts, 0,
                _requests.RequestManagedAccounts,
                "managed accounts").ConfigureAwait(false)).Text;
        }

        private async Task<string> RequestFinancialAdvisorXmlAsync(
            SnapshotScope scope, int faDataType) =>
            (await SendUnkeyedAsync(
                scope, PendingKind.FinancialAdvisor, faDataType,
                authorize => _requests.RequestFinancialAdvisor(faDataType, authorize),
                faDataType == GroupsFaDataType ? "FA groups" : "FA aliases")
                .ConfigureAwait(false)).Text;

        private async Task<FamilyCode[]> RequestFamilyCodesAsync(SnapshotScope scope) =>
            (await SendUnkeyedAsync(
                scope, PendingKind.FamilyCodes, 0, _requests.RequestFamilyCodes,
                "family codes").ConfigureAwait(false)).FamilyCodes;

        private async Task<IReadOnlyList<PositionMultiEventArgs>> RequestPositionsAsync(
            SnapshotScope scope, string accountOrGroup)
        {
            var requestId = NextRequestId();
            var pending = await SendKeyedAsync(
                scope, PendingKind.Positions, requestId,
                authorize => _requests.RequestPositions(
                    requestId, accountOrGroup, authorize),
                authorize => _requests.CancelPositions(requestId, authorize),
                $"positions for '{accountOrGroup}'").ConfigureAwait(false);
            return pending.PositionRows.ToArray();
        }

        private async Task<IReadOnlyList<AccountUpdateMultiEventArgs>>
            RequestAccountUpdatesAsync(SnapshotScope scope, string accountId)
        {
            var requestId = NextRequestId();
            var pending = await SendKeyedAsync(
                scope, PendingKind.AccountUpdates, requestId,
                authorize => _requests.RequestAccountUpdates(
                    requestId, accountId, authorize),
                authorize => _requests.CancelAccountUpdates(requestId, authorize),
                $"account updates for '{accountId}'").ConfigureAwait(false);
            return pending.AccountRows.ToArray();
        }

        private async Task<PendingRequest> SendUnkeyedAsync(
            SnapshotScope scope,
            PendingKind kind,
            int faDataType,
            WireAction send,
            string description)
        {
            var pending = InstallPending(
                scope, kind, 0, faDataType, unkeyed: true);
            var sent = false;
            try
            {
                sent = PaceAndInvoke(pending, send, cancellation: false);
                if (!sent)
                {
                    return await pending.Completion.Task.ConfigureAwait(false);
                }
                pending.WireSent = true;
                return await AwaitPendingAsync(pending, description, poisonOnTimeout: true)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception) when (sent)
            {
                throw new UnkeyedRequestTimeoutException(exception.Message, exception);
            }
            finally
            {
                ClearPending(pending);
            }
        }

        private async Task<PendingRequest> SendKeyedAsync(
            SnapshotScope scope,
            PendingKind kind,
            int requestId,
            WireAction send,
            WireAction cancel,
            string description)
        {
            var pending = InstallPending(
                scope, kind, requestId, 0, unkeyed: false);
            var sent = false;
            try
            {
                sent = PaceAndInvoke(pending, send, cancellation: false);
                if (!sent)
                {
                    return await pending.Completion.Task.ConfigureAwait(false);
                }
                pending.WireSent = true;
                return await AwaitPendingAsync(pending, description, poisonOnTimeout: false)
                    .ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (sent)
                    {
                        TryCancel(pending, cancel, description, requestId);
                    }
                }
                finally
                {
                    ClearPending(pending);
                }
            }
        }

        private PendingRequest InstallPending(
            SnapshotScope scope,
            PendingKind kind,
            int requestId,
            int faDataType,
            bool unkeyed)
        {
            var socketConnected = _isConnected();
            lock (_callbackStateLock)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(GetType().Name);
                }
                if (!IsCurrentScopeLocked(scope))
                {
                    throw new RequestInvalidatedException(
                        "The account-state refresh became obsolete before installing its request.");
                }
                if (!_connected || !socketConnected)
                {
                    throw new InvalidOperationException("Interactive Brokers is not connected.");
                }
                if (unkeyed && _unkeyedRequestsPoisoned)
                {
                    throw new InvalidOperationException(
                        "Unkeyed IB requests are blocked until a fresh connection is confirmed.");
                }
                if (_pendingRequest != null)
                {
                    throw new InvalidOperationException("An IB account-state request is already pending.");
                }
                return _pendingRequest =
                    new PendingRequest(scope, kind, requestId, faDataType);
            }
        }

        private bool IsCurrentScopeLocked(SnapshotScope scope) =>
            ReferenceEquals(_activeRefresh, scope) &&
            scope.RequestVersion == _requestVersion;

        private async Task<PendingRequest> AwaitPendingAsync(
            PendingRequest pending, string description, bool poisonOnTimeout)
        {
            try
            {
                return await pending.Completion.Task
                    .WaitAsync(_requestTimeout, _disposeTokenSource.Token)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                var timeoutMessage = $"Timed out waiting for IB {description}.";
                var completedByCallback = false;
                lock (_callbackStateLock)
                {
                    completedByCallback = pending.Finished;
                    if (!completedByCallback &&
                        ReferenceEquals(_pendingRequest, pending))
                    {
                        if (poisonOnTimeout)
                        {
                            _pendingRequest = null;
                            if (pending.WireSent)
                            {
                                _unkeyedRequestsPoisoned = true;
                                _handshakeManagedAccounts = null;
                                _snapshot = CreateStatusSnapshot(
                                    _snapshot,
                                    BrokerageAccountSnapshotStatus.Stale,
                                    timeoutMessage);
                            }
                        }
                        else
                        {
                            // Keep ownership through the paced cancellation so a
                            // disconnect/reconnect cannot cancel on a new connection.
                            pending.Finished = true;
                        }
                    }
                }
                if (completedByCallback)
                {
                    return await pending.Completion.Task.ConfigureAwait(false);
                }
                throw new TimeoutException(timeoutMessage, exception);
            }
        }

        private void ClearPending(PendingRequest pending)
        {
            lock (_callbackStateLock)
            {
                if (ReferenceEquals(_pendingRequest, pending))
                {
                    _pendingRequest = null;
                }
            }
        }

        private bool PaceAndInvoke(
            PendingRequest pending, WireAction action, bool cancellation)
        {
            _disposeTokenSource.Token.ThrowIfCancellationRequested();
            _requestRateGate.WaitToProceed();
            _disposeTokenSource.Token.ThrowIfCancellationRequested();
            _paceRequest();

            var authorizationInvoked = false;
            var authorizationGranted = false;
            bool Authorize()
            {
                if (authorizationInvoked)
                {
                    throw new InvalidOperationException(
                        "A wire action requested authorization more than once.");
                }
                authorizationInvoked = true;
                var socketConnected = _isConnected();
                lock (_callbackStateLock)
                {
                    if (_disposed || !_connected || !socketConnected ||
                        !ReferenceEquals(_pendingRequest, pending) ||
                        !IsCurrentScopeLocked(pending.Scope) ||
                        pending.RequestId == 0 && _unkeyedRequestsPoisoned)
                    {
                        throw new RequestInvalidatedException(
                            "The IB account-state request was invalidated at its wire boundary.");
                    }
                    if (!cancellation && pending.Finished)
                    {
                        return false;
                    }
                    authorizationGranted = true;
                    return true;
                }
            }

            var wrote = action(Authorize);
            if (!authorizationInvoked || wrote && !authorizationGranted)
            {
                throw new InvalidOperationException(
                    "A wire action did not honor its authorization contract.");
            }
            return wrote;
        }

        private void TryCancel(
            PendingRequest pending, WireAction cancel, string description, int requestId)
        {
            try
            {
                PaceAndInvoke(pending, cancel, cancellation: true);
            }
            catch (RequestInvalidatedException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Log.Error(
                    $"InteractiveBrokersFinancialAdvisorAccountState: failed to cancel " +
                    $"{description} request {requestId}: {exception.Message}");
            }
        }

        private int NextRequestId()
        {
            if (_nextRequestId >= -1)
            {
                throw new InvalidOperationException("The FA service request ID range is exhausted.");
            }
            return _nextRequestId++;
        }

        private void AttachCallbacks()
        {
            _client.NextValidId += OnNextValidId;
            _client.ConnectionClosed += OnConnectionClosed;
            _client.InternalManagedAccounts += OnManagedAccounts;
            _client.InternalReceiveFa += OnFinancialAdvisor;
            _client.InternalFamilyCodes += OnFamilyCodes;
            _client.AccountUpdateMultiWithRequestId += OnAccountUpdate;
            _client.AccountUpdateMultiEndWithRequestId += OnAccountUpdateEnd;
            _client.PositionMulti += OnPosition;
            _client.PositionMultiEndWithRequestId += OnPositionEnd;
            _client.InternalError += OnError;
        }

        private void DetachCallbacks()
        {
            _client.NextValidId -= OnNextValidId;
            _client.ConnectionClosed -= OnConnectionClosed;
            _client.InternalManagedAccounts -= OnManagedAccounts;
            _client.InternalReceiveFa -= OnFinancialAdvisor;
            _client.InternalFamilyCodes -= OnFamilyCodes;
            _client.AccountUpdateMultiWithRequestId -= OnAccountUpdate;
            _client.AccountUpdateMultiEndWithRequestId -= OnAccountUpdateEnd;
            _client.PositionMulti -= OnPosition;
            _client.PositionMultiEndWithRequestId -= OnPositionEnd;
            _client.InternalError -= OnError;
        }

        private void OnNextValidId(object sender, NextValidIdEventArgs args) => MarkConnected();

        private void OnConnectionClosed(object sender, EventArgs args) =>
            MarkDisconnected("Interactive Brokers connection closed.");

        private void OnManagedAccounts(object sender, ManagedAccountsEventArgs args)
        {
            PendingRequest completed = null;
            lock (_callbackStateLock)
            {
                if (_disposed || !_connected || _unkeyedRequestsPoisoned)
                {
                    return;
                }
                if (_pendingRequest is { Kind: PendingKind.ManagedAccounts, Finished: false })
                {
                    completed = _pendingRequest;
                    completed.Text = args.AccountList;
                    completed.Finished = true;
                }
                else
                {
                    _handshakeManagedAccounts = args.AccountList;
                }
            }
            completed?.Completion.TrySetResult(completed);
        }

        private void OnFinancialAdvisor(object sender, ReceiveFaEventArgs args) =>
            CompleteUnkeyed(
                PendingKind.FinancialAdvisor, args.FaDataType,
                args.FaXmlData, null);

        private void OnFamilyCodes(object sender, FamilyCodesEventArgs args) =>
            CompleteUnkeyed(
                PendingKind.FamilyCodes, null, null,
                args.FamilyCodes ?? Array.Empty<FamilyCode>());

        private void CompleteUnkeyed(
            PendingKind kind, int? faDataType, string text, FamilyCode[] familyCodes)
        {
            PendingRequest completed = null;
            lock (_callbackStateLock)
            {
                if (!_disposed && !_unkeyedRequestsPoisoned &&
                    _pendingRequest is { Finished: false } pending &&
                    pending.Kind == kind &&
                    (!faDataType.HasValue || pending.FaDataType == faDataType.Value))
                {
                    completed = pending;
                    if (text != null)
                    {
                        completed.Text = text;
                    }
                    if (familyCodes != null)
                    {
                        completed.FamilyCodes = familyCodes;
                    }
                    completed.Finished = true;
                }
            }
            completed?.Completion.TrySetResult(completed);
        }

        private void OnAccountUpdate(object sender, AccountUpdateMultiEventArgs args) =>
            AddKeyedRow(PendingKind.AccountUpdates, args.RequestId, null, args);

        private void OnAccountUpdateEnd(object sender, AccountUpdateMultiEndEventArgs args) =>
            CompleteKeyed(PendingKind.AccountUpdates, args.RequestId);

        private void OnPosition(object sender, PositionMultiEventArgs args) =>
            AddKeyedRow(PendingKind.Positions, args.RequestId, args, null);

        private void AddKeyedRow(
            PendingKind kind,
            int requestId,
            PositionMultiEventArgs position,
            AccountUpdateMultiEventArgs account)
        {
            lock (_callbackStateLock)
            {
                if (_pendingRequest is { Finished: false } pending &&
                    pending.Kind == kind && pending.RequestId == requestId)
                {
                    if (position != null)
                    {
                        pending.PositionRows.Add(position);
                    }
                    if (account != null)
                    {
                        pending.AccountRows.Add(account);
                    }
                }
            }
        }

        private void OnPositionEnd(object sender, RequestEndEventArgs args) =>
            CompleteKeyed(PendingKind.Positions, args.RequestId);

        private void CompleteKeyed(PendingKind kind, int requestId)
        {
            PendingRequest completed = null;
            lock (_callbackStateLock)
            {
                if (_pendingRequest is { Finished: false } pending &&
                    pending.Kind == kind && pending.RequestId == requestId)
                {
                    completed = pending;
                    completed.Finished = true;
                }
            }
            completed?.Completion.TrySetResult(completed);
        }

        private void OnError(object sender, ErrorEventArgs args)
        {
            if (args.Code == 1100)
            {
                MarkDisconnected(
                    $"Interactive Brokers connectivity was lost (1100): {args.Message}");
                return;
            }
            if (args.Code is 1101 or 1102 || args.Id is -1 or 0)
            {
                return;
            }
            PendingRequest completed = null;
            lock (_callbackStateLock)
            {
                if (_pendingRequest is { Finished: false } pending &&
                    pending.RequestId == args.Id)
                {
                    completed = pending;
                    completed.Finished = true;
                }
            }
            completed?.Completion.TrySetException(new InvalidOperationException(
                $"IB rejected the account-state request ({args.Code}): {args.Message}"));
        }

        private void AddPosition(
            PositionMultiEventArgs row,
            IDictionary<string, Dictionary<(Symbol, string), BrokerageAccountPosition>> positions,
            IDictionary<string, Dictionary<string, BrokerageAccountUnmappedPosition>> unmapped)
        {
            if (row.Position == 0m)
            {
                return;
            }
            BrokerageAccountPosition value;
            try
            {
                value = new BrokerageAccountPosition(
                    _mapSymbol(row.Contract), row.Position,
                    NormalizeAveragePrice(row.Contract, row.AverageCost), row.ModelCode);
            }
            catch (Exception exception)
            {
                var unknownValue = CreateUnmappedPosition(row, exception.Message);
                if (!unmapped.TryGetValue(row.Account, out var accountPositions))
                {
                    unmapped[row.Account] = accountPositions =
                        new Dictionary<string, BrokerageAccountUnmappedPosition>(
                            StringComparer.Ordinal);
                }
                var key = string.Join("|", unknownValue.BrokerageContractId,
                    unknownValue.BrokerageSymbol, unknownValue.LocalSymbol,
                    unknownValue.BrokerageSecurityType, unknownValue.Currency,
                    unknownValue.Exchange, unknownValue.PrimaryExchange,
                    unknownValue.TradingClass, unknownValue.Expiration,
                    unknownValue.BrokerageStrike, unknownValue.Right,
                    unknownValue.Multiplier, unknownValue.ModelCode);
                if (accountPositions.TryGetValue(key, out var prior) &&
                    (prior.Quantity != unknownValue.Quantity ||
                     prior.AveragePrice != unknownValue.AveragePrice))
                {
                    throw new InvalidOperationException(
                        $"IB returned conflicting duplicate unmapped positions for '{row.Account}'.");
                }
                accountPositions[key] = unknownValue;
                return;
            }
            if (!positions.TryGetValue(row.Account, out var mappedPositions))
            {
                positions[row.Account] = mappedPositions = new();
            }
            var mappedKey = (value.Symbol, value.ModelCode);
            if (mappedPositions.TryGetValue(mappedKey, out var previous) &&
                (previous.Quantity != value.Quantity ||
                 previous.AveragePrice != value.AveragePrice))
            {
                throw new InvalidOperationException(
                    $"IB returned conflicting duplicate positions for '{row.Account}'.");
            }
            mappedPositions[mappedKey] = value;
        }

        private static BrokerageAccountSnapshot CreateStatusSnapshot(
            BrokerageAccountSnapshot current,
            BrokerageAccountSnapshotStatus status,
            string error)
        {
            return new BrokerageAccountSnapshot(
                status, current.Generation, DateTime.UtcNow,
                current.LastSuccessfulUpdateUtc, current.Groups, current.Accounts,
                current.UnassignedAccountIds, current.MembershipHash,
                current.GroupConfigurationVersion, error, current.PrimaryAccountId,
                current.ManagedAccountIds, current.AllGroups, current.AccountDirectory,
                current.IsComplete, current.CollectionStartedUtc);
        }

        private bool PublishFailure(
            string error,
            bool forceStale = false,
            long? expectedRequestVersion = null) =>
            TryPublishSnapshot(
                null, error, forceStale, expectedRequestVersion,
                requireConnected: false);

        private void PublishReady(
            BrokerageAccountSnapshot snapshot, long requestVersion) =>
            TryPublishSnapshot(
                snapshot, null, forceStale: false,
                expectedRequestVersion: requestVersion,
                requireConnected: true);

        private bool TryPublishSnapshot(
            BrokerageAccountSnapshot readySnapshot,
            string error,
            bool forceStale,
            long? expectedRequestVersion,
            bool requireConnected)
        {
            BrokerageAccountSnapshot snapshot;
            lock (_callbackStateLock)
            {
                if (_disposed || requireConnected && !_connected ||
                    expectedRequestVersion.HasValue &&
                    expectedRequestVersion.Value != _requestVersion)
                {
                    return false;
                }
                snapshot = readySnapshot;
                if (snapshot == null)
                {
                    var current = _snapshot;
                    var status = forceStale ||
                        current.Status == BrokerageAccountSnapshotStatus.Stale ||
                        current.LastSuccessfulUpdateUtc != default
                            ? BrokerageAccountSnapshotStatus.Stale
                            : BrokerageAccountSnapshotStatus.Failed;
                    snapshot = CreateStatusSnapshot(current, status, error);
                }
                _snapshot = snapshot;
            }
            return true;
        }

        private void ReportUnsupported(string message)
        {
            try
            {
                _reportUnsupported?.Invoke(message);
            }
            catch (Exception exception)
            {
                Log.Error(
                    $"InteractiveBrokersFinancialAdvisorAccountState: unsupported topology " +
                    $"reporter failed: {exception.Message}");
            }
        }

        internal sealed class AccountValueBuilder
        {
            private readonly Dictionary<string, decimal> _cash =
                new(StringComparer.OrdinalIgnoreCase);
            private bool? _accountReady;
            private bool _hasLedgerPrefix;

            internal string AccountId { get; }
            internal IReadOnlyCollection<string> GroupNames { get; }
            internal string AccountType { get; private set; } = string.Empty;
            internal decimal? NetLiquidation { get; private set; }
            internal decimal? TotalCashValue { get; private set; }
            internal decimal? AvailableFunds { get; private set; }
            internal decimal? ExcessLiquidity { get; private set; }
            internal decimal? BuyingPower { get; private set; }
            internal string ValuationCurrency { get; private set; } = string.Empty;

            internal AccountValueBuilder(
                string accountId, IReadOnlyCollection<string> groupNames)
            {
                AccountId = accountId;
                GroupNames = groupNames;
            }

            internal void Apply(string tag, string value, string currency)
            {
                var ledger = tag?.StartsWith("$LEDGER", StringComparison.OrdinalIgnoreCase) == true;
                _hasLedgerPrefix |= ledger;
                var normalizedTag = NormalizeLedgerTag(tag, ref currency);
                if (normalizedTag.Equals("AccountReady", StringComparison.OrdinalIgnoreCase))
                {
                    if (!bool.TryParse(value, out var ready))
                    {
                        throw new InvalidOperationException(
                            $"IB returned invalid AccountReady value '{value}' for '{AccountId}'.");
                    }
                    _accountReady = _accountReady == false ? false : ready;
                    return;
                }
                if (normalizedTag.Equals("AccountType", StringComparison.OrdinalIgnoreCase))
                {
                    AccountType = value ?? string.Empty;
                    return;
                }
                var cashBalance = normalizedTag.Equals(
                    "CashBalance", StringComparison.OrdinalIgnoreCase);
                if (cashBalance && !ledger || ledger && !cashBalance)
                {
                    return;
                }
                var numeric = cashBalance ||
                    normalizedTag.Equals("NetLiquidation", StringComparison.OrdinalIgnoreCase) ||
                    normalizedTag.Equals("TotalCashValue", StringComparison.OrdinalIgnoreCase) ||
                    normalizedTag.Equals("TotalCashBalance", StringComparison.OrdinalIgnoreCase) ||
                    normalizedTag.Equals("AvailableFunds", StringComparison.OrdinalIgnoreCase) ||
                    normalizedTag.Equals("ExcessLiquidity", StringComparison.OrdinalIgnoreCase) ||
                    normalizedTag.Equals("BuyingPower", StringComparison.OrdinalIgnoreCase);
                if (!numeric || IsUnsetValue(value))
                {
                    return;
                }
                if (!decimal.TryParse(
                    value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    throw new InvalidOperationException(
                        $"IB returned invalid {normalizedTag} value '{value}' for '{AccountId}'.");
                }
                if (cashBalance)
                {
                    if (string.IsNullOrWhiteSpace(currency))
                    {
                        throw new InvalidOperationException(
                            $"IB returned a cash balance without a currency for '{AccountId}'.");
                    }
                    _cash[currency.Trim()] = parsed;
                }
                else if (normalizedTag.Equals(
                    "NetLiquidation", StringComparison.OrdinalIgnoreCase))
                {
                    NetLiquidation = parsed;
                    ValuationCurrency = currency ?? string.Empty;
                }
                else if (normalizedTag.Equals(
                    "TotalCashValue", StringComparison.OrdinalIgnoreCase) ||
                    normalizedTag.Equals("TotalCashBalance", StringComparison.OrdinalIgnoreCase) &&
                    "BASE".Equals(currency, StringComparison.OrdinalIgnoreCase))
                {
                    TotalCashValue = parsed;
                }
                else if (normalizedTag.Equals(
                    "AvailableFunds", StringComparison.OrdinalIgnoreCase))
                {
                    AvailableFunds = parsed;
                }
                else if (normalizedTag.Equals(
                    "ExcessLiquidity", StringComparison.OrdinalIgnoreCase))
                {
                    ExcessLiquidity = parsed;
                }
                else if (normalizedTag.Equals(
                    "BuyingPower", StringComparison.OrdinalIgnoreCase))
                {
                    BuyingPower = parsed;
                }
            }

            internal BrokerageAccountState Build(
                IEnumerable<BrokerageAccountPosition> positions,
                IEnumerable<BrokerageAccountUnmappedPosition> unmappedPositions = null)
            {
                if (_accountReady != true)
                {
                    throw new InvalidOperationException(
                        $"IB did not report that account '{AccountId}' was ready.");
                }
                if (!_hasLedgerPrefix)
                {
                    throw new InvalidOperationException(
                        $"IB returned ambiguous currency values for '{AccountId}'. Enable " +
                        "'Prepend $LEDGER- to Currency-Specific Account Values' in TWS.");
                }
                if (!NetLiquidation.HasValue || !TotalCashValue.HasValue ||
                    string.IsNullOrWhiteSpace(ValuationCurrency) || _cash.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"IB returned an incomplete account snapshot for '{AccountId}'.");
                }
                return new BrokerageAccountState(
                    AccountId, GroupNames, AccountType, NetLiquidation, TotalCashValue,
                    AvailableFunds, ExcessLiquidity, BuyingPower, ValuationCurrency,
                    _cash, positions, unmappedPositions);
            }

            private static bool IsUnsetValue(string value) =>
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var parsed) &&
                (parsed == double.MaxValue || double.IsInfinity(parsed));

            private static string NormalizeLedgerTag(string tag, ref string currency)
            {
                tag ??= string.Empty;
                if (tag.StartsWith("$LEDGER:", StringComparison.OrdinalIgnoreCase))
                {
                    return tag[8..];
                }
                if (tag.StartsWith("$LEDGER-", StringComparison.OrdinalIgnoreCase))
                {
                    var separator = tag.IndexOf(':');
                    if (separator > 8)
                    {
                        if (string.IsNullOrWhiteSpace(currency))
                        {
                            currency = tag[8..separator];
                        }
                        return tag[(separator + 1)..];
                    }
                    return tag[8..];
                }
                return tag;
            }
        }

        internal delegate bool WireAction(Func<bool> authorize);

        internal sealed class RequestActions
        {
            internal WireAction RequestManagedAccounts { get; set; }
            internal Func<int, Func<bool>, bool> RequestFinancialAdvisor { get; set; }
            internal WireAction RequestFamilyCodes { get; set; }
            internal Func<int, string, Func<bool>, bool> RequestPositions { get; set; }
            internal Func<int, Func<bool>, bool> CancelPositions { get; set; }
            internal Func<int, string, Func<bool>, bool> RequestAccountUpdates { get; set; }
            internal Func<int, Func<bool>, bool> CancelAccountUpdates { get; set; }

            internal RequestActions(InteractiveBrokersClient client)
            {
                RequestManagedAccounts = authorize =>
                    InvokeAuthorized(authorize, client.ClientSocket.reqManagedAccts);
                RequestFinancialAdvisor = (faDataType, authorize) =>
                    InvokeAuthorized(
                        authorize,
                        () => client.ClientSocket.requestFA(faDataType));
                RequestFamilyCodes = authorize =>
                    InvokeAuthorized(authorize, client.ClientSocket.reqFamilyCodes);
                RequestPositions = (id, accountOrGroup, authorize) =>
                    InvokeAuthorized(
                        authorize,
                        () => client.ClientSocket.reqPositionsMulti(
                            id, accountOrGroup, string.Empty));
                CancelPositions = (id, authorize) =>
                    InvokeAuthorized(
                        authorize,
                        () => client.ClientSocket.cancelPositionsMulti(id));
                RequestAccountUpdates = (id, accountId, authorize) =>
                    InvokeAuthorized(
                        authorize,
                        () => client.ClientSocket.reqAccountUpdatesMulti(
                            id, accountId, string.Empty, ledgerAndNLV: false));
                CancelAccountUpdates = (id, authorize) =>
                    InvokeAuthorized(
                        authorize,
                        () => client.ClientSocket.cancelAccountUpdatesMulti(id));
            }

            private static bool InvokeAuthorized(
                Func<bool> authorize, Action wireCall)
            {
                if (!authorize())
                {
                    return false;
                }
                wireCall();
                return true;
            }
        }

        private enum PendingKind
        {
            ManagedAccounts,
            FinancialAdvisor,
            FamilyCodes,
            Positions,
            AccountUpdates
        }

        private sealed class PendingRequest
        {
            internal SnapshotScope Scope { get; }
            internal PendingKind Kind { get; }
            internal int RequestId { get; }
            internal int FaDataType { get; }
            internal TaskCompletionSource<PendingRequest> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal List<PositionMultiEventArgs> PositionRows { get; } = new();
            internal List<AccountUpdateMultiEventArgs> AccountRows { get; } = new();
            internal FamilyCode[] FamilyCodes { get; set; } = Array.Empty<FamilyCode>();
            internal string Text { get; set; } = string.Empty;
            internal bool WireSent { get; set; }
            internal bool Finished { get; set; }

            internal PendingRequest(
                SnapshotScope scope, PendingKind kind, int requestId, int faDataType)
            {
                Scope = scope;
                Kind = kind;
                RequestId = requestId;
                FaDataType = faDataType;
            }
        }

        private sealed class UnkeyedRequestTimeoutException : TimeoutException
        {
            internal UnkeyedRequestTimeoutException(string message, Exception inner)
                : base(message, inner)
            {
            }
        }

        private sealed class RequestInvalidatedException : InvalidOperationException
        {
            internal RequestInvalidatedException(string message) : base(message)
            {
            }
        }
    }
}
