using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LAN.Lib;

/// <summary>
/// An <see cref="ILanGrantStore"/> in one JSON file: an array of <c>{ id, label, grantedAt, tokenHash }</c>, camelCase,
/// the shape pilot's and tianwen's nodes already wrote theirs in, so either reads its existing file. Hashes only, so a
/// copy of the file grants nothing.
/// </summary>
/// <remarks>
/// A save writes the whole array to a sibling temporary file and moves it over the old one, so a reader sees the old
/// grants or the new ones, never half of either. A missing file is no grants. Reflection-free, so AOT and trim safe.
/// </remarks>
public sealed class LanGrantFile(string path) : ILanGrantStore
{
    /// <summary>The file's path.</summary>
    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public async ValueTask<IReadOnlyList<LanGrant>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Path))
        {
            return [];
        }

        await using var stream = File.OpenRead(Path);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var grants = new List<LanGrant>();
        foreach (var g in doc.RootElement.EnumerateArray())
        {
            grants.Add(new LanGrant(
                g.GetProperty("id").GetString()!,
                g.GetProperty("label").GetString()!,
                g.GetProperty("grantedAt").GetDateTimeOffset(),
                g.GetProperty("tokenHash").GetString()!));
        }
        return grants;
    }

    public async ValueTask SaveAsync(IReadOnlyList<LanGrant> grants, CancellationToken cancellationToken)
    {
        if (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) is { } folder)
        {
            Directory.CreateDirectory(folder);
        }

        var temp = Path + ".tmp";
        await using (var stream = File.Create(temp))
        await using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (var g in grants)
            {
                writer.WriteStartObject();
                writer.WriteString("id", g.Id);
                writer.WriteString("label", g.Label);
                writer.WriteString("grantedAt", g.GrantedAt);
                writer.WriteString("tokenHash", g.TokenHash);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temp, Path, overwrite: true);
    }
}
