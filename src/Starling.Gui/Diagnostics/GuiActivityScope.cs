// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace Starling.Gui.Diagnostics;

internal static class GuiActivityScope
{
    public static IDisposable Detached()
    {
        var previous = Activity.Current;
        Activity.Current = null;
        return new RestoreScope(previous);
    }

    public static IDisposable Use(Activity? activity)
    {
        var previous = Activity.Current;
        Activity.Current = activity is { IsStopped: false } ? activity : null;
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope(Activity? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Activity.Current = previous;
        }
    }
}
