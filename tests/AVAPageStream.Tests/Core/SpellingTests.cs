using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

public class SpellingTests
{
    private static readonly Speller Us = Speller.For("en-US");
    private static readonly Speller Gb = Speller.For("en-GB");

    [Theory]
    [InlineData("harbor", true)]
    [InlineData("Harbor", true)]       // capitalised at the start of a sentence
    [InlineData("HARBOR", true)]       // in capitals
    [InlineData("hArbor", false)]
    [InlineData("harbour", false)]     // British
    [InlineData("masons'", true)]      // the apostrophe round it doesn't count
    [InlineData("don’t", true)]        // a curly apostrophe is a straight one
    [InlineData("well-known", true)]   // each part of a hyphenated word
    [InlineData("well-knwon", false)]
    [InlineData("1990s", true)]        // words with digits pass
    [InlineData("teh", false)]
    [InlineData("London", true)]
    [InlineData("london", false)]      // a name keeps its capital
    public void American_words_are_checked_with_english_case_rules(string word, bool right) =>
        Assert.Equal(right, Us.Check(word));

    [Fact]
    public void British_has_its_own_spellings()
    {
        Assert.True(Gb.Check("harbour"));
        Assert.True(Gb.Check("colour"));
        Assert.False(Us.Check("colour"));
        Assert.Equal("en-US", Speller.Languages[0].Code);
        Assert.Same(Us, Speller.For("xx-XX")); // American for a language it doesn't have
    }

    [Fact]
    public void Suggestions_are_near_words_in_the_words_case()
    {
        Assert.Contains("the", Us.Suggest("teh"));
        Assert.Contains("harbor", Us.Suggest("harbr"));
        Assert.Equal("Harbor", Us.Suggest("Harbr").First(s => s.StartsWith("Harb")));
        Assert.Contains("RECEIVE", Us.Suggest("RECIEVE"));
        Assert.Contains("necessary", Us.Suggest("neccessary"));
        Assert.True(Us.Suggest("qzxwv").Count <= 6);
    }

    [Fact]
    public void Words_and_their_places_are_picked_out_of_text()
    {
        // An apostrophe ends a word unless a letter follows it.
        Assert.Equal([(0, "The"), (4, "masons"), (12, "marks"), (19, "well-known"), (31, "don't")],
            Speller.Words("The masons' marks, well-known; don't").ToList());
    }

    [Fact]
    public void The_next_misspelt_word_is_found_from_the_caret_on_and_round()
    {
        var e = new Editor(Document.New());
        var a = e.Add(SheetRef.PageAt(0), new TextFrame { W = 200, H = 100 });
        e.ReplaceText(a.StoryId, 0, 0, "Teh harbor has wierd lamps");
        var b = e.Add(SheetRef.PageAt(0), new TextFrame { Y = 200, W = 200, H = 100 });
        e.ReplaceText(b.StoryId, 0, 0, "Fine words and a mispeling");
        Assert.Equal(new Match(a.StoryId, 0, 3), Us.Next(e.Doc, null, null, 0));
        Assert.Equal(new Match(a.StoryId, 15, 5), Us.Next(e.Doc, null, a.StoryId, 3));
        Assert.Equal(new Match(b.StoryId, 17, 9), Us.Next(e.Doc, null, a.StoryId, 20));
        Assert.Equal(new Match(a.StoryId, 0, 3), Us.Next(e.Doc, null, b.StoryId, 26)); // round to the start
        Assert.Equal(new Match(b.StoryId, 17, 9), Us.Next(e.Doc, new HashSet<string> { "Teh", "wierd" }, null, 0));
        e.ReplaceText(b.StoryId, 17, 9, "misspelling");
        e.ReplaceText(a.StoryId, 0, 26, "All right");
        Assert.Null(Us.Next(e.Doc, null, null, 0));
    }
}
