# Security policy

## Reporting
Please report vulnerabilities privately via GitHub's "Report a vulnerability" (Security tab) rather than a public issue.

## Threat model
- The add-in and its MCP server can **modify, overwrite and delete CAD files** in the user's running SOLIDWORKS. Anything that can talk to the MCP server (Claude Code, Claude Desktop, any local process running as the same user) gets that power.
- The bridge is a per-user named pipe (`\\.\pipe\ClaudeForSolidWorks.Tools`) restricted to the current user. It can be turned off in Settings ("Allow Claude Code and Claude Desktop").
- The generic `invoke_api` tool (late-bound SOLIDWORKS API call) is **off by default**, and every call needs explicit user confirmation.
- Destructive tools ask for confirmation in the pane; saving or overwriting happens only on request.
- The Anthropic API key (pane chat only) is stored with Windows DPAPI (current user) and sent only to api.anthropic.com. Logs redact keys.
- Prompt injection: text inside opened documents (notes, properties, file names) is untrusted data and could try to steer the model. Review tool actions on files you did not author.
- The installer is **unsigned** for now; Windows SmartScreen will warn. Verify the SHA-256 published with each release.
