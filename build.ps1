param([switch]$Test)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $assemblies = @('System.Data', 'System.Drawing', 'System.Windows.Forms', 'System.Net.Http',
        'System.Web.Extensions', 'System.IO.Compression', 'System.IO.Compression.FileSystem')
    $argsCommon = @('/nologo', '/optimize+', '/platform:anycpu', '/codepage:65001', '/resource:src/de.tsv,de.tsv')
    $argsCommon += @($assemblies | ForEach-Object { "/reference:$_.dll" })
    $source = @(Get-ChildItem -LiteralPath src -Filter '*.cs' | ForEach-Object FullName)
    New-Item -Path out -ItemType Directory -Force | Out-Null
    & $compiler @argsCommon /target:winexe /out:out/MovieSearch.exe @source
    if ($LASTEXITCODE -ne 0) { throw "Application compilation failed." }
    if ($Test) {
        $testSource = (Get-Item tests/Checks.cs).FullName
        & $compiler @argsCommon /target:exe /main:MovieSearch.Checks /out:out/Checks.exe @source $testSource
        if ($LASTEXITCODE -ne 0) { throw "Test compilation failed." }
        & ./out/Checks.exe
        if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
    }
    Compress-Archive -Path out/MovieSearch.exe, Install.cmd, README.md, LICENSE -DestinationPath out/MovieSearch-Windows.zip -Force
    Get-Item out/MovieSearch-Windows.zip | Select-Object Name,Length
}
finally { Pop-Location }
