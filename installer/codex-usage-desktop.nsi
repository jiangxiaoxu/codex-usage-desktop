Unicode true

!ifndef PUBLISH_DIR
  !error "PUBLISH_DIR is required"
!endif
!ifndef OUTPUT_FILE
  !error "OUTPUT_FILE is required"
!endif
!ifndef UNINSTALL_FILES_INCLUDE
  !error "UNINSTALL_FILES_INCLUDE is required"
!endif
!ifndef LICENSE_FILE
  !error "LICENSE_FILE is required"
!endif
!ifndef APP_ICON_FILE
  !error "APP_ICON_FILE is required"
!endif
!ifndef PRODUCT_VERSION
  !define PRODUCT_VERSION "0.3.1"
!endif
!ifndef PRODUCT_FILE_VERSION
  !define PRODUCT_FILE_VERSION "0.3.1.0"
!endif

!define PRODUCT_NAME "Codex Usage Desktop"
!define PRODUCT_PUBLISHER "jiangxiaoxu"
!define PRODUCT_EXE "Codex Usage Desktop.exe"
!define LEGACY_EXE "Codex Usage Desktop.exe"
!define UNINSTALL_EXE "Uninstall Codex Usage Desktop.exe"
!define UNINSTALL_ID "84c6521f-e257-5d83-93e2-0f5e984c4280"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${UNINSTALL_ID}"
!define MUI_ICON "${APP_ICON_FILE}"
!define MUI_UNICON "${APP_ICON_FILE}"

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "WordFunc.nsh"
!include "StrFunc.nsh"
!include "nsDialogs.nsh"
!include "Sections.nsh"
!include "x64.nsh"

!insertmacro VersionCompare
${StrStr}
${UnStrStr}

Name "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "${OUTPUT_FILE}"
InstallDir "$PROGRAMFILES64\${PRODUCT_NAME}"
InstallDirRegKey HKLM "${UNINSTALL_KEY}" "InstallLocation"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
SetCompressorDictSize 64
CRCCheck on
ShowInstDetails show
ShowUninstDetails show
ManifestDPIAware true
ManifestSupportedOS Win10

