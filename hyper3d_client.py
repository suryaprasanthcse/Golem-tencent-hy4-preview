"""Hyper3D Rodin client: text prompt -> generation job -> poll -> download the .glb.

Rodin API (docs.hyper3d.ai, Gen-2.5):
  POST /api/v2/rodin     multipart form -> {uuid, jobs: {uuids, subscription_key}, consumed}
  POST /api/v2/status    {"subscription_key"} -> {jobs: [{uuid, status}]},
                         status is Waiting | Generating | Done | Failed
  POST /api/v2/download  {"task_uuid"} -> {list: [{name, url}]}; URLs expire, so download at once

Every submitted job is recorded in assets/raw/<name>.job.json the moment it is
accepted, so an interrupted run can be resumed without paying again.

Usage:
  python hyper3d_client.py ping
  python hyper3d_client.py generate "wooden treasure chest with a hinged lid, closed" --name chest
  python hyper3d_client.py resume assets/raw/chest.job.json
"""

import argparse
import json
import re
import sys
import time
from datetime import datetime
from pathlib import Path

import requests

from golem_secrets import get_secret

API = "https://api.hyper3d.com/api/v2"
RAW_DIR = Path(__file__).resolve().parent / "assets" / "raw"
TIERS = ["Gen-2.5-Extreme-Low", "Gen-2.5-Low", "Gen-2.5-Medium", "Gen-2.5-High", "Gen-2.5-Extreme-High"]
DEFAULT_TIER = "Gen-2.5-Medium"
# The docs ask for polling from 5 s, backing off to at most 30 s.
POLL_FIRST, POLL_MAX = 5.0, 30.0


def _post(endpoint: str, **kwargs) -> requests.Response:
    """POST to the Rodin API; turns auth and request errors into readable exits."""
    headers = {"Authorization": f"Bearer {get_secret('HYPER3D_API_KEY')}"}
    response = requests.post(f"{API}/{endpoint}", headers=headers, timeout=60, **kwargs)
    if response.status_code == 401:
        raise SystemExit("Hyper3D rejected the API key (HTTP 401). Check HYPER3D_API_KEY in secrets.env.")
    if response.status_code >= 400 and response.status_code != 429:
        raise SystemExit(f"Hyper3D /{endpoint} failed: HTTP {response.status_code}: {response.text[:500]}")
    return response


def _now() -> str:
    return datetime.now().astimezone().isoformat(timespec="seconds")


def _save(job: dict) -> None:
    Path(job["manifest"]).write_text(json.dumps(job, indent=2), encoding="utf-8")


def submit(prompt: str, name: str, tier: str, quality_override: int | None) -> dict:
    """Start a text-to-3D job and record it before anything else can go wrong."""
    RAW_DIR.mkdir(parents=True, exist_ok=True)
    manifest = RAW_DIR / f"{name}.job.json"
    if manifest.exists():
        raise SystemExit(f"{manifest} already exists: pick another --name, or resume that job.")

    # Text fields still go as multipart/form-data, which the API requires.
    form = {"prompt": prompt, "tier": tier, "geometry_file_format": "glb"}
    if quality_override is not None:
        form["quality_override"] = str(quality_override)
    response = _post("rodin", files={key: (None, value) for key, value in form.items()})
    if response.status_code == 429:
        raise SystemExit("Hyper3D is throttling new jobs (HTTP 429). Wait a minute and retry.")
    body = response.json()

    job = {
        "name": name,
        "prompt": prompt,
        "tier": tier,
        "task_uuid": body["uuid"],
        "subscription_key": body["jobs"]["subscription_key"],
        "credits_consumed": body.get("consumed"),
        "submitted_at": _now(),
        "manifest": str(manifest),
    }
    _save(job)
    print(f"[submit] {name}: task {job['task_uuid']}, {job['credits_consumed']} credits, tier {tier}")
    return job


def wait(job: dict, timeout_minutes: float) -> None:
    """Poll until every job is Done; exit on Failed or timeout."""
    start, interval, last = time.monotonic(), POLL_FIRST, None
    while True:
        time.sleep(interval)
        response = _post("status", json={"subscription_key": job["subscription_key"]})
        elapsed = time.monotonic() - start
        if response.status_code == 429:
            interval = float(response.headers.get("Retry-After", POLL_MAX))
            print(f"[status] throttled, retrying in {interval:.0f} s")
            continue

        statuses = [j["status"] for j in response.json().get("jobs", [])]
        summary = ", ".join(statuses) or "no jobs yet"
        if summary != last:
            print(f"[status] {elapsed:5.0f} s  {summary}")
            last = summary
        if "Failed" in statuses:
            job["failed_at"] = _now()
            _save(job)
            raise SystemExit(f"Hyper3D reports the job Failed after {elapsed:.0f} s. Record: {job['manifest']}")
        if statuses and all(s == "Done" for s in statuses):
            job["generation_seconds"] = round(elapsed)
            _save(job)
            return
        if elapsed > timeout_minutes * 60:
            raise SystemExit(f"Still not Done after {timeout_minutes} min. Resume later: {job['manifest']}")
        interval = min(interval * 1.5, POLL_MAX)


def download(job: dict) -> Path:
    """Fetch the result files right away (their URLs expire). Returns the .glb path."""
    files = _post("download", json={"task_uuid": job["task_uuid"]}).json().get("list", [])
    glbs = [f for f in files if f["name"].lower().endswith(".glb")]
    if not glbs:
        raise SystemExit(f"No .glb in the download list: {[f['name'] for f in files]}")

    saved = []
    for i, item in enumerate(glbs):
        target = RAW_DIR / (f"{job['name']}.glb" if i == 0 else f"{job['name']}_{i}.glb")
        content = requests.get(item["url"], timeout=300).content
        if content[:4] != b"glTF":
            raise SystemExit(f"{item['name']} is not a binary glTF file (starts with {content[:4]!r}).")
        target.write_bytes(content)
        saved.append(str(target))
        print(f"[download] {target.name}: {len(content) / 1e6:.1f} MB")

    job["files_available"] = [f["name"] for f in files]
    job["glb"] = saved
    job["downloaded_at"] = _now()
    _save(job)
    return Path(saved[0])


def ping() -> None:
    """Check the key without spending credits: ask for the status of a job that doesn't exist."""
    response = _post("status", json={"subscription_key": "golem-auth-check"})
    print(f"Hyper3D API key accepted (HTTP {response.status_code} for a dummy status query; 401 would mean a bad key).")


def _slug(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "_", text.lower()).strip("_")[:40] or "asset"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("ping", help="check the API key (no credits used)")
    generate = commands.add_parser("generate", help="text prompt -> .glb in assets/raw/")
    generate.add_argument("prompt")
    generate.add_argument("--name", help="file name (default: from the prompt)")
    generate.add_argument("--tier", default=DEFAULT_TIER, choices=TIERS)
    generate.add_argument("--quality-override", type=int, help="passed through as quality_override (API max 20000)")
    generate.add_argument("--timeout", type=float, default=15, help="minutes to wait (default 15)")
    resume = commands.add_parser("resume", help="continue polling and downloading a recorded job")
    resume.add_argument("manifest", type=Path)
    resume.add_argument("--timeout", type=float, default=15)
    args = parser.parse_args()

    if args.command == "ping":
        ping()
        return 0
    if args.command == "generate":
        job = submit(args.prompt, args.name or _slug(args.prompt), args.tier, args.quality_override)
    else:
        job = json.loads(args.manifest.read_text(encoding="utf-8"))
    wait(job, args.timeout)
    glb = download(job)
    print(f"[done] {glb}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
