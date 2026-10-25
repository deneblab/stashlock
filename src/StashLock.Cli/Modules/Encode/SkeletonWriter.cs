using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Deneblab.StashLock.Cli.Models;
using Deneblab.StashLock.Shared.Formats;

namespace Deneblab.StashLock.Cli.Modules.Encode;

/// <summary>
/// Builds <c>stashlock.skeleton.&lt;FILE&gt;.json</c>: the key names of every
/// <c>stashlock.enc.&lt;tag&gt;.&lt;FILE&gt;.json</c> in the vault, nested, with every value set to "text".
/// Only key names are read (they are not encrypted), so no private key is needed and no value,
/// length or hash of a value ends up in the skeleton.
/// </summary>
internal static class SkeletonWriter
{
    public const string Placeholder = "text";

    public sealed record Result(string Path, JsonObject Skeleton, List<string> Warnings);

    public static string FileNameFor(string inputBaseName) => $"stashlock.skeleton.{inputBaseName}.json";

    /// <summary>Reads the encrypted files of <paramref name="inputBaseName"/> and writes the skeleton next to them.</summary>
    public static Result Write(string vaultDir, string inputBaseName, SkeletonSettings settings)
    {
        var warnings = new List<string>();
        var keysByTag = ReadKeysByTag(vaultDir, inputBaseName, warnings);
        warnings.AddRange(TagDifferences(keysByTag));

        var exclude = settings.Exclude.Where(p => !string.IsNullOrWhiteSpace(p)).Select(ToRegex).ToList();
        var keys = keysByTag.Values.SelectMany(k => k)
            .Where(k => !exclude.Any(r => r.IsMatch(k)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var skeleton = Build(keys, warnings);
        var path = System.IO.Path.Combine(vaultDir, FileNameFor(inputBaseName));
        File.WriteAllText(path, skeleton.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return new Result(path, skeleton, warnings);
    }

    private static SortedDictionary<string, HashSet<string>> ReadKeysByTag(string vaultDir, string inputBaseName, List<string> warnings)
    {
        var result = new SortedDictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var suffix = $".{inputBaseName}.json";
        foreach (var path in Directory.GetFiles(vaultDir, $"stashlock.enc.*{suffix}"))
        {
            var name = System.IO.Path.GetFileName(path);
            var tag = name.Substring("stashlock.enc.".Length, name.Length - "stashlock.enc.".Length - suffix.Length);
            if (tag.Length == 0 || tag.Contains('.'))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("_stashlock", out var meta) || meta.ValueKind != JsonValueKind.Object
                    || !meta.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String || mode.GetString() != SopsCodec.Mode
                    || !root.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object)
                    throw new JsonException($"no 'values' or no '_stashlock' metadata with mode '{SopsCodec.Mode}'");
                result[tag] = new HashSet<string>(values.EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);
            }
            catch (JsonException ex)
            {
                warnings.Add($"{name} skipped: not a StashLock encrypted file ({ex.Message})");
            }
        }
        return result;
    }

    private static IEnumerable<string> TagDifferences(SortedDictionary<string, HashSet<string>> keysByTag)
    {
        if (keysByTag.Count < 2)
            yield break;
        var all = keysByTag.Values.SelectMany(k => k).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal);
        foreach (var key in all)
        {
            var missing = keysByTag.Where(t => !t.Value.Contains(key)).Select(t => t.Key).ToList();
            if (missing.Count == 0)
                continue;
            var present = keysByTag.Where(t => t.Value.Contains(key)).Select(t => t.Key);
            yield return $"{key}: in {string.Join(", ", present)}, missing in {string.Join(", ", missing)}";
        }
    }

    /// <summary>Nests colon-separated keys; objects whose keys are exactly 0..n-1 become arrays.</summary>
    internal static JsonObject Build(IEnumerable<string> keys, List<string> warnings)
    {
        var root = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var parts = key.Split(':');
            var node = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (!node.TryGetValue(parts[i], out var child) || child is not SortedDictionary<string, object> dict)
                {
                    if (child is string)
                        warnings.Add($"{string.Join(':', parts.Take(i + 1))} is a value in one file and a section in another; kept as a section");
                    dict = new SortedDictionary<string, object>(StringComparer.Ordinal);
                    node[parts[i]] = dict;
                }
                node = dict;
            }
            var leaf = parts[^1];
            if (node.TryGetValue(leaf, out var existing) && existing is SortedDictionary<string, object>)
            {
                warnings.Add($"{key} is a value in one file and a section in another; kept as a section");
                continue;
            }
            node[leaf] = Placeholder;
        }
        return (JsonObject)ToNode(root);
    }

    private static JsonNode ToNode(object value)
    {
        if (value is not SortedDictionary<string, object> dict)
            return JsonValue.Create((string)value)!;

        var indexes = dict.Keys.Select(k => int.TryParse(k, out var n) && n.ToString() == k ? n : -1).ToList();
        if (indexes.Count > 0 && indexes.All(n => n >= 0) && indexes.OrderBy(n => n).SequenceEqual(Enumerable.Range(0, indexes.Count)))
        {
            var array = new JsonArray();
            foreach (var item in dict.OrderBy(kv => int.Parse(kv.Key)))
                array.Add(ToNode(item.Value));
            return array;
        }

        var obj = new JsonObject();
        foreach (var (k, v) in dict)
            obj[k] = ToNode(v);
        return obj;
    }

    internal static Regex ToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
