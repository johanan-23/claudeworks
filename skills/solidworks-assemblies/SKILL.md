---
name: solidworks-assemblies
description: Use when working with SOLIDWORKS assemblies: inserting components, fixing the base part, mates, interference checks and configurations.
---

# Assemblies

- `new_document` (assembly) or `open_document`, then `add_component` for each part. `list_components` shows what is inserted.
- `fix_component` the first (base) component so it cannot move.
- Mate with `add_mate`; select the two faces/edges with `find_faces`/`find_edges` references first. Add mates one at a time and check `get_rebuild_errors`; over-defined mates are the common failure.
- `check_interference` before declaring an assembly done. `delete_component` removes a part from the assembly only (confirm with the user).
- `configurations` manages configurations; read the tool schema for its actions.
- Not covered: exploded views, motion studies, smart fasteners, large-assembly mode.