VIProductVersion "${PRODUCT_FILE_VERSION}"
VIAddVersionKey /LANG=2052 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=2052 "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=2052 "FileDescription" "${PRODUCT_NAME} Setup"
VIAddVersionKey /LANG=2052 "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "Copyright (c) 2026 ${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=1033 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=1033 "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=1033 "FileDescription" "${PRODUCT_NAME} Setup"
VIAddVersionKey /LANG=1033 "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Copyright (c) 2026 ${PRODUCT_PUBLISHER}"

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${PRODUCT_EXE}"
!define MUI_FINISHPAGE_RUN_NOTCHECKED

!insertmacro MUI_PAGE_LICENSE "${LICENSE_FILE}"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
Page custom StartupPageCreate StartupPageLeave
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

LangString SectionProgram ${LANG_SIMPCHINESE} "程序文件(必需)"
LangString SectionProgram ${LANG_ENGLISH} "Application files (required)"
LangString SectionDesktop ${LANG_SIMPCHINESE} "桌面快捷方式"
LangString SectionDesktop ${LANG_ENGLISH} "Desktop shortcut"
LangString SectionStartMenu ${LANG_SIMPCHINESE} "开始菜单快捷方式"
LangString SectionStartMenu ${LANG_ENGLISH} "Start menu shortcut"
LangString SameVersionPrompt ${LANG_SIMPCHINESE} "版本 ${PRODUCT_VERSION} 已安装。是否继续修复安装？"
LangString SameVersionPrompt ${LANG_ENGLISH} "Version ${PRODUCT_VERSION} is already installed. Continue with a repair install?"
LangString DowngradeBlocked ${LANG_SIMPCHINESE} "检测到较新的版本 $InstalledVersion。为保护数据，不能降级到 ${PRODUCT_VERSION}。"
LangString DowngradeBlocked ${LANG_ENGLISH} "A newer version ($InstalledVersion) is installed. Downgrade to ${PRODUCT_VERSION} is blocked to protect your data."
LangString UnsafeInstallDir ${LANG_SIMPCHINESE} "安装目录不安全。请选择以 '${PRODUCT_NAME}' 结尾的专用目录。"
LangString UnsafeInstallDir ${LANG_ENGLISH} "The install directory is unsafe. Choose a dedicated directory ending in '${PRODUCT_NAME}'."
LangString BackupFailed ${LANG_SIMPCHINESE} "无法备份 usage ledger，安装已中止。请检查 %LOCALAPPDATA% 的磁盘空间和权限。"
LangString BackupFailed ${LANG_ENGLISH} "The usage ledger could not be backed up. Setup was aborted. Check free space and permissions under %LOCALAPPDATA%."
LangString ProfileMismatch ${LANG_SIMPCHINESE} "当前用户 profile、HKCU 和 LocalAppData 不一致。安装已中止，以避免操作错误账户的数据。"
LangString ProfileMismatch ${LANG_ENGLISH} "The current user profile, HKCU, and LocalAppData do not agree. Setup was aborted to avoid changing another account's data."
LangString ProcessCheckFailed ${LANG_SIMPCHINESE} "无法可靠关闭或确认 Codex Usage Desktop 进程已经退出。安装已中止。"
LangString ProcessCheckFailed ${LANG_ENGLISH} "Codex Usage Desktop could not be closed or verified as stopped. Setup was aborted."
LangString RemoveFailed ${LANG_SIMPCHINESE} "无法删除已知的旧程序文件。安装已中止，用户数据未删除。"
LangString RemoveFailed ${LANG_ENGLISH} "Known old application files could not be removed. Setup was aborted without deleting user data."
LangString LegacyUninstallFailed ${LANG_SIMPCHINESE} "旧 Electron 版本卸载失败。安装已中止，且不会继续覆盖程序文件。"
LangString LegacyUninstallFailed ${LANG_ENGLISH} "The legacy Electron version could not be uninstalled. Setup was aborted without replacing application files."
LangString LedgerRestoreFailed ${LANG_SIMPCHINESE} "旧版本卸载后无法恢复 usage ledger。安装已中止；备份仍保留在 LocalAppData。"
LangString LedgerRestoreFailed ${LANG_ENGLISH} "The usage ledger could not be restored after uninstalling the legacy version. Setup was aborted; the backup remains under LocalAppData."
LangString DeployFailed ${LANG_SIMPCHINESE} "无法替换程序文件。安装已中止；usage ledger 备份仍保留在 LocalAppData。"
LangString DeployFailed ${LANG_ENGLISH} "Application files could not be replaced. Setup was aborted; the usage ledger backup remains under LocalAppData."
LangString RegistrationFailed ${LANG_SIMPCHINESE} "无法写入安装注册信息。安装已中止。"
LangString RegistrationFailed ${LANG_ENGLISH} "Installation registration could not be written. Setup was aborted."
LangString SilentAdminOptInRequired ${LANG_SIMPCHINESE} "静默安装必须显式传入 /CURRENTADMIN=1，以确认 HKCU 和 LocalAppData 属于目标管理员账户。"
LangString SilentAdminOptInRequired ${LANG_ENGLISH} "Silent setup requires /CURRENTADMIN=1 to confirm that HKCU and LocalAppData belong to the target administrator account."
LangString StartupTitle ${LANG_SIMPCHINESE} "开机自启动"
LangString StartupTitle ${LANG_ENGLISH} "Start at sign-in"
LangString StartupDescription ${LANG_SIMPCHINESE} "选择是否在登录 Windows 时自动启动 Codex Usage Desktop。升级时会保留旧版选择。"
LangString StartupDescription ${LANG_ENGLISH} "Choose whether Codex Usage Desktop starts when you sign in to Windows. Upgrades preserve the previous choice."
LangString StartupCheckboxLabel ${LANG_SIMPCHINESE} "登录 Windows 时自动启动 Codex Usage Desktop"
LangString StartupCheckboxLabel ${LANG_ENGLISH} "Start Codex Usage Desktop when I sign in to Windows"

Var InstalledVersion
Var AppRunning
Var BackupPath
Var StartupCheckbox
Var StartupRequested
Var LegacyStartupDetected
Var ExistingStartupRun
Var OldDisplayVersion
Var OldInstallLocation
Var OldDisplayIcon
Var CurrentAdminOptIn
Var LegacyInstallDetected
Var HadDesktopShortcut
Var HadStartMenuShortcut

Function .onInit
  SetRegView 64
  SetShellVarContext current
  StrCpy $StartupRequested "0"
  StrCpy $LegacyStartupDetected "0"
  StrCpy $ExistingStartupRun ""
  StrCpy $CurrentAdminOptIn ""
  StrCpy $LegacyInstallDetected "0"
  StrCpy $HadDesktopShortcut "0"
  StrCpy $HadStartMenuShortcut "0"
  ${GetParameters} $0
  ${GetOptions} "$0" "/CURRENTADMIN=" $CurrentAdminOptIn
  StrCpy $LANGUAGE ${LANG_SIMPCHINESE}
  !insertmacro MUI_LANGDLL_DISPLAY

  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "${PRODUCT_NAME} ${PRODUCT_VERSION} requires 64-bit Windows." /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}

  Call ValidateCurrentAdministratorProfile
  IfSilent silent_admin_check current_admin_confirmed
  silent_admin_check:
    StrCmp $CurrentAdminOptIn "1" current_admin_confirmed
    DetailPrint "$(SilentAdminOptInRequired)"
    SetErrorLevel 5
    Abort
  current_admin_confirmed:

  SetShellVarContext all
  ${If} ${FileExists} "$DESKTOP\${PRODUCT_NAME}.lnk"
    StrCpy $HadDesktopShortcut "1"
  ${EndIf}
  ${If} ${FileExists} "$SMPROGRAMS\${PRODUCT_NAME}.lnk"
    StrCpy $HadStartMenuShortcut "1"
  ${EndIf}
  SetShellVarContext current
  ${If} ${FileExists} "$APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\${PRODUCT_NAME}.lnk"
    StrCpy $LegacyStartupDetected "1"
    StrCpy $StartupRequested "1"
  ${EndIf}
  ReadRegStr $ExistingStartupRun HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${PRODUCT_NAME}"
  ${If} $ExistingStartupRun != ""
    StrCpy $StartupRequested "1"
  ${EndIf}
  SetShellVarContext all

  ReadRegStr $OldDisplayVersion HKLM "${UNINSTALL_KEY}" "DisplayVersion"
  ReadRegStr $OldInstallLocation HKLM "${UNINSTALL_KEY}" "InstallLocation"
  ReadRegStr $OldDisplayIcon HKLM "${UNINSTALL_KEY}" "DisplayIcon"
  StrCpy $InstalledVersion $OldDisplayVersion
  StrCpy $0 $InstalledVersion 4
  ${If} $0 == "0.2."
    StrCpy $LegacyInstallDetected "1"
  ${EndIf}

  ${If} $InstalledVersion != ""
    ${VersionCompare} "$InstalledVersion" "${PRODUCT_VERSION}" $0
    ${If} $0 == "1"
      MessageBox MB_ICONSTOP "$(DowngradeBlocked)" /SD IDOK
      SetErrorLevel 3
      Abort
    ${ElseIf} $0 == "0"
      IfSilent same_version_continue
      MessageBox MB_ICONQUESTION|MB_YESNO "$(SameVersionPrompt)" /SD IDNO IDYES same_version_continue
      SetErrorLevel 4
      Abort
      same_version_continue:
    ${EndIf}
  ${EndIf}

  ${If} $OldInstallLocation != ""
    StrCpy $INSTDIR $OldInstallLocation
  ${Else}
    ${If} $OldDisplayIcon != ""
      ${GetParent} "$OldDisplayIcon" $1
      ${If} ${FileExists} "$1\${LEGACY_EXE}"
        StrCpy $INSTDIR $1
      ${EndIf}
    ${EndIf}
  ${EndIf}
  ${If} ${FileExists} "$INSTDIR\resources\app.asar"
    StrCpy $LegacyInstallDetected "1"
  ${EndIf}
