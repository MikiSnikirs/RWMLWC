using System;
using System.Text.RegularExpressions;

namespace RWMLWC.Net;

/// <summary>
/// Код подключения к выделенному серверу (T-058): адрес, порт и отпечаток сертификата одной строкой —
/// <c>rwmlwc://адрес:порт/отпечаток</c>. Отпечаток (SHA-256, 64 шестнадцатеричных знака) защищает
/// от подмены сервера: клиент примет только сертификат с этим отпечатком.
/// </summary>
public static class ConnectionCode
{
    public const string Scheme = "rwmlwc://";

    private static readonly Regex Pattern = new(
        @"^\s*(?:rwmlwc://)?(?<host>\[[0-9a-fA-F:]+\]|[^\s:/\[\]]+):(?<port>\d{1,5})/(?<thumb>[0-9a-fA-F]{64})\s*$",
        RegexOptions.CultureInvariant);

    public static string Format(string host, int port, string thumbprintSha256)
    {
        if (host.Contains(':') && !host.StartsWith("[", StringComparison.Ordinal)) host = "[" + host + "]"; // IPv6
        return $"{Scheme}{host}:{port}/{thumbprintSha256.ToUpperInvariant()}";
    }

    public static bool TryParse(string? text, out string host, out int port, out string thumbprintSha256)
    {
        host = "";
        port = 0;
        thumbprintSha256 = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        Match m = Pattern.Match(text);
        if (!m.Success || !int.TryParse(m.Groups["port"].Value, out port) || port < 1 || port > 65535) return false;
        host = m.Groups["host"].Value.Trim('[', ']');
        thumbprintSha256 = m.Groups["thumb"].Value.ToUpperInvariant();
        return true;
    }
}
