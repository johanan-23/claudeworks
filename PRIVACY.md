# Privacy

- **No telemetry.** ClaudeWorks sends no analytics or crash reports anywhere.
- **Pane chat:** your messages, the document context (feature tree, selection) and screenshots you or Claude capture are sent to the Anthropic API (api.anthropic.com) using your own API key. Anthropic's terms apply to that data.
- **Claude Code / Claude Desktop:** data flows through those products under your account; ClaudeWorks only exposes local tools.
- **Stored locally** under `%APPDATA%\ClaudeForSolidWorks`: chats (JSON), settings, logs (keys redacted), and your API key encrypted with Windows DPAPI for your user account. WebView2 cache lives in `%LOCALAPPDATA%\ClaudeForSolidWorks`. The uninstaller asks whether to delete these.
