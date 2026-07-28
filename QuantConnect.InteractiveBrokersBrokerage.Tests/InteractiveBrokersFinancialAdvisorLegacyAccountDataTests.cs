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
using System.Linq;
using System.Reflection;
using IBApi;
using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Brokerages.InteractiveBrokers;
using QuantConnect.Interfaces;
using QuantConnect.Util;
using IB = QuantConnect.Brokerages.InteractiveBrokers.Client;

namespace QuantConnect.Tests.Brokerages.InteractiveBrokers
{
    [TestFixture]
    public class InteractiveBrokersFinancialAdvisorLegacyAccountDataTests
    {
        private const BindingFlags InstanceNonPublic =
            BindingFlags.Instance | BindingFlags.NonPublic;
        private const string AccountId = "DU-LEGACY";
        private const string GroupName = "Alpha";
        private const decimal CashBalance = 123.45m;
        private const decimal ExactPosition = 1.75m;
        private const int PositiveRequestId = 41;
        private const int ServiceRequestId = -2;

        private static readonly FieldInfo AccountField =
            GetRequiredField("_account");
        private static readonly FieldInfo AlgorithmField =
            GetRequiredField("_algorithm");
        private static readonly FieldInfo ClientField =
            GetRequiredField("_client");
        private static readonly FieldInfo FinancialAdvisorGroupFilterField =
            GetRequiredField("_financialAdvisorsGroupFilter");
        private static readonly FieldInfo LoadExistingHoldingsField =
            GetRequiredField("_loadExistingHoldings");
        private static readonly FieldInfo SymbolMapperField =
            GetRequiredField("_symbolMapper");
        private static readonly MethodInfo ConfigureFinancialAdvisorFeaturesMethod =
            GetRequiredMethod("ConfigureFinancialAdvisorFeatures");
        private static readonly MethodInfo DisposeFinancialAdvisorAccountStateMethod =
            GetRequiredMethod("DisposeFinancialAdvisorAccountState");
        private static readonly MethodInfo HandlePortfolioUpdatesMethod =
            GetRequiredMethod("HandlePortfolioUpdates");
        private static readonly MethodInfo HandleUpdateAccountValueMethod =
            GetRequiredMethod("HandleUpdateAccountValue");
        private static readonly MethodInfo InitializeFinancialAdvisorAccountStateMethod =
            GetRequiredMethod("InitializeFinancialAdvisorAccountState");

