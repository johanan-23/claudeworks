; ClaudeWorks - installer (Inno Setup 6)
; Build: ISCC.exe /DAppVersion=1.0.0 /DSourceDir=<publish folder> ClaudeWorks.iss  (build.ps1 does this)
; Silent: ClaudeWorks-Setup.exe /S   (or Inno's /VERYSILENT /SUPPRESSMSGBOXES)
;         add /CLOSESOLIDWORKS to close a running SOLIDWORKS instead of failing.
; Uninstall keeps nothing by default; pass /KEEPDATA to keep chats, settings and the saved key.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\src\ClaudeSW.AddIn\bin\Release\net48"
#endif
#define AddinGuid "{{ABA6C017-84D2-41CD-BD49-E617BE823AB0}"
#define ProgId "ClaudeForSolidWorks.AddIn"
#define AddinClass "ClaudeSW.AddIn.SwAddin"
#define AsmName "ClaudeSW.AddIn, Version=" + AppVersion + ".0, Culture=neutral, PublicKeyToken=null"

[Setup]
AppId={{1691D016-AFD8-4BBB-B1A0-54B0C1EAB367}
AppName=ClaudeWorks
AppVersion={#AppVersion}
AppVerName=ClaudeWorks {#AppVersion}
AppPublisher=ClaudeWorks (unofficial)
DefaultDirName={autopf}\ClaudeWorks
DefaultGroupName=ClaudeWorks
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=output
OutputBaseFilename=ClaudeWorks-Setup
SetupIconFile=..\assets\icons\app.ico
UninstallDisplayIcon={app}\icons\app.ico
UninstallDisplayName=ClaudeWorks
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=no
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName=ClaudeWorks
; Code signing (optional): ISCC /Ssigntool="signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /a $f" ...
#ifdef SignTool
SignTool=signtool
SignedUninstaller=yes
#endif

[Tasks]
Name: "mcpcode"; Description: "Connect Claude &Code (use these tools on your Claude plan, no API key)"; GroupDescription: "Use with Claude Code / Claude Desktop (MCP):"
Name: "mcpdesktop"; Description: "Connect Claude &Desktop"; GroupDescription: "Use with Claude Code / Claude Desktop (MCP):"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\assets\icons\app.ico"; DestDir: "{app}\icons"; Flags: ignoreversion
Source: "redist\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWebView2

[Registry]
; --- COM registration of the add-in class (what RegAsm /codebase would write), 64-bit view ---
Root: HKLM64; Subkey: "SOFTWARE\Classes\CLSID\{#AddinGuid}"; ValueType: string; ValueName: ""; ValueData: "{#AddinClass}"; Flags: uninsdeletekey
Root: HKLM64; Subkey: "SOFTWARE\Classes\CLSID\{#AddinGuid}\ProgId"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"
Root: HKLM64; Subkey: "SOFTWARE\Classes\CLSID\{#AddinGuid}\Implemented Categories\{{62C8FE65-4EBB-45e7-B440-6E39B2CDBF29}"
Root: HKLM64; Subkey: "SOFTWARE\Classes\CLSID\{#AddinGuid}\InprocServer32"; ValueType: string; ValueName: ""; ValueData: "mscoree.dll"
Root: HKLM64; Subkey: "SOFTWARE\Classes\CLSID\{#AddinGuid}\InprocServer32"; ValueType: string; ValueName: "ThreadingModel"; ValueData: "Both"
Root: HKLM64; Subkey: "SOFTWARE\Classes\CLSID\{#AddinGuid}\InprocServer32"; ValueType: string; ValueName: "Class"; ValueData: "{#AddinClass}"
Root: HKLM64; Subkey: "SOFTWARE\Classes\CLSID\{#AddinGuid}\InprocServer32"; ValueType: string; ValueName: "Assembly"; ValueData: "{#AsmName}"
Root: HKLM64; Subkey: "SOFTWARE\Classes\CLSID\{#AddinGuid}\InprocServer32"; ValueType: string; ValueName: "RuntimeVersion"; ValueData: "v4.0.30319"
Root: HKLM64; Subkey: "SOFTWARE\Classes\CLSID\{#AddinGuid}\InprocServer32"; ValueType: string; ValueName: "CodeBase"; ValueData: "{code:CodeBase}"
Root: HKLM64; Subkey: "SOFTWARE\Classes\{#ProgId}"; ValueType: string; ValueName: ""; ValueData: "{#AddinClass}"; Flags: uninsdeletekey
Root: HKLM64; Subkey: "SOFTWARE\Classes\{#ProgId}\CLSID"; ValueType: string; ValueName: ""; ValueData: "{#AddinGuid}"
; --- SOLIDWORKS add-in registration (shared by all installed SOLIDWORKS versions) ---
Root: HKLM64; Subkey: "SOFTWARE\SolidWorks\AddIns\{#AddinGuid}"; ValueType: dword; ValueName: ""; ValueData: 1; Flags: uninsdeletekey
Root: HKLM64; Subkey: "SOFTWARE\SolidWorks\AddIns\{#AddinGuid}"; ValueType: string; ValueName: "Title"; ValueData: "ClaudeWorks"
Root: HKLM64; Subkey: "SOFTWARE\SolidWorks\AddIns\{#AddinGuid}"; ValueType: string; ValueName: "Description"; ValueData: "Chat with Claude in the Task Pane to inspect, build and edit parts, assemblies and drawings."
; --- load at startup for the installing user ---
Root: HKCU; Subkey: "Software\SolidWorks\AddInsStartup\{#AddinGuid}"; ValueType: dword; ValueName: ""; ValueData: 1; Flags: uninsdeletekey

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Installing the Microsoft Edge WebView2 Runtime..."; Flags: waituntilterminated; Check: NeedsWebView2
Filename: "{app}\ClaudeSW.Mcp.exe"; Parameters: "--register-claude-code"; StatusMsg: "Connecting Claude Code..."; Flags: runasoriginaluser runhidden waituntilterminated; Tasks: mcpcode
Filename: "{app}\ClaudeSW.Mcp.exe"; Parameters: "--register-claude-desktop"; StatusMsg: "Connecting Claude Desktop..."; Flags: runasoriginaluser runhidden waituntilterminated; Tasks: mcpdesktop

[UninstallRun]
Filename: "{app}\ClaudeSW.Mcp.exe"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterMcp"

[Code]
const
  WebView2Key = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2KeyUser = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  AddinGuid = '{ABA6C017-84D2-41CD-BD49-E617BE823AB0}';

var
  Diag: TStringList;

function CodeBase(Param: String): String;
var S: String;
begin
  S := ExpandConstant('{app}\ClaudeSW.AddIn.dll');
  StringChangeEx(S, '\', '/', True);
  Result := 'file:///' + S;
end;

procedure D(const Line: String);
begin
  Log(Line);
  if Diag <> nil then Diag.Add(Line);
end;

function HasParam(const Name: String): Boolean;
var I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then Result := True;
end;

function WebView2Version(): String;
begin
  Result := '';
  if not RegQueryStringValue(HKLM, WebView2Key, 'pv', Result) then
    if not RegQueryStringValue(HKCU, WebView2KeyUser, 'pv', Result) then Result := '';
  if Result = '0.0.0.0' then Result := '';
end;

function NeedsWebView2(): Boolean;
begin
  Result := WebView2Version() = '';
end;

function DotNetRelease(): Cardinal;
begin
  if not RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Result) then Result := 0;
end;

function SolidWorksRunning(): Boolean;
var Code: Integer;
begin
  { tasklist exits 0 either way; find /I returns 0 only when the process is listed }
  Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq SLDWORKS.exe" /NH | find /I "SLDWORKS.exe" >nul', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := Code = 0;
end;

function CloseSolidWorks(): Boolean;
var Code, I: Integer;
begin
  { polite close first (lets SOLIDWORKS prompt to save), then wait up to 60 s }
  Log('Asking SOLIDWORKS to close (taskkill /IM SLDWORKS.exe, not forced)');
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM SLDWORKS.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Log('taskkill exit code ' + IntToStr(Code));
  for I := 1 to 60 do
  begin
    if not SolidWorksRunning() then begin Result := True; Exit; end;
    Sleep(1000);
  end;
  Result := not SolidWorksRunning();
  if not Result then Log('SOLIDWORKS is still running after 60 s (a save prompt or a busy command?)');
end;

function EnsureSolidWorksClosed(const Action: String; Silent: Boolean): Boolean;
begin
  Result := True;
  if not SolidWorksRunning() then Exit;
  Log('SOLIDWORKS is running (' + Action + '); silent=' + IntToStr(Ord(Silent)) + ' /CLOSESOLIDWORKS=' + IntToStr(Ord(HasParam('/CLOSESOLIDWORKS'))));
  if Silent then
  begin
    if HasParam('/CLOSESOLIDWORKS') then Result := CloseSolidWorks()
    else begin Log('SOLIDWORKS is running; pass /CLOSESOLIDWORKS to close it.'); Result := False; end;
    Exit;
  end;
  while SolidWorksRunning() do
  begin
    case MsgBox('SOLIDWORKS is running. Close it to ' + Action + ' ClaudeWorks.' #13#10#13#10 +
                'Yes: close SOLIDWORKS now (you will be asked to save your work).' #13#10 +
                'No: I closed it myself, check again.' #13#10 +
                'Cancel: stop.', mbConfirmation, MB_YESNOCANCEL) of
      IDYES: if not CloseSolidWorks() then MsgBox('SOLIDWORKS did not close (maybe a save prompt is open). Finish it, then continue.', mbInformation, MB_OK);
      IDCANCEL: begin Result := False; Exit; end;
    end;
  end;
end;

function InitializeSetup(): Boolean;
var Code: Integer; Args, TmpExe: String; I: Integer;
begin
  Result := True;
  { NSIS-style /S: re-run a temp copy of ourselves fully silent with the same extra arguments.
    Re-running the original file directly is refused with "Access is denied" while it is still open. }
  if HasParam('/S') then
  begin
    Args := '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART';
    for I := 1 to ParamCount do
      if CompareText(ParamStr(I), '/S') <> 0 then Args := Args + ' ' + AddQuotes(ParamStr(I));
    TmpExe := ExpandConstant('{tmp}\silent-setup.exe');
    if FileCopy(ExpandConstant('{srcexe}'), TmpExe, False) and
       Exec(TmpExe, Args, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    begin
      Log('Silent install finished with exit code ' + IntToStr(Code));
      Result := False;
      Exit;
    end;
    Log('Silent relaunch failed (' + SysErrorMessage(Code) + '); continuing with the normal installer.');
  end;
  if DotNetRelease() < 528040 then
  begin
    SuppressibleMsgBox('ClaudeWorks needs .NET Framework 4.8 or later, which SOLIDWORKS 2022-2026 also require.' #13#10 +
      'Install it from https://dotnet.microsoft.com/download/dotnet-framework/net48 and run this setup again.', mbCriticalError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;
  Result := EnsureSolidWorksClosed('install', WizardSilent());
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Names: TArrayOfString; I: Integer; Found: String; Path: String;
begin
  if CurStep = ssInstall then
  begin
    Diag := TStringList.Create;
    D('ClaudeWorks {#AppVersion} install ' + GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':'));
    D('Windows ' + GetWindowsVersionString + ', .NET Framework release ' + IntToStr(DotNetRelease()));
    D('WebView2 runtime before install: ' + WebView2Version());
    Found := '';
    if RegGetSubkeyNames(HKLM64, 'SOFTWARE\SolidWorks', Names) then
      for I := 0 to GetArrayLength(Names) - 1 do
        if Pos('SOLIDWORKS 20', Uppercase(Names[I])) = 1 then
        begin
          Path := '';
          RegQueryStringValue(HKLM64, 'SOFTWARE\SolidWorks\' + Names[I] + '\Setup', 'SolidWorks Folder', Path);
          D('Detected ' + Names[I] + ' ' + Path);
          Found := Found + Names[I] + ' ';
        end;
    if Found = '' then D('No SOLIDWORKS installation detected (the add-in registers anyway and loads once SOLIDWORKS is installed).');
    { remove any per-user developer registration, which would shadow this install }
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\CLSID\' + AddinGuid);
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\ClaudeForSolidWorks.AddIn');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\SolidWorks\AddIns\' + AddinGuid);
  end;
  if CurStep = ssPostInstall then
  begin
    D('WebView2 runtime after install: ' + WebView2Version());
    D('Registered add-in ' + AddinGuid + ' at ' + CodeBase(''));
    if Diag <> nil then Diag.SaveToFile(ExpandConstant('{app}\install-diagnostics.txt'));
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := EnsureSolidWorksClosed('uninstall', UninstallSilent());
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var RemoveData: Boolean;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveData := not HasParam('/KEEPDATA');
    if RemoveData and not UninstallSilent() then
      RemoveData := MsgBox('Also delete your Claude chats, settings and saved API key from this PC?', mbConfirmation, MB_YESNO) = IDYES;
    if RemoveData then
    begin
      DelTree(ExpandConstant('{userappdata}\ClaudeForSolidWorks'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\ClaudeForSolidWorks'), True, True, True);
    end;
    { per-user leftovers from developer builds }
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\CLSID\' + AddinGuid);
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\ClaudeForSolidWorks.AddIn');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\SolidWorks\AddIns\' + AddinGuid);
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\SolidWorks\AddInsStartup\' + AddinGuid);
    DelTree(ExpandConstant('{app}'), True, True, True);
  end;
end;
