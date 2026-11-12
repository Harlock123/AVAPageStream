using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

/// <summary>Milestone 4: character styles, keeping text together, page-number sections, layers.</summary>
public class LongDocumentTests
{
    private static readonly FixedMeasurer M = new();

    private static (Editor E, string Story) Text(string text)
    {
        var e = new Editor(Document.New());
        var f = e.Add(SheetRef.PageAt(0), new TextFrame { W = 300, H = 300 });
        e.ReplaceText(f.StoryId, 0, 0, text);
        return (e, f.StoryId);
    }

    [Fact]
    public void A_character_style_lays_its_settings_over_the_paragraphs()
    {
        var (e, id) = Text("Plain and emphasised\nHeading words");
        e.ApplyCharStyle(id, 10, 10, "Emphasis");
        var s = e.Doc.Stories[id];
        Assert.True(s.FormatAt(12).Italic);
        Assert.Equal("Emphasis", s.FormatAt(12).CharStyle);
        Assert.False(s.FormatAt(5).Italic);

        // A paragraph style applied over it keeps it: a heading's emphasis is italic heading type.
        e.ApplyCharStyle(id, 21, 7, "Emphasis");
        e.ApplyStyle(id, 21, 0, "Heading");
        s = e.Doc.Stories[id];
        Assert.Equal((24.0, true, true, "Emphasis"), (s.FormatAt(22).Size, s.FormatAt(22).Italic, s.FormatAt(22).Bold, s.FormatAt(22).CharStyle));
        Assert.False(s.FormatAt(30).Italic);

        // Redefined, its text follows; taken off, the text is its paragraph's style again.
        e.DefineCharStyle(new CharacterStyle("Emphasis", new CharOverrides(Italic: true, Colour: "@Red")));
        Assert.Equal("@Red", e.Doc.Stories[id].FormatAt(12).Colour);
        e.ApplyCharStyle(id, 10, 10, null);
        Assert.Equal(e.Doc.Style("Body").Char, e.Doc.Stories[id].FormatAt(12));
    }

    [Fact]
    public void Character_styles_are_renamed_and_deleted_with_their_text_following()
    {
        var (e, id) = Text("one two three");
        e.ApplyCharStyle(id, 0, 3, "Strong");
        e.ApplyCharStyle(id, 4, 3, "Emphasis");
        Assert.Null(e.RenameCharStyle("Strong", "Loud"));
        Assert.Equal("Loud", e.Doc.Stories[id].FormatAt(0).CharStyle);
        Assert.NotNull(e.RenameCharStyle("Loud", "Emphasis"));
        Assert.Null(e.DeleteCharStyle("Loud"));
        var s = e.Doc.Stories[id];
        Assert.False(s.FormatAt(0).Bold);                     // back to the paragraph's
        Assert.Equal("Emphasis", s.FormatAt(5).CharStyle);    // the other character style is left alone
        Assert.True(s.FormatAt(5).Italic);
    }

    [Fact]
    public void A_character_style_is_the_difference_from_the_paragraphs_type()
    {
        var basis = new CharFormat(Size: 10);
        var set = CharOverrides.Difference(basis, basis with { Bold = true, Size = 14 });
        Assert.Equal(new CharOverrides(Size: 14, Bold: true), set);
        Assert.Equal("14 pt, Bold", set.Describe());
    }

    [Fact]
    public void Placed_markdown_uses_the_strong_and_emphasis_styles()
    {
        var (e, id) = Text("");
        e.Place(id, 0, TextImport.Markdown("Some **bold** and *italic* words"));
        var s = e.Doc.Stories[id];
        Assert.Equal("Strong", s.FormatAt(s.Text.IndexOf("bold", StringComparison.Ordinal)).CharStyle);
        Assert.Equal("Emphasis", s.FormatAt(s.Text.IndexOf("italic", StringComparison.Ordinal)).CharStyle);
    }

    // Keeping together, with the fixed measurer: at size 10 a 100 pt line holds 20 characters, lines are 12 pt apart,
    // and a 30 pt area holds two lines.
    private static readonly ParagraphStyle Plain = new("Body", new CharFormat(Size: 10), new ParaFormat());

    private static Story Make(string text)
    {
        var s = Story.Create("s", Plain);
        s.Insert(0, text);
        return s;
    }

    private static Area[] Columns(int n) => Enumerable.Range(0, n).Select(i => new Area(new Box(i * 200, 0, 100, 30), "f", i)).ToArray();

    /// <summary>Which area each paragraph's lines are in.</summary>
    private static string Where(TextLayout l) => string.Join(" ", l.Lines.Select(x => $"{x.Paragraph}:{x.Area}"));

    private static string Words(int n) => string.Join(" ", Enumerable.Repeat("word", n));

    [Fact]
    public void An_orphan_moves_its_paragraph_on_to_the_next_column()
    {
        var s = Make("one\n" + Words(12)); // one line, then three: the first of them alone at the foot of the column
        Assert.Equal("0:0 1:0 1:1 1:1", Where(TextFlow.Layout(s, Columns(3), M)));
        s.FormatParas(4, 0, p => p with { Orphans = 2 });
        Assert.Equal("0:0 1:1 1:1 1:2", Where(TextFlow.Layout(s, Columns(3), M)));
    }

    [Fact]
    public void A_widow_brings_a_line_over_with_it()
    {
        var s = Make(Words(12)); // three lines in two-line columns: one left alone at the top of the second
        Assert.Equal("0:0 0:0 0:1", Where(TextFlow.Layout(s, Columns(2), M)));
        s.FormatParas(0, 0, p => p with { Widows = 2 });
        Assert.Equal("0:0 0:1 0:1", Where(TextFlow.Layout(s, Columns(2), M)));
        Assert.False(TextFlow.Layout(s, Columns(2), M).Overset);
    }

