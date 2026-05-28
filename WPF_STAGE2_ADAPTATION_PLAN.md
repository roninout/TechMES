# WPF reference and WEB adaptation plan

Source archive: `D:\C_Recipe\Work\TechEquipments\TechEquipments.zip`

Analysis date: 2026-05-27

This document is a compact reference for continuing the WPF-to-WEB migration. It records what the WPF project contains, what is already implemented in the WEB project, and the recommended next steps.

## Current WEB status

- Equipment catalog is implemented in WEB: station/type/search filters, tree/list, favorites, QR selection, footer diagnostics.
- Info module is mostly implemented in WEB: summary card, supplier/product code/logo, first image preview, description/images/pdf/schemes/notes/QR tabs, DB-backed favorites, notes, file streaming and PDF view-state.
- Messages module is already implemented in WEB and uses the existing PostgreSQL tables through the Runtime service.
- QR module is implemented in WEB through browser camera access, QR generation/download and equipment selection.
- Param module is implemented as read-only foundation: Runtime endpoints for snapshot/trend, WEB tabs for Graph/Values/Alarm and placeholders for technical pages, Apache ECharts trend rendering with touch pan/zoom.

## Important WPF behavior to preserve

### Param

- WPF polls params only while the Param view is active. It stops unnecessary Citect reads when the user leaves the Param tab.
- Polling period is 5 seconds.
- Param reads and writes are serialized through one read/write gate.
- Read refresh is paused briefly after write operations.
- Different equipment type groups map to different raw models:
  - AI, DI, DO
  - Motor
  - ATV
  - VGA, VGA_EL, VGD
- Param pages are type-dependent:
  - Motor: PLC, DI/DO, Alarm, TimeWork, DryRun, ATV
  - ATV: ATV, Alarm
  - VGA_EL/VGD: PLC, DI/DO, Alarm
- Trend logic supports live and history modes, left-side history loading, visual-range preservation, and MinR/MaxR scaling for analog values.
- WPF graph uses a left value bar, grid, area/step area series, vertical cursor and a scrollable time window.

### Info

- WPF stores info in the existing `srd_db` tables and keeps file data in PostgreSQL.
- WPF has photo/PDF/scheme libraries and links equipment to library files.
- WPF remembers PDF position in a separate DB table.
- WPF notes are a collection, not a single note.
- WPF has extra maintenance workflows: import files, sync libraries, product code options, supplier/order data and camera capture.

### Messages

- WPF message logic is similar to current WEB: active/all filter, viewed state, edit/add/delete, refresh timer.
- Current WEB implementation already covers the main operator scenario.

### QR

- WPF remembers preferred camera index in user state.
- WPF can scan QR and navigate/filter the equipment list.
- Current WEB scans QR in the browser and selects equipment directly. This is acceptable for WEB, but camera preference and HTTPS deployment still need final deployment work.

### SOE

- WPF has an SOE controller that reads trend/event data for selected equipment and decodes changed status bits.
- SOE is not yet migrated to WEB.

## Main differences found

| Area | WPF | Current WEB | Gap |
| --- | --- | --- | --- |
| Equipment | Rich list filtering and remembered selection | Implemented | Minor state persistence polish |
| Info | Full data/library/edit workflows | Operator view/edit implemented | Maintenance/import workflows are not migrated |
| Messages | Working PostgreSQL workflow | Implemented | Optional UI polish only |
| QR | Camera scan, generation, preferred camera | Implemented in browser | HTTPS/tablet camera deployment and camera preference |
| Param snapshot | Full raw model read/write | Read-only snapshot implemented | Write logic intentionally not migrated |
| Param graph | Live/history, cursor, scroll, chunk history | ECharts implemented | Smooth prefetch/history refinement |
| Param pages | PLC, DI/DO, DryRun, ATV, Alarm, TimeWork | Values/Alarm plus placeholders | Need read-only page endpoints and UI |
| SOE | Implemented | Not implemented | Need endpoint and UI |

## Recommended next adaptation steps

1. Stabilize Param read-only core.
   - Keep `AllowWrites=false`.
   - Keep polling only when Param/Graph is visible.
   - Move more WPF param definitions into structured backend definitions where needed.
   - Make the configured trend windows explicit in `appsettings.json`, using a safe history size for Citect.

2. Add read-only Runtime endpoints for WPF Param reference pages.
   - `GET /api/param/{equipmentName}/refs/plc`
   - `GET /api/param/{equipmentName}/refs/dido`
   - `GET /api/param/{equipmentName}/refs/dryrun`
   - `GET /api/param/{equipmentName}/refs/atv`
   - Reuse WPF logic from `ParamRefsController`: `TabPLC`, `TabDIDO`, `WinOpened`, dry-run DI/AI links and motor-to-ATV link.

3. Implement WEB UI for Param technical pages.
   - Replace current placeholders with read-only tables/cards.
   - PLC: tag, item, type/comment, value, unit, forced state.
   - DI/DO: linked equipment cards with type badge, value/forced/alarm state and click-to-select navigation.
   - DryRun: linked DI/AI state.
   - ATV: linked ATV values for motors.

4. Finish Param graph behavior.
   - Keep Apache ECharts.
   - Preserve WPF-like live/history mode.
   - Smoothly prefetch older history before the user reaches the left edge.
   - Keep MinR/MaxR as the analog base axis source.
   - Keep history requests small enough for Citect reliability.

5. Add SOE module after Param technical pages.
   - Port WPF decoding from `EquipmentService.GetDataFromEquipAsync`, `GetTrnByEquipment`, `GetChangedBitCode`.
   - Add Runtime endpoint for selected equipment.
   - Add WEB view under the selected equipment, probably as its own top menu mode like Param.

6. Add user-state persistence.
   - Remember last selected menu mode, station/type filter, search text, selected equipment, selected Param tab, trend live/history state and preferred camera.
   - Use browser local storage first; DB user profiles can come later.

7. Leave maintenance workflows for the future service WPF app unless explicitly needed in WEB.
   - Bulk imports.
   - Library synchronization.
   - Supplier/order/product-code administration.
   - Service start/stop and diagnostic tools.

8. Plan write mode as a separate controlled stage.
   - Keep it disabled for now.
   - When needed, migrate WPF privilege checks, confirmation dialogs, read/write gate, audit logging and `AllowWrites` configuration.

## Best next implementation step

Start with read-only Param reference pages: PLC and DI/DO. They are already modeled in the WPF project, they fill the biggest visible gap in WEB Param, and they do not require enabling writes.
