# Worked example: plate with a centre hole

1. `get_solidworks_context` (is a document open?)
2. `new_document` (part)
3. `begin_sketch` on Front, `add_rectangle` 100 x 60 centred on the origin, `end_sketch`
4. `extrude` 20 mm, then `capture_view`
5. `find_faces` to get the top face `F#`, `begin_sketch` on that face, `add_circle` 10 mm diameter at the centre, `end_sketch`, cut with `extrude`
6. `get_model_info` to confirm volume and bounding box, `rename_feature` for the important features

Check each tool's own input schema for exact argument names; this list shows the order only.
