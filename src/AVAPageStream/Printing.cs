using System.Diagnostics;

namespace AVAPageStream;

/// <summary>
/// Printing, through a PDF: Avalonia has no printing of its own, so the pages go to the system's printing as a PDF:
/// lp (CUPS) on Linux and macOS, the PDF's own Print action on Windows.
/// </summary>
internal static class Printing
{
    /// <summary>Sends a PDF to the default printer. Returns why it couldn't, or null.</summary>
    public static async Task<string?> Send(string pdf)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(pdf) { UseShellExecute = true, Verb = "print" });
                return null;
            }
            if (Find("lp") is not { } lp)
                return $"There's no lp command here to print with. The PDF is ready to print from another program: {pdf}";
            using var process = Process.Start(new ProcessStartInfo(lp) { ArgumentList = { pdf }, RedirectStandardError = true, UseShellExecute = false })!;
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? null : $"The printer said: {error.Trim()}";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return $"Couldn't print: {e.Message}. The PDF is at {pdf}.";
        }
    }

    private static string? Find(string command) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, command)).FirstOrDefault(File.Exists);
}
