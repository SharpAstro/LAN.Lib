using System;
using System.IO;
using LAN.Lib;
using Shouldly;
using Xunit;

namespace LAN.Lib.Tests;

public sealed class LanHeldTokenFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lht-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void AMissingFileHoldsNothing()
    {
        var store = new LanHeldTokenFile(Path.Combine(_folder, "client-grants.json"));

        store.Endpoints.ShouldBeEmpty();
        store.TokenFor("a.local:1").ShouldBeNull();
        store.Forget("a.local:1").ShouldBeFalse();
    }

    [Fact]
    public void ATokenSavedIsTheTokenHeldAcrossInstances_ByLowerCasedEndpoint()
    {
        var path = Path.Combine(_folder, "nested", "client-grants.json");
        new LanHeldTokenFile(path).Save("SE2.Local:47731", "tok-b");
        new LanHeldTokenFile(path).Save("le1.local:47731", "tok-a");

        var reloaded = new LanHeldTokenFile(path);
        reloaded.Endpoints.ShouldBe(["le1.local:47731", "se2.local:47731"]);
        reloaded.TokenFor("se2.LOCAL:47731").ShouldBe("tok-b");
        File.Exists(path + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public void ASaveOverAnEarlierTokenReplacesIt()
    {
        var store = new LanHeldTokenFile(Path.Combine(_folder, "g.json"));
        store.Save("a.local:1", "old");
        store.Save("a.local:1", "new");

        store.Endpoints.ShouldHaveSingleItem();
        store.TokenFor("a.local:1").ShouldBe("new");
    }

    [Fact]
    public void ForgetDropsOnlyThatNode()
    {
        var store = new LanHeldTokenFile(Path.Combine(_folder, "g.json"));
        store.Save("a.local:1", "ta");
        store.Save("b.local:1", "tb");

        store.Forget("A.local:1").ShouldBeTrue();

        store.TokenFor("a.local:1").ShouldBeNull();
        store.TokenFor("b.local:1").ShouldBe("tb");
        store.Forget("a.local:1").ShouldBeFalse();
    }

    [Fact]
    public void ReadsTheFilePilotAlreadyWrote()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "client-grants.json");
        File.WriteAllText(path, "{\n  \"le1.local:47731\": \"tok-a\"\n}");

        new LanHeldTokenFile(path).TokenFor("le1.local:47731").ShouldBe("tok-a");
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[\"a\"]")]
    [InlineData("")]
    public void ABrokenFileGrantsNothing_AndTheNextSaveWritesAGoodOne(string content)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "client-grants.json");
        File.WriteAllText(path, content);
        var store = new LanHeldTokenFile(path);

        store.Endpoints.ShouldBeEmpty();
        store.TokenFor("a.local:1").ShouldBeNull();

        store.Save("a.local:1", "t");
        new LanHeldTokenFile(path).TokenFor("a.local:1").ShouldBe("t");
    }

    [Fact]
    public void NonStringValuesAreSkipped()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "client-grants.json");
        File.WriteAllText(path, "{ \"a.local:1\": 5, \"b.local:1\": \"tb\", \"c.local:1\": \"\" }");

        new LanHeldTokenFile(path).Endpoints.ShouldBe(["b.local:1"]);
    }
}
