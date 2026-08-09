"use strict";

// Service worker for the Bid Assistant. It owns every call to the DevStrider desktop app over
// loopback HTTP, and orchestrates the single flow the extension has:
//
//   job tab clicks the button
//     → GET  /active-profile   (fetch the resume prompt)
//     → INJECT_PROMPT         (drive the *background* ChatGPT tab, never focusing it)
//     → poll GET_GENERATION_STATE until the reply settles (the loop lives here, not in the tab)
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

// ---- Waiting out the generation -------------------------------------------
// This loop lives in the service worker, not in the ChatGPT tab. A hidden tab's timers are
// throttled to one tick per second, and to one per *minute* once it has been hidden for five
// minutes — which a ChatGPT tab left open in the background always has been. A service worker
// has no document, so none of that applies to it, and the content script answers each poll the
// moment it arrives.
const GEN_POLL_MS = 1500;
const GEN_TIMEOUT_MS = 300000;
// How long the reply must stay byte-identical before we call it finished. This is wall clock,
// not a tick count: ticks used to be driven by DOM mutations, so two firing in quick succession
// mid-stream could declare a half-written resume complete — which is how a bid once got recorded
// from a sentence in the middle of an experience section.
const GEN_QUIET_MS = 3000;
// Grace for replies with no [FolderName]: line, where we have no positive end-of-reply marker.
const GEN_SETTLED_MS = 15000;

// The [FolderName]: line is emitted last, so its arrival is a definitive end-of-reply marker —
// far more reliable than the stop button, which flickers while streaming.
function looksComplete(text) {
  var m = /\[\s*FolderName\s*\]\s*:([\s\S]*)$/i.exec(text || "");
  if (!m) return false;
  // The value may sit on the same line or the next one. Either way it must be non-empty and
  // carry at least the "id, Company, Role" shape.
  var lines = m[1].split("\n").map(function (s) { return s.trim(); }).filter(Boolean);
  if (!lines.length) return false;
  return lines[0].split(",").filter(function (s) { return s.trim(); }).length >= 3;
}

function waitForGeneration(tabId, callback) {
  var startedAt = Date.now();
  var lastText = null;
  var lastChangeAt = Date.now();

  function poll() {
    if (Date.now() - startedAt > GEN_TIMEOUT_MS) {
      callback({ ok: false, error: "ChatGPT did not finish within 5 minutes" });
      return;
    }
    chrome.tabs.sendMessage(tabId, { type: "GET_GENERATION_STATE" }, function (resp) {
      if (chrome.runtime.lastError || !resp || !resp.ok) {
        // Tab may be mid-navigation or the script not yet injected; retry until the timeout.
        setTimeout(poll, GEN_POLL_MS);
        return;
      }
      var text = resp.text || "";
      if (text !== lastText) { lastText = text; lastChangeAt = Date.now(); }
      var quietFor = Date.now() - lastChangeAt;

      // Settled means: not streaming, and the text has stopped changing. With a [FolderName]:
      // line we know the reply is whole and a short quiet period is enough; without one we wait
      // longer, because there is nothing to distinguish "finished" from "paused mid-sentence".
      var settled = !resp.streaming && quietFor >= GEN_QUIET_MS &&
                    (looksComplete(text) || quietFor >= GEN_SETTLED_MS);

      if (settled && text.length >= 20) { callback({ ok: true, reply: text }); return; }
      setTimeout(poll, GEN_POLL_MS);
    });
  }
  poll();
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
        chrome.tabs.sendMessage(chatTabId, { type: "INJECT_PROMPT", text: payload }, function (resp) {
          if (chrome.runtime.lastError) { sendResponse({ ok: false, error: chrome.runtime.lastError.message }); return; }
          if (!resp || !resp.ok) { sendResponse({ ok: false, error: (resp && resp.error) || "ChatGPT tab didn't respond" }); return; }
          waitForGeneration(chatTabId, function (gen) {
            if (!gen.ok) { sendResponse({ ok: false, error: gen.error }); return; }
            postGenerateResume({ url: url, jobDescription: jd }, gen.reply, sendResponse);
          });
        });
      });
    });
    return true;
  }
});

console.log("[DevStrider] Background worker loaded.");
