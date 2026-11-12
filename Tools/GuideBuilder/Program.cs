using AVAPageStream.Guide;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: GuideBuilder <input.md> <output.pdf> [version]");
    return 1;
}

var source = args[0];
var target = args[1];
var version = args.Length > 2 ? args[2] : string.Empty;

if (!File.Exists(source))
{
    Console.Error.WriteLine($"{source}: no such file");
    return 1;
}

var blocks = Markdown.Read(File.ReadAllText(source));
var folder = Path.GetDirectoryName(Path.GetFullPath(source)) ?? ".";

using (var stream = File.Create(target))
    new PdfWriter(folder, version).Write(blocks, stream);

Console.WriteLine($"{target}: {new FileInfo(target).Length / 1024} KB from {blocks.Count} blocks");
return 0;
