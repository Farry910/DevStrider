// background.js

let activeJobTabId = null;
let currentJobIdForExtraction = null;
let requestingChatTabId = null; // Stores the ID of the ChatGPT tab that initiated the task

// --- Tab Management (Enable/Disable Popup based on URL) ---
function checkTabAndToggleAction(tabId) {
    chrome.tabs.get(tabId, (tab) => {
        if (chrome.runtime.lastError || !tab.url) return;
        if (tab.url.includes('chatgpt.com') || tab.url.includes('chat.openai.com')) {
            chrome.action.enable(tabId);
        } else {
            chrome.action.disable(tabId);
        }
    });
}

chrome.tabs.onActivated.addListener((activeInfo) => checkTabAndToggleAction(activeInfo.tabId));
chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
    if (changeInfo.status === 'complete') checkTabAndToggleAction(tabId);
});

chrome.tabs.query({ active: true, currentWindow: true }, (tabs) => {
    if (tabs.length > 0) checkTabAndToggleAction(tabs[0].id);
});

// --- Job Extraction Logic ---
async function openJobTabAndExtract(url, jobId) {
    console.log("🌐 [BG] Opening job link:", url);
    currentJobIdForExtraction = jobId;
    const tab = await chrome.tabs.create({ url: url, active: false }); // Open in background
    activeJobTabId = tab.id;
    
    const listener = (tabId, changeInfo) => {
        if (tabId === activeJobTabId && changeInfo.status === "complete") {
            console.log("✅ [BG] Job page loaded. Extracting text...");
            chrome.tabs.onUpdated.removeListener(listener);
            extractJobText();
        }
    };
    chrome.tabs.onUpdated.addListener(listener);
}

async function extractJobText() {
    try {
        const results = await chrome.scripting.executeScript({
            target: { tabId: activeJobTabId },
            func: () => document.body.innerText
        });
        
        // Close the job tab immediately after extraction
        if (activeJobTabId) {
            chrome.tabs.remove(activeJobTabId, () => {
                if (chrome.runtime.lastError) console.warn("⚠️ [BG] Could not close job tab.");
            });
            activeJobTabId = null;
        }

        // Send the extracted text back to the specific ChatGPT tab that requested it
        if (requestingChatTabId) {
            chrome.tabs.sendMessage(requestingChatTabId, {
                action: "JOB_TEXT_EXTRACTED",
                jobId: currentJobIdForExtraction,
                raw_text: results[0].result
            });
        }
    } catch (e) {
        console.error("❌ [BG] Extraction failed:", e);
        if (activeJobTabId) chrome.tabs.remove(activeJobTabId);
        activeJobTabId = null;
        
        if (requestingChatTabId) {
            chrome.tabs.sendMessage(requestingChatTabId, {
                action: "EXTRACTION_FAILED",
                jobId: currentJobIdForExtraction
            });
        }
    }
}

// --- Message Listeners ---
chrome.runtime.onMessage.addListener((request, sender, sendResponse) => {
    // 1. Messages from Content Script (ChatGPT page)
    if (request.action === "START_JOB_EXTRACTION") {
        requestingChatTabId = sender.tab.id; // Save the exact ChatGPT tab ID
        openJobTabAndExtract(request.jobUrl, request.jobId);
        sendResponse({ status: "extracting" });
    } 
    // 2. Messages from Popup (Forward to Content Script)
    else if (request.action === "CONNECT_WS" || request.action === "GET_WS_STATE") {
        chrome.tabs.query({ active: true, currentWindow: true }, (tabs) => {
            if (tabs.length > 0) {
                chrome.tabs.sendMessage(tabs[0].id, request, (response) => {
                    if (chrome.runtime.lastError) {
                        console.warn("⚠️ [BG] Content script not ready. Injecting...");
                        chrome.scripting.executeScript({ 
                            target: { tabId: tabs[0].id }, 
                            files: ["content.js"] 
                        }).then(() => {
                            setTimeout(() => {
                                chrome.tabs.sendMessage(tabs[0].id, request, sendResponse);
                            }, 500);
                        });
                    } else {
                        sendResponse(response);
                    }
                });
            } else {
                sendResponse({ error: "No active tab" });
            }
        });
        return true; // Keep channel open for async response
    } 
    // 3. State changes from Content Script (Forward to Popup)
    else if (request.action === "WS_STATE_CHANGED") {
        chrome.runtime.sendMessage(request).catch(() => {});
        sendResponse({ status: "ok" });
    } 
    // 4. Reload commands from Popup
    else if (request.action === "RELOAD_EXTENSION") {
        sendResponse({ status: "reloading" });
        setTimeout(() => chrome.runtime.reload(), 100);
    } 
    else if (request.action === "RELOAD_PAGE") {
        chrome.tabs.query({ active: true, currentWindow: true }, (tabs) => {
            if (tabs.length > 0) chrome.tabs.reload(tabs[0].id);
        });
        sendResponse({ status: "reloading_page" });
    }
    
    return true;
});

console.log("🚀 [Init] Background Script Loaded.");