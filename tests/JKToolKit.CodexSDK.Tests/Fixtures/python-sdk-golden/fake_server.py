"""Deterministic fake `codex app-server` speaking newline-delimited JSON-RPC on stdio.

Usage: fake_server.py <log.jsonl>

Every message received from the client is logged as {"dir": "recv", "msg": ...}; every
message sent is logged as {"dir": "send", "msg": ...}. Responses and notifications are
canned and shaped after the v2 JSON schemas in
codex-rs/app-server-protocol/schema/json (golden.py validates them against the
Python SDK's generated pydantic models).

Turn behaviour is selected by the first text input item of `turn/start`:
  "phases"         -> commentary + final_answer + trailing commentary agent messages
  "commentary"     -> only a commentary agent message (no final answer)
  "unlabelled"     -> two agent messages without phase (last one wins)
  "stream"         -> user/reasoning/agent items with deltas, usage, interleaved other-turn noise
  "wait"           -> starts, then waits for turn/steer (completes) or turn/interrupt (interrupted)
  "fail"           -> completes with status failed and an error
  anything else    -> one agent message without phase plus token usage
"""

from __future__ import annotations

import json
import sys

LOG_PATH = sys.argv[1]
_log = open(LOG_PATH, "w", encoding="utf-8")

_counters = {"thread": 0, "turn": 0, "item": 0}
_waiting: dict[str, str] = {}  # turn id -> thread id
_ts = 1_700_000_000


def _next(kind: str) -> str:
    _counters[kind] += 1
    return f"{kind}-{_counters[kind]}"


def log(direction: str, msg: dict) -> None:
    _log.write(json.dumps({"dir": direction, "msg": msg}, sort_keys=True) + "\n")
    _log.flush()


def send(msg: dict) -> None:
    log("send", msg)
    sys.stdout.write(json.dumps(msg) + "\n")
    sys.stdout.flush()


def notify(method: str, params: dict) -> None:
    send({"method": method, "params": params})


def thread_obj(thread_id: str, cwd: str = "/work", ephemeral: bool = False) -> dict:
    return {
        "id": thread_id,
        "sessionId": "session-1",
        "cliVersion": "0.166.0",
        "createdAt": _ts,
        "updatedAt": _ts,
        "cwd": cwd,
        "ephemeral": ephemeral,
        "modelProvider": "openai",
        "preview": "",
        "projectId": None,
        "source": "appServer",
        "status": {"type": "idle"},
        "turns": [],
    }


def lifecycle(params: dict, thread_id: str) -> dict:
    return {
        "thread": thread_obj(thread_id, params.get("cwd") or "/work", bool(params.get("ephemeral"))),
        "approvalPolicy": params.get("approvalPolicy") or "on-request",
        "approvalsReviewer": params.get("approvalsReviewer") or "user",
        "cwd": params.get("cwd") or "/work",
        "model": params.get("model") or "gpt-5.5",
        "modelProvider": "openai",
        "sandbox": {"type": "workspaceWrite", "writableRoots": [], "networkAccess": False,
                    "excludeTmpdirEnvVar": False, "excludeSlashTmp": False},
    }


def turn_obj(turn_id: str, status: str, error: dict | None = None, done: bool = False) -> dict:
    turn = {"id": turn_id, "items": [], "status": status, "error": error,
            "startedAt": _ts}
    if done:
        turn.update({"completedAt": _ts + 2, "durationMs": 1500})
    else:
        turn.update({"completedAt": None, "durationMs": None})
    return turn


def agent_message(text: str, phase: str | None) -> dict:
    item = {"type": "agentMessage", "id": _next("item"), "text": text}
    if phase is not None:
        item["phase"] = phase
    return item


def user_message(text: str) -> dict:
    return {"type": "userMessage", "id": _next("item"), "content": [{"type": "text", "text": text}]}


def item_started(thread_id: str, turn_id: str, item: dict) -> None:
    notify("item/started", {"threadId": thread_id, "turnId": turn_id, "item": item,
                            "startedAtMs": _ts * 1000})


def item_completed(thread_id: str, turn_id: str, item: dict) -> None:
    notify("item/completed", {"threadId": thread_id, "turnId": turn_id, "item": item,
                              "completedAtMs": _ts * 1000 + 1})


