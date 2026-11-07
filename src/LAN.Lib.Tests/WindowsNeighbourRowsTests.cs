using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LAN.Lib;
using Shouldly;
using Xunit;

namespace LAN.Lib.Tests;

/// <summary>Rows of Windows' neighbour table, built by hand: nothing here calls Windows, so it runs on every platform.</summary>
internal static class NeighbourRowBuilder
{
    public const uint Unreachable = 0, Incomplete = 1, Probe = 2, Delay = 3, Stale = 4, Reachable = 5, Permanent = 6;

    /// <summary>An IPv4 row: <c>SOCKADDR_IN</c> is the family, a port, then the four address bytes.</summary>
    public static MibIpNetRow2 V4(string address, string? mac, uint state, uint interfaceIndex = 12, uint? macLength = null)
    {
        var row = new MibIpNetRow2();
        Span<byte> sockaddr = row.Address;
        MemoryMarshal.Write(sockaddr, WindowsNeighbourRows.AfInet);
        IPAddress.Parse(address).TryWriteBytes(sockaddr[4..], out _).ShouldBeTrue();
        return Finish(row, mac, state, interfaceIndex, macLength);
    }

    /// <summary>An IPv6 row: <c>SOCKADDR_IN6</c> is the family, a port, the flow info, the 16 address bytes, the scope id.</summary>
    public static MibIpNetRow2 V6(string address, uint scopeId, string? mac, uint state, uint interfaceIndex = 12)
    {
        var row = new MibIpNetRow2();
        Span<byte> sockaddr = row.Address;
        MemoryMarshal.Write(sockaddr, WindowsNeighbourRows.AfInet6);
        IPAddress.Parse(address).TryWriteBytes(sockaddr[8..], out _).ShouldBeTrue();
        MemoryMarshal.Write(sockaddr[24..], scopeId);
        return Finish(row, mac, state, interfaceIndex, null);
    }

    /// <summary>A row whose address family is <paramref name="family"/>, which can be neither.</summary>
    public static MibIpNetRow2 OfFamily(ushort family, string mac, uint state)
    {
        var row = V4("10.0.0.1", mac, state);
        MemoryMarshal.Write((Span<byte>)row.Address, family);
        return row;
    }

    private static MibIpNetRow2 Finish(MibIpNetRow2 row, string? mac, uint state, uint interfaceIndex, uint? macLength)
    {
        row.InterfaceIndex = interfaceIndex;
        row.State = state;
        if (mac is not null)
        {
            var bytes = PhysicalAddress.Parse(mac).GetAddressBytes();
            Span<byte> buffer = row.PhysicalAddress;
            bytes.CopyTo(buffer);
            row.PhysicalAddressLength = macLength ?? (uint)bytes.Length;
        }
        return row;
    }
}

public class WindowsNeighbourRowsTests
{
    private const string DongleMac = "7C-DF-A1-00-11-22";

    [Fact]
    public void TheRowIsTheNativeRowByteForByte()
    {
        // MIB_IPNET_ROW2 as netioapi.h lays it out: GetIpNetTable2 returns an array of these, so a field off by a byte
        // reads another field's bytes as a MAC.
        var row = new MibIpNetRow2();
        ref var start = ref Unsafe.As<MibIpNetRow2, byte>(ref row);

        Unsafe.SizeOf<MibIpNetRow2>().ShouldBe(88);
        Unsafe.SizeOf<SockAddrInet>().ShouldBe(28);
        Unsafe.SizeOf<PhysicalAddressBuffer>().ShouldBe(32);
        Unsafe.ByteOffset(ref start, ref Unsafe.As<SockAddrInet, byte>(ref row.Address)).ShouldBe(0);
        Unsafe.ByteOffset(ref start, ref Unsafe.As<uint, byte>(ref row.InterfaceIndex)).ShouldBe(28);
        Unsafe.ByteOffset(ref start, ref Unsafe.As<ulong, byte>(ref row.InterfaceLuid)).ShouldBe(32);
        Unsafe.ByteOffset(ref start, ref Unsafe.As<PhysicalAddressBuffer, byte>(ref row.PhysicalAddress)).ShouldBe(40);
        Unsafe.ByteOffset(ref start, ref Unsafe.As<uint, byte>(ref row.PhysicalAddressLength)).ShouldBe(72);
        Unsafe.ByteOffset(ref start, ref Unsafe.As<uint, byte>(ref row.State)).ShouldBe(76);
        Unsafe.ByteOffset(ref start, ref row.Flags).ShouldBe(80);
        Unsafe.ByteOffset(ref start, ref Unsafe.As<uint, byte>(ref row.ReachabilityTime)).ShouldBe(84);
    }

