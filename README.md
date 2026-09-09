# USDC for BTCPay Server

A separately installable, non-custodial BTCPay Server plugin for native USDC on Ethereum. It does not require the USDt plugin. Both plugins can be installed: USDC uses its own assembly, controllers, views, settings, payment methods, and payment identifiers.

This is an initial implementation for review, not a published plugin-store release.

## Networks

| BTCPay network | Payment method | EVM chain ID | Native USDC contract |
| --- | --- | --- | --- |
| Mainnet | `USDC-ETHEREUM` | 1 | `0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48` |
| Testnet or regtest | `USDC-SEPOLIA` | 11155111 | `0x1c7d4b196cb0c7b01d743fbc6116a902379c7238` |

Both use six decimals. Contracts are fixed to [Circle's native USDC deployments](https://developers.circle.com/stablecoins/usdc-contract-addresses). Regtest deliberately maps to public Sepolia, following the reference plugin's network convention; it is not a local EVM chain. Polygon, Base, Solana, TRON, bridged USDC.e, and other tokens are outside this initial scope. Ethereum reuses the reference's ERC-20 engine and provides a matching Circle-issued test token on Sepolia.

## Build and load in a development server

Use .NET SDK 10, Git, and a BTCPay development instance with PostgreSQL. The pinned BTCPay submodule is the compatibility target; the plugin declares BTCPay >=2.3.7, matching the USDt reference. Earlier host releases have not been tested.

```sh
git submodule update --init --recursive
dotnet build BTCPayServer.Plugins.USDC.sln -c Release -m:1 -p:StaticWebAssetsEnabled=false
dotnet test BTCPayServer.Plugins.USDC.Tests -c Release -m:1 -p:StaticWebAssetsEnabled=false
dotnet publish Plugins/BTCPayServer.Plugins.USDC -c Release -p:StaticWebAssetsEnabled=false -o artifacts/usdc
dotnet run --project submodules/btcpayserver/BTCPayServer.PluginPacker -c Release -- artifacts/usdc BTCPayServer.Plugins.USDC artifacts/packed
```

The packer creates `artifacts/packed/BTCPayServer.Plugins.USDC/0.1.0.0/BTCPayServer.Plugins.USDC.btcpay` for manual installation through the test server's plugin administration page, followed by a restart. For an isolated development host, copy the contents of `artifacts/usdc` into `<plugindir>/BTCPayServer.Plugins.USDC` and pass that parent directory as BTCPay's `plugindir` setting. The host integration tests use this installation path with a Release build. `DEBUG_PLUGINS` is an alternative only when the **host itself** was compiled with DEBUG; Release hosts ignore it. The repository follows the standalone Razor plugin layout used by [SamRockProtocol](https://github.com/rockstardev/SamRockProtocol). Distribution through Plugin Builder remains a release task. No deployment or automatic publishing workflow is included.

## Configure a test store

1. Run BTCPay on testnet/regtest. Verify that the USDC menu says **Sepolia (testnet)**.
2. In server settings, configure a reliable Sepolia JSON-RPC endpoint under USDC. The endpoint must support `eth_chainId`, `eth_call`, `eth_getLogs`, `eth_syncing`, and `eth_getBlockByNumber` with the `finalized` tag. The public default is for development. RPC chain ID and contract decimals are checked before accepting logs and reporting availability.
3. Add fresh public receiving addresses under the store's USDC wallet and enable it. Keep keys in an external wallet; the plugin never needs a seed or private key. Each address is permanently assigned to one invoice for that network, across all stores on this server. Replenish the pool as addresses are used. Do not share a pool with another BTCPay installation.
4. The default rate rules assume `USDC_USD = 1` and convert via USD/BTC. This is an explicit pricing assumption, not a live peg oracle; customize store rates if required.
5. Allow at least an hour of invoice monitoring after expiration for finalized-block detection. Use a long invoice validity while testing. Generate an invoice, pay with Sepolia test USDC and test ETH for gas, and wait for finalized detection plus the chosen confirmation policy. Obtain test tokens through [Circle's faucet](https://faucet.circle.com/); never send mainnet funds for testing.

Endpoint overrides can also be provided through BTCPay configuration as `USDC_ETHEREUM_JSONRPC_URI` or `USDC_SEPOLIA_JSONRPC_URI`. Saved server settings take precedence. Keep authenticated endpoint URLs in private server configuration, never source control, screenshots, or issue reports.

## Payment lifecycle

The handler atomically claims an unused address in PostgreSQL before exposing it, sets USDC as the currency with six decimals, and produces an EIP-681 ERC-20 transfer URI. A database transaction and unique address key prevent concurrent invoices or stores from receiving the same address. Retrying configuration for the same invoice returns its original address. Claims survive restarts, invoice expiration, settlement, and deletion; an ERC-20 transfer has no invoice identifier, so automatic address recycling would misattribute late payments. Even an unsuccessful invoice creation can consume an address. Standard, address-only, and custom-template links follow the reference's checkout conventions.

The listener starts after store activation, persists a separate scan height, and indexes finalized Ethereum blocks. It queries Transfer logs for the fixed contract and invoice destinations in batches of 20. It verifies returned contract, block hash/height, recipient, removal flag, and log index. Zero-value transfers are ignored. Each positive log becomes a separate BTCPay payment with exact decimal amounts. Transaction hash plus log index, currency, and chain identifies the payment, allowing repeat scans without changing its ID and preventing collisions with USDt.

BTCPay handles partial payments, overpayments, and invoice state transitions through its existing PaymentService. Block timestamps preserve the timing of late payments; deposits in blocks timestamped before invoice creation are ignored, comparing at Ethereum's one-second precision. Missing or invalid block timestamps stop indexing until the node recovers. Expired invoices remain tracked until monitoring expiration. Confirmation policies match the reference: HighSpeed 2, MediumSpeed 6, LowMediumSpeed 12, LowSpeed 20 **subsequent indexed finalized blocks**. The inclusion block starts at zero. Finalization adds latency; an RPC without finality support fails closed rather than falling back to latest blocks.

## Database and running-host tests

Start a disposable PostgreSQL service, restricted to loopback:

```sh
docker run --detach --name usdc-test-postgres -p 127.0.0.1:45432:5432 -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=usdc_tests postgres:16-alpine
export USDC_TEST_POSTGRES='Host=127.0.0.1;Port=45432;Database=usdc_tests;Username=postgres'
dotnet publish Plugins/BTCPayServer.Plugins.USDC -c Release -m:1 -p:StaticWebAssetsEnabled=false -o artifacts/usdc
dotnet test BTCPayServer.Plugins.USDC.Tests -c Release -m:1 -p:StaticWebAssetsEnabled=false --logger trx
```

In PowerShell use `$env:USDC_TEST_POSTGRES = '...'` instead of `export`. Never point tests at a real BTCPay database: allocator tests apply host migrations, and host tests create and drop isolated databases using this account's database-creation permission. The trust authentication above is only for this local disposable service. Stop and remove that named test container when finished.

To include USDt coexistence tests, publish `BTCPayServer.Plugins.USDt` from reference commit `cacb4e8c3d2672ae966853bc833af93a98e4c0b1` and set `USDC_TEST_USDT_PATH` to its absolute publish directory. CI provides PostgreSQL, publishes both plugins, and runs these tests. Without the relevant environment variables, database/coexistence tests explicitly report skipped.

For canonical-contract transaction tests, start an isolated [Anvil development node](https://www.getfoundry.sh/anvil/index.html):

```sh
docker run --detach --name usdc-test-anvil -p 127.0.0.1:48545:8545 --entrypoint anvil ghcr.io/foundry-rs/foundry@sha256:0c00cb0bda1ab1b91c9a6bf60f4c76c09c1a8870824b6d4718afbabacf6f9a17 --host 0.0.0.0 --silent
export USDC_TEST_ANVIL_URL='http://127.0.0.1:48545'
dotnet test BTCPayServer.Plugins.USDC.Tests -c Release -m:1 -p:StaticWebAssetsEnabled=false --filter FullyQualifiedName~LocalForkTests --logger trx
```

This test resets that local node, forks current Ethereum and Sepolia state from public read-only RPC endpoints, and uses Anvil impersonation to mint test balances and execute transfers against the canonical USDC contracts **on the local forks only**. It requires internet access and available upstream state. It verifies receipts, recipient balances, six-decimal payment detection, finalized-head coverage, and stable replay IDs. All transaction/state-changing calls are restricted to an explicit `http://127.0.0.1` Anvil endpoint. No wallet keys or public-network transactions are needed. CI runs this test too; it is explicitly skipped when the Anvil environment variable is absent. Stop and remove the named test node when finished.

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
