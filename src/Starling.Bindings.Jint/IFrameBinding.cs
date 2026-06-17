using System.Runtime.CompilerServices;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Starling.Dom;
using Starling.Dom.Events;
using Starling.Html;
using Starling.Loop;
using StarlingUrlParser = global::Starling.Url.UrlParser;

namespace Starling.Bindings.Jint;

/// <summary>
/// Minimal HTMLIFrameElement / browsing-context surface for the Jint backend,
/// mirroring <c>Starling.Bindings/IFrameBinding.cs</c>. Each &lt;iframe&gt; gets a
/// nested <see cref="Document"/> (about:blank by default) plus a
/// <c>contentWindow</c> proxy, and <c>iframe.src</c> lazily loads + parses HTML
/// into the nested document and fires <c>load</c>.
/// </summary>
/// <remarks>
/// Intentional divergences from the canonical (Starling.Js) backend, which models
/// nested browsing contexts as separate realms inside one engine: Jint has exactly
/// one realm per engine, so a cross-realm window object would be a foreign-engine
/// object. Instead the nested document is wrapped in the <b>parent</b> engine (so
/// <c>iframe.contentDocument.body</c> works) and <c>contentWindow</c> is a
/// parent-engine proxy exposing <c>document</c>/<c>parent</c>/<c>top</c>/<c>self</c>/
/// <c>window</c>/<c>frameElement</c>/<c>length</c>. Nested classic inline scripts run
/// in a fresh child engine over the nested document (best-effort, fail-soft); their
/// DOM mutations are visible through <c>contentDocument</c>. No postMessage, no
/// cross-realm global identity, no frame history; <c>src</c> is fetched
/// synchronously on first access.
/// </remarks>
internal static class IFrameBinding
{
    private sealed class FrameContext
    {
        public Element Frame;
        public Document Document;
        public JsObject? ContentWindow;
        public string LoadedSrc = "\0"; // sentinel: nothing loaded yet
        public FrameContext(Element frame, Document doc) { Frame = frame; Document = doc; }
    }

    private static readonly ConditionalWeakTable<Element, FrameContext> Contexts = new();
    private static readonly ConditionalWeakTable<Document, FrameContext> ByDocument = new();

    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;
        var elProto = ctx.Wrappers.ElementPrototype;
        var docProto = ctx.Wrappers.DocumentPrototype;
        if (elProto is null)
        {
            return;
        }

        JintInterop.DefineAccessor(engine, elProto, "contentDocument", (t, _) =>
        {
            if (ctx.Wrappers.UnwrapElement(t) is not { } e || !IsFrameElement(e))
            {
                return JsValue.Null;
            }

            var fc = EnsureContext(ctx, e);
            return ctx.Wrappers.Wrap(fc.Document);
        });
        JintInterop.DefineAccessor(engine, elProto, "contentWindow", (t, _) =>
        {
            if (ctx.Wrappers.UnwrapElement(t) is not { } e || !IsFrameElement(e))
            {
                return JsValue.Null;
            }

            return EnsureContentWindow(ctx, EnsureContext(ctx, e));
        });

