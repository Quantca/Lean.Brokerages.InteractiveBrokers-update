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
using IBApi;
using NUnit.Framework;
using QuantConnect.Brokerages.InteractiveBrokers.Client;

namespace QuantConnect.Tests.Brokerages.InteractiveBrokers
{
    [TestFixture]
    public class InteractiveBrokersClientFinancialAdvisorEventTests
    {
        [Test]
        public void MultiCallbacksPreservePublicEventsAndAddRequestAwareEvents()
        {
            using var client = new InteractiveBrokersClient(new EReaderMonitorSignal());
            AccountUpdateMultiEventArgs accountUpdate = null;
            AccountUpdateMultiEndEventArgs accountUpdateEnd = null;
            PositionMultiEventArgs positionUpdate = null;
            RequestEndEventArgs positionUpdateEnd = null;
            UpdateAccountValueEventArgs legacyAccountUpdate = null;
            AccountUpdateMultiEndEventArgs legacyAccountUpdateEnd = null;
            UpdatePortfolioEventArgs legacyPositionUpdate = null;
            var legacyPositionUpdateEndCount = 0;

            client.AccountUpdateMultiWithRequestId += (_, args) => accountUpdate = args;
            client.AccountUpdateMultiEndWithRequestId += (_, args) => accountUpdateEnd = args;
            client.PositionMulti += (_, args) => positionUpdate = args;
            client.PositionMultiEndWithRequestId += (_, args) => positionUpdateEnd = args;
            client.AccountUpdateMulti += (_, args) => legacyAccountUpdate = args;
            client.AccountUpdateMultiEnd += (_, args) => legacyAccountUpdateEnd = args;
            client.UpdatePortfolio += (_, args) => legacyPositionUpdate = args;
            client.PositionMultiEnd += (_, _) => legacyPositionUpdateEndCount++;

            var contract = new Contract { Symbol = "SPY", SecType = "STK" };
            client.accountUpdateMulti(17, "DU123", "ModelA", "NetLiquidation", "1000", "USD");
            client.accountUpdateMultiEnd(17);
            client.positionMulti(18, "DU123", "ModelA", contract, 1.25m, 100.5);
            client.positionMultiEnd(18);

            Assert.Multiple(() =>
            {
                Assert.AreEqual(17, accountUpdate.RequestId);
                Assert.AreEqual("DU123", accountUpdate.Account);
                Assert.AreEqual("ModelA", accountUpdate.ModelCode);
                Assert.AreEqual("NetLiquidation", legacyAccountUpdate.Key);
                Assert.AreEqual("DU123", legacyAccountUpdate.AccountName);
                Assert.AreEqual(17, accountUpdateEnd.RequestId);
                Assert.AreEqual(17, legacyAccountUpdateEnd.RequestId);

                Assert.AreEqual(18, positionUpdate.RequestId);
                Assert.AreEqual("DU123", positionUpdate.Account);
                Assert.AreEqual("ModelA", positionUpdate.ModelCode);
                Assert.AreSame(contract, positionUpdate.Contract);
                Assert.AreEqual(1.25m, positionUpdate.Position);
                Assert.AreEqual(100.5, positionUpdate.AverageCost);
                Assert.AreEqual(1, legacyPositionUpdate.Position);
                Assert.AreEqual(1.25m, legacyPositionUpdate.PositionQuantity);
                Assert.AreEqual("DU123", legacyPositionUpdate.AccountName);
                Assert.AreEqual(18, positionUpdateEnd.RequestId);
                Assert.AreEqual(1, legacyPositionUpdateEndCount);
            });
        }

        [Test]
        public void UnkeyedCallbacksPreservePublicEventsAndAddInternalEvents()
        {
            using var client = new InteractiveBrokersClient(new EReaderMonitorSignal());
            var publicErrorCount = 0;
            var internalErrorCount = 0;
            var publicReceiveFaCount = 0;
            var internalReceiveFaCount = 0;
            var publicManagedAccountsCount = 0;
            var internalManagedAccountsCount = 0;
            var publicFamilyCodesCount = 0;
            var internalFamilyCodesCount = 0;
            ReplaceFaEndEventArgs replacement = null;

            client.Error += (_, _) => publicErrorCount++;
            client.InternalError += (_, _) => internalErrorCount++;
            client.ReceiveFa += (_, _) => publicReceiveFaCount++;
            client.InternalReceiveFa += (_, _) => internalReceiveFaCount++;
            client.ManagedAccounts += (_, _) => publicManagedAccountsCount++;
            client.InternalManagedAccounts += (_, _) => internalManagedAccountsCount++;
            client.FamilyCodes += (_, _) => publicFamilyCodesCount++;
            client.InternalFamilyCodes += (_, _) => internalFamilyCodesCount++;
            client.ReplaceFaEnd += (_, args) => replacement = args;

            client.error(17, 123, 10230, "configuration pending", string.Empty);
            client.receiveFA(1, "<ListOfGroups />");
            client.managedAccounts("DU123");
            client.familyCodes(new[]
            {
                new FamilyCode { AccountID = "DU123", FamilyCodeStr = "FamilyA" }
            });
            client.replaceFAEnd(19, null);

            Assert.Multiple(() =>
            {
                Assert.AreEqual(1, publicErrorCount);
                Assert.AreEqual(1, internalErrorCount);
                Assert.AreEqual(1, publicReceiveFaCount);
                Assert.AreEqual(1, internalReceiveFaCount);
                Assert.AreEqual(1, publicManagedAccountsCount);
                Assert.AreEqual(1, internalManagedAccountsCount);
                Assert.AreEqual(1, publicFamilyCodesCount);
                Assert.AreEqual(1, internalFamilyCodesCount);
                Assert.AreEqual(19, replacement.RequestId);
                Assert.AreEqual(string.Empty, replacement.Message);
            });
        }

        [Test]
        public void PublicCallbackExceptionsDoNotSuppressInternalEvents()
        {
            using var client = new InteractiveBrokersClient(new EReaderMonitorSignal());
            var internalReceiveFaCount = 0;
            var internalPositionEndCount = 0;
            client.ReceiveFa += (_, _) => throw new InvalidOperationException("public receiveFA");
            client.InternalReceiveFa += (_, _) => internalReceiveFaCount++;
            client.PositionMultiEnd += (_, _) => throw new InvalidOperationException("public positionMultiEnd");
            client.PositionMultiEndWithRequestId += (_, _) => internalPositionEndCount++;

            Assert.Throws<InvalidOperationException>(() => client.receiveFA(1, "<ListOfGroups />"));
            Assert.Throws<InvalidOperationException>(() => client.positionMultiEnd(18));

            Assert.Multiple(() =>
            {
                Assert.AreEqual(1, internalReceiveFaCount);
                Assert.AreEqual(1, internalPositionEndCount);
            });
        }

        [Test]
        public void PortfolioCallbacksPreserveExactQuantityAndLegacyConversion()
        {
            using var client = new InteractiveBrokersClient(new EReaderMonitorSignal());
            UpdatePortfolioEventArgs update = null;
            client.UpdatePortfolio += (_, args) => update = args;

            client.updatePortfolio(new Contract(), 1.25m, 2, 3, 4, 5, 6, "DU123");

            Assert.Multiple(() =>
            {
                Assert.AreEqual(1, update.Position);
                Assert.AreEqual(1.25m, update.PositionQuantity);
            });
            Assert.Throws<OverflowException>(() =>
                new UpdatePortfolioEventArgs(new Contract(), decimal.MaxValue, 0, 0, 0, 0, 0, "DU123"));
        }
    }
}
