; Pneutronic XAE MCP (ptc-xae-mcp) Inno Setup Script
; Requires Inno Setup 6.x: https://jrsoftware.org/isinfo.php
;
; Standard-Weg (Publish + Compile + .sha256 in einem Schritt):
;   powershell -ExecutionPolicy Bypass -File setupuild-installer.ps1
; Jedes Release braucht die .sha256 und eine höhere <Version> als das letzte.
;
; Manuell: Inno Setup öffnen → diese Datei öffnen → Build → Compile
; Voraussetzung: dotnet publish zuerst ausführen (siehe unten)
;
; AppVersion is passed in from the command line (issue #157 item 3) so the
; installer name/version tracks the single <Version> in Directory.Build.props
; instead of a second hardcoded copy. Full publish + compile sequence:
;
;   dotnet publish src\PtcXaeMcp.WinForms\PtcXaeMcp.WinForms.csproj ^
;     -c Release -r win-x64 --self-contained true ^
;     -o setup\publish
;   iscc setup\setup.iss /DAppVersion=1.3.0
;
; (Read the version to pass with e.g.
;   dotnet msbuild Directory.Build.props -getProperty:Version
; so it never drifts from the assembly.) Compiling without /DAppVersion falls
; back to the DefaultAppVersion below and still works from the IDE.
#define DefaultAppVersion "2.2.1"
#ifndef AppVersion
  #define AppVersion DefaultAppVersion
#endif

#define AppName     "Pneutronic XAE MCP"
#define Publisher   "Pneutronic AG"
#define AppExe      "ptc-xae-mcp.exe"
#define PublishDir  "publish"

[Setup]
AppId={{E4B7C2A1-3F8D-4E56-9A0C-1D2E3F4A5B6C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL=https://www.pneutronic.ch
; The install folder keeps the technical name {autopf}\Pneutronic\ptc-xae-mcp\ptc-xae-mcp.exe;
; update scripts look for it there.
DefaultDirName={autopf}\Pneutronic\ptc-xae-mcp
DefaultGroupName=Pneutronic\{#AppName}
OutputDir=dist
OutputBaseFilename=ptc-xae-mcp-setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\PtcXaeMcp.WinForms\favicon.ico
MinVersion=10.0.19041

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Shortcuts from versions before the rename to "Pneutronic XAE MCP" (<= 1.4.0).
Type: files; Name: "{group}\ptc-xae-mcp.lnk"
Type: files; Name: "{group}\ptc-xae-mcp deinstallieren.lnk"
Type: files; Name: "{commondesktop}\ptc-xae-mcp.lnk"

[Icons]
Name: "{group}\{#AppName}";         Filename: "{app}\{#AppExe}"
Name: "{group}\{#AppName} deinstallieren"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: desktopicon; Description: "Desktop-Verknüpfung erstellen"; GroupDescription: "Zusätzliche Symbole:"; Flags: unchecked

