using System.Collections.Generic;

namespace Deneblab.StashLock.Client.Common;

public class SecretsModel
{
    public Dictionary<string, object> Values { get; set; } = new();

}