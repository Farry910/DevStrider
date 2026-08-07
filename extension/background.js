"use strict";

// DevStrider local desktop app listener. The extension talks to it over loopback HTTP.
// Two flows live here:
//   • Manual (floating buttons): START_GENERATE injects a JD into ChatGPT; REFRESH_WORD runs
//     the Word macro + records one bid.
//   • Batch (Resume tab): the ChatGPT content script polls the app and drives generation; this
//     worker only scrapes job descriptions in throwaway background tabs (RESUME_SCRAPE_JD).
const APP_URL = "http://127.0.0.1:8765";
const FETCH_TIMEOUT_MS = 15000;

function fetchWithTimeout(url, options, timeoutMs) {
  var controller = new AbortController();
  var id = setTimeout(function () { controller.abort(); }, timeoutMs || FETCH_TIMEOUT_MS);
  return fetch(url, Object.assign({}, options, { signal: controller.signal }))
    .finally(function () { clearTimeout(id); });
}

function findChatGPTTab(cb) {
  chrome.tabs.query(
    { url: ["*://*.openai.com/*", "*://chatgpt.com/*", "*://*.chatgpt.com/*", "*://chat.com/*", "*://*.chat.com/*"] },
    function (tabs) {
      if (tabs && tabs.length) { tabs.sort(function (a, b) { return (b.lastAccessed || 0) - (a.lastAccessed || 0); }); return cb(tabs[0].id); }
      cb(null);
    }
  );
}

// --- Background-tab JD scrape (batch engine asks for this) -----------------
async function scrapeJobDescription(url) {
  var tab = await chrome.tabs.create({ url: url, active: false });
  var tabId = tab.id;
  try {
    await waitForComplete(tabId, 30000);
    var results = await chrome.scripting.executeScript({
      target: { tabId: tabId },
      func: extractJdInPage
    });
    return (results && results[0] && results[0].result) || "";
  } finally {
    try { await chrome.tabs.remove(tabId); } catch (e) { /* ignore */ }
  }
}

function waitForComplete(tabId, timeoutMs) {
  return new Promise(function (resolve) {
    var done = false;
    var timer = setTimeout(function () { if (!done) { done = true; chrome.tabs.onUpdated.removeListener(listener); resolve(); } }, timeoutMs || 30000);
    function listener(id, info) {
      if (id === tabId && info.status === "complete") {
        if (done) return;
        done = true; clearTimeout(timer); chrome.tabs.onUpdated.removeListener(listener);
        // Give SPA job boards a beat to render the description.
        setTimeout(resolve, 1500);
      }
    }
    chrome.tabs.onUpdated.addListener(listener);
  });
}

// Runs IN the job page (isolated world). Self-contained — no outer references.
function extractJdInPage() {
  function txt(el) { return el ? (el.innerText || el.textContent || "").trim() : ""; }
  var selectors = [
    ".jobs-description__content", ".jobs-description", "#jobDescriptionText",
    "#job-description-container", ".jobsearch-JobComponent-description",
    ".job-post", ".job__description", "#job-description", ".job-description",
    "[class*='job-description']", "[class*='JobDescription']", ".posting-page",
    "[id*='requisitionDescription']", "article", "[role='main']", "main"
  ];
  for (var i = 0; i < selectors.length; i++) {
    try {
      var els = document.querySelectorAll(selectors[i]);
      for (var j = 0; j < els.length; j++) { var t = txt(els[j]); if (t.length >= 200) return t; }
    } catch (e) { /* ignore */ }
  }
  var body = (document.body && (document.body.innerText || document.body.textContent)) || "";
  return body.trim();
}

