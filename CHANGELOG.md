# Changelog

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
