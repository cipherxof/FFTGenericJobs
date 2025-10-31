# Set Working Directory
Split-Path $MyInvocation.MyCommand.Path | Push-Location
[Environment]::CurrentDirectory = $PWD

Remove-Item "$env:RELOADEDIIMODS/GenericJobs/*" -Force -Recurse
dotnet publish "./GenericJobs.csproj" -c Release -o "$env:RELOADEDIIMODS/GenericJobs" /p:OutputPath="./bin/Release" /p:ReloadedILLink="true"

# Restore Working Directory
Pop-Location