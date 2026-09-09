using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.USDC.Services;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json.Linq;
using Npgsql;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using AngleSharp.Html.Parser;

namespace BTCPayServer.Plugins.USDC.Tests;

public class HostSmokeTests
{
    internal static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BTCPayServer.Plugins.USDC.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot locate the USDC checkout.");
    }

    [DatabaseFact]
    public Task PublishedPluginPersistsPartialAndMultiplePaymentsRecoversFromRpcFailureAndListenerRestart() => RunLifecycle(false);

    [CoInstallFact]
    public Task PublishedPluginWorksAlongsideUSDt() => RunLifecycle(true);

    private async Task RunLifecycle(bool coInstall)
    {
        await using var node = new LocalEvmNode();
        await using var replacementNode = new LocalEvmNode();
        // A fresh database prevents previous runs' global scan cursors/settings influencing this chain.
        var database = "usdc_host_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("USDC_TEST_POSTGRES"));
        await using var admin = new NpgsqlConnection(connection.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin)) await create.ExecuteNonQueryAsync();
        connection.Database = database;
        var root = RepositoryRoot();
        var data = Path.Combine(root, "artifacts", "host-tests", Guid.NewGuid().ToString("N"));
        var plugins = Path.Combine(data, "plugins");
        var plugin = Path.Combine(plugins, "BTCPayServer.Plugins.USDC");
        Directory.CreateDirectory(plugin);
        var published = Path.Combine(root, "artifacts", "usdc");
        Assert.True(File.Exists(Path.Combine(published, "BTCPayServer.Plugins.USDC.dll")), "Publish the plugin to artifacts/usdc before running host integration tests.");
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(USDCPlugin).Assembly.Location)),
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(published, "BTCPayServer.Plugins.USDC.dll"))));
        foreach (var file in Directory.GetFiles(published)) File.Copy(file, Path.Combine(plugin, Path.GetFileName(file)));
        if (coInstall)
        {
            var usdt = Environment.GetEnvironmentVariable("USDC_TEST_USDT_PATH")!;
            Assert.True(File.Exists(Path.Combine(usdt, "BTCPayServer.Plugins.USDt.dll")));
            var target = Path.Combine(plugins, "BTCPayServer.Plugins.USDt");
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(usdt)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        var config = new Dictionary<string, string?>
        {
            ["network"] = "regtest", ["chains"] = "none", ["nodefaultchain"] = "true",
            ["datadir"] = data, ["plugindir"] = plugins,
            ["postgres"] = connection.ConnectionString,
            ["TEST_RUNNER_ENABLED"] = "true", ["disable-registration"] = "false",
            ["USDC_SEPOLIA_JSONRPC_URI"] = node.Url,
            ["USDT_SEPOLIA_JSONRPC_URI"] = node.Url
        };
        IHost BuildHost() => Host.CreateDefaultBuilder()
            .UseEnvironment("Development")
            .UseContentRoot(Path.Combine(root, "submodules", "btcpayserver", "BTCPayServer"))
            .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(config))
            .ConfigureLogging(builder => builder.ClearProviders().AddConsole().SetMinimumLevel(LogLevel.Warning))
            .ConfigureWebHostDefaults(web => web.UseKestrel().UseUrls("http://127.0.0.1:0").UseStartup<Startup>())
            .Build();
        var host = BuildHost();
        await host.StartWithTasksAsync().WaitAsync(TimeSpan.FromSeconds(90));
        try
        {
            var handlers = host.Services.GetRequiredService<PaymentMethodHandlerDictionary>();
            Assert.True(handlers.TryGetValue(new PaymentMethodId("USDC-SEPOLIA"), out _));
            Assert.Equal(coInstall, handlers.TryGetValue(new PaymentMethodId("USDT-SEPOLIA"), out _));
            var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var response = await client.GetAsync("/");
            response.EnsureSuccessStatusCode();
            Assert.Contains("BTCPay", await response.Content.ReadAsStringAsync());

            var email = $"usdc-{Guid.NewGuid():N}@example.test";
            var password = Guid.NewGuid().ToString("N") + "!Aa9";
            using (var scope = host.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                if (!await roles.RoleExistsAsync(Roles.ServerAdmin))
                    Assert.True((await roles.CreateAsync(new IdentityRole(Roles.ServerAdmin))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
                Assert.True((await users.CreateAsync(user, password)).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, Roles.ServerAdmin)).Succeeded);
            }
            var api = new BTCPayServerClient(new Uri(address), email, password);
            var store = await api.CreateStore(new CreateStoreRequest { Name = "USDC lifecycle test" });
            var destination = "0x" + Guid.NewGuid().ToString("N") + "11111111";
            var secondDestination = "0x" + Guid.NewGuid().ToString("N") + "22222222";
            var lateDestination = "0x" + Guid.NewGuid().ToString("N") + "33333333";
            var switchedDestination = "0x" + Guid.NewGuid().ToString("N") + "44444444";
            foreach (var invalid in new[] { "not-an-address", "0x0000000000000000000000000000000000000000", Configuration.USDCConfigurationProvider.SepoliaContract })
                await Assert.ThrowsAsync<GreenfieldValidationException>(() => api.UpdateStorePaymentMethod(store.Id, "USDC-SEPOLIA",
                    new UpdatePaymentMethodRequest { Enabled = true, Config = new JObject { ["addresses"] = new JArray(invalid) } }));
            await api.UpdateStorePaymentMethod(store.Id, "USDC-SEPOLIA", new UpdatePaymentMethodRequest
            {
                Enabled = true, Config = new JObject { ["addresses"] = new JArray(destination, secondDestination, lateDestination, switchedDestination) }
            });
            const string usdtContract = "0xf02d1af3c9ec13f4e9e986f2de75da96d75a57b0";
            BTCPayServer.Client.Models.InvoiceData? usdtInvoice = null;
            if (coInstall)
            {
                await api.UpdateStorePaymentMethod(store.Id, "USDT-SEPOLIA", new UpdatePaymentMethodRequest
                {
                    Enabled = true, Config = new JObject { ["addresses"] = new JArray(destination) }
                });
                var usdtPmi = new PaymentMethodId("USDT-SEPOLIA");
                // Resolve readiness from the dynamically installed assembly without a USDt project dependency.
                var rpcType = handlers[usdtPmi].GetType().Assembly.GetType("BTCPayServer.Plugins.USDt.Services.EVMUSDtRPCProvider")!;
                dynamic usdtRpc = host.Services.GetRequiredService(rpcType);
                await Eventually(async () => { await usdtRpc.UpdateSummary(usdtPmi); return (bool)usdtRpc.IsAvailable(usdtPmi); });
                usdtInvoice = await api.CreateInvoice(store.Id, new CreateInvoiceRequest
                {
                    Currency = "USDT", Amount = 1m,
                    Checkout = new InvoiceDataBase.CheckoutOptions
                    {
                        PaymentMethods = ["USDT-SEPOLIA"], LazyPaymentMethods = false,
                        SpeedPolicy = SpeedPolicy.HighSpeed, Expiration = TimeSpan.FromHours(1)
                    }
                });
                var usdtMethod = Assert.Single(await api.GetInvoicePaymentMethods(usdtInvoice.Id));
                Assert.Equal(destination, usdtMethod.Destination);
                Assert.Contains(usdtContract, usdtMethod.PaymentLink, StringComparison.OrdinalIgnoreCase);
                var usdtCheckout = await client.GetAsync(usdtInvoice.CheckoutLink);
                usdtCheckout.EnsureSuccessStatusCode();
                Assert.Contains("USDt", await usdtCheckout.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            }
            var rpc = host.Services.GetRequiredService<EVMUSDCRPCProvider>();
            var pmi = new PaymentMethodId("USDC-SEPOLIA");
            await Eventually(async () => { await rpc.UpdateSummary(pmi); return rpc.IsAvailable(pmi); });
            if (!coInstall)
            {
                var sharedPool = Enumerable.Range(0, 6).Select(_ => "0x" + Guid.NewGuid().ToString("N") + "abcdef12").ToArray();
                var stores = new[]
                {
                    await api.CreateStore(new CreateStoreRequest { Name = "Concurrent A" }),
                    await api.CreateStore(new CreateStoreRequest { Name = "Concurrent B" })
                };
                foreach (var concurrentStore in stores)
                    await api.UpdateStorePaymentMethod(concurrentStore.Id, "USDC-SEPOLIA", new UpdatePaymentMethodRequest
                    {
                        Enabled = true, Config = new JObject { ["addresses"] = new JArray(sharedPool) }
                    });
                var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => api.CreateInvoice(stores[i % 2].Id,
                    new CreateInvoiceRequest
                    {
                        Currency = "USDC", Amount = 1m,
                        Checkout = new InvoiceDataBase.CheckoutOptions { PaymentMethods = ["USDC-SEPOLIA"], LazyPaymentMethods = false }
                    })));
                var destinations = await Task.WhenAll(concurrent.Select(async item =>
                    Assert.Single(await api.GetInvoicePaymentMethods(item.Id)).Destination));
                Assert.Equal(6, destinations.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                Assert.All(destinations, item => Assert.Contains(item, sharedPool));
            }
            var invoice = await api.CreateInvoice(store.Id, new CreateInvoiceRequest
            {
                Currency = "USDC", Amount = 1m,
                Checkout = new InvoiceDataBase.CheckoutOptions
                {
                    PaymentMethods = ["USDC-SEPOLIA"], LazyPaymentMethods = false,
                    SpeedPolicy = SpeedPolicy.HighSpeed, Expiration = TimeSpan.FromHours(1), Monitoring = TimeSpan.FromHours(1)
                }
            });
            var method = Assert.Single(await api.GetInvoicePaymentMethods(invoice.Id));
            Assert.Equal(destination, method.Destination);
            Assert.Contains("@11155111/transfer", method.PaymentLink);
            var checkout = await client.GetAsync(invoice.CheckoutLink);
            checkout.EnsureSuccessStatusCode();
            Assert.Contains("USDC", await checkout.Content.ReadAsStringAsync());
            var secondInvoice = await api.CreateInvoice(store.Id, new CreateInvoiceRequest
            {
                Currency = "USDC", Amount = 1m,
                Checkout = new InvoiceDataBase.CheckoutOptions
                {
                    PaymentMethods = ["USDC-SEPOLIA"], LazyPaymentMethods = false,
                    SpeedPolicy = SpeedPolicy.HighSpeed, Expiration = TimeSpan.FromHours(1), Monitoring = TimeSpan.FromHours(1)
                }
            });
            Assert.Equal(secondDestination, (await api.GetInvoicePaymentMethods(secondInvoice.Id)).Single().Destination);
            var lateInvoice = await api.CreateInvoice(store.Id, new CreateInvoiceRequest
            {
                Currency = "USDC", Amount = 1m,
                Checkout = new InvoiceDataBase.CheckoutOptions
                {
                    PaymentMethods = ["USDC-SEPOLIA"], LazyPaymentMethods = false,
                    SpeedPolicy = SpeedPolicy.HighSpeed, Expiration = TimeSpan.FromMinutes(1), Monitoring = TimeSpan.FromHours(1)
                }
            });
            Assert.Equal(lateDestination, (await api.GetInvoicePaymentMethods(lateInvoice.Id)).Single().Destination);
            node.SetTimestamp(101, DateTimeOffset.UtcNow.AddDays(-1));
            node.Transfer(101, destination, 5000000); // An old deposit cannot pay this invoice.
            node.Transfer(102, destination, 400000);
            node.FinalizedHeight = 103;
            await Eventually(async () => (await api.GetInvoicePaymentMethods(invoice.Id)).Single().TotalPaid == 0.4m);
            Assert.Equal(InvoiceStatus.New, (await api.GetInvoice(invoice.Id)).Status);
            // Stop completely, then resume from the persisted cursor with an unavailable RPC.
            var listener = host.Services.GetServices<IHostedService>().OfType<EVMUSDCListener>().Single();
            await listener.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            node.FailRequests = true;
            await listener.StartAsync(CancellationToken.None);
            await Eventually(() => Task.FromResult(node.FailedRequests > 0));
            Assert.Equal(0.4m, (await api.GetInvoicePaymentMethods(invoice.Id)).Single().TotalPaid);
            node.Transfer(103, destination, 600000);
            node.Transfer(103, secondDestination, 700000, 1);
            node.Transfer(103, secondDestination, 500000, 2);
            node.Transfer(103, secondDestination, 500000, 2); // Duplicate RPC log must not double-credit.
            if (coInstall) node.Transfer(103, destination, 1000000, 3, usdtContract);
            node.FinalizedHeight = 104;
            node.FailRequests = false;
            await Eventually(async () => (await api.GetInvoice(invoice.Id)).Status == InvoiceStatus.Processing);
            node.FinalizedHeight = 107;
            await Eventually(async () => (await api.GetInvoice(invoice.Id)).Status == InvoiceStatus.Settled);
            method = Assert.Single(await api.GetInvoicePaymentMethods(invoice.Id));
            Assert.Equal(1m, method.TotalPaid);
            Assert.Equal(2, method.Payments.Count);
            Assert.All(method.Payments, p => Assert.Equal(InvoicePaymentMethodDataModel.Payment.PaymentStatus.Settled, p.Status));
            await Eventually(async () => (await api.GetInvoice(secondInvoice.Id)).Status == InvoiceStatus.Settled);
            var overpaid = (await api.GetInvoicePaymentMethods(secondInvoice.Id)).Single();
            Assert.Equal(1.2m, overpaid.TotalPaid);
            Assert.Equal(2, overpaid.Payments.Count);
            Assert.Equal(InvoiceExceptionStatus.PaidOver, (await api.GetInvoice(secondInvoice.Id)).AdditionalStatus);
            if (usdtInvoice is not null)
            {
                await Eventually(async () => (await api.GetInvoice(usdtInvoice.Id)).Status == InvoiceStatus.Settled);
                var usdtPayment = Assert.Single(await api.GetInvoicePaymentMethods(usdtInvoice.Id));
                Assert.Equal(1m, usdtPayment.TotalPaid);
                Assert.Single(usdtPayment.Payments);
                Assert.Equal(1m, (await api.GetInvoicePaymentMethods(invoice.Id)).Single().TotalPaid);
            }
            await Eventually(async () => (await api.GetInvoice(lateInvoice.Id)).Status == InvoiceStatus.Expired);

            var switchedInvoice = await api.CreateInvoice(store.Id, new CreateInvoiceRequest
            {
                Currency = "USDC", Amount = 1m,
                Checkout = new InvoiceDataBase.CheckoutOptions
                {
                    PaymentMethods = ["USDC-SEPOLIA"], LazyPaymentMethods = false,
                    SpeedPolicy = SpeedPolicy.HighSpeed, Expiration = TimeSpan.FromHours(1)
                }
            });
            Assert.Equal(switchedDestination, (await api.GetInvoicePaymentMethods(switchedInvoice.Id)).Single().Destination);
            await SubmitForm(client, "/login", "#login-form", new()
            {
                ["Email"] = email, ["Password"] = password, ["Method"] = "Password"
            });
            replacementNode.FinalizedHeight = 107;
            // The old provider has the next block; the replacement is still behind. Switch while
            // that block request is in flight to prove the cursor cannot skip its payment.
            if (!coInstall)
            {
                replacementNode.ObserveEmptyLogsHeight = 108;
                node.PauseBlockHeight = 108;
                node.Transfer(108, switchedDestination, 1000000);
                node.FinalizedHeight = 109;
                await node.BlockRequestPaused.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            try
            {
                await SubmitForm(client, "/server/evmUSDClike/USDC-SEPOLIA", "form[enctype='multipart/form-data']",
                    new() { ["JsonRpcUri"] = replacementNode.Url });
            }
            finally { node.ResumeBlockRequest.TrySetResult(); }
            if (!coInstall)
                await replacementNode.EmptyLogResponseCreated.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var activeConfiguration = host.Services.GetRequiredService<Configuration.USDCPluginConfiguration>();
            Assert.Equal(replacementNode.Url, activeConfiguration.EVMUSDCLikeConfigurationItems[pmi].JsonRpcUri.AbsoluteUri);
            // The old endpoint stays healthy. Detection must switch without a restart/error.
            replacementNode.Transfer(108, switchedDestination, 1000000);
            replacementNode.FinalizedHeight = 112;
            await Eventually(async () => (await api.GetInvoice(switchedInvoice.Id)).Status == InvoiceStatus.Settled);

            // Rebuild the entire host, including tracking caches, against its existing database.
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
            host.Dispose();
            host = BuildHost();
            await host.StartWithTasksAsync().WaitAsync(TimeSpan.FromSeconds(90));
            address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            api = new BTCPayServerClient(new Uri(address), email, password);
            Assert.Equal(1m, (await api.GetInvoicePaymentMethods(invoice.Id)).Single().TotalPaid);
            replacementNode.Transfer(113, lateDestination, 1000000);
            replacementNode.FinalizedHeight = 117;
            await Eventually(async () => (await api.GetInvoice(lateInvoice.Id)).AdditionalStatus == InvoiceExceptionStatus.PaidLate);
            var late = (await api.GetInvoicePaymentMethods(lateInvoice.Id)).Single();
            Assert.Equal(1m, late.TotalPaid);
            Assert.Single(late.Payments);
            Assert.Equal(InvoiceStatus.Expired, (await api.GetInvoice(lateInvoice.Id)).Status);
        }
        finally
        {
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
            host.Dispose();
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    internal static async Task Eventually(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(200);
        }
        Assert.Fail("The expected live-host state was not reached within 90 seconds.");
    }

    private static async Task SubmitForm(HttpClient client, string path, string selector, Dictionary<string, string> values)
    {
        using var page = await client.GetAsync(path);
        page.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync());
        var form = document.QuerySelector(selector);
        Assert.NotNull(form);
        var fields = form.QuerySelectorAll("input[name]").GroupBy(input => input.GetAttribute("name")!)
            .ToDictionary(group => group.Key, group => group.First().GetAttribute("value") ?? "");
        Assert.Contains("__RequestVerificationToken", fields.Keys);
        foreach (var value in values) fields[value.Key] = value.Value;
        using var result = await client.PostAsync(path, new FormUrlEncodedContent(fields));
        result.EnsureSuccessStatusCode();
        Assert.NotEqual("/login", result.RequestMessage!.RequestUri!.AbsolutePath);
    }
}

public sealed class CoInstallFactAttribute : FactAttribute
{
    public CoInstallFactAttribute([System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1) : base(sourceFilePath, sourceLineNumber)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("USDC_TEST_POSTGRES")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("USDC_TEST_USDT_PATH")))
            Skip = "Set USDC_TEST_POSTGRES and USDC_TEST_USDT_PATH to run plugin coexistence tests.";
    }
}
