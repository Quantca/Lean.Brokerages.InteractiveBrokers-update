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
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Brokerages.InteractiveBrokers;
using QuantConnect.Configuration;
using QuantConnect.IBAutomater;
using QuantConnect.Interfaces;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;
using IB = QuantConnect.Brokerages.InteractiveBrokers.Client;

namespace QuantConnect.Tests.Brokerages.InteractiveBrokers
{
    [TestFixture]
    public class InteractiveBrokersBrokerageFactoryTests
    {
        public static readonly IAlgorithm AlgorithmDependency = new InteractiveBrokersBrokerageFactoryAlgorithmDependency();

        [Test]
        public void PublicConstructorsPreserveBinaryCompatibilityTest()
        {
            var constructors = typeof(InteractiveBrokersBrokerage)
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public);

            Assert.AreEqual(5, constructors.Length);

            AssertConstructor(constructors, Array.Empty<Type>(), Array.Empty<string>());
            AssertConstructor(
                constructors,
                new[] { typeof(IAlgorithm), typeof(IOrderProvider), typeof(ISecurityProvider) },
                new[] { "algorithm", "orderProvider", "securityProvider" });
            AssertConstructor(
                constructors,
                new[] { typeof(IAlgorithm), typeof(IOrderProvider), typeof(ISecurityProvider), typeof(string) },
                new[] { "algorithm", "orderProvider", "securityProvider", "account" });

            var legacyParameters = AssertConstructor(
                constructors,
                new[]
                {
                    typeof(IAlgorithm),
                    typeof(IOrderProvider),
                    typeof(ISecurityProvider),
                    typeof(string),
                    typeof(string),
                    typeof(int),
                    typeof(string),
                    typeof(string),
                    typeof(string),
                    typeof(string),
                    typeof(string),
                    typeof(string),
                    typeof(bool),
                    typeof(TimeSpan?),
                    typeof(string)
                },
                new[]
                {
                    "algorithm",
                    "orderProvider",
                    "securityProvider",
                    "account",
                    "host",
                    "port",
                    "ibDirectory",
                    "ibVersion",
                    "userName",
                    "password",
                    "tradingMode",
                    "agentDescription",
                    "loadExistingHoldings",
                    "weeklyRestartUtcTime",
                    "financialAdvisorsGroupFilter"
                });
            var unifiedParameters = AssertConstructor(
                constructors,
                new[]
                {
                    typeof(IAlgorithm),
                    typeof(IOrderProvider),
                    typeof(ISecurityProvider),
                    typeof(string),
                    typeof(string),
                    typeof(int),
                    typeof(string),
                    typeof(string),
                    typeof(string),
                    typeof(string),
                    typeof(string),
                    typeof(string),
                    typeof(bool),
                    typeof(TimeSpan?),
                    typeof(string),
                    typeof(bool),
                    typeof(bool)
                },
                new[]
                {
                    "algorithm",
                    "orderProvider",
                    "securityProvider",
                    "account",
                    "host",
                    "port",
                    "ibDirectory",
                    "ibVersion",
                    "userName",
                    "password",
                    "tradingMode",
                    "agentDescription",
                    "loadExistingHoldings",
                    "weeklyRestartUtcTime",
                    "financialAdvisorsGroupFilter",
                    "financialAdvisorGroupManagementEnabled",
                    "financialAdvisorUnifiedGroupsEnabled"
                });

            Assert.Multiple(() =>
            {
                Assert.IsTrue(legacyParameters.Take(11).All(parameter => !parameter.IsOptional));
                Assert.IsTrue(legacyParameters.Skip(11).All(parameter => parameter.IsOptional));
                Assert.AreEqual(
                    IB.AgentDescription.Individual,
                    legacyParameters[11].DefaultValue);
                Assert.AreEqual(true, legacyParameters[12].DefaultValue);
                Assert.IsNull(legacyParameters[13].DefaultValue);
                Assert.IsNull(legacyParameters[14].DefaultValue);
                Assert.IsTrue(unifiedParameters.All(parameter => !parameter.IsOptional));
            });
        }

