# Hartonomous.Build — dotnet + cmake discovery and wrapping.

function Resolve-HartDotnet {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Cfg)
    $cmd = Assert-HartCommand -Name 'dotnet' -InstallHint 'install .NET 9 SDK from https://dotnet.microsoft.com/download'
    $ver = (& dotnet --version 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $ver) { throw 'dotnet --version failed.' }
    Assert-HartMinVersion -Actual $ver -Minimum $Cfg.Dotnet.MinSdk -Label 'dotnet SDK'
    return @{ Path = $cmd.Source; Version = $ver }
}

function Resolve-HartCMake {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Cfg)
    $cmd = Assert-HartCommand -Name 'cmake' -InstallHint 'install CMake >= 3.24'
    $out = (& cmake --version 2>$null)
    if ($LASTEXITCODE -ne 0) { throw 'cmake --version failed.' }
    $first = ($out -split "`n")[0]
    Assert-HartMinVersion -Actual $first -Minimum $Cfg.Native.CMakeMinVersion -Label 'cmake'
    return @{ Path = $cmd.Source; Version = $first }
}

function Resolve-HartVsGenerator {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Cfg)
    # Prefer configured generator, fall back to VS 2022 if vswhere reports 17.x only.
    $vswhere = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\Installer\vswhere.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $vswhere) {
        Write-HartWarn "vswhere not found; using preferred generator as-is: $($Cfg.Native.PreferredGenerator)"
        return $Cfg.Native.PreferredGenerator
    }
    $installs = & $vswhere -all -products '*' -requires 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' -format 'json' | ConvertFrom-Json
    $haveVs18 = $installs | Where-Object { $_.installationVersion -match '^18\.' }
    if ($haveVs18) { return $Cfg.Native.PreferredGenerator }
    $haveVs17 = $installs | Where-Object { $_.installationVersion -match '^17\.' }
    if ($haveVs17) {
        Write-HartWarn "VS 18 (2026) not found; falling back to $($Cfg.Native.FallbackGenerator)."
        return $Cfg.Native.FallbackGenerator
    }
    throw "No MSVC toolchain with VC Tools found via vswhere."
}

function Invoke-HartDotnetBuild {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Cfg,
        [string]$Configuration,
        [switch]$NoRestore
    )
    if (-not $Configuration) { $Configuration = $Cfg.Dotnet.Configuration }
    Resolve-HartDotnet -Cfg $Cfg | Out-Null
    $solution = Join-Path $Cfg.Repo.Root $Cfg.Paths.Solution
    Assert-HartPath -Path $solution -Label 'solution'

    $argv = @('build', $solution, '-c', $Configuration, "--verbosity:$($Cfg.Dotnet.Verbosity)")
    if ($Cfg.Dotnet.NoLogo) { $argv += '--nologo' }
    if ($NoRestore)         { $argv += '--no-restore' }

    Invoke-HartNative -FilePath 'dotnet' -Argv $argv -WorkingDirectory $Cfg.Repo.Root
}

function Invoke-HartDotnetRestore {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Cfg)
    Resolve-HartDotnet -Cfg $Cfg | Out-Null
    Invoke-HartNative -FilePath 'dotnet' -Argv @('restore', (Join-Path $Cfg.Repo.Root $Cfg.Paths.Solution)) `
                      -WorkingDirectory $Cfg.Repo.Root
}

function Invoke-HartDotnetClean {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Cfg, [string]$Configuration)
    if (-not $Configuration) { $Configuration = $Cfg.Dotnet.Configuration }
    Invoke-HartNative -FilePath 'dotnet' -Argv @('clean', (Join-Path $Cfg.Repo.Root $Cfg.Paths.Solution), '-c', $Configuration) `
                      -WorkingDirectory $Cfg.Repo.Root
}

