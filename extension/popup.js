(function () {
  'use strict';

  // Version pill — verifies which extension build is loaded.
  try {
    var v = chrome.runtime.getManifest().version;
    var el = document.getElementById('version');
    if (el) el.textContent = 'v' + v;
  } catch (e) { /* ignore */ }
})();
