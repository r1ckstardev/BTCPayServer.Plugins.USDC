using System.Net;
using System.Net.Sockets;
using System.Text;
using BTCPayServer.Plugins.USDC.Configuration;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.USDC.Tests;

// Controllable HTTP RPC fixture for deterministic host/database lifecycle tests.
internal sealed class LocalEvmNode : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _worker;
    private readonly object _gate = new();
    private readonly List<JObject> _logs = [];
    private readonly Dictionary<int, long> _timestamps = [];
    private int _failedRequests;
    public int FailedRequests => Volatile.Read(ref _failedRequests);
    public int FinalizedHeight { get; set; } = 100;
    public bool FailRequests { get; set; }
    public string Url { get; }
    public LocalEvmNode()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        Url = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _worker = Run();
    }
    public static string Hash(int height) => "0x" + height.ToString("x").PadLeft(64, '0');
    public void SetTimestamp(int height, DateTimeOffset timestamp)
    {
        lock (_gate) _timestamps[height] = timestamp.ToUnixTimeSeconds();
    }
    public void Transfer(int height, string destination, int atomicAmount, int logIndex = 0)
    {
        lock (_gate) _logs.Add(new JObject
        {
            ["address"] = USDCConfigurationProvider.SepoliaContract,
            ["blockNumber"] = "0x" + height.ToString("x"), ["blockHash"] = Hash(height),
            ["transactionHash"] = Hash(height + 100000), ["transactionIndex"] = "0x0",
            ["logIndex"] = "0x" + logIndex.ToString("x"), ["removed"] = false,
            ["data"] = "0x" + atomicAmount.ToString("x").PadLeft(64, '0'),
            ["topics"] = new JArray("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef",
                Hash(42), "0x" + destination[2..].PadLeft(64, '0'))
        });
    }
    private async Task Run()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) when (!_listener.IsListening) { break; }
            try
            {
                if (FailRequests) { Interlocked.Increment(ref _failedRequests); context.Response.StatusCode = 503; continue; }
                using var reader = new StreamReader(context.Request.InputStream);
                var request = JObject.Parse(await reader.ReadToEndAsync());
                var result = Reply(request);
                var bytes = Encoding.UTF8.GetBytes(new JObject { ["jsonrpc"] = "2.0", ["id"] = request["id"], ["result"] = result }.ToString());
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            finally { context.Response.Close(); }
        }
    }
    private JToken Reply(JObject request)
    {
        switch (request["method"]!.Value<string>())
        {
            case "eth_chainId": return "0xaa36a7";
            case "eth_syncing": return false;
            case "eth_blockNumber": return "0x" + FinalizedHeight.ToString("x");
            case "eth_call":
                return request["params"]![0]!["data"]!.Value<string>() == "0x313ce567"
                    ? "0x" + "6".PadLeft(64, '0') : "0x" + new string('0', 64);
            case "eth_getBlockByNumber":
                var tag = request["params"]![0]!.Value<string>()!;
                var height = tag is "finalized" or "latest" ? FinalizedHeight : Convert.ToInt32(tag[2..], 16);
                long timestamp;
                lock (_gate)
                    if (!_timestamps.TryGetValue(height, out timestamp))
                        _timestamps[height] = timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return new JObject { ["number"] = "0x" + height.ToString("x"), ["hash"] = Hash(height),
                    ["timestamp"] = "0x" + timestamp.ToString("x"), ["transactions"] = new JArray() };
            case "eth_getLogs":
                var filter = request["params"]![0]!;
                lock (_gate) return new JArray(_logs.Where(log => log["blockNumber"]!.Value<string>() == filter["fromBlock"]!.Value<string>() &&
                    filter["topics"]![2]!.ToString().Contains(log["topics"]![2]!.Value<string>()!, StringComparison.OrdinalIgnoreCase)).Select(log => log.DeepClone()));
            default: throw new InvalidOperationException($"Unexpected RPC: {request["method"]}");
        }
    }
    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        await _worker;
        _listener.Close();
    }
}
