#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MultiImageClient
{
    public static class GrokWebCookieLoader
    {
        private static readonly string[] RequiredCookieNames = { "sso", "sso-rw" };

        public static string LoadCookieHeader(string path)
            => string.Join("; ", LoadCookiePairs(path).Select(kvp => $"{kvp.Key}={kvp.Value}"));

        // Playwright requires discrete cookie records rather than one Cookie
        // header string. Keep parsing and auth validation identical for both
        // the HTTP/WebSocket client and the browser-backed video transport.
        public static Dictionary<string, string> LoadCookiePairs(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Cookie path is empty.", nameof(path));
            }

            var expanded = Settings.ExpandPath(path);
            if (!File.Exists(expanded))
            {
                throw new FileNotFoundException($"Grok web cookie file not found: {expanded}", expanded);
            }

            var text = File.ReadAllText(expanded).Trim().Trim('\'', '"');
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException($"Grok web cookie file is empty: {expanded}");
            }

            // Raw Cookie header pasted from Network tab: sso=...; sso-rw=...; cf_clearance=...
            if (!text.Contains('\n') && text.Contains('=') && text.Contains(';'))
            {
                var headerPairs = ParseHeaderPairs(text);
                ValidateRequiredCookies(headerPairs);
                return headerPairs;
            }

            var pairs = ParseCookieFile(text);
            if (pairs.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Could not parse any cookies from {expanded}. Paste DevTools Application cookies, Netscape cookies.txt, or a raw Cookie header line.");
            }

            ValidateRequiredCookies(pairs);
            return pairs;
        }

        private static Dictionary<string, string> ParseCookieFile(string text)
        {
            var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
            var domainByName = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (!line.Contains('\t'))
                {
                    var eq = line.IndexOf('=');
                    if (eq > 0)
                    {
                        AddCookie(pairs, domainByName, line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim(), "grok.com");
                    }
                    continue;
                }

                var parts = line.Split('\t');
                if (parts.Length < 2)
                {
                    continue;
                }

                // Chrome DevTools Application > Cookies copy: Name, Value, Domain, Path, ...
                if (LooksLikeDevToolsCookieRow(parts))
                {
                    var name = parts[0].Trim();
                    var value = parts[1].Trim();
                    var domain = parts.Length > 2 ? parts[2].Trim() : "";
                    if (!IsGrokDomain(domain))
                    {
                        continue;
                    }

                    AddCookie(pairs, domainByName, name, value, domain);
                    continue;
                }

                // Netscape cookies.txt: domain, flag, path, secure, expiration, name, value
                if (parts.Length >= 7)
                {
                    var domain = parts[0].Trim();
                    var name = parts[5].Trim();
                    var value = parts[6].Trim();
                    if (!IsGrokDomain(domain) || string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    AddCookie(pairs, domainByName, name, value, domain);
                }
            }

            return pairs;
        }

        private static Dictionary<string, string> ParseHeaderPairs(string header)
        {
            var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var segment in header.Split(';'))
            {
                var piece = segment.Trim();
                if (piece.Length == 0)
                {
                    continue;
                }

                var eq = piece.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                pairs[piece.Substring(0, eq).Trim()] = piece.Substring(eq + 1).Trim();
            }

            return pairs;
        }

        private static bool LooksLikeDevToolsCookieRow(string[] parts)
        {
            var first = parts[0].Trim();
            if (first.Length == 0 || first.StartsWith('.') || first.Contains(' '))
            {
                return false;
            }

            // Netscape rows start with a domain in column 0.
            if (parts.Length >= 7 && parts[0].TrimStart('.').Contains('.'))
            {
                return false;
            }

            return parts.Length >= 3;
        }

        private static bool IsGrokDomain(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                return false;
            }

            return domain.Equals("grok.com", StringComparison.OrdinalIgnoreCase)
                   || domain.EndsWith(".grok.com", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddCookie(
            Dictionary<string, string> pairs,
            Dictionary<string, string> domainByName,
            string name,
            string value,
            string domain)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value))
            {
                return;
            }

            if (!pairs.ContainsKey(name))
            {
                pairs[name] = value;
                domainByName[name] = domain;
                return;
            }

            var existingDomain = domainByName[name];
            var newIsGrok = domain.Equals("grok.com", StringComparison.OrdinalIgnoreCase)
                            || domain.Equals(".grok.com", StringComparison.OrdinalIgnoreCase);
            var existingIsGrok = existingDomain.Equals("grok.com", StringComparison.OrdinalIgnoreCase)
                                 || existingDomain.Equals(".grok.com", StringComparison.OrdinalIgnoreCase);

            if (newIsGrok && !existingIsGrok)
            {
                pairs[name] = value;
                domainByName[name] = domain;
            }
        }

        private static void ValidateRequiredCookies(Dictionary<string, string> pairs)
        {
            var missing = RequiredCookieNames.Where(name => !pairs.ContainsKey(name)).ToList();
            if (missing.Count == 0)
            {
                return;
            }

            throw new InvalidOperationException(
                "Grok web cookie file is missing required auth cookies: "
                + string.Join(", ", missing)
                + ". document.cookie is not enough — copy from DevTools > Application > Cookies > https://grok.com and include HttpOnly rows like sso and sso-rw.");
        }
    }
}
