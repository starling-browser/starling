using Starling.IdlGen.Emit;
using Starling.IdlGen.Mapping;
using Starling.IdlGen.Merging;
using Starling.IdlGen.Model;
using Starling.IdlGen.Overrides;
using Starling.IdlGen.Parsing;

namespace Starling.IdlGen;

// Starling.IdlGen — generates Starling.Bindings glue from Web IDL.
//
// Commands:
//   parse   Parse every vendored IDL file and print a per-file summary. A smoke
//           test for the parser. Exits non-zero if any file fails to parse.
//
// Run from the repo root:
//   dotnet run --project tools/Starling.IdlGen -- parse
public static class Program
{
    public static int Main(string[] args)
    {
        string repoRoot = FindRepoRoot();
        string idlDir = Path.Combine(repoRoot, "testdata", "webref", "idl");

        string command = args.Length > 0 ? args[0] : "help";
        return command switch
        {
            "parse" => RunParse(idlDir),
            "model" => RunModel(idlDir),
            "emit" => RunEmit(idlDir, repoRoot),
            "manifest" => RunManifest(idlDir, repoRoot),
            "coverage" => RunCoverage(idlDir, repoRoot, args.Contains("--notes")),
            _ => Help(),
        };
    }

    private static int Help()
    {
        Console.WriteLine("usage: starling-idlgen <parse|model|emit|manifest|coverage>");
        return 0;
    }

    // Reports member coverage: handled (generated or deliberately marked) versus
    // gaps the generator cannot emit yet, by cause. Drives the 99% goal.
    private static int RunCoverage(string idlDir, string repoRoot, bool notes = false)
    {
        var model = LoadModel(idlDir);
        string overridesPath = Path.Combine(repoRoot, "tools", "Starling.IdlGen", "overrides", "overrides.json");
        var emitter = new BindingsEmitter(model, new ClrMap(), OverrideSet.Load(overridesPath));
        emitter.Emit(BindingsEmitter.CoreDomInterfaces, out var stats);

        int generated = stats.Accessors + stats.Methods + stats.Constants;
        int deliberate = 0, gap = 0;
        var gapByCause = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (string note in stats.SkipNotes)
        {
            if (note.Contains(": not in model", StringComparison.Ordinal)
                || note.Contains(": no CLR type", StringComparison.Ordinal)
                || note.Contains(": no prototype slot", StringComparison.Ordinal))
            {
                continue;   // interface-level, not a member
            }

            if (note.Contains("override skip", StringComparison.Ordinal)) { deliberate++; continue; }

            gap++;
            int colon = note.IndexOf(": ", StringComparison.Ordinal);
            string cause = colon >= 0 ? note[(colon + 2)..] : note;
            gapByCause[cause] = gapByCause.GetValueOrDefault(cause) + 1;
        }

        int total = generated + deliberate + gap;
        double coverage = total == 0 ? 100d : 100d * (generated + deliberate) / total;

        Console.WriteLine($"Coverage over {BindingsEmitter.CoreDomInterfaces.Length} interfaces:");
        Console.WriteLine($"  generated={generated} deliberate(skip/override)={deliberate} gaps={gap} total={total}");
        Console.WriteLine($"  COVERAGE: {coverage:F1}%  ({generated + deliberate}/{total} handled)");

        // Per-interface coverage with the per-interface gates. A gate fails the
        // command (non-zero exit) when an interface drops below its floor.
        string gatesPath = CoverageGates.DefaultPath(repoRoot);
        var gates = File.Exists(gatesPath) ? CoverageGates.Load(gatesPath) : null;
        bool gateFailed = false;
        Console.WriteLine("Per-interface coverage:");
        foreach (var iface in stats.PerInterface)
        {
            int? min = gates?.MinPercent.TryGetValue(iface.Interface, out var m) == true ? m : null;
            string status = min is null ? "(no gate)"
                : iface.CoveragePercent + 1e-9 >= min ? $"gate {min}% OK"
                : $"gate {min}% FAIL";
            if (min is not null && iface.CoveragePercent + 1e-9 < min)
            {
                gateFailed = true;
            }

            Console.WriteLine($"  {iface.CoveragePercent,5:F1}%  {iface.Interface,-22} (gen {iface.Generated}, deliberate {iface.Deliberate}, gap {iface.Gap})  {status}");
        }
        if (gates is not null)
        {
            var ungated = stats.PerInterface.Select(i => i.Interface)
                .Where(n => !gates.MinPercent.ContainsKey(n)).ToList();
            if (ungated.Count > 0)
            {
                Console.WriteLine($"  WARNING: target interfaces with no gate: {string.Join(", ", ungated)}");
                gateFailed = true;
            }
        }

        Console.WriteLine("Gaps by cause:");
        foreach (var (cause, count) in gapByCause.OrderByDescending(kv => kv.Value))
        {
            Console.WriteLine($"  {count,4}  {cause}");
        }

        if (notes)
        {
            Console.WriteLine("Gap members:");
            foreach (string note in stats.SkipNotes
                         .Where(n => !n.Contains("override skip", StringComparison.Ordinal)
                                  && !n.Contains(": not in model", StringComparison.Ordinal)
                                  && !n.Contains(": no CLR type", StringComparison.Ordinal)
                                  && !n.Contains(": no prototype slot", StringComparison.Ordinal))
                         .OrderBy(n => n, StringComparer.Ordinal))
            {
                Console.WriteLine($"    {note}");
            }
        }
        return gateFailed ? 1 : 0;
    }

