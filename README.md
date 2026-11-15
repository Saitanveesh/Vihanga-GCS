# Vihanga — Standalone Ground Control Platform

This is the **independent Vihanga rewrite**. Its working tree contains **no Mission Planner source, plugins, binaries or runtime dependencies**. The previous Vihanga repository branches remain available as historical reference and as a rollback path. This branch has a new source tree, but it still shares the original Git history; a dedicated new repository is required for independent Git history.

## Current executable scope

- Cross-platform browser-based operations console built with React and TypeScript.
- Local FastAPI control-plane foundation with SQLite persistence.
- Register aircraft identities as **OFFLINE** until a trusted, authenticated device link is implemented.
- Create, validate, list and inspect immutable-revision mission **drafts**. Calculated route length is advisory, not navigation/airworthiness approval.
- Explicit capability status: physical flight commands are **NOT IMPLEMENTED**, with no physical MAVLink downlink or device connection.
- No simulated telemetry is misrepresented as live; no outbound vehicle-command API exists.

## Run on Windows

From this branch checkout (with Python 3.12+ and Node 20+):

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-Vihanga-Standalone.ps1
~~~

Open http://127.0.0.1:5173. The API is bound to 127.0.0.1:8010 and the Vite development server proxies /api to it.

## Run on Linux

~~~bash
python3 -m venv .venv
. .venv/bin/activate
pip install -e "./backend[dev]"
python -m uvicorn vihanga.app:app --host 127.0.0.1 --port 8010 &
cd console
npm install
npm run dev
~~~

Tests: \`python -m pytest -q backend/tests\`; front-end build: \`cd console && npm run build\`.

## Boundary

This is an initial **software development increment**, not a finished product or a flight-certified system. Aircraft registration does not connect a drone. Mission drafting does not upload or execute it. This development API has no operator authentication and must remain on loopback; do not expose it to public networks. The intended production architecture is documented in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

### Engineering release rule

No physical command adapter will be activated until protocol interoperability, authenticated identity, deterministic command/ACK semantics, RC precedence, failsafes and SITL/HIL/propeller-free bench acceptance have separately passed. The working legacy Vihanga GCS is not modified by this branch.
