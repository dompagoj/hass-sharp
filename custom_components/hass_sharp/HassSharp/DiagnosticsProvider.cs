using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace HassSharp;

public class DiagnosticsProvider
{
    readonly AdhocWorkspace _workspace;
    readonly ProjectId _baseProjectId;

    readonly object _gate = new();
    DocumentId? _servicesDocumentId, _entitiesDocumentId;
    GeneratedServiceCalls _serviceCalls;

    public const string GlobalUsings = """
                                       global using System;
                                       global using System.Threading;
                                       global using System.Threading.Tasks;
                                       global using System.Collections.Generic;
                                       global using System.Linq;
                                       global using HassSharp;
                                       global using HassSharp.Entities;

                                       """;

    public DiagnosticsProvider()
    {
        var host = MefHostServices.Create(MefHostServices.DefaultAssemblies);
        _workspace = new AdhocWorkspace(host);

        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "AutomationProject",
            "AutomationProject",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest)
        );

        _baseProjectId = _workspace.AddProject(projectInfo).Id;

        // Add default references to the workspace project
        var references = ProjectReferences.GetDefaultReferences();
        _workspace.TryApplyChanges(
            _workspace.CurrentSolution.WithProjectMetadataReferences(_baseProjectId, references));
        // Until a schema is given: the typed calls without a domain, and the entity classes with only EntityHandles.
        var entities = GeneratedEntities.Build(EntitiesGenerator([]).Source, [], out _)
            ?? throw new InvalidOperationException("The empty entity classes do not compile.");
        var services = GeneratedServiceCalls.Build(Generator([]).Source, [], entities, out _)
            ?? throw new InvalidOperationException("The empty typed service calls do not compile.");
        lock (_gate)
        {
            ReplaceDocument(ref _servicesDocumentId, ServicesPath, services.Tree);
            ReplaceDocument(ref _entitiesDocumentId, EntitiesPath, entities.Tree);
            _serviceCalls = services;
        }
    }

    /// <summary>
    /// The accepted generated code: the typed service calls (<c>context.Services</c>) and, beside them, the entity classes
    /// (<see cref="GeneratedServiceCalls.Entities"/>); their sources, and the assemblies built from them once, which every
    /// compilation references. Swapped whole under the lock when either changes: a compilation takes it once and keeps
    /// it, so it never references an assembly it was not given. Until a schema is given, there are no domains.
    /// </summary>
    public GeneratedServiceCalls ServiceCalls()
    {
        lock (_gate) return _serviceCalls;
    }

    /// <summary>
    /// The source of the accepted typed service calls, as the language services' workspace has it (completion, hover,
    /// signatures and definitions read it there); compilations reference <see cref="ServiceCalls"/>' assembly instead.
    /// </summary>
    public SyntaxTree ServicesSyntaxTree() => ServiceCalls().Tree;

    /// <summary>
    /// Replaces the typed service calls with those of <paramref name="services"/> (normalised, keyed by
    /// <c>domain.service</c>), as <see cref="UseServiceSchema(IReadOnlyCollection{KeyValuePair{string, JsonObject}})"/>
    /// does. Compilations started later and the next language request see them; nothing compiled earlier changes.
    /// </summary>
    public void UseServiceSchema(ServiceSchemaCatalog services) =>
        UseServiceSchema(services.Items.Select(p => KeyValuePair.Create(p.Key, (JsonObject)p.Value!)).ToArray());

    /// <summary>What a new schema did to the typed service calls.</summary>
    /// <param name="Accepted">False when no tree from it compiles: the previous tree stays.</param>
    /// <param name="Changed">Whether the assembly every compilation gets was replaced.</param>
    /// <param name="Grew">Whether that replacement added a service or changed one (rather than only dropping some).</param>
    /// <param name="Services">The services the tree now has: the schema's, less <paramref name="Excluded"/>.</param>
    /// <param name="Excluded">Services whose generated code did not compile, left out; the others are kept.</param>
    /// <param name="Errors">The compiler's errors, when there were any.</param>
    internal sealed record ServiceSchemaResult(bool Accepted, bool Changed, bool Grew, IReadOnlyList<KeyValuePair<string, JsonObject>> Services,
        IReadOnlyList<string> Excluded, IReadOnlyList<string> Errors);

    /// <summary>What a new schema did to the entity classes: as <see cref="ServiceSchemaResult"/>, by domain.</summary>
    internal sealed record EntitySchemaResult(bool Accepted, bool Changed, bool Grew, IReadOnlyList<KeyValuePair<string, JsonObject>> Domains,
        IReadOnlyList<string> Excluded, IReadOnlyList<string> Errors);

    /// <summary>How the typed service calls are generated; a test may break them on purpose.</summary>
    internal Func<IEnumerable<KeyValuePair<string, JsonObject>>, ServiceGenerator.Generated> Generator { get; set; } = ServiceGenerator.GenerateWithSpans;

    /// <summary>How the entity classes are generated; a test may break them on purpose.</summary>
    internal Func<IEnumerable<KeyValuePair<string, JsonObject>>, EntityGenerator.Generated> EntitiesGenerator { get; set; } = EntityGenerator.Generate;

    /// <summary>
    /// Generates the typed service calls of <paramref name="services"/> and, if they compile on their own without errors,
    /// builds them once into an assembly and installs it beside the entity classes, which stay. A service whose own code
    /// does not compile is left out and the rest are tried again; when no tree compiles, the previous assembly stays (at
    /// first, the empty one). Schema text cannot reach beyond comments and string literals (<see cref="ServiceGenerator"/>),
    /// so this is a safety net: what one schema holds never stops node code that does not call that service from
    /// compiling. Generating, building and checking run outside the lock; only the swap takes it.
    /// </summary>
    internal ServiceSchemaResult UseServiceSchema(IReadOnlyCollection<KeyValuePair<string, JsonObject>> services)
    {
        var (accepted, changed, grew, kept, excluded, errors) = Use(services,
            candidate =>
            {
                var generated = Generator(candidate);
                var ids = candidate.Select(p => p.Key).ToArray();
                return (generated.Source, line => generated.ServicesAt(line, ids));
            },
            installed => installed.Source,
            (string source, List<KeyValuePair<string, JsonObject>> candidate, out Diagnostic[] failures) =>
                GeneratedServiceCalls.Build(source, candidate, ServiceCalls().Entities, out failures),
            Install);
        return new(accepted, changed, grew, kept, excluded, errors);
    }

    /// <summary>
    /// Generates the entity classes of <paramref name="domains"/> (normalised <see cref="EntitySchemaCatalog"/> items,
    /// keyed by domain) and installs them beside the typed service calls, which stay, as
    /// <see cref="UseServiceSchema(IReadOnlyCollection{KeyValuePair{string, JsonObject}})"/> installs those: a domain whose
    /// own code does not compile is left out and the rest are kept; when no tree compiles, the previous classes stay.
    /// </summary>
    internal EntitySchemaResult UseEntitySchema(IReadOnlyCollection<KeyValuePair<string, JsonObject>> domains)
    {
        var (accepted, changed, grew, kept, excluded, errors) = Use<GeneratedEntities>(domains,
            candidate => { var generated = EntitiesGenerator(candidate); return (generated.Source, generated.DomainsAt); },
            installed => installed.Entities.Source, GeneratedEntities.Build, Install);
        return new(accepted, changed, grew, kept, excluded, errors);
    }

    delegate T? Builder<T>(string source, List<KeyValuePair<string, JsonObject>> items, out Diagnostic[] errors) where T : class;

    /// <summary>
    /// The generated code of <paramref name="items"/>, built and installed when it compiles. Up to three attempts: the
    /// items whose code has the errors are left out each time, and no attempt follows an error outside every item's code
    /// (the generator's own, which no subset would mend). A source that is the installed one is not built again.
    /// </summary>
    /// <param name="generate">The source of the items, and the items whose code is at a 1-based line of it.</param>
    /// <param name="installed">This half's source in the installed code.</param>
    (bool Accepted, bool Changed, bool Grew, List<KeyValuePair<string, JsonObject>> Items, string[] Excluded, List<string> Errors) Use<T>(
        IReadOnlyCollection<KeyValuePair<string, JsonObject>> items,
        Func<List<KeyValuePair<string, JsonObject>>, (string Source, Func<int, IEnumerable<string>> ItemsAt)> generate,
        Func<GeneratedServiceCalls, string> installed, Builder<T> build, Func<T, (bool Changed, bool Grew)> install) where T : class
    {
        var candidate = items.ToList();
        var excluded = new SortedSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            (string Source, Func<int, IEnumerable<string>> ItemsAt) generated;
            try { generated = generate(candidate); }
            catch (InvalidOperationException ex) { errors.Add(ex.Message); break; }
            lock (_gate) if (installed(_serviceCalls) == generated.Source) return (true, false, false, candidate, [.. excluded], errors);
            var built = build(generated.Source, candidate, out var failures);
            if (built != null)
            {
                lock (_gate) { var (changed, grew) = install(built); return (true, changed, grew, candidate, [.. excluded], errors); }
            }
            errors.AddRange(failures.Take(10).Select(d => d.ToString()));
            var broken = failures.Select(d => generated.ItemsAt(d.Location.GetLineSpan().StartLinePosition.Line + 1).ToArray()).ToArray();
            // An error outside every item's code is the generator's: no subset would compile.
            if (broken.Any(b => b.Length == 0)) break;
            excluded.UnionWith(broken.SelectMany(b => b));
            candidate = candidate.Where(p => !excluded.Contains(p.Key)).ToList();
        }
        return (false, false, false, [], [.. excluded], errors);
    }

    // Under _gate. The entity classes installed meanwhile stay.
    (bool Changed, bool Grew) Install(GeneratedServiceCalls built)
    {
        if (_serviceCalls.Source == built.Source) return (false, false);
        ReplaceDocument(ref _servicesDocumentId, ServicesPath, built.Tree);
        var grew = GeneratedAssembly.Grew(_serviceCalls.Schemas, built.Schemas);
        _serviceCalls = built.With(_serviceCalls.Entities);
        return (true, grew);
    }

    // Under _gate: a new object, carrying the same service calls and these entity classes.
    (bool Changed, bool Grew) Install(GeneratedEntities built)
    {
        if (_serviceCalls.Entities.Source == built.Source) return (false, false);
        ReplaceDocument(ref _entitiesDocumentId, EntitiesPath, built.Tree);
        var grew = GeneratedAssembly.Grew(_serviceCalls.Entities.Schemas, built.Schemas);
        _serviceCalls = _serviceCalls.With(built);
        return (true, grew);
    }

    // Under _gate (or in the constructor): the language services' workspace gets the generated source.
    void ReplaceDocument(ref DocumentId? id, string path, SyntaxTree tree)
    {
        var solution = _workspace.CurrentSolution;
        if (id is null)
        {
            id = DocumentId.CreateNewId(_baseProjectId);
            solution = solution.AddDocument(id, path, tree.GetText());
        }
        else
        {
            solution = solution.WithDocumentText(id, tree.GetText());
        }
        if (!_workspace.TryApplyChanges(solution))
            throw new InvalidOperationException($"Could not replace the generated document {path}.");
    }

    /// <summary>The path of the services tree: a diagnostic in it is told apart from one in node code by its tree.</summary>
    public const string ServicesPath = "Services.g.cs";

    /// <summary>The path of the entity classes' tree, as <see cref="ServicesPath"/> is the services'.</summary>
    public const string EntitiesPath = "Entities.g.cs";

    internal Project CreateLibraryProject()
    {
        lock (_gate)
            return _workspace.CurrentSolution.GetProject(_baseProjectId)!
                .WithMetadataReferences(NodeLibraryCompiler.FrameworkReferences())
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable))
                .AddDocument("GlobalUsings.g.cs", SourceText.From(GlobalUsings)).Project;
    }
}

