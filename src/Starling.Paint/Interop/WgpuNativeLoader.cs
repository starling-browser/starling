using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Starling.Paint.Interop;

/// <summary>
/// Resolves <c>wgpu_native</c> P/Invokes from <c>Silk.NET.WebGPU</c> by
/// installing a <see cref="NativeLibrary.SetDllImportResolver"/> on the
/// Silk.NET.WebGPU assembly itself when it loads. On Mac Catalyst the runtime
/// RID is <c>maccatalyst-arm64</c> but the wgpu-native NuGet only ships
/// <c>osx-arm64</c> binaries, and MAUI's Catalyst bundler doesn't resolve
/// <c>osx-*</c> assets through the standard RID graph — so the default .NET
/// native loader can't find <c>libwgpu_native.dylib</c> at runtime even though
/// the file is in the .app bundle. This resolver probes the bundle's
/// <c>runtimes/osx-arm64/native/</c> path explicitly. Compiled in only under
/// the <c>EnableImageSharpDrawing3</c> build flag.
/// </summary>
/// <remarks>
/// <para>
/// The hook lives on Silk.NET's assembly, not ours, because
/// <see cref="NativeLibrary.SetDllImportResolver"/> is scoped to the
/// registering assembly — a resolver on Starling.Paint catches our own
/// P/Invokes but never Silk.NET's, where the <c>[DllImport("wgpu_native")]</c>
/// declarations actually live. We register from a <c>[ModuleInitializer]</c>
/// here and react via <see cref="AssemblyLoadEventArgs"/> so the hook is in
/// place by the first Silk.NET call (Silk.NET hasn't loaded yet at module-init
/// time on a fresh process; it's only pulled in via the
/// the ImageSharp backend code path we own).
/// </para>
/// <para>
/// Best-effort: if no candidate path resolves the dylib, the resolver returns
/// <c>nint.Zero</c> so .NET falls back to its default search. Silk.NET then
/// fails with its native error, and the <c>WebGPUEnvironment.ProbeAvailability</c>
/// check in <c>ImageSharpBackend.RenderWebGpu</c> surfaces a clear actionable
/// <see cref="InvalidOperationException"/>. Throwing here would break the
/// default-build path (this initializer fires whenever the assembly loads,
/// regardless of whether the WebGPU backend is ever selected).
/// </para>
/// </remarks>
internal static class WgpuNativeLoader
{
    private const string LibraryName = "wgpu_native";
    private const string SilkAssemblyPrefix = "Silk.NET.WebGPU";

    // Diagnostic trail: each step the loader takes is appended so we can dump
    // the whole story into the exception message when ProbeAvailability later
    // returns an error. The loader runs in [ModuleInitializer] which is too
    // early to plumb an ILogger through, so this is a self-contained log.
    private static readonly List<string> _events = [];
    private static readonly Lock _eventsLock = new();
    private static int _resolveCalls;
    private static int _hookedAssemblies;

    private static void Note(string evt)
    {
        lock (_eventsLock)
        {
            _events.Add(evt);
        }
    }

    /// <summary>Returns a snapshot of the loader's decision log for inclusion in diagnostics.</summary>
    internal static string Diagnose()
    {
        lock (_eventsLock)
        {
            var lines = new List<string>
            {
                $"WgpuNativeLoader: hooked {_hookedAssemblies} Silk.NET.WebGPU assembly/ies, " +
                $"Resolve called {_resolveCalls} time(s).",
            };
            lines.AddRange(_events);
            return string.Join(Environment.NewLine + "  ", lines);
        }
    }

#pragma warning disable CA2255 // ModuleInitializer in libraries: needed to install the resolver before Silk.NET WebGPU's first call
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
              || OperatingSystem.IsLinux() || OperatingSystem.IsWindows()))
        {
            Note("module init: unsupported OS, no resolver registered.");
            return;
        }

        Note($"module init: asm={typeof(WgpuNativeLoader).Assembly.Location}, IsMacCatalyst={OperatingSystem.IsMacCatalyst()}, arch={RuntimeInformation.ProcessArchitecture}");

        // Eager pre-load: walk candidate paths and try to map the dylib into
        // the process. Once loaded, .NET tracks it in its internal NativeLibrary
        // cache, so any subsequent NativeLibrary.Load("wgpu_native") (which is
        // what Silk.NET uses — it bypasses [DllImport] / SetDllImportResolver)
        // returns the cached handle instead of running its own search.
        var preloaded = false;
        foreach (var candidate in CandidatePaths())
        {
            if (!File.Exists(candidate))
            {
                Note($"preload: probe '{candidate}' -> not found.");
                continue;
            }
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                Note($"preload: loaded '{candidate}' -> handle 0x{handle:x}.");
                preloaded = true;
                break;
            }
            Note($"preload: probe '{candidate}' -> file exists but NativeLibrary.TryLoad failed.");
        }
        if (!preloaded)
        {
            Note("preload: no candidate resolved; relying on Silk.NET's default loader.");
        }

        // Still register a SetDllImportResolver in case Silk.NET ever switches
        // back to [DllImport] semantics (or a future Silk.NET version exposes
        // a [DllImport] surface). Free safety net.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            TryRegister(asm, "sweep");
        }

        AppDomain.CurrentDomain.AssemblyLoad += static (_, e) => TryRegister(e.LoadedAssembly, "AssemblyLoad");
    }
