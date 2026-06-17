namespace Starling.Shell.Native;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (Array.IndexOf(args, "--spike") >= 0)
        {
            return SpikeProgram.Run();
        }

        // --frames N: auto-close after N presented frames (smoke-test helper).
        var maxFrames = 0;
        var fi = Array.IndexOf(args, "--frames");
        if (fi >= 0 && fi + 1 < args.Length && int.TryParse(args[fi + 1], out var n))
        {
            maxFrames = n;
        }

        // --browser: run the interactive browser window with real engine navigation.
        if (Array.IndexOf(args, "--browser") >= 0)
        {
            // --url <URL>: open this URL at launch instead of the built-in demo page.
            string? startUrl = null;
            var ui = Array.IndexOf(args, "--url");
            if (ui >= 0 && ui + 1 < args.Length)
            {
                startUrl = args[ui + 1];
            }

            string? wasmIslandUrl = null;
            var wi = Array.IndexOf(args, "--wasm-island-url");
            if (wi >= 0 && wi + 1 < args.Length)
            {
                wasmIslandUrl = args[wi + 1];
            }

            using var browser = new NativeBrowserWindow(maxFrames, startUrl, wasmIslandUrl);
            return browser.Run();
        }

        // Default: the zero-copy page-present demo.
        return NativePresentDemo.Run(maxFrames);
    }
}
