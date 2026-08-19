@echo off
rem Обёртка native messaging host: манифест Chrome требует path до исполняемого файла.
rem %* передаёт origin расширения (chrome-extension://...), host.js его игнорирует.
node "%~dp0host.js" %*
