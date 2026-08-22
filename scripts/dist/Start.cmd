@echo off
rem Entry point for first-time users: detect and install .NET 8 Desktop Runtime
rem if missing, then launch WindowsGlobalLauncher.exe.
rem Runs Start.ps1 via Windows PowerShell with execution policy bypassed.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start.ps1"