    private static int RunEmit(string idlDir, string repoRoot)
    {
        var model = LoadModel(idlDir);
        string overridesPath = Path.Combine(repoRoot, "tools", "Starling.IdlGen", "overrides", "overrides.json");
        var emitter = new BindingsEmitter(model, new ClrMap(), OverrideSet.Load(overridesPath));
        string code = emitter.Emit(BindingsEmitter.CoreDomInterfaces, out var stats);

        string genDir = Path.Combine(repoRoot, "src", "Starling.Bindings", "Generated");
        Directory.CreateDirectory(genDir);

        string outPath = Path.Combine(genDir, "CoreDomBindings.g.cs");
        File.WriteAllText(outPath, code);

        var unionEmitter = new UnionEmitter(model, new TypeMapper(model), new ClrMap());
        string unions = unionEmitter.Emit(BindingsEmitter.CoreDomInterfaces, out int unionCount);
        string unionsPath = Path.Combine(genDir, "Unions.g.cs");
        File.WriteAllText(unionsPath, unions);

        var enumEmitter = new EnumEmitter(model);
        string enums = enumEmitter.Emit(BindingsEmitter.CoreDomInterfaces, out int enumCount);
        string enumsPath = Path.Combine(genDir, "Enums.g.cs");
        File.WriteAllText(enumsPath, enums);

        var dictEmitter = new DictionaryEmitter(model, new TypeMapper(model), new ClrMap());
        string dicts = dictEmitter.Emit(BindingsEmitter.CoreDomInterfaces, out int dictCount);
        string dictsPath = Path.Combine(genDir, "Dictionaries.g.cs");
        File.WriteAllText(dictsPath, dicts);

        var callbackEmitter = new CallbackEmitter(model, new TypeMapper(model), new ClrMap());
        string callbacks = callbackEmitter.Emit(BindingsEmitter.CoreDomInterfaces, out int callbackCount);
        string callbacksPath = Path.Combine(genDir, "Callbacks.g.cs");
        File.WriteAllText(callbacksPath, callbacks);

        string manifestPath = WriteSurfaceManifest(model, repoRoot, overridesPath);

        Console.WriteLine($"Wrote {outPath}");
        Console.WriteLine($"  accessors={stats.Accessors} setters={stats.Setters} methods={stats.Methods} constants={stats.Constants} skipped={stats.SkippedMembers}");
        Console.WriteLine($"Wrote {unionsPath}  unions={unionCount}");
        Console.WriteLine($"Wrote {enumsPath}  enums={enumCount}");
        Console.WriteLine($"Wrote {dictsPath}  dictionaries={dictCount}");
        Console.WriteLine($"Wrote {callbacksPath}  callbacks={callbackCount}");
        Console.WriteLine($"Wrote {manifestPath}");
        return 0;
    }