        // document.defaultView — nested iframe doc → its contentWindow; otherwise
        // the main window (matching the NodeBindings default).
        if (docProto is not null)
        {
            JintInterop.DefineAccessor(engine, docProto, "defaultView", (t, _) =>
            {
                if (ctx.Wrappers.UnwrapDocument(t) is not { } d)
                {
                    return JsValue.Null;
                }

                if (ByDocument.TryGetValue(d, out var fc))
                {
                    return EnsureContentWindow(ctx, fc);
                }

                return engine.Global;
            });
        }
    }

    private static bool IsFrameElement(Element e) =>
        e.LocalName.Equals("iframe", StringComparison.OrdinalIgnoreCase)
        || e.LocalName.Equals("frame", StringComparison.OrdinalIgnoreCase);

    private static FrameContext EnsureContext(JintBackendContext ctx, Element frame)
    {
        if (!Contexts.TryGetValue(frame, out var fc))
        {
            fc = new FrameContext(frame, BuildBlankDocument());
            Contexts.Add(frame, fc);
            ByDocument.AddOrUpdate(fc.Document, fc);
        }
        MaybeLoadSrc(ctx, fc);
        return fc;
    }

    private static JsObject EnsureContentWindow(JintBackendContext ctx, FrameContext fc)
    {
        if (fc.ContentWindow is not null)
        {
            return fc.ContentWindow;
        }

        var engine = ctx.Engine;
        var win = new JsObject(engine);
        JintInterop.DefineAccessor(engine, win, "document", (_, _) => ctx.Wrappers.Wrap(fc.Document));
        JintInterop.DefineAccessor(engine, win, "self", (_, _) => win);
        JintInterop.DefineAccessor(engine, win, "window", (_, _) => win);
        JintInterop.DefineAccessor(engine, win, "parent", (_, _) => engine.Global);
        JintInterop.DefineAccessor(engine, win, "top", (_, _) => engine.Global);
        JintInterop.DefineAccessor(engine, win, "frameElement", (_, _) => ctx.Wrappers.Wrap(fc.Frame));
        JintInterop.DefineDataProp(win, "length", JintInterop.Num(0), writable: true, enumerable: true, configurable: true);
        fc.ContentWindow = win;
        return win;
    }

    private static void MaybeLoadSrc(JintBackendContext ctx, FrameContext fc)
    {
        var src = fc.Frame.GetAttribute("src");
        if (string.IsNullOrEmpty(src) || src == fc.LoadedSrc)
        {
            return;
        }

        fc.LoadedSrc = src;

        var parsed = StarlingUrlParser.Parse(src, ctx.BaseUrl);
        if (parsed.IsErr)
        {
            return;
        }

        var url = parsed.Value;

        string? body;
        try { body = ctx.Fetch(url, CancellationToken.None).GetAwaiter().GetResult(); }
        catch { body = null; }
        if (body is null) { FireLoad(ctx, fc.Frame); return; }

        var doc = HtmlParser.Parse(body, scriptingEnabled: true);
        fc.Document = doc;
        ByDocument.AddOrUpdate(doc, fc);
        fc.ContentWindow = null; // rebuild against the new document on next read

        RunFrameScripts(ctx, fc, url);
        FireLoad(ctx, fc.Frame);
    }

    // Run nested classic inline scripts in a fresh child engine over the nested
    // document. Fail-soft: a script error must not escape into the parent.
    private static void RunFrameScripts(JintBackendContext ctx, FrameContext fc, global::Starling.Url.Url url)
    {
        var childEngine = new global::Jint.Engine();
        var childCtx = new JintBackendContext(
            childEngine, fc.Document, url, ctx.Http, ctx.LoggerFactory,
            new WebEventLoop(), ctx.LayoutHost, ctx.Fetch);
        JintBindings.InstallAll(childCtx);

        foreach (var node in fc.Document.Descendants())
        {
            if (node is not Element { LocalName: "script" } sc)
            {
                continue;
            }

            var type = sc.GetAttribute("type");
            if (!string.IsNullOrEmpty(type)
                && !type.Equals("text/javascript", StringComparison.OrdinalIgnoreCase)
                && !type.Equals("application/javascript", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(sc.GetAttribute("src")))
            {
                continue; // external skipped (best-effort)
            }

            var source = sc.TextContent;
            if (string.IsNullOrEmpty(source))
            {
                continue;
            }

            try { childEngine.Execute(source); }
            catch { /* fail-soft: nested script errors stay in the frame */ }
        }
    }

    private static void FireLoad(JintBackendContext ctx, Element frame)
    {
        try { frame.DispatchEvent(new Event("load", new EventInit(Bubbles: false, Cancelable: false))); }
        catch { /* fail-soft */ }

        // iframe.onload set on the JS wrapper is invoked explicitly.
        var wrapper = ctx.Wrappers.Wrap(frame);
        if (wrapper is ObjectInstance oi)
        {
            var handler = oi.Get("onload");
            if (handler.IsCallable())
            {
                try { handler.Call(oi, System.Array.Empty<JsValue>()); }
                catch { /* fail-soft */ }
            }
        }
    }

    private static Document BuildBlankDocument()
    {
        var doc = new Document { IsHtml = true };
        var html = doc.CreateElement("html");
        var head = doc.CreateElement("head");
        var body = doc.CreateElement("body");
        html.AppendChild(head);
        html.AppendChild(body);
        doc.AppendChild(html);
        return doc;
    }
}
