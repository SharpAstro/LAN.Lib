using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LAN.Lib;

/// <summary>
/// The three calls of the IP Helper library (<c>iphlpapi.dll</c>) the Windows neighbour table makes, through
/// source-generated <c>LibraryImport</c> marshalling: every parameter is a blittable integer, a pointer-sized value or a
/// <c>ref</c> to one, so nothing is marshalled at run time and nothing is reflected on, which keeps the package AOT and
/// trim clean. The library is looked up in the system directory only (on each import, since the attribute
/// does not apply to a class).
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class IpHelperNative
{
    private const string Library = "iphlpapi.dll";

    /// <summary>
    /// <c>GetIpNetTable2</c>: the neighbour table of one address family (<c>AF_INET</c>, <c>AF_INET6</c>, or 0 for both) as
    /// a <c>MIB_IPNET_TABLE2</c> that the call allocates and <see cref="FreeMibTable"/> frees. Returns the
    /// <c>NETIO_STATUS</c>: 0 is <c>NO_ERROR</c>, and only then is <paramref name="table"/> set.
    /// </summary>
    [LibraryImport(Library)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint GetIpNetTable2(ushort family, out nint table);

    /// <summary><c>FreeMibTable</c>: frees what <see cref="GetIpNetTable2"/> allocated.</summary>
    [LibraryImport(Library)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial void FreeMibTable(nint memory);

    /// <summary>
    /// <c>SendARP</c>: asks the link for the MAC of an IPv4 address (an ARP request, and a wait for the reply) and fills
    /// <paramref name="mac"/>, a buffer of at least 8 bytes, with it; <paramref name="macLength"/> is the size of that
    /// buffer going in and the length of the MAC coming out. <paramref name="destination"/> and <paramref name="source"/>
    /// are <c>IPAddr</c>s (see <see cref="WindowsNeighbourRows.ToIpAddr"/>), a source of 0 meaning any. Returns the Win32
    /// error: 0 is <c>NO_ERROR</c>. It cannot be cancelled and can take seconds when nobody answers.
    /// </summary>
    [LibraryImport(Library)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint SendARP(uint destination, uint source, ref byte mac, ref uint macLength);
}
