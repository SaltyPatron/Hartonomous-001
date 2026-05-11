@{
    # ── Hartonomous ops-tooling config ─────────────────────────────────────
    # Data-only. Every value is overridable via environment variable. The
    # Common module overlays env vars on top of these defaults — see
    # Get-HartonomousConfig in lib\Hartonomous.Common.psm1.
    #
    # Env var convention: HARTONOMOUS_<UPPER_SNAKE_KEY>  (nested via double
    # underscore — e.g. Postgres.Port → HARTONOMOUS_POSTGRES__PORT).

    ScriptsVersion = '1.0.0'

    Repo = @{
        # Filled in at runtime by Get-HartonomousConfig — do not edit.
        Root = $null
    }

    Paths = @{
        SourceRoot       = '/vault/Data'
        Logs             = 'logs'
        Reports          = 'reports'
        Artifacts        = 'artifacts'
        NativeBuild      = 'ext\libhartonomous\build'
        PgExtensionSrc   = 'ext\hartonomous_pg'
        LibHartonomousSrc= 'ext\libhartonomous'
        Solution         = 'Hartonomous.slnx'
        SchemaBootstrap  = 'sql\schema\bootstrap.sql'
    }

    Dotnet = @{
        MinSdk       = '9.0.0'
        Configuration= 'Debug'
        Verbosity    = 'minimal'
        NoLogo       = $true
    }

    Native = @{
        CMakeMinVersion   = '3.24'
        Configuration     = 'Release'
        PreferredGenerator= 'Visual Studio 18 2026'
        FallbackGenerator = 'Visual Studio 17 2022'
        Arch              = 'x64'
        BuildTests        = $true
        BuildShared       = $true
    }

    Docker = @{
        ComposeProject = 'hartonomous'
        ComposeFile    = 'docker-compose.yml'
        PgContainer    = 'hartonomous-postgres'
        PgImage        = 'hartonomous-postgres'
        PgDockerfile   = 'Dockerfile.pg'
        DesktopStartTimeoutSec  = 180
        HealthCheckTimeoutSec   = 120
        DesktopCandidatePaths   = @(
            '${env:ProgramFiles}\Docker\Docker\Docker Desktop.exe',
            '${env:ProgramFiles(x86)}\Docker\Docker\Docker Desktop.exe',
            '${env:LOCALAPPDATA}\Programs\Docker\Docker\Docker Desktop.exe'
        )
    }

    Postgres = @{
        Host       = 'localhost'
        Port       = 5433
        User       = 'hartonomous'
        Password   = 'hartonomous'
        Database   = 'hartonomous'
        MaintenanceDatabase = 'postgres'
        # Built at runtime if not explicitly overridden.
        ConnectionString = $null
    }

    Seed = @{
        # Relative paths under Paths.SourceRoot that must exist before seeding.
        # Each decomposer's full source footprint — every file/dir the C# code
        # reads must be asserted before Invoke-HartPhase runs. Paths mirror the
        # subpaths hardcoded in src/Hartonomous.Cli/Program.cs.
        Ucd                  = 'Unicode\Public\UCD\latest'
        Uca                  = 'Unicode\Public\UCD\latest\uca\allkeys.txt'
        Iso639               = 'ISO639\iso-639-3.tab'
        WordNet              = 'Wordnet\WordNet-3.0\dict'
        Omw                  = 'omw'
        ModelHub             = 'hub'
        # Wiktionary: the decomposer streams one multi-GB JSONL produced by wiktextract.
        # Both the parent dir and the file are asserted so a missing-or-moved JSONL
        # fails loudly before the phase starts.
        WiktionaryRoot       = 'Wiktionary'
        WiktionaryJsonl      = 'Wiktionary\raw-wiktextract-data.jsonl'
        # Universal Dependencies: every UD_{Lang}-{Bank}/*.conllu file across every
        # treebank in v2.17. The decomposer enumerates directories recursively, so
        # we assert the tree root; the scanner fails per-treebank if a .conllu is
        # missing.
        UniversalDepsRoot    = 'UD-Treebanks\ud-treebanks-v2.17'
        # Tatoeba: three-pass decomposer consumes sentences.csv, links.csv, and
        # audio/sentences_with_audio.csv. All three are asserted; audio is optional
        # at decompose-time but required for a complete seeded substrate.
        TatoebaRoot          = 'Tatoeba'
        TatoebaSentences     = 'Tatoeba\sentences.csv'
        TatoebaLinks         = 'Tatoeba\links.csv'
        TatoebaAudioManifest = 'Tatoeba\audio\sentences_with_audio.csv'
        # Text: test documents for TextDecomp phase.
        TextRoot             = 'test_data\text'
    }

    # ── Native Windows install layout ─────────────────────────────────────
    # Used by scripts/build/PgExtension.ps1 (Windows target) and
    # scripts/install/Repair-WindowsRuntime.ps1. Every value is overridable
    # via env var (HARTONOMOUS_WINDOWSNATIVE__*).
    WindowsNative = @{
        # Candidate Postgres install roots, scanned in order. First one that
        # contains lib\postgres.lib wins. Pin a specific version with
        # HARTONOMOUS_WINDOWSNATIVE__PGROOT=...
        PgRootCandidates = @(
            'C:\Program Files\PostgreSQL\18',
            'C:\Program Files\PostgreSQL\17',
            'C:\Program Files\PostgreSQL\16'
        )
        # Intel oneAPI install root. setvars.bat is the canonical sentinel.
        IntelOneApiRoot  = 'C:\Program Files (x86)\Intel\oneAPI'
        # Visual Studio: discovered via vswhere.exe; this is the override.
        VsInstallRoot    = $null
        # Runtime DLL closure libhartonomous.dll links against. Each entry is
        # a (subdir-under-oneAPI, glob) tuple. Staged into PG bin\ on install.
        IntelRuntimeDlls = @(
            @{ Subdir = 'mkl\latest\bin';      Pattern = 'mkl_core.*.dll' },
            @{ Subdir = 'mkl\latest\bin';      Pattern = 'mkl_def.*.dll' },
            @{ Subdir = 'mkl\latest\bin';      Pattern = 'mkl_avx2.*.dll' },
            @{ Subdir = 'mkl\latest\bin';      Pattern = 'mkl_avx512.*.dll' },
            @{ Subdir = 'mkl\latest\bin';      Pattern = 'mkl_intel_thread.*.dll' },
            @{ Subdir = 'mkl\latest\bin';      Pattern = 'mkl_rt.*.dll' },
            @{ Subdir = 'compiler\latest\bin'; Pattern = 'libiomp5md.dll' },
            @{ Subdir = 'compiler\latest\bin'; Pattern = 'svml_dispmd.dll' }
        )
    }

    Logging = @{
        Console = 'Info'      # Trace|Debug|Info|Warn|Error
        File    = 'Debug'
        # File naming format. Substituted: {Script}, {Date}, {Pid}.
        FileNameFormat = 'hartonomous-{Date}-{Script}-{Pid}.log'
    }

    # sysexits.h-style exit codes (POSIX standard).
    ExitCodes = @{
        Ok           = 0
        GenericError = 1
        Usage        = 64
        DataError    = 65
        NoInput      = 66
        NoUser       = 67
        NoHost       = 68
        Unavailable  = 69   # docker daemon down, service unreachable
        Software     = 70   # internal logic error
        OsError      = 71
        OsFile       = 72
        CantCreate   = 73
        IoError      = 74
        TempFail     = 75
        Protocol     = 76
        NoPerm       = 77
        Config       = 78
    }
}
