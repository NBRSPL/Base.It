@echo off
rem Thin wrapper — the real publish logic lives in publish.ps1 so the
rem line continuations and error handling work reliably across shells.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
