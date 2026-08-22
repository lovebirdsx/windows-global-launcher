@echo off
rem Install WindowsGlobalLauncher to the per-user Programs directory
rem (%LOCALAPPDATA%\Programs\WindowsGlobalLauncher), create a Start Menu
rem shortcut and configure autostart, then launch it.
rem Runs Install.ps1 via Windows PowerShell with execution policy bypassed.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
