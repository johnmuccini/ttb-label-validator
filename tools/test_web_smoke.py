#!/usr/bin/env python3
"""Start the published local app and verify its complete offline demonstration."""

from __future__ import annotations

import json
import socket
import subprocess
import time
import urllib.request
from pathlib import Path


HERE = Path(__file__).resolve().parent
APP = HERE.parent / "bin" / "local-app" / "Ttb.LabelWeb.exe"
BASE = ""


def request(path: str, method: str = "GET") -> dict:
    with urllib.request.urlopen(urllib.request.Request(BASE + path, method=method), timeout=10) as response:
        return json.loads(response.read())


def main() -> None:
    global BASE
    if not APP.exists():
        raise SystemExit("Publish the local application before running this test.")
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        port = listener.getsockname()[1]
    BASE = f"http://127.0.0.1:{port}"
    process = subprocess.Popen(
        [str(APP), "--no-open-browser", "--listen-url", BASE],
        cwd=APP.parent,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    try:
        for _ in range(60):
            try:
                config = request("/api/config")
                break
            except Exception:
                if process.poll() is not None:
                    raise RuntimeError("The local web application exited during startup.")
                time.sleep(0.25)
        else:
            raise RuntimeError("The local web application did not start within 15 seconds.")
        assert config["localUrl"] == BASE
        assert config["syntheticDemoAvailable"] is True
        assert config["rulesVersion"] != "unknown"

        collision = subprocess.run(
            [str(APP), "--no-open-browser", "--listen-url", BASE],
            cwd=APP.parent,
            capture_output=True,
            text=True,
            timeout=15,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        assert collision.returncode == 2
        assert "already in use" in collision.stderr

        job = request("/api/jobs/demo", "POST")
        deadline = time.monotonic() + 180
        while not job["isComplete"] and time.monotonic() < deadline:
            time.sleep(0.5)
            job = request("/api/jobs/" + job["id"])
        assert job["isComplete"], "Synthetic demonstration timed out."
        assert job["total"] == 60
        assert job["summary"]["approved"] == 30
        assert job["summary"]["rejected"] == 30
        assert all(item["synthetic"] for item in job["items"])
        assert not any(item["status"] in {"malformed_input", "external_service_unavailable"} for item in job["items"])
        print("Local web smoke test passed: 60 files, 30 approved, 30 rejected.")
    finally:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()


if __name__ == "__main__":
    main()
