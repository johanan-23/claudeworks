---
name: solidworks-sketching
description: Use when drawing 2D sketches in SOLIDWORKS with begin_sketch and add_* tools: choosing planes, closed profiles, relations and dimensions, and avoiding under-defined or open sketches.
---

# Sketching

- Start with `begin_sketch` on `Front`, `Top`, `Right`, or a selected planar face (`F#`). Always finish with `end_sketch` before using the sketch in a feature.
- Profiles for `extrude`/`revolve` must be **closed** and non-self-intersecting. Shapes: `add_line`, `add_rectangle`, `add_circle`, `add_arc`, `add_polygon`, `add_slot`, `add_sketch_point`.
- Revolve needs an axis (centerline or edge) as the tool describes.
- Pin down intent: `add_sketch_relation` for coincident/horizontal/etc., `add_sketch_dimension` or `set_dimension` for sizes. Centre key geometry on the origin when the part is symmetric.
- Verify with `capture_view`; check `get_rebuild_errors` if the next feature fails.
- Common failures: open profile, duplicate overlapping lines, wrong plane, forgetting `end_sketch`.
