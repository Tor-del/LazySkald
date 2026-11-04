using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Renderers.Html;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Helpers;
using System.Buffers;
using System.Text;

namespace ScreenReader.App;

public static class TranslationPlainText
{
    private static readonly MarkdownPipeline Pipeline=new MarkdownPipelineBuilder()
        .UsePipeTables().UseGridTables().UseEmphasisExtras().UseTaskLists().Build();

    // Parse the structure so literal punctuation, code and escaped symbols survive.
    public static string Convert(string text)=>CleanCharacters(Render(Markdown.Parse(CleanCharacters(text),Pipeline)));

    private static string CleanCharacters(string text)
    {
        var result=new StringBuilder(text.Length);
        for (int i=0;i<text.Length;)
        {
            var status=Rune.DecodeFromUtf16(text.AsSpan(i),out var rune,out int consumed);
            i+=consumed;
            if (status!=OperationStatus.Done) continue;
            int value=rune.Value;
            // Keep Unicode letters, combining accents, emoji and script joiners.
            // Drop invalid XML characters and invisible transport/formatting noise.
            if (value is '\r' or '\n' or '\t') { result.Append((char)value); continue; }
            if (Rune.IsControl(rune) || value is 0x00AD or 0x200B or 0xFEFF or 0x2060
                or 0x200E or 0x200F or >=0x202A and <=0x202E or >=0x2066 and <=0x2069
                or >=0xFDD0 and <=0xFDEF || (value&0xFFFF) is 0xFFFE or 0xFFFF) continue;
            result.Append(rune.ToString());
        }
        return result.ToString();
    }

    private static string Render(MarkdownObject node)
    {
        using var writer=new StringWriter();
        var renderer=new HtmlRenderer(writer) { EnableHtmlForBlock=false,EnableHtmlForInline=false,EnableHtmlEscape=false };
        Pipeline.Setup(renderer);
        renderer.ObjectRenderers.Insert(0,new PlainTableRenderer());
        renderer.ObjectRenderers.Insert(0,new PlainTaskRenderer());
        renderer.Render(node);
        return writer.ToString().Trim();
    }

    private sealed class PlainTaskRenderer : HtmlObjectRenderer<TaskList>
    {
        protected override void Write(HtmlRenderer renderer,TaskList task)
        {
            if (task.NextSibling is LiteralInline literal)
                literal.Content=new StringSlice(literal.Content.ToString().TrimStart());
        }
    }

    private sealed class PlainTableRenderer : HtmlObjectRenderer<Table>
    {
        protected override void Write(HtmlRenderer renderer,Table table)
        {
            renderer.EnsureLine();
            foreach (TableRow row in table)
            {
                for (int i=0;i<row.Count;i++)
                {
                    if (i>0) renderer.Write('\t');
                    renderer.Write(Render(row[i]));
                }
                renderer.WriteLine();
            }
        }
    }
}
