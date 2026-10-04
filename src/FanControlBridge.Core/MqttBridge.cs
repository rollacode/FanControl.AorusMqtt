using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using MQTTnet.Formatter;

namespace FanControlBridge;

public sealed class MqttCredentialStore(string path)
{
    public record Credentials(string Username, string Password);
    public void Save(string username, string password)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Credentials(username, password));
        try
        {
            var protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".new";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(protectedBytes); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public Credentials? Read()
    {
        if (!File.Exists(path)) return null;
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<Credentials>(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

public sealed class MqttBridge(MqttSettings settings, MqttCredentialStore credentials, Controller controller, Diagnostics diagnostics) : IAsyncDisposable
{
    public const string Root = "fancontrol/aorus";
    private string TopicRoot => settings.TopicRoot;
    private readonly IMqttClient client = new MqttFactory().CreateMqttClient();
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<(string Topic, string Payload)> commands = Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private Task? connectionLoop;
    private Task? commandLoop;
    private string connectionState = "Disabled";
    private string commandState = "No command received";
    public string ConnectionState => connectionState;
    public void Start()
    {
        if (!settings.Enabled) return;
        if (!ValidRoot(TopicRoot)) { connectionState = "Invalid MQTT topic prefix"; return; }
        client.ApplicationMessageReceivedAsync += args =>
        {
            var message = args.ApplicationMessage;
            if (message.Retain || message.PayloadSegment.Count > 64) return Task.CompletedTask;
            var payload = Encoding.UTF8.GetString(message.PayloadSegment);
            if (!ValidCommand(message.Topic, payload, TopicRoot) || !commands.Writer.TryWrite((message.Topic, payload)))
                commandState = "Rejected unknown command or full queue";
            return Task.CompletedTask;
        };
        connectionLoop = Task.Run(ConnectLoopAsync); commandLoop = Task.Run(CommandLoopAsync);
    }
    public static bool ValidRoot(string root) => root.Length <= 128 && System.Text.RegularExpressions.Regex.IsMatch(root, "^[a-zA-Z0-9_-]+(/[a-zA-Z0-9_-]+){0,3}$");
    public static bool ValidCommand(string topic, string payload, string root = Root) => ValidRoot(root) &&
        topic == root + "/profile/set" && payload is "Performance" or "Balanced" or "Night";
    public async Task ExecuteCommandAsync(string topic, string payload)
    {
        if (!ValidCommand(topic, payload, TopicRoot)) throw new InvalidOperationException("Unknown MQTT command");
        await controller.CommandAsync("select", payload);
    }
    private static bool Private(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 100 && b[1] is >= 64 and <= 127;
    }
    private async Task ConnectLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected)
                {
                    var secret = credentials.Read();
                    if (secret is null || string.IsNullOrWhiteSpace(secret.Username) || string.IsNullOrEmpty(secret.Password))
                        throw new InvalidOperationException("Broker credentials required");
                    if (settings.Port is < 1 or > 65535 || settings.Host.Length == 0) throw new InvalidOperationException("Broker address required");
                    var host = settings.Host;
                    if (!settings.UseTls)
                    {
                        if (!settings.AllowPlaintextPrivateBroker) throw new InvalidOperationException("Plain MQTT requires explicit private-network approval");
                        var resolved = await Dns.GetHostAddressesAsync(host, stop.Token);
                        if (resolved.Length == 0 || resolved.Any(a => !Private(a))) throw new InvalidOperationException("Plain MQTT allowed only on private/Tailscale/loopback addresses");
                        host = resolved[0].ToString(); // Pin the validated address for this connection.
                    }
                    var options = new MqttClientOptionsBuilder().WithTcpServer(host, settings.Port).WithClientId(settings.ClientId)
                        .WithProtocolVersion(MqttProtocolVersion.V500).WithMaximumPacketSize(4096).WithReceiveMaximum(8).WithTimeout(TimeSpan.FromSeconds(5))
                        .WithCredentials(secret.Username, secret.Password).WithCleanSession()
                        .WithWillTopic(TopicRoot + "/availability").WithWillPayload("offline").WithWillRetain()
                        .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
                    if (settings.UseTls) options.WithTlsOptions(o =>
                    {
                        o.UseTls();
                        if (settings.TlsServerName.Length > 0) o.WithTargetHost(settings.TlsServerName);
                    }); // OS chain and configured hostname validation; never bypass.
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    connectionState = "Connecting";
                    await client.ConnectAsync(options.Build(), timeout.Token);
                    var subscriptions = new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f.WithTopic(TopicRoot + "/profile/set").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)).Build();
                    var result = await client.SubscribeAsync(subscriptions, timeout.Token);
                    if (result.Items.Any(i => (int)i.ResultCode >= 128)) throw new InvalidOperationException("Broker denied command subscriptions");
                    connectionState = "Connected";
                    await PublishAsync("availability", "online", true, timeout.Token);
                }
                await PublishStateAsync(stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch
            {
                connectionState = "Disconnected / broker settings or credentials require checking";
                // Reconnect from a clean session if subscription or transport setup failed.
                if (client.IsConnected) try { await client.DisconnectAsync(); } catch { }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stop.Token); } catch (OperationCanceledException) { break; }
        }
    }
    private async Task CommandLoopAsync()
    {
        try
        {
            await foreach (var command in commands.Reader.ReadAllAsync(stop.Token))
            {
                try { await ExecuteCommandAsync(command.Topic, command.Payload); commandState = "Handled; inspect bridge pending/outcome before treating cooling as applied"; }
                catch (InvalidOperationException ex) { commandState = ex.Message; }
                catch { commandState = "Command failed; inspect local bridge"; }
                try { if (client.IsConnected) await PublishStateAsync(stop.Token); } catch { }
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task PublishAsync(string suffix, string payload, bool retain, CancellationToken cancellation)
    {
        var result = await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(TopicRoot + "/" + suffix).WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(retain).Build(), cancellation);
        if ((int)result.ReasonCode >= 128) throw new InvalidOperationException("Broker denied publication");
    }
    private async Task PublishStateAsync(CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var snapshot = await diagnostics.SnapshotAsync();
        await PublishAsync("state", JsonSerializer.Serialize(new { bridge = snapshot.Bridge, sensors = snapshot.Telemetry,
            liveControlEnabled = snapshot.LiveControlEnabled, controlReady = snapshot.ControlReady,
            commandResult = commandState, sampledAt = DateTimeOffset.UtcNow }, Storage.Json), true, timeout.Token);
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); commands.Writer.TryComplete();
        if (connectionLoop is not null) await connectionLoop;
        if (commandLoop is not null) await commandLoop;
        if (client.IsConnected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await PublishAsync("availability", "offline", true, timeout.Token); await client.DisconnectAsync(cancellationToken: timeout.Token); } catch { }
        }
        client.Dispose(); stop.Dispose();
    }
}
