@ECHO OFF
REM Builds PippiHotFix.exe.
REM
REM Preferred path: the Roslyn compiler from any installed .NET SDK, with
REM -deterministic. That makes the build REPRODUCIBLE - anyone rebuilding this
REM source gets a byte-identical exe and can check its SHA-256 against the one
REM published with the release.
REM
REM Fallback path: the C# compiler that ships with Windows. No SDK needed, but
REM it is the pre-Roslyn compiler, has no -deterministic, and stamps a fresh
REM MVID and timestamp into every build - so the exe works but its hash will
REM NOT match the published one.
SETLOCAL ENABLEDELAYEDEXPANSION

SET "OUT=PippiHotFix.exe"
SET "REFS=%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"

SET "ROSLYN="
FOR /F "delims=" %%D IN ('dir /b /o-n "%ProgramFiles%\dotnet\sdk" 2^>nul') DO (
  IF NOT DEFINED ROSLYN IF EXIST "%ProgramFiles%\dotnet\sdk\%%D\Roslyn\bincore\csc.dll" (
    SET "ROSLYN=%ProgramFiles%\dotnet\sdk\%%D\Roslyn\bincore\csc.dll"
  )
)

IF DEFINED ROSLYN IF EXIST "%REFS%\mscorlib.dll" (
  ECHO Building reproducibly with Roslyn:
  ECHO   !ROSLYN!
  dotnet "!ROSLYN!" -nologo -deterministic -target:winexe -out:"%OUT%" ^
    -optimize+ -platform:anycpu -nostdlib+ PippiHotFix.cs ^
    -r:"%REFS%\mscorlib.dll" -r:"%REFS%\System.dll" -r:"%REFS%\System.Core.dll" ^
    -r:"%REFS%\System.Drawing.dll" -r:"%REFS%\System.Windows.Forms.dll" ^
    -r:"%REFS%\System.Web.Extensions.dll"
  IF ERRORLEVEL 1 ( ECHO BUILD FAILED & EXIT /B 1 )
  ECHO.
  ECHO Built %OUT% ^(reproducible^). Verify with:
  ECHO   certutil -hashfile %OUT% SHA256
  EXIT /B 0
)

ECHO No .NET SDK or 4.8 reference assemblies found - falling back to the
ECHO in-box compiler. The exe will work but will NOT be hash-reproducible.
ECHO.
SET "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
IF NOT EXIST "%CSC%" ( ECHO No C# compiler found. & EXIT /B 1 )
"%CSC%" -nologo -target:winexe -out:"%OUT%" -optimize+ -platform:anycpu ^
  PippiHotFix.cs ^
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll ^
  -r:System.Windows.Forms.dll -r:System.Web.Extensions.dll
IF ERRORLEVEL 1 ( ECHO BUILD FAILED & EXIT /B 1 )
ECHO Built %OUT% ^(not reproducible^).
