using Avalonia;

namespace AVAPageStream;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        // AVAPageStream --mcp: the MCP server for AI agents, with no window (see AgentMain).
        Mcp.McpCommandLine.Wanted(args) ? Agents.AgentMain.Run(args) : BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