    [Fact]
    public void AnIpv4RowIsAnEntryWithItsAddressMacStateAndInterface()
    {
        var row = NeighbourRowBuilder.V4("192.168.4.1", DongleMac, NeighbourRowBuilder.Reachable, interfaceIndex: 17);

        WindowsNeighbourRows.TryToEntry(in row, out var entry).ShouldBeTrue();

        entry.ShouldBe(new LanNeighbourEntry(IPAddress.Parse("192.168.4.1"), PhysicalAddress.Parse(DongleMac), LanNeighbourState.Reachable, "17"));
        entry.IsUsable.ShouldBeTrue();
    }

    [Fact]
    public void AnIpv6RowIsAnEntryWithItsScope()
    {
        var row = NeighbourRowBuilder.V6("fe80::1234", scopeId: 12, DongleMac, NeighbourRowBuilder.Stale);

        WindowsNeighbourRows.TryToEntry(in row, out var entry).ShouldBeTrue();

        entry.Address.ShouldBe(IPAddress.Parse("fe80::1234%12"));
        entry.Address.ScopeId.ShouldBe(12);
        entry.PhysicalAddress.ShouldBe(PhysicalAddress.Parse(DongleMac));
        entry.State.ShouldBe(LanNeighbourState.Stale);
    }

    [Theory]
    [InlineData(NeighbourRowBuilder.Reachable, LanNeighbourState.Reachable, true)]
    [InlineData(NeighbourRowBuilder.Stale, LanNeighbourState.Stale, true)]
    [InlineData(NeighbourRowBuilder.Delay, LanNeighbourState.Delay, true)]
    [InlineData(NeighbourRowBuilder.Probe, LanNeighbourState.Probe, true)]
    [InlineData(NeighbourRowBuilder.Permanent, LanNeighbourState.Permanent, true)]
    [InlineData(NeighbourRowBuilder.Incomplete, LanNeighbourState.Incomplete, false)]
    [InlineData(NeighbourRowBuilder.Unreachable, LanNeighbourState.Unreachable, false)]
    [InlineData(7u, LanNeighbourState.Incomplete, false)]
    [InlineData(uint.MaxValue, LanNeighbourState.Incomplete, false)]
    public void EachNlNeighborStateMapsAndOnlyAHeldAddressIsUsable(uint native, LanNeighbourState expected, bool usable)
    {
        var row = NeighbourRowBuilder.V4("192.168.4.1", DongleMac, native);

        WindowsNeighbourRows.TryToEntry(in row, out var entry).ShouldBeTrue();

        entry.State.ShouldBe(expected);
        entry.IsUsable.ShouldBe(usable);
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(4u, false)]
    [InlineData(6u, true)]
    [InlineData(8u, false)]
    [InlineData(20u, false)]
    [InlineData(33u, false)]
    [InlineData(uint.MaxValue, false)]
    public void OnlySixBytesAreAMac(uint length, bool isAMac)
    {
        // Whatever the length says, the buffer holds the same bytes: a link without a MAC gives no address, not a prefix of one.
        var row = NeighbourRowBuilder.V4("192.168.4.1", DongleMac, NeighbourRowBuilder.Reachable, macLength: length);

        WindowsNeighbourRows.TryToEntry(in row, out var entry).ShouldBeTrue();

        entry.PhysicalAddress.ShouldBe(isAMac ? PhysicalAddress.Parse(DongleMac) : PhysicalAddress.None);
        entry.IsUsable.ShouldBe(isAMac);
    }

