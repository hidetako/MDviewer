/*
 * render.js — Markdown → HTML 変換の純粋ロジック。
 * ブラウザ (WebView2) と Node (tests/) の両方から読める UMD 風の書き方にしている。
 *
 *   createRenderer({ markdownit, hljs, plugins }) → { render(text, opts) }
 *     render() は { html, toc, frontMatter, hasMermaid } を返す。
 *
 * サニタイズ (DOMPurify) はこのモジュールの責務外。app.js 側で DOM に挿す直前に行う。
 */
(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory();
  } else {
    root.MdRender = factory();
  }
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  var DOC_HOST = "https://doc.mdviewer/";

  function escapeHtml(s) {
    return String(s)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  // 見出し ID: 日本語を含む文字をそのまま残し、空白を '-' に、記号を落とす
  function slugify(text) {
    var s = String(text)
      .trim()
      .toLowerCase()
      .replace(/<[^>]+>/g, "")
      .replace(/[`*_~\[\]\(\)!#$%^&+={}|;:'",.<>?/\\]/g, "")
      .replace(/\s+/g, "-")
      .replace(/-+/g, "-")
      .replace(/^-|-$/g, "");
    return s || "section";
  }

  function isExternalUrl(href) {
    return /^[a-z][a-z0-9+.-]*:/i.test(href) || href.indexOf("//") === 0;
  }

  // 相対パス → doc.mdviewer 仮想ホスト上の URL
  function resolveDocUrl(href) {
    if (!href || isExternalUrl(href) || href.charAt(0) === "#") return href;
    var h = href.replace(/\\|%5[Cc]/g, "/"); // markdown-it は "\\" を %5C に正規化する
    if (h.charAt(0) === "/") h = h.slice(1);
    while (h.indexOf("./") === 0) h = h.slice(2);
    return DOC_HOST + h;
  }

  // 先頭の YAML front matter を切り出す ("---" で始まり "---" / "..." で終わる)
  function splitFrontMatter(text) {
    var m = /^(?:﻿)?---[ \t]*\r?\n([\s\S]*?)\r?\n(?:---|\.\.\.)[ \t]*(?:\r?\n|$)/.exec(text);
    if (!m) return { frontMatter: null, body: text };
    var fm = {};
    m[1].split(/\r?\n/).forEach(function (line) {
      var kv = /^([A-Za-z0-9_\-\s　-鿿]+?)\s*:\s*(.*)$/.exec(line);
      if (kv) fm[kv[1].trim()] = kv[2].trim().replace(/^["']|["']$/g, "");
    });
    return { frontMatter: fm, body: text.slice(m[0].length) };
  }

  function renderFrontMatter(fm) {
    var keys = Object.keys(fm);
    if (!keys.length) return "";
    var rows = keys
      .map(function (k) {
        return "<tr><th>" + escapeHtml(k) + "</th><td>" + escapeHtml(fm[k]) + "</td></tr>";
      })
      .join("");
    return (
      '<details class="front-matter"><summary>Front matter (' +
      keys.length +
      ")</summary><table>" +
      rows +
      "</table></details>"
    );
  }

  function createRenderer(deps) {
    var markdownit = deps.markdownit;
    var hljs = deps.hljs || null;
    var plugins = deps.plugins || [];

    var md = markdownit({
      html: true,
      linkify: true,
      typographer: false,
      breaks: false,
      highlight: function (code, lang) {
        var l = (lang || "").trim().toLowerCase();
        if (l === "mermaid") {
          // mermaid はコードとして着色せず、mermaid.run() に拾わせる
          return '<pre class="mermaid">' + escapeHtml(code) + "</pre>";
        }
        if (hljs && l && hljs.getLanguage(l)) {
          try {
            return (
              '<pre class="hljs"><code class="language-' +
              escapeHtml(l) +
              '">' +
              hljs.highlight(code, { language: l, ignoreIllegals: true }).value +
              "</code></pre>"
            );
          } catch (e) {
            /* fall through */
          }
        }
        return '<pre class="hljs"><code>' + escapeHtml(code) + "</code></pre>";
      },
    });

    plugins.forEach(function (p) {
      if (Array.isArray(p)) md.use.apply(md, p);
      else if (p) md.use(p);
    });

    // --- 見出し: id 付与 + TOC 収集 ---
    md.core.ruler.push("md_viewer_headings", function (state) {
      var used = {};
      var toc = [];
      var tokens = state.tokens;
      for (var i = 0; i < tokens.length; i++) {
        var t = tokens[i];
        if (t.type !== "heading_open") continue;
        var inline = tokens[i + 1];
        var text = inline && inline.children
          ? inline.children
              .filter(function (c) {
                return c.type === "text" || c.type === "code_inline";
              })
              .map(function (c) {
                return c.content;
              })
              .join("")
          : "";
        var base = slugify(text);
        var id = base;
        var n = 1;
        while (used[id]) id = base + "-" + ++n;
        used[id] = true;
        t.attrSet("id", id);
        toc.push({ level: parseInt(t.tag.slice(1), 10), id: id, text: text });
      }
      state.env.toc = toc;
    });

    // --- 画像: 相対パスを doc.mdviewer に向ける ---
    var defaultImage = md.renderer.rules.image;
    md.renderer.rules.image = function (tokens, idx, options, env, self) {
      var tok = tokens[idx];
      var src = tok.attrGet("src");
      if (src) tok.attrSet("src", resolveDocUrl(src));
      tok.attrSet("loading", "lazy");
      return defaultImage(tokens, idx, options, env, self);
    };

    // --- リンク: 外部は新規ウィンドウ扱い (app.js 側で OS ブラウザへ)、相対 .md は viewer 内で開く ---
    var defaultLinkOpen =
      md.renderer.rules.link_open ||
      function (tokens, idx, options, env, self) {
        return self.renderToken(tokens, idx, options);
      };
    md.renderer.rules.link_open = function (tokens, idx, options, env, self) {
      var tok = tokens[idx];
      var href = tok.attrGet("href") || "";
      if (href.charAt(0) === "#") {
        tok.attrSet("data-anchor", "1");
      } else if (isExternalUrl(href)) {
        tok.attrSet("data-external", "1");
        tok.attrSet("rel", "noopener noreferrer");
      } else {
        tok.attrSet("data-relative", href);
      }
      return defaultLinkOpen(tokens, idx, options, env, self);
    };

    // --- テーブル: 横スクロール用ラッパ ---
    md.renderer.rules.table_open = function () {
      return '<div class="table-wrap"><table>';
    };
    md.renderer.rules.table_close = function () {
      return "</table></div>";
    };

    function render(text) {
      var src = String(text || "").replace(/^﻿/, "");
      var split = splitFrontMatter(src);
      var env = {};
      var body = md.render(split.body, env);
      var html = (split.frontMatter ? renderFrontMatter(split.frontMatter) : "") + body;
      return {
        html: html,
        toc: env.toc || [],
        frontMatter: split.frontMatter,
        hasMermaid: html.indexOf('<pre class="mermaid">') !== -1,
      };
    }

    return { render: render, md: md };
  }

  return {
    createRenderer: createRenderer,
    slugify: slugify,
    resolveDocUrl: resolveDocUrl,
    splitFrontMatter: splitFrontMatter,
    escapeHtml: escapeHtml,
    DOC_HOST: DOC_HOST,
  };
});
