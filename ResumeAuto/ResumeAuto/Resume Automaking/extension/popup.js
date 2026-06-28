document.addEventListener('DOMContentLoaded', () => {
    const statusEl = document.getElementById('status');
    const controlsEl = document.getElementById('controls');
    const disabledMsgEl = document.getElementById('disabled-msg');
    const btnConnect = document.getElementById('btn-connect');
    const btnReloadExt = document.getElementById('btn-reload-ext');
    const btnReloadPage = document.getElementById('btn-reload-page');

    // Check if current tab is ChatGPT
    chrome.tabs.query({ active: true, currentWindow: true }, (tabs) => {
        if (tabs.length > 0 && tabs[0].url) {
            const url = tabs[0].url;
            // If not a ChatGPT page, hide controls and show message
            if (!url.includes('chatgpt.com') && !url.includes('chat.openai.com')) {
                controlsEl.style.display = 'none';
                disabledMsgEl.style.display = 'block';
                statusEl.className = 'status disconnected';
                statusEl.textContent = 'Not on ChatGPT';
                return;
            }
        }
        
        // If it is a ChatGPT page, enable controls and get state
        controlsEl.style.display = 'block';
        disabledMsgEl.style.display = 'none';
        
        // Get initial state from background
        chrome.runtime.sendMessage({ action: "GET_WS_STATE" }, (response) => {
            if (response) updateUI(response.state);
        });
    });

    // Listen for state changes from background
    chrome.runtime.onMessage.addListener((request, sender, sendResponse) => {
        if (request.action === "WS_STATE_CHANGED") {
            updateUI(request.state);
        }
    });

    // Update UI based on connection state
    function updateUI(state) {
        statusEl.className = 'status ' + state.toLowerCase();
        
        if (state === 'CONNECTED') {
            statusEl.textContent = 'Connected';
            btnConnect.textContent = 'Connected';
            btnConnect.disabled = true;
        } else if (state === 'CONNECTING') {
            statusEl.textContent = 'Connecting...';
            btnConnect.textContent = 'Connecting...';
            btnConnect.disabled = true;
        } else {
            statusEl.textContent = 'Disconnected';
            btnConnect.textContent = 'Connect to Server';
            btnConnect.disabled = false;
        }
    }

    // Button Event Listeners
    btnConnect.addEventListener('click', () => {
        chrome.runtime.sendMessage({ action: "CONNECT_WS" });
    });

    btnReloadExt.addEventListener('click', () => {
        chrome.runtime.sendMessage({ action: "RELOAD_EXTENSION" });
        window.close(); // Close popup after reloading extension
    });

    btnReloadPage.addEventListener('click', () => {
        chrome.runtime.sendMessage({ action: "RELOAD_PAGE" });
        window.close(); // Close popup after reloading page
    });
});