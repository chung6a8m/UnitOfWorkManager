[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repoRoot
try {
    dotnet restore UnitOfWork.slnx
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet build UnitOfWork.slnx --no-restore --warnaserror
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet test UnitOfWork.slnx --no-build --logger "console;verbosity=normal"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $files = Get-ChildItem src, tests -Recurse -File -Include *.cs, *.csproj
    $patterns = @(
        '\bIUnitOfWork\b',
        '\bClearCurrent\s*\(',
        '\bAmbientFlowId\b',
        '\bOwnerFlowId\b',
        '\bIncrementRef\s*\(',
        '\bGuardedDbConnection\b',
        '\bGuardedDbCommand\b',
        '\bResetAmbientStateForTests\b',
        'Func<Type,\s*IDbConnection,\s*IDbTransaction'
    )

    foreach ($pattern in $patterns) {
        $matches = $files | Select-String -Pattern $pattern
        if ($matches) {
            $matches | ForEach-Object { Write-Host $_.ToString() }
            throw "Forbidden legacy pattern remains: $pattern"
        }
    }

    Write-Host 'P0 transaction invariants verification passed.'
}
finally {
    Pop-Location
}
