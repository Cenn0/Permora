"""Exercise the fault boundary itself without Docker or IAM credentials."""
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import http.client
import io
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace
import threading
import unittest
from unittest.mock import patch
import urllib.error
import urllib.request

from ci_federation_faults import FederationProxy, FaultState, read_request_body, start_proxy

PREFIX = "/admin/realms/permora-federation-unit/"


class Upstream(BaseHTTPRequestHandler):
    def log_message(self, *_): pass

    def do_GET(self):
        self.server.reads += 1
        if self.path.endswith("redirect"):
            self.send_response(302)
            self.send_header("Location", self.server.url + PREFIX + "unexpected")
            self.end_headers()
            return
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b'[{"id":"synthetic-fixture"}]')

    def do_DELETE(self):
        self.server.deletes += 1
        length = int(self.headers.get("Content-Length", "0"))
        payload = self.rfile.read(length) if length else None
        if payload is None:
            self.server.roles.clear()
        else:
            for role in json.loads(payload): self.server.roles.discard(role["id"])
        self.send_response(204)
        self.end_headers()


@contextmanager
def fixture():
    upstream = ThreadingHTTPServer(("127.0.0.1", 0), Upstream)
    upstream.url = "http://127.0.0.1:" + str(upstream.server_port)
    upstream.reads = upstream.deletes = 0
    upstream.roles = {"target", "unrelated"}
    handler = type("UnitProxy", (FederationProxy,), {"upstream": upstream.url})
    proxy = ThreadingHTTPServer(("127.0.0.1", 0), handler)
    proxy.allowed_paths = (PREFIX,)
    proxy.faults = FaultState()
    proxy.faults.proxy_port = proxy.server_port
    threads = [threading.Thread(target=s.serve_forever, daemon=True) for s in (upstream, proxy)]
    for thread in threads: thread.start()

    def request(path, method="GET"):
        req = urllib.request.Request("http://127.0.0.1:" + str(proxy.server_port) + path, method=method)
        try:
            response = urllib.request.urlopen(req, timeout=3)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            raw = response.read()
            return response.status, json.loads(raw) if raw else None
    try:
        yield proxy.faults, upstream, request
    finally:
        proxy.faults.release.set()
        for server in (proxy, upstream):
            server.shutdown()
            server.server_close()
        for thread in threads: thread.join(timeout=3)


class FederationFaultTests(unittest.TestCase):
    def test_normal_reads_and_realm_boundary(self):
        with fixture() as (_, upstream, request):
            self.assertEqual(request(PREFIX + "users?first=0&max=100"),
                             (200, [{"id": "synthetic-fixture"}]))
            self.assertEqual(request("/admin/realms/foreign/users")[0], 404)
            self.assertEqual(upstream.reads, 1)

    def test_duplicate_only_the_first_users_page(self):
        with fixture() as (faults, _, request):
            faults.duplicate_users = True
            self.assertEqual(len(request(PREFIX + "users?first=0&max=100")[1]), 2)
            self.assertEqual(len(request(PREFIX + "users?first=100&max=100")[1]), 1)
            self.assertEqual(len(request(PREFIX + "groups?first=0&max=100")[1]), 1)
            self.assertEqual(faults.duplicates, 1)

    def test_outage_never_forwards_a_write(self):
        with fixture() as (faults, upstream, request):
            faults.offline = True
            self.assertEqual(request(PREFIX + "groups/g/role-mappings/clients/c", "DELETE")[0], 503)
            self.assertEqual((upstream.deletes, faults.deletes, faults.outages), (0, 0, 1))

    def test_response_loss_requires_an_actual_committed_delete(self):
        with fixture() as (faults, upstream, request):
            route = PREFIX + "groups/g/role-mappings/clients/c"
            faults.drop_path = route
            outcome = []
            def deleting():
                try: outcome.append(request(route, "DELETE"))
                except OSError: outcome.append("connection-lost")
            client = threading.Thread(target=deleting)
            client.start()
            try:
                self.assertTrue(faults.committed.wait(timeout=2))
                self.assertEqual((upstream.deletes, faults.deletes), (1, 1))
                self.assertIsNone(faults.drop_path)
            finally:
                faults.release.set()
                client.join(timeout=4)
            self.assertEqual(outcome, ["connection-lost"])

    def test_redirects_do_not_forward_the_request_again(self):
        with fixture() as (_, upstream, request):
            self.assertEqual(request(PREFIX + "redirect")[0], 302)
            self.assertEqual(upstream.reads, 1)

    def test_chunked_delete_preserves_unrelated_remote_role(self):
        with fixture() as (faults, upstream, _):
            connection = http.client.HTTPConnection("127.0.0.1", faults.proxy_port, timeout=3)
            try:
                connection.request("DELETE", PREFIX + "groups/g/role-mappings/clients/c",
                    body=iter([b'[{"id":', b'"target"}]']),
                    headers={"Content-Type": "application/json"}, encode_chunked=True)
                response = connection.getresponse()
                self.assertEqual(response.status, 204)
                response.read()
            finally:
                connection.close()
            self.assertEqual(upstream.roles, {"unrelated"})
            self.assertEqual(faults.deletes, 1)

    def test_content_length_body_is_preserved(self):
        handler = SimpleNamespace(headers={"Content-Length": "3"}, rfile=io.BytesIO(b"123"))
        self.assertEqual(read_request_body(handler), b"123")

    def test_invalid_or_oversized_framing_is_rejected(self):
        for headers, data in (
            ({"Transfer-Encoding": "gzip"}, b""),
            ({"Transfer-Encoding": "chunked", "Content-Length": "3"}, b""),
            ({"Transfer-Encoding": "chunked"}, b"invalid\r\n"),
            ({"Transfer-Encoding": "chunked"}, b"3\r\nx"),
            ({"Transfer-Encoding": "chunked"}, b"9\r\n123456789\r\n0\r\n\r\n"),
            ({"Content-Length": "-1"}, b""),
            ({"Content-Length": "9"}, b"123456789"),
            ({"Content-Length": "3"}, b"12"),
        ):
            with self.subTest(headers=headers, data=data):
                handler = SimpleNamespace(headers=headers, rfile=io.BytesIO(data))
                with self.assertRaises(ValueError): read_request_body(handler, limit=8)

    def test_outside_isolated_ci_fails_before_binding(self):
        with patch.dict("os.environ", {}, clear=True), patch("ci_federation_faults.ThreadingHTTPServer") as server:
            with self.assertRaises(RuntimeError): start_proxy("permora-federation-unit")
            server.assert_not_called()

    def test_worker_diagnostic_runpy_entrypoint_loads_shared_decoder(self):
        # Match the workflow's root-level runpy invocation without starting
        # Docker or running its destructive __main__ path. A fresh interpreter
        # prevents this test suite's cached helper module from hiding a failure.
        result = subprocess.run([sys.executable, "-c",
            "import runpy; n=runpy.run_path('scripts/ci-worker-system.py'); "
            "assert callable(n['read_request_body'])"],
            cwd=Path(__file__).resolve().parents[1], capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == "__main__":
    unittest.main()
