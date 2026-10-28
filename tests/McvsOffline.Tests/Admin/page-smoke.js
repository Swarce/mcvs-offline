#!/usr/bin/env node
// Smoke test of the settings page (server/Content/admin/index.html), design docs/design/settings-page.md §10.5 / §11.4.
//
//   node page-smoke.js <http://127.0.0.1:<port>/_mcvs/ui/#t=<ticket>>   against a live server (PageSmokeTests starts it)
//   node page-smoke.js --fixtures <index.html>                          against the built-in fixture API below
//
// The page is fetched over HTTP, its markup parsed into a minimal DOM that implements exactly the surface of §2.3 (any
// other member throws, so the page cannot quietly depend on more), and its inline script run in a vm context with a
// cookie-aware XMLHttpRequest over http. Then the scenario of §10.5: every screen, a settings change with the Back
// guard, logs, export as text and pasting the parts back, plus the re-creation boot (used ticket + cookie) and view mode
// (#news, no cookie). Exit code 0 when everything rendered without a script error or #banner-error; 1 otherwise.
// MCVS_ACORN=<path to acorn.js> additionally parses the script with ecmaVersion 5.
'use strict';

const fs = require('fs');
const http = require('http');
const vm = require('vm');
const crypto = require('crypto');

const UA = 'Mozilla/5.0 (Linux; Android 16; SM-S901B; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/141.0 Mobile Safari/537.36 page-smoke';
const failures = [];
const log = (msg) => process.stdout.write(msg + '\n');

function fail(msg) {
  failures.push(msg);
  log('FAIL ' + msg);
}

