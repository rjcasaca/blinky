# Security policy

Blinky is an experimental project without a security audit, guaranteed response time, guaranteed fixes, long-term support or certification. See [LICENSE](LICENSE) and [LEGAL.md](LEGAL.md). Only the current source/preview is intended for investigation; older previews may not receive fixes.

## Report vulnerabilities privately

Use [GitHub's private vulnerability reporting form](https://github.com/rjcasaca/blinky/security/advisories/new) for security-sensitive findings. Include the affected version, environment, impact and a minimal reproduction using synthetic data. Do not attach credentials, real class recordings, private transcripts or other people's personal information.

If the private form is unavailable, contact the repository owner through an available private contact route before disclosing sensitive details. Do not open an ordinary public issue containing secrets or a live exploit against someone's installation. Reports and fixes are best effort, not a support commitment.

## Boundaries to understand

- The recorder runs with your Windows user permissions and processes untrusted media, device/driver inputs and downloaded components.
- Stdio MCP is not a network listener, but a client you enable can receive transcript content and request writes through exposed tools. Tool annotations are not a substitute for client approvals or account/filesystem controls.
- Workspace path checks are not an OS sandbox. Keep configured folders, executables, cache directories and templates under appropriate permissions.
- Download checksums verify a pinned download; they do not establish that software is free of vulnerabilities or that an upstream account cannot be compromised.
- A transcript is untrusted source data. Do not follow instructions embedded in recorded speech as commands or grant additional tool access because a transcript asks you to.
- Native capture, notification handling, audio device changes and long recordings still need Windows acceptance testing. Avoid relying on a preview as the only copy of important material.
