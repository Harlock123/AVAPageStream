using System.ComponentModel;
using System.Text.Json.Nodes;
using AVAPageStream.Core;
using ModelContextProtocol.Server;

namespace AVAPageStream.Mcp;

/// <summary>The tools for text: stories, their formats, styles, swatches, and find and replace.</summary>
public sealed partial class PageTools
{
    private const string StoryHelp = "A text frame's id (its story), a story's id, or a table cell's story id.";

    [McpServerTool(Name = "get_text", ReadOnly = true, Idempotent = true)]
    [Description("A story's text, paragraph by paragraph: where each starts (character positions count from 0), its style and its text, with page numbers shown as {page} and the page count as {pages}. Also which frames it runs through and whether it's overset (and from which character). With formats true, each paragraph's stretches of text that differ from its style (bold, a font, a colour, a character style).")]
    public Task<string> GetText(
        [Description(StoryHelp)] string id,
        [Description("Also the formats inside each paragraph. Default false.")] bool formats = false,
        CancellationToken cancel = default) =>
        Read("read text", s =>
        {
            var d = s.Editor.Doc;
            var story = d.Stories[StoryOf(d, id)];
            var paras = new JsonArray();
            for (var p = 0; p < story.ParagraphCount; p++)
            {
                var (start, end) = story.ParagraphRange(p);
                var para = story.Paras[p];
                var o = new JsonObject { ["start"] = start, ["style"] = para.Style };
                if (para.Align != Align.Left) o["align"] = para.Align.ToString().ToLowerInvariant();
                o["text"] = Shown(story.Text[start..end]);
                if (formats)
                {
                    var style = d.Style(para.Style).Char;
                    var runs = new JsonArray();
                    var at = 0;
                    foreach (var run in story.Runs)
                    {
                        var (a, b) = (Math.Max(at, start), Math.Min(at + run.Length, end));
                        if (a < b && Differences(run.Format, style) is { Count: > 0 } diff)
                        {
                            diff["start"] = a;
                            diff["length"] = b - a;
                            runs.Add(diff);
                        }
                        at += run.Length;
                    }
                    if (runs.Count > 0) o["formats"] = runs;
                }
                paras.Add(o);
            }
            var result = new JsonObject
            {
                ["story"] = story.Id,
                ["frames"] = new JsonArray(story.Frames.Select(f => (JsonNode)f).ToArray()),
                ["characters"] = story.Length,
                ["paragraphs"] = paras,
            };
            if (story.Table is { } table) result["table"] = table;
            if (host.Library.LayoutOf(d, story.Id) is { } layout)
            {
                result["overset"] = layout.Overset;
                if (layout.Overset) result["overset_from"] = layout.OversetFrom;
            }
            return result;
        }, cancel);

    [McpServerTool(Name = "set_text")]
    [Description("Puts text into a story: by default replacing all of it, or with start and length replacing just that stretch (length 0 inserts at start). The text is paragraphs separated by newlines, or Markdown with markdown true (# Heading, ## Subheading, **bold**, *italic*, lists, quotes); file reads it from a .txt or .md file in the working folder instead. \"{page}\" puts in the page number and \"{pages}\" the page count. Says whether it all fits.")]
    public Task<string> SetText(
        [Description(StoryHelp)] string id,
        [Description("The text. Or give file.")] string? text = null,
        [Description("A .txt or .md file in the working folder to read the text from (.md is read as Markdown).")] string? file = null,
        [Description("Read the text as Markdown. Default false (true for a .md file).")] bool? markdown = null,
        [Description("The paragraph style for its paragraphs (with markdown, for the plain ones). Default Body.")] string? style = null,
        [Description("Where to start replacing, counting characters from 0. Default 0.")] int start = 0,
        [Description("How many characters to replace. Default: everything from start to the end.")] int? length = null,
        CancellationToken cancel = default) =>
        Edit("set text", s =>
        {
            var e = s.Editor;
            var storyId = StoryOf(e.Doc, id);
            var story = e.Doc.Stories[storyId];
            if (story.OnPath is not null && markdown == true) throw new ArgumentException("Text on a path is one line: markdown doesn't apply.");
            if ((text is null) == (file is null)) throw new ArgumentException("Give text, or file to read it from (not both).");
            var md = markdown ?? false;
            if (file is not null)
            {
                var full = _files.Resolve(file, "The text file", ".txt", ".md", ".markdown", ".text");
                if (!File.Exists(full)) throw new FileNotFoundException($"There's no {file} in the working folder.");
                text = File.ReadAllText(full);
                md = markdown ?? Path.GetExtension(full).ToLowerInvariant() is ".md" or ".markdown";
            }
            if (start < 0 || start > story.Length) throw new ArgumentException($"start is from 0 to {story.Length} (the story's length).");
            var len = length ?? story.Length - start;
            if (len < 0 || start + len > story.Length) throw new ArgumentException($"length runs past the story's end: from {start} there are {story.Length - start} characters.");
            PutText(e, storyId, start, len, text!, md, style);
            var result = new JsonObject { ["story"] = storyId, ["characters"] = e.Doc.Stories[storyId].Length, ["paragraphs"] = e.Doc.Stories[storyId].ParagraphCount };
            AddFit(result, e.Doc, storyId);
            return result;
        }, cancel);

