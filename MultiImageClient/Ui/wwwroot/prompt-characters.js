"use strict";

// Character mentions and @Name tags in prompt text.
// - A plain mention is a whole-word, case-insensitive occurrence of a saved
//   character name; "describe" adds its description once in parentheses.
// - An @Name tag auto-expands at send time (first occurrence becomes
//   "Name (description)"; repeated @Name tags strip "@") and can attach the
//   character's saved reference photo while keeping the composer prompt short.
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

  function isAtBoundary(value, atIndex) {
    if (atIndex < 0 || value[atIndex] !== "@") return false;
    if (atIndex === 0) return true;
    const prev = value[atIndex - 1];
    return !isWordChar(prev) && prev !== "@";
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

  function orderedCharacters(characters) {
    return [...(characters || [])]
      .filter((c) => c && typeof c.name === "string" && c.name.trim())
      .sort((a, b) => b.name.trim().length - a.name.trim().length);
  }

  // Longer names win, so "Jing Li" is one mention, not "Jing" plus "Li".
  function findMentions(text, characters) {
    const value = String(text || "");
    const ordered = orderedCharacters(characters);
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
        const isAtTag = isAtBoundary(value, start - 1);
        out.push({
          start,
          end,
          text: m[0],
          characterId: character.id,
          character,
          insertAt,
          described: DescribedAfter.test(value.slice(insertAt)),
          isAtTag,
        });
      }
    }
    return out.sort((a, b) => a.start - b.start);
  }

  // Finds @Name tags (whole-word, case-insensitive, longest name first).
  // Ignores email-like forms where a word character precedes "@".
  function findAtTags(text, characters) {
    const value = String(text || "");
    const ordered = orderedCharacters(characters);
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
        const atStart = start - 1;
        if (!isAtBoundary(value, atStart) || isWordChar(value[end])) continue;
        if (taken.some(([s, e]) => atStart < e && end > s)) continue;
        taken.push([atStart, end]);
        const insertAt = possessiveEnd(value, end);
        out.push({
          atStart,
          start,
          end,
          insertAt,
          text: m[0],
          rawText: value.slice(atStart, insertAt),
          characterId: character.id,
          character,
          described: DescribedAfter.test(value.slice(insertAt)),
        });
      }
    }
    return out.sort((a, b) => a.atStart - b.atStart);
  }

  // Expands @Name tags for send-time or explicit inline expansion.
  // First tag for a character becomes "Name (description)" unless the character
  // already has a parenthetical description in the prompt; repeated @Name tags
  // strip "@" cleanly so the description is never duplicated.
  function expandAtTags(text, characters) {
    const value = String(text || "");
    const tags = findAtTags(value, characters);
    if (!tags.length) {
      return { text: value, changed: false, expanded: [] };
    }
    const alreadyDescribed = new Set();
    for (const mention of findMentions(value, characters)) {
      if (mention.described) alreadyDescribed.add(mention.characterId);
    }
    const referencedById = new Map();
    const edits = [];
    for (const tag of tags) {
      if (!referencedById.has(tag.characterId)) {
        referencedById.set(tag.characterId, tag.character);
      }
      const nameAndSuffix = value.slice(tag.start, tag.insertAt);
      const desc = String(tag.character.description || "").trim();
      let replacement = nameAndSuffix;
      if (!alreadyDescribed.has(tag.characterId) && !tag.described && desc) {
        replacement = `${nameAndSuffix} (${desc})`;
        alreadyDescribed.add(tag.characterId);
      }
      edits.push({ from: tag.atStart, to: tag.insertAt, replacement });
    }
    let out = value;
    for (let i = edits.length - 1; i >= 0; i--) {
      const edit = edits[i];
      out = out.slice(0, edit.from) + edit.replacement + out.slice(edit.to);
    }
    return {
      text: out,
      changed: out !== value,
      expanded: [...referencedById.values()],
    };
  }

  // Detects an active "@<partial>" token at the cursor for inline autocomplete.
  function autocompleteContext(text, caret, characters) {
    const value = String(text || "");
    const list = (characters || []).filter((c) => c && typeof c.name === "string" && c.name.trim());
    if (!list.length) return null;
    const pos = Math.max(0, Math.min(value.length, Number(caret) || 0));
    const floor = Math.max(0, pos - 81);
    let atIndex = -1;
    for (let i = pos - 1; i >= floor; i--) {
      const ch = value[i];
      if (ch === "\n" || ch === "\r") return null;
      if (ch === "@") {
        if (!isAtBoundary(value, i)) return null;
        atIndex = i;
        break;
      }
    }
    if (atIndex < 0) return null;
    const query = value.slice(atIndex + 1, pos);
    if (query.length > 0 && /^\s/u.test(query)) return null;
    const lower = query.toLowerCase();
    const prefix = [];
    const contains = [];
    for (const character of list) {
      const nameLower = character.name.trim().toLowerCase();
      if (!lower || nameLower.startsWith(lower)) {
        prefix.push(character);
      } else if (nameLower.includes(lower)) {
        contains.push(character);
      }
    }
    const matches = [...prefix, ...contains];
    if (!matches.length) return null;
    return { start: atIndex, end: pos, query, matches };
  }

  function applyAutocomplete(text, start, end, character) {
    const value = String(text || "");
    const s = Math.max(0, Math.min(value.length, Number(start) || 0));
    const e = Math.max(s, Math.min(value.length, Number(end) || s));
    if (!character || !String(character.name || "").trim()) {
      return { text: value, caret: e };
    }
    const before = value.slice(0, s);
    const after = value.slice(e);
    const tag = `@${character.name.trim()}`;
    const trail = (!after || (!/^\s/u.test(after) && !/^[,.;:!?)'\u2019\]]/u.test(after))) ? " " : "";
    const next = before + tag + trail + after;
    return { text: next, caret: (before + tag + trail).length };
  }

  // One entry per mentioned character, in order of first mention.
  function summarize(mentions) {
    const byId = new Map();
    for (const mention of mentions) {
      const entry = byId.get(mention.characterId);
      if (entry) {
        entry.count += 1;
        entry.described = entry.described || mention.described;
        entry.hasAtTag = entry.hasAtTag || Boolean(mention.isAtTag);
        entry.hasPlain = entry.hasPlain || !mention.isAtTag;
      } else {
        byId.set(mention.characterId, {
          characterId: mention.characterId,
          character: mention.character,
          count: 1,
          described: mention.described,
          hasAtTag: Boolean(mention.isAtTag),
          hasPlain: !mention.isAtTag,
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

  // Inserts an @Name tag at the selection so it stays compact and auto-expands at send.
  function insertAtTag(text, selectionStart, selectionEnd, characters, characterId) {
    const value = String(text || "");
    const start = Math.max(0, Math.min(value.length, Number(selectionStart) || 0));
    const end = Math.max(start, Math.min(value.length, Number(selectionEnd) || start));
    const character = (characters || []).find((c) => c.id === characterId);
    if (!character) return { text: value, changed: false, caret: end };
    const before = value.slice(0, start);
    const after = value.slice(end);
    const lead = before && !/\s$/u.test(before) ? " " : "";
    const insertion = `@${character.name.trim()}`;
    const trail = (!after || isWordChar(after[0])) ? " " : "";
    const next = before + lead + insertion + trail + after;
    return { text: next, changed: true, caret: (before + lead + insertion + trail).length };
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
    findAtTags,
    expandAtTags,
    autocompleteContext,
    applyAutocomplete,
    summarize,
    describeFirst,
    describeAll,
    insertCharacter,
    insertAtTag,
    nameWords,
  });
});