        [Test]
        public void IBAutomaterApiSupportsMobileAuthenticatorWithoutChangingExistingConstructors()
        {
            var constructors = typeof(QuantConnect.IBAutomater.IBAutomater)
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                .Select(constructor => constructor.GetParameters())
                .ToList();

            Assert.Multiple(() =>
            {
                Assert.IsTrue(HasConstructor(constructors,
                    typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(int), typeof(bool)));
                Assert.IsTrue(HasConstructor(constructors,
                    typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(int), typeof(bool), typeof(bool)));
                Assert.IsTrue(HasConstructor(constructors,
                    typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(int), typeof(bool), typeof(bool),
                    typeof(TwoFactorAuthenticationMethod), typeof(string)));
            });

            var expectedErrorCodes = new[]
            {
                ErrorCode.None,
                ErrorCode.ProcessStartFailed,
                ErrorCode.IbGatewayVersionNotInstalled,
                ErrorCode.JavaNotFound,
                ErrorCode.JavaException,
                ErrorCode.LoginFailed,
                ErrorCode.ExistingSessionDetected,
                ErrorCode.SecurityDialogDetected,
                ErrorCode.TwoFactorConfirmationTimeout,
                ErrorCode.InitializationTimeout,
                ErrorCode.UnsupportedVersion,
                ErrorCode.ApiSupportNotAvailable,
                ErrorCode.RestartedProcessNotFound,
                ErrorCode.UnknownMessageWindowDetected,
                ErrorCode.SoftRestartTimeout,
                ErrorCode.LoginFailedAccountTasksRequired,
                ErrorCode.FinancialAdvisorAllocationGroupsConfigurationUnavailable,
                ErrorCode.MobileAuthenticatorAuthenticationFailed
            };
            CollectionAssert.AreEqual(Enumerable.Range(0, expectedErrorCodes.Length), expectedErrorCodes.Select(value => (int)value));
            CollectionAssert.AreEqual(expectedErrorCodes, Enum.GetValues<ErrorCode>());
        }

        [TestCase(null, null, TwoFactorAuthenticationMethod.IbKey)]
        [TestCase("", "", TwoFactorAuthenticationMethod.IbKey)]
        [TestCase("  IB-KEY  ", "  ", TwoFactorAuthenticationMethod.IbKey)]
        [TestCase("  MOBILE-AUTHENTICATOR  ", "JBSWY3DPEHPK3PXP", TwoFactorAuthenticationMethod.MobileAuthenticator)]
        public void ParsesTwoFactorAuthenticationSettings(
            string method,
            string secret,
            TwoFactorAuthenticationMethod expected)
        {
            Assert.AreEqual(
                expected,
                InteractiveBrokersBrokerage.ParseTwoFactorAuthenticationSettings(method, secret));
        }

        [TestCase("unknown", null)]
        [TestCase("mobile-authenticator", null)]
        [TestCase("mobile-authenticator", " ")]
        public void RejectsInvalidTwoFactorAuthenticationSettings(string method, string secret)
        {
            Assert.Throws<ArgumentException>(() =>
                InteractiveBrokersBrokerage.ParseTwoFactorAuthenticationSettings(method, secret));
        }

        [Test]
        public void RejectsUnusedMobileAuthenticatorSecretWithoutLeakingIt()
        {
            const string secret = "JBSWY3DPEHPK3PXP";

            var exception = Assert.Throws<ArgumentException>(() =>
                InteractiveBrokersBrokerage.ParseTwoFactorAuthenticationSettings("ib-key", secret));

            StringAssert.DoesNotContain(secret, exception.Message);
        }

        [Test]
        public void RejectsInvalidTwoFactorAuthenticationBeforeFactoryInitialization()
        {
            using var factory = new InteractiveBrokersBrokerageFactory();
            var job = CreateJob("ib-key", "JBSWY3DPEHPK3PXP");

            Assert.Throws<ArgumentException>(() => factory.CreateBrokerage(job, AlgorithmDependency));
        }

