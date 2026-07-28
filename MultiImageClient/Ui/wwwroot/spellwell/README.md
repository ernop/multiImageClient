# SpellWell — drop-in textarea spellcheck for any web project

Dictionary-based (offline, free, instant) spell highlighting and one-click
local fixes for plain `<textarea>`s. No build step, no framework, no server
component: copy this folder into any project and serve it as static files.

Grown out of two earlier prototypes: the fuseki4_ai article editor (typo-js +
Hunspell en_US + localStorage custom dictionary, sidebar suggestion UI) and
stalin-mode.html (screenshot editor that only had the browser's native
squiggles). SpellWell replaces the browser's red squiggles with block
highlights:

| class | meaning | look |
| --- | --- | --- |
| `spellwell-mark-misspelled` | lowercase word not in any dictionary | light pink block |
| `spellwell-mark-unknown` | not in dictionary but shaped like a name/acronym/identifier (Capitalized, ALLCAPS, camelCase) | light blue block |
| `spellwell-mark-doublespace` | each extra space in a 2+ space run | small yellow box, grey outline (a double space reads as two squares) |

## Files

- `spellwell.js` — the module (global `SpellWell`, plain script, ES5-safe)
- `spellwell.css` — overlay + mark styles
- `vendor/typo/typo.min.js` — [typo-js 1.2.1](https://github.com/cfinke/Typo.js) (Hunspell reader, Modified BSD)
- `vendor/typo/en_US.aff`, `vendor/typo/en_US.dic` — Hunspell en_US dictionary

## Usage

```html
<link rel="stylesheet" href="spellwell/spellwell.css">
<script src="spellwell/vendor/typo/typo.min.js"></script>
<script src="spellwell/spellwell.js"></script>
```

```js
const sw = await SpellWell.create({
  affUrl: "spellwell/vendor/typo/en_US.aff",
  dicUrl: "spellwell/vendor/typo/en_US.dic",
  extraWords: ["recraft", "grok"],          // project jargon, never flagged
  customDictStorageKey: "myapp_spellwell",  // per-user dictionary (localStorage)
});

// Live highlighting behind a textarea (native typing/selection untouched):
const ctl = sw.attach(document.querySelector("textarea"));
ctl.refresh();          // after programmatic .value writes (also auto-polled)
ctl.setEnabled(false);  // toggle off (restores native browser spellcheck)
ctl.detach();

// One-click local fix — collapses 2+ space runs and replaces each pink word
// with a confidently-chosen correction (ambiguous words and blue unknown
// words are left alone rather than guessed at):
const fix = sw.localFix(textarea.value);
textarea.value = fix.text;
console.log(fix.wordChanges, fix.spaceRuns); // exact change list for undo/report

// Analysis without UI, e.g. for a lint pass:
sw.analyze("teh  Quick brown fox"); // [{kind:"word",value:"teh",...}, {kind:"doublespace",...}]

// Personal dictionary:
sw.addCustomWord("fuseki");
sw.listCustomWords();
```

## Design notes

- The overlay is a mirrored backdrop `div` rendered *behind* the textarea
  (transparent text, colored mark backgrounds, textarea background made
  transparent). Geometry mirrors the textarea's client box so a vertical
  scrollbar can't skew wrapping.
- Classification is deliberately heuristic and predictable, not clever:
  anything capitalized/ALLCAPS/camelCase that the dictionary doesn't know is
  "unknown" (blue), on the theory that names and jargon shouldn't nag. The
  cost: a sentence-initial capitalized typo reads blue, not pink.
- `localFix` is precision-first because a wrong "fix" is worse than a
  highlight: a small built-in common-typos map wins outright (typo-js ranks
  classics like "teh" badly — its suggestions don't even include "the");
  otherwise the minimum edit-distance suggestion is applied only when it is
  unique, is the only adjacent-transposition candidate, or strictly wins a
  shared prefix+suffix tie-break. Blue words are never touched; anything
  ambiguous stays highlighted. Extend the map per project via
  `options.autofixMap`.
- en_US.dic is ~700 KB; `SpellWell.create` fetches and parses it once per
  page. Load it lazily if startup matters.
