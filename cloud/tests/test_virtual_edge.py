"""Full HTTP request/claim/ACK path with two independent virtual vehicles."""
from __future__ import annotations

import socket
import time
from threading import Thread

import httpx
import jwt
import pytest
import uvicorn

from fastapi.testclient import TestClient

from cloud.app.main import create_app
from cloud.tools.sim_edge import VirtualEdge
from cloud.tests.test_control_plane import (
    TEST_SECRET, acquire, approve, config, draft, jwt_header, register, start,
)


@pytest.fixture(params=["asgi", "tcp"])
def virtual_http_client(monkeypatch, request):
    monkeypatch.setenv("AEROHELM_VIRTUAL_DISPATCH_ENABLED", "true")
    app = create_app(config())
    if request.param == "asgi":
        with TestClient(app) as client:
            yield client
        return

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        origin = f"http://127.0.0.1:{listener.getsockname()[1]}"
        server = uvicorn.Server(uvicorn.Config(app, log_level="warning", ws="none"))
        thread = Thread(target=server.run, kwargs={"sockets": [listener]}, daemon=True)
        thread.start()
        try:
            deadline = time.monotonic() + 5
            while thread.is_alive() and not server.started and time.monotonic() < deadline:
                time.sleep(.01)
            assert server.started, "Loopback simulator API did not start"
            with httpx.Client(base_url=origin, trust_env=False, timeout=5) as client:
                yield client
        finally:
            server.should_exit = True
            thread.join(timeout=5)
            assert not thread.is_alive(), "Loopback simulator API did not shut down"


def test_two_virtual_devices_complete_multi_uav_workflow(virtual_http_client):
    client = virtual_http_client
    assert register(client, "SIM-A", 12.97).status_code == 201
    assert register(client, "SIM-B", 12.99).status_code == 201
    response = draft(client)
    assert response.status_code == 201
    mission = response.json()
    assert approve(client, mission).status_code == 200
    assert acquire(client, "SIM-A").status_code == 200
    assert acquire(client, "SIM-B").status_code == 200
    assert start(client, mission).status_code == 202

    # Devices receive an identity-scoped credential and no MAVLink stream.
    now = int(time.time())

    def token(name):
        return jwt.encode({
            "iss": "aerohelm-cloud-test", "aud": "aerohelm-cloud",
            "sub": "edge-" + name, "tenant": "TENANT-A", "kind": "device",
            "vehicle_id": name, "scope": "jobs:claim jobs:ack",
            "iat": now, "nbf": now, "exp": now + 300,
        }, TEST_SECRET, algorithm="HS256")

    for name in ("SIM-A", "SIM-B"):
        edge = VirtualEdge(client, device_id=name, bearer=token(name))
        first = edge.poll_once()
        assert first["status"] == "COMPLETED_SIM"
        assert first["flight_was_performed"] is False
        assert edge.poll_once() == {"status": "IDLE_SIM"}

    result = client.get("/api/v1/missions/" + mission["id"],
                        headers=jwt_header()).json()
    assert result["status"] == "COMPLETED_SIM"


def test_virtual_adapter_refuses_hardware_identity():
    with pytest.raises(ValueError):
        VirtualEdge(None, device_id="PIXHAWK-REAL", bearer="nope")
