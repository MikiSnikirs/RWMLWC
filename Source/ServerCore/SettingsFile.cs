using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RWMLWC.Server;

/// <summary>
/// Простой файл настроек (T-065): строки <c>ключ = значение</c>, <c>#</c> — комментарий, регистр ключей не важен.
/// Если файла нет, он создаётся из шаблона с комментариями, чтобы его было удобно править руками.
/// Неверное значение не роняет запуск: берётся значение по умолчанию и пишется предупреждение.
/// </summary>
public sealed class SettingsFile
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _warn;

    public string Path { get; }

    /// <summary>Файл только что создан из шаблона.</summary>
    public bool Created { get; }

    private SettingsFile(string path, bool created, Action<string> warn)
    {
        Path = path;
        Created = created;
        _warn = warn;
    }

    public static SettingsFile LoadOrCreate(string path, string template, Action<string>? warn = null)
    {
        bool created = false;
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            File.WriteAllText(path, template, new UTF8Encoding(false));
            created = true;
        }
        var file = new SettingsFile(path, created, warn ?? (_ => { }));
        file.Parse(File.ReadAllLines(path));
        return file;
    }

    /// <summary>Разбор текста без файла (для тестов).</summary>
    public static SettingsFile FromText(string text, Action<string>? warn = null)
    {
        var file = new SettingsFile("(текст)", false, warn ?? (_ => { }));
        file.Parse(text.Split('\n'));
        return file;
    }

    private void Parse(IEnumerable<string> lines)
    {
        int n = 0;
        foreach (string raw in lines)
        {
            n++;
            string line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                _warn($"{Path}, строка {n}: нет «=» — пропущена");
                continue;
            }
            _values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
    }

    public string? GetString(string key)
    {
        return _values.TryGetValue(key, out string? v) && v.Length > 0 ? v : null;
    }

    public int GetInt(string key, int fallback, int min, int max)
    {
        if (!_values.TryGetValue(key, out string? v) || v.Length == 0) return fallback;
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) && x >= min && x <= max) return x;
        _warn($"{Path}: {key} = «{v}» — нужно целое от {min} до {max}, беру {fallback}");
        return fallback;
    }

    public double GetDouble(string key, double fallback, double min, double max)
    {
        if (!_values.TryGetValue(key, out string? v) || v.Length == 0) return fallback;
        if (double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double x) && x >= min && x <= max) return x;
        _warn($"{Path}: {key} = «{v}» — нужно число от {min} до {max}, беру {fallback}");
        return fallback;
    }
}
