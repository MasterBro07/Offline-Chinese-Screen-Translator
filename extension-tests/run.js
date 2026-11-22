// End-to-end tests for the Chrome extension in ../extension.
//
//   node tools/fetch-ocr-assets.mjs      (once: offline OCR files for images)
//   npm i playwright && npx playwright install chromium
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
// "debugFakeTranslator"), which answers "EN[<text>]" instead of translating
// (English -> Chinese: "ZH[<text>]"). Text in images is recognised with the
// real offline OCR models.
//
// --real prints one line: REAL_TRANSLATION: OK | UNAVAILABLE | FAILED

'use strict';

const fs = require('fs');
const path = require('path');
const http = require('http');
const os = require('os');
const { execFileSync } = require('child_process');
const { chromium } = require('playwright');

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
    const pathname = new URL(req.url, 'http://x').pathname;
    const name = path.basename(pathname) || 'basic.html';
    // /img/x.png: test images. /protected/x.png: only for requests with the page as referrer
    // (like shop CDNs with hotlink protection), so the extension must use its screenshot fallback.
    const isImage = pathname.startsWith('/img/') || pathname.startsWith('/protected/');
    if (pathname.startsWith('/protected/') && !String(req.headers.referer || '').includes('/images.html')) {
      server.imageRefusals = (server.imageRefusals || 0) + 1;
      res.statusCode = 403; res.end('hotlink'); return;
    }
    const file = isImage ? path.join(FIXTURES, 'img', name) : path.join(FIXTURES, name);
    if (!fs.existsSync(file)) { res.statusCode = 404; res.end('not found'); return; }
    if (isImage) server.imageRequests = (server.imageRequests || 0) + 1;
    res.setHeader('content-type', isImage ? 'image/png' : 'text/html; charset=utf-8');
    res.end(fs.readFileSync(file));
  });
  return new Promise((resolve) => server.listen(0, () => resolve(server)));
}

