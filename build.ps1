# Builds WillyWerkel-Setup.exe from the sources in .\src
# Requires the .NET Framework C# compiler (ships with Windows).
$ErrorActionPreference = "Stop"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = Join-Path $PSScriptRoot "src"

# 1) compile the launcher
$runner = Join-Path $env:TEMP "WillyRun_built.exe"
& $csc /nologo /optimize+ /target:winexe "/out:$runner" "$src\WillyRun.cs"

# 2) embed the launcher (base64) into the setup source
$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($runner))
$final = Join-Path $env:TEMP "WillySetup_final.cs"
(Get-Content "$src\WillySetup.cs" -Raw).Replace('__WILLYRUN_B64__', $b64) | Set-Content $final -Encoding UTF8 -NoNewline

# 3) compile the setup (with admin manifest)
$out = Join-Path $PSScriptRoot "WillyWerkel-Setup.exe"
& $csc /nologo /optimize+ /target:exe "/out:$out" /win32manifest:"$src\app.manifest" /r:System.Windows.Forms.dll /r:System.dll $final
Write-Host "Built: $out"
