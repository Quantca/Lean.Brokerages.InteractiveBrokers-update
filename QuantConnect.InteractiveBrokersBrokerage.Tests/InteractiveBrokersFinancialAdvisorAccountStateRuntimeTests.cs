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
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IBApi;
using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.InteractiveBrokers;
using QuantConnect.Brokerages.InteractiveBrokers.Client;

namespace QuantConnect.Tests.Brokerages.InteractiveBrokers
{
    [TestFixture]
    public class InteractiveBrokersFinancialAdvisorAccountStateRuntimeTests
    {
        [Test]
        public async Task BrokerageOperationsAreSerializedTest()
        {
            using var scenario = new Scenario();
            using var state = scenario.CreateState();

            var snapshot = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));

            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Ready, snapshot.Status);
                Assert.AreEqual(1, snapshot.Generation);
                Assert.IsTrue(snapshot.IsComplete);
                Assert.AreEqual(2, scenario.GroupsRequestCount);
                Assert.AreEqual(
                    InteractiveBrokersFinancialAdvisorAccountState.ComputeConfigurationHash(
                        Scenario.GroupsXml),
                    snapshot.GroupConfigurationVersion);
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "managed",
                        "fa:1",
                        "fa:3",
                        "family",
                        "positions:Alpha",
                        "positions:Beta",
                        "positions:ACC3",
                        "account:ACC1",
                        "account:ACC2",
                        "account:ACC3",
                        "fa:1"
                    },
                    scenario.Requests);
                CollectionAssert.AreEqual(
                    Enumerable.Range(0, 6).Select(offset => int.MinValue + offset),
                    scenario.KeyedRequestIds);
                Assert.IsTrue(scenario.KeyedRequestIds.All(
                    InteractiveBrokersFinancialAdvisorAccountState.IsServiceRequestId));
                Assert.AreEqual(2, snapshot.Groups.Count);
                Assert.AreEqual(2, snapshot.AllGroups.Count);
                Assert.AreEqual(3, snapshot.Accounts.Count);
                Assert.AreEqual(4, snapshot.AccountDirectory.Count);
                CollectionAssert.AreEqual(new[] { "ACC3" }, snapshot.UnassignedAccountIds);
                Assert.AreEqual(1, scenario.MaximumConcurrentExternalCalls);
            });
        }

        [Test]
        public async Task SnapshotGenerationMonotonicTest()
        {
            using var scenario = Scenario.SingleAccount();
            using var state = scenario.CreateState();

            var first = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));
            var firstAccountIds = first.Accounts.Keys.ToArray();
            var second = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));

            Assert.Throws<NotSupportedException>(() =>
                ((IDictionary<string, BrokerageAccountState>)first.Accounts).Remove("ACC1"));
            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Ready, first.Status);
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Ready, second.Status);
                Assert.AreEqual(1, first.Generation);
                Assert.AreEqual(2, second.Generation);
                Assert.Greater(second.Generation, first.Generation);
                CollectionAssert.AreEqual(firstAccountIds, first.Accounts.Keys);
                Assert.GreaterOrEqual(second.LastSuccessfulUpdateUtc,
                    first.LastSuccessfulUpdateUtc);
            });
        }

        [Test]
        public async Task OrdinaryRefreshFailureDoesNotBlockGroupTradingTest()
        {
            using var scenario = Scenario.SingleAccount();
            using var state = scenario.CreateState();
            var ready = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));
            scenario.Actions.RequestPositions =
                (requestId, accountOrGroup, authorize) =>
                    scenario.RunAuthorized(authorize, () =>
                        throw new InvalidOperationException(
                            "simulated ordinary position refresh failure"));

            var stale = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));

            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Ready, ready.Status);
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Stale, stale.Status);
                StringAssert.Contains(
                    "simulated ordinary position refresh failure", stale.ErrorMessage);
                Assert.IsFalse(state.IsGroupTradingBlocked);
            });
        }

        [TestCase("managed", false)]
        [TestCase("managed", true)]
        [TestCase("fa:1", false)]
        [TestCase("fa:1", true)]
        [TestCase("fa:3", false)]
        [TestCase("fa:3", true)]
        [TestCase("family", false)]
        [TestCase("family", true)]
        public async Task UnkeyedSocketExceptionUsesAuthorizationBoundaryTest(
            string request,
            bool afterAuthorization)
        {
            using var scenario = new Scenario();
            var managedAccounts = scenario.Actions.RequestManagedAccounts;
            var financialAdvisor = scenario.Actions.RequestFinancialAdvisor;
            var familyCodes = scenario.Actions.RequestFamilyCodes;
            bool Fail(Func<bool> authorize)
            {
                if (afterAuthorization && !authorize())
                {
                    return false;
                }
                throw new System.Net.Sockets.SocketException(
                    (int)System.Net.Sockets.SocketError.ConnectionReset);
            }
            scenario.Actions.RequestManagedAccounts = authorize =>
                request == "managed" ? Fail(authorize) : managedAccounts(authorize);
            scenario.Actions.RequestFinancialAdvisor = (faDataType, authorize) =>
                request == $"fa:{faDataType}"
                    ? Fail(authorize)
                    : financialAdvisor(faDataType, authorize);
            scenario.Actions.RequestFamilyCodes = authorize =>
                request == "family" ? Fail(authorize) : familyCodes(authorize);
            using var state = scenario.CreateState();

            var failed = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));

            scenario.Actions.RequestManagedAccounts = managedAccounts;
            scenario.Actions.RequestFinancialAdvisor = financialAdvisor;
            scenario.Actions.RequestFamilyCodes = familyCodes;
            if (afterAuthorization)
            {
                Assert.Multiple(() =>
                {
                    Assert.AreEqual(BrokerageAccountSnapshotStatus.Stale, failed.Status);
                    Assert.IsFalse(state.RequestRefresh(Array.Empty<string>()));
                });
                return;
            }

            Assert.AreEqual(BrokerageAccountSnapshotStatus.Failed, failed.Status);
            var recovered = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));
            Assert.AreEqual(BrokerageAccountSnapshotStatus.Ready, recovered.Status);
        }

        [Test]
        public async Task UnkeyedTimeoutRequiresFreshConnectionTest()
        {
            using var scenario = new Scenario
            {
                ManagedAccounts = "MASTER,ACC1",
                GroupsDocument = Scenario.EmptyGroupsXml,
                EndingGroupsDocument = Scenario.EmptyGroupsXml,
                AliasesDocument = "<ListOfAccountAliases />",
                FamilyCodes = Array.Empty<FamilyCode>()
            };
            scenario.Actions.RequestManagedAccounts = authorize =>
            {
                if (!authorize())
                {
                    return false;
                }
                scenario.Requests.Add("managed-timeout");
                return true;
            };
            using var state = scenario.CreateState(requestTimeout: TimeSpan.FromMilliseconds(100));

            var timeout = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));

            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Stale, timeout.Status);
                StringAssert.Contains("Timed out waiting for IB managed accounts", timeout.ErrorMessage);
                Assert.IsFalse(state.RequestRefresh(Array.Empty<string>()));
            });

            // A late unkeyed callback cannot be reused after the timeout.
            scenario.Client.managedAccounts(scenario.ManagedAccounts);
            Assert.IsFalse(state.RequestRefresh(Array.Empty<string>()));
            scenario.Client.error(-1, 0, 1101, "Connectivity restored; data lost.", string.Empty);
            Assert.IsFalse(state.RequestRefresh(Array.Empty<string>()));
            scenario.Client.error(-1, 0, 1102, "Connectivity restored; data maintained.", string.Empty);
            Assert.IsFalse(state.RequestRefresh(Array.Empty<string>()));

            scenario.Actions.RequestManagedAccounts = authorize =>
                scenario.RunAuthorized(authorize, () =>
                {
                    scenario.Requests.Add("managed-reconnected");
                    scenario.Client.managedAccounts(scenario.ManagedAccounts);
                });
            scenario.Client.nextValidId(123);
            Assert.IsFalse(state.RequestRefresh(Array.Empty<string>()));
            scenario.Client.connectionClosed();
            scenario.Client.nextValidId(124);

            var recovered = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));

            Assert.AreEqual(BrokerageAccountSnapshotStatus.Ready, recovered.Status);
            Assert.AreEqual(1, recovered.Generation);
        }

        [TestCase(1101)]
        [TestCase(1102)]
        public async Task LogicalReconnectRestoresAvailabilityWithoutOutstandingUnkeyedRequestTest(
            int recoveryCode)
        {
            using var scenario = Scenario.SingleAccount();
            using var state = scenario.CreateState();
            var ready = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));

            scenario.Client.error(
                -1, 0, 1100, "Connectivity between IB and TWS was lost.", string.Empty);

            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Stale, state.Snapshot.Status);
                Assert.IsFalse(state.RequestRefresh(Array.Empty<string>()));
            });
            var disconnectedRequestVersion = GetRequestVersion(state);
            var disconnectedRequestCount = scenario.Requests.Count;

            scenario.Client.error(
                -1, 0, recoveryCode, "Connectivity between IB and TWS was restored.",
                string.Empty);
            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Stale, state.Snapshot.Status);
                Assert.AreEqual(disconnectedRequestVersion, GetRequestVersion(state));
                Assert.AreEqual(disconnectedRequestCount, scenario.Requests.Count);
            });
            var recovered = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));

            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Ready, recovered.Status);
                Assert.AreEqual(ready.Generation + 1, recovered.Generation);
            });
        }

        [Test]
        public async Task LogicalReconnectDoesNotClearOutstandingUnkeyedRequestTest()
        {
            using var scenario = Scenario.SingleAccount();
            using var wireSent = new ManualResetEventSlim();
            scenario.Actions.RequestManagedAccounts = authorize =>
            {
                if (!authorize())
                {
                    return false;
                }
                wireSent.Set();
                return true;
            };
            using var state = scenario.CreateState();
            var interruptedRefresh = RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));
            Assert.IsTrue(wireSent.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(SpinWait.SpinUntil(
                () => IsPendingRequestWireSent(state), TimeSpan.FromSeconds(5)));

            scenario.Client.error(
                -1, 0, 1100, "Connectivity between IB and TWS was lost.", string.Empty);
            var stale = await interruptedRefresh;
            scenario.Client.error(
                -1, 0, 1102, "Connectivity between IB and TWS was restored.", string.Empty);

            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Stale, stale.Status);
                Assert.IsFalse(state.RequestRefresh(Array.Empty<string>()));
            });

            scenario.Actions.RequestManagedAccounts = authorize =>
                scenario.RunAuthorized(authorize, () =>
                {
                    scenario.Requests.Add("managed-reconnected");
                    scenario.Client.managedAccounts(scenario.ManagedAccounts);
                });
            scenario.Client.nextValidId(123);
            Assert.IsFalse(state.RequestRefresh(Array.Empty<string>()));
            scenario.Client.connectionClosed();
            scenario.Client.nextValidId(124);

            var recovered = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));
            Assert.AreEqual(BrokerageAccountSnapshotStatus.Ready, recovered.Status);
        }

        [Test]
        public async Task PhysicalReconnectQueuesRefreshOnlyAfterPriorAcceptedRequestTest()
        {
            using (var unusedScenario = Scenario.SingleAccount())
            using (var unusedState = unusedScenario.CreateState())
            {
                unusedScenario.Client.connectionClosed();
                unusedScenario.Client.nextValidId(123);
                unusedScenario.Client.nextValidId(124);

                Assert.Multiple(() =>
                {
                    CollectionAssert.IsEmpty(unusedScenario.Requests);
                    Assert.AreEqual(
                        BrokerageAccountSnapshotStatus.Stale,
                        unusedState.Snapshot.Status);
                });
            }

            using var scenario = new Scenario();
            using var state = scenario.CreateState();
            var alpha = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(new[] { "Alpha" }));

            scenario.Client.connectionClosed();
            Assert.IsFalse(state.RequestRefresh(
                new[] { "Beta" }, new[] { "ACC3" }));
            scenario.Requests.Clear();
            scenario.Client.nextValidId(125);
            var recovered = await WaitForReadyGenerationAsync(
                state, alpha.Generation);

            CollectionAssert.AreEqual(
                new[]
                {
                    "managed",
                    "fa:1",
                    "fa:3",
                    "family",
                    "positions:Alpha",
                    "account:ACC1",
                    "fa:1"
                },
                scenario.Requests);
            var requestCount = scenario.Requests.Count;
            var requestVersion = GetRequestVersion(state);

            scenario.Client.nextValidId(126);
            await Task.Delay(50);

            Assert.Multiple(() =>
            {
                Assert.AreEqual(alpha.Generation + 1, recovered.Generation);
                Assert.IsFalse(recovered.IsComplete);
                CollectionAssert.AreEquivalent(
                    new[] { "Alpha" }, recovered.Groups.Keys);
                CollectionAssert.AreEquivalent(
                    new[] { "ACC1" }, recovered.Accounts.Keys);
                Assert.AreEqual(requestCount, scenario.Requests.Count);
                Assert.AreEqual(requestVersion, GetRequestVersion(state));
            });
        }

        [Test]
        public async Task QueuedScopeDuringUnkeyedTimeoutRemainsStaleTest()
        {
            using var scenario = Scenario.SingleAccount();
            using var wireSent = new ManualResetEventSlim();
            scenario.Actions.RequestManagedAccounts = authorize =>
            {
                if (!authorize())
                {
                    return false;
                }
                scenario.Requests.Add("managed-timeout");
                wireSent.Set();
                return true;
            };
            using var state = scenario.CreateState(
                requestTimeout: TimeSpan.FromMilliseconds(100));

            var timeoutTask = RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));
            Assert.IsTrue(wireSent.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(state.RequestRefresh(
                Array.Empty<string>(), new[] { "ACC1" }));

            var timeout = await timeoutTask;
            await Task.Delay(50);

            Assert.Multiple(() =>
            {
                Assert.AreEqual(
                    BrokerageAccountSnapshotStatus.Stale, timeout.Status);
                Assert.AreEqual(
                    BrokerageAccountSnapshotStatus.Stale, state.Snapshot.Status);
                StringAssert.Contains(
                    "Timed out waiting for IB managed accounts",
                    state.Snapshot.ErrorMessage);
                CollectionAssert.AreEqual(
                    new[] { "managed-timeout" }, scenario.Requests);
                Assert.IsFalse(state.RequestRefresh(Array.Empty<string>()));
            });
        }

        [Test]
        public async Task ReconnectBetweenCallbackAndNextInstallRejectsObsoleteScopeTest()
        {
            using var scenario = Scenario.SingleAccount();
            using var reconnected = new ManualResetEventSlim();
            using var releaseOldAction = new ManualResetEventSlim();
            var requestManagedAccounts = scenario.Actions.RequestManagedAccounts;
            scenario.Actions.RequestManagedAccounts = authorize =>
            {
                if (!authorize())
                {
                    return false;
                }
                scenario.Requests.Add("managed-obsolete");
                scenario.Client.managedAccounts(scenario.ManagedAccounts);
                scenario.Client.connectionClosed();
                scenario.Client.nextValidId(456);
                reconnected.Set();
                if (!releaseOldAction.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        "The obsolete wire action was not released.");
                }
                return true;
            };
            using var state = scenario.CreateState();

            Assert.IsTrue(state.RequestRefresh(Array.Empty<string>()));
            try
            {
                Assert.IsTrue(reconnected.Wait(TimeSpan.FromSeconds(5)));
                scenario.Actions.RequestManagedAccounts = requestManagedAccounts;
                releaseOldAction.Set();
                var recovered = await WaitForReadyGenerationAsync(state, 0);

                Assert.Multiple(() =>
                {
                    Assert.AreEqual(
                        BrokerageAccountSnapshotStatus.Ready, recovered.Status);
                    CollectionAssert.AreEqual(
                        new[] { "managed-obsolete", "managed" },
                        scenario.Requests.Take(2));
                    Assert.AreEqual(1, recovered.Generation);
                });
            }
            finally
            {
                releaseOldAction.Set();
            }
        }

        [Test]
        public async Task InvalidationAtWireBoundaryPreventsSocketActionTest()
        {
            using var scenario = Scenario.SingleAccount();
            using var boundaryReturned = new ManualResetEventSlim();
            var requestManagedAccounts = scenario.Actions.RequestManagedAccounts;
            scenario.Actions.RequestManagedAccounts = authorize =>
            {
                try
                {
                    scenario.Client.connectionClosed();
                    scenario.Client.nextValidId(789);
                    if (!authorize())
                    {
                        return false;
                    }
                    scenario.Requests.Add("unauthorized-managed-write");
                    scenario.Client.managedAccounts(scenario.ManagedAccounts);
                    return true;
                }
                finally
                {
                    scenario.Actions.RequestManagedAccounts = requestManagedAccounts;
                    boundaryReturned.Set();
                }
            };
            using var state = scenario.CreateState();

            Assert.IsTrue(state.RequestRefresh(Array.Empty<string>()));
            Assert.IsTrue(boundaryReturned.Wait(TimeSpan.FromSeconds(5)));
            var recovered = await WaitForReadyGenerationAsync(state, 0);

            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Ready, recovered.Status);
                CollectionAssert.DoesNotContain(
                    scenario.Requests, "unauthorized-managed-write");
            });
        }

        [Test]
        public async Task DisconnectDuringPacingDoesNotWriteOnFreshConnectionTest()
        {
            using var scenario = Scenario.SingleAccount();
            using var paceEntered = new ManualResetEventSlim();
            using var releasePacing = new ManualResetEventSlim();
            var paceCalls = 0;
            using var state = scenario.CreateState(paceRequest: () =>
            {
                if (Interlocked.Increment(ref paceCalls) == 1)
                {
                    paceEntered.Set();
                    if (!releasePacing.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("Test pacing was not released.");
                    }
                }
            });

            Assert.IsTrue(state.RequestRefresh(Array.Empty<string>()));
            try
            {
                Assert.IsTrue(paceEntered.Wait(TimeSpan.FromSeconds(5)));
                scenario.Client.connectionClosed();
                Assert.AreEqual(
                    BrokerageAccountSnapshotStatus.Stale, state.Snapshot.Status);
                scenario.Client.nextValidId(456);
            }
            finally
            {
                releasePacing.Set();
            }

            var recovered = await WaitForReadyGenerationAsync(state, 0);

            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Ready, recovered.Status);
                Assert.AreEqual(1, scenario.Requests.Count(request => request == "managed"),
                    "The pre-disconnect pending request must not write on the fresh connection.");
            });
        }

        [Test]
        public async Task DisconnectDuringCancelDoesNotCancelOnFreshConnectionTest()
        {
            using var scenario = Scenario.SingleAccount();
            using var cancelPaceEntered = new ManualResetEventSlim();
            using var releaseCancelPacing = new ManualResetEventSlim();
            var paceCalls = 0;
            using var state = scenario.CreateState(paceRequest: () =>
            {
                if (Interlocked.Increment(ref paceCalls) == 6)
                {
                    cancelPaceEntered.Set();
                    if (!releaseCancelPacing.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("Test cancellation pacing was not released.");
                    }
                }
            });

            Assert.IsTrue(state.RequestRefresh(Array.Empty<string>()));
            var staleRequestId = 0;
            try
            {
                Assert.IsTrue(cancelPaceEntered.Wait(TimeSpan.FromSeconds(5)));
                staleRequestId = scenario.KeyedRequestIds.Single();
                scenario.Client.connectionClosed();
                Assert.AreEqual(
                    BrokerageAccountSnapshotStatus.Stale, state.Snapshot.Status);
                scenario.Client.nextValidId(789);
            }
            finally
            {
                releaseCancelPacing.Set();
            }

            var recovered = await WaitForReadyGenerationAsync(state, 0);

            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Ready, recovered.Status);
                Assert.IsFalse(scenario.CanceledPositionIds.Contains(staleRequestId));
                Assert.AreEqual(1, scenario.CanceledPositionIds.Count,
                    "Only the recovered pass may send a position cancellation.");
            });
        }

        [Test]
        public async Task QueuedScopesAreMergedAndCompleteDiscoveryDominatesTest()
        {
            using var scenario = new Scenario();
            using var paceEntered = new ManualResetEventSlim();
            using var releasePacing = new ManualResetEventSlim();
            var paceCalls = 0;
            using var state = scenario.CreateState(paceRequest: () =>
            {
                if (Interlocked.Increment(ref paceCalls) == 1)
                {
                    paceEntered.Set();
                    if (!releasePacing.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("Test pacing was not released.");
                    }
                }
            });

            var refresh = RunRefreshAsync(
                state,
                () => state.RequestRefresh(new[] { "Alpha" }));
            try
            {
                Assert.IsTrue(paceEntered.Wait(TimeSpan.FromSeconds(5)));
                var version = GetRequestVersion(state);
                Assert.IsTrue(state.RequestRefresh(new[] { "Alpha" }));
                Assert.AreEqual(version, GetRequestVersion(state));
                Assert.IsFalse(HasQueuedRefresh(state));

                Assert.IsTrue(state.RequestRefresh(
                    new[] { "Beta" }, new[] { "ACC3" }));
                Assert.IsTrue(state.RequestRefresh(new[] { "Alpha" }));

                var partial = GetQueuedScope(state);
                CollectionAssert.AreEqual(new[] { "Beta", "Alpha" }, partial.Groups);
                CollectionAssert.AreEqual(new[] { "ACC3" }, partial.AdditionalAccounts);
                Assert.IsFalse(partial.CompleteDiscovery);

                Assert.IsTrue(state.RequestRefresh(Array.Empty<string>()));
                var complete = GetQueuedScope(state);
                CollectionAssert.AreEqual(new[] { "Beta", "Alpha" }, complete.Groups);
                CollectionAssert.AreEqual(new[] { "ACC3" }, complete.AdditionalAccounts);
                Assert.IsTrue(complete.CompleteDiscovery);
            }
            finally
            {
                releasePacing.Set();
            }

            var snapshot = await refresh;
            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Ready, snapshot.Status);
                Assert.IsTrue(snapshot.IsComplete);
                CollectionAssert.AreEquivalent(
                    new[] { "Alpha", "Beta" }, snapshot.Groups.Keys);
                CollectionAssert.AreEquivalent(
                    new[] { "ACC1", "ACC2", "ACC3" }, snapshot.Accounts.Keys);
            });
        }

        [Test]
        public async Task FullChannelDoesNotAdvanceVersionOrOrphanActiveRefreshTest()
        {
            using var scenario = new Scenario();
            using var paceEntered = new ManualResetEventSlim();
            using var releasePacing = new ManualResetEventSlim();
            var paceCalls = 0;
            using var state = scenario.CreateState(paceRequest: () =>
            {
                if (Interlocked.Increment(ref paceCalls) == 1)
                {
                    paceEntered.Set();
                    if (!releasePacing.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("Test pacing was not released.");
                    }
                }
            });

            var activeRefresh = RunRefreshAsync(
                state,
                () => state.RequestRefresh(new[] { "Alpha" }));
            try
            {
                Assert.IsTrue(paceEntered.Wait(TimeSpan.FromSeconds(5)));
                FillRefreshQueue(state);
                var requestVersion = GetRequestVersion(state);

                Assert.IsFalse(state.RequestRefresh(new[] { "Beta" }));
                Assert.AreEqual(requestVersion, GetRequestVersion(state));
            }
            finally
            {
                releasePacing.Set();
            }

            var snapshot = await activeRefresh;
            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Ready, snapshot.Status);
                CollectionAssert.AreEqual(new[] { "Alpha" }, snapshot.Groups.Keys);
            });
        }

        [Test]
        public async Task UnkeyedGlobalErrorsAreIgnoredTest()
        {
            using var scenario = Scenario.SingleAccount();
            var requestManagedAccounts = scenario.Actions.RequestManagedAccounts;
            scenario.Actions.RequestManagedAccounts = authorize =>
            {
                scenario.Client.error(
                    -1, 0, 399, "Unrelated global warning.", string.Empty);
                scenario.Client.error(
                    0, 0, 321, "Unrelated request-zero warning.", string.Empty);
                return requestManagedAccounts(authorize);
            };
            using var state = scenario.CreateState();

            var snapshot = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));

            Assert.AreEqual(BrokerageAccountSnapshotStatus.Ready, snapshot.Status);
        }

        [Test]
        public async Task FaRowsRequireOwnedRequestIdTest()
        {
            using var positionScenario = Scenario.SingleAccount();
            positionScenario.Actions.RequestPositions =
                (requestId, accountOrGroup, authorize) =>
            {
                if (!authorize())
                {
                    return false;
                }
                positionScenario.Requests.Add($"positions:{accountOrGroup}");
                positionScenario.KeyedRequestIds.Add(requestId);
                positionScenario.Client.positionMulti(
                    requestId + 1,
                    accountOrGroup,
                    string.Empty,
                    Scenario.MappedContract(),
                    9.5m,
                    42);
                positionScenario.Client.positionMultiEnd(requestId + 1);
                return true;
            };
            using var positionState = positionScenario.CreateState(
                requestTimeout: TimeSpan.FromMilliseconds(100));

            var positionSnapshot = await RunRefreshAsync(
                positionState,
                () => positionState.RequestRefresh(Array.Empty<string>()));

            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Failed, positionSnapshot.Status);
                StringAssert.Contains("Timed out waiting for IB positions for 'ACC1'",
                    positionSnapshot.ErrorMessage);
                Assert.AreEqual(1, positionScenario.CanceledPositionIds.Count);
                Assert.AreEqual(positionScenario.KeyedRequestIds.Single(),
                    positionScenario.CanceledPositionIds.Single());
                Assert.IsTrue(InteractiveBrokersFinancialAdvisorAccountState.IsServiceRequestId(
                    positionScenario.CanceledPositionIds.Single()));
                Assert.IsEmpty(positionScenario.AccountRequestIds);
            });

            using var accountScenario = Scenario.SingleAccount();
            accountScenario.Actions.RequestAccountUpdates =
                (requestId, accountId, authorize) =>
            {
                if (!authorize())
                {
                    return false;
                }
                accountScenario.Requests.Add($"account:{accountId}");
                accountScenario.KeyedRequestIds.Add(requestId);
                accountScenario.AccountRequestIds.Add(requestId);
                accountScenario.Client.accountUpdateMulti(
                    requestId + 1,
                    accountId,
                    string.Empty,
                    "AccountReady",
                    "true",
                    string.Empty);
                accountScenario.Client.accountUpdateMultiEnd(requestId + 1);
                return true;
            };
            using var accountState = accountScenario.CreateState(
                requestTimeout: TimeSpan.FromMilliseconds(100));

            var accountSnapshot = await RunRefreshAsync(
                accountState,
                () => accountState.RequestRefresh(Array.Empty<string>()));

            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Failed, accountSnapshot.Status);
                StringAssert.Contains("Timed out waiting for IB account updates for 'ACC1'",
                    accountSnapshot.ErrorMessage);
                Assert.AreEqual(1, accountScenario.CanceledAccountIds.Count);
                Assert.AreEqual(accountScenario.AccountRequestIds.Single(),
                    accountScenario.CanceledAccountIds.Single());
                Assert.IsTrue(InteractiveBrokersFinancialAdvisorAccountState.IsServiceRequestId(
                    accountScenario.CanceledAccountIds.Single()));
            });

            using var summaryScenario = Scenario.SingleAccount();
            summaryScenario.Actions.RequestAccountUpdates =
                (requestId, accountId, authorize) =>
                    summaryScenario.RunAuthorized(authorize, () =>
                    {
                        summaryScenario.Client.accountUpdateMulti(
                            requestId, "All", string.Empty,
                            "AccountReady", "false", string.Empty);
                        summaryScenario.Client.accountUpdateMulti(
                            requestId, "MASTER", string.Empty,
                            "NetLiquidation", "999999", "USD");
                        summaryScenario.Client.accountUpdateMulti(
                            requestId, "MASTERA", string.Empty,
                            "TotalCashValue", "999999", "USD");
                        summaryScenario.EmitAccountValues(requestId, accountId);
                    });
            using var summaryState = summaryScenario.CreateState();

            var summarySnapshot = await RunRefreshAsync(
                summaryState,
                () => summaryState.RequestRefresh(Array.Empty<string>()));

            Assert.Multiple(() =>
            {
                Assert.AreEqual(
                    BrokerageAccountSnapshotStatus.Ready, summarySnapshot.Status);
                Assert.AreEqual(
                    1000.25m, summarySnapshot.Accounts["ACC1"].NetLiquidation);
                Assert.AreEqual(
                    250.50m, summarySnapshot.Accounts["ACC1"].TotalCashValue);
            });
        }

        [Test]
        public async Task ScopedRefreshRetainsCompleteTopologyTest()
        {
            using var scenario = new Scenario();
            using var state = scenario.CreateState(configuredGroup: "Alpha");

            var snapshot = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));

            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Ready, snapshot.Status);
                Assert.IsFalse(snapshot.IsComplete);
                CollectionAssert.AreEqual(new[] { "Alpha" }, snapshot.Groups.Keys);
                CollectionAssert.AreEquivalent(new[] { "Alpha", "Beta" },
                    snapshot.AllGroups.Keys);
                CollectionAssert.AreEqual(new[] { "ACC1" }, snapshot.Accounts.Keys);
                CollectionAssert.AreEqual(new[] { "ACC3" }, snapshot.UnassignedAccountIds);
                CollectionAssert.AreEquivalent(
                    new[] { "MASTER", "ACC1", "ACC2", "ACC3" },
                    snapshot.AccountDirectory.Keys);
                CollectionAssert.AreEqual(new[] { "positions:Alpha" },
                    scenario.Requests.Where(request =>
                        request.StartsWith("positions:", StringComparison.Ordinal)));
                CollectionAssert.AreEqual(new[] { "account:ACC1" },
                    scenario.Requests.Where(request =>
                        request.StartsWith("account:", StringComparison.Ordinal)));
            });
        }

        [Test]
        public async Task ScopedRefreshRejectsUnsupportedSavedMethodOnlyWhenTargetedTest()
        {
            const string groups = """
                <ListOfGroups>
                  <Group>
                    <name>Alpha</name>
                    <defaultMethod>NetLiq</defaultMethod>
                    <ListOfAccts><String>ACC1</String></ListOfAccts>
                  </Group>
                  <Group>
                    <name>Monetary</name>
                    <defaultMethod>MonetaryAmount</defaultMethod>
                    <ListOfAccts><String>ACC2</String></ListOfAccts>
                  </Group>
                  <Group>
                    <name>SavedPctChange</name>
                    <defaultMethod>PctChange</defaultMethod>
                    <ListOfAccts><String>ACC3</String></ListOfAccts>
                  </Group>
                </ListOfGroups>
                """;
            using var scenario = new Scenario
            {
                GroupsDocument = groups,
                EndingGroupsDocument = groups
            };
            using var state = scenario.CreateState(configuredGroup: "Alpha");

            var snapshot = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));
            var supportedOrder = new IBApi.Order { FaGroup = "Alpha" };
            var unsupportedOrder = new IBApi.Order { FaGroup = "Monetary" };
            var savedPctChangeOrder = new IBApi.Order { FaGroup = "SavedPctChange" };

            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Ready, snapshot.Status);
                CollectionAssert.AreEqual(new[] { "Alpha" }, snapshot.Groups.Keys);
                CollectionAssert.AreEquivalent(
                    new[] { "Alpha", "Monetary", "SavedPctChange" },
                    snapshot.AllGroups.Keys);
                CollectionAssert.IsSubsetOf(
                    new[] { "ACC1", "ACC2", "ACC3" }, snapshot.AccountDirectory.Keys);
                CollectionAssert.Contains(
                    snapshot.AccountDirectory["ACC3"].GroupNames,
                    "SavedPctChange");
                Assert.DoesNotThrow(() =>
                    InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                        supportedOrder,
                        snapshot));
                StringAssert.Contains(
                    "unsupported saved allocation method 'MonetaryAmount'",
                    Assert.Throws<NotSupportedException>(() =>
                        InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                            unsupportedOrder,
                            snapshot)).Message);
                StringAssert.Contains(
                    "unsupported saved allocation method 'PctChange'",
                    Assert.Throws<NotSupportedException>(() =>
                        InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                            savedPctChangeOrder,
                            snapshot)).Message);
            });
        }

        [Test]
        public async Task NoExternalCallUnderSynchronizationTest()
        {
            using var scenario = new Scenario();
            object callbackStateLock = null;
            var callObservedUnderLock = false;
            scenario.ExternalCallProbe = () =>
            {
                if (callbackStateLock != null)
                {
                    callObservedUnderLock |= Monitor.IsEntered(callbackStateLock);
                }
            };
            using var state = scenario.CreateState(
                paceRequest: scenario.ExternalCallProbe,
                mapSymbol: contract =>
                {
                    scenario.ExternalCallProbe();
                    return contract.Symbol == "UNMAPPED"
                        ? throw new InvalidOperationException("No LEAN symbol mapping.")
                        : Symbol.Create(contract.Symbol, SecurityType.Equity, Market.USA);
                });
            callbackStateLock = typeof(InteractiveBrokersFinancialAdvisorAccountState)
                .GetField("_callbackStateLock",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(state);
            Assert.IsNotNull(callbackStateLock);

            var snapshot = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));

            Assert.AreEqual(BrokerageAccountSnapshotStatus.Ready, snapshot.Status);
            Assert.IsFalse(callObservedUnderLock,
                "An injected request, pacing, mapping, or publication callback ran under the callback-state lock.");
        }

        [Test]
        public async Task SemanticTopologyFailureIsBrokerageExceptionTest()
        {
            const string primaryInGroup = """
                <ListOfGroups>
                  <Group>
                    <name>Invalid</name>
                    <defaultMethod>NetLiq</defaultMethod>
                    <ListOfAccts><String>MASTER</String></ListOfAccts>
                  </Group>
                </ListOfGroups>
                """;
            using var scenario = Scenario.SingleAccount();
            scenario.GroupsDocument = primaryInGroup;
            scenario.EndingGroupsDocument = primaryInGroup;
            string reported = null;
            object callbackStateLock = null;
            var reporterCalledUnderLock = false;
            using var state = scenario.CreateState(
                reportUnsupported: message =>
                {
                    reporterCalledUnderLock = callbackStateLock != null &&
                        Monitor.IsEntered(callbackStateLock);
                    reported = message;
                });
            callbackStateLock = typeof(InteractiveBrokersFinancialAdvisorAccountState)
                .GetField("_callbackStateLock",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(state);
            Assert.IsNotNull(callbackStateLock);

            var snapshot = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));

            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Failed, snapshot.Status);
                Assert.AreEqual(snapshot.ErrorMessage, reported);
                Assert.AreEqual(snapshot.ErrorMessage, state.UnsupportedConfigurationError);
                Assert.IsFalse(reporterCalledUnderLock);
                StringAssert.Contains("not a managed subaccount", snapshot.ErrorMessage);
                StringAssert.Contains("Correct the group membership in TWS",
                    snapshot.ErrorMessage);
                Assert.IsTrue(typeof(InteractiveBrokersFinancialAdvisorAccountState
                    .UnsupportedFinancialAdvisorConfigurationException)
                    .IsSubclassOf(typeof(InvalidOperationException)));
                Assert.IsFalse(scenario.Requests.Any(request =>
                    request.StartsWith("positions:", StringComparison.Ordinal) ||
                    request.StartsWith("account:", StringComparison.Ordinal)));
            });
        }

        [TestCase("MonetaryAmount")]
        [TestCase("PctChange")]
        [TestCase("UnrecognizedMethod")]
        public async Task UnsupportedSavedMethodLatchPersistsUntilReadyTest(
            string allocationMethod)
        {
            var unsupportedGroups = $"""
                <ListOfGroups>
                  <Group>
                    <name>Unsupported</name>
                    <defaultMethod>{allocationMethod}</defaultMethod>
                    <ListOfAccts><String>ACC1</String></ListOfAccts>
                  </Group>
                </ListOfGroups>
                """;
            using var scenario = Scenario.SingleAccount();
            scenario.GroupsDocument = unsupportedGroups;
            scenario.EndingGroupsDocument = unsupportedGroups;
            var reports = new List<string>();
            using var state = scenario.CreateState(reportUnsupported: reports.Add);

            var unsupported = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));
            var unsupportedReason = unsupported.ErrorMessage;

            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Failed, unsupported.Status);
                Assert.AreEqual(unsupportedReason, state.UnsupportedConfigurationError);
                StringAssert.Contains(allocationMethod, unsupportedReason);
                StringAssert.Contains("Change the group's allocation method in TWS", unsupportedReason);
                CollectionAssert.AreEqual(new[] { unsupportedReason }, reports);
            });

            state.MarkDisconnected("simulated disconnect");
            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Stale, state.Snapshot.Status);
                Assert.AreEqual(unsupportedReason, state.UnsupportedConfigurationError);
            });
            state.MarkConnected();
            scenario.GroupsDocument = Scenario.EmptyGroupsXml;
            scenario.EndingGroupsDocument = Scenario.EmptyGroupsXml;

            using var refreshStarted = new ManualResetEventSlim();
            using var releaseRefresh = new ManualResetEventSlim();
            var requestManagedAccounts = scenario.Actions.RequestManagedAccounts;
            var requestPositions = scenario.Actions.RequestPositions;
            scenario.Actions.RequestManagedAccounts = authorize =>
            {
                refreshStarted.Set();
                if (!releaseRefresh.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("The ordinary refresh was not released.");
                }
                return requestManagedAccounts(authorize);
            };
            scenario.Actions.RequestPositions = (requestId, accountOrGroup, authorize) =>
                scenario.RunAuthorized(authorize, () =>
                    throw new InvalidOperationException(
                        "simulated ordinary refresh failure"));

            var ordinaryTask = RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));
            Assert.IsTrue(refreshStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.Multiple(() =>
            {
                Assert.AreEqual(
                    BrokerageAccountSnapshotStatus.Refreshing,
                    state.Snapshot.Status);
                Assert.AreEqual(unsupportedReason, state.UnsupportedConfigurationError);
            });
            releaseRefresh.Set();
            var ordinaryFailure = await ordinaryTask;

            Assert.Multiple(() =>
            {
                StringAssert.Contains(
                    "simulated ordinary refresh failure",
                    ordinaryFailure.ErrorMessage);
                Assert.AreEqual(unsupportedReason, state.UnsupportedConfigurationError);
            });

            scenario.Actions.RequestManagedAccounts = requestManagedAccounts;
            scenario.Actions.RequestPositions = requestPositions;
            var ready = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));

            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Ready, ready.Status);
                Assert.IsNull(state.UnsupportedConfigurationError);
                CollectionAssert.AreEqual(new[] { unsupportedReason }, reports);
            });
        }

        [Test]
        public async Task ReadySnapshotRequiresStableConfigurationTest()
        {
            using var scenario = new Scenario
            {
                EndingGroupsDocument = Scenario.EmptyGroupsXml
            };
            using var state = scenario.CreateState();

            var snapshot = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));

            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Failed, snapshot.Status);
                Assert.IsFalse(snapshot.IsReady);
                Assert.AreEqual(0, snapshot.Generation);
                Assert.AreEqual(2, scenario.GroupsRequestCount);
                StringAssert.Contains("changed while the account snapshot was collected",
                    snapshot.ErrorMessage);
            });
        }

        [Test]
        public async Task FractionalAndUnmappedPositionsArePreservedTest()
        {
            using var scenario = new Scenario();
            using var state = scenario.CreateState();

            var snapshot = await RunRefreshAsync(
                state,
                () => state.RequestRefresh(Array.Empty<string>()));
            var mapped = snapshot.Accounts["ACC1"].Positions.Single();
            var unmapped = snapshot.Accounts["ACC2"].UnmappedPositions.Single();

            Assert.Multiple(() =>
            {
                Assert.AreEqual(BrokerageAccountSnapshotStatus.Ready, snapshot.Status);
                Assert.AreEqual(1.25m, mapped.Quantity);
                Assert.AreEqual(100.5m, mapped.AveragePrice);
                Assert.AreEqual(2.75m, unmapped.Quantity);
                Assert.AreEqual(12.345m, unmapped.AveragePrice);
                Assert.AreEqual("UNMAPPED", unmapped.BrokerageSymbol);
                Assert.IsTrue(snapshot.HasUnmappedPositions);
            });
        }

        private static async Task<BrokerageAccountSnapshot> RunRefreshAsync(
            InteractiveBrokersFinancialAdvisorAccountState state,
            Func<bool> request)
        {
            Assert.IsTrue(request(), "The refresh request was not accepted.");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var snapshot = state.Snapshot;
                if (snapshot.Status is BrokerageAccountSnapshotStatus.Ready or
                    BrokerageAccountSnapshotStatus.Failed or
                    BrokerageAccountSnapshotStatus.Stale)
                {
                    return snapshot;
                }
                await Task.Delay(5);
            }
            throw new TimeoutException("The refresh did not publish a terminal snapshot.");
        }

        private static async Task<BrokerageAccountSnapshot> WaitForReadyGenerationAsync(
            InteractiveBrokersFinancialAdvisorAccountState state,
            long previousGeneration)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var snapshot = state.Snapshot;
                if (snapshot.Status == BrokerageAccountSnapshotStatus.Ready &&
                    snapshot.Generation > previousGeneration)
                {
                    return snapshot;
                }
                await Task.Delay(5);
            }
            throw new TimeoutException(
                "The reconnect refresh did not publish a newer Ready snapshot.");
        }

        private static long GetRequestVersion(
            InteractiveBrokersFinancialAdvisorAccountState state) =>
            (long)typeof(InteractiveBrokersFinancialAdvisorAccountState)
                .GetField("_requestVersion",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(state);

        private static bool IsPendingRequestWireSent(
            InteractiveBrokersFinancialAdvisorAccountState state)
        {
            var pending = typeof(InteractiveBrokersFinancialAdvisorAccountState)
                .GetField("_pendingRequest", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(state);
            return pending != null && (bool)pending.GetType()
                .GetProperty("WireSent", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(pending);
        }

        private static bool HasQueuedRefresh(
            InteractiveBrokersFinancialAdvisorAccountState state) =>
            typeof(InteractiveBrokersFinancialAdvisorAccountState)
                .GetField("_queuedRefresh",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(state) != null;

        private static void FillRefreshQueue(
            InteractiveBrokersFinancialAdvisorAccountState state)
        {
            var stateType = typeof(InteractiveBrokersFinancialAdvisorAccountState);
            var scopeType = stateType.GetNestedType(
                "SnapshotScope",
                BindingFlags.NonPublic);
            var workItemType = stateType.GetNestedType(
                "WorkItem",
                BindingFlags.NonPublic);
            var channel = stateType.GetField(
                    "_work",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(state);
            var writer = channel?.GetType().GetProperty("Writer")?.GetValue(channel);
            var tryWrite = writer?.GetType().GetMethod(
                "TryWrite",
                new[] { workItemType });

            Assert.Multiple(() =>
            {
                Assert.IsNotNull(scopeType);
                Assert.IsNotNull(workItemType);
                Assert.IsNotNull(writer);
                Assert.IsNotNull(tryWrite);
            });
            for (var index = 0; index < 8; index++)
            {
                var scope = Activator.CreateInstance(
                    scopeType,
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new object[]
                    {
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        false,
                        0L
                    },
                    null);
                var item = Activator.CreateInstance(
                    workItemType,
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[] { scope },
                    null);
                Assert.IsTrue(
                    (bool)tryWrite.Invoke(writer, new[] { item }),
                    $"Expected bounded queue slot {index + 1} to accept a dummy item.");
            }
        }

        private static (
            IReadOnlyCollection<string> Groups,
            IReadOnlyCollection<string> AdditionalAccounts,
            bool CompleteDiscovery) GetQueuedScope(
                InteractiveBrokersFinancialAdvisorAccountState state)
        {
            var queued = typeof(InteractiveBrokersFinancialAdvisorAccountState)
                .GetField("_queuedRefresh", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(state);
            Assert.IsNotNull(queued);
            var scope = queued.GetType()
                .GetField("Scope", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(queued);
            Assert.IsNotNull(scope);
            var scopeType = scope.GetType();
            return (
                (IReadOnlyCollection<string>)scopeType.GetProperty(
                    "GroupNames", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(scope),
                (IReadOnlyCollection<string>)scopeType.GetProperty(
                    "AdditionalAccountIds", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(scope),
                (bool)scopeType.GetProperty(
                    "CompleteDiscovery", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(scope));
        }

        private sealed class Scenario : IDisposable
        {
            internal const string EmptyGroupsXml = "<ListOfGroups />";
            internal const string GroupsXml = """
                <ListOfGroups>
                  <Group>
                    <name>Alpha</name>
                    <defaultMethod>NetLiq</defaultMethod>
                    <ListOfAccts><String>ACC1</String></ListOfAccts>
                  </Group>
                  <Group>
                    <name>Beta</name>
                    <defaultMethod>Equal</defaultMethod>
                    <ListOfAccts><String>ACC2</String></ListOfAccts>
                  </Group>
                </ListOfGroups>
                """;
            private const string AliasesXml = """
                <ListOfAccountAliases>
                  <AccountAlias><account>ACC1</account><alias>Alpha Client</alias></AccountAlias>
                  <AccountAlias><account>ACC2</account><alias>Beta Client</alias></AccountAlias>
                  <AccountAlias><account>ACC3</account><alias>Unassigned Client</alias></AccountAlias>
                </ListOfAccountAliases>
                """;

            internal InteractiveBrokersClient Client { get; }
            internal InteractiveBrokersFinancialAdvisorAccountState.RequestActions Actions { get; }
            internal List<string> Requests { get; } = new();
            internal List<int> KeyedRequestIds { get; } = new();
            internal List<int> AccountRequestIds { get; } = new();
            internal List<int> CanceledPositionIds { get; } = new();
            internal List<int> CanceledAccountIds { get; } = new();
            internal Action ExternalCallProbe { get; set; } = () => { };
            internal string ManagedAccounts { get; set; } = "MASTER,ACC1,ACC2,ACC3";
            internal string GroupsDocument { get; set; } = GroupsXml;
            internal string EndingGroupsDocument { get; set; } = GroupsXml;
            internal string AliasesDocument { get; set; } = AliasesXml;
            internal FamilyCode[] FamilyCodes { get; set; } =
            {
                new() { AccountID = "ACC1", FamilyCodeStr = "Family-A" },
                new() { AccountID = "ACC2", FamilyCodeStr = "Family-B" },
                new() { AccountID = "ACC3", FamilyCodeStr = "Family-C" }
            };
            internal int GroupsRequestCount { get; private set; }
            internal int MaximumConcurrentExternalCalls => _maximumConcurrentExternalCalls;
            private int _activeExternalCalls;
            private int _maximumConcurrentExternalCalls;

            internal Scenario()
            {
                Client = new InteractiveBrokersClient(new EReaderMonitorSignal());
                Actions =
                    new InteractiveBrokersFinancialAdvisorAccountState.RequestActions(Client)
                    {
                        RequestManagedAccounts = authorize =>
                            RunAuthorized(authorize, () =>
                            {
                                Requests.Add("managed");
                                Client.managedAccounts(ManagedAccounts);
                            }),
                        RequestFinancialAdvisor = (faDataType, authorize) =>
                            RunAuthorized(authorize, () =>
                            {
                                Requests.Add($"fa:{faDataType}");
                                var document = faDataType == 1
                                    ? ++GroupsRequestCount == 1
                                        ? GroupsDocument
                                        : EndingGroupsDocument
                                    : AliasesDocument;
                                Client.receiveFA(faDataType, document);
                            }),
                        RequestFamilyCodes = authorize =>
                            RunAuthorized(authorize, () =>
                            {
                                Requests.Add("family");
                                Client.familyCodes(FamilyCodes);
                            }),
                        RequestPositions = (requestId, accountOrGroup, authorize) =>
                            RunAuthorized(
                                authorize,
                                () => EmitPositions(requestId, accountOrGroup)),
                        CancelPositions = (requestId, authorize) =>
                            RunAuthorized(
                                authorize,
                                () => CanceledPositionIds.Add(requestId)),
                        RequestAccountUpdates = (requestId, accountId, authorize) =>
                            RunAuthorized(
                                authorize,
                                () => EmitAccountValues(requestId, accountId)),
                        CancelAccountUpdates = (requestId, authorize) =>
                            RunAuthorized(
                                authorize,
                                () => CanceledAccountIds.Add(requestId))
                    };
            }

            internal InteractiveBrokersFinancialAdvisorAccountState CreateState(
                string configuredGroup = "",
                TimeSpan? requestTimeout = null,
                Action paceRequest = null,
                Func<Contract, Symbol> mapSymbol = null,
                Action<string> reportUnsupported = null)
            {
                return new InteractiveBrokersFinancialAdvisorAccountState(
                    Client,
                    paceRequest ?? (() => { }),
                    () => true,
                    mapSymbol ?? (contract => contract.Symbol == "UNMAPPED"
                            ? throw new InvalidOperationException("No LEAN symbol mapping.")
                            : Symbol.Create(contract.Symbol, SecurityType.Equity, Market.USA)),
                    "MASTER",
                    configuredGroup,
                    requestTimeout ?? TimeSpan.FromSeconds(2),
                    reportUnsupported,
                    requestActions: Actions);
            }

            internal static Scenario SingleAccount() => new()
            {
                ManagedAccounts = "MASTER,ACC1",
                GroupsDocument = EmptyGroupsXml,
                EndingGroupsDocument = EmptyGroupsXml,
                AliasesDocument = "<ListOfAccountAliases />",
                FamilyCodes = Array.Empty<FamilyCode>()
            };

            internal bool RunAuthorized(Func<bool> authorize, Action action)
            {
                if (!authorize())
                {
                    return false;
                }
                RunExternal(action);
                return true;
            }

            private void RunExternal(Action action)
            {
                ExternalCallProbe();
                var active = Interlocked.Increment(ref _activeExternalCalls);
                int observed;
                while (active > (observed = _maximumConcurrentExternalCalls) &&
                    Interlocked.CompareExchange(
                        ref _maximumConcurrentExternalCalls, active, observed) != observed)
                {
                }
                try
                {
                    action();
                }
                finally
                {
                    Interlocked.Decrement(ref _activeExternalCalls);
                }
            }

            private void EmitPositions(int requestId, string accountOrGroup)
            {
                Requests.Add($"positions:{accountOrGroup}");
                KeyedRequestIds.Add(requestId);
                if (accountOrGroup == "Alpha")
                {
                    Client.positionMulti(
                        requestId,
                        "ACC1",
                        "Model-A",
                        MappedContract(),
                        1.25m,
                        100.5);
                }
                else if (accountOrGroup == "Beta")
                {
                    Client.positionMulti(
                        requestId,
                        "ACC2",
                        "Model-B",
                        UnmappedContract(),
                        2.75m,
                        12.345);
                }
                Client.positionMultiEnd(requestId);
            }

            internal void EmitAccountValues(int requestId, string accountId)
            {
                Requests.Add($"account:{accountId}");
                KeyedRequestIds.Add(requestId);
                AccountRequestIds.Add(requestId);
                Client.accountUpdateMulti(
                    requestId, accountId, string.Empty, "AccountReady", "true", string.Empty);
                Client.accountUpdateMulti(
                    requestId, accountId, string.Empty, "AccountType", "INDIVIDUAL", string.Empty);
                Client.accountUpdateMulti(
                    requestId, accountId, string.Empty, "NetLiquidation", "1000.25", "USD");
                Client.accountUpdateMulti(
                    requestId, accountId, string.Empty, "TotalCashValue", "250.50", "USD");
                Client.accountUpdateMulti(
                    requestId, accountId, string.Empty, "$LEDGER-USD:CashBalance", "250.50", string.Empty);
                Client.accountUpdateMultiEnd(requestId);
            }

            internal static Contract MappedContract() => new()
            {
                ConId = 101,
                Symbol = "SPY",
                LocalSymbol = "SPY",
                SecType = "STK",
                Currency = "USD",
                Exchange = "SMART",
                PrimaryExch = "ARCA"
            };

            private static Contract UnmappedContract() => new()
            {
                ConId = 202,
                Symbol = "UNMAPPED",
                LocalSymbol = "UNMAPPED",
                SecType = "STK",
                Currency = "USD",
                Exchange = "SMART",
                PrimaryExch = "ARCA"
            };

            public void Dispose()
            {
                Client.Dispose();
            }
        }
    }
}
