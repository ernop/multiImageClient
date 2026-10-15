"use strict";

// Character mentions in prompt text. A mention is a whole-word,
// case-insensitive occurrence of a saved character name. A mention counts as
// described when a parenthesis follows it, so the composer never inserts a
// second description and never rewrites text the user wrote.
(function publishPromptCharacters(root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  } else {
    root.MultiImagePromptCharacters = api;
  }
})(typeof globalThis === "object" ? globalThis : this, function createPromptCharacters() {
  const WordChar = /[\p{L}\p{N}_]/u;
  const PossessiveSuffix = /^['\u2019]s(?![\p{L}\p{N}_])/u;
  const DescribedAfter = /^\s*\(/;

  function isWordChar(ch) {
    return typeof ch === "string" && ch.length > 0 && WordChar.test(ch);
  }

  function escapeRegex(ch) {
    return ch.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  }

  function namePattern(name) {
    let source = "";
    for (const part of String(name).trim().split(/(\s+)/u)) {
      if (!part) continue;
      if (/^\s+$/u.test(part)) {
        source += "\\s+";
        continue;
      }
      for (const ch of part) {
        source += ch === "'" || ch === "\u2019" ? "['\u2019]" : escapeRegex(ch);
      }
    }
    return new RegExp(source, "giu");
  }

  function possessiveEnd(text, end) {
    const m = PossessiveSuffix.exec(text.slice(end, end + 3));
    return m ? end + m[0].length : end;
  }

  // Longer names win, so "Jing Li" is one mention, not "Jing" plus "Li".
  function findMentions(text, characters) {
    const value = String(text || "");
    const ordered = [...(characters || [])]
      .filter((c) => c && typeof c.name === "string" && c.name.trim())
      .sort((a, b) => b.name.trim().length - a.name.trim().length);
    const taken = [];
    const out = [];
    for (const character of ordered) {
      const pattern = namePattern(character.name);
      let m;
      while ((m = pattern.exec(value)) !== null) {
        const start = m.index;
        const end = start + m[0].length;
        if (m[0].length === 0) {
          pattern.lastIndex += 1;
          continue;
        }
        if (isWordChar(value[start - 1]) || isWordChar(value[end])) continue;
        if (taken.some(([s, e]) => start < e && end > s)) continue;
        taken.push([start, end]);
        const insertAt = possessiveEnd(value, end);
        out.push({
          start,
          end,
          text: m[0],
          characterId: character.id,
          character,
          insertAt,
          described: DescribedAfter.test(value.slice(insertAt)),
        });
      }
    }
    return out.sort((a, b) => a.start - b.start);
  }

  // One entry per mentioned character, in order of first mention.
  function summarize(mentions) {
    const byId = new Map();
    for (const mention of mentions) {
      const entry = byId.get(mention.characterId);
      if (entry) {
        entry.count += 1;
        entry.described = entry.described || mention.described;
      } else {
        byId.set(mention.characterId, {
          characterId: mention.characterId,
          character: mention.character,
          count: 1,
          described: mention.described,
          first: mention,
        });
      }
    }
    return [...byId.values()];
  }

  function descriptionText(character) {
    return ` (${String(character.description || "").trim()})`;
  }

  // Adds the description after the first mention, once per prompt.
  function describeFirst(text, characters, characterId) {
    const value = String(text || "");
    const entry = summarize(findMentions(value, characters))
      .find((e) => e.characterId === characterId);
    if (!entry || entry.described || !String(entry.character.description || "").trim()) {
      return { text: value, changed: false };
    }
    const insertion = descriptionText(entry.character);
    const at = entry.first.insertAt;
    return {
      text: value.slice(0, at) + insertion + value.slice(at),
      changed: true,
      caret: at + insertion.length,
    };
  }

  function describeAll(text, characters) {
    let value = String(text || "");
    let count = 0;
    const pending = summarize(findMentions(value, characters))
      .filter((e) => !e.described && String(e.character.description || "").trim())
      .sort((a, b) => b.first.insertAt - a.first.insertAt);
    for (const entry of pending) {
      const at = entry.first.insertAt;
      value = value.slice(0, at) + descriptionText(entry.character) + value.slice(at);
      count += 1;
    }
    return { text: value, changed: count > 0, count };
  }

  // Inserts the name at the selection, plus the description when the prompt
  // does not describe that character yet. Spaces separate it from words.
  function insertCharacter(text, selectionStart, selectionEnd, characters, characterId) {
    const value = String(text || "");
    const start = Math.max(0, Math.min(value.length, Number(selectionStart) || 0));
    const end = Math.max(start, Math.min(value.length, Number(selectionEnd) || start));
    const character = (characters || []).find((c) => c.id === characterId);
    if (!character) return { text: value, changed: false, caret: end };
    const before = value.slice(0, start);
    const after = value.slice(end);
    const remaining = before + after;
    const alreadyDescribed = summarize(findMentions(remaining, characters))
      .some((e) => e.characterId === characterId && e.described);
    let insertion = character.name.trim();
    if (!alreadyDescribed && String(character.description || "").trim()) {
      insertion += descriptionText(character);
    }
    const lead = before && !/\s$/u.test(before) ? " " : "";
    const trail = after && isWordChar(after[0]) ? " " : "";
    const next = before + lead + insertion + trail + after;
    return { text: next, changed: true, caret: (before + lead + insertion).length };
  }

  // Words McPhee must accept, so names never show as misspellings.
  function nameWords(characters) {
    const words = new Set();
    for (const character of characters || []) {
      for (const word of String(character.name || "").toLowerCase().split(/[^\p{L}\p{N}'\u2019]+/u)) {
        const clean = word.replace(/\u2019/g, "'").replace(/^'+|'+$/g, "");
        if (clean) words.add(clean);
      }
    }
    return [...words];
  }

  return Object.freeze({
    findMentions,
    summarize,
    describeFirst,
    describeAll,
    insertCharacter,
    nameWords,
  });
});