/// <summary>
/// One accepted set of typed service calls: the generated source and its syntax tree, and the assembly built from it
/// once; and the entity classes installed beside them. The assembly's name is unique per source
/// (<c>HassSharpServiceCalls_</c> and a hash of it), so code compiled against an older set keeps finding the one it was
/// compiled against: its load context loads <see cref="Image"/> by that name (<see cref="ScriptLoadContext"/>).
/// </summary>
public sealed class GeneratedServiceCalls
{
    public const string AssemblyPrefix = "HassSharpServiceCalls_";
    public string Source { get; }
    /// <summary>The source parsed, as the language services' workspace has it.</summary>
    public SyntaxTree Tree { get; }
    public string AssemblyName { get; }
    /// <summary>The built assembly, loaded from these bytes into each automation's load context that uses it.</summary>
    public byte[] Image { get; }
    /// <summary>What every compilation references in place of the source.</summary>
    public MetadataReference Reference { get; }
    /// <summary>The entity classes beside these calls: a service push keeps them, and an entity push keeps these calls.</summary>
    public GeneratedEntities Entities { get; }
    /// <summary>What every compilation references in place of the generated sources: the service calls, then the entities.</summary>
    public ImmutableArray<MetadataReference> References { get; }
    /// <summary>Each service's normalised schema as JSON, to tell an added or changed service from a dropped one.</summary>
    internal IReadOnlyDictionary<string, string> Schemas { get; }
    readonly GeneratedAssembly.Emitted _emitted;

