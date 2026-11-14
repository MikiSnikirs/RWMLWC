# Compile check without the .NET SDK: PowerShell 7.6+ ships the Roslyn compiler.
# Compiles Shared + Net + ServerCore + Client as one assembly against the game's own assemblies
# and prints compiler errors. With -Emit writes the assembly (for tools/check-patches.ps1).
#
#   pwsh tools/check-compile.ps1 -Game <RimWorld folder> -Harmony <path to 1.7 0Harmony.dll> [-Emit out/RWMLWC.Check.dll]
#
# This is NOT the real build (dotnet build RWMLWC.slnx): one assembly instead of four, no tests.

param(
    [string]$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [Parameter(Mandatory = $true)][string]$Game,
    [Parameter(Mandatory = $true)][string]$Harmony,
    [string[]]$Projects = @('Shared', 'Net', 'ServerCore', 'Client'),
    [string]$Emit = ''
)
$ErrorActionPreference = 'Stop'
$parse = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::new([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Latest)
$trees = [System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
foreach ($p in $Projects) {
    $dir = Join-Path $Repo "Source/$p"
    if (-not (Test-Path $dir)) { continue }
    Get-ChildItem $dir -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } | ForEach-Object {
        $text = [System.IO.File]::ReadAllText($_.FullName)
        $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($text, $parse, $_.FullName.Substring($Repo.Length + 1)))
    }
}
$refs = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
$pattern = '^(System\..*|Microsoft\.(CSharp|VisualBasic.*|Win32\..*)|netstandard|mscorlib|Assembly-CSharp|UnityStubs|Waylith|com\.rlabrecque\.steamworks\.net)\.dll$'
foreach ($f in (Get-ChildItem $Game -Filter *.dll | Where-Object { $_.Name -match $pattern })) {
    try {
        [void][System.Reflection.AssemblyName]::GetAssemblyName($f.FullName)   # skips native libraries
        $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($f.FullName))
    } catch { }
}
$refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($Harmony))
$opts = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$opts = $opts.WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable).WithAllowUnsafe($true)
$comp = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('RWMLWC.Check', $trees, $refs, $opts)
$diags = @($comp.GetDiagnostics() | Where-Object { $_.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error })
"sources: $($trees.Count), references: $($refs.Count), errors: $($diags.Count)"
$diags | Select-Object -First 100 | ForEach-Object {
    $span = $_.Location.GetLineSpan()
    '{0}({1}): {2}: {3}' -f $span.Path, ($span.StartLinePosition.Line + 1), $_.Id, $_.GetMessage([System.Globalization.CultureInfo]::InvariantCulture)
}
if ($Emit -and $diags.Count -eq 0) {
    New-Item -ItemType Directory -Force -Path (Split-Path $Emit) | Out-Null
    $fs = [System.IO.File]::Create($Emit)
    try { $result = $comp.Emit($fs) } finally { $fs.Close() }
    "emit: $($result.Success) -> $Emit"
}
if ($diags.Count -gt 0) { exit 1 }
