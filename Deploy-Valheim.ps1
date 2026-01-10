param(
	# Корневая папка Valheim (где лежит Valheim.exe и папка BepInEx)
	[string]$GameRoot = "C:\Program Files (x86)\Steam\steamapps\common\Valheim",

	# Конфигурация сборки (Release/Debug)
	[ValidateSet("Release", "Debug")]
	[string]$Configuration = "Release",

	# Пропустить сборку и только скопировать уже собранные файлы
	[switch]$SkipBuild,

	# Копировать PDB (если существует)
	[switch]$CopyPdb,

	# Копировать XML (если существует)
	[switch]$CopyXml,

	# Также копировать в runtime/BepInEx/core внутри репозитория
	[switch]$AlsoCopyRuntime
)

$ErrorActionPreference = "Stop"

function Assert-Path([string]$path, [string]$label) {
	if (-not (Test-Path -LiteralPath $path)) {
		throw "Не найдено: $label = $path"
	}
}

function Copy-FileIfExists([string]$source, [string]$destDir) {
	if (-not (Test-Path -LiteralPath $source)) {
		Write-Host "Пропуск (нет файла): $source"
		return $false
	}

	New-Item -ItemType Directory -Force -Path $destDir | Out-Null
	Copy-Item -Force -LiteralPath $source -Destination $destDir
	return $true
}

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$solutionPath = Join-Path $repoRoot "BepInEx.sln"

Assert-Path $solutionPath "Solution"

$gameCore = Join-Path $GameRoot "BepInEx\core"
Assert-Path $gameCore "Valheim BepInEx/core"

if (-not $SkipBuild) {
	Write-Host "Сборка решения: $solutionPath (Configuration=$Configuration)"
	& dotnet build $solutionPath -c $Configuration -v minimal
	if ($LASTEXITCODE -ne 0) {
		throw "Сборка завершилась с ошибкой (код $LASTEXITCODE)."
	}
}

$srcCache = Join-Path $repoRoot ("BepInEx.Cache.Core\bin\{0}\net35" -f $Configuration)
$srcBep = Join-Path $repoRoot ("BepInEx\bin\{0}\net35" -f $Configuration)

Assert-Path $srcCache "Build output (BepInEx.Cache.Core)"
Assert-Path $srcBep "Build output (BepInEx)"

$targets = @(
	@{ Name = "BepInEx.dll"; Src = (Join-Path $srcBep "BepInEx.dll") },
	@{ Name = "BepInEx.Cache.Core.dll"; Src = (Join-Path $srcCache "BepInEx.Cache.Core.dll") }
)

function Deploy-To([string]$destCore) {
	Write-Host ""
	Write-Host "Деплой в: $destCore"

	foreach ($t in $targets) {
		$copied = Copy-FileIfExists $t.Src $destCore
		if ($copied) {
			$item = Get-Item -LiteralPath (Join-Path $destCore $t.Name)
			Write-Host ("  OK  {0}  ({1} bytes, {2})" -f $t.Name, $item.Length, $item.LastWriteTime)
		}

		if ($CopyPdb) {
			$pdb = [System.IO.Path]::ChangeExtension($t.Src, ".pdb")
			Copy-FileIfExists $pdb $destCore | Out-Null
		}

		if ($CopyXml) {
			$xml = [System.IO.Path]::ChangeExtension($t.Src, ".xml")
			Copy-FileIfExists $xml $destCore | Out-Null
		}
	}
}

Deploy-To $gameCore

if ($AlsoCopyRuntime) {
	$runtimeCore = Join-Path $repoRoot "runtime\BepInEx\core"
	Assert-Path $runtimeCore "runtime/BepInEx/core"
	Deploy-To $runtimeCore
}

Write-Host ""
Write-Host "Готово."

