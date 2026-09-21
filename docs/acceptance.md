# Parity acceptance matrix

This matrix identifies tests, not a claim that every gate has passed. Final evidence must name the exact commit and CI run. Open items block release readiness.

| Feature | Evidence/test | Gate |
| --- | --- | --- |
| Four native networks, six decimals, rate and checkout links | PaymentTests | Automated |
| Settings validation, preserved fields, navigation, CSRF | BrowserConfigurationValidationPermissionsAndCheckout | Actual Chromium |
| Authenticated address/balance API, unknown methods, store isolation | BrowserConfigurationValidationPermissionsAndCheckout | Actual host |
| Concurrent reservations, retries, stores/process-shared lock, exhaustion | DatabaseTests, HostSmokeTests | PostgreSQL |
| Release boundaries, saved deadlines, >49-day records, history and re-add | ReservationLifecycleTests | PostgreSQL |
| Partial/exact/multiple/overpaid/late/old deposits and RPC switching | PublishedPluginPersistsPartialAndMultiplePaymentsRecoversFromRpcFailureAndListenerRestart | Actual host |
| Smallest unit, replay, wrong chain/token, receipt/log consistency | PaymentTests, RpcPaymentTests, recovery host test | Automated |
| Reorg, receipt-position identity, original ownership after reuse, notices | ReorganizationRetainsInvoiceAssociationAfterAddressReuseAndHaltsOnFinalityConflict | Actual host |
| Ambiguous failed payment write, durable observation restart, checkpoint ordering | Same recovery host test with PostgreSQL failure trigger | Actual host |
| USDt/USDC same address without cross-credit | PublishedPluginWorksAlongsideUSDt at frozen USDt reference | Actual host |
| Canonical contract transfers | LocalForkTests | Local Anvil only |
| Official package, exact source/assembly and checksum | CI packer and host assembly comparison | CI artifact |
| Package upload UI and restart | Pending dedicated package exercise | Required |
| Public Sepolia and Amoy checkout payments | Pending owner-controlled funded wallets | Required |
| Plugin Builder registration/source access/build/publish permissions | Pending authenticated inspection | Required handoff |

Additional fault coverage must be reviewed before declaring acceptance: provider throttling, process interruption after a successful payment write but before checkpoint write, and any gaps found during test/implementation review. Unexpected skips, attribution/accounting/authentication/recovery defects block release readiness.
