# Re-run the Python <-> C# parity check after changing either side.
# Needs: the Python venv in Code\.sona_env (or any Python with numpy) and the .NET 8 SDK.
# Exits non-zero if any step fails, so a failed build can never compare against old output.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$py = Join-Path $PSScriptRoot "..\..\..\..\Code\.sona_env\Scripts\python.exe"
if (-not (Test-Path $py)) { $py = "py" }
if (Test-Path cs_out) { Remove-Item -Recurse -Force cs_out }
& $py py_reference.py ref
if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet run -c Release -- cs_out
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& $py compare.py ref cs_out
exit $LASTEXITCODE