        [Test]
        [NonParallelizable]
        public void RejectsInvalidTwoFactorAuthenticationBeforeConfigurationInitialization()
        {
            using var configScope = new ConfigValueScope(
                InteractiveBrokersBrokerage.TwoFactorAuthenticationMethodConfigKey,
                InteractiveBrokersBrokerage.MobileAuthenticatorSecretConfigKey);

            Config.Set(InteractiveBrokersBrokerage.TwoFactorAuthenticationMethodConfigKey, "mobile-authenticator");
            Config.Set(InteractiveBrokersBrokerage.MobileAuthenticatorSecretConfigKey, "");

            Assert.Throws<ArgumentException>(() => new InteractiveBrokersBrokerage(
                AlgorithmDependency,
                AlgorithmDependency.Transactions,
                AlgorithmDependency.Portfolio));
        }

        [Test]
        public void RejectsInvalidTwoFactorAuthenticationBeforeSetJobInitialization()
        {
            using var brokerage = new InteractiveBrokersBrokerage();
            var job = CreateJob("mobile-authenticator", null);

            Assert.Throws<ArgumentException>(() => brokerage.SetJob(job));
        }

        [TestCase(TwoFactorAuthenticationMethod.IbKey, ErrorCode.TwoFactorConfirmationTimeout, true)]
        [TestCase(TwoFactorAuthenticationMethod.IbKey, ErrorCode.InitializationTimeout, true)]
        [TestCase(TwoFactorAuthenticationMethod.MobileAuthenticator, ErrorCode.TwoFactorConfirmationTimeout, false)]
        [TestCase(TwoFactorAuthenticationMethod.MobileAuthenticator, ErrorCode.InitializationTimeout, false)]
        [TestCase(TwoFactorAuthenticationMethod.MobileAuthenticator, ErrorCode.MobileAuthenticatorAuthenticationFailed, false)]
        public void OnlyIbKeyTimeoutsAreRecoverable(
            TwoFactorAuthenticationMethod method,
            ErrorCode errorCode,
            bool expected)
        {
            using var brokerage = new InteractiveBrokersBrokerage();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(InteractiveBrokersBrokerage).GetField("_twoFactorAuthenticationMethod", flags).SetValue(brokerage, method);
            typeof(InteractiveBrokersBrokerage).GetField("_pastFirstConnection", flags).SetValue(brokerage, true);

            var result = (bool)typeof(InteractiveBrokersBrokerage)
                .GetMethod("IsRecuperable2FATimeout", flags)
                .Invoke(brokerage, new object[] { new StartResult(errorCode) });

            Assert.AreEqual(expected, result);
        }

        private static ParameterInfo[] AssertConstructor(
            IEnumerable<ConstructorInfo> constructors,
            Type[] parameterTypes,
            string[] parameterNames)
        {
            var parameters = constructors
                .Select(constructor => constructor.GetParameters())
                .Single(candidate => candidate
                    .Select(parameter => parameter.ParameterType)
                    .SequenceEqual(parameterTypes));

            CollectionAssert.AreEqual(parameterNames, parameters.Select(parameter => parameter.Name));
            return parameters;
        }

        private static bool HasConstructor(IEnumerable<ParameterInfo[]> constructors, params Type[] parameterTypes)
        {
            return constructors.Any(parameters => parameters
                .Select(parameter => parameter.ParameterType)
                .SequenceEqual(parameterTypes));
        }

        private static LiveNodePacket CreateJob(string method, string secret)
        {
            var brokerageData = new Dictionary<string, string>
            {
                ["ib-account"] = "DU1234567",
                ["ib-user-name"] = "user",
                ["ib-password"] = "password",
                ["ib-trading-mode"] = "paper",
                ["ib-agent-description"] = "I"
            };

            if (method != null)
            {
                brokerageData[InteractiveBrokersBrokerage.TwoFactorAuthenticationMethodConfigKey] = method;
            }
            if (secret != null)
            {
                brokerageData[InteractiveBrokersBrokerage.MobileAuthenticatorSecretConfigKey] = secret;
            }

            return new LiveNodePacket { BrokerageData = brokerageData };
        }

