---
name: solidworks-troubleshooting
description: Use when a SOLIDWORKS tool call fails, a feature does not appear, rebuild errors occur, nothing is selected, a reference cannot be resolved, or the resulting body is empty.
---

# Troubleshooting

1. `get_rebuild_errors` and `list_features` show which feature is broken.
2. "Nothing selected" or unresolved reference: `get_selection`, `clear_selection`, then reselect with `find_faces` / `find_edges` / `select_entities`. References change after rebuilds; re-query them.
3. Failed feature: `undo_last_change`, or `suppress_feature` / `delete_feature` the bad one and redo with corrected input (closed profile, correct plane, valid distance).
4. Empty body or no change: confirm `end_sketch` ran and the sketch is on the intended plane; `capture_view`.
5. SOLIDWORKS busy, modal dialog, or add-in not loaded: `sw_status`; ask the user to close the dialog or start SOLIDWORKS. Do not loop retries.
6. `rebuild` after edits to force an update.
