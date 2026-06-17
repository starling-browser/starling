// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Common.Diagnostics;
using Starling.Css.Cascade;
using Starling.Css.Parser;
using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Dom;

namespace Starling.Css.Tests;

[TestClass]
public sealed class StyleEngineIndexTests
{
    [TestMethod]
    public void Compute_applies_candidate_rule_without_hot_path_counters()
    {
        var css = new StringBuilder();
        for (var i = 0; i < 1000; i++)
        {
            css.Append(".missing").Append(i).Append(" { color: red; }\n");
        }

        css.Append("article { color: blue; }");

        var doc = new Document();
        var article = doc.CreateElement("article");
        doc.AppendChild(article);
        using var metrics = new MetricRecorder();
        var engine = new StyleEngine(includeUserAgentStyleSheet: false, loggerFactory: NullLoggerFactory.Instance);
        engine.AddStyleSheet(CssParser.ParseStyleSheet(css.ToString()));

        var style = engine.Compute(article);

        style.GetColor(PropertyId.Color).Should().Be(new CssColor(0, 0, 255));
        metrics.CountOf("css.rule_candidates").Should().Be(0);
        metrics.CountOf("css.selector_tests").Should().Be(0);
    }

    [TestMethod]
    public void Style_sharing_survives_structural_selector_on_unrelated_candidates()
    {
        var doc = new Document();
        var root = doc.CreateElement("div");
        doc.AppendChild(root);
        root.AppendChild(doc.CreateElement("i"));
        var first = doc.CreateElement("span");
        root.AppendChild(first);
        root.AppendChild(doc.CreateElement("i"));
        var second = doc.CreateElement("span");
        root.AppendChild(second);

        var engine = new StyleEngine(includeUserAgentStyleSheet: false);
        engine.AddStyleSheet(CssParser.ParseStyleSheet("""
            li:nth-child(odd) { color: red; }
            span { color: blue; }
            """));

        var cache = new CascadeCache();
        engine.PrecomputeTree(root, cache);

        engine.Compute(first, context: null, cache).GetColor(PropertyId.Color).Should().Be(new CssColor(0, 0, 255));
        engine.Compute(second, context: null, cache).GetColor(PropertyId.Color).Should().Be(new CssColor(0, 0, 255));
    }

    [TestMethod]
    public void Style_sharing_revalidates_sibling_dependent_selectors()
    {
        var doc = new Document();
        var root = doc.CreateElement("div");
        doc.AppendChild(root);
        var markedPrevious = doc.CreateElement("i");
        markedPrevious.ClassList.Add("marker");
        root.AppendChild(markedPrevious);
        var first = doc.CreateElement("span");
        root.AppendChild(first);
        root.AppendChild(doc.CreateElement("i"));
        var second = doc.CreateElement("span");
        root.AppendChild(second);

        var engine = new StyleEngine(includeUserAgentStyleSheet: false);
        engine.AddStyleSheet(CssParser.ParseStyleSheet(".marker + span { color: red; }"));

        var cache = new CascadeCache();
        engine.PrecomputeTree(root, cache);

        engine.Compute(first, context: null, cache).GetColor(PropertyId.Color).Should().Be(new CssColor(255, 0, 0));
        engine.Compute(second, context: null, cache).GetColor(PropertyId.Color).Should().Be(CssColor.Black);
    }

    [TestMethod]
    public void Style_sharing_revalidates_nth_child_selectors()
    {
        var doc = new Document();
        var root = doc.CreateElement("div");
        doc.AppendChild(root);
        root.AppendChild(doc.CreateElement("i"));
        var first = doc.CreateElement("span");
        root.AppendChild(first);
        root.AppendChild(doc.CreateElement("i"));
        var second = doc.CreateElement("span");
        root.AppendChild(second);

        var engine = new StyleEngine(includeUserAgentStyleSheet: false);
        engine.AddStyleSheet(CssParser.ParseStyleSheet("span:nth-child(2) { color: red; }"));

        var cache = new CascadeCache();
        engine.PrecomputeTree(root, cache);

        engine.Compute(first, context: null, cache).GetColor(PropertyId.Color).Should().Be(new CssColor(255, 0, 0));
        engine.Compute(second, context: null, cache).GetColor(PropertyId.Color).Should().Be(CssColor.Black);
    }

    [TestMethod]
    public void Style_sharing_revalidates_empty_selectors()
    {
        var doc = new Document();
        var root = doc.CreateElement("div");
        doc.AppendChild(root);
        root.AppendChild(doc.CreateElement("i"));
        var empty = doc.CreateElement("span");
        root.AppendChild(empty);
        root.AppendChild(doc.CreateElement("i"));
        var nonEmpty = doc.CreateElement("span");
        nonEmpty.AppendChild(doc.CreateElement("b"));
        root.AppendChild(nonEmpty);

        var engine = new StyleEngine(includeUserAgentStyleSheet: false);
        engine.AddStyleSheet(CssParser.ParseStyleSheet("span:empty { color: red; }"));

        var cache = new CascadeCache();
        engine.PrecomputeTree(root, cache);

        engine.Compute(empty, context: null, cache).GetColor(PropertyId.Color).Should().Be(new CssColor(255, 0, 0));
        engine.Compute(nonEmpty, context: null, cache).GetColor(PropertyId.Color).Should().Be(CssColor.Black);
    }

    [TestMethod]
    public void Style_sharing_revalidates_container_query_conditions()
    {
        var doc = new Document();
        var root = doc.CreateElement("div");
        doc.AppendChild(root);
        root.AppendChild(doc.CreateElement("i"));
        var wide = doc.CreateElement("div");
        var wideChild = doc.CreateElement("p");
        wide.AppendChild(wideChild);
        root.AppendChild(wide);
        root.AppendChild(doc.CreateElement("i"));
        var narrow = doc.CreateElement("div");
        var narrowChild = doc.CreateElement("p");
        narrow.AppendChild(narrowChild);
        root.AppendChild(narrow);

        var engine = new StyleEngine(includeUserAgentStyleSheet: false)
        {
            ContainerSizeLookup = el => el == wide
                ? (500, 200)
                : el == narrow
                    ? (300, 200)
                    : null,
        };
        engine.AddStyleSheet(CssParser.ParseStyleSheet("""
            @container (min-width: 400px) {
                p { color: red; }
            }
            """));

        var cache = new CascadeCache();
        engine.PrecomputeTree(root, cache);

        engine.Compute(wideChild, context: null, cache).GetColor(PropertyId.Color).Should().Be(new CssColor(255, 0, 0));
        engine.Compute(narrowChild, context: null, cache).GetColor(PropertyId.Color).Should().Be(CssColor.Black);
    }

    private sealed class MetricRecorder : IDisposable
    {
        private readonly MeterListener _l = new();
        private readonly ConcurrentDictionary<string, double> _v = new();

        public MetricRecorder()
        {
            _l.InstrumentPublished = (inst, lst) =>
            { if (inst.Meter.Name == StarlingTelemetry.SourceName)
                {
                    lst.EnableMeasurementEvents(inst);
                }
            };
            _l.SetMeasurementEventCallback<double>((inst, m, t, s) => Add(inst.Name, m));
            _l.SetMeasurementEventCallback<long>((inst, m, t, s) => Add(inst.Name, m));
            _l.Start();
        }

        private void Add(string n, double m) => _v.AddOrUpdate(n, m, (_, p) => p + m);
        public double CountOf(string name) => _v.TryGetValue(name, out var x) ? x : 0d;
        public void Dispose() => _l.Dispose();
    }
}
