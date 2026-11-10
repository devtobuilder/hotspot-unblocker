using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

if (args.Contains("--help")) { Console.WriteLine("Hotspot Unblocker [--bind 192.168.137.1] [--port 8080] [--hotspot] [--auto]\n--auto: PAC setup without proxy login, restricted to the bound IPv4 subnet."); return; }
string Option(string key, string fallback) { int i = Array.IndexOf(args, key); return i < 0 ? fallback : i + 1 < args.Length ? args[i + 1] : throw new ArgumentException("Missing " + key); }
try
{
    Log.Initialize(Option("--log", Path.Combine(AppContext.BaseDirectory, "proxy.log")));
    if (args.Contains("--hotspot")) Process.Start(new ProcessStartInfo("ms-settings:network-mobilehotspot") { UseShellExecute = true });
    Console.WriteLine("Enable Windows Mobile hotspot and connect your iPhone to THAT network.\nComplete upstream Wi-Fi login on this PC first.\nAvailable IPv4 addresses:");
    foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        foreach (var ip in adapter.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            Console.WriteLine($"  {adapter.Name}: {ip.Address}");
    var address = IPAddress.Parse(Option("--bind", "192.168.137.1"));
    int port = int.Parse(Option("--port", "8080"));
    bool automatic = args.Contains("--auto");
    var localInterface = NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).FirstOrDefault(a => a.Address.Equals(address));
    if (automatic && (address.AddressFamily != AddressFamily.InterNetwork || localInterface == null ||
        !(IPAddress.IsLoopback(address) || address.GetAddressBytes()[0] == 10 || address.GetAddressBytes()[0] == 192 && address.GetAddressBytes()[1] == 168 || address.GetAddressBytes()[0] == 172 && address.GetAddressBytes()[1] is >= 16 and <= 31)))
        throw new ArgumentException("--auto requires a specific private local IPv4 interface (not 0.0.0.0).");
    string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    string auth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("iphone:" + password));
    var listener = new TcpListener(address, port);
    listener.Start();
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); listener.Stop(); };
    if (!automatic) Console.WriteLine($"\niPhone > Settings > Wi-Fi > (i) on PC hotspot > Configure Proxy > Manual\nServer: {address}\nPort: {port}\nAuthentication: On\nUsername: iphone\nPassword: {password}\n\nHTTPS needs no certificate. Apps must support Wi-Fi proxies.\nNo guarantee of non-detection. Ctrl+C stops. Password changes on restart.");
    if (automatic) Console.WriteLine($"\nAUTO MODE: use Configure Proxy > Automatic instead.\nURL: http://{address}:{port}/\nNo proxy login in this mode. Access is limited to this interface's subnet.\nProtect the hotspot with a strong Wi-Fi password.");
    using var slots = new SemaphoreSlim(128);
    var tasks = new List<Task>();
    async Task Run(TcpClient client)
    {
        try
        {
            if (automatic && !Proxy.SameSubnet(((IPEndPoint)client.Client.RemoteEndPoint!).Address, address, localInterface!.PrefixLength)) return;
            await Proxy.Handle(client, automatic ? null : auth, address, port, stop.Token);
        }
        catch (Exception e) { if (!stop.IsCancellationRequested) Log.Write($"CLIENT {client.Client.RemoteEndPoint} ended: {e.GetType().Name}: {e.Message}"); }
        finally { client.Dispose(); slots.Release(); }
    }
    try
    {
        while (!stop.IsCancellationRequested)
        {
            await slots.WaitAsync(stop.Token);
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch { slots.Release(); throw; }
            tasks.RemoveAll(t => t.IsCompleted);
            tasks.Add(Run(client));
        }
    }
    catch (Exception) when (stop.IsCancellationRequested) { }
    finally { listener.Stop(); stop.Cancel(); await Task.WhenAll(tasks); }
}
catch (Exception e) { Console.Error.WriteLine("Cannot start: " + e.Message + "\nEnable hotspot first; use --bind <hotspot IPv4> if its address differs."); Environment.ExitCode = 1; }

