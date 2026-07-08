# SVSAP / SVSAPME Ver1.4.0Alpha1

Internal version: `1.4.0-alpha.1`

This Alpha build prepares the 1.4.0 line for wider testing. The new machine content belongs to SVSAPME.

## Highlights

- Added SVSAPME standalone Single-Block Keg machines: Copper, Steel, Gold, Iridium.
- Added SVSAPME standalone Single-Block Cask machines: Copper, Steel, Gold, Iridium.
- Processor capacities are 16 / 64 / 144 / 256 internal slots.
- Added processor grid GUI with per-slot product, progress, ETA, and collect controls.
- Added processor input buffers and compact GUI layout bounds checks for storage drives, import/export menus, farms, kegs, and casks.
- Processor network auto-pull now defaults to off. Existing schema v3 migration disables only the old unsafe all-eligible empty-filter default while preserving intentional player filters.
- Single-Block Kegs use the vanilla flavored Wine/Juice creation path, and Keg ETA now uses a documented 1200 in-game minutes per day progression budget.
- Farmhands now get host-backed remote Storage Drive and Importer/Exporter configuration menus instead of local empty menus.
- Farmhand processor deliveries and consuming machine actions now use durable host/client acknowledgement and escrow paths to reduce disconnect loss/duplication windows.
- Fixed unfinished processor recovery: breaking, reclaiming, or overflowing an unfinished Keg/Cask slot now returns the input item instead of creating the future output early.
- Kept SVSAP and SVSAPME version fields aligned at `1.4.0-alpha.1`.

## Validation

- SVSAP Debug/Release build: PASS, 0 warnings, 0 errors.
- SVSAPME Debug/Release build: PASS, 0 warnings, 0 errors.
- i18n parity: SVSAP `570/570`, SVSAPME `371/371`.
- SVSAP selftest: `31/31`.
- SVSAPME selftest: `37` implemented cases passed.
- FullMatrix E2E: `45/45`, with SVSAPME BigCraftables `types=37`.
- P0/P1 single-player E2E: PASS.
- P0/P1 multiplayer E2E: PASS.
- SVSAP RouteA multiplayer E2E: PASS.

## Install

1. Install SMAPI 4.5.2 or newer.
2. Install `SVSAP 1.4.0-alpha.1.zip`.
3. Install `SVSAPME 1.4.0-alpha.1.zip` if you want the machine and energy extension.
4. All multiplayer players should use the same SVSAP and SVSAPME versions.

Back up saves before testing this Alpha build.