    GeneratedServiceCalls(GeneratedAssembly.Emitted emitted, string source, IReadOnlyDictionary<string, string> schemas, GeneratedEntities entities)
    {
        (_emitted, Source, Schemas, Entities) = (emitted, source, schemas, entities);
        (Tree, AssemblyName, Image, Reference) = (emitted.Tree, emitted.Name, emitted.Image, emitted.Reference);
        References = [Reference, entities.Reference];
    }

    /// <summary>These calls beside <paramref name="entities"/>; this object when they are the ones it has.</summary>
    internal GeneratedServiceCalls With(GeneratedEntities entities) => ReferenceEquals(entities, Entities) ? this : new(_emitted, Source, Schemas, entities);

    /// <summary>Parses and builds <paramref name="source"/> on its own; null, with the errors, when it does not compile.</summary>
    /// <param name="entities">The entity classes the result carries.</param>
    internal static GeneratedServiceCalls? Build(string source, IEnumerable<KeyValuePair<string, JsonObject>> services, GeneratedEntities entities, out Diagnostic[] errors) =>
        GeneratedAssembly.Emit(source, DiagnosticsProvider.ServicesPath, AssemblyPrefix, out errors) is { } emitted ? new(emitted, source, GeneratedAssembly.Schemas(services), entities) : null;
}