    [Fact]
    public void A_heading_kept_with_the_next_paragraph_goes_on_with_it()
    {
        var s = Make("first\nHeading\n" + Words(4));
        Assert.Equal("0:0 1:0 2:1", Where(TextFlow.Layout(s, Columns(2), M)));
        s.FormatParas(6, 0, p => p with { KeepWithNext = true });
        Assert.Equal("0:0 1:1 2:1", Where(TextFlow.Layout(s, Columns(2), M)));
    }

    [Fact]
    public void A_paragraph_kept_together_goes_whole_to_the_next_column()
    {
        var s = Make("one\n" + Words(8)); // one line, then two that don't both fit beside it
        Assert.Equal("0:0 1:0 1:1", Where(TextFlow.Layout(s, Columns(2), M)));
        s.FormatParas(4, 0, p => p with { KeepTogether = true });
        Assert.Equal("0:0 1:1 1:1", Where(TextFlow.Layout(s, Columns(2), M)));
    }

    [Fact]
    public void Keeping_never_moves_a_paragraph_already_at_the_top_of_a_column()
    {
        var s = Make(Words(30)); // too long for any column, at the top of the first
        s.FormatParas(0, 0, p => p with { KeepTogether = true, Orphans = 3 });
        Assert.StartsWith("0:0 0:0", Where(TextFlow.Layout(s, Columns(4), M)));
    }

    [Theory]
    [InlineData(1, "I")]
    [InlineData(4, "IV")]
    [InlineData(1994, "MCMXCIV")]
    [InlineData(0, "0")]
    public void Roman_numerals(int n, string roman) => Assert.Equal(roman, PageNumbering.Roman(n));

    [Theory]
    [InlineData(1, "A")]
    [InlineData(26, "Z")]
    [InlineData(27, "AA")]
    [InlineData(53, "BA")]
    public void Letters(int n, string letters) => Assert.Equal(letters, PageNumbering.Letters(n));

    [Fact]
    public void Sections_number_their_pages_from_their_own_start()
    {
        var e = new Editor(Document.New());
        for (var i = 1; i < 8; i++) e.AddPage(i);
        e.SetSection(0, new PageNumbering(1, NumberStyle.LowerRoman));
        e.SetSection(3, new PageNumbering(1));
        e.SetSection(6, new PageNumbering(1, NumberStyle.Arabic, "A-"));
        Assert.Equal(["i", "ii", "iii", "1", "2", "3", "A-1", "A-2"], Enumerable.Range(0, 8).Select(e.Doc.PageLabel));
        Assert.Equal("Page iii of 8", Fields.Expand($"Page {Fields.PageNumber} of {Fields.PageCount}", new FieldContext(3, 8, e.Doc.PageLabel(2))));
        e.SetSection(0, null);
        Assert.Equal("1", e.Doc.PageLabel(0));
    }

    [Fact]
    public void Objects_are_drawn_layer_by_layer()
    {
        var e = new Editor(Document.New());
        var low = e.Add(SheetRef.PageAt(0), new RectangleObject { Name = "low" });
        e.CurrentLayer = e.Doc.Layers[0].Name;
        var top = e.AddLayer("Notes");
        Assert.Equal("Notes", e.CurrentLayer);
        var note = e.Add(SheetRef.PageAt(0), new RectangleObject { Name = "note" });
        e.CurrentLayer = "Layer 1";
        var later = e.Add(SheetRef.PageAt(0), new RectangleObject { Name = "later" });
        Assert.Equal("Notes", e.Doc.Find(note.Id)!.Layer);
        // Added after the note, but on the lower layer, so drawn under it.
        Assert.Equal(["low", "later", "note"], e.Doc.Ordered(e.Doc.Pages[0].Objects).Select(o => o.Name));
        e.MoveLayer("Notes", -1);
        Assert.Equal(["note", "low", "later"], e.Doc.Ordered(e.Doc.Pages[0].Objects).Select(o => o.Name));
        Assert.Null(e.DeleteLayer("Notes"));
        Assert.Equal("Layer 1", e.Doc.Find(note.Id)!.Layer); // onto the layer that was next to it
        Assert.NotNull(e.DeleteLayer("Layer 1"));            // never the last
        Assert.Null(e.RenameLayer("Layer 1", "Artwork"));
        Assert.All(e.Doc.Pages[0].Objects, o => Assert.Equal("Artwork", o.Layer));
        Assert.Equal("Notes", top);
    }

    [Fact]
    public void A_hidden_layer_wraps_no_text()
    {
        var e = new Editor(Document.New());
        var frame = e.Add(SheetRef.PageAt(0), new TextFrame { W = 200, H = 200 });
        e.AddLayer("Art");
        e.Add(SheetRef.PageAt(0), new RectangleObject { X = 0, Y = 0, W = 100, H = 100, Wrap = true });
        Assert.Single(Chains.Areas(e.Doc, e.Doc.Stories[frame.StoryId])[0].Keep);
        e.SetLayer("Art", l => l with { Visible = false });
        Assert.Empty(Chains.Areas(e.Doc, e.Doc.Stories[frame.StoryId])[0].Keep);
    }

    [Fact]
    public void Older_files_get_a_layer_and_the_character_styles()
    {
        var doc = Document.New();
        doc.Layers = [];
        doc.CharStyles = [];
        using var ms = new MemoryStream();
        DocumentFile.Write(doc, ms);
        ms.Position = 0;
        var back = DocumentFile.Read(ms);
        Assert.Single(back.Layers);
        Assert.Contains(back.CharStyles, s => s.Name == "Emphasis");
    }
}
