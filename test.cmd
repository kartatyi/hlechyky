@echo off
rem Hlechyky tests: test (own tests from this branch) | test Clicker | test -All | test -List
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0test.ps1" %*
