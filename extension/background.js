"use strict";

// DevStrider local desktop app listener. The extension talks to it over loopback HTTP.
// One flow lives here — the manual floating buttons: START_GENERATE injects a JD into ChatGPT,
// REFRESH_WORD runs the Word macro and records one bid.
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

// Active profile's resume prompt. Never fails the bid: if the app is unreachable we fall back to
// sending the bare JD, which still works when ChatGPT already knows the format from a Project or
// custom instructions.
function fetchActiveProfile(callback) {
  fetchWithTimeout(APP_URL + "/active-profile", { method: "GET" }, 10000)
    .then(function (r) { return r.text(); })
    .then(function (t) { var d; try { d = JSON.parse(t); } catch (e) { d = null; } callback(d); })
    .catch(function () { callback(null); });
}

// Ask the app to build the resume silently and record the bid. `reply` is the full ChatGPT
// output including its trailing fast-feed line — the app splits it.
function postGenerateResume(pending, reply, callback) {
  if (!pending || !pending.url) {
    callback({ ok: false, error: "No job context — click the button on a job page." });
    return;
  }
  var bodyObj = {
    url: pending.url,
    jobDescription: pending.jobDescription || "",
    resumeText: String(reply || ""),
    origin: "Bid Assistant"
  };
  // Generous timeout: the app runs Word over COM, and that can take a while on a cold start.
  fetchWithTimeout(APP_URL + "/generate-resume", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(bodyObj)
  }, 180000)
    .then(function (r) { return r.text().then(function (t) { return { status: r.status, text: t }; }); })
    .then(function (r) {
      var data; try { data = JSON.parse(r.text); } catch (e) { callback({ ok: false, error: "Invalid JSON from app" }); return; }
      if (r.status >= 200 && r.status < 300 && data && data.ok) callback(data);
      else callback({ ok: false, error: (data && data.error) || ("HTTP " + r.status) });
    })
    .catch(function () { callback({ ok: false, error: "App not reachable. Is DevStrider running?" }); });
}

chrome.runtime.onMessage.addListener(function (message, sender, sendResponse) {
  // ---- One-button flow, driven entirely from the job tab --------------------
  // Nothing here focuses or activates the ChatGPT tab: the user stays on the job page filling
  // in the application while generation happens behind them.
  if (message && message.type === "BID_JOB") {
    var jd = message.jd ? String(message.jd) : "";
    var url = message.url ? String(message.url) : "";
    findChatGPTTab(function (chatTabId) {
      if (!chatTabId) { sendResponse({ ok: false, error: "Open a logged-in ChatGPT tab first" }); return; }
      // The content script opens a new chat per bid, so the profile's resume prompt has to lead
      // — otherwise ChatGPT has no idea it should emit the [FolderName] + fast-feed lines.
      fetchActiveProfile(function (profile) {
        var prompt = (profile && profile.resumePrompt) || "";
        var payload = prompt ? (prompt + "\n\n" + jd) : jd;
        chrome.tabs.sendMessage(chatTabId, { type: "INJECT_AND_HARVEST", text: payload }, function (resp) {
          if (chrome.runtime.lastError) { sendResponse({ ok: false, error: chrome.runtime.lastError.message }); return; }
          if (!resp || !resp.ok) { sendResponse({ ok: false, error: (resp && resp.error) || "ChatGPT tab didn't respond" }); return; }
          postGenerateResume({ url: url, jobDescription: jd }, resp.reply, sendResponse);
        });
      });
    });
    return true;
  }

  // ---- Manual fallback: commit a reply already visible on the ChatGPT tab ----
  if (message && message.type === "BID_FROM_REPLY") {
    var replyText = message.reply ? String(message.reply) : "";
    chrome.storage.local.get(["devstriderPending"], function (st) {
      postGenerateResume(st && st.devstriderPending, replyText, sendResponse);
    });
    return true;
  }

  // ---- Manual: send a JD into ChatGPT via DOM injection (no clipboard) ----
  if (message && message.type === "START_GENERATE") {
    var startJd = message.jd ? String(message.jd) : "";
    findChatGPTTab(function (chatTabId) {
      if (!chatTabId) { sendResponse({ ok: false, error: "Open a logged-in ChatGPT tab first" }); return; }
      chrome.tabs.update(chatTabId, { active: true }, function (tab) {
        if (tab && tab.windowId) chrome.windows.update(tab.windowId, { focused: true });
        chrome.tabs.sendMessage(chatTabId, { type: "INJECT_TEXT", text: startJd }, function (resp) {
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
