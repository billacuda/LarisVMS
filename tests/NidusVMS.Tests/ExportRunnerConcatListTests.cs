using NidusVMS.Node;

namespace NidusVMS.Tests;

/// <summary>
/// Guards ExportRunner's ffmpeg concat-demuxer list file format — a single malformed line breaks
/// the whole `-f concat` read, so the quoting rule (escape a literal single quote as <c>'\''</c>,
/// leave everything else — including a Windows path's backslashes — untouched inside the quotes)
/// needs to be exactly right.
/// </summary>
public class ExportRunnerConcatListTests
{
    [Fact]
    public void WritesOneQuotedFileLinePerSegment()
    {
        var list = ExportRunner.BuildConcatList([@"C:\rec\cam-1\main\a.mp4", @"C:\rec\cam-1\main\b.mp4"]);

        Assert.Equal(
            "file 'C:\\rec\\cam-1\\main\\a.mp4'\n" +
            "file 'C:\\rec\\cam-1\\main\\b.mp4'\n",
            list);
    }

    [Fact]
    public void EscapesALiteralSingleQuoteInAPath()
    {
        // ffmpeg's own documented concat-file quoting rule: close the quote, escape a literal
        // quote, reopen — anything else (including backslashes) is taken literally inside quotes.
        var list = ExportRunner.BuildConcatList([@"C:\rec\Bob's Camera\main\a.mp4"]);

        Assert.Equal("file 'C:\\rec\\Bob'\\''s Camera\\main\\a.mp4'\n", list);
    }

    [Fact]
    public void ReturnsAnEmptyStringForNoSegments()
    {
        Assert.Equal("", ExportRunner.BuildConcatList([]));
    }
}