function staticChecks() {
  const m = JSON.parse(fs.readFileSync(path.join(EXT, 'manifest.json'), 'utf8'));
  ok(m.manifest_version === 3 && m.name === 'Auto Translator' && /^\d+\.\d+\.\d+$/.test(m.version), 'manifest basics');
  const refs = [m.background.service_worker, m.action.default_popup, ...m.content_scripts.flatMap((c) => c.js),
    ...Object.values(m.icons), ...Object.values(m.action.default_icon), 'offscreen.html', 'engine.html', 'welcome.html'];
  for (const f of fs.readdirSync(EXT).filter((f) => f.endsWith('.html'))) {
    const html = fs.readFileSync(path.join(EXT, f), 'utf8');
    for (const mm of html.matchAll(/(?:src|href)="([^"#:]+)"/g)) refs.push(mm[1]);
  }
  const missing = [...new Set(refs)].filter((r) => !fs.existsSync(path.join(EXT, r)));
  ok(missing.length === 0, 'all referenced files exist', missing.join(','));
  const risky = [];
  for (const f of [...fs.readdirSync(EXT).filter((f) => f.endsWith('.js')), 'ocr/ocr.js']) {
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
    }
    if (welcome) {
      const wtxt = await welcome.innerText('body');
      ok(/Sprachpakete laden/.test(wtxt) && /Auf Chinesisch schreiben/.test(wtxt) && /Text in Bildern/.test(wtxt) && !/Desktop-App/.test(wtxt), 'welcome page shows German steps incl. images and writing helper, no desktop app');
      ok(!/Claude|API-Schlüssel/.test(wtxt), 'welcome page does not mention Claude or API keys');
    }

    // --- 1. simulate missing Translator API: offscreen -> tab -> none
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
    await swEval(() => chrome.storage.local.set({ debugFakeTranslator: 'all', engineMode: 'offscreen' }));
    const st = await stateIs((s) => s.ready && s, 15000);
    ok(await swEval(() => chrome.offscreen.hasDocument()), 'engine hosted in offscreen document');
    ok(st && st.host === 'offscreen', 'engineState in storage', JSON.stringify(st));
    ok(st && st.pagesReady === true && st.pageEngine === 'chrome' && st.detail === '', 'engineState: pagesReady, on-device engine', JSON.stringify(st));
    ok(st && !('bridgeConnected' in st) && !('premium' in st), 'engineState: no desktop-app fields left', JSON.stringify(st));
    const stats = () => swEval(() => globalThis.autoTranslatorStats);
    const resetStats = () => swEval(() => { const st = globalThis.autoTranslatorStats; st.requests = 0; st.texts.length = 0; st.batches.length = 0; });

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
    const ptxt = await popup.innerText('body');
    ok(/Sprachpaket/.test(ptxt) && !/Desktop-App/.test(ptxt) && /Webseiten automatisch übersetzen/.test(ptxt) && /Bilder automatisch übersetzen/.test(ptxt), 'popup renders German UI (no desktop app)');
    ok(await until(async () => (await popup.textContent('#page-engine')) === 'bereit'), 'popup: Webseiten bereit', await popup.textContent('#page-engine'));
    const direct = await popup.evaluate(() => chrome.runtime.sendMessage({ type: 'translate', source: 'zh', target: 'en', texts: ['你好', '', '  世界 ', '第一行\n第二行', '   ', '你好'] }));
    ok(direct && direct.ok && JSON.stringify(direct.translations) === JSON.stringify(['EN[你好]', '', '  EN[世界] ', 'EN[第一行]\nEN[第二行]', '   ', 'EN[你好]']),
      'translate message: order, length, whitespace, line breaks', JSON.stringify(direct));
    ok(!(await popup.$('#use-premium')) && !/Claude|API-Schlüssel/.test(ptxt), 'popup: no premium setting, no Claude/API mention');
    console.log('INFO popup pack status (real API):', await popup.textContent('#pack-status'), '| button visible:', await popup.isVisible('#download'), '| hint visible:', await popup.isVisible('#api-hint'));
    await popup.uncheck('#enabled');
    ok(await until(async () => (await page.textContent('#h')) === '  你好世界  '), 'popup toggle switches page translation off');
    await popup.check('#enabled');
    ok(await until(async () => (await page.textContent('#h')) === '  EN[你好世界]  '), 'popup toggle switches it on again');

    // --- 6a. traditional Chinese (Taiwan, Hong Kong): converted to simplified, then translated
    {
      const tp = await ctx.newPage();
      await tp.goto(base + 'traditional.html');
      const t = (id) => tp.textContent('#' + id);
      ok(await until(async () => (await t('t2')) === 'EN[营业时间：11:00 ~ 21:00]', 8000), 'traditional: converted to simplified before translating', await t('t2'));
      ok((await t('t1')) === 'EN[【 台中西区男士发型首选 】]' && (await t('t3')).startsWith('EN[初次莅临或是没有特别指定设计师'), 'traditional: all traditional lines', await t('t1') + ' | ' + await t('t3'));
      ok((await t('s1')) === 'EN[营业时间：每天开放]', 'traditional: simplified text on the same page is left as it is', await t('s1'));
      ok((await t('n1')) === 'EN[台中]', 'traditional: text without telling characters unchanged', await t('n1'));
      ok((await tp.title()) === 'EN[线上预约]', 'traditional: page title too', await tp.title());
      ok((await t('t2')) !== '營業時間：11:00 ~ 21:00', 'traditional: works without any extra language pack');
      await tp.close();
    }
    const directHant = await popup.evaluate(() => chrome.runtime.sendMessage({ type: 'translate', source: 'zh', target: 'en', texts: ['這個', '这个'] }));
    ok(directHant && directHant.ok && directHant.translations.join('|') === 'EN[这个]|EN[这个]', 'traditional: decided per text inside one batch', JSON.stringify(directHant));

    // --- 6a2. pages that mark themselves "do not translate" (translate="no", class notranslate)
    {
      const np = await ctx.newPage();
      await np.goto(base + 'notranslate.html');
      ok(await until(async () => (await np.textContent('#p1')) === 'EN[营业时间：11:00 ~ 21:00]', 8000),
        'notranslate: a page marked translate="no" / notranslate is still translated', await np.textContent('#p1'));
      ok((await np.textContent('#p2')) === 'EN[实习师]' && (await np.getAttribute('#i1', 'placeholder')) === 'EN[请输入姓名]' && (await np.title()) === 'EN[线上预约]',
        'notranslate: marked elements, attributes and title too', await np.textContent('#p2'));
      await np.close();
    }

    // --- 6b. writing helper: type English, insert Chinese
    ok(await stateIs((x) => x.writeReady === true), 'compose: engineState writeReady with the fake translator', JSON.stringify(await engineState()));
    ok(await until(async () => (await popup.textContent('#compose-status')) === 'bereit') && await popup.isChecked('#compose'),
      'popup: Schreibhilfe bereit, switch on by default', await popup.textContent('#compose-status'));
    {
      const cp = await ctx.newPage();
      await cp.goto(base + 'compose.html');
      const bar = cp.locator('auto-translator-compose .text');
      const barText = () => bar.textContent({ timeout: 300 }).catch(() => null);
      const barShown = async () => (await cp.locator('auto-translator-compose').count()) > 0;
      const shows = (want) => until(async () => (await barText()) === want, 5000);

      await cp.click('#plain');
      await cp.keyboard.type('hello');
      ok(await shows('ZH[hello]'), 'compose: English in a text input shows the Chinese preview', await barText());
      await cp.click('auto-translator-compose .insert');
      ok(await until(async () => (await cp.inputValue('#plain')) === 'ZH[hello]'), 'compose: "Einfügen" puts the translation into the field', await cp.inputValue('#plain'));
      ok(!(await barShown()) && (await cp.evaluate(() => document.activeElement.id)) === 'plain', 'compose: bar closes, focus stays in the field');

      await cp.click('#area');
      await cp.keyboard.type('good morning');
      ok(await shows('ZH[good morning]'), 'compose: textarea preview', await barText());
      await cp.keyboard.press('Control+Shift+Enter');
      ok(await until(async () => (await cp.inputValue('#area')) === 'ZH[good morning]'), 'compose: Ctrl+Shift+Enter inserts', await cp.inputValue('#area'));

      await cp.click('#editor');
      await cp.keyboard.type('thanks');
      ok(await shows('ZH[thanks]'), 'compose: contenteditable preview', await barText());
      await cp.click('auto-translator-compose .insert');
      ok(await until(async () => (await cp.textContent('#editor')) === 'ZH[thanks]'), 'compose: contenteditable insert', await cp.textContent('#editor'));

      await cp.click('#react');
      await cp.keyboard.type('how much');
      ok(await shows('ZH[how much]'), 'compose: preview for a framework-controlled field');
      const eventsBefore = await cp.evaluate(() => window.inputEvents);
      await cp.keyboard.press('Control+Shift+Enter');
      ok(await until(async () => (await cp.evaluate(() => window.reactState)) === 'ZH[how much]'), 'compose: a controlled field sees the change through input events', await cp.evaluate(() => window.reactState));
      ok((await cp.evaluate(() => window.inputEvents)) > eventsBefore, 'compose: inserting fires an input event');

      for (const id of ['pass', 'mail', 'card']) {
        await cp.click('#' + id);
        await cp.keyboard.type('secret text');
        await sleep(900);
        ok(!(await barShown()), 'compose: no helper on #' + id + ' (password / email / card number)');
      }

      await cp.click('#search');
      await cp.keyboard.insertText('你好');
      await sleep(900);
      ok(!(await barShown()), 'compose: Chinese input shows no helper');
      await cp.fill('#area', '');
      await cp.click('#area');
      await cp.keyboard.type('hi there');
      ok(await shows('ZH[hi there]'), 'compose: helper appears again for English', await barText());
      await cp.keyboard.press('Escape');
      ok(!(await barShown()), 'compose: Escape closes the helper');
      await cp.keyboard.type(' again');
      await sleep(900);
      ok(!(await barShown()) && (await cp.inputValue('#area')) === 'hi there again', 'compose: closed helper stays closed for that field; typing is untouched', await cp.inputValue('#area'));

      await swEval(() => chrome.storage.sync.set({ compose: false }));
      await cp.fill('#plain', '');
      await cp.click('#plain');
      await cp.keyboard.type('bye');
      await sleep(900);
      ok(!(await barShown()), 'compose: switch off -> no helper');
      await swEval(() => chrome.storage.sync.set({ compose: true }));
      await cp.keyboard.type(' now');
      ok(await shows('ZH[bye now]'), 'compose: switch on again -> helper back', await barText());
      ok((await cp.textContent('#h')) === 'EN[联系卖家]', 'compose: page translation still works next to the helper', await cp.textContent('#h'));
      await cp.close();

      const en = await ctx.newPage();
      await en.goto(base + 'english.html');
      await en.click('#plain');
      await en.keyboard.type('hello');
      await sleep(900);
      ok((await en.locator('auto-translator-compose').count()) === 0, 'compose: no helper on pages without Chinese');
      await en.close();
    }

    // --- 6c. text in images: real offline OCR (models ship with the extension), fake translator
    {
      const ip = await ctx.newPage();
      await ip.setViewportSize({ width: 1000, height: 800 });
      const tImages = Date.now();
      await ip.goto(base + 'images.html');
      // Overlay boxes over the image with this id (matched by position), or null.
      const overlay = (id) => ip.evaluate((id) => {
        const img = document.getElementById(id);
        const r = img.getBoundingClientRect();
        const host = document.querySelector('auto-translator-images');
        if (!host || !host.shadowRoot) return null;
        for (const layer of host.shadowRoot.querySelectorAll('.layer')) {
          if (layer.style.display === 'none') continue;
          const lr = layer.getBoundingClientRect();
          if (Math.abs(lr.left - r.left) < 2 && Math.abs(lr.top - r.top) < 2 && Math.abs(lr.width - r.width) < 2) {
            const inner = layer.querySelector('.inner');
            return {
              off: inner.classList.contains('off'),
              boxes: [...layer.querySelectorAll('.box')].map((b) => {
                const br = b.getBoundingClientRect();
                return { t: b.textContent, x: br.left - r.left, y: br.top - r.top, w: br.width, h: br.height };
              }),
            };
          }
        }
        return null;
      }, id);
      const texts = async (id) => { const o = await overlay(id); return o ? o.boxes.map((b) => b.t).join(' | ') : null; };

      ok(await until(async () => (await texts('clear')) === 'EN[限时特价] | EN[全场包邮]', 30000),
        'images: text in a clear image is recognised and translated automatically', await texts('clear'));
      console.log('INFO first image translated after', Date.now() - tImages, 'ms (includes loading the OCR models)');
      const clear = await overlay('clear');
      const near = (a, b, tol) => Math.abs(a - b) <= tol;
      // clear.png is 640x240 shown at 320x120: the first line sits at about (21, 20), 86x21 px.
      ok(clear && near(clear.boxes[0].x, 21, 8) && near(clear.boxes[0].y, 20, 8) && near(clear.boxes[1].y, 64, 8),
        'images: boxes sit on the original text (scaled to the displayed size)', JSON.stringify(clear && clear.boxes));
      ok(await until(async () => (await texts('banner')) === 'EN[新品上市] | EN[立即购买享八折优惠]', 15000),
        'images: coloured banner with two text sizes', await texts('banner'));
      ok(await until(async () => (await texts('fit')) === 'EN[限时特价] | EN[全场包邮]', 15000), 'images: object-fit: contain image translated', await texts('fit'));
      const fit = await overlay('fit');
      // 640x240 contained in 400x300: scale .625, picture starts 75 px down; first line at y ~ 100.
      ok(fit && near(fit.boxes[0].y, 100, 8) && near(fit.boxes[0].x, 26, 8), 'images: object-fit offsets respected', JSON.stringify(fit && fit.boxes[0]));
      ok(await until(async () => (await texts('protected')) === 'EN[新品上市] | EN[立即购买享八折优惠]', 20000) && server.imageRefusals > 0,
        'images: hotlink-protected image translated through the screenshot fallback', String(await texts('protected')) + ' refusals=' + server.imageRefusals);
      await sleep(2500);
      ok((await overlay('icon')) === null, 'images: small images (icons) are skipped');
      ok((await overlay('blank')) === null, 'images: image without text gets no overlay');
      ok((await overlay('detail')) === null, 'images: images far below the viewport wait until they are scrolled near');

      await ip.evaluate(() => document.getElementById('detail').scrollIntoView());
      ok(await until(async () => (await texts('detail')) === 'EN[产品参数] | EN[面料：纯棉] | EN[尺码：均码] | EN[颜色：白色] | EN[洗涤说明] | EN[手洗不可漂白]', 30000),
        'images: long detail image (tiled OCR) translated after scrolling', await texts('detail'));
      await ip.evaluate(() => window.scrollTo(0, 0));
      ok(await until(async () => { const o = await overlay('clear'); return o && near(o.boxes[0].y, 20, 8); }, 3000), 'images: overlays follow the page when scrolling');

      await ip.locator('auto-translator-images .toggle').first().click();
      ok(await until(async () => { const o = await overlay('clear'); return o && o.off; }, 3000), 'images: "原文" button shows the original');
      await ip.locator('auto-translator-images .toggle').first().click();
      ok(await until(async () => { const o = await overlay('clear'); return o && !o.off; }, 3000), 'images: and switches back to the translation');

      await swEval(() => chrome.storage.sync.set({ images: false }));
      ok(await until(async () => (await overlay('clear')) === null && (await overlay('banner')) === null, 4000), 'images: switch off removes all overlays');
      await swEval(() => chrome.storage.sync.set({ images: true }));
      ok(await until(async () => (await texts('clear')) === 'EN[限时特价] | EN[全场包邮]', 6000), 'images: switch on shows them again (from the cache)');

      const menu = await swEval(() => new Promise((r) => chrome.contextMenus.update('translate-image', {}, () => r(!chrome.runtime.lastError))));
      ok(menu === true, 'images: right-click menu "Bild übersetzen" exists');
      await swEval(async (src) => {
        const tabs = await chrome.tabs.query({});
        const tab = tabs.find((t) => (t.url || '').includes('/images.html'));
        await chrome.tabs.sendMessage(tab.id, { type: 'images.translateNow', srcUrl: src }, { frameId: 0 });
      }, base + 'img/clear.png?icon');
      ok(await until(async () => (await texts('icon')) === 'EN[限时特价] | EN[全场包邮]', 15000), 'images: right-click "Bild übersetzen" also translates a small image', await texts('icon'));

      const tReload = Date.now();
      await ip.reload();
      ok(await until(async () => (await texts('clear')) === 'EN[限时特价] | EN[全场包邮]', 8000), 'images: after a reload the cached result appears again');
      console.log('INFO overlay after reload took', Date.now() - tReload, 'ms');
      ok((await ip.textContent('#h')) === 'EN[商品详情]', 'images: page text translation still works next to image overlays');
      await ip.close();
    }

    // --- 10. shop page: inline pieces, select, SVG, shadow roots, frames, mutation storm, batches
    await resetStats();
    const shop = await ctx.newPage();
    await shop.goto(base + 'shop.html');
    const text = (sel) => shop.evaluate((q) => document.querySelector(q).textContent, sel);
    ok(await until(async () => (await text('#order p:last-child')) === 'EN[顺序五]' && (await text('#o3')) === 'EN[大号]'), 'shop page translated');
    ok((await text('#promo')) === 'EN[满]300 EN[减]50', 'inline pieces: numbers kept, gap added before a piece that follows a digit', await text('#promo'));
    ok((await text('#latin')) === 'iPhone EN[手机壳]', 'inline pieces: gap after Latin text', await text('#latin'));
    ok((await text('#pair')) === 'Free shipping', 'inline pieces: adjacent translated spans do not run together', await text('#pair'));
    ok((await text('#flexpair')) === 'Freereturns', 'inline pieces: separate boxes (flex items) get no extra space', await text('#flexpair'));
    ok((await text('#spaced')) === 'EN[买] EN[二] EN[送] EN[一]', 'inline pieces: existing whitespace kept as is', await text('#spaced'));
    ok((await text('#price')) === '¥199.00' && !(await stats()).texts.some((t) => t.includes('199.00')), 'price without Chinese is never touched or sent');
    ok((await text('#sold')) === 'EN[已售 1.2万+]' && (await text('#range')) === 'EN[¥1,299.00 - ¥2,499.00 起]', 'number-heavy strings go through as one piece', await text('#sold') + ' | ' + await text('#range'));

    const sel = await shop.evaluate(() => {
      const q = (id) => document.getElementById(id);
      return { o1: q('o1').textContent, o1v: q('o1').value, o2: q('o2').textContent, o2v: q('o2').value, o3v: q('o3').value, og: q('og').label, value: q('sel').value };
    });
    ok(sel.o1 === 'EN[红色]' && sel.o2 === 'EN[蓝色]' && sel.og === 'EN[尺码]', 'select: option text and optgroup label translated', JSON.stringify(sel));
    ok(sel.o1v === '红色' && sel.o3v === '大号' && sel.o2v === 'b' && sel.value === 'b', 'select: submitted option values stay original', JSON.stringify(sel));

    const svg = await shop.evaluate(() => ({ t: document.getElementById('svgtext').textContent, title: document.getElementById('svgtitle').textContent, attr: document.getElementById('svgtext').hasAttribute('title') }));
    ok(svg.t === 'EN[销量]' && svg.title === 'EN[图表标题]' && !svg.attr, 'SVG <text> and <title> translated, no title attribute on SVG elements', JSON.stringify(svg));

    ok(await until(() => shop.evaluate(() => document.getElementById('late').shadowRoot && document.getElementById('late').shadowRoot.getElementById('ls').textContent === 'EN[迟到的影子]'), 8000),
      'shadow root attached after the scan is found and translated');
    ok(await until(() => shop.evaluate(() => window.__closedRoot && window.__closedRoot.textContent === 'EN[封闭的影子]'), 8000), 'closed shadow root of a custom element translated');
    await shop.evaluate(() => { document.getElementById('late').shadowRoot.getElementById('ls').textContent = '影子更新'; });
    ok(await until(() => shop.evaluate(() => document.getElementById('late').shadowRoot.getElementById('ls').textContent === 'EN[影子更新]')), 'late shadow root is observed for later changes');

    for (const [name, want] of [['f-same', 'EN[框架内容]'], ['f-cross', 'EN[框架内容]'], ['f-srcdoc', 'EN[内嵌文档]'], ['f-blank', 'EN[空白框架]']]) {
      const frame = shop.frames().find((f) => f.name() === name);
      const got = frame && await until(async () => (await frame.evaluate(() => document.getElementById('ft').textContent)) === want, 6000);
      ok(got, 'iframe translated: ' + name + (frame ? ' (' + frame.url().slice(0, 40) + ')' : ''), frame ? await frame.evaluate(() => document.body.textContent) : 'frame missing');
    }

    // mutation storm: 80 ticks, every 50 ms, on a text node and on a replaced container
    await sleep(500);
    await resetStats();
    const stormStart = Date.now();
    const storm = shop.evaluate(() => window.startStorm(80, 50));
    const staticOk = await until(async () => (await text('#static')) === 'EN[静态文本]', 3000, 50);
    ok(staticOk, 'storm: other text is still translated promptly while the page mutates every 50 ms', await text('#static'));
    await storm;
    ok(await until(async () => (await text('#tick1')) === 'EN[剩余 80 秒]' && (await text('#tick2')) === 'EN[倒计时 80 秒]', 4000), 'storm: counters end up translated with the right number',
      await text('#tick1') + ' | ' + await text('#tick2'));
    const stormStats = await stats();
    console.log('INFO storm: ' + stormStats.requests + ' requests, ' + stormStats.texts.length + ' texts for 160 text changes in ' + (Date.now() - stormStart) + ' ms');
    ok(stormStats.requests <= 8 && stormStats.texts.length <= 16, 'storm: 160 text changes cause only a handful of requests (no request storm)', stormStats.requests + ' requests / ' + stormStats.texts.length + ' texts');
    await sleep(700);
    const quiet = (await stats()).requests;
    await sleep(700);
    ok((await stats()).requests === quiet, 'storm: no requests after the page went quiet');

    // toggle off restores the shop page, including pinned option values
    await swEval(() => chrome.storage.sync.set({ enabled: false }));
    ok(await until(async () => (await text('#o1')) === '红色' && (await text('#promo')) === '满300减50' && (await text('#pair')) === '免费送货'), 'shop page: toggle off restores originals (no leftover spaces)', await text('#promo'));
    ok(await shop.evaluate(() => !document.getElementById('o1').hasAttribute('value') && document.getElementById('og').label === '尺码' &&
      document.getElementById('late').shadowRoot.getElementById('ls').textContent === '影子更新' && window.__closedRoot.textContent === '封闭的影子'),
      'shop page: toggle off removes pinned option values, restores labels and shadow roots');
    await swEval(() => chrome.storage.sync.set({ enabled: true }));
    ok(await until(async () => (await text('#o1')) === 'EN[红色]'), 'shop page: toggle on again');

    // large page: batch limits
    await resetStats();
    await shop.evaluate(() => {
      const d = document.createElement('div'); d.id = 'big';
      for (let i = 0; i < 200; i++) { const p = document.createElement('p'); p.textContent = '这是第' + i + '个商品的详细描述，'.repeat(14); d.appendChild(p); }
      document.body.appendChild(d);
    });
    ok(await until(() => shop.evaluate(() => [...document.querySelectorAll('#big p')].every((p) => p.textContent.startsWith('EN['))), 15000), 'large page: 200 long texts translated');
    const sizes = (await stats()).batches;
    ok(sizes.length > 1 && sizes.every(([n, c]) => n <= 60 && (c <= 4000 || n === 1)), 'large page: batches stay within 60 texts / 4000 characters', JSON.stringify(sizes));
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
      ok(Array.isArray(forged) && forged[7] && forged[7].ok === true && forged[7].translations[0] === 'EN[验证]', 'service worker still serves a well-formed page batch', JSON.stringify(forged));
      const forgedOcr = await csEval(`Promise.all([
        chrome.runtime.sendMessage({ type: 'ocrImage', url: 'file:///etc/passwd' }),
        chrome.runtime.sendMessage({ type: 'ocrImage', url: 'chrome://settings/' }),
        chrome.runtime.sendMessage({ type: 'ocrImage', url: 'javascript:alert(1)' }),
        chrome.runtime.sendMessage({ type: 'ocrImage', url: 'https://x/' + 'a'.repeat(9000) }),
        chrome.runtime.sendMessage({ type: 'translate', source: 'fr', target: 'en', texts: ['x'] }),
      ].map((p) => p.catch(() => 'threw')))`);
      ok(Array.isArray(forgedOcr) && forgedOcr.every((r) => r && r.ok === false && r.error === 'bad-request'),
        'service worker rejects image URLs other than http(s) and unsupported language pairs', JSON.stringify(forgedOcr));
      await sleep(500);
      ok(JSON.stringify(await engineState()) === stateBefore && (await local('engineMode')).engineMode === 'offscreen' && await swEval(() => chrome.offscreen.hasDocument()),
        'forged messages changed neither the engine state nor the engine host');
    }
    await cdp.detach();
    await page.evaluate(() => { document.getElementById('dyn').textContent = '页面变量无效'; });
    ok(await until(async () => (await snap()).dyn === 'EN[页面变量无效]'), 'page-defined __fakeTranslator / debug flags have no effect');

    // --- 13. language pack missing: pages wait, then resume without reload
    await swEval(() => chrome.storage.local.set({ debugFakeTranslator: 'missing' }));
    const ms = await stateIs((x) => x.apiPresent && x.availability === 'downloadable' && x, 15000);
    ok(ms && ms.ready === false && ms.pagesReady === false && ms.detail === 'Sprachpaket fehlt', 'pack missing: engineState ready:false, detail "Sprachpaket fehlt"', JSON.stringify(await engineState()));
    ok(await stateIs((x) => x.writeReady === false), 'pack missing: writing helper not ready', JSON.stringify(await engineState()));
    const page2 = await ctx.newPage();
    await page2.goto(base + 'basic.html');
    await sleep(1500);
    ok((await page2.textContent('#h')) === '  你好世界  ', 'pack missing: page text is left alone');
    const tResume = Date.now();
    await swEval(() => chrome.storage.local.set({ debugFakeTranslator: 'all' }));
    ok(await until(async () => (await page2.textContent('#h')) === '  EN[你好世界]  ', 15000), 'pack ready: the waiting page resumes without reload', await page2.textContent('#h'));
    console.log('INFO resume after the pack became ready took', Date.now() - tResume, 'ms');
    await page2.close();

    // --- 7. tab fallback host (fake only in the tab)
    await swEval(() => chrome.storage.local.set({ debugFakeTranslator: 'tab' }));
    ok(await stateIs((x) => x.host === 'tab' && x.ready, 15000), 'tab fallback: engine runs in the tab host', JSON.stringify(await engineState()));
    ok((await local('engineMode')).engineMode === 'tab' && !(await swEval(() => chrome.offscreen.hasDocument())), 'tab fallback: mode tab, offscreen closed');
    const info = await swEval(async (u) => (await chrome.tabs.query({})).filter((t) => (t.url || '').startsWith(u)).map((t) => ({ pinned: t.pinned, active: t.active, id: t.id })), `chrome-extension://${extId}/engine.html`);
    ok(info.length === 1 && info[0].pinned && !info[0].active, 'tab fallback: exactly one pinned inactive engine tab', JSON.stringify(info));
    await page.evaluate(() => { document.getElementById('dyn').textContent = '标签页模式'; });
    ok(await until(async () => (await snap()).dyn === 'EN[标签页模式]'), 'tab fallback: page translation works');
    await swEval((id) => chrome.tabs.remove(id), info[0].id);
    const engineTabs = () => swEval(async (u) => (await chrome.tabs.query({})).filter((t) => (t.url || '').startsWith(u) && t.id !== undefined).map((t) => t.id), `chrome-extension://${extId}/engine.html`);
    ok(await until(async () => { const ids = await engineTabs(); return ids.length === 1 && ids[0] !== info[0].id; }, 15000), 'tab fallback: engine tab re-created after being closed');
    await sleep(1500);
    ok((await engineTabs()).length === 1, 'tab fallback: still exactly one engine tab');
    await page.evaluate(() => { document.getElementById('dyn').textContent = '重新打开之后'; });
    ok(await until(async () => (await snap()).dyn === 'EN[重新打开之后]', 15000), 'tab fallback: translation works again after the engine tab was re-created');

  } catch (e) {
    console.log('FAIL exception', e);
    failed++;
  } finally {
    await ctx.close().catch(() => {});
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
