@echo off
setlocal
pushd "%~dp0"
set "compiler=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%compiler%" (
    echo .NET Framework 4.8 is required on 64-bit Windows.
    pause
    exit /b 1
)
"%compiler%" /nologo /target:winexe /platform:x64 /optimize+ /out:AudioBatch.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll AudioBatch.cs
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)
echo Done: AudioBatch.exe
popd
pause
exit /b 0
