"use strict";

// Service worker for the Bid Assistant. It owns every call to the DevStrider desktop app over
// loopback HTTP, and orchestrates the single flow the extension has:
//
//   job tab clicks the button
//     → GET  /active-profile   (fetch the resume prompt)
//     → INJECT_AND_HARVEST     (drive the *background* ChatGPT tab, never focusing it)
//     → POST /generate-resume  (silent Word macro + record the bid)
//     → reply back to the job tab
//
// The job tab is never navigated away from and the ChatGPT tab is never activated.
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
});

console.log("[DevStrider] Background worker loaded.");
