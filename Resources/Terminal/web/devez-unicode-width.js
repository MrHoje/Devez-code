(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  root.DevezUnicodeWidth = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  const VERSION = '6-devezvibe-paw2';
  const PAW_PRINTS = 0x1F43E;

  function install(term, agent) {
    if (agent !== 'devezvibe') return false;
    const service = term && term._core && term._core.unicodeService;
    const base = service && service._activeProvider;
    if (!base || typeof base.wcwidth !== 'function' || typeof base.charProperties !== 'function') {
      return false;
    }

    const provider = {
      version: VERSION,
      wcwidth: function (codepoint) {
        return codepoint === PAW_PRINTS ? 2 : base.wcwidth(codepoint);
      },
      charProperties: function (codepoint, preceding) {
        const value = base.charProperties(codepoint, preceding);
        // xterm stores width in bits 1..2. Preserve its joining flag and every
        // other property, changing only the observed paw-print width to two.
        return codepoint === PAW_PRINTS ? (value & ~6) | 4 : value;
      }
    };
    term.unicode.register(provider);
    term.unicode.activeVersion = VERSION;
    return true;
  }

  return { install: install, version: VERSION };
});
