#!/usr/bin/env python3
"""Squad exchange server for the ONLINE build of Fortnite Chest Counter.

Python 3.8+, standard library only:   python3 server.py

What it keeps, per finished match a player reported: Epic id, display name, match id (a hint), start time, number of
chests (and how many destroyed), and the Epic ids of the human teammates. Nothing else (no treasures, no IP address).
A player gets another player's count only if BOTH reported each other as teammates in the same match.

Environment: PORT (8787), HOST (127.0.0.1), DATA_FILE (./data.json), BEHIND_PROXY=1 (read X-Forwarded-For for the
rate limit), RETENTION_DAYS (14)
"""
import hashlib
import hmac
import json
import os
import re
import threading
import time
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

PORT = int(os.environ.get("PORT", "8787"))
HOST = os.environ.get("HOST", "127.0.0.1")
DATA_FILE = os.environ.get("DATA_FILE", os.path.join(os.path.dirname(os.path.abspath(__file__)), "data.json"))
BEHIND_PROXY = os.environ.get("BEHIND_PROXY") == "1"
RETENTION_S = int(os.environ.get("RETENTION_DAYS", "14")) * 86400
PAIR_WINDOW_S = 10 * 60        # two reports of one match start within this
FETCH_WINDOW_S = 48 * 3600     # matches older than this are not offered any more
MAX_BODY = 4096
MAX_CHESTS = 300

ID = re.compile(r"^[A-Za-z0-9-]{8,64}$")
KEY = re.compile(r"^[A-Za-z0-9]{32,128}$")

lock = threading.Lock()
db = {"keys": {}, "reports": {}}   # keys: epic(lower) -> sha256(install key); reports: "epic|match" -> report
try:
    with open(DATA_FILE, encoding="utf-8") as f:
        db = json.load(f)
except (OSError, ValueError):
    pass
db.setdefault("keys", {})
db.setdefault("reports", {})


def save():
    tmp = DATA_FILE + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(db, f)
    os.replace(tmp, DATA_FILE)


def purge():
    limit = time.time() - RETENTION_S
    for k in [k for k, r in db["reports"].items() if r["ts"] < limit]:
        del db["reports"][k]


def parse_start(text):
    try:
        t = datetime.fromisoformat(str(text).replace("Z", "+00:00"))
        if t.tzinfo is None:
            t = t.replace(tzinfo=timezone.utc)
        return t.timestamp()
    except ValueError:
        return None


hits = {}


def limited(ip):
    now = time.time()
    h = [t for t in hits.get(ip, []) if now - t < 60]
    h.append(now)
    hits[ip] = h
    if len(hits) > 5000:
        for k in [k for k, v in hits.items() if not any(now - t < 60 for t in v)]:
            del hits[k]
    return len(h) > 90


def authenticate(epic, key):
    """The install key proves 'same installation as the first time' for an Epic id. The first key seen is kept."""
    if not isinstance(epic, str) or not ID.match(epic) or not KEY.match(key or ""):
        return 400
    e, h = epic.lower(), hashlib.sha256(key.encode()).hexdigest()
    if e not in db["keys"]:
        db["keys"][e] = h
        return 0
    return 0 if hmac.compare_digest(db["keys"][e], h) else 403


def is_int(v):
    return isinstance(v, int) and not isinstance(v, bool)


