using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>Milestone 3: spelling.</summary>
public partial class WindowTests
{
    [AvaloniaFact]
    public void The_spelling_window_steps_through_misspelt_words_and_changes_them()
    {
        var w = Open();
        var frame = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 400, H = 200 });
        w.Editor.ReplaceText(frame.StoryId, 0, 0, "Teh lamps on teh wall, and Kerrith quay, and the wierd steps.");
        Assert.Equal(4, w.View.MisspeltIn(frame.StoryId).Count); // Teh, teh, Kerrith, wierd
        Shot(w, "spelling");

        Press(w, Key.F7);
        var sw = Assert.IsType<SpellingWindow>(Assert.Single(w.OwnedWindows));
        Assert.Equal("Teh", sw.Word.Text);
        Assert.Equal("The", sw.ChangeTo.Text);              // the best suggestion, capitalised like the word
        Assert.Equal((0, 3), (w.View.Editing!.Start, w.View.Editing.Length)); // shown selected on the page

        sw.Change();
        Assert.StartsWith("The lamps", w.Editor.Doc.Stories[frame.StoryId].Text);
        Assert.Equal("teh", sw.Word.Text);
        sw.ChangeTo.Text = "the";
        sw.Change();
        Assert.Equal("Kerrith", sw.Word.Text);

        sw.Add(); // a place name: into the user's dictionary
        Assert.Contains("Kerrith", File.ReadAllLines(w.UserWordsPath));
        Assert.Equal("wierd", sw.Word.Text);
        sw.IgnoreAll();
        Assert.Contains("wierd", w.Editor.Doc.IgnoredWords);
        Assert.Equal("No misspelt words", sw.Word.Text);
        Assert.Empty(w.View.MisspeltIn(frame.StoryId));
        sw.Close();
        Done(w);
    }

    [AvaloniaFact]
    public void The_documents_language_sets_which_spellings_are_right()
    {
        var w = Open();
        var frame = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 400, H = 100 });
        w.Editor.ReplaceText(frame.StoryId, 0, 0, "The colour of the harbour");
        Assert.Equal(2, w.View.MisspeltIn(frame.StoryId).Count);
        w.Panel.SelectedIndex = 3;
        Pump();
        w.Panel.LanguageChoice.SelectedIndex = 1; // British
        Pump();
        Assert.Equal("en-GB", w.Editor.Doc.Language);
        Assert.Empty(w.View.MisspeltIn(frame.StoryId));
        Done(w);
    }
}
