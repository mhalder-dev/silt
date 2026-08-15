<#
.SYNOPSIS
    Produce Silt's shippable payload: the SPA, then a self-contained single-file win-x64 exe.

.DESCRIPTION
    One definition of "what ships", used by the release workflow and by anyone building
    locally, for the same reason scripts/safety-gates.ps1 exists: two copies of a build
    recipe drift, and the one that drifts is always the one nobody runs.

    Order matters. The frontend MUST be built before `dotnet publish`, because the shell
    project collects src/frontend/dist into wwwroot during AssignTargetPaths. A stale or
    absent dist/ does not fail the publish - it produces an installer whose app shows the
    "run npm run build" placeholder to the user. The verification block at the end exists
    precisely because that failure is silent.

.PARAMETER OutputDirectory
    Where the publish payload lands. Defaults to <repo>/artifacts/publish.

.PARAMETER SkipFrontend
    Reuse an existing src/frontend/dist. Only for iterating on the C# side.

.PARAMETER Version
    Overrides the version stamped into Silt.exe. The release workflow passes the git tag.
    Omit it locally - Directory.Build.props supplies the development default.

.EXAMPLE
    pwsh scripts/publish.ps1
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [switch] $SkipFrontend,
    [string] $Version
)

$ErrorActionPreference = 'Stop'

$repo = if ($PSScriptRoot) { Split-Path -Parent $PSScriptRoot } else { (Get-Location).Path }
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path (Join-Path $repo 'artifacts') 'publish'
}

$frontend = Join-Path (Join-Path $repo 'src') 'frontend'
$shellProject = Join-Path $repo 'src\shell\Silt.Shell\Silt.Shell.csproj'

if (-not $SkipFrontend) {
    Write-Host '==> Building the SPA'
    # `npm ci` rather than `npm install`: the lockfile is the contract, and a release must
    # not silently float a transitive dependency forward.
    Push-Location $frontend
    try {
        npm ci
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed ($LASTEXITCODE)" }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed ($LASTEXITCODE)" }
    }
    finally { Pop-Location }
}

if (Test-Path $OutputDirectory) {
    # A stale payload is worse than none: a renamed asset would leave the old hashed bundle
    # behind, and index.html would still reference whichever one happened to survive.
    Remove-Item $OutputDirectory -Recurse -Force
}

Write-Host '==> Publishing the shell (self-contained, single file, win-x64)'
# No -p: overrides here. SelfContained / PublishSingleFile / RuntimeIdentifier are set in the
# csproj under Configuration=Release, with the reasoning next to them. Restating them on the
# command line would let the two disagree, and the command line would win silently.
$publishArgs = @('publish', $shellProject, '-c', 'Release', '-o', $OutputDirectory)
if ($Version) { $publishArgs += "-p:Version=$Version" }
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Write-Host '==> Verifying the payload'

$problems = @()

$exe = Join-Path $OutputDirectory 'Silt.exe'
if (-not (Test-Path $exe)) { $problems += 'Silt.exe is missing.' }

# The shell serves AppContext.BaseDirectory\wwwroot. Without these three the installer would
# ship a working window with no application in it.
foreach ($required in @('wwwroot\index.html', 'wwwroot\assets')) {
    if (-not (Test-Path (Join-Path $OutputDirectory $required))) {
        $problems += "$required is missing - was the SPA built before publish?"
    }
}

$bundles = @(Get-ChildItem (Join-Path $OutputDirectory 'wwwroot\assets') -Filter *.js -ErrorAction SilentlyContinue)
if ($bundles.Count -eq 0) { $problems += 'wwwroot\assets contains no JavaScript bundle.' }

# Self-contained means the WPF framework travels with us. Verified on this machine before
# this check was written: Microsoft.WindowsDesktop.App is present at 9.0.14 only, with no
# 10.x entry, so a framework-dependent build dies at launch on a clean machine with the
# OS runtime-missing dialog. A single-file publish embeds those assemblies, so the evidence
# is the exe's size, not a file listing - a framework-dependent Silt.exe is a few hundred KB.
if (Test-Path $exe) {
    $exeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    if ($exeMb -lt 60) {
        $problems += "Silt.exe is only $exeMb MB - that is not a self-contained publish."
    }
    Write-Host "    Silt.exe            $exeMb MB"
}

# index.html must reference the bundle that is actually present. A stale dist/ passes every
# check above and still produces a blank window.
$indexPath = Join-Path $OutputDirectory 'wwwroot\index.html'
if ((Test-Path $indexPath) -and $bundles.Count -gt 0) {
    $index = Get-Content $indexPath -Raw
    $referenced = $bundles | Where-Object { $index -match [regex]::Escape($_.Name) }
    if (-not $referenced) {
        $problems += 'index.html references no bundle present in wwwroot\assets - dist/ is stale.'
    }
}

if ($problems) {
    foreach ($p in $problems) { Write-Host "::error::$p" }
    exit 1
}

$totalMb = [math]::Round((Get-ChildItem $OutputDirectory -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "    payload total       $totalMb MB"
Write-Host "OK - payload verified at $OutputDirectory"
