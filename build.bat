@echo off
REM Build Inkly with the .NET Framework C# compiler that ships with Windows
REM (no Visual Studio or .NET SDK required). Requires .NET Framework 4.x.
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /win32icon:icon.ico /out:Inkly.exe /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll Program.cs
echo Build complete: Inkly.exe
