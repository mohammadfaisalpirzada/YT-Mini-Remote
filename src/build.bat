@echo off
rem Builds YTMiniRemote.exe with Windows' own C# compiler.
rem Needs the WebView2 SDK DLLs in a "wv2" folder next to this file:
rem   Microsoft.Web.WebView2.Core.dll, Microsoft.Web.WebView2.WinForms.dll,
rem   WebView2Loader.x64.dll, WebView2Loader.x86.dll  (from the Microsoft.Web.WebView2 NuGet package)
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /win32icon:icon.ico ^
 /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
 /r:wv2\Microsoft.Web.WebView2.Core.dll /r:wv2\Microsoft.Web.WebView2.WinForms.dll ^
 /resource:wv2\Microsoft.Web.WebView2.Core.dll,Microsoft.Web.WebView2.Core.dll ^
 /resource:wv2\Microsoft.Web.WebView2.WinForms.dll,Microsoft.Web.WebView2.WinForms.dll ^
 /resource:wv2\WebView2Loader.x64.dll,WebView2Loader.x64.dll ^
 /resource:wv2\WebView2Loader.x86.dll,WebView2Loader.x86.dll ^
 /out:YTMiniRemote.exe YTMiniRemote.cs
if errorlevel 1 (echo Build FAILED & pause & exit /b 1)
echo Built: YTMiniRemote.exe
