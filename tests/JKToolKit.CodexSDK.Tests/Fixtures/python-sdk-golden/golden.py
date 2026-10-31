"""Drive the official Python SDK (openai_codex) against fake_server.py and record golden output.

Writes:
  golden.txt   human-readable: requests Python sent (method + params) and TurnResult fields
  golden.json  machine-readable: per scenario, every client->server message and every
               server->client message (for byte-faithful replay in C# tests), plus results
"""

from __future__ import annotations

import json
import sys
import tempfile
from pathlib import Path

from openai_codex import (
    ApprovalMode,
    Codex,
    CodexConfig,
    ExternalMessage,
    ImageInput,
    LocalImageInput,
    MentionInput,
    Sandbox,
    SkillInput,
    TextInput,
)
from openai_codex.generated.notification_registry import NOTIFICATION_MODELS
from openai_codex.generated.v2_all import (
    AgentMessageDeltaNotification,
    Personality,
    ReasoningEffort,
    ReasoningSummary,
    SortDirection,
    ThreadSortKey,
    ThreadSourceKind,
)

HERE = Path(__file__).resolve().parent
FAKE = HERE / "fake_server.py"
LOG_DIR = Path(tempfile.mkdtemp(prefix="golden-logs-"))

SCHEMA = {
    "type": "object",
    "properties": {"answer": {"type": "string"}},
    "required": ["answer"],
    "additionalProperties": False,
}


def dump(value):
    if value is None:
        return None
    if hasattr(value, "model_dump"):
        return value.model_dump(by_alias=True, exclude_none=True, mode="json")
    if hasattr(value, "value"):
        return value.value
    return value


def turn_result(result) -> dict:
    return {
        "id": result.id,
        "status": result.status.value,
        "error": dump(result.error),
        "started_at": result.started_at,
        "completed_at": result.completed_at,
        "duration_ms": result.duration_ms,
        "final_response": result.final_response,
        "items_count": len(result.items),
        "item_types": [item.root.type for item in result.items],
        "item_ids": [item.root.id for item in result.items],
        "usage": dump(result.usage),
    }


class Scenario:
    def __init__(self, name: str, note: str = "") -> None:
        self.name = name
        self.note = note
        self.log = LOG_DIR / f"{name}.jsonl"
        self.results: list[dict] = []

    def config(self) -> CodexConfig:
        return CodexConfig(launch_args_override=(sys.executable, str(FAKE), str(self.log)))

    def record(self, label: str, value) -> None:
        self.results.append({"label": label, "value": value})

    def traffic(self) -> list[dict]:
        return [json.loads(line) for line in self.log.read_text().splitlines() if line.strip()]


def validate_server_notifications(traffic: list[dict]) -> None:
    """Fail loudly if a canned notification would not parse into the SDK's typed model."""
    for entry in traffic:
        msg = entry["msg"]
        if entry["dir"] != "send" or "method" not in msg:
            continue
        model = NOTIFICATION_MODELS.get(msg["method"])
        if model is None:
            raise SystemExit(f"unknown notification method {msg['method']}")
        model.model_validate(msg.get("params") or {})