function check(cond, msg) {
  if (!cond) fail(msg);
  return !!cond;
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ------------------------------------------------------------------ HTML: a small parser for our own markup

const VOID = new Set(['area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'source', 'track', 'wbr']);
const RAW = new Set(['script', 'style', 'textarea', 'title']);

function decodeEntities(s) {
  return s.replace(/&(#x[0-9a-fA-F]+|#[0-9]+|amp|lt|gt|quot|apos|nbsp);/g, (m, e) => {
    if (e[0] === '#') return String.fromCodePoint(e[1] === 'x' ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10));
    return { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' }[e];
  });
}

class Node {
  constructor(tag, text) {
    this.tag = tag;                 // lower-case tag name, or '#text' / '#document'
    this.text = text || '';
    this.attrs = new Map();
    this.children = [];
    this.parent = null;
    this.listeners = {};
    this.style = {};
    this.props = {};                // value / checked / disabled once set by script or user
    this.scrollTop = 0;
  }

  attr(name) { return this.attrs.has(name) ? this.attrs.get(name) : null; }
}

function parseStyle(node, css) {
  for (const decl of css.split(';')) {
    const i = decl.indexOf(':');
    if (i < 0) continue;
    const name = decl.slice(0, i).trim().replace(/-([a-z])/g, (m, c) => c.toUpperCase());
    node.style[name] = decl.slice(i + 1).trim();
  }
}

function parseHtml(html) {
  const root = new Node('#document');
  const stack = [root];
  let scripts = [];
  let styles = [];
  let i = 0;
  const top = () => stack[stack.length - 1];
  const append = (n) => { n.parent = top(); top().children.push(n); };
  while (i < html.length) {
    if (html.startsWith('<!--', i)) {
      const e = html.indexOf('-->', i + 4);
      i = e < 0 ? html.length : e + 3;
    } else if (html.startsWith('<!', i)) {
      i = html.indexOf('>', i) + 1;
    } else if (html.startsWith('</', i)) {
      const e = html.indexOf('>', i);
      const name = html.slice(i + 2, e).trim().toLowerCase();
      for (let k = stack.length - 1; k > 0; k--) {
        if (stack[k].tag === name) { stack.length = k; break; }
      }
      i = e + 1;
    } else if (html[i] === '<' && /[a-zA-Z]/.test(html[i + 1] || '')) {
      let j = i + 1;
      while (j < html.length && /[a-zA-Z0-9-]/.test(html[j])) j++;
      const node = new Node(html.slice(i + 1, j).toLowerCase());
      // attributes
      for (;;) {
        while (/\s/.test(html[j])) j++;
        if (html[j] === '>' || html.startsWith('/>', j)) { j += html[j] === '>' ? 1 : 2; break; }
        let k = j;
        while (k < html.length && !/[\s=>]/.test(html[k])) k++;
        const name = html.slice(j, k).toLowerCase();
        let value = '';
        j = k;
        while (/\s/.test(html[j])) j++;
        if (html[j] === '=') {
          j++;
          while (/\s/.test(html[j])) j++;
          if (html[j] === '"' || html[j] === "'") {
            const q = html[j];
            const e = html.indexOf(q, j + 1);
            value = html.slice(j + 1, e);
            j = e + 1;
          } else {
            let e = j;
            while (e < html.length && !/[\s>]/.test(html[e])) e++;
            value = html.slice(j, e);
            j = e;
          }
        }
        node.attrs.set(name, decodeEntities(value));
      }
      if (node.attrs.has('style')) parseStyle(node, node.attrs.get('style'));
      append(node);
      if (RAW.has(node.tag)) {
        const close = html.toLowerCase().indexOf('</' + node.tag, j);
        const raw = html.slice(j, close);
        if (node.tag === 'script') scripts.push(raw);
        else if (node.tag === 'style') styles.push(raw);
        else { const t = new Node('#text', decodeEntities(raw)); t.parent = node; node.children.push(t); }
        i = html.indexOf('>', close) + 1;
      } else {
        if (!VOID.has(node.tag)) stack.push(node);
        i = j;
      }
    } else {
      const e = html.indexOf('<', i);
      const end = e < 0 ? html.length : e;
      append(new Node('#text', decodeEntities(html.slice(i, end))));
      i = end;
    }
  }
  return { root, scripts, styles };
}

function* walk(node) {
  yield node;
  for (const c of node.children) yield* walk(c);
}

function textOf(node) {
  if (node.tag === '#text') return node.text;
  let s = '';
  for (const c of node.children) s += textOf(c);
  return s;
}

// ------------------------------------------------------------------ the page: DOM surface of §2.3 and the runtime

class Page {
  constructor(label, origin, jar, options) {
    this.label = label;
    this.origin = origin;
    this.jar = jar;
    this.options = options || {};
    this.errors = [];
    this.pushes = 0;
    this.replaces = 0;
    // The WebView's session history of this document: entries pushed by the page, the current index, and the page's
    // own history.back() calls (each one a later traversal, like a browser).
    this.entries = [{ state: null }];
    this.index = 0;
    this.backs = 0;
    this.pending = 0;
    this.requests = [];
    this.clipboard = [];
    this.timers = new Set();
    this.listeners = { popstate: [], visibilitychange: [] };
    this.proxies = new WeakMap();   // Node -> proxy
    this.nodes = new WeakMap();     // proxy -> Node
    this.selected = null;
    this.closed = false;
  }

  error(where, e) {
    const msg = where + ': ' + (e && e.stack ? e.stack.split('\n').slice(0, 3).join(' | ') : String(e));
    this.errors.push(msg);
    log('  script error ' + msg);
  }

  guard(where, fn) {
    const page = this;
    return function () {
      if (page.closed) return undefined;
      try {
        return fn.apply(this, arguments);
      } catch (e) {
        page.error(where, e);
        return undefined;
      }
    };
  }

  surface(what, name) {
    throw new Error('page uses ' + what + '.' + String(name) + ', outside the DOM surface of design §2.3');
  }

  unwrap(p) {
    const n = this.nodes.get(p);
    if (!n) throw new Error('not a node of this page');
    return n;
  }

  wrap(node) {
    if (node === null || node === undefined) return null;
    let p = this.proxies.get(node);
    if (p) return p;
    const page = this;
    const isText = node.tag === '#text';
    const styleProxy = new Proxy(node.style, {
      get: (t, k) => (typeof k === 'symbol' ? undefined : (t[k] === undefined ? '' : t[k])),
      set: (t, k, v) => { t[k] = String(v); return true; },
    });
    const methods = {
      appendChild: (child) => { page.insert(node, page.unwrap(child), null); return child; },
      removeChild: (child) => {
        const c = page.unwrap(child);
        const idx = node.children.indexOf(c);
        if (idx < 0) throw new Error('removeChild: not a child');
        node.children.splice(idx, 1);
        c.parent = null;
        return child;
      },
      insertBefore: (child, ref) => { page.insert(node, page.unwrap(child), ref ? page.unwrap(ref) : null); return child; },
      setAttribute: (name, value) => {
        name = String(name).toLowerCase();
        node.attrs.set(name, String(value));
        if (name === 'style') parseStyle(node, String(value));
      },
      getAttribute: (name) => node.attr(String(name).toLowerCase()),
      addEventListener: (type, fn) => {
        if (['click', 'change', 'input'].indexOf(type) < 0) page.surface('addEventListener', type);
        (node.listeners[type] = node.listeners[type] || []).push(fn);
      },
      select: () => { page.selected = node; },
      focus: () => { page.focused = node; },
    };
    p = new Proxy({}, {
      get: (t, k) => {
        if (typeof k === 'symbol') return undefined;
        if (k === 'textContent') return textOf(node);
        if (isText) return page.surface('Text', k);
        if (Object.prototype.hasOwnProperty.call(methods, k)) return methods[k];
        switch (k) {
          case 'className': return node.attr('class') || '';
          case 'style': return styleProxy;
          case 'value': return page.valueOf(node);
          case 'checked': return node.props.checked !== undefined ? node.props.checked : node.attrs.has('checked');
          case 'disabled': return node.props.disabled !== undefined ? node.props.disabled : node.attrs.has('disabled');
          case 'scrollTop': return node.scrollTop;
          case 'offsetTop': return 0;
          case 'firstChild': return page.wrap(node.children[0] || null);
          default: return page.surface('Element', k);
        }
      },
      set: (t, k, v) => {
        if (k === 'textContent') {
          for (const c of node.children) c.parent = null;
          node.children = [];
          if (isText) node.text = String(v);
          else if (String(v) !== '') page.insert(node, new Node('#text', String(v)), null);
          return true;
        }
        if (isText) return page.surface('Text', k);
        switch (k) {
          case 'className': node.attrs.set('class', String(v)); return true;
          case 'value': node.props.value = String(v); return true;
          case 'checked': node.props.checked = !!v; return true;
          case 'disabled': node.props.disabled = !!v; return true;
          case 'scrollTop': node.scrollTop = Number(v) || 0; return true;
          case 'innerHTML':
            if (v !== '') throw new Error('innerHTML may only be cleared (design §2.3)');
            for (const c of node.children) c.parent = null;
            node.children = [];
            return true;
          default: return page.surface('Element', k);
        }
      },
    });
    this.proxies.set(node, p);
    this.nodes.set(p, node);
    return p;
  }

  insert(parent, child, ref) {
    if (child.parent) {
      const old = child.parent.children;
      old.splice(old.indexOf(child), 1);
    }
    const idx = ref ? parent.children.indexOf(ref) : -1;
    if (ref && idx < 0) throw new Error('insertBefore: reference is not a child');
    if (idx < 0) parent.children.push(child);
    else parent.children.splice(idx, 0, child);
    child.parent = parent;
  }

  valueOf(node) {
    if (node.props.value !== undefined && node.tag !== 'select') return node.props.value;
    if (node.tag === 'textarea') return textOf(node);
    if (node.tag === 'option') return node.attr('value') !== null ? node.attr('value') : textOf(node);
    if (node.tag === 'select') {
      const opts = [...walk(node)].filter((n) => n.tag === 'option');
      const values = opts.map((o) => this.valueOf(o));
      if (node.props.value !== undefined && values.indexOf(node.props.value) >= 0) return node.props.value;
      const sel = opts.find((o) => o.attrs.has('selected'));
      return sel ? this.valueOf(sel) : (values[0] || '');
    }
    return node.attr('value') || '';
  }

  byId(id) {
    for (const n of walk(this.root)) if (n.attr && n.attr('id') === id) return n;
    return null;
  }

  // ---- the runtime the script sees

  context() {
    const page = this;
    const doc = {
      getElementById: (id) => page.wrap(page.byId(String(id))),
      createElement: (tag) => page.wrap(new Node(String(tag).toLowerCase())),
      createTextNode: (text) => page.wrap(new Node('#text', String(text))),
      addEventListener: (type, fn) => {
        if (type !== 'visibilitychange') page.surface('document.addEventListener', type);
        page.listeners.visibilitychange.push(fn);
      },
      execCommand: (cmd) => {
        if (cmd !== 'copy') page.surface('document.execCommand', cmd);
        if (page.options.clipboard !== 'command' || !page.selected) return false;
        page.clipboard.push(page.valueOf(page.selected));
        return true;
      },
    };
    let hash = this.hash;
    const location = {
      get hash() { return hash; },
      set hash(v) { hash = v ? '#' + String(v).replace(/^#/, '') : ''; },
      get pathname() { return page.pathname; },
    };
    const history = {
      replaceState: (state, title, url) => {
        page.replaces++;
        page.entries[page.index] = { state: state === undefined ? null : state };
        if (url !== undefined) hash = String(url).indexOf('#') >= 0 ? String(url).slice(String(url).indexOf('#')) : '';
      },
      pushState: (state) => {
        if (!state || state.mcvs !== 1) throw new Error('pushState without {mcvs: 1}');
        page.pushes++;
        page.entries.splice(page.index + 1);
        page.entries.push({ state });
        page.index = page.entries.length - 1;
      },
      // Asynchronous, as in a browser: the popstate comes after the current task.
      back: () => {
        page.backs++;
        page.pending++;
        setTimeout(() => { page.pending--; page.traverse(-1); }, 0);
      },
    };
    // options.clipboard: 'api' (navigator.clipboard works), 'command' (it refuses; execCommand('copy') works),
    // 'none' (no navigator.clipboard, execCommand fails: the page must fall back to POST clipboard).
    const navigator = { userAgent: UA, clipboard: undefined };
    if (this.options.clipboard !== 'none') {
      navigator.clipboard = {
        writeText: (text) => {
          if (page.options.clipboard === 'command') return Promise.reject(new Error('NotAllowedError'));
          page.clipboard.push(String(text));
          return Promise.resolve();
        },
      };
    }
    const timer = (kind) => (fn, ms) => {
      const wrapped = page.guard(kind, fn);
      const t = kind === 'setInterval' ? setInterval(wrapped, ms) : setTimeout(() => { page.timers.delete(t); wrapped(); }, ms);
      page.timers.add(t);
      return t;
    };
    const clear = (t) => { page.timers.delete(t); clearTimeout(t); clearInterval(t); };
    const strict = (name, obj) => new Proxy(obj, {
      get: (t, k) => {
        if (typeof k === 'symbol') return undefined;
        if (!(k in t)) return page.surface(name, k);
        return t[k];
      },
      set: (t, k, v) => {
        if (name === 'location' && k === 'hash') { t.hash = v; return true; }
        return page.surface(name, k);
      },
    });
    const sandbox = {
      document: strict('document', Object.assign(doc, { get hidden() { return false; } })),
      window: strict('window', {
        addEventListener: (type, fn) => {
          if (type !== 'popstate') page.surface('window.addEventListener', type);
          page.listeners.popstate.push(fn);
        },
      }),
      location: strict('location', location),
      history: strict('history', history),
      navigator: strict('navigator', navigator),
      XMLHttpRequest: this.xhrClass(),
      setTimeout: timer('setTimeout'),
      setInterval: timer('setInterval'),
      clearTimeout: clear,
      clearInterval: clear,
    };
    return vm.createContext(sandbox);
  }

  xhrClass() {
    const page = this;
    const READABLE = new Set(['readyState', 'status', 'responseText', 'timeout', 'onreadystatechange', 'ontimeout', 'onload', 'onerror']);
    const WRITABLE = new Set(['timeout', 'onreadystatechange', 'ontimeout', 'onload', 'onerror']);
    return function XMLHttpRequest() {
      const s = { readyState: 0, status: 0, responseText: '', timeout: 0, onreadystatechange: null, ontimeout: null, onload: null, onerror: null };
      let method = 'GET';
      let url = '';
      const headers = {};
      let resHeaders = {};
      let done = false;
      const finish = (status, text, hdrs) => {
        if (done) return;
        done = true;
        page.pending--;
        s.readyState = 4;
        s.status = status;
        s.responseText = text;
        resHeaders = hdrs || {};
        if (page.closed) return;
        if (s.onreadystatechange) page.guard('onreadystatechange ' + method + ' ' + url, s.onreadystatechange)();
        if (status === 0 && s.ontimeout) page.guard('ontimeout', s.ontimeout)();
        if (status !== 0 && s.onload) page.guard('onload', s.onload)();
      };
      const api = {
        open: (m, u, async) => {
          if (async === false) throw new Error('synchronous XHR');
          method = String(m).toUpperCase();
          url = String(u);
          s.readyState = 1;
        },
        setRequestHeader: (n, v) => { headers[String(n)] = String(v); },
        getResponseHeader: (n) => {
          const v = resHeaders[String(n).toLowerCase()];
          return v === undefined ? null : String(v);
        },
        abort: () => finish(0, '', {}),
        send: (body) => {
          page.pending++;
          if (!/^\/_mcvs\/api\/v1\//.test(url)) fail(page.label + ': request to ' + url + ' (only relative /_mcvs/api/v1/ URLs, §2.3)');
          const entry = { method, url, status: null };
          page.requests.push(entry);
          const target = new URL(url, page.origin);
          const h = Object.assign({}, headers, {
            Host: target.host,
            'User-Agent': UA,
            Accept: '*/*',
            'Sec-Fetch-Site': 'same-origin',
          });
          if (method !== 'GET' && method !== 'HEAD') h.Origin = page.origin;
          const cookie = page.jar.header();
          if (cookie) h.Cookie = cookie;
          const data = body === null || body === undefined ? null : Buffer.from(String(body), 'utf8');
          if (data) h['Content-Length'] = data.length;
          const req = http.request({ host: target.hostname, port: target.port, path: target.pathname + target.search, method, headers: h }, (res) => {
            const chunks = [];
            res.on('data', (c) => chunks.push(c));
            res.on('end', () => {
              page.jar.update(res.headers['set-cookie']);
              entry.status = res.statusCode;
              finish(res.statusCode, Buffer.concat(chunks).toString('utf8'), res.headers);
            });
          });
          req.on('error', () => { entry.status = 0; finish(0, '', {}); });
          if (s.timeout > 0) req.setTimeout(s.timeout, () => { req.destroy(); });
          if (data) req.write(data);
          req.end();
        },
      };
      return new Proxy(s, {
        get: (t, k) => {
          if (typeof k === 'symbol') return undefined;
          if (Object.prototype.hasOwnProperty.call(api, k)) return api[k];
          if (READABLE.has(k)) return t[k];
          return page.surface('XMLHttpRequest', k);
        },
        set: (t, k, v) => {
          if (!WRITABLE.has(k)) return page.surface('XMLHttpRequest', k);
          t[k] = v;
          return true;
        },
      });
    };
  }

  // ---- loading and driving the page

  async load(url) {
    const u = new URL(url);
    this.hash = u.hash;
    this.pathname = u.pathname;
    const res = await httpGet(this.origin + u.pathname, this.jar);
    check(res.status === 200, this.label + ': GET ' + u.pathname + ' answered ' + res.status);
    check(/text\/html/.test(res.headers['content-type'] || ''), this.label + ': page content type ' + res.headers['content-type']);
    check(/default-src 'none'/.test(res.headers['content-security-policy'] || ''), this.label + ': page served without its Content-Security-Policy');
    const parsed = parseHtml(res.body);
    this.root = parsed.root;
    this.html = res.body;
    check(parsed.scripts.length === 1, this.label + ': ' + parsed.scripts.length + ' scripts');
    const ctx = this.context();
    try {
      vm.runInContext(parsed.scripts[0], ctx, { filename: 'index.html <script>' });
    } catch (e) {
      this.error('top level', e);
    }
    await this.settle();
    return this;
  }

  async settle(maxMs) {
    const deadline = Date.now() + (maxMs || 15000);
    let quiet = 0;
    while (Date.now() < deadline) {
      await sleep(20);
      if (this.pending === 0) {
        if (++quiet >= 3) return;
      } else {
        quiet = 0;
      }
    }
    fail(this.label + ': requests still pending after ' + (maxMs || 15000) + ' ms');
  }

  close() {
    this.closed = true;
    for (const t of this.timers) { clearTimeout(t); clearInterval(t); }
    this.timers.clear();
  }

  el(id) { return this.byId(id); }

  isShown(id) {
    let n = typeof id === 'string' ? this.byId(id) : id;
    if (!n) return false;
    for (; n && n.tag !== '#document'; n = n.parent) if (n.style.display === 'none') return false;
    return !!n;
  }

  text(id) {
    const n = this.byId(id);
    return n ? textOf(n) : '';
  }

  disabled(n) { return n.props.disabled !== undefined ? n.props.disabled : n.attrs.has('disabled'); }

  fire(node, type) {
    const event = new Proxy({}, { get: (t, k) => (typeof k === 'symbol' ? undefined : this.surface('Event', k)) });
    for (const fn of (node.listeners[type] || []).slice()) this.guard(type + ' on #' + (node.attr('id') || node.tag), fn)(event);
  }

  // A player's tap: only on a visible, enabled element. Checkboxes toggle first (as the browser does).
  async tap(idOrNode, why) {
    const n = typeof idOrNode === 'string' ? this.byId(idOrNode) : idOrNode;
    const name = typeof idOrNode === 'string' ? '#' + idOrNode : (n && n.attr('id') ? '#' + n.attr('id') : '<' + (n ? n.tag : '?') + '>');
    if (!check(n, this.label + ': ' + name + ' missing' + (why ? ' (' + why + ')' : ''))) return false;
    if (!check(this.isShown(n), this.label + ': ' + name + ' tapped while hidden' + (why ? ' (' + why + ')' : ''))) return false;
    if (!check(!this.disabled(n), this.label + ': ' + name + ' tapped while disabled' + (why ? ' (' + why + ')' : ''))) return false;
    if (n.tag === 'a') {
      if (n.attr('href') === 'exit:') this.exited = true;
      return true;
    }
    if (n.tag === 'input' && (n.attr('type') === 'checkbox')) {
      n.props.checked = !(n.props.checked !== undefined ? n.props.checked : n.attrs.has('checked'));
      this.fire(n, 'click');
      this.fire(n, 'change');
    } else {
      this.fire(n, 'click');
    }
    await this.settle();
    return true;
  }

  async choose(id, value) {
    const n = this.byId(id);
    if (!check(n && n.tag === 'select', this.label + ': select #' + id + ' missing')) return;
    const values = [...walk(n)].filter((o) => o.tag === 'option').map((o) => this.valueOf(o));
    if (!check(values.indexOf(value) >= 0, this.label + ': #' + id + ' has no option ' + value)) return;
    n.props.value = value;
    this.fire(n, 'change');
    await this.settle();
  }

  type(id, text) {
    const n = this.byId(id);
    if (!check(n, this.label + ': field #' + id + ' missing')) return;
    n.props.value = text;
    this.fire(n, 'input');
  }

  // One step through the session history with a popstate, as WebView.goBack/goForward do; false when there is none.
  traverse(delta) {
    const to = this.index + delta;
    if (this.closed || to < 0 || to >= this.entries.length) return false;
    this.index = to;
    const state = this.entries[to].state;
    for (const fn of this.listeners.popstate.slice()) this.guard('popstate', fn)({ state });
    return true;
  }

  // Whether hardware Back would close the browser now: InGameBrowser.onBackPressed is
  // `if (webView.canGoBack()) webView.goBack(); else close`, and the page is the first entry of its WebView.
  backCloses() { return this.index === 0; }

  // Hardware Back. Returns false (and changes nothing) when it would close the browser instead.
  async back() {
    if (this.backCloses()) return false;
    this.traverse(-1);
    await this.settle();
    return true;
  }

  async forward() {
    const moved = this.traverse(1);
    await this.settle();
    return moved;
  }

  // The first descendant of #id that reacts to a tap (list rows have no ids).
  firstTappable(id) {
    const n = this.byId(id);
    if (!n) return null;
    for (const c of walk(n)) if (c !== n && c.listeners.click && this.isShown(c)) return c;
    return null;
  }

  button(containerId, label) {
    const n = this.byId(containerId);
    if (!n) return null;
    for (const c of walk(n)) if (c.tag === 'button' && textOf(c).trim() === label) return c;
    return null;
  }

  pageErrorsShown() { return this.isShown('page-errors') ? this.text('page-errors') : null; }
}

class CookieJar {
  constructor() { this.cookies = {}; }

  header() {
    const parts = Object.keys(this.cookies).map((k) => k + '=' + this.cookies[k]);
    return parts.length ? parts.join('; ') : null;
  }

  update(setCookies) {
    for (const sc of setCookies || []) {
      const first = sc.split(';')[0];
      const i = first.indexOf('=');
      const name = first.slice(0, i).trim();
      const value = first.slice(i + 1).trim();
      if (/;\s*max-age=0/i.test(sc) || value === '') delete this.cookies[name];
      else this.cookies[name] = value;
    }
  }
}

function httpGet(url, jar) {
  return new Promise((resolve, reject) => {
    const u = new URL(url);
    const headers = { Host: u.host, 'User-Agent': UA, Accept: 'text/html' };
    const cookie = jar && jar.header();
    if (cookie) headers.Cookie = cookie;
    http.get({ host: u.hostname, port: u.port, path: u.pathname + u.search, headers }, (res) => {
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => {
        if (jar) jar.update(res.headers['set-cookie']);
        resolve({ status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks).toString('utf8') });
      });
    }).on('error', reject);
  });
}

// ------------------------------------------------------------------ the scenario (§10.5)

async function screenCheck(p, screen, ok, what) {
  await p.tap('nav-' + screen);
  check(p.isShown('screen-' + screen), p.label + ': #screen-' + screen + ' not shown after #nav-' + screen);
  for (const other of ['status', 'settings', 'saves', 'logs', 'actions', 'about']) {
    if (other !== screen) check(!p.isShown('screen-' + other), p.label + ': #screen-' + other + ' still shown on ' + screen);
  }
  check(ok(), p.label + ': screen ' + screen + ' did not render ' + what);
  log('  screen ' + screen + ' rendered');
}

async function fullRun(url, jar, mode, fx) {
  const origin = new URL(url).origin;
  const p = await new Page('run 1 (ticket)', origin, jar, { clipboard: 'api' }).load(url);
  log('run 1: boot with a one-time ticket');
  check(p.replaces >= 1, 'run 1: the fragment was not removed with history.replaceState');
  check(!p.isShown('banner-nosession'), 'run 1: #banner-nosession shown');
  check(!p.isShown('banner-viewmode'), 'run 1: #banner-viewmode shown');
  check(!p.isShown('banner-error'), 'run 1: #banner-error shown');
  check(jar.cookies.mcvs_s, 'run 1: no mcvs_s cookie after the session exchange');
  check(p.requests.some((r) => r.method === 'POST' && r.url === '/_mcvs/api/v1/session' && r.status === 200), 'run 1: POST session did not succeed');
  check(p.isShown('screen-status') && /Server/.test(p.text('status-body')), 'run 1: the status screen did not render');

  // Every screen.
  await screenCheck(p, 'settings', () => !!p.el('set-instantBots') && !!p.el('grp-gameplay'), '#set-instantBots / #grp-gameplay');
  await screenCheck(p, 'saves', () => p.text('saves-current').length > 10, '#saves-current');
  await screenCheck(p, 'logs', () => p.el('logs-files').children.length > 0, '#logs-files');
  await screenCheck(p, 'actions', () => !!p.el('action-files'), '#action-files');
  await screenCheck(p, 'about', () => p.text('about-ua') === UA && p.text('about-version') !== '–', '#about-ua / #about-version');
  await screenCheck(p, 'status', () => /Player/.test(p.text('status-body')), '#status-body');
  check((mode === 'fixtures' ? /Cosmetic crate\s*guard \(live ops are off\)/ : /Cosmetic crate\s*(dormant|guard|active)/).test(p.text('status-body')),
    'run 1: Status does not show the Cosmetic crate phase');
  await p.tap('status-storage');
  check(/Total/.test(p.text('status-storage-body')), 'run 1: storage usage not shown');

  // Settings: every group tab, the presets, then a change, the Back guard, Save.
  await p.tap('nav-settings');
  const tabs = p.el('settings-tabs').children.map((c) => c.attr('id'));
  for (const g of ['grp-gameplay', 'grp-economy', 'grp-unlocks', 'grp-liveops', 'grp-diagnostics', 'grp-advanced']) {
    check(tabs.indexOf(g) >= 0, 'run 1: group tab #' + g + ' missing (have ' + tabs.join(', ') + ')');
  }
  for (const id of tabs) {
    await p.tap(id);
    check(p.el('settings-body').children.length > 0, 'run 1: group ' + id + ' rendered nothing');
  }
  if (await p.tap('grp-economy')) {
    for (const preset of ['preset-standard', 'preset-relaxed', 'preset-fast']) check(p.el(preset), 'run 1: ' + preset + ' missing');
    // The presets own the five multipliers only: a pending crate edit survives one (and a multiplier Relaxed leaves out
    // goes back to its default).
    const items = p.el('set-economy-collector-itemsPerCrate');
    const plus = items && items.parent.children[items.parent.children.indexOf(items) + 1];
    if (check(plus, 'run 1: no + next to #set-economy-collector-itemsPerCrate')) await p.tap(plus, 'itemsPerCrate +1');
    const credits = p.el('set-economy-creditsMultiplier');
    const creditsPlus = credits && credits.parent.children[credits.parent.children.indexOf(credits) + 1];
    if (check(creditsPlus, 'run 1: no + next to #set-economy-creditsMultiplier')) await p.tap(creditsPlus, 'creditsMultiplier +0.1');
    await p.tap('preset-relaxed');
    check(p.text('set-economy-versusCoinsMultiplier') === '2.0', 'run 1: preset Relaxed did not fill VersusCoins (' + p.text('set-economy-versusCoinsMultiplier') + ')');
    check(p.text('set-economy-creditsMultiplier') === '1.0', 'run 1: preset Relaxed left Credits at ' + p.text('set-economy-creditsMultiplier') + ', not its default');
    check(p.text('set-economy-collector-itemsPerCrate') === '6', 'run 1: preset Relaxed changed the items per crate (' + p.text('set-economy-collector-itemsPerCrate') + ', not the edited 6)');
    const crate = p.el('set-economy-collector-enabled');
    check(crate && crate.props.checked !== false, 'run 1: preset Relaxed switched the Cosmetic crate off');
    check(!p.backCloses(), 'run 1: a pending preset edit left no history entry for Back');
    await p.tap('settings-discard');
    check(p.disabled(p.el('settings-save')), 'run 1: Save enabled after Discard');
    // Nothing left to lose: the page takes its entry off again, so hardware Back closes the browser (§2.1, C18).
    check(p.backCloses(), 'run 1: after Discard hardware Back would not close the browser (' + p.index + ' entries above the page)');
  }
  const pushesBefore = p.pushes;
  await p.tap('grp-gameplay');
  const instant = p.el('set-instantBots');
  const before = instant && instant.props.checked !== undefined ? instant.props.checked : instant && instant.attrs.has('checked');
  await p.tap('set-instantBots');
  check(p.pushes === pushesBefore + 1, 'run 1: a pending setting edit pushed ' + (p.pushes - pushesBefore) + ' history entries, not 1');
  check(!p.disabled(p.el('settings-save')), 'run 1: Save disabled with a pending edit');
  check(await p.back(), 'run 1: Back with a pending edit would close the browser');
  check(p.isShown('dialog') && /Discard/.test(p.text('dialog-title')), 'run 1: Back with a pending edit did not ask "Discard your changes?"');
  check(p.backCloses(), 'run 1: while "Discard your changes?" is open a second Back does not leave the page');
  await p.tap('dialog-cancel', 'Stay');
  check(!p.isShown('dialog'), 'run 1: the dialog stayed open after Stay');
  check(p.pushes === pushesBefore + 2, 'run 1: Stay did not push the history entry again');
  check(!p.backCloses(), 'run 1: after Stay Back would close the browser');
  await p.tap('settings-save');
  if (p.isShown('dialog')) await p.tap('dialog-ok', 'confirm the save');
  check(/No changes/.test(p.text('settings-dirty')), 'run 1: settings still dirty after Save (' + p.text('settings-dirty') + ')');
  check(/Saved/.test(p.text('toast-text')), 'run 1: no "Saved" toast (' + p.text('toast-text') + ')');
  const after = p.el('set-instantBots');
  check(after && after.props.checked === !before, 'run 1: #set-instantBots does not show the saved value');
  // C18: edit, Back, Stay, Save, Back closes the browser (the entry is gone after the save).
  check(p.backCloses(), 'run 1: after Save hardware Back would not close the browser (' + p.index + ' entries above the page)');

  // A dialog (the confirmation of a risky setting): Back closes it and keeps the edit; Discard then leaves no entry.
  await p.tap('set-devMenu');
  await p.tap('settings-save');
  if (check(p.isShown('dialog'), 'run 1: enabling the cheat menu did not ask first')) {
    check(await p.back(), 'run 1: Back with a dialog open would close the browser');
    check(!p.isShown('dialog'), 'run 1: Back did not close the dialog');
    check(!/No changes/.test(p.text('settings-dirty')), 'run 1: Back on the dialog dropped the pending edit');
    check(!p.backCloses(), 'run 1: the pending edit has no history entry after the dialog closed');
  }
  await p.tap('settings-discard');
  check(p.backCloses(), 'run 1: after the dialog and Discard Back would not close the browser');
  // The browser's Forward button onto the old entry: the page takes it off again.
  if (await p.forward()) check(p.backCloses(), 'run 1: Forward onto the page\'s old entry left it there');
  log('  settings: instantBots ' + before + ' -> ' + !before + ', Back guard and Save ok');

  // Logs: open a file, filter, then build the zip.
  await p.tap('nav-logs');
  const fileRow = p.firstTappable('logs-files');
  if (check(fileRow, 'run 1: no log file to open')) {
    await p.tap(fileRow);
    check(p.isShown('logs-view') && p.el('logs-view').children.length > 0, 'run 1: the log viewer is empty');
    await p.choose('logs-level', 'warn');
    await p.tap('logs-copy');
    await p.tap('logs-back');
  }
  await p.tap('logs-tab-share');
  await p.tap('logs-build');
  check(/\.zip/.test(p.text('logs-built')), 'run 1: the zip was not built (' + p.text('logs-built') + ')');
  await p.tap('logs-tab-view');
  log('  logs: viewer, filter, copy and zip ok');

  // Saves: export as text in Discord-sized parts, then paste them back (two pastes).
  await p.tap('nav-saves');
  await p.tap('saves-open-export');
  await p.choose('export-partsize', '1900');
  await p.tap('export-copy');
  const parts = [];
  for (let k = 0; k < 400 && p.isShown('export-copy-next') && !/again/.test(p.text('export-copy-next')); k++) {
    const n = p.clipboard.length;
    await p.tap('export-copy-next');
    if (!check(p.clipboard.length === n + 1, 'run 1: "Copy part" copied nothing')) break;
    parts.push(p.clipboard[p.clipboard.length - 1]);
  }
  check(parts.length >= 2, 'run 1: the export gave ' + parts.length + ' parts; the smoke needs at least 2');
  check(parts.every((t) => /^MCVS-SAVE:1:[0-9a-f]{8}:\d+\/\d+:[ds]:/.test(t)), 'run 1: a copied part is not transfer text');
  log('  export: ' + parts.length + ' parts copied');
  await p.tap('export-back');
  await p.tap('saves-open-import');
  if (parts.length) {
    p.type('import-text', 'chat noise before\n' + parts[0] + '\nand after');
    await p.tap('import-add');
    if (parts.length > 1) {
      check(!p.el('import-summary').children.length, 'run 1: a summary after the first of ' + parts.length + ' parts');
      check(/1/.test(p.text('import-parts')), 'run 1: #import-parts does not report part 1 (' + p.text('import-parts') + ')');
      p.type('import-text', parts.slice(1).reverse().join('\r\n\r\n'));
      await p.tap('import-add');
    }
    check(p.el('import-summary').children.length > 0, 'run 1: no import summary after all parts (' + p.text('import-parts') + ')');
    if (check(p.isShown('import-confirm-row'), 'run 1: the import cannot be staged: ' + p.text('import-summary'))) {
      check(p.disabled(p.el('import-stage')), 'run 1: Stage enabled before the confirmation');
      await p.tap('import-confirm');
      check(!p.disabled(p.el('import-stage')), 'run 1: Stage disabled after the confirmation');
      await p.tap('import-stage');
      if (p.isShown('dialog')) await p.tap('dialog-cancel', 'not now');
      check(p.isShown('saves-main') && p.isShown('saves-staged') && p.isShown('staged-cancel'), 'run 1: the staged import is not shown on Saves');
      await p.tap('staged-cancel');
      await p.tap('dialog-ok', 'cancel the staged import');
      check(!p.isShown('staged-cancel'), 'run 1: the staged import is still shown after Cancel');
      log('  import: pasted back, staged and cancelled');
    }
  }
  if (mode === 'fixtures') await fixtureExtras(p, fx);

  await p.tap('nav-actions');
  await p.tap('action-files');
  check(p.isShown('action-files-body') && p.text('action-files-body').length > 10, 'run 1: "Where are my files" is empty');
  await p.tap('nav-about');

  check(!p.isShown('banner-error'), 'run 1: #banner-error shown at the end');
  check(!p.isShown('dialog') && p.backCloses(), 'run 1: with nothing open or unsaved, hardware Back would not close the browser at the end ('
        + p.index + ' entries above the page)');
  check(p.pageErrorsShown() === null, 'run 1: the page reported errors: ' + p.pageErrorsShown());
  p.close();

  // Activity re-creation: the same URL (used ticket) with the cookie: the session is kept.
  const p2 = await new Page('run 2 (re-created)', origin, jar).load(url);
  log('run 2: re-creation with the used ticket and the cookie');
  check(!p2.isShown('banner-nosession'), 'run 2: #banner-nosession shown after re-creation');
  check(!p2.disabled(p2.el('nav-settings')), 'run 2: navigation locked after re-creation');
  check(!p2.requests.some((r) => r.url === '/_mcvs/api/v1/session'), 'run 2: the used ticket was sent again although the cookie was valid');
  check(p2.pageErrorsShown() === null, 'run 2: page errors: ' + p2.pageErrorsShown());
  p2.close();

  // View mode: the inbox marker without the adminlink patch, no cookie.
  const p3 = await new Page('run 3 (view mode)', origin, new CookieJar()).load(origin + new URL(url).pathname + '#news');
  log('run 3: #news without a cookie');
  check(p3.isShown('banner-viewmode'), 'run 3: #banner-viewmode not shown');
  check(p3.isShown('screen-about'), 'run 3: the About screen is not shown');
  check(p3.disabled(p3.el('nav-settings')), 'run 3: Settings is not disabled in view mode');
  check(p3.requests.some((r) => r.url === '/_mcvs/api/v1/ping' && r.status === 200), 'run 3: no successful ping in view mode');
  check(p3.pageErrorsShown() === null, 'run 3: page errors: ' + p3.pageErrorsShown());
  p3.close();

  return [p, p2, p3];
}

// Paths the live standalone server cannot take (it has no game host): share, restart, save now, copy fallbacks.
async function fixtureExtras(p, fx) {
  await p.tap('saves-save-now');
  check(/opens again/.test(p.text('toast-text')), 'fixtures: save now did not answer (' + p.text('toast-text') + ')');
  await p.tap('saves-backup-now');
  check(/Backup made/.test(p.text('toast-text')), 'fixtures: back up now failed (' + p.text('toast-text') + ')');
  const del = p.el('delete-20261026T101500123Z-r512-manual');
  if (check(del, 'fixtures: no Delete on the manual backup')) {
    await p.tap(del);
    await p.tap('dialog-ok', 'delete the backup');
  }
  const restore = p.el('restore-20261026T090000000Z-r500-auto');
  if (check(restore, 'fixtures: no Restore on the automatic backup')) {
    await p.tap(restore);
    await p.tap('dialog-ok', 'restore');
    check(p.isShown('dialog') && /Restart/.test(p.text('dialog-title')), 'fixtures: staging a restore did not offer the restart');
    await p.tap('dialog-cancel', 'restart later');
  }
  const relink = p.el('relink-fedcba9876543210fedcba98');
  if (check(relink, 'fixtures: no "Play this account on this phone"')) {
    await p.tap(relink);
    await p.tap('dialog-ok', 'relink');
    check(p.isShown('dialog') && /Another operation/.test(p.text('dialog-title')), 'fixtures: staged_exists did not ask to replace');
    await p.tap('dialog-ok', 'replace');
    if (p.isShown('dialog')) await p.tap('dialog-cancel', 'no restart');
  }
  await p.tap('saves-open-export');
  await p.tap('export-share');
  check(/within a minute/.test(p.text('export-result')), 'fixtures: export share result missing (' + p.text('export-result') + ')');
  // 413 too_large: more than 999 parts of 1,900 characters. The page offers larger parts and a file.
  fx.state.exportTooLarge = 'parts';
  await p.choose('export-partsize', '1900');
  await p.tap('export-copy');
  check(/999 parts/.test(p.text('export-result')) && p.isShown('export-more'), 'fixtures: export 413 (parts) not explained (' + p.text('export-result') + ')');
  check(p.button('export-more', 'Use Telegram parts (3,900)') && p.button('export-more', 'Share as a file'), 'fixtures: export 413 (parts) offers ' + p.text('export-more'));
  await p.tap(p.button('export-more', 'Use whole parts (60,000)'));
  check(p.isShown('export-text') && !p.isShown('export-more') && p.byId('export-partsize').props.value === '60000', 'fixtures: larger parts after a 413 did not export');
  check(fx.state.exports[fx.state.exports.length - 1].partChars === 60000, 'fixtures: the retry did not ask for 60,000-character parts');
  // 413 too_large in bytes: only the .json file can carry it.
  fx.state.exportTooLarge = 'bytes';
  await p.tap('export-copy');
  const json = p.button('export-more', 'Share as a .json file');
  if (check(json, 'fixtures: export 413 (bytes) offers ' + p.text('export-more'))) {
    await p.tap(json);
    const last = fx.state.exports[fx.state.exports.length - 1];
    check(last.format === 'json' && last.target === 'share', 'fixtures: the .json share asked for ' + JSON.stringify(last));
    check(/within a minute/.test(p.text('export-result')) && !p.isShown('export-more'), 'fixtures: the .json share result is missing (' + p.text('export-result') + ')');
  }
  fx.state.exportTooLarge = null;
  await p.tap('export-back');
  await p.tap('saves-open-import');
  const fileCheck = p.button('import-files', 'Check');
  if (check(fileCheck, 'fixtures: no import file listed')) {
    await p.tap(fileCheck);
    check(/switch|play this save/i.test(p.text('import-summary')), 'fixtures: the import file summary has no plan');
    await p.tap('import-reset');
  }
  await p.tap('import-back');

  // Copy through the hidden textarea, then through the game, while the clipboard API is missing.
  p.options.clipboard = 'command';
  await p.tap('nav-settings');
  await p.tap('grp-unlocks');
  const copy = p.button('settings-body', 'Copy');
  if (check(copy, 'fixtures: no Copy button for the unlock codes')) {
    const n = p.clipboard.length;
    await p.tap(copy);
    check(p.clipboard.length === n + 1 && p.clipboard[n] === 'UNLOCKALL', 'fixtures: the copy command fallback did not copy the code');
    check(/copy command/.test(p.text('toast-text')), 'fixtures: wrong copy toast (' + p.text('toast-text') + ')');
  }
  p.options.clipboard = 'api';

  // Logs share, the text report and the CRM/unlock actions.
  await p.tap('nav-logs');
  await p.tap('logs-tab-share');
  await p.tap('logs-build');
  await p.tap('logs-share');
  check(/within a minute/.test(p.text('logs-built')), 'fixtures: logs share result missing (' + p.text('logs-built') + ')');
  await p.tap('logs-share-text');
  check(/Choose an app\. Come back to the game within a minute/.test(p.text('toast-text')), 'fixtures: text report toast (' + p.text('toast-text') + ')');
  fx.state.shareTextResult = { ok: false, method: 'text', publicPath: null, message: 'The share sheet is slow to open; it may still appear.' };
  await p.tap('logs-share-text');
  check(/may still appear\. If it opens, come back .*; if not, try again/.test(p.text('toast-text')), 'fixtures: slow text report toast (' + p.text('toast-text') + ')');
  fx.state.shareTextResult = { ok: false, method: 'none', publicPath: null, message: 'Sharing failed: no activity.' };
  await p.tap('logs-share-text');
  check(p.text('toast-text') === 'Sharing failed: no activity.', 'fixtures: failed text report toast (' + p.text('toast-text') + ')');
  fx.state.shareTextResult = null;
  await p.tap('logs-tab-view');
  // game.log: the game's clock (no date) is shown as it is.
  const gameLog = [...walk(p.el('logs-files'))].find((n) => n.listeners.click && /game\.log/.test(textOf(n)) && p.isShown(n));
  if (check(gameLog, 'fixtures: game.log is not listed')) {
    await p.tap(gameLog);
    check(/10:42:39\.123 ERROR/.test(p.text('logs-view')), 'fixtures: game.log times not shown (' + p.text('logs-view').slice(0, 120) + ')');
    await p.tap('logs-back');
  }
  await p.tap('nav-actions');
  await p.tap('action-crm-reload');
  check(/takes effect/.test(p.text('toast-text')), 'fixtures: CRM rebuild not confirmed');
  await p.tap('action-restart');
  check(p.isShown('dialog') && /Restart/.test(p.text('dialog-title')), 'fixtures: Restart did not ask first');
  const box = [...walk(p.byId('dialog-body'))].find((n) => n.tag === 'input');
  if (check(box, 'fixtures: no "restart even if the game does not save" checkbox')) await p.tap(box);
  await p.tap('dialog-ok', 'restart');
  check(/Restarting/.test(p.text('toast-text')), 'fixtures: restart not accepted (' + p.text('toast-text') + ')');
  check(fx.state.restarts.length === 1 && fx.state.restarts[0].restartWithoutSave === true && fx.state.restarts[0].reason === 'user',
        'fixtures: the restart request was ' + JSON.stringify(fx.state.restarts));
  await p.tap('action-restart');
  check(!p.isShown('dialog') && /already restarting/.test(p.text('toast-text')), 'fixtures: a second restart was offered while restarting');
  log('  fixtures: save now, backups, restore, relink + replace, share, copy fallbacks, actions and restart ok');
}

// The broken settings.json panel (fixtures only).
async function repairRun(fx) {
  fx.state.fallback = true;
  const jar = new CookieJar();
  const ticket = fx.mintTicket();
  const p = await new Page('run 4 (broken settings.json)', fx.origin, jar, { clipboard: 'none' }).load(fx.origin + '/_mcvs/ui/#t=' + ticket + '&s=settings');
  log('run 4: broken settings.json');
  check(p.isShown('screen-settings'), 'run 4: #s=settings did not open Settings');
  check(p.isShown('settings-repairpanel'), 'run 4: the repair panel is not shown');
  check(!p.isShown('bar-settings'), 'run 4: Save shown with a broken settings.json');
  await p.tap('settings-showfile');
  check(p.isShown('settings-filetext') && /\*\*\*/.test(p.text('settings-filetext')), 'run 4: the file text is not shown');
  await p.tap('settings-repair');
  await p.tap('dialog-ok', 'move aside');
  check(!p.isShown('settings-repairpanel') && p.isShown('settings-normal'), 'run 4: still broken after the repair');
  check(/Moved to/.test(p.text('toast-text')), 'run 4: no confirmation of the repair');
  check(p.isShown('banner-restart'), 'run 4: no restart banner after the repair');
  // Copying with no clipboard API and no copy command goes through the game (POST clipboard).
  await p.tap('grp-unlocks');
  const copy = p.button('settings-body', 'Copy');
  if (check(copy, 'run 4: no Copy button')) {
    await p.tap(copy);
    check(fx.state.clipboard.length === 1, 'run 4: the copy did not fall back to POST clipboard');
    check(/through the game/.test(p.text('toast-text')), 'run 4: wrong copy toast (' + p.text('toast-text') + ')');
    // The game could not copy (POST clipboard 501): the player copies it by hand.
    fx.state.clipboardFails = true;
    await p.tap(copy);
    check(/^Could not copy\. Select the text/.test(p.text('toast-text')), 'run 4: wrong toast when the game cannot copy (' + p.text('toast-text') + ')');
    fx.state.clipboardFails = false;
  }
  check(p.pageErrorsShown() === null, 'run 4: page errors: ' + p.pageErrorsShown());
  p.close();
  return p;
}

// A used ticket without a cookie: "open from the game", and "the game did not save" after save-now (fixtures).
async function edgeRuns(fx) {
  const ticket = fx.mintTicket();
  const jar = new CookieJar();
  const p = await new Page('run 5 (r=nosave)', fx.origin, jar).load(fx.origin + '/_mcvs/ui/#t=' + ticket + '&s=saves&r=nosave');
  check(p.isShown('screen-saves'), 'run 5: #s=saves did not open Saves');
  check(/did not save/.test(p.text('toast-text')), 'run 5: no "did not save" toast');
  p.close();
  const p2 = await new Page('run 6 (used ticket)', fx.origin, new CookieJar()).load(fx.origin + '/_mcvs/ui/#t=' + ticket);
  check(p2.isShown('banner-nosession'), 'run 6: a used ticket without a cookie did not show #banner-nosession');
  check(p2.disabled(p2.el('nav-saves')), 'run 6: navigation not locked without a session');
  p2.close();
  fx.state.down = true;
  const p3 = await new Page('run 7 (server gone)', fx.origin, jar).load(fx.origin + '/_mcvs/ui/');
  check(p3.isShown('banner-error'), 'run 7: #banner-error not shown when the API does not answer');
  p3.close();
  fx.state.down = false;
  log('runs 5-7: r=nosave, used ticket, dead server ok');

  // Post mode when the exchange failed (a used or expired ticket, or a WebView that dropped the 303's cookie): the
  // redirect always names the screen (§5.3), and the page asks to open it from the game instead of view mode.
  const p8 = await new Page('run 8 (post mode, no session)', fx.origin, new CookieJar()).load(fx.origin + '/_mcvs/ui/#s=status');
  check(p8.isShown('banner-nosession'), 'run 8: the failed post-mode exchange did not show #banner-nosession');
  check(!p8.isShown('banner-viewmode'), 'run 8: the failed post-mode exchange showed #banner-viewmode');
  p8.close();

  // The probation banner shows the server's sentences as written (§6.5): no repeated prefix, no "..".
  const reverted = 'Reverted automatically: the game did not load this save in 3 starts.';
  const notes = reverted + ' (the backup from before it is gone or unreadable, so the save was not restored)';
  const banners = [
    [{ result: 'reverted', message: reverted }, reverted],
    [{ result: 'reverted', message: notes }, notes],
    [{ result: 'reverted', message: null }, 'Reverted automatically: the game did not load the save in 3 starts.'],
    [{ result: 'failed', message: 'Gave up after 3 attempts.' }, 'The last operation failed: Gave up after 3 attempts.'],
    [{ result: 'failed', message: 'The staged save is damaged or missing.' }, 'The last operation failed: The staged save is damaged or missing.'],
    [{ result: 'failed', message: 'disk full' }, 'The last operation failed: disk full.'],
  ];
  const lastBefore = fx.state.last;
  for (let i = 0; i < banners.length; i++) {
    fx.state.last = Object.assign({ id: '2026102' + i + 'T080000000Z-import', kind: 'import', at: ISO(Date.now() - 3600000), undoable: false }, banners[i][0]);
    const pb = await new Page('run 9.' + (i + 1) + ' (' + banners[i][0].result + ' banner)', fx.origin, new CookieJar()).load(fx.origin + '/_mcvs/ui/#t=' + fx.mintTicket());
    check(pb.isShown('banner-probation') && pb.text('banner-probation-text') === banners[i][1],
          pb.label + ': #banner-probation reads "' + pb.text('banner-probation-text') + '", not "' + banners[i][1] + '"');
    check(pb.pageErrorsShown() === null, pb.label + ': page errors: ' + pb.pageErrorsShown());
    pb.close();
  }
  fx.state.last = lastBefore;
  log('runs 8-9: failed post-mode exchange, probation banners ok');
}

function acornCheck(html) {
  const path = process.env.MCVS_ACORN;
  if (!path) return;
  const acorn = require(path);
  const m = /<script>([\s\S]*?)<\/script>/.exec(html);
  try {
    acorn.parse(m[1], { ecmaVersion: 5 });
    log('acorn: the page script parses as ECMAScript 5');
  } catch (e) {
    fail('acorn (ecmaVersion 5): ' + e.message);
  }
}

async function main() {
  const args = process.argv.slice(2);
  process.on('unhandledRejection', (e) => fail('unhandled rejection: ' + (e && e.stack ? e.stack : e)));
  if (args[0] === '--fixtures' && args[1]) {
    const fx = await startFixtures(fs.readFileSync(args[1], 'utf8'));
    log('fixture API on ' + fx.origin);
    try {
      const jar = new CookieJar();
      const [p] = await fullRun(fx.origin + '/_mcvs/ui/#t=' + fx.mintTicket(), jar, 'fixtures', fx);
      acornCheck(p.html);
      await repairRun(fx);
      await edgeRuns(fx);
      for (const v of fx.state.violations) fail('contract: ' + v);
    } finally {
      fx.close();
    }
  } else if (args.length === 1 && /^http:\/\/127\.0\.0\.1:\d+\/_mcvs\/ui\/#t=[0-9a-f]{32}/.test(args[0])) {
    const [p] = await fullRun(args[0], new CookieJar(), 'live');
    acornCheck(p.html);
  } else {
    log('usage: node page-smoke.js <http://127.0.0.1:<port>/_mcvs/ui/#t=<ticket>> | --fixtures <index.html>');
    process.exit(2);
  }
  if (failures.length) {
    log('SMOKE FAILED: ' + failures.length + ' problem(s)');
    process.exit(1);
  }
  log('SMOKE OK');
  process.exit(0);
}

// ------------------------------------------------------------------ fixture API (shapes of design §4)

const ACCOUNT = '0123456789abcdef01234567';
const OTHER = 'fedcba9876543210fedcba98';
const ISO = (ms) => new Date(ms).toISOString();

function schema() {
  const k = (key, label, type, def, apply, extra) => Object.assign({ key, label, help: label + ' (help).', type, default: def, apply, editable: true, confirm: null, confirmWhen: null, backupBefore: null }, extra || {});
  const num = (lo, hi, step) => ({ min: lo, max: hi, step });
  const cfmEnable = (text) => ({ confirm: text, confirmWhen: 'enable', backupBefore: 'enable' });
  const season = { confirm: 'Changing this starts a new season now.', confirmWhen: 'any', backupBefore: 'any' };
  return [
    { id: 'gameplay', label: 'Gameplay', keys: [
      k('instantBots', 'Instant bot matches', 'bool', true, 'menu'),
      k('devMenu', 'Developer cheat menu', 'bool', false, 'restart', cfmEnable('Cheats can damage your profile. A backup of your save is made now.')),
      k('leaderboards.virtualPlayers', 'Leaderboard opponents', 'bool', true, 'live'),
      k('geo.country', 'Country', 'string', 'US', 'restart', { pattern: '^[A-Z]{2}$', maxLength: 2 }),
    ] },
    { id: 'economy', label: 'Economy', keys: [
      k('economy.versusCoinsMultiplier', 'VersusCoins per match', 'number', 1.0, 'menu', num(0.1, 20, 0.1)),
      k('economy.creditsMultiplier', 'Credits per match', 'number', 1.0, 'menu', num(0.1, 20, 0.1)),
      k('economy.xpMultiplier', 'XP speed', 'number', 1.0, 'menu', num(0.1, 20, 0.1)),
      k('economy.priceMultiplier', 'Shop prices', 'number', 1.0, 'menu', num(0.05, 10, 0.05)),
      k('crm.dailyCapMultiplier', 'Daily cap', 'number', 1.0, 'menu', num(0.1, 20, 0.1)),
      k('economy.collector.enabled', 'Cosmetic crate', 'bool', true, 'menu'),
      k('economy.collector.itemsPerCrate', 'Items per Cosmetic crate', 'int', 5, 'menu', num(1, 10, 1)),
    ] },
    { id: 'unlocks', label: 'Unlocks & codes', keys: [
      k('unlockAll', 'Unlock everything', 'bool', false, 'live', cfmEnable('This cannot be undone.')),
      k('assets.couponsEnabled', 'Unlock codes', 'bool', true, 'live'),
      k('crm.redeemCoupons', 'Redeem-code button', 'bool', true, 'menu'),
      k('unlocks.batchSize', 'Rewards per batch', 'int', 400, 'live', num(50, 400, 50)),
      k('admin.redeemCode', 'Code that opens this page', 'string', 'MCVSOFFLINE', 'live', { pattern: '^([A-Z0-9]{4,20})?$', maxLength: 20 }),
    ] },
    { id: 'liveops', label: 'Live ops', keys: [
      k('liveops.enabled', 'Live events', 'bool', true, 'menu'),
      k('liveops.seasonDays', 'Season length (days)', 'int', 7, 'menu', Object.assign(num(1, 28, 1), season)),
      k('liveops.seasonStart', 'Season anchor (UTC)', 'string', '2024-01-01T00:00:00Z', 'menu', Object.assign({ pattern: '^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}Z$', maxLength: 20 }, season)),
      k('liveops.dailyResetHour', 'Daily reset hour (UTC)', 'int', 0, 'menu', num(0, 23, 1)),
      k('liveops.modifiers', 'Weekly modifier', 'enum', 'rotate', 'menu', { options: ['rotate', 'off', 'standard', 'coinRush', 'trophyFever', 'creditRush', 'mvpBounty', 'rivalSurge'] }),
      k('liveops.difficulty', 'Event difficulty', 'number', 1.0, 'menu', num(0.1, 10, 0.1)),
    ] },
    { id: 'diagnostics', label: 'Diagnostics', keys: [
      k('storage.backups.keep', 'Automatic backups kept', 'int', 20, 'live', Object.assign(num(0, 200, 1), { confirm: 'Older automatic backups beyond the new number are deleted.', confirmWhen: 'decrease' })),
      k('storage.backups.intervalMinutes', 'Minutes between automatic backups', 'int', 30, 'live', num(5, 1440, 1)),
      k('logging.fileLevel', 'server.log detail', 'enum', 'debug', 'restart', { options: ['trace', 'debug', 'info', 'warn', 'error'] }),
      k('gameLog.levels', 'Game log levels', 'tokens', 'error warning', 'restart', { options: ['error', 'warning', 'info', 'debug'] }),
      k('gameLog.file', 'Copy the game log', 'bool', true, 'restart'),
    ] },
    { id: 'advanced', label: 'Advanced', keys: [
      { key: 'port', label: 'Port', type: 'int', editable: false, value: 51780 },
      { key: 'superPassword', label: 'Super password', type: 'string', editable: false, display: '•••• (default)' },
      { key: 'crm.overlay', label: 'CRM overlay', type: 'object', editable: false, display: 'not set' },
    ] },
  ];
}

function startFixtures(html) {
  const groups = schema();
  const keys = {};
  for (const g of groups) for (const k of g.keys) keys[k.key] = k;
  const state = {
    values: {}, boot: {}, fallback: false, down: false, tickets: new Set(), sessions: new Set(), violations: [],
    clipboard: [], restarts: [], staged: null, last: { id: '20261020T080000000Z-import', kind: 'import', result: 'applied', at: ISO(Date.now() - 86400000 * 6), message: 'Imported Player4711.', undoable: true },
    backups: {
      [ACCOUNT]: [
        { id: '20261026T101500123Z-r512-manual', date: ISO(Date.now() - 600000), revision: 512, reason: 'manual', pool: 'manual', size: 611000, level: 37, deletable: true },
        { id: '20261026T090000000Z-r500-auto', date: ISO(Date.now() - 3600000), revision: 500, reason: 'auto', pool: 'auto', size: 609000, level: 37, deletable: false },
        { id: '20261025T090000000Z-r480-preimport', date: ISO(Date.now() - 90000000), revision: 480, reason: 'preimport', pool: 'system', size: 590000, level: 36, deletable: false },
      ],
      [OTHER]: [{ id: '20261020T080000000Z-r900-auto', date: ISO(Date.now() - 86400000 * 6), revision: 900, reason: 'auto', pool: 'auto', size: 640000, level: 41, deletable: false }],
    },
    sets: {}, uploads: {}, zip: null,
    // Failure switches of the extras: export 413 ('parts' for 1,900-character parts, or 'bytes'), the text report's share
    // result, POST clipboard answering 501.
    exportTooLarge: null, exports: [], shareTextResult: null, clipboardFails: false,
  };
  for (const k in keys) if (keys[k].editable) state.values[k] = keys[k].default;
  state.boot = Object.assign({}, state.values);
  let port = 0;
  const started = Date.now();

  const json = (res, status, body, extra) => {
    res.writeHead(status, Object.assign({ 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' }, extra || {}));
    res.end(JSON.stringify(body));
  };
  const error = (res, status, code, message, details) => json(res, status, Object.assign({ error: code, message }, details ? { details } : {}));
  const restartPending = () => Object.keys(keys).filter((k) => keys[k].apply === 'restart' && keys[k].editable && state.values[k] !== state.boot[k]);
  const settingsBody = () => ({
    file: '/data/McvsOffline/settings.json', fallback: state.fallback, error: state.fallback ? 'Unexpected character at line 4, column 3' : null,
    fingerprint: '512:638000000000000000', restartPending: restartPending(),
    groups: groups.map((g) => ({ id: g.id, label: g.label, keys: g.keys.map((k) => Object.assign({}, k, k.editable ? { value: state.values[k.key] } : {})) })),
    presets: [
      { id: 'standard', label: 'Standard', values: { 'economy.versusCoinsMultiplier': 1, 'economy.creditsMultiplier': 1, 'economy.xpMultiplier': 1, 'economy.priceMultiplier': 1, 'crm.dailyCapMultiplier': 1 } },
      { id: 'relaxed', label: 'Relaxed', values: { 'economy.versusCoinsMultiplier': 2, 'economy.priceMultiplier': 0.5 } },
      { id: 'fast', label: 'Fast', values: { 'economy.versusCoinsMultiplier': 5 } },
    ],
  });
  const caps = { openPage: true, shareFile: true, saveToDownloads: true, shareText: true, clipboard: true, toast: true, save: true, exit: true };
  const statusBody = (storage) => ({
    version: '0.1.0-dev', build: '9',
    server: { started: ISO(started), uptimeSeconds: Math.round((Date.now() - started) / 1000) + 3600, instance: 'mcvs1a2b3c4d', port, bind: '127.0.0.1', lanMode: false, hostCheck: true, dataDir: '/storage/emulated/0/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/McvsOffline', dataPack: '/storage/emulated/0/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/Data' },
    host: { platform: 'android', sdkInt: 36, deviceId: 'dev', logsDir: '/storage/emulated/0/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/McvsOffline/logs', fallbackLogsDir: null, publicLogsDir: '/storage/emulated/0/Download/MCVS-Offline', mirror: 'active, 3 files, last copy 12 s ago', watchdog: 'game thread tid 1234, paused for the page', browser: 'initialized', browserOpen: true, freeBytes: 12345678901, gameCredential: 'anonymous:a1b2…f9e8', restartVia: 'game', missingJni: [], capabilities: caps },
    settings: { file: '/data/McvsOffline/settings.json', fallback: state.fallback, error: null, restartPending: restartPending().length },
    player: { account: ACCOUNT, fedId: 'fed_id:' + ACCOUNT, alias: 'Player0815', source: 'game', level: 37, revision: 512, modified: ISO(Date.now() - 420000), lastGameSave: ISO(Date.now() - 420000), hasGameProfile: true },
    staged: { pending: state.staged, probation: null, last: state.last },
    exit: { state: 'idle', last: { kind: 'restart', result: 'cancelled', at: ISO(Date.now() - 7200000), message: 'The game did not save, so it was not restarted.' } },
    problems: [{ time: ISO(Date.now() - 60000), level: 'WARN', category: 'crm', message: 'CRM document: 2 store items dropped\n  first: X\n  second: Y', source: 'server' }],
    crm: { built: ISO(Date.now() - 300000), chars: 89282, problems: 0, collectorPhase: 'Guard', collectorReason: 'live ops are off' },
    storage: storage ? { profiles: { files: 2, bytes: 1251000 }, backups: { files: 4, bytes: 2450000 }, blobs: { files: 0, bytes: 0 }, messages: { files: 2, bytes: 3000 }, logs: { files: 6, bytes: 30000000 }, admin: { files: 1, bytes: 107000 }, other: { files: 9, bytes: 40000 }, total: 33801000, scanCapped: false } : undefined,
  });
  const account = (id) => (id === ACCOUNT
    ? { account: ACCOUNT, fedId: 'fed_id:' + ACCOUNT, alias: 'Player0815', created: ISO(started - 864e6), credentials: ['anonymous:a1b2…f9e8'], isCurrent: true, hasGameProfile: true, level: 37, revision: 512, modified: ISO(Date.now() - 420000), lastGameSave: ISO(Date.now() - 420000), size: 612000, backups: state.backups[ACCOUNT].length }
    : { account: OTHER, fedId: 'fed_id:' + OTHER, alias: 'Player4711', created: ISO(started - 1728e6), credentials: [], isCurrent: false, hasGameProfile: true, level: 41, revision: 900, modified: ISO(Date.now() - 86400000 * 6), lastGameSave: null, size: 640000, backups: 1 });
  const manifest = (kind, plan, target, summary) => ({ id: ISO(Date.now()).replace(/[-:.]/g, '') + '-' + kind, kind, plan, created: ISO(Date.now()), attempts: 0, done: [], target, summary });
  const stage = (res, body, m) => {
    if (state.staged && !body.replace) return error(res, 409, 'staged_exists', 'Another operation is already waiting for a restart.');
    state.staged = m;
    return json(res, 200, { staged: m });
  };
  const crc = (s) => (crypto.createHash('md5').update(s).digest('hex').slice(0, 8));
  const transfer = (partChars) => {
    const data = crypto.randomBytes(6000).toString('base64').replace(/=+$/, '');
    const set = crypto.randomBytes(4).toString('hex');
    const n = Math.ceil(data.length / partChars);
    const texts = [];
    for (let k = 0; k < n; k++) {
      const d = data.slice(k * partChars, (k + 1) * partChars);
      texts.push('MCVS-SAVE:1:' + set + ':' + (k + 1) + '/' + n + ':d:6000:' + crc(data) + ':' + crc(d) + ':\n' + d + '\n~');
    }
    return { set, method: 'd', chars: texts.join('').length, parts: n, partChars, texts };
  };
  const validation = (plan) => ({
    complete: true, uploadId: 'u-' + crypto.randomBytes(8).toString('hex'),
    bundle: { account: plan === 'replace' ? ACCOUNT : OTHER, alias: plan === 'replace' ? 'Player0815' : 'Player4711', level: 41, revision: 900, modified: ISO(Date.now() - 3600000), hasGameProfile: true, size: 640000, messages: 2, created: ISO(Date.now() - 3000000), server: '0.1.0-dev' },
    local: { exists: true, isCurrent: plan === 'replace', revision: 120, modified: ISO(Date.now() - 864e5), hasGameProfile: true },
    current: { account: ACCOUNT, alias: 'Player0815', level: 37, source: 'game' },
    plans: [plan], plan, errors: [], warnings: plan === 'replace' ? [] : ['This phone already has a newer save of that account (revision 950 > 900).'], canStage: true,
  });
  const logEntries = (min, name) => {
    const order = ['TRACE', 'DEBUG', 'INFO', 'WARN', 'ERROR'];
    const all = [];
    // game.log has the game's own clock, without a date (design §7.1).
    const time = (i) => (name === 'game.log' ? '10:42:' + String(i).padStart(2, '0') + '.123' : ISO(Date.now() - (40 - i) * 1000));
    for (let i = 0; i < 40; i++) all.push({ t: time(i), l: order[i % 5], c: ['http', 'crm', 'storage'][i % 3], m: 'line ' + i + (i % 7 === 0 ? '\n   at Something.Else()' : '') });
    return all.filter((e) => order.indexOf(e.l) >= order.indexOf(String(min || 'trace').toUpperCase()));
  };

  const routes = [
    ['POST', /^session$/, false, (req, res, body) => {
      if (!body.ticket || !state.tickets.has(body.ticket)) return error(res, 401, 'ticket_invalid', 'Open this page again from the game.');
      state.tickets.delete(body.ticket);
      const sid = crypto.randomBytes(32).toString('hex');
      state.sessions.add(sid);
      return json(res, 200, { ok: true, session: { created: ISO(Date.now()), trigger: 'test', expiresIdle: 7200 } }, { 'Set-Cookie': 'mcvs_s=' + sid + '; Path=/_mcvs/; HttpOnly; SameSite=Strict' });
    }],
    ['GET', /^ping$/, false, (req, res, body, m, session) => json(res, 200, { ok: true, session, now: ISO(Date.now()), uptimeSeconds: 123 })],
    ['GET', /^status$/, true, (req, res, body, m, s, q) => json(res, 200, statusBody(q.get('storage') === '1'))],
    ['GET', /^settings$/, true, (req, res) => json(res, 200, settingsBody())],
    ['POST', /^settings$/, true, (req, res, body) => {
      if (state.fallback) return error(res, 409, 'settings_fallback', 'settings.json cannot be read.');
      if (typeof body.fingerprint !== 'string') return error(res, 400, 'bad_request', 'fingerprint missing');
      const problems = [];
      for (const key of Object.keys(body.changes || {})) {
        const k = keys[key];
        if (!k || !k.editable) return error(res, 400, 'not_editable', key + ' cannot be changed here.');
        const v = body.changes[key];
        const t = k.type;
        if ((t === 'bool' && typeof v !== 'boolean') || ((t === 'int' || t === 'number') && typeof v !== 'number') || ((t === 'string' || t === 'enum' || t === 'tokens') && typeof v !== 'string')) problems.push({ key, message: 'wrong type' });
        else if (typeof v === 'number' && (v < k.min || v > k.max)) problems.push({ key, message: 'out of range' });
        else if (t === 'string' && k.pattern && !new RegExp(k.pattern).test(v)) problems.push({ key, message: 'not valid' });
      }
      if (problems.length) return error(res, 400, 'invalid', 'Some values are not valid.', { problems });
      const applied = [];
      for (const key of Object.keys(body.changes || {})) {
        state.values[key] = body.changes[key];
        applied.push({ key, apply: keys[key].apply });
      }
      return json(res, 200, { ok: true, applied, restartRequired: applied.some((a) => a.apply === 'restart'), backup: null, warnings: [], overwrittenCopy: null, settings: settingsBody() });
    }],
    ['GET', /^settings\/file$/, true, (req, res) => {
      if (!state.fallback) return error(res, 404, 'not_found', 'Not found.');
      res.writeHead(200, { 'Content-Type': 'text/plain; charset=utf-8', 'Cache-Control': 'no-store' });
      return res.end('{\n  "port": 51780,\n  "superPassword": "***"\n  oops\n}\n');
    }],
    ['POST', /^settings\/repair$/, true, (req, res, body) => {
      if (!state.fallback || body.confirm !== 'move-aside') return error(res, 409, 'invalid', 'Nothing to repair.');
      state.fallback = false;
      state.boot.devMenu = !state.values.devMenu; // the repaired file now differs from boot: a restart applies it
      return json(res, 200, { ok: true, movedTo: 'settings.json.broken-20261026T101500Z', restartRequired: true });
    }],
    ['GET', /^codes$/, true, (req, res) => json(res, 200, { codes: [{ code: 'UNLOCKALL', description: 'Everything that was released.', numericSuffix: true }, { code: 'UNLOCKMERCS', description: 'Every agent.', numericSuffix: true }], pageCode: 'MCVSOFFLINE', enabled: true })],
    ['POST', /^clipboard$/, true, (req, res, body) => {
      if (state.clipboardFails) return error(res, 501, 'not_supported', 'The game could not copy the text.');
      state.clipboard.push(body.text);
      return json(res, 200, { ok: true });
    }],
    ['POST', /^actions\/save-now$/, true, (req, res) => json(res, 202, { accepted: true })],
    ['POST', /^actions\/restart$/, true, (req, res, body) => {
      if (body.confirm !== true) return error(res, 400, 'bad_request', 'confirm missing');
      state.restarts.push(body);
      return json(res, 202, { restarting: true });
    }],
    ['POST', /^actions\/crm-reload$/, true, (req, res) => json(res, 200, { ok: true })],
    ['POST', /^actions\/unlock-now$/, true, (req, res) => (state.values.unlockAll ? json(res, 200, { queued: true }) : error(res, 409, 'invalid', 'Unlock everything is off.'))],
    ['GET', /^saves$/, true, (req, res) => json(res, 200, { current: { account: ACCOUNT, source: 'game', credential: 'anonymous:a1b2…f9e8' }, accounts: [account(ACCOUNT), account(OTHER)], staged: state.staged, probation: null, last: state.last })],
    ['GET', /^saves\/import\/files$/, true, (req, res) => json(res, 200, { directory: '/data/McvsOffline/import', files: [{ name: 'old-phone.txt', size: 107020, modified: ISO(Date.now() - 864e5) }], writableBy: 'On Android 10 and older any file manager can put files here; on Android 11 and later use a PC (USB file transfer or adb).' })],
    ['POST', /^saves\/import\/paste$/, true, (req, res, body) => {
      const re = /MCVS-SAVE:1:([0-9a-f]{8}):(\d+)\/(\d+):[ds]:(\d+):([0-9a-f]{8}):([0-9a-f]{8}):([\s\S]*?)~/g;
      let mm;
      let set = null;
      const rejected = [];
      while ((mm = re.exec(body.text || ''))) {
        const data = mm[7].replace(/[\s ]/g, '');
        if (crc(data) !== mm[6]) { rejected.push({ part: +mm[2], reason: 'damaged (copied incompletely?)' }); continue; }
        set = mm[1];
        const s = state.sets[set] = state.sets[set] || { total: +mm[3], have: {} };
        s.have[+mm[2]] = data;
      }
      if (!set) return error(res, 400, 'invalid', 'No MCVS Offline save found in the pasted text.');
      const s = state.sets[set];
      const have = Object.keys(s.have).map(Number).sort((a, b) => a - b);
      if (have.length < s.total) {
        const missing = [];
        for (let k = 1; k <= s.total; k++) if (!s.have[k]) missing.push(k);
        return json(res, 200, { complete: false, set, total: s.total, have, missing, rejected, message: 'Got parts ' + have.join(', ') + ' of ' + s.total + '. Paste part ' + missing.join(', ') + '.' });
      }
      const v = validation('replace');
      state.uploads[v.uploadId] = v;
      return json(res, 200, Object.assign({ rejected }, v));
    }],
    ['POST', /^saves\/import\/validate$/, true, (req, res, body) => {
      if (body.file !== 'old-phone.txt') return error(res, 404, 'not_found', 'Not found.');
      const v = validation('switch');
      state.uploads[v.uploadId] = v;
      return json(res, 200, v);
    }],
    ['POST', /^saves\/import\/stage$/, true, (req, res, body) => {
      const v = state.uploads[body.uploadId];
      if (!v) return error(res, 404, 'not_found', 'Not found.');
      if (body.confirm !== v.plan) return error(res, 400, 'invalid', 'Confirm the plan.');
      return stage(res, body, manifest('import', v.plan, v.bundle.account, { alias: v.bundle.alias, level: v.bundle.level, revision: v.bundle.revision }));
    }],
    ['GET', /^saves\/staged$/, true, (req, res) => json(res, 200, { pending: state.staged, probation: null, history: [state.last] })],
    ['DELETE', /^saves\/staged$/, true, (req, res) => { state.staged = null; return json(res, 200, { ok: true }); }],
    ['POST', /^saves\/staged\/undo$/, true, (req, res, body) => stage(res, body, manifest('undo', null, OTHER, { alias: 'Player0815', level: 3 }))],
    ['GET', /^saves\/([0-9a-f]{24})\/backups$/, true, (req, res, body, m) => (state.backups[m[1]] ? json(res, 200, { account: m[1], backups: state.backups[m[1]], limits: { auto: 20, event: 20, system: 10, manual: 10 } }) : error(res, 404, 'not_found', 'Not found.'))],
    ['POST', /^saves\/([0-9a-f]{24})\/backups$/, true, (req, res, body, m) => {
      const b = { id: ISO(Date.now()).replace(/[-:.]/g, '') + '-r512-manual', date: ISO(Date.now()), revision: 512, reason: 'manual', pool: 'manual', size: 612000, level: 37, deletable: true };
      state.backups[m[1]].unshift(b);
      return json(res, 200, { backup: b, asOf: ISO(Date.now() - 420000) });
    }],
    ['DELETE', /^saves\/([0-9a-f]{24})\/backups\/([^/]+)$/, true, (req, res, body, m) => {
      const list = state.backups[m[1]] || [];
      const i = list.findIndex((b) => b.id === decodeURIComponent(m[2]));
      if (i < 0) return error(res, 404, 'not_found', 'Not found.');
      if (!list[i].deletable) return error(res, 400, 'not_deletable', 'Only your own backups can be deleted.');
      list.splice(i, 1);
      return json(res, 200, { ok: true });
    }],
    ['POST', /^saves\/([0-9a-f]{24})\/restore$/, true, (req, res, body, m) => {
      if (body.confirm !== body.plan) return error(res, 400, 'invalid', 'Confirm the plan.');
      if ((m[1] === ACCOUNT) !== (body.plan === 'replace')) return error(res, 400, 'invalid', 'Wrong plan for this account.');
      return stage(res, body, manifest('restore', body.plan, m[1], { alias: account(m[1]).alias, level: 37, revision: 500 }));
    }],
    ['POST', /^saves\/([0-9a-f]{24})\/relink$/, true, (req, res, body, m) => (body.confirm === 'switch' ? stage(res, body, manifest('relink', 'switch', m[1], { alias: account(m[1]).alias, level: 41 })) : error(res, 400, 'invalid', 'Confirm.'))],
    ['POST', /^saves\/([0-9a-f]{24})\/export$/, true, (req, res, body) => {
      state.exports.push(body);
      if (state.exportTooLarge === 'parts' && body.format !== 'json' && body.partChars === 1900) {
        return error(res, 413, 'too_large', 'This save needs more than 999 parts of 1900 characters. Choose larger parts, or share it as a file.', { limit: 999, unit: 'parts', partChars: 1900 });
      }
      if (state.exportTooLarge === 'bytes' && body.format !== 'json') {
        return error(res, 413, 'too_large', 'This save is too large to export as transfer text. Export it as a .json file.', { limit: 16777216, unit: 'bytes', partChars: body.partChars || 60000 });
      }
      const t = transfer(body.partChars || 60000);
      if (body.target === 'text') return json(res, 200, { file: null, size: t.chars, asOf: ISO(Date.now() - 420000), transfer: t, share: null });
      t.texts = null;
      return json(res, 200, { file: 'mcvs-save-Player0815-20261026-1042.txt', size: t.chars, asOf: ISO(Date.now() - 420000), transfer: t, share: { ok: true, method: body.target === 'downloads' ? 'downloads' : 'sheet', publicPath: 'Download/MCVS-Offline/mcvs-save-Player0815-20261026-1042.txt', message: 'Shared.' } });
    }],
    ['GET', /^logs$/, true, (req, res) => json(res, 200, {
      directories: [{ index: 0, path: '/data/McvsOffline/logs', kind: 'primary' }],
      files: [{ dir: 0, name: 'server.log', kind: 'server', size: 5242880, modified: ISO(Date.now()) }, { dir: 0, name: 'bootstrap.log', kind: 'bootstrap', size: 80000, modified: ISO(Date.now()) }, { dir: 0, name: 'game.log', kind: 'game', size: 40000, modified: ISO(Date.now()) }, { dir: 0, name: 'crash-20261025-1010.txt', kind: 'crash', size: 9000, modified: ISO(Date.now() - 864e5) }],
      mirror: 'active, 3 files, last copy 12 s ago',
    })],
    ['POST', /^logs\/zip$/, true, (req, res) => { state.zip = 'mcvs-logs-20261026-101500.zip'; return json(res, 200, { file: state.zip, size: 2412345, entries: 14, skipped: 3, fits: { discord: true, gmail: true } }); }],
    ['POST', /^logs\/share$/, true, (req, res, body) => (body.file === state.zip ? json(res, 200, { share: { ok: true, method: 'sheet', publicPath: 'Download/MCVS-Offline/' + state.zip, message: 'Shared.' } }) : error(res, 404, 'not_found', 'Build the zip first.'))],
    ['POST', /^logs\/share-text$/, true, (req, res) => json(res, 200, { share: state.shareTextResult || { ok: true, method: 'text', publicPath: null, message: 'Choose an app.' } })],
    ['GET', /^logs\/([A-Za-z0-9._-]{1,128})$/, true, (req, res, body, m, s, q) => {
      const entries = logEntries(q.get('min'), m[1]).filter((e) => !q.get('q') || e.m.indexOf(q.get('q')) >= 0);
      return json(res, 200, { name: m[1], size: 5242880, window: 1048576, truncatedHead: true, entries: entries.slice(-Number(q.get('lines') || 200)) });
    }],
  ];

  const server = http.createServer((req, res) => {
    const chunks = [];
    req.on('data', (c) => chunks.push(c));
    req.on('end', () => {
      const raw = Buffer.concat(chunks).toString('utf8');
      const u = new URL(req.url, 'http://127.0.0.1:' + port);
      const v = (msg) => state.violations.push(req.method + ' ' + u.pathname + ': ' + msg);
      if (req.headers.host !== '127.0.0.1:' + port) v('Host ' + req.headers.host);
      if (u.pathname === '/_mcvs/ui/' && req.method === 'GET') {
        res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store', 'Content-Security-Policy': "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'" });
        return res.end(html);
      }
      if (!u.pathname.startsWith('/_mcvs/api/v1/')) return error(res, 404, 'not_found', 'Not found.');
      if (state.down) { req.socket.destroy(); return undefined; }
      const path = u.pathname.slice('/_mcvs/api/v1/'.length);
      let body = null;
      if (req.method !== 'GET') {
        // The write contract of §2.3/§5.2: JSON object, X-MCVS: 1, same-origin Origin.
        if (!/^application\/json/.test(req.headers['content-type'] || '')) { v('Content-Type ' + req.headers['content-type']); return error(res, 415, 'unsupported_media_type', 'JSON only.'); }
        if (req.headers['x-mcvs'] !== '1') { v('no X-MCVS: 1'); return error(res, 403, 'csrf', 'Refused.'); }
        if (req.headers.origin !== undefined && req.headers.origin !== 'http://127.0.0.1:' + port) { v('Origin ' + req.headers.origin); return error(res, 403, 'csrf', 'Refused.'); }
        try { body = JSON.parse(raw); } catch (e) { body = null; }
        if (!body || typeof body !== 'object' || Array.isArray(body)) { v('body is not a JSON object: ' + raw.slice(0, 80)); return error(res, 400, 'bad_request', 'JSON object expected.'); }
      } else if (raw.length) {
        v('GET with a body');
      }
      const sid = ((req.headers.cookie || '').match(/(?:^|;\s*)mcvs_s=([0-9a-f]{64})/) || [])[1];
      const session = !!(sid && state.sessions.has(sid));
      for (const [method, re, needsSession, handler] of routes) {
        const m = re.exec(path);
        if (!m || method !== req.method) continue;
        if (needsSession && !session) return error(res, 401, 'no_session', 'Open this page from the game.');
        return handler(req, res, body, m, session, u.searchParams);
      }
      v('unknown route');
      return error(res, 404, 'not_found', 'Not found.');
    });
  });
  return new Promise((resolve) => {
    server.listen(0, '127.0.0.1', () => {
      port = server.address().port;
      resolve({
        origin: 'http://127.0.0.1:' + port,
        state,
        mintTicket: () => { const t = crypto.randomBytes(16).toString('hex'); state.tickets.add(t); return t; },
        close: () => server.close(),
      });
    });
  });
}

if (require.main === module) {
  main().catch((e) => {
    log('SMOKE CRASHED: ' + (e && e.stack ? e.stack : e));
    process.exit(1);
  });
}

// For manual checks in a real browser (e.g. screenshots at the WebView sizes of §2.1).
module.exports = { startFixtures };
