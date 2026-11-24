---
name: solidworks-drawings
description: Use when creating SOLIDWORKS drawings: create_drawing, standard/section/detail/auxiliary views, model dimensions, notes, BOM tables and exporting to PDF or DXF.
---

# Drawings

- `create_drawing` from the active part/assembly, then `add_drawing_view` (standard views), `add_section_view`, `add_detail_view`, `add_auxiliary_view`. `list_drawing_views` to check.
- `insert_model_dimensions` imports dimensions from the model; `add_note` adds text. Assemblies: `add_bom_table`.
- Export with `export_file` (PDF/DXF). Only save or export when asked.
- Verify layout with `capture_view`. Templates, sheet size and standards depend on the user's setup; ask if it matters.
- Not covered: custom sheet formats, GD&T symbols, revision tables.
