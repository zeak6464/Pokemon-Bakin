@echo off
setlocal enabledelayedexpansion

echo Installing fonts in the current folder...

for %%i in (*.ttf, *.otf) do (
    echo Installing "%%i"...
    copy "%%i" "%windir%\Fonts\"
)

echo All fonts installed successfully.
pause
