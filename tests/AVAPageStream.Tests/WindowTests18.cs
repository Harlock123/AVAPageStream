using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>Milestone 7, part 2: paragraph rules, object styles, text to outlines and placed PDF pages.</summary>
public partial class WindowTests
{
    [AvaloniaFact]
    public void Paragraph_rules_are_drawn_above_and_below()
    {
        var w = Open();
        var e = w.Editor;
        var f = e.Add(SheetRef.PageAt(0), new TextFrame { X = 50, Y = 50, W = 300, H = 200 });
        e.ReplaceText(f.StoryId, 0, 0, "Heading\nBody text.");
        e.FormatParas(f.StoryId, 0, 0, p => p with
        {
            RuleAbove = new ParaRule(4, "@Red", RuleLength.Column, Offset: 4),
            RuleBelow = new ParaRule(2, "@Blue", RuleLength.Width, Width: 100, Offset: 2),
            SpaceBefore = 12, SpaceAfter = 10,
        });
        var layout = w.View.Layouts.Of(e.Doc, f.StoryId, 0)!;
        var first = layout.Lines[0];
        using var bitmap = DrawPage(e.Doc);
        var above = (int)Math.Round(first.Baseline - first.Ascent - 4 - 2);
        var below = (int)Math.Round(first.Baseline + first.Descent + 2 + 1);
        var red = Pixel(bitmap, 340, above);                     // the column-long rule reaches near the frame's right
        Assert.True(red.R > 180 && red.G < 80, $"{red}");
        var blue = Pixel(bitmap, 140, below);                    // the 100 pt rule
        Assert.True(blue.B > 150 && blue.B - blue.R > 100, $"{blue}");
        Assert.Equal((255, 255, 255), Pixel(bitmap, 160, below)); // and no further
        // Renaming the swatch keeps the rule's colour, by its new name.
        Assert.Null(e.RenameSwatch("Red", "Signal"));
        Assert.Equal("@Signal", e.Doc.Stories[f.StoryId].Paras[0].RuleAbove!.Colour);
        Assert.Contains("@Signal", Colours.Used(e.Doc));
        Done(w);
    }

    [AvaloniaFact]
    public void The_text_tab_sets_paragraph_rules_and_styles_keep_them()
    {
        var w = Open();
        var f = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 300, H = 200 });
        w.Editor.ReplaceText(f.StoryId, 0, 0, "Section\nText.");
        w.View.StartEditing((TextFrame)w.Editor.Doc.Find(f.Id)!);
        w.View.Editing!.Caret = w.View.Editing.Anchor = 2;
        w.Panel.SelectedIndex = 1;
        w.Refresh();
        Pump();
        void Commit(Avalonia.Controls.TextBox box, string text) { box.Focus(); box.Text = text; Press(w, Key.Enter); }
        var rc = w.Panel.RuleBelowControls;
        Assert.False(rc.Weight.IsEnabled);
        rc.Choice.SelectedIndex = 2;            // as long as the text
        Pump();
        Commit(rc.Weight, "3pt");
        Commit(rc.Colour, "Green");
        Commit(rc.Offset, "5pt");
        var para = w.Editor.Doc.Stories[f.StoryId].Paras[0];
        Assert.Equal(new ParaRule(3, "@Green", RuleLength.Text, 72, 5), para.RuleBelow);
        Assert.Null(para.RuleAbove);
        Assert.Null(w.Editor.Doc.Stories[f.StoryId].Paras[1].RuleBelow); // only the caret's paragraph
        Assert.True(rc.Weight.IsEnabled);
        Assert.False(rc.Width.IsEnabled);
        // A style made from it has the rule, and gives it to the paragraphs it is applied to.
        w.Panel.StyleNameBox.Text = "Ruled";
        w.Panel.NewStyle();
        Assert.Equal(para.RuleBelow, w.Editor.Doc.Style("Ruled").Para.RuleBelow);
        w.Editor.ApplyStyle(f.StoryId, 9, 0, "Ruled");
        Assert.Equal(para.RuleBelow, w.Editor.Doc.Stories[f.StoryId].Paras[1].RuleBelow);
        // The document keeps it.
        var copy = Document.FromJson(w.Editor.Doc.ToJson(), w.Editor.Doc.Images);
        Assert.Equal(para.RuleBelow, copy.Stories[f.StoryId].Paras[0].RuleBelow);
        rc.Choice.SelectedIndex = 0;
        Pump();
        Assert.Null(w.Editor.Doc.Stories[f.StoryId].Paras[0].RuleBelow);
        Done(w);
    }
}
