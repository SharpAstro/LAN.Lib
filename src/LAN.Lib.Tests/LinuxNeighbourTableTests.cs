using System;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using LAN.Lib;
using Shouldly;
using Xunit;

namespace LAN.Lib.Tests;

public class LinuxNeighbourTableTests
{
    /// <summary>What /proc/net/arp looks like on a laptop with WiFi, a Docker bridge and a half-learnt neighbour.</summary>
    private const string Fixture =
        "IP address       HW type     Flags       HW address            Mask     Device\n" +
        "192.168.1.1      0x1         0x2         aa:bb:cc:dd:ee:ff     *        wlan0\n" +
        "192.168.1.9      0x1         0x0         00:00:00:00:00:00     *        wlan0\n" +
        "192.168.4.1      0x1         0x2         7c:df:a1:00:11:22     *        wlan1\n" +
        "172.17.0.2       0x1         0x6         02:42:ac:11:00:02     *        docker0\n";

    private static readonly PhysicalAddress DongleMac = PhysicalAddress.Parse("7C-DF-A1-00-11-22");

    [Fact]
    public void ACompleteRowIsAnEntryWithItsAddressMacAndDevice()
    {
        var entries = LinuxNeighbourTable.ParseProcNetArp(Fixture);

        entries[2].ShouldBe(new LanNeighbourEntry(IPAddress.Parse("192.168.4.1"), DongleMac, LanNeighbourState.Complete, "wlan1"));
        entries[0].PhysicalAddress.ShouldBe(PhysicalAddress.Parse("AA-BB-CC-DD-EE-FF"));
        entries[0].State.ShouldBe(LanNeighbourState.Complete, "this table does not say how fresh a row is");
    }

    [Fact]
    public void AnIncompleteRowIsIncompleteWithItsZeroMac()
    {
        var incomplete = LinuxNeighbourTable.ParseProcNetArp(Fixture)[1];

        incomplete.Address.ShouldBe(IPAddress.Parse("192.168.1.9"));
        incomplete.State.ShouldBe(LanNeighbourState.Incomplete);
        incomplete.PhysicalAddress.ShouldBe(new PhysicalAddress(new byte[6]));
        incomplete.IsUsable.ShouldBeFalse();
    }

    [Fact]
    public void SeveralDevicesGiveOneEntryPerRowInOrderAndTheHeaderIsSkipped()
    {
        var entries = LinuxNeighbourTable.ParseProcNetArp(Fixture);

        entries.Count.ShouldBe(4);
        entries.Select(e => e.Interface).ShouldBe(["wlan0", "wlan0", "wlan1", "docker0"]);
        entries.Select(e => e.Address.ToString()).ShouldBe(["192.168.1.1", "192.168.1.9", "192.168.4.1", "172.17.0.2"]);
    }

    [Fact]
    public void ARowWithThePermanentBitIsPermanent()
    {
        var docker = LinuxNeighbourTable.ParseProcNetArp(Fixture)[3];

        docker.State.ShouldBe(LanNeighbourState.Permanent, "0x6 is complete and permanent");
        docker.IsUsable.ShouldBeTrue();
    }

    [Theory]
    [InlineData("0x0", LanNeighbourState.Incomplete)]
    [InlineData("0x1", LanNeighbourState.Incomplete)]
    [InlineData("0x2", LanNeighbourState.Complete)]
    [InlineData("0x6", LanNeighbourState.Permanent)]
    [InlineData("0xa", LanNeighbourState.Complete)]
    [InlineData("0x4", LanNeighbourState.Incomplete)]
    [InlineData("0x12", LanNeighbourState.Complete)]
    public void TheFlagsFieldSaysCompleteOrPermanentOrNeither(string flags, LanNeighbourState expected)
    {
        var entries = LinuxNeighbourTable.ParseProcNetArp($"192.168.1.1 0x1 {flags} aa:bb:cc:dd:ee:ff * eth0");

        entries.Count.ShouldBe(1);
        entries[0].State.ShouldBe(expected);
    }

    [Fact]
    public void TheHardwareAddressInUpperCaseAndTheFlagsWithoutTheirPrefixAreStillARow()
    {
        var entries = LinuxNeighbourTable.ParseProcNetArp("192.168.1.1 1 2 AA:BB:CC:DD:EE:FF * eth0");

        entries.Count.ShouldBe(1);
        entries[0].PhysicalAddress.ShouldBe(PhysicalAddress.Parse("AA-BB-CC-DD-EE-FF"));
        entries[0].State.ShouldBe(LanNeighbourState.Complete);
    }

