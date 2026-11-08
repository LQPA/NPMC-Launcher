using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPMC.Launcher.App.P2P;
using NPMC.Launcher.App.ViewModels;

namespace NPMC.Launcher.App;

public partial class P2PTestWindow : UserControl
{
    // 临时联机服务器：当前主机的固定 IPv6。以后换云端只改这两个常量。
    private const string ServerBaseUrl = "https://[240e:878:440:1686:1848:e8b3:98fc:762e]:8443";
    private const string ServerThumbprint = "902C01E179E4CEE6A4B6C2C4F6E08967B51AB2D8";
    private const string ServerKey = "dev-verify-key";
    private const string TermsVersion = "1";

    private const string RoomId = "room-auth";
    private const string NodeId = "node-auth";
    private const string ProfileId = "profile-auth";
    private const string GameSessionId = "session-auth";

    private readonly string _root = FindRepoRoot();
    private readonly List<Process> _processes = new();
    private Process? _gatewayProcess;
    private Process? _hostProcess;
    private Process? _joinProcess;
    private Process? _agentHostProcess;
    private string? _agentHostRoom;
    private string? _agentContextPath;
    private LanBridgeService? _lanBridge;
    private CancellationTokenSource? _hostWatchCts;
    private CancellationTokenSource? _roomRefreshCts;
    private int _lastPublishedPort;

    private bool Portable =>
        Directory.Exists(Path.Combine(_root, "data")) &&
        (Directory.Exists(Path.Combine(_root, "minecraft")) ||
         Directory.Exists(Path.Combine(_root, "cli")));

    private string DataArg => Portable ? "data" : ".data";

    private string CliPath => Portable
        ? Path.Combine(_root, "cli", "npmc.exe")
        : Path.Combine(_root, "src", "NPMC.Launcher.Cli", "bin", "Release", "net8.0-windows", "npmc.exe");

    private string AgentPath => Portable
        ? Path.Combine(_root, "agent", "nmpc-p2p-agent.jar")
        : Path.Combine(_root, "tools", "p2p-agent", "out", "nmpc-p2p-agent.jar");

    private string AuthHostPath => Portable
        ? Path.Combine(_root, "gateway", "NPMC.P2P.AuthHost.exe")
        : Path.Combine(_root, "tools", "p2p-auth-host", "bin", "Release", "net8.0-windows", "NPMC.P2P.AuthHost.exe");

    private string LoopbackAgentPath => Portable
        ? Path.Combine(_root, "agent", "nmpc-loopback-agent.jar")
        : Path.Combine(_root, "tools", "p2p-loopback-agent", "stage", "nmpc-loopback-agent.jar");

    private string OfflineAgentPath => Portable
        ? Path.Combine(_root, "agent", "gate0a-offline-agent.jar")
        : Path.Combine(_root, "tools", "p2p-gate0a-agent", "out", "gate0a-offline-agent.jar");

