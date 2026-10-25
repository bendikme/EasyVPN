using System.IO;

namespace EasyVPN.GatewayApp;

public enum FieldKind
{
    Text,
    /// <summary>A file, chosen with Browse and copied into the gateway's own folder.</summary>
    File,
    /// <summary>A program installed on the gateway, chosen with Browse and left where it is.</summary>
    Program,
    /// <summary>A check box: "true", or left out.</summary>
    Flag,
    /// <summary>One of <see cref="ConnectorField.Choices"/>.</summary>
    Choice,
}

/// <summary>
/// One of a connector's settings, as the facility editor asks for it. It reaches the connector as
/// <c>EASYVPN_&lt;KEY&gt;</c>. A <see cref="Shared"/> one can also be set once for all the
/// connector's facilities, in the gateway app's Settings.
/// </summary>
public sealed record ConnectorField(
    string Key,
    string Label,
    string Hint,
    FieldKind Kind = FieldKind.Text,
    bool Required = false,
    string? Filter = null,
    IReadOnlyList<string>? Choices = null,
    bool Shared = false,
    string? SharedHint = null);

/// <summary>
/// A kind of VPN client the editor can set a facility up for: one script in connectors\. The
/// scripts' headers are the truth; these mirror them. A script the editor does not know (one of
/// this gateway's own) gets its settings as plain lines.
/// </summary>
public sealed record ConnectorKind(string Name, string Title, string Description, bool UsesPassword, IReadOnlyList<ConnectorField> Fields)
{
    public bool IsCustom { get; private init; }

    /// <summary>Its password can be set once for all its facilities, in Settings: one client's own, rather than each server's.</summary>
    public bool SharesPassword { get; init; }

    /// <summary>Anything to set in Settings.</summary>
    public bool HasShared => SharesPassword || Fields.Any(f => f.Shared);

    public static ConnectorKind Custom(string name) =>
        new(name, name, "A connector of this gateway's own: its settings are whatever its script reads.", true, []) { IsCustom = true };
}

public static class ConnectorKinds
{
    private static readonly ConnectorField User = new("user", "User name", "The password goes below.");

