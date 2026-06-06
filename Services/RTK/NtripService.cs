// NtripService.cs — PATCHED VERSION
// Fixes applied:
//   BUG-01: Per-frame CancellationTokenSource prevents indefinite hang on silent TCP drop
//   BUG-02: IAsyncDisposable awaits background task before releasing resources
//   BUG-02b: Connect() cancels previous task before spawning a new one

using System.Net.Sockets;
using System.Text;

namespace BlazorApp3.Services;

public enum NtripConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Error
}

public sealed record NtripConfig(
    string Host,
    int    Port,
    string Mountpoint,
    string Username,
    string Password
)
{
    public bool RequiresAuth => !string.IsNullOrWhiteSpace(Username);
}

public sealed record NtripStats(
    int   TotalFrames,
    int   TotalBytes,
    float RateHz,
    NtripConnectionState State
);

// BUG-02 FIX: Implement IAsyncDisposable so ASP.NET Core's DI container
// calls DisposeAsync() on shutdown, allowing us to await the background task.
public sealed class NtripService : IAsyncDisposable
{
    private readonly MavlinkService        _mavlink;
    private readonly ILogger<NtripService> _log;

    private volatile NtripConnectionState _state = NtripConnectionState.Disconnected;
    private CancellationTokenSource?      _cts;
    private Task?                         _connectionTask;

    // BUG-01 FIX: Timeout for each individual frame read.
    // If no data arrives within this window, the frame read is cancelled,
    // throwing OperationCanceledException which the reconnect loop catches.
    // RTCM correction streams typically arrive at 1-20 Hz; 30s is a safe ceiling.
    private static readonly TimeSpan FrameReadTimeout = TimeSpan.FromSeconds(30);

    private int      _totalFrames;
    private int      _totalBytes;
    private int      _framesThisWindow;
    private DateTime _windowStart = DateTime.UtcNow;

    public NtripConnectionState State  => _state;
    public int   TotalFrames           => _totalFrames;
    public int   TotalBytes            => _totalBytes;
    public float RateHz                { get; private set; }
    public string? LastErrorMessage    { get; private set; }
    public bool IsConnected            => _state == NtripConnectionState.Connected;

    public event Action<NtripConnectionState>? OnStateChanged;
    public event Action<NtripStats>?           OnStats;

    public NtripService(MavlinkService mavlink, ILogger<NtripService> log)
    {
        _mavlink = mavlink;
        _log     = log;
    }

