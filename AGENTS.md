# Using ClaudeWorks from any MCP client

Server: `C:\Program Files\ClaudeWorks\ClaudeSW.Mcp.exe` (stdio), name `solidworks-addin`. Units are mm and degrees.
Loop: `get_solidworks_context` -> one modifying tool -> `capture_view` / `get_model_info` -> fix with `undo_last_change`. Use `find_faces`/`find_edges` references (F#/E#) and selection (S#) instead of coordinates. Never save or overwrite files unless asked. Detailed guidance: `skills/`.
