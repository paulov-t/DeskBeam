using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Xml.Linq;
using DeskBeam.Core.Models;

namespace DeskBeam.Core.Net;

/// <summary>
/// Minimal UPnP IGD (Internet Gateway Device) client: SSDP discovery, SOAP AddPortMapping /
/// DeletePortMapping / GetExternalIPAddress. No third-party dependency so it works on any
/// router that exposes WANIPConnection or WANPPPConnection.
/// </summary>
public sealed class UpnpPortMapper : IDisposable
{
    private sealed record Gateway(string Name, string ServiceType, Uri ControlUri);

    private sealed class UpnpException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private static readonly string[] SearchTargets =
    [
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "upnp:rootdevice",
        // Some consumer routers only answer a catch-all search.
        "ssdp:all",
    ];

    private readonly HttpClient _http;
    private readonly List<(int Port, string Protocol, string Description)> _mapped = [];
    private Gateway? _gateway;

    public UpnpPortMapper()
    {
        // Routers live on the LAN, so system/VPN proxies must never be used for discovery or SOAP.
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    public event Action<string>? Log;

    public bool HasGateway => _gateway is not null;

    public Task<UpnpMappingResult> MapAsync(
        int controlPort,
        int videoPort,
        IPAddress localAddress,
        CancellationToken ct = default) =>
        MapAsync(controlPort, videoPort, 0, localAddress, ct);

    public async Task<UpnpMappingResult> MapAsync(
        int controlPort,
        int videoPort,
        int audioPort,
        IPAddress localAddress,
        CancellationToken ct = default)
    {
        if (localAddress.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(localAddress))
            return Failure("No LAN IPv4 address available for port mapping.");

        try
        {
            _gateway ??= await DiscoverGatewayAsync(ct).ConfigureAwait(false);
            if (_gateway is null)
                return Failure("No UPnP gateway replied. Enable UPnP on the router, or forward ports manually.");

            Log?.Invoke($"UPnP gateway: {_gateway.Name}");
            string? externalIp = null;
            try
            {
                externalIp = await GetExternalIpAsync(_gateway, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log?.Invoke("Could not read the external IP: " + ex.Message);
            }

            var mappedControl = await TryAddMappingAsync(_gateway, localAddress, controlPort, "TCP",
                "DeskBeam control", ct).ConfigureAwait(false);
            if (mappedControl is null)
                return Failure("The router rejected the control port mapping (it may already be used by another app).");

            var mappedVideo = await TryAddMappingAsync(_gateway, localAddress, videoPort, "TCP", "DeskBeam video", ct)
                .ConfigureAwait(false);
            if (mappedVideo is null)
            {
                try
                {
                    await TryDeleteMappingAsync(_gateway, controlPort, "TCP", ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Best effort rollback.
                }

                _mapped.Clear();
                return Failure("The router rejected the video port mapping.");
            }

            // Audio is best-effort: a router that refuses the UDP mapping must not take video down.
            var mappedAudio = false;
            if (audioPort > 0)
            {
                var audioMapping = await TryAddMappingAsync(_gateway, localAddress, audioPort, "UDP",
                    "DeskBeam audio", ct).ConfigureAwait(false);
                mappedAudio = audioMapping is not null;
                if (!mappedAudio)
                    Log?.Invoke($"Router refused UDP audio port {audioPort}; video is still mapped.");
            }

            var audioNote = audioPort > 0 && !mappedAudio
                ? " Audio is only reachable on the LAN because the UDP port could not be mapped."
                : string.Empty;
            var message = (externalIp is null
                              ? "Ports mapped. The router did not report an external IP."
                              : $"Ports mapped. Share {externalIp}:{controlPort} with the remote player.") + audioNote;

            return new UpnpMappingResult(true, message, externalIp, controlPort, videoPort, _gateway.Name,
                mappedAudio ? audioPort : 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log?.Invoke("UPnP failed: " + ex.Message);
            return Failure(ex.Message);
        }
    }

    public async Task UnmapAsync(CancellationToken ct = default)
    {
        if (_gateway is null)
            return;

        foreach (var (port, protocol, description) in _mapped.ToArray())
        {
            try
            {
                await TryDeleteMappingAsync(_gateway, port, protocol, ct).ConfigureAwait(false);
                Log?.Invoke($"Removed UPnP mapping for {protocol} {port} ({description}).");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Could not remove UPnP mapping {protocol} {port}: {ex.Message}");
            }
        }

        _mapped.Clear();
    }

    private async Task<string?> TryAddMappingAsync(Gateway gateway, IPAddress local, int port, string protocol,
        string description, CancellationToken ct)
    {
        var args =
            "<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{port}</NewExternalPort>" +
            $"<NewProtocol>{protocol}</NewProtocol>" +
            $"<NewInternalPort>{port}</NewInternalPort>" +
            $"<NewInternalClient>{local}</NewInternalClient>" +
            "<NewEnabled>1</NewEnabled>" +
            $"<NewPortMappingDescription>{Escape(description)}</NewPortMappingDescription>" +
            "<NewLeaseDuration>0</NewLeaseDuration>";

        // A mapping left behind by an earlier run (or another app) makes AddPortMapping return
        // 718 ConflictInMappingEntry, so always clear the port first and retry once if needed.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await TryDeleteMappingAsync(gateway, port, protocol, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Some routers dislike deleting a mapping that does not exist; ignore.
            }

            try
            {
                await InvokeAsync(gateway, "AddPortMapping", args, ct).ConfigureAwait(false);
                if (!_mapped.Any(m => m.Port == port && m.Protocol == protocol))
                    _mapped.Add((port, protocol, description));
                Log?.Invoke($"Mapped {protocol} {port} -> {local}:{port}.");
                return local.ToString();
            }
            catch (UpnpException ex) when (attempt == 0 && ex.Code is 718 or 501)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            catch (UpnpException ex)
            {
                Log?.Invoke($"Router refused port {port}: {ex.Code} {ex.Message}");
                return null;
            }
        }

        return null;
    }

    private async Task TryDeleteMappingAsync(Gateway gateway, int port, string protocol, CancellationToken ct)
    {
        var args =
            "<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{port}</NewExternalPort>" +
            $"<NewProtocol>{protocol}</NewProtocol>";
        await InvokeAsync(gateway, "DeletePortMapping", args, ct).ConfigureAwait(false);
    }

    private async Task<string?> GetExternalIpAsync(Gateway gateway, CancellationToken ct)
    {
        var doc = await InvokeAsync(gateway, "GetExternalIPAddress", string.Empty, ct).ConfigureAwait(false);
        return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value;
    }

    private async Task<XDocument> InvokeAsync(Gateway gateway, string action, string args, CancellationToken ct)
    {
        var body =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            $"<s:Body><u:{action} xmlns:u=\"{gateway.ServiceType}\">{args}</u:{action}></s:Body>" +
            "</s:Envelope>";

        using var request = new HttpRequestMessage(HttpMethod.Post, gateway.ControlUri);
        request.Content = new StringContent(body, Encoding.UTF8, "text/xml");
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{gateway.ServiceType}#{action}\"");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        XDocument doc;
        try
        {
            doc = XDocument.Parse(text);
        }
        catch (Exception)
        {
            throw new InvalidOperationException($"Router returned an invalid SOAP response ({(int)response.StatusCode}).");
        }

        var errorCode = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value;
        if (errorCode is not null)
        {
            var description = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value
                              ?? "UPnP error";
            throw new UpnpException(int.TryParse(errorCode, out var code) ? code : -1, description);
        }

        return doc;
    }

    private async Task<Gateway?> DiscoverGatewayAsync(CancellationToken ct)
    {
        var locations = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var sockets = new List<UdpClient>();

        // Send from every LAN interface: with VPN/virtual adapters present, a single unbound
        // socket often sends the M-SEARCH out of the wrong interface and no gateway answers.
        var addresses = NetworkUtil.GetLocalIPv4().ToList();
        var primary = NetworkUtil.GetPrimaryIPv4();
        if (primary is not null)
            addresses.RemoveAll(a => a.Equals(primary));
        if (primary is not null)
            addresses.Insert(0, primary);

        try
        {
            foreach (var address in addresses.Take(6))
            {
                try
                {
                    var udp = new UdpClient(new IPEndPoint(address, 0));
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, false);
                    try
                    {
                        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                            address.GetAddressBytes());
                    }
                    catch (SocketException)
                    {
                        // Interface selection is best effort.
                    }

                    sockets.Add(udp);
                }
                catch (SocketException ex)
                {
                    Log?.Invoke($"SSDP could not bind {address}: {ex.Message}");
                }
            }

            if (sockets.Count == 0)
            {
                var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                sockets.Add(udp);
            }

            using var discoveryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            discoveryCts.CancelAfter(TimeSpan.FromSeconds(6));

            var receiveTasks = sockets
                .Select(socket => ReceiveSsdpAsync(socket, locations, discoveryCts.Token))
                .ToArray();

            var endpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            for (var round = 0; round < 3; round++)
            {
                foreach (var socket in sockets)
                {
                    foreach (var target in SearchTargets)
                        SendSearch(socket, endpoint, target);
                }

                await Task.Delay(round == 0 ? 300 : 900, ct).ConfigureAwait(false);
            }

            var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                foreach (var location in locations.Keys.ToArray())
                {
                    if (!attempted.Add(location))
                        continue;

                    var gateway = await TryDescribeAsync(location, ct).ConfigureAwait(false);
                    if (gateway is not null)
                        return gateway;
                }

                await Task.Delay(150, ct).ConfigureAwait(false);
            }

            await Task.WhenAll(receiveTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Discovery window elapsed.
        }
        finally
        {
            foreach (var socket in sockets)
                socket.Dispose();
        }

        return null;
    }

