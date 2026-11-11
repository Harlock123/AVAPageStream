using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

/// <summary>Every character half its size wide (bold 0.6), ascent 0.8 and descent 0.2 of the size: easy sums for layout tests.</summary>
internal sealed class FixedMeasurer : ITextMeasurer
{
    public double Width(string text, CharFormat format) => text.Length * format.Size * (format.Bold ? 0.6 : 0.5);

    public FontMetrics Metrics(CharFormat format) => new(format.Size * 0.8, format.Size * 0.2);
}
