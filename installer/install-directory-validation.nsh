!ifndef PRODUCT_NAME
  !error "PRODUCT_NAME is required before including install-directory-validation.nsh"
!endif
!ifndef PRODUCT_EXE
  !error "PRODUCT_EXE is required before including install-directory-validation.nsh"
!endif

!include "LogicLib.nsh"
!include "FileFunc.nsh"

Function IsSafeInstallDirectory
  StrCpy $0 "0"
  StrCmp "$INSTDIR" "" done

  ${GetRoot} "$INSTDIR" $1
  StrCmp "$1" "" done

  ; Only local drive roots are accepted. Network shares are not dedicated,
  ; all-users installation locations.
  StrLen $2 "$1"
  IntCmp $2 2 local_drive_root done done

  local_drive_root:
    StrCpy $3 "$1" 1 1
    StrCmp "$3" ":" 0 done

  ; GetRoot returns a drive root without its trailing separator. Require that
  ; separator to reject drive-relative paths such as C:folder.
  StrCpy $3 "$INSTDIR" 1 $2
  StrCmp "$3" "\" has_absolute_root
  StrCmp "$3" "/" has_absolute_root
  Goto done

  has_absolute_root:
    ; A root path is never a dedicated installation directory, even if a
    ; stale executable with the product name happens to be present there.
    IntOp $2 $2 + 1
    StrLen $3 "$INSTDIR"
    IntCmp $3 $2 done not_root not_root

  not_root:
    ${GetFileName} "$INSTDIR" $1
    StrCmp "$1" "" done
    StrCmp "$1" "${PRODUCT_NAME}" valid
    ${If} ${FileExists} "$INSTDIR\${PRODUCT_EXE}"
      Goto valid
    ${EndIf}
    Goto done

  valid:
    StrCpy $0 "1"
  done:
FunctionEnd
