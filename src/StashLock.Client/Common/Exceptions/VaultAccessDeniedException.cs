using System;
using System.Net;

namespace Deneblab.StashLock.Client.Common.Exceptions;

/// <summary>
/// Exception thrown when the server rejects the API key (401 Unauthorized or 403 Forbidden).
/// Never answered from the local cache: a revoked or wrong key must fail at once.
/// </summary>
public class VaultAccessDeniedException : StashLockException
{
    /// <summary>The requested entry as <c>box.tag.version</c>.</summary>
    public string VaultKey { get; }
    /// <summary><see cref="HttpStatusCode.Unauthorized"/> (key wrong, revoked or expired) or <see cref="HttpStatusCode.Forbidden"/> (key not valid for this box.tag).</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Creates the exception.</summary>
    /// <param name="vaultKey">The requested entry.</param>
    /// <param name="statusCode">401 or 403.</param>
    /// <param name="message">What went wrong and what to check.</param>
    public VaultAccessDeniedException(string vaultKey, HttpStatusCode statusCode, string message)
        : base(message)
    {
        VaultKey = vaultKey;
        StatusCode = statusCode;
    }
}
