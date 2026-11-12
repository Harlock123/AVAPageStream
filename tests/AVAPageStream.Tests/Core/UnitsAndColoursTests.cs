using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

public class UnitsAndColoursTests
{
    [Theory]
    [InlineData("54", Unit.Point, 54)]
    [InlineData("1", Unit.Inch, 72)]
    [InlineData("3in", Unit.Point, 216)]
    [InlineData("25.4 mm", Unit.Point, 72)]
    [InlineData("2.54cm", Unit.Point, 72)]
    [InlineData("12p6", Unit.Point, 150)]
    [InlineData("4p", Unit.Inch, 48)]
    [InlineData("p6", Unit.Point, double.NaN)]
    [InlineData("1in + 6pt", Unit.Point, 78)]
    [InlineData("2in-0.25in", Unit.Point, 126)]
    [InlineData("-18", Unit.Point, -18)]
    [InlineData(".5in", Unit.Point, 36)]
    [InlineData("1\"", Unit.Point, 72)]
    [InlineData("abc", Unit.Point, double.NaN)]
    [InlineData("12 12", Unit.Point, double.NaN)]
    [InlineData("", Unit.Point, double.NaN)]
    public void Measures_are_read_in_any_unit(string text, Unit current, double expected)
    {
        var ok = Units.TryParse(text, current, out var points);
        if (double.IsNaN(expected)) Assert.False(ok);
        else
        {
            Assert.True(ok);
            Assert.Equal(expected, points, 6);
        }
    }

    [Theory]
    [InlineData(54, Unit.Point, "54 pt")]
    [InlineData(54, Unit.Inch, "0.75 in")]
    [InlineData(72, Unit.Millimetre, "25.4 mm")]
    [InlineData(54, Unit.Pica, "4p6")]
    [InlineData(48, Unit.Pica, "4p0")]
    [InlineData(1.5, Unit.Point, "1.5 pt")]
    public void Measures_are_shown_rounded(double points, Unit unit, string expected) =>
        Assert.Equal(expected, Units.Format(points, unit));

    [Fact]
    public void Formatted_measures_read_back_the_same()
    {
        foreach (var unit in Units.All)
            foreach (var v in new[] { 0, 18, 54.5, 612, 841.89 })
            {
                Assert.True(Units.TryParse(Units.Format(v, unit), Unit.Point, out var back));
                Assert.Equal(v, back, 1);
            }
    }

    [Fact]
    public void Colours_are_read_as_hex_names_or_none()
    {
        var swatches = Colours.DefaultSwatches();
        Assert.True(Colours.TryRead("#ff0000", swatches, out var red));
        Assert.Equal("#FF0000", red);
        Assert.True(Colours.TryRead("80FF0000", swatches, out var clear));
        Assert.Equal("#80FF0000", clear);
        Assert.True(Colours.TryRead("blue", swatches, out var blue));
        Assert.Equal("@Blue", blue);                        // a swatch's name refers to the swatch
        Assert.Equal("#2050D0", Colours.Resolve(blue, swatches));
        Assert.True(Colours.TryRead("none", swatches, out var none));
        Assert.Null(none);
        Assert.False(Colours.TryRead("#12345", swatches, out _));
        Assert.Equal("Blue", Colours.Describe("@Blue", swatches));
        Assert.Equal("#2050D0", Colours.Describe("#2050D0", swatches)); // a value is a value, even one a swatch has
        Assert.Null(Colours.Resolve("@Gone", swatches));
        Assert.Equal("#123456", Colours.Describe("#123456", swatches));
        Assert.Equal("none", Colours.Describe(null, swatches));
    }
}
