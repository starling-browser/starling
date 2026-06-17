// SPDX-License-Identifier: Apache-2.0
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SixLabors.ImageSharp.Drawing.Processing.Backends;

namespace Starling.Paint.Backend;

/// <summary>
/// Adapter-internal reflection bridge to ImageSharp's non-public WebGPU device
/// context, used to allocate render targets on the compositor's device. One
/// instance is cached per device handle (matching the old engine-held context)
/// and disposed when the device is torn down via
/// <see cref="DisposeForDevice"/>. The compositor never sees this type — it
/// passes a neutral <see cref="GpuPaintDevice"/> and the adapter resolves the
/// context here.
/// </summary>
internal sealed class ImageSharpGpuContext : IDisposable
{
    private static readonly ConcurrentDictionary<nint, ImageSharpGpuContext> ByDevice = new();

    private static readonly Type ContextType = typeof(WebGPURenderTarget).Assembly.GetType(
        "SixLabors.ImageSharp.Drawing.Processing.Backends.WebGPUDeviceContext",
        throwOnError: true)!;

    private static readonly ConstructorInfo ContextConstructor = ContextType.GetConstructor(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        types: [typeof(nint), typeof(nint)],
        modifiers: null)
        ?? throw new InvalidOperationException("ImageSharp WebGPU device context constructor was not found.");

    private static readonly MethodInfo CreateRenderTargetMethod = ContextType.GetMethod(
        "CreateRenderTarget",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        types: [typeof(WebGPUTextureFormat), typeof(int), typeof(int)],
        modifiers: null)
        ?? throw new InvalidOperationException("ImageSharp WebGPU CreateRenderTarget method was not found.");

    private static readonly MethodInfo ThrowIfDisposedMethod = ContextType.GetMethod(
        "ThrowIfDisposed",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        types: Type.EmptyTypes,
        modifiers: null)
        ?? throw new InvalidOperationException("ImageSharp WebGPU ThrowIfDisposed method was not found.");

    private object? _context;

    private ImageSharpGpuContext(nint deviceHandle, nint queueHandle)
    {
        _context = ContextConstructor.Invoke([deviceHandle, queueHandle]);
    }

    /// <summary>The cached context for <paramref name="device"/>, created on first use.
    /// Keyed by device handle; <see cref="DisposeForDevice"/> must run on device
    /// teardown (it does, via the engine's dispose) so a reused handle never hits a
    /// stale context.</summary>
    public static ImageSharpGpuContext GetOrCreate(GpuPaintDevice device)
        => ByDevice.GetOrAdd(device.Device, static (_, d) => new ImageSharpGpuContext(d.Device, d.Queue), device);

    /// <summary>Dispose and forget the context for a device being torn down.</summary>
    public static void DisposeForDevice(nint deviceHandle)
    {
        if (deviceHandle != 0 && ByDevice.TryRemove(deviceHandle, out var context))
        {
            context.Dispose();
        }
    }

    public void ThrowIfDisposed()
    {
        var context = Context;
        Invoke(() => ThrowIfDisposedMethod.Invoke(context, null));
    }

    public WebGPURenderTarget CreateRenderTarget(WebGPUTextureFormat format, int width, int height)
    {
        var context = Context;
        return Invoke(() =>
            CreateRenderTargetMethod.Invoke(context, [format, width, height]) as WebGPURenderTarget
            ?? throw new InvalidOperationException("ImageSharp WebGPU CreateRenderTarget returned no target."));
    }

    private object Context
        => _context ?? throw new ObjectDisposedException(nameof(ImageSharpGpuContext));

    private static void Invoke(Action action)
    {
        try { action(); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static T Invoke<T>(Func<T> action)
    {
        try { return action(); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    public void Dispose()
    {
        if (_context is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _context = null;
    }
}
