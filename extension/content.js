(function () {
  'use strict';

  const GROUP_ID = 'resume-gen-btn-group';
  const BLUE_BTN_ID = 'resume-gen-blue';
  const PURPLE_BTN_ID = 'resume-gen-purple';
  const MIN_JD_LENGTH = 200;
  const DEFAULT_TOP_PCT = 2.5;
  const GROUP_HEIGHT = 90;
  const STORAGE_KEY_TOP = 'resumeGenGroupTop';

  function isChatGPTUrl() {
    var u = (window.location.hostname || '').toLowerCase();
    return u.indexOf('openai.com') !== -1 || u.indexOf('chatgpt.com') !== -1 || u.indexOf('chat.com') !== -1;
  }

  // Site-specific selectors
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

  function getText(el) {
    if (!el) return '';
    return (el.innerText || el.textContent || '').trim();
  }

  function trySelectors(selectors, minLen) {
    // Optimized: Single DOM query with combined selector
    try {
      var combinedSelector = selectors.join(',');
      var elements = document.querySelectorAll(combinedSelector);
      for (var i = 0; i < elements.length; i++) {
        var text = getText(elements[i]);
        if (text.length >= (minLen || 100)) return text;
      }
    } catch (e) {
      // Fallback to sequential if combined selector fails
      for (var i = 0; i < selectors.length; i++) {
        try {
          var el = document.querySelector(selectors[i]);
          if (el) {
            var text = getText(el);
            if (text.length >= (minLen || 100)) return text;
          }
        } catch (e2) {
          if (typeof console !== 'undefined' && console.warn) console.warn('[Bid Assistant] Selector failed:', selectors[i], e2);
        }
      }
    }
    return '';
  }

  function getMainContentText() {
    var main = document.querySelector('main') || document.querySelector('article') || document.querySelector('[role="main"]') || document.body;
    if (!main) return '';
    var candidates = [];
    function walk(node, depth) {
      if (depth > 15) return;
      if (node.nodeType !== Node.ELEMENT_NODE) return;
      var tag = (node.tagName || '').toLowerCase();
      if (tag === 'script' || tag === 'style' || tag === 'nav' || tag === 'header' || tag === 'footer' || tag === 'form' || tag === 'select' || tag === 'option' || tag === 'datalist') return;
      if (node.getAttribute('role') === 'listbox' || node.getAttribute('role') === 'combobox') return;
      var text = getText(node);
      if (text.length < 50) return;
      var linkCount = (node.querySelectorAll('a') || []).length;
      if (linkCount > 0 && linkCount * 50 > text.length) return;
      if (text.length > 50000) return;
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
    var text = getText(el);
    var score = 0;
    
    // Length score (ideal: 500-5000 chars)
    if (text.length >= 500 && text.length <= 5000) score += 50;
    else if (text.length >= 200) score += 20;
    
    // Keyword density
    var keywords = ['responsibilities', 'requirements', 'qualifications', 'experience', 'skills', 'role', 'position', 'job', 'duties', 'description'];
    var lowerText = text.toLowerCase();
    for (var i = 0; i < keywords.length; i++) {
      if (lowerText.indexOf(keywords[i]) !== -1) score += 10;
    }
    
    // Penalize navigation/menu content
    var navKeywords = ['home', 'about', 'contact', 'login', 'sign up', 'sign in', 'register', 'menu'];
    for (var i = 0; i < navKeywords.length; i++) {
      if (lowerText.indexOf(navKeywords[i]) !== -1) score -= 5;
    }
    
    // Penalize high link density
    var linkCount = (el.querySelectorAll('a') || []).length;
    if (linkCount > 10 && linkCount * 30 > text.length) score -= 20;
    
    return score;
  }

  function extractLastAssistantMessage() {
    var nodes = document.querySelectorAll('[data-message-author-role="assistant"]');
    if (!nodes || nodes.length === 0) return '';
    return getText(nodes[nodes.length - 1]);
  }

  /** Same as DevStrider fast feed: resumeId, Company, Role, skill1, … (optional legacy […] around the line). */
  function parseFastFeedLine(line) {
    var t = String(line || '').trim();
    if (!t) return null;
    var core = t;
    if (core.charAt(0) === '[' && core.charAt(core.length - 1) === ']') {
      core = core.slice(1, -1).trim();
    }
    var parts = core.split(',').map(function (p) { return p.trim(); }).filter(function (p) { return p.length > 0; });
    if (parts.length < 3) return null;
    return { resumeId: parts[0], company: parts[1], role: parts[2], primaryStacks: parts.slice(3) };
  }

  /** GPT is expected to put the fast-feed line last; Word + DevStrider get resume text without it. */
  function splitTrailingFastFeed(text) {
    var full = String(text || '');
    var lines = full.split(/\r?\n/);
    for (var i = lines.length - 1; i >= 0; i--) {
      var line = lines[i].trim();
      if (!line) continue;
      if (parseFastFeedLine(line)) {
        var resumePart = lines.slice(0, i).join('\n').replace(/\s+$/, '');
        return { resumePart: resumePart, fastFeedLine: line };
      }
    }
    return { resumePart: full.trim(), fastFeedLine: '' };
  }

  function extractJobDescription() {
    var host = document.location.hostname || '';
    var text = '';
    var siteSelectors = SITE_SELECTORS[host] || null;
    if (!siteSelectors) {
      // Match subdomains (e.g., company.taleo.net matches 'taleo.net')
      var siteKeys = Object.keys(SITE_SELECTORS);
      siteKeys.sort(function (a, b) { return b.length - a.length; }); // Prefer more specific matches
      for (var si = 0; si < siteKeys.length; si++) {
        if (host.includes(siteKeys[si])) {
          siteSelectors = SITE_SELECTORS[siteKeys[si]];
          break;
        }
      }
    }
    if (siteSelectors) text = trySelectors(siteSelectors, 100);
    if (!text) text = trySelectors(GENERIC_SELECTORS, 100);
    
    // Smart extraction with scoring if still no good match
    if (!text || text.length < MIN_JD_LENGTH) {
      var candidates = [];
      var containers = document.querySelectorAll('article, main, section, div[class*="job"], div[class*="description"], div[class*="posting"]');
      for (var i = 0; i < containers.length; i++) {
        var el = containers[i];
        var elScore = scoreElement(el);
        if (elScore > 0) {
          candidates.push({ el: el, score: elScore, text: getText(el) });
        }
      }
      candidates.sort(function(a, b) { return b.score - a.score; });
      if (candidates.length > 0 && candidates[0].text.length >= MIN_JD_LENGTH) {
        text = candidates[0].text;
      }
    }
    
    if (!text) text = getMainContentText();
    if ((!text || text.length < MIN_JD_LENGTH) && document.body) {
      var bodyText = document.body.innerText || document.body.textContent || '';
      if (bodyText.length >= MIN_JD_LENGTH) text = bodyText;
    }
    return text || '';
  }

  const DEFAULT_BLUE_LABEL = 'Generate Resume (Ctrl+click to use selected text as JD)';
  var _busy = false;

  function updateBlueStatus(status) {
    var btn = document.getElementById(BLUE_BTN_ID);
    if (!btn) return;
    var icon = btn.querySelector('span');
    if (!icon) return;
    btn.title = status || DEFAULT_BLUE_LABEL;
    var sl = (status || '').toLowerCase();
    if (sl.includes('error') || sl.includes('failed') || sl.includes('too short')) icon.style.color = 'rgba(255,255,255,0.5)';
    else if (sl.includes('success') || sl.includes('pasted')) icon.style.color = 'rgba(255,255,255,0.95)';
    else if (sl.includes('extracting') || sl.includes('switching') || sl.includes('focusing')) icon.style.color = 'rgba(255,255,255,0.85)';
    else icon.style.color = 'rgba(255,255,255,0.9)';
  }

  var SVG_ICONS = {
    clipboard: '<svg viewBox="0 0 384 512" fill="currentColor" width="16" height="16"><path d="M336 64h-80c0-35.3-28.7-64-64-64s-64 28.7-64 64H48C21.5 64 0 85.5 0 112v352c0 26.5 21.5 48 48 48h288c26.5 0 48-21.5 48-48V112c0-26.5-21.5-48-48-48zM96 424c-13.3 0-24-10.7-24-24s10.7-24 24-24 24 10.7 24 24-10.7 24-24 24zm0-96c-13.3 0-24-10.7-24-24s10.7-24 24-24 24 10.7 24 24-10.7 24-24 24zm0-96c-13.3 0-24-10.7-24-24s10.7-24 24-24 24 10.7 24 24-10.7 24-24 24zm96-192c13.3 0 24 10.7 24 24s-10.7 24-24 24-24-10.7-24-24 10.7-24 24-24zm128 368c0 4.4-3.6 8-8 8H168c-4.4 0-8-3.6-8-8v-16c0-4.4 3.6-8 8-8h144c4.4 0 8 3.6 8 8v16zm0-96c0 4.4-3.6 8-8 8H168c-4.4 0-8-3.6-8-8v-16c0-4.4 3.6-8 8-8h144c4.4 0 8 3.6 8 8v16zm0-96c0 4.4-3.6 8-8 8H168c-4.4 0-8-3.6-8-8v-16c0-4.4 3.6-8 8-8h144c4.4 0 8 3.6 8 8v16z"/></svg>',
    fileWord: '<svg viewBox="0 0 384 512" fill="currentColor" width="16" height="16"><path d="M224 136V0H24C10.7 0 0 10.7 0 24v464c0 13.3 10.7 24 24 24h336c13.3 0 24-10.7 24-24V160H248c-13.2 0-24-10.8-24-24zm65.2 211.4l-57.1 168c-2.2 6.5-8.3 10.6-15.1 10.6-6.8 0-12.9-4.1-15.1-10.6l-57.1-168c-2.2-6.5.5-13.6 6.1-15.8 5.6-2.2 12.4.5 14.6 6.1l18.3 54.8 18.3-54.8c2.2-5.6 9-8.3 14.6-6.1 5.6 2.2 8.3 9.3 6.1 15.8zm-139.9 54.8l18.3 54.8c2.2 5.6 9 8.3 14.6 6.1 5.6-2.2 8.3-9.3 6.1-15.8l-57.1-168c-2.2-6.5-8.3-10.6-15.1-10.6-6.8 0-12.9 4.1-15.1 10.6l-57.1 168c-2.2 6.5.5 13.6 6.1 15.8 5.6 2.2 12.4-.5 14.6-6.1l18.3-54.8 18.3 54.8c2.2 5.6 9 8.3 14.6 6.1 5.6-2.2 8.3-9.3 6.1-15.8zM384 121.9v6.1H256V0h6.1c6.4 0 12.5 2.5 17 7l97.9 98c4.5 4.5 7 10.6 7 16.9z"/></svg>',
    spinner: '<svg viewBox="0 0 512 512" fill="currentColor" width="16" height="16" style="animation:bid-spin .8s linear infinite"><path d="M304 48c0 26.51-21.49 48-48 48s-48-21.49-48-48 21.49-48 48-48 48 21.49 48 48zm-48 368c-26.51 0-48 21.49-48 48s21.49 48 48 48 48-21.49 48-48-21.49-48-48-48zm208-208c-26.51 0-48 21.49-48 48s21.49 48 48 48 48-21.49 48-48-21.49-48-48-48zM96 256c0-26.51-21.49-48-48-48S0 229.49 0 256s21.49 48 48 48 48-21.49 48-48zm12.922 99.078c-26.51 0-48 21.49-48 48s21.49 48 48 48 48-21.49 48-48c0-26.509-21.491-48-48-48zm294.156 0c-26.51 0-48 21.49-48 48s21.49 48 48 48 48-21.49 48-48c0-26.509-21.49-48-48-48zM108.922 60.922c-26.51 0-48 21.49-48 48s21.49 48 48 48 48-21.49 48-48-21.491-48-48-48z"/></svg>',
    check: '<svg viewBox="0 0 512 512" fill="currentColor" width="16" height="16"><path d="M504 256c0 136.967-111.033 248-248 248S8 392.967 8 256 119.033 8 256 8s248 111.033 248 248zM227.314 387.314l184-184c6.248-6.248 6.248-16.379 0-22.627l-22.627-22.627c-6.248-6.249-16.379-6.249-22.628 0L216 308.118l-70.059-70.059c-6.248-6.248-16.379-6.248-22.628 0l-22.627 22.627c-6.248 6.248-6.248 16.379 0 22.627l104 104c6.249 6.249 16.379 6.249 22.628.001z"/></svg>',
    xmark: '<svg viewBox="0 0 512 512" fill="currentColor" width="16" height="16"><path d="M256 8C119 8 8 119 8 256s111 248 248 248 248-111 248-248S393 8 256 8zm121.6 313.1c4.7 4.7 4.7 12.3 0 17L338 377.6c-4.7 4.7-12.3 4.7-17 0L256 312l-65.1 65.6c-4.7 4.7-12.3 4.7-17 0L134.4 338c-4.7-4.7-4.7-12.3 0-17l65.6-65-65.6-65.1c-4.7-4.7-4.7-12.3 0-17l39.6-39.6c4.7-4.7 12.3-4.7 17 0l65 65.7 65.1-65.6c4.7-4.7 12.3-4.7 17 0l39.6 39.6c4.7 4.7 4.7 12.3 0 17L312 256l65.6 65.1z"/></svg>'
  };

  function updatePurpleStatus(status, iconKey, color) {
    var btn = document.getElementById(PURPLE_BTN_ID);
    if (!btn) return;
    var icon = btn.querySelector('span');
    if (!icon) return;
    icon.innerHTML = SVG_ICONS[iconKey] || SVG_ICONS.fileWord;
    icon.style.color = color || 'rgba(255,255,255,0.9)';
    btn.title = status || 'Update Word Document';
  }

  function setButtonStates() {
    var onChatGPT = isChatGPTUrl();
    var blueBtn = document.getElementById(BLUE_BTN_ID);
    var purpleBtn = document.getElementById(PURPLE_BTN_ID);
    if (blueBtn) {
      blueBtn.disabled = onChatGPT;
      blueBtn.style.opacity = onChatGPT ? '0.4' : '1';
      blueBtn.style.cursor = onChatGPT ? 'not-allowed' : 'pointer';
    }
    if (purpleBtn) {
      purpleBtn.disabled = !onChatGPT;
      purpleBtn.style.opacity = onChatGPT ? '1' : '0.4';
      purpleBtn.style.cursor = onChatGPT ? 'pointer' : 'not-allowed';
    }
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
    group.style.cssText = 'position:fixed!important;right:0!important;left:auto!important;top:' + DEFAULT_TOP_PCT + '%!important;margin:0!important;padding:0!important;z-index:2147483647!important;display:flex!important;flex-direction:column!important;align-items:stretch!important;user-select:none!important;direction:ltr!important;background:linear-gradient(180deg,#18181b 0%,#0f0f11 100%)!important;backdrop-filter:blur(20px)!important;-webkit-backdrop-filter:blur(20px)!important;border:1px solid rgba(255,255,255,0.06)!important;border-right:none!important;border-radius:10px 0 0 10px!important;box-shadow:0 0 0 1px rgba(0,0,0,0.3),-6px 4px 24px rgba(0,0,0,0.25)!important;overflow:hidden!important;';

    var dragHandle = document.createElement('div');
    dragHandle.style.cssText = 'width:40px;height:10px;cursor:grab;display:flex;align-items:center;justify-content:center;color:rgba(255,255,255,0.25);font-size:6px;letter-spacing:1px;flex-shrink:0;transition:color 0.2s ease;border-bottom:1px solid rgba(255,255,255,0.04);';
    dragHandle.textContent = '⋯';
    dragHandle.title = 'Drag to move';
    dragHandle.addEventListener('mouseenter', function () { dragHandle.style.color = 'rgba(255,255,255,0.45)'; });
    dragHandle.addEventListener('mouseleave', function () { dragHandle.style.color = 'rgba(255,255,255,0.25)'; });

    var blueBtn = document.createElement('button');
    blueBtn.id = BLUE_BTN_ID;
    blueBtn.type = 'button';
    blueBtn.title = DEFAULT_BLUE_LABEL;
    blueBtn.style.cssText = 'all:initial;display:flex;align-items:center;justify-content:center;width:40px;height:40px;padding:0;margin:0;border:none;border-bottom:1px solid rgba(255,255,255,0.04);background:transparent;cursor:pointer;transition:background 0.2s ease,transform 0.15s ease;';
    blueBtn.innerHTML = '<span style="color:rgba(255,255,255,0.9);display:flex;align-items:center;justify-content:center;pointer-events:none;">' + SVG_ICONS.clipboard + '</span>';

    var purpleBtn = document.createElement('button');
    purpleBtn.id = PURPLE_BTN_ID;
    purpleBtn.type = 'button';
    purpleBtn.title = 'Update Word & record bid in DevStrider';
    purpleBtn.style.cssText = 'all:initial;display:flex;align-items:center;justify-content:center;width:40px;height:40px;padding:0;margin:0;border:none;background:transparent;cursor:pointer;transition:background 0.2s ease,transform 0.15s ease;';
    purpleBtn.innerHTML = '<span style="color:rgba(255,255,255,0.9);display:flex;align-items:center;justify-content:center;pointer-events:none;">' + SVG_ICONS.fileWord + '</span>';

    blueBtn.addEventListener('mouseenter', function () {
      if (!blueBtn.disabled) { blueBtn.style.background = 'rgba(255,255,255,0.08)'; }
    });
    blueBtn.addEventListener('mouseleave', function () { blueBtn.style.background = 'transparent'; });
    purpleBtn.addEventListener('mouseenter', function () {
      if (!purpleBtn.disabled) { purpleBtn.style.background = 'rgba(255,255,255,0.08)'; }
    });
    purpleBtn.addEventListener('mouseleave', function () { purpleBtn.style.background = 'transparent'; });

    blueBtn.addEventListener('click', function(e) { onBlueClick(e); });
    purpleBtn.addEventListener('click', onPurpleClick);

    group.appendChild(dragHandle);
    group.appendChild(blueBtn);
    group.appendChild(purpleBtn);

    // Vertical drag (stores position as % for consistent placement across sites/viewports)
    var dragStartY = 0, dragStartTopPx = 0;
    var onBlurCleanup = function () { onDragEnd(); };

    dragHandle.addEventListener('mousedown', function (e) {
      e.preventDefault();
      e.stopPropagation();
      dragStartY = e.clientY;
      dragStartTopPx = group.getBoundingClientRect().top;
      dragHandle.style.cursor = 'grabbing';
      document.addEventListener('mousemove', onDrag);
      document.addEventListener('mouseup', onDragEnd);
      window.addEventListener('blur', onBlurCleanup);
    });

    function onDrag(e) {
      var dy = e.clientY - dragStartY;
      var maxTopPx = Math.max(0, window.innerHeight - GROUP_HEIGHT - 15);
      var newTopPx = Math.max(0, Math.min(maxTopPx, dragStartTopPx + dy));
      group.style.top = newTopPx + 'px';
    }

    function onDragEnd() {
      dragHandle.style.cursor = 'grab';
      document.removeEventListener('mousemove', onDrag);
      document.removeEventListener('mouseup', onDragEnd);
      window.removeEventListener('blur', onBlurCleanup);
      var topPx = group.getBoundingClientRect().top;
      var pct = (topPx / window.innerHeight) * 100;
      chrome.storage.local.set({ [STORAGE_KEY_TOP]: pct });
    }

    (document.documentElement || document.body).appendChild(group);
    setButtonStates();

    chrome.storage.local.get([STORAGE_KEY_TOP], function (r) {
      var stored = r[STORAGE_KEY_TOP];
      if (typeof stored === 'number' && stored >= 0) {
        var pct = stored > 100 ? (stored / window.innerHeight) * 100 : stored;
        var maxPct = Math.max(0, 100 - (GROUP_HEIGHT + 15) / window.innerHeight * 100);
        pct = Math.min(pct, maxPct);
        group.style.top = pct + '%';
      }
    });

    chrome.storage.local.get(['lastStatus'], function (r) {
      if (r.lastStatus) updateBlueStatus(r.lastStatus);
    });

    chrome.runtime.sendMessage({ type: 'GET_CONFIG' }, function (response) {
      if (response && response.ok && response.word_hotkey) {
        purpleBtn.title = 'Update Word & DevStrider (' + response.word_hotkey + ')';
      }
    });
  }

  function onBlueClick(e) {
    var btn = document.getElementById(BLUE_BTN_ID);
    if (!btn || btn.disabled || _busy) return;

    // Ctrl+click: use the user's current text selection as the JD
    if (e && e.ctrlKey) {
      var selected = (window.getSelection() || '').toString().trim();
      if (!selected || selected.length < MIN_JD_LENGTH) {
        updateBlueStatus(
          selected.length === 0
            ? 'No text selected – select the JD first, then Ctrl+click'
            : 'Selection too short (' + selected.length + ' chars) – select more text'
        );
        return;
      }

      _busy = true;
      updateBlueStatus('Using selected text as JD…');

      // Save the current page URL + selected text as devstriderPending so the
      // purple button can record the bid correctly even though auto-extract was skipped.
      chrome.storage.local.set({
        devstriderPending: {
          url: window.location.href,
          jobDescription: selected,
          savedAt: Date.now(),
        },
      });

      navigator.clipboard.writeText(selected).then(
        function () {
          chrome.runtime.sendMessage({ type: 'START_GENERATE', jd: selected }, function () {
            if (chrome.runtime.lastError) console.error('Bid Assistant:', chrome.runtime.lastError.message);
            _busy = false;
          });
        },
        function () {
          updateBlueStatus('Clipboard failed – paste JD manually');
          _busy = false;
        }
      );
      return;
    }

    // Normal click: auto-extract JD from page
    _busy = true;
    updateBlueStatus("Extracting JD...");

    var jd = extractJobDescription();

    if (!jd || jd.trim().length < MIN_JD_LENGTH) {
      updateBlueStatus("JD too short (" + (jd ? jd.trim().length : 0) + " chars) – select text & Ctrl+click");
      _busy = false;
      return;
    }

    navigator.clipboard.writeText(jd).then(
      function () {
        chrome.runtime.sendMessage({ type: 'START_GENERATE', jd: jd }, function () {
          if (chrome.runtime.lastError) console.error('Bid Assistant:', chrome.runtime.lastError.message);
          _busy = false;
        });
      },
      function () {
        updateBlueStatus("Clipboard failed – paste JD manually");
        _busy = false;
      }
    );
  }

  function onPurpleClick() {
    var btn = document.getElementById(PURPLE_BTN_ID);
    if (!btn || btn.disabled) return;

    btn.disabled = true;
    updatePurpleStatus('Processing...', 'spinner', 'rgba(255,255,255,0.85)');

    var gptFull = extractLastAssistantMessage();
    var splitFf = splitTrailingFastFeed(gptFull);
    chrome.runtime.sendMessage(
      { type: 'REFRESH_WORD', gptResumeContent: splitFf.resumePart, fastFeedInput: splitFf.fastFeedLine },
      function (response) {
        setButtonStates();
        if (chrome.runtime.lastError) {
          updatePurpleStatus('Error: ' + chrome.runtime.lastError.message, 'xmark', 'rgba(255,255,255,0.5)');
        } else if (response && response.ok) {
          var ds = response.devStrider;
          if (ds && ds.ok === false) {
            updatePurpleStatus(
              ('Word OK · DevStrider: ' + (ds.error || 'failed')).slice(0, 44),
              'xmark',
              'rgba(255,193,7,0.95)'
            );
          } else {
            updatePurpleStatus('Word + DevStrider OK!', 'check', 'rgba(255,255,255,0.95)');
          }
        } else {
          updatePurpleStatus(
            'Error: ' + ((response && response.error) || 'Word not found or app not running'),
            'xmark',
            'rgba(255,255,255,0.5)'
          );
        }
        setTimeout(function () {
          chrome.runtime.sendMessage({ type: 'GET_CONFIG' }, function (r) {
            var hotkey = r && r.ok && r.word_hotkey ? r.word_hotkey : null;
            updatePurpleStatus(
              hotkey ? 'Update Word & DevStrider (' + hotkey + ')' : 'Update Word & record bid in DevStrider',
              'fileWord',
              'rgba(255,255,255,0.9)'
            );
          });
        }, 3500);
      }
    );
  }

  chrome.storage.onChanged.addListener(function (changes, areaName) {
    if (areaName === 'local' && changes.lastStatus) updateBlueStatus(changes.lastStatus.newValue);
  });

  createButtonGroup();

  // Re-create buttons if removed from DOM; update states on SPA navigation
  var lastUrl = location.href;
  var observer = new MutationObserver(function () {
    if (!document.getElementById(GROUP_ID) && document.body) {
      createButtonGroup();
    } else if (location.href !== lastUrl) {
      lastUrl = location.href;
      setButtonStates();
    }
  });
  observer.observe(document.documentElement, { childList: true, subtree: true });
})();