FunctionEnd

; RequestExecutionLevel elevation happens before .onInit. Without an additional UAC
; broker plugin, an over-the-shoulder credential flow cannot be mapped back to the
; initiating standard user. This installer therefore supports only the current
; administrator account and refuses internally inconsistent profile/HKCU state.
Function ValidateCurrentAdministratorProfile
  UserInfo::GetAccountType
  Pop $0
  StrCmp $0 "Admin" profile_environment
  Goto profile_mismatch

  profile_environment:
  ExpandEnvStrings $0 "%USERPROFILE%"
  StrCmp $0 "" profile_mismatch
  StrCmp $0 "%USERPROFILE%" profile_mismatch
  GetFullPathName $0 "$0"
  StrCmp $0 "" profile_mismatch

  ClearErrors
  ReadRegStr $1 HKCU "Volatile Environment" "USERPROFILE"
  IfErrors profile_mismatch
  StrCmp $1 "" profile_mismatch
  GetFullPathName $1 "$1"
  StrCmp $1 "" profile_mismatch
  StrCmp $0 $1 +2
  Goto profile_mismatch

  ${GetParent} "$LOCALAPPDATA" $2
  ${GetParent} "$2" $2
  StrCmp $2 "" profile_mismatch
  GetFullPathName $2 "$2"
  StrCmp $2 "" profile_mismatch
  StrCmp $0 $2 +2
  Goto profile_mismatch
  Return

  profile_mismatch:
    MessageBox MB_ICONSTOP "$(ProfileMismatch)" /SD IDOK
    SetErrorLevel 6
    Abort
