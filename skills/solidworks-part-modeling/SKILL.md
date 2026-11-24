---
name: solidworks-part-modeling
description: Use when building or editing SOLIDWORKS part features: extrude, revolve, sweep, loft, shell, fillet, chamfer, hole_wizard, patterns, mirror, equations, materials and custom properties.
---

# Part modeling

- Build the base feature first (`extrude` or `revolve`), then add details. Put `fillet`/`chamfer` **last**; they break when upstream geometry changes.
- Get edge and face references with `find_edges` / `find_faces` instead of coordinates, then pass `refs`.
- Holes: `hole_wizard`, or sketch a circle and cut with `extrude`. Repeat with `linear_pattern`, `circular_pattern`, `mirror`.
- `shell` needs the faces to remove; `sweep` needs a profile and path sketch; `loft` needs two or more profiles.
- Dimensions: `list_dimensions`, `set_dimension` to adjust. Link values with `list_equations` / `add_equation`.
- Material: `set_material`. Metadata: `set_custom_property`, `get_custom_properties`.
- Name features (`rename_feature`), use `suppress_feature` to test, and `get_model_info` to confirm mass, volume and bounding box.
- After each step: `capture_view` and `get_rebuild_errors`. See solidworks-troubleshooting when a feature fails.