    private string GameLogPath
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(_root, "minecraft", "instances", "26.1.2", "logs", "latest.log"),
                Path.Combine(_root, DataArg, "instances", "26.1.2", "logs", "latest.log"),
                Path.Combine(_root, ".data", "instances", "26.1.2", "logs", "latest.log"),
            };

            return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        }
    }

    public P2PTestWindow()
    {
        InitializeComponent();
        CleanupStaleAgentHosts();
        DetectEndpoint();
        InitializePanels();
    }

    /// <summary>
    /// 上一次启动器退出不完整时，可能残留多个 agent-host，导致游戏抓到旧管道。
    /// 只清理同目录的 npmc，不动用户手动启动的 Gateway/CLI。
    /// </summary>
    private void CleanupStaleAgentHosts()
    {
        var expected = Path.GetFullPath(CliPath);
        foreach (var process in Process.GetProcessesByName("npmc"))
        {
            try
            {
                var actual = Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
                if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill();
                }
            }
            catch
            {
            }
        }
    }

    private string SessionPath => Path.Combine(_root, DataArg, "p2p", "session.json");

    private string TermsPath => Path.Combine(_root, DataArg, "p2p", "terms.txt");

    private void InitializePanels()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TermsPath)!);
        }
        catch
        {
        }

        var accepted = File.Exists(TermsPath);
        TermsPanel.IsVisible = !accepted;
        EntryPanel.IsVisible = accepted;
        RoomPanel.IsVisible = false;
        LoadDeviceSession();

        if (accepted && !File.Exists(SessionPath))
        {
            _ = AutoRegisterAsync();
        }
        else if (File.Exists(SessionPath))
        {
            _ = ValidateSessionIdentityAsync();
        }
    }

    private void DetectEndpoint()
    {
        var addresses = FindGlobalAddresses();
        // 主机入口优先使用稳定地址；Windows 隐私临时地址会轮换，
        // 且可能与用户在 ipconfig / 网络设置里看到的地 址不一致。
        var address = addresses.Stable ?? addresses.Temporary;
        if (!string.IsNullOrWhiteSpace(address))
        {
            EndpointBox.Text = $"[{address}]:25566";
        }
    }

    private void LoadDeviceSession()
    {
        try
        {
            if (!File.Exists(SessionPath))
            {
                DeviceStatusBlock.Text = "设备：未注册";
                DeviceIdBlock.Text = "device_id: -";
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(SessionPath));
            var root = document.RootElement;
            var deviceId = root.TryGetProperty("device_id", out var id) ? id.GetString() : null;
            var terms = root.TryGetProperty("terms_version", out var version) ? version.GetString() : null;
            var expires = root.TryGetProperty("expires_at", out var exp) ? exp.GetString() : null;

            DeviceStatusBlock.Text = "设备：已注册" +
                                     (string.IsNullOrWhiteSpace(expires) ? string.Empty : $"（凭证到期 {expires}）");
            DeviceIdBlock.Text = $"device_id: {deviceId ?? "-"}";
            TermsCheck.IsChecked = string.Equals(terms, TermsVersion, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            DeviceStatusBlock.Text = $"设备：读取 session 失败：{ex.Message}";
        }
    }

    private async void OnAcceptTermsClick(object? sender, RoutedEventArgs e)
    {
        if (TermsCheck.IsChecked != true)
        {
            Log("请先勾选并同意条款");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(TermsPath)!);
        await File.WriteAllTextAsync(TermsPath, TermsVersion);
        TermsPanel.IsVisible = false;
        EntryPanel.IsVisible = true;
        await RegisterDeviceAsync();
    }

    private async Task<bool> RegisterDeviceAsync()
    {
        var code = await RunProcessAsync(
            CliPath,
            new[]
            {
                "p2p", "enroll",
                "--server", ServerBaseUrl,
                "--thumbprint", ServerThumbprint,
                "--root", DataArg,
                "--terms", TermsVersion,
            },
            "enroll");

        if (code != 0)
        {
            return false;
        }

        LoadDeviceSession();
        return true;
    }

    private async Task AutoRegisterAsync()
    {
        if (File.Exists(SessionPath))
        {
            return;
        }

        await RegisterDeviceAsync();
    }

    /// <summary>
    /// session 文件可能随便携包被复制到另一台设备；必须用本机 TPM 重新核对 device_id，
    /// 不一致时丢弃旧 session 并自动重新注册（条款已接受，不再弹条款）。
    /// </summary>
    private async Task ValidateSessionIdentityAsync()
    {
        var sessionDeviceId = ReadDeviceId();
        if (string.IsNullOrWhiteSpace(sessionDeviceId))
        {
            return;
        }

        var checkFile = Path.Combine(_root, DataArg, "p2p", "device-id.txt");
        try
        {
            File.Delete(checkFile);
        }
        catch
        {
        }

        var code = await RunProcessAsync(
            CliPath,
            new[] { "p2p", "device-id", "--out", checkFile },
            "device-id");

        if (code == 2)
        {
            // 本机没有对应的 TPM 私钥：session 必定是从别的设备复制来的。
            ClearForeignSession(sessionDeviceId, "本机 TPM 中没有该身份的私钥");
            return;
        }

        if (code != 0 || !File.Exists(checkFile))
        {
            return;
        }

        var localDeviceId = (await File.ReadAllTextAsync(checkFile)).Trim();
        try
        {
            File.Delete(checkFile);
        }
        catch
        {
        }

        if (!string.IsNullOrWhiteSpace(localDeviceId) &&
            !string.Equals(localDeviceId, sessionDeviceId, StringComparison.Ordinal))
        {
            ClearForeignSession(sessionDeviceId, "session 与本机 TPM 公钥不一致");
        }
    }

    private void ClearForeignSession(string sessionDeviceId, string reason)
    {
        try
        {
            File.Delete(SessionPath);
        }
        catch
        {
            return;
        }

        Log($"[identity] {reason}；已清除 session（{sessionDeviceId}）");
        LoadDeviceSession();
        if (File.Exists(TermsPath))
        {
            _ = RegisterDeviceAsync();
        }
    }

    private void OnRefreshDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (!File.Exists(SessionPath))
        {
            _ = RegisterDeviceAsync();
            return;
        }

        LoadDeviceSession();
    }

    private void OnShowCreateFormClick(object? sender, RoutedEventArgs e)
    {
        CreateForm.IsVisible = true;
        JoinForm.IsVisible = false;
    }

    private void OnShowJoinFormClick(object? sender, RoutedEventArgs e)
    {
        JoinForm.IsVisible = true;
        CreateForm.IsVisible = false;
    }

    private async void OnCreateRoomClick(object? sender, RoutedEventArgs e)
    {
        var token = ReadToken();
        if (token is null)
        {
            return;
        }

        var password = CreatePasswordBox.Text;
        if (string.IsNullOrWhiteSpace(password))
        {
            Log("创建房间必须设置密码");
            return;
        }

        var requestedRoom = CreateRoomCodeBox.Text?.Trim();
        try
        {
            using var http = CreateHttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ServerBaseUrl}/api/v1/rooms")
            {
                Content = JsonContent.Create(new
                {
                    room_id = string.IsNullOrWhiteSpace(requestedRoom) ? null : requestedRoom,
                    password,
                    name = Environment.MachineName,
                    node_id = NodeId,
                    profile_id = ProfileId,
                    game_session_id = GameSessionId,
                }),
            };
            request.Headers.Add("Authorization", $"Bearer {token}");

            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Log($"创建房间失败：{(int)response.StatusCode} {text}");
                return;
            }

            using var document = JsonDocument.Parse(text);
            RoomCodeBox.Text = document.RootElement.GetProperty("room_id").GetString();
            CreateForm.IsVisible = false;
            EntryPanel.IsVisible = false;
            RoomPanel.IsVisible = true;
            RoomStatusBlock.Text = $"房间：{RoomCodeBox.Text}（已创建）";
            Log($"房间已创建：{RoomCodeBox.Text}");
            StartBridgeIfReady();
        }
        catch (Exception ex)
        {
            Log($"创建房间失败：{ex.Message}");
        }
    }

    private async void OnJoinRoomClick(object? sender, RoutedEventArgs e)
    {
        var token = ReadToken();
        if (token is null)
        {
            return;
        }

        var room = JoinRoomCodeBox.Text?.Trim();
        var password = JoinPasswordBox.Text;
        if (string.IsNullOrWhiteSpace(room) || string.IsNullOrWhiteSpace(password))
        {
            Log("加入房间需要房间号和密码");
            return;
        }

        try
        {
            using var http = CreateHttpClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{ServerBaseUrl}/api/v1/rooms/{Uri.EscapeDataString(room)}/join")
            {
                Content = JsonContent.Create(new { password, name = Environment.MachineName }),
            };
            request.Headers.Add("Authorization", $"Bearer {token}");
            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Log($"加入房间失败：{(int)response.StatusCode} {text}");
                return;
            }

            RoomCodeBox.Text = room;
            JoinForm.IsVisible = false;
            EntryPanel.IsVisible = false;
            RoomPanel.IsVisible = true;
            RoomStatusBlock.Text = $"房间：{room}（已加入）";
            ApplyRoom(text);
            StartBridgeIfReady();
        }
        catch (Exception ex)
        {
            Log($"加入房间失败：{ex.Message}");
        }
    }

    private void OnLeaveRoomClick(object? sender, RoutedEventArgs e)
    {
        _roomRefreshCts?.Cancel();
        StopAgentHost();
        _ = StopBridgeAsync();
        RoomPanel.IsVisible = false;
        EntryPanel.IsVisible = true;
        CreateForm.IsVisible = false;
        JoinForm.IsVisible = false;
        MemberList.ItemsSource = null;
        RoomCodeBox.Text = string.Empty;
        EndpointBox.Text = string.Empty;
        RoomStatusBlock.Text = "已离开房间";
    }

    private void OnFixFirewallClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var script =
                "New-NetFirewallRule -DisplayName 'NPMC P2P TCP 25566' " +
                "-Direction Inbound -Protocol TCP -LocalPort 25566 -Action Allow " +
                "-Profile Any -ErrorAction SilentlyContinue";
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
            };
            Process.Start(psi);
            Log("已请求管理员权限：添加 TCP 25566 入站规则");
        }
        catch (Exception ex)
        {
            Log($"添加防火墙规则失败：{ex.Message}");
        }
    }

    private async void OnRefreshRoomClick(object? sender, RoutedEventArgs e) => await RefreshRoomAsync();

    private async Task RefreshRoomAsync()
    {
        var token = ReadToken();
        var room = RoomCodeBox.Text?.Trim();
        if (token is null || string.IsNullOrWhiteSpace(room))
        {
            return;
        }

        try
        {
            using var http = CreateHttpClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{ServerBaseUrl}/api/v1/rooms/{Uri.EscapeDataString(room)}");
            request.Headers.Add("Authorization", $"Bearer {token}");
            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                ApplyRoom(text);
            }
            else
            {
                Log($"刷新房间失败：{(int)response.StatusCode} {text}");
            }
        }
        catch (Exception ex)
        {
            Log($"刷新房间失败：{ex.Message}");
        }
    }

    private void ApplyRoom(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // 房间里的 endpoint 是“某个远端主机”的入口，不能写进本机 EndpointBox。
        // 否则 B 加入 A 的房间后，B 开 LAN 会把 A 的地址当成自己的上报。
        UpdateMembers(root);
        RoomStatusBlock.Text = $"房间：{RoomCodeBox.Text}（已连接）";
    }

    private void UpdateMembers(JsonElement root)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("member_names", out var memberNames) &&
            memberNames.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in memberNames.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    names[property.Name] = property.Value.GetString()!;
                }
            }
        }

        if (root.TryGetProperty("hosts", out var hosts) && hosts.ValueKind == JsonValueKind.Array)
        {
            foreach (var host in hosts.EnumerateArray())
            {
                var id = host.TryGetProperty("device_id", out var idElement) ? idElement.GetString() : null;
                var name = host.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id) && !names.ContainsKey(id))
                {
                    names[id] = string.IsNullOrWhiteSpace(name) ? id : name;
                }
            }
        }

        var members = new List<string>();
        if (root.TryGetProperty("members", out var memberElements) && memberElements.ValueKind == JsonValueKind.Array)
        {
            foreach (var member in memberElements.EnumerateArray())
            {
                var id = member.GetString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                members.Add(names.TryGetValue(id, out var name) ? name : id);
            }
        }

        MemberList.ItemsSource = members;
    }

    private void StartRoomRefresh(string room, string token)
    {
        _roomRefreshCts?.Cancel();
        _roomRefreshCts = new CancellationTokenSource();
        var cancellationToken = _roomRefreshCts.Token;

        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var http = CreateHttpClient();
                    using var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        $"{ServerBaseUrl}/api/v1/rooms/{Uri.EscapeDataString(room)}");
                    request.Headers.Add("Authorization", $"Bearer {token}");
                    using var response = await http.SendAsync(request, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        var text = await response.Content.ReadAsStringAsync(cancellationToken);
                        Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                ApplyRoom(text);
                            }
                            catch
                            {
                            }
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, cancellationToken);
    }

    private async void OnPublishHostClick(object? sender, RoutedEventArgs e)
    {
        await PublishHostAsync();
    }

    private async Task PublishHostAsync()
    {
        // 主机入口永远取本机当前地址，不能沿用房间里的远端 endpoint。
        DetectEndpoint();

        var token = ReadToken();
        var room = RoomCodeBox.Text?.Trim();
        var endpoint = EndpointBox.Text?.Trim();
        if (token is null || string.IsNullOrWhiteSpace(room) || string.IsNullOrWhiteSpace(endpoint))
        {
            Log("上报主机入口需要房间码和主机入口");
            return;
        }

        try
        {
            using var http = CreateHttpClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{ServerBaseUrl}/api/v1/rooms/{Uri.EscapeDataString(room)}/host")
            {
                Content = JsonContent.Create(new
                {
                    endpoint,
                    name = Environment.MachineName,
                    node_id = NodeId,
                    profile_id = ProfileId,
                    game_session_id = GameSessionId,
                }),
            };
            request.Headers.Add("Authorization", $"Bearer {token}");

            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            Log($"上报主机入口 {(int)response.StatusCode}: {text}");
            StartBridgeIfReady();
        }
        catch (Exception ex)
        {
            Log($"上报主机入口失败：{ex.Message}");
        }
    }

    private void OnStartHostClick(object? sender, RoutedEventArgs e)
    {
        var args = new List<string>
        {
            "launch", "26.1.2",
            "--instance", "26.1.2",
            "--root", DataArg,
            "--world", string.IsNullOrWhiteSpace(WorldBox.Text) ? "New World" : WorldBox.Text!.Trim(),
            "--p2p-host",
            "--p2p-loopback-agent", LoopbackAgentPath,
            "--p2p-offline-agent", OfflineAgentPath,
        };

        _hostProcess = StartProcess(
            CliPath,
            args,
            "host-game");

        BeginHostWatch();
    }

    private void OnStartGatewayClick(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(LanPortBox.Text, out var lanPort))
        {
            Log("LAN 端口无效");
            return;
        }

        StartGateway(lanPort);
    }

    private void StartGateway(int lanPort)
    {
        if (_gatewayProcess is { HasExited: false })
        {
            return;
        }

        if (IsLocalPortOpen(25566))
        {
            // 25566 可能由用户手动启动的 Gateway 占用；启动器不应误杀它。
            Log("[gateway] 25566 already listening; reuse existing process");
            return;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var args = new List<string>
        {
            "--target", lanPort.ToString(),
            "--room", RoomId,
            "--node", NodeId,
            "--profile", ProfileId,
            "--session", GameSessionId,
            "--listen", "[::]:25566",
            "--game-log", GameLogPath,
            "--trace", Path.Combine(_root, DataArg, "p2p", "gate0d", $"gui-{stamp}.jsonl"),
            "--duration", "28800",
            "--verify-url", ServerBaseUrl,
            "--verify-thumbprint", ServerThumbprint,
            "--verify-key", ServerKey,
        };

        _gatewayProcess = StartProcess(
            AuthHostPath,
            args,
            "gateway");
    }

    private void BeginHostWatch()
    {
        if (_hostWatchCts is { IsCancellationRequested: false })
        {
            return;
        }

        _hostWatchCts = new CancellationTokenSource();
        var token = _hostWatchCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(GameLogPath))
                    {
                        await using var stream = new FileStream(
                            GameLogPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(stream);
                        var text = await reader.ReadToEndAsync(token);
                        var matches = System.Text.RegularExpressions.Regex.Matches(
                            text,
                            @"Started serving on (\d+)");
                        if (matches.Count > 0 &&
                            int.TryParse(matches[^1].Groups[1].Value, out var port) &&
                            port != _lastPublishedPort &&
                            IsLocalPortOpen(port))
                        {
                            _lastPublishedPort = port;
                            Dispatcher.UIThread.Post(async () =>
                            {
                                try
                                {
                                    LanPortBox.Text = port.ToString();
                                    StartGateway(port);
                                    await PublishHostAsync();
                                }
                                catch (Exception ex)
                                {
                                    Log($"host publish: {ex.Message}");
                                }
                            });
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"host watch: {ex.Message}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    private static bool IsLocalPortOpen(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var task = client.ConnectAsync(IPAddress.Loopback, port);
            return task.Wait(TimeSpan.FromMilliseconds(300)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private void OnJoinGameClick(object? sender, RoutedEventArgs e)
    {
        var token = ReadToken();
        var room = RoomCodeBox.Text?.Trim();
        if (token is null || string.IsNullOrWhiteSpace(room))
        {
            Log("加入游戏需要先注册并加入房间");
            return;
        }

        StartBridgeIfReady();

        if (_joinProcess is { HasExited: false })
        {
            Log("联机客户端已经在运行；请在多人游戏里刷新 LAN 列表");
            return;
        }

        var args = new List<string>
        {
            "launch", "26.1.2",
            "--instance", "gate0a-joiner",
            "--root", DataArg,
            "--account", "LQPA_",
            "--p2p-auth",
            "--p2p-agent", AgentPath,
            "--p2p-room", RoomId,
            "--p2p-node", NodeId,
            "--p2p-profile", ProfileId,
            "--p2p-session", GameSessionId,
        };

        _joinProcess = StartProcess(
            CliPath,
            args,
            "join-game");
    }

    private void OnStopAllClick(object? sender, RoutedEventArgs e)
    {
        _hostWatchCts?.Cancel();
        _roomRefreshCts?.Cancel();
        StopAgentHost();
        _ = StopBridgeAsync();
        StopProcess(ref _gatewayProcess, "gateway");
        StopProcess(ref _hostProcess, "host-game");
        StopProcess(ref _joinProcess, "join-game");

        lock (_processes)
        {
            foreach (var process in _processes.ToArray())
            {
                Kill(process);
            }

            _processes.Clear();
        }
    }

    /// <summary>主窗口关闭时调用；确保 agent-host / Gateway / CLI 不残留。</summary>
    public void StopAllForShutdown()
    {
        _hostWatchCts?.Cancel();
        _roomRefreshCts?.Cancel();
        StopAgentHost();
        StopProcess(ref _gatewayProcess, "gateway");
        StopProcess(ref _hostProcess, "host-game");
        StopProcess(ref _joinProcess, "join-game");

        lock (_processes)
        {
            foreach (var process in _processes.ToArray())
            {
                Kill(process);
            }

            _processes.Clear();
        }

        // 只清理本启动器自己启动并记录在 _processes 里的子进程；
        // 不按进程名全局清理，避免误杀用户手动启动的 Gateway/CLI。
        if (_lanBridge is not null)
        {
            _ = _lanBridge.DisposeAsync();
            _lanBridge = null;
        }
    }

    private void StartBridgeIfReady()
    {
        if (_lanBridge is not null)
        {
            return;
        }

        var token = ReadToken();
        var room = RoomCodeBox.Text?.Trim();
        var deviceId = ReadDeviceId();
        if (token is null || string.IsNullOrWhiteSpace(room) || string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        _lanBridge = new LanBridgeService(ServerBaseUrl, ServerThumbprint, token, room, deviceId, Log);
        _lanBridge.Start();
        StartRoomRefresh(room, token);
        BeginHostWatch();
        StartAgentHost(room);
    }

    /// <summary>
    /// 房间激活后启动本机 Agent Host：它持有 TPM 私钥和命名管道。
    /// 普通实例启动读取 agent.json 后自动注入 javaagent，用户不需要从联机页启动游戏。
    /// </summary>
    private string AgentContextPath =>
        DataContext is MainWindowViewModel vm
            ? Path.Combine(vm.P2PContextDirectory, "agent.json")
            : Path.Combine(_root, DataArg, "p2p", "agent.json");

    private string ActiveRoomPath => Path.Combine(Path.GetDirectoryName(AgentContextPath)!, "active-room.txt");

    private void StartAgentHost(string room)
    {
        if (_agentHostProcess is { HasExited: false } &&
            string.Equals(_agentHostRoom, room, StringComparison.Ordinal))
        {
            return;
        }

        StopAgentHost();
        var contextPath = AgentContextPath;
        _agentContextPath = contextPath;
        try
        {
            File.Delete(contextPath);
            Directory.CreateDirectory(Path.GetDirectoryName(contextPath)!);
            File.WriteAllText(ActiveRoomPath, room);
        }
        catch
        {
        }

        _agentHostProcess = StartProcess(
            CliPath,
            new[]
            {
                "p2p", "agent-host",
                "--root", DataArg,
                "--out", contextPath,
                "--room", room,
                "--node", NodeId,
                "--profile", ProfileId,
                "--session", GameSessionId,
                "--agent", AgentPath,
                "--loopback", LoopbackAgentPath,
                "--offline", OfflineAgentPath,
            },
            "agent-host");
        _agentHostRoom = room;
        Log($"[agent-host] context={contextPath}");

        // 等待 agent.json 落盘：用户看到“房间已连接”后立刻启动游戏时，
        // 实例启动必须已经能读到注入参数，不能靠运气抢时序。
        for (var i = 0; i < 40; i++)
        {
            if (File.Exists(contextPath))
            {
                break;
            }

            Thread.Sleep(50);
        }
    }

    private void StopAgentHost()
    {
        StopProcess(ref _agentHostProcess, "agent-host");
        _agentHostRoom = null;
        try
        {
            File.Delete(_agentContextPath ?? AgentContextPath);
            File.Delete(ActiveRoomPath);
        }
        catch
        {
        }

        _agentContextPath = null;
    }

    private async Task StopBridgeAsync()
    {
        if (_lanBridge is null)
        {
            return;
        }

        await _lanBridge.DisposeAsync();
        _lanBridge = null;
        Log("[lan] bridge stopped");
    }

    private string? ReadDeviceId()
    {
        try
        {
            if (!File.Exists(SessionPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(SessionPath));
            return document.RootElement.TryGetProperty("device_id", out var device)
                ? device.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private string? ReadToken()
    {
        try
        {
            if (!File.Exists(SessionPath))
            {
                Log("设备未注册，请先同意条款并注册");
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(SessionPath));
            return document.RootElement.TryGetProperty("token", out var token)
                ? token.GetString()
                : null;
        }
        catch (Exception ex)
        {
            Log($"读取设备凭证失败：{ex.Message}");
            return null;
        }
    }

    private HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is not null &&
                string.Equals(certificate.GetCertHashString(), ServerThumbprint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return errors == SslPolicyErrors.None;
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    private Process? StartProcess(string file, IReadOnlyCollection<string> args, string label)
    {
        if (!File.Exists(file))
        {
            Log($"[{label}] missing: {file}");
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo(file)
            {
                WorkingDirectory = _root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log($"[{label}] {e.Data}"); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log($"[{label}] {e.Data}"); };
            process.Exited += (_, _) => Log($"[{label}] exited code={SafeExitCode(process)}");
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_processes)
            {
                _processes.Add(process);
            }

            Log($"[{label}] started pid={process.Id}");
            return process;
        }
        catch (Exception ex)
        {
            Log($"[{label}] start failed: {ex.Message}");
            return null;
        }
    }

    private async Task<int> RunProcessAsync(string file, IReadOnlyCollection<string> args, string label)
    {
        if (!File.Exists(file))
        {
            Log($"[{label}] missing: {file}");
            return 1;
        }

        try
        {
            var psi = new ProcessStartInfo(file)
            {
                WorkingDirectory = _root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log($"[{label}] {e.Data}"); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log($"[{label}] {e.Data}"); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Log($"[{label}] run failed: {ex.Message}");
            return 1;
        }
    }

    private void StopProcess(ref Process? process, string label)
    {
        if (process is null)
        {
            return;
        }

        Kill(process);
        Log($"[{label}] stopped");
        process = null;
    }

    private static void Kill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
        }
    }

    private static int? SafeExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    private static (string? Stable, string? Temporary) FindGlobalAddresses()
    {
        string? stable = null;
        string? temporary = null;

        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up ||
                network.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in network.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetworkV6 ||
                    unicast.Address.IsIPv6LinkLocal ||
                    unicast.Address.IsIPv6SiteLocal ||
                    IPAddress.IsLoopback(unicast.Address))
                {
                    continue;
                }

                var text = unicast.Address.ToString();
                if (unicast.PrefixLength == 128 && temporary is null)
                {
                    temporary = text;
                }
                else if (unicast.PrefixLength == 64 && stable is null)
                {
                    stable = text;
                }
            }
        }

        return (stable, temporary);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NPMC.Launcher.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        // 便携包：exe 同级有 data\，并且有 minecraft\ 或 cli\。
        directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "data")) &&
                (Directory.Exists(Path.Combine(directory.FullName, "minecraft")) ||
                 Directory.Exists(Path.Combine(directory.FullName, "cli"))))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return @"D:\Agents\NPMC-Launcher";
    }

    private void Log(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LogBox.Text = (LogBox.Text ?? string.Empty) + $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            LogBox.CaretIndex = LogBox.Text.Length;
        });
    }
}
