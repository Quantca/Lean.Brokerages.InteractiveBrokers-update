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
using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Brokerages.InteractiveBrokers;
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

        class InteractiveBrokersBrokerageFactoryAlgorithmDependency : QCAlgorithm
        {
        }
    }
}
