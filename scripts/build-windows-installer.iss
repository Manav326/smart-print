#ifndef SmartPrintPublicUrl
  #error SmartPrintPublicUrl must be supplied from SMARTPRINT_PUBLIC_BASE_URL by the build.
#endif
#ifndef SmartPrintVersion
  #define SmartPrintVersion "0.1.0"
#endif
#ifndef SmartPrintBuildId
  #define SmartPrintBuildId "local"
#endif

[Setup]
SourceDir=..\
AppId={{A7F2E9CA-2C3A-4E58-AE63-26E6B7B0DDF1}
AppName=SmartPrint by QuantaEdge
AppVersion={#SmartPrintVersion}
AppPublisher=QuantaEdge
DefaultDirName={autopf}\SmartPrint
DefaultGroupName=SmartPrint
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=artifacts\agent-installer
OutputBaseFilename=SmartPrint-Windows-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=SmartPrint by QuantaEdge
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "artifacts\agent\*"; DestDir: "{app}\agent\{#SmartPrintVersion}-{#SmartPrintBuildId}"; Excludes: "pair-agent.ps1,appsettings.example.json,README.md,agent-service-lifecycle.ps1"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "artifacts\desktop\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "artifacts\desktop\*"; DestDir: "{app}"; Excludes: "MicrosoftEdgeWebView2RuntimeInstallerX64.exe;SmartPrint.Desktop.QA.exe"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SmartPrint"; Filename: "{app}\SmartPrint.Desktop.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\SmartPrint"; Filename: "{app}\SmartPrint.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{group}\Uninstall SmartPrint"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\SmartPrint.Desktop.exe"; Description: "Launch SmartPrint"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\agent\{#SmartPrintVersion}-{#SmartPrintBuildId}\uninstall-agent-service.ps1"""; Flags: runhidden waituntilterminated skipifdoesntexist

[Code]
function IsWebView2Installed: Boolean;
var
  RuntimeVersion: string;
begin
  Result :=
    (RegQueryStringValue(HKLM64, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', RuntimeVersion) and
      (RuntimeVersion <> '') and (RuntimeVersion <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', RuntimeVersion) and
      (RuntimeVersion <> '') and (RuntimeVersion <> '0.0.0.0'));
end;

procedure EnsureWebView2;
var
  InstallerPath: string;
  ExitCode: Integer;
begin
  if IsWebView2Installed then
    Exit;

  InstallerPath := ExpandConstant('{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe');
  if not FileExists(InstallerPath) then
    RaiseException('The Microsoft Edge WebView2 Runtime installer is missing. Repair SmartPrint setup and try again.');

  if not Exec(InstallerPath, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    RaiseException('SmartPrint could not start the Microsoft WebView2 Runtime installer.');
  if (ExitCode <> 0) and (ExitCode <> 3010) then
    RaiseException('Microsoft WebView2 Runtime setup failed. Check Windows Update and try SmartPrint setup again.');
  if not IsWebView2Installed then
    RaiseException('Microsoft WebView2 Runtime is still unavailable. SmartPrint cannot open without it.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  PowerShellPath: string;
  Parameters: string;
  ExitCode: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;

  EnsureWebView2;

  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Parameters :=
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\agent\{#SmartPrintVersion}-{#SmartPrintBuildId}\install-agent-service.ps1') +
    '" -AgentExePath "' + ExpandConstant('{app}\agent\{#SmartPrintVersion}-{#SmartPrintBuildId}\SmartPrint.Agent.exe') +
    '" -AgentVersion "{#SmartPrintVersion}" -CustomerPortalOrigin "{#SmartPrintPublicUrl}" -ApiBaseUrl "{#SmartPrintPublicUrl}"';

  if not Exec(PowerShellPath, Parameters, ExpandConstant('{app}\agent\{#SmartPrintVersion}-{#SmartPrintBuildId}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    RaiseException('SmartPrint could not start its background service installer.');
  if ExitCode <> 0 then
    RaiseException('SmartPrint could not install or start the background print service. No printer connection has been changed. Restart Windows and run setup again.');

  // Remove legacy Agent files from the old install layout only after the
  // replacement Windows service has started successfully. The previous Agent
  // binary remains side-by-side until a later cleanup; failed service switches
  // restore the previous executable path before setup reports failure.
  DeleteFile(ExpandConstant('{app}\SmartPrint.Agent.exe'));
  DeleteFile(ExpandConstant('{app}\SmartPrint.Agent.dll'));
  DeleteFile(ExpandConstant('{app}\SmartPrint.Agent.deps.json'));
  DeleteFile(ExpandConstant('{app}\SmartPrint.Agent.runtimeconfig.json'));
  DeleteFile(ExpandConstant('{app}\SmartPrint.Agent.pdb'));
  DeleteFile(ExpandConstant('{app}\appsettings.json'));
  DeleteFile(ExpandConstant('{app}\install-agent-service.ps1'));
  DeleteFile(ExpandConstant('{app}\uninstall-agent-service.ps1'));
  DeleteFile(ExpandConstant('{app}\verify-agent-local.ps1'));
end;
