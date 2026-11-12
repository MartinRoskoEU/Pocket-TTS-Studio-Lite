{ Managed code owns private-data plans and installation receipts. Native code
  holds checked payload deletion handles; Inno owns consent, shortcuts and host. }
[Code]
var
  MaintenancePins: TDirectoryHandles;
  MaintenanceWorkerRoot, MaintenanceReport, MaintenancePreparedRoot: String;
  NativeRemovalComplete: Boolean;
  MaintenancePrepared, MaintenanceFailed: Boolean;
  MaintenanceInstance: THandle;
  MaintenanceInstancePins: TDirectoryHandles;
  MaintenanceBusy, MaintenanceCancelable, MaintenanceCancelRequested: Boolean;
  MaintenanceFilesStarted, MaintenanceFilesComplete, MaintenanceExiting: Boolean;
  MaintenanceProgress: TOutputMarqueeProgressWizardPage;
  MaintenanceCancelButton: TNewButton;

procedure MaterializeWorker; forward;
function PinWorker(Root: String): Boolean; forward;
function QueuePayloadFiles: Boolean; forward;
procedure ReleaseMaintenanceInstance; forward;

procedure SetMaintenanceWindow(Window: HWND);
  external 'SetMaintenanceWindow@{tmp}\PocketMaintenance.dll stdcall delayload';
function VerifyMaintenanceFile(Path, Hash: String): Boolean;
  external 'VerifyMaintenanceFile@{tmp}\PocketMaintenance.dll stdcall delayload';
function RunMaintenanceProcess(Filename, Parameters, Directory: String): Integer;
  external 'RunMaintenanceProcess@{tmp}\PocketMaintenance.dll stdcall delayload';
function PostMaintenanceMessage(Window: HWND; Message, WParam, LParam: NativeInt): Boolean;
  external 'PostMessageW@user32.dll stdcall';

procedure RequestMaintenanceCancel(Sender: TObject);
begin
  if not MaintenanceCancelable or MaintenanceCancelRequested then exit;
  if MsgBox('Cancel installation? Setup will finish the current verification and stop safely.', mbConfirmation, MB_YESNO) <> IDYES then exit;
  if not SaveStringToFile(MaintenanceReport + '.cancel', 'cancel', False) then begin
    MsgBox('The cancellation request could not be saved. Please retry.', mbError, MB_OK);
    exit;
  end;
  MaintenanceCancelRequested := True;
  MaintenanceCancelButton.Enabled := False;
  MaintenanceProgress.SetText('Cancelling installation preparation...', 'Waiting for verification to finish safely.');
end;

