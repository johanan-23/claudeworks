---
name: solidworks-overview
description: Use whenever the user asks to create, edit, inspect or export anything in SOLIDWORKS (parts, assemblies, drawings) through the ClaudeWorks tools (solidworks-addin MCP server). Explains units, the context-act-verify loop, references, undo and safety rules.
---

# SOLIDWORKS with ClaudeWorks: overview

Tools drive the user's running SOLIDWORKS. All lengths are **millimetres**, angles **degrees** (except `invoke_api`, which uses raw API units: metres/radians).

## The loop
1. `get_solidworks_context` first: active document, feature tree, current selection (`S1`, `S2`...). If SOLIDWORKS is not running call `launch_solidworks`; check health with `sw_status`.
2. Act with one modifying tool at a time (`begin_sketch`, `add_*`, `end_sketch`, `extrude`, ...). Each modifying call is one undo step.
3. Verify with `capture_view` and `get_model_info` / `list_features` after every meaningful step. Do not assume a feature worked: read the result and `get_rebuild_errors`.
4. If something is wrong, `undo_last_change` (or `delete_feature`) rather than stacking fixes.

## References, not coordinates
Prefer references over guessing positions: the user's selection is `S#`; `find_faces` returns `F#`, `find_edges` returns `E#`. Pass them in `refs`. Feature, plane and component names also work. Use `clear_selection` before building a new selection.

## Rules
- Never `save_document`, overwrite or delete files unless the user asked. Say what you changed.
- Name important features with `rename_feature`.
- `invoke_api` is an advanced escape hatch, off by default, and every call needs user approval. Use dedicated tools first.
- A modal dialog or a busy SOLIDWORKS makes calls fail or time out: tell the user, do not retry in a loop.
- Not covered by the tools: sheet metal, weldments, surfacing beyond loft/sweep, simulation, PDM, macros. Say so instead of improvising.

See `references/workflow.md` for a worked example.
