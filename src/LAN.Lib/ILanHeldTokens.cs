using System.Collections.Generic;

namespace LAN.Lib;

/// <summary>
/// The client half of a grant: the bearer tokens this user holds for nodes on the LAN, by endpoint
/// (<c>host:port</c>, compared without regard to case). The mirror of <see cref="ILanGrantStore"/>, which keeps what a
/// node GAVE (hashes); this keeps what a client WAS GIVEN (the tokens themselves, so the store must be private to the user).
/// </summary>
/// <remarks>Synchronous on purpose: the store is a handful of short lines and a user interface reads it while it draws.</remarks>
public interface ILanHeldTokens
{
    /// <summary>The token held for <paramref name="endpoint"/>, or null when none is.</summary>
    string? TokenFor(string endpoint);

    /// <summary>Every endpoint a token is held for, lower-cased and in ordinal order.</summary>
    IReadOnlyList<string> Endpoints { get; }

    /// <summary>Keeps <paramref name="token"/> for <paramref name="endpoint"/>, over any earlier one.</summary>
    void Save(string endpoint, string token);

    /// <summary>Drops the token held for <paramref name="endpoint"/>; false when none was held.</summary>
    bool Forget(string endpoint);
}
