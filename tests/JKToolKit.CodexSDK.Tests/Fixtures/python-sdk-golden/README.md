# Python SDK golden traffic

`golden.json` is recorded from the official Python SDK (`openai-codex`, `sdk/python` in
openai/codex) driving `fake_server.py`, a deterministic fake `codex app-server`. For every
scenario it holds the exact JSON-RPC messages Python wrote (`client`), everything the fake
server answered (`replay`, keyed by the client request index), and the resulting
`TurnResult` values (`results`). `PythonSdkGoldenParityTests` replays the same server traffic
to the C# `Codex` facade and asserts the same requests and results.

Regenerate (no real `codex` binary needed):

```sh
python -m venv .venv && .venv/bin/pip install openai-codex   # or a checkout of sdk/python
.venv/bin/python golden.py   # writes golden.json and golden.txt next to the script
```

`golden.txt` is a human-readable rendering of the same data.
