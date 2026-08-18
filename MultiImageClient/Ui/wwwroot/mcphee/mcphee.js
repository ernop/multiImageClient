// McPhee — drop-in, dictionary-based spell highlighting + one-click local
// fixes for any <textarea>, no build step, no server, no framework.
//
// Named for John McPhee, who ran Kedit's All command over every piece to see
// each use of a chosen word and the distance between occurrences: a
// distinctive word got one appearance per piece, and bunched ordinary words
// got respaced ("Draft No. 4", essay "Structure"). The repetition detectors
// below automate that check.
//
// Distribution is copy-the-folder: host projects receive verbatim copies of
// this folder (mcphee.js + mcphee.css + vendor/); check McPhee.version
// against CHANGELOG.md to see how far behind a copy is.
//
// Usage:
//   <link rel="stylesheet" href="mcphee/mcphee.css">
//   <script src="mcphee/vendor/typo/typo.min.js"></script>
//   <script src="mcphee/mcphee.js"></script>
//   const sw = await McPhee.create({
//     affUrl: "mcphee/vendor/typo/en_US.aff",
//     dicUrl: "mcphee/vendor/typo/en_US.dic",
//     freqUrl: "mcphee/vendor/wordfreq/en-30k.txt", // optional; powers
//                                                     // word-frequency rules
//     extraWords: ["recraft", "grok"],            // project jargon, always ok
//     customDictStorageKey: "myapp_mcphee",     // localStorage, user-grown
//     profile: "standard",                         // default rule profile
//   });
  //   const ctl = sw.attach(document.querySelector("textarea"));
  //   const panel = sw.attachPanel({ textarea, container, controller: ctl });
  //   const guard = sw.guardForm(form, { blockOn: ["misspelled"], watch: true });
  //   sw.applyFixes(textarea);   // undo-preserving one-click fix
  //   sw.applyNearestBackwardFix(textarea);  // Control-tap: nearest behind caret
//
// Rule profiles (see McPhee.profiles):
//   standard  misspelled + unknown + doublespace + echo + obscureRepeat
//   strict    standard + sentenceCapitalization + terminalPunctuation
//   casual    misspelled + doublespace only — no unknown-word nagging, no
//             capitalization/punctuation rules, no repetition detectors (for
//             contexts where lowercase proper nouns and unpunctuated prose
//             are intentional)
// Any create/attach/analyze call takes { profile } or { rules: {...} }
// overrides; word lists (extraWords, custom dictionary) always apply.
//
// Repetition detectors (the McPhee rules — a distinctive word ordinarily
// earns one appearance per piece, and bunched ordinary words betray the ear):
//   echo           the same content word or exact multi-word phrase
//                  reappears within echoWindowWords words (default 50).
//                  Single-word echoes exempt function words and words ranked
//                  more common than echoCommonRank (default 2000); phrase
//                  echoes inspect every dictionary-known word, including
//                  function words, without a curated phrase list.
//   obscureRepeat  a dictionary word ranked rarer than obscureRank (default
//                  10000) — or absent from the frequency list entirely —
//                  used 2+ times anywhere in the text. Requires freqUrl.
// Personal-dictionary and extraWords entries are exempt from both (they are
// the text's topic vocabulary), as are words on the persistent not-rare
// list (checker.markNotRare(word) — the correction for frequency-list gaps
// such as contractions). There is no autofix — word choice is the author's
// call; the panel offers hover-to-scroll and a session-scoped dismiss per
// word or phrase (checker.ignoreRepeat(value)).
//
// Highlighting model (deliberately NOT the browser's red squiggles):
//   .mcphee-mark-misspelled      lowercase word not in any dictionary -> pink
//   .mcphee-mark-unknown         not in dictionary but plausibly meant -> blue
//                                   (Capitalized, ALLCAPS, camelCase)
//   .mcphee-mark-doublespace     an ILLEGITIMATE extra-space run, marked as
//                                   ONE joined yellow rectangle (no internal
//                                   divisions). Two legitimate space patterns
//                                   are never flagged: exactly two spaces
//                                   after sentence-ending punctuation (the
//                                   author double-spaces sentences on
//                                   purpose) and line-leading indentation
//                                   (Markdown code blocks / list continuations)
//   .mcphee-mark-capitalization  lowercase sentence-start word -> orange
//   .mcphee-mark-punctuation     text ends without terminal punctuation
//                                   (last character boxed) -> orange outline
//   .mcphee-mark-echo            same word or phrase reused nearby -> lavender
//   .mcphee-mark-obscure         rare word reused in the text -> green
//   .mcphee-mark-culture         proper name written lowercase (jupiter,
//                                   japanese, usa) -> teal, with the cased fix
// The overlay renders BEHIND the textarea (transparent text, colored
// backgrounds only), so typing latency and native selection are untouched.

