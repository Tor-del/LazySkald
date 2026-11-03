; Reusable Inno Setup 6.7+ entry point.
#ifndef AppConfigPath
  #define AppConfigPath "AppConfig.iss"
#endif
#include AppConfigPath
#if Len(ProductAppId) > 40
  #error ProductAppId must be a compact stable identifier (use a GUID without braces).
#endif
#ifndef AppVersion
  #define AppVersion "0.5.2"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\LazySkald-win-x64-0.5.0"
#endif
#ifndef InstallerOutputDir
  #define InstallerOutputDir "..\artifacts\installers"
#endif

[Setup]
AppId={#ProductAppId}
AppName={#ProductName}
AppVersion={#AppVersion}
AppPublisher={#ProductPublisher}
DefaultDirName={localappdata}\Programs\{#ProductDirectory}
DefaultGroupName={#ProductName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22621
OutputDir={#InstallerOutputDir}
OutputBaseFilename={#ProductDirectory}-{#AppVersion}-Setup-x64
SetupIconFile={#ProductIcon}
UninstallDisplayIcon={app}\{#ProductExe}
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
UsePreviousAppDir=yes
UsePreviousTasks=yes
CloseApplications=yes
RestartApplications=no
SetupMutex={#ProductAppId}.Setup
Uninstallable=yes
UninstallLogMode=new
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#ProductName} Setup
VersionInfoProductName={#ProductName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
english.DesktopShortcut=Create a desktop shortcut
russian.DesktopShortcut=Создать ярлык на рабочем столе
english.LaunchApp=Launch {#ProductName}
russian.LaunchApp=Запустить {#ProductName}
english.CloseAppManual=Please exit {#ProductName} using its tray menu, then try again.
russian.CloseAppManual=Завершите {#ProductName} через меню значка в трее и повторите установку.
english.InvalidUninstaller=The previous version's uninstaller is missing or invalid. Remove the previous installation manually before continuing.
russian.InvalidUninstaller=Деинсталлятор прежней версии отсутствует или некорректен. Удалите прежнюю установленную версию вручную.
english.UninstallFailed=The previous version could not be removed. Setup stopped before installing new files. Your settings were preserved.
russian.UninstallFailed=Не удалось удалить прежнюю версию. Новые файлы не установлены. Настройки пользователя сохранены.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#ProductName}\{#ProductName}"; Filename: "{app}\{#ProductExe}"; Check: not WizardNoIcons
Name: "{autodesktop}\{#ProductName}"; Filename: "{app}\{#ProductExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#ProductExe}"; Parameters: "--ui-language={code:ApplicationLanguage}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
#include "Upgrade.iss"