    private async Task<Gateway?> TryDescribeAsync(string location, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(location, UriKind.Absolute, out var uri))
                return null;

            var xml = await _http.GetStringAsync(uri, ct).ConfigureAwait(false);

            // Handle the "error" where it is providing and OK status, not XML
            if (xml.StartsWith("status"))
            {
                return null;
            }

            return ParseDescription(xml, uri);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Ignoring UPnP device {location}: {ex.Message}");
            return null;
        }
    }

    private static void SendSearch(UdpClient udp, IPEndPoint endpoint, string target)
    {
        var request =
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            $"ST: {target}\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(request);
        try
        {
            udp.Send(bytes, bytes.Length, endpoint);
        }
        catch (SocketException)
        {
            // Try the next interface.
        }
    }

    private static async Task ReceiveSsdpAsync(UdpClient udp, ConcurrentDictionary<string, byte> locations,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                var text = Encoding.ASCII.GetString(result.Buffer);
                var location = ParseSsdpHeader(text, "LOCATION");
                if (!string.IsNullOrWhiteSpace(location))
                    locations.TryAdd(location, 0);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static Gateway? ParseDescription(string xml, Uri location)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception)
        {
            return null;
        }

        var friendlyName = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "friendlyName")?.Value
                           ?? location.Host;

        var urlBase = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value;
        var baseUri = Uri.TryCreate(urlBase, UriKind.Absolute, out var parsedBase) ? parsedBase : location;

        foreach (var service in doc.Descendants().Where(e => e.Name.LocalName == "service"))
        {
            var type = service.Descendants().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value;
            if (string.IsNullOrWhiteSpace(type) ||
                (!type.Contains("WANIPConnection", StringComparison.OrdinalIgnoreCase) &&
                 !type.Contains("WANPPPConnection", StringComparison.OrdinalIgnoreCase)))
                continue;

            var control = service.Descendants().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value;
            if (string.IsNullOrWhiteSpace(control))
                continue;

            if (!Uri.TryCreate(baseUri, control, out var controlUri))
                continue;

            return new Gateway(friendlyName, type, controlUri);
        }

        return null;
    }

    private static string? ParseSsdpHeader(string response, string header)
    {
        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim();
            var index = trimmed.IndexOf(':');
            if (index <= 0)
                continue;
            if (trimmed.AsSpan(0, index).Equals(header, StringComparison.OrdinalIgnoreCase))
                return trimmed[(index + 1)..].Trim();
        }

        return null;
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private static UpnpMappingResult Failure(string message) =>
        new(false, message, null, 0, 0, null);

    public void Dispose() => _http.Dispose();
}
