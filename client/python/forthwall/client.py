"""Sequential, bounded client. Mutations are never automatically retried."""
from __future__ import annotations

import logging
import os
from pathlib import Path
from urllib.parse import urlsplit

from websockets.sync.client import connect
from . import forthwall_pb2 as pb


def default_token_file() -> Path:
    if path := os.getenv("FORTHWALL_TOKEN_FILE"):
        return Path(path).expanduser()
    return Path.home() / ".local/share/Steam/steamapps/compatdata/244850/pfx/drive_c/users/steamuser/AppData/Roaming/SpaceEngineers/Storage/ForthWallAttenna/gateway.token"


class GatewayError(RuntimeError):
    def __init__(self, response: pb.Response):
        self.response = response
        super().__init__(f"{pb.Error.Code.Name(response.error.code)}: {response.error.message} (mutation: {pb.MutationOutcome.Name(response.mutation_outcome)})")


class Client:
    def __init__(self, uri="ws://127.0.0.1:18751/forthwall", token_file=None, timeout_ms=10000):
        address = urlsplit(uri)
        if address.scheme != "ws" or address.hostname not in {"127.0.0.1", "localhost"} or address.username or address.password or address.path != "/forthwall" or address.query or address.fragment:
            raise ValueError("The local prototype requires ws://127.0.0.1:PORT/forthwall (or localhost).")
        self.uri = uri
        self.token_file = Path(token_file) if token_file else default_token_file()
        self.timeout_ms = min(30000, max(1, timeout_ms))
        self.session_id = ""
        self._id = 0
        self._ws = None

    def __enter__(self):
        token = self.token_file.read_text().strip()
        if len(token) < 32 or any(ord(c) < 33 or ord(c) > 126 for c in token):
            raise ValueError("Invalid gateway token file.")
        # Never inherit an application's debug logger: it could log the auth header.
        quiet = logging.Logger("forthwall.transport", level=logging.CRITICAL + 1)
        quiet.addHandler(logging.NullHandler())
        self._ws = connect(self.uri, subprotocols=["forthwall.v1"],
                           additional_headers={"Authorization": "Bearer " + token},
                           compression=None, proxy=None, logger=quiet, legacy=True,
                           open_timeout=5, close_timeout=1, ping_interval=10,
                           max_size=1024 * 1024, max_queue=4)
        return self

    def __exit__(self, *_):
        if self._ws is not None:
            self._ws.close()
            self._ws = None

    def request(self, operation: str, message, hub_id=0) -> pb.Response:
        if self._ws is None:
            raise RuntimeError("Open the Client with a with statement first.")
        self._id += 1
        request = pb.Request(id=self._id, session_id=self.session_id,
                             hub_id=hub_id, timeout_ms=self.timeout_ms)
        getattr(request, operation).CopyFrom(message)
        data = pb.Envelope(protocol_version=1, request=request).SerializeToString()
        if len(data) > 1024 * 1024:
            raise ValueError("Request exceeds 1 MiB.")
        try:
            self._ws.send(data)
            raw = self._ws.recv(timeout=self.timeout_ms / 1000 + 2)
            if not isinstance(raw, bytes):
                raise ValueError("Gateway sent a non-binary message.")
            envelope = pb.Envelope.FromString(raw)
            if envelope.protocol_version != 1 or envelope.WhichOneof("body") != "response" or envelope.response.id != self._id:
                raise ValueError("Unexpected gateway response.")
        except Exception:
            # Closing cancels queued work; late replies cannot pollute another call.
            self.__exit__()
            raise
        response = envelope.response
        if response.HasField("error"):
            if response.error.code in (pb.Error.WRONG_SESSION, pb.Error.WORLD_UNLOADED):
                self.session_id = ""
            raise GatewayError(response)
        if operation == "list_hubs":
            self.session_id = response.session_id
        elif response.session_id != self.session_id:
            self.session_id = ""
            raise ValueError("World changed before response; refresh hubs.")
        return response

    def hubs(self):
        return self.request("list_hubs", pb.ListHubs()).blocks.blocks
