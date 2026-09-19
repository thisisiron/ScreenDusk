@echo off
rem Builds ScreenDusk.exe with the C# compiler that ships with Windows (.NET Framework 4).
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" -nologo -target:winexe -win32icon:assets\app-icon.ico -out:ScreenDusk.exe ScreenDusk.cs
