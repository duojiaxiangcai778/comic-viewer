@echo off
chcp 936 >nul
cd /d "%~dp0"
start "" "%~dp0ComicViewer.exe" --install
exit