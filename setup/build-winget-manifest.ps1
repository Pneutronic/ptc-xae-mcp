# Writes the winget manifests for one release of ptc-xae-mcp.
#
#   powershell -ExecutionPolicy Bypass -File setup\build-winget-manifest.ps1
#
# Reads <Version> from Directory.Build.props and the SHA-256 from
# setup\dist\ptc-xae-mcp-setup-<Version>.exe.sha256 (written by build-installer.ps1), and writes
#
#   setup\dist\winget\manifests\p\Pneutronic\XaeMcp\<Version>\
#       Pneutronic.XaeMcp.yaml                 (version manifest)
#       Pneutronic.XaeMcp.installer.yaml       (installer: the GitHub release asset + its hash)
#       Pneutronic.XaeMcp.locale.en-US.yaml    (default locale)
#
# The folder layout is the one microsoft/winget-pkgs expects, so publishing a release is:
#   1. build-installer.ps1, create the GitHub release, upload the .exe and the .sha256;
#   2. run this script (the InstallerUrl points at that release asset);
#   3. validate locally:  winget validate --manifest setup\dist\winget\manifests\p\Pneutronic\XaeMcp\<Version>
#      and, if you want:  winget install --manifest <that folder>   (needs "winget settings" -> LocalManifestFiles)
#   4. open a pull request to https://github.com/microsoft/winget-pkgs with that folder
#      (or: wingetcreate submit <that folder>). A person does this; nothing here pushes.
#
# -Sha256 <hex> overrides the hash (e.g. before the installer is built, for a dry run);
# -Version <x.y.z> overrides the version; -OutDir <folder> the output root.
[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$Sha256 = '',
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

$packageId = 'Pneutronic.XaeMcp'
$manifestVersion = '1.10.0'
$releaseBase = 'https://github.com/Pneutronic/ptc-xae-mcp'
# AppId from setup.iss; Inno Setup registers the uninstaller as "<AppId>_is1".
$productCode = '{E4B7C2A1-3F8D-4E56-9A0C-1D2E3F4A5B6C}_is1'

if (-not $Version) {
    [xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
    $Version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
    if (-not $Version) { throw 'No <Version> in Directory.Build.props.' }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be x.y.z (got '$Version')." }

$exeName = "ptc-xae-mcp-setup-$Version.exe"
if (-not $Sha256) {
    $shaFile = Join-Path $PSScriptRoot "dist\$exeName.sha256"
    if (-not (Test-Path $shaFile)) {
        throw "No $shaFile. Run build-installer.ps1 first, or pass -Sha256 <hex> for a dry run."
    }
    $Sha256 = ((Get-Content $shaFile -Raw) -split '\s+')[0]
}
$Sha256 = $Sha256.Trim().ToUpperInvariant()
if ($Sha256 -notmatch '^[0-9A-F]{64}$') { throw "Sha256 must be 64 hex characters (got '$Sha256')." }

if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'dist\winget' }
$folder = Join-Path $OutDir "manifests\p\Pneutronic\XaeMcp\$Version"
New-Item -ItemType Directory -Force $folder | Out-Null

$installerUrl = "$releaseBase/releases/download/v$Version/$exeName"
$releaseNotesUrl = "$releaseBase/releases/tag/v$Version"

$versionYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$manifestVersion.schema.json
PackageIdentifier: $packageId
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $manifestVersion
"@

$installerYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$manifestVersion.schema.json
PackageIdentifier: $packageId
PackageVersion: $Version
InstallerType: inno
Scope: machine
InstallModes:
- interactive
- silent
- silentWithProgress
UpgradeBehavior: install
MinimumOSVersion: 10.0.19041.0
ProductCode: '$productCode'
AppsAndFeaturesEntries:
- DisplayName: Pneutronic XAE MCP
  Publisher: Pneutronic AG
  ProductCode: '$productCode'
ReleaseDate: $(Get-Date -Format 'yyyy-MM-dd')
Installers:
- Architecture: x64
  InstallerUrl: $installerUrl
  InstallerSha256: $Sha256
ManifestType: installer
ManifestVersion: $manifestVersion
"@

$localeYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$manifestVersion.schema.json
PackageIdentifier: $packageId
PackageVersion: $Version
PackageLocale: en-US
Publisher: Pneutronic AG
PublisherUrl: https://www.pneutronic.ch
PublisherSupportUrl: $releaseBase/issues
PackageName: Pneutronic XAE MCP
PackageUrl: $releaseBase
License: Apache-2.0
LicenseUrl: $releaseBase/blob/master/LICENSE
Copyright: Copyright (c) Pneutronic AG
ShortDescription: MCP server that lets an AI assistant operate the Beckhoff TwinCAT XAE on this machine.
Description: |-
  Pneutronic XAE MCP is a local Model Context Protocol (MCP) server for the Beckhoff TwinCAT 3 XAE.
  It lets an MCP client such as Claude Code open and build projects, read and write PLC code,
  manage libraries, drive login/download, read ADS variables and inspect the EtherCAT topology
  through the TwinCAT Automation Interface. It binds to localhost only and needs a licensed
  TwinCAT XAE installation on the same machine. Independent project, not a Beckhoff product.
Moniker: ptc-xae-mcp
Tags:
- beckhoff
- twincat
- plc
- mcp
- automation
ReleaseNotesUrl: $releaseNotesUrl
ManifestType: defaultLocale
ManifestVersion: $manifestVersion
"@

$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $folder "$packageId.yaml"), $versionYaml + "`n", $utf8)
[IO.File]::WriteAllText((Join-Path $folder "$packageId.installer.yaml"), $installerYaml + "`n", $utf8)
[IO.File]::WriteAllText((Join-Path $folder "$packageId.locale.en-US.yaml"), $localeYaml + "`n", $utf8)

Write-Host "winget manifests: $folder"
Write-Host "Installer URL:    $installerUrl"
Write-Host "SHA256:           $Sha256"
Write-Host "Next: winget validate --manifest `"$folder`" and a pull request to microsoft/winget-pkgs."
