/*
 * app.js — WebView2 側の UI ロジック。
 *
 * ホスト (C#) との通信:
 *   Web → Host : window.chrome.webview.postMessage(JSON 文字列)
 *   Host → Web : window.chrome.webview.addEventListener("message", e => e.data)
 *
 * ホストが居ない (普通のブラウザで index.html を開いた) 場合もドラッグ&ドロップで
 * 最低限動くようにしてある (開発時のプレビュー用途)。
 */
(function () {
  "use strict";

  var $ = function (sel) { return document.querySelector(sel); };
  var host = window.chrome && window.chrome.webview ? window.chrome.webview : null;

  var state = {
    path: null,
    dir: null,
    text: "",
    theme: "light",
    zoom: 1,
    tocVisible: true,
    recent: [],
    version: "",
    toc: [],
  };

  var ZOOM_MIN = 0.6, ZOOM_MAX = 2.2, ZOOM_STEP = 0.1;

  // ---------------------------------------------------------------- renderer
  var renderer = window.MdRender.createRenderer({
    markdownit: window.markdownit,
    hljs: window.hljs,
    plugins: [
      window.markdownitTaskLists ? [window.markdownitTaskLists, { enabled: false, label: true }] : null,
      window.markdownitFootnote || null,
    ],
  });

  if (window.mermaid) {
    window.mermaid.initialize({ startOnLoad: false, securityLevel: "strict", theme: "default" });
  }

  // ---------------------------------------------------------------- host I/O
  function send(msg) {
    if (host) host.postMessage(JSON.stringify(msg));
  }

  function saveSettings() {
    send({
      type: "settings",
      settings: { theme: state.theme, zoom: state.zoom, tocVisible: state.tocVisible },
    });
  }

  if (host) {
    host.addEventListener("message", function (e) {
      var msg = typeof e.data === "string" ? safeParse(e.data) : e.data;
      if (!msg || !msg.type) return;
      switch (msg.type) {
        case "init":
          applySettings(msg.settings || {});
          state.version = msg.version || "";
          break;
        case "load":
          loadDocument(msg);
          break;
        case "recent":
          state.recent = msg.recent || [];
          break;
        case "error":
          showError(msg.message || "エラーが発生しました");
          break;
        case "toast":
          toast(msg.message || "");
          break;
      }
    });
  }

  function safeParse(s) {
    try { return JSON.parse(s); } catch (e) { return null; }
  }

  // ---------------------------------------------------------------- settings
  function applySettings(s) {
    if (s.theme === "dark" || s.theme === "light") setTheme(s.theme, true);
    if (typeof s.zoom === "number" && s.zoom >= ZOOM_MIN && s.zoom <= ZOOM_MAX) setZoom(s.zoom, true);
    if (typeof s.tocVisible === "boolean") setTocVisible(s.tocVisible, true);
    if (Array.isArray(s.recent)) state.recent = s.recent;
  }

  function setTheme(theme, silent) {
    state.theme = theme;
    document.body.setAttribute("data-theme", theme);
    var dark = theme === "dark";
    $("#css-md-light").disabled = dark;
    $("#css-hljs-light").disabled = dark;
    $("#css-md-dark").disabled = !dark;
    $("#css-hljs-dark").disabled = !dark;
    if (window.mermaid) {
      window.mermaid.initialize({ startOnLoad: false, securityLevel: "strict", theme: dark ? "dark" : "default" });
      if (state.text) renderCurrent(true); // mermaid の色を更新
    }
    if (!silent) saveSettings();
  }

  function setZoom(z, silent) {
    z = Math.round(Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, z)) * 100) / 100;
    state.zoom = z;
    document.documentElement.style.setProperty("--doc-scale", String(z));
    $("#zoom-label").textContent = Math.round(z * 100) + "%";
    if (!silent) saveSettings();
  }

  function setTocVisible(v, silent) {
    state.tocVisible = v;
    document.body.classList.toggle("toc-hidden", !v);
    $("#btn-toc").classList.toggle("active", v);
    if (!silent) saveSettings();
  }

  // ---------------------------------------------------------------- document
  function loadDocument(msg) {
    state.path = msg.path || null;
    state.dir = msg.dir || null;
    state.text = msg.text || "";
    var label = $("#file-label");
    label.textContent = msg.path || msg.name || "";
    label.title = (msg.path || "") + (msg.encoding ? "  [" + msg.encoding + "]" : "");
    document.title = (msg.name || "MD Viewer") + " — MD Viewer";
    renderCurrent(!!msg.keepScroll);
    if (msg.keepScroll) toast("再読み込みしました");
    else if (msg.anchor) scrollToId(msg.anchor);
    else $("#content").focus();
  }

  function renderCurrent(keepScroll) {
    var content = $("#content");
    var prevScroll = keepScroll ? content.scrollTop : 0;
    var result;
    try {
      result = renderer.render(state.text);
    } catch (e) {
      showError("Markdown の変換に失敗しました:\n" + (e && e.message ? e.message : e));
      return;
    }
    var clean = window.DOMPurify
      ? window.DOMPurify.sanitize(result.html, {
          ADD_ATTR: ["data-anchor", "data-external", "data-relative", "loading"],
          FORBID_TAGS: ["style", "script", "iframe", "object", "embed"],
        })
      : result.html;
    var doc = $("#doc");
    doc.innerHTML = clean;
    state.toc = result.toc;
    buildToc(result.toc);
    clearFind(false);
    if (result.hasMermaid && window.mermaid) {
      window.mermaid
        .run({ nodes: doc.querySelectorAll("pre.mermaid") })
        .catch(function (e) { console.warn("mermaid", e); });
    }
    content.scrollTop = prevScroll;
    updateTocCurrent();
    if (findState.open && $("#find-input").value) runFind($("#find-input").value);
  }

  function showError(message) {
    $("#doc").innerHTML = '<div class="error-state"></div>';
    $("#doc .error-state").textContent = message;
    $("#toc").innerHTML = "";
  }

  function buildToc(toc) {
    var nav = $("#toc");
    nav.innerHTML = "";
    if (!toc.length) {
      var p = document.createElement("div");
      p.className = "toc-empty";
      p.textContent = "見出しがありません";
      nav.appendChild(p);
      return;
    }
    var minLevel = Math.min.apply(null, toc.map(function (t) { return t.level; }));
    toc.forEach(function (t) {
      var a = document.createElement("a");
      a.href = "#" + t.id;
      a.textContent = t.text || "(無題)";
      a.title = t.text;
      a.className = "lv" + Math.min(6, t.level - minLevel + 1);
      a.setAttribute("data-target", t.id);
      a.addEventListener("click", function (ev) {
        ev.preventDefault();
        scrollToId(t.id);
      });
      nav.appendChild(a);
    });
  }

  function scrollToId(id) {
    var el = document.getElementById(id);
    if (el) el.scrollIntoView({ block: "start" });
  }

  function updateTocCurrent() {
    if (!state.toc.length) return;
    var content = $("#content");
    var top = content.scrollTop + 20;
    var currentId = state.toc[0].id;
    for (var i = 0; i < state.toc.length; i++) {
      var el = document.getElementById(state.toc[i].id);
      if (!el) continue;
      if (el.offsetTop <= top) currentId = state.toc[i].id;
      else break;
    }
    var links = document.querySelectorAll("#toc a");
    for (var j = 0; j < links.length; j++) {
      var isCur = links[j].getAttribute("data-target") === currentId;
      links[j].classList.toggle("current", isCur);
      if (isCur && links[j].scrollIntoViewIfNeeded) links[j].scrollIntoViewIfNeeded(false);
    }
  }

  // ---------------------------------------------------------------- links
  $("#doc").addEventListener("click", function (ev) {
    var a = ev.target.closest ? ev.target.closest("a") : null;
    if (!a) return;
    var href = a.getAttribute("href") || "";
    if (a.hasAttribute("data-anchor") || href.charAt(0) === "#") {
      ev.preventDefault();
      var id = decodeURIComponent(href.slice(1));
      scrollToId(id) ;
      return;
    }
    if (a.hasAttribute("data-external")) {
      ev.preventDefault();
      if (host) send({ type: "openExternal", url: href });
      else window.open(href, "_blank", "noopener");
      return;
    }
    if (a.hasAttribute("data-relative")) {
      ev.preventDefault();
      if (host) send({ type: "openRelative", href: a.getAttribute("data-relative") });
      else toast("相対リンクはアプリ内でのみ開けます: " + href);
    }
  });

  // ---------------------------------------------------------------- toolbar
  $("#btn-open").addEventListener("click", function () { send({ type: "open" }); });
  $("#btn-reload").addEventListener("click", function () { send({ type: "reload" }); });
  $("#btn-toc").addEventListener("click", function () { setTocVisible(!state.tocVisible); });
  $("#btn-theme").addEventListener("click", function () { setTheme(state.theme === "dark" ? "light" : "dark"); });
  $("#btn-zoom-in").addEventListener("click", function () { setZoom(state.zoom + ZOOM_STEP); });
  $("#btn-zoom-out").addEventListener("click", function () { setZoom(state.zoom - ZOOM_STEP); });
  $("#zoom-label").addEventListener("click", function () { setZoom(1); });
  $("#btn-print").addEventListener("click", function () { if (host) send({ type: "print" }); else window.print(); });
  $("#btn-pdf").addEventListener("click", function () { send({ type: "pdf" }); });
  $("#btn-find").addEventListener("click", function () { openFind(); });

  // 「最近」メニュー
  var recentBtn = $("#btn-recent"), recentMenu = $("#recent-menu");
  recentBtn.addEventListener("click", function (ev) {
    ev.stopPropagation();
    closeMenus(recentMenu);
    if (!recentMenu.hidden) { recentMenu.hidden = true; return; }
    recentMenu.innerHTML = "";
    if (!state.recent.length) {
      var li = document.createElement("li");
      li.className = "empty";
      li.textContent = "履歴はありません";
      recentMenu.appendChild(li);
    } else {
      state.recent.forEach(function (p) {
        var li = document.createElement("li");
        var b = document.createElement("button");
        b.textContent = p;
        b.title = p;
        b.addEventListener("click", function () {
          recentMenu.hidden = true;
          send({ type: "openPath", path: p });
        });
        li.appendChild(b);
        recentMenu.appendChild(li);
      });
      var div = document.createElement("li"); div.className = "divider"; recentMenu.appendChild(div);
      var clr = document.createElement("li");
      var cb = document.createElement("button");
      cb.textContent = "履歴を消去";
      cb.addEventListener("click", function () { recentMenu.hidden = true; send({ type: "clearRecent" }); });
      clr.appendChild(cb); recentMenu.appendChild(clr);
    }
    recentMenu.hidden = false;
  });

  // 「⋯」メニュー
  var moreBtn = $("#btn-more"), moreMenu = $("#more-menu");
  moreBtn.addEventListener("click", function (ev) {
    ev.stopPropagation();
    closeMenus(moreMenu);
    moreMenu.hidden = !moreMenu.hidden;
  });
  moreMenu.addEventListener("click", function (ev) {
    var b = ev.target.closest("button[data-action]");
    if (!b) return;
    moreMenu.hidden = true;
    var action = b.getAttribute("data-action");
    switch (action) {
      case "copy-html":
        copyText($("#doc").innerHTML, "HTML をコピーしました");
        break;
      case "open-folder": send({ type: "openFolder" }); break;
      case "register": send({ type: "register" }); break;
      case "unregister": send({ type: "unregister" }); break;
      case "about":
        toast("MD Viewer " + (state.version || "") + " — Markdown を読みやすく表示するスタンドアロンビューア");
        break;
    }
  });

  function closeMenus(except) {
    [recentMenu, moreMenu].forEach(function (m) { if (m !== except) m.hidden = true; });
  }
  document.addEventListener("click", function () { closeMenus(null); });

  function copyText(text, okMsg) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(
        function () { toast(okMsg); },
        function () { send({ type: "clipboard", text: text }); }
      );
    } else {
      send({ type: "clipboard", text: text });
    }
  }

  // ---------------------------------------------------------------- find
  var findState = { open: false, hits: [], index: -1 };

  function openFind() {
    findState.open = true;
    $("#find-bar").hidden = false;
    var input = $("#find-input");
    input.focus();
    input.select();
    if (input.value) runFind(input.value);
  }

  function closeFind() {
    findState.open = false;
    $("#find-bar").hidden = true;
    clearFind(true);
    $("#content").focus();
  }

  function clearFind(resetCount) {
    var marks = document.querySelectorAll("#doc mark.find-hit");
    for (var i = 0; i < marks.length; i++) {
      var m = marks[i];
      var parent = m.parentNode;
      parent.replaceChild(document.createTextNode(m.textContent), m);
      parent.normalize();
    }
    findState.hits = [];
    findState.index = -1;
    if (resetCount) $("#find-count").textContent = "";
  }

  function runFind(query) {
    clearFind(true);
    var q = query.trim().toLowerCase();
    if (!q) return;
    var walker = document.createTreeWalker($("#doc"), NodeFilter.SHOW_TEXT, {
      acceptNode: function (node) {
        var p = node.parentNode;
        if (!p || /^(SCRIPT|STYLE|MARK)$/.test(p.nodeName)) return NodeFilter.FILTER_REJECT;
        return node.nodeValue.toLowerCase().indexOf(q) !== -1 ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_SKIP;
      },
    });
    var nodes = [];
    while (walker.nextNode()) nodes.push(walker.currentNode);
    nodes.forEach(function (node) {
      var text = node.nodeValue, lower = text.toLowerCase();
      var frag = document.createDocumentFragment();
      var pos = 0, idx;
      while ((idx = lower.indexOf(q, pos)) !== -1) {
        if (idx > pos) frag.appendChild(document.createTextNode(text.slice(pos, idx)));
        var mark = document.createElement("mark");
        mark.className = "find-hit";
        mark.textContent = text.slice(idx, idx + q.length);
        frag.appendChild(mark);
        findState.hits.push(mark);
        pos = idx + q.length;
      }
      if (pos < text.length) frag.appendChild(document.createTextNode(text.slice(pos)));
      node.parentNode.replaceChild(frag, node);
    });
    if (findState.hits.length) gotoHit(0);
    else $("#find-count").textContent = "0 件";
  }

  function gotoHit(i) {
    var n = findState.hits.length;
    if (!n) return;
    i = ((i % n) + n) % n;
    if (findState.index >= 0 && findState.hits[findState.index]) findState.hits[findState.index].classList.remove("current");
    findState.index = i;
    var m = findState.hits[i];
    m.classList.add("current");
    m.scrollIntoView({ block: "center" });
    $("#find-count").textContent = (i + 1) + " / " + n + " 件";
  }

  var findTimer = null;
  $("#find-input").addEventListener("input", function (ev) {
    clearTimeout(findTimer);
    var v = ev.target.value;
    findTimer = setTimeout(function () { runFind(v); }, 120);
  });
  $("#find-input").addEventListener("keydown", function (ev) {
    if (ev.key === "Enter") { ev.preventDefault(); gotoHit(findState.index + (ev.shiftKey ? -1 : 1)); }
    else if (ev.key === "Escape") { ev.preventDefault(); closeFind(); }
  });
  $("#find-next").addEventListener("click", function () { gotoHit(findState.index + 1); });
  $("#find-prev").addEventListener("click", function () { gotoHit(findState.index - 1); });
  $("#find-close").addEventListener("click", closeFind);

  // ---------------------------------------------------------------- keyboard
  document.addEventListener("keydown", function (ev) {
    var ctrl = ev.ctrlKey || ev.metaKey;
    if (ev.key === "F5") { ev.preventDefault(); send({ type: "reload" }); return; }
    if (ev.key === "Escape") { closeMenus(null); if (findState.open) closeFind(); return; }
    if (!ctrl) return;
    switch (ev.key) {
      case "o": case "O": ev.preventDefault(); send({ type: "open" }); break;
      case "r": case "R": ev.preventDefault(); send({ type: "reload" }); break;
      case "b": case "B": ev.preventDefault(); setTocVisible(!state.tocVisible); break;
      case "d": case "D": ev.preventDefault(); setTheme(state.theme === "dark" ? "light" : "dark"); break;
      case "f": case "F": ev.preventDefault(); openFind(); break;
      case "p": case "P": ev.preventDefault(); if (host) send({ type: "print" }); else window.print(); break;
      case "+": case "=": case ";": ev.preventDefault(); setZoom(state.zoom + ZOOM_STEP); break;
      case "-": ev.preventDefault(); setZoom(state.zoom - ZOOM_STEP); break;
      case "0": ev.preventDefault(); setZoom(1); break;
    }
  });

  // Ctrl + ホイールで文字サイズ
  $("#content").addEventListener("wheel", function (ev) {
    if (!ev.ctrlKey) return;
    ev.preventDefault();
    setZoom(state.zoom + (ev.deltaY < 0 ? ZOOM_STEP : -ZOOM_STEP));
  }, { passive: false });

  var scrollTimer = null;
  $("#content").addEventListener("scroll", function () {
    if (scrollTimer) return;
    scrollTimer = setTimeout(function () { scrollTimer = null; updateTocCurrent(); }, 80);
  });

  // ---------------------------------------------------------------- drag & drop (ホスト不在時のフォールバック)
  var dragDepth = 0;
  document.addEventListener("dragenter", function (ev) { ev.preventDefault(); dragDepth++; $("#drop-overlay").hidden = false; });
  document.addEventListener("dragover", function (ev) { ev.preventDefault(); });
  document.addEventListener("dragleave", function () { if (--dragDepth <= 0) { dragDepth = 0; $("#drop-overlay").hidden = true; } });
  document.addEventListener("drop", function (ev) {
    ev.preventDefault();
    dragDepth = 0;
    $("#drop-overlay").hidden = true;
    var f = ev.dataTransfer && ev.dataTransfer.files && ev.dataTransfer.files[0];
    if (!f) return;
    var reader = new FileReader();
    reader.onload = function () {
      loadDocument({ name: f.name, path: f.name, text: String(reader.result || "") });
    };
    reader.readAsText(f, "utf-8");
  });

  // ---------------------------------------------------------------- toast
  var toastTimer = null;
  function toast(msg) {
    var el = $("#toast");
    el.textContent = msg;
    el.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { el.hidden = true; }, 2200);
  }

  // ---------------------------------------------------------------- boot
  setTocVisible(true, true);
  setZoom(1, true);
  send({ type: "ready" });
})();
