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
    private volatile bool _stopping;
    public int FailedRequests => Volatile.Read(ref _failedRequests);
    public int FinalizedHeight { get; set; } = 100; // Existing fixtures use this as latest head too.
    public int? FinalityHeightOverride { get; set; }
    public int ChainId { get; set; } = 11155111;
    private readonly Dictionary<int, int> _forks = [];
    private string HashAt(int height) { lock (_gate) return Hash(height + (_forks.Where(f => height >= f.Key).OrderByDescending(f => f.Key).FirstOrDefault().Value * 1000000)); }
    public void ForkFrom(int height, int branch) { lock (_gate) _forks[height] = branch; }
    public void Reinclude(int oldHeight, int newHeight, int indexOffset = 0)
    {
        lock (_gate)
        foreach (var old in _logs.Where(l => l["blockNumber"]!.Value<string>() == "0x" + oldHeight.ToString("x")).ToArray())
        {
            var log = (JObject)old.DeepClone();
            log["blockNumber"] = "0x" + newHeight.ToString("x");
            log["blockHash"] = HashAt(newHeight);
            log["logIndex"] = "0x" + (Convert.ToInt32(log["logIndex"]!.Value<string>()![2..],16) + indexOffset).ToString("x");
            _logs.Add(log);
        }
    }
    public bool FailRequests { get; set; }
    public int? PauseBlockHeight { get; set; }
    public TaskCompletionSource BlockRequestPaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ResumeBlockRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int? ObserveEmptyLogsHeight { get; set; }
    public TaskCompletionSource EmptyLogResponseCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
    public void Transfer(int height, string destination, int atomicAmount, int logIndex = 0, string? contract = null)
    {
        lock (_gate) _logs.Add(new JObject
        {
            ["address"] = contract ?? (ChainId == 80002 ? USDCConfigurationProvider.AmoyContract : USDCConfigurationProvider.SepoliaContract),
            ["blockNumber"] = "0x" + height.ToString("x"), ["blockHash"] = HashAt(height),
            ["transactionHash"] = Hash(height + 100000), ["transactionIndex"] = "0x0",
            ["logIndex"] = "0x" + logIndex.ToString("x"), ["removed"] = false,
            ["data"] = "0x" + atomicAmount.ToString("x").PadLeft(64, '0'),
            ["topics"] = new JArray("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef",
                Hash(42), "0x" + destination[2..].PadLeft(64, '0'))
        });
    }
    private async Task Run()
    {
        while (!_stopping)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) when (_stopping) { break; }
            try
            {
                if (FailRequests) { Interlocked.Increment(ref _failedRequests); context.Response.StatusCode = 503; continue; }
                using var reader = new StreamReader(context.Request.InputStream);
                var request = JObject.Parse(await reader.ReadToEndAsync());
                if (PauseBlockHeight is { } pausedHeight && request["method"]!.Value<string>() == "eth_getBlockByNumber" &&
                    request["params"]![0]!.Value<string>() == "0x" + pausedHeight.ToString("x"))
                {
                    BlockRequestPaused.TrySetResult();
                    await ResumeBlockRequest.Task;
                }
                var result = Reply(request);
                if (ObserveEmptyLogsHeight is { } emptyHeight && result is JArray { Count: 0 } &&
                    request["method"]!.Value<string>() == "eth_getLogs" &&
                    request["params"]![0]!["fromBlock"]!.Value<string>() == "0x" + emptyHeight.ToString("x"))
                    EmptyLogResponseCreated.TrySetResult();
                var bytes = Encoding.UTF8.GetBytes(new JObject { ["jsonrpc"] = "2.0", ["id"] = request["id"], ["result"] = result }.ToString());
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            catch (Exception) when (_stopping) { break; }
            finally { context.Response.Close(); }
        }
    }
    private JToken Reply(JObject request)
    {
        switch (request["method"]!.Value<string>())
        {
            case "eth_chainId": return "0x" + ChainId.ToString("x");
            case "eth_syncing": return false;
            case "eth_blockNumber": return "0x" + FinalizedHeight.ToString("x");
            case "eth_call":
                return request["params"]![0]!["data"]!.Value<string>() == "0x313ce567"
                    ? "0x" + "6".PadLeft(64, '0') : "0x" + new string('0', 64);
            case "eth_getBlockByNumber":
                var tag = request["params"]![0]!.Value<string>()!;
                var height = tag == "finalized" ? FinalityHeightOverride ?? FinalizedHeight : tag == "latest" ? FinalizedHeight : Convert.ToInt32(tag[2..], 16);
                long timestamp;
                lock (_gate)
                    if (!_timestamps.TryGetValue(height, out timestamp))
                        _timestamps[height] = timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return new JObject { ["number"] = "0x" + height.ToString("x"), ["hash"] = HashAt(height), ["parentHash"] = HashAt(height - 1),
                    ["timestamp"] = "0x" + timestamp.ToString("x"), ["transactions"] = new JArray() };
            case "eth_getTransactionReceipt":
                lock (_gate)
                {
                    var receiptLogs = _logs.Where(l => l["transactionHash"]!.Value<string>() == request["params"]![0]!.Value<string>() &&
                        l["blockHash"]!.Value<string>() == HashAt(Convert.ToInt32(l["blockNumber"]!.Value<string>()![2..],16)))
                        .GroupBy(l => l["logIndex"]!.Value<string>()).Select(g => g.First()).OrderBy(l => Convert.ToInt32(l["logIndex"]!.Value<string>()![2..],16)).ToArray();
                    if (receiptLogs.Length == 0) return JValue.CreateNull();
                    return new JObject { ["transactionHash"] = receiptLogs[0]["transactionHash"], ["blockHash"] = receiptLogs[0]["blockHash"],
                        ["blockNumber"] = receiptLogs[0]["blockNumber"], ["status"] = "0x1", ["logs"] = new JArray(receiptLogs.Select(l => l.DeepClone())) };
                }
            case "eth_getLogs":
                var filter = request["params"]![0]!;
                lock (_gate) return new JArray(_logs.Where(log => log["blockNumber"]!.Value<string>() == filter["fromBlock"]!.Value<string>() &&
                    log["blockHash"]!.Value<string>() == HashAt(Convert.ToInt32(log["blockNumber"]!.Value<string>()![2..],16)) &&
                    filter["address"]!.ToString().Contains(log["address"]!.Value<string>()!, StringComparison.OrdinalIgnoreCase) &&
                    filter["topics"]![2]!.ToString().Contains(log["topics"]![2]!.Value<string>()!, StringComparison.OrdinalIgnoreCase)).Select(log => log.DeepClone()));
            default: throw new InvalidOperationException($"Unexpected RPC: {request["method"]}");
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        ResumeBlockRequest.TrySetResult();
        _listener.Stop();
        await _worker;
        _listener.Close();
    }
}