        [Test]
        public void InitializesInstanceFromComposer()
        {
            var composer = Composer.Instance;
            using (var factory = composer.Single<IBrokerageFactory>(instance => instance.BrokerageType == typeof (InteractiveBrokersBrokerage)))
            {
                Assert.IsNotNull(factory);

                var job = new LiveNodePacket {BrokerageData = factory.BrokerageData};
                using (var brokerage = factory.CreateBrokerage(job, AlgorithmDependency))
                {
                    Assert.IsNotNull(brokerage);
                    Assert.IsInstanceOf<InteractiveBrokersBrokerage>(brokerage);

                    brokerage.Connect();
                    Assert.IsTrue(brokerage.IsConnected);
                }
            }
        }

        [TestCase("ib-financial-advisors-group-management-enabled")]
        [TestCase("ib-financial-advisors-unified-groups-enabled")]
        public void RejectsInvalidFinancialAdvisorBooleanSetting(string setting)
        {
            using var factory = new InteractiveBrokersBrokerageFactory();
            var job = new LiveNodePacket
            {
                BrokerageData = new Dictionary<string, string>
                {
                    ["ib-account"] = "F1234567",
                    ["ib-user-name"] = "user",
                    ["ib-password"] = "password",
                    ["ib-trading-mode"] = "paper",
                    ["ib-agent-description"] = "I",
                    [setting] = "not-a-boolean"
                }
            };

            var exception = Assert.Throws<Exception>(() => factory.CreateBrokerage(job, AlgorithmDependency));

            StringAssert.Contains("must be either 'true' or 'false'", exception.Message);
        }

        [TestCase(null, null, null, null, false, false)]
        [TestCase("  Group Name  ", "", " ", "  Group Name  ", false, false)]
        [TestCase("Group", "false", "true", "Group", false, true)]
        [TestCase("Group", "true", "true", "Group", true, true)]
        public void ParsesFinancialAdvisorSettings(
            string filter,
            string groupManagement,
            string unifiedGroups,
            string expectedFilter,
            bool expectedGroupManagement,
            bool expectedUnifiedGroups)
        {
            var brokerageData = new Dictionary<string, string>();
            if (filter != null)
            {
                brokerageData["ib-financial-advisors-group-filter"] = filter;
            }
            if (groupManagement != null)
            {
                brokerageData["ib-financial-advisors-group-management-enabled"] = groupManagement;
            }
            if (unifiedGroups != null)
            {
                brokerageData["ib-financial-advisors-unified-groups-enabled"] = unifiedGroups;
            }
            var errors = new List<string>();

            InteractiveBrokersBrokerageFactory.ParseFinancialAdvisorSettings(
                brokerageData,
                errors,
                out var parsedFilter,
                out var parsedGroupManagement,
                out var parsedUnifiedGroups);

            Assert.Multiple(() =>
            {
                Assert.IsEmpty(errors);
                Assert.AreEqual(expectedFilter, parsedFilter);
                Assert.AreEqual(expectedGroupManagement, parsedGroupManagement);
                Assert.AreEqual(expectedUnifiedGroups, parsedUnifiedGroups);
            });
        }

        [TestCase("ib-financial-advisors-group-management-enabled")]
        [TestCase("ib-financial-advisors-unified-groups-enabled")]
        public void RejectsInvalidFinancialAdvisorBooleanBeforeSetJobInitialization(
            string setting)
        {
            using var brokerage = new InteractiveBrokersBrokerage();
            var job = CreateJob(null, null);
            job.BrokerageData[setting] = "not-a-boolean";

            var exception = Assert.Throws<ArgumentException>(() =>
                brokerage.SetJob(job));

            StringAssert.Contains("must be either 'true' or 'false'", exception.Message);
        }

