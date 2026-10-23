// Native Inno controls: no additional runtime, web view or unsigned helper.
const
  UiCanvas = $0F1010;
  UiPanel = $171919;
  UiLine = $2C3335;
  UiInk = $DDEBF2;
  UiMuted = $9EACB3;
  UiGold = $62A3C6;
var
  UiBackground, UiLogo: TBitmapImage;
  UiSceneSource, UiScene: TBitmap;
  UiStatusText: String;
  UiStatusHeading: Boolean;
  UiPrimary, UiCustom, UiClose, UiMinimize: TBitmapButton;
  UiVersion, UiLanguage: TLabel;
  UiProgress, UiProgressMax: Integer;
  UiCustomForm: TSetupForm;
  UiDirectory: TNewEdit;
  UiDesktop: TNewCheckBox;
  UiSpace, UiValidation: TLabel;
  UiInstalling, UiFinished, UiDesktopSelected, UiCustomAccepted: Boolean;
  UiFraming: Boolean;
  UiNativeResize: TNotifyEvent;

function UiShowWindow(Handle: HWND; Command: Integer): Boolean;
  external 'ShowWindow@user32.dll stdcall';
function UiReleaseCapture: Boolean;
  external 'ReleaseCapture@user32.dll stdcall';
function UiSendMessage(Handle: HWND; Msg: LongWord; WParam, LParam: Longint): Longint;
  external 'SendMessageW@user32.dll stdcall';
function UiRoundRegion(X1, Y1, X2, Y2, Width, Height: Integer): THandle;
  external 'CreateRoundRectRgn@gdi32.dll stdcall';
function UiSetRegion(Handle: HWND; Region: THandle; Redraw: Boolean): Integer;
  external 'SetWindowRgn@user32.dll stdcall';
function UiWindowRect(Handle: HWND; var Bounds: TRect): Boolean;
  external 'GetWindowRect@user32.dll stdcall';
function UiStretchPixels(Dest: THandle; X, Y, Width, Height: Integer; Source: THandle;
  SourceX, SourceY, SourceWidth, SourceHeight: Integer; Operation: LongWord): Boolean;
  external 'StretchBlt@gdi32.dll stdcall';
function UiCopyPixels(Dest: THandle; X, Y, Width, Height: Integer; Source: THandle;
  SourceX, SourceY: Integer; Operation: LongWord): Boolean;
  external 'BitBlt@gdi32.dll stdcall';
function UiStretchMode(Canvas: THandle; Mode: Integer): Integer;
  external 'SetStretchBltMode@gdi32.dll stdcall';

#include "InstallerProgressSurface.iss"

procedure UiPrepareScene;
begin
  if (UiScene.Width = WizardForm.ClientWidth) and (UiScene.Height = WizardForm.ClientHeight) then exit;
  UiScene.Width := WizardForm.ClientWidth; UiScene.Height := WizardForm.ClientHeight;
  UiStretchMode(UiScene.Canvas.Handle, 4); // HALFTONE, scale the backdrop once.
  UiStretchPixels(UiScene.Canvas.Handle, 0, 0, UiScene.Width, UiScene.Height,
    UiSceneSource.Canvas.Handle, 0, 0, UiSceneSource.Width, UiSceneSource.Height, $00CC0020);
  UiBackground.Bitmap := UiScene;
end;

procedure UiSetStatus(Text: String);
begin
  UiStatusText := Text;
  UiRenderSurface;
end;

procedure UiFrame(Form: TSetupForm; Radius: Integer);
var
  Bounds: TRect;
begin
  // Regions use native window pixels. Compute them from the final HWND, rather
  // than retaining a region created before Inno's show-time size adjustment.
  if UiWindowRect(Form.Handle, Bounds) then
    UiSetRegion(Form.Handle, UiRoundRegion(0, 0, Bounds.Right - Bounds.Left + 1,
      Bounds.Bottom - Bounds.Top + 1, ScaleX(Radius), ScaleY(Radius)), True);
end;

