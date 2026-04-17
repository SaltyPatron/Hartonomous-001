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
        SourceRoot       = 'D:\Models'
        Logs             = 'logs'
        Reports          = 'reports'
        Artifacts        = 'artifacts'
        NativeBuild      = 'ext\libhartonomous\build'
        PgExtensionSrc   = 'ext\hartonomous_pg'
        LibHartonomousSrc= 'ext\libhartonomous'
        Solution         = 'Hartonomous.slnx'
        MigrationsDir    = 'sql\migrations'
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
        Ucd     = 'UCD\Public\UCD\latest\ucdxml\ucd.all.grouped.xml'
        Uca     = 'UCD\Public\UCD\latest\uca\allkeys.txt'
        Iso639  = 'ISO639\iso-639-3.tab'
        WordNet = 'princeton-wordnet\WordNet-3.0\dict'
        Omw     = 'omw'
        ModelHub= 'hub'
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
