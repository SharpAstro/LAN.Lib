using System;
using System.IO;

namespace LAN.Lib;

/// <summary>
/// The local node's LAN identity: a <b>per-process</b> peer id plus an optional <b>stable</b> node id.
///
/// <para><see cref="PeerId"/> is minted fresh on every <see cref="Create"/> and never persisted. Its
/// only job is the discovery self-echo filter (<see cref="LanDiscovery"/>), which needs the id unique
/// <i>per running process</i>, not stable across sessions. Persisting it was exactly what made two
/// instances on one machine (sharing one file) load the same id and then silently ignore each other as
/// their own echo.</para>
///
/// <para><see cref="NodeId"/> is the opposite: a stable identity minted <b>once</b> and persisted, so a
/// consumer that binds to a node (a saved "remote rig") can recognise it across restarts even as its
/// address changes. It is empty when no persistence path is given. A version-7 GUID is used for both:
/// unique and time-ordered, so peers naturally sort by first-seen time.</para>
/// </summary>
public sealed record LanIdentity(string PeerId, string NodeId)
{
    /// <summary>Create an identity: a fresh per-process <see cref="PeerId"/>, plus a stable
    /// <see cref="NodeId"/> loaded from (or minted once into) <paramref name="stableNodeIdPath"/>. When
    /// the path is null the node id is empty (a pure consumer/monitor needs no stable identity).</summary>
    public static LanIdentity Create(string? stableNodeIdPath)
    {
        var peerId = NewId();
        var nodeId = string.IsNullOrEmpty(stableNodeIdPath) ? "" : LoadOrMintNodeId(stableNodeIdPath);
        return new LanIdentity(peerId, nodeId);
    }

    private static string LoadOrMintNodeId(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(existing))
                    return existing;
            }

            var id = NewId();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, id);
            return id;
        }
        catch
        {
            // Can't read/persist (permissions, read-only FS) — fall back to a fresh, unpersisted id so
            // discovery still works this session; it just won't be stable across restarts.
            return NewId();
        }
    }

    private static string NewId() => Guid.CreateVersion7().ToString("N");
}
