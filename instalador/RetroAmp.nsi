; Instalador de RetroAmp (NSIS 3)
Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 64
RequestExecutionLevel user
ManifestDPIAware true

!define APP      "RetroAmp"
!define VERSION  "1.1.0"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\RetroAmp"
!define RUNKEY   "Software\Microsoft\Windows\CurrentVersion\Run"

Name "${APP}"
Caption "Instalar ${APP} ${VERSION}"
OutFile "RetroAmp-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\RetroAmp"
InstallDirRegKey HKCU "Software\RetroAmp" "InstallDir"
BrandingText "RetroAmp ${VERSION}"

VIProductVersion "1.1.0.0"
VIAddVersionKey /LANG=1034 "ProductName" "RetroAmp"
VIAddVersionKey /LANG=1034 "FileDescription" "Instalador de RetroAmp"
VIAddVersionKey /LANG=1034 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1034 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1034 "LegalCopyright" "RetroAmp"
VIAddVersionKey /LANG=1034 "CompanyName" "Elar Castro"
VIAddVersionKey /LANG=1034 "Comments" "github.com/ElarCastro/RetroAmp"

!include "MUI2.nsh"
!include "FileFunc.nsh"

!define MUI_ICON   "app\RetroAmp.ico"
!define MUI_UNICON "app\RetroAmp.ico"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_SMALLDESC

!define MUI_WELCOMEPAGE_TITLE "Bienvenido a RetroAmp ${VERSION}"
!define MUI_WELCOMEPAGE_TEXT "Reproductor retro inspirado en el Winamp clásico, solo audio, con música de YouTube.$\r$\n$\r$\nVersión 1.1: motor compilado, más rápido y sin PowerShell.$\r$\n$\r$\nIncluye: ventana propia ajustada al reproductor, modo fantasma, mini reproductor junto a la barra de tareas, botones en la miniatura de la barra de tareas, ecualizador de 10 bandas, 12 temas (diseño Cromo nuevo) y descarga a MP3.$\r$\n$\r$\nSe instala solo para tu usuario (no pide permisos de administrador). Si RetroAmp (o la versión anterior WinampYT) está abierto, se cerrará; tus listas y ajustes se conservan.$\r$\n$\r$\nPulsa Siguiente para continuar."
!define MUI_FINISHPAGE_RUN "$INSTDIR\RetroAmp.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Abrir RetroAmp ahora"
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\LEEME.txt"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Ver el LEEME (instrucciones y atajos)"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Spanish"

; Cierra RetroAmp si está abierto (con su propia opción --salir; sin PowerShell)
!macro StopApp
  IfFileExists "$INSTDIR\RetroAmp.exe" 0 +3
    ExecWait '"$INSTDIR\RetroAmp.exe" --salir'
    Sleep 2500
!macroend

Section "RetroAmp (programa)" SecMain
  SectionIn RO
  SetShellVarContext current
  !insertmacro StopApp

  SetOutPath "$INSTDIR"
  SetOverwrite on
  ; Archivos de la versión 1.0 que ya no se usan
  Delete "$INSTDIR\servidor.ps1"
  Delete "$INSTDIR\host.cs"
  File "app\RetroAmp.exe"
  File "app\RetroAmp.exe.config"
  File "app\RetroAmp.ico"
  File "app\RetroAmp.html"
  File "app\LEEME.txt"

  SetOutPath "$INSTDIR\bin"
  File "app\bin\deno.exe"
  ; yt-dlp puede actualizarse desde el menú: no pisar una versión más nueva ya instalada
  SetOverwrite ifnewer
  File "app\bin\yt-dlp.exe"
  SetOverwrite on
  SetOutPath "$INSTDIR\bin\webview2"
  File /r "app\bin\webview2\*.*"


  ; ---- Pasar datos desde la versión anterior (WinampYT) ----
  StrCpy $1 "$LOCALAPPDATA\Programs\WinampYT"
  StrCmp $INSTDIR $1 no_old
  IfFileExists "$1\*.*" 0 no_old
    IfFileExists "$1\WinampYT.exe" 0 +2
      ExecWait '"$1\WinampYT.exe" --salir'
    Sleep 1500
    CreateDirectory "$INSTDIR\playlists"
    IfFileExists "$INSTDIR\estado.json" +2 0
      CopyFiles /SILENT "$1\estado.json" "$INSTDIR"
    CopyFiles /SILENT "$1\playlists\*.*" "$INSTDIR\playlists"
    CreateDirectory "$INSTDIR\cache"
    CopyFiles /SILENT "$1\cache\*.*" "$INSTDIR\cache"
    IfFileExists "$INSTDIR\ventana.txt" +2 0
      CopyFiles /SILENT "$1\ventana.txt" "$INSTDIR"
    ; quitar la versión anterior (sus listas quedan copiadas aquí)
    Delete "$DESKTOP\WinampYT.lnk"
    RMDir /r "$SMPROGRAMS\WinampYT"
    DeleteRegValue HKCU "${RUNKEY}" "WinampYT"
    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\WinampYT"
    DeleteRegKey HKCU "Software\WinampYT"
    RMDir /r "$1"
  no_old:
  ; Restos de la versión portable que ya no se usan
  Delete "$INSTDIR\Iniciar-RetroAmp.bat"
  Delete "$INSTDIR\Detener-RetroAmp.bat"

  SetOutPath "$INSTDIR"
  CreateDirectory "$SMPROGRAMS\RetroAmp"
  CreateShortcut "$SMPROGRAMS\RetroAmp\RetroAmp.lnk" "$INSTDIR\RetroAmp.exe" "" "$INSTDIR\RetroAmp.ico" 0 SW_SHOWNORMAL "" "Reproductor de música de YouTube"
  CreateShortcut "$SMPROGRAMS\RetroAmp\Cerrar RetroAmp.lnk" "$INSTDIR\RetroAmp.exe" "--salir" "$INSTDIR\RetroAmp.ico" 0
  CreateShortcut "$SMPROGRAMS\RetroAmp\LEEME.lnk" "$INSTDIR\LEEME.txt"
  CreateShortcut "$SMPROGRAMS\RetroAmp\Desinstalar RetroAmp.lnk" "$INSTDIR\Desinstalar.exe"

  WriteUninstaller "$INSTDIR\Desinstalar.exe"
  WriteRegStr HKCU "Software\RetroAmp" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName" "RetroAmp"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "Publisher" "RetroAmp"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\RetroAmp.ico"
  WriteRegStr HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString" '"$INSTDIR\Desinstalar.exe"'
  WriteRegStr HKCU "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\Desinstalar.exe" /S'
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINSTKEY}" "EstimatedSize" "$0"
SectionEnd

