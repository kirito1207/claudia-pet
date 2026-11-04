# Build claudia-pet (Windows) and lay out a runnable folder in dist\claudia-pet.
#
#   pwsh -NoProfile -File scripts\build.ps1
#
# Needs only PowerShell 7: it compiles the C# sources with the Roslyn compiler that ships inside PowerShell,
# against the .NET Framework 4.8 that ships with Windows. No .NET SDK, no Visual Studio.
# The WebView2 SDK (Microsoft.Web.WebView2 on NuGet) is downloaded once into build\downloads.

param(
    [string]$Out = (Join-Path $PSScriptRoot '..\dist\claudia-pet'),
    [switch]$MotionTests
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Out = [IO.Path]::GetFullPath($Out)
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()

# ---- WebView2 SDK from NuGet ----
$wv2Version = '1.0.3537.50'
$downloads = Join-Path $root 'build\downloads'
$wv2Dir = Join-Path $downloads "microsoft.web.webview2.$wv2Version"
if (-not (Test-Path (Join-Path $wv2Dir 'lib\net462\Microsoft.Web.WebView2.Core.dll'))) {
    New-Item -ItemType Directory -Force $downloads | Out-Null
    $pkg = Join-Path $downloads "microsoft.web.webview2.$wv2Version.nupkg"
    Write-Host "downloading Microsoft.Web.WebView2 $wv2Version from nuget.org"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$wv2Version/microsoft.web.webview2.$wv2Version.nupkg" -OutFile $pkg
    Expand-Archive -LiteralPath $pkg -DestinationPath $wv2Dir -Force
}
$wv2Lib = Join-Path $wv2Dir 'lib\net462'
foreach ($dll in 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll', 'Microsoft.Web.WebView2.WinForms.dll') {
    $v = (Get-Item (Join-Path $wv2Lib $dll)).VersionInfo.FileVersion
    if ($v -ne $wv2Version) { throw "$dll is version $v, expected $wv2Version" }
}

# ---- compile ----
$fw  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$wpf = "$fw\WPF"
$refPaths = @(
    "$fw\mscorlib.dll", "$fw\System.dll", "$fw\System.Core.dll", "$fw\System.Xaml.dll",
    "$fw\System.Windows.Forms.dll", "$fw\System.Drawing.dll", "$fw\System.Web.Extensions.dll",
    "$fw\System.Net.Http.dll", "$fw\System.Management.dll", "$fw\System.IO.Compression.dll",
    "$fw\System.IO.Compression.FileSystem.dll", "$fw\Microsoft.CSharp.dll", "$fw\System.Runtime.dll",
    "$fw\System.Xml.dll", "$fw\System.Xml.Linq.dll", "$fw\System.Data.dll",
    "$wpf\PresentationCore.dll", "$wpf\PresentationFramework.dll", "$wpf\WindowsBase.dll",
    "$wpf\UIAutomationProvider.dll", "$wpf\UIAutomationTypes.dll",
    (Join-Path $wv2Lib 'Microsoft.Web.WebView2.Core.dll'), (Join-Path $wv2Lib 'Microsoft.Web.WebView2.Wpf.dll'), (Join-Path $wv2Lib 'Microsoft.Web.WebView2.WinForms.dll')
)
if (Test-Path "$fw\netstandard.dll") { $refPaths += "$fw\netstandard.dll" }
$refs = New-Object System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]
foreach ($p in $refPaths) { if (-not (Test-Path $p)) { throw "missing reference $p" }; $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($p)) }

$parse = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Latest)
$trees = New-Object System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]
foreach ($f in Get-ChildItem (Join-Path $root 'app\src') -Filter *.cs) {
    if ($MotionTests -and $f.Name -eq 'Program.cs') { continue }
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8), $parse, $f.FullName, [Text.Encoding]::UTF8))
}
if ($MotionTests) {
    $testFile = Join-Path $root 'tests\MotionHandoff.cs'
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($testFile), $parse, $testFile, [Text.Encoding]::UTF8))
}
$info = @"
[assembly: System.Reflection.AssemblyTitle("claudia-pet")]
[assembly: System.Reflection.AssemblyProduct("claudia-pet")]
[assembly: System.Reflection.AssemblyDescription("claudia-pet - Claude desktop companion")]
[assembly: System.Reflection.AssemblyVersion("$version.0")]
[assembly: System.Reflection.AssemblyFileVersion("$version.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("$version")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
"@
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($info, $parse, 'AssemblyInfo.g.cs', [Text.Encoding]::UTF8))

$kind = if ($MotionTests) { [Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication } else { [Microsoft.CodeAnalysis.OutputKind]::WindowsApplication }
$opts = (New-Object Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions $kind).
    WithPlatform([Microsoft.CodeAnalysis.Platform]::AnyCpu).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithAllowUnsafe($true)
$comp = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('claudia-pet', $trees, $refs, $opts)
$errors = @($comp.GetDiagnostics() | Where-Object { $_.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error })
if ($errors.Count) { $errors | Select-Object -First 30 | ForEach-Object { Write-Host ("ERR " + $_.ToString().Replace($root + '\', '')) }; throw "compile failed: $($errors.Count) error(s)" }

if (Test-Path $Out) { Remove-Item -LiteralPath $Out -Recurse -Force }
New-Item -ItemType Directory -Force $Out | Out-Null
$exe = Join-Path $Out 'claudia-pet.exe'
$manifest = [IO.File]::OpenRead((Join-Path $root 'app\app.manifest'))
$icon = [IO.File]::OpenRead((Join-Path $root 'icon\AppIcon.ico'))
try {
    $win32 = $comp.CreateDefaultWin32Resources($true, $false, $manifest, $icon)
    $fs = [IO.File]::Create($exe)
    try { $r = $comp.Emit($fs, $null, $null, $win32) } finally { $fs.Dispose() }
} finally { $manifest.Dispose(); $icon.Dispose() }
if (-not $r.Success) { $r.Diagnostics | Where-Object { $_.Severity -eq 'Error' } | Select-Object -First 30 | ForEach-Object { Write-Host "EMIT $_" }; throw 'emit failed' }

# ---- lay out the folder ----
Copy-Item (Join-Path $root 'app\App.config') (Join-Path $Out 'claudia-pet.exe.config')
Copy-Item (Join-Path $wv2Lib 'Microsoft.Web.WebView2.*.dll') $Out
foreach ($arch in 'win-x64', 'win-x86', 'win-arm64') {
    $loader = Join-Path $wv2Dir "runtimes\$arch\native\WebView2Loader.dll"
    New-Item -ItemType Directory -Force (Join-Path $Out "runtimes\$arch\native") | Out-Null
    Copy-Item $loader (Join-Path $Out "runtimes\$arch\native\")
}
Copy-Item (Join-Path $root 'web') (Join-Path $Out 'web') -Recurse
Copy-Item (Join-Path $root 'characters') (Join-Path $Out 'characters') -Recurse
Copy-Item (Join-Path $root 'icon\AppIcon.ico') $Out
Copy-Item (Join-Path $root 'VERSION') $Out
foreach ($f in 'LICENSE', 'NOTICE.md') { $src = Join-Path $root $f; if (Test-Path $src) { Copy-Item $src $Out } }
$plugin = Join-Path $root 'plugin'
if (Test-Path $plugin) { Copy-Item $plugin (Join-Path $Out 'plugin') -Recurse }

"built claudia-pet $version -> $Out  (exe $((Get-Item $exe).Length) bytes)"
if ($MotionTests) {
    & $exe
    if ($LASTEXITCODE -ne 0) { throw 'Native motion handoff tests failed' }
}
