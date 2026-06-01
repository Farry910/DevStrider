"use strict";

// DevStrider is now a local desktop app — no web service, no JWT, no groupId.
// The extension talks directly to the WPF listener at 127.0.0.1:8765, which absorbs
// what BidAssistantApp used to do (paste-submit, refresh-word, browse-word, record-bid).
const APP_URL = "http://127.0.0.1:8765";
const FETCH_TIMEOUT_MS = 10000;

function fetchWithTimeout(url, options, timeoutMs) {
  var controller = new AbortController();
  var id = setTimeout(function () { controller.abort(); }, timeoutMs || FETCH_TIMEOUT_MS);
  return fetch(url, Object.assign({}, options, { signal: controller.signal }))
    .finally(function () { clearTimeout(id); });
}

function setStatus(msg) {
  try {
    chrome.storage.local.set({ lastStatus: msg });
  } catch (e) {
    if (typeof console !== 'undefined' && console.warn) console.warn('[Bid Assistant] setStatus failed:', e);
  }
}

function focusChatInput(tabId, cb) {
  chrome.scripting.executeScript(
    {
      target: { tabId: tabId },
      func: function () {
        var selectors = [
          'textarea[data-id="root"]',
          'div#prompt-textarea.ProseMirror',
          'textarea#prompt-textarea',
          'form[data-type="unified-composer"] [contenteditable="true"]',
          'form[data-type="unified-composer"] .ProseMirror',
          'textarea[placeholder*="Message"]',
          'textarea[placeholder*="Ask"]',
          'div[contenteditable="true"][role="textbox"]',
          'form textarea',
          'textarea'
        ];
        
        for (var i = 0; i < selectors.length; i++) {
          try {
            var input = document.querySelector(selectors[i]);
            if (input && input.offsetParent) {
              input.scrollIntoView({ behavior: 'instant', block: 'center' });
              input.focus();
              input.click();
              
              if (document.activeElement === input) {
                return { success: true, selector: selectors[i] };
              }
            }
          } catch (e) {
            continue;
          }
        }
        
        return { success: false, selector: null };
      },
    },
    function (results) {
      if (chrome.runtime.lastError || !results || !results[0]) return cb(false);
      var result = results[0].result;
      if (result && result.success && typeof console !== 'undefined' && console.log) {
        console.log('[Bid Assistant] Focused input using:', result.selector);
      }
      cb(result && result.success === true);
    },
  );
}

function findChatGPTTab(cb) {
  chrome.tabs.query(
    { url: ["*://*.openai.com/*", "*://chatgpt.com/*", "*://*.chatgpt.com/*", "*://chat.com/*", "*://*.chat.com/*"] },
    function (tabs) {
      if (tabs && tabs.length) {
        tabs.sort(function (a, b) {
          return (b.lastAccessed || 0) - (a.lastAccessed || 0);
        }); 
        return cb(tabs[0].id);
      }
      cb(null);
    },
  );
}

/**
 * Record the URL + JD + GPT resume in the local DevStrider desktop app.
 * Loopback-only POST to /record-bid; no JWT, no groupId. The WPF listener applies
 * the same fast-feed parsing rules the web backend used.
 */
function submitDevStriderRecord(st, gptResumeContent, fastFeedInput, callback) {
  var pending = st && st.devstriderPending;
  if (!pending || !pending.url) {
    callback({
      ok: false,
      error: "No job context saved. On a job posting page, click Generate first.",
    });
    return;
  }
  var gpt =
    gptResumeContent != null && String(gptResumeContent) ? String(gptResumeContent).trim() : "";
  var ff =
    fastFeedInput != null && String(fastFeedInput).trim()
      ? String(fastFeedInput).trim()
      : "";
  var bodyObj = {
    url: pending.url,
    jobDescription: pending.jobDescription || "",
    gptResumeContent: gpt,
    origin: "Bid Assistant",
  };
  if (ff) bodyObj.fastFeedInput = ff;
  var body = JSON.stringify(bodyObj);

  fetchWithTimeout(APP_URL + "/record-bid", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: body,
  }, 120000)
    .then(function (r) {
      return r.text().then(function (text) {
        return { status: r.status, text: text };
      });
    })
    .then(function (r) {
      var data;
      try { data = JSON.parse(r.text); }
      catch (e) { callback({ ok: false, error: "Invalid JSON from DevStrider app" }); return; }
      if (r.status >= 200 && r.status < 300) {
        chrome.storage.local.set({
          bidAssistantSessionCache: {
            url: pending.url,
            jobDescription: pending.jobDescription || "",
            gptResumeContent: gpt,
            updatedAt: Date.now(),
          },
        });
        callback({ ok: true, data: data });
      } else {
        var err =
          (data && (data.error || (data.errors && data.errors[0] && data.errors[0].msg))) ||
          String(r.status);
        callback({ ok: false, error: String(err) });
      }
    })
    .catch(function (err) {
      if (typeof console !== "undefined" && console.error)
        console.error("[Bid Assistant] DevStrider record failed:", err);
      callback({
        ok: false,
        error: "Network error — is the DevStrider desktop app running?",
      });
    });
}

