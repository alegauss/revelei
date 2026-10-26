; PB35: the per-user installer, after claude-tray's. Built by build\build-installer.cmd, which
; publishes first. Every relative path is resolved against this file's folder (build\).
; Output: artifacts\installer\Revelei-Setup-<version>.exe

#define MyAppName "Revelei"
#define MyAppPublisher "Alexandre Oliveira"
#define MyAppExeName "Revelei.exe"
#define MyPublishDir "..\artifacts\publish"
; Read from the published exe, whose version comes from Directory.Build.props: one place to bump.
#define MyAppVersion GetStringFileInfo(MyPublishDir + "\" + MyAppExeName, PRODUCT_VERSION)

[Setup]
AppId={{08E0D5A3-3E55-4B47-9528-FD1AA1E43DD0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; Per user, no administrator: models download into the user's own folder anyway.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Revelei
DisableProgramGroupPage=yes
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
; The app's own icon on the setup wizard and its .exe (src/Revelei.App/Revelei.ico).
SetupIconFile=..\src\Revelei.App\Revelei.ico
OutputDir=..\artifacts\installer
OutputBaseFilename=Revelei-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; x64 only, as the build is (Directory.Build.props), on Windows 10 1903 (19H1) or later: Windows ML
; (PB43), which every operation runs through, activates its WinRT types reg-free, which 1809 lacks.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.18362
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The single-file exe and the natives ONNX Runtime, DirectML, WPF and SkiaSharp load from beside
; it (the PB5 spike ran DirectML this way). The licenses folder is collect-notices.ps1's, and
; the models folder (PB51) is stage-models.ps1's FP16 set, which the app reads before its own.
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs; \
    Excludes: "*.pdb,DirectML.Debug.dll,*.lib,*.xml"

[InstallDelete]
; Up to 0.3.0 the app was PhotoBoost. The AppId is unchanged, so an upgrade lands in the old
; folder and Start menu group; the old exe and shortcuts would otherwise stay behind, pointing nowhere.
Type: files; Name: "{app}\PhotoBoost.exe"
Type: files; Name: "{group}\PhotoBoost.lnk"
Type: files; Name: "{group}\Uninstall PhotoBoost.lnk"
Type: files; Name: "{group}\Desinstalar PhotoBoost.lnk"
Type: files; Name: "{autodesktop}\PhotoBoost.lnk"

[UninstallDelete]
; PB58: an upgrade from 0.1.0 keeps the GFPGAN, RMBG and ViTMatte it bundled, which this
; installer no longer lists, so uninstalling removes the whole bundled folder rather than leaving them.
Type: filesandordirs; Name: "{app}\models"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
