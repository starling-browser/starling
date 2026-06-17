// SPDX-License-Identifier: Apache-2.0

using System.Reflection;

namespace Starling.Shell.Native;

internal static class BuildFactsRenderer
{
    public static string Render(string jsEngine, string renderBackend, string labelColor, string valueColor)
    {
        var commit = ValueOrFallback(BuildLabel());
        var js = ValueOrFallback(jsEngine);
        var render = ValueOrFallback(renderBackend);
        return
            "<div style=\"position:relative;width:196px;height:68px;" +
            $"color:{EscapeHtml(labelColor)};font-size:11px;line-height:14px\">" +
            $"<div style=\"position:absolute;left:0;top:0;width:196px;color:{EscapeHtml(valueColor)};" +
            "font-family:'Geist Mono','SFMono-Regular',Menlo,Consolas,monospace\">native shell</div>" +
            "<div style=\"position:absolute;left:0;top:20px;width:54px\">commit</div>" +
            $"<div style=\"position:absolute;right:0;top:20px;width:130px;text-align:right;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;color:{EscapeHtml(valueColor)};" +
            $"font-family:'Geist Mono','SFMono-Regular',Menlo,Consolas,monospace\">{EscapeHtml(commit)}</div>" +
            "<div style=\"position:absolute;left:0;top:36px;width:54px\">js</div>" +
            $"<div style=\"position:absolute;right:0;top:36px;width:130px;text-align:right;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;color:{EscapeHtml(valueColor)};" +
            $"font-family:'Geist Mono','SFMono-Regular',Menlo,Consolas,monospace\">{EscapeHtml(js)}</div>" +
            "<div style=\"position:absolute;left:0;top:52px;width:54px\">render</div>" +
            $"<div style=\"position:absolute;right:0;top:52px;width:130px;text-align:right;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;color:{EscapeHtml(valueColor)};" +
            $"font-family:'Geist Mono','SFMono-Regular',Menlo,Consolas,monospace\">{EscapeHtml(render)}</div>" +
            "</div>";
    }

    private static string BuildLabel()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? string.Empty;

        var plus = info.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            info = info[(plus + 1)..];
        }

        return info.Length > 8 ? info[..8] : info;
    }

    private static string ValueOrFallback(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string EscapeHtml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
