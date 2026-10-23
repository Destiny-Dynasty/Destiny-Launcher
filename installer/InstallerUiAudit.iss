// Compiled only with -UiAudit -Experimental -Offline, under artifacts, using a
// separate AppId and registry key. Production installers contain none of this.
type
  TUiAuditMessage = record
    Window: HWND;
    Message: LongWord;
    WParam, LParam: Longint;
    Time: LongWord;
    Point: TPoint;
  end;
var
  UiAuditStarted: Boolean;
  UiAuditPercent: Integer;

function UiAuditSetTimer(Window: HWND; Id, Interval: LongWord; Callback: Longint): LongWord;
  external 'SetTimer@user32.dll stdcall';
function UiAuditKillTimer(Window: HWND; Id: LongWord): Boolean;
  external 'KillTimer@user32.dll stdcall';
function UiAuditPeekMessage(var Message: TUiAuditMessage; Window: HWND; Min, Max, Remove: LongWord): Boolean;
  external 'PeekMessageW@user32.dll stdcall';
function UiAuditTranslateMessage(var Message: TUiAuditMessage): Boolean;
  external 'TranslateMessage@user32.dll stdcall';
function UiAuditDispatchMessage(var Message: TUiAuditMessage): Longint;
  external 'DispatchMessageW@user32.dll stdcall';
function UiAuditTicks: LongWord;
  external 'GetTickCount@kernel32.dll stdcall';

procedure UiAuditTimer(Window: HWND; Message, Id, Time: LongWord);
begin
  if not FileExists('{#UiAuditRoot}\recording.ready') then exit;
  if UiAuditStarted then exit;
  UiAuditStarted := True;
  UiAuditKillTimer(0, Id);
  Log('UI audit: starting a real isolated install.');
  UiPrimaryClick(nil);
end;

procedure UiAuditStart;
begin
  WizardForm.DirEdit.Text := '{#UiAuditRoot}\program';
  UiAuditPercent := -1;
  UiScene.SaveToFile('{#UiAuditRoot}\scene.bmp');
  SaveStringToFile('{#UiAuditRoot}\ready.txt', 'Waiting for the window recorder.', False);
  UiAuditSetTimer(0, 0, 100, CreateCallback(@UiAuditTimer));
end;

procedure UiAuditProgress(Current, Maximum: Integer);
var
  Message: TUiAuditMessage;
  Started: LongWord;
begin
  if Maximum <= 0 then exit;
  if Round(100.0 * Current / Maximum) = UiAuditPercent then exit;
  UiAuditPercent := Round(100.0 * Current / Maximum);
  UiSurface.Update();
  SaveStringToFile('{#UiAuditRoot}\progress.txt', IntToStr(Current) + '/' + IntToStr(Maximum), False);
  Log(Format('UI audit: real progress %d/%d (%d%%)', [Current, Maximum, UiAuditPercent]));
  // Pace each real percentage for high-frame-rate recording; never invent
  // progress. The test-only message pump keeps paint/state transitions live.
  Started := UiAuditTicks;
  while UiAuditTicks - Started < 80 do begin
    while UiAuditPeekMessage(Message, 0, 0, 0, 1) do begin
      UiAuditTranslateMessage(Message); UiAuditDispatchMessage(Message);
    end;
    Sleep(1);
  end;
end;

procedure UiAuditFinished;
begin
  SaveStringToFile('{#UiAuditRoot}\finished.txt', 'Real isolated install completed.', False);
  Log('UI audit: actual installation finished; waiting for the tester to close the completion view.');
end;
