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

    1..3 | ForEach-Object {
        dotnet test UnitOfWork.slnx --no-build --logger "console;verbosity=minimal"
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $sourceFiles = Get-ChildItem src/UnitOfWork.Core -Recurse -File -Include *.cs
    $forbiddenPatterns = @(
        'TransactionBoundDbConnection\s*:\s*IDbConnection',
        'TransactionBoundDbCommand\s*:\s*IDbCommand',
        'TransactionBoundDbTransaction\s*:\s*IDbTransaction',
        'Func<Type,\s*IDbConnection,\s*object>',
        'Task\.FromResult\s*\(\s*_inner\.Execute',
        '_connection\.BeginTransaction\s*\(',
        '_transaction\??\.Commit\s*\(',
        '_transaction\??\.Rollback\s*\('
    )

    foreach ($pattern in $forbiddenPatterns) {
        $matches = $sourceFiles | Select-String -Pattern $pattern
        if ($matches) {
            $matches | ForEach-Object { Write-Host $_.ToString() }
            throw "Forbidden P1 pattern remains: $pattern"
        }
    }

    $requiredSymbols = @(
        'IAsyncDisposable',
        'UnitOfWorkOperationLease',
        'TransactionBoundDbDataReader',
        'ExecuteNonQueryAsync',
        'BeginTransactionAsync',
        'CommitAsync',
        'RollbackAsync',
        'UnitOfWorkOptions',
        'IUnitOfWorkTransactionFactory'
    )

    foreach ($symbol in $requiredSymbols) {
        if (-not ($sourceFiles | Select-String -SimpleMatch $symbol)) {
            throw "Required P1 symbol is missing: $symbol"
        }
    }

    Write-Host 'P1 async and concurrency boundary verification passed.'
}
finally {
    Pop-Location
}