def report(body, key):
    try:
        b = json.loads(body)
        assert isinstance(b, dict)
    except (ValueError, AssertionError):
        return 400, {"error": "bad json"}
    epic, match, chests, destroyed = b.get("epic"), b.get("match"), b.get("chests"), b.get("destroyed")
    mates = b.get("mates", [])
    start = parse_start(b.get("start"))
    if not isinstance(epic, str) or not isinstance(match, str) or not ID.match(match):
        return 400, {"error": "bad ids"}
    if not (is_int(chests) and 0 <= chests <= MAX_CHESTS and is_int(destroyed) and 0 <= destroyed <= chests):
        return 400, {"error": "bad numbers"}
    now = time.time()
    if start is None or start > now + 300 or start < now - 3 * 86400:
        return 400, {"error": "bad start"}
    if not isinstance(mates, list) or len(mates) > 8 or not all(isinstance(m, str) and ID.match(m) for m in mates):
        return 400, {"error": "bad mates"}
    with lock:
        auth = authenticate(epic, key)
        if auth:
            return auth, {"error": "this Epic id belongs to another installation" if auth == 403 else "bad credentials"}
        name = re.sub(r"[\x00-\x1f]", "", str(b.get("name") or ""))[:40]
        me = epic.lower()
        db["reports"][me + "|" + match.lower()] = {
            "epic": me, "name": name, "match": match, "start": start, "chests": chests, "destroyed": destroyed,
            "mates": sorted({m.lower() for m in mates} - {me}), "ts": now,
        }
        purge()
        save()
    return 200, {"ok": True}


def mates(epic, key):
    """Teammates' counts for the requester's own recent matches, only where both sides reported each other."""
    with lock:
        auth = authenticate(epic, key)
        if auth:
            return auth, {"error": "bad credentials"}
        save()
        me, now = epic.lower(), time.time()
        reports = list(db["reports"].values())
    mine = [r for r in reports if r["epic"] == me and now - r["start"] < FETCH_WINDOW_S]
    others = [r for r in reports if r["epic"] != me and now - r["start"] < FETCH_WINDOW_S + PAIR_WINDOW_S]
    cards = []
    for a in mine:
        for m in a["mates"]:
            cand = [o for o in others if o["epic"] == m and me in o["mates"]
                    and (o["match"].lower() == a["match"].lower() or abs(o["start"] - a["start"]) <= PAIR_WINDOW_S)]
            if not cand:
                continue
            # the same match id wins, then the closest start time
            cand.sort(key=lambda o: (o["match"].lower() != a["match"].lower(), abs(o["start"] - a["start"])))
            o = cand[0]
            cards.append({"match": a["match"], "epic": o["epic"], "name": o["name"], "chests": o["chests"],
                          "destroyed": o["destroyed"],
                          "start": datetime.fromtimestamp(a["start"], timezone.utc).isoformat().replace("+00:00", "Z")})
    return 200, {"cards": cards}


class Handler(BaseHTTPRequestHandler):
    server_version = "SquadServer"
    sys_version = ""

    def log_message(self, *args):  # no access log: nothing about the players is written to disk besides data.json
        pass

    def ip(self):
        if BEHIND_PROXY:
            fwd = self.headers.get("X-Forwarded-For", "").split(",")[0].strip()
            if fwd:
                return fwd
        return self.client_address[0]

    def send(self, code, body):
        data = json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def handle_any(self, method):
        if limited(self.ip()):
            return self.send(429, {"error": "slow down"})
        url = urlparse(self.path)
        key = self.headers.get("X-Install-Key", "")
        try:
            if method == "GET" and url.path == "/health":
                return self.send(200, {"ok": True})
            if method == "POST" and url.path == "/v1/report":
                length = int(self.headers.get("Content-Length", "0") or 0)
                if length > MAX_BODY:
                    return self.send(413, {"error": "too large"})
                return self.send(*report(self.rfile.read(length).decode("utf-8", "replace"), key))
            if method == "GET" and url.path == "/v1/mates":
                return self.send(*mates(parse_qs(url.query).get("epic", [""])[0], key))
            self.send(404, {"error": "not found"})
        except Exception:
            self.send(500, {"error": "server error"})

    def do_GET(self):
        self.handle_any("GET")

    def do_POST(self):
        self.handle_any("POST")


if __name__ == "__main__":
    srv = ThreadingHTTPServer((HOST, PORT), Handler)
    print(f"squad server on http://{HOST}:{PORT}", flush=True)
    srv.serve_forever()
