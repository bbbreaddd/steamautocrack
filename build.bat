@echo off
if "%1"=="--debug" (
    dotnet build SteamAutoCrack\SteamAutoCrack.csproj -c Debug -r win-x86
) else (
    dotnet publish SteamAutoCrack\SteamAutoCrack.csproj -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
)