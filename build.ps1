# Builds build\LGBrightnessHotkeys.exe with the C# compiler that ships with Windows (.NET Framework 4.8.1).
# No SDK needed. On ARM64 the ARM64 compiler is used; the manifest makes the exe run natively there.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\FrameworkArm64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe' }
New-Item -ItemType Directory -Force build | Out-Null
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /win32manifest:src\app.manifest `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:build\LGBrightnessHotkeys.exe src\LGBrightnessHotkeys.cs
exit $LASTEXITCODE
