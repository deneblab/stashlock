using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Deneblab.StashLock.Client.Common;

/// <summary>Small string and file helpers.</summary>
public static class Helpers
{
    /// <summary>Decodes URL-safe base64 (no padding) to UTF-8 text.</summary>
    /// <param name="text">base64url text.</param>
    public static string Base64UrlDecode(string text)
    {
        text = text.Replace("-", "+").Replace("_", "/");
        switch (text.Length % 4)
        {
            case 2:
                text += "==";
                break;
            case 3:
                text += "=";
                break;
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(text));
    }

    /// <summary>Encodes UTF-8 text as URL-safe base64 without padding (the key format in <c>/boxes/{key}</c>).</summary>
    /// <param name="input">Text such as <c>box.tag.version</c>.</param>
    public static string Base64UrlEncode(string input)
    {
        var inputBytes = Encoding.UTF8.GetBytes(input);
        return Convert.ToBase64String(inputBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .Replace("=", "");
    }

    /// <summary>Creates the directory if it does not exist.</summary>
    /// <param name="path">Directory path.</param>
    public static void CreateDirIfNotExist(string path)
    {
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path!);
    }
    /// <summary>Parses a query string (<c>?a=1&amp;b=2</c>) into unescaped key/value pairs.</summary>
    /// <param name="query">The query string, with or without the leading <c>?</c>.</param>
    public static Dictionary<string, string> ParseQs(string query)
    {
        var ret = new Dictionary<string, string>();

        var arr = query.Split("&");

        foreach (var kv in arr)
        {
            var arr2 = kv.Split("=");
            var k1 = arr2[0].StartsWith("?") ? arr2[0].Substring(1) : arr2[0];
            var v1 = arr2[1];
            ret[Uri.UnescapeDataString(k1)] = Uri.UnescapeDataString(v1);
        }

        return ret;
    }

   
}

