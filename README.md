# GG-Downloader-Windows

Download files from GOG Games CDN.

To access, a "cdn.gog-games.com" username/password is required.

## TL:DR I'm dumb
1. Download the latest release (the .zip file found [here](https://github.com/GOG-Games-com/GG-Downloader-Windows/releases)) and extract
2. Open a Command Prompt or PowerShell terminal in the location of `gg-downloader.exe`
3. Authenticate: `./gg-downloader.exe auth`
3. Download: `./gg-downloader.exe download https://gog-games.com/game/arcade_paradise` (replace url for another game) or `./gg-downloader download arcade_paradise` (replace slug for another game)
4. Entered wrong creds for auth? `./gg-downloader.exe reset`

## Usage
`./gg-downloader [command] [options]`

**Options**

`--version` Show version information

`-?`, `-h` or `--help` Show help and usage information

**Commands**

`donate` Open the GOG Games Store link for CDN access

`user <username>` Set your GOG Games CDN username

`password <password>` Set your GOG Games CDN password

`reset` Reset username/password config

`webroot <url>` Set the GOG Games CDN root address

`auth` Check authentication and exit
 
`latest` List added/updated on GOG Games CDN

`Latest` List slugs of added/updated on GOG Games CDN

`download <slug-or-url>` download a game from the GOG Games CDN. See below for more download specific options.

**Download Command**

In addition to the options above, the download command has several additional optional options.

`-u` or `--username <username>` Override your set GOG Games CDN username for this download.

`-p` or `--password <password>` Override your set GOG Games CDN password for this download.
  
`-w <cdn-root>` Override the default GOG Games CDN root for this download.

`-n` or `--no-dir` Do not put files in a sub-directory [default: False]

`--unsafe` Do not verify integrity of downloads [default: False]
  
`--goodies` Download the goodies [default: True]

`--patches` Download the patches [default: False]

`--game` Download the game files [default: True]

**Examples**

Authenticate against the CDN and set the username and password for the first time: `gg-downloader auth`

Download _Cyberpunk 2077_ with patches, but without goodies: `gg-downloader download cyberpunk_2077_game --patches:true --goodies:false`

Download _The Ultimate Doom_ into the current working directory without validating the files: `gg-downloader download the_ultimate_doom_game --no-dir --unsafe`

Get the latest releases from the GOG-Games feed: `gg-downloader latest` or `./gg-downloader Latest`

When you fuck up and break your authentication or root CDN because you couldn't stop fiddling with things and just want to start over: `gg-downloader reset`

# Requirements for binary release

* .NET Framework 4.8 runtime. You probably have this, but if you don't [you can grab it from here](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48).
* User executing the tool needs to have write access to the folder the `gg-downloader.exe` executable exists in.

# Requirements for building from source

* .NET Framework 4.8 SDK/Developer Pack. If you have Visual Studio, you probably have this, but if you don't [you can grab it from here](https://dotnet.microsoft.com/en-us/download/dotnet-framework/thank-you/net48-developer-pack-offline-installer). Build with `dotnet build -f:net48 -c:Release`

OR

* .NET 7.0 SDK. You [can grab it from here](https://dotnet.microsoft.com/en-us/download/dotnet/7.0) if you don't have it. Build with `dotnet build -f:net7.0 -c:Release`

# How to build
Compile with a .NET SDK (supports .NET Framework 4.8 or .NET 7.0).
 1. To compile with the .NET Framework 4.8, from the root source folder, execute:
 `dotnet build -f:net48 -c:Release`
 2. To compile with the .NET 7.0 SDK, from the root source folder, execute 
 `dotnet build -f:net7.0-windows -c:Release`
 3. Will also compile for .NET 6.0 via adding the `net6.0` moniker to the `<TargetFrameworks>` property in `gg-downloader.csproj`. No other changes should 
