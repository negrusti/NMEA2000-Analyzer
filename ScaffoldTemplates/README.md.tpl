# {{ProjectName}}

Standalone C# / PCAN scaffold generated from a capture for `{{DeviceName}}` at source address `{{SourceAddress}}`.

## What was generated

- Reusable PCAN transport wrapper in `PcanBus.cs`
- CAN ID parsing/building plus fast-packet assembly/disassembly in `Nmea2000Protocol.cs`
- A reusable device-emulator base class in `DeviceEmulatorBase.cs`
- A generated device class with one stub per observed PGN in `{{EmulatorClassFileName}}`
- `ObservedTraffic.json` with the PGNs, counts, peers, intervals, and sample payloads from the capture
- `logs/raw-device-records.json` with raw frames related to this device
- `logs/assembled-device-records.json` with assembled packets related to this device

## Observed traffic

- Transmitted PGNs: {{TransmittedPgnCount}}
- Received PGNs: {{ReceivedPgnCount}}

## Logs

- [ObservedTraffic.json](ObservedTraffic.json)
- [Raw device records](logs/raw-device-records.json)
- [Assembled device records](logs/assembled-device-records.json)
- [Runtime trace](logs/tracer.log)

## Suggested next steps

- Replace the sample payload builders with typed serializers for the PGNs you care about first.
- Keep the generated transport/protocol files generic; put device behavior changes in the generated device class.
- Add unit tests around the fast-packet helpers before changing them.
- Implement ISO Request, Address Claim, and Product Information first; they are usually what makes a device visible to an MFD.
- Move per-installation values such as source address, serial number, and model strings into `appsettings.json`.
- Capture a known-good bus trace from the real device and compare emitted frames against it as you flesh out each PGN.
- If the device has multiple firmware variants, keep one `ObservedTraffic.json` snapshot per variant and diff them before refactoring handlers.
