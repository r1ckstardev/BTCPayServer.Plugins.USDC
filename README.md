# USDC for BTCPay Server

A separately installable, non-custodial plugin for native USDC on Ethereum and Polygon, with Sepolia and Amoy for testing. Keys stay in your wallet. The plugin monitors incoming transfers; it does not generate wallets, sweep funds, refund, or pay out.

**Parity work is under review in [PR #1](https://github.com/rockstardev/BTCPayServer.Plugins.USDC/pull/1). No plugin-store release or production readiness is implied by this branch.** See [acceptance evidence](docs/acceptance.md).

## Networks and RPC configuration

| BTCPay network | Payment method | Chain ID | Native contract | Default RPC |
| --- | --- | --- | --- | --- |
| Mainnet | `USDC-ETHEREUM` | 1 | `0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48` | `https://ethereum.publicnode.com` |
| Mainnet | `USDC-POLYGON` | 137 | `0x3c499c542cef5e3811e1192ce70d8cc03d5c3359` | `https://polygon.drpc.org` |
| Testnet/regtest | `USDC-SEPOLIA` | 11155111 | `0x1c7d4b196cb0c7b01d743fbc6116a902379c7238` | `https://sepolia.publicnode.com` |
| Testnet/regtest | `USDC-AMOY` | 80002 | `0x41e94eb019c0762f9bfcf9fb1e58725bfb0e7582` | `https://polygon-amoy.drpc.org` |

Contracts are fixed to [Circle's native deployments](https://developers.circle.com/stablecoins/usdc-contract-addresses); all use six decimals. Bridged USDC.e, arbitrary tokens and other chains are excluded. Regtest selects public test networks; it does not provision an EVM chain.

Each chain has independent activation, configuration, synchronization, balances, explorer links and durable scan state. Configure RPCs in the corresponding USDC server settings page. Alternatively use `USDC_<CHAIN>_JSONRPC_URI` in BTCPay configuration, or `BTCPAY_USDC_<CHAIN>_JSONRPC_URI` as an environment variable. Saved server settings override configuration, which overrides the defaults. Saving an endpoint replaces the running client without restarting; the same chain resumes its existing cursor. Never include credentials in source, screenshots or bug reports.

Providers must support `eth_chainId`, `eth_call`, `eth_syncing`, `eth_blockNumber`, `eth_getBlockByNumber` (including `finalized`), `eth_getLogs` and `eth_getTransactionReceipt`. The plugin checks chain ID and token decimals. Public defaults can throttle; use a suitable provider for production. An unavailable provider pauses indexing and does not advance the checkpoint.

## Installation and store setup

The compatibility target and declared minimum are **BTCPay Server 2.4.4**, using .NET 10. Earlier hosts are unsupported. Do not upgrade a live host just to evaluate this branch.

1. Use a disposable BTCPay test host with a fresh database. Migration from the permanent-address prototype is excluded; databases are never automatically reset.
2. Build the exact reviewed source using the official packer below, upload the `.btcpay` file through **Server Settings > Plugins**, and restart through the host's normal process.
3. Check each USDC server settings page and RPC synchronization status.
4. Under the store's USDC navigation, add addresses you control and enable the selected chain. Keep wallet keys outside BTCPay. Multiple addresses allow concurrent invoices. Use dedicated addresses; do not share their pool with a separate BTCPay installation.
5. Choose EIP-681, address-only links or a custom template. Address-only payment requires the payer to select the correct network/token and enter the amount. Custom placeholders are `{to}`, `{amount}`, `{amountUnits}`, `{smartContractAddress}`, `{chainId}`; `{to}` is mandatory.
6. Check pricing and monitoring. Default rates assume `USDC_USD = 1`; this is not a live peg oracle. Configure a monitoring window long enough for the expected payment and confirmation delays.

For testnet checkout, obtain test USDC through [Circle's faucet](https://faucet.circle.com/) and native gas funds for Sepolia or Amoy. Create a small invoice, pay using the exact displayed network/contract/address, and verify detection and settlement. Local-fork transactions are not public testnet evidence.

Address input is validated before saving. Duplicates are handled without creating extra entries; removal ignores address case. Adding addresses preserves other payment settings. Legacy blank entries can be removed explicitly with **Remove empty entries**. Balances display all six decimal places when needed.

## Reservations and payment lifecycle

Allocation uses PostgreSQL transactions and a shared advisory lock per payment method, across stores and processes. Retries for the same invoice keep its assignment. UI/API availability and allocation use the same reservation rules:

- `New` and `Processing` invoices retain their address.
- `Expired` invoices retain it until their saved `MonitoringExpiration`.
- `Settled` and `Invalid` invoices release it. Assignment history remains durable.

Changing a store's monitoring policy affects future invoices; existing invoices retain their saved deadlines. Removing and re-adding an address does not bypass a live reservation. Monitoring is reconstructed from durable claims, including archived invoices and deadlines more than 49 days old.

**After an address is reassigned, a new transfer intended for an old invoice may be indistinguishable from payment for the new invoice.** Inform payers not to reuse old invoices. A transfer already observed by the plugin always keeps its original invoice association, including after re-inclusion.

Indexing follows the reference USDt timing: Ethereum/Sepolia lag the current head by one block; Polygon/Amoy by two. Confirmation policies require 2, 6, 12 or 20 *additional indexed blocks* for High, Medium, Low-Medium or Low speed. Detection is not delayed until finality. Partial, multiple, exact, overpaid and smallest-unit transfers use exact decimal accounting. Deposits predating invoice creation do not pay a new invoice.

## Reorganizations and recovery

The plugin persists block hashes/parents, a finalized checkpoint, and a versioned transfer journal. Transfer identity includes chain, contract, transaction hash and receipt-relative event position. A changed block-global log index does not create a second payment. Receipts and log payloads must agree; conflicting data stops advancement.

An observation's invoice association is persisted before its payment write. A cursor advances only after reading back and verifying durable payment state, including ambiguous `AddPayment` results. On a reorganization the listener finds the common ancestor, marks orphaned payments unaccounted, requests invoice recalculation, rewinds and replays. Settled payments remain monitored through finality even after their address is released.

BTCPay does not automatically reopen a settled invoice. If its payment is removed or deconfirmed, the plugin corrects payment accounting and retains a reconciliation notice in payment details and invoice events, with a BTCPay notification to the store. Review goods/services already delivered. Notification delivery is at least once across crashes, so a retry can repeat the notification.

A history conflict at the finalized checkpoint halts that chain and displays a diagnostic in the server sync summary. Preserve evidence, compare trusted RPC providers, and reconcile affected invoices manually. Do not clear the checkpoint to suppress the error. Prototype scan state without recovery history also stops with a diagnostic; use fresh staging data.

Back up the complete BTCPay database, configuration and plugin directory together. Reservation/assignment records (`USDC.Address.*`, `USDC.InvoiceAddress.*`), transfer journals (`USDC.Transfer.v2.*`) and listener settings are part of the database. Never delete these to make an address available. Restoring an old database while continuing to accept payments risks losing assignments and observations.

If an outage outlasts an invoice's monitoring deadline, reconcile transfers that were never observed before the deadline manually. There is no pre-activation historical backfill. Native chain finality and the provider's data remain trust assumptions.

## API

Use standard Greenfield payment-method configuration endpoints to configure USDC.

`GET /api/v1/stores/{storeId}/evmUSDClike/{paymentMethodId}` returns:

```json
{"storeId":"...","paymentMethodId":"USDC-SEPOLIA","enabled":true,"addresses":[{"value":"0x...","available":true,"balance":1.234567}]}
```

Requires Greenfield authentication and `btcpay.store.canviewstoresettings` for that store. Unknown or unconfigured methods return 404. Balance is nullable when RPC lookup fails. Availability is a current snapshot, not an allocation promise. Swagger documents this endpoint. BTCPay 2.4.4 users must explicitly enable Basic authentication if using it instead of an API key.

## Build, tests and package

```sh
git clone --recurse-submodules https://github.com/rockstardev/BTCPayServer.Plugins.USDC.git
cd BTCPayServer.Plugins.USDC
git checkout feat/usdc-ethereum
git submodule update --init --recursive
dotnet publish Plugins/BTCPayServer.Plugins.USDC -c Release -m:1 -p:StaticWebAssetsEnabled=false -o artifacts/usdc
dotnet build BTCPayServer.Plugins.USDC.Tests -c Release -m:1 -p:StaticWebAssetsEnabled=false
pwsh BTCPayServer.Plugins.USDC.Tests/bin/Release/net10.0/playwright.ps1 install --with-deps chromium
dotnet run --project submodules/btcpayserver/BTCPayServer.PluginPacker -c Release -- artifacts/usdc BTCPayServer.Plugins.USDC artifacts/packed
```

Package path: `artifacts/packed/BTCPayServer.Plugins.USDC/0.2.0.0/BTCPayServer.Plugins.USDC.btcpay`. Release version remains proposed until the readiness handoff. Private repository access is currently required.

The Ubuntu workflow provisions PostgreSQL and a pinned Anvil container, publishes both plugins, installs Chromium, executes required integration tests, rejects skips, and retains the package, SHA256, tested source SHA, pinned host SHA, test results and screenshots. Host tests verify that the published DLL matches the tested assembly.

For local integration tests set:

- `USDC_TEST_POSTGRES`: connection string to a disposable PostgreSQL database whose user can create/drop test databases.
- `USDC_TEST_USDT_PATH`: published USDt assembly directory from reference `f1b3da1bc1ff2131cde946b938a2669e31f8fcc7`.
- `USDC_TEST_ANVIL_URL`: isolated Anvil at `http://127.0.0.1:<port>`; canonical-contract tests mutate only this local chain and use public upstream RPCs read-only.

Then run `dotnet test BTCPayServer.Plugins.USDC.Tests -c Release --no-build --logger trx`. Missing infrastructure produces local skips; a skipped integration test is **not** release acceptance. CI requires all tests to execute. No test sends real funds.

## Troubleshooting

- **No available addresses:** pending/processing invoices or saved expired monitoring windows still reserve the pool. Add addresses or wait for release; do not delete database records.
- **Unavailable / N/A balances:** check chain ID, native token contract decimals, RPC authentication, rate limits and connectivity.
- **Not settled yet:** compare indexed height and policy thresholds; inspect the transaction and receipt on the correct network explorer.
- **Indexing halted:** preserve the diagnostic and checkpoint; investigate finality/data conflicts before restarting monitoring.
- **Invoice remains settled after a reorg:** inspect unaccounted payments and the reconciliation notice; settlement history does not guarantee current payment accounting.
- **No plugin navigation:** verify BTCPay >=2.4.4, package identity, installation and restart logs.

## Provenance

Started from USDC `6c66e4ad4218edcb737b478d787f2a80a351d75c`. Parity reference is USDt master `f1b3da1bc1ff2131cde946b938a2669e31f8fcc7`, incorporating applicable PR #68 balance precision and PR #69 server navigation spacing. BTCPay is pinned to v2.4.4 (`2d5a0d8077bb33af080e949031da33d84b80638d`). The MIT license and original attribution are retained. Inherited host dependency advisories are reported by NuGet; they are not silently overridden here.
