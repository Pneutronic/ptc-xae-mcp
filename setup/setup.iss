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
#define DefaultAppVersion "4.2.0"
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
; The wizard follows the language of Windows. English comes first and is the fallback; the language dialog only
; appears when Windows is none of the languages below. Silent installs (/SILENT, winget) never show it;
; /LANG=<name> (e.g. /LANG=french) forces a language. The language of the wizard is left for the app
; (HKLM\SOFTWARE\Pneutronic\ptc-xae-mcp, value InstallLanguage) as its language for the first start.
ShowLanguageDialog=auto
LanguageDetectionMethod=uilanguage

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "czech"; MessagesFile: "compiler:Languages\Czech.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[CustomMessages]
; The name of the uninstall shortcut in the start menu group, one per wizard language. English and German keep the
; names of earlier versions; the others are the wording of the Inno Setup language file ("UninstallProgram").
; Every name is also in [InstallDelete], so a reinstall in another language leaves no second shortcut behind.
english.UninstallShortcut=Uninstall {#AppName}
german.UninstallShortcut={#AppName} deinstallieren
french.UninstallShortcut=Désinstaller {#AppName}
italian.UninstallShortcut=Disinstalla {#AppName}
spanish.UninstallShortcut=Desinstalar {#AppName}
brazilianportuguese.UninstallShortcut=Desinstalar o {#AppName}
dutch.UninstallShortcut=Verwijder {#AppName}
polish.UninstallShortcut=Dezinstalacja aplikacji {#AppName}
turkish.UninstallShortcut={#AppName} uygulamasını kaldır
czech.UninstallShortcut=Odinstalovat aplikaci {#AppName}
russian.UninstallShortcut=Деинсталлировать {#AppName}
japanese.UninstallShortcut={#AppName} をアンインストールする
korean.UninstallShortcut={#AppName} 제거

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Shortcuts from versions before the rename to "Pneutronic XAE MCP" (<= 1.4.0).
Type: files; Name: "{group}\ptc-xae-mcp.lnk"
Type: files; Name: "{group}\ptc-xae-mcp deinstallieren.lnk"
Type: files; Name: "{commondesktop}\ptc-xae-mcp.lnk"
; The uninstall shortcut of the other language, so a reinstall in a different language leaves no second one behind.
Type: files; Name: "{group}\{#AppName} deinstallieren.lnk"
Type: files; Name: "{group}\Uninstall {#AppName}.lnk"
Type: files; Name: "{group}\Désinstaller {#AppName}.lnk"
Type: files; Name: "{group}\Disinstalla {#AppName}.lnk"
Type: files; Name: "{group}\Desinstalar {#AppName}.lnk"
Type: files; Name: "{group}\Desinstalar o {#AppName}.lnk"
Type: files; Name: "{group}\Verwijder {#AppName}.lnk"
Type: files; Name: "{group}\Dezinstalacja aplikacji {#AppName}.lnk"
Type: files; Name: "{group}\Odinstalovat aplikaci {#AppName}.lnk"
Type: files; Name: "{group}\Деинсталлировать {#AppName}.lnk"
Type: files; Name: "{group}\{#AppName} uygulamasını kaldır.lnk"
Type: files; Name: "{group}\{#AppName} をアンインストールする.lnk"
Type: files; Name: "{group}\{#AppName} 제거.lnk"

[Icons]
Name: "{group}\{#AppName}";         Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallShortcut}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Registry]
; The language of the wizard becomes the app's language for its first start (the app reads this value only when the
; user has not chosen one in its settings, and ignores a language it has no texts for).
Root: HKLM; Subkey: "SOFTWARE\Pneutronic\ptc-xae-mcp"; ValueType: string; ValueName: "InstallLanguage"; ValueData: "{code:HostLanguageCode}"; Check: HostHasLanguage; Flags: uninsdeletevalue uninsdeletekeyifempty

[Code]
// The app's language code for the wizard language, or '' when the app has no texts in it (it then follows Windows).
function HostLanguageCode(Param: String): String;
begin
  if ActiveLanguage = 'english' then Result := 'en'
  else if ActiveLanguage = 'german' then Result := 'de'
  else if ActiveLanguage = 'french' then Result := 'fr'
  else if ActiveLanguage = 'italian' then Result := 'it'
  else Result := '';
end;

function HostHasLanguage: Boolean;
begin
  Result := HostLanguageCode('') <> '';
end;
