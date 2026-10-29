"""錄 Claude Code CLI 的 stream-json 協定樣本，給 C# 的單元測試重播。

用官方 Python SDK 實際跑一遍各個情境，把 SDK 寫進 CLI stdin、從 stdout 讀到的每一則訊息
依序存成 JSONL。C# 這邊不用啟動 Claude Code，拿這些檔案餵假的 transport，
就能測讀取迴圈、control 協定與 parser。檔案格式見 Fixtures/README.md。

要用裝了對照版本 SDK 的虛擬環境執行；不帶參數就錄全部情境：

    D:/SideProject/claude-agent-sdk-python/.venv/Scripts/python tools/record_fixtures.py
    D:/SideProject/claude-agent-sdk-python/.venv/Scripts/python tools/record_fixtures.py resume interrupt

會真的啟動 Claude Code，用目前登入的帳號跑；每個情境都是很短的對話。
"""

from __future__ import annotations

import asyncio
import json
import re
import shutil
import subprocess
import sys
import tempfile
from collections.abc import AsyncIterator, Awaitable, Callable
from dataclasses import dataclass
from datetime import date
from pathlib import Path
from typing import Any

from claude_agent_sdk import (
    ClaudeAgentOptions,
    ClaudeSDKClient,
    PermissionResultAllow,
    PermissionResultDeny,
    ResultMessage,
    StreamEvent,
    ToolPermissionContext,
)
from claude_agent_sdk import __version__ as SDK_VERSION
from claude_agent_sdk._internal.transport.subprocess_cli import SubprocessCLITransport
from claude_agent_sdk.types import _configure_can_use_tool

REPO_ROOT = Path(__file__).resolve().parent.parent
FIXTURES_DIR = REPO_ROOT / "tests" / "PamisWang.ClaudeAgentSdk.Tests" / "Fixtures"
# 固定的工作目錄：resume 要回到同一個資料夾才找得到 session；每個情境開始前清空
SANDBOX = Path(tempfile.gettempdir()) / "pamiswang-claude-agent-sdk-fixtures"
SCENARIO_TIMEOUT_S = 180
MODEL = "haiku"
# 跟 chibi-office 的 chat.ts 一樣：畫面沒有回答選擇題的介面
DISALLOWED_TOOLS = ["AskUserQuestion"]
DENY_MESSAGE = "使用者在辦公室拒絕了這個動作"
WRITE_PROMPT = "用 Write 工具在目前資料夾建立 hello.txt，內容是「你好」。不要用其他工具。"

PermissionCallback = Callable[[str, dict[str, Any], ToolPermissionContext], Awaitable[Any]]


class Recorder:
    """依收發順序記下每一則訊息。

    dir 是 send（SDK 寫進 CLI stdin）、recv（從 CLI stdout 讀到）或 note（腳本自己的註記，重播時略過）。
    """

    def __init__(self) -> None:
        self.entries: list[dict[str, Any]] = []
        self.argv: list[str] = []
        self.stderr: list[str] = []

    def add(self, direction: str, data: dict[str, Any]) -> None:
        self.entries.append({"dir": direction, "data": data})

    def note(self, text: str) -> None:
        self.entries.append({"dir": "note", "text": text})


class RecordingTransport(SubprocessCLITransport):
    """官方的 subprocess transport，多做一件事：把收發的每一則訊息交給 Recorder。"""

    def __init__(self, options: ClaudeAgentOptions, recorder: Recorder) -> None:
        # 自己建 transport 時，client 不會替它補上 --permission-prompt-tool stdio，要自己套
        super().__init__(prompt="", options=_configure_can_use_tool(options))
        self._recorder = recorder

    async def connect(self) -> None:
        await super().connect()
        self._recorder.argv = ["claude", *self._build_command()[1:]]

    async def write(self, data: str) -> None:
        # 先記再寫：CLI 回得快的時候，回應才不會排在請求前面
        for line in data.splitlines():
            if line.strip():
                self._recorder.add("send", json.loads(line))
        await super().write(data)

    def read_messages(self) -> AsyncIterator[dict[str, Any]]:
        return self._record(super().read_messages())

    async def _record(self, inner: AsyncIterator[dict[str, Any]]) -> AsyncIterator[dict[str, Any]]:
        try:
            async for message in inner:
                self._recorder.add("recv", message)
                yield message
        finally:
            await inner.aclose()  # type: ignore[attr-defined]


