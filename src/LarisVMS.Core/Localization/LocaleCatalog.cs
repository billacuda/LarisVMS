using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LarisVMS.Core.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Core.Localization;

public enum LocaleSource { BuiltIn, Shipped, DropIn, Pseudo }

/// <summary>One language the server can show, for the picker and the admin Language page.</summary>
public sealed record LocaleInfo(string Culture, string Name, LocaleSource Source, int Coverage, string? Author, string? BaseVersion);

/// <summary>A language file that was found but couldn't be used, with why.</summary>
public sealed record LocaleProblem(string File, string Error);

/// <summary>
/// Every UI language the server knows. English is built in (LarisVMS.Core/Localization/en.json, embedded so a deploy
/// can never ship without it). Other languages are JSON files named for their culture (fr.json,
/// pt-BR.json) in two folders, the drop-in folder winning key by key over the shipped one:
/// <list type="number">
/// <item>%ProgramData%\LarisVMS\locales — translators' and admins' files; survives upgrades.</item>
/// <item>&lt;install&gt;\locales — languages that ship with LarisVMS.</item>
/// </list>
/// Both folders are watched, so a dropped-in or uploaded file appears without a restart. A lookup
/// walks fr-CA → fr → English → the key itself. A file that won't parse is skipped and listed in
/// <see cref="Problems"/>; it never stops the server.
/// </summary>
public sealed class LocaleCatalog : IDisposable
{
    public const string English = "en";

    /// <summary>The pseudo-locale: English with accents and padding, generated rather than shipped,
    /// for spotting text that isn't translatable yet and layouts that break with longer text.</summary>
    public const string Pseudo = "qps-Ploc";

    private readonly string? _dropInFolder;
    private readonly string? _shippedFolder;
    private readonly IReadOnlyDictionary<string, string> _english;
    private readonly ILogger _logger;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _reloadTimer;
    private volatile Snapshot _snapshot;

    private sealed record Snapshot(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Languages,
        IReadOnlyList<LocaleInfo> Locales,
        IReadOnlyList<LocaleProblem> Problems,
        string Version);

    public LocaleCatalog(IHostEnvironment env, ILogger<LocaleCatalog> logger)
        : this(Path.Combine(LocalDataAcl.Root, "locales"), Path.Combine(env.ContentRootPath, "locales"),
               LoadEmbeddedEnglish(), logger, watch: true)
    {
    }

    public LocaleCatalog(string? dropInFolder, string? shippedFolder, IReadOnlyDictionary<string, string> english,
        ILogger logger, bool watch)
    {
        _dropInFolder = dropInFolder;
        _shippedFolder = shippedFolder;
        _english = english;
        _logger = logger;
        _reloadTimer = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        _snapshot = Build();
        if (watch) StartWatching();
    }

    /// <summary>Where uploads and drop-ins go.</summary>
    public string? DropInFolder => _dropInFolder;

    /// <summary>Where the languages that ship with LarisVMS are.</summary>
    public string? ShippedFolder => _shippedFolder;

    /// <summary>The built-in English text, key → text.</summary>
    public IReadOnlyDictionary<string, string> EnglishEntries => _english;

    /// <summary>Every usable language, English first, then by name. Includes the pseudo-locale; the
    /// picker hides it unless an admin turned it on.</summary>
    public IReadOnlyList<LocaleInfo> Locales => _snapshot.Locales;

    public IReadOnlyList<LocaleProblem> Problems => _snapshot.Problems;

    /// <summary>Changes whenever any language file does; part of the browser's cache key for the
    /// script-side text (/locales/{culture}.js).</summary>
    public string Version => _snapshot.Version;

    /// <summary>Raised after a reload.</summary>
    public event Action? Changed;

    /// <summary>The best available language for a requested culture name ("fr-CA" → "fr" when only
    /// French is installed), or null when neither it nor its parent is available.</summary>
    public string? Match(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;
        var languages = _snapshot.Languages;
        var name = requested.Trim();
        while (name.Length > 0)
        {
            var found = languages.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (found is not null) return found;
            var dash = name.LastIndexOf('-');
            if (dash < 0) break;
            name = name[..dash];
        }
        return null;
    }

    /// <summary>The text for <paramref name="key"/> in <paramref name="culture"/>, falling back
    /// through its parent language to English. Null when even English doesn't have it.</summary>
    public string? Find(string culture, string key)
    {
        var languages = _snapshot.Languages;
        var name = culture;
        while (name.Length > 0)
        {
            if (languages.TryGetValue(name, out var entries) && entries.TryGetValue(key, out var text)) return text;
            var dash = name.LastIndexOf('-');
            if (dash < 0) break;
            name = name[..dash];
        }
        return _english.GetValueOrDefault(key);
    }

