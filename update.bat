@echo off
REM ---------------------------------------------------------------------------
REM  update.bat
REM  ---------------------------------------------------------------------------
REM  Bu dosya su komutu dogrudan calistirir:
REM
REM      powershell -ExecutionPolicy Bypass -File scripts\update-evren.ps1
REM
REM  Kullanim:
REM      update.bat                       -> standart guncelleme
REM      update.bat -Clean                -> once temizle, sonra guncelle
REM      update.bat -TargetDir "D:\bin"   -> farkli klasore kur
REM      update.bat -Runtime linux-x64    -> farkli platform icin derle
REM
REM  Herhangi bir arguman dogrudan update-evren.ps1'e aktarilir.
REM  Pencerenin kapanmasini engellemek icin en sonda "pause" var.
REM ---------------------------------------------------------------------------

setlocal
cd /d "%~dp0"

echo ============================================================
echo  evren-cli guncelleme  (scripts\update-evren.ps1)
echo ============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\update-evren.ps1" %*
set "EXITCODE=%ERRORLEVEL%"

echo.
if not "%EXITCODE%"=="0" (
    echo [HATA] Guncelleme basarisiz. Cikis kodu: %EXITCODE%
) else (
    echo [TAMAM] Guncelleme basariyla bitti.
)

echo.
pause
endlocal & exit /b %EXITCODE%