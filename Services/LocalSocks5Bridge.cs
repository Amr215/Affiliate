using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Affiliate.Services
{
    /// <summary>
    /// Local unauthenticated SOCKS5 listener that forwards to an upstream SOCKS5 proxy with
    /// username/password. Chromium/Playwright cannot authenticate SOCKS5 itself, but Webshare
    /// only reaches Amazon reliably over SOCKS5 (HTTP proxy times out).
    /// </summary>
    public sealed class LocalSocks5Bridge : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _upstreamHost;
        private readonly int _upstreamPort;
        private readonly byte[] _userBytes;
        private readonly byte[] _passBytes;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private int _disposed;

        private LocalSocks5Bridge(
            TcpListener listener,
            string upstreamHost,
            int upstreamPort,
            string username,
            string password)
        {
            _listener = listener;
            _upstreamHost = upstreamHost;
            _upstreamPort = upstreamPort;
            _userBytes = Encoding.UTF8.GetBytes(username ?? "");
            _passBytes = Encoding.UTF8.GetBytes(password ?? "");
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = AcceptLoopAsync(_cts.Token);
        }

        public int Port { get; }

        public string PlaywrightServer => $"socks5://127.0.0.1:{Port}";

        public static LocalSocks5Bridge Start(
            string upstreamHost,
            int upstreamPort,
            string? username,
            string? password)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new LocalSocks5Bridge(
                listener,
                upstreamHost,
                upstreamPort,
                username ?? "",
                password ?? "");
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    var local = client.GetStream();

                    // SOCKS5 greeting from Chromium
                    var header = new byte[2];
                    await ReadExactAsync(local, header, ct);
                    if (header[0] != 0x05)
                        return;

                    var nMethods = header[1];
                    if (nMethods > 0)
                        await ReadExactAsync(local, new byte[nMethods], ct);

                    // Offer no-auth only
                    await local.WriteAsync(new byte[] { 0x05, 0x00 }, ct);

                    // CONNECT request
                    var req = new byte[4];
                    await ReadExactAsync(local, req, ct);
                    if (req[0] != 0x05 || req[1] != 0x01)
                    {
                        await WriteSocksReplyAsync(local, 0x07, ct); // command not supported
                        return;
                    }

                    string destHost;
                    int destPort;
                    switch (req[3])
                    {
                        case 0x01: // IPv4
                        {
                            var addr = new byte[4];
                            await ReadExactAsync(local, addr, ct);
                            destHost = new IPAddress(addr).ToString();
                            break;
                        }
                        case 0x03: // domain
                        {
                            var lenBuf = new byte[1];
                            await ReadExactAsync(local, lenBuf, ct);
                            var hostBuf = new byte[lenBuf[0]];
                            await ReadExactAsync(local, hostBuf, ct);
                            destHost = Encoding.UTF8.GetString(hostBuf);
                            break;
                        }
                        case 0x04: // IPv6
                        {
                            var addr = new byte[16];
                            await ReadExactAsync(local, addr, ct);
                            destHost = new IPAddress(addr).ToString();
                            break;
                        }
                        default:
                            await WriteSocksReplyAsync(local, 0x08, ct);
                            return;
                    }

                    var portBuf = new byte[2];
                    await ReadExactAsync(local, portBuf, ct);
                    destPort = BinaryPrimitives.ReadUInt16BigEndian(portBuf);

                    TcpClient upstream;
                    try
                    {
                        upstream = await ConnectUpstreamAsync(destHost, destPort, ct);
                    }
                    catch
                    {
                        await WriteSocksReplyAsync(local, 0x05, ct); // connection refused
                        return;
                    }

                    using (upstream)
                    {
                        await WriteSocksReplyAsync(local, 0x00, ct);
                        var remote = upstream.GetStream();
                        await Task.WhenAny(
                            local.CopyToAsync(remote, ct),
                            remote.CopyToAsync(local, ct));
                    }
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // Client dropped or upstream failed mid-stream — ignore.
                }
            }
        }

        private async Task<TcpClient> ConnectUpstreamAsync(
            string destHost,
            int destPort,
            CancellationToken ct)
        {
            var upstream = new TcpClient { NoDelay = true };
            await upstream.ConnectAsync(_upstreamHost, _upstreamPort, ct);
            var stream = upstream.GetStream();

            // Greeting: username/password auth
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x02 }, ct);
            var method = new byte[2];
            await ReadExactAsync(stream, method, ct);
            if (method[0] != 0x05 || method[1] != 0x02)
                throw new IOException("Upstream SOCKS5 did not accept username/password auth.");

            // RFC 1929 username/password
            if (_userBytes.Length > 255 || _passBytes.Length > 255)
                throw new InvalidOperationException("SOCKS5 credentials too long.");

            var auth = new byte[3 + _userBytes.Length + _passBytes.Length];
            auth[0] = 0x01;
            auth[1] = (byte)_userBytes.Length;
            Buffer.BlockCopy(_userBytes, 0, auth, 2, _userBytes.Length);
            auth[2 + _userBytes.Length] = (byte)_passBytes.Length;
            Buffer.BlockCopy(_passBytes, 0, auth, 3 + _userBytes.Length, _passBytes.Length);
            await stream.WriteAsync(auth, ct);

            var authReply = new byte[2];
            await ReadExactAsync(stream, authReply, ct);
            if (authReply[1] != 0x00)
                throw new IOException("Upstream SOCKS5 authentication failed.");

            // CONNECT to destination (domain form)
            var hostBytes = Encoding.UTF8.GetBytes(destHost);
            if (hostBytes.Length > 255)
                throw new InvalidOperationException("Destination host too long for SOCKS5.");

            var connect = new byte[7 + hostBytes.Length];
            connect[0] = 0x05;
            connect[1] = 0x01;
            connect[2] = 0x00;
            connect[3] = 0x03;
            connect[4] = (byte)hostBytes.Length;
            Buffer.BlockCopy(hostBytes, 0, connect, 5, hostBytes.Length);
            BinaryPrimitives.WriteUInt16BigEndian(connect.AsSpan(5 + hostBytes.Length), (ushort)destPort);
            await stream.WriteAsync(connect, ct);

            var replyHdr = new byte[4];
            await ReadExactAsync(stream, replyHdr, ct);
            if (replyHdr[1] != 0x00)
                throw new IOException($"Upstream SOCKS5 CONNECT failed (code {replyHdr[1]}).");

            // Consume bind address
            switch (replyHdr[3])
            {
                case 0x01:
                    await ReadExactAsync(stream, new byte[4 + 2], ct);
                    break;
                case 0x03:
                {
                    var len = new byte[1];
                    await ReadExactAsync(stream, len, ct);
                    await ReadExactAsync(stream, new byte[len[0] + 2], ct);
                    break;
                }
                case 0x04:
                    await ReadExactAsync(stream, new byte[16 + 2], ct);
                    break;
                default:
                    throw new IOException("Upstream SOCKS5 returned unknown address type.");
            }

            return upstream;
        }

        private static async Task WriteSocksReplyAsync(Stream stream, byte status, CancellationToken ct)
        {
            // VER REP RSV ATYP (IPv4 0.0.0.0) PORT 0
            await stream.WriteAsync(
                new byte[] { 0x05, status, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
        }

        private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
                if (n == 0)
                    throw new EndOfStreamException();
                read += n;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            _cts.Cancel();
            try { _listener.Stop(); } catch { /* ignore */ }
            try { await _acceptLoop; } catch { /* ignore */ }
            _cts.Dispose();
        }
    }
}
