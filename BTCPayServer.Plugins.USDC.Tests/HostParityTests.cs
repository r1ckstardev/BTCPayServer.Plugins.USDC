using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Configuration;
using BTCPayServer.Plugins.USDC.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.USDC.Tests;

public partial class HostSmokeTests
{
    private static async Task RunRecoveryParity(IHost host, BTCPayServerClient api, string storeId,
        LocalEvmNode node, string originalId, string destination, string secondId)
    {
        var pmi = new PaymentMethodId("USDC-SEPOLIA");
        var recovery = host.Services.GetRequiredService<USDCChainRecovery>();
        var repository = host.Services.GetRequiredService<InvoiceRepository>();
        var originalIds = (await api.GetInvoicePaymentMethods(originalId)).Single().Payments.Select(p => p.Id).Order().ToArray();
        await api.UpdateStorePaymentMethod(storeId, pmi.ToString(), new UpdatePaymentMethodRequest
        {
            Enabled = true, Config = new JObject { ["addresses"] = new JArray(destination) }
        });
        var reused = await api.CreateInvoice(storeId, new CreateInvoiceRequest
        {
            Currency = "USDC", Amount = 1,
            Checkout = new InvoiceDataBase.CheckoutOptions { PaymentMethods = [pmi.ToString()], LazyPaymentMethods = false, SpeedPolicy = SpeedPolicy.HighSpeed }
        });
        Assert.Equal(destination, (await api.GetInvoicePaymentMethods(reused.Id)).Single().Destination);
        node.ForkFrom(102, 1);
        await Eventually(async () => (await repository.GetInvoice(originalId)).GetPayments(false) is { Count: 2 } payments && payments.All(p => p.Status == PaymentStatus.Unaccounted));
        Assert.Equal(InvoiceStatus.Settled, (await api.GetInvoice(originalId)).Status);
        await Eventually(async () => (await repository.GetInvoiceLogs(originalId)).Any(l => l.Message.Contains("USDC reconciliation required")));
        await using (var context = host.Services.GetRequiredService<ApplicationDbContextFactory>().CreateContext())
            Assert.True(await context.Notifications.AnyAsync(n => n.NotificationType == "usdc_reconciliation"));

        node.Reinclude(102, 108, 17);
        node.Reinclude(103, 109, 29);
        node.FinalizedHeight = 113;
        await Eventually(async () => (await api.GetInvoicePaymentMethods(originalId)).Single().Payments is { Count: 2 } payments && payments.All(p => p.Status == InvoicePaymentMethodDataModel.Payment.PaymentStatus.Settled));
        var restored = (await api.GetInvoicePaymentMethods(originalId)).Single();
        Assert.Equal(originalIds, restored.Payments.Select(p => p.Id).Order().ToArray());
        Assert.Equal(1m, restored.TotalPaid);
        Assert.Equal(1.2m, (await api.GetInvoicePaymentMethods(secondId)).Single().TotalPaid);
        Assert.Empty((await api.GetInvoicePaymentMethods(reused.Id)).Single().Payments);

        // Simulate an ambiguous payment write: BTCPay catches DbUpdateException
        // and returns null. The observation must survive while the cursor stops.
        await using (var context = host.Services.GetRequiredService<ApplicationDbContextFactory>().CreateContext())
            await context.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION usdc_test_failure() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Injected payment write failure'; END $$;
                CREATE TRIGGER usdc_test_failure BEFORE INSERT ON "Payments" FOR EACH ROW EXECUTE FUNCTION usdc_test_failure();
                """);
        node.Transfer(114, destination, 1);
        node.FinalizedHeight = 118;
        await Eventually(async () => (await recovery.Outstanding(pmi)).Any(o => o.InvoiceId == reused.Id));
        var listener = host.Services.GetServices<IHostedService>().OfType<EVMUSDCListener>().Single();
        await listener.StopAsync(CancellationToken.None);
        var configuration = host.Services.GetRequiredService<USDCPluginConfiguration>().EVMUSDCLikeConfigurationItems[pmi];
        var settings = host.Services.GetRequiredService<USDCChainStateStore>();
        var state = await settings.Get(EVMUSDCRPCProvider.ListenerStateSettingKey(configuration));
        Assert.True(state!.LastBlockHeight < 114);
        Assert.Empty((await api.GetInvoicePaymentMethods(reused.Id)).Single().Payments);
        await using (var context = host.Services.GetRequiredService<ApplicationDbContextFactory>().CreateContext())
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER usdc_test_failure ON \"Payments\"; DROP FUNCTION usdc_test_failure();");
        await listener.StartAsync(CancellationToken.None);
        await Eventually(async () => (await api.GetInvoicePaymentMethods(reused.Id)).Single().TotalPaid == 0.000001m);

        // Successful payment followed by a failed cursor write: restart must
        // replay without adding a second payment or changing invoice ownership.
        await using (var context = host.Services.GetRequiredService<ApplicationDbContextFactory>().CreateContext())
            await context.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION usdc_test_checkpoint_failure() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."Id" = 'USDC_SEPOLIA_LISTENER_STATE' AND (NEW."Value"->>'LastBlockHeight')::numeric >= 120
                  THEN RAISE EXCEPTION 'Injected checkpoint write failure'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER usdc_test_checkpoint_failure BEFORE INSERT OR UPDATE ON "Settings" FOR EACH ROW EXECUTE FUNCTION usdc_test_checkpoint_failure();
                """);
        node.Transfer(120, destination, 999999);
        node.FinalizedHeight = 124;
        await Eventually(async () => (await api.GetInvoicePaymentMethods(reused.Id)).Single().TotalPaid == 1m);
        await listener.StopAsync(CancellationToken.None);
        state = await settings.Get(EVMUSDCRPCProvider.ListenerStateSettingKey(configuration));
        Assert.True(state!.LastBlockHeight < 120);
        var beforeRestart = (await api.GetInvoicePaymentMethods(reused.Id)).Single().Payments.Select(p => p.Id).Order().ToArray();
        Assert.Equal(2, beforeRestart.Length);
        await using (var context = host.Services.GetRequiredService<ApplicationDbContextFactory>().CreateContext())
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER usdc_test_checkpoint_failure ON \"Settings\"; DROP FUNCTION usdc_test_checkpoint_failure();");
        node.ThrottleRequests = true;
        var failedBefore = node.FailedRequests;
        await listener.StartAsync(CancellationToken.None);
        await Eventually(() => Task.FromResult(node.FailedRequests > failedBefore));
        Assert.Equal(state.LastBlockHeight, (await settings.Get(EVMUSDCRPCProvider.ListenerStateSettingKey(configuration)))!.LastBlockHeight);
        node.ThrottleRequests = false;
        await Eventually(async () => (await api.GetInvoice(reused.Id)).Status == InvoiceStatus.Settled);
        Assert.Equal(beforeRestart, (await api.GetInvoicePaymentMethods(reused.Id)).Single().Payments.Select(p => p.Id).Order().ToArray());

        // A conflicting finalized anchor stops only this chain with a diagnostic.
        node.ForkFrom(100, 2);
        await Eventually(async () => (await settings.Get(EVMUSDCRPCProvider.ListenerStateSettingKey(configuration)))?.HaltReason is not null);
        var rpc = host.Services.GetRequiredService<EVMUSDCRPCProvider>();
        await rpc.UpdateSummary(pmi);
        Assert.False(rpc.IsAvailable(pmi));
        Assert.Contains("finalized checkpoint", rpc.Summaries[pmi].Diagnostic);
    }

    private static async Task RunBrowserParity(IHost host, string address, string email, string password,
        BTCPayServerClient api, string storeId, LocalEvmNode node)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { BaseURL = address });
        var page = await context.NewPageAsync();
        var screenshots = Path.Combine(RepositoryRoot(), "artifacts", "screenshots");
        Directory.CreateDirectory(screenshots);
        await page.GotoAsync("/login");
        await page.Locator("#Email").FillAsync(email);
        await page.Locator("#Password").FillAsync(password);
        await page.Locator("#LoginButton").ClickAsync();
        await page.WaitForURLAsync(url => !new Uri(url).AbsolutePath.Equals("/login"));
        var path = $"/stores/{storeId}/evmUSDClike/USDC-SEPOLIA";
        await page.GotoAsync(path);
        await Assertions.Expect(page.Locator("h3").Filter(new() { HasText = "USDC on Sepolia" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator($"a[href='/stores/{storeId}/evmUSDClike/USDC-AMOY']")).ToBeVisibleAsync();
        // Validate server-rendered errors and preserve submitted invalid values.
        await page.Locator("#Address").FillAsync("bad-address");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add address(es)", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("#Address")).ToHaveValueAsync("bad-address");
        await Assertions.Expect(page.Locator("[data-valmsg-for=Address]")).Not.ToBeEmptyAsync();

        await page.GotoAsync(path);
        await page.Locator("#PaymentLinkFormat").SelectOptionAsync("3");
        const string template = "ethereum:{smartContractAddress}@{chainId}/transfer?address={to}&uint256={amountUnits}";
        await page.Locator("#PaymentLinkTemplate").FillAsync(template);
        await page.Locator("#Enabled").UncheckAsync();
        await page.Locator("#SaveButton").ClickAsync();
        var added = "0x" + Guid.NewGuid().ToString("N") + "aabbccdd";
        await page.Locator("#Address").FillAsync(added);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add address(es)", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("#Enabled")).Not.ToBeCheckedAsync();
        await Assertions.Expect(page.Locator("#PaymentLinkFormat")).ToHaveValueAsync("3");
        await Assertions.Expect(page.Locator("#PaymentLinkTemplate")).ToHaveValueAsync(template);
        await Assertions.Expect(page.Locator("#StoreUsersList")).ToContainTextAsync(added);
        await page.ScreenshotAsync(new() { Path = Path.Combine(screenshots, "usdc-configuration.png"), FullPage = true });

        // Cookie-authenticated POST without an antiforgery token must be rejected.
        var csrf = await context.APIRequest.PostAsync(path + "/addresses/" + added + "/delete");
        Assert.Equal(400, csrf.Status);
        var infoPath = $"/api/v1/stores/{storeId}/evmUSDClike/USDC-SEPOLIA";
        using var anonymous = new HttpClient { BaseAddress = new Uri(address) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(infoPath)).StatusCode);
        using var authorized = new HttpClient { BaseAddress = new Uri(address) };
        authorized.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(email + ":" + password)));
        var information = await authorized.GetAsync(infoPath);
        information.EnsureSuccessStatusCode();
        var json = JObject.Parse(await information.Content.ReadAsStringAsync());
        Assert.Equal(storeId, json["storeId"]!.Value<string>());
        Assert.False(json["enabled"]!.Value<bool>());
        Assert.Contains(json["addresses"]!, a => a["value"]!.Value<string>() == added && a["available"]!.Value<bool>());
        Assert.Equal(HttpStatusCode.NotFound, (await authorized.GetAsync(infoPath.Replace("USDC-SEPOLIA", "USDC-AMOY"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await authorized.GetAsync(infoPath.Replace("USDC-SEPOLIA", "USDC-UNKNOWN"))).StatusCode);

        // Real Greenfield API keys must enforce the documented store-view scope.
        using var scopedClient = new HttpClient { BaseAddress = new Uri(address) };
        var viewKey = await api.CreateAPIKey(new CreateApiKeyRequest
        {
            Label = "USDC read test", Permissions = [Permission.Parse($"btcpay.store.canviewstoresettings:{storeId}")]
        });
        scopedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", viewKey.ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await scopedClient.GetAsync(infoPath)).StatusCode);
        var invoiceKey = await api.CreateAPIKey(new CreateApiKeyRequest
        {
            Label = "USDC denied test", Permissions = [Permission.Parse($"btcpay.store.canviewinvoices:{storeId}")]
        });
        scopedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", invoiceKey.ApiKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await scopedClient.GetAsync(infoPath)).StatusCode);

        // An authenticated user outside the store cannot see balances or settings.
        var outsiderEmail = $"outsider-{Guid.NewGuid():N}@example.test";
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = outsiderEmail, Email = outsiderEmail, EmailConfirmed = true };
            user.SetBlob(new UserBlob { AllowGreenfieldBasicAuth = true });
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
        }
        using var outsider = new HttpClient { BaseAddress = new Uri(address) };
        outsider.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(outsiderEmail + ":" + password)));
        Assert.Contains((await outsider.GetAsync(infoPath)).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });

        await page.Locator("#Enabled").CheckAsync();
        await page.Locator("#PaymentLinkFormat").SelectOptionAsync("0");
        await page.Locator("#SaveButton").ClickAsync();
        var invoice = await api.CreateInvoice(storeId, new CreateInvoiceRequest
        {
            Currency = "USDC", Amount = 1.234567m,
            Checkout = new InvoiceDataBase.CheckoutOptions { PaymentMethods = ["USDC-SEPOLIA"], LazyPaymentMethods = false }
        });
        var method = (await api.GetInvoicePaymentMethods(invoice.Id)).Single();
        Assert.Contains("uint256=1234567", method.PaymentLink);
        await page.GotoAsync(invoice.CheckoutLink);
        await Assertions.Expect(page.Locator("body")).ToContainTextAsync("USDC");
        await Assertions.Expect(page.Locator("body")).ToContainTextAsync("1.234567");
        await Assertions.Expect(page.Locator(".qr-container")).ToHaveAttributeAsync("data-qr-value", method.PaymentLink);
        await Assertions.Expect(page.Locator(".qr-container svg")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".qr-container")).ToHaveAttributeAsync("data-clipboard", method.PaymentLink);
        await page.ScreenshotAsync(new() { Path = Path.Combine(screenshots, "usdc-checkout.png"), FullPage = true });
        // Exercise BTCPay's actual package upload and queued installation. The
        // caller restarts the host and compares the installed assembly hash.
        var package = Directory.GetFiles(Path.Combine(RepositoryRoot(), "artifacts", "packed"), "*.btcpay", SearchOption.AllDirectories).Single();
        await page.GotoAsync("/server/plugins");
        await page.GetByRole(AriaRole.Button, new() { Name = "Upload Plugin", Exact = true }).ClickAsync();
        await page.Locator("#files").SetInputFilesAsync(package);
        await page.GetByRole(AriaRole.Button, new() { Name = "Upload", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("body")).ToContainTextAsync("Files uploaded, restart server to load plugins");
        await page.ScreenshotAsync(new() { Path = Path.Combine(screenshots, "usdc-package-installed.png"), FullPage = true });
    }
}
