(function () {
  'use strict';

  chrome.storage.local.get(
    ['wordDocPath', 'wordHotkey'],
    function (r) {
      if (r.wordDocPath) document.getElementById('wordPath').value = r.wordDocPath;
      if (r.wordHotkey) {
        document.getElementById('wordHotkey').value = r.wordHotkey;
        document.getElementById('hotkeyPlaceholder').style.display = 'none';
      }
    }
  );

  function saveAll() {
    var wordPath = document.getElementById('wordPath').value.trim();
    var wordHotkey = document.getElementById('wordHotkey').value.trim();
    chrome.storage.local.set({
      wordDocPath: wordPath,
      wordHotkey: wordHotkey || 'F9',
    }, function () {
      var el = document.getElementById('saved');
      el.style.display = 'block';
      setTimeout(function () { el.style.display = 'none'; }, 2000);
    });
  }

  function formatHotkeyFromEvent(e) {
    var mods = [];
    if (e.ctrlKey) mods.push('Ctrl');
    if (e.altKey) mods.push('Alt');
    if (e.shiftKey) mods.push('Shift');
    if (e.metaKey) mods.push('Win');
    var key = e.key;
    if (key === 'Control' || key === 'Shift' || key === 'Alt' || key === 'Meta') return null;
    if ((key === 'Escape' || key === 'Esc' || key === 'Backspace') && !e.ctrlKey && !e.altKey && !e.shiftKey && !e.metaKey) {
      hotkeyInput.value = '';
      hotkeyPlaceholder.textContent = PLACEHOLDER_WAITING;
      hotkeyPlaceholder.style.display = '';
      saveAll();
      return null;
    }
    if (key.length === 1 && key >= 'a' && key <= 'z') key = key.toUpperCase();
    if (key.length === 1 && key >= 'A' && key <= 'Z') return mods.concat(key).join('+');
    if (key.length === 1 && key >= '0' && key <= '9') return mods.concat(key).join('+');
    if (/^F([1-9]|1[0-9]|2[0-4])$/.test(key)) return mods.concat(key).join('+');
    var special = { Tab: 'Tab', Enter: 'Enter', Escape: 'Escape', Esc: 'Escape', ' ': 'Space', Backspace: 'Backspace', Delete: 'Delete', Insert: 'Insert', Home: 'Home', End: 'End', PageUp: 'PageUp', PageDown: 'PageDown', ArrowLeft: 'Left', ArrowRight: 'Right', ArrowUp: 'Up', ArrowDown: 'Down' };
    var mapped = special[key] || special[e.code];
    if (mapped) return mods.concat(mapped).join('+');
    return null;
  }

  var hotkeyInput = document.getElementById('wordHotkey');
  var hotkeyPlaceholder = document.getElementById('hotkeyPlaceholder');
  var PLACEHOLDER_IDLE = 'Click to set hotkey';
  var PLACEHOLDER_WAITING = 'Please press hotkey';

  hotkeyInput.addEventListener('focus', function () {
    hotkeyPlaceholder.textContent = PLACEHOLDER_WAITING;
    hotkeyPlaceholder.style.display = hotkeyInput.value.trim() ? 'none' : '';
  });
  hotkeyInput.addEventListener('blur', function () {
    if (!hotkeyInput.value.trim()) {
      hotkeyPlaceholder.textContent = PLACEHOLDER_IDLE;
      hotkeyPlaceholder.style.display = '';
    } else {
      hotkeyPlaceholder.style.display = 'none';
    }
  });

  hotkeyInput.addEventListener('keydown', function (e) {
    e.preventDefault();
    e.stopPropagation();
    var formatted = formatHotkeyFromEvent(e);
    if (formatted) {
      hotkeyInput.value = formatted;
      hotkeyPlaceholder.style.display = 'none';
      saveAll();
    }
  });
  hotkeyInput.addEventListener('click', function () { hotkeyInput.focus(); });

  document.getElementById('save').addEventListener('click', saveAll);

  var pathBrowseInProgress = false;
  document.getElementById('wordPath').addEventListener('click', function () {
    if (pathBrowseInProgress) return;
    var pathInput = document.getElementById('wordPath');
    pathBrowseInProgress = true;
    pathInput.placeholder = 'Opening...';
    var controller = new AbortController();
    var timeoutId = setTimeout(function () { controller.abort(); }, 60000);
    fetch('http://127.0.0.1:8765/browse-word', { signal: controller.signal })
      .then(function (r) { return r.text(); })
      .then(function (text) {
        var data;
        try {
          data = JSON.parse(text);
        } catch (e) {
          pathInput.placeholder = 'Invalid response from app. Start it first.';
          return;
        }
        if (data && data.success && data.path) {
          pathInput.value = data.path;
          pathInput.placeholder = 'Click to select Word document';
          saveAll();
        } else {
          pathInput.placeholder = 'Click to select Word document';
        }
      })
      .catch(function () {
        pathInput.placeholder = 'App not running? Start it first.';
      })
      .finally(function () {
        clearTimeout(timeoutId);
        pathBrowseInProgress = false;
      });
  });

  document.getElementById('wordPath').addEventListener('blur', saveAll);
  hotkeyInput.addEventListener('blur', saveAll);
})();
