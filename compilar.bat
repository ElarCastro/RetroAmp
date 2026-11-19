@echo off
rem ============================================================
rem  RetroAmp - compila RetroAmp.exe y prepara instalador\app
rem  Usa el compilador de C# que ya trae Windows (.NET Framework 4.x)
rem  Antes: copia yt-dlp.exe y deno.exe en la carpeta bin\
rem  Despues: makensis instalador\RetroAmp.nsi   (NSIS 3)
rem ============================================================
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set WV=bin\webview2
set APP=instalador\app

if not exist "bin\yt-dlp.exe" (echo Falta bin\yt-dlp.exe & exit /b 1)
if not exist "bin\deno.exe"   (echo Falta bin\deno.exe & exit /b 1)
if not exist "%APP%" mkdir "%APP%"

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:%APP%\RetroAmp.exe /win32icon:RetroAmp.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Core.dll ^
  /r:%WV%\Microsoft.Web.WebView2.Core.dll /r:%WV%\Microsoft.Web.WebView2.WinForms.dll ^
  Program.cs Server.cs Host.cs
if errorlevel 1 (echo ERROR al compilar & exit /b 1)

copy /y RetroAmp.exe.config "%APP%\" >nul
copy /y RetroAmp.html "%APP%\" >nul
copy /y RetroAmp.ico "%APP%\" >nul
copy /y LEEME.txt "%APP%\" >nul
xcopy /y /e /i bin "%APP%\bin" >nul

echo Listo: %APP%\RetroAmp.exe
echo Para crear el instalador:  makensis instalador\RetroAmp.nsi
