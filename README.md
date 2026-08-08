![header-cheetah](https://user-images.githubusercontent.com/79997186/184224088-de4f3003-0c22-4a17-8cc7-b341b8e5b55d.png)

&nbsp;
&nbsp;
&nbsp;

## Introduction

This repository hosts the Interactive Brokers (IB) Brokerage Plugin Integration with the QuantConnect LEAN Algorithmic Trading Engine. LEAN is a brokerage agnostic operating system for quantitative finance. Thanks to open-source plugins such as this [LEAN](https://github.com/QuantConnect/Lean) can route strategies to almost any market.

[LEAN](https://github.com/QuantConnect/Lean) is maintained primarily by [QuantConnect](https://www.quantconnect.com), a US based technology company hosting a cloud algorithmic trading platform. QuantConnect has successfully hosted more than 200,000 live algorithms since 2015, and trades more than $1B volume per month.


### About Interactive Brokers

<p align="center">
<picture >
  <source media="(prefers-color-scheme: dark)" srcset="https://user-images.githubusercontent.com/79997186/188234310-4d3cf2e5-4a88-4f8b-abaf-1f4c8f5ae71e.png">
  <source media="(prefers-color-scheme: light)" srcset="https://user-images.githubusercontent.com/79997186/188234305-1924c76a-86c1-46cf-b9bf-b29f8449e97e.png">
  <img alt="introduction" width="40%">
</picture>
<p>

IB was founded by Thomas Peterffy in 1993 with the goal to "create technology to provide liquidity on better terms. Compete on price, speed, size, diversity of global products and advanced trading tools". IB provides access to trading Equities, ETFs, Options, Futures, Future Options, Forex, Gold, Warrants, Bonds, and Mutual Funds for clients in over [200 countries and territories](https://www.interactivebrokers.com/en/index.php?f=7021) with no minimum deposit. IB also provides paper trading, a trading platform, and educational services.

For more information about the IB brokerage, see the [QuantConnect-IB Integration Page](https://www.quantconnect.com/docs/v2/cloud-platform/live-trading/brokerages/interactive-brokers).

## Using the Brokerage Plugin
  
### Deploying IB with VSCode User Interace

  You can deploy using a visual interface in the QuantConnect cloud. For instructions, see the [QuantConnect-IB Integration Page](https://www.quantconnect.com/docs/v2/cloud-platform/live-trading/brokerages/interactive-brokers). 
  
  ![deploy-ib](https://user-images.githubusercontent.com/38889814/207988504-86a110b9-dc74-4d8a-83ac-e0f0572d413f.gif) 

  In the QuantConnect Cloud Platform, you can harness the QuantConnect Live Data Feed, the IB Live Data Feed, or both. For most users, this is substantially cheaper and easier than self-hosting.
  
### Deploying IB with LEAN CLI

Follow these steps to start local live trading with the IB brokerage:

1.  Open a terminal in your [organization workspace](https://www.quantconnect.com/docs/v2/lean-cli/initialization/organization-workspaces).
2.  Run `lean live "<projectName>"` to start a live deployment wizard for the project in `./<projectName>` and then enter the brokerage number.

	```
    $ lean live "My Project"
    Select a brokerage:
    1) Paper Trading
    2) Interactive Brokers
    3) Tradier
    4) OANDA
    5) Bitfinex
    6) Coinbase Pro
    7) Binance
    8) Zerodha
    9) Samco
    10) Terminal Link
    11) Atreyu
    12) Trading Technologies
    13) Kraken
    14) FTX 
    Enter an option: 
	```


3.  Enter the number of the organization that has a subscription for the IB module. 

    ```
    $ lean live "My Project"
    Select the organization with the Interactive Brokers module subscription:
    1) Organization 1
    2) Organization 2
    3) Organization 3
    Enter an option: 1
    ```

4.  Set up IB Key Security via IBKR Mobile. For instructions, see [IB Key Security via IBKR Mobile](https://guides.interactivebrokers.com/iphone/log_in/ibkey.htm?tocpath=IB%20Key%20Security%20Protocol%7C_____0) on the IB website.

5.  Go back to the terminal and enter your IB username, account id, and password.

    ```
    $ lean live "My Project"
    Username: trader777
    Account id: DU1234567
    Account password: ****************
    ```

6.  Enter the number of the data feed to use and then follow the steps required for the data connection.

    ```
    $ lean live "My Project"
    Select a data feed:
    1) Interactive Brokers
    2) Tradier
    3) Oanda
    4) Bitfinex
    5) Coinbase Pro
    6) Binance
    7) Zerodha
    8) Samco
    9) Terminal Link
    10) Trading Technologies
    11) Kraken
    12) FTX
    13) IQFeed
    14) Polygon Data Feed
    15) Custom data only
    To enter multiple options, separate them with comma.:
    ```

    If you select IQFeed, see [IQFeed](https://www.quantconnect.com/docs/v2/lean-cli/live-trading/data-providers/iqfeed) for set up instructions.  
    If you select Polygon Data Feed, see [Polygon](https://www.quantconnect.com/docs/v2/lean-cli/live-trading/data-providers/polygon) for set up instructions.

7.  Enter whether you want to enable delayed market data.

    ```
    $ lean live "My Project"
    Enable delayed market data? [yes/no]: 
    ```

    This property configures the behavior when your algorithm attempts to subscribe to market data for which you don't have a market data subscription on Interactive Brokers. When enabled, your algorithm continues running using delayed market data. When disabled, live trading will stop and LEAN will shut down.

8.  View the result in the `<projectName>/live/<timestamp>` directory. Results are stored in real-time in JSON format. You can save results to a different directory by providing the `--output <path>` option in step 2.

If you already have a live environment configured in your Lean configuration file, you can skip the interactive wizard by providing the `--environment <value>` option in step 2. The value of this option must be the name of an environment which has `live-mode` set to `true`.


## Account Types

The IB API does not support the IBKR LITE plan. You need an IBKR PRO plan. Individual and Financial Advisor (FA) accounts are available. IB supports cash and margin accounts.

### Financial Advisor Groups

LEAN supports IB's current unified Allocation Groups model, configured in TWS as **Use Account Groups with Allocation Methods**. The following settings are optional and default to an empty value or `false`:

| Setting | Description |
| --- | --- |
| `ib-financial-advisors-group-filter` | Limits the deployment to one FA group. Leave it empty for multi-group operation and select `FaGroup` on each group order. When unified groups are enabled, a nonempty filter is a strict boundary and LEAN rejects a group order whose explicit `FaGroup` does not match it. Direct managed-account orders remain a separate route. |
| `ib-financial-advisors-unified-groups-enabled` | Set to `true` only after confirming that TWS uses unified Allocation Groups. This enables unified-group account discovery, validation, configuration management, and account-state reconciliation. Legacy separate Profiles are not supported by these features. |
| `ib-financial-advisors-group-management-enabled` | Enables algorithm-initiated membership and saved-allocation updates for existing groups. This setting requires `ib-financial-advisors-unified-groups-enabled=true`. Group creation and deletion are not supported. |

`ib-financial-advisors-unified-groups-enabled` is a deployment-time brokerage setting. LEAN passes it to IBAutomater before IB Gateway starts, so setting it during algorithm initialization is too late. When the setting is missing or `false`, IBAutomater retains its established behavior and deselects the recognized **Use Account Groups with Allocation Methods** checkbox. When it is `true`, IBAutomater leaves the recognized checkbox in its current state; it does not select, validate, or require the checkbox. Before launching with this setting enabled, confirm that the checkbox is already selected in the Gateway configuration.

**Account-group movement requires `ib-financial-advisors-group-management-enabled=true`, which implies unified groups. With an empty group filter, a managed account may move among discovered existing groups or between a group and the global unassigned state. With a configured filter, movement is limited to that configured group and the global unassigned state; cross-group and outside-group movement is rejected.**

`InteractiveBrokersOrderProperties.ExactFaPercentage` takes precedence over `FaPercentage` only when `ib-financial-advisors-unified-groups-enabled=true`. The legacy unified-disabled conversion ignores `ExactFaPercentage`, so set `FaPercentage` to a usable integer value whenever an order must remain compatible with that path.

Unified order-level `PctChange` instructions inherit the upstream placeholder-quantity fill accounting because IB computes their total quantity after submission. Paper TWS accepts and returns groups saved with `PctChange`; LEAN intentionally declines to infer that saved method because it cannot select the required placeholder-quantity fill path from an implicit instruction. With a `Ready` snapshot, a saved-`PctChange` group order whose `FaMethod` is blank is therefore rejected. Set `FaGroup`, `FaMethod = "PctChange"`, and the percentage explicitly; that route is supported whether the group is saved with `PctChange`, `ContractsOrShares`, `Ratio`, `Percent`, `NetLiq`, `AvailableEquity`, or `Equal`. Recovered `PctChange` orders retain upstream's zero-wire-quantity limitation, so their original aggregate parent quantity cannot be reconstructed from `TotalQuantity`. Use a saved `Percent` or `Ratio` allocation with an empty order-level `FaMethod` when exact decimal fill accounting and terminal-status reconciliation are required.

The supported saved user-specified allocation methods are `ContractsOrShares`, `Ratio`, and `Percent`. Saved `ContractsOrShares` child values may be fractional, but their total must be a valid parent quantity for the symbol's lot size; for a lot size of one, `12.5 + 7.5 = 20` is valid. TWS may display `Equal` as “Equal Quantity”; LEAN uses IB's `Equal` wire value and normalizes the legacy `EqualQuantity` spelling to it. `MonetaryAmount` group management and execution are not supported.

Ready-gated saved-method and allocation-vector validation for placements and updates deliberately fails open when the latest brokerage snapshot is unavailable, stale, reconnecting, or does not contain the requested group, preserving existing order behavior while IB remains the final authority. State-independent route and deployment-filter checks and the active-mutation gate continue to apply; new group-order placements also fail closed while a known malformed or unsupported topology is latched. Group configuration writes are stricter: they require ready, version-matched state and readback verification.

Snapshot discovery is read-only and publishes structurally valid groups even when their saved allocation method cannot be executed or managed by LEAN. `IsComplete` means all managed accounts and groups were discovered and all eligible managed-account financial state was collected; it does not mean every group is executable.

The C# and Python `FinancialAdvisorGroupAssignmentAlgorithm` samples demonstrate alias-driven group movement. Configure `fa-alias-pattern` with a case-insensitive regular expression, `fa-target-group` with an existing destination group, and `fa-cash-change-threshold` with the desired account-value threshold. Set `fa-allocation-value` to a non-negative value for `ContractsOrShares`, a positive value for `Ratio`, a positive `Percent` value below 100 when the destination already has members, or exactly 100 when the Percent destination is empty. `Equal`, `NetLiq`, and `AvailableEquity` take no value; saved `PctChange` and other methods are not supported by this sample. Saved-method support is checked even when a candidate already belongs to the destination, before any source-group removal is requested. Setting `fa-target-group` to an empty string requests removal of each matching managed account from all groups, but the samples skip alias-matched candidates whose assignment would remove the final member of any source group. The samples serialize assignments, poll their immutable results, and obtain a confirming snapshot generation before reevaluating membership after a cash or net-liquidation change. A broker-rejected assignment may be retried after a newer authoritative snapshot; the samples do not permanently suppress that candidate.

The C# and Python `FinancialAdvisorUnifiedGroupsDemoAlgorithm` samples require `ib-financial-advisors-unified-groups-enabled=true` with `ib-financial-advisors-group-filter` empty, because terminal reconciliation retains the original group members as additional account scope. In live mode they submit an explicit one-unit aggregate `MarketOrder`; they do not size the selected group from the FA master's aggregate portfolio. A synchronous submission exception is treated as an indeterminate broker outcome: automatic resubmission is disabled, the operator is told to verify TWS, and the exception is rethrown to fail closed against duplicate parent orders. The samples expect a group saved with a computed method (`Equal`, `NetLiq`, or `AvailableEquity`) or with `Ratio`/`Percent`; they do not submit orders to a group whose saved method is `ContractsOrShares`. That method requires updating the group's complete saved allocation vector, confirming the readback, and then submitting an aggregate parent whose exact quantity matches the vector's total. `FinancialAdvisorGroupAssignmentAlgorithm` demonstrates the asynchronous membership-mutation/readback state machine only; it does not replace a group's saved allocation vector.

Identifiers in common account snapshots are compared case-insensitively. Mutation requests should reuse the account and group spelling supplied by the brokerage provider so the provider's canonical spelling reaches TWS. Those lookup semantics do not normalize spelling or identifier placement in the raw group-configuration hash.

If an Allocation Group name case-insensitively matches a managed-account identifier, snapshot collection remains supported but skips the ambiguous group-scoped positions request. Members not already covered by another selected group use per-account position requests. Renaming the group so the identifiers no longer collide restores batched group-position collection.

#### Snapshot Freshness

The Financial Advisor service has no brokerage-owned periodic refresh cadence by design. The algorithm owns freshness policy and schedules `RequestBrokerageAccountSnapshotRefresh` calls. A practical starting point is a scoped group refresh about every 60 seconds and a complete account-state refresh about every 180 seconds, adjusted for the strategy and IB pacing budget. As an example of algorithm-owned policy, the C# and Python assignment and unified-group samples schedule a refresh intent every 90 seconds and normally request two scoped group refreshes followed by one complete-discovery refresh. State, order, and reconciliation gates defer an intent; after a provider rejection, the assignment samples retain the intent for retry, while the unified-order samples log the rejection and consume that scheduled tick. Those FA v2 samples use minute-resolution data and conservatively refuse to act on a snapshot older than five minutes; production algorithms should choose a maximum age appropriate to their strategy.

Before accepted refresh work proceeds, the provider publishes `Refreshing` without advancing `Generation`; a coalesced request joins previously accepted work. A fast request may publish `Ready`, `Failed`, or `Stale` before the caller's next read, so algorithms must not require observing `Refreshing`. Enforce an algorithm-owned polling timeout, handle terminal non-Ready status, and require a later-generation `Ready` snapshot before treating the request as successful.

A disconnect publishes `Stale`. After a confirmed reconnect, the brokerage issues one coalesced refresh using the most recently accepted or coalesced algorithm-requested scope, but only if the service session has prior accepted snapshot demand. A request that returned `false` is not remembered. This reconnect recovery is one request, not a cadence; an algorithm that never uses snapshots causes no automatic snapshot traffic. Before acting, check status and completeness and enforce the strategy's maximum age using `AsOfUtc` (snapshot publication), `CollectionStartedUtc` (collection start), and `LastSuccessfulUpdateUtc` (last successful refresh, preserved by later status and `Stale` publications).

For discovered Allocation Groups, snapshot collection reduces IB message traffic by opening one transient, named-group `reqAccountSummary` subscription at a time. Each request combines `AccountType`, `NetLiquidation`, `TotalCashValue`, `AvailableFunds`, `ExcessLiquidity`, `BuyingPower`, `AccountReady`, `$LEDGER`, and `$LEDGER:ALL`, waits for the matching end callback, and then cancels the subscription. LEAN infers each group's base currency from the currency-bearing scalar rows, with `RealCurrency` used only as optional corroboration; there is no USD assumption or base-currency setting.

Every returned member of an overlapping group is structurally and base-currency validated, and only validated required scalar and cash fields for unresolved candidate members enter new builders. If a group request or validation fails, LEAN discards that group's provisional summary data and falls back to the existing exact per-account `reqAccountUpdatesMulti` path for every in-scope member, including members provisionally resolved through an earlier overlapping group. Fallback triggers include incomplete membership or required rows, readiness that is not explicitly true, mixed or ambiguous base currencies, disagreeing child cash rows, disagreeing aggregate `BASE` and concrete-base-currency cash rows, and any nonzero aggregate-ledger currency other than the inferred base. A non-base aggregate currency that nets to exactly zero is an accepted detector limitation, so summary-path currency composition is not guaranteed exact in that case. Accounts outside selected groups—including unassigned and explicitly retained additional accounts—always use the exact per-account path; rows attributed to nonmembers never enter a child account builder.

The ending topology reread still brackets both summary and fallback collection, so a membership or group-configuration change aborts publication. Cancellation is mandatory because IB permits only two account-summary subscriptions, but IB provides no positive cancellation acknowledgement. If the local cancellation socket write fails, LEAN keeps the already validated current-group result, disables further summary batching until the next physical reconnect, and completes unresolved accounts through the exact path. A late broker-side cancellation rejection after the matching end callback cannot always be correlated once the request has completed, so remote subscription release cannot be proven. These batching details do not change the `GetAccountSnapshot` or `RequestAccountSnapshotRefresh` signatures or their freshness contract; the fast path is subject to the accepted net-zero detector limitation described above.

The brokerage no longer opens an `AccountType` summary for `group="All"` on a blank-filter FA connection. Non-FA connections retain the upstream startup request, and legacy FA connections retain it when a named group filter makes the request bounded. Unified FA mode relies on its transient named-group requests and does not attach the brokerage's private per-row summary trace handler; public `InteractiveBrokersBrokerage.Client.AccountSummary` subscribers still receive those rows.

The first successful `Ready` snapshot on each physical connection emits one compact `FA snapshot ready` trace. It reports the accessible managed-account count (including the primary but excluding its aggregate `<primary>A` identifier), discovered group names and authoritative member counts, collected and outside-group account counts, named-summary and exact-fallback path counts, completeness, and observed account types. Group details are sorted and capped at 50 entries with an omitted count. Normal refreshes and logical reconnects do not repeat the trace; the first `Ready` publication after a confirmed physical reconnect does.

If an unkeyed FA request times out, or its socket write fails after authorization, the unkeyed channel remains poisoned until a physical `ConnectionClosed`/`NextValidId` reconnect boundary. Logical connectivity-restored notifications do not clear that ambiguity.

Public subscribers to events exposed through `InteractiveBrokersBrokerage.Client` share IB's single-threaded message pump and must not block. A blocking subscriber can starve IB message delivery for the process, including callbacks awaited by the FA service's own pending requests; for an unkeyed FA request, the resulting timeout can poison the service until a physical reconnect.

IB does not correlate `reqManagedAccts`, `requestFA`, or `reqFamilyCodes` callbacks with request IDs. Do not issue those methods through the public `InteractiveBrokersBrokerage.Client` while the FA account-state service is collecting or reconciling state: a concurrent foreign request can cross-satisfy a wire-sent service request. The one-writer-per-TWS-session operating model applies to these unkeyed discovery calls as well.

During collection, LEAN reads managed accounts, FA groups, account aliases, and family codes before collecting per-account data, then re-reads all four topology sources afterward. It compares canonical group-configuration and complete-membership hashes and aborts publication if either comparison changes. The group-configuration hash ignores insignificant XML whitespace, attribute ordering, recognized group/account ordering, recognized allocation-method spelling, and numeric amount formatting. It remains case-sensitive for XML names and non-normalized values, including identifier spelling, and it does not normalize whether an identifier appears in an attribute, child element, or direct text; a case-only raw-XML change can therefore abort a refresh. The membership hash treats identifier keys case-insensitively but treats alias and family-code values case-sensitively.

IB does not provide an atomic operation that checks open orders and replaces FA configuration. Operate one configuration/order writer for each TWS user/session. While a `replaceFA` operation or its reconciliation is in progress, do not submit manual group orders or make manual group edits from TWS, Client Portal, or another API client. LEAN blocks its own conflicting group operations, but it cannot prevent an external client from racing the replacement.

For `RequestBrokerageAccountGroupAssignment` and `RequestBrokerageAccountGroupAllocationUpdate`, `true` means the request was accepted for asynchronous processing while the algorithm is running. Do not request configuration mutations during `OnEndOfAlgorithm` or teardown: such a request may be accepted but is not guaranteed to reach the broker or publish a result.

While a Financial Advisor configuration mutation or its reconciliation is active, LEAN rejects every update to an FA group order as well as new FA group orders. An update can change the aggregate parent quantity while its saved allocation vector is being rewritten, so algorithms should cancel rather than update during this interval.

Before a configuration mutation, LEAN's open-order precondition examines only Financial Advisor group orders already known to LEAN through `IOrderProvider`. It does not discover orders or quiesce configuration and order writers in TWS, Client Portal, or other API clients. Operators are responsible for ensuring those external sources remain quiescent throughout the mutation and reconciliation window.

TWS rejects a group configuration that removes its final member. Add another managed account before moving the original final member, or manage group creation/deletion manually in TWS.

#### Reconciling Group Orders

Retain a fresh `Ready` snapshot before submitting a Financial Advisor group order, and track its aggregate parent order. When any terminal parent status arrives, capture the current snapshot `Generation` before requesting a group-scoped refresh. This includes canceled, invalid, and partially filled orders that later become terminal. Poll from `OnData` until the snapshot is `Ready` with a strictly newer generation, then diff its per-account positions against the retained pre-order snapshot.

The aggregate parent `OrderEvent` is the reconciliation trigger; the refreshed snapshot is the final authority for per-account results. After an `Invalid` parent, the samples permit their existing bounded automatic retry only when every original group member has zero target-symbol position change in the newer snapshot. Any nonzero member change—including offsetting changes whose group total is zero—disables automatic resubmission to avoid duplicating a partial allocation and instructs the operator to review TWS. This mechanism intentionally adds no child-execution event, execution-correlation queue, drop counter, or additional aggregate fill.

For sub-lot aggregate positions, LEAN's `Portfolio` and `SecurityHolding` APIs—including `Invested`, `TotalPortfolioValue`, and `Liquidate`—apply the security's lot-size semantics. Use `BrokerageAccountSnapshot` as the authority for per-account Financial Advisor decisions; do not infer those account-level positions from the aggregate portfolio.

## Order Types and Asset Classes

The following table describes the order types that IB supports. For specific details about each order type, refer to the IB documentation.

| Order Type  | IB Documentation Page |
| ----------- | ----------- |
| `MarketOrder` | [Market Orders](https://www.interactivebrokers.com/en/index.php?f=602) |
| `LimitOrder` | [Limit Orders](https://www.interactivebrokers.com/en/index.php?f=593) |
| `LimitIfTouchedOrder` | [Limit if Touched Orders](https://www.interactivebrokers.com/en/index.php?f=592) |
| `StopMarketOrder` | [Stop Orders](https://www.interactivebrokers.com/en/index.php?f=609) |
| `StopLimitOrder` | [Stop-Limit Orders](https://www.interactivebrokers.com/en/index.php?f=608) |
| `TrailingStopOrder` | [Trailing Stop Orders](https://www.interactivebrokers.com/en/trading/orders/trailing-stops.php) |
| `MarketOnOpenOrder` | [Market-on-Open (MOO) Orders](https://www.interactivebrokers.com/en/index.php?f=598) |
| `MarketOnCloseOrder` | [Market-on-Close (MOC) Orders](https://www.interactivebrokers.com/en/index.php?f=599) |
| `ExerciseOption` | [Options Exercise](https://www.interactivebrokers.ca/en/trading/exerciseCloseout.php) |


## Downloading Data

For local deployment, the algorithm needs to download the following datasets:

- [US Equities Security Master](https://www.quantconnect.com/datasets/quantconnect-us-equity-security-master) provided by QuantConnect
- [US Equities](https://www.quantconnect.com/datasets/algoseek-us-equities)
- [US Coarse Universe](https://www.quantconnect.com/datasets/quantconnect-us-coarse-universe-constituents)
- [US Equity Options](https://www.quantconnect.com/datasets/algoseek-us-equity-options)
- [FOREX Data](https://www.quantconnect.com/datasets/oanda-forex)
- [US Futures Security Master](https://www.quantconnect.com/datasets/quantconnect-us-futures-security-master)
- [US Futures](https://www.quantconnect.com/datasets/algoseek-us-futures)
- [US Future Options](https://www.quantconnect.com/datasets/algoseek-us-future-options)
- [US Cash Indices](https://www.quantconnect.com/datasets/tickdata-us-cash-indices)
- [US Index Options](https://www.quantconnect.com/datasets/algoseek-us-index-options)

## Brokerage Model

Lean models the brokerage behavior for backtesting purposes. The margin model is used in live trading to avoid placing orders that will be rejected due to insufficient buying power.

You can set the Brokerage Model with the following statements

    SetBrokerageModel(BrokerageName.InteractiveBrokersBrokerage, AccountType.Cash);
    SetBrokerageModel(BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);

[Read Documentation](https://www.quantconnect.com/docs/v2/cloud-platform/live-trading/brokerages/interactive-brokers)

### Fees

We model the order fees of IB for each asset class. For information about each asset class, see [Fees](https://www.quantconnect.com/docs/v2/cloud-platform/live-trading/brokerages/interactive-brokers#07-Fees).

### Margin

We model buying power and margin calls to ensure your algorithm stays within the margin requirements.

[Read Documentation](https://www.quantconnect.com/docs/v2/cloud-platform/live-trading/brokerages/interactive-brokers)

#### Buying Power

In the US, IB allows up to 2x leverage on Equity trades for margin accounts. In other countries, IB may offer different amounts of leverage. To figure out how much leverage you can access, check with your local legislation or contact an IB representative. We model the US version of IB leverage by default.

#### Margin Calls

Regulation T margin rules apply. When the amount of margin remaining in your portfolio drops below 5% of the total portfolio value, you receive a [warning](https://www.quantconnect.com/docs/v2/writing-algorithms/reality-modeling/margin-calls#08-Monitor-Margin-Call-Events). When the amount of margin remaining in your portfolio drops to zero or goes negative, the portfolio sorts the generated margin call orders by their unrealized profit and executes each order synchronously until your portfolio is within the margin requirements.

### Slippage

Orders through IB do not experience slippage in backtests. In paper trading and live trading, your orders may experience slippage.

### Fills

We fill market orders immediately and completely in backtests. In live trading, if the quantity of your market orders exceeds the quantity available at the top of the order book, your orders are filled according to what is available in the order book.

### Settlements

If you trade with a margin account, trades settle immediately. If you trade with a cash account, Equity trades settle 3 days after the transaction date (T+3) and Option trades settle on the business day following the transaction (T+1).


### Deposits and Withdraws

You can deposit and withdraw cash from your brokerage account while you run an algorithm that's connected to the account. We sync the algorithm's cash holdings with the cash holdings in your brokerage account every day at 7:45 AM Eastern Time (ET).


&nbsp;
&nbsp;
&nbsp;

![whats-lean](https://user-images.githubusercontent.com/79997186/184042682-2264a534-74f7-479e-9b88-72531661e35d.png)

&nbsp;
&nbsp;
&nbsp;

LEAN Engine is an open-source algorithmic trading engine built for easy strategy research, backtesting, and live trading. We integrate with common data providers and brokerages, so you can quickly deploy algorithmic trading strategies.

The core of the LEAN Engine is written in C#, but it operates seamlessly on Linux, Mac and Windows operating systems. To use it, you can write algorithms in Python 3.8 or C#. QuantConnect maintains the LEAN project and uses it to drive the web-based algorithmic trading platform on the website.

## Contributions

Contributions are warmly very welcomed but we ask you to read the existing code to see how it is formatted, commented and ensure contributions match the existing style. All code submissions must include accompanying tests. Please see the [contributor guide lines](https://github.com/QuantConnect/Lean/blob/master/CONTRIBUTING.md).

## Code of Conduct

We ask that our users adhere to the community [code of conduct](https://www.quantconnect.com/codeofconduct) to ensure QuantConnect remains a safe, healthy environment for
high quality quantitative trading discussions.

## License Model

Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License. You
may obtain a copy of the License at

<http://www.apache.org/licenses/LICENSE-2.0>

Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language
governing permissions and limitations under the License.
