# BattleBit-GSS
BattleBit Game Server Service

Handles starting the server and auto updating.

## Requirements

### SteamCMD

You need steamcmd installed somewhere and logged in once with an account that owns BattleBit to allow the service to function properly.

### Configuration

All the configuration of the service itself is inside of App.config:

`steam_username`: The required system-scope environment variable name that represents your steam username, to be able to update the game server.

`gss_syslog_name`: The optional system-scope environment variable name that represents the server name of your syslog server, to send messages to syslog from your service. Game Server output is also redirected.

`gss_interval`: Configure the interval for when the service checks for service updates (ms)

`battlebit_exe`: The file name and suffix of the BattleBit executable. Should remain unchanged for most cases.

`battlebit_app_id`: The steam app id for BattleBit. Should remain unchanged for the foreseeable future.

`battlebit_dir`: The directory you wish to install the server files.

`battlebit_temp_dir`: The directory you wish to cache downloaded server files to check for updates. 

`steamcmd_file_path`: The full file path to steamcmd executable.

### Server Arguments

Default arguments that are passed to the server are `-batchmode -nographics`. To pass in more, read below.

Wherever you sc create/start the GSService, you must also have a `ServerArgs.json` for personal arguments to pass to server startup. Consult the discord for more information regarding arguments.
For example:
```
{
  "Name": "The best server",
  "AntiCheat": "eac",
  "LocalIp": "0.0.0.0",
  "Port": 29595,
  "Hz": 60
  "ApiEndpoint": "127.0.0.1"
  "ApiToken": "1234"
  "Password": "password"
}
```
