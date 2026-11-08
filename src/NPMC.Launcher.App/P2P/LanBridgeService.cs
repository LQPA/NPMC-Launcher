using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NPMC.Launcher.App.P2P;

/// <summary>
/// 启动器内的虚拟 LAN 桥：
/// 从大厅拉取房间节点，为每个远程节点建本地 TCP 代理，
/// 并向本机 Minecraft 的 LAN 发现组播地址发送伪造 MOTD。
/// </summary>
public sealed class LanBridgeService : IAsyncDisposable
{
    private const string MulticastGroup = "224.0.2.60";
    private const int DiscoveryPort = 4445;

    private readonly string _serverUrl;
    private readonly string? _thumbprint;
    private readonly string _token;
    private readonly string _roomId;
    private readonly string _selfDeviceId;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, LanProxy> _proxies = new(StringComparer.Ordinal);
    private Task? _loop;

    public LanBridgeService(
        string serverUrl,
        string? thumbprint,
        string token,
        string roomId,
        string selfDeviceId,
        Action<string> log)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _thumbprint = thumbprint;
        _token = token;
        _roomId = roomId;
        _selfDeviceId = selfDeviceId;
        _log = log;
    }

    public void Start()
    {
        _loop = Task.Run(() => RunAsync(_cts.Token));
        _log($"[lan] bridge started room={_roomId}");
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var http = CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var json = await http.GetStringAsync(
                    $"{_serverUrl}/api/v1/rooms/{Uri.EscapeDataString(_roomId)}",
                    cancellationToken);
                var hosts = ParseHosts(json);
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var host in hosts)
                {
                    if (string.IsNullOrWhiteSpace(host.DeviceId) ||
                        string.Equals(host.DeviceId, _selfDeviceId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    seen.Add(host.DeviceId);
                    if (_proxies.TryGetValue(host.DeviceId, out var existing))
                    {
                        existing.UpdateTarget(host.Endpoint);
                    }
                    else
                    {
                        var proxy = new LanProxy(host.Name, host.Endpoint, _log);
                        _proxies[host.DeviceId] = proxy;
                        _log($"[lan] advertise {host.Name} -> {host.Endpoint} (local {proxy.LocalPort})");
                    }
                }

                foreach (var stale in _proxies.Keys.Where(key => !seen.Contains(key)).ToArray())
                {
                    await _proxies[stale].DisposeAsync();
                    _proxies.Remove(stale);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log($"[lan] poll failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static List<HostInfo> ParseHosts(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var hosts = new List<HostInfo>();

        if (root.TryGetProperty("hosts", out var hostsElement) && hostsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in hostsElement.EnumerateArray())
            {
                hosts.Add(new HostInfo(
                    item.TryGetProperty("device_id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("name", out var name) ? name.GetString() ?? "NPMC Host" : "NPMC Host",
                    item.TryGetProperty("endpoint", out var endpoint) ? endpoint.GetString() ?? string.Empty : string.Empty));
            }
        }

        if (hosts.Count == 0 &&
            root.TryGetProperty("endpoint", out var legacy) &&
            legacy.GetString() is { Length: > 0 } legacyEndpoint)
        {
            hosts.Add(new HostInfo("legacy-host", "NPMC Host", legacyEndpoint));
        }

        return hosts.Where(host => !string.IsNullOrWhiteSpace(host.Endpoint)).ToList();
    }

    private HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(_thumbprint))
        {
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is not null &&
                    string.Equals(certificate.GetCertHashString(), _thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return errors == System.Net.Security.SslPolicyErrors.None;
            };
        }

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch
            {
            }
        }

        foreach (var proxy in _proxies.Values)
        {
            await proxy.DisposeAsync();
        }

        _proxies.Clear();
        _cts.Dispose();
    }

    private sealed record HostInfo(string DeviceId, string Name, string Endpoint);

    private sealed class LanProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _name;
        private readonly Action<string> _log;
        private string _targetEndpoint;
        private Task? _acceptLoop;
        private Task? _advertiseLoop;

        public LanProxy(string name, string targetEndpoint, Action<string> log)
        {
            _name = name;
            _targetEndpoint = targetEndpoint;
            _log = log;
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
            LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
            _advertiseLoop = Task.Run(AdvertiseLoopAsync);
        }

        public int LocalPort { get; }

        public void UpdateTarget(string endpoint) => _targetEndpoint = endpoint;

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    break;
                }

                _ = Task.Run(() => HandleAsync(client, _cts.Token));
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            using (var target = new TcpClient())
            {
                try
                {
                    var (host, port) = ParseEndpoint(_targetEndpoint);
                    await target.ConnectAsync(host, port, cancellationToken);
                    var clientStream = client.GetStream();
                    var targetStream = target.GetStream();
                    var up = clientStream.CopyToAsync(targetStream, cancellationToken);
                    var down = targetStream.CopyToAsync(clientStream, cancellationToken);
                    await Task.WhenAny(up, down);
                }
                catch (Exception ex)
                {
                    _log($"[lan] proxy error: {ex.Message}");
                }
            }
        }

        private async Task AdvertiseLoopAsync()
        {
            var packet = Encoding.UTF8.GetBytes($"[MOTD]{_name}[/MOTD][AD]{LocalPort}[/AD]");
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            udp.EnableBroadcast = true;

            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await udp.SendAsync(
                        packet,
                        new IPEndPoint(IPAddress.Parse(MulticastGroup), DiscoveryPort),
                        _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log($"[lan] advertise failed: {ex.Message}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1500), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private static (string Host, int Port) ParseEndpoint(string endpoint)
        {
            endpoint = endpoint.Trim();
            if (endpoint.StartsWith('['))
            {
                var close = endpoint.IndexOf(']');
                if (close > 0 && close + 2 <= endpoint.Length)
                {
                    return (endpoint[1..close], int.Parse(endpoint[(close + 2)..]));
                }
            }

            var colon = endpoint.LastIndexOf(':');
            return (endpoint[..colon], int.Parse(endpoint[(colon + 1)..]));
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();

            foreach (var task in new[] { _acceptLoop, _advertiseLoop })
            {
                if (task is null)
                {
                    continue;
                }

                try
                {
                    await task;
                }
                catch
                {
                }
            }

            _cts.Dispose();
        }
    }
}