def base_options(recorder: Recorder, can_use_tool: PermissionCallback | None, **overrides: Any) -> ClaudeAgentOptions:
    return ClaudeAgentOptions(
        cwd=SANDBOX,
        model=MODEL,
        can_use_tool=can_use_tool,
        disallowed_tools=DISALLOWED_TOOLS,
        # 不載入使用者的設定、外掛、hook 與 claude.ai 連接器：錄的時候不會觸發本機的 hook，
        # 樣本裡也不會出現本機裝了哪些外掛和 MCP server
        setting_sources=[],
        strict_mcp_config=True,
        stderr=recorder.stderr.append,
        **overrides,
    )


class Session:
    """一個情境的錄製狀態：建出來的 client 都記到同一個 Recorder。"""

    def __init__(self) -> None:
        self.recorder = Recorder()

    def client(self, can_use_tool: PermissionCallback | None = None, **overrides: Any) -> ClaudeSDKClient:
        # chat.ts 一律帶 canUseTool，所以每個情境都掛權限回呼，CLI 參數才跟實際用法一樣
        options = base_options(self.recorder, can_use_tool or self.deny_unexpected, **overrides)
        return ClaudeSDKClient(options=options, transport=RecordingTransport(options, self.recorder))

    def unrecorded_client(self, **overrides: Any) -> ClaudeSDKClient:
        """準備用的 client（例如先開一個要被 resume 的 session），不寫進樣本"""
        return ClaudeSDKClient(options=base_options(Recorder(), None, **overrides))

    async def deny_unexpected(
        self, tool_name: str, tool_input: dict[str, Any], context: ToolPermissionContext
    ) -> PermissionResultDeny:
        self.recorder.note(f"can_use_tool：{tool_name}，這個情境不該用工具，拒絕")
        return PermissionResultDeny(message="這個情境不使用工具")


async def turn(client: ClaudeSDKClient, prompt: str) -> ResultMessage:
    """送一句話，讀到這一輪的 result 為止"""
    await client.query(prompt)
    async for message in client.receive_response():
        if isinstance(message, ResultMessage):
            return message
    raise RuntimeError("沒收到 result 訊息，回應就結束了")


# ── 情境 ─────────────────────────────────────────────────────────


async def basic(s: Session) -> None:
    async with s.client() as client:
        await turn(client, "用一句繁體中文回答：台灣最高的山是哪一座？")


async def multi_turn(s: Session) -> None:
    async with s.client() as client:
        await turn(client, "請記住這個詞：鳳梨酥。只要回答「好」。")
        await turn(client, "我剛才請你記住哪個詞？只回答那個詞。")


async def resume(s: Session) -> None:
    async with s.unrecorded_client() as setup:
        first = await turn(setup, "請記住這個詞：珍珠奶茶。只要回答「好」。")
    s.recorder.note(f"先用另一個 client 開好 session {first.session_id}，這裡接續它")
    async with s.client(resume=first.session_id) as client:
        await turn(client, "我剛才請你記住哪個詞？只回答那個詞。")


async def partial_messages(s: Session) -> None:
    async with s.client(include_partial_messages=True) as client:
        await turn(client, "用一句繁體中文回答：台灣最高的山是哪一座？")


async def permission_allow(s: Session) -> None:
    async def allow(tool_name: str, tool_input: dict[str, Any], context: ToolPermissionContext) -> PermissionResultAllow:
        s.recorder.note(f"can_use_tool：{tool_name}，允許")
        return PermissionResultAllow(updated_input=tool_input)

    async with s.client(allow) as client:
        await turn(client, WRITE_PROMPT)


