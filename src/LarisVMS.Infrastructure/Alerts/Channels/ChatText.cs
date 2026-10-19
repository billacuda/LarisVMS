using System.Text;

namespace LarisVMS.Infrastructure.Alerts.Channels;

/// <summary>Makes rule and camera names safe inside chat messages: a name containing "&lt;!channel&gt;"
/// or a markdown link must show as text, not ping everyone or link somewhere.</summary>
internal static class ChatText
{
    /// <summary>Slack's own rule: escape &amp;, &lt; and &gt;, which is what mentions and links use.</summary>
    public static string Slack(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Teams connector text is markdown with some HTML: encode HTML and escape markdown.</summary>
    public static string Teams(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '\\' or '*' or '_' or '`' or '[' or ']' or '(' or ')' or '#' or '~' or '|':
                    sb.Append('\\').Append(c); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>For a one-line HTTP header value.</summary>
    public static string HeaderLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
}
