# Scores an evaluation run. For each paired case, the mean absolute difference between the
# two variants across RGB, as a percentage of full scale.
#
#   powershell -File eval/score.ps1 -Models illustrious,pony,noobai,flux2-klein
#
# The number answers "did changing this one variable change the image, and by how much".
# It says nothing about whether the change was the right one -- that is what the contact
# sheet is for. A near-zero score is the interesting result: it means the variable is inert.

param([string]$Models = "illustrious", [string]$Root = "eval/out")

Add-Type -AssemblyName System.Drawing

function Get-Pixels($path) {
    $bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $path).Path)
    $rect = New-Object System.Drawing.Rectangle(0, 0, $bmp.Width, $bmp.Height)
    $data = $bmp.LockBits($rect, 'ReadOnly', 'Format32bppArgb')
    $len = $data.Stride * $bmp.Height
    $buf = New-Object byte[] $len
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $len)
    $bmp.UnlockBits($data)
    $result = @{ Buffer = $buf; Stride = $data.Stride; Width = $bmp.Width; Height = $bmp.Height }
    $bmp.Dispose()
    $result
}

function Get-MeanDiff($pathA, $pathB) {
    $a = Get-Pixels $pathA
    $b = Get-Pixels $pathB
    if ($a.Width -ne $b.Width -or $a.Height -ne $b.Height) { return $null }

    [long]$total = 0
    [long]$count = 0
    # Every 3rd pixel on each axis. Sampling rather than exhaustive because this is a
    # comparison between models, and the sample is identical for all of them.
    for ($y = 0; $y -lt $a.Height; $y += 3) {
        $row = $a.Stride * $y
        for ($x = 0; $x -lt $a.Width; $x += 3) {
            $i = $row + $x * 4
            $total += [math]::Abs($a.Buffer[$i] - $b.Buffer[$i])
            $total += [math]::Abs($a.Buffer[$i + 1] - $b.Buffer[$i + 1])
            $total += [math]::Abs($a.Buffer[$i + 2] - $b.Buffer[$i + 2])
            $count += 3
        }
    }
    if ($count -eq 0) { return $null }
    [math]::Round(100.0 * $total / $count / 255.0, 2)
}

$cases = (Get-Content "eval/cases.json" -Raw | ConvertFrom-Json).cases

foreach ($model in $Models -split ',') {
    $dir = Join-Path $Root $model.Trim()
    if (-not (Test-Path $dir)) { Write-Host "$model : not run"; continue }

    Write-Host ""
    Write-Host "=== $model"
    foreach ($case in $cases) {
        if ($case.visualOnly) {
            Write-Host ("  {0,-10} visual only" -f $case.id)
            continue
        }
        $a = Join-Path $dir "$($case.id)-a.png"
        $b = Join-Path $dir "$($case.id)-b.png"
        if (-not (Test-Path $a) -or -not (Test-Path $b)) {
            Write-Host ("  {0,-10} missing" -f $case.id)
            continue
        }
        $diff = Get-MeanDiff $a $b
        $note = if ($case.invert) { "(lower is better)" } else { "" }
        Write-Host ("  {0,-10} {1,6}%  {2}" -f $case.id, $diff, $note)
    }
}
