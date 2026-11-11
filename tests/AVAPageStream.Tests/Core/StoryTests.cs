using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

public class StoryTests
{
    private static readonly ParagraphStyle Body = Document.DefaultStyles()[0];
    private static readonly ParagraphStyle Heading = Document.DefaultStyles()[1];

    private static Story Make(string text)
    {
        var s = Story.Create("s", Body);
        s.Insert(0, text);
        return s;
    }

    [Fact]
    public void An_empty_story_has_one_paragraph_and_a_format()
    {
        var s = Story.Create("s", Body);
        Assert.True(s.IsWellFormed());
        Assert.Equal(1, s.ParagraphCount);
        Assert.Equal(Body.Char, s.TypingFormat(0));
    }

    [Fact]
    public void Inserting_new_lines_makes_paragraphs_that_copy_the_format()
    {
        var s = Make("one two");
        s.FormatParas(0, 0, p => p with { Align = Align.Centre });
        s.Insert(3, "\n");
        Assert.Equal("one\n two", s.Text);
        Assert.Equal(2, s.ParagraphCount);
        Assert.All(s.Paras, p => Assert.Equal(Align.Centre, p.Align));
        Assert.Equal((4, 8), s.ParagraphRange(1));
        Assert.True(s.IsWellFormed());
    }

    [Fact]
    public void Carriage_returns_become_new_lines()
    {
        var s = Make("a\r\nb\rc");
        Assert.Equal("a\nb\nc", s.Text);
        Assert.Equal(3, s.ParagraphCount);
    }

    [Fact]
    public void Formatting_splits_runs_and_joining_merges_them_back()
    {
        var s = Make("hello world");
        s.FormatChars(6, 5, f => f with { Bold = true });
        Assert.Equal(3, s.Runs.Count); // "hello ", "world", the end
        Assert.True(s.FormatAt(6).Bold);
        Assert.False(s.FormatAt(5).Bold);
        s.FormatChars(0, 11, f => f with { Bold = false });
        Assert.Single(s.Runs);
        Assert.True(s.IsWellFormed());
    }

    [Fact]
    public void Typing_takes_the_format_before_the_caret_or_after_at_a_paragraph_start()
    {
        var s = Make("ab\ncd");
        s.FormatChars(1, 1, f => f with { Italic = true });
        s.FormatChars(3, 1, f => f with { Bold = true });
        Assert.True(s.TypingFormat(2).Italic); // after the italic b
        Assert.True(s.TypingFormat(3).Bold);   // the start of "cd": the bold c
        s.Insert(2, "X");
        Assert.True(s.FormatAt(2).Italic);
        Assert.True(s.IsWellFormed());
    }

    [Fact]
    public void Deleting_across_paragraphs_keeps_the_first_paragraphs_format()
    {
        var s = Make("one\ntwo\nthree");
        s.FormatParas(0, 0, p => p with { Align = Align.Right });
        s.Delete(2, 7); // "e\ntwo\nt"
        Assert.Equal("onhree", s.Text);
        Assert.Equal(1, s.ParagraphCount);
        Assert.Equal(Align.Right, s.Paras[0].Align);
        Assert.True(s.IsWellFormed());
    }

    [Fact]
    public void Deleting_everything_keeps_a_format_to_type_in()
    {
        var s = Make("bold");
        s.FormatChars(0, 5, f => f with { Bold = true });
        s.Delete(0, 4);
        Assert.Equal("", s.Text);
        Assert.True(s.TypingFormat(0).Bold);
        Assert.True(s.IsWellFormed());
    }

    [Fact]
    public void Applying_a_style_sets_paragraphs_and_their_characters()
    {
        var s = Make("Title\nBody text");
        s.ApplyStyle(2, 0, Heading);
        Assert.Equal("Heading", s.Paras[0].Style);
        Assert.Equal("Body", s.Paras[1].Style);
        Assert.Equal(Heading.Char, s.FormatAt(0));
        Assert.Equal(Heading.Char, s.FormatAt(5)); // its end too, so an emptied heading stays one
        Assert.Equal(Body.Char, s.FormatAt(6));
    }

    [Fact]
    public void Redefining_a_style_updates_its_paragraphs_but_keeps_local_bold()
    {
        var s = Make("one\ntwo");
        s.FormatChars(0, 1, f => f with { Bold = true });
        var bigger = Body with { Char = Body.Char with { Size = 14 }, Para = Body.Para with { Align = Align.Justify } };
        s.Restyle(bigger);
        Assert.Equal(14, s.FormatAt(0).Size);
        Assert.True(s.FormatAt(0).Bold);
        Assert.False(s.FormatAt(1).Bold);
        Assert.All(s.Paras, p => Assert.Equal(Align.Justify, p.Align));
    }
}
