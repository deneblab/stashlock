using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Deneblab.StashLock.Client.Common;

public static class Helpers
{
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

    public static string Base64UrlEncode(string input)
    {
        var inputBytes = Encoding.UTF8.GetBytes(input);
        return Convert.ToBase64String(inputBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .Replace("=", "");
    }

    public static void CreateDirIfNotExist(string path)
    {
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path!);
    }
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

