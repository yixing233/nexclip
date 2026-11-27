!macro NSIS_HOOK_POSTUNINSTALL
  ; Preserve startup during updates and remove only this installation's command.
  ${If} $UpdateMode <> 1
    ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "NexClip"
    StrCmp $0 '$\"$INSTDIR\${MAINBINARYNAME}.exe$\" --autostart' 0 +2
      DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "NexClip"
  ${EndIf}
!macroend