    /// <summary>The plural form of <paramref name="baseKey"/> for <paramref name="count"/>:
    /// baseKey_{category} in the language, then baseKey_other, then baseKey, each falling back to English.</summary>
    public string? FindPlural(string culture, string baseKey, long count)
    {
        var category = PluralRules.Category(string.Equals(culture, Pseudo, StringComparison.OrdinalIgnoreCase) ? English : culture, count);
        // Look in the language itself before falling back, so French "_one" beats English "_one".
        var languages = _snapshot.Languages;
        var name = culture;
        while (name.Length > 0)
        {
            if (languages.TryGetValue(name, out var entries))
            {
                if (entries.TryGetValue($"{baseKey}_{category}", out var t)) return t;
                if (entries.TryGetValue($"{baseKey}_{PluralRules.Other}", out t)) return t;
                if (entries.TryGetValue(baseKey, out t)) return t;
            }
            var dash = name.LastIndexOf('-');
            if (dash < 0) break;
            name = name[..dash];
        }
        var englishCategory = PluralRules.Category(English, count);
        return _english.GetValueOrDefault($"{baseKey}_{englishCategory}")
               ?? _english.GetValueOrDefault($"{baseKey}_{PluralRules.Other}")
               ?? _english.GetValueOrDefault(baseKey);
    }

