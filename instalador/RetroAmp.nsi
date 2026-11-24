; Instalador de RetroAmp 1.3 (NSIS 3)
; Compilar:  makensis instalador\RetroAmp.nsi   (con la carpeta lista en instalador\app\RetroAmp)
Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 64
RequestExecutionLevel user
ManifestDPIAware true

!define APP       "RetroAmp"
!define VERSION   "1.3"
!define VERSION4  "1.3.0.0"
!define SRC       "app\RetroAmp"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\RetroAmp"
!define RUNKEY    "Software\Microsoft\Windows\CurrentVersion\Run"

Name "${APP}"
Caption "Instalar ${APP} ${VERSION}"
OutFile "RetroAmp-${VERSION}-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\RetroAmp"
InstallDirRegKey HKCU "Software\RetroAmp" "InstallDir"
BrandingText "RetroAmp ${VERSION}"

VIProductVersion "${VERSION4}"
VIAddVersionKey /LANG=1034 "ProductName" "RetroAmp"
VIAddVersionKey /LANG=1034 "FileDescription" "Instalador de RetroAmp"
VIAddVersionKey /LANG=1034 "FileVersion" "${VERSION4}"
VIAddVersionKey /LANG=1034 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1034 "LegalCopyright" "Elar Castro"
VIAddVersionKey /LANG=1034 "CompanyName" "Elar Castro"
VIAddVersionKey /LANG=1034 "Comments" "github.com/ElarCastro/RetroAmp"

!include "MUI2.nsh"
!include "FileFunc.nsh"

!define MUI_ICON   "${SRC}\datos\RetroAmp.ico"
!define MUI_UNICON "${SRC}\datos\RetroAmp.ico"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_SMALLDESC

!define MUI_WELCOMEPAGE_TITLE "Bienvenido a RetroAmp ${VERSION}"
!define MUI_WELCOMEPAGE_TEXT "Reproductor retro de música, videos y películas: YouTube, la música de tu PC, radios, Internet TV, consola DJ, skins clásicos y más.$\r$\n$\r$\nSe instala solo para tu usuario (no pide permisos de administrador). Si RetroAmp (o una versión anterior: RetroAmp 1.1/1.2, WINAMP_Plus o WinampYT) está abierto, se cerrará; tus listas y ajustes se conservan.$\r$\n$\r$\nPulsa Siguiente para continuar."
!define MUI_FINISHPAGE_RUN "$INSTDIR\RetroAmp.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Abrir RetroAmp ahora"
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\ManualUsuario.pdf"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Ver el manual de usuario (PDF)"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Spanish"

; Cierra RetroAmp si está abierto (con su propia opción /salir)
!macro StopApp
  IfFileExists "$INSTDIR\RetroAmp.exe" 0 +3
    ExecWait '"$INSTDIR\RetroAmp.exe" /salir'
    Sleep 2500
!macroend