    public static readonly IReadOnlyList<ConnectorKind> Known =
    [
        new("openvpn", "OpenVPN",
            "An .ovpn profile, run by OpenVPN 2.6 or later. A Sophos SSL VPN profile is an OpenVPN profile too.",
            true,
            [
                new("config", "Profile", "The .ovpn file. It is copied into the gateway's own folder, where only administrators can change it, and read for its networks.",
                    FieldKind.File, Required: true, Filter: "OpenVPN profiles (*.ovpn;*.conf)|*.ovpn;*.conf|All files (*.*)|*.*"),
                User,
                new("legacy", "Allow old certificates", "Signed with SHA1, or with RSA keys under 2048 bits: OpenVPN 2.6 refuses them otherwise. Ticked by itself for a profile that has them; better still, the customer makes new ones.",
                    FieldKind.Flag),
            ]),
        new("tosibox", "Tosibox",
            "Tosi Client (once TOSIBOX Key) on this gateway's desktop, driven by the EasyVPN Gateway app there: it starts the client if need be, enters its password if asked, finds the Lock, presses Connect or Disconnect, and waits until the client shows it. Someone must be signed in to the gateway's desktop.",
            true,
            [
                new("lock", "Lock", "Its name exactly as Tosi Client lists it, such as Tosibox 15 [Matbørsen].", Required: true),
                new("program", "Tosi Client's program", "TosiboxKey.exe, which starts the client. Set once for every Tosibox facility in Settings; one here is for this facility alone.",
                    FieldKind.Program, Filter: "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk", Shared: true,
                    SharedHint: "TosiboxKey.exe, which starts Tosi Client when it is not running, in its own folder. Or its Start menu shortcut, to start it just as that does. Empty: EasyVPN looks for it among the installed programs."),
            ]) { SharesPassword = true },
        new("sophos", "Sophos Connect",
            "Sophos Connect, through its command line sccli: for IPsec (.scx) and provisioning (.pro) files, which only it takes.",
            true,
            [
                new("config", "Connection file", "Imported into Sophos Connect each time it connects. Leave it empty for a connection already there.",
                    FieldKind.File, Filter: "Sophos Connect files (*.scx;*.pro;*.ovpn)|*.scx;*.pro;*.ovpn|All files (*.*)|*.*"),
                new("connection", "Connection name", "Its name in Sophos Connect. Empty: the facility's id."),
                User,
            ]),
        new("netextender", "SonicWall NetExtender",
            "NetExtender, through its command line (nxcli, or NECLI in 10.2 and earlier).",
            true,
            [
                new("host", "Server", "As server or server:port.", Required: true),
                new("domain", "Domain", "The domain the server asks for, such as LocalDomain."),
                User,
                new("trust", "Accept the server's certificate without checking it", "Only for a server with a self-signed certificate: NetExtender would stop and ask otherwise.",
                    FieldKind.Flag),
            ]),
        new("pulse", "Ivanti / Pulse Secure",
            "Ivanti Secure Access (once Pulse Secure), through pulselauncher. It cannot answer a token after the password, or choose between roles.",
            true,
            [
                new("url", "Sign-in address", "Such as https://vpn.example.com.", Required: true),
                new("realm", "Realm", "As the sign-in page names it.", Required: true),
                User with { Hint = "Required. The password goes below.", Required = true },
            ]),
        new("openconnect", "OpenConnect",
            "The open-source client, for GlobalProtect above all; also Pulse, Fortinet, F5, Array and AnyConnect servers that sign in with a user name and password.",
            true,
            [
                new("protocol", "Protocol", "gp is GlobalProtect; nc is older Juniper and Pulse.",
                    FieldKind.Choice, Choices: ["gp", "pulse", "nc", "fortinet", "f5", "array", "anyconnect"]),
                new("host", "Server or portal", "", Required: true),
                User,
                new("group", "Gateway or group", "When the server offers several."),
                new("servercert", "Server certificate pin", "pin-sha256:… as OpenConnect prints it, for a certificate Windows does not trust."),
            ]),
        new("cisco", "Cisco Secure Client",
            "Cisco Secure Client or AnyConnect, through vpncli.",
            true,
            [
                new("host", "Server", "As it is typed into the Cisco client.", Required: true),
                User,
                new("group", "Group", "When the server offers several."),
            ]),
        new("windows", "Windows VPN",
            "The VPN built into Windows (IKEv2, SSTP, L2TP), dialled with rasdial. Make the connection for all users first: Add-VpnConnection -AllUserConnection.",
            true,
            [
                new("name", "Connection name", "As Add-VpnConnection named it.", Required: true),
                User,
            ]),
        new("wireguard", "WireGuard",
            "A tunnel run by WireGuard for Windows.",
            false,
            [
                new("config", "Tunnel file", "The .conf file. It is copied into the gateway's own folder; the tunnel takes its name.",
                    FieldKind.File, Required: true, Filter: "WireGuard tunnels (*.conf)|*.conf|All files (*.*)|*.*"),
            ]),
        new("manual", "By hand",
            "Someone connects it in its own client on this gateway's desktop: a sign-in with a token or in a browser, any client without a command line. EasyVPN waits for the check (or for the networks its VPN sets up, when Routes is empty), for up to the timeout.",
            false,
            []),
    ];

    /// <summary>The ones known here, then any other script in connectors\: shipped beside the app, or this gateway's own.</summary>
    public static IReadOnlyList<ConnectorKind> Available(string dataFolder)
    {
        var scripts = new[] { dataFolder, AppContext.BaseDirectory }
            .Select(folder => Path.Combine(folder, "connectors"))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.ps1"))
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>();
        return [.. Known, .. scripts.Where(name => Find(name) is null).Distinct(StringComparer.OrdinalIgnoreCase).Order().Select(ConnectorKind.Custom)];
    }

    public static ConnectorKind? Find(string name) =>
        Known.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));
}