    /// <summary>Every key → text for a culture with fallbacks applied, limited to keys starting with
    /// <paramref name="prefix"/> (the script-side text is everything under "js.").</summary>
    public Dictionary<string, string> Resolve(string culture, string prefix)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, text) in _english)
            if (key.StartsWith(prefix, StringComparison.Ordinal)) result[key] = text;

        var chain = new List<string>();
        for (var name = culture; name.Length > 0;)
        {
            chain.Add(name);
            var dash = name.LastIndexOf('-');
            if (dash < 0) break;
            name = name[..dash];
        }
        chain.Reverse();
        foreach (var name in chain)
            if (_snapshot.Languages.TryGetValue(name, out var entries))
                foreach (var (key, text) in entries)
                    if (key.StartsWith(prefix, StringComparison.Ordinal)) result[key] = text;
        return result;
    }

    /// <summary>Checks an uploaded file and returns warnings (placeholders the English text doesn't
    /// have, keys English doesn't know). Throws <see cref="FormatException"/> when it can't be used.</summary>
    public IReadOnlyList<string> Validate(string culture, string json)
    {
        var file = LocaleFile.Parse(culture, json);
        if (string.Equals(file.Culture, English, StringComparison.OrdinalIgnoreCase) || string.Equals(file.Culture, Pseudo, StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"\"{file.Culture}\" is built in and can't be replaced.");
        if (file.Entries.Count == 0)
            throw new FormatException("The file has no text in it.");

        var warnings = new List<string>();
        var unknown = file.Entries.Keys.Where(k => !_english.ContainsKey(k) && !HasPluralSibling(k)).ToList();
        if (unknown.Count > 0)
            warnings.Add($"{unknown.Count} key(s) aren't used by this version and will be ignored, for example \"{unknown[0]}\".");
        foreach (var (key, text) in file.Entries)
        {
            var english = _english.GetValueOrDefault(key) ?? _english.GetValueOrDefault($"{LocaleFile.BaseKey(key)}_other");
            if (english is null) continue;
            var extra = LocaleFile.Placeholders(text).Except(LocaleFile.Placeholders(english).Append("count")).ToList();
            if (extra.Count > 0)
                warnings.Add($"\"{key}\" uses {{{extra[0]}}}, which the English text doesn't have; it will show as written.");
        }
        return warnings;
    }

    public void Reload()
    {
        try
        {
            _snapshot = Build();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reloading language files failed; keeping the languages already loaded");
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _reloadTimer.Dispose();
    }

    private bool HasPluralSibling(string key)
    {
        var baseKey = LocaleFile.BaseKey(key);
        return baseKey != key && (_english.ContainsKey($"{baseKey}_other") || _english.ContainsKey(baseKey));
    }

    private Snapshot Build()
    {
        var problems = new List<LocaleProblem>();
        var shipped = ReadFolder(_shippedFolder, problems);
        var dropIn = ReadFolder(_dropInFolder, problems);

        var languages = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            [English] = _english,
            [Pseudo] = PseudoLocale.From(_english)
        };
        var englishBaseKeys = _english.Keys.Select(LocaleFile.BaseKey).ToHashSet(StringComparer.Ordinal);
        var infos = new List<LocaleInfo>
        {
            new(English, "English", LocaleSource.BuiltIn, 100, null, null)
        };

        foreach (var culture in shipped.Keys.Union(dropIn.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(culture, English, StringComparison.OrdinalIgnoreCase) || string.Equals(culture, Pseudo, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(new LocaleProblem($"{culture}.json", "English and the pseudo-locale are built in; this file is ignored."));
                continue;
            }
            shipped.TryGetValue(culture, out var s);
            dropIn.TryGetValue(culture, out var d);
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            if (s is not null) foreach (var (k, v) in s.Entries) merged[k] = v;
            if (d is not null) foreach (var (k, v) in d.Entries) merged[k] = v;

            var translatedBaseKeys = merged.Keys.Select(LocaleFile.BaseKey).Where(englishBaseKeys.Contains).Distinct().Count();
            var coverage = englishBaseKeys.Count == 0 ? 0 : (int)Math.Floor(100.0 * translatedBaseKeys / englishBaseKeys.Count);
            var top = d ?? s!;
            var name = top.Name ?? NativeName(top.Culture);
            languages[top.Culture] = merged;
            infos.Add(new LocaleInfo(top.Culture, name, d is not null ? LocaleSource.DropIn : LocaleSource.Shipped,
                coverage, top.Author, top.BaseVersion));
        }

        var ordered = infos.Take(1).Concat(infos.Skip(1).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)).ToList();
        ordered.Add(new LocaleInfo(Pseudo, "Pseudo-locale (testing)", LocaleSource.Pseudo, 100, null, null));

        foreach (var p in problems) _logger.LogWarning("Language file {File} skipped: {Error}", p.File, p.Error);
        return new Snapshot(languages, ordered, problems, ComputeVersion(languages));
    }

    private static Dictionary<string, LocaleFile> ReadFolder(string? folder, List<LocaleProblem> problems)
    {
        var files = new Dictionary<string, LocaleFile>(StringComparer.OrdinalIgnoreCase);
        if (folder is null || !Directory.Exists(folder)) return files;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            var fileName = Path.GetFileName(path);
            try
            {
                if (new FileInfo(path).Length > LocaleFile.MaxBytes)
                {
                    problems.Add(new LocaleProblem(fileName, $"Larger than {LocaleFile.MaxBytes / (1024 * 1024)} MB."));
                    continue;
                }
                var file = LocaleFile.Parse(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path, Encoding.UTF8));
                files[file.Culture] = file;
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                problems.Add(new LocaleProblem(fileName, ex.Message));
            }
        }
        return files;
    }

    private void StartWatching()
    {
        foreach (var folder in new[] { _dropInFolder, _shippedFolder })
        {
            if (folder is null) continue;
            try
            {
                Directory.CreateDirectory(folder);
                // Subfolders too: translated Help pages live in {culture}\help\.
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                // Editors and copies fire several events per save; reload once things settle.
                void Schedule(string? path)
                {
                    var ext = Path.GetExtension(path ?? "");
                    if (ext.Length == 0 || ext.Equals(".json", StringComparison.OrdinalIgnoreCase) || ext.Equals(".md", StringComparison.OrdinalIgnoreCase))
                        _reloadTimer.Change(TimeSpan.FromMilliseconds(750), Timeout.InfiniteTimeSpan);
                }
                watcher.Changed += (_, e) => Schedule(e.FullPath);
                watcher.Created += (_, e) => Schedule(e.FullPath);
                watcher.Deleted += (_, e) => Schedule(e.FullPath);
                watcher.Renamed += (_, e) => Schedule(e.FullPath);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogWarning(ex, "Can't watch {Folder} for language files", folder);
            }
        }
    }

    private static string NativeName(string culture)
    {
        try
        {
            var native = CultureInfo.GetCultureInfo(culture).NativeName;
            return native.Length > 0 ? char.ToUpper(native[0], CultureInfo.GetCultureInfo(culture)) + native[1..] : culture;
        }
        catch (CultureNotFoundException)
        {
            return culture;
        }
    }

    private static string ComputeVersion(Dictionary<string, IReadOnlyDictionary<string, string>> languages)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (culture, entries) in languages.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(culture));
            foreach (var (k, v) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                sha.AppendData(Encoding.UTF8.GetBytes(k));
                sha.AppendData(Encoding.UTF8.GetBytes(v));
            }
        }
        return Convert.ToHexString(sha.GetHashAndReset())[..12].ToLowerInvariant();
    }

    public static IReadOnlyDictionary<string, string> LoadEmbeddedEnglish()
    {
        using var stream = typeof(LocaleCatalog).Assembly.GetManifestResourceStream("Locales/en.json")
                           ?? throw new InvalidOperationException("The built-in English text (LarisVMS.Core/Localization/en.json) is missing from the build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return LocaleFile.Parse(English, reader.ReadToEnd()).Entries;
    }
}
