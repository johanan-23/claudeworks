---
name: solidworks-export-and-standards
description: Use when exporting SOLIDWORKS models (STEP, STL, PDF, DXF) or applying naming and custom-property conventions; includes an editable company-standards template.
---

# Export and standards

- `export_file` writes STEP/STL/PDF/DXF and similar; confirm the target path with the user and never overwrite existing files silently.
- Record metadata with `set_custom_property` (see `references/standards-template.md`) and verify with `get_custom_properties`.
- `save_document` only when asked.
