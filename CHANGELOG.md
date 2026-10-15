# Changelog

## 1.5.0 - 2026-10-15

- License: **GNU GPL v3 or later** (instead of MIT, before the first public release).
- Settings are now stored in **`GPUTool.ini` next to the exe** (portable, same file for all user accounts). If that folder is not writable, `%APPDATA%\GPUTool\GPUTool.ini` is used. Settings of older versions (`%APPDATA%\GPUTool\settings.txt`) are taken over automatically.
- Window position and size, maximized state, compact graph view, selected tab and selected GPU are restored on the next start. A saved position that is no longer on any screen is ignored.
- Settings are also saved right after moving or resizing the window.

## 1.4.2 - 2026-10-15

- Settings and Logging tabs: label, number box and button of a row are now centred on the same middle line ("New limit", "Interval").

## 1.4.1 - 2026-10-15

- Settings: transparency options laid out as a grid (opacity box left, check boxes right, aligned), slightly taller start window so all options are visible.

## 1.4.0 - 2026-10-15

- Curve colours selectable: click the coloured name below the graph (the box still shows/hides the curve). *Reset colours* in the graph's right-click menu.
- Visible curves are remembered.
- Settings: **smart transparency**. The window fades to the chosen opacity (20-95 %) while another program is active and the mouse is not over it, and is fully opaque again on hover. Optionally only in the compact graph view (default).

## 1.3.0 - 2026-10-15

- Compact mode without title bar: move the window by dragging the graph, resize it at the thin frame.
- Right-click menu on the graph: *Normal view* (compact mode), *Always on top* (remembered), *Close*.

## 1.2.1 - 2026-10-15

- Compact mode can be made narrower (min. ~150 px). Axis margins now fit the actual labels; the watt axis has no labels while the power curve is hidden.
- Compact header shows power, temperature, load and VRAM (clock left out); values that don't fit are dropped, units only appear when there is room.

## 1.2.0 - 2026-10-15

- Graph: new time range **60 min** (history is kept for 60 min, roughly 100 bytes per sample and GPU, i.e. well under 1 MB).
- **Compact graph-only mode**: on the Graph tab the window can be made smaller than the normal layout; then only the graph, the time range box and the current values (coloured like the curves) are shown. Enlarging the window switches back.
- Curves with more samples than pixels are combined per pixel column (min/max kept), so long ranges stay fast and spikes stay visible.
- Auto scale: more round step sizes, percent/temperature axes no longer run past 100 when the data doesn't.

## 1.1.0 - 2026-10-15

- Graph: optional **auto scaling** of both axes (checkbox *Auto*, on by default, remembered). Fits the visible data with headroom and round grid steps; zooms out immediately when a value would leave the plot, zooms in only after 20 s.
- Axis rescales when switching GPU, time range or visible series.

## 1.0.0 - 2026-10-15

First public release.

- Set the power limit per GPU (slider or number field), with confirmation
- Live values: power draw, power limit, temperature, load, SM clock, VRAM
- History graph (1 / 5 / 15 min) for power, temperature, load, clock and VRAM, each series can be toggled
- Data logging of all GPUs to a tab-separated text file (path and interval selectable, default 5 s, off on first start)
- Autostart task via Windows Task Scheduler to re-apply the power limit after every boot
- Startup self-check (.NET, admin rights, nvidia-smi, driver, Task Scheduler); the check window only appears on errors, details always via the `?` / `!` button
- Resizable, DPI-aware window
- App/crash log next to the exe (fallback: `%TEMP%`)
