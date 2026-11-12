using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>Milestone 4: character styles, keeping text together, page-number sections and layers in the panels.</summary>
public partial class WindowTests
{
    [AvaloniaFact]
    public void The_text_tab_applies_character_styles_and_keeps()
    {
        var w = Open();
        Tool(w, AVAPageStream.Tool.Text);
        Drag(w, new Pt(72, 72), new Pt(540, 400));
        TypeLines(w, "A headline", "Body text goes here.");
        var t = w.View.Editing!;
        var id = t.StoryId;
        w.Panel.SelectedIndex = 1;
        Pump();

        t.Anchor = 11;
        t.Caret = 15;
        w.Refresh();
        Assert.Equal("(none)", w.Panel.CharStyleChoice.SelectedItem);
        w.Panel.CharStyleChoice.SelectedItem = "Strong";
        Pump();
        var s = w.Editor.Doc.Stories[id];
        Assert.Equal("Strong", s.FormatAt(12).CharStyle);
        Assert.True(s.FormatAt(12).Bold);
        Assert.False(s.FormatAt(16).Bold);
        w.Refresh();
        Assert.Equal("Strong", w.Panel.CharStyleChoice.SelectedItem);

        // A paragraph style over it keeps it.
        w.Panel.StyleChoice.SelectedItem = "Subheading";
        Pump();
        Assert.Equal("Strong", w.Editor.Doc.Stories[id].FormatAt(12).CharStyle);

        // Keeping, for the paragraph the caret is in.
        t.Anchor = t.Caret = 2;
        w.Refresh();
        Assert.False(w.Panel.KeepWithNextBox.IsChecked);
        w.Panel.KeepWithNextBox.IsChecked = true;
        w.Panel.OrphansBox.Value = 2;
        Pump();
        Assert.True(w.Editor.Doc.Stories[id].Paras[0].KeepWithNext);
        Assert.Equal(2, w.Editor.Doc.Stories[id].Paras[0].Orphans);
        Assert.False(w.Editor.Doc.Stories[id].Paras[1].KeepWithNext);
        w.Undo();
        Assert.Equal(1, w.Editor.Doc.Stories[id].Paras[0].Orphans);
        Done(w);
    }

    [AvaloniaFact]
    public void The_pages_tab_starts_a_section_of_numbering()
    {
        var w = Open();
        w.AddPage(after: true);
        w.AddPage(after: true);
        w.Panel.SelectedIndex = 2;
        Pump();
        Assert.Equal("Page 3 of 3", w.PageInfo.Text);
        Assert.False(w.Panel.SectionStartBox.IsEffectivelyEnabled); // until the page starts a section
        w.Panel.SectionBox.IsChecked = true;
        Pump();
        Assert.Equal(new PageNumbering(1), w.Editor.Doc.Pages[2].Section);
        w.Panel.SectionStyleChoice.SelectedIndex = 2; // I, II, III
        Pump();
        Assert.Equal(new PageNumbering(1, NumberStyle.UpperRoman), w.Editor.Doc.Pages[2].Section);
        Assert.Equal("Page 3 (I) of 3", w.PageInfo.Text);
        w.Panel.SectionStartBox.Value = 4;
        Pump();
        Assert.Equal("IV", w.Editor.Doc.PageLabel(2));
        w.Panel.SectionBox.IsChecked = false;
        Pump();
        Assert.Null(w.Editor.Doc.Pages[2].Section);
        Assert.Equal("Page 3 of 3", w.PageInfo.Text);
        Done(w);
    }

    [AvaloniaFact]
    public void Layers_are_picked_hidden_and_locked_in_the_pages_tab()
    {
        var w = Open();
        w.View.Snap = false;
        w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { Name = "art", X = 100, Y = 100, W = 100, H = 100, Fill = "#FF0000" });
        w.Editor.AddLayer("Notes");
        w.Panel.SelectedIndex = 2;
        w.Refresh();
        Pump();
        var shown = ((IEnumerable<Layer>)w.Panel.LayerList.ItemsSource!).Select(l => l.Name);
        Assert.Equal(["Notes", "Layer 1"], shown); // top first
        Assert.Equal("Notes", ((Layer)w.Panel.LayerList.SelectedItem!).Name);

        w.Panel.LayerList.SelectedIndex = 1;
        Pump();
        Assert.Equal("Layer 1", w.Editor.CurrentLayer);

        // Locked: the rectangle can't be picked; hidden, nor seen.
        w.Editor.SetLayer("Layer 1", l => l with { Locked = true });
        Tool(w, AVAPageStream.Tool.Select);
        Click(w, new Pt(150, 150));
        Assert.Empty(w.View.SelectedIds);
        w.Editor.SetLayer("Layer 1", l => l with { Locked = false });
        Click(w, new Pt(150, 150));
        Assert.Single(w.View.SelectedIds);
        Done(w);
    }
}
