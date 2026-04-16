# Project Structure

**Status**: ✅ Complete

Solution layout, project organization, assembly boundaries, and dependency management for the .NET solution.

---

## Solution Layout

```
Hartonomous.sln
src/
    Hartonomous.Core/                  -- Interfaces, base classes, domain types, enums
        Hartonomous.Core.csproj
        Decomposition/
            IDecomposer.cs
            BaseDecomposer.cs
            DecomposerConfig.cs
        Analysis/
            IAnalysisPass.cs
            BaseAnalysisPass.cs
        Recomposition/
            IRecomposer.cs
            BaseRecomposer.cs
            RecompositionOptions.cs
        Ingestion/
            IIngestionPipeline.cs
            IIngestionBatch.cs
            EntityHandle.cs
            EdgeMemberSpec.cs
            PipelineStats.cs
        Engine/
            ISignificanceUpdater.cs
            ITraversal.cs
            TraversalQuery.cs
            TraversalResult.cs
        Orchestration/
            IPhaseRunner.cs
            Phase.cs
            PhaseResult.cs
        Monitoring/
            IProgressReporter.cs
            IHealthCheck.cs
            ProgressSnapshot.cs
            SubstrateHealth.cs
        Errors/
            SubstrateException.cs
            SourceValidationException.cs
            IngestionException.cs
            TraversalException.cs
        Native/
            Blake3Native.cs           -- P/Invoke declarations for libhartonomous

    Hartonomous.Decomposers/           -- All decomposer implementations
        Hartonomous.Decomposers.csproj
        UcdUcaDecomposer.cs
        Iso639Decomposer.cs
        WordNetDecomposer.cs
        OmwDecomposer.cs
        UdDecomposer.cs
        WiktionaryDecomposer.cs
        TatoebaDecomposer.cs
        SafetensorsDecomposer.cs
        TextDecomposer.cs              -- Runtime: arbitrary text (tree-sitter + UAX #29)
        ImageDecomposer.cs             -- Runtime: raster images
        AudioDecomposer.cs             -- Runtime: audio files
        VideoDecomposer.cs             -- Runtime: video (composes Image + Audio)
        Parsers/                       -- Format-specific parsers
            ConllUParser.cs
            WordNetDbParser.cs
            UnicodeDataParser.cs
            SafetensorsHeaderParser.cs
            WiktextractParser.cs
            TsvParser.cs

    Hartonomous.Engine/                -- Pipeline, significance, traversal
        Hartonomous.Engine.csproj
        Ingestion/
            NpgsqlIngestionPipeline.cs
            IngestionBatch.cs
        Significance/
            GlickoSignificanceUpdater.cs
        Traversal/
            SignificanceGuidedTraversal.cs
        Monitoring/
            DatabaseProgressReporter.cs
            SqlHealthCheck.cs

    Hartonomous.Analysis/              -- Analysis pass implementations
        Hartonomous.Analysis.csproj
        Text/
            MorphologicalAnalysis.cs
            DependencyParsing.cs
            SemanticSimilarity.cs
            CrossLingualAlignment.cs
            FrequencyAnalysis.cs
            CollocationDetection.cs
            EtymologyTracing.cs
        Image/
            FeatureExtraction.cs
            SpatialDecomposition.cs
            ColorSpaceAnalysis.cs
            TextureClassification.cs
            ShapeDetection.cs
            PatternRecognition.cs
            CompositionAnalysis.cs
            PerceptualHashing.cs
        Audio/
            SpectralAnalysis.cs
            PitchDetection.cs
            RhythmAnalysis.cs
            HarmonicAnalysis.cs
            TimbreAnalysis.cs
            OnsetDetection.cs
            EnvelopeExtraction.cs
            FormantAnalysis.cs
            SourceSeparation.cs
            SpatialAudioAnalysis.cs
            DynamicRangeAnalysis.cs
            NoiseProfiling.cs
            TransientAnalysis.cs
            ModulationAnalysis.cs
            PsychoacousticModeling.cs
            TemporalPattern.cs
            SpectralPattern.cs
            CrossModalAlignment.cs
            MicrostructureAnalysis.cs
            PhaseCoherence.cs
            ResonanceDetection.cs
            ArtifactDetection.cs
        Video/
            FrameDecomposition.cs
            MotionEstimation.cs
            SceneDetection.cs
            TemporalSegmentation.cs
            AudioVisualSync.cs
            ObjectTracking.cs

    Hartonomous.Recomposers/           -- Recomposer implementations
        Hartonomous.Recomposers.csproj
        TextRecomposer.cs
        ImageRecomposer.cs
        AudioRecomposer.cs
        VideoRecomposer.cs
        SafetensorsRecomposer.cs

    Hartonomous.Api/                   -- ASP.NET Core minimal API
        Hartonomous.Api.csproj
        Program.cs
        Endpoints/
            EntityEndpoints.cs
            EdgeEndpoints.cs
            TraversalEndpoints.cs
            RecompositionEndpoints.cs
            MonitoringEndpoints.cs

    Hartonomous.Cli/                   -- CLI entry point (phase runner, migration, diagnostics)
        Hartonomous.Cli.csproj
        Program.cs
        Commands/
            RunCommand.cs              -- Phase execution
            MigrateCommand.cs          -- Schema migration
            StatusCommand.cs           -- Phase + health status
            ValidateCommand.cs         -- Source + schema validation

tests/
    Hartonomous.Core.Tests/
    Hartonomous.Decomposers.Tests/
    Hartonomous.Engine.Tests/
    Hartonomous.Analysis.Tests/
    Hartonomous.Api.Tests/
    Hartonomous.Integration.Tests/     -- End-to-end with real PostgreSQL

ext/
    hartonomous_pg/                    -- PostgreSQL C extension
    libhartonomous/                    -- Shared C/C++ native library

sql/
    domains/
    types/
    tables/
    indexes/
    functions/
    procedures/
    views/
    triggers/
    migrations/
    seed/
```

