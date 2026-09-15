using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerSideBar.Models;

namespace PowerSideBar.Services;

/// <summary>
/// TCP client for the smart home controller (typically telnet port).
///
/// Protocol overview:
///
///   1. LOGIN
///      Client → [MAC 6 bytes] + CR LF + [user] + CR LF + [password] + CR LF
///      Server → "HverRsp" (7 bytes) + 4 binary bytes  (= success)
///               Nothing / closed                        (= failure)
///
///   2. DEVICE LIST  (sent after successful login)
///      Client → "getalldevice" + CR LF
///      Server → one line per device: "XX,XX,XX,XX,;CHANNEL;NAME;POSITION;"
///               terminated by an empty line or a specific marker
///
///   3. EXECUTE COMMAND
///      Client → "ctrldevice;[ID];[CHANNEL];[ACTION];" + CR LF
///      where ACTION = "up" | "down" | "stop"
/// </summary>
public class ShutterService : IDisposable
{
    private static bool EnableTrace = false;
    // Dooya SHCP1 login response prefix
    private const string LoginResponsePrefix = "UlogRsp";

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Human-readable description of the last error, for display in the UI.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raw diagnostic info from the last login attempt (banner + response bytes).</summary>
    private string _loginDiag = string.Empty;

    /// <summary>Diagnostic of last GetDevicesAsync (frames received, payload) for UI when 0 devices.</summary>
    public string? LastGetDevicesDiag { get; private set; }

    public bool IsConnected => _tcpClient?.Connected == true && _stream != null;

    // ── Connection ────────────────────────────────────────────────────────────