function Invoke-HartDotnetTest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Cfg,
        [string]$Configuration,
        [string]$Filter,
        [string]$Project,
        [switch]$Coverage,
        [switch]$NoBuild
    )
    if (-not $Configuration) { $Configuration = $Cfg.Dotnet.Configuration }
    $target = if ($Project) { $Project } else { Join-Path $Cfg.Repo.Root $Cfg.Paths.Solution }
    $reportsDir = Join-Path $Cfg.Repo.Root $Cfg.Paths.Reports
    if (-not (Test-Path $reportsDir)) { New-Item -ItemType Directory -Path $reportsDir | Out-Null }

    $argv = @('test', $target, '-c', $Configuration, '--nologo', '--logger', "trx;LogFileName=$reportsDir/dotnet-test.trx")
    if ($NoBuild) { $argv += '--no-build' }
    if ($Filter)  { $argv += @('--filter', $Filter) }
    if ($Coverage) {
        $argv += @('--collect', 'XPlat Code Coverage', '--results-directory', $reportsDir)
    }
    Invoke-HartNative -FilePath 'dotnet' -Argv $argv -WorkingDirectory $Cfg.Repo.Root
}

function Invoke-HartCMakeConfigure {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Cfg,
        [string]$Configuration,
        [switch]$WithTests
    )
    if (-not $Configuration) { $Configuration = $Cfg.Native.Configuration }
    Resolve-HartCMake -Cfg $Cfg | Out-Null

    $src = Join-Path $Cfg.Repo.Root $Cfg.Paths.LibHartonomousSrc
    $build = Join-Path $Cfg.Repo.Root $Cfg.Paths.NativeBuild
    Assert-HartPath -Path $src -Label 'libhartonomous src'
    if (-not (Test-Path $build)) { New-Item -ItemType Directory -Path $build | Out-Null }

    $argv = @('-S', $src, '-B', $build, "-DCMAKE_BUILD_TYPE=$Configuration")
    $argv += "-DHARTONOMOUS_BUILD_TESTS=$(if ($WithTests -or $Cfg.Native.BuildTests) {'ON'} else {'OFF'})"
    $argv += "-DHARTONOMOUS_BUILD_SHARED=$(if ($Cfg.Native.BuildShared) {'ON'} else {'OFF'})"

    if ($IsWindows) {
        $gen = Resolve-HartVsGenerator -Cfg $Cfg
        $argv += @('-G', $gen, '-A', $Cfg.Native.Arch)
    }
    Invoke-HartNative -FilePath 'cmake' -Argv $argv
}

function Invoke-HartCMakeBuild {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Cfg,
        [string]$Configuration
    )
    if (-not $Configuration) { $Configuration = $Cfg.Native.Configuration }
    $build = Join-Path $Cfg.Repo.Root $Cfg.Paths.NativeBuild
    Assert-HartPath -Path $build -Label 'cmake build dir'
    Invoke-HartNative -FilePath 'cmake' -Argv @('--build', $build, '--config', $Configuration, '--parallel')
}

function Invoke-HartCMakeClean {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Cfg)
    $build = Join-Path $Cfg.Repo.Root $Cfg.Paths.NativeBuild
    if (Test-Path $build) {
        Remove-Item -Recurse -Force $build
        Write-HartInfo "Removed $build"
    } else {
        Write-HartInfo "No native build dir to clean."
    }
}

function Invoke-HartCTest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Cfg,
        [string]$Configuration
    )
    if (-not $Configuration) { $Configuration = $Cfg.Native.Configuration }
    $build = Join-Path $Cfg.Repo.Root $Cfg.Paths.NativeBuild
    Assert-HartPath -Path $build -Label 'cmake build dir'
    Invoke-HartNative -FilePath 'ctest' -Argv @('-C', $Configuration, '--output-on-failure', '--no-tests=error') `
                      -WorkingDirectory $build
}

Export-ModuleMember -Function `
    Resolve-HartDotnet, Resolve-HartCMake, Resolve-HartVsGenerator, `
    Invoke-HartDotnetBuild, Invoke-HartDotnetRestore, Invoke-HartDotnetClean, Invoke-HartDotnetTest, `
    Invoke-HartCMakeConfigure, Invoke-HartCMakeBuild, Invoke-HartCMakeClean, Invoke-HartCTest
