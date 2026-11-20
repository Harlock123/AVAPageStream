using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using AVAPageStream.Mcp;

namespace AVAPageStream.Agents;

/// <summary>Runs work on Avalonia's UI thread, where text is measured and pages are drawn.</summary>
internal sealed class AvaloniaThread : IUiThread
{
    public Task<T> InvokeAsync<T>(Func<Task<T>> work) => Dispatcher.UIThread.InvokeAsync(work);
}

/// <summary>
/// <c>AVAPageStream --mcp</c>: the MCP server with no window. Avalonia starts on its headless platform with Skia (so
/// text is measured and pages drawn exactly as on screen and in export), its dispatcher runs on this thread, and the
/// server runs beside it until the client goes away or Ctrl+C.
/// </summary>
internal static class AgentMain
{
    public static int Run(string[] args)
    {
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        var server = Task.Run(() => McpCommandLine.RunAsync(args, () => new AgentLibrary(), new AvaloniaThread(), input, output, Console.Error, stop.Token));
        server.ContinueWith(_ => Dispatcher.UIThread.Post(() => stop.Cancel()), TaskScheduler.Default);
        Dispatcher.UIThread.MainLoop(stop.Token);
        try
        {
            return server.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"The MCP server stopped: {e.Message}");
            return 1;
        }
    }
}
