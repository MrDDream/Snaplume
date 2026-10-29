; Snaplume installer - Inno Setup 6.3 or later (x64compatible).
;
; Normally compiled by build/build-release.ps1, which publishes the application and passes:
;   /DAppVersion=1.0.0 /DAppVersionNumeric=1.0.0.0 /DSourceDir=<publish folder> /DRepoRoot=<repository>
;   /DOutputDir=<artifacts> /DOutputBaseName=Snaplume-Setup-1.0.0
;   [/DVCRedistExe=<vc_redist.x64.exe>]  only when the Visual C++ runtime DLLs are not shipped app-locally
;
; Per-user installation by default (no administrator rights, %LOCALAPPDATA%\Programs\Snaplume); the
; "install for all users" choice is offered in a dialog. User data (captures, %APPDATA%\Snaplume) is
; never touched by the uninstaller.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef AppVersionNumeric
  #define AppVersionNumeric "1.0.0.0"
#endif
#ifndef RepoRoot
  #define RepoRoot AddBackslash(SourcePath) + ".."
#endif
#ifndef SourceDir
  #define SourceDir RepoRoot + "\artifacts\staging\publish"
#endif
#ifndef OutputDir
  #define OutputDir RepoRoot + "\artifacts"
#endif
#ifndef OutputBaseName
  #define OutputBaseName "Snaplume-Setup-" + AppVersion
#endif

#define AppName "Snaplume"
#define AppExe "Snaplume.exe"
#define AppPublisher "Snaplume contributors"
#define AppUrl "https://github.com/MrDDream/Snaplume"
; ScreenRecorderLib 7.0.1 is linked with the 14.44 toolset: the Visual C++ runtime must be 14.44 or later.
#define VCMinMinor 44