    [McpServerTool(Name = "format_text")]
    [Description("Changes how characters look in a stretch of a story (all of it unless start and length are given): font, size, bold, italic, underline, colour, tracking, small caps, superscript or subscript; or a character style. Give only what changes. For a whole paragraph's look, prefer format_paragraphs with a style.")]
    public Task<string> FormatText(
        [Description(StoryHelp)] string id,
        [Description("From this character (counting from 0). Default 0.")] int start = 0,
        [Description("This many characters. Default: to the end.")] int? length = null,
        [Description("A font family (list_fonts).")] string? font = null,
        [Description("Size in points.")] double? size = null,
        [Description("Bold.")] bool? bold = null,
        [Description("Italic.")] bool? italic = null,
        [Description("Underlined.")] bool? underline = null,
        [Description("Colour: \"#RRGGBB\" or a swatch's name.")] string? colour = null,
        [Description("Tracking: thousandths of an em added after each character (negative tightens).")] double? tracking = null,
        [Description("Small capitals.")] bool? small_caps = null,
        [Description("normal, superscript or subscript.")] string? script = null,
        [Description("A character style to apply (get_document lists them), or \"none\" to take one off.")] string? char_style = null,
        CancellationToken cancel = default) =>
        Edit("formatted text", s =>
        {
            var e = s.Editor;
            var storyId = StoryOf(e.Doc, id);
            var (a, n) = Range(e.Doc.Stories[storyId], start, length);
            var f = Font(font);
            if (size is { } sz && (sz < 1 || sz > 1000)) throw new ArgumentException("size is from 1 to 1000 points.");
            var c = colour is null ? null : Colour(e.Doc, colour, "colour") ?? throw new ArgumentException("Text needs a colour: give one, not \"none\".");
            Script? sc = script?.Trim().ToLowerInvariant() switch
            {
                null => null, "normal" or "none" => Core.Script.Normal, "superscript" or "super" => Core.Script.Super, "subscript" or "sub" => Core.Script.Sub,
                _ => throw new ArgumentException($"script is normal, superscript or subscript, not \"{script}\"."),
            };
            if (char_style is not null)
            {
                var none = char_style.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);
                var cs = none ? null : e.Doc.CharStyles.FirstOrDefault(x => x.Name.Equals(char_style.Trim(), StringComparison.OrdinalIgnoreCase))?.Name
                    ?? throw new ArgumentException($"There's no character style \"{char_style}\". The character styles: {string.Join(", ", e.Doc.CharStyles.Select(x => x.Name))}; define_character_style makes one.");
                e.ApplyCharStyle(storyId, a, n, cs);
            }
            if (f is not null || size is not null || bold is not null || italic is not null || underline is not null || c is not null || tracking is not null || small_caps is not null || sc is not null)
                e.FormatChars(storyId, a, n, x => x with
                {
                    Font = f ?? x.Font, Size = size ?? x.Size, Bold = bold ?? x.Bold, Italic = italic ?? x.Italic,
                    Underline = underline ?? x.Underline, Colour = c ?? x.Colour, Tracking = tracking ?? x.Tracking,
                    SmallCaps = small_caps ?? x.SmallCaps, Script = sc ?? x.Script,
                });
            var result = new JsonObject { ["story"] = storyId, ["start"] = a, ["length"] = n };
            AddFit(result, e.Doc, storyId);
            return result;
        }, cancel);

    [McpServerTool(Name = "format_paragraphs")]
    [Description("Changes the paragraphs a stretch of a story touches (all of them unless start and length are given): a paragraph style, and alignment, leading, indents, space before and after, hyphenation, keeping with the next paragraph or together, and a drop cap. Give only what changes; a style is applied first, then the rest over it.")]
    public Task<string> FormatParagraphs(
        [Description(StoryHelp)] string id,
        [Description("From the paragraph holding this character (counting from 0). Default 0.")] int start = 0,
        [Description("Through the paragraph holding character start + length. Default: to the end.")] int? length = null,
        [Description("A paragraph style (get_document lists them).")] string? style = null,
        [Description("left, centre, right or justify.")] string? align = null,
        [Description("Leading: baseline to baseline, in points (0: automatic, 120% of the size).")] double? leading = null,
        [Description("Left indent. " + MeasureHelp)] System.Text.Json.JsonElement? left_indent = null,
        [Description("Right indent. " + MeasureHelp)] System.Text.Json.JsonElement? right_indent = null,
        [Description("First-line indent (negative hangs). " + MeasureHelp)] System.Text.Json.JsonElement? first_indent = null,
        [Description("Space before. " + MeasureHelp)] System.Text.Json.JsonElement? space_before = null,
        [Description("Space after. " + MeasureHelp)] System.Text.Json.JsonElement? space_after = null,
        [Description("Hyphenate words at line ends.")] bool? hyphenate = null,
        [Description("Keep with the next paragraph (for headings).")] bool? keep_with_next = null,
        [Description("Keep all its lines in one column.")] bool? keep_together = null,
        [Description("A drop cap this many lines deep (0: none).")] int? drop_lines = null,
        CancellationToken cancel = default) =>
        Edit("formatted paragraphs", s =>
        {
            var e = s.Editor;
            var storyId = StoryOf(e.Doc, id);
            var (a, n) = Range(e.Doc.Stories[storyId], start, length);
            if (style is not null) e.ApplyStyle(storyId, a, n, StyleName(e.Doc, style));
            var al = Alignment(align);
            var (li, ri, fi, sb, sa) = (Measure(left_indent, "left_indent"), Measure(right_indent, "right_indent"), Measure(first_indent, "first_indent", allowNegative: true), Measure(space_before, "space_before"), Measure(space_after, "space_after"));
            if (drop_lines is { } dl && dl is < 0 or > 10) throw new ArgumentException("drop_lines is from 0 to 10.");
            if (leading is { } ld && (ld < 0 || ld > 1000)) throw new ArgumentException("leading is from 0 (automatic) to 1000 points.");
            if (al is not null || leading is not null || li is not null || ri is not null || fi is not null || sb is not null || sa is not null || hyphenate is not null || keep_with_next is not null || keep_together is not null || drop_lines is not null)
                e.FormatParas(storyId, a, n, p => p with
                {
                    Align = al ?? p.Align, Leading = leading ?? p.Leading, LeftIndent = li ?? p.LeftIndent, RightIndent = ri ?? p.RightIndent,
                    FirstIndent = fi ?? p.FirstIndent, SpaceBefore = sb ?? p.SpaceBefore, SpaceAfter = sa ?? p.SpaceAfter,
                    Hyphenate = hyphenate ?? p.Hyphenate, KeepWithNext = keep_with_next ?? p.KeepWithNext, KeepTogether = keep_together ?? p.KeepTogether,
                    DropLines = drop_lines ?? p.DropLines,
                });
            var story = e.Doc.Stories[storyId];
            var (first, last) = story.ParagraphsIn(a, n);
            var result = new JsonObject { ["story"] = storyId, ["paragraphs"] = $"{first + 1} to {last + 1} of {story.ParagraphCount}" };
            AddFit(result, e.Doc, storyId);
            return result;
        }, cancel);

    [McpServerTool(Name = "define_paragraph_style")]
    [Description("Makes a paragraph style, or changes one (every paragraph in it changes too). A new style starts as a copy of based_on (default Body); give only what differs.")]
    public Task<string> DefineParagraphStyle(
        [Description("The style's name.")] string name,
        [Description("A new style starts as a copy of this one. Default Body.")] string? based_on = null,
        [Description("A font family (list_fonts).")] string? font = null,
        [Description("Size in points.")] double? size = null,
        [Description("Bold.")] bool? bold = null,
        [Description("Italic.")] bool? italic = null,
        [Description("Colour: \"#RRGGBB\" or a swatch's name.")] string? colour = null,
        [Description("Tracking, thousandths of an em.")] double? tracking = null,
        [Description("Small capitals.")] bool? small_caps = null,
        [Description("left, centre, right or justify.")] string? align = null,
        [Description("Leading in points (0: automatic).")] double? leading = null,
        [Description("Left indent. " + MeasureHelp)] System.Text.Json.JsonElement? left_indent = null,
        [Description("First-line indent (negative hangs). " + MeasureHelp)] System.Text.Json.JsonElement? first_indent = null,
        [Description("Space before. " + MeasureHelp)] System.Text.Json.JsonElement? space_before = null,
        [Description("Space after. " + MeasureHelp)] System.Text.Json.JsonElement? space_after = null,
        [Description("Hyphenate.")] bool? hyphenate = null,
        [Description("Keep with the next paragraph.")] bool? keep_with_next = null,
        CancellationToken cancel = default) =>
        Edit("defined a paragraph style", s =>
        {
            var e = s.Editor;
            var n = name.Trim();
            if (n.Length == 0) throw new ArgumentException("A style needs a name.");
            var existing = e.Doc.Styles.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            var basis = existing ?? (based_on is null ? e.Doc.Style("Body") : e.Doc.Styles.FirstOrDefault(x => x.Name.Equals(based_on.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"There's no style \"{based_on}\". The styles: {string.Join(", ", e.Doc.Styles.Select(x => x.Name))}."));
            var f = Font(font);
            if (size is { } sz && (sz < 1 || sz > 1000)) throw new ArgumentException("size is from 1 to 1000 points.");
            var c = colour is null ? null : Colour(e.Doc, colour, "colour") ?? throw new ArgumentException("Text needs a colour: give one, not \"none\".");
            var ch = basis.Char with
            {
                Font = f ?? basis.Char.Font, Size = size ?? basis.Char.Size, Bold = bold ?? basis.Char.Bold, Italic = italic ?? basis.Char.Italic,
                Colour = c ?? basis.Char.Colour, Tracking = tracking ?? basis.Char.Tracking, SmallCaps = small_caps ?? basis.Char.SmallCaps,
            };
            var pa = basis.Para with
            {
                Style = existing?.Name ?? n, Align = Alignment(align) ?? basis.Para.Align, Leading = leading ?? basis.Para.Leading,
                LeftIndent = Measure(left_indent, "left_indent") ?? basis.Para.LeftIndent, FirstIndent = Measure(first_indent, "first_indent", true) ?? basis.Para.FirstIndent,
                SpaceBefore = Measure(space_before, "space_before") ?? basis.Para.SpaceBefore, SpaceAfter = Measure(space_after, "space_after") ?? basis.Para.SpaceAfter,
                Hyphenate = hyphenate ?? basis.Para.Hyphenate, KeepWithNext = keep_with_next ?? basis.Para.KeepWithNext,
            };
            var style = new ParagraphStyle(existing?.Name ?? n, ch, pa);
            e.DefineStyle(style);
            return DescribeStyle(style);
        }, cancel);

    [McpServerTool(Name = "define_character_style")]
    [Description("Makes a character style, or changes one (text in it changes too): only what it sets is changed in the text it's applied to (format_text char_style), so a bold style keeps each paragraph's own font and size.")]
    public Task<string> DefineCharacterStyle(
        [Description("The style's name.")] string name,
        [Description("A font family.")] string? font = null,
        [Description("Size in points.")] double? size = null,
        [Description("Bold.")] bool? bold = null,
        [Description("Italic.")] bool? italic = null,
        [Description("Underlined.")] bool? underline = null,
        [Description("Colour: \"#RRGGBB\" or a swatch's name.")] string? colour = null,
        [Description("Tracking, thousandths of an em.")] double? tracking = null,
        [Description("Small capitals.")] bool? small_caps = null,
        CancellationToken cancel = default) =>
        Edit("defined a character style", s =>
        {
            var e = s.Editor;
            var existing = e.Doc.CharStyles.FirstOrDefault(x => x.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            var c = colour is null ? null : Colour(e.Doc, colour, "colour");
            var set = new CharOverrides(Font(font), size, bold, italic, underline, c, tracking, small_caps);
            if (existing is not null)
                set = new CharOverrides(set.Font ?? existing.Set.Font, set.Size ?? existing.Set.Size, set.Bold ?? existing.Set.Bold, set.Italic ?? existing.Set.Italic,
                    set.Underline ?? existing.Set.Underline, set.Colour ?? existing.Set.Colour, set.Tracking ?? existing.Set.Tracking, set.SmallCaps ?? existing.Set.SmallCaps, existing.Set.Script);
            var style = new CharacterStyle(existing?.Name ?? name.Trim(), set);
            if (e.DefineCharStyle(style) is { } why) throw new ArgumentException(why);
            return new JsonObject { ["name"] = style.Name, ["sets"] = style.Set.Describe() };
        }, cancel);

    [McpServerTool(Name = "add_swatch")]
    [Description("Adds a named colour (a swatch) to the document, or changes one: everything in it changes too. Colours given by its name (\"Brand blue\", or \"Brand blue 40%\" for a tint) then follow it.")]
    public Task<string> AddSwatch(
        [Description("The swatch's name.")] string name,
        [Description("Its colour: \"#RRGGBB\", or CMYK per cents such as \"0 100 100 0\".")] string colour,
        [Description("A spot colour (its own ink in a CMYK PDF). Default false.")] bool spot = false,
        CancellationToken cancel = default) =>
        Edit("added a swatch", s =>
        {
            var e = s.Editor;
            var existing = e.Doc.Swatches.FirstOrDefault(w => w.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            var why = existing is null ? e.AddSwatch(name, colour, spot) : e.ChangeSwatch(existing.Name, colour, spot);
            if (why is not null) throw new ArgumentException(why);
            var w = e.Doc.Swatches.First(x => x.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            return new JsonObject { ["name"] = w.Name, ["colour"] = w.Colour, ["spot"] = w.Spot, ["changed"] = existing is not null };
        }, cancel);

    [McpServerTool(Name = "find_text", ReadOnly = true, Idempotent = true)]
    [Description("Finds text through every story in reading order: plain text, whole words, or a regular expression (pattern true); optionally only in a paragraph or character style, and with an empty query everything in that style. Gives each match's story, start and length (for set_text and format_text), the text round it and the page its story starts on.")]
    public Task<string> FindText(
        [Description("What to look for (empty with a style finds all text in that style).")] string query,
        [Description("Match capitals exactly. Default false.")] bool match_case = false,
        [Description("Whole words only. Default false.")] bool whole_word = false,
        [Description("query is a regular expression (.NET's). Default false.")] bool pattern = false,
        [Description("Only in paragraphs of this style.")] string? paragraph_style = null,
        [Description("Only in text in this character style.")] string? character_style = null,
        [Description("At most this many matches. Default 50.")] int limit = 50,
        CancellationToken cancel = default) =>
        Read("found text", s =>
        {
            var d = s.Editor.Doc;
            var options = new FindOptions(match_case, whole_word, pattern, paragraph_style, character_style);
            if (Search.Problem(d, query, options) is { } why) throw new ArgumentException(why);
            var all = Search.FindAll(d, query, options);
            return new JsonObject
            {
                ["count"] = all.Count,
                ["matches"] = new JsonArray(all.Take(Math.Clamp(limit, 1, 500)).Select(m =>
                {
                    var story = d.Stories[m.StoryId];
                    var from = Math.Max(0, m.Start - 30);
                    var to = Math.Min(story.Length, m.Start + m.Length + 30);
                    return (JsonNode)new JsonObject
                    {
                        ["story"] = m.StoryId, ["start"] = m.Start, ["length"] = m.Length,
                        ["context"] = Shown(story.Text[from..to]).Replace('\n', '¶'),
                        ["page"] = Chains.PageOf(d, story),
                    };
                }).ToArray()),
            };
        }, cancel);

    [McpServerTool(Name = "replace_text")]
    [Description("Replaces every match of a find (as find_text finds them) with other text, and optionally puts what's replaced in a paragraph or character style (with an empty replacement and a style, the text is kept and restyled). With pattern, $1 and so on in the replacement are the pattern's groups.")]
    public Task<string> ReplaceText(
        [Description("What to look for.")] string query,
        [Description("What to put instead (empty with a style: keep the text, change its style).")] string replacement,
        [Description("Match capitals exactly. Default false.")] bool match_case = false,
        [Description("Whole words only. Default false.")] bool whole_word = false,
        [Description("query is a regular expression. Default false.")] bool pattern = false,
        [Description("Only in paragraphs of this style.")] string? paragraph_style = null,
        [Description("Only in text in this character style.")] string? character_style = null,
        [Description("Put the paragraphs replaced in this paragraph style.")] string? to_paragraph_style = null,
        [Description("Put what's replaced in this character style.")] string? to_character_style = null,
        CancellationToken cancel = default) =>
        Edit("replaced text", s =>
        {
            var e = s.Editor;
            var options = new FindOptions(match_case, whole_word, pattern, paragraph_style, character_style);
            if (Search.Problem(e.Doc, query, options) is { } why) throw new ArgumentException(why);
            var change = new StyleChange(to_paragraph_style is null ? null : StyleName(e.Doc, to_paragraph_style), to_character_style);
            if (to_character_style is not null && e.Doc.CharStyle(to_character_style) is null)
                throw new ArgumentException($"There's no character style \"{to_character_style}\".");
            var n = e.ReplaceAll(query, replacement.Replace("{page}", Fields.PageNumber.ToString()), options, change);
            return new JsonObject { ["replaced"] = n };
        }, cancel);

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>The story an id means: a story's own id, a text frame's, or text on a path's.</summary>
    private static string StoryOf(Document d, string id)
    {
        if (d.Stories.ContainsKey(id)) return id;
        return d.Find(id) switch
        {
            TextFrame f when d.Stories.ContainsKey(f.StoryId) => f.StoryId,
            PathObject { StoryId: { } p } when d.Stories.ContainsKey(p) => p,
            TableObject => throw new ArgumentException($"{id} is a table: give one of its cells' story ids (get_page lists them under cells)."),
            null => throw new ArgumentException($"There's no story or text frame \"{id}\". get_document lists the stories; get_page a page's frames."),
            var o => throw new ArgumentException($"{id} is a {o.Kind.ToLowerInvariant()}, which holds no text."),
        };
    }

    private static (int Start, int Length) Range(Story story, int start, int? length)
    {
        if (start < 0 || start > story.Length) throw new ArgumentException($"start is from 0 to {story.Length} (the story's length).");
        var n = length ?? story.Length - start;
        if (n < 0 || start + n > story.Length) throw new ArgumentException($"length runs past the story's end: from {start} there are {story.Length - start} characters.");
        return (start, n);
    }

    /// <summary>Text as an agent writes it ({page}, {pages}) to the field characters a story holds.</summary>
    private static string Fielded(string text) =>
        text.Replace("{page}", Fields.PageNumber.ToString()).Replace("{pages}", Fields.PageCount.ToString());

    /// <summary>A story's text for an agent: fields as {page} and {pages}, other markers left out.</summary>
    internal static string Shown(string text) =>
        new string(text.Replace(Fields.PageNumber.ToString(), "{page}").Replace(Fields.PageCount.ToString(), "{pages}")
            .Where(c => !Fields.IsField(c)).ToArray());

    private void PutText(Editor e, string storyId, int start, int length, string text, bool markdown, string? style)
    {
        var plainStyle = style is null ? null : StyleName(e.Doc, style);
        text = Fielded(text);
        var paras = markdown ? TextImport.Markdown(text) : TextImport.PlainText(text);
        if (plainStyle is not null) paras = paras.Select(p => p.Style == "Body" ? p with { Style = plainStyle } : p).ToList();
        foreach (var p in paras)
            if (!e.Doc.Styles.Any(x => x.Name == p.Style))
                e.DefineStyle(e.Doc.Style("Body") with { Name = p.Style, Para = e.Doc.Style("Body").Para with { Style = p.Style } });
        if (length > 0) e.ReplaceText(storyId, start, length, "");
        if (paras.Count == 0) return;
        if (e.Doc.Stories[storyId].OnPath is not null)
        {
            e.ReplaceText(storyId, start, 0, string.Join(" ", paras.Select(p => p.Text)));
            return;
        }
        e.Place(storyId, start, paras);
    }

    private string? Font(string? font)
    {
        if (font is null) return null;
        var f = host.Library.Fonts.FirstOrDefault(x => x.Equals(font.Trim(), StringComparison.OrdinalIgnoreCase));
        return f ?? throw new ArgumentException($"There's no font \"{font}\" on this computer. list_fonts gives them; Liberation Serif and Liberation Sans are always there.");
    }

    private static string StyleName(Document d, string style) =>
        d.Styles.FirstOrDefault(x => x.Name.Equals(style.Trim(), StringComparison.OrdinalIgnoreCase))?.Name
        ?? throw new ArgumentException($"There's no paragraph style \"{style}\". The styles: {string.Join(", ", d.Styles.Select(x => x.Name))}; define_paragraph_style makes one.");

    private static Align? Alignment(string? align) => align?.Trim().ToLowerInvariant() switch
    {
        null => null, "left" => Align.Left, "centre" or "center" => Align.Centre, "right" => Align.Right, "justify" or "justified" => Align.Justify,
        _ => throw new ArgumentException($"align is left, centre, right or justify, not \"{align}\"."),
    };

    internal static JsonObject DescribeStyle(ParagraphStyle st)
    {
        var o = new JsonObject
        {
            ["name"] = st.Name, ["font"] = st.Char.Font, ["size"] = R(st.Char.Size),
        };
        if (st.Char.Bold) o["bold"] = true;
        if (st.Char.Italic) o["italic"] = true;
        if (st.Char.Colour != Colours.Black) o["colour"] = st.Char.Colour;
        if (st.Para.Align != Align.Left) o["align"] = st.Para.Align.ToString().ToLowerInvariant();
        if (st.Para.Leading > 0) o["leading"] = R(st.Para.Leading);
        if (st.Para.SpaceBefore > 0) o["space_before"] = R(st.Para.SpaceBefore);
        if (st.Para.SpaceAfter > 0) o["space_after"] = R(st.Para.SpaceAfter);
        if (st.Para.FirstIndent != 0) o["first_indent"] = R(st.Para.FirstIndent);
        if (st.Para.LeftIndent != 0) o["left_indent"] = R(st.Para.LeftIndent);
        return o;
    }

    /// <summary>How a run's format differs from its paragraph style's.</summary>
    private static JsonObject Differences(CharFormat f, CharFormat style)
    {
        var o = new JsonObject();
        if (f.Font != style.Font) o["font"] = f.Font;
        if (f.Size != style.Size) o["size"] = R(f.Size);
        if (f.Bold != style.Bold) o["bold"] = f.Bold;
        if (f.Italic != style.Italic) o["italic"] = f.Italic;
        if (f.Underline != style.Underline) o["underline"] = f.Underline;
        if (f.Colour != style.Colour) o["colour"] = f.Colour;
        if (f.Tracking != style.Tracking) o["tracking"] = R(f.Tracking);
        if (f.SmallCaps != style.SmallCaps) o["small_caps"] = f.SmallCaps;
        if (f.Script != style.Script) o["script"] = f.Script.ToString().ToLowerInvariant();
        if (f.CharStyle is { } cs) o["char_style"] = cs;
        return o;
    }
}
