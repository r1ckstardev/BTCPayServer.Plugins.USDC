using System.Net;
using System.Numerics;
using System.Text;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Plugins.USDC.Configuration;
using BTCPayServer.Plugins.USDC.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.USDC.Tests;

public class RpcPaymentTests
{
    private const string Destination = "0x1111111111111111111111111111111111111111";
    private const string Sender = "0x2222222222222222222222222222222222222222";
    private static readonly string Hash = "0x" + new string('a', 64);
    private static readonly string BlockHash = "0x" + new string('b', 64);

    [Theory]
    [InlineData(400000, 0.4)]
    [InlineData(1000000, 1)]
    [InlineData(1200000, 1.2)]
    public async Task RpcTransferDetectionPreservesPartialExactAndOverpaymentAmounts(int atomic, double expected)
    {
        var (rpc, listener, transport) = Create();
        transport.Logs = new JArray(Log(1, atomic));
        var match = Assert.Single(await listener.Scan());
        Assert.Equal((decimal)expected, USDCAmounts.FromBaseUnits(match.Amount, 6));
        Assert.Equal(Destination, match.Destination);
        Assert.EndsWith("-USDC-11155111", match.Id);
        Assert.Equal(1, transport.LogRequests);
        Assert.Equal("0x64", transport.LastFilter!["fromBlock"]!.Value<string>());
        Assert.Equal("0x64", transport.LastFilter["toBlock"]!.Value<string>());
        Assert.Contains(USDCConfigurationProvider.SepoliaContract, transport.LastFilter["address"]!.ToString());
        Assert.Contains(Destination[2..], transport.LastFilter["topics"]!.ToString());
    }

    [Fact]
    public async Task MultipleLogsInOneTransactionAreDistinctAndReplayIsStable()
    {
        var (_, listener, transport) = Create();
        transport.Logs = new JArray(Log(7, 400000), Log(8, 600000), Log(7, 400000));
        var matches = await listener.Scan();
        Assert.Equal(2, matches.Length);
        Assert.Equal(new BigInteger(1_000_000), matches.Aggregate(BigInteger.Zero, (sum, m) => sum + m.Amount));
        Assert.Equal(2, matches.Select(m => m.Id).Distinct().Count());
        Assert.Equal(matches, await listener.Scan());
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("contract")]
    [InlineData("recipient")]
    [InlineData("blockHash")]
    [InlineData("blockNumber")]
    [InlineData("logIndex")]
    [InlineData("zero")]
    public async Task InvalidOrUnrelatedLogsCannotCreditAnInvoice(string reason)
    {
        var (_, listener, transport) = Create();
        var log = Log(1, 1000000);
        switch (reason)
        {
            case "removed": log["removed"] = true; break;
            case "contract": log["address"] = Sender; break;
            case "recipient": log["topics"]![2] = "0x" + Sender[2..].PadLeft(64, '0'); break;
            case "blockHash": log["blockHash"] = Hash; break;
            case "blockNumber": log["blockNumber"] = "0x63"; break;
            case "logIndex": log["logIndex"] = null; break;
            case "zero": log["data"] = "0x" + new string('0', 64); break;
        }
        transport.Logs = new JArray(log);
        Assert.Empty(await listener.Scan());
    }

    [Theory]
    [InlineData(1, 6)]
    [InlineData(11155111, 18)]
    public async Task WrongNetworkOrDecimalsFailBeforeReadingLogs(int chain, int decimals)
    {
        var (_, listener, transport) = Create();
        transport.ChainId = chain;
        transport.Decimals = decimals;
        await Assert.ThrowsAsync<InvalidOperationException>(() => listener.Scan());
        Assert.Equal(0, transport.LogRequests);
    }

    [Fact]
    public async Task FinalizedHeadIsRequiredWithoutLatestFallback()
    {
        var (rpc, _, transport) = Create();
        var pmi = new PaymentMethodId("USDC-SEPOLIA");
        Assert.Equal(new BigInteger(100), (await EVMUSDCRPCProvider.GetFinalizedHeight(rpc.GetWeb3Client(pmi))).Value);
        Assert.Equal("finalized", transport.LastBlockTag);
        transport.FinalizedAvailable = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => EVMUSDCRPCProvider.GetFinalizedHeight(rpc.GetWeb3Client(pmi)));
    }

    private static JObject Log(int index, int atomic) => new()
    {
        ["address"] = USDCConfigurationProvider.SepoliaContract,
        ["blockNumber"] = "0x64", ["blockHash"] = BlockHash,
        ["transactionHash"] = Hash, ["transactionIndex"] = "0x3",
        ["logIndex"] = "0x" + index.ToString("x"), ["removed"] = false,
        ["data"] = "0x" + atomic.ToString("x").PadLeft(64, '0'),
        ["topics"] = new JArray("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef",
            "0x" + Sender[2..].PadLeft(64, '0'), "0x" + Destination[2..].PadLeft(64, '0'))
    };

    private static (EVMUSDCRPCProvider Rpc, TestListener Listener, Transport Transport) Create()
    {
        var config = USDCConfigurationProvider.GetDefault(ChainName.Testnet);
        var configuration = new USDCPluginConfiguration { EVMUSDCLikeConfigurationItems = new() { [config.GetPaymentMethodId()] = config } };
        var transport = new Transport();
        var rpc = new EVMUSDCRPCProvider(configuration, new EventAggregator(new Logs()), null!, transport);
        return (rpc, new TestListener(rpc, configuration), transport);
    }

    private sealed class TestListener(EVMUSDCRPCProvider rpc, USDCPluginConfiguration configuration)
        : EVMUSDCListener(null!, null!, rpc, configuration, null!, null!, NullLogger<EVMUSDCListener>.Instance, null!, null!)
    {
        public async Task<(string Destination, BigInteger Amount, string Id)[]> Scan()
        {
            var transfers = await GetTransfersAsync(new PaymentMethodId("USDC-SEPOLIA"),
                new BlockWithTransactions { Number = new HexBigInteger(100), BlockHash = BlockHash },
                new Dictionary<string, InvoiceEntity> { [Destination] = null! }, CancellationToken.None);
            return transfers.Select(t => (t.DestinationKey, t.TotalAmount, t.TransactionId)).ToArray();
        }
    }

    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        public int ChainId { get; set; } = 11155111;
        public int Decimals { get; set; } = 6;
        public JArray Logs { get; set; } = new();
        public int LogRequests { get; private set; }
        public JToken? LastFilter { get; private set; }
        public string? LastBlockTag { get; private set; }
        public bool FinalizedAvailable { get; set; } = true;
        public HttpClient CreateClient(string name) => new(this, false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JObject.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            JToken result;
            switch (body["method"]!.Value<string>())
            {
                case "eth_chainId": result = "0x" + ChainId.ToString("x"); break;
                case "eth_call": result = "0x" + Decimals.ToString("x").PadLeft(64, '0'); break;
                case "eth_getLogs":
                    LastFilter = body["params"]![0]!.DeepClone();
                    LogRequests++;
                    result = Logs;
                    break;
                case "eth_getBlockByNumber":
                    LastBlockTag = body["params"]![0]!.Value<string>();
                    result = FinalizedAvailable ? new JObject { ["number"] = "0x64", ["hash"] = BlockHash, ["transactions"] = new JArray() } : JValue.CreateNull();
                    break;
                default: throw new InvalidOperationException($"Unexpected RPC method: {body["method"]}");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JObject { ["jsonrpc"] = "2.0", ["id"] = body["id"], ["result"] = result }.ToString(), Encoding.UTF8, "application/json")
            };
        }
    }
}
