using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Nexora.Core.Network;

public interface IPinger
{
    /// <returns>Round-trip time in ms, or null if the probe was lost.</returns>
    Task<double?> PingAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>ICMP echo using the OS API (no raw sockets, no elevation required on Windows).</summary>
public sealed class IcmpPinger : IPinger
{
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("NEXORA-LATENCY-PROBE-32-BYTES!!");

    public async Task<double?> PingAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        using var ping = new Ping();
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var reply = await ping.SendPingAsync(address, timeout, Payload, new PingOptions(128, dontFragment: true), cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            if (reply.Status != IPStatus.Success)
            {
                return null;
            }

            // RoundtripTime is integer ms; prefer the higher-resolution stopwatch when it is consistent.
            var measured = stopwatch.Elapsed.TotalMilliseconds;
            return reply.RoundtripTime > 0 && measured > reply.RoundtripTime + 5 ? reply.RoundtripTime : Math.Max(measured, reply.RoundtripTime);
        }
        catch (PingException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}

public interface IDnsProbe
{
    /// <summary>Measures a single uncached query directly against a resolver.</summary>
    Task<double?> MeasureAsync(IPAddress resolver, string hostName, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends a standard DNS A query over UDP straight to the configured resolver, bypassing the local
/// DNS cache, so the measured time reflects real resolver latency.
/// </summary>
public sealed class UdpDnsProbe : IDnsProbe
{
    public async Task<double?> MeasureAsync(IPAddress resolver, string hostName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        var id = (ushort)RandomNumberGenerator.GetInt32(0, ushort.MaxValue + 1);
        var query = DnsMessage.BuildQuery(id, hostName);
        using var client = new UdpClient(resolver.AddressFamily);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            client.Connect(resolver, 53);
            var stopwatch = Stopwatch.StartNew();
            await client.SendAsync(query, cts.Token).ConfigureAwait(false);
            while (true)
            {
                var response = await client.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (DnsMessage.IsResponseTo(response.Buffer, id))
                {
                    return stopwatch.Elapsed.TotalMilliseconds;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}

public static class DnsMessage
{
    /// <summary>Builds a recursive A-record query (RFC 1035).</summary>
    public static byte[] BuildQuery(ushort id, string hostName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        var labels = hostName.TrimEnd('.').Split('.');
        var length = 12 + labels.Sum(l => l.Length + 1) + 1 + 4;
        var buffer = new byte[length];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0), id);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), 0x0100); // RD
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4), 1); // QDCOUNT
        var offset = 12;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label.Any(c => c > 127))
            {
                throw new ArgumentException("Invalid DNS label.", nameof(hostName));
            }

            buffer[offset++] = (byte)label.Length;
            offset += Encoding.ASCII.GetBytes(label, buffer.AsSpan(offset));
        }

        buffer[offset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), 1); // QTYPE A
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 2), 1); // QCLASS IN
        return buffer;
    }

    public static bool IsResponseTo(ReadOnlySpan<byte> message, ushort id)
        => message.Length >= 12
           && BinaryPrimitives.ReadUInt16BigEndian(message) == id
           && (message[2] & 0x80) != 0;
}
