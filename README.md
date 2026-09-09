# USDC for BTCPay Server

A separately installable, non-custodial BTCPay Server plugin for native USDC on Ethereum. It does not require the USDt plugin. Both plugins can be installed: USDC uses its own assembly, controllers, views, settings, payment methods, and payment identifiers.

This is an initial implementation for review, not a published plugin-store release.

The plugin adds USDC to BTCPay invoices and checkout. Customers pay directly to addresses you control; BTCPay detects transfers and handles partial payments, overpayments, confirmations, and invoice status updates. It does not create a wallet, exchange USDC, or move the received funds.

**Plan for one fresh receiving address per invoice.** Addresses are never recycled, even after an invoice expires. You manage the wallet, address supply, and any later movement of funds outside BTCPay.

## Start here

- **Try a payment:** [build and install](#build-and-load-in-a-development-server), then [configure a Sepolia test store](#configure-a-test-store).
- **Run automated tests locally:** follow [database and running-host tests](#database-and-running-host-tests), including the local-fork and USDt instructions for full coverage.
- **Operate on mainnet:** read [production setup](#production-setup), [payment lifecycle](#payment-lifecycle), and [operations and recovery](#operations-and-recovery).
- **Diagnose a problem:** see [troubleshooting](#troubleshooting) and [validation and limitations](#validation-and-limitations).

## Production payment node

**On mainnet, the plugin connects by default to `https://ethereum.publicnode.com`, a public Ethereum JSON-RPC endpoint.** It does not provision an Ethereum node. Payment detection depends on the configured endpoint's availability and finalized history.

To use your own Ethereum node or another RPC provider, set the endpoint in **BTCPay's USDC server settings**, or set `USDC_ETHEREUM_JSONRPC_URI` in BTCPay configuration. **Saved server settings take precedence.** The endpoint must support finalized-block and transfer-log queries, plus the validation methods listed in [store setup](#configure-a-test-store). Keep authenticated endpoint URLs in private server configuration.

The plugin monitors incoming USDC transfers; private keys remain in your external wallet, and the plugin does not send funds. On testnet/regtest, the default endpoint is `https://sepolia.publicnode.com`, overridden with `USDC_SEPOLIA_JSONRPC_URI` or the USDC server settings.

## Networks

| BTCPay network | Payment method | EVM chain ID | Native USDC contract |
| --- | --- | --- | --- |
| Mainnet | `USDC-ETHEREUM` | 1 | `0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48` |
| Testnet or regtest | `USDC-SEPOLIA` | 11155111 | `0x1c7d4b196cb0c7b01d743fbc6116a902379c7238` |

Both use six decimals. Contracts are fixed to [Circle's native USDC deployments](https://developers.circle.com/stablecoins/usdc-contract-addresses). Regtest deliberately maps to public Sepolia, following the reference plugin's network convention; it is not a local EVM chain. Polygon, Base, Solana, TRON, bridged USDC.e, and other tokens are outside this initial scope. Ethereum reuses the reference's ERC-20 engine and provides a matching Circle-issued test token on Sepolia.

## Build and load in a development server

Use .NET SDK 10, Git, and a BTCPay development instance with PostgreSQL. The pinned BTCPay submodule is the compatibility target; the plugin declares BTCPay >=2.3.7, matching the USDt reference. Earlier host releases have not been tested.

You need repository access while this project is private. Clone it and run the following commands from the repository root. Docker is needed for the database and local-fork tests below; internet access is needed for dependency restore and public RPC queries.

```sh
git clone --recurse-submodules https://github.com/rockstardev/BTCPayServer.Plugins.USDC.git
cd BTCPayServer.Plugins.USDC
git checkout feat/usdc-ethereum
```

The implementation currently lives on `feat/usdc-ethereum` in a draft PR. Use the reviewed commit you intend to evaluate; do not assume the default branch contains it.

```sh
git submodule update --init --recursive
dotnet build BTCPayServer.Plugins.USDC.sln -c Release -m:1 -p:StaticWebAssetsEnabled=false
dotnet test BTCPayServer.Plugins.USDC.Tests -c Release -m:1 -p:StaticWebAssetsEnabled=false
dotnet publish Plugins/BTCPayServer.Plugins.USDC -c Release -p:StaticWebAssetsEnabled=false -o artifacts/usdc
dotnet run --project submodules/btcpayserver/BTCPayServer.PluginPacker -c Release -- artifacts/usdc BTCPayServer.Plugins.USDC artifacts/packed
```

The packer creates `artifacts/packed/BTCPayServer.Plugins.USDC/0.1.0.0/BTCPayServer.Plugins.USDC.btcpay` for manual installation through the test server's plugin administration page, followed by a restart. For an isolated development host, copy the contents of `artifacts/usdc` into `<plugindir>/BTCPayServer.Plugins.USDC` and pass that parent directory as BTCPay's `plugindir` setting. The host integration tests use this installation path with a Release build. `DEBUG_PLUGINS` is an alternative only when the **host itself** was compiled with DEBUG; Release hosts ignore it. The repository follows the standalone Razor plugin layout used by [SamRockProtocol](https://github.com/rockstardev/SamRockProtocol). Distribution through Plugin Builder remains a release task. No deployment or automatic publishing workflow is included.

After restart, confirm that **USDC** appears in the server navigation and store wallet navigation. Installation requires server administrator access; configuring a store requires permission to modify that store. Use a separate BTCPay test instance and database rather than changing an existing production server's network. The automated host tests below start their own temporary BTCPay instances and need no manually running BTCPay server.

## Configure a test store

1. Run BTCPay on testnet/regtest. Verify that the USDC menu says **Sepolia (testnet)**.
2. In server settings, configure a reliable Sepolia JSON-RPC endpoint under USDC. The endpoint must support `eth_chainId`, `eth_call`, `eth_getLogs`, `eth_syncing`, and `eth_getBlockByNumber` with the `finalized` tag. The public default is for development. RPC chain ID and contract decimals are checked before accepting logs and reporting availability.
3. Add fresh public receiving addresses under the store's USDC wallet and enable it. Keep keys in an external wallet; the plugin never needs a seed or private key. Each address is permanently assigned to one invoice for that network, across all stores on this server. Replenish the pool as addresses are used. Do not share a pool with another BTCPay installation.
4. The default rate rules assume `USDC_USD = 1` and convert via USD/BTC. This is an explicit pricing assumption, not a live peg oracle; customize store rates if required.
5. Allow at least an hour of invoice monitoring after expiration for finalized-block detection. Use a long invoice validity while testing. Generate an invoice, pay with Sepolia test USDC and test ETH for gas, and wait for finalized detection plus the chosen confirmation policy. Obtain test tokens through [Circle's faucet](https://faucet.circle.com/); never send mainnet funds for testing.

Endpoint overrides can also be provided through BTCPay configuration as `USDC_ETHEREUM_JSONRPC_URI` or `USDC_SEPOLIA_JSONRPC_URI`. Saved server settings take precedence. Keep authenticated endpoint URLs in private server configuration, never source control, screenshots, or issue reports.

Saving a new RPC endpoint through the server settings form switches the running listener without a BTCPay restart. The listener validates the network and token decimals before initializing its scan cursor, and resumes from the existing cursor when changing providers for the same chain. The saved endpoint is restored after a host restart.

### Try checkout and verify a payment

1. Generate unused Sepolia receiving addresses in a wallet you control. Add their public `0x...` addresses in the store's USDC settings; multiple addresses can be separated by commas or newlines. Enable the payment method and save. Never enter a private key or seed phrase.
2. Keep **EIP-681 token transfer URI** as the payment-link format if your payer wallet supports it. With **Address only**, the payer must select Sepolia, select native USDC, and enter the displayed amount manually. Custom templates support `{to}`, `{amount}`, `{amountUnits}`, `{smartContractAddress}`, and `{chainId}`; `amountUnits` is the integer amount in millionths of USDC.
3. Create a small invoice in BTCPay and open its checkout. Choose **USDC on Sepolia (testnet)**. Check the destination, token, network, and amount before sending from a separate test wallet holding test USDC and test ETH for gas. Do not send to the USDC contract address itself.
4. Inspect the invoice's payment details and the transaction in the Sepolia explorer. Expect a delay until Ethereum finalizes the transfer, followed by the configured confirmation threshold. Refresh the invoice to verify its status; a successful wallet transaction alone does not mean BTCPay has settled it.
5. Optionally repeat with partial payments using a new invoice. The payments should accumulate toward its amount. Every new invoice needs another unused receiving address.

A BTCPay server running on your laptop can still query public Sepolia. This is different from the **local-fork automated tests**, which execute transactions only inside Anvil on loopback. A plain, unforked Anvil chain is not a supported store network: the plugin fixes the chain ID and native USDC contract for Ethereum or Sepolia.

## Production setup

This private implementation is ready for review, not a published production release. The following describes mainnet configuration; this project has not been deployed or tested with real funds as part of its acceptance testing.

1. **Prepare the host.** Use a maintained BTCPay installation with PostgreSQL, HTTPS, and server administrator access. Validate compatibility with your exact host version in staging; the declared minimum version is not evidence that every later release was tested. Back up the BTCPay database and configuration before installing or upgrading.
2. **Install the reviewed package.** Build/package the selected commit as above, install it through plugin administration, and restart BTCPay. For a containerized host, the package/plugin directory and configuration must be persisted through your host's normal deployment mechanism; a copy into an ephemeral container is lost on replacement. USDC can coexist with USDt without depending on it.
3. **Confirm mainnet.** The USDC menu must say **USDC on Ethereum**, with chain ID `1` and the contract in the network table. Network selection follows BTCPay's network setting; an RPC URL does not change it. Do not repurpose a test database or its address pool for mainnet.
4. **Configure RPC.** Open the server's USDC settings and enter an Ethereum mainnet JSON-RPC URL. If unset, the public default above is used. Ensure the BTCPay process can reach the endpoint and that it supports finalized history, log queries, and the validation methods listed above. The plugin trusts this provider's responses; availability, request limits, and history retention affect payment detection.
5. **Configure each store.** Add fresh mainnet addresses from your external wallet, enable USDC, select a compatible payment-link format, and save. Keep enough unused addresses for expected invoice volume. Check store exchange-rate rules, invoice expiration, monitoring duration, and confirmation policy before offering checkout to customers.
6. **Check readiness.** Confirm that the USDC node status is healthy, balances load, and a newly created invoice offers the correct token/network/destination. Continue monitoring the host and its RPC after activation. This guide does not automatically send a mainnet transaction.

### RPC configuration reference

| Setting | Ethereum mainnet | Sepolia testnet/regtest |
| --- | --- | --- |
| Default JSON-RPC URL | `https://ethereum.publicnode.com` | `https://sepolia.publicnode.com` |
| BTCPay configuration key | `USDC_ETHEREUM_JSONRPC_URI` | `USDC_SEPOLIA_JSONRPC_URI` |
| Environment variable for the standard BTCPay host | `BTCPAY_USDC_ETHEREUM_JSONRPC_URI` | `BTCPAY_USDC_SEPOLIA_JSONRPC_URI` |
| Server settings path | `/server/evmUSDClike/USDC-ETHEREUM` | `/server/evmUSDClike/USDC-SEPOLIA` |

BTCPay's standard host strips the `BTCPAY_` environment prefix. Set environment variables on the BTCPay process/container, not just on a build shell. For a Docker deployment, explicitly pass the variable into the BTCPay container. Keep provider credentials in private deployment configuration. The `USDC_TEST_*` variables below belong only to the test harness and do not configure a production listener.

Precedence is **saved USDC server settings > BTCPay configuration > built-in default**. Clear and save the server's RPC field to return to the configured/default URL. Server-form changes apply live and survive restart; restart the host after changing its environment configuration. Changing providers preserves the scan cursor for the same chain and validates the replacement endpoint before continuing.

## Payment lifecycle

The handler atomically claims an unused address in PostgreSQL before exposing it, sets USDC as the currency with six decimals, and produces an EIP-681 ERC-20 transfer URI. A database transaction and unique address key prevent concurrent invoices or stores from receiving the same address. Retrying configuration for the same invoice returns its original address. Claims survive restarts, invoice expiration, settlement, and deletion; an ERC-20 transfer has no invoice identifier, so automatic address recycling would misattribute late payments. Even an unsuccessful invoice creation can consume an address. Standard, address-only, and custom-template links follow the reference's checkout conventions.

The listener starts after store activation, persists a separate scan height, and indexes finalized Ethereum blocks. It queries Transfer logs for the fixed contract and invoice destinations in batches of 20. It verifies returned contract, block hash/height, recipient, removal flag, and log index. Zero-value transfers are ignored. Each positive log becomes a separate BTCPay payment with exact decimal amounts. Transaction hash plus log index, currency, and chain identifies the payment, allowing repeat scans without changing its ID and preventing collisions with USDt.

BTCPay handles partial payments, overpayments, and invoice state transitions through its existing PaymentService. Block timestamps preserve the timing of late payments; deposits in blocks timestamped before invoice creation are ignored, comparing at Ethereum's one-second precision. Missing or invalid block timestamps stop indexing until the node recovers. Expired invoices remain tracked until monitoring expiration. Confirmation policies match the reference: HighSpeed 2, MediumSpeed 6, LowMediumSpeed 12, LowSpeed 20 **subsequent indexed finalized blocks**. The inclusion block starts at zero. Finalization adds latency; an RPC without finality support fails closed rather than falling back to latest blocks.

## Database and running-host tests

Run commands from the repository root. Shell examples use POSIX syntax unless marked PowerShell. The basic `dotnet test` command above runs the tests available without external services and explicitly skips environment-dependent tests. **A passing run with skips is not full acceptance coverage.**

| Coverage | Required setup |
| --- | --- |
| Amounts, decimals, payment links, log filtering, confirmations | No database, wallet, or node |
| Durable address allocation and running BTCPay payment lifecycle | Disposable PostgreSQL plus a fresh publish to `artifacts/usdc` |
| USDt and USDC in the same host | PostgreSQL plus published USDt and USDC plugins |
| Transfers against canonical USDC contracts | Local Anvil, internet access for read-only Ethereum/Sepolia forks |

Start a disposable PostgreSQL service, restricted to loopback:

```sh
docker run --detach --name usdc-test-postgres -p 127.0.0.1:45432:5432 -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=usdc_tests postgres:16-alpine
export USDC_TEST_POSTGRES='Host=127.0.0.1;Port=45432;Database=usdc_tests;Username=postgres'
dotnet publish Plugins/BTCPayServer.Plugins.USDC -c Release -m:1 -p:StaticWebAssetsEnabled=false -o artifacts/usdc
dotnet test BTCPayServer.Plugins.USDC.Tests -c Release -m:1 -p:StaticWebAssetsEnabled=false --logger trx
```

In PowerShell use `$env:USDC_TEST_POSTGRES = '...'` instead of `export`. Never point tests at a real BTCPay database: allocator tests apply host migrations, and host tests create and drop isolated databases using this account's database-creation permission. The trust authentication above is only for this local disposable service. Stop and remove that named test container when finished.

To include USDt coexistence tests, publish `BTCPayServer.Plugins.USDt` from reference commit `cacb4e8c3d2672ae966853bc833af93a98e4c0b1` and set `USDC_TEST_USDT_PATH` to its absolute publish directory. CI provides PostgreSQL, publishes both plugins, and runs these tests. Without the relevant environment variables, database/coexistence tests explicitly report skipped.

```sh
git clone --recurse-submodules https://github.com/btcpayserver-tether/BTCPayServer.Plugins.USDt.git artifacts/reference-usdt
git -C artifacts/reference-usdt checkout cacb4e8c3d2672ae966853bc833af93a98e4c0b1
git -C artifacts/reference-usdt submodule update --init --recursive
dotnet publish artifacts/reference-usdt/BTCPayServer.Plugins.USDt -c Release -m:1 -p:StaticWebAssetsEnabled=false -o artifacts/usdt
export USDC_TEST_USDT_PATH="$PWD/artifacts/usdt"
```

For canonical-contract transaction tests, start an isolated [Anvil development node](https://www.getfoundry.sh/anvil/index.html):

```sh
docker run --detach --name usdc-test-anvil -p 127.0.0.1:48545:8545 --entrypoint anvil ghcr.io/foundry-rs/foundry@sha256:0c00cb0bda1ab1b91c9a6bf60f4c76c09c1a8870824b6d4718afbabacf6f9a17 --host 0.0.0.0 --silent
export USDC_TEST_ANVIL_URL='http://127.0.0.1:48545'
dotnet test BTCPayServer.Plugins.USDC.Tests -c Release -m:1 -p:StaticWebAssetsEnabled=false --filter FullyQualifiedName~LocalForkTests --logger trx
```

This test resets that local node, forks current Ethereum and Sepolia state from public read-only RPC endpoints, and uses Anvil impersonation to mint test balances and execute transfers against the canonical USDC contracts **on the local forks only**. It requires internet access and available upstream state. It verifies receipts, recipient balances, six-decimal payment detection, finalized-head coverage, and stable replay IDs. All transaction/state-changing calls are restricted to an explicit `http://127.0.0.1` Anvil endpoint. No wallet keys or public-network transactions are needed. CI runs this test too; it is explicitly skipped when the Anvil environment variable is absent. Stop and remove the named test node when finished.

### Run the complete suite

With both containers running and all three `USDC_TEST_*` variables set in the same shell, republish USDC and run without a test filter:

```sh
dotnet publish Plugins/BTCPayServer.Plugins.USDC -c Release -m:1 -p:StaticWebAssetsEnabled=false -o artifacts/usdc
dotnet test BTCPayServer.Plugins.USDC.Tests -c Release -m:1 -p:StaticWebAssetsEnabled=false --logger trx
```

PowerShell equivalents for the environment setup are:

```powershell
$env:USDC_TEST_POSTGRES = 'Host=127.0.0.1;Port=45432;Database=usdc_tests;Username=postgres'
$env:USDC_TEST_USDT_PATH = (Resolve-Path artifacts/usdt).Path
$env:USDC_TEST_ANVIL_URL = 'http://127.0.0.1:48545'
```

Wait for PostgreSQL and Anvil to be ready before starting tests. TRX reports are written under `BTCPayServer.Plugins.USDC.Tests/TestResults`. The verified implementation commit `5e538c0220584228247bbd296699fa466ae1c0b0` passed **32 tests, zero failures, zero skips**, locally and in CI. This is evidence for that commit and setup, not a substitute for testing future changes. [The CI workflow](.github/workflows/build.yml) records the automated setup.

When finished, remove only the disposable containers created by these instructions:

```sh
docker rm -f usdc-test-postgres usdc-test-anvil
```

This destroys their temporary test state. Keep them separate from any server database or node you want to retain.

## Operations and recovery

- **Address supply:** check unused addresses regularly and add fresh ones before the pool runs out. Assignment is permanent across stores on the same server. Removing and re-adding an address does not release it, and a failed invoice creation can consume an address. Do not use the pool for unrelated payments or share it across separate BTCPay installations.
- **Funds:** payments remain at the receiving addresses. The plugin does not consolidate them or provide refunds/payouts. Any later outgoing transfer is your external wallet's responsibility and requires ETH for network fees. Back up wallet recovery material separately from BTCPay and never enter it in plugin settings.
- **Backups:** preserve the entire BTCPay database, including scan state and `USDC.Address.*` / `USDC.InvoiceAddress.*` claims, plus private server configuration. Do not delete claims or restore an older database and continue issuing invoices from previously exposed addresses. Reconcile payments and use fresh addresses when recovering from stale state.
- **RPC outages:** restore connectivity or change the provider through server settings. Normal restarts resume the persisted cursor; do not reset it to fix a connection error. If an outage exceeds an invoice's monitoring window, investigate its destination and transaction history and reconcile it manually.
- **Upgrades or removal:** test a new package against the target host in staging and retain a database backup. Disabling or removing the plugin interrupts detection; it does not prevent customers from sending to already issued addresses. Keep monitoring outstanding invoices when planning maintenance.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| No USDC menu after installation | Confirm the plugin files/package are installed on the actual host, restart BTCPay, and inspect startup logs for dependency/load errors. A Release host ignores `DEBUG_PLUGINS`. |
| Wrong network or no supported USDC method | Check BTCPay's mainnet/testnet/regtest setting. An RPC override cannot register another chain or token. |
| Node unavailable or balances show `N/A` | Check RPC connectivity from the BTCPay process, authentication, request limits, chain ID, six-decimal contract response, and finalized-block support. |
| A changed environment URL seems ignored | Saved USDC server settings take precedence. Clear the saved override if appropriate, pass the `BTCPAY_` variable into the host process, and restart after environment changes. |
| Invoice cannot obtain an address | Add fresh valid addresses and enable USDC. Previously assigned addresses remain unavailable even if removed and re-added. |
| Wallet says sent but invoice has no payment | Verify exact chain, native USDC contract, destination, and successful transfer log. Wait for finality and check RPC health and the invoice's monitoring window. A deposit predating invoice creation is ignored. |
| Payment detected but invoice not settled | Check whether the full amount has been paid and whether the confirmation threshold has elapsed. Thresholds count finalized blocks, not just latest-chain confirmations. |
| Old/late payment is missing after downtime | Review invoice monitoring expiration and the 49-day bootstrap lookback. Do not recycle the address or reset scan state; reconcile the payment manually if it is outside monitoring. |
| Database/coexistence/fork tests are skipped | Set the corresponding `USDC_TEST_*` variables in the shell running `dotnet test`. For full coverage, publish both plugins and start both disposable services. |
| Host test reports a published assembly mismatch | Republish USDC to `artifacts/usdc` from the same source/configuration being tested, then rerun. |
| Local-fork test cannot retrieve upstream state | Verify internet access and public RPC availability. The test needs upstream reads even though transactions stay local. |

When reporting a problem, include the plugin commit/version, BTCPay version, network, relevant redacted logs, and test output if applicable. Remove RPC credentials, wallet secrets, and private customer data. Do not infer successful settlement solely from a wallet screenshot.

## Validation and limitations

- Automated tests use an in-memory JSON-RPC transport, exercising real Nethereum filter construction and ERC-20 decoding without credentials or transactions. Tests cover partial/exact/overpayment amounts, one atomic unit, large amounts, culture invariance, batched logs, duplicate replay, wrong contract/recipient/block, removed logs, network/decimal mismatch, finality, checkout links, confirmation thresholds, and expiration tracking.
- PostgreSQL integration tests exercise concurrent allocation across service instances and stores. Running-host tests install the published plugin, configure a store through the authenticated API, reject invalid receiving addresses, create concurrent invoices across stores, render HTTP checkout, persist partial and multiple payments, reject duplicate and pre-invoice credit, and recover from RPC failure and listener restart. A full host restart also verifies persisted invoices and detection of late payments after expiration. Coexistence tests settle both USDt and USDC invoices at the same receiving address without cross-crediting the tokens. These host tests use synthetic HTTP RPC logs; the separate local-fork test executes actual EVM transfers against canonical Ethereum and Sepolia USDC bytecode/state. A funded public-Sepolia payment has not been sent.
- Address claims are part of the BTCPay database backup. Never delete the `USDC.Address.*` or `USDC.InvoiceAddress.*` settings to replenish a pool, and do not restore an older database while continuing to use previously exposed addresses. This initial private implementation has no migration from older prototype invoices that did not create durable claims; start with fresh addresses.
- No automatic sweeping, refunds, payouts, wallet generation, historical backfill from before initial activation, or recovery after catastrophic finalized-chain rollback. The node is trusted to report canonical finalized history. RPC outages or stalled finality delay detection.
- The inherited 49-day expired-invoice bootstrap lookback does not cover arbitrarily old monitoring windows. Keep monitoring windows within that bound.
- An RPC outage that lasts beyond an invoice's monitoring window can leave a payment undetected and require manual reconciliation. Addresses remain permanently claimed even when monitoring ends.
- NuGet reports existing advisories in the pinned BTCPay dependency graph (AngleSharp, SSH.NET, and Microsoft.Build.Tasks.Git). Host dependency upgrades belong in the BTCPay repository and were not silently substituted here.

## Provenance

Derived from the MIT-licensed [USDt plugin](https://github.com/btcpayserver-tether/BTCPayServer.Plugins.USDt) at `b930b5d90116a8da824f84327c3e2f1ae79d1fcf`. Its EVM lifecycle, settings, checkout, and monitoring code were adapted into a separate USDC namespace; unrelated TRON implementations were omitted. The original license is retained. The pinned BTCPay commit is `cea07e5e18528396d97368b43085f59e3e58e284`.
