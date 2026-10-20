// render.js の単体テスト (Node 標準 node:test のみ使用、追加依存なし)
//   実行: node --test tools/md-viewer/tests/
import { test } from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import path from "node:path";

const require = createRequire(import.meta.url);
const here = path.dirname(fileURLToPath(import.meta.url));
const assets = path.join(here, "..", "MdViewer", "Assets");

const MdRender = require(path.join(assets, "render.js"));
const markdownit = require(path.join(assets, "vendor", "markdown-it.min.js"));
const hljs = require(path.join(assets, "vendor", "highlight.min.js"));
const taskLists = require(path.join(assets, "vendor", "markdown-it-task-lists.min.js"));
const footnote = require(path.join(assets, "vendor", "markdown-it-footnote.min.js"));

const renderer = MdRender.createRenderer({
  markdownit,
  hljs,
  plugins: [[taskLists, { enabled: false, label: true }], footnote],
});

test("見出しに id が付き TOC が得られる (日本語見出し・重複)", () => {
  const r = renderer.render("# はじめに\n\n## 手順\n\n## 手順\n\n### 注意 点\n");
  assert.equal(r.toc.length, 4);
  assert.deepEqual(r.toc.map((t) => t.level), [1, 2, 2, 3]);
  assert.equal(r.toc[0].id, "はじめに");
  assert.equal(r.toc[1].id, "手順");
  assert.equal(r.toc[2].id, "手順-2");
  assert.equal(r.toc[3].id, "注意-点");
  assert.match(r.html, /<h1 id="はじめに">はじめに<\/h1>/);
});

test("コードブロックがハイライトされる", () => {
  const r = renderer.render("```js\nconst a = 1;\n```\n");
  assert.match(r.html, /<pre class="hljs"><code class="language-js">/);
  assert.match(r.html, /hljs-keyword/);
});

test("未知の言語はエスケープのみ", () => {
  const r = renderer.render("```nosuchlang\n<b>x</b>\n```\n");
  assert.match(r.html, /&lt;b&gt;x&lt;\/b&gt;/);
  assert.doesNotMatch(r.html, /<b>x<\/b>/);
});

test("mermaid ブロックは pre.mermaid になる", () => {
  const r = renderer.render("```mermaid\ngraph TD; A-->B;\n```\n");
  assert.equal(r.hasMermaid, true);
  assert.match(r.html, /<pre class="mermaid">graph TD; A--&gt;B;\n<\/pre>/);
});

test("相対画像パスは doc.mdviewer 仮想ホストに向く", () => {
  const r = renderer.render("![a](./img/a.png) ![b](img\\b.png) ![c](https://x.test/c.png) ![d](data:image/png;base64,AAAA)");
  assert.match(r.html, /src="https:\/\/doc\.mdviewer\/img\/a\.png"/);
  assert.match(r.html, /src="https:\/\/doc\.mdviewer\/img\/b\.png"/);
  assert.match(r.html, /src="https:\/\/x\.test\/c\.png"/);
  assert.match(r.html, /src="data:image\/png;base64,AAAA"/);
});

test("リンク種別が data 属性で区別される", () => {
  const r = renderer.render("[ext](https://example.com) [rel](other.md#sec) [anc](#top)");
  assert.match(r.html, /<a href="https:\/\/example\.com" data-external="1" rel="noopener noreferrer">/);
  assert.match(r.html, /<a href="other\.md#sec" data-relative="other\.md#sec">/);
  assert.match(r.html, /<a href="#top" data-anchor="1">/);
});

test("front matter は分離して details 表示される", () => {
  const r = renderer.render("---\ntitle: 報告書\ndate: 2026-10-20\n---\n\n# 本文\n");
  assert.deepEqual(r.frontMatter, { title: "報告書", date: "2026-10-20" });
  assert.match(r.html, /<details class="front-matter">/);
  assert.match(r.html, /<th>title<\/th><td>報告書<\/td>/);
  assert.equal(r.toc[0].text, "本文");
});

test("front matter が無ければ本文はそのまま", () => {
  const r = renderer.render("# A\n\n---\n\ntext\n");
  assert.equal(r.frontMatter, null);
  assert.match(r.html, /<hr>/);
});

test("テーブルは横スクロール用ラッパで包まれる", () => {
  const r = renderer.render("| a | b |\n|---|---|\n| 1 | 2 |\n");
  assert.match(r.html, /<div class="table-wrap"><table>/);
  assert.match(r.html, /<\/table><\/div>/);
});

test("タスクリスト・脚注プラグインが効く", () => {
  const r = renderer.render("- [x] done\n- [ ] todo\n\ntext[^1]\n\n[^1]: note\n");
  assert.match(r.html, /class="task-list-item-checkbox"[^>]*checked/);
  assert.match(r.html, /class="footnotes"/);
});

test("BOM 付きテキストも処理できる", () => {
  const r = renderer.render("﻿# Title\n");
  assert.equal(r.toc[0].text, "Title");
});

test("URL の自動リンク (linkify)", () => {
  const r = renderer.render("see https://example.org/x\n");
  assert.match(r.html, /<a href="https:\/\/example\.org\/x" data-external="1"/);
});

test("slugify / resolveDocUrl ユーティリティ", () => {
  assert.equal(MdRender.slugify("Hello, World!"), "hello-world");
  assert.equal(MdRender.slugify("  "), "section");
  assert.equal(MdRender.resolveDocUrl("#x"), "#x");
  assert.equal(MdRender.resolveDocUrl("/abs/p.png"), "https://doc.mdviewer/abs/p.png");
  assert.equal(MdRender.resolveDocUrl("mailto:a@b"), "mailto:a@b");
});
