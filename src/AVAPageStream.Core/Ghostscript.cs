using System.Diagnostics;

namespace AVAPageStream.Core;

/// <summary>Drawing PostScript with Ghostscript, when it is installed (gs, or gswin64c on Windows).</summary>
public static class Ghostscript
{
    /// <summary>The Ghostscript command on this computer, or null.</summary>
    public static string? Find()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "gswin64c.exe", "gswin32c.exe" } : ["gs"];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in names)
                if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }

    /// <summary>An EPS drawn as a PNG at <paramref name="dpi"/>, cropped to its bounding box; null without Ghostscript or if it fails.</summary>
    public static byte[]? Render(byte[] eps, int dpi = 300)
    {
        if (Find() is not { } gs) return null;
        var dir = Directory.CreateTempSubdirectory("avapagestream-eps-").FullName;
        try
        {
            var input = Path.Combine(dir, "in.eps");
            var output = Path.Combine(dir, "out.png");
            File.WriteAllBytes(input, Eps.PostScript(eps));
            var info = new ProcessStartInfo(gs)
            {
                ArgumentList = { "-q", "-dSAFER", "-dBATCH", "-dNOPAUSE", "-dEPSCrop", "-sDEVICE=pngalpha", $"-r{dpi}", $"-sOutputFile={output}", input },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(info)!;
            if (!process.WaitForExit(30_000)) { process.Kill(true); return null; }
            return process.ExitCode == 0 && File.Exists(output) ? File.ReadAllBytes(output) : null;
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