    [Fact]
    public void WindowsLineEndingsAndBlankLinesAndNoTrailingNewlineAreAllRight()
    {
        var text = Fixture.Replace("\n", "\r\n") + "\r\n\r\n";
        LinuxNeighbourTable.ParseProcNetArp(text).Count.ShouldBe(4);
        LinuxNeighbourTable.ParseProcNetArp(Fixture.TrimEnd('\n')).Count.ShouldBe(4);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("IP address       HW type     Flags       HW address            Mask     Device\n")]
    public void NoRowsIsAnEmptyList(string text)
    {
        LinuxNeighbourTable.ParseProcNetArp(text).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("192.168.1.1 0x1 0x2 aa:bb:cc:dd:ee:ff *", "no device column")]
    [InlineData("192.168.1.1", "an address alone")]
    [InlineData("192.168.1 0x1 0x2 aa:bb:cc:dd:ee:ff * eth0", "three octets, which IPAddress.Parse reads as 192.168.0.1")]
    [InlineData("1 0x1 0x2 aa:bb:cc:dd:ee:ff * eth0", "one number, which IPAddress.Parse reads as 0.0.0.1")]
    [InlineData("256.1.1.1 0x1 0x2 aa:bb:cc:dd:ee:ff * eth0", "an octet out of range")]
    [InlineData("fe80::1 0x1 0x2 aa:bb:cc:dd:ee:ff * eth0", "an IPv6 address is not in this table")]
    [InlineData("router.lan 0x1 0x2 aa:bb:cc:dd:ee:ff * eth0", "a name")]
    [InlineData("192.168.1.1 0xZZ 0x2 aa:bb:cc:dd:ee:ff * eth0", "a hardware type that is not hex")]
    [InlineData("192.168.1.1 0x1 0xZZ aa:bb:cc:dd:ee:ff * eth0", "flags that are not hex")]
    [InlineData("192.168.1.1 0x1 0x2 aa:bb:cc:dd:ee * eth0", "five octets")]
    [InlineData("192.168.1.1 0x1 0x2 aa:bb:cc:dd:ee:ff:00 * eth0", "seven octets")]
    [InlineData("192.168.1.1 0x1 0x2 aa-bb-cc-dd-ee-ff * eth0", "hyphens, which the kernel never prints")]
    [InlineData("192.168.1.1 0x1 0x2 aabbccddeeff * eth0", "no separators")]
    [InlineData("192.168.1.1 0x1 0x2 aa:bb:cc:dd:ee:zz * eth0", "an octet that is not hex")]
    [InlineData("192.168.1.1 0x1 0x2 a:bb:cc:dd:ee:ff * eth0", "a one-digit octet")]
    [InlineData("192.168.1.1 0x1 0x2 aaa:bb:cc:dd:ee:ff * eth0", "a three-digit octet")]
    [InlineData("192.168.1.1 0x1 0x2 +a:bb:cc:dd:ee:ff * eth0", "a signed octet")]
    [InlineData("this is not a neighbour table at all", "prose")]
    public void AMalformedLineIsSkippedAndNeverThrows(string line, string why)
    {
        LinuxNeighbourTable.ParseProcNetArp(line).ShouldBeEmpty(why);
    }

    [Fact]
    public void AMalformedLineBetweenGoodOnesCostsOnlyItself()
    {
        var text =
            "192.168.1.1      0x1         0x2         aa:bb:cc:dd:ee:ff     *        eth0\n" +
            "192.168.1.2      0x1         0x2         not-a-mac             *        eth0\n" +
            "garbage\n" +
            "192.168.1.3      0x1         0x2         aa:bb:cc:dd:ee:03     *        eth0\n";

        LinuxNeighbourTable.ParseProcNetArp(text).Select(e => e.Address.ToString()).ShouldBe(["192.168.1.1", "192.168.1.3"]);
    }

    [Fact]
    public void ATabSeparatedRowIsARowToo()
    {
        LinuxNeighbourTable.ParseProcNetArp("192.168.1.1\t0x1\t0x2\taa:bb:cc:dd:ee:ff\t*\teth0").Count.ShouldBe(1);
    }

    [Fact]
    public void ANullTextIsAnArgumentError()
    {
        Should.Throw<ArgumentNullException>(() => LinuxNeighbourTable.ParseProcNetArp(null!));
    }

    // The reader, on a copy of the fixture in the machine's temp folder: the file it is given, never /proc/net/arp.

    private static async Task<string> WriteAsync(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lan-lib-neighbour-{Guid.NewGuid():N}.arp");
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        return path;
    }

