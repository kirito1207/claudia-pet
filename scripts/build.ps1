# Build claudia-pet (Windows) and lay out a runnable folder in dist\claudia-pet.
#
#   pwsh -NoProfile -File scripts\build.ps1
# -Out names a dedicated child of build or dist; an existing one is replaced only if it is a claudia-pet build.
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
$Out = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Out, $PWD.ProviderPath))
$outputParent = [IO.Path]::GetDirectoryName($Out)
$outputMarker = '.claudia-pet-build-output'

function Assert-PlainDirectory([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($item -and (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint))) {
        throw "Build output cannot use a file, symlink or junction: $Path"
    }
}

function Assert-BuildOutput {
    if ($outputParent -notin @((Join-Path $root 'build'), (Join-Path $root 'dist')) -or
        $Out -eq (Join-Path $root 'build\downloads') -or
        [IO.Path]::GetFileName($Out) -match '[. ]$|:|^\.claudia-(stage|previous)-') {
        throw 'Choose a dedicated output directory directly under this checkout''s build or dist folder (for example, build/native-tests).'
    }
    Assert-PlainDirectory $outputParent
    Assert-PlainDirectory $Out
    if (Test-Path -LiteralPath $Out) {
        $marker = Get-Item -LiteralPath (Join-Path $Out $outputMarker) -Force -ErrorAction SilentlyContinue
        # Output from the older build script has no marker; recognise it by its layout.
        $legacy = (Test-Path -LiteralPath (Join-Path $Out 'claudia-pet.exe') -PathType Leaf) -and
                  (Test-Path -LiteralPath (Join-Path $Out 'web\index.html') -PathType Leaf)
        if ($marker -and ($marker.PSIsContainer -or ($marker.Attributes -band [IO.FileAttributes]::ReparsePoint))) { $marker = $null }
        if (-not $marker -and -not $legacy) {
            throw "Existing directory has no claudia-pet build marker: $Out. Rename it or choose a new -Out directory."
        }
    }
}

function Remove-BuildScratch([string]$Path) {
    if ([IO.Path]::GetDirectoryName($Path) -ne $outputParent -or
        [IO.Path]::GetFileName($Path) -notmatch '^\.claudia-(stage|previous)-[0-9a-f]{32}$') {
        throw "Unexpected build scratch directory: $Path"
    }
    Assert-PlainDirectory $outputParent
    Assert-PlainDirectory $Path
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
}

Assert-BuildOutput
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

Assert-BuildOutput
$staging = Join-Path $outputParent ('.claudia-stage-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($staging)
try {
    $manifest = [IO.File]::OpenRead((Join-Path $root 'app\app.manifest'))
    $icon = [IO.File]::OpenRead((Join-Path $root 'icon\AppIcon.ico'))
    try {
        $win32 = $comp.CreateDefaultWin32Resources($true, $false, $manifest, $icon)
        $fs = [IO.File]::Create((Join-Path $staging 'claudia-pet.exe'))
        try { $r = $comp.Emit($fs, $null, $null, $win32) } finally { $fs.Dispose() }
    } finally { $manifest.Dispose(); $icon.Dispose() }
    if (-not $r.Success) { $r.Diagnostics | Where-Object { $_.Severity -eq 'Error' } | Select-Object -First 30 | ForEach-Object { Write-Host "EMIT $_" }; throw 'emit failed' }

    # Lay out the complete package before replacing the last successful build.
    Copy-Item (Join-Path $root 'app\App.config') (Join-Path $staging 'claudia-pet.exe.config')
    Copy-Item (Join-Path $wv2Lib 'Microsoft.Web.WebView2.*.dll') $staging
    # The WebView2 package allows binary redistribution if its copyright notice and disclaimer go along with it.
    Copy-Item (Join-Path $wv2Dir 'LICENSE.txt') (Join-Path $staging 'WebView2-LICENSE.txt')
    Copy-Item (Join-Path $wv2Dir 'NOTICE.txt') (Join-Path $staging 'WebView2-NOTICE.txt')
    foreach ($arch in 'win-x64', 'win-x86', 'win-arm64') {
        $loader = Join-Path $wv2Dir "runtimes\$arch\native\WebView2Loader.dll"
        New-Item -ItemType Directory -Force (Join-Path $staging "runtimes\$arch\native") | Out-Null
        Copy-Item $loader (Join-Path $staging "runtimes\$arch\native\")
    }
    Copy-Item (Join-Path $root 'web') (Join-Path $staging 'web') -Recurse
    Copy-Item (Join-Path $root 'characters') (Join-Path $staging 'characters') -Recurse
    Copy-Item (Join-Path $root 'icon\AppIcon.ico') $staging
    Copy-Item (Join-Path $root 'VERSION') $staging
    foreach ($f in 'LICENSE', 'NOTICE.md') { $src = Join-Path $root $f; if (Test-Path $src) { Copy-Item $src $staging } }
    $plugin = Join-Path $root 'plugin'
    if (Test-Path $plugin) { Copy-Item $plugin (Join-Path $staging 'plugin') -Recurse }
    New-Item -ItemType File -Path (Join-Path $staging $outputMarker) | Out-Null

    $publishLock = [Threading.Mutex]::new($false, 'Local\ClaudiaBuildPublish')
    try {
        try { $null = $publishLock.WaitOne() } catch [Threading.AbandonedMutexException] { }
        try {
            Assert-BuildOutput
            $previous = $null
            if (Test-Path -LiteralPath $Out) {
                $previous = Join-Path $outputParent ('.claudia-previous-' + [guid]::NewGuid().ToString('N'))
                [IO.Directory]::Move($Out, $previous)
            }
            try {
                [IO.Directory]::Move($staging, $Out)
            } catch {
                if ($previous) { [IO.Directory]::Move($previous, $Out) }
                throw
            }
            if ($previous) { Remove-BuildScratch $previous }
        } finally { $publishLock.ReleaseMutex() }
    } finally { $publishLock.Dispose() }
} finally {
    Remove-BuildScratch $staging
}

$exe = Join-Path $Out 'claudia-pet.exe'
"built claudia-pet $version -> $Out  (exe $((Get-Item -LiteralPath $exe).Length) bytes)"
if ($MotionTests) {
    & $exe
    if ($LASTEXITCODE -ne 0) { throw 'Native motion handoff tests failed' }
}
