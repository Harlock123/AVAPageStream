using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>Milestone 2: styled copy and paste, the story editor, kerning keys.</summary>
public partial class WindowTests
{
    [AvaloniaFact]
    public void Text_copied_and_pasted_here_keeps_its_formats()
    {
        var w = Open();
        var a = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 300, H = 100 });
        w.Editor.ReplaceText(a.StoryId, 0, 0, "plain BOLD plain");
        w.Editor.FormatChars(a.StoryId, 6, 4, f => f with { Bold = true, Colour = "@Red" });
        var b = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 300, W = 300, H = 100 });
        w.Editor.ReplaceText(b.StoryId, 0, 0, "into: ");

        w.View.StartEditing((TextFrame)w.Editor.Doc.Find(a.Id)!);
        w.View.Editing!.Anchor = 6;
        w.View.Editing!.Caret = 10;
        Wait(w.Copy());
        w.View.StartEditing((TextFrame)w.Editor.Doc.Find(b.Id)!);
        w.View.Editing!.Caret = w.View.Editing.Anchor = 6;
        Wait(w.Paste());
        var story = w.Editor.Doc.Stories[b.StoryId];
        Assert.Equal("into: BOLD", story.Text);
        Assert.True(story.FormatAt(7).Bold);
        Assert.Equal("@Red", story.FormatAt(7).Colour);
        Assert.Equal(10, w.View.Editing!.Caret);
        Done(w);
    }

    [AvaloniaFact]
    public void The_story_editor_and_the_page_change_each_other()
    {
        var w = Open();
        var frame = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 300, H = 200 });
        w.Editor.ReplaceText(frame.StoryId, 0, 0, "Headline\nThe body text.");
        w.Editor.ApplyStyle(frame.StoryId, 0, 0, "Heading");
        w.Editor.FormatChars(frame.StoryId, 13, 4, f => f with { Italic = true }); // "body"
        w.View.Select([frame.Id]);
        Press(w, Key.E, RawInputModifiers.Control | RawInputModifiers.Shift);
        var se = Assert.IsType<StoryEditor>(Assert.Single(w.OwnedWindows));
        Assert.Equal("Headline\nThe body text.", se.Text.Text);
        Assert.Equal("Story: Headline", se.Title);
        Assert.Contains("4 words · 2 paragraphs · 1 frame", se.Info.Text);

        // Typing there: only the changed stretch is replaced, so the italic and the heading stay.
        se.Text.Text = "Headline\nThe body text, edited.";
        Pump();
        var story = w.Editor.Doc.Stories[frame.StoryId];
        Assert.Equal("Headline\nThe body text, edited.", story.Text);
        Assert.True(story.FormatAt(13).Italic);
        Assert.Equal("Heading", story.Paras[0].Style);

        // Changes on the page show there.
        w.Editor.ReplaceText(frame.StoryId, 0, 0, "New ");
        Pump();
        Assert.Equal("New Headline\nThe body text, edited.", se.Text.Text);

        // The caret's paragraph's style, shown and set.
        se.Text.CaretIndex = 20;
        Pump();
        Assert.Equal("Body", se.StyleChoice.SelectedItem);
        se.StyleChoice.SelectedItem = "Subheading";
        Pump();
        Assert.Equal("Subheading", w.Editor.Doc.Stories[frame.StoryId].Paras[1].Style);

        // Undo reaches it too, and a deleted story closes its window.
        w.Undo();
        Pump();
        w.Editor.Delete([frame.Id]);
        Pump();
        Assert.Empty(w.OwnedWindows);
        Done(w);
    }

    [AvaloniaFact]
    public void Alt_arrows_kern_the_pair_before_the_caret()
    {
        var w = Open();
        Tool(w, AVAPageStream.Tool.Text);
        Drag(w, new Pt(72, 72), new Pt(372, 172));
        Type(w, "AV");
        Press(w, Key.Left);
        Press(w, Key.Left, RawInputModifiers.Alt);
        Press(w, Key.Left, RawInputModifiers.Alt);
        Press(w, Key.Right, RawInputModifiers.Alt | RawInputModifiers.Shift);
        var story = w.Editor.Doc.Stories[w.View.Editing!.StoryId];
        Assert.Equal(30, story.FormatAt(0).Tracking); // -10 -10 +50
        Assert.Equal(0, story.FormatAt(1).Tracking);
        w.Undo(); // one step for the run of kerning
        Assert.Equal(0, w.Editor.Doc.Stories[w.View.Editing!.StoryId].FormatAt(0).Tracking);
        Done(w);
    }

    [AvaloniaFact]
    public void Lines_get_arrowheads_from_the_object_tab()
    {
        var w = Open();
        var line = w.Editor.Add(SheetRef.PageAt(0), new LineObject { X = 100, Y = 100, W = 200, H = 0, StrokeWidth = 2 });
        w.View.Select([line.Id]);
        Pump();
        w.Panel.ArrowEndBox.IsChecked = true;
        Pump();
        Assert.True(((LineObject)w.Editor.Doc.Find(line.Id)!).ArrowEnd);
        Assert.False(((LineObject)w.Editor.Doc.Find(line.Id)!).ArrowStart);
        Shot(w, "arrow");
        Done(w);
    }
}
