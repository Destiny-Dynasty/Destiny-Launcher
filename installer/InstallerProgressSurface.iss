// One opaque HWND owns the entire progress composition. Unlike windowless
// graphic controls, the parent cannot erase through it between paint passes.
// WM_PAINT always presents the last complete frame, with no allocation or
// intermediate track/text drawing on the screen. Uses only Windows APIs.
// References: Microsoft WM_ERASEBKGND, Memory Device Contexts, SetWindowSubclass.
type
  TUiPaintStruct = record
    DC: THandle;
    Erase: Longint;
    PaintRect: TRect;
    Restore, IncUpdate: Longint;
    Reserved: array[0..31] of Byte;
  end;
var
  UiSurface: TPanel;
  UiFrontFrame, UiBackFrame: TBitmap;
  UiSurfaceCallback: Longint;

function UiSubclass(Window: HWND; Callback, Id, Data: Longint): Boolean;
  external 'SetWindowSubclass@comctl32.dll stdcall';
function UiUnsubclass(Window: HWND; Callback, Id: Longint): Boolean;
  external 'RemoveWindowSubclass@comctl32.dll stdcall';
function UiDefaultSubclass(Window: HWND; Msg: LongWord; WParam, LParam: Longint): Longint;
  external 'DefSubclassProc@comctl32.dll stdcall';
function UiBeginPaint(Window: HWND; var Paint: TUiPaintStruct): THandle;
  external 'BeginPaint@user32.dll stdcall';
function UiEndPaint(Window: HWND; var Paint: TUiPaintStruct): Boolean;
  external 'EndPaint@user32.dll stdcall';
function UiInvalidate(Window: HWND; Bounds: Longint; Erase: Boolean): Boolean;
  external 'InvalidateRect@user32.dll stdcall';

function UiSurfaceProc(Window: HWND; Msg: LongWord; WParam, LParam, Id, Data: Longint): Longint;
var
  Paint: TUiPaintStruct;
  DC: THandle;
begin
  if Msg = $0014 then begin // WM_ERASEBKGND: our complete frame includes background.
    Result := 1;
    exit;
  end;
  if (Msg = $000F) or (Msg = $0318) then begin // WM_PAINT / WM_PRINTCLIENT
    if Msg = $000F then DC := UiBeginPaint(Window, Paint) else DC := WParam;
    try
      if (DC <> 0) and (UiFrontFrame <> nil) then
        UiCopyPixels(DC, 0, 0, UiFrontFrame.Width, UiFrontFrame.Height,
          UiFrontFrame.Canvas.Handle, 0, 0, $00CC0020);
    finally
      if Msg = $000F then UiEndPaint(Window, Paint);
    end;
    Result := 0;
    exit;
  end;
  if Msg = $0082 then UiUnsubclass(Window, UiSurfaceCallback, 1); // WM_NCDESTROY
  Result := UiDefaultSubclass(Window, Msg, WParam, LParam);
end;

procedure UiRenderSurface;
var
  OldFront: TBitmap;
  TextHeight, TrackTop, Filled: Integer;
begin
  if (UiSurface = nil) or (UiScene = nil) or (UiScene.Width = 0) then exit;
  UiBackFrame.Width := UiSurface.Width; UiBackFrame.Height := UiSurface.Height;
  UiCopyPixels(UiBackFrame.Canvas.Handle, 0, 0, UiBackFrame.Width, UiBackFrame.Height,
    UiScene.Canvas.Handle, UiSurface.Left, UiSurface.Top, $00CC0020);
  UiBackFrame.Canvas.Brush.Style := bsClear;
  if UiStatusHeading then begin
    UiBackFrame.Canvas.Font.Name := 'Georgia';
    UiBackFrame.Canvas.Font.Height := -ScaleY(32);
    UiBackFrame.Canvas.Font.Color := $8BC5E2;
  end else begin
    UiBackFrame.Canvas.Font.Name := 'Segoe UI';
    UiBackFrame.Canvas.Font.Height := -ScaleY(16);
    UiBackFrame.Canvas.Font.Color := UiInk;
  end;
  TextHeight := UiSurface.Height;
  if UiInstalling then TextHeight := ScaleY(48);
  UiBackFrame.Canvas.TextOut((UiSurface.Width - UiBackFrame.Canvas.TextWidth(UiStatusText)) div 2,
    (TextHeight - UiBackFrame.Canvas.TextHeight(UiStatusText)) div 2, UiStatusText);
  if UiInstalling then begin
    TrackTop := UiSurface.Height - ScaleY(6);
    UiBackFrame.Canvas.Brush.Style := bsSolid;
    UiBackFrame.Canvas.Brush.Color := UiLine; UiBackFrame.Canvas.Pen.Color := UiLine;
    UiBackFrame.Canvas.Rectangle(0, TrackTop, UiSurface.Width, UiSurface.Height);
    if UiProgressMax > 0 then begin
      Filled := Round(1.0 * UiSurface.Width * UiProgress / UiProgressMax);
      if Filled > UiSurface.Width then Filled := UiSurface.Width;
      if Filled > 0 then begin
        UiBackFrame.Canvas.Brush.Color := UiGold; UiBackFrame.Canvas.Pen.Color := UiGold;
        UiBackFrame.Canvas.Rectangle(0, TrackTop, Filled, UiSurface.Height);
      end;
    end;
  end;
  // No HWND or message-pumping call may occur while building the back frame.
  // Publish only after backdrop, text and bar are all ready.
  OldFront := UiFrontFrame; UiFrontFrame := UiBackFrame; UiBackFrame := OldFront;
  UiSurface.Caption := UiStatusText; // Keep the status available to accessibility.
  UiInvalidate(UiSurface.Handle, 0, False);
  UiSurface.Update();
end;

procedure UiLayoutSurface;
begin
  if UiSurface = nil then exit;
  if UiFinished then
    UiSurface.SetBounds(ScaleX(36), ScaleY(274), WizardForm.ClientWidth - ScaleX(72), ScaleY(54))
  else
    UiSurface.SetBounds(ScaleX(36), WizardForm.ClientHeight - ScaleY(123),
      WizardForm.ClientWidth - ScaleX(72), ScaleY(60));
  UiRenderSurface;
end;

procedure UiCreateSurface;
begin
  UiFrontFrame := TBitmap.Create; UiBackFrame := TBitmap.Create;
  UiSurface := TPanel.Create(WizardForm);
  UiSurface.Visible := False; UiSurface.Parent := WizardForm;
  UiSurface.Caption := ''; UiSurface.TabStop := False;
  UiSurfaceCallback := CreateCallback(@UiSurfaceProc);
  if not UiSubclass(UiSurface.Handle, UiSurfaceCallback, 1, 0) then
    RaiseException('No se pudo inicializar la superficie de progreso.');
end;

procedure UiDestroySurface;
begin
  if UiSurface <> nil then begin
    UiUnsubclass(UiSurface.Handle, UiSurfaceCallback, 1);
    UiSurface.Free(); UiSurface := nil;
  end;
  if UiFrontFrame <> nil then begin UiFrontFrame.Free(); UiFrontFrame := nil; end;
  if UiBackFrame <> nil then begin UiBackFrame.Free(); UiBackFrame := nil; end;
end;
