@echo off
rem ==================================================================
rem  RetroAmp 1.2 - compila RetroAmp.exe y arma la carpeta portable
rem  Usa el compilador de C# que ya trae Windows (.NET Framework 4.x)
rem  Antes: copia yt-dlp.exe, deno.exe, ffmpeg.exe y ffprobe.exe en bin\
rem  Resultado: build\RetroAmp  (para el instalador: instalador\app\RetroAmp)
rem ==================================================================
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set WV=bin\webview2
set OUT=build\RetroAmp

for %%f in (yt-dlp.exe deno.exe ffmpeg.exe ffprobe.exe) do if not exist "bin\%%f" (echo Falta bin\%%f & exit /b 1)
if exist build rd /s /q build
mkdir "%OUT%\datos\bin" "%OUT%\datos\fonts" "%OUT%\datos\lib" "%OUT%\datos\skins"

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:%OUT%\RetroAmp.exe /win32icon:RetroAmp.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.dll /r:System.Web.Extensions.dll ^
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  /r:%WV%\Microsoft.Web.WebView2.Core.dll /r:%WV%\Microsoft.Web.WebView2.WinForms.dll ^
  *.cs
if errorlevel 1 (echo ERROR al compilar & exit /b 1)

copy /y LEEME.txt "%OUT%\" >nul
if exist manual\ManualUsuario.pdf copy /y manual\ManualUsuario.pdf "%OUT%\" >nul
copy /y RetroAmp.html "%OUT%\datos\" >nul
copy /y RetroAmp.ico "%OUT%\datos\" >nul
copy /y Detener-RetroAmp.bat "%OUT%\datos\" >nul
copy /y VT323-Regular.ttf "%OUT%\datos\fonts\" >nul
copy /y VT323-LICENCIA-OFL.txt "%OUT%\datos\fonts\" >nul
xcopy /y /e /i /q lib "%OUT%\datos\lib" >nul
xcopy /y /e /i /q skins "%OUT%\datos\skins" >nul
xcopy /y /e /i /q bin "%OUT%\datos\bin" >nul
for %%d in (musica video playlists perfil) do mkdir "%OUT%\media\%%d"

if exist instalador\app rd /s /q instalador\app
xcopy /y /e /i /q build "instalador\app" >nul
echo.
echo Listo: %OUT%\RetroAmp.exe
echo Instalador:  makensis instalador\RetroAmp.nsi   (NSIS 3)