    [Fact]
    public void OnlyTheFirstSixBytesOfTheBufferAreTheMac()
    {
        var row = NeighbourRowBuilder.V4("192.168.4.1", DongleMac, NeighbourRowBuilder.Reachable);
        ((Span<byte>)row.PhysicalAddress)[6] = 0xEE;
        ((Span<byte>)row.PhysicalAddress)[31] = 0xEE;

        WindowsNeighbourRows.TryToEntry(in row, out var entry).ShouldBeTrue();

        entry.PhysicalAddress.GetAddressBytes().Length.ShouldBe(6);
        entry.PhysicalAddress.ShouldBe(PhysicalAddress.Parse(DongleMac));
    }

    [Theory]
    [InlineData("00-00-00-00-00-00")]
    [InlineData("FF-FF-FF-FF-FF-FF")]
    public void AReachableRowWithAnAllZeroOrAllOnesMacIsNotUsable(string mac)
    {
        var row = NeighbourRowBuilder.V4("192.168.4.255", mac, NeighbourRowBuilder.Reachable);

        WindowsNeighbourRows.TryToEntry(in row, out var entry).ShouldBeTrue();

        entry.IsUsable.ShouldBeFalse();
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    [InlineData((ushort)17)]
    [InlineData(ushort.MaxValue)]
    public void ARowOfAnotherAddressFamilyIsNotAnEntry(ushort family)
    {
        var row = NeighbourRowBuilder.OfFamily(family, DongleMac, NeighbourRowBuilder.Reachable);

        WindowsNeighbourRows.TryToEntry(in row, out _).ShouldBeFalse();
    }

    [Fact]
    public void FindGivesTheRowForTheAddressAndNoOtherAndNullForNone()
    {
        MibIpNetRow2[] rows =
        [
            NeighbourRowBuilder.V4("192.168.4.7", "AA-BB-CC-DD-EE-01", NeighbourRowBuilder.Reachable),
            NeighbourRowBuilder.V4("192.168.4.1", DongleMac, NeighbourRowBuilder.Stale),
            NeighbourRowBuilder.V4("192.168.4.9", "AA-BB-CC-DD-EE-02", NeighbourRowBuilder.Reachable),
        ];

        WindowsNeighbourRows.Find(rows, IPAddress.Parse("192.168.4.1"))!.PhysicalAddress.ShouldBe(PhysicalAddress.Parse(DongleMac));
        WindowsNeighbourRows.Find(rows, IPAddress.Parse("192.168.4.2")).ShouldBeNull();
        WindowsNeighbourRows.Find([], IPAddress.Parse("192.168.4.1")).ShouldBeNull();
    }

    [Fact]
    public void FindPrefersARowThatNamesAHostWhenTheAddressIsHeldOnTwoAdapters()
    {
        // Two links that both use 192.168.4.x (a mount's dongle and a second one): the one that never answered must not hide
        // the one that did, whichever the table lists first.
        MibIpNetRow2[] unusableFirst =
        [
            NeighbourRowBuilder.V4("192.168.4.1", null, NeighbourRowBuilder.Incomplete, interfaceIndex: 5),
            NeighbourRowBuilder.V4("192.168.4.1", DongleMac, NeighbourRowBuilder.Reachable, interfaceIndex: 9),
        ];

        var picked = WindowsNeighbourRows.Find(unusableFirst, IPAddress.Parse("192.168.4.1"));

        picked!.PhysicalAddress.ShouldBe(PhysicalAddress.Parse(DongleMac));
        picked.Interface.ShouldBe("9");
    }

    [Fact]
    public void FindGivesTheFirstRowWhenNoneNamesAHost()
    {
        MibIpNetRow2[] rows =
        [
            NeighbourRowBuilder.V4("192.168.4.1", null, NeighbourRowBuilder.Incomplete, interfaceIndex: 5),
            NeighbourRowBuilder.V4("192.168.4.1", null, NeighbourRowBuilder.Unreachable, interfaceIndex: 9),
        ];

        var picked = WindowsNeighbourRows.Find(rows, IPAddress.Parse("192.168.4.1"));

        picked!.State.ShouldBe(LanNeighbourState.Incomplete);
        picked.IsUsable.ShouldBeFalse();
    }

    [Fact]
    public void FindSkipsARowItCannotRead()
    {
        MibIpNetRow2[] rows =
        [
            NeighbourRowBuilder.OfFamily(99, "AA-BB-CC-DD-EE-01", NeighbourRowBuilder.Reachable),
            NeighbourRowBuilder.V4("192.168.4.1", DongleMac, NeighbourRowBuilder.Reachable),
        ];

        WindowsNeighbourRows.Find(rows, IPAddress.Parse("192.168.4.1"))!.PhysicalAddress.ShouldBe(PhysicalAddress.Parse(DongleMac));
    }

    [Fact]
    public void AnIpv4AddressIsNeverFoundInAnIpv6Row()
    {
        // The same bytes at the same offset would not be the same host, and the families are not compared by their text.
        MibIpNetRow2[] rows = [NeighbourRowBuilder.V6("::ffff:192.168.4.1", 0, DongleMac, NeighbourRowBuilder.Reachable)];

        WindowsNeighbourRows.Find(rows, IPAddress.Parse("192.168.4.1")).ShouldBeNull();
    }

    [Theory]
    [InlineData("fe80::1234", 12u, "fe80::1234", 0u, true)]
    [InlineData("fe80::1234", 12u, "fe80::1234", 12u, true)]
    [InlineData("fe80::1234", 0u, "fe80::1234", 7u, true)]
    [InlineData("fe80::1234", 12u, "fe80::1234", 13u, false)]
    [InlineData("fe80::1234", 12u, "fe80::1235", 12u, false)]
    public void AnIpv6RowMatchesByAddressAndByZoneWhenBothHaveOne(string row, uint rowScope, string asked, uint askedScope, bool matches)
    {
        MibIpNetRow2[] rows = [NeighbourRowBuilder.V6(row, rowScope, DongleMac, NeighbourRowBuilder.Reachable)];
        var address = IPAddress.Parse(asked);
        address.ScopeId = askedScope;

        (WindowsNeighbourRows.Find(rows, address) is not null).ShouldBe(matches);
    }

    [Theory]
    [InlineData("192.168.4.1", new byte[] { 192, 168, 4, 1 })]
    [InlineData("10.0.0.254", new byte[] { 10, 0, 0, 254 })]
    [InlineData("0.0.0.0", new byte[] { 0, 0, 0, 0 })]
    public void SendArpTakesTheAddressAsItsFourBytesInWireOrder(string address, byte[] wireOrder)
    {
        // The IPAddr is a struct in_addr's memory: the same four bytes in memory, whichever way round the machine stores integers.
        BitConverter.GetBytes(WindowsNeighbourRows.ToIpAddr(IPAddress.Parse(address))).ShouldBe(wireOrder);
    }

    [Fact]
    public void SendArpIsOnlyForIpv4()
    {
        Should.Throw<ArgumentException>(() => WindowsNeighbourRows.ToIpAddr(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void SendArpGivesTheMacWhenItSucceedsWithSixBytes()
    {
        // SendARP is given an 8-byte buffer and says how much of it is the MAC.
        byte[] buffer = [0x7C, 0xDF, 0xA1, 0x00, 0x11, 0x22, 0xEE, 0xEE];

        WindowsNeighbourRows.ToPhysicalAddress(0, buffer, 6).ShouldBe(PhysicalAddress.Parse(DongleMac));
    }

    [Theory]
    [InlineData(67u, 6u, "ERROR_BAD_NET_NAME: not on a link")]
    [InlineData(31u, 6u, "ERROR_GEN_FAILURE: nobody answered")]
    [InlineData(50u, 6u, "ERROR_NOT_SUPPORTED")]
    [InlineData(0u, 0u, "success with no address")]
    [InlineData(0u, 8u, "success with an address that is not a MAC")]
    [InlineData(0u, 4u, "success with half of one")]
    public void SendArpGivesNothingForAnErrorOrALengthThatIsNotAMac(uint status, uint length, string why)
    {
        byte[] buffer = [0x7C, 0xDF, 0xA1, 0x00, 0x11, 0x22, 0xEE, 0xEE];

        WindowsNeighbourRows.ToPhysicalAddress(status, buffer, length).ShouldBeNull(why);
    }

    [Fact]
    public void SendArpGivesNothingForABufferShorterThanItsLength()
    {
        WindowsNeighbourRows.ToPhysicalAddress(0, new byte[4], 6).ShouldBeNull();
    }
}
