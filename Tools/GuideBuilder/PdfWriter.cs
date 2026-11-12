using SkiaSharp;

namespace AVAPageStream.Guide;

/// <summary>
/// Lays the guide out on Letter paper and writes it as a PDF.
///
/// A very small typesetter: one column, a cursor down the page, and a new page whenever the
/// next thing will not fit. It knows nothing about widows, hyphenation or floating figures,
/// because a guide of this size does not need it to.
/// </summary>
public sealed class PdfWriter(string folder, string version)
{
    private const float PageWidth = 612;
    private const float PageHeight = 792;
    private const float Margin = 56;
    private const float Body = 10.5f;
    private const float Leading = 1.5f;

    private static readonly SKColor Ink = new(0x1A, 0x1A, 0x1A);
    private static readonly SKColor Quiet = new(0x70, 0x70, 0x70);
    private static readonly SKColor Rule = new(0xD0, 0xD4, 0xDA);
    private static readonly SKColor Accent = new(0x2A, 0x6F, 0xD6);
    private static readonly SKColor CodeBack = new(0xF2, 0xF4, 0xF7);

    private readonly SKTypeface _text = Face("DejaVu Sans", SKFontStyle.Normal);
    private readonly SKTypeface _bold = Face("DejaVu Sans", SKFontStyle.Bold);
    private readonly SKTypeface _italic = Face("DejaVu Sans", SKFontStyle.Italic);
    private readonly SKTypeface _boldItalic = Face("DejaVu Sans", SKFontStyle.BoldItalic);
    private readonly SKTypeface _mono = Face("DejaVu Sans Mono", SKFontStyle.Normal);

    private SKDocument _pdf = null!;
    private SKCanvas _canvas = null!;
    private float _y;
    private int _page;

    private static float Right => PageWidth - Margin;
    private static float Width => PageWidth - Margin * 2;
    private static float Bottom => PageHeight - Margin - 18;

    private static SKTypeface Face(string family, SKFontStyle style) =>
        SKTypeface.FromFamilyName(family, style) ?? SKTypeface.Default;

    public void Write(IReadOnlyList<Block> blocks, Stream stream)
    {
        _pdf = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata
        {
            Title = "AVAPageStream User Guide",
            Author = "AVAPageStream",
            Creator = "AVAPageStream",

            // Said out loud because a fresh SKDocumentPdfMetadata leaves this at zero, and
            // zero is a real JPEG quality rather than a "not set" - every screenshot in the
            // guide came out as blotches with the colour boiled out of it. 90 is the usual
            // photographic setting and costs the guide a couple of megabytes.
            EncodingQuality = 90
        }) ?? throw new IOException("Skia would not open a PDF document.");

        Cover(blocks);
        NewPage();

        // The document's own title is already on the cover; repeating it would give the first
        // page one line and nothing else.
        var body = blocks.Count > 0 && blocks[0] is Heading { Level: 1 }
            ? blocks.Skip(1)
            : blocks;

        foreach (var block in body)
            Lay(block);

