# Integration Guide

Technical setup and API reference. Linked from [AGENTS.md](../AGENTS.md).
Product description lives in [README.md](../README.md) — never put tech
there.

## Files

- `mcphee.js` — the module (global `McPhee`, plain script, no build)
- `mcphee.css` — overlay + mark + panel styles
- `vendor/typo/typo.min.js` — [typo-js 1.2.1](https://github.com/cfinke/Typo.js) (Hunspell reader, Modified BSD)
- `vendor/typo/en_US.aff`, `vendor/typo/en_US.dic` — Hunspell en_US dictionary
- `vendor/wordfreq/en-30k.txt` — top 30,000 English words by frequency (one
  per line, most common first), from [Peter Norvig's Google Web Trillion Word
  Corpus counts](https://norvig.com/ngrams/); powers the repetition detectors
- `demo.html` — feature exercise page (open via any static server)
- `extension/` — the McPhee Guard Firefox extension (see its README)
- `test/node/` — analysis-layer suites (`npm test`)
- `test/browser/suite.py` — Playwright overlay suite (`python test/browser/suite.py`)

## Mark classes

| class | meaning | look |
| --- | --- | --- |
| `mcphee-mark-misspelled` | lowercase word not in any dictionary | light pink block |
| `mcphee-mark-unknown` | not in dictionary but shaped like a name/acronym/identifier (Capitalized, ALLCAPS, camelCase) | light blue block |
| `mcphee-mark-doublespace` | an illegitimate extra-space run | ONE joined yellow rectangle, grey outline, no internal divisions. Never flagged: exactly two spaces after sentence-ending punctuation (deliberate sentence separator) and line-leading indentation (Markdown code blocks) |
| `mcphee-mark-capitalization` | lowercase sentence-start dictionary word (strict profile) | light orange block |
| `mcphee-mark-punctuation` | text ends without terminal punctuation (strict profile) | orange-outlined box on the last character |
| `mcphee-mark-echo` | the same content word or exact multi-word phrase reused within 50 words (every occurrence) | light lavender block |
| `mcphee-mark-obscure` | a rare word (outside the top 10,000 by frequency) used 2+ times in the text | light green block |
| `mcphee-mark-culture` | a proper name written lowercase ("japanese", "usa", "jupiter") | gentle teal block |

## Setup

```html
<link rel="stylesheet" href="mcphee/mcphee.css">
<script src="mcphee/vendor/typo/typo.min.js"></script>
<script src="mcphee/mcphee.js"></script>
```

```js
const sw = await McPhee.create({
  affUrl: "mcphee/vendor/typo/en_US.aff",
  dicUrl: "mcphee/vendor/typo/en_US.dic",
  freqUrl: "mcphee/vendor/wordfreq/en-30k.txt", // optional; word-frequency rules
  extraWords: ["recraft", "grok"],          // project jargon, never flagged
  customDictStorageKey: "myapp_mcphee",     // per-user dictionary (localStorage)
  profile: "standard",                      // default rule profile
  // Exclusion zones — spans invisible to EVERY rule and every fix. Most
  // host pages have some markup that isn't prose; list it here. An array
  // of global RegExps or a function (text) => [[start, end), ...]:
  exclude: [/\{\{[\s\S]*?\}\}/g],           // e.g. double-brace template blocks
  // Repetition-detector tuning (defaults shown):
  // echoWindowWords: 50, echoCommonRank: 2000, obscureRank: 10000,
});
```

## Batteries-included layout: `dock()`

```js
// Overlay + panel + placement in one call. The panel either docks inline
// beside the textarea (~30% of the row, sticky) or slides in from the
// screen edge as a drawer; a chrome button switches modes live and the
// choice persists in localStorage per origin:
const d = sw.dock(document.querySelector("textarea"), {
  // mode: "inline" | "drawer"   (default: stored preference, else inline)
  // panelFraction: 0.3,          inline width share
  // modeStorageKey: "mcphee_panel_mode",
  // handle: true,                drawer-mode floating opener
});
d.controller;      // overlay controller (below)
d.panel;           // panel controller
d.setMode("drawer"); d.openDrawer(); d.toggleDrawer();
d.detach();
```

## Wiring the pieces yourself

```js
// Live highlighting behind a textarea (native typing/selection untouched):
const ctl = sw.attach(document.querySelector("textarea"));
ctl.refresh();            // after programmatic .value writes (also auto-polled)
ctl.refresh(true);        // force full regeneration: styles re-mirrored,
                          // geometry re-synced, marks rebuilt (the panel's
                          // "↻ recheck" button calls this)
ctl.scrollToOffset(120);  // scroll the textarea to a character offset
ctl.hoverStart([120]);    // solidly highlight marks at offsets (no animation,
                          // no transition); hoverStop() clears instantly
ctl.setRules({ profile: "casual" });  // switch rule profile live
ctl.setEnabled(false);    // toggle off (restores native browser spellcheck)
ctl.detach();
// attach() also: the word containing the caret is not marked misspelled;
// tapping Control (no other key) applies guessCorrection to the nearest
// misspelling behind the caret (Ctrl+Z undoes it).

// Live issues panel: suggestion buttons (replace-all, undo-preserving),
// add-to-dictionary, ignore (persistent per-word mute with a 3s undo chip
// and an "ignored (N)" manager), capitalize, collapse extra spaces.
// Hovering a row scrolls the textarea to the issue and solidly recolors
// EVERY occurrence (repeat rows recolor both uses at once) for exactly as
// long as the pointer stays — a plain background change, no animation or
// transition; clicking anywhere on the row selects the issue's text. The header's "↻ recheck" button
// force-regenerates the overlay and the panel. A formality chooser
// (casual/normal/formal -> the three profiles) is always visible, persisted
// per origin; its ⚙ config opens per-rule checkboxes and the repetition
// knobs, also persisted per origin. With a controller the panel stays
// linked to the text both ways: rows whose occurrences are all scrolled off
// screen dim (followViewport), and the row nearest the caret is highlighted
// and scrolled into view in the panel (followCaret) — both default on:
const panel = sw.attachPanel({
  textarea, container: sidebarDiv, controller: ctl,
  // formalityStorageKey: "mcphee_formality",
  // ruleOverridesStorageKey: "mcphee_rule_overrides",
});
panel.setFormality("strict"); panel.getFormality();

// Persistent per-word mute (what the panel's ignore buttons call):
sw.ignoreWord("Helbro"); sw.unignoreWord("Helbro");
sw.listIgnoredWords(); sw.unignoreAll();

// Persistent not-rare list (what the panel's "not rare" button on obscure
// rows calls). The vendored frequency list is the top ~30k of Peter
// Norvig's Google Web Trillion Word Corpus counts; that corpus lost
// apostrophes, so contractions ("won't") are unranked and would count as
// obscure. Marked words rank as maximally common — never obscure, exempt
// from echo. Stored at customDictStorageKey + ":notrare". Unranked
// contractions also fall back to their apostrophe-stripped rank
// automatically (won't -> wont).
sw.markNotRare("won't"); sw.unmarkNotRare("won't");
sw.listNotRareWords();

// One-click local fix, applied through the browser's editing pipeline so
// Ctrl+Z still works (one undo step):
const fix = sw.applyFixes(textarea);
console.log(fix.wordChanges, fix.spaceRuns, fix.applied);

// Control-tap fixer: nearest misspelling at or behind the caret, one
// occurrence, undo-preserving. attach() already binds this to a Control
// tap; hosts can also call it directly:
sw.applyNearestBackwardFix(textarea);

// ...or compute without touching the DOM:
const fix2 = sw.localFix(textarea.value);

// Form gating — refuse to submit text with spelling errors:
const guard = sw.guardForm(form, {
  blockOn: ["misspelled"],  // blue unknowns don't block by default
  watch: true,              // live-disable submit buttons ("insists" mode)
  // A guard is a hard block: fix the words or add them to the dictionary.
});

// Analysis without UI, e.g. for a lint pass:
sw.analyze("teh  Quick brown fox"); // [{kind:"word",value:"teh",...}, {kind:"doublespace",...}]
sw.analyze("no cap", { profile: "strict" }); // adds capitalization/punctuation issues
sw.analyze("teh cat", { caret: 3 }); // display path: teh is in-progress, omitted

// Personal dictionary:
sw.addCustomWord("anaphora");
sw.removeCustomWord("anaphora");
sw.listCustomWords();
sw.importWords(oldWordArray);  // union-merge (migration / future remote sync)

// Repetition detectors (issue kinds "echo" and "obscure"):
sw.analyze("The leopard slept. Later the leopard woke.");
// -> two {kind:"echo", norm:"leopard", distance:4} issues (both occurrences)
sw.analyze("It matters at all here, if it matters at all anywhere.");
// -> two {kind:"echo", norm:"at all", phraseWords:2, ...} issues
sw.ignoreRepeat("at all");     // session-scoped "this repetition is deliberate"

// Concordance primitives for deep-look UIs:
sw.concordance(text, "but");   // every occurrence + word-gaps between them
sw.repetitionReport(text);     // all repeated words ranked by bunching
                               // surprise; rare-word repeats pinned on top
```

## Rule catalog — exact parameters

Every rule, precisely what fires it, and what exempts it. Text inside an
exclusion zone (`options.exclude`) fires nothing at all — the checker
treats it as not being there:

- **misspelled** (pink) — a word matching `[A-Za-z]+(?:['’][A-Za-z]+)*`,
  entirely lowercase, ≥2 letters, not in the Hunspell dictionary, whose
  Capitalized form is also not in the dictionary. Exempt: personal
  dictionary, `extraWords`, ignore list. Display-only: when `opts.caret`
  is passed (overlay and panel do this), the word containing the caret is
  omitted so the author is not nagged while typing it. Form guards and
  `localFix` do not pass caret, so they still see the word.
- **unknown** (blue) — a word the dictionary doesn't know that is shaped
  like a name/acronym/identifier (contains any uppercase), OR a lowercase
  word whose Capitalized form IS in the dictionary (a casually-lowercased
  proper noun — never "corrected" to an unrelated word). Same exemptions.
- **doublespace** (yellow) — a run of 2+ spaces that is neither
  line-leading indentation nor exactly two spaces after sentence-ending
  punctuation `.` `!` `?` `…` (closing quotes/brackets allowed between).
  Sentence separators grown to 3+ spaces collapse back to two, everything
  else to one.
- **culture** (teal) — a proper name written entirely lowercase; the fix is
  the properly-cased form. Three detectors, in order:
  1. a curated nation/group/language/religion list ("japanese", "usa") plus
     per-project `options.cultureWords`;
  2. dictionary-omission probe: the dictionary rejects the lowercase form
     but knows the Capitalized one — that omission proves "jupiter",
     "friday", "virginians" are proper nouns ("jupiter" → Jupiter);
  3. ALLCAPS probe: "nasa" is a non-word but "NASA" is known ("nasa" →
     NASA; words shorter than 3 letters skipped so "ok" is left alone).
  All detectors are conservative by construction: turkey, china, polish,
  and black (the color) never fire because their lowercase forms are
  ordinary dictionary words — add such ambiguous words via `cultureWords`
  only where the proper-noun reading dominates. Exempt: personal
  dictionary, `extraWords`, ignore list.
- **sentenceCapitalization** (orange) — a lowercase dictionary word at a
  sentence start (after `.` `!` `?` `…` + whitespace, or text start).
- **terminalPunctuation** (orange outline) — the text's last
  non-whitespace character is not sentence-ending punctuation or a closer.
- **echo** (lavender) — either:
  - the same content word (case-insensitive, possessive-stripped,
    plural-folded) reappears within `echoWindowWords` words (default 50).
    Exempt: words under 4 letters, stopwords, words ranked more common than
    `echoCommonRank` (default 2000), dictionary and extra words, session
    dismissals; or
  - any exact sequence of 2+ dictionary-known words reappears within that
    window. Phrase matching is case/apostrophe-insensitive, includes
    function words, and uses no curated phrase list or frequency gate.
    Punctuation, exclusions, misspellings, and other active issues bound a
    phrase. Nested matches collapse to the candidate that explains the most
    repeated text; all full occurrences carry `phraseWords` and are marked.
    A custom/extra word exempts a phrase containing it, and
    `ignoreRepeat(phrase)` dismisses that exact normalized phrase for the
    session.
- **obscureRepeat** (green) — a word rarer than `obscureRank` (default
  10000) or absent from the frequency list, used 2+ times anywhere. Same
  exemptions; inert without `freqUrl`.

## Formality levels (rule profiles)

The panel shows these as the always-visible chooser casual / normal /
formal; the selected level persists per origin.

| profile (chooser label) | misspelled | unknown | doublespace | culture | sentenceCapitalization | terminalPunctuation | echo | obscureRepeat |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `standard` ("normal") | on | on | on | on | off | off | on | on |
| `strict` ("formal") | on | on | on | on | on | on | on | on |
| `casual` ("casual") | on | off | on | off | off | off | off | off |

`casual` is the mode for contexts where lowercase proper nouns, lowercase
i, and unpunctuated prose are intentional, so only genuine non-words and
double spaces are flagged. `strict` is the full rigamarole: complete
sentences, capitalized sentence starts, terminal punctuation.

Every entry point (`create`, `attach`, `attachPanel`, `analyze`, `localFix`,
`applyFixes`, `guardForm`) accepts `{ profile }` and/or per-rule `{ rules }`
overrides; rules win over the profile, the profile wins over the instance
default. The panel's ⚙ config writes per-origin overrides on top of the
chosen profile (localStorage `mcphee_rule_overrides`). Word lists
(extraWords, personal dictionary, ignore list) apply regardless of profile —
that layering follows cSpell's model (word lists union; settings override).

## Design notes

- The overlay is a mirrored backdrop `div` rendered *behind* the textarea
  (transparent text, colored mark backgrounds, textarea background made
  transparent). Geometry mirrors the textarea's client box so a vertical
  scrollbar can't skew wrapping.
- **Overlay correctness is enforced, not assumed**: after every render the
  controller checks that the backdrop's text equals the textarea's value
  and that both boxes wrapped identically (equal scrollHeights). A failed
  check triggers one automatic re-mirror + re-render; if it still fails the
  overlay hides itself rather than display misplaced highlights, and keeps
  retrying in the background until it verifies. Full analysis in DESIGN.md
  ("Overlay correctness").
- Classification is deliberately heuristic and predictable, not clever:
  anything capitalized/ALLCAPS/camelCase that the dictionary doesn't know is
  "unknown" (blue), on the theory that names and jargon shouldn't nag. A
  lowercase word whose Capitalized form is in the dictionary is surfaced as
  a culture issue with the cased fix, never auto-"fixed" to an unrelated
  word (english→anguish would be vandalism).
- `localFix` is precision-first because a wrong "fix" is worse than a
  highlight: a small built-in common-typos map wins outright (typo-js ranks
  classics like "teh" badly — its suggestions don't even include "the");
  otherwise the minimum edit-distance suggestion is applied only when it is
  unique, is the only adjacent-transposition candidate, or strictly wins a
  shared prefix+suffix tie-break. Blue words are never touched; anything
  ambiguous stays highlighted. Extend the map per project via
  `options.autofixMap`. Missing terminal punctuation is never auto-fixed
  (. vs ? vs ! is a guess).
- Space-run policy: exactly two spaces after `.` `!` `?` `…` (closing
  quotes/brackets allowed in between) are a deliberate sentence separator,
  legitimate and unmarked. Line-leading runs are indentation and also
  legitimate. Everything else is a violation shown as one joined rectangle;
  fixes collapse a sentence separator that grew to 3+ spaces back to two,
  and any other run to one.
- All programmatic edits (`applyFixes`, panel buttons, Control-tap
  `applyNearestBackwardFix`) go through `execCommand("insertText")` with a
  `setRangeText` fallback, so the textarea's native undo stack survives.
  Direct `.value` assignment wipes it. A Control tap with no other key
  (wired in `attach()`) replaces the nearest misspelling at or behind the
  caret: if that misspelling is a unique distance-1 slip with the previous
  word across one space (`i fi` → `if I`), the pair is rewritten; otherwise
  `guessCorrection` (`pickCorrection` when confident, else the top Hunspell
  suggestion). One occurrence; Ctrl+Z reverts it; the next tap walks further
  back. Other Control chords cancel the tap.
- The panel's first suggestion (or cased-fix) button on spelling rows
  shares a vertical line (a fixed first column in the row's main slot).
  A region rewrite, when there is one, is offered first and applies to the
  local pair rather than replace-all of the fragment.
- en_US.dic is ~700 KB and en-30k.txt ~250 KB; `McPhee.create` fetches and
  parses them once per page. Load lazily if startup matters. A failed
  frequency-list fetch degrades gracefully (echo falls back to the stopword
  list alone, obscureRepeat goes inert) instead of killing the checker.

## Distribution

Copy-the-folder, on purpose: a consumer copy can never break because this
repo changed. To update a copy, re-copy the folder and read the changelog
diff; `McPhee.version` tells you how far behind a copy is.
