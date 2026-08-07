(function () {
  'use strict';

  const APP_URL = 'http://127.0.0.1:8765';
  const GROUP_ID = 'resume-gen-btn-group';
  const BLUE_BTN_ID = 'resume-gen-blue';
  const PURPLE_BTN_ID = 'resume-gen-purple';
  const MIN_JD_LENGTH = 200;
  const DEFAULT_TOP_PCT = 2.5;
  const GROUP_HEIGHT = 90;
  const STORAGE_KEY_TOP = 'resumeGenGroupTop';

  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  function isChatGPTUrl() {
    var u = (window.location.hostname || '').toLowerCase();
    return u.indexOf('openai.com') !== -1 || u.indexOf('chatgpt.com') !== -1 || u.indexOf('chat.com') !== -1;
  }

  // ===========================================================================
  // JOB-DESCRIPTION EXTRACTION  (DevStrider's per-site selectors + scoring)
  // ===========================================================================
  const SITE_SELECTORS = {
    'recruitcrm.io': ['[class*="job-description"]', '[class*="JobDescription"]', '.job-detail', '.description', '[data-testid*="description"]', 'article', 'main', '[role="main"]'],
    'www.linkedin.com': ['.jobs-description__content', '.jobs-description', '[class*="jobs-description"]', '.description__text', 'section.jobs-description-content', 'article'],
    'linkedin.com': ['.jobs-description__content', '.jobs-description', '[class*="jobs-description"]', 'article'],
    'www.indeed.com': ['#job-description-container', '.jobsearch-JobComponent-description', '[class*="jobsearch-JobComponent"]', '#jobDescriptionText', '.job_snippet'],
    'indeed.com': ['#job-description-container', '.jobsearch-JobComponent-description', '#jobDescriptionText'],
    'greenhouse.io': ['.job-post', '.job__description', '[class*="job-post"]', '#job-description', '.job-description', '#content'],
    'jobs.lever.co': ['.content', '.posting-page', '[class*="posting"]', '.job-description', 'section'],
    'lever.co': ['.content', '.posting-page', '.job-description'],
    'taleo.net': ['.col-xs-12.col-sm-12', '[class*="col-xs-12"]', '#requisitionDescriptionInterface', '[id*="requisitionDescription"]', '.requisitionDescription', '[class*="requisitionDescription"]', '#job-description', '.job-description', '[id*="jobdescription"]', '[class*="jobdescription"]', 'article', 'main', '[role="main"]'],
  };
  const GENERIC_SELECTORS = ['[data-job-description]', '.job-description', '.job-description-content', '[class*="job-description"]', '[class*="JobDescription"]', 'article', '[role="main"]', 'main', '.content__body', '.description', '#job-description', '.job-detail', '[class*="description"]'];

  function getText(el) { return el ? (el.innerText || el.textContent || '').trim() : ''; }

  function trySelectors(selectors, minLen) {
    try {
      var elements = document.querySelectorAll(selectors.join(','));
      for (var i = 0; i < elements.length; i++) {
        var text = getText(elements[i]);
        if (text.length >= (minLen || 100)) return text;
      }
    } catch (e) {
      for (var j = 0; j < selectors.length; j++) {
        try {
          var el = document.querySelector(selectors[j]);
          if (el) { var t = getText(el); if (t.length >= (minLen || 100)) return t; }
        } catch (e2) { /* ignore */ }
      }
    }
    return '';
  }

  function getMainContentText() {
    var main = document.querySelector('main') || document.querySelector('article') || document.querySelector('[role="main"]') || document.body;
    if (!main) return '';
    var candidates = [];
    function walk(node, depth) {
      if (depth > 15 || node.nodeType !== Node.ELEMENT_NODE) return;
      var tag = (node.tagName || '').toLowerCase();
      if (['script', 'style', 'nav', 'header', 'footer', 'form', 'select', 'option', 'datalist'].indexOf(tag) !== -1) return;
      if (node.getAttribute('role') === 'listbox' || node.getAttribute('role') === 'combobox') return;
      var text = getText(node);
      if (text.length < 50 || text.length > 50000) return;
      var linkCount = (node.querySelectorAll('a') || []).length;
      if (linkCount > 0 && linkCount * 50 > text.length) return;
      candidates.push({ text: text, len: text.length });
      var children = node.children || [];
      for (var j = 0; j < children.length; j++) walk(children[j], depth + 1);
    }
    walk(main, 0);
    if (candidates.length === 0) return getText(main);
    candidates.sort(function (a, b) { return b.len - a.len; });
    return candidates[0] ? candidates[0].text : getText(main);
  }

  function scoreElement(el) {
    var text = getText(el), score = 0;
    if (text.length >= 500 && text.length <= 5000) score += 50; else if (text.length >= 200) score += 20;
    var lower = text.toLowerCase();
    ['responsibilities', 'requirements', 'qualifications', 'experience', 'skills', 'role', 'position', 'job', 'duties', 'description'].forEach(function (k) { if (lower.indexOf(k) !== -1) score += 10; });
    ['home', 'about', 'contact', 'login', 'sign up', 'sign in', 'register', 'menu'].forEach(function (k) { if (lower.indexOf(k) !== -1) score -= 5; });
    var linkCount = (el.querySelectorAll('a') || []).length;
    if (linkCount > 10 && linkCount * 30 > text.length) score -= 20;
    return score;
  }

  function extractJobDescription() {
    var host = document.location.hostname || '';
    var text = '';
    var siteSelectors = SITE_SELECTORS[host] || null;
    if (!siteSelectors) {
      var keys = Object.keys(SITE_SELECTORS).sort(function (a, b) { return b.length - a.length; });
      for (var si = 0; si < keys.length; si++) { if (host.indexOf(keys[si]) !== -1) { siteSelectors = SITE_SELECTORS[keys[si]]; break; } }
    }
    if (siteSelectors) text = trySelectors(siteSelectors, 100);
    if (!text) text = trySelectors(GENERIC_SELECTORS, 100);
    if (!text || text.length < MIN_JD_LENGTH) {
      var candidates = [];
      var containers = document.querySelectorAll('article, main, section, div[class*="job"], div[class*="description"], div[class*="posting"]');
      for (var i = 0; i < containers.length; i++) {
        var sc = scoreElement(containers[i]);
        if (sc > 0) candidates.push({ score: sc, text: getText(containers[i]) });
      }
      candidates.sort(function (a, b) { return b.score - a.score; });
      if (candidates.length > 0 && candidates[0].text.length >= MIN_JD_LENGTH) text = candidates[0].text;
    }
    if (!text) text = getMainContentText();
    if ((!text || text.length < MIN_JD_LENGTH) && document.body) {
      var body = document.body.innerText || document.body.textContent || '';
      if (body.length >= MIN_JD_LENGTH) text = body;
    }
    return text || '';
  }

  // ===========================================================================
  // FAST-FEED + ASSISTANT HARVEST (for the manual purple button)
  // ===========================================================================
  function extractLastAssistantMessage() {
    var nodes = document.querySelectorAll('[data-message-author-role="assistant"]');
    if (!nodes || nodes.length === 0) return '';
    return getText(nodes[nodes.length - 1]);
  }
  function parseFastFeedLine(line) {
    var t = String(line || '').trim();
    if (!t) return null;
    var core = t;
    if (core.charAt(0) === '[' && core.charAt(core.length - 1) === ']') core = core.slice(1, -1).trim();
    var parts = core.split(',').map(function (p) { return p.trim(); }).filter(function (p) { return p.length > 0; });
    if (parts.length < 3) return null;
    return { resumeId: parts[0], company: parts[1], role: parts[2], primaryStacks: parts.slice(3) };
  }
  function splitTrailingFastFeed(text) {
    var lines = String(text || '').split(/\r?\n/);
    for (var i = lines.length - 1; i >= 0; i--) {
      var line = lines[i].trim();
      if (!line) continue;
      if (parseFastFeedLine(line)) return { resumePart: lines.slice(0, i).join('\n').replace(/\s+$/, ''), fastFeedLine: line };
    }
    return { resumePart: String(text || '').trim(), fastFeedLine: '' };
  }

  // ===========================================================================
  // CHATGPT DOM INJECTION  (ResumeAuto — no clipboard, background-safe)
  // ===========================================================================
  async function injectText(text) {
    var newChatBtn = document.querySelector('[data-testid="create-new-chat-button"]');
    if (newChatBtn) newChatBtn.click();
    await sleep(2500);

    var inputEl = document.querySelector('#prompt-textarea');
    if (!inputEl) throw new Error('ChatGPT input box not found');
    inputEl.focus();
    inputEl.innerHTML = '';
    inputEl.innerText = text;
    inputEl.dispatchEvent(new Event('input', { bubbles: true }));

    var sendBtn = null, attempts = 0;
    while (attempts < 40) {
      sendBtn = document.querySelector('#composer-submit-button') || document.querySelector('[data-testid="send-button"]');
      if (sendBtn && !sendBtn.disabled) break;
      sendBtn = null; await sleep(100); attempts++;
    }
    if (!sendBtn) throw new Error('ChatGPT send button never enabled');
    await sleep(800);
    inputEl.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true }));
  }

  function harvestWhenComplete(timeoutMs) {
    return new Promise(function (resolve, reject) {
      var to = setTimeout(function () { obs.disconnect(); reject(new Error('ChatGPT generation timed out')); }, timeoutMs || 180000);
      var obs = new MutationObserver(async function () {
        // Completion signal: the "Start Voice" button reappears when streaming finishes.
        var done = document.querySelector('button[aria-label="Start Voice"]');
        if (!done) return;
        clearTimeout(to); obs.disconnect();
        await sleep(1000);
        var msgs = document.querySelectorAll('[data-message-author-role="assistant"]');
        var last = msgs[msgs.length - 1];
        if (!last) { reject(new Error('No assistant message found')); return; }
        var txt = '';
        var els = last.querySelectorAll('p, pre, li, h1, h2, h3, h4, h5, h6');
        if (els.length > 0) els.forEach(function (e) { txt += e.innerText + '\n'; }); else txt = last.innerText;
        resolve(txt.replace(/\n{3,}/g, '\n\n').trim());
      });
      obs.observe(document.querySelector('main') || document.body, { childList: true, subtree: true });
    });
  }

  async function injectAndHarvest(text) {
    await injectText(text);
    await sleep(2500);
    return await harvestWhenComplete();
  }

  // ===========================================================================
  // BATCH ENGINE  (runs only on a ChatGPT tab; the tab being open IS the engine)
  // ===========================================================================
  var batchBusy = false;

  function scrapeJdViaBackground(url) {
    return new Promise(function (resolve, reject) {
      chrome.runtime.sendMessage({ type: 'RESUME_SCRAPE_JD', url: url }, function (resp) {
        if (chrome.runtime.lastError) return reject(new Error(chrome.runtime.lastError.message));
        if (resp && resp.ok) resolve(resp.jd || ''); else reject(new Error((resp && resp.error) || 'scrape failed'));
      });
    });
  }
  function postResult(jobId, jd, resumeText) {
    return fetch(APP_URL + '/resume/result', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ jobId: jobId, jobDescription: jd, resumeText: resumeText })
    }).catch(function () {});
  }
  function postFail(jobId, error) {
    return fetch(APP_URL + '/resume/fail', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ jobId: jobId, error: error })
    }).catch(function () {});
  }

  async function batchTick() {
    if (batchBusy) return;
    batchBusy = true;
    try {
      var res = await fetch(APP_URL + '/resume/next-job');
      if (res.status === 204 || !res.ok) return;          // nothing queued / batch paused
      var job = await res.json();
      if (!job || !job.jobId) return;

      var jd = '';
      try { jd = await scrapeJdViaBackground(job.url); }
      catch (e) { await postFail(job.jobId, 'JD scrape failed: ' + e.message); return; }
      if (!jd || jd.trim().length < 100) { await postFail(job.jobId, 'Job description too short or not found'); return; }

      var combined = (job.prompt ? job.prompt + '\n\n' : '') + '--- JOB DESCRIPTION ---\n' + jd.trim();
      var resumeText = '';
      try { resumeText = await injectAndHarvest(combined); }
      catch (e) { await postFail(job.jobId, 'ChatGPT: ' + e.message); return; }

      await postResult(job.jobId, jd.trim(), resumeText);
    } catch (e) {
      // App down / network hiccup — stay quiet, retry next tick.
    } finally {
      batchBusy = false;
    }
  }

  // ===========================================================================
  // MESSAGE HANDLER (manual blue button asks us to inject)
  // ===========================================================================
  chrome.runtime.onMessage.addListener(function (msg, sender, sendResponse) {
    if (msg && msg.type === 'INJECT_TEXT') {
      injectText(String(msg.text || '')).then(function () { sendResponse({ ok: true }); })
        .catch(function (e) { sendResponse({ ok: false, error: e.message }); });
      return true;
    }
  });

  // ===========================================================================
  // FLOATING BUTTONS (manual single-bid path — preserved)
  // ===========================================================================
  const DEFAULT_BLUE_LABEL = 'Send JD to ChatGPT (Ctrl+click to use selected text)';
  var _busy = false;

  function updateBlueStatus(status) {
    var btn = document.getElementById(BLUE_BTN_ID);
    if (!btn) return;
    btn.title = status || DEFAULT_BLUE_LABEL;
  }

  var SVG_ICONS = {
    clipboard: '<svg viewBox="0 0 384 512" fill="currentColor" width="16" height="16"><path d="M336 64h-80c0-35.3-28.7-64-64-64s-64 28.7-64 64H48C21.5 64 0 85.5 0 112v352c0 26.5 21.5 48 48 48h288c26.5 0 48-21.5 48-48V112c0-26.5-21.5-48-48-48zM96 424c-13.3 0-24-10.7-24-24s10.7-24 24-24 24 10.7 24 24-10.7 24-24 24zm0-96c-13.3 0-24-10.7-24-24s10.7-24 24-24 24 10.7 24 24-10.7 24-24 24zm0-96c-13.3 0-24-10.7-24-24s10.7-24 24-24 24 10.7 24 24-10.7 24-24 24zm96-192c13.3 0 24 10.7 24 24s-10.7 24-24 24-24-10.7-24-24 10.7-24 24-24z"/></svg>',
    fileWord: '<svg viewBox="0 0 384 512" fill="currentColor" width="16" height="16"><path d="M224 136V0H24C10.7 0 0 10.7 0 24v464c0 13.3 10.7 24 24 24h336c13.3 0 24-10.7 24-24V160H248c-13.2 0-24-10.8-24-24z"/></svg>',
    spinner: '<svg viewBox="0 0 512 512" fill="currentColor" width="16" height="16" style="animation:bid-spin .8s linear infinite"><path d="M304 48a48 48 0 1 1-96 0 48 48 0 0 1 96 0zm-48 368a48 48 0 1 0 0 96 48 48 0 0 0 0-96zm208-208a48 48 0 1 0 0 96 48 48 0 0 0 0-96zM96 256a48 48 0 1 0-96 0 48 48 0 0 0 96 0z"/></svg>',
    check: '<svg viewBox="0 0 512 512" fill="currentColor" width="16" height="16"><path d="M504 256c0 137-111 248-248 248S8 393 8 256 119 8 256 8s248 111 248 248zM227 387l184-184c6-6 6-16 0-23l-22-22c-7-7-17-7-23 0L216 308l-70-70c-7-6-17-6-23 0l-22 22c-7 7-7 17 0 23l104 104c6 6 16 6 22 0z"/></svg>',
    xmark: '<svg viewBox="0 0 512 512" fill="currentColor" width="16" height="16"><path d="M256 8C119 8 8 119 8 256s111 248 248 248 248-111 248-248S393 8 256 8zm121 313c5 5 5 12 0 17l-39 39c-5 5-12 5-17 0l-65-66-65 66c-5 5-12 5-17 0l-39-39c-5-5-5-12 0-17l66-65-66-65c-5-5-5-12 0-17l39-39c5-5 12-5 17 0l65 66 65-66c5-5 12-5 17 0l39 39c5 5 5 12 0 17l-66 65 66 65z"/></svg>'
  };

  function updatePurpleStatus(status, iconKey, color) {
    var btn = document.getElementById(PURPLE_BTN_ID);
    if (!btn) return;
    var icon = btn.querySelector('span');
    if (!icon) return;
    icon.innerHTML = SVG_ICONS[iconKey] || SVG_ICONS.fileWord;
    icon.style.color = color || 'rgba(255,255,255,0.9)';
    btn.title = status || 'Update Word & record bid in DevStrider';
  }

  function setButtonStates() {
    var onChatGPT = isChatGPTUrl();
    var blueBtn = document.getElementById(BLUE_BTN_ID);
    var purpleBtn = document.getElementById(PURPLE_BTN_ID);
    if (blueBtn) { blueBtn.disabled = onChatGPT; blueBtn.style.opacity = onChatGPT ? '0.4' : '1'; blueBtn.style.cursor = onChatGPT ? 'not-allowed' : 'pointer'; }
    if (purpleBtn) { purpleBtn.disabled = !onChatGPT; purpleBtn.style.opacity = onChatGPT ? '1' : '0.4'; purpleBtn.style.cursor = onChatGPT ? 'pointer' : 'not-allowed'; }
  }

  function createButtonGroup() {
    if (document.getElementById(GROUP_ID)) return;
    if (!document.getElementById('bid-assistant-style')) {
      var style = document.createElement('style');
      style.id = 'bid-assistant-style';
      style.textContent = '@keyframes bid-spin{100%{transform:rotate(360deg)}}';
      (document.head || document.documentElement).appendChild(style);
    }
    var group = document.createElement('div');
    group.id = GROUP_ID;
    group.style.cssText = 'position:fixed!important;right:0!important;left:auto!important;top:' + DEFAULT_TOP_PCT + '%!important;margin:0!important;padding:0!important;z-index:2147483647!important;display:flex!important;flex-direction:column!important;align-items:stretch!important;user-select:none!important;direction:ltr!important;background:linear-gradient(180deg,#18181b 0%,#0f0f11 100%)!important;border:1px solid rgba(255,255,255,0.06)!important;border-right:none!important;border-radius:10px 0 0 10px!important;box-shadow:0 0 0 1px rgba(0,0,0,0.3),-6px 4px 24px rgba(0,0,0,0.25)!important;overflow:hidden!important;';

    var dragHandle = document.createElement('div');
    dragHandle.style.cssText = 'width:40px;height:10px;cursor:grab;display:flex;align-items:center;justify-content:center;color:rgba(255,255,255,0.25);font-size:6px;flex-shrink:0;border-bottom:1px solid rgba(255,255,255,0.04);';
    dragHandle.textContent = '⋯';
    dragHandle.title = 'Drag to move';

    var blueBtn = document.createElement('button');
    blueBtn.id = BLUE_BTN_ID; blueBtn.type = 'button'; blueBtn.title = DEFAULT_BLUE_LABEL;
    blueBtn.style.cssText = 'all:initial;display:flex;align-items:center;justify-content:center;width:40px;height:40px;border:none;border-bottom:1px solid rgba(255,255,255,0.04);background:transparent;cursor:pointer;';
    blueBtn.innerHTML = '<span style="color:rgba(255,255,255,0.9);display:flex;pointer-events:none;">' + SVG_ICONS.clipboard + '</span>';

    var purpleBtn = document.createElement('button');
    purpleBtn.id = PURPLE_BTN_ID; purpleBtn.type = 'button'; purpleBtn.title = 'Update Word & record bid in DevStrider';
    purpleBtn.style.cssText = 'all:initial;display:flex;align-items:center;justify-content:center;width:40px;height:40px;border:none;background:transparent;cursor:pointer;';
    purpleBtn.innerHTML = '<span style="color:rgba(255,255,255,0.9);display:flex;pointer-events:none;">' + SVG_ICONS.fileWord + '</span>';

    [blueBtn, purpleBtn].forEach(function (b) {
      b.addEventListener('mouseenter', function () { if (!b.disabled) b.style.background = 'rgba(255,255,255,0.08)'; });
      b.addEventListener('mouseleave', function () { b.style.background = 'transparent'; });
    });
    blueBtn.addEventListener('click', onBlueClick);
    purpleBtn.addEventListener('click', onPurpleClick);

    group.appendChild(dragHandle); group.appendChild(blueBtn); group.appendChild(purpleBtn);

    var dragStartY = 0, dragStartTopPx = 0;
    dragHandle.addEventListener('mousedown', function (e) {
      e.preventDefault(); e.stopPropagation();
      dragStartY = e.clientY; dragStartTopPx = group.getBoundingClientRect().top;
      dragHandle.style.cursor = 'grabbing';
      document.addEventListener('mousemove', onDrag); document.addEventListener('mouseup', onDragEnd);
    });
    function onDrag(e) {
      var dy = e.clientY - dragStartY;
      var maxTopPx = Math.max(0, window.innerHeight - GROUP_HEIGHT - 15);
      group.style.top = Math.max(0, Math.min(maxTopPx, dragStartTopPx + dy)) + 'px';
    }
    function onDragEnd() {
      dragHandle.style.cursor = 'grab';
      document.removeEventListener('mousemove', onDrag); document.removeEventListener('mouseup', onDragEnd);
      chrome.storage.local.set({ [STORAGE_KEY_TOP]: (group.getBoundingClientRect().top / window.innerHeight) * 100 });
    }

    (document.documentElement || document.body).appendChild(group);
    setButtonStates();
    chrome.storage.local.get([STORAGE_KEY_TOP], function (r) {
      var stored = r[STORAGE_KEY_TOP];
      if (typeof stored === 'number' && stored >= 0) group.style.top = Math.min(stored, 92) + '%';
    });
  }

  // Blue button: extract JD (or selection) → inject into ChatGPT via DOM (no clipboard).
  function onBlueClick(e) {
    var btn = document.getElementById(BLUE_BTN_ID);
    if (!btn || btn.disabled || _busy) return;

    var jd = '';
    if (e && e.ctrlKey) {
      jd = (window.getSelection() || '').toString().trim();
      if (!jd || jd.length < MIN_JD_LENGTH) { updateBlueStatus('Select the JD text first, then Ctrl+click'); return; }
    } else {
      jd = extractJobDescription();
      if (!jd || jd.trim().length < MIN_JD_LENGTH) { updateBlueStatus('JD too short — select text & Ctrl+click'); return; }
    }

    _busy = true;
    updateBlueStatus('Sending to ChatGPT…');
    chrome.storage.local.set({
      devstriderPending: { url: window.location.href, jobDescription: jd, savedAt: Date.now() }
    });
    chrome.runtime.sendMessage({ type: 'START_GENERATE', jd: jd }, function () {
      _busy = false;
      updateBlueStatus(chrome.runtime.lastError ? 'Error: ' + chrome.runtime.lastError.message : 'Sent to ChatGPT');
    });
  }

  // Purple button: harvest the ChatGPT reply → run Word macro + record the bid (manual path).
  function onPurpleClick() {
    var btn = document.getElementById(PURPLE_BTN_ID);
    if (!btn || btn.disabled) return;
    btn.disabled = true;
    updatePurpleStatus('Processing…', 'spinner', 'rgba(255,255,255,0.85)');

    var gptFull = extractLastAssistantMessage();
    var split = splitTrailingFastFeed(gptFull);
    chrome.runtime.sendMessage(
      { type: 'REFRESH_WORD', gptResumeContent: split.resumePart, fastFeedInput: split.fastFeedLine },
      function (response) {
        setButtonStates();
        if (chrome.runtime.lastError) {
          updatePurpleStatus('Error: ' + chrome.runtime.lastError.message, 'xmark', 'rgba(255,255,255,0.5)');
        } else if (response && response.ok) {
          // Word refresh and DevStrider recording are independent now — report both outcomes.
          // The DevStrider write is the one that matters most: it's never gated on Word.
          var word = response.word || { ok: false };
          var ds = response.devStrider || { ok: false };
          if (word.ok && ds.ok) {
            updatePurpleStatus('Word + DevStrider OK!', 'check', 'rgba(255,255,255,0.95)');
          } else if (ds.ok) {
            updatePurpleStatus(('DevStrider OK · Word: ' + (word.error || 'failed')).slice(0, 44), 'xmark', 'rgba(255,193,7,0.95)');
          } else if (word.ok) {
            updatePurpleStatus(('Word OK · DevStrider: ' + (ds.error || 'failed')).slice(0, 44), 'xmark', 'rgba(255,193,7,0.95)');
          } else {
            updatePurpleStatus(('Word: ' + (word.error || 'failed') + ' · DevStrider: ' + (ds.error || 'failed')).slice(0, 60), 'xmark', 'rgba(255,255,255,0.5)');
          }
        } else {
          updatePurpleStatus('Error: ' + ((response && response.error) || 'app not running'), 'xmark', 'rgba(255,255,255,0.5)');
        }
        setTimeout(function () { updatePurpleStatus('Update Word & record bid in DevStrider', 'fileWord', 'rgba(255,255,255,0.9)'); }, 3500);
      }
    );
  }

  // ===========================================================================
  // BOOT
  // ===========================================================================
  createButtonGroup();

  var lastUrl = location.href;
  var observer = new MutationObserver(function () {
    if (!document.getElementById(GROUP_ID) && document.body) createButtonGroup();
    else if (location.href !== lastUrl) { lastUrl = location.href; setButtonStates(); }
  });
  observer.observe(document.documentElement, { childList: true, subtree: true });

  // The batch engine only lives on ChatGPT tabs — keep one open + logged in to run batches.
  if (isChatGPTUrl()) {
    setInterval(batchTick, 4000);
  }
})();
