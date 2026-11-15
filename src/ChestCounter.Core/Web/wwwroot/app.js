/* Chest Counter dashboard – vanilla JS, no external dependencies. */
(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);
  const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
  const DAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
  const nf = new Intl.NumberFormat(undefined);
  const nf1 = new Intl.NumberFormat(undefined, { minimumFractionDigits: 1, maximumFractionDigits: 1 });

  let snap = null;
  let showAllMatches = false;
  let connected = false;
  let lastVersion = -1;
  let loadTimer = null;

  // ------------------------------------------------------------------ formatting
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const int = (n) => nf.format(n ?? 0);
  const dec = (n) => nf1.format(n ?? 0);
  const pad = (n) => String(n).padStart(2, "0");
  const parseLocal = (s) => new Date(s); // "2026-11-09T20:45:20" (no zone) = local time
  const dayLabel = (d) => `${d.getDate()} ${MONTHS[d.getMonth()]}`;
  const dateLong = (d) => `${d.getDate()} ${MONTHS[d.getMonth()]} ${d.getFullYear()}`;
  const timeHM = (d) => `${pad(d.getHours())}:${pad(d.getMinutes())}`;
  const timeHMS = (d) => `${timeHM(d)}:${pad(d.getSeconds())}`;
  const matchDate = (d) => `${DAYS[d.getDay()]} ${dayLabel(d)} · ${timeHM(d)}`;
  const dur = (s) => `${Math.floor(s / 60)}:${pad(Math.round(s % 60))}`;
  const ymd = (s) => { const [y, m, d] = s.split("-").map(Number); return new Date(y, m - 1, d); };
  const ago = (d) => {
    const mins = Math.max(0, Math.round((Date.now() - d.getTime()) / 60000));
    if (mins < 60) return `${mins} min`;
    const h = Math.floor(mins / 60);
    return `${h} h ${mins % 60} min`;
  };

  // ------------------------------------------------------------------ data
  async function load() {
    try {
      const r = await fetch("/api/snapshot", { cache: "no-store" });
      if (!r.ok) throw new Error(r.status);
      snap = await r.json();
      lastVersion = snap.version;
      render();
      if (!connected) setConn("warn", "Live updates reconnecting…");
    } catch (e) {
      setConn("bad", "Cannot reach the Chest Counter app. Is it running?");
      $("updated").textContent = "App not running";
    }
  }

  function scheduleLoad() {
    clearTimeout(loadTimer);
    loadTimer = setTimeout(load, 250);
  }

  function connect() {
    if (!("EventSource" in window)) return;
    const es = new EventSource("/api/events");
    es.addEventListener("update", (e) => {
      connected = true;
      setConn("ok", "Connected – updates automatically when a replay is processed");
      if (Number(e.data) !== lastVersion) scheduleLoad();
    });
    es.onopen = () => { connected = true; setConn("ok", "Connected – updates automatically when a replay is processed"); };
    es.onerror = () => { connected = false; setConn("bad", "Connection to the app lost – retrying…"); };
  }

  async function post(path) {
    const r = await fetch(path, { method: "POST", headers: { "X-Chest-Counter": "1" } });
    if (!r.ok) throw new Error(r.status);
    return r.json();
  }

  function setConn(state, title) {
    const dot = $("conn");
    dot.className = `dot ${state}`;
    dot.title = title;
    $("refresh").textContent = state === "ok" ? "auto refresh" : state === "warn" ? "connecting…" : "offline";
  }

  // ------------------------------------------------------------------ render
  function render() {
    if (!snap) return;
    const o = snap.overall;
    document.title = `Chest Counter · ${snap.displayName}`;
    $("name").textContent = snap.displayName;
    const gen = new Date(snap.generatedUtc);
    $("updated").textContent = `Updated ${dateLong(gen)} at ${timeHMS(gen)}`;

    renderBanner();
    renderNotices();

    $("c-total").textContent = int(o.totalCredited);
    $("c-total-sub").textContent = `${int(o.opened)} opened · ${int(o.destroyed)} destroyed`;
    $("c-matches").textContent = int(o.matches);
    $("c-matches-sub").innerHTML =
      `<span class="played">${int(o.gamesPlayed)} games played · ${int(o.gamesPlayedToday)} today</span><br>since ${esc(dateLong(ymd(snap.seasonStart)))}`;
    $("c-matches-sub").title =
      `Games played = every game since the season start in any mode (ranked, tournaments, pickaxe-only…). ` +
      `${int(o.matches)} of them count towards your chest stats` +
      (o.pickaxeOnlySkipped ? `; ${int(o.pickaxeOnlySkipped)} pickaxe-only games are skipped (change this in Settings).` : ".");
    $("c-cpg").textContent = dec(o.cpg);
    $("c-cpg-sub").textContent = o.last10Cpg != null ? `last 10: ${dec(o.last10Cpg)}` : "";
    $("c-best").textContent = int(o.bestMatch);
    const best = snap.matches.find((m) => m.id === o.bestMatchId);
    $("c-best-sub").textContent = best ? dayLabel(parseLocal(best.startLocal)) : "";
    $("c-since").textContent = int(o.sinceAppStart);
    $("c-since-sub").textContent = `since ${timeHM(new Date(snap.app.startedUtc))}`;
    $("c-treasures").textContent = int(o.treasures);
    $("c-tr-sub").textContent = o.teamTreasures ? `+${int(o.teamTreasures)} from your team` : "";
    $("c-tr-card").title = o.treasuresNote;

    renderDaily();
    renderTypes();
    renderSplit();
    renderSession();
    renderCpg();
    renderMatches();
    renderSessions();
    renderExcluded();
    renderFooter();
  }

  function renderBanner() {
    const b = $("banner");
    const lib = snap.library;
    if (!lib.accessGranted) {
      b.className = "banner warn";
      b.textContent = "Chest Counter does not have permission to read your Fortnite replay folder yet, so nothing is being counted. Open the Chest Counter app to allow it.";
      b.hidden = false;
    } else if (!lib.folderFound) {
      b.className = "banner bad";
      b.innerHTML = `Replay folder not found: <code>${esc(lib.replayFolder)}</code>. In Fortnite turn on <b>Settings → Game → Replays → Record Replays</b>, or choose the folder in the widget's Settings.`;
      b.hidden = false;
    } else if (lib.busy) {
      const pct = lib.total > 0 ? Math.round((lib.done / lib.total) * 100) : 0;
      b.className = "banner warn";
      b.innerHTML = `<span>Analysing replays ${lib.total > 0 ? `${int(lib.done)} / ${int(lib.total)}` : "…"}</span><span class="bar"><span style="width:${pct}%"></span></span>`;
      b.hidden = false;
    } else if (lib.matchInProgress) {
      b.className = "banner";
      b.innerHTML = `<span class="pill live">● Match in progress</span> Its chests are added a few seconds after the match ends, when Fortnite finalises the replay.`;
      b.hidden = false;
    } else if (lib.lastError) {
      b.className = "banner bad";
      b.textContent = `Last scan reported a problem: ${lib.lastError}`;
      b.hidden = false;
    } else {
      b.hidden = true;
    }
  }

  function renderNotices() {
    const cls = { warn: "banner warn", info: "banner ok", hint: "banner hint" };
    const icon = { warn: "⚠", info: "✓", hint: "ⓘ" };
    $("notices").innerHTML = (snap.notices || []).map((n) =>
      `<div class="${cls[n.level] || "banner"}" data-kind="${esc(n.kind)}"><span class="icon">${icon[n.level] || ""}</span><span>${esc(n.text)}</span></div>`).join("");
  }

  // ------------------------------------------------------------------ tooltip
  const tip = $("tip");
  function showTip(html, ev) {
    tip.innerHTML = html;
    tip.hidden = false;
    const pad = 14;
    let x = ev.clientX + pad;
    let y = ev.clientY + pad;
    const r = tip.getBoundingClientRect();
    if (x + r.width > window.innerWidth - 8) x = ev.clientX - r.width - pad;
    if (y + r.height > window.innerHeight - 8) y = ev.clientY - r.height - pad;
    tip.style.left = `${x}px`;
    tip.style.top = `${y}px`;
  }
  const hideTip = () => { tip.hidden = true; };

  // ------------------------------------------------------------------ charts
  const SVG = "http://www.w3.org/2000/svg";
  function el(name, attrs, parent) {
    const e = document.createElementNS(SVG, name);
    for (const [k, v] of Object.entries(attrs || {})) e.setAttribute(k, v);
    if (parent) parent.appendChild(e);
    return e;
  }

  /** Bar with rounded top (4px) anchored flat on the baseline. */
  function barPath(x, y, w, h, r) {
    r = Math.min(r, w / 2, h);
    return `M${x},${y + h}V${y + r}Q${x},${y} ${x + r},${y}H${x + w - r}Q${x + w},${y} ${x + w},${y + r}V${y + h}Z`;
  }

  function renderDaily() {
    const box = $("daily");
    box.innerHTML = "";
    const data = snap.daily;
    const W = Math.max(280, box.clientWidth - 32);
    const H = 150;
    const top = 18, bottom = 22, plotH = H - top - bottom;
    const max = Math.max(1, ...data.map((d) => d.chests));
    const n = data.length;
    const gap = W > 600 ? 4 : 2;
    const bw = (W - gap * (n - 1)) / n;
    const svg = el("svg", { viewBox: `0 0 ${W} ${H}`, height: H });

    let maxIdx = -1;
    data.forEach((d, i) => { if (d.chests === max && d.chests > 0) maxIdx = i; });

    data.forEach((d, i) => {
      const x = i * (bw + gap);
      if (d.chests > 0) {
        const h = Math.max(4, (d.chests / max) * plotH);
        el("path", { d: barPath(x, top + plotH - h, bw, h, 4), fill: "var(--gold)" }, svg);
        if (i === maxIdx && bw >= 14) {
          const t = el("text", { x: x + bw / 2, y: top + plotH - h - 5, "text-anchor": "middle", class: "axis", fill: "var(--text-2)" }, svg);
          t.textContent = int(d.chests);
        }
      } else {
        el("rect", { x, y: top + plotH - 3, width: bw, height: 3, rx: 1.5, fill: "var(--stub)" }, svg);
      }
      const hit = el("rect", { x: x - gap / 2, y: 0, width: bw + gap, height: H - bottom + 4, class: "hit" }, svg);
      const date = ymd(d.date);
      hit.addEventListener("mousemove", (ev) => showTip(
        `<b>${DAYS[date.getDay()]} ${dateLong(date)}</b><br>${int(d.chests)} chests<br><span class="t-muted">${d.matches} ${d.matches === 1 ? "match" : "matches"}${d.matches ? ` · ${dec(d.chests / d.matches)} per match` : ""}</span>`, ev));
      hit.addEventListener("mouseleave", hideTip);
    });

    const first = el("text", { x: 0, y: H - 4, class: "axis" }, svg);
    first.textContent = dayLabel(ymd(data[0].date));
    const last = el("text", { x: W, y: H - 4, class: "axis", "text-anchor": "end" }, svg);
    last.textContent = "today";
    box.appendChild(svg);
  }

  function niceStep(max, ticks) {
    const raw = max / ticks;
    const mag = Math.pow(10, Math.floor(Math.log10(raw)));
    const norm = raw / mag;
    return (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10) * mag;
  }

  function renderCpg() {
    const box = $("cpg");
    box.innerHTML = "";
    const pts = snap.cpgHistory;
    if (pts.length === 0) {
      box.innerHTML = `<div class="empty">No eligible matches yet.</div>`;
      return;
    }

    const legend = document.createElement("div");
    legend.className = "legend";
    legend.innerHTML = `<span><i style="background:var(--gold)"></i>Average chests per match (all matches)</span>` +
      `<span><i style="background:var(--blue)"></i>Average of the last 10 matches</span>` +
      `<span><i class="box" style="background:#3a3a42"></i>Chests in each match</span>`;
    box.appendChild(legend);

    const W = Math.max(280, box.clientWidth - 32);
    const H = 220;
    const left = 34, right = 44, top = 10, bottom = 22;
    const pw = W - left - right, ph = H - top - bottom;
    const maxV = Math.max(5, ...pts.map((p) => p.credited)) * 1.08;
    const step = niceStep(maxV, 4);
    const yMax = Math.ceil(maxV / step) * step;
    const n = pts.length;
    const xs = (i) => left + (n === 1 ? pw / 2 : (i / (n - 1)) * pw);
    const ys = (v) => top + ph - (v / yMax) * ph;
    const svg = el("svg", { viewBox: `0 0 ${W} ${H}`, height: H });

    for (let v = 0; v <= yMax + 1e-9; v += step) {
      el("line", { x1: left, x2: W - right, y1: ys(v), y2: ys(v), stroke: "var(--grid)", "stroke-width": 1 }, svg);
      const t = el("text", { x: left - 8, y: ys(v) + 4, class: "axis", "text-anchor": "end" }, svg);
      t.textContent = int(v);
    }

    const slot = n === 1 ? pw : pw / (n - 1);
    const bw = Math.max(1.5, Math.min(10, slot * 0.6));
    pts.forEach((p, i) => {
      if (p.credited <= 0) return;
      const h = Math.max(2, (p.credited / yMax) * ph);
      el("path", { d: barPath(xs(i) - bw / 2, top + ph - h, bw, h, Math.min(3, bw / 2)), fill: "#34343b" }, svg);
    });

    const line = (key, color) => {
      const d = pts.map((p, i) => `${i ? "L" : "M"}${xs(i).toFixed(1)},${ys(p[key]).toFixed(1)}`).join("");
      el("path", { d, fill: "none", stroke: color, "stroke-width": 2, "stroke-linejoin": "round", "stroke-linecap": "round" }, svg);
    };
    line("rolling10", "var(--blue)");
    line("cumulative", "var(--gold)");

    // Selective direct labels at the line ends
    const lastP = pts[n - 1];
    const lbl = (v, color, dy) => {
      const t = el("text", { x: xs(n - 1) + 8, y: ys(v) + 4 + dy, class: "axis", fill: "var(--text)" }, svg);
      t.textContent = dec(v);
      el("circle", { cx: xs(n - 1), cy: ys(v), r: 4, fill: color, stroke: "var(--panel)", "stroke-width": 2 }, svg);
    };
    const apart = Math.abs(ys(lastP.cumulative) - ys(lastP.rolling10)) < 12;
    lbl(lastP.rolling10, "var(--blue)", apart ? (lastP.rolling10 >= lastP.cumulative ? -6 : 6) : 0);
    lbl(lastP.cumulative, "var(--gold)", apart ? (lastP.cumulative > lastP.rolling10 ? -6 : 6) : 0);

    const first = el("text", { x: left, y: H - 4, class: "axis" }, svg);
    first.textContent = dayLabel(parseLocal(pts[0].startLocal));
    const lastT = el("text", { x: W - right, y: H - 4, class: "axis", "text-anchor": "end" }, svg);
    lastT.textContent = `match ${n}`;

    // Crosshair + tooltip
    const cross = el("line", { y1: top, y2: top + ph, stroke: "var(--border-strong)", "stroke-width": 1, visibility: "hidden" }, svg);
    const hit = el("rect", { x: left - 6, y: top, width: pw + 12, height: ph, class: "hit" }, svg);
    hit.addEventListener("mousemove", (ev) => {
      const rect = svg.getBoundingClientRect();
      const mx = ((ev.clientX - rect.left) / rect.width) * W;
      const i = n === 1 ? 0 : Math.max(0, Math.min(n - 1, Math.round(((mx - left) / pw) * (n - 1))));
      const p = pts[i];
      cross.setAttribute("x1", xs(i));
      cross.setAttribute("x2", xs(i));
      cross.setAttribute("visibility", "visible");
      const d = parseLocal(p.startLocal);
      showTip(`<b>Match ${p.index}</b> <span class="t-muted">${matchDate(d)}</span><br>${int(p.credited)} chests<br>` +
        `<span class="t-muted">Average so far</span> ${dec(p.cumulative)}<br><span class="t-muted">Last 10 average</span> ${dec(p.rolling10)}`, ev);
    });
    hit.addEventListener("mouseleave", () => { cross.setAttribute("visibility", "hidden"); hideTip(); });
    box.appendChild(svg);
  }

  // ------------------------------------------------------------------ tables & panels
  function renderTypes() {
    const rows = snap.byType.map((r) => {
      const cls = r.isSubRow ? "sub" : !r.inChestTotal ? "extra" : "";
      const label = r.isSubRow ? `↳ ${esc(r.label)}` : esc(r.label);
      return `<tr class="${cls}"><td>${label}</td><td class="r">${int(r.count)}</td></tr>`;
    });
    $("types").innerHTML = rows.join("");
  }

  function renderSplit() {
    const o = snap.overall;
    const total = o.opened + o.destroyed;
    const po = total ? Math.round((o.opened / total) * 100) : 0;
    const pd = total ? 100 - po : 0;
    const bar = total
      ? `<div class="splitbar" role="img" aria-label="${po}% opened, ${pd}% destroyed">` +
        (o.opened ? `<span class="op" style="flex:${o.opened}">${po >= 12 ? `Opened ${po}%` : ""}</span>` : "") +
        (o.destroyed ? `<span class="de" style="flex:${o.destroyed}">${pd >= 12 ? `Destroyed ${pd}%` : ""}</span>` : "") +
        `</div>`
      : `<div class="empty">No chests yet.</div>`;
    $("split").innerHTML = bar +
      `<div class="kv">` +
      `<span class="k"><i class="sw" style="background:var(--gold)"></i>Opened</span><span class="v">${int(o.opened)}</span>` +
      `<span class="k"><i class="sw" style="background:var(--blue)"></i>Destroyed (broken instead of opened)</span><span class="v">${int(o.destroyed)}</span>` +
      `<span class="k subk">↳ with the Gum Gum Devil Fruit equipped</span><span class="v subv">${int(o.gumGumDestroyed)}</span>` +
      `<span class="k subk">↳ other ways (pickaxe, weapons, explosives…)</span><span class="v subv">${int(o.destroyed - o.gumGumDestroyed)}</span>` +
      `<span class="k">Matches where you used the Gum Gum fruit</span><span class="v subv">${int(o.matchesWithGumGum)}</span>` +
      `<span class="k" title="Chests your teammates opened or broke – shown for reference, never added to your total">Teammates' chests <span class="muted">(not counted)</span></span><span class="v subv">${int(o.teamChests)}</span>` +
      `<span class="k" title="Chests destroyed near you that the game did not credit to anyone – never added to your total">Destroyed by nobody on record <span class="muted">(not counted)</span></span><span class="v subv">${int(o.unattributedDestroyed)}</span>` +
      `<span class="k" title="${esc(o.treasuresNote)}">★ Treasures you found</span><span class="v">${int(o.treasures)}</span>` +
      `<span class="k" title="The treasure hunt shares every treasure with the whole team – shown for reference, never added to your total">★ Treasures your teammates found <span class="muted">(shared with you, not counted)</span></span><span class="v subv">${int(o.teamTreasures)}</span>` +
      `</div>` +
      (o.treasureMatchesUnchecked ? `<p class="note">Treasures are counted in ${int(o.matches - o.treasureMatchesUnchecked)} of your ${int(o.matches)} matches. The other ${int(o.treasureMatchesUnchecked)} could not be checked: Fortnite had already deleted their replays before treasures were tracked, or the replay did not record the treasure hunt.</p>` : "");
  }

  function renderSession() {
    const box = $("session");
    const s = snap.activeSession;
    if (s) {
      const start = new Date(s.startUtc);
      box.innerHTML =
        `<div class="session-big">` +
        `<div><div class="n gold">${int(s.chests)}</div><div class="l">chests this session</div></div>` +
        `<div><div class="n">${int(s.matches)}</div><div class="l">${s.matches === 1 ? "match" : "matches"}</div></div>` +
        `<div><div class="n">${s.matches ? dec(s.average) : "–"}</div><div class="l">avg. per match</div></div>` +
        `</div>` +
        `<div class="kv"><span class="k">Last match</span><span class="v">${s.lastMatchChests != null ? `${int(s.lastMatchChests)} chests` : "–"}</span>` +
        `<span class="k">Best match this session</span><span class="v">${s.matches ? int(s.best) : "–"}</span>` +
        `<span class="k">Running since</span><span class="v subv">${timeHM(start)} (${ago(start)})</span></div>` +
        `<div class="session-actions"><span class="pill live">● Session active</span><button class="btn" id="session-btn" type="button">■ Stop session</button></div>`;
    } else {
      const prev = snap.sessions[0];
      box.innerHTML =
        `<p class="note" style="margin-top:0">No session running. A session counts the eligible matches you finish between Start and Stop – it is kept even if you close the app.</p>` +
        (prev ? `<div class="kv"><span class="k">Previous session</span><span class="v">${int(prev.chests)} chests · ${prev.matches} ${prev.matches === 1 ? "match" : "matches"}</span>` +
          `<span class="k">Average</span><span class="v subv">${prev.matches ? dec(prev.average) : "–"}</span></div>` : "") +
        `<div class="session-actions"><span></span><button class="btn primary" id="session-btn" type="button">▶ Start session</button></div>`;
    }
    $("session-btn").addEventListener("click", async (e) => {
      e.target.disabled = true;
      try { await post(s ? "/api/session/stop" : "/api/session/start"); } catch { }
      await load();
    });
  }

  function modePill(mode) {
    return `<span class="pill">${esc(mode)}</span>`;
  }

  function renderMatches() {
    const list = snap.matches;
    const shown = showAllMatches ? list : list.slice(0, 25);
    $("matches").innerHTML = shown.length === 0
      ? `<tr><td colspan="8" class="dimmed">No eligible matches yet. Play a public Battle Royale or Zero Build match with replays turned on.</td></tr>`
      : shown.map((m) => {
        const d = parseLocal(m.startLocal);
        const place = m.placement != null ? `#${m.placement}${m.totalPlayers ? `<span class="dimmed">/${m.totalPlayers}</span>` : ""}` : `<span class="dimmed">–</span>`;
        return `<tr data-id="${m.id}"><td>${matchDate(d)}</td><td>${modePill(m.mode)}</td><td class="r">${place}</td>` +
          `<td class="r">${int(m.opened)}</td><td class="r">${int(m.destroyed)}</td>` +
          `<td class="r ${m.gumGum ? "gold" : "dimmed"}">${m.gumGum ? int(m.gumGum) : "–"}</td>` +
          `<td class="r strong">${int(m.credited)}</td><td class="r">${dur(m.durationSeconds)}</td></tr>`;
      }).join("");
    const more = $("more-matches");
    more.hidden = list.length <= 25;
    more.textContent = showAllMatches ? "Show fewer" : `Show all ${list.length} matches`;
  }

  function renderSessions() {
    const rows = snap.sessions;
    $("sessions").innerHTML = rows.length === 0
      ? `<tr><td colspan="7" class="dimmed">No sessions yet. Use “Start session” on the widget or above.</td></tr>`
      : rows.map((s) => {
        const st = new Date(s.startUtc);
        const en = s.endUtc ? new Date(s.endUtc) : null;
        const ended = en ? (en.toDateString() === st.toDateString() ? timeHM(en) : `${dayLabel(en)} ${timeHM(en)}`) : `<span class="pill live">active</span>`;
        return `<tr><td>${DAYS[st.getDay()]} ${dayLabel(st)} · ${timeHM(st)}</td><td>${ended}</td><td class="r">${s.matches}</td>` +
          `<td class="r strong">${int(s.chests)}</td><td class="r">${s.matches ? dec(s.average) : "–"}</td><td class="r">${s.matches ? int(s.best) : "–"}</td>` +
          `<td class="r">${s.lastMatchChests != null ? int(s.lastMatchChests) : "–"}</td></tr>`;
      }).join("");
  }

  function renderExcluded() {
    const rows = snap.excluded;
    $("excluded-count").textContent = rows.length ? `(${rows.length})` : "(none)";
    $("excluded").innerHTML = rows.length === 0
      ? `<tr><td colspan="5" class="dimmed">Nothing excluded.</td></tr>`
      : rows.map((x) => {
        const d = x.startLocal ? parseLocal(x.startLocal) : null;
        const bad = ["Unreadable", "Invalid"].includes(x.category);
        return `<tr><td>${d ? matchDate(d) : "–"}</td><td class="dimmed">${esc(x.fileName)}</td>` +
          `<td><span class="pill ${bad ? "bad" : ""}">${esc(x.category)}</span></td><td>${esc(x.mode)}</td><td>${esc(x.reason)}</td></tr>`;
      }).join("");
  }

  function renderFooter() {
    const lib = snap.library;
    const last = lib.lastScanUtc ? new Date(lib.lastScanUtc) : null;
    $("foot").innerHTML =
      `<b>Season start</b> ${esc(dateLong(ymd(snap.seasonStart)))} · <b>Replay folder</b> ${esc(lib.replayFolder || "–")} (${int(lib.replayFiles)} files) · ` +
      `<b>Status</b> ${esc(lib.phase)}${last ? ` (checked ${timeHMS(last)})` : ""}<br>` +
      `<b>Account</b> ${esc(lib.account || "auto")} · <b>Stored matches</b> ${int(lib.analyzedMatches)} · <b>Data</b> ${esc(snap.app.dataFolder)} · Chest Counter ${esc(snap.app.version)} · reads replay files only`;
  }

  // ------------------------------------------------------------------ match modal
  const KIND = {
    NormalChest: "Chest", RareChest: "Rare chest", BuriedChest: "Buried chest", ExtractionRelicChest: "Extraction / relic chest",
    SpritePosterChest: "Sprite poster chest", OtherChest: "Other chest", AmmoBox: "Ammo box", FoodBox: "Food box",
    LootCannon: "Loot cannon", OtherContainer: "Other container",
  };
  const isChest = (k) => ["NormalChest", "RareChest", "BuriedChest", "ExtractionRelicChest", "SpritePosterChest", "OtherChest"].includes(k);

  async function openMatch(id) {
    let d;
    try {
      const r = await fetch(`/api/match/${id}`, { cache: "no-store" });
      d = await r.json();
    } catch { return; }
    const m = d.match, v = d.view || {};
    const start = parseLocal(m.startLocal);
    const ev = d.events;
    const rows = ev.map((e) => {
      const t = `${Math.floor(e.time / 60)}:${pad(Math.floor(e.time % 60))}`;
      const chest = isChest(e.kind);
      return `<tr class="${chest ? "" : "dimmed"}"><td>${t}</td><td>${esc(KIND[e.kind] || e.kind)}${chest ? "" : " <span class='dimmed'>(not a chest)</span>"}</td>` +
        `<td>${e.action === "Opened" ? "Opened" : "Destroyed"}</td><td>${e.gumGum ? `<span class="pill gold">Gum Gum</span>` : ""}</td>` +
        `<td class="dimmed" title="${esc(e.className)}">${esc(e.className.replace(/_C$/, ""))}</td></tr>`;
    }).join("");
    $("modal-body").innerHTML =
      `<h3 id="modal-title">${matchDate(start)}</h3>` +
      `<div class="muted">${esc(d.verdict.mode)} · ${esc(m.playlist || "unknown playlist")} · ${dur(m.durationSeconds)}` +
      `${m.placement != null ? ` · placed #${m.placement}${m.totalPlayers ? `/${m.totalPlayers}` : ""}` : ""}${m.eliminations != null ? ` · ${m.eliminations} elims` : ""}</div>` +
      `${d.verdict.counted ? "" : `<p class="banner bad" style="margin:12px 0 0">Not counted: ${esc(d.verdict.reason)}</p>`}` +
      `<div class="mini-cards"><div class="g"><b>${int(m.opened + m.destroyed)}</b><span>chests</span></div><div><b>${int(m.opened)}</b><span>opened</span></div>` +
      `<div><b>${int(m.destroyed)}</b><span>destroyed</span></div><div><b>${int(m.gumGumDestroyed)}</b><span>with Gum Gum</span></div></div>` +
      `<div class="kv" style="margin-bottom:14px">` +
      `<span class="k">Chest / rare / buried / relic / sprite poster</span><span class="v">${m.normal} / ${m.rare} / ${m.buried} / ${m.relic} / ${m.sprite}${m.otherChest ? ` / other ${m.otherChest}` : ""}</span>` +
      `<span class="k">Ammo boxes · food boxes · loot cannons <span class="muted">(not chests)</span></span><span class="v subv">${m.ammo} · ${m.food} · ${m.cannon}</span>` +
      `<span class="k">Teammates' chests <span class="muted">(not counted)</span></span><span class="v subv">${m.teamChests}</span>` +
      `<span class="k">Destroyed by nobody on record <span class="muted">(not counted)</span></span><span class="v subv">${m.unattributedDestroyed}${m.unattributedDuringGumGum ? ` (${m.unattributedDuringGumGum} while you held the fruit)` : ""}</span>` +
      (m.treasuresKnown
        ? `<span class="k">★ Treasures you found · from teammates</span><span class="v">${m.treasures} <span class="subv">· ${m.teamTreasures}</span></span>`
        : `<span class="k">★ Treasures</span><span class="v subv">could not be checked for this match</span>`) +
      `</div>` +
      (rows ? `<div class="events"><table class="list"><thead><tr><th>Time</th><th>Type</th><th>Action</th><th></th><th>Blueprint</th></tr></thead><tbody>${rows}</tbody></table></div>` : `<p class="note">No container interactions credited to you in this match.</p>`) +
      (m.notes ? `<p class="note">${esc(m.notes)}</p>` : "") +
      `<p class="note">Replay: ${esc(v.fileName || m.filePath)}${v.fileMissing ? " (already deleted by Fortnite – results are kept)" : ""}</p>`;
    $("modal").hidden = false;
    $("modal-close").focus();
  }

  function closeModal() { $("modal").hidden = true; }

  // ------------------------------------------------------------------ wiring
  $("matches").addEventListener("click", (e) => {
    const tr = e.target.closest("tr[data-id]");
    if (tr) openMatch(Number(tr.dataset.id));
  });
  $("more-matches").addEventListener("click", () => { showAllMatches = !showAllMatches; renderMatches(); });
  $("modal-close").addEventListener("click", closeModal);
  $("modal").addEventListener("click", (e) => { if (e.target.id === "modal") closeModal(); });
  document.addEventListener("keydown", (e) => { if (e.key === "Escape") closeModal(); });

  let resizeTimer = null;
  window.addEventListener("resize", () => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => { if (snap) { renderDaily(); renderCpg(); } }, 150);
  });

  // Fallback refresh (time-based session values, or if live updates are unavailable)
  setInterval(() => { if (!connected || document.visibilityState === "visible") load(); }, 30000);

  load();
  connect();
})();
