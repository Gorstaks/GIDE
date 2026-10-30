; GIDE - Gorstaks IDE (C# .NET 4.8)
; Requires InnoSetup 6.x or 7.x

#define MyAppName "GIDE"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Gorstak"
#define MyAppURL "https://github.com/CroatiaSecurity/GIDE"
#define MyAppExeName "GIDE.exe"

[Setup]
AppId={{8A3F2B71-C4D5-4E6F-A7B8-9C0D1E2F3A4B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\GIDE
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\releases\{#MyAppVersion}
OutputBaseFilename=GIDE-Setup-{#MyAppVersion}
SetupIconFile=..\GIDE.ico
UninstallDisplayIcon={app}\GIDE.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "addtopath"; Description: "Add GIDE to system PATH"; GroupDescription: "System:"

[Registry]
Root: HKCU; Subkey: "Software\Classes\Directory\shell\GIDE"; ValueType: string; ValueName: ""; ValueData: "Run GIDE here"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\GIDE"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\GIDE.ico,0"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Directory\shell\GIDE\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" --dir ""%1"""; Flags: uninsdeletevalue

Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\GIDE"; ValueType: string; ValueName: ""; ValueData: "Run GIDE here"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\GIDE"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\GIDE.ico,0"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\GIDE\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" --dir ""%V"""; Flags: uninsdeletevalue

[Files]
Source: "..\dist\GIDE.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\GIDE.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\GIDE"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\GIDE.ico"
Name: "{group}\Uninstall GIDE"; Filename: "{uninstallexe}"
Name: "{autodesktop}\GIDE"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\GIDE.ico"; Tasks: desktopicon

[Code]
const
  VC_REDIST_URL = 'https://aka.ms/vs/17/release/vc_redist.x64.exe';

function VCRedistInstalled: Boolean;
var
  Version: String;
  Major: Cardinal;
begin
  Result := False;

  // Method 1: Check official VC++ Runtimes registry key with version validation
  if RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64', 'Version', Version) then
  begin
    Result := True;
    Exit;
  end;

  // Method 2: Check for key existence (some installs don't write Version)
  if RegKeyExists(HKLM, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64') then
  begin
    Result := True;
    Exit;
  end;

  // Method 3: Check WOW6432Node (32-bit view on 64-bit OS)
  if RegKeyExists(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X64') then
  begin
    Result := True;
    Exit;
  end;

  // Method 4: Check via Major version DWORD value
  if RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64', 'Major', Major) then
  begin
    if Major >= 14 then
    begin
      Result := True;
      Exit;
    end;
  end;

  // Method 5: Check if vcruntime140.dll exists in System32
  if FileExists(ExpandConstant('{sys}\vcruntime140.dll')) then
  begin
    Result := True;
    Exit;
  end;
end;

function DownloadAndInstallVCRedist: Boolean;
var
  TempFile: String;
  ResultCode: Integer;
  DownloadSuccess: Boolean;
begin
  Result := True;
  if VCRedistInstalled then
    Exit;

  TempFile := ExpandConstant('{tmp}\vc_redist.x64.exe');
  DownloadSuccess := False;

  // Try download using curl (built into Windows 10+)
  if Exec('curl.exe', '-L --silent --fail -o "' + TempFile + '" "' + VC_REDIST_URL + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if (ResultCode = 0) and FileExists(TempFile) then
      DownloadSuccess := True;
  end;

  // Fallback: Try PowerShell Invoke-WebRequest (Windows 7+ with PS 3.0+)
  if not DownloadSuccess then
  begin
    if Exec('powershell.exe',
      '-NoProfile -ExecutionPolicy Bypass -Command "try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri ''' + VC_REDIST_URL + ''' -OutFile ''' + TempFile + ''' -UseBasicParsing } catch { exit 1 }"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      if (ResultCode = 0) and FileExists(TempFile) then
        DownloadSuccess := True;
    end;
  end;

  // Fallback: Try certutil (available on all modern Windows)
  if not DownloadSuccess then
  begin
    if Exec('certutil.exe', '-urlcache -split -f "' + VC_REDIST_URL + '" "' + TempFile + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      if (ResultCode = 0) and FileExists(TempFile) then
        DownloadSuccess := True;
    end;
  end;

  if not DownloadSuccess then
  begin
    MsgBox('Could not download the Visual C++ Redistributable automatically.' + #13#10 + #13#10 +
           'GIDE requires this component to run its AI engine.' + #13#10 + #13#10 +
           'Please install it manually from:' + #13#10 +
           'https://aka.ms/vs/17/release/vc_redist.x64.exe' + #13#10 + #13#10 +
           'GIDE will also attempt to install it on first launch.',
           mbInformation, MB_OK);
    Result := False;
    Exit;
  end;

  // Install silently
  if Exec(TempFile, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    // Exit code 0 = success, 3010 = success but needs reboot
    if (ResultCode <> 0) and (ResultCode <> 3010) then
    begin
      MsgBox('Visual C++ Redistributable installation returned code ' + IntToStr(ResultCode) + '.' + #13#10 + #13#10 +
             'If GIDE fails to start, please install it manually from:' + #13#10 +
             'https://aka.ms/vs/17/release/vc_redist.x64.exe',
             mbInformation, MB_OK);
    end;
  end;

  // Verify installation succeeded
  if not VCRedistInstalled then
  begin
    // One more check: the DLL might be placed without registry entry
    if not FileExists(ExpandConstant('{sys}\vcruntime140.dll')) then
    begin
      MsgBox('Visual C++ Redistributable may not have installed correctly.' + #13#10 + #13#10 +
             'GIDE will attempt to install it again on first launch.' + #13#10 +
             'If problems persist, install manually from:' + #13#10 +
             'https://aka.ms/vs/17/release/vc_redist.x64.exe',
             mbInformation, MB_OK);
      Result := False;
    end;
  end;

  // Cleanup temp file
  if FileExists(TempFile) then
    DeleteFile(TempFile);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Path: string;
  AppDir: string;
begin
  if CurStep = ssPostInstall then
  begin
    // Install VC++ Redistributable if needed
    DownloadAndInstallVCRedist;

    if WizardIsTaskSelected('addtopath') then
    begin
      AppDir := ExpandConstant('{app}');
      RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', Path);
      if Pos(AppDir, Path) = 0 then
      begin
        Path := Path + ';' + AppDir;
        RegWriteStringValue(HKLM, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', Path);
      end;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Path: string;
  AppDir: string;
  P: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    AppDir := ExpandConstant('{app}');
    RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', Path);
    P := Pos(';' + AppDir, Path);
    if P > 0 then
    begin
      Delete(Path, P, Length(';' + AppDir));
      RegWriteStringValue(HKLM, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', Path);
    end;
  end;
end;