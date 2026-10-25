using System;
using System.Net;

namespace Deneblab.StashLock.Client.Common.Exceptions;

/// <summary>
/// Exception thrown when the server rejects the API key (401 Unauthorized or 403 Forbidden).
/// Never answered from the local cache: a revoked or wrong key must fail at once.
/// </summary>
public class VaultAccessDeniedException : StashLockException
{
    public string VaultKey { get; }
    public HttpStatusCode StatusCode { get; }

    public VaultAccessDeniedException(string vaultKey, HttpStatusCode statusCode, string message)
        : base(message)
    {
        VaultKey = vaultKey;
        StatusCode = statusCode;
    }
}