        [Test]
        public void UnifiedGroupsDisabledIsUpstreamEquivalentTest()
        {
            foreach (var filterSet in new[] { false, true })
            {
                var filter = filterSet ? GroupName : string.Empty;
                using var upstream = LegacyAccountScenario.CreateUpstream(filter);
                using var disabled = LegacyAccountScenario.CreateConfigured(
                    filter,
                    unifiedGroupsEnabled: false);

                upstream.EmitLegacyRows();
                disabled.EmitLegacyRows();

                Assert.Multiple(() =>
                {
                    Assert.AreEqual(
                        upstream.GetCashBalance(),
                        disabled.GetCashBalance(),
                        $"Cash differed with filterSet={filterSet}.");
                    Assert.AreEqual(
                        upstream.GetHoldingQuantity(),
                        disabled.GetHoldingQuantity(),
                        $"Holdings differed with filterSet={filterSet}.");
                    Assert.AreEqual(
                        Convert.ToInt32(ExactPosition),
                        disabled.GetHoldingQuantity(),
                        "Disabled mode must preserve the upstream whole-position conversion.");
                });
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void LegacyAccountDataMatrixTest(
            bool filterSet,
            bool unifiedGroupsEnabled)
        {
            var filter = filterSet
                ? GroupName
                : unifiedGroupsEnabled
                    ? " \t "
                    : string.Empty;
            using var scenario = LegacyAccountScenario.CreateConfigured(
                filter,
                unifiedGroupsEnabled);

            if (!filterSet && unifiedGroupsEnabled)
            {
                Assert.AreEqual(
                    string.Empty,
                    FinancialAdvisorGroupFilterField.GetValue(scenario.Brokerage),
                    "Whitespace-only filters must normalize to the blank-filter path.");
            }

            scenario.EmitLegacyRows();
            if (unifiedGroupsEnabled)
            {
                scenario.EmitServiceRows();
            }

            Assert.Multiple(() =>
            {
                Assert.AreEqual(CashBalance, scenario.GetCashBalance());
                Assert.AreEqual(
                    unifiedGroupsEnabled
                        ? ExactPosition
                        : Convert.ToInt32(ExactPosition),
                    scenario.GetHoldingQuantity());
                Assert.AreEqual(
                    "SPY",
                    scenario.Brokerage.GetAccountHoldings().Single().Symbol.Value);
            });
        }

        private static FieldInfo GetRequiredField(string name)
        {
            return typeof(InteractiveBrokersBrokerage).GetField(name, InstanceNonPublic)
                ?? throw new InvalidOperationException($"Missing brokerage field '{name}'.");
        }

        private static MethodInfo GetRequiredMethod(string name)
        {
            return typeof(InteractiveBrokersBrokerage).GetMethod(name, InstanceNonPublic)
                ?? throw new InvalidOperationException($"Missing brokerage method '{name}'.");
        }

        private sealed class LegacyAccountScenario : IDisposable
        {
            private readonly FieldInfo _socketConnectedField;
            private bool _disposed;

            public InteractiveBrokersBrokerage Brokerage { get; }
            public IB.InteractiveBrokersClient Client { get; }

            private LegacyAccountScenario(
                string financialAdvisorGroupFilter,
                bool unifiedGroupsEnabled,
                bool configureFeatures)
            {
                Brokerage = new InteractiveBrokersBrokerage();
                Client = new IB.InteractiveBrokersClient(new EReaderMonitorSignal());

                AccountField.SetValue(Brokerage, "F-MASTER");
                AlgorithmField.SetValue(Brokerage, new QCAlgorithm());
                ClientField.SetValue(Brokerage, Client);
                LoadExistingHoldingsField.SetValue(Brokerage, true);
                SymbolMapperField.SetValue(
                    Brokerage,
                    new InteractiveBrokersSymbolMapper(
                        Composer.Instance.GetPart<IMapFileProvider>()));

                if (configureFeatures)
                {
                    ConfigureFinancialAdvisorFeaturesMethod.Invoke(
                        Brokerage,
                        new object[]
                        {
                            financialAdvisorGroupFilter,
                            false,
                            unifiedGroupsEnabled
                        });
                }
                else
                {
                    FinancialAdvisorGroupFilterField.SetValue(
                        Brokerage,
                        financialAdvisorGroupFilter);
                }

                InitializeFinancialAdvisorAccountStateMethod.Invoke(Brokerage, null);
                Client.UpdateAccountValue += HandleUpdateAccountValue;
                Client.UpdatePortfolio += HandlePortfolioUpdate;
                if (!string.IsNullOrEmpty(
                    (string)FinancialAdvisorGroupFilterField.GetValue(Brokerage)))
                {
                    Client.AccountUpdateMulti += HandleUpdateAccountValue;
                }

                _socketConnectedField = FindSocketConnectedField(Client.ClientSocket);
                _socketConnectedField.SetValue(Client.ClientSocket, true);
                Assert.IsTrue(
                    Client.Connected,
                    "The hermetic client must be marked connected without calling eConnect.");
            }

            public static LegacyAccountScenario CreateConfigured(
                string financialAdvisorGroupFilter,
                bool unifiedGroupsEnabled)
            {
                return new LegacyAccountScenario(
                    financialAdvisorGroupFilter,
                    unifiedGroupsEnabled,
                    configureFeatures: true);
            }

            public static LegacyAccountScenario CreateUpstream(
                string financialAdvisorGroupFilter)
            {
                return new LegacyAccountScenario(
                    financialAdvisorGroupFilter,
                    unifiedGroupsEnabled: false,
                    configureFeatures: false);
            }

            public void EmitLegacyRows()
            {
                if (string.IsNullOrEmpty(
                    (string)FinancialAdvisorGroupFilterField.GetValue(Brokerage)))
                {
                    Client.updatePortfolio(
                        CreateContract(),
                        ExactPosition,
                        101,
                        176.75,
                        100.5,
                        0,
                        0,
                        AccountId);
                    Client.updateAccountValue(
                        "CashBalance",
                        CashBalance.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Currencies.USD,
                        AccountId);
                    return;
                }

                Client.positionMulti(
                    PositiveRequestId,
                    AccountId,
                    string.Empty,
                    CreateContract(),
                    ExactPosition,
                    100.5);
                Client.accountUpdateMulti(
                    PositiveRequestId,
                    AccountId,
                    string.Empty,
                    "CashBalance",
                    CashBalance.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Currencies.USD);
            }

            public void EmitServiceRows()
            {
                Client.positionMulti(
                    ServiceRequestId,
                    "DU-SERVICE",
                    string.Empty,
                    CreateContract(),
                    999.5m,
                    900.5);
                Client.accountUpdateMulti(
                    ServiceRequestId,
                    AccountId,
                    string.Empty,
                    "CashBalance",
                    "9999.99",
                    Currencies.USD);
            }

            public decimal GetCashBalance()
            {
                return Brokerage.GetCashBalance().Single(
                    cash => cash.Currency == Currencies.USD).Amount;
            }

            public decimal GetHoldingQuantity()
            {
                return Brokerage.GetAccountHoldings().Single().Quantity;
            }

            private void HandlePortfolioUpdate(
                object sender,
                IB.UpdatePortfolioEventArgs eventArgs)
            {
                HandlePortfolioUpdatesMethod.Invoke(
                    Brokerage,
                    new object[] { sender, eventArgs });
            }

            private void HandleUpdateAccountValue(
                object sender,
                IB.UpdateAccountValueEventArgs eventArgs)
            {
                HandleUpdateAccountValueMethod.Invoke(
                    Brokerage,
                    new object[] { sender, eventArgs });
            }

            private static Contract CreateContract()
            {
                return new Contract
                {
                    ConId = 101,
                    Symbol = "SPY",
                    LocalSymbol = "SPY",
                    SecType = IB.SecurityType.Stock,
                    Currency = Currencies.USD,
                    Exchange = "SMART",
                    PrimaryExch = "ARCA",
                    Multiplier = "1"
                };
            }

            private static FieldInfo FindSocketConnectedField(EClientSocket socket)
            {
                for (var type = socket.GetType(); type != null; type = type.BaseType)
                {
                    foreach (var field in type.GetFields(InstanceNonPublic))
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

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                DisposeFinancialAdvisorAccountStateMethod.Invoke(Brokerage, null);
                _socketConnectedField.SetValue(Client.ClientSocket, false);
                Client.Dispose();
                ClientField.SetValue(Brokerage, null);
                Brokerage.Dispose();
            }
        }
    }
}