async def permission_deny(s: Session) -> None:
    async def deny(tool_name: str, tool_input: dict[str, Any], context: ToolPermissionContext) -> PermissionResultDeny:
        s.recorder.note(f"can_use_tool：{tool_name}，拒絕")
        return PermissionResultDeny(message=DENY_MESSAGE)

    async with s.client(deny) as client:
        await turn(client, WRITE_PROMPT)


async def permission_always_allow(s: Session) -> None:
    calls = 0

    async def always(tool_name: str, tool_input: dict[str, Any], context: ToolPermissionContext) -> PermissionResultAllow:
        nonlocal calls
        calls += 1
        # 「一律允許」：把 CLI 給的建議權限原樣帶回去，跟 chat.ts 的 remember 一樣
        s.recorder.note(f"can_use_tool：{tool_name}，允許並套用 {len(context.suggestions)} 個建議權限")
        return PermissionResultAllow(updated_input=tool_input, updated_permissions=context.suggestions)

    async with s.client(always) as client:
        await turn(client, "用 Write 工具在目前資料夾建立 a.txt，內容是「甲」。不要用其他工具。")
        await turn(client, "再用 Write 工具在目前資料夾建立 b.txt，內容是「乙」。不要用其他工具。")
    s.recorder.note(f"兩輪下來權限回呼總共被呼叫 {calls} 次")


async def interrupt(s: Session) -> None:
    # 開部分訊息，才能在模型剛開始輸出時就中斷，不用靠計時
    async with s.client(include_partial_messages=True) as client:
        await client.query("寫一篇大約 400 字的繁體中文短文，主題是辦公室裡的貓。")
        interrupted = False
        async for message in client.receive_response():
            if not interrupted and isinstance(message, StreamEvent) and message.event.get("type") == "content_block_delta":
                interrupted = True
                s.recorder.note("收到第一個 content_block_delta，送出 interrupt")
                await client.interrupt()
        # 中斷的是這一輪，對話還在，可以接著說
        await turn(client, "只回答「好」。")


async def interrupt_during_permission(s: Session) -> None:
    asked = asyncio.Event()

    async def never_answer(
        tool_name: str, tool_input: dict[str, Any], context: ToolPermissionContext
    ) -> PermissionResultAllow:
        s.recorder.note(f"can_use_tool：{tool_name}，不回覆，改送 interrupt")
        asked.set()
        try:
            await asyncio.get_running_loop().create_future()
        except asyncio.CancelledError:
            s.recorder.note("權限回呼被取消，沒有回覆 control_response")
            raise
        raise AssertionError("unreachable")

    async with s.client(never_answer) as client:

        async def interrupt_when_asked() -> None:
            await asked.wait()
            await client.interrupt()

        interrupter = asyncio.create_task(interrupt_when_asked())
        await client.query(WRITE_PROMPT)
        async for message in client.receive_response():
            if isinstance(message, ResultMessage):
                break
        await interrupter


@dataclass(frozen=True)
class Scenario:
    name: str
    description: str
    run: Callable[[Session], Awaitable[None]]


SCENARIOS = [
    Scenario("basic", "一般問答：一輪，不用工具", basic),
    Scenario("multi_turn", "多輪對話：同一個連線送兩句話", multi_turn),
    Scenario("resume", "接續舊 session（--resume）", resume),
    Scenario("partial_messages", "部分訊息串流（--include-partial-messages）", partial_messages),
    Scenario("permission_allow", "權限詢問：允許", permission_allow),
    Scenario("permission_deny", "權限詢問：拒絕", permission_deny),
    Scenario("permission_always_allow", "權限詢問：一律允許後，同一工具不再問", permission_always_allow),
    Scenario("interrupt", "模型輸出到一半時中斷，再接著說一句", interrupt),
    Scenario("interrupt_during_permission", "等權限回覆時中斷（CLI 送 control_cancel_request）", interrupt_during_permission),
]


# ── 去識別化與寫檔 ────────────────────────────────────────────────


def _path_pattern(path: Path) -> re.Pattern[str]:
    # 斜線、反斜線、JSON 跳脫後的雙反斜線都算分隔；磁碟代號大小寫不一定
    parts = [re.escape(p) for p in re.split(r"[\\/]+", str(path)) if p]
    return re.compile(r"[\\/]+".join(parts) + r"(?![A-Za-z0-9_])", re.IGNORECASE)