FunctionEnd

Function un.onInit
  SetRegView 64
  SetShellVarContext current
  StrCpy $LANGUAGE ${LANG_SIMPCHINESE}
  ClearErrors
  ReadRegDWORD $0 HKLM "${UNINSTALL_KEY}" "InstallerLanguage"
  ${IfNot} ${Errors}
    StrCpy $LANGUAGE $0
  ${EndIf}
  Call un.ValidateCurrentAdministratorProfile
  SetShellVarContext all
FunctionEnd

Function un.ValidateCurrentAdministratorProfile
  UserInfo::GetAccountType
  Pop $0
  StrCmp $0 "Admin" profile_environment
  Goto profile_mismatch

  profile_environment:
  ExpandEnvStrings $0 "%USERPROFILE%"
  StrCmp $0 "" profile_mismatch
  StrCmp $0 "%USERPROFILE%" profile_mismatch
  GetFullPathName $0 "$0"
  StrCmp $0 "" profile_mismatch

  ClearErrors
  ReadRegStr $1 HKCU "Volatile Environment" "USERPROFILE"
  IfErrors profile_mismatch
  StrCmp $1 "" profile_mismatch
  GetFullPathName $1 "$1"
  StrCmp $1 "" profile_mismatch
  StrCmp $0 $1 +2
  Goto profile_mismatch

  ${un.GetParent} "$LOCALAPPDATA" $2
  ${un.GetParent} "$2" $2
  StrCmp $2 "" profile_mismatch
  GetFullPathName $2 "$2"
  StrCmp $2 "" profile_mismatch
  StrCmp $0 $2 +2
  Goto profile_mismatch
  Return

  profile_mismatch:
    MessageBox MB_ICONSTOP "$(ProfileMismatch)" /SD IDOK
    SetErrorLevel 6
    Abort
FunctionEnd

Function StartupPageCreate
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}

  !insertmacro MUI_HEADER_TEXT "$(StartupTitle)" "$(StartupDescription)"
  ${NSD_CreateLabel} 0 0 100% 28u "$(StartupDescription)"
  Pop $0
  ${NSD_CreateCheckbox} 0 40u 100% 16u "$(StartupCheckboxLabel)"
  Pop $StartupCheckbox
  ${If} $StartupRequested == "1"
    ${NSD_SetState} $StartupCheckbox ${BST_CHECKED}
  ${Else}
    ${NSD_SetState} $StartupCheckbox ${BST_UNCHECKED}
  ${EndIf}
  nsDialogs::Show
FunctionEnd

