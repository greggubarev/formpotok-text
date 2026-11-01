#ifndef SourceDir
  #error SourceDir must be provided by the build script.
#endif
#ifndef AppVersion
  #error AppVersion must be provided by the build script.
#endif

[Setup]
AppId={{04E4C1D6-3A56-4380-9F8D-8C95C7967F9A}
AppName=ФормПоток: Текст
AppVersion={#AppVersion}
AppPublisher=ФормПоток
DefaultDirName={autopf}\FormPotok Text
DefaultGroupName=ФормПоток
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
OutputBaseFilename=FormPotok.Text-win-x64-v{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\FormPotok.Text.exe
SetupIconFile={#SourceDir}\Assets\formpotok-mark.ico
SetupLogging=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\ФормПоток Текст"; Filename: "{app}\FormPotok.Text.exe"
Name: "{autodesktop}\ФормПоток Текст"; Filename: "{app}\FormPotok.Text.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\FormPotok.Text.exe"; Description: "Запустить ФормПоток: Текст"; Flags: nowait postinstall skipifsilent
