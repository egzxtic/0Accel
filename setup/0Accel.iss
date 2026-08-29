#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef AppSource
  #error AppSource is required
#endif
#ifndef DriverSource
  #error DriverSource is required
#endif
#ifndef DriverHelper
  #error DriverHelper is required
#endif
#ifndef OutputDirectory
  #error OutputDirectory is required
#endif
#ifndef SetupIcon
  #error SetupIcon is required
#endif

[Setup]
AppId={{A3B1E6C2-BC8F-4BD7-A424-0A4418FF38D1}
AppName=0Accel
AppVersion={#AppVersion}
AppVerName=0Accel {#AppVersion} Preview
AppPublisher=0Accel contributors
AppPublisherURL=https://github.com/egzxtic/0Accel
AppSupportURL=https://github.com/egzxtic/0Accel/issues
AppUpdatesURL=https://github.com/egzxtic/0Accel/releases
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=0Accel contributors
VersionInfoDescription=0Accel Preview installer
VersionInfoProductName=0Accel
VersionInfoProductVersion={#AppVersion}
DefaultDirName={autopf}\0Accel
DefaultGroupName=0Accel
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
UninstallDisplayName=0Accel {#AppVersion} Preview
UninstallDisplayIcon={app}\0Accel.exe
OutputDir={#OutputDirectory}
OutputBaseFilename=0Accel-Setup-{#AppVersion}-preview-win-x64
SetupIconFile={#SetupIcon}
LicenseFile=..\LICENSE
InfoBeforeFile=install-info.txt
WizardStyle=modern dynamic
WizardSizePercent=100
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
PrivilegesRequired=admin
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
CloseApplicationsFilter=0Accel.exe,0Accel.Panel.exe
RestartApplications=no
SetupLogging=yes
ChangesEnvironment=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"

[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#DriverHelper}"; DestName: "0Accel.DriverSetup.exe"; Flags: dontcopy
Source: "{#DriverSource}"; DestName: "rawaccel.sys"; Flags: dontcopy
Source: "{#DriverHelper}"; DestDir: "{app}\support"; DestName: "0Accel.DriverSetup.exe"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\0Accel"; Filename: "{app}\0Accel.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\0Accel.exe"; Description: "{cm:LaunchProgram,0Accel}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent; Check: CanLaunchNow

[Code]
var
  DriverRestartRequired: Boolean;
  DriverRemovalAsked: Boolean;

function Polish(): Boolean;
begin
  Result := ActiveLanguage = 'polish';
end;

function DriverInstallError(Code: Integer): String;
begin
  if Polish() then
    Result := 'Nie udało się bezpiecznie zainstalować podpisanego sterownika Raw Accel (kod ' + IntToStr(Code) + '). Instalacja 0Accel została przerwana. Szczegóły: C:\ProgramData\0Accel\setup.log'
  else
    Result := 'The signed Raw Accel driver could not be installed safely (code ' + IntToStr(Code) + '). 0Accel setup was stopped. Details: C:\ProgramData\0Accel\setup.log';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  HelperPath: String;
  DriverPath: String;
begin
  Result := '';
  ExtractTemporaryFile('0Accel.DriverSetup.exe');
  ExtractTemporaryFile('rawaccel.sys');
  HelperPath := ExpandConstant('{tmp}\0Accel.DriverSetup.exe');
  DriverPath := ExpandConstant('{tmp}\rawaccel.sys');
  if not Exec(HelperPath, 'install "' + DriverPath + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := DriverInstallError(-1);
    Exit;
  end;
  if ResultCode = 3010 then
  begin
    DriverRestartRequired := True;
    NeedsRestart := True;
  end
  else if ResultCode <> 0 then
    Result := DriverInstallError(ResultCode);
end;

function CanLaunchNow(): Boolean;
begin
  Result := not DriverRestartRequired;
end;

function NeedRestart(): Boolean;
begin
  Result := DriverRestartRequired;
end;

function UninstallNeedRestart(): Boolean;
begin
  Result := DriverRestartRequired;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  Prompt: String;
begin
  if (CurUninstallStep <> usUninstall) or DriverRemovalAsked then Exit;
  DriverRemovalAsked := True;
  if Polish() then
    Prompt := 'Czy usunąć również współdzielony sterownik Raw Accel?' + #13#10 + #13#10 +
      'Wybierz Nie, jeśli używasz go także z oryginalnym panelem Raw Accel. Usunięcie sterownika wymaga ponownego uruchomienia Windows.'
  else
    Prompt := 'Remove the shared Raw Accel driver too?' + #13#10 + #13#10 +
      'Choose No if you also use it with the original Raw Accel panel. Removing the driver requires a Windows restart.';
  if MsgBox(Prompt, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then Exit;
  if not Exec(ExpandConstant('{app}\support\0Accel.DriverSetup.exe'), 'uninstall', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox(DriverInstallError(-1), mbError, MB_OK);
    Exit;
  end;
  if ResultCode = 3010 then
    DriverRestartRequired := True
  else if ResultCode <> 0 then
    MsgBox(DriverInstallError(ResultCode), mbError, MB_OK);
end;
