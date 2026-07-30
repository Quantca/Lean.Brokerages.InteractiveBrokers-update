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
using System.Reflection;
using System.Runtime.CompilerServices;
using IBApi;
using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.Backtesting;
using QuantConnect.Brokerages.InteractiveBrokers;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.TransactionHandlers;
using QuantConnect.Orders;
using QuantConnect.Tests.Brokerages;
using QuantConnect.Tests.Engine;
using QuantConnect.Tests.Engine.DataFeeds;
using QuantConnect.Util;
using FAState = QuantConnect.Brokerages.InteractiveBrokers.InteractiveBrokersFinancialAdvisorAccountState;
using IB = QuantConnect.Brokerages.InteractiveBrokers.Client;
using LeanOrder = QuantConnect.Orders.Order;

namespace QuantConnect.Tests.Brokerages.InteractiveBrokers
{
    /// <summary>
    /// Hermetic regression test for IB error 201 ("Invalid account number") when an FA group
    /// filter is active and an order also carries a per-order <see cref="InteractiveBrokersOrderProperties.Account"/>
    /// override. The fixture invokes <c>InteractiveBrokersBrokerage.ConvertOrder</c> via reflection
    /// (the assembly already exposes its internals to this test project) so the test can run in CI
    /// without a live IB Gateway / TWS connection.
    /// </summary>
    /// <remarks>
    /// Validated end-to-end against a live IB Financial Advisor master account: the same
    /// (filter set + per-order Account override) configuration that produced IB error 201 before
    /// the fix is accepted with <c>Status: Submitted</c> after the fix. A companion two-order
    /// scenario (one with an Account override, one without) confirmed both branches route as
    /// intended in TWS — the per-order override appears under the "IndBrokerage" allocation, the
    /// default order appears under the configured FA group's allocation method.
    /// </remarks>
    [TestFixture]
    public class InteractiveBrokersFaGroupOrderConversionTests
    {
        private const string FaMasterAccount = "F1234567";    // any account containing 'F' is a master account
        private const string FaGroupName     = "TestGroup1";
        private const string AgentDescription = "I";          // IB.AgentDescription.Individual

