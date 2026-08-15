; Offstream installer (Inno Setup 6).
;
; Per-user by design, and that is the whole reason WiX/MSI was not chosen: Offstream needs no
; administrator rights to run - routing, session mute and loopback capture were all verified
; unelevated in Phase 0 - so the thing that installs it should not ask for any either. An
; elevation prompt is a decision the user has to make about software they have not run yet.
;
; Built by build/windows/build-installer.ps1, which passes AppVersion and SourceDir. Building
; this file directly needs both:
;
;   iscc offstream.iss /DAppVersion=1.2.3 /DSourceDir=..\..\artifacts\staging\Offstream-1.2.3-win-x64

#ifndef AppVersion
  #error AppVersion is required. Pass /DAppVersion=1.2.3 - the version comes from the git tag.
#endif

#ifndef SourceDir
  #error SourceDir is required. Pass /DSourceDir=<the staged publish folder>.
#endif

#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif

#define AppName "Offstream"
#define AppPublisher "Offstream contributors"
#define AppUrl "https://github.com/revtex/offstream"
#define AppExeName "Offstream.exe"

[Setup]
; Never regenerate this. It is how Windows knows an upgrade from a second installation, and a
; new one strands the previous version in Apps & features with no way to remove it.
AppId={{BA198809-2F62-4C20-AF9C-968CDDB7FE4A}

AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases

; lowest, and {autopf} therefore resolves to {localappdata}\Programs rather than Program Files.
; PrivilegesRequiredOverridesAllowed is deliberately not set: there is no supported machine-wide
; install, so offering one would create a second install location nothing else knows about.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; Offstream is Windows 11 only (decided 2026-08-11) and ships win-x64 only. Both are checked
; here so the failure is a sentence during setup rather than a crash on first run.
MinVersion=10.0.22000
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; The app's own single-instance mutex (OffstreamPaths.InstanceMutex). Setup asks the user to
; close a running copy instead of failing halfway through replacing a locked executable.
AppMutex=Local\Offstream

LicenseFile={#SourceDir}\LICENSE
InfoAfterFile={#SourceDir}\NOTICE
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-setup
SetupIconFile=..\..\src\Offstream.App\Assets\offstream.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

; The payload is a self-contained .NET publish plus a 108 MB ffmpeg, so compression is worth
; the build minutes. Solid compression pays off precisely because there are a few very large,
; similar files rather than many small ones.
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
; Matching the app's own en/fr localisation. An installer in one language for an app that
; offers two is a seam the user meets before anything else.
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; recursesubdirs picks up the ffmpeg folder, which has to keep its name: FFmpegLocator looks
; for {app}\ffmpeg\ffmpeg.exe and falls through to PATH if it is not there.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Anything the app wrote inside its own install folder. Recordings are not here - they go to
; the user's Music folder by default and are never touched.
Type: filesandordirs; Name: "{app}\ffmpeg"

[Code]
{ Uninstalling leaves settings and logs behind unless the user says otherwise, and asks rather
  than guessing. Deleting them silently loses a Last.fm API key and a Spotify sign-in that
  reinstalling will ask for again; keeping them silently is the wrong answer for someone who is
  uninstalling because they are done. Recordings are never in scope: they are the point of the
  app, they live outside this folder, and no uninstaller should reach them. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  DataDir := ExpandConstant('{userappdata}\Offstream');

  if not DirExists(DataDir) then
    Exit;

  if MsgBox('Remove Offstream''s settings and logs as well?' + #13#10#13#10 +
            DataDir + #13#10#13#10 +
            'This includes your Last.fm API key and Spotify sign-in. Your recordings are ' +
            'stored elsewhere and are never removed.',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(DataDir, True, True, True);
end;