        [Test]
        public void RejectsFinancialAdvisorGroupManagementWithoutUnifiedGroups()
        {
            using var factory = new InteractiveBrokersBrokerageFactory();
            var job = new LiveNodePacket
            {
                BrokerageData = new Dictionary<string, string>
                {
                    ["ib-account"] = "F1234567",
                    ["ib-user-name"] = "user",
                    ["ib-password"] = "password",
                    ["ib-trading-mode"] = "paper",
                    ["ib-agent-description"] = "I",
                    ["ib-financial-advisors-group-management-enabled"] = "true",
                    ["ib-financial-advisors-unified-groups-enabled"] = "false"
                }
            };

            var exception = Assert.Throws<Exception>(() => factory.CreateBrokerage(job, AlgorithmDependency));

            StringAssert.Contains("requires 'ib-financial-advisors-unified-groups-enabled=true'", exception.Message);
        }

        [Test]
        public void RejectsFinancialAdvisorGroupManagementWithoutUnifiedGroupsBeforeSetJobInitialization()
        {
            using var brokerage = new InteractiveBrokersBrokerage();
            var job = CreateJob(null, null);
            job.BrokerageData["ib-financial-advisors-group-management-enabled"] = "true";
            job.BrokerageData["ib-financial-advisors-unified-groups-enabled"] = "false";

            var exception = Assert.Throws<ArgumentException>(() =>
                brokerage.SetJob(job));

            StringAssert.Contains(
                "requires 'ib-financial-advisors-unified-groups-enabled=true'",
                exception.Message);
        }

        [TestCase("true")]
        [TestCase("false")]
        [NonParallelizable]
        public void ExportsFinancialAdvisorUnifiedGroupsSettingInBrokerageData(string value)
        {
            const string key = "ib-financial-advisors-unified-groups-enabled";
            using var configScope = new ConfigValueScope(key);

            Config.Set(key, value);
            using var factory = new InteractiveBrokersBrokerageFactory();

            Assert.AreEqual(value, factory.BrokerageData[key]);
        }

        [TestCase("ib-two-factor-authentication-method", "mobile-authenticator")]
        [TestCase("ib-mobile-authenticator-secret", "JBSWY3DPEHPK3PXP")]
        [NonParallelizable]
        public void ExportsTwoFactorAuthenticationSettingsInBrokerageData(string key, string value)
        {
            using var configScope = new ConfigValueScope(key);

            Config.Set(key, value);
            using var factory = new InteractiveBrokersBrokerageFactory();

            Assert.AreEqual(value, factory.BrokerageData[key]);
        }

        [Test]
        [NonParallelizable]
        public void ConfigValueScopeRestoresMissingAndExplicitlyEmptyValues()
        {
            const string key = "interactive-brokers-factory-test-setting";
            using var originalScope = new ConfigValueScope(key);
            RemoveConfigValue(key);

            using (new ConfigValueScope(key))
            {
                Config.Set(key, "temporary");
            }
            Assert.IsNull(Config.GetToken(key));

            Config.Set(key, "");
            using (new ConfigValueScope(key))
            {
                Config.Set(key, "temporary");
            }

            Assert.Multiple(() =>
            {
                Assert.AreEqual(JTokenType.String, Config.GetToken(key)?.Type);
                Assert.AreEqual(string.Empty, Config.Get(key));
            });
        }

        private static void RemoveConfigValue(string key)
        {
            Config.GetToken(key)?.Parent?.Remove();
        }

        private sealed class ConfigValueScope : IDisposable
        {
            private readonly Dictionary<string, JToken> _originalValues;

            public ConfigValueScope(params string[] keys)
            {
                _originalValues = keys.ToDictionary(key => key, key => Config.GetToken(key)?.DeepClone());
            }

            public void Dispose()
            {
                foreach (var pair in _originalValues)
                {
                    if (pair.Value == null)
                    {
                        RemoveConfigValue(pair.Key);
                    }
                    else
                    {
                        Config.Set(pair.Key, pair.Value.DeepClone());
                    }
                }
            }
        }

        class InteractiveBrokersBrokerageFactoryAlgorithmDependency : QCAlgorithm
        {
        }
    }
}
