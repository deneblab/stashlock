using System.Collections.Generic;

namespace Deneblab.StashLock.Client.Common;

/// <summary>Secrets file wrapped in a <c>Values</c> object (older plain-file format).</summary>
public class SecretsModel
{
    /// <summary>Flattened secrets (colon-separated keys).</summary>
    public Dictionary<string, object> Values { get; set; } = new();

}