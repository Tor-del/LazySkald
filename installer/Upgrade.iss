// Shared upgrade module. Only the product identifiers come from AppConfig.iss.
const
  ProductUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductAppId}_is1';
var
  PreviousRemoved: Boolean;

function OpenEventW(DesiredAccess: LongWord; InheritHandle: Boolean; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Event: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function CloseProduct: Boolean;
var Event: THandle; Attempt: Integer;
begin
  Result := True;
  if not CheckForMutexes('{#ProductMutex}') then Exit;
  Log('Requesting a graceful application shutdown.');
  Event := OpenEventW(2, False, '{#ProductExitEvent}');
  if Event <> 0 then begin
    SetEvent(Event);
    CloseHandle(Event);
  end;
  for Attempt := 1 to 60 do begin
    if not CheckForMutexes('{#ProductMutex}') then Exit;
    Sleep(100);
  end;
  Result := False;
end;

function FindPrevious(var Root: Integer; var Command, Location: String): Boolean;
begin
  Root := HKCU64;
  Result := RegQueryStringValue(Root, ProductUninstallKey, 'UninstallString', Command);
  if not Result then begin
    Root := HKCU32;
    Result := RegQueryStringValue(Root, ProductUninstallKey, 'UninstallString', Command);
  end;
  if Result then begin
    if not RegQueryStringValue(Root, ProductUninstallKey, 'Inno Setup: App Path', Location) then
      RegQueryStringValue(Root, ProductUninstallKey, 'InstallLocation', Location);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Root, Code: Integer; Command, Location, Uninstaller, Name, Parameters: String;
begin
  Result := '';
  if not CloseProduct then begin
    Result := CustomMessage('CloseAppManual');
    Exit;
  end;
  if PreviousRemoved then Exit;
  if not FindPrevious(Root, Command, Location) then Exit;
  Uninstaller := RemoveQuotes(Trim(Command));
  Name := LowerCase(ExtractFileName(Uninstaller));
  if (Location = '') or not SameText(RemoveBackslashUnlessRoot(ExtractFileDir(Uninstaller)), RemoveBackslashUnlessRoot(Location)) or
     (Pos('unins', Name) <> 1) or (ExtractFileExt(Name) <> '.exe') or not FileExists(Uninstaller) then begin
    Result := CustomMessage('InvalidUninstaller');
    Exit;
  end;
  Log('Removing the registered previous version before installing files.');
  Parameters := '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=' + AddQuotes(ExpandConstant('{tmp}\previous-uninstall.log'));
  if not Exec(Uninstaller, Parameters, '', SW_HIDE, ewWaitUntilTerminated, Code) then begin
    Result := CustomMessage('UninstallFailed');
    Exit;
  end;
  if (Code <> 0) or RegKeyExists(Root, ProductUninstallKey) then begin
    Log(Format('Previous uninstall did not complete. Exit code: %d', [Code]));
    Result := CustomMessage('UninstallFailed');
    Exit;
  end;
  PreviousRemoved := True;
  Log('Previous version removed successfully. User settings were not deleted.');
end;

function InitializeUninstall: Boolean;
begin
  Result := CloseProduct;
  if not Result then
    SuppressibleMsgBox(CustomMessage('CloseAppManual'), mbError, MB_OK, IDOK);
end;

function ApplicationLanguage(Param: String): String;
begin
  if ActiveLanguage = 'russian' then Result := 'ru' else Result := 'en';
end;