#pragma warning restore CA2255

    private static void TryRegister(Assembly assembly, string trigger)
    {
        var name = assembly.GetName().Name;
        if (name is null || !name.StartsWith(SilkAssemblyPrefix, StringComparison.Ordinal))
        {
            return;
        }

        // SetDllImportResolver throws if called twice for the same assembly;
        // guard with a try/catch since the assembly-load event can race with
        // the initial sweep above.
        try
        {
            NativeLibrary.SetDllImportResolver(assembly, Resolve);
            Interlocked.Increment(ref _hookedAssemblies);
            Note($"hooked '{name}' via {trigger}.");
        }
        catch (InvalidOperationException ex)
        {
            Note($"hook '{name}' via {trigger} skipped: {ex.Message}");
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        Interlocked.Increment(ref _resolveCalls);
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
        {
            Note($"Resolve({libraryName}): ignored (not '{LibraryName}').");
            return nint.Zero;
        }

        foreach (var candidate in CandidatePaths())
        {
            var exists = File.Exists(candidate);
            if (!exists)
            {
                Note($"Resolve: probe '{candidate}' -> not found.");
                continue;
            }
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                Note($"Resolve: loaded '{candidate}' -> handle 0x{handle:x}.");
                return handle;
            }
            Note($"Resolve: probe '{candidate}' -> file exists but NativeLibrary.TryLoad failed.");
        }
        Note("Resolve: all candidates exhausted, returning IntPtr.Zero (Silk.NET will fall back to its default loader).");
        return nint.Zero;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var fileName = NativeFileName();
        var asmDir = Path.GetDirectoryName(typeof(WgpuNativeLoader).Assembly.Location)
                     ?? AppContext.BaseDirectory;

        // RIDs to probe, most-likely first. macOS and Mac Catalyst both load
        // osx-arm64 dylibs on Apple Silicon (the wgpu-native build is platform
        // PLATFORM_MACOS but dlopen accepts it from Catalyst processes); we
        // also check the maccatalyst-arm64 directory in case a future package
        // ships there.
        string[] rids = (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
            ? RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? new[] { "osx-arm64", "maccatalyst-arm64", "osx-x64" }
                : new[] { "osx-x64", "osx-arm64" }
            : OperatingSystem.IsWindows()
                ? RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? new[] { "win-arm64", "win-x64" }
                    : new[] { "win-x64", "win-x86" }
                : [$"linux-{Arch()}"];

        // 1. Starling.Paint's wgpu-native/ sidecar (set by Starling.Paint.csproj
        //    when EnableImageSharpDrawing3=true). Placed outside runtimes/ so
        //    the MAUI Catalyst SDK doesn't try to run install_name_tool on it.
        yield return Path.Combine(asmDir, "wgpu-native", fileName);

        foreach (var rid in rids)
        {
            // 2. Standard NuGet runtime-asset layout — works for the non-MAUI
            //    .NET host (Headless CLI, tests) which gets the dylib placed
            //    here automatically by Silk.NET's own targets.
            yield return Path.Combine(asmDir, "runtimes", rid, "native", fileName);

            // 3. Walk up looking for a repo-root runtimes tree — covers test
            //    runs where the working directory differs from the bin layout.
            var dir = new DirectoryInfo(asmDir);
            while (dir is not null)
            {
                yield return Path.Combine(dir.FullName, "runtimes", rid, "native", fileName);
                dir = dir.Parent;
            }
        }

        // 4. Flat next to the assembly (Silk.NET's targets default link layout).
        yield return Path.Combine(asmDir, fileName);
    }

    private static string Arch() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64",
    };

    private static string NativeFileName()
    {
        if (OperatingSystem.IsWindows())
        {
            return $"{LibraryName}.dll";
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            return $"lib{LibraryName}.dylib";
        }

        return $"lib{LibraryName}.so";
    }
}