Function StartupPageLeave
  ${NSD_GetState} $StartupCheckbox $0
  ${If} $0 == ${BST_CHECKED}
    StrCpy $StartupRequested "1"
  ${Else}
    StrCpy $StartupRequested "0"
  ${EndIf}
FunctionEnd

Function CheckAppProcesses
  StrCpy $AppRunning "0"

  nsExec::ExecToStack '"$SYSDIR\tasklist.exe" /NH /FI "IMAGENAME eq ${PRODUCT_EXE}"'
  Pop $0
  Pop $1
  ${If} $0 != "0"
    StrCpy $AppRunning "2"
    Return
  ${EndIf}
  ${StrStr} $2 $1 "${PRODUCT_EXE}"
  ${If} $2 != ""
    StrCpy $AppRunning "1"
  ${EndIf}
FunctionEnd

Function EnsureAppClosed
  Call CheckAppProcesses
  ${If} $AppRunning == "0"
    Return
  ${ElseIf} $AppRunning == "2"
    Goto close_failed
  ${EndIf}

  DetailPrint "Stopping ${PRODUCT_NAME} before replacing program files."
  nsExec::ExecToStack '"$SYSDIR\taskkill.exe" /T /F /IM "${PRODUCT_EXE}"'
  Pop $0
  Pop $1
  ${If} $0 != "0"
    Goto close_failed
  ${EndIf}

  StrCpy $3 0
  close_wait:
    Sleep 250
    Call CheckAppProcesses
    ${If} $AppRunning == "0"
      Return
    ${EndIf}
    ${If} $AppRunning == "2"
      Goto close_failed
    ${EndIf}
    IntOp $3 $3 + 1
    IntCmp $3 20 close_failed close_wait close_failed

  close_failed:
    MessageBox MB_ICONSTOP "$(ProcessCheckFailed)" /SD IDOK
    SetErrorLevel 13
    Quit
FunctionEnd

Function ValidateInstallDirectory
  GetFullPathName $INSTDIR "$INSTDIR"
  ${GetRoot} "$INSTDIR" $0
  StrCmp "$INSTDIR" "$0" invalid

  ${GetFileName} "$INSTDIR" $1
  StrCmp "$1" "${PRODUCT_NAME}" valid
  ${If} ${FileExists} "$INSTDIR\${LEGACY_EXE}"
    Goto valid
  ${EndIf}
  ${If} ${FileExists} "$INSTDIR\${PRODUCT_EXE}"
    Goto valid
  ${EndIf}

  invalid:
    MessageBox MB_ICONSTOP "$(UnsafeInstallDir)" /SD IDOK
    SetErrorLevel 11
    Quit
  valid:
FunctionEnd

Function BackupLedger
  StrCpy $BackupPath ""
  ${IfNot} ${FileExists} "$LOCALAPPDATA\${PRODUCT_NAME}\usage.sqlite"
    Return
  ${EndIf}

  ${GetTime} "" "L" $0 $1 $2 $3 $4 $5 $6
  ; Keep the backup outside the legacy data directory because the Electron
  ; uninstaller may remove that entire directory.
  StrCpy $BackupPath "$LOCALAPPDATA\${PRODUCT_NAME} Ledger Backups\preinstall-${PRODUCT_VERSION}-$2$1$0-$4$5$6"
  CreateDirectory "$BackupPath"
  IfErrors backup_failed

  ClearErrors
  CopyFiles /SILENT "$LOCALAPPDATA\${PRODUCT_NAME}\usage.sqlite" "$BackupPath\usage.sqlite"
  IfErrors backup_failed
  IfFileExists "$BackupPath\usage.sqlite" +2 backup_failed

  ${If} ${FileExists} "$LOCALAPPDATA\${PRODUCT_NAME}\usage.sqlite-wal"
    ClearErrors
    CopyFiles /SILENT "$LOCALAPPDATA\${PRODUCT_NAME}\usage.sqlite-wal" "$BackupPath\usage.sqlite-wal"
    IfErrors backup_failed
    IfFileExists "$BackupPath\usage.sqlite-wal" +2 backup_failed
  ${EndIf}

  ${If} ${FileExists} "$LOCALAPPDATA\${PRODUCT_NAME}\usage.sqlite-shm"
    ClearErrors
    CopyFiles /SILENT "$LOCALAPPDATA\${PRODUCT_NAME}\usage.sqlite-shm" "$BackupPath\usage.sqlite-shm"
    IfErrors backup_failed
    IfFileExists "$BackupPath\usage.sqlite-shm" +2 backup_failed
  ${EndIf}
  DetailPrint "Ledger backup: $BackupPath"
  Return

  backup_failed:
    MessageBox MB_ICONSTOP "$(BackupFailed)" /SD IDOK
    SetErrorLevel 12
    Quit
