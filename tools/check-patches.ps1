# Applies every Harmony patch of the mod to the real game assembly, without starting the game.
# Catches: a patched method that does not exist, a wrong parameter name in a prefix, a private field
# name that changed (static initializers of the listed classes run here).
# Does NOT run any game logic: it only proves that the patches can be installed.
#
#   pwsh tools/check-compile.ps1 -Game <RimWorld> -Harmony <0Harmony.dll> -Emit out/RWMLWC.Check.dll
#   pwsh tools/check-patches.ps1 -Game <RimWorld> -Harmony <0Harmony.dll> -Assembly out/RWMLWC.Check.dll

param(
    [Parameter(Mandatory = $true)][string]$Game,
    [Parameter(Mandatory = $true)][string]$Harmony,
    [Parameter(Mandatory = $true)][string]$Assembly
)
# The game and the mod live in their own load context: the game ships newer versions of some
# libraries than PowerShell itself has loaded.
Add-Type -TypeDefinition @"
using System; using System.IO; using System.Reflection; using System.Runtime.Loader;
public sealed class GameAlc : AssemblyLoadContext {
  readonly string game;
  public GameAlc(string game) : base("game") { this.game = game; }
  protected override Assembly Load(AssemblyName name) {
    string n = name.Name;
    if (n.StartsWith("System.") || n == "System" || n == "netstandard" || n == "mscorlib" || n.StartsWith("Microsoft.CSharp") || n.StartsWith("Microsoft.Win32") || n.StartsWith("Microsoft.VisualBasic")) return null;
    string p = Path.Combine(game, n + ".dll");
    if (File.Exists(p)) return LoadFromAssemblyPath(p);
    if (n.StartsWith("UnityEngine") || n.StartsWith("Unity.")) return LoadFromAssemblyPath(Path.Combine(game, "UnityStubs.dll"));
    return null;
  }
}
"@
$alc = [GameAlc]::new((Resolve-Path $Game).Path)
$acs = $alc.LoadFromAssemblyPath((Join-Path (Resolve-Path $Game).Path 'Assembly-CSharp.dll'))
$har = $alc.LoadFromAssemblyPath((Resolve-Path $Harmony).Path)
$mod = $alc.LoadFromAssemblyPath((Resolve-Path $Assembly).Path)
"game assembly $($acs.GetName().Version), Harmony $($har.GetName().Version)"
$bad = 0

'static initializers (private fields and properties reached by name):'
foreach ($name in @('RWMLWC.Client.Sim.MapRandContext', 'RWMLWC.Client.Sim.LockstepClient', 'RWMLWC.Client.Input.GizmoCommands',
        'RWMLWC.Client.Input.DesignatorCommands', 'RWMLWC.Client.Input.VirtualUi', 'RWMLWC.Client.Input.MethodCommands',
        'RWMLWC.Client.Input.ValueCodec', 'RWMLWC.Client.Input.CommandBus', 'RWMLWC.Client.Input.MapClickCommands')) {
    $t = $mod.GetType($name)
    if (-not $t) { "  (no type $name in this branch)"; continue }
    try { [System.Runtime.CompilerServices.RuntimeHelpers]::RunClassConstructor($t.TypeHandle) }
    catch { $bad++; $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; "  FAILED: $name : $($e.Message)" }
}

$h = [Activator]::CreateInstance($har.GetType('HarmonyLib.Harmony'), @('rwmlwc.patchcheck'))
$classes = @($mod.GetTypes() | Where-Object { $_.GetCustomAttributesData() | Where-Object { $_.AttributeType.Name -eq 'HarmonyPatch' } })
$ok = 0
foreach ($c in $classes) {
    try { [void]$h.CreateClassProcessor($c).Patch(); $ok++ }
    catch { $bad++; $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; "  PATCH FAILED: $($c.FullName): $($e.GetType().Name): $($e.Message)" }
}
"patch classes applied: $ok of $($classes.Count)"

# Patches installed in bulk at game start ([StaticConstructorOnStartup] classes call these with the mod's Harmony instance).
foreach ($pair in @('RWMLWC.Client.Input.DesignatorCommands|PatchAll', 'RWMLWC.Client.Input.GizmoCommands|PatchAll',
        'RWMLWC.Client.Input.VirtualUi|PatchOrderForceTarget', 'RWMLWC.Client.Input.MethodCommands|PatchAll')) {
    $typeName, $methodName = $pair.Split('|')
    $t = $mod.GetType($typeName)
    if (-not $t) { continue }
    try { [void]$t.GetMethod($methodName).Invoke($null, @($h)); "bulk ok: $typeName.$methodName" }
    catch { $bad++; $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; "BULK FAILED: $typeName.$methodName : $($e.GetType().Name): $($e.Message)" }
}

$names = @{}
foreach ($m in $h.GetPatchedMethods()) { if ($names.ContainsKey($m.Name)) { $names[$m.Name]++ } else { $names[$m.Name] = 1 } }
"patched methods: $(($names.Values | Measure-Object -Sum).Sum)"
($names.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '
if ($bad -gt 0) { "PROBLEMS: $bad"; exit 1 }