    private static int RunManifest(string idlDir, string repoRoot)
    {
        var model = LoadModel(idlDir);
        string overridesPath = Path.Combine(repoRoot, "tools", "Starling.IdlGen", "overrides", "overrides.json");
        string manifestPath = WriteSurfaceManifest(model, repoRoot, overridesPath);
        Console.WriteLine($"Wrote {manifestPath}");
        return 0;
    }

    private static string WriteSurfaceManifest(WebIdlModel model, string repoRoot, string overridesPath)
    {
        string manifest = new SurfaceManifestEmitter(model, OverrideSet.Load(overridesPath))
            .Emit(BindingsEmitter.CoreDomInterfaces);
        string manifestPath = Path.Combine(repoRoot, "testdata", "webref", "core-dom-surface.json");
        File.WriteAllText(manifestPath, manifest);
        return manifestPath;
    }

    private static WebIdlModel LoadModel(string idlDir) =>
        IdlMerger.Merge(Directory.EnumerateFiles(idlDir, "*.idl")
            .OrderBy(p => p)
            .Select(p => IdlParser.Parse(File.ReadAllText(p))));

    private static int RunModel(string idlDir)
    {
        var docs = Directory.EnumerateFiles(idlDir, "*.idl")
            .OrderBy(p => p)
            .Select(p => IdlParser.Parse(File.ReadAllText(p)))
            .ToList();

        var model = IdlMerger.Merge(docs);
        Console.WriteLine("Merged model:");
        Console.WriteLine($"  interfaces={model.Interfaces.Count} mixins={model.Mixins.Count} " +
                          $"dictionaries={model.Dictionaries.Count} enums={model.Enums.Count} " +
                          $"callbacks={model.Callbacks.Count} namespaces={model.Namespaces.Count} " +
                          $"typedefs={model.Typedefs.Count}");
        Console.WriteLine($"  unresolved includes (external mixins): {model.UnresolvedIncludes.Count}");
        foreach (var u in model.UnresolvedIncludes.OrderBy(x => x, StringComparer.Ordinal))
        {
            Console.WriteLine($"    - {u}");
        }

        return 0;
    }

    private static int RunParse(string idlDir)
    {
        if (!Directory.Exists(idlDir))
        {
            Console.Error.WriteLine($"error: IDL data not found at {idlDir}");
            return 1;
        }

        int files = 0, failed = 0;
        int interfaces = 0, mixins = 0, dictionaries = 0, enums = 0, callbacks = 0,
            typedefs = 0, namespaces = 0, includes = 0, partials = 0;

        foreach (string path in Directory.EnumerateFiles(idlDir, "*.idl").OrderBy(p => p))
        {
            files++;
            string name = Path.GetFileName(path);
            try
            {
                var doc = IdlParser.Parse(File.ReadAllText(path));
                foreach (var d in doc.Definitions)
                {
                    if (d.Partial)
                    {
                        partials++;
                    }

                    switch (d)
                    {
                        case IdlInterface { Mixin: true }: mixins++; break;
                        case IdlInterface: interfaces++; break;
                        case IdlDictionary: dictionaries++; break;
                        case IdlEnum: enums++; break;
                        case IdlCallback: callbacks++; break;
                        case IdlTypedef: typedefs++; break;
                        case IdlNamespace: namespaces++; break;
                        case IdlIncludes: includes++; break;
                    }
                }
            }
            catch (IdlParseException ex)
            {
                failed++;
                Console.Error.WriteLine($"  FAIL {name}: {ex.Message}");
            }
        }

        Console.WriteLine($"Parsed {files - failed}/{files} IDL files.");
        Console.WriteLine($"  interfaces={interfaces} mixins={mixins} dictionaries={dictionaries} " +
                          $"enums={enums} callbacks={callbacks} typedefs={typedefs} " +
                          $"namespaces={namespaces} includes={includes} partials={partials}");
        return failed == 0 ? 0 : 1;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !File.Exists(Path.Combine(dir.FullName, "Starling.slnx"))
               && !File.Exists(Path.Combine(dir.FullName, "Starling.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
    }
}
