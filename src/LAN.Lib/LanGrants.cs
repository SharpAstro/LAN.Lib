using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LAN.Lib;

/// <summary>
/// A capability one peer was given by another, remembered until revoked: who was given it (<see cref="Label"/>, for a
/// person to read in a list of grants) and the hash of the token that carries it. The token itself is never kept.
/// </summary>
public sealed record LanGrant(string Id, string Label, DateTimeOffset GrantedAt, string TokenHash);

/// <summary>A grant as it is issued: the grant, and its <see cref="Token"/>, which exists only here and in the hands of
/// the peer it is given to.</summary>
public readonly record struct LanGrantIssued(LanGrant Grant, string Token);

/// <summary>Where <see cref="LanGrants"/> keeps its grants: the application's own storage, written whole.</summary>
public interface ILanGrantStore
{
    /// <summary>The grants saved last, or none.</summary>
    ValueTask<IReadOnlyList<LanGrant>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the saved grants with <paramref name="grants"/>.</summary>
    ValueTask SaveAsync(IReadOnlyList<LanGrant> grants, CancellationToken cancellationToken);
}

/// <summary>
/// The grants a node has given, and the check a request's token is put to: what an accepted invite
/// (<see cref="LanInvites{T}"/>) turns into when the acceptance is meant to last.
/// <para><b>A token is a bearer capability and is kept only as its hash.</b> <see cref="GrantAsync"/> mints 256 random
/// bits and hands them out once; the store holds their SHA-256, so a copy of the store grants nothing. Over plain HTTP a
/// token can be read off the wire, which is what TLS is for; this closes "anyone who can reach the port", not a hostile
/// network.</para>
/// <para><b>Reads are lock-free, writes are one at a time.</b> <see cref="TryVerify"/> runs on every request and reads an
/// immutable array; a grant and a revoke replace the array and save it under one asynchronous gate, so two racing writes
/// can never save their snapshots in the wrong order and lose one.</para>
/// </summary>
public sealed class LanGrants(ILanGrantStore store, TimeProvider timeProvider) : IDisposable
{
    private const int TokenBytes = 32;

    private readonly SemaphoreSlim _writes = new SemaphoreSlim(1, 1);
    private LanGrant[] _grants = [];

    /// <summary>Every grant given and not revoked.</summary>
    public ImmutableArray<LanGrant> All => ImmutableCollectionsMarshal.AsImmutableArray(Volatile.Read(ref _grants));

    /// <summary>Reads the grants the store holds, replacing any in memory.</summary>
    public async ValueTask LoadAsync(CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _grants, [.. loaded]);
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>Gives <paramref name="label"/> a grant and saves it: the grant, and the token to hand to its holder.</summary>
    public async ValueTask<LanGrantIssued> GrantAsync(string label, CancellationToken cancellationToken)
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        var grant = new LanGrant(Guid.CreateVersion7().ToString("N"), label, timeProvider.GetUtcNow(), HashOf(token));
        await ChangeAsync(grants => grants.Add(grant), cancellationToken).ConfigureAwait(false);
        return new LanGrantIssued(grant, token);
    }

    /// <summary>Revokes the grant <paramref name="id"/>: true when there was one. Its token is refused from then on.</summary>
    public async ValueTask<bool> RevokeAsync(string id, CancellationToken cancellationToken)
    {
        var revoked = false;
        await ChangeAsync(grants =>
        {
            var kept = grants.RemoveAll(grant => grant.Id == id);
            revoked = kept.Length != grants.Length;
            return revoked ? kept : grants;
        }, cancellationToken).ConfigureAwait(false);
        return revoked;
    }

    /// <summary>The grant <paramref name="token"/> carries: true with it, false for no token, a malformed one, or one no
    /// grant holds (never given, or revoked).</summary>
    public bool TryVerify(string? token, [NotNullWhen(true)] out LanGrant? grant)
    {
        grant = null;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }
        var presented = Encoding.ASCII.GetBytes(HashOf(token));
        foreach (var candidate in All)
        {
            if (CryptographicOperations.FixedTimeEquals(presented, Encoding.ASCII.GetBytes(candidate.TokenHash)))
            {
                grant = candidate;
                return true;
            }
        }
        return false;
    }

    /// <inheritdoc/>
    public void Dispose() => _writes.Dispose();

    private async ValueTask ChangeAsync(Func<ImmutableArray<LanGrant>, ImmutableArray<LanGrant>> change, CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = All;
            var after = change(before);
            if (after == before)
            {
                return;
            }
            // Saved first: a grant the store refused must not verify, and a revoke it refused must not look done.
            await store.SaveAsync(after, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _grants, ImmutableCollectionsMarshal.AsArray(after) ?? []);
        }
        finally
        {
            _writes.Release();
        }
    }

    private static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
