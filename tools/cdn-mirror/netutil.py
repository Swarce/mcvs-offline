"""Tiny HTTP helper (standard library only) with:
  * no automatic redirect following (the redirector's 301 is data we want),
  * optional proxy support via CONNECT tunnels for both http:// and https://
    targets (or classic absolute-URI forwarding for http:// with
    proxy_mode="forward"),
  * Range requests and streaming bodies.
"""

import http.client
import os
import socket
import ssl
import time
import random
import urllib.parse

USER_AGENT = "Dalvik/2.1.0 (Linux; U; Android 10; Pixel Build/QQ3A.200805.001) mcvs-cdn-mirror/1.0"


class HttpError(Exception):
    pass


def _no_proxy_match(host):
    np = os.environ.get("NO_PROXY") or os.environ.get("no_proxy") or ""
    for pat in [p.strip() for p in np.split(",") if p.strip()]:
        if pat == "*":
            return True
        p = pat.lstrip("*")
        if p.startswith("."):
            if host.endswith(p) or host == p[1:]:
                return True
        elif host == p or host.endswith("." + p):
            return True
    return False


class Client(object):
    def __init__(self, proxy=None, proxy_mode="connect", cafile=None, timeout=60,
                 user_agent=USER_AGENT):
        if proxy is None:
            proxy = (os.environ.get("HTTPS_PROXY") or os.environ.get("https_proxy")
                     or os.environ.get("HTTP_PROXY") or os.environ.get("http_proxy") or "")
        self.proxy = urllib.parse.urlsplit(proxy) if proxy else None
        self.proxy_mode = proxy_mode
        self.timeout = timeout
        self.user_agent = user_agent
        cafile = cafile or os.environ.get("SSL_CERT_FILE") or None
        self.ctx = ssl.create_default_context(cafile=cafile) if cafile else ssl.create_default_context()

    def _conn(self, scheme, host, port):
        use_proxy = self.proxy is not None and not _no_proxy_match(host)
        if not use_proxy:
            if scheme == "https":
                return http.client.HTTPSConnection(host, port, timeout=self.timeout, context=self.ctx), False
            return http.client.HTTPConnection(host, port, timeout=self.timeout), False
        ph = self.proxy.hostname
        pp = self.proxy.port or (443 if self.proxy.scheme == "https" else 80)
        hdrs = {}
        if self.proxy.username:
            import base64
            tok = base64.b64encode(("%s:%s" % (urllib.parse.unquote(self.proxy.username),
                                               urllib.parse.unquote(self.proxy.password or ""))).encode()).decode()
            hdrs["Proxy-Authorization"] = "Basic " + tok
        if scheme == "https":
            c = http.client.HTTPSConnection(ph, pp, timeout=self.timeout, context=self.ctx)
            c.set_tunnel(host, port, headers=hdrs)
            return c, False
        if self.proxy_mode == "forward":
            c = http.client.HTTPConnection(ph, pp, timeout=self.timeout)
            return c, True  # absolute-URI request
        c = http.client.HTTPConnection(ph, pp, timeout=self.timeout)
        c.set_tunnel(host, port, headers=hdrs)
        return c, False

    def open(self, method, url, headers=None):
        """Returns (conn, response). Caller must close conn."""
        u = urllib.parse.urlsplit(url)
        scheme = u.scheme.lower()
        if scheme not in ("http", "https"):
            raise HttpError("unsupported scheme in %s" % url)
        port = u.port or (443 if scheme == "https" else 80)
        conn, absolute = self._conn(scheme, u.hostname, port)
        path = u.path or "/"
        if u.query:
            path += "?" + u.query
        target = url if absolute else path
        h = {"User-Agent": self.user_agent, "Accept-Encoding": "identity", "Connection": "close"}
        if headers:
            h.update(headers)
        if absolute:
            h["Host"] = u.netloc
        conn.request(method, target, headers=h)
        resp = conn.getresponse()
        return conn, resp

    def fetch(self, method, url, headers=None, max_body=8 * 1024 * 1024):
        """Small request: returns (status, headers_dict_lower, raw_header_list, body)."""
        conn, resp = self.open(method, url, headers)
        try:
            body = b""
            if method != "HEAD":
                body = resp.read(max_body + 1)
                if len(body) > max_body:
                    raise HttpError("body larger than %d bytes for %s" % (max_body, url))
            hl = resp.getheaders()
            hd = {k.lower(): v for k, v in hl}
            return resp.status, hd, hl, body
        finally:
            conn.close()


class RetryableStatus(HttpError):
    """429 / 5xx answer; .retry_after is the server's Retry-After in seconds (or None)."""

    def __init__(self, status, retry_after, url):
        HttpError.__init__(self, "HTTP %d (retry-after %s) for %s" % (status, retry_after, url))
        self.status = status
        self.retry_after = retry_after


RETRYABLE_STATUS = (429, 500, 502, 503, 504)


def check_retryable(status, headers, url):
    if status in RETRYABLE_STATUS:
        ra = headers.get("retry-after") if isinstance(headers, dict) else None
        try:
            ra = float(ra) if ra is not None else None
        except ValueError:
            ra = None
        raise RetryableStatus(status, ra, url)


class Throttle(object):
    """Global minimum interval between requests (thread safe)."""

    def __init__(self, min_interval):
        import threading
        self.min_interval = min_interval
        self.lock = threading.Lock()
        self.next_t = 0.0

    def wait(self):
        if self.min_interval <= 0:
            return
        with self.lock:
            now = time.time()
            t = max(now, self.next_t)
            self.next_t = t + self.min_interval
        if t > now:
            time.sleep(t - now)

    def penalize(self, seconds):
        with self.lock:
            self.next_t = max(self.next_t, time.time() + seconds)


def backoff_sleep(attempt, base=2.0, cap=120.0):
    t = min(cap, base * (2 ** attempt)) * (0.5 + random.random() / 2)
    time.sleep(t)
    return t


def format_headers(status, header_list, first_line=None):
    lines = [first_line or ("HTTP %d" % status)]
    for k, v in header_list:
        lines.append("%s: %s" % (k, v))
    return "\n".join(lines) + "\n"


NET_ERRORS = (OSError, socket.timeout, http.client.HTTPException, ssl.SSLError, HttpError)