procedure UiLayout(Sender: TObject);
begin
  if UiFraming then exit;
  UiFraming := True;
  try
    if UiNativeResize <> nil then UiNativeResize(Sender);
    if UiSurface = nil then exit;
    UiBackground.SetBounds(0, 0, WizardForm.ClientWidth, WizardForm.ClientHeight);
    UiPrepareScene;
    UiLogo.Left := (WizardForm.ClientWidth - UiLogo.Width) div 2;
    UiPrimary.Left := (WizardForm.ClientWidth - UiPrimary.Width) div 2;
    UiCustom.Left := (WizardForm.ClientWidth - UiCustom.Width) div 2;
    UiClose.Left := WizardForm.ClientWidth - ScaleX(52);
    UiMinimize.Left := WizardForm.ClientWidth - ScaleX(96);
    UiVersion.Top := WizardForm.ClientHeight - ScaleY(39);
    UiLanguage.Top := UiVersion.Top;
    UiLanguage.Left := WizardForm.ClientWidth - ScaleX(122);
    UiLayoutSurface;
    UiFrame(WizardForm, 20);
    Log(Format('Destiny UI: final client %dx%d; close right %d; language right %d', [WizardForm.ClientWidth, WizardForm.ClientHeight, UiClose.Left + UiClose.Width, UiLanguage.Left + UiLanguage.Width]));
  finally
    UiFraming := False;
  end;
end;

procedure UiCustomResize(Sender: TObject);
begin
  if UiFraming then exit;
  UiFraming := True;
  try UiFrame(UiCustomForm, 16);
  finally UiFraming := False; end;
end;

function UiLabel(Parent: TWinControl; Text: String; X, Y, W, H, Size: Integer; Color: TColor): TLabel;
begin
  Result := TLabel.Create(WizardForm);
  Result.Parent := Parent;
  Result.AutoSize := False;
  Result.Transparent := True;
  Result.SetBounds(ScaleX(X), ScaleY(Y), ScaleX(W), ScaleY(H));
  Result.Font.Name := 'Segoe UI'; Result.Font.Size := Size; Result.Font.Color := Color;
  Result.Caption := Text;
  Result.WordWrap := True;
end;

procedure UiPaintButton(Button: TBitmapButton; Text: String; Primary: Boolean);
begin
  Button.Caption := Text;
  Button.Bitmap.Width := Button.Width - 4;
  Button.Bitmap.Height := Button.Height - 4;
  Button.Bitmap.Canvas.Brush.Color := UiCanvas;
  Button.Bitmap.Canvas.Rectangle(0, 0, Button.Width, Button.Height);
  if Primary then begin
    Button.Bitmap.Canvas.Brush.Color := UiGold;
    Button.Bitmap.Canvas.Pen.Color := $8BC5E2;
    Button.Bitmap.Canvas.Font.Color := $0E1A21;
  end else begin
    Button.Bitmap.Canvas.Brush.Color := UiPanel;
    Button.Bitmap.Canvas.Pen.Color := UiLine;
    Button.Bitmap.Canvas.Font.Color := UiInk;
  end;
  Button.Bitmap.Canvas.RoundRect(0, 0, Button.Bitmap.Width, Button.Bitmap.Height, ScaleX(12), ScaleY(12));
  Button.Bitmap.Canvas.Font.Name := 'Segoe UI';
  Button.Bitmap.Canvas.Font.Height := -ScaleY(14);
  Button.Bitmap.Canvas.Font.Style := [fsBold];
  Button.Bitmap.Canvas.TextOut((Button.Bitmap.Width - Button.Bitmap.Canvas.TextWidth(Text)) div 2,
    (Button.Bitmap.Height - Button.Bitmap.Canvas.TextHeight(Text)) div 2, Text);
end;

function UiButton(Parent: TWinControl; Text: String; X, Y, W, H: Integer; Primary: Boolean): TBitmapButton;
begin
  Result := TBitmapButton.Create(WizardForm);
  Result.Parent := Parent;
  Result.SetBounds(ScaleX(X), ScaleY(Y), ScaleX(W), ScaleY(H));
  Result.BackColor := clNone;
  Result.Cursor := crHand;
  Result.TabStop := True;
  UiPaintButton(Result, Text, Primary);
  // TBitmapButton subclasses BUTTON. Prevent Windows' default button painter
  // from drawing a light caption/background over our bitmap on state changes.
#ifndef UiAuditLegacyPaint
  UiSendMessage(Result.Handle, $00F4, $000B, 1); // BM_SETSTYLE, BS_OWNERDRAW
#endif
end;