static class Proxy
{
    public static bool SameSubnet(IPAddress peer, IPAddress local, int prefix)
    {
        var a = peer.GetAddressBytes(); var b = local.GetAddressBytes();
        if (a.Length != 4 || b.Length != 4) return false;
        for (int bit = 0; bit < prefix; bit++)
            if ((a[bit / 8] & (1 << (7 - bit % 8))) != (b[bit / 8] & (1 << (7 - bit % 8)))) return false;
        return true;
    }
    public static async Task Handle(TcpClient client, string? auth, IPAddress address, int port, CancellationToken stop)
    {
        using var life = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var hello = CancellationTokenSource.CreateLinkedTokenSource(life.Token);
        hello.CancelAfter(TimeSpan.FromSeconds(20));
        var stream = client.GetStream();
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 32768)
        {
            if (await stream.ReadAsync(one, hello.Token) == 0) return;
            bytes.Add(one[0]);
            int n = bytes.Count;
            if (n >= 4 && bytes[n-4] == 13 && bytes[n-3] == 10 && bytes[n-2] == 13 && bytes[n-1] == 10) break;
        }
        async Task Reply(string status, string extra = "") => await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{extra}Content-Length: 0\r\nConnection: close\r\n\r\n"), life.Token);
        if (bytes.Count >= 32768) { await Reply("431 Request Header Fields Too Large"); return; }
        var lines = Encoding.Latin1.GetString(bytes.ToArray()).Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length != 3 || request[2] != "HTTP/1.1" && request[2] != "HTTP/1.0") { await Reply("400 Bad Request"); return; }
        var headers = lines.Skip(1).Where(l => l.Length > 0).ToArray();
        if (headers.Any(h => !h.Contains(':') || char.IsWhiteSpace(h[0]))) { await Reply("400 Bad Request"); return; }
        if (request[0] == "GET" && request[1] == "/health")
        {
            var body = Encoding.UTF8.GetBytes("PC proxy is reachable. This checks the hotspot link, not upstream internet.\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nCache-Control: no-store\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), life.Token);
            await stream.WriteAsync(body, life.Token);
            Log.Write($"Health check from {client.Client.RemoteEndPoint}");
            return;
        }
        // Only origin-form requests are local PAC fetches; absolute proxy URLs
        // with the same path must still go to their actual destination.
        if ((request[0] == "GET" || request[0] == "HEAD") && request[1] is "/" or "/proxy.pac" or "/wpad.dat")
        {
            Log.Write($"PAC fetched by {client.Client.RemoteEndPoint}");
            var pac = Encoding.ASCII.GetBytes($"function FindProxyForURL(url, host) {{ return \"PROXY {address}:{port}\"; }}\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/x-ns-proxy-autoconfig\r\nCache-Control: no-store\r\nContent-Length: {pac.Length}\r\nConnection: close\r\n\r\n"), life.Token);
            if (request[0] == "GET") await stream.WriteAsync(pac, life.Token);
            return;
        }
        if (auth != null && !headers.Any(h => h.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase) && h[(h.IndexOf(':')+1)..].Trim() == auth))
        { await Reply("407 Proxy Authentication Required", "Proxy-Authenticate: Basic realm=\"PC Hotspot\"\r\n"); return; }
        bool tunnel = request[0] == "CONNECT";
        if (!Uri.TryCreate(tunnel ? "http://" + request[1] : request[1], UriKind.Absolute, out var target) || target.Scheme != "http" || target.UserInfo.Length > 0 || target.Host.Length == 0)
        { await Reply("400 Bad Request"); return; }
        if (tunnel && (!Uri.CheckHostName(target.Host).Equals(UriHostNameType.Unknown)) && (target.AbsolutePath != "/" || target.Query.Length > 0)) { await Reply("400 Bad Request"); return; }
        // CONNECT requires an explicit port (including IPv6 bracket notation).
        int targetPort = target.Port;
        if (tunnel && !int.TryParse(request[1][(request[1].LastIndexOf(':')+1)..], out targetPort)) { await Reply("400 Bad Request"); return; }
        if (targetPort is < 1 or > 65535) { await Reply("400 Bad Request"); return; }
        if (!tunnel) { await ForwardHttp(request, headers, target, stream, life.Token); return; }
        string id = Guid.NewGuid().ToString("N")[..8];
        Log.Write($"{id} CONNECT opening {target.Host}:{targetPort} from {client.Client.RemoteEndPoint}");
        using var upstream = new TcpClient();
        try { await upstream.ConnectAsync(target.Host, targetPort, hello.Token); }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        { Log.Write($"{id} CONNECT FAILED {target.Host}:{targetPort}: {e.GetType().Name}: {e.Message}"); if (!stop.IsCancellationRequested) await Reply("502 Bad Gateway"); return; }
        var remote = upstream.GetStream();
        if (tunnel) await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), life.Token);
        Log.Write($"{id} CONNECT established {target.Host}:{targetPort}");
        long sent = 0, received = 0;
        string reason = "closed normally";
        var timer = Stopwatch.StartNew();
        async Task Pump(NetworkStream source, NetworkStream destination, Socket socket, bool uploading)
        {
            var buffer = new byte[65536];
            while (true)
            {
                int n = await source.ReadAsync(buffer, life.Token);
                if (n == 0) { socket.Shutdown(SocketShutdown.Send); return; }
                await destination.WriteAsync(buffer.AsMemory(0, n), life.Token);
                if (uploading) Interlocked.Add(ref sent, n); else Interlocked.Add(ref received, n);
            }
        }
        var upload = Pump(stream, remote, upstream.Client, true);
        var download = Pump(remote, stream, client.Client, false);
        try
        {
            // A clean EOF closes only that direction; let the other direction drain.
            var first = await Task.WhenAny(upload, download);
            if (first.IsFaulted || first.IsCanceled) life.Cancel();
            await Task.WhenAll(upload, download);
        }
        catch (Exception e)
        {
            reason = $"{e.GetType().Name}: {e.Message}";
            life.Cancel();
            try { await Task.WhenAll(upload, download); } catch { }
        }
        finally { Log.Write($"{id} CONNECT ended {target.Host}:{targetPort} after {timer.Elapsed.TotalSeconds:F1}s; sent={sent} received={received}; {reason}"); }
    }
    static async Task ForwardHttp(string[] request, string[] headers, Uri target, NetworkStream stream, CancellationToken token)
    {
        // Plain HTTP: one request per connection. Reject ambiguous/unsupported framing.
        if (headers.Any(h => h.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)))
        { await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 501 Not Implemented\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token); return; }
        var lengths = headers.Where(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (lengths.Length > 1 || lengths.Length == 1 && (!long.TryParse(lengths[0].Split(':')[1].Trim(), out var n) || n < 0)) throw new IOException("Invalid Content-Length");
        long length = lengths.Length == 0 ? 0 : long.Parse(lengths[0].Split(':')[1].Trim());
        using var connection = new TcpClient();
        await connection.ConnectAsync(target.Host, target.Port, token);
        var remote = connection.GetStream();
        var remove = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Host", "Connection", "Proxy-Connection", "Proxy-Authorization", "Keep-Alive", "Upgrade", "TE", "Trailer", "Expect" };
        foreach (var h in headers.Where(h => h.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)))
            foreach (var name in h[(h.IndexOf(':')+1)..].Split(',')) remove.Add(name.Trim());
        if (headers.Any(h => h.StartsWith("Expect:", StringComparison.OrdinalIgnoreCase)))
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), token);
        string outgoing = string.Join("\r\n", new[] { $"{request[0]} {target.PathAndQuery} HTTP/1.1", $"Host: {target.Authority}" }.Concat(headers.Where(h => !remove.Contains(h[..h.IndexOf(':')]))).Append("Connection: close")) + "\r\n\r\n";
        await remote.WriteAsync(Encoding.Latin1.GetBytes(outgoing), token);
        var buffer = new byte[65536];
        while (length > 0) { int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), token); if (read == 0) throw new IOException("Truncated body"); await remote.WriteAsync(buffer.AsMemory(0, read), token); length -= read; }
        Log.Write($"{request[0]} {target.Host}:{target.Port}");
        await remote.CopyToAsync(stream, token);
    }
}

static class Log
{
    static readonly object Gate = new();
    static string? file;
    public static void Initialize(string path)
    {
        file = Path.GetFullPath(path);
        Console.WriteLine($"Diagnostics: {file}");
        Write("Proxy starting (destination hostnames and connection statistics are logged; no payloads or credentials).");
    }
    public static void Write(string message)
    {
        lock (Gate)
        {
            string line = $"{DateTimeOffset.Now:O} {message}";
            Console.WriteLine(line);
            try
            {
                if (file != null)
                {
                    if (File.Exists(file) && new FileInfo(file).Length > 5 * 1024 * 1024) File.Move(file, file + ".1", true);
                    File.AppendAllText(file, line + Environment.NewLine);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("Cannot write diagnostic log: " + e.Message); }
        }
    }
}