FunctionEnd

Function RestoreLedgerAfterLegacyUninstall
  ${If} $BackupPath == ""
    Return
  ${EndIf}
  CreateDirectory "$LOCALAPPDATA\${PRODUCT_NAME}"
  IfErrors ledger_restore_failed

  ${IfNot} ${FileExists} "$LOCALAPPDATA\${PRODUCT_NAME}\usage.sqlite"
    ClearErrors
    CopyFiles /SILENT "$BackupPath\usage.sqlite" "$LOCALAPPDATA\${PRODUCT_NAME}\usage.sqlite"
    IfErrors ledger_restore_failed
    IfFileExists "$LOCALAPPDATA\${PRODUCT_NAME}\usage.sqlite" +2 ledger_restore_failed
  ${EndIf}
  ${If} ${FileExists} "$BackupPath\usage.sqlite-wal"
  ${AndIfNot} ${FileExists} "$LOCALAPPDATA\${PRODUCT_NAME}\usage.sqlite-wal"
    ClearErrors
    CopyFiles /SILENT "$BackupPath\usage.sqlite-wal" "$LOCALAPPDATA\${PRODUCT_NAME}\usage.sqlite-wal"
    IfErrors ledger_restore_failed
    IfFileExists "$LOCALAPPDATA\${PRODUCT_NAME}\usage.sqlite-wal" +2 ledger_restore_failed
  ${EndIf}
  ${If} ${FileExists} "$BackupPath\usage.sqlite-shm"
  ${AndIfNot} ${FileExists} "$LOCALAPPDATA\${PRODUCT_NAME}\usage.sqlite-shm"
    ClearErrors
    CopyFiles /SILENT "$BackupPath\usage.sqlite-shm" "$LOCALAPPDATA\${PRODUCT_NAME}\usage.sqlite-shm"
    IfErrors ledger_restore_failed
    IfFileExists "$LOCALAPPDATA\${PRODUCT_NAME}\usage.sqlite-shm" +2 ledger_restore_failed
  ${EndIf}
  Return

  ledger_restore_failed:
    MessageBox MB_ICONSTOP "$(LedgerRestoreFailed)" /SD IDOK
    SetErrorLevel 22
    Quit
FunctionEnd

Function UninstallLegacyElectron
  ${If} $LegacyInstallDetected != "1"
    Return
  ${EndIf}
  IfFileExists "$INSTDIR\${UNINSTALL_EXE}" 0 legacy_uninstall_failed
  InitPluginsDir
  ClearErrors
  CopyFiles /SILENT "$INSTDIR\${UNINSTALL_EXE}" "$PLUGINSDIR\${UNINSTALL_EXE}"
  IfErrors legacy_uninstall_failed
  IfFileExists "$PLUGINSDIR\${UNINSTALL_EXE}" 0 legacy_uninstall_failed
  DetailPrint "Removing the legacy Electron installation."
  nsExec::ExecToStack '"$PLUGINSDIR\${UNINSTALL_EXE}" /S /allusers _?=$INSTDIR'
  Pop $0
  Pop $1
  ${If} $0 != "0"
    Goto legacy_uninstall_failed
  ${EndIf}
  Call RestoreLedgerAfterLegacyUninstall
  Return

  legacy_uninstall_failed:
    MessageBox MB_ICONSTOP "$(LegacyUninstallFailed)" /SD IDOK
    SetErrorLevel 21
    Quit
FunctionEnd

