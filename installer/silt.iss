; Silt installer (Inno Setup 6).
;
; Build with:
;   pwsh scripts/publish.ps1
;   iscc installer\silt.iss
;
; The payload must already exist at artifacts\publish. This script deliberately does not
; invoke dotnet or npm: an installer that builds its own input hides a stale-payload bug
; behind a green compile.

#define AppName    "Silt"
#define AppPublisher "mhalder-dev"
#define AppUrl     "https://github.com/mhalder-dev/silt"
#define Payload    "..\artifacts\publish"

; Version comes from the built exe, not from a literal here. Two hand-maintained version
; numbers drift, and the one that drifts is the one on the download page.
; The Win32 resource is always four-part ("0.1.0.0"). Trim the trailing revision so the
; asset filename matches the git tag that produced it - a release tagged v0.1.0 that ships
; Silt-0.1.0.0-setup.exe invites the question of whether they are the same build.
#define FileVersion GetVersionNumbersString(Payload + "\Silt.exe")
#define AppVersion Copy(FileVersion, 1, RPos(".", FileVersion) - 1)

[Setup]
; Never change AppId - it is how Windows recognises an existing install to upgrade.
AppId={{566AFA05-7382-4447-BBAC-0B4A54E5CA25}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
LicenseFile=..\LICENSE
OutputDir=..\artifacts\installer
OutputBaseFilename=Silt-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Install into Program Files under an admin token. This is the ACL the plan relies on: the
; binary that deletes files must not be rewritable by the unprivileged process it later runs
; as, or the whole safety model is bypassable by editing Silt.exe on disk. It is also why
; there is no per-user install option - that would put the delete engine somewhere the
; delete engine's own user can modify.
PrivilegesRequired=admin

; The payload is win-x64 self-contained. On any other architecture it will not run at all,
; so refuse at install time rather than producing an install that fails at launch.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; net10.0-windows requires Windows 10 1809+.
MinVersion=10.0.17763

; NOT code-signed. There is no certificate, and pretending otherwise helps nobody: SmartScreen
; will show "Windows protected your PC" until the download builds reputation. Documented in
; docs/INSTALL.md rather than hidden.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Recurse over the whole verified payload rather than listing files. scripts/publish.ps1 is
; what asserts the payload is complete; duplicating that list here would give two places to
; forget wwwroot, which is exactly the bug that shipped once already.
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Silt.exe"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Silt.exe"; Tasks: desktopicon

[Run]
; runasoriginaluser is load-bearing, not cosmetic. Setup runs elevated; without this flag the
; post-install launch inherits that admin token and the very first run of Silt happens as
; administrator - the one thing src/shell/Silt.Shell/Silt.Shell.csproj documents must never
; happen. It would also create the WebView2 user data folder under
; %LOCALAPPDATA%\Silt at high integrity, which every later ordinary launch then contends
; with. The damage would outlive the install.
Filename: "{app}\Silt.exe"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

; There is deliberately NO [UninstallDelete] section.
;
; The obvious entry - drop {localappdata}\Silt\WebView2, which is pure regenerable cache -
; is wrong here and Inno says so at compile time: with PrivilegesRequired=admin the
; uninstaller runs elevated, so {localappdata} resolves to the ADMINISTRATOR's profile, not
; the profile whose cache we meant. On a machine with a separate admin account it deletes a
; directory belonging to someone who never ran Silt, and leaves the real one untouched.
;
; Everything else under %LOCALAPPDATA%\Silt - snapshot history and the hash-chained
; operation journal - must survive uninstall regardless. It is the user's scan history and
; the audit trail of what Silt deleted for them. A tool whose governing invariant is that
; nothing goes without naming how it comes back does not get to shred its own audit log on
; the way out. docs/INSTALL.md says where it lives so the user can remove it deliberately.

[Code]
{ The Evergreen WebView2 Runtime ships with Windows 11 but not with every Windows 10
  install. Without it CoreWebView2Environment.CreateAsync fails and the user gets a window
  with nothing in it - indistinguishable, to them, from a broken app. Detect it and say so
  plainly. This does not block the install: the runtime can be installed afterwards and Silt
  will then work, and silently refusing to install is worse than a clear warning. }
function WebView2RuntimeInstalled(): Boolean;
var
  Version: String;
begin
  { Machine-wide installs land under WOW6432Node even on x64, because the Evergreen
    installer writes a 32-bit view key. Per-user installs live under HKCU. Both count. }
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not WebView2RuntimeInstalled() then
  begin
    MsgBox(
      'The Microsoft Edge WebView2 Runtime was not found.' + #13#10#13#10 +
      'Silt draws its entire interface in WebView2, so it will open an empty window until ' +
      'the runtime is installed. Setup will continue; install the Evergreen runtime from ' +
      'https://developer.microsoft.com/microsoft-edge/webview2/ and Silt will work without ' +
      'being reinstalled.',
      mbInformation, MB_OK);
  end;
end;