Section "Acceso directo en el escritorio" SecDesk
  SetShellVarContext current
  CreateShortcut "$DESKTOP\RetroAmp.lnk" "$INSTDIR\RetroAmp.exe" "" "$INSTDIR\RetroAmp.ico" 0 SW_SHOWNORMAL "" "Reproductor de música de YouTube"
SectionEnd

; Desmarcado por defecto: lo elige el usuario (también se cambia luego en OPC > INICIO)
Section /o "Iniciar con Windows" SecAuto
  WriteRegStr HKCU "${RUNKEY}" "RetroAmp" '"$INSTDIR\RetroAmp.exe" --inicio'
SectionEnd

Section "-inicio automático existente"
  ; Al actualizar: si ya estaba activado, se conserva apuntando a esta versión
  ReadRegStr $0 HKCU "${RUNKEY}" "RetroAmp"
  StrCmp $0 "" +2
    WriteRegStr HKCU "${RUNKEY}" "RetroAmp" '"$INSTDIR\RetroAmp.exe" --inicio'
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMain} "El reproductor y los programas que necesita (yt-dlp, Deno y WebView2). Incluye accesos en el Menú Inicio."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesk} "Crea el icono de RetroAmp en el escritorio."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecAuto} "Abre RetroAmp minimizado al encender Windows. Se puede cambiar luego en OPC > INICIO."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---------------- Desinstalar ----------------
Section "Uninstall"
  SetShellVarContext current
  !insertmacro StopApp

  Delete "$DESKTOP\RetroAmp.lnk"
  RMDir /r "$SMPROGRAMS\RetroAmp"
  DeleteRegValue HKCU "${RUNKEY}" "RetroAmp"
  DeleteRegKey HKCU "${UNINSTKEY}"
  DeleteRegKey HKCU "Software\RetroAmp"

  Delete "$INSTDIR\RetroAmp.exe"
  Delete "$INSTDIR\RetroAmp.exe.config"
  Delete "$INSTDIR\RetroAmp.ico"
  Delete "$INSTDIR\RetroAmp.html"
  Delete "$INSTDIR\servidor.ps1"
  Delete "$INSTDIR\host.cs"
  Delete "$INSTDIR\LEEME.txt"
  Delete "$INSTDIR\registro.txt"
  Delete "$INSTDIR\ventana.txt"
  RMDir /r "$INSTDIR\bin"
  RMDir /r "$INSTDIR\cache"
  RMDir /r "$INSTDIR\perfil"

  IfSilent keep
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "¿Borrar también tus listas guardadas y ajustes de RetroAmp?$\r$\n$\r$\n(Los MP3 de la carpeta Música\RetroAmp no se tocan.)" IDYES wipe IDNO keep
  wipe:
    RMDir /r "$INSTDIR\playlists"
    Delete "$INSTDIR\estado.json"
  keep:
  Delete "$INSTDIR\Desinstalar.exe"
  RMDir "$INSTDIR"
SectionEnd
