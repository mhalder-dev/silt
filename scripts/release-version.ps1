<#
.SYNOPSIS
    Resolve the version a build should be stamped with, from the git ref.

.DESCRIPTION
    The version is the one fact the release pipeline cannot get wrong quietly. It is stamped
    into Silt.exe, read back out of that exe by the installer to name the asset, and used as
    the release title. If it disagrees with the tag, the download page says one thing and the
    binary says another, and nobody finds out until a user reports it.

    This lives in a script for the same reason scripts/safety-gates.ps1 does: the previous
    version was six lines of inline PowerShell in .github/workflows/release.yml, which meant
    it could not be run, could not be tested, and had never executed even once - the release
    workflow is tag-triggered and no tag exists. It carried two defects (below), both of
    which would have fired on the very first use.

    Rules:

      refs/tags/vX.Y.Z    -> X.Y.Z
      refs/tags/<other>   -> ERROR. The workflow triggers on 'v*', so a tag like v0.1.0-rc1
                             or v1.2 reaches here. The inline version fell back to the
                             Directory.Build.props default for those, which does not fail -
                             it creates a release named v0.1.0-rc1 containing an asset named
                             Silt-0.1.0-win-x64-setup.exe stamped 0.1.0. Refusing is the only
                             honest answer; a release whose version is a guess should not exist.
      anything else       -> the Directory.Build.props default (workflow_dispatch dry runs).

    The props read is deliberately InnerText off a resolved node, not property-style dotted
    access. `([xml]$x).Project.PropertyGroup.Version` returned the *string* '0.1.0' until
    <Version> gained a Condition attribute; PowerShell's XML adapter returns an XmlElement
    once an element carries attributes, and stringifying that yields garbage - measured as
    'System.Xml.XmlElement' and, in another context in the same 5.1 session, ' Version'. The
    exact garbage varies; the point is that none of it is a version, and it would have been
    handed to `dotnet publish -p:Version=` on the very first dry run. The mutation test that
    restores the dotted form reports it, so the shape of the props file cannot regress this
    silently a second time.

.PARAMETER Ref
    The full git ref, i.e. the value of github.ref.

.PARAMETER SelfTest
    Run the cases above and exit non-zero if any disagrees. Wired into CI.

.EXAMPLE
    pwsh scripts/release-version.ps1 -Ref refs/tags/v0.1.0
#>
[CmdletBinding()]
param(
    [string] $Ref,
    [string] $PropsPath,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'

$repo = if ($PSScriptRoot) { Split-Path -Parent $PSScriptRoot } else { (Get-Location).Path }
if (-not $PropsPath) { $PropsPath = Join-Path $repo 'Directory.Build.props' }

function Get-DefaultVersion {
    param([Parameter(Mandatory)][string] $Path)

    $xml = [xml](Get-Content -LiteralPath $Path -Raw)
    $node = $xml.SelectSingleNode('/Project/PropertyGroup/Version')
    if (-not $node) { throw "No <Version> element in $Path." }
    return $node.InnerText.Trim()
}

function Resolve-SiltVersion {
    param(
        [string] $Ref,
        [Parameter(Mandatory)][string] $PropsPath
    )

    if ($Ref -match '^refs/tags/(.+)$') {
        $tag = $Matches[1]
        # Anchored, and three components exactly. The release asset filename is derived from
        # the exe's four-part Win32 resource with the revision trimmed, so a two-part tag
        # would not round-trip either.
        if ($tag -notmatch '^v(\d+\.\d+\.\d+)$') {
            throw "Tag '$tag' is not vX.Y.Z. Delete it and push a well-formed tag; a release will not be built from a version this script had to guess."
        }
        return $Matches[1]
    }

    $version = Get-DefaultVersion -Path $PropsPath
    if ($version -notmatch '^\d+\.\d+\.\d+$') {
        throw "Directory.Build.props <Version> is '$version', which is not X.Y.Z."
    }
    return $version
}

if ($SelfTest) {
    $cases = @(
        @{ Name = 'a well-formed tag yields its version'; Ref = 'refs/tags/v1.2.3'; Expect = '1.2.3' },
        @{ Name = 'a prerelease tag is refused, not guessed'; Ref = 'refs/tags/v0.1.0-rc1'; Throws = $true },
        @{ Name = 'a two-component tag is refused'; Ref = 'refs/tags/v1.2'; Throws = $true },
        @{ Name = 'a tag without the v prefix is refused'; Ref = 'refs/tags/1.2.3'; Throws = $true },
        @{ Name = 'a tag that merely contains a version is refused'; Ref = 'refs/tags/release-v1.2.3'; Throws = $true },
        @{ Name = 'a branch ref falls back to the props default'; Ref = 'refs/heads/main'; ExpectPattern = '^\d+\.\d+\.\d+$' },
        @{ Name = 'an empty ref falls back to the props default'; Ref = ''; ExpectPattern = '^\d+\.\d+\.\d+$' }
    )

    $failed = 0
    foreach ($case in $cases) {
        $actual = $null
        $threw = $false
        try { $actual = Resolve-SiltVersion -Ref $case.Ref -PropsPath $PropsPath }
        catch { $threw = $true; $actual = $_.Exception.Message }

        $ok =
            if ($case.Throws) { $threw }
            elseif ($case.Expect) { -not $threw -and $actual -eq $case.Expect }
            else { -not $threw -and $actual -match $case.ExpectPattern }

        if ($ok) {
            Write-Host ("  OK   {0}  ->  {1}" -f $case.Name, $(if ($threw) { 'refused' } else { $actual }))
        } else {
            $failed++
            Write-Host ("::error::FAIL {0}  ->  {1}" -f $case.Name, $actual)
        }
    }

    # The regression that motivated this script. The props default must be the element's
    # text; the dotted-access form returned 'System.Xml.XmlElement' once <Version> gained a
    # Condition attribute, and every check above would still have passed on a branch ref if
    # the pattern were merely 'non-empty'.
    $default = Get-DefaultVersion -Path $PropsPath
    if ($default -match 'Xml' -or $default -notmatch '^\d+\.\d+\.\d+$') {
        $failed++
        Write-Host "::error::FAIL props default is '$default', not a version"
    } else {
        Write-Host "  OK   props default reads as text, not an XmlElement  ->  $default"
    }

    if ($failed) { Write-Host "::error::$failed version-resolution case(s) failed."; exit 1 }
    Write-Host 'All version-resolution cases passed.'
    exit 0
}

Resolve-SiltVersion -Ref $Ref -PropsPath $PropsPath
