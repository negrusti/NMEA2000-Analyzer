# AGENTS.md

## Goal
This project is a standalone NMEA 2000 device emulator scaffold generated from a real capture.
Preserve bus-visible behavior while gradually replacing sample payloads with typed implementations.

## File Roles
- `PcanBus.cs`: PCAN transport only.
- `Nmea2000Protocol.cs`: CAN ID parsing, fast-packet assembly/disassembly, protocol helpers.
- `DeviceEmulatorBase.cs`: generic runtime, scheduling, and common request/send plumbing.
- `{{EmulatorClassFileName}}`: all device-specific PGN behavior belongs here.
- `ObservedTraffic.json`: captured reference behavior. Use it before making assumptions.
- `logs/raw-device-records.json`: raw frame-level capture related to the device.
- `logs/assembled-device-records.json`: assembled packet-level capture related to the device.
- `logs/tracer.log`: runtime RX/TX trace from the generated emulator. Use it first when bus behavior differs from the original capture.
- `appsettings.json`: deployment-specific values such as source address and PCAN channel.

## Rules
- Do not remove or weaken fast-packet support.
- Do not move device-specific behavior into generic infrastructure.
- Do not change observed PGN directions, destinations, priorities, or cadence without an explicit reason.
- Prefer additive, local changes over large rewrites.
- Prefer protocol correctness over code elegance.
- Do not normalize away captured quirks until verified on real hardware or an MFD.

## When Implementing a PGN
- Match the observed PGN number, priority, and payload length first.
- Decode the sample payload from `ObservedTraffic.json` before redesigning the handler.
- Use named constants for fields once understood.
- If field semantics are unclear, keep the sample payload path and add a focused TODO on the unknown bytes.
- Preserve a way to emit the original sample payload for side-by-side comparison.

## ISO Requests and Identity
- Keep ISO Request handling explicit per PGN.
- Treat `60928`, `126464`, `126996`, and `126998` as high-risk identity PGNs; avoid behavior changes unless required.
- If device visibility on an MFD changes, inspect identity traffic first.

## Validation
- Build successfully.
- If possible, compare transmitted frames against the original capture.
- Inspect `logs/tracer.log` after each test run to confirm address claim, ISO Request handling, and fast-packet fragmentation on the wire.
- For fast packets, verify fragmentation and reassembly explicitly.
- Avoid changing unrelated PGNs while fixing one behavior.

## Refactoring Guidance
- Refactor only after behavior is stable on the bus.
- Extract helpers only when used by multiple PGNs.
- Avoid adding frameworks, DI containers, or unnecessary abstractions.
- Keep `PcanBus.cs`, `Nmea2000Protocol.cs`, and `DeviceEmulatorBase.cs` reusable across devices.
