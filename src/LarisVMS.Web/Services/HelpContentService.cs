using System.Reflection;
using Markdig;

namespace LarisVMS.Web.Services;

/// <summary>One Help page: the slug is its URL (/Help/{slug}), taken from the file name minus its
/// ordering prefix ("03-cameras.md" → "cameras"); the title is the file's first "# " heading and the
/// summary its first paragraph.</summary>
public sealed record HelpTopic(string Slug, string Title, string Summary, string Html);

/// <summary>Serves the Help section's pages from the Markdown files embedded under Help/ (see
/// LarisVMS.Web.csproj). Rendered once at first use — the content only changes with a deploy.</summary>
public sealed class HelpContentService
{
    private readonly Lazy<IReadOnlyList<HelpTopic>> _topics = new(Load);

    public IReadOnlyList<HelpTopic> Topics => _topics.Value;

    public HelpTopic? Find(string slug) =>
        Topics.FirstOrDefault(t => string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<HelpTopic> Load()
    {
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var assembly = Assembly.GetExecutingAssembly();
        var topics = new List<HelpTopic>();

        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith("Help/", StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var markdown = reader.ReadToEnd();

            var file = Path.GetFileNameWithoutExtension(name["Help/".Length..]);
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
}