def run_scenarios() -> list[Scenario]:
    done: list[Scenario] = []

    s = Scenario("initialize", "handshake only; metadata exposed by Codex.metadata")
    with Codex(s.config()) as codex:
        s.record("metadata", codex.metadata.model_dump(mode="json"))
    done.append(s)

    s = Scenario("account", "login_api_key, account(), account(refresh_token=True), logout()")
    with Codex(s.config()) as codex:
        s.record("login_api_key", codex.login_api_key("sk-test-123"))
        s.record("account", dump(codex.account()))
        s.record("account_refresh", dump(codex.account(refresh_token=True)))
        s.record("logout", codex.logout())
    done.append(s)

    s = Scenario("login_chatgpt", "browser login handle wait(); device-code login handle cancel()")
    with Codex(s.config()) as codex:
        browser = codex.login_chatgpt()
        s.record("browser", {"login_id": browser.login_id, "auth_url": browser.auth_url})
        s.record("browser_wait", dump(browser.wait()))
        device = codex.login_chatgpt_device_code()
        s.record("device", {"login_id": device.login_id, "verification_url": device.verification_url,
                            "user_code": device.user_code})
        s.record("device_cancel", dump(device.cancel()))
    done.append(s)

    s = Scenario("thread_start","defaults, then each option, each sandbox, each approval mode")
    with Codex(s.config()) as codex:
        s.record("default", codex.thread_start().id)
        s.record("options", codex.thread_start(
            model="gpt-5.5", cwd="/repo", ephemeral=True,
            base_instructions="Base rules.", developer_instructions="Dev rules.",
            personality=Personality.pragmatic, service_tier="fast", model_provider="openai",
            service_name="golden", config={"model_reasoning_effort": "high"}).id)
        for sandbox in (Sandbox.read_only, Sandbox.workspace_write, Sandbox.full_access):
            s.record(f"sandbox={sandbox.name}", codex.thread_start(sandbox=sandbox).id)
        for mode in (ApprovalMode.deny_all, ApprovalMode.auto_review):
            s.record(f"approval_mode={mode.name}", codex.thread_start(approval_mode=mode).id)
    done.append(s)

    s = Scenario("run_text", "thread.run('hello') with no options")
    with Codex(s.config()) as codex:
        thread = codex.thread_start()
        s.record("result", turn_result(thread.run("hello")))
    done.append(s)

    s = Scenario("run_list_options", "list input of every item type + output_schema + effort + all turn options")
    with Codex(s.config()) as codex:
        thread = codex.thread_start()
        result = thread.run(
            [TextInput("describe"), ImageInput("https://example.com/cat.png"),
             LocalImageInput("/tmp/dog.png"), SkillInput("review", "/skills/review/SKILL.md"),
             MentionInput("repo", "app://repo")],
            output_schema=SCHEMA, effort=ReasoningEffort.high, model="gpt-5.5", cwd="/repo",
            approval_mode=ApprovalMode.deny_all, sandbox=Sandbox.read_only,
            personality=Personality.friendly, service_tier="flex",
            summary=ReasoningSummary("concise"), source="golden-test", turn_service_tier="priority")
        s.record("result", turn_result(result))
    done.append(s)

    s = Scenario("run_turn_sandbox", "turn-level sandbox policy for each preset and auto_review approval")
    with Codex(s.config()) as codex:
        thread = codex.thread_start()
        for sandbox in (Sandbox.read_only, Sandbox.workspace_write, Sandbox.full_access):
            s.record(f"sandbox={sandbox.name}", turn_result(thread.run("go", sandbox=sandbox)))
        s.record("approval_mode=auto_review",
                 turn_result(thread.run("go", approval_mode=ApprovalMode.auto_review)))
    done.append(s)

    s = Scenario("final_response", "final_response selection across phases")
    with Codex(s.config()) as codex:
        thread = codex.thread_start()
        for text in ("phases", "commentary", "unlabelled"):
            s.record(text, turn_result(thread.run(text)))
    done.append(s)

    s = Scenario("run_failed", "failed turn raises with turn.error.message")
    with Codex(s.config()) as codex:
        thread = codex.thread_start()
        try:
            thread.run("fail")
            s.record("raised", None)
        except RuntimeError as exc:
            s.record("raised", {"type": type(exc).__name__, "message": str(exc)})
    done.append(s)

    s = Scenario("external_message", "ExternalMessage -> toolOutput with empty input")
    with Codex(s.config()) as codex:
        thread = codex.thread_start()
        s.record("result", turn_result(thread.run(ExternalMessage(tool_name="github", content="issue body"))))
    done.append(s)

    s = Scenario("stream", "turn.stream(): only this turn's notifications, ends at turn/completed")
    with Codex(s.config()) as codex:
        thread = codex.thread_start()
        turn = thread.turn("stream")
        events = list(turn.stream())
        s.record("turn_id", turn.id)
        s.record("methods", [e.method for e in events])
        s.record("deltas", [e.payload.delta for e in events if isinstance(e.payload, AgentMessageDeltaNotification)])
        s.record("result_via_run", turn_result(thread.run("stream")))
    done.append(s)

    s = Scenario("steer", "turn('wait') then steer('more please') then run()")
    with Codex(s.config()) as codex:
        thread = codex.thread_start()
        turn = thread.turn("wait")
        s.record("steer", dump(turn.steer("more please")))
        s.record("result", turn_result(turn.run()))
    done.append(s)

    s = Scenario("interrupt", "turn('wait') then interrupt() then run()")
    with Codex(s.config()) as codex:
        thread = codex.thread_start()
        turn = thread.turn("wait")
        s.record("interrupt", dump(turn.interrupt()))
        s.record("result", turn_result(turn.run()))
    done.append(s)

    s = Scenario("lifecycle", "resume / fork / list / archive / unarchive, defaults and options")
    with Codex(s.config()) as codex:
        s.record("resume", codex.thread_resume("thread-x").id)
        s.record("resume_options", codex.thread_resume(
            "thread-x", include_turns=False, model="gpt-5.5", cwd="/repo", sandbox=Sandbox.full_access,
            approval_mode=ApprovalMode.deny_all, personality=Personality.none, service_tier="fast",
            base_instructions="b", developer_instructions="d", model_provider="openai").id)
        s.record("fork", codex.thread_fork("thread-x").id)
        s.record("fork_options", codex.thread_fork(
            "thread-x", ephemeral=True, include_turns=True, sandbox=Sandbox.read_only,
            approval_mode=ApprovalMode.auto_review, model="gpt-5.5").id)
        page = codex.thread_list()
        s.record("list", {"ids": [t.id for t in page.data], "next_cursor": page.next_cursor})
        s.record("list_options", dump(codex.thread_list(
            archived=True, cursor="c1", limit=5, model_providers=["openai"], search_term="bug",
            sort_direction=SortDirection.desc, sort_key=ThreadSortKey.updated_at,
            source_kinds=[ThreadSourceKind.cli, ThreadSourceKind.app_server]).next_cursor))
        s.record("archive", dump(codex.thread_archive("thread-a")))
        s.record("unarchive", codex.thread_unarchive("thread-a").id)
    done.append(s)

    s = Scenario("thread_ops", "thread.read / read(include_turns) / set_name / compact")
    with Codex(s.config()) as codex:
        thread = codex.thread_start()
        s.record("read", dump(thread.read()).get("thread", {}).get("id"))
        s.record("read_turns", dump(thread.read(include_turns=True)).get("thread", {}).get("id"))
        s.record("set_name", dump(thread.set_name("Golden")))
        s.record("compact", dump(thread.compact()))
    done.append(s)

    s = Scenario("models", "models() and models(include_hidden=True)")
    with Codex(s.config()) as codex:
        s.record("models", [m.id for m in codex.models().data])
        s.record("models_hidden", [m.id for m in codex.models(include_hidden=True).data])
    done.append(s)

    return done


