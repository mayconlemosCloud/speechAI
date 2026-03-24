# Tente carregar do .env se existir
if (Test-Path ".env") {
    Get-Content ".env" | ForEach-Object {
        $name, $value = $_.Split('=', 2)
        if ($name -eq "GEMINI_API_KEY") { $env:GEMINI_API_KEY = $value }
    }
}

$apiKey = $env:GEMINI_API_KEY

if (-not $apiKey) {
    Write-Host "GEMINI_API_KEY não encontrada no sistema ou no arquivo .env." -ForegroundColor Red
    $apiKey = Read-Host "Por favor, insira sua API Key"
}

$response = Invoke-RestMethod -Uri "https://generativelanguage.googleapis.com/v1beta/models?key=$apiKey"
$response.models | Select-Object name, displayName | Format-Table -AutoSize
