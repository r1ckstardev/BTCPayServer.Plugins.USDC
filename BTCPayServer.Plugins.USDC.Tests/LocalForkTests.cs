using System.Numerics;
using System.Text;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Configuration;
using BTCPayServer.Plugins.USDC.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.USDC.Tests;

// Opt-in transaction tests against canonical issuer bytecode and state on a LOCAL Anvil fork.
// Only Anvil receives state changes or transactions; upstream RPC is used for read-only fork data.
public class LocalForkTests(ITestOutputHelper output)
{
    private const string Abi = """
        [{"type":"function","name":"masterMinter","inputs":[],"outputs":[{"type":"address"}],"stateMutability":"view"},
         {"type":"function","name":"configureMinter","inputs":[{"name":"minter","type":"address"},{"name":"amount","type":"uint256"}],"outputs":[{"type":"bool"}]},
         {"type":"function","name":"mint","inputs":[{"name":"to","type":"address"},{"name":"amount","type":"uint256"}],"outputs":[{"type":"bool"}]},
         {"type":"function","name":"transfer","inputs":[{"name":"to","type":"address"},{"name":"amount","type":"uint256"}],"outputs":[{"type":"bool"}]},
         {"type":"function","name":"balanceOf","inputs":[{"name":"owner","type":"address"}],"outputs":[{"type":"uint256"}],"stateMutability":"view"}]
        """;

    [LocalForkFact]
    public async Task CanonicalContractsOnAllFourNetworksExecuteTransfersRecognizedByThePlugin()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("USDC_TEST_ANVIL_URL")!);
        Assert.True(endpoint.Scheme == "http" && endpoint.Host == "127.0.0.1",
            "Transaction tests require an explicit local Anvil endpoint at http://127.0.0.1.");
        using var http = new HttpClient { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(90) };
        async Task<JToken> Rpc(string method, params object[] parameters)
        {
            var request = new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = JArray.FromObject(parameters) };
            using var response = await http.PostAsync("", new StringContent(request.ToString(), Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            var body = JObject.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(body["error"] is null, $"Local Anvil {method} failed: {body["error"]}");
            return body["result"]!;
        }
        Assert.Contains("anvil", (await Rpc("web3_clientVersion")).Value<string>()!, StringComparison.OrdinalIgnoreCase);
        var networks = new[]
        {
            USDCConfigurationProvider.GetDefault(ChainName.Mainnet),
            USDCConfigurationProvider.GetDefault(ChainName.Testnet) with { JsonRpcUri = new Uri("https://ethereum-sepolia-rpc.publicnode.com") },
            USDCConfigurationProvider.GetPolygonDefault(ChainName.Mainnet),
            USDCConfigurationProvider.GetPolygonDefault(ChainName.Testnet)
        };
        foreach (var network in networks)
        {
            await Rpc("anvil_reset", new { forking = new { jsonRpcUrl = network.JsonRpcUri.AbsoluteUri } });
            var config = network with { JsonRpcUri = endpoint };
            await Rpc("anvil_setChainId", config.ChainId);
            var configuration = new USDCPluginConfiguration { EVMUSDCLikeConfigurationItems = new() { [config.GetPaymentMethodId()] = config } };
            var rpc = new EVMUSDCRPCProvider(configuration, new EventAggregator(new Logs()), null!, new ClientFactory());
            var pmi = config.GetPaymentMethodId();
            await rpc.ValidateNetworkAsync(pmi);
            var web3 = rpc.GetWeb3Client(pmi);
            var forkHeight = await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();
            var contract = web3.Eth.GetContract(Abi, config.SmartContractAddress);
            var minter = await contract.GetFunction("masterMinter").CallAsync<string>();
            var sender = "0x" + Guid.NewGuid().ToString("N") + "12345678";
            var destination = "0x" + Guid.NewGuid().ToString("N") + "87654321";
            foreach (var account in new[] { minter, sender })
            {
                await Rpc("anvil_impersonateAccount", account);
                await Rpc("anvil_setBalance", account, "0x56bc75e2d63100000");
            }
            async Task<TransactionReceipt> Send(string from, string data)
            {
                var hash = (await Rpc("eth_sendTransaction", new { from, to = config.SmartContractAddress, data, gas = "0xf4240" })).Value<string>()!;
                TransactionReceipt? receipt = null;
                await HostSmokeTests.Eventually(async () =>
                    (receipt = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(hash)) is not null);
                Assert.NotNull(receipt);
                Assert.Equal(BigInteger.One, receipt.Status.Value);
                return receipt;
            }
            await Send(minter, contract.GetFunction("configureMinter").GetData(sender, new BigInteger(2000000)));
            await Send(sender, contract.GetFunction("mint").GetData(sender, new BigInteger(2000000)));
            var receipt = await Send(sender, contract.GetFunction("transfer").GetData(destination, new BigInteger(1234567)));
            Assert.Equal(new BigInteger(1234567), await contract.GetFunction("balanceOf").CallAsync<BigInteger>(destination));
            // Advance only the local chain until Anvil reports the transfer block finalized.
            await Rpc("anvil_mine", "0x64");
            Assert.True((await EVMUSDCRPCProvider.GetFinalizedHeight(web3)).Value >= receipt.BlockNumber.Value);
            var block = await web3.Eth.Blocks.GetBlockWithTransactionsByNumber.SendRequestAsync(new BlockParameter(receipt.BlockNumber));
            var listener = new ForkListener(rpc, configuration);
            var payment = Assert.Single(await listener.Scan(pmi, block, destination));
            Assert.Equal(1.234567m, USDCAmounts.FromBaseUnits(payment.Amount, 6));
            Assert.Contains(receipt.TransactionHash[2..], payment.Id);
            Assert.EndsWith($"-USDC-{config.ChainId}-{config.SmartContractAddress.ToLowerInvariant()}", payment.Id);
            Assert.Equal(payment, Assert.Single(await listener.Scan(pmi, block, destination)));
            output.WriteLine($"{pmi}: fork={forkHeight.Value}; local transfer={receipt.TransactionHash}; block={receipt.BlockNumber.Value}; amount=1.234567; finalized and replay verified.");
        }
    }

    private sealed class ClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class ForkListener(EVMUSDCRPCProvider rpc, USDCPluginConfiguration config)
        : EVMUSDCListener(null!, null!, rpc, config, null!, null!, NullLogger<EVMUSDCListener>.Instance, null!, null!)
    {
        public async Task<(BigInteger Amount, string Id)[]> Scan(PaymentMethodId pmi, BlockWithTransactions block, string destination)
        {
            var matches = await GetTransfersAsync(pmi, block, new Dictionary<string, InvoiceEntity> { [destination] = null! }, CancellationToken.None);
            return matches.Select(m => (m.TotalAmount, m.TransactionId)).ToArray();
        }
    }
}

public sealed class LocalForkFactAttribute : FactAttribute
{
    public LocalForkFactAttribute([System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1) : base(sourceFilePath, sourceLineNumber)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("USDC_TEST_ANVIL_URL")))
            Skip = "Set USDC_TEST_ANVIL_URL to an isolated local Anvil node for canonical-contract transaction tests.";
    }
}
