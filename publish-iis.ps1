param(
    [string]$OutputPath = "$PSScriptRoot\publish"
)

$ErrorActionPreference = 'Stop'

dotnet publish "$PSScriptRoot\Vigear.MultiWarehouse.Api.csproj" `
  --configuration Release `
  --runtime win-x64 `
  --self-contained false `
  --output $OutputPath

Write-Host "Da tao goi IIS tai: $OutputPath"
Write-Host "Truoc khi khoi dong website, dat bien moi truong bao mat trong IIS va cap nhat OAuth Redirect URL trong Partner Center."
