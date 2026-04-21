# Changelog

## 1.4.5 - 2026-04-21

- Fixed raw 29-bit CAN PGN extraction across candump, YDWG, PCAN-View, and Yacht Devices imports so data-page PGNs such as `126208` are no longer misclassified.
- Corrected PDU1 destination stripping for data-page PGNs when deriving PGNs from CAN identifiers.

## 1.4.4 - 2026-04-19

- Added active log filename suffixes to Devices, PGNs, and Alarms window titles.
- Added PGN and device counts to the statistics totals footers.
- Fixed right-click handling so multi-row selections are preserved, multi-row-only filters include values from all selected rows, and single-row reference actions are disabled for multi-selection.
- Improved Actisense NGT-1 capture throughput by increasing the serial buffer, reading in chunks, and deferring decoded packet formatting off the read loop.
- Changed Actisense NGT-1 startup control traffic to send the captured one-time Actisense control burst instead of periodically resending the old startup frame.
- Improved unavailable lookup decoding for fields that use all-bits-set or range-limit sentinel values.

## 1.4.3 - 2026-04-17

- Added Actisense EBL binary log parsing and Actisense serial capture support.
- Added a reload-definitions action and MCP hook so updated local/custom definitions can be applied without restarting.
- Expanded proprietary decode coverage, including Seatalk1 embedded payloads, additional local PGN definitions, and Victron VREG register enrichment from public Victron documentation.
- Added `victron.json` and Victron register decoding for PGN `61184`, including device state/mode, AC current limits, remote-control registers, and consumed Ah.
- Improved selected-packet presentation by removing duplicate PGN/description/type fields from decoded details and rendering nested decoded subdata as YAML instead of compact JSON strings.
- Improved alert value repeated-field decoding, including `STRING_LAU` alert values such as on/off state payloads.

## 1.4.2 - 2026-04-09

- Fixed signed 64-bit numeric decoding, which corrected fields such as `Longitude` in `129029`.
- Stopped rendering zero-valued `DATE` fields as fake epoch dates like `1970-01-01`.
- Fixed repeated-field-set stepping and count capping so truncated `129029` payloads no longer produce impossible repeated reference-station lists.
- Applied canboat numeric `Offset` during decoding, fixing vendor PGNs such as `65016 Utility Total AC Power`.
- Reduced alarm-history noise by only keeping `Cleared` events such as `Alarm condition not met` when they actually clear a previously active alarm.

## 1.4.1 - 2026-04-08

- Broadened the MCP reverse-engineering candidate list to include proprietary manufacturer-specific PGNs such as `MFG-Specific ...`, not just fully unknown PGNs.
- Mitigated truncated trailing `STRING_LAU` decoding for final-field AIS payloads, fixing `129041` AtoN names like `NG3 WINDFARM` without relaxing validation for non-terminal string fields.

## 1.4.0 - 2026-04-08

- Added an MCP analysis server for the currently open data, including file open, packet querying, packet context, byte-variability, packet highlighting, and UI selection readback.
- Added reverse-engineering MCP tools for packet-variant grouping, correlated-packet discovery, packet-pair comparison, and UI-equivalent Include/Exclude PGN filters.
- Migrated the MCP transport from a custom raw TCP socket to Streamable HTTP on localhost, backed by `mcp.json` configuration and self-describing runtime discovery via `get_server_guide` and richer `tools/list` metadata.
- Improved proprietary PGN decoding support for variable strings, dynamic fields, partial group-function decoding, and indirect lookup handling.

## 1.3.13 - 2026-04-08

- Added an `Alarms` menu and alarm history window built from assembled alarm and alert PGNs.
- Added a `Request device info` button to the PCAN capture dialog to broadcast an ISO Request for `126996` once per capture session.
- Fixed window-title updates for PCAN capture and candump saves, and switched PCAN capture timestamps to full absolute local timestamps with millisecond precision.
- Improved PGN search to operate on the currently visible grid and highlight the matched row correctly.
- Implemented shared `INDIRECT_LOOKUP` decoding for PGNs such as `60928` and `65240`.
- Improved statistics-window discoverability with footer hints and added `Packet Graph` to the Devices row context menu alongside `Supported PGNs`.

## 1.3.12 - 2026-04-08

- Fixed CSV row export to omit internal object/type fields and support exporting multiple selected rows.
- Improved PGN decoding for variable strings, partial proprietary group-function parameter decoding, and repeated PGN-list fields like `126464`.
- Added `Supported PGNs` to the Devices statistics window, showing per-device transmit and receive PGN lists from `126464`.
- Appended PGN descriptions to supported-PGN list entries when unambiguous and tightened the list scrollbars.
- Documented the supported-PGN list window in the README and added a screenshot.

## 1.3.11 - 2026-04-05

- Refined traffic graph zooming to use discrete time-per-bar steps with fixed bar width and a dynamic zoom-out limit based on full-log fit.
- Simplified graph tooltips to show only packet counts and aligned graph label sizing with the time-axis text.
- Documented timestamp requirements for graphs and added a graph screenshot to the README.

## 1.3.10 - 2026-04-05

- Added zoomable packet-rate graphs for both `Devices` and `PGNs`, opened by double-clicking rows in the statistics windows.
- Reworked the graph interaction and presentation: fixed-width bars, no grid lines, lighter shell styling, and less overlapping X-axis labels.
- Added `highlight.json` support for PGN-based row background highlights, with default address-claim and group-function entries.
- Documented graph access and `highlight.json` in the README.
- Removed the `cli-last-run.txt` diagnostic trace from command-line mode.

## 1.3.9 - 2026-04-05

- Expanded the Devices statistics window to include all observed source addresses, even when no device info PGNs were decoded.
- Added per-device `Avg Bps` and `Peak Bps` columns based on assembled packets when timestamps are available.
- Added a footer row in the Devices statistics window with totals for `Raw Packets`, `Assembled Packets`, `Avg Bps`, and `Peak Bps`.
- Highlighted total `Avg Bps` and `Peak Bps` values in dark red when they exceed `18000`.
- Added zoomable packet-rate graphs for `Devices` and `PGNs`, opened by double-clicking rows in the statistics windows.
- Added optional `highlight.json` support to color PGN rows, with default highlights for address-claim and group-function traffic.
