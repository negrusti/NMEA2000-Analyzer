# NMEA2000-Analyzer

NMEA2000 log analyzer for Windows, using the canboat JSON definitions for PGN decoding.

![App Screenshot](images/screenshot1.png)

## Platform

Windows 10/11 only

## Supported log formats

* TwoCan CSV
* Actisense
* CanDump
* Yacht Devices Wireless Gateway
* PCANView
* Yacht Devices CSV

## PGN Definitions

Custom PGN overrides can be added in `local.json`.

## Presets

You can define your own presets in `presets.json`.

## Live capture

Supported with CANable-compatible boards using the PCAN driver.

## Views

The main grid can switch between:

* `Assembled` packets
* `Unassembled` raw frames

## Export

`File -> Save as candump` exports the full unassembled capture in candump format.

## Command line

You can open a file directly from the command line:

```powershell
NMEA2000Analyzer.exe "C:\path\to\capture.log"
```

The file is loaded on startup using the same detection logic as `File -> Open`.
