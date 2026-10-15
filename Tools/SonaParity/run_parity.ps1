# Re-run the Python <-> C# parity check after changing either side.
# Needs: the Python venv in Code\.sona_env (or any Python with numpy) and the .NET 8 SDK.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$py = Join-Path $PSScriptRoot "..\..\..\..\Code\.sona_env\Scripts\python.exe"
if (-not (Test-Path $py)) { $py = "py" }
& $py py_reference.py ref
dotnet run -c Release -- cs_out
& $py compare.py ref cs_out