    public void Connect(NtripConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.Host))
            throw new ArgumentException("NTRIP host cannot be empty.");
        if (string.IsNullOrWhiteSpace(config.Mountpoint))
            throw new ArgumentException("NTRIP mountpoint cannot be empty.");

        // BUG-02b FIX: Cancel the previous session's CTS before discarding it.
        // Do NOT await the connectionTask here — Connect() must be non-blocking.
        // The previous task will self-terminate when its CTS is cancelled.
        var oldCts = _cts;
        oldCts?.Cancel();
        // Do not dispose oldCts here — the connectionTask may still be using it.
        // It will be garbage-collected after the task completes.

        _cts = new CancellationTokenSource();

        _totalFrames = _totalBytes = _framesThisWindow = 0;
        RateHz       = 0;
        _windowStart = DateTime.UtcNow;

        _connectionTask = Task.Run(() => RunConnectionLoopAsync(config, _cts.Token));
        _log.LogInformation("[NTRIP] Connect → {Host}:{Port}/{Mount}",
            config.Host, config.Port, config.Mountpoint);
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        SetState(NtripConnectionState.Disconnected);
        _log.LogInformation("[NTRIP] Disconnected by operator");
    }

    private async Task RunConnectionLoopAsync(NtripConfig config, CancellationToken ct)
    {
        int retryDelaySecs = 3;
        LastErrorMessage = string.Empty;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetState(NtripConnectionState.Connecting);
                await RunSingleSessionAsync(config, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
{
    // 1. FATAL ERROR: Host name is invalid (DNS failure). 
    // We stop the loop immediately so the UI button can reset.
    if (ex.Message.Contains("No such host is known"))
    {
        _log.LogWarning("[NTRIP] Fatal error: {Error}. Aborting connection.", ex.Message);
        LastErrorMessage = "Fatal error: Host not found. Check your URL.";
        
        // Setting state to Disconnected allows the UI to stop the spinner 
        // and return the button to the "START INJECTION" green state.
        SetState(NtripConnectionState.Disconnected);
        
        // This 'break' exits the 'while' loop entirely.
        break; 
    }

    // 2. TEMPORARY ERROR: Normal network retry logic
    LastErrorMessage = ex.Message;
    _log.LogWarning("[NTRIP] Session ended: {Error}. Retry in {Delay}s",
        ex.Message, retryDelaySecs);
        
    SetState(NtripConnectionState.Reconnecting);

    try { await Task.Delay(TimeSpan.FromSeconds(retryDelaySecs), ct); }
    catch (OperationCanceledException) { break; }

    // Increment delay (Exponential Backoff)
    retryDelaySecs = Math.Min(retryDelaySecs * 2, 60);
}
        }

        SetState(NtripConnectionState.Disconnected);
    }

    public void ClearErrorMessage()
{
    LastErrorMessage = string.Empty;
}

    private async Task RunSingleSessionAsync(NtripConfig config, CancellationToken ct)
    {
        using var tcp = new TcpClient { NoDelay = true };

        // TCP connect — 10s timeout
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            await tcp.ConnectAsync(config.Host, config.Port, connectCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"TCP connect to {config.Host}:{config.Port} timed out.");
        }

        _log.LogDebug("[NTRIP] TCP connected to {Host}:{Port}", config.Host, config.Port);

        using var stream = tcp.GetStream();
        // NOTE: stream.ReadTimeout intentionally NOT set here.
        // It only affects synchronous reads. All our reads are async and
        // are controlled by the per-frame CancellationToken (BUG-01 fix).

        await SendNtripRequestAsync(stream, config, ct);

        string response = await ReadHttpHeadersAsync(stream, ct);
        ValidateNtripResponse(response, config);

        _log.LogInformation("[NTRIP] Stream connected → /{Mount}", config.Mountpoint);
        LastErrorMessage = null;
        SetState(NtripConnectionState.Connected);

        while (!ct.IsCancellationRequested && tcp.Connected)
        {
            // BUG-01 FIX: Each frame read gets a fresh per-frame CTS linked to the
            // session CT. If no data arrives within FrameReadTimeout (30s), the frame
            // read throws OperationCanceledException, which propagates up to
            // RunConnectionLoopAsync as a non-cancellation exception (since ct itself
            // is not cancelled), triggering a reconnect.
            using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            frameCts.CancelAfter(FrameReadTimeout);

            byte[]? frame;
            try
            {
                frame = await ReadRtcm3FrameAsync(stream, frameCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Per-frame timeout expired — TCP socket has gone silent.
                // Throw a non-cancellation exception so the reconnect loop triggers.
                throw new TimeoutException(
                    $"NTRIP stream silent for {FrameReadTimeout.TotalSeconds}s — reconnecting.");
            }

            if (frame == null) continue;

            if (_mavlink.IsConnected)
            {
                _mavlink.InjectGpsData(frame, (ushort)frame.Length);
            }

            _totalFrames++;
            _totalBytes += frame.Length;
            _framesThisWindow++;

            var elapsed = (DateTime.UtcNow - _windowStart).TotalSeconds;
            if (elapsed >= 1.0)
            {
                RateHz            = (float)(_framesThisWindow / elapsed);
                _framesThisWindow = 0;
                _windowStart      = DateTime.UtcNow;
                OnStats?.Invoke(new NtripStats(_totalFrames, _totalBytes, RateHz, _state));
            }
        }
    }

    private static async Task SendNtripRequestAsync(
        NetworkStream stream, NtripConfig config, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append($"GET /{config.Mountpoint.TrimStart('/')} HTTP/1.0\r\n");
        sb.Append($"Host: {config.Host}:{config.Port}\r\n");
        sb.Append("User-Agent: NTRIP DivyaLink/1.0\r\n");

        if (config.RequiresAuth)
        {
            string b64 = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{config.Username}:{config.Password}"));
            sb.Append($"Authorization: Basic {b64}\r\n");
        }

        sb.Append("Accept: */*\r\n\r\n");
        byte[] req = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(req, ct);
    }

    private static async Task<string> ReadHttpHeadersAsync(
        NetworkStream stream, CancellationToken ct)
    {
        // Headers are short — use 10s timeout for the response phase
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(TimeSpan.FromSeconds(10));

        var  sb  = new StringBuilder(512);
        var  buf = new byte[1];

        while (true)
        {
            int read = await stream.ReadAsync(buf, 0, 1, headerCts.Token);
            if (read == 0) throw new EndOfStreamException("NTRIP caster closed during handshake.");
            sb.Append((char)buf[0]);

            string s = sb.ToString();
            if (s.EndsWith("\r\n\r\n") || s.EndsWith("\n\n")) break;
            if (s.Contains("ICY 200 OK") && s.EndsWith("\r\n")) break;
            if (sb.Length > 8192)
                throw new InvalidOperationException("NTRIP response header unexpectedly large.");
        }

        return sb.ToString();
    }

    private static void ValidateNtripResponse(string response, NtripConfig config)
    {
        bool ok = response.StartsWith("ICY 200 OK",   StringComparison.OrdinalIgnoreCase)
               || response.StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase)
               || response.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase);

        if (!ok)
        {
            string firstLine = response.Split('\n')[0].Trim();
            if (response.Contains("401") || response.Contains("Unauthorized"))
                throw new UnauthorizedAccessException(
                    $"NTRIP authentication failed for /{config.Mountpoint}.");
            if (response.Contains("404") || response.Contains("Not Found"))
                throw new InvalidOperationException(
                    $"Mount point /{config.Mountpoint} not found on {config.Host}.");
            throw new InvalidOperationException($"NTRIP rejected: {firstLine}");
        }
    }

    /// <summary>
    /// Reads one complete RTCM3 frame.
    /// The caller wraps this in a per-frame CancellationToken with a 30s deadline
    /// (BUG-01 fix) — this method itself does not manage timeouts.
    /// </summary>
    private static async Task<byte[]?> ReadRtcm3FrameAsync(
        NetworkStream stream, CancellationToken ct)
    {
        const byte Preamble      = 0xD3;
        const int  MaxSyncSearch = 2048;

        var oneByte  = new byte[1];
        int searched = 0;

        while (true)
        {
            int read = await stream.ReadAsync(oneByte, 0, 1, ct);
            if (read == 0) throw new EndOfStreamException("NTRIP stream closed.");
            if (oneByte[0] == Preamble) break;
            if (++searched >= MaxSyncSearch)
                throw new InvalidOperationException("RTCM3 stream lost sync (2KB searched).");
        }

        var lenBuf = new byte[2];
        await ReadExactAsync(stream, lenBuf, ct);

        int payloadLen = ((lenBuf[0] & 0x03) << 8) | lenBuf[1];
        if (payloadLen > 1023) return null;

        var payload  = new byte[payloadLen];
        var crcBytes = new byte[3];
        await ReadExactAsync(stream, payload,  ct);
        await ReadExactAsync(stream, crcBytes, ct);

        var crcInput = new byte[3 + payloadLen];
        crcInput[0] = Preamble;
        crcInput[1] = lenBuf[0];
        crcInput[2] = lenBuf[1];
        Array.Copy(payload, 0, crcInput, 3, payloadLen);

        uint computed = Crc24Q(crcInput);
        uint received = ((uint)crcBytes[0] << 16) | ((uint)crcBytes[1] << 8) | crcBytes[2];
        if (computed != received) return null;

        var frame = new byte[3 + payloadLen + 3];
        frame[0] = Preamble;
        frame[1] = lenBuf[0];
        frame[2] = lenBuf[1];
        Array.Copy(payload,  0, frame, 3,             payloadLen);
        Array.Copy(crcBytes, 0, frame, 3 + payloadLen, 3);
        return frame;
    }

    private static async Task ReadExactAsync(
        NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, ct);
            if (read == 0) throw new EndOfStreamException("NTRIP stream closed during read.");
            offset += read;
        }
    }

    private static uint Crc24Q(byte[] data)
    {
        const uint Poly = 0x1864CFBu;
        uint crc = 0;
        foreach (byte b in data)
        {
            crc ^= (uint)(b << 16);
            for (int i = 0; i < 8; i++)
            {
                crc <<= 1;
                if ((crc & 0x1000000u) != 0) crc ^= Poly;
            }
        }
        return crc & 0xFFFFFFu;
    }

    private void SetState(NtripConnectionState s)
    {
        if (_state == s) return;
        _state = s;
        _log.LogDebug("[NTRIP] State → {State}", s);
        OnStateChanged?.Invoke(s);
    }

    // BUG-02 FIX: IAsyncDisposable allows ASP.NET Core to properly await
    // the background task before the process exits. Without this, the task
    // may throw ObjectDisposedException as the DI container tears down.
    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();

        var task = _connectionTask;
        if (task != null)
        {
            try
            {
                // Give the background task 5 seconds to clean up its TcpClient
                await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch { /* OperationCanceledException or timeout — expected on shutdown */ }
        }

        _cts?.Dispose();
    }
}