def _project_dir_pattern(path: Path) -> re.Pattern[str]:
    # ~/.claude/projects 底下的資料夾名稱：路徑裡非英數字元都換成 -
    return re.compile(re.escape(re.sub(r"[^A-Za-z0-9]", "-", str(path))), re.IGNORECASE)


SCRUB_RULES: list[tuple[re.Pattern[str], str]] = [
    # 工作目錄在家目錄底下，要先換
    (_path_pattern(SANDBOX), "<CWD>"),
    (_project_dir_pattern(SANDBOX), "<CWD-PROJECT-DIR>"),
    (_path_pattern(Path.home()), "<HOME>"),
    (_project_dir_pattern(Path.home()), "<HOME-PROJECT-DIR>"),
    (re.compile(r"[\w.+-]+@[\w-]+(\.[\w-]+)+"), "user@example.com"),
    # initialize 回應的 account 裡有帳號所屬的組織名稱
    (re.compile(r'("organization": )"[^"]*"'), r'\1"Example Org"'),
]


def scrub(text: str) -> str:
    for pattern, replacement in SCRUB_RULES:
        text = pattern.sub(replacement, text)
    return text


def write_fixture(scenario: Scenario, recorder: Recorder, cli_version: str) -> Path:
    meta = {
        "dir": "meta",
        "scenario": scenario.name,
        "description": scenario.description,
        "sdk_version": SDK_VERSION,
        "cli_version": cli_version,
        "model": MODEL,
        "argv": recorder.argv,
        "recorded_on": date.today().isoformat(),
    }
    lines = [json.dumps(entry, ensure_ascii=False) for entry in [meta, *recorder.entries]]
    path = FIXTURES_DIR / f"{scenario.name}.jsonl"
    path.write_text(scrub("\n".join(lines) + "\n"), encoding="utf-8", newline="\n")
    return path


def reset_sandbox() -> None:
    shutil.rmtree(SANDBOX, ignore_errors=True)
    SANDBOX.mkdir(parents=True)


def cli_version() -> str:
    cli = shutil.which("claude")
    if cli is None:
        raise SystemExit("找不到 claude，請先安裝 Claude Code CLI")
    output = subprocess.run([cli, "--version"], capture_output=True, text=True, check=True).stdout
    return output.split()[0]


async def record(selected: list[Scenario], version: str) -> int:
    failures = 0
    for scenario in selected:
        reset_sandbox()
        session = Session()
        try:
            async with asyncio.timeout(SCENARIO_TIMEOUT_S):
                await scenario.run(session)
        except Exception as e:  # noqa: BLE001 - 一個情境失敗不影響其他情境
            failures += 1
            print(f"✗ {scenario.name}：{type(e).__name__}: {e}", file=sys.stderr)
            for line in session.recorder.stderr[-20:]:
                print(f"    {line}", file=sys.stderr)
            continue
        path = write_fixture(scenario, session.recorder, version)
        counts = {d: sum(1 for e in session.recorder.entries if e["dir"] == d) for d in ("send", "recv", "note")}
        print(f"✓ {scenario.name}：send {counts['send']}、recv {counts['recv']}、note {counts['note']} → {path.name}")
    return 1 if failures else 0


def main() -> int:
    # Windows 上輸出被導到管線時預設是系統字碼頁，中文會變亂碼
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8")  # type: ignore[attr-defined]
    by_name = {s.name: s for s in SCENARIOS}
    unknown = [name for name in sys.argv[1:] if name not in by_name]
    if unknown:
        print(f"沒有這些情境：{', '.join(unknown)}；可用的有：{', '.join(by_name)}", file=sys.stderr)
        return 2
    selected = [by_name[name] for name in sys.argv[1:]] or SCENARIOS
    version = cli_version()
    print(f"Python SDK {SDK_VERSION}、Claude Code CLI {version}、模型 {MODEL}")
    return asyncio.run(record(selected, version))


if __name__ == "__main__":
    sys.exit(main())
