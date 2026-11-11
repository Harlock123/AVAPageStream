namespace AVAPageStream.Core;

/// <summary>Stories and the frames they flow through: the areas text fills, and linking frames together.</summary>
public static class Chains
{
    /// <summary>The frames a story flows through, in order (frames that no longer exist are left out).</summary>
    public static List<TextFrame> FramesOf(Document doc, Story story) =>
        story.Frames.Select(id => doc.Find(id)).OfType<TextFrame>().ToList();

    /// <summary>Every column of every frame in the story's chain, in the order text fills them.</summary>
    public static List<Area> Areas(Document doc, Story story) =>
        FramesOf(doc, story).SelectMany(f => f.ColumnBoxes().Select((b, i) => new Area(b, f.Id, i))).ToList();

    /// <summary>
    /// Makes <paramref name="to"/> follow <paramref name="from"/> in its story's chain. The frame joining must hold
    /// no text of its own (an empty frame, or one whose text has nowhere else to go is refused). Returns why not, or null.
    /// </summary>
    public static string? Link(Document doc, TextFrame from, TextFrame to)
    {
        if (from.Id == to.Id) return "A frame can't follow itself.";
        if (doc.StoryOf(from) is not { } story) return "That frame has no story.";
        if (to.StoryId == from.StoryId) return "Those frames are already in the same chain.";
        if (doc.StoryOf(to) is { } other)
        {
            if (other.Length > 0 && other.Frames.Count <= 1)
                return "The second frame already holds text. Clear it, or link from it instead.";
            other.Frames.Remove(to.Id);
            if (other.Frames.Count == 0) doc.Stories.Remove(other.Id);
        }
        to.StoryId = story.Id;
        story.Frames.Remove(to.Id);
        story.Frames.Insert(story.Frames.IndexOf(from.Id) + 1, to.Id);
        return null;
    }

    /// <summary>Takes a frame out of its chain and gives it a new empty story; the text flows on through the rest.</summary>
    public static string? Unlink(Document doc, TextFrame frame)
    {
        if (doc.StoryOf(frame) is not { } story || story.Frames.Count <= 1) return "That frame isn't linked to another.";
        story.Frames.Remove(frame.Id);
        NewStory(doc, frame, doc.Style(story.Paras[0].Style));
        return null;
    }

    /// <summary>Gives a frame a new, empty story of its own in a style.</summary>
    public static Story NewStory(Document doc, TextFrame frame, ParagraphStyle style)
    {
        var story = Story.Create(doc.NewId("story"), style);
        story.Frames.Add(frame.Id);
        doc.Stories[story.Id] = story;
        frame.StoryId = story.Id;
        return story;
    }

    /// <summary>Takes a frame that is being deleted out of its chain; a story left with no frames goes too.</summary>
    public static void Forget(Document doc, TextFrame frame)
    {
        if (doc.StoryOf(frame) is not { } story) return;
        story.Frames.Remove(frame.Id);
        if (story.Frames.Count == 0) doc.Stories.Remove(story.Id);
    }
}
