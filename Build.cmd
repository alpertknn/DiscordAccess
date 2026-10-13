@echo off
setlocal
pushd "%~dp0" || exit /b 1
set "DiscordAccessCompiler=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%DiscordAccessCompiler%" (
    echo .NET Framework 4.x 64-bit C# compiler not found.
    popd
    exit /b 1
)
echo Building DiscordAccess v4.21...
"%DiscordAccessCompiler%" /nologo /target:winexe /main:DiscordAccess.Program /platform:x64 /optimize+ /win32manifest:src\app.manifest /out:DiscordAccess.exe /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /resource:bridge\attachment-download.cjs,DiscordAccess.AttachmentDownloadHook @build-production.rsp
set "DiscordAccessBuildResult=%errorlevel%"
popd
exit /b %DiscordAccessBuildResult%
