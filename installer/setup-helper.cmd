@echo off
rem Entry point for the Start menu "setup helper" shortcut (bypasses PowerShell execution policy, waits at the end)
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-helper.ps1" -Pause %*
