"""Loopback-only fault boundary for the disposable joined federation test.

Successful writes always come from the actual Keycloak. No request headers,
tokens, response bodies or credentials are logged by this proxy.
"""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
import socket
import threading
import urllib.error
import urllib.request


class FaultState:
    def __init__(self):
        self.lock = threading.Lock()
        self.duplicate_users = False
        self.offline = False
        self.drop_path = None
        self.outages = self.duplicates = self.deletes = self.grants = 0
        self.committed = threading.Event()
        self.release = threading.Event()


class FederationProxy(BaseHTTPRequestHandler):
    upstream = "http://127.0.0.1:18084"
    protocol_version = "HTTP/1.1"

    def log_message(self, *_):
        pass

    def do_GET(self): self.forward()
    def do_POST(self): self.forward()
    def do_PUT(self): self.forward()
    def do_DELETE(self): self.forward()

    def reply(self, status, content=b"", content_type="application/json"):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(content)))
        self.end_headers()
        if content:
            self.wfile.write(content)

    def forward(self):
        # Never become an arbitrary URL proxy, even in the isolated fixture.
        if not any(self.path.startswith(prefix) for prefix in self.server.allowed_paths):
            self.reply(404)
            return
        length = int(self.headers.get("Content-Length", "0"))
        if not 0 <= length <= 1024 * 1024:
            self.reply(413)
            self.close_connection = True
            return
        body = self.rfile.read(length) if length else None
        state = self.server.faults
        with state.lock:
            if state.offline:
                state.outages += 1
                self.reply(503)
                return
        headers = {k: v for k, v in self.headers.items()
                   if k.lower() in ("authorization", "accept", "content-type")}
        try:
            request = urllib.request.Request(self.upstream + self.path, data=body,
                                             headers=headers, method=self.command)
            # Redirects must not leak a scoped token to another server.
            class NoRedirect(urllib.request.HTTPRedirectHandler):
                def redirect_request(self, *_): return None
            opener = urllib.request.build_opener(NoRedirect())
            try:
                response = opener.open(request, timeout=20)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                status = response.status
                content = response.read(4 * 1024 * 1024 + 1)
                content_type = response.headers.get("Content-Type", "application/json")
            if len(content) > 4 * 1024 * 1024:
                self.reply(502)
                return
            with state.lock:
                if (state.duplicate_users and self.command == "GET" and status == 200
                        and "/users?first=0&" in self.path):
                    users = json.loads(content)
                    if users:
                        content = json.dumps(users + [users[0]]).encode()
                        state.duplicates += 1
                if "/role-mappings/clients/" in self.path and status == 204:
                    if self.command == "POST": state.grants += 1
                    if self.command == "DELETE": state.deletes += 1
                lose = self.command == "DELETE" and status == 204 and self.path == state.drop_path
                if lose:
                    state.drop_path = None
                    state.committed.set()
            if lose:
                # Keycloak has committed; the worker has not received success.
                # The caller kills it, causing the local DB transaction to roll back.
                state.release.wait(timeout=25)
                self.close_connection = True
                try: self.connection.shutdown(socket.SHUT_RDWR)
                except OSError: pass
                return
            self.reply(status, content, content_type)
        except (OSError, ValueError):
            self.close_connection = True


def start_proxy(realm):
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("PERMORA_SYSTEM_TESTS") != "isolated":
        raise RuntimeError("Fault proxy is restricted to the disposable CI fixture")
    if not realm.startswith("permora-federation-") or not realm.replace("-", "").isalnum():
        raise ValueError("Unapproved disposable realm")
    server = ThreadingHTTPServer(("127.0.0.1", 0), FederationProxy)
    server.allowed_paths = ("/admin/realms/" + realm + "/", "/realms/" + realm + "/")
    server.faults = FaultState()
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    return server, thread
