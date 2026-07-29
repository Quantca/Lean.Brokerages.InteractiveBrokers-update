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
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.InteractiveBrokers;
using QuantConnect.Orders;
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
                AssertAdmissionAndConversionRejectSame(
                    brokerage,
                    groupOrder).Message);
            AssertAdmissionAndConversionRejectSame(
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
                AssertAdmissionAndConversionRejectSame(
                    brokerage,
                    groupOrder).Message);
            Assert.AreEqual(
                unsupportedReason,
                AssertAdmissionAndConversionRejectSame(
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
                AssertAdmissionAndConversionRejectSame(
                    brokerage,
                    outsideGroupOrder).Message);
            Assert.IsInstanceOf<NotSupportedException>(
                AssertAdmissionAndConversionRejectSame(
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

        [Test]
        public void FractionalContractsOrSharesTotalRoundTripsThroughAdmissionAndConversionTest()
        {
            var brokerage = CreateOfflineBrokerage();
            UnifiedGroupsField.SetValue(brokerage, true);
            var savedGroup = new BrokerageAccountGroup(
                FaGroupName,
                "ContractsOrShares",
                new[] { "A", "B" },
                new Dictionary<string, decimal>
                {
                    ["A"] = 9.5m,
                    ["B"] = 10.25m
                });
            var state = (InteractiveBrokersFinancialAdvisorAccountState)
                RuntimeHelpers.GetUninitializedObject(typeof(InteractiveBrokersFinancialAdvisorAccountState));
            SnapshotField.SetValue(
                state,
                CreateSnapshot(BrokerageAccountSnapshotStatus.Ready, savedGroup));
            AccountStateField.SetValue(brokerage, state);
            var order = new LimitOrder(
                Symbols.SPY,
                19.75m,
                100m,
                new DateTime(2026, 1, 1, 15, 0, 0, DateTimeKind.Utc),
                properties: new InteractiveBrokersOrderProperties
                {
                    FaGroup = FaGroupName
                });

            Assert.DoesNotThrow(() => brokerage.ValidateFinancialAdvisorOrderAdmission(order));
            var ibOrder = ConvertOrder(brokerage, order);

            Assert.AreEqual(19.75m, ibOrder.TotalQuantity);
            Assert.IsEmpty(ibOrder.FaMethod);
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
                AssertAdmissionAndConversionRejectSame(
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

            if (expectedAllowed)
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
                    var expectedAllowed = requestedMethod.Length == 0 ||
                        (savedMethod is "NetLiq" or "AvailableEquity" or "Equal") &&
                        (requestedMethod == savedMethod || requestedMethod == "PctChange");
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

        private static Exception AssertAdmissionAndConversionRejectSame(
            InteractiveBrokersBrokerage brokerage,
            LeanOrder order)
        {
            var admissionException = Assert.Catch(() =>
                brokerage.ValidateFinancialAdvisorOrderAdmission(order));
            var conversionException = Assert.Throws<TargetInvocationException>(() =>
                ConvertOrder(brokerage, order)).InnerException;

            Assert.Multiple(() =>
            {
                Assert.IsNotNull(admissionException);
                Assert.IsNotNull(conversionException);
                Assert.AreEqual(
                    admissionException.GetType(),
                    conversionException.GetType());
                Assert.AreEqual(
                    admissionException.Message,
                    conversionException.Message);
            });
            return admissionException;
        }

        private static BrokerageAccountSnapshot CreateSnapshot(
            BrokerageAccountSnapshotStatus status,
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
                allGroups: allGroups);
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
        private static InteractiveBrokersBrokerage CreateOfflineBrokerage()
        {
            var brokerage = new InteractiveBrokersBrokerage();

            AccountField.SetValue(brokerage, FaMasterAccount);
            AgentDescriptionField.SetValue(brokerage, AgentDescription);

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
    }
}
