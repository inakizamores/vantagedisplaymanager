using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Vantage.App;

/// <summary>
/// Renders the slice of Markdown a changelog actually uses — headings, bullets, inline bold and
/// `code` — into WPF text blocks. Deliberately not a Markdown library: pulling in a parser and
/// a viewer to display twenty lines of release notes would cost more than the feature.
/// Anything it doesn't recognise falls through as plain text, which is the right failure mode.
/// </summary>
public static partial class ReleaseNotesRenderer
{
    public static void Render(Panel target, string markdown)
    {
        target.Children.Clear();

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.Trim().Length == 0)
                continue;

            // Headings: "## Added" → a small section label.
            if (line.StartsWith('#'))
            {
                var text = line.TrimStart('#', ' ');
                target.Children.Add(new Wpf.Ui.Controls.TextBlock
                {
                    Text = text,
                    FontTypography = Wpf.Ui.Controls.FontTypography.BodyStrong,
                    Margin = new Thickness(0, target.Children.Count == 0 ? 0 : 14, 0, 4),
                    TextWrapping = TextWrapping.Wrap,
                });
                continue;
            }

            // Bullets, including the indented continuation lines a wrapped changelog entry has.
            var bullet = BulletPattern().Match(line);
            if (bullet.Success)
            {
                target.Children.Add(Paragraph(bullet.Groups["text"].Value, indent: 16, bullet: true));
                continue;
            }

            // A continuation line of the previous bullet — keep it with its bullet.
            if (raw.StartsWith("  ", StringComparison.Ordinal) &&
                target.Children.Count > 0 &&
                target.Children[^1] is TextBlock previous)
            {
                AppendInlines(previous, " " + line.Trim());
                continue;
            }

            target.Children.Add(Paragraph(line, indent: 0, bullet: false));
        }

        if (target.Children.Count == 0)
            target.Children.Add(Paragraph("No release notes were published for this version.", 0, false));
    }

    private static TextBlock Paragraph(string text, double indent, bool bullet)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(indent, 0, 0, 5),
        };

        if (bullet)
            block.Inlines.Add(new Run("• ") { Foreground = SystemColors.GrayTextBrush });

        AppendInlines(block, text);
        return block;
    }

    /// <summary>Splits on **bold** and `code` runs, leaving everything else as plain text.</summary>
    private static void AppendInlines(TextBlock block, string text)
    {
        var position = 0;
        foreach (Match match in InlinePattern().Matches(text))
        {
            if (match.Index > position)
                block.Inlines.Add(new Run(text[position..match.Index]));

            if (match.Groups["bold"].Success)
                block.Inlines.Add(new Run(match.Groups["bold"].Value) { FontWeight = FontWeights.SemiBold });
            else
                block.Inlines.Add(new Run(match.Groups["code"].Value) { FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas") });

            position = match.Index + match.Length;
        }

        if (position < text.Length)
            block.Inlines.Add(new Run(text[position..]));
    }

    [GeneratedRegex(@"^\s*[-*+]\s+(?<text>.*)$")]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"\*\*(?<bold>[^*]+)\*\*|`(?<code>[^`]+)`")]
    private static partial Regex InlinePattern();
}
