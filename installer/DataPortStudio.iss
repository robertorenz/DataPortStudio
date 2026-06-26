#define AppName "DataPortStudio"
#define AppVersion "1.0.11"
#define AppPublisher "Reddin Assessments"
#define AppURL "https://github.com/robertorenz/DataPortStudio"
#define AppExeName "DataPortStudio.exe"
#define SrcDir "..\run"

[Setup]
AppId={{F3A7B2D1-9C4E-4F8A-B6D2-1E5C3A7F9B2D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
LicenseFile=
OutputDir=..\installer\output
OutputBaseFilename=DataPortStudio-{#AppVersion}-Setup
SetupIconFile=..\Assets\AppIcon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardSmallImageFile=..\Assets\AppIcon.ico
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Main executable
Source: "{#SrcDir}\DataPortStudio.exe"; DestDir: "{app}"; Flags: ignoreversion

; WPF native DLLs (required alongside the single-file exe)
Source: "{#SrcDir}\D3DCompiler_47_cor3.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\wpfgfx_cor3.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\PresentationNative_cor3.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\PenImc_cor3.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\vcruntime140_cor3.dll"; DestDir: "{app}"; Flags: ignoreversion

; SQLite native
Source: "{#SrcDir}\e_sqlite3.dll"; DestDir: "{app}"; Flags: ignoreversion

; SQL Server SNI
Source: "{#SrcDir}\Microsoft.Data.SqlClient.SNI.dll"; DestDir: "{app}"; Flags: ignoreversion

; WebView2
Source: "{#SrcDir}\WebView2Loader.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\runtimes\win-x64\native\WebView2Loader.dll"; DestDir: "{app}\runtimes\win-x64\native"; Flags: ignoreversion

; XML docs (optional, can be removed if you don't need IntelliSense)
Source: "{#SrcDir}\Microsoft.Web.WebView2.Core.xml"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\Microsoft.Web.WebView2.Wpf.xml"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\Microsoft.Web.WebView2.WinForms.xml"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
