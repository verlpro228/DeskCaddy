; DeskCaddy 安装脚本（Inno Setup 6 / 7）
;
; 设计要点：
; 1. 按当前用户安装到 %LocalAppData%\Programs，不弹 UAC
; 2. 用户数据在 %LocalAppData%\DeskCaddy，与安装目录分离，卸载不会碰
; 3. 不开机自启除非用户勾选；卸载时自动清理 Run 键
; 4. 不打包单文件（自解压容易被杀软误判），整目录安装

#define AppName        "DeskCaddy"
; publish.ps1 会通过 /DAppVersion=<csproj 版本> 覆盖；直接编译时用下面的兜底值
#ifndef AppVersion
#define AppVersion     "1.0.2"
#endif
#define AppPublisher   "DeskCaddy"
#define AppExeName     "DeskCaddy.exe"
#define AppUrl         "https://github.com/verlpro228/DeskCaddy"

[Setup]
; 换项目时请重新生成这个 GUID，不要复用
AppId={{8B4F2C41-6E37-4D0A-9B55-3C1F2A7D9E10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\artifacts\installer
OutputBaseFilename={#AppName}-{#AppVersion}-setup
SetupIconFile=..\src\DeskCaddy.App\assets\icon.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\LICENSE
MinVersion=10.0.22621

; 程序运行时会持有这个互斥体；安装/卸载前提示用户先关闭，避免文件被占用
AppMutex=DeskCaddy.SingleInstance

[Languages]
; Default.isl 是英文向导；中文翻译（ChineseSimplified.isl）不是 Inno Setup 自带的
; 标准文件，等安装 ISCC 后再决定是否引入，避免编译时找不到文件。
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked
Name: "startup";     Description: "开机时自动启动 {#AppName}"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; 与程序内「设置 → 常规 → 开机自动启动」写的是同一个键，二者不会冲突
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "DeskCaddy"; ValueData: """{app}\{#AppExeName}"""; \
    Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 结束正在运行的进程，否则卸载时会提示文件占用
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExeName}"; Flags: runhidden skipifdoesntexist; RunOnceId: "KillDeskCaddy"

[Code]
// 卸载前提醒：收纳库与配置不会随卸载删除
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\DeskCaddy');
    if DirExists(DataDir) then
      MsgBox('收纳库与配置文件仍保留在：' + #13#10 + DataDir + #13#10#13#10 +
             '如需彻底清除，请手动删除该目录。' + #13#10 +
             '如需把快捷方式还原到桌面，请先启动程序执行「全部还原到桌面」。',
             mbInformation, MB_OK);
  end;
end;