---

## Project Dependency Graph

```
Hartonomous.Core              (no project dependencies — interfaces and types only)
    ↑
Hartonomous.Engine            (depends on Core)
    ↑
Hartonomous.Decomposers       (depends on Core)
    ↑
Hartonomous.Analysis          (depends on Core)
    ↑
Hartonomous.Recomposers       (depends on Core)
    ↑
Hartonomous.Api               (depends on Core, Engine, Recomposers)
    ↑
Hartonomous.Cli               (depends on Core, Engine, Decomposers, Analysis, Recomposers)
```

**Rule**: Core depends on nothing. Everything depends on Core. No circular dependencies. No lateral dependencies between Decomposers/Analysis/Recomposers.

---

## Assembly Boundaries

| Project | Contains | References |
|---------|----------|------------|
| `Core` | All interfaces, base classes, domain types, enums, exception types, P/Invoke declarations | None |
| `Engine` | `NpgsqlIngestionPipeline`, `GlickoSignificanceUpdater`, `SignificanceGuidedTraversal`, `DatabaseProgressReporter`, `SqlHealthCheck` | Core, Npgsql |
| `Decomposers` | All 12 decomposer classes (8 seed + 4 runtime) + format-specific parsers | Core |
| `Analysis` | All 43 analysis pass classes | Core |
| `Recomposers` | All 5 recomposer classes | Core |
| `Api` | ASP.NET minimal API endpoints | Core, Engine, Recomposers |
| `Cli` | CLI commands, DI composition root, `SequentialPhaseRunner` | Core, Engine, Decomposers, Analysis, Recomposers |

**Decomposers are NOT plugins.** They are compiled into the `Decomposers` project. No `Assembly.LoadFrom`, no runtime discovery. The phase runner knows all decomposers at compile time. New decomposers = add a class, rebuild.

---

## Package Dependencies

| Package | Used By | Purpose |
|---------|---------|---------|
| `Npgsql` (9.x) | Engine | PostgreSQL driver. Connection pooling, parameterized queries, COPY protocol. |
| `Npgsql.NetTopologySuite` | Engine | PostGIS geometry type support for Npgsql. |
| `NetTopologySuite` (2.x) | Core, Engine | PostGIS geometry types (`Point`, `LineString`, `MultiLineString`). |
| `Microsoft.Extensions.Logging.Abstractions` | Core | `ILogger` interface. No implementation — the host chooses the sink. |
| `Microsoft.Extensions.DependencyInjection` | Cli, Api | DI container for composing the object graph. |
| `Microsoft.Extensions.Configuration` | Cli, Api | Configuration binding (appsettings.json). |
| `System.CommandLine` (2.x) | Cli | CLI argument parsing and command structure. |
| `System.IO.Hashing` | Core | Managed fallback for hashing (never used in production — P/Invoke preferred). |
| `xUnit` + `FluentAssertions` | Tests | Test framework. |

**No third-party parsing libraries.** All parsers (CoNLL-U, WordNet database files, UnicodeData.txt, safetensors headers, Wiktextract JSONL, TSV) are hand-written. The formats are stable and well-documented — no need for general-purpose parsing frameworks.

**No Serilog.** Use `Microsoft.Extensions.Logging` with the built-in console/file providers. The CLI and API compose the logging pipeline at startup.

---

## Build & Publish

```xml
<!-- Directory.Build.props (solution root) -->
<Project>
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  </PropertyGroup>
</Project>
```

| Setting | Value | Rationale |
|---------|-------|-----------|
| Target framework | `net9.0` | Latest LTS-adjacent. Required for ReadOnlySpan improvements and NativeAOT readiness. |
| Nullable | `enable` | All reference types are non-nullable by default. Null is opt-in. |
| Implicit usings | `disable` | Explicit imports. No magic. |
| Platform | `win-x64`, `linux-x64` | Native interop (libhartonomous) requires platform-specific builds. |
| Publish | Self-contained, single-file for CLI. Framework-dependent for API (Docker). |

### Native Library Deployment

The `libhartonomous` shared library (`.dll` / `.so`) is built separately by the C/C++ build system and placed in `runtimes/{rid}/native/`. The `Core` project includes it as a native dependency:

```xml
<!-- Hartonomous.Core.csproj -->
<ItemGroup>
    <NativeFileReference Include="runtimes\win-x64\native\libhartonomous.dll" />
</ItemGroup>
```

---

## Code Style

File-scoped namespaces. One type per file. File name matches type name.

```
.editorconfig:
    indent_style = space
    indent_size = 4
    end_of_line = crlf
    dotnet_sort_system_directives_first = true
    csharp_style_namespace_declarations = file_scoped:error
    csharp_style_var_for_built_in_types = false:warning
    csharp_style_var_when_type_is_apparent = true:suggestion
    dotnet_naming_rule.interfaces_begin_with_i = true
    dotnet_naming_rule.private_fields_begin_with_underscore = true
```

Naming conventions follow [architecture.md](../../architecture.md) — no additional conventions invented here.
