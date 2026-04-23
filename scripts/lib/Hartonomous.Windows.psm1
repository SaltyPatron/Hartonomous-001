# Hartonomous.Windows — native Windows install/repair helpers.
#
# Idempotent primitives for staging the hartonomous PG extension and its
# Intel oneAPI runtime DLL closure into a local PostgreSQL install on
# Windows. Every primitive is safe to re-run.
#
# Discovery: PostgreSQL install root, Intel oneAPI root, Visual Studio dev
# tools (vswhere.exe). Mutation: idempotent file-sync (skip if dest exists
# with same length+mtime), automatic UAC elevation when writing into Program
# Files.

# ── Discovery ─────────────────────────────────────────────────────────────

function Find-HartPgRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Cfg,
        [string]$Override
    )
    $candidates = @()
    if ($Override)                                  { $candidates += $Override }
    if ($env:HARTONOMOUS_WINDOWSNATIVE__PGROOT)     { $candidates += $env:HARTONOMOUS_WINDOWSNATIVE__PGROOT }
    if ($env:PGROOT)                                { $candidates += $env:PGROOT }
    $candidates += $Cfg.WindowsNative.PgRootCandidates

    foreach ($c in $candidates) {
        if ([string]::IsNullOrWhiteSpace($c)) { continue }
        $libPath = Join-Path $c 'lib\postgres.lib'
        if (Test-Path $libPath) {
            Write-HartDebug "PgRoot resolved: $c"
            return (Resolve-Path $c).Path
        }
    }
    throw "No PostgreSQL install root found. Tried: $($candidates -join '; '). Set HARTONOMOUS_WINDOWSNATIVE__PGROOT or pass -PgRoot."
}

function Find-HartIntelOneApi {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Cfg,
        [string]$Override
    )
    $root = $Override
    if (-not $root) { $root = $env:HARTONOMOUS_WINDOWSNATIVE__INTELONEAPIROOT }
    if (-not $root) { $root = $Cfg.WindowsNative.IntelOneApiRoot }
    if (-not (Test-Path (Join-Path $root 'setvars.bat'))) {
        throw "Intel oneAPI root not found at '$root' (no setvars.bat). Set HARTONOMOUS_WINDOWSNATIVE__INTELONEAPIROOT or install the toolkit."
    }
    Write-HartDebug "IntelOneApiRoot resolved: $root"
    return (Resolve-Path $root).Path
}

function Find-HartVsDevCmd {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Cfg)

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        # Fall back to "already in dev prompt" if both tools are on PATH.
        if ((Get-Command cl -ErrorAction SilentlyContinue) -and (Get-Command nmake -ErrorAction SilentlyContinue)) {
            Write-HartDebug 'vswhere absent; cl/nmake already on PATH — using ambient dev environment.'
            return $null
        }
        throw "vswhere.exe not found at '$vswhere'. Install Visual Studio (any edition with C++ workload) or run inside an x64 Native Tools Command Prompt."
    }

    $vsArgs = @('-latest','-prerelease','-products','*',
                '-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64',
                '-property','installationPath','-format','value')
    $vsRoot = & $vswhere @vsArgs | Select-Object -First 1
    if (-not $vsRoot) {
        throw "vswhere found no Visual Studio install with VC.Tools.x86.x64. Install the C++ workload."
    }

    $vcvars = Join-Path $vsRoot 'VC\Auxiliary\Build\vcvars64.bat'
    if (-not (Test-Path $vcvars)) {
        throw "vcvars64.bat not found at '$vcvars'."
    }
    Write-HartDebug "VS dev tools resolved: $vcvars"
    return $vcvars
}

# ── Privilege ──────────────────────────────────────────────────────────────

function Test-HartIsAdmin {
    [CmdletBinding()] param()
    return ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-HartElevated {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [scriptblock]$ScriptBlock,
        [string]$Description = 'elevated operation'
    )
    if (Test-HartIsAdmin) {
        Write-HartDebug "Already admin — running '$Description' inline."
        & $ScriptBlock
        return
    }
    Write-HartInfo "Elevation requested for: $Description"

    $tmp = [System.IO.Path]::GetTempFileName()
    Rename-Item -LiteralPath $tmp -NewName ([System.IO.Path]::ChangeExtension($tmp,'ps1'))
    $tmp = [System.IO.Path]::ChangeExtension($tmp,'ps1')
    $exitFile = "$tmp.exit"

    $body = @"
`$ErrorActionPreference = 'Stop'
try {
$($ScriptBlock.ToString())
    [int]0 | Out-File -LiteralPath '$exitFile' -Encoding ascii
} catch {
    `$_.ToString() | Out-File -LiteralPath '$exitFile.err' -Encoding utf8
    [int]1 | Out-File -LiteralPath '$exitFile' -Encoding ascii
}
"@
    Set-Content -LiteralPath $tmp -Value $body -Encoding utf8

    try {
        $p = Start-Process pwsh -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$tmp) -Verb RunAs -Wait -PassThru
        $exit = if (Test-Path $exitFile) { [int](Get-Content -LiteralPath $exitFile -Raw).Trim() } else { $p.ExitCode }
        if ($exit -ne 0) {
            $errMsg = if (Test-Path "$exitFile.err") { Get-Content -LiteralPath "$exitFile.err" -Raw } else { "exit=$exit" }
            throw "Elevated step '$Description' failed: $errMsg"
        }
    }
    finally {
        Remove-Item -LiteralPath $tmp,$exitFile,"$exitFile.err" -Force -ErrorAction SilentlyContinue
    }
}