chrome.runtime.onMessage.addListener(function (message, sender, sendResponse) {
  if (message.type === "GET_CONFIG") {
    chrome.storage.local.get(["wordHotkey"], function (r) {
      sendResponse({ ok: true, word_hotkey: r.wordHotkey || "F9" });
    });
    return true;
  }

  if (message.type === "REFRESH_WORD") {
    setStatus("Switching to job tab...");
    var gptFromPage =
      message.gptResumeContent != null && message.gptResumeContent !== undefined
        ? String(message.gptResumeContent)
        : "";
    var fastFeedInput =
      message.fastFeedInput != null && message.fastFeedInput !== undefined
        ? String(message.fastFeedInput)
        : "";
    chrome.storage.local.get(
      [
        "lastJobTabId",
        "wordDocPath",
        "wordHotkey",
        "devstriderPending",
      ],
      function (st) {
        var jobTabId = st && st.lastJobTabId;
        var wordPath = (st && st.wordDocPath) || "";
        wordPath = String(wordPath).trim();
        var wordHotkey = (st && st.wordHotkey) || "F9";
        if (!wordPath) {
          setStatus("Error: Set Word path in extension popup");
          sendResponse({ ok: false, error: "Set Word document path in extension popup first." });
          return;
        }
        function afterWordSuccess() {
          setStatus("Word updated — syncing DevStrider…");
          submitDevStriderRecord(st, gptFromPage, fastFeedInput, function (ds) {
            // Fire-and-forget: report outcome to BAA dashboard
            var outcomePayload = JSON.stringify({
              phase: "after_word",
              code: ds.ok ? "ok" : "error",
              detail: ds.ok ? "" : (ds.error || "failed"),
              ts: Date.now(),
            });
            try {
              fetch(APP_URL + "/client/devstrider-outcome", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: outcomePayload,
              }).catch(function () {});
            } catch (e) {}

            if (ds.ok) {
              setStatus("Word updated & DevStrider recorded!");
              sendResponse({ ok: true, devStrider: { ok: true, data: ds.data } });
            } else {
              setStatus("Word OK; DevStrider: " + (ds.error || "failed"));
              sendResponse({ ok: true, devStrider: { ok: false, error: ds.error } });
            }
          });
        }
        function doRefreshWord() {
          setStatus("Refreshing Word document...");
          fetchWithTimeout(APP_URL + "/refresh-word", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ word_doc_path: wordPath, word_hotkey: wordHotkey }),
          })
            .then(function (r) {
              return r.text();
            })
            .then(function (text) {
              var data;
              try {
                data = JSON.parse(text);
              } catch (e) {
                setStatus("Error: Invalid response from app");
                sendResponse({
                  ok: false,
                  error:
                    "Invalid response from app. Ensure the Bid Assistant app is running and responding correctly.",
                });
                return;
              }
              if (data && data.success) {
                afterWordSuccess();
              } else {
                setStatus("Error: " + (data && data.error ? data.error : "Unknown error"));
                sendResponse({ ok: false, error: (data && data.error) || "Failed to refresh Word" });
              }
            })
            .catch(function (err) {
              if (typeof console !== "undefined" && console.error)
                console.error("[Bid Assistant] refresh-word fetch failed:", err);
              setStatus("Error: App not reachable");
              sendResponse({
                ok: false,
                error: "App not reachable. Make sure the Bid Assistant app is running.",
              });
            });
        }
        if (jobTabId) {
          chrome.tabs.get(jobTabId, function (tab) {
            if (chrome.runtime.lastError || !tab) {
              doRefreshWord();
              return;
            }
            chrome.tabs.update(jobTabId, { active: true }, function () {
              if (chrome.runtime.lastError) {
                console.warn(
                  "[Bid Assistant] Could not switch to job tab:",
                  chrome.runtime.lastError.message
                );
              }
              doRefreshWord();
            });
          });
        } else {
          doRefreshWord();
        }
      }
    );
    return true;
  }

  if (message.type !== "START_GENERATE") return;

  if (sender && sender.tab && sender.tab.id) {
    chrome.storage.local.set({ lastJobTabId: sender.tab.id });
  }

  setStatus("Starting…");

  var MIN_JD_LENGTH = 200;

  if (
    message.jd &&
    typeof message.jd === "string" &&
    message.jd.trim().length >= MIN_JD_LENGTH &&
    sender &&
    sender.tab &&
    sender.tab.url &&
    /^https?:\/\//i.test(sender.tab.url)
  ) {
    chrome.storage.local.set({
      devstriderPending: {
        url: sender.tab.url,
        jobDescription: message.jd.trim(),
        savedAt: Date.now(),
      },
    });
  }
  var MANUAL_FALLBACK_MSG = "Couldn't extract job description. Paste the JD into ChatGPT manually.";

  var jdDone = false;
  var jdErr = null;
  var chatDone = false;
  var chatTabIdVal = null;

  if (message.jd && typeof message.jd === "string" && message.jd.trim().length >= MIN_JD_LENGTH) {
    jdDone = true;
    jdErr = null;
    maybeContinue();
  } else {
    jdDone = true;
    jdErr = new Error(MANUAL_FALLBACK_MSG);
    maybeContinue();
  }

  function maybeContinue() {
    if (!jdDone || !chatDone) return;
    if (jdErr) {
      setStatus("Error: " + (jdErr.message || "Could not extract job description"));
      sendResponse({ ok: false, error: jdErr.message });
      return;
    }
    if (!chatTabIdVal) {
      setStatus("Open a ChatGPT tab first");
      sendResponse({ ok: false, error: "Open a ChatGPT tab first" });
      return;
    }
    const chatTabId = chatTabIdVal;
    
    // FIXED: Switch to ChatGPT FIRST, then call app
    setStatus("Switching to ChatGPT…");
    chrome.tabs.update(chatTabId, { active: true }, function (tab) {
      if (tab && tab.windowId) chrome.windows.update(tab.windowId, { focused: true });
      
      // Focus input, then immediately tell C# app to Ctrl+V + Enter
      focusChatInput(chatTabId, function (focused) {
        if (!focused) {
          setStatus("Couldn't focus input. Press Ctrl+V manually.");
          sendResponse({ ok: false, error: "Press Ctrl+V manually.", needManual: true });
          return;
        }

        setStatus("Pasting…");
        fetchWithTimeout(APP_URL + "/trigger-paste-submit", { method: "POST" })
          .then(function (r) { return r.text(); })
          .then(function (text) {
            var data;
            try { data = JSON.parse(text); } catch (e) { data = { success: false }; }
            if (data && data.success) {
              setStatus("JD pasted into ChatGPT!");
              sendResponse({ ok: true });
            } else {
              setStatus("App not responding. Press Ctrl+V manually.");
              sendResponse({ ok: false, error: "Press Ctrl+V manually." });
            }
          })
          .catch(function (err) {
            if (typeof console !== 'undefined' && console.error) console.error('[Bid Assistant] paste-submit fetch failed:', err);
            setStatus("App not running. Press Ctrl+V manually.");
            sendResponse({ ok: false, error: "App not running. Press Ctrl+V manually." });
          });
      });
    });
  }

  findChatGPTTab(function (id) {
    chatDone = true;
    chatTabIdVal = id;
    maybeContinue();
  });

  return true;
});
