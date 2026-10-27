; IBDM Pro — Windows installer (per user, no administrator rights), Arabic first.
; برمجة و تطوير ابراهيم الخزرجي
; Built by the GitHub workflow (or BUILD-ON-WINDOWS.ps1), which passes /DAppVersion=1.0.N /DSource=<published folder>

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef Source
  #define Source "..\build\publish"
#endif

[Setup]
AppId={{5B7E3A90-1D4C-4F8B-A2E6-9C31D0B7F4A1}
AppName=IBDM Pro
AppVersion={#AppVersion}
AppVerName=IBDM Pro {#AppVersion}
AppPublisher=ابراهيم الخزرجي — IBRAPROGRAM
AppPublisherURL=https://t.me/ibraprogram
AppSupportURL=https://t.me/ibraprogram
AppUpdatesURL=https://t.me/ibraprogram
AppCopyright=برمجة و تطوير ابراهيم الخزرجي
VersionInfoVersion={#AppVersion}
VersionInfoDescription=IBDM Pro — مدير التحميل
DefaultDirName={autopf}\IBDM Pro
DefaultGroupName=IBDM Pro
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
SetupIconFile=..\assets\ibdm.ico
WizardImageFile=wizard-side-100.bmp,wizard-side-150.bmp,wizard-side-200.bmp
WizardSmallImageFile=wizard-corner-100.bmp,wizard-corner-150.bmp,wizard-corner-200.bmp
UninstallDisplayIcon={app}\IBDMPro.exe
UninstallDisplayName=IBDM Pro
OutputDir=..\build\installer
OutputBaseFilename=IBDM-Pro-Setup
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
ShowLanguageDialog=yes
LanguageDetectionMethod=none
MinVersion=10.0.17763
CloseApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "ar"; MessagesFile: "compiler:Languages\Arabic.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
en.SetupExtension=Install the browser extension automatically after setup (Chrome, Edge, Brave, Opera, Vivaldi)
en.StartWithWindows=Start IBDM Pro with Windows (so it always catches downloads from the browser)
en.DesktopIcon=Create a desktop shortcut
en.Extras=Options:
en.Launch=Start IBDM Pro
ar.SetupExtension=تنصيب إضافة المتصفح تلقائياً بعد التثبيت (Chrome، Edge، Brave، Opera، Vivaldi)
ar.StartWithWindows=تشغيل IBDM Pro مع بدء Windows (حتى يلتقط التحميلات من المتصفح دائماً)
ar.DesktopIcon=إنشاء اختصار على سطح المكتب
ar.Extras=خيارات:
ar.Launch=تشغيل IBDM Pro

[Tasks]
Name: "startup"; Description: "{cm:StartWithWindows}"; GroupDescription: "{cm:Extras}"
Name: "extension"; Description: "{cm:SetupExtension}"; GroupDescription: "{cm:Extras}"
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:Extras}"

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\IBDM Pro"; Filename: "{app}\IBDMPro.exe"
Name: "{autoprograms}\IBDM Pro — إضافة المتصفح"; Filename: "{app}\extension"
Name: "{autodesktop}\IBDM Pro"; Filename: "{app}\IBDMPro.exe"; Tasks: desktopicon

[Registry]
; The same value the app's "start with Windows" setting writes.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "IBDM Pro"; ValueData: """{app}\IBDMPro.exe"" --background"; Tasks: startup; Flags: uninsdeletevalue
; ibdmpro:// opens the app (used by the browser extension)
Root: HKCU; Subkey: "Software\Classes\ibdmpro"; ValueType: string; ValueName: ""; ValueData: "URL:IBDM Pro"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\ibdmpro"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\ibdmpro\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\IBDMPro.exe"",0"
Root: HKCU; Subkey: "Software\Classes\ibdmpro\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\IBDMPro.exe"" ""%1"""

[Run]
Filename: "{app}\IBDMPro.exe"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent; Tasks: not extension
Filename: "{app}\IBDMPro.exe"; Parameters: "--setup-extensions"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent; Tasks: extension
; After an update from inside the app (silent install), start it again.
Filename: "{app}\IBDMPro.exe"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/im IBDMPro.exe /f"; Flags: runhidden; RunOnceId: "StopIbdmPro"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
{ Closes a running IBDM Pro so every file can be replaced. Downloads in progress resume when it starts again. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im IBDMPro.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im yt-dlp.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(500);
  Result := '';
end;

{ A first install in English starts the app in English (with English folder names); later installs keep the
  language chosen in the app's settings. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  Folder: String;
begin
  if CurStep = ssPostInstall then
  begin
    Folder := ExpandConstant('{userappdata}\IBDM Pro');
    if (ActiveLanguage = 'en') and not FileExists(Folder + '\settings.json') then
    begin
      ForceDirectories(Folder);
      SaveStringToFile(Folder + '\settings.json', '{"Language":"en"}', False);
    end;
  end;
end;