def client_messages(traffic: list[dict]) -> list[dict]:
    out = []
    for entry in traffic:
        if entry["dir"] != "recv":
            continue
        msg = entry["msg"]
        out.append({"method": msg.get("method"), "kind": "request" if "id" in msg else "notification",
                    "keys": sorted(msg), "params": msg.get("params")})
    return out


def main() -> None:
    scenarios = run_scenarios()
    golden = []
    lines = ["# Golden output: official Python SDK (openai_codex) against fake_server.py",
             "# Generated by golden.py. Requests are exactly what Python wrote to stdin (ids omitted).", ""]
    for s in scenarios:
        traffic = s.traffic()
        validate_server_notifications(traffic)
        requests = client_messages(traffic)
        golden.append({
            "name": s.name, "note": s.note, "client": requests,
            "server": [e["msg"] for e in traffic if e["dir"] == "send"],
            # Pair each server message with the index of the client message it follows.
            "replay": replay_script(traffic),
            "results": s.results,
        })
        lines.append(f"## {s.name} — {s.note}")
        lines.append("### requests (method + params)")
        for r in requests:
            params = json.dumps(r["params"], sort_keys=True) if r["params"] is not None else "<absent>"
            lines.append(f"{r['kind']:<12} {r['method']:<22} {params}")
        lines.append("### results")
        for r in s.results:
            lines.append(f"{r['label']}: {json.dumps(r['value'], sort_keys=True)}")
        lines.append("")
    (HERE / "golden.json").write_text(json.dumps(golden, indent=1, sort_keys=True) + "\n")
    (HERE / "golden.txt").write_text("\n".join(lines) + "\n")
    print(f"wrote {len(golden)} scenarios")


def replay_script(traffic: list[dict]) -> list[dict]:
    """Server messages grouped by the client request that triggered them (request index)."""
    script = []
    index = -1
    for entry in traffic:
        if entry["dir"] == "recv":
            if "id" in entry["msg"]:
                index += 1
            continue
        msg = dict(entry["msg"])
        is_response = "id" in msg
        msg.pop("id", None)
        script.append({"after_request": index, "response": is_response, "msg": msg})
    return script


if __name__ == "__main__":
    main()
