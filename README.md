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

For development, set BTCPay's `DEBUG_PLUGINS` to the absolute path of `Plugins/BTCPayServer.Plugins.USDC/bin/Release/net10.0/BTCPayServer.Plugins.USDC.dll`, then restart the development server. Alternatively, the packer creates `artifacts/packed/BTCPayServer.Plugins.USDC/0.1.0.0/BTCPayServer.Plugins.USDC.btcpay` for manual installation in a test instance. Packaging and plugin metadata loading were verified locally. The repository follows the standalone Razor plugin layout used by [SamRockProtocol](https://github.com/rockstardev/SamRockProtocol). Distribution through Plugin Builder remains a release task. No deployment or automatic publishing workflow is included.

## Configure a test store

1. Run BTCPay on testnet/regtest. Verify that the USDC menu says **Sepolia (testnet)**.
2. In server settings, configure a reliable Sepolia JSON-RPC endpoint under USDC. The endpoint must support `eth_chainId`, `eth_call`, `eth_getLogs`, `eth_syncing`, and `eth_getBlockByNumber` with the `finalized` tag. The public default is for development. RPC chain ID and contract decimals are checked before accepting logs and reporting availability.
3. Add public receiving addresses under the store's USDC wallet and enable it. Keep keys in an external wallet; the plugin never needs a seed or private key. Do not reuse these receiving addresses across stores or simultaneous invoices. See the concurrency limitation below.
4. The default rate rules assume `USDC_USD = 1` and convert via USD/BTC. This is an explicit pricing assumption, not a live peg oracle; customize store rates if required.
5. Allow at least an hour of invoice monitoring after expiration for finalized-block detection. Use a long invoice validity while testing. Generate an invoice, pay with Sepolia test USDC and test ETH for gas, and wait for finalized detection plus the chosen confirmation policy. Obtain test tokens through [Circle's faucet](https://faucet.circle.com/); never send mainnet funds for testing.

Endpoint overrides can also be provided through BTCPay configuration as `USDC_ETHEREUM_JSONRPC_URI` or `USDC_SEPOLIA_JSONRPC_URI`. Saved server settings take precedence. Keep authenticated endpoint URLs in private server configuration, never source control, screenshots, or issue reports.

## Payment lifecycle

The handler reserves an address from the configured pool for a tracked invoice, sets USDC as the currency with six decimals, and produces an EIP-681 ERC-20 transfer URI. Standard, address-only, and custom-template links follow the reference's checkout conventions.

The listener starts after store activation, persists a separate scan height, and indexes finalized Ethereum blocks. It queries Transfer logs for the fixed contract and invoice destinations in batches of 20. It verifies returned contract, block hash/height, recipient, removal flag, and log index. Zero-value transfers are ignored. Each positive log becomes a separate BTCPay payment with exact decimal amounts. Transaction hash plus log index, currency, and chain identifies the payment, allowing repeat scans without changing its ID and preventing collisions with USDt.

BTCPay handles partial payments, overpayments, and invoice state transitions through its existing PaymentService. Block timestamps preserve the timing of late payments. Expired invoices remain tracked until monitoring expiration. Confirmation policies match the reference: HighSpeed 2, MediumSpeed 6, LowMediumSpeed 12, LowSpeed 20 **subsequent indexed finalized blocks**. The inclusion block starts at zero. Finalization adds latency; an RPC without finality support fails closed rather than falling back to latest blocks.

## Validation and limitations

- Automated tests use an in-memory JSON-RPC transport, exercising real Nethereum filter construction and ERC-20 decoding without credentials or transactions. Tests cover partial/exact/overpayment amounts, one atomic unit, large amounts, culture invariance, batched logs, duplicate replay, wrong contract/recipient/block, removed logs, network/decimal mismatch, finality, checkout links, confirmation thresholds, and expiration tracking.
- PostgreSQL-backed invoice transitions, a live BTCPay UI session, co-installation in a running server, and a funded Sepolia payment have not yet been verified. Docker's Linux engine was unavailable on the development host. These are pre-release acceptance steps, not claimed passing tests.
- The reference's address-pool allocation checks existing invoice reservations but is not transactional across concurrent invoice creation. Until an atomic allocation mechanism is implemented and integration-tested, serialize invoice creation and dedicate address pools to one store. Address reuse after monitoring expiration can misattribute very late payments; retire addresses rather than reuse them when that risk matters.
- No automatic sweeping, refunds, payouts, wallet generation, historical backfill from before initial activation, or recovery after catastrophic finalized-chain rollback. The node is trusted to report canonical finalized history. RPC outages or stalled finality delay detection.
- The inherited 49-day expired-invoice bootstrap lookback does not cover arbitrarily old monitoring windows. Keep monitoring windows within that bound.
- NuGet reports existing advisories in the pinned BTCPay dependency graph (AngleSharp, SSH.NET, and Microsoft.Build.Tasks.Git). Host dependency upgrades belong in the BTCPay repository and were not silently substituted here.

## Provenance

Derived from the MIT-licensed [USDt plugin](https://github.com/btcpayserver-tether/BTCPayServer.Plugins.USDt) at `b930b5d90116a8da824f84327c3e2f1ae79d1fcf`. Its EVM lifecycle, settings, checkout, and monitoring code were adapted into a separate USDC namespace; unrelated TRON implementations were omitted. The original license is retained. The pinned BTCPay commit is `cea07e5e18528396d97368b43085f59e3e58e284`.