        EndPage();
        _pdf.Close();
    }

    #region The page

    private void NewPage()
    {
        EndPage();

        _canvas = _pdf.BeginPage(PageWidth, PageHeight);
        _page++;
        _y = Margin;
    }

    private void EndPage()
    {
        if (_canvas is null)
            return;

        // A running foot, on every page but the cover.
        if (_page > 0)
        {
            using var paint = Paint(_text, 8.5f, Quiet);
            var label = $"AVAPageStream User Guide";

            _canvas.DrawText(label, Margin, PageHeight - Margin + 12, paint);

            var number = _page.ToString();
            _canvas.DrawText(number, Right - paint.MeasureText(number), PageHeight - Margin + 12, paint);
        }

        _pdf.EndPage();
        _canvas = null!;
    }

    /// <summary>Room for something this tall, or a fresh page to put it on.</summary>
    private void Room(float height)
    {
        if (_y + height > Bottom)
            NewPage();
    }

    private static SKPaint Paint(SKTypeface face, float size, SKColor colour) => new()
    {
        Typeface = face,
        TextSize = size,
        Color = colour,
        IsAntialias = true,
        SubpixelText = true
    };

    #endregion

    #region The cover

    private void Cover(IReadOnlyList<Block> blocks)
    {
        _canvas = _pdf.BeginPage(PageWidth, PageHeight);
        _page = 0;

        using var title = Paint(_bold, 34, Ink);
        using var subtitle = Paint(_text, 13, Quiet);
        using var small = Paint(_text, 10, Quiet);

        _canvas.DrawText("AVAPageStream", Margin, 250, title);

        using var line = new SKPaint { Color = Accent, StrokeWidth = 3, IsAntialias = true };
        _canvas.DrawLine(Margin, 268, Margin + 120, 268, line);

        _canvas.DrawText("User Guide", Margin, 302, subtitle);

        var note = string.IsNullOrWhiteSpace(version)
            ? "A desktop publishing program"
            : $"A desktop publishing program  ·  version {version}";

        _canvas.DrawText(note, Margin, 324, small);

        // A picture of the thing itself, if it is where it should be. Kept short enough to
        // leave the contents room beneath it.
        var shot = Path.Combine(folder, "docs", "images", "guide", "overview.png");
        var below = 400f;

        if (File.Exists(shot))
        {
            using var bitmap = SKBitmap.Decode(shot);

            if (bitmap is not null)
            {
                var height = Math.Min(210f, Width * bitmap.Height / bitmap.Width);
                var width = height * bitmap.Width / bitmap.Height;
                var box = SKRect.Create(Margin + (Width - width) / 2, 356, width, height);

                _canvas.DrawBitmap(bitmap, box);
                Frame(box);

                below = box.Bottom + 46;
            }
        }

        // The contents, so a reader can find a section without turning every page.
        var sections = blocks.OfType<Heading>().Where(h => h.Level == 2).ToList();

        if (sections.Count > 0)
        {
            using var heading = Paint(_bold, 10, Ink);
            using var entry = Paint(_text, 10, Quiet);

            var y = below;
            _canvas.DrawText("Contents", Margin, y, heading);
            y += 18;

            // Two columns, because there are more sections than fit down one.
            var half = (sections.Count + 1) / 2;

            for (var i = 0; i < sections.Count; i++)
            {
                var text = Flatten(sections[i].Text);
                var x = i < half ? Margin : Margin + Width / 2;
                var at = y + (i % half) * 14;

                _canvas.DrawText(text, x, at, entry);
            }
        }
    }

    private static string Flatten(IReadOnlyList<Run> runs) =>
        string.Concat(runs.Select(run => run.Text));

    #endregion

    #region Blocks

    private void Lay(Block block)
    {
        switch (block)
        {
            case Heading heading:
                LayHeading(heading);
                break;

            case Paragraph paragraph:
                _y += 4;
                Flow(paragraph.Text, Margin, Width, Body, _text);
                _y += 4;
                break;

            case Bullet bullet:
                LayBullet(bullet);
                break;

            case Picture picture:
                LayPicture(picture);
                break;

            case Table table:
                LayTable(table);
                break;

            case Code code:
                LayCode(code);
                break;
        }
    }

    private void LayHeading(Heading heading)
    {
        var size = heading.Level switch { 1 => 20f, 2 => 15f, _ => 11.5f };

        // Every section starts a page of its own, which is what makes it a guide you can
        // leaf through rather than a wall you have to read.
        if (heading.Level <= 2 && _y > Margin + 1)
            NewPage();
        else
            Room(size * 3);

        _y += heading.Level <= 2 ? 2 : 12;

        Flow(heading.Text, Margin, Width, size, _bold, heading.Level <= 2 ? Ink : Accent);

        if (heading.Level <= 2)
        {
            using var paint = new SKPaint { Color = Rule, StrokeWidth = 0.8f, IsAntialias = true };
            _y += 2;
            _canvas.DrawLine(Margin, _y, Right, _y, paint);
        }

        _y += heading.Level <= 2 ? 12 : 3;
    }

    private void LayBullet(Bullet bullet)
    {
        const float indent = 16;

        Room(Body * Leading);

        using var dot = Paint(bullet.Number is null ? _text : _bold, Body, Accent);
        _canvas.DrawText(bullet.Number ?? "•", Margin + (bullet.Number is null ? 3 : 0), _y + Body, dot);

        Flow(bullet.Text, Margin + indent, Width - indent, Body, _text);
        _y += 3;
    }

    private void LayPicture(Picture picture)
    {
        var path = Path.Combine(folder, picture.Path.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"  missing picture: {picture.Path}");
            return;
        }

        using var bitmap = SKBitmap.Decode(path);

        if (bitmap is null)
            return;

        // Never wider than the column, and never so tall it cannot share a page with a
        // paragraph - a screenshot that fills a page on its own tells the reader nothing
        // about where it belongs.
        var width = Math.Min(Width, bitmap.Width * 0.5f);
        var height = width * bitmap.Height / bitmap.Width;
        var most = Bottom - Margin - 60;

        if (height > most)
        {
            height = most;
            width = height * bitmap.Width / bitmap.Height;
        }

        Room(height + 16);
        _y += 8;

        var box = SKRect.Create(Margin + (Width - width) / 2, _y, width, height);

        _canvas.DrawBitmap(bitmap, box);
        Frame(box);

        _y += height + 12;
    }

    private void Frame(SKRect box)
    {
        using var paint = new SKPaint
        {
            Color = Rule,
            StrokeWidth = 0.8f,
            IsStroke = true,
            IsAntialias = true
        };

        _canvas.DrawRect(box, paint);
    }

    private void LayCode(Code code)
    {
        var height = code.Lines.Count * (Body * 1.4f) + 14;

        Room(height + 8);
        _y += 6;

        using var back = new SKPaint { Color = CodeBack, IsAntialias = true };
        _canvas.DrawRoundRect(SKRect.Create(Margin, _y, Width, height), 3, 3, back);

        using var paint = Paint(_mono, Body - 0.5f, Ink);
        var line = _y + 14;

        foreach (var text in code.Lines)
        {
            _canvas.DrawText(text, Margin + 10, line, paint);
            line += Body * 1.4f;
        }

        _y += height + 10;
    }

    private void LayTable(Table table)
    {
        var columns = table.Rows.Max(row => row.Count);

        if (columns == 0)
            return;

        // The first column is the term being explained and is usually short; the rest share
        // what is left. A two-column table is the shape almost every one of these has.
        var widths = new float[columns];
        widths[0] = columns == 1 ? Width : Math.Min(Width * 0.32f, 150);

        for (var i = 1; i < columns; i++)
            widths[i] = (Width - widths[0]) / (columns - 1);

        const float padding = 6;

        foreach (var row in table.Rows)
        {
            var header = table.HasHeader && ReferenceEquals(row, table.Rows[0]);
            var face = header ? _bold : _text;

            // Measured before anything is drawn, so a row is never split across two pages.
            var height = 0f;

            for (var i = 0; i < row.Count; i++)
                height = Math.Max(height, Measure(row[i], widths[i] - padding * 2, Body - 0.5f, face));

            Room(height + padding * 2);

            var top = _y;
            var x = Margin;

            for (var i = 0; i < row.Count; i++)
            {
                _y = top + padding;
                Flow(row[i], x + padding, widths[i] - padding * 2, Body - 0.5f, face);
                x += widths[i];
            }

            _y = top + height + padding * 2;

            using var rule = new SKPaint { Color = Rule, StrokeWidth = 0.6f, IsAntialias = true };
            _canvas.DrawLine(Margin, _y, Right, _y, rule);
        }

        _y += 8;
    }

    #endregion

    #region Text

    /// <summary>How tall this text will be at this width, without drawing any of it.</summary>
    private float Measure(IReadOnlyList<Run> runs, float width, float size, SKTypeface face)
    {
        var was = _y;
        var canvas = _canvas;

        _canvas = null!;
        Flow(runs, 0, width, size, face, Ink, measuring: true);

        var height = _y - was;

        _y = was;
        _canvas = canvas;

        return height;
    }

    /// <summary>
    /// Draws a line of runs, wrapped to the width, advancing the cursor. Wrapping is by word,
    /// and a word longer than the column is simply allowed to overhang - it will be a file
    /// path, and breaking one is worse than letting it run on.
    /// </summary>
    private void Flow(IReadOnlyList<Run> runs, float x, float width, float size,
        SKTypeface face, SKColor? colour = null, bool measuring = false)
    {
        var ink = colour ?? Ink;
        var line = new List<(string Word, SKPaint Paint)>();
        var used = 0f;

        void Draw()
        {
            if (line.Count == 0)
                return;

            if (!measuring && _canvas is not null)
            {
                var at = x;

                foreach (var (word, paint) in line)
                {
                    _canvas.DrawText(word, at, _y + size, paint);
                    at += paint.MeasureText(word);
                }
            }

            _y += size * Leading;
            line.Clear();
            used = 0;
        }

        var paints = new List<SKPaint>();

        foreach (var run in runs)
        {
            var runFace = run.Code
                ? _mono
                : (run.Bold || ReferenceEquals(face, _bold)) && run.Italic
                    ? _boldItalic
                    : run.Bold || ReferenceEquals(face, _bold)
                        ? _bold
                        : run.Italic
                            ? _italic
                            : face;

            var paint = Paint(runFace, run.Code ? size - 0.5f : size, run.Code ? Accent : ink);
            paints.Add(paint);

            foreach (var word in Split(run.Text))
            {
                var advance = paint.MeasureText(word);

                if (used + advance > width && line.Count > 0 && word != " ")
                {
                    Draw();

                    if (!measuring)
                        Room(size * Leading);
                }

                if (used == 0 && word == " ")
                    continue;

                line.Add((word, paint));
                used += advance;
            }
        }

        Draw();

        foreach (var paint in paints)
            paint.Dispose();
    }

    /// <summary>Words, with the spaces kept as words of their own so they can be measured.</summary>
    private static IEnumerable<string> Split(string text)
    {
        var word = new System.Text.StringBuilder();

        foreach (var c in text)
        {
            if (c == ' ')
            {
                if (word.Length > 0)
                {
                    yield return word.ToString();
                    word.Clear();
                }

                yield return " ";
            }
            else
            {
                word.Append(c);
            }
        }

        if (word.Length > 0)
            yield return word.ToString();
    }

    #endregion
}
