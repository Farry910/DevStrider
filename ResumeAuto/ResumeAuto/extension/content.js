// content.js

let ws = null;
let wsState = "DISCONNECTED";

// --- WebSocket Management ---
function connectWebSocket() {
    if (ws && ws.readyState === WebSocket.CONNECTING) return;
    if (ws && ws.readyState !== WebSocket.CLOSED) ws.close();

    wsState = "CONNECTING";
    notifyBackgroundState();
    console.log("🔄 [Content WS] Attempting to connect to Python...");
    
    ws = new WebSocket("ws://localhost:12345");
    
    ws.onopen = () => {
        console.log("✅ [Content WS] Connected to Python Backend!");
        wsState = "CONNECTED";
        notifyBackgroundState();
    };
    
    ws.onmessage = (event) => {
        try {
            const data = JSON.parse(event.data);
            console.log("📩 [Content WS] Received:", data.type);
            
            if (data.type === "START_TASK") {
                console.log("▶️ [Content] Received START_TASK. Requesting job extraction...");
                // Ask background to open the job tab and extract text
                chrome.runtime.sendMessage({
                    action: "START_JOB_EXTRACTION",
                    jobUrl: data.jobUrl,
                    jobId: data.jobId
                });
            } 
            else if (data.type === "PARSE_RESPONSE") {
                console.log("📝 [Content] Received PARSE_RESPONSE. Starting injection...");
                const combinedText = `${data.prompt}\n\n--- JOB DESCRIPTION ---\n${data.job_description}`;
                injectAndObserve(combinedText, data.jobId);
            }
        } catch (e) {
            console.error("❌ [Content WS] Error parsing message:", e);
        }
    };

    ws.onclose = () => {
        console.warn("⚠️ [Content WS] Disconnected.");
        wsState = "DISCONNECTED";
        notifyBackgroundState();
    };

    ws.onerror = () => {
        console.error("❌ [Content WS] Connection error.");
        wsState = "DISCONNECTED";
        notifyBackgroundState();
    };
}

function notifyBackgroundState() {
    chrome.runtime.sendMessage({ action: "WS_STATE_CHANGED", state: wsState }).catch(() => {});
}

function safeSendToBackend(dataObject) {
    if (ws && ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify(dataObject));
        console.log("📤 [Content WS] Sent to backend:", dataObject.type);
    } else {
        console.error("❌ [Content WS] Cannot send, WebSocket is not open.");
    }
}

// --- Listeners ---
chrome.runtime.onMessage.addListener((request, sender, sendResponse) => {
    if (request.action === "CONNECT_WS") {
        connectWebSocket();
        sendResponse({ status: "connecting" });
    } 
    else if (request.action === "GET_WS_STATE") {
        sendResponse({ state: wsState });
    }
    else if (request.action === "JOB_TEXT_EXTRACTED") {
        // Background finished extracting job text, send it to Python for parsing
        console.log("✅ [Content] Job text extracted. Sending to backend for parsing...");
        safeSendToBackend({
            type: "PARSE_REQUEST",
            jobId: request.jobId,
            raw_text: request.raw_text
        });
        sendResponse({ status: "parsing" });
    }
    else if (request.action === "EXTRACTION_FAILED") {
        console.error("❌ [Content] Job extraction failed.");
        safeSendToBackend({ type: "TASK_FAILED", jobId: request.jobId });
        sendResponse({ status: "failed" });
    }
    
    return true; // Keep message channel open for async responses
});

// --- ChatGPT Automation ---
async function injectAndObserve(text, jobId) {
    console.log("⌨️ [Content] Starting ChatGPT automation sequence...");

    const newChatBtn = document.querySelector('[data-testid="create-new-chat-button"]');
    if (newChatBtn) newChatBtn.click();

    await new Promise(r => setTimeout(r, 3000));

    const inputEl = document.querySelector('#prompt-textarea');
    if (!inputEl) {
        console.error("❌ [Content] Could not find #prompt-textarea!");
        safeSendToBackend({ type: "TASK_FAILED", jobId });
        return;
    }

    inputEl.focus();
    inputEl.innerHTML = ''; 
    inputEl.innerText = text;
    inputEl.dispatchEvent(new Event('input', { bubbles: true }));

    let sendBtn = null;
    let attempts = 0;
    while (!sendBtn && attempts < 30) { 
        sendBtn = document.querySelector('#composer-submit-button') || document.querySelector('[data-testid="send-button"]');
        if (sendBtn && !sendBtn.disabled) break;
        sendBtn = null;
        await new Promise(r => setTimeout(r, 100));
        attempts++;
    }

    if (sendBtn) {
        await new Promise(r => setTimeout(r, 1000));
        inputEl.dispatchEvent(new KeyboardEvent('keydown', { 
            key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true 
        }));
    } else {
        console.error("❌ [Content] Send button did not appear!");
        safeSendToBackend({ type: "TASK_FAILED", jobId });
        return;
    }

    await new Promise(r => setTimeout(r, 3000));
    observeGenerationCompletion(jobId);
}

function observeGenerationCompletion(jobId) {
    let timeoutId = setTimeout(() => {
        console.warn("⏳ [Content] Timeout: ChatGPT took too long.");
        observer.disconnect();
        safeSendToBackend({ type: "TASK_FAILED", jobId });
    }, 120000); // 2 minutes timeout

    const observer = new MutationObserver(async (mutations, obs) => {
        const voiceBtn = document.querySelector('button[aria-label="Start Voice"]');
        
        if (voiceBtn) {
            clearTimeout(timeoutId);
            obs.disconnect();
            console.log("✅ [Content] Generation COMPLETE.");
            await new Promise(r => setTimeout(r, 1000));
            
            const assistantMessages = document.querySelectorAll('[data-message-author-role="assistant"]');
            const lastMsg = assistantMessages[assistantMessages.length - 1];
            
            if (!lastMsg) {
                safeSendToBackend({ type: "TASK_FAILED", jobId });
                return;
            }

            let finalText = "";
            const textElements = lastMsg.querySelectorAll('p, pre, li, h1, h2, h3, h4, h5, h6');
            if (textElements.length > 0) {
                textElements.forEach(el => { finalText += el.innerText + "\n"; });
            } else {
                finalText = lastMsg.innerText;
            }
            finalText = finalText.replace(/\n{3,}/g, '\n\n').trim();

            // 🚀 Send TASK_COMPLETE directly to Python backend!
            safeSendToBackend({
                type: "TASK_COMPLETE",
                jobId: jobId,
                resumeText: finalText
            });
        }
    });

    const chatContainer = document.querySelector('main') || document.body;
    observer.observe(chatContainer, { childList: true, subtree: true });
}