    /// <summary>Connect and authenticate. Returns true on success.</summary>
    public async Task<bool> ConnectAsync(
        string host, int port, string user, string password, string hostId,
        CancellationToken ct = default)
    {
        LastError = null;
        await DisconnectAsync();

        try
        {
            _tcpClient = new TcpClient { ReceiveTimeout = 12000, SendTimeout = 12000 };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

            var connectStart = Stopwatch.GetTimestamp();
            await _tcpClient.ConnectAsync(host, port, timeoutCts.Token);
            var connectElapsedMs = ElapsedMilliseconds(connectStart);
            _stream = _tcpClient.GetStream();

            LogTrace($"TCP connected to {host}:{port} in {connectElapsedMs} ms");

            _loginDiag = string.Empty;
            var loginOk = await LoginAsync(user, password, hostId, ct);
            if (!loginOk)
            {
                LastError = Loc.Tf("shutters.auth_failed", user, _loginDiag);
                await DisconnectAsync();
                return false;
            }

            LogTrace("Login OK");
            return true;
        }
        catch (OperationCanceledException)
        {
            LastError = Loc.Tf("shutters.timeout", host, port);
            await DisconnectAsync();
            return false;
        }
        catch (SocketException ex)
        {
            LastError = Loc.Tf("shutters.network_error", ex.Message, host, port);
            LogTrace($"SocketException: {ex.Message}");
            await DisconnectAsync();
            return false;
        }
        catch (Exception ex)
        {
            LastError = Loc.Tf("shutters.connect_error", ex.Message);
            LogTrace($"Connect error: {ex}");
            await DisconnectAsync();
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _stream?.Close();
            _stream = null;
            _tcpClient?.Close();
            _tcpClient = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Login ─────────────────────────────────────────────────────────────────

    private async Task<bool> LoginAsync(string user, string password, string hostId, CancellationToken ct)
    {
        if (_stream == null) return false;

        // The controller uses a proprietary binary \"UlogReq\" protocol (Dooya SHCP1),
        // not a text telnet login.

        var frame = BuildDooyaLoginFrame(user, password);
        LogTrace($"TX login: {BitConverter.ToString(frame)}");

        await _stream.WriteAsync(frame.AsMemory(0, frame.Length), ct);
        await _stream.FlushAsync(ct);

        var readStart = Stopwatch.GetTimestamp();
        var response = await ReadLoginResponseAsync(TimeSpan.FromSeconds(8), ct);
        var elapsedMs = ElapsedMilliseconds(readStart);

        if (response == null || response.Length == 0)
        {
            _loginDiag = Loc.Tf("shutters.login_no_response", elapsedMs);
            LogTrace(_loginDiag);
            return false;
        }

        var responseHex = BitConverter.ToString(response);
        var responseText = Encoding.ASCII.GetString(response).Replace("\r", "\\r").Replace("\n", "\\n");
        _loginDiag = Loc.Tf("shutters.login_response", responseText, responseHex, elapsedMs);

        LogTrace($"Login diag: {_loginDiag}");

        if (response.Length < 7) return false;
        var prefix = Encoding.ASCII.GetString(response, 0, 7);
        return prefix == LoginResponsePrefix;
    }

    /// <summary>
    /// Read any bytes the server sends spontaneously after connect (banner, prompt, etc.).
    /// Stops when nothing arrives within <paramref name="timeout"/> or <paramref name="maxBytes"/> read.
    /// Never throws — returns whatever was collected (may be empty).
    /// </summary>
    private async Task<byte[]> DrainInitialAsync(int maxBytes, TimeSpan timeout, CancellationToken ct)
    {
        if (_stream == null) return Array.Empty<byte>();
        var result = new List<byte>(maxBytes);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var buf = new byte[1];
        try
        {
            while (result.Count < maxBytes)
            {
                var n = await _stream.ReadAsync(buf.AsMemory(0, 1), timeoutCts.Token);
                if (n == 0) break;
                result.Add(buf[0]);
            }
        }
        catch
        {
            // timeout or disconnect — not an error here
        }
        return result.ToArray();
    }

    private static byte[] ParseMacBytes(string hostId)
    {
        // Accept "AABBCCDDEEFF" or "AA:BB:CC:DD:EE:FF"
        var clean = hostId.Replace(":", "").Replace("-", "").Trim();
        if (clean.Length < 12) return new byte[6];
        var result = new byte[6];
        for (var i = 0; i < 6; i++)
            result[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
        return result;
    }

    // ── Device discovery ──────────────────────────────────────────────────────

    /// <summary>
    /// Fetch all devices registered on the controller using the Dooya SHCP1 binary protocol.
    /// After login sends "GetaReq"; server replies with RallRsp, then DallRsp, then SallRsp, etc.
    /// We send GetaReq and read frames until we get DallRsp.
    /// </summary>
    public async Task<List<ShutterDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        LastGetDevicesDiag = null;
        await _lock.WaitAsync(ct);
        try
        {
            if (_stream == null)
            {
                LastGetDevicesDiag = Loc.T("shutters.conn_closed");
                return new List<ShutterDevice>();
            }

            // HverReq (1-byte payload), then GetaReq.
            var hverReq = BuildHverReqFrame(getVersion: true);
            LogTrace($"TX HverReq: {BitConverter.ToString(hverReq)}");
            await _stream.WriteAsync(hverReq, ct);
            await _stream.FlushAsync(ct);
            var hverFrame = await ReadFrameAsync(TimeSpan.FromSeconds(5), ct);
            if (hverFrame == null || hverFrame.Length < 7)
            {
                LastGetDevicesDiag = Loc.T("shutters.hver_no_response");
                return new List<ShutterDevice>();
            }
            var hverTitle = Encoding.ASCII.GetString(hverFrame, 0, 7);
            if (!hverTitle.StartsWith("HverRsp", StringComparison.Ordinal))
                LastGetDevicesDiag = Loc.Tf("shutters.hver_unexpected", hverTitle);

            var req = BuildSimpleReqFrame("GetaReq");
            LogTrace($"TX GetaReq: {BitConverter.ToString(req)}");
            await _stream.WriteAsync(req, ct);
            await _stream.FlushAsync(ct);

            // Frames: [7 ASCII title][2 len LE][payload]. Accumulate by chunks and parse.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            var buffer = new List<byte>();
            var frameTitles = new List<string>();
            var chunk = new byte[4096];

            try
            {
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(15))
                {
                    var n = await _stream.ReadAsync(chunk.AsMemory(0, chunk.Length), timeoutCts.Token);
                    if (n == 0) break;
                    for (var i = 0; i < n; i++) buffer.Add(chunk[i]);

                    while (buffer.Count >= 9)
                    {
                        var payloadLen = buffer[7] | (buffer[8] << 8);
                        if (payloadLen < 0 || payloadLen > 20480)
                        {
                            buffer.RemoveAt(0);
                            continue;
                        }
                        if (buffer.Count < 9 + payloadLen) break;

                        var title = Encoding.ASCII.GetString(buffer.GetRange(0, 7).ToArray());
                        frameTitles.Add($"{title}({9 + payloadLen})");

                        if (title.StartsWith("DallRsp", StringComparison.Ordinal))
                        {
                            var frame = buffer.GetRange(0, 9 + payloadLen).ToArray();
                            var devices = ParseDallRspToShutters(frame);
                            if (devices.Count == 0)
                            {
                                var count = frame.Length >= 10 ? frame[9] : 0;
                                LastGetDevicesDiag = Loc.Tf("shutters.dall_empty", count);
                            }
                            return devices;
                        }
                        buffer.RemoveRange(0, 9 + payloadLen);
                    }
                }

                LastGetDevicesDiag = frameTitles.Count > 0
                    ? Loc.Tf("shutters.frames_no_dall", string.Join(", ", frameTitles))
                    : Loc.T("shutters.no_frames");
                return new List<ShutterDevice>();
            }
            catch (OperationCanceledException)
            {
                LastGetDevicesDiag = frameTitles.Count > 0
                    ? Loc.Tf("shutters.timeout_frames", string.Join(", ", frameTitles))
                    : Loc.T("shutters.timeout_geta");
                return new List<ShutterDevice>();
            }
        }
        catch (Exception ex)
        {
            LastGetDevicesDiag = Loc.Tf("shutters.error", ex.Message);
            LogTrace($"GetDevices error: {ex.Message}");
            return new List<ShutterDevice>();
        }
        finally
        {
            _lock.Release();
        }
    }

    private static byte[] BuildSimpleReqFrame(string cmd7)
    {
        var header = Encoding.ASCII.GetBytes(cmd7);
        if (header.Length != 7) throw new ArgumentException("Command must be 7 ASCII bytes.", nameof(cmd7));
        return [.. header, 0x00, 0x00];
    }

    private static byte[] BuildHverReqFrame(bool getVersion)
    {
        // HverReq + len(1) + payload: 1 byte (0 = get, 1 = set)
        var header = Encoding.ASCII.GetBytes("HverReq");
        return [.. header, 0x01, 0x00, (byte)(getVersion ? 0 : 1)];
    }

    private static List<ShutterDevice> ParseDallRspToShutters(byte[] frame)
    {
        // DallRsp payload = deviceCount(1) then per device (24 bytes):
        //   roomType(1), roomIndex(1), zdmac(4), status(1), engle/pad(1), name(16).
        if (frame.Length < 10) return [];
        var payloadLen = frame[7] | (frame[8] << 8);
        var totalLen = 9 + payloadLen;
        if (frame.Length < totalLen) return [];

        var idx = 9;
        var deviceCount = frame[idx++];
        var shutters = new List<ShutterDevice>();
        const int recordSize = 1 + 1 + 4 + 2 + 16; // 24

        for (var i = 0; i < deviceCount; i++)
        {
            if (idx + recordSize > totalLen) break;

            var roomType = frame[idx++];
            var roomIndex = frame[idx++];
            var macBytes = frame.AsSpan(idx, 4).ToArray();
            idx += 4;
            var mac = Encoding.Latin1.GetString(macBytes);
            var deviceType = mac.Length > 0 ? (char)mac[0] : '\0';
            // status(1) + engle or padding(1)
            var status = frame[idx++];
            var engle = frame[idx++];
            var nameBytes = frame.AsSpan(idx, 16).ToArray();
            idx += 16;
            var rawName = Encoding.UTF8.GetString(nameBytes).Trim('\0', ' ');
            var name = string.IsNullOrWhiteSpace(rawName) ? $"Device {mac}" : rawName;

            // Common shutter types: D, E, F, T.
            // Channel 0 = invalid / non-shutter input → ignored.
            if (mac.Length == 4)
            {
                var channel = (int)(mac[3] & 0xFF);
                if (channel == 0) continue;
                shutters.Add(new ShutterDevice(mac, channel, name, status));
            }
        }

        return shutters
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Channel)
            .ToList();
    }