Section "RetroAmp (programa)" SecMain
  SectionIn RO
  SetShellVarContext current
  !insertmacro StopApp

  ; Archivos de versiones anteriores que ya no se usan (los datos del usuario se acomodan solos al abrir)
  Delete "$INSTDIR\servidor.ps1"
  Delete "$INSTDIR\host.cs"
  Delete "$INSTDIR\RetroAmp.exe.config"
  Delete "$INSTDIR\RetroAmp.html"
  Delete "$INSTDIR\Iniciar-RetroAmp.bat"
  Delete "$INSTDIR\Detener-RetroAmp.bat"

  SetOverwrite on
  SetOutPath "$INSTDIR"
  File "${SRC}\RetroAmp.exe"
  File "${SRC}\RetroAmp.ico"   ; para los accesos directos fijados en la barra con la versión 1.1
  File "${SRC}\LEEME.txt"
  File "${SRC}\ManualUsuario.pdf"

  SetOutPath "$INSTDIR\datos"
  File "${SRC}\datos\RetroAmp.html"
  File "${SRC}\datos\RetroAmp.ico"
  File "${SRC}\datos\Detener-RetroAmp.bat"
  SetOutPath "$INSTDIR\datos\fonts"
  File /r "${SRC}\datos\fonts\*.*"
  SetOutPath "$INSTDIR\datos\lib"
  File /r "${SRC}\datos\lib\*.*"
  SetOutPath "$INSTDIR\datos\skins"
  File /r "${SRC}\datos\skins\*.*"

  ; La versión 1.1 tenía bin junto al .exe: si ya existe, RetroAmp lo pasa a datos\bin al abrirse.
  SetOutPath "$INSTDIR\datos\bin"
  File "${SRC}\datos\bin\deno.exe"
  File "${SRC}\datos\bin\ffmpeg.exe"
  File "${SRC}\datos\bin\ffprobe.exe"
  ; yt-dlp se actualiza desde el programa: no pisar una versión más nueva ya instalada
  SetOverwrite ifnewer
  File "${SRC}\datos\bin\yt-dlp.exe"
  SetOverwrite on
  SetOutPath "$INSTDIR\datos\bin\webview2"
  File /r "${SRC}\datos\bin\webview2\*.*"

  CreateDirectory "$INSTDIR\media\musica"
  CreateDirectory "$INSTDIR\media\video"
  CreateDirectory "$INSTDIR\media\playlists"

  SetOutPath "$INSTDIR"
  CreateDirectory "$SMPROGRAMS\RetroAmp"
  CreateShortcut "$SMPROGRAMS\RetroAmp\RetroAmp.lnk" "$INSTDIR\RetroAmp.exe" "" "$INSTDIR\RetroAmp.exe" 0 SW_SHOWNORMAL "" "Reproductor retro de música y video"
  CreateShortcut "$SMPROGRAMS\RetroAmp\Cerrar RetroAmp.lnk" "$INSTDIR\RetroAmp.exe" "/salir" "$INSTDIR\RetroAmp.exe" 0
  CreateShortcut "$SMPROGRAMS\RetroAmp\Manual de usuario.lnk" "$INSTDIR\ManualUsuario.pdf"
  CreateShortcut "$SMPROGRAMS\RetroAmp\Mis archivos (media).lnk" "$INSTDIR\media"
  CreateShortcut "$SMPROGRAMS\RetroAmp\Desinstalar RetroAmp.lnk" "$INSTDIR\Desinstalar.exe"
  Delete "$SMPROGRAMS\RetroAmp\LEEME.lnk"

  WriteUninstaller "$INSTDIR\Desinstalar.exe"
  WriteRegStr HKCU "Software\RetroAmp" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName" "RetroAmp"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "Publisher" "Elar Castro"
  WriteRegStr HKCU "${UNINSTKEY}" "URLInfoAbout" "https://github.com/ElarCastro/RetroAmp"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\RetroAmp.exe"
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
  CreateShortcut "$DESKTOP\RetroAmp.lnk" "$INSTDIR\RetroAmp.exe" "" "$INSTDIR\RetroAmp.exe" 0 SW_SHOWNORMAL "" "Reproductor retro de música y video"
SectionEnd

; Desmarcado por defecto: lo elige el usuario
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
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMain} "El reproductor y lo que necesita (yt-dlp, Deno, FFmpeg y WebView2), con accesos en el Menú Inicio."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesk} "Crea el icono de RetroAmp en el escritorio."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecAuto} "Abre RetroAmp minimizado al encender Windows."
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
  Delete "$INSTDIR\RetroAmp.ico"
  Delete "$INSTDIR\LEEME.txt"
  Delete "$INSTDIR\ManualUsuario.pdf"
  RMDir /r "$INSTDIR\datos"

  IfSilent keep
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "¿Borrar también la carpeta media (tus listas, ajustes, MP3, videos y grabaciones)?$\r$\n$\r$\nSi eliges No, se queda en:$\r$\n$INSTDIR\media" IDYES wipe IDNO keep
  wipe:
    RMDir /r "$INSTDIR\media"
  keep:
  Delete "$INSTDIR\Desinstalar.exe"
  RMDir "$INSTDIR"
SectionEnd
