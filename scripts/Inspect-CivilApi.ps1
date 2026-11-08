param([string[]]$Types = @('TinSurface'), [string]$Match = '.*')
$ErrorActionPreference = 'Stop'
$cadDirectory = 'C:\Program Files\Autodesk\AutoCAD 2021'
[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve({
    param($sender, $eventArgs)
    $assemblyName = [Reflection.AssemblyName]::new($eventArgs.Name).Name + '.dll'
    foreach ($folder in @($cadDirectory, (Join-Path $cadDirectory 'C3D'))) {
        $candidate = Join-Path $folder $assemblyName
        if (Test-Path -LiteralPath $candidate) { return [Reflection.Assembly]::ReflectionOnlyLoadFrom($candidate) }
    }
    return [Reflection.Assembly]::ReflectionOnlyLoad($eventArgs.Name)
})
$assembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom((Join-Path $cadDirectory 'C3D\AeccDbMgd.dll'))
foreach ($name in $Types) {
    $type = $assembly.GetTypes() | Where-Object { $_.Name -eq $name }
    foreach ($entry in $type) {
        Write-Output $entry.FullName
        $entry.GetMembers([Reflection.BindingFlags]'Public,Instance,Static,DeclaredOnly') | Where-Object { $_.Name -match $Match } | ForEach-Object { $_.ToString() }
    }
}
