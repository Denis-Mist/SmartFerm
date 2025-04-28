using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app     = builder.Build();

// Enable WebSockets
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(120) });
app.UseDefaultFiles();
app.UseStaticFiles();

var random       = new Random();
var wsConnections = new ConcurrentDictionary<Guid, WebSocket>();
var wateringOn    = false;

// Utility to serialize and wrap payload
static byte[] MakePayload(string type, string data)
{
    var payload = new {
        type,
        data,
        timestamp = DateTime.UtcNow.ToString("o")
    };
    return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
}

// Broadcast to all clients
async Task BroadcastAsync(byte[] msg)
{
    var segment = new ArraySegment<byte>(msg);
    foreach (var (id, ws) in wsConnections)
    {
        if (ws.State == WebSocketState.Open)
        {
            try { await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None); }
            catch { wsConnections.TryRemove(id, out _); }
        }
    }
}

// Periodic weather
var weatherTimer = new System.Timers.Timer(60_000) { AutoReset = true };
weatherTimer.Elapsed += async (_,_) => {
    var opts = new[] { "Sunny", "Cloudy", "Rainy" };
    await BroadcastAsync(MakePayload("weather", opts[random.Next(opts.Length)]));
};
weatherTimer.Start();

// Periodic sensor
var sensorTimer = new System.Timers.Timer(5_000) { AutoReset = true };
sensorTimer.Elapsed += async (_,_) => {
    var hum = random.Next(30, 90).ToString();
    await BroadcastAsync(MakePayload("sensor", hum));
};
sensorTimer.Start();

// WebSocket endpoint
app.MapGet("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    var id = Guid.NewGuid();
    wsConnections[id] = ws;

    // Send initial watering status
    await ws.SendAsync(
      new ArraySegment<byte>(MakePayload("status", $"Watering is {(wateringOn ? "ON" : "OFF")}")),
      WebSocketMessageType.Text,
      true,
      CancellationToken.None
    );

    var buffer = new byte[1024];
    while (ws.State == WebSocketState.Open)
    {
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        if (result.CloseStatus.HasValue)
        {
            await ws.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
            break;
        }

        // Handle incoming text command
        var cmd = Encoding.UTF8.GetString(buffer, 0, result.Count).Trim();
        if (cmd == "WATER_ON"  ) wateringOn = true;
        if (cmd == "WATER_OFF" ) wateringOn = false;

        // Broadcast watering change
        await BroadcastAsync(MakePayload("watering", wateringOn ? "ON" : "OFF"));
    }

    wsConnections.TryRemove(id, out _);
});

app.Run();
