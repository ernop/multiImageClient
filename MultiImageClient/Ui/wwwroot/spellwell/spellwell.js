// SpellWell — drop-in, dictionary-based spell highlighting + one-click local
// fixes for any <textarea>, no build step, no server, no framework.
//
// Origin: grown out of the fuseki4_ai article-editor prototype (typo-js +
// Hunspell en_US + localStorage custom dictionary) and the stalin-mode.html
// screenshot editor; packaged here so it can be copied into any project as a
// folder (spellwell.js + spellwell.css + vendor/typo/{typo.min.js,en_US.aff,
// en_US.dic}).
//
// Usage:
//   <link rel="stylesheet" href="spellwell/spellwell.css">
//   <script src="spellwell/vendor/typo/typo.min.js"></script>
//   <script src="spellwell/spellwell.js"></script>
//   const sw = await SpellWell.create({
//     affUrl: "spellwell/vendor/typo/en_US.aff",
//     dicUrl: "spellwell/vendor/typo/en_US.dic",
//     extraWords: ["recraft", "grok"],            // project jargon, always ok
//     customDictStorageKey: "myapp_spellwell",     // localStorage, user-grown
//   });
//   const ctl = sw.attach(document.querySelector("textarea"));
//   // later: ctl.refresh() after programmatic value changes; ctl.detach().
//   const fix = sw.localFix(textarea.value); // { text, wordChanges, spaceRuns }
//
// Highlighting model (deliberately NOT the browser's red squiggles):
//   .spellwell-mark-misspelled  lowercase word not in any dictionary  -> pink
//   .spellwell-mark-unknown     not in dictionary but plausibly meant -> blue
//                               (Capitalized, ALLCAPS, camelCase)
//   .spellwell-mark-doublespace each extra space in a 2+ space run    -> yellow
// The overlay renders BEHIND the textarea (transparent text, colored
// backgrounds only), so typing latency and native selection are untouched.

