"""publish したバイナリを stdio で起動し、MCP の initialize と tools/list が通るか確かめる。

Google Flights には問い合わせない（CI からの外部アクセスで結果が揺れないように）。
使い方: python smoke_test.py <実行ファイルのパス>
"""

import json
import subprocess
import sys

EXPECTED_TOOLS = {"search_airports", "search_flights", "search_cheapest_dates"}


def main() -> int:
    proc = subprocess.Popen(
        [sys.argv[1]],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=sys.stderr,
        text=True,
        encoding="utf-8",
    )

    def send(message: dict) -> None:
        proc.stdin.write(json.dumps(message) + "\n")
        proc.stdin.flush()

    def receive(request_id: int) -> dict:
        while True:
            line = proc.stdout.readline()
            if not line:
                raise RuntimeError("サーバーが応答せずに終了しました")
            message = json.loads(line)
            if message.get("id") == request_id:
                return message

    try:
        send({
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {"name": "smoke-test", "version": "1"},
            },
        })
        server = receive(1)["result"]["serverInfo"]
        print(f"serverInfo: {server}")

        send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        send({"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
        tools = {t["name"] for t in receive(2)["result"]["tools"]}
        print(f"tools: {sorted(tools)}")

        missing = EXPECTED_TOOLS - tools
        if missing:
            print(f"ツールが足りません: {sorted(missing)}", file=sys.stderr)
            return 1
        return 0
    finally:
        proc.stdin.close()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()


if __name__ == "__main__":
    sys.exit(main())
