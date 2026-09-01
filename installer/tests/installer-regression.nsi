Unicode true

!ifndef OUTPUT_FILE
  !error "OUTPUT_FILE is required"
!endif
!ifndef PAYLOAD_SIZE_KB
  !error "PAYLOAD_SIZE_KB is required"
!endif

!define PRODUCT_NAME "Codex Usage Desktop"
!define PRODUCT_EXE "Codex Usage Desktop.exe"

!include "..\install-directory-validation.nsh"

Name "Codex Usage Desktop installer regression"
OutFile "${OUTPUT_FILE}"
RequestExecutionLevel user
SilentInstall silent

Function AssertSafeInstallDirectory
  Call IsSafeInstallDirectory
  StrCmp "$0" "1" passed
  SetErrorLevel 1
  Quit
  passed:
FunctionEnd

Function AssertUnsafeInstallDirectory
  Call IsSafeInstallDirectory
  StrCmp "$0" "0" passed
  SetErrorLevel 1
  Quit
  passed:
FunctionEnd

Section "Application files" SEC_PROGRAM
  SectionIn RO
  AddSize ${PAYLOAD_SIZE_KB}
SectionEnd

Function .onInit
  IntCmp ${PAYLOAD_SIZE_KB} 1 payload_size_valid payload_size_invalid payload_size_valid

  payload_size_invalid:
    SetErrorLevel 2
    Quit
  payload_size_valid:
    SectionGetSize ${SEC_PROGRAM} $0
    IntCmp $0 ${PAYLOAD_SIZE_KB} section_size_valid section_size_invalid section_size_invalid

  section_size_invalid:
    SetErrorLevel 3
    Quit
  section_size_valid:
    InitPluginsDir

    ; The target does not exist, which models a fresh default installation.
    StrCpy $INSTDIR "$PLUGINSDIR\${PRODUCT_NAME}"
    Call AssertSafeInstallDirectory

    ; An existing dedicated directory must follow the same single-backslash
    ; absolute-path validation as a fresh install.
    CreateDirectory "$INSTDIR"
    IfErrors failed
    Call AssertSafeInstallDirectory

    ${GetRoot} "$PROGRAMFILES64" $1
    StrCpy $INSTDIR "$1\"
    Call AssertUnsafeInstallDirectory

    StrCpy $INSTDIR "$PLUGINSDIR\not-a-dedicated-directory"
    Call AssertUnsafeInstallDirectory

    StrCpy $INSTDIR "${PRODUCT_NAME}"
    Call AssertUnsafeInstallDirectory

    StrCpy $INSTDIR "\\server\share\${PRODUCT_NAME}"
    Call AssertUnsafeInstallDirectory

    StrCpy $INSTDIR "$PLUGINSDIR\legacy-install-location"
    CreateDirectory "$INSTDIR"
    IfErrors failed
    FileOpen $2 "$INSTDIR\${PRODUCT_EXE}" w
    IfErrors failed
    FileClose $2
    Call AssertSafeInstallDirectory

    ; The runner compiles this script once with REGRESSION_CANARY to prove
    ; that a non-zero child exit code is observed instead of being hidden.
    !ifdef REGRESSION_CANARY
      SetErrorLevel 99
      Quit
    !endif

    SetErrorLevel 0
    Quit

  failed:
    SetErrorLevel 4
    Quit
FunctionEnd