    // ── Command execution ─────────────────────────────────────────────────────

    /// <summary>Send a shutter command (Up / Down / Stop) to a device.</summary>
    public async Task SendCommandAsync(
        ShutterDevice device, ShutterCommand cmd,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_stream == null) return;

            // DexeReq frame. Payload = zdtype(1) + zdcmd(1) + mac(4).
            // On this box: 0 = Up, 128 = Down (close), 255 = Stop.
            byte zdtype = 1;
            byte zdcmd = cmd switch
            {
                ShutterCommand.Up => 0,
                ShutterCommand.Down => 128,
                ShutterCommand.Stop => 255,
                _ => 255,
            };

            var macBytes = Encoding.Latin1.GetBytes(device.Id);
            if (macBytes.Length != 4)
                macBytes = macBytes.Take(4).Concat(Enumerable.Repeat((byte)0, Math.Max(0, 4 - macBytes.Length))).ToArray();

            var frame = new byte[15];
            Encoding.ASCII.GetBytes("DexeReq").CopyTo(frame, 0);
            frame[7] = 0x06; // payload len = 6
            frame[8] = 0x00;
            frame[9] = zdtype;
            frame[10] = zdcmd;
            Buffer.BlockCopy(macBytes, 0, frame, 11, 4);

            LogTrace($"TX DexeReq: {BitConverter.ToString(frame)} (cmd={cmd})");
            await _stream.WriteAsync(frame, ct);
            await _stream.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            LogTrace($"SendCommand error: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Sends a raw zdcmd code (0-255) to a device. Used to probe which code = Up / Down / Stop.</summary>
    public async Task SendRawCommandAsync(ShutterDevice device, byte zdcmd, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_stream == null) return;
            byte zdtype = 1;
            var macBytes = Encoding.Latin1.GetBytes(device.Id);
            if (macBytes.Length != 4)
                macBytes = macBytes.Take(4).Concat(Enumerable.Repeat((byte)0, Math.Max(0, 4 - macBytes.Length))).ToArray();
            var frame = new byte[15];
            Encoding.ASCII.GetBytes("DexeReq").CopyTo(frame, 0);
            frame[7] = 0x06;
            frame[8] = 0x00;
            frame[9] = zdtype;
            frame[10] = zdcmd;
            Buffer.BlockCopy(macBytes, 0, frame, 11, 4);
            LogTrace($"TX DexeReq raw: zdcmd={zdcmd} {BitConverter.ToString(frame)}");
            await _stream.WriteAsync(frame, ct);
            await _stream.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            LogTrace($"SendRawCommand error: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Low-level I/O ─────────────────────────────────────────────────────────

    private async Task<byte[]?> ReadBytesAsync(int count, TimeSpan timeout, CancellationToken ct)
    {
        if (_stream == null) return null;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var buf = new byte[count];
            var read = 0;
            while (read < count)
            {
                var n = await _stream.ReadAsync(buf.AsMemory(read, count - read), timeoutCts.Token);
                if (n == 0) return null;
                read += n;
            }
            return buf;
        }
        catch
        {
            return null;
        }
    }

    private async Task<byte[]?> ReadLoginResponseAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_stream == null) return null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var buf = new byte[1];
        var result = new List<byte>();