procedure InitializeWizard;
begin
  MaintenanceProgress := CreateOutputMarqueeProgressPage('Preparing installation', 'Verifying application files.');
  MaintenanceCancelButton := TNewButton.Create(WizardForm);
  MaintenanceCancelButton.Parent := WizardForm;
  MaintenanceCancelButton.SetBounds(WizardForm.CancelButton.Left, WizardForm.CancelButton.Top,
    WizardForm.CancelButton.Width, WizardForm.CancelButton.Height);
  MaintenanceCancelButton.Caption := SetupMessage(msgButtonCancel);
  MaintenanceCancelButton.OnClick := @RequestMaintenanceCancel;
  MaintenanceCancelButton.Visible := False;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if MediaFeatureInstalling then begin
    Cancel := False;
    Confirm := False;
  end else if MaintenanceBusy then begin
    Cancel := False;
    Confirm := False;
    RequestMaintenanceCancel(WizardForm);
  end else if (CurPageID = wpInstalling) and MaintenanceFilesComplete then begin
    Cancel := False;
    Confirm := False;
  end else if CurPageID = wpInstalling then begin
    Confirm := False;
    Cancel := MsgBox('Cancel installation? Setup will undo the changes made by this installation.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    if Cancel then begin
      MaintenanceCancelRequested := True;
      ReleaseMaintenanceInstance;
    end;
  end else if MaintenanceCancelRequested then Confirm := False;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then WizardForm.NextButton.Left := WizardForm.CancelButton.Left;
end;

procedure RefuseMaintenanceClose(Sender: TObject; var CanClose: Boolean);
begin
  CanClose := False;
end;

procedure InitializeUninstallProgressForm;
begin
  UninstallProgressForm.CancelButton.Visible := False;
end;

procedure CheckPreparationCancellation;
begin
  if not MaintenanceCancelable or IsUninstaller or MaintenanceExiting then exit;
  MaintenanceProgress.Animate;
  if MaintenanceCancelRequested then RaiseException('Installation preparation cancelled safely.');
end;

function BeginOwnedDelete(Root: String): Boolean;
  external 'BeginOwnedDelete@{tmp}\PocketMaintenance.dll stdcall delayload';
function QueueOwnedFile(RelativePath, Hash: String): Boolean;
  external 'QueueOwnedFile@{tmp}\PocketMaintenance.dll stdcall delayload';
function CommitOwnedDelete: Boolean;
  external 'CommitOwnedDelete@{tmp}\PocketMaintenance.dll stdcall delayload';
procedure CancelOwnedDelete;
  external 'CancelOwnedDelete@{tmp}\PocketMaintenance.dll stdcall delayload';
function CheckDirectoryIdentity(Path, Identity: String): Boolean;
  external 'CheckDirectoryIdentity@{tmp}\PocketMaintenance.dll stdcall delayload';
procedure ExitIncompleteUninstall(Code: Cardinal);
  external 'ExitProcess@kernel32.dll stdcall uninstallonly';

procedure ReleaseMaintenancePins;
var I: Integer;
begin
  for I := GetArrayLength(MaintenancePins) - 1 downto 0 do CloseHandle(MaintenancePins[I]);
  SetArrayLength(MaintenancePins, 0);
end;

procedure ReleaseMaintenanceInstance;
var I: Integer;
begin
  if (MaintenanceInstance <> 0) and (MaintenanceInstance <> THandle(-1)) then CloseHandle(MaintenanceInstance);
  MaintenanceInstance := 0;
  for I := GetArrayLength(MaintenanceInstancePins) - 1 downto 0 do CloseHandle(MaintenanceInstancePins[I]);
  SetArrayLength(MaintenanceInstancePins, 0);
end;

procedure AcquireMaintenanceInstance(Root: String);
begin
  if (MaintenanceInstance <> 0) and (MaintenanceInstance <> THandle(-1)) then exit;
  if not PinDirectoryChain(AddBackslash(Root) + 'maintenance', MaintenanceInstancePins) then RaiseException('Cannot lock the maintenance directory.');
  { CreateNew + delete-on-close: never adopt an existing file by name. }
  MaintenanceInstance := CreateFileW(AddBackslash(Root) + 'maintenance\operation.lock', $C0010000, 0, 0, 1, $04200000, 0);
  if MaintenanceInstance = THandle(-1) then RaiseException('Another maintenance operation is active, or an unrecognized maintenance lock exists. Close the other setup/uninstaller and retry.');
end;

function PinVerifiedFile(Path, Hash: String): Boolean;
var Handle: THandle; Attributes: Cardinal;
begin
  Result := False;
  CheckPreparationCancellation;
  if not PinDirectoryChain(ExtractFileDir(Path), MaintenancePins) then exit;
  Handle := CreateFileW(Path, FileReadAttributes, ShareReadOnly, 0, OpenExisting, $00200000, 0);
  if Handle = THandle(-1) then exit;
  SetArrayLength(MaintenancePins, GetArrayLength(MaintenancePins) + 1);
  MaintenancePins[GetArrayLength(MaintenancePins) - 1] := Handle;
  Attributes := GetFileAttributesW(Path);
  if (Attributes = InvalidFileAttributes) or ((Attributes and (FileAttributeDirectory or FileAttributeReparsePoint)) <> 0) then exit;
  if FileExists(ExpandConstant('{tmp}\PocketMaintenance.dll')) then Result := VerifyMaintenanceFile(Path, Hash)
  else Result := CompareText(GetSHA256OfFile(Path), Hash) = 0;
end;

procedure PrepareNativeSupport;
var Source: String;
begin
  if FileExists(ExpandConstant('{tmp}\PocketMaintenance.dll')) then begin
    if CompareText(GetSHA256OfFile(ExpandConstant('{tmp}\PocketMaintenance.dll')), '{#SupportHash}') <> 0 then RaiseException('Temporary maintenance bootstrap changed.');
    exit;
  end;
  if IsUninstaller then Source := ExpandConstant('{app}\maintenance\InstallerSupport.dll')
  else begin
    { The files: CPU import already extracts and loads this DLL at startup.
      Re-extracting would overwrite a mapped image and fail with a sharing error. }
    Source := ExpandConstant('{tmp}\InstallerSupport.dll');
    if not FileExists(Source) then ExtractTemporaryFile('InstallerSupport.dll');
  end;
  try
    if not PinVerifiedFile(Source, '{#SupportHash}') then RaiseException('The maintenance bootstrap could not be verified.');
    if not FileCopy(Source, ExpandConstant('{tmp}\PocketMaintenance.dll'), False) then RaiseException('Cannot prepare the maintenance bootstrap.');
  finally ReleaseMaintenancePins; end;
end;

procedure MaterializeWorkerFile(TemporaryName, RelativePath: String);
var Destination: String;
begin
  CheckPreparationCancellation;
  MaintenanceProgress.SetText('Preparing maintenance files...', RelativePath);
  ExtractTemporaryFile(TemporaryName);
  CheckPreparationCancellation;
  Destination := AddBackslash(MaintenanceWorkerRoot) + RelativePath;
  if not ForceDirectories(ExtractFileDir(Destination)) or FileExists(Destination) then RaiseException('Cannot prepare temporary maintenance files.');
  if not RenameFile(ExpandConstant('{tmp}\') + TemporaryName, Destination) then RaiseException('Cannot materialize temporary maintenance files.');
end;

function RunMaintenance(Action, Root: String; RemoveData: Boolean; var ErrorText: String): Boolean;
var ExitCode: Integer; Worker, Manifest, Parameters, RemoveValue: String;
begin
  Result := False;
  MaintenanceReport := ExpandConstant('{tmp}\PocketMaintenance-report.ini');
  if FileExists(MaintenanceReport) and not DeleteFile(MaintenanceReport) then RaiseException('Cannot reset the maintenance report.');
  if FileExists(MaintenanceReport + '.cancel') and not DeleteFile(MaintenanceReport + '.cancel') then RaiseException('Cannot reset the maintenance cancellation request.');
  MaintenanceBusy := True;
  MaintenanceCancelable := not IsUninstaller and (Action = 'prepare');
  if MaintenanceExiting then SetMaintenanceWindow(0)
  else if IsUninstaller then SetMaintenanceWindow(UninstallProgressForm.Handle)
  else begin
    SetMaintenanceWindow(WizardForm.Handle);
    MaintenanceProgress.SetText('Verifying application files...', 'Please wait while maintenance completes.');
    MaintenanceProgress.Show;
    MaintenanceProgress.Animate;
    MaintenanceCancelButton.Visible := MaintenanceCancelable;
    MaintenanceCancelButton.Enabled := MaintenanceCancelable;
    MaintenanceCancelButton.BringToFront;
  end;
  try
    Root := RemoveBackslashUnlessRoot(Root);
    if RemoveData then RemoveValue := '1' else RemoveValue := '0';
    if IsUninstaller then begin
      MaintenanceWorkerRoot := ExpandConstant('{app}\maintenance\worker');
      Manifest := ExpandConstant('{app}\maintenance\package.json');
    end else begin
      if MaintenanceWorkerRoot = '' then begin
        MaintenanceWorkerRoot := ExpandConstant('{tmp}\maintenance-worker');
        MaterializeWorker;
        CheckPreparationCancellation;
        ExtractTemporaryFile('maintenance-package.json');
      end;
      Manifest := ExpandConstant('{tmp}\maintenance-package.json');
    end;
    Worker := AddBackslash(MaintenanceWorkerRoot) + 'PocketTTSStudioLite.Maintenance.exe';
    if not IsUninstaller and not MaintenanceExiting then MaintenanceProgress.SetText('Verifying maintenance files...', '');
    if not PinWorker(MaintenanceWorkerRoot) or not PinVerifiedFile(Manifest, '{#PackageManifestHash}') then RaiseException('Maintenance files are missing or changed. Reinstall the matching package before retrying.');
    CheckPreparationCancellation;
    Parameters := Action + ' "' + Root + '" "' + Manifest + '" "{#PackageManifestHash}" "' + MaintenanceReport + '" ' + RemoveValue;
    if FileExists(MaintenanceReport) then DeleteFile(MaintenanceReport);
    if not IsUninstaller and not MaintenanceExiting then MaintenanceProgress.SetText('Verifying application files...', '');
    ExitCode := RunMaintenanceProcess(Worker, Parameters, MaintenanceWorkerRoot);
    if ExitCode <> 0 then
      RaiseException(GetIniString('results', 'message', 'Maintenance could not finish. Keep recovery records and retry after closing applications.', MaintenanceReport));
    if GetIniString('results', 'complete', '0', MaintenanceReport) <> '1' then RaiseException('Maintenance did not verify completion.');
    if GetIniString('results', 'legacy_preserved', '0', MaintenanceReport) = '1' then
      Log('Earlier uninventoried files were preserved. Only the current package paths are adopted; no legacy directory sweep was performed.');
    Result := True;
  except ErrorText := GetExceptionMessage; end;
  MaintenanceCancelable := False;
  MaintenanceBusy := False;
  if not IsUninstaller and not MaintenanceExiting then begin
    MaintenanceCancelButton.Visible := False;
    MaintenanceProgress.Hide;
  end;
  ReleaseMaintenancePins;
  Log('Maintenance ' + Action + ': ' + ErrorText + '. Report: ' + MaintenanceReport);
end;

<event('PrepareToInstall')>
function PrepareOwnedMaintenance(var NeedsRestart: Boolean): String;
var ErrorText, CleanupError: String;
begin
  Result := '';
  if MaintenancePrepared and (CompareText(MaintenancePreparedRoot, WizardDirValue) = 0) then exit;
  try
    ReleaseMaintenanceInstance;
    PrepareNativeSupport;
    if FileExists(ExpandConstant('{tmp}\PocketMaintenance-report.ini.preparation.json')) then
      if not RunMaintenance('cancel-prepare', MaintenancePreparedRoot, False, CleanupError) then RaiseException(CleanupError);
    MaintenancePreparedRoot := WizardDirValue;
    if not RunMaintenance('prepare', WizardDirValue, False, ErrorText) then RaiseException(ErrorText);
    if MaintenanceCancelRequested then RaiseException('Installation preparation cancelled safely.');
    AcquireMaintenanceInstance(WizardDirValue);
    MaintenancePrepared := True;
    MaintenancePreparedRoot := WizardDirValue;
    WizardForm.CancelButton.Enabled := True;
  except
    Result := GetExceptionMessage;
    MaintenancePrepared := False;
    ReleaseMaintenanceInstance;
    if FileExists(ExpandConstant('{tmp}\PocketMaintenance-report.ini.preparation.json')) then
      if not RunMaintenance('cancel-prepare', MaintenancePreparedRoot, False, CleanupError) then begin
        Result := Result + #13#10 + 'Preparation cleanup is incomplete. ' + CleanupError;
        if MaintenanceCancelRequested then MsgBox(Result, mbError, MB_OK);
      end;
  end;
  if MaintenanceCancelRequested then PostMaintenanceMessage(WizardForm.Handle, $0010, 0, 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var ErrorText: String;
begin
  if CurStep = ssInstall then MaintenanceFilesStarted := True;
  if CurStep = ssPostInstall then begin
    MaintenanceFilesComplete := True;
    if not RunMaintenance('commit', ExpandConstant('{app}'), False, ErrorText) then begin
      MaintenanceFailed := True;
      MsgBox('Application files were installed, but verification or owned-file cleanup is incomplete.' + #13#10 + ErrorText + #13#10 + 'Rerun this same installer. Keep maintenance recovery records.', mbError, MB_OK);
    end else begin
      if not WizardIsTaskSelected('startmenuicon') then begin
        DeleteFile(ExpandConstant('{group}\Pocket TTS Studio lite.lnk'));
        DeleteFile(ExpandConstant('{group}\Uninstall Pocket TTS Studio lite.lnk'));
      end;
      if not WizardIsTaskSelected('desktopicon') then DeleteFile(ExpandConstant('{userdesktop}\Pocket TTS Studio lite.lnk'));
    end;
  end;
end;

function GetCustomSetupExitCode: Integer;
begin
  if MaintenanceFailed then Result := 1 else Result := 0;
end;

function ValidUninstallState: Boolean;
var State: String;
begin
  State := ExpandConstant('{app}\maintenance\uninstall-state.ini');
  Result := False;
  if not FileExists(State) then exit;
  try
    if not PinVerifiedFile(State, GetSHA256OfFile(State)) then exit;
    Result := (GetIniString('state', 'schema', '', State) = '1') and
      (GetIniString('state', 'app_id', '', State) = '336B2514-DBF3-4566-9302-95EE5D2D7DD3') and
      (CompareText(GetIniString('state', 'app_root', '', State), ExpandConstant('{app}')) = 0) and
      (CompareText(GetIniString('state', 'package_hash', '', State), '{#PackageManifestHash}') = 0) and
      (GetIniString('state', 'data_complete', '', State) = '1') and
      CheckDirectoryIdentity(ExpandConstant('{app}'), GetIniString('state', 'app_identity', '', State));
  finally ReleaseMaintenancePins; end;
end;

function InitializeUninstall: Boolean;
begin
  Result := False;
  try
    PrepareNativeSupport;
    AcquireMaintenanceInstance(ExpandConstant('{app}'));
    RemovePrivateData := False;
    if ValidUninstallState then begin
      RemovePrivateData := GetIniString('state', 'remove_private', '0', ExpandConstant('{app}\maintenance\uninstall-state.ini')) = '1';
      Log('Resuming the previously confirmed uninstall and private-data choice.');
    end else if not UninstallSilent and DirExists(PrivateDataPath) then
      RemovePrivateData := MsgBox('Also permanently delete verified private Pocket TTS Studio lite data?' + #13#10 + PrivateDataPath + #13#10#13#10 +
        'Downloaded models, runtime updates and supported settings will be removed. Unknown files, external profiles and shared caches are preserved.' + #13#10 +
        'Choose No to keep private data.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    Result := True;
  except MsgBox(GetExceptionMessage, mbError, MB_OK); end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var ErrorText, Hash, State: String; Names: TArrayOfString; I: Integer;
begin
  if CurUninstallStep <> usUninstall then exit;
  try
    UninstallProgressForm.OnCloseQuery := @RefuseMaintenanceClose;
    SetMaintenanceWindow(UninstallProgressForm.Handle);
    UninstallProgressForm.StatusLabel.Caption := 'Verifying and removing owned application data...';
    UninstallProgressForm.ProgressBar.Style := npbstMarquee;
    if not ValidUninstallState then
      if not RunMaintenance('uninstall', ExpandConstant('{app}'), RemovePrivateData, ErrorText) then RaiseException(ErrorText);
    if not ValidUninstallState then RaiseException('Uninstall recovery ownership could not be verified.');
    if not BeginOwnedDelete(ExpandConstant('{app}')) then RaiseException('The application directory could not be locked.');
    if not QueuePayloadFiles then RaiseException('An application file is busy, changed or linked. Close applications and retry; verified private-data cleanup remains recorded.');
    Names := ['installation.json', 'upgrade-plan.json', 'uninstall-plan.json'];
    State := ExpandConstant('{app}\maintenance\uninstall-state.ini');
    for I := 0 to GetArrayLength(Names) - 1 do begin
      Hash := GetIniString('metadata', Names[I], '', State);
      if (Hash <> '') and not QueueOwnedFile('maintenance\' + Names[I], Hash) then RaiseException('Maintenance metadata changed. Keep recovery records and retry.');
    end;
    { Queue recovery state last: keep it on preflight/partial payload failure.
      UninstallDelete Check is evaluated by Setup, not by Uninstall. }
    if not QueueOwnedFile('maintenance\uninstall-state.ini', GetSHA256OfFile(State)) then RaiseException('Uninstall recovery state changed. Keep recovery records and retry.');
    if not CommitOwnedDelete then RaiseException('Application cleanup was interrupted. Rerun this uninstaller to resume.');
    NativeRemovalComplete := True;
    { Inno removes its bootstrap and registered empty directories next. Our own
      delete-on-close lock and directory pins must no longer prevent that. }
    ReleaseMaintenanceInstance;
    UninstallProgressForm.OnCloseQuery := nil;
    UninstallProgressForm.ProgressBar.Style := npbstNormal;
    UninstallProgressForm.StatusLabel.Caption := 'Finishing Windows uninstall registration and shortcuts...';
  except
    CancelOwnedDelete;
    ErrorText := GetExceptionMessage;
    Log('Uninstall incomplete: ' + ErrorText);
    MsgBox(ErrorText + #13#10#13#10 + 'The uninstall entry and maintenance bootstrap were retained. Rerun this uninstaller. Log: ' + ExpandConstant('{log}'), mbError, MB_OK);
    ExitIncompleteUninstall(1);
  end;
end;

procedure DeinitializeSetup;
var ErrorText, Action: String;
begin
  ReleaseMaintenanceInstance;
  if not MaintenanceFilesComplete and (MaintenancePreparedRoot <> '') and
      FileExists(ExpandConstant('{tmp}\PocketMaintenance-report.ini.preparation.json')) then begin
    MaintenanceExiting := True;
    if MaintenanceFilesStarted then Action := 'rollback-install' else Action := 'cancel-prepare';
    if not RunMaintenance(Action, MaintenancePreparedRoot, False, ErrorText) then begin
      Log('Installation recovery is incomplete: ' + ErrorText);
      if not WizardSilent then MsgBox('Installation recovery could not finish.' + #13#10 + ErrorText + #13#10 +
        'Keep the recovery records under ' + AddBackslash(MaintenancePreparedRoot) + 'maintenance.', mbError, MB_OK);
    end;
  end;
end;

procedure DeinitializeUninstall;
begin
  ReleaseMaintenanceInstance;
  if NativeRemovalComplete then begin
    RemoveDir(ExpandConstant('{app}\maintenance'));
    RemoveDir(ExpandConstant('{app}'));
  end;
end;

#include SupportDir + "\maintenance-generated.iss"
