<#
.SYNOPSIS
    Silt's safety gates. One definition, used by both CI and local runs.

.DESCRIPTION
    These enforce the two structural invariants that keep a disk-cleaning tool from
    destroying data:

      1. Only SandboxedFileSystem mutates the filesystem.
      2. Nothing opens a TCP listener.

    This lives in a script rather than inline in the workflow because the two copies drifted:
    a local check running an older pattern passed while CI failed, which is precisely the
    situation where a gate stops being trustworthy. There is now one definition and no way
    for the local answer to disagree with CI's.

.EXAMPLE
    pwsh scripts/safety-gates.ps1
#>
[CmdletBinding()]
param(
    [string] $SourceRoot
)

$ErrorActionPreference = 'Stop'

# Resolved at runtime rather than as a parameter default, and with nested two-argument
# Join-Path calls: three-argument Join-Path is PowerShell 7+ only, and this script has to run
# under Windows PowerShell 5.1 locally as well as pwsh in CI. A gate that only runs in one
# place is how the local and CI checks drifted apart before.
if (-not $SourceRoot) {
    $root = if ($PSScriptRoot) { Split-Path -Parent $PSScriptRoot } else { (Get-Location).Path }
    $SourceRoot = Join-Path $root 'src'
}

if (-not (Test-Path $SourceRoot)) {
    Write-Host "::error::Source root not found: $SourceRoot"
    exit 1
}

# Exemptions are narrow, and each must be EARNED by an enforced containment check rather
# than merely asserted in a comment. Adding a name here without one hollows out the gate.
#
#   SandboxedFileSystem.cs - the funnel itself. Deletion lives here and nowhere else; the
#                            Win32 interop is in that file so no mutation primitive is
#                            callable from anywhere else.
#   SnapshotStore.cs       - writes only its own history directory, guarded by
#                            PathJail.Require.
#   OperationJournal.cs    - appends only to its own audit file, likewise guarded.
#   Diagnostics.cs         - appends only to its own log file, likewise guarded.
#
# Anything that touches USER files belongs behind SandboxedFileSystem, always.
$exemptFiles = @(
    'SandboxedFileSystem.cs',
    'SnapshotStore.cs',
    'OperationJournal.cs',
    'Diagnostics.cs'
)

# Append and write APIs are included deliberately. An earlier version listed only
# destructive calls, which meant a component could create or append to files anywhere and
# pass untouched.
$mutationPattern = 'File\.Delete|Directory\.Delete|File\.Move|File\.Copy|File\.Create|' +
                   'File\.WriteAllBytes|File\.WriteAllText|File\.AppendAllText|' +
                   'File\.AppendAllLines|FileInfo\.Delete|NtSetInformationFile|SHFileOperation'

# Silt's API is reached through WebView2 resource interception, never a socket. A loopback
# port would be reachable by every other process on the machine and by any page in any
# browser - unacceptable for a component that deletes files.
$listenerPattern = 'ListenLocalhost|ListenAnyIP|UseUrls|HttpListener|TcpListener'

# Note: Get-ChildItem -Recurse, not a '**' glob. PowerShell provider wildcards do NOT
# implement recursive '**' - an earlier version used one and silently matched 1 file in 3,
# exempting every subdirectory from the gate that called itself Layer 1.
$sourceFiles = Get-ChildItem -Path $SourceRoot -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }

Write-Host "Scanning $($sourceFiles.Count) C# files under $SourceRoot"

$failed = $false

$mutationHits = $sourceFiles |
    Where-Object { $exemptFiles -notcontains $_.Name } |
    Select-String -Pattern $mutationPattern

if ($mutationHits) {
    $failed = $true
    Write-Host '::error::Filesystem mutation found outside SandboxedFileSystem:'
    $mutationHits | ForEach-Object {
        Write-Host "  $($_.Path):$($_.LineNumber)  $($_.Line.Trim())"
    }
} else {
    Write-Host 'OK - no filesystem mutation outside the funnel.'
}

$listenerHits = $sourceFiles | Select-String -Pattern $listenerPattern

if ($listenerHits) {
    $failed = $true
    Write-Host '::error::TCP listener found:'
    $listenerHits | ForEach-Object {
        Write-Host "  $($_.Path):$($_.LineNumber)  $($_.Line.Trim())"
    }
} else {
    Write-Host 'OK - no TCP listener.'
}

if ($failed) {
    exit 1
}

Write-Host 'All safety gates passed.'
