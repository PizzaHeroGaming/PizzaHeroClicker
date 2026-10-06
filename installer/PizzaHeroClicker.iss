; Inno Setup script for Pizza Hero Clicker.
; Build with:  .\installer\build-installer.ps1      (publishes the exe, then compiles this)
; Needs Inno Setup 6.3 or newer: https://jrsoftware.org/isinfo.php

#ifndef AppVersion
  #define AppVersion "1.1.2"
#endif
#define AppName "Pizza Hero Clicker"
#define AppExe "PizzaHeroClicker.exe"
#define Publisher "Pizza Hero Gaming"
#define Website "https://pizzaherogaming.github.io/PizzaHeroGaming/"

[Setup]
; AppId identifies the app for upgrades and uninstall. Never change it once a version has shipped.
AppId={{6E2B1C54-9D0A-4F7B-8A43-2C5D7E1F9B60}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#Website}
AppSupportURL={#Website}
VersionInfoVersion={#AppVersion}

; Installs for the current user by default: no administrator prompt. The first page lets
; someone choose "all users" instead (that choice does ask for administrator rights).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; 64-bit Windows 10 version 1803 or newer (what the app itself needs).
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17134

; Matches the single-instance mutex in App.xaml.cs, so Setup asks the user to close a running copy.
AppMutex=PizzaHeroClicker.SingleInstance

SetupIconFile=..\src\PizzaHeroClicker\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
OutputDir=output
OutputBaseFilename=PizzaHeroClicker-Setup-{#AppVersion}
; The exe is already compressed internally, so heavier settings would only slow Setup down.
Compression=lzma2/fast
SolidCompression=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "staging\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; DestName: "README.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent
; The app's own updater runs Setup with /SILENT /RELAUNCH=1: start the new version afterwards.
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: RelaunchRequested

; Uninstalling removes the program only. Profiles and settings in %APPDATA%\PizzaHeroClicker are
; the user's own work and are deliberately left in place.

[Code]
function RelaunchRequested: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;