Function RemoveInstalledPayload
  ; Remove only known application payload. User data is stored under LocalAppData.
  ClearErrors
  !include "${UNINSTALL_FILES_INCLUDE}"

  ; Electron 0.2.x payload whitelist for the breaking WinUI replacement.
  Delete "$INSTDIR\chrome_100_percent.pak"
  Delete "$INSTDIR\chrome_200_percent.pak"
  Delete "$INSTDIR\d3dcompiler_47.dll"
  Delete "$INSTDIR\ffmpeg.dll"
  Delete "$INSTDIR\icudtl.dat"
  Delete "$INSTDIR\libEGL.dll"
  Delete "$INSTDIR\libGLESv2.dll"
  Delete "$INSTDIR\LICENSE.electron.txt"
  Delete "$INSTDIR\LICENSES.chromium.html"
  Delete "$INSTDIR\resources.pak"
  Delete "$INSTDIR\snapshot_blob.bin"
  Delete "$INSTDIR\v8_context_snapshot.bin"
  Delete "$INSTDIR\version"
  Delete "$INSTDIR\vk_swiftshader.dll"
  Delete "$INSTDIR\vk_swiftshader_icd.json"
  Delete "$INSTDIR\vulkan-1.dll"
  RMDir /r "$INSTDIR\locales"
  RMDir /r "$INSTDIR\resources"
  RMDir /r "$INSTDIR\swiftshader"
  Delete "$INSTDIR\${UNINSTALL_EXE}"
  IfErrors remove_failed
  Return

  remove_failed:
    MessageBox MB_ICONSTOP "$(RemoveFailed)" /SD IDOK
    SetErrorLevel 14
    Quit
FunctionEnd

Function DeployPayload
  CreateDirectory "$INSTDIR"
  ClearErrors
  SetOutPath "$INSTDIR"
  File /r "${PUBLISH_DIR}\*.*"
  IfErrors deploy_failed
  ClearErrors
  WriteUninstaller "$INSTDIR\${UNINSTALL_EXE}"
  IfErrors deploy_failed
  IfFileExists "$INSTDIR\${PRODUCT_EXE}" 0 deploy_failed
  Return

  deploy_failed:
    MessageBox MB_ICONSTOP "$(DeployFailed)" /SD IDOK
    SetErrorLevel 15
    Quit
FunctionEnd

Function RegisterActivatedPayload
  ClearErrors
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT_NAME} ${PRODUCT_VERSION}"
  IfErrors registration_failed
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  IfErrors registration_failed
  WriteRegStr HKLM "${UNINSTALL_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  IfErrors registration_failed
  WriteRegStr HKLM "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  IfErrors registration_failed
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${PRODUCT_EXE},0"
  IfErrors registration_failed
  WriteRegStr HKLM "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\${UNINSTALL_EXE}$\"'
  IfErrors registration_failed
  WriteRegStr HKLM "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\${UNINSTALL_EXE}$\" /S'
  IfErrors registration_failed
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoModify" 1
  IfErrors registration_failed
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoRepair" 1
  IfErrors registration_failed
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "InstallerLanguage" "$LANGUAGE"
  IfErrors registration_failed
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "EstimatedSize" "$0"
  IfErrors registration_failed
  Return

  registration_failed:
    MessageBox MB_ICONSTOP "$(RegistrationFailed)" /SD IDOK
    SetErrorLevel 18
    Quit
FunctionEnd

Section "$(SectionProgram)" SEC_PROGRAM
  SectionIn RO
  SetRegView 64
  SetShellVarContext all
  Call ValidateInstallDirectory
  Call EnsureAppClosed
  SetShellVarContext current
  Call BackupLedger
  Call UninstallLegacyElectron
  SetShellVarContext all
  Call RemoveInstalledPayload
  Call DeployPayload
  Call RegisterActivatedPayload
  Call ApplySelectedShellState
SectionEnd

Section "$(SectionStartMenu)" SEC_START_MENU
SectionEnd

Section "$(SectionDesktop)" SEC_DESKTOP
SectionEnd

