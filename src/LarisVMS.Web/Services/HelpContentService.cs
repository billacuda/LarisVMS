using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using LarisVMS.Core.Localization;
using Markdig;
using Markdig.Syntax;

namespace LarisVMS.Web.Services;

/// <summary>One Help page: the slug is its URL (/Help/{slug}), taken from the file name minus its
/// ordering prefix ("03-cameras.md" → "cameras"); the title is the file's first "# " heading and the
/// summary its first paragraph.</summary>
public sealed record HelpTopic(string Slug, string Title, string Summary, string Html);

/// <summary>Serves the Help section's pages. The English Markdown is embedded from Help/en/ (see
/// LarisVMS.Web.csproj) and decides which pages exist. A translation replaces them file by file, with
/// the same file name, from (first hit wins): the drop-in folder locales\{culture}\help\, the shipped
/// folder locales\{culture}\help\, and the embedded Help/{culture}/ — for the language, then its
/// parent ("es" for "es-AR"). A partly translated Help keeps its untranslated pages in English, and
/// every page keeps its URL. Rendered once per language, again when a language file changes.</summary>
public sealed class HelpContentService
{
    private const string EmbeddedPrefix = "Help/";

    private readonly LocaleCatalog? _catalog;
    private readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> _embedded;
    private readonly ConcurrentDictionary<string, IReadOnlyList<HelpTopic>> _byCulture = new(StringComparer.OrdinalIgnoreCase);

    // Raw HTML in the Markdown is shown as text: translations come from contributors and from a
    // drop-in folder, and a page must not be able to inject markup into the app. For the same reason
    // there are no {attribute} blocks, and links and images only go to web, mail, or in-app addresses
    // (see Render).
    private static readonly MarkdownPipeline Pipeline = BuildPipeline();

    private static MarkdownPipeline BuildPipeline()
    {
        var builder = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml();
        builder.Extensions.RemoveAll(e => e is Markdig.Extensions.GenericAttributes.GenericAttributesExtension);
        return builder.Build();
    }

