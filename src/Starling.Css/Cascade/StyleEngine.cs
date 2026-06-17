using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Css.Animations;
using Starling.Css.CounterStyle;
using Starling.Css.Media;
using Starling.Css.Parser;
using Starling.Css.Properties;
using Starling.Css.Selectors;
using Starling.Css.Tokenizer;
using Starling.Css.UserAgent;
using Starling.Css.Values;
using Starling.Dom;

namespace Starling.Css.Cascade;

public sealed class StyleEngine
{
    private readonly List<StyleSheet> _sheets = [];
    private readonly Dictionary<StyleOrigin, LayerOrder> _layerOrders = new();
    private readonly Dictionary<StyleSheet, SheetIndex> _sheetIndexes
        = new(ReferenceEqualityComparer.Instance);
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;
    private MediaContext _mediaContext = MediaContext.Default;
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<CssComponentValue>> s_emptyCustomProperties
        = new Dictionary<string, IReadOnlyList<CssComponentValue>>(StringComparer.Ordinal);

    // True when any active stylesheet uses :has() or :empty — selectors whose
    // match for an element depends on its descendants/emptiness, so a child
    // insert/remove can restyle ancestors or the parent itself, anywhere in the
    // tree. Incremental structural layout then can't localize and falls back to
    // a full rebuild. (Sibling combinators and positional pseudos like
    // :nth-child only restyle within the changed parent's children, which the
    // structural reconciler handles by re-cascading that subtree.)
    private bool _structuralRebuildSensitive;

    /// <summary>Whether a child insert/remove can change the cascade outside the
    /// changed parent's own subtree (because some selector uses <c>:has()</c> or
    /// <c>:empty</c>). When true, incremental structural layout falls back to a
    /// full rebuild.</summary>
    public bool StructuralChangeNeedsFullRebuild => _structuralRebuildSensitive;

    // Attribute names referenced by any selector's attribute selector (e.g.
    // `[data-state="open"]` references "data-state"). Selector-aware invalidation
    // uses this so a script write to such an attribute is treated as
    // layout/style-relevant even when the static heuristic would skip it (plan §7).
    private readonly HashSet<string> _referencedAttributes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The attribute names any active stylesheet selects on
    /// (<c>[attr]</c>, <c>[attr=v]</c>, …, recursing into <c>:is/:where/:not/:has</c>).
    /// A DOM write to one of these can change the cascade, so it must invalidate
    /// layout even if it's a <c>data-*</c>/<c>aria-*</c> attribute the static
    /// heuristic treats as cosmetic.</summary>
    public IReadOnlySet<string> ReferencedAttributeNames => _referencedAttributes;

    /// <summary>Resolver built from every <c>@counter-style</c> rule in the
    /// attached stylesheets (CSS Counter Styles 3 §3). Holds the predefined
    /// styles plus any author-defined ones. Use
    /// <see cref="CounterStyleResolver.Render(string, int)"/> to turn a counter
    /// integer into its marker text.</summary>
    public CounterStyleResolver CounterStyles { get; private set; } = CounterStyleResolver.Default;

    /// <summary>Custom properties registered via <c>@property</c> across the
    /// attached stylesheets (CSS Properties and Values API 1 §2), keyed by name
    /// (including the leading <c>--</c>). Later registrations of the same name
    /// win, matching the cascade's last-wins order. Holds the parsed
    /// <c>syntax</c>/<c>inherits</c>/<c>initial-value</c> descriptors so the
    /// cascade can honor a registered property's initial value and inheritance.</summary>
    public IReadOnlyDictionary<string, PropertiesValues.RegisteredProperty> RegisteredProperties { get; private set; }
        = new Dictionary<string, PropertiesValues.RegisteredProperty>();

    private readonly AnimationTimeline _timeline;

    public StyleEngine(bool includeUserAgentStyleSheet = true, ILoggerFactory? loggerFactory = null)
        : this(includeUserAgentStyleSheet, loggerFactory, timeline: null)
    {
    }

    /// <summary>
    /// Build a style engine that draws its animation/transition state from
    /// <paramref name="timeline"/>. Pass a timeline the caller keeps alive across
    /// layout passes (one per document) so animation playback survives a
    /// relayout — the <see cref="StyleEngine"/> itself is rebuilt every layout,
    /// but the timeline is not. When <paramref name="timeline"/> is <c>null</c> a
    /// fresh, engine-local timeline is created (the single-shot render and unit
    /// test default).
    /// </summary>
    public StyleEngine(bool includeUserAgentStyleSheet, ILoggerFactory? loggerFactory, AnimationTimeline? timeline)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _log = _loggerFactory.CreateLogger<StyleEngine>();
        _timeline = timeline ?? new AnimationTimeline();
        // The keyframe registry tracks the *current* stylesheet set, which is
        // re-attached on every layout. A reused timeline still holds the prior
        // layout's keyframes, so clear them before AddStyleSheet re-registers
        // from this layout's sheets. Playback state (active instances, script
        // animations, transition snapshots) lives elsewhere and is preserved.
        _timeline.Animations.ClearKeyframes();
        foreach (var origin in Enum.GetValues<StyleOrigin>())
        {
            _layerOrders[origin] = new LayerOrder();
        }

