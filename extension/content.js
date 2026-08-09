(function () {
  'use strict';

  // No APP_URL here on purpose: every call to the desktop app goes through the service worker,
  // so the content script never talks to localhost directly.
  const GROUP_ID = 'resume-gen-btn-group';
  const BTN_ID = 'devstrider-bid-btn';
  const MIN_JD_LENGTH = 200;
  const DEFAULT_TOP_PCT = 2.5;
  /** Drag handle (10px) + the single button (40px). Used to clamp dragging to the viewport. */
  const GROUP_HEIGHT = 50;
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
  // ASSISTANT MESSAGE HARVEST
  // ===========================================================================
  function extractLastAssistantMessage() {
    var nodes = document.querySelectorAll('[data-message-author-role="assistant"]');
    if (!nodes || nodes.length === 0) return '';
    return getText(nodes[nodes.length - 1]);
  }
  // Fast-feed parsing lives in the desktop app now (FastFeed.SplitTrailing) — the extension
  // ships the reply verbatim rather than keeping a second implementation in sync.

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


  // ===========================================================================
  // GENERATION STATE  (this script runs in the ChatGPT tab, which is nearly always hidden)
  // ===========================================================================

  /**
   * True while ChatGPT is still producing a reply. Checked against several signals because any
   * single one breaks whenever the UI is redesigned — a stop button under either of its known
   * test ids, or the absence of the voice button that only returns once streaming ends.
   */
  function isStreaming() {
    if (document.querySelector('[data-testid="stop-button"]')) return true;
    if (document.querySelector('button[aria-label*="Stop" i]')) return true;
    // Voice button is present only when idle; its absence means a reply is in flight.
    var idle = document.querySelector('button[aria-label="Start Voice"]') ||
               document.querySelector('[data-testid="composer-speech-button"]');
    return !idle;
  }

  // ===========================================================================
  // MESSAGE HANDLER (the service worker drives this tab remotely)
  // ===========================================================================
  chrome.runtime.onMessage.addListener(function (msg, sender, sendResponse) {
    // Put the prompt in and submit it. Returns as soon as it is sent — waiting out the answer
    // is the service worker's job. The caller stays on the job page; nothing here takes focus.
    if (msg && msg.type === 'INJECT_PROMPT') {
      injectText(String(msg.text || ''))
        .then(function () { sendResponse({ ok: true }); })
        .catch(function (e) { sendResponse({ ok: false, error: (e && e.message) || String(e) }); });
      return true;
    }

    // A synchronous DOM read, answered the instant the message arrives.
    //
    // This replaced a setInterval that used to live here. Chrome throttles timers in hidden tabs
    // to once a second, and to once a *minute* once the tab has been hidden for five minutes —
    // which a ChatGPT tab left open in the background always has been. That is why generation
    // looked instant when you switched to the tab and took minutes when you didn't. Message
    // delivery is not throttled, so the poll now runs in the service worker and this side only
    // answers questions.
    if (msg && msg.type === 'GET_GENERATION_STATE') {
      var text = '';
      var streaming = false;
      try { text = extractLastAssistantMessage() || ''; } catch (e) { text = ''; }
      try { streaming = isStreaming(); } catch (e) { streaming = false; }
      sendResponse({ ok: true, text: text, streaming: streaming });
      return true;
    }
  });

  // ===========================================================================
  // THE BUTTON  (one, on job pages)
  // ===========================================================================
  const DEFAULT_LABEL = 'Bid this job — generate resume + record (Ctrl+click to use selected text)';
  var _busy = false;

  /**
   * Text selected on the page, captured on mousedown.
   *
   * Clicking a <button> collapses the document selection as part of mousedown's default
   * action, so by the time the click handler runs window.getSelection() is already empty —
   * which made Ctrl+click silently do nothing. Listeners run before the default action, so
   * reading it here still sees what the user highlighted.
   */
  var _selectionAtMouseDown = '';

  var TOAST_ID = 'devstrider-toast';
  var _toastTimer = null;

  /**
   * Show the message on screen, not just in the button's tooltip. Every rejection used to be
   * tooltip-only, so "nothing happened" and "your selection was too short" looked identical.
   */
  function showToast(message, color) {
    if (!message) return;
    var el = document.getElementById(TOAST_ID);
    if (!el) {
      el = document.createElement('div');
      el.id = TOAST_ID;
      (document.documentElement || document.body).appendChild(el);
    }
    el.style.cssText = 'all:initial;position:fixed!important;right:52px!important;top:2.5%!important;' +
      'z-index:2147483647!important;max-width:320px!important;padding:8px 12px!important;' +
      'border-radius:8px!important;background:#18181b!important;' +
      'border:1px solid rgba(255,255,255,0.10)!important;' +
      'box-shadow:0 4px 20px rgba(0,0,0,0.35)!important;' +
      'font-family:-apple-system,Segoe UI,Roboto,sans-serif!important;font-size:12px!important;' +
      'line-height:1.4!important;white-space:normal!important;pointer-events:none!important;' +
      'color:' + (color || 'rgba(255,255,255,0.92)') + '!important;';
    el.textContent = message;
    if (_toastTimer) clearTimeout(_toastTimer);
    // Long enough to read a failure; generation messages are replaced by the outcome anyway.
    _toastTimer = setTimeout(function () { if (el && el.parentNode) el.parentNode.removeChild(el); }, 7000);
  }

  function updateStatus(status, iconKey, color, toast) {
    var btn = document.getElementById(BTN_ID);
    if (btn) {
      btn.title = status || DEFAULT_LABEL;
      var icon = btn.querySelector('span');
      if (icon && iconKey) {
        icon.innerHTML = SVG_ICONS[iconKey] || SVG_ICONS.clipboard;
        icon.style.color = color || 'rgba(255,255,255,0.9)';
      }
    }
    if (toast !== false) showToast(status, color);
  }

  var SVG_ICONS = {
    clipboard: '<svg viewBox="0 0 384 512" fill="currentColor" width="16" height="16"><path d="M336 64h-80c0-35.3-28.7-64-64-64s-64 28.7-64 64H48C21.5 64 0 85.5 0 112v352c0 26.5 21.5 48 48 48h288c26.5 0 48-21.5 48-48V112c0-26.5-21.5-48-48-48zM96 424c-13.3 0-24-10.7-24-24s10.7-24 24-24 24 10.7 24 24-10.7 24-24 24zm0-96c-13.3 0-24-10.7-24-24s10.7-24 24-24 24 10.7 24 24-10.7 24-24 24zm0-96c-13.3 0-24-10.7-24-24s10.7-24 24-24 24 10.7 24 24-10.7 24-24 24zm96-192c13.3 0 24 10.7 24 24s-10.7 24-24 24-24-10.7-24-24 10.7-24 24-24z"/></svg>',
    spinner: '<svg viewBox="0 0 512 512" fill="currentColor" width="16" height="16" style="animation:bid-spin .8s linear infinite"><path d="M304 48a48 48 0 1 1-96 0 48 48 0 0 1 96 0zm-48 368a48 48 0 1 0 0 96 48 48 0 0 0 0-96zm208-208a48 48 0 1 0 0 96 48 48 0 0 0 0-96zM96 256a48 48 0 1 0-96 0 48 48 0 0 0 96 0z"/></svg>',
    check: '<svg viewBox="0 0 512 512" fill="currentColor" width="16" height="16"><path d="M504 256c0 137-111 248-248 248S8 393 8 256 119 8 256 8s248 111 248 248zM227 387l184-184c6-6 6-16 0-23l-22-22c-7-7-17-7-23 0L216 308l-70-70c-7-6-17-6-23 0l-22 22c-7 7-7 17 0 23l104 104c6 6 16 6 22 0z"/></svg>',
    xmark: '<svg viewBox="0 0 512 512" fill="currentColor" width="16" height="16"><path d="M256 8C119 8 8 119 8 256s111 248 248 248 248-111 248-248S393 8 256 8zm121 313c5 5 5 12 0 17l-39 39c-5 5-12 5-17 0l-65-66-65 66c-5 5-12 5-17 0l-39-39c-5-5-5-12 0-17l66-65-66-65c-5-5-5-12 0-17l39-39c5-5 12-5 17 0l65 66 65-66c5-5 12-5 17 0l39 39c5 5 5 12 0 17l-66 65 66 65z"/></svg>'
  };

  /**
   * The button is a job-page tool, so the whole group is hidden on ChatGPT rather than shown
   * greyed out — that tab is where generation happens in the background, not somewhere you act.
   * While a bid is in flight the button is disabled so a double-click can't start a second run.
   */
  function setButtonStates() {
    var group = document.getElementById(GROUP_ID);
    if (group) group.style.display = isChatGPTUrl() ? 'none' : 'flex';

    var btn = document.getElementById(BTN_ID);
    if (!btn) return;
    btn.disabled = _busy;
    btn.style.opacity = _busy ? '0.4' : '1';
    btn.style.cursor = _busy ? 'wait' : 'pointer';
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

    var btn = document.createElement('button');
    btn.id = BTN_ID; btn.type = 'button'; btn.title = DEFAULT_LABEL;
    btn.style.cssText = 'all:initial;display:flex;align-items:center;justify-content:center;width:40px;height:40px;border:none;background:transparent;cursor:pointer;';
    btn.innerHTML = '<span style="color:rgba(255,255,255,0.9);display:flex;pointer-events:none;">' + SVG_ICONS.clipboard + '</span>';
    btn.addEventListener('mouseenter', function () { if (!btn.disabled) btn.style.background = 'rgba(255,255,255,0.08)'; });
    btn.addEventListener('mouseleave', function () { btn.style.background = 'transparent'; });
    // Runs before the browser collapses the selection, so Ctrl+click can still see it.
    // preventDefault stops the button taking focus, which is what clears it.
    btn.addEventListener('mousedown', function (e) {
      try { _selectionAtMouseDown = (window.getSelection() || '').toString().trim(); }
      catch (err) { _selectionAtMouseDown = ''; }
      e.preventDefault();
    });
    btn.addEventListener('click', onBidClick);

    group.appendChild(dragHandle); group.appendChild(btn);

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

  /**
   * The one button. Scrapes the JD off this page (or your selection with Ctrl+click), hands it to
   * a background ChatGPT tab, waits out the generation, and has DevStrider build the resume
   * silently and record the bid.
   *
   * You never leave this tab — keep filling in the application while it runs. The whole round
   * trip is one message to the worker; the reply carries both outcomes.
   */
  function onBidClick(e) {
    var btn = document.getElementById(BTN_ID);
    if (!btn || btn.disabled || _busy) return;

    var jd = '';
    if (e && e.ctrlKey) {
      // Captured on mousedown -- see _selectionAtMouseDown. Falling back to a live read keeps
      // keyboard-activated clicks working, where no mousedown ever fired.
      jd = _selectionAtMouseDown || (window.getSelection() || '').toString().trim();
      if (!jd) {
        updateStatus('Nothing selected. Highlight the job description first, then Ctrl+click.',
                     'xmark', 'rgba(255,120,120,0.95)');
        return;
      }
      if (jd.length < MIN_JD_LENGTH) {
        updateStatus('Selection is only ' + jd.length + ' characters; needs at least ' +
                     MIN_JD_LENGTH + '. Select the whole job description.',
                     'xmark', 'rgba(255,193,7,0.95)');
        return;
      }
    } else {
      jd = extractJobDescription();
      if (!jd || jd.trim().length < MIN_JD_LENGTH) {
        updateStatus('Could not find the job description on this page. Select it by hand, then Ctrl+click.',
                     'xmark', 'rgba(255,193,7,0.95)');
        return;
      }
    }

    _busy = true;
    setButtonStates();
    updateStatus('Generating resume… keep working, this runs in the background', 'spinner', 'rgba(255,255,255,0.85)');

    var pending = { url: window.location.href, jobDescription: jd, savedAt: Date.now() };
    chrome.storage.local.set({ devstriderPending: pending });

    // If the service worker is torn down mid-request the callback never fires, and without this
    // the button would stay disabled until the page reloads.
    var settled = false;
    var watchdog = setTimeout(function () {
      if (settled) return;
      settled = true;
      _busy = false;
      setButtonStates();
      updateStatus('No response after 5 minutes. Check DevStrider is running and the ChatGPT tab is open.',
                   'xmark', 'rgba(255,120,120,0.95)');
    }, 300000);

    chrome.runtime.sendMessage({ type: 'BID_JOB', jd: jd, url: pending.url }, function (response) {
      if (settled) return;
      settled = true;
      clearTimeout(watchdog);
      _busy = false;
      setButtonStates();

      if (chrome.runtime.lastError) {
        updateStatus('Error: ' + chrome.runtime.lastError.message, 'xmark', 'rgba(255,255,255,0.5)');
      } else if (response && response.ok) {
        // Bid recorded. The macro is reported separately — a failed macro costs you the resume
        // file, not the bid, and the two shouldn't look like one failure.
        var label = [response.company, response.role].filter(Boolean).join(' · ') || 'Bid recorded';
        if (response.macro) {
          updateStatus(('Resume + bid done — ' + label).slice(0, 60), 'check', 'rgba(255,255,255,0.95)');
        } else {
          updateStatus(('Bid recorded · macro: ' + (response.macroError || 'failed')).slice(0, 60), 'xmark', 'rgba(255,193,7,0.95)');
        }
      } else {
        updateStatus('Failed: ' + ((response && response.error) || 'app not running'), 'xmark', 'rgba(255,255,255,0.5)');
      }
      setTimeout(function () { updateStatus(DEFAULT_LABEL, 'clipboard', 'rgba(255,255,255,0.9)'); }, 6000);
    });
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

})();
