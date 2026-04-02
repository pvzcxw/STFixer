@echo off
set "VSTOOLS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\14.44.35207"
set "WINSDK=C:\Program Files (x86)\Windows Kits\10"
set "WINSDKVER=10.0.26100.0"

"%VSTOOLS%\bin\Hostx64\x64\cl.exe" /nologo /O1 /GS- /W3 /c /Fo"stella_fallback.obj" stella_fallback.c /I"%VSTOOLS%\include" /I"%WINSDK%\Include\%WINSDKVER%\ucrt" /I"%WINSDK%\Include\%WINSDKVER%\um" /I"%WINSDK%\Include\%WINSDKVER%\shared"
if errorlevel 1 exit /b 1

"%VSTOOLS%\bin\Hostx64\x64\link.exe" /nologo /DLL /OUT:stella_fallback.dll /DEF:stella_fallback.def stella_fallback.obj winhttp.lib kernel32.lib user32.lib /LIBPATH:"%VSTOOLS%\lib\x64" /LIBPATH:"%WINSDK%\Lib\%WINSDKVER%\ucrt\x64" /LIBPATH:"%WINSDK%\Lib\%WINSDKVER%\um\x64" /NODEFAULTLIB:libcmt.lib /DEFAULTLIB:msvcrt.lib /SUBSYSTEM:WINDOWS /OPT:REF /OPT:ICF
if errorlevel 1 exit /b 1

echo Build OK
dir stella_fallback.dll
