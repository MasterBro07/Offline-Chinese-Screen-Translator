// End-to-end tests for the Chrome extension in ../extension.
//
//   npm i playwright ws && npx playwright install chromium
//   node extension-tests/run.js            fake-translator checks (no language pack needed)
//   node extension-tests/run.js --real     the same, then REAL on-device translation
//
// Options:
//   --real           also download the real language pack and translate a fixture page
//   --only-real      skip the fake-translator checks
//   --require-real   exit non-zero when real translation is UNAVAILABLE
//   --channel=NAME   browser channel (default "chromium"; e.g. "chrome", "msedge")
//   --headed         show the browser window
//   --ext=PATH       extension folder (default ../extension)
//
// The fake checks use the extension's test hook (chrome.storage.local
// "debugFakeTranslator"), which answers "EN[<text>]" instead of translating,
// and a WebSocket server in this script that plays the desktop app and
// answers page batches with "CL[<text>]".
//
// --real prints one line: REAL_TRANSLATION: OK | UNAVAILABLE | FAILED

'use strict';

const fs = require('fs');
const path = require('path');
const http = require('http');
const os = require('os');
const { execFileSync } = require('child_process');
const { chromium } = require('playwright');
const { WebSocketServer } = require('ws');

const argv = process.argv.slice(2);
const flag = (name) => argv.includes('--' + name);
const option = (name, fallback) => {
  const hit = argv.find((a) => a.startsWith('--' + name + '='));
  return hit ? hit.slice(name.length + 3) : fallback;
};

const EXT = path.resolve(option('ext', path.join(__dirname, '..', 'extension')));
const FIXTURES = path.join(__dirname, 'fixtures');
const CHANNEL = option('channel', 'chromium');
const HEADLESS = !flag('headed');
const WS_PORT = Number(option('port', '47999'));
const HAN = /\p{Script=Han}/u;

