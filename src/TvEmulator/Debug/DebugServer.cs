using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace TvEmulator.Debug;

public sealed class DebugServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private DebugServer(WebApplication app, DebugHub hub, int port, IReadOnlyList<string> urls)
    {
        _app = app;
        Hub = hub;
        Port = port;
        Urls = urls;
    }

    public DebugHub Hub { get; }
    public int Port { get; }
    public IReadOnlyList<string> Urls { get; }

    public string FormatAddresses()
    {
        var builder = new StringBuilder();
        builder.Append("Podgląd na żywo: http://127.0.0.1:").Append(Port);
        foreach (var nic in DebugHub.LanAddresses())
        {
            builder.AppendLine();
            builder.Append("W sieci (").Append(nic.Name).Append("): http://").Append(nic.Address).Append(':').Append(Port);
        }

        builder.AppendLine();
        builder.Append("Telefon w tej samej sieci Wi-Fi otwiera adres „W sieci”. Jeśli nie wchodzi, zezwól aplikacji na sieci prywatne w zaporze Windows.");
        return builder.ToString();
    }

    public static async Task<DebugServer> StartAsync(string uiDirectory, int preferredPort = 2137)
    {
        if (preferredPort == 0)
            return await BindAsync(uiDirectory, "http://127.0.0.1:0");

        Exception? last = null;
        for (var port = preferredPort; port <= preferredPort + 10; port++)
        {
            try
            {
                return await BindAsync(uiDirectory, "http://0.0.0.0:" + port);
            }
            catch (Exception ex) when (IsInUse(ex))
            {
                last = ex;
            }
        }

        throw new InvalidOperationException("Brak wolnego portu dla podglądu (" + preferredPort + "-" + (preferredPort + 10) + ").", last);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _app.StopAsync(stop.Token);
        }
        catch (Exception)
        {
        }

        await _app.DisposeAsync();
    }

    private static async Task<DebugServer> BindAsync(string uiDirectory, string url)
    {
        var hub = new DebugHub();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);
        builder.Services.AddSingleton(hub);
        var app = builder.Build();
        try
        {
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
            if (Directory.Exists(uiDirectory))
            {
                var files = new PhysicalFileProvider(uiDirectory);
                app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = files,
                    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
                });
            }

            app.MapGet("/api/status", (DebugHub current) => Results.Text(current.StatusJson(), "application/json"));
            app.MapGet("/api/network/{id:long}", (long id, DebugHub current) =>
            {
                var json = current.NetworkDetailJson(id);
                return json is null ? Results.NotFound() : Results.Text(json, "application/json");
            });
            app.MapPost("/api/heap/snapshot", async (HttpRequest request, DebugHub current) =>
            {
                var body = await request.ReadFromJsonAsync<SnapshotBody>();
                current.RequestSnapshot(body?.Target ?? "");
                return Results.NoContent();
            });
            app.Map("/ws", HandleSocket);
            await app.StartAsync();
            var port = BoundPort(app);
            var urls = new List<string> { "http://127.0.0.1:" + port };
            foreach (var nic in DebugHub.LanAddresses())
                urls.Add("http://" + nic.Address + ":" + port);
            hub.SetEndpoint(port, urls);
            return new DebugServer(app, hub, port, urls);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static int BoundPort(WebApplication app)
    {
        var feature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        var raw = feature?.Addresses.FirstOrDefault() ?? app.Urls.FirstOrDefault();
        if (raw is null || !Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Serwer podglądu nie podał portu.");
        return uri.Port;
    }

    private static async Task HandleSocket(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var hub = context.RequestServices.GetRequiredService<DebugHub>();
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(400)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        using var subscription = hub.Subscribe(message => channel.Writer.TryWrite(message), out var hello);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        Task? pump = null;
        try
        {
            await SendAsync(socket, hello, cts.Token);
            pump = PumpAsync(socket, channel.Reader, cts.Token);
            var buffer = new byte[4096];
            using var incoming = new MemoryStream();
            var dropping = false;
            while (socket.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (!dropping)
                {
                    if (incoming.Length + result.Count > 8192)
                    {
                        dropping = true;
                        incoming.SetLength(0);
                    }
                    else
                    {
                        incoming.Write(buffer, 0, result.Count);
                    }
                }

                if (!result.EndOfMessage)
                    continue;
                if (!dropping && incoming.Length > 0)
                {
                    var text = Encoding.UTF8.GetString(incoming.GetBuffer(), 0, (int)incoming.Length);
                    hub.HandleClient(text);
                }

                dropping = false;
                incoming.SetLength(0);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            subscription.Dispose();
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            channel.Writer.TryComplete();
            if (pump is not null)
            {
                try { await pump; } catch (Exception) { }
            }

            if (socket.State == WebSocketState.Open)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                catch (Exception) { }
            }
        }
    }

    private static async Task PumpAsync(WebSocket socket, ChannelReader<string> reader, CancellationToken cancellationToken)
    {
        await foreach (var message in reader.ReadAllAsync(cancellationToken))
        {
            if (socket.State != WebSocketState.Open)
                break;
            await SendAsync(socket, message, cancellationToken);
        }
    }

    private static async Task SendAsync(WebSocket socket, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static bool IsInUse(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket && socket.SocketErrorCode == SocketError.AddressAlreadyInUse)
                return true;
            if (current.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private sealed class SnapshotBody
    {
        public string? Target { get; set; }
    }
}
