# Changelog

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