    [Fact]
    public async Task TheReaderFindsTheRowOfAnAddressInTheFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await WriteAsync(Fixture);
        try
        {
            var table = new LinuxNeighbourTable(path);

            (await table.FindAsync(IPAddress.Parse("192.168.4.1"), ct))!.PhysicalAddress.ShouldBe(DongleMac);
            (await table.FindAsync(IPAddress.Parse("192.168.1.9"), ct))!.State.ShouldBe(LanNeighbourState.Incomplete, "an incomplete row is returned as it is, for the lookup to rule on");
            (await table.FindAsync(IPAddress.Parse("192.168.4.77"), ct)).ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TheReaderReadsTheFileAfreshOnEveryCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await WriteAsync("");
        try
        {
            var table = new LinuxNeighbourTable(path);
            (await table.FindAsync(IPAddress.Parse("192.168.4.1"), ct)).ShouldBeNull();

            await File.WriteAllTextAsync(path, Fixture, ct);
            (await table.FindAsync(IPAddress.Parse("192.168.4.1"), ct)).ShouldNotBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnIPv4AddressSeenThroughAnIPv6SocketIsFoundAsTheIPv4Address()
    {
        var path = await WriteAsync(Fixture);
        try
        {
            var mapped = IPAddress.Parse("192.168.4.1").MapToIPv6();

            (await new LinuxNeighbourTable(path).FindAsync(mapped, TestContext.Current.CancellationToken))!.PhysicalAddress.ShouldBe(DongleMac);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnIPv6AddressIsNotFoundSinceThisTableIsIPv4Only()
    {
        var path = await WriteAsync(Fixture + "fe80::1 0x1 0x2 aa:bb:cc:dd:ee:ff * wlan0\n");
        try
        {
            (await new LinuxNeighbourTable(path).FindAsync(IPAddress.Parse("fe80::1"), TestContext.Current.CancellationToken)).ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AFileThatIsNotThereOrCannotBeReadIsAnEmptyTableNotAnException()
    {
        var ct = TestContext.Current.CancellationToken;
        var missing = Path.Combine(Path.GetTempPath(), $"lan-lib-neighbour-{Guid.NewGuid():N}-missing", "arp");

        (await new LinuxNeighbourTable(missing).FindAsync(IPAddress.Parse("192.168.4.1"), ct)).ShouldBeNull("a container without /proc/net");
        (await new LinuxNeighbourTable(Path.GetTempPath()).FindAsync(IPAddress.Parse("192.168.4.1"), ct)).ShouldBeNull("a directory is not a table");
    }

    [Fact]
    public void ThePathMustBeGivenAndTheDefaultIsTheKernelsFile()
    {
        Should.Throw<ArgumentException>(() => new LinuxNeighbourTable(""));
        Should.Throw<ArgumentNullException>(() => new LinuxNeighbourTable(null!));
        LinuxNeighbourTable.DefaultPath.ShouldBe("/proc/net/arp");
    }

    [Fact]
    public async Task ANullAddressIsAnArgumentError()
    {
        await Should.ThrowAsync<ArgumentNullException>(new LinuxNeighbourTable("/not/read").FindAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AddressHeldOnTwoAdaptersAnswersTheOneThatNamesAHostBeforeOneThatDoesNot()
    {
        var address = IPAddress.Parse("192.168.4.1");
        var rows = LinuxNeighbourTable.ParseProcNetArp(
            "192.168.4.1      0x1         0x0         00:00:00:00:00:00     *        eth0\n" +
            "192.168.4.1      0x1         0x2         7c:df:a1:00:11:22     *        wlan0\n" +
            "192.168.4.1      0x1         0x2         7c:df:a1:00:11:99     *        wlan1\n");

        var picked = LinuxNeighbourTable.Pick(rows, address);

        picked.ShouldNotBeNull();
        picked.Interface.ShouldBe("wlan0");
        picked.PhysicalAddress.ShouldBe(DongleMac);
    }

    [Fact]
    public void AnAddressHeldOnlyIncompletelyIsAnsweredWithThatRowAndAnAbsentOneWithNone()
    {
        var rows = LinuxNeighbourTable.ParseProcNetArp(Fixture);

        LinuxNeighbourTable.Pick(rows, IPAddress.Parse("192.168.1.9"))!.State.ShouldBe(LanNeighbourState.Incomplete);
        LinuxNeighbourTable.Pick(rows, IPAddress.Parse("10.9.9.9")).ShouldBeNull();
    }

    [Fact]
    public async Task ARowOfTheFileGoesThroughTheLookupLikeAnyOther()
    {
        var path = await WriteAsync(Fixture);
        try
        {
            var links = new NetworkInterfaceLinks(() => [(IPAddress.Parse("192.168.4.2"), 24), (IPAddress.Parse("192.168.1.50"), 24)]);
            var lookup = new LanNeighbours(new LinuxNeighbourTable(path), links, new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
            var ct = TestContext.Current.CancellationToken;

            (await lookup.PhysicalAddressOfAsync(IPAddress.Parse("192.168.4.1"), ct)).ShouldBe(DongleMac);
            (await lookup.PhysicalAddressOfAsync(IPAddress.Parse("192.168.1.9"), ct)).ShouldBeNull("incomplete");
            (await lookup.PhysicalAddressOfAsync(IPAddress.Parse("172.17.0.2"), ct)).ShouldBeNull("the file has it, but this machine is on no link with it");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