        private static readonly FieldInfo AccountField =
            typeof(InteractiveBrokersBrokerage).GetField("_account", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo AlgorithmField =
            typeof(InteractiveBrokersBrokerage).GetField("_algorithm", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo OrderProviderField =
            typeof(InteractiveBrokersBrokerage).GetField("_orderProvider", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SymbolMapperField =
            typeof(InteractiveBrokersBrokerage).GetField("_symbolMapper", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo AgentDescriptionField =
            typeof(InteractiveBrokersBrokerage).GetField("_agentDescription", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FaFilterField =
            typeof(InteractiveBrokersBrokerage).GetField("_financialAdvisorsGroupFilter", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo UnifiedGroupsField =
            typeof(InteractiveBrokersBrokerage).GetField("_financialAdvisorUnifiedGroupsEnabled", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo AccountStateField =
            typeof(InteractiveBrokersBrokerage).GetField("_financialAdvisorAccountState", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ClientField =
            typeof(InteractiveBrokersBrokerage).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PendingOrderResponseField =
            typeof(InteractiveBrokersBrokerage).GetField("_pendingOrderResponse", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RequestInformationField =
            typeof(InteractiveBrokersBrokerage).GetField("_requestInformation", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo GroupTradingBlockedField =
            typeof(InteractiveBrokersFinancialAdvisorAccountState).GetField(
                "_groupTradingBlocked",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo UnsupportedConfigurationErrorField =
            typeof(InteractiveBrokersFinancialAdvisorAccountState).GetField(
                "_unsupportedConfigurationError",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SnapshotField =
            typeof(InteractiveBrokersFinancialAdvisorAccountState).GetField(
                "_snapshot",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo ConvertOrderMethod =
            typeof(InteractiveBrokersBrokerage).GetMethod(
                "ConvertOrder",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(List<LeanOrder>), typeof(Contract), typeof(int) },
                modifiers: null);
        private static readonly MethodInfo ConvertOrdersMethod =
            typeof(InteractiveBrokersBrokerage).GetMethod(
                "ConvertOrders",
                BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// When the FA group filter is configured and the order carries a per-order
        /// <c>Account</c> override, the resulting <see cref="IBApi.Order"/> must have
        /// <c>Account</c> set and <c>FaGroup</c>/<c>FaMethod</c> cleared. The two are mutually
        /// exclusive on the wire; sending them together produces IB error 201
        /// ("Invalid account number").
        /// </summary>
        [Test]
        public void LimitOrder_WithAccountOverrideAndGroupFilter_AccountWinsAndFaGroupCleared()
        {
            // Arrange
            var brokerage = CreateOfflineBrokerage();
            FaFilterField.SetValue(brokerage, FaGroupName);

            const string overrideAccount = "TestSubAccount";
            var props = new InteractiveBrokersOrderProperties
            {
                Account = overrideAccount,
                FaGroup = string.Empty,
                FaProfile = string.Empty,
                FaMethod = string.Empty,
            };

            var leanOrder = new LimitOrder(Symbols.SPY, 10m, 100.00m,
                new DateTime(2026, 1, 1, 15, 0, 0, DateTimeKind.Utc),
                tag: "",
                properties: props);

            var ibContract = new Contract
            {
                Symbol = "SPY", SecType = IB.SecurityType.Stock, Exchange = "SMART", Currency = "USD"
            };

            // Act
            var ibOrder = (IBApi.Order)ConvertOrderMethod.Invoke(
                brokerage,
                new object[] { new List<LeanOrder> { leanOrder }, ibContract, 1 });

            // Assert
            Assert.AreEqual(overrideAccount, ibOrder.Account,
                "When orderProperties.Account is supplied, ibOrder.Account must reflect that override.");
            Assert.IsTrue(string.IsNullOrEmpty(ibOrder.FaGroup),
                "FaGroup must be cleared when an Account override is supplied (IB rejects Account+FaGroup together with error 201).");
            Assert.IsTrue(string.IsNullOrEmpty(ibOrder.FaMethod),
                "FaMethod must be cleared alongside FaGroup when an Account override is supplied.");
        }

        [Test]
        public void DirectAccountOrderPreservesExactQuantityForFractionalAndIntegerLotsTest()
        {
            var brokerage = CreateOfflineBrokerage();
            UnifiedGroupsField.SetValue(brokerage, true);
            var properties = new InteractiveBrokersOrderProperties
            {
                Account = "TestSubAccount"
            };
            var fractionalOrder = new LimitOrder(
                Symbol.Create("AUDUSD", SecurityType.Cfd, Market.InteractiveBrokers),
                -9925.25m,
                1m,
                new DateTime(2026, 1, 1, 15, 0, 0, DateTimeKind.Utc),
                properties: properties);
            var integerOrder = new LimitOrder(
                Symbols.SPY,
                -9m,
                100m,
                fractionalOrder.Time,
                properties: properties);

            Assert.AreEqual(9925.25m, ConvertOrder(brokerage, fractionalOrder).TotalQuantity);
            Assert.AreEqual(9m, ConvertOrder(brokerage, integerOrder).TotalQuantity);
        }

        [Test]
        public void CoherentComboLegRoutingIsAcceptedWithoutChangingWireRoutingTest()
        {
            var brokerage = CreateOfflineBrokerage();
            UnifiedGroupsField.SetValue(brokerage, true);
            FaFilterField.SetValue(brokerage, FaGroupName);
            var orders = CreateComboOrders(
                brokerage,
                new InteractiveBrokersOrderProperties(),
                new InteractiveBrokersOrderProperties
                {
                    FaGroup = FaGroupName
                });

            Assert.DoesNotThrow(() =>
                brokerage.ValidateFinancialAdvisorOrderAdmission(orders[0]));
            var converted = ConvertOrder(brokerage, orders[0]);
            Assert.AreEqual(FaGroupName, converted.FaGroup);
            Assert.AreEqual(string.Empty, converted.FaMethod);
        }

        [TestCase("NormalizedMethod")]
        [TestCase("NumericPercentage")]
        [TestCase("IgnoredDirectFields")]
        public void EquivalentComboLegRoutingFormsAreAcceptedTest(string equivalence)
        {
            var brokerage = CreateOfflineBrokerage();
            UnifiedGroupsField.SetValue(brokerage, true);
            var first = new InteractiveBrokersOrderProperties
            {
                FaGroup = equivalence == "IgnoredDirectFields" ? "FirstIgnoredGroup" : FaGroupName,
                FaMethod = equivalence == "NumericPercentage" ? "PctChange" : "EqualQuantity",
                FaPercentage = 12,
                Account = equivalence == "IgnoredDirectFields" ? "ManagedAccount" : string.Empty
            };
            var second = new InteractiveBrokersOrderProperties
            {
                FaGroup = equivalence == "IgnoredDirectFields" ? "SecondIgnoredGroup" : FaGroupName,
                FaMethod = equivalence == "NumericPercentage" ? "PctChange" : "Equal",
                ExactFaPercentage = 12m,
                Account = equivalence == "IgnoredDirectFields" ? "ManagedAccount" : string.Empty
            };
            var orders = CreateComboOrders(brokerage, first, second);

            Assert.DoesNotThrow(() =>
                brokerage.ValidateFinancialAdvisorOrderAdmission(orders[0]));
        }

        [TestCase("Account")]
        [TestCase("FaGroup")]
        [TestCase("FaMethod")]
        [TestCase("Percentage")]
        public void DivergentComboLegRoutingIsRejectedTest(string divergence)
        {
            var brokerage = CreateOfflineBrokerage();
            UnifiedGroupsField.SetValue(brokerage, true);
            var first = new InteractiveBrokersOrderProperties
            {
                FaGroup = FaGroupName,
                FaMethod = "PctChange",
                ExactFaPercentage = 12.5m
            };
            var second = new InteractiveBrokersOrderProperties
            {
                FaGroup = divergence == "FaGroup" ? "AnotherGroup" : FaGroupName,
                FaMethod = divergence == "FaMethod" ? "Equal" : "PctChange",
                ExactFaPercentage = divergence == "Percentage" ? 11m : 12.5m,
                Account = divergence == "Account" ? "ManagedAccount" : string.Empty
            };
            var orders = CreateComboOrders(brokerage, first, second);

            StringAssert.Contains(
                "All combo legs",
                Assert.Throws<InvalidOperationException>(() =>
                    brokerage.ValidateFinancialAdvisorOrderAdmission(orders[0])).Message);
            Assert.DoesNotThrow(() =>
                brokerage.ValidateFinancialAdvisorOrderAdmission(
                    orders[0],
                    isUpdate: true));
        }

        [Test]
        public void IncompleteComboLegGroupDefersCoherenceValidationTest()
        {
            var brokerage = CreateOfflineBrokerage();
            UnifiedGroupsField.SetValue(brokerage, true);
            var group = new GroupOrderManager(1, 2, 1m);
            var order = new ComboMarketOrder(
                Symbols.SPY,
                1m,
                DateTime.UtcNow,
                group,
                properties: new InteractiveBrokersOrderProperties
                {
                    FaGroup = FaGroupName
                });
            var provider = new OrderProvider();
            provider.Add(order);
            group.OrderIds.Add(order.Id);
            OrderProviderField.SetValue(brokerage, provider);

            Assert.DoesNotThrow(() =>
                brokerage.ValidateFinancialAdvisorOrderAdmission(order));
        }

        [Test]
        public void ConfigurationWriteGateBlocksOnlyGroupOrdersTest()
        {
            var brokerage = CreateOfflineBrokerage();
            UnifiedGroupsField.SetValue(brokerage, true);
            FaFilterField.SetValue(brokerage, FaGroupName);
            var state = (InteractiveBrokersFinancialAdvisorAccountState)
                RuntimeHelpers.GetUninitializedObject(typeof(InteractiveBrokersFinancialAdvisorAccountState));
            GroupTradingBlockedField.SetValue(state, true);
            AccountStateField.SetValue(brokerage, state);

            var directOrder = CreateOrder(new InteractiveBrokersOrderProperties
            {
                Account = "ManagedAccount",
                FaGroup = "AnotherGroup"
            });
            var groupOrder = CreateOrder(new InteractiveBrokersOrderProperties
            {
                FaGroup = FaGroupName
            });
            var implicitFilterOrder = CreateOrder(new InteractiveBrokersOrderProperties());

            Assert.DoesNotThrow(() => brokerage.ValidateFinancialAdvisorOrderAdmission(directOrder));
            Assert.DoesNotThrow(() => ConvertOrder(brokerage, directOrder));
            StringAssert.Contains(
                "FA configuration mutation is active",
                AssertAdmissionRejectsAndConversionConfigures(
                    brokerage,
                    groupOrder).Message);
            AssertAdmissionRejectsAndConversionConfigures(
                brokerage,
                implicitFilterOrder);
        }

        [Test]
        public void UnsupportedTopologyLatchBlocksOnlyGroupOrdersTest()
        {
            const string unsupportedReason =
                "Correct the unsupported FA topology in TWS and refresh.";
            var brokerage = CreateOfflineBrokerage();
            UnifiedGroupsField.SetValue(brokerage, true);
            FaFilterField.SetValue(brokerage, FaGroupName);
            var state = (InteractiveBrokersFinancialAdvisorAccountState)
                RuntimeHelpers.GetUninitializedObject(
                    typeof(InteractiveBrokersFinancialAdvisorAccountState));
            UnsupportedConfigurationErrorField.SetValue(state, unsupportedReason);
            SnapshotField.SetValue(
                state,
                CreateSnapshot(BrokerageAccountSnapshotStatus.Stale));
            AccountStateField.SetValue(brokerage, state);

            var directOrder = CreateOrder(new InteractiveBrokersOrderProperties
            {
                Account = "ManagedAccount",
                FaGroup = "AnotherGroup"
            });
            var groupOrder = CreateOrder(new InteractiveBrokersOrderProperties
            {
                FaGroup = FaGroupName
            });
            var implicitFilterOrder = CreateOrder(
                new InteractiveBrokersOrderProperties());

            Assert.DoesNotThrow(() =>
                brokerage.ValidateFinancialAdvisorOrderAdmission(directOrder));
            Assert.DoesNotThrow(() => ConvertOrder(brokerage, directOrder));
            Assert.AreEqual(
                unsupportedReason,
                AssertAdmissionRejectsAndConversionConfigures(
                    brokerage,
                    groupOrder).Message);
            Assert.AreEqual(
                unsupportedReason,
                AssertAdmissionRejectsAndConversionConfigures(
                    brokerage,
                    implicitFilterOrder).Message);

            UnsupportedConfigurationErrorField.SetValue(state, null);
            Assert.DoesNotThrow(() =>
                brokerage.ValidateFinancialAdvisorOrderAdmission(groupOrder));
            Assert.DoesNotThrow(() => ConvertOrder(brokerage, groupOrder));
            Assert.DoesNotThrow(() =>
                brokerage.ValidateFinancialAdvisorOrderAdmission(implicitFilterOrder));
            Assert.DoesNotThrow(() => ConvertOrder(brokerage, implicitFilterOrder));
        }

        [Test]
        public void StateChangeAfterAdmissionCannotThrowDuringConversionTest()
        {
            var brokerage = CreateOfflineBrokerage();
            UnifiedGroupsField.SetValue(brokerage, true);
            FaFilterField.SetValue(brokerage, FaGroupName);
            var state = (InteractiveBrokersFinancialAdvisorAccountState)
                RuntimeHelpers.GetUninitializedObject(
                    typeof(InteractiveBrokersFinancialAdvisorAccountState));
            AccountStateField.SetValue(brokerage, state);
            var order = CreateOrder(new InteractiveBrokersOrderProperties
            {
                FaGroup = FaGroupName
            });

            Assert.DoesNotThrow(() =>
                brokerage.ValidateFinancialAdvisorOrderAdmission(order));
            UnsupportedConfigurationErrorField.SetValue(
                state, "Topology changed after admission.");
            GroupTradingBlockedField.SetValue(state, true);

            Assert.DoesNotThrow(() => ConvertOrder(brokerage, order));
        }

        [Test]
        public void UpdateAdmissionRejectsOnlyStateIndependentRoutingErrorsTest()
        {
            var brokerage = CreateOfflineBrokerage();
            UnifiedGroupsField.SetValue(brokerage, true);
            FaFilterField.SetValue(brokerage, FaGroupName);
            var state = (InteractiveBrokersFinancialAdvisorAccountState)
                RuntimeHelpers.GetUninitializedObject(
                    typeof(InteractiveBrokersFinancialAdvisorAccountState));
            UnsupportedConfigurationErrorField.SetValue(
                state, "Unsupported current topology.");
            GroupTradingBlockedField.SetValue(state, true);
            SnapshotField.SetValue(
                state,
                CreateSnapshot(
                    BrokerageAccountSnapshotStatus.Ready,
                    new BrokerageAccountGroup(
                        FaGroupName, "NetLiq", new[] { "ManagedAccount" })));
            AccountStateField.SetValue(brokerage, state);

            Assert.DoesNotThrow(() =>
                brokerage.ValidateFinancialAdvisorOrderAdmission(
                    CreateOrder(new InteractiveBrokersOrderProperties
                    {
                        FaGroup = FaGroupName,
                        FaMethod = "Equal"
                    }),
                    isUpdate: true));
            Assert.IsInstanceOf<NotSupportedException>(
                Assert.Catch(() =>
                    brokerage.ValidateFinancialAdvisorOrderAdmission(
                        CreateOrder(new InteractiveBrokersOrderProperties
                        {
                            FaProfile = "LegacyProfile"
                        }),
                        isUpdate: true)));
            StringAssert.Contains(
                "does not match the configured",
                Assert.Catch(() =>
                    brokerage.ValidateFinancialAdvisorOrderAdmission(
                        CreateOrder(new InteractiveBrokersOrderProperties
                        {
                            FaGroup = "OutsideGroup"
                        }),
                        isUpdate: true)).Message);
        }

        [Test]
        public void AllocationMethodValidationFailsOpenWithoutAuthorityTest()
        {
            var savedGroup = new BrokerageAccountGroup(
                FaGroupName,
                "NetLiq",
                new[] { "ManagedAccount" });
            var conflictingOrder = new IBApi.Order
            {
                FaGroup = FaGroupName,
                FaMethod = "Equal",
                TotalQuantity = 1m
            };

            Assert.DoesNotThrow(() =>
                InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                    conflictingOrder,
                    null));
            foreach (var status in Enum.GetValues<BrokerageAccountSnapshotStatus>()
                .Where(status => status != BrokerageAccountSnapshotStatus.Ready))
            {
                Assert.DoesNotThrow(() =>
                    InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                        conflictingOrder,
                        CreateSnapshot(status, savedGroup)));
            }
            Assert.DoesNotThrow(() =>
                InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                    conflictingOrder,
                    CreateSnapshot(
                        BrokerageAccountSnapshotStatus.Ready,
                        new BrokerageAccountGroup("OtherGroup", "Equal", new[] { "ManagedAccount" }))));

            conflictingOrder.FaGroup = FaGroupName.ToLowerInvariant();
            StringAssert.Contains(
                "cannot execute an order",
                Assert.Throws<InvalidOperationException>(() =>
                    InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                        conflictingOrder,
                        CreateSnapshot(BrokerageAccountSnapshotStatus.Ready, savedGroup))).Message);
        }

        [TestCase(BrokerageAccountRelationship.Primary)]
        [TestCase(BrokerageAccountRelationship.Aggregate)]
        [TestCase(BrokerageAccountRelationship.Unknown)]
        [TestCase(null)]
        public void RelationshipInvalidTargetGroupIsRejectedWithReadyAuthorityTest(
            BrokerageAccountRelationship? relationship)
        {
            var group = new BrokerageAccountGroup(
                FaGroupName,
                "Equal",
                new[] { "Account" });
            var directory = relationship.HasValue
                ? new Dictionary<string, BrokerageAccountDirectoryEntry>
                {
                    ["Account"] = new BrokerageAccountDirectoryEntry(
                        "Account",
                        relationship.Value,
                        new[] { FaGroupName })
                }
                : new Dictionary<string, BrokerageAccountDirectoryEntry>();
            var order = new IBApi.Order
            {
                FaGroup = FaGroupName,
                TotalQuantity = 1m
            };

            StringAssert.Contains(
                "not classified as managed",
                Assert.Throws<InvalidOperationException>(() =>
                    InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                        order,
                        CreateSnapshotWithDirectory(
                            BrokerageAccountSnapshotStatus.Ready,
                            directory,
                            group))).Message);
        }

        [Test]
        public void RelationshipValidationExaminesOnlyTargetedGroupTest()
        {
            var target = new BrokerageAccountGroup(
                FaGroupName,
                "Equal",
                new[] { "ManagedAccount" });
            var unsafeGroup = new BrokerageAccountGroup(
                "UnsafeGroup",
                "Equal",
                new[] { "PrimaryAccount" });
            var directory = new Dictionary<string, BrokerageAccountDirectoryEntry>
            {
                ["ManagedAccount"] = new BrokerageAccountDirectoryEntry(
                    "ManagedAccount",
                    BrokerageAccountRelationship.Managed,
                    new[] { FaGroupName }),
                ["PrimaryAccount"] = new BrokerageAccountDirectoryEntry(
                    "PrimaryAccount",
                    BrokerageAccountRelationship.Primary,
                    new[] { "UnsafeGroup" })
            };

            Assert.DoesNotThrow(() =>
                InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                    new IBApi.Order
                    {
                        FaGroup = FaGroupName,
                        TotalQuantity = 1m
                    },
                    CreateSnapshotWithDirectory(
                        BrokerageAccountSnapshotStatus.Ready,
                        directory,
                        target,
                        unsafeGroup)));
        }

        [Test]
        public void UnknownSavedAllocationMethodIsRejectedOnlyWithReadyAuthorityTest()
        {
            var group = new BrokerageAccountGroup(
                FaGroupName,
                "FutureMethod",
                new[] { "ManagedAccount" });
            var order = new IBApi.Order
            {
                FaGroup = FaGroupName,
                FaMethod = "PctChange",
                FaPercentage = "1"
            };

            foreach (var status in Enum.GetValues<BrokerageAccountSnapshotStatus>()
                .Where(status => status != BrokerageAccountSnapshotStatus.Ready))
            {
                Assert.DoesNotThrow(() =>
                    InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                        order,
                        CreateSnapshot(status, group)));
            }

            var exception = Assert.Throws<NotSupportedException>(() =>
                InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                    order,
                    CreateSnapshot(BrokerageAccountSnapshotStatus.Ready, group)));
            Assert.Multiple(() =>
            {
                StringAssert.Contains("FutureMethod", exception.Message);
                StringAssert.Contains("Supported saved allocation methods", exception.Message);
            });
        }

        [Test]
        public void AdmissionValidationFailureLeavesBrokerageOrderStateUntouchedTest()
        {
            var brokerage = CreateOfflineBrokerage();
            UnifiedGroupsField.SetValue(brokerage, true);
            var savedGroup = new BrokerageAccountGroup(
                FaGroupName,
                "NetLiq",
                new[] { "ManagedAccount" });
            var state = (InteractiveBrokersFinancialAdvisorAccountState)
                RuntimeHelpers.GetUninitializedObject(typeof(InteractiveBrokersFinancialAdvisorAccountState));
            SnapshotField.SetValue(
                state,
                CreateSnapshot(BrokerageAccountSnapshotStatus.Ready, savedGroup));
            AccountStateField.SetValue(brokerage, state);
            var order = CreateOrder(new InteractiveBrokersOrderProperties
            {
                FaGroup = FaGroupName,
                FaMethod = "Equal"
            });
            var client = new IB.InteractiveBrokersClient(new EReaderMonitorSignal());
            var connectedField = FindSocketConnectedField(client.ClientSocket);
            ClientField.SetValue(brokerage, client);
            connectedField.SetValue(client.ClientSocket, true);

            try
            {
                Assert.IsFalse(brokerage.PlaceOrder(order));
                Assert.IsEmpty(order.BrokerId);
                Assert.AreEqual(0, GetCollectionCount(RequestInformationField.GetValue(brokerage)));
                Assert.AreEqual(0, GetCollectionCount(PendingOrderResponseField.GetValue(brokerage)));
            }
            finally
            {
                connectedField.SetValue(client.ClientSocket, false);
                client.Dispose();
                ClientField.SetValue(brokerage, null);
            }
        }

        [Test]
        public void DirectAndGroupRoutingRemainExclusiveTest()
        {
            var brokerage = CreateOfflineBrokerage();
            UnifiedGroupsField.SetValue(brokerage, true);
            FaFilterField.SetValue(brokerage, FaGroupName);
            var directOrder = CreateOrder(new InteractiveBrokersOrderProperties
            {
                Account = "ManagedAccount",
                FaGroup = "AnotherGroup",
                FaMethod = "Equal"
            });
            var outsideGroupOrder = CreateOrder(new InteractiveBrokersOrderProperties
            {
                FaGroup = "AnotherGroup"
            });
            var matchingGroupOrder = CreateOrder(new InteractiveBrokersOrderProperties
            {
                FaGroup = $"  {FaGroupName}  "
            });
            var implicitFilterOrder = CreateOrder(new InteractiveBrokersOrderProperties());
            var profileOrder = CreateOrder(new InteractiveBrokersOrderProperties
            {
                FaProfile = "LegacyProfile"
            });

            Assert.DoesNotThrow(() => brokerage.ValidateFinancialAdvisorOrderAdmission(directOrder));
            var ibOrder = ConvertOrder(brokerage, directOrder);
            Assert.AreEqual("ManagedAccount", ibOrder.Account);
            Assert.IsEmpty(ibOrder.FaGroup);
            Assert.IsEmpty(ibOrder.FaMethod);

            StringAssert.Contains(
                "does not match the configured",
                AssertAdmissionRejectsAndConversionConfigures(
                    brokerage,
                    outsideGroupOrder).Message);
            Assert.IsInstanceOf<NotSupportedException>(
                AssertAdmissionRejectsAndConversionConfigures(
                    brokerage,
                    profileOrder));
            Assert.DoesNotThrow(() => brokerage.ValidateFinancialAdvisorOrderAdmission(matchingGroupOrder));
            Assert.AreEqual(FaGroupName, ConvertOrder(brokerage, matchingGroupOrder).FaGroup);
            Assert.AreEqual(FaGroupName, ConvertOrder(brokerage, implicitFilterOrder).FaGroup);
        }

        [Test]
        public void UnifiedPctChangeUsesExactPercentagePrecedenceTest()
        {
            var brokerage = CreateOfflineBrokerage();
            UnifiedGroupsField.SetValue(brokerage, true);
            var ibOrder = ConvertOrder(
                brokerage,
                CreateOrder(new InteractiveBrokersOrderProperties
                {
                    FaGroup = FaGroupName,
                    FaMethod = " pctchange ",
                    FaPercentage = 25,
                    ExactFaPercentage = 12.5m
                }));

            Assert.AreEqual("PctChange", ibOrder.FaMethod);
            Assert.AreEqual("12.5", ibOrder.FaPercentage);
            Assert.AreEqual(0m, ibOrder.TotalQuantity);
        }

        [TestCase(12.5, 7.5, 20, true)]
        [TestCase(9.5, 10.25, 19.75, false)]
        public void ContractsOrSharesLotAlignmentFlowsThroughBrokerageTransactionHandlerTest(
            decimal firstAllocation,
            decimal secondAllocation,
            decimal requestedQuantity,
            bool expectedSuccess)
        {
            var algorithm = new AlgorithmStub();
            algorithm.SetCash(100000m);
            var security = algorithm.AddEquity("SPY");
            security.SetMarketPrice(new Tick(
                DateTime.UtcNow,
                security.Symbol,
                100m,
                100m,
                100m));
            algorithm.SetFinishedWarmingUp();

            var brokerage = CreateOfflineBrokerage(algorithm);
            UnifiedGroupsField.SetValue(brokerage, true);
            var savedGroup = new BrokerageAccountGroup(
                FaGroupName,
                "ContractsOrShares",
                new[] { "A", "B" },
                new Dictionary<string, decimal>
                {
                    ["A"] = firstAllocation,
                    ["B"] = secondAllocation
                });
            var state = (InteractiveBrokersFinancialAdvisorAccountState)
                RuntimeHelpers.GetUninitializedObject(typeof(InteractiveBrokersFinancialAdvisorAccountState));
            SnapshotField.SetValue(
                state,
                CreateSnapshot(BrokerageAccountSnapshotStatus.Ready, savedGroup));
            AccountStateField.SetValue(brokerage, state);
            using var orderBrokerage =
                new FinancialAdvisorOrderBrokerage(algorithm, brokerage);
            var transactionHandler =
                new ImmediateBrokerageTransactionHandler();
            transactionHandler.Initialize(
                algorithm,
                orderBrokerage,
                new TestResultHandler());
            algorithm.Transactions.SetOrderProcessor(transactionHandler);

            try
            {
                var request = new SubmitOrderRequest(
                    OrderType.Limit,
                    security.Type,
                    security.Symbol,
                    requestedQuantity,
                    0m,
                    100m,
                    DateTime.UtcNow,
                    string.Empty,
                    new InteractiveBrokersOrderProperties
                    {
                        FaGroup = FaGroupName
                    },
                    asynchronous: true);
                var ticket = algorithm.Transactions.AddOrder(request);

                Assert.AreEqual(expectedSuccess, request.Response.IsSuccess);
                Assert.AreEqual(
                    decimal.Truncate(requestedQuantity),
                    orderBrokerage.ReceivedQuantity);
                if (expectedSuccess)
                {
                    Assert.Multiple(() =>
                    {
                        Assert.AreEqual(1, orderBrokerage.WireReadyCount);
                        Assert.AreEqual(20m, orderBrokerage.ConvertedOrder.TotalQuantity);
                        Assert.AreEqual(FaGroupName, orderBrokerage.ConvertedOrder.FaGroup);
                        Assert.IsEmpty(orderBrokerage.ConvertedOrder.FaMethod);
                        Assert.AreEqual(12.5m, savedGroup.AccountAllocationValues["A"]);
                        Assert.AreEqual(7.5m, savedGroup.AccountAllocationValues["B"]);
                    });
                    return;
                }

                Assert.Multiple(() =>
                {
                    Assert.AreEqual(0, orderBrokerage.WireReadyCount);
                    Assert.IsNull(orderBrokerage.ConvertedOrder);
                    Assert.AreEqual(OrderStatus.Invalid, ticket.Status);
                    Assert.IsInstanceOf<InvalidOperationException>(
                        orderBrokerage.AdmissionException);
                    StringAssert.Contains(
                        "ContractsOrShares group 'TestGroup1' has a saved allocation total of 19.75",
                        orderBrokerage.AdmissionException.Message);
                    StringAssert.Contains(
                        "SPY (lot size 1)",
                        orderBrokerage.AdmissionException.Message);
                    StringAssert.Contains(
                        "Adjust the saved vector so its total is a whole multiple of the lot size.",
                        orderBrokerage.AdmissionException.Message);
                });
            }
            finally
            {
                transactionHandler.Exit();
            }
        }

        [Test]
        public void StartupAndReconnectRecoveryPreserveFinancialAdvisorIdentityTest()
        {
            var algorithm = new AlgorithmStub();
            algorithm.AddEquity("SPY");
            var brokerage = CreateOfflineBrokerage(algorithm);
            UnifiedGroupsField.SetValue(brokerage, true);
            using var client = new IB.InteractiveBrokersClient(new EReaderMonitorSignal());
            ClientField.SetValue(brokerage, client);
            var contract = new Contract
            {
                Symbol = "SPY",
                SecType = IB.SecurityType.Stock,
                Exchange = "SMART",
                Currency = "USD"
            };
            var orderState = new OrderState { Status = "Submitted" };
            IBApi.Order groupIbOrder = null;
            IBApi.Order directIbOrder = null;
            var requestCount = 0;
            client.Error += (_, _) =>
            {
                requestCount++;
                if (requestCount % 2 == 0)
                {
                    client.openOrder(
                        groupIbOrder.OrderId, contract, groupIbOrder, orderState);
                    client.openOrder(
                        directIbOrder.OrderId, contract, directIbOrder, orderState);
                }
                client.openOrderEnd();
            };

            try
            {
                foreach (var recoveryPass in new[] { "startup", "reconnect" })
                {
                    groupIbOrder = new IBApi.Order
                    {
                        Account = FaMasterAccount,
                        FaGroup = FaGroupName,
                        FaMethod = "NetLiq",
                        FaPercentage = "12.5",
                        TotalQuantity = 19.75m,
                        Action = "BUY",
                        OrderType = "LMT",
                        LmtPrice = 100d,
                        Tif = IB.TimeInForce.GoodTillCancel,
                        OutsideRth = true,
                        OrderId = recoveryPass == "startup" ? 10 : 20
                    };
                    directIbOrder = new IBApi.Order
                    {
                        Account = "DU1234567",
                        TotalQuantity = 0.5m,
                        Action = "BUY",
                        OrderType = "LMT",
                        LmtPrice = 100d,
                        Tif = IB.TimeInForce.Day,
                        OrderId = recoveryPass == "startup" ? 11 : 21
                    };

                    var recoveredOrders = brokerage.GetOpenOrders();
                    var groupOrder = recoveredOrders.Single(order =>
                        ((InteractiveBrokersOrderProperties)order.Properties).FaGroup ==
                        FaGroupName);
                    var directOrder = recoveredOrders.Single(order =>
                        ((InteractiveBrokersOrderProperties)order.Properties).Account ==
                        "DU1234567");

                    var groupProperties =
                        (InteractiveBrokersOrderProperties)groupOrder.Properties;
                    var directProperties =
                        (InteractiveBrokersOrderProperties)directOrder.Properties;
                    var orderProvider = new OrderProvider(recoveredOrders);
                    var mutationBlockingOrders = orderProvider.GetOpenOrders(order =>
                        FAState.IsFinancialAdvisorGroupOrder(order, string.Empty));

                    Assert.Multiple(() =>
                    {
                        Assert.AreEqual(2, recoveredOrders.Count, recoveryPass);
                        Assert.AreEqual(19.75m, groupOrder.Quantity, recoveryPass);
                        Assert.AreEqual(FaGroupName, groupProperties.FaGroup, recoveryPass);
                        Assert.IsEmpty(groupProperties.Account, recoveryPass);
                        Assert.AreEqual("NetLiq", groupProperties.FaMethod, recoveryPass);
                        Assert.AreEqual(12.5m, groupProperties.ExactFaPercentage, recoveryPass);
                        Assert.AreEqual(TimeInForce.GoodTilCanceled.GetType(),
                            groupProperties.TimeInForce.GetType(), recoveryPass);
                        Assert.IsTrue(
                            groupProperties.OutsideRegularTradingHours, recoveryPass);
                        Assert.AreEqual(0.5m, directOrder.Quantity, recoveryPass);
                        Assert.AreEqual(
                            "DU1234567", directProperties.Account, recoveryPass);
                        Assert.IsEmpty(directProperties.FaGroup, recoveryPass);
                        Assert.AreEqual(1, mutationBlockingOrders.Count, recoveryPass);
                        Assert.AreEqual(
                            groupOrder.BrokerId.Single(),
                            mutationBlockingOrders[0].BrokerId.Single(),
                            recoveryPass);
                    });
                }
                Assert.AreEqual(4, requestCount);
            }
            finally
            {
                ClientField.SetValue(brokerage, null);
            }
        }

        [TestCase(false, FaMasterAccount)]
        [TestCase(true, "DU7654321")]
        public void OrdinaryRecoveryKeepsUpstreamQuantityAndPropertiesTest(
            bool unifiedGroupsEnabled,
            string account)
        {
            var algorithm = new AlgorithmStub();
            algorithm.AddEquity("SPY");
            var brokerage = CreateOfflineBrokerage(algorithm);
            UnifiedGroupsField.SetValue(brokerage, unifiedGroupsEnabled);
            AccountField.SetValue(brokerage, account);

            var recovered = RecoverOrder(brokerage, new IBApi.Order
            {
                Account = "DU1234567",
                FaGroup = FaGroupName,
                TotalQuantity = 19.75m,
                Action = "BUY",
                OrderType = "LMT",
                LmtPrice = 100d,
                Tif = IB.TimeInForce.Day,
                OrderId = 30
            });

            Assert.Multiple(() =>
            {
                Assert.AreEqual(20m, recovered.Quantity);
                Assert.AreEqual(typeof(OrderProperties), recovered.Properties.GetType());
            });
        }

        [Test]
        public void ReadySnapshotWithInconsistentSavedAllocationFailsClosedTest()
        {
            var brokerage = CreateOfflineBrokerage();
            UnifiedGroupsField.SetValue(brokerage, true);
            var savedGroup = new BrokerageAccountGroup(
                FaGroupName,
                "Ratio",
                new[] { "A", "B" },
                new Dictionary<string, decimal>
                {
                    ["A"] = 1m
                });
            var state = (InteractiveBrokersFinancialAdvisorAccountState)
                RuntimeHelpers.GetUninitializedObject(
                    typeof(InteractiveBrokersFinancialAdvisorAccountState));
            SnapshotField.SetValue(
                state,
                CreateSnapshot(
                    BrokerageAccountSnapshotStatus.Ready,
                    savedGroup));
            AccountStateField.SetValue(brokerage, state);
            var order = CreateOrder(new InteractiveBrokersOrderProperties
            {
                FaGroup = FaGroupName
            });

            StringAssert.Contains(
                "allocation keys must exactly match",
                AssertAdmissionRejectsAndConversionConfigures(
                    brokerage,
                    order).Message);
        }

        [Test]
        public void SavedUserSpecifiedAndComputedMethodsAreValidated()
        {
            var ratioGroup = new BrokerageAccountGroup(
                "RatioGroup",
                "Ratio",
                new[] { "A", "B" },
                new Dictionary<string, decimal> { ["A"] = 1m, ["B"] = 2m });
            var ratioOrder = new IBApi.Order
            {
                FaGroup = ratioGroup.Name,
                TotalQuantity = 3m
            };
            var ratioSnapshot = CreateSnapshot(BrokerageAccountSnapshotStatus.Ready, ratioGroup);
            Assert.DoesNotThrow(() =>
                InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                    ratioOrder,
                    ratioSnapshot));
            ratioOrder.FaMethod = "Ratio";
            StringAssert.Contains(
                "leave FaMethod empty",
                Assert.Throws<InvalidOperationException>(() =>
                    InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                        ratioOrder,
                        ratioSnapshot)).Message);

            var fixedGroup = new BrokerageAccountGroup(
                "FixedGroup",
                "ContractsOrShares",
                new[] { "A", "B" },
                new Dictionary<string, decimal> { ["A"] = 0.4m, ["B"] = 0.6m });
            var fixedOrder = new IBApi.Order
            {
                FaGroup = fixedGroup.Name,
                TotalQuantity = 1m
            };
            var fixedSnapshot = CreateSnapshot(BrokerageAccountSnapshotStatus.Ready, fixedGroup);
            Assert.DoesNotThrow(() =>
                InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                    fixedOrder,
                    fixedSnapshot));
            fixedOrder.TotalQuantity = 2m;
            StringAssert.Contains(
                "saved allocation total",
                Assert.Throws<InvalidOperationException>(() =>
                    InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                        fixedOrder,
                        fixedSnapshot)).Message);

            var equalGroup = new BrokerageAccountGroup(
                "EqualGroup",
                "EqualQuantity",
                new[] { "A" });
            var computedOrder = new IBApi.Order
            {
                FaGroup = equalGroup.Name,
                FaMethod = "Equal",
                TotalQuantity = 1m
            };
            var computedSnapshot = CreateSnapshot(BrokerageAccountSnapshotStatus.Ready, equalGroup);
            Assert.DoesNotThrow(() =>
                InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                    computedOrder,
                    computedSnapshot));
            computedOrder.FaMethod = "NetLiq";
            StringAssert.Contains(
                "cannot execute an order",
                Assert.Throws<InvalidOperationException>(() =>
                    InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                        computedOrder,
                    computedSnapshot)).Message);
        }