Function ApplySelectedShellState
  SetShellVarContext all
  SectionGetFlags ${SEC_START_MENU} $0
  IntOp $0 $0 & ${SF_SELECTED}
  ${If} $0 != "0"
    ClearErrors
    CreateShortCut "$SMPROGRAMS\${PRODUCT_NAME}.lnk" "$INSTDIR\${PRODUCT_EXE}"
    IfErrors shell_state_failed
  ${ElseIf} $HadStartMenuShortcut == "1"
    ClearErrors
    Delete "$SMPROGRAMS\${PRODUCT_NAME}.lnk"
    IfErrors shell_state_failed
  ${EndIf}

  SectionGetFlags ${SEC_DESKTOP} $0
  IntOp $0 $0 & ${SF_SELECTED}
  ${If} $0 != "0"
    ClearErrors
    CreateShortCut "$DESKTOP\${PRODUCT_NAME}.lnk" "$INSTDIR\${PRODUCT_EXE}"
    IfErrors shell_state_failed
  ${ElseIf} $HadDesktopShortcut == "1"
    ClearErrors
    Delete "$DESKTOP\${PRODUCT_NAME}.lnk"
    IfErrors shell_state_failed
  ${EndIf}

  SetShellVarContext current
  ${If} $LegacyStartupDetected == "1"
    ClearErrors
    Delete "$APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\${PRODUCT_NAME}.lnk"
    IfErrors shell_state_failed
  ${EndIf}
  ${If} $StartupRequested == "1"
    ClearErrors
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${PRODUCT_NAME}" '$\"$INSTDIR\${PRODUCT_EXE}$\" --startup'
    IfErrors shell_state_failed
  ${ElseIf} $ExistingStartupRun != ""
    ClearErrors
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${PRODUCT_NAME}"
    IfErrors shell_state_failed
  ${EndIf}
  SetShellVarContext all
  Return

  shell_state_failed:
    SetShellVarContext all
    SetErrorLevel 20
    Quit
FunctionEnd

Function un.CheckAppProcesses
  StrCpy $AppRunning "0"
  nsExec::ExecToStack '"$SYSDIR\tasklist.exe" /NH /FI "IMAGENAME eq ${PRODUCT_EXE}"'
  Pop $0
  Pop $1
  ${If} $0 != "0"
    StrCpy $AppRunning "2"
    Return
  ${EndIf}
  ${UnStrStr} $2 $1 "${PRODUCT_EXE}"
  ${If} $2 != ""
    StrCpy $AppRunning "1"
    Return
  ${EndIf}
FunctionEnd

Function un.EnsureAppClosed
  Call un.CheckAppProcesses
  ${If} $AppRunning == "0"
    Return
  ${ElseIf} $AppRunning == "2"
    Goto close_failed
  ${EndIf}
  nsExec::ExecToStack '"$SYSDIR\taskkill.exe" /T /F /IM "${PRODUCT_EXE}"'
  Pop $0
  Pop $1
  ${If} $0 != "0"
    Goto close_failed
  ${EndIf}
  StrCpy $3 0
  close_wait:
    Sleep 250
    Call un.CheckAppProcesses
    ${If} $AppRunning == "0"
      Return
    ${EndIf}
    ${If} $AppRunning == "2"
      Goto close_failed
    ${EndIf}
    IntOp $3 $3 + 1
    IntCmp $3 20 close_failed close_wait close_failed
  close_failed:
    MessageBox MB_ICONSTOP "$(ProcessCheckFailed)" /SD IDOK
    SetErrorLevel 13
    Quit
FunctionEnd

Section "Uninstall"
  SetRegView 64
  SetShellVarContext all
  Call un.EnsureAppClosed

  Delete "$DESKTOP\${PRODUCT_NAME}.lnk"
  Delete "$SMPROGRAMS\${PRODUCT_NAME}.lnk"
  SetShellVarContext current
  Delete "$APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\${PRODUCT_NAME}.lnk"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${PRODUCT_NAME}"
  SetShellVarContext all

  !include "${UNINSTALL_FILES_INCLUDE}"
  Delete "$INSTDIR\${UNINSTALL_EXE}"
  RMDir "$INSTDIR"
  DeleteRegKey HKLM "${UNINSTALL_KEY}"
SectionEnd