function UiWindowButton(Name, ImageFile: String; X: Integer): TBitmapButton;
begin
  Result := TBitmapButton.Create(WizardForm);
  Result.Parent := WizardForm;
  Result.SetBounds(ScaleX(X), ScaleY(8), ScaleX(44), ScaleY(38));
  Result.Caption := Name;
  Result.ShowHint := False;
  Result.BackColor := clNone;
  Result.ParentBackground := True;
  Result.Cursor := crHand; Result.TabStop := True;
  Result.PngImage.LoadFromFile(ExpandConstant('{tmp}\') + ImageFile);
  Result.Stretch := True;
#ifndef UiAuditLegacyPaint
  UiSendMessage(Result.Handle, $00F4, $000B, 1);
#endif
end;

procedure UiDrag(Sender: TObject);
begin
  UiReleaseCapture;
  UiSendMessage(WizardForm.Handle, $00A1, 2, 0);
end;

procedure UiCloseClick(Sender: TObject);
begin
  if WizardForm.CurPageID = wpInstalling then exit;
  if UiFinished then WizardForm.NextButton.OnClick(WizardForm.NextButton)
  else WizardForm.CancelButton.OnClick(WizardForm.CancelButton);
end;

procedure UiMinimizeClick(Sender: TObject);
begin
  UiShowWindow(WizardForm.Handle, 6);
end;

function UiExistingParent(Directory: String): String;
var
  Parent: String;
begin
  while (Length(Directory) > 3) and not DirExists(Directory) do begin
    Parent := ExtractFileDir(Directory);
    if Parent = Directory then break;
    Directory := Parent;
  end;
  Result := Directory;
end;

procedure UiSpaceChanged(Sender: TObject);
var
  Directory: String;
  FreeMB, TotalMB: Cardinal;
begin
  Directory := UiExistingParent(Trim(UiDirectory.Text));
  if GetSpaceOnDisk(Directory, True, FreeMB, TotalMB) then
    UiSpace.Caption := 'Espacio necesario: {#RequiredMB} MB   |   Disponible: ' + IntToStr(FreeMB div 1024) + ' GB'
  else UiSpace.Caption := 'Espacio necesario: {#RequiredMB} MB';
  UiValidation.Caption := '';
end;

procedure UiBrowseClick(Sender: TObject);
var
  Directory: String;
begin
  Directory := UiDirectory.Text;
  if BrowseForFolder('Carpeta de Destiny Launcher', Directory, True) then UiDirectory.Text := Directory;
end;

procedure UiCustomCancel(Sender: TObject);
begin
  UiCustomForm.Close();
end;

procedure UiDesktopToggle(Sender: TObject);
begin
  UiDesktop.Checked := not UiDesktop.Checked;
end;

procedure UiCustomKey(Sender: TObject; var Key: Word; Shift: TShiftState);
begin
  if Key = 27 then begin
    UiCustomForm.Close();
    Key := 0;
  end;
end;

function UiValidDirectory(Directory: String): Boolean;
var
  FreeMB, TotalMB: Cardinal;
  Existing: String;
begin
  Result := False;
  Directory := Trim(Directory);
  if (Length(Directory) <= 3) or not ((Copy(Directory, 2, 2) = ':\') or (Copy(Directory, 1, 2) = '\\')) or (Pos('"', Directory) > 0) then begin
    UiValidation.Caption := 'Selecciona una carpeta de instalación válida.';
    exit;
  end;
  Existing := UiExistingParent(Directory);
  if GetSpaceOnDisk(Existing, True, FreeMB, TotalMB) and (FreeMB < {#RequiredMB}) then begin
    UiValidation.Caption := 'No hay espacio suficiente en esta unidad.';
    exit;
  end;
  Result := True;
end;

procedure UiCustomAccept(Sender: TObject);
begin
  if not UiValidDirectory(UiDirectory.Text) then exit;
  WizardForm.DirEdit.Text := Trim(UiDirectory.Text);
  UiDesktopSelected := UiDesktop.Checked;
  UiCustomAccepted := True;
  UiCustomForm.Close();
end;

procedure UiPrimaryClick(Sender: TObject);
var
  Code: Integer;
begin
  if UiFinished then begin
    if not Exec(ExpandConstant('{app}\DestinyLauncher.exe'), '', ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, Code) then begin
      UiStatusHeading := False;
      UiSetStatus('No se pudo abrir el launcher. Puedes iniciarlo desde Inicio.');
      exit;
    end;
  end else if UiDesktopSelected then WizardSelectTasks('desktopicon')
  else WizardSelectTasks('!desktopicon');
  WizardForm.NextButton.OnClick(WizardForm.NextButton);
end;

#ifdef InstallerUiAudit
  #include "InstallerUiAudit.iss"
#endif

procedure UiCustomClick(Sender: TObject);
var
  Browse, AcceptButton, BackButton: TBitmapButton;
  DirectoryLabel: TLabel;
  DesktopLabel: TLabel;
begin
  UiCustomAccepted := False;
  UiCustomForm := CreateCustomForm();
  try
    UiCustomForm.Caption := 'Instalación personalizada';
    UiCustomForm.BorderStyle := bsNone;
    UiCustomForm.KeyPreview := True; UiCustomForm.OnKeyDown := @UiCustomKey;
    UiCustomForm.Color := UiPanel;
    UiCustomForm.ClientWidth := ScaleX(624); UiCustomForm.ClientHeight := ScaleY(310);
    UiCustomForm.Position := poMainFormCenter;
    UiLabel(UiCustomForm, 'Instalación personalizada', 28, 26, 560, 34, 19, UiInk).Font.Name := 'Georgia';
    DirectoryLabel := UiLabel(UiCustomForm, 'CARPETA DEL LAUNCHER', 28, 80, 560, 20, 8, UiMuted);
    UiDirectory := TNewEdit.Create(UiCustomForm);
    UiDirectory.Parent := UiCustomForm;
    UiDirectory.SetBounds(ScaleX(28), ScaleY(109), ScaleX(452), ScaleY(32));
    UiDirectory.Font.Name := 'Segoe UI'; UiDirectory.Font.Size := 10;
    UiDirectory.Color := UiCanvas; UiDirectory.Font.Color := UiInk;
    UiDirectory.Text := WizardForm.DirEdit.Text;
    DirectoryLabel.FocusControl := UiDirectory;
    UiDirectory.OnChange := @UiSpaceChanged;
    Browse := UiButton(UiCustomForm, 'Cambiar', 490, 105, 106, 40, False);
    Browse.OnClick := @UiBrowseClick;
    UiSpace := UiLabel(UiCustomForm, '', 28, 154, 568, 24, 9, UiMuted);
    UiDesktop := TNewCheckBox.Create(UiCustomForm);
    UiDesktop.Parent := UiCustomForm;
    UiDesktop.SetBounds(ScaleX(28), ScaleY(184), ScaleX(22), ScaleY(26));
    UiDesktop.Font.Name := 'Segoe UI'; UiDesktop.Font.Size := 10;
    UiDesktop.Font.Color := UiInk; UiDesktop.Color := UiPanel;
    UiDesktop.Caption := 'Acceso directo en el escritorio'; UiDesktop.Checked := UiDesktopSelected;
    // Keep the accessible native checkbox name, but paint its caption with a
    // warm foreground: Windows' light checkbox theme forces black label text.
    DesktopLabel := UiLabel(UiCustomForm, UiDesktop.Caption, 56, 187, 540, 24, 10, UiInk);
    DesktopLabel.Cursor := crHand; DesktopLabel.FocusControl := UiDesktop;
    DesktopLabel.OnClick := @UiDesktopToggle;
    UiValidation := UiLabel(UiCustomForm, '', 28, 214, 568, 24, 9, $96A3E0);
    BackButton := UiButton(UiCustomForm, 'Volver', 28, 250, 110, 42, False);
    BackButton.OnClick := @UiCustomCancel;
    AcceptButton := UiButton(UiCustomForm, 'Continuar e instalar', 378, 250, 218, 42, True);
    AcceptButton.OnClick := @UiCustomAccept;
    UiSpaceChanged(nil);
    UiCustomForm.KeepSizeY := True;
    UiCustomForm.FlipSizeAndCenterIfNeeded(True, WizardForm, False);
    UiCustomForm.OnResize := @UiCustomResize;
    UiCustomResize(UiCustomForm);
    UiCustomForm.ShowModal();
  finally
    UiCustomForm.Free();
  end;
  if UiCustomAccepted then UiPrimaryClick(nil);
end;

procedure InitializeWizard;
var
  I: Integer;
begin
  if WizardSilent then exit;
  WizardForm.BorderStyle := bsNone;
  WizardForm.Color := UiCanvas;
  WizardForm.ClientWidth := ScaleX(840); WizardForm.ClientHeight := ScaleY(520);
  WizardForm.Position := poScreenCenter;
  for I := 0 to WizardForm.ControlCount - 1 do WizardForm.Controls[I].Visible := False;
  ExtractTemporaryFile('setup-background.png'); ExtractTemporaryFile('setup-logo.png');
  ExtractTemporaryFile('setup-close.png'); ExtractTemporaryFile('setup-minimize.png');
  UiBackground := TBitmapImage.Create(WizardForm);
  UiBackground.Parent := WizardForm;
  UiBackground.SetBounds(0, 0, ScaleX(840), ScaleY(520));
  UiBackground.PngImage.LoadFromFile(ExpandConstant('{tmp}\setup-background.png'));
  UiSceneSource := TBitmap.Create; UiSceneSource.Assign(UiBackground.Bitmap);
  UiScene := TBitmap.Create;
  UiBackground.Stretch := False; UiBackground.OnClick := @UiDrag;
  UiLogo := TBitmapImage.Create(WizardForm);
  UiLogo.Parent := WizardForm; UiLogo.BackColor := clNone;
  UiLogo.PngImage.LoadFromFile(ExpandConstant('{tmp}\setup-logo.png'));
  UiLogo.SetBounds(ScaleX(290), ScaleY(92), ScaleX(260), ScaleY(151));
  UiLogo.Stretch := True;
  UiPrimary := UiButton(WizardForm, 'Instalación rápida', 302, 326, 236, 54, True);
  UiPrimary.OnClick := @UiPrimaryClick;
  UiCustom := UiButton(WizardForm, 'Instalación personalizada', 302, 390, 236, 42, False);
  UiCustom.OnClick := @UiCustomClick;
  UiVersion := UiLabel(WizardForm, 'DESTINY LAUNCHER  /  {#AppVersion}', 32, 481, 550, 22, 8, UiMuted);
  UiLanguage := UiLabel(WizardForm, 'Español', 718, 481, 90, 22, 9, UiMuted); UiLanguage.Alignment := taRightJustify;
  UiClose := UiWindowButton('Cerrar instalador', 'setup-close.png', 788); UiClose.OnClick := @UiCloseClick;
  UiMinimize := UiWindowButton('Minimizar', 'setup-minimize.png', 744); UiMinimize.OnClick := @UiMinimizeClick;
  UiCreateSurface;
  WizardForm.KeepSizeY := True;
  WizardForm.FlipSizeAndCenterIfNeeded(False, nil, False);
  UiNativeResize := WizardForm.OnResize;
  WizardForm.OnResize := @UiLayout;
  UiLayout(WizardForm);
#ifdef InstallerUiAudit
  UiAuditStart;
#endif
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = wpSelectDir) or (PageID = wpSelectProgramGroup) or (PageID = wpSelectTasks) or (PageID = wpReady);
end;

procedure CurPageChanged(PageID: Integer);
begin
  if WizardSilent then exit;
  UiLayout(WizardForm);
  // Inno changes button visibility when navigating; keep its chrome suppressed.
  WizardForm.NextButton.SetBounds(-ScaleX(400), 0, ScaleX(100), ScaleY(30));
  WizardForm.NextButton.TabStop := False;
  WizardForm.NextButton.Visible := PageID <> wpInstalling;
  WizardForm.BackButton.Visible := False;
  // Inno's close path checks CancelButton.CanFocus, even for Alt+F4. Keep
  // this native command alive outside the client area instead of hiding it.
  WizardForm.CancelButton.SetBounds(-ScaleX(200), 0, ScaleX(100), ScaleY(30));
  WizardForm.CancelButton.TabStop := False;
  WizardForm.CancelButton.Visible := True;
  WizardForm.OuterNotebook.Visible := False;
  WizardForm.MainPanel.Visible := False; WizardForm.Bevel.Visible := False;
  UiInstalling := (PageID = wpPreparing) or (PageID = wpInstalling);
  UiFinished := PageID = wpFinished;
  UiClose.Enabled := PageID <> wpInstalling;
  UiPrimary.Visible := not UiInstalling;
  UiCustom.Visible := not UiInstalling and not UiFinished;
  UiPrimary.Top := ScaleY(326);
  UiStatusHeading := False;
  UiSurface.Visible := False;
  if UiInstalling then begin
    UiLayoutSurface;
    UiSetStatus('Preparando instalación…');
    UiSurface.Visible := True;
  end;
  if UiFinished then begin
    UiStatusHeading := True;
    UiLayoutSurface;
    UiSetStatus('Instalación completada');
    UiSurface.Visible := True;
    UiPrimary.Top := ScaleY(350);
    UiPaintButton(UiPrimary, 'Abrir launcher', True);
#ifdef InstallerUiAudit
    UiAuditFinished;
#endif
  end;
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  if WizardSilent or (MaxProgress <= 0) then exit;
  UiProgress := CurProgress; UiProgressMax := MaxProgress;
  UiSetStatus('Instalando  ' + IntToStr(Round(100.0 * CurProgress / MaxProgress)) + '%');
#ifdef InstallerUiAudit
  UiAuditProgress(CurProgress, MaxProgress);
#endif
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  Cancel := CurPageID <> wpInstalling;
  Confirm := False;
end;