// --- Manual: run Word macro + record one bid (purple button) ----------------
function submitDevStriderRecord(st, gptResumeContent, fastFeedInput, callback) {
  var pending = st && st.devstriderPending;
  if (!pending || !pending.url) { callback({ ok: false, error: "No job context. Use the blue button on a job page first." }); return; }
  var bodyObj = {
    url: pending.url,
    jobDescription: pending.jobDescription || "",
    gptResumeContent: gptResumeContent ? String(gptResumeContent).trim() : "",
    origin: "Bid Assistant"
  };
  if (fastFeedInput && String(fastFeedInput).trim()) bodyObj.fastFeedInput = String(fastFeedInput).trim();

  fetchWithTimeout(APP_URL + "/record-bid", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(bodyObj) }, 120000)
    .then(function (r) { return r.text().then(function (text) { return { status: r.status, text: text }; }); })
    .then(function (r) {
      var data; try { data = JSON.parse(r.text); } catch (e) { callback({ ok: false, error: "Invalid JSON from app" }); return; }
      if (r.status >= 200 && r.status < 300) callback({ ok: true, data: data });
      else callback({ ok: false, error: (data && data.error) || String(r.status) });
    })
    .catch(function () { callback({ ok: false, error: "App not running?" }); });
}

chrome.runtime.onMessage.addListener(function (message, sender, sendResponse) {
  // ---- Batch: scrape a JD in a throwaway tab ----
  if (message && message.type === "RESUME_SCRAPE_JD") {
    scrapeJobDescription(message.url)
      .then(function (jd) { sendResponse({ ok: true, jd: jd }); })
      .catch(function (e) { sendResponse({ ok: false, error: e.message }); });
    return true;
  }

  // ---- Manual: send a JD into ChatGPT via DOM injection (no clipboard) ----
  if (message && message.type === "START_GENERATE") {
    var jd = message.jd ? String(message.jd) : "";
    findChatGPTTab(function (chatTabId) {
      if (!chatTabId) { sendResponse({ ok: false, error: "Open a logged-in ChatGPT tab first" }); return; }
      chrome.tabs.update(chatTabId, { active: true }, function (tab) {
        if (tab && tab.windowId) chrome.windows.update(tab.windowId, { focused: true });
        chrome.tabs.sendMessage(chatTabId, { type: "INJECT_TEXT", text: jd }, function (resp) {
          if (chrome.runtime.lastError) sendResponse({ ok: false, error: chrome.runtime.lastError.message });
          else sendResponse(resp || { ok: false, error: "No response from ChatGPT tab" });
        });
      });
    });
    return true;
  }

  // ---- Manual: run the Word macro (hotkey path) and record the bid ----
  // These two are independent: a slow/failed Word refresh (e.g. queued behind another
  // Chrome window's refresh) must never prevent the bid from being recorded in DevStrider,
  // so both run in parallel and report their outcomes separately.
  if (message && message.type === "REFRESH_WORD") {
    var gpt = message.gptResumeContent ? String(message.gptResumeContent) : "";
    var ff = message.fastFeedInput ? String(message.fastFeedInput) : "";
    chrome.storage.local.get(["devstriderPending"], function (st) {
      var wordDone = fetchWithTimeout(APP_URL + "/refresh-word", { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" }, 90000)
        .then(function (r) { return r.text(); })
        .then(function (text) {
          var data; try { data = JSON.parse(text); } catch (e) { return { ok: false, error: "Invalid response from app" }; }
          if (data && data.success) return { ok: true };
          return { ok: false, error: (data && data.error) || "Failed to refresh Word" };
        })
        .catch(function () { return { ok: false, error: "App not reachable. Is DevStrider running?" }; });

      var recordDone = new Promise(function (resolve) {
        submitDevStriderRecord(st, gpt, ff, function (ds) { resolve(ds); });
      });

      Promise.all([wordDone, recordDone]).then(function (results) {
        var word = results[0], ds = results[1];
        sendResponse({
          ok: true,
          word: word,
          devStrider: ds.ok ? { ok: true, data: ds.data } : { ok: false, error: ds.error }
        });
      });
    });
    return true;
  }
});

console.log("[DevStrider] Background worker loaded.");