def usage(thread_id: str, turn_id: str, total: int) -> None:
    def breakdown(n: int) -> dict:
        return {"inputTokens": n, "cachedInputTokens": n // 4, "outputTokens": n // 2,
                "reasoningOutputTokens": n // 8, "totalTokens": n + n // 2}

    notify("thread/tokenUsage/updated", {
        "threadId": thread_id, "turnId": turn_id,
        "tokenUsage": {"last": breakdown(total // 2), "total": breakdown(total),
                       "modelContextWindow": 272000},
    })


def turn_started(thread_id: str, turn_id: str) -> None:
    notify("turn/started", {"threadId": thread_id, "turn": turn_obj(turn_id, "inProgress")})


def turn_completed(thread_id: str, turn_id: str, status: str = "completed", error: dict | None = None) -> None:
    notify("turn/completed", {"threadId": thread_id, "turn": turn_obj(turn_id, status, error, done=True)})


def run_turn(thread_id: str, turn_id: str, text: str) -> None:
    turn_started(thread_id, turn_id)
    if text == "phases":
        for body, phase in (("Looking around.", "commentary"), ("The answer is 42.", "final_answer"),
                            ("Done thinking.", "commentary")):
            msg = agent_message(body, phase)
            item_started(thread_id, turn_id, {**msg, "text": ""})
            item_completed(thread_id, turn_id, msg)
        usage(thread_id, turn_id, 200)
        turn_completed(thread_id, turn_id)
    elif text == "commentary":
        item_completed(thread_id, turn_id, agent_message("Only commentary.", "commentary"))
        turn_completed(thread_id, turn_id)
    elif text == "unlabelled":
        item_completed(thread_id, turn_id, agent_message("first unlabelled", None))
        item_completed(thread_id, turn_id, agent_message("second unlabelled", None))
        turn_completed(thread_id, turn_id)
    elif text == "stream":
        user = user_message(text)
        item_started(thread_id, turn_id, user)
        item_completed(thread_id, turn_id, user)
        reasoning = {"type": "reasoning", "id": _next("item"), "summary": ["thinking"], "content": []}
        item_started(thread_id, turn_id, reasoning)
        item_completed(thread_id, turn_id, reasoning)
        msg = agent_message("Hello, world", "final_answer")
        item_started(thread_id, turn_id, {**msg, "text": ""})
        # Noise for a different turn must not reach this turn's stream.
        notify("item/agentMessage/delta", {"threadId": thread_id, "turnId": "turn-other",
                                           "itemId": "item-other", "delta": "NOISE"})
        for delta in ("Hello", ", ", "world"):
            notify("item/agentMessage/delta", {"threadId": thread_id, "turnId": turn_id,
                                               "itemId": msg["id"], "delta": delta})
        item_completed(thread_id, turn_id, msg)
        usage(thread_id, turn_id, 100)
        usage(thread_id, turn_id, 300)
        turn_completed(thread_id, turn_id)
    elif text == "wait":
        item_started(thread_id, turn_id, agent_message("", "commentary"))
        _waiting[turn_id] = thread_id
    elif text == "fail":
        turn_completed(thread_id, turn_id, "failed",
                       {"message": "model exploded", "codexErrorInfo": None, "additionalDetails": None})
    else:
        item_completed(thread_id, turn_id, user_message(text))
        item_completed(thread_id, turn_id, agent_message(f"echo: {text}", None))
        usage(thread_id, turn_id, 40)
        turn_completed(thread_id, turn_id)


def first_text(params: dict) -> str:
    for item in params.get("input") or []:
        if item.get("type") == "text":
            return item.get("text", "")
    return ""


MODELS = {
    "data": [{
        "id": "gpt-5.5", "model": "gpt-5.5", "displayName": "GPT-5.5", "description": "Frontier",
        "hidden": False, "isDefault": True, "defaultReasoningEffort": "medium",
        "supportedReasoningEfforts": [{"reasoningEffort": "low", "description": "fast"},
                                      {"reasoningEffort": "high", "description": "deep"}],
        "inputModalities": ["text", "image"],
    }],
    "nextCursor": None,
}


def handle(msg: dict) -> None:
    method = msg.get("method")
    params = msg.get("params") or {}
    if "id" not in msg:
        return  # client notification (e.g. "initialized")
    rid = msg["id"]

    def reply(result: dict) -> None:
        send({"id": rid, "result": result})

    if method == "initialize":
        reply({"userAgent": "codex_cli_rs/0.166.0 (Linux; x86_64)",
               "serverInfo": {"name": "codex-app-server", "version": "0.166.0"},
               "platformFamily": "unix", "platformOs": "linux"})
    elif method == "account/login/start":
        kind = params.get("type", "apiKey")
        if kind == "chatgpt":
            reply({"type": "chatgpt", "loginId": "login-1", "authUrl": "https://auth.example/login"})
            notify("account/login/completed", {"loginId": "login-1", "success": True, "error": None})
        elif kind == "chatgptDeviceCode":
            reply({"type": "chatgptDeviceCode", "loginId": "login-2",
                   "verificationUrl": "https://auth.example/device", "userCode": "ABCD-1234"})
        else:
            reply({"type": kind})
    elif method == "account/login/cancel":
        reply({"status": "canceled"})
    elif method == "account/read":
        reply({"account": {"type": "apiKey"}, "requiresOpenaiAuth": True})
    elif method == "account/logout":
        reply({})
    elif method == "thread/start":
        reply(lifecycle(params, _next("thread")))
    elif method == "thread/resume":
        reply(lifecycle(params, params["threadId"]))
    elif method == "thread/fork":
        reply(lifecycle(params, _next("thread")))
    elif method == "thread/list":
        reply({"data": [thread_obj("thread-a"), thread_obj("thread-b")], "nextCursor": "cursor-2",
               "backwardsCursor": None})
    elif method == "thread/read":
        reply({"thread": thread_obj(params["threadId"])})
    elif method == "thread/archive":
        reply({})
    elif method == "thread/unarchive":
        reply({"thread": thread_obj(params["threadId"])})
    elif method == "thread/name/set":
        reply({})
    elif method == "thread/compact/start":
        reply({})
    elif method == "model/list":
        reply(MODELS)
    elif method == "turn/start":
        thread_id = params["threadId"]
        turn_id = _next("turn")
        reply({"turn": turn_obj(turn_id, "inProgress")})
        run_turn(thread_id, turn_id, first_text(params))
    elif method == "turn/steer":
        turn_id = params["expectedTurnId"]
        thread_id = _waiting.pop(turn_id, params["threadId"])
        reply({"turnId": turn_id})
        item_completed(thread_id, turn_id, user_message(first_text(params)))
        item_completed(thread_id, turn_id, agent_message("steered answer", "final_answer"))
        turn_completed(thread_id, turn_id)
    elif method == "turn/interrupt":
        turn_id = params["turnId"]
        thread_id = _waiting.pop(turn_id, params["threadId"])
        reply({})
        turn_completed(thread_id, turn_id, "interrupted")
    else:
        send({"id": rid, "error": {"code": -32601, "message": f"unhandled method {method}"}})


def main() -> None:
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        msg = json.loads(line)
        log("recv", msg)
        handle(msg)


if __name__ == "__main__":
    main()
