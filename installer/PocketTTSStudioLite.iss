; AppVersion is supplied by MSBuild through build.ps1. AppId must NEVER change.
#ifndef AppVersion
  #error AppVersion must be supplied from the MSBuild Version property.
#endif
#ifndef PayloadDir
  #error PayloadDir is required. Use installer/build.ps1.
#endif
#ifndef SupportDir
  #error SupportDir is required. Use installer/build.ps1.
#endif
#ifndef OutputDir
  #error OutputDir is required. Use installer/build.ps1.
#endif

#ifndef PackageManifestHash
  #error PackageManifestHash is required.
#endif
#ifndef SupportHash
  #error SupportHash is required.
#endif

[Setup]
AppId={{336B2514-DBF3-4566-9302-95EE5D2D7DD3}
AppName=Pocket TTS Studio lite
AppVersion={#AppVersion}
AppVerName=Pocket TTS Studio lite {#AppVersion}
AppPublisher=Nendis
VersionInfoVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Pocket TTS Studio lite
DefaultGroupName=Pocket TTS Studio lite
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
SetupArchitecture=x64
MinVersion=10.0.17763
WizardStyle=modern
SetupIconFile=..\PocketTTSStudioLite\Assets\AppIcon.ico
UninstallDisplayIcon={app}\PocketTTSStudioLite.exe
UninstallDisplayName=Pocket TTS Studio lite
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=PocketTTSStudioLite-{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
DisableDirPage=auto
AllowUNCPath=no
AllowNetworkDrive=no
AllowRootDirectory=no
UsePreviousTasks=yes
DisableProgramGroupPage=yes
UninstallLogMode=overwrite
SetupLogging=yes
UninstallLogging=yes

[Tasks]
Name: "startmenuicon"; Description: "Create a Start Menu shortcut"
Name: "desktopicon"; Description: "Create a Desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SupportDir}\InstallerSupport.dll"; Flags: dontcopy
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Excludes: "maintenance\InstallerSupport.dll"; Flags: ignoreversion recursesubdirs createallsubdirs uninsneveruninstall
Source: "{#SupportDir}\InstallerSupport.dll"; DestDir: "{app}\maintenance"; Flags: ignoreversion

[Icons]
; PrivilegesRequired=lowest makes {group} the current user's Start Menu group.
Name: "{group}\Pocket TTS Studio lite"; Filename: "{app}\PocketTTSStudioLite.exe"; WorkingDir: "{app}"; Tasks: startmenuicon
Name: "{group}\Uninstall Pocket TTS Studio lite"; Filename: "{uninstallexe}"; Tasks: startmenuicon
Name: "{userdesktop}\Pocket TTS Studio lite"; Filename: "{app}\PocketTTSStudioLite.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[InstallDelete]
; Apply unchecked choices to shortcuts left by an earlier installation as well.
Type: files; Name: "{group}\Pocket TTS Studio lite.lnk"; Tasks: not startmenuicon
Type: files; Name: "{group}\Uninstall Pocket TTS Studio lite.lnk"; Tasks: not startmenuicon
Type: files; Name: "{userdesktop}\Pocket TTS Studio lite.lnk"; Tasks: not desktopicon

[Code]
type
  TDirectoryHandles = array of THandle;

const
  FileAttributeDirectory = $10;
  FileAttributeReparsePoint = $400;
  InvalidFileAttributes = $FFFFFFFF;
  FileReadAttributes = $80;
  ShareReadOnly = 1; // Deny directory writes/reparse changes and delete/rename.
  OpenExisting = 3;
  OpenReparsePointAndBackupSemantics = $02200000;

function CompatibleCpu: Boolean;
  external 'CompatibleCpu@files:InstallerSupport.dll stdcall setuponly';
function MediaFoundationAvailable: Boolean;
  external 'MediaFoundationAvailable@files:InstallerSupport.dll stdcall setuponly';
function GetFileAttributesW(Name: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function CreateFileW(Name: String; Access, Share: Cardinal; Security: NativeInt;
  Creation, Flags: Cardinal; Template: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

var
  RemovePrivateData: Boolean;
  MediaFeatureInstalled: Boolean;
  MediaFeatureAttempted: Boolean;

function InitializeSetup: Boolean;
begin
  Result := CompatibleCpu;
  if not Result then
    MsgBox('Pocket TTS Studio lite requires an x64 processor with AVX2 and FMA, and Windows support for AVX state. This computer is not supported.', mbError, MB_OK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Edition: String;
  ExitCode: Integer;
begin
  Result := '';
  if MediaFeatureInstalled or MediaFoundationAvailable then exit;
  RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\Windows NT\CurrentVersion', 'EditionID', Edition);
  if (Length(Edition) = 0) or (Copy(Edition, Length(Edition), 1) <> 'N') then
  begin
    Result := 'Windows Media Foundation is unavailable. Repair/enable the Windows media components before installing Pocket TTS Studio lite.';
    exit;
  end;
  if MediaFeatureAttempted then
  begin
    Result := 'The Microsoft Media Feature Pack is still unavailable. Restart Windows if its installation succeeded, then run Setup again. Otherwise review Windows\Logs\DISM\dism.log.';
    exit;
  end;
  MediaFeatureAttempted := True;
  // This Windows optional feature is the only prerequisite requiring elevation.
  // DISM obtains the signed, OS-matched Microsoft payload from Windows Update.
  if not ShellExec('runas', ExpandConstant('{sys}\dism.exe'),
    '/Online /Add-Capability /CapabilityName:Media.MediaFeaturePack~~~~0.0.1.0 /NoRestart',
    '', SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode) then
  begin
    Result := 'The Microsoft Media Feature Pack could not be installed (administrator approval may have been declined). No application files were installed. Install the Windows optional feature and retry.';
    exit;
  end;
  if (ExitCode <> 0) and (ExitCode <> 3010) then
  begin
    Result := Format('Microsoft Media Feature Pack installation failed (code %d). No application files were installed. Review Windows\Logs\DISM\dism.log and retry.', [ExitCode]);
    exit;
  end;
  MediaFeatureInstalled := True;
  // Microsoft requires a restart after installing this optional feature.
end;

function NeedRestart: Boolean;
begin
  Result := MediaFeatureInstalled;
end;

function PrivateDataPath: String;
begin
  // No caller-supplied path, settings value, inventory path, or export folder.
  Result := AddBackslash(ExpandConstant('{localappdata}')) + 'PocketTTSStudioLite';
end;

function PinDirectoryChain(Path: String; var Handles: TDirectoryHandles): Boolean;
var
  Parent: String;
  Attributes: Cardinal;
  Handle: THandle;
begin
  Result := False;
  if (Length(Path) < 3) or (Copy(Path, 2, 2) <> ':\') then exit;
  Parent := ExtractFileDir(Path);
  if (Length(Path) > 3) and not PinDirectoryChain(Parent, Handles) then exit;
  Handle := CreateFileW(Path, FileReadAttributes, ShareReadOnly, 0,
    OpenExisting, OpenReparsePointAndBackupSemantics, 0);
  if Handle = THandle(-1) then exit;
  SetArrayLength(Handles, GetArrayLength(Handles) + 1);
  Handles[GetArrayLength(Handles) - 1] := Handle;
  Attributes := GetFileAttributesW(Path);
  Result := (Attributes <> InvalidFileAttributes) and
    ((Attributes and FileAttributeDirectory) <> 0) and
    ((Attributes and FileAttributeReparsePoint) = 0);
end;

#include "maintenance.iss"