var McPhee = (function () {
  "use strict";

  var VERSION = "3.10.0";

  var WORD_RE = /[A-Za-z]+(?:['\u2019][A-Za-z]+)*/g;
  var TOKEN_RE = /([A-Za-z]+(?:['\u2019][A-Za-z]+)*)|( {2,})/g;
  var SENTENCE_START_RE = /(?:^|[.!?\u2026]["')\]]?\s+)([a-z])/g;
  var TERMINAL_PUNCT_RE = /[.!?\u2026:;"'\u2019\u201d)\]]$/;
  var SENTENCE_ENDS = ".!?\u2026";
  var TRAILING_CLOSERS = "\"'\u2019\u201d)]";

  // Named rule profiles, presented in UIs as the formality ladder:
  // casual < standard ("normal") < strict ("formal"). "casual" is the
  // no-nagging mode for contexts where lowercase proper nouns ("japanese"),
  // lowercase i, and unpunctuated prose are the author's intent; "strict"
  // demands the full rigamarole: sentences start capitalized, text ends
  // punctuated. Exact parameters for every rule are documented in
  // docs/integration.md ("Rule catalog").
  var PROFILES = {
    standard: {
      misspelled: true, unknown: true, doublespace: true,
      sentenceCapitalization: false, terminalPunctuation: false,
      echo: true, obscureRepeat: true, culture: true,
    },
    strict: {
      misspelled: true, unknown: true, doublespace: true,
      sentenceCapitalization: true, terminalPunctuation: true,
      echo: true, obscureRepeat: true, culture: true,
    },
    casual: {
      misspelled: true, unknown: false, doublespace: true,
      sentenceCapitalization: false, terminalPunctuation: false,
      echo: false, obscureRepeat: false, culture: false,
    },
  };

  // Formality ladder shown by the panel chooser; maps display names to
  // profiles. "normal" is the historical standard behavior.
  var FORMALITY_LEVELS = [
    { id: "casual", label: "casual" },
    { id: "standard", label: "normal" },
    { id: "strict", label: "formal" },
  ];

  // ---------- culture rule: nation/group/language names ----------
  // Proper nouns of nationality, place, language, religion, and ethnicity
  // written in lowercase ("japanese", "usa", "english") get their own
  // gentle category instead of drowning among unknown-word flags. The list
  // is deliberately conservative: entries whose lowercase form is a common
  // English word (turkey, china, polish, us...) are excluded because
  // flagging them would produce constant false positives — "Black" as an
  // ethnonym is the clearest example: lowercase "black" is a color far more
  // often, so it is NOT in the default list. Add such words per project via
  // options.cultureWords when the writing context justifies it.
  var CULTURE_ALLCAPS = {
    usa: "USA", uk: "UK", uae: "UAE", ussr: "USSR", eu: "EU", nato: "NATO",
    nyc: "NYC", la: "LA", sf: "SF", ddr: "DDR",
  };
  var CULTURE_WORDS = new Set(("american america americans english england britain british french france german germany germans spanish spain italian italy italians portuguese portugal dutch netherlands belgian belgium swiss switzerland austrian austria greek greece greeks russian russia russians ukrainian ukraine poland czech slovak slovakia hungarian hungary romanian romania bulgarian bulgaria serbian serbia croatian croatia danish denmark swedish sweden swedes norwegian norway finnish finland icelandic iceland irish ireland scottish scotland welsh wales japanese japan chinese korean korea koreans vietnamese vietnam thai thailand indian india indians pakistani pakistan bangladeshi bangladesh nepali nepal indonesian indonesia malaysian malaysia filipino filipinos philippines singaporean singapore mongolian mongolia taiwanese taiwan tibetan tibet african africa africans egyptian egypt moroccan morocco algerian algeria tunisian tunisia libyan libya nigerian nigeria kenyan kenya ethiopian ethiopia ghanaian ghana somali somalia sudanese sudan ugandan uganda tanzanian tanzania zimbabwean zimbabwe rwandan rwanda senegalese senegal cameroonian cameroon congolese congo mexican mexico mexicans canadian canada canadians brazilian brazil brazilians argentine argentina chilean peruvian peru colombian colombia venezuelan venezuela bolivian bolivia ecuadorian ecuador uruguayan uruguay paraguayan paraguay cuban cubans haitian haiti jamaican jamaica australian australia australians iranian iran iranians iraqi iraq israeli israel israelis palestinian palestine palestinians syrian syria lebanese lebanon saudi arabia arabian arabic turkish armenian armenia azerbaijani azerbaijan georgian kazakh kazakhstan uzbek uzbekistan afghan afghanistan afghans european europe europeans asian asia asians latino latina latinos hispanic hispanics arab arabs jewish jew jews christian christians muslim muslims islamic islam buddhist buddhists hindu hindus catholic catholics protestant protestants mormon mormons sikh sikhs london paris tokyo moscow beijing berlin madrid rome vienna amsterdam dublin edinburgh stockholm oslo copenhagen helsinki warsaw prague budapest athens istanbul cairo delhi mumbai seoul bangkok hanoi manila jakarta sydney melbourne toronto vancouver chicago boston seattle texas california florida hawaii alaska").split(" "));

  // Expected capitalized form for a lowercase culture word.
  function cultureExpected(lower) {
    if (CULTURE_ALLCAPS[lower]) return CULTURE_ALLCAPS[lower];
    if (CULTURE_WORDS.has(lower)) return lower.charAt(0).toUpperCase() + lower.slice(1);
    return null;
  }

  // Function words that repeat constantly in healthy prose; never echo
  // candidates even without a frequency list. Content words only get past
  // this AND the echoCommonRank frequency gate. Contractions are included
  // as their own closed class: the vendored frequency list's web corpus
  // lost apostrophes, so without these entries "won't" would count as an
  // unranked (hence "rare") word.
  var STOPWORDS = new Set(("a about above after again against all also although always am an and any are around as at be because been before being below between both but by came can cannot come could day did do does doing down during each even every few first for from get give go good got had has have having he her here hers herself him himself his how however i if in into is it its itself just know like little long made make many may me might more most much must my myself never new no nor not now of off on once one only onto or other our ours ourselves out over own said same see she should since so some still such take than that the their theirs them themselves then there these they this those through time to too two under until up upon us used very was way we well went were what when where which while who whom why will with without would year you your yours yourself yourselves"
    + " ain't aren't can't couldn't didn't doesn't don't hadn't hasn't haven't he'd he'll i'd i'll i'm i've isn't it'll mightn't mustn't needn't shan't she'd she'll shouldn't that'll there'd there'll they'd they'll they're they've wasn't we'd we'll we're we've weren't won't wouldn't you'd you'll you're you've could've might've must've should've would've y'all o'clock").split(" "));

  // Lowercases and strips a possessive; the shared normal form for the
  // repetition detectors.
  function normWord(word) {
    var n = word.toLowerCase().replace(/\u2019/g, "'");
    if (n.slice(-2) === "'s") n = n.slice(0, -2);
    return n;
  }

  // Naive plural fold: "leopards" counts as another "leopard". Only strips a
  // bare trailing s from longer words, so short words and double-s words are
  // left alone; good enough for repetition detection, not for grammar.
  function pluralKey(norm) {
    return norm.length > 4 && norm.slice(-1) === "s" && norm.slice(-2) !== "ss"
      ? norm.slice(0, -1) : norm;
  }

  // Exact phrase matching is case/apostrophe-insensitive but deliberately
  // does not stem: "at all" matches "At all", while two phrases with
  // different nouns or inflections remain different phrases.
  function phraseTokenNorm(word) {
    return word.toLowerCase().replace(/\u2019/g, "'");
  }

  // Session-dismissal key for either a word or a phrase. Words retain the
  // historical possessive/plural folding; phrases use their exact normalized
  // token sequence.
  function repeatKey(value) {
    var tokens = [];
    WORD_RE.lastIndex = 0;
    var m;
    while ((m = WORD_RE.exec(String(value))) !== null) {
      tokens.push(phraseTokenNorm(m[0]));
    }
    return tokens.length > 1 ? tokens.join(" ") : pluralKey(normWord(String(value)));
  }

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

  function escapeRegExp(s) {
    return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  }

  // The word the caret is inside or at the edge of — the token still being
  // typed. A caret in the space after a word is not in that word.
  function wordAtCaret(text, caret) {
    if (typeof caret !== "number" || caret < 0) return null;
    WORD_RE.lastIndex = 0;
    var m;
    while ((m = WORD_RE.exec(text)) !== null) {
      var start = m.index, end = start + m[0].length;
      if (caret >= start && caret <= end) {
        return { start: start, end: end, value: m[0] };
      }
      if (start > caret) break;
    }
    return null;
  }

  // Display-only: drop spelling nags on the in-progress word. analyze()
  // still finds that word (form guards and the Control-tap fixer use the
  // unfiltered result); overlay and panel pass opts.caret so the author
  // is not told the word they are currently typing is misspelled.
  function hideTypingWord(issues, text, caret) {
    var span = wordAtCaret(text, caret);
    if (!span) return issues;
    return issues.filter(function (issue) {
      if (issue.start < span.start || issue.end > span.end) return true;
      return issue.kind !== "word" && issue.kind !== "culture"
        && issue.kind !== "capitalization";
    });
  }

  // Classifies a run of 2+ spaces. Returns null when the run is legitimate:
  // line-leading indentation (start of text or right after a newline), or a
  // deliberate two-space sentence separator — exactly two spaces following
  // sentence-ending punctuation, closing quotes/brackets allowed in between.
  // Violations return the string the run should collapse to: a sentence
  // separator that grew to 3+ spaces collapses back to two, everything else
  // to one.
  function classifySpaceRun(text, start, length) {
    if (start === 0 || text.charAt(start - 1) === "\n") return null;
    var i = start - 1;
    while (i >= 0 && TRAILING_CLOSERS.indexOf(text.charAt(i)) !== -1) i--;
    var afterSentence = i >= 0 && SENTENCE_ENDS.indexOf(text.charAt(i)) !== -1;
    if (afterSentence && length === 2) return null;
    return { collapseTo: afterSentence ? "  " : " " };
  }

  // Character offsets of every lowercase letter that begins a sentence.
  function sentenceStartOffsets(text) {
    var offsets = new Set();
    SENTENCE_START_RE.lastIndex = 0;
    var m;
    while ((m = SENTENCE_START_RE.exec(text)) !== null) {
      offsets.add(m.index + m[0].length - 1);
    }
    return offsets;
  }

  // Replaces textarea[start..end) with `replacement` through the browser's
  // editing pipeline so the native undo stack survives (direct .value writes
  // detach it — a bug an earlier prototype shipped with).
  // execCommand("insertText") is deprecated but remains the only
  // undo-integrated programmatic edit; setRangeText is the fallback.
  function replaceRange(textarea, start, end, replacement) {
    textarea.focus();
    textarea.setSelectionRange(start, end);
    var ok = false;
    try {
      ok = document.execCommand("insertText", false, replacement);
    } catch (e) { ok = false; }
    if (!ok) {
      textarea.setRangeText(replacement, start, end, "end");
      textarea.dispatchEvent(new Event("input", { bubbles: true }));
    }
  }

  // Maps a caret position through a whole-text rewrite using the common
  // prefix/suffix of the two versions: a caret in unchanged leading or
  // trailing text keeps its exact spot; a caret inside the changed region
  // lands at that region's end. Whole-text replaceRange otherwise leaves
  // the caret at the end of the document.
  function caretAfterRewrite(oldText, newText, caret) {
    var max = Math.min(oldText.length, newText.length);
    var p = 0;
    while (p < max && oldText.charCodeAt(p) === newText.charCodeAt(p)) p++;
    var s = 0;
    while (s < max - p
      && oldText.charCodeAt(oldText.length - 1 - s) === newText.charCodeAt(newText.length - 1 - s)) s++;
    if (caret <= p) return caret;
    if (caret >= oldText.length - s) return newText.length - (oldText.length - caret);
    return newText.length - s;
  }

  function Checker(dict, options, freqRank) {
    this.dict = dict;
    this.extraWords = new Set((options.extraWords || []).map(function (w) { return w.toLowerCase(); }));
    this.storageKey = options.customDictStorageKey || "mcphee_custom_dict";
    this.autofixMap = Object.assign({}, COMMON_TYPOS, options.autofixMap || {});
    this.defaultRules = this.resolveRules(options);
    this.suggestionCache = new Map();
    this.customWords = new Set();
    this.loadCustomDict();
    // Per-project additions to the culture list (e.g. group names like
    // "Black" that are too ambiguous for the default list).
    this.cultureWords = new Set((options.cultureWords || []).map(function (w) { return w.toLowerCase(); }));
    // Exclusion zones: spans of text invisible to EVERY rule — no spelling,
    // spacing, capitalization, culture, or repetition checks, and no fixes.
    // An array of global RegExps (each match is a zone) or a function
    // (text) -> [[start, end), ...]. The host page knows its own markup:
    // a wiki-style editor might exclude double-brace template blocks, a code
    // site fenced blocks, a URL-heavy site bare URLs.
    this.exclude = options.exclude || null;
    // Persistent per-word mute: unlike +dict this doesn't teach the
    // dictionary anything, it just stops flagging the exact word.
    this.ignoreStorageKey = options.ignoreStorageKey || (this.storageKey + ":ignored");
    this.ignoredWords = new Set();
    this.loadIgnoredWords();
    // Repetition-detector state: word -> frequency rank (1 = most common),
    // tuning knobs, and the session's "this repetition is deliberate" set.
    this.freqRank = freqRank || null;
    this.echoWindowWords = options.echoWindowWords || 50;
    this.echoCommonRank = options.echoCommonRank || 2000;
    this.obscureRank = options.obscureRank || 10000;
    this.ignoredRepeats = new Set();
    // Persistent "not actually rare" list: the vendored frequency list has
    // gaps (its web corpus lost apostrophes, so contractions like "won't"
    // are unranked and would count as obscure). Words here are treated as
    // maximally common — never obscure, exempt from echo like any common
    // word.
    this.notRareStorageKey = options.notRareStorageKey || (this.storageKey + ":notrare");
    this.notRareWords = new Set();
    this.loadNotRareWords();
  }

  // Session-scoped: silences echo/obscureRepeat for this word or exact phrase
  // until reload. Permanent word exemption = add it to the personal
  // dictionary.
  Checker.prototype.ignoreRepeat = function (value) {
    this.ignoredRepeats.add(repeatKey(value));
  };

  // Frequency rank of a word's normal form, plural-folded; Infinity when the
  // word is rarer than the vendored list, null when no list was loaded.
  // Words on the not-rare list rank as 1. Contractions fall back to their
  // apostrophe-stripped form (won't -> wont), since the list's web corpus
  // lost apostrophes.
  Checker.prototype.rankOf = function (norm) {
    if (this.notRareWords.has(pluralKey(norm))) return 1;
    if (!this.freqRank) return null;
    var r = this.freqRank.get(norm);
    if (r === undefined) r = this.freqRank.get(pluralKey(norm));
    if (r === undefined && norm.indexOf("'") !== -1) {
      r = this.freqRank.get(norm.replace(/'/g, ""));
    }
    return r === undefined ? Infinity : r;
  };

  Checker.prototype.loadNotRareWords = function () {
    try {
      var raw = localStorage.getItem(this.notRareStorageKey);
      var self = this;
      if (raw) JSON.parse(raw).forEach(function (w) { self.notRareWords.add(String(w)); });
    } catch (e) { /* corrupted or unavailable storage — start empty */ }
  };

  Checker.prototype.saveNotRareWords = function () {
    try {
      localStorage.setItem(this.notRareStorageKey, JSON.stringify(Array.from(this.notRareWords)));
    } catch (e) { /* private mode */ }
  };

  Checker.prototype.markNotRare = function (word) {
    this.notRareWords.add(pluralKey(normWord(String(word))));
    this.saveNotRareWords();
  };

  Checker.prototype.unmarkNotRare = function (word) {
    this.notRareWords.delete(pluralKey(normWord(String(word))));
    this.saveNotRareWords();
  };

  Checker.prototype.listNotRareWords = function () {
    return Array.from(this.notRareWords).sort();
  };

  // Resolves the exclusion option (per-call opts.exclude wins over the
  // instance default) into merged, sorted [start, end) ranges.
  Checker.prototype.excludedRanges = function (text, opts) {
    var src = (opts && opts.exclude !== undefined) ? opts.exclude : this.exclude;
    if (!src) return [];
    var ranges = [];
    if (typeof src === "function") {
      (src(text) || []).forEach(function (r) { ranges.push([r[0], r[1]]); });
    } else {
      src.forEach(function (re) {
        if (!re.global) {
          var single = re.exec(text);
          if (single && single[0].length) ranges.push([single.index, single.index + single[0].length]);
          return;
        }
        re.lastIndex = 0;
        var m;
        while ((m = re.exec(text)) !== null) {
          if (m[0].length === 0) { re.lastIndex++; continue; }
          ranges.push([m.index, m.index + m[0].length]);
        }
      });
    }
    ranges.sort(function (a, b) { return a[0] - b[0]; });
    var merged = [];
    ranges.forEach(function (r) {
      var last = merged[merged.length - 1];
      if (last && r[0] <= last[1]) {
        if (r[1] > last[1]) last[1] = r[1];
      } else {
        merged.push(r.slice());
      }
    });
    return merged;
  };

  // Membership test over sorted ranges for monotonically non-decreasing
  // query positions (how tokenizers walk text) — O(n + m) overall.
  function rangeCursor(ranges) {
    var i = 0;
    return function (pos) {
      while (i < ranges.length && ranges[i][1] <= pos) i++;
      return i < ranges.length && pos >= ranges[i][0];
    };
  }

  // opts may carry { profile: "strict" } and/or { rules: { unknown: false } };
  // rules win over the profile, the profile wins over the instance default.
  Checker.prototype.resolveRules = function (opts) {
    opts = opts || {};
    var base = this.defaultRules || PROFILES.standard;
    if (opts.profile) {
      if (!PROFILES[opts.profile]) throw new Error("McPhee: unknown profile '" + opts.profile + "'");
      base = PROFILES[opts.profile];
    }
    return Object.assign({}, base, opts.rules || {});
  };

  Checker.prototype.loadIgnoredWords = function () {
    try {
      var stored = JSON.parse(localStorage.getItem(this.ignoreStorageKey) || "[]");
      this.ignoredWords = new Set(stored.map(function (w) { return String(w).toLowerCase(); }));
    } catch (e) {
      this.ignoredWords = new Set();
    }
  };

  Checker.prototype.saveIgnoredWords = function () {
    localStorage.setItem(this.ignoreStorageKey, JSON.stringify(Array.from(this.ignoredWords).sort()));
  };

  Checker.prototype.ignoreWord = function (word) {
    this.ignoredWords.add(String(word).toLowerCase());
    this.saveIgnoredWords();
  };

  Checker.prototype.unignoreWord = function (word) {
    this.ignoredWords.delete(String(word).toLowerCase());
    this.saveIgnoredWords();
  };

  Checker.prototype.unignoreAll = function () {
    this.ignoredWords.clear();
    this.saveIgnoredWords();
  };

  Checker.prototype.listIgnoredWords = function () {
    return Array.from(this.ignoredWords).sort();
  };

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

  // Union-merge a word list into the custom dictionary (migration from older
  // storage keys, or a future remote dictionary pull — union is always safe
  // because the personal dictionary only grows; removals are explicit).
  Checker.prototype.importWords = function (words) {
    var self = this;
    var added = 0;
    (words || []).forEach(function (w) {
      var lower = String(w).toLowerCase();
      if (lower && !self.customWords.has(lower)) { self.customWords.add(lower); added++; }
    });
    if (added) this.saveCustomDict();
    return added;
  };

  // "ok" | "misspelled" | "unknown". Deliberately heuristic and predictable:
  // a plain-lowercase word the dictionary doesn't know is a misspelling
  // (pink); anything shaped like a name/acronym/identifier is unknown (blue).
  // Sentence-initial capitalized typos therefore read as unknown — acceptable
  // for the "don't nag me about proper nouns" trade this makes. A lowercase
  // word whose Capitalized form IS in the dictionary (jupiter, english,
  // virginians) is also unknown, not misspelled: the omission proves it's a
  // casually-lowercased proper noun, and "correcting" it to an unrelated
  // word (english→anguish) would be vandalism. When the culture rule is on,
  // analyze() surfaces exactly these words as culture issues with the
  // capitalized fix instead of leaving them vague blue unknowns.
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

  // Hunspell suggestion cost grows explosively with word length; past any
  // realistic typo there is nothing useful to offer anyway, and a pasted
  // long token (a URL fragment, a run of characters) must never freeze the
  // panel. 24 chars comfortably covers real English words.
  var MAX_SUGGEST_LENGTH = 24;

  Checker.prototype.suggest = function (word, limit) {
    if (word.length > MAX_SUGGEST_LENGTH) return [];
    var key = word + "\u0000" + (limit || 3);
    if (!this.suggestionCache.has(key)) {
      this.suggestionCache.set(key, this.dict.suggest(word.replace(/\u2019/g, "'"), limit || 3));
    }
    return this.suggestionCache.get(key);
  };

  // Tokenizes text into issue entries { kind, value, start, end,
  // classification? } filtered by the active rules. Kinds:
  //   word           misclassified word (classification: misspelled|unknown)
  //   doublespace    an illegitimate extra-space run (sentence-separator
  //                  double spaces and line-leading indentation are fine;
  //                  the issue carries collapseTo, the run's correct form)
  //   capitalization lowercase sentence-start word that IS in the dictionary
  //   punctuation    the text ends without terminal punctuation
  //   echo           same content word or exact phrase reused within
  //                  echoWindowWords words (carries norm + distance, every
  //                  occurrence flagged; phrase issues also carry phraseWords)
  //   obscure        rare word (rank >= obscureRank or unranked) used 2+
  //                  times anywhere (carries norm + count, all flagged)
  //   culture        proper name written lowercase — curated list plus
  //                  dictionary-omission probes (jupiter/Jupiter, usa/USA)
  //                  (carries expected, the properly-cased form)
  // Words on the persistent ignore list are skipped entirely, and text
  // inside exclusion zones (options.exclude) is invisible to every rule.
  // Only issues are returned; clean text yields [].
  Checker.prototype.analyze = function (text, opts) {
    var rules = this.resolveRules(opts);
    var issues = [];
    var words = [];
    var starts = rules.sentenceCapitalization ? sentenceStartOffsets(text) : null;
    var excludedList = this.excludedRanges(text, opts);
    var excluded = rangeCursor(excludedList);
    TOKEN_RE.lastIndex = 0;
    var m;
    while ((m = TOKEN_RE.exec(text)) !== null) {
      // Tokens touching an exclusion zone never reach any rule — not even
      // the repetition word list (an excluded "gallery" is not an echo of
      // a prose "gallery").
      if (excluded(m.index) || excluded(m.index + m[0].length - 1)) continue;
      if (m[1] !== undefined) {
        var cls = this.classify(m[1]);
        words.push({ value: m[1], start: m.index, end: m.index + m[1].length, cls: cls });
        var lower = m[1].toLowerCase();
        if (this.ignoredWords.has(lower)) continue;
        // Culture check first: a proper name written lowercase gets its own
        // category instead of a generic unknown-word flag. Three detectors,
        // curated list first, then proof by dictionary omission:
        //   1. the nation/group/language list (+ per-project cultureWords);
        //   2. Capitalized-form probe — classify() already proved it: an
        //      all-lowercase word classifies "unknown" exactly when the
        //      dictionary rejects "jupiter" but knows "Jupiter", and that
        //      omission IS the evidence the word is a proper noun;
        //   3. ALLCAPS probe — "usa" is misspelled to the dictionary but
        //      "USA" is a word (length >= 3, so "ok" doesn't become "OK").
        // The probes are conservative by construction: turkey, china,
        // polish, black never fire because their lowercase forms are
        // ordinary dictionary words.
        var expected = null;
        if (rules.culture && m[1] === lower && !this.customWords.has(lower) && !this.extraWords.has(lower)) {
          expected = this.cultureWords.has(lower)
            ? lower.charAt(0).toUpperCase() + lower.slice(1)
            : cultureExpected(lower);
          if (!expected && cls === "unknown") {
            expected = lower.charAt(0).toUpperCase() + lower.slice(1);
          }
          if (!expected && cls === "misspelled" && lower.length >= 3
            && this.dict.check(lower.toUpperCase())) {
            expected = lower.toUpperCase();
          }
        }
        if (expected) {
          issues.push({ kind: "culture", value: m[1], start: m.index, end: m.index + m[1].length, classification: "culture", expected: expected });
        } else if (cls !== "ok" && rules[cls]) {
          issues.push({ kind: "word", value: m[1], start: m.index, end: m.index + m[1].length, classification: cls });
        } else if (starts && starts.has(m.index) && cls === "ok") {
          // Only dictionary words get the capitalization nag; a misspelled or
          // unknown sentence-start word is already flagged (or muted) above.
          issues.push({ kind: "capitalization", value: m[1], start: m.index, end: m.index + m[1].length, classification: "capitalization" });
        }
      } else if (rules.doublespace) {
        var run = classifySpaceRun(text, m.index, m[2].length);
        if (run) {
          issues.push({ kind: "doublespace", value: m[2], start: m.index, end: m.index + m[2].length, collapseTo: run.collapseTo });
        }
      }
    }
    if (rules.misspelled) this.attachRegionFixes(text, words, issues);
    var phraseCovered = rules.echo
      ? this.addPhraseRepetitionIssues(text, words, excludedList, issues)
      : new Set();
    if (rules.echo || rules.obscureRepeat) {
      this.addRepetitionIssues(words, rules, issues, phraseCovered);
    }
    if (rules.terminalPunctuation) {
      var trimmed = text.replace(/\s+$/, "");
      if (trimmed.length && !TERMINAL_PUNCT_RE.test(trimmed)
        && !excluded(trimmed.length - 1)) {
        issues.push({ kind: "punctuation", value: trimmed.slice(-1), start: trimmed.length - 1, end: trimmed.length, classification: "punctuation" });
      }
    }
    if (opts && typeof opts.caret === "number") {
      issues = hideTypingWord(issues, text, opts.caret);
    }
    return issues;
  };

  // Finds every exact repeated multi-word sequence without a phrase list.
  // Candidate starts within echoWindowWords are compared, extended to their
  // maximal identical sequence, and collapsed so nested subphrases do not
  // produce duplicate rows. Phrase spans never cross punctuation, exclusion
  // zones, misspellings, or other active issues. More specific phrase echoes
  // claim their component words so the panel does not also report a weaker
  // single-word echo for the same occurrences.
  Checker.prototype.addPhraseRepetitionIssues = function (text, words, excludedList, issues) {
    var covered = new Set();
    if (words.length < 4) return covered;

    var norms = new Array(words.length);
    var valid = new Array(words.length);
    for (var i = 0; i < words.length; i++) {
      var norm = phraseTokenNorm(words[i].value);
      norms[i] = norm;
      valid[i] = words[i].cls === "ok"
        && !this.customWords.has(norm) && !this.extraWords.has(norm);
    }

    // A phrase can contain whitespace but not punctuation or an excluded
    // range. Segment numbers make that boundary check constant-time during
    // candidate extension.
    var segments = new Array(words.length);
    var segment = 0;
    var rangeIndex = 0;
    segments[0] = segment;
    for (i = 1; i < words.length; i++) {
      var gapStart = words[i - 1].end;
      var gapEnd = words[i].start;
      while (rangeIndex < excludedList.length && excludedList[rangeIndex][1] <= gapStart) {
        rangeIndex++;
      }
      var crossesExcluded = rangeIndex < excludedList.length
        && excludedList[rangeIndex][0] < gapEnd
        && excludedList[rangeIndex][1] > gapStart;
      if (crossesExcluded || !/^\s+$/.test(text.slice(gapStart, gapEnd))) segment++;
      segments[i] = segment;
    }

    var candidates = new Map();
    for (i = 0; i < words.length; i++) {
      if (!valid[i]) continue;
      for (var j = i + 2; j < words.length && j - i <= this.echoWindowWords; j++) {
        if (!valid[j] || norms[i] !== norms[j]) continue;

        // A matching token immediately to the left means this pair belongs
        // to a longer phrase beginning there; only its leftmost start should
        // create a candidate.
        if (i > 0 && j > 0
          && valid[i - 1] && valid[j - 1]
          && segments[i - 1] === segments[i]
          && segments[j - 1] === segments[j]
          && norms[i - 1] === norms[j - 1]) {
          continue;
        }

        var length = 0;
        var maxLength = j - i; // repeated occurrences must not overlap
        while (length < maxLength
          && i + length < words.length && j + length < words.length
          && valid[i + length] && valid[j + length]
          && segments[i + length] === segments[i]
          && segments[j + length] === segments[j]
          && norms[i + length] === norms[j + length]) {
          length++;
        }
        if (length < 2) continue;

        var key = norms.slice(i, i + length).join(" ");
        var entry = candidates.get(key);
        if (!entry) {
          entry = { key: key, wordCount: length, indexes: new Set(), minDistance: j - i };
          candidates.set(key, entry);
        }
        entry.indexes.add(i);
        entry.indexes.add(j);
        entry.minDistance = Math.min(entry.minDistance, j - i);
      }
    }

    var occupiedByOtherIssues = issues.map(function (issue) {
      return [issue.start, issue.end];
    });
    function overlapsRanges(start, end, ranges) {
      for (var r = 0; r < ranges.length; r++) {
        if (ranges[r][0] < end && ranges[r][1] > start) return true;
      }
      return false;
    }

    var groups = [];
    candidates.forEach(function (entry) {
      var indexes = Array.from(entry.indexes).sort(function (a, b) { return a - b; });
      var usable = [];
      var lastEndIndex = -1;
      indexes.forEach(function (index) {
        var endIndex = index + entry.wordCount - 1;
        if (index < lastEndIndex) return;
        var start = words[index].start;
        var end = words[endIndex].end;
        if (overlapsRanges(start, end, occupiedByOtherIssues)) return;
        usable.push(index);
        lastEndIndex = index + entry.wordCount;
      });
      if (usable.length >= 2) {
        entry.indexes = usable;
        entry.coverage = usable.length * entry.wordCount;
        groups.push(entry);
      }
    });

    // Prefer the candidate that explains the most repeated text. This keeps
    // a longer repeated phrase instead of every nested bigram, while a
    // shorter phrase wins when it genuinely occurs in more places.
    groups.sort(function (a, b) {
      return b.coverage - a.coverage
        || b.indexes.length - a.indexes.length
        || b.wordCount - a.wordCount
        || a.minDistance - b.minDistance
        || a.indexes[0] - b.indexes[0];
    });

    var self = this;
    groups.forEach(function (entry) {
      var available = entry.indexes.filter(function (index) {
        for (var k = index; k < index + entry.wordCount; k++) {
          if (covered.has(k)) return false;
        }
        return true;
      });
      if (available.length < 2) return;

      var minDistance = Infinity;
      for (var a = 1; a < available.length; a++) {
        minDistance = Math.min(minDistance, available[a] - available[a - 1]);
      }
      available.forEach(function (index) {
        for (var k = index; k < index + entry.wordCount; k++) covered.add(k);
      });

      // Dismissing a phrase also suppresses the weaker component-word echoes
      // that the phrase had superseded.
      if (self.ignoredRepeats.has(entry.key)) return;
      available.forEach(function (index) {
        var endIndex = index + entry.wordCount - 1;
        issues.push({
          kind: "echo",
          value: text.slice(words[index].start, words[endIndex].end),
          start: words[index].start,
          end: words[endIndex].end,
          classification: "echo",
          norm: entry.key,
          distance: minDistance,
          phraseWords: entry.wordCount,
        });
      });
    });
    return covered;
  };

  // The word-level McPhee detectors. Both work on the words the dictionary
  // accepts (misspellings are someone else's problem) minus function words,
  // dictionary/jargon words, and session-dismissed words.
  //   echo:          keep the last position of each normal form; a
  //                  reappearance within echoWindowWords words flags BOTH
  //                  occurrences. Words more common than echoCommonRank are
  //                  exempt when a frequency list is loaded.
  //   obscureRepeat: count plural-folded occurrences of words rarer than
  //                  obscureRank; 2+ uses flag every occurrence not already
  //                  flagged as an echo. Needs the frequency list — without
  //                  it there is no notion of "obscure".
  Checker.prototype.addRepetitionIssues = function (words, rules, issues, phraseCovered) {
    var norms = new Array(words.length);
    for (var i = 0; i < words.length; i++) {
      var w = words[i];
      if (w.cls !== "ok" || phraseCovered.has(i)) { norms[i] = null; continue; }
      var n = normWord(w.value);
      if (n.length < 4 || STOPWORDS.has(n)
        || this.customWords.has(n) || this.extraWords.has(n)
        || this.ignoredRepeats.has(pluralKey(n))) { norms[i] = null; continue; }
      norms[i] = n;
    }
    var echoFlagged = new Set();
    if (rules.echo) {
      var lastSeen = new Map(); // plural-folded key -> last word index
      for (i = 0; i < words.length; i++) {
        var norm = norms[i];
        if (!norm) continue;
        var rank = this.rankOf(norm);
        if (rank !== null && rank < this.echoCommonRank) continue;
        var key = pluralKey(norm);
        var prev = lastSeen.get(key);
        if (prev !== undefined && i - prev <= this.echoWindowWords) {
          var distance = i - prev;
          if (!echoFlagged.has(prev)) {
            echoFlagged.add(prev);
            issues.push({ kind: "echo", value: words[prev].value, start: words[prev].start, end: words[prev].end, classification: "echo", norm: key, distance: distance });
          }
          echoFlagged.add(i);
          issues.push({ kind: "echo", value: words[i].value, start: words[i].start, end: words[i].end, classification: "echo", norm: key, distance: distance });
        }
        lastSeen.set(key, i);
      }
    }
    if (rules.obscureRepeat && this.freqRank) {
      var occurrences = new Map(); // plural-folded key -> word indexes
      for (i = 0; i < words.length; i++) {
        var n2 = norms[i];
        if (!n2 || echoFlagged.has(i)) continue;
        if (this.rankOf(n2) < this.obscureRank) continue;
        var k2 = pluralKey(n2);
        if (!occurrences.has(k2)) occurrences.set(k2, []);
        occurrences.get(k2).push(i);
      }
      occurrences.forEach(function (idxs, key) {
        if (idxs.length < 2) return;
        idxs.forEach(function (wi) {
          issues.push({ kind: "obscure", value: words[wi].value, start: words[wi].start, end: words[wi].end, classification: "obscure", norm: key, count: idxs.length });
        });
      });
    }
  };

  // Kedit's All command: every occurrence of one chosen word (case,
  // possessive, and plural folded) with the word-distances between
  // successive occurrences. No thresholds, no judgment — that's the
  // author's. Returns { word, key, count, totalWords, occurrences:
  // [{ value, start, end, wordIndex }], gaps }.
  Checker.prototype.concordance = function (text, word) {
    var target = pluralKey(normWord(String(word)));
    var occurrences = [];
    var total = 0;
    var excluded = rangeCursor(this.excludedRanges(text));
    WORD_RE.lastIndex = 0;
    var m;
    while ((m = WORD_RE.exec(text)) !== null) {
      if (excluded(m.index)) continue;
      if (pluralKey(normWord(m[0])) === target) {
        occurrences.push({ value: m[0], start: m.index, end: m.index + m[0].length, wordIndex: total });
      }
      total++;
    }
    var gaps = [];
    for (var i = 1; i < occurrences.length; i++) {
      gaps.push(occurrences[i].wordIndex - occurrences[i - 1].wordIndex);
    }
    return { word: String(word), key: target, count: occurrences.length, totalWords: total, occurrences: occurrences, gaps: gaps };
  };

  // The automatic All: every repeated word ranked by how suspicious its
  // repetition is. A word used k times in an N-word text has an expected
  // gap of N/k if evenly spread; score = expectedGap / closest actual gap,
  // so two "however" five words apart in a long text scores high while
  // "the" never surfaces (its expected gap is already tiny). Rare words
  // (rank >= obscureRank, or unranked) sort above everything — one
  // appearance per piece is the rule, distance irrelevant. Personal
  // dictionary, extraWords, and session-dismissed words are excluded, as in
  // the detectors. opts: { minLength: 3, limit: 25 }.
  Checker.prototype.repetitionReport = function (text, opts) {
    opts = opts || {};
    var minLength = opts.minLength || 3;
    var limit = opts.limit || 25;
    var byKey = new Map();
    var total = 0;
    var excluded = rangeCursor(this.excludedRanges(text, opts));
    WORD_RE.lastIndex = 0;
    var m;
    while ((m = WORD_RE.exec(text)) !== null) {
      if (excluded(m.index)) continue;
      var norm = normWord(m[0]);
      if (norm.length >= minLength && !this.customWords.has(norm) && !this.extraWords.has(norm)) {
        var key = pluralKey(norm);
        if (!this.ignoredRepeats.has(key)) {
          var entry = byKey.get(key);
          if (!entry) { entry = { value: m[0], indexes: [], firstStart: m.index }; byKey.set(key, entry); }
          entry.indexes.push(total);
        }
      }
      total++;
    }
    var rows = [];
    var self = this;
    byKey.forEach(function (entry, key) {
      var k = entry.indexes.length;
      if (k < 2) return;
      var minGap = Infinity;
      for (var i = 1; i < k; i++) {
        minGap = Math.min(minGap, entry.indexes[i] - entry.indexes[i - 1]);
      }
      var expectedGap = total / k;
      var rank = self.rankOf(key);
      var rare = rank === null ? false : rank >= self.obscureRank;
      rows.push({
        value: entry.value, key: key, count: k,
        minGap: minGap, expectedGap: Math.round(expectedGap),
        rank: rank === Infinity ? null : rank, rare: rare,
        score: expectedGap / Math.max(minGap, 1),
        firstStart: entry.firstStart,
      });
    });
    rows.sort(function (a, b) {
      if (a.rare !== b.rare) return a.rare ? -1 : 1;
      return b.score - a.score;
    });
    return { totalWords: total, rows: rows.slice(0, limit) };
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

  // True when `word` is a real token for region-rewrites: in the dictionary
  // as written, or its lowercase form is. Single letters count (classify
  // already returns "ok" for length 1), so "i" can participate.
  Checker.prototype.tokenOk = function (word) {
    if (!word) return false;
    if (this.classify(word) === "ok") return true;
    var lower = word.toLowerCase();
    return lower !== word && this.classify(lower) === "ok";
  };

  // Standalone "i" in a region rewrite is the pronoun — emit "I".
  function polishPronounI(phrase) {
    return phrase.replace(/(^|\s)i(?=\s|$)/g, function (_, p) { return p + "I"; });
  }

  // Unique distance-1 rewrite of (previous word + single space + misspelling):
  // join into one dictionary word, or every way to place a single space in
  // the concatenation that yields two dictionary words. "i fi" → "if I"
  // (space/letter transposition, then the pronoun capital). Null when there
  // is not exactly one such candidate — then the single-word guess stands.
  Checker.prototype.pickRegionFix = function (prev, miss) {
    var original = prev + " " + miss;
    var concat = prev + miss;
    var originalLower = original.toLowerCase();
    var found = [];
    var seen = new Set();
    var self = this;
    function consider(candidate) {
      var key = candidate.toLowerCase();
      if (key === originalLower || seen.has(key)) return;
      if (editDistance(originalLower, key) > 1) return;
      seen.add(key);
      found.push(candidate);
    }
    if (self.tokenOk(concat)) consider(concat);
    for (var i = 1; i < concat.length; i++) {
      var a = concat.slice(0, i), b = concat.slice(i);
      if (self.tokenOk(a) && self.tokenOk(b)) consider(a + " " + b);
    }
    if (found.length !== 1) return null;
    return polishPronounI(found[0]);
  };

  // A misspelling one single space after a previous token may be a local
  // slip ("fi" after "i"), not a lone-word typo. Attach regionFix on those
  // issues so Control-tap, localFix, and the panel rewrite the pair.
  Checker.prototype.attachRegionFixes = function (text, words, issues) {
    var byStart = new Map();
    for (var i = 0; i < issues.length; i++) {
      if (issues[i].kind === "word" && issues[i].classification === "misspelled") {
        byStart.set(issues[i].start, issues[i]);
      }
    }
    for (i = 1; i < words.length; i++) {
      var cur = words[i], prev = words[i - 1];
      if (cur.cls !== "misspelled") continue;
      if (cur.start !== prev.end + 1 || text.charAt(prev.end) !== " ") continue;
      var to = this.pickRegionFix(prev.value, cur.value);
      if (!to) continue;
      var issue = byStart.get(cur.start);
      if (issue) issue.regionFix = { start: prev.start, end: cur.end, to: to };
    }
  };

  // Naive best guess for the Control-tap fixer: prefer pickCorrection's
  // confident choice, otherwise the top Hunspell suggestion. A wrong guess
  // is meant to be undone with Ctrl+Z; the gesture is deliberately willing
  // to try.
  Checker.prototype.guessCorrection = function (word) {
    var confident = this.pickCorrection(word);
    if (confident) return confident;
    var suggestions = this.suggest(word, 1) || [];
    return suggestions.length ? suggestions[0] : null;
  };

  // Nearest misspelled word at or before `caret` (searching backward only).
  Checker.prototype.nearestMisspellingBehind = function (text, caret, opts) {
    var issues = this.analyze(text, opts);
    var best = null;
    for (var i = 0; i < issues.length; i++) {
      var issue = issues[i];
      if (issue.kind !== "word" || issue.classification !== "misspelled") continue;
      if (issue.start > caret) continue;
      if (!best || issue.start > best.start) best = issue;
    }
    return best;
  };

  // Replace the nearest misspelling behind the caret. A misspelling that
  // is a local slip with the previous word (i fi → if I) rewrites that
  // pair; otherwise guessCorrection rewrites the word alone. Undo-preserving
  // so Ctrl+Z reverts just that change. Words with no usable guess are
  // skipped and the search continues backward.
  Checker.prototype.applyNearestBackwardFix = function (textarea, opts) {
    var analyzeOpts = { rules: this.resolveRules(opts) };
    var caret = textarea.selectionStart;
    var text = textarea.value;
    var issue = this.nearestMisspellingBehind(text, caret, analyzeOpts);
    while (issue) {
      var span = issue.regionFix
        ? issue.regionFix
        : (function () {
            var guess = this.guessCorrection(issue.value);
            return guess && guess !== issue.value
              ? { start: issue.start, end: issue.end, to: guess }
              : null;
          }).call(this);
      if (span) {
        replaceRange(textarea, span.start, span.end, span.to);
        var newCaret;
        if (caret <= span.start) newCaret = caret;
        else if (caret >= span.end) newCaret = caret + (span.to.length - (span.end - span.start));
        else newCaret = span.start + span.to.length;
        textarea.setSelectionRange(newCaret, newCaret);
        return {
          applied: true,
          from: text.slice(span.start, span.end),
          to: span.to,
          offset: span.start,
        };
      }
      if (issue.start <= 0) break;
      issue = this.nearestMisspellingBehind(text, issue.start - 1, analyzeOpts);
    }
    return { applied: false };
  };

  // One-click local fix: collapse every illegitimate extra-space run
  // (sentence separators back to two spaces, everything else to one;
  // legitimate sentence double-spaces and indentation untouched), rewrite
  // local-region slips (i fi → if I) then remaining pink words with
  // pickCorrection, and — when the sentenceCapitalization rule is on —
  // capitalize lowercase dictionary words that start a sentence. Ambiguous
  // words, words with no usable suggestion, and blue (unknown) words are left
  // alone; missing terminal punctuation is never auto-fixed (. vs ? vs ! is a
  // guess). Returns the new text plus an exact change list so the caller can
  // report and undo.
  Checker.prototype.localFix = function (text, opts) {
    var rules = this.resolveRules(opts);
    var wordChanges = [];
    var self = this;
    var working = text;
    if (rules.misspelled) {
      var regionFixes = this.analyze(text, { rules: rules })
        .filter(function (i) { return i.regionFix; })
        .map(function (i) { return i.regionFix; })
        .sort(function (a, b) { return b.start - a.start; });
      regionFixes.forEach(function (r) {
        wordChanges.push({ from: text.slice(r.start, r.end), to: r.to, offset: r.start });
        working = working.slice(0, r.start) + r.to + working.slice(r.end);
      });
    }
    var starts = rules.sentenceCapitalization ? sentenceStartOffsets(working) : null;
    var excludedWords = rangeCursor(this.excludedRanges(working, opts));
    var fixedWords = working.replace(WORD_RE, function (word, offset) {
      if (excludedWords(offset) || excludedWords(offset + word.length - 1)) return word;
      var cls = self.classify(word);
      if (cls === "misspelled" && rules.misspelled) {
        var correction = self.pickCorrection(word);
        if (correction) {
          wordChanges.push({ from: word, to: correction, offset: offset });
          return correction;
        }
        return word;
      }
      if (starts && starts.has(offset) && cls === "ok") {
        var capitalized = word.charAt(0).toUpperCase() + word.slice(1);
        wordChanges.push({ from: word, to: capitalized, offset: offset });
        return capitalized;
      }
      return word;
    });
    var spaceRuns = 0;
    var excludedSpaces = rangeCursor(this.excludedRanges(fixedWords, opts));
    var fixed = rules.doublespace
      ? fixedWords.replace(/ {2,}/g, function (run, offset) {
          if (excludedSpaces(offset) || excludedSpaces(offset + run.length - 1)) return run;
          var v = classifySpaceRun(fixedWords, offset, run.length);
          if (!v) return run;
          spaceRuns++;
          return v.collapseTo;
        })
      : fixedWords;
    return { text: fixed, wordChanges: wordChanges, spaceRuns: spaceRuns };
  };

  // localFix applied straight to a textarea through the undo-preserving
  // editing pipeline (one undo step). Returns the localFix result with an
  // extra `applied` flag.
  Checker.prototype.applyFixes = function (textarea, opts) {
    var fix = this.localFix(textarea.value, opts);
    fix.applied = fix.text !== textarea.value;
    if (fix.applied) {
      var caret = caretAfterRewrite(textarea.value, fix.text, textarea.selectionStart);
      replaceRange(textarea, 0, textarea.value.length, fix.text);
      textarea.setSelectionRange(caret, caret);
    }
    return fix;
  };

  // ---------- overlay rendering ----------

  var MIRRORED_STYLES = [
    "fontFamily", "fontSize", "fontWeight", "fontStyle", "letterSpacing",
    "lineHeight", "textTransform", "wordSpacing", "textIndent",
    // Wrapping behavior is mirrored from the live textarea rather than
    // trusted to the stylesheet defaults, so site CSS that restyles
    // textareas (word-break, tab-size, RTL) can't desynchronize the wrap.
    "whiteSpace", "overflowWrap", "wordBreak", "tabSize", "direction",
    "paddingTop", "paddingRight", "paddingBottom", "paddingLeft",
    "borderTopWidth", "borderRightWidth", "borderBottomWidth", "borderLeftWidth",
    "borderRadius",
  ];

  Checker.prototype.renderHtml = function (text, opts) {
    var issues = this.analyze(text, opts);
    // Repetition analysis suppresses weaker overlapping candidates; the only
    // remaining overlap can be a tail punctuation issue. Keep the
    // earlier-starting issue and drop the overlapper.
    issues.sort(function (a, b) { return a.start - b.start || a.end - b.end; });
    var out = [];
    var last = 0;
    for (var i = 0; i < issues.length; i++) {
      var issue = issues[i];
      if (issue.start < last) continue;
      out.push(escapeHtml(text.slice(last, issue.start)));
      if (issue.kind === "doublespace") {
        // The whole illegitimate run is one joined rectangle — no internal
        // divisions.
        out.push('<mark class="mcphee-mark-doublespace" data-start="' + issue.start + '">'
          + text.slice(issue.start, issue.end) + "</mark>");
      } else {
        var cls = issue.kind === "word" ? issue.classification : issue.kind;
        out.push('<mark class="mcphee-mark-' + cls + '" data-start="' + issue.start + '">'
          + escapeHtml(text.slice(issue.start, issue.end)) + "</mark>");
      }
      last = issue.end;
    }
    out.push(escapeHtml(text.slice(last)));
    // A trailing newline needs a visible line for scroll-height parity.
    out.push("\n");
    return out.join("");
  };

  // Wraps the textarea in a positioning host and slides a mirrored backdrop
  // underneath it. The textarea keeps focus/selection/native behavior; only
  // its background becomes transparent so the marks show through.
  Checker.prototype.attach = function (textarea, opts) {
    var self = this;
    var renderOpts = { rules: this.resolveRules(opts) };
    var computed = getComputedStyle(textarea);

    var host = document.createElement("div");
    host.className = "mcphee-host";
    var backdrop = document.createElement("div");
    backdrop.className = "mcphee-backdrop";
    backdrop.setAttribute("aria-hidden", "true");

    // Styles are re-mirrored on every forced refresh, not just at attach:
    // late-loading fonts, theme switches, or zoom changes after attach would
    // otherwise leave the backdrop wrapping text differently than the
    // textarea, drifting every mark.
    function mirrorStyles() {
      MIRRORED_STYLES.forEach(function (prop) {
        backdrop.style[prop] = computed[prop];
      });
      // Always border-box, never mirrored: syncGeometry sets the OUTER box
      // (clientWidth + borders). Mirroring a content-box textarea would add
      // the mirrored padding/borders on top of that, wrapping the backdrop
      // ~18px wider than the textarea and drifting every mark leftward.
      backdrop.style.boxSizing = "border-box";
      backdrop.style.background = computed.backgroundColor;
    }
    mirrorStyles();

    textarea.parentNode.insertBefore(host, textarea);
    host.appendChild(backdrop);
    host.appendChild(textarea);
    textarea.classList.add("mcphee-textarea");
    // McPhee's marks replace the browser's red squiggles.
    textarea.spellcheck = false;

    var lastRendered = null;
    var lastCaretWord = null;
    var enabled = true;

    function caretWordKey() {
      var span = wordAtCaret(textarea.value, textarea.selectionStart);
      return span ? span.start + ":" + span.end : "";
    }

    // The backdrop must mirror the textarea's CLIENT box (plus borders), not
    // its offset box: a vertical scrollbar shrinks the client width and would
    // otherwise skew where lines wrap.
    //
    // The client box must be measured at FULL PRECISION. The textarea wraps
    // its text against its true fractional width (width:100% of anything is
    // rarely a whole pixel), but clientWidth reports that width rounded to
    // the nearest integer. A backdrop sized from the rounded value is up to
    // half a pixel wider or narrower — enough to wrap a line one word
    // differently whenever a word boundary lands inside the fraction. When
    // the flipped wrap point does not change the total line count, the
    // scrollHeight-based wrap-parity check cannot see the divergence, so
    // every mark below it displays shifted by one word. The fraction is
    // therefore recovered from the client rect (exact, since borders come
    // from computed style and scrollbars occupy whole pixels) and clientWidth
    // only contributes the integer part, resolved to whichever neighbor of
    // the rounded value carries that fraction.
    function exactClientSize(rounded, rectSize, borderA, borderB) {
      var frac = (rectSize - borderA - borderB) % 1;
      // At an exact half-pixel tie the browsers round the client size UP
      // (verified in Firefox and Chromium: true 726.5 reports 727), so the
      // true size sits at the lower neighbor.
      return rounded + frac - (frac >= 0.5 ? 1 : 0);
    }

    function syncGeometry() {
      var bl = parseFloat(computed.borderLeftWidth) || 0;
      var br = parseFloat(computed.borderRightWidth) || 0;
      var bt = parseFloat(computed.borderTopWidth) || 0;
      var bb = parseFloat(computed.borderBottomWidth) || 0;
      var rect = textarea.getBoundingClientRect();
      backdrop.style.width =
        (exactClientSize(textarea.clientWidth, rect.width, bl, br) + bl + br) + "px";
      backdrop.style.height =
        (exactClientSize(textarea.clientHeight, rect.height, bt, bb) + bt + bb) + "px";
    }

    // ----- overlay integrity: never display wrong highlights -----
    // Two invariants must hold after every render, or the marks are lies:
    //   1. content parity — the backdrop's text equals the textarea's value
    //      (plus the trailing scroll-parity newline), so every mark sits on
    //      exactly the characters the analysis measured;
    //   2. wrap parity — both boxes lay the text out identically, observable
    //      as equal content heights (same wrap points => same line count).
    // On violation: one automatic full regeneration; if the invariant still
    // fails, the overlay HIDES itself (fail closed — a missing highlight is
    // an inconvenience, a misplaced one is misinformation), warns on the
    // console, and retries on the background poll until it verifies again.
    var integrityFailed = false;

    // scrollHeight clamps to the element's own box, so comparing raw
    // scrollHeights verifies nothing while the text fits inside the visible
    // box — the common case of short text in a tall textarea would accept
    // ANY wrap divergence, even a wrong font. Measuring at height:0 forces
    // scrollHeight to report the real content height whether or not it
    // overflows. Both writes happen inside one layout pass (no paint in
    // between) and scrollTop is put back because shrinking clamps it.
    function contentHeight(el) {
      var prevHeight = el.style.height;
      var prevMinHeight = el.style.minHeight;
      var prevScrollTop = el.scrollTop;
      el.style.height = "0px";
      el.style.minHeight = "0px";
      var h = el.scrollHeight;
      el.style.height = prevHeight;
      el.style.minHeight = prevMinHeight;
      el.scrollTop = prevScrollTop;
      return h;
    }

    function verifyIntegrity() {
      // Reality, not belief: compare against the textarea's LIVE value, never
      // against a variable recording what we think we rendered.
      if (backdrop.textContent !== textarea.value + "\n") return false;
      if (Math.abs(contentHeight(backdrop) - contentHeight(textarea)) > 2) return false;
      return true;
    }

    function applyVisibility() {
      backdrop.style.visibility = enabled && !integrityFailed ? "visible" : "hidden";
    }

    // refresh() re-renders when the text changed or the in-progress word
    // (the token containing the caret) changed; refresh(true) is a full
    // regeneration — styles re-mirrored, geometry re-synced, marks rebuilt
    // from scratch — the recovery path for any drift.
    function refresh(force) {
      if (!enabled) return;
      if (force === true || integrityFailed) {
        mirrorStyles();
        lastRendered = null;
      }
      var wordKey = caretWordKey();
      if (textarea.value !== lastRendered || wordKey !== lastCaretWord) {
        // A render that does not COMPLETE is an integrity violation like any
        // other: the marks on screen no longer describe the buffer. The only
        // path to a visible overlay is a finished render plus a passed
        // verification, so an analyzer exception hides the overlay and the
        // background poll retries — it can never leave stale marks displayed
        // while the state claims they are current. (lastRendered is set
        // unconditionally so the retry is driven by integrityFailed, which
        // forces a full regeneration, rather than by a value comparison that
        // a thrown exception would have left lying.)
        var ok = false;
        var renderError = null;
        try {
          renderOpts.caret = textarea.selectionStart;
          backdrop.innerHTML = self.renderHtml(textarea.value, renderOpts);
          syncGeometry();
          if (!verifyIntegrity()) {
            // One self-repair attempt: re-mirror styles and re-render.
            mirrorStyles();
            backdrop.innerHTML = self.renderHtml(textarea.value, renderOpts);
            syncGeometry();
          }
          ok = verifyIntegrity();
        } catch (err) {
          renderError = err;
        }
        lastRendered = textarea.value;
        lastCaretWord = wordKey;
        if (!ok && !integrityFailed) {
          console.warn("McPhee: overlay integrity check failed; hiding highlights rather than showing them misaligned.", renderError || {
            contentParity: backdrop.textContent === textarea.value + "\n",
            backdropContentHeight: contentHeight(backdrop),
            textareaContentHeight: contentHeight(textarea),
          });
        }
        integrityFailed = !ok;
        applyVisibility();
      }
      backdrop.scrollTop = textarea.scrollTop;
      backdrop.scrollLeft = textarea.scrollLeft;
    }

    function onEvent() { refresh(); }

    textarea.addEventListener("input", onEvent);
    textarea.addEventListener("scroll", onEvent);
    var resizeObserver = new ResizeObserver(function () {
      syncGeometry();
      refresh();
    });
    resizeObserver.observe(textarea);
    // Programmatic .value writes fire no event; a light poll keeps the
    // overlay honest without every caller having to remember refresh().
    var pollTimer = setInterval(refresh, 700);

    // Caret moves that do not change the text (clicking into a word, arrow
    // keys) still have to hide/show the in-progress-word mark.
    function onSelectionChange() {
      if (document.activeElement !== textarea) return;
      if (caretWordKey() !== lastCaretWord) refresh();
    }
    document.addEventListener("selectionchange", onSelectionChange);

    // Control tap (no other key): naive-correct the nearest misspelling
    // behind the caret. Left and right Control both count. Other Control
    // chords (Ctrl+Z, Ctrl+C, …) clear the tap so they keep their native
    // meaning; Ctrl+Z undoes the replacement because it went through the
    // undo-preserving pipeline. IME engines (IBus on Linux) inject
    // Process/Unidentified keydowns while Control is held; those are not
    // a chord and must not cancel the tap.
    var ctrlTapClean = false;
    function isControlKey(e) {
      return e.key === "Control" || e.code === "ControlLeft" || e.code === "ControlRight";
    }
    function fieldFocused() {
      var a = document.activeElement;
      return a === textarea || !!(textarea.contains && textarea.contains(a));
    }
    function onAnyKeyDown(e) {
      if (isControlKey(e)) {
        if (!e.repeat && fieldFocused()) ctrlTapClean = true;
        return;
      }
      if (e.isComposing || e.key === "Process" || e.key === "Unidentified") return;
      ctrlTapClean = false;
    }
    function onCtrlKeyUp(e) {
      if (!isControlKey(e)) return;
      var wasClean = ctrlTapClean;
      ctrlTapClean = false;
      if (!wasClean || !enabled) return;
      if (!fieldFocused()) return;
      if (textarea.readOnly || textarea.disabled) return;
      self.applyNearestBackwardFix(textarea, { rules: renderOpts.rules });
    }
    window.addEventListener("keydown", onAnyKeyDown, true);
    window.addEventListener("keyup", onCtrlKeyUp, true);

    refresh();

    // Scrolls the textarea so the character at `offset` sits roughly a third
    // of the way down the view. The backdrop mirrors the textarea's exact
    // wrapping, so a collapsed Range over its text nodes gives the true pixel
    // position of any character offset.
    function scrollToOffset(offset) {
      if (!enabled) return;
      refresh();
      var walker = document.createTreeWalker(backdrop, NodeFilter.SHOW_TEXT);
      var remaining = offset;
      var node;
      while ((node = walker.nextNode())) {
        var len = node.nodeValue.length;
        if (remaining <= len) {
          var range = document.createRange();
          range.setStart(node, Math.max(0, Math.min(remaining, len)));
          range.collapse(true);
          var rect = range.getBoundingClientRect();
          var backdropRect = backdrop.getBoundingClientRect();
          var y = rect.top - backdropRect.top + backdrop.scrollTop;
          textarea.scrollTop = Math.max(0, y - textarea.clientHeight / 3);
          refresh();
          return;
        }
        remaining -= len;
      }
    }

    // Hover highlight: while the pointer is on a panel row, every occurrence
    // of that row's issue swaps to a solid, saturated background — a plain
    // color change, no animation and no transition, on the instant the
    // pointer enters and gone the instant it leaves. Only one row's marks
    // are highlighted at a time. `starts` lists every occurrence (repeat
    // rows highlight all their words together, so both uses of an echoed
    // word are visible at once).
    function hoverStart(starts) {
      hoverStop();
      if (!enabled) return;
      starts.forEach(function (s) {
        var m = backdrop.querySelector('mark[data-start="' + s + '"]');
        if (m) m.classList.add("mcphee-mark-hover");
      });
    }

    function hoverStop() {
      backdrop.querySelectorAll(".mcphee-mark-hover").forEach(function (m) {
        m.classList.remove("mcphee-mark-hover");
      });
    }

    // The set of issue offsets whose marks are currently visible on screen.
    // Visibility is judged against the intersection of the textarea's box
    // with the viewport, so it works both for textareas with an inner
    // scrollbar and for auto-grown textareas where the page scrolls.
    function visibleStarts() {
      var out = new Set();
      var taR = textarea.getBoundingClientRect();
      var top = Math.max(taR.top, 0);
      var bottom = Math.min(taR.bottom, window.innerHeight || document.documentElement.clientHeight);
      if (bottom <= top) return out;
      backdrop.querySelectorAll("mark").forEach(function (m) {
        var r = m.getBoundingClientRect();
        if (r.bottom >= top && r.top <= bottom) out.add(+m.dataset.start);
      });
      return out;
    }

    return {
      // refresh(true) forces a re-render (e.g. after addCustomWord, which
      // changes classification without changing the text).
      refresh: refresh,
      scrollToOffset: scrollToOffset,
      hoverStart: hoverStart,
      hoverStop: hoverStop,
      visibleStarts: visibleStarts,
      setRules: function (o) {
        renderOpts.rules = self.resolveRules(o);
        refresh(true);
      },
      setEnabled: function (on) {
        enabled = !!on;
        applyVisibility();
        textarea.spellcheck = !enabled;
        // Full regeneration on re-enable: anything (styles, geometry, text)
        // may have changed while the overlay was off.
        if (enabled) refresh(true);
      },
      detach: function () {
        clearInterval(pollTimer);
        resizeObserver.disconnect();
        textarea.removeEventListener("input", onEvent);
        textarea.removeEventListener("scroll", onEvent);
        document.removeEventListener("selectionchange", onSelectionChange);
        window.removeEventListener("keydown", onAnyKeyDown, true);
        window.removeEventListener("keyup", onCtrlKeyUp, true);
        textarea.classList.remove("mcphee-textarea");
        textarea.spellcheck = true;
        host.parentNode.insertBefore(textarea, host);
        host.remove();
      },
    };
  };

  // ---------- issues panel ----------

  // Live issue list with per-word actions: suggestion buttons (replace all
  // occurrences, undo-preserving), add-to-dictionary for misspelled/unknown
  // words, capitalize for sentence-start nags, collapse for double spaces.
  // With a controller, the panel stays linked to the text both ways: rows
  // whose occurrences are all scrolled off screen are dimmed
  // (followViewport), and the row nearest the caret is highlighted and
  // scrolled into view within the panel (followCaret). Both default on.
  // config: { textarea, container, controller?, profile?, rules?, onChange?,
  //           followViewport?, followCaret? }
  Checker.prototype.attachPanel = function (config) {
    var self = this;
    var textarea = config.textarea;
    var container = config.container;
    var analyzeOpts = { rules: this.resolveRules(config) };

    container.classList.add("mcphee-panel");

    // Formality choice (profile) and per-rule overrides persist per origin,
    // so each hostname/browser pair keeps its own writing register.
    var formalityKey = config.formalityStorageKey || "mcphee_formality";
    var overridesKey = config.ruleOverridesStorageKey || "mcphee_rule_overrides";
    var currentProfile = null;
    var ruleOverrides = {};
    try {
      var storedProfile = localStorage.getItem(formalityKey);
      if (storedProfile && PROFILES[storedProfile]) currentProfile = storedProfile;
      ruleOverrides = JSON.parse(localStorage.getItem(overridesKey) || "{}") || {};
    } catch (e) { ruleOverrides = {}; }
    if (!currentProfile) currentProfile = config.profile || "standard";
    var showConfig = false;
    var showIgnored = false;
    var confirmUnignoreAll = false;

    function activeRules() {
      return Object.assign(
        {},
        self.resolveRules({ profile: currentProfile, rules: config.rules }),
        ruleOverrides.rules || {}
      );
    }

    function persistOverrides() {
      try { localStorage.setItem(overridesKey, JSON.stringify(ruleOverrides)); } catch (e) { /* private mode */ }
    }

    function applyRuleState() {
      analyzeOpts.rules = activeRules();
      var params = ruleOverrides.params || {};
      ["echoWindowWords", "echoCommonRank", "obscureRank"].forEach(function (p) {
        if (typeof params[p] === "number" && params[p] > 0) self[p] = params[p];
      });
      if (config.controller && config.controller.setRules) {
        config.controller.setRules({ rules: analyzeOpts.rules });
      }
    }

    function setFormality(profileId) {
      currentProfile = profileId;
      try { localStorage.setItem(formalityKey, profileId); } catch (e) { /* private mode */ }
      applyRuleState();
      render();
    }

    function refreshOverlay() {
      if (config.controller) config.controller.refresh(true);
    }

    function afterAction() {
      refreshOverlay();
      render();
      if (config.onChange) config.onChange();
    }

    // Rewrites the whole value in ONE undo step, then restores the caret.
    // A whole-text replace leaves the caret at the end of the document,
    // which would make the caret-follow linkage yank the panel to its last
    // row after every accepted suggestion; re-anchoring the caret keeps the
    // author where they were working, so the next issue's row simply takes
    // the fixed row's place.
    function replaceAllOccurrences(word, replacement) {
      var re = new RegExp("\\b" + escapeRegExp(word) + "\\b", "g");
      var value = textarea.value;
      var excluded = rangeCursor(self.excludedRanges(value, analyzeOpts));
      var caret = textarea.selectionStart;
      var newCaret = caret;
      var newText = value.replace(re, function (match, offset) {
        if (excluded(offset) || excluded(offset + match.length - 1)) return match;
        if (offset + match.length <= caret) newCaret += replacement.length - match.length;
        else if (offset < caret) newCaret = offset + replacement.length;
        return replacement;
      });
      if (newText !== value) {
        replaceRange(textarea, 0, value.length, newText);
        textarea.setSelectionRange(newCaret, newCaret);
      }
      afterAction();
    }

    function button(label, className, onClick) {
      var b = document.createElement("button");
      b.type = "button";
      b.className = "mcphee-panel-btn " + className;
      b.textContent = label;
      b.addEventListener("click", onClick);
      return b;
    }

    // "Move cursor here": focuses the textarea with the issue's text SELECTED
    // so the author can immediately retype over it. The issue is re-located
    // in the current value at click time (text may have moved since render).
    function selectButton(match) {
      return button("select", "mcphee-panel-select", function () {
        var current = self.analyze(textarea.value, analyzeOpts).find(match);
        if (!current) return;
        textarea.focus();
        textarea.setSelectionRange(current.start, current.end);
        if (config.controller && config.controller.scrollToOffset) {
          config.controller.scrollToOffset(current.start);
        }
      });
    }

    // Ignore: persistent per-word mute (localStorage). Unlike +dict it
    // teaches the dictionary nothing — the exact word just stops being
    // flagged. A 3-second "undo ignore" chip guards against misclicks; the
    // header's "ignored" button reopens the full list for unignoring.
    function ignoreButton(value) {
      return button("ignore", "mcphee-panel-ignore", function () {
        self.ignoreWord(value);
        afterAction();
        showUndoChip(value);
      });
    }

    function showUndoChip(word) {
      var chip = document.createElement("div");
      chip.className = "mcphee-undo-chip";
      var text = document.createElement("span");
      text.textContent = "ignored \u201c" + word + "\u201d";
      var undo = button("undo ignore", "mcphee-panel-select", function () {
        self.unignoreWord(word);
        chip.remove();
        afterAction();
      });
      chip.appendChild(text);
      chip.appendChild(undo);
      var header = container.querySelector(".mcphee-panel-header");
      if (header && header.nextSibling) container.insertBefore(chip, header.nextSibling);
      else container.appendChild(chip);
      setTimeout(function () { chip.remove(); }, 3000);
    }

    // Rows are four grid slots — content, ignore, dict-action, select — so
    // each action column aligns vertically across every row.
    function panelRow(mainChildren, ignoreBtn, actionBtn, selBtn) {
      var row = document.createElement("div");
      row.className = "mcphee-panel-item";
      var main = document.createElement("span");
      main.className = "mcphee-panel-main";
      mainChildren.forEach(function (c) { main.appendChild(c); });
      row.appendChild(main);
      row.appendChild(ignoreBtn || document.createElement("span"));
      row.appendChild(actionBtn || document.createElement("span"));
      row.appendChild(selBtn);
      return row;
    }

    // Row behaviors shared by every issue type: hovering scrolls the text
    // to the first occurrence and solidly highlights EVERY occurrence for
    // exactly as long as the pointer stays (repeat rows highlight both
    // words); leaving clears it instantly. Clicking the row background acts
    // like "select".
    function wireRow(row) {
      var el = row.el;
      el.addEventListener("mouseenter", function () {
        if (config.controller && config.controller.scrollToOffset) {
          config.controller.scrollToOffset(row.start);
        }
        if (config.controller && config.controller.hoverStart) {
          config.controller.hoverStart(row.spans.map(function (s) { return s[0]; }));
        }
      });
      el.addEventListener("mouseleave", function () {
        if (config.controller && config.controller.hoverStop) config.controller.hoverStop();
      });
      el.addEventListener("click", function (e) {
        if (e.target.closest("button")) return;
        var sel = el.querySelector(".mcphee-panel-select");
        if (sel) sel.click();
      });
    }

    function applyRegionFixes(word) {
      var issues = self.analyze(textarea.value, analyzeOpts);
      var fixes = issues
        .filter(function (i) {
          return i.kind === "word" && i.value === word && i.regionFix;
        })
        .map(function (i) { return i.regionFix; })
        .sort(function (a, b) { return b.start - a.start; });
      if (!fixes.length) return;
      var value = textarea.value;
      var caret = textarea.selectionStart;
      var newCaret = caret;
      var newText = value;
      fixes.forEach(function (f) {
        newText = newText.slice(0, f.start) + f.to + newText.slice(f.end);
        var delta = f.to.length - (f.end - f.start);
        if (f.end <= caret) newCaret += delta;
        else if (f.start < caret) newCaret = f.start + f.to.length;
      });
      if (newText !== value) {
        replaceRange(textarea, 0, value.length, newText);
        textarea.setSelectionRange(newCaret, newCaret);
      }
      afterAction();
    }

    function wordRow(value, classification, count, regionTo) {
      var label = document.createElement("span");
      label.className = "mcphee-panel-word mcphee-panel-word-" + classification;
      label.textContent = count > 1 ? value + " \u00d7" + count : value;
      var main = [label];
      var fixBtns = [];
      if (classification === "misspelled") {
        var seen = new Set();
        if (regionTo) {
          seen.add(regionTo.toLowerCase());
          fixBtns.push(button(regionTo, "mcphee-panel-suggestion", function () {
            applyRegionFixes(value);
          }));
        }
        var preferred = self.pickCorrection(value);
        var suggestions = (preferred ? [preferred] : []).concat(self.suggest(value, 3) || []);
        suggestions.forEach(function (s) {
          var key = s.toLowerCase();
          if (seen.has(key) || seen.size >= 3) return;
          seen.add(key);
          fixBtns.push(button(s, "mcphee-panel-suggestion", function () {
            replaceAllOccurrences(value, s);
          }));
        });
      }
      // No suggestions for an all-caps word: offer its normal-cased form.
      if (!fixBtns.length && value.length > 1 && value === value.toUpperCase()) {
        var normal = value.charAt(0) + value.slice(1).toLowerCase();
        fixBtns.push(button(normal, "mcphee-panel-suggestion", function () {
          replaceAllOccurrences(value, normal);
        }));
      }
      if (fixBtns.length) {
        var fixes = document.createElement("span");
        fixes.className = "mcphee-panel-fixes";
        fixBtns.forEach(function (b) { fixes.appendChild(b); });
        main.push(fixes);
      }
      var dict = button("+ dict", "mcphee-panel-adddict", function () {
        self.addCustomWord(value);
        afterAction();
      });
      var sel = selectButton(function (i) {
        return i.kind === "word" && i.value === value;
      });
      return panelRow(main, ignoreButton(value), dict, sel);
    }

    function cultureRow(value, expected, count) {
      var label = document.createElement("span");
      label.className = "mcphee-panel-word mcphee-panel-word-culture";
      label.textContent = count > 1 ? value + " \u00d7" + count : value;
      var fix = button(expected, "mcphee-panel-suggestion", function () {
        replaceAllOccurrences(value, expected);
      });
      var fixes = document.createElement("span");
      fixes.className = "mcphee-panel-fixes";
      fixes.appendChild(fix);
      var dict = button("+ dict", "mcphee-panel-adddict", function () {
        self.addCustomWord(value);
        afterAction();
      });
      var sel = selectButton(function (i) {
        return i.kind === "culture" && i.value === value;
      });
      return panelRow([label, fixes], ignoreButton(value), dict, sel);
    }

    // Formality chooser: three always-visible buttons; the selected one is
    // a thick-bordered, obviously-different button (never an underline).
    function formalityChooser() {
      var bar = document.createElement("div");
      bar.className = "mcphee-formality";
      FORMALITY_LEVELS.forEach(function (level) {
        var b = button(level.label, "mcphee-formality-btn", function () {
          setFormality(level.id);
        });
        if (level.id === currentProfile) b.classList.add("mcphee-formality-selected");
        bar.appendChild(b);
      });
      var cfg = button("\u2699 config", "mcphee-formality-config", function () {
        showConfig = !showConfig;
        render();
      });
      if (showConfig) cfg.classList.add("mcphee-formality-selected");
      bar.appendChild(cfg);
      return bar;
    }

    // Rule config: per-rule on/off plus the repetition-detector knobs, all
    // persisted per origin as overrides on top of the chosen formality.
    var RULE_LABELS = {
      misspelled: "misspellings (pink)",
      unknown: "unknown words (blue)",
      doublespace: "extra spaces (yellow)",
      culture: "lowercase nation/group names (teal)",
      echo: "same word or phrase nearby (lavender)",
      obscureRepeat: "rare word reused (green)",
      sentenceCapitalization: "sentence capitalization (orange)",
      terminalPunctuation: "terminal punctuation",
    };
    var RULE_PARAMS = [
      { key: "echoWindowWords", label: "echo window (words)" },
      { key: "echoCommonRank", label: "echo exempt above rank" },
      { key: "obscureRank", label: "obscure below rank" },
    ];

    function configSection() {
      var box = document.createElement("div");
      box.className = "mcphee-config";
      var rules = activeRules();
      Object.keys(RULE_LABELS).forEach(function (rule) {
        var line = document.createElement("label");
        line.className = "mcphee-config-line";
        var cb = document.createElement("input");
        cb.type = "checkbox";
        cb.checked = !!rules[rule];
        cb.addEventListener("change", function () {
          ruleOverrides.rules = ruleOverrides.rules || {};
          ruleOverrides.rules[rule] = cb.checked;
          persistOverrides();
          applyRuleState();
          render();
        });
        line.appendChild(cb);
        line.appendChild(document.createTextNode(" " + RULE_LABELS[rule]));
        box.appendChild(line);
      });
      RULE_PARAMS.forEach(function (p) {
        var line = document.createElement("label");
        line.className = "mcphee-config-line";
        var input = document.createElement("input");
        input.type = "number";
        input.min = "1";
        input.value = self[p.key];
        input.addEventListener("change", function () {
          var v = parseInt(input.value, 10);
          if (!(v > 0)) return;
          ruleOverrides.params = ruleOverrides.params || {};
          ruleOverrides.params[p.key] = v;
          persistOverrides();
          applyRuleState();
          afterAction();
        });
        line.appendChild(input);
        line.appendChild(document.createTextNode(" " + p.label));
        box.appendChild(line);
      });
      var reset = button("reset to profile defaults", "mcphee-panel-select", function () {
        ruleOverrides = {};
        persistOverrides();
        self.echoWindowWords = 50;
        self.echoCommonRank = 2000;
        self.obscureRank = 10000;
        applyRuleState();
        afterAction();
      });
      box.appendChild(reset);
      return box;
    }

    // Ignored-words manager: unignore one by one, or all via a two-click
    // confirm (first click arms it, second executes).
    function ignoredSection() {
      var box = document.createElement("div");
      box.className = "mcphee-config";
      var words = self.listIgnoredWords();
      if (!words.length) {
        box.appendChild(document.createTextNode("nothing ignored"));
        return box;
      }
      words.forEach(function (w) {
        var line = document.createElement("div");
        line.className = "mcphee-config-line";
        var un = button("unignore", "mcphee-panel-select", function () {
          self.unignoreWord(w);
          afterAction();
        });
        var t = document.createElement("span");
        t.textContent = w + " ";
        line.appendChild(t);
        line.appendChild(un);
        box.appendChild(line);
      });
      var all = button(
        confirmUnignoreAll ? "confirm: unignore all " + words.length : "unignore all",
        "mcphee-panel-ignore",
        function () {
          if (!confirmUnignoreAll) {
            confirmUnignoreAll = true;
            render();
            setTimeout(function () {
              if (confirmUnignoreAll) { confirmUnignoreAll = false; render(); }
            }, 3000);
            return;
          }
          confirmUnignoreAll = false;
          self.unignoreAll();
          afterAction();
        }
      );
      box.appendChild(all);
      return box;
    }

    function render() {
      var issues = self.analyze(textarea.value, Object.assign({}, analyzeOpts, {
        caret: textarea.selectionStart,
      }));
      // Rebuilding wipes the panel's DOM, which clamps the scroll position
      // of whatever element scrolls it (the panel container, the dock side
      // column, or the drawer) to 0. Preserve it so acting on a row leaves
      // the list where the author was looking — the next row simply moves
      // up into the acted-on row's place.
      var scroller = container;
      while (scroller && scroller !== document.documentElement
        && scroller.scrollHeight <= scroller.clientHeight) {
        scroller = scroller.parentElement;
      }
      var savedScroll = scroller ? scroller.scrollTop : 0;
      container.innerHTML = "";

      var header = document.createElement("div");
      header.className = "mcphee-panel-header";
      var headerLabel = document.createElement("span");
      headerLabel.textContent = issues.length
        ? issues.length + " issue" + (issues.length === 1 ? "" : "s")
        : "no issues";
      header.appendChild(headerLabel);
      var headerBtns = document.createElement("span");
      headerBtns.className = "mcphee-panel-headerbtns";
      var ignoredCount = self.ignoredWords.size;
      if (ignoredCount || showIgnored) {
        var ignoredBtn = button("ignored (" + ignoredCount + ")", "mcphee-panel-ignore", function () {
          showIgnored = !showIgnored;
          confirmUnignoreAll = false;
          render();
        });
        if (showIgnored) ignoredBtn.classList.add("mcphee-formality-selected");
        headerBtns.appendChild(ignoredBtn);
      }
      // Manual escape hatch: full overlay regeneration (styles, geometry,
      // marks) plus a fresh panel, for when anything looks stale.
      var recheckBtn = button("\u21bb recheck", "mcphee-panel-recheck", function () {
        refreshOverlay();
        render();
      });
      recheckBtn.title = "Force full re-analysis and overlay redraw";
      headerBtns.appendChild(recheckBtn);
      header.appendChild(headerBtns);
      container.appendChild(header);

      container.appendChild(formalityChooser());
      if (showConfig) container.appendChild(configSection());
      if (showIgnored) container.appendChild(ignoredSection());

      // Group repeated words into a single row (remembering the first
      // occurrence so hover can scroll to it).
      var wordGroups = new Map();
      var cultureGroups = new Map();
      var repeatGroups = new Map(); // norm -> echo/obscure group
      var doubleSpaces = 0;
      var firstDoubleSpaceStart = null;
      var doubleSpaceSpans = [];
      issues.forEach(function (issue) {
        if (issue.kind === "word") {
          var key = issue.value + "\u0000" + issue.classification;
          var group = wordGroups.get(key);
          if (group) {
            group.count++;
            group.spans.push([issue.start, issue.end]);
            if (!group.regionTo && issue.regionFix) group.regionTo = issue.regionFix.to;
          } else {
            wordGroups.set(key, {
              count: 1, start: issue.start, spans: [[issue.start, issue.end]],
              regionTo: issue.regionFix ? issue.regionFix.to : null,
            });
          }
        } else if (issue.kind === "culture") {
          var cg = cultureGroups.get(issue.value);
          if (cg) {
            cg.count++;
            cg.spans.push([issue.start, issue.end]);
          } else {
            cultureGroups.set(issue.value, { count: 1, start: issue.start, expected: issue.expected, spans: [[issue.start, issue.end]] });
          }
        } else if (issue.kind === "echo" || issue.kind === "obscure") {
          // An echo outranks an obscure row for the same word.
          var rg = repeatGroups.get(issue.norm);
          if (!rg || (issue.kind === "echo" && rg.kind === "obscure")) {
            rg = { kind: issue.kind, value: issue.value, count: 0, start: issue.start, distance: issue.distance, spans: [] };
            repeatGroups.set(issue.norm, rg);
          }
          rg.spans.push([issue.start, issue.end]);
          if (issue.kind === rg.kind) {
            rg.count++;
            if (issue.distance !== undefined) {
              rg.distance = rg.distance === undefined ? issue.distance : Math.min(rg.distance, issue.distance);
            }
          }
        } else if (issue.kind === "doublespace") {
          doubleSpaces++;
          if (firstDoubleSpaceStart === null) firstDoubleSpaceStart = issue.start;
          doubleSpaceSpans.push([issue.start, issue.end]);
        }
      });

      // Sections by issue type — misspelled (red), unknown (blue), culture
      // (teal), echo (lavender), obscure repeat (green), capitalization,
      // punctuation, spaces — and document order (first occurrence) within
      // each section. Echo and obscure are separate sections: same-colored
      // rows read as one block, so mixing lavender and green rows was
      // disorienting.
      var SECTION_RANK = {
        misspelled: 0, unknown: 1, culture: 2, echo: 3, obscure: 4,
        capitalization: 5, punctuation: 6, doublespace: 7,
      };
      var rows = [];

      wordGroups.forEach(function (group, key) {
        var parts = key.split("\u0000");
        rows.push({
          rank: SECTION_RANK[parts[1]],
          start: group.start,
          spans: group.spans,
          el: wordRow(parts[0], parts[1], group.count, group.regionTo),
        });
      });

      cultureGroups.forEach(function (group, value) {
        rows.push({
          rank: SECTION_RANK.culture,
          start: group.start,
          spans: group.spans,
          el: cultureRow(value, group.expected, group.count),
        });
      });

      // Repetition rows: no autofix (word choice is the author's), just
      // hover-to-scroll plus a session dismiss. Obscure rows additionally
      // carry "not rare" — the permanent correction for frequency-list gaps
      // (contractions like "won't" are unranked): the word is treated as
      // common from then on, in every text.
      repeatGroups.forEach(function (group, norm) {
        var label = document.createElement("span");
        label.className = "mcphee-panel-word mcphee-panel-word-" + group.kind;
        label.textContent = group.kind === "echo"
          ? group.value + " \u00d7" + group.count + " \u00b7 " + group.distance + " word" + (group.distance === 1 ? "" : "s") + " apart"
          : group.value + " \u00d7" + group.count + " \u00b7 rare word reused";
        var notRare = group.kind === "obscure"
          ? button("not rare", "mcphee-panel-ignore", function () {
              self.markNotRare(norm);
              afterAction();
            })
          : null;
        var dismiss = button("dismiss", "mcphee-panel-adddict", function () {
          self.ignoreRepeat(norm);
          afterAction();
        });
        var sel = selectButton(function (i) {
          return (i.kind === "echo" || i.kind === "obscure") && i.norm === norm;
        });
        rows.push({
          rank: SECTION_RANK[group.kind],
          start: group.start,
          spans: group.spans,
          el: panelRow([label], notRare, dismiss, sel),
        });
      });

      issues.forEach(function (issue) {
        if (issue.kind === "capitalization") {
          var label = document.createElement("span");
          label.className = "mcphee-panel-word mcphee-panel-word-capitalization";
          label.textContent = issue.value;
          var capBtn = button("Capitalize", "mcphee-panel-suggestion", function () {
            // Re-locate the issue in the current value; text may have moved.
            var current = self.analyze(textarea.value, analyzeOpts).find(function (i) {
              return i.kind === "capitalization" && i.value === issue.value;
            });
            if (current) {
              replaceRange(textarea, current.start, current.end,
                current.value.charAt(0).toUpperCase() + current.value.slice(1));
            }
            afterAction();
          });
          var capFixes = document.createElement("span");
          capFixes.className = "mcphee-panel-fixes";
          capFixes.appendChild(capBtn);
          var capSel = selectButton(function (i) {
            return i.kind === "capitalization" && i.value === issue.value;
          });
          rows.push({
            rank: SECTION_RANK.capitalization,
            start: issue.start,
            spans: [[issue.start, issue.end]],
            el: panelRow([label, capFixes], ignoreButton(issue.value), null, capSel),
          });
        } else if (issue.kind === "punctuation") {
          var ptext = document.createElement("span");
          ptext.textContent = "missing end punctuation";
          var pSel = selectButton(function (i) {
            return i.kind === "punctuation";
          });
          var prow = panelRow([ptext], null, null, pSel);
          prow.classList.add("mcphee-panel-note");
          rows.push({
            rank: SECTION_RANK.punctuation,
            start: issue.start,
            spans: [[issue.start, issue.end]],
            el: prow,
          });
        }
      });

      if (doubleSpaces) {
        var slabel = document.createElement("span");
        slabel.className = "mcphee-panel-word mcphee-panel-word-doublespace";
        slabel.textContent = doubleSpaces + " extra-space run" + (doubleSpaces === 1 ? "" : "s");
        var collapseBtn = button("collapse", "mcphee-panel-suggestion", function () {
          var value = textarea.value;
          var excluded = rangeCursor(self.excludedRanges(value, analyzeOpts));
          var caret = textarea.selectionStart;
          var newCaret = caret;
          var newText = value.replace(/ {2,}/g, function (run, offset) {
            if (excluded(offset) || excluded(offset + run.length - 1)) return run;
            var v = classifySpaceRun(value, offset, run.length);
            if (!v) return run;
            if (offset + run.length <= caret) newCaret += v.collapseTo.length - run.length;
            else if (offset < caret) newCaret = offset + v.collapseTo.length;
            return v.collapseTo;
          });
          if (newText !== value) {
            replaceRange(textarea, 0, value.length, newText);
            textarea.setSelectionRange(newCaret, newCaret);
          }
          afterAction();
        });
        var sSel = selectButton(function (i) {
          return i.kind === "doublespace";
        });
        rows.push({
          rank: SECTION_RANK.doublespace,
          start: firstDoubleSpaceStart,
          spans: doubleSpaceSpans,
          el: panelRow([slabel, collapseBtn], null, null, sSel),
        });
      }

      rows.sort(function (a, b) { return a.rank - b.rank || a.start - b.start; });
      rows.forEach(function (r) {
        wireRow(r);
        container.appendChild(r.el);
      });
      rowMeta = rows;
      lastPanelCaretWord = panelCaretWordKey();

      var dictLine = document.createElement("div");
      dictLine.className = "mcphee-panel-dictcount";
      dictLine.textContent = "personal dictionary: " + self.customWords.size + " words";
      container.appendChild(dictLine);
      if (scroller && scroller.scrollTop !== savedScroll) scroller.scrollTop = savedScroll;
      updateViewport();
      updateCaretRow();
    }

    // ----- scroll/caret linkage -----
    // The panel and the text stay oriented to each other: rows whose every
    // occurrence is scrolled out of view are dimmed, and the row nearest the
    // caret is highlighted and kept scrolled into view in the panel.
    var rowMeta = [];

    function spansVisible(spans, vis) {
      for (var i = 0; i < spans.length; i++) {
        if (vis.has(spans[i][0])) return true;
      }
      return false;
    }

    function updateViewport() {
      if (config.followViewport === false) return;
      if (!config.controller || !config.controller.visibleStarts) return;
      var vis = config.controller.visibleStarts();
      rowMeta.forEach(function (r) {
        r.el.classList.toggle("mcphee-panel-item-offscreen", !spansVisible(r.spans, vis));
      });
    }

    function updateCaretRow() {
      if (config.followCaret === false) return;
      if (document.activeElement !== textarea) return;
      var caret = textarea.selectionStart;
      var best = null;
      var bestDist = Infinity;
      rowMeta.forEach(function (r) {
        r.spans.forEach(function (span) {
          var dist = caret < span[0] ? span[0] - caret
            : caret > span[1] ? caret - span[1] : 0;
          if (dist < bestDist) { bestDist = dist; best = r; }
        });
      });
      // Only claim a "current" row when the caret is actually near an issue;
      // a caret in clean prose highlights nothing.
      var current = bestDist <= 120 ? best : null;
      rowMeta.forEach(function (r) {
        r.el.classList.toggle("mcphee-panel-item-current", r === current);
      });
      if (current) {
        current.el.scrollIntoView({ block: "nearest" });
      }
    }

    var lastPanelCaretWord = "";
    function panelCaretWordKey() {
      var span = wordAtCaret(textarea.value, textarea.selectionStart);
      return span ? span.start + ":" + span.end : "";
    }

    var debounceTimer = null;
    function onInput() {
      clearTimeout(debounceTimer);
      debounceTimer = setTimeout(render, 400);
    }
    var viewportTimer = null;
    function onViewportChange() {
      clearTimeout(viewportTimer);
      viewportTimer = setTimeout(updateViewport, 120);
    }
    var caretTimer = null;
    function onCaretMove() {
      clearTimeout(caretTimer);
      caretTimer = setTimeout(function () {
        var key = panelCaretWordKey();
        if (key !== lastPanelCaretWord) render();
        else updateCaretRow();
      }, 150);
    }
    function onPanelSelectionChange() {
      if (document.activeElement !== textarea) return;
      onCaretMove();
    }
    textarea.addEventListener("input", onInput);
    textarea.addEventListener("scroll", onViewportChange);
    // Capture-phase window listener also catches page and ancestor-container
    // scrolling (auto-grown textareas scroll the page, not themselves).
    window.addEventListener("scroll", onViewportChange, true);
    textarea.addEventListener("keyup", onCaretMove);
    textarea.addEventListener("click", onCaretMove);
    document.addEventListener("selectionchange", onPanelSelectionChange);
    applyRuleState();
    render();

    return {
      refresh: render,
      setFormality: setFormality,
      getFormality: function () { return currentProfile; },
      detach: function () {
        clearTimeout(debounceTimer);
        clearTimeout(viewportTimer);
        clearTimeout(caretTimer);
        window.removeEventListener("scroll", onViewportChange, true);
        document.removeEventListener("selectionchange", onPanelSelectionChange);
        textarea.removeEventListener("scroll", onViewportChange);
        textarea.removeEventListener("keyup", onCaretMove);
        textarea.removeEventListener("click", onCaretMove);
        textarea.removeEventListener("input", onInput);
        container.classList.remove("mcphee-panel");
        container.innerHTML = "";
      },
    };
  };

  // ---------- dock: batteries-included layout ----------

  // One call that claims space for the whole McPhee UI around a textarea:
  // overlay + issues panel + a persisted, per-origin placement preference.
  //
  //   const d = sw.dock(textarea, opts);
  //
  // Two placements, toggleable live from the panel chrome and remembered in
  // localStorage (which is per-origin, so each hostname/browser pair keeps
  // its own choice):
  //   "inline"  the textarea keeps ~70% of its row and the panel docks
  //             beside it (sticky, so it rides along as the page scrolls)
  //   "drawer"  the textarea keeps all its space; the panel slides in from
  //             the right edge, opened by a floating handle or openDrawer()
  //
  // opts: profile/rules/onChange (forwarded), panelFraction (inline width
  // share, default 0.3), mode ("inline"|"drawer", overrides the stored
  // preference), modeStorageKey (default "mcphee_panel_mode"), handle
  // (drawer-mode floating opener, default true).
  //
  // Returns { controller, panel, getMode, setMode, openDrawer, closeDrawer,
  // toggleDrawer, detach }.
  Checker.prototype.dock = function (textarea, opts) {
    var self = this;
    opts = opts || {};
    var storageKey = opts.modeStorageKey || "mcphee_panel_mode";
    var fraction = opts.panelFraction || 0.3;
    var mode;
    try {
      mode = opts.mode || localStorage.getItem(storageKey) || "inline";
    } catch (e) { mode = opts.mode || "inline"; }
    if (mode !== "inline" && mode !== "drawer") mode = "inline";

    // Layout skeleton: wrap replaces the textarea in the document flow.
    var wrap = document.createElement("div");
    wrap.className = "mcphee-dock";
    textarea.parentNode.insertBefore(wrap, textarea);
    var main = document.createElement("div");
    main.className = "mcphee-dock-main";
    var side = document.createElement("div");
    side.className = "mcphee-dock-side";
    side.style.flex = "0 0 " + Math.round(fraction * 100) + "%";
    wrap.appendChild(main);
    wrap.appendChild(side);
    main.appendChild(textarea);

    var drawer = document.createElement("div");
    drawer.className = "mcphee-drawer";
    document.body.appendChild(drawer);
    var handle = null;
    if (opts.handle !== false) {
      handle = document.createElement("button");
      handle.type = "button";
      handle.className = "mcphee-drawer-handle";
      handle.textContent = "\u2713 spelling";
      handle.addEventListener("click", function () { api.toggleDrawer(); });
      document.body.appendChild(handle);
    }

    // The panel renders into one container that MOVES between the two homes
    // (inline cell / drawer body); no re-render needed on mode switch.
    var chrome = document.createElement("div");
    chrome.className = "mcphee-dock-bar";
    var modeBtn = document.createElement("button");
    modeBtn.type = "button";
    modeBtn.className = "mcphee-panel-btn mcphee-dock-modebtn";
    modeBtn.addEventListener("click", function () {
      api.setMode(mode === "inline" ? "drawer" : "inline");
    });
    var closeBtn = document.createElement("button");
    closeBtn.type = "button";
    closeBtn.className = "mcphee-panel-btn mcphee-dock-closebtn";
    closeBtn.textContent = "\u00d7";
    closeBtn.title = "Close";
    closeBtn.addEventListener("click", function () { api.closeDrawer(); });
    chrome.appendChild(modeBtn);
    chrome.appendChild(closeBtn);

    var panelContainer = document.createElement("div");

    function applyMode() {
      modeBtn.textContent = mode === "inline" ? "\u21e5 side drawer" : "\u21e4 dock inline";
      modeBtn.title = mode === "inline"
        ? "Move the panel into a slide-out drawer at the screen edge"
        : "Dock the panel beside the text";
      closeBtn.style.display = mode === "drawer" ? "" : "none";
      if (handle) handle.style.display = mode === "drawer" ? "" : "none";
      if (mode === "inline") {
        side.appendChild(chrome);
        side.appendChild(panelContainer);
        side.style.display = "";
        drawer.classList.remove("mcphee-drawer-open");
      } else {
        drawer.appendChild(chrome);
        drawer.appendChild(panelContainer);
        side.style.display = "none";
      }
    }

    var api = {
      getMode: function () { return mode; },
      setMode: function (m) {
        if (m !== "inline" && m !== "drawer") return;
        mode = m;
        try { localStorage.setItem(storageKey, m); } catch (e) { /* private mode */ }
        applyMode();
        if (mode === "drawer") api.openDrawer();
      },
      openDrawer: function () {
        if (mode === "drawer") drawer.classList.add("mcphee-drawer-open");
      },
      closeDrawer: function () { drawer.classList.remove("mcphee-drawer-open"); },
      toggleDrawer: function () {
        if (drawer.classList.contains("mcphee-drawer-open")) api.closeDrawer();
        else api.openDrawer();
      },
      detach: function () {
        api.panel.detach();
        api.controller.detach();
        wrap.parentNode.insertBefore(textarea, wrap);
        wrap.remove();
        drawer.remove();
        if (handle) handle.remove();
      },
    };

    applyMode();
    api.controller = this.attach(textarea, opts);
    api.panel = this.attachPanel({
      textarea: textarea,
      container: panelContainer,
      controller: api.controller,
      profile: opts.profile,
      rules: opts.rules,
      onChange: opts.onChange,
      followViewport: opts.followViewport,
      followCaret: opts.followCaret,
    });
    return api;
  };

  // ---------- form submit gating ----------

  function issueMatches(issue, blockOn) {
    return blockOn.indexOf(issue.kind) !== -1
      || (issue.classification && blockOn.indexOf(issue.classification) !== -1);
  }

  // Blocks form submission while registered fields contain blocking issues.
  // options:
  //   fields         textareas to check (default: all textareas in the form)
  //   blockOn        issue kinds/classifications that block (default
  //                  ["misspelled"] — blue unknowns don't block by default)
  //   profile/rules  rule overrides for the gating analysis
  //   watch          when true, live-disable the form's submit buttons while
  //                  blocking issues exist (the "insists" mode)
  //   onBlock        callback(blockedFields) for custom UI; default behavior
  //                  focuses the first offending field
  Checker.prototype.guardForm = function (form, options) {
    var self = this;
    options = options || {};
    var fields = options.fields || Array.prototype.slice.call(form.querySelectorAll("textarea"));
    var blockOn = options.blockOn || ["misspelled"];
    var analyzeOpts = { rules: this.resolveRules(options) };

    function blockedFields() {
      var blocked = [];
      fields.forEach(function (field) {
        var issues = self.analyze(field.value, analyzeOpts).filter(function (issue) {
          return issueMatches(issue, blockOn);
        });
        if (issues.length) blocked.push({ field: field, issues: issues });
      });
      return blocked;
    }

    // A guard is a hard block: fix the words or add them to the dictionary.
    // No resubmit override — recovery from a false positive is add-to-dict.
    function onSubmit(event) {
      var blocked = blockedFields();
      if (!blocked.length) return;
      event.preventDefault();
      if (options.onBlock) {
        options.onBlock(blocked);
      } else {
        blocked[0].field.focus();
      }
    }

    form.addEventListener("submit", onSubmit, true);

    var watchTimer = null;
    var submitButtons = [];
    if (options.watch) {
      submitButtons = Array.prototype.slice.call(
        form.querySelectorAll('button[type="submit"], button:not([type]), input[type="submit"]'));
      var updateButtons = function () {
        var count = 0;
        blockedFields().forEach(function (b) { count += b.issues.length; });
        submitButtons.forEach(function (btn) {
          btn.disabled = count > 0;
          btn.title = count > 0
            ? count + " spelling issue" + (count === 1 ? "" : "s") + " \u2014 fix or add to dictionary"
            : "";
        });
      };
      fields.forEach(function (f) { f.addEventListener("input", updateButtons); });
      watchTimer = setInterval(updateButtons, 1000);
      updateButtons();
    }

    return {
      check: blockedFields,
      detach: function () {
        form.removeEventListener("submit", onSubmit, true);
        if (watchTimer) clearInterval(watchTimer);
        submitButtons.forEach(function (btn) { btn.disabled = false; btn.title = ""; });
      },
    };
  };

  // ---------- factory ----------

  function create(options) {
    if (typeof Typo === "undefined") {
      return Promise.reject(new Error("McPhee: typo.min.js must be loaded first (mcphee/vendor/typo/typo.min.js)"));
    }
    if (!options || !options.affUrl || !options.dicUrl) {
      return Promise.reject(new Error("McPhee.create requires { affUrl, dicUrl }"));
    }
    var loads = [
      fetch(options.affUrl).then(function (r) {
        if (!r.ok) throw new Error("McPhee: failed to load " + options.affUrl + " (HTTP " + r.status + ")");
        return r.text();
      }),
      fetch(options.dicUrl).then(function (r) {
        if (!r.ok) throw new Error("McPhee: failed to load " + options.dicUrl + " (HTTP " + r.status + ")");
        return r.text();
      }),
    ];
    if (options.freqUrl) {
      // Rank list: one word per line, most common first (line number = rank).
      // Optional — without it, single-word echo falls back to the stopword
      // list alone, phrase echo still works, and obscureRepeat stays inert. A
      // load failure degrades the same way rather than killing the
      // spellchecker.
      loads.push(fetch(options.freqUrl).then(function (r) {
        return r.ok ? r.text() : null;
      }).catch(function () { return null; }));
    }
    return Promise.all(loads).then(function (parts) {
      var dict = new Typo("en_US", parts[0], parts[1]);
      var freqRank = null;
      if (parts[2]) {
        freqRank = new Map();
        var lines = parts[2].split(/\r?\n/);
        for (var i = 0; i < lines.length; i++) {
          var word = lines[i].trim();
          if (word && !freqRank.has(word)) freqRank.set(word, i + 1);
        }
      }
      return new Checker(dict, options, freqRank);
    });
  }

  return { create: create, version: VERSION, profiles: PROFILES };
})();
