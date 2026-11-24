@echo off
rem Cierra RetroAmp (este archivo va en la carpeta datos)
if exist "%~dp0..\RetroAmp.exe" ("%~dp0..\RetroAmp.exe" /salir) else ("%~dp0RetroAmp.exe" /salir)