    /// <summary>Markdown to HTML, with any link or image address that isn't http(s), mailto, or
    /// relative (javascript:, data:, and so on) replaced by "#".</summary>
    internal static string Render(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<Markdig.Syntax.Inlines.LinkInline>())
            if (!IsSafeUrl(link.Url)) link.Url = "#";
        foreach (var link in document.Descendants<Markdig.Syntax.Inlines.AutolinkInline>())
            if (!link.IsEmail && !IsSafeUrl(link.Url)) link.Url = "#";
        using var writer = new StringWriter();
        var renderer = new Markdig.Renderers.HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    internal static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        var trimmed = url.Trim();
        // Relative: in-page, root-relative (not protocol-relative), or a plain path with no scheme.
        if (trimmed.StartsWith('#')) return true;
        if (trimmed.StartsWith('/')) return trimmed.Length == 1 || (trimmed[1] != '/' && trimmed[1] != '\\');
        var colon = trimmed.IndexOf(':');
        var slash = trimmed.IndexOfAny(['/', '?', '#']);
        if (colon < 0 || (slash >= 0 && slash < colon)) return true;
        var scheme = trimmed[..colon];
        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
               || scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase);
    }

    public HelpContentService() : this(null, null) { }

    public HelpContentService(LocaleCatalog catalog) : this(catalog, null) { }

    /// <summary>For tests: <paramref name="embedded"/> replaces the assembly's pages
    /// (culture → file name without extension → Markdown).</summary>
    internal HelpContentService(LocaleCatalog? catalog, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? embedded)
    {
        _catalog = catalog;
        _embedded = new(() => embedded ?? LoadEmbedded());
        if (catalog is not null) catalog.Changed += _byCulture.Clear;
    }

    /// <summary>The Help pages in the request's UI language.</summary>
    public IReadOnlyList<HelpTopic> Topics => TopicsFor(CultureInfo.CurrentUICulture.Name);

    public HelpTopic? Find(string slug) =>
        Topics.FirstOrDefault(t => string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>The cultures with at least one embedded Help translation, English excluded.</summary>
    public IReadOnlyList<string> GetEmbeddedTranslations() =>
        _embedded.Value.Keys.Where(c => !string.Equals(c, LocaleCatalog.English, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>How many of the English pages <paramref name="culture"/> has its own text for.</summary>
    public int TranslatedPageCount(string culture)
    {
        var translated = TranslatedFiles(culture);
        return English.Keys.Count(translated.ContainsKey);
    }

    public int PageCount => English.Count;

    public IReadOnlyList<HelpTopic> TopicsFor(string culture)
    {
        // Without a catalog (tests) the culture is used as asked; with one, only installed languages.
        var matched = _catalog is null ? culture : _catalog.Match(culture) ?? LocaleCatalog.English;
        return _byCulture.GetOrAdd(matched, Load);
    }

    private IReadOnlyDictionary<string, string> English =>
        _embedded.Value.TryGetValue(LocaleCatalog.English, out var en) ? en : new Dictionary<string, string>();

    private IReadOnlyList<HelpTopic> Load(string culture)
    {
        var translated = TranslatedFiles(culture);
        var topics = new List<HelpTopic>();

        foreach (var (file, english) in English.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var markdown = translated.TryGetValue(file, out var text) ? text : english;
            var dash = file.IndexOf('-');
            var slug = dash > 0 && file[..dash].All(char.IsDigit) ? file[(dash + 1)..] : file;

            var lines = markdown.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            var title = lines.FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim() ?? slug;
            var summary = lines.FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith('>')) ?? "";

            var html = Render(markdown);
            // Links across the app and the Help ("/Help/nodes#direct-streaming") use the English
            // headings' ids; a translated page whose headings line up with the English ones gets them.
            if (!ReferenceEquals(markdown, english)) html = WithEnglishHeadingIds(html, Render(english));
            // The app's <base href="/"> would resolve a bare "#heading" link to the Dashboard, so
            // in-page anchors are pinned to this topic's own URL.
            html = html.Replace("href=\"#", $"href=\"/Help/{slug}#");
            topics.Add(new HelpTopic(slug, title, summary, html));
        }
        return Order(topics, culture);
    }

    /// <summary>The pages a reader starts with, in this order, ahead of the rest.</summary>
    internal static readonly string[] PinnedFirst = ["overview", "installation"];

    /// <summary><see cref="PinnedFirst"/>, then every other page alphabetically by its title in this
    /// language (so a translation sorts in its own alphabet).</summary>
    internal static List<HelpTopic> Order(IEnumerable<HelpTopic> topics, string culture)
    {
        CultureInfo info;
        try { info = CultureInfo.GetCultureInfo(culture); }
        catch (CultureNotFoundException) { info = CultureInfo.InvariantCulture; }
        var comparer = StringComparer.Create(info, ignoreCase: true);
        return topics
            .OrderBy(t => Array.FindIndex(PinnedFirst, p => string.Equals(p, t.Slug, StringComparison.OrdinalIgnoreCase)) is var i && i >= 0 ? i : PinnedFirst.Length)
            .ThenBy(t => t.Title, comparer)
            .ToList();
    }

    /// <summary>File name (no extension) → Markdown for a language. Applied most general first, so
    /// later sources win: embedded parent, embedded culture, then the shipped and drop-in folders.</summary>
    private Dictionary<string, string> TranslatedFiles(string culture)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.Equals(culture, LocaleCatalog.English, StringComparison.OrdinalIgnoreCase)) return files;

        var chain = CultureChain(culture);
        foreach (var name in chain)
            if (_embedded.Value.TryGetValue(name, out var pages))
                foreach (var (file, markdown) in pages) files[file] = markdown;

        if (_catalog is null) return files;
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

    private static readonly System.Text.RegularExpressions.Regex HeadingId =
        new("<h([1-6]) id=\"([^\"]*)\"", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Gives a translated page's headings the English page's ids, in order, when both have
    /// the same headings at the same levels. A page that's been restructured keeps its own ids.
    /// In-page links ("#título-traducido") are moved to the new ids as well.</summary>
    internal static string WithEnglishHeadingIds(string translatedHtml, string englishHtml)
    {
        var english = HeadingId.Matches(englishHtml);
        var translated = HeadingId.Matches(translatedHtml);
        if (english.Count != translated.Count) return translatedHtml;
        for (var i = 0; i < english.Count; i++)
            if (english[i].Groups[1].Value != translated[i].Groups[1].Value) return translatedHtml;

        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < english.Count; i++)
            renamed.TryAdd(translated[i].Groups[2].Value, english[i].Groups[2].Value);

        var index = 0;
        var result = HeadingId.Replace(translatedHtml, m => $"<h{m.Groups[1].Value} id=\"{english[index++].Groups[2].Value}\"");
        foreach (var (from, to) in renamed)
            if (from != to) result = result.Replace($"href=\"#{from}\"", $"href=\"#{to}\"");
        return result;
    }

    /// <summary>"es-AR" → ["es", "es-AR"]: parent first.</summary>
    internal static List<string> CultureChain(string culture)
    {
        var chain = new List<string>();
        for (var name = culture; name.Length > 0;)
        {
            chain.Insert(0, name);
            var dash = name.LastIndexOf('-');
            if (dash < 0) break;
            name = name[..dash];
        }
        return chain;
    }

    /// <summary>Embedded resources "Help/{culture}/{file}.md" (MSBuild's RecursiveDir may use "\").</summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var byCulture = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var name = resource.Replace('\\', '/');
            if (!name.StartsWith(EmbeddedPrefix, StringComparison.Ordinal) || !name.EndsWith(".md", StringComparison.Ordinal)) continue;
            var parts = name[EmbeddedPrefix.Length..].Split('/');
            if (parts.Length != 2) continue;

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            if (!byCulture.TryGetValue(parts[0], out var pages))
                byCulture[parts[0]] = pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            pages[Path.GetFileNameWithoutExtension(parts[1])] = reader.ReadToEnd();
        }
        return byCulture.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }
}
