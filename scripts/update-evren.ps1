# scripts/update-evren.ps1
# ---------------------------------------------------------------------------
# evren-cli'ı kaynak koddan yeniden derleyip PATH'teki kurulumu günceller.
#
# Kurulum hedefi (varsayılan): %USERPROFILE%\.evren-cli\bin\evren-cli.exe
#
# Kullanım:
#   powershell -ExecutionPolicy Bypass -File scripts\update-evren.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\update-evren.ps1 -TargetDir "D:\bin"
#   powershell -ExecutionPolicy Bypass -File scripts\update-evren.ps1 -Clean
#
# Parametreler:
#   -TargetDir <path>  Güncellenecek klasör (varsayılan: %USERPROFILE%\.evren-cli\bin)
#   -Runtime   <rid>   Hedef platform (varsayılan: win-x64)
#   -Clean             Güncellemeden önce obj/bin/artifacts klasörlerini temizle
#   -SelfContained     Bağımlılıkları exe içine göm (varsayılan: açık)
#   -Aot               NativeAOT ile derle (varsayılan: kapalı, daha taşınabilir)
#
# Not: evren-cli şu an çalışıyor olsa bile güncelleme çalışır; Windows'ta
# çalışan bir exe üzerine yazılamaz ama YENİLENDİRİLEBİLİR. Script bu
# "rename & swap" tekniğini kullanır; eski .old dosyası sonraki çalıştırmada
# otomatik silinir.
# ---------------------------------------------------------------------------

[CmdletBinding()]
param(
    [string]$TargetDir = (Join-Path $env:USERPROFILE ".evren-cli\bin"),
    [string]$Runtime   = "win-x64",
    [switch]$Clean,
    [bool]$SelfContained = $true,
    [switch]$Aot
)

$ErrorActionPreference = "Stop"

# Repo kökünü script konumundan bul (scripts\..).
$repoRoot  = Split-Path -Parent $PSScriptRoot
$project   = Join-Path $repoRoot "evren-cli.csproj"
$stageDir  = Join-Path $repoRoot "artifacts\publish\$Runtime"
$exeName   = "evren-cli.exe"

Write-Host "==> evren-cli guncelleme basliyor" -ForegroundColor Cyan
Write-Host "    Kaynak   : $repoRoot"
Write-Host "    Hedef    : $TargetDir"
Write-Host "    Platform : $Runtime"

# --- Gereksinim kontrolleri -------------------------------------------------
if (-not (Test-Path $project)) {
    throw "Proje dosyasi bulunamadi: $project"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "'dotnet' komutu yok. .NET SDK kurulu mu kontrol edin: https://dotnet.microsoft.com/download"
}

# --- Temizle (opsiyonel) ------------------------------------------------------
if ($Clean) {
    Write-Host "==> Temizleniyor (obj/bin/artifacts)..." -ForegroundColor DarkGray
    foreach ($d in @("bin", "obj", "artifacts")) {
        $p = Join-Path $repoRoot $d
        if (Test-Path $p) { Remove-Item $p -Recurse -Force }
    }
}

# --- Publish ------------------------------------------------------------------
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

$publishArgs = @(
    "publish", $project,
    "-c", "Release",
    "-r", $Runtime,
    "-o", $stageDir,
    "--nologo",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true"
)
if ($SelfContained) { $publishArgs += "--self-contained=true" } else { $publishArgs += "--self-contained=false" }
if ($Aot)           { $publishArgs += "-p:PublishAot=true" } else { $publishArgs += @("-p:PublishAot=false", "-p:PublishTrimmed=false") }

Write-Host "==> Publish ediliyor..." -ForegroundColor Cyan
Write-Host "    dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish basarisiz (cikis kodu: $LASTEXITCODE)"
}

$built = Join-Path $stageDir $exeName
if (-not (Test-Path $built)) {
    throw "Derlenmis executable bulunamadi: $built"
}

# --- Eski surumu degistir (Windows rename-and-swap teknigi) --------------------
New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
$target  = Join-Path $TargetDir $exeName
$stale   = Join-Path $TargetDir "evren-cli.exe.old"

# Onceki guncellemeden kalan .old dosyasini silmeyi dene (artik kilitli degildir).
if (Test-Path $stale) {
    Remove-Item $stale -Force -ErrorAction SilentlyContinue
}

if (Test-Path $target) {
    # Calisan exe'nin uzerine yazilamaz ama yeniden adlandirilabilir.
    $renamed = $false
    $backup  = Join-Path $TargetDir ("evren-cli.exe.{0:yyyyMMdd-HHmmss}.bak" -f (Get-Date))
    for ($i = 0; $i -lt 5 -and -not $renamed; $i++) {
        try {
            Move-Item $target $backup -Force -ErrorAction Stop
            $renamed = $true
        }
        catch {
            Start-Sleep -Milliseconds 400
        }
    }
    if (-not $renamed) {
        throw "Hedef dosya kilitli ve yeniden adlandirilamadi: $target"
    }
    Write-Host "==> Eski surum yedeklendi -> $backup" -ForegroundColor DarkGray
}

# Yeni exe'yi yerlestir (birka kez dene; antivirus taramasi gibi gecici kilitler).
$placed = $false
for ($i = 0; $i -lt 5 -and -not $placed; $i++) {
    try {
        Copy-Item $built $target -Force
        $placed = $true
    }
    catch {
        Start-Sleep -Milliseconds 400
    }
}
if (-not $placed) {
    throw "Yeni exe yerlestirilemedi: $target"
}

# --- Dogrula ----------------------------------------------------------------
Write-Host "==> Dogrulaniyor..." -ForegroundColor Cyan
try {
    $versionOutput = & $target --version 2>&1
    if ($LASTEXITCODE -ne 0) { throw "cikis kodu $LASTEXITCODE" }
}
catch {
    throw "Guncellenen binary calismadi ($_). Yedekten geri yuklemek icin: Move-Item $backup $target -Force"
}

Write-Host ""
Write-Host "  Guncellendi : $target" -ForegroundColor Green
Write-Host "  Surum       : $versionOutput" -ForegroundColor Green
Write-Host ""

# PATH kontrolu.
$pathDirs = $env:PATH -split ';'
if ($pathDirs -notcontains $TargetDir) {
    Write-Host "  ! Not: '$TargetDir' PATH'te gorunmuyor. Eklemek icin:" -ForegroundColor Yellow
    Write-Host "      [Environment]::SetEnvironmentVariable('PATH', `"`$(`$env:PATH);$TargetDir`", 'User')" -ForegroundColor Yellow
}

# Cagriyi script'i calistiran evren-cli icinden yapiyorsa bilgilendir.
if (Get-Process -Name "evren-cli" -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $target }) {
    Write-Host "  ! evren-cli su an calisiyor; yeni surum bir sonraki baslatmada etkin olacak." -ForegroundColor Yellow
}
