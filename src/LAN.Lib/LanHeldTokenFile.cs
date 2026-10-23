using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LAN.Lib;

/// <summary>
/// An <see cref="ILanHeldTokens"/> in one JSON file: an object of <c>endpoint: token</c>, the shape pilot's
/// <c>client-grants.json</c> already has, so an existing file is read as it stands.
/// </summary>
/// <remarks>
/// Endpoints are lower-cased on the way in and out. A save writes the whole object to a sibling temporary file and moves
/// it over the old one, so a reader sees the old tokens or the new ones, never half of either; off Windows the file is
/// created readable by its user alone, because it holds the tokens themselves. A missing file, or one that is not an
/// object of strings, holds nothing (a token that cannot be read grants nothing; the next save writes a good file).
/// Each call reads the file afresh, so two processes sharing it see each other's saves. Reflection-free, so AOT and trim safe.
/// </remarks>
public sealed class LanHeldTokenFile(string path) : ILanHeldTokens
{
    private readonly object _gate = new();

    /// <summary>The file's path.</summary>
    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    /// <inheritdoc />
    public IReadOnlyList<string> Endpoints
    {
        get
        {
            lock (_gate)
            {
                return [.. Load().Keys.Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <inheritdoc />
    public string? TokenFor(string endpoint)
    {
        lock (_gate)
        {
            return Load().TryGetValue(Key(endpoint), out var token) ? token : null;
        }
    }

    /// <inheritdoc />
    public void Save(string endpoint, string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        lock (_gate)
        {
            var tokens = Load();
            tokens[Key(endpoint)] = token;
            Write(tokens);
        }
    }

    /// <inheritdoc />
    public bool Forget(string endpoint)
    {
        lock (_gate)
        {
            var tokens = Load();
            if (!tokens.Remove(Key(endpoint)))
            {
                return false;
            }
            Write(tokens);
            return true;
        }
    }

    private static string Key(string endpoint) => endpoint.Trim().ToLowerInvariant();

    private Dictionary<string, string> Load()
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(Path))
        {
            return tokens;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path));
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } token)
                {
                    tokens[Key(property.Name)] = token;
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or IOException)
        {
            // A broken file grants nothing; the next save writes a good one.
            tokens.Clear();
        }
        return tokens;
    }

    private void Write(Dictionary<string, string> tokens)
    {
        if (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) is { } folder)
        {
            Directory.CreateDirectory(folder);
        }

        var temp = Path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var stream = new FileStream(temp, options))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in tokens.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                writer.WriteString(key, value);
            }
            writer.WriteEndObject();
        }
        File.Move(temp, Path, overwrite: true);
    }
}
