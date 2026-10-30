# Security

TokenScope has no built-in login, API authentication, or per-user isolation. The default Compose ports bind to loopback. Protect every endpoint with an authenticated gateway or use a trusted private network before allowing access from other machines. Browser origin checks and read-only MCP reports do not provide authentication.

Keep database credentials out of source control and review usage output before sharing it. Optional attribution can contain private names. The collector requires only read access to Claude projects; do not mount authentication files. See [deployment](docs/DEPLOYMENT.md) for permissions, TLS, backups, and delivery limits.

## Report a vulnerability

If the repository's **Security** tab provides **Report a vulnerability**, use GitHub's private vulnerability reporting. If it is unavailable, use the contact links on [Milan's GitHub profile](https://github.com/m-jovanovic) to request a private reporting channel. Do not open a public issue with exploit details, credentials, or personal session data.

Include the affected version, reproduction steps, impact, and a minimal sanitized example. No response-time guarantee is currently offered. Use the latest reviewed revision when checking whether an issue is already fixed.