/// <summary>
/// One accepted set of entity classes (<see cref="EntityGenerator"/>): the generated source and its syntax tree, and the
/// assembly built from it once, named after its source (<c>HassSharpEntities_</c> and a hash of it) as the typed service
/// calls' is, so code compiled against an older set keeps loading the one it was compiled against.
/// </summary>
public sealed class GeneratedEntities
{
    public const string AssemblyPrefix = "HassSharpEntities_";
    public string Source { get; }
    /// <summary>The source parsed, as the language services' workspace has it.</summary>
    public SyntaxTree Tree { get; }
    public string AssemblyName { get; }
    /// <summary>The built assembly, loaded from these bytes into each automation's load context that uses it.</summary>
    public byte[] Image { get; }
    /// <summary>What every compilation references in place of the source.</summary>
    public MetadataReference Reference { get; }
    /// <summary>Each domain's normalised schema as JSON, to tell an added or changed domain from a dropped one.</summary>
    internal IReadOnlyDictionary<string, string> Schemas { get; }

    GeneratedEntities(GeneratedAssembly.Emitted emitted, string source, IReadOnlyDictionary<string, string> schemas) =>
        (Source, Tree, AssemblyName, Image, Reference, Schemas) = (source, emitted.Tree, emitted.Name, emitted.Image, emitted.Reference, schemas);

    /// <summary>Parses and builds <paramref name="source"/> on its own; null, with the errors, when it does not compile.</summary>
    internal static GeneratedEntities? Build(string source, IEnumerable<KeyValuePair<string, JsonObject>> domains, out Diagnostic[] errors) =>
        GeneratedAssembly.Emit(source, DiagnosticsProvider.EntitiesPath, AssemblyPrefix, out errors) is { } emitted ? new(emitted, source, GeneratedAssembly.Schemas(domains)) : null;
}

/// <summary>Builds generated source on its own, against the framework references, into an assembly named after it.</summary>
static class GeneratedAssembly
{
    internal sealed record Emitted(SyntaxTree Tree, string Name, byte[] Image, MetadataReference Reference);

    /// <summary>
    /// The source parsed as <paramref name="path"/> and built as <paramref name="prefix"/> and 16 hex digits of its
    /// SHA-256; null, with the errors, when it does not compile.
    /// </summary>
    internal static Emitted? Emit(string source, string path, string prefix, out Diagnostic[] errors)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: path);
        var name = prefix + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)))[..16].ToLowerInvariant();
        var compilation = CSharpCompilation.Create(name, [tree], NodeLibraryCompiler.FrameworkReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (!result.Success) { if (errors.Length == 0) errors = result.Diagnostics.ToArray(); return null; }
        var image = stream.ToArray();
        return new(tree, name, image, MetadataReference.CreateFromImage(ImmutableArray.Create(image), filePath: name + ".dll"));
    }

    /// <summary>Each item's normalised schema as JSON.</summary>
    internal static IReadOnlyDictionary<string, string> Schemas(IEnumerable<KeyValuePair<string, JsonObject>> items) =>
        items.ToDictionary(p => p.Key, p => p.Value.ToJsonString(), StringComparer.Ordinal);

    /// <summary>Whether <paramref name="after"/> adds an item to <paramref name="before"/> or changes one, rather than only dropping some.</summary>
    internal static bool Grew(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after) =>
        after.Any(p => !before.TryGetValue(p.Key, out var schema) || schema != p.Value);
}

public class CompletionModel
{
    public string DisplayText { get; init; } = "";
    public string DisplayTextPrefix { get; init; } = "";
    public string DisplayTextSuffix { get; init; } = "";
    public string FilterText { get; init; } = "";
    public string[] Tags { get; init; } = [];
    public CompletionTextEdit TextEdit { get; init; } = new();
    public CompletionTextEdit[] AdditionalTextEdits { get; init; } = [];
}

public class CompletionTextEdit
{
    public int Start { get; init; }
    public int Length { get; init; }
    public string NewText { get; init; } = "";
}