        try
        {
            while (result.Count < 256)
            {
                var n = await _stream.ReadAsync(buf.AsMemory(0, 1), timeoutCts.Token);
                if (n == 0) break;
                result.Add(buf[0]);

                if (result.Count >= 7)
                {
                    var text = Encoding.ASCII.GetString(result.ToArray());
                    if (text.Contains(LoginResponsePrefix, StringComparison.Ordinal))
                        break;
                }
            }
        }
        catch
        {
            // timeout or disconnect — return what we have (may be empty)
        }

        return result.ToArray();
    }

    private async Task<byte[]?> ReadFrameAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_stream == null) return null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        // Read header (9 bytes)
        var header = await ReadExactAsync(9, timeoutCts.Token);
        if (header == null) return null;

        var payloadLen = header[7] | (header[8] << 8);
        if (payloadLen < 0 || payloadLen > 20480) return header; // suspicious, but return what we have

        var payload = payloadLen == 0 ? Array.Empty<byte>() : await ReadExactAsync(payloadLen, timeoutCts.Token);
        if (payload == null) return header;

        return [.. header, .. payload];
    }

    private async Task<byte[]?> ReadExactAsync(int count, CancellationToken ct)
    {
        if (_stream == null) return null;
        var buf = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await _stream.ReadAsync(buf.AsMemory(read, count - read), ct);
            if (n == 0) return null;
            read += n;
        }
        return buf;
    }

    /// <summary>
    /// Builds the Dooya SHCP1 binary login frame.
    /// Format:
    ///   "UlogReq" (7 ASCII bytes)
    ///   [len_lo][len_hi]  (2 bytes, payload length = totalLen - 9)
    ///   user (UTF-8 raw bytes, max 16 bytes, zero-padded)
    ///   password (ASCII, max 6 bytes, zero-padded)
    /// </summary>
    private static byte[] BuildDooyaLoginFrame(string user, string password)
    {
        const string header = "UlogReq";
        var headerBytes = Encoding.ASCII.GetBytes(header);

        // Fixed layout: 7 (header) + 2 (length) + 16 (user) + 6 (password) = 31 bytes.
        var frame = new byte[31];

        // Header
        Buffer.BlockCopy(headerBytes, 0, frame, 0, headerBytes.Length);

        // Leave len_lo/len_hi (positions 7 and 8) at 0 for now.

        // User: UTF-8 bytes, max 16.
        var userUtf8 = Encoding.UTF8.GetBytes(user ?? string.Empty);
        var userLen = Math.Min(16, userUtf8.Length);
        if (userLen > 0)
            Buffer.BlockCopy(userUtf8, 0, frame, 9, userLen);
        // remaining bytes of the 16 stay at 0

        // Password: max 6 ASCII bytes.
        var passBytes = Encoding.ASCII.GetBytes(password ?? string.Empty);
        var passLen = Math.Min(6, passBytes.Length);
        if (passLen > 0)
            Buffer.BlockCopy(passBytes, 0, frame, 9 + 16, passLen);

        // Payload length = totalLen - 9.
        var totalLen = frame.Length; // 31
        var payloadLen = totalLen - 9; // 22
        frame[7] = (byte)(payloadLen & 0xFF);        // len_lo
        frame[8] = (byte)((payloadLen >> 8) & 0xFF); // len_hi

        return frame;
    }

    private static double ElapsedMilliseconds(long startTimestamp)
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
        return elapsedTicks * 1000.0 / Stopwatch.Frequency;
    }

    private static void LogTrace(string message)
    {
        if (!EnableTrace) return;
        Debug.WriteLine($"[Shutter] {message}");
    }

    // Legacy text parsing removed: this controller uses binary frames.

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _stream?.Dispose();
        _tcpClient?.Dispose();
        _lock.Dispose();
    }
}