[Setup]
; Never change AppId: it identifies the installation for upgrades and uninstallation.
AppId={{99A39269-5DA6-482F-AC92-453095B44057}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppCopyright=Copyright (c) {#AppPublisher} - MIT License
VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductVersion={#AppVersionNumeric}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 version 2004 (20H1) or later, like the application.
MinVersion=10.0.19041
LicenseFile={#RepoRoot}\LICENSE
SetupIconFile={#RepoRoot}\src\Snaplume\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
ShowLanguageDialog=auto
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseName}
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
; A running Snaplume is closed (Restart Manager) before its files are replaced; it is not restarted
; automatically, the "Launch Snaplume" box of the last page does it.
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
; Arabic is an unofficial Inno Setup translation: used only when the Inno Setup installation has it.
#if FileExists(CompilerPath + "Languages\Arabic.isl")
  #define HaveArabic
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"
#elif FileExists(CompilerPath + "Languages\Unofficial\Arabic.isl")
  #define HaveArabic
Name: "arabic"; MessagesFile: "compiler:Languages\Unofficial\Arabic.isl"
#else
  #pragma message "Arabic.isl not found in the Inno Setup installation: Arabic installer UI skipped."
#endif

[CustomMessages]
english.InstallingVCRuntime=Installing the Microsoft Visual C++ runtime (x64)...
english.VCRuntimeFailed=The Microsoft Visual C++ 2015-2022 runtime (x64) could not be installed (%1).%n%nSnaplume needs it for video recording. You can install it later from:%nhttps://aka.ms/vs/17/release/vc_redist.x64.exe
french.InstallingVCRuntime=Installation du runtime Microsoft Visual C++ (x64)...
french.VCRuntimeFailed=Le runtime Microsoft Visual C++ 2015-2022 (x64) n'a pas pu être installé (%1).%n%nSnaplume en a besoin pour l'enregistrement vidéo. Vous pourrez l'installer plus tard depuis :%nhttps://aka.ms/vs/17/release/vc_redist.x64.exe
german.InstallingVCRuntime=Microsoft Visual C++-Laufzeitumgebung (x64) wird installiert...
german.VCRuntimeFailed=Die Microsoft Visual C++ 2015-2022-Laufzeitumgebung (x64) konnte nicht installiert werden (%1).%n%nSnaplume benötigt sie für die Videoaufnahme. Sie können sie später hier installieren:%nhttps://aka.ms/vs/17/release/vc_redist.x64.exe
spanish.InstallingVCRuntime=Instalando el entorno de ejecución de Microsoft Visual C++ (x64)...
spanish.VCRuntimeFailed=No se pudo instalar el entorno de ejecución de Microsoft Visual C++ 2015-2022 (x64) (%1).%n%nSnaplume lo necesita para la grabación de vídeo. Puede instalarlo más tarde desde:%nhttps://aka.ms/vs/17/release/vc_redist.x64.exe
italian.InstallingVCRuntime=Installazione del runtime Microsoft Visual C++ (x64)...
italian.VCRuntimeFailed=Impossibile installare il runtime Microsoft Visual C++ 2015-2022 (x64) (%1).%n%nSnaplume ne ha bisogno per la registrazione video. Puoi installarlo in seguito da:%nhttps://aka.ms/vs/17/release/vc_redist.x64.exe
#ifdef HaveArabic
arabic.InstallingVCRuntime=جارٍ تثبيت وقت تشغيل Microsoft Visual C++ (x64)...
arabic.VCRuntimeFailed=تعذّر تثبيت وقت تشغيل Microsoft Visual C++ 2015-2022 (x64) (%1).%n%nيحتاج Snaplume إليه لتسجيل الفيديو. يمكنك تثبيته لاحقًا من:%nhttps://aka.ms/vs/17/release/vc_redist.x64.exe
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "{cm:AutoStartProgram,{#AppName}}"; GroupDescription: "{cm:AutoStartProgramGroupDescription}"; Flags: unchecked

[Files]
; The "portable" marker must never be installed (settings go to %APPDATA%\Snaplume).
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "portable"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
#ifdef VCRedistExe
Source: "{#VCRedistExe}"; DestName: "vc_redist.x64.exe"; Flags: dontcopy
#endif

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{#AppName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Same value as the application's own "Start with Windows" setting (Core/AppSettings.cs, ApplyStartWithWindows).
; Always HKCU, even for an all-users installation, so that the application can change it afterwards.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --tray"; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

{ Force-closes the Snaplume processes started from the installation folder (not portable copies).
  The single-instance mutex name ends with the user SID, so AppMutex cannot be used. }
procedure CloseRunningSnaplume;
var
  Locator, Service, Processes, Process: Variant;
  AppDir, ExePath: String;
  I, Closed: Integer;
begin
  AppDir := AddBackslash(ExpandConstant('{app}'));
  Closed := 0;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    { SELECT *: Terminate needs the object's key (Handle), absent from a narrower projection. }
    Processes := Service.ExecQuery('SELECT * FROM Win32_Process WHERE Name = ''{#AppExe}''');
    for I := 0 to Processes.Count - 1 do
    begin
      Process := Processes.ItemIndex(I);
      if not VarIsNull(Process.ExecutablePath) then
      begin
        ExePath := Process.ExecutablePath;
        if CompareText(Copy(ExePath, 1, Length(AppDir)), AppDir) = 0 then
        begin
          Log('Closing ' + ExePath + ' (process ' + IntToStr(Process.ProcessId) + ')');
          Process.Terminate(0);
          Closed := Closed + 1;
        end;
      end;
    end;
  except
    Log('Could not enumerate or close Snaplume processes: ' + GetExceptionMessage);
  end;
  if Closed > 0 then
    Sleep(1000);  { let Windows release the files }
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value: String;
begin
  if CurUninstallStep = usUninstall then
    CloseRunningSnaplume
  else if CurUninstallStep = usPostUninstall then
  begin
    { Remove "Start with Windows" if it points to this installation, whether the installer task or
      the application's own setting created it. Captures and %APPDATA%\Snaplume are kept. }
    if RegQueryStringValue(HKCU, RunKey, '{#AppName}', Value) and
       (Pos(Lowercase(AddBackslash(ExpandConstant('{app}'))), Lowercase(Value)) > 0) then
      RegDeleteValue(HKCU, RunKey, '{#AppName}');
  end;
end;

#ifdef VCRedistExe
function VCRuntimeInstalled(RootKey: Integer): Boolean;
var
  Key: String;
  Installed, Major, Minor: Cardinal;
begin
  Key := 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64';
  Result := RegQueryDWordValue(RootKey, Key, 'Installed', Installed) and (Installed = 1) and
            RegQueryDWordValue(RootKey, Key, 'Major', Major) and
            RegQueryDWordValue(RootKey, Key, 'Minor', Minor) and
            ((Major > 14) or ((Major = 14) and (Minor >= {#VCMinMinor})));
end;

function VCRuntimeNeeded: Boolean;
begin
  Result := not (VCRuntimeInstalled(HKLM64) or VCRuntimeInstalled(HKLM32)) or
            not FileExists(ExpandConstant('{sys}\vcruntime140_1.dll'));
end;

procedure InstallVCRuntime;
var
  Verb: String;
  ResultCode: Integer;
  Ok: Boolean;
begin
  WizardForm.StatusLabel.Caption := CustomMessage('InstallingVCRuntime');
  ExtractTemporaryFile('vc_redist.x64.exe');
  { The runtime is installed for the whole machine: ask for elevation in a per-user installation. }
  if IsAdminInstallMode then Verb := '' else Verb := 'runas';
  Ok := ShellExec(Verb, ExpandConstant('{tmp}\vc_redist.x64.exe'), '/install /quiet /norestart', '',
                  SW_SHOW, ewWaitUntilTerminated, ResultCode);
  Log('vc_redist.x64.exe: started=' + IntToStr(Ord(Ok)) + ', code=' + IntToStr(ResultCode));
  { 0 = installed, 1638 = a newer version is already installed, 3010 = installed, restart required }
  if not Ok or not ((ResultCode = 0) or (ResultCode = 1638) or (ResultCode = 3010)) then
    SuppressibleMsgBox(FmtMessage(CustomMessage('VCRuntimeFailed'), [IntToStr(ResultCode)]),
                       mbError, MB_OK, IDOK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and VCRuntimeNeeded then
    InstallVCRuntime;
end;
#endif