        if (includeUserAgentStyleSheet)
        {
            AddStyleSheet(UaStyleSheet.Parse());
        }
    }

    /// <summary>The animation engine fed by <c>@keyframes</c> rules in every
    /// stylesheet attached via <see cref="AddStyleSheet"/>. Sample with
    /// <see cref="AnimationEngine.GetEffective(Element, PropertyId)"/> after
    /// cascaded declarations have been fed to it. Lives on the
    /// <see cref="AnimationTimeline"/> so it outlives this engine.</summary>
    public AnimationEngine AnimationEngine => _timeline.Animations;

    /// <summary>The transition engine. Independent from animations but exposed
    /// here so callers can route both through one
    /// <see cref="StyleEngine"/> instance.</summary>
    public TransitionEngine TransitionEngine => _timeline.Transitions;

    public MediaContext MediaContext
    {
        get => _mediaContext;
        set => _mediaContext = value ?? MediaContext.Default;
    }

    /// <summary>Provider that resolves real font glyph metrics (x-height, cap-height,
    /// '0' advance, ideographic advance) for the cascaded font. Defaults to a
    /// heuristic implementation that derives metrics from <c>font-size</c>. A
    /// real shaping-aware backend should replace this.</summary>
    public IFontMetricsProvider FontMetrics { get; set; } = new HeuristicFontMetricsProvider();

    /// <summary>Optional lookup invoked when resolving <c>cq*</c> units. The engine
    /// walks the element's ancestor chain; for each ancestor, the lookup may return
    /// a <c>(width, height)</c> pair if that ancestor is a container with a known
    /// box size. The first non-null result is used. When the lookup is null or
    /// every ancestor returns null, the spec-correct fallback is the small viewport
    /// per CSS Containment 3 §3.2.</summary>
    public Func<Element, (double Width, double Height)?>? ContainerSizeLookup { get; set; }

    public void AddStyleSheet(StyleSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        _sheets.Add(sheet);
        RegisterLayers(sheet.Rules, sheet.Origin, currentPath: null);
        var index = BuildSheetIndex(sheet, _loggerFactory, _layerOrders[sheet.Origin]);
        _sheetIndexes[sheet] = index;
        if (!_structuralRebuildSensitive && SheetUsesHasOrEmpty(index))
        {
            _structuralRebuildSensitive = true;
        }

        CollectReferencedAttributes(index, _referencedAttributes);
        RegisterKeyframesFromSheet(sheet);
        RebuildCounterStyles();
        RebuildRegisteredProperties();
    }

    public void RemoveStyleSheet(StyleSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        _sheets.Remove(sheet);
        _sheetIndexes.Remove(sheet);
        _structuralRebuildSensitive = _sheetIndexes.Values.Any(SheetUsesHasOrEmpty);
        _referencedAttributes.Clear();
        foreach (var idx in _sheetIndexes.Values)
        {
            CollectReferencedAttributes(idx, _referencedAttributes);
        }

        UnregisterKeyframesFromSheet(sheet);
        RebuildCounterStyles();
        RebuildRegisteredProperties();
    }

    private void RebuildCounterStyles()
    {
        // CSS Counter Styles 3 §3: later @counter-style definitions of a name
        // win. Collect across all sheets in add order so the resolver's
        // last-wins map matches the cascade.
        var rules = _sheets.SelectMany(CounterStyleParser.ParseAll);
        CounterStyles = new CounterStyleResolver(rules);
    }

    private void RebuildRegisteredProperties()
    {
        // CSS Properties and Values API 1 §2: a later @property of the same name
        // wins. Collect across all sheets in add order into a last-wins map.
        var map = new Dictionary<string, PropertiesValues.RegisteredProperty>();
        foreach (var sheet in _sheets)
        {
            foreach (var registered in PropertiesValues.PropertyDefinitionParser.ParseAll(sheet))
            {
                map[registered.Name] = registered;
            }
        }

        RegisteredProperties = map;
    }

    private void RegisterKeyframesFromSheet(StyleSheet sheet)
    {
        foreach (var rule in KeyframesParser.ParseAll(sheet))
        {
            AnimationEngine.RegisterKeyframes(rule);
        }
    }

    private void UnregisterKeyframesFromSheet(StyleSheet sheet)
    {
        // After removing this sheet, rebuild the keyframes registry from the
        // remaining sheets so that names defined elsewhere survive and names
        // unique to the removed sheet are dropped. Last-wins ordering matches
        // the original add order, just like CSSOM removeRule semantics.
        AnimationEngine.ClearKeyframes();
        foreach (var remaining in _sheets)
        {
            RegisterKeyframesFromSheet(remaining);
        }
    }

    /// <summary>True if any selector in <paramref name="index"/> uses
    /// <c>:has()</c> or <c>:empty</c> (recursing into <c>:is()</c>/<c>:where()</c>/
    /// <c>:not()</c>/<c>:has()</c> arguments). Those make an element's match
    /// depend on its descendants/emptiness, so a structural change can restyle
    /// outside the changed parent's subtree.</summary>
    private static bool SheetUsesHasOrEmpty(SheetIndex index)
    {
        foreach (var parsed in index.SelectorLists)
        {
            if (SelectorListUsesHasOrEmpty(parsed))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Collect every attribute name referenced by an attribute selector
    /// in <paramref name="index"/> (recursing into functional pseudo arguments)
    /// into <paramref name="sink"/>. Selector-aware invalidation (plan §7).</summary>
    private static void CollectReferencedAttributes(SheetIndex index, HashSet<string> sink)
    {
        foreach (var parsed in index.SelectorLists)
        {
            CollectReferencedAttributes(parsed, sink);
        }
    }

    private static void CollectReferencedAttributes(SelectorList list, HashSet<string> sink)
    {
        foreach (var complex in list.Selectors)
        {
            foreach (var part in complex.Parts)
            {
                foreach (var simple in part.Compound.SimpleSelectors)
                {
                    if (simple is AttributeSelector attr)
                    {
                        sink.Add(attr.Name);
                    }
                    else if (simple is PseudoClassSelector pc)
                    {
                        switch (pc.Argument)
                        {
                            case SelectorList nested: CollectReferencedAttributes(nested, sink); break;
                            case NthArgument { OfSelector: { } of }: CollectReferencedAttributes(of, sink); break;
                        }
                    }
                }
            }
        }
    }

    private static bool SelectorListUsesHasOrEmpty(SelectorList list)
    {
        foreach (var complex in list.Selectors)
        {
            foreach (var part in complex.Parts)
            {
                foreach (var simple in part.Compound.SimpleSelectors)
                {
                    if (simple is not PseudoClassSelector pc)
                    {
                        continue;
                    }

                    if (pc.Name is "has" or "empty")
                    {
                        return true;
                    }
                    // Recurse into functional pseudo arguments (:is/:where/:not/:has).
                    switch (pc.Argument)
                    {
                        case SelectorList nested when SelectorListUsesHasOrEmpty(nested):
                            return true;
                        case NthArgument { OfSelector: { } of } when SelectorListUsesHasOrEmpty(of):
                            return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Per-sheet cascade plan. Style rules are flattened through
    /// <c>@media</c>/<c>@supports</c>/<c>@container</c>/<c>@layer</c> and CSS
    /// nesting, then bucketed by the rightmost simple selector so per-element
    /// cascade starts from candidates instead of walking every rule.
    /// </summary>
    private sealed class SheetIndex
    {
        public SelectorIndex<IndexedStyleRule> Selectors { get; } = new();
        public List<SelectorList> SelectorLists { get; } = [];
    }

    private sealed class IndexedStyleRule(
        StyleRule rule,
        IReadOnlyList<RuleCondition> conditions,
        int sourceOrder,
        string? layerPath)
    {
        public StyleRule Rule { get; } = rule;
        public IReadOnlyList<RuleCondition> Conditions { get; } = conditions;
        public int SourceOrder { get; } = sourceOrder;
        public string? LayerPath { get; } = layerPath;
    }

    private static SheetIndex BuildSheetIndex(StyleSheet sheet, ILoggerFactory loggerFactory, LayerOrder layerOrder)
    {
        var index = new SheetIndex();
        var sourceOrder = 0;
        var log = loggerFactory.CreateLogger(typeof(StyleEngine));
        IndexRules(
            index,
            sheet.Rules,
            log,
            layerOrder,
            currentLayerPath: null,
            parentSelectors: null,
            conditions: [],
            ref sourceOrder);
        return index;
    }

    private static void IndexRules(
        SheetIndex index,
        IReadOnlyList<CssRule> rules,
        ILogger log,
        LayerOrder layerOrder,
        string? currentLayerPath,
        SelectorList? parentSelectors,
        IReadOnlyList<RuleCondition> conditions,
        ref int sourceOrder)
    {
        foreach (var rule in rules)
        {
            switch (rule)
            {
                case StyleRule styleRule:
                    // CSS Syntax 3 §5: an invalid selector causes the rule to be dropped, but
                    // other rules in the stylesheet must still apply. Swallow per-rule parse
                    // failures instead of aborting the whole indexing pass.
                    SelectorList parsed;
                    try
                    {
                        parsed = parentSelectors is null
                            ? SelectorParser.ParseSelectorList(styleRule.Prelude)
                            : ResolveSelectorList(styleRule.Prelude, parentSelectors);
                    }
                    catch (FormatException ex)
                    {
                        StyleEngineLog.InvalidSelector(log, ex.Message);
                        break;
                    }
                    index.SelectorLists.Add(parsed);
                    if (styleRule.Declarations.Count > 0)
                    {
                        var indexedRule = new IndexedStyleRule(
                            styleRule,
                            conditions,
                            sourceOrder++,
                            currentLayerPath);
                        index.Selectors.Add(parsed, indexedRule);
                    }
                    if (styleRule.NestedRulesOrEmpty.Count > 0)
                    {
                        IndexRules(
                            index,
                            styleRule.NestedRulesOrEmpty,
                            log,
                            layerOrder,
                            currentLayerPath,
                            parsed,
                            conditions,
                            ref sourceOrder);
                    }
                    break;
                case AtRule atRule when atRule.Name.Equals("media", StringComparison.OrdinalIgnoreCase):
                    IndexRules(
                        index,
                        atRule.Rules,
                        log,
                        layerOrder,
                        currentLayerPath,
                        parentSelectors,
                        AppendCondition(conditions, new RuleCondition(RuleConditionKind.Media, atRule)),
                        ref sourceOrder);
                    break;
                case AtRule atRule when atRule.Name.Equals("supports", StringComparison.OrdinalIgnoreCase):
                    IndexRules(
                        index,
                        atRule.Rules,
                        log,
                        layerOrder,
                        currentLayerPath,
                        parentSelectors,
                        AppendCondition(conditions, new RuleCondition(RuleConditionKind.Supports, atRule)),
                        ref sourceOrder);
                    break;
                case AtRule atRule when atRule.Name.Equals("container", StringComparison.OrdinalIgnoreCase):
                    IndexRules(
                        index,
                        atRule.Rules,
                        log,
                        layerOrder,
                        currentLayerPath,
                        parentSelectors,
                        AppendCondition(conditions, new RuleCondition(RuleConditionKind.Container, atRule)),
                        ref sourceOrder);
                    break;
                case AtRule atRule when atRule.Name.Equals("layer", StringComparison.OrdinalIgnoreCase):
                    var layerNames = ParseLayerNamesFromPrelude(atRule.Prelude);
                    if (atRule.Rules.Count == 0 && atRule.Declarations.Count == 0)
                    {
                        break;
                    }

                    string layerPath;
                    if (layerNames.Count == 0)
                    {
                        layerPath = Combine(
                            currentLayerPath,
                            "__anon" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(atRule).ToString());
                        layerOrder.RegisterLayer(layerPath);
                    }
                    else
                    {
                        layerPath = Combine(currentLayerPath, layerNames[0]);
                    }

                    IndexRules(
                        index,
                        atRule.Rules,
                        log,
                        layerOrder,
                        layerPath,
                        parentSelectors,
                        conditions,
                        ref sourceOrder);
                    break;
            }
        }
    }

    private static RuleCondition[] AppendCondition(
        IReadOnlyList<RuleCondition> conditions,
        RuleCondition condition)
    {
        var next = new RuleCondition[conditions.Count + 1];
        for (var i = 0; i < conditions.Count; i++)
        {
            next[i] = conditions[i];
        }

        next[^1] = condition;
        return next;
    }

    public bool MatchMedia(string query) => MatchMedia(query, _mediaContext);

    public bool MatchMedia(string query, MediaContext ctx)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(ctx);
        // Reuse the full CSS parser so `(...)` becomes a CssSimpleBlock.
        var sheet = CssParser.ParseStyleSheet($"@media {query} {{ }}");
        var at = sheet.Rules.OfType<AtRule>().FirstOrDefault();
        if (at is null)
        {
            return false;
        }

        var list = MediaQueryParser.ParseList(at.Prelude);
        return MediaQueryEvaluator.Evaluate(list, ctx);
    }

    public IReadOnlyDictionary<string, int> GetLayersForOrigin(StyleOrigin origin)
        => _layerOrders[origin].AllLayers;

    public ComputedStyle Compute(Element element)
        => Compute(element, context: null, cache: null);

    public ComputedStyle Compute(Element element, SelectorMatchContext? context)
        => Compute(element, context, cache: null);

    /// <summary>
    /// The animation/transition compositor wired to this engine's
    /// <see cref="AnimationEngine"/> and <see cref="TransitionEngine"/>. Lives on
    /// the <see cref="AnimationTimeline"/>, so its per-element snapshots survive
    /// relayouts (a re-cascade with the same declarations does not restart
    /// playback or re-trigger transitions).
    /// </summary>
    public AnimationCompositor Compositor => _timeline.Compositor;

    /// <summary>
    /// Compute the static cascade for <paramref name="element"/> and overlay
    /// the current animation + transition samples at clock
    /// <paramref name="nowMs"/>. Equivalent to
    /// <c>Compositor.Compose(element, Compute(element, context, cache), nowMs)</c>.
    /// Returns the static <see cref="ComputedStyle"/> unchanged when no
    /// animation or transition affects the element.
    /// </summary>
    public ComputedStyle ComputeWithAnimations(
        Element element,
        double nowMs,
        SelectorMatchContext? context = null,
        CascadeCache? cache = null)
    {
        var staticStyle = Compute(element, context, cache);
        return Compositor.Compose(element, staticStyle, nowMs);
    }

    /// <summary>
    /// Pre-cascade an entire element subtree in parallel, populating
    /// <paramref name="cache"/> so subsequent
    /// <see cref="Compute(Element, SelectorMatchContext?, CascadeCache?)"/>
    /// calls hit the cache without doing real work. This is the Stylo-style
    /// depth-breadth-first parallel cascade: level K+1 elements are
    /// independent of each other (they only depend on level K's already-
    /// cached styles), so the level can be split across cores.
    /// </summary>
    /// <remarks>
    /// Falls back to sequential when:
    /// <list type="bullet">
    ///   <item><paramref name="context"/> is non-null (pseudo-class state is
    ///   per-element; the cache is bypassed anyway).</item>
    ///   <item>A level has very few elements — the Parallel.ForEach setup
    ///   overhead outweighs the work.</item>
    /// </list>
    /// Thread safety: <see cref="CascadeCache"/> uses ConcurrentDictionary
    /// internally so concurrent element and shared writes from worker threads
    /// are safe. <c>Compute</c> itself is otherwise re-entrant — it reads
    /// <c>_sheets</c>/<c>_sheetIndexes</c>/<c>_layerOrders</c> which are not
    /// mutated during cascade.
    /// </remarks>
    public void PrecomputeTree(Element root, CascadeCache cache, SelectorMatchContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(cache);

        // Interactive cascades (hover/focus) are per-element and the cache
        // bypass kicks in inside Compute. Nothing to pre-cache.
        if (context is not null)
        {
            return;
        }

        // Group descendants by depth (relative to root). Root is level 0.
        var levels = new List<List<Element>> { new() { root } };
        FillLevels(root, depth: 1, levels);

        foreach (var level in levels)
        {
            // For tiny levels parallelism is a net loss — Parallel.ForEach
            // creates a Task per partition, queues to ThreadPool, and joins.
            // The break-even point is around a dozen items; below that, run
            // straight through.
            if (level.Count < 12)
            {
                foreach (var el in level)
                {
                    Compute(el, context: null, cache);
                }
            }
            else
            {
                Parallel.ForEach(level, el => Compute(el, context: null, cache));
            }
        }
    }

    private static void FillLevels(Element parent, int depth, List<List<Element>> levels)
    {
        while (levels.Count <= depth)
        {
            levels.Add(new List<Element>());
        }

        var bucket = levels[depth];
        foreach (var child in parent.ChildNodes)
        {
            if (child is Element el)
            {
                bucket.Add(el);
            }
        }

        foreach (var child in parent.ChildNodes)
        {
            if (child is Element el)
            {
                FillLevels(el, depth + 1, levels);
            }
        }
    }

    /// <summary>
    /// Compute styles for <paramref name="element"/>, optionally honouring an
    /// interactive <see cref="SelectorMatchContext"/> so <c>:hover</c>,
    /// <c>:focus</c>, and <c>:active</c> selectors fire. Interactive shells
    /// pass a context with <see cref="SelectorMatchContext.HoveredElement"/>
    /// (etc.) set, then ask the engine for an updated style to push to the
    /// affected view.
    /// </summary>
    /// <remarks>
    /// Pass a <see cref="CascadeCache"/> when computing styles for many
    /// elements that share ancestors (e.g. during box-tree construction) to
    /// avoid recomputing each ancestor's cascade once per descendant. The
    /// cache is bypassed when <paramref name="context"/> is non-null because
    /// pseudo-class state (<c>:hover</c>/<c>:focus</c>/<c>:active</c>) is part
    /// of the selector-match input and would otherwise contaminate entries
    /// computed under a different context.
    /// </remarks>
    /// <summary>
    /// Compute under an interactive pseudo-class context WITH a caller-owned
    /// cache. The general overloads bypass the cache under a context so a
    /// shared cache cannot mix entries from different pseudo-class states. A
    /// hover pass re-cascades a subtree plus the ancestor chain under ONE
    /// fixed context, where a private per-pass cache is safe — and saves the
    /// repeated ancestor-chain walks that made every hover toggle re-cascade
    /// the same ancestors once per affected element.
    /// </summary>
    public ComputedStyle ComputeForContext(Element element, SelectorMatchContext context, CascadeCache cache)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cache);
        var elementsStyled = 0;
        return ComputeWithAncestors(element, context, cache, ref elementsStyled);
    }

    public ComputedStyle Compute(Element element, SelectorMatchContext? context, CascadeCache? cache)
    {
        ArgumentNullException.ThrowIfNull(element);

        // Interactive cascades are skipped — see remarks. Treating the cache
        // as a miss is the safest option: it keeps callers that share a cache
        // across a hover/focus pass correct without forcing them to build a
        // separate cache for each pseudo-class state.
        var effectiveCache = context is null ? cache : null;

        try
        {
            var elementsStyled = 0;
            return ComputeWithAncestors(element, context, effectiveCache, ref elementsStyled);
        }
        catch (Exception ex)
        {
            Activity.Current?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private ComputedStyle ComputeWithAncestors(
        Element element,
        SelectorMatchContext? context,
        CascadeCache? cache,
        ref int elementsStyled)
    {
        if (cache is not null && cache.TryGet(element, out var cached))
        {
            return cached;
        }

        var parent = element.ParentNode as Element;
        var parentStyle = parent is null
            ? null
            : ComputeWithAncestors(parent, context, cache, ref elementsStyled);

        // Style sharing: the key narrows likely matches. The stored selector
        // profile must also match this element before the style can be reused.
        SharingKey? sharingKey = null;
        if (cache is not null)
        {
            sharingKey = BuildSharingKey(element, parentStyle);
            if (cache.TryGetSharedEntry(sharingKey.Value, out var shared)
                && CanReuseSharedStyle(element, shared.Validation, context))
            {
                cache.Set(element, shared.Style);
                return shared.Style;
            }
        }

        elementsStyled++;
        var validation = sharingKey is null ? null : new List<SelectorValidationResult>();
        var computed = Compute(element, parentStyle, context, validation);
        cache?.Set(element, computed);
        if (sharingKey is { } key)
        {
            cache!.SetSharedEntry(
                key,
                new SharedStyleEntry(computed, validation));
        }
        return computed;
    }

    private bool CanReuseSharedStyle(
        Element element,
        IReadOnlyList<SelectorValidationResult>? validation,
        SelectorMatchContext? context)
    {
        if (validation is null)
        {
            return false;
        }

        var matchContext = context ?? new SelectorMatchContext();
        foreach (var result in validation)
        {
            var matched = ConditionsMatch(result.Conditions, element)
                && SelectorMatcher.Matches(result.Selector, element, matchContext);
            if (matched != result.Matched)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Build the style-sharing signature for <paramref name="element"/>. The
    /// signature captures every input visible to selector matching that the
    /// sharing cache supports: tag, sorted attribute set, parent computed
    /// style (reference), and previous element sibling's tag (null when the
    /// element is the first element child — handles <c>:first-child</c> and
    /// adjacent-sibling selectors that only look one element back).
    /// </summary>
    private static SharingKey BuildSharingKey(Element element, ComputedStyle? parentStyle)
        => new(
            element.LocalName,
            SerializeAttributes(element),
            parentStyle,
            PreviousElementSiblingTag(element));

    private static string SerializeAttributes(Element element)
    {
        var count = element.Attributes.Count;
        if (count == 0)
        {
            return string.Empty;
        }
        // Sort by name so two elements with the same attributes in different
        // source order hit the same key. For attribute names + values we use
        // ordinal comparison — HTML attribute names are already lower-cased
        // by Element on set, and CSS attribute selectors are case-sensitive
        // for values unless an `i` flag is present.
        if (count == 1)
        {
            var only = element.Attributes[0];
            return only.Name + "=" + only.Value;
        }
        var pairs = new (string Name, string Value)[count];
        for (var i = 0; i < count; i++)
        {
            var a = element.Attributes[i];
            pairs[i] = (a.Name, a.Value);
        }
        Array.Sort(pairs, (a, b) => string.CompareOrdinal(a.Name, b.Name));
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < pairs.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(';');
            }

            sb.Append(pairs[i].Name);
            sb.Append('=');
            sb.Append(pairs[i].Value);
        }
        return sb.ToString();
    }

    private static string? PreviousElementSiblingTag(Element element)
    {
        for (var n = element.PreviousSibling; n is not null; n = n.PreviousSibling)
        {
            if (n is Element prev)
            {
                return prev.LocalName;
            }
        }

        return null;
    }

    public void Invalidate(Element root)
    {
        ArgumentNullException.ThrowIfNull(root);
    }

    /// <summary>
    /// Compute the cascade for a pseudo-element (<c>::before</c>/<c>::after</c>/
    /// <c>::marker</c>) of <paramref name="element"/>. Only rules whose selector
    /// targets the same pseudo-element contribute; inherited properties are
    /// resolved from the originating element's own <paramref name="elementStyle"/>
    /// (CSS Pseudo 4 §3.1 — a pseudo-element inherits from its originating
    /// element, not from the element's parent). Returns null when no rule sets a
    /// renderable <c>content</c> (so callers can skip box synthesis cheaply for
    /// the overwhelmingly common no-pseudo case).
    /// </summary>
    public ComputedStyle? ComputePseudoElement(
        Element element,
        PseudoElement pseudo,
        ComputedStyle elementStyle,
        SelectorMatchContext? baseContext = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(elementStyle);

        var b = baseContext ?? SelectorMatchContext.Default;
        var pseudoContext = new SelectorMatchContext
        {
            HoveredElement = b.HoveredElement,
            ActiveElement = b.ActiveElement,
            FocusedElement = b.FocusedElement,
            TargetElement = b.TargetElement,
            ScopeElement = b.ScopeElement,
            DocumentUrl = b.DocumentUrl,
            VisitedHrefs = b.VisitedHrefs,
            PseudoElement = pseudo,
        };
        return Compute(element, parentStyle: elementStyle, context: pseudoContext);
    }

    /// <summary>
    /// Compute a pseudo-element only when it can generate a layout box through a
    /// matching <c>content</c> declaration. CSSOM callers that need the computed
    /// pseudo style should use <see cref="ComputePseudoElement"/>.
    /// </summary>
    public ComputedStyle? ComputeGeneratedPseudoElement(
        Element element,
        PseudoElement pseudo,
        ComputedStyle elementStyle,
        SelectorMatchContext? baseContext = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(elementStyle);

        var b = baseContext ?? SelectorMatchContext.Default;
        var pseudoContext = new SelectorMatchContext
        {
            HoveredElement = b.HoveredElement,
            ActiveElement = b.ActiveElement,
            FocusedElement = b.FocusedElement,
            TargetElement = b.TargetElement,
            ScopeElement = b.ScopeElement,
            DocumentUrl = b.DocumentUrl,
            VisitedHrefs = b.VisitedHrefs,
            PseudoElement = pseudo,
        };
        return HasMatchingPseudoContentDeclaration(element, pseudo, pseudoContext)
            ? Compute(element, parentStyle: elementStyle, context: pseudoContext)
            : null;
    }

    private bool HasMatchingPseudoContentDeclaration(
        Element element,
        PseudoElement pseudo,
        SelectorMatchContext context)
    {
        var selectorScratch = new List<SelectorIndexEntry<IndexedStyleRule>>();
        var selectorScratchSeen = new HashSet<int>();

        foreach (var sheet in _sheets)
        {
            if (!_sheetIndexes.TryGetValue(sheet, out var sheetIndex))
            {
                continue;
            }

            sheetIndex.Selectors.GetCandidates(
                element,
                selectorScratch,
                selectorScratchSeen,
                filterPseudoElement: true,
                pseudoElement: pseudo);

            foreach (var entry in selectorScratch)
            {
                var indexedRule = entry.Value;
                if (!RuleDeclaresContent(indexedRule.Rule)
                    || !ConditionsMatch(indexedRule.Conditions, element)
                    || !SelectorMatcher.Matches(entry.Selector, element, context))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private static bool RuleDeclaresContent(StyleRule rule)
    {
        foreach (var declaration in rule.Declarations)
        {
            if (declaration.Name.Equals("content", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private ComputedStyle Compute(
        Element element,
        ComputedStyle? parentStyle,
        SelectorMatchContext? context = null,
        List<SelectorValidationResult>? validation = null)
    {
        context ??= new SelectorMatchContext();
        var allCandidates = new Dictionary<PropertyId, List<CascadedValue>>();
        var customCandidates = new Dictionary<string, List<CustomPropertyValue>>(StringComparer.Ordinal);
        var inheritedCustomProperties = parentStyle?.CustomProperties ?? s_emptyCustomProperties;
        var order = 0;
        var selectorScratch = new List<SelectorIndexEntry<IndexedStyleRule>>();
        var selectorScratchSeen = new HashSet<int>();

        foreach (var sheet in _sheets)
        {
            var layerOrder = _layerOrders[sheet.Origin];
            var sheetIndex = _sheetIndexes.GetValueOrDefault(sheet);
            if (sheetIndex is not null)
            {
                GatherFromIndexedRules(
                    sheetIndex,
                    sheet.Origin,
                    element,
                    allCandidates,
                    customCandidates,
                    context,
                    validation,
                    ref order,
                    layerOrder,
                    selectorScratch,
                    selectorScratchSeen);
            }
        }

        var inlineStyle = element.GetAttribute("style");
        if (!string.IsNullOrWhiteSpace(inlineStyle))
        {
            var parser = new CssParser(inlineStyle);
            var declarations = parser.ParseDeclarationList();
            AddDeclarations(
                declarations,
                StyleOrigin.Author,
                inline: true,
                new Specificity(1, 0, 0),
                allCandidates,
                customCandidates,
                ref order,
                layerIndex: LayerOrder.UnlayeredIndex,
                layerPath: null);
        }

        // CSS Logical Properties 1 §3.2: logical and physical properties that
        // resolve to the same physical edge cascade as if they were the same
        // property — declaration order across the pair determines the winner.
        // The flow-relative → physical mapping depends on the element's
        // computed `writing-mode` + `direction` (CSS Writing Modes §6), so
        // resolve those first, then copy each logical bucket's candidates into
        // its physical bucket and drop the logical bucket.
        var writingMode = ResolveKeywordEarly(PropertyId.WritingMode, allCandidates, parentStyle, "horizontal-tb");
        var direction = ResolveKeywordEarly(PropertyId.Direction, allCandidates, parentStyle, "ltr");
        MergeLogicalIntoPhysical(allCandidates, BuildLogicalToPhysical(writingMode, direction));

        // Pick winners for each property.
        var winners = new Dictionary<PropertyId, CascadedValue>();
        foreach (var kvp in allCandidates)
        {
            CascadedValue? best = null;
            foreach (var cand in kvp.Value)
            {
                if (best is null || cand.IsStrongerThan(best))
                {
                    best = cand;
                }
            }

            if (best is not null)
            {
                winners[kvp.Key] = best;
            }
        }
        var customWinners = new Dictionary<string, CustomPropertyValue>(StringComparer.Ordinal);
        foreach (var kvp in customCandidates)
        {
            CustomPropertyValue? best = null;
            foreach (var cand in kvp.Value)
            {
                if (best is null || cand.IsStrongerThan(best))
                {
                    best = cand;
                }
            }

            if (best is not null)
            {
                customWinners[kvp.Key] = best;
            }
        }

        IReadOnlyDictionary<string, IReadOnlyList<CssComponentValue>> customProperties = inheritedCustomProperties;
        if (customWinners.Count > 0)
        {
            var mergedCustomProperties = inheritedCustomProperties.Count == 0
                ? new Dictionary<string, IReadOnlyList<CssComponentValue>>(StringComparer.Ordinal)
                : inheritedCustomProperties.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal);
            foreach (var pair in customWinners)
            {
                mergedCustomProperties[pair.Key] = pair.Value.Value;
            }

            // CSS Variables L1 §3.3: a custom property whose var() references form a
            // cycle is invalid at computed-value time and computes to the
            // guaranteed-invalid value. Drop every property in a cycle from the map
            // so it reads as undefined — any var() pointing at it then takes its
            // fallback, or makes the using property invalid-at-computed-value-time.
            RemoveCyclicCustomProperties(mergedCustomProperties);
            customProperties = mergedCustomProperties;
        }

        var values = new Dictionary<PropertyId, CssValue>();
        foreach (var property in PropertyRegistry.All)
        {
            CssValue value;
            if (winners.TryGetValue(property, out var cascaded))
            {
                value = ResolveSpecialKeywords(cascaded, property, parentStyle, customProperties, allCandidates, element);
            }
            else if (PropertyRegistry.Inherits(property) && parentStyle is not null)
            {
                value = parentStyle.Get(property);
            }
            else
            {
                value = PropertyRegistry.InitialValue(property);
            }

            var resolved = ResolveReferences(value, customProperties, element);
            // CSS Variables L1 §3.2: if a var() in a non-custom property cannot be
            // substituted (the referenced custom property is undefined or
            // guaranteed-invalid and no fallback supplies a value), the declaration
            // is invalid at computed-value time and behaves as `unset` — the
            // inherited value for inherited properties, else the initial value.
            if (ContainsUnresolvedVar(resolved))
            {
                resolved = PropertyRegistry.Inherits(property) && parentStyle is not null
                    ? parentStyle.Get(property)
                    : PropertyRegistry.InitialValue(property);
            }

            values[property] = resolved;
        }

        ResolveLengths(values, parentStyle, element);

        return new ComputedStyle(values, customProperties);
    }

    /// <summary>
    /// Computed-value-time length resolution per CSS Values 4 §6. Resolves
    /// <c>em</c>/<c>rem</c>/<c>lh</c>/<c>rlh</c>/<c>cap</c>/<c>ch</c>/<c>ex</c>/<c>ic</c>,
    /// viewport units (<c>vh</c>/<c>vw</c>/<c>sv*</c>/<c>lv*</c>/<c>dv*</c>),
    /// and container units (<c>cq*</c>) to absolute pixels using
    /// <see cref="MediaContext"/> and the cascaded font-size. Reduces
    /// <see cref="CssCalc"/> trees to literals where possible. Percentages are
    /// left symbolic — they resolve at layout time against the containing block.
    /// </summary>
    private void ResolveLengths(
        Dictionary<PropertyId, CssValue> values,
        ComputedStyle? parentStyle,
        Element element)
    {
        var parentFontPx = parentStyle is not null
            ? ParentFontPx(parentStyle)
            : 16d;

        var fontPx = ResolveFontSizeValue(values[PropertyId.FontSize], parentFontPx);
        values[PropertyId.FontSize] = new CssLength(fontPx, CssLengthUnit.Px);

        var ctx = BuildResolutionContext(fontPx, values, element);

        foreach (var property in PropertyRegistry.All)
        {
            if (property == PropertyId.FontSize)
            {
                continue;
            }

            values[property] = CssCalcResolver.Resolve(values[property], ctx);
        }
    }

    private CssResolutionContext BuildResolutionContext(double fontPx, Dictionary<PropertyId, CssValue> values, Element element)
    {
        var family = ExtractFontFamily(values);
        var style = ExtractFontStyle(values);
        var weight = ExtractFontWeight(values);
        var metrics = FontMetrics.Resolve(family, fontPx, style, weight);
        var (containerW, containerH) = ResolveContainerSize(element);
        return CssResolutionContext.Default with
        {
            FontSizePx = fontPx,
            RootFontSizePx = 16d,
            LineHeightPx = fontPx * 1.2,
            RootLineHeightPx = 16d * 1.2,
            XHeightPx = metrics.XHeightPx,
            CapHeightPx = metrics.CapHeightPx,
            ZeroAdvancePx = metrics.ZeroAdvancePx,
            IcAdvancePx = metrics.IcAdvancePx,
            ViewportWidthPx = _mediaContext.ViewportWidthPx,
            ViewportHeightPx = _mediaContext.ViewportHeightPx,
            SmallViewportWidthPx = _mediaContext.ViewportWidthPx,
            SmallViewportHeightPx = _mediaContext.ViewportHeightPx,
            LargeViewportWidthPx = _mediaContext.ViewportWidthPx,
            LargeViewportHeightPx = _mediaContext.ViewportHeightPx,
            DynamicViewportWidthPx = _mediaContext.ViewportWidthPx,
            DynamicViewportHeightPx = _mediaContext.ViewportHeightPx,
            ContainerWidthPx = containerW,
            ContainerHeightPx = containerH,
        };
    }

    private (double Width, double Height) ResolveContainerSize(Element element)
    {
        if (ContainerSizeLookup is { } lookup)
        {
            for (var anc = element.ParentNode as Element; anc is not null; anc = anc.ParentNode as Element)
            {
                var size = lookup(anc);
                if (size is { } v)
                {
                    return (v.Width, v.Height);
                }
            }
        }
        // Spec-correct fallback per CSS Containment 3 §3.2 — small viewport.
        return (_mediaContext.ViewportWidthPx, _mediaContext.ViewportHeightPx);
    }

    // CSS Containment 3 §5: evaluate an `@container` size query against the
    // element's query container. The optional leading container-name is stripped.
    // Name matching is not implemented yet, so the nearest container is used. The
    // remaining condition is a size feature query evaluated against the container
    // box (reusing the media-feature evaluator with a container-sized context).
    private bool ContainerQueryMatches(MediaQueryList queryList, Element element)
    {
        var (cw, ch) = ResolveContainerSize(element);
        var ctx = _mediaContext with { ViewportWidthPx = cw, ViewportHeightPx = ch };
        return MediaQueryEvaluator.Evaluate(queryList, ctx);
    }

    private static string ExtractFontFamily(Dictionary<PropertyId, CssValue> values)
        => values[PropertyId.FontFamily] switch
        {
            CssKeyword k => k.Name,
            CssString s => s.Value,
            CssValueList list when list.Values.Count > 0 => list.Values[0] switch
            {
                CssKeyword k => k.Name,
                CssString s => s.Value,
                _ => "serif",
            },
            _ => "serif",
        };

    private static string ExtractFontStyle(Dictionary<PropertyId, CssValue> values)
        => values[PropertyId.FontStyle] is CssKeyword k ? k.Name : "normal";

    private static double ExtractFontWeight(Dictionary<PropertyId, CssValue> values)
        => values[PropertyId.FontWeight] switch
        {
            CssNumber n => n.Value,
            CssKeyword { Name: "bold" } => 700,
            CssKeyword { Name: "normal" } => 400,
            CssKeyword { Name: "lighter" } => 300,
            CssKeyword { Name: "bolder" } => 700,
            _ => 400,
        };

    private static double ParentFontPx(ComputedStyle parentStyle)
    {
        var v = parentStyle.Get(PropertyId.FontSize);
        return v is CssLength { Unit: CssLengthUnit.Px } len ? len.Value : 16d;
    }

    private double ResolveFontSizeValue(CssValue value, double parentFontPx)
    {
        var ctx = CssResolutionContext.Default with
        {
            FontSizePx = parentFontPx,
            RootFontSizePx = 16d,
            LineHeightPx = parentFontPx * 1.2,
            RootLineHeightPx = 16d * 1.2,
            XHeightPx = parentFontPx * 0.5,
            CapHeightPx = parentFontPx * 0.7,
            ZeroAdvancePx = parentFontPx * 0.5,
            IcAdvancePx = parentFontPx,
            ViewportWidthPx = _mediaContext.ViewportWidthPx,
            ViewportHeightPx = _mediaContext.ViewportHeightPx,
            SmallViewportWidthPx = _mediaContext.ViewportWidthPx,
            SmallViewportHeightPx = _mediaContext.ViewportHeightPx,
            LargeViewportWidthPx = _mediaContext.ViewportWidthPx,
            LargeViewportHeightPx = _mediaContext.ViewportHeightPx,
            DynamicViewportWidthPx = _mediaContext.ViewportWidthPx,
            DynamicViewportHeightPx = _mediaContext.ViewportHeightPx,
            PercentageBasisPx = parentFontPx,
        };
        var resolved = CssCalcResolver.Resolve(value, ctx);
        return resolved switch
        {
            CssLength { Unit: CssLengthUnit.Px } len => len.Value,
            CssNumber n => n.Value,
            _ => parentFontPx,
        };
    }

    private void RegisterLayers(IReadOnlyList<CssRule> rules, StyleOrigin origin, string? currentPath)
    {
        foreach (var rule in rules)
        {
            if (rule is AtRule at && at.Name.Equals("layer", StringComparison.OrdinalIgnoreCase))
            {
                var paths = ParseLayerNamesFromPrelude(at.Prelude);
                if (at.Rules.Count == 0 && at.Declarations.Count == 0)
                {
                    // Statement form: `@layer a, b, c;` — just registers names.
                    foreach (var p in paths)
                    {
                        _layerOrders[origin].RegisterLayer(Combine(currentPath, p));
                    }
                }
                else if (paths.Count == 0)
                {
                    // Anonymous block — register an anonymous layer for ordering, recurse.
                    var anon = "__anon" + Guid.NewGuid().ToString("N");
                    var path = Combine(currentPath, anon);
                    _layerOrders[origin].RegisterLayer(path);
                    RegisterLayers(at.Rules, origin, path);
                }
                else
                {
                    var path = Combine(currentPath, paths[0]);
                    _layerOrders[origin].RegisterLayer(path);
                    RegisterLayers(at.Rules, origin, path);
                }
            }
            else if (rule is AtRule { Name: "media" or "supports" } container)
            {
                RegisterLayers(container.Rules, origin, currentPath);
            }
        }
    }

    private static List<string> ParseLayerNamesFromPrelude(IReadOnlyList<CssComponentValue> prelude)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        void Flush()
        {
            var s = current.ToString().Trim();
            if (s.Length > 0)
            {
                result.Add(s);
            }

            current.Clear();
        }
        foreach (var v in prelude)
        {
            if (v is CssTokenValue tv)
            {
                if (tv.Token.Type == CssTokenType.Comma) { Flush(); continue; }
                if (tv.Token.Type == CssTokenType.Whitespace)
                {
                    continue;
                }

                if (tv.Token.Type == CssTokenType.Ident)
                {
                    current.Append(tv.Token.Value);
                }
                else if (tv.Token.Type == CssTokenType.Delim && tv.Token.Delimiter == '.')
                {
                    current.Append('.');
                }
            }
        }
        Flush();
        return result;
    }

    private static string Combine(string? parent, string child)
        => string.IsNullOrEmpty(parent) ? child : parent + "." + child;

    private void GatherFromIndexedRules(
        SheetIndex sheetIndex,
        StyleOrigin origin,
        Element element,
        Dictionary<PropertyId, List<CascadedValue>> candidates,
        Dictionary<string, List<CustomPropertyValue>> customCandidates,
        SelectorMatchContext? context,
        List<SelectorValidationResult>? validation,
        ref int order,
        LayerOrder layerOrder,
        List<SelectorIndexEntry<IndexedStyleRule>> selectorScratch,
        HashSet<int> selectorScratchSeen)
    {
        sheetIndex.Selectors.GetCandidates(
            element,
            selectorScratch,
            selectorScratchSeen,
            filterPseudoElement: true,
            pseudoElement: context?.PseudoElement);

        IndexedStyleRule? currentRule = null;
        bool currentConditionsMatched = false;
        int currentLayerIndex = LayerOrder.UnlayeredIndex;

        foreach (var entry in selectorScratch)
        {
            var indexedRule = entry.Value;
            if (!ReferenceEquals(indexedRule, currentRule))
            {
                currentRule = indexedRule;
                currentConditionsMatched = ConditionsMatch(indexedRule.Conditions, element);
                currentLayerIndex = currentConditionsMatched
                    ? layerOrder.GetIndex(indexedRule.LayerPath)
                    : LayerOrder.UnlayeredIndex;
            }

            if (!currentConditionsMatched)
            {
                validation?.Add(new SelectorValidationResult(
                    entry.Selector,
                    indexedRule.Conditions,
                    Matched: false));
                continue;
            }

            var selectorMatched = SelectorMatcher.Matches(entry.Selector, element, context);
            validation?.Add(new SelectorValidationResult(
                entry.Selector,
                indexedRule.Conditions,
                selectorMatched));
            if (!selectorMatched)
            {
                continue;
            }

            AddDeclarations(
                indexedRule.Rule.Declarations,
                origin,
                inline: false,
                entry.Selector.Specificity,
                candidates,
                customCandidates,
                ref order,
                currentLayerIndex,
                indexedRule.LayerPath);
        }
    }

    private bool ConditionsMatch(IReadOnlyList<RuleCondition> conditions, Element element)
    {
        foreach (var condition in conditions)
        {
            switch (condition.Kind)
            {
                case RuleConditionKind.Media:
                    if (condition.QueryList is null ||
                        !MediaQueryEvaluator.Evaluate(condition.QueryList, _mediaContext))
                    {
                        return false;
                    }

                    break;
                case RuleConditionKind.Supports:
                    if (!condition.SupportsResult)
                    {
                        return false;
                    }

                    break;
                case RuleConditionKind.Container:
                    if (condition.QueryList is null || !ContainerQueryMatches(condition.QueryList, element))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    private static SelectorList ResolveSelectorList(
        IReadOnlyList<CssComponentValue> prelude,
        SelectorList? parentSelectors)
    {
        if (parentSelectors is null)
        {
            return SelectorParser.ParseSelectorList(prelude);
        }

        // CSS Nesting 1 §3: textually desugar `&` and implicit-`&` against parent selectors,
        // then reparse via SelectorParser.
        var rawText = ComponentValuesToText(prelude).Trim();
        var parentText = SelectorListToText(parentSelectors);
        // Split on top-level commas to handle `& .a, & .b` correctly.
        var pieces = SplitTopLevelCommas(rawText);
        var rebuilt = new System.Text.StringBuilder();
        for (var i = 0; i < pieces.Count; i++)
        {
            if (i > 0)
            {
                rebuilt.Append(", ");
            }

            var piece = pieces[i].Trim();
            if (piece.Contains('&', StringComparison.Ordinal))
            {
                rebuilt.Append(piece.Replace("&", $":is({parentText})", StringComparison.Ordinal));
            }
            else
            {
                rebuilt.Append($":is({parentText}) {piece}");
            }
        }
        return SelectorParser.ParseSelectorList(rebuilt.ToString());
    }

    private static List<string> SplitTopLevelCommas(string text)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var c in text)
        {
            if (c == '(' || c == '[')
            {
                depth++;
            }
            else if (c == ')' || c == ']')
            {
                depth = Math.Max(0, depth - 1);
            }

            if (c == ',' && depth == 0)
            {
                result.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }

    private static string ComponentValuesToText(IReadOnlyList<CssComponentValue> values)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var v in values)
        {
            AppendComponentValueText(sb, v);
        }

        return sb.ToString();
    }

    private static void AppendComponentValueText(System.Text.StringBuilder sb, CssComponentValue value)
    {
        switch (value)
        {
            case CssTokenValue tv:
                AppendTokenText(sb, tv.Token);
                break;
            case CssFunction fn:
                sb.Append(fn.Name).Append('(');
                foreach (var v in fn.Values)
                {
                    AppendComponentValueText(sb, v);
                }

                sb.Append(')');
                break;
            case CssSimpleBlock block:
                sb.Append(block.StartToken switch
                {
                    CssTokenType.LeftParen => '(',
                    CssTokenType.LeftSquare => '[',
                    CssTokenType.LeftBrace => '{',
                    _ => '(',
                });
                foreach (var v in block.Values)
                {
                    AppendComponentValueText(sb, v);
                }

                sb.Append(block.StartToken switch
                {
                    CssTokenType.LeftParen => ')',
                    CssTokenType.LeftSquare => ']',
                    CssTokenType.LeftBrace => '}',
                    _ => ')',
                });
                break;
        }
    }

    private static void AppendTokenText(System.Text.StringBuilder sb, CssToken token)
    {
        switch (token.Type)
        {
            case CssTokenType.Ident: sb.Append(token.Value); break;
            case CssTokenType.Hash: sb.Append('#').Append(token.Value); break;
            case CssTokenType.String: sb.Append('"').Append(token.Value).Append('"'); break;
            case CssTokenType.Number: sb.Append(token.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
            case CssTokenType.Percentage: sb.Append(token.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('%'); break;
            case CssTokenType.Dimension: sb.Append(token.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(token.Unit); break;
            case CssTokenType.Delim: sb.Append(token.Delimiter); break;
            case CssTokenType.Whitespace: sb.Append(' '); break;
            case CssTokenType.Colon: sb.Append(':'); break;
            case CssTokenType.Semicolon: sb.Append(';'); break;
            case CssTokenType.Comma: sb.Append(','); break;
            case CssTokenType.Url: sb.Append("url(").Append(token.Value).Append(')'); break;
        }
    }

    private static string SelectorListToText(SelectorList list)
        => string.Join(", ", list.Selectors.Select(ComplexSelectorToText));

    private static string ComplexSelectorToText(ComplexSelector selector)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < selector.Parts.Count; i++)
        {
            var part = selector.Parts[i];
            if (i > 0)
            {
                sb.Append(part.CombinatorFromPrevious switch
                {
                    SelectorCombinator.Descendant => " ",
                    SelectorCombinator.Child => " > ",
                    SelectorCombinator.NextSibling => " + ",
                    SelectorCombinator.SubsequentSibling => " ~ ",
                    _ => " ",
                });
            }
            sb.Append(CompoundToText(part.Compound));
        }
        return sb.ToString();
    }

    private static string CompoundToText(CompoundSelector compound)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var simple in compound.SimpleSelectors)
        {
            switch (simple)
            {
                case TypeSelector t: sb.Append(t.LocalName); break;
                case UniversalSelector: sb.Append('*'); break;
                case IdSelector i: sb.Append('#').Append(i.Id); break;
                case ClassSelector c: sb.Append('.').Append(c.ClassName); break;
                case AttributeSelector a:
                    sb.Append('[').Append(a.Name);
                    if (a.Operator != AttributeOperator.Exists)
                    {
                        sb.Append(a.Operator switch
                        {
                            AttributeOperator.Equals => "=",
                            AttributeOperator.Includes => "~=",
                            AttributeOperator.DashMatch => "|=",
                            AttributeOperator.Prefix => "^=",
                            AttributeOperator.Suffix => "$=",
                            AttributeOperator.Substring => "*=",
                            _ => "=",
                        });
                        sb.Append('"').Append(a.Value).Append('"');
                    }
                    sb.Append(']');
                    break;
                case PseudoClassSelector pc:
                    sb.Append(':').Append(pc.Name);
                    if (pc.Argument is SelectorList sl)
                    {
                        sb.Append('(').Append(SelectorListToText(sl)).Append(')');
                    }
                    else if (pc.Argument is NthPattern np)
                    {
                        sb.Append('(').Append(np.A).Append('n').Append('+').Append(np.B).Append(')');
                    }
                    else if (pc.Argument is string s)
                    {
                        sb.Append('(').Append(s).Append(')');
                    }

                    break;
                case PseudoElementSelector pe:
                    sb.Append("::").Append(pe.Name);
                    break;
            }
        }
        return sb.ToString();
    }

    private static void AddDeclarations(
        IReadOnlyList<CssDeclaration> declarations,
        StyleOrigin origin,
        bool inline,
        Specificity specificity,
        Dictionary<PropertyId, List<CascadedValue>> candidates,
        Dictionary<string, List<CustomPropertyValue>> customCandidates,
        ref int order,
        int layerIndex,
        string? layerPath = null)
    {
        foreach (var declaration in declarations)
        {
            var currentOrder = order++;
            if (declaration.Name.StartsWith("--", StringComparison.Ordinal))
            {
                var custom = new CustomPropertyValue(
                    declaration.Value,
                    declaration.Important,
                    origin,
                    inline,
                    specificity,
                    currentOrder,
                    layerIndex,
                    layerPath);
                if (!customCandidates.TryGetValue(declaration.Name, out var list))
                {
                    customCandidates[declaration.Name] = list = new List<CustomPropertyValue>();
                }

                list.Add(custom);
                continue;
            }

            // `all: <wide-keyword>` — expand to every property.
            if (declaration.Name.Equals("all", StringComparison.OrdinalIgnoreCase) &&
                TryParseWideKeyword(declaration.Value, out var allKeyword))
            {
                foreach (var p in PropertyRegistry.All)
                {
                    var candidate = new CascadedValue(
                        new CssKeyword(allKeyword),
                        declaration.Important,
                        origin,
                        inline,
                        specificity,
                        currentOrder,
                        layerIndex,
                        layerPath);
                    if (!candidates.TryGetValue(p, out var list))
                    {
                        candidates[p] = list = new List<CascadedValue>();
                    }

                    list.Add(candidate);
                }
                continue;
            }

            foreach (var parsed in PropertyRegistry.Parse(declaration))
            {
                var candidate = new CascadedValue(
                    parsed.Value,
                    parsed.Important,
                    origin,
                    inline,
                    specificity,
                    currentOrder,
                    layerIndex,
                    layerPath);
                if (!candidates.TryGetValue(parsed.Id, out var list))
                {
                    candidates[parsed.Id] = list = new List<CascadedValue>();
                }

                list.Add(candidate);
            }
        }
    }

    private static bool TryParseWideKeyword(IReadOnlyList<CssComponentValue> values, out string keyword)
    {
        keyword = string.Empty;
        var first = values.FirstOrDefault(v => v is not CssTokenValue { Token.Type: CssTokenType.Whitespace });
        if (first is CssTokenValue { Token.Type: CssTokenType.Ident } tok)
        {
            var name = tok.Token.Value.ToLowerInvariant();
            if (name is "initial" or "inherit" or "unset" or "revert" or "revert-layer")
            {
                keyword = name;
                return true;
            }
        }
        return false;
    }

    private static CssValue ResolveSpecialKeywords(
        CascadedValue cascaded,
        PropertyId property,
        ComputedStyle? parentStyle,
        IReadOnlyDictionary<string, IReadOnlyList<CssComponentValue>> customProperties,
        Dictionary<PropertyId, List<CascadedValue>> allCandidates,
        Element element)
    {
        var value = ResolveReferences(cascaded.Value, customProperties, element);
        switch (value)
        {
            case CssKeyword { Name: "inherit" } when parentStyle is not null:
                return parentStyle.Get(property);
            case CssKeyword { Name: "initial" }:
                return PropertyRegistry.InitialValue(property);
            case CssKeyword { Name: "unset" } when PropertyRegistry.Inherits(property) && parentStyle is not null:
                return parentStyle.Get(property);
            case CssKeyword { Name: "unset" }:
                return PropertyRegistry.InitialValue(property);
            case CssKeyword { Name: "revert" }:
                return ResolveRevert(property, cascaded, parentStyle, allCandidates, element, sameOriginOnly: false);
            case CssKeyword { Name: "revert-layer" }:
                return ResolveRevert(property, cascaded, parentStyle, allCandidates, element, sameOriginOnly: true);
            default:
                return value;
        }
    }

    private static CssValue ResolveRevert(
        PropertyId property,
        CascadedValue current,
        ComputedStyle? parentStyle,
        Dictionary<PropertyId, List<CascadedValue>> allCandidates,
        Element element,
        bool sameOriginOnly)
    {
        if (!allCandidates.TryGetValue(property, out var list))
        {
            return DefaultForProperty(property, parentStyle);
        }

        CascadedValue? best = null;
        foreach (var cand in list)
        {
            if (sameOriginOnly)
            {
                // revert-layer: same origin, earlier layer (or different importance).
                if (cand.Origin != current.Origin)
                {
                    continue;
                }

                if (cand.Important != current.Important)
                {
                    continue;
                }
                // strictly weaker than current in cascade ordering.
                if (cand.IsStrongerThan(current) || cand.SameAs(current))
                {
                    continue;
                }
            }
            else
            {
                // revert: previous origin (lower origin rank than current).
                if (OriginRank(cand.Origin, cand.Important) >= OriginRank(current.Origin, current.Important))
                {
                    continue;
                }
            }
            if (best is null || cand.IsStrongerThan(best))
            {
                best = cand;
            }
        }
        if (best is null)
        {
            return DefaultForProperty(property, parentStyle);
        }

        var inner = ResolveSpecialKeywords(best, property, parentStyle, parentStyle?.CustomProperties ?? new Dictionary<string, IReadOnlyList<CssComponentValue>>(StringComparer.Ordinal), allCandidates, element);
        return inner;
    }

    private static CssValue DefaultForProperty(PropertyId property, ComputedStyle? parentStyle)
    {
        if (PropertyRegistry.Inherits(property) && parentStyle is not null)
        {
            return parentStyle.Get(property);
        }

        return PropertyRegistry.InitialValue(property);
    }

    private static CssValue ResolveReferences(
        CssValue value,
        IReadOnlyDictionary<string, IReadOnlyList<CssComponentValue>> customProperties,
        Element element)
        => value switch
        {
            CssPendingSubstitution pending => ResolvePendingSubstitution(pending, customProperties, element),
            CssVarReference var when customProperties.TryGetValue(var.Name, out var tokens) =>
                ResolveReferences(CssValueParser.Parse(tokens), customProperties, element),
            CssVarReference { Fallback: not null } var => ResolveReferences(var.Fallback, customProperties, element),
            CssAttrReference attr => attr.Resolve(element.GetAttribute)
                ?? (attr.Fallback is null ? value : ResolveReferences(attr.Fallback, customProperties, element)),
            CssValueList list => new CssValueList(list.Values.Select(v => ResolveReferences(v, customProperties, element)).ToList()),
            CssFunctionValue function => new CssFunctionValue(
                function.Name,
                function.Arguments.Select(v => ResolveReferences(v, customProperties, element)).ToList()),
            _ => value,
        };

    /// <summary>
    /// True if <paramref name="value"/> still carries a <see cref="CssVarReference"/>
    /// after substitution — i.e. a var() that pointed at an undefined or
    /// guaranteed-invalid custom property and had no usable fallback. Per CSS
    /// Variables L1 §3.2 the using declaration is then invalid at computed-value
    /// time.
    /// </summary>
    private static bool ContainsUnresolvedVar(CssValue value)
        => value switch
        {
            CssVarReference => true,
            CssValueList list => list.Values.Any(ContainsUnresolvedVar),
            CssFunctionValue function => function.Arguments.Any(ContainsUnresolvedVar),
            _ => false,
        };

    /// <summary>
    /// CSS Variables L1 §3.3: detect cycles in the custom-property dependency
    /// graph and remove every custom property that takes part in one. Each edge
    /// runs from a custom property to every other custom property named by a
    /// var() anywhere in its token stream (including inside fallbacks — a var()
    /// in a fallback still counts as a reference per the §3.3 cycle algorithm).
    /// A property on a cycle (including a direct self-reference) computes to the
    /// guaranteed-invalid value, so dropping it from the map makes it read as
    /// undefined for the rest of substitution.
    /// </summary>
    private static void RemoveCyclicCustomProperties(
        Dictionary<string, IReadOnlyList<CssComponentValue>> customProperties)
    {
        if (customProperties.Count == 0)
        {
            return;
        }

        // Build the dependency edges once. Only references to names that are
        // actually defined can form a cycle.
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (name, tokens) in customProperties)
        {
            var refs = new HashSet<string>(StringComparer.Ordinal);
            CollectVarReferences(tokens, refs);
            refs.IntersectWith(customProperties.Keys);
            edges[name] = refs;
        }

        // Iterative DFS with three colours (white/grey/black) so we can flag
        // any node that reaches a grey node — that back-edge is the cycle.
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0=white,1=grey,2=black
        var onCycle = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in edges.Keys)
        {
            if (state.GetValueOrDefault(start) != 0)
            {
                continue;
            }

            var stack = new Stack<(string Node, IEnumerator<string> Edges)>();
            state[start] = 1;
            stack.Push((start, edges[start].GetEnumerator()));
            while (stack.Count > 0)
            {
                var (node, it) = stack.Peek();
                if (it.MoveNext())
                {
                    var next = it.Current;
                    var color = state.GetValueOrDefault(next);
                    if (color == 1)
                    {
                        // Back-edge: `next` and every grey node above it on the
                        // stack (down to `next`) sit on the cycle.
                        onCycle.Add(next);
                        foreach (var frame in stack)
                        {
                            onCycle.Add(frame.Node);
                            if (string.Equals(frame.Node, next, StringComparison.Ordinal))
                            {
                                break;
                            }
                        }
                    }
                    else if (color == 0)
                    {
                        state[next] = 1;
                        stack.Push((next, edges[next].GetEnumerator()));
                    }
                }
                else
                {
                    state[node] = 2;
                    stack.Pop();
                }
            }
        }

        foreach (var name in onCycle)
        {
            customProperties.Remove(name);
        }
    }

    /// <summary>Collect the names of every custom property referenced by a
    /// <c>var()</c> anywhere in <paramref name="values"/> (recursing into
    /// functions and blocks, including var() fallbacks).</summary>
    private static void CollectVarReferences(
        IReadOnlyList<CssComponentValue> values,
        HashSet<string> into)
    {
        foreach (var cv in values)
        {
            switch (cv)
            {
                case CssFunction func when func.Name.Equals("var", StringComparison.OrdinalIgnoreCase):
                    foreach (var arg in func.Values)
                    {
                        if (arg is CssTokenValue { Token.Type: CssTokenType.Ident } tv
                            && tv.Token.Value.StartsWith("--", StringComparison.Ordinal))
                        {
                            into.Add(tv.Token.Value);
                            break; // first ident is the referenced name
                        }
                        if (arg is CssTokenValue { Token.Type: CssTokenType.Whitespace })
                        {
                            continue;
                        }

                        break;
                    }
                    // The fallback (anything past the first comma) may also name
                    // custom properties — recurse so those edges are seen too.
                    CollectVarReferences(func.Values, into);
                    break;
                case CssFunction func:
                    CollectVarReferences(func.Values, into);
                    break;
                case CssSimpleBlock block:
                    CollectVarReferences(block.Values, into);
                    break;
            }
        }
    }

    /// <summary>
    /// CSS Variables L1 §3.7: resolve a pending-substitution value. Substitutes
    /// var() references in the original shorthand components, re-runs the
    /// shorthand expander, and returns the value for the longhand this placeholder
    /// stands for. If the resolved shorthand doesn't populate that longhand
    /// (either because resolution produced no value for it or because a var()
    /// remained unresolvable), the longhand falls back to its initial value.
    /// </summary>
    private static CssValue ResolvePendingSubstitution(
        CssPendingSubstitution pending,
        IReadOnlyDictionary<string, IReadOnlyList<CssComponentValue>> customProperties,
        Element element)
    {
        // Substitute var() in each component. A multi-component custom property
        // value (e.g. `--side: 1px solid red`) parses as a CssValueList; per
        // §3.7's "substitute the tokens" semantics those components splice into
        // the surrounding shorthand context rather than appearing as a single
        // nested list.
        var resolved = new List<CssValue>(pending.Values.Count);
        foreach (var v in pending.Values)
        {
            var r = ResolveReferences(v, customProperties, element);
            if (r is CssValueList nested)
            {
                resolved.AddRange(nested.Values);
            }
            else
            {
                resolved.Add(r);
            }
        }

        foreach (var decl in PropertyRegistry.ExpandResolved(pending.Shorthand, resolved, important: false))
        {
            if (decl.Id == pending.Longhand)
            {
                return decl.Value;
            }
        }
        return PropertyRegistry.InitialValue(pending.Longhand);
    }

    // Physical sides indexed 0=Top, 1=Right, 2=Bottom, 3=Left.
    private static readonly PropertyId[] MarginSides = [PropertyId.MarginTop, PropertyId.MarginRight, PropertyId.MarginBottom, PropertyId.MarginLeft];
    private static readonly PropertyId[] PaddingSides = [PropertyId.PaddingTop, PropertyId.PaddingRight, PropertyId.PaddingBottom, PropertyId.PaddingLeft];
    private static readonly PropertyId[] InsetSides = [PropertyId.Top, PropertyId.Right, PropertyId.Bottom, PropertyId.Left];
    private static readonly PropertyId[] BorderWidthSides = [PropertyId.BorderTopWidth, PropertyId.BorderRightWidth, PropertyId.BorderBottomWidth, PropertyId.BorderLeftWidth];
    private static readonly PropertyId[] BorderStyleSides = [PropertyId.BorderTopStyle, PropertyId.BorderRightStyle, PropertyId.BorderBottomStyle, PropertyId.BorderLeftStyle];
    private static readonly PropertyId[] BorderColorSides = [PropertyId.BorderTopColor, PropertyId.BorderRightColor, PropertyId.BorderBottomColor, PropertyId.BorderLeftColor];

    // CSS Logical Properties 1 §2-§4 / CSS Writing Modes §6: resolve the
    // flow-relative → physical mapping for a given `writing-mode` + `direction`.
    // For horizontal-tb + ltr this reproduces the classic LTR table exactly.
    private static Dictionary<PropertyId, PropertyId> BuildLogicalToPhysical(string writingMode, string direction)
    {
        var vertical = writingMode.StartsWith("vertical", StringComparison.OrdinalIgnoreCase)
            || writingMode.StartsWith("sideways", StringComparison.OrdinalIgnoreCase);
        var rl = writingMode is "vertical-rl" or "sideways-rl";
        var ltr = !direction.Equals("rtl", StringComparison.OrdinalIgnoreCase);

        // Block axis: horizontal-tb → top/bottom; vertical-rl → right/left; vertical-lr → left/right.
        int blockStart, blockEnd;
        if (!vertical) { blockStart = 0; blockEnd = 2; }
        else if (rl) { blockStart = 1; blockEnd = 3; }
        else { blockStart = 3; blockEnd = 1; }

        // Inline axis: horizontal modes run left/right; vertical modes run top/bottom.
        // `direction` orients start/end along the inline axis.
        int inlineStart, inlineEnd;
        if (!vertical) { (inlineStart, inlineEnd) = ltr ? (3, 1) : (1, 3); }
        else { (inlineStart, inlineEnd) = ltr ? (0, 2) : (2, 0); }

        var map = new Dictionary<PropertyId, PropertyId>
        {
            [PropertyId.MarginBlockStart] = MarginSides[blockStart],
            [PropertyId.MarginBlockEnd] = MarginSides[blockEnd],
            [PropertyId.MarginInlineStart] = MarginSides[inlineStart],
            [PropertyId.MarginInlineEnd] = MarginSides[inlineEnd],
            [PropertyId.PaddingBlockStart] = PaddingSides[blockStart],
            [PropertyId.PaddingBlockEnd] = PaddingSides[blockEnd],
            [PropertyId.PaddingInlineStart] = PaddingSides[inlineStart],
            [PropertyId.PaddingInlineEnd] = PaddingSides[inlineEnd],
            [PropertyId.InsetBlockStart] = InsetSides[blockStart],
            [PropertyId.InsetBlockEnd] = InsetSides[blockEnd],
            [PropertyId.InsetInlineStart] = InsetSides[inlineStart],
            [PropertyId.InsetInlineEnd] = InsetSides[inlineEnd],
            [PropertyId.BorderBlockStartWidth] = BorderWidthSides[blockStart],
            [PropertyId.BorderBlockEndWidth] = BorderWidthSides[blockEnd],
            [PropertyId.BorderInlineStartWidth] = BorderWidthSides[inlineStart],
            [PropertyId.BorderInlineEndWidth] = BorderWidthSides[inlineEnd],
            [PropertyId.BorderBlockStartStyle] = BorderStyleSides[blockStart],
            [PropertyId.BorderBlockEndStyle] = BorderStyleSides[blockEnd],
            [PropertyId.BorderInlineStartStyle] = BorderStyleSides[inlineStart],
            [PropertyId.BorderInlineEndStyle] = BorderStyleSides[inlineEnd],
            [PropertyId.BorderBlockStartColor] = BorderColorSides[blockStart],
            [PropertyId.BorderBlockEndColor] = BorderColorSides[blockEnd],
            [PropertyId.BorderInlineStartColor] = BorderColorSides[inlineStart],
            [PropertyId.BorderInlineEndColor] = BorderColorSides[inlineEnd],
            // Corner radii: each logical corner = the physical corner touching both named sides.
            [PropertyId.BorderStartStartRadius] = CornerRadius(blockStart, inlineStart),
            [PropertyId.BorderStartEndRadius] = CornerRadius(blockStart, inlineEnd),
            [PropertyId.BorderEndStartRadius] = CornerRadius(blockEnd, inlineStart),
            [PropertyId.BorderEndEndRadius] = CornerRadius(blockEnd, inlineEnd),
            // Sizing: inline-size is width in horizontal modes, height in vertical (block swapped).
            [PropertyId.InlineSize] = vertical ? PropertyId.Height : PropertyId.Width,
            [PropertyId.BlockSize] = vertical ? PropertyId.Width : PropertyId.Height,
            [PropertyId.MinInlineSize] = vertical ? PropertyId.MinHeight : PropertyId.MinWidth,
            [PropertyId.MinBlockSize] = vertical ? PropertyId.MinWidth : PropertyId.MinHeight,
            [PropertyId.MaxInlineSize] = vertical ? PropertyId.MaxHeight : PropertyId.MaxWidth,
            [PropertyId.MaxBlockSize] = vertical ? PropertyId.MaxWidth : PropertyId.MaxHeight,
        };
        return map;
    }

    // The physical corner-radius property at the intersection of two physical
    // sides (one vertical: Top/Bottom, one horizontal: Left/Right).
    private static PropertyId CornerRadius(int sideA, int sideB)
    {
        var top = sideA == 0 || sideB == 0;
        var left = sideA == 3 || sideB == 3;
        var right = sideA == 1 || sideB == 1;
        if (top)
        {
            return left ? PropertyId.BorderTopLeftRadius : PropertyId.BorderTopRightRadius;
        }

        return right ? PropertyId.BorderBottomRightRadius : PropertyId.BorderBottomLeftRadius;
    }

    // Resolve a keyword-valued, inherited property (writing-mode / direction)
    // before final winner selection: pick the strongest candidate, else inherit
    // from the parent, else the given initial keyword.
    private static string ResolveKeywordEarly(
        PropertyId id,
        Dictionary<PropertyId, List<CascadedValue>> allCandidates,
        ComputedStyle? parentStyle,
        string initial)
    {
        if (allCandidates.TryGetValue(id, out var list) && list.Count > 0)
        {
            CascadedValue? best = null;
            foreach (var c in list)
            {
                if (best is null || c.IsStrongerThan(best))
                {
                    best = c;
                }
            }

            if (best!.Value is CssKeyword k && !IsCssWideKeyword(k.Name))
            {
                return k.Name;
            }
        }
        if (parentStyle is not null && parentStyle.Get(id) is CssKeyword pk)
        {
            return pk.Name;
        }

        return initial;
    }

    private static bool IsCssWideKeyword(string name)
        => name is "inherit" or "initial" or "unset" or "revert" or "revert-layer";

    private static void MergeLogicalIntoPhysical(
        Dictionary<PropertyId, List<CascadedValue>> allCandidates,
        Dictionary<PropertyId, PropertyId> logicalToPhysical)
    {
        foreach (var (logical, physical) in logicalToPhysical)
        {
            if (!allCandidates.TryGetValue(logical, out var logicalList) || logicalList.Count == 0)
            {
                continue;
            }

            if (!allCandidates.TryGetValue(physical, out var physicalList))
            {
                allCandidates[physical] = physicalList = new List<CascadedValue>();
            }

            physicalList.AddRange(logicalList);
            allCandidates.Remove(logical);
        }
    }

    private sealed record CascadedValue(
        CssValue Value,
        bool Important,
        StyleOrigin Origin,
        bool Inline,
        Specificity Specificity,
        int Order,
        int LayerIndex,
        string? LayerPath = null)
    {
        public bool IsStrongerThan(CascadedValue other)
        {
            var origin = OriginRank(Origin, Important).CompareTo(OriginRank(other.Origin, other.Important));
            if (origin != 0)
            {
                return origin > 0;
            }

            if (Inline != other.Inline)
            {
                return Inline;
            }
            // Layer: per spec, layered styles are weaker than unlayered (non-important);
            // for !important the order is inverted.
            var layer = LayerOrder.Compare(LayerPath, LayerIndex, other.LayerPath, other.LayerIndex);
            if (layer != 0)
            {
                return Important ? layer < 0 : layer > 0;
            }

            var specificity = Specificity.CompareTo(other.Specificity);
            if (specificity != 0)
            {
                return specificity > 0;
            }

            return Order > other.Order;
        }

        public bool SameAs(CascadedValue other) => Order == other.Order;
    }

    private sealed record CustomPropertyValue(
        IReadOnlyList<CssComponentValue> Value,
        bool Important,
        StyleOrigin Origin,
        bool Inline,
        Specificity Specificity,
        int Order,
        int LayerIndex,
        string? LayerPath = null)
    {
        public bool IsStrongerThan(CustomPropertyValue other)
        {
            var origin = OriginRank(Origin, Important).CompareTo(OriginRank(other.Origin, other.Important));
            if (origin != 0)
            {
                return origin > 0;
            }

            if (Inline != other.Inline)
            {
                return Inline;
            }

            var layer = LayerOrder.Compare(LayerPath, LayerIndex, other.LayerPath, other.LayerIndex);
            if (layer != 0)
            {
                return Important ? layer < 0 : layer > 0;
            }

            var specificity = Specificity.CompareTo(other.Specificity);
            if (specificity != 0)
            {
                return specificity > 0;
            }

            return Order > other.Order;
        }
    }

    private static int OriginRank(StyleOrigin origin, bool important)
        => (origin, important) switch
        {
            (StyleOrigin.UserAgent, false) => 0,
            (StyleOrigin.User, false) => 1,
            (StyleOrigin.Author, false) => 2,
            (StyleOrigin.Author, true) => 3,
            (StyleOrigin.User, true) => 4,
            (StyleOrigin.UserAgent, true) => 5,
            _ => 0,
        };
}

internal static partial class StyleEngineLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "dropping rule with invalid selector: {Reason}")]
    public static partial void InvalidSelector(ILogger logger, string reason);
}

internal enum RuleConditionKind
{
    Media,
    Supports,
    Container,
}

internal sealed class RuleCondition
{
    public RuleCondition(RuleConditionKind kind, AtRule rule)
    {
        Kind = kind;

        switch (kind)
        {
            case RuleConditionKind.Media:
                QueryList = TryParseMediaQueryList(rule.Prelude);
                break;
            case RuleConditionKind.Supports:
                SupportsResult = TryEvaluateSupports(rule.Prelude);
                break;
            case RuleConditionKind.Container:
                QueryList = TryParseContainerQueryList(rule.Prelude);
                break;
        }
    }

    public RuleConditionKind Kind { get; }
    public MediaQueryList? QueryList { get; }
    public bool SupportsResult { get; }

    private static MediaQueryList? TryParseMediaQueryList(IReadOnlyList<CssComponentValue> values)
    {
        try
        {
            return MediaQueryParser.ParseList(values);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryEvaluateSupports(IReadOnlyList<CssComponentValue> values)
    {
        try
        {
            return SupportsEvaluator.Evaluate(values);
        }
        catch
        {
            return false;
        }
    }

    private static MediaQueryList? TryParseContainerQueryList(IReadOnlyList<CssComponentValue> prelude)
    {
        var conditionStart = 0;
        for (var i = 0; i < prelude.Count; i++)
        {
            if (prelude[i] is CssTokenValue { Token.Type: CssTokenType.Whitespace })
            {
                conditionStart = i + 1;
                continue;
            }

            if (prelude[i] is CssTokenValue { Token: { Type: CssTokenType.Ident, Value: var ident } }
                && !ident.Equals("not", StringComparison.OrdinalIgnoreCase))
            {
                conditionStart = i + 1;
            }

            break;
        }

        if (conditionStart >= prelude.Count)
        {
            return null;
        }

        var condition = new List<CssComponentValue>(prelude.Count - conditionStart);
        for (var i = conditionStart; i < prelude.Count; i++)
        {
            condition.Add(prelude[i]);
        }

        return TryParseMediaQueryList(condition);
    }
}
