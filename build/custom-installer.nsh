!include "LogicLib.nsh"
!include "nsDialogs.nsh"

!ifndef BUILD_UNINSTALLER
  Var StartAtLoginCheckbox
  Var StartAtLoginEnabled

  !macro customPageAfterChangeDir
    Page custom StartAtLoginPageCreate StartAtLoginPageLeave
  !macroend

  !macro customHeader
    Function StartAtLoginPageCreate
    ${If} $installMode == "all"
      SetShellVarContext current
    ${EndIf}
    nsDialogs::Create 1018
    Pop $0
    ${If} $0 == error
      ${If} $installMode == "all"
        SetShellVarContext all
      ${EndIf}
      Abort
    ${EndIf}
    ${NSD_CreateLabel} 0 0 100% 24u "选择是否在登录 Windows 时自动启动 Codex Usage Desktop。"
    Pop $0
    ${NSD_CreateCheckbox} 0 34u 100% 12u "登录 Windows 时自动启动 Codex Usage Desktop"
    Pop $StartAtLoginCheckbox
    ${If} ${FileExists} "$APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\Codex Usage Desktop.lnk"
      ${NSD_SetState} $StartAtLoginCheckbox ${BST_CHECKED}
    ${Else}
      ${NSD_SetState} $StartAtLoginCheckbox ${BST_UNCHECKED}
    ${EndIf}
    ${If} $installMode == "all"
      SetShellVarContext all
    ${EndIf}
    nsDialogs::Show
    FunctionEnd

    Function StartAtLoginPageLeave
    ${NSD_GetState} $StartAtLoginCheckbox $0
    ${If} $0 == ${BST_CHECKED}
      StrCpy $StartAtLoginEnabled "1"
    ${Else}
      StrCpy $StartAtLoginEnabled "0"
    ${EndIf}
    FunctionEnd
  !macroend

  !macro customInstall
    ${If} $installMode == "all"
      SetShellVarContext current
    ${EndIf}
    ${If} $StartAtLoginEnabled == "1"
      CreateShortCut "$APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\Codex Usage Desktop.lnk" "$INSTDIR\${APP_EXECUTABLE_FILENAME}" "--startup"
    ${ElseIf} $StartAtLoginEnabled == "0"
      Delete "$APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\Codex Usage Desktop.lnk"
    ${ElseIf} ${FileExists} "$APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\Codex Usage Desktop.lnk"
      CreateShortCut "$APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\Codex Usage Desktop.lnk" "$INSTDIR\${APP_EXECUTABLE_FILENAME}" "--startup"
    ${EndIf}
    ${If} $installMode == "all"
      SetShellVarContext all
    ${EndIf}
  !macroend
!endif

!macro customUnInstall
  ${If} $installMode == "all"
    SetShellVarContext current
  ${EndIf}
  Delete "$APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\Codex Usage Desktop.lnk"
  ${If} $installMode == "all"
    SetShellVarContext all
  ${EndIf}
!macroend

!macro customUnInstallSection
  Section /o "un.删除 Codex Usage Desktop 配置和 usage ledger"
    ${If} $installMode == "all"
      SetShellVarContext current
    ${EndIf}
    RMDir /r "$LOCALAPPDATA\Codex Usage Desktop"
    RMDir /r "$APPDATA\codex-usage-desktop"
    ${If} $installMode == "all"
      SetShellVarContext all
    ${EndIf}
  SectionEnd
!macroend
