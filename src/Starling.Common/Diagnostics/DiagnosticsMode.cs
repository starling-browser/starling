// SPDX-License-Identifier: Apache-2.0

namespace Starling.Common.Diagnostics;

public static class DiagnosticsMode
{
    public static bool TraceConsole { get; } = Enabled("STARLING_DIAG_TRACE");
    public static bool Detailed { get; } = TraceConsole || Enabled("STARLING_DIAG_DETAIL");
    public static bool TelemetrySinks { get; } = Detailed || Enabled("STARLING_TELEMETRY_DEVTOOLS");
    public static bool ProcessSampler { get; } = Detailed || TelemetrySinks || Enabled("STARLING_PROCESS_DIAG");

    private static bool Enabled(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}
