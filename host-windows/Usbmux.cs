using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace IPadScreenHost;

internal sealed record UsbDevice(long DeviceId, string Serial, string ConnectionType, long ProductId);

/// <summary>
/// Minimal client for Apple's usbmux daemon. On Windows it is part of "Apple Mobile Device Support"
/// (installed by the "Apple Devices" Microsoft Store app or iTunes) and listens on 127.0.0.1:27015.
/// After a successful "Connect" the socket becomes a raw TCP tunnel to a port on the iPad over USB.
/// </summary>
internal static class Usbmux
{
    private const int MuxPort = 27015;
    private const uint PlistVersion = 1, PlistMessage = 8;

    public sealed class UsbmuxException(string message) : Exception(message);

    public static List<UsbDevice> ListDevices()
    {
        using var client = Open();
        var stream = client.GetStream();
        Send(stream, Request("ListDevices"), tag: 1);
        var reply = Receive(stream);
        var list = new List<UsbDevice>();
        if (reply.TryGetValue("DeviceList", out var dl) && dl is List<object> devices)
        {
            foreach (var d in devices.OfType<Dictionary<string, object>>())
            {
                if (d.GetValueOrDefault("Properties") is not Dictionary<string, object> p) continue;
                list.Add(new UsbDevice(
                    Convert.ToInt64(p.GetValueOrDefault("DeviceID") ?? d.GetValueOrDefault("DeviceID") ?? 0L),
                    p.GetValueOrDefault("SerialNumber") as string ?? "?",
                    p.GetValueOrDefault("ConnectionType") as string ?? "?",
                    Convert.ToInt64(p.GetValueOrDefault("ProductID") ?? 0L)));
            }
        }
        return list;
    }

    /// <summary>Opens a tunnel to <paramref name="port"/> on the device. Returns the connected socket.</summary>
    public static TcpClient Connect(long deviceId, int port)
    {
        var client = Open();
        try
        {
            var stream = client.GetStream();
            var req = Request("Connect");
            req["DeviceID"] = deviceId;
            // usbmuxd expects the port in network byte order, stored in a host-order integer.
            req["PortNumber"] = (long)BinaryPrimitives.ReverseEndianness((ushort)port);
            Send(stream, req, tag: 2);
            var reply = Receive(stream);
            long result = Convert.ToInt64(reply.GetValueOrDefault("Number") ?? -1L);
            if (result != 0)
            {
                throw new UsbmuxException(result switch
                {
                    2 => "device not found (unplugged?)",
                    3 => $"connection refused - is the Second Screen app open on the iPad (listening on port {port})?",
                    6 => "usbmux protocol version mismatch",
                    _ => $"usbmux connect failed with code {result}",
                });
            }
            client.NoDelay = true;
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static TcpClient Open()
    {
        var c = new TcpClient { NoDelay = true };
        try
        {
            c.Connect("127.0.0.1", MuxPort);
        }
        catch (SocketException)
        {
            c.Dispose();
            throw new UsbmuxException(
                "Apple's USB service for iPads is not running (127.0.0.1:27015 refused). Fix one of these ways:\n" +
                "  - iTunes from the Microsoft Store: Settings > Apps > Startup > turn ON \"AppleMobileDeviceProcess\",\n" +
                "    then sign out and back in (it is off by default).\n" +
                "  - or install \"Apple Devices\" from the Microsoft Store, open it once, and reconnect the iPad.\n" +
                "  (Or use Wi-Fi mode meanwhile: --wifi <iPad IP shown in the app>)");
        }
        return c;
    }

    private static Dictionary<string, object> Request(string messageType) => new()
    {
        ["MessageType"] = messageType,
        ["ClientVersionString"] = "ipad-second-screen-host",
        ["ProgName"] = "IPadScreenHost",
        ["kLibUSBMuxVersion"] = 3L,
    };

    private static void Send(NetworkStream s, Dictionary<string, object> dict, uint tag)
    {
        byte[] body = Encoding.UTF8.GetBytes(Plist.Write(dict));
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), (uint)(16 + body.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), PlistVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), PlistMessage);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), tag);
        s.Write(header);
        s.Write(body);
    }

    private static Dictionary<string, object> Receive(NetworkStream s)
    {
        s.ReadTimeout = 5000;
        var header = new byte[16];
        s.ReadExactly(header);
        int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (len < 16 || len > 16 * 1024 * 1024) throw new UsbmuxException($"bad usbmux reply length {len}");
        var body = new byte[len - 16];
        s.ReadExactly(body);
        s.ReadTimeout = Timeout.Infinite;
        return Plist.Read(body);
    }
}

/// <summary>Just enough XML property-list support for usbmux messages.</summary>
internal static class Plist
{
    public static string Write(Dictionary<string, object> dict)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n");
        WriteValue(sb, dict);
        sb.Append("</plist>\n");
        return sb.ToString();
    }

    private static void WriteValue(StringBuilder sb, object v)
    {
        switch (v)
        {
            case string s: sb.Append("<string>").Append(SecurityElement.Escape(s)).Append("</string>\n"); break;
            case long or int: sb.Append("<integer>").Append(Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture)).Append("</integer>\n"); break;
            case bool b: sb.Append(b ? "<true/>\n" : "<false/>\n"); break;
            case Dictionary<string, object> d:
                sb.Append("<dict>\n");
                foreach (var (k, val) in d)
                {
                    sb.Append("<key>").Append(SecurityElement.Escape(k)).Append("</key>\n");
                    WriteValue(sb, val);
                }
                sb.Append("</dict>\n");
                break;
            default: throw new NotSupportedException(v.GetType().Name);
        }
    }

    public static Dictionary<string, object> Read(byte[] xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(new MemoryStream(xml), settings);
        var doc = XDocument.Load(reader);
        var root = doc.Root?.Elements().FirstOrDefault() ?? throw new FormatException("empty plist");
        return ReadValue(root) as Dictionary<string, object> ?? throw new FormatException("plist root is not a dict");
    }

    private static object ReadValue(XElement e) => e.Name.LocalName switch
    {
        "dict" => ReadDict(e),
        "array" => e.Elements().Select(ReadValue).ToList(),
        "string" => e.Value,
        "integer" => long.Parse(e.Value, CultureInfo.InvariantCulture),
        "real" => double.Parse(e.Value, CultureInfo.InvariantCulture),
        "true" => true,
        "false" => false,
        "data" => Convert.FromBase64String(string.Concat(e.Value.Where(c => !char.IsWhiteSpace(c)))),
        _ => e.Value,
    };

    private static Dictionary<string, object> ReadDict(XElement e)
    {
        var d = new Dictionary<string, object>();
        var items = e.Elements().ToList();
        for (int i = 0; i + 1 < items.Count; i += 2)
            d[items[i].Value] = ReadValue(items[i + 1]);
        return d;
    }
}