# ── Idempotent file sync ───────────────────────────────────────────────────

function Sync-HartFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string]$Destination
    )
    if (-not (Test-Path -LiteralPath $Source)) {
        throw "Sync source missing: $Source"
    }
    $src = Get-Item -LiteralPath $Source
    if (Test-Path -LiteralPath $Destination) {
        $dst = Get-Item -LiteralPath $Destination
        if ($dst.Length -eq $src.Length -and $dst.LastWriteTimeUtc -ge $src.LastWriteTimeUtc) {
            Write-HartTrace "skip (up-to-date): $Destination"
            return $false  # no change
        }
    }
    $dstDir = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $dstDir)) {
        New-Item -ItemType Directory -Path $dstDir -Force | Out-Null
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
    Write-HartDebug "copied: $Source -> $Destination"
    return $true  # changed
}

function Sync-HartFileElevated {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string[]]$Sources,
        [Parameter(Mandatory)] [string]$DestinationDir,
        [string]$Description = "stage $($Sources.Count) file(s)"
    )
    # Pre-flight: which sources are actually different from what's at dest?
    $needCopy = @()
    foreach ($s in $Sources) {
        $name = Split-Path -Leaf $s
        $d = Join-Path $DestinationDir $name
        $up = $false
        if (Test-Path -LiteralPath $d) {
            $sf = Get-Item -LiteralPath $s
            $df = Get-Item -LiteralPath $d
            $up = ($sf.Length -eq $df.Length -and $df.LastWriteTimeUtc -ge $sf.LastWriteTimeUtc)
        }
        if (-not $up) { $needCopy += [pscustomobject]@{ Src = $s; Dst = $d } }
    }
    if ($needCopy.Count -eq 0) {
        Write-HartInfo "$Description — already up-to-date ($($Sources.Count) files)."
        return 0
    }

    $writableProbe = Join-Path $DestinationDir ".hart-writeprobe-$([guid]::NewGuid().Guid).tmp"
    $needsElevation = $false
    try {
        Set-Content -LiteralPath $writableProbe -Value 'x' -ErrorAction Stop
        Remove-Item -LiteralPath $writableProbe -ErrorAction SilentlyContinue
    } catch { $needsElevation = $true }

    $pairs = @($needCopy | ForEach-Object { "@{Src='$($_.Src.Replace("'", "''"))'; Dst='$($_.Dst.Replace("'", "''"))'}" }) -join ','
    $body = [scriptblock]::Create(@"
`$pairs = @($pairs)
foreach (`$p in `$pairs) {
    `$dir = Split-Path -Parent `$p.Dst
    if (-not (Test-Path -LiteralPath `$dir)) { New-Item -ItemType Directory -Path `$dir -Force | Out-Null }
    Copy-Item -LiteralPath `$p.Src -Destination `$p.Dst -Force
}
"@)

    if ($needsElevation) {
        Invoke-HartElevated -ScriptBlock $body -Description $Description
    } else {
        & $body
    }
    Write-HartInfo "$Description — staged $($needCopy.Count) file(s) into $DestinationDir."
    return $needCopy.Count
}

# ── Intel runtime staging ─────────────────────────────────────────────────

function Get-HartIntelRuntimeFiles {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Cfg,
        [string]$IntelRoot
    )
    if (-not $IntelRoot) { $IntelRoot = Find-HartIntelOneApi -Cfg $Cfg }
    $files = @()
    foreach ($entry in $Cfg.WindowsNative.IntelRuntimeDlls) {
        $dir = Join-Path $IntelRoot $entry.Subdir
        if (-not (Test-Path -LiteralPath $dir)) {
            throw "Intel runtime dir missing: $dir"
        }
        $matches = @(Get-ChildItem -LiteralPath $dir -Filter $entry.Pattern -ErrorAction SilentlyContinue)
        if ($matches.Count -eq 0) {
            throw "No Intel runtime DLLs match '$($entry.Pattern)' in $dir"
        }
        $files += $matches.FullName
    }
    return $files
}

# ── Postgres service ──────────────────────────────────────────────────────

function Get-HartPgService {
    [CmdletBinding()] param([string]$NameLike = 'postgresql-x64-*')
    return Get-Service -Name $NameLike -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Restart-HartPgService {
    [CmdletBinding()] param([string]$ServiceName)
    if (-not $ServiceName) {
        $svc = Get-HartPgService
        if (-not $svc) { Write-HartWarn 'No postgresql-x64-* service found; skip restart.'; return }
        $ServiceName = $svc.Name
    }
    Invoke-HartElevated -ScriptBlock ([scriptblock]::Create("Restart-Service -Name '$ServiceName' -Force")) `
                        -Description "restart $ServiceName"
}

Export-ModuleMember -Function `
    Find-HartPgRoot, Find-HartIntelOneApi, Find-HartVsDevCmd, `
    Test-HartIsAdmin, Invoke-HartElevated, `
    Sync-HartFile, Sync-HartFileElevated, `
    Get-HartIntelRuntimeFiles, `
    Get-HartPgService, Restart-HartPgService
