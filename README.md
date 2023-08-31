# BattleBit-GSS
BattleBit Game Server Service

Handles starting the server and auto updating.

## Requirements

Wherever you sc create/start the GSService, you must also have a `ServerArgs.json` for personal arguments to pass to server startup. Consult the discord for more information regarding arguments.
For example:
```
{
  "Name": "The best server",
  "AntiCheat": "eac",
  "LocalIp": "0.0.0.0",
  "Port": 29595,
  "Hz": 60
}
```
