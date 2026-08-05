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

using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.InteractiveBrokers;
using IB = QuantConnect.Brokerages.InteractiveBrokers.Client;

namespace QuantConnect.Tests.Brokerages.InteractiveBrokers
{
    [TestFixture]
    public class InteractiveBrokersFinancialAdvisorErrorRoutingTests
    {
        [Test]
        public void HandleErrorSuppressesOnlyOwnedFinancialAdvisorServiceRequestErrors()
        {
            using var client = new IB.InteractiveBrokersClient(new IBApi.EReaderMonitorSignal());
            using var accountState = new InteractiveBrokersFinancialAdvisorAccountState(
                client,
                () => { },
                () => true,
                _ => Symbol.Empty,
                "F-MASTER");
            using var brokerage = new InteractiveBrokersBrokerage();
            SetPrivateFieldValue(brokerage, "_financialAdvisorAccountState", accountState);

            try
            {
                var serviceRequestId = (int)typeof(InteractiveBrokersFinancialAdvisorAccountState)
                    .GetMethod("NextRequestId", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(accountState, null);
                var messages = new List<BrokerageMessageEvent>();
                brokerage.Message += (_, message) => messages.Add(message);
                client.Error += brokerage.HandleError;

                client.error(serviceRequestId, 0, 321, "FA service failure", string.Empty);
                client.error(-2, 0, 321, "unallocated negative request failure", string.Empty);

                Assert.Multiple(() =>
                {
                    Assert.IsTrue(accountState.IsServiceOwnedRequestId(serviceRequestId));
                    Assert.AreEqual(1, messages.Count);
                    Assert.AreEqual(BrokerageMessageType.Error, messages[0].Type);
                    Assert.AreEqual("321", messages[0].Code);
                    StringAssert.Contains("unallocated negative request failure", messages[0].Message);
                });
            }
            finally
            {
                SetPrivateFieldValue(brokerage, "_financialAdvisorAccountState", null);
            }
        }

        private static void SetPrivateFieldValue(object instance, string name, object value)
        {
            instance.GetType()
                .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(instance, value);
        }
    }
}
