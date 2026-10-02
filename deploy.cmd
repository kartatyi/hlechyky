@echo off
title Hlechyky deploy
rem Deploy on command: pulls main from GitHub, test build, restart (players see a short reconnect). Log: logs\deploy.log
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy.ps1"
pause