        [TestCaseSource(nameof(SavedAndRequestedAllocationMethods))]
        public void SavedAndRequestedAllocationMethodMatrixIsEnforced(
            string savedMethod,
            string requestedMethod,
            bool expectedAllowed)
        {
            var allocations = savedMethod switch
            {
                "ContractsOrShares" => new Dictionary<string, decimal>
                {
                    ["A"] = 0.4m,
                    ["B"] = 0.6m
                },
                "Ratio" => new Dictionary<string, decimal>
                {
                    ["A"] = 1m,
                    ["B"] = 2m
                },
                "Percent" => new Dictionary<string, decimal>
                {
                    ["A"] = 40m,
                    ["B"] = 60m
                },
                _ => null
            };
            var group = new BrokerageAccountGroup(
                FaGroupName,
                savedMethod,
                new[] { "A", "B" },
                allocations);
            var order = new IBApi.Order
            {
                FaGroup = FaGroupName,
                FaMethod = requestedMethod,
                FaPercentage = "25",
                TotalQuantity = 1m
            };
            var snapshot = CreateSnapshot(
                BrokerageAccountSnapshotStatus.Ready,
                group);

            if (savedMethod == "PctChange")
            {
                StringAssert.Contains(
                    "unsupported saved allocation method 'PctChange'",
                    Assert.Throws<NotSupportedException>(() =>
                        InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                            order,
                            snapshot)).Message);
            }
            else if (expectedAllowed)
            {
                Assert.DoesNotThrow(() =>
                    InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                        order,
                        snapshot));
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() =>
                    InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                        order,
                        snapshot));
            }
        }

        [Test]
        public void PctChangeAndMonetaryAmountValidationIsActionable()
        {
            var netLiq = new BrokerageAccountGroup(
                FaGroupName,
                "NetLiq",
                new[] { "ManagedAccount" });
            var pctChangeOrder = new IBApi.Order
            {
                FaGroup = FaGroupName,
                FaMethod = "PctChange",
                FaPercentage = "invalid"
            };
            var snapshot = CreateSnapshot(BrokerageAccountSnapshotStatus.Ready, netLiq);

            StringAssert.Contains(
                "valid FaPercentage",
                Assert.Throws<InvalidOperationException>(() =>
                    InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                        pctChangeOrder,
                        snapshot)).Message);
            pctChangeOrder.FaPercentage = "-100.5";
            Assert.DoesNotThrow(() =>
                InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                    pctChangeOrder,
                    snapshot));

            var monetary = new BrokerageAccountGroup(
                "Monetary",
                "MonetaryAmount",
                new[] { "ManagedAccount" },
                new Dictionary<string, decimal> { ["ManagedAccount"] = 100m });
            pctChangeOrder.FaGroup = monetary.Name;
            StringAssert.Contains(
                "unsupported saved allocation method 'MonetaryAmount'",
                Assert.Throws<NotSupportedException>(() =>
                    InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                        pctChangeOrder,
                        CreateSnapshot(BrokerageAccountSnapshotStatus.Ready, monetary))).Message);

            var savedPctChange = new BrokerageAccountGroup(
                "SavedPctChange",
                "PctChange",
                new[] { "ManagedAccount" });
            pctChangeOrder.FaGroup = savedPctChange.Name;
            pctChangeOrder.FaMethod = string.Empty;
            StringAssert.Contains(
                "unsupported saved allocation method 'PctChange'",
                Assert.Throws<NotSupportedException>(() =>
                    InteractiveBrokersBrokerage.ValidateFinancialAdvisorAllocationMethod(
                        pctChangeOrder,
                        CreateSnapshot(BrokerageAccountSnapshotStatus.Ready, savedPctChange))).Message);
        }

        private static IEnumerable<TestCaseData> SavedAndRequestedAllocationMethods()
        {
            var savedMethods = new[]
            {
                "ContractsOrShares",
                "Ratio",
                "Percent",
                "NetLiq",
                "AvailableEquity",
                "Equal",
                "PctChange"
            };
            var requestedMethods = new[]
            {
                string.Empty,
                "ContractsOrShares",
                "Ratio",
                "Percent",
                "NetLiq",
                "AvailableEquity",
                "Equal",
                "PctChange"
            };

            foreach (var savedMethod in savedMethods)
            {
                foreach (var requestedMethod in requestedMethods)
                {
                    var expectedAllowed = savedMethod != "PctChange" &&
                        (requestedMethod.Length == 0 ||
                            (savedMethod is "NetLiq" or "AvailableEquity" or "Equal") &&
                            (requestedMethod == savedMethod || requestedMethod == "PctChange"));
                    yield return new TestCaseData(
                            savedMethod,
                            requestedMethod,
                            expectedAllowed)
                        .SetName(
                            $"Saved_{savedMethod}_Requested_{(requestedMethod.Length == 0 ? "Blank" : requestedMethod)}");
                }
            }
        }

        private static LimitOrder CreateOrder(InteractiveBrokersOrderProperties properties)
        {
            return new LimitOrder(
                Symbols.SPY,
                10m,
                100m,
                new DateTime(2026, 1, 1, 15, 0, 0, DateTimeKind.Utc),
                properties: properties);
        }

        private static List<ComboMarketOrder> CreateComboOrders(
            InteractiveBrokersBrokerage brokerage,
            InteractiveBrokersOrderProperties firstProperties,
            InteractiveBrokersOrderProperties secondProperties)
        {
            var group = new GroupOrderManager(1, 2, 1m);
            var time = new DateTime(2026, 1, 1, 15, 0, 0, DateTimeKind.Utc);
            var orders = new List<ComboMarketOrder>
            {
                new(Symbols.SPY, 1m, time, group, properties: firstProperties),
                new(Symbols.AAPL, -1m, time, group, properties: secondProperties)
            };
            var provider = new OrderProvider();
            foreach (var order in orders)
            {
                provider.Add(order);
                group.OrderIds.Add(order.Id);
            }
            OrderProviderField.SetValue(brokerage, provider);
            return orders;
        }

        private static IBApi.Order ConvertOrder(
            InteractiveBrokersBrokerage brokerage,
            LeanOrder order)
        {
            var contract = new Contract
            {
                Symbol = "SPY",
                SecType = IB.SecurityType.Stock,
                Exchange = "SMART",
                Currency = "USD"
            };
            return (IBApi.Order)ConvertOrderMethod.Invoke(
                brokerage,
                new object[] { new List<LeanOrder> { order }, contract, 1 });
        }

        private static LeanOrder RecoverOrder(
            InteractiveBrokersBrokerage brokerage,
            IBApi.Order order)
        {
            var contract = new Contract
            {
                Symbol = "SPY",
                SecType = IB.SecurityType.Stock,
                Exchange = "SMART",
                Currency = "USD"
            };
            var orderState = new OrderState { Status = "Submitted" };
            return ((List<LeanOrder>)ConvertOrdersMethod.Invoke(
                brokerage,
                new object[] { order, contract, orderState })).Single();
        }

        private static Exception AssertAdmissionRejectsAndConversionConfigures(
            InteractiveBrokersBrokerage brokerage,
            LeanOrder order)
        {
            var admissionException = Assert.Catch(() =>
                brokerage.ValidateFinancialAdvisorOrderAdmission(order));
            Assert.IsNotNull(admissionException);
            Assert.DoesNotThrow(() => ConvertOrder(brokerage, order));
            return admissionException;
        }

        private static BrokerageAccountSnapshot CreateSnapshot(
            BrokerageAccountSnapshotStatus status,
            params BrokerageAccountGroup[] groups)
        {
            var groupNamesByAccount = groups
                .SelectMany(group => group.AccountIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    accountId => accountId,
                    accountId => new BrokerageAccountDirectoryEntry(
                        accountId,
                        BrokerageAccountRelationship.Managed,
                        groups
                            .Where(group => group.AccountIds.Contains(
                                accountId,
                                StringComparer.OrdinalIgnoreCase))
                            .Select(group => group.Name)),
                    StringComparer.OrdinalIgnoreCase);
            return CreateSnapshotWithDirectory(status, groupNamesByAccount, groups);
        }

        private static BrokerageAccountSnapshot CreateSnapshotWithDirectory(
            BrokerageAccountSnapshotStatus status,
            IReadOnlyDictionary<string, BrokerageAccountDirectoryEntry> accountDirectory,
            params BrokerageAccountGroup[] groups)
        {
            var allGroups = groups.ToDictionary(group => group.Name, StringComparer.OrdinalIgnoreCase);
            var now = DateTime.UtcNow;
            return new BrokerageAccountSnapshot(
                status,
                1,
                now,
                now,
                allGroups,
                new Dictionary<string, BrokerageAccountState>(),
                Array.Empty<string>(),
                "membership",
                "configuration",
                string.Empty,
                allGroups: allGroups,
                accountDirectory: accountDirectory);
        }

        private static int GetCollectionCount(object collection)
        {
            return (int)collection.GetType().GetProperty("Count").GetValue(collection);
        }

        private static FieldInfo FindSocketConnectedField(EClientSocket socket)
        {
            for (var type = socket.GetType(); type != null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (field.FieldType != typeof(bool))
                    {
                        continue;
                    }

                    var original = field.GetValue(socket);
                    try
                    {
                        field.SetValue(socket, true);
                        if (socket.IsConnected())
                        {
                            field.SetValue(socket, original);
                            return field;
                        }
                    }
                    finally
                    {
                        field.SetValue(socket, original);
                    }
                }
            }

            throw new InvalidOperationException(
                "Unable to locate the EClientSocket in-memory connection flag.");
        }

        /// <summary>
        /// Builds an <see cref="InteractiveBrokersBrokerage"/> with just enough state for
        /// <c>ConvertOrder</c> to run without opening a TCP connection to TWS / IB Gateway.
        /// </summary>
        private static InteractiveBrokersBrokerage CreateOfflineBrokerage(
            QCAlgorithm algorithm = null)
        {
            var brokerage = new InteractiveBrokersBrokerage();

            AccountField.SetValue(brokerage, FaMasterAccount);
            AgentDescriptionField.SetValue(brokerage, AgentDescription);
            AlgorithmField.SetValue(brokerage, algorithm);
            SymbolMapperField.SetValue(
                brokerage,
                new InteractiveBrokersSymbolMapper(
                    Composer.Instance.GetPart<IMapFileProvider>()));

            // Stub contract-details provider — returns a deterministic min tick of 0.01 so the
            // limit price round-trips cleanly through NormalizePriceToBrokerage.
            brokerage._contractSpecificationService = new IB.ContractSpecificationService(
                (contract, ticker, failIfNotFound) => new ContractDetails
                {
                    Contract = contract,
                    MinTick = 0.01
                });

            return brokerage;
        }

        private sealed class FinancialAdvisorOrderBrokerage : BacktestingBrokerage
        {
            private readonly InteractiveBrokersBrokerage _interactiveBrokersBrokerage;

            public decimal ReceivedQuantity { get; private set; }
            public int WireReadyCount { get; private set; }
            public IBApi.Order ConvertedOrder { get; private set; }
            public Exception AdmissionException { get; private set; }

            public FinancialAdvisorOrderBrokerage(
                QCAlgorithm algorithm,
                InteractiveBrokersBrokerage interactiveBrokersBrokerage)
                : base(algorithm)
            {
                _interactiveBrokersBrokerage = interactiveBrokersBrokerage;
            }

            public override bool PlaceOrder(LeanOrder order)
            {
                ReceivedQuantity = order.Quantity;
                try
                {
                    _interactiveBrokersBrokerage.ValidateFinancialAdvisorOrderAdmission(order);
                    ConvertedOrder = ConvertOrder(_interactiveBrokersBrokerage, order);
                    WireReadyCount++;
                    return true;
                }
                catch (Exception exception)
                {
                    AdmissionException = exception;
                    return false;
                }
            }
        }

        private sealed class ImmediateBrokerageTransactionHandler :
            BrokerageTransactionHandler
        {
            protected override bool SynchronousProcessing => true;

            protected override void WaitForOrderSubmission(OrderTicket ticket)
            {
                ProcessPendingRequests();
            }
        }
    }
}