var SpellWell = (function () {
  "use strict";

  var WORD_RE = /[A-Za-z]+(?:['\u2019][A-Za-z]+)*/g;
  var TOKEN_RE = /([A-Za-z]+(?:['\u2019][A-Za-z]+)*)|( {2,})/g;

  // Classic finger-slips whose right answer typo-js ranks poorly or misses
  // entirely (its suggest() for "teh" doesn't even include "the"). Checked
  // before the suggestion machinery; extensible per-project via
  // options.autofixMap.
  var COMMON_TYPOS = {
    teh: "the", hte: "the", taht: "that", thsi: "this", tihs: "this",
    adn: "and", jsut: "just", waht: "what", wiht: "with", thier: "their",
    woudl: "would", coudl: "could", beleive: "believe", untill: "until",
    wierd: "weird", becuase: "because", tommorow: "tomorrow", tommorrow: "tomorrow",
    dont: "don't", doesnt: "doesn't", isnt: "isn't", didnt: "didn't",
    wasnt: "wasn't", couldnt: "couldn't", wouldnt: "wouldn't", shouldnt: "shouldn't",
  };

  // Optimal-string-alignment distance (Levenshtein + adjacent transposition
  // counted as 1), which is what typing errors actually look like.
  function editDistance(a, b) {
    var la = a.length, lb = b.length;
    if (Math.abs(la - lb) > 2) return 3;
    var d = [];
    for (var i = 0; i <= la; i++) { d[i] = [i]; }
    for (var j = 0; j <= lb; j++) { d[0][j] = j; }
    for (i = 1; i <= la; i++) {
      for (j = 1; j <= lb; j++) {
        var cost = a[i - 1] === b[j - 1] ? 0 : 1;
        d[i][j] = Math.min(d[i - 1][j] + 1, d[i][j - 1] + 1, d[i - 1][j - 1] + cost);
        if (i > 1 && j > 1 && a[i - 1] === b[j - 2] && a[i - 2] === b[j - 1]) {
          d[i][j] = Math.min(d[i][j], d[i - 2][j - 2] + 1);
        }
      }
    }
    return d[la][lb];
  }

  // True when b is a by exactly one adjacent-character swap (teh/the,
  // recieve/receive, wierd/weird) — the single most characteristic typo.
  function isAdjacentTransposition(a, b) {
    if (a.length !== b.length || a === b) return false;
    for (var i = 0; i < a.length - 1; i++) {
      if (a[i] !== b[i]) {
        return a[i] === b[i + 1] && a[i + 1] === b[i]
          && a.slice(i + 2) === b.slice(i + 2);
      }
    }
    return false;
  }

  function sharedPrefixSuffixLength(a, b) {
    var prefix = 0;
    while (prefix < a.length && prefix < b.length && a[prefix] === b[prefix]) prefix++;
    var suffix = 0;
    while (suffix < a.length - prefix && suffix < b.length - prefix
      && a[a.length - 1 - suffix] === b[b.length - 1 - suffix]) suffix++;
    return prefix + suffix;
  }

  function escapeHtml(s) {
    return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  }

  function Checker(dict, options) {
    this.dict = dict;
    this.extraWords = new Set((options.extraWords || []).map(function (w) { return w.toLowerCase(); }));
    this.storageKey = options.customDictStorageKey || "spellwell_custom_dict";
    this.autofixMap = Object.assign({}, COMMON_TYPOS, options.autofixMap || {});
    this.suggestionCache = new Map();
    this.customWords = new Set();
    this.loadCustomDict();
  }

  Checker.prototype.loadCustomDict = function () {
    try {
      var stored = JSON.parse(localStorage.getItem(this.storageKey) || "[]");
      this.customWords = new Set(stored.map(function (w) { return String(w).toLowerCase(); }));
    } catch (e) {
      this.customWords = new Set();
    }
  };

  Checker.prototype.saveCustomDict = function () {
    localStorage.setItem(this.storageKey, JSON.stringify(Array.from(this.customWords).sort()));
  };

  Checker.prototype.addCustomWord = function (word) {
    this.customWords.add(String(word).toLowerCase());
    this.saveCustomDict();
  };

  Checker.prototype.removeCustomWord = function (word) {
    this.customWords.delete(String(word).toLowerCase());
    this.saveCustomDict();
  };

  Checker.prototype.listCustomWords = function () {
    return Array.from(this.customWords).sort();
  };

  // "ok" | "misspelled" | "unknown". Deliberately heuristic and predictable:
  // a plain-lowercase word the dictionary doesn't know is a misspelling
  // (pink); anything shaped like a name/acronym/identifier is unknown (blue).
  // Sentence-initial capitalized typos therefore read as unknown — acceptable
  // for the "don't nag me about proper nouns" trade this makes. A lowercase
  // word whose Capitalized form IS in the dictionary (english, virginians,
  // mainer) is also unknown, not misspelled: it's a casually-lowercased
  // proper noun, and "correcting" it to an unrelated word (english→anguish)
  // would be vandalism.
  Checker.prototype.classify = function (word) {
    if (word.length <= 1) return "ok";
    var lower = word.toLowerCase();
    var normalizedApostrophe = lower.replace(/\u2019/g, "'");
    if (this.customWords.has(normalizedApostrophe) || this.extraWords.has(normalizedApostrophe)) return "ok";
    var plain = word.replace(/\u2019/g, "'");
    if (this.dict.check(plain)) return "ok";
    if (plain !== normalizedApostrophe && this.dict.check(normalizedApostrophe)) return "ok";
    if (word !== lower) return "unknown";
    if (this.dict.check(plain.charAt(0).toUpperCase() + plain.slice(1))) return "unknown";
    return "misspelled";
  };

  Checker.prototype.suggest = function (word, limit) {
    var key = word + "\u0000" + (limit || 3);
    if (!this.suggestionCache.has(key)) {
      this.suggestionCache.set(key, this.dict.suggest(word.replace(/\u2019/g, "'"), limit || 3));
    }
    return this.suggestionCache.get(key);
  };

  // Tokenizes text into { kind: "word"|"doublespace", value, start, end,
  // classification } entries. Only issues are returned; clean text yields [].
  Checker.prototype.analyze = function (text) {
    var issues = [];
    TOKEN_RE.lastIndex = 0;
    var m;
    while ((m = TOKEN_RE.exec(text)) !== null) {
      if (m[1] !== undefined) {
        var cls = this.classify(m[1]);
        if (cls !== "ok") {
          issues.push({ kind: "word", value: m[1], start: m.index, end: m.index + m[1].length, classification: cls });
        }
      } else {
        issues.push({ kind: "doublespace", value: m[2], start: m.index, end: m.index + m[2].length });
      }
    }
    return issues;
  };

  // Picks a correction for a misspelled word, or null when nothing is safe to
  // apply. Precision over recall — a wrong "fix" is worse than a highlight:
  //   1. the common-typos map wins outright (typo-js ranks these badly);
  //   2. otherwise take typo-js suggestions within edit distance 2 and keep
  //      the minimum-distance ones (transposition counts as 1 edit);
  //   3. a distance tie prefers a UNIQUE adjacent-transposition candidate
  //      (recieve→receive beats relieve; wierd→weird beats wield), then a
  //      strictly longer shared prefix+suffix; anything still ambiguous is
  //      left alone and stays highlighted rather than guessed.
  Checker.prototype.pickCorrection = function (word) {
    var mapped = this.autofixMap[word.toLowerCase()];
    if (mapped) return mapped;
    var suggestions = this.suggest(word, 8) || [];
    var lower = word.toLowerCase();
    var bestDistance = 3;
    for (var i = 0; i < suggestions.length; i++) {
      bestDistance = Math.min(bestDistance, editDistance(lower, suggestions[i].toLowerCase()));
    }
    if (bestDistance >= 3) return null;
    var candidates = suggestions.filter(function (s) { return editDistance(lower, s.toLowerCase()) === bestDistance; });
    if (candidates.length === 1) return candidates[0];
    var transpositions = candidates.filter(function (s) { return isAdjacentTransposition(lower, s.toLowerCase()); });
    if (transpositions.length === 1) return transpositions[0];
    candidates.sort(function (a, b) {
      return sharedPrefixSuffixLength(lower, b.toLowerCase()) - sharedPrefixSuffixLength(lower, a.toLowerCase());
    });
    var top = sharedPrefixSuffixLength(lower, candidates[0].toLowerCase());
    var second = sharedPrefixSuffixLength(lower, candidates[1].toLowerCase());
    return top > second ? candidates[0] : null;
  };

  // One-click local fix: collapse every 2+ space run to a single space and
  // replace each pink (misspelled) word with a confidently-chosen correction
  // (see pickCorrection). Ambiguous words, words with no usable suggestion,
  // and blue (unknown) words are left alone — they stay highlighted instead
  // of being guessed at. Returns the new text plus an exact change list so
  // the caller can report and undo.
  Checker.prototype.localFix = function (text) {
    var wordChanges = [];
    var self = this;
    var fixedWords = text.replace(WORD_RE, function (word, offset) {
      if (self.classify(word) !== "misspelled") return word;
      var correction = self.pickCorrection(word);
      if (!correction) return word;
      wordChanges.push({ from: word, to: correction, offset: offset });
      return correction;
    });
    var spaceRuns = 0;
    var fixed = fixedWords.replace(/ {2,}/g, function () { spaceRuns++; return " "; });
    return { text: fixed, wordChanges: wordChanges, spaceRuns: spaceRuns };
  };

  // ---------- overlay rendering ----------

  var MIRRORED_STYLES = [
    "fontFamily", "fontSize", "fontWeight", "fontStyle", "letterSpacing",
    "lineHeight", "textTransform", "wordSpacing", "textIndent",
    "paddingTop", "paddingRight", "paddingBottom", "paddingLeft",
    "borderTopWidth", "borderRightWidth", "borderBottomWidth", "borderLeftWidth",
    "borderRadius", "boxSizing",
  ];

  Checker.prototype.renderHtml = function (text) {
    var out = [];
    var last = 0;
    TOKEN_RE.lastIndex = 0;
    var m;
    while ((m = TOKEN_RE.exec(text)) !== null) {
      out.push(escapeHtml(text.slice(last, m.index)));
      if (m[1] !== undefined) {
        var cls = this.classify(m[1]);
        if (cls === "ok") {
          out.push(escapeHtml(m[1]));
        } else {
          out.push('<mark class="spellwell-mark-' + cls + '">' + escapeHtml(m[1]) + "</mark>");
        }
      } else {
        // Each space of the run gets its own boxed mark so a double space
        // reads as two adjacent little squares.
        for (var i = 0; i < m[2].length; i++) {
          out.push('<mark class="spellwell-mark-doublespace"> </mark>');
        }
      }
      last = m.index + m[0].length;
    }
    out.push(escapeHtml(text.slice(last)));
    // A trailing newline needs a visible line for scroll-height parity.
    out.push("\n");
    return out.join("");
  };

  // Wraps the textarea in a positioning host and slides a mirrored backdrop
  // underneath it. The textarea keeps focus/selection/native behavior; only
  // its background becomes transparent so the marks show through.
  Checker.prototype.attach = function (textarea) {
    var self = this;
    var computed = getComputedStyle(textarea);

    var host = document.createElement("div");
    host.className = "spellwell-host";
    var backdrop = document.createElement("div");
    backdrop.className = "spellwell-backdrop";
    backdrop.setAttribute("aria-hidden", "true");

    MIRRORED_STYLES.forEach(function (prop) {
      backdrop.style[prop] = computed[prop];
    });
    backdrop.style.background = computed.backgroundColor;

    textarea.parentNode.insertBefore(host, textarea);
    host.appendChild(backdrop);
    host.appendChild(textarea);
    textarea.classList.add("spellwell-textarea");
    // SpellWell's marks replace the browser's red squiggles.
    textarea.spellcheck = false;

    var lastRendered = null;
    var enabled = true;

    // The backdrop must mirror the textarea's CLIENT box (plus borders), not
    // its offset box: a vertical scrollbar shrinks the client width and would
    // otherwise skew where lines wrap.
    function syncGeometry() {
      var bl = parseFloat(computed.borderLeftWidth) || 0;
      var br = parseFloat(computed.borderRightWidth) || 0;
      var bt = parseFloat(computed.borderTopWidth) || 0;
      var bb = parseFloat(computed.borderBottomWidth) || 0;
      backdrop.style.width = (textarea.clientWidth + bl + br) + "px";
      backdrop.style.height = (textarea.clientHeight + bt + bb) + "px";
    }

    function refresh() {
      if (!enabled) return;
      if (textarea.value !== lastRendered) {
        lastRendered = textarea.value;
        backdrop.innerHTML = self.renderHtml(textarea.value);
        syncGeometry();
      }
      backdrop.scrollTop = textarea.scrollTop;
      backdrop.scrollLeft = textarea.scrollLeft;
    }

    textarea.addEventListener("input", refresh);
    textarea.addEventListener("scroll", refresh);
    var resizeObserver = new ResizeObserver(function () {
      syncGeometry();
      refresh();
    });
    resizeObserver.observe(textarea);
    // Programmatic .value writes fire no event; a light poll keeps the
    // overlay honest without every caller having to remember refresh().
    var pollTimer = setInterval(refresh, 700);
    refresh();

    return {
      refresh: refresh,
      setEnabled: function (on) {
        enabled = !!on;
        backdrop.style.visibility = enabled ? "visible" : "hidden";
        textarea.spellcheck = !enabled;
        if (enabled) { lastRendered = null; refresh(); }
      },
      detach: function () {
        clearInterval(pollTimer);
        resizeObserver.disconnect();
        textarea.removeEventListener("input", refresh);
        textarea.removeEventListener("scroll", refresh);
        textarea.classList.remove("spellwell-textarea");
        textarea.spellcheck = true;
        host.parentNode.insertBefore(textarea, host);
        host.remove();
      },
    };
  };

  // ---------- factory ----------

  function create(options) {
    if (typeof Typo === "undefined") {
      return Promise.reject(new Error("SpellWell: typo.min.js must be loaded first (spellwell/vendor/typo/typo.min.js)"));
    }
    if (!options || !options.affUrl || !options.dicUrl) {
      return Promise.reject(new Error("SpellWell.create requires { affUrl, dicUrl }"));
    }
    return Promise.all([
      fetch(options.affUrl).then(function (r) {
        if (!r.ok) throw new Error("SpellWell: failed to load " + options.affUrl + " (HTTP " + r.status + ")");
        return r.text();
      }),
      fetch(options.dicUrl).then(function (r) {
        if (!r.ok) throw new Error("SpellWell: failed to load " + options.dicUrl + " (HTTP " + r.status + ")");
        return r.text();
      }),
    ]).then(function (parts) {
      var dict = new Typo("en_US", parts[0], parts[1]);
      return new Checker(dict, options);
    });
  }

  return { create: create };
})();
