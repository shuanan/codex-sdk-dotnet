# Third-party provenance

This repository is a **private mirror** of third-party code. It is not an official OpenAI product and was not written by the owner of this repository.

## Upstream

| | |
|---|---|
| Project | JKToolKit.CodexSDK — a .NET SDK for the OpenAI Codex CLI (`codex exec`, `codex app-server`, `codex mcp-server`) |
| Author | Jonas Kamsker ([@JKamsker](https://github.com/JKamsker)) |
| Source | https://github.com/JKamsker/JKToolKit.CodexSDK |
| NuGet | https://www.nuget.org/packages/JKToolKit.CodexSDK |
| Mirrored commit | `c88a455173816a072e5cf4d1860663770e76322f` (upstream `master`, aligned to Codex `0.166.0` per `UPSTREAM_CODEX_VERSION.json`) |
| License | MIT — see [`LICENSE`](LICENSE) (Copyright (c) 2025 Jonas Kamsker) |

The full upstream commit history is preserved. Upstream files are kept unmodified; changes made in this mirror are made in separate commits on top.

## Relationship to OpenAI Codex

JKToolKit.CodexSDK wraps and ports the protocol and SDK surface of [openai/codex](https://github.com/openai/codex) (including its official TypeScript `@openai/codex-sdk` and Python `openai-codex` SDKs under `sdk/`), which is licensed under the **Apache License 2.0**, Copyright 2025 OpenAI. The upstream Apache-2.0 license and NOTICE are included unchanged as [`runtime/LICENSE-CODEX`](runtime/LICENSE-CODEX) and [`runtime/NOTICE-CODEX`](runtime/NOTICE-CODEX). `external/codex` is a git submodule pointing at openai/codex.

"OpenAI" and "Codex" are trademarks of OpenAI. This mirror is not affiliated with or endorsed by OpenAI or by the upstream author.

## Status

Unofficial, community-maintained. The Codex CLI protocol evolves quickly; behavior may differ when used with a Codex CLI version other than the one the upstream commit targets.
