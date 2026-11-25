/* Plaxis ProMax Modeller and Viewer: shared code. No libraries: this file and the page are all there is. */
"use strict";
const PP = (() => {
  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));

  /** el("div.cls#id", {attrs}, children...) */
  function el(spec, attrs, ...kids) {
    const m = /^([a-z0-9]+)?((?:[.#][\w-]+)*)$/i.exec(spec) || [];
    const n = document.createElement(m[1] || "div");
    (m[2] || "").replace(/([.#])([\w-]+)/g, (_, k, v) => { if (k === ".") n.classList.add(v); else n.id = v; });
    if (attrs && (typeof attrs !== "object" || attrs instanceof Node || Array.isArray(attrs))) { kids.unshift(attrs); attrs = null; }
    for (const [k, v] of Object.entries(attrs || {})) {
      if (v == null || v === false) continue;
      if (k.startsWith("on")) n.addEventListener(k.slice(2), v);
      else if (k === "style" && typeof v === "object") Object.assign(n.style, v);
      else if (k === "html") n.innerHTML = v;
      else n.setAttribute(k, v === true ? "" : v);
    }
    for (const c of kids.flat(9)) if (c != null && c !== false) n.append(c instanceof Node ? c : document.createTextNode(String(c)));
    return n;
  }

  // ---- Icons (24 px, stroke) ------------------------------------------------------------------------------------
  const P = {
    logo: '<path d="M3 17l5-9 4 6 3-4 6 7z" fill="currentColor" stroke="none"/>',
    select: '<path d="M5 3l14 8-6 1.5L10 19z"/>',
    pan: '<path d="M12 3v18M3 12h18M12 3l-3 3M12 3l3 3M12 21l-3-3M12 21l3-3M3 12l3-3M3 12l3 3M21 12l-3-3M21 12l-3 3"/>',
    borehole: '<path d="M12 2v20"/><path d="M8 6h8M8 11h8M8 16h8" opacity=".6"/><circle cx="12" cy="3" r="1.6" fill="currentColor"/>',
    layers: '<path d="M3 7l9-4 9 4-9 4z"/><path d="M3 12l9 4 9-4"/><path d="M3 17l9 4 9-4"/>',
    material: '<rect x="3" y="3" width="8" height="8" rx="1.5"/><rect x="13" y="3" width="8" height="8" rx="1.5"/><rect x="3" y="13" width="8" height="8" rx="1.5"/><rect x="13" y="13" width="8" height="8" rx="1.5"/>',
    point: '<circle cx="12" cy="12" r="3" fill="currentColor"/>',
    line: '<path d="M4 20L20 4"/><circle cx="4" cy="20" r="1.8" fill="currentColor"/><circle cx="20" cy="4" r="1.8" fill="currentColor"/>',
    polygon: '<path d="M4 18l3-12 10-2 3 11-9 5z"/>',
    rect: '<rect x="4" y="6" width="16" height="12" rx="1"/>',
    plate: '<path d="M10 2v20M14 2v20" stroke-width="2.4"/>',
    anchor: '<path d="M4 6l16 12" stroke-dasharray="3 2.4"/><circle cx="4" cy="6" r="2" fill="currentColor"/><circle cx="20" cy="18" r="2" fill="currentColor"/>',
    fixed: '<path d="M6 12h12"/><path d="M18 6v12M21 8l-3 2M21 12l-3 2M21 16l-3 2" opacity=".7"/>',
    geogrid: '<path d="M3 15c3-3 6-3 9 0s6 3 9 0" stroke-width="2.2"/>',
    embedded: '<path d="M12 2v20" stroke-dasharray="5 2 1 2"/><path d="M8 2h8"/>',
    interface: '<path d="M8 3v18" stroke-dasharray="2.5 2"/><path d="M14 3v18"/>',
    lineload: '<path d="M3 5h18"/><path d="M6 5v10M12 5v10M18 5v10"/><path d="M4 13l2 3 2-3M10 13l2 3 2-3M16 13l2 3 2-3"/>',
    pointload: '<path d="M12 3v14"/><path d="M8 13l4 5 4-5"/>',
    surfaceload: '<path d="M3 9l6-4h12l-6 4z"/><path d="M6 10v8M12 10v8M18 7v8"/>',
    water: '<path d="M2 14c2.5-2 5-2 7.5 0s5 2 7.5 0 3.5-1.6 5-1"/><path d="M12 3l3 5a3 3 0 11-6 0z"/>',
    mesh: '<path d="M3 4h18v16H3z"/><path d="M3 4l9 8 9-8M3 20l9-8 9 8M12 4v16M3 12h18"/>',
    stages: '<rect x="3" y="4" width="18" height="4" rx="1"/><rect x="3" y="10" width="13" height="4" rx="1"/><rect x="3" y="16" width="8" height="4" rx="1"/>',
    soil: '<path d="M2 8c3 1.5 6-1.5 10 0s7 1.5 10 0"/><path d="M2 14c3 1.5 6-1.5 10 0s7 1.5 10 0"/><path d="M2 4h20v16H2z"/>',
    structures: '<path d="M6 2v20M18 2v20"/><path d="M6 6l12 6"/><path d="M2 22h20"/>',
    flow: '<path d="M12 3l5 8a5 5 0 11-10 0z"/>',
    calculate: '<path d="M7 4l12 8-12 8z" fill="currentColor"/>',
    check: '<path d="M4 12l5 5L20 6"/>',
    warn: '<path d="M12 3l10 18H2z"/><path d="M12 10v5M12 18v.5"/>',
    undo: '<path d="M9 7L4 12l5 5"/><path d="M4 12h11a5 5 0 010 10h-2"/>',
    redo: '<path d="M15 7l5 5-5 5"/><path d="M20 12H9a5 5 0 000 10h2"/>',
    trash: '<path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13"/>',
    open: '<path d="M3 7h6l2 2h10v10H3z"/>',
    save: '<path d="M5 3h12l4 4v14H3V3z"/><path d="M7 3v6h9V3M7 21v-7h10v7"/>',
    new: '<path d="M6 2h9l5 5v15H6z"/><path d="M14 2v6h6M13 12v6M10 15h6"/>',
    fit: '<path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5"/>',
    zoomin: '<circle cx="10" cy="10" r="6"/><path d="M15 15l6 6M10 7v6M7 10h6"/>',
    zoomout: '<circle cx="10" cy="10" r="6"/><path d="M15 15l6 6M7 10h6"/>',
    cube: '<path d="M12 2l9 5v10l-9 5-9-5V7z"/><path d="M3 7l9 5 9-5M12 12v10"/>',
    plan: '<rect x="4" y="4" width="16" height="16" rx="1"/><path d="M4 12h16M12 4v16" opacity=".5"/>',
    grid: '<path d="M4 4h16v16H4zM4 9.3h16M4 14.6h16M9.3 4v16M14.6 4v16"/>',
    magnet: '<path d="M6 3v8a6 6 0 0012 0V3h-4v8a2 2 0 01-4 0V3z"/>',
    eye: '<path d="M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>',
    viewer: '<rect x="3" y="4" width="18" height="14" rx="2"/><path d="M3 14l5-4 4 3 3-2 6 4"/>',
    script: '<path d="M8 7l-5 5 5 5M16 7l5 5-5 5M14 4l-4 16"/>',
    table: '<rect x="3" y="4" width="18" height="16" rx="1"/><path d="M3 9h18M3 14h18M9 4v16"/>',
    chart: '<path d="M3 20h18"/><path d="M4 16l5-6 4 4 7-9"/>',
    section: '<path d="M3 20L21 4"/><path d="M3 4h4M17 20h4" opacity=".5"/>',
    curve: '<path d="M3 20c4-12 8-14 18-16"/><circle cx="3" cy="20" r="1.5" fill="currentColor"/>',
    deformed: '<path d="M3 5h18M3 12c6 3 12 3 18 0M3 19c6 5 12 5 18 0"/>',
    arrows: '<path d="M4 18L14 8M14 8h-5M14 8v5"/><path d="M12 20l8-8M20 12h-4M20 12v4"/>',
    contour: '<path d="M3 17c4-6 7 2 11-4s5-4 7-6"/><path d="M3 11c3-4 6 1 9-3s5-3 9-4" opacity=".6"/>',
    shading: '<rect x="3" y="4" width="18" height="16" rx="1"/><path d="M3 9h18M3 14h18" opacity=".5"/><rect x="3" y="4" width="18" height="5" fill="currentColor" opacity=".25"/>',
    cross: '<path d="M5 5l14 14M19 5L5 19"/>',
    plastic: '<rect x="5" y="5" width="4" height="4" fill="currentColor"/><rect x="14" y="10" width="4" height="4" fill="currentColor"/><rect x="7" y="15" width="4" height="4" fill="currentColor"/>',
    image: '<rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="9" cy="10" r="2"/><path d="M3 18l6-5 4 3 3-2 5 4"/>',
    share: '<circle cx="6" cy="12" r="2.5"/><circle cx="18" cy="6" r="2.5"/><circle cx="18" cy="18" r="2.5"/><path d="M8.2 11l7.6-4M8.2 13l7.6 4"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M2 12h2M20 12h2M5 5l1.5 1.5M17.5 17.5L19 19M5 19l1.5-1.5M17.5 6.5L19 5"/>',
    info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7.5v.5"/>',
    phase: '<path d="M4 6h16M4 12h10M4 18h6"/><circle cx="18" cy="16" r="3"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    copy: '<rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V4H4v12h4"/>',
    prev: '<path d="M15 5l-7 7 7 7"/>',
    next: '<path d="M9 5l7 7-7 7"/>',
    play: '<path d="M7 4l12 8-12 8z" fill="currentColor"/>',
    stop: '<rect x="6" y="6" width="12" height="12" rx="1.5" fill="currentColor"/>',
    licence: '<rect x="4" y="10" width="16" height="11" rx="2"/><path d="M8 10V7a4 4 0 018 0v3"/>',
  };
  function icon(name, cls) {
    const s = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    s.setAttribute("viewBox", "0 0 24 24"); s.setAttribute("fill", "none"); s.setAttribute("stroke", "currentColor");
    s.setAttribute("stroke-width", "1.7"); s.setAttribute("stroke-linecap", "round"); s.setAttribute("stroke-linejoin", "round");
    if (cls) s.setAttribute("class", cls);
    s.innerHTML = P[name] || P.info;
    return s;
  }

  // ---- Theme ----------------------------------------------------------------------------------------------------
  function theme(mode) {
    if (mode) { try { localStorage.setItem("ppm-theme", mode); } catch (e) { } }
    let m = mode; try { m = m || localStorage.getItem("ppm-theme") || "auto"; } catch (e) { m = m || "auto"; }
    if (m === "auto") document.documentElement.removeAttribute("data-theme"); else document.documentElement.setAttribute("data-theme", m);
    return m;
  }
  const css = (name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  const isDark = () => { const t = document.documentElement.getAttribute("data-theme"); return t ? t === "dark" : matchMedia("(prefers-color-scheme: dark)").matches; };

  // ---- Menus ----------------------------------------------------------------------------------------------------
  /** items: [{label, key, run, check:()=>bool, enabled:()=>bool} | "-" | {head}] */
  function menubar(host, spec) {
    host.innerHTML = "";
    let open = null;
    const close = () => { if (open) open.classList.remove("open"); open = null; };
    for (const [title, items] of spec) {
      const m = el("div.menu");
      const b = el("button", title);
      const dd = el("div.dropdown");
      const fill = () => {
        dd.innerHTML = "";
        for (const it of (typeof items === "function" ? items() : items)) {
          if (it === "-") { dd.append(el("hr")); continue; }
          if (it.head) { dd.append(el("div.head", it.head)); continue; }
          const on = it.check ? it.check() : null;
          const bt = el("button", { onclick: () => { close(); it.run && it.run(); } }, el("span.check", on ? "✓" : ""), it.label, it.key ? el("span.key", it.key) : null);
          if (it.enabled && !it.enabled()) bt.disabled = true;
          dd.append(bt);
        }
      };
      b.addEventListener("mousedown", (e) => { e.preventDefault(); if (open === m) close(); else { close(); fill(); m.classList.add("open"); open = m; } });
      b.addEventListener("mouseenter", () => { if (open && open !== m) { close(); fill(); m.classList.add("open"); open = m; } });
      m.append(b, dd);
      host.append(m);
    }
    document.addEventListener("mousedown", (e) => { if (open && !open.contains(e.target)) close(); });
    document.addEventListener("keydown", (e) => { if (e.key === "Escape") close(); });
  }

  // ---- Dialogs and toasts ---------------------------------------------------------------------------------------
  function dialog({ title, sub, size, body, buttons, onclose }) {
    const scrim = el("div.scrim");
    const close = () => { scrim.remove(); onclose && onclose(); };
    const foot = el("footer");
    for (const b of buttons || [{ label: "Close" }]) {
      if (b.left) { foot.append(el("div.left", b.left)); continue; }
      foot.append(el("button.btn" + (b.primary ? ".primary" : "") + (b.danger ? ".danger" : ""), {
        onclick: async () => { if (b.run && (await b.run()) === false) return; close(); },
      }, b.label));
    }
    const d = el("div.dialog" + (size ? "." + size : ""), el("header", el("h2", title), sub ? el("span.sub", sub) : null, el("button.x", { onclick: close, title: "Close" }, "×")),
      el("div.content", body), foot);
    scrim.append(d);
    scrim.addEventListener("mousedown", (e) => { if (e.target === scrim) close(); });
    document.body.append(scrim);
    const first = d.querySelector("input,select");
    if (first) setTimeout(() => first.focus(), 30);
    return { close, root: d };
  }
  let toastTimer = 0;
  function toast(text, ms = 2600) {
    $$(".toast").forEach((t) => t.remove());
    const t = el("div.toast", text);
    document.body.append(t);
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => t.remove(), ms);
  }

  // ---- Numbers --------------------------------------------------------------------------------------------------
  function fmt(v, d = 3) {
    if (v == null || !isFinite(v)) return "–";
    const a = Math.abs(v);
    if (a !== 0 && (a < 1e-3 || a >= 1e6)) return v.toExponential(Math.max(1, d - 1)).replace("e", "E");
    return (+v.toFixed(d)).toString();
  }
  function fmtFixed(v, d) { return v == null || !isFinite(v) ? "–" : v.toFixed(d); }
  const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
  function niceStep(span, target) {
    const raw = span / Math.max(1, target);
    const p = Math.pow(10, Math.floor(Math.log10(raw)));
    const n = raw / p;
    return (n < 1.5 ? 1 : n < 3 ? 2 : n < 7 ? 5 : 10) * p;
  }

  // ---- Colour maps ----------------------------------------------------------------------------------------------
  // Plaxis's own scale runs blue (low) to red (high) through cyan, green and yellow; the Viewer uses the same order
  // so engineers read it the same way, with evenly spaced, slightly softened hues.
  const RAINBOW = ["#1d2a9a", "#1f4fd6", "#1d86f0", "#16b4ea", "#17cfc2", "#2fd38a", "#7bd648", "#c3dc2e", "#f2d22a", "#f9a825", "#f4731f", "#e2401c", "#b3161b"];
  function hex(c) { const v = parseInt(c.slice(1), 16); return [(v >> 16) & 255, (v >> 8) & 255, v & 255]; }
  const RAINBOW_RGB = RAINBOW.map(hex);
  function ramp(t) {
    t = clamp(t, 0, 1) * (RAINBOW_RGB.length - 1);
    const i = Math.min(RAINBOW_RGB.length - 2, Math.floor(t)), f = t - i;
    const a = RAINBOW_RGB[i], b = RAINBOW_RGB[i + 1];
    return [a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f];
  }
  const rgb = (c, a = 1) => `rgba(${c[0] | 0},${c[1] | 0},${c[2] | 0},${a})`;
  function bandColours(n) { const out = []; for (let i = 0; i < n; i++) out.push(rgb(ramp(n === 1 ? 0.5 : i / (n - 1)))); return out; }
  function shade(hexColour, f) { const c = hex(hexColour); return rgb(c.map((v) => clamp(v * f, 0, 255))); }
  function mix(hexColour, toward, t) { const a = hex(hexColour), b = hex(toward); return rgb(a.map((v, i) => v + (b[i] - v) * t)); }

  /** Band levels from min to max: n bands, "nice" when it helps reading. */
  function levels(min, max, n = 13) {
    if (!isFinite(min) || !isFinite(max)) return [0, 1];
    if (max - min < 1e-12) { const d = Math.abs(max) * 0.05 || 1e-6; min -= d; max += d; }
    const out = [];
    for (let i = 0; i <= n; i++) out.push(min + (max - min) * i / n);
    return out;
  }

  // ---- Geometry -------------------------------------------------------------------------------------------------
  function inside(poly, x, y) {
    let c = false;
    for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
      const xi = poly[i][0], yi = poly[i][1], xj = poly[j][0], yj = poly[j][1];
      if ((yi > y) !== (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) c = !c;
    }
    return c;
  }
  function segDist(px, py, ax, ay, bx, by) {
    const dx = bx - ax, dy = by - ay, l = dx * dx + dy * dy;
    let t = l ? ((px - ax) * dx + (py - ay) * dy) / l : 0;
    t = clamp(t, 0, 1);
    return Math.hypot(px - (ax + t * dx), py - (ay + t * dy));
  }
  function area(poly) { let a = 0; for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) a += (poly[j][0] + poly[i][0]) * (poly[j][1] - poly[i][1]); return a / 2; }

  // ---- Delaunay triangulation (sweep-hull, after Mapbox's Delaunator, ISC licence) ------------------------------
  function delaunay(coords) {
    const n = coords.length >> 1;
    const maxT = Math.max(2 * n - 5, 0);
    const triangles = new Uint32Array(maxT * 3), halfedges = new Int32Array(maxT * 3);
    const hashSize = Math.ceil(Math.sqrt(n));
    const hullPrev = new Uint32Array(n), hullNext = new Uint32Array(n), hullTri = new Uint32Array(n), hullHash = new Int32Array(hashSize).fill(-1);
    const ids = new Uint32Array(n), dists = new Float64Array(n);
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (let i = 0; i < n; i++) {
      const x = coords[2 * i], y = coords[2 * i + 1];
      if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y;
      ids[i] = i;
    }
    const cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
    let i0 = 0, i1 = 0, i2 = 0, minDist = Infinity;
    for (let i = 0; i < n; i++) { const d = dist(cx, cy, coords[2 * i], coords[2 * i + 1]); if (d < minDist) { i0 = i; minDist = d; } }
    const i0x = coords[2 * i0], i0y = coords[2 * i0 + 1];
    minDist = Infinity;
    for (let i = 0; i < n; i++) { if (i === i0) continue; const d = dist(i0x, i0y, coords[2 * i], coords[2 * i + 1]); if (d < minDist && d > 0) { i1 = i; minDist = d; } }
    let i1x = coords[2 * i1], i1y = coords[2 * i1 + 1];
    let minRadius = Infinity;
    for (let i = 0; i < n; i++) {
      if (i === i0 || i === i1) continue;
      const r = circumradius(i0x, i0y, i1x, i1y, coords[2 * i], coords[2 * i + 1]);
      if (r < minRadius) { i2 = i; minRadius = r; }
    }
    let i2x = coords[2 * i2], i2y = coords[2 * i2 + 1];
    if (minRadius === Infinity) return { triangles: new Uint32Array(0), halfedges: new Int32Array(0) };
    if (orient(i0x, i0y, i1x, i1y, i2x, i2y) < 0) { const i = i1, x = i1x, y = i1y; i1 = i2; i1x = i2x; i1y = i2y; i2 = i; i2x = x; i2y = y; }
    const center = circumcenter(i0x, i0y, i1x, i1y, i2x, i2y);
    const ccx = center.x, ccy = center.y;
    for (let i = 0; i < n; i++) dists[i] = dist(coords[2 * i], coords[2 * i + 1], ccx, ccy);
    const order = Array.from(ids).sort((a, b) => dists[a] - dists[b]);
    let hullStart = i0, hullSize = 3;
    hullNext[i0] = hullPrev[i2] = i1; hullNext[i1] = hullPrev[i0] = i2; hullNext[i2] = hullPrev[i1] = i0;
    hullTri[i0] = 0; hullTri[i1] = 1; hullTri[i2] = 2;
    const hashKey = (x, y) => Math.floor(pseudoAngle(x - ccx, y - ccy) * hashSize) % hashSize;
    hullHash[hashKey(i0x, i0y)] = i0; hullHash[hashKey(i1x, i1y)] = i1; hullHash[hashKey(i2x, i2y)] = i2;
    let trianglesLen = 0;
    const EDGE_STACK = new Uint32Array(512);
    const addTriangle = (a, b, c, ha, hb, hc) => {
      const t = trianglesLen;
      triangles[t] = a; triangles[t + 1] = b; triangles[t + 2] = c;
      link(t, ha); link(t + 1, hb); link(t + 2, hc);
      trianglesLen += 3;
      return t;
    };
    const link = (a, b) => { halfedges[a] = b; if (b !== -1) halfedges[b] = a; };
    const legalize = (a) => {
      let i = 0, ar = 0;
      while (true) {
        const b = halfedges[a];
        const a0 = a - a % 3;
        ar = a0 + (a + 2) % 3;
        if (b === -1) { if (i === 0) break; a = EDGE_STACK[--i]; continue; }
        const b0 = b - b % 3, al = a0 + (a + 1) % 3, bl = b0 + (b + 2) % 3;
        const p0 = triangles[ar], pr = triangles[a], pl = triangles[al], p1 = triangles[bl];
        const illegal = inCircle(coords[2 * p0], coords[2 * p0 + 1], coords[2 * pr], coords[2 * pr + 1], coords[2 * pl], coords[2 * pl + 1], coords[2 * p1], coords[2 * p1 + 1]);
        if (illegal) {
          triangles[a] = p1; triangles[b] = p0;
          const hbl = halfedges[bl];
          if (hbl === -1) {
            let e = hullStart;
            do { if (hullTri[e] === bl) { hullTri[e] = a; break; } e = hullPrev[e]; } while (e !== hullStart);
          }
          link(a, hbl); link(b, halfedges[ar]); link(ar, bl);
          const br = b0 + (b + 1) % 3;
          if (i < EDGE_STACK.length) EDGE_STACK[i++] = br;
        } else {
          if (i === 0) break;
          a = EDGE_STACK[--i];
        }
      }
      return ar;
    };
    addTriangle(i0, i1, i2, -1, -1, -1);
    let xp = 0, yp = 0;
    for (let k = 0; k < order.length; k++) {
      const i = order[k], x = coords[2 * i], y = coords[2 * i + 1];
      if (k > 0 && Math.abs(x - xp) <= 1e-12 && Math.abs(y - yp) <= 1e-12) continue;
      xp = x; yp = y;
      if (i === i0 || i === i1 || i === i2) continue;
      let start = 0;
      for (let j = 0, key = hashKey(x, y); j < hashSize; j++) { start = hullHash[(key + j) % hashSize]; if (start !== -1 && start !== hullNext[start]) break; }
      start = hullPrev[start];
      let e = start, q;
      while (q = hullNext[e], orient(x, y, coords[2 * e], coords[2 * e + 1], coords[2 * q], coords[2 * q + 1]) >= 0) { e = q; if (e === start) { e = -1; break; } }
      if (e === -1) continue;
      let t = addTriangle(e, i, hullNext[e], -1, -1, hullTri[e]);
      hullTri[i] = legalize(t + 2); hullTri[e] = t; hullSize++;
      let nn = hullNext[e];
      while (q = hullNext[nn], orient(x, y, coords[2 * nn], coords[2 * nn + 1], coords[2 * q], coords[2 * q + 1]) < 0) {
        t = addTriangle(nn, i, q, hullTri[i], -1, hullTri[nn]);
        hullTri[i] = legalize(t + 2); hullNext[nn] = nn; hullSize--; nn = q;
      }
      if (e === start) {
        while (q = hullPrev[e], orient(x, y, coords[2 * q], coords[2 * q + 1], coords[2 * e], coords[2 * e + 1]) < 0) {
          t = addTriangle(q, i, e, -1, hullTri[e], hullTri[q]);
          legalize(t + 2); hullTri[q] = t; hullNext[e] = e; hullSize--; e = q;
        }
      }
      hullStart = hullPrev[i] = e; hullNext[e] = hullPrev[nn] = i; hullNext[i] = nn;
      hullHash[hashKey(x, y)] = i; hullHash[hashKey(coords[2 * e], coords[2 * e + 1])] = e;
    }
    return { triangles: triangles.subarray(0, trianglesLen), halfedges: halfedges.subarray(0, trianglesLen) };

    function dist(ax, ay, bx, by) { const dx = ax - bx, dy = ay - by; return dx * dx + dy * dy; }
    function pseudoAngle(dx, dy) { const p = dx / (Math.abs(dx) + Math.abs(dy)); return (dy > 0 ? 3 - p : 1 + p) / 4; }
    function orient(px, py, qx, qy, rx, ry) { return (qy - py) * (rx - qx) - (qx - px) * (ry - qy); }
    function inCircle(ax, ay, bx, by, cx, cy, px, py) {
      const dx = ax - px, dy = ay - py, ex = bx - px, ey = by - py, fx = cx - px, fy = cy - py;
      const ap = dx * dx + dy * dy, bp = ex * ex + ey * ey, cp = fx * fx + fy * fy;
      return dx * (ey * cp - bp * fy) - dy * (ex * cp - bp * fx) + ap * (ex * fy - ey * fx) < 0;
    }
    function circumradius(ax, ay, bx, by, cx, cy) {
      const dx = bx - ax, dy = by - ay, ex = cx - ax, ey = cy - ay, bl = dx * dx + dy * dy, cl = ex * ex + ey * ey, d = 0.5 / (dx * ey - dy * ex);
      const x = (ey * bl - dy * cl) * d, y = (dx * cl - ex * bl) * d;
      return x * x + y * y;
    }
    function circumcenter(ax, ay, bx, by, cx, cy) {
      const dx = bx - ax, dy = by - ay, ex = cx - ax, ey = cy - ay, bl = dx * dx + dy * dy, cl = ex * ex + ey * ey, d = 0.5 / (dx * ey - dy * ex);
      return { x: ax + (ey * bl - dy * cl) * d, y: ay + (dx * cl - ex * bl) * d };
    }
  }

  /**
   * A display mesh over points: Delaunay triangles, then the ones outside the domain dropped (keep(cx, cy) false at the
   * centroid) and the slivers across gaps (an edge much longer than the usual spacing). For drawing only.
   */
  function displayMesh(xs, ys, keep) {
    const n = xs.length, coords = new Float64Array(2 * n);
    for (let i = 0; i < n; i++) { coords[2 * i] = xs[i]; coords[2 * i + 1] = ys[i]; }
    const d = delaunay(coords);
    const t = d.triangles, lens = [];
    for (let i = 0; i < t.length; i += 3 * 7) {
      const a = t[i], b = t[i + 1];
      lens.push(Math.hypot(xs[a] - xs[b], ys[a] - ys[b]));
    }
    lens.sort((a, b) => a - b);
    const typical = lens.length ? lens[Math.floor(lens.length / 2)] : 1;
    const maxEdge = typical * 4.5;
    const out = [];
    for (let i = 0; i < t.length; i += 3) {
      const a = t[i], b = t[i + 1], c = t[i + 2];
      const e = Math.max(Math.hypot(xs[a] - xs[b], ys[a] - ys[b]), Math.hypot(xs[b] - xs[c], ys[b] - ys[c]), Math.hypot(xs[c] - xs[a], ys[c] - ys[a]));
      if (e > maxEdge) continue;
      const cx = (xs[a] + xs[b] + xs[c]) / 3, cy = (ys[a] + ys[b] + ys[c]) / 3;
      if (keep && !keep(cx, cy)) continue;
      out.push(a, b, c);
    }
    return { tri: Uint32Array.from(out), typical };
  }

  /** Grid index over triangles, to find the one under a point (probe, cross sections). */
  function triIndex(xs, ys, tri) {
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (let i = 0; i < xs.length; i++) { minX = Math.min(minX, xs[i]); maxX = Math.max(maxX, xs[i]); minY = Math.min(minY, ys[i]); maxY = Math.max(maxY, ys[i]); }
    const nt = tri.length / 3, cells = Math.max(1, Math.ceil(Math.sqrt(nt / 2)));
    const w = (maxX - minX) / cells || 1, h = (maxY - minY) / cells || 1;
    const buckets = Array.from({ length: cells * cells }, () => []);
    for (let k = 0; k < nt; k++) {
      const a = tri[3 * k], b = tri[3 * k + 1], c = tri[3 * k + 2];
      const x0 = Math.min(xs[a], xs[b], xs[c]), x1 = Math.max(xs[a], xs[b], xs[c]), y0 = Math.min(ys[a], ys[b], ys[c]), y1 = Math.max(ys[a], ys[b], ys[c]);
      for (let gx = clamp(Math.floor((x0 - minX) / w), 0, cells - 1); gx <= clamp(Math.floor((x1 - minX) / w), 0, cells - 1); gx++)
        for (let gy = clamp(Math.floor((y0 - minY) / h), 0, cells - 1); gy <= clamp(Math.floor((y1 - minY) / h), 0, cells - 1); gy++) buckets[gy * cells + gx].push(k);
    }
    return {
      /** {k, w:[wa,wb,wc]} or null */
      find(x, y) {
        const gx = Math.floor((x - minX) / w), gy = Math.floor((y - minY) / h);
        if (gx < 0 || gy < 0 || gx >= cells || gy >= cells) return null;
        for (const k of buckets[gy * cells + gx]) {
          const a = tri[3 * k], b = tri[3 * k + 1], c = tri[3 * k + 2];
          const det = (ys[b] - ys[c]) * (xs[a] - xs[c]) + (xs[c] - xs[b]) * (ys[a] - ys[c]);
          if (!det) continue;
          const wa = ((ys[b] - ys[c]) * (x - xs[c]) + (xs[c] - xs[b]) * (y - ys[c])) / det;
          const wb = ((ys[c] - ys[a]) * (x - xs[c]) + (xs[a] - xs[c]) * (y - ys[c])) / det;
          const wc = 1 - wa - wb;
          if (wa >= -1e-9 && wb >= -1e-9 && wc >= -1e-9) return { k, a, b, c, w: [wa, wb, wc] };
        }
        return null;
      },
    };
  }

  /** Clips a triangle (points with values) to lo <= v <= hi: the polygon of that band, or null. */
  function bandPolygon(p, lo, hi) {
    let poly = p;
    poly = clipScalar(poly, lo, 1);
    if (poly.length < 3) return null;
    poly = clipScalar(poly, hi, -1);
    return poly.length < 3 ? null : poly;
  }
  function clipScalar(poly, level, sign) {
    const out = [];
    for (let i = 0; i < poly.length; i++) {
      const a = poly[i], b = poly[(i + 1) % poly.length];
      const ina = sign > 0 ? a[2] >= level : a[2] <= level, inb = sign > 0 ? b[2] >= level : b[2] <= level;
      if (ina) out.push(a);
      if (ina !== inb) {
        const t = (level - a[2]) / (b[2] - a[2]);
        out.push([a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, level]);
      }
    }
    return out;
  }

  // ---- 2D view: pan, zoom, world <-> screen (y up) --------------------------------------------------------------
  class View2D {
    constructor(canvas, onchange) {
      this.c = canvas; this.ctx = canvas.getContext("2d"); this.s = 10; this.ox = 0; this.oy = 0; this.onchange = onchange; this.dpr = 1;
      this.drag = null;
      canvas.addEventListener("wheel", (e) => {
        e.preventDefault();
        const r = canvas.getBoundingClientRect(), mx = e.clientX - r.left, my = e.clientY - r.top;
        const [wx, wy] = this.toWorld(mx, my);
        const f = Math.exp(-e.deltaY * 0.0015);
        this.s = clamp(this.s * f, 0.05, 50000);
        this.ox = mx - wx * this.s; this.oy = my + wy * this.s;
        this.onchange && this.onchange();
      }, { passive: false });
      canvas.addEventListener("mousedown", (e) => {
        if (e.button === 1 || e.button === 2 || (e.button === 0 && this.panMode)) {
          e.preventDefault(); this.drag = { x: e.clientX, y: e.clientY, ox: this.ox, oy: this.oy, moved: false };
        }
      });
      window.addEventListener("mousemove", (e) => {
        if (!this.drag) return;
        this.ox = this.drag.ox + e.clientX - this.drag.x; this.oy = this.drag.oy + e.clientY - this.drag.y;
        this.drag.moved = true;
        this.onchange && this.onchange();
      });
      window.addEventListener("mouseup", () => { this.drag = null; });
      canvas.addEventListener("contextmenu", (e) => e.preventDefault());
    }
    size() {
      const r = this.c.getBoundingClientRect(), dpr = window.devicePixelRatio || 1;
      this.w = r.width; this.h = r.height; this.dpr = dpr;
      if (this.c.width !== Math.round(r.width * dpr) || this.c.height !== Math.round(r.height * dpr)) {
        this.c.width = Math.round(r.width * dpr); this.c.height = Math.round(r.height * dpr);
      }
      this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }
    fit(x0, y0, x1, y1, pad = 60) {
      this.size();
      const w = Math.max(x1 - x0, 1e-6), h = Math.max(y1 - y0, 1e-6);
      this.s = Math.min((this.w - 2 * pad) / w, (this.h - 2 * pad) / h);
      this.ox = (this.w - w * this.s) / 2 - x0 * this.s;
      this.oy = (this.h + h * this.s) / 2 + y0 * this.s;
    }
    X(x) { return this.ox + x * this.s; }
    Y(y) { return this.oy - y * this.s; }
    toWorld(sx, sy) { return [(sx - this.ox) / this.s, (this.oy - sy) / this.s]; }
    zoom(f) { const cx = this.w / 2, cy = this.h / 2, [wx, wy] = this.toWorld(cx, cy); this.s *= f; this.ox = cx - wx * this.s; this.oy = cy + wy * this.s; this.onchange && this.onchange(); }
    grid(colorMinor, colorMajor, textColor, opts = {}) {
      const ctx = this.ctx, [x0, y1] = this.toWorld(0, 0), [x1, y0] = this.toWorld(this.w, this.h);
      const step = niceStep((x1 - x0), this.w / 70);
      ctx.lineWidth = 1;
      for (let pass = 0; pass < 2; pass++) {
        ctx.strokeStyle = pass ? colorMajor : colorMinor;
        ctx.beginPath();
        const st = pass ? step * 5 : step;
        for (let x = Math.ceil(x0 / st) * st; x <= x1; x += st) { const X = Math.round(this.X(x)) + 0.5; ctx.moveTo(X, 0); ctx.lineTo(X, this.h); }
        for (let y = Math.ceil(y0 / st) * st; y <= y1; y += st) { const Y = Math.round(this.Y(y)) + 0.5; ctx.moveTo(0, Y); ctx.lineTo(this.w, Y); }
        ctx.stroke();
      }
      if (opts.rulers !== false) {
        ctx.fillStyle = textColor; ctx.font = "10.5px Segoe UI, system-ui, sans-serif"; ctx.textBaseline = "top";
        const st = step * 5;
        for (let x = Math.ceil(x0 / st) * st; x <= x1; x += st) ctx.fillText(fmt(x, 2), this.X(x) + 3, this.h - 15);
        ctx.textBaseline = "bottom";
        for (let y = Math.ceil(y0 / st) * st; y <= y1; y += st) ctx.fillText(fmt(y, 2), 4, this.Y(y) - 2);
      }
      return step;
    }
    axes(color) {
      const ctx = this.ctx, x = 18, y = this.h - 30;
      ctx.strokeStyle = color; ctx.fillStyle = color; ctx.lineWidth = 1.5;
      ctx.beginPath(); ctx.moveTo(x, y); ctx.lineTo(x + 34, y); ctx.moveTo(x, y); ctx.lineTo(x, y - 34); ctx.stroke();
      ctx.font = "600 11px Segoe UI, sans-serif"; ctx.textBaseline = "middle";
      ctx.fillText("x", x + 39, y); ctx.fillText(this.upLabel || "y", x - 3, y - 42);
    }
  }

  // ---- 3D view: orbit camera, projection ------------------------------------------------------------------------
  class Orbit3D {
    constructor(canvas, onchange) {
      this.c = canvas; this.ctx = canvas.getContext("2d"); this.onchange = onchange;
      this.yaw = -0.75; this.pitch = 0.55; this.dist = 100; this.target = [0, 0, 0]; this.fov = 0.9; this.drag = null;
      canvas.addEventListener("wheel", (e) => { e.preventDefault(); this.dist = clamp(this.dist * Math.exp(e.deltaY * 0.0012), 1, 1e5); this.onchange && this.onchange(); }, { passive: false });
      canvas.addEventListener("mousedown", (e) => {
        if (e.button === 0 && this.planMode && !this.panMode) return;
        if (e.button === 0 || e.button === 1 || e.button === 2) { e.preventDefault(); this.drag = { x: e.clientX, y: e.clientY, b: e.button, shift: e.shiftKey, moved: false }; }
      });
      window.addEventListener("mousemove", (e) => {
        const d = this.drag; if (!d) return;
        const dx = e.clientX - d.x, dy = e.clientY - d.y; d.x = e.clientX; d.y = e.clientY;
        if (Math.abs(dx) + Math.abs(dy) > 0) d.moved = true;
        if (d.b === 0 && !d.shift && !this.panMode && !this.planMode) { this.yaw -= dx * 0.008; this.pitch = clamp(this.pitch + dy * 0.008, -1.45, 1.45); }
        else {
          const k = this.dist * 0.0018, [r, u] = this.basis();
          for (let i = 0; i < 3; i++) this.target[i] += (-r[i] * dx + u[i] * dy) * k;
        }
        this.onchange && this.onchange();
      });
      window.addEventListener("mouseup", () => { this.drag = null; });
      canvas.addEventListener("contextmenu", (e) => e.preventDefault());
    }
    size() {
      const r = this.c.getBoundingClientRect(), dpr = window.devicePixelRatio || 1;
      this.w = r.width; this.h = r.height;
      if (this.c.width !== Math.round(r.width * dpr) || this.c.height !== Math.round(r.height * dpr)) { this.c.width = Math.round(r.width * dpr); this.c.height = Math.round(r.height * dpr); }
      this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }
    eye() {
      const cp = Math.cos(this.pitch), sp = Math.sin(this.pitch);
      return [this.target[0] + this.dist * cp * Math.cos(this.yaw), this.target[1] + this.dist * cp * Math.sin(this.yaw), this.target[2] + this.dist * sp];
    }
    basis() {
      const e = this.eye(), f = norm([this.target[0] - e[0], this.target[1] - e[1], this.target[2] - e[2]]);
      const r = norm(cross(f, [0, 0, 1])), u = cross(r, f);
      return [r, u, f, e];
    }
    fit(min, max) {
      this.target = [(min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2];
      const r = Math.hypot(max[0] - min[0], max[1] - min[1], max[2] - min[2]) / 2;
      this.dist = r / Math.tan(this.fov / 2) * 1.05;
    }
    /** [sx, sy, depth] */
    project(p) {
      if (!this._b) this._b = this.basis();
      const [r, u, f, e] = this._b;
      const d = [p[0] - e[0], p[1] - e[1], p[2] - e[2]];
      const z = d[0] * f[0] + d[1] * f[1] + d[2] * f[2];
      const k = (this.h / 2) / Math.tan(this.fov / 2) / Math.max(z, 1e-3);
      return [this.w / 2 + (d[0] * r[0] + d[1] * r[1] + d[2] * r[2]) * k, this.h / 2 - (d[0] * u[0] + d[1] * u[1] + d[2] * u[2]) * k, z];
    }
    begin() { this.size(); this._b = this.basis(); }
    light(n) { const [, , f] = this._b, l = norm([-0.35 - f[0] * 0.5, -0.55 - f[1] * 0.5, 0.75 - f[2] * 0.5]); return Math.abs(n[0] * l[0] + n[1] * l[1] + n[2] * l[2]); }
  }
  function cross(a, b) { return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]; }
  function norm(a) { const l = Math.hypot(a[0], a[1], a[2]) || 1; return [a[0] / l, a[1] / l, a[2] / l]; }
  function faceNormal(p) { return norm(cross([p[1][0] - p[0][0], p[1][1] - p[0][1], p[1][2] - p[0][2]], [p[2][0] - p[0][0], p[2][1] - p[0][1], p[2][2] - p[0][2]])); }

  // ---- Charts ---------------------------------------------------------------------------------------------------
  /** series: [{name, xs, ys, color}]; opts: {xlabel, ylabel, swap (y down the side, for walls)} */
  function chart(canvas, series, opts = {}) {
    const r = canvas.getBoundingClientRect(), dpr = window.devicePixelRatio || 1;
    canvas.width = Math.round(r.width * dpr); canvas.height = Math.round(r.height * dpr);
    const ctx = canvas.getContext("2d"); ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    const W = r.width, H = r.height, L = 58, R = 16, T = 16, B = 38;
    ctx.clearRect(0, 0, W, H);
    let x0 = Infinity, x1 = -Infinity, y0 = Infinity, y1 = -Infinity;
    for (const s of series) for (let i = 0; i < s.xs.length; i++) {
      const x = s.xs[i], y = s.ys[i]; if (x == null || y == null || !isFinite(x) || !isFinite(y)) continue;
      x0 = Math.min(x0, x); x1 = Math.max(x1, x); y0 = Math.min(y0, y); y1 = Math.max(y1, y);
    }
    if (!isFinite(x0)) { ctx.fillStyle = css("--muted"); ctx.fillText("No values", W / 2 - 24, H / 2); return; }
    if (opts.zero) { y0 = Math.min(y0, 0); y1 = Math.max(y1, 0); }
    if (x1 - x0 < 1e-12) { x0 -= 1; x1 += 1; }
    if (y1 - y0 < 1e-12) { const d = Math.abs(y0) * 0.1 || 1; y0 -= d; y1 += d; }
    const py = (y1 - y0) * 0.06; y0 -= py; y1 += py;
    const X = (x) => L + (x - x0) / (x1 - x0) * (W - L - R), Y = (y) => H - B - (y - y0) / (y1 - y0) * (H - T - B);
    ctx.font = "10.5px Segoe UI, system-ui, sans-serif"; ctx.lineWidth = 1;
    const grid = css("--border-subtle"), text = css("--muted");
    const xs = niceStep(x1 - x0, 6), ys = niceStep(y1 - y0, 5);
    ctx.strokeStyle = grid; ctx.fillStyle = text; ctx.beginPath();
    ctx.textAlign = "center"; ctx.textBaseline = "top";
    for (let x = Math.ceil(x0 / xs) * xs; x <= x1 + 1e-9; x += xs) { const v = Math.abs(x) < xs * 1e-6 ? 0 : x; ctx.moveTo(X(x), T); ctx.lineTo(X(x), H - B); ctx.fillText(fmt(v, 3), X(x), H - B + 5); }
    ctx.textAlign = "right"; ctx.textBaseline = "middle";
    for (let y = Math.ceil(y0 / ys) * ys; y <= y1 + 1e-9; y += ys) { const v = Math.abs(y) < ys * 1e-6 ? 0 : y; ctx.moveTo(L, Y(y)); ctx.lineTo(W - R, Y(y)); ctx.fillText(fmt(v, 3), L - 6, Y(y)); }
    ctx.stroke();
    if (y0 < 0 && y1 > 0) { ctx.strokeStyle = text; ctx.beginPath(); ctx.moveTo(L, Y(0)); ctx.lineTo(W - R, Y(0)); ctx.stroke(); }
    ctx.textAlign = "center"; ctx.textBaseline = "bottom"; ctx.fillStyle = css("--text");
    if (opts.xlabel) ctx.fillText(opts.xlabel, (L + W - R) / 2, H - 4);
    if (opts.ylabel) { ctx.save(); ctx.translate(12, (T + H - B) / 2); ctx.rotate(-Math.PI / 2); ctx.textBaseline = "top"; ctx.fillText(opts.ylabel, 0, -6); ctx.restore(); }
    for (const s of series) {
      ctx.strokeStyle = s.color || css("--accent"); ctx.lineWidth = 2; ctx.beginPath();
      let started = false;
      for (let i = 0; i < s.xs.length; i++) {
        if (s.ys[i] == null || !isFinite(s.ys[i])) { started = false; continue; }
        const x = X(s.xs[i]), y = Y(s.ys[i]);
        if (!started) { ctx.moveTo(x, y); started = true; } else ctx.lineTo(x, y);
      }
      ctx.stroke();
      if (s.dots) { ctx.fillStyle = s.color || css("--accent"); for (let i = 0; i < s.xs.length; i++) if (s.ys[i] != null) { ctx.beginPath(); ctx.arc(X(s.xs[i]), Y(s.ys[i]), 3, 0, 7); ctx.fill(); } }
    }
    if (series.length > 1) {
      let lx = L + 8; ctx.textAlign = "left"; ctx.textBaseline = "middle";
      for (const s of series) { ctx.fillStyle = s.color; ctx.fillRect(lx, T + 4, 12, 3); ctx.fillStyle = css("--text"); ctx.fillText(s.name, lx + 16, T + 6); lx += ctx.measureText(s.name).width + 34; }
    }
  }

  /** A movable floating window inside host. */
  function floating(host, title, { x = 80, y = 80, w = 460, h = 300, onclose } = {}) {
    const body = el("div.fbody");
    const win = el("div.float", { style: { left: x + "px", top: y + "px", width: w + "px", height: h + "px" } },
      el("header", title, el("button.x", { title: "Close", onclick: () => { win.remove(); onclose && onclose(); } }, "×")), body);
    const head = win.firstChild;
    head.addEventListener("mousedown", (e) => {
      if (e.target.closest("button")) return;
      const sx = e.clientX, sy = e.clientY, ox = win.offsetLeft, oy = win.offsetTop;
      const mv = (ev) => { win.style.left = ox + ev.clientX - sx + "px"; win.style.top = oy + ev.clientY - sy + "px"; };
      const up = () => { window.removeEventListener("mousemove", mv); window.removeEventListener("mouseup", up); };
      window.addEventListener("mousemove", mv); window.addEventListener("mouseup", up);
    });
    host.append(win);
    return { win, body };
  }

  // ---- Files ----------------------------------------------------------------------------------------------------
  function download(name, data, type = "application/octet-stream") {
    const blob = data instanceof Blob ? data : new Blob([data], { type });
    const a = el("a", { href: URL.createObjectURL(blob), download: name });
    document.body.append(a); a.click(); setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
  }
  // A results file (.ppr): version 2 is gzip of "PPR2", the header's length, the header (JSON) and the numbers, with
  // each long list in the header pointing into the numbers, stored byte plane by byte plane: {"$f": [offset, count]}
  // 4-byte floats (NaN missing), or {"$q": [offset, count, low, step]} 2-byte steps (65535 missing). Version 1 is plain
  // gzip JSON. Both come back as plain JSON with plain lists (see ppm_build.py, write_pack).
  async function readPack(buffer) {
    let bytes = new Uint8Array(buffer);
    if (bytes[0] === 0x1f && bytes[1] === 0x8b) {
      const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream("gzip"));
      bytes = new Uint8Array(await new Response(stream).arrayBuffer());
    }
    if (!(bytes[0] === 0x50 && bytes[1] === 0x50 && bytes[2] === 0x52 && bytes[3] === 0x32)) return JSON.parse(new TextDecoder().decode(bytes));
    const n = bytes[4] | (bytes[5] << 8) | (bytes[6] << 16) | (bytes[7] << 24);
    const head = JSON.parse(new TextDecoder().decode(bytes.subarray(8, 8 + n)));
    const blob = bytes.subarray(8 + n), cache = new Map();
    const get = (offset, count) => {
      const key = offset + ":" + count;
      if (cache.has(key)) return cache.get(key);
      const raw = new Uint8Array(4 * count);
      for (let p = 0; p < 4; p++) { const plane = blob.subarray(offset + p * count, offset + (p + 1) * count); for (let i = 0; i < count; i++) raw[4 * i + p] = plane[i]; }
      const f = new Float32Array(raw.buffer), out = new Array(count);
      for (let i = 0; i < count; i++) { const v = f[i]; out[i] = v !== v ? null : +v.toPrecision(7); }
      cache.set(key, out);
      return out;
    };
    // 2-byte steps over the list's own range (soil results): value = low + q * step, 65535 is a missing value.
    const steps = (offset, count, low, step) => {
      const key = "q" + offset + ":" + count + ":" + low + ":" + step;
      if (cache.has(key)) return cache.get(key);
      const lo = blob.subarray(offset, offset + count), hi = blob.subarray(offset + count, offset + 2 * count), out = new Array(count);
      for (let i = 0; i < count; i++) { const q = lo[i] | (hi[i] << 8); out[i] = q === 65535 ? null : +(low + q * step).toPrecision(7); }
      cache.set(key, out);
      return out;
    };
    const walk = (v) => {
      if (Array.isArray(v)) return v.map(walk);
      if (v && typeof v === "object") {
        if (v.$f && Object.keys(v).length === 1) return get(v.$f[0], v.$f[1]);
        if (v.$q && Object.keys(v).length === 1) return steps(v.$q[0], v.$q[1], v.$q[2], v.$q[3]);
        const o = {}; for (const k in v) o[k] = walk(v[k]); return o;
      }
      return v;
    };
    head.phases = walk(head.phases || []);
    return head;
  }
  async function gunzipJson(buffer) {
    const bytes = new Uint8Array(buffer);
    if (bytes[0] === 0x1f && bytes[1] === 0x8b) {
      const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream("gzip"));
      return JSON.parse(await new Response(stream).text());
    }
    return JSON.parse(new TextDecoder().decode(bytes));
  }
  function pickFile(accept) {
    return new Promise((resolve) => {
      const i = el("input", { type: "file", accept, style: { display: "none" } });
      i.addEventListener("change", () => { resolve(i.files[0] || null); i.remove(); });
      document.body.append(i); i.click();
    });
  }
  function csv(rows) { return rows.map((r) => r.map((v) => { const s = v == null ? "" : String(v); return /[",\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s; }).join(",")).join("\r\n"); }

  return { $, $$, el, icon, theme, css, isDark, menubar, dialog, toast, fmt, fmtFixed, clamp, niceStep, ramp, rgb, bandColours, levels, shade, mix, hex,
    inside, segDist, area, delaunay, displayMesh, triIndex, bandPolygon, View2D, Orbit3D, faceNormal, norm, cross, chart, floating, download, gunzipJson, readPack, pickFile, csv };
})();