let failed = 0;
const ok = (cond, name, extra = '') => {
  console.log((cond ? 'PASS ' : 'FAIL ') + name + (cond ? '' : '  ' + extra));
  if (!cond) failed++;
  return !!cond;
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function until(fn, timeout = 10000, step = 100) {
  const end = Date.now() + timeout;
  let last;
  while (Date.now() < end) {
    try { last = await fn(); if (last) return last; } catch (e) { last = e; }
    await sleep(step);
  }
  return false;
}

function launch(profile) {
  return chromium.launchPersistentContext(profile, {
    channel: CHANNEL,
    headless: HEADLESS,
    args: [
      `--disable-extensions-except=${EXT}`,
      `--load-extension=${EXT}`,
      // Branded Chrome 137+ ignores --load-extension unless this is switched off.
      '--disable-features=DisableLoadExtensionCommandLineSwitch',
    ],
  });
}

function startFixtureServer() {
  const server = http.createServer((req, res) => {
    const name = path.basename(new URL(req.url, 'http://x').pathname) || 'basic.html';
    const file = path.join(FIXTURES, name);
    if (!fs.existsSync(file)) { res.statusCode = 404; res.end('not found'); return; }
    res.setHeader('content-type', 'text/html; charset=utf-8');
    res.end(fs.readFileSync(file));
  });
  return new Promise((resolve) => server.listen(0, () => resolve(server)));
}

function staticChecks() {
  const m = JSON.parse(fs.readFileSync(path.join(EXT, 'manifest.json'), 'utf8'));
  ok(m.manifest_version === 3 && m.name === 'Auto Translator' && m.version === '0.1.0', 'manifest basics');
  const refs = [m.background.service_worker, m.action.default_popup, ...m.content_scripts.flatMap((c) => c.js),
    ...Object.values(m.icons), ...Object.values(m.action.default_icon), 'offscreen.html', 'engine.html', 'welcome.html'];
  for (const f of fs.readdirSync(EXT).filter((f) => f.endsWith('.html'))) {
    const html = fs.readFileSync(path.join(EXT, f), 'utf8');
    for (const mm of html.matchAll(/(?:src|href)="([^"#:]+)"/g)) refs.push(mm[1]);
  }
  const missing = [...new Set(refs)].filter((r) => !fs.existsSync(path.join(EXT, r)));
  ok(missing.length === 0, 'all referenced files exist', missing.join(','));
  const risky = [];
  for (const f of fs.readdirSync(EXT).filter((f) => f.endsWith('.js'))) {
    let good = true;
    try { execFileSync(process.execPath, ['--check', path.join(EXT, f)]); } catch (e) { good = false; }
    ok(good, 'node --check ' + f);
    const src = fs.readFileSync(path.join(EXT, f), 'utf8').replace(/\/\/.*$/gm, '');
    if (/\beval\s*\(|new Function\s*\(|\.innerHTML\b|\.outerHTML\b|insertAdjacentHTML|document\.write|__fakeTranslator/.test(src)) risky.push(f);
  }
  ok(risky.length === 0, 'no eval / innerHTML / document.write / global test hook in extension code', risky.join(','));
  ok(!m.externally_connectable && !m.web_accessible_resources, 'manifest exposes nothing to web pages');
}

// ------------------------------------------------------------- fake checks

async function fakeChecks() {
  staticChecks();

  // --- fake desktop app (bridge server)
  const wss = new WebSocketServer({ port: WS_PORT, path: '/bridge' });
  const inbox = [];
  const origins = [];
  let client = null;
  // mode: "answer" | "error" | "silent" (never answers) ; state: sent after every hello
  const app = { state: { premium: false, engine: 'claude' }, mode: 'answer', pageReqs: [], answers: {} };
  wss.on('connection', (ws, req) => {
    origins.push(req.headers.origin);
    client = ws;
    ws.on('message', (d) => {
      const m = JSON.parse(d.toString());
      if (m.type === 'page-translate') {
        app.pageReqs.push(m);
        if (app.mode === 'answer') {
          ws.send(JSON.stringify({ type: 'page-result', id: m.id, translations: m.texts.map((t) => (t in app.answers ? app.answers[t] : 'CL[' + t + ']')) }));
        } else if (app.mode === 'error') {
          ws.send(JSON.stringify({ type: 'page-error', id: m.id, message: 'Tageslimit erreicht' }));
        }
        return;
      }
      inbox.push(m);
      if (m.type === 'hello' && app.state) ws.send(JSON.stringify({ type: 'app-state', ...app.state }));
    });
  });
  const send = (obj) => client.send(JSON.stringify(obj));
  const setAppState = (state) => { app.state = state; send({ type: 'app-state', ...state }); };
  const nextMsg = (pred, timeout = 8000) => until(() => { const i = inbox.findIndex(pred); return i >= 0 ? inbox.splice(i, 1)[0] : null; }, timeout, 50);
  const sentTexts = () => app.pageReqs.flatMap((r) => r.texts);

  const server = await startFixtureServer();
  const base = 'http://localhost:' + server.address().port + '/';

  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'atx-'));
  const ctx = await launch(profile);
  try {
    let sw = ctx.serviceWorkers()[0] || await ctx.waitForEvent('serviceworker', { timeout: 15000 });
    const extId = new URL(sw.url()).host;
    console.log('extension loaded, id', extId, 'browser', ctx.browser() && ctx.browser().version());
    const swEval = async (fn, arg) => {
      for (let i = 0; i < 5; i++) {
        try { sw = ctx.serviceWorkers().find((w) => w.url().includes(extId)) || sw; return await sw.evaluate(fn, arg); }
        catch (e) { await ctx.newPage().then(async (p) => { await p.goto(`chrome-extension://${extId}/popup.html`); await p.close(); }); }
      }
      throw new Error('service worker unreachable');
    };
    const local = (keys) => swEval((k) => chrome.storage.local.get(k), keys);
    const engineState = async () => (await local('engineState')).engineState || {};
    const stateIs = (pred, timeout = 8000) => until(async () => pred(await engineState()), timeout);

    // --- 0. real API state in this browser
    const hasApi = await swEval(() => 'Translator' in self);
    console.log('INFO real Translator global in service worker:', hasApi);
    const welcome = await until(() => ctx.pages().find((p) => p.url().endsWith('/welcome.html')));
    ok(!!welcome, 'welcome tab opened on install');
    const real = await until(async () => (await local('engineState')).engineState, 10000);
    console.log('INFO real engine state (no fake):', JSON.stringify(real), 'mode', (await local('engineMode')).engineMode);
    if (real && real.apiPresent) {
      ok(real.host === 'offscreen', 'REAL Translator API is exposed in the offscreen document of this browser');
      await swEval((port) => chrome.storage.local.set({ bridgePort: port }), WS_PORT);
      const h0 = await nextMsg((m) => m.type === 'hello');
      console.log('INFO real hello:', JSON.stringify(h0));
      ok(h0 && h0.ready === (real.availability === 'available'), 'real API: hello.ready matches availability');
      if (h0 && !h0.ready) {
        send({ type: 'translate', id: '1', source: 'zh', target: 'en', texts: ['你好'] });
        const e0 = await nextMsg((m) => m.id === '1');
        ok(e0 && e0.type === 'error', 'real API, pack missing: translate -> error', JSON.stringify(e0));
      }
    } else {
      await swEval((port) => chrome.storage.local.set({ bridgePort: port }), WS_PORT);
    }
    if (welcome) {
      const wtxt = await welcome.innerText('body');
      ok(/Sprachpaket laden/.test(wtxt) && /Desktop-App/.test(wtxt), 'welcome page shows German steps');
      ok(!/Claude|API-Schlüssel/.test(wtxt), 'welcome page does not mention Claude or API keys');
    }

    // --- 1. simulate missing Translator API: offscreen -> tab -> none
    inbox.length = 0;
    await swEval(() => chrome.storage.local.set({ debugFakeTranslator: 'none' }));
    const none = await until(async () => (await local('engineMode')).engineMode === 'none', 15000);
    ok(none, 'API missing everywhere: engine mode falls back offscreen -> tab -> none', JSON.stringify(await local(null)));
    ok(!(await swEval(() => chrome.offscreen.hasDocument())), 'offscreen document closed after fallback');
    ok(!(await until(() => ctx.pages().find((p) => p.url().endsWith('/engine.html')), 1500)), 'engine tab closed in mode none');
    ok((await engineState()).apiPresent === false, 'engineState reports API missing');

    // --- 2. page opened while engine is unavailable stays original, queued
    const page = await ctx.newPage();
    await page.goto(base + 'basic.html');
    await sleep(1500);
    ok((await page.textContent('#h')) === '  你好世界  ', 'page untouched while engine unavailable');

    // --- 3. enable fake translator in the offscreen host
    inbox.length = 0;
    await swEval(() => chrome.storage.local.set({ debugFakeTranslator: 'all', engineMode: 'offscreen' }));
    const hello = await nextMsg((m) => m.type === 'hello');
    ok(hello && hello.version === 2 && hello.engine === 'chrome-translator' && hello.ready === true && hello.detail === '',
      'bridge: hello (protocol version 2)', JSON.stringify(hello));
    ok(origins.length > 0 && origins.every((o) => o === 'chrome-extension://' + extId), 'bridge: Origin is chrome-extension://<this extension>', origins.join());
    ok(await swEval(() => chrome.offscreen.hasDocument()), 'engine hosted in offscreen document');
    const st = await stateIs((s) => s.ready && s.bridgeConnected && s);
    ok(st && st.host === 'offscreen', 'engineState in storage', JSON.stringify(st));
    ok(st && st.pagesReady === true && st.pageEngine === 'chrome' && st.premium === false, 'engineState: pagesReady, pageEngine chrome, no premium', JSON.stringify(st));

    send({ type: 'translate', id: '42', source: 'zh', target: 'en', texts: ['你好', '', '  世界 ', '第一行\n第二行', '   ', '你好'] });
    const res = await nextMsg((m) => m.id === '42');
    ok(res && res.type === 'result' && JSON.stringify(res.translations) === JSON.stringify(['EN[你好]', '', '  EN[世界] ', 'EN[第一行]\nEN[第二行]', '   ', 'EN[你好]']),
      'bridge: translate -> result (order, length, whitespace, line breaks)', JSON.stringify(res));
    send({ type: 'ping' });
    ok(!!(await nextMsg((m) => m.type === 'pong')), 'bridge: ping -> pong');
    send({ type: 'translate', id: '43', source: 'zh', target: 'en', texts: 'oops' });
    const err = await nextMsg((m) => m.id === '43');
    ok(err && err.type === 'error' && typeof err.message === 'string', 'bridge: invalid request -> error', JSON.stringify(err));
    send({ type: 'translate', id: '44', source: 'zh-Hans', target: 'en', texts: [] });
    const empty = await nextMsg((m) => m.id === '44');
    ok(empty && empty.type === 'result' && empty.translations.length === 0, 'bridge: empty batch');

    // --- 4. queued page resumes without reload once the engine is ready
    ok(await until(async () => (await page.textContent('#h')) === '  EN[你好世界]  ', 8000), 'queued page translated after engine became ready (whitespace kept)', await page.textContent('#h'));
    const snap = () => page.evaluate(() => ({
      b: document.getElementById('b').textContent, en: document.getElementById('en').textContent,
      mixed: document.getElementById('mixed').textContent,
      hTitle: document.getElementById('h').title, titledTitle: document.getElementById('titled').title,
      titled: document.getElementById('titled').textContent,
      inp: document.getElementById('inp').value, pw: document.getElementById('pw').value, pwPh: document.getElementById('pw').placeholder,
      ph: document.getElementById('ph').placeholder, ta: document.getElementById('ta').value, taPh: document.getElementById('ta').placeholder,
      btn: document.getElementById('btn').value, alt: document.getElementById('img').alt,
      ce: document.getElementById('ce').textContent, pre: document.getElementById('pre').textContent,
      sh: document.getElementById('host').shadowRoot.getElementById('sh').textContent, title: document.title,
      dyn: document.getElementById('dyn').textContent,
    }));
    await until(async () => (await snap()).sh === 'EN[影子]' && (await snap()).alt === 'EN[图片]');
    let s = await snap();
    ok(s.b === 'EN[中文]' && s.mixed === 'Hello EN[中文] text' && s.en === 'English only', 'text nodes translated, English untouched', JSON.stringify(s));
    ok(s.hTitle === '你好世界', 'original shown as tooltip', s.hTitle);
    ok(s.titled === 'EN[有标题]' && s.titledTitle === 'own title', 'own non-Han title kept', JSON.stringify(s));
    ok(s.inp === '用户输入' && s.ta === '文本区域', 'input/textarea values untouched');
    ok(s.pw === '密码' && s.pwPh === '密码', 'password field untouched');
    ok(s.ph === 'EN[请输入]' && s.taPh === 'EN[占位]' && s.alt === 'EN[图片]' && s.btn === 'EN[提交]', 'attributes placeholder/alt/button value translated', JSON.stringify(s));
    ok(s.ce === '可编辑' && s.pre === '代码', 'contenteditable and pre untouched');
    ok(s.sh === 'EN[影子]', 'open shadow root translated');
    ok(s.title === 'EN[测试页面]', 'document title translated', s.title);

    // dynamic content
    await page.evaluate(() => {
      const d = document.getElementById('dyn');
      d.innerHTML = '<span id=d1>动态</span><span id=d2 aria-label="标签">x</span>';
      document.getElementById('en').firstChild.nodeValue = '后来变了';
      document.getElementById('b').firstChild.nodeValue = '再变'; // page changes an already translated node
    });
    ok(await until(async () => await page.evaluate(() => document.getElementById('d1').textContent === 'EN[动态]' &&
      document.getElementById('d2').getAttribute('aria-label') === 'EN[标签]' &&
      document.getElementById('en').textContent === 'EN[后来变了]' && document.getElementById('b').textContent === 'EN[再变]'), 5000),
      'dynamically added / changed text and attributes translated', JSON.stringify(await snap()));
    await sleep(800);
    ok(await page.evaluate(() => document.getElementById('d1').textContent === 'EN[动态]' && document.getElementById('h').textContent === '  EN[你好世界]  '),
      'no re-translation loop (stable after 800 ms)');
    // typing into fields never changes them
    await page.fill('#inp', '我打字'); await page.fill('#ta', '我也打字'); await sleep(500);
    ok(await page.evaluate(() => document.getElementById('inp').value === '我打字' && document.getElementById('ta').value === '我也打字'), 'typed text untouched');

    // many nodes -> several batches
    await page.evaluate(() => {
      const d = document.createElement('div'); d.id = 'many';
      for (let i = 0; i < 150; i++) { const p = document.createElement('p'); p.textContent = '第' + i + '行'; d.appendChild(p); }
      document.body.appendChild(d);
    });
    ok(await until(() => page.evaluate(() => [...document.querySelectorAll('#many p')].every((p, i) => p.textContent === 'EN[第' + i + '行]')), 8000), '150 added nodes translated across batches');

    // --- 5. global toggle off restores, on translates again
    await swEval(() => chrome.storage.sync.set({ enabled: false }));
    ok(await until(async () => (await page.textContent('#h')) === '  你好世界  '), 'toggle off restores original text');
    s = await snap();
    ok(s.ph === '请输入' && s.alt === '图片' && s.btn === '提交' && s.sh === '影子' && s.title === '测试页面' && s.hTitle === '' && s.titledTitle === 'own title' && s.b === '再变',
      'toggle off restores attributes, shadow DOM, title, tooltips', JSON.stringify(s));
    await page.evaluate(() => { document.getElementById('dyn').textContent = '关闭时添加'; }); await sleep(600);
    ok((await snap()).dyn === '关闭时添加', 'nothing translated while off');
    await swEval(() => chrome.storage.sync.set({ enabled: true }));
    ok(await until(async () => (await page.textContent('#h')) === '  EN[你好世界]  ' && (await snap()).dyn === 'EN[关闭时添加]'), 'toggle on translates again');
    // per-site
    await swEval(() => chrome.storage.sync.set({ disabledHosts: ['localhost'] }));
    ok(await until(async () => (await page.textContent('#h')) === '  你好世界  '), 'per-site disable restores original');
    await swEval(() => chrome.storage.sync.set({ disabledHosts: ['example.org'] }));
    ok(await until(async () => (await page.textContent('#h')) === '  EN[你好世界]  '), 'other hosts in the list do not disable this site');

    // --- 6. popup
    const popup = await ctx.newPage();
    await popup.goto(`chrome-extension://${extId}/popup.html`);
    await until(async () => (await popup.textContent('#app-status')) === 'verbunden', 4000);
    const ptxt = await popup.innerText('body');
    ok(/Sprachpaket/.test(ptxt) && /Desktop-App/.test(ptxt) && /Webseiten automatisch übersetzen/.test(ptxt), 'popup renders German UI');
    ok((await popup.textContent('#app-status')) === 'verbunden', 'popup: Desktop-App verbunden', await popup.textContent('#app-status'));
    ok((await popup.textContent('#page-engine')) === 'Chrome-Übersetzer', 'popup: Webseiten: Chrome-Übersetzer (no premium)', await popup.textContent('#page-engine'));
    ok(!(await popup.$('#use-premium')) && !/Claude|API-Schlüssel/.test(ptxt), 'popup: no premium setting, no Claude/API mention');
    console.log('INFO popup pack status (real API):', await popup.textContent('#pack-status'), '| button visible:', await popup.isVisible('#download'), '| hint visible:', await popup.isVisible('#api-hint'));
    await popup.uncheck('#enabled');
    ok(await until(async () => (await page.textContent('#h')) === '  你好世界  '), 'popup toggle switches page translation off');
    await popup.check('#enabled');
    ok(await until(async () => (await page.textContent('#h')) === '  EN[你好世界]  '), 'popup toggle switches it on again');

    ok(app.pageReqs.length === 0, 'v2: no page-translate is ever sent while premium is false', String(app.pageReqs.length));

    // =================================================== protocol version 2
    // --- 9. app-state premium on: engine switch in the middle of a page
    setAppState({ premium: true, engine: 'claude' });
    const prem = await stateIs((x) => x.premium && x.pageEngine === 'claude' && x);
    ok(prem && prem.pagesReady && prem.ready, 'v2: app-state premium:true -> engineState premium, pageEngine claude', JSON.stringify(await engineState()));
    ok(await until(async () => (await popup.textContent('#page-engine')) === 'Desktop-App', 4000), 'popup: Webseiten: Desktop-App', await popup.textContent('#page-engine'));
    await sleep(1000);
    ok(app.pageReqs.length === 0 && (await page.textContent('#h')) === '  EN[你好世界]  ', 'v2: engine switch does not re-translate the page', JSON.stringify(sentTexts()));
    await page.evaluate(() => { document.getElementById('dyn').innerHTML = '<span id=n1>新文本</span><span id=n2>动态</span>'; });
    ok(await until(async () => (await page.textContent('#n1')) === 'CL[新文本]' && (await page.textContent('#n2')) === 'CL[动态]'), 'v2: page-translate round trip, new text comes from the app',
      await page.textContent('#dyn'));
    const r0 = app.pageReqs[0];
    ok(r0 && typeof r0.id === 'string' && r0.source === 'zh' && r0.target === 'en' && Array.isArray(r0.texts), 'v2: page-translate message shape', JSON.stringify(r0));
    ok(sentTexts().includes('动态'), 'v2: caches are per engine (text known on-device is asked again from the app)', JSON.stringify(sentTexts()));
    // the app's own requests still use the on-device translator
    const before = app.pageReqs.length;
    send({ type: 'translate', id: '50', source: 'zh', target: 'en', texts: ['新文本', '别的'] });
    const r50 = await nextMsg((m) => m.id === '50');
    ok(r50 && JSON.stringify(r50.translations) === JSON.stringify(['EN[新文本]', 'EN[别的]']) && app.pageReqs.length === before,
      'v2: bridge translate stays on-device while premium is on (no loop)', JSON.stringify(r50));

    // --- 10. shop page under premium: order, batches, robustness
    app.pageReqs.length = 0;
    app.answers = { 免费: 'Free', 送货: 'shipping', 包邮: 'Free', 退货: 'returns' };
    const shop = await ctx.newPage();
    await shop.goto(base + 'shop.html');
    const text = (sel) => shop.evaluate((q) => document.querySelector(q).textContent, sel);
    ok(await until(async () => (await text('#order p:last-child')) === 'CL[顺序五]' && (await text('#o3')) === 'CL[大号]'), 'shop page translated through the app');
    const firstReq = app.pageReqs.find((r) => r.texts.includes('顺序一'));
    const firstTexts = firstReq ? firstReq.texts : [];
    const positions = ['顺序一', '顺序二', '顺序三', '顺序四', '顺序五', '满', '减', '手机壳', '红色', '蓝色', '大号', '销量'].map((t) => firstTexts.indexOf(t));
    ok(positions.every((p, i) => p >= 0 && (i === 0 || p > positions[i - 1])), 'v2: batch is sent in document order (texts and attributes interleaved)', JSON.stringify(firstTexts));
    ok((await text('#promo')) === 'CL[满]300 CL[减]50', 'inline pieces: numbers kept, gap added before a piece that follows a digit', await text('#promo'));
    ok((await text('#latin')) === 'iPhone CL[手机壳]', 'inline pieces: gap after Latin text', await text('#latin'));
    ok((await text('#pair')) === 'Free shipping', 'inline pieces: adjacent translated spans do not run together', await text('#pair'));
    ok((await text('#flexpair')) === 'Freereturns', 'inline pieces: separate boxes (flex items) get no extra space', await text('#flexpair'));
    ok((await text('#spaced')) === 'CL[买] CL[二] CL[送] CL[一]', 'inline pieces: existing whitespace kept as is', await text('#spaced'));
    ok((await text('#price')) === '¥199.00' && !sentTexts().some((t) => t.includes('199.00')), 'price without Chinese is never touched or sent');
    ok((await text('#sold')) === 'CL[已售 1.2万+]' && (await text('#range')) === 'CL[¥1,299.00 - ¥2,499.00 起]', 'number-heavy strings go through as one piece', await text('#sold') + ' | ' + await text('#range'));

    const sel = await shop.evaluate(() => {
      const q = (id) => document.getElementById(id);
      return { o1: q('o1').textContent, o1v: q('o1').value, o2: q('o2').textContent, o2v: q('o2').value, o3v: q('o3').value, og: q('og').label, value: q('sel').value };
    });
    ok(sel.o1 === 'CL[红色]' && sel.o2 === 'CL[蓝色]' && sel.og === 'CL[尺码]', 'select: option text and optgroup label translated', JSON.stringify(sel));
    ok(sel.o1v === '红色' && sel.o3v === '大号' && sel.o2v === 'b' && sel.value === 'b', 'select: submitted option values stay original', JSON.stringify(sel));

    const svg = await shop.evaluate(() => ({ t: document.getElementById('svgtext').textContent, title: document.getElementById('svgtitle').textContent, attr: document.getElementById('svgtext').hasAttribute('title') }));
    ok(svg.t === 'CL[销量]' && svg.title === 'CL[图表标题]' && !svg.attr, 'SVG <text> and <title> translated, no title attribute on SVG elements', JSON.stringify(svg));

    ok(await until(() => shop.evaluate(() => document.getElementById('late').shadowRoot && document.getElementById('late').shadowRoot.getElementById('ls').textContent === 'CL[迟到的影子]'), 8000),
      'shadow root attached after the scan is found and translated');
    ok(await until(() => shop.evaluate(() => window.__closedRoot && window.__closedRoot.textContent === 'CL[封闭的影子]'), 8000), 'closed shadow root of a custom element translated');
    await shop.evaluate(() => { document.getElementById('late').shadowRoot.getElementById('ls').textContent = '影子更新'; });
    ok(await until(() => shop.evaluate(() => document.getElementById('late').shadowRoot.getElementById('ls').textContent === 'CL[影子更新]')), 'late shadow root is observed for later changes');

    for (const [name, want] of [['f-same', 'CL[框架内容]'], ['f-cross', 'CL[框架内容]'], ['f-srcdoc', 'CL[内嵌文档]'], ['f-blank', 'CL[空白框架]']]) {
      const frame = shop.frames().find((f) => f.name() === name);
      const got = frame && await until(async () => (await frame.evaluate(() => document.getElementById('ft').textContent)) === want, 6000);
      ok(got, 'iframe translated: ' + name + (frame ? ' (' + frame.url().slice(0, 40) + ')' : ''), frame ? await frame.evaluate(() => document.body.textContent) : 'frame missing');
    }

    // mutation storm: 80 ticks, every 50 ms, on a text node and on a replaced container
    await sleep(500);
    app.pageReqs.length = 0;
    const stormStart = Date.now();
    const storm = shop.evaluate(() => window.startStorm(80, 50));
    const staticOk = await until(async () => (await text('#static')) === 'CL[静态文本]', 3000, 50);
    ok(staticOk, 'storm: other text is still translated promptly while the page mutates every 50 ms', await text('#static'));
    await storm;
    ok(await until(async () => (await text('#tick1')) === 'CL[剩余 80 秒]' && (await text('#tick2')) === 'CL[倒计时 80 秒]', 4000), 'storm: counters end up translated with the right number',
      await text('#tick1') + ' | ' + await text('#tick2'));
    console.log('INFO storm: ' + app.pageReqs.length + ' requests, ' + sentTexts().length + ' texts for 160 text changes in ' + (Date.now() - stormStart) + ' ms');
    ok(app.pageReqs.length <= 8 && sentTexts().length <= 16, 'storm: 160 text changes cause only a handful of requests (no request storm)', app.pageReqs.length + ' requests / ' + sentTexts().length + ' texts');
    await sleep(700);
    const quiet = app.pageReqs.length;
    await sleep(700);
    ok(app.pageReqs.length === quiet, 'storm: no requests after the page went quiet');

    // toggle off restores the shop page, including pinned option values
    await swEval(() => chrome.storage.sync.set({ enabled: false }));
    ok(await until(async () => (await text('#o1')) === '红色' && (await text('#promo')) === '满300减50' && (await text('#pair')) === '免费送货'), 'shop page: toggle off restores originals (no leftover spaces)', await text('#promo'));
    ok(await shop.evaluate(() => !document.getElementById('o1').hasAttribute('value') && document.getElementById('og').label === '尺码' &&
      document.getElementById('late').shadowRoot.getElementById('ls').textContent === '影子更新' && window.__closedRoot.textContent === '封闭的影子'),
      'shop page: toggle off removes pinned option values, restores labels and shadow roots');
    await swEval(() => chrome.storage.sync.set({ enabled: true }));
    ok(await until(async () => (await text('#o1')) === 'CL[红色]'), 'shop page: toggle on again');

    // large page: batch limits under premium
    app.pageReqs.length = 0;
    await shop.evaluate(() => {
      const d = document.createElement('div'); d.id = 'big';
      for (let i = 0; i < 200; i++) { const p = document.createElement('p'); p.textContent = '这是第' + i + '个商品的详细描述，'.repeat(14); d.appendChild(p); }
      document.body.appendChild(d);
    });
    ok(await until(() => shop.evaluate(() => [...document.querySelectorAll('#big p')].every((p) => p.textContent.startsWith('CL['))), 15000), 'large page: 200 long texts translated through the app');
    const sizes = app.pageReqs.map((r) => [r.texts.length, r.texts.join('').length]);
    ok(sizes.every(([n, c]) => n <= 60 && c <= 6000) && sizes.some(([n, c]) => c > 4000), 'v2: premium batches are large (up to 60 texts / 6000 chars) but bounded', JSON.stringify(sizes));
    await shop.close();

    // --- 11. security: what a web page (or a compromised content script) can reach
    ok(await page.evaluate(() => typeof chrome === 'undefined' || !chrome.runtime || !chrome.runtime.sendMessage), 'web page main world has no chrome.runtime messaging');
    await page.evaluate(() => { window.__fakeTranslator = { availability: async () => 'available' }; window.debugFakeTranslator = 'none'; localStorage.setItem('debugFakeTranslator', 'none'); });
    const cdp = await ctx.newCDPSession(page);
    const worlds = [];
    cdp.on('Runtime.executionContextCreated', (e) => { if (e.context.auxData && e.context.auxData.type === 'isolated') worlds.push(e.context); });
    await cdp.send('Runtime.enable');
    const world = await until(() => worlds.find((w) => (w.origin || '').includes(extId) || w.name === 'Auto Translator'), 3000);
    if (ok(!!world, 'content script world found (for message validation checks)', JSON.stringify(worlds))) {
      const csEval = async (expression) => {
        const r = await cdp.send('Runtime.evaluate', { contextId: world.id, expression, awaitPromise: true, returnByValue: true });
        return r.exceptionDetails ? { exception: r.exceptionDetails.exception && r.exceptionDetails.exception.description } : r.result.value;
      };
      const stateBefore = JSON.stringify(await engineState());
      const forged = await csEval(`Promise.all([
        chrome.runtime.sendMessage({ type: 'engine.state', state: { ready: false, pageEngine: 'evil', pagesReady: false } }).then((r) => r === undefined ? 'ignored' : r, () => 'ignored'),
        chrome.runtime.sendMessage({ type: 'engine.hello', host: 'offscreen', apiPresent: false }).then((r) => r === undefined ? 'ignored' : r, () => 'ignored'),
        chrome.runtime.sendMessage({ type: 'engine.getConfig' }).then((r) => r === undefined ? 'ignored' : r, () => 'ignored'),
        chrome.runtime.sendMessage({ type: 'recheck', apiPresent: false }).then((r) => r === undefined ? 'ignored' : r, () => 'ignored'),
        chrome.runtime.sendMessage({ type: 'translate', texts: 'x'.repeat(10) }).catch(() => 'threw'),
        chrome.runtime.sendMessage({ type: 'translate', texts: [{ toString() { return 'x'; } }, 5] }).catch(() => 'threw'),
        chrome.runtime.sendMessage({ type: 'translate', texts: new Array(5000).fill('文') }).catch(() => 'threw'),
        chrome.runtime.sendMessage({ type: 'translate', source: 'zh', target: 'en', texts: ['验证'] }).catch(() => 'threw'),
      ])`);
      ok(Array.isArray(forged) && forged.slice(0, 4).every((r) => r === 'ignored'), 'service worker ignores engine.* / recheck messages from content scripts', JSON.stringify(forged));
      ok(Array.isArray(forged) && forged.slice(4, 7).every((r) => r && r.ok === false && r.error === 'bad-request'), 'service worker rejects malformed / oversized page batches', JSON.stringify(forged));
      ok(Array.isArray(forged) && forged[7] && forged[7].ok === true && forged[7].translations[0] === 'CL[验证]', 'service worker still serves a well-formed page batch', JSON.stringify(forged));
      await sleep(500);
      ok(JSON.stringify(await engineState()) === stateBefore && (await local('engineMode')).engineMode === 'offscreen' && await swEval(() => chrome.offscreen.hasDocument()),
        'forged messages changed neither the engine state nor the engine host');
    }
    await cdp.detach();
    await page.evaluate(() => { document.getElementById('dyn').textContent = '页面变量无效'; });
    ok(await until(async () => (await snap()).dyn === 'CL[页面变量无效]'), 'page-defined __fakeTranslator / debug flags have no effect');

    // --- 12. fallbacks: page-error, user setting, timeout, disconnect
    app.mode = 'error';
    app.pageReqs.length = 0;
    await page.evaluate(() => { document.getElementById('dyn').textContent = '错误回退'; });
    ok(await until(async () => (await snap()).dyn === 'EN[错误回退]'), 'v2: page-error -> this batch falls back to the on-device translator', (await snap()).dyn);
    ok(app.pageReqs.length === 1 && app.pageReqs[0].texts[0] === '错误回退', 'v2: the app was asked first', JSON.stringify(sentTexts()));
    app.mode = 'answer';
    await page.evaluate(() => { document.getElementById('dyn').innerHTML = '<i>错误回退</i>'; });
    ok(await until(async () => (await snap()).dyn === 'CL[错误回退]'), 'v2: an on-device fallback result is not cached as the app engine\'s result', (await snap()).dyn);

    // user setting off / on
    await swEval(() => chrome.storage.sync.set({ usePremium: false }));
    ok(await stateIs((x) => x.pageEngine === 'chrome' && x.premium === true && x.usePremium === false), 'setting off: pageEngine chrome although the app offers premium', JSON.stringify(await engineState()));
    ok(await until(async () => (await popup.textContent('#page-engine')) === 'Chrome-Übersetzer'), 'popup shows Chrome-Übersetzer when the setting is off');
    app.pageReqs.length = 0;
    await page.evaluate(() => { document.getElementById('dyn').textContent = '设置关闭'; });
    ok(await until(async () => (await snap()).dyn === 'EN[设置关闭]') && app.pageReqs.length === 0, 'setting off: pages use the on-device translator, nothing is sent to the app');
    await swEval(() => chrome.storage.sync.set({ usePremium: true }));
    ok(await stateIs((x) => x.pageEngine === 'claude'), 'setting on again: pageEngine claude');
    await popup.screenshot({ path: path.join(os.tmpdir(), 'atx-popup.png') }).catch(() => {});
    await popup.close();

    // timeout
    app.mode = 'silent';
    app.pageReqs.length = 0;
    const t0 = Date.now();
    await page.evaluate(() => { document.getElementById('dyn').textContent = '超时回退'; });
    await sleep(3000);
    ok((await snap()).dyn === '超时回退' && app.pageReqs.length === 1, 'v2: while the app is silent the batch waits (no duplicate requests)', (await snap()).dyn + ' / ' + app.pageReqs.length);
    ok(await until(async () => (await snap()).dyn === 'EN[超时回退]', 12000), 'v2: timeout -> on-device fallback', (await snap()).dyn);
    const waited = Date.now() - t0;
    ok(waited > 9000 && waited < 13000, 'v2: the timeout is about 10 s', waited + ' ms');
    ok(await stateIs((x) => x.pageEngine === 'chrome' && x.premium === true, 2000), 'v2: after a timeout the app engine is paused (pageEngine chrome)', JSON.stringify(await engineState()));
    await page.evaluate(() => { document.getElementById('dyn').textContent = '暂停期间'; });
    ok(await until(async () => (await snap()).dyn === 'EN[暂停期间]', 3000) && app.pageReqs.length === 1, 'v2: during the pause pages translate on-device at once');
    // late answer to the timed-out request is ignored
    send({ type: 'page-result', id: app.pageReqs[0].id, translations: ['LATE'] });
    app.mode = 'answer';
    setAppState({ premium: false, engine: 'claude' });
    setAppState({ premium: true, engine: 'claude' });
    ok(await stateIs((x) => x.pageEngine === 'claude'), 'v2: a fresh app-state premium:true ends the pause');
    ok((await snap()).dyn === 'EN[暂停期间]', 'v2: late page-result for a timed-out id is ignored');

    // disconnect while a request is pending
    app.mode = 'silent';
    app.pageReqs.length = 0;
    inbox.length = 0;
    await page.evaluate(() => { document.getElementById('dyn').textContent = '断开回退'; });
    ok(await until(() => app.pageReqs.length === 1, 4000), 'v2: request pending at the app');
    for (const c of wss.clients) c.terminate();
    ok(await until(async () => (await snap()).dyn === 'EN[断开回退]', 4000), 'v2: disconnect -> pending batch falls back on-device immediately', (await snap()).dyn);
    app.mode = 'answer';
    app.state = { premium: false, engine: 'claude' };
    const helloAgain = await nextMsg((m) => m.type === 'hello', 10000);
    ok(helloAgain && helloAgain.version === 2, 'bridge: reconnects after connection loss');
    ok(await stateIs((x) => x.bridgeConnected && x.premium === false && x.pageEngine === 'chrome'), 'v2: premium state is reset by a disconnect and follows the new app-state', JSON.stringify(await engineState()));
    await page.evaluate(() => { document.getElementById('dyn').textContent = '重连之后'; });
    ok(await until(async () => (await snap()).dyn === 'EN[重连之后]'), 'v2: premium false after reconnect -> on-device, as before');

    // --- 13. language pack missing: pages translate through the app only
    inbox.length = 0;
    app.pageReqs.length = 0;
    app.state = { premium: true, engine: 'claude' };
    await swEval(() => chrome.storage.local.set({ debugFakeTranslator: 'missing' }));
    const helloMissing = await nextMsg((m) => m.type === 'hello', 15000);
    ok(helloMissing && helloMissing.ready === false && helloMissing.detail === 'Sprachpaket fehlt', 'pack missing: hello ready:false "Sprachpaket fehlt"', JSON.stringify(helloMissing));
    const ms = await stateIs((x) => x.premium && x.pagesReady && x);
    ok(ms && ms.ready === false && ms.pageEngine === 'claude' && ms.availability === 'downloadable', 'pack missing + premium: engineState ready:false, pagesReady:true, pageEngine claude', JSON.stringify(await engineState()));
    const page2 = await ctx.newPage();
    await page2.goto(base + 'basic.html');
    ok(await until(async () => (await page2.textContent('#h')) === '  CL[你好世界]  ' && (await page2.title()) === 'CL[测试页面]'), 'pack missing + premium: a new page is translated through the app', await page2.textContent('#h'));
    send({ type: 'translate', id: '60', source: 'zh', target: 'en', texts: ['你好'] });
    const e60 = await nextMsg((m) => m.id === '60');
    ok(e60 && e60.type === 'error' && !sentTexts().includes('你好'), 'pack missing: bridge translate answers error and is never routed to the app', JSON.stringify(e60));
    // the app fails and there is no on-device fallback: text stays, then recovers
    app.mode = 'error';
    await page2.evaluate(() => { document.getElementById('dyn').textContent = '两者都不行'; });
    await sleep(1500);
    ok((await page2.textContent('#dyn')) === '两者都不行', 'pack missing + page-error: text stays original (nothing to fall back to)');
    app.mode = 'answer';
    ok(await until(async () => (await page2.textContent('#dyn')) === 'CL[两者都不行]', 8000), 'pack missing: queued text is retried and translated once the app answers again');
    // premium off: nothing can translate; premium on: the queue resumes
    setAppState({ premium: false, engine: 'claude' });
    ok(await stateIs((x) => x.pagesReady === false && x.pageEngine === 'chrome'), 'pack missing + premium off: pagesReady false');
    await page2.evaluate(() => { document.getElementById('dyn').textContent = '等待引擎'; });
    await sleep(1500);
    ok((await page2.textContent('#dyn')) === '等待引擎', 'pack missing + premium off: page text is left alone');
    const tResume = Date.now();
    setAppState({ premium: true, engine: 'claude' });
    ok(await until(async () => (await page2.textContent('#dyn')) === 'CL[等待引擎]', 6000), 'premium on: waiting page resumes without reload', await page2.textContent('#dyn'));
    console.log('INFO resume after premium on took', Date.now() - tResume, 'ms');
    await page2.close();
    app.state = { premium: false, engine: 'claude' };

    // --- 7. tab fallback host (fake only in the tab)
    inbox.length = 0;
    await swEval(() => chrome.storage.local.set({ debugFakeTranslator: 'tab' }));
    const hello2 = await nextMsg((m) => m.type === 'hello', 15000);
    ok(hello2 && hello2.ready === true, 'tab fallback: engine connects from tab host', JSON.stringify(hello2));
    ok((await local('engineMode')).engineMode === 'tab' && !(await swEval(() => chrome.offscreen.hasDocument())), 'tab fallback: mode tab, offscreen closed');
    const info = await swEval(async (u) => (await chrome.tabs.query({})).filter((t) => (t.url || '').startsWith(u)).map((t) => ({ pinned: t.pinned, active: t.active, id: t.id })), `chrome-extension://${extId}/engine.html`);
    ok(info.length === 1 && info[0].pinned && !info[0].active, 'tab fallback: exactly one pinned inactive engine tab', JSON.stringify(info));
    await page.evaluate(() => { document.getElementById('dyn').textContent = '标签页模式'; });
    ok(await until(async () => (await snap()).dyn === 'EN[标签页模式]'), 'tab fallback: page translation works');
    setAppState({ premium: true, engine: 'claude' });
    await stateIs((x) => x.pageEngine === 'claude');
    await page.evaluate(() => { document.getElementById('dyn').textContent = '标签页高级'; });
    ok(await until(async () => (await snap()).dyn === 'CL[标签页高级]'), 'tab fallback: premium page translation works from the tab host too');
    app.state = { premium: false, engine: 'claude' };
    inbox.length = 0;
    await swEval((id) => chrome.tabs.remove(id), info[0].id);
    const hello3 = await nextMsg((m) => m.type === 'hello', 15000);
    ok(!!hello3, 'tab fallback: engine tab re-created after being closed');
    const info2 = await swEval(async (u) => (await chrome.tabs.query({})).filter((t) => (t.url || '').startsWith(u)).length, `chrome-extension://${extId}/engine.html`);
    ok(info2 === 1, 'tab fallback: still exactly one engine tab', String(info2));

    // --- 8. bridge reconnect after the app restarts
    inbox.length = 0;
    for (const c of wss.clients) c.terminate();
    const hello4 = await nextMsg((m) => m.type === 'hello', 10000);
    ok(!!hello4, 'bridge: reconnects after connection loss (tab host)');
    ok(origins.every((o) => o === 'chrome-extension://' + extId), 'bridge: every connection carried the extension origin (' + origins.length + ' connections)', origins.join());
  } catch (e) {
    console.log('FAIL exception', e);
    failed++;
  } finally {
    await ctx.close().catch(() => {});
    wss.close();
    server.close();
    fs.rmSync(profile, { recursive: true, force: true });
  }
}

// -------------------------------------------------------------- real check

/** Returns "OK" | "UNAVAILABLE" | "FAILED". */
async function realCheck() {
  const server = await startFixtureServer();
  const base = 'http://localhost:' + server.address().port + '/';
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'atx-real-'));
  const ctx = await launch(profile);
  const log = (...a) => console.log('REAL', ...a);
  try {
    const sw = ctx.serviceWorkers()[0] || await ctx.waitForEvent('serviceworker', { timeout: 15000 });
    const extId = new URL(sw.url()).host;
    log('browser', ctx.browser() && ctx.browser().version(), 'channel', CHANNEL, 'extension', extId);
    let welcome = await until(() => ctx.pages().find((p) => p.url().endsWith('/welcome.html')), 8000);
    if (!welcome) { welcome = await ctx.newPage(); await welcome.goto(`chrome-extension://${extId}/welcome.html`); }
    await welcome.bringToFront();
    const packState = () => welcome.getAttribute('#pack-status', 'data-state');
    await until(async () => !!(await packState()), 10000);
    let state = await packState();
    log('language pack status at start:', state, '(' + (await welcome.textContent('#pack-status')) + ')');
    if (state === 'no-api' || state === 'unavailable') {
      log('this browser has no usable Translator API for zh -> en:', await welcome.textContent('#api-hint'));
      return 'UNAVAILABLE';
    }
    if (state !== 'available') {
      await welcome.waitForSelector('#download:visible', { timeout: 10000 });
      await welcome.click('#download'); // a real click: Chrome requires a user gesture for the download
      log('clicked "Sprachpaket laden", waiting up to 5 minutes');
      const end = Date.now() + 5 * 60 * 1000;
      let lastText = '';
      while (Date.now() < end) {
        state = await packState();
        const shown = await welcome.textContent('#pack-status');
        if (shown !== lastText) { lastText = shown; log('  status:', shown); }
        if (state === 'available') break;
        if (await welcome.isVisible('#download-error')) { log('download error:', await welcome.textContent('#download-error')); break; }
        await sleep(1000);
      }
      if (state !== 'available') {
        log('language pack did not become available (status: ' + state + ')');
        return 'UNAVAILABLE';
      }
    }
    log('language pack available');

    const page = await ctx.newPage();
    await page.goto(base + 'real.html');
    const read = () => page.evaluate(() => {
      const q = (id) => document.getElementById(id);
      return {
        'h1': q('r1').textContent, 'paragraph': q('r2').textContent, 'inline pieces': q('r3').textContent, 'button': q('r4').textContent,
        'short text': q('r5').textContent, 'option 1': q('r6').options[0].textContent, 'option 2': q('r6').options[1].textContent,
        'placeholder': q('r7').placeholder, 'title': document.title, 'price': q('price').textContent,
        'option value': q('r6').options[0].value,
      };
    });
    const original = await read();
    const translatedKeys = Object.keys(original).filter((k) => k !== 'price' && k !== 'option value');
    const done = await until(async () => { const now = await read(); return translatedKeys.every((k) => !HAN.test(now[k])); }, 90000, 500);
    const now = await read();
    for (const k of Object.keys(original)) console.log('REAL   ' + k + ': ' + JSON.stringify(original[k]) + ' -> ' + JSON.stringify(now[k]));
    const english = translatedKeys.every((k) => !HAN.test(now[k]) && /[A-Za-z]{2}/.test(now[k]));
    const intact = now.price === '¥199.00' && now['option value'] === '红色' && /300/.test(now['inline pieces']) && /50/.test(now['inline pieces']);
    if (!done || !english) { log('page text did not become English'); return 'FAILED'; }
    if (!intact) { log('price, option value or numbers were changed'); return 'FAILED'; }
    return 'OK';
  } catch (e) {
    log('exception', e);
    return 'FAILED';
  } finally {
    await ctx.close().catch(() => {});
    server.close();
    fs.rmSync(profile, { recursive: true, force: true });
  }
}

(async () => {
  if (!flag('only-real')) await fakeChecks();
  if (flag('real') || flag('only-real')) {
    const result = await realCheck();
    console.log('REAL_TRANSLATION: ' + result);
    if (result === 'FAILED' || (result === 'UNAVAILABLE' && flag('require-real'))) failed++;
  }
  console.log(failed ? `\n${failed} FAILED` : '\nALL PASSED');
  process.exit(failed ? 1 : 0);
})();
