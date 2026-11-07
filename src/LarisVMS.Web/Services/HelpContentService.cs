using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using LarisVMS.Core.Localization;
using Markdig;

namespace LarisVMS.Web.Services;

/// <summary>One Help page: the slug is its URL (/Help/{slug}), taken from the file name minus its
/// ordering prefix ("03-cameras.md" → "cameras"); the title is the file's first "# " heading and the
/// summary its first paragraph.</summary>
public sealed record HelpTopic(string Slug, string Title, string Summary, string Html);

/// <summary>Serves the Help section's pages from the Markdown files embedded under Help/ (see
/// LarisVMS.Web.csproj), in English. A translation replaces them file by file: a file with the same
/// name under locales\{culture}\help\ in either language folder (see <see cref="LocaleCatalog"/>)
/// is used for that language instead, so a partly translated Help keeps its untranslated pages in
/// English and every page keeps its URL. Rendered once per language, again when a language file changes.</summary>
public sealed class HelpContentService
{
    private readonly LocaleCatalog? _catalog;
    private readonly Lazy<IReadOnlyList<(string File, string Markdown)>> _english = new(LoadEmbedded);
    private readonly ConcurrentDictionary<string, IReadOnlyList<HelpTopic>> _byCulture = new(StringComparer.OrdinalIgnoreCase);

    public HelpContentService() { }

    public HelpContentService(LocaleCatalog catalog)
    {
        _catalog = catalog;
        catalog.Changed += _byCulture.Clear;
    }

    /// <summary>The Help pages in the request's UI language.</summary>
    public IReadOnlyList<HelpTopic> Topics => TopicsFor(CultureInfo.CurrentUICulture.Name);

    public HelpTopic? Find(string slug) =>
        Topics.FirstOrDefault(t => string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<HelpTopic> TopicsFor(string culture)
    {
        var matched = _catalog?.Match(culture) ?? LocaleCatalog.English;
        return _byCulture.GetOrAdd(matched, Load);
    }

    private IReadOnlyList<HelpTopic> Load(string culture)
    {
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var translated = TranslatedFiles(culture);
        var topics = new List<HelpTopic>();

        foreach (var (file, english) in _english.Value)
        {
            var markdown = translated.TryGetValue(file, out var text) ? text : english;
            var dash = file.IndexOf('-');
            var slug = dash > 0 && file[..dash].All(char.IsDigit) ? file[(dash + 1)..] : file;

            var lines = markdown.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            var title = lines.FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim() ?? slug;
            var summary = lines.FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith('>')) ?? "";

            // The app's <base href="/"> would resolve a bare "#heading" link to the Dashboard, so
            // in-page anchors are pinned to this topic's own URL.
            var html = Markdown.ToHtml(markdown, pipeline).Replace("href=\"#", $"href=\"/Help/{slug}#");
            topics.Add(new HelpTopic(slug, title, summary, html));
        }
        return topics;
    }

    /// <summary>File name (no extension) → Markdown for a language: the shipped folder first, the
    /// drop-in folder over it; the parent language ("fr" for "fr-CA") under both.</summary>
    private Dictionary<string, string> TranslatedFiles(string culture)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_catalog is null || string.Equals(culture, LocaleCatalog.English, StringComparison.OrdinalIgnoreCase)) return files;

        var chain = new List<string>();
        for (var name = culture; name.Length > 0;)
        {
            chain.Insert(0, name);
            var dash = name.LastIndexOf('-');
            if (dash < 0) break;
            name = name[..dash];
        }
        foreach (var root in new[] { _catalog.ShippedFolder, _catalog.DropInFolder })
        {
            if (root is null) continue;
            foreach (var name in chain)
            {
                var dir = Path.Combine(root, name, "help");
                if (!Directory.Exists(dir)) continue;
                foreach (var path in Directory.EnumerateFiles(dir, "*.md"))
                {
                    try { files[Path.GetFileNameWithoutExtension(path)] = File.ReadAllText(path); }
                    catch (IOException) { /* being written; the watcher reloads once it's done */ }
                }
            }
        }
        return files;
    }

    private static IReadOnlyList<(string File, string Markdown)> LoadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var pages = new List<(string, string)>();
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith("Help/", StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            pages.Add((Path.GetFileNameWithoutExtension(name["Help/".Length..]), reader.ReadToEnd()));
        }
        return pages;
    }
}
